using System.Collections.Generic;
using System.Linq;

namespace Exerussus.Nexus.Theme
{
    /// <summary>Реестр встроенных палитр (данные). Имя — ключ для выбора/персиста.</summary>
    public static class NexusPalettes
    {
        public static readonly NexusPalette SoftDark = NexusPalette.SoftDark();
        public static readonly NexusPalette Graphite = NexusPalette.Graphite();

        private static readonly NexusPalette[] _all = { SoftDark, Graphite };

        public static IReadOnlyList<NexusPalette> All => _all;

        public static IEnumerable<string> Names => _all.Select(p => p.Name);

        /// <summary>Палитра по имени; неизвестное имя → дефолтная SoftDark.</summary>
        public static NexusPalette Get(string name)
            => _all.FirstOrDefault(p => p.Name == name) ?? SoftDark;
    }
}
