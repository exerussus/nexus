using System.Collections.Generic;
using System.Text;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Theme;
using UnityEngine;
using UnityEngine.UIElements;

namespace Exerussus.Nexus.UI
{
    /// <summary>
    /// Лёгкий рендерер README — ПОДМНОЖЕСТВО markdown, на токенах темы, без внешних
    /// зависимостей. Поддерживается (см. скилл):
    ///   блоки: # / ## / ### заголовки; "- "/"* " маркированный список; "N. " нумерованный;
    ///          ``` … ``` блок кода; "> " цитата; --- / *** горизонтальная линия;
    ///          пустая строка = разрыв абзаца;
    ///   inline: **жирный**, *курсив*, `код`, [текст](url) (ссылка стилизуется, не кликается).
    /// НЕ поддерживается: таблицы, картинки, вложенные списки, inline-HTML, _курсив_, чек-листы.
    /// Неизвестное трактуется как обычный абзац (рендерер устойчив к мусору).
    /// </summary>
    public static class MarkdownView
    {
        // добавляет блоки в parent (не очищает — очисткой управляет вызывающий)
        public static void Render(VisualElement parent, string markdown)
        {
            if (parent == null) return;
            var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                if (trimmed.StartsWith("```"))   // блок кода до закрывающей ```
                {
                    i++;
                    var code = new List<string>();
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) { code.Add(lines[i]); i++; }
                    if (i < lines.Length) i++;   // пропустить закрывающую
                    parent.Add(CodeBlock(string.Join("\n", code)));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line)) { parent.Add(Spacer(4f)); i++; continue; }
                if (trimmed == "---" || trimmed == "***") { parent.Add(Rule()); i++; continue; }

                if (line.StartsWith("### ")) { parent.Add(Heading(line.Substring(4), 3)); i++; continue; }
                if (line.StartsWith("## "))  { parent.Add(Heading(line.Substring(3), 2)); i++; continue; }
                if (line.StartsWith("# "))   { parent.Add(Heading(line.Substring(2), 1)); i++; continue; }
                if (line.StartsWith("> "))   { parent.Add(Quote(line.Substring(2))); i++; continue; }

                var t = line.TrimStart();
                if (t.StartsWith("- ") || t.StartsWith("* ")) { parent.Add(Bullet("\u2022", t.Substring(2))); i++; continue; }

                var digits = OrderedPrefix(t);
                if (digits > 0) { parent.Add(Bullet(t.Substring(0, digits) + ".", t.Substring(digits + 2))); i++; continue; }

                parent.Add(Paragraph(line));
                i++;
            }
        }

        // число ведущих цифр, если строка вида "N. " ; иначе 0
        private static int OrderedPrefix(string s)
        {
            var k = 0;
            while (k < s.Length && char.IsDigit(s[k])) k++;
            return k > 0 && k + 1 < s.Length && s[k] == '.' && s[k + 1] == ' ' ? k : 0;
        }

        // ----- блоки -----

        private static Label Heading(string s, int level)
        {
            var l = NewRich(Inline(s));
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.color = Col(NexusToken.TextNormal);
            l.style.fontSize = level == 1 ? 16f : level == 2 ? 14f : 12.5f;
            l.style.marginTop = level == 1 ? 8f : 6f;
            l.style.marginBottom = 3f;
            return l;
        }

        private static Label Paragraph(string s)
        {
            var l = NewRich(Inline(s));
            l.style.color = Col(NexusToken.TextNormal);
            l.style.marginBottom = 2f;
            return l;
        }

        private static VisualElement Bullet(string marker, string content)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginLeft = 8f;
            row.style.marginBottom = 1f;

            var m = new Label(marker);
            m.style.color = Col(NexusToken.TextDim);
            m.style.marginRight = 6f;
            m.style.minWidth = 14f;
            row.Add(m);

            var c = NewRich(Inline(content));
            c.style.color = Col(NexusToken.TextNormal);
            c.style.flexGrow = 1f;
            row.Add(c);
            return row;
        }

        private static VisualElement Quote(string s)
        {
            var box = new VisualElement();
            box.style.flexDirection = FlexDirection.Row;
            box.style.marginLeft = 4f;
            box.style.marginBottom = 2f;
            box.style.borderLeftWidth = 3f;
            box.style.borderLeftColor = Col(NexusToken.Border);
            box.style.paddingLeft = 8f;

            var c = NewRich(Inline(s));
            c.style.color = Col(NexusToken.TextDim);
            c.style.unityFontStyleAndWeight = FontStyle.Italic;
            c.style.flexGrow = 1f;
            box.Add(c);
            return box;
        }

        private static VisualElement CodeBlock(string code)
        {
            var l = new Label(code) { enableRichText = false };   // код показываем как есть
            l.style.color = Col(NexusToken.TextNormal);
            l.style.backgroundColor = Col(NexusToken.BgRaised);
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.paddingLeft = 8f; l.style.paddingRight = 8f;
            l.style.paddingTop = 6f; l.style.paddingBottom = 6f;
            l.style.marginTop = 2f; l.style.marginBottom = 4f;
            Round(l, 3f);
            return l;
        }

        private static VisualElement Rule()
        {
            var v = new VisualElement();
            v.style.height = 1f;
            v.style.backgroundColor = Col(NexusToken.Border);
            v.style.marginTop = 6f;
            v.style.marginBottom = 6f;
            return v;
        }

        private static VisualElement Spacer(float h)
        {
            var v = new VisualElement();
            v.style.height = h;
            return v;
        }

        // ----- inline -----

        // markdown-inline → rich-text строка Unity (<b>/<i>/<color>/<u>)
        private static string Inline(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder();
            int i = 0, n = s.Length;
            while (i < n)
            {
                var c = s[i];

                if (c == '`')
                {
                    var j = s.IndexOf('`', i + 1);
                    if (j > i)
                    {
                        sb.Append("<color=#").Append(Hex(NexusToken.Accent)).Append('>')
                          .Append(s, i + 1, j - i - 1).Append("</color>");
                        i = j + 1; continue;
                    }
                }
                else if (c == '*' && i + 1 < n && s[i + 1] == '*')
                {
                    var j = s.IndexOf("**", i + 2);
                    if (j >= 0)
                    {
                        sb.Append("<b>").Append(Inline(s.Substring(i + 2, j - (i + 2)))).Append("</b>");
                        i = j + 2; continue;
                    }
                }
                else if (c == '*')
                {
                    var j = s.IndexOf('*', i + 1);
                    if (j > i)
                    {
                        sb.Append("<i>").Append(Inline(s.Substring(i + 1, j - i - 1))).Append("</i>");
                        i = j + 1; continue;
                    }
                }
                else if (c == '[')
                {
                    var close = s.IndexOf(']', i + 1);
                    if (close > i && close + 1 < n && s[close + 1] == '(')
                    {
                        var urlEnd = s.IndexOf(')', close + 2);
                        if (urlEnd > close)
                        {
                            var label = s.Substring(i + 1, close - i - 1);
                            sb.Append("<color=#").Append(Hex(NexusToken.Accent)).Append("><u>")
                              .Append(Inline(label)).Append("</u></color>");
                            i = urlEnd + 1; continue;
                        }
                    }
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // ----- helpers -----

        private static Label NewRich(string richText)
        {
            var l = new Label(richText) { enableRichText = true };
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        private static void Round(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        private static Color Col(NexusToken t) => NexusTheme.Get(t);
        private static string Hex(NexusToken t) => ColorUtility.ToHtmlStringRGB(NexusTheme.Get(t));
    }
}
