using System;
using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Команды объектов сцены: поиск, дерево, сведения, создание, удаление, копия, переименование, перенос,
    /// свойства GameObject и transform. Все правки — через Undo, сцены помечаются изменёнными, но не сохраняются (save).
    /// </summary>
    internal static class GameObjectCommands
    {
        // ================================================================ чтение

        [NexusCommand("go.find", "Найти объекты сцен по шаблону имени или пути (* и ?), с фильтрами",
            Usage = "go.find [шаблон] [--scene S] [--tag T] [--layer L] [--component Type] [--active|--inactive] [--max 100]", ReadOnly = true)]
        private static int Find(NexusCommandContext ctx)
        {
            var pattern = ctx.Arg(0) ?? "*";
            var found = UnityObjectQuery.FindAll(pattern, ctx.Get("scene"));

            var tag = ctx.Get("tag");
            if (tag != null) UnityEdit.CheckTag(tag);
            var layerSpec = ctx.Get("layer");
            var layer = layerSpec != null ? UnityEdit.LayerOf(layerSpec) : -1;
            var componentSpec = ctx.Get("component");
            var type = componentSpec != null ? UnityObjectQuery.ResolveComponentType(componentSpec) : null;
            var onlyActive = ctx.Flag("active");
            var onlyInactive = ctx.Flag("inactive");
            if (onlyActive && onlyInactive) throw new NexusCommandException("--active и --inactive вместе не бывают");

            var max = UnityEdit.Max(ctx, "max", 100);
            var items = new List<object>();
            var total = 0;
            foreach (var go in found)
            {
                if (tag != null && go.tag != tag) continue;
                if (layer >= 0 && go.layer != layer) continue;
                if (type != null && go.GetComponent(type) == null) continue;
                if (onlyActive && !go.activeInHierarchy) continue;
                if (onlyInactive && go.activeInHierarchy) continue;

                total++;
                if (items.Count >= max) continue;
                var path = UnityObjectQuery.PathOf(go);
                var id = UnityObjectQuery.IdOf(go);
                ctx.Print($"{path}  {id}{(go.activeInHierarchy ? string.Empty : "  (выкл)")}");
                items.Add(new { path, id, activeSelf = go.activeSelf, activeInHierarchy = go.activeInHierarchy });
            }

            if (total == 0) ctx.Print("Ничего не найдено.");
            else if (total > max) ctx.Print($"… ещё {total - max} (--max)");
            ctx.Data = new { total, items };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("go.tree", "Дерево открытых сцен (или открытого префаба) либо поддерево объекта",
            Usage = "go.tree [spec] [--depth 3] [--components] [--max 300]", ReadOnly = true)]
        private static int Tree(NexusCommandContext ctx)
        {
            var depth = Math.Max(0, ctx.GetInt("depth", 3));
            var withComponents = ctx.Flag("components");
            var state = new TreeState { Max = UnityEdit.Max(ctx, "max", 300), Components = withComponents, Depth = depth };

            var spec = ctx.Arg(0);
            if (spec != null)
            {
                Walk(ctx, UnityObjectQuery.Resolve(spec), 0, state);
            }
            else
            {
                var stage = UnityEdit.Stage;
                if (stage != null)
                {
                    ctx.Print($"== префаб {stage.assetPath}{(stage.scene.isDirty ? " *" : string.Empty)}");
                    if (stage.prefabContentsRoot != null) Walk(ctx, stage.prefabContentsRoot, 0, state);
                }
                else
                {
                    for (var i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var s = SceneManager.GetSceneAt(i);
                        if (!s.isLoaded) continue;
                        ctx.Print($"== {(string.IsNullOrEmpty(s.name) ? "(без имени)" : s.name)}{(s.isDirty ? " *" : string.Empty)}");
                        foreach (var root in s.GetRootGameObjects()) Walk(ctx, root, 0, state);
                    }
                }
            }

            if (state.Skipped > 0) ctx.Print($"… ещё {state.Skipped} объектов (--max, --depth или go.tree <spec>)");
            ctx.Data = new { total = state.Items.Count, skipped = state.Skipped, items = state.Items };
            return NexusCommandContext.Exit.Ok;
        }

        private sealed class TreeState
        {
            public int Max;
            public int Depth;
            public bool Components;
            public int Skipped;
            public readonly List<object> Items = new List<object>();
        }

        private static void Walk(NexusCommandContext ctx, GameObject go, int level, TreeState state)
        {
            if (state.Items.Count >= state.Max)
            {
                state.Skipped++;
                return;
            }

            var t = go.transform;
            var line = new System.Text.StringBuilder();
            line.Append(' ', level * 2).Append(go.name);
            if (!go.activeSelf) line.Append(" (выкл)");
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go)) line.Append(" [префаб]");

            List<string> components = null;
            if (state.Components)
            {
                components = new List<string>();
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c is Transform && !(c is RectTransform)) continue;
                    components.Add(c == null ? "(Missing Script)" : c.GetType().Name);
                }

                if (components.Count > 0) line.Append("  {").Append(string.Join(", ", components)).Append('}');
            }

            var cut = level >= state.Depth && t.childCount > 0;
            if (cut) line.Append($"  +{t.childCount}");
            ctx.Print(line.ToString());
            state.Items.Add(new
            {
                path = UnityObjectQuery.PathOf(go),
                id = UnityObjectQuery.IdOf(go),
                depth = level,
                activeSelf = go.activeSelf,
                children = t.childCount,
                components,
            });

            if (cut) return;
            for (var i = 0; i < t.childCount; i++) Walk(ctx, t.GetChild(i).gameObject, level + 1, state);
        }

        [NexusCommand("go.info", "Сведения об объекте: путь, id, активность, тег, слой, static, transform, компоненты, префаб",
            Usage = "go.info <spec>", ReadOnly = true)]
        private static int Info(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var t = go.transform;
            var path = UnityObjectQuery.PathOf(go);
            var id = UnityObjectQuery.IdOf(go);
            var flags = GameObjectUtility.GetStaticEditorFlags(go);

            ctx.Print($"{path}  {id}");
            ctx.Print($"активен: {(go.activeSelf ? "да" : "нет")} (в иерархии: {(go.activeInHierarchy ? "да" : "нет")})  тег: {go.tag}  слой: {go.layer} {UnityEdit.LayerLabel(go.layer)}  static: {flags}");
            ctx.Print($"local: pos {UnityEdit.Short(t.localPosition)}  rot {UnityEdit.Short(t.localEulerAngles)}  scale {UnityEdit.Short(t.localScale)}");
            ctx.Print($"world: pos {UnityEdit.Short(t.position)}  rot {UnityEdit.Short(t.eulerAngles)}  scale {UnityEdit.Short(t.lossyScale)}");

            object rect = null;
            if (t is RectTransform rt)
            {
                ctx.Print($"rect: anchored {UnityEdit.Short(rt.anchoredPosition)}  size {UnityEdit.Short(rt.sizeDelta)}  anchors {UnityEdit.Short(rt.anchorMin)}–{UnityEdit.Short(rt.anchorMax)}  pivot {UnityEdit.Short(rt.pivot)}");
                rect = new
                {
                    anchoredPosition = UnityEdit.Arr(rt.anchoredPosition),
                    sizeDelta = UnityEdit.Arr(rt.sizeDelta),
                    anchorMin = UnityEdit.Arr(rt.anchorMin),
                    anchorMax = UnityEdit.Arr(rt.anchorMax),
                    pivot = UnityEdit.Arr(rt.pivot),
                };
            }

            var components = ListComponents(ctx, go);
            var prefab = PrefabInfo(ctx, go);
            var parent = t.parent != null ? UnityObjectQuery.PathOf(t.parent.gameObject) : null;
            ctx.Print($"дети: {t.childCount}{(parent != null ? "  родитель: " + parent : string.Empty)}  место среди соседей: {t.GetSiblingIndex()}");

            ctx.Data = new
            {
                path,
                id,
                name = go.name,
                scene = go.scene.IsValid() ? go.scene.path : null,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = go.layer,
                layerName = LayerMask.LayerToName(go.layer),
                staticFlags = flags.ToString(),
                local = new { position = UnityEdit.Arr(t.localPosition), rotation = UnityEdit.Arr(t.localEulerAngles), scale = UnityEdit.Arr(t.localScale) },
                world = new { position = UnityEdit.Arr(t.position), rotation = UnityEdit.Arr(t.eulerAngles), scale = UnityEdit.Arr(t.lossyScale) },
                rect,
                components,
                prefab,
                parent,
                children = t.childCount,
                siblingIndex = t.GetSiblingIndex(),
            };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Компоненты с номерами: печать и данные (общие для go.info и comp.list).</summary>
        internal static List<object> ListComponents(NexusCommandContext ctx, GameObject go)
        {
            var list = new List<object>();
            var all = go.GetComponents<Component>();
            ctx.Print($"компоненты ({all.Length}):");
            for (var i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null)
                {
                    ctx.Print($"  {i,2} (Missing Script)");
                    list.Add(new { index = i, spec = (string)null, type = (string)null, enabled = (bool?)null, missing = true });
                    continue;
                }

                var spec = UnityObjectQuery.ComponentSpec(c);
                var enabledState = EditorUtility.GetObjectEnabled(c);
                bool? enabled = enabledState < 0 ? (bool?)null : enabledState > 0;
                var added = PrefabUtility.IsAddedComponentOverride(c);
                ctx.Print($"  {i,2} {spec}{(enabled == false ? " (выкл)" : string.Empty)}{(added ? " (+ к префабу)" : string.Empty)}");
                list.Add(new { index = i, spec, type = c.GetType().FullName, enabled, missing = false, addedOverride = added });
            }

            return list;
        }

        private static object PrefabInfo(NexusCommandContext ctx, GameObject go)
        {
            if (UnityEdit.InStage(go))
            {
                var stage = UnityEdit.Stage;
                ctx.Print($"в открытом префабе: {stage.assetPath}{(go == stage.prefabContentsRoot ? " (корень)" : string.Empty)}");
            }

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
            {
                if (PrefabUtility.IsAddedGameObjectOverride(go))
                {
                    ctx.Print("префаб: объект добавлен в экземпляр (переопределение)");
                    return new { instance = false, addedOverride = true };
                }

                return null;
            }

            var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            var nearest = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            var asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            var assetType = PrefabUtility.GetPrefabAssetType(go);
            var overrides = outer != null && PrefabUtility.HasPrefabInstanceAnyOverrides(outer, false);
            var isRoot = outer == go;
            ctx.Print($"префаб: {asset} ({assetType}, {status}){(isRoot ? " — корень экземпляра" : "  корень: " + UnityObjectQuery.PathOf(outer))}{(overrides ? "  есть переопределения (prefab.overrides)" : string.Empty)}");
            return new
            {
                instance = true,
                asset,
                assetType = assetType.ToString(),
                status = status.ToString(),
                outermostRoot = UnityObjectQuery.PathOf(outer),
                nearestRoot = UnityObjectQuery.PathOf(nearest),
                isRoot,
                overrides,
            };
        }

        // ================================================================ создание и удаление

        [NexusCommand("go.create", "Создать объект: пустой, примитив или экземпляр префаба (связь с префабом сохраняется)",
            Usage = "go.create <имя> [--parent spec] [--primitive Cube|Sphere|Capsule|Cylinder|Plane|Quad] [--prefab ассет] [--pos x,y,z] [--rot x,y,z] [--scale x,y,z] [--world] [--scene S] [--component Type …] [--play]")]
        private static int Create(NexusCommandContext ctx)
        {
            var name = ctx.Arg(0) ?? throw new NexusCommandException("нужно имя объекта");
            UnityEdit.Begin(ctx);

            // всё разбираем до создания: ошибка аргумента не оставит полуготовый объект
            var parentSpec = ctx.Get("parent");
            var sceneSpec = ctx.Get("scene");
            if (parentSpec != null && sceneSpec != null) throw new NexusCommandException("--parent и --scene вместе не нужны: объект попадёт в сцену родителя");

            var stage = UnityEdit.Stage;
            var parent = parentSpec != null ? UnityObjectQuery.Resolve(parentSpec) : null;
            Scene scene;
            if (parent != null) scene = parent.scene;
            else if (sceneSpec != null) scene = UnityEdit.ResolveScene(sceneSpec);
            else if (stage != null)
            {
                // у префаба один корень — новый объект ложится под него
                parent = stage.prefabContentsRoot;
                scene = stage.scene;
            }
            else scene = SceneManager.GetActiveScene();

            var primitiveSpec = ctx.Get("primitive");
            var prefabSpec = ctx.Get("prefab");
            if (primitiveSpec != null && prefabSpec != null) throw new NexusCommandException("--primitive и --prefab вместе не бывают");

            PrimitiveType? primitive = null;
            if (primitiveSpec != null)
            {
                if (!Enum.TryParse(primitiveSpec, true, out PrimitiveType pt) || !Enum.IsDefined(typeof(PrimitiveType), pt))
                    throw new NexusCommandException($"--primitive: {string.Join(", ", Enum.GetNames(typeof(PrimitiveType)))} — «{primitiveSpec}»");
                primitive = pt;
            }

            GameObject prefab = null;
            if (prefabSpec != null)
            {
                prefab = AssetQuery.Resolve(prefabSpec, typeof(GameObject)) as GameObject;
                if (prefab == null || !PrefabUtility.IsPartOfPrefabAsset(prefab))
                    throw new NexusCommandException($"«{prefabSpec}» — не префаб (assets.find \"t:Prefab …\").", NexusCommandContext.Exit.Failed);
            }

            var types = ComponentTypes(ctx);
            var pos = UnityEdit.OptionalVector3(ctx, "pos");
            var rot = UnityEdit.OptionalVector3(ctx, "rot");
            var scale = UnityEdit.OptionalVector3(ctx, "scale");
            var world = ctx.Flag("world");

            var group = UnityEdit.BeginGroup("Nexus: go.create " + name);
            GameObject go;
            if (prefab != null)
            {
                go = parent != null
                    ? PrefabUtility.InstantiatePrefab(prefab, parent.transform) as GameObject
                    : PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
                if (go == null) throw new NexusCommandException($"Префаб «{prefabSpec}» не создался в сцене.", NexusCommandContext.Exit.Failed);
            }
            else
            {
                go = primitive.HasValue ? GameObject.CreatePrimitive(primitive.Value) : new GameObject();
            }

            try
            {
                go.name = name;
                if (prefab == null)
                {
                    var targetScene = parent != null ? parent.scene : scene;
                    if (go.scene != targetScene)
                    {
                        if (stage != null && targetScene == stage.scene) StageUtility.PlaceGameObjectInCurrentStage(go);
                        else SceneManager.MoveGameObjectToScene(go, targetScene);
                    }

                    // как меню GameObject: родитель, нулевой локальный transform, слой родителя
                    if (parent != null) GameObjectUtility.SetParentAndAlign(go, parent);
                }

                foreach (var type in types)
                    if (go.AddComponent(type) == null)
                        throw new NexusCommandException($"Unity не добавил {type.Name} (logs --errors).", NexusCommandContext.Exit.Failed);

                UnityEdit.ApplyTransform(go.transform, pos, rot, scale, world);
            }
            catch
            {
                Object.DestroyImmediate(go);
                throw;
            }

            Undo.RegisterCreatedObjectUndo(go, "Nexus: go.create " + name);
            UnityEdit.EndGroup(group);
            UnityEdit.MarkDirty(go);

            var path = UnityObjectQuery.PathOf(go);
            var id = UnityObjectQuery.IdOf(go);
            ctx.Print($"✓ {path}  {id}{(prefab != null ? "  [префаб " + AssetDatabase.GetAssetPath(prefab) + "]" : string.Empty)}");
            ctx.Data = new { path, id, prefab = prefab != null ? AssetDatabase.GetAssetPath(prefab) : null };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary><c>--component A --component B,C</c> → проверенные типы (до создания объекта).</summary>
        private static List<Type> ComponentTypes(NexusCommandContext ctx)
        {
            var list = new List<Type>();
            foreach (var value in UnityEdit.Repeated(ctx, "component"))
            foreach (var part in value.Split(',', ';'))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                var type = UnityObjectQuery.ResolveComponentType(part.Trim());
                ComponentCommands.CheckAddable(type);
                list.Add(type);
            }

            return list;
        }

        [NexusCommand("go.delete", "Удалить объект (или все по шаблону с --all) — через Undo",
            Usage = "go.delete <spec> | go.delete <шаблон> --all [--scene S] [--yes] [--dry-run] [--play]")]
        private static int Delete(NexusCommandContext ctx)
        {
            var spec = ctx.Arg(0);
            var all = ctx.Flag("all");

            // «go.delete --all Enemy*» — шаблон съеден как значение --all
            var allValue = ctx.Get("all");
            if (spec == null && allValue != null && !UnityEdit.IsBoolText(allValue))
            {
                spec = allValue;
                all = true;
            }

            if (spec == null) throw new NexusCommandException("нужен объект или шаблон (с --all)");

            var targets = all ? UnityObjectQuery.FindAll(spec, ctx.Get("scene")) : new List<GameObject> { UnityObjectQuery.Resolve(spec) };
            targets = DropDescendants(targets);
            if (targets.Count == 0) throw new NexusCommandException($"Под «{spec}» ничего не подходит (go.find \"{spec}\").", NexusCommandContext.Exit.Failed);

            var stage = UnityEdit.Stage;
            foreach (var go in targets)
                if (stage != null && go == stage.prefabContentsRoot)
                    throw new NexusCommandException("Корень открытого префаба не удаляется — prefab.close, затем удалите ассет.", NexusCommandContext.Exit.Failed);

            var paths = new List<string>();
            foreach (var go in targets) paths.Add(UnityObjectQuery.PathOf(go));

            if (ctx.Flag("dry-run") || (targets.Count > 1 && !ctx.Flag("yes")))
            {
                ctx.Print($"Будет удалено {targets.Count} (с детьми):");
                for (var i = 0; i < paths.Count && i < 50; i++) ctx.Print("  " + paths[i]);
                if (paths.Count > 50) ctx.Print($"  … ещё {paths.Count - 50}");
                ctx.Data = new { dryRun = true, total = targets.Count, paths };
                if (ctx.Flag("dry-run")) return NexusCommandContext.Exit.Ok;
                throw new NexusCommandException("Объектов больше одного — подтвердите: --yes", NexusCommandContext.Exit.Failed);
            }

            UnityEdit.Begin(ctx);
            var scenes = new List<Scene>();
            foreach (var go in targets)
                if (!scenes.Contains(go.scene)) scenes.Add(go.scene);

            var group = UnityEdit.BeginGroup($"Nexus: go.delete ({targets.Count})");
            var failed = new List<string>();
            for (var i = 0; i < targets.Count; i++)
            {
                Undo.DestroyObjectImmediate(targets[i]);
                if (targets[i] != null) failed.Add(paths[i]);
            }

            UnityEdit.EndGroup(group);
            foreach (var s in scenes) UnityEdit.MarkDirty(s);

            foreach (var p in failed) ctx.Error("не удалён (Unity запретил, logs --errors): " + p);
            ctx.Print($"✓ удалено {targets.Count - failed.Count}{(targets.Count == 1 ? ": " + paths[0] : string.Empty)}");
            ctx.Data = new { total = targets.Count - failed.Count, paths, failed };
            return failed.Count > 0 ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok;
        }

        /// <summary>Убрать объекты, чей предок тоже в списке: он удалится вместе с предком.</summary>
        private static List<GameObject> DropDescendants(List<GameObject> list)
        {
            var set = new HashSet<GameObject>(list);
            var result = new List<GameObject>();
            foreach (var go in list)
            {
                var covered = false;
                for (var p = go.transform.parent; p != null; p = p.parent)
                    if (set.Contains(p.gameObject))
                    {
                        covered = true;
                        break;
                    }

                if (!covered) result.Add(go);
            }

            return result;
        }

        [NexusCommand("go.duplicate", "Копия объекта (как Ctrl+D: связи с префабами сохраняются)",
            Usage = "go.duplicate <spec> [--name N] [--parent spec] [--play]")]
        private static int Duplicate(NexusCommandContext ctx)
        {
            var source = UnityObjectQuery.Resolve(ctx.Arg(0));
            UnityEdit.Begin(ctx);
            var stage = UnityEdit.Stage;
            if (stage != null && source == stage.prefabContentsRoot) throw new NexusCommandException("Корень префаба не копируется: у префаба один корень.", NexusCommandContext.Exit.Failed);

            var parentSpec = ctx.Get("parent");
            var parent = parentSpec != null ? UnityObjectQuery.Resolve(parentSpec) : null;
            var newName = ctx.Get("name");

            var group = UnityEdit.BeginGroup("Nexus: go.duplicate " + source.name);
            var copy = DuplicateViaPasteboard(source) ?? DuplicateFallback(ctx, source);

            if (!string.IsNullOrEmpty(newName))
            {
                Undo.RecordObject(copy, "Nexus: go.duplicate");
                copy.name = newName;
            }

            if (parent != null)
            {
                CheckNotDescendant(copy, parent);
                Undo.SetTransformParent(copy.transform, parent.transform, true, "Nexus: go.duplicate");
            }

            UnityEdit.EndGroup(group);
            UnityEdit.MarkDirty(copy);
            var path = UnityObjectQuery.PathOf(copy);
            var id = UnityObjectQuery.IdOf(copy);
            ctx.Print($"✓ {path}  {id}");
            ctx.Data = new { path, id, source = UnityObjectQuery.PathOf(source) };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Дублирование как в иерархии (Ctrl+D): Unity сам пишет Undo и сохраняет вложенные префабы.</summary>
        private static GameObject DuplicateViaPasteboard(GameObject source)
        {
            var previous = Selection.objects;
            try
            {
                Selection.objects = new Object[] { source };
                Unsupported.DuplicateGameObjectsUsingPasteboard();
                var copy = Selection.activeGameObject;
                return copy != null && copy != source ? copy : null;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("go.duplicate", e);
                return null;
            }
            finally
            {
                Selection.objects = previous;
            }
        }

        /// <summary>Запасной путь: Instantiate (экземпляр префаба — заново из префаба с переопределениями свойств).</summary>
        private static GameObject DuplicateFallback(NexusCommandContext ctx, GameObject source)
        {
            GameObject copy;
            var parent = source.transform.parent;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(source))
            {
                var asset = PrefabUtility.GetCorrespondingObjectFromSource(source);
                copy = parent != null ? PrefabUtility.InstantiatePrefab(asset, parent) as GameObject : PrefabUtility.InstantiatePrefab(asset, source.scene) as GameObject;
                if (copy == null) throw new NexusCommandException("Копия не создалась.", NexusCommandContext.Exit.Failed);
                PrefabUtility.SetPropertyModifications(copy, PrefabUtility.GetPropertyModifications(source));
                ctx.Warn("копия собрана из префаба: добавленные/удалённые компоненты и объекты экземпляра не перенесены");
            }
            else
            {
                copy = Object.Instantiate(source, parent, false);
                if (parent == null && copy.scene != source.scene) SceneManager.MoveGameObjectToScene(copy, source.scene);
                ctx.Warn("копия через Instantiate: вложенные экземпляры префабов стали обычными объектами");
            }

            copy.name = source.name;
            copy.transform.localPosition = source.transform.localPosition;
            copy.transform.localRotation = source.transform.localRotation;
            copy.transform.localScale = source.transform.localScale;
            copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            Undo.RegisterCreatedObjectUndo(copy, "Nexus: go.duplicate");
            return copy;
        }

        [NexusCommand("go.rename", "Переименовать объект", Usage = "go.rename <spec> <новое имя> [--play]")]
        private static int Rename(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var name = ctx.Arg(1) ?? throw new NexusCommandException("нужно новое имя");
            if (name.IndexOf('/') >= 0) ctx.Warn("в имени есть '/': адресовать такой объект путём не получится — только #id");
            UnityEdit.Begin(ctx);
            var old = go.name;
            Undo.RecordObject(go, "Nexus: go.rename");
            go.name = name;
            UnityEdit.MarkDirty(go);
            ctx.Print($"✓ {old} → {UnityObjectQuery.PathOf(go)}");
            ctx.Data = new { path = UnityObjectQuery.PathOf(go), id = UnityObjectQuery.IdOf(go), old };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ перенос и свойства

        [NexusCommand("go.move", "Сменить родителя, место среди соседей или сцену (корнем)",
            Usage = "go.move <spec> [--parent spec | --root | --scene S] [--index N] [--keep-local] [--play]")]
        private static int Move(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var parentSpec = ctx.Get("parent");
            var sceneSpec = ctx.Get("scene");
            var toRoot = ctx.Flag("root");
            var hasIndex = ctx.Has("index");
            var index = ctx.GetInt("index", int.MinValue);
            if (hasIndex && index == int.MinValue) throw new NexusCommandException("--index: нужно целое ≥ 0");
            var keepWorld = !ctx.Flag("keep-local");

            var modes = (parentSpec != null ? 1 : 0) + (sceneSpec != null ? 1 : 0) + (toRoot ? 1 : 0);
            if (modes > 1) throw new NexusCommandException("выберите одно: --parent, --root или --scene");
            if (modes == 0 && !hasIndex) throw new NexusCommandException("что сделать: --parent spec, --root, --scene S или --index N");

            UnityEdit.Begin(ctx);
            var t = go.transform;
            var stage = UnityEdit.Stage;
            var inStage = UnityEdit.InStage(go);
            if (inStage && go == stage.prefabContentsRoot && modes > 0) throw new NexusCommandException("Корень префаба не переносится.", NexusCommandContext.Exit.Failed);

            var oldScene = go.scene;
            var oldPath = UnityObjectQuery.PathOf(go);
            const string undoName = "Nexus: go.move";
            var group = UnityEdit.BeginGroup(undoName);

            if (sceneSpec != null)
            {
                if (inStage) throw new NexusCommandException("Объект открытого префаба в сцену не переносится — prefab.close.", NexusCommandContext.Exit.Failed);
                var scene = UnityEdit.ResolveScene(sceneSpec);
                if (t.parent != null) SetParent(t, null, keepWorld, undoName);
                if (go.scene != scene) Undo.MoveGameObjectToScene(go, scene, undoName);
            }
            else if (toRoot)
            {
                if (inStage) throw new NexusCommandException("У префаба один корень — укажите --parent.", NexusCommandContext.Exit.Failed);
                if (t.parent != null) SetParent(t, null, keepWorld, undoName);
            }
            else if (parentSpec != null)
            {
                var parent = UnityObjectQuery.Resolve(parentSpec);
                CheckNotDescendant(go, parent);
                if (t.parent != parent.transform) SetParent(t, parent.transform, keepWorld, undoName);
            }

            if (hasIndex)
            {
                if (index < 0) throw new NexusCommandException("--index: целое ≥ 0");
                Undo.SetSiblingIndex(t, index, undoName);
            }

            UnityEdit.EndGroup(group);
            UnityEdit.MarkDirty(oldScene);
            UnityEdit.MarkDirty(go.scene);

            var path = UnityObjectQuery.PathOf(go);
            ctx.Print($"✓ {oldPath} → {path}  (место {t.GetSiblingIndex()})");
            ctx.Data = new { path, id = UnityObjectQuery.IdOf(go), from = oldPath, siblingIndex = t.GetSiblingIndex() };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Смена родителя с проверкой: Unity молча отказывает для детей экземпляра префаба.</summary>
        private static void SetParent(Transform t, Transform parent, bool keepWorld, string undoName)
        {
            Undo.SetTransformParent(t, parent, keepWorld, undoName);
            if (t.parent != parent)
                throw new NexusCommandException($"Unity не сменил родителя «{t.name}» (часть экземпляра префаба?) — prefab.unpack или правка в prefab.open. Подробности: logs --errors",
                    NexusCommandContext.Exit.Failed);
        }

        private static void CheckNotDescendant(GameObject go, GameObject parent)
        {
            for (var p = parent.transform; p != null; p = p.parent)
                if (p == go.transform)
                    throw new NexusCommandException($"«{parent.name}» — сам объект или его потомок: так родителя не сменить.", NexusCommandContext.Exit.Failed);
        }

        [NexusCommand("go.set", "Свойства объекта: активность, тег, слой, static-флаги, имя",
            Usage = "go.set <spec> [--active true|false] [--tag T] [--layer L] [--static true|false|Флаг|Флаг…] [--name N] [--children (слой детям)] [--play]")]
        private static int Set(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var active = ctx.Get("active");
            var tag = ctx.Get("tag");
            var layerSpec = ctx.Get("layer");
            var staticSpec = ctx.Get("static");
            var name = ctx.Get("name");
            if (active == null && tag == null && layerSpec == null && staticSpec == null && name == null)
                throw new NexusCommandException("что задать: --active, --tag, --layer, --static, --name");

            // проверить всё до правок
            bool? activeValue = active != null ? UnityEdit.ParseBool(active, "--active") : (bool?)null;
            if (tag != null) UnityEdit.CheckTag(tag);
            var layer = layerSpec != null ? UnityEdit.LayerOf(layerSpec) : -1;
            StaticEditorFlags? flags = staticSpec != null ? ParseStatic(staticSpec) : (StaticEditorFlags?)null;
            UnityEdit.Begin(ctx);

            const string undoName = "Nexus: go.set";
            var group = UnityEdit.BeginGroup(undoName);
            Undo.RecordObject(go, undoName);
            if (name != null) go.name = name;
            if (tag != null) go.tag = tag;
            if (layer >= 0)
            {
                go.layer = layer;
                if (ctx.Flag("children"))
                {
                    var all = new List<GameObject>();
                    UnityObjectQuery.Walk(go, all);
                    foreach (var child in all)
                    {
                        if (child == go) continue;
                        Undo.RecordObject(child, undoName);
                        child.layer = layer;
                    }
                }
            }

            if (flags.HasValue) GameObjectUtility.SetStaticEditorFlags(go, flags.Value);
            if (activeValue.HasValue) go.SetActive(activeValue.Value);
            UnityEdit.EndGroup(group);
            UnityEdit.MarkDirty(go);

            var currentFlags = GameObjectUtility.GetStaticEditorFlags(go);
            ctx.Print($"✓ {UnityObjectQuery.PathOf(go)}  активен: {(go.activeSelf ? "да" : "нет")}  тег: {go.tag}  слой: {UnityEdit.LayerLabel(go.layer)}  static: {currentFlags}");
            ctx.Data = new
            {
                path = UnityObjectQuery.PathOf(go),
                id = UnityObjectQuery.IdOf(go),
                activeSelf = go.activeSelf,
                tag = go.tag,
                layer = go.layer,
                staticFlags = currentFlags.ToString(),
            };
            return NexusCommandContext.Exit.Ok;
        }

        private static StaticEditorFlags ParseStatic(string spec)
        {
            var s = spec.Trim();
            if (UnityEdit.IsBoolText(s))
            {
                if (!UnityEdit.ParseBool(s, "--static")) return 0;
                // «всё», как галочка Static в инспекторе: объединение всех объявленных флагов
                long everything = 0;
                foreach (var v in Enum.GetValues(typeof(StaticEditorFlags))) everything |= Convert.ToInt64(v);
                return (StaticEditorFlags)everything;
            }

            long result = 0;
            foreach (var raw in s.Split('|', ','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                if (!Enum.TryParse(part, true, out StaticEditorFlags f))
                    throw new NexusCommandException($"--static: нет флага «{part}». Есть: {string.Join(", ", Enum.GetNames(typeof(StaticEditorFlags)))}");
                result |= Convert.ToInt64(f);
            }

            return (StaticEditorFlags)result;
        }

        [NexusCommand("transform.set", "Положение, поворот, масштаб (локально или --world); у RectTransform — anchored и size",
            Usage = "transform.set <spec> [--pos x,y,z] [--rot x,y,z] [--scale x,y,z] [--world] [--anchored x,y] [--size w,h] [--play]")]
        private static int TransformSet(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var pos = UnityEdit.OptionalVector3(ctx, "pos");
            var rot = UnityEdit.OptionalVector3(ctx, "rot");
            var scale = UnityEdit.OptionalVector3(ctx, "scale");
            var world = ctx.Flag("world");
            var anchoredText = ctx.Get("anchored");
            var sizeText = ctx.Get("size");
            Vector2? anchored = anchoredText != null ? UnityEdit.Vector2Of(anchoredText, "--anchored") : (Vector2?)null;
            Vector2? size = sizeText != null ? UnityEdit.Vector2Of(sizeText, "--size") : (Vector2?)null;
            if (!pos.HasValue && !rot.HasValue && !scale.HasValue && !anchored.HasValue && !size.HasValue)
                throw new NexusCommandException("что задать: --pos, --rot, --scale, --anchored, --size");

            var t = go.transform;
            var rt = t as RectTransform;
            if ((anchored.HasValue || size.HasValue) && rt == null)
                throw new NexusCommandException($"«{go.name}» — не RectTransform: --anchored/--size только для UI.", NexusCommandContext.Exit.Failed);

            UnityEdit.Begin(ctx);
            Undo.RecordObject(t, "Nexus: transform.set");
            UnityEdit.ApplyTransform(t, pos, rot, scale, world);
            if (rt != null)
            {
                if (anchored.HasValue) rt.anchoredPosition = anchored.Value;
                if (size.HasValue) rt.sizeDelta = size.Value;
            }

            if (world && scale.HasValue && t.parent != null) ctx.Warn("мировой масштаб задан через локальный (с поворотом родителя возможен перекос)");
            UnityEdit.MarkDirty(go);

            ctx.Print($"✓ {UnityObjectQuery.PathOf(go)}");
            ctx.Print($"  local: pos {UnityEdit.Short(t.localPosition)}  rot {UnityEdit.Short(t.localEulerAngles)}  scale {UnityEdit.Short(t.localScale)}");
            ctx.Print($"  world: pos {UnityEdit.Short(t.position)}  rot {UnityEdit.Short(t.eulerAngles)}  scale {UnityEdit.Short(t.lossyScale)}");
            if (rt != null) ctx.Print($"  rect: anchored {UnityEdit.Short(rt.anchoredPosition)}  size {UnityEdit.Short(rt.sizeDelta)}");
            ctx.Data = new
            {
                path = UnityObjectQuery.PathOf(go),
                local = new { position = UnityEdit.Arr(t.localPosition), rotation = UnityEdit.Arr(t.localEulerAngles), scale = UnityEdit.Arr(t.localScale) },
                world = new { position = UnityEdit.Arr(t.position), rotation = UnityEdit.Arr(t.eulerAngles), scale = UnityEdit.Arr(t.lossyScale) },
                rect = rt != null ? new { anchoredPosition = UnityEdit.Arr(rt.anchoredPosition), sizeDelta = UnityEdit.Arr(rt.sizeDelta) } : null,
            };
            return NexusCommandContext.Exit.Ok;
        }
    }
}
