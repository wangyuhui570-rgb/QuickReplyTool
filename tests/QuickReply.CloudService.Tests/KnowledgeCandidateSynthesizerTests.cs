using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Options;
using QuickReply.CloudService;

namespace QuickReply.CloudService.Tests;

public sealed class KnowledgeCandidateSynthesizerTests
{
    [Fact]
    public async Task HybridSynthesizer_UsesOllamaResult_WhenAiResponseIsValid()
    {
        var synthesizer = CreateHybridSynthesizer(
            """
            {"question":"支持开发票吗","answerDraft":"您好，支持开发票，下单后备注开票信息即可。","tags":["发票","售后"],"riskLevel":"medium","evidenceSummary":"来源汇总：conversation 2 条。","confidence":0.86}
            """,
            enableAiSynthesis: true);

        var result = await synthesizer.SynthesizeAsync(CreateCandidate(), CreateSources());

        Assert.NotNull(result);
        Assert.Equal("支持开发票吗", result!.QuestionNormalized);
        Assert.Equal("您好，支持开发票，下单后备注开票信息即可。", result.AnswerDraft);
        Assert.Contains("售后", result.Tags);
        Assert.Equal("medium", result.RiskLevel);
        Assert.StartsWith("synthesized_by_ollama:", result.ReviewComment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HybridSynthesizer_UsesOpenAiResult_WhenConfigured()
    {
        var synthesizer = CreateHybridSynthesizer(
            ollamaContent: string.Empty,
            enableAiSynthesis: true,
            aiProvider: "openai",
            openAiContent:
            """
            {"output_text":"{\"question\":\"支持开发票吗\",\"answerDraft\":\"您好，支持开发票，下单后备注开票信息即可。\",\"tags\":[\"发票\",\"售后\"],\"riskLevel\":\"medium\",\"evidenceSummary\":\"来源汇总：conversation 2 条。\",\"confidence\":0.91}"}
            """,
            openAiApiKey: "sk-test");

        var result = await synthesizer.SynthesizeAsync(CreateCandidate(), CreateSources());

        Assert.NotNull(result);
        Assert.Equal("支持开发票吗", result!.QuestionNormalized);
        Assert.Equal("您好，支持开发票，下单后备注开票信息即可。", result.AnswerDraft);
        Assert.StartsWith("synthesized_by_openai:", result.ReviewComment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HybridSynthesizer_FallsBackToHeuristic_WhenAiFails()
    {
        var synthesizer = CreateHybridSynthesizer(
            ollamaContent: string.Empty,
            enableAiSynthesis: true,
            ollamaStatusCode: HttpStatusCode.InternalServerError);

        var result = await synthesizer.SynthesizeAsync(CreateCandidate(), CreateSources());

        Assert.NotNull(result);
        Assert.Equal("可以开发票吗", result!.QuestionNormalized);
        Assert.Equal("您好，这款商品支持开发票，下单后备注开票信息即可。", result.AnswerDraft);
        Assert.Equal("synthesized_by_heuristic_fallback", result.ReviewComment);
    }

    [Fact]
    public async Task HybridSynthesizer_FallsBackToOllama_WhenOpenAiFails()
    {
        var synthesizer = CreateHybridSynthesizer(
            ollamaContent:
            """
            {"question":"支持开发票吗","answerDraft":"您好，支持开发票，下单后备注开票信息即可。","tags":["发票"],"riskLevel":"medium","evidenceSummary":"来源汇总：conversation 2 条。","confidence":0.86}
            """,
            enableAiSynthesis: true,
            aiProvider: "auto",
            openAiContent: string.Empty,
            openAiStatusCode: HttpStatusCode.InternalServerError,
            openAiApiKey: "sk-test");

        var result = await synthesizer.SynthesizeAsync(CreateCandidate(), CreateSources());

        Assert.NotNull(result);
        Assert.Equal("支持开发票吗", result!.QuestionNormalized);
        Assert.StartsWith("synthesized_by_ollama:", result.ReviewComment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HybridSynthesizer_SkipsAi_ForResourceCandidates()
    {
        var synthesizer = CreateHybridSynthesizer(
            """
            {"question":"不会被使用","answerDraft":"不会被使用","tags":["无效"],"riskLevel":"low","evidenceSummary":"无","confidence":0.1}
            """,
            enableAiSynthesis: true);

        var candidate = new KnowledgeCandidateRecord
        {
            Id = Guid.NewGuid(),
            Status = "draft",
            CandidateKind = KnowledgeCandidateKinds.Resource,
            Platform = "qianniu",
            ProductKey = string.Empty,
            QuestionNormalized = "智骑软件下载",
            AnswerDraft = string.Empty,
            AnswerFinal = string.Empty,
            Tags = ["资料"],
            Confidence = 0.72d,
            RiskLevel = "low",
            SourceCount = 3,
            ClusterKey = "qianniu||resource|智骑软件下载",
            EvidenceSummary = string.Empty,
            ReviewComment = string.Empty,
            ReviewReason = string.Empty,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-1)
        };
        var sources = new List<KnowledgeCandidateSourceRecord>
        {
            new()
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                SourceType = "conversation",
                SourceId = "resource-1",
                Excerpt = "智骑软件下载 => 智骑软件下载地址：https://thinkrider.com/zh/pages/software"
            },
            new()
            {
                Id = Guid.NewGuid(),
                CandidateId = candidate.Id,
                SourceType = "conversation",
                SourceId = "resource-2",
                Excerpt = "智骑软件下载 => 智骑软件下载地址：https://thinkrider.com/zh/pages/software"
            }
        };

        var result = await synthesizer.SynthesizeAsync(candidate, sources);

        Assert.NotNull(result);
        Assert.Equal(KnowledgeCandidateKinds.Resource, result!.CandidateKind);
        Assert.Equal("synthesized_by_heuristic", result.ReviewComment);
    }

    private static HybridKnowledgeCandidateSynthesizer CreateHybridSynthesizer(
        string ollamaContent,
        bool enableAiSynthesis,
        HttpStatusCode ollamaStatusCode = HttpStatusCode.OK,
        string aiProvider = "ollama",
        string openAiContent = "",
        HttpStatusCode openAiStatusCode = HttpStatusCode.OK,
        string openAiApiKey = "")
    {
        var options = Options.Create(new KnowledgeCloudOptions
        {
            EnableAiSynthesis = enableAiSynthesis,
            AiProvider = aiProvider,
            CodexCliCommand = "codex",
            CodexCliModelName = "gpt-5.4",
            CodexCliReasoningEffort = "medium",
            CodexCliRequestTimeoutMs = 5000,
            OllamaBaseUrl = "http://127.0.0.1:11434",
            OllamaModelName = "qwen2.5:7b-instruct",
            OpenAiBaseUrl = "https://api.openai.com/v1/",
            OpenAiApiKey = openAiApiKey,
            OpenAiModelName = "gpt-5.4",
            OpenAiReasoningEffort = "medium",
            AiRequestTimeoutMs = 5000
        });
        var httpClientFactory = new FakeHttpClientFactory(new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal)
        {
            [nameof(OllamaKnowledgeCandidateSynthesizer)] = new OllamaFakeHttpMessageHandler(ollamaContent, ollamaStatusCode),
            [nameof(OpenAiKnowledgeCandidateSynthesizer)] = new PassthroughFakeHttpMessageHandler(openAiContent, openAiStatusCode)
        });
        var fakeRunner = new FakeCodexCliCommandRunner();
        var authService = new CodexCliAuthService(fakeRunner, options);
        return new HybridKnowledgeCandidateSynthesizer(
            options,
            new HeuristicKnowledgeCandidateSynthesizer(),
            new CodexCliKnowledgeCandidateSynthesizer(fakeRunner, authService, options),
            new OllamaKnowledgeCandidateSynthesizer(httpClientFactory, options),
            new OpenAiKnowledgeCandidateSynthesizer(httpClientFactory, options));
    }

    private static KnowledgeCandidateRecord CreateCandidate()
    {
        return new KnowledgeCandidateRecord
        {
            Id = Guid.NewGuid(),
            Status = "draft",
            CandidateKind = KnowledgeCandidateKinds.Qa,
            Platform = "wecom",
            ProductKey = "SKU-INV-1",
            QuestionNormalized = "可以开发票吗",
            AnswerDraft = string.Empty,
            AnswerFinal = string.Empty,
            Tags = ["发票"],
            Confidence = 0.72d,
            RiskLevel = "medium",
            SourceCount = 2,
            ClusterKey = "wecom|sku|invoice",
            EvidenceSummary = string.Empty,
            ReviewComment = string.Empty,
            ReviewReason = string.Empty,
            CreatedAt = DateTimeOffset.Now.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-1)
        };
    }

    private static List<KnowledgeCandidateSourceRecord> CreateSources()
    {
        return
        [
            new KnowledgeCandidateSourceRecord
            {
                Id = Guid.NewGuid(),
                CandidateId = Guid.NewGuid(),
                SourceType = "conversation",
                SourceId = "source-1",
                Excerpt = "可以开发票吗 => 您好，这款商品支持开发票，下单后备注开票信息即可。"
            },
            new KnowledgeCandidateSourceRecord
            {
                Id = Guid.NewGuid(),
                CandidateId = Guid.NewGuid(),
                SourceType = "conversation",
                SourceId = "source-2",
                Excerpt = "可以开发票吗 => 您好，这款商品支持开发票，下单时填写开票资料即可。"
            }
        ];
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyDictionary<string, HttpMessageHandler> _handlers;

        public FakeHttpClientFactory(IReadOnlyDictionary<string, HttpMessageHandler> handlers)
        {
            _handlers = handlers;
        }

        public HttpClient CreateClient(string name)
        {
            var handler = _handlers.TryGetValue(name, out var found)
                ? found
                : throw new InvalidOperationException($"Missing fake handler for client '{name}'.");

            var baseAddress = name == nameof(OpenAiKnowledgeCandidateSynthesizer)
                ? "https://api.openai.com/v1/"
                : "http://127.0.0.1:11434";

            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri(baseAddress)
            };
        }
    }

    private sealed class OllamaFakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly HttpStatusCode _statusCode;

        public OllamaFakeHttpMessageHandler(string content, HttpStatusCode statusCode)
        {
            _content = content;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = string.IsNullOrWhiteSpace(_content)
                ? string.Empty
                : $"{{\"message\":{{\"content\":{System.Text.Json.JsonSerializer.Serialize(_content)}}}}}";
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class PassthroughFakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly HttpStatusCode _statusCode;

        public PassthroughFakeHttpMessageHandler(string content, HttpStatusCode statusCode)
        {
            _content = content;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeCodexCliCommandRunner : ICodexCliCommandRunner
    {
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
                    ExitCode = 1,
                    StandardOutput = "Not logged in"
                });
            }

            return Task.FromResult(new CodexCliCommandResult
            {
                ExitCode = 1,
                StandardError = "unsupported test command"
            });
        }
    }
}
