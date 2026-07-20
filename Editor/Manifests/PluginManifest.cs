using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Exerussus.Nexus.Manifests
{
    /// <summary>
    /// Манифест плагина-страницы (Plugins/&lt;id&gt;/manifest.json).
    ///
    /// Стабильное ядро идентичности (<see cref="SchemaVersion"/>, <see cref="Guid"/>,
    /// <see cref="Id"/>, <see cref="Version"/>, <see cref="TypeName"/>) не меняет форму
    /// никогда. Всё остальное растёт ТОЛЬКО аддитивно в пределах мажора схемы.
    ///
    /// <see cref="Extra"/> ловит и сохраняет неизвестные поля: старый хаб, читая
    /// манифест от более нового, обязан вернуть незнакомые ключи как есть, а не
    /// затереть. Именно поэтому здесь Newtonsoft, а не JsonUtility (тот молча
    /// выкидывает неизвестное).
    /// </summary>
    public sealed class PluginManifest
    {
        // --- стабильное ядро идентичности ---
        public int    SchemaVersion { get; set; } = 1;
        public string Guid          { get; set; }   // вечный ключ связи Plugins↔State↔Preserve
        public string Id            { get; set; }   // стабильный slug; имя папок State/Preserve/UserSettings
        public string Version       { get; set; }   // "major.minor" — для проверки протухания
        public string TypeName      { get; set; }   // текущий Type.FullName; РЕЗОЛВИМЫЙ указатель, не ключ

        // --- метаданные (аддитивно расширяемые) ---
        public ManifestDisplay            Display  { get; set; }
        public string                     Kind     { get; set; }   // "constitutive" | "servicing"
        public List<ServiceRequirement>   Requires { get; set; }
        public List<PackageDependency>    PackageRequires { get; set; }   // внешние UPM-пакеты (git)
        public List<DeployEntry>          Deploy   { get; set; }

        /// <summary>Все неизвестные поля — сохраняются и пишутся обратно дословно.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>Как страница показывается в сайдбаре.</summary>
    public sealed class ManifestDisplay
    {
        public string Name     { get; set; }
        public string Category { get; set; }   // имя PageCategory строкой — forward-safe
        public int    Order    { get; set; }
        public string Icon     { get; set; }   // имя встроенной editor-иконки, опционально

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>Зависимость страницы от статического сервиса (dependency-gate).</summary>
    public sealed class ServiceRequirement
    {
        public string Service { get; set; }   // serviceId
        public string Range    { get; set; }  // напр. "2.x &gt;=2.1" (мажор точный, минор снизу)
    }

    /// <summary>Внешний UPM-пакет, который странице нужен. Версии НЕ пиним — ставится
    /// последнее (git-URL без тега). Перед требованием установки Nexus проверяет, нет ли
    /// зависимости уже в проекте (UPM/embedded/вендор) — тогда git-установка не нужна.</summary>
    public sealed class PackageDependency
    {
        public string Name     { get; set; }   // имя UPM-пакета (ключ в Packages/manifest.json)
        public string GitUrl   { get; set; }   // git-URL (можно с ?path=); ставится последнее
        public string Assembly { get; set; }   // опц.: имя сборки для детекта «уже в проекте»
    }

    /// <summary>Один файл развёртки: инертный исток в Plugins → живой путь в State.</summary>
    public sealed class DeployEntry
    {
        public string From { get; set; }   // "LogsPage.cs.txt"
        public string To   { get; set; }   // "LogsPage.cs"
    }
}
