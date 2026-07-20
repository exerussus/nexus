using System;
using System.Collections.Generic;
using System.IO;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Дискавери плагинов. Статический stateless-сервис: каждый вызов сканирует
    /// диск и возвращает свежий список, ничего не кэшируя между вызовами.
    ///
    /// ВАЖНО (инвариант скилла): обнаружение идёт ПО МАНИФЕСТАМ на диске, а не по
    /// рефлексии типов — иначе выгруженные/выключенные плагины были бы не видны.
    ///
    /// M1: read-only. Развёртка (Plugins↔State), Preserve и dependency-gate —
    /// следующие милстоуны.
    /// </summary>
    public static class PluginDiscovery
    {
        /// <summary>
        /// Слить манифесты Plugins и папки State по id и расставить статусы:
        ///   manifest + State  → Deployed
        ///   manifest, no State → Available
        ///   State, no manifest → OrphanedState
        /// </summary>
        public static List<DiscoveredPlugin> Discover()
        {
            var result = new List<DiscoveredPlugin>();

            // развёрнутые id (по папкам State)
            var deployedIds = new HashSet<string>(EnumerateChildDirNames(NexusPaths.StateRoot),
                                                  StringComparer.Ordinal);

            // плагины с манифестами — по всем корням (встроенный + доп. пути проекта);
            // дубль id в другом корне игнорируется (первый корень выигрывает)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in PluginRoots.Roots())
            {
                foreach (var id in EnumerateChildDirNames(root))
                {
                    if (seen.Contains(id)) continue;
                    var manifest = ManifestIo.Load(Path.Combine(root, id, "manifest.json"));
                    if (manifest == null) continue; // папка без валидного манифеста — не плагин

                    seen.Add(id);
                    var status = deployedIds.Contains(id) ? PluginStatus.Deployed : PluginStatus.Available;
                    result.Add(new DiscoveredPlugin(id, status, manifest));
                }
            }

            // осиротевшие состояния: есть в State, но манифеста к ним не нашлось
            foreach (var id in deployedIds)
            {
                if (seen.Contains(id)) continue;
                result.Add(new DiscoveredPlugin(id, PluginStatus.OrphanedState, null));
            }

            result.Sort(CompareForSidebar);
            return result;
        }

        // сортировка под сайдбар: сначала раздел, затем order, затем имя
        private static int CompareForSidebar(DiscoveredPlugin a, DiscoveredPlugin b)
        {
            var byCat = string.CompareOrdinal(a.Category, b.Category);
            if (byCat != 0) return byCat;
            var byOrder = a.Order.CompareTo(b.Order);
            if (byOrder != 0) return byOrder;
            return string.CompareOrdinal(a.DisplayName, b.DisplayName);
        }

        private static IEnumerable<string> EnumerateChildDirNames(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                yield break;

            foreach (var dir in Directory.EnumerateDirectories(root))
                yield return Path.GetFileName(dir);
        }
    }
}
