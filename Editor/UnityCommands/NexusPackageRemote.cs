using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Запросы наружу, которые ничего не меняют в проекте: <c>pkg.outdated</c> (<c>git ls-remote</c> по каждому
    /// git-пакету) и <c>pkg.search</c> (Client.Search в реестрах). Оба долгие — идут заданием <c>pkg.remote</c>:
    /// работа в фоне (процессы git — в пуле потоков, запрос Package Manager — асинхронный), тик задания только
    /// смотрит, готово ли, и собирает итог. Главный поток не ждёт ни процесса, ни запроса.
    /// </summary>
    /// <remarks>
    /// Фоновая работа — поле домена <see cref="Works"/> (владелец — этот класс, ключ — id задания); после перезагрузки
    /// домена её нет, и тик запускает её заново по параметрам задания. Задание кончилось — запись удаляется.
    /// </remarks>
    [InitializeOnLoad]
    internal static class NexusPackageRemote
    {
        internal const string Kind = "pkg.remote";

        private sealed class Work
        {
            public List<Task<NexusPackageProbe>> Probes;
            public Request<UpmPackageInfo[]> Search;
        }

        /// <summary>Результат проверки одного git-пакета.</summary>
        internal sealed class NexusPackageProbe
        {
            public string Name;
            public string Url;
            public string Ref;
            public string Hash;
            public string Remote;
            public string RemoteRef;
            public string State;   // same, outdated, pinned, error
            public string Error;
            public bool NoGit;
        }

        private static readonly Dictionary<string, Work> Works = new Dictionary<string, Work>(StringComparer.Ordinal);

        static NexusPackageRemote()
        {
            NexusCliJobs.Register(Kind, Step);
            NexusCliJobs.RegisterAbort(Kind, (job, exit, reason) => Works.Remove(job.Id));
        }

        // ================================================================ команды

        [NexusCommand("pkg.outdated", "git-пакеты: сравнить hash из packages-lock.json с удалённой веткой или тегом (git ls-remote)",
            Usage = "pkg.outdated [имя…] [--per 10 (секунд на пакет)] [--timeout 60]", ReadOnly = true)]
        private static int Outdated(NexusCommandContext ctx)
        {
            var rows = NexusPackageCatalog.Load();
            var targets = new List<NexusPackageRow>();
            if (ctx.Positional.Count == 0) targets.AddRange(rows.Where(r => r.Requested != null && r.IsGit && !NexusPackageGit.IsLocal(r.Requested)));
            else
                foreach (var q in ctx.Positional)
                {
                    var r = NexusPackageCatalog.Find(rows, q, x => x.Requested != null && x.IsGit);
                    if (!targets.Contains(r)) targets.Add(r);
                }

            if (targets.Count == 0)
            {
                ctx.Print("git-пакетов в manifest нет.");
                ctx.Data = new { packages = Array.Empty<object>() };
                return NexusCommandContext.Exit.Ok;
            }

            var per = Math.Max(2, Math.Min(120, ctx.GetInt("per", 10)));
            var items = targets.Select(r => new NexusPackageProbe
            {
                Name = r.Name, Url = NexusPackageGit.RemoteUrl(r.Requested), Ref = r.Ref, Hash = r.Hash,
                State = NexusPackageGit.IsHash(r.Ref) ? "pinned" : null,
            }).ToList();

            // процессы идут параллельно: срок — один пакет и запас, а не сумма
            var job = NexusCliJobs.Start(Kind, ctx.GetInt("timeout", per + 30), ctx);
            job.Set("op", "outdated");
            job.Set("per", per.ToString(CultureInfo.InvariantCulture));
            job.Set("items", JsonConvert.SerializeObject(items));
            NexusCliJobs.SetPhase(job, $"git ls-remote: 0/{items.Count}");
            Launch(job);
            NexusCliJobs.Save();
            ctx.Print($"→ git ls-remote для {NexusCliText.Plural(items.Count, "пакета", "пакетов", "пакетов")} (до {per} с на пакет)");
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("pkg.search", "Найти пакет в реестрах (Unity и scoped): по подстроке имени или точно (--exact)",
            Usage = "pkg.search <имя> [--exact] [--max 30] [--timeout 120]", ReadOnly = true)]
        private static int Search(NexusCommandContext ctx)
        {
            var q = ctx.Arg(0)?.Trim();
            if (string.IsNullOrEmpty(q)) throw new NexusCommandException("нужно имя или его часть: pkg.search timeline");

            var job = NexusCliJobs.Start(Kind, ctx.GetInt("timeout", 120), ctx);
            job.Set("op", "search");
            job.Set("q", q);
            if (ctx.Flag("exact")) job.Set("exact", "1");
            job.Set("max", Math.Max(1, ctx.GetInt("max", 30)).ToString(CultureInfo.InvariantCulture));
            NexusCliJobs.SetPhase(job, "запрос к реестрам");
            // сам запрос — в тике: Client.* не вызываем посреди обработки команды
            NexusCliJobs.Save();
            ctx.Print($"→ поиск «{q}» в реестрах");
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ тик

        private static void Step(NexusCliJob job)
        {
            try
            {
                if (!Works.TryGetValue(job.Id, out var w)) w = Launch(job);
                if (w == null) return;

                if (job.Get("op") == "search") StepSearch(job, w);
                else StepOutdated(job, w);
            }
            finally
            {
                if (job.Done) Works.Remove(job.Id);
                // хвосты заданий, снятых без тика (отмена, выключение CLI)
                if (Works.Count > 0)
                    foreach (var id in Works.Keys.ToList())
                        if (!NexusCliJobs.IsRunning(id))
                            Works.Remove(id);
            }
        }

        private static Work Launch(NexusCliJob job)
        {
            var w = new Work();
            if (job.Get("op") == "search")
            {
                var q = job.Get("q");
                w.Search = job.Get("exact") == "1" ? (Request<UpmPackageInfo[]>)Client.Search(q) : Client.SearchAll();
            }
            else
            {
                List<NexusPackageProbe> items;
                try
                {
                    items = JsonConvert.DeserializeObject<List<NexusPackageProbe>>(job.Get("items", "[]")) ?? new List<NexusPackageProbe>();
                }
                catch
                {
                    items = new List<NexusPackageProbe>();
                }

                int.TryParse(job.Get("per", "10"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var per);
                per = Math.Max(2, per);
                w.Probes = items.Select(p => p.State != null ? Task.FromResult(p) : Task.Run(() => Probe(p, per))).ToList();
            }

            Works[job.Id] = w;
            return w;
        }

        private static void StepOutdated(NexusCliJob job, Work w)
        {
            var done = w.Probes.Count(t => t.IsCompleted);
            if (done < w.Probes.Count)
            {
                NexusCliJobs.SetPhase(job, $"git ls-remote: {done}/{w.Probes.Count}");
                return;
            }

            var results = w.Probes.Select(t => t.Status == TaskStatus.RanToCompletion ? t.Result : new NexusPackageProbe { State = "error", Error = t.Exception?.GetBaseException().Message }).ToList();
            var sb = new StringBuilder();
            var outdated = new List<string>();
            var errors = 0;

            if (results.Count > 0 && results.All(r => r.NoGit))
            {
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed,
                    "✗ git не найден в PATH — установите Git (git-scm.com) или добавьте его в PATH и перезапустите Unity\n",
                    new { packages = results, gitMissing = true });
                return;
            }

            foreach (var r in results)
            {
                var label = $"{r.Name} #{r.Ref ?? "HEAD"}";
                switch (r.State)
                {
                    case "pinned":
                        sb.Append($"· {label} — закреплён на коммите\n");
                        break;
                    case "same":
                        sb.Append($"= {label} @{NexusPackageGit.Short(r.Hash)} — актуален\n");
                        break;
                    case "outdated":
                        outdated.Add(r.Name);
                        sb.Append($"↑ {label}: {NexusPackageGit.Short(r.Hash)} → {NexusPackageGit.Short(r.Remote)} — есть новее\n");
                        break;
                    default:
                        errors++;
                        sb.Append($"✗ {label}: {r.Error ?? "ошибка"}\n");
                        break;
                }
            }

            if (outdated.Count > 0) sb.Append($"устарели: {outdated.Count} — pkg.update {string.Join(" ", outdated)}\n");
            else if (errors == 0) sb.Append("✓ всё актуально\n");
            if (errors > 0) sb.Append($"! не проверено: {errors}\n");

            NexusCliJobs.Finish(job, errors > 0 ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok, sb.ToString(),
                new { packages = results, outdated, errors });
        }

        private static void StepSearch(NexusCliJob job, Work w)
        {
            if (!w.Search.IsCompleted) return;

            if (w.Search.Status == StatusCode.Failure)
            {
                NexusCliJobs.Finish(job, NexusCommandContext.Exit.Failed, $"✗ поиск не удался: {w.Search.Error?.message ?? "нет ответа реестра"}\n");
                return;
            }

            var q = job.Get("q") ?? string.Empty;
            int.TryParse(job.Get("max", "30"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max);
            max = Math.Max(1, max);

            var installed = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var p in UpmPackageInfo.GetAllRegisteredPackages())
                    if (p != null && p.name != null)
                        installed[p.name] = p.version;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("pkg.search: установленные пакеты", e);
            }

            var found = (w.Search.Result ?? Array.Empty<UpmPackageInfo>())
                .Where(p => p != null && (job.Get("exact") == "1"
                                          || (p.name ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                          || (p.displayName ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(p => p.name, StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            var data = new List<object>();
            foreach (var p in found.Take(max))
            {
                installed.TryGetValue(p.name, out var have);
                sb.Append($"{p.name} {p.version} — {p.displayName}");
                if (have != null) sb.Append(have == p.version ? " (установлен)" : $" (в проекте {have})");
                sb.Append('\n');
                data.Add(new { name = p.name, version = p.version, displayName = p.displayName, installed = have });
            }

            if (found.Count == 0) sb.Append($"Ничего не найдено по «{q}».\n");
            else if (found.Count > max) sb.Append($"… ещё {found.Count - max} (--max)\n");
            if (found.Count > 0) sb.Append($"добавить: pkg.add <имя>[@версия]\n");

            NexusCliJobs.Finish(job, NexusCommandContext.Exit.Ok, sb.ToString(), new { query = q, total = found.Count, packages = data });
        }

        // ================================================================ git ls-remote (пул потоков)

        /// <summary>
        /// Удалённый коммит ссылки. Без интерактива (GIT_TERMINAL_PROMPT=0, GCM_INTERACTIVE=never): нужен пароль —
        /// ошибка, а не окно; висит дольше срока — процесс снимается.
        /// </summary>
        private static NexusPackageProbe Probe(NexusPackageProbe item, int perSeconds)
        {
            var r = new NexusPackageProbe { Name = item.Name, Url = item.Url, Ref = item.Ref, Hash = item.Hash };
            var reference = string.IsNullOrEmpty(item.Ref) ? "HEAD" : item.Ref;
            var args = reference == "HEAD"
                ? $"ls-remote {Quote(item.Url)} HEAD"
                : $"ls-remote {Quote(item.Url)} {Quote(reference)} {Quote(reference + "^{}")}";

            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) throw new InvalidOperationException("git не запустился");
                    proc.StandardInput.Close();
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(perSeconds * 1000))
                    {
                        try
                        {
                            proc.Kill();
                        }
                        catch
                        {
                            // уже вышел
                        }

                        r.State = "error";
                        r.Error = $"нет ответа за {perSeconds} с (сеть, доступ или запрос пароля)";
                        return r;
                    }

                    proc.WaitForExit();
                    var output = stdout.Wait(2000) ? stdout.Result : string.Empty;
                    var error = stderr.Wait(2000) ? stderr.Result : string.Empty;
                    if (proc.ExitCode != 0)
                    {
                        r.State = "error";
                        r.Error = $"git ls-remote: код {proc.ExitCode}" + (string.IsNullOrWhiteSpace(error) ? string.Empty : " — " + FirstLine(error));
                        return r;
                    }

                    Match(output, reference, r);
                }
            }
            catch (Win32Exception)
            {
                r.State = "error";
                r.NoGit = true;
                r.Error = "git не найден в PATH";
                return r;
            }
            catch (Exception e)
            {
                r.State = "error";
                r.Error = e.Message;
                return r;
            }

            if (r.Remote == null)
            {
                r.State = "error";
                r.Error = $"ссылки «{reference}» нет на remote (ветку удалили или тег не запушен?)";
            }
            else
            {
                r.State = string.Equals(r.Remote, r.Hash, StringComparison.OrdinalIgnoreCase) ? "same" : "outdated";
            }

            return r;
        }

        /// <summary>Выбрать коммит: ветка, затем раскрытый тег (<c>^{}</c> — коммит аннотированного тега), затем тег.</summary>
        private static void Match(string output, string reference, NexusPackageProbe r)
        {
            var refs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                refs[line.Substring(tab + 1).Trim()] = line.Substring(0, tab).Trim();
            }

            string[] order = reference == "HEAD"
                ? new[] { "HEAD" }
                : reference.StartsWith("refs/", StringComparison.Ordinal)
                    ? new[] { reference + "^{}", reference }
                    : new[] { "refs/heads/" + reference, "refs/tags/" + reference + "^{}", "refs/tags/" + reference };

            foreach (var key in order)
                if (refs.TryGetValue(key, out var hash))
                {
                    r.Remote = hash;
                    r.RemoteRef = key;
                    return;
                }
        }

        private static string Quote(string s) => "\"" + (s ?? string.Empty).Replace("\"", "\\\"") + "\"";

        private static string FirstLine(string s)
        {
            foreach (var line in s.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    return line.Trim();
            return string.Empty;
        }
    }
}
