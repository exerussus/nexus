using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Обратные ссылки: какие ассеты области прямо зависят от целей (<see cref="AssetDatabase.GetDependencies(string, bool)"/>
    /// без рекурсии). Всё живёт в рамках вызова — кэша между вызовами нет.
    /// </summary>
    internal static class AssetReferences
    {
        /// <summary>Расширения файлов, которые сами ни на что не ссылаются: их не открываем (быстрее на больших проектах).</summary>
        private static readonly HashSet<string> Leaf = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".dll", ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".hdr", ".bmp", ".gif", ".svg",
            ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac", ".mp4", ".mov", ".webm", ".ttf", ".otf",
            ".txt", ".json", ".xml", ".bytes", ".csv", ".md", ".yaml", ".hlsl", ".cginc", ".asmdef", ".asmref", ".rsp",
        };

        /// <summary>Цели: путь ассета или (для папки) все ассеты внутри неё.</summary>
        public static HashSet<string> Expand(IEnumerable<string> paths)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in paths)
            {
                set.Add(p);
                if (!AssetDatabase.IsValidFolder(p)) continue;
                foreach (var g in AssetDatabase.FindAssets(string.Empty, new[] { p }))
                {
                    var inner = AssetDatabase.GUIDToAssetPath(g);
                    if (!string.IsNullOrEmpty(inner)) set.Add(inner);
                }
            }

            return set;
        }

        /// <summary>
        /// Ассеты области <paramref name="scope"/>, которые прямо ссылаются на любую из <paramref name="targets"/>
        /// (сами цели не считаются). <paramref name="all"/> — открывать и файлы-«листья» (текстуры, скрипты, …).
        /// Ключ — ссылающийся ассет, значение — на какие цели он ссылается.
        /// </summary>
        public static SortedDictionary<string, List<string>> Find(HashSet<string> targets, string scope, bool all, out int scanned)
        {
            scanned = 0;
            var result = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in AssetDatabase.FindAssets(string.Empty, new[] { scope }))
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                if (targets.Contains(path) || AssetDatabase.IsValidFolder(path)) continue;
                if (!all && Leaf.Contains(Path.GetExtension(path))) continue;

                scanned++;
                foreach (var dep in AssetDatabase.GetDependencies(path, false))
                {
                    if (dep == path || !targets.Contains(dep)) continue;
                    if (!result.TryGetValue(path, out var list)) result[path] = list = new List<string>();
                    list.Add(dep);
                }
            }

            return result;
        }
    }
}
