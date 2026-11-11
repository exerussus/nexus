using System;
using System.Collections.Generic;
using System.Reflection;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Команды компонентов: список, добавление, удаление, чтение и запись сериализованных полей, вызов методов
    /// [ContextMenu]. Компонент адресуется типом: <c>Rigidbody</c>, <c>MyGame.Health</c>, <c>Health[1]</c> или номером из comp.list.
    /// </summary>
    internal static class ComponentCommands
    {
        [NexusCommand("comp.list", "Компоненты объекта с номерами и адресами Type[n]", Usage = "comp.list <spec>", ReadOnly = true)]
        private static int List(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var path = UnityObjectQuery.PathOf(go);
            ctx.Print($"{path}  {UnityObjectQuery.IdOf(go)}");
            var components = GameObjectCommands.ListComponents(ctx, go);
            ctx.Data = new { path, id = UnityObjectQuery.IdOf(go), components };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("comp.add", "Добавить компонент (и сразу задать поля path=value)",
            Usage = "comp.add <spec> <Type> [path=value …] [--set path=value …] [--play]")]
        private static int Add(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var typeSpec = ctx.Arg(1) ?? throw new NexusCommandException("нужен тип компонента");
            var type = UnityObjectQuery.ResolveComponentType(typeSpec);
            CheckAddable(type);
            if (type.IsDefined(typeof(DisallowMultipleComponent), true) && go.GetComponent(type) != null)
                throw new NexusCommandException($"{type.Name} уже есть на «{go.name}», а второй запрещён [DisallowMultipleComponent].", NexusCommandContext.Exit.Failed);

            var assignments = UnityEdit.PositionalFrom(ctx, 2);
            assignments.AddRange(UnityEdit.Repeated(ctx, "set"));
            UnityEdit.Begin(ctx);

            var group = UnityEdit.BeginGroup("Nexus: comp.add " + type.Name);
            var component = Undo.AddComponent(go, type);
            if (component == null)
                throw new NexusCommandException($"Unity не добавил {type.Name} на «{go.name}» (конфликт компонентов или ошибка скрипта — logs --errors).", NexusCommandContext.Exit.Failed);

            if (assignments.Count > 0)
            {
                try
                {
                    SerializedAccess.SetMany(new SerializedObject(component), assignments);
                }
                catch
                {
                    // поле не задалось — компонент тоже не добавляем
                    UnityEdit.EndGroup(group);
                    Undo.RevertAllDownToGroup(group);
                    throw;
                }
            }

            UnityEdit.EndGroup(group);
            UnityEdit.MarkDirty(go);
            var spec = UnityObjectQuery.ComponentSpec(component);
            ctx.Print($"✓ {UnityObjectQuery.PathOf(go)} + {spec}");
            if (assignments.Count > 0) PrintValues(ctx, component, assignments, out _);
            ctx.Data = new { path = UnityObjectQuery.PathOf(go), id = UnityObjectQuery.IdOf(go), component = spec, type = type.FullName };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Тип можно добавить командой: не абстрактный, не Transform.</summary>
        internal static void CheckAddable(Type type)
        {
            if (typeof(Transform).IsAssignableFrom(type))
                throw new NexusCommandException("Transform есть у каждого объекта и не добавляется (RectTransform — через UI-компоненты).", NexusCommandContext.Exit.Failed);
            if (type.IsAbstract || type.ContainsGenericParameters)
                throw new NexusCommandException($"{type.FullName} — абстрактный или обобщённый тип: укажите конкретный.", NexusCommandContext.Exit.Failed);
        }

        [NexusCommand("comp.remove", "Удалить компонент (с проверкой [RequireComponent])", Usage = "comp.remove <spec> <Type[n]> [--play]")]
        private static int Remove(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var component = UnityObjectQuery.ResolveComponent(go, ctx.Arg(1));
            if (component is Transform) throw new NexusCommandException("Transform не удаляется — удалите объект: go.delete", NexusCommandContext.Exit.Failed);

            var blockers = RequiredBy(component);
            if (blockers.Count > 0)
                throw new NexusCommandException(
                    $"{component.GetType().Name} нужен [RequireComponent] у: {string.Join(", ", blockers)} — сначала удалите их (comp.remove {ctx.Arg(0)} {blockers[0]}).",
                    NexusCommandContext.Exit.Failed);

            UnityEdit.Begin(ctx);
            var spec = UnityObjectQuery.ComponentSpec(component);
            Undo.DestroyObjectImmediate(component);
            if (component != null)
                throw new NexusCommandException($"Unity не удалил {spec} (logs --errors).", NexusCommandContext.Exit.Failed);

            UnityEdit.MarkDirty(go);
            ctx.Print($"✓ {UnityObjectQuery.PathOf(go)} − {spec}");
            ctx.Data = new { path = UnityObjectQuery.PathOf(go), removed = spec };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Компоненты, чьё [RequireComponent] держится только на этом компоненте.</summary>
        private static List<string> RequiredBy(Component component)
        {
            var result = new List<string>();
            var all = component.gameObject.GetComponents<Component>();
            foreach (var other in all)
            {
                if (other == null || other == component) continue;
                foreach (RequireComponent attr in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                foreach (var required in new[] { attr.m_Type0, attr.m_Type1, attr.m_Type2 })
                {
                    if (required == null || !required.IsInstanceOfType(component)) continue;
                    var satisfiedElsewhere = false;
                    foreach (var c in all)
                        if (c != null && c != component && required.IsInstanceOfType(c))
                        {
                            satisfiedElsewhere = true;
                            break;
                        }

                    var spec = UnityObjectQuery.ComponentSpec(other);
                    if (!satisfiedElsewhere && !result.Contains(spec)) result.Add(spec);
                }
            }

            return result;
        }

        [NexusCommand("comp.get", "Сериализованные поля компонента (как в инспекторе, с путями для comp.set)",
            Usage = "comp.get <spec> <Type[n]> [путь] [--depth 2] [--max-array 20]", ReadOnly = true)]
        private static int Get(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var component = UnityObjectQuery.ResolveComponent(go, ctx.Arg(1));
            var path = ctx.Arg(2);
            var depth = Math.Max(0, ctx.GetInt("depth", 2));
            var maxArray = Math.Max(0, ctx.GetInt("max-array", 20));

            var so = new SerializedObject(component);
            var spec = UnityObjectQuery.ComponentSpec(component);
            ctx.Print($"{UnityObjectQuery.PathOf(go)} @ {spec}");
            SerializedAccess.Print(ctx, so, path, depth, maxArray);
            ctx.Data = new
            {
                path = UnityObjectQuery.PathOf(go),
                id = UnityObjectQuery.IdOf(go),
                component = spec,
                type = component.GetType().FullName,
                fields = SerializedAccess.Dump(so, path, depth, maxArray),
            };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("comp.set", "Задать поля компонента: path=value … (одной записью Undo)",
            Usage = "comp.set <spec> <Type[n]> path=value [path=value …] [--play]")]
        private static int Set(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var component = UnityObjectQuery.ResolveComponent(go, ctx.Arg(1));
            var assignments = UnityEdit.PositionalFrom(ctx, 2);
            assignments.AddRange(UnityEdit.Repeated(ctx, "set"));
            if (assignments.Count == 0) throw new NexusCommandException("нужны присваивания path=value (пути — comp.get)");

            UnityEdit.Begin(ctx);
            SerializedAccess.SetMany(new SerializedObject(component), assignments);
            UnityEdit.MarkDirty(go);
            if (EditorUtility.IsPersistent(component)) EditorUtility.SetDirty(component);

            ctx.Print($"✓ {UnityObjectQuery.PathOf(go)} @ {UnityObjectQuery.ComponentSpec(component)}");
            PrintValues(ctx, component, assignments, out var values);
            ctx.Data = new { path = UnityObjectQuery.PathOf(go), component = UnityObjectQuery.ComponentSpec(component), values };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Значения полей после записи — как их теперь видит Unity.</summary>
        private static void PrintValues(NexusCommandContext ctx, Component component, List<string> assignments, out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>();
            var so = new SerializedObject(component);
            foreach (var a in assignments)
            {
                SerializedAccess.SplitAssignment(a, out var path, out _);
                var p = SerializedAccess.Find(so, path);
                var text = SerializedAccess.Format(p);
                values[p.propertyPath] = text;
                ctx.Print($"  {p.propertyPath} = {text}");
            }
        }

        [NexusCommand("comp.invoke", "Вызвать метод компонента с [ContextMenu] (как пункт меню инспектора)",
            Usage = "comp.invoke <spec> <Type[n]> <метод или текст пункта> [--play]")]
        private static int Invoke(NexusCommandContext ctx)
        {
            var go = UnityObjectQuery.Resolve(ctx.Arg(0));
            var component = UnityObjectQuery.ResolveComponent(go, ctx.Arg(1));
            var name = ctx.Arg(2);
            var menu = ContextMethods(component.GetType());
            if (string.IsNullOrEmpty(name))
                throw new NexusCommandException("нужен метод. Доступны: " + Describe(menu));

            MethodInfo method = null;
            foreach (var entry in menu)
                if (string.Equals(entry.Key.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    method = entry.Key;
                    break;
                }

            if (method == null)
                throw new NexusCommandException($"У {component.GetType().Name} нет метода [ContextMenu] «{name}». Доступны: {Describe(menu)}", NexusCommandContext.Exit.Failed);

            UnityEdit.Begin(ctx);
            Undo.RecordObject(component, "Nexus: comp.invoke " + method.Name);
            try
            {
                method.Invoke(component, null);
            }
            catch (TargetInvocationException e)
            {
                var inner = e.InnerException ?? e;
                throw new NexusCommandException($"{method.Name}: {inner.GetType().Name}: {inner.Message}", NexusCommandContext.Exit.Failed);
            }

            EditorUtility.SetDirty(component);
            UnityEdit.MarkDirty(go);
            ctx.Print($"✓ {UnityObjectQuery.PathOf(go)} @ {UnityObjectQuery.ComponentSpec(component)}: {method.Name}()");
            ctx.Data = new { path = UnityObjectQuery.PathOf(go), component = UnityObjectQuery.ComponentSpec(component), method = method.Name };
            return NexusCommandContext.Exit.Ok;
        }

        /// <summary>Методы с [ContextMenu] без параметров (не validate) по всей иерархии типа: метод → текст пункта.</summary>
        private static List<KeyValuePair<MethodInfo, string>> ContextMethods(Type type)
        {
            var list = new List<KeyValuePair<MethodInfo, string>>();
            var seen = new HashSet<string>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.GetParameters().Length != 0 || m.ContainsGenericParameters) continue;
                    foreach (ContextMenu attr in m.GetCustomAttributes(typeof(ContextMenu), false))
                    {
                        if (attr.validate) continue;
                        if (!seen.Add(m.Name)) continue;
                        list.Add(new KeyValuePair<MethodInfo, string>(m, attr.menuItem));
                    }
                }
            }

            return list;
        }

        private static string Describe(List<KeyValuePair<MethodInfo, string>> menu)
        {
            if (menu.Count == 0) return "(нет методов [ContextMenu])";
            var parts = new List<string>();
            foreach (var e in menu) parts.Add(string.Equals(e.Key.Name, e.Value, StringComparison.Ordinal) ? e.Key.Name : $"{e.Key.Name} («{e.Value}»)");
            return string.Join(", ", parts);
        }
    }
}
