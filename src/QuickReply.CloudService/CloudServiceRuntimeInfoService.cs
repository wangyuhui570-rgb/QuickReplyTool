using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

public sealed class CloudServiceRuntimeInfoService
{
    private readonly KnowledgeCloudStore _store;
    private readonly CodexCliAuthService _authService;
    private readonly IOptions<KnowledgeCloudOptions> _options;

    public CloudServiceRuntimeInfoService(
        KnowledgeCloudStore store,
        CodexCliAuthService authService,
        IOptions<KnowledgeCloudOptions> options)
    {
        _store = store;
        _authService = authService;
        _options = options;
    }

    public async Task<CloudServiceRuntimeInfoResponse> GetRuntimeInfoAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var warnings = new List<string>();

        var databaseReady = true;
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            databaseReady = false;
            warnings.Add($"数据库初始化失败：{ex.Message}");
        }

        CodexCliAuthStatusResponse? codexStatus = null;
        if (NeedsCodexCli(options))
        {
            codexStatus = await _authService.GetStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!codexStatus.CliAvailable)
            {
                warnings.Add("未检测到可用的 Codex CLI。");
            }
            else if (!codexStatus.LoggedIn)
            {
                warnings.Add("Codex CLI 可用，但当前未登录。");
            }
        }

        var aiReady = EvaluateAiReadiness(options, codexStatus);
        if (options.EnableAiSynthesis && !aiReady)
        {
            warnings.Add("AI 综合已开启，但当前没有可用的 AI provider。");
        }

        return new CloudServiceRuntimeInfoResponse
        {
            Ok = databaseReady,
            Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            DatabaseReady = databaseReady,
            DatabasePath = options.DatabasePath,
            AiSynthesisEnabled = options.EnableAiSynthesis,
            AiProvider = options.AiProvider,
            AiReady = aiReady,
            ManagementApiTokenConfigured = !string.IsNullOrWhiteSpace(options.ManagementApiToken),
            LoopbackBypassEnabled = options.AllowLoopbackWithoutAuth,
            CodexCliAvailable = codexStatus?.CliAvailable ?? false,
            CodexLoggedIn = codexStatus?.LoggedIn ?? false,
            CodexLoginMode = codexStatus?.LoginMode ?? string.Empty,
            Warnings = warnings.ToArray()
        };
    }

    private static bool NeedsCodexCli(KnowledgeCloudOptions options)
    {
        if (!options.EnableAiSynthesis)
        {
            return false;
        }

        return string.Equals(options.AiProvider, "codex_cli", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(options.AiProvider, "auto", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateAiReadiness(KnowledgeCloudOptions options, CodexCliAuthStatusResponse? codexStatus)
    {
        if (!options.EnableAiSynthesis)
        {
            return true;
        }

        return (options.AiProvider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "codex_cli" => codexStatus?.CliAvailable == true && codexStatus.LoggedIn,
            "openai_api" => !string.IsNullOrWhiteSpace(options.OpenAiApiKey),
            "ollama" => !string.IsNullOrWhiteSpace(options.OllamaModelName),
            "auto" => (codexStatus?.CliAvailable == true && codexStatus.LoggedIn)
                      || !string.IsNullOrWhiteSpace(options.OpenAiApiKey)
                      || !string.IsNullOrWhiteSpace(options.OllamaModelName),
            _ => false
        };
    }
}
