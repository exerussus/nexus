using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Окно-пробудитель для будильника демона (режим <c>quiet</c>): пустое окно 1×1 за пределами экрана, без панели
    /// задач и Alt+Tab, в главном потоке редактора. Демон активирует его, когда Unity застряла в фоне после
    /// перезагрузки домена: процесс становится активным приложением, а главное окно не разворачивается и не
    /// поднимается. Случайные нажатия в это время уходят в пустое окно, а не в сцену.
    /// <list type="bullet">
    /// <item>Обработчик окна — системный <c>DefWindowProcW</c>, ни строчки управляемого кода: окно переживает
    /// перезагрузку домена (её оно и помогает закончить) и ничего не делает само.</item>
    /// <item>Живёт, только пока CLI включён и будильник в режиме <c>quiet</c>; иначе уничтожается.</item>
    /// <item>Одно на процесс: ищется по классу и заголовку <c>NexusWake:&lt;pid&gt;</c>.</item>
    /// </list>
    /// Только Windows; на других ОС класс ничего не делает.
    /// </summary>
    [InitializeOnLoad]
    public static class NexusWakeWindow
    {
        public const string ClassName = "NexusWakeWnd";

        private static readonly int MainThread;

        static NexusWakeWindow()
        {
            MainThread = Thread.CurrentThread.ManagedThreadId;
            Sync();
        }

        /// <summary>Привести окно в соответствие с настройками. Только из главного потока (окно должно жить в нём).</summary>
        public static void Sync()
        {
#if UNITY_EDITOR_WIN
            if (Thread.CurrentThread.ManagedThreadId != MainThread) return;
            try
            {
                var want = NexusCliStore.Enabled && NexusCliStore.Wake == "quiet";
                var hwnd = FindWindowW(ClassName, Title);
                if (want && hwnd == IntPtr.Zero) Create();
                else if (!want && hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Nexus: окно-пробудитель — " + e.Message);
            }
#endif
        }

        /// <summary>Есть ли окно сейчас (для диагностики).</summary>
        public static bool Exists
        {
            get
            {
#if UNITY_EDITOR_WIN
                return FindWindowW(ClassName, Title) != IntPtr.Zero;
#else
                return false;
#endif
            }
        }

#if UNITY_EDITOR_WIN
        private static string Title => "NexusWake:" + GetCurrentProcessId();

        private static void Create()
        {
            var instance = GetModuleHandleW(null);
            // обработчик — системный: управляемый делегат умер бы при перезагрузке домена вместе с окном
            var proc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");
            if (proc == IntPtr.Zero) throw new InvalidOperationException("нет DefWindowProcW");

            var cls = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = proc,
                Instance = instance,
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref cls) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorClassAlreadyExists) throw new InvalidOperationException("RegisterClassEx: " + error);
            }

            // без WS_VISIBLE при создании: показываем без активации, чтобы не отнять фокус у человека
            var hwnd = CreateWindowExW(WsExToolWindow, ClassName, Title, WsPopup, -32000, -32000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx: " + Marshal.GetLastWin32Error());
            ShowWindow(hwnd, SwShowNoActivate);
        }

        private const uint WsPopup = 0x80000000;
        private const uint WsExToolWindow = 0x00000080;
        private const int SwShowNoActivate = 4;
        private const int ErrorClassAlreadyExists = 1410;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public IntPtr WndProc;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string MenuName;
            public string ClassName;
            public IntPtr IconSmall;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WndClassEx cls);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y,
            int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string className, string title);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
#endif
    }
}
