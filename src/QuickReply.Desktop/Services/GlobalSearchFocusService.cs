using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Collections.Generic;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Native;

namespace QuickReply.Desktop.Services;

public sealed class GlobalSearchFocusService
{
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private Window? _window;
    private IntPtr _mainWindowHandle;
    private Func<SearchFocusHotkeyMode>? _getMode;
    private Func<IntPtr, bool>? _canRedirect;
    private Action<IntPtr>? _focusSearchAction;
    private Func<IntPtr, bool>? _allowOwnProcessRedirect;
    private NativeMethods.LowLevelKeyboardProc? _hookCallback;
    private IntPtr _hookHandle;
    private long _lastCtrlPressedAt;
    private long _lastTriggeredAt;
    private IntPtr _lastTriggerWindowHandle;
    private bool _awaitingTabRelease;
    private bool _altPressedByHook;

    public void Start(
        Window window,
        Func<SearchFocusHotkeyMode> getMode,
        Func<IntPtr, bool> canRedirect,
        Action<IntPtr> focusSearchAction,
        Func<IntPtr, bool>? allowOwnProcessRedirect = null)
    {
        _window = window;
        _mainWindowHandle = new WindowInteropHelper(window).Handle;
        _getMode = getMode;
        _canRedirect = canRedirect;
        _focusSearchAction = focusSearchAction;
        _allowOwnProcessRedirect = allowOwnProcessRedirect;
        _hookCallback = HookCallback;
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _hookCallback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("Failed to install keyboard hook. Error: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("Keyboard hook installed successfully. Handle: " + _hookHandle);
        }
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
        }

        _hookHandle = IntPtr.Zero;
        _hookCallback = null;
        _getMode = null;
        _canRedirect = null;
        _focusSearchAction = null;
        _allowOwnProcessRedirect = null;
        _mainWindowHandle = IntPtr.Zero;
        _lastCtrlPressedAt = 0;
        _lastTriggeredAt = 0;
        _lastTriggerWindowHandle = IntPtr.Zero;
        _awaitingTabRelease = false;
        _altPressedByHook = false;
        _window = null;
    }

    public void SuppressTabUntilRelease()
    {
        _awaitingTabRelease = true;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _window is null || _getMode is null || _canRedirect is null || _focusSearchAction is null)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var keyboardData = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        UpdateModifierState(message, keyboardData);

        if (message == NativeMethods.WmKeyup || message == NativeMethods.WmSyskeyup)
        {
            if (keyboardData.vkCode == NativeMethods.VkTab)
            {
                _awaitingTabRelease = false;
            }

            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (message != NativeMethods.WmKeydown && message != NativeMethods.WmSyskeydown)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var handled = _getMode() switch
        {
            SearchFocusHotkeyMode.DoubleCtrl => TryHandleDoubleCtrl(keyboardData),
            SearchFocusHotkeyMode.Tab => TryHandleTab(message, keyboardData),
            _ => TryHandleAltQ(keyboardData)
        };

        if (!handled)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        System.Diagnostics.Debug.WriteLine("Triggering focus search action");
        var triggerWindowHandle = _lastTriggerWindowHandle;
        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() => _focusSearchAction(triggerWindowHandle)));
        return (IntPtr)1;
    }

    private bool TryHandleAltQ(NativeMethods.KBDLLHOOKSTRUCT keyboardData)
    {
        if (keyboardData.vkCode != NativeMethods.VkQ || !IsPressed(NativeMethods.VkMenu))
        {
            return false;
        }

        System.Diagnostics.Debug.WriteLine("Alt+Q detected");
        return TryThrottleTrigger();
    }

    private bool TryHandleDoubleCtrl(NativeMethods.KBDLLHOOKSTRUCT keyboardData)
    {
        if (keyboardData.vkCode != NativeMethods.VkLcontrol && keyboardData.vkCode != NativeMethods.VkRcontrol)
        {
            return false;
        }

        if (IsPressed(NativeMethods.VkMenu) || IsPressed(NativeMethods.VkShift))
        {
            _lastCtrlPressedAt = 0;
            return false;
        }

        var now = Environment.TickCount64;
        var doubleClickWindow = System.Windows.Forms.SystemInformation.DoubleClickTime;
        var isDoubleCtrl = _lastCtrlPressedAt > 0 && now - _lastCtrlPressedAt <= doubleClickWindow;
        _lastCtrlPressedAt = now;

        if (isDoubleCtrl)
        {
            System.Diagnostics.Debug.WriteLine("Double Ctrl detected");
            return TryThrottleTrigger();
        }

        return false;
    }

    private bool TryHandleTab(int message, NativeMethods.KBDLLHOOKSTRUCT keyboardData)
    {
        if (keyboardData.vkCode != NativeMethods.VkTab)
        {
            return false;
        }

        if (IsAltChordActive(message, keyboardData) ||
            IsPressed(NativeMethods.VkShift) ||
            IsPressed(NativeMethods.VkLcontrol) ||
            IsPressed(NativeMethods.VkRcontrol))
        {
            var payload = new Dictionary<string, object?>
            {
                ["message"] = message,
                ["altPressedByHook"] = _altPressedByHook,
                ["altDownFlag"] = (keyboardData.flags & NativeMethods.LlkhfAltdown) != 0,
                ["tabVkCode"] = keyboardData.vkCode,
                ["tabFlags"] = keyboardData.flags
            };
            SearchOverlayDiagnostics.Log("global_search_focus", "trigger_skipped_system_modifier", payload);
            return false;
        }

        if (_awaitingTabRelease)
        {
            SearchOverlayDiagnostics.Log("global_search_focus", "trigger_swallowed_waiting_tab_release");
            return true;
        }

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (!IsSupportedWorkbenchWindow(foregroundWindow))
        {
            var payload = SearchOverlayDiagnostics.CreateWindowSnapshot("foreground", foregroundWindow);
            payload["hotkeyMode"] = SearchFocusHotkeyMode.Tab.ToString();
            SearchOverlayDiagnostics.Log("global_search_focus", "trigger_skipped_tab_unsupported_window", payload);
            return false;
        }

        System.Diagnostics.Debug.WriteLine("Tab detected");
        var triggered = TryThrottleTrigger();
        if (triggered)
        {
            _awaitingTabRelease = true;
        }

        return triggered;
    }

    private void UpdateModifierState(int message, NativeMethods.KBDLLHOOKSTRUCT keyboardData)
    {
        if (keyboardData.vkCode != NativeMethods.VkMenu &&
            keyboardData.vkCode != NativeMethods.VkLmenu &&
            keyboardData.vkCode != NativeMethods.VkRmenu)
        {
            return;
        }

        if (message == NativeMethods.WmKeyup || message == NativeMethods.WmSyskeyup)
        {
            _altPressedByHook = false;
            return;
        }

        if (message == NativeMethods.WmKeydown || message == NativeMethods.WmSyskeydown)
        {
            _altPressedByHook = true;
        }
    }

    private bool IsAltChordActive(int message, NativeMethods.KBDLLHOOKSTRUCT keyboardData)
    {
        if (message == NativeMethods.WmSyskeydown)
        {
            return true;
        }

        return _altPressedByHook ||
               (keyboardData.flags & NativeMethods.LlkhfAltdown) != 0 ||
               IsPressed(NativeMethods.VkMenu) ||
               IsPressed(NativeMethods.VkLmenu) ||
               IsPressed(NativeMethods.VkRmenu);
    }

    private bool TryThrottleTrigger()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            SearchOverlayDiagnostics.Log("global_search_focus", "trigger_skipped_foreground_empty");
            return false;
        }

        var isOwnProcessWindow = IsOwnProcessWindow(foregroundWindow);
        if (isOwnProcessWindow &&
            (_allowOwnProcessRedirect is null || !_allowOwnProcessRedirect(foregroundWindow)))
        {
            return false;
        }

        if (_canRedirect is not null && !_canRedirect(foregroundWindow))
        {
            return false;
        }

        var now = Environment.TickCount64;
        if (now - _lastTriggeredAt < 250)
        {
            var payload = SearchOverlayDiagnostics.CreateWindowSnapshot("foreground", foregroundWindow);
            payload["elapsedSinceLastTriggerMs"] = now - _lastTriggeredAt;
            SearchOverlayDiagnostics.Log("global_search_focus", "trigger_skipped_throttled", payload);
            return false;
        }

        _lastTriggeredAt = now;
        _lastTriggerWindowHandle = isOwnProcessWindow || foregroundWindow == _mainWindowHandle
            ? IntPtr.Zero
            : foregroundWindow;
        var acceptedPayload = SearchOverlayDiagnostics.CreateWindowSnapshot("foreground", foregroundWindow);
        acceptedPayload["hotkeyMode"] = _getMode?.Invoke().ToString();
        acceptedPayload["bypassThrottle"] = false;
        acceptedPayload["isOwnProcessWindow"] = isOwnProcessWindow;
        acceptedPayload["triggerWindowHandle"] = SearchOverlayDiagnostics.FormatHandle(_lastTriggerWindowHandle);
        SearchOverlayDiagnostics.Log("global_search_focus", "trigger_allowed", acceptedPayload);
        return true;
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

    private static bool IsSupportedWorkbenchWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        return SupportedSearchFocusWorkbenchMatcher.IsSupported(TryGetWindowInfo(windowHandle));
    }

    private static WindowDockTargetInfo? TryGetWindowInfo(IntPtr windowHandle)
    {
        windowHandle = NormalizeTopLevelWindow(windowHandle);
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            return null;
        }

        var titleLength = NativeMethods.GetWindowTextLength(windowHandle);
        var title = string.Empty;
        if (titleLength > 0)
        {
            var titleBuilder = new System.Text.StringBuilder(titleLength + 1);
            NativeMethods.GetWindowText(windowHandle, titleBuilder, titleBuilder.Capacity);
            title = titleBuilder.ToString().Trim();
        }

        var className = string.Empty;
        var classNameBuilder = new System.Text.StringBuilder(256);
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
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                processName = process.ProcessName?.Trim() ?? string.Empty;
                try
                {
                    processPath = process.MainModule?.FileName?.Trim() ?? string.Empty;
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
            Left = 0,
            Top = 0,
            Width = 0,
            Height = 0
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

    private static bool IsPressed(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
}
