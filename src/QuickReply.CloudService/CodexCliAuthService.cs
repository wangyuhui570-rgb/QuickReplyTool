using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

public sealed class CodexCliAuthService
{
    private readonly ICodexCliCommandRunner _commandRunner;
    private readonly IOptions<KnowledgeCloudOptions> _options;
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private CodexCliAuthStatusResponse? _cachedStatus;
    private DateTimeOffset _cachedAt;

    public CodexCliAuthService(
        ICodexCliCommandRunner commandRunner,
        IOptions<KnowledgeCloudOptions> options)
    {
        _commandRunner = commandRunner;
        _options = options;
    }

    public async Task<CodexCliAuthStatusResponse> GetStatusAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh &&
            _cachedStatus is not null &&
            DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(15))
        {
            return _cachedStatus;
        }

        await _statusGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh &&
                _cachedStatus is not null &&
                DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(15))
            {
                return _cachedStatus;
            }

            var status = await ProbeStatusCoreAsync(cancellationToken).ConfigureAwait(false);
            _cachedStatus = status;
            _cachedAt = DateTimeOffset.UtcNow;
            return status;
        }
        finally
        {
            _statusGate.Release();
        }
    }

    public async Task<CodexCliAuthActionResponse> LoginAsync(
        bool useDeviceAuth = true,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "login" };
        if (useDeviceAuth)
        {
            args.Add("--device-auth");
        }

        try
        {
            var result = await _commandRunner.RunAsync(
                args,
                timeoutMs: Math.Max(10000, _options.Value.CodexCliRequestTimeoutMs),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            InvalidateCache();

            var status = await GetStatusAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            return new CodexCliAuthActionResponse
            {
                Success = result.ExitCode == 0 && status.LoggedIn,
                Message = status.LoggedIn
                    ? $"已通过 Codex CLI 登录，当前模式：{status.LoginModeDisplayName}。"
                    : BuildCommandFailureMessage("codex login", result),
                LoginMode = status.LoginMode,
                LoginModeDisplayName = status.LoginModeDisplayName,
                CommandOutput = MergeOutput(result),
                CliAvailable = status.CliAvailable,
                LoggedIn = status.LoggedIn
            };
        }
        catch (Exception ex)
        {
            return new CodexCliAuthActionResponse
            {
                Success = false,
                Message = $"无法启动 Codex CLI 登录流程：{ex.Message}",
                LoginMode = string.Empty,
                LoginModeDisplayName = "未登录",
                CommandOutput = string.Empty,
                CliAvailable = false,
                LoggedIn = false
            };
        }
    }

    public async Task<CodexCliAuthActionResponse> LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _commandRunner.RunAsync(
                ["logout"],
                timeoutMs: 30000,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            InvalidateCache();
            var status = await GetStatusAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);

            return new CodexCliAuthActionResponse
            {
                Success = result.ExitCode == 0,
                Message = result.ExitCode == 0
                    ? "已通过 Codex CLI 退出登录。"
                    : BuildCommandFailureMessage("codex logout", result),
                LoginMode = status.LoginMode,
                LoginModeDisplayName = status.LoginModeDisplayName,
                CommandOutput = MergeOutput(result),
                CliAvailable = status.CliAvailable,
                LoggedIn = status.LoggedIn
            };
        }
        catch (Exception ex)
        {
            return new CodexCliAuthActionResponse
            {
                Success = false,
                Message = $"无法执行 Codex CLI 退出登录：{ex.Message}",
                LoginMode = string.Empty,
                LoginModeDisplayName = "未登录",
                CommandOutput = string.Empty,
                CliAvailable = false,
                LoggedIn = false
            };
        }
    }

    private async Task<CodexCliAuthStatusResponse> ProbeStatusCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _commandRunner.RunAsync(
                ["login", "status"],
                timeoutMs: 15000,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var output = MergeOutput(result);
            var trimmed = output.Trim();
            if (result.ExitCode != 0)
            {
                return new CodexCliAuthStatusResponse
                {
                    CliAvailable = true,
                    LoggedIn = false,
                    LoginMode = "none",
                    LoginModeDisplayName = "未登录",
                    Message = string.IsNullOrWhiteSpace(trimmed)
                        ? "Codex CLI 已安装，但当前未登录。"
                        : trimmed
                };
            }

            if (trimmed.Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase))
            {
                return new CodexCliAuthStatusResponse
                {
                    CliAvailable = true,
                    LoggedIn = true,
                    LoginMode = "chatgpt",
                    LoginModeDisplayName = "ChatGPT / Codex 登录",
                    Message = "已检测到 Codex CLI 的 ChatGPT 登录态。"
                };
            }

            if (trimmed.Contains("Logged in using API key", StringComparison.OrdinalIgnoreCase))
            {
                return new CodexCliAuthStatusResponse
                {
                    CliAvailable = true,
                    LoggedIn = true,
                    LoginMode = "api_key",
                    LoginModeDisplayName = "API key 登录",
                    Message = "已检测到 Codex CLI 的 API key 登录态。"
                };
            }

            return new CodexCliAuthStatusResponse
            {
                CliAvailable = true,
                LoggedIn = result.ExitCode == 0,
                LoginMode = result.ExitCode == 0 ? "unknown" : "none",
                LoginModeDisplayName = result.ExitCode == 0 ? "已登录（未知模式）" : "未登录",
                Message = string.IsNullOrWhiteSpace(trimmed)
                    ? "Codex CLI 状态未知。"
                    : trimmed
            };
        }
        catch (Exception ex)
        {
            return new CodexCliAuthStatusResponse
            {
                CliAvailable = false,
                LoggedIn = false,
                LoginMode = "unavailable",
                LoginModeDisplayName = "不可用",
                Message = $"未检测到可用的 Codex CLI：{ex.Message}"
            };
        }
    }

    private void InvalidateCache()
    {
        _cachedStatus = null;
        _cachedAt = DateTimeOffset.MinValue;
    }

    private static string MergeOutput(CodexCliCommandResult result)
    {
        return string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
    }

    private static string BuildCommandFailureMessage(string commandName, CodexCliCommandResult result)
    {
        if (result.TimedOut)
        {
            return $"{commandName} 超时。";
        }

        var output = MergeOutput(result);
        return string.IsNullOrWhiteSpace(output)
            ? $"{commandName} 失败，退出码：{result.ExitCode}。"
            : output;
    }
}
