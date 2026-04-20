using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal interface IAiDraftProvider
{
    string ProviderType { get; }

    Task<AiDraftProviderResponse> GenerateAsync(
        AppSettings settings,
        AiDraftProviderRequest request,
        CancellationToken cancellationToken);
}
