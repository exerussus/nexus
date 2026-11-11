using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>Пакет проекта глазами трёх источников: что запрошено в manifest, что записано в lock, что реально зарегистрировано.</summary>
    internal sealed class NexusPackageRow
    {
        public string Name;
        public string DisplayName;

        /// <summary>Значение из Packages/manifest.json (версия, URL#ref, file:…) или null — не прописан.</summary>
        public string Requested;

        /// <summary>Установленная версия (PackageInfo), null — не зарегистрирован.</summary>
        public string Installed;

        /// <summary>registry, git, embedded, local, tarball, builtin, unknown.</summary>
        public string Source;

        /// <summary>Прямая зависимость проекта (manifest, встроенный пакет, depth 0).</summary>
        public bool Direct;

        public string LockVersion;
        public string LockSource;
        public int Depth = -1;
        public string LockHash;
        public List<KeyValuePair<string, string>> LockDependencies;

        public string GitHash;
        public string GitRevision;
        public string ResolvedPath;
        public string Description;
        public List<KeyValuePair<string, string>> InfoDependencies;

        public bool Registered => Installed != null;
        public bool IsGit => Source == "git" || LockSource == "git" || (Source == null && NexusPackageGit.LooksGit(Requested));

        /// <summary>Прописан в manifest, но Unity его не разрешила.</summary>
        public bool Unresolved => Requested != null && !Registered && LockVersion == null;

        /// <summary>Коммит git-пакета: из lock (то, что закреплено), иначе из PackageInfo.</summary>
        public string Hash => LockHash ?? GitHash;

        /// <summary>Ссылка git (<c>#develop</c>, <c>#v1.2.0</c>) — из manifest, иначе из lock.</summary>
        public string Ref => NexusPackageGit.Fragment(Requested ?? LockVersion);

        /// <summary>Папка встроенного пакета: <c>Packages/&lt;папка&gt;</c>.</summary>
        public string EmbeddedFolder
        {
            get
            {
                if (Source != "embedded") return null;
                if (!string.IsNullOrEmpty(ResolvedPath)) return "Packages/" + Path.GetFileName(ResolvedPath.TrimEnd('/', '\\'));
                if (LockVersion != null && LockVersion.StartsWith("file:", StringComparison.Ordinal)) return "Packages/" + LockVersion.Substring(5);
                return "Packages/" + Name;
            }
        }

        /// <summary>Зависимости пакета: из lock, иначе из PackageInfo.</summary>
        public List<KeyValuePair<string, string>> Dependencies => LockDependencies ?? InfoDependencies ?? new List<KeyValuePair<string, string>>();

        public object ToData() => new
        {
            name = Name, displayName = DisplayName, requested = Requested, installed = Installed, source = Source,
            direct = Direct, depth = Depth, git = IsGit ? new { url = NexusPackageGit.BaseUrl(Requested ?? LockVersion), @ref = Ref, hash = Hash, lockHash = LockHash, infoHash = GitHash, revision = GitRevision } : null,
            embedded = EmbeddedFolder, unresolved = Unresolved,
        };
    }

    /// <summary>Сборка списка пакетов проекта и поиск по имени. Без состояния: всё читается на вызов.</summary>
    internal static class NexusPackageCatalog
    {
        public static List<NexusPackageRow> Load()
        {
            var manifest = NexusPackageJsonFile.Load(NexusPackageJsonFile.ManifestPath, true);
            var lockFile = NexusPackageJsonFile.Load(NexusPackageJsonFile.LockPath, false);
            var rows = new Dictionary<string, NexusPackageRow>(StringComparer.Ordinal);

            NexusPackageRow Row(string name)
            {
                if (!rows.TryGetValue(name, out var r)) rows[name] = r = new NexusPackageRow { Name = name };
                return r;
            }

            var deps = manifest.Section("dependencies", false);
            if (deps != null)
                foreach (var p in deps.Properties())
                    Row(p.Name).Requested = p.Value.Type == JTokenType.String ? (string)p.Value : p.Value.ToString(Newtonsoft.Json.Formatting.None);

            var lockDeps = lockFile?.Section("dependencies", false);
            if (lockDeps != null)
                foreach (var p in lockDeps.Properties())
                {
                    if (!(p.Value is JObject e)) continue;
                    var r = Row(p.Name);
                    r.LockVersion = Str(e["version"]);
                    r.LockSource = Str(e["source"]);
                    r.LockHash = Str(e["hash"]);
                    r.Depth = e["depth"] != null && e["depth"].Type == JTokenType.Integer ? (int)e["depth"] : -1;
                    if (e["dependencies"] is JObject d)
                        r.LockDependencies = d.Properties().Select(x => new KeyValuePair<string, string>(x.Name, Str(x.Value))).ToList();
                }

            foreach (var info in Registered())
            {
                if (info == null || string.IsNullOrEmpty(info.name)) continue;
                var r = Row(info.name);
                r.Installed = info.version;
                r.DisplayName = info.displayName;
                r.Source = SourceName(info.source);
                r.ResolvedPath = info.resolvedPath;
                r.Description = info.description;
                r.GitHash = info.git?.hash;
                r.GitRevision = info.git?.revision;
                if (info.isDirectDependency) r.Direct = true;
                if (info.dependencies != null)
                    r.InfoDependencies = info.dependencies.Select(x => new KeyValuePair<string, string>(x.name, x.version)).ToList();
            }

            foreach (var r in rows.Values)
            {
                if (r.Source == null) r.Source = r.LockSource ?? GuessSource(r.Requested);
                if (r.Requested != null || r.Depth == 0 || r.Source == "embedded") r.Direct = true;
            }

            var list = rows.Values.ToList();
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        /// <summary>
        /// Пакет по имени: точное имя, затем хвост после точки (<c>nexus</c> → <c>com.exerussus.nexus</c>) или
        /// отображаемое имя, затем подстрока. Неоднозначно — ошибка со списком кандидатов.
        /// </summary>
        public static NexusPackageRow Find(List<NexusPackageRow> rows, string query, Func<NexusPackageRow, bool> scope = null)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new NexusCommandException("нужно имя пакета (pkg.list)");
            query = query.Trim();
            var pool = scope == null ? rows : rows.Where(scope).ToList();

            foreach (var r in pool)
                if (string.Equals(r.Name, query, StringComparison.OrdinalIgnoreCase))
                    return r;

            var tail = pool.Where(r => r.Name.EndsWith("." + query, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(r.DisplayName, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tail.Count == 1) return tail[0];
            if (tail.Count > 1) throw Ambiguous(query, tail);

            var part = pool.Where(r => r.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (part.Count == 1) return part[0];
            if (part.Count > 1) throw Ambiguous(query, part);

            throw new NexusCommandException($"Нет пакета «{query}»" + (scope != null ? " среди подходящих для команды" : string.Empty) + " (pkg.list --all).",
                NexusCommandContext.Exit.Failed);
        }

        /// <summary>Кто зависит от пакета: по dependencies записей lock, иначе по PackageInfo.</summary>
        public static List<NexusPackageRow> Dependents(List<NexusPackageRow> rows, string name)
        {
            var list = new List<NexusPackageRow>();
            foreach (var r in rows)
            {
                if (r.Name == name) continue;
                foreach (var d in r.Dependencies)
                    if (d.Key == name)
                    {
                        list.Add(r);
                        break;
                    }
            }

            return list;
        }

        public static string SourceName(PackageSource s)
        {
            switch (s)
            {
                case PackageSource.Registry: return "registry";
                case PackageSource.Git: return "git";
                case PackageSource.Embedded: return "embedded";
                case PackageSource.Local: return "local";
                case PackageSource.LocalTarball: return "tarball";
                case PackageSource.BuiltIn: return "builtin";
                default: return "unknown";
            }
        }

        private static string GuessSource(string requested)
        {
            if (requested == null) return "unknown";
            if (NexusPackageGit.IsLocal(requested)) return requested.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? "tarball" : "local";
            if (NexusPackageGit.LooksGit(requested)) return "git";
            return "registry";
        }

        private static IEnumerable<UpmPackageInfo> Registered()
        {
            try
            {
                return UpmPackageInfo.GetAllRegisteredPackages() ?? Array.Empty<UpmPackageInfo>();
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("pkg: зарегистрированные пакеты", e);
                return Array.Empty<UpmPackageInfo>();
            }
        }

        private static string Str(JToken t) => t == null || t.Type == JTokenType.Null ? null : t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);

        private static NexusCommandException Ambiguous(string query, List<NexusPackageRow> found) =>
            new NexusCommandException($"«{query}» неоднозначно: {string.Join(", ", found.Take(8).Select(r => r.Name))}" + (found.Count > 8 ? $" … ещё {found.Count - 8}" : string.Empty) + " — укажите полное имя");
    }

    /// <summary>Разбор значений manifest для git-пакетов: <c>https://…/x.git?path=/sub#develop</c>.</summary>
    internal static class NexusPackageGit
    {
        /// <summary>Значение похоже на git-ссылку UPM.</summary>
        public static bool LooksGit(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || IsLocal(value)) return false;
            if (value.StartsWith("git+", StringComparison.OrdinalIgnoreCase) || value.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("git:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return true;
            var url = RemoteUrl(value);
            return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                   || (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && value.Contains("#"));
        }

        /// <summary>Локальный путь: <c>file:</c>, абсолютный путь или относительный с точки.</summary>
        public static bool IsLocal(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.StartsWith(".", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("\\", StringComparison.Ordinal)) return true;
            return value.Length > 2 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');
        }

        /// <summary>Значение без фрагмента <c>#…</c> (с <c>?path=</c>, если был).</summary>
        public static string BaseUrl(string value)
        {
            if (value == null) return null;
            var i = value.IndexOf('#');
            return i >= 0 ? value.Substring(0, i) : value;
        }

        /// <summary>Фрагмент после <c>#</c> или null.</summary>
        public static string Fragment(string value)
        {
            if (value == null) return null;
            var i = value.IndexOf('#');
            return i >= 0 && i + 1 < value.Length ? value.Substring(i + 1) : null;
        }

        /// <summary>URL для git: без фрагмента, без <c>?path=</c> и без префикса <c>git+</c>.</summary>
        public static string RemoteUrl(string value)
        {
            var url = BaseUrl(value) ?? string.Empty;
            var q = url.IndexOf('?');
            if (q >= 0) url = url.Substring(0, q);
            if (url.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) url = url.Substring(4);
            return url;
        }

        /// <summary>Ссылка — это коммит (7–40 hex): закреплён, «обновлять» нечего.</summary>
        public static bool IsHash(string reference)
        {
            if (string.IsNullOrEmpty(reference) || reference.Length < 7 || reference.Length > 40) return false;
            foreach (var c in reference)
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'))
                    return false;
            return true;
        }

        public static string Short(string hash) => string.IsNullOrEmpty(hash) ? "—" : hash.Length > 10 ? hash.Substring(0, 10) : hash;

        /// <summary>Тот же репозиторий (без учёта ссылки, регистра и хвоста .git).</summary>
        public static bool SameRepo(string a, string b)
        {
            string Norm(string v)
            {
                var u = RemoteUrl(v).Trim().TrimEnd('/');
                if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u.Substring(0, u.Length - 4);
                return u.ToLowerInvariant();
            }

            return a != null && b != null && Norm(a) == Norm(b) && PathQuery(a) == PathQuery(b);
        }

        private static string PathQuery(string v)
        {
            var b = BaseUrl(v) ?? string.Empty;
            var q = b.IndexOf('?');
            return q >= 0 ? b.Substring(q) : string.Empty;
        }
    }
}
