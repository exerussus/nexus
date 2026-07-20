using System.Collections.Generic;
using System.Linq;
using Exerussus.Nexus.Deployment;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Вторая фаза установки «пакет → плагин». Когда Apply прописал отсутствующий
    /// UPM-пакет и отложил развёртку страницы, мы НЕ деплоим её сразу: код страницы
    /// не скомпилируется против ещё не подтянутой сборки. Пакет резолвится асинхронно,
    /// возможно за несколько reload — поэтому отложенные намерения живут в SessionState
    /// и проверяются на каждом старте домена: как только ВСЕ пакеты страницы реально
    /// разрешены (packages-lock.json), разворачиваем её (это второй, безопасный рекомпил).
    /// Пока не разрешены — ждём (не теряем намерение). Провал/удаление манифеста — отмена.
    /// </summary>
    [InitializeOnLoad]
    public static class PackageDeployContinuation
    {
        internal const string PendingKey = "Exerussus.Nexus.pendingPackageDeploy";

        static PackageDeployContinuation()
        {
            // отложенно — чтобы не дёргать AssetDatabase прямо в инициализации домена
            EditorApplication.delayCall += Run;
        }

        internal static void Enqueue(IEnumerable<string> ids)
        {
            var set = Current();
            set.UnionWith(ids);
            SessionState.SetString(PendingKey, string.Join("\n", set));
        }

        private static HashSet<string> Current()
        {
            var raw = SessionState.GetString(PendingKey, string.Empty);
            var set = new HashSet<string>(System.StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(raw))
                foreach (var s in raw.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
            return set;
        }

        private static void Run()
        {
            var pending = Current();
            if (pending.Count == 0) return;

            var byId = PluginDiscovery.Discover().ToDictionary(d => d.Id);
            var stillWaiting = new HashSet<string>(System.StringComparer.Ordinal);
            var report = new List<string>();
            var deployedAny = false;

            foreach (var id in pending)
            {
                if (!byId.TryGetValue(id, out var dp) || dp.Manifest == null)
                {
                    report.Add($"отложенный деплой «{id}»: манифест пропал — отменено");
                    continue;
                }
                if (dp.Status == PluginStatus.Deployed) continue;   // уже развёрнут — снять с ожидания

                var unresolved = dp.Manifest.PackageRequires?
                    .Where(p => !PackageRegistry.IsResolved(p.Name)).ToList();
                if (unresolved != null && unresolved.Count > 0)
                {
                    stillWaiting.Add(id);   // пакет ещё тянется — ждём следующего reload
                    continue;
                }

                report.Add("после установки пакета развёрнут: " + PluginDeployer.Deploy(dp.Manifest).Message);
                deployedAny = true;
            }

            if (stillWaiting.Count > 0) SessionState.SetString(PendingKey, string.Join("\n", stillWaiting));
            else SessionState.EraseString(PendingKey);

            if (report.Count > 0) Debug.Log("[Nexus] Отложенная установка:\n" + string.Join("\n", report));
            if (deployedAny) AssetDatabase.Refresh();   // развёрнутый код скомпилируется → ещё один reload
        }
    }
}
