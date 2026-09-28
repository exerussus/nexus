namespace Exerussus.Nexus.Core
{
    /// <summary>Вид корня плагинов (зеркало Deployment.PluginRootSource для слоя UI).</summary>
    public enum PluginRootKind
    {
        BuiltIn,
        ScanPath,
        Marker,
    }

    /// <summary>Строка секции «Корни плагинов» в Manage.</summary>
    public sealed class PluginRootRow
    {
        public PluginRootKind Kind;
        public string Path;        // project-relative или asset-путь
        public string Name;        // имя из маркера
        public bool   Exists;
        public bool   Duplicate;
    }
}
