using System;
using System.Collections.Generic;
using System.Globalization;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Общее для команд правки сцены: проверка «можно ли менять сейчас», группы Undo, пометка сцен,
    /// разбор чисел и векторов. Без состояния.
    /// </summary>
    internal static class UnityEdit
    {
        internal static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- можно ли менять

        /// <summary>
        /// Перед правкой объектов сцены: нет меняющего задания, компиляции и импорта; в Play — только с <c>--play</c>
        /// (правка как в инспекторе: пропадёт при выходе из Play).
        /// </summary>
        internal static void Begin(NexusCommandContext ctx)
        {
            BeginAsset(ctx);
            if (EditorApplication.isPlaying && !ctx.Flag("play"))
                throw new NexusCommandException("Идёт Play: правки объектов сцены пропадут при выходе. Добавьте --play (как правка в инспекторе) или остановите: play.stop", NexusCommandContext.Exit.Failed);
        }

        /// <summary>Перед правкой ассетов (префабы): нет меняющего задания, компиляции, импорта и смены режима Play.</summary>
        internal static void BeginAsset(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new NexusCommandException("Редактор компилирует или импортирует ассеты — дождитесь: refresh", NexusCommandContext.Exit.Failed);
            if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
                throw new NexusCommandException("Редактор переключает режим Play — повторите, когда он закончит (status).", NexusCommandContext.Exit.Failed);
        }

        internal static void NotInPlay(string what)
        {
            if (EditorApplication.isPlaying)
                throw new NexusCommandException($"В Play {what} не выполняется — остановите: play.stop", NexusCommandContext.Exit.Failed);
        }

        // ---------------------------------------------------------------- Undo и сцены

        /// <summary>Новая группа Undo: всё до <see cref="EndGroup"/> отменяется одним Ctrl+Z.</summary>
        internal static int BeginGroup(string name)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            return Undo.GetCurrentGroup();
        }

        internal static void EndGroup(int group) => Undo.CollapseUndoOperations(group);

        internal static PrefabStage Stage => PrefabStageUtility.GetCurrentPrefabStage();

        /// <summary>Объект лежит в открытом Prefab Stage.</summary>
        internal static bool InStage(GameObject go)
        {
            var stage = Stage;
            return stage != null && go != null && go.scene == stage.scene;
        }

        internal static void MarkDirty(GameObject go)
        {
            if (go != null) MarkDirty(go.scene);
        }

        /// <summary>Сцена изменена (сохранение — отдельной командой save / prefab.save). В Play обычные сцены не помечаются.</summary>
        internal static void MarkDirty(Scene scene)
        {
            if (!scene.IsValid()) return;
            var stage = Stage;
            var isStage = stage != null && scene == stage.scene;
            if (EditorApplication.isPlaying && !isStage) return;
            EditorSceneManager.MarkSceneDirty(scene);
        }

        /// <summary>Загруженная сцена по имени или пути .unity (основная сцена, не Prefab Stage).</summary>
        internal static Scene ResolveScene(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) throw new NexusCommandException("нужна сцена: имя или путь .unity");
            var found = UnityObjectQuery.FindLoadedScenes(spec.Trim());
            if (found.Count == 1) return found[0];
            if (found.Count == 0)
                throw new NexusCommandException($"Сцена «{spec}» не загружена (открытые: scene.list; открыть: scene.open {spec} --additive).", NexusCommandContext.Exit.Failed);
            var paths = new List<string>();
            foreach (var s in found) paths.Add(s.path);
            throw new NexusCommandException($"«{spec}» — несколько загруженных сцен: {string.Join(", ", paths)} — укажите путь.", NexusCommandContext.Exit.Failed);
        }

        // ---------------------------------------------------------------- аргументы

        /// <summary>Все значения повторяемой опции: <c>--set a=1 --set b=2</c> (разбор контекста хранит только последнее).</summary>
        internal static List<string> Repeated(NexusCommandContext ctx, string key)
        {
            var list = new List<string>();
            var raw = ctx.Raw;
            var plain = "--" + key;
            var withEq = plain + "=";
            for (var i = 0; i < raw.Count; i++)
            {
                var a = raw[i];
                if (a == null) continue;
                if (a.StartsWith(withEq, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(a.Substring(withEq.Length));
                    continue;
                }

                if (string.Equals(a, plain, StringComparison.OrdinalIgnoreCase) && i + 1 < raw.Count && raw[i + 1] != null &&
                    !raw[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    list.Add(raw[i + 1]);
                    i++;
                }
            }

            return list;
        }

        /// <summary>Позиционные аргументы начиная с номера.</summary>
        internal static List<string> PositionalFrom(NexusCommandContext ctx, int start)
        {
            var list = new List<string>();
            for (var i = start; i < ctx.Positional.Count; i++) list.Add(ctx.Positional[i]);
            return list;
        }

        internal static int Max(NexusCommandContext ctx, string key, int fallback) => Math.Max(1, ctx.GetInt(key, fallback));

        internal static bool ParseBool(string value, string what)
        {
            var v = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (v)
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
            }

            throw new NexusCommandException($"{what}: ожидается true или false — «{value}»");
        }

        internal static bool IsBoolText(string value)
        {
            var v = (value ?? string.Empty).Trim().ToLowerInvariant();
            return v == "true" || v == "false" || v == "1" || v == "0" || v == "yes" || v == "no" || v == "on" || v == "off";
        }

        /// <summary>Числа через запятую (или пробелы): <c>1,2,3</c>, <c>1, 2, 3</c>, <c>1 2 3</c>.</summary>
        internal static double[] Numbers(string value, int min, int max, string what)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.StartsWith("(", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal)) text = text.Substring(1, text.Length - 2);
            var parts = text.IndexOf(',') >= 0 || text.IndexOf(';') >= 0
                ? text.Split(new[] { ',', ';' }, StringSplitOptions.None)
                : text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (text.Length == 0 || parts.Length < min || parts.Length > max)
                throw new NexusCommandException($"{what}: ожидается {Count(min, max)} через запятую — «{value}»");
            var result = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, Inv, out result[i]))
                    throw new NexusCommandException($"{what}: «{parts[i].Trim()}» — не число (дробная часть через точку)");
            return result;
        }

        internal static int[] Ints(string value, int min, int max, string what)
        {
            var d = Numbers(value, min, max, what);
            var r = new int[d.Length];
            for (var i = 0; i < d.Length; i++)
            {
                if (d[i] != Math.Floor(d[i]) || d[i] < int.MinValue || d[i] > int.MaxValue)
                    throw new NexusCommandException($"{what}: ожидаются целые — «{value}»");
                r[i] = (int)d[i];
            }

            return r;
        }

        private static string Count(int min, int max) => min == max ? $"{min} чис." : $"{min}–{max} чис.";

        internal static Vector3 Vector3Of(string value, string what)
        {
            var n = Numbers(value, 3, 3, what);
            return new Vector3((float)n[0], (float)n[1], (float)n[2]);
        }

        internal static Vector2 Vector2Of(string value, string what)
        {
            var n = Numbers(value, 2, 2, what);
            return new Vector2((float)n[0], (float)n[1]);
        }

        internal static Vector3? OptionalVector3(NexusCommandContext ctx, string key)
        {
            var v = ctx.Get(key);
            if (v == null)
            {
                if (ctx.Has(key)) throw new NexusCommandException($"--{key}: нужно значение x,y,z");
                return null;
            }

            return Vector3Of(v, "--" + key);
        }

        // ---------------------------------------------------------------- вывод

        /// <summary>Число для человека: до 3 знаков после точки.</summary>
        internal static string Short(float f) => f.ToString("0.###", Inv);

        internal static string Short(Vector3 v) => Short(v.x) + "," + Short(v.y) + "," + Short(v.z);

        internal static string Short(Vector2 v) => Short(v.x) + "," + Short(v.y);

        internal static float[] Arr(Vector3 v) => new[] { v.x, v.y, v.z };

        internal static float[] Arr(Vector2 v) => new[] { v.x, v.y };

        // ---------------------------------------------------------------- теги и слои

        internal static string CheckTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) throw new NexusCommandException("--tag: нужно имя тега");
            foreach (var t in UnityEditorInternal.InternalEditorUtility.tags)
                if (t == tag) return t;
            throw new NexusCommandException($"Тега «{tag}» нет — добавьте: tag.add {tag} (или Project Settings > Tags and Layers).", NexusCommandContext.Exit.Failed);
        }

        internal static int LayerOf(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) throw new NexusCommandException("--layer: нужно имя или номер слоя");
            var s = spec.Trim();
            if (int.TryParse(s, NumberStyles.Integer, Inv, out var n))
            {
                if (n < 0 || n > 31) throw new NexusCommandException($"Слой {n}: номер 0–31");
                return n;
            }

            var layer = LayerMask.NameToLayer(s);
            if (layer < 0) throw new NexusCommandException($"Слоя «{s}» нет — добавьте: layer.add {s} (или Project Settings > Tags and Layers).", NexusCommandContext.Exit.Failed);
            return layer;
        }

        internal static string LayerLabel(int layer)
        {
            var name = LayerMask.LayerToName(layer);
            return string.IsNullOrEmpty(name) ? layer.ToString(Inv) : name;
        }

        // ---------------------------------------------------------------- transform

        /// <summary>Положение, поворот (эйлер) и масштаб: локально или в мире. Undo записывает вызывающий.</summary>
        internal static void ApplyTransform(Transform t, Vector3? pos, Vector3? rot, Vector3? scale, bool world)
        {
            if (world)
            {
                if (pos.HasValue) t.position = pos.Value;
                if (rot.HasValue) t.rotation = Quaternion.Euler(rot.Value);
                if (scale.HasValue)
                {
                    // мировой масштаб задаётся через локальный: делим на масштаб родителя (без учёта перекоса)
                    var p = t.parent != null ? t.parent.lossyScale : Vector3.one;
                    var s = scale.Value;
                    t.localScale = new Vector3(Div(s.x, p.x), Div(s.y, p.y), Div(s.z, p.z));
                }

                return;
            }

            if (pos.HasValue) t.localPosition = pos.Value;
            if (rot.HasValue) t.localRotation = Quaternion.Euler(rot.Value);
            if (scale.HasValue) t.localScale = scale.Value;
        }

        private static float Div(float a, float b) => Mathf.Approximately(b, 0f) ? a : a / b;
    }
}
