using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Exerussus.Nexus.Manifests
{
    /// <summary>
    /// Манифест сервиса (Services/&lt;id&gt;/service-manifest.json). Только декларация
    /// контракта: никаких настроек и состояния (сервис stateless). Версия — зеркало
    /// кода (атрибут [Service]); код — источник истины, манифест — кэш для гейта.
    /// </summary>
    public sealed class ServiceManifest
    {
        public int    SchemaVersion { get; set; } = 1;
        public string Guid          { get; set; }
        public string ServiceId     { get; set; }
        public string Version       { get; set; }   // "major.minor"

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; }
    }
}
