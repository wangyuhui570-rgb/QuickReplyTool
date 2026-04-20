param(
    [int]$DurationSeconds = 6,
    [int]$IntervalMs = 25,
    [int]$ArmDelayMs = 1200,
    [string]$OutputDirectory = "",
    [switch]$AutoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $OutputDirectory = Join-Path $repoRoot "artifacts\focus-diagnostics"
}

if (-not (Test-Path -LiteralPath $OutputDirectory))
{
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$samplePath = Join-Path $OutputDirectory "focus-samples-$timestamp.jsonl"
$appTailPath = Join-Path $OutputDirectory "search-overlay-tail-$timestamp.jsonl"
$summaryPath = Join-Path $OutputDirectory "summary-$timestamp.txt"
$todayLogName = "{0:yyyyMMdd}.jsonl" -f (Get-Date)
$searchOverlayLogPath = Join-Path $env:APPDATA "QuickReplyTool\logs\search-overlay\$todayLogName"
$baselineLineCount = 0

if (Test-Path -LiteralPath $searchOverlayLogPath)
{
    $baselineLineCount = (Get-Content -LiteralPath $searchOverlayLogPath | Measure-Object -Line).Lines
}

$traceSource = @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FocusTrace
{
public sealed class WindowSnapshot
{
    public WindowSnapshot()
    {
        Handle = "0x0";
        Title = string.Empty;
        ClassName = string.Empty;
        ProcessName = string.Empty;
    }

    public string Handle { get; set; }
    public bool Exists { get; set; }
    public bool Visible { get; set; }
    public bool Minimized { get; set; }
    public string Title { get; set; }
    public string ClassName { get; set; }
    public uint ProcessId { get; set; }
    public uint ThreadId { get; set; }
    public string ProcessName { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

public sealed class GuiThreadSnapshot
{
    public GuiThreadSnapshot()
    {
        CaretRect = string.Empty;
        ActiveWindow = new WindowSnapshot();
        FocusWindow = new WindowSnapshot();
        CaptureWindow = new WindowSnapshot();
        CaretWindow = new WindowSnapshot();
    }

    public uint ThreadId { get; set; }
    public bool Available { get; set; }
    public uint Flags { get; set; }
    public string CaretRect { get; set; }
    public WindowSnapshot ActiveWindow { get; set; }
    public WindowSnapshot FocusWindow { get; set; }
    public WindowSnapshot CaptureWindow { get; set; }
    public WindowSnapshot CaretWindow { get; set; }
}

public static class Diagnostics
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    public static WindowSnapshot CaptureForegroundWindow()
    {
        return CaptureWindow(GetForegroundWindow());
    }

    public static WindowSnapshot CaptureWindow(IntPtr handle)
    {
        var snapshot = new WindowSnapshot();
        snapshot.Handle = FormatHandle(handle);

        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return snapshot;
        }

        snapshot.Exists = true;
        snapshot.Visible = IsWindowVisible(handle);
        snapshot.Minimized = IsIconic(handle);
        snapshot.Title = GetWindowTitle(handle);
        snapshot.ClassName = GetWindowClass(handle);
        uint processId;
        snapshot.ThreadId = GetWindowThreadProcessId(handle, out processId);
        snapshot.ProcessId = processId;
        snapshot.ProcessName = GetProcessName(processId);

        RECT rect;
        if (GetWindowRect(handle, out rect))
        {
            snapshot.Left = rect.Left;
            snapshot.Top = rect.Top;
            snapshot.Right = rect.Right;
            snapshot.Bottom = rect.Bottom;
        }

        return snapshot;
    }

    public static GuiThreadSnapshot CaptureGuiThread(uint threadId)
    {
        var snapshot = new GuiThreadSnapshot();
        snapshot.ThreadId = threadId;

        if (threadId == 0)
        {
            return snapshot;
        }

        var guiInfo = new GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<GUITHREADINFO>()
        };

        if (!GetGUIThreadInfo(threadId, ref guiInfo))
        {
            return snapshot;
        }

        snapshot.Available = true;
        snapshot.Flags = guiInfo.flags;
        snapshot.CaretRect = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0},{1},{2},{3}",
            guiInfo.rcCaret.Left,
            guiInfo.rcCaret.Top,
            guiInfo.rcCaret.Right,
            guiInfo.rcCaret.Bottom);
        snapshot.ActiveWindow = CaptureWindow(guiInfo.hwndActive);
        snapshot.FocusWindow = CaptureWindow(guiInfo.hwndFocus);
        snapshot.CaptureWindow = CaptureWindow(guiInfo.hwndCapture);
        snapshot.CaretWindow = CaptureWindow(guiInfo.hwndCaret);
        return snapshot;
    }

    public static WindowSnapshot[] CaptureProcessWindows(uint processId)
    {
        var windows = new List<WindowSnapshot>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindow(hWnd))
            {
                return true;
            }

            uint currentProcessId;
            GetWindowThreadProcessId(hWnd, out currentProcessId);
            if (currentProcessId != processId)
            {
                return true;
            }

            windows.Add(CaptureWindow(hWnd));
            return true;
        }, IntPtr.Zero);

        return windows.ToArray();
    }

    private static string FormatHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return "0x0";
        }

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "0x{0:X}",
            unchecked((ulong)handle.ToInt64()));
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = Math.Max(256, GetWindowTextLength(handle) + 1);
        var builder = new StringBuilder(length);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string GetWindowClass(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        GetClassName(handle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            using (var process = Process.GetProcessById((int)processId))
            {
                return process.ProcessName;
            }
        }
        catch
        {
            return string.Empty;
        }
    }
}
}
"@

Add-Type -TypeDefinition $traceSource -Language CSharp

function Get-QuickReplyWindows {
    $snapshots = New-Object System.Collections.Generic.List[object]
    $processes = Get-Process -Name QuickReplyTool -ErrorAction SilentlyContinue
    foreach ($process in $processes)
    {
        foreach ($window in [FocusTrace.Diagnostics]::CaptureProcessWindows([uint32]$process.Id))
        {
            $snapshots.Add($window)
        }
    }

    return $snapshots
}

function Get-SampleRecord {
    param(
        [long]$ElapsedMs
    )

    $foreground = [FocusTrace.Diagnostics]::CaptureForegroundWindow()
    $foregroundGui = [FocusTrace.Diagnostics]::CaptureGuiThread([uint32]$foreground.ThreadId)
    $foregroundProcessWindows = @()
    if ($foreground.ProcessId -ne 0)
    {
        $foregroundProcessWindows = [FocusTrace.Diagnostics]::CaptureProcessWindows([uint32]$foreground.ProcessId)
    }

    [ordered]@{
        occurredAt = (Get-Date).ToString("O")
        elapsedMs = $ElapsedMs
        foreground = $foreground
        foregroundGui = $foregroundGui
        foregroundProcessWindows = $foregroundProcessWindows
        quickReplyWindows = (Get-QuickReplyWindows)
    }
}

if (-not $AutoStart)
{
    Write-Host "Ready. Focus the target app, then press Enter and reproduce once." -ForegroundColor Cyan
    [void](Read-Host)
}

if ($ArmDelayMs -gt 0)
{
    Write-Host ("Arming capture for {0} ms..." -f $ArmDelayMs) -ForegroundColor Yellow
    Start-Sleep -Milliseconds $ArmDelayMs
}

Write-Host ("Capturing for {0}s at {1}ms interval..." -f $DurationSeconds, $IntervalMs) -ForegroundColor Green

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$deadlineMs = [Math]::Max(1000, $DurationSeconds * 1000)

while ($stopwatch.ElapsedMilliseconds -lt $deadlineMs)
{
    $loopStartMs = $stopwatch.ElapsedMilliseconds
    $record = Get-SampleRecord -ElapsedMs $loopStartMs
    ($record | ConvertTo-Json -Depth 8 -Compress) | Add-Content -LiteralPath $samplePath -Encoding UTF8

    $remaining = $IntervalMs - ($stopwatch.ElapsedMilliseconds - $loopStartMs)
    if ($remaining -gt 0)
    {
        Start-Sleep -Milliseconds $remaining
    }
}

$newAppLogLines = @()
if (Test-Path -LiteralPath $searchOverlayLogPath)
{
    $newAppLogLines = @(Get-Content -LiteralPath $searchOverlayLogPath | Select-Object -Skip $baselineLineCount)
    if ($newAppLogLines.Count -gt 0)
    {
        $newAppLogLines | Set-Content -LiteralPath $appTailPath -Encoding UTF8
    }
}

$interestingEvents = @(
    "show_for_search_start",
    "overlay_shown",
    "open_animation_started",
    "open_animation_completed",
    "activation_started",
    "activation_checkpoint",
    "activation_recovery_started",
    "window_activated",
    "window_deactivated",
    "search_box_got_keyboard_focus",
    "search_box_lost_keyboard_focus",
    "search_box_ready_for_input"
)

$summaryLines = New-Object System.Collections.Generic.List[string]
$summaryLines.Add("sampleLog=$samplePath")
$summaryLines.Add("searchOverlayLogTail=$appTailPath")
$summaryLines.Add("fullSearchOverlayLog=$searchOverlayLogPath")
$summaryLines.Add("durationSeconds=$DurationSeconds")
$summaryLines.Add("intervalMs=$IntervalMs")

if ($newAppLogLines.Count -eq 0)
{
    $summaryLines.Add("appEvents=0")
    $summaryLines.Add("note=No new search-overlay log lines were captured during this run.")
}
else
{
    $events = @(foreach ($line in $newAppLogLines)
    {
        try
        {
            $line | ConvertFrom-Json
        }
        catch
        {
        }
    })

    $summaryLines.Add("appEvents=$($events.Count)")
    foreach ($event in $events | Where-Object { $_.event -in $interestingEvents } | Select-Object -First 40)
    {
        $summaryLines.Add(
            ("event={0} occurredAt={1} elapsedMs={2} directTypingState={3} overlayOpacity={4} fgProcess={5} targetProcess={6}" -f
                $event.event,
                $event.occurredAt,
                $event.elapsedMs,
                $event.directTypingState,
                $event.overlayChromeOpacity,
                $event.foregroundProcessName,
                $event.overlayTargetProcessName))
    }
}

$summaryLines | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host "Capture finished." -ForegroundColor Green
Write-Host ("Samples: {0}" -f $samplePath)
Write-Host ("Summary: {0}" -f $summaryPath)
if ($newAppLogLines.Count -gt 0)
{
    Write-Host ("App log tail: {0}" -f $appTailPath)
}
