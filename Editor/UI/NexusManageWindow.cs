using System.Collections.Generic;
using System.Linq;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using Exerussus.Nexus.Theme;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.UI
{
    /// <summary>
    /// Окно управления — ОТДЕЛЬНОЕ от рабочего окна Nexus (management ≠ workspace).
    /// Здесь включают/выключают плагины (тогл = намерение), жмут Apply, делают
    /// Restore и чистят осиротевшие состояния. Сам Apply транзакционный
    /// (см. <see cref="ApplyService"/>); деструктив подтверждается там же.
    /// </summary>
    public sealed class NexusManageWindow : EditorWindow
    {
        private static Color BgHard    => NexusTheme.Get(NexusToken.BgHard);
        private static Color BgSoft    => NexusTheme.Get(NexusToken.BgSoft);
        private static Color TextDim   => NexusTheme.Get(NexusToken.TextDim);
        private static Color Deployed  => NexusTheme.Get(NexusToken.Ok);
        private static Color Available => NexusTheme.Get(NexusToken.Muted);
        private static Color Orphaned  => NexusTheme.Get(NexusToken.Orphan);

        private readonly Dictionary<string, bool> _desired = new Dictionary<string, bool>();
        private List<DiscoveredPlugin> _plugins = new List<DiscoveredPlugin>();

        private ScrollView _list;
        private ScrollView _detail;
        private string     _detailId;
        private Button     _apply;
        private Label      _pending;

        [MenuItem("Exerussus/Nexus/Manage")]
        public static void Open()
        {
            var w = GetWindow<NexusManageWindow>();
            w.titleContent = new GUIContent("Nexus — Manage");
            w.minSize = new Vector2(640f, 320f);
            w.Show();
        }

        private void OnEnable()
        {
            // после domain reload (рекомпил по Apply) пересобираем актуальное состояние
            if (_list != null) Reload();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.flexDirection = FlexDirection.Column;
            root.style.backgroundColor = BgHard;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.paddingLeft = 8f;
            header.style.paddingRight = 8f;
            header.style.paddingTop = 6f;
            header.style.paddingBottom = 4f;
            header.style.backgroundColor = BgSoft;

            var title = new Label("Plugins & Pages") { style = { unityFontStyleAndWeight = FontStyle.Bold, flexGrow = 1f } };
            header.Add(title);

            header.Add(new Label("Palette") { style = { color = TextDim, marginRight = 6f } });
            var palettes = NexusPalettes.Names.ToList();
            var picker = new PopupField<string>(palettes, NexusTheme.Active.Name);
            picker.RegisterValueChangedCallback(e =>
            {
                NexusTheme.Active = NexusPalettes.Get(e.newValue);   // событие перекрасит окна
                NexusThemeStore.ActiveName = e.newValue;             // запомнить личный выбор
            });
            header.Add(picker);

            root.Add(header);

            // тело: слева список плагинов, справа README выбранного
            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1f;

            _list = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1f, flexBasis = 0f, paddingTop = 4f } };
            body.Add(_list);

            _detail = new ScrollView(ScrollViewMode.Vertical)
            {
                style = { flexGrow = 1f, flexBasis = 0f, paddingLeft = 10f, paddingRight = 10f, paddingTop = 8f },
            };
            _detail.style.borderLeftWidth = 1f;
            _detail.style.borderLeftColor = NexusTheme.Get(NexusToken.Border);
            body.Add(_detail);

            root.Add(body);
            root.Add(BuildFooter());

            Reload();
        }

        private VisualElement BuildFooter()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.height = 30f;
            bar.style.paddingLeft = 8f;
            bar.style.paddingRight = 8f;
            bar.style.backgroundColor = BgSoft;

            _pending = new Label("нет изменений") { style = { flexGrow = 1f, color = TextDim } };
            bar.Add(_pending);

            bar.Add(NexusStyles.Button("Revert", Reload));

            _apply = NexusStyles.Button("Apply", ApplyChanges);
            _apply.SetEnabled(false);
            bar.Add(_apply);

            return bar;
        }

        // пересканировать диск и сбросить желаемое состояние к фактическому
        private void Reload()
        {
            _plugins = PluginDiscovery.Discover();
            _desired.Clear();
            foreach (var p in _plugins)
                if (p.Status != PluginStatus.OrphanedState)
                    _desired[p.Id] = p.Status == PluginStatus.Deployed;

            if (_detailId != null && !_plugins.Exists(x => x.Id == _detailId))
                _detailId = null;

            Repaint_();
            RenderDetail();
        }

        private void Repaint_()
        {
            if (_list == null) return;
            _list.Clear();

            _list.Add(ScanPathsSection());

            if (_plugins.Count == 0)
            {
                _list.Add(Hint("Плагины не найдены. Положите Plugins/<id>/manifest.json."));
            }
            else
            {
                string category = null;
                foreach (var p in _plugins)
                {
                    if (p.Category != category)
                    {
                        category = p.Category;
                        _list.Add(CategoryHeader(category));
                    }
                    _list.Add(Row(p));
                }
            }

            UpdatePending();
        }

        // доп. корни сканирования плагинов (проектные плагины вне основной папки Nexus)
        private VisualElement ScanPathsSection()
        {
            var fold = new Foldout { text = "Scan paths (доп. корни плагинов)" };
            const string key = "Exerussus.Nexus.manage.scanfold";
            fold.value = SessionState.GetBool(key, false);
            fold.RegisterValueChangedCallback(e => SessionState.SetBool(key, e.newValue));

            foreach (var path in ApplyService.GetScanPaths())
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.Add(new Label(path) { style = { flexGrow = 1f, color = TextDim } });
                row.Add(NexusStyles.Button("Remove", () => { ApplyService.RemoveScanPath(path); Reload(); }));
                fold.Add(row);
            }

            fold.Add(NexusStyles.Button("Add scan path…", AddScanPathDialog));

            var hint = new Label("Папки с Plugins-структурой (<id>/manifest.json), напр. проектные плагины. Должны быть внутри проекта.");
            hint.style.color = TextDim;
            hint.style.fontSize = 10f;
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginTop = 2f;
            fold.Add(hint);

            return fold;
        }

        private void AddScanPathDialog()
        {
            var abs = EditorUtility.OpenFolderPanel("Nexus — папка с плагинами проекта", Application.dataPath, "");
            if (string.IsNullOrEmpty(abs)) return;
            if (!ApplyService.AddScanPath(abs))
            {
                EditorUtility.DisplayDialog("Nexus", "Папка должна быть внутри проекта.", "Ок");
                return;
            }
            Reload();
        }

        private VisualElement Row(DiscoveredPlugin p)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 26f;
            row.style.paddingLeft = 12f;
            row.style.paddingRight = 8f;
            if (p.Id == _detailId) row.style.backgroundColor = NexusTheme.Get(NexusToken.Selection);

            // клик по строке — показать README этого плагина справа
            row.RegisterCallback<MouseDownEvent>(_ => ShowDetail(p.Id));

            if (p.Status == PluginStatus.OrphanedState)
            {
                var ico = RowIcon(p.Id);
                if (ico != null) row.Add(ico);
                row.Add(new Label(p.Id) { style = { flexGrow = 1f, color = TextDim } });
                row.Add(StatusBadge(p.Status));
                var clean = NexusStyles.Button("Clean", () => ApplyService.CleanOrphan(p.Id));
                row.Add(clean);
                if (ApplyService.HasUserSettings(p.Id)) row.Add(ClearCacheButton(p.Id));
                return row;
            }

            var toggle = new Toggle { value = _desired.TryGetValue(p.Id, out var v) && v };
            toggle.RegisterValueChangedCallback(evt =>
            {
                _desired[p.Id] = evt.newValue;
                UpdatePending();
            });
            row.Add(toggle);

            var icon = RowIcon(p.Id);
            if (icon != null) row.Add(icon);

            row.Add(new Label(p.DisplayName) { style = { flexGrow = 1f, marginLeft = 4f } });
            row.Add(StatusBadge(p.Status));

            if (p.Status == PluginStatus.Deployed)
            {
                var restore = NexusStyles.Button("Restore", () => ApplyService.RestoreDefault(p.Id));
                row.Add(restore);

                var vis = NexusView.IsVisible(p.Id);
                var visBtn = NexusStyles.Button(vis ? "Hide" : "Show",
                    () => { NexusView.SetVisible(p.Id, !vis); Repaint_(); });
                visBtn.tooltip = "Видимость в сайдбаре (модерирует Nexus; на работу страницы не влияет)";
                row.Add(visBtn);
            }

            if (ApplyService.HasUserSettings(p.Id)) row.Add(ClearCacheButton(p.Id));

            return row;
        }

        // иконка плагина (page_logo.png) для строки списка, или null
        private static Image RowIcon(string id)
        {
            var tex = PluginMedia.LoadIcon(id);
            if (tex == null) return null;
            var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit };
            img.style.width = 16f;
            img.style.height = 16f;
            img.style.marginLeft = 2f;
            img.style.marginRight = 4f;
            return img;
        }

        // показать README выбранного плагина в правой панели
        private void ShowDetail(string id)
        {
            _detailId = id;
            RenderDetail();
            Repaint_();   // обновить подсветку выбранной строки
        }

        private void RenderDetail()
        {
            if (_detail == null) return;
            _detail.Clear();

            if (string.IsNullOrEmpty(_detailId))
            {
                _detail.Add(DetailHint("Выберите плагин слева, чтобы увидеть его README."));
                return;
            }

            var name = _plugins.Find(x => x.Id == _detailId)?.DisplayName ?? _detailId;

            var title = new Label(name);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 13f;
            title.style.marginBottom = 6f;
            _detail.Add(title);

            var readme = PluginMedia.ReadReadme(_detailId);
            if (string.IsNullOrEmpty(readme))
            {
                _detail.Add(DetailHint($"У «{name}» нет README.\nПоложите README.md в Plugins/{_detailId}/."));
                return;
            }

            // лёгкий markdown-рендер (подмножество) на токенах темы
            MarkdownView.Render(_detail, readme);
        }

        private VisualElement DetailHint(string message)
        {
            var label = new Label(message);
            label.style.color = TextDim;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        // затереть персональные настройки/кэш страницы (UserSettings); диалог — в ApplyService
        private Button ClearCacheButton(string id)
        {
            var b = NexusStyles.Button("Clear cache", () => { ApplyService.ClearUserSettings(id); Repaint_(); });
            b.tooltip = "Затереть персональные настройки/кэш страницы (UserSettings)";
            return b;
        }

        // пересчитать список ожидающих изменений и состояние кнопки Apply
        private void UpdatePending()
        {
            var changes = CollectIntents();
            if (_apply != null) _apply.SetEnabled(changes.Count > 0);
            if (_pending != null)
                _pending.text = changes.Count == 0
                    ? "нет изменений"
                    : $"к применению: {changes.Count}";
        }

        private List<PendingIntent> CollectIntents()
        {
            var intents = new List<PendingIntent>();
            foreach (var p in _plugins)
            {
                if (p.Status == PluginStatus.OrphanedState) continue;
                if (!_desired.TryGetValue(p.Id, out var want)) continue;

                var isDeployed = p.Status == PluginStatus.Deployed;
                if (want && !isDeployed) intents.Add(new PendingIntent(p.Id, IntentKind.Deploy));
                else if (!want && isDeployed) intents.Add(new PendingIntent(p.Id, IntentKind.Undeploy));
            }
            return intents;
        }

        private void ApplyChanges()
        {
            var intents = CollectIntents();
            if (intents.Count == 0) return;
            ApplyService.Apply(intents);   // делает подтверждение + файлы + Refresh (→ reload → OnEnable→Reload)
        }

        // -------- мелочи отрисовки (временная мягкая тёмная; уедет в палитру M5) --------

        private VisualElement CategoryHeader(string category)
        {
            var label = new Label(category);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = TextDim;
            label.style.fontSize = 11f;
            label.style.marginTop = 8f;
            label.style.marginLeft = 8f;
            label.style.marginBottom = 2f;
            return label;
        }

        private VisualElement StatusBadge(PluginStatus status)
        {
            var (text, color) = status switch
            {
                PluginStatus.Deployed      => ("deployed", Deployed),
                PluginStatus.Available     => ("available", Available),
                PluginStatus.OrphanedState => ("orphaned", Orphaned),
                _                          => ("?", Available),
            };

            var badge = new Label(text);
            badge.style.color = Color.white;
            badge.style.backgroundColor = color;
            badge.style.fontSize = 10f;
            badge.style.paddingLeft = 6f;
            badge.style.paddingRight = 6f;
            badge.style.marginLeft = 4f;
            badge.style.borderTopLeftRadius = 3f;
            badge.style.borderTopRightRadius = 3f;
            badge.style.borderBottomLeftRadius = 3f;
            badge.style.borderBottomRightRadius = 3f;
            return badge;
        }

        private VisualElement Hint(string message)
        {
            var label = new Label(message);
            label.style.color = TextDim;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop = 12f;
            label.style.marginLeft = 12f;
            label.style.marginRight = 12f;
            return label;
        }
    }
}
