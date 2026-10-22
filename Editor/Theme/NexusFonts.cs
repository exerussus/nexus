using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
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
            // Живость проверяем целиком: шрифт без материала или атласа рисовать нельзя.
            if (_loaded && (Alive(_sans) || Alive(_mono))) return;
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

            // Один атлас без дозаказа новых: каждый объект шрифта создаётся здесь и сразу защищается.
            var asset = FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            if (asset == null) return null;

            // Шрифт собран в памяти, это не ассет на диске. Без DontSave редактор выгружает его материал и
            // атлас при сборке неиспользуемых ассетов (смена сцены, вход в префаб), а UI Toolkit падает
            // MissingReferenceException на каждом кадре и дальше — InvalidOperationException в рендере.
            Protect(asset);
            Protect(asset.material);
            if (asset.atlasTextures != null)
                foreach (var texture in asset.atlasTextures) Protect(texture);

            return asset;
        }

        private static void Protect(Object obj)
        {
            if (obj != null) obj.hideFlags = HideFlags.HideAndDontSave;
        }

        private static bool Alive(FontAsset asset) =>
            asset != null && asset.material != null && asset.atlasTexture != null;
    }
}
