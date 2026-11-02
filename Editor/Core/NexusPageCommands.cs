using System.Collections.Generic;
using Exerussus.Nexus.Abstractions;

namespace Exerussus.Nexus.Core
{
    /// <summary>Команды рабочего окна: какие страницы есть и какую показать.</summary>
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
    }
}
