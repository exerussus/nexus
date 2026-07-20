using UnityEditor;
using UnityEngine;

namespace Exerussus.Nexus.Core
{
    /// <summary>
    /// Apply делает все файловые операции ДО AssetDatabase.Refresh(), поэтому
    /// domain reload не рвёт процедуру на полуслове. Но reload стирает память и
    /// окно — поэтому сводку результата кладём в SessionState и логируем здесь,
    /// уже после перезагрузки. Это и есть «хвост транзакции».
    /// </summary>
    [InitializeOnLoad]
    public static class ApplyReportSink
    {
        internal const string ReportKey = "Exerussus.Nexus.applyReport";

        static ApplyReportSink()
        {
            var report = SessionState.GetString(ReportKey, string.Empty);
            if (string.IsNullOrEmpty(report)) return;

            SessionState.EraseString(ReportKey);
            Debug.Log("[Nexus] Apply завершён:\n" + report);
        }
    }
}
