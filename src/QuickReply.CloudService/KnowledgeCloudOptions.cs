namespace QuickReply.CloudService;

public sealed class KnowledgeCloudOptions
{
    public const string SectionName = "KnowledgeCloud";

    public string DatabasePath { get; set; } = "data/quickreply-cloud.db";
    public bool EnableAiSynthesis { get; set; }
    public string AiProvider { get; set; } = "auto";
    public string CodexCliCommand { get; set; } = "codex";
    public string CodexCliModelName { get; set; } = "gpt-5.4";
    public string CodexCliReasoningEffort { get; set; } = "medium";
    public int CodexCliRequestTimeoutMs { get; set; } = 180000;
    public string ManagementApiToken { get; set; } = string.Empty;
    public bool AllowLoopbackWithoutAuth { get; set; } = true;
    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string OllamaModelName { get; set; } = string.Empty;
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModelName { get; set; } = "gpt-5.4";
    public string OpenAiReasoningEffort { get; set; } = "medium";
    public int AiRequestTimeoutMs { get; set; } = 30000;
    public int SynthesisLeaseDurationSeconds { get; set; } = 180;
}
