using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Команды git, которые выполняет сам демон (не Unity): работают при закрытом или компилирующем редакторе и с
    /// учётными данными пользователя (credential manager машины). Фиксированный набор с разбором аргументов —
    /// произвольные команды git не пробрасываются. Репозитории — только из белого списка (<see cref="GitRepos"/>).
    /// Запись — только с явным подтверждением (<c>--yes</c>; push — двухшаговый код) и проверкой следа ИИ
    /// (<see cref="AiTraceCheck"/>). Состояние (коды подтверждения push, замки репозиториев) — компонент демона,
    /// живёт в памяти процесса.
    /// </summary>
    internal static partial class GitCommands
    {
        /// <summary>Код «нужно подтверждение» (как efc: 8 — denied/no-approval): ничего не сделано.</summary>
        public const int NeedsConfirmation = 8;

        public static readonly (string Name, string Usage, string Summary, bool ReadOnly)[] Table =
        {
            ("git.repos", "git.repos", "белый список репозиториев: ветка, ahead/behind, чистое ли дерево", true),
            ("git.status", "git.status [--repo R]", "ветка, upstream, ahead/behind, изменённые/новые/удалённые файлы", true),
            ("git.log", "git.log [--repo R] [--max 20] [--range a..b] [--path p]", "коммиты: хэш, автор, дата, тема", true),
            ("git.diff", "git.diff [--repo R] [--staged] [--stat] [--path p] [--max-lines 400]", "дифф рабочего дерева или индекса", true),
            ("git.branch", "git.branch [--repo R] [--all]", "ветки (и удалённые с --all)", true),
            ("git.show", "git.show [--repo R] <rev> [--stat] [--max-lines 400]", "коммит или тег", true),
            ("git.fetch", "git.fetch [--repo R | --all]", "получить изменения remote (сеть; --all — все репозитории списка)", true),
            ("git.checkout", "git.checkout --repo R <ветка> [--create [--from ref]] [--yes]", "переключить или создать ветку (отказ при грязном дереве)", false),
            ("git.pull", "git.pull --repo R [--yes]", "pull только --ff-only", false),
            ("git.add", "git.add --repo R <путь…>|--all [--yes]", "добавить в индекс", false),
            ("git.commit", "git.commit --repo R -m \"сообщение\" [--all] [--allow-ai-marks] [--yes]", "коммит от user.name/user.email репозитория, сообщение проверяется", false),
            ("git.merge", "git.merge --repo R <ветка> [--ff-only] [--yes]", "слияние в текущую ветку, конфликт — отмена", false),
            ("git.tag", "git.tag --repo R <vX.Y.Z> -m \"сообщение\" [--allow-ai-marks] [--yes]", "аннотированный тег на HEAD, сообщение проверяется", false),
            ("git.push", "git.push --repo R [--tags|--tag vX.Y.Z] [--set-upstream] [--allow-ai-marks] | --repo R --confirm <код>", "двухшаговый push: отчёт и код, затем отправка (без force)", false),
        };

        public static bool Owns(string command) => command != null && command.StartsWith("git.", StringComparison.Ordinal);

        public static bool IsReadOnly(string command)
        {
            foreach (var c in Table)
                if (c.Name == command)
                    return c.ReadOnly;
            return false;
        }

        /// <summary>Замки записи по пути репозитория: две записи в один репозиторий не идут параллельно (index.lock).</summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private static async Task<IDisposable> LockRepo(GitRepo repo, Action<string> phase, CancellationToken ct)
        {
            var gate = RepoLocks.GetOrAdd(repo.Path, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, ct))
            {
                phase("жду другую запись в " + repo.Name);
                await gate.WaitAsync(ct);
            }

            return new Releaser(gate);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim _gate;
            public Releaser(SemaphoreSlim gate) => _gate = gate;

            public void Dispose()
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }

        /// <summary>Выполнить команду. Отмена ct — отмена или срок вызова: процесс git останавливается.</summary>
        public static async Task<(int Exit, string Output, string Result)> Run(Call call, Action<string> phase, CancellationToken ct)
        {
            var entry = Table.FirstOrDefault(t => t.Name == call.Command);
            if (entry.Name == null)
                return (3, $"✗ Нет git-команды «{call.Command}». Есть: {string.Join(", ", Table.Select(t => t.Name))}\n", null);
            if (call.Args.Contains("--help") || call.Args.Contains("-h"))
                return (0, $"{entry.Usage}\n  {entry.Summary}\n", null);

            var ctx = new Ctx { Call = call, Project = call.Project, Phase = phase, Ct = ct };
            try
            {
                switch (call.Command)
                {
                    case "git.repos": return await Repos(ctx);
                    case "git.status": return await Status(ctx);
                    case "git.log": return await LogCmd(ctx);
                    case "git.diff": return await Diff(ctx);
                    case "git.branch": return await Branch(ctx);
                    case "git.show": return await Show(ctx);
                    case "git.fetch": return await Fetch(ctx);
                    case "git.checkout": return await Checkout(ctx);
                    case "git.pull": return await Pull(ctx);
                    case "git.add": return await Add(ctx);
                    case "git.commit": return await Commit(ctx);
                    case "git.merge": return await Merge(ctx);
                    case "git.tag": return await Tag(ctx);
                    case "git.push": return await Push(ctx);
                }
            }
            catch (UsageException e)
            {
                return (2, $"✗ {e.Message}\nИспользование: {entry.Usage}\n", null);
            }
            catch (GitFailure e)
            {
                return (1, $"✗ {e.Message}\n", e.Data == null ? null : Json.Write(e.Data));
            }

            return (3, $"✗ Нет git-команды «{call.Command}».\n", null);
        }

        // ================================================================== общее

        private sealed class Ctx
        {
            public Call Call;
            public Project Project;
            public Action<string> Phase;
            public CancellationToken Ct;
            public GitRepo Repo;
            public Parsed Args;
        }

        private sealed class UsageException : Exception
        {
            public UsageException(string message) : base(message)
            {
            }
        }

        private sealed class GitFailure : Exception
        {
            public new readonly JsonObject Data;

            public GitFailure(string message, JsonObject data = null) : base(message) => Data = data;
        }

        private sealed class Parsed
        {
            public readonly Dictionary<string, List<string>> Values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public readonly HashSet<string> Flags = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<string> Pos = new List<string>();

            public string Get(string key) => Values.TryGetValue(key, out var l) ? l[^1] : null;
            public List<string> All(string key) => Values.TryGetValue(key, out var l) ? l : new List<string>();
            public bool Has(string flag) => Flags.Contains(flag);
        }

        /// <summary>Разбор: --key value, --key=value, флаги, позиционные; после «--» всё позиционное. -m — это --message.</summary>
        private static Parsed Parse(string[] args, string[] valued, string[] flags, int maxPositional)
        {
            var p = new Parsed();
            var rest = false;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i] ?? string.Empty;
                if (rest)
                {
                    p.Pos.Add(a);
                    continue;
                }

                if (a == "--")
                {
                    rest = true;
                    continue;
                }

                if (a == "-m") a = "--message";
                if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1)
                {
                    string key = a, value = null;
                    var eq = a.IndexOf('=');
                    if (a.StartsWith("--", StringComparison.Ordinal) && eq > 2)
                    {
                        key = a.Substring(0, eq);
                        value = a.Substring(eq + 1);
                    }

                    if (Array.IndexOf(valued, key) >= 0)
                    {
                        if (value == null)
                        {
                            if (i + 1 >= args.Length) throw new UsageException($"{key}: нужно значение");
                            value = args[++i] ?? string.Empty;
                        }

                        if (!p.Values.TryGetValue(key, out var list)) p.Values[key] = list = new List<string>();
                        list.Add(value);
                        continue;
                    }

                    if (Array.IndexOf(flags, key) >= 0 && value == null)
                    {
                        p.Flags.Add(key);
                        continue;
                    }

                    throw new UsageException($"неизвестный аргумент «{a}»");
                }

                p.Pos.Add(a);
            }

            if (p.Pos.Count > maxPositional)
                throw new UsageException(maxPositional == 0 ? $"лишний аргумент «{p.Pos[0]}»" : $"лишний аргумент «{p.Pos[maxPositional]}»");
            return p;
        }

        private static int IntArg(Parsed a, string key, int fallback, int min, int max)
        {
            var v = a.Get(key);
            if (v == null) return fallback;
            if (!int.TryParse(v, out var n) || n < min || n > max) throw new UsageException($"{key}: нужно число {min}…{max}");
            return n;
        }

        /// <summary>Разобрать аргументы и найти репозиторий. writing — --repo обязателен.</summary>
        private static async Task Prepare(Ctx c, string[] valued, string[] flags, int maxPositional, bool writing)
        {
            c.Args = Parse(c.Call.Args, valued.Append("--repo").ToArray(), flags, maxPositional);
            var name = c.Args.Get("--repo");
            if (writing && string.IsNullOrEmpty(name)) throw new UsageException("для записи нужен явный --repo (имя из git.repos, «project» — проект)");
            c.Phase("проверяю репозиторий");
            var (repo, error) = await GitRepos.Resolve(c.Project, name, c.Ct);
            if (repo == null) throw new GitFailure(error);
            c.Repo = repo;
        }

        private static Task<GitResult> Git(Ctx c, params string[] args) => GitRunner.Run(c.Repo.Path, args, GitRunner.DefaultTimeout, c.Ct, readOnly: true);

        private static Task<GitResult> GitWrite(Ctx c, int timeout, string input, params string[] args) =>
            GitRunner.Run(c.Repo.Path, args, timeout, c.Ct, input);

        /// <summary>git, который должен пройти: иначе — отказ с первой строкой ошибки.</summary>
        private static async Task<string> Must(Ctx c, string what, params string[] args)
        {
            var r = await Git(c, args);
            if (!r.Ok) throw new GitFailure($"{what}: {r.FirstError}");
            return r.Out;
        }

        private static string RepoLabel(GitRepo r) => r.IsProject ? $"project ({r.Name})" : r.Name;

        /// <summary>Ревизия от клиента: не опция, без пробелов и управляющих символов.</summary>
        private static string Rev(string rev, string what)
        {
            if (string.IsNullOrWhiteSpace(rev)) throw new UsageException($"нужно {what}");
            if (rev.StartsWith("-", StringComparison.Ordinal) || rev.Length > 250 || rev.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
                throw new UsageException($"неверное {what}: «{rev}»");
            return rev;
        }

        /// <summary>Путь от клиента → pathspec внутри репозитория (литеральный, без glob); выход за репозиторий — отказ.</summary>
        private static string PathSpec(GitRepo repo, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) throw new UsageException("неверный путь");
            string full;
            try
            {
                full = Project.Normalize(System.IO.Path.Combine(repo.Path, path));
            }
            catch (Exception e)
            {
                throw new UsageException($"неверный путь «{path}»: {e.Message}");
            }

            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(full, repo.Path, cmp) && !full.StartsWith(repo.Path + "/", cmp))
                throw new UsageException($"путь «{path}» вне репозитория {repo.Name}");
            var rel = full.Length == repo.Path.Length ? "." : full.Substring(repo.Path.Length + 1);
            return ":(literal)" + rel;
        }

        private static string Cut(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";

        /// <summary>Короткий хэш.</summary>
        private static string Short(string hash) => hash == null ? "" : hash.Length <= 10 ? hash : hash.Substring(0, 10);

        /// <summary>Ограничить текст по строкам.</summary>
        private static (string Text, int Lines, bool Cut) Lines(string text, int max)
        {
            var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            if (text.Length == 0) return ("", 0, false);
            if (lines.Length <= max) return (string.Join("\n", lines) + "\n", lines.Length, false);
            return (string.Join("\n", lines.Take(max)) + "\n", lines.Length, true);
        }

        // ------------------------------------------------------------------ состояние репозитория

        private sealed class RepoStatus
        {
            public string Oid;
            public string Head;
            public string Upstream;
            public int Ahead;
            public int Behind;
            public readonly List<(string XY, string Path, string Orig)> Changed = new List<(string, string, string)>();
            public readonly List<string> Untracked = new List<string>();
            public readonly List<string> Conflicts = new List<string>();

            public bool Detached => Head == "(detached)";
            public bool Dirty => Changed.Count > 0 || Conflicts.Count > 0;
            public bool Clean => !Dirty && Untracked.Count == 0;
        }

        /// <summary>status --porcelain=v2 --branch -z (без сети: ahead/behind — по локальной копии upstream).</summary>
        private static async Task<RepoStatus> ReadStatus(string repo, CancellationToken ct)
        {
            var r = await GitRunner.Run(repo, new[] { "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all" }, GitRunner.DefaultTimeout, ct, readOnly: true);
            if (!r.Ok) throw new GitFailure("git status: " + r.FirstError);
            var s = new RepoStatus();
            var items = r.Out.Split('\0');
            for (var i = 0; i < items.Length; i++)
            {
                var e = items[i];
                if (e.Length == 0) continue;
                if (e.StartsWith("# branch.oid ", StringComparison.Ordinal)) s.Oid = e.Substring(13);
                else if (e.StartsWith("# branch.head ", StringComparison.Ordinal)) s.Head = e.Substring(14);
                else if (e.StartsWith("# branch.upstream ", StringComparison.Ordinal)) s.Upstream = e.Substring(18);
                else if (e.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    var ab = e.Substring(12).Split(' ');
                    if (ab.Length == 2)
                    {
                        int.TryParse(ab[0].TrimStart('+'), out s.Ahead);
                        int.TryParse(ab[1].TrimStart('-'), out s.Behind);
                    }
                }
                else if (e.StartsWith("1 ", StringComparison.Ordinal))
                {
                    var f = e.Split(' ', 9);
                    if (f.Length == 9) s.Changed.Add((f[1], f[8], null));
                }
                else if (e.StartsWith("2 ", StringComparison.Ordinal))
                {
                    var f = e.Split(' ', 10);
                    var orig = i + 1 < items.Length ? items[++i] : null;
                    if (f.Length == 10) s.Changed.Add((f[1], f[9], orig));
                }
                else if (e.StartsWith("u ", StringComparison.Ordinal))
                {
                    var f = e.Split(' ', 11);
                    if (f.Length == 11) s.Conflicts.Add(f[10]);
                }
                else if (e.StartsWith("? ", StringComparison.Ordinal)) s.Untracked.Add(e.Substring(2));
            }

            return s;
        }

        private static string Kind(char c) => c switch
        {
            'M' => "изменён",
            'T' => "сменил тип",
            'A' => "добавлен",
            'D' => "удалён",
            'R' => "переименован",
            'C' => "скопирован",
            _ => c.ToString(),
        };

        private static string BranchLine(RepoStatus s)
        {
            var sb = new StringBuilder(s.Detached ? $"HEAD отсоединён ({Short(s.Oid)})" : s.Head);
            if (s.Upstream != null)
            {
                sb.Append(" → ").Append(s.Upstream);
                if (s.Ahead > 0) sb.Append(" ↑").Append(s.Ahead);
                if (s.Behind > 0) sb.Append(" ↓").Append(s.Behind);
                if (s.Ahead == 0 && s.Behind == 0) sb.Append(" (синхронно)");
            }
            else if (!s.Detached) sb.Append(" (без upstream)");

            return sb.ToString();
        }

        private static JsonObject StatusJson(GitRepo repo, RepoStatus s) => new JsonObject
        {
            ["repo"] = repo.IsProject ? "project" : repo.Name,
            ["path"] = repo.Path,
            ["branch"] = s.Detached ? null : s.Head,
            ["head"] = s.Oid,
            ["upstream"] = s.Upstream,
            ["ahead"] = s.Ahead,
            ["behind"] = s.Behind,
            ["clean"] = s.Clean,
            ["changed"] = s.Changed.Count,
            ["untracked"] = s.Untracked.Count,
            ["conflicts"] = s.Conflicts.Count,
        };

        private static async Task<(string Name, string Email)> Identity(Ctx c)
        {
            var name = (await Git(c, "config", "--get", "user.name")).Out.Trim();
            var email = (await Git(c, "config", "--get", "user.email")).Out.Trim();
            return (name, email);
        }

        private static async Task<(string Name, string Email)> RequireIdentity(Ctx c)
        {
            var (name, email) = await Identity(c);
            if (name.Length == 0 || email.Length == 0)
                throw new GitFailure($"В {RepoLabel(c.Repo)} не задана личность git (user.name/user.email). Укажите её сами: " +
                                     $"git -C \"{c.Repo.Path}\" config user.name \"Имя\" и config user.email \"почта\" (или --global). Демон ничего не подставляет.");
            return (name, email);
        }

        private static async Task<string> CurrentBranch(Ctx c)
        {
            var r = await Git(c, "symbolic-ref", "--short", "-q", "HEAD");
            return r.Ok ? r.Out.Trim() : null;
        }

        private static async Task<bool> RefExists(Ctx c, string fullRef) => (await Git(c, "rev-parse", "--verify", "--quiet", fullRef)).Ok;

        // ================================================================== чтение

        private static async Task<(int, string, string)> Repos(Ctx c)
        {
            Parse(c.Call.Args, Array.Empty<string>(), Array.Empty<string>(), 0);
            c.Phase("проверяю белый список");
            var notes = new List<string>();
            var repos = await GitRepos.List(c.Project, c.Ct, notes);
            var statuses = new RepoStatus[repos.Count];
            var errors = new string[repos.Count];
            await Task.WhenAll(repos.Select(async (r, i) =>
            {
                if (r.Problem != null) return;
                try
                {
                    statuses[i] = await ReadStatus(r.Path, c.Ct);
                }
                catch (GitFailure e)
                {
                    errors[i] = e.Message;
                }
            }));

            var sb = new StringBuilder();
            var arr = new JsonArray();
            var w = repos.Max(r => (r.IsProject ? "project" : r.Name).Length);
            for (var i = 0; i < repos.Count; i++)
            {
                var r = repos[i];
                var label = (r.IsProject ? "project" : r.Name).PadRight(w);
                if (r.Problem != null || errors[i] != null)
                {
                    sb.Append($"✗ {label}  недоступен: {r.Problem ?? errors[i]}  ({r.Path}, {r.Source})\n");
                    arr.Add(new JsonObject { ["repo"] = r.IsProject ? "project" : r.Name, ["path"] = r.Path, ["source"] = r.Source, ["problem"] = r.Problem ?? errors[i] });
                    continue;
                }

                var s = statuses[i];
                var state = s.Clean ? "чисто" : $"изменений {s.Changed.Count + s.Conflicts.Count}, новых {s.Untracked.Count}";
                sb.Append($"{(s.Clean ? "•" : "*")} {label}  {BranchLine(s)}  {state}{(r.Ambiguous ? "  (имя неоднозначно — --repo путём)" : "")}  {r.Path}\n");
                var o = StatusJson(r, s);
                o["source"] = r.Source;
                o["ambiguous"] = r.Ambiguous;
                arr.Add(o);
            }

            foreach (var n in notes) sb.Append("! ").Append(n).Append('\n');
            sb.Append($"ahead/behind — по локальной копии upstream (без fetch). Список: gitRepos в ProjectSettings/Nexus.json, repos в {DaemonPaths.GitReposFile}\n");
            return (0, sb.ToString(), Json.Write(new JsonObject { ["repos"] = arr, ["notes"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray()) }));
        }

        private static async Task<(int, string, string)> Status(Ctx c)
        {
            await Prepare(c, Array.Empty<string>(), Array.Empty<string>(), 0, false);
            var s = await ReadStatus(c.Repo.Path, c.Ct);
            var sb = new StringBuilder($"{RepoLabel(c.Repo)}: {BranchLine(s)}\n");
            const int max = 300;
            var shown = 0;

            void Line(string text)
            {
                if (shown++ < max) sb.Append(text).Append('\n');
            }

            var staged = s.Changed.Where(f => f.XY[0] != '.').ToList();
            var unstaged = s.Changed.Where(f => f.XY[1] != '.').ToList();
            if (s.Conflicts.Count > 0)
            {
                Line($"Конфликты ({s.Conflicts.Count}):");
                foreach (var f in s.Conflicts) Line("  ! " + f);
            }

            if (staged.Count > 0)
            {
                Line($"В индексе ({staged.Count}):");
                foreach (var f in staged) Line($"  {Kind(f.XY[0])}: {f.Path}{(f.Orig != null ? " ← " + f.Orig : "")}");
            }

            if (unstaged.Count > 0)
            {
                Line($"Не в индексе ({unstaged.Count}):");
                foreach (var f in unstaged) Line($"  {Kind(f.XY[1])}: {f.Path}");
            }

            if (s.Untracked.Count > 0)
            {
                Line($"Новые ({s.Untracked.Count}):");
                foreach (var f in s.Untracked) Line("  " + f);
            }

            if (shown > max) sb.Append($"… и ещё {shown - max} строк\n");
            if (s.Clean) sb.Append("Дерево чистое.\n");

            var data = StatusJson(c.Repo, s);
            data["staged"] = new JsonArray(staged.Take(1000).Select(f => (JsonNode)new JsonObject { ["xy"] = f.XY, ["path"] = f.Path, ["from"] = f.Orig }).ToArray());
            data["unstaged"] = new JsonArray(unstaged.Take(1000).Select(f => (JsonNode)new JsonObject { ["xy"] = f.XY, ["path"] = f.Path }).ToArray());
            data["untrackedFiles"] = new JsonArray(s.Untracked.Take(1000).Select(f => (JsonNode)f).ToArray());
            data["conflictFiles"] = new JsonArray(s.Conflicts.Take(1000).Select(f => (JsonNode)f).ToArray());
            return (0, sb.ToString(), Json.Write(data));
        }

        private const string LogFormat = "--format=%H%x1f%an%x1f%ae%x1f%ad%x1f%s%x1e";

        private static async Task<(int, string, string)> LogCmd(Ctx c)
        {
            await Prepare(c, new[] { "--max", "--range", "--path" }, Array.Empty<string>(), 0, false);
            var max = IntArg(c.Args, "--max", 20, 1, 1000);
            var args = new List<string> { "log", "--max-count=" + max, "--date=format:%Y-%m-%d %H:%M", LogFormat, "--end-of-options" };
            var range = c.Args.Get("--range");
            if (range != null) args.Add(Rev(range, "--range"));
            var path = c.Args.Get("--path");
            if (path != null)
            {
                args.Add("--");
                args.Add(PathSpec(c.Repo, path));
            }

            var r = await Git(c, args.ToArray());
            if (!r.Ok)
            {
                // пустой репозиторий (нет коммитов) — не ошибка
                if ((await Git(c, "rev-parse", "--verify", "--quiet", "HEAD")).Ok || range != null) throw new GitFailure("git log: " + r.FirstError);
                return (0, $"{RepoLabel(c.Repo)}: коммитов нет\n", Json.Write(new JsonObject { ["commits"] = new JsonArray() }));
            }

            var sb = new StringBuilder();
            var arr = new JsonArray();
            foreach (var rec in r.Out.Split('\x1e'))
            {
                var f = rec.Trim('\n').Split('\x1f');
                if (f.Length < 5) continue;
                sb.Append($"{f[0].Substring(0, Math.Min(10, f[0].Length))} {f[3]} {f[1]} <{f[2]}>  {f[4]}\n");
                arr.Add(new JsonObject { ["hash"] = f[0], ["author"] = f[1], ["email"] = f[2], ["date"] = f[3], ["subject"] = f[4] });
            }

            if (arr.Count == 0) sb.Append("коммитов нет\n");
            else if (arr.Count == max) sb.Append($"(показано {max}: --max N, --range a..b)\n");
            return (0, sb.ToString(), Json.Write(new JsonObject { ["repo"] = c.Repo.Name, ["commits"] = arr }));
        }

        private static async Task<(int, string, string)> Diff(Ctx c)
        {
            await Prepare(c, new[] { "--path", "--max-lines" }, new[] { "--staged", "--cached", "--stat" }, 0, false);
            var max = IntArg(c.Args, "--max-lines", 400, 1, 20000);
            var args = new List<string> { "diff", "--no-ext-diff" };
            if (c.Args.Has("--staged") || c.Args.Has("--cached")) args.Add("--cached");
            if (c.Args.Has("--stat")) args.Add("--stat");
            var path = c.Args.Get("--path");
            if (path != null)
            {
                args.Add("--");
                args.Add(PathSpec(c.Repo, path));
            }

            var r = await Git(c, args.ToArray());
            if (!r.Ok) throw new GitFailure("git diff: " + r.FirstError);
            var (text, lines, cut) = Lines(r.Out, max);
            if (lines == 0) text = (c.Args.Has("--staged") || c.Args.Has("--cached") ? "Индекс не отличается от HEAD.\n" : "Рабочее дерево не отличается от индекса.\n");
            if (cut || r.Truncated) text += $"… показано {max} из {lines}{(r.Truncated ? "+" : "")} строк (--max-lines N, --path p, --stat)\n";
            return (0, text, Json.Write(new JsonObject { ["repo"] = c.Repo.Name, ["lines"] = lines, ["truncated"] = cut || r.Truncated }));
        }

        private static async Task<(int, string, string)> Branch(Ctx c)
        {
            await Prepare(c, Array.Empty<string>(), new[] { "--all" }, 0, false);
            var args = new List<string> { "for-each-ref", "--format=%(HEAD)%00%(refname)%00%(objectname:short)%00%(upstream:short)%00%(upstream:track)%00%(contents:subject)", "refs/heads" };
            if (c.Args.Has("--all")) args.Add("refs/remotes");
            var r = await Git(c, args.ToArray());
            if (!r.Ok) throw new GitFailure("git for-each-ref: " + r.FirstError);
            var sb = new StringBuilder();
            var arr = new JsonArray();
            foreach (var line in r.Out.Split('\n'))
            {
                var f = line.Split('\0');
                if (f.Length < 6) continue;
                var remote = f[1].StartsWith("refs/remotes/", StringComparison.Ordinal);
                var name = remote ? f[1].Substring(13) : f[1].StartsWith("refs/heads/", StringComparison.Ordinal) ? f[1].Substring(11) : f[1];
                if (remote && name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
                var current = f[0] == "*";
                sb.Append(current ? "* " : "  ").Append(remote ? "remotes/" + name : name).Append("  ").Append(f[2]);
                if (f[3].Length > 0) sb.Append(" → ").Append(f[3]).Append(f[4].Length > 0 ? " " + f[4] : "");
                sb.Append("  ").Append(Cut(f[5], 80)).Append('\n');
                arr.Add(new JsonObject { ["name"] = name, ["remote"] = remote, ["current"] = current, ["head"] = f[2], ["upstream"] = f[3].Length > 0 ? f[3] : null, ["track"] = f[4] });
            }

            if (arr.Count == 0) sb.Append("веток нет\n");
            return (0, sb.ToString(), Json.Write(new JsonObject { ["repo"] = c.Repo.Name, ["branches"] = arr }));
        }

        private static async Task<(int, string, string)> Show(Ctx c)
        {
            await Prepare(c, new[] { "--max-lines" }, new[] { "--stat" }, 1, false);
            if (c.Args.Pos.Count == 0) throw new UsageException("нужна ревизия: git.show <rev>");
            var rev = Rev(c.Args.Pos[0], "ревизия");
            var max = IntArg(c.Args, "--max-lines", 400, 1, 20000);
            var args = new List<string> { "show", "--no-ext-diff", "--format=fuller" };
            if (c.Args.Has("--stat")) args.Add("--stat");
            args.Add("--end-of-options");
            args.Add(rev);
            var r = await Git(c, args.ToArray());
            if (!r.Ok) throw new GitFailure("git show: " + r.FirstError);
            var (text, lines, cut) = Lines(r.Out, max);
            if (cut || r.Truncated) text += $"… показано {max} из {lines}{(r.Truncated ? "+" : "")} строк (--max-lines N, --stat)\n";
            return (0, text, Json.Write(new JsonObject { ["repo"] = c.Repo.Name, ["rev"] = rev, ["lines"] = lines, ["truncated"] = cut || r.Truncated }));
        }

        private static async Task<(int, string, string)> Fetch(Ctx c)
        {
            var a = Parse(c.Call.Args, new[] { "--repo" }, new[] { "--all" }, 0);
            List<GitRepo> targets;
            if (a.Has("--all"))
            {
                if (a.Get("--repo") != null) throw new UsageException("--repo и --all вместе нельзя");
                c.Phase("проверяю белый список");
                targets = (await GitRepos.List(c.Project, c.Ct)).Where(r => r.Problem == null).ToList();
            }
            else
            {
                c.Args = a;
                c.Phase("проверяю репозиторий");
                var (repo, error) = await GitRepos.Resolve(c.Project, a.Get("--repo"), c.Ct);
                if (repo == null) throw new GitFailure(error);
                targets = new List<GitRepo> { repo };
            }

            var results = new (GitRepo Repo, GitResult Result)[targets.Count];
            var done = 0;
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(targets.Select(async (repo, i) =>
            {
                await gate.WaitAsync(c.Ct);
                try
                {
                    results[i] = (repo, await GitRunner.Run(repo.Path, new[] { "fetch" }, GitRunner.NetworkTimeout, c.Ct));
                }
                finally
                {
                    gate.Release();
                    c.Phase($"fetch: {Interlocked.Increment(ref done)}/{targets.Count}");
                }
            }));

            var sb = new StringBuilder();
            var arr = new JsonArray();
            var failed = 0;
            foreach (var (repo, r) in results)
            {
                var label = RepoLabel(repo);
                if (r.Ok)
                {
                    var changes = GitRunner.Mask(r.Err).Split('\n').Count(l => l.Contains("->", StringComparison.Ordinal));
                    sb.Append($"✓ {label}: {(changes > 0 ? $"обновлено ссылок: {changes}" : "без изменений")}\n");
                }
                else
                {
                    failed++;
                    sb.Append($"✗ {label}: {r.FirstError}\n");
                }

                arr.Add(new JsonObject { ["repo"] = repo.IsProject ? "project" : repo.Name, ["ok"] = r.Ok, ["error"] = r.Ok ? null : r.FirstError });
            }

            if (targets.Count == 0) sb.Append("В белом списке нет доступных репозиториев.\n");
            return (failed > 0 ? 1 : 0, sb.ToString(), Json.Write(new JsonObject { ["repos"] = arr }));
        }
    }
}
