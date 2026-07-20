using Exerussus.Nexus.Abstractions;

namespace Exerussus.Nexus.Services
{
    /// <summary>
    /// Демонстрационный сервис: статический, stateless, без полей. Версия объявлена
    /// в атрибуте (истина); service-manifest.json её зеркалит. Страницы зовут его
    /// напрямую — отсюда и компиляторный уровень гейта: нет сервиса → код страницы
    /// не соберётся.
    /// </summary>
    [Service("logformat", "1.0")]
    public static class LogFormatService
    {
        /// <summary>Поставить временную метку перед сообщением.</summary>
        public static string Stamp(string message)
            => $"[{System.DateTime.Now:HH:mm:ss}] {message}";
    }
}
