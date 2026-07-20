using System.Collections.Generic;
using System.IO;
using Exerussus.Nexus.Manifests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>
    /// Работа с UPM-зависимостями проекта. ЧИТАЕТ/ПИШЕТ Packages/manifest.json,
    /// сохраняя все незнакомые поля (scopedRegistries и пр.) через extension-data —
    /// иначе перезапись затёрла бы их. Факт «пакет реально подтянулся» проверяем по
    /// packages-lock.json (Unity пишет туда только успешно разрешённые зависимости).
    /// </summary>
    public static class PackageRegistry
    {
        public enum AddResult { Added, AlreadyPresent, Conflict }

        private static string ManifestPath => Path.Combine(NexusPaths.ProjectRoot, "Packages", "manifest.json");
        private static string LockPath     => Path.Combine(NexusPaths.ProjectRoot, "Packages", "packages-lock.json");

        /// <summary>Объявлен ли пакет в Packages/manifest.json.</summary>
        public static bool Has(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var m = JsonIo.Load<UpmManifest>(ManifestPath);
            return m?.Dependencies != null && m.Dependencies.ContainsKey(name);
        }

        /// <summary>Подтянулся ли пакет (есть в packages-lock.json — успешно разрешён).</summary>
        public static bool IsResolved(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var l = JsonIo.Load<UpmLock>(LockPath);
            return l?.Dependencies != null && l.Dependencies.ContainsKey(name);
        }

        /// <summary>Загружена ли сборка с таким именем (asmdef name = имя сборки).
        /// Ловит код, уже присутствующий в проекте любым путём — UPM/embedded/вендор.</summary>
        public static bool AssemblyPresent(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return false;
            foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(a.GetName().Name, assemblyName, System.StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>Найти package.json с таким name где-то в Assets (последний резорт —
        /// рекурсивный скан; зовём только когда зависимости иначе нет). Путь или null.</summary>
        public static string FindInAssets(string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return null;
            var root = Path.Combine(NexusPaths.ProjectRoot, "Assets");
            if (!Directory.Exists(root)) return null;

            foreach (var file in Directory.EnumerateFiles(root, "package.json", SearchOption.AllDirectories))
            {
                PackageJsonName pj = null;
                try { pj = JsonIo.Load<PackageJsonName>(file); } catch { /* битый package.json — пропускаем */ }
                if (pj != null && string.Equals(pj.Name, packageName, System.StringComparison.Ordinal))
                    return file;
            }
            return null;
        }

        /// <summary>Уже есть в проекте? Дёшево → дорого: объявлен/разрешён (UPM) →
        /// сборка загружена → (последним) скан package.json в Assets.</summary>
        public static bool PresentInProject(PackageDependency d)
        {
            if (d == null) return false;
            if (Has(d.Name) || IsResolved(d.Name)) return true;
            if (AssemblyPresent(d.Assembly)) return true;
            return FindInAssets(d.Name) != null;
        }

        /// <summary>Откуда зависимость уже есть в проекте (для отчёта), или null если её нет.</summary>
        public static string LocalSource(PackageDependency d)
        {
            if (d == null) return null;
            if (IsResolved(d.Name)) return "UPM";
            if (AssemblyPresent(d.Assembly)) return $"сборка «{d.Assembly}»";
            var p = FindInAssets(d.Name);
            return p != null ? "Assets: " + (NexusPaths.ToAssetPath(p) ?? p) : null;
        }

        /// <summary>Прописать git-зависимость; чужой пин с другим значением НЕ перетираем.</summary>
        public static AddResult Add(string name, string gitValue)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(gitValue)) return AddResult.Conflict;

            var m = JsonIo.Load<UpmManifest>(ManifestPath) ?? new UpmManifest();
            m.Dependencies ??= new Dictionary<string, string>();

            if (m.Dependencies.TryGetValue(name, out var existing))
                return existing == gitValue ? AddResult.AlreadyPresent : AddResult.Conflict;

            m.Dependencies[name] = gitValue;
            JsonIo.Save(ManifestPath, m);
            return AddResult.Added;
        }

        /// <summary>git-значение зависимости: просто URL (версии не пиним — последнее).</summary>
        public static string GitValue(PackageDependency d) => d?.GitUrl;

        // только поле name из package.json
        private sealed class PackageJsonName
        {
            public string Name { get; set; }

            [JsonExtensionData]
            public IDictionary<string, JToken> Extra { get; set; }
        }

        // модель manifest.json: dependencies + всё прочее сохраняем дословно
        private sealed class UpmManifest
        {
            public Dictionary<string, string> Dependencies { get; set; }

            [JsonExtensionData]
            public IDictionary<string, JToken> Extra { get; set; }
        }

        // модель packages-lock.json: нужен только список ключей dependencies
        private sealed class UpmLock
        {
            public Dictionary<string, JToken> Dependencies { get; set; }

            [JsonExtensionData]
            public IDictionary<string, JToken> Extra { get; set; }
        }
    }
}
