using System.Collections.Generic;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>Проектный конфиг Nexus (коммитится, вне основной папки —
    /// ProjectSettings/Nexus.json). Сейчас — только доп. пути сканирования плагинов.</summary>
    public sealed class NexusConfig
    {
        public List<string> ScanPaths { get; set; } = new List<string>();
    }

    /// <summary>
    /// Чтение/запись проектного конфига. Хранится в ProjectSettings (вне Assets, вне
    /// основной папки Nexus) — поэтому обновление/переустановка Nexus его не трогает.
    /// Явный владелец — этот модуль; кэш сбрасывается записью.
    /// </summary>
    public static class NexusConfigStore
    {
        private static NexusConfig _cached;

        public static NexusConfig Load()
            => _cached ??= JsonIo.Load<NexusConfig>(NexusPaths.ProjectConfigPath) ?? new NexusConfig();

        /// <summary>Добавить путь (относительный корню проекта); дубли и пустое игнорируются.</summary>
        public static void AddScanPath(string projectRelative)
        {
            if (string.IsNullOrWhiteSpace(projectRelative)) return;
            var cfg = Load();
            var norm = projectRelative.Replace('\\', '/').TrimEnd('/');
            if (cfg.ScanPaths.Contains(norm)) return;
            cfg.ScanPaths.Add(norm);
            Save();
        }

        public static void RemoveScanPath(string projectRelative)
        {
            var cfg = Load();
            if (cfg.ScanPaths.Remove(projectRelative)) Save();
        }

        private static void Save() => JsonIo.Save(NexusPaths.ProjectConfigPath, _cached);
    }
}
