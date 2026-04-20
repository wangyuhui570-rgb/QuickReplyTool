using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickReply.Desktop.Native;
using System.Text;
using System.Diagnostics;

namespace QuickReply.Desktop.Services;

public sealed class ForegroundWindowTracker : IExternalWorkbenchService
{
    private readonly DispatcherTimer _timer;
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private IntPtr _lastExternalWindow;

    public ForegroundWindowTracker()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _timer.Tick += OnTick;
    }

    public void Start(Window window)
    {
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public bool TryActivateLastExternalWindow()
    {
        if (_lastExternalWindow == IntPtr.Zero || !NativeMethods.IsWindow(_lastExternalWindow))
        {
            return false;
        }

        if (NativeMethods.IsIconic(_lastExternalWindow))
        {
            NativeMethods.ShowWindow(_lastExternalWindow, NativeMethods.SwRestore);
        }

        NativeMethods.BringWindowToTop(_lastExternalWindow);
        return NativeMethods.SetForegroundWindow(_lastExternalWindow);
    }

    public bool TryGetLastExternalWindowInfo(out WindowDockTargetInfo? windowInfo)
    {
        windowInfo = TryGetWindowInfoCore(_lastExternalWindow);
        return windowInfo is not null;
    }

    public bool TryGetWindowInfo(IntPtr windowHandle, out WindowDockTargetInfo? windowInfo)
    {
        windowInfo = TryGetWindowInfoCore(windowHandle);
        return windowInfo is not null;
    }

    public IntPtr GetLastExternalWindowHandle()
    {
        return _lastExternalWindow;
    }

    public void RememberExternalWindow(IntPtr windowHandle)
    {
        var normalizedWindow = NormalizeTopLevelWindow(windowHandle);
        if (normalizedWindow == IntPtr.Zero || IsOwnProcessWindow(normalizedWindow))
        {
            return;
        }

        _lastExternalWindow = normalizedWindow;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var currentWindow = NativeMethods.GetForegroundWindow();
        currentWindow = NormalizeTopLevelWindow(currentWindow);
        if (currentWindow == IntPtr.Zero || IsOwnProcessWindow(currentWindow))
        {
            return;
        }

        RememberExternalWindow(currentWindow);
    }

    private bool IsOwnProcessWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return processId == _currentProcessId;
    }

    private static WindowDockTargetInfo? TryGetWindowInfoCore(IntPtr windowHandle)
    {
        windowHandle = NormalizeTopLevelWindow(windowHandle);
        if (windowHandle == IntPtr.Zero ||
            !NativeMethods.IsWindow(windowHandle) ||
            !NativeMethods.IsWindowVisible(windowHandle) ||
            NativeMethods.IsIconic(windowHandle) ||
            !NativeMethods.GetWindowRect(windowHandle, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var titleLength = NativeMethods.GetWindowTextLength(windowHandle);
        var title = string.Empty;
        if (titleLength > 0)
        {
            var titleBuilder = new StringBuilder(titleLength + 1);
            NativeMethods.GetWindowText(windowHandle, titleBuilder, titleBuilder.Capacity);
            title = titleBuilder.ToString().Trim();
        }

        var className = string.Empty;
        var classNameBuilder = new StringBuilder(256);
        if (NativeMethods.GetClassName(windowHandle, classNameBuilder, classNameBuilder.Capacity) > 0)
        {
            className = classNameBuilder.ToString().Trim();
        }

        var processName = string.Empty;
        var processPath = string.Empty;
        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
                try
                {
                    processPath = process.MainModule?.FileName ?? string.Empty;
                }
                catch
                {
                    processPath = string.Empty;
                }
            }
            catch
            {
                processName = string.Empty;
                processPath = string.Empty;
            }
        }

        return new WindowDockTargetInfo
        {
            Handle = windowHandle,
            Title = title,
            WindowClassName = className,
            ProcessName = processName,
            ProcessPath = processPath,
            Left = rect.Left,
            Top = rect.Top,
            Width = width,
            Height = height
        };
    }

    private static IntPtr NormalizeTopLevelWindow(IntPtr windowHandle)
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
