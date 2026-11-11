using System.IO;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Папка темы (Editor/Theme) — без захардкоженного «Packages/com.exerussus.nexus». Движок может
    /// стоять UPM-пакетом (кэш пакетов, AssetDatabase видит «Packages/&lt;имя&gt;/…») или лежать в
    /// Assets где угодно. Theme не ссылается на Deployment (там NexusPaths), поэтому корень ищется
    /// тут же тем же способом: пакет сборки → asmdef темы через AssetDatabase. Найденное кэшируется
    /// на домен; запасной путь НЕ кэшируется, чтобы после импорта ассетов поиск повторился.
    /// </summary>
    internal static class NexusThemePaths
    {
        private const string AsmdefName = "Exerussus.Nexus.Theme";
        private const string FallbackAssetDir = "Packages/com.exerussus.nexus/Editor/Theme";

        private static string _assetDir;
        private static string _absDir;
        private static UnityEditor.PackageManager.PackageInfo _package;

        /// <summary>Папка темы для AssetDatabase («Packages/…/Editor/Theme» или «Assets/…/Editor/Theme»).</summary>
        public static string AssetDir => Resolve() ? _assetDir : FallbackAssetDir;

        /// <summary>Папка темы в файловой системе (в т.ч. кэш пакетов); null — если не определена.</summary>
        public static string AbsDir => Resolve() ? _absDir : null;

        /// <summary>Пакет, в котором стоит движок; null — движок лежит в Assets.</summary>
        public static UnityEditor.PackageManager.PackageInfo Package => Resolve() ? _package : null;

        public static string UssDir => AssetDir + "/Uss";
        public static string FontsDir => AssetDir + "/Fonts";

        private static bool Resolve()
        {
            if (_assetDir != null) return true;

            // 1) движок — UPM-пакет
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NexusThemePaths).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.assetPath))
                {
                    var root = Norm(info.assetPath);
                    var abs  = string.IsNullOrEmpty(info.resolvedPath) ? Path.Combine(ProjectRoot(), root) : info.resolvedPath;
                    _package  = info;
                    _assetDir = root + "/Editor/Theme";
                    _absDir   = Norm(Path.Combine(abs, "Editor", "Theme"));
                    return true;
                }
            }
            catch (System.Exception ex) { NexusDiagnostics.Swallowed("определение пакета темы", ex); }

            // 2) движок в Assets — папка asmdef темы и есть Editor/Theme
            try
            {
                foreach (var guid in AssetDatabase.FindAssets(AsmdefName + " t:AssemblyDefinitionAsset"))
                {
                    var path = Norm(AssetDatabase.GUIDToAssetPath(guid));
                    if (string.IsNullOrEmpty(path) || !path.EndsWith("/" + AsmdefName + ".asmdef")) continue;

                    var dir = Norm(Path.GetDirectoryName(path));
                    if (string.IsNullOrEmpty(dir)) continue;

                    _package  = null;
                    _assetDir = dir;
                    _absDir   = Norm(Path.Combine(ProjectRoot(), dir));
                    return true;
                }
            }
            catch (System.Exception ex) { NexusDiagnostics.Swallowed("поиск asmdef темы", ex); }

            NexusDiagnostics.Trace("Тема", $"папка темы не определена, использую {FallbackAssetDir}");
            return false;
        }

        private static string ProjectRoot() => Directory.GetParent(Application.dataPath)!.FullName;

        private static string Norm(string p) => string.IsNullOrEmpty(p) ? p : p.Replace('\\', '/').TrimEnd('/');
    }
}
