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
    /// </summary>
    public sealed class PageMessageBus : IPageMessageBus
    {
        private readonly Dictionary<Type, List<Delegate>> _subs = new Dictionary<Type, List<Delegate>>();

        public IDisposable Subscribe<T>(Action<T> handler)
        {
            if (handler == null) return new Subscription(null, null, null);

            if (!_subs.TryGetValue(typeof(T), out var list))
                _subs[typeof(T)] = list = new List<Delegate>();
            list.Add(handler);

            return new Subscription(this, typeof(T), handler);
        }

        public void Publish<T>(T message)
        {
            if (!_subs.TryGetValue(typeof(T), out var list)) return;

            // копия — подписчик может отписаться/подписаться внутри обработчика
            foreach (var d in list.ToArray())
            {
                try { ((Action<T>)d).Invoke(message); }
                catch (Exception ex) { Debug.LogError($"[Nexus] Обработчик шины упал: {ex.Message}"); }
            }
        }

        private void Unsubscribe(Type type, Delegate handler)
        {
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
            private readonly Delegate _handler;

            public Subscription(PageMessageBus bus, Type type, Delegate handler)
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
