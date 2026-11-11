using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Exerussus.Nexus.Daemon
{
    /// <summary>
    /// Проверка следа ИИ в том, что уходит в историю git: сообщения коммитов и тегов, личности автора и коммиттера,
    /// добавленные строки диффа. Чистые функции без состояния; регистр не важен.
    /// <para>Нарушение блокирует commit/tag/push (обход — только явный <c>--allow-ai-marks</c>), предупреждение показывается.</para>
    /// </summary>
    internal static class AiTraceCheck
    {
        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        /// <summary>Имена ассистентов и их компаний (в трейлерах соавторства и личностях).</summary>
        private const string AiNames = @"claude|anthropic|chat\s*gpt|\bgpt\b|gpt-?\d|openai|copilot|gemini|\bai\b|assistant|\bllm\b";

        /// <summary>Почтовые адреса ассистентов.</summary>
        private const string AiEmails = @"noreply@anthropic\.com|@anthropic\.com|@openai\.com|copilot@github\.com";

        /// <summary>Имена, упоминание которых как автора или соавтора — нарушение.</summary>
        private const string AuthorNames = @"claude|anthropic|chat\s*gpt|copilot|gemini|openai";

        private static readonly Regex NameOrEmail = new Regex($"{AiNames}|{AiEmails}", Opt);
        private static readonly Regex CoAuthor = new Regex(@"^\s*co-?authored-?by\s*:(.*)$", Opt);

        private static readonly (Regex Re, string Why)[] Rules =
        {
            (new Regex(@"claude-session\s*:", Opt), "трейлер Claude-Session"),
            (new Regex(@"\bclaude\.(ai|com)\b|\banthropic\.com\b", Opt), "ссылка на Claude/Anthropic"),
            (new Regex(@"generated\s+(with|by|using|via|from)\b.{0,40}?\b(claude|ai|chat\s*gpt|gpt|copilot|llm|gemini|openai|anthropic)\b", Opt), "«Generated with/by …» (ИИ)"),
            (new Regex("\U0001F916", Opt), "эмодзи 🤖"),
            (new Regex(@"\b(ai|llm)[-\s]?(generated|assisted|written|authored)\b|\bgenerated\s+by\s+(an\s+)?(ai|llm)\b", Opt), "пометка «AI-generated»"),
            (new Regex(@"сгенерирован\w*\s+(с\s+помощью\s+|при\s+помощи\s+)?(ии\b|нейросет\w*|искусственн\w*\s+интеллект\w*|chat\s*gpt|claude|copilot|gpt)", Opt), "«сгенерировано ИИ»"),
            (new Regex(@"(создан|написан|подготовлен|сделан)\w*\s+(с\s+помощью|при\s+помощи|силами)\s+(ии\b|нейросет\w*|искусственн\w*\s+интеллект\w*|" + AuthorNames + ")", Opt), "«создано с помощью ИИ»"),
            (new Regex(@"\b(with|by|via|using|thanks\s+to|courtesy\s+of)\s+(the\s+)?(help\s+of\s+)?(" + AuthorNames + @")\b", Opt), "ИИ как автор («with/by Claude»)"),
            (new Regex(@"\b(" + AuthorNames + @")(\s+code)?\s+(wrote|authored|generated|created|co-?authored)\b", Opt), "ИИ как автор («Claude wrote»)"),
            (new Regex(@"(с\s+помощью|при\s+помощи|совместно\s+с|вместе\s+с|написал\w*|автор\w*|соавтор\w*)\s*:?\s*(" + AuthorNames + @"|ии\b|нейросет\w*)", Opt), "ИИ как автор/соавтор"),
        };

        /// <summary>Просто упоминание (не как автора) — только предупреждение: например, «поддержка Claude Desktop».</summary>
        private static readonly Regex Mention = new Regex(@"\b(" + AuthorNames + @")\b", Opt);

        /// <summary>Нарушения в сообщении коммита или тега: «строка N: причина — «текст»».</summary>
        public static List<string> Message(string message)
        {
            var found = new List<string>();
            var lines = (message ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var why = Line(lines[i]);
                if (why != null) found.Add($"строка {i + 1}: {why} — «{Cut(lines[i].Trim(), 120)}»");
            }

            return found;
        }

        /// <summary>Предупреждения к сообщению: упоминания имён ассистентов, не попавшие в нарушения.</summary>
        public static List<string> Mentions(string message)
        {
            var found = new List<string>();
            var lines = (message ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (Line(lines[i]) == null && Mention.IsMatch(lines[i]))
                    found.Add($"строка {i + 1}: упоминание «{Mention.Match(lines[i]).Value}» — проверьте, что это не пометка авторства");
            return found;
        }

        /// <summary>Причина нарушения в одной строке или null.</summary>
        public static string Line(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var co = CoAuthor.Match(line);
            if (co.Success && NameOrEmail.IsMatch(co.Groups[1].Value)) return "трейлер Co-Authored-By с ИИ";
            foreach (var (re, why) in Rules)
                if (re.IsMatch(line))
                    return why;
            return null;
        }

        /// <summary>Маркеры ИИ в имени или почте (автор, коммиттер, автор тега).</summary>
        public static string Identity(string name, string email)
        {
            if (!string.IsNullOrEmpty(name) && NameOrEmail.IsMatch(name)) return $"в имени «{name}» маркер ИИ";
            if (!string.IsNullOrEmpty(email) && NameOrEmail.IsMatch(email)) return $"в почте «{email}» маркер ИИ";
            return null;
        }

        private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
