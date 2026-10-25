using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Команды управления редактором: состояние, обновление и компиляция, задания, меню, ассеты.
    /// Долгие операции идут заданиями (<see cref="NexusCliJobs"/>): клиент ждёт их конца и через перезагрузку домена.
    /// </summary>
    [InitializeOnLoad]
    internal static class NexusEditorCommands
    {
        /// <summary>Сколько секунд без компиляции и импорта считать «редактор успокоился».</summary>
        private const double QuietSeconds = 1.5;

        /// <summary>Задания, которые меняют состояние редактора: одновременно идёт только одно.</summary>
        internal static readonly string[] ExclusiveKinds = { "refresh", "compile", "settle", "play.start", "play.stop", "play.step", "tests" };

        static NexusEditorCommands()
        {
            NexusCliJobs.Register("refresh", Settle);
            NexusCliJobs.Register("compile", Settle);
            NexusCliJobs.Register("settle", Settle);
        }

        // ================================================================ состояние

        [NexusCommand("status", "Состояние редактора: Play, компиляция, сцена, ошибки, задания", Usage = "status", ReadOnly = true)]
        private static int Status(NexusCommandContext ctx)
        {
            var info = NexusCliServer.Describe();
            var playing = EditorApplication.isPlaying;
            var mode = playing ? (EditorApplication.isPaused ? "play (пауза)" : "play") : EditorApplication.isPlayingOrWillChangePlaymode ? "вход в play" : "edit";
            var focused = InternalEditorUtility.isApplicationActive;
            ctx.Print($"{info.Project} · Unity {info.Unity} · {mode} · фокус: {(focused ? "да" : "нет")}");

            var active = SceneManager.GetActiveScene();
            var scenes = new List<object>();
            var sceneLine = new StringBuilder("сцена: ");
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (i > 0) sceneLine.Append(", ");
                sceneLine.Append(string.IsNullOrEmpty(s.path) ? "(без имени)" : s.path);
                if (s.isDirty) sceneLine.Append(" *");
                if (s == active && SceneManager.sceneCount > 1) sceneLine.Append(" [активна]");
                scenes.Add(new { path = s.path, name = s.name, dirty = s.isDirty, loaded = s.isLoaded, active = s == active });
            }

            ctx.Print(sceneLine.ToString());

            var compiling = EditorApplication.isCompiling;
            var updating = EditorApplication.isUpdating;
            var failed = EditorUtility.scriptCompilationFailed;
            var compile = NexusCompileLog.Last;
            var compileLine = compiling ? "идёт" : failed ? "ошибки" + (compile != null ? $" ({compile.ErrorCount}) — compile.errors" : " — compile.errors") : "ок";
            if (!compiling && compile != null && compile.Finished > 0) compileLine += " · последняя " + NexusCliText.Clock(compile.Finished);
            ctx.Print($"компиляция: {compileLine} · импорт: {(updating ? "идёт" : "нет")}");

            NexusLogBuffer.Count(0, out var errors, out var warnings, out var logs);
            var play = NexusLogBuffer.PlaySeq;
            var logLine = $"лог: {NexusCliText.Plural(errors, "ошибка", "ошибки", "ошибок")}, {NexusCliText.Plural(warnings, "предупреждение", "предупреждения", "предупреждений")}";
            if (errors > 0) logLine += " — logs --errors";
            ctx.Print(logLine);

            ctx.Print(NexusCliDaemon.Describe());

            var jobs = new List<object>();
            foreach (var j in NexusCliJobs.All)
            {
                if (j.Done) continue;
                ctx.Print($"задание {j.Id}: {j.Kind} — {j.Phase} ({NexusCliText.Seconds(NexusCliJobs.Now - j.StartedAt)})");
                jobs.Add(new { id = j.Id, kind = j.Kind, phase = j.Phase });
            }

            ctx.Data = new
            {
                project = info.Project, path = info.Path, unity = info.Unity, pid = info.Pid,
                playing, paused = EditorApplication.isPaused, focused,
                compiling, updating, compileFailed = failed, compileErrors = compile?.ErrorCount ?? (failed ? -1 : 0),
                logErrors = errors, logWarnings = warnings, logLast = NexusLogBuffer.LastSeq, logPlaySeq = play,
                scenes, jobs,
            };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ обновление и компиляция

        [NexusCommand("refresh", "Обновить ассеты и дождаться импорта, компиляции и перезагрузки домена; итог — ошибки компиляции",
            Usage = "refresh [--force] [--compile] [--max-errors 20] [--timeout 600]")]
        private static int Refresh(NexusCommandContext ctx)
        {
            Guard(ctx);
            var generation = NexusCompileLog.Generation;

            // сам Refresh — в задании, после ответа клиенту: он может синхронно перекомпилировать и перезагрузить
            // домен, и тогда ответ этой команды потерялся бы вместе с соединением
            var job = NexusCliJobs.Start("refresh", ctx.GetInt("timeout", 600), ctx);
            job.Set("todo", ctx.Flag("force") ? "refresh-force" : "refresh");
            if (ctx.Flag("compile")) job.Set("compile", "1");
            job.Set("gen", generation.ToString(CultureInfo.InvariantCulture));
            job.Set("maxErrors", ctx.GetInt("max-errors", 20).ToString(CultureInfo.InvariantCulture));
            if (EditorApplication.isPlaying) ctx.Warn("Идёт Play: скрипты перекомпилируются по настройке «Script Changes While Playing».");
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("compile", "Перекомпилировать скрипты и дождаться итога (ошибки — код 1)",
            Usage = "compile [--clean] [--max-errors 20] [--timeout 600]")]
        private static int Compile(NexusCommandContext ctx)
        {
            Guard(ctx);
            if (EditorApplication.isPlaying) throw new NexusCommandException("Идёт Play — остановите: play.stop", NexusCommandContext.Exit.Failed);

            var generation = NexusCompileLog.Generation;
            var job = NexusCliJobs.Start("compile", ctx.GetInt("timeout", 600), ctx);
            job.Set("todo", ctx.Flag("clean") ? "compile-clean" : "compile");
            job.Set("gen", generation.ToString(CultureInfo.InvariantCulture));
            job.Set("maxErrors", ctx.GetInt("max-errors", 20).ToString(CultureInfo.InvariantCulture));
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("compile.errors", "Ошибки (и предупреждения) последней компиляции", Usage = "compile.errors [--warnings] [--max 30]", ReadOnly = true)]
        private static int CompileErrors(NexusCommandContext ctx)
        {
            var max = Math.Max(1, ctx.GetInt("max", 30));
            var r = NexusCompileLog.Last;
            var failed = EditorUtility.scriptCompilationFailed;

            if (r == null)
            {
                if (EditorApplication.isCompiling) ctx.Print("Компиляция идёт — повторите позже или compile.");
                else if (failed) ctx.Error("В проекте ошибки компиляции, но компиляция была до включения CLI: выполните compile.");
                else ctx.Print("✓ Ошибок компиляции нет.");
                ctx.Data = new { failed, errors = Array.Empty<string>() };
                return failed ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok;
            }

            if (r.Running) ctx.Warn("Компиляция ещё идёт — список неполный.");
            if (r.ErrorCount == 0) ctx.Print($"✓ Ошибок нет ({NexusCliText.Plural(r.WarningCount, "предупреждение", "предупреждения", "предупреждений")}, компиляция {NexusCliText.Clock(r.Finished)}).");
            else ctx.Print($"✗ Ошибок: {r.ErrorCount} (компиляция {NexusCliText.Clock(r.Finished)}):");

            for (var i = 0; i < r.Errors.Count && i < max; i++) ctx.Print("  " + r.Errors[i]);
            if (r.ErrorCount > max) ctx.Print($"  … ещё {r.ErrorCount - max}");

            if (ctx.Flag("warnings") && r.WarningCount > 0)
            {
                ctx.Print($"Предупреждений: {r.WarningCount}:");
                for (var i = 0; i < r.Warnings.Count && i < max; i++) ctx.Print("  " + r.Warnings[i]);
                if (r.WarningCount > max) ctx.Print($"  … ещё {r.WarningCount - max}");
            }

            ctx.Data = new { failed = r.ErrorCount > 0, errorCount = r.ErrorCount, warningCount = r.WarningCount, errors = r.Errors, warnings = ctx.Flag("warnings") ? r.Warnings : null, running = r.Running };
            return r.ErrorCount > 0 ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok;
        }

        /// <summary>Обновление и компиляция: ждать, пока редактор не будет «тихим» <see cref="QuietSeconds"/>.</summary>
        private static void Settle(NexusCliJob job)
        {
            var now = NexusCliJobs.Now;

            var todo = job.Get("todo");
            if (todo != null)
            {
                // пауза — чтобы ответ команды успел уйти клиенту до возможной перезагрузки домена
                if (now - job.StartedAt < 0.25) return;
                job.Params.Remove("todo");
                NexusCliJobs.SetPhase(job, todo.StartsWith("compile", StringComparison.Ordinal) ? "компиляция скриптов" : "импорт ассетов");
                NexusCliJobs.Touch(job);
                NexusCliJobs.Save();
                RunTodo(job, todo);
                job.Anchor = NexusCliJobs.Now;
                return;
            }

            if (EditorApplication.isCompiling)
            {
                job.Anchor = now;
                NexusCliJobs.SetPhase(job, "компиляция скриптов");
                return;
            }

            if (EditorApplication.isUpdating)
            {
                job.Anchor = now;
                NexusCliJobs.SetPhase(job, "импорт ассетов");
                return;
            }

            if (now - job.Anchor < QuietSeconds) return;
            FinishCompile(job);
        }

        private static void RunTodo(NexusCliJob job, string todo)
        {
            switch (todo)
            {
                case "refresh":
                case "refresh-force":
                    AssetDatabase.Refresh(todo == "refresh-force" ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
                    if (job.Get("compile") == "1") CompilationPipeline.RequestScriptCompilation();
                    break;
                case "compile":
                case "compile-clean":
                    CompilationPipeline.RequestScriptCompilation(todo == "compile-clean" ? RequestScriptCompilationOptions.CleanBuildCache : RequestScriptCompilationOptions.None);
                    break;
                case "reimport":
                    var options = ImportAssetOptions.ForceUpdate;
                    if (job.Get("recursive") == "1") options |= ImportAssetOptions.ImportRecursive;
                    AssetDatabase.ImportAsset(job.Get("path"), options);
                    break;
            }
        }

        private static void FinishCompile(NexusCliJob job)
        {
            int.TryParse(job.Get("gen", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation);
            int.TryParse(job.Get("maxErrors", "20"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxErrors);
            maxErrors = Math.Max(1, maxErrors);

            var r = NexusCompileLog.Last;
            var compiled = r != null && r.Generation > generation;
            var failed = EditorUtility.scriptCompilationFailed;
            var sb = new StringBuilder();
            var exit = NexusCommandContext.Exit.Ok;
            var errors = new List<string>();

            if (compiled && r.ErrorCount > 0 || !compiled && failed)
            {
                exit = NexusCommandContext.Exit.Failed;
                var count = r?.ErrorCount ?? 0;
                sb.Append(compiled
                    ? $"✗ Компиляция: {NexusCliText.Plural(count, "ошибка", "ошибки", "ошибок")}\n"
                    : "✗ Скрипты не менялись, но в проекте ошибки компиляции" + (count > 0 ? $" ({count})" : " (выполните compile)") + "\n");
                if (r != null)
                {
                    for (var i = 0; i < r.Errors.Count && i < maxErrors; i++)
                    {
                        sb.Append("  ").Append(r.Errors[i]).Append('\n');
                        errors.Add(r.Errors[i]);
                    }

                    if (r.ErrorCount > maxErrors) sb.Append($"  … ещё {r.ErrorCount - maxErrors} — compile.errors --max {r.ErrorCount}\n");
                }
            }
            else if (compiled)
            {
                sb.Append($"✓ Скрипты скомпилированы ({NexusCliText.Plural(r.Assemblies.Count, "сборка", "сборки", "сборок")}, " +
                          $"{NexusCliText.Plural(r.WarningCount, "предупреждение", "предупреждения", "предупреждений")})\n");
            }
            else
            {
                sb.Append(job.Kind == "compile" ? "✓ Компиляция не понадобилась\n"
                    : job.Kind == "settle" ? "✓ Редактор готов, скрипты не менялись\n"
                    : "✓ Ассеты обновлены, скрипты не менялись\n");
            }

            var logLines = new List<string>();
            var logErrors = NexusCliText.ErrorDigest(job.LogSeq, 3, logLines);
            if (logErrors > 0)
            {
                sb.Append($"! В логе за это время {NexusCliText.Plural(logErrors, "ошибка", "ошибки", "ошибок")} (logs --errors --since {job.LogSeq}):\n");
                foreach (var l in logLines) sb.Append(l).Append('\n');
            }

            sb.Append($"за {NexusCliText.Seconds(NexusCliJobs.Now - job.StartedAt)}\n");
            NexusCliJobs.Finish(job, exit, sb.ToString(), new
            {
                compiled, failed = exit != NexusCommandContext.Exit.Ok,
                errorCount = compiled ? r.ErrorCount : failed ? r?.ErrorCount ?? -1 : 0,
                warningCount = compiled ? r.WarningCount : 0,
                errors, logErrors, logSince = job.LogSeq,
            });
        }

        /// <summary>Ждать, пока редактор не успокоится после команды (WaitForEditor): итог — как у refresh.</summary>
        internal static void StartSettle(NexusCommandContext ctx, int generationBefore)
        {
            try
            {
                var job = NexusCliJobs.Start("settle", 600, ctx);
                // поколение до команды: компиляция могла начаться прямо внутри неё
                job.Set("gen", generationBefore.ToString(CultureInfo.InvariantCulture));
                job.Set("maxErrors", "20");
                NexusCliJobs.SetPhase(job, "ожидание редактора");
            }
            catch (Exception e)
            {
                NexusDiagnostics.Error("CLI: ожидание редактора", e);
            }
        }

        /// <summary>Не начинать операцию, пока идёт другая, меняющая состояние редактора.</summary>
        internal static void Guard(NexusCommandContext ctx)
        {
            var running = NexusCliJobs.Running(ExclusiveKinds);
            if (running != null)
                throw new NexusCommandException($"Идёт задание {running.Id} ({running.Kind}: {running.Phase}) — дождитесь: job.status {running.Id}", NexusCommandContext.Exit.Failed);
        }

        // ================================================================ задания

        [NexusCommand("jobs", "Задания CLI: идущие и последние завершённые", Usage = "jobs", ReadOnly = true)]
        private static int Jobs(NexusCommandContext ctx)
        {
            var list = new List<object>();
            if (NexusCliJobs.All.Count == 0) ctx.Print("Заданий нет.");
            foreach (var j in NexusCliJobs.All)
            {
                var state = j.Done ? (j.ExitCode == 0 ? "✓" : "✗ " + j.ExitCode) : "… " + j.Phase;
                ctx.Print($"{j.Id,-5} {j.Kind,-12} {state}");
                list.Add(new { id = j.Id, kind = j.Kind, done = j.Done, exitCode = j.ExitCode, phase = j.Phase });
            }

            ctx.Data = list;
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("job.status", "Состояние или итог задания (клиент ждёт задания этой командой)", Usage = "job.status <id>", ReadOnly = true)]
        private static int JobStatus(NexusCommandContext ctx)
        {
            var id = ctx.Arg(0) ?? throw new NexusCommandException("нужно <id> задания: jobs");
            var job = NexusCliJobs.Find(id) ?? throw new NexusCommandException($"Нет задания «{id}» (задания живут до перезапуска редактора).", NexusCommandContext.Exit.Failed);
            ctx.Job = job.Id;

            if (!job.Done)
            {
                ctx.Print($"… {job.Kind}: {job.Phase} ({NexusCliText.Seconds(NexusCliJobs.Now - job.StartedAt)})");
                ctx.Data = new { id = job.Id, kind = job.Kind, phase = job.Phase, done = false };
                return NexusCommandContext.Exit.Ok;
            }

            if (!string.IsNullOrEmpty(job.Output)) ctx.Print(job.Output.TrimEnd('\n'));
            if (!string.IsNullOrEmpty(job.Data)) ctx.Data = new JRaw(job.Data);
            return job.ExitCode;
        }

        [NexusCommand("bridge.hello", "Для демона: версии пакета и протокола, состояние редактора, список команд", Usage = "bridge.hello", ReadOnly = true)]
        private static int BridgeHello(NexusCommandContext ctx)
        {
            var commands = new List<object>();
            foreach (var c in NexusCommands.All())
                commands.Add(new { name = c.Name, summary = c.Summary, usage = c.Usage, source = c.Source, readOnly = c.ReadOnly });
            var info = NexusCliServer.Describe();
            ctx.Data = new
            {
                protocol = 1,
                nexus = NexusCliDaemon.NexusVersion,
                project = info.Project,
                path = info.Path,
                unity = info.Unity,
                pid = info.Pid,
                phase = NexusCliDomainMarks.Phase(),
                playing = EditorApplication.isPlaying,
                commands,
            };
            ctx.Print($"{info.Project} · Nexus {NexusCliDaemon.NexusVersion} · команд: {commands.Count}");
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("daemon.status", "Состояние Nexus Daemon (запущен ли, версия, порт)", Usage = "daemon.status", ReadOnly = true)]
        private static int DaemonStatus(NexusCommandContext ctx)
        {
            ctx.Print(NexusCliDaemon.Describe());
            ctx.Data = new { status = NexusCliDaemon.Describe() };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("cli.wake", "Будильник сторожа для редактора, застрявшего в фоне: off, soft (по умолчанию), focus",
            Usage = "cli.wake [off|soft|focus]")]
        private static int CliWake(NexusCommandContext ctx)
        {
            var mode = ctx.Arg(0);
            if (mode != null)
            {
                if (System.Array.IndexOf(NexusCliStore.WakeModes, mode) < 0) throw new NexusCommandException("cli.wake off|soft|focus");
                NexusCliStore.Wake = mode;
            }

            var now = NexusCliStore.Wake;
            ctx.Print($"будильник: {now}" + (now == "focus" ? " (при застревании Unity на пару секунд выходит на передний план)" : now == "soft" ? " (без смены фокуса)" : " (только диагноз)"));
            ctx.Data = new { wake = now };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("job.cancel", "Отменить ожидание задания (сама операция Unity может продолжиться)", Usage = "job.cancel <id>")]
        private static int JobCancel(NexusCommandContext ctx)
        {
            var id = ctx.Arg(0) ?? throw new NexusCommandException("нужно <id> задания: jobs");
            var target = NexusCliJobs.Find(id);
            if (target != null && target.Kind == NexusCliTasks.Kind && NexusCliTasks.Cancel(id))
            {
                ctx.Print($"Задача {id}: отмена — выполняются шаги always.");
                return NexusCommandContext.Exit.Ok;
            }

            if (!NexusCliJobs.Cancel(id, "по команде")) throw new NexusCommandException($"Задание «{id}» не идёт.", NexusCommandContext.Exit.Failed);
            ctx.Print($"Задание {id} отменено.");
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ меню, ассеты, выделение

        [NexusCommand("menu", "Выполнить пункт меню Unity (без модальных окон!) или показать подпункты",
            Usage = "menu \"Assets/Refresh\" | menu --list \"Window/General\"")]
        private static int Menu(NexusCommandContext ctx)
        {
            if (ctx.Has("list"))
            {
                var prefix = ctx.Get("list") ?? ctx.Arg(0) ?? string.Empty;
                string[] items;
                try
                {
                    items = Unsupported.GetSubmenus(prefix) ?? Array.Empty<string>();
                }
                catch (Exception e)
                {
                    throw new NexusCommandException("Список меню недоступен: " + e.Message, NexusCommandContext.Exit.Failed);
                }

                var max = Math.Max(1, ctx.GetInt("max", 100));
                for (var i = 0; i < items.Length && i < max; i++) ctx.Print(items[i]);
                if (items.Length > max) ctx.Print($"… ещё {items.Length - max} (--max)");
                if (items.Length == 0) ctx.Print("Пунктов нет.");
                ctx.Data = items;
                return NexusCommandContext.Exit.Ok;
            }

            var path = ctx.Arg(0) ?? throw new NexusCommandException("нужен путь пункта меню");
            if (!EditorApplication.ExecuteMenuItem(path)) throw new NexusCommandException($"Пункт «{path}» не найден или недоступен сейчас.", NexusCommandContext.Exit.Failed);
            ctx.Print("✓ " + path);
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("assets.find", "Найти ассеты (синтаксис поиска Project: t:Scene, l:метка, имя)",
            Usage = "assets.find \"t:Prefab Ship\" [--in Assets/Game] [--max 50]", ReadOnly = true)]
        private static int AssetsFind(NexusCommandContext ctx)
        {
            var filter = ctx.Arg(0) ?? throw new NexusCommandException("нужен фильтр поиска");
            var folder = ctx.Get("in");
            var guids = string.IsNullOrEmpty(folder) ? AssetDatabase.FindAssets(filter) : AssetDatabase.FindAssets(filter, new[] { folder });
            var max = Math.Max(1, ctx.GetInt("max", 50));
            var paths = new List<string>();
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(p)) continue;
                if (paths.Count < max) ctx.Print(p);
                paths.Add(p);
            }

            if (paths.Count > max) ctx.Print($"… ещё {paths.Count - max} (--max)");
            if (paths.Count == 0) ctx.Print("Ничего не найдено.");
            ctx.Data = new { total = paths.Count, paths = paths.Count > max ? paths.GetRange(0, max) : paths };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("asset.reimport", "Переимпортировать ассет или папку", Usage = "asset.reimport <путь> [--recursive] [--timeout 600]")]
        private static int AssetReimport(NexusCommandContext ctx)
        {
            Guard(ctx);
            var path = ctx.Arg(0) ?? throw new NexusCommandException("нужен путь ассета");
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) throw new NexusCommandException($"Нет ассета «{path}».", NexusCommandContext.Exit.Failed);
            // как refresh: импорт скрипта может перезагрузить домен — делается в задании после ответа
            var job = NexusCliJobs.Start("refresh", ctx.GetInt("timeout", 600), ctx);
            job.Set("todo", "reimport");
            job.Set("path", path);
            if (ctx.Flag("recursive")) job.Set("recursive", "1");
            job.Set("gen", NexusCompileLog.Generation.ToString(CultureInfo.InvariantCulture));
            job.Set("maxErrors", "20");
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("select", "Выделить ассет или объект сцены и подсветить его", Usage = "select <путь ассета | путь объекта в сцене>")]
        private static int Select(NexusCommandContext ctx)
        {
            var path = ctx.Arg(0) ?? throw new NexusCommandException("нужен путь");
            UnityEngine.Object target = AssetDatabase.LoadMainAssetAtPath(path);
            if (target == null) target = GameObject.Find(path);
            if (target == null) throw new NexusCommandException($"Не найдено: «{path}» (ни ассет, ни активный объект сцены).", NexusCommandContext.Exit.Failed);
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
            ctx.Print("✓ " + target.name);
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("save", "Сохранить открытые сцены и ассеты", Usage = "save [--scenes] [--assets]")]
        private static int Save(NexusCommandContext ctx)
        {
            var scenes = ctx.Flag("scenes") || !ctx.Flag("assets");
            var assets = ctx.Flag("assets") || !ctx.Flag("scenes");
            if (scenes)
            {
                if (EditorApplication.isPlaying) ctx.Warn("Идёт Play — сцены не сохраняются.");
                else if (NexusSceneCommands.HasUntitledDirty()) ctx.Error("Есть изменённая сцена без пути (иначе Unity открыла бы диалог) — scene.save --as Assets/…/Имя.unity.");
                else if (EditorSceneManager.SaveOpenScenes()) ctx.Print("✓ сцены сохранены");
                else ctx.Error("Сцены не сохранены.");
            }

            if (assets)
            {
                AssetDatabase.SaveAssets();
                ctx.Print("✓ ассеты сохранены");
            }

            return ctx.HasErrors ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok;
        }

        internal static string ProjectRoot => NexusPaths.ProjectRoot;
    }
}
