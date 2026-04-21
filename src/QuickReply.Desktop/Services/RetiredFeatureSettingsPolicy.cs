using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class RetiredFeatureSettingsPolicy
{
    public static void Apply(AppSettings settings)
    {
        settings.EnableCloudSync = false;
        settings.AllowRawConversationCloudSync = false;
        settings.AllowAutomaticIssueReportUpload = false;
    }
}
