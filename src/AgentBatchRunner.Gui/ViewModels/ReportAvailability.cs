using System.IO;

namespace AgentBatchRunner.Gui.ViewModels;

public static class ReportAvailability
{
    public const string MissingReportMessage = "Run is still running or did not complete. Report is not available yet.";

    public static string GetReportOpenMessage(string reportPath)
    {
        return string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath)
            ? MissingReportMessage
            : string.Empty;
    }
}
