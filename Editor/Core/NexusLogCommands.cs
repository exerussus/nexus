using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Exerussus.Nexus.Abstractions;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Лог редактора для CLI: по умолчанию коротко (первая строка, без стека, повторы схлопнуты, последние 30),
    /// чтобы читающий не тратил токены на весь лог. Подробности — флагами.
    /// </summary>
    internal static class NexusLogCommands
    {
        [NexusCommand("logs", "Лог редактора с фильтрами: --errors, --grep слова, --since N; коротко по умолчанию",
            Usage = "logs [--errors] [--warnings] [--type error,exception,assert,warning,log] [--grep a,b] [--all a,b] [--not a,b] " +
                    "[--regex <re>] [--in-stack] [--since <N>|--play] [--last 30] [--stack [строк]] [--full] [--chars 300] [--no-collapse] [--count]", ReadOnly = true)]
        private static int Logs(NexusCommandContext ctx)
        {
            if (!NexusLogBuffer.Active) throw new NexusCommandException("Буфер лога не работает (CLI выключен?).", NexusCommandContext.Exit.Failed);

            var q = new NexusLogQuery
            {
                Types = ParseTypes(ctx),
                AnyWords = ctx.GetList("grep"),
                AllWords = ctx.GetList("all"),
                NotWords = ctx.GetList("not"),
                InStack = ctx.Flag("in-stack"),
            };

            var regex = ctx.Get("regex");
            if (!string.IsNullOrEmpty(regex))
            {
                try
                {
                    q.Pattern = new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                }
                catch (ArgumentException e)
                {
                    throw new NexusCommandException("Неверное --regex: " + e.Message);
                }
            }

            if (ctx.Has("since"))
            {
                var raw = ctx.Get("since");
                if (raw == null || !long.TryParse(raw, out var since) || since < 0) throw new NexusCommandException("--since <номер записи> (номер — из прошлого ответа)");
                q.AfterSeq = since;
            }
            else if (ctx.Flag("play"))
            {
                q.AfterSeq = NexusLogBuffer.PlaySeq;
            }

            if (ctx.Flag("count"))
            {
                NexusLogBuffer.Count(q.AfterSeq, out var e, out var w, out var l);
                ctx.Print($"ошибок: {e}, предупреждений: {w}, сообщений: {l} · последний #{NexusLogBuffer.LastSeq}");
                ctx.Data = new { errors = e, warnings = w, logs = l, last = NexusLogBuffer.LastSeq };
                return NexusCommandContext.Exit.Ok;
            }

            var last = Math.Max(1, ctx.GetInt("last", 30));
            var full = ctx.Flag("full");
            var chars = full ? 0 : Math.Max(40, ctx.GetInt("chars", 300));
            var stackLines = ctx.Has("stack") ? Math.Max(1, ctx.GetInt("stack", 6)) : 0;
            var collapse = !ctx.Flag("no-collapse");

            // схлопывание идёт по всем подошедшим, а «последние N» — по уже схлопнутым строкам
            var matched = NexusLogBuffer.Query(q, 0, out var total);
            var rows = new List<Row>();
            var index = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (var e in matched)
            {
                var key = collapse ? (int)e.Type + "|" + e.Message : null;
                if (key != null && index.TryGetValue(key, out var row))
                {
                    row.Count++;
                    row.Last = e;
                    continue;
                }

                row = new Row { First = e, Last = e, Count = 1 };
                rows.Add(row);
                if (key != null) index[key] = row;
            }

            if (collapse) rows.Sort((a, b) => a.Last.Seq.CompareTo(b.Last.Seq));
            var skipped = Math.Max(0, rows.Count - last);
            var data = new List<object>();
            for (var i = skipped; i < rows.Count; i++)
            {
                var r = rows[i];
                var e = r.Last;
                var message = full ? NexusCliText.Cut(e.Message, 0) : NexusCliText.OneLine(e.Message, chars);
                ctx.Print($"#{e.Seq} {NexusCliText.Clock(e.Time)} {NexusCliText.Letter(e.Type)} {message}{(r.Count > 1 ? $" (×{r.Count})" : string.Empty)}");
                if (stackLines > 0)
                {
                    var head = NexusCliText.StackHead(e.Stack, stackLines);
                    if (head.Length > 0) ctx.Print(head);
                }

                data.Add(new
                {
                    seq = e.Seq, type = NexusCliText.TypeName(e.Type), time = new DateTime(e.Time, DateTimeKind.Utc).ToString("o"),
                    message = full ? e.Message : message, count = r.Count, stack = stackLines > 0 ? NexusCliText.StackHead(e.Stack, stackLines, string.Empty) : null,
                });
            }

            var lastSeq = NexusLogBuffer.LastSeq;
            if (rows.Count == 0) ctx.Print($"Записей нет · последний #{lastSeq}");
            else ctx.Print($"— {rows.Count - skipped} из {rows.Count}{(collapse && rows.Count != total ? $" (записей {total})" : string.Empty)} · последний #{lastSeq} · новые: logs --since {lastSeq}");

            ctx.Data = new { last = lastSeq, total, rows = rows.Count, shown = data.Count, entries = data };
            return NexusCommandContext.Exit.Ok;
        }

        [NexusCommand("logs.clear", "Очистить буфер лога CLI (--console — и консоль Unity)", Usage = "logs.clear [--console]")]
        private static int Clear(NexusCommandContext ctx)
        {
            NexusLogBuffer.Clear();
            ctx.Print($"Буфер лога очищен (номера продолжатся с #{NexusLogBuffer.LastSeq + 1}).");
            if (ctx.Flag("console"))
            {
                if (ClearConsole()) ctx.Print("Консоль Unity очищена.");
                else ctx.Warn("Консоль Unity очистить не удалось (внутренний API изменился).");
            }

            return NexusCommandContext.Exit.Ok;
        }

        private sealed class Row
        {
            public NexusLogEntry First;
            public NexusLogEntry Last;
            public int Count;
        }

        private static HashSet<LogType> ParseTypes(NexusCommandContext ctx)
        {
            var set = new HashSet<LogType>();
            if (ctx.Flag("errors"))
            {
                set.Add(LogType.Error);
                set.Add(LogType.Exception);
                set.Add(LogType.Assert);
            }

            if (ctx.Flag("warnings")) set.Add(LogType.Warning);

            foreach (var t in ctx.GetList("type"))
            {
                switch (t.ToLowerInvariant())
                {
                    case "error": case "errors": set.Add(LogType.Error); break;
                    case "exception": case "exceptions": set.Add(LogType.Exception); break;
                    case "assert": case "asserts": set.Add(LogType.Assert); break;
                    case "warning": case "warnings": set.Add(LogType.Warning); break;
                    case "log": case "info": set.Add(LogType.Log); break;
                    default: throw new NexusCommandException($"--type: неизвестный тип «{t}» (error, exception, assert, warning, log)");
                }
            }

            return set.Count == 0 ? null : set;
        }

        /// <summary>Очистить консоль Unity: внутренний UnityEditor.LogEntries.Clear (может исчезнуть — тогда false).</summary>
        private static bool ClearConsole()
        {
            try
            {
                var type = Type.GetType("UnityEditor.LogEntries, UnityEditor") ?? Type.GetType("UnityEditorInternal.LogEntries, UnityEditor");
                var method = type?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) return false;
                method.Invoke(null, null);
                return true;
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("CLI: очистка консоли", e);
                return false;
            }
        }
    }
}
