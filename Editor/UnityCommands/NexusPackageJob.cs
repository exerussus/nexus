using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Задание <c>pkg</c>: применить правки пакетов (Client.Resolve или Client.Add) и дождаться разрешения,
    /// импорта, компиляции и перезагрузки домена; итог — что стало с пакетами (было/стало hash) и ошибки компиляции,
    /// как у <c>refresh</c>.
    /// </summary>
    /// <remarks>
    /// <para>Фазы (параметр <c>stage</c>): <c>todo</c> — через 0,25 с после ответа команды вызвать Resolve/Add (они
    /// могут перезагрузить домен — ответ команды уже ушёл); <c>wait</c> — ждать, пока Unity перепишет
    /// packages-lock.json и зарегистрирует пакеты; <c>settle</c> — общий для ядра <see cref="NexusEditorCommands.Settle"/>:
    /// тишина компиляции и импорта, итог компиляции.</para>
    /// <para>Ожидание — только тиками задания, никаких циклов на главном потоке. Признаки конца разрешения: изменился
    /// отпечаток packages-lock.json, пришло событие <see cref="Events.registeredPackages"/> (оно приходит уже в новом
    /// домене — подписка в статическом конструкторе), в логе ошибки Package Manager, либо долгая тишина.</para>
    /// <para>Состояние: параметры задания (SessionState ядра) плюс два ключа SessionState этого класса — счётчик
    /// регистраций и их сводка; живут, только пока идёт задание <c>pkg</c>, и стираются вместе с отпиской. Запросы
    /// Client.Add — поле домена (переживать перезагрузку им незачем: после неё результат читается с диска).</para>
    /// </remarks>
    [InitializeOnLoad]
    internal static class NexusPackageJob
    {
        internal const string Kind = "pkg";

        private const string RegisteredKey = "Exerussus.Nexus.Pkg.Registered";
        private const string ChangesKey = "Exerussus.Nexus.Pkg.Changes";
        private const int MaxChangeLines = 40;

        /// <summary>Сколько ждать перезаписи lock, когда она обязана случиться (git тянет репозиторий), с.</summary>
        private const double NoChangeGrace = 180;

        /// <summary>Lock переписан, пакеты поменялись, но события регистрации нет — столько тишины считаем концом, с.</summary>
        private const double RegisterGrace = 30;

        private static readonly Dictionary<string, AddRequest> Adds = new Dictionary<string, AddRequest>(StringComparer.Ordinal);
        private static bool _subscribed;

        static NexusPackageJob()
        {
            NexusCliJobs.Register(Kind, Step);
            NexusCliJobs.RegisterAbort(Kind, Abort);
            // событие регистрации приходит в новом домене сразу после перезагрузки — подписка нужна уже здесь,
            // но только если задание pkg действительно идёт
            if (NexusCliJobs.Running(Kind) != null) Subscribe();
        }

        // ================================================================ старт

        /// <summary>
        /// Можно ли сейчас менять пакеты. Занят другим заданием, Play — ошибка; идёт компиляция или импорт —
        /// причина для RetryLater (вернётся строкой).
        /// </summary>
        internal static string Ready(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            var own = NexusCliJobs.Running(Kind);
            if (own != null)
                throw new NexusCommandException($"Идёт задание {own.Id} (pkg: {own.Phase}) — дождитесь: job.status {own.Id}", NexusCommandContext.Exit.Failed);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new NexusCommandException("Идёт Play — остановите: play.stop", NexusCommandContext.Exit.Failed);
            if (EditorApplication.isCompiling) return "редактор компилирует скрипты";
            if (EditorApplication.isUpdating) return "редактор импортирует ассеты";
            return null;
        }

        /// <summary>
        /// Начать задание. <paramref name="op"/>: add, remove, ref, update, resolve, registry. <paramref name="before"/> —
        /// hash git-пакетов до операции (для «было/стало»), <paramref name="expectLockChange"/> — Unity обязана
        /// переписать packages-lock.json (иначе считаем, что Resolve не прошёл).
        /// </summary>
        internal static NexusCliJob Begin(NexusCommandContext ctx, string op, IEnumerable<string> names,
            Dictionary<string, string> before, bool expectLockChange, string id = null, IEnumerable<string> notes = null)
        {
            var job = NexusCliJobs.Start(Kind, ctx.GetInt("timeout", 900), ctx);
            job.Set("stage", "todo");
            job.Set("op", op);
            job.Set("names", JsonConvert.SerializeObject((names ?? Enumerable.Empty<string>()).ToList()));
            job.Set("before", JsonConvert.SerializeObject(before ?? new Dictionary<string, string>()));
            job.Set("expect", expectLockChange ? "1" : "0");
            if (id != null) job.Set("id", id);
            if (notes != null) job.Set("notes", string.Join("\n", notes));
            job.Set("gen", NexusCompileLog.Generation.ToString(CultureInfo.InvariantCulture));
            job.Set("maxErrors", Math.Max(1, ctx.GetInt("max-errors", 20)).ToString(CultureInfo.InvariantCulture));

            SessionState.EraseString(ChangesKey);
            job.Set("reg0", SessionState.GetInt(RegisteredKey, 0).ToString(CultureInfo.InvariantCulture));
            Subscribe();
            NexusCliJobs.SetPhase(job, "ожидание");
            NexusCliJobs.Save();
            return job;
        }

        // ================================================================ тик

        private static void Step(NexusCliJob job)
        {
            try
            {
                switch (job.Get("stage"))
                {
                    case "todo":
                        Kick(job);
                        break;
                    case "wait":
                        Wait(job);
                        break;
                    default:
                        NexusEditorCommands.Settle(job);
                        if (job.Done) MergeData(job);
                        break;
                }
            }
            finally
            {
                if (job.Done) ReleaseIfIdle();
            }
        }

        /// <summary>Запуск Resolve/Add — после паузы, чтобы ответ команды ушёл до возможной перезагрузки домена.</summary>
        private static void Kick(NexusCliJob job)
        {
            if (NexusCliJobs.Now - job.StartedAt < 0.25) return;

            job.Set("stage", "wait");
            job.Set("lock0", NexusPackageJsonFile.Fingerprint(NexusPackageJsonFile.LockPath));
            if (job.Get("op") == "add") job.Set("deps0", JsonConvert.SerializeObject(ManifestNames()));
            NexusCliJobs.Touch(job);
            NexusCliJobs.Save();

            if (job.Get("op") == "add")
            {
                NexusCliJobs.SetPhase(job, "Client.Add: загрузка пакета");
                Adds[job.Id] = Client.Add(job.Get("id"));
            }
            else
            {
                NexusCliJobs.SetPhase(job, "разрешение пакетов (Client.Resolve)");
                Client.Resolve();
            }

            job.Anchor = NexusCliJobs.Now;
            NexusCliJobs.Touch(job);
        }

        /// <summary>Ждать конца разрешения пакетов.</summary>
        private static void Wait(NexusCliJob job)
        {
            var now = NexusCliJobs.Now;

            if (Adds.TryGetValue(job.Id, out var add))
            {
                if (!add.IsCompleted)
                {
                    job.Anchor = now;
                    return;
                }

                Adds.Remove(job.Id);
                if (add.Status == StatusCode.Failure) job.Set("addError", add.Error?.message ?? "Client.Add: неизвестная ошибка");
                else if (add.Result != null)
                {
                    job.Set("addName", add.Result.name);
                    job.Set("addVersion", add.Result.version);
                }

                job.Anchor = now;
                NexusCliJobs.Touch(job);
                if (job.Get("addError") != null)
                {
                    ToSettle(job);
                    return;
                }
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                job.Anchor = now;
                NexusCliJobs.SetPhase(job, EditorApplication.isCompiling ? "компиляция скриптов" : "импорт пакетов");
                return;
            }

            var quiet = now - job.Anchor;
            var lockChanged = NexusPackageJsonFile.Fingerprint(NexusPackageJsonFile.LockPath) != job.Get("lock0");
            var registered = SessionState.GetInt(RegisteredKey, 0) > Int(job.Get("reg0"));
            var expect = job.Get("expect") == "1";

            double need;
            if (registered || PackageErrors(job) > 0) need = 1.5;
            else if (lockChanged) need = PackagesChanged(job) ? RegisterGrace : 1.5;
            else need = expect ? NoChangeGrace : 4.0;

            if (quiet < need)
            {
                NexusCliJobs.SetPhase(job, lockChanged ? "пакеты разрешены, жду импорт" : "разрешение пакетов");
                return;
            }

            if (!lockChanged && !registered && expect) job.Set("noChange", "1");
            ToSettle(job);
        }

        /// <summary>Отчёт по пакетам — и дальше общий итог компиляции ядра.</summary>
        private static void ToSettle(NexusCliJob job)
        {
            var lines = new List<string>();
            var data = new JArray();
            var failed = false;
            var op = job.Get("op");
            var notes = job.Get("notes");
            if (!string.IsNullOrEmpty(notes)) lines.AddRange(notes.Split('\n'));

            List<NexusPackageRow> rows;
            try
            {
                rows = NexusPackageCatalog.Load();
            }
            catch (Exception e)
            {
                rows = new List<NexusPackageRow>();
                lines.Add("✗ " + e.Message);
                failed = true;
            }

            var byName = rows.ToDictionary(r => r.Name, StringComparer.Ordinal);
            var before = Before(job);

            switch (op)
            {
                case "ref":
                case "update":
                    foreach (var name in Names(job))
                    {
                        before.TryGetValue(name, out var was);
                        byName.TryGetValue(name, out var row);
                        var now = row?.LockHash;
                        string state;
                        if (now == null)
                        {
                            state = "failed";
                            failed = true;
                            lines.Add($"✗ {name}: записи в packages-lock.json нет — разрешение не удалось (logs --errors, pkg.info {name})");
                        }
                        else if (string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
                        {
                            state = "same";
                            lines.Add($"= {name} #{row.Ref ?? "HEAD"} @{NexusPackageGit.Short(now)} — без изменений" + (op == "update" ? " (новых коммитов нет)" : string.Empty));
                        }
                        else
                        {
                            state = "changed";
                            lines.Add($"✓ {name} #{row.Ref ?? "HEAD"}: {NexusPackageGit.Short(was)} → {NexusPackageGit.Short(now)}" + (row.Installed != null ? $" (v{row.Installed})" : string.Empty));
                        }

                        data.Add(JObject.FromObject(new { name, @ref = row?.Ref, before = was, after = now, state }));
                    }

                    break;

                case "add":
                {
                    var error = job.Get("addError");
                    var name = job.Get("addName") ?? NewManifestName(job);
                    if (error != null)
                    {
                        failed = true;
                        lines.Add("✗ не добавлен: " + NexusCliText.OneLine(error, 300));
                    }
                    else if (name == null || !byName.TryGetValue(name, out var row) || row.Requested == null)
                    {
                        failed = true;
                        lines.Add($"✗ «{job.Get("id")}» не появился в Packages/manifest.json (logs --errors)");
                    }
                    else
                    {
                        var version = row.Installed ?? job.Get("addVersion");
                        lines.Add($"✓ добавлен {row.Name}" + (version != null ? " v" + version : string.Empty) +
                                  $" [{row.Source}]" + (row.IsGit ? $" #{row.Ref ?? "HEAD"} @{NexusPackageGit.Short(row.Hash)}" : string.Empty));
                        data.Add(JObject.FromObject(row.ToData()));
                    }

                    break;
                }

                case "remove":
                    foreach (var name in Names(job))
                    {
                        byName.TryGetValue(name, out var row);
                        if (row == null || row.LockVersion == null && !row.Registered)
                            lines.Add($"✓ удалён {name}");
                        else if (row.Requested == null && row.Depth > 0)
                            lines.Add($"✓ {name} убран из manifest, но остался косвенной зависимостью: его тянут " +
                                      string.Join(", ", NexusPackageCatalog.Dependents(rows, name).Select(r => r.Name)));
                        else if (row.Requested != null)
                        {
                            failed = true;
                            lines.Add($"✗ {name} всё ещё в Packages/manifest.json");
                        }
                        else
                            lines.Add($"! {name} ещё зарегистрирован — Unity уберёт его после импорта (pkg.list)");

                        data.Add(JObject.FromObject(new { name, removed = row == null || row.LockVersion == null && !row.Registered }));
                    }

                    break;

                default:
                    lines.Add("✓ пакеты разрешены");
                    break;
            }

            var changes = SessionState.GetString(ChangesKey, string.Empty);
            if (SessionState.GetInt(RegisteredKey, 0) > Int(job.Get("reg0")) && changes.Length > 0)
            {
                lines.Add("Package Manager:");
                lines.AddRange(changes.TrimEnd('\n').Split('\n').Select(l => "  " + l));
            }

            if (job.Get("noChange") == "1")
                lines.Add($"! packages-lock.json не изменился за {NoChangeGrace:0} с — Resolve не прошёл или ещё идёт (редактор в фоне?): pkg.list, logs --errors");

            job.Set("report", string.Join("\n", lines));
            if (failed) job.Set("failed", "1");
            job.Set("pkgdata", data.ToString(Formatting.None));
            job.Set("stage", "settle");
            job.Anchor = NexusCliJobs.Now;
            NexusCliJobs.SetPhase(job, "импорт и компиляция");
            NexusCliJobs.Touch(job);
            NexusCliJobs.Save();
        }

        /// <summary>Итог ядра (компиляция) + данные по пакетам — одним объектом для --json.</summary>
        private static void MergeData(NexusCliJob job)
        {
            try
            {
                var o = string.IsNullOrEmpty(job.Data) ? new JObject() : JObject.Parse(job.Data);
                o["op"] = job.Get("op");
                o["packages"] = JToken.Parse(job.Get("pkgdata", "[]"));
                job.Data = o.ToString(Formatting.None);
                NexusCliJobs.Save();
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("pkg: данные итога", e);
            }
        }

        private static void Abort(NexusCliJob job, int exitCode, string reason)
        {
            Adds.Remove(job.Id);
            var report = job.Get("report");
            var output = (string.IsNullOrEmpty(report) ? string.Empty : report.TrimEnd('\n') + "\n") + reason +
                         "Package Manager мог продолжить работу сам: pkg.list, logs --errors\n";
            NexusCliJobs.Finish(job, exitCode, output);
            ReleaseIfIdle();
        }

        // ================================================================ события Package Manager

        private static void Subscribe()
        {
            if (_subscribed) return;
            Events.registeringPackages += OnRegistering;
            Events.registeredPackages += OnRegistered;
            _subscribed = true;
        }

        /// <summary>Заданий pkg нет — отписаться и стереть свои ключи («выключено — значит выключено»).</summary>
        private static void ReleaseIfIdle()
        {
            if (NexusCliJobs.Running(Kind) != null) return;
            Adds.Clear();
            if (_subscribed)
            {
                Events.registeringPackages -= OnRegistering;
                Events.registeredPackages -= OnRegistered;
                _subscribed = false;
            }

            SessionState.EraseString(ChangesKey);
            SessionState.EraseInt(RegisteredKey);
        }

        private static void OnRegistering(PackageRegistrationEventArgs args)
        {
            // перед перезагрузкой домена: зафиксировать задания, чтобы фаза не потерялась
            if (NexusCliJobs.Running(Kind) is NexusCliJob job)
            {
                NexusCliJobs.SetPhase(job, "регистрация пакетов");
                NexusCliJobs.Save();
            }
        }

        private static void OnRegistered(PackageRegistrationEventArgs args)
        {
            try
            {
                var sb = new StringBuilder(SessionState.GetString(ChangesKey, string.Empty));
                var count = sb.ToString().Count(c => c == '\n');

                void Line(string s)
                {
                    if (count >= MaxChangeLines) return;
                    sb.Append(count == MaxChangeLines - 1 ? "…" : s).Append('\n');
                    count++;
                }

                if (args != null)
                {
                    if (args.added != null)
                        foreach (var p in args.added)
                            Line($"+ {p.name} {p.version}");
                    if (args.removed != null)
                        foreach (var p in args.removed)
                            Line($"− {p.name} {p.version}");
                    if (args.changedTo != null)
                        foreach (var p in args.changedTo)
                        {
                            var from = args.changedFrom?.FirstOrDefault(x => x.name == p.name);
                            var fromText = from == null ? "?" : from.git?.hash != null ? NexusPackageGit.Short(from.git.hash) : from.version;
                            var toText = p.git?.hash != null ? NexusPackageGit.Short(p.git.hash) : p.version;
                            Line($"~ {p.name} {fromText} → {toText}");
                        }
                }

                SessionState.SetString(ChangesKey, sb.ToString());
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("pkg: сводка регистрации", e);
            }

            SessionState.SetInt(RegisteredKey, SessionState.GetInt(RegisteredKey, 0) + 1);
        }

        // ================================================================ разное

        /// <summary>Ошибки Package Manager в логе с начала задания.</summary>
        private static int PackageErrors(NexusCliJob job)
        {
            var q = new NexusLogQuery
            {
                AfterSeq = job.LogSeq,
                Types = new HashSet<UnityEngine.LogType> { UnityEngine.LogType.Error, UnityEngine.LogType.Exception },
                AnyWords = new List<string> { "resolving packages", "package manager", "invalid dependencies", "upm", "git" },
            };
            NexusLogBuffer.Query(q, 1, out var total);
            return total;
        }

        /// <summary>Поменялись ли сами пакеты (тогда ждём регистрацию, а не только перезапись lock).</summary>
        private static bool PackagesChanged(NexusCliJob job)
        {
            var op = job.Get("op");
            if (op != "ref" && op != "update") return op == "add" || op == "remove";

            NexusPackageJsonFile lockFile;
            try
            {
                lockFile = NexusPackageJsonFile.Load(NexusPackageJsonFile.LockPath, false);
            }
            catch
            {
                return true;
            }

            var deps = lockFile?.Section("dependencies", false);
            var before = Before(job);
            foreach (var name in Names(job))
            {
                var now = (deps?[name] as JObject)?["hash"]?.ToString();
                before.TryGetValue(name, out var was);
                if (!string.Equals(now, was, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static List<string> ManifestNames()
        {
            try
            {
                var deps = NexusPackageJsonFile.Load(NexusPackageJsonFile.ManifestPath, false)?.Section("dependencies", false);
                return deps?.Properties().Select(p => p.Name).ToList() ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>Имя, которое Client.Add дописал в manifest (запрос мог потеряться с перезагрузкой домена).</summary>
        private static string NewManifestName(NexusCliJob job)
        {
            List<string> was;
            try
            {
                was = JsonConvert.DeserializeObject<List<string>>(job.Get("deps0", "[]")) ?? new List<string>();
            }
            catch
            {
                was = new List<string>();
            }

            var added = ManifestNames().Where(n => !was.Contains(n)).ToList();
            if (added.Count == 1) return added[0];

            // имя уже было (заменили версию/ссылку): ищем по самому идентификатору
            var id = job.Get("id") ?? string.Empty;
            var at = id.IndexOf('@');
            if (!NexusPackageGit.LooksGit(id)) return at > 0 ? id.Substring(0, at) : id;

            try
            {
                var deps = NexusPackageJsonFile.Load(NexusPackageJsonFile.ManifestPath, false)?.Section("dependencies", false);
                return deps?.Properties().FirstOrDefault(p => NexusPackageGit.SameRepo((string)p.Value, id))?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static List<string> Names(NexusCliJob job)
        {
            try
            {
                return JsonConvert.DeserializeObject<List<string>>(job.Get("names", "[]")) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static Dictionary<string, string> Before(NexusCliJob job)
        {
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(job.Get("before", "{}")) ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
