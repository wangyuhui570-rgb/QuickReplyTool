using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

return await MainAsync(args).ConfigureAwait(false);

static async Task<int> MainAsync(string[] args)
{
    var options = ReplayEvalOptions.Parse(args);
    return options.RunLogHealthCheck
        ? await RunLogHealthCheckAsync(options).ConfigureAwait(false)
        : await RunReplayEvaluationAsync(options).ConfigureAwait(false);
}

static async Task<int> RunReplayEvaluationAsync(ReplayEvalOptions options)
{
    var logDatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickReplyTool",
        "logs",
        "conversation-logs.db");

    if (!File.Exists(logDatabasePath))
    {
        Console.Error.WriteLine($"未找到日志数据库：{logDatabasePath}");
        return 1;
    }

    var outputDirectory = options.OutputDirectory ?? Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        "replay-eval",
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(outputDirectory);

    await using var queryService = new ReplayRecommendationQueryService(logDatabasePath);
    var replyGenerator = new RetrievalBackedReplyGenerator();
    var replayStore = new ReplaySampleStore(logDatabasePath);

    var corrections = await replayStore.LoadCorrectionsAsync().ConfigureAwait(false);
    var samples = await replayStore.LoadSamplesAsync(options.SampleLimit, options.PlatformHint, options.SampleSource).ConfigureAwait(false);
    if (samples.Count == 0)
    {
        var emptySummaryPath = Path.Combine(outputDirectory, "summary.txt");
        await File.WriteAllTextAsync(emptySummaryPath, "没有找到符合条件的历史日志样本。").ConfigureAwait(false);
        Console.WriteLine(emptySummaryPath);
        return 0;
    }

    var evaluationRows = new List<ReplayEvaluationRow>(samples.Count);

    foreach (var sample in samples)
    {
        var correction = sample.CorrectionAlreadyApplied
            ? null
            : corrections.TryResolve(sample);
        var snapshot = sample.ToSnapshot(correction);
        var scopedUsageStats = await replayStore.LoadPhraseUsageStatsAsync(sample.OccurredAt, sample.Id).ConfigureAwait(false);
        var historicalPhrases = await replayStore.LoadHistoricalPhrasesAsync(sample.OccurredAt).ConfigureAwait(false);
        var phraseCatalog = new InMemoryRecommendationPhraseCatalog(historicalPhrases, scopedUsageStats);
        var phraseStillExists = phraseCatalog.GetAllPhrases().Any(phrase => phrase.Id == sample.ActualPhraseId);

        CandidateRetrievalResult retrievalResult = new();
        IReadOnlyList<GeneratedReplyCandidate> generatedReplies = [];

        if (phraseStillExists)
        {
            var candidateRetriever = new HistoricalCandidateRetriever(
                queryService,
                phraseCatalog,
                new InMemoryPublishedKnowledgeCardCatalog([]));
            var scopedRequest = new RecommendationRequest
            {
                DisplayCount = 5,
                MinimumSampleCount = options.MinimumSampleCount,
                GeneratedReplyCount = options.GeneratedReplyCount,
                QueryScope = new RecommendationQueryScope
                {
                    MaxOccurredAt = sample.OccurredAt,
                    ExcludedConversationLogId = sample.Id
                }
            };

            retrievalResult = await candidateRetriever
                .RetrieveCandidatesAsync(snapshot, scopedRequest)
                .ConfigureAwait(false);
            generatedReplies = await replyGenerator
                .GenerateCandidatesAsync(snapshot, retrievalResult, scopedRequest)
                .ConfigureAwait(false);
        }

        var actualRank = retrievalResult.Suggestions
            .Select((suggestion, index) => new { suggestion.Phrase.Id, Rank = index + 1 })
            .FirstOrDefault(entry => entry.Id == sample.ActualPhraseId)
            ?.Rank;
        var nonFallbackGeneratedReplies = generatedReplies
            .Where(candidate => !string.Equals(candidate.Source, "retrieval_fallback", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var generatedHit = nonFallbackGeneratedReplies.Any(candidate =>
            candidate.BackingPhraseId == sample.ActualPhraseId ||
            (!string.IsNullOrWhiteSpace(sample.ActualPayloadText) &&
             string.Equals(candidate.Text, sample.ActualPayloadText, StringComparison.Ordinal)));

        evaluationRows.Add(new ReplayEvaluationRow
        {
            SampleId = sample.Id,
            SampleSource = sample.SampleSource,
            PlatformHint = sample.PlatformHint,
            OccurredAt = sample.OccurredAt,
            CorrectedApplied = sample.CorrectionAlreadyApplied || correction is not null,
            PhraseStillExists = phraseStillExists,
            ActualPhraseId = sample.ActualPhraseId,
            ActualPhraseTitle = sample.ActualPhraseTitle,
            SearchText = snapshot.RecognizedCustomerText,
            ContextText = snapshot.RecognizedContextText,
            ProductId = snapshot.RecognizedProductId,
            ProductName = snapshot.RecognizedProductName,
            ActualRank = actualRank,
            GeneratedHit = generatedHit,
            HasGeneratedCandidates = nonFallbackGeneratedReplies.Count > 0,
            CandidateCount = retrievalResult.Suggestions.Count,
            TopSuggestions = retrievalResult.Suggestions
                .Take(5)
                .Select(suggestion => new ReplaySuggestionRow
                {
                    PhraseId = suggestion.Phrase.Id,
                    PhraseTitle = suggestion.Phrase.Title,
                    Confidence = suggestion.Confidence,
                    MatchedKeyword = suggestion.MatchedKeyword
                })
                .ToList()
        });
    }

    var summaryText = ReplayEvaluationReportWriter.BuildSummary(evaluationRows, options);
    var summaryPath = Path.Combine(outputDirectory, "summary.txt");
    await File.WriteAllTextAsync(summaryPath, summaryText).ConfigureAwait(false);

    var allSamplesPath = Path.Combine(outputDirectory, "samples.jsonl");
    await ReplayEvaluationReportWriter.WriteJsonLinesAsync(allSamplesPath, evaluationRows).ConfigureAwait(false);

    var missesPath = Path.Combine(outputDirectory, "misses.jsonl");
    await ReplayEvaluationReportWriter.WriteJsonLinesAsync(
        missesPath,
        ReplayEvaluationReportWriter.SelectMissRows(evaluationRows)).ConfigureAwait(false);

    var successfulMissesPath = Path.Combine(outputDirectory, "successful-misses.jsonl");
    await ReplayEvaluationReportWriter.WriteJsonLinesAsync(
        successfulMissesPath,
        ReplayEvaluationReportWriter.SelectMissRows(evaluationRows, ReplaySampleSources.SuccessfulSend)).ConfigureAwait(false);

    var failedMissesPath = Path.Combine(outputDirectory, "failed-misses.jsonl");
    await ReplayEvaluationReportWriter.WriteJsonLinesAsync(
        failedMissesPath,
        ReplayEvaluationReportWriter.SelectMissRows(evaluationRows, ReplaySampleSources.FailedSend)).ConfigureAwait(false);

    var correctionBackfillMissesPath = Path.Combine(outputDirectory, "correction-backfill-misses.jsonl");
    await ReplayEvaluationReportWriter.WriteJsonLinesAsync(
        correctionBackfillMissesPath,
        ReplayEvaluationReportWriter.SelectMissRows(evaluationRows, ReplaySampleSources.CorrectionBackfill)).ConfigureAwait(false);

    var successfulSamplesPath = Path.Combine(outputDirectory, "successful-samples.jsonl");
    await ReplayEvaluationReportWriter.WriteJsonLinesAsync(
        successfulSamplesPath,
        evaluationRows.Where(row => string.Equals(row.SampleSource, ReplaySampleSources.SuccessfulSend, StringComparison.Ordinal))).ConfigureAwait(false);

    var failedSamplesPath = Path.Combine(outputDirectory, "failed-samples.jsonl");
    await ReplayEvaluationReportWriter.WriteJsonLinesAsync(
        failedSamplesPath,
        evaluationRows.Where(row => string.Equals(row.SampleSource, ReplaySampleSources.FailedSend, StringComparison.Ordinal))).ConfigureAwait(false);

    var correctionBackfillSamplesPath = Path.Combine(outputDirectory, "correction-backfill-samples.jsonl");
    await ReplayEvaluationReportWriter.WriteJsonLinesAsync(
        correctionBackfillSamplesPath,
        evaluationRows.Where(row => string.Equals(row.SampleSource, ReplaySampleSources.CorrectionBackfill, StringComparison.Ordinal))).ConfigureAwait(false);

    Console.WriteLine(summaryPath);
    Console.WriteLine(allSamplesPath);
    Console.WriteLine(missesPath);
    Console.WriteLine(successfulMissesPath);
    Console.WriteLine(failedMissesPath);
    Console.WriteLine(correctionBackfillMissesPath);
    Console.WriteLine(successfulSamplesPath);
    Console.WriteLine(failedSamplesPath);
    Console.WriteLine(correctionBackfillSamplesPath);
    return 0;
}

static async Task<int> RunLogHealthCheckAsync(ReplayEvalOptions options)
{
    var logRootPath = options.LogRootPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuickReplyTool",
        "logs");

    var outputDirectory = options.OutputDirectory ?? Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        "log-health",
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(outputDirectory);

    var analyzer = new LogHealthAnalyzer();
    var report = await analyzer.AnalyzeAsync(logRootPath).ConfigureAwait(false);

    var summaryPath = Path.Combine(outputDirectory, "summary.md");
    await File.WriteAllTextAsync(summaryPath, LogHealthReportWriter.BuildSummary(report)).ConfigureAwait(false);

    var jsonPath = Path.Combine(outputDirectory, "report.json");
    await File.WriteAllTextAsync(
        jsonPath,
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);

    Console.WriteLine(summaryPath);
    Console.WriteLine(jsonPath);

    if (report.HasBlockingIssues)
    {
        Console.Error.WriteLine("日志健康检查未通过。请查看 summary.md 和 report.json。");
        return 2;
    }

    return 0;
}

internal static class ReplaySampleSources
{
    public const string SuccessfulSend = "successful_send";
    public const string FailedSend = "failed_send";
    public const string CorrectionBackfill = "correction_backfill";
}

internal sealed class ReplaySampleStore
{
    private readonly string _logDatabasePath;

    public ReplaySampleStore(string logDatabasePath)
    {
        _logDatabasePath = logDatabasePath;
    }

    public async Task<RecognitionCorrectionIndex> LoadCorrectionsAsync()
    {
        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT occurred_at,
                   platform_hint,
                   window_title,
                   source_customer_text,
                   source_context_text,
                   source_product_id,
                   source_product_name,
                   source_messages_json,
                   corrected_customer_text,
                   corrected_context_text,
                   corrected_product_id,
                   corrected_product_name,
                   corrected_messages_json
            FROM recognition_corrections
            WHERE corrected_customer_text <> ''
               OR corrected_context_text <> ''
               OR corrected_product_id <> ''
               OR corrected_product_name <> ''
               OR corrected_messages_json <> ''
            ORDER BY occurred_at DESC;
            """;

        var exactEntries = new Dictionary<string, StoredRecognitionCorrection>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var correction = new StoredRecognitionCorrection
            {
                SourceCustomerText = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                SourceContextText = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                SourceProductId = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                SourceProductName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                SourceMessagesJson = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                CorrectedCustomerText = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                CorrectedContextText = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                CorrectedProductId = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                CorrectedProductName = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                CorrectedMessagesJson = reader.IsDBNull(12) ? string.Empty : reader.GetString(12)
            };

            var key = BuildCorrectionKey(
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                correction.SourceCustomerText,
                correction.SourceContextText,
                correction.SourceProductId,
                correction.SourceProductName,
                correction.SourceMessagesJson);
            exactEntries.TryAdd(key, correction);
        }

        return new RecognitionCorrectionIndex(exactEntries);
    }

    public async Task<IReadOnlyList<ReplaySample>> LoadSamplesAsync(int sampleLimit, string? platformHint, string? sampleSource = null)
    {
        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
        var samples = new List<ReplaySample>(sampleLimit * 2);
        samples.AddRange(await LoadConversationSamplesAsync(connection, sampleLimit, platformHint).ConfigureAwait(false));
        samples.AddRange(await LoadCorrectionBackfillSamplesAsync(connection, sampleLimit, platformHint).ConfigureAwait(false));
        var normalizedSampleSource = NormalizeSampleSource(sampleSource);

        return samples
            .Where(sample => string.IsNullOrWhiteSpace(normalizedSampleSource) ||
                             string.Equals(sample.SampleSource, normalizedSampleSource, StringComparison.Ordinal))
            .OrderByDescending(sample => sample.OccurredAt)
            .Take(Math.Max(1, sampleLimit))
            .ToArray();
    }

    private static async Task<IReadOnlyList<ReplaySample>> LoadConversationSamplesAsync(
        SqliteConnection connection,
        int sampleLimit,
        string? platformHint)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   occurred_at,
                   platform_hint,
                   target_window_title,
                   target_window_class_name,
                   target_process_name,
                   target_process_path,
                   recognition_status,
                   recognition_method,
                   recognition_confidence,
                   recognized_customer_text,
                   recognized_context_text,
                   recognized_product_id,
                   recognized_product_name,
                   recognized_messages_json,
                   phrase_id,
                   phrase_title,
                   payload_text,
                   result_success
            FROM conversation_logs
            WHERE payload_type = 'phrase'
              AND action_mode IN ('Send', 'Input')
              AND phrase_id <> ''
              AND (
                    recognized_customer_text <> ''
                 OR recognized_context_text <> ''
                 OR recognized_product_id <> ''
                 OR recognized_product_name <> ''
              )
              AND ($platformHint = '' OR platform_hint = $platformHint)
            ORDER BY occurred_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$platformHint", string.IsNullOrWhiteSpace(platformHint) ? string.Empty : platformHint.Trim());
        command.Parameters.AddWithValue("$limit", Math.Max(1, sampleLimit));

        var samples = new List<ReplaySample>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(15), out var phraseId))
            {
                continue;
            }

            var messagesJson = reader.IsDBNull(14) ? string.Empty : reader.GetString(14);
            samples.Add(new ReplaySample
            {
                Id = reader.GetString(0),
                OccurredAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                PlatformHint = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                WindowTitle = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                WindowClassName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                ProcessName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                ProcessPath = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                RecognitionStatus = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                RecognitionMethod = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                RecognitionConfidence = reader.IsDBNull(9) ? 0d : reader.GetDouble(9),
                RecognizedCustomerText = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                RecognizedContextText = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                ProductId = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                ProductName = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                MessagesJson = messagesJson,
                Messages = ParseMessages(messagesJson),
                ActualPhraseId = phraseId,
                ActualPhraseTitle = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
                ActualPayloadText = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
                SampleSource = reader.IsDBNull(18) || reader.GetInt64(18) != 0
                    ? ReplaySampleSources.SuccessfulSend
                    : ReplaySampleSources.FailedSend
            });
        }

        return samples;
    }

    private static async Task<IReadOnlyList<ReplaySample>> LoadCorrectionBackfillSamplesAsync(
        SqliteConnection connection,
        int sampleLimit,
        string? platformHint)
    {
        var correctionColumns = await LoadTableColumnsAsync(connection, "recognition_corrections").ConfigureAwait(false);
        var recommendationColumns = await LoadTableColumnsAsync(connection, "recommendation_events").ConfigureAwait(false);
        if (correctionColumns.Count == 0 || recommendationColumns.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rc.id,
                   rc.occurred_at,
                   rc.platform_hint,
                   rc.window_title,
                   rc.source_customer_text,
                   rc.corrected_customer_text,
                   rc.source_context_text,
                   rc.corrected_context_text,
                   rc.source_product_id,
                   rc.corrected_product_id,
                   rc.source_product_name,
                   rc.corrected_product_name,
                   rc.source_messages_json,
                   rc.corrected_messages_json,
                   re.phrase_id,
                   re.phrase_title,
                   re.actual_payload_text
            FROM recognition_corrections rc
            INNER JOIN recommendation_events re
                ON re.recommendation_session_id = rc.recommendation_session_id
            WHERE re.event_type = 'accepted'
              AND re.phrase_id <> ''
              AND (
                    rc.corrected_customer_text <> ''
                 OR rc.corrected_context_text <> ''
                 OR rc.corrected_product_id <> ''
                 OR rc.corrected_product_name <> ''
                 OR rc.corrected_messages_json <> ''
              )
              AND ($platformHint = '' OR rc.platform_hint = $platformHint OR re.platform_hint = $platformHint)
            ORDER BY rc.occurred_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$platformHint", string.IsNullOrWhiteSpace(platformHint) ? string.Empty : platformHint.Trim());
        command.Parameters.AddWithValue("$limit", Math.Max(1, sampleLimit));

        var samples = new List<ReplaySample>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(14), out var phraseId))
            {
                continue;
            }

            var resolvedMessagesJson = ResolveCorrectedValue(
                reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                reader.IsDBNull(13) ? string.Empty : reader.GetString(13));
            samples.Add(new ReplaySample
            {
                Id = $"correction:{reader.GetString(0)}",
                OccurredAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                PlatformHint = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                WindowTitle = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                WindowClassName = string.Empty,
                ProcessName = string.Empty,
                ProcessPath = string.Empty,
                RecognitionStatus = "corrected_backfill",
                RecognitionMethod = "manual_correction",
                RecognitionConfidence = 1d,
                RecognizedCustomerText = ResolveCorrectedValue(
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5)),
                RecognizedContextText = ResolveCorrectedValue(
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.IsDBNull(7) ? string.Empty : reader.GetString(7)),
                ProductId = ResolveCorrectedValue(
                    reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    reader.IsDBNull(9) ? string.Empty : reader.GetString(9)),
                ProductName = ResolveCorrectedValue(
                    reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    reader.IsDBNull(11) ? string.Empty : reader.GetString(11)),
                MessagesJson = resolvedMessagesJson,
                Messages = ParseMessages(resolvedMessagesJson),
                ActualPhraseId = phraseId,
                ActualPhraseTitle = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                ActualPayloadText = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
                SampleSource = ReplaySampleSources.CorrectionBackfill,
                CorrectionAlreadyApplied = true
            });
        }

        return samples;
    }

    public async Task<IReadOnlyDictionary<Guid, PhraseUsageStat>> LoadPhraseUsageStatsAsync(
        DateTimeOffset maxOccurredAt,
        string excludedConversationLogId)
    {
        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT phrase_id,
                   COUNT(1) AS send_count,
                   MAX(occurred_at) AS last_sent_at
            FROM conversation_logs
            WHERE payload_type = 'phrase'
              AND result_success = 1
              AND action_mode IN ('Send', 'Input')
              AND phrase_id <> ''
              AND occurred_at <= $maxOccurredAt
              AND ($excludedConversationLogId = '' OR id <> $excludedConversationLogId)
            GROUP BY phrase_id;
            """;
        command.Parameters.AddWithValue("$maxOccurredAt", maxOccurredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$excludedConversationLogId", excludedConversationLogId ?? string.Empty);

        var stats = new Dictionary<Guid, PhraseUsageStat>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(0), out var phraseId))
            {
                continue;
            }

            stats[phraseId] = new PhraseUsageStat
            {
                PhraseId = phraseId,
                SendCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                LastSentAt = reader.IsDBNull(2)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            };
        }

        return stats;
    }

    public async Task<IReadOnlyList<ReplyPhrase>> LoadHistoricalPhrasesAsync(DateTimeOffset maxOccurredAt)
    {
        await using var connection = await OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT phrase_id,
                   phrase_title,
                   phrase_category_name,
                   payload_text,
                   occurred_at
            FROM conversation_logs
            WHERE payload_type = 'phrase'
              AND phrase_id <> ''
              AND payload_text <> ''
              AND occurred_at <= $maxOccurredAt
            ORDER BY occurred_at DESC;
            """;
        command.Parameters.AddWithValue("$maxOccurredAt", maxOccurredAt.ToString("O", CultureInfo.InvariantCulture));

        var phrases = new Dictionary<Guid, ReplyPhrase>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(0), out var phraseId) ||
                phrases.ContainsKey(phraseId))
            {
                continue;
            }

            var occurredAt = reader.IsDBNull(4)
                ? maxOccurredAt
                : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            phrases[phraseId] = new ReplyPhrase
            {
                Id = phraseId,
                CategoryId = Guid.Empty,
                CategoryName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Content = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                SortOrder = 0,
                CreatedAt = occurredAt,
                UpdatedAt = occurredAt
            };
        }

        return phrases.Values.ToList();
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection($"Data Source={_logDatabasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static IReadOnlyList<ConversationMessageTurn> ParseMessages(string messagesJson)
    {
        if (string.IsNullOrWhiteSpace(messagesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ConversationMessageTurn>>(messagesJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveCorrectedValue(string sourceValue, string correctedValue)
    {
        return !string.IsNullOrWhiteSpace(sourceValue) || !string.IsNullOrWhiteSpace(correctedValue)
            ? correctedValue.Trim()
            : sourceValue.Trim();
    }

    private static string? NormalizeSampleSource(string? sampleSource)
    {
        if (string.IsNullOrWhiteSpace(sampleSource))
        {
            return null;
        }

        return sampleSource.Trim().ToLowerInvariant() switch
        {
            "successful" or "success" or "successful_send" => ReplaySampleSources.SuccessfulSend,
            "failed" or "failure" or "failed_send" => ReplaySampleSources.FailedSend,
            "correction" or "correction_backfill" => ReplaySampleSources.CorrectionBackfill,
            "all" => null,
            _ => sampleSource.Trim().ToLowerInvariant()
        };
    }

    private static async Task<HashSet<string>> LoadTableColumnsAsync(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string BuildCorrectionKey(
        string platformHint,
        string windowTitle,
        string sourceCustomerText,
        string sourceContextText,
        string sourceProductId,
        string sourceProductName,
        string sourceMessagesJson)
    {
        return string.Join(
            "\u001f",
            new[]
            {
                platformHint.Trim(),
                windowTitle.Trim(),
                sourceCustomerText.Trim(),
                sourceContextText.Trim(),
                sourceProductId.Trim(),
                sourceProductName.Trim(),
                sourceMessagesJson.Trim()
            });
    }

    public sealed class RecognitionCorrectionIndex
    {
        private readonly IReadOnlyDictionary<string, StoredRecognitionCorrection> _exactEntries;

        public RecognitionCorrectionIndex(IReadOnlyDictionary<string, StoredRecognitionCorrection> exactEntries)
        {
            _exactEntries = exactEntries;
        }

        public StoredRecognitionCorrection? TryResolve(ReplaySample sample)
        {
            var key = BuildCorrectionKey(
                sample.PlatformHint,
                sample.WindowTitle,
                sample.RecognizedCustomerText,
                sample.RecognizedContextText,
                sample.ProductId,
                sample.ProductName,
                sample.MessagesJson);
            return _exactEntries.TryGetValue(key, out var correction)
                ? correction
                : null;
        }
    }
}

internal sealed class ReplaySample
{
    public string Id { get; init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; init; }

    public string PlatformHint { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string WindowClassName { get; init; } = string.Empty;

    public string ProcessName { get; init; } = string.Empty;

    public string ProcessPath { get; init; } = string.Empty;

    public string RecognitionStatus { get; init; } = string.Empty;

    public string RecognitionMethod { get; init; } = string.Empty;

    public double RecognitionConfidence { get; init; }

    public string RecognizedCustomerText { get; init; } = string.Empty;

    public string RecognizedContextText { get; init; } = string.Empty;

    public string ProductId { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public string MessagesJson { get; init; } = string.Empty;

    public IReadOnlyList<ConversationMessageTurn> Messages { get; init; } = [];

    public Guid ActualPhraseId { get; init; }

    public string ActualPhraseTitle { get; init; } = string.Empty;

    public string ActualPayloadText { get; init; } = string.Empty;

    public string SampleSource { get; init; } = ReplaySampleSources.SuccessfulSend;

    public bool CorrectionAlreadyApplied { get; init; }

    public ConversationRecommendationSnapshot ToSnapshot(StoredRecognitionCorrection? correction)
    {
        var effectiveCustomerText = correction?.ResolveCustomerText(this) ?? RecognizedCustomerText;
        var effectiveContextText = correction?.ResolveContextText(this) ?? RecognizedContextText;
        var effectiveProductId = correction?.ResolveProductId(this) ?? ProductId;
        var effectiveProductName = correction?.ResolveProductName(this) ?? ProductName;
        var effectiveMessages = correction?.ResolveMessages(this) ?? Messages;

        return new ConversationRecommendationSnapshot
        {
            PlatformHint = PlatformHint,
            WindowTitle = WindowTitle,
            WindowClassName = WindowClassName,
            ProcessName = ProcessName,
            ProcessPath = ProcessPath,
            RecognitionStatus = RecognitionStatus,
            RecognitionMethod = RecognitionMethod,
            RecognitionConfidence = RecognitionConfidence,
            RecognizedCustomerText = effectiveCustomerText,
            RecognizedContextText = effectiveContextText,
            Product = new ConversationProductContext
            {
                ProductId = effectiveProductId,
                ProductName = effectiveProductName
            },
            Messages = effectiveMessages,
            Keywords = RecommendationKeywordBuilder.Build(
                effectiveCustomerText,
                effectiveContextText,
                effectiveProductId,
                effectiveProductName)
        };
    }
}

internal sealed class ReplayEvaluationRow
{
    public string SampleId { get; init; } = string.Empty;

    public string SampleSource { get; init; } = ReplaySampleSources.SuccessfulSend;

    public DateTimeOffset OccurredAt { get; init; }

    public string PlatformHint { get; init; } = string.Empty;

    public bool CorrectedApplied { get; init; }

    public bool PhraseStillExists { get; init; }

    public Guid ActualPhraseId { get; init; }

    public string ActualPhraseTitle { get; init; } = string.Empty;

    public string SearchText { get; init; } = string.Empty;

    public string ContextText { get; init; } = string.Empty;

    public string ProductId { get; init; } = string.Empty;

    public string ProductName { get; init; } = string.Empty;

    public int? ActualRank { get; init; }

    public bool GeneratedHit { get; init; }

    public bool HasGeneratedCandidates { get; init; }

    public int CandidateCount { get; init; }

    public IReadOnlyList<ReplaySuggestionRow> TopSuggestions { get; init; } = [];
}

internal sealed class ReplaySuggestionRow
{
    public Guid PhraseId { get; init; }

    public string PhraseTitle { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public string MatchedKeyword { get; init; } = string.Empty;
}

internal sealed class ReplayEvalOptions
{
    public int SampleLimit { get; init; } = 300;

    public int MinimumSampleCount { get; init; } = 8;

    public int GeneratedReplyCount { get; init; } = 3;

    public string? PlatformHint { get; init; }

    public string? SampleSource { get; init; }

    public string? OutputDirectory { get; init; }

    public bool RunLogHealthCheck { get; init; }

    public string? LogRootPath { get; init; }

    public static ReplayEvalOptions Parse(string[] args)
    {
        var sampleLimit = 300;
        var minimumSampleCount = 8;
        var generatedReplyCount = 3;
        string? platformHint = null;
        string? sampleSource = null;
        string? outputDirectory = null;
        var runLogHealthCheck = false;
        string? logRootPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            var nextValue = index + 1 < args.Length ? args[index + 1] : null;
            switch (argument)
            {
                case "--log-health":
                    runLogHealthCheck = true;
                    break;
                case "--limit" when int.TryParse(nextValue, out var parsedLimit):
                    sampleLimit = parsedLimit;
                    index++;
                    break;
                case "--min-samples" when int.TryParse(nextValue, out var parsedMinSamples):
                    minimumSampleCount = parsedMinSamples;
                    index++;
                    break;
                case "--generated" when int.TryParse(nextValue, out var parsedGenerated):
                    generatedReplyCount = parsedGenerated;
                    index++;
                    break;
                case "--platform" when !string.IsNullOrWhiteSpace(nextValue):
                    platformHint = nextValue;
                    index++;
                    break;
                case "--source" when !string.IsNullOrWhiteSpace(nextValue):
                    sampleSource = nextValue;
                    index++;
                    break;
                case "--out" when !string.IsNullOrWhiteSpace(nextValue):
                    outputDirectory = nextValue;
                    index++;
                    break;
                case "--logs-root" when !string.IsNullOrWhiteSpace(nextValue):
                    logRootPath = nextValue;
                    index++;
                    break;
            }
        }

        return new ReplayEvalOptions
        {
            SampleLimit = Math.Max(1, sampleLimit),
            MinimumSampleCount = Math.Max(0, minimumSampleCount),
            GeneratedReplyCount = Math.Max(1, generatedReplyCount),
            PlatformHint = platformHint,
            SampleSource = NormalizeSampleSource(sampleSource),
            OutputDirectory = outputDirectory,
            RunLogHealthCheck = runLogHealthCheck,
            LogRootPath = logRootPath
        };
    }

    private static string? NormalizeSampleSource(string? sampleSource)
    {
        if (string.IsNullOrWhiteSpace(sampleSource))
        {
            return null;
        }

        return sampleSource.Trim().ToLowerInvariant() switch
        {
            "successful" or "success" or "successful_send" => ReplaySampleSources.SuccessfulSend,
            "failed" or "failure" or "failed_send" => ReplaySampleSources.FailedSend,
            "correction" or "correction_backfill" => ReplaySampleSources.CorrectionBackfill,
            "all" => null,
            _ => sampleSource.Trim().ToLowerInvariant()
        };
    }
}

internal sealed class StoredRecognitionCorrection
{
    public string SourceCustomerText { get; init; } = string.Empty;

    public string SourceContextText { get; init; } = string.Empty;

    public string SourceProductId { get; init; } = string.Empty;

    public string SourceProductName { get; init; } = string.Empty;

    public string SourceMessagesJson { get; init; } = string.Empty;

    public string CorrectedCustomerText { get; init; } = string.Empty;

    public string CorrectedContextText { get; init; } = string.Empty;

    public string CorrectedProductId { get; init; } = string.Empty;

    public string CorrectedProductName { get; init; } = string.Empty;

    public string CorrectedMessagesJson { get; init; } = string.Empty;

    public string ResolveCustomerText(ReplaySample sample)
    {
        return HasCustomerOverride()
            ? CorrectedCustomerText.Trim()
            : sample.RecognizedCustomerText;
    }

    public string ResolveContextText(ReplaySample sample)
    {
        return HasContextOverride()
            ? CorrectedContextText.Trim()
            : sample.RecognizedContextText;
    }

    public string ResolveProductId(ReplaySample sample)
    {
        return HasProductIdOverride()
            ? CorrectedProductId.Trim()
            : sample.ProductId;
    }

    public string ResolveProductName(ReplaySample sample)
    {
        return HasProductNameOverride()
            ? CorrectedProductName.Trim()
            : sample.ProductName;
    }

    public IReadOnlyList<ConversationMessageTurn> ResolveMessages(ReplaySample sample)
    {
        if (!HasMessagesOverride())
        {
            return sample.Messages;
        }

        if (string.IsNullOrWhiteSpace(CorrectedMessagesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ConversationMessageTurn>>(CorrectedMessagesJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private bool HasCustomerOverride()
    {
        return !string.IsNullOrWhiteSpace(SourceCustomerText) || !string.IsNullOrWhiteSpace(CorrectedCustomerText);
    }

    private bool HasContextOverride()
    {
        return !string.IsNullOrWhiteSpace(SourceContextText) || !string.IsNullOrWhiteSpace(CorrectedContextText);
    }

    private bool HasProductIdOverride()
    {
        return !string.IsNullOrWhiteSpace(SourceProductId) || !string.IsNullOrWhiteSpace(CorrectedProductId);
    }

    private bool HasProductNameOverride()
    {
        return !string.IsNullOrWhiteSpace(SourceProductName) || !string.IsNullOrWhiteSpace(CorrectedProductName);
    }

    private bool HasMessagesOverride()
    {
        return !string.IsNullOrWhiteSpace(SourceMessagesJson) || !string.IsNullOrWhiteSpace(CorrectedMessagesJson);
    }
}
