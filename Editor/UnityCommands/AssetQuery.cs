using System;
using System.Collections.Generic;
using System.IO;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Адресация ассетов для команд CLI (spec): путь <c>Assets/…</c> или <c>Packages/…</c>; guid (32 hex) или
    /// <c>guid:&lt;hex&gt;</c>; <c>путь:ИмяПодассета</c> — подассет по имени (спрайт в png); имя без '/' — поиск по
    /// проекту, только если совпадение ровно одно. Чистые функции без состояния.
    /// </summary>
    public static class AssetQuery
    {
        private const int MaxCandidates = 10;

        /// <summary>
        /// Найти ассет по spec. <paramref name="type"/> != null — нужен объект этого типа: главный ассет или подассет
        /// (если главный другого типа). Не нашлось или неоднозначно — <see cref="NexusCommandException"/> (код 1).
        /// </summary>
        public static Object Resolve(string spec, Type type = null)
        {
            if (string.IsNullOrWhiteSpace(spec)) throw new NexusCommandException("нужен ассет: путь, guid или имя");
            var s = spec.Trim().Replace('\\', '/');

            string path;
            string sub = null;

            if (s.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                var body = s.Substring(5);
                SplitSub(ref body, out sub);
                path = PathFromGuid(body, spec);
            }
            else if (IsGuid(s))
            {
                path = PathFromGuid(s, spec);
            }
            else if (s.IndexOf('/') < 0)
            {
                // имя без пути: «Hero» или «Hero:Idle_0»
                var name = s;
                SplitSub(ref name, out sub);
                path = FindByName(name, type, spec);
            }
            else
            {
                path = s;
                // «путь:Подассет» — ':' в путях Unity не встречается (Windows его запрещает)
                var colon = path.LastIndexOf(':');
                if (colon > 0)
                {
                    sub = path.Substring(colon + 1);
                    path = path.Substring(0, colon);
                }

                if (!Exists(path))
                {
                    // путь от Assets без префикса: «Game/Hero.prefab»
                    if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal) && Exists("Assets/" + path))
                        path = "Assets/" + path;
                    else
                        throw new NexusCommandException($"Нет ассета «{path}» (assets.find).", NexusCommandContext.Exit.Failed);
                }
            }

            return Load(path, sub, type, spec);
        }

        /// <summary>
        /// Адрес объекта в форме spec: путь главного ассета; для подассета — <c>путь:Имя</c> (принимается
        /// <see cref="Resolve"/>). Не ассет (объект сцены, null) — пустая строка.
        /// </summary>
        public static string PathOf(Object obj)
        {
            if (obj == null) return string.Empty;
            var path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return AssetDatabase.IsMainAsset(obj) ? path : path + ":" + obj.name;
        }

        // ---------------------------------------------------------------- внутреннее

        /// <summary>Путь главного ассета spec без загрузки подассета: для команд, работающих с файлом.</summary>
        internal static string ResolveMainPath(string spec)
        {
            var obj = Resolve(spec);
            var path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) throw new NexusCommandException($"«{spec}» — не ассет.", NexusCommandContext.Exit.Failed);
            return path;
        }

        internal static bool Exists(string path) =>
            !string.IsNullOrEmpty(path) && (AssetDatabase.IsValidFolder(path) || !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets)));

        internal static bool IsGuid(string s)
        {
            if (s == null || s.Length != 32) return false;
            foreach (var c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static void SplitSub(ref string body, out string sub)
        {
            sub = null;
            var colon = body.IndexOf(':');
            if (colon <= 0) return;
            sub = body.Substring(colon + 1);
            body = body.Substring(0, colon);
        }

        private static string PathFromGuid(string guid, string spec)
        {
            if (!IsGuid(guid)) throw new NexusCommandException($"«{spec}»: guid — 32 шестнадцатеричных символа.");
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !Exists(path)) throw new NexusCommandException($"Нет ассета с guid {guid}.", NexusCommandContext.Exit.Failed);
            return path;
        }

        private static string FindByName(string name, Type type, string spec)
        {
            var filter = name;
            if (type != null && type != typeof(Object))
            {
                // компоненты и GameObject в ассетах живут в префабах (и моделях): поиск по t:Component не находит их
                var typeFilter = typeof(Component).IsAssignableFrom(type) || type == typeof(GameObject) ? "t:GameObject" : "t:" + type.Name;
                filter = typeFilter + " " + name;
            }

            var exact = new List<string>();
            var loose = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets(filter))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(p) || exact.Contains(p) || loose.Contains(p)) continue;
                var file = Path.GetFileName(p);
                if (string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(file, name, StringComparison.OrdinalIgnoreCase))
                    exact.Add(p);
                else
                    loose.Add(p);
            }

            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) throw Ambiguous(spec, exact);
            if (loose.Count == 0) throw new NexusCommandException($"Ассет «{name}» не найден{(type != null ? " (тип " + type.Name + ")" : string.Empty)} (assets.find).", NexusCommandContext.Exit.Failed);
            throw new NexusCommandException($"Ассет с именем «{name}» не найден; похожие: {Join(loose)} — укажите путь.", NexusCommandContext.Exit.Failed);
        }

        private static NexusCommandException Ambiguous(string spec, List<string> paths) =>
            new NexusCommandException($"«{spec}» подходит к {paths.Count} ассетам: {Join(paths)} — укажите путь или guid.", NexusCommandContext.Exit.Failed);

        private static string Join(List<string> paths)
        {
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            var shown = paths.Count > MaxCandidates ? paths.GetRange(0, MaxCandidates) : paths;
            return string.Join(", ", shown) + (paths.Count > MaxCandidates ? $" … ещё {paths.Count - MaxCandidates}" : string.Empty);
        }

        private static Object Load(string path, string sub, Type type, string spec)
        {
            if (!string.IsNullOrEmpty(sub))
            {
                var names = new List<string>();
                Object found = null;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (o == null) continue;
                    if (!names.Contains(o.name)) names.Add(o.name);
                    if (!string.Equals(o.name, sub, StringComparison.Ordinal)) continue;
                    if (type != null && !type.IsInstanceOfType(o)) continue;
                    // при одинаковых именах предпочесть подассет главному (png и его спрайт зовутся одинаково)
                    if (found == null || AssetDatabase.IsMainAsset(found)) found = o;
                }

                if (found != null) return found;
                throw new NexusCommandException($"В «{path}» нет подассета «{sub}»{(type != null ? " типа " + type.Name : string.Empty)}; есть: {Join(names)}.", NexusCommandContext.Exit.Failed);
            }

            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null) throw new NexusCommandException($"Ассет «{path}» не загружается.", NexusCommandContext.Exit.Failed);
            if (type == null || type.IsInstanceOfType(main)) return main;

            Object match = null;
            var count = 0;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null || !type.IsInstanceOfType(o)) continue;
                if (match == null) match = o;
                count++;
            }

            if (match == null) throw new NexusCommandException($"В «{path}» нет объекта типа {type.Name} (главный — {main.GetType().Name}).", NexusCommandContext.Exit.Failed);
            if (count > 1 && type != typeof(Object)) NexusDiagnostics.Trace("AssetQuery", $"{spec}: {count} объектов типа {type.Name}, взят первый ({match.name})");
            return match;
        }
    }
}
