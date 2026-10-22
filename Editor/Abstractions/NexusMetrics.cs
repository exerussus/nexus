namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Метрики палитры — то, что кроме цвета делает вид: скругления, шаги отступов, кегли и
    /// характер заголовков. Страница берёт их через <see cref="IPageTheme.Metrics"/>, а не пишет
    /// свои 6f и 8f — тогда смена палитры меняет и форму.
    /// </summary>
    public readonly struct NexusMetrics
    {
        /// <summary>Скругление мелочей: метки, плашки кода.</summary>
        public readonly float RadiusS;

        /// <summary>Скругление контролов: кнопки, поля, строки.</summary>
        public readonly float RadiusM;

        /// <summary>Скругление поверхностей: карточки, всплывающие.</summary>
        public readonly float RadiusL;

        /// <summary>Заголовки моноширинным шрифтом.</summary>
        public readonly bool MonoTitles;

        /// <summary>Заголовки капслоком с разрядкой.</summary>
        public readonly bool UpperTitles;

        public NexusMetrics(float radiusS, float radiusM, float radiusL, bool monoTitles, bool upperTitles)
        {
            RadiusS = radiusS;
            RadiusM = radiusM;
            RadiusL = radiusL;
            MonoTitles = monoTitles;
            UpperTitles = upperTitles;
        }

        // Шаги отступов и кегли общие для всех палитр.
        public const float Space1 = 4f;
        public const float Space2 = 6f;
        public const float Space3 = 8f;
        public const float Space4 = 12f;
        public const float Space5 = 16f;
        public const float Space6 = 20f;

        public const int FontPage = 16;
        public const int FontCard = 13;
        public const int FontBody = 12;
        public const int FontHint = 11;
        public const int FontSection = 10;
    }
}
