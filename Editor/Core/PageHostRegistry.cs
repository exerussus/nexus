using System.Collections.Generic;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Реестр живых хостов. Выключение плагина инициируется в окне управления и
    /// доходит до файловых операций, а живой инстанс страницы держит рабочее окно —
    /// этот реестр их соединяет, чтобы при выключении честно отработали CanClose
    /// (вето) и OnPrePack (сброс данных перед упаковкой). Явный владелец — модуль
    /// Core; хосты регистрируются в конструкторе и снимаются в Dispose.
    /// </summary>
    public static class PageHostRegistry
    {
        private static readonly List<PageHost> Hosts = new List<PageHost>();

        internal static void Register(PageHost host)
        {
            if (!Hosts.Contains(host)) Hosts.Add(host);
        }

        internal static void Unregister(PageHost host) => Hosts.Remove(host);

        /// <summary>Можно ли выключать id: false, если хоть один хост держит не готовую страницу.</summary>
        public static bool CanClose(string id)
        {
            foreach (var h in Hosts.ToArray())
                if (!h.CanClose(id)) return false;
            return true;
        }

        /// <summary>Дать всем хостам id свернуть страницу и сбросить данные перед упаковкой.</summary>
        public static void PrePack(string id)
        {
            foreach (var h in Hosts.ToArray())
                h.PrePack(id);
        }
    }
}
