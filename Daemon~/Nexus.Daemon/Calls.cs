using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>Вызов команды в проекте: принят демоном сразу, выполняется, когда редактор готов.</summary>
    internal sealed class Call
    {
        public string Id;
        public Project Project;
        public string Command;
        public string[] Args = Array.Empty<string>();
        public string Cwd;
        public int Timeout;
        public double Keep;
        public bool Force;
        public string Source;

        public volatile string State = "queued"; // queued | running | done
        public volatile string Phase = "в очереди";
        public volatile string Job;
        public int ExitCode;
        public string Output = string.Empty;
        public string Result;
        public DateTime Created = DateTime.UtcNow;
        public DateTime Finished;

        public readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Вызывается при смене фазы (файловый адаптер обновляет active/).</summary>
        public Action<Call> PhaseChanged;

        public double Seconds => ((State == "done" ? Finished : DateTime.UtcNow) - Created).TotalSeconds;

        public JsonObject ToJson()
        {
            var o = new JsonObject
            {
                ["id"] = Id,
                ["project"] = Project?.Id,
                ["command"] = Command,
                ["state"] = State,
                ["phase"] = Phase,
                ["seconds"] = Math.Round(Seconds, 2),
            };
            if (Job != null) o["job"] = Job;
            if (State == "done")
            {
                o["exitCode"] = ExitCode;
                o["ok"] = ExitCode == 0;
                o["output"] = Output;
                if (Result != null) o["result"] = Result;
            }

            return o;
        }
    }

    /// <summary>
    /// Вызовы — компонент демона: живут в памяти до keep (не меньше 2 минут после завершения). Демон не
    /// перезагружается, поэтому ожидание перезагрузки домена — просто цикл.
    /// </summary>
    internal static class Calls
    {
        public const int DefaultTimeout = 600;

        private static readonly ConcurrentDictionary<string, Call> ById = new ConcurrentDictionary<string, Call>(StringComparer.Ordinal);

        public static Call Find(string id) => id != null && ById.TryGetValue(id, out var c) ? c : null;

        public static IEnumerable<Call> Active => ById.Values.Where(c => c.State != "done");

        /// <summary>Самый долгий keep: неделя (больше — переполнение дат и мусор на диске).</summary>
        public const double MaxKeep = 7 * 24 * 3600;

        public static Call Start(Project project, string command, string[] args, string cwd, int timeout, double keep, bool force, string source, string id = null, Action<Call> phaseChanged = null)
        {
            var call = Create(project, command, args, cwd, timeout, keep, force, source, id, phaseChanged);
            Begin(call);
            return call;
        }

        /// <summary>Создать вызов без запуска: вызывающий сначала регистрирует его и пишет свои файлы, затем <see cref="Begin"/>.</summary>
        public static Call Create(Project project, string command, string[] args, string cwd, int timeout, double keep, bool force, string source, string id = null, Action<Call> phaseChanged = null)
        {
            var call = new Call
            {
                Id = id ?? NewId(),
                Project = project,
                Command = command,
                Args = args ?? Array.Empty<string>(),
                Cwd = cwd ?? project.Path,
                Timeout = timeout > 0 ? timeout : DefaultTimeout,
                Keep = Math.Clamp(double.IsFinite(keep) ? keep : 0, 0, MaxKeep),
                Force = force,
                Source = source,
                PhaseChanged = phaseChanged,
            };
            ById[call.Id] = call;
            return call;
        }

        public static void Begin(Call call) => _ = Task.Run(() => Run(call));

        public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

        private static void SetPhase(Call call, string phase)
        {
            if (phase == null || call.Phase == phase) return;
            call.Phase = phase;
            if (call.Project != null) call.Project.CallPhase = phase;
            try
            {
                call.PhaseChanged?.Invoke(call);
            }
            catch (Exception e)
            {
                Log.Error("фаза вызова", e);
            }
        }

        private static async Task Run(Call call)
        {
            var p = call.Project;
            var deadline = DateTime.UtcNow.AddSeconds(call.Timeout);
            p.Enter();
            try
            {
                // ----------------------------------------------------- доставка
                JsonObject response = null;
                while (response == null)
                {
                    if (p.Removed) { Finish(call, 10, "✗ CLI в проекте выключен.\n"); return; }
                    if (p.State == "closed") { Finish(call, 10, "✗ Редактор проекта закрыт.\n"); return; }
                    if (DateTime.UtcNow > deadline) { Finish(call, 5, $"✗ Редактор не принял команду за {call.Timeout} с (фаза: {p.Phase}).\n"); return; }

                    if (!p.Ready)
                    {
                        SetPhase(call, p.State == "stalled" ? "редактор не отвечает — бужу" : p.State == "reloading" ? "редактор перезагружает домен" : "в очереди");
                        await Task.Delay(250);
                        continue;
                    }

                    // слот — только на время разговора с Unity: ждущие вызовы друг друга не блокируют
                    if (!await p.Slots.WaitAsync(250))
                    {
                        SetPhase(call, "в очереди");
                        continue;
                    }

                    BridgeStatus status;
                    JsonObject r;
                    string error;
                    try
                    {
                        call.State = "running";
                        SetPhase(call, "выполняется");
                        var remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalSeconds);
                        var request = Bridge.Request(call.Command, call.Args, call.Cwd, remaining, call.Force);
                        (status, r, error) = await Bridge.Send(p, request, remaining, CancellationToken.None);
                    }
                    finally
                    {
                        p.Slots.Release();
                    }

                    if (status == BridgeStatus.NotDelivered)
                    {
                        SetPhase(call, "редактор перезагружает домен");
                        await Task.Delay(300);
                        continue;
                    }

                    if (status == BridgeStatus.Lost)
                    {
                        Finish(call, 4, $"✗ Связь с редактором оборвалась ({error}) — результат неизвестен, проверьте status.\n");
                        return;
                    }

                    var exit = (int)Json.Num(r, "exitCode", 1);
                    var output = Json.Str(r, "output") ?? string.Empty;
                    if (exit == 6)
                    {
                        // «занят»: команда не выполнялась (перезагрузка, очередь) — повторить
                        SetPhase(call, "редактор занят");
                        await Task.Delay(500);
                        continue;
                    }

                    if (exit == 2 && output.StartsWith("Неверный токен", StringComparison.Ordinal))
                    {
                        Registry.Refresh();
                        await Task.Delay(300);
                        continue;
                    }

                    response = r;
                }

                p.MarkAlive();
                var first = Json.Str(response, "output") ?? string.Empty;
                var job = Json.Str(response, "job");
                if (!Json.Bool(response, "pending") || string.IsNullOrEmpty(job))
                {
                    Finish(call, (int)Json.Num(response, "exitCode", 1), first, Json.Str(response, "result"));
                    return;
                }

                // ----------------------------------------------------- задание: ждём через job.status
                call.Job = job;
                SetPhase(call, "задание " + job);
                var hardStop = deadline.AddSeconds(30);
                while (true)
                {
                    if (p.Removed) { Finish(call, 10, first + "✗ CLI в проекте выключен.\n"); return; }
                    if (p.State == "closed") { Finish(call, 4, first + "✗ Редактор закрылся во время задания — результат неизвестен.\n"); return; }
                    if (DateTime.UtcNow > hardStop) { Finish(call, 5, first + $"✗ Задание {job} не завершилось за {call.Timeout} с (фаза: {call.Phase}).\n"); return; }

                    await Task.Delay(300);
                    if (!p.Ready) { SetPhase(call, $"задание {job}: редактор перезагружает домен"); continue; }

                    var (status, r, _) = await Bridge.Send(p, Bridge.Request("job.status", new[] { job }, p.Path, 10), 10, CancellationToken.None);
                    if (status != BridgeStatus.Ok) continue; // перезагрузка домена — задание живёт в SessionState
                    var exit = (int)Json.Num(r, "exitCode", 1);
                    // 6 — занят; 5 — главный поток Unity не успел ответить (импорт, компиляция): задание живо, ждём
                    if (exit == 6 || exit == 5) continue;
                    if (exit == 2 && (Json.Str(r, "output") ?? string.Empty).StartsWith("Неверный токен", StringComparison.Ordinal))
                    {
                        Registry.Refresh();
                        continue;
                    }

                    p.MarkAlive();

                    if (Json.Bool(r, "pending"))
                    {
                        var data = Json.TryParseObject(Json.Str(r, "result"));
                        var phase = Json.Str(data, "phase");
                        if (phase != null) SetPhase(call, phase);
                        continue;
                    }

                    var output = Json.Str(r, "output") ?? string.Empty;
                    if (exit == 1 && output.Contains("Нет задания «", StringComparison.Ordinal))
                    {
                        Finish(call, 4, first + "✗ Задание пропало (редактор перезапущен?) — результат неизвестен.\n");
                        return;
                    }

                    Finish(call, exit, first + (output.Length > 0 && !output.EndsWith("\n") ? output + "\n" : output), Json.Str(r, "result"));
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error("вызов " + call.Command, e);
                Finish(call, 4, $"✗ Демон: {e.GetType().Name}: {e.Message}\n");
            }
            finally
            {
                p.Leave();
                if (p.InFlight == 0) p.CallPhase = null;
            }
        }

        private static void Finish(Call call, int exit, string output, string result = null)
        {
            if (call.State == "done") return;
            call.ExitCode = exit;
            call.Output = output ?? string.Empty;
            call.Result = result;
            call.Finished = DateTime.UtcNow;
            call.Phase = "готово";
            call.State = "done";
            call.Done.TrySetResult(true);
            try
            {
                call.PhaseChanged?.Invoke(call);
            }
            catch (Exception e)
            {
                Log.Error("итог вызова", e);
            }
        }

        /// <summary>Убрать завершённые вызовы старше keep (минимум 2 минуты).</summary>
        public static void Sweep()
        {
            var now = DateTime.UtcNow;
            foreach (var c in ById.Values)
                if (c.State == "done" && (now - c.Finished).TotalSeconds > Math.Max(120, c.Keep))
                    ById.TryRemove(c.Id, out _);
        }
    }
}
