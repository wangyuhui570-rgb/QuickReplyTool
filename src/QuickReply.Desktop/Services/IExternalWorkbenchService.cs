using System.Windows;

namespace QuickReply.Desktop.Services;

public interface IExternalWorkbenchService
{
    void Start(Window window);

    void Stop();

    bool TryActivateLastExternalWindow();

    bool TryGetLastExternalWindowInfo(out WindowDockTargetInfo? windowInfo);

    bool TryGetWindowInfo(IntPtr windowHandle, out WindowDockTargetInfo? windowInfo);

    IntPtr GetLastExternalWindowHandle();

    void RememberExternalWindow(IntPtr windowHandle);
}
