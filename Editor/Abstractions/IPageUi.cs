using System;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Фабрика типовых UI-элементов в едином стиле хаба, выдаётся странице через
    /// <see cref="IPageContext.Ui"/>. Страница больше не пересобирает primary/alarm/…
    /// кнопки руками — берёт готовое по роли. Контракт живёт в Abstractions, реализация —
    /// в движке; страница зависит от интерфейса, не от сборки Theme (изоляция цела).
    /// </summary>
    public interface IPageUi
    {
        /// <summary>Кнопка в стиле роли (фон/текст/контур + hover/pressed).</summary>
        Button Button(NexusRole role, string text, Action onClick);

        /// <summary>Небольшой бейдж-«пилюля» в цвете роли.</summary>
        VisualElement Badge(NexusRole role, string text);

        /// <summary>Покрасить ПРОИЗВОЛЬНЫЙ элемент под роль (фон/текст/контур + состояния) —
        /// для собственных виджетов, которых нет среди готовых фабрик.</summary>
        void Paint(VisualElement element, NexusRole role);

        /// <summary>Приподнятый контейнер-карточка (BgRaised + Border, ховер SurfaceHover).
        /// Возвращает контейнер — наполняйте его своими элементами.</summary>
        VisualElement Card();

        /// <summary>Тематизированное поле ввода (BgHard + Border, рамка Focus в фокусе).</summary>
        TextField Input(string value, System.Action<string> onChanged);

        /// <summary>Статус-«пилюля»: мягкая заливка + сплошной цвет статуса (текст/рамка).</summary>
        VisualElement StatusPill(StatusKind kind, string text);

        /// <summary>Тонкий разделитель (хайрлайн) цветом Divider.</summary>
        VisualElement Divider();

        /// <summary>
        /// Вкладки верхнего уровня: строка текстовых кнопок, активная подчёркнута акцентом.
        /// Клик переключает выделение и вызывает <paramref name="onSelected"/> с индексом.
        /// </summary>
        VisualElement Tabs(string[] labels, int selected, Action<int> onSelected);

        /// <summary>
        /// Сегмент-контрол для подвкладок и переключателей режима: «пилюли» на мягком фоне,
        /// активная приподнята. Визуально отличается от <see cref="Tabs"/>, чтобы уровни не путались.
        /// </summary>
        VisualElement Segmented(string[] labels, int selected, Action<int> onSelected);

        // ---- 0.4 ----

        /// <summary>Заголовок карточки/раздела в стиле палитры (у Neon — моноширинный капслок).</summary>
        Label Title(string text);

        /// <summary>Строка кода/сниппета на утопленном фоне.</summary>
        Label Code(string text);

        /// <summary>Метка-плашка цвета <paramref name="color"/> (аргумент, живое значение, форма…).</summary>
        Label Tag(NexusTagColor color, string text);

        /// <summary>Строка списка: ховер поверхностью; клик — <paramref name="onClick"/>. <c>selected</c> — подсветка выбора.</summary>
        VisualElement ListRow(Action onClick, bool selected = false);

        /// <summary>Свечение элемента при наведении и фокусе (если палитра светится).</summary>
        void Glow(VisualElement element);

        /// <summary>Стили хаба на корне своего окна (поповеры, утилитные окна страницы).</summary>
        void ApplyTheme(VisualElement root);

        /// <summary>HUD-уголки на углах элемента (если у палитры они есть). Элемент становится position: relative-контейнером.</summary>
        void Brackets(VisualElement element);
    }
}
