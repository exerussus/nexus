using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Записывающие git-команды демона: checkout, pull, add, commit, merge, tag. Без <c>--yes</c> каждая только
    /// показывает, что будет сделано (код 8 — нужно подтверждение). Ничего не правится молча: отказ — с причиной.
    /// </summary>
    internal static partial class GitCommands
    {
        private const int WriteTimeout = 120;

        private static readonly Regex TagName = new Regex(@"^v\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$", RegexOptions.CultureInvariant);

        /// <summary>Предпросмотр вместо выполнения: код 8, в данных preview.</summary>
        private static (int, string, string) Preview(Ctx c, StringBuilder plan, JsonObject data)
        {
            var text = $"Не выполнено — нужно подтверждение ({c.Call.Command} … --yes). Будет сделано:\n" + plan;
            data["repo"] = RepoLabel(c.Repo);
            data["path"] = c.Repo.Path;
            data["preview"] = true;
            data["reason"] = "needs-confirmation";
            return (NeedsConfirmation, text, Json.Write(data));
        }

        private static string Bullets(IEnumerable<string> lines, string mark = "✗", string indent = "  ") => string.Concat(lines.Select(l => $"{indent}{mark} {l}\n"));

        private static string Quote(string message) =>
            string.Concat(message.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => "    | " + l + "\n"));

        private static string AllowNote(bool allowAi, List<string> skipped) =>
            !allowAi ? "" : "! --allow-ai-marks: проверка следа ИИ отключена по явной просьбе пользователя" +
                            (skipped.Count > 0 ? $" (пропущено нарушений: {skipped.Count})" : "") + "\n";

        /// <summary>Сообщение из -m (несколько -m — абзацы, как у git).</summary>
        private static string MessageArg(Ctx c)
        {
            var parts = c.Args.All("--message").Select(m => m.Replace("\r\n", "\n").Trim('\n')).Where(m => m.Trim().Length > 0).ToList();
            if (parts.Count == 0) throw new UsageException("нужно сообщение: -m \"текст\"");
            return string.Join("\n\n", parts);
        }

        /// <summary>Проверка сообщения и личности; нарушения без --allow-ai-marks — отказ (ничего не сделано).</summary>
        private static List<string> CheckOrRefuse(string what, string message, string name, string email, bool allowAi)
        {
            var v = AiTraceCheck.Message(message);
            var id = AiTraceCheck.Identity(name, email);
            if (id != null) v.Add("личность репозитория: " + id);
            if (v.Count > 0 && !allowAi)
                throw new GitFailure($"{what} не прошло проверку следа ИИ — ничего не сделано, ничего не исправлено:\n{Bullets(v)}" +
                                     "Исправьте сообщение сами (или, только если пользователь сам попросил пометку, --allow-ai-marks).",
                    new JsonObject { ["reason"] = "ai-trace", ["violations"] = new JsonArray(v.Select(x => (JsonNode)x).ToArray()) });
            return v;
        }

        private static async Task RequireClean(Ctx c, string action)
        {
            var s = await ReadStatus(c.Repo.Path, c.Ct);
            if (!s.Dirty) return;
            var files = s.Conflicts.Select(f => "конфликт: " + f).Concat(s.Changed.Select(f => f.Path)).ToList();
            throw new GitFailure($"{action}: дерево {RepoLabel(c.Repo)} грязное ({files.Count}) — закоммитьте или уберите изменения сами:\n" +
                                 Bullets(files.Take(15), "•") + (files.Count > 15 ? $"  … и ещё {files.Count - 15}\n" : ""));
        }

        // ------------------------------------------------------------------ checkout

        private static async Task<(int, string, string)> Checkout(Ctx c)
        {
            await Prepare(c, new[] { "--from" }, new[] { "--create", "--yes" }, 1, true);
            if (c.Args.Pos.Count == 0) throw new UsageException("нужна ветка");
            var branch = Rev(c.Args.Pos[0], "имя ветки");
            if (!(await Git(c, "check-ref-format", "--branch", branch)).Ok) throw new UsageException($"неверное имя ветки «{branch}»");
            var create = c.Args.Has("--create");
            var from = c.Args.Get("--from");
            if (from != null && !create) throw new UsageException("--from только вместе с --create");

            await RequireClean(c, "checkout");
            var current = await CurrentBranch(c);
            var plan = new StringBuilder();
            var data = new JsonObject { ["branch"] = branch, ["from"] = current };
            List<string> args;
            if (create)
            {
                if (await RefExists(c, "refs/heads/" + branch)) throw new GitFailure($"Ветка «{branch}» уже есть — уберите --create.");
                args = new List<string> { "switch", "-c", branch };
                if (from != null)
                {
                    Rev(from, "--from");
                    var sha = (await Git(c, "rev-parse", "--verify", "--quiet", from + "^{commit}")).Out.Trim();
                    if (sha.Length == 0) throw new GitFailure($"Нет ревизии «{from}».");
                    args.Add(from);
                    plan.Append($"  создать ветку {branch} от {from} ({Short(sha)}) и перейти на неё\n");
                }
                else plan.Append($"  создать ветку {branch} от текущего HEAD{(current != null ? $" ({current})" : "")} и перейти на неё\n");
            }
            else
            {
                if (current == branch) return (0, $"{RepoLabel(c.Repo)}: уже на ветке {branch}\n", Json.Write(data));
                if (await RefExists(c, "refs/heads/" + branch))
                {
                    args = new List<string> { "switch", branch };
                    plan.Append($"  перейти с {current ?? "отсоединённого HEAD"} на {branch}\n");
                }
                else
                {
                    var remotes = (await Must(c, "for-each-ref", "for-each-ref", "--format=%(refname:short)", "refs/remotes/*/" + branch))
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (remotes.Count == 0) throw new GitFailure($"Нет ветки «{branch}» ни локально, ни в remote (git.fetch?). Новая — с --create.");
                    if (remotes.Count > 1) throw new GitFailure($"Ветка «{branch}» есть в нескольких remote: {string.Join(", ", remotes)}.");
                    args = new List<string> { "switch", "-c", branch, "--track", remotes[0] };
                    plan.Append($"  создать локальную {branch} из {remotes[0]} (с отслеживанием) и перейти на неё\n");
                }
            }

            plan.Append($"  репозиторий: {RepoLabel(c.Repo)} ({c.Repo.Path})\n");
            if (!c.Args.Has("--yes")) return Preview(c, plan, data);

            using (await LockRepo(c.Repo, c.Phase, c.Ct))
            {
                await RequireClean(c, "checkout");
                c.Phase("git switch");
                var r = await GitWrite(c, WriteTimeout, null, args.ToArray());
                if (!r.Ok) throw new GitFailure("git switch: " + r.FirstError);
            }

            Log.Info($"git {c.Repo.Name}: checkout {branch}{(create ? " (новая)" : "")}");
            return (0, $"✓ {RepoLabel(c.Repo)}: на ветке {branch}\n", Json.Write(data));
        }

        // ------------------------------------------------------------------ pull

        private static async Task<(int, string, string)> Pull(Ctx c)
        {
            await Prepare(c, Array.Empty<string>(), new[] { "--yes" }, 0, true);
            var s = await ReadStatus(c.Repo.Path, c.Ct);
            if (s.Detached) throw new GitFailure("HEAD отсоединён — pull только на ветке.");
            if (s.Upstream == null) throw new GitFailure($"У ветки {s.Head} нет upstream — pull некуда (git.push --set-upstream или настройте сами).");
            if (s.Conflicts.Count > 0) throw new GitFailure("В дереве конфликты — разрешите их сами.");

            var plan = new StringBuilder($"  git pull --ff-only: {s.Head} ← {s.Upstream} в {RepoLabel(c.Repo)}\n");
            plan.Append($"  по локальной копии upstream (без fetch): ↑{s.Ahead} ↓{s.Behind}\n");
            if (s.Ahead > 0 && s.Behind > 0) plan.Append("  ! ветки разошлись: fast-forward невозможен — pull откажет (слияние — git.merge)\n");
            if (s.Changed.Count > 0) plan.Append($"  ! в дереве {s.Changed.Count} изменений: git откажет, если их заденет обновление\n");
            var data = new JsonObject { ["branch"] = s.Head, ["upstream"] = s.Upstream };
            if (!c.Args.Has("--yes")) return Preview(c, plan, data);

            string before, after;
            using (await LockRepo(c.Repo, c.Phase, c.Ct))
            {
                before = (await Git(c, "rev-parse", "HEAD")).Out.Trim();
                c.Phase("git pull --ff-only");
                var r = await GitWrite(c, GitRunner.NetworkTimeout, null, "pull", "--ff-only", "--no-rebase");
                if (!r.Ok) throw new GitFailure("git pull --ff-only: " + r.FirstError + (r.Err.Contains("Not possible to fast-forward", StringComparison.OrdinalIgnoreCase) || r.Err.Contains("diverg", StringComparison.OrdinalIgnoreCase) ? " (ветки разошлись — git.merge)" : ""));
                after = (await Git(c, "rev-parse", "HEAD")).Out.Trim();
            }

            data["before"] = before;
            data["after"] = after;
            if (before == after) return (0, $"✓ {RepoLabel(c.Repo)}: {s.Head} уже актуальна\n", Json.Write(data));
            var log = (await Git(c, "log", "--format=  %h %an  %s", "--max-count=20", before + ".." + after)).Out;
            var count = (await Git(c, "rev-list", "--count", before + ".." + after)).Out.Trim();
            Log.Info($"git {c.Repo.Name}: pull {s.Head} {Short(before)}..{Short(after)}");
            return (0, $"✓ {RepoLabel(c.Repo)}: {s.Head} {Short(before)} → {Short(after)}, коммитов: {count}\n{log}", Json.Write(data));
        }

        // ------------------------------------------------------------------ add

        private static async Task<(int, string, string)> Add(Ctx c)
        {
            await Prepare(c, Array.Empty<string>(), new[] { "--all", "--yes" }, int.MaxValue, true);
            var all = c.Args.Has("--all");
            if (all && c.Args.Pos.Count > 0) throw new UsageException("либо пути, либо --all");
            if (!all && c.Args.Pos.Count == 0) throw new UsageException("нужны пути или --all");
            var args = new List<string> { "add" };
            if (all) args.Add("--all");
            else
            {
                args.Add("--");
                args.AddRange(c.Args.Pos.Select(p => PathSpec(c.Repo, p)));
            }

            var dry = new List<string> { "add", "--dry-run" };
            dry.AddRange(args.Skip(1));
            var d = await Git(c, dry.ToArray());
            if (!d.Ok) throw new GitFailure("git add: " + d.FirstError);
            var lines = d.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            var data = new JsonObject { ["files"] = new JsonArray(lines.Take(1000).Select(l => (JsonNode)l).ToArray()) };
            if (lines.Count == 0) return (0, $"{RepoLabel(c.Repo)}: добавлять нечего\n", Json.Write(data));

            var plan = new StringBuilder($"  в индекс {RepoLabel(c.Repo)} ({lines.Count}):\n");
            foreach (var l in lines.Take(200)) plan.Append("    ").Append(l).Append('\n');
            if (lines.Count > 200) plan.Append($"    … и ещё {lines.Count - 200}\n");
            if (!c.Args.Has("--yes")) return Preview(c, plan, data);

            using (await LockRepo(c.Repo, c.Phase, c.Ct))
            {
                c.Phase("git add");
                var r = await GitWrite(c, WriteTimeout, null, args.ToArray());
                if (!r.Ok) throw new GitFailure("git add: " + r.FirstError);
            }

            return (0, $"✓ {RepoLabel(c.Repo)}: в индекс добавлено {lines.Count}\n", Json.Write(data));
        }

        // ------------------------------------------------------------------ commit

        private static async Task<(int, string, string)> Commit(Ctx c)
        {
            await Prepare(c, new[] { "--message" }, new[] { "--all", "--yes", "--allow-ai-marks" }, 0, true);
            var message = MessageArg(c);
            var allowAi = c.Args.Has("--allow-ai-marks");
            var all = c.Args.Has("--all");
            var (name, email) = await RequireIdentity(c);
            var skipped = CheckOrRefuse("Сообщение коммита", message, name, email, allowAi);

            var branch = await CurrentBranch(c);
            if (branch == null) throw new GitFailure("HEAD отсоединён — коммит только на ветке (git.checkout).");
            var s = await ReadStatus(c.Repo.Path, c.Ct);
            if (s.Conflicts.Count > 0) throw new GitFailure("В дереве конфликты — разрешите их сами.");
            var files = s.Changed.Where(f => f.XY[0] != '.').Select(f => $"{Kind(f.XY[0])}: {f.Path}").ToList();
            if (all) files.AddRange(s.Changed.Where(f => f.XY[0] == '.' && f.XY[1] != '.').Select(f => $"{Kind(f.XY[1])}: {f.Path}"));
            if (files.Count == 0)
                throw new GitFailure($"Нечего коммитить в {RepoLabel(c.Repo)}: индекс пуст" + (all ? " и отслеживаемые файлы не изменены" : " (git.add или --all)") +
                                     (s.Untracked.Count > 0 ? $"; новых файлов {s.Untracked.Count} — их добавляет только git.add" : "") + ".");

            var plan = new StringBuilder();
            plan.Append($"  коммит в {RepoLabel(c.Repo)}, ветка {branch}\n");
            plan.Append($"  автор и коммиттер: {name} <{email}> (git config репозитория)\n");
            plan.Append($"  файлы ({files.Count}){(all ? ", с --all" : "")}:\n");
            foreach (var f in files.Take(100)) plan.Append("    ").Append(f).Append('\n');
            if (files.Count > 100) plan.Append($"    … и ещё {files.Count - 100}\n");
            plan.Append("  сообщение:\n").Append(Quote(message));
            plan.Append("  проверка следа ИИ: ").Append(skipped.Count == 0 ? "чисто\n" : "нарушения пропущены (--allow-ai-marks):\n" + Bullets(skipped));
            foreach (var m in AiTraceCheck.Mentions(message)) plan.Append("  ! ").Append(m).Append('\n');
            plan.Append(AllowNote(allowAi, skipped));
            var data = new JsonObject { ["branch"] = branch, ["author"] = $"{name} <{email}>", ["message"] = message, ["files"] = files.Count, ["allowAiMarks"] = allowAi };
            if (!c.Args.Has("--yes")) return Preview(c, plan, data);

            string sha;
            using (await LockRepo(c.Repo, c.Phase, c.Ct))
            {
                c.Phase("git commit");
                var args = new List<string> { "commit", "--cleanup=whitespace", "-F", "-" };
                if (all) args.Add("--all");
                var r = await GitWrite(c, WriteTimeout, message + "\n", args.ToArray());
                if (!r.Ok) throw new GitFailure("git commit: " + r.FirstError + (r.Err.Length > 0 ? "\n" + Cut(r.Err.Trim(), 2000) : ""));
                sha = (await Git(c, "rev-parse", "HEAD")).Out.Trim();
            }

            data["hash"] = sha;
            Log.Info($"git {c.Repo.Name}: commit {Short(sha)} в {branch}");
            var text = new StringBuilder($"✓ {RepoLabel(c.Repo)}: коммит {Short(sha)} в {branch} — {Cut(message.Split('\n')[0], 100)}\n");
            text.Append(AllowNote(allowAi, skipped));

            // хуки (commit-msg, prepare-commit-msg) могли дописать сообщение: проверить, что получилось на деле
            var after = await VerifyCommit(c, sha, name, email);
            if (after.Count > 0 && !allowAi)
            {
                text.Append("✗ Итоговый коммит не проходит проверку (сообщение изменил хук?):\n").Append(Bullets(after));
                text.Append("  Коммит создан локально и не отправлен; git.push его не пропустит — исправьте сами (git commit --amend).\n");
                data["violations"] = new JsonArray(after.Select(x => (JsonNode)x).ToArray());
                return (1, text.ToString(), Json.Write(data));
            }

            return (0, text.ToString(), Json.Write(data));
        }

        /// <summary>Проверка готового коммита: сообщение, автор и коммиттер (маркеры и совпадение с личностью репозитория).</summary>
        private static async Task<List<string>> VerifyCommit(Ctx c, string sha, string name, string email)
        {
            var r = await Git(c, "log", "-1", "--format=%an%x1f%ae%x1f%cn%x1f%ce%x1f%B", sha);
            var f = r.Out.Split('\x1f');
            if (!r.Ok || f.Length < 5) return new List<string> { "не удалось прочитать коммит " + sha };
            return CheckCommit(f[0], f[1], f[2], f[3], f[4], name, email);
        }

        /// <summary>Нарушения одного коммита: сообщение, маркеры в личностях, расхождение с user.name/user.email.</summary>
        private static List<string> CheckCommit(string an, string ae, string cn, string ce, string message, string name, string email)
        {
            var v = AiTraceCheck.Message(message);
            var a = AiTraceCheck.Identity(an, ae);
            if (a != null) v.Add("автор: " + a);
            var cm = AiTraceCheck.Identity(cn, ce);
            if (cm != null) v.Add("коммиттер: " + cm);
            if (name.Length == 0 || email.Length == 0) v.Add("в репозитории нет user.name/user.email — автора не с чем сверить");
            else
            {
                if (an != name || !string.Equals(ae, email, StringComparison.OrdinalIgnoreCase)) v.Add($"автор {an} <{ae}> ≠ {name} <{email}>");
                if (cn != name || !string.Equals(ce, email, StringComparison.OrdinalIgnoreCase)) v.Add($"коммиттер {cn} <{ce}> ≠ {name} <{email}>");
            }

            return v;
        }

        // ------------------------------------------------------------------ merge

        private static async Task<(int, string, string)> Merge(Ctx c)
        {
            await Prepare(c, Array.Empty<string>(), new[] { "--ff-only", "--yes" }, 1, true);
            if (c.Args.Pos.Count == 0) throw new UsageException("нужна ветка: git.merge --repo R <ветка>");
            var source = Rev(c.Args.Pos[0], "ветка");
            var ffOnly = c.Args.Has("--ff-only");
            var sha = (await Git(c, "rev-parse", "--verify", "--quiet", source + "^{commit}")).Out.Trim();
            if (sha.Length == 0) throw new GitFailure($"Нет ветки или ревизии «{source}».");
            var branch = await CurrentBranch(c);
            if (branch == null) throw new GitFailure("HEAD отсоединён — слияние только в ветку.");
            await RequireClean(c, "merge");
            var (name, email) = await RequireIdentity(c);

            var count = int.TryParse((await Git(c, "rev-list", "--count", "HEAD.." + sha)).Out.Trim(), out var n) ? n : 0;
            var data = new JsonObject { ["into"] = branch, ["source"] = source, ["commits"] = count };
            if (count == 0) return (0, $"{RepoLabel(c.Repo)}: {source} уже слита в {branch}\n", Json.Write(data));
            var ff = (await Git(c, "merge-base", "--is-ancestor", "HEAD", sha)).Ok;
            data["fastForward"] = ff;
            if (ffOnly && !ff) throw new GitFailure($"Fast-forward {branch} ← {source} невозможен (в {branch} есть свои коммиты). Без --ff-only будет merge-коммит.");

            var plan = new StringBuilder($"  слить {source} ({count} коммитов) в {branch} в {RepoLabel(c.Repo)}: ");
            plan.Append(ff ? "fast-forward\n" : $"merge-коммит от {name} <{email}>\n");
            var log = (await Git(c, "log", "--format=%h%x1f%an%x1f%s%x1f%B%x1e", "--max-count=50", "HEAD.." + sha)).Out;
            var incoming = new List<string>();
            foreach (var rec in log.Split('\x1e'))
            {
                var f = rec.Trim('\n').Split('\x1f');
                if (f.Length < 4) continue;
                plan.Append($"    {f[0]} {f[1]}  {Cut(f[2], 90)}\n");
                if (AiTraceCheck.Message(f[3]).Count > 0) incoming.Add($"{f[0]}: {string.Join("; ", AiTraceCheck.Message(f[3]))}");
            }

            if (count > 50) plan.Append($"    … и ещё {count - 50}\n");
            if (incoming.Count > 0) plan.Append("  ! в сливаемых коммитах след ИИ — git.push их не пропустит:\n").Append(Bullets(incoming, "!"));
            if (!c.Args.Has("--yes")) return Preview(c, plan, data);

            string after;
            using (await LockRepo(c.Repo, c.Phase, c.Ct))
            {
                await RequireClean(c, "merge");
                c.Phase("git merge");
                var args = new List<string> { "merge", "--no-edit" };
                if (ffOnly) args.Add("--ff-only");
                args.Add(source);
                var r = await GitWrite(c, WriteTimeout, null, args.ToArray());
                if (!r.Ok)
                {
                    if (await RefExists(c, "MERGE_HEAD"))
                    {
                        var conflicts = (await Git(c, "diff", "--name-only", "--diff-filter=U")).Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        var abort = await GitWrite(c, WriteTimeout, null, "merge", "--abort");
                        throw new GitFailure($"Конфликт слияния {source} → {branch}" + (abort.Ok ? " — слияние отменено (merge --abort), дерево как было" : $" — merge --abort не прошёл: {abort.FirstError}") + ":\n" +
                                             Bullets(conflicts.Take(30), "•") + "Разрешите конфликт сами.",
                            new JsonObject { ["reason"] = "conflict", ["conflicts"] = new JsonArray(conflicts.Select(x => (JsonNode)x).ToArray()), ["aborted"] = abort.Ok });
                    }

                    throw new GitFailure("git merge: " + r.FirstError);
                }

                after = (await Git(c, "rev-parse", "HEAD")).Out.Trim();
            }

            data["hash"] = after;
            Log.Info($"git {c.Repo.Name}: merge {source} → {branch} ({Short(after)})");
            var text = $"✓ {RepoLabel(c.Repo)}: {source} слита в {branch} ({(ff ? "fast-forward" : "merge-коммит")}) → {Short(after)}\n";
            if (!ff)
            {
                var v = await VerifyCommit(c, after, name, email);
                if (v.Count > 0)
                {
                    data["violations"] = new JsonArray(v.Select(x => (JsonNode)x).ToArray());
                    return (1, text + "✗ Merge-коммит не проходит проверку следа ИИ (merge.log?):\n" + Bullets(v) + "  git.push его не пропустит — исправьте сами.\n", Json.Write(data));
                }
            }

            return (0, text, Json.Write(data));
        }

        // ------------------------------------------------------------------ tag

        private static async Task<(int, string, string)> Tag(Ctx c)
        {
            await Prepare(c, new[] { "--message" }, new[] { "--yes", "--allow-ai-marks" }, 1, true);
            if (c.Args.Pos.Count == 0) throw new UsageException("нужно имя тега vX.Y.Z");
            var tag = c.Args.Pos[0];
            if (!TagName.IsMatch(tag) || !(await Git(c, "check-ref-format", "refs/tags/" + tag)).Ok) throw new UsageException($"тег должен быть вида vX.Y.Z (или vX.Y.Z-pre.N): «{tag}»");
            var message = MessageArg(c);
            var allowAi = c.Args.Has("--allow-ai-marks");
            if (await RefExists(c, "refs/tags/" + tag)) throw new GitFailure($"Тег {tag} уже есть — перезапись тегов запрещена.");
            var (name, email) = await RequireIdentity(c);
            var skipped = CheckOrRefuse("Сообщение тега", message, name, email, allowAi);
            var head = (await Git(c, "rev-parse", "--verify", "--quiet", "HEAD")).Out.Trim();
            if (head.Length == 0) throw new GitFailure("В репозитории нет коммитов.");
            var subject = (await Git(c, "log", "-1", "--format=%s", head)).Out.Trim();
            var branch = await CurrentBranch(c);

            var plan = new StringBuilder($"  аннотированный тег {tag} на {Short(head)} ({branch ?? "отсоединённый HEAD"}: {Cut(subject, 80)}) в {RepoLabel(c.Repo)}\n");
            plan.Append($"  автор тега: {name} <{email}>\n  сообщение:\n").Append(Quote(message));
            plan.Append("  проверка следа ИИ: ").Append(skipped.Count == 0 ? "чисто\n" : "нарушения пропущены (--allow-ai-marks):\n" + Bullets(skipped));
            foreach (var m in AiTraceCheck.Mentions(message)) plan.Append("  ! ").Append(m).Append('\n');
            plan.Append(AllowNote(allowAi, skipped));
            var data = new JsonObject { ["tag"] = tag, ["target"] = head, ["message"] = message, ["allowAiMarks"] = allowAi };
            if (!c.Args.Has("--yes")) return Preview(c, plan, data);

            using (await LockRepo(c.Repo, c.Phase, c.Ct))
            {
                c.Phase("git tag");
                var r = await GitWrite(c, WriteTimeout, message + "\n", "tag", "-a", "--cleanup=whitespace", "-F", "-", tag, head);
                if (!r.Ok) throw new GitFailure("git tag: " + r.FirstError);
            }

            Log.Info($"git {c.Repo.Name}: tag {tag} на {Short(head)}");
            return (0, $"✓ {RepoLabel(c.Repo)}: тег {tag} на {Short(head)}\n{AllowNote(allowAi, skipped)}Отправка — git.push --repo {(c.Repo.IsProject ? "project" : c.Repo.Name)} --tag {tag}\n", Json.Write(data));
        }
    }
}
