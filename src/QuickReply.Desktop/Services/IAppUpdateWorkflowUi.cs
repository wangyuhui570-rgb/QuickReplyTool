namespace QuickReply.Desktop.Services;

internal interface IAppUpdateWorkflowUi
{
    Task<bool> ConfirmAsync(string title, string message);

    Task ShowInfoAsync(string title, string message);

    Task ShowWarningAsync(string title, string message);

    Task ShowBackgroundNotificationAsync(string title, string message, bool isWarning = false);

    Task OpenExternalUrlAsync(string url);

    void PrepareForRestart();

    void Shutdown();
}
