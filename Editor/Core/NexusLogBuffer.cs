using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Exerussus.Nexus.Abstractions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>Запись лога редактора.</summary>
    public sealed class NexusLogEntry
    {
        public long Seq;
        public LogType Type;

        /// <summary>UTC, ticks.</summary>
        public long Time;

        public string Message;
        public string Stack;
    }

    /// <summary>Фильтр выборки лога.</summary>
    public sealed class NexusLogQuery
    {
        /// <summary>Пусто — все типы.</summary>
        public HashSet<LogType> Types;

        /// <summary>Только записи с номером больше этого.</summary>
        public long AfterSeq;

        /// <summary>Любое из слов (без учёта регистра) в сообщении или стеке.</summary>
        public List<string> AnyWords;

        /// <summary>Все слова сразу.</summary>
        public List<string> AllWords;

        /// <summary>Исключить записи с любым из слов.</summary>
        public List<string> NotWords;

        public Regex Pattern;

        /// <summary>Искать слова и шаблон ещё и в стеке.</summary>
        public bool InStack;
    }

    /// <summary>
    /// Кольцевой буфер лога редактора для CLI — компонент Core с явным владельцем (этот класс).
    /// Пишет с любого потока (<see cref="Application.logMessageReceivedThreaded"/>), читается в главном.
    /// </summary>
    /// <remarks>
    /// <para>Работает, только пока запущен сервер CLI: выключен — ни подписки, ни буфера в памяти, ни копии в
    /// SessionState. Перед перезагрузкой домена последние записи кладутся в SessionState и поднимаются в новом
    /// домене: лог входа в Play и компиляции не теряется.</para>
    /// <para>Память ограничена: <see cref="Capacity"/> записей, сообщение и стек обрезаются.</para>
    /// </remarks>
    public static class NexusLogBuffer
    {
        public const int Capacity = 2000;
        private const int MaxMessageChars = 8 * 1024;
        private const int MaxStackChars = 8 * 1024;
        private const int PersistCount = 1000;
        private const int PersistMessageChars = 2048;
        private const int PersistStackChars = 2048;

        private const string StoreKey = "Exerussus.Nexus.Cli.Log";
        private const string SeqKey = "Exerussus.Nexus.Cli.LogSeq";
        private const string PlaySeqKey = "Exerussus.Nexus.Cli.LogPlaySeq";
        private const string ClearSeqKey = "Exerussus.Nexus.Cli.LogClearSeq";

        private static readonly object Gate = new object();
        private static NexusLogEntry[] _ring;
        private static int _head; // следующая позиция записи
        private static int _count;
        private static long _seq;
        private static bool _active;

        /// <summary>Номер последней записи (0 — записей не было).</summary>
        public static long LastSeq
        {
            get
            {
                lock (Gate) return _seq;
            }
        }

        /// <summary>Номер последней записи перед последним входом в Play.</summary>
        public static long PlaySeq => ReadLong(PlaySeqKey);

        /// <summary>Номер последней записи на момент очистки (logs.clear).</summary>
        public static long ClearSeq => ReadLong(ClearSeqKey);

        public static bool Active => _active;

        // ---------------------------------------------------------------- жизненный цикл (владелец — сервер CLI)

        internal static void Activate()
        {
            if (_active) return;
            lock (Gate)
            {
                _ring = new NexusLogEntry[Capacity];
                _head = 0;
                _count = 0;
                _seq = ReadLong(SeqKey);
                Restore();
            }

            Application.logMessageReceivedThreaded += OnLog;
            EditorApplication.playModeStateChanged += OnPlayMode;
            _active = true;
        }

        /// <summary>Остановить. Перезагрузка — сохранить хвост в SessionState; выключение — стереть всё.</summary>
        internal static void Deactivate(bool reloading)
        {
            if (!_active)
            {
                if (!reloading) EraseStore();
                return;
            }

            _active = false;
            Application.logMessageReceivedThreaded -= OnLog;
            EditorApplication.playModeStateChanged -= OnPlayMode;

            lock (Gate)
            {
                if (reloading)
                {
                    Persist();
                }
                else
                {
                    EraseStore();
                }

                _ring = null;
                _count = 0;
                _head = 0;
            }
        }

        private static void EraseStore()
        {
            SessionState.EraseString(StoreKey);
            SessionState.EraseString(SeqKey);
            SessionState.EraseString(PlaySeqKey);
            SessionState.EraseString(ClearSeqKey);
        }

        private static void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) WriteLong(PlaySeqKey, LastSeq);
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            lock (Gate)
            {
                if (_ring == null) return;
                Add(new NexusLogEntry
                {
                    Seq = ++_seq,
                    Type = type,
                    Time = DateTime.UtcNow.Ticks,
                    Message = Cut(message, MaxMessageChars),
                    Stack = Cut(stack, MaxStackChars),
                });
            }
        }

        private static void Add(NexusLogEntry e)
        {
            _ring[_head] = e;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }

        /// <summary>Забыть записи (номера продолжаются). Видимая граница — <see cref="ClearSeq"/>.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                if (_ring != null) Array.Clear(_ring, 0, _ring.Length);
                _head = 0;
                _count = 0;
                WriteLong(ClearSeqKey, _seq);
            }
        }

        // ---------------------------------------------------------------- выборка

        /// <summary>Записи по фильтру, по возрастанию номера. <paramref name="total"/> — сколько подошло всего.</summary>
        public static List<NexusLogEntry> Query(NexusLogQuery q, int last, out int total)
        {
            var matched = new List<NexusLogEntry>();
            lock (Gate)
            {
                if (_ring != null)
                {
                    var start = (_head - _count + _ring.Length) % _ring.Length;
                    for (var i = 0; i < _count; i++)
                    {
                        var e = _ring[(start + i) % _ring.Length];
                        if (e != null && Matches(e, q)) matched.Add(e);
                    }
                }
            }

            total = matched.Count;
            if (last > 0 && matched.Count > last) matched.RemoveRange(0, matched.Count - last);
            return matched;
        }

        /// <summary>Счётчики по типам после номера.</summary>
        public static void Count(long afterSeq, out int errors, out int warnings, out int logs)
        {
            errors = warnings = logs = 0;
            lock (Gate)
            {
                if (_ring == null) return;
                foreach (var e in _ring)
                {
                    if (e == null || e.Seq <= afterSeq) continue;
                    if (IsError(e.Type)) errors++;
                    else if (e.Type == LogType.Warning) warnings++;
                    else logs++;
                }
            }
        }

        public static bool IsError(LogType t) => t == LogType.Error || t == LogType.Exception || t == LogType.Assert;

        private static bool Matches(NexusLogEntry e, NexusLogQuery q)
        {
            if (q == null) return true;
            if (e.Seq <= q.AfterSeq) return false;
            if (q.Types != null && q.Types.Count > 0 && !q.Types.Contains(e.Type)) return false;

            if (q.AnyWords != null && q.AnyWords.Count > 0)
            {
                var hit = false;
                foreach (var w in q.AnyWords)
                    if (Contains(e, w, q.InStack))
                    {
                        hit = true;
                        break;
                    }

                if (!hit) return false;
            }

            if (q.AllWords != null)
                foreach (var w in q.AllWords)
                    if (!Contains(e, w, q.InStack))
                        return false;

            if (q.NotWords != null)
                foreach (var w in q.NotWords)
                    if (Contains(e, w, q.InStack))
                        return false;

            if (q.Pattern != null)
            {
                try
                {
                    if (!q.Pattern.IsMatch(e.Message ?? string.Empty) && !(q.InStack && q.Pattern.IsMatch(e.Stack ?? string.Empty))) return false;
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Contains(NexusLogEntry e, string word, bool inStack)
        {
            if (string.IsNullOrEmpty(word)) return true;
            if (e.Message != null && e.Message.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return inStack && e.Stack != null && e.Stack.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------- SessionState

        private static void Persist()
        {
            try
            {
                var list = new List<NexusLogEntry>(Math.Min(_count, PersistCount));
                var take = Math.Min(_count, PersistCount);
                var start = (_head - take + _ring.Length) % _ring.Length;
                for (var i = 0; i < take; i++)
                {
                    var e = _ring[(start + i) % _ring.Length];
                    if (e == null) continue;
                    list.Add(new NexusLogEntry
                    {
                        Seq = e.Seq, Type = e.Type, Time = e.Time,
                        Message = Cut(e.Message, PersistMessageChars), Stack = Cut(e.Stack, PersistStackChars),
                    });
                }

                SessionState.SetString(StoreKey, JsonConvert.SerializeObject(list, Formatting.None));
                WriteLong(SeqKey, _seq);
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: сохранение лога", e);
            }
        }

        private static void Restore()
        {
            try
            {
                var raw = SessionState.GetString(StoreKey, string.Empty);
                SessionState.EraseString(StoreKey);
                if (string.IsNullOrEmpty(raw)) return;
                var list = JsonConvert.DeserializeObject<List<NexusLogEntry>>(raw);
                if (list == null) return;
                foreach (var e in list)
                {
                    if (e == null) continue;
                    Add(e);
                    if (e.Seq > _seq) _seq = e.Seq;
                }
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: чтение лога", e);
            }
        }

        private static long ReadLong(string key)
        {
            var raw = SessionState.GetString(key, string.Empty);
            return long.TryParse(raw, out var v) ? v : 0;
        }

        private static void WriteLong(string key, long value) => SessionState.SetString(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private static string Cut(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
