using UnityEngine;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Полный набор цветов одной роли — состояния заданы ЯВНО (а не выводятся
    /// множителем), чтобы палитра давала точный контроль над «фирменным» видом.
    /// Палитра хранит такой набор на каждую роль; страница/фабрика берёт готовое.
    /// </summary>
    public struct NexusRoleColors
    {
        public Color Bg;        // фон в покое
        public Color Fg;        // текст/иконка
        public Color Hover;     // фон при наведении
        public Color Pressed;   // фон при нажатии
        public Color Disabled;  // фон в выключенном состоянии
        public Color Border;    // рамка (для Ghost — основной видимый цвет)
        public Color Soft;      // низкоальфовая заливка (чипы/бейджи, ховер ghost-кнопок)
    }
}
