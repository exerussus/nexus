using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Проект с Nexus, известный демону по файлу экземпляра (его пишет Editor Bridge). Поля обновляет реестр;
    /// читать можно из любого потока — значения простые, запись атомарна по ссылке.
    /// </summary>
    internal sealed class Project
    {
        public string Id;
        public string Name;
        public string Path;
        public string InstanceFile;

        /// <summary>Порт и токен Bridge — одной парой: читаются и подменяются одной ссылкой (без смеси старого и нового).</summary>
        public sealed class Endpoint
        {
            public readonly int Port;
            public readonly string Token;

            public Endpoint(int port, string token)
            {
                Port = port;
                Token = token;
            }
        }

        public volatile Endpoint Bridge = new Endpoint(0, null);

        public int Port => Bridge.Port;
        public string Token => Bridge.Token;

        /// <summary>Сколько вызовов одновременно разговаривают с Unity (её сервер держит до 8 соединений).</summary>
        public readonly System.Threading.SemaphoreSlim Slots = new System.Threading.SemaphoreSlim(5);

        /// <summary>Сколько обновлений подряд файла экземпляра не было на диске.</summary>
        public int Misses;
        public volatile int Pid;
        public volatile string EditorState = "ready";   // ready | reloading | closed (из файла экземпляра)
        public volatile string Unity;
        public volatile string EditorLog;

        /// <summary>Когда файл экземпляра последний раз изменился (начало перезагрузки домена).</summary>
        public DateTime InstanceWritten;

        /// <summary>Последний признак жизни главного потока (ping или ответ на вызов), UTC.</summary>
        public DateTime AliveAt = DateTime.UtcNow;

        public volatile string EditorPhase = "редактор";
        public volatile bool Removed;

        private int _inFlight;

        public int InFlight => Volatile.Read(ref _inFlight);
        public void Enter() => Interlocked.Increment(ref _inFlight);
        public void Leave() => Interlocked.Decrement(ref _inFlight);

        /// <summary>Фаза последнего идущего вызова (для пульса и сторожа).</summary>
        public volatile string CallPhase;

        /// <summary>Вызов (пакет efc с lock), который держит блокировку проекта: чужие меняющие вызовы получают отказ.</summary>
        public volatile string LockHolder;

        /// <summary>Команды редактора из bridge.hello (name, summary, usage, readOnly) — для паспорта канала и блокировки.</summary>
        public volatile System.Text.Json.Nodes.JsonArray Commands;

        /// <summary>Команда только читает и проходит чужую блокировку. trash.clean проходит любые блокировки (efc, SPEC 7).</summary>
        public bool ReadOnly(string command)
        {
            if (command == "trash.clean" || command == "ping" || command == "help" || command == "dialog.list") return true;
            // git демона: чтение проходит блокировку, запись — нет (как меняющие команды редактора)
            if (GitCommands.Owns(command)) return GitCommands.IsReadOnly(command);
            // служебные: демону и клиентам нужны всегда (список команд, задания, состояние демона)
            if (command == "bridge.hello" || command == "daemon.status" || command == "jobs" || command.StartsWith("job.", StringComparison.Ordinal)) return true;
            var list = Commands;
            if (list == null) return false;
            foreach (var c in list)
                if (Json.Str(c, "name") == command) return Json.Bool(c, "readOnly");
            return false;
        }

        public string CliDir => System.IO.Path.Combine(Path, "NexusTemp", "cli");
        public string TempDir => System.IO.Path.Combine(Path, "NexusTemp");

        /// <summary>Поле started файла экземпляра (UTC, ISO): когда сервер CLI этого редактора стартовал.</summary>
        public volatile string Started;

        // Процесс редактора: один раз на пару (pid, started) проверяется, что это действительно Unity,
        // запущенная не позже записи файла экземпляра, и хэндл держится открытым — дальше «жив ли»
        // = HasExited по нему, без GetProcessById и без исключений. Раньше GetProcessById шёл на КАЖДОЕ
        // чтение State (~20 раз/с на проект), после падения Unity бросал исключение на каждое обращение,
        // а pid, доставшийся чужому процессу, считался живым редактором: сторож будил окна чужой программы,
        // dialog.list снимал её диалоги. Открытый хэндл к тому же не даёт системе отдать pid другому.
        private readonly object _procGate = new object();
        private int _procPid = -1;
        private string _procStarted;
        private Process _proc;

        public bool PidAlive
        {
            get
            {
                var pid = Pid;
                var started = Started;
                lock (_procGate)
                {
                    if (pid != _procPid || !string.Equals(started, _procStarted, StringComparison.Ordinal))
                    {
                        ReleaseProcessLocked();
                        _procPid = pid;
                        _procStarted = started;
                        _proc = OpenEditorProcess(pid, started);
                    }

                    if (_proc == null) return false;
                    try
                    {
                        if (!_proc.HasExited) return true;
                    }
                    catch
                    {
                        // хэндл больше не годится — считаем закрытым до смены pid
                    }

                    ReleaseProcessLocked();
                    return false;
                }
            }
        }

        /// <summary>Отпустить хэндл процесса (проект убран из реестра).</summary>
        public void ReleaseProcess()
        {
            lock (_procGate) ReleaseProcessLocked();
        }

        private void ReleaseProcessLocked()
        {
            try
            {
                _proc?.Dispose();
            }
            catch
            {
                // неважно
            }

            _proc = null;
        }

        /// <summary>Живой процесс Unity с этим pid, начатый не позже started (+ запас), или null.</summary>
        private static Process OpenEditorProcess(int pid, string started)
        {
            if (pid <= 0) return null;
            Process p = null;
            try
            {
                p = Process.GetProcessById(pid);
                if (p.HasExited || !p.ProcessName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase))
                {
                    p.Dispose();
                    return null;
                }

                // процесс, начатый ПОСЛЕ записи файла экземпляра, — не тот редактор (pid переиспользован);
                // запас — на дрожание системных часов
                if (DateTime.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s) &&
                    p.StartTime.ToUniversalTime() > s.ToUniversalTime().AddSeconds(60))
                {
                    p.Dispose();
                    return null;
                }

                // Process из GetProcessById хэндл не держит (каждый HasExited открывал бы pid заново) —
                // обращение к SafeHandle открывает и удерживает его до Dispose
                _ = p.SafeHandle;
                return p;
            }
            catch
            {
                p?.Dispose();
                return null;
            }
        }

        /// <summary>Итоговое состояние для клиентов: ready | reloading | stalled | closed.</summary>
        public string State
        {
            get
            {
                if (EditorState == "closed" || !PidAlive) return "closed";
                if (Stalled) return "stalled";
                if (EditorState == "reloading" || Port <= 0 || string.IsNullOrEmpty(Token)) return "reloading";
                return "ready";
            }
        }

        /// <summary>Пульс главного потока стоит дольше порога (сторож пишет диагноз и может будить).</summary>
        public volatile bool Stalled;

        public bool Ready => State == "ready";

        public double SilentSeconds
        {
            get
            {
                var since = EditorState == "reloading" ? Max(AliveAt, InstanceWritten) : AliveAt;
                return (DateTime.UtcNow - since).TotalSeconds;
            }
        }

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        public void MarkAlive() => AliveAt = DateTime.UtcNow;

        public string Phase => CallPhase ?? EditorPhase ?? "редактор";

        public bool OwnsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var root = Normalize(Path);
                var p = Normalize(path);
                return p == root || p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string Normalize(string path) => System.IO.Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

        public bool Exists => Directory.Exists(Path);
    }
}
