using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>Шаг задачи: команда CLI и правила её успеха.</summary>
    public sealed class NexusTaskStep
    {
        public string Id;
        public string Command;
        public List<string> Args = new List<string>();
        public double Timeout;
        public string OnError;
        public bool Always;
        public JObject Expect;
    }

    /// <summary>Определение задачи: упорядоченные шаги-команды.</summary>
    public sealed class NexusTaskDef
    {
        public string Name;
        public string Description;
        public double Timeout;
        public double Keep;
        public string OnError = "stop";
        public bool Lock = true;
        public List<NexusTaskStep> Steps = new List<NexusTaskStep>();
        public Dictionary<string, string> Args = new Dictionary<string, string>();
    }

    /// <summary>
    /// Задачи CLI — компонент Core с явным владельцем (этот класс). Задача — задание вида <c>task</c>: его обработчик
    /// ведёт шаги по очереди, ждёт заданий долгих команд, проверяет <c>expect</c>, подставляет <c>${шаг.поле}</c> и
    /// собирает отчёт. Состояние — в параметрах задания (SessionState): задача переживает перезагрузку домена.
    /// </summary>
    /// <remarks>Работает только вместе с сервером CLI (через задания): выключен CLI — задачи прерываются и стираются.</remarks>
    [InitializeOnLoad]
    public static class NexusCliTasks
    {
        public const string Kind = "task";
        private const int MaxSteps = 100;
        private const int MaxStepOutput = 4000;
        private const double DefaultTimeout = 1800;
        private const double AlwaysStepTimeout = 60;

        public static string ScenariosDir => System.IO.Path.Combine(NexusPaths.ProjectRoot, "ProjectSettings", "Nexus", "tasks");

        // кэш разобранного состояния: источник правды — параметры задания, кэш живёт до конца задачи или домена
        private static readonly Dictionary<string, TaskState> Cache = new Dictionary<string, TaskState>(StringComparer.Ordinal);

        private sealed class StepResult
        {
            public string Id;
            public string Command;
            public List<string> Args;
            public string Status;
            public int ExitCode;
            public double Seconds;
            public string Output;
            public JToken Data;
            public string ExpectFailed;
            public List<string> Artifacts;
        }

        private sealed class Interference
        {
            public string Step;
            public string Command;
            public List<string> Args;
        }

        private sealed class TaskState
        {
            public NexusTaskDef Def;
            public int Index;
            public List<StepResult> Results = new List<StepResult>();
            public List<Interference> Interference = new List<Interference>();
            public bool Failed;
            public bool Cancelled;
            public bool TimedOut;
            public string FailedStep;
            public double TaskDeadline;
            public bool StepStarted;
            public double StepStartedAt;
            public int StepCounter;
            public string StepJob;
            public string StepOutput;
            public List<string> StepArgs;
        }

        static NexusCliTasks()
        {
            NexusCliJobs.Register(Kind, Step);
            NexusCliJobs.RegisterAbort(Kind, Abort);
        }

        // ============================================================ запуск и управление

        /// <summary>Запустить задачу. Ошибка определения или уже идущая задача — <see cref="NexusCommandException"/>.</summary>
        public static NexusCliJob Start(NexusTaskDef def, NexusCommandContext ctx = null)
        {
            Validate(def);
            var running = NexusCliJobs.Running(Kind);
            if (running != null)
                throw new NexusCommandException($"Уже идёт задача {running.Get("name")} ({running.Id}) — task.status {running.Id} или task.cancel {running.Id}", NexusCommandContext.Exit.Failed);

            var timeout = def.Timeout > 0 ? def.Timeout : DefaultTimeout;
            var job = NexusCliJobs.Start(Kind, timeout + Reserve(def), ctx);
            job.Set("name", def.Name);
            job.Set("lock", def.Lock ? "1" : "0");
            var state = new TaskState { Def = def, TaskDeadline = job.StartedAt + timeout };
            Persist(job, state);
            NexusCliJobs.SetPhase(job, $"[0/{def.Steps.Count}] старт");
            NexusCliJobs.Save();
            return job;
        }

        /// <summary>
        /// Запас внешнего срока задания сверх срока задачи: шаги always после остановки (каждый — свой timeout или
        /// <see cref="AlwaysStepTimeout"/>) и минута на отчёт. Внешний срок — страховка: задача кончается сама.
        /// </summary>
        private static double Reserve(NexusTaskDef def)
        {
            var reserve = 60.0;
            foreach (var s in def.Steps)
                if (s.Always)
                    reserve += s.Timeout > 0 ? s.Timeout : AlwaysStepTimeout;
            return reserve;
        }

        /// <summary>Срок вызывающего (--timeout у task.run): урезает срок задачи, внешний срок пересчитывается с запасом.</summary>
        public static void LimitTimeout(NexusCliJob job, double seconds)
        {
            if (job == null || job.Done || job.Kind != Kind || seconds <= 0) return;
            var st = State(job);
            if (st == null) return;
            var limit = job.StartedAt + seconds;
            if (limit >= st.TaskDeadline) return;
            st.TaskDeadline = limit;
            job.Deadline = limit + Reserve(st.Def);
            Persist(job, st);
            NexusCliJobs.Touch(job);
        }

        /// <summary>
        /// Аварийный выход (внешний срок, исключение обработчика): прервать задание шага и всё равно написать отчёт.
        /// Шаги always здесь уже не выполняются — на них не осталось срока.
        /// </summary>
        private static void Abort(NexusCliJob job, int exit, string reason)
        {
            var st = State(job);
            if (st == null) return;
            if (st.StepJob != null && NexusCliJobs.IsRunning(st.StepJob)) NexusCliJobs.Cancel(st.StepJob, "задача прервана");
            var timeout = exit == NexusCommandContext.Exit.Timeout;
            if (st.Index < st.Def.Steps.Count && (st.StepStarted || st.StepJob != null))
                Complete(job, st, exit, (st.StepOutput ?? string.Empty) + reason, null, timeout ? "timeout" : "crashed");
            for (; st.Index < st.Def.Steps.Count; st.Index++)
            {
                var s = st.Def.Steps[st.Index];
                st.Results.Add(new StepResult { Id = s.Id, Command = s.Command, Args = s.Args, Status = "skipped" });
            }

            if (timeout) st.TimedOut = true;
            else
            {
                st.Failed = true;
                st.FailedStep ??= st.Results.FindLast(r => r.Status != "skipped")?.Id;
            }

            FinishTask(job, st);
        }

        public static bool Cancel(string id)
        {
            var job = NexusCliJobs.Find(id);
            if (job == null || job.Done || job.Kind != Kind) return false;
            var state = State(job);
            if (state == null) return NexusCliJobs.Cancel(id, "по команде");
            state.Cancelled = true;
            Persist(job, state);
            return true;
        }

        /// <summary>Идущая задача, держащая редактор (lock), или null.</summary>
        public static NexusCliJob LockingTask()
        {
            var job = NexusCliJobs.Running(Kind);
            return job != null && job.Get("lock") == "1" ? job : null;
        }

        /// <summary>
        /// Пропустить ли меняющую команду при идущей задаче: свои шаги задачи и <c>task.cancel</c> — да; чужие — только
        /// с <c>--force</c> (вмешательство пишется в отчёт). Задача без lock пропускает всё, но тоже пишет.
        /// </summary>
        public static bool Admit(NexusCommandContext ctx, out string refusal)
        {
            refusal = null;
            var job = NexusCliJobs.Running(Kind);
            if (job == null || ctx == null) return true;
            if (string.Equals(ctx.Task, job.Id, StringComparison.Ordinal)) return true;
            // task.*: отмена; status/list читают; второй task.run откажет сам («уже идёт») — без записи вмешательства
            if (ctx.Name != null && ctx.Name.StartsWith("task.", StringComparison.OrdinalIgnoreCase)) return true;

            var state = State(job);
            var locked = job.Get("lock") == "1";
            if (locked && !ctx.Force)
            {
                var where = state != null ? $"шаг {Math.Min(state.Index + 1, state.Def.Steps.Count)}/{state.Def.Steps.Count}" +
                                            (state.Index < state.Def.Steps.Count ? ", " + state.Def.Steps[state.Index].Id : string.Empty) : "?";
                refusal = $"Идёт задача {job.Get("name")} ({where}) — дождитесь, task.cancel {job.Id} или --force";
                return false;
            }

            if (state != null)
            {
                state.Interference.Add(new Interference
                {
                    Step = state.Index < state.Def.Steps.Count ? state.Def.Steps[state.Index].Id : "-",
                    Command = ctx.Name,
                    Args = new List<string>(ctx.Raw),
                });
                Persist(job, state);
            }

            return true;
        }

        // ============================================================ определение

        /// <summary>Разобрать определение задачи из JSON (файл, запрос файлового канала, сценарий проекта).</summary>
        public static NexusTaskDef Parse(string json, string fallbackName)
        {
            JObject o;
            try
            {
                o = JObject.Parse(json);
            }
            catch (Exception e)
            {
                throw new NexusCommandException("Задача: JSON не разбирается — " + e.Message, NexusCommandContext.Exit.Usage);
            }

            return Parse(o, fallbackName);
        }

        public static NexusTaskDef Parse(JObject o, string fallbackName)
        {
            var def = new NexusTaskDef
            {
                Name = Str(o, "name") ?? fallbackName ?? "task",
                Description = Str(o, "description"),
                Timeout = Num(o, "timeout"),
                Keep = Num(o, "keep"),
                OnError = Str(o, "onError") ?? "stop",
                Lock = o["lock"] == null || o["lock"].Type != JTokenType.Boolean || o["lock"].Value<bool>(),
            };

            if (o["args"] is JObject args)
                foreach (var p in args.Properties())
                    def.Args[p.Name] = p.Value.Type == JTokenType.String ? p.Value.Value<string>() : p.Value.ToString(Formatting.None);

            if (!(o["steps"] is JArray steps)) throw new NexusCommandException("Задача: нужен массив steps", NexusCommandContext.Exit.Usage);
            foreach (var t in steps)
            {
                if (!(t is JObject s)) throw new NexusCommandException("Задача: шаг должен быть объектом", NexusCommandContext.Exit.Usage);
                var step = new NexusTaskStep
                {
                    Id = Str(s, "id"),
                    Command = Str(s, "command"),
                    Timeout = Num(s, "timeout"),
                    OnError = Str(s, "onError"),
                    Always = s["always"] != null && s["always"].Type == JTokenType.Boolean && s["always"].Value<bool>(),
                    Expect = s["expect"] as JObject,
                };

                var a = s["args"];
                if (a is JArray arr)
                    foreach (var x in arr) step.Args.Add(x.Type == JTokenType.String ? x.Value<string>() : x.ToString(Formatting.None));
                else if (a != null && a.Type == JTokenType.String) step.Args.AddRange(Tokenize(a.Value<string>()));

                // "command": "play.start --clear-logs" — аргументы прямо в команде идут перед args
                if (step.Command != null && step.Command.Trim().IndexOfAny(new[] { ' ', '\t' }) > 0)
                {
                    var tokens = Tokenize(step.Command);
                    step.Command = tokens[0];
                    step.Args.InsertRange(0, tokens.GetRange(1, tokens.Count - 1));
                }

                def.Steps.Add(step);
            }

            return def;
        }

        /// <summary>Быстрая задача из строки: шаги через «;», аргументы через пробел (кавычки — одна строка).</summary>
        public static NexusTaskDef FromSteps(string line)
        {
            var def = new NexusTaskDef { Name = "inline" };
            foreach (var part in SplitSteps(line))
            {
                var tokens = Tokenize(part);
                if (tokens.Count == 0) continue;
                var step = new NexusTaskStep { Command = tokens[0] };
                for (var i = 1; i < tokens.Count; i++) step.Args.Add(tokens[i]);
                def.Steps.Add(step);
            }

            return def;
        }

        private static void Validate(NexusTaskDef def)
        {
            if (def == null || def.Steps == null || def.Steps.Count == 0) throw new NexusCommandException("Задача без шагов", NexusCommandContext.Exit.Usage);
            if (def.Steps.Count > MaxSteps) throw new NexusCommandException($"Задача: не больше {MaxSteps} шагов", NexusCommandContext.Exit.Usage);
            if (def.OnError != "stop" && def.OnError != "continue") throw new NexusCommandException("onError: stop или continue", NexusCommandContext.Exit.Usage);

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < def.Steps.Count; i++)
            {
                var s = def.Steps[i];
                if (string.IsNullOrWhiteSpace(s.Command)) throw new NexusCommandException($"Шаг {i + 1}: нет command", NexusCommandContext.Exit.Usage);
                if (s.Command.StartsWith("task.", StringComparison.OrdinalIgnoreCase))
                    throw new NexusCommandException($"Шаг {i + 1}: задача внутри задачи не поддерживается ({s.Command})", NexusCommandContext.Exit.Usage);
                if (string.IsNullOrWhiteSpace(s.Id)) s.Id = "s" + (i + 1);
                if (!ids.Add(s.Id)) throw new NexusCommandException($"Шаг «{s.Id}»: id повторяется", NexusCommandContext.Exit.Usage);
                if (s.OnError != null && s.OnError != "stop" && s.OnError != "continue") throw new NexusCommandException($"Шаг «{s.Id}»: onError — stop или continue", NexusCommandContext.Exit.Usage);
            }
        }

        // ============================================================ ход задачи (обработчик задания)

        private static void Step(NexusCliJob job)
        {
            var st = State(job);
            if (st == null)
            {
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Crashed, "✗ Состояние задачи потеряно.\n");
                return;
            }

            var now = NexusCliJobs.Now;
            var steps = st.Def.Steps;
            if (!st.TimedOut && now > st.TaskDeadline)
            {
                st.TimedOut = true;
                Persist(job, st);
            }

            // шаг ушёл в задание — ждём его
            if (st.StepJob != null)
            {
                var step = steps[st.Index];
                var sj = NexusCliJobs.Find(st.StepJob);
                if (sj != null && !sj.Done)
                {
                    var halt = (st.Cancelled || st.TimedOut) && !step.Always;
                    var limit = step.Timeout > 0 ? step.Timeout : (step.Always && (st.Cancelled || st.TimedOut || st.Failed) ? AlwaysStepTimeout : 0);
                    if (halt)
                    {
                        NexusCliJobs.Cancel(sj.Id, st.Cancelled ? "задача отменена" : "истёк срок задачи");
                        Complete(job, st, NexusCommandContext.Exit.Failed, st.StepOutput, null, st.Cancelled ? "cancelled" : "timeout");
                    }
                    else if (limit > 0 && now - st.StepStartedAt > limit)
                    {
                        NexusCliJobs.Cancel(sj.Id, "истёк срок шага");
                        Complete(job, st, NexusCommandContext.Exit.Timeout, st.StepOutput + $"✗ Шаг не уложился в {limit:0} с.\n", null, "timeout");
                    }
                    else
                    {
                        NexusCliJobs.SetPhase(job, $"[{st.Index + 1}/{steps.Count}] {step.Id}: {sj.Phase}");
                    }

                    return;
                }

                if (sj == null) Complete(job, st, NexusCommandContext.Exit.Crashed, st.StepOutput + "✗ Задание шага пропало.\n", null, "interrupted");
                else Complete(job, st, sj.ExitCode, st.StepOutput + sj.Output, ParseData(sj.Data), null);
                return;
            }

            // шаг начали, а итога нет — домен перезагрузился посреди быстрой команды
            if (st.StepStarted)
            {
                // команда успела начать задание, а домен перезагрузился до записи StepJob — подхватить его
                foreach (var j in NexusCliJobs.All)
                    if (j.Kind != Kind && NexusCliJobs.CreatedAfter(j, st.StepCounter))
                    {
                        st.StepJob = j.Id;
                        st.StepOutput = string.Empty;
                        Persist(job, st);
                        return;
                    }

                Complete(job, st, NexusCommandContext.Exit.Crashed, "✗ Шаг прерван перезагрузкой домена — результат неизвестен.\n", null, "interrupted");
                return;
            }

            if (st.Index >= steps.Count)
            {
                FinishTask(job, st);
                return;
            }

            var current = steps[st.Index];
            if ((st.Failed || st.Cancelled || st.TimedOut) && !current.Always)
            {
                st.Results.Add(new StepResult { Id = current.Id, Command = current.Command, Args = current.Args, Status = "skipped" });
                st.Index++;
                Persist(job, st);
                return;
            }

            if (!Substitute(current.Args, st, out var args, out var problem))
            {
                st.StepArgs = current.Args;
                Complete(job, st, NexusCommandContext.Exit.Usage, "✗ " + problem + "\n", null, "failed", problem);
                return;
            }

            // отметка «шаг начат» — до запуска: перезагрузка посреди него не должна выполнить его второй раз
            st.StepStarted = true;
            st.StepStartedAt = now;
            st.StepCounter = NexusCliJobs.Counter;
            st.StepArgs = args;
            NexusCliJobs.SetPhase(job, $"[{st.Index + 1}/{steps.Count}] {current.Id}");
            Persist(job, st);
            NexusCliJobs.Save();

            var ctx = new NexusCommandContext(current.Command, args, NexusPaths.ProjectRoot) { Task = job.Id };
            int exit;
            try
            {
                exit = NexusCliServer.RunCommand(ctx);
            }
            catch (Exception e)
            {
                ctx.Error($"{current.Command}: {e.GetType().Name}: {e.Message}");
                exit = NexusCommandContext.Exit.Crashed;
            }

            if (ctx.Job != null)
            {
                var sj = NexusCliJobs.Find(ctx.Job);
                if (sj != null && !sj.Done)
                {
                    st.StepJob = sj.Id;
                    st.StepOutput = ctx.Output;
                    if (current.Timeout > 0 && NexusCliJobs.CreatedAfter(sj, st.StepCounter)) NexusCliJobs.LimitDeadline(sj, current.Timeout);
                    Persist(job, st);
                    return;
                }

                if (sj != null)
                {
                    Complete(job, st, sj.ExitCode, ctx.Output + sj.Output, ParseData(sj.Data), null);
                    return;
                }
            }

            Complete(job, st, exit, ctx.Output, ToToken(ctx.Data), null);
        }

        private static void Complete(NexusCliJob job, TaskState st, int exit, string output, JToken data, string forcedStatus, string expectFailed = null)
        {
            var step = st.Def.Steps[st.Index];
            var result = new StepResult
            {
                Id = step.Id,
                Command = step.Command,
                Args = st.StepArgs ?? step.Args,
                ExitCode = exit,
                Seconds = Math.Round(NexusCliJobs.Now - st.StepStartedAt, 2),
                Output = Cut(output),
                Data = data,
                ExpectFailed = expectFailed,
                Artifacts = Artifacts(data),
            };

            if (forcedStatus != null)
            {
                result.Status = forcedStatus;
            }
            else
            {
                var ok = Check(step.Expect, exit, output, data, out var why);
                result.Status = ok ? "ok" : "failed";
                result.ExpectFailed = why;
            }

            if (!st.StepStarted) result.Seconds = 0;
            st.Results.Add(result);

            // провал шага останавливает задачу (кроме onError: continue); первый провал — «упавший шаг» отчёта
            if (result.Status != "ok" && (step.OnError ?? st.Def.OnError) != "continue")
            {
                st.Failed = true;
                st.FailedStep ??= step.Id;
            }

            st.Index++;
            st.StepStarted = false;
            st.StepJob = null;
            st.StepOutput = null;
            st.StepArgs = null;
            Persist(job, st);
        }

        private static void FinishTask(NexusCliJob job, TaskState st)
        {
            var status = st.Cancelled ? "cancelled" : st.TimedOut ? "timeout" : st.Failed ? "failed" : "ok";
            var exit = status == "ok" ? NexusCommandContext.Exit.Ok : status == "timeout" ? NexusCommandContext.Exit.Timeout : NexusCommandContext.Exit.Failed;
            var report = BuildReport(job, st, status, exit);
            var text = FormatReport(report);
            Cache.Remove(job.Id);
            NexusCliJobs.Finish(job, exit, text, report);
        }

        // ============================================================ отчёт

        private static JObject BuildReport(NexusCliJob job, TaskState st, string status, int exit)
        {
            var steps = new JArray();
            var artifacts = new JArray();
            var okCount = 0;
            foreach (var r in st.Results)
            {
                if (r.Status == "ok") okCount++;
                var o = new JObject
                {
                    ["id"] = r.Id,
                    ["command"] = r.Command,
                    ["status"] = r.Status,
                };
                if (r.Args != null && r.Args.Count > 0) o["args"] = new JArray(r.Args);
                if (r.Status != "skipped")
                {
                    o["exitCode"] = r.ExitCode;
                    o["seconds"] = r.Seconds;
                    if (!string.IsNullOrEmpty(r.Output)) o["output"] = r.Output;
                    if (r.Data != null && r.Data.Type != JTokenType.Null) o["data"] = r.Data;
                }

                if (r.ExpectFailed != null) o["expectFailed"] = r.ExpectFailed;
                if (r.Artifacts != null && r.Artifacts.Count > 0)
                {
                    o["artifacts"] = new JArray(r.Artifacts);
                    foreach (var a in r.Artifacts) artifacts.Add(a);
                }

                steps.Add(o);
            }

            var total = st.Def.Steps.Count;
            var seconds = Math.Round(NexusCliJobs.Now - job.StartedAt, 1);
            string summary;
            var name = st.Def.Name;
            switch (status)
            {
                case "ok":
                    summary = $"✓ {name}: {okCount}/{total} шагов за {seconds.ToString(CultureInfo.InvariantCulture)} с";
                    break;
                case "cancelled":
                    summary = $"✗ {name}: отменена, выполнено {okCount}/{total}";
                    break;
                case "timeout":
                    summary = $"✗ {name}: истёк срок, выполнено {okCount}/{total}";
                    break;
                default:
                    var failed = st.Results.Find(r => r.Id == st.FailedStep) ?? st.Results.FindLast(r => r.Status != "ok" && r.Status != "skipped");
                    var why = failed == null ? string.Empty : failed.ExpectFailed ?? FirstLine(failed.Output);
                    summary = $"✗ {name}: {okCount}/{total} шагов, упал «{failed?.Id}»" + (string.IsNullOrEmpty(why) ? string.Empty : ": " + why);
                    break;
            }

            if (st.Interference.Count > 0) summary += $" (сторонних вызовов: {st.Interference.Count})";

            var interference = new JArray();
            foreach (var i in st.Interference)
                interference.Add(new JObject { ["step"] = i.Step, ["command"] = i.Command, ["args"] = new JArray(i.Args ?? new List<string>()) });

            return new JObject
            {
                ["id"] = job.Id,
                ["name"] = name,
                ["status"] = status,
                ["exitCode"] = exit,
                ["seconds"] = seconds,
                ["failedStep"] = st.FailedStep,
                ["summary"] = summary,
                ["steps"] = steps,
                ["interference"] = interference,
                ["artifacts"] = artifacts,
            };
        }

        /// <summary>Отчёт для человека: итог, проваленные шаги с выводом, артефакты.</summary>
        public static string FormatReport(JObject report)
        {
            var sb = new StringBuilder();
            sb.Append((string)report["summary"]).Append('\n');
            if (report["steps"] is JArray steps)
                foreach (var t in steps)
                {
                    var status = (string)t["status"];
                    if (status == "ok" || status == "skipped") continue;
                    sb.Append("  ✗ ").Append((string)t["id"]).Append(" (").Append((string)t["command"]).Append("): ").Append(status);
                    if (t["expectFailed"] != null) sb.Append(" — ").Append((string)t["expectFailed"]);
                    sb.Append('\n');
                    var output = (string)t["output"];
                    if (!string.IsNullOrEmpty(output))
                        foreach (var line in output.TrimEnd('\n').Split('\n'))
                            sb.Append("    ").Append(line).Append('\n');
                }

            if (report["artifacts"] is JArray artifacts && artifacts.Count > 0)
            {
                sb.Append("артефакты:\n");
                foreach (var a in artifacts) sb.Append("  ").Append((string)a).Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>Прогресс идущей задачи (для файлового канала и task.status) или null.</summary>
        public static JObject Progress(NexusCliJob job)
        {
            if (job == null || job.Kind != Kind) return null;
            var st = State(job);
            if (st == null) return null;
            var steps = new JArray();
            for (var i = 0; i < st.Def.Steps.Count; i++)
            {
                var s = st.Def.Steps[i];
                var r = i < st.Results.Count ? st.Results[i] : null;
                steps.Add(new JObject
                {
                    ["id"] = s.Id,
                    ["command"] = s.Command,
                    ["status"] = r?.Status ?? (i == st.Index ? "running" : "pending"),
                });
            }

            return new JObject
            {
                ["id"] = job.Id,
                ["name"] = st.Def.Name,
                ["status"] = "running",
                ["step"] = Math.Min(st.Index + 1, st.Def.Steps.Count),
                ["total"] = st.Def.Steps.Count,
                ["phase"] = job.Phase,
                ["cancelled"] = st.Cancelled,
                ["steps"] = steps,
            };
        }

        public static double KeepOf(NexusCliJob job) => State(job)?.Def.Keep ?? 0;

        // ============================================================ expect и подстановки

        private static bool Check(JObject expect, int exit, string output, JToken data, out string why)
        {
            why = null;
            var expectedExit = 0;
            if (expect != null && expect["exit"] != null && expect["exit"].Type == JTokenType.Integer) expectedExit = int.TryParse(expect["exit"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : int.MinValue;
            if (exit != expectedExit)
            {
                why = expect != null && expect["exit"] != null ? $"код выхода: ожидался {expectedExit}, получен {exit}" : null;
                return false;
            }

            if (expect == null) return true;
            output ??= string.Empty;

            var contains = expect["output"]?.Type == JTokenType.String ? expect["output"].Value<string>() : null;
            if (contains != null && output.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                why = $"в выводе нет «{contains}»";
                return false;
            }

            var not = expect["notOutput"]?.Type == JTokenType.String ? expect["notOutput"].Value<string>() : null;
            if (not != null && output.IndexOf(not, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                why = $"в выводе есть «{not}»";
                return false;
            }

            if (expect["data"] is JObject wanted)
            {
                var actual = data as JObject;
                foreach (var p in wanted.Properties())
                {
                    var got = actual?[p.Name];
                    if (!SameValue(p.Value, got))
                    {
                        why = $"{p.Name}: ожидалось {Show(p.Value)}, получено {Show(got)}";
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool SameValue(JToken expected, JToken actual)
        {
            if (actual == null) return expected == null || expected.Type == JTokenType.Null;
            var numeric = expected.Type == JTokenType.Integer || expected.Type == JTokenType.Float;
            if (numeric && (actual.Type == JTokenType.Integer || actual.Type == JTokenType.Float))
                return Math.Abs(expected.Value<double>() - actual.Value<double>()) < 1e-9;
            return JToken.DeepEquals(expected, actual);
        }

        private static string Show(JToken t) => t == null ? "нет поля" : t.Type == JTokenType.String ? "«" + t.Value<string>() + "»" : t.ToString(Formatting.None);

        private static bool Substitute(List<string> args, TaskState st, out List<string> result, out string problem)
        {
            result = new List<string>(args?.Count ?? 0);
            problem = null;
            if (args == null) return true;

            foreach (var a in args)
            {
                if (a == null || a.IndexOf("${", StringComparison.Ordinal) < 0)
                {
                    result.Add(a);
                    continue;
                }

                var sb = new StringBuilder();
                var i = 0;
                while (i < a.Length)
                {
                    if (a[i] == '$' && i + 2 < a.Length && a[i + 1] == '$' && a[i + 2] == '{')
                    {
                        sb.Append("${");
                        i += 3;
                        continue;
                    }

                    if (a[i] == '$' && i + 1 < a.Length && a[i + 1] == '{')
                    {
                        var end = a.IndexOf('}', i + 2);
                        if (end < 0)
                        {
                            problem = $"незакрытая подстановка в «{a}»";
                            return false;
                        }

                        var key = a.Substring(i + 2, end - i - 2).Trim();
                        if (!Resolve(key, st, out var value, out problem)) return false;
                        sb.Append(value);
                        i = end + 1;
                        continue;
                    }

                    sb.Append(a[i]);
                    i++;
                }

                result.Add(sb.ToString());
            }

            return true;
        }

        private static bool Resolve(string key, TaskState st, out string value, out string problem)
        {
            value = null;
            problem = null;
            var dot = key.IndexOf('.');
            if (dot <= 0 || dot == key.Length - 1)
            {
                problem = $"подстановка «${{{key}}}»: нужно ${{шаг.поле}} или ${{args.ключ}}";
                return false;
            }

            var owner = key.Substring(0, dot);
            var field = key.Substring(dot + 1);
            if (owner == "args")
            {
                if (st.Def.Args != null && st.Def.Args.TryGetValue(field, out value)) return true;
                problem = $"подстановка «${{{key}}}»: нет аргумента «{field}» (--set {field}=…)";
                return false;
            }

            var r = st.Results.Find(x => x.Id == owner);
            if (r == null)
            {
                problem = $"подстановка «${{{key}}}»: шаг «{owner}» ещё не пройден";
                return false;
            }

            var token = (r.Data as JObject)?[field];
            if (token == null || token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                problem = $"подстановка «${{{key}}}»: у шага «{owner}» нет поля «{field}»";
                return false;
            }

            value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
            return true;
        }

        // ============================================================ состояние

        private static TaskState State(NexusCliJob job)
        {
            if (job == null) return null;
            if (Cache.TryGetValue(job.Id, out var st)) return st;
            var raw = job.Get("task");
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                st = JsonConvert.DeserializeObject<TaskState>(raw);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: состояние задачи", e);
                return null;
            }

            if (st != null) Cache[job.Id] = st;
            return st;
        }

        private static void Persist(NexusCliJob job, TaskState st)
        {
            Cache[job.Id] = st;
            job.Set("task", JsonConvert.SerializeObject(st, Formatting.None));
            NexusCliJobs.Touch(job);
        }

        // ============================================================ разное

        private static JToken ToToken(object data)
        {
            if (data == null) return null;
            try
            {
                return data is JToken t ? t : JToken.Parse(JsonConvert.SerializeObject(data, Formatting.None));
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: данные шага", e);
                return null;
            }
        }

        private static JToken ParseData(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JToken.Parse(json);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: данные задания шага", e);
                return null;
            }
        }

        private static List<string> Artifacts(JToken data)
        {
            if (!(data is JObject o)) return null;
            var list = new List<string>();
            foreach (var key in new[] { "path", "report" })
                if (o[key]?.Type == JTokenType.String)
                {
                    var p = o[key].Value<string>();
                    if (!string.IsNullOrEmpty(p)) list.Add(NexusTempPaths.ProjectRelative(p));
                }

            return list.Count > 0 ? list : null;
        }

        private static string Cut(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= MaxStepOutput ? s : s.Substring(0, MaxStepOutput) + "\n… (вывод обрезан)\n";
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var line = s.TrimStart().Split('\n')[0].Trim();
            return line.Length > 200 ? line.Substring(0, 200) + "…" : line;
        }

        private static string Str(JObject o, string key) => o[key]?.Type == JTokenType.String ? o[key].Value<string>() : null;

        private static double Num(JObject o, string key) =>
            o[key] != null && (o[key].Type == JTokenType.Integer || o[key].Type == JTokenType.Float) ? o[key].Value<double>() : 0;

        private static List<string> SplitSteps(string line)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            foreach (var c in line ?? string.Empty)
            {
                if (c == '"') quoted = !quoted;
                if (c == ';' && !quoted)
                {
                    parts.Add(sb.ToString());
                    sb.Clear();
                    continue;
                }

                sb.Append(c);
            }

            parts.Add(sb.ToString());
            return parts;
        }

        /// <summary>Разбить строку на аргументы: пробелы разделяют, двойные кавычки объединяют.</summary>
        public static List<string> Tokenize(string text)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;
            var any = false;
            foreach (var c in text ?? string.Empty)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                    continue;
                }

                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (any) list.Add(sb.ToString());
                    sb.Clear();
                    any = false;
                    continue;
                }

                sb.Append(c);
                any = true;
            }

            if (any) list.Add(sb.ToString());
            return list;
        }
    }
}
