using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Exerussus.Nexus.Abstractions;
using Exerussus.Nexus.Core;
using Exerussus.Nexus.Deployment;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Exerussus.Nexus.UnityCommands
{
    /// <summary>
    /// Бережная правка <c>Packages/manifest.json</c> и <c>Packages/packages-lock.json</c>: <see cref="JObject"/>
    /// хранит порядок ключей, запись — с тем же отступом и переводом строки, что были в файле, UTF-8 без BOM,
    /// атомарно (temp → replace). Всё, что команда не трогает (scopedRegistries, testables, enableLockFile …),
    /// остаётся дословно.
    /// </summary>
    /// <remarks>
    /// <see cref="PackageRegistry"/> ядра пишет manifest через модель с extension-data: смысл сохраняет, но может
    /// переставить ключи верхнего уровня и сменить форматирование. Командам пакетов нужен дифф ровно в правленые
    /// строки, поэтому здесь отдельный путь чтения-записи. Экземпляр живёт только внутри вызова.
    /// </remarks>
    internal sealed class NexusPackageJsonFile
    {
        private readonly string _newLine;
        private readonly int _indent;
        private readonly char _indentChar;
        private readonly bool _trailingNewLine;

        private NexusPackageJsonFile(string path, JObject root, string newLine, int indent, char indentChar, bool trailing)
        {
            FilePath = path;
            Root = root;
            _newLine = newLine;
            _indent = indent;
            _indentChar = indentChar;
            _trailingNewLine = trailing;
        }

        public static string ManifestPath => Path.Combine(NexusPaths.ProjectRoot, "Packages", "manifest.json");
        public static string LockPath => Path.Combine(NexusPaths.ProjectRoot, "Packages", "packages-lock.json");

        public string FilePath { get; }
        public JObject Root { get; }

        /// <summary>Имя файла для сообщений: <c>Packages/manifest.json</c>.</summary>
        public static string Rel(string path) => "Packages/" + Path.GetFileName(path);

        /// <summary>
        /// Прочитать файл. Нет файла — null (или ошибка при <paramref name="required"/>); битый JSON — всегда ошибка:
        /// молча работать поверх него нельзя, запись затёрла бы то, что человек правит руками.
        /// </summary>
        public static NexusPackageJsonFile Load(string path, bool required)
        {
            if (!File.Exists(path))
            {
                if (required) throw new NexusCommandException($"Нет файла {Rel(path)}.", NexusCommandContext.Exit.Failed);
                return null;
            }

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception e)
            {
                throw new NexusCommandException($"{Rel(path)}: не прочитать — {e.Message}", NexusCommandContext.Exit.Failed);
            }

            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                    root = JObject.Load(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
            }
            catch (Exception e)
            {
                throw new NexusCommandException($"{Rel(path)}: битый JSON — {NexusCliText.OneLine(e.Message, 200)}", NexusCommandContext.Exit.Failed);
            }

            var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            var trailing = text.EndsWith("\n", StringComparison.Ordinal);
            DetectIndent(text, out var indent, out var indentChar);
            return new NexusPackageJsonFile(path, root, newLine, indent, indentChar, trailing);
        }

        /// <summary>Объект верхнего уровня по ключу; <paramref name="create"/> — добавить в конец, если нет.</summary>
        public JObject Section(string key, bool create)
        {
            if (Root[key] is JObject o) return o;
            if (!create) return null;
            o = new JObject();
            Root[key] = o;
            return o;
        }

        /// <summary>Записать с исходным форматированием, атомарно.</summary>
        public void Save()
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            {
                sw.NewLine = "\n";
                using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented, Indentation = _indent, IndentChar = _indentChar })
                    Root.WriteTo(w);
            }

            var text = sb.ToString().Replace("\r\n", "\n");
            if (_newLine != "\n") text = text.Replace("\n", _newLine);
            if (_trailingNewLine) text += _newLine;

            var tmp = FilePath + ".nexus-tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch
                {
                    // временный файл не помешает: следующая запись его перезапишет
                }

                throw new NexusCommandException($"{Rel(FilePath)}: не записать — {e.Message}", NexusCommandContext.Exit.Failed);
            }
        }

        /// <summary>Отпечаток содержимого файла (SHA-1) — понять, переписал ли его Unity. Нет файла — «-».</summary>
        public static string Fingerprint(string path)
        {
            try
            {
                if (!File.Exists(path)) return "-";
                using (var sha = SHA1.Create())
                {
                    var hash = sha.ComputeHash(File.ReadAllBytes(path));
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
            }
            catch (Exception e)
            {
                NexusDiagnostics.Swallowed("pkg: отпечаток " + path, e);
                return "?";
            }
        }

        /// <summary>Отступ по первой строке со вложенным ключом; Unity пишет 2 пробела — это и запасной вариант.</summary>
        private static void DetectIndent(string text, out int indent, out char indentChar)
        {
            indent = 2;
            indentChar = ' ';
            var i = text.IndexOf('\n');
            while (i >= 0 && i + 1 < text.Length)
            {
                var j = i + 1;
                var c = text[j];
                if (c == ' ' || c == '\t')
                {
                    var n = 0;
                    while (j < text.Length && text[j] == c)
                    {
                        n++;
                        j++;
                    }

                    if (j < text.Length && text[j] != '\r' && text[j] != '\n')
                    {
                        indent = n;
                        indentChar = c;
                        return;
                    }
                }

                i = text.IndexOf('\n', i + 1);
            }
        }
    }
}
