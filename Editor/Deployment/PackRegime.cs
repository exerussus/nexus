using System;
using System.IO;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>
    /// Регламент инертности. ЕДИНСТВЕННОЕ правило: инертная обёртка — это
    /// «&lt;имя&gt;.&lt;целевое-расширение&gt;.txt» (например LogsPage.cs.txt → LogsPage.cs).
    /// Внешний «.txt» = «распакуй меня»; целевой путь берётся из манифеста (deploy-
    /// маппинг), а НЕ угадывается по имени.
    /// </summary>
    public static class PackRegime
    {
        public const string InertSuffix = ".txt";

        public static bool IsInert(string fileName)
            => fileName != null && fileName.EndsWith(InertSuffix, StringComparison.Ordinal);

        /// <summary>Живое имя → инертное (LogsPage.cs → LogsPage.cs.txt).</summary>
        public static string ToInert(string liveName) => liveName + InertSuffix;

        /// <summary>Инертное имя → живое (LogsPage.cs.txt → LogsPage.cs).</summary>
        public static string ToLive(string inertName)
            => IsInert(inertName)
                ? inertName.Substring(0, inertName.Length - InertSuffix.Length)
                : inertName;

        // Расширения, которые Unity импортирует/компилирует. В Plugins таких файлов
        // в ЖИВОМ виде быть не должно — иначе выключенный плагин всё равно «давит»
        // на проект. Используется валидацией перед развёрткой.
        private static readonly string[] Importable =
        {
            ".cs", ".asmdef", ".asmref", ".uxml", ".uss", ".asset",
            ".prefab", ".dll", ".unity", ".mat", ".shader", ".compute",
        };

        public static bool IsImportable(string fileName)
        {
            var ext = Path.GetExtension(fileName);
            foreach (var e in Importable)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
