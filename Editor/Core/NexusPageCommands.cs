using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;

namespace Exerussus.Nexus.Core
{
    /// <summary>Команды рабочего окна: какие страницы есть, какую показать, какие скрыть из сайдбара.</summary>
    public static class NexusPageCommands
    {
        [NexusCommand("page.list", "Страницы рабочего окна Nexus: id, имя, видимость, активная",
            Usage = "page.list", ReadOnly = true)]
        private static int List(NexusCommandContext ctx)
        {
            var active = NexusNavigation.ActiveId;
            var pages = new List<object>();
            foreach (var p in NexusNavigation.Pages())
            {
                var on = p.Id == active;
                ctx.Print($"{(on ? "▶" : " ")} {p.Id,-24} {p.Name}{(p.Visible ? string.Empty : " (скрыта)")}");
                pages.Add(new { id = p.Id, name = p.Name, category = p.Category, visible = p.Visible, active = on });
            }

            ctx.Data = new { active, pages };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("page.open", "Показать страницу в рабочем окне Nexus (окно закрыто — откроется; --focus — отдать окну фокус)",
            Usage = "page.open <id|имя> [--focus]")]
        private static int Open(NexusCommandContext ctx)
        {
            var query = ctx.Arg(0) ?? throw new NexusCommandException("нужна страница: id или имя (page.list)");
            if (!NexusNavigation.Open(query, ctx.Flag("focus"), out var id, out var error))
                throw new NexusCommandException(error, NexusCommandContext.Exit.Failed);
            ctx.Print("✓ страница: " + id);
            ctx.Data = new { id };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("page.hide", "Скрыть страницу из сайдбара (остаётся развёрнутой и работает; личная настройка)",
            Usage = "page.hide <id|имя>")]
        private static int Hide(NexusCommandContext ctx) => SetVisible(ctx, false);

        [NexusCommand("page.show", "Вернуть скрытую страницу в сайдбар", Usage = "page.show <id|имя>")]
        private static int Show(NexusCommandContext ctx) => SetVisible(ctx, true);

        private static int SetVisible(NexusCommandContext ctx, bool visible)
        {
            var query = ctx.Arg(0) ?? throw new NexusCommandException("нужна страница: id или имя (plugin.list)");
            var all = PluginDiscovery.Discover();
            var p = all.Find(x => x.Id == query)
                    ?? all.Find(x => string.Equals(x.Id, query, System.StringComparison.OrdinalIgnoreCase))
                    ?? all.Find(x => string.Equals(x.DisplayName, query, System.StringComparison.OrdinalIgnoreCase))
                    ?? throw new NexusCommandException($"страница «{query}» не найдена (plugin.list)");

            var was = NexusView.IsVisible(p.Id);
            NexusView.SetVisible(p.Id, visible);
            ctx.Print(was == visible
                ? $"«{p.DisplayName}» уже {(visible ? "видна" : "скрыта")}"
                : $"✓ «{p.DisplayName}» {(visible ? "снова в сайдбаре" : "скрыта из сайдбара")}");
            if (p.Status != PluginStatus.Deployed) ctx.Warn("страница не развёрнута — настройка сработает после plugin.deploy " + p.Id);
            ctx.Data = new { id = p.Id, visible };
            return NexusCommandContext.Exit.Ok;
        }
    }
}
