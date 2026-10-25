using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Окна процесса Unity и фокус — Windows API (без мыши и клавиатуры). На других ОС — пустые ответы:
    /// диагноз без окон, будильник выключен.
    /// </summary>
    internal static class WinApi
    {
        public static bool Supported => OperatingSystem.IsWindows();

        private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const uint WM_NULL = 0x0000;
        private const uint WM_ACTIVATEAPP = 0x001C;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_SETFOCUS = 0x0007;
        private const uint WM_KILLFOCUS = 0x0008;
        private const uint WM_NCACTIVATE = 0x0086;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int SW_RESTORE = 9;
        private const int SW_MINIMIZE = 6;

        public sealed class Window
        {
            public long Handle;
            public string Class;
            public bool Enabled;
            public string Title;
        }

        public static List<Window> Windows(int pid)
        {
            var list = new List<Window>();
            if (!Supported || pid <= 0) return list;
            try
            {
                EnumProc callback = (hwnd, param) =>
                {
                    GetWindowThreadProcessId(hwnd, out var owner);
                    if (owner != (uint)pid || !IsWindowVisible(hwnd)) return true;
                    var title = new StringBuilder(512);
                    GetWindowText(hwnd, title, title.Capacity);
                    var cls = new StringBuilder(256);
                    GetClassName(hwnd, cls, cls.Capacity);
                    list.Add(new Window { Handle = hwnd.ToInt64(), Class = cls.ToString(), Enabled = IsWindowEnabled(hwnd), Title = title.ToString() });
                    return true;
                };
                EnumWindows(callback, IntPtr.Zero);
                GC.KeepAlive(callback);
            }
            catch (Exception e)
            {
                Log.Warn("окна процесса: " + e.Message);
            }

            return list;
        }

        /// <summary>Диалог Windows (#32770): модальное окно — будить нельзя, решает человек.</summary>
        public static bool HasDialog(List<Window> windows) => windows.Exists(w => w.Class == "#32770");

        public static long MainWindow(List<Window> windows)
        {
            foreach (var w in windows)
                if (w.Class == "UnityContainerWndClass" && w.Enabled)
                    return w.Handle;
            return 0;
        }

        public static bool Foreground(int pid)
        {
            if (!Supported) return false;
            GetWindowThreadProcessId(GetForegroundWindow(), out var owner);
            return owner == (uint)pid;
        }

        public static long ForegroundHandle() => Supported ? GetForegroundWindow().ToInt64() : 0;

        /// <summary>Отвечает ли главное окно на сообщения (как Process.Responding, с таймаутом 2 с).</summary>
        public static bool? Responding(List<Window> windows)
        {
            if (!Supported) return null;
            var main = MainWindow(windows);
            if (main == 0) return null;
            var ok = SendMessageTimeout(new IntPtr(main), WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out _);
            return ok != IntPtr.Zero;
        }

        public static bool Iconic(long hwnd) => Supported && hwnd != 0 && IsIconic(new IntPtr(hwnd));

        /// <summary>Мягко: «приложение активно / неактивно» всем окнам процесса, фокус не меняется.</summary>
        public static void ActivateApp(List<Window> windows, bool active)
        {
            if (!Supported) return;
            foreach (var w in windows)
                PostMessage(new IntPtr(w.Handle), WM_ACTIVATEAPP, new IntPtr(active ? 1 : 0), IntPtr.Zero);

            // и главному окну — «окно активно, фокус ввода у тебя» (без SetForegroundWindow: передний план не меняется)
            var main = MainWindow(windows);
            if (main == 0) return;
            var hwnd = new IntPtr(main);
            PostMessage(hwnd, WM_NCACTIVATE, new IntPtr(active ? 1 : 0), IntPtr.Zero);
            PostMessage(hwnd, WM_ACTIVATE, new IntPtr(active ? 1 : 0), IntPtr.Zero);
            PostMessage(hwnd, active ? WM_SETFOCUS : WM_KILLFOCUS, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Передний план без мыши и клавиатуры: на время привязать ввод к потоку текущего переднего окна.</summary>
        public static bool Focus(long handle, bool restore)
        {
            if (!Supported || handle == 0) return false;
            var hwnd = new IntPtr(handle);
            if (!IsWindow(hwnd)) return false;
            if (restore && IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var self = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
            try
            {
                BringWindowToTop(hwnd);
                return SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(self, fgThread, false);
            }
        }

        public static void Minimize(long handle)
        {
            if (Supported && handle != 0) ShowWindow(new IntPtr(handle), SW_MINIMIZE);
        }
    }
}
