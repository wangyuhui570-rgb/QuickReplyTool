using System.Net.Http.Json;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QuickReply.CloudService;

namespace QuickReply.CloudService.Tests;

public sealed class KnowledgeCloudApiTests
{
    [Fact]
    public async Task GetReadyz_ReturnsRuntimeReadiness()
    {
        await using var factory = new CloudServiceFactory(enableAiSynthesis: false);

        var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<CloudServiceRuntimeInfoResponse>("/readyz");

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.True(response.DatabaseReady);
        Assert.Equal("QuickReply.CloudService", response.Service);
        Assert.False(response.AiSynthesisEnabled);
        Assert.True(response.AiReady);
    }

    [Fact]
    public async Task ManagementEndpoints_RequireBearerToken_WhenLoopbackBypassIsDisabled()
    {
        await using var factory = new CloudServiceFactory(
            allowLoopbackWithoutAuth: false,
            managementApiToken: "secret-token",
            enableAiSynthesis: false);

        var client = factory.CreateClient();
        var unauthorizedResponse = await client.GetAsync("/info");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-token");
        var authorizedResponse = await client.GetFromJsonAsync<CloudServiceRuntimeInfoResponse>("/info");
        Assert.NotNull(authorizedResponse);
        Assert.True(authorizedResponse!.DatabaseReady);
        Assert.True(authorizedResponse.ManagementApiTokenConfigured);
        Assert.False(authorizedResponse.LoopbackBypassEnabled);
    }

    [Fact]
    public async Task GetPublishedKnowledgeCards_ReturnsCardsUpdatedAfterSince()
    {
        await using var factory = new CloudServiceFactory();
        await factory.SeedApprovedCandidateAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<PublishedKnowledgeSyncResponse>(
            $"/api/quickreply/knowledge/published?sinceRevision=0&limit=20");

        Assert.NotNull(response);
        var card = Assert.Single(response!.Cards);
        Assert.Equal("qa", card.CandidateKind);
        Assert.Equal("wecom", card.Platform);
        Assert.Equal("SKU-1", card.ProductKey);
        Assert.Equal("这款今天能发吗", card.Question);
        Assert.True(response.LatestRevision > 0);
        Assert.Empty(response.RemovedCardIds);
    }

    [Fact]
    public async Task GetReviewQueue_ReturnsDraftCandidatesOrderedByConfidence()
    {
        await using var factory = new CloudServiceFactory();
        await factory.SeedCandidateAsync(new KnowledgeCandidateRecord
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Status = "draft",
            Platform = "jd_workbench",
            ProductKey = "SKU-JD-1",
            QuestionNormalized = "什么时候发货",
            AnswerDraft = "您好，今天可以安排发货。",
            Tags = ["发货", "时效"],
            Confidence = 0.84d,
            RiskLevel = "low",
            SourceCount = 3,
            ClusterKey = "jd|sku-jd-1|shipping",
            EvidenceSummary = "3 条已发送成功样本",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-20),
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-10)
        });

        var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<KnowledgeReviewQueueResponse>("/api/quickreply/knowledge/review/queue?limit=20");

        Assert.NotNull(response);
        var item = Assert.Single(response!.Items);
        Assert.Equal("draft", item.Status);
        Assert.Equal("jd_workbench", item.Platform);
    }

    [Fact]
    public async Task GetReviewQueue_SupportsCursorPaging()
    {
        await using var factory = new CloudServiceFactory();
        await factory.SeedCandidateAsync(new KnowledgeCandidateRecord
        {
            Id = Guid.Parse("23232323-2323-2323-2323-232323232323"),
            Status = "draft",
            Platform = "wecom",
            ProductKey = "SKU-2",
            QuestionNormalized = "支持退款吗",
            AnswerDraft = "支持退款，按售后流程处理。",
            Tags = ["售后"],
            Confidence = 0.93d,
            RiskLevel = "medium",
            SourceCount = 2,
            ClusterKey = "wecom|sku-2|refund",
            EvidenceSummary = "2 条样本",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-20),
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-10)
        });
        await factory.SeedCandidateAsync(new KnowledgeCandidateRecord
        {
            Id = Guid.Parse("24242424-2424-2424-2424-242424242424"),
            Status = "draft",
            Platform = "wecom",
            ProductKey = "SKU-3",
            QuestionNormalized = "今天能发吗",
            AnswerDraft = "今天可以安排发货。",
            Tags = ["发货"],
            Confidence = 0.82d,
            RiskLevel = "low",
            SourceCount = 2,
            ClusterKey = "wecom|sku-3|shipping",
            EvidenceSummary = "2 条样本",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-18),
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-8)
        });

        var client = factory.CreateClient();
        var page1 = await client.GetFromJsonAsync<KnowledgeReviewQueueResponse>("/api/quickreply/knowledge/review/queue?limit=1");
        Assert.NotNull(page1);
        Assert.True(page1!.HasMore);
        Assert.Single(page1.Items);
        Assert.False(string.IsNullOrWhiteSpace(page1.NextCursor));

        var page2 = await client.GetFromJsonAsync<KnowledgeReviewQueueResponse>(
            $"/api/quickreply/knowledge/review/queue?limit=1&cursor={Uri.EscapeDataString(page1.NextCursor)}");
        Assert.NotNull(page2);
        Assert.Single(page2!.Items);
        Assert.NotEqual(page1.Items[0].Id, page2.Items[0].Id);
    }

    [Fact]
    public async Task GetPublishedKnowledgeCards_SupportsRevisionCursorPaging()
    {
        await using var factory = new CloudServiceFactory();
        await factory.SeedApprovedCandidateAsync(Guid.Parse("12121212-1212-1212-1212-121212121212"));
        await factory.SeedApprovedCandidateAsync(Guid.Parse("13131313-1313-1313-1313-131313131313"));

        var client = factory.CreateClient();
        var page1 = await client.GetFromJsonAsync<PublishedKnowledgeSyncResponse>("/api/quickreply/knowledge/published?sinceRevision=0&limit=1");
        Assert.NotNull(page1);
        Assert.True(page1!.HasMore);
        Assert.Single(page1.Cards);
        Assert.True(page1.LatestRevision > 0);

        var page2 = await client.GetFromJsonAsync<PublishedKnowledgeSyncResponse>(
            $"/api/quickreply/knowledge/published?sinceRevision=0&cursor={Uri.EscapeDataString(page1.NextCursor)}&limit=1");
        Assert.NotNull(page2);
        Assert.Single(page2!.Cards);
        Assert.NotEqual(page1.Cards[0].Id, page2.Cards[0].Id);
    }

    [Fact]
    public async Task ClaimKnowledgeCandidates_PreventsDuplicateClaimsUntilReleased()
    {
        await using var factory = new CloudServiceFactory();
        var candidateId = Guid.Parse("25252525-2525-2525-2525-252525252525");
        await factory.SeedCandidateAsync(new KnowledgeCandidateRecord
        {
            Id = candidateId,
            Status = "draft",
            Platform = "wecom",
            ProductKey = "SKU-5",
            QuestionNormalized = "可以补开发票吗",
            AnswerDraft = "支持补开发票。",
            Tags = ["发票"],
            Confidence = 0.77d,
            RiskLevel = "medium",
            SourceCount = 2,
            ClusterKey = "wecom|sku-5|invoice",
            EvidenceSummary = "2 条样本",
            CreatedAt = DateTimeOffset.Now.AddMinutes(-12),
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-6)
        });

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<KnowledgeCloudStore>();

        var firstClaim = await store.ClaimKnowledgeCandidatesAsync(["draft"], 1, TimeSpan.FromMinutes(5));
        var firstCandidate = Assert.Single(firstClaim);
        Assert.False(string.IsNullOrWhiteSpace(firstCandidate.LeaseToken));

        var secondClaim = await store.ClaimKnowledgeCandidatesAsync(["draft"], 1, TimeSpan.FromMinutes(5));
        Assert.Empty(secondClaim);

        await store.ReleaseKnowledgeCandidateClaimAsync(candidateId, firstCandidate.LeaseToken);

        var thirdClaim = await store.ClaimKnowledgeCandidatesAsync(["draft"], 1, TimeSpan.FromMinutes(5));
        Assert.Single(thirdClaim);
    }

    [Fact]
    public async Task ApproveCandidate_PublishesCardAndRemovesItFromReviewQueue()
    {
        await using var factory = new CloudServiceFactory();
        var candidateId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await factory.SeedCandidateAsync(new KnowledgeCandidateRecord
        {
            Id = candidateId,
            Status = "draft",
            Platform = "wecom",
            ProductKey = "SKU-3",
            QuestionNormalized = "可以开发票吗",
            AnswerDraft = "您好，支持开票。",
            Tags = ["发票"],
            Confidence = 0.88d,
            RiskLevel = "medium",
            SourceCount = 6,
            ClusterKey = "wecom|sku-3|invoice",
            EvidenceSummary = "6 条纠错后稳定样本",
            CreatedAt = DateTimeOffset.Now.AddHours(-1),
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-5)
        });

        var client = factory.CreateClient();
        var approveResponse = await client.PostAsJsonAsync(
            $"/api/quickreply/knowledge/review/{candidateId}/approve",
            new ApproveKnowledgeCandidateRequest
            {
                AnswerFinal = "您好，这款商品支持开票，下单后可以备注开票信息。",
                Synonyms = ["开票", "发票"],
                Tags = ["发票", "售后"],
                UsageNotes = "仅限企业客户按流程申请",
                ReviewComment = "确认可发布",
                Version = "v1"
            });

        approveResponse.EnsureSuccessStatusCode();

        var published = await client.GetFromJsonAsync<PublishedKnowledgeSyncResponse>("/api/quickreply/knowledge/published");
        var card = Assert.Single(published!.Cards.Where(item => item.Id == candidateId));
        Assert.Equal("您好，这款商品支持开票，下单后可以备注开票信息。", card.Answer);

        var queue = await client.GetFromJsonAsync<KnowledgeReviewQueueResponse>("/api/quickreply/knowledge/review/queue");
        Assert.DoesNotContain(queue!.Items, item => item.Id == candidateId);
    }

    [Fact]
    public async Task RejectCandidate_MarksItRejectedAndHidesItFromReviewQueue()
    {
        await using var factory = new CloudServiceFactory();
        var candidateId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        await factory.SeedCandidateAsync(new KnowledgeCandidateRecord
        {
            Id = candidateId,
            Status = "reviewed",
            Platform = "pdd_workbench",
            ProductKey = "SKU-PDD-1",
            QuestionNormalized = "今天一定能到吗",
            AnswerDraft = "您好，时效以物流为准。",
            Tags = ["物流"],
            Confidence = 0.62d,
            RiskLevel = "high",
            SourceCount = 2,
            ClusterKey = "pdd|sku-pdd-1|delivery-time",
            EvidenceSummary = "样本少，时效承诺风险高",
            CreatedAt = DateTimeOffset.Now.AddHours(-2),
            UpdatedAt = DateTimeOffset.Now.AddMinutes(-8)
        });

        var client = factory.CreateClient();
        var rejectResponse = await client.PostAsJsonAsync(
            $"/api/quickreply/knowledge/review/{candidateId}/reject",
            new RejectKnowledgeCandidateRequest
            {
                Reason = "时效承诺风险高",
                ReviewComment = "要求补更多证据后再看"
            });

        rejectResponse.EnsureSuccessStatusCode();

        var queue = await client.GetFromJsonAsync<KnowledgeReviewQueueResponse>("/api/quickreply/knowledge/review/queue");
        Assert.DoesNotContain(queue!.Items, item => item.Id == candidateId);
    }

    [Fact]
    public async Task RemovePublishedCandidate_TracksAuditHistoryAndPublishesRemovalRevision()
    {
        await using var factory = new CloudServiceFactory();
        var candidateId = Guid.Parse("45454545-4545-4545-4545-454545454545");
        await factory.SeedApprovedCandidateAsync(candidateId);

        var client = factory.CreateClient();
        var publishedBeforeRemoval = await client.GetFromJsonAsync<PublishedKnowledgeSyncResponse>("/api/quickreply/knowledge/published?sinceRevision=0&limit=20");
        Assert.NotNull(publishedBeforeRemoval);
        var latestRevisionBeforeRemoval = publishedBeforeRemoval!.LatestRevision;

        var historyBeforeRemoval = await client.GetFromJsonAsync<KnowledgeReviewHistoryResponse>(
            $"/api/quickreply/knowledge/review/{candidateId}/history?limit=20");
        Assert.NotNull(historyBeforeRemoval);
        var approvedEvent = Assert.Single(historyBeforeRemoval!.Items);
        Assert.Equal("approved", approvedEvent.ActionType);
        Assert.False(string.IsNullOrWhiteSpace(approvedEvent.AfterSnapshotJson));
        Assert.Contains(candidateId.ToString(), approvedEvent.AfterSnapshotJson, StringComparison.OrdinalIgnoreCase);

        var removeResponse = await client.PostAsJsonAsync(
            $"/api/quickreply/knowledge/published/{candidateId}/remove",
            new RemovePublishedKnowledgeCardRequest
            {
                Reason = "知识已过期",
                ReviewComment = "停售，先下线",
                Reviewer = "tester@machine",
                ReviewSessionId = "session-remove-1"
            });
        removeResponse.EnsureSuccessStatusCode();

        var historyAfterRemoval = await client.GetFromJsonAsync<KnowledgeReviewHistoryResponse>(
            $"/api/quickreply/knowledge/review/{candidateId}/history?limit=20");
        Assert.NotNull(historyAfterRemoval);
        Assert.Collection(
            historyAfterRemoval!.Items,
            item =>
            {
                Assert.Equal("unpublished", item.ActionType);
                Assert.Equal("tester@machine", item.Reviewer);
                Assert.Equal("知识已过期", item.ReviewReason);
            },
            item => Assert.Equal("approved", item.ActionType));

        var publishedDelta = await client.GetFromJsonAsync<PublishedKnowledgeSyncResponse>(
            $"/api/quickreply/knowledge/published?sinceRevision={latestRevisionBeforeRemoval}&limit=20");
        Assert.NotNull(publishedDelta);
        Assert.Empty(publishedDelta!.Cards);
        Assert.Contains(candidateId, publishedDelta.RemovedCardIds);
    }

    [Fact]
    public async Task ImportLogsJob_CreatesDraftCandidateAndJobRun()
    {
        await using var factory = new CloudServiceFactory();
        var logsZipPath = factory.CreateImportableLogsZip();
        var client = factory.CreateClient();

        var importResponse = await client.PostAsJsonAsync(
            "/api/quickreply/knowledge/jobs/import-logs",
            new ImportKnowledgeCandidatesRequest
            {
                InputPath = logsZipPath
            });
        importResponse.EnsureSuccessStatusCode();

        var importResult = await importResponse.Content.ReadFromJsonAsync<KnowledgeImportRunResponse>();
        Assert.NotNull(importResult);
        Assert.Equal(1, importResult!.CandidateCount);
        Assert.Equal(1, importResult.SampleCount);

        var queue = await client.GetFromJsonAsync<KnowledgeReviewQueueResponse>("/api/quickreply/knowledge/review/queue");
        var item = Assert.Single(queue!.Items);
        Assert.Equal("可以开发票吗", item.QuestionNormalized);

        var runs = await client.GetFromJsonAsync<List<KnowledgeJobRunRecord>>("/api/quickreply/knowledge/jobs/runs");
        Assert.NotNull(runs);
        Assert.Equal(importResult.JobRunId, Assert.Single(runs!).Id);
    }

    [Fact]
    public async Task SynthesizeDrafts_TransitionsCandidateToReviewed()
    {
        await using var factory = new CloudServiceFactory();
        var logsZipPath = factory.CreateImportableLogsZip();
        var client = factory.CreateClient();

        var importResponse = await client.PostAsJsonAsync(
            "/api/quickreply/knowledge/jobs/import-logs",
            new ImportKnowledgeCandidatesRequest
            {
                InputPath = logsZipPath
            });
        importResponse.EnsureSuccessStatusCode();

        var synthesizeResponse = await client.PostAsync("/api/quickreply/knowledge/jobs/synthesize-drafts?limit=20", content: null);
        synthesizeResponse.EnsureSuccessStatusCode();

        var result = await synthesizeResponse.Content.ReadFromJsonAsync<KnowledgeImportRunResponse>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.CandidateCount);

        var queue = await client.GetFromJsonAsync<KnowledgeReviewQueueResponse>("/api/quickreply/knowledge/review/queue");
        var item = Assert.Single(queue!.Items);
        Assert.Equal("reviewed", item.Status);
        Assert.Contains("开发票", item.QuestionNormalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReviewSources_ReturnsCandidateSourceExcerpts()
    {
        await using var factory = new CloudServiceFactory();
        var logsZipPath = factory.CreateImportableLogsZip();
        var client = factory.CreateClient();

        var importResponse = await client.PostAsJsonAsync(
            "/api/quickreply/knowledge/jobs/import-logs",
            new ImportKnowledgeCandidatesRequest
            {
                InputPath = logsZipPath
            });
        importResponse.EnsureSuccessStatusCode();

        var queue = await client.GetFromJsonAsync<KnowledgeReviewQueueResponse>("/api/quickreply/knowledge/review/queue");
        var candidate = Assert.Single(queue!.Items);

        var sources = await client.GetFromJsonAsync<KnowledgeReviewSourcesResponse>(
            $"/api/quickreply/knowledge/review/{candidate.Id}/sources");

        var source = Assert.Single(sources!.Items);
        Assert.Equal("conversation", source.SourceType);
        Assert.Contains("开发票", source.Excerpt, StringComparison.Ordinal);
    }

    private sealed class CloudServiceFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _databaseDirectory = Path.Combine(Path.GetTempPath(), "QuickReplyCloudTests", Guid.NewGuid().ToString("N"));
        private readonly bool _allowLoopbackWithoutAuth;
        private readonly string _managementApiToken;
        private readonly bool _enableAiSynthesis;
        private readonly string _aiProvider;
        private readonly string _ollamaModelName;
        private string DatabasePath => Path.Combine(_databaseDirectory, "quickreply-cloud.db");

        public CloudServiceFactory(
            bool allowLoopbackWithoutAuth = true,
            string managementApiToken = "",
            bool enableAiSynthesis = true,
            string aiProvider = "auto",
            string ollamaModelName = "")
        {
            _allowLoopbackWithoutAuth = allowLoopbackWithoutAuth;
            _managementApiToken = managementApiToken;
            _enableAiSynthesis = enableAiSynthesis;
            _aiProvider = aiProvider;
            _ollamaModelName = ollamaModelName;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting($"{KnowledgeCloudOptions.SectionName}:DatabasePath", DatabasePath);
            builder.UseSetting($"{KnowledgeCloudOptions.SectionName}:AllowLoopbackWithoutAuth", _allowLoopbackWithoutAuth.ToString());
            builder.UseSetting($"{KnowledgeCloudOptions.SectionName}:ManagementApiToken", _managementApiToken);
            builder.UseSetting($"{KnowledgeCloudOptions.SectionName}:EnableAiSynthesis", _enableAiSynthesis.ToString());
            builder.UseSetting($"{KnowledgeCloudOptions.SectionName}:AiProvider", _aiProvider);
            builder.UseSetting($"{KnowledgeCloudOptions.SectionName}:OllamaModelName", _ollamaModelName);
        }

        public async Task SeedCandidateAsync(KnowledgeCandidateRecord candidate)
        {
            using var scope = Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<KnowledgeCloudStore>();
            await store.UpsertKnowledgeCandidateAsync(candidate);
        }

        public async Task SeedApprovedCandidateAsync(Guid candidateId)
        {
            await SeedCandidateAsync(new KnowledgeCandidateRecord
            {
                Id = candidateId,
                Status = "draft",
                Platform = "wecom",
                ProductKey = "SKU-1",
                QuestionNormalized = "这款今天能发吗",
                AnswerDraft = "您好，这款今天可以安排发出。",
                Tags = ["发货"],
                Confidence = 0.9d,
                RiskLevel = "low",
                SourceCount = 5,
                ClusterKey = "wecom|sku-1|shipping",
                EvidenceSummary = "5 条高置信度样本",
                CreatedAt = DateTimeOffset.Now.AddMinutes(-10),
                UpdatedAt = DateTimeOffset.Now.AddMinutes(-5)
            });

            using var scope = Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<KnowledgeCloudStore>();
            await store.ApproveKnowledgeCandidateAsync(
                candidateId,
                new ApproveKnowledgeCandidateRequest
                {
                    AnswerFinal = "您好，这款今天可以安排发出。",
                    Synonyms = ["发货时效"],
                    Tags = ["发货"],
                    UsageNotes = "仅限现货",
                    ReviewComment = "通过",
                    Version = "v1"
                });
        }

        public string CreateImportableLogsZip()
        {
            var sourceRoot = Path.Combine(_databaseDirectory, "import-source");
            var logsDirectory = Path.Combine(sourceRoot, "logs");
            Directory.CreateDirectory(logsDirectory);

            var dbPath = Path.Combine(logsDirectory, "conversation-logs.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE conversation_logs (
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
                        recognized_messages_json TEXT NOT NULL DEFAULT '',
                        reply_group_id TEXT NOT NULL DEFAULT '',
                        reply_group_index INTEGER NOT NULL DEFAULT 0,
                        data_quality_grade TEXT NOT NULL DEFAULT '',
                        app_version TEXT NOT NULL DEFAULT '',
                        recommendation_rule_version TEXT NOT NULL DEFAULT '',
                        recognized_product_id TEXT NOT NULL DEFAULT '',
                        recognized_product_name TEXT NOT NULL DEFAULT ''
                    );
                    CREATE TABLE recognition_corrections (
                        id TEXT PRIMARY KEY,
                        occurred_at TEXT NOT NULL,
                        recommendation_session_id TEXT NOT NULL DEFAULT '',
                        platform_hint TEXT NOT NULL DEFAULT '',
                        window_title TEXT NOT NULL DEFAULT '',
                        source_customer_text TEXT NOT NULL DEFAULT '',
                        corrected_customer_text TEXT NOT NULL DEFAULT '',
                        recognition_method TEXT NOT NULL DEFAULT '',
                        app_version TEXT NOT NULL DEFAULT '',
                        source_context_text TEXT NOT NULL DEFAULT '',
                        source_product_id TEXT NOT NULL DEFAULT '',
                        source_product_name TEXT NOT NULL DEFAULT '',
                        source_messages_json TEXT NOT NULL DEFAULT '',
                        corrected_context_text TEXT NOT NULL DEFAULT '',
                        corrected_product_id TEXT NOT NULL DEFAULT '',
                        corrected_product_name TEXT NOT NULL DEFAULT '',
                        corrected_messages_json TEXT NOT NULL DEFAULT ''
                    );
                    CREATE TABLE recommendation_events (
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
                        actual_payload_text TEXT NOT NULL DEFAULT '',
                        app_version TEXT NOT NULL DEFAULT '',
                        recommendation_rule_version TEXT NOT NULL DEFAULT ''
                    );
                    INSERT INTO conversation_logs (
                        id,
                        occurred_at,
                        trigger_source,
                        action_mode,
                        payload_type,
                        result_success,
                        platform_hint,
                        payload_text,
                        recognition_confidence,
                        recognized_customer_text,
                        recognized_product_id,
                        recognized_product_name)
                    VALUES (
                        '55555555-5555-5555-5555-555555555555',
                        '2026-04-16T12:00:00+08:00',
                        'search_overlay',
                        'send',
                        'text',
                        1,
                        'wecom',
                        '您好，这款商品支持开发票，下单后备注开票信息即可。',
                        0.88,
                        '可以开发票吗',
                        'SKU-INV-1',
                        '发票商品');

                    INSERT INTO conversation_logs (
                        id,
                        occurred_at,
                        trigger_source,
                        action_mode,
                        payload_type,
                        search_keyword,
                        result_success,
                        platform_hint,
                        payload_text,
                        recognition_confidence,
                        recognized_customer_text,
                        recognized_product_id,
                        recognized_product_name)
                    VALUES (
                        '66666666-6666-6666-6666-666666666666',
                        '2026-04-16T12:05:00+08:00',
                        'search_overlay',
                        'send',
                        'text',
                        'cor',
                        1,
                        'wecom',
                        'corki脚撑教程：https://v.youku.com/v_show/id_XMTI5MzI5ODc1Ng==.html',
                        0.12,
                        '',
                        '',
                        '');
                    """;
                command.ExecuteNonQuery();
                connection.Close();
            }

            var zipPath = Path.Combine(_databaseDirectory, "logs.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(sourceRoot, zipPath);
            return zipPath;
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            if (Directory.Exists(_databaseDirectory))
            {
                try
                {
                    Directory.Delete(_databaseDirectory, recursive: true);
                }
                catch
                {
                    // ignore cleanup issues in tests
                }
            }
        }
    }
}
