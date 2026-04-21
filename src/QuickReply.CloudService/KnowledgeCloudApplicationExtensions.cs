namespace QuickReply.CloudService;

internal static class KnowledgeCloudApplicationExtensions
{
    public static async Task InitializeKnowledgeCloudAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        using var scope = app.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<KnowledgeCloudStore>();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public static WebApplication MapKnowledgeCloudEndpoints(this WebApplication app)
    {
        app.MapOperationalEndpoints();
        app.MapKnowledgeEndpoints();
        return app;
    }
}
