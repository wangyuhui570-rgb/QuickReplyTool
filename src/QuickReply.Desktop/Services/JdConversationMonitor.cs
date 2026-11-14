using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class JdConversationMonitor : IJdConversationMonitor
{
    private readonly IJdWorkbenchProfileService _profileService;
    private readonly Func<JdWorkbenchProfile, IJdWorkbenchChatClient> _clientFactory;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;

    public JdConversationMonitor(
        IJdWorkbenchProfileService profileService,
        Func<JdWorkbenchProfile, IJdWorkbenchChatClient> clientFactory)
    {
        _profileService = profileService;
        _clientFactory = clientFactory;
    }

    public event EventHandler<JdRecommendationContext>? RecommendationContextDetected;

    public event EventHandler<JdMonitorStatus>? StatusChanged;

    public JdMonitorStatus CurrentStatus { get; private set; } = new();

    public Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (_runTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _sessions.Clear();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = Task.Run(() => RunAsync(AppSettingsSnapshotService.Clone(settings), _runCancellation.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (_gate)
        {
            _runCancellation?.Cancel();
            task = _runTask;
        }

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_gate)
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            _runTask = null;
            _sessions.Clear();
        }

        PublishStatus("stopped", "京东监听已停止。", string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        JdWorkbenchProfile profile;
        try
        {
            profile = _profileService.ResolveProfile(settings.JdWorkbenchProfilePath);
        }
        catch (Exception ex)
        {
            PublishStatus("error", SanitizeError($"京东监听启动失败：{ex.Message}"), string.Empty);
            return;
        }

        PublishStatus("starting", $"正在连接京东工作台：{profile.DisplayName}", profile.ProfilePath);
        using var client = _clientFactory(profile);
        PublishStatus("running", $"京东监听中：{profile.DisplayName}", profile.ProfilePath);

        var pollDelay = TimeSpan.FromSeconds(Math.Clamp(settings.JdWorkbenchPollIntervalSeconds, 2, 60));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(client, cancellationToken).ConfigureAwait(false);
                PublishStatus("running", $"京东监听中：{profile.DisplayName}", profile.ProfilePath);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishStatus("error", SanitizeError($"京东监听失败：{ex.Message}"), profile.ProfilePath);
            }

            await Task.Delay(pollDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollOnceAsync(IJdWorkbenchChatClient client, CancellationToken cancellationToken)
    {
        var end = DateTime.Now.AddMinutes(5);
        var start = end.AddDays(-2);
        var sessions = await client.QuerySessionsAsync(start, end, cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Cid))
            {
                continue;
            }

            if (_sessions.TryGetValue(session.Cid, out var state) &&
                string.Equals(state.Marker, session.RawMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var messages = await client.QueryChatMessagesAsync(session.Cid, cancellationToken).ConfigureAwait(false);
            ProcessSession(session, messages);
        }
    }

    private void ProcessSession(JdChatSession session, IReadOnlyList<JdChatMessage> messages)
    {
        var knownKeys = _sessions.TryGetValue(session.Cid, out var state)
            ? state.MessageKeys
            : new HashSet<string>(StringComparer.Ordinal);
        var newMessages = messages
            .Where(message => !string.IsNullOrWhiteSpace(message.MessageKey) && !knownKeys.Contains(message.MessageKey))
            .ToArray();

        _sessions[session.Cid] = new SessionState(
            session.RawMarker,
            messages.Select(message => message.MessageKey).Where(key => !string.IsNullOrWhiteSpace(key)).ToHashSet(StringComparer.Ordinal));

        if (newMessages.Length == 0)
        {
            return;
        }

        var latest = newMessages[^1];
        if (string.Equals(latest.Speaker, "waiter", StringComparison.OrdinalIgnoreCase))
        {
            PublishStatus("running", $"客服已回复，会话 {session.Customer} 的推荐已取消。", CurrentStatus.ProfilePath);
            return;
        }

        if (!IsCustomerText(latest))
        {
            return;
        }

        RecommendationContextDetected?.Invoke(
            this,
            new JdRecommendationContext
            {
                Session = session,
                RecentMessages = messages.TakeLast(10).ToArray(),
                TriggerMessage = latest
            });
    }

    internal static bool IsCustomerText(JdChatMessage message)
    {
        if (!string.Equals(message.Speaker, "customer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var messageType = (message.MessageType ?? string.Empty).Trim().ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(message.Content) &&
               (messageType is "" or "text" or "txt" || messageType.Contains("text", StringComparison.OrdinalIgnoreCase));
    }

    private void PublishStatus(string state, string message, string profilePath)
    {
        CurrentStatus = new JdMonitorStatus
        {
            State = state,
            Message = message,
            ProfilePath = profilePath,
            UpdatedAt = DateTimeOffset.Now
        };
        StatusChanged?.Invoke(this, CurrentStatus);
    }

    private static string SanitizeError(string message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? "京东监听失败。"
            : message.Replace("Cookie", "登录态", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SessionState(string Marker, HashSet<string> MessageKeys);
}
