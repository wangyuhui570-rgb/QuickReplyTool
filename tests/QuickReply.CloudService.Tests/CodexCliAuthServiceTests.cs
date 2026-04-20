using Microsoft.Extensions.Options;
using QuickReply.CloudService;

namespace QuickReply.CloudService.Tests;

public sealed class CodexCliAuthServiceTests
{
    [Fact]
    public async Task GetStatusAsync_ReturnsChatGptMode_WhenCliReportsChatGpt()
    {
        var service = new CodexCliAuthService(
            new FakeCodexCliCommandRunner(
                new CodexCliCommandResult
                {
                    ExitCode = 0,
                    StandardOutput = "Logged in using ChatGPT"
                }),
            Options.Create(new KnowledgeCloudOptions()));

        var status = await service.GetStatusAsync(forceRefresh: true);

        Assert.True(status.CliAvailable);
        Assert.True(status.LoggedIn);
        Assert.Equal("chatgpt", status.LoginMode);
    }

    [Fact]
    public async Task CodexCliSynthesizer_ReturnsStructuredDraft_WhenExecSucceeds()
    {
        var options = Options.Create(new KnowledgeCloudOptions
        {
            EnableAiSynthesis = true,
            CodexCliCommand = "codex",
            CodexCliModelName = "gpt-5.4",
            CodexCliReasoningEffort = "medium",
            CodexCliRequestTimeoutMs = 5000
        });
        var runner = new RecordingCodexCliCommandRunner();
        var authService = new CodexCliAuthService(runner, options);
        var synthesizer = new CodexCliKnowledgeCandidateSynthesizer(runner, authService, options);

        var result = await synthesizer.TrySynthesizeAsync(
            new KnowledgeCandidateRecord
            {
                Id = Guid.NewGuid(),
                Status = "draft",
                CandidateKind = KnowledgeCandidateKinds.Qa,
                Platform = "wecom",
                ProductKey = "sku-1",
                QuestionNormalized = "可以开发票吗",
                SourceCount = 2,
                Tags = ["发票"],
                ClusterKey = "cluster",
                CreatedAt = DateTimeOffset.Now,
                UpdatedAt = DateTimeOffset.Now
            },
            [
                new KnowledgeCandidateSourceRecord
                {
                    Id = Guid.NewGuid(),
                    CandidateId = Guid.NewGuid(),
                    SourceType = "conversation",
                    SourceId = "1",
                    Excerpt = "可以开发票吗 => 您好，支持开发票，下单后备注开票信息即可。",
                    CreatedAt = DateTimeOffset.Now
                }
            ]);

        Assert.NotNull(result);
        Assert.Equal("支持开发票吗", result!.Question);
        Assert.Contains("\"-\"", runner.ExecArgumentsCombined);
    }

    private sealed class FakeCodexCliCommandRunner : ICodexCliCommandRunner
    {
        private readonly CodexCliCommandResult _statusResult;

        public FakeCodexCliCommandRunner(CodexCliCommandResult statusResult)
        {
            _statusResult = statusResult;
        }

        public Task<CodexCliCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput = null,
            string? workingDirectory = null,
            int timeoutMs = 0,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_statusResult);
        }
    }

    private sealed class RecordingCodexCliCommandRunner : ICodexCliCommandRunner
    {
        public string ExecArgumentsCombined { get; private set; } = string.Empty;

        public Task<CodexCliCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput = null,
            string? workingDirectory = null,
            int timeoutMs = 0,
            CancellationToken cancellationToken = default)
        {
            if (arguments.Count >= 2 &&
                string.Equals(arguments[0], "login", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(arguments[1], "status", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new CodexCliCommandResult
                {
                    ExitCode = 0,
                    StandardOutput = "Logged in using ChatGPT"
                });
            }

            ExecArgumentsCombined = string.Join(" ", arguments.Select(value => $"\"{value}\""));
            var outputIndex = arguments
                .Select((value, index) => new { value, index })
                .First(item => item.value == "--output-last-message")
                .index;
            var outputPath = arguments[outputIndex + 1];
            File.WriteAllText(
                outputPath,
                """
                {"question":"支持开发票吗","answerDraft":"您好，支持开发票，下单后备注开票信息即可。","tags":["发票"],"riskLevel":"medium","evidenceSummary":"来源汇总：conversation 1 条。","confidence":0.82}
                """);

            return Task.FromResult(new CodexCliCommandResult
            {
                ExitCode = 0,
                StandardOutput = "{\"ok\":\"yes\"}"
            });
        }
    }
}
