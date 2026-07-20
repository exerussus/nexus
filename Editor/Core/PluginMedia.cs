using System.IO;
using Exerussus.Nexus.Deployment;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Необязательные медиа плагина — иконка и README — читаются ХАБОМ из исходной
    /// папки Plugins/&lt;id&gt; (она всегда есть как каталог, независимо от развёртки).
    /// Это метаданные, а не рантайм-ассеты страницы: их не деплоят в State, страница к
    /// ним не обращается, раскладкой владеет хаб (инвариант #7 не задет).
    ///
    /// Соглашение об именах: иконка — page_logo.png, ридми — README.md (или вариации).
    /// </summary>
    public static class PluginMedia
    {
        public const string IconFile = "page_logo.png";

        // дефолтная иконка хаба; используется, если у плагина нет своей page_logo.png
        private const string DefaultIconAssetPath = NexusPaths.EditorAssetRoot + "/Media/default_page_icon.png";

        private static readonly string[] ReadmeNames =
            { "README.md", "readme.md", "Readme.md", "README.txt", "readme.txt" };

        /// <summary>Иконка плагина: своя page_logo.png → иначе дефолтная default_page_icon.png →
        /// иначе null (ничего не показываем). Служебные id («::…») иконку не получают.</summary>
        public static Texture2D LoadIcon(string id)
        {
            if (string.IsNullOrEmpty(id) || id.StartsWith("::")) return null;
            var iconAsset = NexusPaths.ToAssetPath(Path.Combine(PluginRoots.SourceDir(id), IconFile));
            var own = iconAsset != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(iconAsset) : null;
            if (own != null) return own;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(DefaultIconAssetPath);   // дефолт или null
        }

        public static bool HasReadme(string id) => ReadmePath(id) != null;

        /// <summary>Текст README плагина или null, если его нет.</summary>
        public static string ReadReadme(string id)
        {
            var path = ReadmePath(id);
            if (path == null) return null;
            try { return File.ReadAllText(path); }
            catch { return null; }
        }

        private static string ReadmePath(string id)
        {
            if (string.IsNullOrEmpty(id) || id.StartsWith("::")) return null;
            var dir = PluginRoots.SourceDir(id);   // абсолютный путь (встроенный или доп. корень)
            foreach (var name in ReadmeNames)
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
