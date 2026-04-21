namespace QuickReply.CloudService;

internal static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var publicKnowledgeGroup = endpoints.MapGroup("/api/quickreply/knowledge");
        publicKnowledgeGroup.MapGet("/published", async (
            DateTimeOffset? since,
            long? sinceRevision,
            string? cursor,
            int? limit,
            KnowledgeCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var response = await store.GetPublishedKnowledgeCardsAsync(
                sinceRevision,
                cursor,
                limit ?? 200,
                since,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        });

        var managementKnowledgeGroup = endpoints.MapGroup("/api/quickreply/knowledge")
            .RequireManagementAccess();

        managementKnowledgeGroup.MapGet("/review/queue", async (
            string? cursor,
            int? limit,
            KnowledgeCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var queue = await store.GetKnowledgeReviewQueuePageAsync(cursor, limit ?? 200, cancellationToken).ConfigureAwait(false);
            return Results.Ok(queue);
        });

        managementKnowledgeGroup.MapGet("/review/{id:guid}/sources", async (
            Guid id,
            string? cursor,
            int? limit,
            KnowledgeCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var sources = await store.GetKnowledgeCandidateSourcesPageAsync(id, cursor, limit ?? 200, cancellationToken).ConfigureAwait(false);
            return Results.Ok(sources);
        });

        managementKnowledgeGroup.MapGet("/review/{id:guid}/history", async (
            Guid id,
            string? cursor,
            int? limit,
            KnowledgeCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var history = await store.GetKnowledgeReviewHistoryPageAsync(id, cursor, limit ?? 100, cancellationToken).ConfigureAwait(false);
            return Results.Ok(history);
        });

        managementKnowledgeGroup.MapGet("/jobs/runs", async (
            int? limit,
            KnowledgeCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var runs = await store.GetKnowledgeJobRunsAsync(limit ?? 20, cancellationToken).ConfigureAwait(false);
            return Results.Ok(runs);
        });

        managementKnowledgeGroup.MapPost("/jobs/import-logs", async (
            ImportKnowledgeCandidatesRequest request,
            KnowledgeCandidateImportService importService,
            CancellationToken cancellationToken) =>
        {
            var result = await importService.ImportFromPathAsync(request.InputPath, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

        managementKnowledgeGroup.MapPost("/review/{id:guid}/approve", async (
            Guid id,
            ApproveKnowledgeCandidateRequest request,
            KnowledgeCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var result = await store.ApproveKnowledgeCandidateAsync(id, request, cancellationToken).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        managementKnowledgeGroup.MapPost("/review/{id:guid}/reject", async (
            Guid id,
            RejectKnowledgeCandidateRequest request,
            KnowledgeCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var result = await store.RejectKnowledgeCandidateAsync(id, request, cancellationToken).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        managementKnowledgeGroup.MapPost("/published/{id:guid}/remove", async (
            Guid id,
            RemovePublishedKnowledgeCardRequest request,
            KnowledgeCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var removed = await store.RemovePublishedKnowledgeCardAsync(id, request, cancellationToken).ConfigureAwait(false);
            return removed ? Results.Ok(new { id, removed = true }) : Results.NotFound();
        });

        return endpoints;
    }
}
