namespace QuickReply.CloudService;

public interface ICodexCliCommandRunner
{
    Task<CodexCliCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        string? workingDirectory = null,
        int timeoutMs = 0,
        CancellationToken cancellationToken = default);
}

public sealed class CodexCliCommandResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
}
