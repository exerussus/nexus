using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Адресация объектов сцены для команд: путь в иерархии, <c>Сцена:Путь</c>, имя, <c>#id</c>; типы компонентов
    /// по короткому или полному имени. Открыт Prefab Stage — адресация идёт по нему (явная сцена <c>Сцена:…</c> —
    /// по основным сценам). Неактивные объекты находятся. Без состояния.
    /// </summary>
    public static class UnityObjectQuery
    {
        private const int MaxCandidates = 10;

        // ================================================================ объекты

        /// <summary>
        /// Ровно один объект по spec: <c>Root/Child/Leaf</c>, <c>Сцена:Root/Child</c> (имя сцены или путь .unity),
        /// <c>#число</c> (EntityId, как печатает go.info) или имя без '/', если оно уникально.
        /// Иначе — <see cref="NexusCommandException"/> (не найден / неоднозначно, с кандидатами).
        /// </summary>
        public static GameObject Resolve(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
                throw new NexusCommandException("нужен объект: путь Root/Child, Сцена:Root/Child, имя или #id (go.find)");

            var s = spec.Trim();
            if (s[0] == '#') return ResolveId(s);

            SplitScene(s, out var scenes, out var path);
            var roots = SearchRoots(scenes);
            var matches = new List<GameObject>();
            var trimmed = path.Trim('/');
            if (trimmed.Length == 0) throw new NexusCommandException($"«{spec}»: пустой путь объекта");

            if (path.IndexOf('/') >= 0)
            {
                var segments = trimmed.Split('/');
                CollectByPath(roots, segments, StringComparison.Ordinal, matches);
                if (matches.Count == 0) CollectByPath(roots, segments, StringComparison.OrdinalIgnoreCase, matches);

                // в Prefab Stage путь можно писать без имени корня префаба
                var stage = UnityEdit.Stage;
                if (matches.Count == 0 && scenes == null && stage != null && stage.prefabContentsRoot != null)
                {
                    var children = new List<GameObject>();
                    foreach (Transform c in stage.prefabContentsRoot.transform) children.Add(c.gameObject);
                    CollectByPath(children, segments, StringComparison.OrdinalIgnoreCase, matches);
                }
            }
            else
            {
                CollectByName(roots, trimmed, StringComparison.Ordinal, matches);
                if (matches.Count == 0) CollectByName(roots, trimmed, StringComparison.OrdinalIgnoreCase, matches);
            }

            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0)
            {
                var leaf = trimmed.Substring(trimmed.LastIndexOf('/') + 1);
                throw new NexusCommandException($"Объект «{spec}» не найден{Where(scenes)}. Поиск: go.find \"*{leaf}*\"", NexusCommandContext.Exit.Failed);
            }

            throw new NexusCommandException(Ambiguous(spec, matches), NexusCommandContext.Exit.Failed);
        }

        /// <summary>
        /// Все объекты, подходящие под glob (<c>*</c>, <c>?</c>, без учёта регистра): по имени или, если в шаблоне
        /// есть '/', по пути от корня (без префикса сцены). Порядок — как в иерархии.
        /// </summary>
        public static List<GameObject> FindAll(string pattern, string scene = null, bool includeInactive = true)
        {
            var p = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
            List<Scene> scenes = null;
            if (!string.IsNullOrWhiteSpace(scene)) scenes = new List<Scene> { UnityEdit.ResolveScene(scene) };

            var byPath = p.IndexOf('/') >= 0;
            if (byPath) p = p.Trim('/');
            var regex = Glob(p);
            var result = new List<GameObject>();
            var stack = new List<GameObject>();
            foreach (var root in SearchRoots(scenes)) Walk(root, stack);

            foreach (var go in stack)
            {
                if (!includeInactive && !go.activeInHierarchy) continue;
                var subject = byPath ? RelativePath(go) : go.name;
                if (regex.IsMatch(subject)) result.Add(go);
            }

            return result;
        }

        /// <summary>
        /// <c>Сцена:Root/Child</c>; без сцены, если загружена одна сцена или объект в открытом Prefab Stage
        /// (там путь — от корня префаба). Объект ассета — <c>Assets/…prefab:Root/Child</c>.
        /// </summary>
        public static string PathOf(GameObject go)
        {
            if (go == null) return "(нет)";
            if (EditorUtility.IsPersistent(go)) return AssetDatabase.GetAssetPath(go) + ":" + RelativePath(go);

            var relative = RelativePath(go);
            if (UnityEdit.InStage(go)) return relative;
            if (!go.scene.IsValid()) return relative;

            var stageOpen = UnityEdit.Stage != null;
            if (!stageOpen && LoadedSceneCount() <= 1) return relative;
            var name = string.IsNullOrEmpty(go.scene.name) ? go.scene.path : go.scene.name;
            if (string.IsNullOrEmpty(name)) return relative;

            // имя сцены не уникально среди загруженных — пишем путь .unity
            if (FindLoadedScenes(name).Count > 1 && !string.IsNullOrEmpty(go.scene.path)) name = go.scene.path;
            return name + ":" + relative;
        }

        /// <summary><c>#&lt;ulong&gt;</c> — EntityId объекта (живёт до перезагрузки сцены или домена).</summary>
        public static string IdOf(GameObject go)
        {
            if (go == null) return "#0";
            return "#" + EntityId.ToULong(go.GetEntityId()).ToString(CultureInfo.InvariantCulture);
        }

        // ================================================================ компоненты

        /// <summary>
        /// Компонент на объекте: тип по короткому или полному имени (<c>Rigidbody</c>, <c>UnityEngine.Rigidbody</c>,
        /// <c>MyGame.Health</c>); <c>Type[1]</c> или <paramref name="index"/> — n-й компонент этого типа (с наследниками);
        /// просто число — n-й компонент объекта, как в comp.list.
        /// </summary>
        public static Component ResolveComponent(GameObject go, string typeSpec, int index = 0)
        {
            if (go == null) throw new ArgumentNullException(nameof(go));
            if (string.IsNullOrWhiteSpace(typeSpec)) throw new NexusCommandException("нужен тип компонента: Rigidbody, MyGame.Health, Health[1] (comp.list)");

            var spec = typeSpec.Trim();
            if (int.TryParse(spec, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
            {
                var all = go.GetComponents<Component>();
                if (ordinal < 0 || ordinal >= all.Length)
                    throw new NexusCommandException($"У «{go.name}» нет компонента №{ordinal} (всего {all.Length}): {ListComponents(go)}", NexusCommandContext.Exit.Failed);
                if (all[ordinal] == null)
                    throw new NexusCommandException($"Компонент №{ordinal} у «{go.name}» — Missing Script (скрипт удалён или не скомпилирован).", NexusCommandContext.Exit.Failed);
                return all[ordinal];
            }

            var name = ParseIndex(spec, ref index);
            var type = ResolveComponentType(name);
            var comps = go.GetComponents(type);
            var live = new List<Component>();
            foreach (var c in comps)
                if (c != null) live.Add(c);

            if (live.Count == 0)
                throw new NexusCommandException($"У «{PathOf(go)}» нет {type.Name}. Есть: {ListComponents(go)}", NexusCommandContext.Exit.Failed);
            if (index < 0 || index >= live.Count)
                throw new NexusCommandException($"У «{PathOf(go)}» {live.Count} × {type.Name} — индекс {index} вне диапазона (0–{live.Count - 1}).", NexusCommandContext.Exit.Failed);
            return live[index];
        }

        /// <summary>Тип компонента по полному или короткому имени (сначала точное совпадение, потом без регистра).</summary>
        public static Type ResolveComponentType(string typeSpec)
        {
            if (string.IsNullOrWhiteSpace(typeSpec)) throw new NexusCommandException("нужен тип компонента");
            var dummy = 0;
            var name = ParseIndex(typeSpec.Trim(), ref dummy);
            var types = TypeCache.GetTypesDerivedFrom<Component>();

            var found = Match(types, name, true, StringComparison.Ordinal);
            if (found.Count == 0) found = Match(types, name, true, StringComparison.OrdinalIgnoreCase);
            if (found.Count == 0) found = Match(types, name, false, StringComparison.Ordinal);
            if (found.Count == 0) found = Match(types, name, false, StringComparison.OrdinalIgnoreCase);

            if (found.Count == 1) return found[0];
            if (found.Count == 0)
                throw new NexusCommandException($"Тип компонента «{name}» не найден (не скомпилирован, опечатка или не Component).", NexusCommandContext.Exit.Failed);

            var names = new List<string>();
            for (var i = 0; i < found.Count && i < MaxCandidates; i++) names.Add(found[i].FullName);
            throw new NexusCommandException($"«{name}» — несколько типов: {string.Join(", ", names)} — укажите полное имя.", NexusCommandContext.Exit.Failed);
        }

        // ================================================================ для команд сборки

        /// <summary>Как адресовать компонент в командах: <c>Type</c> или <c>Type[n]</c> (короткое имя, если оно однозначно).</summary>
        internal static string ComponentSpec(Component c)
        {
            if (c == null) return "(Missing Script)";
            var type = c.GetType();
            var same = c.gameObject.GetComponents(type);
            var index = 0;
            var count = 0;
            foreach (var s in same)
            {
                if (s == null) continue;
                if (s == c) index = count;
                count++;
            }

            var name = TypeSpec(type);
            return count > 1 ? $"{name}[{index}]" : name;
        }

        /// <summary>Короткое имя типа, если по нему тип находится однозначно, иначе полное.</summary>
        internal static string TypeSpec(Type type)
        {
            var found = Match(TypeCache.GetTypesDerivedFrom<Component>(), type.Name, false, StringComparison.Ordinal);
            return found.Count == 1 && found[0] == type ? type.Name : type.FullName;
        }

        internal static string ListComponents(GameObject go)
        {
            var parts = new List<string>();
            var all = go.GetComponents<Component>();
            for (var i = 0; i < all.Length; i++) parts.Add(all[i] == null ? $"{i}:(Missing Script)" : $"{i}:{ComponentSpec(all[i])}");
            return string.Join(", ", parts);
        }

        /// <summary>Загруженные основные сцены по имени или пути .unity.</summary>
        internal static List<Scene> FindLoadedScenes(string spec)
        {
            var list = new List<Scene>();
            var q = (spec ?? string.Empty).Trim().Replace('\\', '/');
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                if (string.Equals(s.path, q, StringComparison.OrdinalIgnoreCase) || string.Equals(s.name, q, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(s.path) && s.path.EndsWith("/" + q, StringComparison.OrdinalIgnoreCase)))
                    list.Add(s);
            }

            return list;
        }

        internal static int LoadedSceneCount()
        {
            var n = 0;
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isLoaded) n++;
            return n;
        }

        /// <summary>
        /// Корни поиска: явные сцены; иначе — корень открытого префаба; иначе — все загруженные сцены.
        /// </summary>
        internal static List<GameObject> SearchRoots(List<Scene> scenes)
        {
            var roots = new List<GameObject>();
            if (scenes == null)
            {
                var stage = UnityEdit.Stage;
                if (stage != null)
                {
                    if (stage.prefabContentsRoot != null) roots.Add(stage.prefabContentsRoot);
                    return roots;
                }

                scenes = new List<Scene>();
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded) scenes.Add(s);
                }
            }

            foreach (var s in scenes)
                if (s.IsValid() && s.isLoaded)
                    roots.AddRange(s.GetRootGameObjects());
            return roots;
        }

        /// <summary>Объект и все потомки в порядке иерархии.</summary>
        internal static void Walk(GameObject go, List<GameObject> into)
        {
            into.Add(go);
            var t = go.transform;
            for (var i = 0; i < t.childCount; i++) Walk(t.GetChild(i).gameObject, into);
        }

        /// <summary>Путь от корня сцены (в Prefab Stage — от корня префаба), без сцены.</summary>
        internal static string RelativePath(GameObject go)
        {
            var stage = UnityEdit.Stage;
            var stop = stage != null && go.scene == stage.scene && stage.prefabContentsRoot != null ? stage.prefabContentsRoot.transform : null;
            var sb = new StringBuilder(go.name);
            var t = go.transform;
            while (t != stop && t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, '/').Insert(0, t.name);
                if (t == stop) break;
            }

            return sb.ToString();
        }

        // ================================================================ внутреннее

        private static GameObject ResolveId(string s)
        {
            if (!ulong.TryParse(s.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw) || raw == 0)
                throw new NexusCommandException($"«{s}»: id — # и число, как печатают go.find / go.info");

            var obj = EditorUtility.EntityIdToObject(EntityId.FromULong(raw));
            GameObject go = null;
            if (obj is GameObject g) go = g;
            else if (obj is Component c) go = c.gameObject;

            if (go == null)
                throw new NexusCommandException($"Объект {s} не найден: id живёт до перезагрузки сцены или домена — найдите заново: go.find", NexusCommandContext.Exit.Failed);
            if (EditorUtility.IsPersistent(go))
                throw new NexusCommandException($"{s} — объект ассета ({AssetDatabase.GetAssetPath(go)}), а не сцены: откройте префаб (prefab.open).", NexusCommandContext.Exit.Failed);
            return go;
        }

        /// <summary><c>Сцена:путь</c> — префикс считается сценой, только если такая сцена загружена.</summary>
        private static void SplitScene(string s, out List<Scene> scenes, out string path)
        {
            scenes = null;
            path = s;
            var colon = s.IndexOf(':');
            if (colon <= 0) return;

            var candidate = s.Substring(0, colon);
            var found = FindLoadedScenes(candidate);
            if (found.Count == 0)
            {
                if (candidate.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    throw new NexusCommandException($"Сцена «{candidate}» не загружена (scene.open {candidate} --additive).", NexusCommandContext.Exit.Failed);
                return;
            }

            if (found.Count > 1)
            {
                var paths = new List<string>();
                foreach (var f in found) paths.Add(f.path);
                throw new NexusCommandException($"«{candidate}» — несколько загруженных сцен: {string.Join(", ", paths)} — укажите путь .unity.", NexusCommandContext.Exit.Failed);
            }

            scenes = found;
            path = s.Substring(colon + 1);
        }

        private static void CollectByPath(List<GameObject> roots, string[] segments, StringComparison cmp, List<GameObject> into)
        {
            var level = new List<GameObject>();
            foreach (var r in roots)
                if (string.Equals(r.name, segments[0], cmp)) level.Add(r);

            for (var i = 1; i < segments.Length && level.Count > 0; i++)
            {
                var next = new List<GameObject>();
                foreach (var go in level)
                {
                    var t = go.transform;
                    for (var c = 0; c < t.childCount; c++)
                    {
                        var child = t.GetChild(c);
                        if (string.Equals(child.name, segments[i], cmp)) next.Add(child.gameObject);
                    }
                }

                level = next;
            }

            into.AddRange(level);
        }

        private static void CollectByName(List<GameObject> roots, string name, StringComparison cmp, List<GameObject> into)
        {
            var all = new List<GameObject>();
            foreach (var r in roots) Walk(r, all);
            foreach (var go in all)
                if (string.Equals(go.name, name, cmp)) into.Add(go);
        }

        private static string Ambiguous(string spec, List<GameObject> matches)
        {
            var sb = new StringBuilder();
            sb.Append('«').Append(spec).Append("» — ").Append(matches.Count).Append(" объектов, уточните путём или #id:");
            for (var i = 0; i < matches.Count && i < MaxCandidates; i++)
                sb.Append("\n  ").Append(PathOf(matches[i])).Append("  ").Append(IdOf(matches[i]));
            if (matches.Count > MaxCandidates) sb.Append("\n  … ещё ").Append(matches.Count - MaxCandidates);
            return sb.ToString();
        }

        private static string Where(List<Scene> scenes)
        {
            if (scenes != null && scenes.Count == 1) return $" в сцене {scenes[0].name}";
            return UnityEdit.Stage != null ? " в открытом префабе (основные сцены — через Сцена:путь)" : string.Empty;
        }

        private static Regex Glob(string pattern)
        {
            var sb = new StringBuilder("^");
            foreach (var ch in pattern)
            {
                if (ch == '*') sb.Append(".*");
                else if (ch == '?') sb.Append('.');
                else sb.Append(Regex.Escape(ch.ToString()));
            }

            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }

        /// <summary><c>Type[2]</c> → «Type», индекс 2; без скобок индекс не меняется.</summary>
        private static string ParseIndex(string spec, ref int index)
        {
            var open = spec.LastIndexOf('[');
            if (open > 0 && spec.EndsWith("]", StringComparison.Ordinal))
            {
                var inner = spec.Substring(open + 1, spec.Length - open - 2);
                if (!int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
                    throw new NexusCommandException($"«{spec}»: индекс компонента — целое ≥ 0: Type[1]");
                index = n;
                return spec.Substring(0, open).Trim();
            }

            return spec;
        }

        private static List<Type> Match(TypeCache.TypeCollection types, string name, bool full, StringComparison cmp)
        {
            var list = new List<Type>();
            foreach (var t in types)
            {
                if (t.ContainsGenericParameters) continue;
                if (full)
                {
                    var fn = t.FullName;
                    if (fn == null) continue;
                    if (string.Equals(fn, name, cmp) || string.Equals(fn.Replace('+', '.'), name, cmp)) list.Add(t);
                }
                else if (string.Equals(t.Name, name, cmp))
                {
                    list.Add(t);
                }
            }

            return list;
        }
    }
}
