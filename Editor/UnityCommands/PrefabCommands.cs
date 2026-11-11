using System;
using System.Collections.Generic;
using System.IO;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Команды префабов: создание из объекта, открытие/сохранение/закрытие Prefab Stage, переопределения экземпляра,
    /// apply/revert/unpack. Без диалогов: несохранённое — ошибка с подсказкой --save/--discard.
    /// </summary>
    internal static class PrefabCommands
    {
        // ================================================================ создание

        [NexusCommand("prefab.create", "Сохранить объект как префаб (по умолчанию объект становится его экземпляром)",
            Usage = "prefab.create <spec> <Assets/…/Имя.prefab> [--no-connect] [--overwrite]")]
        private static int Create(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var path = (ctx.Arg(1) ?? throw new NexusCommandException("нужен путь Assets/…/Имя.prefab")).Trim().Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new NexusCommandException("путь префаба — Assets/…/Имя.prefab");

            var connect = !ctx.Flag("no-connect");
            UnityEdit.BeginAsset(ctx);
            if (EditorApplication.isPlaying && connect)
            {
                ctx.Warn("в Play объект не связывается с префабом — сохранено как --no-connect");
                connect = false;
            }

            // рабочая папка редактора — корень проекта
            var exists = AssetDatabase.LoadMainAssetAtPath(path) != null || File.Exists(path);
            if (exists && !ctx.Flag("overwrite"))
                throw new NexusCommandException($"{path} уже есть — добавьте --overwrite (экземпляры старого префаба получат новое содержимое).", NexusCommandContext.Exit.Failed);

            EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));

            bool ok;
            GameObject result;
            try
            {
                result = connect
                    ? PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction, out ok)
                    : PrefabUtility.SaveAsPrefabAsset(go, path, out ok);
            }
            catch (ArgumentException e)
            {
                throw new NexusCommandException("Префаб не сохранён: " + e.Message, NexusCommandContext.Exit.Failed);
            }

            if (!ok || result == null) throw new NexusCommandException($"Префаб не сохранён: {path} (logs --errors).", NexusCommandContext.Exit.Failed);
            if (connect) UnityEdit.MarkDirty(go);

            var guid = AssetDatabase.AssetPathToGUID(path);
            ctx.Print($"✓ {path}{(connect ? "  ← " + UnityObjectQuery.PathOf(go) + " теперь экземпляр" : string.Empty)}");
            ctx.Data = new { path, guid, connected = connect, source = UnityObjectQuery.PathOf(go) };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Создать недостающие папки по сегментам: Assets/A/B.</summary>
        private static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    throw new NexusCommandException($"Папка {next} не создалась.", NexusCommandContext.Exit.Failed);
                current = next;
            }
        }

        // ================================================================ Prefab Stage

        [NexusCommand("prefab.open", "Открыть префаб на редактирование (Prefab Stage): дальше go.*/comp.* адресуют его",
            Usage = "prefab.open <ассет: путь | guid | имя> [--save|--discard (для уже открытого префаба)]")]
        private static int Open(NexusCommandContext ctx)
        {
            var spec = ctx.Arg(0) ?? throw new NexusCommandException("нужен префаб: путь Assets/…prefab, guid или имя");
            UnityEdit.BeginAsset(ctx);
            var asset = AssetQuery.Resolve(spec, typeof(GameObject)) as GameObject;
            if (asset == null) throw new NexusCommandException($"«{spec}» — не префаб.", NexusCommandContext.Exit.Failed);
            var path = AssetDatabase.GetAssetPath(asset);
            var type = PrefabUtility.GetPrefabAssetType(asset);
            if (type == PrefabAssetType.NotAPrefab || type == PrefabAssetType.MissingAsset || string.IsNullOrEmpty(path))
                throw new NexusCommandException($"«{spec}» — не префаб.", NexusCommandContext.Exit.Failed);
            if (type == PrefabAssetType.Model)
                throw new NexusCommandException($"{path} — модель: она не открывается как префаб (создайте вариант).", NexusCommandContext.Exit.Failed);

            var current = UnityEdit.Stage;
            if (current != null)
            {
                if (string.Equals(current.assetPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Print($"✓ уже открыт: {path}{(current.scene.isDirty ? " *" : string.Empty)}");
                    ctx.Data = new { path, dirty = current.scene.isDirty, already = true };
                    return NexusCommandContext.Exit.Ok;
                }

                LeaveDirty(ctx, current);
            }

            var stage = PrefabStageUtility.OpenPrefab(path);
            if (stage == null) throw new NexusCommandException($"Префаб не открылся: {path} (logs --errors).", NexusCommandContext.Exit.Failed);
            ctx.Print($"✓ открыт {path}  корень: {stage.prefabContentsRoot.name}");
            ctx.Data = new { path, root = stage.prefabContentsRoot.name, already = false };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("prefab.close", "Закрыть открытый префаб (несохранённое — только с --save или --discard)",
            Usage = "prefab.close [--save|--discard]")]
        private static int Close(NexusCommandContext ctx)
        {
            var stage = UnityEdit.Stage;
            if (stage == null)
            {
                ctx.Print("Префаб не открыт.");
                ctx.Data = new { closed = false };
                return NexusCommandContext.Exit.Ok;
            }

            UnityEdit.BeginAsset(ctx);
            var path = stage.assetPath;
            var saved = LeaveDirty(ctx, stage);
            StageUtility.GoToMainStage();
            ctx.Print($"✓ закрыт {path}{(saved ? " (сохранён)" : string.Empty)}");
            ctx.Data = new { closed = true, path, saved };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("prefab.save", "Сохранить открытый префаб", Usage = "prefab.save")]
        private static int Save(NexusCommandContext ctx)
        {
            var stage = UnityEdit.Stage ?? throw new NexusCommandException("Префаб не открыт (prefab.open).", NexusCommandContext.Exit.Failed);
            UnityEdit.BeginAsset(ctx);
            SaveStage(stage);
            ctx.Print("✓ сохранён " + stage.assetPath);
            ctx.Data = new { path = stage.assetPath };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>
        /// Перед уходом со Stage: изменён — --save сохраняет, --discard отбрасывает, иначе ошибка (Unity спросил бы диалогом).
        /// Возвращает true, если сохранили.
        /// </summary>
        private static bool LeaveDirty(NexusCommandContext ctx, PrefabStage stage)
        {
            if (!stage.scene.isDirty) return false;
            var save = ctx.Flag("save");
            var discard = ctx.Flag("discard");
            if (save && discard) throw new NexusCommandException("--save и --discard вместе не бывают");
            if (save)
            {
                SaveStage(stage);
                return true;
            }

            if (discard)
            {
                // снять пометку: Unity закроет Stage без вопроса, изменения пропадут
                stage.ClearDirtiness();
                return false;
            }

            throw new NexusCommandException($"В открытом префабе {stage.assetPath} есть несохранённые изменения — добавьте --save или --discard.", NexusCommandContext.Exit.Failed);
        }

        private static void SaveStage(PrefabStage stage)
        {
            var root = stage.prefabContentsRoot;
            if (root == null) throw new NexusCommandException("У открытого префаба нет корня.", NexusCommandContext.Exit.Failed);
            PrefabUtility.SaveAsPrefabAsset(root, stage.assetPath, out var ok);
            if (!ok) throw new NexusCommandException($"Префаб {stage.assetPath} не сохранён (logs --errors).", NexusCommandContext.Exit.Failed);
            stage.ClearDirtiness();
        }

        // ================================================================ переопределения экземпляра

        [NexusCommand("prefab.overrides", "Переопределения экземпляра префаба: свойства, добавленные/удалённые компоненты и объекты",
            Usage = "prefab.overrides <spec> [--defaults] [--max 50]", ReadOnly = true)]
        private static int Overrides(NexusCommandContext ctx)
        {
            var root = InstanceRoot(ctx, UnityObjectQuery.Resolve(ctx.Arg(0)));
            var report = Collect(root, ctx.Flag("defaults"));
            Print(ctx, root, report, UnityEdit.Max(ctx, "max", 50));
            ctx.Data = report.ToData(root);
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("prefab.apply", "Применить переопределения экземпляра к ассету префаба (меняет все его экземпляры)",
            Usage = "prefab.apply <spec> [--yes] [--dry-run]")]
        private static int Apply(NexusCommandContext ctx)
        {
            var root = InstanceRoot(ctx, UnityObjectQuery.Resolve(ctx.Arg(0)));
            var asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            if (PrefabUtility.GetPrefabAssetType(root) == PrefabAssetType.Model)
                throw new NexusCommandException($"{asset} — модель: переопределения к ней не применяются.", NexusCommandContext.Exit.Failed);

            var report = Collect(root, false);
            if (report.Total == 0)
            {
                ctx.Print($"Переопределений нет — применять нечего ({asset}).");
                ctx.Data = report.ToData(root);
                return NexusCommandContext.Exit.Ok;
            }

            if (ctx.Flag("dry-run") || !ctx.Flag("yes"))
            {
                ctx.Print($"Будет применено к {asset}:");
                Print(ctx, root, report, 50);
                ctx.Data = report.ToData(root);
                if (ctx.Flag("dry-run")) return NexusCommandContext.Exit.Ok;
                throw new NexusCommandException($"Это изменит {asset} и все его экземпляры — подтвердите: --yes", NexusCommandContext.Exit.Failed);
            }

            UnityEdit.BeginAsset(ctx);
            UnityEdit.NotInPlay("prefab.apply");
            try
            {
                PrefabUtility.ApplyPrefabInstance(root, InteractionMode.UserAction);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                throw new NexusCommandException($"Не применено к {asset}: {e.Message}", NexusCommandContext.Exit.Failed);
            }

            UnityEdit.MarkDirty(root);
            ctx.Print($"✓ применено к {asset} ({report.Total} изм.)");
            ctx.Data = new { path = UnityObjectQuery.PathOf(root), asset, applied = report.Total };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("prefab.revert", "Отменить все переопределения экземпляра (вернуть как в префабе)",
            Usage = "prefab.revert <spec> [--dry-run] [--play]")]
        private static int Revert(NexusCommandContext ctx)
        {
            var root = InstanceRoot(ctx, UnityObjectQuery.Resolve(ctx.Arg(0)));
            var report = Collect(root, false);
            if (ctx.Flag("dry-run"))
            {
                ctx.Print("Будет отменено:");
                Print(ctx, root, report, 50);
                ctx.Data = report.ToData(root);
                return NexusCommandContext.Exit.Ok;
            }

            UnityEdit.Begin(ctx);
            PrefabUtility.RevertPrefabInstance(root, InteractionMode.UserAction);
            UnityEdit.MarkDirty(root);
            ctx.Print($"✓ {UnityObjectQuery.PathOf(root)}: отменено {report.Total} изм.");
            ctx.Data = new { path = UnityObjectQuery.PathOf(root), reverted = report.Total };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("prefab.unpack", "Распаковать экземпляр префаба в обычные объекты (--completely — и вложенные)",
            Usage = "prefab.unpack <spec> [--completely] [--play]")]
        private static int Unpack(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                throw new NexusCommandException($"«{UnityObjectQuery.PathOf(go)}» — не экземпляр префаба.", NexusCommandContext.Exit.Failed);
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root != go)
                throw new NexusCommandException($"Распаковывается только внешний корень экземпляра: {UnityObjectQuery.PathOf(root)}", NexusCommandContext.Exit.Failed);

            UnityEdit.Begin(ctx);
            var asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            var mode = ctx.Flag("completely") ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot;
            PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.UserAction);
            UnityEdit.MarkDirty(root);
            ctx.Print($"✓ {UnityObjectQuery.PathOf(root)} распакован ({asset}{(mode == PrefabUnpackMode.Completely ? ", полностью" : string.Empty)})");
            ctx.Data = new { path = UnityObjectQuery.PathOf(root), asset, completely = mode == PrefabUnpackMode.Completely };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ общее

        /// <summary>Внешний корень экземпляра (apply/revert работают с ним); не экземпляр — ошибка.</summary>
        private static GameObject InstanceRoot(NexusCommandContext ctx, GameObject go)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                var hint = PrefabUtility.IsAddedGameObjectOverride(go) ? " (это объект, добавленный в экземпляр, — смотрите его корень)" : string.Empty;
                throw new NexusCommandException($"«{UnityObjectQuery.PathOf(go)}» — не экземпляр префаба{hint}.", NexusCommandContext.Exit.Failed);
            }

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null) throw new NexusCommandException("Не найден корень экземпляра префаба.", NexusCommandContext.Exit.Failed);
            if (root != go) ctx.Warn($"берётся весь экземпляр от корня: {UnityObjectQuery.PathOf(root)}");
            if (PrefabUtility.GetPrefabInstanceStatus(root) == PrefabInstanceStatus.MissingAsset)
                throw new NexusCommandException("Ассет префаба этого экземпляра потерян (MissingAsset).", NexusCommandContext.Exit.Failed);
            return root;
        }

        private sealed class Report
        {
            public readonly List<object> Properties = new List<object>();
            public readonly List<string> PropertyLines = new List<string>();
            public readonly List<string> AddedComponents = new List<string>();
            public readonly List<string> RemovedComponents = new List<string>();
            public readonly List<string> AddedObjects = new List<string>();
            public readonly List<string> RemovedObjects = new List<string>();

            public int Total => Properties.Count + AddedComponents.Count + RemovedComponents.Count + AddedObjects.Count + RemovedObjects.Count;

            public object ToData(GameObject root) => new
            {
                path = UnityObjectQuery.PathOf(root),
                asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root),
                total = Total,
                properties = Properties,
                addedComponents = AddedComponents,
                removedComponents = RemovedComponents,
                addedObjects = AddedObjects,
                removedObjects = RemovedObjects,
            };
        }

        private static Report Collect(GameObject root, bool includeDefaults)
        {
            var r = new Report();
            var mods = PrefabUtility.GetPropertyModifications(root);
            if (mods != null)
                foreach (var m in mods)
                {
                    if (m == null || m.target == null) continue;
                    if (!includeDefaults && PrefabUtility.IsDefaultOverride(m)) continue;
                    var target = TargetLabel(m.target);
                    var value = m.objectReference != null ? RefLabel(m.objectReference) : m.value;
                    r.Properties.Add(new { target, property = m.propertyPath, value });
                    r.PropertyLines.Add($"{target}: {m.propertyPath} = {value}");
                }

            foreach (var a in PrefabUtility.GetAddedComponents(root))
                if (a.instanceComponent != null)
                    r.AddedComponents.Add(UnityObjectQuery.PathOf(a.instanceComponent.gameObject) + "@" + UnityObjectQuery.ComponentSpec(a.instanceComponent));

            foreach (var rc in PrefabUtility.GetRemovedComponents(root))
                r.RemovedComponents.Add(UnityObjectQuery.PathOf(rc.containingInstanceGameObject) + "@" + (rc.assetComponent != null ? rc.assetComponent.GetType().Name : "?"));

            foreach (var a in PrefabUtility.GetAddedGameObjects(root))
                if (a.instanceGameObject != null)
                    r.AddedObjects.Add(UnityObjectQuery.PathOf(a.instanceGameObject));

            foreach (var rg in PrefabUtility.GetRemovedGameObjects(root))
            {
                var parent = rg.parentOfRemovedGameObjectInInstance != null ? UnityObjectQuery.PathOf(rg.parentOfRemovedGameObjectInInstance) : "?";
                r.RemovedObjects.Add(parent + "/" + (rg.assetGameObject != null ? rg.assetGameObject.name : "?"));
            }

            return r;
        }

        private static void Print(NexusCommandContext ctx, GameObject root, Report r, int max)
        {
            ctx.Print($"{UnityObjectQuery.PathOf(root)}  ← {PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root)}");
            if (r.Total == 0)
            {
                ctx.Print("Переопределений нет.");
                return;
            }

            Section(ctx, "свойства", r.PropertyLines, max, "~ ");
            Section(ctx, "добавлены компоненты", r.AddedComponents, max, "+ ");
            Section(ctx, "удалены компоненты", r.RemovedComponents, max, "− ");
            Section(ctx, "добавлены объекты", r.AddedObjects, max, "+ ");
            Section(ctx, "удалены объекты", r.RemovedObjects, max, "− ");
        }

        private static void Section(NexusCommandContext ctx, string title, List<string> lines, int max, string mark)
        {
            if (lines.Count == 0) return;
            ctx.Print($"{title} ({lines.Count}):");
            for (var i = 0; i < lines.Count && i < max; i++) ctx.Print("  " + mark + lines[i]);
            if (lines.Count > max) ctx.Print($"  … ещё {lines.Count - max} (--max)");
        }

        /// <summary>Объект префаба, к которому относится изменение: «Объект@Компонент» по именам из ассета.</summary>
        private static string TargetLabel(Object target)
        {
            if (target is Component c) return RelativeInAsset(c.gameObject) + "@" + c.GetType().Name;
            if (target is GameObject g) return RelativeInAsset(g);
            return target.name;
        }

        private static string RelativeInAsset(GameObject go)
        {
            var path = go.name;
            for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }

        private static string RefLabel(Object o)
        {
            if (EditorUtility.IsPersistent(o)) return AssetQuery.PathOf(o);
            if (o is GameObject g) return "go:" + UnityObjectQuery.PathOf(g);
            if (o is Component c) return "go:" + UnityObjectQuery.PathOf(c.gameObject) + "@" + c.GetType().Name;
            return o.name;
        }
    }
}
