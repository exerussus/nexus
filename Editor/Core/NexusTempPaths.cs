using System;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Папка <c>NexusTemp</c> в корне проекта: файловый канал CLI и артефакты команд (скриншоты, отчёты тестов).
    /// Unity её не импортирует и не чистит; внутри — <c>.gitignore</c> со звёздочкой, папка игнорирует сама себя.
    /// </summary>
    public static class NexusTempPaths
    {
        public static string Root => Path.Combine(NexusPaths.ProjectRoot, "NexusTemp");
        public static string Screenshots => Path.Combine(Root, "screenshots");
        public static string Tests => Path.Combine(Root, "tests");
        public static string Cli => Path.Combine(Root, "cli");

        /// <summary>Создать корень и <c>.gitignore</c>. Никогда не бросает.</summary>
        public static string EnsureRoot()
        {
            var root = Root;
            try
            {
                Directory.CreateDirectory(root);
                var ignore = Path.Combine(root, ".gitignore");
                if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n");
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("NexusTemp: корень", e);
            }

            return root;
        }

        /// <summary>Создать подпапку NexusTemp (и корень с .gitignore).</summary>
        public static string Ensure(string dir)
        {
            EnsureRoot();
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Путь относительно корня проекта (с «/»), если файл внутри проекта; иначе как есть.</summary>
        public static string ProjectRelative(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var root = NexusPaths.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            var p = path.Replace('\\', '/');
            return p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length) : p;
        }
    }
}
