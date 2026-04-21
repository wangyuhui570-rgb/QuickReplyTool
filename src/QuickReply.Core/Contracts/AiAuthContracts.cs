namespace QuickReply.Core.Contracts;

public class CodexCliAuthStatusResponse
{
    public bool CliAvailable { get; init; }
    public bool LoggedIn { get; init; }
    public string LoginMode { get; init; } = string.Empty;
    public string LoginModeDisplayName { get; init; } = "未登录";
    public string Message { get; init; } = string.Empty;
}

public sealed class CodexCliLoginRequest
{
    public bool UseDeviceAuth { get; init; } = true;
}

public class CodexCliAuthActionResponse : CodexCliAuthStatusResponse
{
    public bool Success { get; init; }
    public string CommandOutput { get; init; } = string.Empty;
}

public sealed class CloudServiceRuntimeInfoResponse
{
    public bool Ok { get; init; }
    public string Service { get; init; } = "QuickReply.CloudService";
    public string Version { get; init; } = string.Empty;
    public bool DatabaseReady { get; init; }
    public string DatabasePath { get; init; } = string.Empty;
    public bool AiSynthesisEnabled { get; init; }
    public string AiProvider { get; init; } = string.Empty;
    public bool AiReady { get; init; }
    public bool ManagementApiTokenConfigured { get; init; }
    public bool LoopbackBypassEnabled { get; init; }
    public bool CodexCliAvailable { get; init; }
    public bool CodexLoggedIn { get; init; }
    public string CodexLoginMode { get; init; } = string.Empty;
    public string[] Warnings { get; init; } = [];
}
