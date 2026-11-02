using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Команды, которые выполняет сам демон, не обращаясь к редактору: они нужны именно тогда, когда главный
    /// поток Unity стоит (модальное окно). <c>dialog.*</c>, <c>trash.clean</c> (корзина файлового канала, efc) и
    /// <c>ping</c> файлового канала; остальное уходит в Bridge (<c>ping</c> по HTTP — тоже: он про главный поток Unity).
    /// </summary>
    internal static class DaemonCommands
    {
        public static readonly (string Name, string Usage, string Summary)[] All =
        {
            ("dialog.list", "dialog.list [--raw] [--no-shot]", "системные диалоги Windows редактора: заголовок, текст, кнопки, снимок (только чтение)"),
            ("trash.clean", "trash.clean [--dry]", "очистить корзину файлового канала NexusTemp/cli/trash (клиенту в ВМ удалять нельзя)"),
            ("ping", "ping", "в файловом канале — ответ демона без редактора (по HTTP — пинг главного потока Unity)"),
        };

        public static bool Owns(Call call)
        {
            var c = call.Command;
            if (c == null) return false;
            if (c.StartsWith("dialog.", StringComparison.Ordinal) || c == "trash.clean") return true;
            // ping демона — только клиентам efc (и шагам их пакетов); nexus-fs и HTTP пингуют главный поток Unity, как раньше
            return c == "ping" && (call.Source == "file-efc" || call.Source == "file-batch");
        }

        /// <summary>Раздел «команды демона» к выводу help редактора.</summary>
        public static string HelpSection()
        {
            var sb = new StringBuilder("\nКоманды демона (работают и при зависшем редакторе):\n");
            var w = All.Max(c => c.Name.Length);
            foreach (var c in All) sb.Append("  ").Append(c.Name.PadRight(w)).Append("  ").Append(c.Summary).Append('\n');
            return sb.ToString();
        }

        public static (int Exit, string Output, string Result) Run(Call call)
        {
            switch (call.Command)
            {
                case "dialog.list": return DialogList(call);
                case "trash.clean": return TrashClean(call);
                case "ping": return Ping(call);
                default:
                    return (3, $"✗ Нет команды демона «{call.Command}». Есть: {string.Join(", ", All.Select(c => c.Name))}\n", null);
            }
        }

        // ------------------------------------------------------------------ trash.clean, ping

        /// <summary>Удалить файлы корзины канала (только обычные файлы прямо в trash/). <c>--dry</c> — только посчитать.</summary>
        public static (int, string, string) TrashClean(Call call, double olderThan = 0)
        {
            var dry = false;
            foreach (var a in call?.Args ?? Array.Empty<string>())
            {
                if (a == "--dry") dry = true;
                else return (2, $"✗ Неизвестный аргумент «{a}». Использование: trash.clean [--dry]\n", null);
            }

            var (files, bytes) = CleanTrash(System.IO.Path.Combine(call.Project.CliDir, "trash"), dry, olderThan);
            var size = bytes < 1024 ? $"{bytes} Б" : bytes < 1024 * 1024 ? $"{(bytes + 512) / 1024} КБ" : $"{bytes / 1048576.0:0.0} МБ";
            var text = dry ? $"Корзина: {files} {Plural(files)}, {size} — ничего не удалено\n" : $"Корзина: удалено {files} {Plural(files)}, {size}\n";
            return (0, text, Json.Write(new JsonObject { ["files"] = files, ["bytes"] = bytes, ["dry"] = dry }));
        }

        /// <summary>Очистка корзины: обычные файлы (не папки, не ссылки) старше olderThan секунд.</summary>
        public static (long Files, long Bytes) CleanTrash(string dir, bool dry, double olderThan)
        {
            long files = 0, bytes = 0;
            if (!System.IO.Directory.Exists(dir)) return (0, 0);
            var now = DateTime.UtcNow;
            foreach (var path in System.IO.Directory.GetFiles(dir))
            {
                try
                {
                    var info = new System.IO.FileInfo(path);
                    if (info.LinkTarget != null || (info.Attributes & System.IO.FileAttributes.ReparsePoint) != 0) continue;
                    if (olderThan > 0 && (now - info.LastWriteTimeUtc).TotalSeconds <= olderThan) continue;
                    var len = info.Length;
                    if (!dry) info.Delete();
                    files++;
                    bytes += len;
                }
                catch (Exception e)
                {
                    Log.Warn($"корзина {path}: {e.Message}");
                }
            }

            return (files, bytes);
        }

        private static string Plural(long n)
        {
            var d = n % 10;
            var h = n % 100;
            if (d == 1 && h != 11) return "файл";
            if (d >= 2 && d <= 4 && (h < 12 || h > 14)) return "файла";
            return "файлов";
        }

        private static (int, string, string) Ping(Call call)
        {
            var p = call.Project;
            var data = new JsonObject
            {
                ["server"] = "nexusd",
                ["version"] = DaemonInfo.Version,
                ["editor"] = p.State,
                ["phase"] = p.Phase,
            };
            return (0, "pong\n", Json.Write(data));
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
