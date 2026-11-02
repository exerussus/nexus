using System;
using System.Collections.Generic;
using Exerussus.Nexus.Deployment;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Навигация рабочего окна — компонент ядра хаба (владелец — ядро). Сменить активную страницу можно только
    /// через него: команда <c>page.open</c>, страница — <c>IPageContext.OpenPage</c>. Решает и переключает хаб:
    /// просящий передаёт только id, чужой объект страницы не получает; целевая страница получает обычные
    /// OnEnter/OnFocus. Данные между страницами — по-прежнему через шину.
    /// <para>Ядро не видит сборку окна (UI ссылается на Core, не наоборот), поэтому окно само регистрирует
    /// «открыть окно» и подписывается на <see cref="Requested"/>. Окно закрыто — выбор пишется в
    /// <see cref="NexusSelectionStore"/>, окно открывается и берёт его при сборке. Состояния, кроме этих двух
    /// ссылок от окна, нет; фоновой работы нет.</para>
    /// </summary>
    public static class NexusNavigation
    {
        /// <summary>Встроенная страница «Nexus Health».</summary>
        public const string HealthId = "::health";

        /// <summary>Окно открыто и переключит страницу само (id, отдать ли окну фокус).</summary>
        public static event Action<string, bool> Requested;

        private static Action<bool> _openWindow;

        /// <summary>Окно регистрирует, как себя открыть (focus — отдать окну фокус).</summary>
        public static void RegisterWindow(Action<bool> open) => _openWindow = open;

        /// <summary>Активная страница (последний выбор; «Nexus Health», если не выбирали).</summary>
        public static string ActiveId => NexusSelectionStore.ActiveId ?? HealthId;

        /// <summary>Страница для навигации: id, имя, видимость.</summary>
        public sealed class PageEntry
        {
            public string Id;
            public string Name;
            public string Category;
            public bool Visible;
        }

        /// <summary>Развёрнутые страницы (видимые и скрытые) + «Nexus Health».</summary>
        public static List<PageEntry> Pages()
        {
            var list = new List<PageEntry> { new PageEntry { Id = HealthId, Name = "Nexus Health", Category = "Nexus", Visible = true } };
            foreach (var p in PluginDiscovery.Discover())
            {
                if (p.Status != PluginStatus.Deployed) continue;
                list.Add(new PageEntry { Id = p.Id, Name = p.DisplayName, Category = p.Category, Visible = NexusView.IsVisible(p.Id) });
            }

            return list;
        }

        /// <summary>
        /// Найти страницу по id или отображаемому имени (без учёта регистра); «health» — Nexus Health.
        /// null — не нашлась или открыть нельзя, причина — в <paramref name="error"/>.
        /// </summary>
        public static string Resolve(string query, out string error)
        {
            error = null;
            query = query?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                error = "нужна страница: id или имя (page.list)";
                return null;
            }

            if (Same(query, HealthId) || Same(query, "health") || Same(query, "Nexus Health")) return HealthId;

            PageEntry found = null;
            var byName = new List<PageEntry>();
            foreach (var e in Pages())
            {
                if (e.Id == HealthId) continue;
                if (Same(e.Id, query)) { found = e; break; }
                if (Same(e.Name, query)) byName.Add(e);
            }

            if (found == null && byName.Count > 1)
            {
                error = $"имя «{query}» у нескольких страниц — укажите id: " + string.Join(", ", byName.ConvertAll(x => x.Id));
                return null;
            }

            found ??= byName.Count == 1 ? byName[0] : null;
            if (found == null)
            {
                foreach (var p in PluginDiscovery.Discover())
                    if (Same(p.Id, query) || Same(p.DisplayName, query))
                    {
                        error = $"страница «{p.DisplayName}» не развёрнута — Manage → включить → Apply";
                        return null;
                    }

                error = $"нет страницы «{query}» (page.list)";
                return null;
            }

            if (!found.Visible)
            {
                error = $"страница «{found.Name}» скрыта — Manage → показать";
                return null;
            }

            return found.Id;
        }

        /// <summary>
        /// Показать страницу в рабочем окне. Окно закрыто — откроется. <paramref name="focus"/> — отдать окну
        /// фокус (по умолчанию нет: человек продолжает работать там, где был). false — причина в <paramref name="error"/>.
        /// </summary>
        public static bool Open(string query, bool focus, out string id, out string error)
        {
            id = Resolve(query, out error);
            if (id == null) return false;

            if (Requested != null)
            {
                Requested(id, focus);
                return true;
            }

            // окна нет — выбор в стор, окно возьмёт его при сборке
            NexusSelectionStore.ActiveId = id;
            if (_openWindow == null)
            {
                error = "окно Nexus недоступно (UI не загружен)";
                return false;
            }

            _openWindow(focus);
            return true;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
