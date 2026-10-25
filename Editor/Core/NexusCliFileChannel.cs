using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Файловый вход CLI — компонент сервера с явным владельцем (этот класс): для агентов без сети до редактора.
    /// Вызов — файл <c>NexusTemp/cli/requests/&lt;id&gt;.json</c>, который сервер переносит в <c>active/</c>, выполняет
    /// тем же путём, что TCP, и кладёт итог в <c>done/</c>. Задачи — так же, в <c>cli/tasks/…</c>.
    /// </summary>
    /// <remarks>
    /// <para>Работает только в главном потоке, опросом из цикла сервера (не чаще 5 раз в секунду); без
    /// FileSystemWatcher и фоновых потоков. Пульс <c>alive.json</c> — раз в 2 с.</para>
    /// <para>Выключено — значит выключено: остановка CLI удаляет <c>NexusTemp/cli</c>; перед перезагрузкой домена
    /// файлы остаются (новые вызовы ждут), пульс переходит в <c>reloading</c>.</para>
    /// </remarks>
    internal static class NexusCliFileChannel
    {
        private const double PollInterval = 0.2;
        private const double BeatInterval = 2.0;
        private const double SweepInterval = 10.0;
        private const double PhaseInterval = 0.5;
        private const int MaxPerTick = 4;
        private const long MaxRequestBytes = 1 << 20;
        private const int MaxOutputChars = 512 * 1024;

        private const string SeqKey = "Exerussus.Nexus.Cli.FileSeq";
        private const string ProcessedKey = "Exerussus.Nexus.Cli.FileProcessed";
        private const string TrackKey = "Exerussus.Nexus.Cli.FileTracking";

        /// <summary>Итоги без keep, которые клиент так и не забрал (ушёл по сроку), — удаляются через час.</summary>
        private const double OrphanAge = 3600;

        private static readonly Regex SafeId = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        private sealed class Tracked
        {
            public string Id;
            public bool IsTask;
            public string Job;
            public string InitialOutput;
            public double Keep;
            public string Phase;
            public double LastWrite;
        }

        private static readonly Dictionary<string, Tracked> Tracking = new Dictionary<string, Tracked>(StringComparer.Ordinal);

        // итоги, которые не записались (файл занят клиентом): повтор на каждом опросе, active/ остаётся до записи
        private sealed class Delivery
        {
            public string Path;
            public string Content;
            public string Remove;
        }

        private static readonly List<Delivery> Retry = new List<Delivery>();
        private static bool _active;
        private static double _nextPoll;
        private static double _nextBeat;
        private static double _nextSweep;

        private static string Cli => NexusTempPaths.Cli;
        private static string Requests => Path.Combine(Cli, "requests");
        private static string Active => Path.Combine(Cli, "active");
        private static string Done => Path.Combine(Cli, "done");
        private static string TaskRequests => Path.Combine(Cli, "tasks", "requests");
        private static string TaskActive => Path.Combine(Cli, "tasks", "active");
        private static string TaskDone => Path.Combine(Cli, "tasks", "done");
        private static string Alive => Path.Combine(Cli, "alive.json");

        public static bool IsActive => _active;

        public static int Processed => SessionState.GetInt(ProcessedKey, 0);

        // ============================================================ жизненный цикл (владелец — сервер CLI)

        internal static void Activate()
        {
            if (_active) return;
            try
            {
                NexusTempPaths.EnsureRoot();
                foreach (var dir in new[] { Requests, Active, Done, TaskRequests, TaskActive, TaskDone }) Directory.CreateDirectory(dir);
                InstallClient();
                Tracking.Clear();
                Retry.Clear();
                _active = true;
                Beat("ready");
                RecoverTails();
                Sweep();
                var now = NexusCliJobs.Now;
                _nextPoll = now;
                _nextBeat = now + BeatInterval;
                _nextSweep = now + SweepInterval;
            }
            catch (Exception e)
            {
                _active = false;
                NexusDiagnostics.Error("CLI: файловый канал не запустился", e);
            }
        }

        internal static void Deactivate(bool reloading)
        {
            var was = _active;
            _active = false;
            if (reloading && was) SaveTracking();
            Tracking.Clear();
            Retry.Clear();

            if (reloading)
            {
                if (was) Beat("reloading");
                return;
            }

            SessionState.EraseString(TrackKey);
            SessionState.EraseInt(SeqKey);
            SessionState.EraseInt(ProcessedKey);
            try
            {
                if (Directory.Exists(Cli)) Directory.Delete(Cli, true);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: удаление файлового канала", e);
            }
        }

        /// <summary>Строка для status / Manage.</summary>
        public static string Describe()
        {
            if (!_active) return "файловый канал: выкл";
            var task = NexusCliJobs.Running(NexusCliTasks.Kind);
            return $"файловый канал: вкл (NexusTemp), обработано: {Processed}" + (task != null ? $", задача: {task.Get("name")} {task.Phase}" : string.Empty);
        }

        // ============================================================ цикл (главный поток, из Pump сервера)

        internal static void Tick()
        {
            if (!_active) return;
            var now = NexusCliJobs.Now;

            if (now >= _nextBeat)
            {
                _nextBeat = now + BeatInterval;
                Beat("ready");
            }

            if (now >= _nextPoll)
            {
                _nextPoll = now + PollInterval;
                try
                {
                    Redeliver();
                    PollRequests();
                    PollTasks();
                    Follow(now);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("CLI: файловый канал", e);
                }
            }

            if (now >= _nextSweep)
            {
                _nextSweep = now + SweepInterval;
                try
                {
                    Sweep();
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("CLI: уборка файлового канала", e);
                }
            }
        }

        /// <summary>
        /// Главный поток сейчас надолго займётся (синхронный Refresh, импорт): пульс «busy», чтобы клиент не счёл
        /// редактор зависшим. Состояние держится до следующего обычного пульса.
        /// </summary>
        public static void Busy()
        {
            if (!_active) return;
            Beat("busy");
            _nextBeat = NexusCliJobs.Now + BeatInterval;
        }

        private static string _doing;

        /// <summary>
        /// Что сейчас делает главный поток (команда CLI) — пульс пишется сразу: зависнет команда — сторож и клиент
        /// покажут её. null — закончил.
        /// </summary>
        public static void Note(string doing)
        {
            _doing = doing;
            if (!_active || doing == null) return;
            Beat("ready");
            _nextBeat = NexusCliJobs.Now + BeatInterval;
        }

        private static void PollRequests()
        {
            var files = Sorted(Requests);
            var taken = 0;
            foreach (var path in files)
            {
                if (taken >= MaxPerTick || !_active) break;
                var id = Path.GetFileNameWithoutExtension(path);
                if (!SafeId.IsMatch(id)) continue;

                // захват до выполнения: после перезагрузки домена вызов не выполнится второй раз
                var active = Path.Combine(Active, id + ".json");
                if (!TryMove(path, active)) continue;
                taken++;
                Count();

                var json = ReadSmall(active);
                if (!NexusCliServer.TryParse(json, out var request, out var error))
                {
                    error.Id = id;
                    FinishCall(id, error, ParseKeep(json));
                    continue;
                }

                request.Id = id;
                NexusCliServer.Response response;
                try
                {
                    response = NexusCliServer.ProcessRequest(request);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Error("CLI: файловый вызов", e);
                    response = new NexusCliServer.Response { ExitCode = NexusCommandContext.Exit.Crashed, Output = "✗ " + e.Message, Project = NexusCliServer.ProjectName };
                }

                response.Id = id;
                if (response.Pending && response.Job != null)
                {
                    var t = new Tracked { Id = id, Job = response.Job, InitialOutput = response.Output, Keep = request.Keep };
                    Tracking[Key(t)] = t;
                    SaveTracking();
                    WriteActiveCall(t, request);
                }
                else
                {
                    FinishCall(id, response, request.Keep);
                }
            }
        }

        private static void PollTasks()
        {
            if (NexusCliJobs.Running(NexusCliTasks.Kind) != null) return; // задачи — по одной; следующая ждёт в requests/
            foreach (var path in Sorted(TaskRequests))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!SafeId.IsMatch(id)) continue;
                var active = Path.Combine(TaskActive, id + ".json");
                if (!TryMove(path, active)) continue;
                Count();

                var json = ReadSmall(active);
                JObject o = null;
                try
                {
                    o = json == null ? null : JObject.Parse(json);
                }
                catch
                {
                    // ниже — ответ «неверный запрос»
                }

                var keep = o?["keep"] != null && (o["keep"].Type == JTokenType.Integer || o["keep"].Type == JTokenType.Float) ? o["keep"].Value<double>() : 0;
                if (o == null)
                {
                    FinishTaskError(id, NexusCommandContext.Exit.Usage, "Неверный запрос.", keep);
                    return;
                }

                var token = NexusCliServer.Token;
                var sent = o["token"]?.Type == JTokenType.String ? o["token"].Value<string>() : null;
                if (token == null || sent != token)
                {
                    FinishTaskError(id, NexusCommandContext.Exit.Usage, "Неверный токен: клиент читает устаревший файл экземпляра — повторите.", keep);
                    return;
                }

                try
                {
                    var def = NexusCliTasks.Parse(o, "task");
                    var job = NexusCliTasks.Start(def);
                    var t = new Tracked { Id = id, IsTask = true, Job = job.Id, Keep = def.Keep };
                    Tracking[Key(t)] = t;
                    SaveTracking();
                    WriteActiveTask(t, job);
                }
                catch (NexusCommandException e)
                {
                    FinishTaskError(id, e.ExitCode, e.Message, keep);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Error("CLI: файловая задача", e);
                    FinishTaskError(id, NexusCommandContext.Exit.Crashed, $"{e.GetType().Name}: {e.Message}", keep);
                }

                return; // одна задача за тик
            }
        }

        /// <summary>Следить за вызовами и задачами в работе: фаза — в active/, конец — итог в done/.</summary>
        private static void Follow(double now)
        {
            if (Tracking.Count == 0) return;
            foreach (var t in new List<Tracked>(Tracking.Values))
            {
                var job = NexusCliJobs.Find(t.Job);
                if (job == null)
                {
                    Tracking.Remove(Key(t));
                    SaveTracking();
                    if (t.IsTask) FinishTaskError(t.Id, NexusCommandContext.Exit.Crashed, "Задача пропала (перезапуск редактора?) — результат неизвестен.", t.Keep);
                    else FinishCall(t.Id, Interrupted(t.Id, t.Job), t.Keep);
                    continue;
                }

                if (job.Done)
                {
                    Tracking.Remove(Key(t));
                    SaveTracking();
                    if (t.IsTask) FinishTask(t, job);
                    else FinishCall(t.Id, FromJob(t, job), t.Keep);
                    continue;
                }

                if (job.Phase != t.Phase && now - t.LastWrite >= PhaseInterval)
                {
                    t.Phase = job.Phase;
                    t.LastWrite = now;
                    if (t.IsTask) WriteActiveTask(t, job);
                    else UpdateActivePhase(t, job);
                }
            }
        }

        // ============================================================ запись итогов

        private static void FinishCall(string id, NexusCliServer.Response response, double keep)
        {
            response.Id = id;
            response.State = "done";
            if (keep > 0) response.Expires = DateTime.UtcNow.AddSeconds(keep).ToString("o", CultureInfo.InvariantCulture);
            if (response.Output != null && response.Output.Length > MaxOutputChars)
                response.Output = response.Output.Substring(0, MaxOutputChars) + "\n… (вывод обрезан)\n";
            Deliver(Path.Combine(Done, id + ".json"), JsonConvert.SerializeObject(response, Formatting.None, NexusCliServer.Wire), Path.Combine(Active, id + ".json"));
        }

        private static NexusCliServer.Response FromJob(Tracked t, NexusCliJob job) => new NexusCliServer.Response
        {
            Id = t.Id,
            ExitCode = job.ExitCode,
            Ok = job.ExitCode == NexusCommandContext.Exit.Ok,
            Output = (t.InitialOutput ?? string.Empty) + (job.Output ?? string.Empty),
            Result = job.Data,
            Project = NexusCliServer.ProjectName,
            Job = job.Id,
            Seconds = Math.Round(NexusCliJobs.Now - job.StartedAt, 2),
        };

        private static NexusCliServer.Response Interrupted(string id, string jobId) => new NexusCliServer.Response
        {
            Id = id,
            ExitCode = NexusCommandContext.Exit.Crashed,
            Output = "✗ Команда прервана перезагрузкой домена — результат неизвестен, проверьте status.\n",
            Project = NexusCliServer.ProjectName,
            Job = jobId,
        };

        private static void FinishTask(Tracked t, NexusCliJob job)
        {
            JObject report;
            try
            {
                report = string.IsNullOrEmpty(job.Data) ? new JObject() : JObject.Parse(job.Data);
            }
            catch
            {
                report = new JObject();
            }

            report["job"] = job.Id;
            report["id"] = t.Id;
            report["state"] = "done";
            report["exitCode"] = job.ExitCode;
            if (report["summary"] == null) report["summary"] = (job.Output ?? string.Empty).TrimEnd('\n');
            if (t.Keep > 0) report["expires"] = DateTime.UtcNow.AddSeconds(t.Keep).ToString("o", CultureInfo.InvariantCulture);
            Deliver(Path.Combine(TaskDone, t.Id + ".json"), report.ToString(Formatting.None), Path.Combine(TaskActive, t.Id + ".json"));
        }

        private static void FinishTaskError(string id, int exit, string message, double keep)
        {
            var report = new JObject
            {
                ["id"] = id,
                ["state"] = "done",
                ["status"] = "failed",
                ["exitCode"] = exit,
                ["summary"] = "✗ " + message,
                ["steps"] = new JArray(),
                ["artifacts"] = new JArray(),
            };
            if (keep > 0) report["expires"] = DateTime.UtcNow.AddSeconds(keep).ToString("o", CultureInfo.InvariantCulture);
            Deliver(Path.Combine(TaskDone, id + ".json"), report.ToString(Formatting.None), Path.Combine(TaskActive, id + ".json"));
        }

        /// <summary>Итог — в done/, затем убрать active/. Не записалось — повтор на следующих опросах, active/ остаётся.</summary>
        private static void Deliver(string path, string content, string remove)
        {
            if (WriteAtomic(path, content))
            {
                TryDelete(remove);
                return;
            }

            Retry.Add(new Delivery { Path = path, Content = content, Remove = remove });
        }

        private static void Redeliver()
        {
            if (Retry.Count == 0) return;
            for (var i = Retry.Count - 1; i >= 0; i--)
            {
                var d = Retry[i];
                if (!WriteAtomic(d.Path, d.Content)) continue;
                TryDelete(d.Remove);
                Retry.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------------- карта вызовов (переживает перезагрузку)

        private sealed class SavedTrack
        {
            public string Id;
            public bool IsTask;
            public string Job;
            public string InitialOutput;
            public double Keep;
        }

        private static void SaveTracking()
        {
            try
            {
                var list = new List<SavedTrack>();
                foreach (var t in Tracking.Values) list.Add(new SavedTrack { Id = t.Id, IsTask = t.IsTask, Job = t.Job, InitialOutput = t.InitialOutput, Keep = t.Keep });
                SessionState.SetString(TrackKey, JsonConvert.SerializeObject(list, Formatting.None));
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: карта файловых вызовов", e);
            }
        }

        private static Dictionary<string, SavedTrack> LoadTracking()
        {
            var map = new Dictionary<string, SavedTrack>(StringComparer.Ordinal);
            try
            {
                var raw = SessionState.GetString(TrackKey, string.Empty);
                if (string.IsNullOrEmpty(raw)) return map;
                foreach (var s in JsonConvert.DeserializeObject<List<SavedTrack>>(raw) ?? new List<SavedTrack>())
                    if (s?.Id != null)
                        map[(s.IsTask ? "t:" : "c:") + s.Id] = s;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: карта файловых вызовов", e);
            }

            return map;
        }

        private static void WriteActiveCall(Tracked t, NexusCliServer.Request request)
        {
            var o = new JObject
            {
                ["id"] = t.Id,
                ["state"] = "running",
                ["command"] = request.Command,
                ["args"] = new JArray(request.Args ?? Array.Empty<string>()),
                ["job"] = t.Job,
                ["keep"] = t.Keep,
                ["output"] = t.InitialOutput ?? string.Empty,
                ["phase"] = NexusCliJobs.Find(t.Job)?.Phase,
            };
            t.Phase = (string)o["phase"];
            WriteAtomic(Path.Combine(Active, t.Id + ".json"), o.ToString(Formatting.None));
        }

        private static void UpdateActivePhase(Tracked t, NexusCliJob job)
        {
            var path = Path.Combine(Active, t.Id + ".json");
            JObject o;
            try
            {
                o = JObject.Parse(File.ReadAllText(path));
            }
            catch
            {
                o = new JObject { ["id"] = t.Id, ["state"] = "running", ["job"] = t.Job, ["keep"] = t.Keep, ["output"] = t.InitialOutput ?? string.Empty };
            }

            o["phase"] = job.Phase;
            WriteAtomic(path, o.ToString(Formatting.None));
        }

        private static void WriteActiveTask(Tracked t, NexusCliJob job)
        {
            var o = NexusCliTasks.Progress(job) ?? new JObject { ["status"] = "running" };
            o["job"] = job.Id;
            o["id"] = t.Id;
            o["state"] = "running";
            o["keep"] = t.Keep;
            t.Phase = job.Phase;
            WriteAtomic(Path.Combine(TaskActive, t.Id + ".json"), o.ToString(Formatting.None));
        }

        // ============================================================ хвосты, уборка, пульс

        /// <summary>После перезагрузки домена или старта: вызовы в active/ — продолжить или ответить «прервано».</summary>
        private static void RecoverTails()
        {
            // источник правды — карта из SessionState: файл active/ мог не обновиться (занят клиентом)
            var saved = LoadTracking();
            foreach (var path in SafeFiles(Active))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                var o = ReadJson(path);
                saved.TryGetValue("c:" + id, out var s);
                var jobId = s?.Job ?? (o?["job"]?.Type == JTokenType.String ? o["job"].Value<string>() : null);
                var keep = s?.Keep ?? (o?["keep"]?.Type == JTokenType.Float || o?["keep"]?.Type == JTokenType.Integer ? o["keep"].Value<double>() : 0);
                var job = jobId != null ? NexusCliJobs.Find(jobId) : null;
                var output = s?.InitialOutput ?? (o?["output"]?.Type == JTokenType.String ? o["output"].Value<string>() : null);
                var t = new Tracked { Id = id, Job = jobId, InitialOutput = output, Keep = keep };
                if (job == null) FinishCall(id, Interrupted(id, jobId), keep);
                else if (job.Done) FinishCall(id, FromJob(t, job), keep);
                else Tracking[Key(t)] = t;
            }

            foreach (var path in SafeFiles(TaskActive))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                var o = ReadJson(path);
                saved.TryGetValue("t:" + id, out var s);
                var jobId = s?.Job ?? (o?["job"]?.Type == JTokenType.String ? o["job"].Value<string>() : null);
                var keep = s?.Keep ?? (o?["keep"]?.Type == JTokenType.Float || o?["keep"]?.Type == JTokenType.Integer ? o["keep"].Value<double>() : 0);
                var job = jobId != null ? NexusCliJobs.Find(jobId) : null;
                var t = new Tracked { Id = id, IsTask = true, Job = jobId, Keep = keep };
                if (job == null) FinishTaskError(id, NexusCommandContext.Exit.Crashed, "Задача прервана перезапуском — результат неизвестен.", keep);
                else if (job.Done) FinishTask(t, job);
                else Tracking[Key(t)] = t;
            }

            SaveTracking();
        }

        /// <summary>
        /// Удалить итоги с истёкшим keep; итоги без keep обычно удаляет клиент — брошенные (клиент ушёл по сроку)
        /// удаляются через <see cref="OrphanAge"/>. Плюс недописанные .tmp старше 10 минут.
        /// </summary>
        private static void Sweep()
        {
            var now = DateTime.UtcNow;
            foreach (var dir in new[] { Done, TaskDone })
                foreach (var path in SafeFiles(dir))
                {
                    var o = ReadJson(path);
                    var raw = o?["expires"]?.Type == JTokenType.String ? o["expires"].Value<string>() : null;
                    if (raw == null)
                    {
                        if (Age(path, now) > OrphanAge) TryDelete(path);
                        continue;
                    }

                    if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expires) && expires < now)
                        TryDelete(path);
                }

            foreach (var dir in new[] { Requests, Active, Done, TaskRequests, TaskActive, TaskDone })
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var tmp in Directory.GetFiles(dir, "*.tmp"))
                        if (Age(tmp, now) > 600)
                            TryDelete(tmp);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("CLI: уборка .tmp", e);
                }
        }

        private static double Age(string path, DateTime now)
        {
            try
            {
                return (now - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            }
            catch
            {
                return 0;
            }
        }

        private static void Beat(string state)
        {
            try
            {
                var seq = SessionState.GetInt(SeqKey, 0) + 1;
                SessionState.SetInt(SeqKey, seq);
                var info = NexusCliServer.Describe();
                var token = NexusCliServer.Token;
                var o = new JObject
                {
                    ["seq"] = seq,
                    ["pid"] = info.Pid,
                    ["state"] = state,
                    ["project"] = info.Project,
                    ["path"] = info.Path,
                    ["token"] = token != null && token.Length >= 8 ? token.Substring(0, 8) : null,
                    ["phase"] = _doing ?? NexusCliWatchdog.Phase(),
                    ["at"] = DateTime.UtcNow.ToString("o"),
                };
                if (Directory.Exists(Cli)) WriteAtomic(Alive, o.ToString(Formatting.None));
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: пульс", e);
            }
        }

        /// <summary>Клиент nexus-fs — в NexusTemp/, чтобы агент нашёл его в папке проекта.</summary>
        private static void InstallClient()
        {
            try
            {
                var from = Path.Combine(NexusCliStore.PackageClientDir, "nexus-fs");
                if (!File.Exists(from)) return;
                var to = Path.Combine(NexusTempPaths.Root, "nexus-fs");
                var bytes = File.ReadAllBytes(from);
                if (File.Exists(to) && Same(File.ReadAllBytes(to), bytes)) return;
                File.WriteAllBytes(to, bytes);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: копия nexus-fs", e);
            }
        }

        // ============================================================ файлы

        private static string Key(Tracked t) => (t.IsTask ? "t:" : "c:") + t.Id;

        private static void Count() => SessionState.SetInt(ProcessedKey, SessionState.GetInt(ProcessedKey, 0) + 1);

        private static List<string> Sorted(string dir)
        {
            var list = SafeFiles(dir);
            if (list.Count > 1)
                list.Sort((a, b) =>
                {
                    try
                    {
                        return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                    }
                    catch
                    {
                        return 0;
                    }
                });
            return list;
        }

        private static List<string> SafeFiles(string dir)
        {
            try
            {
                return Directory.Exists(dir) ? new List<string>(Directory.GetFiles(dir, "*.json")) : new List<string>();
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: список файлов", e);
                return new List<string>();
            }
        }

        private static string ReadSmall(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length <= MaxRequestBytes ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: чтение запроса", e);
                return null;
            }
        }

        private static JObject ReadJson(string path)
        {
            try
            {
                var text = ReadSmall(path);
                return text == null ? null : JObject.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static double ParseKeep(string json)
        {
            try
            {
                var o = json == null ? null : JObject.Parse(json);
                var k = o?["keep"];
                return k != null && (k.Type == JTokenType.Integer || k.Type == JTokenType.Float) ? k.Value<double>() : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryMove(string from, string to)
        {
            try
            {
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
                return true;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: захват файла", e);
                return false;
            }
        }

        /// <summary>Запись через .tmp с заменой; файл занят клиентом (Windows) — несколько повторов. false — не записано.</summary>
        private static bool WriteAtomic(string path, string content)
        {
            var temp = path + ".tmp";
            for (var attempt = 0; ; attempt++)
                try
                {
                    File.WriteAllText(temp, content, new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    return true;
                }
                catch (IOException) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(15);
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    System.Threading.Thread.Sleep(15);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("CLI: запись " + Path.GetFileName(path), e);
                    return false;
                }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: удаление " + Path.GetFileName(path), e);
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
