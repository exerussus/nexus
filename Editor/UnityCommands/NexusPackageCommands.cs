using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using Newtonsoft.Json.Linq;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Команды Package Manager: пакеты проекта, Packages/manifest.json, packages-lock.json, git-ссылки.
    /// </summary>
    /// <remarks>
    /// <para>Чтение — сразу (manifest + lock + зарегистрированные пакеты). Изменения правят файлы бережно
    /// (<see cref="NexusPackageJsonFile"/>) и продолжаются заданием <see cref="NexusPackageJob"/>: Resolve, импорт,
    /// компиляция, перезагрузка домена; клиент ждёт его через <c>job.status</c>, как <c>refresh</c>.</para>
    /// <para>Рабочий цикл своих пакетов: проект смотрит на <c>#develop</c>; проверить фичу —
    /// <c>pkg.ref nexus feature/x</c>, вернуть — <c>pkg.ref nexus develop</c>; после push в пакет — <c>pkg.update nexus</c>.
    /// Локальный <c>file:</c> — только осознанно, с <c>--allow-file</c>.</para>
    /// </remarks>
    internal static class NexusPackageCommands
    {
        // ================================================================ чтение

        [NexusCommand("pkg.list", "Пакеты проекта: что запрошено в manifest, что установлено, источник, hash git-пакетов",
            Usage = "pkg.list [--all (с косвенными и встроенными модулями)] [--git] [--max 200]", ReadOnly = true)]
        private static int List(NexusCommandContext ctx)
        {
            var all = ctx.Flag("all");
            var gitOnly = ctx.Flag("git");
            var max = Math.Max(1, ctx.GetInt("max", 200));
            var rows = NexusPackageCatalog.Load();

            var data = new List<object>();
            int matched = 0, hiddenIndirect = 0, hiddenBuiltin = 0, unresolved = 0;
            foreach (var r in rows)
            {
                if (gitOnly && !r.IsGit) continue;
                if (!all && !r.Direct)
                {
                    hiddenIndirect++;
                    continue;
                }

                if (!all && r.Source == "builtin")
                {
                    hiddenBuiltin++;
                    continue;
                }

                matched++;
                if (r.Unresolved) unresolved++;
                if (matched > max) continue;
                ctx.Print(Line(r));
                data.Add(r.ToData());
            }

            if (matched == 0) ctx.Print(gitOnly ? "git-пакетов нет." : "Пакетов нет.");
            if (matched > max) ctx.Print($"… ещё {matched - max} (--max)");

            var footer = new StringBuilder(NexusCliText.Plural(matched, "пакет", "пакета", "пакетов"));
            if (unresolved > 0) footer.Append($", не разрешено: {unresolved} (logs --errors)");
            if (hiddenIndirect + hiddenBuiltin > 0)
                footer.Append($" · скрыто: косвенных {hiddenIndirect}, встроенных модулей {hiddenBuiltin} (--all)");
            footer.Append(" — подробно: pkg.info <имя>");
            ctx.Print(footer.ToString());

            ctx.Data = new { packages = data, total = matched, hiddenIndirect, hiddenBuiltin, unresolved };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("pkg.info", "Пакет подробно: запрос в manifest, запись lock, установленная версия, git, зависимости и кто зависит от него",
            Usage = "pkg.info <имя>", ReadOnly = true)]
        private static int Info(NexusCommandContext ctx)
        {
            var rows = NexusPackageCatalog.Load();
            var r = NexusPackageCatalog.Find(rows, ctx.Arg(0));

            ctx.Print($"{r.Name}" + (string.IsNullOrEmpty(r.DisplayName) ? string.Empty : $" — {r.DisplayName}") +
                      (r.Installed != null ? $" v{r.Installed}" : " (не зарегистрирован)") + $" [{r.Source}]");
            ctx.Print("manifest: " + (r.Requested ?? (r.Source == "embedded" ? "— (встроенный пакет)" : "— (косвенная зависимость)")));
            if (r.LockVersion != null)
                ctx.Print($"lock: {r.LockVersion} · depth {r.Depth} · {r.LockSource ?? "?"}" + (r.LockHash != null ? " · hash " + r.LockHash : string.Empty));
            else
                ctx.Print("lock: записи нет" + (r.IsGit ? " — разрешится при следующем Resolve (pkg.resolve)" : string.Empty));

            if (r.IsGit)
            {
                var url = NexusPackageGit.BaseUrl(r.Requested ?? r.LockVersion);
                ctx.Print($"git: {url} #{r.Ref ?? "HEAD"} · коммит {r.Hash ?? "?"}" +
                          (r.GitRevision != null && r.GitRevision != r.Ref ? $" · revision {r.GitRevision}" : string.Empty));
                if (r.LockHash != null && r.GitHash != null && !string.Equals(r.LockHash, r.GitHash, StringComparison.OrdinalIgnoreCase))
                    ctx.Warn($"lock ({NexusPackageGit.Short(r.LockHash)}) и загруженный пакет ({NexusPackageGit.Short(r.GitHash)}) расходятся — pkg.resolve");
                if (NexusPackageGit.IsHash(r.Ref)) ctx.Print("ссылка — коммит: закреплён, pkg.update его не сдвинет");
            }

            if (r.EmbeddedFolder != null) ctx.Print("папка: " + r.EmbeddedFolder);
            else if (!string.IsNullOrEmpty(r.ResolvedPath)) ctx.Print("путь: " + r.ResolvedPath);
            if (!string.IsNullOrWhiteSpace(r.Description)) ctx.Print("описание: " + NexusCliText.OneLine(r.Description, 200));
            if (r.Unresolved) ctx.Error("прописан в manifest, но не разрешён — logs --errors, pkg.resolve");

            var deps = r.Dependencies;
            ctx.Print(deps.Count == 0 ? "зависимости: нет" : $"зависимости ({deps.Count}):");
            foreach (var d in deps) ctx.Print($"  {d.Key} {d.Value}");

            var dependents = NexusPackageCatalog.Dependents(rows, r.Name);
            ctx.Print(dependents.Count == 0 ? "от него зависят: никто" : "от него зависят: " + string.Join(", ", dependents.Select(x => x.Name)));

            ctx.Data = new
            {
                package = r.ToData(),
                dependencies = deps.Select(d => new { name = d.Key, version = d.Value }).ToList(),
                dependents = dependents.Select(x => x.Name).ToList(),
                description = r.Description, path = r.ResolvedPath,
            };
            return r.Unresolved ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok;
        }

        // ================================================================ изменение

        [NexusCommand("pkg.add", "Добавить пакет (имя[@версия] или git-URL[#ref]) через Client.Add и дождаться Resolve и компиляции",
            Usage = "pkg.add <имя[@версия] | git-url[#ref]> [--yes (заменить уже прописанный)] [--allow-file] [--timeout 900]")]
        private static int Add(NexusCommandContext ctx)
        {
            var id = ctx.Arg(0)?.Trim() ?? throw new NexusCommandException("нужен пакет: pkg.add com.unity.x@1.2.3 | pkg.add https://github.com/o/r.git#develop");
            RefuseLocal(ctx, id);

            var busy = NexusPackageJob.Ready(ctx);
            if (busy != null) return ctx.RetryLater(busy, 120);

            var manifest = NexusPackageJsonFile.Load(NexusPackageJsonFile.ManifestPath, true);
            var deps = manifest.Section("dependencies", false);
            var notes = new List<string>();

            if (NexusPackageGit.LooksGit(id))
            {
                // имя git-пакета известно только из его package.json — поэтому Client.Add: он сам скачает, прочитает
                // имя и пропишет manifest; здесь только защита от второго входа того же репозитория
                var same = deps?.Properties().FirstOrDefault(p => p.Value.Type == JTokenType.String && NexusPackageGit.SameRepo((string)p.Value, id));
                if (same != null)
                {
                    var current = (string)same.Value;
                    if (current == id)
                    {
                        ctx.Print($"· уже прописан: {same.Name} = {current} — свежий коммит: pkg.update {same.Name}");
                        return NexusCommandContext.Exit.Ok;
                    }

                    if (!ctx.Flag("yes"))
                    {
                        ctx.Error($"репозиторий уже прописан: {same.Name} = {current} — сменить ссылку: pkg.ref {same.Name} <ref> (или --yes)");
                        return NexusCommandContext.Exit.Failed;
                    }

                    notes.Add($"{same.Name}: {current} → {id}");
                }
            }
            else if (!NexusPackageGit.IsLocal(id))
            {
                var at = id.IndexOf('@');
                var name = at > 0 ? id.Substring(0, at) : id;
                var version = at > 0 ? id.Substring(at + 1) : null;
                var existing = deps?[name]?.ToString();
                if (existing != null)
                {
                    if (version != null && existing == version)
                    {
                        ctx.Print($"· уже прописан: {name} {existing}");
                        return NexusCommandContext.Exit.Ok;
                    }

                    if (!ctx.Flag("yes"))
                    {
                        ctx.Error($"уже прописан: {name} = {existing} — заменить на {version ?? "последнюю версию"}: повторите с --yes");
                        return NexusCommandContext.Exit.Failed;
                    }

                    notes.Add($"{name}: {existing} → {version ?? "последняя"}");
                }
            }

            NexusPackageJob.Begin(ctx, "add", null, null, true, id, notes);
            ctx.Print($"→ Client.Add {id}: загрузка, Resolve, импорт и компиляция — ждите итога");
            ctx.Data = new { op = "add", id };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("pkg.remove", "Убрать пакет из Packages/manifest.json и разрешить пакеты (без --yes — только показать, кто от него зависит)",
            Usage = "pkg.remove <имя> [--yes] [--timeout 900]")]
        private static int Remove(NexusCommandContext ctx)
        {
            var rows = NexusPackageCatalog.Load();
            var r = NexusPackageCatalog.Find(rows, ctx.Arg(0));
            var dependents = NexusPackageCatalog.Dependents(rows, r.Name);

            if (r.Requested == null)
            {
                if (r.Source == "embedded")
                    throw new NexusCommandException($"{r.Name} — встроенный пакет ({r.EmbeddedFolder}): его нет в manifest, удаляется папка — вручную", NexusCommandContext.Exit.Failed);
                throw new NexusCommandException($"{r.Name} не прописан в manifest — это косвенная зависимость" +
                                                (dependents.Count > 0 ? ", его тянут: " + string.Join(", ", dependents.Select(x => x.Name)) : string.Empty),
                    NexusCommandContext.Exit.Failed);
            }

            ctx.Print($"{r.Name} = {r.Requested}" + (r.Installed != null ? $" (v{r.Installed})" : string.Empty));
            if (dependents.Count == 0) ctx.Print("от него никто не зависит");
            else
            {
                ctx.Warn("от него зависят: " + string.Join(", ", dependents.Select(x => x.Name)) +
                         " — пакет останется косвенной зависимостью, а версию выберут они");
            }

            if (!ctx.Flag("yes"))
            {
                ctx.Error("удаление из manifest — повторите с --yes");
                ctx.Data = new { name = r.Name, dependents = dependents.Select(x => x.Name).ToList() };
                return NexusCommandContext.Exit.Failed;
            }

            var busy = NexusPackageJob.Ready(ctx);
            if (busy != null) return ctx.RetryLater(busy, 120);

            var manifest = NexusPackageJsonFile.Load(NexusPackageJsonFile.ManifestPath, true);
            var deps = manifest.Section("dependencies", false);
            if (deps?.Property(r.Name) == null) throw new NexusCommandException($"{r.Name} уже нет в manifest.", NexusCommandContext.Exit.Failed);
            deps.Remove(r.Name);
            manifest.Save();

            NexusPackageJob.Begin(ctx, "remove", new[] { r.Name }, null, true);
            ctx.Print($"✓ {r.Name} убран из Packages/manifest.json → Resolve, импорт и компиляция — ждите итога");
            ctx.Data = new { op = "remove", name = r.Name, dependents = dependents.Select(x => x.Name).ToList() };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("pkg.ref", "git-пакет: сменить ссылку #… (develop, feature/x, vX.Y.Z, коммит) или URL, сбросить lock и разрешить; итог — новый hash",
            Usage = "pkg.ref <имя> <ref> | pkg.ref <имя> [ref] --url <git-url[#ref]> [--allow-file] [--timeout 900]")]
        private static int Ref(NexusCommandContext ctx)
        {
            var newRef = ctx.Arg(1)?.Trim().TrimStart('#');
            var newUrl = ctx.Get("url")?.Trim();
            if (string.IsNullOrEmpty(newRef) && string.IsNullOrEmpty(newUrl))
                throw new NexusCommandException("нужна ссылка: pkg.ref <имя> develop | feature/x | v1.2.3 | <коммит>, или --url <git-url>");
            if (newUrl != null)
            {
                RefuseLocal(ctx, newUrl);
                if (!NexusPackageGit.LooksGit(newUrl) && !(ctx.Flag("allow-file") && NexusPackageGit.IsLocal(newUrl)))
                    throw new NexusCommandException($"«{newUrl}» не похож на git-URL (https://…/repo.git, git@…, git+ssh://…)");
            }

            var rows = NexusPackageCatalog.Load();
            var r = NexusPackageCatalog.Find(rows, ctx.Arg(0), x => x.Requested != null);
            if (!(r.IsGit || NexusPackageGit.LooksGit(r.Requested)) || NexusPackageGit.IsLocal(r.Requested))
                throw new NexusCommandException($"{r.Name} — не git-пакет ({r.Requested}, {r.Source}): pkg.ref меняет только git-ссылки; версию реестра — pkg.add {r.Name}@<версия> --yes",
                    NexusCommandContext.Exit.Failed);

            var baseUrl = NexusPackageGit.BaseUrl(newUrl ?? r.Requested);
            var fragment = !string.IsNullOrEmpty(newRef) ? newRef : newUrl != null ? NexusPackageGit.Fragment(newUrl) ?? r.Ref : r.Ref;
            var value = string.IsNullOrEmpty(fragment) ? baseUrl : baseUrl + "#" + fragment;

            var busy = NexusPackageJob.Ready(ctx);
            if (busy != null) return ctx.RetryLater(busy, 120);

            var before = new Dictionary<string, string> { [r.Name] = r.Hash };
            var note = value == r.Requested
                ? $"{r.Name}: ссылка та же (#{fragment ?? "HEAD"}) — сброс lock, подтянется свежий коммит"
                : $"{r.Name}: {Describe(r.Requested)} → {Describe(value)}";

            var manifest = NexusPackageJsonFile.Load(NexusPackageJsonFile.ManifestPath, true);
            var deps = manifest.Section("dependencies", false);
            var prop = deps?.Property(r.Name) ?? throw new NexusCommandException($"{r.Name} пропал из manifest.", NexusCommandContext.Exit.Failed);
            if (value != r.Requested)
            {
                prop.Value = value;   // значение на месте: порядок ключей не меняется
                manifest.Save();
            }

            DropLockEntries(new[] { r.Name });
            if (NexusPackageGit.IsHash(fragment)) ctx.Print("· ссылка — коммит: пакет закреплён, pkg.update его не сдвинет");

            NexusPackageJob.Begin(ctx, "ref", new[] { r.Name }, before, true, null, new[] { note });
            ctx.Print("✓ " + note);
            ctx.Print("→ Resolve, импорт и компиляция — ждите итога (новый hash)");
            ctx.Data = new { op = "ref", name = r.Name, from = r.Requested, to = value, before = r.Hash };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("pkg.update", "git-пакеты: сбросить их записи в packages-lock.json и разрешить — подтянуть свежий коммит той же ссылки; итог — было/стало hash",
            Usage = "pkg.update <имя> [имя…] | pkg.update --all-git [--timeout 900]")]
        private static int Update(NexusCommandContext ctx)
        {
            var rows = NexusPackageCatalog.Load();
            var targets = new List<NexusPackageRow>();
            var skipped = new List<string>();

            if (ctx.Flag("all-git"))
            {
                foreach (var r in rows)
                {
                    if (r.Requested == null || !r.IsGit) continue;
                    if (NexusPackageGit.IsHash(r.Ref)) skipped.Add($"{r.Name} (закреплён на коммите)");
                    else targets.Add(r);
                }
            }
            else
            {
                if (ctx.Positional.Count == 0) throw new NexusCommandException("нужны имена git-пакетов или --all-git (pkg.list --git)");
                foreach (var q in ctx.Positional)
                {
                    var r = NexusPackageCatalog.Find(rows, q, x => x.Requested != null);
                    if (!r.IsGit)
                        throw new NexusCommandException($"{r.Name} — не git-пакет ({r.Requested}, {r.Source}): обновляется версией — pkg.add {r.Name}@<версия> --yes",
                            NexusCommandContext.Exit.Failed);
                    if (NexusPackageGit.IsHash(r.Ref)) ctx.Warn($"{r.Name} закреплён на коммите #{r.Ref} — останется тем же; сдвинуть: pkg.ref {r.Name} develop");
                    if (!targets.Contains(r)) targets.Add(r);
                }
            }

            foreach (var s in skipped) ctx.Print("· пропущен: " + s);
            if (targets.Count == 0)
            {
                ctx.Print("git-пакетов для обновления нет.");
                return NexusCommandContext.Exit.Ok;
            }

            var busy = NexusPackageJob.Ready(ctx);
            if (busy != null) return ctx.RetryLater(busy, 120);

            var before = targets.ToDictionary(r => r.Name, r => r.Hash);
            DropLockEntries(targets.Select(r => r.Name));
            foreach (var r in targets) ctx.Print($"· {r.Name} #{r.Ref ?? "HEAD"} @{NexusPackageGit.Short(r.Hash)}");

            NexusPackageJob.Begin(ctx, "update", targets.Select(r => r.Name), before, true);
            ctx.Print("→ записи lock сброшены: Resolve, импорт и компиляция — ждите итога (было/стало)");
            ctx.Data = new { op = "update", packages = before };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("pkg.resolve", "Client.Resolve: применить Packages/manifest.json и дождаться импорта и компиляции",
            Usage = "pkg.resolve [--timeout 900]")]
        private static int Resolve(NexusCommandContext ctx)
        {
            var busy = NexusPackageJob.Ready(ctx);
            if (busy != null) return ctx.RetryLater(busy, 120);
            NexusPackageJob.Begin(ctx, "resolve", null, null, false);
            ctx.Print("→ Client.Resolve — ждите итога");
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("pkg.registry", "Scoped-реестры в Packages/manifest.json: список, добавить или заменить, убрать",
            Usage = "pkg.registry | pkg.registry --add <имя> <url> <scope> [scope…] | pkg.registry --remove <имя> [--timeout 900]")]
        private static int Registry(NexusCommandContext ctx)
        {
            var addName = ctx.Get("add");
            var removeName = ctx.Get("remove");
            if (ctx.Has("add") && string.IsNullOrWhiteSpace(addName) || ctx.Has("remove") && string.IsNullOrWhiteSpace(removeName))
                throw new NexusCommandException("pkg.registry --add <имя> <url> <scope> [scope…] | --remove <имя>");

            var manifest = NexusPackageJsonFile.Load(NexusPackageJsonFile.ManifestPath, true);
            var registries = manifest.Root["scopedRegistries"] as JArray;

            if (addName == null && removeName == null)
            {
                var list = new List<object>();
                if (registries == null || registries.Count == 0) ctx.Print("Scoped-реестров нет.");
                else
                    foreach (var t in registries.OfType<JObject>())
                    {
                        var scopes = (t["scopes"] as JArray)?.Select(x => x.ToString()).ToList() ?? new List<string>();
                        ctx.Print($"{t["name"]}  {t["url"]}  [{string.Join(", ", scopes)}]");
                        list.Add(new { name = t["name"]?.ToString(), url = t["url"]?.ToString(), scopes });
                    }

                ctx.Data = new { registries = list };
                return NexusCommandContext.Exit.Ok;
            }

            var busy = NexusPackageJob.Ready(ctx);
            if (busy != null) return ctx.RetryLater(busy, 120);

            string note;
            if (addName != null)
            {
                var url = ctx.Arg(0) ?? throw new NexusCommandException("нужен url: pkg.registry --add <имя> <url> <scope> [scope…]");
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new NexusCommandException($"url реестра — http(s): «{url}»");
                var scopes = ctx.Positional.Skip(1).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
                if (scopes.Count == 0) throw new NexusCommandException("нужен хотя бы один scope: pkg.registry --add <имя> <url> com.company");

                if (registries == null)
                {
                    registries = new JArray();
                    manifest.Root["scopedRegistries"] = registries;
                }

                var existing = registries.OfType<JObject>().FirstOrDefault(o => string.Equals(o["name"]?.ToString(), addName, StringComparison.Ordinal));
                if (existing != null)
                {
                    // замена на месте: прочие поля записи (если есть) остаются
                    existing["url"] = url;
                    existing["scopes"] = new JArray(scopes);
                    note = $"реестр «{addName}» обновлён: {url} [{string.Join(", ", scopes)}]";
                }
                else
                {
                    registries.Add(new JObject { ["name"] = addName, ["url"] = url, ["scopes"] = new JArray(scopes) });
                    note = $"реестр «{addName}» добавлен: {url} [{string.Join(", ", scopes)}]";
                }
            }
            else
            {
                var existing = registries?.OfType<JObject>().FirstOrDefault(o => string.Equals(o["name"]?.ToString(), removeName, StringComparison.Ordinal))
                               ?? throw new NexusCommandException($"Нет реестра «{removeName}» (pkg.registry).", NexusCommandContext.Exit.Failed);
                existing.Remove();
                note = $"реестр «{removeName}» убран";
            }

            manifest.Save();
            NexusPackageJob.Begin(ctx, "registry", null, null, false, null, new[] { "✓ " + note });
            ctx.Print("✓ " + note + " → Resolve — ждите итога");
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ общее

        /// <summary>Проекты тянут пакеты с remote: локальный путь — только с явным --allow-file.</summary>
        private static void RefuseLocal(NexusCommandContext ctx, string value)
        {
            if (!NexusPackageGit.IsLocal(value) || ctx.Flag("allow-file")) return;
            throw new NexusCommandException(
                $"«{value}» — локальный путь: проект должен тянуть пакет с remote (git-URL#develop / #feature/x). " +
                "Осознанно подключить локальную папку — --allow-file", NexusCommandContext.Exit.Failed);
        }

        /// <summary>Сбросить записи пакетов в packages-lock.json: следующий Resolve заново разрешит их ссылки.</summary>
        private static void DropLockEntries(IEnumerable<string> names)
        {
            var lockFile = NexusPackageJsonFile.Load(NexusPackageJsonFile.LockPath, false);
            var deps = lockFile?.Section("dependencies", false);
            if (deps == null) return;
            var changed = false;
            foreach (var n in names)
                changed |= deps.Remove(n);
            if (changed) lockFile.Save();
        }

        private static string Describe(string value) =>
            NexusPackageGit.LooksGit(value) ? "#" + (NexusPackageGit.Fragment(value) ?? "HEAD") + " (" + NexusPackageGit.BaseUrl(value) + ")" : value;

        private static string Line(NexusPackageRow r)
        {
            var sb = new StringBuilder(r.Unresolved ? "✗ " : "· ");
            sb.Append(r.Name);
            if (r.Installed != null) sb.Append(' ').Append(r.Installed);
            sb.Append(" [").Append(r.Source).Append(']');

            if (r.Unresolved) sb.Append(" не разрешён: ").Append(r.Requested);
            else if (r.IsGit)
            {
                sb.Append(" #").Append(r.Ref ?? "HEAD").Append(" @").Append(NexusPackageGit.Short(r.Hash));
                if (r.LockHash == null) sb.Append(" (нет в lock)");
            }
            else if (r.Source == "embedded") sb.Append(' ').Append(r.EmbeddedFolder);
            else if (r.Source == "local" || r.Source == "tarball") sb.Append(' ').Append(r.Requested ?? r.LockVersion);
            else if (r.Requested != null && r.Installed != null && r.Requested != r.Installed) sb.Append(" (запрошено ").Append(r.Requested).Append(')');

            if (!r.Direct) sb.Append(" · косвенный");
            return sb.ToString();
        }
    }
}
