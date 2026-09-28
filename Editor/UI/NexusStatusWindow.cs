using System.Collections.Generic;
using System.Globalization;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using Exerussus.Nexus.Theme;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.UI
{
    /// <summary>
    /// Окно сообщений: журнал статусов (<see cref="NexusStatusLog"/>) слева, полный
    /// текст выбранного сообщения справа — в поле только для чтения, его можно
    /// выделять и копировать частями. Открывается кликом по строке статуса или кнопкой
    /// рядом с Refresh. Открытие = ошибки прочитаны.
    ///
    /// Выбранное сообщение хранится в SessionState (по времени сообщения), а не в поле:
    /// окно пересоздаётся при рекомпиле и смене раскладки.
    /// </summary>
    public sealed class NexusStatusWindow : EditorWindow
    {
        private const string SelectedKey = "Exerussus.Nexus.statusWindow.selected";

        private readonly List<NexusStatusEntry> _items = new List<NexusStatusEntry>();   // новые сверху
        private ListView _list;
        private Label _header;
        private TextField _text;
        private Label _empty;

        private static long SelectedTicks
        {
            get => long.TryParse(SessionState.GetString(SelectedKey, "0"), out var t) ? t : 0;
            set => SessionState.SetString(SelectedKey, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Открыть на последнем сообщении.</summary>
        public static void ShowLast()
        {
            var last = NexusStatusLog.Last;
            ShowEntry(last);
        }

        /// <summary>Открыть на конкретном сообщении (null — просто открыть).</summary>
        public static void ShowEntry(NexusStatusEntry entry)
        {
            if (entry != null) SelectedTicks = entry.Ticks;
            var w = GetWindow<NexusStatusWindow>(false, "Nexus — сообщения", true);
            w.minSize = new Vector2(560f, 280f);
            w.Show();
            w.Reload();
            NexusStatusLog.MarkSeen();
        }

        private void OnEnable()
        {
            NexusStatusLog.Changed -= Reload;
            NexusStatusLog.Changed += Reload;
            NexusTheme.Changed -= Rebuild;
            NexusTheme.Changed += Rebuild;
        }

        private void OnDisable()
        {
            NexusStatusLog.Changed -= Reload;
            NexusTheme.Changed -= Rebuild;
        }

        private void OnFocus() => NexusStatusLog.MarkSeen();

        private void CreateGUI() => Rebuild();

        private void Rebuild()
        {
            var root = rootVisualElement;
            if (root == null) return;
            root.Clear();
            root.style.backgroundColor = NexusTheme.Get(NexusToken.BgHard);
            root.style.flexDirection = FlexDirection.Column;

            // --- тулбар ---
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.height = 28f;
            bar.style.paddingLeft = 6f;
            bar.style.paddingRight = 6f;
            bar.style.backgroundColor = NexusTheme.Get(NexusToken.BgSoft);
            bar.Add(NexusStyles.Button("Копировать", CopySelected));
            bar.Add(NexusStyles.Button("Копировать всё", () =>
            {
                EditorGUIUtility.systemCopyBuffer = NexusStatusLog.FormatAll();
                ShowNotification(new GUIContent("Журнал скопирован"), 1.0);
            }));
            bar.Add(new VisualElement { style = { flexGrow = 1f } });
            bar.Add(NexusStyles.Button("Очистить", () =>
            {
                if (NexusStatusLog.Entries.Count == 0) return;
                if (!EditorUtility.DisplayDialog("Nexus", "Очистить журнал сообщений?", "Очистить", "Отмена")) return;
                NexusStatusLog.Clear();
            }));
            root.Add(bar);

            // --- тело: список | текст ---
            var body = new TwoPaneSplitView(0, 240f, TwoPaneSplitViewOrientation.Horizontal);
            body.style.flexGrow = 1f;
            root.Add(body);

            var left = new VisualElement { style = { minWidth = 160f, backgroundColor = NexusTheme.Get(NexusToken.BgSoft) } };
            _list = new ListView(_items, 22f, MakeRow, BindRow)
            {
                selectionType = SelectionType.Single,
                style = { flexGrow = 1f },
            };
            _list.selectionChanged += _ => OnSelectionChanged();
            left.Add(_list);
            _empty = new Label("Сообщений нет") { style = { color = NexusTheme.Get(NexusToken.TextDim), marginLeft = 8f, marginTop = 8f } };
            left.Add(_empty);
            body.Add(left);

            var right = new VisualElement { style = { flexGrow = 1f, paddingLeft = 8f, paddingRight = 8f, paddingTop = 6f, paddingBottom = 6f, minWidth = 200f } };
            _header = new Label { style = { color = NexusTheme.Get(NexusToken.TextDim), marginBottom = 4f, whiteSpace = WhiteSpace.Normal } };
            right.Add(_header);

            _text = new TextField { multiline = true, isReadOnly = true };
            _text.style.flexGrow = 1f;
            _text.style.whiteSpace = WhiteSpace.Normal;
            _text.verticalScrollerVisibility = ScrollerVisibility.Auto;
            _text.textSelection.selectAllOnFocus = false;
            _text.textSelection.selectAllOnMouseUp = false;
            var input = _text.Q(className: TextField.inputUssClassName);
            if (input != null)
            {
                input.style.backgroundColor = NexusTheme.Get(NexusToken.BgRaised);
                input.style.color = NexusTheme.Get(NexusToken.TextNormal);
                input.style.unityTextAlign = TextAnchor.UpperLeft;
                input.style.whiteSpace = WhiteSpace.Normal;
            }
            right.Add(_text);
            body.Add(right);

            Reload();
        }

        // журнал изменился — перечитать, сохранив выбор
        private void Reload()
        {
            if (_list == null) return;
            _items.Clear();
            var entries = NexusStatusLog.Entries;
            for (var i = entries.Count - 1; i >= 0; i--) _items.Add(entries[i]);

            _list.style.display = _items.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _empty.style.display = _items.Count > 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _list.RefreshItems();

            var ticks = SelectedTicks;
            var index = _items.FindIndex(e => e.Ticks == ticks);
            if (index < 0 && _items.Count > 0) index = 0;
            if (index >= 0) _list.SetSelectionWithoutNotify(new[] { index });
            else _list.ClearSelection();
            ShowDetails(index >= 0 ? _items[index] : null);
        }

        private void OnSelectionChanged()
        {
            var i = _list.selectedIndex;
            var entry = i >= 0 && i < _items.Count ? _items[i] : null;
            if (entry != null) SelectedTicks = entry.Ticks;
            ShowDetails(entry);
        }

        private void ShowDetails(NexusStatusEntry e)
        {
            if (_text == null) return;
            if (e == null)
            {
                _header.text = "";
                _text.SetValueWithoutNotify("");
                return;
            }
            _header.text = $"{e.Time.ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture)} · {KindName(e.Kind)} · {e.Source}" +
                           (e.Repeat > 1 ? $" · повторилось {e.Repeat} раз" : "");
            _header.style.color = KindColor(e.Kind);
            _text.SetValueWithoutNotify((e.Text ?? "").Replace("\r", ""));
        }

        private void CopySelected()
        {
            var i = _list?.selectedIndex ?? -1;
            if (i < 0 || i >= _items.Count) return;
            EditorGUIUtility.systemCopyBuffer = _items[i].Format();
            ShowNotification(new GUIContent("Скопировано"), 1.0);
        }

        private static VisualElement MakeRow()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 6f, paddingRight = 4f } };
            row.Add(new Label("●") { name = "dot", style = { width = 14f } });
            row.Add(new Label { name = "time", style = { width = 58f, color = NexusTheme.Get(NexusToken.TextDim) } });
            var summary = new Label { name = "summary" };
            summary.style.flexGrow = 1f;
            summary.style.flexShrink = 1f;
            summary.style.minWidth = 0f;
            summary.style.overflow = Overflow.Hidden;
            summary.style.textOverflow = TextOverflow.Ellipsis;
            summary.style.whiteSpace = WhiteSpace.NoWrap;
            summary.style.color = NexusTheme.Get(NexusToken.TextNormal);
            row.Add(summary);
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var e = _items[index];
            var dot = row.Q<Label>("dot");
            dot.style.color = KindColor(e.Kind);
            row.Q<Label>("time").text = e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var summary = row.Q<Label>("summary");
            summary.text = (e.Repeat > 1 ? $"×{e.Repeat} " : "") + e.Source + ": " + e.Summary;
            summary.tooltip = e.Summary;
        }

        internal static Color KindColor(StatusKind kind) => kind switch
        {
            StatusKind.Error   => NexusTheme.Get(NexusToken.Error),
            StatusKind.Warning => NexusTheme.Get(NexusToken.Warning),
            StatusKind.Ok      => NexusTheme.Get(NexusToken.Ok),
            _                  => NexusTheme.Get(NexusToken.TextDim),
        };

        private static string KindName(StatusKind kind) => kind switch
        {
            StatusKind.Error   => "ошибка",
            StatusKind.Warning => "предупреждение",
            StatusKind.Ok      => "готово",
            _                  => "инфо",
        };
    }
}
