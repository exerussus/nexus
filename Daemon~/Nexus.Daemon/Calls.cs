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
        /// <summary>Клиент попросил отменить (файл cancel/ канала efc).</summary>
        public volatile bool CancelRequested;
        /// <summary>Редактору отправлен job.cancel.</summary>
        public volatile bool CancelSent;
        /// <summary>Пакет, от имени которого идёт шаг: проходит блокировку своего пакета.</summary>
        public string LockOwner;
        /// <summary>Отказ из-за чужой блокировки: id держателя.</summary>
        public string BusyWith;
        /// <summary>Вышел срок вызова: редактору отправлена просьба отменить задание, итог — код 5.</summary>
        public volatile bool TimedOut;
        public int ExitCode;
        public string Output = string.Empty;
        public string Result;
        /// <summary>Итог отдан в файловый канал (done/) и отпущен в памяти: вызов живёт до keep, вывод — уже нет.</summary>
        public volatile bool Released;
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
                o["output"] = Released ? "(итог отдан в файловый канал: NexusTemp/cli/done/" + Id + ".json)\n" : Output;
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

        /// <summary>
        /// Демон уходит (обновление): новые вызовы не принимаются, идущие доводятся до конца (<see cref="Program"/>
        /// ждёт <see cref="Active"/> с ограничением).
        /// </summary>
        public static volatile bool Draining;

        /// <summary>Машинная причина ответа Bridge: поле <c>reason</c> ответа или его данных (<c>result</c>).</summary>
        public static string Reason(JsonObject response)
        {
            var reason = Json.Str(response, "reason");
            if (reason != null) return reason;
            var result = Json.Str(response, "result");
            return result != null && result.Contains("\"reason\"", StringComparison.Ordinal) ? Json.Str(Json.TryParseObject(result), "reason") : null;
        }

        /// <summary>Отказ Bridge «неверный токен» (редактор перезапустил сервер): reason, а у старых редакторов — текст.</summary>
        private static bool BadToken(int exit, JsonObject response) =>
            exit == 2 && (Reason(response) == "bad-token" || (Json.Str(response, "output") ?? string.Empty).StartsWith("Неверный токен", StringComparison.Ordinal));

        /// <summary>Итог вызова отдан в done/: вывод и данные в памяти больше не нужны.</summary>
        public static void Release(Call call)
        {
            if (call == null || call.State != "done") return;
            call.Released = true;
            call.Output = string.Empty;
            call.Result = null;
        }

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

        /// <summary>
        /// Блокировка пакета efc: чужие меняющие вызовы — отказ (--force проходит, читающие — тоже). true — вызов
        /// завершён отказом. Проверяется и перед каждой отправкой в редактор: вызов мог ждать перезагрузку домена,
        /// пока кто-то взял блокировку.
        /// </summary>
        private static bool RefusedByLock(Call call)
        {
            var p = call.Project;
            var holder = p.LockHolder;
            if (holder == null || call.LockOwner == holder || call.Id == holder || call.Force || p.ReadOnly(call.Command)) return false;
            call.BusyWith = holder;
            Finish(call, 1, $"✗ Идёт пакет {holder} с блокировкой — дождитесь его, отмените или повторите с --force.\n");
            return true;
        }

        private static async Task Run(Call call)
        {
            var p = call.Project;

            if (RefusedByLock(call)) return;

            if (call.CancelRequested)
            {
                Finish(call, 7, "✗ Отменено до начала выполнения.\n");
                return;
            }

            // команды демона (dialog.*, trash.clean, ping канала) — без редактора: они нужны как раз когда его главный поток стоит
            if (DaemonCommands.Owns(call))
            {
                call.State = "running";
                SetPhase(call, "выполняет демон");
                try
                {
                    // предел — timeout вызова: Win32-вызов к зависшему окну не держит вызов вечно (поток пула останется ждать)
                    var work = Task.Run(() => DaemonCommands.Run(call));
                    if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(call.Timeout))) != work)
                        Finish(call, 5, $"✗ Команда демона не завершилась за {call.Timeout} с (окно не отвечает?).\n");
                    else
                    {
                        var (exit, output, result) = await work;
                        Finish(call, exit, output, result);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("команда демона " + call.Command, e);
                    Finish(call, 4, $"✗ Демон: {e.GetType().Name}: {e.Message}\n");
                }

                return;
            }

            var deadline = DateTime.UtcNow.AddSeconds(call.Timeout);
            p.Enter();
            try
            {
                // ----------------------------------------------------- доставка
                JsonObject response = null;
                while (response == null)
                {
                    if (call.CancelRequested) { Finish(call, 7, "✗ Отменено до отправки в редактор.\n"); return; }
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

                    // блокировку могли взять, пока вызов ждал редактор
                    if (RefusedByLock(call))
                    {
                        p.Slots.Release();
                        return;
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

                    if (BadToken(exit, r))
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
                    var code = (int)Json.Num(response, "exitCode", 1);
                    // help без аргументов: дописать команды демона (работают и при зависшем редакторе)
                    if (call.Command == "help" && call.Args.Length == 0 && code == 0) first += DaemonCommands.HelpSection();
                    Finish(call, code, first, Json.Str(response, "result"));
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
                    // срок вызова: попросить редактор отменить задание (efc, SPEC 6); через 30 с — итог 5 в любом случае
                    if (DateTime.UtcNow > deadline && !call.CancelRequested)
                    {
                        call.TimedOut = true;
                        call.CancelRequested = true;
                    }

                    await Task.Delay(300);
                    if (!p.Ready) { SetPhase(call, $"задание {job}: редактор перезагружает домен"); continue; }

                    // отмена: попросить редактор отменить задание (один раз), итог — когда задание закончится
                    if (call.CancelRequested && !call.CancelSent)
                    {
                        var (cs, _, _) = await Bridge.Send(p, Bridge.Request("job.cancel", new[] { job }, p.Path, 10), 10, CancellationToken.None);
                        if (cs == BridgeStatus.Ok)
                        {
                            call.CancelSent = true;
                            SetPhase(call, $"задание {job}: отменяется");
                        }
                    }

                    var (status, r, _) = await Bridge.Send(p, Bridge.Request("job.status", new[] { job }, p.Path, 10), 10, CancellationToken.None);
                    if (status != BridgeStatus.Ok) continue; // перезагрузка домена — задание живёт в SessionState
                    var exit = (int)Json.Num(r, "exitCode", 1);
                    // 6 — занят; 5 — главный поток Unity не успел ответить (импорт, компиляция): задание живо, ждём.
                    // Но только если ответ — не итог задания: job.status завершённого задания несёт поле job и его
                    // собственный код (задание, прерванное сроком, кончается кодом 5) — это окончательный ответ
                    if ((exit == 6 || exit == 5) && string.IsNullOrEmpty(Json.Str(r, "job"))) continue;
                    if (BadToken(exit, r))
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
                    if (exit == 1 && (Reason(r) == "no-job" || output.Contains("Нет задания «", StringComparison.Ordinal)))
                    {
                        Finish(call, 4, first + "✗ Задание пропало (редактор перезапущен?) — результат неизвестен.\n");
                        return;
                    }

                    // отменённое по просьбе задание — код 7, если оно не успело закончиться успешно
                    if (call.CancelSent && exit != 0) exit = call.TimedOut ? 5 : 7;
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
