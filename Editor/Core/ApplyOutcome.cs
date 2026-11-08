using System.Collections.Generic;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Итог файловой части Apply (<see cref="ApplyService.Execute"/>). Refresh и Client.Resolve здесь ещё не вызваны —
    /// это решает вызывающий: окно Manage делает их сразу, CLI — в тике задания, после ответа клиенту.
    /// </summary>
    public sealed class ApplyOutcome
    {
        /// <summary>Строки отчёта — то же, что окно пишет в консоль после перезагрузки.</summary>
        public readonly List<string> Report = new List<string>();

        /// <summary>Операции, которые не удались (id).</summary>
        public readonly List<string> Failed = new List<string>();

        /// <summary>Выключения, отложенные вето страницы (несохранённое).</summary>
        public readonly List<string> Vetoed = new List<string>();

        /// <summary>Страницы, отложенные до установки UPM-пакетов (развернёт <see cref="PackageDeployContinuation"/>).</summary>
        public readonly List<string> AwaitingPackages = new List<string>();

        /// <summary>Нужно вызвать Client.Resolve — в Packages/manifest.json дописаны пакеты.</summary>
        public bool ResolvePackages;
    }
}
