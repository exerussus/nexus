using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Шрифты хаба: Inter — основной, JetBrains Mono — заголовки Neon и код. Лежат в пакете
    /// (Editor/Theme/Fonts, лицензия OFL — рядом). Из TTF собираются динамические шрифтовые ассеты
    /// в памяти; жирное начертание подключено таблицей весов, чтобы bold был настоящим, а не
    /// растянутым regular. Нет файла — остаётся шрифт редактора.
    /// </summary>
    public static class NexusFonts
    {
        private const string Dir = "Packages/com.exerussus.nexus/Editor/Theme/Fonts";

        private static FontAsset _sans;
        private static FontAsset _mono;
        private static bool _loaded;

        public static FontAsset Sans
        {
            get
            {
                Load();
                return _sans;
            }
        }

        public static FontAsset Mono
        {
            get
            {
                Load();
                return _mono;
            }
        }

        /// <summary>Основной шрифт на корень окна — наследуется всем деревом.</summary>
        public static void ApplySans(VisualElement element)
        {
            var font = Sans;
            if (element != null && font != null) element.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
        }

        public static void ApplyMono(VisualElement element)
        {
            var font = Mono;
            if (element != null && font != null) element.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
        }

        private static void Load()
        {
            // Ассеты в памяти переживают перекомпиляцию плохо — после перезагрузки домена собираем заново.
            if (_loaded && (_sans != null || _mono != null)) return;
            _loaded = true;

            _sans = Pair("Inter-Regular.ttf", "Inter-Bold.ttf");
            _mono = Pair("JetBrainsMono-Regular.ttf", "JetBrainsMono-Bold.ttf");
        }

        private static FontAsset Pair(string regularFile, string boldFile)
        {
            var regular = Create(regularFile);
            if (regular == null) return null;

            var bold = Create(boldFile);
            if (bold != null)
            {
                var table = regular.fontWeightTable;
                if (table != null && table.Length > 7) table[7].regularTypeface = bold;   // 700 — Bold
            }

            return regular;
        }

        private static FontAsset Create(string file)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(Dir + "/" + file);
            if (font == null) return null;

            var asset = FontAsset.CreateFontAsset(font);
            if (asset != null) asset.hideFlags = HideFlags.HideAndDontSave;
            return asset;
        }
    }
}
