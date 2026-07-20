using System.IO;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Core
{
    /// <summary>Хранит ИМЯ активной палитры — личный выбор вида (как nexus-view.json),
    /// одним файлом на уровне Nexus в UserSettings (git-ignored, переживает restore,
    /// вне основной папки). Здесь только строка; саму палитру по имени собирает слой UI
    /// (Core не ссылается на Theme).</summary>
    public sealed class NexusThemeConfig
    {
        public string Palette { get; set; }
    }

    public static class NexusThemeStore
    {
        private static string ConfigPath => Path.Combine(NexusPaths.UserRoot, "nexus-theme.json");

        /// <summary>Имя активной палитры или null, если не выбиралась.</summary>
        public static string ActiveName
        {
            get => JsonIo.Load<NexusThemeConfig>(ConfigPath)?.Palette;
            set => JsonIo.Save(ConfigPath, new NexusThemeConfig { Palette = value });
        }
    }
}
