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
    /// (<c>Tools~/daemon/nexusd.dll</c>, .NET 8+) — один на машину: обслуживает файловый канал, сторожа и будильник,
    /// ходит в редактор по TCP. Работает, только пока включён CLI: выключен — демон не запускается (и сам выходит,
    /// когда CLI выключен во всех проектах).
    /// </summary>
    /// <remarks>
    /// Запуск — в фоновом потоке (поиск dotnet и ожидание старта не держат редактор). Порядок поиска dotnet:
    /// DOTNET_ROOT, PATH, Program Files, затем dotnet из самой Unity (Data/DotNetSdk). Работает демон старее
    /// пакетного — его просят выйти и запускают новый; новее — не трогают (другой проект на свежем Nexus).
    /// </remarks>
    public static class NexusCliDaemon
    {
        private static readonly object Gate = new object();
        private static volatile string _status = "демон: не проверялся";
        private static volatile bool _starting;

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
            if (running != null)
            {
                var version = (string)running["version"];
                if (Compare(version, packageVersion) >= 0)
                {
                    _status = $"демон {version}: работает (pid {(int?)running["pid"]}, порт {(int?)running["port"]})";
                    return;
                }

                // наш пакет новее: попросить старый демон уйти и подождать его выхода
                _status = $"демон {version} старее пакетного {packageVersion} — обновляю";
                Shutdown((int?)running["port"] ?? 0);
                var pid = (int?)running["pid"] ?? 0;
                for (var i = 0; i < 50 && Alive(pid); i++) Thread.Sleep(100);
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

            _status = "демон: не запустился — нужен .NET 8 или новее (https://dotnet.microsoft.com/download), агентские функции недоступны";
            NexusDiagnostics.Error("CLI", "Nexus Daemon не запустился: установите .NET 8+ Runtime (https://dotnet.microsoft.com/download).");
        }

        /// <summary>
        /// Скопировать демон (Tools~/daemon) и клиентов (Tools~/cli → cli/) в %LOCALAPPDATA%\Exerussus\Nexus\daemon\&lt;версия&gt;.
        /// Уже скопированная версия не переписывается (её файлы может держать работающий демон).
        /// </summary>
        private static string Install(string dll, string version)
        {
            try
            {
                var target = Path.Combine(Root, "daemon", version);
                var marker = Path.Combine(target, "nexusd.dll");
                var from = Path.GetDirectoryName(dll) ?? string.Empty;
                // версия — в имени папки: изменили демон — поднимите версию (nexusd.version), иначе копия не обновится
                if (!File.Exists(marker))
                {
                    var temp = target + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    Directory.CreateDirectory(Path.Combine(temp, "cli"));
                    foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(temp, Path.GetFileName(file)), true);
                    foreach (var file in Directory.GetFiles(NexusCliStore.PackageClientDir)) File.Copy(file, Path.Combine(temp, "cli", Path.GetFileName(file)), true);
                    if (Directory.Exists(target))
                    {
                        try
                        {
                            Directory.Delete(target, true);
                        }
                        catch
                        {
                            // старая копия занята работающим демоном — оставить её
                            Directory.Delete(temp, true);
                            return File.Exists(marker) ? marker : null;
                        }
                    }

                    Directory.Move(temp, target);
                }

                return marker;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: копия демона", e);
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
