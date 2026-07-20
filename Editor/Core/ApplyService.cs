using System.Collections.Generic;
using System.Linq;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Транзакционный Apply. Все файловые операции выполняются ДО
    /// AssetDatabase.Refresh(), поэтому domain reload не рвёт процедуру; сводку
    /// результата проносим через reload в <see cref="ApplyReportSink"/>.
    ///
    /// Правило подтверждений: тогл и развёртка — молча; выключение/restore/очистка
    /// сироты — через диалог. Деструктив батчится в ОДИН сводный диалог, а не по
    /// модалке на каждую страницу. Restore явно предупреждает об удалении сейва.
    /// </summary>
    public static class ApplyService
    {
        /// <summary>Применить набор намерений (из management-окна).</summary>
        public static void Apply(IReadOnlyList<PendingIntent> intents)
        {
            if (intents == null || intents.Count == 0) return;

            var byId = PluginDiscovery.Discover().ToDictionary(d => d.Id);

            var destructive = new List<string>();
            var warnings    = new List<string>();
            var refusals    = new List<string>();   // deploys, отклонённые dependency-gate
            var okDeploys   = new List<PendingIntent>();
            var undeploys   = new List<PendingIntent>();
            var needsPackages = new List<DiscoveredPlugin>();   // deploys, ждущие UPM-пакета
            var localNotes  = new List<string>();   // пакеты, уже найденные в проекте (для отчёта)

            foreach (var it in intents)
            {
                byId.TryGetValue(it.Id, out var dp);
                var name = dp?.DisplayName ?? it.Id;

                if (it.Kind == IntentKind.Undeploy)
                {
                    undeploys.Add(it);
                    destructive.Add($"• выключить «{name}» — живой код выгрузится (сейв уйдёт в Preserve)");
                    continue;
                }

                // deploy: сперва dependency-gate (уровень 1). Не сходится — не разворачиваем.
                var gate = DependencyGate.Check(dp?.Manifest);
                if (gate.Count > 0)
                {
                    refusals.Add($"• «{name}»: {string.Join("; ", gate)}");
                    continue;
                }

                // нет нужного UPM-пакета → не деплоим сейчас, уводим в двухфазную установку
                if (DependencyGate.MissingPackages(dp?.Manifest).Count > 0 && dp != null)
                {
                    needsPackages.Add(dp);
                    continue;
                }

                okDeploys.Add(it);

                // пакет, объявленный странице, но уже присутствующий в проекте (не через manifest) — отметим
                foreach (var p in dp?.Manifest?.PackageRequires ?? Enumerable.Empty<PackageDependency>())
                {
                    if (string.IsNullOrEmpty(p?.Name) || PackageRegistry.Has(p.Name)) continue;
                    var src = PackageRegistry.LocalSource(p);
                    if (src != null)
                        localNotes.Add($"«{name}»: пакет {p.Name} уже в проекте ({src}) — git-установка не требуется");
                }

                if (PluginDeployer.HasPreserve(it.Id))
                {
                    var pv = PluginDeployer.PreservedVersion(it.Id);
                    var mv = dp?.Manifest?.Version;
                    if (!string.IsNullOrEmpty(pv) && !string.IsNullOrEmpty(mv) && pv != mv)
                        warnings.Add($"• «{name}»: сейв развёрнут из v{pv}, плагин сейчас v{mv}");
                }
            }

            // диалог нужен, если есть деструктив ИЛИ отказы гейта (чтобы не молчать)
            if (destructive.Count > 0 || refusals.Count > 0)
            {
                var msg = string.Empty;
                if (destructive.Count > 0)
                    msg += "Деструктивные операции:\n" + string.Join("\n", destructive) + "\n\n";
                if (refusals.Count > 0)
                    msg += "НЕ будут развёрнуты (зависимости не удовлетворены):\n" + string.Join("\n", refusals) + "\n\n";
                if (warnings.Count > 0)
                    msg += "Предупреждения о версиях:\n" + string.Join("\n", warnings) + "\n\n";
                msg += "Продолжить?";

                if (!EditorUtility.DisplayDialog("Nexus — применить", msg.TrimEnd(), "Применить", "Отмена"))
                    return;
            }

            var report = new List<string>();
            report.AddRange(refusals.Select(r => "отказано: " + r.TrimStart('•', ' ')));
            report.AddRange(localNotes);

            // сначала выключения (освобождаем), затем включения (занимаем)
            foreach (var it in undeploys)
            {
                // вето страницы (несохранённое?) — отложить выключение
                if (!PageHostRegistry.CanClose(it.Id))
                {
                    report.Add($"отложено: «{it.Id}» не готова к выключению (есть несохранённое)");
                    continue;
                }
                // последний шанс сбросить данные перед упаковкой/рекомпилом
                PageHostRegistry.PrePack(it.Id);
                report.Add(PluginDeployer.Undeploy(it.Id).Message);
            }
            foreach (var it in okDeploys)
            {
                byId.TryGetValue(it.Id, out var dp);
                report.Add(PluginDeployer.Deploy(dp?.Manifest).Message);
            }

            // двухфазная установка: прописать недостающие UPM-пакеты, отложить деплой страниц
            var triggerResolve = false;
            if (needsPackages.Count > 0)
            {
                var lines = needsPackages.Select(dp =>
                {
                    var pk = string.Join(", ", DependencyGate.MissingPackages(dp.Manifest)
                        .Select(p => p.Name));
                    return $"• «{dp.DisplayName}» → {pk}";
                });

                var install = EditorUtility.DisplayDialog(
                    "Nexus — нужны UPM-пакеты",
                    "Этим страницам нужны внешние пакеты, которых нет в проекте:\n\n" +
                    string.Join("\n", lines) + "\n\n" +
                    "Прописать их в Packages/manifest.json и установить? Когда пакет подтянется " +
                    "(рекомпил), страница развернётся автоматически — это отдельный второй шаг.",
                    "Установить и развернуть", "Пропустить");

                if (install)
                {
                    var toEnqueue = new List<string>();
                    foreach (var dp in needsPackages)
                    {
                        var conflict = false;
                        foreach (var p in DependencyGate.MissingPackages(dp.Manifest))
                        {
                            var res = PackageRegistry.Add(p.Name, PackageRegistry.GitValue(p));
                            if (res == PackageRegistry.AddResult.Conflict)
                            {
                                conflict = true;
                                report.Add($"конфликт пакета «{p.Name}» для «{dp.DisplayName}» — уже стоит другой пин, пропущено");
                            }
                            else if (res == PackageRegistry.AddResult.Added)
                            {
                                report.Add($"пакет запрошен: {p.Name} ({PackageRegistry.GitValue(p)})");
                            }
                        }
                        if (!conflict) toEnqueue.Add(dp.Id);
                    }

                    if (toEnqueue.Count > 0)
                    {
                        PackageDeployContinuation.Enqueue(toEnqueue);
                        triggerResolve = true;
                        report.Add("после установки пакетов будут развёрнуты: " + string.Join(", ", toEnqueue));
                    }
                }
                else
                {
                    foreach (var dp in needsPackages)
                        report.Add($"пропущено: «{dp.DisplayName}» — нужны UPM-пакеты (не установлены)");
                }
            }

            Finish(report);
            if (triggerResolve) Client.Resolve();   // применить правки Packages/manifest.json → пакет тянется → reload → фаза 2
        }

        /// <summary>Сброс страницы к дефолту — деструктивно (удаляет сейв).</summary>
        public static void RestoreDefault(string id)
        {
            var dp = Find(id);
            if (dp?.Manifest == null) return;

            var ok = EditorUtility.DisplayDialog(
                "Nexus — сбросить к дефолту",
                $"«{dp.DisplayName}» будет развёрнут заново из дистрибутива.\n\n" +
                "ВАШ СЕЙВ В PRESERVE БУДЕТ УДАЛЁН — текущие правки потеряются.",
                "Сбросить", "Отмена");
            if (!ok) return;

            Finish(new List<string> { PluginDeployer.RestoreDefault(dp.Manifest).Message });
        }

        /// <summary>Очистка осиротевшего состояния — деструктивно.</summary>
        public static void CleanOrphan(string id)
        {
            var ok = EditorUtility.DisplayDialog(
                "Nexus — удалить осиротевшее состояние",
                $"Папка состояния «{id}» не имеет манифеста (плагин удалён/не подтянулся).\n\n" +
                "Удалить её? Действие необратимо.",
                "Удалить", "Отмена");
            if (!ok) return;

            Finish(new List<string> { PluginDeployer.CleanOrphan(id).Message });
        }

        /// <summary>Есть ли что очищать в персональных настройках страницы.</summary>
        public static bool HasUserSettings(string id) => PluginDeployer.HasUserSettings(id);

        // ---- доп. пути сканирования плагинов (проектный конфиг) ----

        public static List<string> GetScanPaths() => NexusConfigStore.Load().ScanPaths;

        /// <summary>Добавить корень сканирования по АБСОЛЮТНОМУ пути; false — если вне проекта.</summary>
        public static bool AddScanPath(string absoluteFolder)
        {
            var rel = NexusPaths.ToProjectRelative(absoluteFolder);
            if (rel == null || rel.Length == 0) return false;
            NexusConfigStore.AddScanPath(rel);
            return true;
        }

        public static void RemoveScanPath(string projectRelative) => NexusConfigStore.RemoveScanPath(projectRelative);

        /// <summary>Затереть персональные настройки/кэш страницы (UserSettings/&lt;id&gt;) —
        /// деструктивно и ОТДЕЛЬНО от Restore (тот префы не трогает). Файлы вне Assets,
        /// поэтому без AssetDatabase.Refresh и без domain reload.</summary>
        public static void ClearUserSettings(string id)
        {
            var name = Find(id)?.DisplayName ?? id;
            var ok = EditorUtility.DisplayDialog(
                "Nexus — сбросить кэш страницы",
                $"Будут удалены персональные настройки/кэш «{name}»\n(UserSettings/Exerussus.Nexus/{id}).\n\n" +
                "Это данные страницы вне проекта (сейвы вкладок, фильтры и т.п.). Код и развёртка " +
                "НЕ затрагиваются. Действие необратимо.",
                "Сбросить", "Отмена");
            if (!ok) return;

            Debug.Log("[Nexus] " + PluginDeployer.ClearUserSettings(id).Message);
            // без Refresh: UserSettings вне Assets, перезагрузки нет — окно само перерисуется
        }

        private static void Finish(List<string> report)
        {
            SessionState.SetString(ApplyReportSink.ReportKey, string.Join("\n", report));
            AssetDatabase.Refresh();
        }

        private static DiscoveredPlugin Find(string id)
            => PluginDiscovery.Discover().FirstOrDefault(d => d.Id == id);
    }
}
