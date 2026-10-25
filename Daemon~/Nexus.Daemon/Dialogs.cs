using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Системные диалоги Windows (#32770) процесса Unity: заголовок, текст, кнопки, снимок. Этап 1 — только
    /// чтение: ничего не нажимается, фокус и ввод не трогаются. Текст чужих контролов — через WM_GETTEXT
    /// (система переносит его между процессами), снимок — PrintWindow в память, без экрана.
    /// </summary>
    internal static class Dialogs
    {
        public sealed class Button
        {
            public long Handle;
            public int CtrlId;
            public string Label;
            public bool Default;
            public bool Enabled;
        }

        public sealed class Child
        {
            public string Class;
            public int CtrlId;
            public string Text;
            public bool Visible;
            public bool Enabled;
            public uint Style;
        }

        public sealed class Dialog
        {
            public string Id;
            public long Handle;
            public long Owner;
            public bool OwnedByEditor;
            public bool Enabled;
            public string Title;
            public string Text;

            /// <summary>Поток диалога не ответил — текст неполный, снимка нет.</summary>
            public bool Hung;

            /// <summary>native — кнопки найдены как окна; taskdialog — TaskDialog (кнопки не окна); unknown — кнопок не нашлось.</summary>
            public string Kind;

            public readonly List<Button> Buttons = new List<Button>();

            /// <summary>Флажки и прочие переключатели диалога (не кнопки выбора) — для сведения.</summary>
            public readonly List<string> Toggles = new List<string>();

            public readonly List<Child> Children = new List<Child>();
            public string Shot;
        }

        // ------------------------------------------------------------------ Win32

        private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern IntPtr SendMessageTimeoutPlain(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

        private const uint GW_OWNER = 4;
        private const int GWL_STYLE = -16;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_NULL = 0x0000;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const uint PW_RENDERFULLCONTENT = 0x0002;
        private static readonly IntPtr DpiPerMonitorV2 = new IntPtr(-4);

        // типы кнопок (BS_TYPEMASK)
        private const uint BS_TYPEMASK = 0x0F;
        private const uint BS_PUSHBUTTON = 0x00;
        private const uint BS_DEFPUSHBUTTON = 0x01;
        private const uint BS_GROUPBOX = 0x07;
        private const uint BS_OWNERDRAW = 0x0B;
        private const uint BS_SPLITBUTTON = 0x0C;
        private const uint BS_DEFSPLITBUTTON = 0x0D;
        private const uint BS_COMMANDLINK = 0x0E;
        private const uint BS_DEFCOMMANDLINK = 0x0F;

        // ------------------------------------------------------------------ поиск

        /// <summary>Все видимые диалоги #32770 процесса (окна уже перечислены вызывающим).</summary>
        public static List<Dialog> Find(int pid, List<WinApi.Window> windows)
        {
            var list = new List<Dialog>();
            if (!WinApi.Supported || pid <= 0 || windows == null) return list;
            foreach (var w in windows)
            {
                if (w.Class != "#32770") continue;
                try
                {
                    list.Add(Inspect(pid, w));
                }
                catch (Exception e)
                {
                    Log.Warn("диалог " + w.Handle + ": " + e.Message);
                }
            }

            return list;
        }

        private static Dialog Inspect(int pid, WinApi.Window w)
        {
            var hwnd = new IntPtr(w.Handle);
            var d = new Dialog
            {
                Handle = w.Handle,
                Title = w.Title ?? string.Empty,
                Enabled = w.Enabled,
                Owner = GetWindow(hwnd, GW_OWNER).ToInt64(),
            };
            // владелец — любое окно этого же процесса (главное, скрытое, другой диалог редактора)
            d.OwnedByEditor = d.Owner != 0 && OwnerPid(d.Owner) == (uint)pid;

            // поток диалога не отвечает — текст не спрашиваем (каждый WM_GETTEXT ждал бы впустую)
            var alive = Responds(hwnd);
            d.Hung = !alive;

            var texts = new List<string>();
            var taskDialog = false;
            EnumProc callback = (child, _) =>
            {
                var c = new Child
                {
                    Class = ClassOf(child),
                    CtrlId = GetDlgCtrlID(child),
                    Text = alive ? TextOf(child, ref alive) : string.Empty,
                    Visible = IsWindowVisible(child),
                    Enabled = IsWindowEnabled(child),
                    Style = (uint)GetWindowLong(child, GWL_STYLE),
                };
                d.Children.Add(c);
                if (c.Class == "DirectUIHWND") taskDialog = true;
                if (!c.Visible) return true;

                if (string.Equals(c.Class, "Button", StringComparison.OrdinalIgnoreCase))
                {
                    var type = c.Style & BS_TYPEMASK;
                    var label = Label(c.Text);
                    if (type == BS_GROUPBOX || label.Length == 0) return true;
                    if (type == BS_PUSHBUTTON || type == BS_DEFPUSHBUTTON || type == BS_COMMANDLINK || type == BS_DEFCOMMANDLINK ||
                        type == BS_OWNERDRAW || type == BS_SPLITBUTTON || type == BS_DEFSPLITBUTTON)
                        d.Buttons.Add(new Button
                        {
                            Handle = child.ToInt64(),
                            CtrlId = c.CtrlId,
                            Label = label,
                            Default = type == BS_DEFPUSHBUTTON || type == BS_DEFCOMMANDLINK || type == BS_DEFSPLITBUTTON,
                            Enabled = c.Enabled,
                        });
                    else
                        d.Toggles.Add(label);
                }
                else if (c.Class == "Static" || c.Class.StartsWith("Edit", StringComparison.OrdinalIgnoreCase) || c.Class.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
                {
                    var t = (c.Text ?? string.Empty).Trim();
                    if (t.Length > 0) texts.Add(t);
                }

                return true;
            };
            EnumChildWindows(hwnd, callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            d.Text = string.Join("\n", texts);
            if (alive == false) d.Hung = true;
            d.Kind = d.Buttons.Count > 0 ? "native" : taskDialog ? "taskdialog" : "unknown";
            d.Id = Fingerprint(pid, d);
            return d;
        }

        private static uint OwnerPid(long hwnd)
        {
            GetWindowThreadProcessId(new IntPtr(hwnd), out var owner);
            return owner;
        }

        /// <summary>Поток окна разбирает сообщения (WM_NULL за 500 мс). Нет — ни текст, ни снимок не просим: они ждали бы без конца.</summary>
        public static bool Responds(IntPtr hwnd) =>
            SendMessageTimeoutPlain(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out _) != IntPtr.Zero;

        /// <summary>Отпечаток: процесс + окно + заголовок + текст + надписи кнопок. Изменилось хоть что-то — другой id.</summary>
        public static string Fingerprint(int pid, Dialog d)
        {
            var key = $"{pid}|{d.Handle}|{d.Title}|{d.Text}|{string.Join("|", d.Buttons.Select(b => b.CtrlId + ":" + b.Label))}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return "d" + Convert.ToHexString(hash, 0, 3).ToLowerInvariant();
        }

        private static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Текст контрола; первый же таймаут выключает дальнейшие запросы к этому диалогу (ожидания не копятся).</summary>
        private static string TextOf(IntPtr hwnd, ref bool alive)
        {
            var sb = new StringBuilder(4096);
            var ok = SendMessageTimeout(hwnd, WM_GETTEXT, new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG, 500, out _);
            if (ok != IntPtr.Zero) return sb.ToString();
            alive = false;
            return string.Empty;
        }

        /// <summary>Надпись кнопки без амперсандов-ускорителей («&amp;Reload» → «Reload», «&amp;&amp;» → «&amp;»).</summary>
        public static string Label(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '&')
                {
                    if (i + 1 < text.Length && text[i + 1] == '&') { sb.Append('&'); i++; }
                    continue;
                }

                sb.Append(text[i]);
            }

            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------------ снимок

        /// <summary>Снимок окна в PNG (PrintWindow в память; на заблокированном экране может выйти чёрным). null — не вышло.</summary>
        public static byte[] Capture(long handle)
        {
            if (!WinApi.Supported || handle == 0) return null;
            var hwnd = new IntPtr(handle);
            // PrintWindow (WM_PRINT) ждёт поток окна без таймаута — сначала убедиться, что он отвечает
            if (!Responds(hwnd)) return null;
            var prevDpi = IntPtr.Zero;
            try
            {
                // настоящие пиксели окна, а не масштабированные под «DPI-неосведомлённый» процесс
                prevDpi = SetThreadDpiAwarenessContext(DpiPerMonitorV2);
            }
            catch (EntryPointNotFoundException)
            {
            }

            var screen = IntPtr.Zero;
            var mem = IntPtr.Zero;
            var bmp = IntPtr.Zero;
            try
            {
                if (!GetWindowRect(hwnd, out var r)) return null;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

                screen = GetDC(IntPtr.Zero);
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, w, h);
                var old = SelectObject(mem, bmp);
                var printed = PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT);
                SelectObject(mem, old); // GetDIBits требует, чтобы битмап не был выбран в DC
                if (!printed) return null;

                var info = new BitmapInfoHeader { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
                var bits = new byte[w * h * 4];
                if (GetDIBits(screen, bmp, 0, (uint)h, bits, ref info, 0) == 0) return null;
                return Png.EncodeBgra(w, h, bits);
            }
            catch (Exception e)
            {
                Log.Warn("снимок окна: " + e.Message);
                return null;
            }
            finally
            {
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                if (prevDpi != IntPtr.Zero)
                    try
                    {
                        SetThreadDpiAwarenessContext(prevDpi);
                    }
                    catch (EntryPointNotFoundException)
                    {
                    }
            }
        }

        /// <summary>Сохранить снимок диалога в NexusTemp/dialogs/&lt;id&gt;.png; вернуть путь относительно NexusTemp.</summary>
        public static string SaveShot(Project p, Dialog d)
        {
            if (d.Hung) return null;
            var png = Capture(d.Handle);
            if (png == null) return null;
            try
            {
                var dir = Path.Combine(p.TempDir, "dialogs");
                Directory.CreateDirectory(dir);
                Prune(dir);
                File.WriteAllBytes(Path.Combine(dir, d.Id + ".png"), png);
                return "dialogs/" + d.Id + ".png";
            }
            catch (Exception e)
            {
                Log.Warn("снимок диалога: " + e.Message);
                return null;
            }
        }

        /// <summary>Снимки старше суток и сверх последних 30 — удалить.</summary>
        private static void Prune(string dir)
        {
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                for (var i = 0; i < files.Count; i++)
                    if (i >= 30 || (DateTime.UtcNow - files[i].LastWriteTimeUtc).TotalHours > 24)
                        files[i].Delete();
            }
            catch (Exception e)
            {
                Log.Warn("чистка снимков: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ JSON

        public static JsonObject ToJson(Dialog d, bool raw)
        {
            var o = new JsonObject
            {
                ["id"] = d.Id,
                ["kind"] = d.Kind,
                ["title"] = d.Title,
                ["text"] = d.Text,
                ["ownedByEditor"] = d.OwnedByEditor,
                ["hung"] = d.Hung,
                ["buttons"] = new JsonArray(d.Buttons.Select(b => (JsonNode)new JsonObject
                {
                    ["label"] = b.Label,
                    ["default"] = b.Default,
                    ["enabled"] = b.Enabled,
                }).ToArray()),
            };
            if (d.Toggles.Count > 0) o["toggles"] = new JsonArray(d.Toggles.Select(t => (JsonNode)t).ToArray());
            if (d.Shot != null) o["shot"] = d.Shot;
            if (raw)
            {
                o["hwnd"] = d.Handle;
                o["owner"] = d.Owner;
                o["children"] = new JsonArray(d.Children.Select(c => (JsonNode)new JsonObject
                {
                    ["class"] = c.Class,
                    ["id"] = c.CtrlId,
                    ["style"] = "0x" + c.Style.ToString("X8"),
                    ["visible"] = c.Visible,
                    ["enabled"] = c.Enabled,
                    ["text"] = Cut(c.Text, 300),
                }).ToArray());
            }

            return o;
        }

        public static string Cut(string s, int n) => s != null && s.Length > n ? s.Substring(0, n) + "…" : s;
    }
}
