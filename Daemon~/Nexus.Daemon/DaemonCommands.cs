using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Команды, которые выполняет сам демон, не обращаясь к редактору: они нужны именно тогда, когда главный
    /// поток Unity стоит (модальное окно). Пространство имён — <c>dialog.*</c>; остальное уходит в Bridge.
    /// </summary>
    internal static class DaemonCommands
    {
        public static readonly (string Name, string Usage, string Summary)[] All =
        {
            ("dialog.list", "dialog.list [--raw] [--no-shot]", "системные диалоги Windows редактора: заголовок, текст, кнопки, снимок (только чтение)"),
        };

        public static bool Owns(string command) => command != null && command.StartsWith("dialog.", StringComparison.Ordinal);

        public static (int Exit, string Output, string Result) Run(Call call)
        {
            switch (call.Command)
            {
                case "dialog.list": return DialogList(call);
                default:
                    return (3, $"✗ Нет команды демона «{call.Command}». Есть: {string.Join(", ", All.Select(c => c.Name))}\n", null);
            }
        }

        // ------------------------------------------------------------------ dialog.list

        private static (int, string, string) DialogList(Call call)
        {
            var flags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in call.Args)
            {
                if (a == "--raw" || a == "--no-shot") flags.Add(a);
                else return (2, $"✗ Неизвестный аргумент «{a}». Использование: dialog.list [--raw] [--no-shot]\n", null);
            }

            var p = call.Project;
            if (!WinApi.Supported) return (1, "✗ Диалоги видны только в Windows.\n", null);
            if (p.Pid <= 0 || !p.PidAlive) return (10, "✗ Редактор проекта закрыт.\n", null);

            var raw = flags.Contains("--raw");
            var windows = WinApi.Windows(p.Pid);
            var dialogs = Dialogs.Find(p.Pid, windows);
            if (!flags.Contains("--no-shot"))
                foreach (var d in dialogs)
                    d.Shot = Dialogs.SaveShot(p, d);

            var data = new JsonObject
            {
                ["project"] = p.Id,
                ["pid"] = p.Pid,
                ["editor"] = p.State,
                ["dialogs"] = new JsonArray(dialogs.Select(d => (JsonNode)Dialogs.ToJson(d, raw)).ToArray()),
            };
            if (raw)
                data["windows"] = new JsonArray(windows.Select(w => (JsonNode)new JsonObject
                {
                    ["hwnd"] = w.Handle,
                    ["class"] = w.Class,
                    ["enabled"] = w.Enabled,
                    ["title"] = Dialogs.Cut(w.Title, 200),
                }).ToArray());

            var sb = new StringBuilder();
            if (dialogs.Count == 0)
            {
                sb.Append($"Диалогов нет (окон редактора: {windows.Count}, состояние: {p.State}).\n");
                if (p.State == "stalled") sb.Append("! Редактор молчит, но системного диалога нет — смотрите nexus-fs diag (окно Unity, фоновое ожидание).\n");
            }
            else
            {
                sb.Append($"Диалогов: {dialogs.Count}\n");
                foreach (var d in dialogs)
                {
                    var kind = d.Kind switch
                    {
                        "native" => "",
                        "taskdialog" => " — TaskDialog: кнопки не видны как окна",
                        _ => " — кнопок не нашлось",
                    };
                    sb.Append($"• {d.Id} «{d.Title}»{kind}{(d.OwnedByEditor ? "" : " (владелец не окно редактора)")}{(d.Hung ? " — окно не отвечает, данные неполные" : "")}\n");
                    if (d.Text.Length > 0) sb.Append("  текст: ").Append(Dialogs.Cut(d.Text.Replace("\n", " ⏎ "), 400)).Append('\n');
                    if (d.Buttons.Count > 0)
                        sb.Append("  кнопки: ").Append(string.Join(" ", d.Buttons.Select(b => "[" + b.Label + (b.Default ? "*" : "") + (b.Enabled ? "" : " (недоступна)") + "]"))).Append('\n');
                    if (d.Toggles.Count > 0) sb.Append("  флажки: ").Append(string.Join(", ", d.Toggles)).Append('\n');
                    if (d.Shot != null) sb.Append("  снимок: NexusTemp/").Append(d.Shot).Append('\n');
                    if (raw)
                        foreach (var c in d.Children)
                            sb.Append($"    {c.Class} #{c.CtrlId} 0x{c.Style:X8}{(c.Visible ? "" : " скрыт")}{(c.Enabled ? "" : " выкл")}: {Dialogs.Cut(c.Text, 120)}\n");
                }

                sb.Append("* — кнопка по умолчанию. Нажатие через демон — следующий этап; сейчас решает человек в окне.\n");
            }

            if (raw)
            {
                sb.Append("Окна процесса:\n");
                foreach (var w in windows)
                    sb.Append($"  {w.Handle} {w.Class}{(w.Enabled ? "" : " (выкл)")}: {Dialogs.Cut(w.Title, 120)}\n");
            }

            return (0, sb.ToString(), Json.Write(data));
        }
    }
}
