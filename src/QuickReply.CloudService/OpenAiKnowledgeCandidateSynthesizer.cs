using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

public sealed class OpenAiKnowledgeCandidateSynthesizer : IAiKnowledgeCandidateSynthesizer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<KnowledgeCloudOptions> _options;

    public OpenAiKnowledgeCandidateSynthesizer(
        IHttpClientFactory httpClientFactory,
        IOptions<KnowledgeCloudOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<AiKnowledgeSynthesisResult?> TrySynthesizeAsync(
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.EnableAiSynthesis ||
            string.IsNullOrWhiteSpace(options.OpenAiModelName) ||
            string.IsNullOrWhiteSpace(options.OpenAiApiKey))
        {
            return null;
        }

        var prompt = BuildPrompt(candidate, sources);
        using var client = _httpClientFactory.CreateClient(nameof(OpenAiKnowledgeCandidateSynthesizer));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.OpenAiApiKey.Trim());
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Max(1000, options.AiRequestTimeoutMs));

        using var response = await client.PostAsJsonAsync(
            "/responses",
            BuildRequest(options, prompt),
            timeoutCts.Token).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var responseContent = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        var modelResponse = ExtractResponseText(responseContent);
        if (string.IsNullOrWhiteSpace(modelResponse))
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<OpenAiKnowledgeSynthesisPayload>(modelResponse, SerializerOptions);
        if (payload is null)
        {
            return null;
        }

        var question = CleanText(payload.Question);
        var answerDraft = CleanText(payload.AnswerDraft);
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answerDraft))
        {
            return null;
        }

        return new AiKnowledgeSynthesisResult
        {
            Question = question,
            AnswerDraft = answerDraft,
            Tags = NormalizeTags(payload.Tags),
            RiskLevel = NormalizeRiskLevel(payload.RiskLevel),
            EvidenceSummary = CleanText(payload.EvidenceSummary),
            Confidence = NormalizeConfidence(payload.Confidence),
            ProviderComment = $"synthesized_by_openai:{options.OpenAiModelName}"
        };
    }

    private static object BuildRequest(KnowledgeCloudOptions options, string prompt)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = options.OpenAiModelName.Trim(),
            ["input"] = new object[]
            {
                new
                {
                    role = "system",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text =
                                "你是客服知识库整理助手。请根据用户问题、历史回复和来源摘要，输出一个严格 JSON 对象。" +
                                "输出字段必须只有：question, answerDraft, tags, riskLevel, evidenceSummary, confidence。" +
                                "question 和 answerDraft 用简体中文；tags 是字符串数组；riskLevel 只能是 low/medium/high；confidence 用 0 到 1 的数字。" +
                                "禁止补充来源里没有的新事实、时效承诺、赔付承诺或商品规格。" +
                                "如果来源证据不足、问题只是寒暄/缩写/内部关键词，或者无法整理成完整自然问句，必须把 question 和 answerDraft 都返回空字符串。"
                        }
                    }
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = prompt
                        }
                    }
                }
            }
        };

        var effort = CleanText(options.OpenAiReasoningEffort).ToLowerInvariant();
        if (effort is "minimal" or "low" or "medium" or "high")
        {
            request["reasoning"] = new { effort };
        }

        return request;
    }

    private static string BuildPrompt(
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources)
    {
        var sourceLines = sources
            .Take(8)
            .Select((source, index) =>
                $"{index + 1}. [{source.SourceType}] {TrimForPrompt(source.Excerpt, 220)}")
            .ToArray();
        var tags = candidate.Tags.Count == 0 ? "无" : string.Join("、", candidate.Tags);

        return
            $$"""
            请把下面这组客服样本整理成一条待审核知识卡草稿。

            候选类型：{{candidate.CandidateKind}}
            平台：{{candidate.Platform}}
            商品：{{candidate.ProductKey}}
            当前问题归一化：{{candidate.QuestionNormalized}}
            当前标签：{{tags}}
            当前来源数：{{candidate.SourceCount}}

            样本来源：
            {{string.Join(Environment.NewLine, sourceLines)}}

            规则：
            1. 只能依据样本来源整理，不能补充来源中没有的新事实。
            2. 如果来源不足以支持一条正式知识卡，或者问题只是寒暄、缩写、内部关键词、单个型号词，请返回空 question 和空 answerDraft。
            3. 如果是问答卡，question 必须写成用户会问的完整自然语言问题。
            4. 如果是资料卡，answerDraft 可以保留教程/下载/安装说明，但不能包装成未被来源支持的售后承诺。
            5. answerDraft 要写成客服可直接参考的标准答复，但不要过度承诺。
            6. tags 只保留 2 到 5 个高价值中文标签。
            7. riskLevel 只返回 low、medium 或 high。
            8. evidenceSummary 用一句中文概括来源和证据。
            9. confidence 返回 0 到 1 的数字；证据不足时返回 0。
            10. 只输出 JSON，不要输出 markdown 或解释。
            """;
    }

    private static string ExtractResponseText(string responseContent)
    {
        try
        {
            using var document = JsonDocument.Parse(responseContent);
            var root = document.RootElement;
            if (root.TryGetProperty("output_text", out var outputTextElement) &&
                outputTextElement.ValueKind == JsonValueKind.String)
            {
                return outputTextElement.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("output", out var outputElement) &&
                outputElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var contentElement) ||
                        contentElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var contentItem in contentElement.EnumerateArray())
                    {
                        if (contentItem.TryGetProperty("text", out var textElement) &&
                            textElement.ValueKind == JsonValueKind.String)
                        {
                            var text = textElement.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return responseContent;
    }

    private static string TrimForPrompt(string value, int maxLength)
    {
        var cleaned = CleanText(value);
        if (cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        return $"{cleaned[..maxLength]}...";
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Replace("\r", " ").Replace("\n", " ");
    }

    private static List<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        return (tags ?? [])
            .Select(CleanText)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static string NormalizeRiskLevel(string? riskLevel)
    {
        var normalized = CleanText(riskLevel).ToLowerInvariant();
        return normalized switch
        {
            "low" or "medium" or "high" => normalized,
            _ => string.Empty
        };
    }

    private static double? NormalizeConfidence(double? confidence)
    {
        if (!confidence.HasValue)
        {
            return null;
        }

        return Math.Clamp(confidence.Value, 0d, 1d);
    }

    private sealed class OpenAiKnowledgeSynthesisPayload
    {
        public string? Question { get; set; }
        public string? AnswerDraft { get; set; }
        public List<string>? Tags { get; set; }
        public string? RiskLevel { get; set; }
        public string? EvidenceSummary { get; set; }
        public double? Confidence { get; set; }
    }
}
