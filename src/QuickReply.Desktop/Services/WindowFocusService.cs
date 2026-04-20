using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

internal sealed class WindowFocusService : IWindowFocusService
{
    public bool IsWindow(IntPtr windowHandle)
    {
        return NativeMethods.IsWindow(windowHandle);
    }

    public IntPtr GetForegroundWindow()
    {
        return NativeMethods.GetForegroundWindow();
    }

    public IntPtr NormalizeTopLevelWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var rootOwner = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRootowner);
        if (rootOwner != IntPtr.Zero)
        {
            return rootOwner;
        }

        var root = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
        return root != IntPtr.Zero ? root : windowHandle;
    }
}
