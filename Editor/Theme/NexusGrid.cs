using Exerussus.Nexus.Abstractions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.Theme
{
    /// <summary>
    /// Фоновая сетка области страницы — плиточная текстура 24×24 с линией по верху и слева.
    /// Градиентов USS не умеет, а текстура в одну плитку стоит копейки. Цвет — токен Grid;
    /// прозрачный — без сетки.
    /// </summary>
    public static class NexusGrid
    {
        private const int Cell = 24;

        private static Texture2D _texture;
        private static Color _color;

        public static void Apply(VisualElement element)
        {
            if (element == null) return;

            var color = NexusTheme.Get(NexusToken.Grid);
            if (color.a <= 0.001f)
            {
                element.style.backgroundImage = StyleKeyword.Null;
                return;
            }

            element.style.backgroundImage = new StyleBackground(Texture(color));
            element.style.backgroundRepeat = new BackgroundRepeat(Repeat.Repeat, Repeat.Repeat);
            element.style.backgroundSize = new BackgroundSize(new Length(Cell), new Length(Cell));
            element.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Left);
            element.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Top);
        }

        private static Texture2D Texture(Color color)
        {
            if (_texture != null && _color == color) return _texture;

            if (_texture != null) Object.DestroyImmediate(_texture);

            _color = color;
            _texture = new Texture2D(Cell, Cell, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                name = "Nexus Grid",
            };

            var clear = new Color(color.r, color.g, color.b, 0f);
            var pixels = new Color[Cell * Cell];
            for (var y = 0; y < Cell; y++)
            for (var x = 0; x < Cell; x++)
                pixels[y * Cell + x] = x == 0 || y == Cell - 1 ? color : clear;

            _texture.SetPixels(pixels);
            _texture.Apply();
            return _texture;
        }
    }
}
