using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Вызов команды CLI: аргументы, вывод, данные ответа. Создаётся хабом на каждый вызов и живёт только в нём.
    /// </summary>
    /// <remarks>
    /// Аргументы: позиционные — по порядку; <c>--ключ значение</c> или <c>--ключ=значение</c> — опции;
    /// <c>--флаг</c> без значения (за ним конец или другая опция) — флаг. Позиционные лучше ставить до опций:
    /// <c>--флаг слово</c> читается как опция со значением.
    /// </remarks>
    public sealed class NexusCommandContext
    {
        /// <summary>Коды выхода CLI.</summary>
        public static class Exit
        {
            public const int Ok = 0;
            /// <summary>Команда выполнилась с ошибкой (данные, файлы, проверка).</summary>
            public const int Failed = 1;
            /// <summary>Неверные аргументы.</summary>
            public const int Usage = 2;
            /// <summary>Нет такой команды.</summary>
            public const int UnknownCommand = 3;
            /// <summary>Исключение внутри команды.</summary>
            public const int Crashed = 4;
            /// <summary>Ответа не дождались (клиент ушёл или истёк срок).</summary>
            public const int Timeout = 5;
            /// <summary>Редактор перекомпилирует скрипты или закрывается — повторить позже.</summary>
            public const int Busy = 6;
        }

        private readonly List<string> _positional = new List<string>();
        private readonly Dictionary<string, string> _options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder _output = new StringBuilder();

        public NexusCommandContext(string name, IReadOnlyList<string> args, string workingDirectory)
        {
            Name = name ?? string.Empty;
            WorkingDirectory = workingDirectory;
            Raw = args ?? Array.Empty<string>();
            Parse(Raw);
        }

        /// <summary>Имя команды.</summary>
        public string Name { get; }

        /// <summary>Аргументы как пришли.</summary>
        public IReadOnlyList<string> Raw { get; }

        /// <summary>Текущая папка клиента (может быть вне проекта или null).</summary>
        public string WorkingDirectory { get; }

        public IReadOnlyList<string> Positional => _positional;

        /// <summary>Клиент перестал ждать: долгой команде стоит прекратить работу.</summary>
        public bool Cancelled { get; private set; }

        /// <summary>Данные для машины (<c>--json</c>): объект, словарь или список — хаб сериализует в JSON.</summary>
        public object Data { get; set; }

        /// <summary>
        /// Id задания хаба, которым продолжается команда (долгие операции: компиляция, Play, тесты). Задано и
        /// задание ещё идёт — клиент ждёт его конца (<c>job.status</c>), а не берёт этот ответ как итог.
        /// </summary>
        public string Job { get; set; }

        /// <summary>
        /// Команда изменила код или ассеты (сгенерировала файлы): после неё хаб ждёт импорта, компиляции и
        /// перезагрузки домена и отдаёт клиенту итог компиляции — как <c>refresh</c>. Работает при коде 0.
        /// </summary>
        public bool WaitForEditor { get; set; }

        /// <summary>Были ошибки (<see cref="Error"/>).</summary>
        public bool HasErrors { get; private set; }

        /// <summary>Текст для человека.</summary>
        public string Output => _output.ToString();

        // ---------------------------------------------------------------- аргументы

        public bool Has(string key) => _options.ContainsKey(key) || _flags.Contains(key);

        public bool Flag(string key) => _flags.Contains(key) || (_options.TryGetValue(key, out var v) && IsTrue(v));

        public string Get(string key, string fallback = null) => _options.TryGetValue(key, out var v) ? v : fallback;

        public int GetInt(string key, int fallback)
        {
            return _options.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        }

        /// <summary>Список через запятую или точку с запятой: <c>--tags a,b</c>.</summary>
        public List<string> GetList(string key)
        {
            var list = new List<string>();
            if (!_options.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return list;
            foreach (var part in v.Split(',', ';'))
                if (!string.IsNullOrWhiteSpace(part)) list.Add(part.Trim());
            return list;
        }

        /// <summary>Позиционный аргумент или null.</summary>
        public string Arg(int index) => index >= 0 && index < _positional.Count ? _positional[index] : null;

        private void Parse(IReadOnlyList<string> args)
        {
            for (var i = 0; i < args.Count; i++)
            {
                var a = args[i] ?? string.Empty;
                if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length == 2)
                {
                    _positional.Add(a);
                    continue;
                }

                var body = a.Substring(2);
                var eq = body.IndexOf('=');
                if (eq > 0)
                {
                    _options[body.Substring(0, eq)] = body.Substring(eq + 1);
                    continue;
                }

                if (i + 1 < args.Count && args[i + 1] != null && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    _options[body] = args[i + 1];
                    i++;
                }
                else
                {
                    _flags.Add(body);
                }
            }
        }

        private static bool IsTrue(string v) =>
            string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1" || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- вывод

        public void Print(string line)
        {
            _output.Append(line ?? string.Empty).Append('\n');
        }

        public void Warn(string line)
        {
            _output.Append("! ").Append(line ?? string.Empty).Append('\n');
        }

        public void Error(string line)
        {
            HasErrors = true;
            _output.Append("✗ ").Append(line ?? string.Empty).Append('\n');
        }

        /// <summary>Только для хаба: клиент перестал ждать.</summary>
        public void MarkCancelled() => Cancelled = true;
    }

    /// <summary>Остановить команду с кодом выхода и сообщением (обычно — неверные аргументы).</summary>
    public sealed class NexusCommandException : Exception
    {
        public NexusCommandException(string message, int exitCode = NexusCommandContext.Exit.Usage) : base(message) => ExitCode = exitCode;

        public int ExitCode { get; }
    }
}
