using System.IO;
using System.Linq;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>
    /// Единственный источник путей Nexus (живёт в слое Deployment — он и владеет
    /// файловой раскладкой). Статический stateless-сервис: только вычисление путей.
    ///
    /// КОРЕНЬ ДВИЖКА НЕ ЗАХАРДКОЖЕН: папку можно положить куда угодно
    /// (Assets/Plugins/Exerussus.Nexus и т.п.), а при установке через Package Manager
    /// она вообще лежит ВНЕ Assets — в кэше пакетов (Library/PackageCache/...) и
    /// доступна AssetDatabase по виртуальному пути «Packages/&lt;имя&gt;/...». Поэтому корень
    /// определяется в рантайме и кэшируется на домен.
    ///
    /// Всё ИЗМЕНЯЕМОЕ лежит вне корня движка: развёрнутый код — в Assets (его нужно
    /// компилировать и он должен быть записываемым), конфиг — в ProjectSettings,
    /// личное — в UserSettings. Поэтому корень может быть read-only.
    /// </summary>
    public static class NexusPaths
    {
        public const string VendorFolder = "Exerussus.Nexus";

        /// <summary>Запасной путь, если корень не удалось определить (свежий импорт).</summary>
        private const string FallbackEngineAssetRoot = "Assets/Plugins/" + VendorFolder;

        /// <summary>Развёрнутый код — ВСЕГДА в Assets (компилируется, должен быть записываем),
        /// рядом с папкой движка, но отдельно от неё: Nexus обновляется, State остаётся.</summary>
        public const string StatesFolder    = "NexusStates";
        public const string StatesAssetRoot = "Assets/Plugins/" + StatesFolder;

        /// <summary>Абсолютный корень проекта (родитель Assets/).</summary>
        public static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)!.FullName;

        // ---- корень движка: определяется в рантайме (Assets/... либо Packages/...) ----

        private static string _engineAssetRoot;   // «Assets/Plugins/Exerussus.Nexus» или «Packages/com.exerussus.nexus»
        private static string _engineAbsRoot;     // абсолютный путь в файловой системе

        /// <summary>Корень движка для AssetDatabase («Assets/…» или «Packages/…»).</summary>
        public static string EngineAssetRoot { get { Resolve(); return _engineAssetRoot; } }

        /// <summary>Корень движка в файловой системе (в т.ч. кэш пакетов).</summary>
        public static string EngineAbsRoot { get { Resolve(); return _engineAbsRoot; } }

        /// <summary>Asset-относительный корень редакторной части (для AssetDatabase-операций).</summary>
        public static string EditorAssetRoot => EngineAssetRoot + "/Editor";

        public static string EditorAbsRoot => Path.Combine(EngineAbsRoot, "Editor");

        public static string PluginsRoot  => Path.Combine(EditorAbsRoot, "Plugins");
        public static string ServicesRoot => Path.Combine(EditorAbsRoot, "Services");

        // ---- развёрнутое, коммитится, ВНЕ папки движка (Assets/Plugins/NexusStates) ----

        public static string StateRoot => Path.Combine(ProjectRoot, "Assets", "Plugins", StatesFolder);

        // ---- персональное, git-ignored (UserSettings/Exerussus.Nexus) ----

        public static string UserRoot =>
            Path.Combine(ProjectRoot, "UserSettings", VendorFolder);

        public static string PreserveRoot => Path.Combine(UserRoot, "Preserve");

        // ---- проектный конфиг ВНЕ папки движка (ProjectSettings, коммитится) ----

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

        /// <summary>Абсолютный путь → путь для AssetDatabase. Понимает и Assets/, и папку
        /// движка внутри пакета (тогда отдаёт «Packages/&lt;имя&gt;/…»), и Packages/ проекта.
        /// null — если путь вне того, что видит AssetDatabase.</summary>
        public static string ToAssetPath(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            var p    = Norm(absolute);
            var data = Norm(Application.dataPath);

            if (p == data) return "Assets";
            if (p.StartsWith(data + "/")) return "Assets" + p.Substring(data.Length);

            // движок (и его встроенные плагины) может лежать в кэше пакетов
            var engine = Norm(EngineAbsRoot);
            if (!string.IsNullOrEmpty(engine))
            {
                if (p == engine) return EngineAssetRoot;
                if (p.StartsWith(engine + "/")) return EngineAssetRoot + p.Substring(engine.Length);
            }

            // встроенные/локальные пакеты проекта
            var packages = Norm(Path.Combine(ProjectRoot, "Packages"));
            if (p.StartsWith(packages + "/")) return "Packages" + p.Substring(packages.Length);

            return null;
        }

        /// <summary>Абсолютный путь → относительный корню проекта, или null, если вне проекта.</summary>
        public static string ToProjectRelative(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            var p    = Norm(absolute);
            var root = Norm(ProjectRoot);
            if (p == root) return string.Empty;
            return p.StartsWith(root + "/") ? p.Substring(root.Length + 1) : null;
        }

        /// <summary>Asset-относительный путь исходной папки плагина (для AssetDatabase —
        /// напр. загрузка иконки page_logo.png хабом).</summary>
        public static string PluginAssetDir(string id) => EditorAssetRoot + "/Plugins/" + id;

        public static string PluginAssetPath(string id, string name) => PluginAssetDir(id) + "/" + name;

        /// <summary>Asset-путь к развёрнутому файлу страницы по ИМЕНИ относительно её
        /// корня (раскладку — `NexusStates/&lt;id&gt;/` — знает только хаб, страница лишь даёт имя).</summary>
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

        // ---- определение корня ----

        private static string Norm(string p) => string.IsNullOrEmpty(p) ? p : p.Replace('\\', '/').TrimEnd('/');

        /// <summary>Сбросить кэш корня (напр. после перемещения папки без рекомпила).</summary>
        public static void InvalidateRoot()
        {
            _engineAssetRoot = null;
            _engineAbsRoot   = null;
        }

        private static void Resolve()
        {
            if (_engineAssetRoot != null) return;

            // 1) установлен как UPM-пакет: реальный путь — кэш пакетов, для AssetDatabase — Packages/<имя>
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(NexusPaths).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.assetPath))
                {
                    _engineAssetRoot = Norm(info.assetPath);
                    _engineAbsRoot   = Norm(string.IsNullOrEmpty(info.resolvedPath)
                        ? Path.Combine(ProjectRoot, info.assetPath)
                        : info.resolvedPath);
                    NexusDiagnostics.Trace("Пути", $"движок как пакет: {_engineAssetRoot} → {_engineAbsRoot}");
                    return;
                }
            }
            catch (System.Exception ex) { NexusDiagnostics.Swallowed("определение пакета движка", ex); }

            // 2) лежит в Assets где угодно — ищем по asmdef ядра, а не по фиксированному пути
            try
            {
                var guid = AssetDatabase.FindAssets("Exerussus.Nexus.Deployment t:AssemblyDefinitionAsset")
                                        .FirstOrDefault();
                var asmdef = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(asmdef))
                {
                    // <root>/Editor/Deployment/<asmdef> → на два уровня вверх
                    var deploymentDir = Norm(Path.GetDirectoryName(asmdef));
                    var editorDir     = Norm(Path.GetDirectoryName(deploymentDir));
                    var root          = Norm(Path.GetDirectoryName(editorDir));
                    if (!string.IsNullOrEmpty(root))
                    {
                        _engineAssetRoot = root;
                        _engineAbsRoot   = Norm(Path.Combine(ProjectRoot, root));
                        NexusDiagnostics.Trace("Пути", $"движок в проекте: {_engineAssetRoot}");
                        return;
                    }
                }
            }
            catch (System.Exception ex) { NexusDiagnostics.Swallowed("поиск корня движка по asmdef", ex); }

            // 3) не нашли (например, ассеты ещё не импортированы) — запасной путь
            _engineAssetRoot = FallbackEngineAssetRoot;
            _engineAbsRoot   = Norm(Path.Combine(ProjectRoot, FallbackEngineAssetRoot));
            NexusDiagnostics.Warn("Пути", $"корень движка не определён, использую {FallbackEngineAssetRoot}");
        }
    }
}
