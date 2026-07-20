using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// База для страниц: хранит <see cref="Context"/>, даёт <see cref="Track"/> для
    /// авто-отписки и no-op реализации хуков. Утечку подписки так сделать трудно —
    /// всё, что обёрнуто в Track, хаб гарантированно освободит в OnDispose.
    /// </summary>
    public abstract class EditorPageBase : IEditorPage
    {
        protected IPageContext Context { get; private set; }

        private readonly List<IDisposable> _tracked = new List<IDisposable>();

        public void Initialize(IPageContext context)
        {
            Context = context;
            OnInitialize();
        }

        /// <summary>Дёшево проинициализироваться (контекст уже доступен).</summary>
        protected virtual void OnInitialize() { }

        public abstract VisualElement BuildUI();

        public virtual void OnEnter()   { }
        public virtual void OnFocus()   { }
        public virtual void OnUnfocus() { }
        public virtual void OnExit()    { }
        public virtual void OnRefresh() { }
        public virtual bool CanClose()  => true;
        public virtual void OnPrePack() { }

        /// <summary>Зарегистрировать ресурс/подписку на авто-освобождение в OnDispose.</summary>
        protected void Track(IDisposable disposable)
        {
            if (disposable != null) _tracked.Add(disposable);
        }

        public void OnDispose()
        {
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                try { _tracked[i]?.Dispose(); }
                catch { /* отписка не должна ронять освобождение остальных */ }
            }
            _tracked.Clear();
            OnDisposing();
        }

        /// <summary>Доосвободить собственные ресурсы страницы (после авто-Track).</summary>
        protected virtual void OnDisposing() { }
    }
}
