using System.Collections.Generic;
using System.Linq;
using Exerussus.Nexus.Abstractions;
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
    /// Конвейер один: <see cref="Plan"/> (расчёт) → <see cref="Execute"/> (файлы). Окно Manage подтверждает
    /// диалогами, CLI — флагом --yes и выполняет в тике задания (модальные окна в командах запрещены).
    ///
    /// Правило подтверждений: тогл и развёртка — молча; выключение/restore/очистка
    /// сироты — через диалог. Деструктив батчится в ОДИН сводный диалог, а не по
    /// модалке на каждую страницу. Restore явно предупреждает об удалении сейва.
    /// </summary>
    public static class ApplyService
    {
        /// <summary>Применить набор намерений из окна Manage: сводный диалог → файлы → Refresh.</summary>
        public static void Apply(IReadOnlyList<PendingIntent> intents)
        {
            if (intents == null || intents.Count == 0) return;

            var plan = Plan(intents);

            // диалог нужен, если есть деструктив ИЛИ отказы гейта (чтобы не молчать)
            if (plan.Destructive.Count > 0 || plan.Refusals.Count > 0)
            {
                if (!EditorUtility.DisplayDialog("Nexus — применить", Summary(plan) + "\n\nПродолжить?", "Применить", "Отмена"))
                    return;
            }

            // все подтверждения — до файловых операций
            var installPackages = false;
            if (plan.NeedsPackages.Count > 0)
            {
                installPackages = EditorUtility.DisplayDialog(
                    "Nexus — нужны UPM-пакеты",
                    "Этим страницам нужны внешние пакеты, которых нет в проекте:\n\n" +
                    string.Join("\n", PackageLines(plan)) + "\n\n" +
                    "Прописать их в Packages/manifest.json и установить? Когда пакет подтянется " +
                    "(рекомпил), страница развернётся автоматически — это отдельный второй шаг.",
                    "Установить и развернуть", "Пропустить");
            }

            var outcome = Execute(plan, installPackages);
            Finish(outcome.Report);
            if (outcome.ResolvePackages) Client.Resolve();   // применить правки Packages/manifest.json → пакет тянется → reload → фаза 2
        }

        /// <summary>
        /// Расчёт без побочных эффектов: gate, недостающие пакеты, предупреждения о версиях сейва, сводка деструктива.
        /// Общий для окна Manage и CLI — один конвейер, разные способы подтверждения.
        /// </summary>
        public static ApplyPlan Plan(IReadOnlyList<PendingIntent> intents)
        {
            var plan = new ApplyPlan { ById = PluginDiscovery.Discover().ToDictionary(d => d.Id) };
            if (intents == null) return plan;

            foreach (var it in intents)
            {
                plan.ById.TryGetValue(it.Id, out var dp);
                var name = dp?.DisplayName ?? it.Id;

                if (it.Kind == IntentKind.Undeploy)
                {
                    plan.Undeploys.Add(it);
                    plan.Destructive.Add($"• выключить «{name}» — живой код выгрузится (сейв уйдёт в Preserve)");
                    continue;
                }

                // deploy: сперва dependency-gate (уровень 1). Не сходится — не разворачиваем.
                var gate = DependencyGate.Check(dp?.Manifest);
                if (gate.Count > 0)
                {
                    NexusDiagnostics.Trace("Gate", $"'{it.Id}' отклонён: {string.Join("; ", gate)}");
                    plan.Refusals.Add($"• «{name}»: {string.Join("; ", gate)}");
                    continue;
                }

                // нет нужного UPM-пакета → не деплоим сейчас, уводим в двухфазную установку
                if (dp != null && DependencyGate.MissingPackages(dp.Manifest).Count > 0)
                {
                    NexusDiagnostics.Trace("Gate",
                        $"'{it.Id}' ждёт UPM-пакеты: {string.Join(", ", DependencyGate.MissingPackages(dp.Manifest).Select(p => p.Name))}");
                    plan.NeedsPackages.Add(dp);
                    continue;
                }

                plan.Deploys.Add(it);

                // пакет, объявленный странице, но уже присутствующий в проекте (не через manifest) — отметим
                foreach (var p in dp?.Manifest?.PackageRequires ?? Enumerable.Empty<PackageDependency>())
                {
                    if (string.IsNullOrEmpty(p?.Name) || PackageRegistry.Has(p.Name)) continue;
                    var src = PackageRegistry.LocalSource(p);
                    if (src != null)
                        plan.LocalNotes.Add($"«{name}»: пакет {p.Name} уже в проекте ({src}) — git-установка не требуется");
                }

                if (PluginDeployer.HasPreserve(it.Id))
                {
                    var pv = PluginDeployer.PreservedVersion(it.Id);
                    var mv = dp?.Manifest?.Version;
                    if (!string.IsNullOrEmpty(pv) && !string.IsNullOrEmpty(mv) && pv != mv)
                        plan.Warnings.Add($"• «{name}»: сейв развёрнут из v{pv}, плагин сейчас v{mv}");
                }
            }

            return plan;
        }

        /// <summary>Текст сводки плана: деструктив, отказы, предупреждения (для диалога и вывода CLI).</summary>
        public static string Summary(ApplyPlan plan)
        {
            var msg = string.Empty;
            if (plan.Destructive.Count > 0)
                msg += "Деструктивные операции:\n" + string.Join("\n", plan.Destructive) + "\n\n";
            if (plan.Refusals.Count > 0)
                msg += "НЕ будут развёрнуты (зависимости не удовлетворены):\n" + string.Join("\n", plan.Refusals) + "\n\n";
            if (plan.Warnings.Count > 0)
                msg += "Предупреждения о версиях:\n" + string.Join("\n", plan.Warnings) + "\n\n";
            return msg.TrimEnd();
        }

        /// <summary>Строки «• «имя» → пакеты» для страниц, ждущих UPM-пакетов.</summary>
        public static IEnumerable<string> PackageLines(ApplyPlan plan)
            => plan.NeedsPackages.Select(dp =>
                $"• «{dp.DisplayName}» → {string.Join(", ", DependencyGate.MissingPackages(dp.Manifest).Select(p => p.Name))}");

        /// <summary>
        /// Файловая часть Apply по готовому плану — без диалогов и без Refresh (их делает вызывающий).
        /// <paramref name="installPackages"/> — согласие на запись недостающих пакетов в Packages/manifest.json.
        /// </summary>
        public static ApplyOutcome Execute(ApplyPlan plan, bool installPackages)
        {
            var outcome = new ApplyOutcome();
            var report  = outcome.Report;
            report.AddRange(plan.Refusals.Select(r => "отказано: " + r.TrimStart('•', ' ')));
            report.AddRange(plan.LocalNotes);

            // сначала выключения (освобождаем), затем включения (занимаем)
            foreach (var it in plan.Undeploys)
            {
                // вето страницы (несохранённое?) — отложить выключение
                if (!PageHostRegistry.CanClose(it.Id))
                {
                    outcome.Vetoed.Add(it.Id);
                    report.Add($"отложено: «{it.Id}» не готова к выключению (есть несохранённое)");
                    continue;
                }
                // последний шанс сбросить данные перед упаковкой/рекомпилом
                PageHostRegistry.PrePack(it.Id);
                Note(outcome, it.Id, PluginDeployer.Undeploy(it.Id));
            }
            foreach (var it in plan.Deploys)
            {
                plan.ById.TryGetValue(it.Id, out var dp);
                Note(outcome, it.Id, PluginDeployer.Deploy(dp?.Manifest));
            }

            // двухфазная установка: прописать недостающие UPM-пакеты, отложить деплой страниц
            if (plan.NeedsPackages.Count == 0) return outcome;

            if (!installPackages)
            {
                foreach (var dp in plan.NeedsPackages)
                    report.Add($"пропущено: «{dp.DisplayName}» — нужны UPM-пакеты (не установлены)");
                return outcome;
            }

            var toEnqueue = new List<string>();
            foreach (var dp in plan.NeedsPackages)
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

                if (conflict) outcome.Failed.Add(dp.Id);
                else toEnqueue.Add(dp.Id);
            }

            if (toEnqueue.Count > 0)
            {
                PackageDeployContinuation.Enqueue(toEnqueue);
                outcome.AwaitingPackages.AddRange(toEnqueue);
                outcome.ResolvePackages = true;
                report.Add("после установки пакетов будут развёрнуты: " + string.Join(", ", toEnqueue));
            }

            return outcome;
        }

        private static void Note(ApplyOutcome outcome, string id, PluginDeployer.Result result)
        {
            outcome.Report.Add(result.Ok ? result.Message : $"ошибка «{id}»: {result.Message}");
            if (!result.Ok) outcome.Failed.Add(id);
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

        // ---- корни плагинов по маркеру nexus-plugins.json ----

        /// <summary>Поиск корней по маркеру (только вручную, из Manage). Пишет конфиг, если
        /// что-то изменилось; перезагрузку домена не вызывает — дискавери читает диск заново.</summary>
        public static RootScanResult ScanPluginRoots() => PluginRootScanner.Scan();

        /// <summary>Все корни с диагностикой — для секции «Корни плагинов» в Manage.</summary>
        public static List<PluginRootRow> DescribePluginRoots()
            => PluginRoots.Describe().Select(i => new PluginRootRow
            {
                Kind      = i.Source == PluginRootSource.BuiltIn  ? PluginRootKind.BuiltIn
                          : i.Source == PluginRootSource.ScanPath ? PluginRootKind.ScanPath
                          : PluginRootKind.Marker,
                Path      = i.Display,
                Name      = i.Name,
                Exists    = i.Exists,
                Duplicate = i.Duplicate,
            }).ToList();

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
