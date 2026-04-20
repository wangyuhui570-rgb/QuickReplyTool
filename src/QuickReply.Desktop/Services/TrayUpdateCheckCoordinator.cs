using System.Threading;
using Forms = System.Windows.Forms;

namespace QuickReply.Desktop.Services;

public sealed class TrayUpdateCheckCoordinator
{
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _runCheckAsync;
    private readonly Action<string, string, Forms.ToolTipIcon, int> _showNotification;
    private int _isRunning;

    public TrayUpdateCheckCoordinator(
        Func<CancellationToken, Task<UpdateCheckResult>> runCheckAsync,
        Action<string, string, Forms.ToolTipIcon, int> showNotification)
    {
        _runCheckAsync = runCheckAsync ?? throw new ArgumentNullException(nameof(runCheckAsync));
        _showNotification = showNotification ?? throw new ArgumentNullException(nameof(showNotification));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        ShowInfo("正在检查更新...");

        try
        {
            var result = await _runCheckAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _showNotification(
                    "更新检查",
                    result.Message,
                    result.Skipped ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning,
                    2600);
                return;
            }

            if (!result.HasAppUpdate && !result.HasKnowledgeUpdate)
            {
                ShowInfo("当前没有检测到新版本或新的知识分析结果。");
            }
        }
        catch (Exception ex)
        {
            _showNotification("更新检查", $"检查更新失败：{ex.Message}", Forms.ToolTipIcon.Warning, 2600);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private void ShowInfo(string message)
    {
        _showNotification("更新检查", message, Forms.ToolTipIcon.Info, 2200);
    }
}
