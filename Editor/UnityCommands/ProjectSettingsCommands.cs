using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using UnityEditor;
using UnityEditor.Build;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Настройки проекта: символы компиляции (defines) и поля ProjectSettings/*.asset через SerializedObject.
    /// </summary>
    internal static class ProjectSettingsCommands
    {
        /// <summary>Короткое имя → файл настроек.</summary>
        private static readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "player", "ProjectSettings/ProjectSettings.asset" },
            { "quality", "ProjectSettings/QualitySettings.asset" },
            { "physics", "ProjectSettings/DynamicsManager.asset" },
            { "physics2d", "ProjectSettings/Physics2DSettings.asset" },
            { "time", "ProjectSettings/TimeManager.asset" },
            { "audio", "ProjectSettings/AudioManager.asset" },
            { "graphics", "ProjectSettings/GraphicsSettings.asset" },
            { "editor", "ProjectSettings/EditorSettings.asset" },
            { "input", "ProjectSettings/InputManager.asset" },
            { "tags", "ProjectSettings/TagManager.asset" },
        };

        private static readonly Regex DefinePattern = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        // ================================================================ defines

        [NexusCommand("defines", "Символы компиляции (Scripting Define Symbols) платформы: показать, --add, --remove, --set (меняющее — перекомпиляция)",
            Usage = "defines [--target Standalone|Android|iOS|WebGL|Server|…] [--add A,B] [--remove C] [--set \"A;B\"]")]
        private static int Defines(NexusCommandContext ctx)
        {
            var target = Target(ctx.Get("target"));
            var before = new List<string>(Split(PlayerSettings.GetScriptingDefineSymbols(target)));
            var after = new List<string>(before);

            if (ctx.Has("set"))
            {
                after = new List<string>(Split(ctx.Get("set") ?? string.Empty));
            }
            else
            {
                foreach (var d in ctx.GetList("remove")) after.RemoveAll(x => x == d);
                foreach (var d in ctx.GetList("add"))
                    if (!after.Contains(d)) after.Add(d);
            }

            foreach (var d in after)
                if (!DefinePattern.IsMatch(d)) throw new NexusCommandException($"«{d}» — не имя символа (латиница, цифры, _).");

            var changed = !SameOrder(before, after);
            if (changed)
            {
                if (EditorApplication.isPlaying) throw new NexusCommandException("Идёт Play — остановите: play.stop", NexusCommandContext.Exit.Failed);
                NexusEditorCommands.Guard(ctx);
                PlayerSettings.SetScriptingDefineSymbols(target, after.ToArray());
                // смена символов перекомпилирует скрипты: хаб дождётся компиляции и отдаст её итог
                ctx.WaitForEditor = true;
                ctx.Warn("символы изменены — скрипты перекомпилируются, домен перезагрузится");
            }

            var added = after.FindAll(d => !before.Contains(d));
            var removed = before.FindAll(d => !after.Contains(d));
            ctx.Print($"{target.TargetName}: {(after.Count == 0 ? "символов нет" : string.Join(";", after))}");
            if (added.Count > 0) ctx.Print("  + " + string.Join(", ", added));
            if (removed.Count > 0) ctx.Print("  − " + string.Join(", ", removed));
            ctx.Data = new { target = target.TargetName, defines = after, added, removed, changed };
            return NexusCommandContext.Exit.Ok;
        }

        private static NamedBuildTarget Target(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                // текущая платформа сборки; выделенный сервер — отдельная цель
                if (EditorUserBuildSettings.selectedBuildTargetGroup == BuildTargetGroup.Standalone &&
                    EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server)
                    return NamedBuildTarget.Server;
                return NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
            }

            if (string.Equals(name, "Server", StringComparison.OrdinalIgnoreCase)) return NamedBuildTarget.Server;
            if (string.Equals(name, "Windows", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Mac", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Linux", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Desktop", StringComparison.OrdinalIgnoreCase))
                return NamedBuildTarget.Standalone;

            if (Enum.TryParse<BuildTargetGroup>(name, true, out var group) && group != BuildTargetGroup.Unknown && !int.TryParse(name, out _))
            {
                try
                {
                    return NamedBuildTarget.FromBuildTargetGroup(group);
                }
                catch (Exception e)
                {
                    throw new NexusCommandException($"--target {name}: {e.Message}");
                }
            }

            throw new NexusCommandException($"--target: неизвестная платформа «{name}» (Standalone, Server, Android, iOS, WebGL, …).");
        }

        private static IEnumerable<string> Split(string value)
        {
            foreach (var part in value.Split(';', ','))
            {
                var d = part.Trim();
                if (d.Length > 0) yield return d;
            }
        }

        private static bool SameOrder(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ================================================================ settings.get / settings.set

        [NexusCommand("settings.get", "Поля настроек проекта: player, quality, physics, physics2d, time, audio, graphics, editor, input, tags",
            Usage = "settings.get <player|quality|physics|physics2d|time|audio|graphics|editor|input|tags> [путь.поля] [--depth 2] [--max-array 20]",
            ReadOnly = true)]
        private static int SettingsGet(NexusCommandContext ctx)
        {
            var so = Settings(ctx.Arg(0), out _);
            var field = ctx.Arg(1) ?? string.Empty;
            var depth = Math.Max(0, ctx.GetInt("depth", 2));
            var maxArray = Math.Max(1, ctx.GetInt("max-array", 20));
            SerializedAccess.Print(ctx, so, field, depth, maxArray);
            ctx.Data = SerializedAccess.Dump(so, field, depth, maxArray);
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("settings.set", "Записать поля настроек проекта (одна запись Undo) и сохранить их",
            Usage = "settings.set <player|quality|physics|…> путь=значение [путь=значение …]")]
        private static int SettingsSet(NexusCommandContext ctx)
        {
            NexusEditorCommands.Guard(ctx);
            var so = Settings(ctx.Arg(0), out var file);
            var assignments = new List<string>();
            for (var i = 1; i < ctx.Positional.Count; i++) assignments.Add(ctx.Positional[i]);
            if (assignments.Count == 0) throw new NexusCommandException("нужны присваивания: путь=значение (settings.get — какие поля есть)");

            SerializedAccess.SetMany(so, assignments);
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            ctx.Print($"✓ {file}: записано ({assignments.Count})");
            foreach (var a in assignments)
            {
                // символы компиляции в PlayerSettings меняют код — дождаться перекомпиляции
                if (!a.StartsWith("scriptingDefineSymbols", StringComparison.Ordinal)) continue;
                ctx.WaitForEditor = true;
                ctx.Warn("изменены символы компиляции — скрипты перекомпилируются (удобнее команда defines)");
                break;
            }

            ctx.Data = new { file, assignments };
            return NexusCommandContext.Exit.Ok;
        }

        private static SerializedObject Settings(string name, out string file)
        {
            if (string.IsNullOrEmpty(name)) throw new NexusCommandException("нужен раздел: " + string.Join(", ", Files.Keys));
            if (!Files.TryGetValue(name, out file)) throw new NexusCommandException($"Раздел «{name}» неизвестен; есть: {string.Join(", ", Files.Keys)}.");
            var assets = AssetDatabase.LoadAllAssetsAtPath(file);
            if (assets == null || assets.Length == 0 || assets[0] == null) throw new NexusCommandException("Не читается " + file, NexusCommandContext.Exit.Failed);
            return new SerializedObject(assets[0]);
        }
    }
}
