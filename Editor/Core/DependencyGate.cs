using System.Collections.Generic;
using System.Linq;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Dependency-gate, уровень 1 (манифест/реестр, до развёртки): проверяет
    /// requires страницы против сервисов. Если не сходится — развёртку запрещаем,
    /// иначе сгенерённый код страницы не скомпилируется (уровень 2 — компилятор).
    /// </summary>
    public static class DependencyGate
    {
        /// <summary>Пустой список — путь чист; иначе человекочитаемые причины отказа.</summary>
        public static List<string> Check(PluginManifest manifest)
        {
            var issues = new List<string>();
            if (manifest?.Requires == null) return issues;

            foreach (var req in manifest.Requires)
            {
                if (string.IsNullOrEmpty(req?.Service)) continue;

                var svc = ServiceRegistry.Find(req.Service);
                if (svc == null || !svc.Present)
                {
                    issues.Add($"нужен сервис «{req.Service}» — отсутствует");
                    continue;
                }

                if (!VersionRange.TryParse(req.Range, out var range))
                {
                    issues.Add($"некорректный диапазон требования «{req.Service}»: '{req.Range}'");
                    continue;
                }
                if (!SemVer.TryParse(svc.EffectiveVersion, out var have))
                {
                    issues.Add($"у сервиса «{req.Service}» некорректная версия: '{svc.EffectiveVersion}'");
                    continue;
                }
                if (!range.Satisfies(have))
                    issues.Add($"сервис «{req.Service}» v{have} не удовлетворяет требованию {req.Range} " +
                               "(мажор точный, минор снизу)");
            }

            return issues;
        }

        public static bool Ok(PluginManifest manifest) => Check(manifest).Count == 0;

        /// <summary>Внешние UPM-пакеты страницы, которых НЕТ в проекте. В отличие от
        /// отсутствующего сервиса (жёсткий отказ) это РАЗРЕШИМО — Nexus предложит
        /// прописать пакет и поставить, затем развернёт страницу (двухфазно).</summary>
        public static List<PackageDependency> MissingPackages(PluginManifest manifest)
        {
            var missing = new List<PackageDependency>();
            if (manifest?.PackageRequires == null) return missing;

            foreach (var p in manifest.PackageRequires)
            {
                if (string.IsNullOrEmpty(p?.Name) || string.IsNullOrEmpty(p?.GitUrl)) continue;
                if (!PackageRegistry.PresentInProject(p)) missing.Add(p);
            }
            return missing;
        }
    }
}
