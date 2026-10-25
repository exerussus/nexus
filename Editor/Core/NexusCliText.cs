using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>Оформление вывода команд управления редактором: коротко, чтобы не тратить токены читающего.</summary>
    internal static class NexusCliText
    {
        /// <summary>Первая строка, обрезанная до <paramref name="max"/> символов (0 — без обрезки).</summary>
        public static string OneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var nl = s.IndexOfAny(new[] { '\r', '\n' });
            var line = nl >= 0 ? s.Substring(0, nl) : s;
            var more = nl >= 0 && nl < s.Length - 1;
            if (max > 0 && line.Length > max) return line.Substring(0, max) + "…";
            return more ? line + " …" : line;
        }

        /// <summary>Текст целиком, обрезанный до <paramref name="max"/> символов (0 — без обрезки).</summary>
        public static string Cut(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", string.Empty).TrimEnd('\n');
            return max > 0 && s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        /// <summary>Первые <paramref name="lines"/> строк стека с отступом.</summary>
        public static string StackHead(string stack, int lines, string indent = "    ")
        {
            if (string.IsNullOrEmpty(stack) || lines <= 0) return string.Empty;
            var sb = new StringBuilder();
            var n = 0;
            foreach (var raw in stack.Replace("\r", string.Empty).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (n++ >= lines) break;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(indent).Append(line);
            }

            return sb.ToString();
        }

        public static char Letter(LogType t)
        {
            switch (t)
            {
                case LogType.Error: return 'E';
                case LogType.Exception: return 'X';
                case LogType.Assert: return 'A';
                case LogType.Warning: return 'W';
                default: return 'I';
            }
        }

        public static string TypeName(LogType t)
        {
            switch (t)
            {
                case LogType.Error: return "error";
                case LogType.Exception: return "exception";
                case LogType.Assert: return "assert";
                case LogType.Warning: return "warning";
                default: return "log";
            }
        }

        public static string Clock(long utcTicks) =>
            utcTicks <= 0 ? "--:--:--" : new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        public static string Seconds(double s) => s < 10 ? s.ToString("0.0", CultureInfo.InvariantCulture) + " с" : s.ToString("0", CultureInfo.InvariantCulture) + " с";

        public static string Plural(int n, string one, string few, string many)
        {
            var m10 = n % 10;
            var m100 = n % 100;
            if (m10 == 1 && m100 != 11) return n + " " + one;
            if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return n + " " + few;
            return n + " " + many;
        }

        /// <summary>
        /// Сводка ошибок лога после номера: число и до <paramref name="max"/> разных строк (повторы схлопнуты).
        /// </summary>
        public static int ErrorDigest(long afterSeq, int max, List<string> lines)
        {
            var q = new NexusLogQuery { AfterSeq = afterSeq, Types = new HashSet<LogType> { LogType.Error, LogType.Exception, LogType.Assert } };
            var found = NexusLogBuffer.Query(q, 0, out var total);
            if (lines == null || max <= 0) return total;

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var e in found)
            {
                var key = OneLine(e.Message, 240);
                if (seen.TryGetValue(key, out var c))
                {
                    seen[key] = c + 1;
                    continue;
                }

                seen[key] = 1;
                order.Add(key);
            }

            for (var i = 0; i < order.Count && i < max; i++)
            {
                var c = seen[order[i]];
                lines.Add("  " + order[i] + (c > 1 ? $" (×{c})" : string.Empty));
            }

            if (order.Count > max) lines.Add($"  … ещё {order.Count - max} разных");
            return total;
        }

        /// <summary>Путь файла от команды: абсолютный, от папки клиента или от корня проекта.</summary>
        public static string ResolveOut(string path, string cwd, string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            var baseDir = !string.IsNullOrEmpty(cwd) && Directory.Exists(cwd) ? cwd : projectRoot;
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }
    }
}
