using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Команды ассетов «на чтение и поля»: сведения, зависимости, обратные ссылки, сериализованные поля, метки.
    /// Ассет адресуется spec (<see cref="AssetQuery"/>).
    /// </summary>
    internal static class AssetCommands
    {
        // ================================================================ сведения

        [NexusCommand("asset.info", "Сведения об ассете: путь, guid, тип, подассеты, импортёр, метки, размер, dirty",
            Usage = "asset.info <spec> [--max 50]", ReadOnly = true)]
        private static int Info(NexusCommandContext ctx)
        {
            var spec = ctx.Arg(0) ?? throw new NexusCommandException("нужен ассет: путь, guid или имя");
            var obj = AssetQuery.Resolve(spec);
            var path = AssetDatabase.GetAssetPath(obj);
            var guid = AssetDatabase.AssetPathToGUID(path);
            var folder = AssetDatabase.IsValidFolder(path);
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            var isSub = main != null && main != obj;

            ctx.Print(path + (isSub ? " : " + obj.name : string.Empty));
            ctx.Print($"guid: {guid}");
            ctx.Print($"тип: {(folder ? "папка" : obj.GetType().FullName)}" + (isSub ? $" (главный — {main.GetType().FullName})" : string.Empty));

            var importer = folder ? null : AssetImporter.GetAtPath(path);
            var importerType = importer != null ? importer.GetType().FullName : null;
            if (importerType != null) ctx.Print("импортёр: " + importerType);

            var labels = main != null ? AssetDatabase.GetLabels(main) : Array.Empty<string>();
            ctx.Print("метки: " + (labels.Length == 0 ? "нет" : string.Join(", ", labels)));

            long? size = null;
            if (!folder)
            {
                try
                {
                    var file = new FileInfo(FileUtil.GetPhysicalPath(path));
                    if (file.Exists) size = file.Length;
                }
                catch (Exception e)
                {
                    NexusDiagnostics.Swallowed("asset.info: размер", e);
                }
            }

            if (size.HasValue) ctx.Print("размер: " + FormatSize(size.Value));

            var dirty = EditorUtility.IsDirty(obj);
            if (dirty) ctx.Print("! изменён в памяти, не сохранён (save --assets)");

            var max = Math.Max(1, ctx.GetInt("max", 50));
            var subs = new List<object>();
            if (!folder)
            {
                var reps = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
                if (reps.Length > 0) ctx.Print($"подассеты ({reps.Length}):");
                for (var i = 0; i < reps.Length; i++)
                {
                    var r = reps[i];
                    if (r == null) continue;
                    if (i < max) ctx.Print($"  {r.name} — {r.GetType().Name}");
                    subs.Add(new { name = r.name, type = r.GetType().FullName });
                }

                if (reps.Length > max) ctx.Print($"  … ещё {reps.Length - max} (--max)");
            }

            ctx.Data = new
            {
                path, guid, spec = AssetQuery.PathOf(obj), folder,
                type = obj.GetType().FullName, mainType = main != null ? main.GetType().FullName : null, subAsset = isSub,
                importer = importerType, labels, size, dirty, subAssets = subs,
            };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ зависимости и ссылки

        [NexusCommand("asset.deps", "Зависимости ассета (прямые; --recursive — все)",
            Usage = "asset.deps <spec> [--recursive] [--max 200]", ReadOnly = true)]
        private static int Deps(NexusCommandContext ctx)
        {
            var path = AssetQuery.ResolveMainPath(ctx.Arg(0) ?? throw new NexusCommandException("нужен ассет: путь, guid или имя"));
            var recursive = ctx.Flag("recursive");
            var deps = new List<string>();
            foreach (var d in AssetDatabase.GetDependencies(path, recursive))
                if (d != path) deps.Add(d);
            deps.Sort(StringComparer.OrdinalIgnoreCase);

            var max = Math.Max(1, ctx.GetInt("max", 200));
            ctx.Print($"{path}: {NexusCliText.Plural(deps.Count, "зависимость", "зависимости", "зависимостей")}{(recursive ? " (все)" : " (прямые; --recursive — все)")}");
            for (var i = 0; i < deps.Count && i < max; i++) ctx.Print("  " + deps[i]);
            if (deps.Count > max) ctx.Print($"  … ещё {deps.Count - max} (--max)");
            ctx.Data = new { path, recursive, total = deps.Count, deps = deps.Count > max ? deps.GetRange(0, max) : deps };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("asset.refs", "Кто ссылается на ассет (прямые ссылки; для папки — на её содержимое)",
            Usage = "asset.refs <spec> [--in Assets/Folder] [--all] [--max 100]", ReadOnly = true)]
        private static int Refs(NexusCommandContext ctx)
        {
            var path = AssetQuery.ResolveMainPath(ctx.Arg(0) ?? throw new NexusCommandException("нужен ассет: путь, guid или имя"));
            var scope = Scope(ctx);
            var watch = Stopwatch.StartNew();
            var targets = AssetReferences.Expand(new[] { path });
            var refs = AssetReferences.Find(targets, scope, ctx.Flag("all"), out var scanned);

            var max = Math.Max(1, ctx.GetInt("max", 100));
            ctx.Print($"{path}: {NexusCliText.Plural(refs.Count, "ссылающийся ассет", "ссылающихся ассета", "ссылающихся ассетов")} в {scope} " +
                      $"(просмотрено {scanned} за {NexusCliText.Seconds(watch.Elapsed.TotalSeconds)})");
            var list = new List<object>();
            var shown = 0;
            foreach (var pair in refs)
            {
                if (shown++ < max) ctx.Print("  " + pair.Key + (targets.Count > 1 ? " → " + string.Join(", ", pair.Value) : string.Empty));
                if (list.Count < max) list.Add(new { path = pair.Key, targets = pair.Value });
            }

            if (refs.Count > max) ctx.Print($"  … ещё {refs.Count - max} (--max)");
            if (ctx.Arg(0).IndexOf(':') > 0 && !ctx.Arg(0).StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
                ctx.Warn("ссылки считаются по файлу целиком: на какой именно подассет — не различается");
            ctx.Data = new { path, scope, scanned, total = refs.Count, refs = list };
            return NexusCommandContext.Exit.Ok;
        }

        internal static string Scope(NexusCommandContext ctx)
        {
            var scope = (ctx.Get("in") ?? "Assets").Replace('\\', '/').TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(scope)) throw new NexusCommandException($"--in: нет папки «{scope}».");
            return scope;
        }

        // ================================================================ поля

        [NexusCommand("asset.get", "Сериализованные поля ассета (главного объекта или подассета); --importer — настроек импорта",
            Usage = "asset.get <spec> [путь.поля] [--importer] [--depth 2] [--max-array 20]", ReadOnly = true)]
        private static int Get(NexusCommandContext ctx)
        {
            var target = Target(ctx, out _, out _);
            var so = new SerializedObject(target);
            var field = ctx.Arg(1) ?? string.Empty;
            var depth = Math.Max(0, ctx.GetInt("depth", 2));
            var maxArray = Math.Max(1, ctx.GetInt("max-array", 20));
            SerializedAccess.Print(ctx, so, field, depth, maxArray);
            ctx.Data = SerializedAccess.Dump(so, field, depth, maxArray);
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("asset.set", "Записать сериализованные поля ассета (одна запись Undo); --importer — настройки импорта с переимпортом",
            Usage = "asset.set <spec> путь=значение [путь=значение …] [--importer] [--save]")]
        private static int Set(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            var target = Target(ctx, out var path, out var importer);
            var assignments = new List<string>();
            for (var i = 1; i < ctx.Positional.Count; i++) assignments.Add(ctx.Positional[i]);
            if (assignments.Count == 0) throw new NexusCommandException("нужны присваивания: путь=значение (asset.get — какие поля есть)");

            var so = new SerializedObject(target);
            SerializedAccess.SetMany(so, assignments);
            so.ApplyModifiedProperties();

            if (importer != null)
            {
                // настройки импорта живут в .meta: без SaveAndReimport они не запишутся и не применятся
                importer.SaveAndReimport();
                ctx.Print($"✓ {path}: импорт переделан ({assignments.Count} {(assignments.Count == 1 ? "поле" : "полей")})");
            }
            else if (ctx.Flag("save"))
            {
                AssetDatabase.SaveAssetIfDirty(target);
                ctx.Print($"✓ {AssetQuery.PathOf(target)}: записано и сохранено");
            }
            else
            {
                ctx.Print($"✓ {AssetQuery.PathOf(target)}: записано (не сохранено на диск — --save или save --assets)");
            }

            ctx.Data = new { path, spec = AssetQuery.PathOf(target), importer = importer != null, assignments, dirty = EditorUtility.IsDirty(target) };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Объект для полей: сам ассет (подассет) или его импортёр (--importer).</summary>
        private static Object Target(NexusCommandContext ctx, out string path, out AssetImporter importer)
        {
            var obj = AssetQuery.Resolve(ctx.Arg(0) ?? throw new NexusCommandException("нужен ассет: путь, guid или имя"));
            path = AssetDatabase.GetAssetPath(obj);
            importer = null;
            if (!ctx.Flag("importer")) return obj;

            importer = AssetImporter.GetAtPath(path);
            if (importer == null) throw new NexusCommandException($"У «{path}» нет импортёра.", NexusCommandContext.Exit.Failed);
            return importer;
        }

        // ================================================================ метки

        [NexusCommand("asset.label", "Метки ассета: показать, добавить, убрать (через запятую)",
            Usage = "asset.label <spec> [--add A,B] [--remove C] [--list]")]
        private static int Label(NexusCommandContext ctx)
        {
            var path = AssetQuery.ResolveMainPath(ctx.Arg(0) ?? throw new NexusCommandException("нужен ассет: путь, guid или имя"));
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            var labels = new List<string>(AssetDatabase.GetLabels(main));
            var add = ctx.GetList("add");
            var remove = ctx.GetList("remove");

            if (add.Count > 0 || remove.Count > 0)
            {
                NexusEditorCommands.Guard(ctx);
                var changed = false;
                foreach (var l in remove)
                    changed |= labels.RemoveAll(x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase)) > 0;
                foreach (var l in add)
                {
                    if (l.IndexOfAny(new[] { ' ', ',', ';' }) >= 0) throw new NexusCommandException($"Метка «{l}»: без пробелов.");
                    if (labels.Exists(x => string.Equals(x, l, StringComparison.OrdinalIgnoreCase))) continue;
                    labels.Add(l);
                    changed = true;
                }

                if (changed) AssetDatabase.SetLabels(main, labels.ToArray());
                ctx.Print(changed ? "✓ метки изменены" : "метки уже такие");
            }

            ctx.Print($"{path}: {(labels.Count == 0 ? "меток нет" : string.Join(", ", labels))}");
            ctx.Data = new { path, labels };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ общее

        internal static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " КБ";
            return (bytes / (1024.0 * 1024.0)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " МБ";
        }
    }
}
