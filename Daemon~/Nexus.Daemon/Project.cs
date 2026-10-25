using System;
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

        public string CliDir => System.IO.Path.Combine(Path, "NexusTemp", "cli");
        public string TempDir => System.IO.Path.Combine(Path, "NexusTemp");

        public bool PidAlive => DaemonInfo.IsAlive(Pid);

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
