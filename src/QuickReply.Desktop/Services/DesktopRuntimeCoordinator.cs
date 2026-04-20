using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed class DesktopRuntimeCoordinator
{
    private readonly Func<AppSettings> _createSettingsSnapshot;
    private readonly Func<AppSettings, bool, CancellationToken, Task<KnowledgeSyncRunResult>> _runCloudSyncAsync;
    private readonly Func<AppSettings, bool, CancellationToken, Task<UpdateCheckResult>> _checkForUpdatesAsync;
    private readonly Func<AppSettings, UpdateCheckResult, Task> _handleUpdateCheckResultAsync;
    private readonly Func<Task> _refreshIssueStatusesAndNotifyAsync;
    private readonly Func<string, string, bool, Task> _showBackgroundNotificationAsync;

    public DesktopRuntimeCoordinator(
        Func<AppSettings> createSettingsSnapshot,
        Func<AppSettings, bool, CancellationToken, Task<KnowledgeSyncRunResult>> runCloudSyncAsync,
        Func<AppSettings, bool, CancellationToken, Task<UpdateCheckResult>> checkForUpdatesAsync,
        Func<AppSettings, UpdateCheckResult, Task> handleUpdateCheckResultAsync,
        Func<Task> refreshIssueStatusesAndNotifyAsync,
        Func<string, string, bool, Task> showBackgroundNotificationAsync)
    {
        _createSettingsSnapshot = createSettingsSnapshot;
        _runCloudSyncAsync = runCloudSyncAsync;
        _checkForUpdatesAsync = checkForUpdatesAsync;
        _handleUpdateCheckResultAsync = handleUpdateCheckResultAsync;
        _refreshIssueStatusesAndNotifyAsync = refreshIssueStatusesAndNotifyAsync;
        _showBackgroundNotificationAsync = showBackgroundNotificationAsync;
    }

    public Task<KnowledgeSyncRunResult> RunCloudSyncNowAsync(
        AppSettings settingsSnapshot,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        return _runCloudSyncAsync(settingsSnapshot, force, cancellationToken);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesNowAsync(
        AppSettings settingsSnapshot,
        bool force = false,
        bool showUpdatePrompt = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _checkForUpdatesAsync(settingsSnapshot, force, cancellationToken).ConfigureAwait(false);
        if (showUpdatePrompt && result.Success && result.Manifest is not null)
        {
            await _handleUpdateCheckResultAsync(settingsSnapshot, result).ConfigureAwait(false);
        }

        return result;
    }

    public async Task RunBackgroundStartupTasksAsync()
    {
        try
        {
            var settingsSnapshot = _createSettingsSnapshot();
            var syncResult = await _runCloudSyncAsync(settingsSnapshot, false, CancellationToken.None).ConfigureAwait(false);
            if (!syncResult.Success && !syncResult.Skipped)
            {
                await _showBackgroundNotificationAsync("云同步", syncResult.Message, true).ConfigureAwait(false);
            }

            var updateResult = await _checkForUpdatesAsync(settingsSnapshot, false, CancellationToken.None).ConfigureAwait(false);
            if (!updateResult.Success && !updateResult.Skipped)
            {
                await _showBackgroundNotificationAsync("更新检查", updateResult.Message, true).ConfigureAwait(false);
                return;
            }

            if (updateResult.Success && updateResult.Manifest is not null)
            {
                await _handleUpdateCheckResultAsync(settingsSnapshot, updateResult).ConfigureAwait(false);
            }

            await _refreshIssueStatusesAndNotifyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _showBackgroundNotificationAsync("后台任务", $"同步或更新检查失败：{ex.Message}", true).ConfigureAwait(false);
        }
    }
}
