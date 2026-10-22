using System;
using Exerussus.Nexus.Abstractions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Помощники стиля движкового UI. С 0.4 состояния компонентов описаны в USS
    /// (NexusComponents.uss); <see cref="MakePressable"/> остаётся для своих виджетов страниц,
    /// которые красятся из C#.
    /// </summary>
    public static class NexusStyles
    {
        /// <summary>
        /// Обычная кнопка хаба (роль Common, малый размер). Вид и состояния — из USS
        /// (<see cref="NexusTheme.ApplyTo"/> на корне окна).
        /// </summary>
        public static Button Button(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.RemoveFromClassList(UnityEngine.UIElements.Button.ussClassName);
            b.AddToClassList("nx-btn");
            b.AddToClassList("nx-btn--sm");
            b.AddToClassList("nx-btn--common");
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
