using System.Collections.Generic;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Расчёт Apply до любых файловых операций (<see cref="ApplyService.Plan"/>): что выключится, что развернётся,
    /// что отклонил dependency-gate, каким страницам нужны UPM-пакеты. Ничего не меняет на диске — поэтому по нему
    /// и окно Manage строит сводный диалог, и CLI печатает сводку без модалок.
    /// </summary>
    public sealed class ApplyPlan
    {
        /// <summary>Развёртки, прошедшие gate и не ждущие пакетов.</summary>
        public readonly List<PendingIntent> Deploys = new List<PendingIntent>();

        /// <summary>Выключения (вето CanClose проверяется уже при выполнении).</summary>
        public readonly List<PendingIntent> Undeploys = new List<PendingIntent>();

        /// <summary>Развёртки, которым нужны отсутствующие в проекте UPM-пакеты (двухфазная установка).</summary>
        public readonly List<DiscoveredPlugin> NeedsPackages = new List<DiscoveredPlugin>();

        /// <summary>Строки сводки деструктивных операций («• выключить …»).</summary>
        public readonly List<string> Destructive = new List<string>();

        /// <summary>Отказы gate («• «имя»: причины»).</summary>
        public readonly List<string> Refusals = new List<string>();

        /// <summary>Предупреждения о версиях сейва.</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Пакеты, объявленные страницам, но уже найденные в проекте.</summary>
        public readonly List<string> LocalNotes = new List<string>();

        /// <summary>Результат дискавери на момент расчёта, по id.</summary>
        public Dictionary<string, DiscoveredPlugin> ById = new Dictionary<string, DiscoveredPlugin>();

        /// <summary>Есть ли что выполнять (без учёта отказов).</summary>
        public bool HasWork => Deploys.Count > 0 || Undeploys.Count > 0 || NeedsPackages.Count > 0;
    }
}
