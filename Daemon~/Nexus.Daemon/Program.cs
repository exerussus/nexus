using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Nexus Daemon (nexusd) — один на машину и пользователя. Запускает его Editor Bridge, когда в проекте
    /// включён CLI; дальше живёт сам: переживает перезагрузки домена и закрытие Unity.
    /// Выходит: файлов экземпляров нет (CLI выключен везде) 15 с; только закрытые редакторы — 30 минут простоя;
    /// POST /v1/daemon/shutdown (новая версия пакета).
    /// </summary>
    internal static class Program
    {
        private static readonly ManualResetEventSlim Exit = new ManualResetEventSlim();

        public static int Main(string[] args)
        {
            int? port = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var pp)) port = pp;
                if (args[i] == "--echo") Log.Echo = true;
                if (args[i] == "--version")
                {
                    Console.WriteLine(DaemonInfo.Version);
                    return 0;
                }
            }

            using var mutex = new Mutex(true, "Local\\Exerussus.Nexus.Daemon", out var first);
            if (!first)
            {
                Log.Info("демон уже работает — выход");
                return 0;
            }

            Tokens.Load();
            var http = new HttpServer(Api.Handle);
            if (!http.Start(port))
            {
                Log.Error($"нет свободного порта {DaemonInfo.BasePort}–{DaemonInfo.BasePort + DaemonInfo.PortRange - 1}");
                return 2;
            }

            DaemonInfo.Write(http.Port);
            Log.Info($"nexusd {DaemonInfo.Version} pid {Environment.ProcessId}, порт {http.Port}");
            Api.ShutdownRequested = () => Exit.Set();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Exit.Set();
            };

            Registry.Refresh();
            var loops = new[]
            {
                Loop("реестр", 1000, Registry.Refresh),
                Loop("файлы", 200, FileAdapter.Tick),
                Loop("сторож", 500, Watchdog.Tick),
                Loop("уборка", 10000, Calls.Sweep),
                Loop("простой", 5000, CheckIdle),
            };

            Exit.Wait();
            Log.Info("выход");
            http.Stop();
            DaemonInfo.Delete();
            return 0;
        }

        private static Task Loop(string name, int intervalMs, Action tick) => Task.Run(async () =>
        {
            while (!Exit.IsSet)
            {
                try
                {
                    tick();
                }
                catch (Exception e)
                {
                    Log.Error(name, e);
                }

                await Task.Delay(intervalMs);
            }
        });

        private static DateTime _noInstancesSince = DateTime.MaxValue;
        private static DateTime _idleSince = DateTime.UtcNow;

        private static void CheckIdle()
        {
            var now = DateTime.UtcNow;
            if (!Registry.AnyInstance)
            {
                if (_noInstancesSince == DateTime.MaxValue) _noInstancesSince = now;
                if ((now - _noInstancesSince).TotalSeconds > 15)
                {
                    Log.Info("CLI выключен во всех проектах — выход");
                    Exit.Set();
                }

                return;
            }

            _noInstancesSince = DateTime.MaxValue;
            var busy = Registry.All.Any(p => p.State != "closed") || Calls.Active.Any();
            if (busy) _idleSince = now;
            else if ((now - _idleSince).TotalMinutes > 30)
            {
                Log.Info("30 минут без открытых редакторов — выход");
                Exit.Set();
            }
        }
    }
}
