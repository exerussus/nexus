using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>Репозиторий из белого списка.</summary>
    internal sealed class GitRepo
    {
        /// <summary>Имя для --repo: имя папки (у проекта — ещё «.» и «project»).</summary>
        public string Name;
        /// <summary>Нормализованный полный путь (корень рабочего дерева).</summary>
        public string Path;
        /// <summary>Откуда взят: project | Nexus.json | личный.</summary>
        public string Source;
        public bool IsProject;
        /// <summary>Почему недоступен (не корень git-репозитория, нет папки, glob не там); null — доступен.</summary>
        public string Problem;
        /// <summary>Имя совпало с другим репозиторием: обращаться по пути.</summary>
        public bool Ambiguous;
    }

    /// <summary>
    /// Белый список репозиториев для git-команд демона: корень проекта Unity (зарегистрированного в демоне),
    /// <c>gitRepos</c> из <c>ProjectSettings/Nexus.json</c> (пути относительно проекта) и <c>repos</c> из личного
    /// файла демона (<see cref="DaemonPaths.GitReposFile"/>, пути абсолютные или от «~»). Glob (* ?) — только в последнем
    /// сегменте. Каждый путь — GetFullPath и проверка, что <c>git rev-parse --show-toplevel</c> совпадает с ним;
    /// всё, что не прошло, недоступно. Список собирается заново на каждый вызов (файлы могли измениться).
    /// </summary>
    internal static class GitRepos
    {
        private static readonly StringComparison PathCmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static string ProjectConfig(Project p) => System.IO.Path.Combine(p.Path, "ProjectSettings", "Nexus.json");

        /// <summary>Записи белого списка: (путь как написан, откуда, база для относительных).</summary>
        private static List<(string Entry, string Source, string Base)> Entries(Project p, List<string> notes)
        {
            var list = new List<(string, string, string)>();
            var cfg = Json.ReadFile(ProjectConfig(p));
            if (cfg?["gitRepos"] is JsonArray arr)
                foreach (var e in arr)
                    if (e != null && e.GetValueKind() == System.Text.Json.JsonValueKind.String)
                        list.Add((e.GetValue<string>(), "Nexus.json", p.Path));
                    else notes.Add("Nexus.json: gitRepos — нужны строки-пути");

            var personal = DaemonPaths.GitReposFile;
            if (File.Exists(personal))
            {
                var o = Json.ReadFile(personal);
                if (o == null) notes.Add($"личный список {personal}: не читается (нужен JSON {{\"repos\": [\"…\"]}})");
                else if (o["repos"] is JsonArray repos)
                    foreach (var e in repos)
                        if (e != null && e.GetValueKind() == System.Text.Json.JsonValueKind.String)
                            list.Add((e.GetValue<string>(), "личный", null));
            }

            return list;
        }

        /// <summary>Развернуть запись в кандидатов (полные пути). Ошибка записи — в problems.</summary>
        private static List<(string Path, bool Explicit)> Expand(string entry, string baseDir, List<string> problems)
        {
            var result = new List<(string, bool)>();
            if (string.IsNullOrWhiteSpace(entry))
            {
                problems.Add("пустой путь");
                return result;
            }

            var raw = entry.Trim().Replace('\\', '/');
            if (raw == "~" || raw.StartsWith("~/", StringComparison.Ordinal))
                raw = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/') + raw.Substring(1);

            if (baseDir == null && !System.IO.Path.IsPathFullyQualified(raw))
            {
                problems.Add($"«{entry}»: в личном списке нужен абсолютный путь (или от ~)");
                return result;
            }

            if (baseDir != null && (System.IO.Path.IsPathRooted(raw) || raw.StartsWith("~", StringComparison.Ordinal)))
            {
                problems.Add($"«{entry}»: в Nexus.json нужен путь относительно проекта");
                return result;
            }

            var segments = raw.TrimEnd('/').Split('/');
            var last = segments[^1];
            for (var i = 0; i < segments.Length - 1; i++)
                if (segments[i].IndexOfAny(new[] { '*', '?' }) >= 0)
                {
                    problems.Add($"«{entry}»: glob разрешён только в последнем сегменте");
                    return result;
                }

            string Full(string path) => Project.Normalize(baseDir == null ? path : System.IO.Path.Combine(baseDir, path));

            if (last.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                result.Add((Full(raw), true));
                return result;
            }

            if (last == "." || last == "..")
            {
                problems.Add($"«{entry}»: неверный шаблон");
                return result;
            }

            var head = string.Join('/', segments.Take(segments.Length - 1));
            var parent = Full(head.Length > 0 ? head : baseDir == null ? "/" : ".");
            if (!Directory.Exists(parent))
            {
                problems.Add($"«{entry}»: нет папки {parent}");
                return result;
            }

            var options = new EnumerationOptions
            {
                MatchType = MatchType.Simple,
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            };
            foreach (var dir in Directory.EnumerateDirectories(parent, last, options).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                // ссылки и точки повторной обработки не разворачиваются: путь должен быть настоящей папкой
                var info = new DirectoryInfo(dir);
                if (info.LinkTarget != null || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                result.Add((Project.Normalize(dir), false));
            }

            return result;
        }

        /// <summary>Совпадает ли корень git-репозитория с папкой (иначе это подпапка чужого репозитория или не репозиторий).</summary>
        public static async Task<string> CheckRoot(string path, CancellationToken ct)
        {
            if (!Directory.Exists(path)) return "нет папки";
            if (!Directory.Exists(System.IO.Path.Combine(path, ".git")) && !File.Exists(System.IO.Path.Combine(path, ".git")))
                return "не корень git-репозитория (нет .git)";
            var r = await GitRunner.Run(path, new[] { "rev-parse", "--show-toplevel" }, 15, ct, readOnly: true);
            if (!r.Ok) return "git: " + r.FirstError;
            string top;
            try
            {
                top = Project.Normalize(r.Out.Trim());
            }
            catch
            {
                return "git вернул странный корень: " + r.Out.Trim();
            }

            return string.Equals(top, path, PathCmp) ? null : $"корень репозитория — {top}, а не эта папка";
        }

        /// <summary>Весь белый список проекта с проверкой каждого пути. notes — замечания к записям (для git.repos).</summary>
        public static async Task<List<GitRepo>> List(Project p, CancellationToken ct, List<string> notes = null)
        {
            notes ??= new List<string>();
            var repos = new List<GitRepo>();
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            var projectPath = Project.Normalize(p.Path);
            seen.Add(projectPath);
            repos.Add(new GitRepo { Name = System.IO.Path.GetFileName(projectPath), Path = projectPath, Source = "project", IsProject = true });

            foreach (var (entry, source, baseDir) in Entries(p, notes))
            {
                var problems = new List<string>();
                List<(string Path, bool Explicit)> found;
                try
                {
                    found = Expand(entry, baseDir, problems);
                }
                catch (Exception e)
                {
                    notes.Add($"{source}: «{entry}» — {e.Message}");
                    continue;
                }

                foreach (var pr in problems) notes.Add($"{source}: {pr}");
                foreach (var (path, isExplicit) in found)
                {
                    if (!seen.Add(path)) continue;
                    // из glob берутся только папки с .git: остальные — просто соседние папки, не ошибка
                    if (!isExplicit && !Directory.Exists(System.IO.Path.Combine(path, ".git")) && !File.Exists(System.IO.Path.Combine(path, ".git"))) continue;
                    repos.Add(new GitRepo { Name = System.IO.Path.GetFileName(path), Path = path, Source = source });
                }
            }

            await Task.WhenAll(repos.Select(async r => r.Problem = await CheckRoot(r.Path, ct)));

            foreach (var g in repos.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var r in g)
                    r.Ambiguous = true;
            return repos;
        }

        /// <summary>
        /// Найти репозиторий по --repo: null/«.»/«project» — проект; иначе имя папки из списка или полный путь,
        /// совпадающий с путём из списка. Только доступные (прошедшие проверку корня).
        /// </summary>
        public static async Task<(GitRepo Repo, string Error)> Resolve(Project p, string name, CancellationToken ct)
        {
            var all = await List(p, ct);
            GitRepo repo;
            if (string.IsNullOrEmpty(name) || name == "." || name == "project") repo = all[0];
            else
            {
                var byName = all.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byName.Count > 1)
                    return (null, $"Имя «{name}» неоднозначно: {string.Join(", ", byName.Select(r => r.Path))} — укажите --repo полным путём.");
                repo = byName.FirstOrDefault();
                if (repo == null)
                {
                    string full = null;
                    try
                    {
                        if (System.IO.Path.IsPathFullyQualified(name)) full = Project.Normalize(name);
                    }
                    catch
                    {
                        // не путь
                    }

                    repo = full == null ? null : all.FirstOrDefault(r => string.Equals(r.Path, full, PathCmp));
                }
            }

            if (repo == null)
                return (null, $"Репозитория «{name}» нет в белом списке. Доступны: {string.Join(", ", all.Where(r => r.Problem == null).Select(r => r.IsProject ? "project" : r.Name))}. " +
                              $"Список: gitRepos в ProjectSettings/Nexus.json или repos в {DaemonPaths.GitReposFile} (git.repos).");
            if (repo.Problem != null) return (null, $"Репозиторий «{repo.Name}» ({repo.Path}) недоступен: {repo.Problem}.");
            return (repo, null);
        }
    }
}
