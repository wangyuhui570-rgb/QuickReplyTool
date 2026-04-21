using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

public sealed class ConversationLoggingService :
    IAsyncDisposable,
    IConversationContextCaptureService,
    IContextRecognizer,
    IRecommendationQueryService,
    IRecommendationTrackingService
{
    private const int MaxCapturedTextLength = 16000;
    private const int MaxAutomationNodes = 400;
    private const int MaxAutomationDepth = 7;
    private const int DefaultScreenshotRetentionDays = 7;
    private const int DefaultStructuredLogRetentionDays = 30;
    private const string RecognitionMethodVersion = "automation_ocr_hybrid_v3";
    private const string RecommendationRuleVersion = "keyword-frequency-v2";
    private const string AiDraftRuleVersion = "grounded-ai-v1";
    private const int ReplyGroupWindowSeconds = 75;
    private const int FrequentReplySuggestionThreshold = 3;
    private const int FrequentReplySuggestionMinimumGeneralLength = 6;
    private const int FrequentReplySuggestionMinimumCjkCount = 3;
    private const int MaxSelectedCustomerLines = 4;
    private const int MaxRecentContextLines = 24;
    private const int MaxRecentCandidateContextLines = 12;
    private const int ContextNeighborLineRadius = 4;
    private const int MaxRecognizedContextLines = 18;
    private static readonly TimeSpan FrequentReplySuggestionLookback = TimeSpan.FromDays(30);

    private readonly string _databasePath;
    private readonly string _capturesRootPath;
    private readonly string _connectionString;
    private readonly Channel<ConversationLogRequest> _channel;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _workerTask;
    private long _sessionPersistedCount;
    private volatile int _screenshotRetentionDays = DefaultScreenshotRetentionDays;
    private volatile int _screenshotMaxSizeMb = 1024;
    private volatile int _structuredLogRetentionDays = DefaultStructuredLogRetentionDays;

    public ConversationLoggingService()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuickReplyTool",
                "logs"))
    {
    }

    internal ConversationLoggingService(string logsRootPath)
    {
        var root = string.IsNullOrWhiteSpace(logsRootPath)
            ? throw new ArgumentException("Logs root path is required.", nameof(logsRootPath))
            : logsRootPath;

        Directory.CreateDirectory(root);
        _capturesRootPath = Path.Combine(root, "captures");
        Directory.CreateDirectory(_capturesRootPath);

        _databasePath = Path.Combine(root, "conversation-logs.db");
        _connectionString = $"Data Source={_databasePath}";
        _channel = Channel.CreateUnbounded<ConversationLogRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _workerTask = Task.Run(ProcessQueueAsync);
    }

    public void LogPhraseUsage(
        ReplyPhrase phrase,
        string triggerSource,
        ComposeActionMode actionMode,
        string? searchKeyword,
        bool resultSuccess,
        string resultMessage,
        IntPtr targetWindowHandle,
        ComposeOperationTelemetry? composeTelemetry = null,
        ConversationRecommendationSnapshot? recognitionOverride = null)
    {
        if (phrase is null)
        {
            return;
        }

        Enqueue(new ConversationLogRequest
        {
            OccurredAt = DateTimeOffset.Now,
            TriggerSource = triggerSource,
            ActionMode = actionMode,
            PayloadType = "phrase",
            SearchKeyword = NormalizeOptionalValue(searchKeyword),
            ResultSuccess = resultSuccess,
            ResultMessage = NormalizeOptionalValue(resultMessage),
            TargetWindowHandle = targetWindowHandle,
            ComposeTelemetry = composeTelemetry,
            RecognitionOverride = recognitionOverride,
            PhraseId = phrase.Id,
            PhraseTitle = NormalizeOptionalValue(phrase.Title),
            PhraseTags = NormalizeOptionalValue(phrase.Tags),
            PhraseCategoryName = NormalizeOptionalValue(phrase.CategoryName),
            PayloadText = NormalizeOptionalValue(phrase.Content)
        });
    }

    public void LogFileUsage(
        LibraryFileItem fileItem,
        string triggerSource,
        ComposeActionMode actionMode,
        string? searchKeyword,
        bool resultSuccess,
        string resultMessage,
        IntPtr targetWindowHandle,
        ComposeOperationTelemetry? composeTelemetry = null)
    {
        if (fileItem is null)
        {
            return;
        }

        Enqueue(new ConversationLogRequest
        {
            OccurredAt = DateTimeOffset.Now,
            TriggerSource = triggerSource,
            ActionMode = actionMode,
            PayloadType = "file",
            SearchKeyword = NormalizeOptionalValue(searchKeyword),
            ResultSuccess = resultSuccess,
            ResultMessage = NormalizeOptionalValue(resultMessage),
            TargetWindowHandle = targetWindowHandle,
            ComposeTelemetry = composeTelemetry,
            FileItemId = fileItem.Id,
            FileDisplayName = NormalizeOptionalValue(fileItem.DisplayName),
            FilePathsJson = JsonSerializer.Serialize(new[] { fileItem.FullPath }),
            PayloadText = NormalizeOptionalValue(fileItem.FullPath)
        });
    }

    public void LogQuickTagUsage(
        QuickTag quickTag,
        string triggerSource,
        ComposeActionMode actionMode,
        bool resultSuccess,
        string resultMessage,
        IntPtr targetWindowHandle,
        ComposeOperationTelemetry? composeTelemetry = null)
    {
        if (quickTag is null)
        {
            return;
        }

        Enqueue(new ConversationLogRequest
        {
            OccurredAt = DateTimeOffset.Now,
            TriggerSource = triggerSource,
            ActionMode = actionMode,
            PayloadType = "quick_tag",
            ResultSuccess = resultSuccess,
            ResultMessage = NormalizeOptionalValue(resultMessage),
            TargetWindowHandle = targetWindowHandle,
            ComposeTelemetry = composeTelemetry,
            PhraseId = quickTag.PhraseId,
            PhraseTitle = NormalizeOptionalValue(quickTag.PhraseTitle),
            PhraseCategoryName = NormalizeOptionalValue(quickTag.PhraseCategoryName),
            PayloadText = NormalizeOptionalValue(quickTag.PhraseContent)
        });
    }

    public void LogPlainTextUsage(
        string payloadText,
        string triggerSource,
        ComposeActionMode actionMode,
        string? searchKeyword,
        bool resultSuccess,
        string resultMessage,
        IntPtr targetWindowHandle,
        ComposeOperationTelemetry? composeTelemetry = null,
        ConversationRecommendationSnapshot? recognitionOverride = null)
    {
        var normalizedPayloadText = NormalizeOptionalValue(payloadText);
        if (string.IsNullOrWhiteSpace(normalizedPayloadText))
        {
            return;
        }

        Enqueue(new ConversationLogRequest
        {
            OccurredAt = DateTimeOffset.Now,
            TriggerSource = triggerSource,
            ActionMode = actionMode,
            PayloadType = "plain_text",
            SearchKeyword = NormalizeOptionalValue(searchKeyword),
            ResultSuccess = resultSuccess,
            ResultMessage = NormalizeOptionalValue(resultMessage),
            TargetWindowHandle = targetWindowHandle,
            ComposeTelemetry = composeTelemetry,
            RecognitionOverride = recognitionOverride,
            PayloadText = normalizedPayloadText
        });
    }

    public async Task LogOperationEventAsync(
        string operationName,
        string stage,
        bool success,
        bool skipped,
        long durationMs,
        string message,
        object? details = null)
    {
        var normalizedOperationName = NormalizeOptionalValue(operationName);
        if (string.IsNullOrWhiteSpace(normalizedOperationName))
        {
            return;
        }

        await InitializeSchemaAsync().ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operation_events (
                id,
                occurred_at,
                operation_name,
                stage,
                success,
                skipped,
                duration_ms,
                message,
                detail_json,
                app_version)
            VALUES (
                $id,
                $occurredAt,
                $operationName,
                $stage,
                $success,
                $skipped,
                $durationMs,
                $message,
                $detailJson,
                $appVersion);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$operationName", normalizedOperationName);
        command.Parameters.AddWithValue("$stage", NormalizeOptionalValue(stage));
        command.Parameters.AddWithValue("$success", success ? 1 : 0);
        command.Parameters.AddWithValue("$skipped", skipped ? 1 : 0);
        command.Parameters.AddWithValue("$durationMs", Math.Max(0, durationMs));
        command.Parameters.AddWithValue("$message", NormalizeOptionalValue(message));
        command.Parameters.AddWithValue("$detailJson", SerializeDetails(details));
        command.Parameters.AddWithValue("$appVersion", AppIdentity.Version);
        await command.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
    }

    public async Task<FrequentReplyPhraseSuggestion?> GetFrequentPlainTextSuggestionAsync(string payloadText, int additionalSuccessCount = 0)
    {
        var normalizedPayloadText = NormalizeOptionalValue(payloadText);
        if (!IsEligibleFrequentReplyPayload(normalizedPayloadText))
        {
            return null;
        }

        await InitializeSchemaAsync().ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        var threshold = DateTimeOffset.Now.Subtract(FrequentReplySuggestionLookback).ToString("O", CultureInfo.InvariantCulture);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
            SELECT COUNT(1)
            FROM conversation_logs
            WHERE payload_type = 'plain_text'
              AND result_success = 1
              AND action_mode IN ('Send', 'Input')
              AND occurred_at >= $threshold
              AND payload_text = $payloadText;
            """;
        countCommand.Parameters.AddWithValue("$threshold", threshold);
        countCommand.Parameters.AddWithValue("$payloadText", normalizedPayloadText);

        var successCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(_cancellationTokenSource.Token).ConfigureAwait(false) ?? 0);
        successCount += Math.Max(0, additionalSuccessCount);
        if (successCount < FrequentReplySuggestionThreshold)
        {
            return null;
        }

        await using var detailsCommand = connection.CreateCommand();
        detailsCommand.CommandText = """
            SELECT recognized_customer_text
            FROM conversation_logs
            WHERE payload_type = 'plain_text'
              AND result_success = 1
              AND action_mode IN ('Send', 'Input')
              AND payload_text = $payloadText
            ORDER BY occurred_at DESC
            LIMIT 1;
            """;
        detailsCommand.Parameters.AddWithValue("$payloadText", normalizedPayloadText);

        await using var reader = await detailsCommand.ExecuteReaderAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            return new FrequentReplyPhraseSuggestion
            {
                PayloadText = normalizedPayloadText,
                UseCount = successCount
            };
        }

        return new FrequentReplyPhraseSuggestion
        {
            PayloadText = normalizedPayloadText,
            UseCount = successCount,
            CustomerText = reader.IsDBNull(0) ? string.Empty : reader.GetString(0)
        };
    }

    internal static bool IsEligibleFrequentReplyPayload(string payloadText)
    {
        var normalizedPayloadText = NormalizeOptionalValue(payloadText);
        if (string.IsNullOrWhiteSpace(normalizedPayloadText))
        {
            return false;
        }

        var significantRuneCount = 0;
        var cjkRuneCount = 0;
        foreach (var rune in normalizedPayloadText.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            significantRuneCount++;
            if (IsCjkRune(rune))
            {
                cjkRuneCount++;
            }
        }

        return cjkRuneCount >= FrequentReplySuggestionMinimumCjkCount ||
               significantRuneCount >= FrequentReplySuggestionMinimumGeneralLength;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _cancellationTokenSource.Cancel();

        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    public async Task<ConversationLogStats> GetStatsAsync()
    {
        await InitializeSchemaAsync().ConfigureAwait(false);

        long totalLogCount = 0;
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM conversation_logs;";
            totalLogCount = (long)(await command.ExecuteScalarAsync().ConfigureAwait(false) ?? 0L);
        }

        var captureDirectory = new DirectoryInfo(_capturesRootPath);
        var captureFiles = captureDirectory.Exists
            ? captureDirectory.EnumerateFiles("*", SearchOption.AllDirectories).ToArray()
            : [];

        var captureFileCount = captureFiles.Length;
        var captureFolderSizeBytes = captureFiles.Sum(file => file.Length);

        return new ConversationLogStats(
            Interlocked.Read(ref _sessionPersistedCount),
            totalLogCount,
            captureFileCount,
            captureFolderSizeBytes,
            _databasePath,
            _capturesRootPath);
    }

    public async Task<ConversationSyncPayload> ExportSyncPayloadAsync(
        string clientId,
        DateTimeOffset? since,
        bool includeRawConversationData = false)
    {
        await InitializeSchemaAsync().ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var generatedAt = DateTimeOffset.Now;
        var conversationLogs = await LoadConversationSyncItemsAsync(connection, since, includeRawConversationData).ConfigureAwait(false);
        var recommendationEvents = await LoadRecommendationEventSyncItemsAsync(connection, since, includeRawConversationData).ConfigureAwait(false);
        var recognitionCorrections = await LoadRecognitionCorrectionSyncItemsAsync(connection, since, includeRawConversationData).ConfigureAwait(false);

        return new ConversationSyncPayload
        {
            ClientId = clientId,
            AppVersion = AppIdentity.Version,
            GeneratedAt = generatedAt,
            Since = since,
            IsFullSync = since is null,
            ConversationLogs = conversationLogs,
            RecommendationEvents = recommendationEvents,
            RecognitionCorrections = recognitionCorrections
        };
    }

    public async Task<CapturedConversationContext> CaptureConversationContextAsync(
        IntPtr targetWindowHandle,
        bool includeScreenshot = false,
        bool enableOcr = false)
    {
        await InitializeSchemaAsync().ConfigureAwait(false);
        var captureStopwatch = Stopwatch.StartNew();
        var snapshot = await RunStaWorkerAsync(() =>
        {
            return CaptureWindowSnapshot(targetWindowHandle, includeScreenshot || enableOcr);
        }).ConfigureAwait(false);
        await LogOperationEventAsync(
                "recommendation_capture",
                "capture",
                success: !string.IsNullOrWhiteSpace(snapshot.CapturedText) || !string.IsNullOrWhiteSpace(snapshot.ScreenshotPath),
                skipped: false,
                durationMs: captureStopwatch.ElapsedMilliseconds,
                message: BuildCaptureOperationMessage(snapshot),
                details: new
                {
                    snapshot.PlatformHint,
                    snapshot.CaptureStatus,
                    hasText = !string.IsNullOrWhiteSpace(snapshot.CapturedText),
                    hasScreenshot = !string.IsNullOrWhiteSpace(snapshot.ScreenshotPath)
                })
            .ConfigureAwait(false);

        if (enableOcr &&
            !string.IsNullOrWhiteSpace(snapshot.ScreenshotPath) &&
            ShouldUseOcrFallback(snapshot))
        {
            var ocrStopwatch = Stopwatch.StartNew();
            var ocrResult = await ScreenOcrReader.TryReadTextDetailedAsync(snapshot.ScreenshotPath, snapshot.PlatformHint).ConfigureAwait(false);
            snapshot.CaptureStatus = AppendCaptureStatus(snapshot.CaptureStatus, ocrResult.Status);
            if (ocrResult.Success && !string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                snapshot.CapturedText = MergeCapturedText(snapshot.CapturedText, ocrResult.Text);
                snapshot.CaptureStatus = AppendCaptureStatus(BuildCaptureStatus(snapshot), "ocr");
            }

            await LogOperationEventAsync(
                    "recommendation_capture",
                    "ocr",
                    success: ocrResult.Success,
                    skipped: false,
                    durationMs: ocrStopwatch.ElapsedMilliseconds,
                    message: BuildOcrOperationMessage(ocrResult),
                    details: new
                    {
                        snapshot.PlatformHint,
                        snapshot.CaptureStatus,
                        ocrResult.Status,
                        ocrResult.HostFileName
                    })
                .ConfigureAwait(false);
        }
        else if (enableOcr)
        {
            await LogOperationEventAsync(
                    "recommendation_capture",
                    "ocr",
                    success: true,
                    skipped: true,
                    durationMs: 0,
                    message: "窗口文本已足够，跳过 OCR。",
                    details: new
                    {
                        snapshot.PlatformHint,
                        snapshot.CaptureStatus
                    })
                .ConfigureAwait(false);
        }

        if (enableOcr && !string.IsNullOrWhiteSpace(snapshot.ScreenshotPath))
        {
            DeleteTemporaryScreenshot(snapshot);
        }

        return ToCapturedConversationContext(snapshot, targetWindowHandle);
    }

    public ConversationRecommendationSnapshot RecognizeContext(CapturedConversationContext capturedContext)
    {
        var request = new ConversationLogRequest
        {
            OccurredAt = DateTimeOffset.Now,
            TriggerSource = "recommendation_preview",
            ActionMode = ComposeActionMode.Copy,
            PayloadType = "recommendation_preview",
            ResultSuccess = true,
            ResultMessage = string.Empty,
            TargetWindowHandle = capturedContext.TargetWindowHandle
        };

        var windowSnapshot = ToWindowCaptureSnapshot(capturedContext);
        var recognition = RecognizeConversation(request, windowSnapshot);
        var keywords = RecommendationKeywordBuilder.Build(
            recognition.RecognizedCustomerText,
            recognition.RecognizedContextText,
            recognition.RecognizedProductId,
            recognition.RecognizedProductName);

        return new ConversationRecommendationSnapshot
        {
            PlatformHint = windowSnapshot.PlatformHint,
            WindowTitle = windowSnapshot.WindowTitle,
            WindowClassName = windowSnapshot.WindowClassName,
            ProcessName = windowSnapshot.ProcessName,
            ProcessPath = windowSnapshot.ProcessPath,
            CaptureStatus = windowSnapshot.CaptureStatus,
            CapturedText = windowSnapshot.CapturedText,
            RecognitionStatus = recognition.Status,
            RecognitionMethod = recognition.Method,
            RecognitionConfidence = recognition.Confidence,
            RecognizedCustomerText = recognition.RecognizedCustomerText,
            RecognizedContextText = recognition.RecognizedContextText,
            Product = new ConversationProductContext
            {
                ProductId = recognition.RecognizedProductId,
                ProductName = recognition.RecognizedProductName
            },
            Messages = recognition.Messages,
            Keywords = keywords
        };
    }

    private static async Task<IReadOnlyList<ConversationLogSyncItem>> LoadConversationSyncItemsAsync(
        SqliteConnection connection,
        DateTimeOffset? since,
        bool includeRawConversationData)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   occurred_at,
                   trigger_source,
                   action_mode,
                   payload_type,
                   search_keyword,
                   result_success,
                   result_message,
                   platform_hint,
                   target_window_title,
                   target_process_name,
                   capture_status,
                   captured_text,
                   phrase_id,
                   phrase_title,
                   phrase_tags,
                   phrase_category_name,
                   file_item_id,
                   file_display_name,
                   payload_text,
                   recognition_status,
                   recognition_confidence,
                   recognized_customer_text,
                   recognized_context_text,
                   recognized_product_id,
                   recognized_product_name,
                   recognized_messages_json,
                   reply_group_id,
                   reply_group_index,
                   data_quality_grade,
                   app_version,
                   recommendation_rule_version
            FROM conversation_logs
            WHERE $since = '' OR occurred_at > $since
            ORDER BY occurred_at ASC;
            """;
        command.Parameters.AddWithValue("$since", since.HasValue ? since.Value.ToString("O", CultureInfo.InvariantCulture) : string.Empty);

        var items = new List<ConversationLogSyncItem>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            items.Add(new ConversationLogSyncItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                OccurredAt = ParseSyncDateTime(reader.GetString(1)),
                TriggerSource = reader.GetString(2),
                ActionMode = reader.GetString(3),
                PayloadType = reader.GetString(4),
                SearchKeyword = SanitizeRemoteTextIfNeeded(reader.GetString(5), includeRawConversationData, 64),
                ResultSuccess = reader.GetInt32(6) == 1,
                ResultMessage = SanitizeRemoteTextIfNeeded(reader.GetString(7), includeRawConversationData, 160),
                PlatformHint = reader.GetString(8),
                TargetWindowTitle = includeRawConversationData ? reader.GetString(9) : string.Empty,
                TargetProcessName = reader.GetString(10),
                CaptureStatus = reader.GetString(11),
                CapturedText = SanitizeRemoteTextIfNeeded(reader.GetString(12), includeRawConversationData, 220),
                PhraseId = reader.GetString(13),
                PhraseTitle = reader.GetString(14),
                PhraseTags = reader.GetString(15),
                PhraseCategoryName = reader.GetString(16),
                FileItemId = reader.GetString(17),
                FileDisplayName = reader.GetString(18),
                PayloadText = includeRawConversationData ? reader.GetString(19) : string.Empty,
                RecognitionStatus = reader.GetString(20),
                RecognitionConfidence = reader.GetDouble(21),
                RecognizedCustomerText = SanitizeRemoteTextIfNeeded(reader.GetString(22), includeRawConversationData, 180),
                RecognizedContextText = SanitizeRemoteTextIfNeeded(reader.GetString(23), includeRawConversationData, 220),
                RecognizedProductId = SanitizeRemoteTextIfNeeded(reader.GetString(24), includeRawConversationData, 64),
                RecognizedProductName = SanitizeRemoteTextIfNeeded(reader.GetString(25), includeRawConversationData, 80),
                RecognizedMessagesJson = includeRawConversationData ? reader.GetString(26) : string.Empty,
                ReplyGroupId = reader.GetString(27),
                ReplyGroupIndex = reader.GetInt32(28),
                DataQualityGrade = reader.GetString(29),
                AppVersion = reader.GetString(30),
                RecommendationRuleVersion = reader.GetString(31)
            });
        }

        return items;
    }

    private static async Task<IReadOnlyList<RecommendationEventSyncItem>> LoadRecommendationEventSyncItemsAsync(
        SqliteConnection connection,
        DateTimeOffset? since,
        bool includeRawConversationData)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   occurred_at,
                   recommendation_session_id,
                   event_type,
                   search_mode,
                   search_keyword,
                   action_mode,
                   result_success,
                   phrase_id,
                   phrase_title,
                   phrase_category_name,
                   display_rank,
                   matched_keyword,
                   match_count,
                   sample_count,
                   confidence,
                   recognized_customer_text,
                   platform_hint,
                   window_title,
                   actual_payload_text,
                   grounding_phrase_count,
                   grounding_knowledge_count,
                   grounding_knowledge_ids_json,
                   knowledge_hit,
                   app_version,
                   recommendation_rule_version
            FROM recommendation_events
            WHERE $since = '' OR occurred_at > $since
            ORDER BY occurred_at ASC;
            """;
        command.Parameters.AddWithValue("$since", since.HasValue ? since.Value.ToString("O", CultureInfo.InvariantCulture) : string.Empty);

        var items = new List<RecommendationEventSyncItem>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            items.Add(new RecommendationEventSyncItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                OccurredAt = ParseSyncDateTime(reader.GetString(1)),
                RecommendationSessionId = reader.GetString(2),
                EventType = reader.GetString(3),
                SearchMode = reader.GetString(4),
                SearchKeyword = SanitizeRemoteTextIfNeeded(reader.GetString(5), includeRawConversationData, 64),
                ActionMode = reader.GetString(6),
                ResultSuccess = reader.GetInt32(7) == 1,
                PhraseId = reader.GetString(8),
                PhraseTitle = reader.GetString(9),
                PhraseCategoryName = reader.GetString(10),
                DisplayRank = reader.GetInt32(11),
                MatchedKeyword = SanitizeRemoteTextIfNeeded(reader.GetString(12), includeRawConversationData, 48),
                MatchCount = reader.GetInt32(13),
                SampleCount = reader.GetInt32(14),
                Confidence = reader.GetDouble(15),
                RecognizedCustomerText = SanitizeRemoteTextIfNeeded(reader.GetString(16), includeRawConversationData, 180),
                PlatformHint = reader.GetString(17),
                WindowTitle = includeRawConversationData ? reader.GetString(18) : string.Empty,
                ActualPayloadText = includeRawConversationData ? reader.GetString(19) : string.Empty,
                GroundingPhraseCount = reader.GetInt32(20),
                GroundingKnowledgeCount = reader.GetInt32(21),
                GroundingKnowledgeIdsJson = reader.GetString(22),
                KnowledgeHit = reader.GetInt32(23) == 1,
                AppVersion = reader.GetString(24),
                RecommendationRuleVersion = reader.GetString(25)
            });
        }

        return items;
    }

    private static async Task<IReadOnlyList<RecognitionCorrectionSyncItem>> LoadRecognitionCorrectionSyncItemsAsync(
        SqliteConnection connection,
        DateTimeOffset? since,
        bool includeRawConversationData)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   occurred_at,
                   recommendation_session_id,
                   platform_hint,
                   window_title,
                   source_customer_text,
                   corrected_customer_text,
                   source_context_text,
                   source_product_id,
                   source_product_name,
                   source_messages_json,
                   corrected_context_text,
                   corrected_product_id,
                   corrected_product_name,
                   corrected_messages_json,
                   recognition_method,
                   app_version
            FROM recognition_corrections
            WHERE $since = '' OR occurred_at > $since
            ORDER BY occurred_at ASC;
            """;
        command.Parameters.AddWithValue("$since", since.HasValue ? since.Value.ToString("O", CultureInfo.InvariantCulture) : string.Empty);

        var items = new List<RecognitionCorrectionSyncItem>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            items.Add(new RecognitionCorrectionSyncItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                OccurredAt = ParseSyncDateTime(reader.GetString(1)),
                RecommendationSessionId = reader.GetString(2),
                PlatformHint = reader.GetString(3),
                WindowTitle = includeRawConversationData ? reader.GetString(4) : string.Empty,
                SourceCustomerText = SanitizeRemoteTextIfNeeded(reader.GetString(5), includeRawConversationData, 180),
                CorrectedCustomerText = SanitizeRemoteTextIfNeeded(reader.GetString(6), includeRawConversationData, 180),
                SourceContextText = SanitizeRemoteTextIfNeeded(reader.GetString(7), includeRawConversationData, 220),
                SourceProductId = SanitizeRemoteTextIfNeeded(reader.GetString(8), includeRawConversationData, 64),
                SourceProductName = SanitizeRemoteTextIfNeeded(reader.GetString(9), includeRawConversationData, 80),
                SourceMessagesJson = includeRawConversationData ? reader.GetString(10) : string.Empty,
                CorrectedContextText = SanitizeRemoteTextIfNeeded(reader.GetString(11), includeRawConversationData, 220),
                CorrectedProductId = SanitizeRemoteTextIfNeeded(reader.GetString(12), includeRawConversationData, 64),
                CorrectedProductName = SanitizeRemoteTextIfNeeded(reader.GetString(13), includeRawConversationData, 80),
                CorrectedMessagesJson = includeRawConversationData ? reader.GetString(14) : string.Empty,
                RecognitionMethod = reader.GetString(15),
                AppVersion = reader.GetString(16)
            });
        }

        return items;
    }

    private static DateTimeOffset ParseSyncDateTime(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    internal static string SanitizeRemoteTextForTesting(string value, int maxLength = 180)
    {
        return SanitizeRemoteText(value, maxLength);
    }

    private static string SanitizeRemoteTextIfNeeded(string value, bool includeRawConversationData, int maxLength)
    {
        return includeRawConversationData
            ? value
            : SanitizeRemoteText(value, maxLength);
    }

    private static string SanitizeRemoteText(string value, int maxLength)
    {
        var normalized = NormalizeOptionalValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = Regex.Replace(normalized, @"https?://\S+", "[url]", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"(?:[A-Za-z]:\\|/)[^\s]+", "[path]");
        normalized = Regex.Replace(normalized, @"\b\d{6,}\b", "[num]");
        normalized = Regex.Replace(normalized, @"\b[A-Za-z0-9_-]{12,}\b", "[id]");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return $"{normalized[..Math.Max(0, maxLength - 3)]}...";
    }

    public async Task<IReadOnlyList<PhraseRecommendationCandidate>> GetPhraseRecommendationsAsync(
        ConversationRecommendationSnapshot snapshot,
        int maxResults = 5,
        RecommendationQueryScope? queryScope = null)
    {
        await InitializeSchemaAsync().ConfigureAwait(false);

        if (snapshot is null ||
            snapshot.RecognitionConfidence < 0.20d ||
            snapshot.Keywords.Count == 0 ||
            (string.IsNullOrWhiteSpace(snapshot.RecognizedCustomerText) &&
             string.IsNullOrWhiteSpace(snapshot.RecognizedContextText) &&
             !snapshot.Product.HasValue))
        {
            return [];
        }

        var candidates = new Dictionary<Guid, PhraseRecommendationCandidate>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        foreach (var keyword in snapshot.Keywords.Take(12))
        {
            var sampleCount = await GetRecommendationSampleCountAsync(connection, snapshot.PlatformHint, keyword, queryScope).ConfigureAwait(false);
            if (sampleCount < 3)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT phrase_id,
                       phrase_title,
                       phrase_category_name,
                       COUNT(1) AS hit_count
                FROM conversation_logs
                WHERE payload_type = 'phrase'
                  AND result_success = 1
                  AND action_mode IN ('Send', 'Input')
                  AND phrase_id <> ''
                  AND recognition_confidence >= 0.2
                  AND (
                        recognized_customer_text LIKE $keywordLike
                     OR recognized_context_text LIKE $keywordLike
                     OR recognized_product_id LIKE $keywordLike
                     OR recognized_product_name LIKE $keywordLike
                  )
                  AND ($platformHint = '' OR platform_hint = $platformHint)
                  AND ($maxOccurredAt = '' OR occurred_at <= $maxOccurredAt)
                  AND ($excludedConversationLogId = '' OR id <> $excludedConversationLogId)
                GROUP BY phrase_id, phrase_title, phrase_category_name
                ORDER BY hit_count DESC
                LIMIT 8;
                """;
            command.Parameters.AddWithValue("$keywordLike", $"%{keyword}%");
            command.Parameters.AddWithValue("$platformHint", string.IsNullOrWhiteSpace(snapshot.PlatformHint) || snapshot.PlatformHint == "unknown"
                ? string.Empty
                : snapshot.PlatformHint);
            BindRecommendationQueryScope(command, queryScope);

            await using var reader = await command.ExecuteReaderAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            while (await reader.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out var phraseId))
                {
                    continue;
                }

                var matchCount = reader.GetInt32(3);
                var confidence = sampleCount <= 0 ? 0d : (double)matchCount / sampleCount;
                if (!ShouldKeepRecommendation(sampleCount, confidence))
                {
                    continue;
                }

                if (!candidates.TryGetValue(phraseId, out var current) ||
                    confidence > current.Confidence ||
                    (Math.Abs(confidence - current.Confidence) < 0.0001d && matchCount > current.MatchCount))
                {
                    candidates[phraseId] = new PhraseRecommendationCandidate
                    {
                        PhraseId = phraseId,
                        PhraseTitle = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        PhraseCategoryName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        MatchedKeyword = keyword,
                        MatchCount = matchCount,
                        SampleCount = sampleCount,
                        Confidence = confidence,
                        RecognizedCustomerText = snapshot.RecognizedCustomerText
                    };
                }
            }
        }

        return candidates.Values
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.MatchCount)
            .ThenByDescending(candidate => candidate.SampleCount)
            .Take(maxResults)
            .ToList();
    }

    public void ApplySettings(AppSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        _screenshotRetentionDays = Math.Max(1, settings.LogScreenshotRetentionDays);
        _screenshotMaxSizeMb = Math.Max(64, settings.LogScreenshotMaxSizeMb);
        _structuredLogRetentionDays = Math.Max(3, settings.LogTextRetentionDays);
    }

    public void RequestMaintenance()
    {
        _channel.Writer.TryWrite(ConversationLogRequest.Maintenance());
    }

    public Guid LogRecommendationsShown(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions,
        SearchOverlayMode mode,
        string? searchKeyword)
    {
        if (snapshot is null || suggestions is null || suggestions.Count == 0)
        {
            return Guid.Empty;
        }

        var sessionId = Guid.NewGuid();
        _ = PersistRecommendationEventsSafeAsync(() => PersistRecommendationsShownAsync(
            sessionId,
            snapshot,
            suggestions,
            mode,
            searchKeyword));
        return sessionId;
    }

    public void LogRecommendationAccepted(
        PhraseRecommendationSuggestion suggestion,
        Guid sessionId,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess)
    {
        if (suggestion is null || sessionId == Guid.Empty)
        {
            return;
        }

        _ = PersistRecommendationEventsSafeAsync(() => PersistRecommendationAcceptedAsync(
            sessionId,
            suggestion,
            mode,
            searchKeyword,
            actionMode,
            resultSuccess));
    }

    public Guid LogKnowledgeCardsShown(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards,
        SearchOverlayMode mode,
        string? searchKeyword)
    {
        if (snapshot is null || knowledgeCards is null || knowledgeCards.Count == 0)
        {
            return Guid.Empty;
        }

        var sessionId = Guid.NewGuid();
        _ = PersistRecommendationEventsSafeAsync(() => PersistKnowledgeCardsShownAsync(
            sessionId,
            snapshot,
            knowledgeCards,
            mode,
            searchKeyword));
        return sessionId;
    }

    public void LogKnowledgeCardAccepted(
        KnowledgeCardSuggestion knowledgeCard,
        Guid sessionId,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess)
    {
        if (knowledgeCard is null || sessionId == Guid.Empty)
        {
            return;
        }

        _ = PersistRecommendationEventsSafeAsync(() => PersistKnowledgeCardAcceptedAsync(
            sessionId,
            knowledgeCard,
            mode,
            searchKeyword,
            actionMode,
            resultSuccess));
    }

    public Guid LogAiDraftsShown(
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<GeneratedReplyCandidate> drafts,
        SearchOverlayMode mode,
        string? searchKeyword)
    {
        if (snapshot is null || drafts is null || drafts.Count == 0)
        {
            return Guid.Empty;
        }

        var sessionId = Guid.NewGuid();
        _ = PersistRecommendationEventsSafeAsync(() => PersistAiDraftsShownAsync(
            sessionId,
            snapshot,
            drafts,
            mode,
            searchKeyword));
        return sessionId;
    }

    public void LogAiDraftAction(
        GeneratedReplyCandidate draft,
        Guid sessionId,
        SearchOverlayMode mode,
        string? searchKeyword,
        string eventType,
        ComposeActionMode actionMode,
        bool resultSuccess)
    {
        if (draft is null || sessionId == Guid.Empty || string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        _ = PersistRecommendationEventsSafeAsync(() => PersistAiDraftActionAsync(
            sessionId,
            draft,
            mode,
            searchKeyword,
            eventType,
            actionMode,
            resultSuccess));
    }

    public Task LogAiDraftStatusAsync(
        ConversationRecommendationSnapshot snapshot,
        SearchOverlayMode mode,
        string? searchKeyword,
        AiDraftGenerationStatus status)
    {
        if (snapshot is null || status is null || !status.ShouldSurfaceStatus)
        {
            return Task.CompletedTask;
        }

        return PersistRecommendationEventsSafeAsync(() => PersistAiDraftStatusAsync(
            snapshot,
            mode,
            searchKeyword,
            status));
    }

    public async Task SaveRecognitionCorrectionAsync(
        Guid sessionId,
        ConversationRecommendationSnapshot snapshot,
        ConversationRecognitionCorrection correction)
    {
        if (!HasMeaningfulRecognitionCorrection(correction))
        {
            return;
        }

        await InitializeSchemaAsync().ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recognition_corrections (
                id,
                occurred_at,
                recommendation_session_id,
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
                corrected_messages_json,
                recognition_method,
                app_version
            )
            VALUES (
                $id,
                $occurredAt,
                $sessionId,
                $platformHint,
                $windowTitle,
                $sourceCustomerText,
                $sourceContextText,
                $sourceProductId,
                $sourceProductName,
                $sourceMessagesJson,
                $correctedCustomerText,
                $correctedContextText,
                $correctedProductId,
                $correctedProductName,
                $correctedMessagesJson,
                $recognitionMethod,
                $appVersion
            );
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sessionId", sessionId == Guid.Empty ? string.Empty : sessionId.ToString());
        command.Parameters.AddWithValue("$platformHint", NormalizeOptionalValue(snapshot.PlatformHint));
        command.Parameters.AddWithValue("$windowTitle", NormalizeOptionalValue(snapshot.WindowTitle));
        command.Parameters.AddWithValue("$sourceCustomerText", NormalizeOptionalValue(snapshot.RecognizedCustomerText));
        command.Parameters.AddWithValue("$sourceContextText", NormalizeOptionalValue(snapshot.RecognizedContextText));
        command.Parameters.AddWithValue("$sourceProductId", NormalizeOptionalValue(snapshot.RecognizedProductId));
        command.Parameters.AddWithValue("$sourceProductName", NormalizeOptionalValue(snapshot.RecognizedProductName));
        command.Parameters.AddWithValue("$sourceMessagesJson", SerializeConversationMessages(snapshot.Messages));
        command.Parameters.AddWithValue("$correctedCustomerText", NormalizeOptionalValue(correction.CustomerText));
        command.Parameters.AddWithValue("$correctedContextText", NormalizeOptionalValue(correction.ContextText));
        command.Parameters.AddWithValue("$correctedProductId", NormalizeOptionalValue(correction.ProductId));
        command.Parameters.AddWithValue("$correctedProductName", NormalizeOptionalValue(correction.ProductName));
        command.Parameters.AddWithValue("$correctedMessagesJson", SerializeConversationMessages(correction.Messages));
        command.Parameters.AddWithValue("$recognitionMethod", RecognitionMethodVersion);
        command.Parameters.AddWithValue("$appVersion", GetAppVersion());
        await command.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HistoricalReplyGroupSuggestion>> GetSimilarReplyGroupsAsync(
        ConversationRecommendationSnapshot snapshot,
        int maxGroups = 3,
        RecommendationQueryScope? queryScope = null)
    {
        await InitializeSchemaAsync().ConfigureAwait(false);

        var sourceText = BuildSearchableRecommendationText(snapshot);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return [];
        }

        var keywords = snapshot.Keywords.Count > 0
            ? snapshot.Keywords.Take(10).ToList()
            : RecommendationKeywordBuilder.Build(
                snapshot.RecognizedCustomerText,
                snapshot.RecognizedContextText,
                snapshot.RecognizedProductId,
                snapshot.RecognizedProductName).Take(10).ToList();
        if (keywords.Count == 0)
        {
            return [];
        }

        var groups = new Dictionary<Guid, ReplyGroupAccumulator>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reply_group_id,
                   occurred_at,
                   recognized_customer_text,
                   recognized_context_text,
                   recognized_product_id,
                   recognized_product_name,
                   payload_text,
                   phrase_title,
                   phrase_category_name,
                   data_quality_grade,
                   platform_hint
            FROM conversation_logs
            WHERE payload_type = 'phrase'
              AND result_success = 1
              AND action_mode IN ('Send', 'Input')
              AND reply_group_id <> ''
              AND (
                    recognized_customer_text <> ''
                 OR recognized_context_text <> ''
                 OR recognized_product_id <> ''
                 OR recognized_product_name <> ''
              )
              AND ($platformHint = '' OR platform_hint = $platformHint)
              AND ($maxOccurredAt = '' OR occurred_at <= $maxOccurredAt)
              AND ($excludedConversationLogId = '' OR id <> $excludedConversationLogId)
            ORDER BY occurred_at DESC
            LIMIT 600;
            """;
        command.Parameters.AddWithValue("$platformHint", string.IsNullOrWhiteSpace(snapshot.PlatformHint) || snapshot.PlatformHint == "unknown"
            ? string.Empty
            : snapshot.PlatformHint);
        BindRecommendationQueryScope(command, queryScope);

        await using var reader = await command.ExecuteReaderAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        while (await reader.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            var replyGroupIdText = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (!Guid.TryParse(replyGroupIdText, out var replyGroupId))
            {
                continue;
            }

            var customerText = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var contextText = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var productId = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var productName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            var searchableText = BuildSearchableRecommendationText(customerText, contextText, productId, productName);
            if (!LooksRelevantToKeywords(searchableText, keywords))
            {
                continue;
            }

            var matchScore = ComputeKeywordMatchScore(searchableText, keywords);
            if (matchScore <= 0)
            {
                continue;
            }

            if (!groups.TryGetValue(replyGroupId, out var group))
            {
                group = new ReplyGroupAccumulator
                {
                    ReplyGroupId = replyGroupId,
                    RecognizedCustomerText = customerText,
                    LastOccurredAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    QualityGrade = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    MatchScore = matchScore
                };
                groups[replyGroupId] = group;
            }

            group.MatchScore = Math.Max(group.MatchScore, matchScore);
            group.Replies.Add(BuildReplyPreview(
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
        }

        return groups.Values
            .OrderByDescending(group => group.MatchScore)
            .ThenByDescending(group => group.LastOccurredAt)
            .Take(maxGroups)
            .Select(group => new HistoricalReplyGroupSuggestion
            {
                ReplyGroupId = group.ReplyGroupId,
                RecognizedCustomerText = group.RecognizedCustomerText,
                ReplyCount = group.Replies.Count,
                RepliesPreviewText = string.Join("  ->  ", group.Replies.Take(3)),
                LastOccurredAt = group.LastOccurredAt,
                MatchScore = group.MatchScore,
                QualityGrade = group.QualityGrade,
                MatchReasonText = $"历史相似问题，关键词匹配度 {Math.Round(group.MatchScore * 100):0}%"
            })
            .ToList();
    }

    public async Task RunMaintenanceAsync()
    {
        await InitializeSchemaAsync().ConfigureAwait(false);
        await CleanupCapturesAsync().ConfigureAwait(false);
        await CleanupStructuredTelemetryAsync().ConfigureAwait(false);
    }

    public async Task<int> DeleteAllCapturesAsync()
    {
        await InitializeSchemaAsync().ConfigureAwait(false);

        var root = new DirectoryInfo(_capturesRootPath);
        if (!root.Exists)
        {
            return 0;
        }

        var files = root.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            return 0;
        }

        var removedPaths = new List<string>(files.Count);
        foreach (var file in files)
        {
            removedPaths.Add(file.FullName);
            TryDeleteFile(file);
        }

        await ClearScreenshotPathReferencesAsync(removedPaths).ConfigureAwait(false);
        RemoveEmptyCaptureFolders(root);
        return removedPaths.Count;
    }

    private async Task PersistRecommendationEventsSafeAsync(Func<Task> action)
    {
        try
        {
            await InitializeSchemaAsync().ConfigureAwait(false);
            await action().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void Enqueue(ConversationLogRequest request)
    {
        _channel.Writer.TryWrite(request);
    }

    private async Task ProcessQueueAsync()
    {
        await InitializeSchemaAsync().ConfigureAwait(false);

        await foreach (var request in _channel.Reader.ReadAllAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            try
            {
                if (request.IsMaintenance)
                {
                    await CleanupCapturesAsync().ConfigureAwait(false);
                    await CleanupStructuredTelemetryAsync().ConfigureAwait(false);
                    continue;
                }

                await PersistRequestAsync(request).ConfigureAwait(false);
                await CleanupCapturesAsync().ConfigureAwait(false);
                await CleanupStructuredTelemetryAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task PersistRequestAsync(ConversationLogRequest request)
    {
        var snapshot = request.RecognitionOverride is null
            ? CaptureWindowSnapshot(request.TargetWindowHandle, includeScreenshot: false)
            : ToWindowCaptureSnapshot(request.RecognitionOverride);
        var recognition = request.RecognitionOverride is null
            ? RecognizeConversation(request, snapshot)
            : ToRecognitionResult(request.RecognitionOverride);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        var replyGroup = await ResolveReplyGroupAsync(connection, request, snapshot, recognition).ConfigureAwait(false);
        var qualityGrade = DetermineQualityGrade(request, recognition);
        var appVersion = GetAppVersion();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_logs (
                id,
                occurred_at,
                trigger_source,
                action_mode,
                payload_type,
                search_keyword,
                result_success,
                result_message,
                target_window_handle,
                target_window_title,
                target_window_class_name,
                target_process_name,
                target_process_path,
                platform_hint,
                target_left,
                target_top,
                target_width,
                target_height,
                capture_status,
                captured_text,
                screenshot_path,
                phrase_id,
                phrase_title,
                phrase_tags,
                phrase_category_name,
                file_item_id,
                file_display_name,
                file_paths_json,
                payload_text,
                recognition_status,
                recognition_method,
                recognition_confidence,
                recognized_customer_text,
                recognized_context_text,
                recognized_product_id,
                recognized_product_name,
                recognized_messages_json,
                reply_group_id,
                reply_group_index,
                data_quality_grade,
                app_version,
                recommendation_rule_version,
                compose_host_profile,
                compose_attempt_count,
                compose_retry_triggered,
                compose_retry_reason,
                compose_retry_outcome,
                compose_failure_category,
                compose_target_changed_before_retry
            )
            VALUES (
                $id,
                $occurredAt,
                $triggerSource,
                $actionMode,
                $payloadType,
                $searchKeyword,
                $resultSuccess,
                $resultMessage,
                $targetWindowHandle,
                $targetWindowTitle,
                $targetWindowClassName,
                $targetProcessName,
                $targetProcessPath,
                $platformHint,
                $targetLeft,
                $targetTop,
                $targetWidth,
                $targetHeight,
                $captureStatus,
                $capturedText,
                $screenshotPath,
                $phraseId,
                $phraseTitle,
                $phraseTags,
                $phraseCategoryName,
                $fileItemId,
                $fileDisplayName,
                $filePathsJson,
                $payloadText,
                $recognitionStatus,
                $recognitionMethod,
                $recognitionConfidence,
                $recognizedCustomerText,
                $recognizedContextText,
                $recognizedProductId,
                $recognizedProductName,
                $recognizedMessagesJson,
                $replyGroupId,
                $replyGroupIndex,
                $dataQualityGrade,
                $appVersion,
                $recommendationRuleVersion,
                $composeHostProfile,
                $composeAttemptCount,
                $composeRetryTriggered,
                $composeRetryReason,
                $composeRetryOutcome,
                $composeFailureCategory,
                $composeTargetChangedBeforeRetry
            );
            """;

        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$occurredAt", request.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$triggerSource", request.TriggerSource);
        command.Parameters.AddWithValue("$actionMode", request.ActionMode.ToString());
        command.Parameters.AddWithValue("$payloadType", request.PayloadType);
        command.Parameters.AddWithValue("$searchKeyword", request.SearchKeyword);
        command.Parameters.AddWithValue("$resultSuccess", request.ResultSuccess ? 1 : 0);
        command.Parameters.AddWithValue("$resultMessage", request.ResultMessage);
        command.Parameters.AddWithValue("$targetWindowHandle", request.TargetWindowHandle == IntPtr.Zero ? string.Empty : $"0x{request.TargetWindowHandle.ToInt64():X}");
        command.Parameters.AddWithValue("$targetWindowTitle", snapshot.WindowTitle);
        command.Parameters.AddWithValue("$targetWindowClassName", snapshot.WindowClassName);
        command.Parameters.AddWithValue("$targetProcessName", snapshot.ProcessName);
        command.Parameters.AddWithValue("$targetProcessPath", snapshot.ProcessPath);
        command.Parameters.AddWithValue("$platformHint", snapshot.PlatformHint);
        command.Parameters.AddWithValue("$targetLeft", snapshot.Left);
        command.Parameters.AddWithValue("$targetTop", snapshot.Top);
        command.Parameters.AddWithValue("$targetWidth", snapshot.Width);
        command.Parameters.AddWithValue("$targetHeight", snapshot.Height);
        command.Parameters.AddWithValue("$captureStatus", snapshot.CaptureStatus);
        command.Parameters.AddWithValue("$capturedText", snapshot.CapturedText);
        command.Parameters.AddWithValue("$screenshotPath", snapshot.ScreenshotPath);
        command.Parameters.AddWithValue("$phraseId", request.PhraseId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("$phraseTitle", request.PhraseTitle);
        command.Parameters.AddWithValue("$phraseTags", request.PhraseTags);
        command.Parameters.AddWithValue("$phraseCategoryName", request.PhraseCategoryName);
        command.Parameters.AddWithValue("$fileItemId", request.FileItemId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("$fileDisplayName", request.FileDisplayName);
        command.Parameters.AddWithValue("$filePathsJson", request.FilePathsJson);
        command.Parameters.AddWithValue("$payloadText", request.PayloadText);
        command.Parameters.AddWithValue("$recognitionStatus", recognition.Status);
        command.Parameters.AddWithValue("$recognitionMethod", recognition.Method);
        command.Parameters.AddWithValue("$recognitionConfidence", recognition.Confidence);
        command.Parameters.AddWithValue("$recognizedCustomerText", recognition.RecognizedCustomerText);
        command.Parameters.AddWithValue("$recognizedContextText", recognition.RecognizedContextText);
        command.Parameters.AddWithValue("$recognizedProductId", recognition.RecognizedProductId);
        command.Parameters.AddWithValue("$recognizedProductName", recognition.RecognizedProductName);
        command.Parameters.AddWithValue("$recognizedMessagesJson", recognition.MessagesJson);
        command.Parameters.AddWithValue("$replyGroupId", replyGroup.GroupId.ToString());
        command.Parameters.AddWithValue("$replyGroupIndex", replyGroup.NextIndex);
        command.Parameters.AddWithValue("$dataQualityGrade", qualityGrade);
        command.Parameters.AddWithValue("$appVersion", appVersion);
        command.Parameters.AddWithValue("$recommendationRuleVersion", RecommendationRuleVersion);
        command.Parameters.AddWithValue("$composeHostProfile", request.ComposeTelemetry?.HostProfile ?? string.Empty);
        command.Parameters.AddWithValue("$composeAttemptCount", request.ComposeTelemetry?.AttemptCount ?? 1);
        command.Parameters.AddWithValue("$composeRetryTriggered", request.ComposeTelemetry?.RetryTriggered == true ? 1 : 0);
        command.Parameters.AddWithValue("$composeRetryReason", request.ComposeTelemetry?.RetryReason ?? string.Empty);
        command.Parameters.AddWithValue("$composeRetryOutcome", request.ComposeTelemetry?.RetryOutcome ?? string.Empty);
        command.Parameters.AddWithValue("$composeFailureCategory", request.ComposeTelemetry?.FailureCategory ?? (request.ResultSuccess ? "none" : ClassifyResultMessageFailureCategory(request.ResultMessage)));
        command.Parameters.AddWithValue("$composeTargetChangedBeforeRetry", request.ComposeTelemetry?.TargetChangedBeforeRetry == true ? 1 : 0);

        await command.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _sessionPersistedCount);
    }

    private static ConversationRecognitionResult ToRecognitionResult(ConversationRecommendationSnapshot snapshot)
    {
        var messages = snapshot.Messages ?? [];
        return new ConversationRecognitionResult
        {
            Status = NormalizeOptionalValue(snapshot.RecognitionStatus),
            Method = NormalizeOptionalValue(snapshot.RecognitionMethod),
            Confidence = snapshot.RecognitionConfidence,
            RecognizedCustomerText = NormalizeOptionalValue(snapshot.RecognizedCustomerText),
            RecognizedContextText = NormalizeOptionalValue(snapshot.RecognizedContextText),
            RecognizedProductId = NormalizeOptionalValue(snapshot.RecognizedProductId),
            RecognizedProductName = NormalizeOptionalValue(snapshot.RecognizedProductName),
            Messages = messages,
            MessagesJson = JsonSerializer.Serialize(messages)
        };
    }

    private async Task CleanupCapturesAsync()
    {
        try
        {
            var root = new DirectoryInfo(_capturesRootPath);
            if (!root.Exists)
            {
                return;
            }

            var retentionCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _screenshotRetentionDays));
            var files = root.EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(file => file.CreationTimeUtc)
                .ToList();

            var removedPaths = new List<string>();
            foreach (var file in files)
            {
                if (file.CreationTimeUtc < retentionCutoff)
                {
                    removedPaths.Add(file.FullName);
                    TryDeleteFile(file);
                }
            }

            files = root.EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(file => file.CreationTimeUtc)
                .ToList();

            long totalSize = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (totalSize <= GetScreenshotMaxSizeBytes())
                {
                    break;
                }

                totalSize -= file.Length;
                removedPaths.Add(file.FullName);
                TryDeleteFile(file);
            }

            if (removedPaths.Count > 0)
            {
                await ClearScreenshotPathReferencesAsync(removedPaths).ConfigureAwait(false);
            }

            RemoveEmptyCaptureFolders(root);
        }
        catch
        {
        }
    }

    private async Task CleanupStructuredTelemetryAsync()
    {
        try
        {
            var retentionCutoff = DateTimeOffset.UtcNow
                .AddDays(-Math.Max(3, _structuredLogRetentionDays))
                .ToString("O", CultureInfo.InvariantCulture);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

            foreach (var tableName in new[] { "conversation_logs", "recommendation_events", "recognition_corrections", "operation_events" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {tableName} WHERE occurred_at < $retentionCutoff;";
                command.Parameters.AddWithValue("$retentionCutoff", retentionCutoff);
                await command.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private async Task ClearScreenshotPathReferencesAsync(IReadOnlyCollection<string> removedPaths)
    {
        var normalizedPaths = removedPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        foreach (var removedPath in normalizedPaths)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE conversation_logs
                SET screenshot_path = '',
                    capture_status = CASE
                        WHEN capture_status LIKE 'text_and_screenshot%' THEN REPLACE(capture_status, 'text_and_screenshot', 'text_only')
                        WHEN capture_status LIKE 'screenshot_only%' THEN REPLACE(capture_status, 'screenshot_only', 'capture_failed')
                        ELSE capture_status
                    END
                WHERE screenshot_path = $screenshotPath;
                """;
            command.Parameters.AddWithValue("$screenshotPath", removedPath);
            await command.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    private static void TryDeleteFile(FileInfo file)
    {
        try
        {
            if (file.Exists)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }

    private static void RemoveEmptyCaptureFolders(DirectoryInfo root)
    {
        foreach (var directory in root.EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(item => item.FullName.Length))
        {
            try
            {
                if (!directory.EnumerateFileSystemInfos().Any())
                {
                    directory.Delete();
                }
            }
            catch
            {
            }
        }
    }

    private long GetScreenshotMaxSizeBytes()
    {
        return Math.Max(64, _screenshotMaxSizeMb) * 1024L * 1024L;
    }

    private static ConversationRecognitionResult RecognizeConversation(
        ConversationLogRequest request,
        WindowCaptureSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.CapturedText))
        {
            return ConversationRecognitionResult.Empty("no_captured_text");
        }

        var excludedTexts = BuildExcludedTextSet(request, snapshot);
        var rawLines = snapshot.CapturedText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeCapturedValue)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(MaxAutomationNodes)
            .ToList();

        if (rawLines.Count == 0)
        {
            return ConversationRecognitionResult.Empty("empty_snapshot_text");
        }

        var candidates = new List<RecognizedMessageCandidate>();
        for (var index = 0; index < rawLines.Count; index++)
        {
            var line = rawLines[index];
            var score = ScoreMessageCandidate(line, excludedTexts, snapshot.PlatformHint, index, rawLines.Count, out var reason);
            if (score <= 0)
            {
                continue;
            }

            candidates.Add(new RecognizedMessageCandidate
            {
                Index = index,
                Text = line,
                Score = score,
                Reason = reason
            });
        }

        if (candidates.Count == 0)
        {
            return new ConversationRecognitionResult
            {
                Status = "no_message_candidates",
                Method = RecognitionMethodVersion,
                Confidence = 0,
                RecognizedCustomerText = string.Empty,
                RecognizedContextText = string.Empty,
                Messages = [],
                MessagesJson = "[]"
            };
        }

        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.Index)
            .ToList();

        var customerCandidateIndexes = orderedCandidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Index)
            .Take(MaxSelectedCustomerLines)
            .Select(candidate => candidate.Index)
            .ToHashSet();

        var contextLineIndexes = BuildContextLineIndexes(rawLines, orderedCandidates, customerCandidateIndexes, snapshot.PlatformHint);
        var contextLineIndexSet = contextLineIndexes.ToHashSet();
        var contextLines = contextLineIndexes
            .Select(index => rawLines[index])
            .ToList();

        var customerLines = orderedCandidates
            .Where(candidate => customerCandidateIndexes.Contains(candidate.Index))
            .OrderBy(candidate => candidate.Index)
            .Select(candidate => candidate.Text)
            .ToList();

        var filteredCustomerLines = FilterRecognizedConversationLines(customerLines, snapshot.PlatformHint);
        var filteredContextLines = FilterRecognizedConversationLines(contextLines, snapshot.PlatformHint)
            .TakeLast(MaxRecognizedContextLines)
            .ToList();
        var messageTurns = orderedCandidates
            .Select(candidate => new ConversationMessageTurn
            {
                Index = candidate.Index,
                Text = candidate.Text,
                Score = Math.Round(candidate.Score, 3, MidpointRounding.AwayFromZero),
                Reason = candidate.Reason,
                SelectedForCustomerText = customerCandidateIndexes.Contains(candidate.Index),
                IncludedInContext = contextLineIndexSet.Contains(candidate.Index)
            })
            .ToArray();
        var product = ExtractProductReference(rawLines, customerLines, contextLines, snapshot.WindowTitle);

        var averageScore = orderedCandidates.Average(candidate => candidate.Score);
        var confidence = Math.Min(1d, Math.Max(0.15d, averageScore / 10d));
        var hasReliableCustomerText = filteredCustomerLines.Count > 0;
        var status = hasReliableCustomerText
            ? confidence >= 0.65d
                ? "identified"
                : confidence >= 0.4d
                    ? "weak_match"
                    : "low_confidence"
            : "low_confidence";

        return new ConversationRecognitionResult
        {
            Status = status,
            Method = RecognitionMethodVersion,
            Confidence = Math.Round(confidence, 3, MidpointRounding.AwayFromZero),
            RecognizedCustomerText = string.Join(Environment.NewLine, filteredCustomerLines),
            RecognizedContextText = string.Join(Environment.NewLine, filteredContextLines),
            RecognizedProductId = product.ProductId,
            RecognizedProductName = product.ProductName,
            Messages = messageTurns,
            MessagesJson = JsonSerializer.Serialize(messageTurns)
        };
    }

    private static ProductReference ExtractProductReference(
        IReadOnlyList<string> rawLines,
        IReadOnlyList<string> customerLines,
        IReadOnlyList<string> contextLines,
        string windowTitle)
    {
        var candidateTexts = customerLines
            .Concat(contextLines)
            .Concat(rawLines.TakeLast(40))
            .Append(windowTitle)
            .Select(NormalizeCapturedValue)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var productId = string.Empty;
        var productName = string.Empty;
        foreach (var text in candidateTexts)
        {
            if (string.IsNullOrWhiteSpace(productId))
            {
                productId = ExtractProductId(text);
            }

            if (string.IsNullOrWhiteSpace(productName))
            {
                productName = ExtractProductName(text);
            }

            if (!string.IsNullOrWhiteSpace(productId) && !string.IsNullOrWhiteSpace(productName))
            {
                break;
            }
        }

        return new ProductReference(productId, productName);
    }

    private static IReadOnlyList<int> BuildContextLineIndexes(
        IReadOnlyList<string> rawLines,
        IReadOnlyList<RecognizedMessageCandidate> orderedCandidates,
        HashSet<int> customerCandidateIndexes,
        string platformHint)
    {
        var indexes = new SortedSet<int>();

        foreach (var index in rawLines
                     .Select((line, index) => new { line, index })
                     .Where(item => LooksLikePotentialContextLine(item.line, platformHint))
                     .Select(item => item.index)
                     .TakeLast(MaxRecentContextLines))
        {
            indexes.Add(index);
        }

        foreach (var customerIndex in customerCandidateIndexes.OrderBy(index => index))
        {
            var start = Math.Max(0, customerIndex - ContextNeighborLineRadius);
            var end = Math.Min(rawLines.Count - 1, customerIndex + ContextNeighborLineRadius);
            for (var index = start; index <= end; index++)
            {
                if (LooksLikePotentialContextLine(rawLines[index], platformHint))
                {
                    indexes.Add(index);
                }
            }
        }

        foreach (var index in orderedCandidates
                     .TakeLast(MaxRecentCandidateContextLines)
                     .Select(candidate => candidate.Index))
        {
            indexes.Add(index);
        }

        return indexes.ToArray();
    }

    private static bool LooksLikePotentialContextLine(string line, string platformHint)
    {
        var normalized = NormalizeCapturedValue(line);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (LooksLikeUiNoise(normalized, platformHint) || LooksLikeWindowControlText(normalized))
        {
            return false;
        }

        return normalized.Length >= 2 || ContainsMeaningfulText(normalized);
    }

    private static string ExtractProductId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var labeledMatch = Regex.Match(
            text,
            @"(?:商品(?:ID|编号)?|宝贝(?:ID|编号)?|货号|款号|型号|SKU|SPU|商品编码|链接ID)[：:\s#]*([A-Za-z0-9_-]{4,32})",
            RegexOptions.IgnoreCase);
        if (labeledMatch.Success)
        {
            return NormalizeOptionalValue(labeledMatch.Groups[1].Value);
        }

        return string.Empty;
    }

    private static string ExtractProductName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var labeledMatch = Regex.Match(
            text,
            @"(?:商品(?:名称)?|宝贝(?:名称)?|产品(?:名称)?|标题|名称|款式|规格)[：:\s]*([^\r\n|]{2,40})",
            RegexOptions.IgnoreCase);
        if (labeledMatch.Success)
        {
            return NormalizeProductName(labeledMatch.Groups[1].Value);
        }

        var cardLikeMatch = Regex.Match(
            text,
            @"(?:【|〖)?(?:商品|宝贝|产品)(?:】|〗)?\s*([^\r\n|]{2,40})",
            RegexOptions.IgnoreCase);
        if (cardLikeMatch.Success)
        {
            return NormalizeProductName(cardLikeMatch.Groups[1].Value);
        }

        return string.Empty;
    }

    private static string NormalizeProductName(string? value)
    {
        var normalized = NormalizeCapturedValue(value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length < 2 ||
            normalized.Length > 40 ||
            LooksLikeUiNoise(normalized, string.Empty))
        {
            return string.Empty;
        }

        return normalized;
    }

    private static HashSet<string> BuildExcludedTextSet(ConversationLogRequest request, WindowCaptureSnapshot snapshot)
    {
        var values = new[]
        {
            request.SearchKeyword,
            request.ResultMessage,
            request.PayloadText,
            request.PhraseTitle,
            request.PhraseTags,
            request.PhraseCategoryName,
            request.FileDisplayName,
            snapshot.WindowTitle,
            snapshot.WindowClassName,
            snapshot.ProcessName
        }.Concat(GetPlatformNoiseLabels(snapshot.PlatformHint));

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            foreach (var part in SplitExcludedText(value))
            {
                excluded.Add(part);
            }
        }

        return excluded;
    }

    private static IEnumerable<string> SplitExcludedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var part in value
                     .Split(['|', ',', '，', '/', '\\', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(NormalizeCapturedValue)
                     .Where(part => part.Length >= 2))
        {
            yield return part;
        }
    }

    private static CapturedConversationContext ToCapturedConversationContext(
        WindowCaptureSnapshot snapshot,
        IntPtr targetWindowHandle)
    {
        return new CapturedConversationContext
        {
            TargetWindowHandle = targetWindowHandle,
            PlatformHint = snapshot.PlatformHint,
            WindowTitle = snapshot.WindowTitle,
            WindowClassName = snapshot.WindowClassName,
            ProcessName = snapshot.ProcessName,
            ProcessPath = snapshot.ProcessPath,
            Left = snapshot.Left,
            Top = snapshot.Top,
            Width = snapshot.Width,
            Height = snapshot.Height,
            CaptureStatus = snapshot.CaptureStatus,
            CapturedText = snapshot.CapturedText,
            ScreenshotPath = snapshot.ScreenshotPath,
            RawLines = SplitCapturedLines(snapshot.CapturedText)
        };
    }

    private static WindowCaptureSnapshot ToWindowCaptureSnapshot(CapturedConversationContext capturedContext)
    {
        return new WindowCaptureSnapshot
        {
            WindowTitle = capturedContext.WindowTitle,
            WindowClassName = capturedContext.WindowClassName,
            ProcessName = capturedContext.ProcessName,
            ProcessPath = capturedContext.ProcessPath,
            PlatformHint = capturedContext.PlatformHint,
            Left = capturedContext.Left,
            Top = capturedContext.Top,
            Width = capturedContext.Width,
            Height = capturedContext.Height,
            CaptureStatus = capturedContext.CaptureStatus,
            CapturedText = capturedContext.CapturedText,
            ScreenshotPath = capturedContext.ScreenshotPath
        };
    }

    private static WindowCaptureSnapshot ToWindowCaptureSnapshot(ConversationRecommendationSnapshot snapshot)
    {
        return new WindowCaptureSnapshot
        {
            WindowTitle = snapshot.WindowTitle,
            WindowClassName = snapshot.WindowClassName,
            ProcessName = snapshot.ProcessName,
            ProcessPath = snapshot.ProcessPath,
            PlatformHint = snapshot.PlatformHint,
            CaptureStatus = snapshot.CaptureStatus,
            CapturedText = snapshot.CapturedText
        };
    }

    private static IReadOnlyList<string> SplitCapturedLines(string capturedText)
    {
        return string.IsNullOrWhiteSpace(capturedText)
            ? []
            : capturedText
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeCapturedValue)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(MaxAutomationNodes)
                .ToArray();
    }

    private static bool ShouldUseOcrFallback(WindowCaptureSnapshot snapshot)
    {
        return ShouldUseOcrFallback(
            snapshot.PlatformHint,
            snapshot.WindowTitle,
            snapshot.ProcessName,
            snapshot.CapturedText,
            hasScreenshot: !string.IsNullOrWhiteSpace(snapshot.ScreenshotPath));
    }

    internal static bool ShouldUseOcrFallback(
        string platformHint,
        string windowTitle,
        string processName,
        string capturedText,
        bool hasScreenshot)
    {
        if (!hasScreenshot)
        {
            return false;
        }

        if (platformHint is "wecom" or "wechat" or "qianniu" or "jd_workbench" or "pdd_workbench")
        {
            return true;
        }

        return !SplitCapturedLines(capturedText)
            .Any(line => IsMeaningfulConversationLine(line, platformHint, windowTitle, processName));
    }

    private static bool IsMeaningfulConversationLine(string line, WindowCaptureSnapshot snapshot)
    {
        return IsMeaningfulConversationLine(line, snapshot.PlatformHint, snapshot.WindowTitle, snapshot.ProcessName);
    }

    private static bool IsMeaningfulConversationLine(
        string line,
        string platformHint,
        string windowTitle,
        string processName)
    {
        var normalizedLine = NormalizeCapturedValue(line);
        if (string.IsNullOrWhiteSpace(normalizedLine))
        {
            return false;
        }

        if (LooksLikeUiNoise(normalizedLine, platformHint) || LooksLikeTimestamp(normalizedLine))
        {
            return false;
        }

        if (string.Equals(normalizedLine, windowTitle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedLine, processName, StringComparison.OrdinalIgnoreCase) ||
            GetPlatformNoiseLabels(platformHint).Contains(normalizedLine, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return ContainsMeaningfulText(normalizedLine);
    }

    internal static string MergeCapturedText(string existingText, string additionalText)
    {
        var mergedLines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in new[] { existingText, additionalText })
        {
            foreach (var line in SplitCapturedLines(source))
            {
                if (seen.Add(line))
                {
                    mergedLines.Add(line);
                }
            }
        }

        return string.Join(Environment.NewLine, mergedLines);
    }

    private static string BuildSearchableRecommendationText(ConversationRecommendationSnapshot snapshot)
    {
        return BuildSearchableRecommendationText(
            snapshot.RecognizedCustomerText,
            snapshot.RecognizedContextText,
            snapshot.RecognizedProductId,
            snapshot.RecognizedProductName);
    }

    private static string BuildSearchableRecommendationText(
        string customerText,
        string contextText,
        string productId,
        string productName)
    {
        return string.Join(
            Environment.NewLine,
            new[]
            {
                NormalizeOptionalValue(customerText),
                NormalizeOptionalValue(contextText),
                NormalizeOptionalValue(productId),
                NormalizeOptionalValue(productName)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static double ScoreMessageCandidate(
        string line,
        HashSet<string> excludedTexts,
        string platformHint,
        int index,
        int totalCount,
        out string reason)
    {
        var score = 0d;
        var reasons = new List<string>();
        var chineseCharacterCount = Regex.Matches(line, @"[\u4e00-\u9fff]").Count;
        var alphaNumericCharacterCount = Regex.Matches(line, @"[A-Za-z0-9]").Count;

        if (line.Length is >= 4 and <= 120)
        {
            score += 2.4d;
            reasons.Add("length_ok");
        }
        else if (line.Length is > 120 and <= 220)
        {
            score += 1.2d;
            reasons.Add("length_long");
        }
        else if (line.Length < 3)
        {
            score -= 2d;
            reasons.Add("too_short");
        }

        if (ContainsMeaningfulText(line))
        {
            score += 2.2d;
            reasons.Add("meaningful_text");
        }

        if (chineseCharacterCount >= 2)
        {
            score += 1.6d;
            reasons.Add("chinese_text");
        }

        if (chineseCharacterCount == 0 &&
            alphaNumericCharacterCount >= 4 &&
            Regex.IsMatch(line, @"^[A-Za-z0-9_\-:/#\.\s]+$"))
        {
            score -= 2.6d;
            reasons.Add("ascii_identifier_like");
        }

        if (Regex.IsMatch(line, @"^(?:id|jd_|wxid|订单号|退回单号|物流单号|工单号|会话id)[:：\s_-]*[A-Za-z0-9_-]{4,}$", RegexOptions.IgnoreCase))
        {
            score -= 3.2d;
            reasons.Add("identifier_line");
        }

        if (Regex.IsMatch(line, @"^\d{6,}$"))
        {
            score -= 3.4d;
            reasons.Add("numeric_only");
        }

        if (LooksLikeUiNoise(line, platformHint))
        {
            score -= 4.5d;
            reasons.Add("ui_noise");
        }

        if (LooksLikeTimestamp(line))
        {
            score -= 2.5d;
            reasons.Add("timestamp");
        }

        if (excludedTexts.Contains(line))
        {
            score -= 8d;
            reasons.Add("exact_excluded_text");
        }
        else if (excludedTexts.Any(item => item.Length >= 4 && line.Contains(item, StringComparison.OrdinalIgnoreCase)))
        {
            score -= 5d;
            reasons.Add("matches_reply_payload");
        }

        if (Regex.IsMatch(line, @"[。！？?!]$"))
        {
            score += 0.8d;
            reasons.Add("sentence_tail");
        }

        if (index >= Math.Max(0, totalCount - 12))
        {
            score += 0.6d;
            reasons.Add("recent_line");
        }

        if (Regex.IsMatch(line, @"(订单|退款|发货|收到|多久|怎么|为什么|可以|你好|您好|麻烦|亲|在吗|图片|视频|地址|链接|安装|售后|快递|物流|赔付|发不了)", RegexOptions.IgnoreCase))
        {
            score += 1.1d;
            reasons.Add("customer_keywords");
        }

        if (Regex.IsMatch(line, @"(退回|登记|补发|处理|安排|核实|申请|发错|拍错|少发|质量|破损|售后|退款|工单|发货|物流|快递)", RegexOptions.IgnoreCase))
        {
            score += 1.2d;
            reasons.Add("service_keywords");
        }

        reason = string.Join(",", reasons);
        return Math.Round(score, 3, MidpointRounding.AwayFromZero);
    }

    private static bool ContainsMeaningfulText(string line)
    {
        return Regex.IsMatch(line, @"[\p{L}\p{N}]{2,}") ||
               Regex.IsMatch(line, @"[\u4e00-\u9fff]{2,}");
    }

    private static bool LooksLikeTimestamp(string line)
    {
        return Regex.IsMatch(line, @"^\d{1,2}:\d{2}(:\d{2})?$") ||
               Regex.IsMatch(line, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}") ||
               Regex.IsMatch(line, @"^(昨天|今天|上午|下午|星期|周[一二三四五六日天])");
    }

    internal static bool LooksLikeUiNoise(string line, string platformHint)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        if (Regex.IsMatch(line, @"^[\W_]+$"))
        {
            return true;
        }

        if (Regex.IsMatch(line, @"^(发送|关闭|搜索|设置|刷新|返回|更多|复制|粘贴|保存|取消|确定|上传|下载|历史消息|聊天记录|快捷短语|文件搜索|工作台|会话列表|订单详情|商品详情|服务记录|评价|备注|标签|输入框|消息列表|卖家工作台|商家工作台|京东工作台|客服工作台|淘宝工作台|消息中心)$", RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (LooksLikeWindowControlText(line))
        {
            return true;
        }

        if (Regex.IsMatch(line, @"^(企业微信|微信|拼多多|拼多多工作台|多多客服|京东|京东咚咚|京东工作台|京东客服|京麦|咚咚|千牛|千牛工作台|千牛接待台|旺旺|淘宝|淘宝工作台|卖家工作台|阿里旺旺|Chrome Legacy Window|Chrome Widget Host|Chromium)$", RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (platformHint is "qianniu" or "jd_workbench" or "pdd_workbench")
        {
            return Regex.IsMatch(line, @"^(接待中|排队中|已结束|机器人|转人工|催付|售后工单|订单中心|商品管理|店铺|平台|服务助手|满意度|催拍|工作台|客服工作台|消息中心|会话|商品卡片|卖家工作台|商家工作台|淘宝工作台|京东工作台|多多客服|京麦)$", RegexOptions.IgnoreCase);
        }

        if (platformHint is "wecom" or "wechat")
        {
            return Regex.IsMatch(line, @"^(企业微信|微信|通讯录|工作台|群聊|聊天信息|表情|发送给|发消息|联系人|聊天记录)$", RegexOptions.IgnoreCase);
        }

        return false;
    }

    internal static string FilterRecognizedConversationTextForDisplay(string text, double confidence, string platformHint)
    {
        var filteredLines = FilterRecognizedConversationLines(SplitCapturedLines(text), platformHint);
        if (filteredLines.Count > 0)
        {
            return string.Join(Environment.NewLine, filteredLines);
        }

        var normalized = NormalizeCapturedValue(text);
        return confidence >= 0.65d &&
               !string.IsNullOrWhiteSpace(normalized) &&
               !LooksLikeUiNoise(normalized, platformHint) &&
               !LooksLikeWindowControlText(normalized)
            ? normalized
            : string.Empty;
    }

    private static List<string> FilterRecognizedConversationLines(IEnumerable<string> lines, string platformHint)
    {
        return lines
            .Select(NormalizeCapturedValue)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !LooksLikeUiNoise(line, platformHint))
            .Where(line => !LooksLikeWindowControlText(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeWindowControlText(string line)
    {
        return Regex.IsMatch(
            NormalizeCapturedValue(line),
            @"^(?:最小化|最大化|恢复|还原|关闭|退出|置顶|展开|收起)(?:[\s,，/|、-]*(?:最小化|最大化|恢复|还原|关闭|退出|置顶|展开|收起))*$",
            RegexOptions.IgnoreCase);
    }

    private WindowCaptureSnapshot CaptureWindowSnapshot(IntPtr windowHandle, bool includeScreenshot = true)
    {
        windowHandle = NormalizeTopLevelWindow(windowHandle);
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            return WindowCaptureSnapshot.Empty("no_target_window");
        }

        var snapshot = new WindowCaptureSnapshot
        {
            WindowTitle = GetWindowTitle(windowHandle),
            WindowClassName = GetWindowClassName(windowHandle)
        };

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId != 0)
        {
            TryPopulateProcessInfo(processId, snapshot);
        }

        snapshot.PlatformHint = InferPlatformHint(snapshot.WindowTitle, snapshot.ProcessName, snapshot.ProcessPath, snapshot.WindowClassName);
        if (!NativeMethods.IsWindowVisible(windowHandle))
        {
            snapshot.CaptureStatus = "invisible_window";
            return snapshot;
        }

        if (NativeMethods.IsIconic(windowHandle))
        {
            snapshot.CaptureStatus = "minimized_window";
            return snapshot;
        }

        var isBackgroundWindow = !IsForegroundWindow(windowHandle);

        if (!NativeMethods.GetWindowRect(windowHandle, out var rect))
        {
            snapshot.CaptureStatus = "missing_rect";
            snapshot.CapturedText = CaptureAutomationText(windowHandle, snapshot.PlatformHint);
            if (isBackgroundWindow)
            {
                snapshot.CaptureStatus = AppendCaptureStatus(snapshot.CaptureStatus, "background_window");
            }
            return snapshot;
        }

        snapshot.Left = rect.Left;
        snapshot.Top = rect.Top;
        snapshot.Width = Math.Max(0, rect.Right - rect.Left);
        snapshot.Height = Math.Max(0, rect.Bottom - rect.Top);
        snapshot.CapturedText = CaptureAutomationText(
            windowHandle,
            snapshot.PlatformHint,
            snapshot.Left,
            snapshot.Top,
            snapshot.Width,
            snapshot.Height);
        snapshot.ScreenshotPath = includeScreenshot ? TryCaptureScreenshot(snapshot) : string.Empty;
        snapshot.CaptureStatus = BuildCaptureStatus(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.CapturedText))
        {
            snapshot.CaptureStatus = AppendCaptureStatus(snapshot.CaptureStatus, "empty");
        }

        if (isBackgroundWindow)
        {
            snapshot.CaptureStatus = AppendCaptureStatus(snapshot.CaptureStatus, "background_window");
        }

        return snapshot;
    }

    private Task<T> RunStaWorkerAsync<T>(Func<T> work)
    {
        var cancellationToken = _cancellationTokenSource.Token;
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "QuickReply.RecommendationCapture"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static string BuildCaptureStatus(WindowCaptureSnapshot snapshot)
    {
        var hasText = !string.IsNullOrWhiteSpace(snapshot.CapturedText);
        var hasImage = !string.IsNullOrWhiteSpace(snapshot.ScreenshotPath);

        if (hasText && hasImage)
        {
            return "text_and_screenshot";
        }

        if (hasText)
        {
            return "text_only";
        }

        if (hasImage)
        {
            return "screenshot_only";
        }

        return "capture_failed";
    }

    private static string AppendCaptureStatus(string captureStatus, string suffix)
    {
        if (string.IsNullOrWhiteSpace(captureStatus) || string.IsNullOrWhiteSpace(suffix))
        {
            return captureStatus;
        }

        return captureStatus.Contains(suffix, StringComparison.OrdinalIgnoreCase)
            ? captureStatus
            : $"{captureStatus}+{suffix}";
    }

    private static string BuildCaptureOperationMessage(WindowCaptureSnapshot snapshot)
    {
        return snapshot.CaptureStatus switch
        {
            var status when status.Contains("minimized_window", StringComparison.OrdinalIgnoreCase) => "目标聊天窗口已最小化，未执行正文抓取。",
            var status when status.Contains("invisible_window", StringComparison.OrdinalIgnoreCase) => "目标聊天窗口当前不可见，未执行正文抓取。",
            var status when status.Contains("background_window", StringComparison.OrdinalIgnoreCase) => "当前抓取的是后台窗口，识别结果可能偏弱。",
            var status when status.Contains("missing_rect", StringComparison.OrdinalIgnoreCase) => "未能读取窗口区域坐标。",
            var status when status.Contains("empty", StringComparison.OrdinalIgnoreCase) => "未抓到稳定聊天文本。",
            _ => "已抓取聊天窗口上下文。"
        };
    }

    private static string BuildOcrOperationMessage(ScreenOcrReader.OcrReadResult result)
    {
        return result.Status switch
        {
            "ocr_read" => "OCR 已补充聊天文本。",
            "ocr_empty" => "OCR 已执行，但没有识别出可用文本。",
            "ocr_timeout" => "OCR 执行超时。",
            "ocr_error" => "OCR 执行失败。",
            "host_missing" => "本机缺少可用的 PowerShell OCR 宿主。",
            "missing_image" => "OCR 输入截图不存在。",
            _ => "OCR 已结束。"
        };
    }

    private static void DeleteTemporaryScreenshot(WindowCaptureSnapshot snapshot)
    {
        var screenshotPath = snapshot.ScreenshotPath;
        if (string.IsNullOrWhiteSpace(screenshotPath))
        {
            return;
        }

        snapshot.ScreenshotPath = string.Empty;
        snapshot.CaptureStatus = RemoveScreenshotReferenceFromCaptureStatus(snapshot.CaptureStatus);

        try
        {
            if (File.Exists(screenshotPath))
            {
                File.Delete(screenshotPath);
            }
        }
        catch
        {
        }
    }

    private static string RemoveScreenshotReferenceFromCaptureStatus(string captureStatus)
    {
        if (string.IsNullOrWhiteSpace(captureStatus))
        {
            return captureStatus;
        }

        if (captureStatus.StartsWith("text_and_screenshot", StringComparison.OrdinalIgnoreCase))
        {
            return $"text_only{captureStatus["text_and_screenshot".Length..]}";
        }

        if (captureStatus.StartsWith("screenshot_only", StringComparison.OrdinalIgnoreCase))
        {
            return $"capture_failed{captureStatus["screenshot_only".Length..]}";
        }

        return captureStatus;
    }

    private static IntPtr NormalizeTopLevelWindow(IntPtr windowHandle)
    {
        return windowHandle == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
    }

    private static bool IsForegroundWindow(IntPtr windowHandle)
    {
        return NormalizeTopLevelWindow(windowHandle) == NormalizeTopLevelWindow(NativeMethods.GetForegroundWindow());
    }

    private string TryCaptureScreenshot(WindowCaptureSnapshot snapshot)
    {
        if (snapshot.Width <= 0 || snapshot.Height <= 0)
        {
            return string.Empty;
        }

        try
        {
            var dateFolder = Path.Combine(_capturesRootPath, DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dateFolder);

            var fileName = $"{DateTime.Now:HHmmssfff}_{Guid.NewGuid():N}.png";
            var outputPath = Path.Combine(dateFolder, fileName);

            using var bitmap = new Bitmap(snapshot.Width, snapshot.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(snapshot.Left, snapshot.Top, 0, 0, new Size(snapshot.Width, snapshot.Height));
            bitmap.Save(outputPath, ImageFormat.Png);
            return outputPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CaptureAutomationText(
        IntPtr windowHandle,
        string platformHint = "",
        int windowLeft = 0,
        int windowTop = 0,
        int windowWidth = 0,
        int windowHeight = 0)
    {
        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (root is null)
            {
                return string.Empty;
            }

            var entries = new List<AutomationTextEntry>();
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue((root, 0));

            while (queue.Count > 0 && entries.Count < MaxAutomationNodes)
            {
                var (element, depth) = queue.Dequeue();
                AppendElementText(element, entries, seen);

                if (depth >= MaxAutomationDepth)
                {
                    continue;
                }

                AutomationElementCollection children;
                try
                {
                    children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
                }
                catch
                {
                    continue;
                }

                foreach (AutomationElement child in children)
                {
                    queue.Enqueue((child, depth + 1));
                    if (queue.Count + entries.Count >= MaxAutomationNodes)
                    {
                        break;
                    }
                }
            }

            var orderedEntries = ShouldUsePreferredAutomationOrdering(platformHint)
                ? entries
                    .Where(entry => ShouldIncludeAutomationEntry(entry, platformHint, windowLeft, windowTop, windowWidth, windowHeight))
                    .OrderBy(entry => entry.Top)
                    .ThenBy(entry => entry.Left)
                    .ThenBy(entry => entry.Sequence)
                : entries.OrderBy(entry => entry.Sequence);

            var lines = new List<string>();
            string? previousLine = null;
            foreach (var entry in orderedEntries)
            {
                if (string.Equals(previousLine, entry.Text, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lines.Add(entry.Text);
                previousLine = entry.Text;
            }

            var text = string.Join(Environment.NewLine, lines);
            return text.Length > MaxCapturedTextLength
                ? text[..MaxCapturedTextLength]
                : text;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AppendElementText(AutomationElement element, ICollection<AutomationTextEntry> entries, ISet<string> seen)
    {
        var bounds = TryGetAutomationBounds(element);
        var controlTypeName = TryGetAutomationControlTypeName(element);
        var isOffscreen = TryGetAutomationIsOffscreen(element);

        void AddValue(string? value)
        {
            foreach (var normalizedValue in SplitAutomationTextValue(value))
            {
                var entry = new AutomationTextEntry
                {
                    Sequence = entries.Count,
                    Text = normalizedValue,
                    Left = bounds?.Left ?? double.MaxValue,
                    Top = bounds?.Top ?? double.MaxValue,
                    Width = bounds?.Width ?? 0d,
                    Height = bounds?.Height ?? 0d,
                    IsOffscreen = isOffscreen,
                    ControlTypeName = controlTypeName
                };
                if (!seen.Add(BuildAutomationEntryDedupKey(entry)))
                {
                    continue;
                }

                entries.Add(entry);
            }
        }

        try
        {
            AddValue(element.Current.Name);
        }
        catch
        {
        }

        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern)
            {
                AddValue(valuePattern.Current.Value);
            }
        }
        catch
        {
        }

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                AddValue(textPattern.DocumentRange.GetText(-1));
            }
        }
        catch
        {
        }
    }

    private static IEnumerable<string> SplitAutomationTextValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var line in value
                     .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(NormalizeCapturedValue)
                     .Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            yield return line;
        }
    }

    internal static IReadOnlyList<string> SplitAutomationTextValueForTesting(string value)
    {
        return SplitAutomationTextValue(value).ToArray();
    }

    internal static bool IsWithinPreferredAutomationRegionForTesting(
        string platformHint,
        double elementLeft,
        double elementTop,
        double elementWidth,
        double elementHeight,
        double windowLeft,
        double windowTop,
        double windowWidth,
        double windowHeight)
    {
        return IsWithinPreferredAutomationRegion(
            platformHint,
            elementLeft,
            elementTop,
            elementWidth,
            elementHeight,
            windowLeft,
            windowTop,
            windowWidth,
            windowHeight);
    }

    private static string BuildAutomationEntryDedupKey(AutomationTextEntry entry)
    {
        var top = double.IsFinite(entry.Top) ? Math.Round(entry.Top, 0, MidpointRounding.AwayFromZero) : -1d;
        var left = double.IsFinite(entry.Left) ? Math.Round(entry.Left, 0, MidpointRounding.AwayFromZero) : -1d;
        return $"{entry.ControlTypeName}\u001f{top}\u001f{left}\u001f{entry.Text}";
    }

    private static System.Windows.Rect? TryGetAutomationBounds(AutomationElement element)
    {
        try
        {
            var bounds = element.Current.BoundingRectangle;
            return bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1
                ? null
                : bounds;
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetAutomationControlTypeName(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType?.ProgrammaticName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryGetAutomationIsOffscreen(AutomationElement element)
    {
        try
        {
            return element.Current.IsOffscreen;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldUsePreferredAutomationOrdering(string platformHint)
    {
        return platformHint is "jd_workbench";
    }

    private static bool ShouldIncludeAutomationEntry(
        AutomationTextEntry entry,
        string platformHint,
        double windowLeft,
        double windowTop,
        double windowWidth,
        double windowHeight)
    {
        if (entry.IsOffscreen)
        {
            return false;
        }

        if (!ShouldUsePreferredAutomationOrdering(platformHint))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(entry.Text))
        {
            return false;
        }

        if (IsChromeAutomationControlType(entry.ControlTypeName))
        {
            return false;
        }

        return IsWithinPreferredAutomationRegion(
            platformHint,
            entry.Left,
            entry.Top,
            entry.Width,
            entry.Height,
            windowLeft,
            windowTop,
            windowWidth,
            windowHeight);
    }

    private static bool IsChromeAutomationControlType(string controlTypeName)
    {
        return controlTypeName is
            "ControlType.Button" or
            "ControlType.Edit" or
            "ControlType.ComboBox" or
            "ControlType.MenuItem" or
            "ControlType.Tab" or
            "ControlType.TabItem" or
            "ControlType.ToolBar" or
            "ControlType.Header" or
            "ControlType.ScrollBar" or
            "ControlType.TitleBar" or
            "ControlType.StatusBar";
    }

    private static bool IsWithinPreferredAutomationRegion(
        string platformHint,
        double elementLeft,
        double elementTop,
        double elementWidth,
        double elementHeight,
        double windowLeft,
        double windowTop,
        double windowWidth,
        double windowHeight)
    {
        var region = ResolvePreferredAutomationRegion(platformHint);
        if (region is null || windowWidth <= 0 || windowHeight <= 0)
        {
            return true;
        }

        if (!double.IsFinite(elementLeft) ||
            !double.IsFinite(elementTop) ||
            elementWidth <= 0 ||
            elementHeight <= 0)
        {
            return false;
        }

        var centerX = ((elementLeft + (elementWidth / 2d)) - windowLeft) / windowWidth;
        var centerY = ((elementTop + (elementHeight / 2d)) - windowTop) / windowHeight;

        return centerX >= region.Value.X &&
               centerX <= region.Value.X + region.Value.Width &&
               centerY >= region.Value.Y &&
               centerY <= region.Value.Y + region.Value.Height;
    }

    private static (double X, double Y, double Width, double Height)? ResolvePreferredAutomationRegion(string platformHint)
    {
        return platformHint switch
        {
            "jd_workbench" => (0.18d, 0.10d, 0.56d, 0.78d),
            _ => null
        };
    }

    private static string NormalizeCapturedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized;
    }

    private static void TryPopulateProcessInfo(uint processId, WindowCaptureSnapshot snapshot)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            snapshot.ProcessName = NormalizeOptionalValue(process.ProcessName);

            try
            {
                snapshot.ProcessPath = NormalizeOptionalValue(process.MainModule?.FileName);
            }
            catch
            {
                snapshot.ProcessPath = string.Empty;
            }
        }
        catch
        {
            snapshot.ProcessName = string.Empty;
            snapshot.ProcessPath = string.Empty;
        }
    }

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        var length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(windowHandle, builder, builder.Capacity);
        return NormalizeOptionalValue(builder.ToString());
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var builder = new StringBuilder(256);
        return NativeMethods.GetClassName(windowHandle, builder, builder.Capacity) > 0
            ? NormalizeOptionalValue(builder.ToString())
            : string.Empty;
    }

    internal static string InferPlatformHint(string windowTitle, string processName, string processPath, string windowClassName)
    {
        var combined = string.Join(
            " ",
            new[]
            {
                windowTitle,
                processName,
                processPath,
                windowClassName
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (combined.Contains("千牛", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("qianniu", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("旺旺", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("淘宝", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("taobao", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("卖家工作台", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("阿里旺旺", StringComparison.OrdinalIgnoreCase))
        {
            return "qianniu";
        }

        if (combined.Contains("京东", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("jingdong", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("jd", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("京麦", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("jingmai", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("咚咚", StringComparison.OrdinalIgnoreCase))
        {
            return "jd_workbench";
        }

        if (combined.Contains("拼多多", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("pinduoduo", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("pdd", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("多多客服", StringComparison.OrdinalIgnoreCase))
        {
            return "pdd_workbench";
        }

        if (combined.Contains("企业微信", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("wecom", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("wxwork", StringComparison.OrdinalIgnoreCase))
        {
            return "wecom";
        }

        if (combined.Contains("微信", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("wechat", StringComparison.OrdinalIgnoreCase))
        {
            return "wechat";
        }

        return "unknown";
    }

    private static IReadOnlyList<string> GetPlatformNoiseLabels(string platformHint)
    {
        return platformHint switch
        {
            "wecom" => ["企业微信", "WeCom"],
            "wechat" => ["微信", "WeChat"],
            "qianniu" => ["千牛", "千牛工作台", "千牛接待台", "旺旺", "淘宝", "淘宝工作台", "卖家工作台", "阿里旺旺"],
            "jd_workbench" => ["京东", "京东咚咚", "咚咚", "京麦", "京东工作台", "京东客服"],
            "pdd_workbench" => ["拼多多", "拼多多工作台", "多多客服", "商家工作台", "Chrome Legacy Window"],
            _ => []
        };
    }

    private async Task PersistRecommendationsShownAsync(
        Guid sessionId,
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<PhraseRecommendationSuggestion> suggestions,
        SearchOverlayMode mode,
        string? searchKeyword)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        foreach (var suggestion in suggestions)
        {
            await InsertRecommendationEventAsync(
                connection,
                sessionId,
                "shown",
                snapshot,
                mode,
                searchKeyword,
                ComposeActionMode.Copy,
                true,
                recommendationKind: suggestion.RecommendationKind,
                suggestion: suggestion).ConfigureAwait(false);
        }
    }

    private async Task PersistRecommendationAcceptedAsync(
        Guid sessionId,
        PhraseRecommendationSuggestion suggestion,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess)
    {
        var snapshot = new ConversationRecommendationSnapshot
        {
            RecognizedCustomerText = suggestion.RecognizedCustomerText
        };

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        await InsertRecommendationEventAsync(
            connection,
            sessionId,
            "accepted",
            snapshot,
            mode,
            searchKeyword,
            actionMode,
            resultSuccess,
            recommendationKind: suggestion.RecommendationKind,
            suggestion: suggestion).ConfigureAwait(false);
    }

    private async Task PersistKnowledgeCardsShownAsync(
        Guid sessionId,
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<KnowledgeCardSuggestion> knowledgeCards,
        SearchOverlayMode mode,
        string? searchKeyword)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        foreach (var knowledgeCard in knowledgeCards)
        {
            await InsertRecommendationEventAsync(
                connection,
                sessionId,
                "shown",
                snapshot,
                mode,
                searchKeyword,
                ComposeActionMode.Copy,
                true,
                recommendationKind: knowledgeCard.RecommendationKind,
                knowledgeCard: knowledgeCard).ConfigureAwait(false);
        }
    }

    private async Task PersistKnowledgeCardAcceptedAsync(
        Guid sessionId,
        KnowledgeCardSuggestion knowledgeCard,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess)
    {
        var snapshot = new ConversationRecommendationSnapshot();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        await InsertRecommendationEventAsync(
            connection,
            sessionId,
            "accepted",
            snapshot,
            mode,
            searchKeyword,
            actionMode,
            resultSuccess,
            recommendationKind: knowledgeCard.RecommendationKind,
            knowledgeCard: knowledgeCard).ConfigureAwait(false);
    }

    private async Task InsertRecommendationEventAsync(
        SqliteConnection connection,
        Guid sessionId,
        string eventType,
        ConversationRecommendationSnapshot snapshot,
        SearchOverlayMode mode,
        string? searchKeyword,
        ComposeActionMode actionMode,
        bool resultSuccess,
        string recommendationKind,
        PhraseRecommendationSuggestion? suggestion = null,
        KnowledgeCardSuggestion? knowledgeCard = null,
        GeneratedReplyCandidate? draft = null,
        string? providerType = null,
        string? modelName = null,
        long latencyMs = 0,
        int groundingPhraseCount = 0,
        int groundingKnowledgeCount = 0,
        string? groundingKnowledgeIdsJson = null,
        string? fallbackReason = null,
        int generationCharacterCount = 0)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recommendation_events (
                id,
                occurred_at,
                recommendation_session_id,
                event_type,
                search_mode,
                search_keyword,
                action_mode,
                result_success,
                phrase_id,
                phrase_title,
                phrase_category_name,
                display_rank,
                matched_keyword,
                match_count,
                sample_count,
                confidence,
                recognized_customer_text,
                platform_hint,
                window_title,
                knowledge_card_id,
                knowledge_card_question,
                knowledge_card_candidate_kind,
                actual_payload_text,
                recommendation_kind,
                provider_type,
                model_name,
                latency_ms,
                grounding_phrase_count,
                grounding_knowledge_count,
                grounding_knowledge_ids_json,
                knowledge_hit,
                fallback_reason,
                generation_character_count,
                app_version,
                recommendation_rule_version
            )
            VALUES (
                $id,
                $occurredAt,
                $sessionId,
                $eventType,
                $searchMode,
                $searchKeyword,
                $actionMode,
                $resultSuccess,
                $phraseId,
                $phraseTitle,
                $phraseCategoryName,
                $displayRank,
                $matchedKeyword,
                $matchCount,
                $sampleCount,
                $confidence,
                $recognizedCustomerText,
                $platformHint,
                $windowTitle,
                $knowledgeCardId,
                $knowledgeCardQuestion,
                $knowledgeCardCandidateKind,
                $actualPayloadText,
                $recommendationKind,
                $providerType,
                $modelName,
                $latencyMs,
                $groundingPhraseCount,
                $groundingKnowledgeCount,
                $groundingKnowledgeIdsJson,
                $knowledgeHit,
                $fallbackReason,
                $generationCharacterCount,
                $appVersion,
                $recommendationRuleVersion
            );
            """;

        var phrase = suggestion?.Phrase;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$searchMode", mode.ToString());
        command.Parameters.AddWithValue("$searchKeyword", NormalizeOptionalValue(searchKeyword));
        command.Parameters.AddWithValue("$actionMode", actionMode.ToString());
        command.Parameters.AddWithValue("$resultSuccess", resultSuccess ? 1 : 0);
        command.Parameters.AddWithValue("$phraseId", phrase?.Id.ToString() ?? NormalizeOptionalValue(draft?.BackingPhraseId?.ToString()));
        command.Parameters.AddWithValue("$phraseTitle", NormalizeOptionalValue(phrase?.Title ?? draft?.BackingPhraseTitle));
        command.Parameters.AddWithValue("$phraseCategoryName", NormalizeOptionalValue(phrase?.CategoryName));
        command.Parameters.AddWithValue("$displayRank", suggestion?.DisplayRank ?? draft?.DisplayRank ?? 0);
        command.Parameters.AddWithValue("$matchedKeyword", NormalizeOptionalValue(suggestion?.MatchedKeyword));
        command.Parameters.AddWithValue("$matchCount", suggestion?.MatchCount ?? 0);
        command.Parameters.AddWithValue("$sampleCount", suggestion?.SampleCount ?? 0);
        command.Parameters.AddWithValue("$confidence", suggestion?.Confidence ?? draft?.Confidence ?? 0d);
        command.Parameters.AddWithValue("$recognizedCustomerText", NormalizeOptionalValue(snapshot.RecognizedCustomerText));
        command.Parameters.AddWithValue("$platformHint", NormalizeOptionalValue(snapshot.PlatformHint));
        command.Parameters.AddWithValue("$windowTitle", NormalizeOptionalValue(snapshot.WindowTitle));
        command.Parameters.AddWithValue("$knowledgeCardId", NormalizeOptionalValue(knowledgeCard?.Card.Id.ToString()));
        command.Parameters.AddWithValue("$knowledgeCardQuestion", NormalizeOptionalValue(knowledgeCard?.Card.Question));
        command.Parameters.AddWithValue("$knowledgeCardCandidateKind", NormalizeOptionalValue(knowledgeCard?.Card.CandidateKind));
        command.Parameters.AddWithValue("$actualPayloadText", NormalizeOptionalValue(phrase?.Content ?? knowledgeCard?.Card.Answer ?? draft?.Text));
        command.Parameters.AddWithValue("$recommendationKind", NormalizeOptionalValue(recommendationKind));
        command.Parameters.AddWithValue("$providerType", NormalizeOptionalValue(providerType ?? draft?.ProviderType));
        command.Parameters.AddWithValue("$modelName", NormalizeOptionalValue(modelName ?? draft?.ModelName));
        command.Parameters.AddWithValue("$latencyMs", Math.Max(0, latencyMs > 0 ? latencyMs : draft?.LatencyMs ?? 0));
        command.Parameters.AddWithValue("$groundingPhraseCount", Math.Max(0, groundingPhraseCount > 0 ? groundingPhraseCount : draft?.GroundingPhraseCount ?? 0));
        var effectiveGroundingKnowledgeCount = Math.Max(0, groundingKnowledgeCount > 0 ? groundingKnowledgeCount : draft?.GroundingKnowledgeCount ?? 0);
        if (knowledgeCard is not null)
        {
            effectiveGroundingKnowledgeCount = Math.Max(effectiveGroundingKnowledgeCount, 1);
        }

        var effectiveGroundingKnowledgeIdsJson = groundingKnowledgeIdsJson ?? draft?.GroundingKnowledgeIdsJson;
        if (knowledgeCard is not null)
        {
            effectiveGroundingKnowledgeIdsJson = JsonSerializer.Serialize(new[] { knowledgeCard.Card.Id });
        }

        command.Parameters.AddWithValue("$groundingKnowledgeCount", effectiveGroundingKnowledgeCount);
        command.Parameters.AddWithValue("$groundingKnowledgeIdsJson", NormalizeOptionalValue(effectiveGroundingKnowledgeIdsJson));
        command.Parameters.AddWithValue("$knowledgeHit", effectiveGroundingKnowledgeCount > 0 ? 1 : 0);
        command.Parameters.AddWithValue("$fallbackReason", NormalizeOptionalValue(fallbackReason ?? draft?.FallbackReason));
        command.Parameters.AddWithValue("$generationCharacterCount", Math.Max(0, generationCharacterCount > 0 ? generationCharacterCount : draft?.CharacterCount ?? 0));
        command.Parameters.AddWithValue("$appVersion", GetAppVersion());
        command.Parameters.AddWithValue("$recommendationRuleVersion", string.Equals(recommendationKind, "ai_draft", StringComparison.OrdinalIgnoreCase)
            ? AiDraftRuleVersion
            : RecommendationRuleVersion);
        await command.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
    }

    private async Task PersistAiDraftsShownAsync(
        Guid sessionId,
        ConversationRecommendationSnapshot snapshot,
        IReadOnlyList<GeneratedReplyCandidate> drafts,
        SearchOverlayMode mode,
        string? searchKeyword)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        foreach (var draft in drafts)
        {
            await InsertRecommendationEventAsync(
                connection,
                sessionId,
                "shown",
                snapshot,
                mode,
                searchKeyword,
                ComposeActionMode.Copy,
                true,
                recommendationKind: "ai_draft",
                draft: draft).ConfigureAwait(false);
        }
    }

    private async Task PersistAiDraftActionAsync(
        Guid sessionId,
        GeneratedReplyCandidate draft,
        SearchOverlayMode mode,
        string? searchKeyword,
        string eventType,
        ComposeActionMode actionMode,
        bool resultSuccess)
    {
        var snapshot = new ConversationRecommendationSnapshot();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        await InsertRecommendationEventAsync(
            connection,
            sessionId,
            eventType,
            snapshot,
            mode,
            searchKeyword,
            actionMode,
            resultSuccess,
            recommendationKind: "ai_draft",
            draft: draft).ConfigureAwait(false);
    }

    private async Task PersistAiDraftStatusAsync(
        ConversationRecommendationSnapshot snapshot,
        SearchOverlayMode mode,
        string? searchKeyword,
        AiDraftGenerationStatus status)
    {
        var eventType = status.State switch
        {
            AiDraftGenerationState.ProviderFailed => "provider_failed",
            AiDraftGenerationState.ContextSkipped => "context_skipped",
            AiDraftGenerationState.Disabled => "disabled",
            _ => "status"
        };

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        await InsertRecommendationEventAsync(
            connection,
            Guid.Empty,
            eventType,
            snapshot,
            mode,
            searchKeyword,
            ComposeActionMode.Copy,
            status.State != AiDraftGenerationState.ProviderFailed,
            recommendationKind: "ai_draft",
            providerType: status.ProviderType,
            modelName: status.ModelName,
            latencyMs: status.LatencyMs,
            groundingPhraseCount: status.GroundingPhraseCount,
            groundingKnowledgeCount: status.GroundingKnowledgeCount,
            fallbackReason: status.FallbackReason,
            generationCharacterCount: status.CharacterCount).ConfigureAwait(false);
    }

    private async Task InitializeSchemaAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conversation_logs (
                id TEXT PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                trigger_source TEXT NOT NULL,
                action_mode TEXT NOT NULL,
                payload_type TEXT NOT NULL,
                search_keyword TEXT NOT NULL DEFAULT '',
                result_success INTEGER NOT NULL DEFAULT 0,
                result_message TEXT NOT NULL DEFAULT '',
                target_window_handle TEXT NOT NULL DEFAULT '',
                target_window_title TEXT NOT NULL DEFAULT '',
                target_window_class_name TEXT NOT NULL DEFAULT '',
                target_process_name TEXT NOT NULL DEFAULT '',
                target_process_path TEXT NOT NULL DEFAULT '',
                platform_hint TEXT NOT NULL DEFAULT '',
                target_left INTEGER NOT NULL DEFAULT 0,
                target_top INTEGER NOT NULL DEFAULT 0,
                target_width INTEGER NOT NULL DEFAULT 0,
                target_height INTEGER NOT NULL DEFAULT 0,
                capture_status TEXT NOT NULL DEFAULT '',
                captured_text TEXT NOT NULL DEFAULT '',
                screenshot_path TEXT NOT NULL DEFAULT '',
                phrase_id TEXT NOT NULL DEFAULT '',
                phrase_title TEXT NOT NULL DEFAULT '',
                phrase_tags TEXT NOT NULL DEFAULT '',
                phrase_category_name TEXT NOT NULL DEFAULT '',
                file_item_id TEXT NOT NULL DEFAULT '',
                file_display_name TEXT NOT NULL DEFAULT '',
                file_paths_json TEXT NOT NULL DEFAULT '',
                payload_text TEXT NOT NULL DEFAULT '',
                recognition_status TEXT NOT NULL DEFAULT '',
                recognition_method TEXT NOT NULL DEFAULT '',
                recognition_confidence REAL NOT NULL DEFAULT 0,
                recognized_customer_text TEXT NOT NULL DEFAULT '',
                recognized_context_text TEXT NOT NULL DEFAULT '',
                recognized_product_id TEXT NOT NULL DEFAULT '',
                recognized_product_name TEXT NOT NULL DEFAULT '',
                recognized_messages_json TEXT NOT NULL DEFAULT '',
                reply_group_id TEXT NOT NULL DEFAULT '',
                reply_group_index INTEGER NOT NULL DEFAULT 0,
                data_quality_grade TEXT NOT NULL DEFAULT '',
                app_version TEXT NOT NULL DEFAULT '',
                recommendation_rule_version TEXT NOT NULL DEFAULT '',
                compose_host_profile TEXT NOT NULL DEFAULT '',
                compose_attempt_count INTEGER NOT NULL DEFAULT 1,
                compose_retry_triggered INTEGER NOT NULL DEFAULT 0,
                compose_retry_reason TEXT NOT NULL DEFAULT '',
                compose_retry_outcome TEXT NOT NULL DEFAULT '',
                compose_failure_category TEXT NOT NULL DEFAULT '',
                compose_target_changed_before_retry INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_conversation_logs_occurred_at
            ON conversation_logs(occurred_at DESC);

            CREATE INDEX IF NOT EXISTS idx_conversation_logs_platform_hint
            ON conversation_logs(platform_hint, occurred_at DESC);

            CREATE TABLE IF NOT EXISTS recommendation_events (
                id TEXT PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                recommendation_session_id TEXT NOT NULL DEFAULT '',
                event_type TEXT NOT NULL DEFAULT '',
                search_mode TEXT NOT NULL DEFAULT '',
                search_keyword TEXT NOT NULL DEFAULT '',
                action_mode TEXT NOT NULL DEFAULT '',
                result_success INTEGER NOT NULL DEFAULT 0,
                phrase_id TEXT NOT NULL DEFAULT '',
                phrase_title TEXT NOT NULL DEFAULT '',
                phrase_category_name TEXT NOT NULL DEFAULT '',
                display_rank INTEGER NOT NULL DEFAULT 0,
                matched_keyword TEXT NOT NULL DEFAULT '',
                match_count INTEGER NOT NULL DEFAULT 0,
                sample_count INTEGER NOT NULL DEFAULT 0,
                confidence REAL NOT NULL DEFAULT 0,
                recognized_customer_text TEXT NOT NULL DEFAULT '',
                platform_hint TEXT NOT NULL DEFAULT '',
                window_title TEXT NOT NULL DEFAULT '',
                knowledge_card_id TEXT NOT NULL DEFAULT '',
                knowledge_card_question TEXT NOT NULL DEFAULT '',
                knowledge_card_candidate_kind TEXT NOT NULL DEFAULT '',
                actual_payload_text TEXT NOT NULL DEFAULT '',
                recommendation_kind TEXT NOT NULL DEFAULT '',
                provider_type TEXT NOT NULL DEFAULT '',
                model_name TEXT NOT NULL DEFAULT '',
                latency_ms INTEGER NOT NULL DEFAULT 0,
                grounding_phrase_count INTEGER NOT NULL DEFAULT 0,
                grounding_knowledge_count INTEGER NOT NULL DEFAULT 0,
                grounding_knowledge_ids_json TEXT NOT NULL DEFAULT '',
                knowledge_hit INTEGER NOT NULL DEFAULT 0,
                fallback_reason TEXT NOT NULL DEFAULT '',
                generation_character_count INTEGER NOT NULL DEFAULT 0,
                app_version TEXT NOT NULL DEFAULT '',
                recommendation_rule_version TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_recommendation_events_occurred_at
            ON recommendation_events(occurred_at DESC);

            CREATE INDEX IF NOT EXISTS idx_recommendation_events_phrase_id
            ON recommendation_events(phrase_id, occurred_at DESC);

            CREATE TABLE IF NOT EXISTS recognition_corrections (
                id TEXT PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                recommendation_session_id TEXT NOT NULL DEFAULT '',
                platform_hint TEXT NOT NULL DEFAULT '',
                window_title TEXT NOT NULL DEFAULT '',
                source_customer_text TEXT NOT NULL DEFAULT '',
                corrected_customer_text TEXT NOT NULL DEFAULT '',
                source_context_text TEXT NOT NULL DEFAULT '',
                source_product_id TEXT NOT NULL DEFAULT '',
                source_product_name TEXT NOT NULL DEFAULT '',
                source_messages_json TEXT NOT NULL DEFAULT '',
                corrected_context_text TEXT NOT NULL DEFAULT '',
                corrected_product_id TEXT NOT NULL DEFAULT '',
                corrected_product_name TEXT NOT NULL DEFAULT '',
                corrected_messages_json TEXT NOT NULL DEFAULT '',
                recognition_method TEXT NOT NULL DEFAULT '',
                app_version TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_recognition_corrections_occurred_at
            ON recognition_corrections(occurred_at DESC);

            CREATE TABLE IF NOT EXISTS operation_events (
                id TEXT PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                operation_name TEXT NOT NULL,
                stage TEXT NOT NULL DEFAULT '',
                success INTEGER NOT NULL DEFAULT 0,
                skipped INTEGER NOT NULL DEFAULT 0,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                message TEXT NOT NULL DEFAULT '',
                detail_json TEXT NOT NULL DEFAULT '',
                app_version TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_operation_events_occurred_at
            ON operation_events(occurred_at DESC);

            CREATE INDEX IF NOT EXISTS idx_operation_events_name
            ON operation_events(operation_name, occurred_at DESC);
            """;

        await command.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recognition_status", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recognition_method", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recognition_confidence", "REAL NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recognized_customer_text", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recognized_context_text", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recognized_product_id", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recognized_product_name", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recognized_messages_json", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "reply_group_id", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "reply_group_index", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "data_quality_grade", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "app_version", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "recommendation_rule_version", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "compose_host_profile", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "compose_attempt_count", "INTEGER NOT NULL DEFAULT 1").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "compose_retry_triggered", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "compose_retry_reason", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "compose_retry_outcome", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "compose_failure_category", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureConversationLogColumnAsync(connection, "compose_target_changed_before_retry", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "knowledge_card_id", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "knowledge_card_question", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "knowledge_card_candidate_kind", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "actual_payload_text", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "recommendation_kind", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "provider_type", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "model_name", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "latency_ms", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "grounding_phrase_count", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "grounding_knowledge_count", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "grounding_knowledge_ids_json", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "knowledge_hit", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "fallback_reason", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "generation_character_count", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "app_version", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecommendationEventColumnAsync(connection, "recommendation_rule_version", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecognitionCorrectionColumnAsync(connection, "source_context_text", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecognitionCorrectionColumnAsync(connection, "source_product_id", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecognitionCorrectionColumnAsync(connection, "source_product_name", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecognitionCorrectionColumnAsync(connection, "source_messages_json", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecognitionCorrectionColumnAsync(connection, "corrected_context_text", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecognitionCorrectionColumnAsync(connection, "corrected_product_id", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecognitionCorrectionColumnAsync(connection, "corrected_product_name", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureRecognitionCorrectionColumnAsync(connection, "corrected_messages_json", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureOperationEventColumnAsync(connection, "stage", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureOperationEventColumnAsync(connection, "success", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureOperationEventColumnAsync(connection, "skipped", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureOperationEventColumnAsync(connection, "duration_ms", "INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        await EnsureOperationEventColumnAsync(connection, "message", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureOperationEventColumnAsync(connection, "detail_json", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
        await EnsureOperationEventColumnAsync(connection, "app_version", "TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
    }

    private async Task EnsureConversationLogColumnAsync(SqliteConnection connection, string columnName, string definition)
    {
        await using var infoCommand = connection.CreateCommand();
        infoCommand.CommandText = "PRAGMA table_info(conversation_logs);";

        var hasColumn = false;
        await using (var reader = await infoCommand.ExecuteReaderAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE conversation_logs ADD COLUMN {columnName} {definition};";
        await alterCommand.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
    }

    private async Task EnsureRecommendationEventColumnAsync(SqliteConnection connection, string columnName, string definition)
    {
        await using var infoCommand = connection.CreateCommand();
        infoCommand.CommandText = "PRAGMA table_info(recommendation_events);";

        var hasColumn = false;
        await using (var reader = await infoCommand.ExecuteReaderAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE recommendation_events ADD COLUMN {columnName} {definition};";
        await alterCommand.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
    }

    private async Task EnsureRecognitionCorrectionColumnAsync(SqliteConnection connection, string columnName, string definition)
    {
        await using var infoCommand = connection.CreateCommand();
        infoCommand.CommandText = "PRAGMA table_info(recognition_corrections);";

        var hasColumn = false;
        await using (var reader = await infoCommand.ExecuteReaderAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE recognition_corrections ADD COLUMN {columnName} {definition};";
        try
        {
            await alterCommand.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                                         ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private async Task EnsureOperationEventColumnAsync(SqliteConnection connection, string columnName, string definition)
    {
        await using var infoCommand = connection.CreateCommand();
        infoCommand.CommandText = "PRAGMA table_info(operation_events);";

        var hasColumn = false;
        await using (var reader = await infoCommand.ExecuteReaderAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE operation_events ADD COLUMN {columnName} {definition};";
        await alterCommand.ExecuteNonQueryAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
    }

    private static string NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
    }

    private static bool IsCjkRune(Rune rune)
    {
        return rune.Value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x2A6DF or
            >= 0x2A700 and <= 0x2B73F or
            >= 0x2B740 and <= 0x2B81F or
            >= 0x2B820 and <= 0x2CEAF or
            >= 0x2CEB0 and <= 0x2EBEF or
            >= 0x30000 and <= 0x3134F;
    }

    private async Task<ReplyGroupResolution> ResolveReplyGroupAsync(
        SqliteConnection connection,
        ConversationLogRequest request,
        WindowCaptureSnapshot snapshot,
        ConversationRecognitionResult recognition)
    {
        if (string.IsNullOrWhiteSpace(recognition.RecognizedCustomerText))
        {
            return new ReplyGroupResolution(Guid.NewGuid(), 1);
        }

        var threshold = request.OccurredAt.AddSeconds(-ReplyGroupWindowSeconds).ToString("O", CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reply_group_id,
                   reply_group_index,
                   recognized_customer_text,
                   recognized_context_text
            FROM conversation_logs
            WHERE occurred_at >= $threshold
              AND platform_hint = $platformHint
              AND target_window_title = $windowTitle
            ORDER BY occurred_at DESC
            LIMIT 8;
            """;
        command.Parameters.AddWithValue("$threshold", threshold);
        command.Parameters.AddWithValue("$platformHint", snapshot.PlatformHint);
        command.Parameters.AddWithValue("$windowTitle", snapshot.WindowTitle);

        await using var reader = await command.ExecuteReaderAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
        var currentText = BuildSearchableRecommendationText(
            recognition.RecognizedCustomerText,
            recognition.RecognizedContextText,
            recognition.RecognizedProductId,
            recognition.RecognizedProductName);
        var bestMatchGroupId = Guid.Empty;
        var bestMatchIndex = 0;
        var bestSimilarity = 0d;
        while (await reader.ReadAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
        {
            var groupText = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var lastIndex = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            if (!Guid.TryParse(groupText, out var groupId))
            {
                continue;
            }

            var historicalText = BuildSearchableRecommendationText(
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                string.Empty,
                string.Empty);
            var similarity = ComputeLooseTextSimilarity(currentText, historicalText);
            if (similarity >= 0.55d && similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestMatchGroupId = groupId;
                bestMatchIndex = lastIndex;
            }
        }

        return bestMatchGroupId != Guid.Empty
            ? new ReplyGroupResolution(bestMatchGroupId, bestMatchIndex + 1)
            : new ReplyGroupResolution(Guid.NewGuid(), 1);
    }

    private static string DetermineQualityGrade(ConversationLogRequest request, ConversationRecognitionResult recognition)
    {
        if (!request.ResultSuccess || string.IsNullOrWhiteSpace(recognition.RecognizedCustomerText))
        {
            return "low";
        }

        if (recognition.Confidence >= 0.75d && recognition.RecognizedCustomerText.Length >= 8)
        {
            return "high";
        }

        if (recognition.Confidence >= 0.45d)
        {
            return "normal";
        }

        return "low";
    }

    private static string GetAppVersion()
    {
        return typeof(ConversationLoggingService).Assembly.GetName().Version?.ToString() ?? string.Empty;
    }

    private async Task<int> GetRecommendationSampleCountAsync(
        SqliteConnection connection,
        string platformHint,
        string keyword,
        RecommendationQueryScope? queryScope)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1)
            FROM conversation_logs
            WHERE recognition_confidence >= 0.2
              AND (
                    recognized_customer_text LIKE $keywordLike
                 OR recognized_context_text LIKE $keywordLike
                 OR recognized_product_id LIKE $keywordLike
                 OR recognized_product_name LIKE $keywordLike
              )
              AND ($platformHint = '' OR platform_hint = $platformHint)
              AND ($maxOccurredAt = '' OR occurred_at <= $maxOccurredAt)
              AND ($excludedConversationLogId = '' OR id <> $excludedConversationLogId);
            """;
        command.Parameters.AddWithValue("$keywordLike", $"%{keyword}%");
        command.Parameters.AddWithValue("$platformHint", string.IsNullOrWhiteSpace(platformHint) || platformHint == "unknown"
            ? string.Empty
            : platformHint);
        BindRecommendationQueryScope(command, queryScope);

        return Convert.ToInt32(await command.ExecuteScalarAsync(_cancellationTokenSource.Token).ConfigureAwait(false) ?? 0);
    }

    private static bool HasMeaningfulRecognitionCorrection(ConversationRecognitionCorrection correction)
    {
        return correction is not null &&
               (!string.IsNullOrWhiteSpace(correction.CustomerText) ||
                !string.IsNullOrWhiteSpace(correction.ContextText) ||
                !string.IsNullOrWhiteSpace(correction.ProductId) ||
                !string.IsNullOrWhiteSpace(correction.ProductName) ||
                correction.Messages.Count > 0);
    }

    private static string SerializeConversationMessages(IReadOnlyList<ConversationMessageTurn> messages)
    {
        return messages is null || messages.Count == 0
            ? string.Empty
            : JsonSerializer.Serialize(messages);
    }

    private static void BindRecommendationQueryScope(SqliteCommand command, RecommendationQueryScope? queryScope)
    {
        command.Parameters.AddWithValue(
            "$maxOccurredAt",
            queryScope?.MaxOccurredAt is null
                ? string.Empty
                : queryScope.MaxOccurredAt.Value.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$excludedConversationLogId",
            NormalizeOptionalValue(queryScope?.ExcludedConversationLogId));
    }

    private static bool ShouldKeepRecommendation(int sampleCount, double confidence)
    {
        if (sampleCount >= 20)
        {
            return confidence >= 0.35d;
        }

        if (sampleCount >= 8)
        {
            return confidence >= 0.45d;
        }

        return confidence >= 0.60d;
    }

    private static double ComputeLooseTextSimilarity(string currentText, string historicalText)
    {
        var normalizedCurrent = NormalizeLooseComparisonText(currentText);
        var normalizedHistorical = NormalizeLooseComparisonText(historicalText);
        if (string.IsNullOrWhiteSpace(normalizedCurrent) || string.IsNullOrWhiteSpace(normalizedHistorical))
        {
            return 0d;
        }

        if (string.Equals(normalizedCurrent, normalizedHistorical, StringComparison.Ordinal))
        {
            return 1d;
        }

        if (normalizedCurrent.Contains(normalizedHistorical, StringComparison.Ordinal) ||
            normalizedHistorical.Contains(normalizedCurrent, StringComparison.Ordinal))
        {
            return (double)Math.Min(normalizedCurrent.Length, normalizedHistorical.Length) /
                   Math.Max(normalizedCurrent.Length, normalizedHistorical.Length);
        }

        var currentTokens = ExtractRecommendationKeywords(normalizedCurrent);
        var historicalTokens = ExtractRecommendationKeywords(normalizedHistorical);
        if (currentTokens.Count == 0 || historicalTokens.Count == 0)
        {
            return 0d;
        }

        var currentSet = currentTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var historicalSet = historicalTokens.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlap = currentSet.Intersect(historicalSet, StringComparer.OrdinalIgnoreCase).Count();
        return overlap <= 0
            ? 0d
            : (double)overlap / Math.Max(currentSet.Count, historicalSet.Count);
    }

    private static string NormalizeLooseComparisonText(string value)
    {
        var normalized = NormalizeCapturedValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return Regex.Replace(normalized.ToLowerInvariant(), @"[\s\p{P}\p{S}]+", string.Empty);
    }

    private static IReadOnlyList<string> BuildRecommendationKeywords(string recognizedCustomerText)
    {
        var keywords = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddKeywordsFromText(string value)
        {
            foreach (var keyword in ExtractRecommendationKeywords(value))
            {
                AddKeyword(keyword, seen, keywords);
                if (keywords.Count >= 12)
                {
                    return;
                }
            }
        }

        AddKeywordsFromText(recognizedCustomerText);

        return keywords.Take(12).ToArray();
    }

    private static IReadOnlyList<string> ExtractRecommendationKeywords(string recognizedCustomerText)
    {
        if (string.IsNullOrWhiteSpace(recognizedCustomerText))
        {
            return [];
        }

        var normalized = Regex.Replace(recognizedCustomerText, @"\s+", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var keywords = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(normalized, @"[A-Za-z0-9]{2,16}"))
        {
            AddKeyword(match.Value, seen, keywords);
        }

        var chineseOnly = Regex.Replace(normalized, @"[^\u4e00-\u9fff]", string.Empty);
        for (var length = 4; length >= 2; length--)
        {
            for (var index = 0; index <= chineseOnly.Length - length; index++)
            {
                AddKeyword(chineseOnly.Substring(index, length), seen, keywords);
                if (keywords.Count >= 12)
                {
                    return keywords;
                }
            }
        }

        return keywords;
    }

    private static void AddKeyword(string keyword, HashSet<string> seen, List<string> keywords)
    {
        var normalized = keyword.Trim();
        if (normalized.Length < 2 ||
            normalized.Length > 16 ||
            !seen.Add(normalized) ||
            IsRecommendationStopword(normalized))
        {
            return;
        }

        keywords.Add(normalized);
    }

    private static bool IsRecommendationStopword(string keyword)
    {
        return keyword is "你好" or "您好" or "请问" or "这个" or "那个" or "一下" or "可以" or "怎么" or "为什么" or "什么" or "麻烦" or "谢谢";
    }

    private static bool LooksRelevantToKeywords(string customerText, IReadOnlyList<string> keywords)
    {
        return keywords.Any(keyword => customerText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static double ComputeKeywordMatchScore(string customerText, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(customerText) || keywords.Count == 0)
        {
            return 0;
        }

        var hits = keywords.Count(keyword => customerText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        return (double)hits / keywords.Count;
    }

    private static string BuildReplyPreview(string phraseTitle, string categoryName, string payloadText)
    {
        var title = NormalizeOptionalValue(phraseTitle);
        var content = NormalizeOptionalValue(payloadText).Replace("\r", " ").Replace("\n", " ");
        if (content.Length > 24)
        {
            content = $"{content[..24]}...";
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return string.IsNullOrWhiteSpace(categoryName)
                ? $"{title}：{content}"
                : $"{categoryName}/{title}：{content}";
        }

        return content;
    }

    private sealed class ConversationLogRequest
    {
        public bool IsMaintenance { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }

        public required string TriggerSource { get; init; }

        public required ComposeActionMode ActionMode { get; init; }

        public required string PayloadType { get; init; }

        public required bool ResultSuccess { get; init; }

        public required string ResultMessage { get; init; }

        public required IntPtr TargetWindowHandle { get; init; }

        public ComposeOperationTelemetry? ComposeTelemetry { get; init; }

        public ConversationRecommendationSnapshot? RecognitionOverride { get; init; }

        public string SearchKeyword { get; init; } = string.Empty;

        public Guid? PhraseId { get; init; }

        public string PhraseTitle { get; init; } = string.Empty;

        public string PhraseTags { get; init; } = string.Empty;

        public string PhraseCategoryName { get; init; } = string.Empty;

        public Guid? FileItemId { get; init; }

        public string FileDisplayName { get; init; } = string.Empty;

        public string FilePathsJson { get; init; } = string.Empty;

        public string PayloadText { get; init; } = string.Empty;

        public static ConversationLogRequest Maintenance()
        {
            return new ConversationLogRequest
            {
                IsMaintenance = true,
                OccurredAt = DateTimeOffset.Now,
                TriggerSource = "maintenance",
                ActionMode = ComposeActionMode.Copy,
                PayloadType = "maintenance",
                ResultSuccess = true,
                ResultMessage = string.Empty,
                TargetWindowHandle = IntPtr.Zero
            };
        }
    }

    private static string ClassifyResultMessageFailureCategory(string resultMessage)
    {
        if (string.IsNullOrWhiteSpace(resultMessage))
        {
            return "unknown";
        }

        if (resultMessage.Contains("未找到目标输入窗口", StringComparison.Ordinal))
        {
            return "missing_target";
        }

        if (resultMessage.Contains("目标输入窗口已变化", StringComparison.Ordinal))
        {
            return "target_changed";
        }

        if (resultMessage.Contains("未能稳定切回目标输入窗口", StringComparison.Ordinal))
        {
            return "activation";
        }

        if (resultMessage.Contains("焦点已丢失", StringComparison.Ordinal) ||
            resultMessage.Contains("先点一下聊天输入框", StringComparison.Ordinal))
        {
            return "focus_lost";
        }

        if (resultMessage.Contains("OpenClipboard", StringComparison.OrdinalIgnoreCase) ||
            resultMessage.Contains("CLIPBRD", StringComparison.OrdinalIgnoreCase) ||
            resultMessage.Contains("占用", StringComparison.Ordinal) ||
            resultMessage.Contains("Insufficient memory", StringComparison.OrdinalIgnoreCase) ||
            resultMessage.Contains("超时", StringComparison.Ordinal))
        {
            return "clipboard";
        }

        if (resultMessage.Contains("文件不存在", StringComparison.Ordinal) ||
            resultMessage.Contains("未找到图片文件", StringComparison.Ordinal))
        {
            return "missing_input";
        }

        return "other";
    }

    private static string SerializeDetails(object? details)
    {
        if (details is null)
        {
            return string.Empty;
        }

        try
        {
            return JsonSerializer.Serialize(details);
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class ConversationRecognitionResult
    {
        public string Status { get; init; } = string.Empty;

        public string Method { get; init; } = string.Empty;

        public double Confidence { get; init; }

        public string RecognizedCustomerText { get; init; } = string.Empty;

        public string RecognizedContextText { get; init; } = string.Empty;

        public string RecognizedProductId { get; init; } = string.Empty;

        public string RecognizedProductName { get; init; } = string.Empty;

        public IReadOnlyList<ConversationMessageTurn> Messages { get; init; } = [];

        public string MessagesJson { get; init; } = string.Empty;

        public static ConversationRecognitionResult Empty(string status)
        {
            return new ConversationRecognitionResult
            {
                Status = status,
                Method = RecognitionMethodVersion,
                Confidence = 0,
                Messages = [],
                MessagesJson = "[]"
            };
        }
    }

    private readonly record struct ProductReference(string ProductId, string ProductName);

    private sealed class RecognizedMessageCandidate
    {
        public int Index { get; init; }

        public required string Text { get; init; }

        public double Score { get; init; }

        public string Reason { get; init; } = string.Empty;
    }

    private sealed class AutomationTextEntry
    {
        public int Sequence { get; init; }

        public string Text { get; init; } = string.Empty;

        public double Left { get; init; }

        public double Top { get; init; }

        public double Width { get; init; }

        public double Height { get; init; }

        public bool IsOffscreen { get; init; }

        public string ControlTypeName { get; init; } = string.Empty;
    }

    private sealed class WindowCaptureSnapshot
    {
        public string WindowTitle { get; set; } = string.Empty;

        public string WindowClassName { get; set; } = string.Empty;

        public string ProcessName { get; set; } = string.Empty;

        public string ProcessPath { get; set; } = string.Empty;

        public string PlatformHint { get; set; } = string.Empty;

        public int Left { get; set; }

        public int Top { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public string CaptureStatus { get; set; } = string.Empty;

        public string CapturedText { get; set; } = string.Empty;

        public string ScreenshotPath { get; set; } = string.Empty;

        public static WindowCaptureSnapshot Empty(string status)
        {
            return new WindowCaptureSnapshot
            {
                CaptureStatus = status
            };
        }
    }

    private readonly record struct ReplyGroupResolution(Guid GroupId, int NextIndex);

    private sealed class ReplyGroupAccumulator
    {
        public Guid ReplyGroupId { get; init; }

        public string RecognizedCustomerText { get; init; } = string.Empty;

        public DateTimeOffset LastOccurredAt { get; init; }

        public string QualityGrade { get; init; } = string.Empty;

        public double MatchScore { get; set; }

        public List<string> Replies { get; } = [];
    }
}
