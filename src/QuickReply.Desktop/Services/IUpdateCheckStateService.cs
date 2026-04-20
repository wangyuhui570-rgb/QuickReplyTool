using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal interface IUpdateCheckStateService
{
    Task MarkKnowledgeVersionSeenAsync(AppSettings settingsSnapshot, string knowledgeVersion);
}
