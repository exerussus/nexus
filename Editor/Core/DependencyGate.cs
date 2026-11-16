using System.Collections.Generic;
using System.Linq;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Dependency-gate, уровень 1 (манифест/реестр, до развёртки): проверяет
    /// requires страницы против сервисов и минимальные версии уже установленных
    /// UPM-пакетов (packageRequires[].version). Если не сходится — развёртку запрещаем,
    /// иначе сгенерённый код страницы не скомпилируется (уровень 2 — компилятор).
    /// </summary>
    public static class DependencyGate
    {
        /// <summary>Пустой список — путь чист; иначе человекочитаемые причины отказа.</summary>
        /// <param name="packages">Снимок пакетов вызова (<see cref="PackageRegistry.Snapshot"/>); null — свой.</param>
        public static List<string> Check(PluginManifest manifest, PackageSnapshot packages = null)
        {
            var issues = new List<string>();
            CheckPackageVersions(manifest, issues, packages);
            if (manifest?.Requires == null) return issues;

            List<ServiceInfo> services = null;   // один скан сервисов на все требования
            foreach (var req in manifest.Requires)
            {
                if (string.IsNullOrEmpty(req?.Service)) continue;

                services ??= ServiceRegistry.Discover();
                var svc = services.Find(s => string.Equals(s.ServiceId, req.Service, System.StringComparison.Ordinal));
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

        // Минимальные версии пакетов, которые УЖЕ есть в проекте. Отсутствующий пакет сюда не относится —
        // он уходит в двухфазную установку (ставится последняя версия) и проверяется после неё.
        // Версия установленного не определяется (только сборка, ещё не разрешён) — не блокируем, пишем в Trace.
        private static void CheckPackageVersions(PluginManifest manifest, List<string> issues, PackageSnapshot packages)
        {
            if (manifest?.PackageRequires == null) return;

            foreach (var p in manifest.PackageRequires)
            {
                if (string.IsNullOrEmpty(p?.Name) || string.IsNullOrWhiteSpace(p.Version)) continue;

                if (!PackageVersion.TryParse(p.Version, out var need))
                {
                    issues.Add($"некорректная минимальная версия пакета «{p.Name}»: '{p.Version}'");
                    continue;
                }

                packages ??= PackageRegistry.Snapshot();
                if (!PackageRegistry.PresentInProject(p, packages)) continue;

                var installed = PackageRegistry.InstalledVersion(p.Name, packages);
                if (!PackageVersion.TryParse(installed, out var have))
                {
                    NexusDiagnostics.Trace("Gate",
                        $"«{manifest.Id}»: версия пакета {p.Name} не определена ('{installed}') — минимум {need} не проверен");
                    continue;
                }

                if (have.CompareTo(need) < 0)
                    issues.Add($"нужен {p.Name} {p.Version}, установлен {installed} — обновите пакет " +
                               "(Package Manager → Update; для git-пакета — сбросить его хэш в packages-lock.json)");
            }
        }

        /// <summary>Внешние UPM-пакеты страницы, которых НЕТ в проекте. В отличие от
        /// отсутствующего сервиса (жёсткий отказ) это РАЗРЕШИМО — Nexus предложит
        /// прописать пакет и поставить, затем развернёт страницу (двухфазно).</summary>
        public static List<PackageDependency> MissingPackages(PluginManifest manifest, PackageSnapshot packages = null)
        {
            var missing = new List<PackageDependency>();
            if (manifest?.PackageRequires == null) return missing;

            foreach (var p in manifest.PackageRequires)
            {
                if (string.IsNullOrEmpty(p?.Name) || string.IsNullOrEmpty(p?.GitUrl)) continue;
                packages ??= PackageRegistry.Snapshot();
                if (!PackageRegistry.PresentInProject(p, packages)) missing.Add(p);
            }
            return missing;
        }
    }
}
