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

        /// <summary>Циклы демона остановлены: только после ожидания идущих вызовов (<see cref="Drain"/>).</summary>
        private static volatile bool _stopped;

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
                Loop("файлы", 50, FileAdapter.Tick),
                Loop("сторож", 500, Watchdog.Tick),
                Loop("уборка", 10000, Calls.Sweep),
                Loop("простой", 5000, CheckIdle),
            };

            Exit.Wait();
            Drain();
            _stopped = true;
            Log.Info("выход");
            http.Stop();
            DaemonInfo.Delete();
            return 0;
        }

        /// <summary>Сколько уходящий демон ждёт идущих вызовов (обновление пакета), с.</summary>
        public const int DrainSeconds = 120;

        /// <summary>
        /// Остановка по API (обновление): новые вызовы уже не принимаются (<see cref="Calls.Draining"/>), идущие
        /// доводятся до конца, но не дольше <see cref="DrainSeconds"/>; циклы демона (файлы, сторож) работают до выхода.
        /// Не дождавшиеся вызовы файлового канала новый демон закроет итогом «демон перезапущен».
        /// </summary>
        private static void Drain()
        {
            if (!Calls.Draining) return;
            var until = DateTime.UtcNow.AddSeconds(DrainSeconds);
            while (Calls.Active.Any() && DateTime.UtcNow < until) Thread.Sleep(200);
            var left = Calls.Active.Count();
            if (left > 0) Log.Warn($"выход с незавершёнными вызовами: {left} (ждал {DrainSeconds} с)");
            // циклы ещё идут: повторная запись итогов файлового канала (если первая не удалась) успевает пройти
            Thread.Sleep(500);
        }

        private static Task Loop(string name, int intervalMs, Action tick) => Task.Run(async () =>
        {
            while (!_stopped)
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
