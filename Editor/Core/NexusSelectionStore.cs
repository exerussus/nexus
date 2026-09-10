using System.IO;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Core
{
    /// <summary>Личный выбор активной вкладки рабочего окна.</summary>
    public sealed class NexusSelectionConfig
    {
        public string ActiveId { get; set; }
    }

    /// <summary>
    /// Помнит, какая вкладка была открыта. В памяти окна это поле НЕ живёт достаточно
    /// долго: Unity пересоздаёт окна при разворачивании другого окна на весь экран
    /// (Game/Scene), при смене раскладки и при рекомпиле — поле сбрасывалось бы к
    /// дефолту. Поэтому владельцем выбора сделан отдельный стор: UserSettings —
    /// личное, git-ignored, вне папки движка, переживает рекомпил и перезапуск.
    ///
    /// Запись идёт только при РЕАЛЬНОЙ смене значения (перестроение сайдбара зовёт
    /// Select повторно тем же id — лишние записи в файл не нужны).
    /// </summary>
    public static class NexusSelectionStore
    {
        private static string _cached;
        private static bool _loaded;

        private static string ConfigPath => Path.Combine(NexusPaths.UserRoot, "nexus-selection.json");

        /// <summary>Id последней активной вкладки или null, если ещё не выбирали.</summary>
        public static string ActiveId
        {
            get
            {
                if (!_loaded)
                {
                    _cached = JsonIo.Load<NexusSelectionConfig>(ConfigPath)?.ActiveId;
                    _loaded = true;
                }
                return _cached;
            }
            set
            {
                if (_loaded && _cached == value) return;   // без изменений — не трогаем диск
                _cached = value;
                _loaded = true;
                JsonIo.Save(ConfigPath, new NexusSelectionConfig { ActiveId = value });
            }
        }
    }
}
