using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

public sealed class CodexCliCommandRunner : ICodexCliCommandRunner
{
    private readonly IOptions<KnowledgeCloudOptions> _options;

    public CodexCliCommandRunner(IOptions<KnowledgeCloudOptions> options)
    {
        _options = options;
    }

    public async Task<CodexCliCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        string? workingDirectory = null,
        int timeoutMs = 0,
        CancellationToken cancellationToken = default)
    {
        var command = (_options.Value.CodexCliCommand ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Codex CLI command is not configured.");
        }

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory
        };

        if (OperatingSystem.IsWindows() &&
            (Path.GetExtension(command).Length == 0 || string.Equals(Path.GetExtension(command), ".ps1", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.FileName = command;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = timeoutMs > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        timeoutCts?.CancelAfter(timeoutMs);
        var waitToken = timeoutCts?.Token ?? cancellationToken;

        try
        {
            await process.WaitForExitAsync(waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            return new CodexCliCommandResult
            {
                ExitCode = -1,
                StandardOutput = await outputTask.ConfigureAwait(false),
                StandardError = await errorTask.ConfigureAwait(false),
                TimedOut = true
            };
        }

        return new CodexCliCommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await outputTask.ConfigureAwait(false),
            StandardError = await errorTask.ConfigureAwait(false),
            TimedOut = false
        };
    }
}
