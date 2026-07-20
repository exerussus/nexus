using System.Collections.Generic;

namespace Exerussus.Nexus.Deployment
{
    /// <summary>
    /// Служебный дескриптор развёртки. Лежит как deploy.json в State/&lt;id&gt; и как
    /// preserve.json в Preserve/&lt;id&gt;. Пишется ТОЛЬКО хабом, generic-механика —
    /// никаких настроек страницы (те живут в UserSettings через Context).
    ///
    /// <see cref="PluginGuid"/> скрепляет связь корней; <see cref="DeployedVersion"/>
    /// = версия манифеста на момент развёртки — вход для проверки протухания.
    /// </summary>
    public sealed class DeployDescriptor
    {
        public int          SchemaVersion   { get; set; } = 1;
        public string       PluginGuid      { get; set; }
        public string       DeployedVersion { get; set; }
        public List<string> Files           { get; set; } = new List<string>();
    }
}
