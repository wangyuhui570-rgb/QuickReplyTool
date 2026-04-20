using System.Diagnostics;
namespace QuickReply.Desktop.Services;

internal sealed class AppUpdateWorkflowUiAdapter : IAppUpdateWorkflowUi
{
    private readonly App _app;

    public AppUpdateWorkflowUiAdapter(App app)
    {
        _app = app;
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = await _app.Dispatcher.InvokeAsync(() =>
            System.Windows.MessageBox.Show(
                message,
                title,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Information));

        return result == System.Windows.MessageBoxResult.Yes;
    }

    public Task ShowInfoAsync(string title, string message)
    {
        return _app.Dispatcher.InvokeAsync(() =>
                System.Windows.MessageBox.Show(
                    message,
                    title,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information))
            .Task;
    }

    public Task ShowWarningAsync(string title, string message)
    {
        return _app.Dispatcher.InvokeAsync(() =>
                System.Windows.MessageBox.Show(
                    message,
                    title,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning))
            .Task;
    }

    public Task ShowBackgroundNotificationAsync(string title, string message, bool isWarning = false)
    {
        return _app.Dispatcher.InvokeAsync(() => _app.ShowBackgroundNotification(title, message, isWarning)).Task;
    }

    public Task OpenExternalUrlAsync(string url)
    {
        return _app.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }).Task;
    }

    public void PrepareForRestart()
    {
        _app.Dispatcher.Invoke(_app.PrepareForRestart);
    }

    public void Shutdown()
    {
        _app.Dispatcher.Invoke(_app.Shutdown);
    }
}
