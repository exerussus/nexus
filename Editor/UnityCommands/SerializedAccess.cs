using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Чтение и запись сериализованных полей (SerializedObject) в текстовом виде для CLI: дерево полей, одна строка
    /// на значение и разбор присваиваний <c>path=value</c>. Формат <see cref="Format"/> принимает <see cref="Set"/>.
    /// Запись — через ApplyModifiedProperties: Undo и переопределения префабов ведёт Unity. Без состояния.
    /// </summary>
    public static class SerializedAccess
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ================================================================ чтение

        /// <summary>
        /// Дерево полей для <c>ctx.Data</c>: узел — словарь (path, name, type и value | count+items | fields).
        /// <paramref name="path"/> == null — список видимых полей верхнего уровня.
        /// </summary>
        public static object Dump(SerializedObject so, string path, int depth, int maxArray)
        {
            if (so == null) throw new ArgumentNullException(nameof(so));
            so.UpdateIfRequiredOrScript();
            depth = Math.Max(0, depth);
            maxArray = Math.Max(0, maxArray);

            if (!string.IsNullOrEmpty(path)) return Node(Find(so, path), depth, maxArray);

            var list = new List<object>();
            var it = so.GetIterator();
            if (it.NextVisible(true))
                do list.Add(Node(it.Copy(), depth, maxArray));
                while (it.NextVisible(false));
            return list;
        }

        /// <summary>Для человека: <c>propertyPath = value</c> с отступами по вложенности.</summary>
        public static void Print(NexusCommandContext ctx, SerializedObject so, string path, int depth, int maxArray)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (so == null) throw new ArgumentNullException(nameof(so));
            so.UpdateIfRequiredOrScript();
            depth = Math.Max(0, depth);
            maxArray = Math.Max(0, maxArray);

            if (!string.IsNullOrEmpty(path))
            {
                PrintNode(ctx, Find(so, path), depth, maxArray, 0);
                return;
            }

            var it = so.GetIterator();
            if (it.NextVisible(true))
                do PrintNode(ctx, it.Copy(), depth, maxArray, 0);
                while (it.NextVisible(false));
        }

        /// <summary>Значение одной строкой — в том же формате, что принимает <see cref="Set"/>.</summary>
        public static string Format(SerializedProperty p)
        {
            if (p == null) return "null";
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    switch (p.numericType)
                    {
                        case SerializedPropertyNumericType.UInt64: return p.ulongValue.ToString(Inv);
                        case SerializedPropertyNumericType.UInt32: return p.uintValue.ToString(Inv);
                        default: return p.longValue.ToString(Inv);
                    }
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return p.numericType == SerializedPropertyNumericType.Double ? p.doubleValue.ToString("R", Inv) : p.floatValue.ToString("R", Inv);
                case SerializedPropertyType.String: return p.stringValue ?? string.Empty;
                case SerializedPropertyType.Character: return ((char)p.intValue).ToString();
                case SerializedPropertyType.Color: return FormatColor(p.colorValue);
                case SerializedPropertyType.ObjectReference: return FormatReference(p.objectReferenceValue);
                case SerializedPropertyType.ExposedReference: return FormatReference(p.exposedReferenceValue);
                case SerializedPropertyType.LayerMask: return FormatLayerMask(p.intValue);
                case SerializedPropertyType.Enum: return FormatEnum(p);
                case SerializedPropertyType.Vector2: return Join(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3: return Join(p.vector3Value.x, p.vector3Value.y, p.vector3Value.z);
                case SerializedPropertyType.Vector4: return Join(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
                case SerializedPropertyType.Vector2Int: return JoinInt(p.vector2IntValue.x, p.vector2IntValue.y);
                case SerializedPropertyType.Vector3Int: return JoinInt(p.vector3IntValue.x, p.vector3IntValue.y, p.vector3IntValue.z);
                case SerializedPropertyType.Quaternion:
                {
                    var e = p.quaternionValue.eulerAngles;
                    return Join(e.x, e.y, e.z);
                }
                case SerializedPropertyType.Rect:
                {
                    var r = p.rectValue;
                    return Join(r.x, r.y, r.width, r.height);
                }
                case SerializedPropertyType.RectInt:
                {
                    var r = p.rectIntValue;
                    return JoinInt(r.x, r.y, r.width, r.height);
                }
                case SerializedPropertyType.Bounds:
                {
                    var b = p.boundsValue;
                    return Join(b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z);
                }
                case SerializedPropertyType.BoundsInt:
                {
                    var b = p.boundsIntValue;
                    return JoinInt(b.position.x, b.position.y, b.position.z, b.size.x, b.size.y, b.size.z);
                }
                case SerializedPropertyType.ArraySize: return p.intValue.ToString(Inv);
                case SerializedPropertyType.FixedBufferSize: return p.fixedBufferSize.ToString(Inv);
                case SerializedPropertyType.AnimationCurve:
                {
                    var curve = p.animationCurveValue;
                    return curve == null ? "null" : $"(AnimationCurve, ключей: {curve.length})";
                }
                case SerializedPropertyType.Gradient: return "(Gradient)";
                case SerializedPropertyType.Hash128: return p.hash128Value.ToString();
                case SerializedPropertyType.ManagedReference:
                {
                    var tn = p.managedReferenceFullTypename;
                    return string.IsNullOrEmpty(tn) ? "null" : "{" + ShortTypename(tn) + "}";
                }
                case SerializedPropertyType.Generic:
                    return IsList(p) ? $"[{p.arraySize}]" : "{…}";
                default:
                    return IsList(p) ? $"[{p.arraySize}]" : $"({p.propertyType})";
            }
        }

        // ================================================================ запись

        /// <summary>Присвоить одно поле и применить (одна запись Undo). Ошибка — <see cref="NexusCommandException"/>.</summary>
        public static void Set(SerializedObject so, string path, string value)
        {
            if (so == null) throw new ArgumentNullException(nameof(so));
            if (string.IsNullOrWhiteSpace(path)) throw new NexusCommandException("нужен путь поля (comp.get покажет пути)");
            so.Update();
            Assign(so, Find(so, path.Trim()), value ?? string.Empty);
            so.ApplyModifiedProperties();
        }

        /// <summary>
        /// Список <c>path=value</c>: все присваивания проверяются и ставятся, потом одно ApplyModifiedProperties
        /// (одна запись Undo). Ошибка в любом — ничего не применяется.
        /// </summary>
        public static void SetMany(SerializedObject so, IReadOnlyList<string> assignments)
        {
            if (so == null) throw new ArgumentNullException(nameof(so));
            if (assignments == null || assignments.Count == 0) throw new NexusCommandException("нужны присваивания path=value");

            so.Update();
            foreach (var a in assignments)
            {
                SplitAssignment(a, out var path, out var value);
                Assign(so, Find(so, path), value);
            }

            so.ApplyModifiedProperties();
        }

        /// <summary><c>path=value</c> → путь и значение (значение — как есть, с пробелами и запятыми).</summary>
        internal static void SplitAssignment(string assignment, out string path, out string value)
        {
            var a = assignment ?? string.Empty;
            var eq = a.IndexOf('=');
            if (eq <= 0) throw new NexusCommandException($"«{a}»: ожидается path=value (например m_Mass=2 или speed=3.5)");
            path = a.Substring(0, eq).Trim();
            value = a.Substring(eq + 1);
        }

        /// <summary>Поле по пути; для одного сегмента пробует и Unity-имя <c>m_Имя</c> (<c>mass</c> → <c>m_Mass</c>).</summary>
        internal static SerializedProperty Find(SerializedObject so, string path)
        {
            var p = so.FindProperty(path);
            if (p != null) return p;

            if (path.IndexOf('.') < 0 && !path.StartsWith("m_", StringComparison.Ordinal) && path.Length > 0)
            {
                p = so.FindProperty("m_" + char.ToUpperInvariant(path[0]) + path.Substring(1));
                if (p != null) return p;
            }

            throw new NexusCommandException($"Поля «{path}» нет у {TargetLabel(so)}. Поля: {TopLevel(so, path)}", NexusCommandContext.Exit.Failed);
        }

        private static void Assign(SerializedObject so, SerializedProperty p, string value)
        {
            var path = p.propertyPath;
            var text = value.Trim();
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    SetInteger(p, text);
                    return;
                case SerializedPropertyType.Boolean:
                    p.boolValue = UnityEdit.ParseBool(text, path);
                    return;
                case SerializedPropertyType.Float:
                {
                    if (!double.TryParse(text, NumberStyles.Float, Inv, out var d))
                        throw new NexusCommandException($"{path}: ожидается число (дробная часть через точку) — «{value}»");
                    if (p.numericType == SerializedPropertyNumericType.Double) p.doubleValue = d;
                    else p.floatValue = (float)d;
                    return;
                }
                case SerializedPropertyType.String:
                    // строка — как есть, без обрезки пробелов
                    p.stringValue = value;
                    return;
                case SerializedPropertyType.Character:
                    if (value.Length != 1) throw new NexusCommandException($"{path}: ожидается один символ — «{value}»");
                    p.intValue = value[0];
                    return;
                case SerializedPropertyType.Enum:
                    SetEnum(p, text);
                    return;
                case SerializedPropertyType.LayerMask:
                    p.intValue = ParseLayerMask(text, path);
                    return;
                case SerializedPropertyType.Color:
                    p.colorValue = ParseColor(text, path);
                    return;
                case SerializedPropertyType.Vector2:
                {
                    var n = UnityEdit.Numbers(text, 2, 2, path);
                    p.vector2Value = new Vector2((float)n[0], (float)n[1]);
                    return;
                }
                case SerializedPropertyType.Vector3:
                {
                    var n = UnityEdit.Numbers(text, 3, 3, path);
                    p.vector3Value = new Vector3((float)n[0], (float)n[1], (float)n[2]);
                    return;
                }
                case SerializedPropertyType.Vector4:
                {
                    var n = UnityEdit.Numbers(text, 4, 4, path);
                    p.vector4Value = new Vector4((float)n[0], (float)n[1], (float)n[2], (float)n[3]);
                    return;
                }
                case SerializedPropertyType.Vector2Int:
                {
                    var n = UnityEdit.Ints(text, 2, 2, path);
                    p.vector2IntValue = new Vector2Int(n[0], n[1]);
                    return;
                }
                case SerializedPropertyType.Vector3Int:
                {
                    var n = UnityEdit.Ints(text, 3, 3, path);
                    p.vector3IntValue = new Vector3Int(n[0], n[1], n[2]);
                    return;
                }
                case SerializedPropertyType.Quaternion:
                {
                    var n = UnityEdit.Numbers(text, 3, 4, path);
                    p.quaternionValue = n.Length == 3
                        ? Quaternion.Euler((float)n[0], (float)n[1], (float)n[2])
                        : new Quaternion((float)n[0], (float)n[1], (float)n[2], (float)n[3]);
                    return;
                }
                case SerializedPropertyType.Rect:
                {
                    var n = UnityEdit.Numbers(text, 4, 4, path);
                    p.rectValue = new Rect((float)n[0], (float)n[1], (float)n[2], (float)n[3]);
                    return;
                }
                case SerializedPropertyType.RectInt:
                {
                    var n = UnityEdit.Ints(text, 4, 4, path);
                    p.rectIntValue = new RectInt(n[0], n[1], n[2], n[3]);
                    return;
                }
                case SerializedPropertyType.Bounds:
                {
                    var n = UnityEdit.Numbers(text, 6, 6, path + " (центр x,y,z и размер x,y,z)");
                    p.boundsValue = new Bounds(new Vector3((float)n[0], (float)n[1], (float)n[2]), new Vector3((float)n[3], (float)n[4], (float)n[5]));
                    return;
                }
                case SerializedPropertyType.BoundsInt:
                {
                    var n = UnityEdit.Ints(text, 6, 6, path + " (позиция x,y,z и размер x,y,z)");
                    p.boundsIntValue = new BoundsInt(new Vector3Int(n[0], n[1], n[2]), new Vector3Int(n[3], n[4], n[5]));
                    return;
                }
                case SerializedPropertyType.ArraySize:
                    p.intValue = ParseSize(text, path);
                    return;
                case SerializedPropertyType.ObjectReference:
                    SetReference(so, p, text);
                    return;
                case SerializedPropertyType.ManagedReference:
                    if (!IsNullText(text))
                        throw new NexusCommandException($"{path}: [SerializeReference] задаётся здесь только как null; вложенные поля — {path}.<поле>=…", NexusCommandContext.Exit.Failed);
                    p.managedReferenceValue = null;
                    return;
                case SerializedPropertyType.Hash128:
                    p.hash128Value = Hash128.Parse(text);
                    return;
                case SerializedPropertyType.AnimationCurve:
                    throw new NexusCommandException($"{path}: AnimationCurve из CLI не задаётся — правьте в инспекторе или кодом.", NexusCommandContext.Exit.Failed);
                case SerializedPropertyType.Gradient:
                    throw new NexusCommandException($"{path}: Gradient из CLI не задаётся — правьте в инспекторе или кодом.", NexusCommandContext.Exit.Failed);
            }

            if (IsList(p))
            {
                // размер списка допускаем и на самом поле: items=3 — то же, что items.Array.size=3
                p.arraySize = ParseSize(text, path);
                return;
            }

            if (p.propertyType == SerializedPropertyType.Generic)
                throw new NexusCommandException($"{path}: составное поле — задайте вложенные: {path}.<поле>=… (список полей: comp.get … {path})", NexusCommandContext.Exit.Failed);
            throw new NexusCommandException($"{path}: тип {p.propertyType} из CLI не задаётся.", NexusCommandContext.Exit.Failed);
        }

        // ---------------------------------------------------------------- числа и перечисления

        private static void SetInteger(SerializedProperty p, string text)
        {
            var path = p.propertyPath;
            switch (p.numericType)
            {
                case SerializedPropertyNumericType.UInt64:
                    if (!ulong.TryParse(text, NumberStyles.Integer, Inv, out var ul)) throw new NexusCommandException($"{path}: ожидается целое ≥ 0 — «{text}»");
                    p.ulongValue = ul;
                    return;
                case SerializedPropertyNumericType.UInt32:
                    if (!uint.TryParse(text, NumberStyles.Integer, Inv, out var ui)) throw new NexusCommandException($"{path}: ожидается целое 0…{uint.MaxValue} — «{text}»");
                    p.uintValue = ui;
                    return;
            }

            if (!long.TryParse(text, NumberStyles.Integer, Inv, out var l)) throw new NexusCommandException($"{path}: ожидается целое — «{text}»");
            if (p.numericType == SerializedPropertyNumericType.Int32 && (l < int.MinValue || l > int.MaxValue))
                throw new NexusCommandException($"{path}: {l} не помещается в int");
            p.longValue = l;
        }

        private static int ParseSize(string text, string path)
        {
            if (!int.TryParse(text, NumberStyles.Integer, Inv, out var n) || n < 0)
                throw new NexusCommandException($"{path}: размер — целое ≥ 0 — «{text}»");
            return n;
        }

        private static void SetEnum(SerializedProperty p, string text)
        {
            var path = p.propertyPath;
            if (long.TryParse(text, NumberStyles.Integer, Inv, out var number))
            {
                p.intValue = (int)number;
                return;
            }

            var type = FieldType(p);
            if (type != null && type.IsEnum)
            {
                long combined = 0;
                foreach (var raw in text.Split('|', ','))
                {
                    var part = raw.Trim();
                    if (part.Length == 0) continue;
                    object parsed;
                    try
                    {
                        parsed = Enum.Parse(type, part, true);
                    }
                    catch (ArgumentException)
                    {
                        throw new NexusCommandException($"{path}: у {type.Name} нет «{part}». Есть: {string.Join(", ", Enum.GetNames(type))}");
                    }

                    combined |= Convert.ToInt64(parsed, Inv);
                }

                p.intValue = (int)combined;
                return;
            }

            // тип поля не нашёлся (встроенный компонент) — по именам, которые знает Unity
            if (text.IndexOf('|') >= 0)
                throw new NexusCommandException($"{path}: тип перечисления не определён — флаги задайте числом.");
            var names = p.enumNames;
            var display = p.enumDisplayNames;
            for (var i = 0; i < names.Length; i++)
                if (string.Equals(names[i], text, StringComparison.OrdinalIgnoreCase) ||
                    (i < display.Length && string.Equals(display[i], text, StringComparison.OrdinalIgnoreCase)))
                {
                    p.enumValueIndex = i;
                    return;
                }

            throw new NexusCommandException($"{path}: нет значения «{text}». Есть: {string.Join(", ", names)}");
        }

        private static string FormatEnum(SerializedProperty p)
        {
            var type = FieldType(p);
            if (type != null && type.IsEnum)
            {
                var boxed = Enum.ToObject(type, p.intValue);
                if (type.IsDefined(typeof(FlagsAttribute), false))
                {
                    var s = boxed.ToString();
                    // число вместо имён — есть биты без имени
                    return s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '-') ? p.intValue.ToString(Inv) : s.Replace(", ", "|");
                }

                return Enum.GetName(type, boxed) ?? p.intValue.ToString(Inv);
            }

            var names = p.enumNames;
            var index = p.enumValueIndex;
            return index >= 0 && index < names.Length ? names[index] : p.intValue.ToString(Inv);
        }

        // ---------------------------------------------------------------- слои и цвета

        private static int ParseLayerMask(string text, string path)
        {
            if (int.TryParse(text, NumberStyles.Integer, Inv, out var n)) return n;
            if (string.Equals(text, "Nothing", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(text, "Everything", StringComparison.OrdinalIgnoreCase)) return ~0;

            var mask = 0;
            foreach (var raw in text.Split('|', ','))
            {
                var name = raw.Trim();
                if (name.Length == 0) continue;
                var layer = LayerMask.NameToLayer(name);
                if (layer < 0) throw new NexusCommandException($"{path}: слоя «{name}» нет — добавьте: layer.add {name} (или числом: маска битов).");
                mask |= 1 << layer;
            }

            return mask;
        }

        private static string FormatLayerMask(int mask)
        {
            if (mask == 0) return "0";
            if (mask == ~0) return "Everything";
            var names = new List<string>();
            for (var i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name) || name.IndexOf('|') >= 0) return mask.ToString(Inv);
                names.Add(name);
            }

            return string.Join("|", names);
        }

        private static Color ParseColor(string text, string path)
        {
            if (text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '#'))
            {
                if (ColorUtility.TryParseHtmlString(text, out var c)) return c;
                throw new NexusCommandException($"{path}: цвет — #rrggbb[aa], имя (red) или r,g,b[,a] — «{text}»");
            }

            var n = UnityEdit.Numbers(text, 3, 4, path + " (r,g,b[,a] от 0 до 1)");
            return new Color((float)n[0], (float)n[1], (float)n[2], n.Length == 4 ? (float)n[3] : 1f);
        }

        private static string FormatColor(Color c)
        {
            // #rrggbbaa — если цвет точно ложится в 8 бит на канал (без HDR), иначе числа
            if (Byteish(c.r) && Byteish(c.g) && Byteish(c.b) && Byteish(c.a)) return "#" + ColorUtility.ToHtmlStringRGBA(c);
            return Join(c.r, c.g, c.b, c.a);
        }

        private static bool Byteish(float v)
        {
            if (v < 0f || v > 1f) return false;
            var scaled = v * 255f;
            return Mathf.Abs(scaled - Mathf.Round(scaled)) < 0.001f;
        }

        // ---------------------------------------------------------------- ссылки на объекты

        /// <summary>
        /// <c>null</c>; ассет — как понимает AssetQuery (путь Assets/…, guid); объект сцены — <c>go:&lt;spec&gt;</c>;
        /// компонент — <c>go:&lt;spec&gt;@Type</c>. Объект к полю компонента — берётся его компонент нужного типа.
        /// </summary>
        private static void SetReference(SerializedObject so, SerializedProperty p, string text)
        {
            var path = p.propertyPath;
            var fieldType = ReferenceType(p);
            Object value = null;

            if (!IsNullText(text))
            {
                if (text.StartsWith("go:", StringComparison.OrdinalIgnoreCase))
                {
                    var body = text.Substring(3).Trim();
                    string component = null;
                    var at = body.LastIndexOf('@');
                    if (at > 0)
                    {
                        component = body.Substring(at + 1).Trim();
                        body = body.Substring(0, at).Trim();
                    }

                    var go = UnityObjectQuery.Resolve(body);
                    value = component != null ? UnityObjectQuery.ResolveComponent(go, component) : Adapt(go, fieldType, path);
                    CheckSceneReference(so, go, path);
                }
                else
                {
                    // для поля-компонента ищем сам ассет (префаб), компонент возьмём с него
                    var query = fieldType != null && !typeof(Component).IsAssignableFrom(fieldType) ? fieldType : null;
                    value = Adapt(AssetQuery.Resolve(text, query), fieldType, path);
                }
            }

            p.objectReferenceValue = value;
            if (value != null && p.objectReferenceValue != value)
            {
                // Unity не ставит объект неподходящего типа (поле остаётся null)
                p.objectReferenceValue = null;
                throw new NexusCommandException($"{path}: {Describe(value)} не подходит к полю типа {fieldType?.Name ?? ReferenceTypeName(p) ?? "?"}", NexusCommandContext.Exit.Failed);
            }
        }

        private static Object Adapt(Object obj, Type fieldType, string path)
        {
            if (obj == null || fieldType == null || fieldType.IsInstanceOfType(obj)) return obj;
            if (obj is GameObject go && typeof(Component).IsAssignableFrom(fieldType))
            {
                var c = go.GetComponent(fieldType);
                if (c == null) throw new NexusCommandException($"{path}: у «{go.name}» нет компонента {fieldType.Name}", NexusCommandContext.Exit.Failed);
                return c;
            }

            if (obj is Component comp && fieldType == typeof(GameObject)) return comp.gameObject;
            return obj;
        }

        /// <summary>Ассет не может ссылаться на объект сцены, а объект одной сцены — на объект другой (Unity не сохранит).</summary>
        private static void CheckSceneReference(SerializedObject so, GameObject referenced, string path)
        {
            var target = so.targetObject;
            if (target == null) return;
            if (EditorUtility.IsPersistent(target))
                throw new NexusCommandException($"{path}: ассет не может ссылаться на объект сцены «{referenced.name}».", NexusCommandContext.Exit.Failed);

            GameObject owner = null;
            if (target is GameObject g) owner = g;
            else if (target is Component c) owner = c.gameObject;
            if (owner != null && owner.scene.IsValid() && referenced.scene.IsValid() && owner.scene != referenced.scene)
                throw new NexusCommandException($"{path}: ссылки между сценами Unity не сохраняет ({owner.scene.name} → {referenced.scene.name}).", NexusCommandContext.Exit.Failed);
        }

        private static string FormatReference(Object o)
        {
            if (o == null) return "null";
            if (EditorUtility.IsPersistent(o)) return AssetQuery.PathOf(o);
            if (o is GameObject go) return "go:" + UnityObjectQuery.PathOf(go);
            if (o is Component c) return "go:" + UnityObjectQuery.PathOf(c.gameObject) + "@" + UnityObjectQuery.ComponentSpec(c);
            return o.name;
        }

        private static string Describe(Object o)
        {
            if (o is Component c) return $"{c.GetType().Name} на «{c.gameObject.name}»";
            return $"{o.GetType().Name} «{o.name}»";
        }

        /// <summary>Тип поля-ссылки: по рефлексии, иначе по имени из <c>PPtr&lt;$Type&gt;</c>.</summary>
        private static Type ReferenceType(SerializedProperty p)
        {
            var t = FieldType(p);
            if (t != null && typeof(Object).IsAssignableFrom(t)) return t;

            var name = ReferenceTypeName(p);
            if (string.IsNullOrEmpty(name)) return null;
            Type found = null;
            foreach (var candidate in TypeCache.GetTypesDerivedFrom<Object>())
            {
                if (candidate.Name != name) continue;
                if (found != null) return null;
                found = candidate;
            }

            return found;
        }

        private static string ReferenceTypeName(SerializedProperty p)
        {
            var type = p.type ?? string.Empty;
            const string prefix = "PPtr<$";
            if (type.StartsWith(prefix, StringComparison.Ordinal) && type.EndsWith(">", StringComparison.Ordinal))
                return type.Substring(prefix.Length, type.Length - prefix.Length - 1);
            return null;
        }

        /// <summary>
        /// Объявленный тип поля по пути свойства (рефлексией от типа объекта). null — поле не C#
        /// (встроенный компонент) или за [SerializeReference].
        /// </summary>
        internal static Type FieldType(SerializedProperty p)
        {
            var target = p.serializedObject.targetObject;
            if (target == null) return null;
            var type = target.GetType();
            var parts = p.propertyPath.Split('.');
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "Array" && i + 1 < parts.Length && parts[i + 1].StartsWith("data[", StringComparison.Ordinal))
                {
                    type = ElementType(type);
                    if (type == null) return null;
                    i++;
                    continue;
                }

                var field = FindField(type, parts[i]);
                if (field == null) return null;
                type = field.FieldType;
            }

            return type;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }

            return null;
        }

        private static Type ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return type.GetGenericArguments()[0];
            return null;
        }

        // ---------------------------------------------------------------- дерево

        private static bool IsList(SerializedProperty p) => p.isArray && p.propertyType != SerializedPropertyType.String;

        private static bool HasFields(SerializedProperty p) =>
            (p.propertyType == SerializedPropertyType.Generic || p.propertyType == SerializedPropertyType.ManagedReference) && p.hasVisibleChildren;

        private static object Node(SerializedProperty p, int depth, int maxArray)
        {
            var d = new Dictionary<string, object>
            {
                ["path"] = p.propertyPath,
                ["name"] = p.name,
                ["type"] = p.type,
            };

            if (IsList(p))
            {
                var count = p.arraySize;
                d["count"] = count;
                if (depth > 0)
                {
                    var items = new List<object>();
                    var n = Math.Min(count, maxArray);
                    for (var i = 0; i < n; i++) items.Add(Node(p.GetArrayElementAtIndex(i), depth - 1, maxArray));
                    d["items"] = items;
                    if (count > n) d["truncated"] = count - n;
                }

                return d;
            }

            if (p.propertyType == SerializedPropertyType.ManagedReference) d["value"] = Format(p);
            if (HasFields(p))
            {
                if (depth > 0)
                {
                    var fields = new List<object>();
                    foreach (var c in Children(p)) fields.Add(Node(c, depth - 1, maxArray));
                    d["fields"] = fields;
                }
                else if (!d.ContainsKey("value"))
                {
                    d["value"] = "{…}";
                }

                return d;
            }

            d["value"] = Format(p);
            return d;
        }

        private static void PrintNode(NexusCommandContext ctx, SerializedProperty p, int depth, int maxArray, int level)
        {
            var indent = new string(' ', 2 * level);
            if (IsList(p))
            {
                var count = p.arraySize;
                ctx.Print($"{indent}{p.propertyPath} [{count}]{(depth > 0 || count == 0 ? string.Empty : " (--depth)")}");
                if (depth <= 0) return;
                var n = Math.Min(count, maxArray);
                for (var i = 0; i < n; i++) PrintNode(ctx, p.GetArrayElementAtIndex(i), depth - 1, maxArray, level + 1);
                if (count > n) ctx.Print($"{indent}  … ещё {count - n} (--max-array)");
                return;
            }

            if (HasFields(p))
            {
                var head = p.propertyType == SerializedPropertyType.ManagedReference ? " = " + Format(p) : string.Empty;
                if (depth <= 0)
                {
                    ctx.Print($"{indent}{p.propertyPath}{(head.Length > 0 ? head : " = {…}")} (--depth)");
                    return;
                }

                ctx.Print($"{indent}{p.propertyPath}{head}:");
                foreach (var c in Children(p)) PrintNode(ctx, c, depth - 1, maxArray, level + 1);
                return;
            }

            ctx.Print($"{indent}{p.propertyPath} = {OneLine(Format(p))}");
        }

        /// <summary>Видимые прямые потомки свойства (копии).</summary>
        private static List<SerializedProperty> Children(SerializedProperty p)
        {
            var result = new List<SerializedProperty>();
            var it = p.Copy();
            var end = p.GetEndProperty();
            if (!it.NextVisible(true)) return result;
            while (!SerializedProperty.EqualContents(it, end))
            {
                result.Add(it.Copy());
                if (!it.NextVisible(false)) break;
            }

            return result;
        }

        // ---------------------------------------------------------------- мелочи

        private static bool IsNullText(string text) =>
            text.Length == 0 || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "none", StringComparison.OrdinalIgnoreCase);

        private static string Join(params float[] values)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString("R", Inv));
            }

            return sb.ToString();
        }

        private static string JoinInt(params int[] values)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString(Inv));
            }

            return sb.ToString();
        }

        /// <summary>Длинные и многострочные строки — в одну строку до 200 знаков (только для печати).</summary>
        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            var one = s.Replace("\r", string.Empty).Replace("\n", "\\n");
            return one.Length > 200 ? one.Substring(0, 200) + $"… ({s.Length} симв.)" : one;
        }

        /// <summary><c>Assembly Namespace.Type</c> → <c>Namespace.Type</c>.</summary>
        private static string ShortTypename(string full)
        {
            var space = full.IndexOf(' ');
            return space >= 0 ? full.Substring(space + 1) : full;
        }

        private static string TargetLabel(SerializedObject so)
        {
            var t = so.targetObject;
            if (t == null) return "объекта";
            return t is Component c ? $"{c.GetType().Name} («{c.gameObject.name}»)" : $"{t.GetType().Name} «{t.name}»";
        }

        /// <summary>Видимые поля верхнего уровня (до 30), похожие на искомое — первыми.</summary>
        private static string TopLevel(SerializedObject so, string wanted)
        {
            var similar = new List<string>();
            var other = new List<string>();
            var leaf = wanted;
            var dot = leaf.LastIndexOf('.');
            if (dot >= 0) leaf = leaf.Substring(dot + 1);
            leaf = leaf.StartsWith("m_", StringComparison.Ordinal) ? leaf.Substring(2) : leaf;

            var it = so.GetIterator();
            if (it.NextVisible(true))
                do
                {
                    if (leaf.Length > 0 && it.name.IndexOf(leaf, StringComparison.OrdinalIgnoreCase) >= 0) similar.Add(it.propertyPath);
                    else other.Add(it.propertyPath);
                } while (it.NextVisible(false));

            similar.AddRange(other);
            if (similar.Count > 30) similar = similar.GetRange(0, 30);
            return similar.Count == 0 ? "(нет видимых)" : string.Join(", ", similar);
        }
    }
}
