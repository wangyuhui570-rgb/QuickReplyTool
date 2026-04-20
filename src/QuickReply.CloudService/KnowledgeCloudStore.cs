using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

public sealed class KnowledgeCloudStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int DefaultPageSize = 100;

    private readonly string _databasePath;

    public KnowledgeCloudStore(IOptions<KnowledgeCloudOptions> options, IWebHostEnvironment environment)
    {
        var configuredPath = options.Value.DatabasePath;
        _databasePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS knowledge_candidates (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                candidate_kind TEXT NOT NULL DEFAULT 'qa',
                platform TEXT NOT NULL,
                product_key TEXT NOT NULL,
                question_normalized TEXT NOT NULL,
                answer_draft TEXT NOT NULL,
                answer_final TEXT NOT NULL,
                tags_json TEXT NOT NULL,
                confidence REAL NOT NULL,
                risk_level TEXT NOT NULL,
                source_count INTEGER NOT NULL,
                cluster_key TEXT NOT NULL,
                evidence_summary TEXT NOT NULL DEFAULT '',
                review_comment TEXT NOT NULL DEFAULT '',
                review_reason TEXT NOT NULL DEFAULT '',
                reviewed_by TEXT NOT NULL DEFAULT '',
                review_session_id TEXT NOT NULL DEFAULT '',
                reviewed_at TEXT NOT NULL DEFAULT '',
                lease_token TEXT NOT NULL DEFAULT '',
                lease_expires_at TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS knowledge_candidate_sources (
                id TEXT PRIMARY KEY,
                candidate_id TEXT NOT NULL,
                source_type TEXT NOT NULL,
                source_id TEXT NOT NULL,
                excerpt TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS published_knowledge_cards (
                id TEXT PRIMARY KEY,
                candidate_kind TEXT NOT NULL DEFAULT 'qa',
                platform TEXT NOT NULL,
                product_key TEXT NOT NULL,
                question TEXT NOT NULL,
                answer TEXT NOT NULL,
                synonyms_json TEXT NOT NULL,
                tags_json TEXT NOT NULL,
                usage_notes TEXT NOT NULL,
                version TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS published_knowledge_card_removals (
                id TEXT PRIMARY KEY,
                revision INTEGER NOT NULL DEFAULT 0,
                removed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS published_knowledge_changes (
                revision INTEGER PRIMARY KEY AUTOINCREMENT,
                card_id TEXT NOT NULL,
                change_type TEXT NOT NULL,
                changed_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS knowledge_job_runs (
                id TEXT PRIMARY KEY,
                sample_count INTEGER NOT NULL DEFAULT 0,
                candidate_count INTEGER NOT NULL DEFAULT 0,
                publish_count INTEGER NOT NULL DEFAULT 0,
                reject_count INTEGER NOT NULL DEFAULT 0,
                avg_confidence REAL NOT NULL DEFAULT 0,
                failed_cluster_count INTEGER NOT NULL DEFAULT 0,
                failed_synthesize_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS knowledge_review_events (
                id TEXT PRIMARY KEY,
                candidate_id TEXT NOT NULL,
                action_type TEXT NOT NULL,
                reviewer TEXT NOT NULL DEFAULT '',
                review_session_id TEXT NOT NULL DEFAULT '',
                review_comment TEXT NOT NULL DEFAULT '',
                review_reason TEXT NOT NULL DEFAULT '',
                before_snapshot_json TEXT NOT NULL DEFAULT '',
                after_snapshot_json TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_knowledge_candidates_status_updated_at
                ON knowledge_candidates(status, updated_at DESC);

            CREATE INDEX IF NOT EXISTS ix_published_knowledge_cards_updated_at
                ON published_knowledge_cards(updated_at DESC);

            CREATE INDEX IF NOT EXISTS ix_published_knowledge_changes_revision
                ON published_knowledge_changes(revision ASC);

            CREATE INDEX IF NOT EXISTS ix_knowledge_review_events_candidate_created_at
                ON knowledge_review_events(candidate_id, created_at DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureCandidateKindColumnAsync(connection, cancellationToken);
        await EnsureColumnAsync(connection, "published_knowledge_cards", "candidate_kind", "TEXT NOT NULL DEFAULT 'qa'", cancellationToken);
        await EnsureColumnAsync(connection, "published_knowledge_cards", "revision", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "published_knowledge_card_removals", "revision", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, "knowledge_candidates", "reviewed_by", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "knowledge_candidates", "review_session_id", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "knowledge_candidates", "reviewed_at", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "knowledge_candidates", "lease_token", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "knowledge_candidates", "lease_expires_at", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await BackfillPublishedKnowledgeChangesAsync(connection, cancellationToken);
    }

    public async Task<KnowledgeReviewQueueResponse> GetKnowledgeReviewQueuePageAsync(
        string? cursor = null,
        int limit = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var pageSize = NormalizePageSize(limit);
        var offset = ParseOffsetCursor(cursor);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var totalCount = 0;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText =
                """
                SELECT COUNT(1)
                FROM knowledge_candidates
                WHERE status IN ('draft', 'reviewed');
                """;
            totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   status,
                   candidate_kind,
                   platform,
                   product_key,
                   question_normalized,
                   answer_draft,
                   tags_json,
                   confidence,
                   risk_level,
                   source_count,
                   cluster_key,
                   evidence_summary,
                   updated_at
            FROM knowledge_candidates
            WHERE status IN ('draft', 'reviewed')
            ORDER BY confidence DESC, updated_at DESC, id ASC
            LIMIT @limit OFFSET @offset;
            """;
        command.Parameters.AddWithValue("@limit", pageSize);
        command.Parameters.AddWithValue("@offset", offset);

        var items = new List<KnowledgeReviewQueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new KnowledgeReviewQueueItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                Status = reader.GetString(1),
                CandidateKind = reader.GetString(2),
                Platform = reader.GetString(3),
                ProductKey = reader.GetString(4),
                QuestionNormalized = reader.GetString(5),
                AnswerDraft = reader.GetString(6),
                Tags = DeserializeStringList(reader.GetString(7)),
                Confidence = reader.GetDouble(8),
                RiskLevel = reader.GetString(9),
                SourceCount = reader.GetInt32(10),
                ClusterKey = reader.GetString(11),
                EvidenceSummary = reader.GetString(12),
                UpdatedAt = ParseDateTimeOffset(reader.GetString(13))
            });
        }

        var consumed = offset + items.Count;
        return new KnowledgeReviewQueueResponse
        {
            Items = items,
            HasMore = consumed < totalCount,
            NextCursor = consumed < totalCount ? consumed.ToString() : string.Empty
        };
    }

    public async Task<PublishedKnowledgeSyncResponse> GetPublishedKnowledgeCardsAsync(
        long? sinceRevision,
        string? cursor,
        int limit,
        DateTimeOffset? since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var latestRevision = await GetLatestPublishedKnowledgeRevisionAsync(connection, cancellationToken);
        var pageSize = NormalizePageSize(limit);
        var effectiveCursor = Math.Max(
            sinceRevision.GetValueOrDefault(0),
            ParseRevisionCursor(cursor));

        if (sinceRevision.GetValueOrDefault(0) <= 0 && string.IsNullOrWhiteSpace(cursor) && since is not null)
        {
            return await GetPublishedKnowledgeCardsByTimestampAsync(connection, since, latestRevision, cancellationToken);
        }

        var changes = new List<(long Revision, Guid CardId, string ChangeType, DateTimeOffset ChangedAt)>();
        await using (var changeCommand = connection.CreateCommand())
        {
            changeCommand.CommandText =
                """
                SELECT revision,
                       card_id,
                       change_type,
                       changed_at
                FROM published_knowledge_changes
                WHERE revision > @cursor
                ORDER BY revision ASC
                LIMIT @limit;
                """;
            changeCommand.Parameters.AddWithValue("@cursor", effectiveCursor);
            changeCommand.Parameters.AddWithValue("@limit", pageSize);

            await using var reader = await changeCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                changes.Add((
                    reader.GetInt64(0),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    ParseDateTimeOffset(reader.GetString(3))));
            }
        }

        if (changes.Count == 0)
        {
            return new PublishedKnowledgeSyncResponse
            {
                KnowledgeVersion = latestRevision <= 0 ? "knowledge-empty" : $"knowledge-r{latestRevision}",
                GeneratedAt = DateTimeOffset.Now,
                LatestRevision = latestRevision,
                HasMore = false,
                NextCursor = string.Empty,
                Cards = [],
                RemovedCardIds = []
            };
        }

        var latestBatchRevision = changes.Max(change => change.Revision);
        var hasMore = latestBatchRevision < latestRevision;
        var latestChangesByCard = changes
            .GroupBy(change => change.CardId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Revision).Last(), EqualityComparer<Guid>.Default);

        var removedIds = latestChangesByCard.Values
            .Where(change => string.Equals(change.ChangeType, "remove", StringComparison.OrdinalIgnoreCase))
            .Select(change => change.CardId)
            .ToArray();
        var cardIds = latestChangesByCard.Values
            .Where(change => string.Equals(change.ChangeType, "upsert", StringComparison.OrdinalIgnoreCase))
            .Select(change => change.CardId)
            .ToArray();
        var cards = await LoadPublishedKnowledgeCardsByIdsAsync(connection, cardIds, cancellationToken);
        var generatedAt = changes.Max(change => change.ChangedAt);

        return new PublishedKnowledgeSyncResponse
        {
            KnowledgeVersion = $"knowledge-r{latestBatchRevision}",
            GeneratedAt = generatedAt,
            LatestRevision = latestBatchRevision,
            HasMore = hasMore,
            NextCursor = hasMore ? latestBatchRevision.ToString() : string.Empty,
            Cards = cards,
            RemovedCardIds = removedIds
        };
    }

    public async Task<IReadOnlyList<KnowledgeJobRunRecord>> GetKnowledgeJobRunsAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   sample_count,
                   candidate_count,
                   publish_count,
                   reject_count,
                   avg_confidence,
                   failed_cluster_count,
                   failed_synthesize_count,
                   created_at
            FROM knowledge_job_runs
            ORDER BY created_at DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var runs = new List<KnowledgeJobRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new KnowledgeJobRunRecord
            {
                Id = Guid.Parse(reader.GetString(0)),
                SampleCount = reader.GetInt32(1),
                CandidateCount = reader.GetInt32(2),
                PublishCount = reader.GetInt32(3),
                RejectCount = reader.GetInt32(4),
                AvgConfidence = reader.GetDouble(5),
                FailedClusterCount = reader.GetInt32(6),
                FailedSynthesizeCount = reader.GetInt32(7),
                CreatedAt = ParseDateTimeOffset(reader.GetString(8))
            });
        }

        return runs;
    }

    public async Task InsertKnowledgeJobRunAsync(
        KnowledgeJobRunRecord run,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_job_runs (
                id,
                sample_count,
                candidate_count,
                publish_count,
                reject_count,
                avg_confidence,
                failed_cluster_count,
                failed_synthesize_count,
                created_at)
            VALUES (
                @id,
                @sampleCount,
                @candidateCount,
                @publishCount,
                @rejectCount,
                @avgConfidence,
                @failedClusterCount,
                @failedSynthesizeCount,
                @createdAt);
            """;
        command.Parameters.AddWithValue("@id", run.Id.ToString());
        command.Parameters.AddWithValue("@sampleCount", run.SampleCount);
        command.Parameters.AddWithValue("@candidateCount", run.CandidateCount);
        command.Parameters.AddWithValue("@publishCount", run.PublishCount);
        command.Parameters.AddWithValue("@rejectCount", run.RejectCount);
        command.Parameters.AddWithValue("@avgConfidence", run.AvgConfidence);
        command.Parameters.AddWithValue("@failedClusterCount", run.FailedClusterCount);
        command.Parameters.AddWithValue("@failedSynthesizeCount", run.FailedSynthesizeCount);
        command.Parameters.AddWithValue("@createdAt", run.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeCandidateRecord>> GetKnowledgeCandidatesByStatusAsync(
        IReadOnlyCollection<string> statuses,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (statuses.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>();
        var index = 0;
        foreach (var status in statuses)
        {
            var name = $"@status{index++}";
            parameterNames.Add(name);
            command.Parameters.AddWithValue(name, status);
        }

        command.CommandText =
            $"""
            SELECT id,
                   status,
                   candidate_kind,
                   platform,
                   product_key,
                   question_normalized,
                   answer_draft,
                   answer_final,
                   tags_json,
                   confidence,
                   risk_level,
                   source_count,
                   cluster_key,
                   evidence_summary,
                   review_comment,
                   review_reason,
                   reviewed_by,
                   review_session_id,
                   reviewed_at,
                   lease_token,
                   lease_expires_at,
                   created_at,
                   updated_at
            FROM knowledge_candidates
            WHERE status IN ({string.Join(", ", parameterNames)})
            ORDER BY updated_at ASC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var items = new List<KnowledgeCandidateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new KnowledgeCandidateRecord
            {
                Id = Guid.Parse(reader.GetString(0)),
                Status = reader.GetString(1),
                CandidateKind = reader.GetString(2),
                Platform = reader.GetString(3),
                ProductKey = reader.GetString(4),
                QuestionNormalized = reader.GetString(5),
                AnswerDraft = reader.GetString(6),
                AnswerFinal = reader.GetString(7),
                Tags = DeserializeStringList(reader.GetString(8)),
                Confidence = reader.GetDouble(9),
                RiskLevel = reader.GetString(10),
                SourceCount = reader.GetInt32(11),
                ClusterKey = reader.GetString(12),
                EvidenceSummary = reader.GetString(13),
                ReviewComment = reader.GetString(14),
                ReviewReason = reader.GetString(15),
                ReviewedBy = reader.GetString(16),
                ReviewSessionId = reader.GetString(17),
                ReviewedAt = string.IsNullOrWhiteSpace(reader.GetString(18)) ? null : ParseDateTimeOffset(reader.GetString(18)),
                LeaseToken = reader.GetString(19),
                LeaseExpiresAt = string.IsNullOrWhiteSpace(reader.GetString(20)) ? null : ParseDateTimeOffset(reader.GetString(20)),
                CreatedAt = ParseDateTimeOffset(reader.GetString(21)),
                UpdatedAt = ParseDateTimeOffset(reader.GetString(22))
            });
        }

        return items;
    }

    public async Task<IReadOnlyList<KnowledgeCandidateRecord>> ClaimKnowledgeCandidatesAsync(
        IReadOnlyCollection<string> statuses,
        int limit,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (statuses.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.Now;
        var leaseToken = Guid.NewGuid().ToString("N");
        var leaseExpiresAt = now.Add(leaseDuration);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var parameterNames = new List<string>();
        var index = 0;
        foreach (var status in statuses)
        {
            var name = $"@status{index++}";
            parameterNames.Add(name);
        }

        var claimedIds = new List<Guid>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            foreach (var status in statuses.Select((value, idx) => (value, idx)))
            {
                selectCommand.Parameters.AddWithValue(parameterNames[status.idx], status.value);
            }

            selectCommand.CommandText =
                $"""
                SELECT id
                FROM knowledge_candidates
                WHERE status IN ({string.Join(", ", parameterNames)})
                  AND (lease_expires_at = '' OR lease_expires_at < @now)
                ORDER BY updated_at ASC, id ASC
                LIMIT @limit;
                """;
            selectCommand.Parameters.AddWithValue("@now", now.ToString("O"));
            selectCommand.Parameters.AddWithValue("@limit", Math.Max(1, limit));

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimedIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        if (claimedIds.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        foreach (var candidateId in claimedIds)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                UPDATE knowledge_candidates
                SET lease_token = @leaseToken,
                    lease_expires_at = @leaseExpiresAt
                WHERE id = @id
                  AND (lease_expires_at = '' OR lease_expires_at < @now);
                """;
            updateCommand.Parameters.AddWithValue("@leaseToken", leaseToken);
            updateCommand.Parameters.AddWithValue("@leaseExpiresAt", leaseExpiresAt.ToString("O"));
            updateCommand.Parameters.AddWithValue("@now", now.ToString("O"));
            updateCommand.Parameters.AddWithValue("@id", candidateId.ToString());
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetKnowledgeCandidatesByLeaseTokenAsync(leaseToken, cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeCandidateSourceRecord>> GetKnowledgeCandidateSourcesAsync(
        Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   candidate_id,
                   source_type,
                   source_id,
                   excerpt,
                   created_at
            FROM knowledge_candidate_sources
            WHERE candidate_id = @candidateId
            ORDER BY created_at ASC;
            """;
        command.Parameters.AddWithValue("@candidateId", candidateId.ToString());

        var items = new List<KnowledgeCandidateSourceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new KnowledgeCandidateSourceRecord
            {
                Id = Guid.Parse(reader.GetString(0)),
                CandidateId = Guid.Parse(reader.GetString(1)),
                SourceType = reader.GetString(2),
                SourceId = reader.GetString(3),
                Excerpt = reader.GetString(4),
                CreatedAt = ParseDateTimeOffset(reader.GetString(5))
            });
        }

        return items;
    }

    public async Task<KnowledgeReviewSourcesResponse> GetKnowledgeCandidateSourcesPageAsync(
        Guid candidateId,
        string? cursor = null,
        int limit = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var pageSize = NormalizePageSize(limit);
        var offset = ParseOffsetCursor(cursor);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var totalCount = 0;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(1) FROM knowledge_candidate_sources WHERE candidate_id = @candidateId;";
            countCommand.Parameters.AddWithValue("@candidateId", candidateId.ToString());
            totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<KnowledgeCandidateSourceRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id,
                       candidate_id,
                       source_type,
                       source_id,
                       excerpt,
                       created_at
                FROM knowledge_candidate_sources
                WHERE candidate_id = @candidateId
                ORDER BY created_at ASC, id ASC
                LIMIT @limit OFFSET @offset;
                """;
            command.Parameters.AddWithValue("@candidateId", candidateId.ToString());
            command.Parameters.AddWithValue("@limit", pageSize);
            command.Parameters.AddWithValue("@offset", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new KnowledgeCandidateSourceRecord
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    CandidateId = Guid.Parse(reader.GetString(1)),
                    SourceType = reader.GetString(2),
                    SourceId = reader.GetString(3),
                    Excerpt = reader.GetString(4),
                    CreatedAt = ParseDateTimeOffset(reader.GetString(5))
                });
            }
        }

        var consumed = offset + items.Count;
        return new KnowledgeReviewSourcesResponse
        {
            CandidateId = candidateId,
            Items = items,
            HasMore = consumed < totalCount,
            NextCursor = consumed < totalCount ? consumed.ToString() : string.Empty
        };
    }

    public async Task<KnowledgeReviewQueueItem?> ApproveKnowledgeCandidateAsync(
        Guid candidateId,
        ApproveKnowledgeCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var candidate = await GetCandidateAsync(connection, candidateId, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var finalAnswer = string.IsNullOrWhiteSpace(request.AnswerFinal)
            ? candidate.AnswerDraft
            : request.AnswerFinal.Trim();
        var reviewedBy = request.Reviewer?.Trim() ?? string.Empty;
        var reviewSessionId = request.ReviewSessionId?.Trim() ?? string.Empty;
        var version = string.IsNullOrWhiteSpace(request.Version)
            ? $"v{now:yyyyMMddHHmmss}"
            : request.Version.Trim();
        var mergedTags = request.Tags.Count == 0 ? candidate.Tags : request.Tags.ToList();
        var beforeSnapshot = SerializeObject(candidate);
        var publishRevision = await InsertPublishedKnowledgeChangeAsync(connection, transaction, candidateId, "upsert", now, cancellationToken);

        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                UPDATE knowledge_candidates
                SET status = 'approved',
                    answer_final = @answerFinal,
                    tags_json = @tagsJson,
                    review_comment = @reviewComment,
                    review_reason = '',
                    reviewed_by = @reviewedBy,
                    review_session_id = @reviewSessionId,
                    reviewed_at = @reviewedAt,
                    lease_token = '',
                    lease_expires_at = '',
                    updated_at = @updatedAt
                WHERE id = @id;
                """;
            updateCommand.Parameters.AddWithValue("@answerFinal", finalAnswer);
            updateCommand.Parameters.AddWithValue("@tagsJson", SerializeStringList(mergedTags));
            updateCommand.Parameters.AddWithValue("@reviewComment", request.ReviewComment ?? string.Empty);
            updateCommand.Parameters.AddWithValue("@reviewedBy", reviewedBy);
            updateCommand.Parameters.AddWithValue("@reviewSessionId", reviewSessionId);
            updateCommand.Parameters.AddWithValue("@reviewedAt", now.ToString("O"));
            updateCommand.Parameters.AddWithValue("@updatedAt", now.ToString("O"));
            updateCommand.Parameters.AddWithValue("@id", candidateId.ToString());
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var publishCommand = connection.CreateCommand())
        {
            publishCommand.Transaction = transaction;
            publishCommand.CommandText =
                """
                INSERT INTO published_knowledge_cards (
                    id,
                    candidate_kind,
                    platform,
                    product_key,
                    question,
                    answer,
                    synonyms_json,
                    tags_json,
                    usage_notes,
                    version,
                    revision,
                    updated_at)
                VALUES (
                    @id,
                    @candidateKind,
                    @platform,
                    @productKey,
                    @question,
                    @answer,
                    @synonymsJson,
                    @tagsJson,
                    @usageNotes,
                    @version,
                    @revision,
                    @updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    candidate_kind = excluded.candidate_kind,
                    platform = excluded.platform,
                    product_key = excluded.product_key,
                    question = excluded.question,
                    answer = excluded.answer,
                    synonyms_json = excluded.synonyms_json,
                    tags_json = excluded.tags_json,
                    usage_notes = excluded.usage_notes,
                    version = excluded.version,
                    revision = excluded.revision,
                    updated_at = excluded.updated_at;
                """;
            publishCommand.Parameters.AddWithValue("@id", candidateId.ToString());
            publishCommand.Parameters.AddWithValue("@candidateKind", candidate.CandidateKind);
            publishCommand.Parameters.AddWithValue("@platform", candidate.Platform);
            publishCommand.Parameters.AddWithValue("@productKey", candidate.ProductKey);
            publishCommand.Parameters.AddWithValue("@question", candidate.QuestionNormalized);
            publishCommand.Parameters.AddWithValue("@answer", finalAnswer);
            publishCommand.Parameters.AddWithValue("@synonymsJson", SerializeStringList(request.Synonyms));
            publishCommand.Parameters.AddWithValue("@tagsJson", SerializeStringList(mergedTags));
            publishCommand.Parameters.AddWithValue("@usageNotes", request.UsageNotes ?? string.Empty);
            publishCommand.Parameters.AddWithValue("@version", version);
            publishCommand.Parameters.AddWithValue("@revision", publishRevision);
            publishCommand.Parameters.AddWithValue("@updatedAt", now.ToString("O"));
            await publishCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteRemovalCommand = connection.CreateCommand())
        {
            deleteRemovalCommand.Transaction = transaction;
            deleteRemovalCommand.CommandText = "DELETE FROM published_knowledge_card_removals WHERE id = @id;";
            deleteRemovalCommand.Parameters.AddWithValue("@id", candidateId.ToString());
            await deleteRemovalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var afterSnapshot = SerializeObject(new
        {
            candidateId,
            status = "approved",
            answer = finalAnswer,
            tags = mergedTags,
            version,
            revision = publishRevision,
            reviewedBy,
            reviewSessionId,
            reviewedAt = now
        });

        await InsertKnowledgeReviewEventAsync(
            connection,
            transaction,
            candidateId,
            "approved",
            reviewedBy,
            reviewSessionId,
            request.ReviewComment ?? string.Empty,
            string.Empty,
            beforeSnapshot,
            afterSnapshot,
            now,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new KnowledgeReviewQueueItem
        {
            Id = candidate.Id,
            Status = "approved",
            CandidateKind = candidate.CandidateKind,
            Platform = candidate.Platform,
            ProductKey = candidate.ProductKey,
            QuestionNormalized = candidate.QuestionNormalized,
            AnswerDraft = finalAnswer,
            Tags = mergedTags,
            Confidence = candidate.Confidence,
            RiskLevel = candidate.RiskLevel,
            SourceCount = candidate.SourceCount,
            ClusterKey = candidate.ClusterKey,
            EvidenceSummary = candidate.EvidenceSummary,
            UpdatedAt = now
        };
    }

    public async Task<KnowledgeReviewQueueItem?> RejectKnowledgeCandidateAsync(
        Guid candidateId,
        RejectKnowledgeCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var candidate = await GetCandidateAsync(connection, candidateId, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var reviewedBy = request.Reviewer?.Trim() ?? string.Empty;
        var reviewSessionId = request.ReviewSessionId?.Trim() ?? string.Empty;
        var beforeSnapshot = SerializeObject(candidate);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE knowledge_candidates
            SET status = 'rejected',
                review_reason = @reason,
                review_comment = @reviewComment,
                reviewed_by = @reviewedBy,
                review_session_id = @reviewSessionId,
                reviewed_at = @reviewedAt,
                lease_token = '',
                lease_expires_at = '',
                updated_at = @updatedAt
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@reason", request.Reason ?? string.Empty);
        command.Parameters.AddWithValue("@reviewComment", request.ReviewComment ?? string.Empty);
        command.Parameters.AddWithValue("@reviewedBy", reviewedBy);
        command.Parameters.AddWithValue("@reviewSessionId", reviewSessionId);
        command.Parameters.AddWithValue("@reviewedAt", now.ToString("O"));
        command.Parameters.AddWithValue("@updatedAt", now.ToString("O"));
        command.Parameters.AddWithValue("@id", candidateId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);

        var afterSnapshot = SerializeObject(new
        {
            candidateId,
            status = "rejected",
            reason = request.Reason ?? string.Empty,
            reviewComment = request.ReviewComment ?? string.Empty,
            reviewedBy,
            reviewSessionId,
            reviewedAt = now
        });
        await InsertKnowledgeReviewEventAsync(
            connection,
            transaction,
            candidateId,
            "rejected",
            reviewedBy,
            reviewSessionId,
            request.ReviewComment ?? string.Empty,
            request.Reason ?? string.Empty,
            beforeSnapshot,
            afterSnapshot,
            now,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new KnowledgeReviewQueueItem
        {
            Id = candidate.Id,
            Status = "rejected",
            CandidateKind = candidate.CandidateKind,
            Platform = candidate.Platform,
            ProductKey = candidate.ProductKey,
            QuestionNormalized = candidate.QuestionNormalized,
            AnswerDraft = candidate.AnswerDraft,
            Tags = candidate.Tags,
            Confidence = candidate.Confidence,
            RiskLevel = candidate.RiskLevel,
            SourceCount = candidate.SourceCount,
            ClusterKey = candidate.ClusterKey,
            EvidenceSummary = candidate.EvidenceSummary,
            UpdatedAt = now
        };
    }

    public async Task<bool> RemovePublishedKnowledgeCardAsync(
        Guid candidateId,
        RemovePublishedKnowledgeCardRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        var publishedCard = await GetPublishedKnowledgeCardAsync(connection, candidateId, cancellationToken);
        if (publishedCard is null)
        {
            return false;
        }

        var candidate = await GetCandidateAsync(connection, candidateId, cancellationToken);
        var now = DateTimeOffset.Now;
        var reviewedBy = request.Reviewer?.Trim() ?? string.Empty;
        var reviewSessionId = request.ReviewSessionId?.Trim() ?? string.Empty;
        var beforeSnapshot = SerializeObject(new
        {
            Candidate = candidate,
            PublishedCard = publishedCard
        });
        var removalRevision = await InsertPublishedKnowledgeChangeAsync(connection, transaction, candidateId, "remove", now, cancellationToken);

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM published_knowledge_cards WHERE id = @id;";
            deleteCommand.Parameters.AddWithValue("@id", candidateId.ToString());
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var removalCommand = connection.CreateCommand())
        {
            removalCommand.Transaction = transaction;
            removalCommand.CommandText =
                """
                INSERT INTO published_knowledge_card_removals (
                    id,
                    revision,
                    removed_at)
                VALUES (
                    @id,
                    @revision,
                    @removedAt)
                ON CONFLICT(id) DO UPDATE SET
                    revision = excluded.revision,
                    removed_at = excluded.removed_at;
                """;
            removalCommand.Parameters.AddWithValue("@id", candidateId.ToString());
            removalCommand.Parameters.AddWithValue("@revision", removalRevision);
            removalCommand.Parameters.AddWithValue("@removedAt", now.ToString("O"));
            await removalCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (candidate is not null)
        {
            await using var archiveCommand = connection.CreateCommand();
            archiveCommand.Transaction = transaction;
            archiveCommand.CommandText =
                """
                UPDATE knowledge_candidates
                SET status = 'archived',
                    review_reason = @reason,
                    review_comment = @reviewComment,
                    reviewed_by = @reviewedBy,
                    review_session_id = @reviewSessionId,
                    reviewed_at = @reviewedAt,
                    updated_at = @updatedAt
                WHERE id = @id;
                """;
            archiveCommand.Parameters.AddWithValue("@reason", request.Reason ?? string.Empty);
            archiveCommand.Parameters.AddWithValue("@reviewComment", request.ReviewComment ?? string.Empty);
            archiveCommand.Parameters.AddWithValue("@reviewedBy", reviewedBy);
            archiveCommand.Parameters.AddWithValue("@reviewSessionId", reviewSessionId);
            archiveCommand.Parameters.AddWithValue("@reviewedAt", now.ToString("O"));
            archiveCommand.Parameters.AddWithValue("@updatedAt", now.ToString("O"));
            archiveCommand.Parameters.AddWithValue("@id", candidateId.ToString());
            await archiveCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var afterSnapshot = SerializeObject(new
        {
            candidateId,
            status = candidate is null ? "unpublished" : "archived",
            reason = request.Reason ?? string.Empty,
            reviewComment = request.ReviewComment ?? string.Empty,
            reviewedBy,
            reviewSessionId,
            revision = removalRevision,
            removedAt = now
        });

        await InsertKnowledgeReviewEventAsync(
            connection,
            transaction,
            candidateId,
            "unpublished",
            reviewedBy,
            reviewSessionId,
            request.ReviewComment ?? string.Empty,
            request.Reason ?? string.Empty,
            beforeSnapshot,
            afterSnapshot,
            now,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task UpsertKnowledgeCandidateAsync(
        KnowledgeCandidateRecord candidate,
        IReadOnlyCollection<KnowledgeCandidateSourceRecord>? sources = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO knowledge_candidates (
                    id,
                    status,
                    candidate_kind,
                    platform,
                    product_key,
                    question_normalized,
                    answer_draft,
                    answer_final,
                    tags_json,
                    confidence,
                    risk_level,
                    source_count,
                    cluster_key,
                    evidence_summary,
                    review_comment,
                    review_reason,
                    reviewed_by,
                    review_session_id,
                    reviewed_at,
                    lease_token,
                    lease_expires_at,
                    created_at,
                    updated_at)
                VALUES (
                    @id,
                    @status,
                    @candidateKind,
                    @platform,
                    @productKey,
                    @questionNormalized,
                    @answerDraft,
                    @answerFinal,
                    @tagsJson,
                    @confidence,
                    @riskLevel,
                    @sourceCount,
                    @clusterKey,
                    @evidenceSummary,
                    @reviewComment,
                    @reviewReason,
                    @reviewedBy,
                    @reviewSessionId,
                    @reviewedAt,
                    @leaseToken,
                    @leaseExpiresAt,
                    @createdAt,
                    @updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    candidate_kind = excluded.candidate_kind,
                    platform = excluded.platform,
                    product_key = excluded.product_key,
                    question_normalized = excluded.question_normalized,
                    answer_draft = excluded.answer_draft,
                    answer_final = excluded.answer_final,
                    tags_json = excluded.tags_json,
                    confidence = excluded.confidence,
                    risk_level = excluded.risk_level,
                    source_count = excluded.source_count,
                    cluster_key = excluded.cluster_key,
                    evidence_summary = excluded.evidence_summary,
                    review_comment = excluded.review_comment,
                    review_reason = excluded.review_reason,
                    reviewed_by = excluded.reviewed_by,
                    review_session_id = excluded.review_session_id,
                    reviewed_at = excluded.reviewed_at,
                    lease_token = excluded.lease_token,
                    lease_expires_at = excluded.lease_expires_at,
                    created_at = excluded.created_at,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("@id", candidate.Id.ToString());
            command.Parameters.AddWithValue("@status", candidate.Status);
            command.Parameters.AddWithValue("@candidateKind", KnowledgeCandidateQualityRules.NormalizeCandidateKind(candidate.CandidateKind));
            command.Parameters.AddWithValue("@platform", candidate.Platform);
            command.Parameters.AddWithValue("@productKey", candidate.ProductKey);
            command.Parameters.AddWithValue("@questionNormalized", candidate.QuestionNormalized);
            command.Parameters.AddWithValue("@answerDraft", candidate.AnswerDraft);
            command.Parameters.AddWithValue("@answerFinal", candidate.AnswerFinal);
            command.Parameters.AddWithValue("@tagsJson", SerializeStringList(candidate.Tags));
            command.Parameters.AddWithValue("@confidence", candidate.Confidence);
            command.Parameters.AddWithValue("@riskLevel", candidate.RiskLevel);
            command.Parameters.AddWithValue("@sourceCount", candidate.SourceCount);
            command.Parameters.AddWithValue("@clusterKey", candidate.ClusterKey);
            command.Parameters.AddWithValue("@evidenceSummary", candidate.EvidenceSummary);
            command.Parameters.AddWithValue("@reviewComment", candidate.ReviewComment);
            command.Parameters.AddWithValue("@reviewReason", candidate.ReviewReason);
            command.Parameters.AddWithValue("@reviewedBy", candidate.ReviewedBy);
            command.Parameters.AddWithValue("@reviewSessionId", candidate.ReviewSessionId);
            command.Parameters.AddWithValue("@reviewedAt", candidate.ReviewedAt?.ToString("O") ?? string.Empty);
            command.Parameters.AddWithValue("@leaseToken", candidate.LeaseToken ?? string.Empty);
            command.Parameters.AddWithValue("@leaseExpiresAt", candidate.LeaseExpiresAt?.ToString("O") ?? string.Empty);
            command.Parameters.AddWithValue("@createdAt", candidate.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("@updatedAt", candidate.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (sources is not null && sources.Count > 0)
        {
            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM knowledge_candidate_sources WHERE candidate_id = @candidateId;";
                deleteCommand.Parameters.AddWithValue("@candidateId", candidate.Id.ToString());
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var source in sources)
            {
                await using var sourceCommand = connection.CreateCommand();
                sourceCommand.Transaction = transaction;
                sourceCommand.CommandText =
                    """
                    INSERT INTO knowledge_candidate_sources (
                        id,
                        candidate_id,
                        source_type,
                        source_id,
                        excerpt,
                        created_at)
                    VALUES (
                        @id,
                        @candidateId,
                        @sourceType,
                        @sourceId,
                        @excerpt,
                        @createdAt);
                    """;
                sourceCommand.Parameters.AddWithValue("@id", source.Id.ToString());
                sourceCommand.Parameters.AddWithValue("@candidateId", source.CandidateId.ToString());
                sourceCommand.Parameters.AddWithValue("@sourceType", source.SourceType);
                sourceCommand.Parameters.AddWithValue("@sourceId", source.SourceId);
                sourceCommand.Parameters.AddWithValue("@excerpt", source.Excerpt);
                sourceCommand.Parameters.AddWithValue("@createdAt", source.CreatedAt.ToString("O"));
                await sourceCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<KnowledgeReviewHistoryResponse> GetKnowledgeReviewHistoryPageAsync(
        Guid candidateId,
        string? cursor = null,
        int limit = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var pageSize = NormalizePageSize(limit);
        var offset = ParseOffsetCursor(cursor);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var totalCount = 0;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(1) FROM knowledge_review_events WHERE candidate_id = @candidateId;";
            countCommand.Parameters.AddWithValue("@candidateId", candidateId.ToString());
            totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<KnowledgeReviewHistoryEventRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id,
                       candidate_id,
                       action_type,
                       reviewer,
                       review_session_id,
                       review_comment,
                       review_reason,
                       before_snapshot_json,
                       after_snapshot_json,
                       created_at
                FROM knowledge_review_events
                WHERE candidate_id = @candidateId
                ORDER BY created_at DESC, id DESC
                LIMIT @limit OFFSET @offset;
                """;
            command.Parameters.AddWithValue("@candidateId", candidateId.ToString());
            command.Parameters.AddWithValue("@limit", pageSize);
            command.Parameters.AddWithValue("@offset", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new KnowledgeReviewHistoryEventRecord
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    CandidateId = Guid.Parse(reader.GetString(1)),
                    ActionType = reader.GetString(2),
                    Reviewer = reader.GetString(3),
                    ReviewSessionId = reader.GetString(4),
                    ReviewComment = reader.GetString(5),
                    ReviewReason = reader.GetString(6),
                    BeforeSnapshotJson = reader.GetString(7),
                    AfterSnapshotJson = reader.GetString(8),
                    CreatedAt = ParseDateTimeOffset(reader.GetString(9))
                });
            }
        }

        var consumed = offset + items.Count;
        return new KnowledgeReviewHistoryResponse
        {
            CandidateId = candidateId,
            Items = items,
            HasMore = consumed < totalCount,
            NextCursor = consumed < totalCount ? consumed.ToString() : string.Empty
        };
    }

    private async Task<KnowledgeCandidateRecord?> GetCandidateAsync(
        SqliteConnection connection,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   status,
                   candidate_kind,
                   platform,
                   product_key,
                   question_normalized,
                   answer_draft,
                   answer_final,
                   tags_json,
                   confidence,
                   risk_level,
                   source_count,
                   cluster_key,
                   evidence_summary,
                   review_comment,
                   review_reason,
                   reviewed_by,
                   review_session_id,
                   reviewed_at,
                   lease_token,
                   lease_expires_at,
                   created_at,
                   updated_at
            FROM knowledge_candidates
            WHERE id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", candidateId.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new KnowledgeCandidateRecord
        {
            Id = Guid.Parse(reader.GetString(0)),
            Status = reader.GetString(1),
            CandidateKind = reader.GetString(2),
            Platform = reader.GetString(3),
            ProductKey = reader.GetString(4),
            QuestionNormalized = reader.GetString(5),
            AnswerDraft = reader.GetString(6),
            AnswerFinal = reader.GetString(7),
            Tags = DeserializeStringList(reader.GetString(8)),
            Confidence = reader.GetDouble(9),
            RiskLevel = reader.GetString(10),
            SourceCount = reader.GetInt32(11),
            ClusterKey = reader.GetString(12),
            EvidenceSummary = reader.GetString(13),
            ReviewComment = reader.GetString(14),
            ReviewReason = reader.GetString(15),
            ReviewedBy = reader.GetString(16),
            ReviewSessionId = reader.GetString(17),
            ReviewedAt = string.IsNullOrWhiteSpace(reader.GetString(18)) ? null : ParseDateTimeOffset(reader.GetString(18)),
            LeaseToken = reader.GetString(19),
            LeaseExpiresAt = string.IsNullOrWhiteSpace(reader.GetString(20)) ? null : ParseDateTimeOffset(reader.GetString(20)),
            CreatedAt = ParseDateTimeOffset(reader.GetString(21)),
            UpdatedAt = ParseDateTimeOffset(reader.GetString(22))
        };
    }

    private async Task<PublishedKnowledgeCardRecord?> GetPublishedKnowledgeCardAsync(
        SqliteConnection connection,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   candidate_kind,
                   platform,
                   product_key,
                   question,
                   answer,
                   synonyms_json,
                   tags_json,
                   usage_notes,
                   version,
                   revision,
                   updated_at
            FROM published_knowledge_cards
            WHERE id = @id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", candidateId.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PublishedKnowledgeCardRecord
        {
            Id = Guid.Parse(reader.GetString(0)),
            CandidateKind = reader.GetString(1),
            Platform = reader.GetString(2),
            ProductKey = reader.GetString(3),
            Question = reader.GetString(4),
            Answer = reader.GetString(5),
            Synonyms = DeserializeStringList(reader.GetString(6)),
            Tags = DeserializeStringList(reader.GetString(7)),
            UsageNotes = reader.GetString(8),
            Version = reader.GetString(9),
            Revision = reader.GetInt64(10),
            UpdatedAt = ParseDateTimeOffset(reader.GetString(11))
        };
    }

    private static async Task InsertKnowledgeReviewEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        string actionType,
        string reviewer,
        string reviewSessionId,
        string reviewComment,
        string reviewReason,
        string beforeSnapshotJson,
        string afterSnapshotJson,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO knowledge_review_events (
                id,
                candidate_id,
                action_type,
                reviewer,
                review_session_id,
                review_comment,
                review_reason,
                before_snapshot_json,
                after_snapshot_json,
                created_at)
            VALUES (
                @id,
                @candidateId,
                @actionType,
                @reviewer,
                @reviewSessionId,
                @reviewComment,
                @reviewReason,
                @beforeSnapshotJson,
                @afterSnapshotJson,
                @createdAt);
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@candidateId", candidateId.ToString());
        command.Parameters.AddWithValue("@actionType", actionType);
        command.Parameters.AddWithValue("@reviewer", reviewer);
        command.Parameters.AddWithValue("@reviewSessionId", reviewSessionId);
        command.Parameters.AddWithValue("@reviewComment", reviewComment);
        command.Parameters.AddWithValue("@reviewReason", reviewReason);
        command.Parameters.AddWithValue("@beforeSnapshotJson", beforeSnapshotJson);
        command.Parameters.AddWithValue("@afterSnapshotJson", afterSnapshotJson);
        command.Parameters.AddWithValue("@createdAt", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseKnowledgeCandidateClaimAsync(
        Guid candidateId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leaseToken))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE knowledge_candidates
            SET lease_token = '',
                lease_expires_at = ''
            WHERE id = @id AND lease_token = @leaseToken;
            """;
        command.Parameters.AddWithValue("@id", candidateId.ToString());
        command.Parameters.AddWithValue("@leaseToken", leaseToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<KnowledgeCandidateRecord>> GetKnowledgeCandidatesByLeaseTokenAsync(
        string leaseToken,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   status,
                   candidate_kind,
                   platform,
                   product_key,
                   question_normalized,
                   answer_draft,
                   answer_final,
                   tags_json,
                   confidence,
                   risk_level,
                   source_count,
                   cluster_key,
                   evidence_summary,
                   review_comment,
                   review_reason,
                   reviewed_by,
                   review_session_id,
                   reviewed_at,
                   lease_token,
                   lease_expires_at,
                   created_at,
                   updated_at
            FROM knowledge_candidates
            WHERE lease_token = @leaseToken
            ORDER BY updated_at ASC, id ASC;
            """;
        command.Parameters.AddWithValue("@leaseToken", leaseToken);

        var items = new List<KnowledgeCandidateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new KnowledgeCandidateRecord
            {
                Id = Guid.Parse(reader.GetString(0)),
                Status = reader.GetString(1),
                CandidateKind = reader.GetString(2),
                Platform = reader.GetString(3),
                ProductKey = reader.GetString(4),
                QuestionNormalized = reader.GetString(5),
                AnswerDraft = reader.GetString(6),
                AnswerFinal = reader.GetString(7),
                Tags = DeserializeStringList(reader.GetString(8)),
                Confidence = reader.GetDouble(9),
                RiskLevel = reader.GetString(10),
                SourceCount = reader.GetInt32(11),
                ClusterKey = reader.GetString(12),
                EvidenceSummary = reader.GetString(13),
                ReviewComment = reader.GetString(14),
                ReviewReason = reader.GetString(15),
                ReviewedBy = reader.GetString(16),
                ReviewSessionId = reader.GetString(17),
                ReviewedAt = string.IsNullOrWhiteSpace(reader.GetString(18)) ? null : ParseDateTimeOffset(reader.GetString(18)),
                LeaseToken = reader.GetString(19),
                LeaseExpiresAt = string.IsNullOrWhiteSpace(reader.GetString(20)) ? null : ParseDateTimeOffset(reader.GetString(20)),
                CreatedAt = ParseDateTimeOffset(reader.GetString(21)),
                UpdatedAt = ParseDateTimeOffset(reader.GetString(22))
            });
        }

        return items;
    }

    private static int NormalizePageSize(int limit)
    {
        return Math.Clamp(limit, 1, 500);
    }

    private static int ParseOffsetCursor(string? cursor)
    {
        return int.TryParse(cursor, out var offset) && offset > 0 ? offset : 0;
    }

    private static long ParseRevisionCursor(string? cursor)
    {
        return long.TryParse(cursor, out var revision) && revision > 0 ? revision : 0L;
    }

    private async Task<PublishedKnowledgeSyncResponse> GetPublishedKnowledgeCardsByTimestampAsync(
        SqliteConnection connection,
        DateTimeOffset? since,
        long latestRevision,
        CancellationToken cancellationToken)
    {
        var cards = new List<PublishedKnowledgeCardRecord>();
        await using (var cardCommand = connection.CreateCommand())
        {
            cardCommand.CommandText =
                """
                SELECT id,
                       candidate_kind,
                       platform,
                       product_key,
                       question,
                       answer,
                       synonyms_json,
                       tags_json,
                       usage_notes,
                       version,
                       revision,
                       updated_at
                FROM published_knowledge_cards
                WHERE @since = '' OR updated_at > @since
                ORDER BY updated_at ASC;
                """;
            cardCommand.Parameters.AddWithValue("@since", since?.ToString("O") ?? string.Empty);

            await using var reader = await cardCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cards.Add(new PublishedKnowledgeCardRecord
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    CandidateKind = reader.GetString(1),
                    Platform = reader.GetString(2),
                    ProductKey = reader.GetString(3),
                    Question = reader.GetString(4),
                    Answer = reader.GetString(5),
                    Synonyms = DeserializeStringList(reader.GetString(6)),
                    Tags = DeserializeStringList(reader.GetString(7)),
                    UsageNotes = reader.GetString(8),
                    Version = reader.GetString(9),
                    Revision = reader.GetInt64(10),
                    UpdatedAt = ParseDateTimeOffset(reader.GetString(11))
                });
            }
        }

        var removedIds = new List<Guid>();
        await using (var removalCommand = connection.CreateCommand())
        {
            removalCommand.CommandText =
                """
                SELECT id
                FROM published_knowledge_card_removals
                WHERE @since = '' OR removed_at > @since
                ORDER BY removed_at ASC;
                """;
            removalCommand.Parameters.AddWithValue("@since", since?.ToString("O") ?? string.Empty);

            await using var reader = await removalCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                removedIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        var generatedAt = cards.Count == 0
            ? DateTimeOffset.Now
            : cards.Max(card => card.UpdatedAt);
        var knowledgeVersion = cards.Count == 0
            ? (since is null ? "knowledge-empty" : "knowledge-no-change")
            : $"knowledge-{generatedAt:yyyy-MM-dd-HHmmss}";

        return new PublishedKnowledgeSyncResponse
        {
            KnowledgeVersion = knowledgeVersion,
            GeneratedAt = generatedAt,
            LatestRevision = latestRevision,
            Cards = cards,
            RemovedCardIds = removedIds
        };
    }

    private async Task<IReadOnlyList<PublishedKnowledgeCardRecord>> LoadPublishedKnowledgeCardsByIdsAsync(
        SqliteConnection connection,
        IReadOnlyCollection<Guid> cardIds,
        CancellationToken cancellationToken)
    {
        if (cardIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>();
        var index = 0;
        foreach (var cardId in cardIds)
        {
            var parameterName = $"@cardId{index++}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, cardId.ToString());
        }

        command.CommandText =
            $"""
            SELECT id,
                   candidate_kind,
                   platform,
                   product_key,
                   question,
                   answer,
                   synonyms_json,
                   tags_json,
                   usage_notes,
                   version,
                   revision,
                   updated_at
            FROM published_knowledge_cards
            WHERE id IN ({string.Join(", ", parameterNames)})
            ORDER BY revision ASC;
            """;

        var cards = new List<PublishedKnowledgeCardRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cards.Add(new PublishedKnowledgeCardRecord
            {
                Id = Guid.Parse(reader.GetString(0)),
                CandidateKind = reader.GetString(1),
                Platform = reader.GetString(2),
                ProductKey = reader.GetString(3),
                Question = reader.GetString(4),
                Answer = reader.GetString(5),
                Synonyms = DeserializeStringList(reader.GetString(6)),
                Tags = DeserializeStringList(reader.GetString(7)),
                UsageNotes = reader.GetString(8),
                Version = reader.GetString(9),
                Revision = reader.GetInt64(10),
                UpdatedAt = ParseDateTimeOffset(reader.GetString(11))
            });
        }

        return cards;
    }

    private static async Task<long> GetLatestPublishedKnowledgeRevisionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(revision), 0) FROM published_knowledge_changes;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> InsertPublishedKnowledgeChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid cardId,
        string changeType,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO published_knowledge_changes (
                card_id,
                change_type,
                changed_at)
            VALUES (
                @cardId,
                @changeType,
                @changedAt);

            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@cardId", cardId.ToString());
        command.Parameters.AddWithValue("@changeType", changeType);
        command.Parameters.AddWithValue("@changedAt", changedAt.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task BackfillPublishedKnowledgeChangesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var hasChangesCommand = connection.CreateCommand();
        hasChangesCommand.CommandText = "SELECT COUNT(1) FROM published_knowledge_changes;";
        var existingChanges = Convert.ToInt32(await hasChangesCommand.ExecuteScalarAsync(cancellationToken));
        if (existingChanges > 0)
        {
            return;
        }

        var events = new List<(Guid CardId, string ChangeType, DateTimeOffset ChangedAt)>();
        await using (var cardCommand = connection.CreateCommand())
        {
            cardCommand.CommandText = "SELECT id, updated_at FROM published_knowledge_cards ORDER BY updated_at ASC, id ASC;";
            await using var reader = await cardCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add((Guid.Parse(reader.GetString(0)), "upsert", ParseDateTimeOffset(reader.GetString(1))));
            }
        }

        await using (var removalCommand = connection.CreateCommand())
        {
            removalCommand.CommandText = "SELECT id, removed_at FROM published_knowledge_card_removals ORDER BY removed_at ASC, id ASC;";
            await using var reader = await removalCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add((Guid.Parse(reader.GetString(0)), "remove", ParseDateTimeOffset(reader.GetString(1))));
            }
        }

        if (events.Count == 0)
        {
            return;
        }

        await using var transaction = connection.BeginTransaction();
        foreach (var changeEvent in events.OrderBy(item => item.ChangedAt).ThenBy(item => item.CardId))
        {
            var revision = await InsertPublishedKnowledgeChangeAsync(connection, transaction, changeEvent.CardId, changeEvent.ChangeType, changeEvent.ChangedAt, cancellationToken);
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            if (string.Equals(changeEvent.ChangeType, "upsert", StringComparison.OrdinalIgnoreCase))
            {
                updateCommand.CommandText = "UPDATE published_knowledge_cards SET revision = @revision WHERE id = @id;";
            }
            else
            {
                updateCommand.CommandText = "UPDATE published_knowledge_card_removals SET revision = @revision WHERE id = @id;";
            }

            updateCommand.Parameters.AddWithValue("@revision", revision);
            updateCommand.Parameters.AddWithValue("@id", changeEvent.CardId.ToString());
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureCandidateKindColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureColumnAsync(connection, "knowledge_candidates", "candidate_kind", "TEXT NOT NULL DEFAULT 'qa'", cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // already migrated
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string SerializeStringList(IEnumerable<string> values)
    {
        return JsonSerializer.Serialize(
            values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(),
            JsonOptions);
    }

    private static string SerializeObject<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static List<string> DeserializeStringList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static DateTimeOffset ParseDateTimeOffset(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }
}
