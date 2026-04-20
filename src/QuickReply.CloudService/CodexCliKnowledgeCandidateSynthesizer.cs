using System.Text.Json;
using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

public sealed class CodexCliKnowledgeCandidateSynthesizer : IAiKnowledgeCandidateSynthesizer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string OutputSchemaJson =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "question": { "type": "string" },
            "answerDraft": { "type": "string" },
            "tags": {
              "type": "array",
              "items": { "type": "string" }
            },
            "riskLevel": { "type": "string" },
            "evidenceSummary": { "type": "string" },
            "confidence": { "type": "number" }
          },
          "required": ["question", "answerDraft", "tags", "riskLevel", "evidenceSummary", "confidence"],
          "additionalProperties": false
        }
        """;

    private readonly ICodexCliCommandRunner _commandRunner;
    private readonly CodexCliAuthService _authService;
    private readonly IOptions<KnowledgeCloudOptions> _options;

    public CodexCliKnowledgeCandidateSynthesizer(
        ICodexCliCommandRunner commandRunner,
        CodexCliAuthService authService,
        IOptions<KnowledgeCloudOptions> options)
    {
        _commandRunner = commandRunner;
        _authService = authService;
        _options = options;
    }

    public async Task<AiKnowledgeSynthesisResult?> TrySynthesizeAsync(
        KnowledgeCandidateRecord candidate,
        IReadOnlyList<KnowledgeCandidateSourceRecord> sources,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        if (!options.EnableAiSynthesis || string.IsNullOrWhiteSpace(options.CodexCliModelName))
        {
            return null;
        }

        var status = await _authService.GetStatusAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!status.CliAvailable || !status.LoggedIn)
        {
            return null;
        }

        var schemaPath = Path.Combine(Path.GetTempPath(), $"quickreply-codex-schema-{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"quickreply-codex-output-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(schemaPath, OutputSchemaJson, cancellationToken).ConfigureAwait(false);
            var prompt = BuildPrompt(candidate, sources);
            var arguments = BuildArguments(options, schemaPath, outputPath);
            var result = await _commandRunner.RunAsync(
                arguments,
                standardInput: prompt,
                timeoutMs: Math.Max(10000, options.CodexCliRequestTimeoutMs),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
            {
                throw new TimeoutException("Codex CLI synthesis timed out.");
            }

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(MergeOutput(result));
            }

            if (!File.Exists(outputPath))
            {
                return null;
            }

            var responseContent = await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseContent))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<CodexCliKnowledgeSynthesisPayload>(responseContent, SerializerOptions);
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
                ProviderComment = $"synthesized_by_codex_cli:{options.CodexCliModelName}"
            };
        }
        finally
        {
            SafeDelete(schemaPath);
            SafeDelete(outputPath);
        }
    }

    private static List<string> BuildArguments(KnowledgeCloudOptions options, string schemaPath, string outputPath)
    {
        var arguments = new List<string>
        {
            "exec",
            "--skip-git-repo-check",
            "--ephemeral",
            "--sandbox",
            "read-only",
            "--color",
            "never",
            "--output-schema",
            schemaPath,
            "--output-last-message",
            outputPath,
            "--model",
            options.CodexCliModelName.Trim(),
            "-c",
            $"model_reasoning_effort=\"{EscapeTomlValue(options.CodexCliReasoningEffort)}\"",
            "-"
        };

        return arguments;
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
            你是客服知识库整理助手。请根据用户问题、历史回复和来源摘要，只输出一个 JSON 对象，并且必须满足外部提供的 JSON Schema。

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
            10. 不要输出 markdown、解释或代码块，只输出 JSON。
            """;
    }

    private static string EscapeTomlValue(string? value)
    {
        return CleanText(value).Replace("\\", "\\\\").Replace("\"", "\\\"");
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

    private static string MergeOutput(CodexCliCommandResult result)
    {
        return string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class CodexCliKnowledgeSynthesisPayload
    {
        public string? Question { get; set; }
        public string? AnswerDraft { get; set; }
        public List<string>? Tags { get; set; }
        public string? RiskLevel { get; set; }
        public string? EvidenceSummary { get; set; }
        public double? Confidence { get; set; }
    }
}
