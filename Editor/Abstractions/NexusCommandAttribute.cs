using System;

namespace Exerussus.Nexus.Abstractions
{
    /// <summary>
    /// Команда CLI Nexus: <c>static int Method(NexusCommandContext ctx)</c> (или <c>void</c> — тогда 0 при успехе).
    /// Хаб находит её в скомпилированном коде: команда есть ровно у развёрнутой страницы или сервиса —
    /// выключенная страница своих команд не даёт («off means off»).
    /// </summary>
    /// <remarks>
    /// <para>Команда работает в главном потоке редактора, без окон и диалогов: всё, что требует человека,
    /// не делается или возвращается ошибкой. Вывод — <see cref="NexusCommandContext.Print"/>, данные для
    /// машины — <see cref="NexusCommandContext.Data"/>.</para>
    /// <para>Имя — точки и латиница (<c>analytics.report</c>), префикс — область страницы.</para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class NexusCommandAttribute : Attribute
    {
        public NexusCommandAttribute(string name, string summary)
        {
            Name = name;
            Summary = summary;
        }

        public string Name { get; }
        public string Summary { get; }

        /// <summary>Строка использования для help: <c>analytics.export --format csv|json|jsonl [--out файл]</c>.</summary>
        public string Usage { get; set; }
    }
}
