namespace QuickReply.Desktop.Services;

public readonly record struct ComposeOperationTelemetry(
    string ComposeKind,
    string HostProfile,
    int AttemptCount,
    bool RetryTriggered,
    string RetryReason,
    string RetryOutcome,
    string FailureCategory,
    bool TargetChangedBeforeRetry)
{
    public static ComposeOperationTelemetry Create(
        string composeKind,
        string hostProfile,
        int attemptCount,
        bool retryTriggered,
        string retryReason,
        string retryOutcome,
        string failureCategory,
        bool targetChangedBeforeRetry = false)
    {
        return new ComposeOperationTelemetry(
            composeKind?.Trim() ?? string.Empty,
            hostProfile?.Trim() ?? string.Empty,
            Math.Max(1, attemptCount),
            retryTriggered,
            retryReason?.Trim() ?? string.Empty,
            retryOutcome?.Trim() ?? string.Empty,
            failureCategory?.Trim() ?? string.Empty,
            targetChangedBeforeRetry);
    }
}
