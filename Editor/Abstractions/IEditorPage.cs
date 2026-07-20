using UnityEngine.UIElements;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Контракт страницы. Хаб сам зовёт эти хуки в строгом порядке; страница внутри
    /// пассивна и тянется наружу только через <see cref="IPageContext"/>.
    ///
    /// ДВЕ ОСИ жизненного цикла:
    ///   • «активная вкладка»     → <see cref="OnEnter"/> / <see cref="OnExit"/>;
    ///   • «окно в фокусе при этом» → <see cref="OnFocus"/> / <see cref="OnUnfocus"/>.
    /// Focus/Unfocus ВСЕГДА вложены в Enter/Exit: сфокусированной может быть только
    /// активная страница. Внутри одного Enter циклов фокуса может быть много.
    ///
    /// Полный порядок: Initialize → BuildUI → OnEnter → (OnFocus ⇄ OnUnfocus)* →
    /// OnExit → OnDispose. Выключение (упаковка в Preserve): CanClose →
    /// (OnUnfocus если в фокусе) → OnExit → OnPrePack. OnRefresh — независимо.
    ///
    /// Удобнее наследоваться от <see cref="EditorPageBase"/> (Context, Track, no-op хуки).
    /// </summary>
    public interface IEditorPage
    {
        /// <summary>Один раз, сразу после создания, до UI. Только дешёвая настройка —
        /// контекст уже доступен.</summary>
        void Initialize(IPageContext context);

        /// <summary>Один раз. Построить корневой элемент. Тяжёлое (загрузку данных)
        /// откладываем до <see cref="OnEnter"/>.</summary>
        VisualElement BuildUI();

        /// <summary>Страница стала активной вкладкой (на экране). Подписки, загрузка
        /// данных «пока видно».</summary>
        void OnEnter();

        /// <summary>Активная вкладка получила фокус окна. Возобновить высокочастотную
        /// работу: пуллинг, слежение за выделением, перепроверку внешних изменений.</summary>
        void OnFocus();

        /// <summary>Окно потеряло фокус (вкладка ещё активна) ИЛИ вкладку сейчас
        /// скроют/выключат. Остановить высокочастотную работу. Парен с OnFocus.</summary>
        void OnUnfocus();

        /// <summary>Перестала быть активной вкладкой. Снять то, что повесил OnEnter.
        /// Инстанс остаётся в кэше — повторный Enter дёшев.</summary>
        void OnExit();

        /// <summary>Явный Refresh пользователем. Независимо от фокуса.</summary>
        void OnRefresh();

        /// <summary>Вето перед выключением/закрытием (есть несохранённое?).
        /// false — отложить.</summary>
        bool CanClose();

        /// <summary>Последний шанс ПЕРЕД упаковкой в Preserve на ВЫКЛЮЧЕНИИ —
        /// сбросить данные в свои файлы. Только при выключении, не при переключении
        /// и не при закрытии окна.</summary>
        void OnPrePack();

        /// <summary>Окончательное освобождение ресурсов страницы.</summary>
        void OnDispose();
    }
}
