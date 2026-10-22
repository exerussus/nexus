using System;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Реализация фабрики компонентов. С 0.4 элементы получают классы <c>nx-*</c>, а вид и
    /// состояния (наведение, нажатие, фокус, выбор) описаны в USS (NexusComponents.uss) через
    /// переменные палитры — без колбэков на каждом элементе, и смена палитры перекрашивает
    /// уже построенное. Выдаётся странице через Context как <see cref="IPageUi"/>.
    /// </summary>
    public sealed class NexusUi : IPageUi
    {
        public Button Button(NexusRole role, string text, Action onClick)
        {
            var b = new Button(() => onClick?.Invoke()) { text = text };
            ClearUnityButton(b);
            b.AddToClassList("nx-btn");
            b.AddToClassList(RoleClass("nx-btn", role));
            if (role == NexusRole.Primary || role == NexusRole.Ghost) Glow(b);
            return b;
        }

        public VisualElement Badge(NexusRole role, string text)
        {
            var l = new Label(text);
            l.AddToClassList("nx-badge");
            l.AddToClassList(RoleClass("nx-badge", role));
            return l;
        }

        public void Paint(VisualElement element, NexusRole role)
        {
            if (element == null) return;

            // Старые роли с элемента снимаем: Paint зовут и повторно, меняя роль.
            foreach (NexusRole r in Enum.GetValues(typeof(NexusRole))) element.RemoveFromClassList(RoleClass("nx-btn", r));
            if (element is Button button) ClearUnityButton(button);

            element.AddToClassList("nx-btn");
            element.AddToClassList(RoleClass("nx-btn", role));
        }

        public VisualElement Card()
        {
            var card = new VisualElement();
            card.AddToClassList("nx-card");
            Brackets(card);
            return card;
        }

        public TextField Input(string value, Action<string> onChanged)
        {
            var f = new TextField { value = value ?? string.Empty };
            f.AddToClassList("nx-input");
            Glow(f);
            if (onChanged != null) f.RegisterValueChangedCallback(e => onChanged(e.newValue));
            return f;
        }

        public VisualElement StatusPill(StatusKind kind, string text)
        {
            var pill = new Label(text);
            pill.AddToClassList("nx-pill");
            pill.AddToClassList(kind switch
            {
                StatusKind.Ok => "nx-pill--ok",
                StatusKind.Warning => "nx-pill--warning",
                StatusKind.Error => "nx-pill--error",
                _ => "nx-pill--info",
            });
            return pill;
        }

        public VisualElement Divider()
        {
            var line = new VisualElement();
            line.AddToClassList("nx-divider");
            return line;
        }

        public VisualElement Tabs(string[] labels, int selected, Action<int> onSelected)
        {
            var bar = new VisualElement();
            bar.AddToClassList("nx-tabs");

            var items = new Label[labels?.Length ?? 0];

            void Select(int index)
            {
                for (var i = 0; i < items.Length; i++) items[i].EnableInClassList("nx-tab--on", i == index);
            }

            for (var i = 0; i < items.Length; i++)
            {
                var index = i;
                var item = new Label(labels[i]);
                item.AddToClassList("nx-tab");
                item.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0) return;
                    Select(index);
                    onSelected?.Invoke(index);
                });
                items[i] = item;
                bar.Add(item);
            }

            Select(selected);
            return bar;
        }

        public VisualElement Segmented(string[] labels, int selected, Action<int> onSelected)
        {
            var bar = new VisualElement();
            bar.AddToClassList("nx-seg");

            var items = new Label[labels?.Length ?? 0];

            void Select(int index)
            {
                for (var i = 0; i < items.Length; i++) items[i].EnableInClassList("nx-seg__item--on", i == index);
            }

            for (var i = 0; i < items.Length; i++)
            {
                var index = i;
                var item = new Label(labels[i]);
                item.AddToClassList("nx-seg__item");
                item.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0) return;
                    Select(index);
                    onSelected?.Invoke(index);
                });
                items[i] = item;
                bar.Add(item);
            }

            Select(selected);
            return bar;
        }

        // ---------------------------------------------------------------- 0.4

        public Label Title(string text)
        {
            var metrics = NexusTheme.Metrics;
            var label = new Label(metrics.UpperTitles ? text?.ToUpperInvariant() : text);
            label.AddToClassList("nx-title");
            if (metrics.MonoTitles) ApplyMono(label);
            return label;
        }

        public Label Code(string text)
        {
            var label = new Label(text);
            label.AddToClassList("nx-code");
            ApplyMono(label);
            label.selection.isSelectable = true;
            return label;
        }

        public Label Tag(NexusTagColor color, string text)
        {
            var label = new Label(text);
            label.AddToClassList("nx-tag");
            label.AddToClassList("nx-tag--" + color.ToString().ToLowerInvariant());
            return label;
        }

        public VisualElement ListRow(Action onClick, bool selected = false)
        {
            var row = new VisualElement();
            row.AddToClassList("nx-row");
            row.EnableInClassList("nx-row--on", selected);
            if (onClick != null)
                row.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) onClick(); });
            return row;
        }

        public void Glow(VisualElement element) => NexusGlow.Attach(element);

        public void ApplyTheme(VisualElement root) => NexusTheme.ApplyTo(root);

        public void Brackets(VisualElement element)
        {
            if (element == null || element.Q(className: "nx-bracket") != null) return;

            // Уголки — в hierarchy, а не в contentContainer: у составных элементов они не попадут
            // в содержимое, и Clear() содержимого их не снимет.
            foreach (var corner in new[] { "nx-bracket--tl", "nx-bracket--br" })
            {
                var bracket = new VisualElement { pickingMode = PickingMode.Ignore };
                bracket.AddToClassList("nx-bracket");
                bracket.AddToClassList(corner);
                element.hierarchy.Add(bracket);
            }
        }

        // ---------------------------------------------------------------- помощники

        /// <summary>Моноширинный шрифт хаба (JetBrains Mono) — для кода и заголовков Neon.</summary>
        public static void ApplyMono(VisualElement element) => NexusFonts.ApplyMono(element);

        private static string RoleClass(string block, NexusRole role) => block + "--" + role.ToString().ToLowerInvariant();

        /// <summary>У штатной кнопки Unity свои фон и рамка из темы редактора — убираем, чтобы правил USS.</summary>
        private static void ClearUnityButton(Button b)
        {
            b.RemoveFromClassList(UnityEngine.UIElements.Button.ussClassName);
        }
    }

    /// <summary>
    /// Свечение (Painter2D): мягкие обводки вокруг элемента при наведении и фокусе. USS теней не
    /// умеет, поэтому ореол рисуется кодом — только когда палитра светится (токен Glow не прозрачный).
    /// </summary>
    internal sealed class NexusGlow
    {
        private readonly VisualElement _element;
        private bool _hover;
        private bool _focus;

        private NexusGlow(VisualElement element) => _element = element;

        public static void Attach(VisualElement element)
        {
            if (element == null || element.ClassListContains("nx-glow")) return;
            element.AddToClassList("nx-glow");

            var glow = new NexusGlow(element);
            element.RegisterCallback<PointerEnterEvent>(_ => glow.Set(ref glow._hover, true));
            element.RegisterCallback<PointerLeaveEvent>(_ => glow.Set(ref glow._hover, false));
            element.RegisterCallback<FocusInEvent>(_ => glow.Set(ref glow._focus, true));
            element.RegisterCallback<FocusOutEvent>(_ => glow.Set(ref glow._focus, false));
            element.generateVisualContent += glow.Draw;
        }

        private void Set(ref bool flag, bool value)
        {
            if (flag == value) return;
            flag = value;
            _element.MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext ctx)
        {
            if (!_hover && !_focus) return;
            if (!_element.enabledInHierarchy) return;

            var color = NexusTheme.Get(NexusToken.Glow);
            if (color.a <= 0.001f) return;

            var layout = _element.layout;
            var w = layout.width;
            var h = layout.height;
            if (w <= 0f || h <= 0f) return;

            var radius = NexusTheme.Metrics.RadiusM;
            var p = ctx.painter2D;

            // Три обводки наружу с затуханием — дёшево и похоже на размытую тень.
            for (var i = 1; i <= 3; i++)
            {
                var grow = i * 1.5f;
                p.strokeColor = new Color(color.r, color.g, color.b, color.a / i);
                p.lineWidth = 1.5f;
                RoundRect(p, -grow, -grow, w + grow * 2f, h + grow * 2f, radius + grow);
                p.Stroke();
            }
        }

        private static void RoundRect(Painter2D p, float x, float y, float w, float h, float r)
        {
            r = Mathf.Min(r, Mathf.Min(w, h) * 0.5f);
            p.BeginPath();
            p.MoveTo(new Vector2(x + r, y));
            p.LineTo(new Vector2(x + w - r, y));
            p.ArcTo(new Vector2(x + w, y), new Vector2(x + w, y + r), r);
            p.LineTo(new Vector2(x + w, y + h - r));
            p.ArcTo(new Vector2(x + w, y + h), new Vector2(x + w - r, y + h), r);
            p.LineTo(new Vector2(x + r, y + h));
            p.ArcTo(new Vector2(x, y + h), new Vector2(x, y + h - r), r);
            p.LineTo(new Vector2(x, y + r));
            p.ArcTo(new Vector2(x, y), new Vector2(x + r, y), r);
            p.ClosePath();
        }
    }
}
