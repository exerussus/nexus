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

        /// <summary>Editor Bridge под Nexus Daemon (протокол демона): демон обслуживает только такие редакторы.</summary>
        public int DaemonProtocol = 1;

        /// <summary>Editor.log этого редактора — для диагноза сторожа демона.</summary>
        public string Log;

        /// <summary>
        /// <c>ready</c> — принимает команды; <c>reloading</c> — редактор перезагружает домен (порта и токена нет):
        /// клиент ждёт и перечитывает файл, а не считает редактор закрытым; <c>closed</c> — редактор закрыт, CLI
        /// в проекте включён (демон знает проект).
        /// </summary>
        public string State = "ready";
    }

    /// <summary>
    /// Сервер CLI — компонент Core с явным владельцем (этот класс): слушает 127.0.0.1 на порту от системы,
    /// принимает по одной строке JSON на соединение, выполняет команду в главном потоке и отвечает строкой JSON.
    /// </summary>
    /// <remarks>
    /// <para>Работает, только пока включён (<see cref="NexusCliStore.Enabled"/>): выключен — ни потока, ни сокета,
    /// ни подписки на update, ни файла экземпляра.</para>
    /// <para>Перед перезагрузкой домена останавливается и помечает файл экземпляра <c>reloading</c> (клиент ждёт);
    /// после неё поднимается снова с новым портом и токеном. При выходе и выключении файлы экземпляра удаляются.
    /// Ждущие команды получают «редактор занят» (<see cref="NexusCommandContext.Exit.Busy"/>).</para>
    /// <para>Вместе с сервером живут его компоненты: буфер лога (<see cref="NexusLogBuffer"/>), итог компиляции
    /// (<see cref="NexusCompileLog"/>) и задания (<see cref="NexusCliJobs"/>) — выключен сервер, выключены и они.</para>
    /// <para>Безопасность: только loopback, токен на каждый запрос (лежит в файле экземпляра в профиле
    /// пользователя), лимит запроса 1 МБ, таймаут чтения 3 с.</para>
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
        internal static readonly JsonSerializerSettings Wire = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy(),
            },
        };

#pragma warning disable 0649 // поля заполняет Newtonsoft
        /// <summary>Запрос — общий для TCP и файлового канала.</summary>
        internal sealed class Request
        {
            public string Id;
            public string Token;
            public string Command;
            public string[] Args;
            public string Cwd;

            /// <summary>Срок, с: TCP — сколько ждать ответа; для команды с заданием — предел срока задания.</summary>
            public int Timeout;

            /// <summary>Файловый канал: сколько секунд хранить итог (0 — до чтения клиентом).</summary>
            public double Keep;

            /// <summary>Пройти блокировку идущей задачи.</summary>
            public bool Force;

            /// <summary>Кто прислал: <c>nexusd</c> — демон. Прямой TCP мимо демона — только с настройкой directTcp.</summary>
            public string Client;
        }
#pragma warning restore 0649

        /// <summary>Ответ — общий для TCP и файлового канала.</summary>
        internal sealed class Response
        {
            public string Id;
            public string State;
            public bool Ok;
            public int ExitCode;
            public string Output;
            public string Result;
            public string Project;

            /// <summary>Команда продолжается заданием: клиент ждёт его через <c>job.status</c>.</summary>
            public string Job;
            public bool Pending;
            public double? Seconds;

            /// <summary>Файловый канал: когда итог можно удалить (UTC, ISO 8601).</summary>
            public string Expires;
        }

        private sealed class Pending
        {
            public Request Request;
            public NexusCommandContext Context;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public Response Response;
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
        private static string _log;

        static NexusCliServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorApplication.quitting += OnQuitting;

            // сервер был запущен до перезагрузки — лог и итог компиляции подхватываются сразу, ещё до delayCall:
            // иначе ошибки Awake/Start первого кадра Play и [InitializeOnLoad] пропадут
            if (!IsServiceProcess && SessionState.GetBool(ActiveKey, false))
            {
                SafeDeactivate(NexusLogBuffer.Activate);
                SafeDeactivate(NexusCompileLog.Activate);
                // метка «домен загружен»: застрянет до «ready» — видно, что сервер не дошёл до старта
                NexusCliDomainMarks.MarkDomain("loaded");
            }

            // старт — после загрузки домена, не в статическом конструкторе (там ещё рано трогать диск и настройки)
            EditorApplication.delayCall += () =>
            {
                if (IsServiceProcess) return;
                var enabled = NexusCliStore.Enabled;
                if (enabled) Start();
                // выключен — стереть всё; включён, но не поднялся — оставить метки и ошибку старта для диагноза
                if (!_running) Cleanup(!enabled);
            };
        }

        private const string ActiveKey = "Exerussus.Nexus.Cli.Active";

        /// <summary>Сервер работал в этой сессии редактора (переживает перезагрузку домена).</summary>
        public static bool ActiveInSession => SessionState.GetBool(ActiveKey, false);

        /// <summary>
        /// Сервер не поднялся (выключен или ошибка): убрать «reloading»-файл экземпляра, иначе клиент ждал бы
        /// закрытый для команд редактор, и стереть следы компонентов («off means off»).
        /// </summary>
        private static void Cleanup(bool wipeChannel)
        {
            Snapshot();
            SessionState.EraseBool(ActiveKey);
            NexusCliStore.DeleteInstance(new NexusCliInstance { Path = _projectPath });
            SafeDeactivate(() => NexusLogBuffer.Deactivate(false));
            SafeDeactivate(() => NexusCompileLog.Deactivate(false));
            SafeDeactivate(() => NexusCliJobs.Deactivate(false));
            if (wipeChannel) SafeDeactivate(DeleteChannel);
        }

        /// <summary>CLI выключен — папка канала демона в проекте удаляется («выключено — значит выключено»).</summary>
        private static void DeleteChannel()
        {
            var dir = NexusTempPaths.Cli;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        public static bool Running => _running;

        /// <summary>Текущий токен (null — сервер не запущен). Для файлового канала.</summary>
        internal static string Token => _token;

        internal static string ProjectName => _projectName;

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
                        Project = _projectName, Path = _projectPath, Port = _port, Token = _token, Pid = _pid, Unity = _unity, Log = _log,
                        Started = DateTime.UtcNow.ToString("o"),
                    };
                    NexusCliStore.WriteInstance(_instance);
                    NexusCliStore.InstallClient();

                    SessionState.SetBool(ActiveKey, true);
                    NexusLogBuffer.Activate();
                    NexusCompileLog.Activate();
                    NexusCliJobs.Activate();
                    NexusCliDomainMarks.ClearStartupError();
                    NexusCliDomainMarks.MarkDomain("ready");
                    NexusCliDaemon.Ensure();
                    NexusDiagnostics.Trace("CLI", $"сервер слушает 127.0.0.1:{_port}");
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Error("CLI: сервер не запустился", e);
                    NexusCliDomainMarks.MarkStartupError(e);
                    StopLocked("не запустился", false);
                }
            }
        }

        public static void Stop()
        {
            lock (Gate) StopLocked("остановлен", false, wipeChannel: true);
        }

        private static void StopLocked(string reason, bool reloading, bool closing = false, bool wipeChannel = false)
        {
            var wasRunning = _running;
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
                p.Response = new Response { ExitCode = NexusCommandContext.Exit.Busy, Output = p.Context.Output, Project = _projectName };
                p.Done.Set();
            }

            if (_instance != null)
            {
                if (reloading)
                {
                    // файл остаётся: редактор жив и вернётся с новым портом — клиент подождёт
                    _instance.State = "reloading";
                    _instance.Port = 0;
                    _instance.Token = null;
                    NexusCliStore.WriteInstance(_instance);
                }
                else if (closing)
                {
                    // редактор закрывается, CLI включён: файл остаётся «closed» — демон знает проект и не выходит
                    _instance.State = "closed";
                    _instance.Port = 0;
                    _instance.Token = null;
                    NexusCliStore.WriteInstance(_instance);
                }
                else
                {
                    NexusCliStore.DeleteInstance(_instance);
                }
            }

            _instance = null;

            if (!reloading) SessionState.EraseBool(ActiveKey);

            if (wipeChannel) SafeDeactivate(DeleteChannel);

            if (wasRunning)
            {
                SafeDeactivate(() => NexusCliJobs.Deactivate(reloading));
                SafeDeactivate(() => NexusCompileLog.Deactivate(reloading));
                SafeDeactivate(() => NexusLogBuffer.Deactivate(reloading));
            }
            _token = null;
            _port = 0;
        }

        private static void OnBeforeReload()
        {
            if (_running) NexusCliDomainMarks.MarkDomain("reloading");
            lock (Gate) StopLocked("перезагружает домен", true);
        }

        private static void OnQuitting()
        {
            lock (Gate) StopLocked("закрывается", false, true);
        }

        private static void SafeDeactivate(Action stop)
        {
            try
            {
                stop();
            }
            catch (Exception e)
            {
                NexusDiagnostics.Error("CLI: остановка компонента", e);
            }
        }

        private static void Snapshot()
        {
            if (_projectPath != null) return;
            _projectPath = NexusPaths.ProjectRoot;
            _projectName = System.IO.Path.GetFileName(_projectPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            _unity = Application.unityVersion;
            try
            {
                _log = Application.consoleLogPath;
            }
            catch
            {
                _log = null; // не во всех режимах редактора есть лог
            }
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
                    var response = Handle(line, stream);
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

        private static readonly byte[] Ack = new UTF8Encoding(false).GetBytes("{\"ack\":true}\n");

        private static Response Handle(string line, NetworkStream stream)
        {
            if (!TryParse(line, out var request, out var error)) return error;

            var pending = new Pending { Request = request, Context = NewContext(request) };

            // демону — подтверждение приёма ДО постановки в очередь: оборвалось до него — команда точно не
            // выполнялась (демон повторит); не записалось — в очередь не ставим
            if (string.Equals(request.Client, "nexusd", StringComparison.Ordinal))
            {
                try
                {
                    stream.Write(Ack, 0, Ack.Length);
                    stream.Flush();
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("CLI: подтверждение приёма", e);
                    return new Response { ExitCode = NexusCommandContext.Exit.Busy, Output = "Соединение оборвалось.", Project = _projectName };
                }
            }

            // проверка и постановка — под тем же замком, что и остановка: иначе команда могла бы застрять в очереди
            lock (Gate)
            {
                if (!_running)
                    return new Response { ExitCode = NexusCommandContext.Exit.Busy, Output = "Редактор занят — повторите.", Project = _projectName };
                Queue.Enqueue(pending);
            }

            var wait = request.Timeout > 0 ? Math.Min(request.Timeout, MaxWaitSeconds) : DefaultWaitSeconds;
            if (!pending.Done.Wait(TimeSpan.FromSeconds(wait)))
            {
                pending.Context.MarkCancelled();
                return new Response
                {
                    ExitCode = NexusCommandContext.Exit.Timeout,
                    Output = $"Нет ответа за {wait} с (редактор занят или команда долгая). Команда может завершиться позже.",
                    Project = _projectName,
                };
            }

            return pending.Response;
        }

        // ============================================================ общее для транспортов

        /// <summary>Разобрать запрос и проверить токен. Можно звать с любого потока.</summary>
        internal static bool TryParse(string json, out Request request, out Response error)
        {
            error = null;
            try
            {
                request = string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<Request>(json, Wire);
            }
            catch
            {
                request = null;
            }

            if (request == null || string.IsNullOrEmpty(request.Command))
            {
                error = new Response { Id = request?.Id, ExitCode = NexusCommandContext.Exit.Usage, Output = "Неверный запрос.", Project = _projectName };
                return false;
            }

            var token = _token;
            if (token == null || !SameToken(token, request.Token))
            {
                NexusDiagnostics.Trace("CLI", "отказ: неверный токен");
                error = new Response
                {
                    Id = request.Id, ExitCode = NexusCommandContext.Exit.Usage, Project = _projectName,
                    Output = "Неверный токен: клиент читает устаревший файл экземпляра — повторите.",
                };
                return false;
            }

            // команды идут через Nexus Daemon (блокировка задач, очередь, будильник); прямой TCP — только для отладки
            if (!string.Equals(request.Client, "nexusd", StringComparison.Ordinal) && !NexusCliStore.DirectTcp)
            {
                error = new Response
                {
                    Id = request.Id, ExitCode = NexusCommandContext.Exit.Usage, Project = _projectName,
                    Output = "Прямой TCP к редактору выключен: команды идут через Nexus Daemon (клиент nexus 5+). Для отладки: nexus-cli.json → DirectTcp.",
                };
                return false;
            }

            return true;
        }

        private static NexusCommandContext NewContext(Request request) =>
            new NexusCommandContext(request.Command, request.Args ?? Array.Empty<string>(), request.Cwd) { Force = request.Force };

        /// <summary>
        /// Выполнить команду в главном потоке: <see cref="NexusCommands.Execute"/> и ожидание редактора
        /// (<see cref="NexusCommandContext.WaitForEditor"/>). Общий путь TCP, файлового канала и шагов задач.
        /// </summary>
        internal static int RunCommand(NexusCommandContext ctx)
        {
            var generation = NexusCompileLog.Generation;
            var exit = NexusCommands.Execute(ctx);

            if (exit == NexusCommandContext.Exit.Ok && ctx.RetryReason != null && ctx.Job == null)
            {
                NexusCliRetry.Start(ctx);
                return exit;
            }

            if (exit == NexusCommandContext.Exit.Ok && ctx.WaitForEditor && ctx.Job == null)
                NexusEditorCommands.StartSettle(ctx, generation);
            return exit;
        }

        /// <summary>Выполнить разобранный запрос в главном потоке и собрать ответ.</summary>
        internal static Response ProcessRequest(Request request) => Complete(request, NewContext(request));

        private static Response Complete(Request request, NexusCommandContext ctx)
        {
            var counter = NexusCliJobs.Counter;
            var exit = RunCommand(ctx);
            var result = Serialize(ctx.Data, ctx);
            var running = ctx.Job != null && NexusCliJobs.IsRunning(ctx.Job);

            // срок вызывающего — только заданию, начатому этим вызовом: job.status/task.status лишь смотрят на чужое
            if (running && request.Timeout > 0)
            {
                var job = NexusCliJobs.Find(ctx.Job);
                if (NexusCliJobs.CreatedAfter(job, counter))
                {
                    if (job.Kind == NexusCliTasks.Kind) NexusCliTasks.LimitTimeout(job, request.Timeout);
                    else NexusCliJobs.LimitDeadline(job, request.Timeout);
                }
            }
            return new Response
            {
                Id = request.Id,
                ExitCode = exit,
                Ok = exit == NexusCommandContext.Exit.Ok,
                Output = ctx.Output,
                Result = result,
                Project = _projectName,
                Job = ctx.Job,
                Pending = running,
            };
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

        private static double _nextDaemonCheck;

        private static void Pump()
        {
            // демон мог упасть или выйти по простою, пока редактор открыт: проверка раз в 30 с (дешёвая — файл и pid)
            var now = EditorApplication.timeSinceStartup;
            if (now >= _nextDaemonCheck)
            {
                _nextDaemonCheck = now + 30;
                NexusCliDaemon.Ensure();
            }

            while (Queue.TryDequeue(out var p))
            {
                if (p.Context.Cancelled)
                {
                    p.Response = new Response { ExitCode = NexusCommandContext.Exit.Timeout, Project = _projectName };
                    p.Done.Set();
                    continue;
                }

                try
                {
                    p.Response = Complete(p.Request, p.Context);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Error("CLI: выполнение", e);
                    p.Response = new Response { ExitCode = NexusCommandContext.Exit.Crashed, Output = "✗ " + e.Message, Project = _projectName };
                }

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

            /// <summary>Будильник сторожа: off | soft | focus (см. <see cref="Wake"/>).</summary>
            public string Wake = "soft";

            /// <summary>Пускать TCP-запросы мимо демона (отладка, клиенты 1.x). По умолчанию выключено.</summary>
            public bool DirectTcp;
        }

        /// <summary>
        /// Прямой TCP к редактору мимо демона (личная настройка, для отладки). Выключено — команды принимаются только
        /// от Nexus Daemon: блокировка задач честная, очередь и будильник общие.
        /// </summary>
        public static bool DirectTcp => JsonIo.Load<Config>(ConfigPath)?.DirectTcp ?? false;

        public static readonly string[] WakeModes = { "off", "soft", "focus" };

        /// <summary>
        /// Будить ли редактор, застрявший в фоне (Unity не продолжает работу после перезагрузки домена, пока окно
        /// не в фокусе). Сторож читает настройку на каждом зависании.
        /// <list type="bullet">
        /// <item><c>off</c> — только диагноз;</item>
        /// <item><c>soft</c> (по умолчанию) — сообщение окну «приложение активно» без смены фокуса;</item>
        /// <item><c>focus</c> — если мягкого не хватило: Unity на передний план на пару секунд, затем фокус
        /// возвращается окну, где вы работали. Без мыши и клавиатуры.</item>
        /// </list>
        /// </summary>
        public static string Wake
        {
            get
            {
                var v = JsonIo.Load<Config>(ConfigPath)?.Wake;
                return Array.IndexOf(WakeModes, v) >= 0 ? v : "soft";
            }
            set
            {
                if (Array.IndexOf(WakeModes, value) < 0) throw new ArgumentException("wake: off | soft | focus");
                var config = JsonIo.Load<Config>(ConfigPath) ?? new Config();
                config.Wake = value;
                JsonIo.Save(ConfigPath, config);
            }
        }

        internal static string ConfigPath => System.IO.Path.Combine(NexusPaths.UserRoot, "nexus-cli.json");

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
                var config = JsonIo.Load<Config>(ConfigPath) ?? new Config();
                config.Enabled = value;
                JsonIo.Save(ConfigPath, config);
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

                // замена без окна «файла нет»: клиент читает его в любой момент; занят клиентом — повторить
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        Thread.Sleep(20);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 5)
                    {
                        Thread.Sleep(20);
                    }
                }
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
