using System.Globalization;
using Microsoft.Data.Sqlite;

namespace QuickReply.ReplayEval.Tests;

public sealed class ReplaySampleStoreTests
{
    [Fact]
    public async Task LoadSamplesAsync_IncludesFailedSendAndCorrectionBackfillSources()
    {
        using var scope = new ReplaySampleStoreScope();
        await using var connection = await scope.OpenConnectionAsync();
        await scope.CreateSchemaAsync(connection);

        await scope.InsertConversationSampleAsync(
            connection,
            id: "log-success",
            occurredAt: "2026-04-15T09:00:00+08:00",
            platformHint: "wechat",
            phraseId: Guid.NewGuid(),
            phraseTitle: "成功样本",
            payloadText: "成功话术",
            recognizedCustomerText: "客户问发货时间",
            resultSuccess: true);

        await scope.InsertConversationSampleAsync(
            connection,
            id: "log-failed",
            occurredAt: "2026-04-15T09:01:00+08:00",
            platformHint: "qianniu",
            phraseId: Guid.NewGuid(),
            phraseTitle: "失败样本",
            payloadText: "失败话术",
            recognizedCustomerText: "客户问尺码",
            resultSuccess: false);

        var correctionPhraseId = Guid.NewGuid();
        await scope.InsertCorrectionBackfillAsync(
            connection,
            correctionId: "corr-1",
            sessionId: "session-1",
            occurredAt: "2026-04-15T09:02:00+08:00",
            platformHint: "pdd",
            sourceCustomerText: "原始识别",
            correctedCustomerText: "纠正后的客户问题",
            phraseId: correctionPhraseId,
            phraseTitle: "纠错回灌样本",
            actualPayloadText: "纠错后命中的话术");

        var store = new ReplaySampleStore(scope.DatabasePath);
        var samples = await store.LoadSamplesAsync(10, platformHint: null);

        Assert.Equal(3, samples.Count);
        Assert.Contains(samples, sample => sample.SampleSource == ReplaySampleSources.SuccessfulSend && sample.Id == "log-success");
        Assert.Contains(samples, sample => sample.SampleSource == ReplaySampleSources.FailedSend && sample.Id == "log-failed");

        var correctionSample = Assert.Single(samples.Where(sample => sample.SampleSource == ReplaySampleSources.CorrectionBackfill));
        Assert.True(correctionSample.CorrectionAlreadyApplied);
        Assert.Equal("纠正后的客户问题", correctionSample.RecognizedCustomerText);
        Assert.Equal(correctionPhraseId, correctionSample.ActualPhraseId);
    }

    [Fact]
    public async Task LoadSamplesAsync_AppliesPlatformFilterToCorrectionBackfill()
    {
        using var scope = new ReplaySampleStoreScope();
        await using var connection = await scope.OpenConnectionAsync();
        await scope.CreateSchemaAsync(connection);

        await scope.InsertCorrectionBackfillAsync(
            connection,
            correctionId: "corr-pdd",
            sessionId: "session-pdd",
            occurredAt: "2026-04-15T10:00:00+08:00",
            platformHint: "pdd",
            sourceCustomerText: "原始",
            correctedCustomerText: "拼多多样本",
            phraseId: Guid.NewGuid(),
            phraseTitle: "PDD",
            actualPayloadText: "PDD payload");

        await scope.InsertCorrectionBackfillAsync(
            connection,
            correctionId: "corr-wechat",
            sessionId: "session-wechat",
            occurredAt: "2026-04-15T10:01:00+08:00",
            platformHint: "wechat",
            sourceCustomerText: "原始",
            correctedCustomerText: "微信样本",
            phraseId: Guid.NewGuid(),
            phraseTitle: "WeChat",
            actualPayloadText: "WeChat payload");

        var store = new ReplaySampleStore(scope.DatabasePath);
        var samples = await store.LoadSamplesAsync(10, platformHint: "wechat");

        var sample = Assert.Single(samples);
        Assert.Equal(ReplaySampleSources.CorrectionBackfill, sample.SampleSource);
        Assert.Equal("wechat", sample.PlatformHint);
        Assert.Equal("微信样本", sample.RecognizedCustomerText);
    }

    [Fact]
    public async Task LoadSamplesAsync_AppliesSampleSourceFilter()
    {
        using var scope = new ReplaySampleStoreScope();
        await using var connection = await scope.OpenConnectionAsync();
        await scope.CreateSchemaAsync(connection);

        await scope.InsertConversationSampleAsync(
            connection,
            id: "log-success",
            occurredAt: "2026-04-15T11:00:00+08:00",
            platformHint: "wechat",
            phraseId: Guid.NewGuid(),
            phraseTitle: "成功样本",
            payloadText: "成功话术",
            recognizedCustomerText: "客户问物流",
            resultSuccess: true);

        await scope.InsertConversationSampleAsync(
            connection,
            id: "log-failed",
            occurredAt: "2026-04-15T11:01:00+08:00",
            platformHint: "wechat",
            phraseId: Guid.NewGuid(),
            phraseTitle: "失败样本",
            payloadText: "失败话术",
            recognizedCustomerText: "客户问退换",
            resultSuccess: false);

        var store = new ReplaySampleStore(scope.DatabasePath);
        var samples = await store.LoadSamplesAsync(10, platformHint: null, sampleSource: ReplaySampleSources.FailedSend);

        var sample = Assert.Single(samples);
        Assert.Equal(ReplaySampleSources.FailedSend, sample.SampleSource);
        Assert.Equal("log-failed", sample.Id);
    }

    private sealed class ReplaySampleStoreScope : IDisposable
    {
        public ReplaySampleStoreScope()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "QuickReplyTool.ReplaySampleStore.Tests", Guid.NewGuid().ToString("N"));
            DatabasePath = Path.Combine(RootPath, "conversation-logs.db");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string DatabasePath { get; }

        public async Task<SqliteConnection> OpenConnectionAsync()
        {
            var connection = new SqliteConnection($"Data Source={DatabasePath}");
            await connection.OpenAsync();
            return connection;
        }

        public async Task CreateSchemaAsync(SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE conversation_logs (
                    id TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    platform_hint TEXT NOT NULL DEFAULT '',
                    target_window_title TEXT NOT NULL DEFAULT '',
                    target_window_class_name TEXT NOT NULL DEFAULT '',
                    target_process_name TEXT NOT NULL DEFAULT '',
                    target_process_path TEXT NOT NULL DEFAULT '',
                    recognition_status TEXT NOT NULL DEFAULT '',
                    recognition_method TEXT NOT NULL DEFAULT '',
                    recognition_confidence REAL NOT NULL DEFAULT 0,
                    recognized_customer_text TEXT NOT NULL DEFAULT '',
                    recognized_context_text TEXT NOT NULL DEFAULT '',
                    recognized_product_id TEXT NOT NULL DEFAULT '',
                    recognized_product_name TEXT NOT NULL DEFAULT '',
                    recognized_messages_json TEXT NOT NULL DEFAULT '',
                    phrase_id TEXT NOT NULL DEFAULT '',
                    phrase_title TEXT NOT NULL DEFAULT '',
                    payload_text TEXT NOT NULL DEFAULT '',
                    result_success INTEGER NOT NULL DEFAULT 1,
                    payload_type TEXT NOT NULL DEFAULT 'phrase',
                    action_mode TEXT NOT NULL DEFAULT 'Send'
                );
                CREATE TABLE recommendation_events (
                    id TEXT NOT NULL,
                    recommendation_session_id TEXT NOT NULL DEFAULT '',
                    event_type TEXT NOT NULL DEFAULT '',
                    phrase_id TEXT NOT NULL DEFAULT '',
                    phrase_title TEXT NOT NULL DEFAULT '',
                    actual_payload_text TEXT NOT NULL DEFAULT '',
                    platform_hint TEXT NOT NULL DEFAULT ''
                );
                CREATE TABLE recognition_corrections (
                    id TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    recommendation_session_id TEXT NOT NULL DEFAULT '',
                    platform_hint TEXT NOT NULL DEFAULT '',
                    window_title TEXT NOT NULL DEFAULT '',
                    source_customer_text TEXT NOT NULL DEFAULT '',
                    corrected_customer_text TEXT NOT NULL DEFAULT '',
                    source_context_text TEXT NOT NULL DEFAULT '',
                    corrected_context_text TEXT NOT NULL DEFAULT '',
                    source_product_id TEXT NOT NULL DEFAULT '',
                    corrected_product_id TEXT NOT NULL DEFAULT '',
                    source_product_name TEXT NOT NULL DEFAULT '',
                    corrected_product_name TEXT NOT NULL DEFAULT '',
                    source_messages_json TEXT NOT NULL DEFAULT '',
                    corrected_messages_json TEXT NOT NULL DEFAULT ''
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertConversationSampleAsync(
            SqliteConnection connection,
            string id,
            string occurredAt,
            string platformHint,
            Guid phraseId,
            string phraseTitle,
            string payloadText,
            string recognizedCustomerText,
            bool resultSuccess)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO conversation_logs (
                    id,
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
                    result_success,
                    payload_type,
                    action_mode)
                VALUES (
                    $id,
                    $occurredAt,
                    $platformHint,
                    'chat-window',
                    'window-class',
                    'app',
                    '',
                    'recognized',
                    'ocr',
                    0.9,
                    $recognizedCustomerText,
                    '',
                    '',
                    '',
                    '',
                    $phraseId,
                    $phraseTitle,
                    $payloadText,
                    $resultSuccess,
                    'phrase',
                    'Send');
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$occurredAt", occurredAt);
            command.Parameters.AddWithValue("$platformHint", platformHint);
            command.Parameters.AddWithValue("$recognizedCustomerText", recognizedCustomerText);
            command.Parameters.AddWithValue("$phraseId", phraseId.ToString());
            command.Parameters.AddWithValue("$phraseTitle", phraseTitle);
            command.Parameters.AddWithValue("$payloadText", payloadText);
            command.Parameters.AddWithValue("$resultSuccess", resultSuccess ? 1 : 0);
            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertCorrectionBackfillAsync(
            SqliteConnection connection,
            string correctionId,
            string sessionId,
            string occurredAt,
            string platformHint,
            string sourceCustomerText,
            string correctedCustomerText,
            Guid phraseId,
            string phraseTitle,
            string actualPayloadText)
        {
            await using (var correctionCommand = connection.CreateCommand())
            {
                correctionCommand.CommandText = """
                    INSERT INTO recognition_corrections (
                        id,
                        occurred_at,
                        recommendation_session_id,
                        platform_hint,
                        window_title,
                        source_customer_text,
                        corrected_customer_text,
                        source_context_text,
                        corrected_context_text,
                        source_product_id,
                        corrected_product_id,
                        source_product_name,
                        corrected_product_name,
                        source_messages_json,
                        corrected_messages_json)
                    VALUES (
                        $id,
                        $occurredAt,
                        $sessionId,
                        $platformHint,
                        'chat-window',
                        $sourceCustomerText,
                        $correctedCustomerText,
                        '',
                        '',
                        '',
                        '',
                        '',
                        '',
                        '',
                        '');
                    """;
                correctionCommand.Parameters.AddWithValue("$id", correctionId);
                correctionCommand.Parameters.AddWithValue("$occurredAt", occurredAt);
                correctionCommand.Parameters.AddWithValue("$sessionId", sessionId);
                correctionCommand.Parameters.AddWithValue("$platformHint", platformHint);
                correctionCommand.Parameters.AddWithValue("$sourceCustomerText", sourceCustomerText);
                correctionCommand.Parameters.AddWithValue("$correctedCustomerText", correctedCustomerText);
                await correctionCommand.ExecuteNonQueryAsync();
            }

            await using var recommendationCommand = connection.CreateCommand();
            recommendationCommand.CommandText = """
                INSERT INTO recommendation_events (
                    id,
                    recommendation_session_id,
                    event_type,
                    phrase_id,
                    phrase_title,
                    actual_payload_text,
                    platform_hint)
                VALUES (
                    $id,
                    $sessionId,
                    'accepted',
                    $phraseId,
                    $phraseTitle,
                    $actualPayloadText,
                    $platformHint);
                """;
            recommendationCommand.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            recommendationCommand.Parameters.AddWithValue("$sessionId", sessionId);
            recommendationCommand.Parameters.AddWithValue("$phraseId", phraseId.ToString());
            recommendationCommand.Parameters.AddWithValue("$phraseTitle", phraseTitle);
            recommendationCommand.Parameters.AddWithValue("$actualPayloadText", actualPayloadText);
            recommendationCommand.Parameters.AddWithValue("$platformHint", platformHint);
            await recommendationCommand.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
