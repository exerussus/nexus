using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>Файл экземпляра: по нему клиент находит открытый редактор. Пишет только сервер.</summary>
    public sealed class NexusCliInstance
    {
        public int SchemaVersion = 1;
        public string Project;
        public string Path;
        public int Port;
        public string Token;
        public int Pid;
        public string Unity;
        public string Started;
    }

    /// <summary>
    /// Сервер CLI — компонент Core с явным владельцем (этот класс): слушает 127.0.0.1 на порту от системы,
    /// принимает по одной строке JSON на соединение, выполняет команду в главном потоке и отвечает строкой JSON.
    /// </summary>
    /// <remarks>
    /// <para>Работает, только пока включён (<see cref="NexusCliStore.Enabled"/>): выключен — ни потока, ни сокета,
    /// ни подписки на update, ни файла экземпляра.</para>
    /// <para>Перед перекомпиляцией и при выходе из редактора останавливается и удаляет файлы экземпляра;
    /// после перезагрузки домена поднимается снова с новым портом и токеном. Ждущие команды получают
    /// «редактор занят» (<see cref="NexusCommandContext.Exit.Busy"/>).</para>
    /// <para>Безопасность: только loopback, токен на каждый запрос (лежит в файле экземпляра в профиле
    /// пользователя), лимит запроса 1 МБ, таймаут чтения 10 с.</para>
    /// </remarks>
    [InitializeOnLoad]
    public static class NexusCliServer
    {
        private const int MaxRequestBytes = 1 << 20;
        private const int ReadTimeoutMs = 3000;
        private const int MaxConnections = 8;
        private const int DefaultWaitSeconds = 600;
        private const int MaxWaitSeconds = 3600;

        // протокол: camelCase, null не пишется (Newtonsoft потокобезопасен — разбор идёт в фоне)
        private static readonly JsonSerializerSettings Wire = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy(),
            },
        };

#pragma warning disable 0649 // поля заполняет Newtonsoft
        private sealed class Request
        {
            public string Token;
            public string Command;
            public string[] Args;
            public string Cwd;
            public int Timeout;
        }
#pragma warning restore 0649

        private sealed class Response
        {
            public bool Ok;
            public int ExitCode;
            public string Output;
            public string Result;
            public string Project;
        }

        private sealed class Pending
        {
            public NexusCommandContext Context;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public int ExitCode;
            public string Result;
        }

        // ---- состояние сервера: живёт, пока он запущен ----
        private static readonly object Gate = new object();
        private static readonly ConcurrentQueue<Pending> Queue = new ConcurrentQueue<Pending>();
        private static TcpListener _listener;
        private static Thread _thread;
        private static volatile bool _running;
        private static string _token;
        private static int _port;
        private static NexusCliInstance _instance;
        private static bool _updateHooked;
        private static int _connections;

        // снимок того, что в фоне нельзя спросить у Unity
        private static string _projectPath;
        private static string _projectName;
        private static string _unity;
        private static int _pid;

        static NexusCliServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorApplication.quitting += OnQuitting;

            // старт — после загрузки домена, не в статическом конструкторе (там ещё рано трогать диск и настройки)
            EditorApplication.delayCall += () =>
            {
                if (IsServiceProcess) return;
                if (NexusCliStore.Enabled) Start();
            };
        }

        public static bool Running => _running;

        /// <summary>
        /// Процесс импорта ассетов или batch mode (сборка, CI): у него тот же путь проекта — его файл экземпляра
        /// перетёр бы файл настоящего редактора, а выход удалил бы его. Такие процессы CLI не поднимают.
        /// </summary>
        private static bool IsServiceProcess => Application.isBatchMode || AssetDatabase.IsAssetImportWorkerProcess();
        public static int Port => _port;

        /// <summary>Сведения для ping и Manage.</summary>
        public static NexusCliInstance Describe()
        {
            Snapshot();
            return new NexusCliInstance { Project = _projectName, Path = _projectPath, Port = _port, Unity = _unity, Pid = _pid };
        }

        // ============================================================ запуск и остановка

        public static void Start()
        {
            lock (Gate)
            {
                if (_running || IsServiceProcess) return;
                try
                {
                    Snapshot();
                    _token = NewToken();
                    _listener = new TcpListener(IPAddress.Loopback, 0);
                    _listener.Start();
                    _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                    _running = true;

                    _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "Nexus CLI" };
                    _thread.Start();

                    if (!_updateHooked)
                    {
                        EditorApplication.update += Pump;
                        _updateHooked = true;
                    }

                    _instance = new NexusCliInstance
                    {
                        Project = _projectName, Path = _projectPath, Port = _port, Token = _token, Pid = _pid, Unity = _unity,
                        Started = DateTime.UtcNow.ToString("o"),
                    };
                    NexusCliStore.WriteInstance(_instance);
                    NexusCliStore.InstallClient();
                    NexusDiagnostics.Trace("CLI", $"сервер слушает 127.0.0.1:{_port}");
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Error("CLI: сервер не запустился", e);
                    StopLocked("не запустился");
                }
            }
        }

        public static void Stop()
        {
            lock (Gate) StopLocked("остановлен");
        }

        private static void StopLocked(string reason)
        {
            _running = false;

            try
            {
                _listener?.Stop();
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: остановка сокета", e);
            }

            _listener = null;
            _thread = null;

            if (_updateHooked)
            {
                EditorApplication.update -= Pump;
                _updateHooked = false;
            }

            // ждущим — «занят»: ответ уйдёт из их потоков
            while (Queue.TryDequeue(out var p))
            {
                p.Context.Error("Редактор " + reason + " — повторите команду.");
                p.ExitCode = NexusCommandContext.Exit.Busy;
                p.Done.Set();
            }

            if (_instance != null) NexusCliStore.DeleteInstance(_instance);
            _instance = null;
            _token = null;
            _port = 0;
        }

        private static void OnBeforeReload()
        {
            lock (Gate) StopLocked("перекомпилирует скрипты");
        }

        private static void OnQuitting()
        {
            lock (Gate) StopLocked("закрывается");
        }

        private static void Snapshot()
        {
            if (_projectPath != null) return;
            _projectPath = NexusPaths.ProjectRoot;
            _projectName = System.IO.Path.GetFileName(_projectPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            _unity = Application.unityVersion;
            using (var p = Process.GetCurrentProcess()) _pid = p.Id;
        }

        private static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(48);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ============================================================ фон: приём и ответ

        private static void AcceptLoop()
        {
            var listener = _listener;
            while (_running && listener != null)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    // сокет закрыт остановкой — выход
                    return;
                }

                // свой поток на соединение (не пул: ожидание команды держит поток до её конца), не больше лимита
                if (Interlocked.Increment(ref _connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref _connections);
                    Reject(client, "Слишком много одновременных команд — повторите.");
                    continue;
                }

                try
                {
                    new Thread(() =>
                    {
                        try
                        {
                            Serve(client);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _connections);
                        }
                    }) { IsBackground = true, Name = "Nexus CLI connection" }.Start();
                }
                catch
                {
                    Interlocked.Decrement(ref _connections);
                    client.Close();
                }
            }
        }

        private static void Reject(TcpClient client, string message)
        {
            using (client)
            {
                try
                {
                    var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(
                        new Response { ExitCode = NexusCommandContext.Exit.Busy, Output = message, Project = _projectName }, Formatting.None, Wire) + "\n");
                    client.GetStream().Write(bytes, 0, bytes.Length);
                }
                catch
                {
                    // клиент уже ушёл
                }
            }
        }

        private static void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = ReadTimeoutMs;
                    client.SendTimeout = ReadTimeoutMs;
                    var stream = client.GetStream();

                    var line = ReadLine(stream);
                    var response = Handle(line);
                    var bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(response, Formatting.None, Wire) + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("CLI: соединение", e);
                }
            }
        }

        private static Response Handle(string line)
        {
            var response = new Response { Project = _projectName };
            Request request;
            try
            {
                request = string.IsNullOrEmpty(line) ? null : JsonConvert.DeserializeObject<Request>(line, Wire);
            }
            catch
            {
                request = null;
            }

            if (request == null || string.IsNullOrEmpty(request.Command))
            {
                response.ExitCode = NexusCommandContext.Exit.Usage;
                response.Output = "Неверный запрос.";
                return response;
            }

            var token = _token;
            if (token == null || !SameToken(token, request.Token))
            {
                response.ExitCode = NexusCommandContext.Exit.Usage;
                response.Output = "Неверный токен: клиент читает устаревший файл экземпляра — повторите.";
                return response;
            }

            var pending = new Pending { Context = new NexusCommandContext(request.Command, request.Args ?? Array.Empty<string>(), request.Cwd) };

            // проверка и постановка — под тем же замком, что и остановка: иначе команда могла бы застрять в очереди
            lock (Gate)
            {
                if (!_running)
                {
                    response.ExitCode = NexusCommandContext.Exit.Busy;
                    response.Output = "Редактор занят — повторите.";
                    return response;
                }

                Queue.Enqueue(pending);
            }

            var wait = request.Timeout > 0 ? Math.Min(request.Timeout, MaxWaitSeconds) : DefaultWaitSeconds;
            if (!pending.Done.Wait(TimeSpan.FromSeconds(wait)))
            {
                pending.Context.MarkCancelled();
                response.ExitCode = NexusCommandContext.Exit.Timeout;
                response.Output = $"Нет ответа за {wait} с (редактор занят или команда долгая). Команда может завершиться позже.";
                return response;
            }

            response.ExitCode = pending.ExitCode;
            response.Ok = pending.ExitCode == NexusCommandContext.Exit.Ok;
            response.Output = pending.Context.Output;
            response.Result = pending.Result;
            return response;
        }

        private static bool SameToken(string a, string b)
        {
            if (b == null || a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Строка до \n в UTF-8, не больше лимита.</summary>
        private static string ReadLine(NetworkStream stream)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (buffer.Length < MaxRequestBytes)
            {
                var n = stream.Read(chunk, 0, chunk.Length);
                if (n <= 0) break;
                var end = Array.IndexOf(chunk, (byte)'\n', 0, n);
                if (end >= 0)
                {
                    buffer.Write(chunk, 0, end);
                    break;
                }

                buffer.Write(chunk, 0, n);
            }

            return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
        }

        // ============================================================ главный поток

        private static void Pump()
        {
            while (Queue.TryDequeue(out var p))
            {
                if (p.Context.Cancelled)
                {
                    p.Done.Set();
                    continue;
                }

                p.ExitCode = NexusCommands.Execute(p.Context);
                p.Result = Serialize(p.Context.Data, p.Context);
                p.Done.Set();
            }
        }

        private static string Serialize(object data, NexusCommandContext ctx)
        {
            if (data == null) return null;
            try
            {
                return JsonConvert.SerializeObject(data, Formatting.None);
            }
            catch (Exception e)
            {
                ctx.Warn("Данные ответа не сериализуются: " + e.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// Настройка CLI (личная: UserSettings/Exerussus.Nexus/nexus-cli.json, по умолчанию включён) и файлы
    /// экземпляров: общий каталог пользователя (для <c>nexus list</c>) и копия в UserSettings проекта.
    /// </summary>
    public static class NexusCliStore
    {
        private sealed class Config
        {
            public bool Enabled = true;
        }

        private static string ConfigPath => System.IO.Path.Combine(NexusPaths.UserRoot, "nexus-cli.json");

        /// <summary>Общий каталог экземпляров: %LOCALAPPDATA%\Exerussus\Nexus\instances.</summary>
        public static string InstancesRoot =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exerussus", "Nexus", "instances");

        public static string ProjectInstancePath => System.IO.Path.Combine(NexusPaths.UserRoot, "cli-instance.json");

        /// <summary>Клиент в пакете: Tools~/cli (путь меняется с версией пакета).</summary>
        public static string PackageClientDir => System.IO.Path.Combine(NexusPaths.EngineAbsRoot, "Tools~", "cli");

        /// <summary>
        /// Постоянная папка клиента: %LOCALAPPDATA%\Exerussus\Nexus\cli. Сервер при старте копирует туда клиент
        /// из пакета (только изменившиеся файлы) — её можно один раз добавить в PATH.
        /// </summary>
        public static string ClientDir =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Exerussus", "Nexus", "cli");

        public static string ClientPath => System.IO.Path.Combine(ClientDir, "nexus.cmd");

        /// <summary>Обновить клиент в постоянной папке. Несколько редакторов — один и тот же результат.</summary>
        internal static void InstallClient()
        {
            try
            {
                var from = PackageClientDir;
                if (!Directory.Exists(from)) return;

                // разные проекты могут стоять на разных версиях Nexus: ставится только более новый клиент,
                // равный не переписывается (cmd читает .cmd по ходу выполнения — запись во время запуска ломает его)
                var packageVersion = ClientVersion(System.IO.Path.Combine(from, "nexus.ps1"));
                var installedVersion = ClientVersion(System.IO.Path.Combine(ClientDir, "nexus.ps1"));
                if (packageVersion <= installedVersion) return;

                Directory.CreateDirectory(ClientDir);
                foreach (var file in Directory.GetFiles(from))
                {
                    var to = System.IO.Path.Combine(ClientDir, System.IO.Path.GetFileName(file));
                    var bytes = File.ReadAllBytes(file);
                    if (File.Exists(to) && Same(File.ReadAllBytes(to), bytes)) continue;
                    File.WriteAllBytes(to, bytes);
                }
            }
            catch (Exception e)
            {
                // другой редактор пишет тот же файл — не страшно, обновится при следующем старте
                NexusDiagnostics.Swallowed("CLI: установка клиента", e);
            }
        }

        /// <summary>Версия клиента из строки «# nexus-cli-version: N» в nexus.ps1; нет файла или строки — 0.</summary>
        private static int ClientVersion(string ps1)
        {
            try
            {
                if (!File.Exists(ps1)) return 0;
                foreach (var line in File.ReadLines(ps1))
                {
                    const string marker = "# nexus-cli-version:";
                    var i = line.IndexOf(marker, StringComparison.Ordinal);
                    if (i < 0) continue;
                    return int.TryParse(line.Substring(i + marker.Length).Trim(), out var v) ? v : 0;
                }
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: версия клиента", e);
            }

            return 0;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        public static bool Enabled
        {
            get => JsonIo.Load<Config>(ConfigPath)?.Enabled ?? true;
            set
            {
                JsonIo.Save(ConfigPath, new Config { Enabled = value });
                if (value) NexusCliServer.Start();
                else NexusCliServer.Stop();
            }
        }

        internal static void WriteInstance(NexusCliInstance instance)
        {
            WriteFile(System.IO.Path.Combine(InstancesRoot, InstanceFileName(instance.Path)), instance);
            WriteFile(ProjectInstancePath, instance);
        }

        internal static void DeleteInstance(NexusCliInstance instance)
        {
            DeleteFile(System.IO.Path.Combine(InstancesRoot, InstanceFileName(instance.Path)));
            DeleteFile(ProjectInstancePath);
        }

        /// <summary>Имя файла по пути проекта: один файл на проект, перезапуск редактора перезаписывает свой.</summary>
        private static string InstanceFileName(string projectPath)
        {
            var normalized = (projectPath ?? string.Empty).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb + ".json";
            }
        }

        private static void WriteFile(string path, NexusCliInstance instance)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(instance, JsonIo.Settings), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Error($"CLI: файл экземпляра {path}", e);
            }
        }

        private static void DeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed($"CLI: удаление {path}", e);
            }
        }
    }
}
