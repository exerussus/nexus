using System;
using System.Text;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Классы-токены хаба: цвет текста, фона и рамки любым токеном палитры через USS
    /// (<c>nx-fg--accent</c>, <c>nx-bg--bg-raised</c>, <c>nx-bd--divider</c>). Значения — переменные
    /// активной палитры, поэтому страница не держит цвета в коде: смена палитры перекрашивает
    /// уже построенное, а инлайн-стили не перебивают состояния компонентов.
    /// </summary>
    /// <remarks>
    /// Имена считаются один раз на токен — вызовы не аллоцируют. <c>Set*</c> снимают прежний класс
    /// того же вида, поэтому годятся для меняющегося состояния (статус «ок → ошибка»).
    /// Для своих USS-правил страницы — переменные <c>var(--nx-…)</c>: <see cref="Var"/>.
    /// </remarks>
    public static class NexusClass
    {
        private static readonly string[] Names;
        private static readonly string[] FgNames;
        private static readonly string[] BgNames;
        private static readonly string[] BdNames;

        static NexusClass()
        {
            var max = 0;
            foreach (NexusToken token in Enum.GetValues(typeof(NexusToken))) max = Math.Max(max, (int)token);

            Names = new string[max + 1];
            FgNames = new string[max + 1];
            BgNames = new string[max + 1];
            BdNames = new string[max + 1];

            foreach (NexusToken token in Enum.GetValues(typeof(NexusToken)))
            {
                var i = (int)token;
                Names[i] = Kebab(token.ToString());
                FgNames[i] = "nx-fg--" + Names[i];
                BgNames[i] = "nx-bg--" + Names[i];
                BdNames[i] = "nx-bd--" + Names[i];
            }
        }

        /// <summary>Имя токена в USS: BgHard → bg-hard.</summary>
        public static string Name(NexusToken token) => Names[(int)token];

        /// <summary>Переменная палитры: BgHard → --nx-bg-hard.</summary>
        public static string Var(NexusToken token) => "--nx-" + Names[(int)token];

        /// <summary>Класс цвета текста.</summary>
        public static string Fg(NexusToken token) => FgNames[(int)token];

        /// <summary>Класс цвета фона.</summary>
        public static string Bg(NexusToken token) => BgNames[(int)token];

        /// <summary>Класс цвета рамки (все стороны; ширину задаёт сам элемент).</summary>
        public static string Border(NexusToken token) => BdNames[(int)token];

        /// <summary>Цвет текста токеном; прежний класс цвета текста снимается.</summary>
        public static void SetFg(VisualElement element, NexusToken token) => Swap(element, FgNames, (int)token);

        /// <summary>Фон токеном; прежний класс фона снимается.</summary>
        public static void SetBg(VisualElement element, NexusToken token) => Swap(element, BgNames, (int)token);

        /// <summary>Рамка токеном; прежний класс рамки снимается.</summary>
        public static void SetBorder(VisualElement element, NexusToken token) => Swap(element, BdNames, (int)token);

        private static void Swap(VisualElement element, string[] names, int index)
        {
            if (element == null) return;
            for (var i = 0; i < names.Length; i++)
                if (i != index && names[i] != null) element.RemoveFromClassList(names[i]);
            element.AddToClassList(names[index]);
        }

        private static string Kebab(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (char.IsUpper(ch) && i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }
    }
}
