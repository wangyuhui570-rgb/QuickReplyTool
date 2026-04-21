using Microsoft.Data.Sqlite;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;

namespace QuickReply.Desktop.Tests;

public sealed class ConversationLoggingServiceTests
{
    [Theory]
    [InlineData("怎么样", true)]
    [InlineData("测试短语3", true)]
    [InlineData("csss", false)]
    [InlineData("hi", false)]
    public void IsEligibleFrequentReplyPayload_UsesChineseFriendlyThreshold(string payloadText, bool expected)
    {
        var actual = ConversationLoggingService.IsEligibleFrequentReplyPayload(payloadText);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task GetFrequentPlainTextSuggestionAsync_ReturnsSuggestion_ForRepeatedShortChinesePhrase()
    {
        using var scope = new TestAppDataScope();
        var logsRootPath = Path.Combine(scope.AppRootPath, "logs");

        await using var service = new ConversationLoggingService(logsRootPath);
        for (var index = 0; index < 3; index++)
        {
            service.LogPlainTextUsage(
                "怎么样",
                "manual_send",
                ComposeActionMode.Send,
                searchKeyword: null,
                resultSuccess: true,
                resultMessage: "发送成功",
                targetWindowHandle: IntPtr.Zero);
        }

        await Task.Delay(250);
        var suggestion = await service.GetFrequentPlainTextSuggestionAsync("怎么样");

        Assert.NotNull(suggestion);
        Assert.Equal("怎么样", suggestion!.PayloadText);
        Assert.True(suggestion.UseCount >= 3);
    }

    [Fact]
    public void ShouldUseOcrFallback_ReturnsTrue_ForWeComWindowTitleOnlyCapture()
    {
        var shouldUseOcr = ConversationLoggingService.ShouldUseOcrFallback(
            "wecom",
            "企业微信",
            "WXWork",
            "企业微信",
            hasScreenshot: true);

        Assert.True(shouldUseOcr);
    }

    [Fact]
    public void ShouldUseOcrFallback_ReturnsTrue_ForPddWorkbenchCapture()
    {
        var shouldUseOcr = ConversationLoggingService.ShouldUseOcrFallback(
            "pdd_workbench",
            "拼多多工作台",
            "chrome",
            "拼多多工作台",
            hasScreenshot: true);

        Assert.True(shouldUseOcr);
    }

    [Fact]
    public void ShouldUseOcrFallback_ReturnsFalse_WhenMeaningfulConversationTextAlreadyExists()
    {
        var shouldUseOcr = ConversationLoggingService.ShouldUseOcrFallback(
            "unknown",
            "聊天窗口",
            "chrome",
            "你好，什么时候发货",
            hasScreenshot: true);

        Assert.False(shouldUseOcr);
    }

    [Fact]
    public void MergeCapturedText_DeduplicatesAndKeepsExistingLinesFirst()
    {
        var merged = ConversationLoggingService.MergeCapturedText(
            "企业微信\r\n什么时候发货",
            "什么时候发货\r\n可以今天发吗");

        Assert.Equal(
            new[] { "企业微信", "什么时候发货", "可以今天发吗" },
            merged.Split(Environment.NewLine, StringSplitOptions.None));
    }

    [Theory]
    [InlineData("最小化 恢复", "wecom")]
    [InlineData("最小化恢复", "wecom")]
    [InlineData("最小化/恢复/关闭", "qianniu")]
    public void LooksLikeUiNoise_ReturnsTrue_ForWindowControlText(string line, string platformHint)
    {
        Assert.True(ConversationLoggingService.LooksLikeUiNoise(line, platformHint));
    }

    [Fact]
    public void FilterRecognizedConversationTextForDisplay_DropsWeakWindowControlText()
    {
        var filtered = ConversationLoggingService.FilterRecognizedConversationTextForDisplay(
            "最小化 恢复",
            confidence: 0.34d,
            platformHint: "wecom");

        Assert.Equal(string.Empty, filtered);
    }

    [Fact]
    public void FilterRecognizedConversationTextForDisplay_KeepsMeaningfulCustomerQuestion()
    {
        var filtered = ConversationLoggingService.FilterRecognizedConversationTextForDisplay(
            "内胎怎么选",
            confidence: 0.34d,
            platformHint: "wecom");

        Assert.Equal("内胎怎么选", filtered);
    }

    [Fact]
    public void SplitAutomationTextValueForTesting_PreservesLineBoundaries()
    {
        var lines = ConversationLoggingService.SplitAutomationTextValueForTesting(
            "客户：订单什么时候发\r\n客服：今天发出\r\n\r\n客户：好的");

        Assert.Equal(
            ["客户：订单什么时候发", "客服：今天发出", "客户：好的"],
            lines);
    }

    [Theory]
    [InlineData("jd_workbench", 420, 220, 360, 80, 100, 100, 1200, 900, true)]
    [InlineData("jd_workbench", 40, 220, 220, 80, 100, 100, 1200, 900, false)]
    [InlineData("jd_workbench", 980, 220, 180, 80, 100, 100, 1200, 900, false)]
    [InlineData("wecom", 40, 220, 220, 80, 100, 100, 1200, 900, true)]
    public void IsWithinPreferredAutomationRegionForTesting_FiltersWorkbenchSidePanels(
        string platformHint,
        double elementLeft,
        double elementTop,
        double elementWidth,
        double elementHeight,
        double windowLeft,
        double windowTop,
        double windowWidth,
        double windowHeight,
        bool expected)
    {
        var actual = ConversationLoggingService.IsWithinPreferredAutomationRegionForTesting(
            platformHint,
            elementLeft,
            elementTop,
            elementWidth,
            elementHeight,
            windowLeft,
            windowTop,
            windowWidth,
            windowHeight);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("淘宝工作台", "AliWorkbench", "", "", "qianniu")]
    [InlineData("京东客服", "jingmai", "", "", "jd_workbench")]
    [InlineData("拼多多商家工作台", "chrome", "", "Chrome_WidgetWin_1", "pdd_workbench")]
    [InlineData("企业微信", "WXWork", "", "", "wecom")]
    public void InferPlatformHint_RecognizesWorkbenchPlatforms(
        string windowTitle,
        string processName,
        string processPath,
        string windowClassName,
        string expected)
    {
        var actual = ConversationLoggingService.InferPlatformHint(windowTitle, processName, processPath, windowClassName);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RecognizeContext_KeepsBroaderRecentConversationContext()
    {
        using var scope = new TestAppDataScope();
        var logsRootPath = Path.Combine(scope.AppRootPath, "logs");

        await using var service = new ConversationLoggingService(logsRootPath);
        var snapshot = service.RecognizeContext(new CapturedConversationContext
        {
            PlatformHint = "wecom",
            WindowTitle = "企业微信",
            CapturedText = string.Join(
                Environment.NewLine,
                [
                    "企业微信",
                    "订单还没收到",
                    "我这边查一下",
                    "昨天已经给您发出了",
                    "物流单号稍后发您",
                    "那安装视频在哪里",
                    "安装视频等会发您",
                    "还有尺码怎么选",
                    "按平时尺码拍就行",
                    "今天下单什么时候发",
                    "16点前付款当天发",
                    "好的谢谢"
                ])
        });

        var contextLines = snapshot.RecognizedContextText
            .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains("订单还没收到", contextLines);
        Assert.Contains("今天下单什么时候发", contextLines);
        Assert.True(contextLines.Length >= 10);
    }

    [Fact]
    public async Task LogOperationEventAsync_PersistsOperationEvent()
    {
        using var scope = new TestAppDataScope();
        var logsRootPath = Path.Combine(scope.AppRootPath, "logs");
        var databasePath = Path.Combine(logsRootPath, "conversation-logs.db");

        await using var service = new ConversationLoggingService(logsRootPath);
        await service.LogOperationEventAsync(
            "update_check",
            "manifest_request",
            success: false,
            skipped: false,
            durationMs: 321,
            message: "服务器没有返回有效的更新清单。",
            details: new { force = true });

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_name,
                   stage,
                   success,
                   skipped,
                   duration_ms,
                   message
            FROM operation_events
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("update_check", reader.GetString(0));
        Assert.Equal("manifest_request", reader.GetString(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
        Assert.Equal(321L, reader.GetInt64(4));
        Assert.Equal("服务器没有返回有效的更新清单。", reader.GetString(5));
    }

    [Fact]
    public async Task LogPlainTextUsage_PersistsComposeTelemetryColumns()
    {
        using var scope = new TestAppDataScope();
        var logsRootPath = Path.Combine(scope.AppRootPath, "logs");
        var databasePath = Path.Combine(logsRootPath, "conversation-logs.db");

        await using var service = new ConversationLoggingService(logsRootPath);
        service.LogPlainTextUsage(
            "你好，这里是带 telemetry 的发送记录",
            "manual_send",
            ComposeActionMode.Send,
            searchKeyword: null,
            resultSuccess: false,
            resultMessage: "已自动补救 1 次，但仍失败：目标输入窗口焦点已丢失，请先点一下聊天输入框，再重试。",
            targetWindowHandle: new IntPtr(123),
            composeTelemetry: ComposeOperationTelemetry.Create(
                "text_only",
                "pdd_workbench",
                attemptCount: 2,
                retryTriggered: true,
                retryReason: "focus_lost",
                retryOutcome: "failed",
                failureCategory: "focus_lost",
                targetChangedBeforeRetry: false));

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'conversation_logs';";
            var tableExists = Convert.ToInt32(await countCommand.ExecuteScalarAsync()) > 0;
            if (tableExists)
            {
                await using var rowCountCommand = connection.CreateCommand();
                rowCountCommand.CommandText = "SELECT COUNT(1) FROM conversation_logs;";
                var rowCount = Convert.ToInt32(await rowCountCommand.ExecuteScalarAsync());
                if (rowCount > 0)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT compose_host_profile,
                   compose_attempt_count,
                   compose_retry_triggered,
                   compose_retry_reason,
                   compose_retry_outcome,
                   compose_failure_category,
                   compose_target_changed_before_retry
            FROM conversation_logs
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("pdd_workbench", reader.GetString(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal("focus_lost", reader.GetString(3));
        Assert.Equal("failed", reader.GetString(4));
        Assert.Equal("focus_lost", reader.GetString(5));
        Assert.Equal(0L, reader.GetInt64(6));
    }

    [Fact]
    public async Task LogPhraseUsage_UsesRecognitionOverride_WhenProvided()
    {
        using var scope = new TestAppDataScope();
        var logsRootPath = Path.Combine(scope.AppRootPath, "logs");
        var databasePath = Path.Combine(logsRootPath, "conversation-logs.db");

        await using var service = new ConversationLoggingService(logsRootPath);
        service.LogPhraseUsage(
            new ReplyPhrase
            {
                Id = Guid.NewGuid(),
                Title = "发货答复",
                Content = "您好，这边今天可以发出。"
            },
            "search_overlay_recommendation_send",
            ComposeActionMode.Send,
            searchKeyword: "发货",
            resultSuccess: true,
            resultMessage: "发送成功",
            targetWindowHandle: IntPtr.Zero,
            composeTelemetry: null,
            recognitionOverride: new ConversationRecommendationSnapshot
            {
                PlatformHint = "wecom",
                WindowTitle = "企业微信",
                CaptureStatus = "text_and_screenshot+ocr",
                CapturedText = "客户：这款今天能发货吗",
                RecognitionStatus = "identified",
                RecognitionMethod = "automation_ocr_hybrid_v2",
                RecognitionConfidence = 0.91d,
                RecognizedCustomerText = "这款今天能发货吗",
                RecognizedContextText = "客户想确认发货时效。",
                Product = new ConversationProductContext
                {
                    ProductId = "SPU-1",
                    ProductName = "测试商品"
                }
            });

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'conversation_logs';";
            var tableExists = Convert.ToInt32(await countCommand.ExecuteScalarAsync()) > 0;
            if (tableExists)
            {
                await using var rowCountCommand = connection.CreateCommand();
                rowCountCommand.CommandText = "SELECT COUNT(1) FROM conversation_logs;";
                if (Convert.ToInt32(await rowCountCommand.ExecuteScalarAsync()) > 0)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT platform_hint,
                   capture_status,
                   recognized_customer_text,
                   recognized_context_text,
                   recognition_confidence
            FROM conversation_logs
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("wecom", reader.GetString(0));
        Assert.Equal("text_and_screenshot+ocr", reader.GetString(1));
        Assert.Equal("这款今天能发货吗", reader.GetString(2));
        Assert.Equal("客户想确认发货时效。", reader.GetString(3));
        Assert.Equal(0.91d, reader.GetDouble(4), 3);
    }

    [Fact]
    public async Task LogAiDraftEvents_PersistStructuredRecommendationColumns()
    {
        using var scope = new TestAppDataScope();
        var logsRootPath = Path.Combine(scope.AppRootPath, "logs");
        var databasePath = Path.Combine(logsRootPath, "conversation-logs.db");

        await using var service = new ConversationLoggingService(logsRootPath);
        var snapshot = new ConversationRecommendationSnapshot
        {
            PlatformHint = "wecom",
            RecognizedCustomerText = "这款什么时候发货？",
            WindowTitle = "企业微信"
        };

        var draft = new GeneratedReplyCandidate
        {
            DisplayRank = 1,
            Text = "您好，这款今天可以安排发出。",
            Reason = "参考了发货类历史短语",
            ProviderType = "ollama",
            ModelName = "qwen2.5:3b",
            GroundingPhraseCount = 2,
            GroundingKnowledgeCount = 1,
            GroundingKnowledgeIdsJson = "[\"11111111-1111-1111-1111-111111111111\"]",
            LatencyMs = 380,
            CharacterCount = 15
        };

        var sessionId = service.LogAiDraftsShown(snapshot, [draft], SearchOverlayMode.Phrase, "发货");
        service.LogAiDraftAction(draft, sessionId, SearchOverlayMode.Phrase, "发货", "sent", ComposeActionMode.Send, resultSuccess: true);
        await service.LogAiDraftStatusAsync(snapshot, SearchOverlayMode.Phrase, "发货", AiDraftGenerationStatus.ProviderFailed(
            "本地 Ollama 当前不可用。",
            "ollama",
            "qwen2.5:3b",
            latencyMs: 800,
            groundingPhraseCount: 2,
            groundingKnowledgeCount: 1,
            fallbackReason: "ollama_unavailable",
            characterCount: 0));

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(1) FROM recommendation_events;";
            var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
            if (count >= 3)
            {
                break;
            }

            await Task.Delay(50);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recommendation_kind,
                   provider_type,
                   model_name,
                   latency_ms,
                   grounding_phrase_count,
                   grounding_knowledge_count,
                   grounding_knowledge_ids_json,
                   knowledge_hit,
                   fallback_reason,
                   generation_character_count
            FROM recommendation_events
            WHERE event_type = 'shown'
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ai_draft", reader.GetString(0));
        Assert.Equal("ollama", reader.GetString(1));
        Assert.Equal("qwen2.5:3b", reader.GetString(2));
        Assert.Equal(380L, reader.GetInt64(3));
        Assert.Equal(2L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal("[\"11111111-1111-1111-1111-111111111111\"]", reader.GetString(6));
        Assert.Equal(1L, reader.GetInt64(7));
        Assert.Equal(string.Empty, reader.GetString(8));
        Assert.Equal(15L, reader.GetInt64(9));
    }

    [Fact]
    public async Task LogKnowledgeCardEvents_PersistKnowledgeCardFields()
    {
        using var scope = new TestAppDataScope();
        var logsRootPath = Path.Combine(scope.AppRootPath, "logs");
        var databasePath = Path.Combine(logsRootPath, "conversation-logs.db");

        await using var service = new ConversationLoggingService(logsRootPath);
        var snapshot = new ConversationRecommendationSnapshot
        {
            PlatformHint = "wecom",
            RecognizedCustomerText = "安装视频在哪",
            WindowTitle = "企业微信"
        };
        var knowledgeCard = new KnowledgeCardSuggestion
        {
            Card = new PublishedKnowledgeCard
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                CandidateKind = "resource",
                Platform = "wecom",
                ProductKey = "SKU-2",
                Question = "安装视频在哪",
                Answer = "安装视频：https://example.com/install",
                UpdatedAt = DateTimeOffset.Now
            },
            DisplayRank = 1,
            MatchedKeyword = "安装",
            MatchCount = 1,
            Confidence = 0.82d
        };

        var sessionId = service.LogKnowledgeCardsShown(snapshot, [knowledgeCard], SearchOverlayMode.Phrase, "安装");
        service.LogKnowledgeCardAccepted(knowledgeCard, sessionId, SearchOverlayMode.Phrase, "安装", ComposeActionMode.Send, resultSuccess: true);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(1) FROM recommendation_events;";
            var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync());
            if (count >= 2)
            {
                break;
            }

            await Task.Delay(50);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT recommendation_kind,
                   knowledge_card_id,
                   knowledge_card_question,
                   knowledge_card_candidate_kind,
                   grounding_knowledge_count,
                   knowledge_hit,
                   actual_payload_text
            FROM recommendation_events
            WHERE event_type = 'shown'
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("knowledge_card_resource", reader.GetString(0));
        Assert.Equal("22222222-2222-2222-2222-222222222222", reader.GetString(1));
        Assert.Equal("安装视频在哪", reader.GetString(2));
        Assert.Equal("resource", reader.GetString(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal("安装视频：https://example.com/install", reader.GetString(6));
    }

    [Fact]
    public async Task ExportSyncPayloadAsync_RedactsSensitiveConversationFields_WhenRawUploadDisabled()
    {
        using var scope = new TestAppDataScope();
        var databasePath = Path.Combine(scope.LogsRootPath, "conversation-logs.db");

        await using var service = scope.CreateConversationLoggingService();
        service.LogPlainTextUsage(
            "内部回复 C:\\secret\\reply.txt",
            "manual_send",
            ComposeActionMode.Send,
            searchKeyword: "订单 123456789",
            resultSuccess: true,
            resultMessage: "已处理 https://example.com/order/123456789",
            targetWindowHandle: IntPtr.Zero,
            recognitionOverride: new ConversationRecommendationSnapshot
            {
                PlatformHint = "wecom",
                WindowTitle = "企业微信",
                CaptureStatus = "text_only",
                CapturedText = "客户订单号 123456789 路径 C:\\Users\\alice\\Desktop\\capture.png",
                RecognitionStatus = "identified",
                RecognitionMethod = "automation_ocr_hybrid_v2",
                RecognitionConfidence = 0.62d,
                RecognizedCustomerText = "订单号123456789 https://example.com/order/123456789",
                RecognizedContextText = "查看路径 C:\\Users\\alice\\Desktop\\capture.png",
                Product = new ConversationProductContext
                {
                    ProductId = "SKU-123456",
                    ProductName = "测试商品"
                }
            });

        await WaitForTableCountAsync(databasePath, "conversation_logs", minimumCount: 1);
        var payload = await service.ExportSyncPayloadAsync("client-001", since: null, includeRawConversationData: false);
        var log = Assert.Single(payload.ConversationLogs);

        Assert.Equal(string.Empty, log.TargetWindowTitle);
        Assert.Equal(string.Empty, log.PayloadText);
        Assert.Equal(string.Empty, log.RecognizedMessagesJson);
        Assert.Contains("[num]", log.RecognizedCustomerText, StringComparison.Ordinal);
        Assert.Contains("[url]", log.RecognizedCustomerText, StringComparison.Ordinal);
        Assert.Contains("[path]", log.CapturedText, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", log.RecognizedCustomerText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportSyncPayloadAsync_PreservesRawConversationFields_WhenOptedIn()
    {
        using var scope = new TestAppDataScope();
        var databasePath = Path.Combine(scope.LogsRootPath, "conversation-logs.db");

        await using var service = scope.CreateConversationLoggingService();
        service.LogPhraseUsage(
            new ReplyPhrase
            {
                Id = Guid.NewGuid(),
                Title = "售后答复",
                Content = "这边帮您处理"
            },
            "search_overlay_recommendation_send",
            ComposeActionMode.Send,
            searchKeyword: "退款",
            resultSuccess: true,
            resultMessage: "发送成功",
            targetWindowHandle: IntPtr.Zero,
            recognitionOverride: new ConversationRecommendationSnapshot
            {
                PlatformHint = "wecom",
                WindowTitle = "企业微信",
                CaptureStatus = "text_only",
                CapturedText = "客户说退款",
                RecognitionStatus = "identified",
                RecognitionMethod = "automation_ocr_hybrid_v2",
                RecognitionConfidence = 0.88d,
                RecognizedCustomerText = "客户说退款",
                RecognizedContextText = "之前已经催过一次"
            });

        await WaitForTableCountAsync(databasePath, "conversation_logs", minimumCount: 1);
        var payload = await service.ExportSyncPayloadAsync("client-001", since: null, includeRawConversationData: true);
        var log = Assert.Single(payload.ConversationLogs);

        Assert.Equal("企业微信", log.TargetWindowTitle);
        Assert.Equal("客户说退款", log.RecognizedCustomerText);
        Assert.Equal("之前已经催过一次", log.RecognizedContextText);
    }

    [Fact]
    public async Task RunMaintenanceAsync_RemovesExpiredStructuredTelemetryRows()
    {
        using var scope = new TestAppDataScope();
        var databasePath = Path.Combine(scope.LogsRootPath, "conversation-logs.db");

        await using var service = scope.CreateConversationLoggingService();
        service.LogPlainTextUsage(
            "测试发送",
            "manual_send",
            ComposeActionMode.Send,
            searchKeyword: null,
            resultSuccess: true,
            resultMessage: "发送成功",
            targetWindowHandle: IntPtr.Zero,
            recognitionOverride: new ConversationRecommendationSnapshot
            {
                PlatformHint = "wecom",
                WindowTitle = "企业微信",
                CaptureStatus = "text_only",
                CapturedText = "客户问发货",
                RecognitionStatus = "identified",
                RecognitionMethod = "automation_ocr_hybrid_v2",
                RecognitionConfidence = 0.71d,
                RecognizedCustomerText = "客户问发货"
            });
        await service.LogOperationEventAsync("maintenance_probe", "capture", success: true, skipped: false, durationMs: 10, message: "ok");
        var sessionId = service.LogAiDraftsShown(
            new ConversationRecommendationSnapshot
            {
                PlatformHint = "wecom",
                RecognizedCustomerText = "客户问发货"
            },
            [
                new GeneratedReplyCandidate
                {
                    DisplayRank = 1,
                    Text = "今天可以发出",
                    ProviderType = "ollama",
                    ModelName = "qwen2.5:3b"
                }
            ],
            SearchOverlayMode.Phrase,
            "发货");
        await service.SaveRecognitionCorrectionAsync(
            sessionId,
            new ConversationRecommendationSnapshot
            {
                PlatformHint = "wecom",
                RecognizedCustomerText = "客户问发货",
                RecognitionMethod = "automation_ocr_hybrid_v2"
            },
            new ConversationRecognitionCorrection
            {
                CustomerText = "客户问发货",
                ContextText = "昨天已下单"
            });

        await WaitForTableCountAsync(databasePath, "conversation_logs", minimumCount: 1);
        await WaitForTableCountAsync(databasePath, "recommendation_events", minimumCount: 1);
        await WaitForTableCountAsync(databasePath, "recognition_corrections", minimumCount: 1);
        await WaitForTableCountAsync(databasePath, "operation_events", minimumCount: 1);

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            foreach (var tableName in new[] { "conversation_logs", "recommendation_events", "recognition_corrections", "operation_events" })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE {tableName} SET occurred_at = $occurredAt;";
                command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.AddDays(-45).ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
        }

        service.ApplySettings(new AppSettings
        {
            LogTextRetentionDays = 7,
            LogScreenshotRetentionDays = 30,
            LogScreenshotMaxSizeMb = 512
        });
        await service.RunMaintenanceAsync();

        await using var verifyConnection = new SqliteConnection($"Data Source={databasePath}");
        await verifyConnection.OpenAsync();
        foreach (var tableName in new[] { "conversation_logs", "recommendation_events", "recognition_corrections", "operation_events" })
        {
            await using var command = verifyConnection.CreateCommand();
            command.CommandText = $"SELECT COUNT(1) FROM {tableName};";
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(0, count);
        }
    }

    private static async Task WaitForTableCountAsync(string databasePath, string tableName, int minimumCount)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(1) FROM {tableName};";
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (count >= minimumCount)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for {tableName} to reach {minimumCount} rows.");
    }
}
