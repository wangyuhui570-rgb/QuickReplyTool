using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IJdConversationMonitor : IAsyncDisposable
{
    event EventHandler<JdRecommendationContext>? RecommendationContextDetected;

    event EventHandler<JdMonitorStatus>? StatusChanged;

    JdMonitorStatus CurrentStatus { get; }

    Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task StopAsync();
}
