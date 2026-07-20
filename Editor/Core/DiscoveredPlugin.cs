using Exerussus.Nexus.Manifests;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Результат дискавери по одному id. Манифест может быть null — для
    /// <see cref="PluginStatus.OrphanedState"/> (State без манифеста).
    /// </summary>
    public sealed class DiscoveredPlugin
    {
        public string         Id       { get; }
        public PluginStatus    Status  { get; }
        public PluginManifest  Manifest { get; }   // null для осиротевшего состояния

        public DiscoveredPlugin(string id, PluginStatus status, PluginManifest manifest)
        {
            Id       = id;
            Status   = status;
            Manifest = manifest;
        }

        /// <summary>Человекочитаемое имя: из манифеста, иначе сам id.</summary>
        public string DisplayName => Manifest?.Display?.Name ?? Id;

        /// <summary>Имя раздела сайдбара строкой; по умолчанию Infrastructure.</summary>
        public string Category => Manifest?.Display?.Category ?? "Infrastructure";

        public int Order => Manifest?.Display?.Order ?? 0;
    }
}
