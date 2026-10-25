using System;
using System.IO;
using System.Text;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>Журнал демона: daemon.log (≤1 МБ, одна предыдущая копия). Пишется из любых потоков.</summary>
    internal static class Log
    {
        private const long MaxBytes = 1 << 20;
        private static readonly object Gate = new object();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static bool Echo { get; set; }

        public static void Info(string message) => Write("info", message);
        public static void Warn(string message) => Write("warn", message);

        public static void Error(string context, Exception e) => Write("error", context + ": " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);

        public static void Error(string message) => Write("error", message);

        private static void Write(string level, string message)
        {
            var line = $"{DateTime.UtcNow:O} {level} {message}\n";
            lock (Gate)
            {
                try
                {
                    if (Echo) Console.Error.Write(line);
                    Directory.CreateDirectory(DaemonPaths.Root);
                    var path = DaemonPaths.LogFile;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }

                    File.AppendAllText(path, line, Utf8);
                }
                catch
                {
                    // журнал никогда не роняет демон
                }
            }
        }
    }
}
