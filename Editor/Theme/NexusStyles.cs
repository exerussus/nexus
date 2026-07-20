using System;
using Exerussus.Nexus.Abstractions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Состояния взаимодействия (наведение/нажатие) для движкового UI. Тема у нас
    /// резолвится в C#, USS-переменных нет — поэтому hover/pressed делаем на
    /// pointer-событиях, меняя фон по токенам. Так смена палитры меняет и эти
    /// состояния, оставаясь единым источником цвета.
    /// </summary>
    public static class NexusStyles
    {
        /// <summary>Кнопка с фоном по теме и состояниями normal/hover/pressed.</summary>
        public static Button Button(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.color = NexusTheme.Get(NexusToken.TextNormal);
            b.style.paddingLeft = 8f;  b.style.paddingRight = 8f;
            b.style.paddingTop = 2f;   b.style.paddingBottom = 2f;
            b.style.marginLeft = 2f;   b.style.marginRight = 2f;
            SetBorder(b, NexusTheme.Get(NexusToken.Border), 1f, 3f);

            MakePressable(b,
                baseColor: () => NexusTheme.Get(NexusToken.BgRaised),
                hover:     NexusTheme.Get(NexusToken.Hover),
                pressed:   NexusTheme.Get(NexusToken.Pressed));
            return b;
        }

        /// <summary>Навесить hover/press на любой элемент. baseColor — функция (чтобы
        /// учитывать «выбран ли элемент»): на ней держится фон в покое.</summary>
        public static void MakePressable(VisualElement e, Func<Color> baseColor, Color hover, Color pressed)
        {
            var over = false;
            var down = false;

            void Apply() => e.style.backgroundColor = down ? pressed : over ? hover : baseColor();

            e.RegisterCallback<PointerEnterEvent>(_ => { over = true;  Apply(); });
            e.RegisterCallback<PointerLeaveEvent>(_ => { over = false; down = false; Apply(); });
            e.RegisterCallback<PointerDownEvent>(_ => { down = true;  Apply(); });
            e.RegisterCallback<PointerUpEvent>(_   => { down = false; Apply(); });
            Apply();
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
