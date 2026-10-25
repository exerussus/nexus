using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Exerussus.Nexus.Core
{
    /// <summary>Команды сцен: список, открытие (без диалога «сохранить?»), сохранение.</summary>
    public static class NexusSceneCommands
    {
        [NexusCommand("scene.list", "Сцены: из Build Settings и открытые; --all — все сцены проекта",
            Usage = "scene.list [--all] [--max 100]", ReadOnly = true)]
        private static int List(NexusCommandContext ctx)
        {
            var build = new List<object>();
            var scenes = EditorBuildSettings.scenes;
            ctx.Print(scenes.Length == 0 ? "Build Settings: пусто" : "Build Settings:");
            for (var i = 0; i < scenes.Length; i++)
            {
                var s = scenes[i];
                ctx.Print($"  {i,2} {(s.enabled ? " " : "-")} {s.path}");
                build.Add(new { index = i, path = s.path, enabled = s.enabled });
            }

            var open = new List<object>();
            var active = SceneManager.GetActiveScene();
            ctx.Print("Открыты:");
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                ctx.Print($"  {(string.IsNullOrEmpty(s.path) ? "(без имени)" : s.path)}{(s.isDirty ? " *" : string.Empty)}{(s == active ? " [активна]" : string.Empty)}{(s.isLoaded ? string.Empty : " (не загружена)")}");
                open.Add(new { path = s.path, dirty = s.isDirty, active = s == active, loaded = s.isLoaded });
            }

            List<string> all = null;
            if (ctx.Flag("all"))
            {
                all = FindAll();
                var max = Math.Max(1, ctx.GetInt("max", 100));
                ctx.Print($"Все сцены ({all.Count}):");
                for (var i = 0; i < all.Count && i < max; i++) ctx.Print("  " + all[i]);
                if (all.Count > max) ctx.Print($"  … ещё {all.Count - max} (--max)");
            }

            ctx.Data = new { build, open, all };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("scene.open", "Открыть сцену по имени, пути или номеру в Build Settings (в Play — загрузить)",
            Usage = "scene.open <имя|путь|номер> [--additive] [--save|--discard]")]
        private static int Open(NexusCommandContext ctx)
        {
            var query = ctx.Arg(0) ?? throw new NexusCommandException("нужна сцена: имя, путь или номер (scene.list)");
            var path = Resolve(query);
            var additive = ctx.Flag("additive");

            if (EditorApplication.isPlaying)
            {
                EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(additive ? LoadSceneMode.Additive : LoadSceneMode.Single));
                ctx.Print($"✓ загружается в Play: {path}");
                ctx.Data = new { path, playMode = true };
                return NexusCommandContext.Exit.Ok;
            }

            NexusEditorCommands.Guard(ctx);
            if (!additive) HandleDirty(ctx.Flag("save"), ctx.Flag("discard"));
            var scene = EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            if (!scene.IsValid()) throw new NexusCommandException($"Сцена не открылась: {path}", NexusCommandContext.Exit.Failed);
            ctx.Print("✓ " + path);
            ctx.Data = new { path, playMode = false };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("scene.save", "Сохранить активную сцену (--all — все открытые, --as — под новым путём)",
            Usage = "scene.save [--all] [--as Assets/Scenes/New.unity]")]
        private static int Save(NexusCommandContext ctx)
        {
            if (EditorApplication.isPlaying) throw new NexusCommandException("Идёт Play — сцены не сохраняются.", NexusCommandContext.Exit.Failed);

            if (ctx.Flag("all"))
            {
                if (HasUntitledDirty()) throw new NexusCommandException("Есть изменённая сцена без пути — сохраните её: scene.save --as Assets/…/Имя.unity", NexusCommandContext.Exit.Failed);
                if (!EditorSceneManager.SaveOpenScenes()) throw new NexusCommandException("Не все сцены сохранены (сцена без пути? — --as).", NexusCommandContext.Exit.Failed);
                ctx.Print("✓ все открытые сцены сохранены");
                return NexusCommandContext.Exit.Ok;
            }

            var scene = SceneManager.GetActiveScene();
            var target = ctx.Get("as");
            if (!string.IsNullOrEmpty(target))
            {
                target = target.Replace('\\', '/');
                if (!target.StartsWith("Assets/", StringComparison.Ordinal) || !target.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    throw new NexusCommandException("--as: путь вида Assets/…/Имя.unity");
            }
            else if (string.IsNullOrEmpty(scene.path))
            {
                throw new NexusCommandException("У сцены нет пути — укажите --as Assets/…/Имя.unity");
            }

            if (!EditorSceneManager.SaveScene(scene, string.IsNullOrEmpty(target) ? string.Empty : target))
                throw new NexusCommandException("Сцена не сохранена.", NexusCommandContext.Exit.Failed);
            ctx.Print("✓ " + (string.IsNullOrEmpty(target) ? scene.path : target));
            return NexusCommandContext.Exit.Ok;
        }

        // ---------------------------------------------------------------- общее

        /// <summary>
        /// Изменённые сцены перед открытием другой: --save — сохранить, --discard — отбросить, иначе ошибка
        /// со списком (диалог «сохранить?» CLI не показывает).
        /// </summary>
        public static void HandleDirty(bool save, bool discard, bool revert = false)
        {
            var dirty = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isDirty) dirty.Add(s);
            }

            if (dirty.Count == 0) return;
            if (discard)
            {
                if (!revert) return;

                // отбросить по-настоящему: перечитать открытые сцены с диска (иначе следующий шаг Unity спросит «сохранить?»)
                foreach (var s in dirty)
                    if (string.IsNullOrEmpty(s.path))
                        throw new NexusCommandException("Изменённая сцена без пути не перечитывается с диска — scene.save --as <путь> или закройте её.", NexusCommandContext.Exit.Failed);
                var setup = EditorSceneManager.GetSceneManagerSetup();
                for (var i = 0; i < setup.Length; i++)
                {
                    var mode = i == 0 ? OpenSceneMode.Single : setup[i].isLoaded ? OpenSceneMode.Additive : OpenSceneMode.AdditiveWithoutLoading;
                    var opened = EditorSceneManager.OpenScene(setup[i].path, mode);
                    if (setup[i].isActive && opened.IsValid() && opened.isLoaded) SceneManager.SetActiveScene(opened);
                }

                return;
            }

            if (!save)
            {
                var names = new List<string>();
                foreach (var s in dirty) names.Add(string.IsNullOrEmpty(s.path) ? "(без имени)" : s.path);
                throw new NexusCommandException("Есть несохранённые сцены: " + string.Join(", ", names) + " — добавьте --save или --discard.", NexusCommandContext.Exit.Failed);
            }

            foreach (var s in dirty)
            {
                if (string.IsNullOrEmpty(s.path)) throw new NexusCommandException("Сцена без пути не сохраняется — scene.save --as <путь> или --discard.", NexusCommandContext.Exit.Failed);
                if (!EditorSceneManager.SaveScene(s)) throw new NexusCommandException("Не удалось сохранить " + s.path, NexusCommandContext.Exit.Failed);
            }
        }

        /// <summary>Сцена по номеру в Build Settings, пути или имени (сначала Build Settings, потом весь проект).</summary>
        /// <summary>Есть изменённая сцена без пути: сохранение открыло бы диалог «Save As».</summary>
        public static bool HasUntitledDirty()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isDirty && string.IsNullOrEmpty(s.path)) return true;
            }

            return false;
        }

        public static string Resolve(string query)
        {
            var q = query.Trim().Replace('\\', '/');
            var build = EditorBuildSettings.scenes;

            if (int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= build.Length) throw new NexusCommandException($"В Build Settings нет сцены №{index} (всего {build.Length}).", NexusCommandContext.Exit.Failed);
                return build[index].path;
            }

            if (q.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(q)))
                return q;

            var name = Path.GetFileNameWithoutExtension(q);
            var fromBuild = new List<string>();
            foreach (var s in build)
                if (string.Equals(Path.GetFileNameWithoutExtension(s.path), name, StringComparison.OrdinalIgnoreCase))
                    fromBuild.Add(s.path);
            if (fromBuild.Count == 1) return fromBuild[0];

            var matches = new List<string>();
            foreach (var p in FindAll())
                if (string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith("/" + q, StringComparison.OrdinalIgnoreCase) || p.EndsWith("/" + q + ".unity", StringComparison.OrdinalIgnoreCase))
                    matches.Add(p);

            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0) throw new NexusCommandException($"Сцена «{query}» не найдена (scene.list --all).", NexusCommandContext.Exit.Failed);
            throw new NexusCommandException($"«{query}» подходит к нескольким сценам: {string.Join(", ", matches)} — укажите путь.", NexusCommandContext.Exit.Failed);
        }

        private static List<string> FindAll()
        {
            var list = new List<string>();
            foreach (var g in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(p)) list.Add(p);
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}
