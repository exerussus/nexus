using System;
using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Тема хаба: единственная активная палитра (выбор уровня редактора, явный
    /// владелец — этот модуль). Палитра подменяема (<see cref="NexusPalettes"/>),
    /// смена шлёт <see cref="Changed"/>, чтобы окна перерисовались; выбор персистит
    /// слой выше (UI ↔ Core), здесь — только активное значение.
    /// </summary>
    /// <remarks>
    /// С 0.4 вид компонентов (кнопки, карточки, поля, вкладки…) описан в USS:
    /// <c>Editor/Theme/Uss/NexusComponents.uss</c> — классы <c>nx-*</c> и их состояния
    /// (:hover, :active, :focus), значения — переменные <c>--nx-*</c> из файла палитры
    /// (<c>Uss/&lt;Palette&gt;.uss</c>). <see cref="ApplyTo"/> вешает оба файла на корень окна;
    /// смена палитры — замена одного файла. Цвета в C# (<see cref="Get"/>) остаются для страниц,
    /// которые красят свои элементы сами, — источник один: палитра, из которой сгенерирован USS
    /// (Exerussus/Nexus/Dev/Regenerate theme USS).
    /// </remarks>
    public static class NexusTheme
    {
        public const string UssDir = "Packages/com.exerussus.nexus/Editor/Theme/Uss";
        public const string ComponentsUss = UssDir + "/NexusComponents.uss";
        public const string TokensUss = UssDir + "/NexusTokens.uss";

        /// <summary>Класс корня, на котором живут переменные палитры.</summary>
        public const string RootClass = "nx-root";

        private static NexusPalette _active = NexusPalettes.Default;
        private static StyleSheet _components;
        private static StyleSheet _tokens;
        private static readonly Dictionary<string, StyleSheet> PaletteSheets = new();
        private static readonly List<WeakReference<VisualElement>> Roots = new();

        /// <summary>Палитра сменилась — окнам стоит перестроиться.</summary>
        public static event Action Changed;

        public static NexusPalette Active
        {
            get => _active;
            set
            {
                _active = value ?? NexusPalettes.Default;
                RefreshRoots();
                Changed?.Invoke();
            }
        }

        public static Color Get(NexusToken token) => _active.Get(token);

        public static NexusRoleColors GetRole(NexusRole role) => _active.Role(role);

        public static NexusMetrics Metrics => _active.Metrics;

        /// <summary>Адаптер темы для выдачи странице через Context (изоляция цела).</summary>
        public static IPageTheme PageTheme { get; } = new Provider();

        /// <summary>
        /// Вешает на корень окна стили компонентов и переменные активной палитры. Идемпотентно;
        /// при смене палитры корни, через которые прошли, перекрашиваются сами.
        /// </summary>
        public static void ApplyTo(VisualElement root)
        {
            if (root == null) return;

            root.AddToClassList(RootClass);
            NexusFonts.ApplySans(root);

            _components ??= AssetDatabase.LoadAssetAtPath<StyleSheet>(ComponentsUss);
            if (_components != null && !root.styleSheets.Contains(_components)) root.styleSheets.Add(_components);

            // Классы-токены (nx-fg--accent…) — после компонентов: на одном элементе токен сильнее.
            _tokens ??= AssetDatabase.LoadAssetAtPath<StyleSheet>(TokensUss);
            if (_tokens != null && !root.styleSheets.Contains(_tokens)) root.styleSheets.Add(_tokens);

            SwapPaletteSheet(root);

            for (var i = Roots.Count - 1; i >= 0; i--)
                if (!Roots[i].TryGetTarget(out var known) || known == null) Roots.RemoveAt(i);
                else if (known == root) return;

            Roots.Add(new WeakReference<VisualElement>(root));
        }

        private static void RefreshRoots()
        {
            for (var i = Roots.Count - 1; i >= 0; i--)
            {
                if (Roots[i].TryGetTarget(out var root) && root != null) SwapPaletteSheet(root);
                else Roots.RemoveAt(i);
            }
        }

        private static void SwapPaletteSheet(VisualElement root)
        {
            foreach (var sheet in PaletteSheets.Values)
                if (sheet != null && root.styleSheets.Contains(sheet)) root.styleSheets.Remove(sheet);

            var name = _active.UssName;
            if (string.IsNullOrEmpty(name)) return;

            if (!PaletteSheets.TryGetValue(name, out var palette) || palette == null)
            {
                palette = AssetDatabase.LoadAssetAtPath<StyleSheet>($"{UssDir}/{name}.uss");
                PaletteSheets[name] = palette;
            }

            if (palette != null) root.styleSheets.Add(palette);
        }

        private sealed class Provider : IPageTheme
        {
            public Color Get(NexusToken token) => NexusTheme.Get(token);
            public NexusRoleColors GetRole(NexusRole role) => NexusTheme.GetRole(role);
            public NexusMetrics Metrics => NexusTheme.Metrics;
        }
    }
}
