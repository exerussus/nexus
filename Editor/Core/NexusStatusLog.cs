using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json;
using UnityEditor;

namespace Exerussus.Nexus.Core
{
    /// <summary>Одно сообщение статуса.</summary>
    public sealed class NexusStatusEntry
    {
        public long       Ticks  { get; set; }   // локальное время, DateTime.Ticks
        public string     Source { get; set; }   // имя страницы (или «Nexus»)
        public StatusKind Kind   { get; set; }
        public string     Text   { get; set; }
        public int        Repeat { get; set; } = 1;   // подряд пришло одно и то же

        [JsonIgnore] public DateTime Time => new DateTime(Ticks, DateTimeKind.Local);

        /// <summary>Первая непустая строка — для строки статуса и списка.</summary>
        [JsonIgnore]
        public string Summary
        {
            get
            {
                var t = (Text ?? "").Replace("\r", "");
                foreach (var line in t.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
                return "";
            }
        }

        /// <summary>Есть ли что-то кроме первой строки.</summary>
        [JsonIgnore] public bool HasDetails => (Text ?? "").Trim().Replace("\r", "").Contains("\n");

        /// <summary>Текст для буфера обмена.</summary>
        public string Format()
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ")
              .Append(Kind).Append(" · ").Append(Source);
            if (Repeat > 1) sb.Append(" ×").Append(Repeat);
            sb.Append('\n').Append((Text ?? "").Replace("\r", ""));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Журнал статусов рабочего окна. Состояние ЯДРА хаба (явный владелец): страницы
    /// пишут в него через <see cref="IPageContext.SetStatus"/>, окно показывает
    /// последнее сообщение в тулбаре, окно сообщений — весь журнал.
    ///
    /// Живёт в SessionState: переживает рекомпил и пересоздание окон, но не перезапуск
    /// редактора (старые сообщения после перезапуска не нужны). Ограничен по размеру;
    /// одинаковые сообщения подряд схлопываются в одно со счётчиком.
    /// </summary>
    public static class NexusStatusLog
    {
        public const int Capacity = 50;

        private const string EntriesKey = "Exerussus.Nexus.statusLog";
        private const string UnseenKey  = "Exerussus.Nexus.statusLog.unseenError";
        private const int    MaxTextLength = 20000;   // защита SessionState от гигантских логов

        private static List<NexusStatusEntry> _entries;

        /// <summary>Журнал изменился (добавили, очистили, прочитали ошибку).</summary>
        public static event Action Changed;

        /// <summary>Сообщения, от старых к новым.</summary>
        public static IReadOnlyList<NexusStatusEntry> Entries => Load();

        /// <summary>Последнее сообщение или null.</summary>
        public static NexusStatusEntry Last
        {
            get
            {
                var list = Load();
                return list.Count > 0 ? list[list.Count - 1] : null;
            }
        }

        /// <summary>Пришла ошибка, а окно сообщений с тех пор не открывали.</summary>
        public static bool HasUnseenError => SessionState.GetBool(UnseenKey, false);

        public static void Add(string source, string text, StatusKind kind)
        {
            text ??= "";
            if (text.Length > MaxTextLength) text = text.Substring(0, MaxTextLength) + "\n… (обрезано)";

            var list = Load();
            var last = list.Count > 0 ? list[list.Count - 1] : null;
            if (last != null && last.Kind == kind && last.Source == source && last.Text == text)
            {
                last.Repeat++;
                last.Ticks = DateTime.Now.Ticks;
            }
            else
            {
                list.Add(new NexusStatusEntry { Ticks = DateTime.Now.Ticks, Source = source ?? "Nexus", Kind = kind, Text = text });
                if (list.Count > Capacity) list.RemoveRange(0, list.Count - Capacity);
            }

            if (kind == StatusKind.Error) SessionState.SetBool(UnseenKey, true);
            Save();
        }

        /// <summary>Окно сообщений открыли — ошибки прочитаны.</summary>
        public static void MarkSeen()
        {
            if (!HasUnseenError) return;
            SessionState.SetBool(UnseenKey, false);
            Raise();
        }

        public static void Clear()
        {
            Load().Clear();
            SessionState.SetBool(UnseenKey, false);
            Save();
        }

        /// <summary>Весь журнал для буфера обмена (от старых к новым).</summary>
        public static string FormatAll()
        {
            var sb = new StringBuilder();
            foreach (var e in Load())
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(e.Format());
            }
            return sb.ToString();
        }

        private static List<NexusStatusEntry> Load()
        {
            if (_entries != null) return _entries;
            try
            {
                var json = SessionState.GetString(EntriesKey, "");
                _entries = string.IsNullOrEmpty(json)
                    ? new List<NexusStatusEntry>()
                    : JsonConvert.DeserializeObject<List<NexusStatusEntry>>(json) ?? new List<NexusStatusEntry>();
            }
            catch (Exception ex)
            {
                NexusDiagnostics.Swallowed("Журнал статусов не прочитан — начинаю пустой", ex);
                _entries = new List<NexusStatusEntry>();
            }
            return _entries;
        }

        private static void Save()
        {
            try
            {
                SessionState.SetString(EntriesKey, JsonConvert.SerializeObject(_entries ?? new List<NexusStatusEntry>()));
            }
            catch (Exception ex)
            {
                NexusDiagnostics.Swallowed("Журнал статусов не сохранён", ex);
            }
            Raise();
        }

        private static void Raise()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { NexusDiagnostics.Error("Подписчик журнала статусов упал", ex); }
        }
    }
}
