using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Команды Play Mode: запуск (с ожиданием первых кадров и сводкой ошибок старта), остановка, пауза, шаг.
    /// Запуск и остановка — задания: переживают перезагрузку домена при входе в Play.
    /// </summary>
    [InitializeOnLoad]
    internal static class NexusPlayCommands
    {
        /// <summary>Сколько ждать, что Unity начала вход в Play, прежде чем счесть запуск отклонённым.</summary>
        private const double EnterGrace = 4.0;

        static NexusPlayCommands()
        {
            NexusCliJobs.Register("play.start", StartStep);
            NexusCliJobs.Register("play.stop", StopStep);
            NexusCliJobs.Register("play.step", FrameStep);
        }

        [NexusCommand("play.start", "Запустить Play и дождаться первых кадров; итог — ошибки лога за старт",
            Usage = "play.start [--scene <имя|путь|номер>] [--save|--discard] [--frames 5] [--clear-logs] [--fail-on-errors] [--no-background] [--timeout 180]")]
        private static int PlayStart(NexusCommandContext ctx)
        {
            if (EditorApplication.isPlaying)
            {
                ctx.Print(EditorApplication.isPaused ? "Play уже идёт (на паузе — play.pause off)." : "Play уже идёт.");
                return NexusCommandContext.Exit.Ok;
            }

            NexusEditorCommands.Guard(ctx);

            if (EditorUtility.scriptCompilationFailed)
            {
                ctx.Error("В проекте ошибки компиляции — Play не запустится:");
                var r = NexusCompileLog.Last;
                if (r != null)
                    for (var i = 0; i < r.Errors.Count && i < 10; i++)
                        ctx.Print("  " + r.Errors[i]);
                else ctx.Print("  compile — получить список");
                return NexusCommandContext.Exit.Failed;
            }

            var scene = ctx.Get("scene");
            if (!string.IsNullOrEmpty(scene))
            {
                var path = NexusSceneCommands.Resolve(scene);
                NexusSceneCommands.HandleDirty(ctx.Flag("save"), ctx.Flag("discard"));
                var opened = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Single);
                if (!opened.IsValid()) throw new NexusCommandException("Сцена не открылась: " + path, NexusCommandContext.Exit.Failed);
                ctx.Print("сцена: " + path);
            }

            if (ctx.Flag("clear-logs")) NexusLogBuffer.Clear();

            var job = NexusCliJobs.Start("play.start", ctx.GetInt("timeout", 180), ctx);
            job.Set("frames", Math.Max(1, ctx.GetInt("frames", 5)).ToString(CultureInfo.InvariantCulture));
            job.Set("background", ctx.Flag("no-background") ? "0" : "1");
            job.Set("failOnErrors", ctx.Flag("fail-on-errors") ? "1" : "0");
            EditorApplication.EnterPlaymode();
            return NexusCommandContext.Exit.Ok;
        }

        private static void StartStep(NexusCliJob job)
        {
            var now = NexusCliJobs.Now;
            if (!EditorApplication.isPlaying)
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    job.Anchor = now;
                    NexusCliJobs.SetPhase(job, EditorApplication.isCompiling ? "компиляция перед Play" : "вход в Play");
                    return;
                }

                if (now - job.Anchor < EnterGrace) return;

                var sb = new StringBuilder("✗ Play не запустился");
                if (EditorUtility.scriptCompilationFailed) sb.Append(": ошибки компиляции — compile.errors");
                sb.Append('\n');
                AppendErrors(sb, job.LogSeq);
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed, sb.ToString());
                return;
            }

            if (job.Get("frame0") == null)
            {
                job.Set("frame0", Time.frameCount.ToString(CultureInfo.InvariantCulture));
                if (job.Get("background") == "1") Application.runInBackground = true;
                NexusCliJobs.SetPhase(job, "первые кадры");
                NexusCliJobs.Touch(job);
                return;
            }

            int.TryParse(job.Get("frame0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame0);
            int.TryParse(job.Get("frames", "5"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames);
            // пауза (кнопка Pause или Error Pause на исключении старта) — кадры стоят: итог сразу, с причиной
            var paused = EditorApplication.isPaused;
            if (!paused && Time.frameCount - frame0 < frames) return;

            var lines = new List<string>();
            var errors = NexusCliText.ErrorDigest(job.LogSeq, 5, lines);
            var output = new StringBuilder();
            output.Append($"✓ Play запущен за {NexusCliText.Seconds(now - job.StartedAt)}");
            if (paused) output.Append(" — на паузе (Pause или Error Pause; продолжить: play.pause off)");
            if (Application.runInBackground && job.Get("background") == "1") output.Append(" (идёт и без фокуса редактора)");
            output.Append('\n');
            if (errors > 0)
            {
                output.Append($"! Ошибок в логе со старта: {errors} — logs --errors --since {job.LogSeq}\n");
                foreach (var l in lines) output.Append(l).Append('\n');
            }

            var fail = errors > 0 && job.Get("failOnErrors") == "1";
            NexusCliJobs.Finish(job, fail ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok, output.ToString(),
                new { playing = true, paused, errors, logSince = job.LogSeq, seconds = now - job.StartedAt });
        }

        [NexusCommand("play.stop", "Остановить Play и дождаться возврата в Edit Mode; итог — ошибки за сессию Play",
            Usage = "play.stop [--timeout 120]")]
        private static int PlayStop(NexusCommandContext ctx)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                ctx.Print("Play не запущен.");
                return NexusCommandContext.Exit.Ok;
            }

            var running = NexusCliJobs.Running("play.start");
            if (running != null) NexusCliJobs.Cancel(running.Id, "Play остановлен");
            NexusEditorCommands.Guard(ctx);

            var job = NexusCliJobs.Start("play.stop", ctx.GetInt("timeout", 120), ctx);
            job.Set("playSeq", NexusLogBuffer.PlaySeq.ToString(CultureInfo.InvariantCulture));
            EditorApplication.ExitPlaymode();
            return NexusCommandContext.Exit.Ok;
        }

        private static void StopStep(NexusCliJob job)
        {
            var now = NexusCliJobs.Now;
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                job.Anchor = now;
                NexusCliJobs.SetPhase(job, "выход из Play");
                return;
            }

            if (now - job.Anchor < 0.5) return;

            long.TryParse(job.Get("playSeq", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var playSeq);
            NexusLogBuffer.Count(playSeq, out var errors, out var warnings, out _);
            var sb = new StringBuilder("✓ Play остановлен\n");
            if (errors > 0 || warnings > 0)
                sb.Append($"за сессию Play: {NexusCliText.Plural(errors, "ошибка", "ошибки", "ошибок")}, " +
                          $"{NexusCliText.Plural(warnings, "предупреждение", "предупреждения", "предупреждений")} — logs --play --errors\n");
            NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok, sb.ToString(), new { playing = false, errors, warnings, logSince = playSeq });
        }

        [NexusCommand("play.pause", "Пауза Play: on | off | без аргумента — переключить", Usage = "play.pause [on|off]")]
        private static int PlayPause(NexusCommandContext ctx)
        {
            if (!EditorApplication.isPlaying) throw new NexusCommandException("Play не запущен.", NexusCommandContext.Exit.Failed);
            var arg = ctx.Arg(0);
            bool value;
            if (string.IsNullOrEmpty(arg)) value = !EditorApplication.isPaused;
            else if (arg == "on" || arg == "1" || arg == "true") value = true;
            else if (arg == "off" || arg == "0" || arg == "false") value = false;
            else throw new NexusCommandException("on или off");

            EditorApplication.isPaused = value;
            ctx.Print(value ? "Пауза." : "Play продолжается.");
            ctx.Data = new { paused = value };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("play.step", "Шаг кадра в Play (ставит паузу)", Usage = "play.step [--frames 1]")]
        private static int PlayStep(NexusCommandContext ctx)
        {
            if (!EditorApplication.isPlaying) throw new NexusCommandException("Play не запущен.", NexusCommandContext.Exit.Failed);
            var frames = Math.Max(1, Math.Min(1000, ctx.GetInt("frames", 1)));
            if (frames > 1) NexusEditorCommands.Guard(ctx);
            EditorApplication.isPaused = true;
            EditorApplication.Step();
            if (frames == 1)
            {
                ctx.Print($"Кадр {Time.frameCount}.");
                return NexusCommandContext.Exit.Ok;
            }

            var job = NexusCliJobs.Start("play.step", 30 + frames, ctx);
            job.Set("left", (frames - 1).ToString(CultureInfo.InvariantCulture));
            job.Set("frame", Time.frameCount.ToString(CultureInfo.InvariantCulture));
            return NexusCommandContext.Exit.Ok;
        }

        private static void FrameStep(NexusCliJob job)
        {
            if (!EditorApplication.isPlaying)
            {
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed, "✗ Play остановлен.\n");
                return;
            }

            // следующий Step — только когда прошлый кадр прошёл: несколько Step до кадра схлопываются в один
            int.TryParse(job.Get("frame", "-1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastFrame);
            if (Time.frameCount == lastFrame) return;

            int.TryParse(job.Get("left", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var left);
            if (left <= 0)
            {
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok, $"Кадр {Time.frameCount}.\n", new { frame = Time.frameCount });
                return;
            }

            job.Set("frame", Time.frameCount.ToString(CultureInfo.InvariantCulture));
            EditorApplication.Step();
            job.Set("left", (left - 1).ToString(CultureInfo.InvariantCulture));
            NexusCliJobs.SetPhase(job, $"осталось кадров: {left - 1}");
        }

        private static void AppendErrors(StringBuilder sb, long since)
        {
            var lines = new List<string>();
            var errors = NexusCliText.ErrorDigest(since, 5, lines);
            if (errors == 0) return;
            sb.Append($"Ошибок в логе: {errors} — logs --errors --since {since}\n");
            foreach (var l in lines) sb.Append(l).Append('\n');
        }
    }
}
