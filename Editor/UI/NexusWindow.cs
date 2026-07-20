using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using Exerussus.Nexus.Theme;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.UI
{
    /// <summary>
    /// Рабочее окно Nexus (workspace). Сайдбар: встроенная страница «Nexus Health» +
    /// развёрнутые страницы; область — активная страница, хостится через
    /// <see cref="PageHost"/> в песочнице (падение страницы не роняет окно).
    /// Управление — в отдельном окне (<see cref="NexusManageWindow"/>).
    /// Цвета временные (палитра — M5).
    /// </summary>
    public sealed class NexusWindow : EditorWindow
    {
        private const string HealthId = "::health";

        private static Color BgHard  => NexusTheme.Get(NexusToken.BgHard);
        private static Color BgSoft  => NexusTheme.Get(NexusToken.BgSoft);
        private static Color TextDim => NexusTheme.Get(NexusToken.TextDim);
        private static Color SelBg   => NexusTheme.Get(NexusToken.Selection);
        private static Color ErrCol  => NexusTheme.Get(NexusToken.Error);
        private static Color OkCol   => NexusTheme.Get(NexusToken.Ok);
        private static Color WarnCol => NexusTheme.Get(NexusToken.Warning);

        private PageMessageBus _bus;
        private PageHost       _host;
        private IPageUi        _ui;
        private bool           _suppressThemeRebuild;

        private VisualElement _sidebar;
        private VisualElement _content;
        private Label         _status;

        private Dictionary<string, DiscoveredPlugin> _byId = new Dictionary<string, DiscoveredPlugin>();
        private string _selectedId = HealthId;

        [MenuItem("Exerussus/Nexus/Open")]
        public static void Open()
        {
            var window = GetWindow<NexusWindow>();
            window.titleContent = new GUIContent("Nexus");
            window.minSize = new Vector2(460f, 300f);
            window.Show();
        }

        private void OnEnable()
        {
            NexusView.Changed -= RebuildSidebar;   // на случай повторной активации
            NexusView.Changed += RebuildSidebar;
            NexusTheme.Changed -= OnThemeChanged;
            NexusTheme.Changed += OnThemeChanged;
            if (_sidebar != null) RebuildSidebar();
        }

        private void OnDisable()
        {
            NexusView.Changed -= RebuildSidebar;
            NexusTheme.Changed -= OnThemeChanged;
            _host?.Dispose();
            _host = null;
        }

        // палитра сменилась — полная пересборка окна (новые цвета во всех частях)
        private void OnThemeChanged()
        {
            if (_suppressThemeRebuild) return;
            if (rootVisualElement != null) CreateGUI();
        }

        // окно Nexus получило фокус — активная страница уходит в OnFocus
        private void OnFocus()
        {
            _host?.SetWindowFocus(true);
        }

        // окно потеряло фокус — активная страница уходит в OnUnfocus (вкладка ещё активна)
        private void OnLostFocus()
        {
            _host?.SetWindowFocus(false);
        }

        private void CreateGUI()
        {
            _suppressThemeRebuild = true;
            try
            {
                // личный выбор палитры (если был) — до построения; событие подавлено флагом
                var saved = NexusThemeStore.ActiveName;
                if (!string.IsNullOrEmpty(saved) && NexusTheme.Active.Name != saved)
                    NexusTheme.Active = NexusPalettes.Get(saved);

                var root = rootVisualElement;
                root.Clear();              // идемпотентно: пересборка при смене палитры
                _host?.Dispose();

                _bus  = new PageMessageBus();
                _ui ??= new NexusUi();
                _host = new PageHost(_bus, NexusTheme.PageTheme, _ui, SetStatus);

                root.style.flexDirection = FlexDirection.Column;
                root.style.backgroundColor = BgHard;

                root.Add(BuildToolbar());

                var body = new VisualElement { style = { flexDirection = FlexDirection.Row, flexGrow = 1f } };
                root.Add(body);

                _sidebar = new VisualElement();
                _sidebar.style.width = 190f;
                _sidebar.style.backgroundColor = BgSoft;
                _sidebar.style.paddingTop = 4f;
                body.Add(_sidebar);

                _content = new VisualElement { style = { flexGrow = 1f, paddingLeft = 12f, paddingTop = 12f } };
                body.Add(_content);

                RebuildSidebar();
            }
            finally { _suppressThemeRebuild = false; }
        }

        private VisualElement BuildToolbar()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.height = 28f;
            bar.style.paddingLeft = 8f;
            bar.style.paddingRight = 8f;
            bar.style.backgroundColor = BgSoft;

            bar.Add(new Label("Nexus") { style = { unityFontStyleAndWeight = FontStyle.Bold } });

            _status = new Label(string.Empty) { style = { flexGrow = 1f, marginLeft = 10f, color = TextDim } };
            bar.Add(_status);

            bar.Add(NexusStyles.Button("Refresh", OnRefreshClicked));
            bar.Add(NexusStyles.Button("Manage…", NexusManageWindow.Open));
            return bar;
        }

        private void OnRefreshClicked()
        {
            _host?.Refresh();
            RebuildSidebar();
        }

        // сайдбар: встроенный Health + два сворачиваемых раздела (Infrastructure / Game)
        private void RebuildSidebar()
        {
            if (_sidebar == null) return;
            _sidebar.Clear();
            _byId.Clear();

            _sidebar.Add(SidebarItem(HealthId, "◆ Nexus Health"));

            var infra = new List<DiscoveredPlugin>();
            var game  = new List<DiscoveredPlugin>();
            foreach (var p in PluginDiscovery.Discover())
            {
                if (p.Status != PluginStatus.Deployed) continue;
                if (!NexusView.IsVisible(p.Id)) continue;     // сокрытые в сайдбар не попадают
                _byId[p.Id] = p;
                (IsGame(p.Category) ? game : infra).Add(p);
            }

            var infraSection = CategorySection("Infrastructure", infra);
            if (infraSection != null) _sidebar.Add(infraSection);
            var gameSection = CategorySection("Game", game);
            if (gameSection != null) _sidebar.Add(gameSection);

            if (_byId.Count == 0)
            {
                var hint = new Label("Нет видимых развёрнутых страниц.\nОткройте «Manage…».");
                hint.style.color = TextDim;
                hint.style.whiteSpace = WhiteSpace.Normal;
                hint.style.marginLeft = 8f;
                hint.style.marginTop = 6f;
                _sidebar.Add(hint);
            }

            if (_selectedId != HealthId && !_byId.ContainsKey(_selectedId))
                _selectedId = HealthId;

            Select(_selectedId);
        }

        private static bool IsGame(string category)
            => string.Equals(category, "Game", System.StringComparison.OrdinalIgnoreCase);

        // сворачиваемый раздел со скролл-листом страниц; пустой раздел не показываем
        private VisualElement CategorySection(string title, List<DiscoveredPlugin> items)
        {
            if (items.Count == 0) return null;

            var fold = new Foldout { text = title };
            var key = "Exerussus.Nexus.fold." + title;
            fold.value = SessionState.GetBool(key, true);
            fold.style.flexGrow = fold.value ? 1f : 0f;
            fold.style.minHeight = 0f;
            fold.RegisterValueChangedCallback(evt =>
            {
                SessionState.SetBool(key, evt.newValue);
                fold.style.flexGrow = evt.newValue ? 1f : 0f;
            });

            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1f, minHeight = 0f } };
            foreach (var p in items) scroll.Add(SidebarItem(p.Id, p.DisplayName));
            fold.Add(scroll);
            return fold;
        }

        private VisualElement SidebarItem(string id, string title)
        {
            var item = new VisualElement { name = "item-" + id };
            item.AddToClassList("nexus-item");
            item.style.flexDirection = FlexDirection.Row;
            item.style.alignItems = Align.Center;
            item.style.height = 24f;
            item.style.paddingLeft = 10f;

            var icon = PluginMedia.LoadIcon(id);   // page_logo.png, если есть
            if (icon != null)
            {
                var img = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                img.style.width = 16f;
                img.style.height = 16f;
                img.style.marginRight = 6f;
                item.Add(img);
            }

            item.Add(new Label(title) { style = { unityTextAlign = TextAnchor.MiddleLeft } });

            item.RegisterCallback<MouseDownEvent>(_ => Select(id));
            NexusStyles.MakePressable(item,
                () => item.name == "item-" + _selectedId ? SelBg : Color.clear,
                NexusTheme.Get(NexusToken.Hover),
                NexusTheme.Get(NexusToken.Pressed));
            return item;
        }

        private void Select(string id)
        {
            _selectedId = id;
            RenderContent(id);
            RefreshHighlight();
        }

        private void RenderContent(string id)
        {
            _content.Clear();

            if (id == HealthId)
            {
                _host?.Activate(null, hasFocus);   // снять активность с прежней страницы
                RenderHealth();
                return;
            }

            if (!_byId.TryGetValue(id, out var p))
            {
                _host?.Activate(null, hasFocus);
                _content.Add(Dim("Страница не найдена."));
                return;
            }

            // автомат сам сделает OnExit прежней и OnEnter+OnFocus этой
            var view = _host.Activate(p, hasFocus);
            if (view == null && _host.IsFaulted(id))
            {
                RenderFaulted(id);
                return;
            }

            _content.Add(view ?? new VisualElement());
        }

        private void RenderFaulted(string id)
        {
            var title = new Label($"Страница «{id}» упала") { style = { color = ErrCol, unityFontStyleAndWeight = FontStyle.Bold } };
            _content.Add(title);

            var msg = new Label(_host.ErrorOf(id) ?? "неизвестная ошибка");
            msg.style.color = TextDim;
            msg.style.whiteSpace = WhiteSpace.Normal;
            msg.style.marginTop = 6f;
            _content.Add(msg);

            var reload = NexusStyles.Button("Reload", () => { _host.Reload(id); Select(id); });
            reload.style.marginTop = 8f;
            _content.Add(reload);
        }

        private void RenderHealth()
        {
            _content.Add(new Label("Nexus Health") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 14f } });

            var plugins = PluginDiscovery.Discover();
            int deployed = 0, available = 0, orphan = 0, faulted = 0;
            foreach (var p in plugins)
            {
                switch (p.Status)
                {
                    case PluginStatus.Deployed:      deployed++;  break;
                    case PluginStatus.Available:     available++; break;
                    case PluginStatus.OrphanedState: orphan++;    break;
                }
                if (_host.IsFaulted(p.Id)) faulted++;
            }

            var summary = new Label($"развёрнуто: {deployed}   доступно: {available}   сирот: {orphan}   упало: {faulted}");
            summary.style.color = TextDim;
            summary.style.marginTop = 6f;
            summary.style.marginBottom = 8f;
            _content.Add(summary);

            foreach (var p in plugins)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, height = 22f } };
                var dot = new Label("●") { style = { width = 16f, color = _host.IsFaulted(p.Id) ? ErrCol : (p.Status == PluginStatus.Deployed ? OkCol : TextDim) } };
                row.Add(dot);
                row.Add(new Label(p.DisplayName) { style = { flexGrow = 1f } });
                var st = _host.IsFaulted(p.Id) ? "faulted" : p.Status.ToString().ToLowerInvariant();
                row.Add(new Label(st) { style = { color = TextDim } });
                _content.Add(row);
            }

            // --- сервисы ---
            _content.Add(new Label("Сервисы") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 12f } });
            var services = ServiceRegistry.Discover();
            if (services.Count == 0)
            {
                _content.Add(Dim("нет"));
                return;
            }
            foreach (var s in services)
            {
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, height = 22f } };
                row.Add(new Label("●") { style = { width = 16f, color = s.Present ? OkCol : ErrCol } });
                row.Add(new Label(s.ServiceId) { style = { flexGrow = 1f } });

                var v = s.EffectiveVersion ?? "—";
                var tag = !s.Present ? "не скомпилирован" : s.Stale ? $"v{v} (manifest stale)" : $"v{v}";
                var col = s.Stale ? WarnCol : TextDim;
                row.Add(new Label(tag) { style = { color = col } });
                _content.Add(row);
            }
        }

        private void RefreshHighlight()
        {
            _sidebar.Query<VisualElement>(null, "nexus-item").ForEach(item =>
            {
                if (item.name == null || !item.name.StartsWith("item-")) return;
                item.style.backgroundColor =
                    item.name == "item-" + _selectedId ? SelBg : new StyleColor(Color.clear);
            });
        }

        private void SetStatus(string text, StatusKind kind)
        {
            if (_status == null) return;
            _status.text = text ?? string.Empty;
            _status.style.color = kind == StatusKind.Error ? ErrCol
                                : kind == StatusKind.Warning ? WarnCol
                                : kind == StatusKind.Ok ? OkCol
                                : TextDim;
        }

        private static Label Dim(string text) => new Label(text) { style = { color = TextDim } };
    }
}
