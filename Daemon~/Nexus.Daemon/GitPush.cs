using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// git.push — два шага. Шаг 1 ничего не отправляет: отчёт (ветка → удалённая ветка, уходящие коммиты и теги
    /// с полными сообщениями и проверкой следа ИИ, предупреждения по диффу) и одноразовый код на 10 минут, привязанный
    /// к репозиторию, HEAD, набору коммитов и тегов и цели. Шаг 2 (<c>--confirm код</c>) пересчитывает всё заново и
    /// отправляет только при полном совпадении и чистой проверке. Принудительной отправки нет вовсе.
    /// </summary>
    internal static partial class GitCommands
    {
        private static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(10);
        private const int MaxOutgoing = 500;
        private const int MaxDiffWarnings = 50;

        /// <summary>Выданные коды подтверждения: код → что подтверждено. Живут в памяти демона, одноразовые.</summary>
        private static readonly ConcurrentDictionary<string, PendingPush> Codes = new ConcurrentDictionary<string, PendingPush>(StringComparer.Ordinal);

        private sealed class PendingPush
        {
            public string Project;
            public string Repo;
            public string Fingerprint;
            public DateTime Expires;
            public string TagMode;
            public string TagName;
            public bool SetUpstream;
            public bool AllowAi;
        }

        private sealed class CommitInfo
        {
            public string Hash, Author, AuthorEmail, Committer, CommitterEmail, Date, Message;
            public List<string> Violations = new List<string>();
            public bool FromTag;
        }

        private sealed class TagInfo
        {
            public string Name, Object, Target, Type, Tagger, TaggerEmail, Message;
            public List<string> Violations = new List<string>();
        }

        private sealed class PushPlan
        {
            public string Branch, Head, Remote, RemoteBranch, Upstream, Base;
            public bool RemoteBranchExists, SetUpstream, AllowAi;
            public string TagMode = "none", TagName;
            public int Behind;
            public readonly List<CommitInfo> Commits = new List<CommitInfo>();
            public readonly List<TagInfo> Tags = new List<TagInfo>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> DiffWarnings = new List<string>();
            public readonly List<string> Violations = new List<string>();

            public bool Nothing => Commits.Count == 0 && Tags.Count == 0 && RemoteBranchExists;

            public string Fingerprint(string repo)
            {
                var sb = new StringBuilder();
                sb.Append(repo).Append('\n').Append(Remote).Append('\n').Append(Branch).Append("→").Append(RemoteBranch).Append('\n')
                    .Append(Head).Append('\n').Append(SetUpstream).Append(AllowAi).Append('\n');
                foreach (var c in Commits.Select(x => x.Hash).OrderBy(x => x, StringComparer.Ordinal)) sb.Append(c).Append('\n');
                foreach (var t in Tags.OrderBy(x => x.Name, StringComparer.Ordinal)) sb.Append(t.Name).Append('=').Append(t.Object).Append('\n');
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
            }
        }

        private static async Task<(int, string, string)> Push(Ctx c)
        {
            await Prepare(c, new[] { "--tag", "--confirm" }, new[] { "--tags", "--set-upstream", "-u", "--allow-ai-marks" }, 0, true);
            var code = c.Args.Get("--confirm");
            foreach (var expired in Codes.Where(kv => kv.Value.Expires < DateTime.UtcNow).Select(kv => kv.Key).ToList()) Codes.TryRemove(expired, out _);

            if (code != null)
            {
                if (c.Args.Has("--tags") || c.Args.Get("--tag") != null || c.Args.Has("--set-upstream") || c.Args.Has("-u") || c.Args.Has("--allow-ai-marks"))
                    throw new UsageException("с --confirm указывается только --repo: что отправлять, решено на шаге 1");
                return await PushConfirmed(c, code.Trim().ToUpperInvariant());
            }

            if (c.Args.Has("--tags") && c.Args.Get("--tag") != null) throw new UsageException("--tags или --tag, не оба");
            var plan = new PushPlan
            {
                SetUpstream = c.Args.Has("--set-upstream") || c.Args.Has("-u"),
                AllowAi = c.Args.Has("--allow-ai-marks"),
                TagMode = c.Args.Has("--tags") ? "all" : c.Args.Get("--tag") != null ? "one" : "none",
                TagName = c.Args.Get("--tag"),
            };
            await BuildPlan(c, plan, true);

            var text = Report(c, plan);
            var data = PlanJson(c, plan);
            if (plan.Nothing)
                return (0, text + "Отправлять нечего.\n", Json.Write(data));
            if (plan.Violations.Count > 0 && !plan.AllowAi)
            {
                data["reason"] = "ai-trace";
                return (1, text + "✗ Проверка следа ИИ не пройдена — код не выдан, ничего не отправлено. Исправьте историю сами " +
                           "(или, только если пользователь сам попросил пометку, --allow-ai-marks).\n", Json.Write(data));
            }

            // новый код заменяет прежние коды этого репозитория
            foreach (var old in Codes.Where(kv => kv.Value.Repo == c.Repo.Path).Select(kv => kv.Key).ToList()) Codes.TryRemove(old, out _);
            var newCode = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 8);
            Codes[newCode] = new PendingPush
            {
                Project = c.Project.Id, Repo = c.Repo.Path, Fingerprint = plan.Fingerprint(c.Repo.Path), Expires = DateTime.UtcNow + CodeLife,
                TagMode = plan.TagMode, TagName = plan.TagName, SetUpstream = plan.SetUpstream, AllowAi = plan.AllowAi,
            };
            data["code"] = newCode;
            data["expires"] = (DateTime.UtcNow + CodeLife).ToString("O");
            data["preview"] = true;
            data["reason"] = "needs-confirmation";
            var repoArg = c.Repo.IsProject ? "project" : c.Repo.Name;
            text += $"Ничего не отправлено. Код подтверждения (10 минут, одноразовый): {newCode}\n" +
                    $"Отправить: git.push --repo {repoArg} --confirm {newCode}\n";
            return (NeedsConfirmation, text, Json.Write(data));
        }

        private static async Task<(int, string, string)> PushConfirmed(Ctx c, string code)
        {
            if (!Codes.TryRemove(code, out var pending) || pending.Expires < DateTime.UtcNow)
                throw new GitFailure("Код недействителен (истёк, уже использован или не выдавался) — пройдите шаг 1 заново: git.push без --confirm.");
            if (pending.Repo != c.Repo.Path || pending.Project != c.Project.Id)
                throw new GitFailure("Код выдан для другого репозитория — пройдите шаг 1 заново.");

            using (await LockRepo(c.Repo, c.Phase, c.Ct))
            {
                var plan = new PushPlan { SetUpstream = pending.SetUpstream, AllowAi = pending.AllowAi, TagMode = pending.TagMode, TagName = pending.TagName };
                await BuildPlan(c, plan, false);
                if (plan.Fingerprint(c.Repo.Path) != pending.Fingerprint)
                    throw new GitFailure("С шага 1 изменились HEAD, набор коммитов или тегов, либо цель — код недействителен. Пройдите шаг 1 заново.");
                if (plan.Violations.Count > 0 && !plan.AllowAi)
                    throw new GitFailure("Проверка следа ИИ не пройдена:\n" + Bullets(plan.Violations));

                var args = new List<string> { "push", "--porcelain", "--no-follow-tags", "--recurse-submodules=no" };
                if (plan.SetUpstream) args.Add("--set-upstream");
                args.Add(plan.Remote);
                args.Add($"refs/heads/{plan.Branch}:refs/heads/{plan.RemoteBranch}");
                foreach (var t in plan.Tags) args.Add($"refs/tags/{t.Name}:refs/tags/{t.Name}");

                c.Phase($"git push {plan.Remote} {plan.RemoteBranch}");
                Log.Info($"git {c.Repo.Name}: push {plan.Branch} → {plan.Remote}/{plan.RemoteBranch}, коммитов {plan.Commits.Count}, тегов {plan.Tags.Count}");
                var r = await GitWrite(c, GitRunner.NetworkTimeout, null, args.ToArray());
                var output = GitRunner.Mask((r.Out + "\n" + r.Err).Trim());
                var data = PlanJson(c, plan);
                data["pushed"] = r.Ok;
                if (!r.Ok)
                {
                    var hint = output.Contains("[rejected]", StringComparison.Ordinal) || output.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                        ? "\nRemote впереди — сначала git.fetch и git.pull (или git.merge); принудительной отправки нет."
                        : "";
                    Log.Warn($"git {c.Repo.Name}: push не прошёл: {r.FirstError}");
                    return (1, $"✗ push не прошёл:\n{Cut(output, 4000)}{hint}\n", Json.Write(data));
                }

                var sb = new StringBuilder($"✓ {RepoLabel(c.Repo)}: отправлено {plan.Branch} → {plan.Remote}/{plan.RemoteBranch}: коммитов {plan.Commits.Count}");
                if (plan.Tags.Count > 0) sb.Append(", теги ").Append(string.Join(", ", plan.Tags.Select(t => t.Name)));
                sb.Append('\n');
                if (plan.AllowAi) sb.Append(AllowNote(true, plan.Violations));
                sb.Append(Cut(output, 2000)).Append('\n');
                return (0, sb.ToString(), Json.Write(data));
            }
        }

        /// <summary>Посчитать, что уйдёт: цель, коммиты, теги, проверки. firstStep — с подробными предупреждениями по диффу.</summary>
        private static async Task BuildPlan(Ctx c, PushPlan plan, bool firstStep)
        {
            c.Phase("считаю, что уйдёт");
            plan.Branch = await CurrentBranch(c) ?? throw new GitFailure("HEAD отсоединён — push только с ветки.");
            plan.Head = (await Git(c, "rev-parse", "--verify", "--quiet", "HEAD")).Out.Trim();
            if (plan.Head.Length == 0) throw new GitFailure("В ветке нет коммитов.");

            var upstream = await Git(c, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
            if (upstream.Ok)
            {
                plan.Remote = (await Git(c, "config", "--get", $"branch.{plan.Branch}.remote")).Out.Trim();
                var merge = (await Git(c, "config", "--get", $"branch.{plan.Branch}.merge")).Out.Trim();
                if (plan.Remote.Length == 0 || plan.Remote == "." || !merge.StartsWith("refs/heads/", StringComparison.Ordinal))
                    throw new GitFailure($"Upstream ветки {plan.Branch} ({upstream.Out.Trim()}) — не ветка remote; push через демон не поддержан.");
                plan.RemoteBranch = merge.Substring(11);
                plan.Upstream = upstream.Out.Trim();
            }
            else
            {
                plan.Remote = "origin";
                plan.RemoteBranch = plan.Branch;
            }

            if (!(await Git(c, "remote", "get-url", plan.Remote)).Ok) throw new GitFailure($"Нет remote «{plan.Remote}».");
            var tracking = $"refs/remotes/{plan.Remote}/{plan.RemoteBranch}";
            plan.RemoteBranchExists = await RefExists(c, tracking);

            string[] range;
            if (plan.Upstream != null)
            {
                range = new[] { "@{u}..HEAD" };
                plan.Base = plan.Upstream;
                int.TryParse((await Git(c, "rev-list", "--count", "HEAD..@{u}")).Out.Trim(), out plan.Behind);
            }
            else if (plan.RemoteBranchExists)
            {
                range = new[] { $"{tracking}..HEAD" };
                plan.Base = $"{plan.Remote}/{plan.RemoteBranch}";
                int.TryParse((await Git(c, "rev-list", "--count", $"HEAD..{tracking}")).Out.Trim(), out plan.Behind);
            }
            else
            {
                range = new[] { "HEAD", "--not", "--remotes" };
                plan.Base = "коммиты, которых нет ни в одной удалённой ветке";
            }

            if (plan.Behind > 0) plan.Warnings.Add($"{plan.Base} впереди на {plan.Behind} (по локальной копии) — push будет отклонён; сначала git.pull или git.merge");
            if (plan.Upstream == null && !plan.SetUpstream) plan.Warnings.Add($"у {plan.Branch} нет upstream: отправка в {plan.Remote}/{plan.RemoteBranch}, upstream не настраивается (--set-upstream)");

            await ReadCommits(c, plan, range, false);

            // теги: сверка с remote (сеть) — только при --tags/--tag
            if (plan.TagMode != "none") await ReadTags(c, plan);

            var (name, email) = await Identity(c);
            foreach (var commit in plan.Commits)
            {
                commit.Violations = CheckCommit(commit.Author, commit.AuthorEmail, commit.Committer, commit.CommitterEmail, commit.Message, name, email);
                plan.Violations.AddRange(commit.Violations.Select(v => $"{Short(commit.Hash)}: {v}"));
            }

            foreach (var t in plan.Tags)
            {
                if (t.Type == "tag")
                {
                    t.Violations.AddRange(AiTraceCheck.Message(t.Message));
                    var id = AiTraceCheck.Identity(t.Tagger, t.TaggerEmail);
                    if (id != null) t.Violations.Add("автор тега: " + id);
                    if (name.Length > 0 && (t.Tagger != name || !string.Equals(t.TaggerEmail, email, StringComparison.OrdinalIgnoreCase)))
                        t.Violations.Add($"автор тега {t.Tagger} <{t.TaggerEmail}> ≠ {name} <{email}>");
                }
                else plan.Warnings.Add($"тег {t.Name} — лёгкий (без сообщения и автора); выпуски — аннотированными (git.tag)");

                plan.Violations.AddRange(t.Violations.Select(v => $"тег {t.Name}: {v}"));
            }

            if (firstStep && plan.Commits.Count > 0) await ScanDiff(c, plan, range);
        }

        private static async Task ReadCommits(Ctx c, PushPlan plan, string[] range, bool fromTag)
        {
            var args = new List<string> { "log", $"--max-count={MaxOutgoing + 1}", "--date=format:%Y-%m-%d %H:%M", "--format=%H%x1f%an%x1f%ae%x1f%cn%x1f%ce%x1f%ad%x1f%B%x1e" };
            args.AddRange(range);
            var r = await Git(c, args.ToArray());
            if (!r.Ok) throw new GitFailure("git log: " + r.FirstError);
            foreach (var rec in r.Out.Split('\x1e'))
            {
                var f = rec.TrimStart('\n').Split('\x1f');
                if (f.Length < 7) continue;
                if (plan.Commits.Any(x => x.Hash == f[0])) continue;
                plan.Commits.Add(new CommitInfo
                {
                    Hash = f[0], Author = f[1], AuthorEmail = f[2], Committer = f[3], CommitterEmail = f[4], Date = f[5],
                    Message = f[6].TrimEnd('\n'), FromTag = fromTag,
                });
            }

            if (plan.Commits.Count > MaxOutgoing) throw new GitFailure($"Уходит больше {MaxOutgoing} коммитов — через демон не отправляется (проверьте ветку и upstream).");
        }

        private static async Task ReadTags(Ctx c, PushPlan plan)
        {
            List<string> names;
            if (plan.TagMode == "one")
            {
                if (!await RefExists(c, "refs/tags/" + plan.TagName)) throw new GitFailure($"Нет локального тега {plan.TagName}.");
                names = new List<string> { plan.TagName };
            }
            else names = (await Must(c, "git for-each-ref", "for-each-ref", "--format=%(refname:short)", "refs/tags")).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

            c.Phase($"сверяю теги с {plan.Remote} (сеть)");
            var ls = await GitRunner.Run(c.Repo.Path, new[] { "ls-remote", "--tags", "--refs", plan.Remote }, GitRunner.NetworkTimeout, c.Ct, readOnly: true);
            if (!ls.Ok) throw new GitFailure($"git ls-remote {plan.Remote}: {ls.FirstError}");
            var remote = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in ls.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && parts[1].StartsWith("refs/tags/", StringComparison.Ordinal)) remote[parts[1].Substring(10)] = parts[0].Trim();
            }

            foreach (var name in names)
            {
                var info = (await Must(c, "git for-each-ref", "for-each-ref",
                    "--format=%(objecttype)%1f%(objectname)%1f%(*objectname)%1f%(taggername)%1f%(taggeremail)%1f%(contents)", "refs/tags/" + name)).Split('\x1f');
                if (info.Length < 6) continue;
                var obj = info[1];
                if (remote.TryGetValue(name, out var there))
                {
                    if (there == obj) continue;
                    if (plan.TagMode == "one") throw new GitFailure($"Тег {name} на {plan.Remote} указывает на другое — перезапись тегов запрещена.");
                    plan.Warnings.Add($"тег {name} на {plan.Remote} другой — пропущен (перезаписи нет)");
                    continue;
                }

                plan.Tags.Add(new TagInfo
                {
                    Name = name, Object = obj, Type = info[0], Target = info[0] == "tag" ? info[2] : obj,
                    Tagger = info[3], TaggerEmail = info[4].Trim('<', '>'), Message = info[5].TrimEnd('\n'),
                });
            }

            // коммиты, которые уйдут вместе с тегами (не в ветке и ни в одной удалённой ветке)
            if (plan.Tags.Count > 0)
            {
                var range = new List<string>();
                range.AddRange(plan.Tags.Select(t => t.Target).Distinct());
                range.Add("--not");
                range.Add("--remotes");
                range.Add("HEAD");
                await ReadCommits(c, plan, range.ToArray(), true);
            }
        }

        /// <summary>Добавленные строки диффа уходящих коммитов с маркерами ИИ — предупреждения (файл:строка).</summary>
        private static async Task ScanDiff(Ctx c, PushPlan plan, string[] range)
        {
            c.Phase("просматриваю дифф уходящих коммитов");
            var args = new List<string> { "log", "-p", "--no-ext-diff", "--no-textconv", "--unified=0", $"--max-count={MaxOutgoing}", "--format=%x1eC%H" };
            args.AddRange(range);
            var r = await GitRunner.Run(c.Repo.Path, args, GitRunner.DefaultTimeout, c.Ct, maxChars: 16 << 20, readOnly: true);
            if (!r.Ok)
            {
                plan.DiffWarnings.Add("дифф не прочитан: " + r.FirstError);
                return;
            }

            string commit = null, file = null;
            var line = 0;
            var inHunk = false;
            foreach (var raw in r.Out.Split('\n'))
            {
                if (raw.StartsWith("\u001eC", StringComparison.Ordinal))
                {
                    commit = raw.Substring(2, Math.Min(10, raw.Length - 2));
                    inHunk = false;
                    continue;
                }

                if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    inHunk = false;
                    file = null;
                    continue;
                }

                if (!inHunk && raw.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    file = raw.StartsWith("+++ b/", StringComparison.Ordinal) ? raw.Substring(6) : raw.Substring(4);
                    continue;
                }

                if (raw.StartsWith("@@ ", StringComparison.Ordinal))
                {
                    inHunk = true;
                    var plus = raw.IndexOf(" +", StringComparison.Ordinal);
                    var end = plus < 0 ? -1 : raw.IndexOfAny(new[] { ',', ' ' }, plus + 2);
                    line = plus >= 0 && end > plus && int.TryParse(raw.AsSpan(plus + 2, end - plus - 2), out var start) ? start : 0;
                    continue;
                }

                if (!inHunk) continue;
                if (raw.StartsWith("+", StringComparison.Ordinal))
                {
                    var why = AiTraceCheck.Line(raw.Substring(1));
                    if (why != null)
                    {
                        if (plan.DiffWarnings.Count < MaxDiffWarnings) plan.DiffWarnings.Add($"{commit} {file}:{line}: {why} — «{Cut(raw.Substring(1).Trim(), 100)}»");
                        else if (plan.DiffWarnings.Count == MaxDiffWarnings) plan.DiffWarnings.Add("… дальше не показано");
                    }

                    line++;
                }
                else if (raw.StartsWith(" ", StringComparison.Ordinal)) line++;
            }

            if (r.Truncated) plan.DiffWarnings.Add("дифф больше 16 МБ — просмотрено начало");
        }

        private static string Report(Ctx c, PushPlan plan)
        {
            var sb = new StringBuilder();
            sb.Append($"Репозиторий: {RepoLabel(c.Repo)} ({c.Repo.Path})\n");
            sb.Append($"Ветка: {plan.Branch} → {plan.Remote}/{plan.RemoteBranch}");
            sb.Append(plan.Upstream != null ? " (upstream)" : plan.RemoteBranchExists ? " (без upstream)" : " (новая ветка на remote)");
            if (plan.SetUpstream) sb.Append(", --set-upstream");
            sb.Append($"\nHEAD: {plan.Head}\nБаза: {plan.Base}\n");
            if (plan.AllowAi) sb.Append("! --allow-ai-marks: проверка следа ИИ отключена по явной просьбе пользователя\n");

            sb.Append($"Уходящие коммиты ({plan.Commits.Count}):\n");
            foreach (var cm in plan.Commits)
            {
                sb.Append($"── {cm.Hash}{(cm.FromTag ? " (уходит с тегом)" : "")}\n");
                sb.Append($"   автор: {cm.Author} <{cm.AuthorEmail}>, {cm.Date}\n");
                sb.Append($"   коммиттер: {cm.Committer} <{cm.CommitterEmail}>\n");
                sb.Append("   сообщение:\n").Append(Quote(cm.Message));
                sb.Append("   проверка: ").Append(cm.Violations.Count == 0 ? "чисто\n" : "\n" + Bullets(cm.Violations, "✗", "     "));
                foreach (var m in AiTraceCheck.Mentions(cm.Message)) sb.Append("   ! ").Append(m).Append('\n');
            }

            if (plan.TagMode != "none")
            {
                sb.Append($"Уходящие теги ({plan.Tags.Count}):\n");
                foreach (var t in plan.Tags)
                {
                    sb.Append($"── {t.Name} → {Short(t.Target)} ({(t.Type == "tag" ? $"аннотированный, {t.Tagger} <{t.TaggerEmail}>" : "лёгкий")})\n");
                    if (t.Type == "tag") sb.Append("   сообщение:\n").Append(Quote(t.Message));
                    sb.Append("   проверка: ").Append(t.Violations.Count == 0 ? "чисто\n" : "\n" + Bullets(t.Violations, "✗", "     "));
                }
            }

            if (plan.Warnings.Count > 0) sb.Append("Предупреждения:\n").Append(Bullets(plan.Warnings, "!"));
            if (plan.DiffWarnings.Count > 0) sb.Append("Дифф (добавленные строки с маркерами ИИ — не блокирует):\n").Append(Bullets(plan.DiffWarnings, "!"));
            sb.Append(plan.Violations.Count == 0 ? "Проверка следа ИИ: чисто\n" : $"Проверка следа ИИ: нарушений {plan.Violations.Count}{(plan.AllowAi ? " (пропущены: --allow-ai-marks)" : "")}\n");
            return sb.ToString();
        }

        private static JsonObject PlanJson(Ctx c, PushPlan plan) => new JsonObject
        {
            ["repo"] = RepoLabel(c.Repo),
            ["path"] = c.Repo.Path,
            ["branch"] = plan.Branch,
            ["remote"] = plan.Remote,
            ["remoteBranch"] = plan.RemoteBranch,
            ["upstream"] = plan.Upstream,
            ["head"] = plan.Head,
            ["setUpstream"] = plan.SetUpstream,
            ["allowAiMarks"] = plan.AllowAi,
            ["commits"] = new JsonArray(plan.Commits.Select(cm => (JsonNode)new JsonObject
            {
                ["hash"] = cm.Hash, ["author"] = $"{cm.Author} <{cm.AuthorEmail}>", ["committer"] = $"{cm.Committer} <{cm.CommitterEmail}>",
                ["date"] = cm.Date, ["message"] = cm.Message, ["fromTag"] = cm.FromTag,
                ["violations"] = new JsonArray(cm.Violations.Select(v => (JsonNode)v).ToArray()),
            }).ToArray()),
            ["tags"] = new JsonArray(plan.Tags.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["target"] = t.Target, ["annotated"] = t.Type == "tag", ["tagger"] = t.Type == "tag" ? $"{t.Tagger} <{t.TaggerEmail}>" : null,
                ["message"] = t.Message, ["violations"] = new JsonArray(t.Violations.Select(v => (JsonNode)v).ToArray()),
            }).ToArray()),
            ["warnings"] = new JsonArray(plan.Warnings.Select(v => (JsonNode)v).ToArray()),
            ["diffWarnings"] = new JsonArray(plan.DiffWarnings.Select(v => (JsonNode)v).ToArray()),
            ["violations"] = new JsonArray(plan.Violations.Select(v => (JsonNode)v).ToArray()),
        };
    }
}
