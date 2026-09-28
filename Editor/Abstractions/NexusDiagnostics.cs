using System;
using UnityEngine;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Единая точка логирования Nexus с переключателем подробностей.
    ///
    /// Смысл разделения: часть ошибок ШТАТНО тихая (битый package.json при скане,
    /// нечитаемый README, упавшая отписка) — в норме такой шум не нужен, но когда
    /// «непонятно, что происходит», нужно увидеть ВСЁ. Поэтому:
    ///   • <see cref="Error"/> — видно всегда (настоящая ошибка); стек добавляется в Verbose;
    ///   • <see cref="Swallowed"/> — проглоченная ветка, видна ТОЛЬКО в Verbose;
    ///   • <see cref="Trace"/>/<see cref="Warn"/> — пояснения хода работы, только в Verbose.
    ///
    /// Живёт в Abstractions, поэтому доступно всем слоям И страницам (у автора страницы
    /// тот же тумблер). Значение — булево в памяти; его ВЛАДЕЛЕЦ по персисту — Core
    /// (NexusDiagnosticsStore пишет личный выбор в UserSettings и применяет при загрузке).
    /// </summary>
    public static class NexusDiagnostics
    {
        private const string Tag = "[Nexus]";

        /// <summary>Подробный разбор: показывать проглоченные ошибки, стеки и ход работы.</summary>
        public static bool Verbose { get; set; }

        /// <summary>Настоящая ошибка — видна всегда. В Verbose добавляется полный стек.</summary>
        public static void Error(string context, Exception ex)
        {
            if (ex == null) { Error(context, (string)null); return; }
            Debug.LogError(Verbose
                ? $"{Tag} {context}: {ex.Message}\n{ex}"
                : $"{Tag} {context}: {ex.Message}");
        }

        /// <summary>Настоящая ошибка с готовым сообщением — видна всегда.</summary>
        public static void Error(string context, string message)
            => Debug.LogError(string.IsNullOrEmpty(message) ? $"{Tag} {context}" : $"{Tag} {context}: {message}");

        /// <summary>Штатно проглоченная ошибка (ветка «молча пропускаем»).
        /// Не видна в обычном режиме; в Verbose печатается целиком со стеком.</summary>
        public static void Swallowed(string context, Exception ex)
        {
            if (!Verbose) return;
            Debug.LogWarning(ex == null
                ? $"{Tag} (подавлено) {context}"
                : $"{Tag} (подавлено) {context}: {ex.Message}\n{ex}");
        }

        /// <summary>Пояснение хода работы — только в Verbose.</summary>
        public static void Trace(string context, string message)
        {
            if (!Verbose) return;
            Debug.Log($"{Tag} {context}: {message}");
        }

        /// <summary>Подозрительная, но не фатальная ситуация — только в Verbose.</summary>
        public static void Warn(string context, string message)
        {
            if (!Verbose) return;
            Debug.LogWarning($"{Tag} {context}: {message}");
        }
    }
}
