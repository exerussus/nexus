using System;
using Exerussus.Nexus.Abstractions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Реализация фабрики компонентов: строит типовые элементы из цветов роли активной
    /// палитры. Роль резолвится в момент построения, поэтому смена палитры отражается на
    /// новых/перестроенных элементах. Выдаётся странице через Context как <see cref="IPageUi"/>,
    /// так что страница пользуется единым стилем, не ссылаясь на сборку Theme.
    /// </summary>
    public sealed class NexusUi : IPageUi
    {
        public Button Button(NexusRole role, string text, Action onClick)
        {
            var b = new Button(() => onClick?.Invoke()) { text = text };
            var c = NexusTheme.GetRole(role);

            b.style.color = c.Fg;
            b.style.paddingLeft = 10f; b.style.paddingRight = 10f;
            b.style.paddingTop = 3f;   b.style.paddingBottom = 3f;
            b.style.marginLeft = 2f;   b.style.marginRight = 2f;
            SetBorder(b, c.Border, 1f, 3f);

            // фон/состояния — через общий помощник (единый источник интеракций)
            NexusStyles.MakePressable(b, () => c.Bg, c.Hover, c.Pressed);
            return b;
        }

        public VisualElement Badge(NexusRole role, string text)
        {
            var c = NexusTheme.GetRole(role);
            var l = new Label(text);
            l.style.color = c.Fg;
            l.style.backgroundColor = c.Bg;
            l.style.paddingLeft = 6f; l.style.paddingRight = 6f;
            l.style.paddingTop = 1f;  l.style.paddingBottom = 1f;
            l.style.fontSize = 10f;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            SetBorder(l, c.Border, 1f, 8f);   // «пилюля»
            return l;
        }

        public void Paint(VisualElement element, NexusRole role)
        {
            if (element == null) return;
            var c = NexusTheme.GetRole(role);
            element.style.color = c.Fg;
            SetBorder(element, c.Border, 1f, 3f);
            NexusStyles.MakePressable(element, () => c.Bg, c.Hover, c.Pressed);
        }

        public VisualElement Card()
        {
            var card = new VisualElement();
            var bg = NexusTheme.Get(NexusToken.BgRaised);
            card.style.backgroundColor = bg;
            card.style.paddingLeft = 10f; card.style.paddingRight = 10f;
            card.style.paddingTop = 8f;   card.style.paddingBottom = 8f;
            card.style.marginBottom = 6f;
            SetBorder(card, NexusTheme.Get(NexusToken.Border), 1f, 5f);

            // только ховер поверхности (карточка не «нажимается»)
            var hover = NexusTheme.Get(NexusToken.SurfaceHover);
            card.RegisterCallback<PointerEnterEvent>(_ => card.style.backgroundColor = hover);
            card.RegisterCallback<PointerLeaveEvent>(_ => card.style.backgroundColor = bg);
            return card;
        }

        public TextField Input(string value, Action<string> onChanged)
        {
            var f = new TextField { value = value ?? string.Empty };
            f.style.color = NexusTheme.Get(NexusToken.TextNormal);
            f.style.backgroundColor = NexusTheme.Get(NexusToken.BgHard);
            var border = NexusTheme.Get(NexusToken.Border);
            var focus  = NexusTheme.Get(NexusToken.Focus);
            SetBorder(f, border, 1f, 3f);

            // рамка фокуса
            f.RegisterCallback<FocusInEvent>(_  => SetBorder(f, focus,  1f, 3f));
            f.RegisterCallback<FocusOutEvent>(_ => SetBorder(f, border, 1f, 3f));
            if (onChanged != null) f.RegisterValueChangedCallback(e => onChanged(e.newValue));
            return f;
        }

        public VisualElement StatusPill(StatusKind kind, string text)
        {
            NexusToken soft, solid;
            switch (kind)
            {
                case StatusKind.Ok:      soft = NexusToken.OkSoft;      solid = NexusToken.Ok;      break;
                case StatusKind.Warning: soft = NexusToken.WarningSoft; solid = NexusToken.Warning; break;
                case StatusKind.Error:   soft = NexusToken.ErrorSoft;   solid = NexusToken.Error;   break;
                default:                 soft = NexusToken.AccentSoft;  solid = NexusToken.Accent;  break;
            }

            var pill = new Label(text);
            pill.style.color = NexusTheme.Get(solid);
            pill.style.backgroundColor = NexusTheme.Get(soft);
            pill.style.paddingLeft = 6f; pill.style.paddingRight = 6f;
            pill.style.paddingTop = 1f;  pill.style.paddingBottom = 1f;
            pill.style.fontSize = 10f;
            pill.style.unityTextAlign = TextAnchor.MiddleCenter;
            SetBorder(pill, NexusTheme.Get(solid), 1f, 8f);
            return pill;
        }

        public VisualElement Divider()
        {
            var line = new VisualElement();
            line.style.height = 1f;
            line.style.backgroundColor = NexusTheme.Get(NexusToken.Divider);
            line.style.marginTop = 4f; line.style.marginBottom = 4f;
            return line;
        }

        private static void SetBorder(VisualElement e, Color c, float w, float r)
        {
            e.style.borderTopWidth = w;    e.style.borderRightWidth = w;
            e.style.borderBottomWidth = w; e.style.borderLeftWidth = w;
            e.style.borderTopColor = c;    e.style.borderRightColor = c;
            e.style.borderBottomColor = c; e.style.borderLeftColor = c;
            e.style.borderTopLeftRadius = r;    e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }
    }
}
