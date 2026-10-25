using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Реестр проектов: файлы экземпляров %LOCALAPPDATA%\Exerussus\Nexus\instances\*.json. Обслуживаются только
    /// редакторы с Editor Bridge под демон (поле <c>daemonProtocol</c>): Nexus 1.x ведёт файловый канал сам.
    /// </summary>
    internal static class Registry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Project> ByFile = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);

        public static event Action<Project> Added;
        public static event Action<Project> Removed;

        public static Project[] All
        {
            get
            {
                lock (Gate) return ByFile.Values.ToArray();
            }
        }

        /// <summary>Есть ли файлы экземпляров вообще (CLI включён хоть где-то).</summary>
        public static bool AnyInstance { get; private set; }

        public static void Refresh()
        {
            string[] files;
            try
            {
                files = Directory.Exists(DaemonPaths.Instances) ? Directory.GetFiles(DaemonPaths.Instances, "*.json") : Array.Empty<string>();
            }
            catch (Exception e)
            {
                Log.Warn("реестр: " + e.Message);
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = new List<Project>();
            var removed = new List<Project>();
            var any = false;

            lock (Gate)
            {
                foreach (var file in files)
                {
                    var o = Json.ReadFile(file);
                    any = true;
                    if (o == null)
                    {
                        // файл есть, но не читается (Unity подменяет его, антивирус) — проект не трогаем
                        if (ByFile.ContainsKey(file)) seen.Add(file);
                        continue;
                    }

                    if (Json.Num(o, "daemonProtocol") < 1) continue; // Nexus 1.x — не наш

                    var path = Json.Str(o, "path");
                    if (string.IsNullOrEmpty(path)) continue;
                    seen.Add(file);

                    if (!ByFile.TryGetValue(file, out var p))
                    {
                        p = new Project { InstanceFile = file, Path = path, Name = Json.Str(o, "project") ?? System.IO.Path.GetFileName(path) };
                        ByFile[file] = p;
                        added.Add(p);
                    }

                    p.Path = path;
                    p.Misses = 0;
                    var port = (int)Json.Num(o, "port");
                    var token = Json.Str(o, "token");
                    if (port != p.Port || token != p.Token) p.Bridge = new Project.Endpoint(port, token);
                    p.Pid = (int)Json.Num(o, "pid");
                    p.EditorState = Json.Str(o, "state") ?? "ready";
                    p.Unity = Json.Str(o, "unity");
                    p.EditorLog = Json.Str(o, "log");
                    try
                    {
                        p.InstanceWritten = File.GetLastWriteTimeUtc(file);
                    }
                    catch
                    {
                        // неважно: только для отсчёта перезагрузки
                    }
                }

                foreach (var file in ByFile.Keys.ToArray())
                    if (!seen.Contains(file))
                    {
                        var p = ByFile[file];
                        // убираем, только если файла нет два обновления подряд (CLI выключен), а не после сбоя чтения
                        if (++p.Misses < 2) continue;
                        p.Removed = true;
                        ByFile.Remove(file);
                        removed.Add(p);
                    }

                AssignIds();
                AnyInstance = any;
            }

            foreach (var p in added)
            {
                Log.Info($"проект {p.Id}: {p.Path}");
                Added?.Invoke(p);
            }

            foreach (var p in removed)
            {
                Log.Info($"проект {p.Id} убран (CLI выключен)");
                Removed?.Invoke(p);
            }
        }

        /// <summary>Id — имя папки; одинаковые имена — имя + короткий хеш пути.</summary>
        private static void AssignIds()
        {
            var groups = ByFile.Values.GroupBy(p => System.IO.Path.GetFileName(p.Path.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var list = g.ToList();
                foreach (var p in list)
                    p.Id = list.Count == 1 ? g.Key : g.Key + "~" + ShortHash(p.Path);
            }
        }

        private static string ShortHash(string s)
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
            return Convert.ToHexString(bytes, 0, 2).ToLowerInvariant();
        }

        /// <summary>Найти проект: id (без регистра), путь проекта или путь внутри него.</summary>
        public static Project Resolve(string key, out string problem)
        {
            problem = null;
            var all = All;
            if (string.IsNullOrEmpty(key))
            {
                if (all.Length == 1) return all[0];
                problem = all.Length == 0 ? "Нет открытых проектов с Nexus CLI." : "Несколько проектов — укажите какой: " + string.Join(", ", all.Select(p => p.Id));
                return null;
            }

            foreach (var p in all)
                if (string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
                    return p;

            Project best = null;
            foreach (var p in all)
                if (p.OwnsPath(key) && (best == null || p.Path.Length > best.Path.Length))
                    best = p;
            if (best != null) return best;

            problem = $"Проект «{key}» не найден. Открыты: " + (all.Length == 0 ? "нет" : string.Join(", ", all.Select(p => p.Id)));
            return null;
        }

        public static bool Contains(Project p)
        {
            lock (Gate) return ByFile.ContainsValue(p);
        }
    }
}
