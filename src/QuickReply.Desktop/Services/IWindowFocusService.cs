namespace QuickReply.Desktop.Services;

internal interface IWindowFocusService
{
    bool IsWindow(IntPtr windowHandle);

    IntPtr GetForegroundWindow();

    IntPtr NormalizeTopLevelWindow(IntPtr windowHandle);
}
