namespace Exerussus.Nexus.Core
{
    public enum IntentKind
    {
        Deploy   = 0,   // развернуть (включить)
        Undeploy = 1,   // выселить (выключить)
    }

    /// <summary>Намерение по одному плагину — копится в management-окне, применяется по Apply.</summary>
    public readonly struct PendingIntent
    {
        public readonly string     Id;
        public readonly IntentKind Kind;
        public PendingIntent(string id, IntentKind kind) { Id = id; Kind = kind; }
    }
}
