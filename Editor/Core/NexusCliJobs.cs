using System;
using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>Задание CLI: долгая операция, которая переживает перезагрузку домена (компиляция, Play, тесты).</summary>
    public sealed class NexusCliJob
    {
        public string Id;
        public string Kind;
        public bool Done;
        public int ExitCode;
        public string Output;

        /// <summary>Данные итога (JSON) — уходят клиенту как результат команды.</summary>
        public string Data;

        /// <summary>Что делается сейчас — клиент показывает при ожидании.</summary>
        public string Phase;

        /// <summary>Время редактора (<see cref="EditorApplication.timeSinceStartup"/>): старт, срок, опорная точка.</summary>
        public double StartedAt;
        public double Deadline;
        public double Anchor;

        /// <summary>Номер последней записи лога на старте — «ошибки с начала задания».</summary>
        public long LogSeq;

        public Dictionary<string, string> Params = new Dictionary<string, string>();

        public string Get(string key, string fallback = null) => Params != null && Params.TryGetValue(key, out var v) ? v : fallback;

        public void Set(string key, string value)
        {
            Params ??= new Dictionary<string, string>();
            Params[key] = value;
        }
    }

    /// <summary>
    /// Задания CLI — компонент Core с явным владельцем (этот класс). Состояние — в SessionState: переживает
    /// перезагрузку домена, но не перезапуск редактора. Обработчик вида (<see cref="Register"/>) вызывается каждый
    /// кадр редактора, пока задание идёт.
    /// </summary>
    /// <remarks>
    /// Работает, только пока запущен сервер CLI: подписка на update — лишь при идущих заданиях; сервер выключен —
    /// идущие задания отменяются, хранилище стирается.
    /// </remarks>
    public static class NexusCliJobs
    {
        private const string StoreKey = "Exerussus.Nexus.Cli.Jobs";
        private const string CounterKey = "Exerussus.Nexus.Cli.JobCounter";
        private const int KeepFinished = 50;

        /// <summary>Сколько ждать обработчик вида после перезагрузки (его сборка могла не загрузиться).</summary>
        private const double MissingHandlerGrace = 15.0;

        private static readonly Dictionary<string, Action<NexusCliJob>> Handlers = new Dictionary<string, Action<NexusCliJob>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Action<NexusCliJob, int, string>> Aborts = new Dictionary<string, Action<NexusCliJob, int, string>>(StringComparer.Ordinal);
        private static List<NexusCliJob> _jobs;
        private static bool _dirty;
        private static bool _ticking;
        private static bool _active;

        // Фаза меняется хоть каждый кадр (wait --frames, play.step, задача над таким шагом) — её
        // изменение сохраняется не чаще PhaseSaveInterval. Раньше она ставила _dirty, и Tick каждый кадр
        // сериализовал ВСЕ задания (до 50 завершённых с выводом) в SessionState. Сохранность параметров
        // перед перезагрузкой домена держит безусловный Save в beforeAssemblyReload / Deactivate(true).
        private static bool _phaseDirty;
        private static double _lastSave;
        private const double PhaseSaveInterval = 2.0;

        /// <summary>Порог для сохраняемой копии: длинный Output завершённого задания режется (голова + хвост),
        /// большие Data заменяются пометкой. В памяти остаётся полный текст (job.status, проверки задач).</summary>
        private const int MaxStoredChars = 256 * 1024;

        public static double Now => EditorApplication.timeSinceStartup;

        /// <summary>Обработчик вида задания: вызывается каждый кадр, пока задание идёт. Регистрируется на загрузке домена.</summary>
        public static void Register(string kind, Action<NexusCliJob> step)
        {
            if (string.IsNullOrEmpty(kind) || step == null) return;
            Handlers[kind] = step;
        }

        /// <summary>
        /// Аварийный выход вида: вызывается вместо обычного <see cref="Finish"/>, когда задание снимается по сроку или
        /// его обработчик бросил исключение. Должен сам завершить задание (иначе завершится текстом по умолчанию).
        /// </summary>
        public static void RegisterAbort(string kind, Action<NexusCliJob, int, string> abort)
        {
            if (string.IsNullOrEmpty(kind) || abort == null) return;
            Aborts[kind] = abort;
        }

        /// <summary>Номер последнего начатого задания: до вызова команды — чтобы отличить «своё» задание от чужого.</summary>
        public static int Counter => SessionState.GetInt(CounterKey, 0);

        /// <summary>Задание начато после отметки <paramref name="counter"/> (то есть этим вызовом).</summary>
        public static bool CreatedAfter(NexusCliJob job, int counter) =>
            job != null && job.Id != null && job.Id.Length > 1 &&
            int.TryParse(job.Id.Substring(1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > counter;

        /// <summary>Все задания (идущие и последние завершённые), новые в конце.</summary>
        public static IReadOnlyList<NexusCliJob> All => Jobs;

        private static List<NexusCliJob> Jobs
        {
            get
            {
                if (_jobs != null) return _jobs;
                _jobs = Load();
                return _jobs;
            }
        }

        /// <summary>Начать задание. Обработчик вида должен быть зарегистрирован.</summary>
        public static NexusCliJob Start(string kind, double timeoutSeconds, NexusCommandContext ctx = null)
        {
            var counter = SessionState.GetInt(CounterKey, 0) + 1;
            SessionState.SetInt(CounterKey, counter);

            var now = Now;
            var job = new NexusCliJob
            {
                Id = "j" + counter,
                Kind = kind,
                StartedAt = now,
                Anchor = now,
                Deadline = now + Math.Max(1.0, timeoutSeconds),
                LogSeq = NexusLogBuffer.LastSeq,
                Phase = "старт",
            };

            Jobs.Add(job);
            Trim();
            Save();
            if (ctx != null) ctx.Job = job.Id;
            EnsureTicking();
            return job;
        }

        public static NexusCliJob Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var j in Jobs)
                if (string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase))
                    return j;
            return null;
        }

        public static bool IsRunning(string id)
        {
            var job = Find(id);
            return job != null && !job.Done;
        }

        /// <summary>Идущее задание вида (или из набора видов), иначе null.</summary>
        public static NexusCliJob Running(params string[] kinds)
        {
            foreach (var j in Jobs)
            {
                if (j.Done) continue;
                if (kinds == null || kinds.Length == 0) return j;
                foreach (var k in kinds)
                    if (string.Equals(j.Kind, k, StringComparison.Ordinal))
                        return j;
            }

            return null;
        }

        /// <summary>Срок задания не позже чем через <paramref name="seconds"/> от начала (0 и меньше — не трогать).</summary>
        public static void LimitDeadline(NexusCliJob job, double seconds)
        {
            if (job == null || job.Done || seconds <= 0) return;
            var limit = job.StartedAt + seconds;
            if (limit < job.Deadline)
            {
                job.Deadline = limit;
                _dirty = true;
            }
        }

        /// <summary>Поставить срок задания (от текущего момента).</summary>
        public static void SetDeadline(NexusCliJob job, double secondsFromNow)
        {
            if (job == null || job.Done) return;
            job.Deadline = Now + Math.Max(1.0, secondsFromNow);
            _dirty = true;
        }

        public static void SetPhase(NexusCliJob job, string phase)
        {
            if (job == null || job.Done || string.Equals(job.Phase, phase, StringComparison.Ordinal)) return;
            job.Phase = phase;
            _phaseDirty = true;
        }

        /// <summary>Отметить изменение полей задания (сохраняется в конце кадра).</summary>
        public static void Touch(NexusCliJob job) => _dirty = true;

        /// <summary>Завершить задание: код, текст и данные итога.</summary>
        public static void Finish(NexusCliJob job, int exitCode, string output, object data = null)
        {
            if (job == null || job.Done) return;
            job.Done = true;
            job.ExitCode = exitCode;
            job.Output = output ?? string.Empty;
            job.Phase = "готово";
            job.Data = data == null ? null : SerializeData(data, job);
            Save();
        }

        /// <summary>Отменить идущее задание.</summary>
        public static bool Cancel(string id, string reason)
        {
            var job = Find(id);
            if (job == null || job.Done) return false;
            Finish(job, NexusCommandContext.Exit.Failed, "✗ Задание отменено: " + reason + "\n");
            return true;
        }

        // ---------------------------------------------------------------- жизненный цикл (владелец — сервер CLI)

        /// <summary>Сервер запущен: после перезагрузки идущие задания продолжаются.</summary>
        internal static void Activate()
        {
            _active = true;
            // своя подписка: сохранить перед перезагрузкой, даже если остановка сервера сорвётся раньше
            AssemblyReloadEvents.beforeAssemblyReload -= SaveBeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += SaveBeforeReload;
            var now = Now;
            foreach (var j in Jobs)
                if (!j.Done)
                    j.Anchor = now; // перезагрузка — тоже работа: «тишина» считается заново

            EnsureTicking();
        }

        /// <summary>Сервер остановлен. Перезагрузка — задания сохраняются; выключение — отменяются и стираются.</summary>
        internal static void Deactivate(bool reloading)
        {
            _active = false;
            StopTicking();
            AssemblyReloadEvents.beforeAssemblyReload -= SaveBeforeReload;

            if (reloading)
            {
                // безусловно: часть обработчиков меняет Params без Touch (play.step, wait, play.start…)
                if (_jobs != null) Save();
                return;
            }

            foreach (var j in Jobs)
                if (!j.Done)
                {
                    j.Done = true;
                    j.ExitCode = NexusCommandContext.Exit.Busy;
                    j.Output = "✗ CLI выключен — задание прервано.\n";
                }

            _jobs = null;
            SessionState.EraseString(StoreKey);
            SessionState.EraseInt(CounterKey);
        }

        private static void EnsureTicking()
        {
            if (!_active || _ticking || Running() == null) return;
            EditorApplication.update += Tick;
            _ticking = true;
        }

        private static void StopTicking()
        {
            if (!_ticking) return;
            EditorApplication.update -= Tick;
            _ticking = false;
        }

        private static void Tick()
        {
            var now = Now;
            var any = false;

            // копия: обработчик может начать новое задание
            var snapshot = Jobs.ToArray();
            foreach (var job in snapshot)
            {
                if (job.Done) continue;

                if (now > job.Deadline)
                {
                    Abort(job, NexusCommandContext.Exit.Timeout, $"✗ Задание «{job.Kind}» не завершилось за {job.Deadline - job.StartedAt:0} с (на шаге «{job.Phase}»).\n");
                    continue;
                }

                if (!Handlers.TryGetValue(job.Kind, out var step))
                {
                    if (now - job.Anchor > MissingHandlerGrace)
                        Finish(job, NexusCommandContext.Exit.Failed, $"✗ Нет обработчика задания «{job.Kind}»: его сборка не загружена.\n");
                    any = true;
                    continue;
                }

                try
                {
                    step(job);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Error("CLI задание " + job.Kind, e);
                    Abort(job, NexusCommandContext.Exit.Crashed, $"✗ {job.Kind}: {e.GetType().Name}: {e.Message}\n");
                }

                if (!job.Done) any = true;
            }

            if (_dirty || (_phaseDirty && now - _lastSave >= PhaseSaveInterval)) Save();
            if (!any && Running() == null) StopTicking();
        }

        private static void SaveBeforeReload()
        {
            if (_active && _jobs != null) Save();
        }

        private static void Abort(NexusCliJob job, int exitCode, string reason)
        {
            if (Aborts.TryGetValue(job.Kind, out var abort))
                try
                {
                    abort(job, exitCode, reason);
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Error("CLI: аварийный выход задания " + job.Kind, e);
                }

            Finish(job, exitCode, reason);
        }

        // ---------------------------------------------------------------- хранилище

        private static void Trim()
        {
            var finished = 0;
            foreach (var j in Jobs)
                if (j.Done)
                    finished++;

            for (var i = 0; i < Jobs.Count && finished > KeepFinished;)
            {
                if (Jobs[i].Done)
                {
                    Jobs.RemoveAt(i);
                    finished--;
                }
                else
                {
                    i++;
                }
            }
        }

        /// <summary>Сохранить сейчас (перед операцией, которая может перезагрузить домен).</summary>
        public static void Save()
        {
            _dirty = false;
            _phaseDirty = false;
            _lastSave = Now;
            try
            {
                Trim();
                SessionState.SetString(StoreKey, JsonConvert.SerializeObject(ForStore(), Formatting.None));
            }
            catch (Exception e)
            {
                NexusDiagnostics.Error("CLI: сохранение заданий", e);
            }
        }

        /// <summary>Список для сохранения: завершённые задания с огромным Output/Data — урезанные копии.</summary>
        private static List<NexusCliJob> ForStore()
        {
            var jobs = Jobs;
            List<NexusCliJob> copy = null;
            for (var i = 0; i < jobs.Count; i++)
            {
                var j = jobs[i];
                if (!j.Done) continue;
                var bigOutput = j.Output != null && j.Output.Length > MaxStoredChars;
                var bigData = j.Data != null && j.Data.Length > MaxStoredChars;
                if (!bigOutput && !bigData) continue;

                copy ??= new List<NexusCliJob>(jobs);
                copy[i] = new NexusCliJob
                {
                    Id = j.Id, Kind = j.Kind, Done = j.Done, ExitCode = j.ExitCode, Phase = j.Phase,
                    StartedAt = j.StartedAt, Deadline = j.Deadline, Anchor = j.Anchor, LogSeq = j.LogSeq, Params = j.Params,
                    Output = bigOutput ? CutMiddle(j.Output) : j.Output,
                    Data = bigData ? JsonConvert.SerializeObject(new { dataOmitted = true, chars = j.Data.Length }) : j.Data,
                };
            }

            return copy ?? jobs;
        }

        private static string CutMiddle(string s)
        {
            var half = MaxStoredChars / 2;
            return s.Substring(0, half) + $"\n… (обрезано при перезагрузке домена: {s.Length - 2 * half} символов) …\n" + s.Substring(s.Length - half);
        }

        private static List<NexusCliJob> Load()
        {
            try
            {
                var raw = SessionState.GetString(StoreKey, string.Empty);
                if (string.IsNullOrEmpty(raw)) return new List<NexusCliJob>();
                var list = JsonConvert.DeserializeObject<List<NexusCliJob>>(raw);
                if (list == null) return new List<NexusCliJob>();
                list.RemoveAll(j => j == null || string.IsNullOrEmpty(j.Id) || string.IsNullOrEmpty(j.Kind));
                return list;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: чтение заданий", e);
                return new List<NexusCliJob>();
            }
        }

        private static string SerializeData(object data, NexusCliJob job)
        {
            try
            {
                return JsonConvert.SerializeObject(data, Formatting.None);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: данные задания " + job.Kind, e);
                return null;
            }
        }
    }
}
