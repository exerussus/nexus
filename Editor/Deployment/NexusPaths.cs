using System.IO;
using UnityEngine;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>
    /// Единственный источник путей Nexus (живёт в слое Deployment — он и владеет
    /// файловой раскладкой). Статический stateless-сервис: только вычисление путей.
    ///
    /// Две КОММИТЯЩИЕСЯ проектные папки под Assets (Plugins — дистрибутив,
    /// State — развёрнутое) и ПЕРСОНАЛЬНОЕ хранилище под UserSettings (Preserve —
    /// упакованные сейвы + per-page настройки). Editor-only обеспечивает флаг
    /// платформы asmdef, а не расположение.
    /// </summary>
    public static class NexusPaths
    {
        public const string VendorFolder = "Exerussus.Nexus";

        /// <summary>Папка развёрнутого кода — ОТДЕЛЬНО от основной папки Nexus,
        /// чтобы Nexus можно было обновлять/держать read-only (в т.ч. как пакет),
        /// а State коммитить независимо.</summary>
        public const string StatesFolder    = "NexusStates";
        public const string StatesAssetRoot = "Assets/" + StatesFolder;

        /// <summary>Абсолютный корень проекта (родитель Assets/).</summary>
        public static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)!.FullName;

        // ---- проектное, коммитится (Assets/Exerussus.Nexus/Editor) ----

        /// <summary>Asset-относительный корень движка (для AssetDatabase-операций).</summary>
        public const string EditorAssetRoot = "Assets/" + VendorFolder + "/Editor";

        public static string EditorAbsRoot =>
            Path.Combine(Application.dataPath, VendorFolder, "Editor");

        public static string PluginsRoot => Path.Combine(EditorAbsRoot, "Plugins");
        public static string ServicesRoot => Path.Combine(EditorAbsRoot, "Services");

        // ---- развёрнутое, коммитится, но ВНЕ основной папки (Assets/NexusStates) ----

        public static string StateRoot => Path.Combine(ProjectRoot, "Assets", StatesFolder);

        // ---- персональное, git-ignored (UserSettings/Exerussus.Nexus) ----

        public static string UserRoot =>
            Path.Combine(ProjectRoot, "UserSettings", VendorFolder);

        public static string PreserveRoot => Path.Combine(UserRoot, "Preserve");

        // ---- проектный конфиг ВНЕ основной папки (ProjectSettings, коммитится) ----

        public static string ProjectConfigPath => Path.Combine(ProjectRoot, "ProjectSettings", "Nexus.json");

        // ---- временная папка ВНЕ Assets (не импортируется Unity; для temp→swap) ----

        public static string TempRoot => Path.Combine(ProjectRoot, "Temp", VendorFolder);

        // ---- помощники ----

        public static string PluginDir(string id)      => Path.Combine(PluginsRoot, id);
        public static string PluginManifest(string id) => Path.Combine(PluginDir(id), "manifest.json");
        public static string StateDir(string id)       => Path.Combine(StateRoot, id);
        public static string PreserveDir(string id)    => Path.Combine(PreserveRoot, id);
        public static string ServiceManifest(string id) => Path.Combine(ServicesRoot, id, "service-manifest.json");

        /// <summary>Asset-относительный путь папки State (для AssetDatabase.DeleteAsset).</summary>
        public static string StateAssetDir(string id) => StatesAssetRoot + "/" + id;

        /// <summary>Абсолютный путь → asset-относительный («Assets/…») или null, если вне Assets.</summary>
        public static string ToAssetPath(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            var p = absolute.Replace('\\', '/');
            var data = Application.dataPath.Replace('\\', '/');
            if (p == data) return "Assets";
            return p.StartsWith(data + "/") ? "Assets" + p.Substring(data.Length) : null;
        }

        /// <summary>Абсолютный путь → относительный корню проекта, или null, если вне проекта.</summary>
        public static string ToProjectRelative(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            var p = absolute.Replace('\\', '/');
            var root = ProjectRoot.Replace('\\', '/');
            if (p == root) return string.Empty;
            return p.StartsWith(root + "/") ? p.Substring(root.Length + 1) : null;
        }

        /// <summary>Asset-относительный путь исходной папки плагина (для AssetDatabase —
        /// напр. загрузка иконки page_logo.png хабом).</summary>
        public static string PluginAssetDir(string id) => EditorAssetRoot + "/Plugins/" + id;

        public static string PluginAssetPath(string id, string name) => PluginAssetDir(id) + "/" + name;

        /// <summary>Asset-путь к развёрнутому файлу страницы по ИМЕНИ относительно её
        /// корня (раскладку — `State/&lt;id&gt;/` — знает только хаб, страница лишь даёт имя).</summary>
        public static string StateAssetPath(string id, string relativeName)
            => StateAssetDir(id) + "/" + (relativeName ?? string.Empty).Replace('\\', '/').TrimStart('/');

        /// <summary>Папка персональных данных страницы — чистый путь, без создания.</summary>
        public static string UserDir(string id) => Path.Combine(UserRoot, id);

        /// <summary>Папка персональных настроек страницы (создаётся по требованию — для записи).</summary>
        public static string UserConfigDir(string id)
        {
            var dir = UserDir(id);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
