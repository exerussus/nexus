using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Запуск и обновление Nexus Daemon — компонент сервера CLI (владелец — <see cref="NexusCliServer"/>). Демон
    /// (<c>Tools~/daemon/nexusd.dll</c>, .NET 10+) — один на машину: обслуживает файловый канал, сторожа и будильник,
    /// ходит в редактор по TCP. Работает, только пока включён CLI: выключен — демон не запускается (и сам выходит,
    /// когда CLI выключен во всех проектах).
    /// </summary>
    /// <remarks>
    /// Запуск — в фоновом потоке (поиск dotnet и ожидание старта не держат редактор). Порядок поиска dotnet:
    /// DOTNET_ROOT, PATH, Program Files, затем dotnet из самой Unity (Data/DotNetSdk). dotnet, рядом с которым видно
    /// только рантаймы старше <see cref="RuntimeMajor"/>, пропускается сразу — иначе каждый такой кандидат стоил бы
    /// ожидания старта впустую (хост dotnet просто не найдёт фреймворк и выйдет). Работает демон старее
    /// пакетного — его просят выйти и запускают новый; новее — не трогают (другой проект на свежем Nexus).
    /// </remarks>
    public static class NexusCliDaemon
    {
        /// <summary>Мажор .NET, под который собран демон (tfm в nexusd.runtimeconfig.json); новее — тоже годится.</summary>
        private const int RuntimeMajor = 10;

        /// <summary>Сколько уходящий демон доводит идущие вызовы (nexusd: Program.DrainSeconds) — плюс запас.</summary>
        private const int DrainWaitSeconds = 120 + 15;

        /// <summary>Проба живости: попыток подряд без ответа, после которых процесс демона считается зависшим.</summary>
        private const int ProbeAttempts = 3;

        private static readonly object Gate = new object();
        private static volatile string _status = "демон: не проверялся";
        private static volatile bool _starting;

        /// <summary>pid старого демона, о невыходе которого уже сообщено в лог (не повторять каждые 30 с).</summary>
        private static int _reportedStuckPid;

        /// <summary>Версия пакета Nexus (из Package Manager; вне пакета — «dev»).</summary>
        public static string NexusVersion
        {
            get
            {
                try
                {
                    return UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NexusCliDaemon).Assembly)?.version ?? "dev";
                }
                catch
                {
                    return "dev";
                }
            }
        }

        /// <summary>Строка для status / Manage.</summary>
        public static string Describe() => _status;

        public static string PackageDir => Path.Combine(NexusCliStore.PackageClientDir, "..", "daemon");
        private static string Dll => Path.GetFullPath(Path.Combine(PackageDir, "nexusd.dll"));
        private static string Root => Path.GetDirectoryName(NexusCliStore.InstancesRoot);
        private static string DaemonFile => Path.Combine(Root, "daemon.json");
        private static string TokensFile => Path.Combine(Root, "tokens.json");

        /// <summary>Проверить демон и при необходимости запустить (из главного потока, работа — в фоне).</summary>
        internal static void Ensure()
        {
            lock (Gate)
            {
                if (_starting) return;
                _starting = true;
            }

            // всё, что спрашивает Unity, — здесь, в главном потоке
            var unityDotnet = UnityDotnet();
            var dll = Dll;
            var packageVersion = PackageVersion();
            new Thread(() =>
            {
                try
                {
                    Run(dll, packageVersion, unityDotnet);
                }
                catch (Exception e)
                {
                    _status = "демон: ошибка запуска — " + e.Message;
                    NexusDiagnostics.Swallowed("CLI: запуск демона", e);
                }
                finally
                {
                    _starting = false;
                }
            }) { IsBackground = true, Name = "Nexus daemon launcher" }.Start();
        }

        private static void Run(string dll, string packageVersion, string unityDotnet)
        {
            if (!File.Exists(dll))
            {
                _status = "демон: нет Tools~/daemon/nexusd.dll в пакете";
                return;
            }

            var running = ReadDaemon();
            // живой pid ещё не значит живой демон: pid мог достаться другому процессу, демон мог зависнуть —
            // спросить его самого (GET /v1/daemon) и сверить pid и время старта
            var draining = false;
            if (running != null && !Responds(running, out draining))
            {
                if (!KillHung(running)) running = null;   // pid чужой — демона просто нет
                else
                {
                    running = null;
                    NexusDiagnostics.Warn("CLI", "Nexus Daemon не отвечал и был остановлен — запускаю заново");
                }
            }

            if (running != null)
            {
                var version = (string)running["version"];
                var pid = (int?)running["pid"] ?? 0;
                if (Compare(version, packageVersion) >= 0 && !draining)
                {
                    _status = $"демон {version}: работает (pid {pid}, порт {(int?)running["port"]})";
                    return;
                }

                // наш пакет новее (или демон уже уходит): попросить его уйти — он доводит идущие вызовы и выходит
                if (!draining) Shutdown((int?)running["port"] ?? 0);
                var until = DateTime.UtcNow.AddSeconds(DrainWaitSeconds);
                while (Alive(pid) && DateTime.UtcNow < until)
                {
                    _status = $"демон {version}: завершает идущие вызовы перед обновлением до {packageVersion} (pid {pid})";
                    Thread.Sleep(250);
                }

                if (Alive(pid))
                {
                    // не вышел: новый не запустится (одна копия на пользователя) — не делать вид, что всё в порядке
                    _status = $"демон {version} не вышел для обновления до {packageVersion} (pid {pid}): работает старая версия — " +
                              "завершите процесс nexusd или перезапустите редактор";
                    if (_reportedStuckPid != pid)
                    {
                        _reportedStuckPid = pid;
                        NexusDiagnostics.Error("CLI", $"Nexus Daemon {version} (pid {pid}) не вышел за {DrainWaitSeconds} с после просьбы обновиться до {packageVersion}.");
                    }

                    return;
                }
            }

            // запуск из копии вне пакета: работающий демон держит свои файлы, а папку пакета Package Manager
            // должен уметь удалить при обновлении
            var installed = Install(dll, packageVersion);
            if (installed == null)
            {
                _status = "демон: не удалось скопировать в " + Path.Combine(Root, "daemon");
                return;
            }

            foreach (var dotnet in Candidates(unityDotnet))
            {
                if (!HasRuntime(dotnet))
                {
                    NexusDiagnostics.Trace("CLI", $"dotnet пропущен — нет рантайма .NET {RuntimeMajor}+: {dotnet}");
                    continue;
                }

                _status = "демон: запускаю через " + dotnet;
                if (!Start(dotnet, installed)) continue;

                for (var i = 0; i < 80; i++)
                {
                    Thread.Sleep(100);
                    var now = ReadDaemon();
                    if (now == null) continue;
                    _status = $"демон {(string)now["version"]}: работает (pid {(int?)now["pid"]}, порт {(int?)now["port"]})";
                    return;
                }
            }

            _status = $"демон: не запустился — нужен .NET {RuntimeMajor} или новее (https://dotnet.microsoft.com/download), агентские функции недоступны";
            NexusDiagnostics.Error("CLI", $"Nexus Daemon не запустился: установите .NET {RuntimeMajor}+ Runtime (https://dotnet.microsoft.com/download).");
        }

        /// <summary>
        /// Скопировать демон (Tools~/daemon) и клиентов (Tools~/cli → cli/) в %LOCALAPPDATA%\Exerussus\Nexus\daemon\&lt;версия&gt;.
        /// Уже скопированная версия не переписывается (её файлы может держать работающий демон).
        /// </summary>
        private static string Install(string dll, string version)
        {
            var target = Path.Combine(Root, "daemon", version);
            var marker = Path.Combine(target, "nexusd.dll");
            var from = Path.GetDirectoryName(dll) ?? string.Empty;
            // версия — в имени папки: изменили демон — поднимите версию (nexusd.version), иначе копия не обновится
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (File.Exists(marker)) return marker;
                string temp = null;
                try
                {
                    temp = target + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    Directory.CreateDirectory(Path.Combine(temp, "cli"));
                    foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(temp, Path.GetFileName(file)), true);
                    foreach (var file in Directory.GetFiles(NexusCliStore.PackageClientDir)) File.Copy(file, Path.Combine(temp, "cli", Path.GetFileName(file)), true);
                    // копия появляется целиком одним переносом: папка версии с маркером — всегда полная
                    Directory.Move(temp, target);
                    return marker;
                }
                catch (Exception e)
                {
                    TryDeleteDir(temp);
                    // второй редактор той же версии успел первым — его копия годится, её не трогать
                    if (File.Exists(marker)) return marker;
                    NexusDiagnostics.Swallowed("CLI: копия демона", e);
                    // папка версии без маркера — обрывок старой схемы копирования: убрать и попробовать ещё раз
                    if (attempt > 0 || !Directory.Exists(target) || !TryDeleteDir(target)) return null;
                }
            }

            return File.Exists(marker) ? marker : null;
        }

        private static bool TryDeleteDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
                return true;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: удаление " + dir, e);
                return false;
            }
        }

        /// <summary>
        /// Отвечает ли демон из daemon.json: GET /v1/daemon (короткий таймаут, до <see cref="ProbeAttempts"/> попыток —
        /// зовётся из фонового потока запуска, главный поток не ждёт) с тем же pid и временем старта. Ответил чужой
        /// pid — daemon.json устарел, ответил другой демон: он жив, трогать нечего.
        /// </summary>
        private static bool Responds(JObject daemon, out bool draining)
        {
            draining = false;
            var port = (int?)daemon["port"] ?? 0;
            var pid = (int?)daemon["pid"] ?? 0;
            if (port <= 0) return false;
            for (var attempt = 0; attempt < ProbeAttempts; attempt++)
            {
                if (attempt > 0) Thread.Sleep(2000);
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/v1/daemon");
                    request.Method = "GET";
                    request.Timeout = 1500;
                    request.ReadWriteTimeout = 1500;
                    request.Headers["Authorization"] = "Bearer " + ReadToken();
                    request.Proxy = null;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null, Encoding.UTF8))
                    {
                        var o = JObject.Parse(reader.ReadToEnd());
                        draining = (bool?)o["draining"] ?? false;
                        var answeredPid = (int?)o["pid"] ?? 0;
                        if (answeredPid != pid)
                            NexusDiagnostics.Trace("CLI", $"на порту {port} отвечает демон pid {answeredPid}, а в daemon.json — {pid}");
                        else if (StartedUtc(o) is DateTime a && StartedUtc(daemon) is DateTime b && Math.Abs((a - b).TotalSeconds) > 1)
                            NexusDiagnostics.Trace("CLI", $"демон pid {pid}: время старта не совпадает с daemon.json");
                        return true;
                    }
                }
                catch (WebException e) when (e.Response is HttpWebResponse)
                {
                    // ответил (401 при сменившемся токене и т.п.) — значит, жив
                    e.Response.Close();
                    return true;
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed($"CLI: проба демона (попытка {attempt + 1})", e);
                }
            }

            return false;
        }

        /// <summary>
        /// Демон не отвечает: остановить его процесс — но только если это действительно dotnet/nexusd, запущенный
        /// около времени из daemon.json (pid мог достаться чужому процессу). true — был наш процесс (остановлен или
        /// уже вышел); false — pid чужой, демона нет.
        /// </summary>
        private static bool KillHung(JObject daemon)
        {
            var pid = (int?)daemon["pid"] ?? 0;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    var name = (p.ProcessName ?? string.Empty).ToLowerInvariant();
                    if (name != "dotnet" && name != "nexusd") return false;
                    if (StartedUtc(daemon) is DateTime started)
                    {
                        try
                        {
                            if (Math.Abs((p.StartTime.ToUniversalTime() - started).TotalSeconds) > 120) return false;
                        }
                        catch (Exception e)
                        {
                            NexusDiagnostics.Swallowed("CLI: время старта демона", e);
                        }
                    }

                    _status = $"демон pid {pid}: не отвечает — останавливаю";
                    p.Kill();
                    p.WaitForExit(3000);
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // уже вышел
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: остановка зависшего демона", e);
                return true;
            }
        }

        /// <summary>Поле started (UTC): Newtonsoft сам превращает ISO-строку в дату — учесть оба вида.</summary>
        private static DateTime? StartedUtc(JObject o)
        {
            var t = o?["started"];
            if (t == null) return null;
            if (t.Type == JTokenType.Date) return ((DateTime)t).ToUniversalTime();
            return DateTime.TryParse((string)t, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
        }

        private static string ReadToken()
        {
            try
            {
                return (string)JObject.Parse(File.ReadAllText(TokensFile))["local"]?["token"];
            }
            catch
            {
                return null;
            }
        }

        /// <summary>daemon.json живого демона или null.</summary>
        private static JObject ReadDaemon()
        {
            try
            {
                if (!File.Exists(DaemonFile)) return null;
                var o = JObject.Parse(File.ReadAllText(DaemonFile));
                return Alive((int?)o["pid"] ?? 0) ? o : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool Alive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void Shutdown(int port)
        {
            if (port <= 0) return;
            try
            {
                var token = (string)JObject.Parse(File.ReadAllText(TokensFile))["local"]?["token"];
                var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/v1/daemon/shutdown");
                request.Method = "POST";
                request.Timeout = 3000;
                request.Headers["Authorization"] = "Bearer " + token;
                request.Proxy = null; // системный прокси не должен перехватывать 127.0.0.1
                request.ContentLength = 0;
                using (request.GetResponse())
                {
                }
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: остановка старого демона", e);
            }
        }

        private static bool Start(string dotnet, string dll)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = dotnet,
                    Arguments = $"\"{dll}\"",
                    // через оболочку: демон не наследует дескрипторы Unity (сокет сервера, открытые файлы)
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(dll) ?? string.Empty,
                };
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: запуск " + dotnet, e);
                return false;
            }
        }

        private static IEnumerable<string> Candidates(string unityDotnet)
        {
            var exe = Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(root)) yield return Path.Combine(root, exe);

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string full;
                try
                {
                    full = Path.Combine(dir.Trim(), exe);
                }
                catch
                {
                    continue;
                }

                if (File.Exists(full) && seen.Add(full)) yield return full;
            }

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
            {
                var full = Path.Combine(pf, "dotnet", exe);
                if (File.Exists(full) && seen.Add(full)) yield return full;
            }

            foreach (var path in new[] { "/usr/local/share/dotnet/dotnet", "/usr/share/dotnet/dotnet" })
                if (File.Exists(path) && seen.Add(path))
                    yield return path;

            if (!string.IsNullOrEmpty(unityDotnet))
                foreach (var candidate in unityDotnet.Split('|'))
                    if (File.Exists(candidate) && seen.Add(candidate))
                        yield return candidate;
        }

        /// <summary>
        /// Есть ли рядом с dotnet рантайм Microsoft.NETCore.App мажора <see cref="RuntimeMajor"/> или новее. Папки
        /// shared не видно (dotnet — ссылка или нестандартная раскладка) — не знаем, пробуем запустить.
        /// </summary>
        private static bool HasRuntime(string dotnet)
        {
            try
            {
                var shared = Path.Combine(Path.GetDirectoryName(dotnet) ?? string.Empty, "shared", "Microsoft.NETCore.App");
                if (!Directory.Exists(shared)) return true;

                foreach (var dir in Directory.GetDirectories(shared))
                {
                    var name = Path.GetFileName(dir);
                    var dot = name.IndexOf('.');
                    if (int.TryParse(dot < 0 ? name : name.Substring(0, dot), out var major) && major >= RuntimeMajor) return true;
                }

                return false;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: проверка рантайма " + dotnet, e);
                return true;
            }
        }

        /// <summary>dotnet из самой Unity 6 (Data/DotNetSdk, Data/NetCoreRuntime) — запасной путь без установки .NET; через «|».</summary>
        private static string UnityDotnet()
        {
            try
            {
                var contents = EditorApplication.applicationContentsPath;
                var exe = Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
                return Path.Combine(contents, "DotNetSdk", exe) + "|" + Path.Combine(contents, "NetCoreRuntime", exe);
            }
            catch
            {
                return null;
            }
        }

        private static string PackageVersion()
        {
            try
            {
                var file = Path.Combine(PackageDir, "nexusd.version");
                return File.Exists(file) ? File.ReadAllText(file).Trim() : "0.0.0";
            }
            catch
            {
                return "0.0.0";
            }
        }

        /// <summary>Сравнение версий «a.b.c» (хвост после «-» отбрасывается).</summary>
        private static int Compare(string a, string b)
        {
            var x = Parse(a);
            var y = Parse(b);
            for (var i = 0; i < 3; i++)
                if (x[i] != y[i])
                    return x[i].CompareTo(y[i]);
            return 0;
        }

        private static int[] Parse(string v)
        {
            var r = new int[3];
            var parts = (v ?? "0").Split('-')[0].Split('.');
            for (var i = 0; i < 3 && i < parts.Length; i++) int.TryParse(parts[i], out r[i]);
            return r;
        }
    }
}
