using System;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Помечает СТАТИЧЕСКИЙ stateless-класс как сервис-контракт. Версия объявляется
    /// ЗДЕСЬ (в коде) — это источник истины; service-manifest.json её лишь зеркалит
    /// (кэш для гейта). Сервис не тоглится: либо его код скомпилирован, либо нет.
    ///
    /// Версия — "major.minor". Мажор бампается при ломающем изменении контракта.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ServiceAttribute : Attribute
    {
        public string Id      { get; }
        public string Version { get; }

        public ServiceAttribute(string id, string version)
        {
            Id      = id;
            Version = version;
        }
    }
}
