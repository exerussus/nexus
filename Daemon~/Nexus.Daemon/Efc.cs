using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Файловый канал efc/1 (стандарт — репозиторий efc, SPEC.md): константы и шаги пакета — имена шагов,
    /// подстановка <c>${шаг.путь}</c>, проверка <c>expect</c>. Логика повторяет efc-core (steps.rs).
    /// </summary>
    internal static class Efc
    {
        public const string Protocol = "efc/1";

        // стандартные машинные причины (SPEC 13, 5.2.1)
        public const string Expired = "expired";
        public const string Cancelled = "cancelled";
        public const string ServerRestarted = "server-restarted";
        public const string Busy = "busy";
        public const string BadRequest = "bad-request";
        public const string UnknownCommand = "unknown-command";
        public const string TooLarge = "too-large";
        public const string Timeout = "timeout";
        public const string BadReference = "bad-reference";
        public const string ExpectFailed = "expect-failed";
        public const string StepTimeout = "step-timeout";

        private static readonly Regex StepIdRe = new Regex("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

        /// <summary>Машинная причина по коду выхода команды (если команда своей не дала).</summary>
        public static string ReasonFor(int exit) => exit switch
        {
            0 => null,
            2 => BadRequest,
            3 => UnknownCommand,
            4 => "crashed",
            5 => Timeout,
            6 => Busy,
            7 => Cancelled,
            8 => "no-approval",
            10 => "no-editor",
            12 => "editor-silent",
            _ => "failed",
        };

        /// <summary>Первая строка с ✗ из вывода — текст ошибки для человека.</summary>
        public static string ErrorText(string output, int exit)
        {
            if (exit == 0) return null;
            foreach (var line in (output ?? string.Empty).Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("✗")) return t.TrimStart('✗', ' ');
            }

            return $"код {exit}";
        }

        // ============================================================ шаги пакета

        /// <summary>Результат выполненного шага — источник подстановок.</summary>
        public sealed class StepResult
        {
            public int Exit;
            public string Output = string.Empty;
            public JsonNode Data;
        }

        /// <summary>Имена шагов: заданные или s1, s2, …; null и problem — недопустимое или повторённое имя.</summary>
        public static List<string> StepIds(JsonArray ops, out string problem)
        {
            problem = null;
            var ids = new List<string>();
            for (var i = 0; i < ops.Count; i++)
            {
                var id = Json.Str(ops[i], "id") ?? "s" + (i + 1);
                if (!StepIdRe.IsMatch(id))
                {
                    problem = $"недопустимое имя шага «{id}» (латиница, цифры, _ и -, до 32 символов)";
                    return null;
                }

                if (ids.Contains(id))
                {
                    problem = $"имя шага «{id}» повторяется";
                    return null;
                }

                ids.Add(id);
            }

            return ids;
        }

        private static JsonNode Lookup(string reference, Dictionary<string, StepResult> results)
        {
            var dot = reference.IndexOf('.');
            if (dot <= 0) throw new StepError($"${{{reference}}}: нужно «шаг.поле»");
            var step = reference.Substring(0, dot);
            var path = reference.Substring(dot + 1);
            if (!results.TryGetValue(step, out var r)) throw new StepError($"${{{reference}}}: шаг «{step}» ещё не выполнен или не существует");
            switch (path)
            {
                case "exitCode": return JsonValue.Create(r.Exit);
                case "output": return JsonValue.Create(r.Output);
                case "ok": return JsonValue.Create(r.Exit == 0);
            }

            var cur = r.Data;
            foreach (var part in path.Split('.'))
            {
                cur = cur switch
                {
                    JsonObject o => o[part],
                    JsonArray a when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                    _ => null,
                };
                if (cur == null) throw new StepError($"${{{reference}}}: у шага «{step}» нет поля «{path}»");
            }

            return cur.DeepClone();
        }

        private static string AsText(JsonNode v) =>
            v == null ? "null" : v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : Json.Write(v);

        /// <summary>Подставить ссылки в строку, результат — строка.</summary>
        public static string SubstText(string s, Dictionary<string, StepResult> results)
        {
            if (s == null || s.IndexOf("${", StringComparison.Ordinal) < 0) return s;
            var sb = new StringBuilder();
            var rest = s;
            int i;
            while ((i = rest.IndexOf("${", StringComparison.Ordinal)) >= 0)
            {
                sb.Append(rest, 0, i);
                var tail = rest.Substring(i + 2);
                var j = tail.IndexOf('}');
                if (j < 0) throw new StepError($"незакрытая ссылка в «{s}»");
                sb.Append(AsText(Lookup(tail.Substring(0, j), results)));
                rest = tail.Substring(j + 1);
            }

            sb.Append(rest);
            return sb.ToString();
        }

        /// <summary>Строка ровно из одной ссылки — значение как есть (число, объект), иначе — текст.</summary>
        public static JsonNode SubstValue(string s, Dictionary<string, StepResult> results)
        {
            if (s.StartsWith("${", StringComparison.Ordinal) && s.EndsWith("}") && s.IndexOf("${", 2, StringComparison.Ordinal) < 0 && s.IndexOf('}') == s.Length - 1)
                return Lookup(s.Substring(2, s.Length - 3), results);
            return JsonValue.Create(SubstText(s, results));
        }

        /// <summary>Подставить ссылки во всё дерево JSON.</summary>
        public static JsonNode SubstJson(JsonNode v, Dictionary<string, StepResult> results)
        {
            switch (v)
            {
                case null: return null;
                case JsonObject o:
                {
                    var r = new JsonObject();
                    foreach (var kv in o) r[kv.Key] = SubstJson(kv.Value, results);
                    return r;
                }
                case JsonArray a:
                {
                    var r = new JsonArray();
                    foreach (var x in a) r.Add(SubstJson(x, results));
                    return r;
                }
                default:
                    return v.GetValueKind() == JsonValueKind.String ? SubstValue(v.GetValue<string>(), results) : v.DeepClone();
            }
        }

        /// <summary>Использует ли пакет шаги (то, что сервер без steps молча проигнорировал бы).</summary>
        public static bool UsesSteps(JsonArray ops) => ops.Any(o => o is JsonObject x &&
            (x["id"] != null || x["expect"] != null || Json.Bool(x, "always") || x["onError"] != null || x["timeout"] != null ||
             Json.StrArray(x, "args").Any(a => a.Contains("${")) || (x["params"] != null && Json.Write(x["params"]).Contains("${"))));

        /// <summary>Проверить результат шага. Пусто — всё сходится. Без expect ожидается код 0.</summary>
        public static List<string> CheckExpect(JsonObject expect, int exit, string output, JsonNode data)
        {
            var bad = new List<string>();
            var wantExit = expect?["exit"] != null ? (int)Json.Num(expect, "exit") : 0;
            if (exit != wantExit) bad.Add($"код {exit}, ожидался {wantExit}");
            if (expect == null) return bad;
            output ??= string.Empty;
            var has = Json.Str(expect, "output");
            if (has != null && !output.Contains(has, StringComparison.Ordinal)) bad.Add($"в выводе нет «{has}»");
            var not = Json.Str(expect, "notOutput");
            if (not != null && output.Contains(not, StringComparison.Ordinal)) bad.Add($"в выводе есть «{not}»");
            if (expect["data"] is JsonObject wanted)
                foreach (var kv in wanted)
                {
                    JsonNode cur = data;
                    foreach (var part in kv.Key.Split('.'))
                        cur = cur switch
                        {
                            JsonObject o => o[part],
                            JsonArray a when int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                            _ => null,
                        };
                    if (cur == null) bad.Add($"в data нет {kv.Key}");
                    else if (!Same(cur, kv.Value)) bad.Add($"data.{kv.Key} = {Json.Write(cur)}, ожидалось {Json.Write(kv.Value)}");
                }

            return bad;
        }

        // 3 и 3.0, "3" и 3 — одно и то же для проверки
        private static bool Same(JsonNode a, JsonNode b) => JsonNode.DeepEquals(a, b) || AsText(a) == AsText(b) ||
            (a.GetValueKind() == JsonValueKind.Number && b?.GetValueKind() == JsonValueKind.Number && a.GetValue<double>() == b.GetValue<double>());

        public sealed class StepError : Exception
        {
            public StepError(string message) : base(message)
            {
            }
        }
    }
}
