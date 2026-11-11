using System;
using System.Collections.Generic;
using Exerussus.Nexus.Manifests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>Проектный конфиг Nexus (коммитится, вне основной папки —
    /// ProjectSettings/Nexus.json): доп. корни плагинов — ручные и найденные по маркеру.</summary>
    public sealed class NexusConfig
    {
        /// <summary>Ручные пути (относительно корня проекта), добавленные через Manage.</summary>
        public List<string> ScanPaths { get; set; } = new List<string>();

        /// <summary>Корни, найденные по маркеру <see cref="PluginRootMarker.FileName"/>.
        /// Перезаписываются целиком каждым поиском; ручные пути поиск не трогает.</summary>
        public List<DiscoveredRoot> DiscoveredRoots { get; set; } = new List<DiscoveredRoot>();

        /// <summary>Неизвестные поля (от более новой версии Nexus) — сохраняются дословно.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>Найденный корень плагинов. Путь — asset-путь («Assets/…» или
    /// «Packages/&lt;имя&gt;/…»): одинаков на всех машинах, реальный путь вычисляется при чтении.</summary>
    public sealed class DiscoveredRoot
    {
        public string AssetPath { get; set; }
        public string Name      { get; set; }
    }

    /// <summary>
    /// Чтение/запись проектного конфига. Хранится в ProjectSettings (вне Assets, вне
    /// основной папки Nexus) — поэтому обновление/переустановка Nexus его не трогает.
    /// Явный владелец — этот модуль; кэш перечитывается, когда файл поменялся на диске
    /// (git pull, правка руками, другой процесс) — сверка по времени изменения и размеру.
    /// </summary>
    public static class NexusConfigStore
    {
        private static NexusConfig _cached;
        private static DateTime    _cachedStamp;
        private static long        _cachedLength = -1;

        public static NexusConfig Load()
        {
            var path = NexusPaths.ProjectConfigPath;
            Stamp(path, out var stamp, out var length);
            if (_cached != null && stamp == _cachedStamp && length == _cachedLength) return _cached;

            _cached = JsonIo.Load<NexusConfig>(path) ?? new NexusConfig();
            _cached.ScanPaths       ??= new List<string>();
            _cached.DiscoveredRoots ??= new List<DiscoveredRoot>();
            _cachedStamp  = stamp;
            _cachedLength = length;
            return _cached;
        }

        // нет файла → (MinValue, -1): появление файла тоже считается изменением
        private static void Stamp(string path, out DateTime stamp, out long length)
        {
            stamp  = DateTime.MinValue;
            length = -1;
            try
            {
                var fi = new System.IO.FileInfo(path);
                if (!fi.Exists) return;
                stamp  = fi.LastWriteTimeUtc;
                length = fi.Length;
            }
            catch (Exception ex) { Exerussus.Nexus.Abstractions.NexusDiagnostics.Swallowed("время изменения " + path, ex); }
        }

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

        /// <summary>
        /// Заменить список найденных корней. Порядок стабильный (по пути), запись — только
        /// при реальном изменении, чтобы повторный поиск не давал пустого диффа в git.
        /// </summary>
        /// <returns><c>true</c>, если конфиг изменился.</returns>
        public static bool SetDiscoveredRoots(IEnumerable<DiscoveredRoot> roots)
        {
            var next = new List<DiscoveredRoot>(roots ?? Array.Empty<DiscoveredRoot>());
            next.Sort((a, b) => string.CompareOrdinal(a.AssetPath, b.AssetPath));

            var cfg = Load();
            if (Same(cfg.DiscoveredRoots, next)) return false;

            cfg.DiscoveredRoots = next;
            Save();
            return true;
        }

        private static bool Same(List<DiscoveredRoot> a, List<DiscoveredRoot> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i].AssetPath != b[i].AssetPath || a[i].Name != b[i].Name) return false;
            return true;
        }

        private static void Save()
        {
            var path = NexusPaths.ProjectConfigPath;
            JsonIo.Save(path, _cached);
            // своя запись — не повод перечитывать: запоминаем новую отметку файла
            Stamp(path, out _cachedStamp, out _cachedLength);
        }
    }
}
