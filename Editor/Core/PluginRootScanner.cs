using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>Итог поиска корней плагинов — для статуса в Manage.</summary>
    public sealed class RootScanResult
    {
        public int Found;
        public List<string> Added    = new List<string>();
        public List<string> Removed  = new List<string>();
        public List<string> Warnings = new List<string>();
        public bool ConfigChanged;

        public string Summary()
        {
            var s = $"Корней найдено: {Found}; новых: {Added.Count}; пропало: {Removed.Count}.";
            if (Warnings.Count > 0) s += " Внимание: " + string.Join("; ", Warnings);
            return s;
        }
    }

    /// <summary>
    /// Поиск корней плагинов по маркеру <see cref="PluginRootMarker.FileName"/> во всём, что
    /// видит AssetDatabase (Assets и все пакеты, включая git-пакеты из кэша).
    /// </summary>
    /// <remarks>
    /// Запускается ТОЛЬКО вручную — кнопкой в Manage: никаких фоновых проходов и хуков
    /// импорта, чтобы не нагружать редактор (off means off). Результат пишется в
    /// ProjectSettings/Nexus.json и дальше читается дискавери без повторного поиска.
    /// Статический stateless-сервис: состояние живёт в конфиге, не здесь.
    /// </remarks>
    public static class PluginRootScanner
    {
        public static RootScanResult Scan()
        {
            var result = new RootScanResult();
            var builtIn = NexusPaths.ToAssetPath(NexusPaths.PluginsRoot);

            var found = new List<DiscoveredRoot>();
            var seen  = new HashSet<string>(StringComparer.Ordinal);

            // Имя-фильтр грубый (подстрока), поэтому ниже сверяем точное имя файла.
            foreach (var guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(PluginRootMarker.FileName)))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (!string.Equals(Path.GetFileName(assetPath), PluginRootMarker.FileName, StringComparison.Ordinal)) continue;

                var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(dir) || !seen.Add(dir)) continue;

                // встроенный Plugins/ и так всегда в корнях — маркер там не нужен
                if (dir == builtIn) continue;

                var abs = NexusPaths.AssetToAbsolute(assetPath);
                var marker = JsonIo.Load<PluginRootMarker>(abs);
                if (marker == null)
                {
                    Warn(result, $"маркер не прочитан, корень пропущен: {assetPath}", hard: true);
                    continue;
                }

                if (marker.SchemaVersion > 1)
                    Warn(result, $"маркер новее этой версии Nexus (schemaVersion {marker.SchemaVersion}), читаю как 1: {assetPath}");

                var rootAbs = NexusPaths.AssetToAbsolute(dir);
                if (CountPlugins(rootAbs) == 0)
                    Warn(result, $"в корне '{dir}' нет ни одного <id>/manifest.json");

                found.Add(new DiscoveredRoot
                {
                    AssetPath = dir,
                    Name      = string.IsNullOrWhiteSpace(marker.Name) ? null : marker.Name.Trim(),
                });
            }

            var before = NexusConfigStore.Load().DiscoveredRoots.Select(r => r.AssetPath).ToList();
            var after  = found.Select(r => r.AssetPath).ToList();

            result.Found   = found.Count;
            result.Added   = after.Except(before).ToList();
            result.Removed = before.Except(after).ToList();
            result.ConfigChanged = NexusConfigStore.SetDiscoveredRoots(found);

            NexusDiagnostics.Trace("Корни плагинов", result.Summary() +
                (after.Count > 0 ? "\n  " + string.Join("\n  ", after) : string.Empty));
            return result;
        }

        private static int CountPlugins(string rootAbs)
        {
            try
            {
                if (string.IsNullOrEmpty(rootAbs) || !Directory.Exists(rootAbs)) return 0;
                return Directory.GetDirectories(rootAbs).Count(d => File.Exists(Path.Combine(d, "manifest.json")));
            }
            catch (Exception ex)
            {
                NexusDiagnostics.Swallowed("подсчёт плагинов в " + rootAbs, ex);
                return 0;
            }
        }

        // hard — настоящая ошибка (всегда в консоль); иначе — только в статус и подробный лог
        private static void Warn(RootScanResult result, string message, bool hard = false)
        {
            result.Warnings.Add(message);
            if (hard) NexusDiagnostics.Error("Корни плагинов", message);
            else      NexusDiagnostics.Warn("Корни плагинов", message);
        }
    }
}
