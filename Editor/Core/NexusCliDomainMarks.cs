using System;
using System.Diagnostics;
using System.IO;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Метки домена — компонент сервера CLI (владелец — <see cref="NexusCliServer"/>): <c>NexusTemp/cli/domain.json</c>
    /// (reloading → loaded → ready, failed) и <c>startup-error.txt</c>. По ним демон и <c>nexus-fs diag</c> видят, где
    /// оборвалась перезагрузка домена. Пишутся только в существующую папку канала (её ведёт демон): выключенный CLI
    /// файлов не пишет.
    /// </summary>
    internal static class NexusCliDomainMarks
    {
        private static string Cli => NexusTempPaths.Cli;
        private static string DomainFile => Path.Combine(Cli, "domain.json");
        private static string StartupError => Path.Combine(Cli, "startup-error.txt");

        internal static void MarkDomain(string state)
        {
            try
            {
                if (!Directory.Exists(Cli)) return;
                using (var p = Process.GetCurrentProcess())
                {
                    var o = new JObject { ["state"] = state, ["at"] = DateTime.UtcNow.ToString("o"), ["pid"] = p.Id };
                    File.WriteAllText(DomainFile, o.ToString(Formatting.None));
                }
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: метка домена", e);
            }
        }

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

        /// <summary>Чем занят редактор — отдаётся демону в ответе на ping (фаза пульса и диагноза).</summary>
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
