using System;
using System.Collections.Generic;
using System.IO;
using Exerussus.Nexus.Abstractions;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>Откуда взялся корень плагинов.</summary>
    public enum PluginRootSource
    {
        BuiltIn,    // встроенный Plugins/ движка
        ScanPath,   // ручной путь из Manage → Scan paths
        Marker,     // найден по маркеру nexus-plugins.json
    }

    /// <summary>Корень плагинов для показа в Manage (включая пропавшие и дубли).</summary>
    public sealed class PluginRootInfo
    {
        public PluginRootSource Source;
        public string Display;     // как показывать: project-relative или asset-путь
        public string Name;        // имя из маркера (для Marker), иначе null
        public string AbsPath;     // реальный путь; null — не удалось вычислить
        public bool   Exists;      // папка на месте
        public bool   Duplicate;   // тот же реальный путь уже дан корнем выше — игнорируется
    }

    /// <summary>
    /// Корни, где ищутся плагины: встроенный Plugins/ (дистрибутив), ручные пути из
    /// проектного конфига и корни, найденные по маркеру. Один источник правды о том, ГДЕ
    /// лежит исходник плагина по id — им пользуются и дискавери, и деплой, и чтение медиа.
    /// Сам ничего не сканирует: найденные корни берёт из конфига (их пишет поиск по кнопке).
    /// </summary>
    public static class PluginRoots
    {
        /// <summary>Все рабочие корни (абсолютные): существующие, без дублей, по приоритету —
        /// встроенный, ручные, найденные по маркеру.</summary>
        public static IEnumerable<string> Roots()
        {
            foreach (var info in Describe())
                if (info.Exists && !info.Duplicate) yield return info.AbsPath;
        }

        /// <summary>Полный список корней с диагностикой — для Manage.</summary>
        public static List<PluginRootInfo> Describe()
        {
            var list = new List<PluginRootInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cfg  = NexusConfigStore.Load();

            Add(list, seen, PluginRootSource.BuiltIn, NexusPaths.ToAssetPath(NexusPaths.PluginsRoot) ?? "Plugins",
                null, NexusPaths.PluginsRoot);

            foreach (var rel in cfg.ScanPaths)
                Add(list, seen, PluginRootSource.ScanPath, rel, null, ResolveAbs(rel));

            foreach (var root in cfg.DiscoveredRoots)
            {
                if (root == null || string.IsNullOrWhiteSpace(root.AssetPath)) continue;
                Add(list, seen, PluginRootSource.Marker, root.AssetPath, root.Name, NexusPaths.AssetToAbsolute(root.AssetPath));
            }

            return list;
        }

        /// <summary>Исходная папка плагина id — первый корень с &lt;id&gt;/manifest.json;
        /// иначе встроенный путь (для понятных сообщений об ошибке).</summary>
        public static string SourceDir(string id)
        {
            foreach (var root in Roots())
            {
                var dir = Path.Combine(root, id);
                if (File.Exists(Path.Combine(dir, "manifest.json"))) return dir;
            }
            return NexusPaths.PluginDir(id);
        }

        private static void Add(List<PluginRootInfo> list, HashSet<string> seen, PluginRootSource source,
                                string display, string name, string abs)
        {
            var info = new PluginRootInfo
            {
                Source  = source,
                Display = display,
                Name    = name,
                AbsPath = abs,
                Exists  = !string.IsNullOrEmpty(abs) && Directory.Exists(abs),
            };

            if (info.Exists)
            {
                var key = Path.GetFullPath(abs).Replace('\\', '/').TrimEnd('/');
                info.Duplicate = !seen.Add(key);
            }
            else if (source != PluginRootSource.BuiltIn)
            {
                // пропавший корень не ломает дискавери — просто пропускается
                NexusDiagnostics.Trace("Корни плагинов", $"корень '{display}' не найден на диске ({abs ?? "путь не вычислен"})");
            }

            list.Add(info);
        }

        private static string ResolveAbs(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return null;
            return Path.IsPathRooted(rel) ? rel : Path.Combine(NexusPaths.ProjectRoot, rel);
        }
    }
}
