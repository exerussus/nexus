using System;
using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Manifests;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Хостит развёрнутые страницы и ВЛАДЕЕТ автоматом их жизненного цикла. Окно
    /// подаёт всего два входа — «активная страница» (<see cref="Activate"/>) и «окно
    /// в фокусе» (<see cref="SetWindowFocus"/>); хаб сам выводит Enter/Focus/Unfocus/
    /// Exit в правильном порядке, держа по странице флаги Entered/Focused. Так порядок
    /// не размазан по окну и не рассинхронится.
    ///
    /// КАЖДЫЙ вызов страницы — в песочнице: исключение помечает страницу faulted и не
    /// роняет хаб. Инстансы ленивые и кэшируются на сессию окна; после domain reload
    /// кэш пуст (OnDispose через перезагрузку не зовётся — осознанное ограничение).
    /// </summary>
    public sealed class PageHost
    {
        private readonly IPageMessageBus _bus;
        private readonly IPageTheme _theme;
        private readonly IPageUi _ui;
        private readonly Action<string, StatusKind> _status;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();

        private string _activeId;        // id активной страницы (или null — напр. Health)
        private bool   _windowFocused;   // окно Nexus в фокусе?

        private sealed class Entry
        {
            public IEditorPage   Page;
            public VisualElement Root;
            public bool          Faulted;
            public string        Error;
            public bool          Entered;   // получила OnEnter и ещё не OnExit
            public bool          Focused;   // получила OnFocus и ещё не OnUnfocus
        }

        public PageHost(IPageMessageBus bus, IPageTheme theme, IPageUi ui, Action<string, StatusKind> status)
        {
            _bus    = bus;
            _theme  = theme;
            _ui     = ui;
            _status = status;
            PageHostRegistry.Register(this);
        }

        public bool   IsFaulted(string id) => _entries.TryGetValue(id, out var e) && e.Faulted;
        public string ErrorOf(string id)   => _entries.TryGetValue(id, out var e) ? e.Error : null;

        // ------------------------------------------------------------ inputs

        /// <summary>Сделать страницу активной (p == null → ничего активного, напр. Health).
        /// Возвращает корневой элемент для показа (null — если faulted или ничего).</summary>
        public VisualElement Activate(DiscoveredPlugin p, bool windowFocused)
        {
            _windowFocused = windowFocused;
            var newId = p?.Id;

            if (newId == _activeId)
            {
                ReconcileFocus();
                return newId != null && !IsFaulted(newId) ? _entries[newId].Root : null;
            }

            Deactivate();              // OnUnfocus → OnExit у прежней активной
            _activeId = newId;
            if (newId == null) return null;

            var view = GetOrBuild(p);  // Initialize → BuildUI (лениво)
            if (view == null) return null;   // faulted

            var e = _entries[newId];
            if (!e.Entered) { Guard(newId, "OnEnter", pg => pg.OnEnter()); e.Entered = true; }
            ReconcileFocus();          // OnFocus, если окно в фокусе
            return view;
        }

        /// <summary>Окно получило/потеряло фокус. Сводит фокус активной страницы.</summary>
        public void SetWindowFocus(bool focused)
        {
            _windowFocused = focused;
            ReconcileFocus();
        }

        /// <summary>Явный Refresh пользователем — активной странице.</summary>
        public void Refresh()
        {
            if (_activeId != null) Guard(_activeId, "OnRefresh", p => p.OnRefresh());
        }

        // ----------------------------------------------------------- teardown

        /// <summary>Сбросить инстанс (после фикса) — пересоздастся при следующем Activate.</summary>
        public void Reload(string id)
        {
            if (!_entries.TryGetValue(id, out var e)) return;
            if (id == _activeId) Deactivate();   // корректно свернуть, если активна
            SafeDispose(e);
            _entries.Remove(id);
        }

        /// <summary>Закрытие окна: OnUnfocus → OnExit активной, затем OnDispose всем.
        /// (PrePack здесь НЕ зовётся — это не упаковка.)</summary>
        public void Dispose()
        {
            Deactivate();
            foreach (var e in _entries.Values) SafeDispose(e);
            _entries.Clear();
            PageHostRegistry.Unregister(this);
        }

        // --------------------------------------------- disable-path (registry)

        /// <summary>Вето выключения страницы id, если её живой инстанс не готов.</summary>
        internal bool CanClose(string id)
        {
            if (!_entries.TryGetValue(id, out var e) || e.Page == null || e.Faulted) return true;
            try { return e.Page.CanClose(); }
            catch { return true; }   // упавшая на CanClose страница не блокирует выключение
        }

        /// <summary>Свернуть и дать последний шанс сбросить данные ПЕРЕД упаковкой/рекомпилом.
        /// Последовательность выключения: (OnUnfocus) → OnExit → OnPrePack.</summary>
        internal void PrePack(string id)
        {
            if (!_entries.TryGetValue(id, out var e) || e.Page == null || e.Faulted) return;

            if (id == _activeId)
            {
                if (e.Focused) { Guard(id, "OnUnfocus", p => p.OnUnfocus()); e.Focused = false; }
                if (e.Entered) { Guard(id, "OnExit",    p => p.OnExit());    e.Entered = false; }
                _activeId = null;
            }
            Guard(id, "OnPrePack", p => p.OnPrePack());
            // OnDispose не зовём: следом идёт AssetDatabase.Refresh → domain reload, инстанс исчезнет.
        }

        // ---------------------------------------------------------- internals

        private void Deactivate()
        {
            if (_activeId == null) return;
            if (_entries.TryGetValue(_activeId, out var e) && e.Page != null && !e.Faulted)
            {
                if (e.Focused) { Guard(_activeId, "OnUnfocus", p => p.OnUnfocus()); e.Focused = false; }
                if (e.Entered) { Guard(_activeId, "OnExit",    p => p.OnExit());    e.Entered = false; }
            }
            _activeId = null;
        }

        private void ReconcileFocus()
        {
            if (_activeId == null) return;
            if (!_entries.TryGetValue(_activeId, out var e) || e.Page == null || e.Faulted || !e.Entered) return;

            if (_windowFocused && !e.Focused)      { Guard(_activeId, "OnFocus",   p => p.OnFocus());   e.Focused = true;  }
            else if (!_windowFocused && e.Focused) { Guard(_activeId, "OnUnfocus", p => p.OnUnfocus()); e.Focused = false; }
        }

        // лениво создать инстанс: резолв typeName → Initialize → BuildUI (всё в песочнице)
        private VisualElement GetOrBuild(DiscoveredPlugin p)
        {
            if (p?.Manifest == null) return null;
            var id = p.Id;

            if (_entries.TryGetValue(id, out var existing))
                return existing.Faulted ? null : existing.Root;

            var entry = new Entry();
            _entries[id] = entry;

            try
            {
                var type = PageTypeResolver.Resolve(p.Manifest.TypeName);
                if (type == null)
                    return Fault(entry, $"тип не найден: {p.Manifest.TypeName}");
                if (!typeof(IEditorPage).IsAssignableFrom(type))
                    return Fault(entry, $"{type.FullName} не реализует IEditorPage");

                var page = (IEditorPage)Activator.CreateInstance(type);
                entry.Page = page;                       // до BuildUI — чтобы Reload/Dispose сняли подписки даже при падении
                page.Initialize(new NexusPageContext(id, _bus, _theme, _ui, _status));
                entry.Root = page.BuildUI() ?? new VisualElement();
                return entry.Root;
            }
            catch (Exception ex)
            {
                return Fault(entry, ex.Message);
            }
        }

        private void Guard(string id, string what, Action<IEditorPage> act)
        {
            if (!_entries.TryGetValue(id, out var e) || e.Faulted || e.Page == null) return;
            try
            {
                act(e.Page);
            }
            catch (Exception ex)
            {
                e.Faulted = true;
                e.Error   = $"{what}: {ex.Message}";
                Debug.LogError($"[Nexus] Страница '{id}' упала в {what}: {ex}");
                _status?.Invoke($"Страница «{id}» упала: {what}", StatusKind.Error);
            }
        }

        private VisualElement Fault(Entry e, string error)
        {
            e.Faulted = true;
            e.Error   = error;
            Debug.LogError($"[Nexus] Страница не загрузилась: {error}");
            return null;
        }

        private void SafeDispose(Entry e)
        {
            if (e?.Page == null) return;
            try { e.Page.OnDispose(); }
            catch (Exception ex) { Debug.LogError($"[Nexus] OnDispose упал: {ex.Message}"); }
        }
    }
}
