using System.IO;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Deployment;
using Exerussus.Nexus.Manifests;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>Личный выбор «подробное логирование» — одним файлом на уровне Nexus
    /// (UserSettings, git-ignored, вне основной папки, переживает restore).</summary>
    public sealed class NexusDiagnosticsConfig
    {
        public bool Verbose { get; set; }
    }

    /// <summary>
    /// Владелец персиста для <see cref="NexusDiagnostics"/>. Применяет сохранённое
    /// значение при КАЖДОЙ загрузке домена — иначе после рекомпила подробный режим
    /// молча сбрасывался бы, а именно в этот момент он и нужен (деплой, установка
    /// пакетов и отложенные фазы происходят как раз через перезагрузки).
    /// </summary>
    [InitializeOnLoad]
    public static class NexusDiagnosticsStore
    {
        static NexusDiagnosticsStore()
        {
            NexusDiagnostics.Verbose = JsonIo.Load<NexusDiagnosticsConfig>(ConfigPath)?.Verbose ?? false;
        }

        private static string ConfigPath => Path.Combine(NexusPaths.UserRoot, "nexus-diagnostics.json");

        /// <summary>Подробный режим: чтение из памяти, запись — в память и в файл.</summary>
        public static bool Verbose
        {
            get => NexusDiagnostics.Verbose;
            set
            {
                NexusDiagnostics.Verbose = value;
                JsonIo.Save(ConfigPath, new NexusDiagnosticsConfig { Verbose = value });
                // подтверждение видно всегда — чтобы было ясно, в каком режиме идут дальнейшие логи
                UnityEngine.Debug.Log(value
                    ? "[Nexus] Подробное логирование ВКЛЮЧЕНО: показываются проглоченные ошибки, стеки и ход работы."
                    : "[Nexus] Подробное логирование выключено.");
            }
        }
    }
}
