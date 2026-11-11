using System;
using System.Collections.Generic;
using System.Globalization;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Теги, слои и слои сортировки: через SerializedObject на ProjectSettings/TagManager.asset (с Undo), после
    /// изменения настройки проекта сохраняются.
    /// </summary>
    internal static class TagLayerCommands
    {
        private const string TagManagerPath = "ProjectSettings/TagManager.asset";

        /// <summary>Встроенные теги Unity: их нет в массиве tags и их не удалить.</summary>
        private static readonly string[] BuiltinTags = { "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController" };

        /// <summary>Встроенные слои (остальные из 0…7 — пользовательские 3, 6, 7).</summary>
        private static readonly int[] BuiltinLayers = { 0, 1, 2, 4, 5 };

        // ================================================================ теги

        [NexusCommand("tag.list", "Теги проекта (встроенные помечены)", Usage = "tag.list", ReadOnly = true)]
        private static int TagList(NexusCommandContext ctx)
        {
            var data = new List<object>();
            foreach (var t in InternalEditorUtility.tags)
            {
                var builtin = Array.IndexOf(BuiltinTags, t) >= 0;
                ctx.Print(builtin ? t + "  (встроенный)" : t);
                data.Add(new { name = t, builtin });
            }

            ctx.Data = data;
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("tag.add", "Добавить тег", Usage = "tag.add <имя>")]
        private static int TagAdd(NexusCommandContext ctx)
        {
            var name = Name(ctx, "тега");
            if (Array.IndexOf(InternalEditorUtility.tags, name) >= 0)
            {
                ctx.Print("тег уже есть: " + name);
                ctx.Data = new { name, added = false };
                return NexusCommandContext.Exit.Ok;
            }

            NexusEditorCommands.Guard(ctx);
            var so = TagManager();
            var tags = Property(so, "tags");
            var at = tags.arraySize;
            tags.InsertArrayElementAtIndex(at);
            tags.GetArrayElementAtIndex(at).stringValue = name;
            Commit(so);
            ctx.Print("✓ тег добавлен: " + name);
            ctx.Data = new { name, added = true };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("tag.remove", "Удалить тег (объекты с ним останутся с неизвестным тегом); без --yes — только показать",
            Usage = "tag.remove <имя> [--yes]")]
        private static int TagRemove(NexusCommandContext ctx)
        {
            var name = Name(ctx, "тега");
            if (Array.IndexOf(BuiltinTags, name) >= 0) throw new NexusCommandException($"«{name}» — встроенный тег, не удаляется.", NexusCommandContext.Exit.Failed);

            var so = TagManager();
            var tags = Property(so, "tags");
            var index = -1;
            for (var i = 0; i < tags.arraySize; i++)
                if (tags.GetArrayElementAtIndex(i).stringValue == name) index = i;
            if (index < 0) throw new NexusCommandException($"Тега «{name}» нет (tag.list).", NexusCommandContext.Exit.Failed);

            if (!ctx.Flag("yes"))
            {
                ctx.Print($"Будет удалён тег «{name}». Объекты сцен и префабов с ним не проверяются и останутся с неизвестным тегом.");
                ctx.Print("Удалить: добавьте --yes");
                ctx.Data = new { name, dryRun = true };
                return NexusCommandContext.Exit.Ok;
            }

            NexusEditorCommands.Guard(ctx);
            tags.DeleteArrayElementAtIndex(index);
            Commit(so);
            ctx.Print("✓ тег удалён: " + name);
            ctx.Data = new { name, dryRun = false };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ слои

        [NexusCommand("layer.list", "Слои 0…31 (пустые пользовательские — «свободен»)", Usage = "layer.list [--all]", ReadOnly = true)]
        private static int LayerList(NexusCommandContext ctx)
        {
            var all = ctx.Flag("all");
            var data = new List<object>();
            var free = 0;
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                var builtin = Array.IndexOf(BuiltinLayers, i) >= 0;
                if (string.IsNullOrEmpty(name))
                {
                    free++;
                    if (all) ctx.Print($"  {i,2} (свободен)");
                }
                else
                {
                    ctx.Print($"  {i,2} {name}{(builtin ? "  (встроенный)" : string.Empty)}");
                }

                data.Add(new { index = i, name, builtin });
            }

            if (!all) ctx.Print($"свободных: {free} (--all — показать)");
            ctx.Data = data;
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("layer.add", "Добавить слой: в первый свободный пользовательский (8…31, затем 3, 6, 7) или в --index N",
            Usage = "layer.add <имя> [--index N]")]
        private static int LayerAdd(NexusCommandContext ctx)
        {
            var name = Name(ctx, "слоя");
            var existing = LayerMask.NameToLayer(name);
            if (existing >= 0)
            {
                ctx.Print($"слой уже есть: {existing} {name}");
                ctx.Data = new { name, index = existing, added = false };
                return NexusCommandContext.Exit.Ok;
            }

            var so = TagManager();
            var layers = Property(so, "layers");
            int index;
            if (ctx.Has("index"))
            {
                index = ctx.GetInt("index", -1);
                if (index < 0 || index > 31 || Array.IndexOf(BuiltinLayers, index) >= 0) throw new NexusCommandException("--index: пользовательский слой 3, 6, 7 или 8…31");
                var current = layers.GetArrayElementAtIndex(index).stringValue;
                if (!string.IsNullOrEmpty(current)) throw new NexusCommandException($"Слой {index} занят: {current}.", NexusCommandContext.Exit.Failed);
            }
            else
            {
                index = FirstFree(layers);
                if (index < 0) throw new NexusCommandException("Свободных пользовательских слоёв нет.", NexusCommandContext.Exit.Failed);
            }

            NexusEditorCommands.Guard(ctx);
            layers.GetArrayElementAtIndex(index).stringValue = name;
            Commit(so);
            ctx.Print($"✓ слой {index}: {name}");
            ctx.Data = new { name, index, added = true };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("layer.remove", "Освободить пользовательский слой (объекты сохранят номер слоя без имени); без --yes — только показать",
            Usage = "layer.remove <имя|номер> [--yes]")]
        private static int LayerRemove(NexusCommandContext ctx)
        {
            var query = Name(ctx, "слоя");
            var index = int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : LayerMask.NameToLayer(query);
            if (index < 0 || index > 31) throw new NexusCommandException($"Слоя «{query}» нет (layer.list).", NexusCommandContext.Exit.Failed);
            if (Array.IndexOf(BuiltinLayers, index) >= 0) throw new NexusCommandException($"Слой {index} встроенный — не удаляется.", NexusCommandContext.Exit.Failed);

            var so = TagManager();
            var element = Property(so, "layers").GetArrayElementAtIndex(index);
            var name = element.stringValue;
            if (string.IsNullOrEmpty(name)) throw new NexusCommandException($"Слой {index} и так свободен.", NexusCommandContext.Exit.Failed);

            if (!ctx.Flag("yes"))
            {
                ctx.Print($"Будет освобождён слой {index} «{name}». Объекты на нём и маски слоёв останутся с номером {index} без имени.");
                ctx.Print("Удалить: добавьте --yes");
                ctx.Data = new { name, index, dryRun = true };
                return NexusCommandContext.Exit.Ok;
            }

            NexusEditorCommands.Guard(ctx);
            element.stringValue = string.Empty;
            Commit(so);
            ctx.Print($"✓ слой {index} освобождён (был «{name}»)");
            ctx.Data = new { name, index, dryRun = false };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ слои сортировки

        [NexusCommand("sortinglayer.list", "Слои сортировки по порядку отрисовки", Usage = "sortinglayer.list", ReadOnly = true)]
        private static int SortingList(NexusCommandContext ctx)
        {
            var data = new List<object>();
            foreach (var l in SortingLayer.layers)
            {
                ctx.Print($"  {l.value,3} {l.name}  (id {l.id})");
                data.Add(new { name = l.name, id = l.id, value = l.value });
            }

            ctx.Data = data;
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("sortinglayer.add", "Добавить слой сортировки в конец (рисуется поверх остальных)", Usage = "sortinglayer.add <имя>")]
        private static int SortingAdd(NexusCommandContext ctx)
        {
            var name = Name(ctx, "слоя сортировки");
            var ids = new HashSet<long>();
            foreach (var l in SortingLayer.layers)
            {
                if (string.Equals(l.name, name, StringComparison.Ordinal))
                {
                    ctx.Print("слой сортировки уже есть: " + name);
                    ctx.Data = new { name, id = l.id, added = false };
                    return NexusCommandContext.Exit.Ok;
                }

                ids.Add((uint)l.id);
            }

            NexusEditorCommands.Guard(ctx);
            var so = TagManager();
            var layers = Property(so, "m_SortingLayers");
            var at = layers.arraySize;
            layers.InsertArrayElementAtIndex(at);
            var element = layers.GetArrayElementAtIndex(at);

            // id — случайное ненулевое uint, уникальное среди слоёв (0 — слой Default)
            var random = new System.Random(Guid.NewGuid().GetHashCode());
            uint id;
            do id = (uint)random.Next(1, int.MaxValue) ^ ((uint)random.Next(0, 2) << 31);
            while (id == 0 || ids.Contains(id));

            Child(element, "name").stringValue = name;
            Child(element, "uniqueID").longValue = id;
            var locked = element.FindPropertyRelative("locked");
            if (locked != null) locked.intValue = 0;
            Commit(so);
            ctx.Print($"✓ слой сортировки: {name}");
            ctx.Data = new { name, id = unchecked((int)id), added = true };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ общее

        private static string Name(NexusCommandContext ctx, string what)
        {
            var name = ctx.Arg(0)?.Trim();
            if (string.IsNullOrEmpty(name)) throw new NexusCommandException("нужно имя " + what);
            return name;
        }

        internal static SerializedObject TagManager()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
            if (assets == null || assets.Length == 0 || assets[0] == null) throw new NexusCommandException("Не читается " + TagManagerPath, NexusCommandContext.Exit.Failed);
            return new SerializedObject(assets[0]);
        }

        private static SerializedProperty Property(SerializedObject so, string name) =>
            so.FindProperty(name) ?? throw new NexusCommandException($"В TagManager нет поля {name} (другая версия Unity?).", NexusCommandContext.Exit.Failed);

        private static SerializedProperty Child(SerializedProperty p, string name) =>
            p.FindPropertyRelative(name) ?? throw new NexusCommandException($"В слое сортировки нет поля {name} (другая версия Unity?).", NexusCommandContext.Exit.Failed);

        private static int FirstFree(SerializedProperty layers)
        {
            for (var i = 8; i < 32 && i < layers.arraySize; i++)
                if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) return i;
            foreach (var i in new[] { 3, 6, 7 })
                if (i < layers.arraySize && string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) return i;
            return -1;
        }

        /// <summary>Применить с Undo и записать настройки проекта на диск.</summary>
        private static void Commit(SerializedObject so)
        {
            so.ApplyModifiedProperties();
            // TagManager — настройка проекта: без сохранения правка жила бы только до перезапуска редактора
            AssetDatabase.SaveAssets();
        }
    }
}
