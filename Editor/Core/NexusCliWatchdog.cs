using System;
using System.Diagnostics;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Взгляд на редактор снаружи — компонент сервера CLI с явным владельцем (этот класс). Код в Unity не может
    /// рассказать, почему стоит главный поток, поэтому:
    /// <list type="bullet">
    /// <item>сторож — отдельный процесс (<c>Tools~/cli/nexus-watchdog.ps1</c>, один на сессию редактора): переживает
    /// перезагрузки домена; пульс канала стоит — пишет <c>NexusTemp/cli/stall.json</c> (фаза, отвечает ли окно,
    /// окна процесса, ошибки из Editor.log);</item>
    /// <item>метки домена — <c>NexusTemp/cli/domain.json</c> (reloading → loaded → ready) и
    /// <c>startup-error.txt</c>: видно, где оборвалась перезагрузка.</item>
    /// </list>
    /// Клиент <c>nexus-fs</c> читает эти файлы сам и печатает диагноз вместо «редактор молчит».
    /// </summary>
    /// <remarks>
    /// Только Windows. Сторож выходит сам: Unity закрыт, папка канала удалена (CLI выключен — «off means off») или
    /// запущен другой сторож. Ошибки здесь никогда не мешают серверу: всё глотается в диагностику.
    /// </remarks>
    internal static class NexusCliWatchdog
    {
        private static string Cli => NexusTempPaths.Cli;
        private static string DomainFile => Path.Combine(Cli, "domain.json");
        private static string StartupError => Path.Combine(Cli, "startup-error.txt");
        private static string SelfFile => Path.Combine(Cli, "watchdog.json");
        private static string Script => Path.Combine(NexusCliStore.PackageClientDir, "nexus-watchdog.ps1");

        // ============================================================ метки домена

        /// <summary>Записать состояние домена (только если канал уже есть: выключенный CLI файлов не пишет).</summary>
        internal static void MarkDomain(string state)
        {
            try
            {
                if (!Directory.Exists(Cli)) return;
                var o = new JObject
                {
                    ["state"] = state,
                    ["at"] = DateTime.UtcNow.ToString("o"),
                    ["pid"] = Process.GetCurrentProcess().Id,
                };
                File.WriteAllText(DomainFile, o.ToString(Formatting.None));
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: метка домена", e);
            }
        }

        /// <summary>Сервер не поднялся: текст исключения — в файл, клиент покажет его.</summary>
        internal static void MarkStartupError(Exception error)
        {
            try
            {
                if (!Directory.Exists(Cli)) return;
                File.WriteAllText(StartupError, $"{DateTime.UtcNow:o}\n{error}\n");
                MarkDomain("failed");
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: ошибка старта в файл", e);
            }
        }

        internal static void ClearStartupError()
        {
            try
            {
                if (File.Exists(StartupError)) File.Delete(StartupError);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: ошибка старта", e);
            }
        }

        // ============================================================ сторож

        /// <summary>Запустить сторожа, если его ещё нет для этого процесса Unity. Вызывается при старте сервера.</summary>
        internal static void Ensure()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) return;
            try
            {
                if (!Directory.Exists(Cli) || !File.Exists(Script)) return;
                var unityPid = Process.GetCurrentProcess().Id;
                if (IsAlive(unityPid)) return;

                var log = Application.consoleLogPath ?? string.Empty;
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Script}\" " +
                                $"-UnityPid {unityPid} -Cli \"{Cli}\" -Log \"{log}\" -Config \"{NexusCliStore.ConfigPath}\"",
                    // через оболочку: дочерний процесс не наследует дескрипторы Unity (сокет сервера, открытые файлы)
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(psi)?.Dispose();
                NexusDiagnostics.Trace("CLI", "сторож запущен");
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: запуск сторожа", e);
            }
        }

        /// <summary>Сторож этого процесса Unity уже работает (по watchdog.json и живому процессу).</summary>
        private static bool IsAlive(int unityPid)
        {
            try
            {
                if (!File.Exists(SelfFile)) return false;
                var o = JObject.Parse(File.ReadAllText(SelfFile));
                if ((int?)o["unityPid"] != unityPid) return false;
                if ((int?)o["version"] != ScriptVersion()) return false; // пакет обновился — новый сторож сменит старого
                var pid = (int?)o["pid"];
                if (pid == null) return false;
                using (var p = Process.GetProcessById(pid.Value))
                    return !p.HasExited && p.ProcessName.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false; // процесса нет или файл битый — запустить заново
            }
        }

        /// <summary>Версия скрипта сторожа из строки «# nexus-watchdog-version: N».</summary>
        private static int ScriptVersion()
        {
            try
            {
                foreach (var line in File.ReadLines(Script))
                {
                    var i = line.IndexOf("nexus-watchdog-version:", StringComparison.Ordinal);
                    if (i >= 0 && int.TryParse(line.Substring(i + 23).Trim(), out var v)) return v;
                    if (!line.StartsWith("#", StringComparison.Ordinal) && !line.StartsWith("\uFEFF#", StringComparison.Ordinal)) break;
                }
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: версия сторожа", e);
            }

            return 0;
        }

        // ============================================================ фаза для пульса

        /// <summary>Чем занят редактор — уходит в пульс: застынет он — сторож и клиент покажут последнюю фазу.</summary>
        internal static string Phase()
        {
            try
            {
                var job = NexusCliJobs.Running();
                var what = job != null ? $"{job.Kind}: {job.Phase}" : null;
                var editor = EditorApplication.isCompiling ? "компиляция" :
                    EditorApplication.isUpdating ? "импорт" :
                    EditorApplication.isPlaying ? (EditorApplication.isPaused ? "Play (пауза)" : "Play") : "редактор";
                return what == null ? editor : editor + " · " + what;
            }
            catch
            {
                return null;
            }
        }
    }
}
