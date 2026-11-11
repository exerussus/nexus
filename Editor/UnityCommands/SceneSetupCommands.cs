using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Команды сцен в дополнение к scene.list/open/save: новая сцена, закрытие аддитивной, активная сцена, список
    /// Build Settings. Без диалогов «сохранить?»: несохранённое — --save или --discard.
    /// </summary>
    internal static class SceneSetupCommands
    {
        // ================================================================ сцены редактора

        [NexusCommand("scene.new", "Создать сцену и сохранить по пути (--empty — пустая, иначе с камерой и светом)",
            Usage = "scene.new <Assets/путь.unity> [--empty|--default] [--additive] [--save|--discard]")]
        private static int New(NexusCommandContext ctx)
        {
            NotPlaying();
            NexusEditorCommands.Guard(ctx);
            var path = AssetFileCommands.NormalizeNew(ctx.Arg(0) ?? throw new NexusCommandException("нужен путь Assets/…/Имя.unity"));
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) path += ".unity";
            if (AssetQuery.Exists(path)) throw new NexusCommandException($"«{path}» уже есть — scene.open {path}", NexusCommandContext.Exit.Failed);

            var additive = ctx.Flag("additive");
            if (additive)
            {
                // Unity не добавляет новую безымянную сцену рядом с другой безымянной
                for (var i = 0; i < SceneManager.sceneCount; i++)
                    if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                        throw new NexusCommandException("Открыта сцена без пути — сохраните её (scene.save --as …) или создайте без --additive.", NexusCommandContext.Exit.Failed);
            }
            else
            {
                NexusSceneCommands.HandleDirty(ctx.Flag("save"), ctx.Flag("discard"));
            }

            AssetFileCommands.EnsureFolder(AssetFileCommands.Parent(path));
            var setup = ctx.Flag("empty") ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects;
            var scene = EditorSceneManager.NewScene(setup, additive ? NewSceneMode.Additive : NewSceneMode.Single);
            if (!scene.IsValid()) throw new NexusCommandException("Сцена не создана.", NexusCommandContext.Exit.Failed);
            if (!EditorSceneManager.SaveScene(scene, path)) throw new NexusCommandException($"Сцена создана, но не сохранена в «{path}».", NexusCommandContext.Exit.Failed);

            ctx.Print($"✓ {path}{(additive ? " (аддитивно)" : string.Empty)}{(ctx.Flag("empty") ? " — пустая" : string.Empty)}");
            ctx.Data = new { path, additive, empty = ctx.Flag("empty"), guid = AssetDatabase.AssetPathToGUID(path) };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("scene.close", "Закрыть открытую сцену (последнюю загруженную — нельзя); --remove — убрать из иерархии",
            Usage = "scene.close <имя|путь> [--save|--discard] [--remove]")]
        private static int Close(NexusCommandContext ctx)
        {
            NotPlaying();
            NexusEditorCommands.Guard(ctx);
            var scene = FindOpen(ctx.Arg(0) ?? throw new NexusCommandException("нужна сцена: имя или путь (scene.list)"));

            var loaded = 0;
            for (var i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isLoaded) loaded++;
            if (scene.isLoaded && loaded <= 1) throw new NexusCommandException("Это последняя загруженная сцена — её не закрыть (scene.open другой или scene.new).", NexusCommandContext.Exit.Failed);

            var name = Describe(scene);
            if (scene.isDirty)
            {
                if (ctx.Flag("save"))
                {
                    if (string.IsNullOrEmpty(scene.path)) throw new NexusCommandException("Сцена без пути — scene.save --as … или --discard.", NexusCommandContext.Exit.Failed);
                    if (!EditorSceneManager.SaveScene(scene)) throw new NexusCommandException("Не удалось сохранить " + name, NexusCommandContext.Exit.Failed);
                }
                else if (!ctx.Flag("discard"))
                {
                    throw new NexusCommandException($"Сцена {name} изменена — добавьте --save или --discard.", NexusCommandContext.Exit.Failed);
                }
            }

            var remove = ctx.Flag("remove") || string.IsNullOrEmpty(scene.path);
            if (!EditorSceneManager.CloseScene(scene, remove)) throw new NexusCommandException("Сцена не закрылась: " + name, NexusCommandContext.Exit.Failed);
            ctx.Print($"✓ закрыта: {name}{(remove ? " (убрана)" : " (осталась в иерархии выгруженной)")}");
            ctx.Data = new { path = scene.path, removed = remove };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("scene.active", "Сделать открытую загруженную сцену активной (туда попадают новые объекты)",
            Usage = "scene.active <имя|путь>")]
        private static int Active(NexusCommandContext ctx)
        {
            var scene = FindOpen(ctx.Arg(0) ?? throw new NexusCommandException("нужна сцена: имя или путь (scene.list)"));
            if (!scene.isLoaded) throw new NexusCommandException($"Сцена {Describe(scene)} не загружена — scene.open {scene.path} --additive", NexusCommandContext.Exit.Failed);
            if (!SceneManager.SetActiveScene(scene)) throw new NexusCommandException("Unity не сделал сцену активной: " + Describe(scene), NexusCommandContext.Exit.Failed);
            ctx.Print("✓ активна: " + Describe(scene));
            ctx.Data = new { path = scene.path };
            return NexusCommandContext.Exit.Ok;
        }

        // ================================================================ Build Settings

        [NexusCommand("build.scenes", "Сцены Build Settings: список; --add, --remove, --move … --to, --enable/--disable",
            Usage = "build.scenes [--add путь [--index N]] [--remove путь|N] [--move путь|N --to N] [--enable путь|N] [--disable путь|N]")]
        private static int BuildScenes(NexusCommandContext ctx)
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            var change = (string)null;

            if (ctx.Has("add"))
            {
                var path = NexusSceneCommands.Resolve(Value(ctx, "add"));
                var at = list.FindIndex(s => string.Equals(s.path, path, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    if (list[at].enabled) ctx.Print($"уже в списке: {at} {path}");
                    else
                    {
                        list[at] = new EditorBuildSettingsScene(path, true);
                        change = $"включена: {path}";
                    }
                }
                else
                {
                    var index = ctx.Has("index") ? ctx.GetInt("index", -1) : list.Count;
                    if (index < 0 || index > list.Count) throw new NexusCommandException($"--index: 0…{list.Count}");
                    list.Insert(index, new EditorBuildSettingsScene(path, true));
                    change = $"добавлена: {index} {path}";
                }
            }
            else if (ctx.Has("remove"))
            {
                var i = Find(list, Value(ctx, "remove"));
                change = "убрана: " + list[i].path;
                list.RemoveAt(i);
            }
            else if (ctx.Has("move"))
            {
                var i = Find(list, Value(ctx, "move"));
                if (!ctx.Has("to")) throw new NexusCommandException("--move нужен --to N");
                var to = ctx.GetInt("to", -1);
                if (to < 0 || to >= list.Count) throw new NexusCommandException($"--to: 0…{list.Count - 1}");
                var item = list[i];
                list.RemoveAt(i);
                list.Insert(to, item);
                change = $"перенесена: {item.path} {i} → {to}";
            }
            else if (ctx.Has("enable") || ctx.Has("disable"))
            {
                var enable = ctx.Has("enable");
                var i = Find(list, Value(ctx, enable ? "enable" : "disable"));
                if (list[i].enabled == enable) ctx.Print($"уже {(enable ? "включена" : "выключена")}: {list[i].path}");
                else
                {
                    list[i] = new EditorBuildSettingsScene(list[i].path, enable);
                    change = $"{(enable ? "включена" : "выключена")}: {list[i].path}";
                }
            }

            if (change != null)
            {
                NexusEditorCommands.Guard(ctx);
                EditorBuildSettings.scenes = list.ToArray();
                ctx.Print("✓ " + change);
            }

            ctx.Print(list.Count == 0 ? "Build Settings: пусто" : "Build Settings:");
            var data = new List<object>();
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var missing = !AssetQuery.Exists(s.path);
                ctx.Print($"  {i,2} {(s.enabled ? " " : "-")} {s.path}{(missing ? "  (файла нет)" : string.Empty)}");
                data.Add(new { index = i, path = s.path, enabled = s.enabled, missing });
            }

            ctx.Data = new { changed = change != null, change, scenes = data };
            return NexusCommandContext.Exit.Ok;
        }

        private static string Value(NexusCommandContext ctx, string key)
        {
            var v = ctx.Get(key);
            if (string.IsNullOrEmpty(v)) throw new NexusCommandException($"--{key}: нужен путь, имя или номер сцены");
            return v;
        }

        /// <summary>Сцена в списке Build Settings: номер, путь или имя файла.</summary>
        private static int Find(List<EditorBuildSettingsScene> list, string query)
        {
            var q = query.Trim().Replace('\\', '/');
            if (int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (n < 0 || n >= list.Count) throw new NexusCommandException($"В Build Settings нет сцены №{n} (всего {list.Count}).", NexusCommandContext.Exit.Failed);
                return n;
            }

            var found = -1;
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i].path;
                if (!string.Equals(p, q, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFileNameWithoutExtension(p), Path.GetFileNameWithoutExtension(q), StringComparison.OrdinalIgnoreCase)) continue;
                if (found >= 0) throw new NexusCommandException($"«{query}» в Build Settings неоднозначно — укажите путь или номер.", NexusCommandContext.Exit.Failed);
                found = i;
            }

            if (found < 0) throw new NexusCommandException($"«{query}» нет в Build Settings (build.scenes).", NexusCommandContext.Exit.Failed);
            return found;
        }

        // ================================================================ общее

        private static void NotPlaying()
        {
            if (EditorApplication.isPlaying) throw new NexusCommandException("Идёт Play — остановите: play.stop", NexusCommandContext.Exit.Failed);
        }

        /// <summary>Открытая сцена по пути или имени (среди открытых в редакторе).</summary>
        private static Scene FindOpen(string query)
        {
            var q = query.Trim().Replace('\\', '/');
            var matches = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (string.Equals(s.path, q, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.name, Path.GetFileNameWithoutExtension(q), StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(s.path) && s.path.EndsWith("/" + q, StringComparison.OrdinalIgnoreCase))
                    matches.Add(s);
            }

            if (matches.Count == 1) return matches[0];
            var open = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++) open.Add(Describe(SceneManager.GetSceneAt(i)));
            if (matches.Count == 0) throw new NexusCommandException($"Сцена «{query}» не открыта; открыты: {string.Join(", ", open)}.", NexusCommandContext.Exit.Failed);
            throw new NexusCommandException($"«{query}» подходит к нескольким открытым сценам — укажите путь.", NexusCommandContext.Exit.Failed);
        }

        private static string Describe(Scene s) => string.IsNullOrEmpty(s.path) ? "(без имени)" : s.path;
    }
}
