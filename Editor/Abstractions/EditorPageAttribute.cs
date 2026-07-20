using System;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Помечает класс страницы. Это ВЕДУЩИЙ источник метаданных, пока код страницы
    /// присутствует в проекте: хаб регенерирует из него манифест плагина. Манифест
    /// (на диске) — лишь проекция, которая умеет пережить отсутствие кода (evict).
    ///
    /// <see cref="Id"/> — стабильный идентификатор, НЕ привязанный к имени типа:
    /// тип можно переименовать/переместить, связь с State/Preserve/настройками
    /// держится по нему (и по guid в манифесте), а не по Type.FullName.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class EditorPageAttribute : Attribute
    {
        public string       Id       { get; }
        public string       Name     { get; }
        public PageCategory Category { get; }
        public int          Order    { get; }
        public string       Icon     { get; }

        public EditorPageAttribute(
            string id,
            string name,
            PageCategory category = PageCategory.Infrastructure,
            int order = 0,
            string icon = null)
        {
            Id       = id;
            Name     = name;
            Category = category;
            Order    = order;
            Icon     = icon;
        }
    }
}
