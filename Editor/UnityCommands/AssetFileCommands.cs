using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Команды ассетов «над файлами»: создание (папка, материал, ScriptableObject, текст), перенос, переименование,
    /// копия, удаление. Без диалогов; удаление — только с --yes.
    /// </summary>
    internal static class AssetFileCommands
    {
        /// <summary>Расширения, импорт которых меняет код: импортируются заданием (может перезагрузить домен).</summary>
        private static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".asmdef", ".asmref", ".rsp", ".dll",
        };

        // ================================================================ создание

        [NexusCommand("asset.create", "Создать ассет: папку, материал, ScriptableObject или текстовый файл (.txt/.json/.cs/…)",
            Usage = "asset.create <Assets/путь> --type folder|material|so|text [--so-type Namespace.Type] [--shader Имя] [--content \"…\"] [--overwrite]")]
        private static int Create(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            var path = NormalizeNew(ctx.Arg(0) ?? throw new NexusCommandException("нужен путь Assets/…"));
            var kind = (ctx.Get("type") ?? GuessKind(path)).ToLowerInvariant();
            var overwrite = ctx.Flag("overwrite");

            switch (kind)
            {
                case "folder":
                    return CreateFolderCommand(ctx, path);
                case "material":
                case "mat":
                    return CreateMaterial(ctx, path, overwrite);
                case "so":
                case "scriptableobject":
                    return CreateScriptable(ctx, path, overwrite);
                case "text":
                case "file":
                    return CreateText(ctx, path, overwrite);
                default:
                    throw new NexusCommandException("--type folder|material|so|text");
            }
        }

        private static string GuessKind(string path)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return "folder";
            if (string.Equals(ext, ".mat", StringComparison.OrdinalIgnoreCase)) return "material";
            if (string.Equals(ext, ".asset", StringComparison.OrdinalIgnoreCase)) return "so";
            return "text";
        }

        private static int CreateFolderCommand(NexusCommandContext ctx, string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                ctx.Print("папка уже есть: " + path);
                ctx.Data = new { path, created = false };
                return NexusCommandContext.Exit.Ok;
            }

            if (AssetQuery.Exists(path)) throw new NexusCommandException($"«{path}» уже есть и это не папка.", NexusCommandContext.Exit.Failed);
            var created = EnsureFolder(path);
            ctx.Print("✓ " + path + (created.Count > 1 ? $" (создано папок: {created.Count})" : string.Empty));
            ctx.Data = new { path, created = true, folders = created };
            return NexusCommandContext.Exit.Ok;
        }

        private static int CreateMaterial(NexusCommandContext ctx, string path, bool overwrite)
        {
            RequireExtension(path, ".mat");
            var shaderName = ctx.Get("shader");
            Shader shader;
            if (!string.IsNullOrEmpty(shaderName))
            {
                shader = Shader.Find(shaderName);
                if (shader == null) throw new NexusCommandException($"Шейдер «{shaderName}» не найден (имя как в меню шейдеров материала, например Universal Render Pipeline/Lit).", NexusCommandContext.Exit.Failed);
            }
            else
            {
                // шейдер по умолчанию текущего конвейера; без SRP — встроенный Standard
                var pipeline = GraphicsSettings.currentRenderPipeline;
                shader = pipeline != null ? pipeline.defaultShader : null;
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) throw new NexusCommandException("Шейдер по умолчанию не найден — укажите --shader.", NexusCommandContext.Exit.Failed);
            }

            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            var replaced = Put(ctx, path, material, overwrite);
            ctx.Print($"✓ {path} (шейдер {shader.name}){(replaced ? " — перезаписан" : string.Empty)}");
            ctx.Data = new { path, guid = AssetDatabase.AssetPathToGUID(path), shader = shader.name, replaced };
            return NexusCommandContext.Exit.Ok;
        }

        private static int CreateScriptable(NexusCommandContext ctx, string path, bool overwrite)
        {
            RequireExtension(path, ".asset");
            var typeName = ctx.Get("so-type") ?? throw new NexusCommandException("нужен --so-type: имя или полное имя класса ScriptableObject");
            var type = FindScriptableType(typeName);
            var instance = ScriptableObject.CreateInstance(type);
            if (instance == null) throw new NexusCommandException($"Экземпляр {type.FullName} не создан.", NexusCommandContext.Exit.Failed);
            instance.name = Path.GetFileNameWithoutExtension(path);
            var replaced = Put(ctx, path, instance, overwrite);
            ctx.Print($"✓ {path} ({type.FullName}){(replaced ? " — перезаписан" : string.Empty)}");
            ctx.Data = new { path, guid = AssetDatabase.AssetPathToGUID(path), type = type.FullName, replaced };
            return NexusCommandContext.Exit.Ok;
        }

        private static int CreateText(NexusCommandContext ctx, string path, bool overwrite)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) throw new NexusCommandException("Текстовому файлу нужно расширение: .txt, .json, .cs, …");
            var exists = AssetQuery.Exists(path);
            if (AssetDatabase.IsValidFolder(path)) throw new NexusCommandException($"«{path}» — папка.", NexusCommandContext.Exit.Failed);
            if (exists && !overwrite) throw new NexusCommandException($"«{path}» уже есть — --overwrite (guid сохранится).", NexusCommandContext.Exit.Failed);

            EnsureFolder(Parent(path));
            var full = Path.GetFullPath(Path.Combine(NexusEditorCommands.ProjectRoot, path));
            File.WriteAllText(full, ctx.Get("content") ?? string.Empty);

            if (CodeExtensions.Contains(ext))
            {
                // импорт кода перекомпилирует и перезагрузит домен — только в задании, после ответа (как asset.reimport)
                var job = NexusCliJobs.Start("refresh", ctx.GetInt("timeout", 600), ctx);
                job.Set("todo", "reimport");
                job.Set("path", path);
                job.Set("gen", NexusCompileLog.Generation.ToString(CultureInfo.InvariantCulture));
                job.Set("maxErrors", "20");
                job.Set("report", $"✓ {path}{(exists ? " — перезаписан" : string.Empty)}");
                return NexusCommandContext.Exit.Ok;
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ctx.Print($"✓ {path}{(exists ? " — перезаписан" : string.Empty)}");
            ctx.Data = new { path, guid = AssetDatabase.AssetPathToGUID(path), replaced = exists };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>
        /// Записать новый объект ассетом. Есть файл и --overwrite: того же типа — содержимое копируется в старый
        /// объект (guid и ссылки целы), другого — файл пересоздаётся (guid новый). Возвращает «перезаписан».
        /// </summary>
        private static bool Put(NexusCommandContext ctx, string path, Object fresh, bool overwrite)
        {
            if (AssetDatabase.IsValidFolder(path)) throw new NexusCommandException($"«{path}» — папка.", NexusCommandContext.Exit.Failed);
            if (!AssetQuery.Exists(path))
            {
                EnsureFolder(Parent(path));
                AssetDatabase.CreateAsset(fresh, path);
                return false;
            }

            if (!overwrite)
            {
                Object.DestroyImmediate(fresh);
                throw new NexusCommandException($"«{path}» уже есть — --overwrite.", NexusCommandContext.Exit.Failed);
            }

            var old = AssetDatabase.LoadMainAssetAtPath(path);
            if (old != null && old.GetType() == fresh.GetType())
            {
                Undo.RecordObject(old, "Nexus asset.create --overwrite");
                EditorUtility.CopySerialized(fresh, old);
                old.name = Path.GetFileNameWithoutExtension(path);
                Object.DestroyImmediate(fresh);
                EditorUtility.SetDirty(old);
                AssetDatabase.SaveAssetIfDirty(old);
                return true;
            }

            ctx.Warn($"тип другой ({(old != null ? old.GetType().Name : "?")}) — файл пересоздан, guid новый: ссылки на старый ассет потеряны");
            if (!AssetDatabase.DeleteAsset(path)) throw new NexusCommandException($"Не удалось удалить старый «{path}».", NexusCommandContext.Exit.Failed);
            AssetDatabase.CreateAsset(fresh, path);
            return true;
        }

        /// <summary>Тип ScriptableObject по полному или короткому имени; неоднозначно — ошибка со списком.</summary>
        private static Type FindScriptableType(string name)
        {
            var exact = new List<Type>();
            var shortCase = new List<Type>();
            var shortIgnore = new List<Type>();
            foreach (var t in TypeCache.GetTypesDerivedFrom<ScriptableObject>())
            {
                if (t.IsAbstract || t.IsGenericTypeDefinition) continue;
                // окна и инспекторы — тоже ScriptableObject, но ассетами не бывают
                if (typeof(EditorWindow).IsAssignableFrom(t) || typeof(Editor).IsAssignableFrom(t)) continue;
                if (string.Equals(t.FullName, name, StringComparison.Ordinal)) exact.Add(t);
                else if (string.Equals(t.Name, name, StringComparison.Ordinal)) shortCase.Add(t);
                else if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)) shortIgnore.Add(t);
            }

            foreach (var list in new[] { exact, shortCase, shortIgnore })
            {
                if (list.Count == 1) return list[0];
                if (list.Count > 1)
                {
                    var names = new List<string>();
                    foreach (var t in list) names.Add(t.FullName + " (" + t.Assembly.GetName().Name + ")");
                    names.Sort(StringComparer.Ordinal);
                    if (names.Count > 10) names.RemoveRange(10, names.Count - 10);
                    throw new NexusCommandException($"--so-type «{name}» неоднозначен: {string.Join(", ", names)} — укажите полное имя.", NexusCommandContext.Exit.Failed);
                }
            }

            throw new NexusCommandException($"Класс ScriptableObject «{name}» не найден (не абстрактный, скомпилирован).", NexusCommandContext.Exit.Failed);
        }

        // ================================================================ перенос, переименование, копия

        [NexusCommand("asset.move", "Перенести ассет или папку (guid сохраняется); в существующую папку — внутрь неё",
            Usage = "asset.move <spec> <новый путь|папка>")]
        private static int Move(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            var from = MainPath(ctx.Arg(0));
            var to = TargetPath(from, ctx.Arg(1) ?? throw new NexusCommandException("нужен новый путь"));
            if (string.Equals(from, to, StringComparison.Ordinal)) throw new NexusCommandException("Путь не меняется.");
            if (AssetQuery.Exists(to)) throw new NexusCommandException($"«{to}» уже есть.", NexusCommandContext.Exit.Failed);

            EnsureFolder(Parent(to));
            var error = AssetDatabase.ValidateMoveAsset(from, to);
            if (string.IsNullOrEmpty(error)) error = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrEmpty(error)) throw new NexusCommandException("Unity: " + error, NexusCommandContext.Exit.Failed);
            ctx.Print($"✓ {from} → {to}");
            ctx.Data = new { from, to, guid = AssetDatabase.AssetPathToGUID(to) };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("asset.rename", "Переименовать ассет или папку (guid сохраняется; расширение остаётся)",
            Usage = "asset.rename <spec> <новое имя>")]
        private static int Rename(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            var from = MainPath(ctx.Arg(0));
            var name = ctx.Arg(1) ?? throw new NexusCommandException("нужно новое имя");
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) throw new NexusCommandException("Имя без '/', '\\' и ':' — для переноса asset.move.");
            var ext = Path.GetExtension(from);
            if (!AssetDatabase.IsValidFolder(from) && !string.IsNullOrEmpty(ext) && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ext.Length);

            var error = AssetDatabase.RenameAsset(from, name);
            if (!string.IsNullOrEmpty(error)) throw new NexusCommandException("Unity: " + error, NexusCommandContext.Exit.Failed);
            var to = Parent(from) + "/" + name + (AssetDatabase.IsValidFolder(Parent(from) + "/" + name) ? string.Empty : ext);
            ctx.Print($"✓ {from} → {to}");
            ctx.Data = new { from, to };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("asset.copy", "Скопировать ассет или папку (у копии новый guid); в существующую папку — внутрь неё",
            Usage = "asset.copy <spec> <новый путь|папка>")]
        private static int Copy(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            var from = MainPath(ctx.Arg(0));
            var to = TargetPath(from, ctx.Arg(1) ?? throw new NexusCommandException("нужен новый путь"));
            if (AssetQuery.Exists(to)) throw new NexusCommandException($"«{to}» уже есть.", NexusCommandContext.Exit.Failed);

            EnsureFolder(Parent(to));
            if (!AssetDatabase.CopyAsset(from, to)) throw new NexusCommandException($"Unity не скопировал «{from}» в «{to}» (подробности — logs --errors).", NexusCommandContext.Exit.Failed);
            ctx.Print($"✓ {from} → {to}");
            ctx.Data = new { from, to, guid = AssetDatabase.AssetPathToGUID(to) };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ удаление

        [NexusCommand("asset.delete", "Удалить ассеты (по умолчанию в корзину системы). Без --yes — только показать, что удалится и кто ссылается",
            Usage = "asset.delete <spec> [spec …] [--yes] [--hard] [--in Assets] [--no-refs]")]
        private static int Delete(NexusCommandContext ctx)
        {
            if (ctx.Positional.Count == 0) throw new NexusCommandException("нужен хотя бы один ассет");
            var paths = new List<string>();
            foreach (var spec in ctx.Positional)
            {
                var p = MainPath(spec);
                if (!p.StartsWith("Assets/", StringComparison.Ordinal)) throw new NexusCommandException($"«{p}»: удалять можно только внутри Assets/.", NexusCommandContext.Exit.Failed);
                if (!paths.Contains(p)) paths.Add(p);
            }

            var hard = ctx.Flag("hard");
            if (!ctx.Flag("yes"))
            {
                ctx.Print($"Будет удалено {(hard ? "безвозвратно" : "в корзину")} ({paths.Count}):");
                foreach (var p in paths) ctx.Print("  " + p + (AssetDatabase.IsValidFolder(p) ? "/ (папка со всем содержимым)" : string.Empty));

                var refsData = new List<object>();
                if (!ctx.Flag("no-refs"))
                {
                    var targets = AssetReferences.Expand(paths);
                    var refs = AssetReferences.Find(targets, AssetCommands.Scope(ctx), false, out var scanned);
                    if (refs.Count == 0) ctx.Print($"Ссылок извне нет (просмотрено {scanned}).");
                    else ctx.Warn($"На удаляемое ссылаются {refs.Count} (просмотрено {scanned}):");
                    var shown = 0;
                    foreach (var pair in refs)
                    {
                        if (shown++ >= 30) break;
                        ctx.Print("  " + pair.Key + " → " + string.Join(", ", pair.Value));
                        refsData.Add(new { path = pair.Key, targets = pair.Value });
                    }

                    if (refs.Count > 30) ctx.Print($"  … ещё {refs.Count - 30} (asset.refs)");
                }

                ctx.Print("Удалить: добавьте --yes" + (hard ? string.Empty : " (--hard — мимо корзины)"));
                ctx.Data = new { dryRun = true, hard, paths, refs = refsData };
                return NexusCommandContext.Exit.Ok;
            }

            NexusEditorCommands.Guard(ctx);
            var failed = new List<string>();
            var ok = hard ? AssetDatabase.DeleteAssets(paths.ToArray(), failed) : AssetDatabase.MoveAssetsToTrash(paths.ToArray(), failed);
            foreach (var p in paths)
                if (!failed.Contains(p)) ctx.Print("✓ удалено: " + p);
            foreach (var p in failed) ctx.Error("не удалено: " + p);
            ctx.Data = new { dryRun = false, hard, deleted = paths.FindAll(p => !failed.Contains(p)), failed };
            return ok && failed.Count == 0 ? NexusCommandContext.Exit.Ok : NexusCommandContext.Exit.Failed;
        }

        // ================================================================ пути

        private static string MainPath(string spec) =>
            AssetQuery.ResolveMainPath(spec ?? throw new NexusCommandException("нужен ассет: путь, guid или имя"));

        /// <summary>Новый путь: проверка формы; существующая папка — «внутрь неё» с прежним именем файла.</summary>
        private static string TargetPath(string from, string to)
        {
            to = NormalizeNew(to);
            if (AssetDatabase.IsValidFolder(to) && !AssetDatabase.IsValidFolder(from)) return to + "/" + Path.GetFileName(from);
            if (AssetDatabase.IsValidFolder(to) && AssetDatabase.IsValidFolder(from)) return to + "/" + Path.GetFileName(from);
            var ext = Path.GetExtension(from);
            if (!AssetDatabase.IsValidFolder(from) && !string.IsNullOrEmpty(ext) && string.IsNullOrEmpty(Path.GetExtension(to)))
                throw new NexusCommandException($"«{to}» без расширения и такой папки нет — укажите имя файла с {ext} или существующую папку.");
            return to;
        }

        /// <summary>Путь нового ассета: прямые слэши, внутри Assets/, без хвостового '/'.</summary>
        internal static string NormalizeNew(string path)
        {
            var p = path.Trim().Replace('\\', '/').TrimEnd('/');
            if (!p.StartsWith("Assets/", StringComparison.Ordinal) && p != "Assets")
                throw new NexusCommandException($"«{path}»: путь должен начинаться с Assets/.");
            if (p.IndexOf("/../", StringComparison.Ordinal) >= 0 || p.EndsWith("/..", StringComparison.Ordinal) || p.IndexOf(':') >= 0)
                throw new NexusCommandException($"«{path}»: недопустимый путь.");
            return p;
        }

        internal static string Parent(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : path;
        }

        private static void RequireExtension(string path, string ext)
        {
            if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) throw new NexusCommandException($"Путь должен оканчиваться на {ext}: {path}");
        }

        /// <summary>Создать недостающие папки пути по сегментам (AssetDatabase.CreateFolder). Возвращает созданные.</summary>
        internal static List<string> EnsureFolder(string folder)
        {
            var created = new List<string>();
            if (AssetDatabase.IsValidFolder(folder)) return created;
            var parts = folder.Split('/');
            var current = parts[0];
            if (current != "Assets") throw new NexusCommandException($"«{folder}»: папки создаются только внутри Assets/.");
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (AssetQuery.Exists(next)) throw new NexusCommandException($"«{next}» — файл, а не папка.", NexusCommandContext.Exit.Failed);
                    var guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid)) throw new NexusCommandException($"Папка «{next}» не создана.", NexusCommandContext.Exit.Failed);
                    created.Add(next);
                }

                current = next;
            }

            return created;
        }
    }
}
