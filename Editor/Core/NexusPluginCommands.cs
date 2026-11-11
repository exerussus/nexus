using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;
using UnityEditor;
using UnityEditor.PackageManager;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Команды управления плагинами — то же, что окно Manage, но без мыши: список и проверка, развёртка и выключение,
    /// сброс к дефолту, чистка сирот и настроек, корни плагинов.
    /// </summary>
    /// <remarks>
    /// Конвейер общий с Manage (<see cref="ApplyService.Plan"/> → <see cref="ApplyService.Execute"/>), отличается
    /// только подтверждение: вместо диалога — флаг <c>--yes</c>. Файловые операции и Refresh идут в тике задания
    /// <c>plugins</c> после ответа клиенту: удаление State и перекомпиляция перезагружают домен, ответ команды
    /// потерялся бы вместе с соединением. Итог задания — отчёт развёртки + результат компиляции, как у refresh.
    /// </remarks>
    [InitializeOnLoad]
    internal static class NexusPluginCommands
    {
        private const string Kind = "plugins";

        static NexusPluginCommands()
        {
            NexusCliJobs.Register(Kind, Step);
        }

        // ================================================================ чтение

        [NexusCommand("plugin.list", "Плагины: статус, версии, сейв, настройки, видимость, проблемы развёртки",
            Usage = "plugin.list [--status deployed|available|orphaned|faulted]", ReadOnly = true)]
        private static int List(NexusCommandContext ctx)
        {
            var filter = ctx.Get("status");
            var rows = new List<object>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var p in PluginDiscovery.Discover())
            {
                var status = StatusOf(p);
                counts[status] = counts.TryGetValue(status, out var c) ? c + 1 : 1;
                if (filter != null && !string.Equals(filter, status, StringComparison.OrdinalIgnoreCase)) continue;

                var m = p.Manifest;
                var deployed = DeployedVersion(p.Id);
                var preserve = PluginDeployer.HasPreserve(p.Id) ? PluginDeployer.PreservedVersion(p.Id) ?? "?" : null;
                var settings = PluginDeployer.HasUserSettings(p.Id);
                var visible = NexusView.IsVisible(p.Id);
                var gate = m != null && p.Status != PluginStatus.Deployed ? DependencyGate.Check(m) : new List<string>();
                var missing = m != null && p.Status != PluginStatus.Deployed
                    ? DependencyGate.MissingPackages(m).Select(x => x.Name).ToList()
                    : new List<string>();

                var line = $"{Mark(status)} {p.Id,-22} {p.DisplayName}";
                if (m != null) line += $"  v{m.Version}";
                if (deployed != null && m != null && deployed != m.Version) line += $" · State из v{deployed}";
                if (preserve != null) line += $" · сейв v{preserve}";
                if (settings) line += " · настройки";
                if (p.Status == PluginStatus.Deployed && !visible) line += " · скрыта";
                if (gate.Count > 0) line += " · gate: " + gate[0] + (gate.Count > 1 ? $" (+{gate.Count - 1})" : string.Empty);
                if (missing.Count > 0) line += " · нужны пакеты: " + string.Join(", ", missing);
                if (status == "faulted") line += " · сбой: " + NexusCliText.OneLine(PageHostRegistry.ErrorOf(p.Id), 160);
                ctx.Print(line);

                rows.Add(new
                {
                    id = p.Id, name = p.DisplayName, status, category = p.Category, kind = m?.Kind,
                    version = m?.Version, deployedVersion = deployed, preserveVersion = preserve,
                    settings, visible, gate, missingPackages = missing,
                });
            }

            if (rows.Count == 0) ctx.Print(filter == null ? "Плагинов нет." : $"Плагинов со статусом «{filter}» нет.");
            ctx.Print(string.Join(" · ", counts.Select(kv => $"{kv.Key}: {kv.Value}")) + " — подробно: plugin.info <id>");
            ctx.Data = new { plugins = rows, counts };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("plugin.info", "Плагин подробно: манифест, где лежат исходник, State, сейв и настройки, зависимости",
            Usage = "plugin.info <id|имя>", ReadOnly = true)]
        private static int Info(NexusCommandContext ctx)
        {
            var p = One(ctx, PluginDiscovery.Discover());
            var m = p.Manifest;
            var status = StatusOf(p);
            ctx.Print($"{p.DisplayName} ({p.Id}) — {StatusText(status)}");

            var stateDir = NexusPaths.StateDir(p.Id);
            var stateFiles = Directory.Exists(stateDir) ? Directory.GetFiles(stateDir, "*", SearchOption.AllDirectories).Length : 0;
            var deployed = DeployedVersion(p.Id);
            var preserve = PluginDeployer.HasPreserve(p.Id) ? PluginDeployer.PreservedVersion(p.Id) ?? "?" : null;
            var settings = PluginDeployer.HasUserSettings(p.Id);
            var source = m != null ? PluginRoots.SourceDir(p.Id) : null;

            if (m != null)
            {
                ctx.Print($"guid {m.Guid} · v{m.Version} · {m.Kind ?? "constitutive"} · {p.Category}/{p.Order}" +
                          (string.IsNullOrEmpty(m.TypeName) ? string.Empty : " · " + m.TypeName));
                ctx.Print("исходник: " + Rel(source) + (PluginMedia.HasReadme(p.Id) ? " (README)" : string.Empty));
            }
            else
            {
                ctx.Warn("манифеста нет — плагин удалён или его корень пропал (plugin.roots); State можно убрать: plugin.clean " + p.Id);
            }

            ctx.Print("State: " + (Directory.Exists(stateDir) ? $"{Rel(stateDir)} (v{deployed ?? "?"}, файлов: {stateFiles})" : "нет"));
            ctx.Print("сейв: " + (preserve != null ? $"{Rel(NexusPaths.PreserveDir(p.Id))} (v{preserve})" : "нет"));
            ctx.Print("настройки: " + (settings ? Rel(NexusPaths.UserDir(p.Id)) : "нет"));
            if (p.Status == PluginStatus.Deployed) ctx.Print("в сайдбаре: " + (NexusView.IsVisible(p.Id) ? "видна" : "скрыта (page.show " + p.Id + ")"));
            if (status == "faulted") ctx.Error("сбой страницы: " + NexusCliText.OneLine(PageHostRegistry.ErrorOf(p.Id), 160));

            var deploy = new List<object>();
            var requires = new List<object>();
            var packages = new List<object>();
            var issues = new List<string>();
            if (m != null)
            {
                foreach (var e in m.Deploy ?? new List<DeployEntry>())
                {
                    ctx.Print($"  развёртка: {e.From} → {e.To}");
                    deploy.Add(new { from = e.From, to = e.To });
                }

                var gate = DependencyGate.Check(m);
                foreach (var r in m.Requires ?? new List<ServiceRequirement>())
                {
                    var svc = ServiceRegistry.Find(r.Service);
                    var have = svc != null && svc.Present ? svc.EffectiveVersion : null;
                    ctx.Print($"  сервис {r.Service} {r.Range}: {(have != null ? "v" + have : "нет")}");
                    requires.Add(new { service = r.Service, range = r.Range, present = have });
                }

                foreach (var g in gate) ctx.Warn("gate: " + g);

                foreach (var d in m.PackageRequires ?? new List<PackageDependency>())
                {
                    var where = PackageRegistry.IsResolved(d.Name) ? "установлен"
                        : PackageRegistry.Has(d.Name) ? "прописан, не разрешён"
                        : PackageRegistry.LocalSource(d) is string src ? "в проекте: " + src
                        : "нет — plugin.deploy --packages";
                    ctx.Print($"  пакет {d.Name}: {where}");
                    packages.Add(new { name = d.Name, gitUrl = d.GitUrl, assembly = d.Assembly, state = where });
                }

                issues = Problems(p.Id, m);
                foreach (var i in issues) ctx.Error(i);
                if (issues.Count == 0) ctx.Print("проверка: ок");
            }

            ctx.Data = new
            {
                id = p.Id, name = p.DisplayName, status, guid = m?.Guid, version = m?.Version, kind = m?.Kind,
                category = p.Category, order = p.Order, typeName = m?.TypeName,
                source = Rel(source), state = Directory.Exists(stateDir) ? Rel(stateDir) : null, deployedVersion = deployed,
                preserveVersion = preserve, settings = settings ? Rel(NexusPaths.UserDir(p.Id)) : null,
                visible = NexusView.IsVisible(p.Id), deploy, requires, packages, issues,
                fault = status == "faulted" ? PageHostRegistry.ErrorOf(p.Id) : null,
            };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("plugin.validate", "Проверить плагины: инертность исходников, маппинг развёртки, id, дубли в корнях",
            Usage = "plugin.validate [id|имя …]", ReadOnly = true)]
        private static int Validate(NexusCommandContext ctx)
        {
            var all = PluginDiscovery.Discover();
            var targets = Queries(ctx).Count == 0
                ? all.Where(p => p.Manifest != null).ToList()
                : Resolve(Queries(ctx), all);

            var bad = 0;
            var result = new List<object>();
            foreach (var p in targets)
            {
                if (p.Manifest == null)
                {
                    ctx.Warn($"{p.Id}: манифеста нет (осиротевшее состояние) — проверять нечего");
                    continue;
                }

                var issues = Problems(p.Id, p.Manifest);
                var warnings = DependencyGate.Check(p.Manifest);
                if (issues.Count == 0) ctx.Print($"✓ {p.Id}" + (warnings.Count > 0 ? " — " + string.Join("; ", warnings) : string.Empty));
                else
                {
                    bad++;
                    ctx.Error($"{p.Id}: {string.Join("; ", issues)}");
                }

                result.Add(new { id = p.Id, issues, gate = warnings });
            }

            // один id в нескольких корнях: дискавери молча берёт первый — здесь это видно
            foreach (var dup in DuplicateIds())
            {
                ctx.Warn($"id «{dup.Key}» есть в нескольких корнях, используется первый: {string.Join(", ", dup.Value)}");
            }

            ctx.Print(bad == 0 ? $"Проблем нет (проверено: {result.Count})." : $"С проблемами: {bad} из {result.Count}.");
            ctx.Data = new { plugins = result, failed = bad };
            return bad == 0 ? NexusCommandContext.Exit.Ok : NexusCommandContext.Exit.Failed;
        }

        // ================================================================ развёртка

        [NexusCommand("plugin.deploy", "Развернуть плагины (одна перекомпиляция); --packages — доставить недостающие UPM-пакеты",
            Usage = "plugin.deploy <id|имя …> [--packages] [--timeout 600]")]
        private static int Deploy(NexusCommandContext ctx) => StartApply(ctx, Queries(ctx), new List<string>());

        [NexusCommand("plugin.undeploy", "Выключить плагины: сейв уходит в Preserve, код выгружается (нужен --yes)",
            Usage = "plugin.undeploy <id|имя …> --yes [--timeout 600]")]
        private static int Undeploy(NexusCommandContext ctx) => StartApply(ctx, new List<string>(), Queries(ctx));

        [NexusCommand("plugin.apply", "Развернуть и выключить за одну перекомпиляцию (выключение — с --yes)",
            Usage = "plugin.apply [--deploy a,b] [--undeploy c] [--yes] [--packages] [--timeout 600]")]
        private static int Apply(NexusCommandContext ctx) => StartApply(ctx, ctx.GetList("deploy"), ctx.GetList("undeploy"));

        [NexusCommand("plugin.restore", "Сбросить плагин к дистрибутиву: State и сейв удаляются, настройки остаются (нужен --yes)",
            Usage = "plugin.restore <id|имя> --yes [--timeout 600]")]
        private static int Restore(NexusCommandContext ctx)
        {
            Ready(ctx);
            var p = One(ctx, PluginDiscovery.Discover());
            if (p.Manifest == null)
                throw new NexusCommandException($"«{p.Id}» без манифеста — сбрасывать не к чему; убрать State: plugin.clean {p.Id} --yes", NexusCommandContext.Exit.Failed);

            var gate = DependencyGate.Check(p.Manifest);
            if (gate.Count > 0)
                throw new NexusCommandException($"«{p.DisplayName}» не развернётся: {string.Join("; ", gate)}", NexusCommandContext.Exit.Failed);

            ctx.Print($"«{p.DisplayName}» будет развёрнут заново из дистрибутива (v{p.Manifest.Version}).");
            if (Directory.Exists(NexusPaths.StateDir(p.Id)) || PluginDeployer.HasPreserve(p.Id))
                ctx.Warn("правки в State и сейв в Preserve будут удалены; настройки (UserSettings) останутся");
            if (!Confirmed(ctx)) return NexusCommandContext.Exit.Failed;

            Begin(ctx, "restore").Set("id", p.Id);
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("plugin.clean", "Удалить осиротевшее состояние (State без манифеста) и его сейв (нужен --yes)",
            Usage = "plugin.clean <id> --yes [--timeout 600]")]
        private static int Clean(NexusCommandContext ctx)
        {
            Ready(ctx);
            var p = One(ctx, PluginDiscovery.Discover());
            if (p.Status != PluginStatus.OrphanedState)
                throw new NexusCommandException($"«{p.Id}» не сирота (манифест есть): выключить — plugin.undeploy, сбросить — plugin.restore",
                    NexusCommandContext.Exit.Failed);

            ctx.Warn($"будут удалены {Rel(NexusPaths.StateDir(p.Id))}" +
                     (PluginDeployer.HasPreserve(p.Id) ? " и " + Rel(NexusPaths.PreserveDir(p.Id)) : string.Empty) + " — необратимо");
            if (!Confirmed(ctx)) return NexusCommandContext.Exit.Failed;

            Begin(ctx, "clean").Set("id", p.Id);
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("plugin.settings.clear", "Удалить персональные настройки и кэш страницы (UserSettings/<id>), без перекомпиляции (нужен --yes)",
            Usage = "plugin.settings.clear <id|имя> --yes")]
        private static int ClearSettings(NexusCommandContext ctx)
        {
            var p = One(ctx, PluginDiscovery.Discover());
            if (!PluginDeployer.HasUserSettings(p.Id))
            {
                ctx.Print($"У «{p.DisplayName}» нет персональных настроек.");
                return NexusCommandContext.Exit.Ok;
            }

            ctx.Warn($"будет удалена папка {Rel(NexusPaths.UserDir(p.Id))} — код, State и сейв не затрагиваются");
            if (!Confirmed(ctx)) return NexusCommandContext.Exit.Failed;

            var r = PluginDeployer.ClearUserSettings(p.Id);
            if (!r.Ok) throw new NexusCommandException(r.Message, NexusCommandContext.Exit.Failed);
            ctx.Print("✓ " + r.Message);
            ctx.Data = new { id = p.Id };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ корни плагинов

        [NexusCommand("plugin.roots", "Корни плагинов: встроенный, ручные пути, найденные по маркеру; сколько в каждом плагинов",
            Usage = "plugin.roots", ReadOnly = true)]
        private static int Roots(NexusCommandContext ctx)
        {
            var rows = new List<object>();
            foreach (var r in PluginRoots.Describe())
            {
                var kind = r.Source == PluginRootSource.BuiltIn ? "встроен" : r.Source == PluginRootSource.ScanPath ? "путь" : "маркер";
                var count = r.Exists ? CountPlugins(r.AbsPath) : 0;
                var line = $"{kind,-8} {r.Display}";
                if (!string.IsNullOrEmpty(r.Name)) line += $" «{r.Name}»";
                line += !r.Exists ? " — нет на диске" : r.Duplicate ? " — дубль, пропущен" : $" — плагинов: {count}";
                if (r.Exists && !r.Duplicate) ctx.Print(line);
                else ctx.Warn(line);
                rows.Add(new { kind = r.Source.ToString(), path = r.Display, name = r.Name, exists = r.Exists, duplicate = r.Duplicate, plugins = count });
            }

            ctx.Print("добавить: plugin.roots.add <папка> · по маркеру nexus-plugins.json: plugin.roots.scan");
            ctx.Data = new { roots = rows };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("plugin.roots.add", "Добавить ручной путь поиска плагинов (папка внутри проекта; пишется в ProjectSettings/Nexus.json)",
            Usage = "plugin.roots.add <папка: от корня проекта, абсолютная или от текущей>")]
        private static int RootsAdd(NexusCommandContext ctx)
        {
            var arg = ctx.Arg(0) ?? throw new NexusCommandException("нужна папка: plugin.roots.add Assets/Tools/NexusPlugins");
            var abs = FindFolder(ctx, arg)
                      ?? throw new NexusCommandException($"папка не найдена: {arg}", NexusCommandContext.Exit.Failed);
            var rel = NexusPaths.ToProjectRelative(abs);
            if (string.IsNullOrEmpty(rel))
                throw new NexusCommandException($"папка вне проекта: {abs}", NexusCommandContext.Exit.Failed);

            if (ApplyService.GetScanPaths().Any(x => SamePath(x, rel)))
            {
                ctx.Print($"Путь уже есть: {rel}");
                ctx.Data = new { path = rel, added = false };
                return NexusCommandContext.Exit.Ok;
            }

            ApplyService.AddScanPath(abs);
            var count = CountPlugins(abs);
            ctx.Print($"✓ путь добавлен: {rel} (плагинов: {count}) — plugin.list");
            if (count == 0) ctx.Warn("в папке нет ни одного <id>/manifest.json");
            if (rel.StartsWith("Library/", StringComparison.Ordinal))
                ctx.Warn("папка внутри Library (кэш пакетов меняется при обновлении) — для пакетов надёжнее маркер: plugin.roots.scan");
            ctx.Data = new { path = rel, added = true, plugins = count };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("plugin.roots.remove", "Убрать ручной путь поиска плагинов",
            Usage = "plugin.roots.remove <путь, как в plugin.roots>")]
        private static int RootsRemove(NexusCommandContext ctx)
        {
            var arg = ctx.Arg(0) ?? throw new NexusCommandException("нужен путь: plugin.roots.remove <путь> (список — plugin.roots)");
            var rel = Path.IsPathRooted(arg) ? NexusPaths.ToProjectRelative(arg) ?? arg : arg;
            var entry = ApplyService.GetScanPaths().FirstOrDefault(x => SamePath(x, rel));
            if (entry == null)
            {
                var marker = PluginRoots.Describe().FirstOrDefault(r => r.Source == PluginRootSource.Marker && SamePath(r.Display, rel));
                if (marker != null)
                    throw new NexusCommandException($"«{marker.Display}» найден по маркеру — уберите из папки nexus-plugins.json и выполните plugin.roots.scan",
                        NexusCommandContext.Exit.Failed);
                throw new NexusCommandException($"ручного пути «{arg}» нет; есть: {string.Join(", ", ApplyService.GetScanPaths().DefaultIfEmpty("—"))}",
                    NexusCommandContext.Exit.Failed);
            }

            // развёрнутые плагины этого корня станут сиротами (их State останется)
            var abs = Path.Combine(NexusPaths.ProjectRoot, entry);
            var affected = PluginDiscovery.Discover()
                .Where(p => p.Status == PluginStatus.Deployed && Under(PluginRoots.SourceDir(p.Id), abs))
                .Select(p => p.Id).ToList();

            ApplyService.RemoveScanPath(entry);
            ctx.Print("✓ путь убран: " + entry);
            if (affected.Count > 0)
                ctx.Warn($"развёрнутые плагины этого корня станут осиротевшими (State остался): {string.Join(", ", affected)} — " +
                         "верните путь или plugin.clean <id> --yes");
            ctx.Data = new { path = entry, orphaned = affected };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("plugin.roots.scan", "Найти корни плагинов по маркеру nexus-plugins.json в Assets и пакетах",
            Usage = "plugin.roots.scan")]
        private static int RootsScan(NexusCommandContext ctx)
        {
            var r = ApplyService.ScanPluginRoots();
            ctx.Print(r.Summary());
            foreach (var a in r.Added) ctx.Print("  + " + a);
            foreach (var x in r.Removed) ctx.Print("  − " + x);
            ctx.Data = new { found = r.Found, added = r.Added, removed = r.Removed, warnings = r.Warnings, changed = r.ConfigChanged };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ запуск задания

        private static int StartApply(NexusCommandContext ctx, List<string> deployQueries, List<string> undeployQueries)
        {
            Ready(ctx);
            if (deployQueries.Count == 0 && undeployQueries.Count == 0)
                throw new NexusCommandException("нужен хотя бы один плагин (plugin.list)");

            var all = PluginDiscovery.Discover();
            var intents = new List<PendingIntent>();
            foreach (var p in Resolve(deployQueries, all))
            {
                if (p.Status == PluginStatus.OrphanedState)
                    throw new NexusCommandException($"«{p.Id}» без манифеста — развернуть нечего (plugin.clean {p.Id} --yes)", NexusCommandContext.Exit.Failed);
                if (p.Status == PluginStatus.Deployed) ctx.Print($"· «{p.DisplayName}» уже развёрнут");
                else intents.Add(new PendingIntent(p.Id, IntentKind.Deploy));
            }

            foreach (var p in Resolve(undeployQueries, all))
            {
                if (p.Status == PluginStatus.OrphanedState)
                    throw new NexusCommandException($"«{p.Id}» — осиротевшее состояние: plugin.clean {p.Id} --yes", NexusCommandContext.Exit.Failed);
                if (p.Status == PluginStatus.Available) ctx.Print($"· «{p.DisplayName}» не развёрнут");
                else intents.Add(new PendingIntent(p.Id, IntentKind.Undeploy));
            }

            if (intents.Count == 0)
            {
                ctx.Print("Применять нечего.");
                return NexusCommandContext.Exit.Ok;
            }

            var plan = ApplyService.Plan(intents);
            var summary = ApplyService.Summary(plan);
            if (summary.Length > 0) foreach (var line in summary.Split('\n')) ctx.Print(line);

            var packages = ctx.Flag("packages");
            if (plan.NeedsPackages.Count > 0)
            {
                ctx.Print("Нужны UPM-пакеты, которых нет в проекте:");
                foreach (var line in ApplyService.PackageLines(plan)) ctx.Print(line);
                if (!packages) ctx.Warn("не установлены — добавьте --packages: пропишет их в Packages/manifest.json и развернёт страницы после установки (две перекомпиляции)");
            }

            if (plan.Undeploys.Count > 0 && !Confirmed(ctx)) return NexusCommandContext.Exit.Failed;

            if (plan.Deploys.Count == 0 && plan.Undeploys.Count == 0 && (plan.NeedsPackages.Count == 0 || !packages))
            {
                ctx.Error("Применять нечего: всё отклонено.");
                return NexusCommandContext.Exit.Failed;
            }

            var job = Begin(ctx, "apply", packages && plan.NeedsPackages.Count > 0 ? 900 : 600);
            job.Set("deploy", string.Join(",", intents.Where(i => i.Kind == IntentKind.Deploy).Select(i => i.Id)));
            job.Set("undeploy", string.Join(",", intents.Where(i => i.Kind == IntentKind.Undeploy).Select(i => i.Id)));
            if (packages) job.Set("packages", "1");
            return NexusCommandContext.Exit.Ok;
        }

        private static NexusCliJob Begin(NexusCommandContext ctx, string todo, int timeout = 600)
        {
            var job = NexusCliJobs.Start(Kind, ctx.GetInt("timeout", timeout), ctx);
            job.Set("todo", todo);
            job.Set("gen", NexusCompileLog.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
            job.Set("maxErrors", "20");
            NexusCliJobs.SetPhase(job, "ожидание");
            return job;
        }

        /// <summary>Не трогать плагины в Play, во время другой операции редактора и пока он компилирует.</summary>
        private static void Ready(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new NexusCommandException("Идёт Play — остановите: play.stop", NexusCommandContext.Exit.Failed);
        }

        private static bool Confirmed(NexusCommandContext ctx)
        {
            if (ctx.Flag("yes")) return true;
            ctx.Error("операция деструктивная — повторите с --yes");
            return false;
        }

        // ================================================================ тик задания

        private static void Step(NexusCliJob job)
        {
            var todo = job.Get("todo");
            if (todo != null)
            {
                // пауза — чтобы ответ команды успел уйти клиенту до перезагрузки домена
                if (NexusCliJobs.Now - job.StartedAt < 0.25) return;
                job.Params.Remove("todo");
                NexusCliJobs.SetPhase(job, "файлы плагинов");

                var resolve = false;
                try
                {
                    resolve = Run(job, todo);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Error("CLI: plugins", e);
                    Report(job, "✗ сбой: " + e.Message, failed: true);
                }

                NexusCliJobs.SetPhase(job, "импорт и компиляция");
                NexusCliJobs.Touch(job);
                NexusCliJobs.Save();
                AssetDatabase.Refresh();
                if (resolve) Client.Resolve();
                job.Anchor = NexusCliJobs.Now;
                return;
            }

            if (!string.IsNullOrEmpty(job.Get("await")))
            {
                AwaitPackages(job);
                return;
            }

            NexusEditorCommands.Settle(job);
        }

        /// <summary>Файловая часть по виду операции; true — нужен Client.Resolve.</summary>
        private static bool Run(NexusCliJob job, string todo)
        {
            switch (todo)
            {
                case "apply":
                {
                    var intents = Split(job.Get("deploy")).Select(id => new PendingIntent(id, IntentKind.Deploy))
                        .Concat(Split(job.Get("undeploy")).Select(id => new PendingIntent(id, IntentKind.Undeploy)))
                        .ToList();
                    var packages = job.Get("packages") == "1";
                    var plan = ApplyService.Plan(intents);
                    var outcome = ApplyService.Execute(plan, packages);

                    var failed = outcome.Failed.Count > 0 || outcome.Vetoed.Count > 0 || plan.Refusals.Count > 0
                                 || plan.NeedsPackages.Count > 0 && !packages;
                    foreach (var line in outcome.Report) Report(job, line);
                    if (failed) job.Set("failed", "1");
                    if (outcome.AwaitingPackages.Count > 0) job.Set("await", string.Join(",", outcome.AwaitingPackages));
                    return outcome.ResolvePackages;
                }
                case "restore":
                {
                    var id = job.Get("id");
                    var m = PluginDiscovery.Discover().FirstOrDefault(d => d.Id == id)?.Manifest;
                    var r = m != null ? PluginDeployer.RestoreDefault(m) : PluginDeployer.Result.Fail($"манифест «{id}» пропал");
                    Report(job, r.Ok ? r.Message : $"ошибка «{id}»: {r.Message}", !r.Ok);
                    return false;
                }
                case "clean":
                {
                    var r = PluginDeployer.CleanOrphan(job.Get("id"));
                    Report(job, r.Message, !r.Ok);
                    return false;
                }
                default:
                    Report(job, "неизвестная операция: " + todo, failed: true);
                    return false;
            }
        }

        /// <summary>Двухфазная установка: ждать, пока PackageDeployContinuation развернёт страницы после пакетов.</summary>
        private static void AwaitPackages(NexusCliJob job)
        {
            // проверка раз в секунду и только в тишине: дискавери читает диск
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || NexusCliJobs.Now - job.Anchor < 1.0) return;
            job.Anchor = NexusCliJobs.Now;

            var ids = Split(job.Get("await"));
            var byId = PluginDiscovery.Discover().ToDictionary(d => d.Id);
            var pending = PackageDeployContinuation.Pending();
            var waiting = new List<string>();
            var lost = new List<string>();
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var p) && p.Status == PluginStatus.Deployed) continue;
                if (pending.Contains(id)) waiting.Add(id);
                else lost.Add(id);
            }

            if (waiting.Count > 0)
            {
                NexusCliJobs.SetPhase(job, "ждёт UPM-пакеты: " + string.Join(", ", waiting));
                return;
            }

            job.Params.Remove("await");
            if (lost.Count > 0) Report(job, "не развёрнуты после установки пакетов: " + string.Join(", ", lost), failed: true);
            else Report(job, "развёрнуты после установки пакетов: " + string.Join(", ", ids));
            NexusCliJobs.SetPhase(job, "компиляция страниц");
            NexusCliJobs.Save();
        }

        private static void Report(NexusCliJob job, string line, bool failed = false)
        {
            var report = job.Get("report");
            job.Set("report", string.IsNullOrEmpty(report) ? line : report + "\n" + line);
            if (failed) job.Set("failed", "1");
        }

        // ================================================================ мелочи

        private static string StatusOf(DiscoveredPlugin p) => p.Status switch
        {
            PluginStatus.OrphanedState => "orphaned",
            PluginStatus.Available => "available",
            _ => PageHostRegistry.IsFaulted(p.Id) ? "faulted" : "deployed",
        };

        private static string StatusText(string status) => status switch
        {
            "orphaned" => "осиротевшее состояние",
            "available" => "доступен, не развёрнут",
            "faulted" => "развёрнут, страница упала",
            _ => "развёрнут",
        };

        private static string Mark(string status) => status switch
        {
            "deployed" => "●",
            "available" => "○",
            "faulted" => "✗",
            _ => "?",
        };

        private static string DeployedVersion(string id)
            => JsonIo.Load<DeployDescriptor>(Path.Combine(NexusPaths.StateDir(id), "deploy.json"))?.DeployedVersion;

        /// <summary>Проблемы самого плагина (не окружения): валидация развёртки, id, файлы вне маппинга.</summary>
        private static List<string> Problems(string id, PluginManifest m)
        {
            var issues = PluginDeployer.Validate(m);
            if (!string.IsNullOrEmpty(m.Id) && m.Id != id) issues.Add($"id в манифесте «{m.Id}» ≠ имени папки «{id}»");
            if (string.IsNullOrEmpty(m.Guid)) issues.Add("нет guid");
            if (string.IsNullOrEmpty(m.Version)) issues.Add("нет version");
            if (m.Deploy == null || m.Deploy.Count == 0) issues.Add("пустой маппинг deploy");

            var dir = PluginRoots.SourceDir(id);
            if (!Directory.Exists(dir)) return issues;

            var mapped = new HashSet<string>((m.Deploy ?? new List<DeployEntry>()).Select(e => e.From), StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (mapped.Contains(name) || IsServiceFile(name)) continue;
                issues.Add($"файл вне маппинга: {name} (не развернётся)");
            }

            return issues;
        }

        // файлы, которые хаб читает сам или которые не разворачиваются по определению
        private static bool IsServiceFile(string name)
            => name == "manifest.json" || name == "page_logo.png" || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("readme.", StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, List<string>> DuplicateIds()
        {
            var where = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var root in PluginRoots.Roots())
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
                var id = Path.GetFileName(dir);
                if (!where.TryGetValue(id, out var list)) where[id] = list = new List<string>();
                list.Add(Rel(root));
            }

            return where.Where(kv => kv.Value.Count > 1).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        private static int CountPlugins(string abs)
        {
            try
            {
                return string.IsNullOrEmpty(abs) || !Directory.Exists(abs)
                    ? 0
                    : Directory.GetDirectories(abs).Count(d => File.Exists(Path.Combine(d, "manifest.json")));
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: подсчёт плагинов в " + abs, e);
                return 0;
            }
        }

        /// <summary>Позиционные аргументы; каждый может быть списком через запятую.</summary>
        private static List<string> Queries(NexusCommandContext ctx)
            => ctx.Positional.SelectMany(a => a.Split(',', ';')).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

        private static IEnumerable<string> Split(string list)
            => string.IsNullOrEmpty(list) ? Enumerable.Empty<string>() : list.Split(',').Where(x => x.Length > 0);

        private static DiscoveredPlugin One(NexusCommandContext ctx, List<DiscoveredPlugin> all)
        {
            var q = ctx.Arg(0) ?? throw new NexusCommandException("нужен плагин: id или имя (plugin.list)");
            return Resolve(new List<string> { q }, all)[0];
        }

        /// <summary>По id (точно, затем без регистра), затем по имени; не найден — код 2 с подсказкой.</summary>
        private static List<DiscoveredPlugin> Resolve(List<string> queries, List<DiscoveredPlugin> all)
        {
            var list = new List<DiscoveredPlugin>();
            foreach (var q in queries)
            {
                var p = all.FirstOrDefault(x => x.Id == q)
                        ?? all.FirstOrDefault(x => string.Equals(x.Id, q, StringComparison.OrdinalIgnoreCase))
                        ?? all.FirstOrDefault(x => string.Equals(x.DisplayName, q, StringComparison.OrdinalIgnoreCase))
                        ?? throw new NexusCommandException($"плагин «{q}» не найден (plugin.list)");
                if (!list.Contains(p)) list.Add(p);
            }

            return list;
        }

        private static string FindFolder(NexusCommandContext ctx, string arg)
        {
            if (Path.IsPathRooted(arg)) return Directory.Exists(arg) ? Path.GetFullPath(arg) : null;
            var fromProject = Path.Combine(NexusPaths.ProjectRoot, arg);
            if (Directory.Exists(fromProject)) return Path.GetFullPath(fromProject);
            if (string.IsNullOrEmpty(ctx.WorkingDirectory)) return null;
            var fromCwd = Path.Combine(ctx.WorkingDirectory, arg);
            return Directory.Exists(fromCwd) ? Path.GetFullPath(fromCwd) : null;
        }

        private static string Norm(string path) => (path ?? string.Empty).Replace('\\', '/').Trim().TrimEnd('/');

        private static bool SamePath(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

        private static bool Under(string path, string root)
        {
            var p = Norm(Path.GetFullPath(path));
            var r = Norm(Path.GetFullPath(root));
            return p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Путь от корня проекта (как в git), иначе абсолютный.</summary>
        private static string Rel(string abs)
        {
            if (string.IsNullOrEmpty(abs)) return null;
            var rel = NexusPaths.ToProjectRelative(abs);
            return string.IsNullOrEmpty(rel) ? Norm(abs) : rel;
        }
    }
}
