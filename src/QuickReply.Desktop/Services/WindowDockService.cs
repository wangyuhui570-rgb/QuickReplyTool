using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using QuickReply.Desktop.Native;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class WindowDockService
{
    private static readonly string[] BuiltInDockTargetKeywords =
    [
        "企业微信",
        "微信",
        "WeChat",
        "千牛",
        "旺旺",
        "京麦",
        "咚咚",
        "飞鸽",
        "抖店",
        "小红书",
        "客服",
        "接待",
        "工作台"
    ];

    private static readonly string[] BuiltInDockTargetProcessKeywords =
    [
        "qianniu",
        "qnwork",
        "jd",
        "jingdong",
        "pinduoduo",
        "pdd",
        "wechat",
        "weixin",
        "wecom",
        "wxwork"
    ];

    private static readonly string[] DirectInputFocusClassKeywords =
    [
        "Edit",
        "RichEdit",
        "RICHEDIT",
        "Scintilla",
        "WindowsForms10.EDIT",
        "TXGuiFoundation"
    ];

    private static readonly string[] BrowserHostedInputFocusClassKeywords =
    [
        "Chrome_RenderWidgetHostHWND",
        "Internet Explorer_Server",
        "WebView",
        "WebView2",
        "CEF"
    ];

    private const int DockGap = 0;
    private const double DockMoveThreshold = 6;
    private const double CompactLauncherWidth = 58;
    private const double CompactLauncherHeight = 118;
    private const double MinDockWidth = 180;
    private const double MaxDockWidth = 620;
    private const double MinDockHeight = 360;
    private const int FullscreenTolerance = 8;

    private readonly DispatcherTimer _timer;
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private Window? _window;
    private NativeMethods.WinEventDelegate? _foregroundChangedCallback;
    private IntPtr _foregroundHook;
    private IntPtr _mainWindowHandle;
    private IntPtr _lastDockedTarget;
    private double _lastLeft = double.NaN;
    private double _lastTop = double.NaN;
    private int _lastTargetLeft;
    private int _lastTargetTop;
    private int _lastTargetWidth;
    private int _lastTargetHeight;
    private bool _isPreviewMode;
    private SidebarWorkMode _sidebarWorkMode = SidebarWorkMode.Standard;
    private bool _isCompactSidebarExpanded;
    private int _sidebarWidth = 300;
    private int _sidebarHeightPercent = 100;

    public event Action<bool>? CompactSidebarExpandedChanged;

    public WindowDockService()
    {
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _timer.Tick += OnTick;
    }

    public void Start(Window window)
    {
        _window = window;
        _mainWindowHandle = new WindowInteropHelper(window).Handle;
        _foregroundChangedCallback = OnForegroundChanged;
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            _foregroundChangedCallback,
            0,
            0,
            NativeMethods.WineventOutofcontext | NativeMethods.WineventSkipownprocess);

        RefreshNow();
        _timer.Start();
    }

    public void Stop()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
        }

        _timer.Stop();
        _window = null;
        _foregroundChangedCallback = null;
        _foregroundHook = IntPtr.Zero;
        _mainWindowHandle = IntPtr.Zero;
        _lastDockedTarget = IntPtr.Zero;
        _lastLeft = double.NaN;
        _lastTop = double.NaN;
        _isPreviewMode = false;
    }

    public void ApplySettings(QuickReply.Desktop.Models.AppSettings settings)
    {
        _sidebarWorkMode = settings.SidebarWorkMode;
        _sidebarWidth = settings.SidebarWidth;
        _sidebarHeightPercent = settings.SidebarHeightPercent;
    }

    public void SetCompactSidebarExpanded(bool isExpanded)
    {
        if (_isCompactSidebarExpanded == isExpanded)
        {
            RefreshNow();
            return;
        }

        _isCompactSidebarExpanded = isExpanded;
        CompactSidebarExpandedChanged?.Invoke(_isCompactSidebarExpanded);

        if (_lastDockedTarget != IntPtr.Zero &&
            TryGetTargetInfo(_lastDockedTarget) is { } lastTargetInfo &&
            MatchesBuiltInTargets(lastTargetInfo))
        {
            MoveNextToTarget(lastTargetInfo, forceMove: true);
            return;
        }

        RefreshNow();
    }

    public void RefreshNow()
    {
        if (_window is null || _isPreviewMode)
        {
            return;
        }

        if (_sidebarWorkMode == SidebarWorkMode.Compact)
        {
            ClearDockState();
            HideDockBar();
            return;
        }

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == _mainWindowHandle || IsOwnProcessWindow(foregroundWindow))
        {
            if (TryMaintainDockForOwnProcessWindow(forceMove: true))
            {
                return;
            }

            var shouldForceHide = _lastDockedTarget != IntPtr.Zero || ShouldForceHideDockBar();
            if (shouldForceHide)
            {
                ClearDockState();
                HideDockBar(force: true);
                return;
            }

            HideDockBar();
            return;
        }

        if (!TryGetDockableTargetInfo(foregroundWindow, out var targetInfo) || targetInfo is null)
        {
            var shouldForceHide = _lastDockedTarget != IntPtr.Zero || ShouldForceHideDockBar();
            ClearDockState();
            HideDockBar(force: shouldForceHide);
            return;
        }

        MoveNextToTarget(targetInfo, forceMove: true);
    }

    public void EnterPreview(double left, double top, double width, double height)
    {
        if (_window is null)
        {
            return;
        }

        _isPreviewMode = true;
        ShowWindowIfNeeded();
        _window.Width = width;
        _window.Height = height;
        _window.Left = left;
        _window.Top = top;
    }

    public void ExitPreview()
    {
        if (_window is null)
        {
            return;
        }

        _isPreviewMode = false;
        ClearDockState();
        HideDockBar(force: true);
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_window is null)
        {
            return;
        }

        if (_isPreviewMode)
        {
            return;
        }

        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_sidebarWorkMode == SidebarWorkMode.Compact)
            {
                ClearDockState();
                HideDockBar();
                return;
            }

            if (!TryGetDockableTargetInfo(hwnd, out var targetInfo) || targetInfo is null)
            {
                if (TryMaintainDockForOwnProcessWindow(forceMove: true))
                {
                    return;
                }

                var shouldForceHide = _lastDockedTarget != IntPtr.Zero || ShouldForceHideDockBar();
                ClearDockState();
                HideDockBar(force: shouldForceHide);
                return;
            }

            if (_sidebarWorkMode == SidebarWorkMode.Compact && _isCompactSidebarExpanded && (_window is null || !_window.IsActive))
            {
                _isCompactSidebarExpanded = false;
                CompactSidebarExpandedChanged?.Invoke(false);
            }

            MoveNextToTarget(targetInfo, forceMove: true);
        });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        if (_isPreviewMode)
        {
            return;
        }

        if (_sidebarWorkMode == SidebarWorkMode.Compact)
        {
            ClearDockState();
            HideDockBar();
            return;
        }

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == _mainWindowHandle || IsOwnProcessWindow(foregroundWindow))
        {
            if (TryMaintainDockForOwnProcessWindow(forceMove: false))
            {
                return;
            }

            var shouldForceHide = _lastDockedTarget != IntPtr.Zero || ShouldForceHideDockBar();
            if (shouldForceHide)
            {
                ClearDockState();
                HideDockBar(force: true);
                return;
            }

            HideDockBar();
            return;
        }

        if (!TryGetDockableTargetInfo(foregroundWindow, out var targetInfo) || targetInfo is null)
        {
            var shouldForceHide = _lastDockedTarget != IntPtr.Zero || ShouldForceHideDockBar();
            ClearDockState();
            HideDockBar(force: shouldForceHide);
            return;
        }

        if (_sidebarWorkMode == SidebarWorkMode.Compact && _isCompactSidebarExpanded && !_window.IsActive)
        {
            _isCompactSidebarExpanded = false;
            CompactSidebarExpandedChanged?.Invoke(false);
        }

        MoveNextToTarget(targetInfo, forceMove: false);
    }

    private void TryTrackWindow(IntPtr handle, bool forceMove)
    {
        if (_window is null)
        {
            return;
        }

        if (_sidebarWorkMode == SidebarWorkMode.Compact)
        {
            ClearDockState();
            HideDockBar();
            return;
        }

        if (!TryGetDockableTargetInfo(handle, out var targetInfo) || targetInfo is null)
        {
            return;
        }

        MoveNextToTarget(targetInfo, forceMove);
    }

    private WindowDockTargetInfo? TryGetTargetInfo(IntPtr handle)
    {
        if (!NativeMethods.IsWindow(handle) ||
            !NativeMethods.IsWindowVisible(handle) ||
            NativeMethods.IsIconic(handle) ||
            !NativeMethods.GetWindowRect(handle, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        if (IsFullscreenLike(handle, rect))
        {
            return null;
        }

        var titleLength = NativeMethods.GetWindowTextLength(handle);
        if (titleLength <= 0)
        {
            titleLength = 0;
        }

        var titleBuilder = new StringBuilder(titleLength + 1);
        if (titleLength > 0)
        {
            NativeMethods.GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
        }

        var title = titleBuilder.ToString().Trim();
        var className = GetWindowClassName(handle);
        var (processName, processPath) = TryGetProcessInfo(handle);

        return new WindowDockTargetInfo
        {
            Handle = handle,
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

    private bool IsOwnProcessWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return processId == _currentProcessId;
    }

    private bool TryGetDockableTargetInfo(IntPtr handle, out WindowDockTargetInfo? targetInfo)
    {
        targetInfo = null;

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var rootHandle = NativeMethods.GetAncestor(handle, NativeMethods.GaRootowner);
        if (rootHandle == IntPtr.Zero)
        {
            rootHandle = NativeMethods.GetAncestor(handle, NativeMethods.GaRoot);
        }
        if (rootHandle == IntPtr.Zero)
        {
            rootHandle = handle;
        }

        if (rootHandle == IntPtr.Zero || rootHandle == _mainWindowHandle || IsOwnProcessWindow(rootHandle))
        {
            return false;
        }

        targetInfo = TryGetTargetInfo(rootHandle);
        return targetInfo is not null &&
               MatchesBuiltInTargets(targetInfo) &&
               HasSendableInputFocus(rootHandle);
    }

    private static bool MatchesBuiltInTargets(WindowDockTargetInfo targetInfo)
    {
        if (BuiltInDockTargetKeywords.Any(keyword =>
                ContainsIgnoreCase(targetInfo.Title, keyword) ||
                ContainsIgnoreCase(targetInfo.WindowClassName, keyword)))
        {
            return true;
        }

        return BuiltInDockTargetProcessKeywords.Any(keyword =>
            ContainsIgnoreCase(targetInfo.ProcessName, keyword) ||
            ContainsIgnoreCase(targetInfo.ProcessPath, keyword) ||
            ContainsIgnoreCase(targetInfo.Title, keyword));
    }

    private static bool IsFullscreenLike(IntPtr handle, NativeMethods.RECT rect)
    {
        var screen = Screen.FromHandle(handle);
        return FillsArea(rect, screen.WorkingArea) || FillsArea(rect, screen.Bounds);
    }

    private static bool FillsArea(NativeMethods.RECT rect, System.Drawing.Rectangle area)
    {
        return Math.Abs(rect.Left - area.Left) <= FullscreenTolerance &&
               Math.Abs(rect.Top - area.Top) <= FullscreenTolerance &&
               Math.Abs(rect.Right - area.Right) <= FullscreenTolerance &&
               Math.Abs(rect.Bottom - area.Bottom) <= FullscreenTolerance;
    }

    private static string GetWindowClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        return NativeMethods.GetClassName(handle, builder, builder.Capacity) > 0
            ? builder.ToString().Trim()
            : string.Empty;
    }

    private static (string ProcessName, string ProcessPath) TryGetProcessInfo(IntPtr handle)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
            {
                return (string.Empty, string.Empty);
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName?.Trim() ?? string.Empty;

            string processPath;
            try
            {
                processPath = process.MainModule?.FileName?.Trim() ?? string.Empty;
            }
            catch
            {
                processPath = string.Empty;
            }

            return (processName, processPath);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    private static bool ContainsIgnoreCase(string source, string keyword)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSendableInputFocus(IntPtr targetHandle)
    {
        if (!TryGetGuiThreadInfo(targetHandle, out var guiThreadInfo))
        {
            return false;
        }

        var focusHandle = guiThreadInfo.hwndFocus != IntPtr.Zero
            ? guiThreadInfo.hwndFocus
            : guiThreadInfo.hwndCaret;
        if (focusHandle == IntPtr.Zero || !NativeMethods.IsWindow(focusHandle))
        {
            return false;
        }

        var focusRoot = NativeMethods.GetAncestor(focusHandle, NativeMethods.GaRootowner);
        if (focusRoot == IntPtr.Zero)
        {
            focusRoot = NativeMethods.GetAncestor(focusHandle, NativeMethods.GaRoot);
        }

        if (focusRoot != IntPtr.Zero && focusRoot != targetHandle)
        {
            return false;
        }

        var focusClassName = GetWindowClassName(focusHandle);
        if (DirectInputFocusClassKeywords.Any(keyword => ContainsIgnoreCase(focusClassName, keyword)))
        {
            return true;
        }

        if (BrowserHostedInputFocusClassKeywords.Any(keyword => ContainsIgnoreCase(focusClassName, keyword)))
        {
            return guiThreadInfo.hwndCaret != IntPtr.Zero;
        }

        return guiThreadInfo.hwndCaret != IntPtr.Zero;
    }

    private static bool TryGetGuiThreadInfo(IntPtr targetHandle, out NativeMethods.GUITHREADINFO guiThreadInfo)
    {
        guiThreadInfo = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        var threadId = NativeMethods.GetWindowThreadProcessId(targetHandle, IntPtr.Zero);
        return threadId != 0 && NativeMethods.GetGUIThreadInfo(threadId, ref guiThreadInfo);
    }

    private void ClearDockState()
    {
        _lastDockedTarget = IntPtr.Zero;
        _lastLeft = double.NaN;
        _lastTop = double.NaN;
        _lastTargetLeft = 0;
        _lastTargetTop = 0;
        _lastTargetWidth = 0;
        _lastTargetHeight = 0;
    }

    private void MoveNextToTarget(WindowDockTargetInfo target, bool forceMove)
    {
        if (_window is null)
        {
            return;
        }

        var screen = Screen.FromHandle(target.Handle).WorkingArea;
        var preferredWidth = Math.Clamp(_sidebarWidth, MinDockWidth, MaxDockWidth);
        var preferredHeight = Math.Clamp(target.Height * (_sidebarHeightPercent / 100d), MinDockHeight, screen.Height);

        var rightSpace = screen.Right - target.Right - DockGap;
        var leftSpace = target.Left - screen.Left - DockGap;

        double toolWidth;
        double desiredLeft;

        if (rightSpace >= MinDockWidth)
        {
            toolWidth = Math.Min(preferredWidth, rightSpace);
            desiredLeft = target.Right + DockGap;
        }
        else if (leftSpace >= MinDockWidth)
        {
            toolWidth = Math.Min(preferredWidth, leftSpace);
            desiredLeft = target.Left - toolWidth - DockGap;
        }
        else
        {
            toolWidth = Math.Min(preferredWidth, Math.Max(MinDockWidth, screen.Width * 0.32));
            desiredLeft = Math.Max(screen.Left, screen.Right - toolWidth);
        }

        var toolHeight = Math.Min(preferredHeight, screen.Height);
        var centeredTop = target.Top + ((target.Height - toolHeight) / 2d);
        var desiredTop = Math.Max(screen.Top, Math.Min(centeredTop, screen.Bottom - toolHeight));

        if (!forceMove &&
            _lastDockedTarget == target.Handle &&
            HasTargetBoundsChanged(target))
        {
            ClearDockState();
            HideDockBar(force: true);
            return;
        }

        if (!forceMove &&
            _lastDockedTarget == target.Handle &&
            Math.Abs(_window.Width - toolWidth) < DockMoveThreshold &&
            Math.Abs(_window.Height - toolHeight) < DockMoveThreshold &&
            Math.Abs(_lastLeft - desiredLeft) < DockMoveThreshold &&
            Math.Abs(_lastTop - desiredTop) < DockMoveThreshold)
        {
            EnsureDockBarZOrder();
            return;
        }

        ShowWindowIfNeeded();
        _window.Width = toolWidth;
        _window.Height = toolHeight;
        _window.Left = desiredLeft;
        _window.Top = desiredTop;
        EnsureDockBarZOrder();
        _lastDockedTarget = target.Handle;
        _lastLeft = desiredLeft;
        _lastTop = desiredTop;
        _lastTargetLeft = target.Left;
        _lastTargetTop = target.Top;
        _lastTargetWidth = target.Width;
        _lastTargetHeight = target.Height;
    }

    private bool HasTargetBoundsChanged(WindowDockTargetInfo target)
    {
        return Math.Abs(target.Left - _lastTargetLeft) >= DockMoveThreshold ||
               Math.Abs(target.Top - _lastTargetTop) >= DockMoveThreshold ||
               Math.Abs(target.Width - _lastTargetWidth) >= DockMoveThreshold ||
               Math.Abs(target.Height - _lastTargetHeight) >= DockMoveThreshold;
    }

    private void HideDockBar(bool force = false)
    {
        if (_isPreviewMode || _window is null || !_window.IsVisible)
        {
            return;
        }

        if (!force && _window.IsActive)
        {
            return;
        }

        WindowTransitionService.HideWithFade(_window);
    }

    private bool ShouldForceHideDockBar()
    {
        if (_lastDockedTarget == IntPtr.Zero)
        {
            return false;
        }

        return TryGetTargetInfo(_lastDockedTarget) is null;
    }

    private bool TryMaintainDockForOwnProcessWindow(bool forceMove)
    {
        if (_window is null || _lastDockedTarget == IntPtr.Zero)
        {
            return false;
        }

        var targetInfo = TryGetTargetInfo(_lastDockedTarget);
        if (targetInfo is null || !MatchesBuiltInTargets(targetInfo))
        {
            return false;
        }

        MoveNextToTarget(targetInfo, forceMove);
        return true;
    }

    private void ShowWindowIfNeeded()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        if (!_window.IsVisible)
        {
            WindowTransitionService.ShowWithFade(_window);
        }

        if (_mainWindowHandle == IntPtr.Zero)
        {
            _mainWindowHandle = new WindowInteropHelper(_window).Handle;
        }
    }

    private void EnsureDockBarZOrder()
    {
        if (_window is null || _mainWindowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _mainWindowHandle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);

        NativeMethods.SetWindowPos(
            _mainWindowHandle,
            NativeMethods.HwndNotopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);
    }
}
