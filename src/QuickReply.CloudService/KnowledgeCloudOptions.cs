namespace QuickReply.CloudService;

public sealed class KnowledgeCloudOptions
{
    public const string SectionName = "KnowledgeCloud";

    public string DatabasePath { get; set; } = "data/quickreply-cloud.db";
    public string ManagementApiToken { get; set; } = string.Empty;
    public bool AllowLoopbackWithoutAuth { get; set; } = true;
}
