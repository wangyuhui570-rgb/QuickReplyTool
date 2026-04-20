namespace QuickReply.Desktop.Services;

public enum ReplyActionFailureKind
{
    None = 0,
    EmptyContent,
    MissingInput,
    MissingTargetWindow,
    ActivationFailed,
    FocusLost,
    ClipboardBusy,
    TargetChanged,
    ExternalTool,
    Unexpected
}

public readonly record struct ReplyActionResult(
    bool Success,
    string Message,
    ComposeOperationTelemetry? ComposeTelemetry = null,
    ReplyActionFailureKind FailureKind = ReplyActionFailureKind.None,
    string UserGuidance = "")
{
    public bool HasUserGuidance => !string.IsNullOrWhiteSpace(UserGuidance);
}
