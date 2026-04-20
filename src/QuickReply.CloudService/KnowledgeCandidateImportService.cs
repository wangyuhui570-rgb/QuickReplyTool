using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace QuickReply.CloudService;

public sealed class KnowledgeCandidateImportService
{
    private readonly KnowledgeCloudStore _store;
    private readonly IWebHostEnvironment _environment;

    public KnowledgeCandidateImportService(KnowledgeCloudStore store, IWebHostEnvironment environment)
    {
        _store = store;
        _environment = environment;
    }

    public async Task<KnowledgeImportRunResponse> ImportFromPathAsync(
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var resolvedInputPath = ResolveInputPath(inputPath);
        if (!File.Exists(resolvedInputPath))
        {
            throw new FileNotFoundException("未找到日志输入文件。", resolvedInputPath);
        }

        var createdAt = DateTimeOffset.Now;
        var jobRunId = Guid.NewGuid();

        using var prepared = await PrepareDatabaseAsync(resolvedInputPath, cancellationToken);
        await using var connection = new SqliteConnection($"Data Source={prepared.DatabasePath}");
        await connection.OpenAsync(cancellationToken);

        var conversationSamples = await LoadConversationSamplesAsync(connection, cancellationToken);
        var correctionSources = await LoadCorrectionSourcesAsync(connection, cancellationToken);
        var recommendationSamples = await LoadRecommendationSamplesAsync(connection, cancellationToken);

        var groupedCorrectionSources = correctionSources
            .GroupBy(source => KnowledgeCandidateQualityRules.BuildClusterKey(
                source.Platform,
                source.ProductKey,
                source.Question,
                KnowledgeCandidateKinds.Qa))
            .ToDictionary(group => group.Key, group => group.ToArray());

        var allAnswerBearingSamples = conversationSamples
            .Concat(recommendationSamples)
            .ToArray();

        var clusters = allAnswerBearingSamples
            .GroupBy(sample => sample.ClusterKey)
            .ToArray();

        var candidateCount = 0;
        foreach (var cluster in clusters)
        {
            var samples = cluster.ToArray();
            if (samples.Length == 0)
            {
                continue;
            }

            var primary = samples
                .OrderByDescending(sample => sample.Confidence)
                .ThenByDescending(sample => sample.OccurredAt)
                .First();
            if (KnowledgeCandidateQualityRules.IsLowValueAnswer(primary.Answer))
            {
                continue;
            }

            groupedCorrectionSources.TryGetValue(cluster.Key, out var extraCorrectionSources);
            var allSources = samples
                .Select(sample => new KnowledgeCandidateSourceRecord
                {
                    Id = DeterministicGuid($"{cluster.Key}|{sample.SourceType}|{sample.SourceId}"),
                    CandidateId = DeterministicGuid(cluster.Key),
                    SourceType = sample.SourceType,
                    SourceId = sample.SourceId,
                    Excerpt = $"{sample.Question} => {sample.Answer}".Trim(),
                    CreatedAt = sample.OccurredAt
                })
                .Concat((extraCorrectionSources ?? []).Select(source => new KnowledgeCandidateSourceRecord
                {
                    Id = DeterministicGuid($"{cluster.Key}|correction|{source.SourceId}"),
                    CandidateId = DeterministicGuid(cluster.Key),
                    SourceType = "correction",
                    SourceId = source.SourceId,
                    Excerpt = source.Question,
                    CreatedAt = source.OccurredAt
                }))
                .ToArray();

            var averageConfidence = samples.Average(sample => sample.Confidence);
            var fromKeywordFallback = samples.Any(sample => string.Equals(sample.QuestionSource, "keyword", StringComparison.OrdinalIgnoreCase));
            if (!KnowledgeCandidateQualityRules.ShouldKeepImportedCandidate(
                    primary.CandidateKind,
                    primary.Question,
                    primary.Answer,
                    allSources.Length,
                    averageConfidence,
                    fromKeywordFallback))
            {
                continue;
            }

            var candidateId = DeterministicGuid(cluster.Key);
            var createdRange = samples.Min(sample => sample.OccurredAt);
            var updatedRange = samples.Max(sample => sample.OccurredAt);
            var normalizedKind = KnowledgeCandidateQualityRules.NormalizeCandidateKind(primary.CandidateKind);

            var candidate = new KnowledgeCandidateRecord
            {
                Id = candidateId,
                Status = "draft",
                CandidateKind = normalizedKind,
                Platform = primary.Platform,
                ProductKey = primary.ProductKey,
                QuestionNormalized = primary.Question,
                AnswerDraft = primary.Answer,
                AnswerFinal = string.Empty,
                Tags = KnowledgeCandidateQualityRules.BuildTags(normalizedKind, primary.Platform, primary.Question, primary.Answer),
                Confidence = Math.Round(averageConfidence, 3),
                RiskLevel = KnowledgeCandidateQualityRules.InferRiskLevel(primary.Question, primary.Answer),
                SourceCount = allSources.Length,
                ClusterKey = cluster.Key,
                EvidenceSummary = string.Equals(normalizedKind, KnowledgeCandidateKinds.Resource, StringComparison.OrdinalIgnoreCase)
                    ? $"{samples.Length} 条资料/教程类样本，{(extraCorrectionSources?.Length ?? 0)} 条纠错补充来源。"
                    : $"{samples.Length} 条问答类样本，{(extraCorrectionSources?.Length ?? 0)} 条纠错补充来源。",
                CreatedAt = createdRange,
                UpdatedAt = updatedRange
            };

            await _store.UpsertKnowledgeCandidateAsync(candidate, allSources, cancellationToken);
            candidateCount++;
        }

        var sampleCount = allAnswerBearingSamples.Length;
        var avgConfidence = sampleCount == 0 ? 0d : Math.Round(allAnswerBearingSamples.Average(sample => sample.Confidence), 3);
        await _store.InsertKnowledgeJobRunAsync(new KnowledgeJobRunRecord
        {
            Id = jobRunId,
            SampleCount = sampleCount,
            CandidateCount = candidateCount,
            PublishCount = 0,
            RejectCount = 0,
            AvgConfidence = avgConfidence,
            FailedClusterCount = 0,
            FailedSynthesizeCount = 0,
            CreatedAt = createdAt
        }, cancellationToken);

        return new KnowledgeImportRunResponse
        {
            JobRunId = jobRunId,
            InputPath = resolvedInputPath,
            SampleCount = sampleCount,
            CandidateCount = candidateCount,
            PublishCount = 0,
            RejectCount = 0,
            AvgConfidence = avgConfidence,
            FailedClusterCount = 0,
            FailedSynthesizeCount = 0,
            ImportedCorrectionSourceCount = correctionSources.Count,
            ImportedRecommendationSourceCount = recommendationSamples.Count,
            Message = candidateCount == 0
                ? "没有提取到满足条件的候选知识，请优先提升客户消息识别质量或补充纠错/采纳样本。"
                : $"已从日志中整理出 {candidateCount} 条候选知识。",
            CreatedAt = createdAt
        };
    }

    private async Task<IReadOnlyList<ImportKnowledgeSample>> LoadConversationSamplesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   occurred_at,
                   platform_hint,
                   search_keyword,
                   recognized_customer_text,
                   captured_text,
                   recognition_confidence,
                   recognized_product_id,
                   recognized_product_name,
                   payload_text,
                   result_success
            FROM conversation_logs;
            """;

        var samples = new List<ImportKnowledgeSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetInt32(10) == 0)
            {
                continue;
            }

            var answer = KnowledgeCandidateQualityRules.NormalizeAnswerText(reader.GetString(9));
            if (KnowledgeCandidateQualityRules.IsLowValueAnswer(answer))
            {
                continue;
            }

            var confidence = reader.GetDouble(6);
            var extracted = ExtractReliableQuestion(
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(3),
                confidence,
                answer);
            if (extracted is null)
            {
                continue;
            }

            var platform = KnowledgeCandidateQualityRules.NormalizePlatform(reader.GetString(2));
            var productKey = NormalizeProductKey(reader.GetString(7), reader.GetString(8));
            samples.Add(new ImportKnowledgeSample(
                SourceType: "conversation",
                SourceId: reader.GetString(0),
                OccurredAt: ParseDateTimeOffset(reader.GetString(1)),
                Platform: platform,
                ProductKey: productKey,
                Question: extracted.Question,
                Answer: answer,
                Confidence: confidence,
                QuestionSource: extracted.Source,
                CandidateKind: extracted.CandidateKind,
                ClusterKey: KnowledgeCandidateQualityRules.BuildClusterKey(platform, productKey, extracted.Question, extracted.CandidateKind)));
        }

        return samples;
    }

    private async Task<IReadOnlyList<ImportKnowledgeSample>> LoadRecommendationSamplesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   occurred_at,
                   platform_hint,
                   search_keyword,
                   recognized_customer_text,
                   confidence,
                    actual_payload_text
            FROM recommendation_events
            WHERE event_type IN ('accepted', 'sent');
            """;

        var samples = new List<ImportKnowledgeSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var answer = KnowledgeCandidateQualityRules.NormalizeAnswerText(reader.GetString(6));
            if (KnowledgeCandidateQualityRules.IsLowValueAnswer(answer))
            {
                continue;
            }

            var confidence = reader.GetDouble(5);
            var extracted = ExtractReliableQuestion(reader.GetString(4), string.Empty, reader.GetString(3), confidence, answer);
            if (extracted is null)
            {
                continue;
            }

            var platform = KnowledgeCandidateQualityRules.NormalizePlatform(reader.GetString(2));
            samples.Add(new ImportKnowledgeSample(
                SourceType: "recommendation_accept",
                SourceId: reader.GetString(0),
                OccurredAt: ParseDateTimeOffset(reader.GetString(1)),
                Platform: platform,
                ProductKey: string.Empty,
                Question: extracted.Question,
                Answer: answer,
                Confidence: confidence,
                QuestionSource: extracted.Source,
                CandidateKind: extracted.CandidateKind,
                ClusterKey: KnowledgeCandidateQualityRules.BuildClusterKey(platform, string.Empty, extracted.Question, extracted.CandidateKind)));
        }

        return samples;
    }

    private async Task<IReadOnlyList<ImportCorrectionSource>> LoadCorrectionSourcesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   occurred_at,
                   platform_hint,
                   corrected_customer_text,
                   corrected_product_id,
                   corrected_product_name
            FROM recognition_corrections;
            """;

        var sources = new List<ImportCorrectionSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var question = KnowledgeCandidateQualityRules.NormalizeQuestionText(reader.GetString(3));
            if (KnowledgeCandidateQualityRules.IsLowValueQuestion(question) ||
                !KnowledgeCandidateQualityRules.LooksLikeCompleteQaQuestion(question))
            {
                continue;
            }

            sources.Add(new ImportCorrectionSource(
                SourceId: reader.GetString(0),
                OccurredAt: ParseDateTimeOffset(reader.GetString(1)),
                Platform: KnowledgeCandidateQualityRules.NormalizePlatform(reader.GetString(2)),
                ProductKey: NormalizeProductKey(reader.GetString(4), reader.GetString(5)),
                Question: question));
        }

        return sources;
    }

    private async Task<PreparedConversationLogDatabase> PrepareDatabaseAsync(string inputPath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(inputPath);
        if (extension.Equals(".db", StringComparison.OrdinalIgnoreCase))
        {
            return new PreparedConversationLogDatabase(inputPath, tempRoot: null);
        }

        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("目前只支持导入 logs.zip 或 conversation-logs.db。");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "QuickReplyCloudImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        await Task.Run(() => ZipFile.ExtractToDirectory(inputPath, tempRoot), cancellationToken);

        var dbPath = Path.Combine(tempRoot, "logs", "conversation-logs.db");
        if (!File.Exists(dbPath))
        {
            Directory.Delete(tempRoot, recursive: true);
            throw new FileNotFoundException("logs.zip 中未找到 logs/conversation-logs.db。", dbPath);
        }

        return new PreparedConversationLogDatabase(dbPath, tempRoot);
    }

    private string ResolveInputPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return Path.Combine(_environment.ContentRootPath, "..", "..", "logs.zip");
        }

        if (Path.IsPathRooted(inputPath))
        {
            return inputPath;
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(_environment.ContentRootPath, inputPath)),
            Path.GetFullPath(Path.Combine(_environment.ContentRootPath, "..", "..", inputPath))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static ExtractedQuestion? ExtractReliableQuestion(
        string recognizedCustomerText,
        string capturedText,
        string searchKeyword,
        double confidence,
        string answer)
    {
        var primary = KnowledgeCandidateQualityRules.NormalizeQuestionText(recognizedCustomerText);
        if (!string.IsNullOrWhiteSpace(primary) && confidence >= 0.45d && !KnowledgeCandidateQualityRules.IsLowValueQuestion(primary))
        {
            return new ExtractedQuestion(
                primary,
                Source: "recognized",
                CandidateKind: KnowledgeCandidateQualityRules.ClassifyCandidateKind(primary, answer));
        }

        var fallback = KnowledgeCandidateQualityRules.NormalizeQuestionText(capturedText);
        if (!string.IsNullOrWhiteSpace(fallback) && confidence >= 0.55d && !KnowledgeCandidateQualityRules.IsLowValueQuestion(fallback))
        {
            return new ExtractedQuestion(
                fallback,
                Source: "captured",
                CandidateKind: KnowledgeCandidateQualityRules.ClassifyCandidateKind(fallback, answer));
        }

        var keyword = KnowledgeCandidateQualityRules.NormalizeQuestionText(searchKeyword);
        if (KnowledgeCandidateQualityRules.CanUseSearchKeywordFallback(keyword))
        {
            return new ExtractedQuestion(
                keyword,
                Source: "keyword",
                CandidateKind: KnowledgeCandidateQualityRules.ClassifyCandidateKind(keyword, answer));
        }

        return null;
    }

    private static string NormalizeProductKey(string productId, string productName)
    {
        var chosen = !string.IsNullOrWhiteSpace(productId) ? productId : productName;
        return string.IsNullOrWhiteSpace(chosen) ? string.Empty : chosen.Trim();
    }

    private static Guid DeterministicGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }

    private sealed record ImportKnowledgeSample(
        string SourceType,
        string SourceId,
        DateTimeOffset OccurredAt,
        string Platform,
        string ProductKey,
        string Question,
        string Answer,
        double Confidence,
        string QuestionSource,
        string CandidateKind,
        string ClusterKey);

    private sealed record ImportCorrectionSource(
        string SourceId,
        DateTimeOffset OccurredAt,
        string Platform,
        string ProductKey,
        string Question);

    private sealed record ExtractedQuestion(
        string Question,
        string Source,
        string CandidateKind);

    private sealed class PreparedConversationLogDatabase : IDisposable
    {
        public PreparedConversationLogDatabase(string databasePath, string? tempRoot)
        {
            DatabasePath = databasePath;
            _tempRoot = tempRoot;
        }

        private readonly string? _tempRoot;
        public string DatabasePath { get; }

        public void Dispose()
        {
            if (!string.IsNullOrWhiteSpace(_tempRoot) && Directory.Exists(_tempRoot))
            {
                try
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }
}
