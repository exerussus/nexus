using System;
using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Реализация шины. Это stateful-компонент ЯДРА (держит подписки = состояние),
    /// с явным владельцем — окном/хабом, который его создаёт. Намеренно НЕ статический
    /// «сервис»: у сервисов состояния быть не должно.
    ///
    /// Подписка может знать своего владельца (страницу): хост подписывает страницы через
    /// <see cref="Subscribe{T}(Action{T}, Action{Exception})"/>, и упавший обработчик помечает
    /// faulted именно ту страницу, а не только пишет в лог.
    /// </summary>
    public sealed class PageMessageBus : IPageMessageBus
    {
        private sealed class Handler
        {
            public Delegate          Action;
            public Action<Exception> OnFault;   // null — владелец неизвестен: только лог
            public bool              Removed;   // отписан (в т.ч. посреди рассылки)
        }

        private readonly Dictionary<Type, List<Handler>> _subs = new Dictionary<Type, List<Handler>>();

        public IDisposable Subscribe<T>(Action<T> handler) => Subscribe(handler, null);

        /// <summary>Подписка с обработчиком падения: исключение подписчика уходит владельцу.</summary>
        internal IDisposable Subscribe<T>(Action<T> handler, Action<Exception> onFault)
        {
            if (handler == null) return new Subscription(null, null, null);

            if (!_subs.TryGetValue(typeof(T), out var list))
                _subs[typeof(T)] = list = new List<Handler>();
            var h = new Handler { Action = handler, OnFault = onFault };
            list.Add(h);

            return new Subscription(this, typeof(T), h);
        }

        public void Publish<T>(T message)
        {
            if (!_subs.TryGetValue(typeof(T), out var list)) return;

            // копия — подписчик может отписаться/подписаться внутри обработчика; отписанный по ходу
            // рассылки (в т.ч. упавшей страницей) больше не вызывается
            foreach (var h in list.ToArray())
            {
                if (h.Removed) continue;
                try { ((Action<T>)h.Action).Invoke(message); }
                catch (Exception ex)
                {
                    if (h.OnFault == null)
                    {
                        NexusDiagnostics.Error("Обработчик шины упал", ex);
                        continue;
                    }

                    try { h.OnFault(ex); }
                    catch (Exception inner) { NexusDiagnostics.Error("Обработка падения подписчика шины", inner); }
                }
            }
        }

        private void Unsubscribe(Type type, Handler handler)
        {
            handler.Removed = true;
            if (_subs.TryGetValue(type, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0) _subs.Remove(type);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private PageMessageBus _bus;
            private readonly Type _type;
            private readonly Handler _handler;

            public Subscription(PageMessageBus bus, Type type, Handler handler)
            {
                _bus = bus; _type = type; _handler = handler;
            }

            public void Dispose()
            {
                _bus?.Unsubscribe(_type, _handler);
                _bus = null;
            }
        }
    }
}
