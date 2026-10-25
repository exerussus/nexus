using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Exerussus.Nexus.Abstractions;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Команды CLI: встроенные (<c>help</c>, <c>ping</c>), команды управления редактором ядра (<c>status</c>,
    /// <c>refresh</c>, <c>logs</c>, <c>play.*</c>, …) и методы с <see cref="NexusCommandAttribute"/> в
    /// скомпилированном коде. Без кэша: TypeCache Unity и так держит индекс домена, список собирается на вызов.
    /// </summary>
    public static class NexusCommands
    {
        public sealed class Info
        {
            public string Name;
            public string Summary;
            public string Usage;
            public string Source;
            public bool ReadOnly;
            internal Func<NexusCommandContext, int> Run;
        }

        /// <summary>Все команды по имени (первая с таким именем выигрывает, дубли — в предупреждения).</summary>
        public static List<Info> All(List<string> warnings = null)
        {
            var list = new List<Info>
            {
                new Info { Name = "help", Summary = "Список команд или справка по одной", Usage = "help [команда]", Source = "nexus", ReadOnly = true, Run = Help },
                new Info { Name = "ping", Summary = "Проверка связи: проект, Unity, Nexus", Usage = "ping", Source = "nexus", ReadOnly = true, Run = Ping },
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "help", "ping", "list" };
            foreach (var method in TypeCache.GetMethodsWithAttribute<NexusCommandAttribute>())
            {
                var attr = method.GetCustomAttribute<NexusCommandAttribute>();
                if (attr == null || string.IsNullOrWhiteSpace(attr.Name)) continue;

                var source = method.DeclaringType != null ? method.DeclaringType.Assembly.GetName().Name : "?";
                if (source.StartsWith("Exerussus.Nexus.", StringComparison.Ordinal) && !source.StartsWith("Exerussus.Nexus.Pages.", StringComparison.Ordinal)) source = "nexus";
                var run = Bind(method, out var problem);
                if (run == null)
                {
                    warnings?.Add($"{attr.Name} ({source}): {problem}");
                    NexusDiagnostics.Warn("CLI", $"{attr.Name}: {problem}");
                    continue;
                }

                if (!seen.Add(attr.Name))
                {
                    warnings?.Add($"{attr.Name} ({source}): имя уже занято — пропущена");
                    continue;
                }

                list.Add(new Info { Name = attr.Name, Summary = attr.Summary, Usage = attr.Usage, Source = source, ReadOnly = attr.ReadOnly, Run = run });
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        /// <summary>static int M(NexusCommandContext) или static void M(NexusCommandContext).</summary>
        private static Func<NexusCommandContext, int> Bind(MethodInfo method, out string problem)
        {
            problem = null;
            if (!method.IsStatic)
            {
                problem = "метод команды должен быть static";
                return null;
            }

            var ps = method.GetParameters();
            if (ps.Length != 1 || ps[0].ParameterType != typeof(NexusCommandContext))
            {
                problem = "сигнатура — (NexusCommandContext ctx)";
                return null;
            }

            if (method.ReturnType == typeof(int)) return ctx => (int)method.Invoke(null, new object[] { ctx });
            if (method.ReturnType == typeof(void))
                return ctx =>
                {
                    method.Invoke(null, new object[] { ctx });
                    return ctx.HasErrors ? NexusCommandContext.Exit.Failed : NexusCommandContext.Exit.Ok;
                };

            problem = "возвращает int или void";
            return null;
        }

        /// <summary>Выполнить в главном потоке. Никогда не бросает: исключение команды — код выхода и текст.</summary>
        public static int Execute(NexusCommandContext ctx)
        {
            Info command = null;
            try
            {
                foreach (var c in All())
                    if (string.Equals(c.Name, ctx.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        command = c;
                        break;
                    }
            }
            catch (Exception e)
            {
                ctx.Error("Не удалось собрать список команд: " + e.Message);
                NexusDiagnostics.Error("CLI", e);
                return NexusCommandContext.Exit.Crashed;
            }

            if (command == null)
            {
                ctx.Error($"Нет команды «{ctx.Name}». Список: help");
                return NexusCommandContext.Exit.UnknownCommand;
            }

            // идущая задача держит редактор: меняющие команды извне — только с --force (вмешательство — в её отчёт)
            if (!command.ReadOnly && !NexusCliTasks.Admit(ctx, out var refusal))
            {
                ctx.Error(refusal);
                return NexusCommandContext.Exit.Failed;
            }

            try
            {
                return command.Run(ctx);
            }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;

                if (e is NexusCommandException ce)
                {
                    ctx.Error(ce.Message);
                    if (ce.ExitCode == NexusCommandContext.Exit.Usage && !string.IsNullOrEmpty(command.Usage)) ctx.Print("Использование: " + command.Usage);
                    return ce.ExitCode;
                }

                ctx.Error($"{command.Name}: {e.GetType().Name}: {e.Message}");
                NexusDiagnostics.Error($"CLI {command.Name}", e);
                return NexusCommandContext.Exit.Crashed;
            }
        }

        // ---------------------------------------------------------------- встроенные

        private static int Help(NexusCommandContext ctx)
        {
            var warnings = new List<string>();
            var all = All(warnings);
            var name = ctx.Arg(0);

            if (!string.IsNullOrEmpty(name))
            {
                var c = all.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (c == null) throw new NexusCommandException($"Нет команды «{name}»", NexusCommandContext.Exit.UnknownCommand);
                ctx.Print(c.Name + " — " + c.Summary);
                if (!string.IsNullOrEmpty(c.Usage)) ctx.Print("Использование: " + c.Usage);
                ctx.Print("Откуда: " + c.Source);
                ctx.Data = new Dictionary<string, object> { { "name", c.Name }, { "summary", c.Summary }, { "usage", c.Usage }, { "source", c.Source } };
                return NexusCommandContext.Exit.Ok;
            }

            var width = 0;
            foreach (var c in all) width = Math.Max(width, c.Name.Length);
            var data = new List<Dictionary<string, object>>();
            foreach (var c in all)
            {
                ctx.Print(c.Name.PadRight(width + 2) + (c.Summary ?? string.Empty));
                data.Add(new Dictionary<string, object> { { "name", c.Name }, { "summary", c.Summary }, { "usage", c.Usage }, { "source", c.Source } });
            }

            foreach (var w in warnings) ctx.Warn(w);
            ctx.Data = data;
            return NexusCommandContext.Exit.Ok;
        }

        private static int Ping(NexusCommandContext ctx)
        {
            var info = NexusCliServer.Describe();
            var sb = new StringBuilder();
            sb.Append(info.Project).Append(" · Unity ").Append(info.Unity).Append(" · ").Append(info.Path);
            ctx.Print(sb.ToString());
            ctx.Data = new Dictionary<string, object>
            {
                { "project", info.Project }, { "path", info.Path }, { "unity", info.Unity }, { "pid", info.Pid },
                { "playing", EditorApplication.isPlaying }, { "compiling", EditorApplication.isCompiling },
                { "phase", NexusCliDomainMarks.Phase() },
            };
            return NexusCommandContext.Exit.Ok;
        }
    }
}
