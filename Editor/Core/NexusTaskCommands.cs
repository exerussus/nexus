using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>Команды задач (<c>task.run</c>, <c>task.status</c>, <c>task.cancel</c>, <c>tasks</c>) и служебный <c>wait</c>.</summary>
    [InitializeOnLoad]
    internal static class NexusTaskCommands
    {
        static NexusTaskCommands()
        {
            NexusCliJobs.Register("wait", WaitStep);
        }

        [NexusCommand("task.run", "Запустить задачу: шаги-команды по очереди, итог — отчёт по задаче и шагам",
            Usage = "task.run <файл.json|имя сценария> [--timeout с] [--keep с] [--set ключ=значение,…] | task.run --steps \"refresh; play.start; screenshot; play.stop\" | task.run --list")]
        private static int Run(NexusCommandContext ctx)
        {
            if (ctx.Flag("list")) return List(ctx);

            NexusTaskDef def;
            var inline = ctx.Get("steps");
            if (!string.IsNullOrWhiteSpace(inline))
            {
                def = NexusCliTasks.FromSteps(inline);
            }
            else
            {
                var source = ctx.Arg(0) ?? throw new NexusCommandException("нужен файл задачи, имя сценария (task.run --list) или --steps \"…\"");
                var path = ResolveFile(source, ctx.WorkingDirectory);
                if (path == null) throw new NexusCommandException($"Задача «{source}» не найдена: ни файла, ни сценария в ProjectSettings/Nexus/tasks.", NexusCommandContext.Exit.Failed);
                def = NexusCliTasks.Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
            }

            if (ctx.Has("timeout")) def.Timeout = ctx.GetInt("timeout", 0);
            if (ctx.Has("keep")) def.Keep = ctx.GetInt("keep", 0);
            foreach (var pair in ctx.GetList("set"))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) throw new NexusCommandException($"--set: «{pair}» — нужно ключ=значение");
                def.Args[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1);
            }

            var job = NexusCliTasks.Start(def, ctx);
            ctx.Print($"задача {def.Name} ({job.Id}): {NexusCliText.Plural(def.Steps.Count, "шаг", "шага", "шагов")}");
            return NexusCommandContext.Exit.Ok;
        }

        private static int List(NexusCommandContext ctx)
        {
            var dir = NexusCliTasks.ScenariosDir;
            var list = new List<object>();
            if (Directory.Exists(dir))
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    string description = null;
                    var steps = 0;
                    try
                    {
                        var def = NexusCliTasks.Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
                        description = def.Description;
                        steps = def.Steps.Count;
                    }
                    catch (NexusCommandException e)
                    {
                        description = "✗ " + e.Message;
                    }

                    var name = Path.GetFileNameWithoutExtension(file);
                    ctx.Print($"{name,-24} {steps,2} шаг. {description}");
                    list.Add(new { name, steps, description });
                }

            if (list.Count == 0) ctx.Print("Сценариев нет (ProjectSettings/Nexus/tasks/<имя>.json).");
            ctx.Data = list;
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("task.status", "Прогресс идущей задачи (сразу, не ждёт) или её отчёт", Usage = "task.status <id>", ReadOnly = true)]
        private static int Status(NexusCommandContext ctx)
        {
            var id = ctx.Arg(0) ?? throw new NexusCommandException("нужен id задачи: tasks");
            var job = NexusCliJobs.Find(id);
            if (job == null || job.Kind != NexusCliTasks.Kind) throw new NexusCommandException($"Нет задачи «{id}».", NexusCommandContext.Exit.Failed);
            if (!job.Done)
            {
                var progress = NexusCliTasks.Progress(job);
                ctx.Print($"… {job.Get("name")}: {job.Phase} ({NexusCliText.Seconds(NexusCliJobs.Now - job.StartedAt)})");
                if (progress?["steps"] is JArray steps)
                    foreach (var s in steps)
                        ctx.Print($"  {Mark((string)s["status"])} {(string)s["id"]} ({(string)s["command"]})");
                ctx.Data = progress;
                return NexusCommandContext.Exit.Ok;
            }

            ctx.Job = job.Id;
            if (!string.IsNullOrEmpty(job.Output)) ctx.Print(job.Output.TrimEnd('\n'));
            if (!string.IsNullOrEmpty(job.Data)) ctx.Data = new JRaw(job.Data);
            return job.ExitCode;
        }

        [NexusCommand("task.cancel", "Отменить задачу: текущий шаг прерывается, шаги always выполняются", Usage = "task.cancel <id>")]
        private static int Cancel(NexusCommandContext ctx)
        {
            var id = ctx.Arg(0) ?? throw new NexusCommandException("нужен id задачи: tasks");
            if (!NexusCliTasks.Cancel(id)) throw new NexusCommandException($"Задача «{id}» не идёт.", NexusCommandContext.Exit.Failed);
            ctx.Print($"Задача {id}: отмена — выполняются шаги always.");
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("tasks", "Последние задачи: имя, статус, шаги, время", Usage = "tasks [--last 10]", ReadOnly = true)]
        private static int Tasks(NexusCommandContext ctx)
        {
            var last = Math.Max(1, ctx.GetInt("last", 10));
            var rows = new List<NexusCliJob>();
            foreach (var j in NexusCliJobs.All)
                if (j.Kind == NexusCliTasks.Kind)
                    rows.Add(j);
            if (rows.Count > last) rows.RemoveRange(0, rows.Count - last);

            var data = new List<object>();
            foreach (var j in rows)
            {
                var state = j.Done ? (j.ExitCode == 0 ? "✓" : "✗ " + j.ExitCode) : "… " + j.Phase;
                ctx.Print($"{j.Id,-5} {j.Get("name"),-20} {state}");
                data.Add(new { id = j.Id, name = j.Get("name"), done = j.Done, exitCode = j.ExitCode, phase = j.Phase });
            }

            if (rows.Count == 0) ctx.Print("Задач не было.");
            ctx.Data = data;
            return NexusCommandContext.Exit.Ok;
        }

        // ============================================================ wait

        [NexusCommand("wait", "Подождать: секунды, кадры Play или пока редактор не закончит импорт и компиляцию",
            Usage = "wait --seconds N | --frames N | --idle [--timeout с]", ReadOnly = true)]
        private static int Wait(NexusCommandContext ctx)
        {
            if (ctx.Has("frames") && !EditorApplication.isPlaying) throw new NexusCommandException("--frames — только в Play", NexusCommandContext.Exit.Failed);
            var seconds = 1.0;
            if (!ctx.Has("frames") && !ctx.Flag("idle"))
            {
                var raw = ctx.Get("seconds") ?? ctx.Arg(0) ?? "1";
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds < 0)
                    throw new NexusCommandException("--seconds N (число)");
            }

            var job = NexusCliJobs.Start("wait", ctx.GetInt("timeout", 600), ctx);
            if (ctx.Has("frames"))
            {
                job.Set("frames", Math.Max(1, ctx.GetInt("frames", 1)).ToString(CultureInfo.InvariantCulture));
                job.Set("frame0", Time.frameCount.ToString(CultureInfo.InvariantCulture));
            }
            else if (ctx.Flag("idle"))
            {
                job.Set("idle", "1");
            }
            else
            {
                job.Set("seconds", seconds.ToString(CultureInfo.InvariantCulture));
            }

            return NexusCommandContext.Exit.Ok;
        }

        private static void WaitStep(NexusCliJob job)
        {
            var now = NexusCliJobs.Now;
            if (job.Get("frames") != null)
            {
                if (!EditorApplication.isPlaying)
                {
                    NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed, "✗ Play остановлен.\n");
                    return;
                }

                int.TryParse(job.Get("frames"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames);
                int.TryParse(job.Get("frame0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame0);
                var passed = Time.frameCount - frame0;
                NexusCliJobs.SetPhase(job, $"кадры {passed}/{frames}");
                if (passed >= frames) NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok, $"✓ прошло кадров: {passed}\n", new { frames = passed });
                else EditorApplication.QueuePlayerLoopUpdate();
                return;
            }

            if (job.Get("idle") != null)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    job.Anchor = now;
                    NexusCliJobs.SetPhase(job, EditorApplication.isCompiling ? "компиляция" : "импорт");
                    return;
                }

                if (now - job.Anchor >= 1.0)
                    NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok, $"✓ редактор свободен ({NexusCliText.Seconds(now - job.StartedAt)})\n");
                return;
            }

            double.TryParse(job.Get("seconds", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds);
            NexusCliJobs.SetPhase(job, $"пауза {seconds:0.#} с");
            if (now - job.StartedAt >= seconds) NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok, $"✓ пауза {seconds:0.#} с\n", new { seconds });
        }

        // ============================================================ общее

        private static string ResolveFile(string source, string cwd)
        {
            var candidates = new List<string>();
            if (Path.IsPathRooted(source)) candidates.Add(source);
            else
            {
                if (!string.IsNullOrEmpty(cwd)) candidates.Add(Path.Combine(cwd, source));
                candidates.Add(Path.Combine(NexusPaths.ProjectRoot, source));
                var name = source.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? source : source + ".json";
                candidates.Add(Path.Combine(NexusCliTasks.ScenariosDir, name));
            }

            foreach (var c in candidates)
                try
                {
                    if (File.Exists(c)) return Path.GetFullPath(c);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("CLI: путь задачи", e);
                }

            return null;
        }

        private static string Mark(string status)
        {
            switch (status)
            {
                case "ok": return "✓";
                case "running": return "…";
                case "pending": return "·";
                case "skipped": return "-";
                default: return "✗";
            }
        }
    }
}
