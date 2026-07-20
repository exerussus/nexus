using UnityEngine;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Доступ к теме для страницы. Страница берёт цвет по семантическому токену и
    /// остаётся изолированной — она не знает ни про палитру, ни про сборку Theme.
    /// </summary>
    public interface IPageTheme
    {
        Color Get(NexusToken token);

        /// <summary>Полный набор цветов роли (для собственных виджетов страницы).
        /// Готовые кнопки/бейджи проще брать через <see cref="IPageUi"/>.</summary>
        NexusRoleColors GetRole(NexusRole role);
    }
}
