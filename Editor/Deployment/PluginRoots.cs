using System.Collections.Generic;
using System.IO;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>
    /// Корни, где ищутся плагины: встроенный Plugins/ (дистрибутив) + доп. пути из
    /// проектного конфига (для специфичных проекту плагинов). Один источник правды о
    /// том, ГДЕ лежит исходник плагина по id — им пользуются и дискавери, и деплой, и
    /// чтение медиа, поэтому исходная папка плагина больше не привязана к Plugins/.
    /// </summary>
    public static class PluginRoots
    {
        /// <summary>Все корни (абсолютные): встроенный первым, затем существующие доп. пути.</summary>
        public static IEnumerable<string> Roots()
        {
            yield return NexusPaths.PluginsRoot;
            foreach (var rel in NexusConfigStore.Load().ScanPaths)
            {
                var abs = ResolveAbs(rel);
                if (abs != null && Directory.Exists(abs)) yield return abs;
            }
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

        private static string ResolveAbs(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return null;
            return Path.IsPathRooted(rel) ? rel : Path.Combine(NexusPaths.ProjectRoot, rel);
        }
    }
}
