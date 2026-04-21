using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

public sealed class CloudServiceRuntimeInfoService
{
    private readonly KnowledgeCloudStore _store;
    private readonly IOptions<KnowledgeCloudOptions> _options;

    public CloudServiceRuntimeInfoService(
        KnowledgeCloudStore store,
        IOptions<KnowledgeCloudOptions> options)
    {
        _store = store;
        _options = options;
    }

    public async Task<CloudServiceRuntimeInfoResponse> GetRuntimeInfoAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var warnings = new List<string>();

        var databaseReady = true;
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            databaseReady = false;
            warnings.Add($"数据库初始化失败：{ex.Message}");
        }

        return new CloudServiceRuntimeInfoResponse
        {
            Ok = databaseReady,
            Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            DatabaseReady = databaseReady,
            DatabasePath = options.DatabasePath,
            ManagementApiTokenConfigured = !string.IsNullOrWhiteSpace(options.ManagementApiToken),
            LoopbackBypassEnabled = options.AllowLoopbackWithoutAuth,
            Warnings = warnings.ToArray()
        };
    }
}
