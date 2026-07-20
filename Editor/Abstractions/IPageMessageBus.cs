using System;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Шина межстраничных сообщений. Это канал ОБЩИХ ФАКТОВ (события), а НЕ способ
    /// одной странице командовать другой — иначе через шину воссоздалась бы
    /// зависимость страница→страница, которую движок запрещает. Страницы остаются
    /// изолированными: знают про типы сообщений, не про друг друга.
    /// </summary>
    public interface IPageMessageBus
    {
        /// <summary>Подписаться на сообщения типа T. Возвращает отписку (Dispose).</summary>
        IDisposable Subscribe<T>(Action<T> handler);

        /// <summary>Опубликовать сообщение всем подписчикам типа T.</summary>
        void Publish<T>(T message);
    }
}
