param(
    [string]$ImagePath = "",
    [string]$Text = "image-send-diagnostic",
    [string]$Mode = "image_then_text_then_send",
    [int]$ArmDelayMs = 1500,
    [int]$TextSettleMs = 200,
    [int]$ImageSettleMs = 1200,
    [int]$ObserveIntervalMs = 50,
    [int]$ObserveTailMs = 3000,
    [switch]$SendPaste,
    [switch]$SendEnter,
    [switch]$PersistText,
    [switch]$PersistImage,
    [switch]$NoPngFormats,
    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA)
{
    $argList = @("-NoProfile", "-Sta", "-ExecutionPolicy", "Bypass", "-File", ('"{0}"' -f $PSCommandPath))
    foreach ($key in $PSBoundParameters.Keys)
    {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch] -and $value.IsPresent)
        {
            $argList += "-$key"
            continue
        }

        $escaped = ($value.ToString()).Replace('"', '\"')
        $argList += "-$key"
        $argList += ('"{0}"' -f $escaped)
    }

    $commandLine = $argList -join " "
    Start-Process powershell -ArgumentList $commandLine -Wait
    exit $LASTEXITCODE
}

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$helperSource = @"
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipboardTrace
{
    public sealed class WindowSnapshot
    {
        public WindowSnapshot()
        {
            Handle = "0x0";
            Title = string.Empty;
            ClassName = string.Empty;
        }

        public string Handle { get; set; }
        public bool Exists { get; set; }
        public string Title { get; set; }
        public string ClassName { get; set; }
        public uint ProcessId { get; set; }
        public uint ThreadId { get; set; }
    }

    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static uint GetSequenceNumber()
        {
            return GetClipboardSequenceNumber();
        }

        public static WindowSnapshot CaptureForeground()
        {
            var handle = GetForegroundWindow();
            var snapshot = new WindowSnapshot();
            snapshot.Handle = string.Format("0x{0:X}", handle.ToInt64());

            if (handle == IntPtr.Zero || !IsWindow(handle))
            {
                return snapshot;
            }

            snapshot.Exists = true;
            uint processId;
            snapshot.ThreadId = GetWindowThreadProcessId(handle, out processId);
            snapshot.ProcessId = processId;

            var titleLength = Math.Max(256, GetWindowTextLength(handle) + 1);
            var titleBuilder = new StringBuilder(titleLength);
            GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
            snapshot.Title = titleBuilder.ToString();

            var classBuilder = new StringBuilder(256);
            GetClassName(handle, classBuilder, classBuilder.Capacity);
            snapshot.ClassName = classBuilder.ToString();
            return snapshot;
        }

        public static int ProbeOpenClipboard()
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                return Marshal.GetLastWin32Error();
            }

            CloseClipboard();
            return 0;
        }

        public static void SendCtrlV()
        {
            SendChord(0x11, 0x56);
        }

        public static void SendEnter()
        {
            SendChord(0x0D);
        }

        private static void SendChord(params ushort[] keys)
        {
            var inputs = new INPUT[keys.Length * 2];
            var index = 0;

            foreach (var key in keys)
            {
                inputs[index++] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = key,
                            dwFlags = 0
                        }
                    }
                };
            }

            for (var i = keys.Length - 1; i >= 0; i--)
            {
                inputs[index++] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = keys[i],
                            dwFlags = KEYEVENTF_KEYUP
                        }
                    }
                };
            }

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
    }
}
"@

Add-Type -TypeDefinition $helperSource -ReferencedAssemblies @("System.dll")

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $OutputDirectory = Join-Path $repoRoot "artifacts\image-send-diagnostics"
}

if (-not (Test-Path -LiteralPath $OutputDirectory))
{
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputDir = Join-Path $OutputDirectory $timestamp
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$tracePath = Join-Path $outputDir "trace.jsonl"
$summaryPath = Join-Path $outputDir "summary.txt"
$todayLogName = "{0:yyyyMMdd}.jsonl" -f (Get-Date)
$searchOverlayLogPath = Join-Path $env:APPDATA "QuickReplyTool\logs\search-overlay\$todayLogName"
$baselineLineCount = if (Test-Path -LiteralPath $searchOverlayLogPath) { (Get-Content -LiteralPath $searchOverlayLogPath | Measure-Object -Line).Lines } else { 0 }

function Write-JsonLine {
    param([hashtable]$Payload)
    ($Payload | ConvertTo-Json -Depth 8 -Compress) | Out-File -FilePath $tracePath -Encoding utf8 -Append
}

function Write-SummaryLine {
    param([string]$Line)
    $Line | Out-File -FilePath $summaryPath -Encoding utf8 -Append
}

function Resolve-DefaultImagePath {
    $imagesRoot = Join-Path $env:APPDATA "QuickReplyTool\phrase-images"
    if (-not (Test-Path -LiteralPath $imagesRoot))
    {
        return ""
    }

    $latest = Get-ChildItem -LiteralPath $imagesRoot -File -Recurse |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $latest)
    {
        return $latest.FullName
    }

    return ""
}

if ([string]::IsNullOrWhiteSpace($ImagePath))
{
    $ImagePath = Resolve-DefaultImagePath
}

if (($Mode -ne "file_drop_send") -and [string]::IsNullOrWhiteSpace($ImagePath))
{
    throw "No image file was provided and no recent phrase image was found."
}

function Get-ClipboardState {
    $state = [ordered]@{
        sequenceNumber = [ClipboardTrace.Native]::GetSequenceNumber()
        openClipboardError = [ClipboardTrace.Native]::ProbeOpenClipboard()
        foreground = [ClipboardTrace.Native]::CaptureForeground()
        formats = @()
        hasText = $false
        hasBitmap = $false
        hasFileDrop = $false
        error = $null
    }

    try
    {
        $data = [System.Windows.Clipboard]::GetDataObject()
        if ($null -ne $data)
        {
            $formats = $data.GetFormats() | Sort-Object
            $state.formats = $formats
            $state.hasText = $formats -contains [System.Windows.DataFormats]::UnicodeText -or $formats -contains [System.Windows.DataFormats]::Text
            $state.hasBitmap = $formats -contains [System.Windows.DataFormats]::Bitmap
            $state.hasFileDrop = $formats -contains [System.Windows.DataFormats]::FileDrop
        }
    }
    catch
    {
        $state.error = $_.Exception.Message
    }

    return $state
}

function Load-ImageEntry {
    param([string]$Path)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    try
    {
        $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
            $stream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $frame = $decoder.Frames[0]
        $frame.Freeze()

        $pngStream = New-Object System.IO.MemoryStream
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        [void]$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($frame))
        $encoder.Save($pngStream)

        return [pscustomobject]@{
            Image = $frame
            PngBytes = $pngStream.ToArray()
        }
    }
    finally
    {
        $stream.Dispose()
    }
}

function New-ImageDataObject {
    param(
        [System.Windows.Media.Imaging.BitmapSource]$Image,
        [byte[]]$PngBytes,
        [bool]$IncludePng
    )

    $dataObject = New-Object System.Windows.DataObject
    $dataObject.SetImage($Image)

    if ($IncludePng)
    {
        $dataObject.SetData("PNG", [System.IO.MemoryStream]::new($PngBytes, $false))
        $dataObject.SetData("image/png", [System.IO.MemoryStream]::new($PngBytes, $false))
    }

    return $dataObject
}

function Set-AppTextClipboard {
    param([string]$Value, [bool]$Persist)
    $dataObject = New-Object System.Windows.DataObject
    $dataObject.SetData([System.Windows.DataFormats]::UnicodeText, $Value)
    $dataObject.SetData([System.Windows.DataFormats]::Text, $Value)
    Invoke-ClipboardWrite -Name "set_text" -Persist:$Persist -Action {
        [System.Windows.Clipboard]::SetDataObject($dataObject, $Persist)
        if ($Persist)
        {
            [System.Windows.Clipboard]::Flush()
        }
    }
}

function Set-AppImageClipboard {
    param(
        [pscustomobject]$ImageEntry,
        [bool]$Persist,
        [bool]$IncludePng
    )

    $dataObject = New-ImageDataObject -Image $ImageEntry.Image -PngBytes $ImageEntry.PngBytes -IncludePng:$IncludePng
    Invoke-ClipboardWrite -Name "set_image" -Persist:$Persist -Action {
        [System.Windows.Clipboard]::SetDataObject($dataObject, $Persist)
        if ($Persist)
        {
            [System.Windows.Clipboard]::Flush()
        }
    }
}

function Set-AppFileDropClipboard {
    param([string]$Path, [bool]$Persist)
    $collection = New-Object System.Collections.Specialized.StringCollection
    [void]$collection.Add($Path)
    $dataObject = New-Object System.Windows.DataObject
    $dataObject.SetFileDropList($collection)
    Invoke-ClipboardWrite -Name "set_filedrop" -Persist:$Persist -Action {
        [System.Windows.Clipboard]::SetDataObject($dataObject, $Persist)
        if ($Persist)
        {
            [System.Windows.Clipboard]::Flush()
        }
    }
}

function Invoke-ClipboardWrite {
    param(
        [string]$Name,
        [bool]$Persist,
        [scriptblock]$Action
    )

    $attempt = 0
    $retryCount = 20
    $delayMs = 120
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastException = $null

    while ($attempt -lt $retryCount)
    {
        $attempt++
        try
        {
            & $Action
            Write-JsonLine @{
                occurredAt = (Get-Date).ToString("o")
                event = "clipboard_write_success"
                name = $Name
                persist = $Persist
                attempt = $attempt
                durationMs = $sw.ElapsedMilliseconds
                clipboard = Get-ClipboardState
            }
            return
        }
        catch
        {
            $lastException = $_.Exception
            Write-JsonLine @{
                occurredAt = (Get-Date).ToString("o")
                event = "clipboard_write_retry"
                name = $Name
                persist = $Persist
                attempt = $attempt
                durationMs = $sw.ElapsedMilliseconds
                message = $_.Exception.Message
                clipboard = Get-ClipboardState
            }
            Start-Sleep -Milliseconds $delayMs
        }
    }

    throw $lastException
}

function Observe-Phase {
    param(
        [string]$Name,
        [int]$DurationMs
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $DurationMs)
    {
        Write-JsonLine @{
            occurredAt = (Get-Date).ToString("o")
            event = "observe"
            phase = $Name
            elapsedMs = $stopwatch.ElapsedMilliseconds
            clipboard = Get-ClipboardState
        }
        Start-Sleep -Milliseconds $ObserveIntervalMs
    }
}

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    $startedAt = Get-Date
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-JsonLine @{
        occurredAt = $startedAt.ToString("o")
        event = "step_start"
        step = $Name
        clipboard = Get-ClipboardState
    }

    try
    {
        & $Action
        Write-JsonLine @{
            occurredAt = (Get-Date).ToString("o")
            event = "step_success"
            step = $Name
            durationMs = $sw.ElapsedMilliseconds
            clipboard = Get-ClipboardState
        }
    }
    catch
    {
        Write-JsonLine @{
            occurredAt = (Get-Date).ToString("o")
            event = "step_failure"
            step = $Name
            durationMs = $sw.ElapsedMilliseconds
            message = $_.Exception.Message
            clipboard = Get-ClipboardState
        }
        throw
    }
}

$imageEntry = if ($Mode -ne "file_drop_send") { Load-ImageEntry -Path $ImagePath } else { $null }

Write-SummaryLine "GeneratedAt: $(Get-Date -Format o)"
Write-SummaryLine "Mode: $Mode"
Write-SummaryLine "ImagePath: $ImagePath"
Write-SummaryLine "PersistText: $($PersistText.IsPresent)"
Write-SummaryLine "PersistImage: $($PersistImage.IsPresent)"
Write-SummaryLine "IncludePngFormats: $($NoPngFormats.IsPresent -eq $false)"
Write-SummaryLine "SendPaste: $($SendPaste.IsPresent)"
Write-SummaryLine "SendEnter: $($SendEnter.IsPresent)"
Write-SummaryLine "ArmDelayMs: $ArmDelayMs"
Write-SummaryLine "TextSettleMs: $TextSettleMs"
Write-SummaryLine "ImageSettleMs: $ImageSettleMs"
Write-SummaryLine "ObserveTailMs: $ObserveTailMs"

Write-JsonLine @{
    occurredAt = (Get-Date).ToString("o")
    event = "baseline"
    clipboard = Get-ClipboardState
}

Write-SummaryLine "Focus the target input box now. The script will start after the arm delay."
Start-Sleep -Milliseconds $ArmDelayMs

$scriptFailed = $false
$fatalMessage = ""

try
{
    switch ($Mode)
    {
        "image_then_text_then_send" {
            Invoke-Step "set_image_clipboard" {
                Set-AppImageClipboard -ImageEntry $imageEntry -Persist:$PersistImage.IsPresent -IncludePng:($NoPngFormats.IsPresent -eq $false)
            }
            if ($SendPaste)
            {
                Invoke-Step "send_paste_image" { [ClipboardTrace.Native]::SendCtrlV() }
            }
            Observe-Phase -Name "after_image" -DurationMs $ImageSettleMs

            if (-not [string]::IsNullOrWhiteSpace($Text))
            {
                Invoke-Step "set_text_clipboard" {
                    Set-AppTextClipboard -Value $Text -Persist:$PersistText.IsPresent
                }
                if ($SendPaste)
                {
                    Invoke-Step "send_paste_text" { [ClipboardTrace.Native]::SendCtrlV() }
                }
                Observe-Phase -Name "after_text" -DurationMs $TextSettleMs
            }
        }
        "text_then_image_then_send" {
            if (-not [string]::IsNullOrWhiteSpace($Text))
            {
                Invoke-Step "set_text_clipboard" {
                    Set-AppTextClipboard -Value $Text -Persist:$PersistText.IsPresent
                }
                if ($SendPaste)
                {
                    Invoke-Step "send_paste_text" { [ClipboardTrace.Native]::SendCtrlV() }
                }
                Observe-Phase -Name "after_text" -DurationMs $TextSettleMs
            }

            Invoke-Step "set_image_clipboard" {
                Set-AppImageClipboard -ImageEntry $imageEntry -Persist:$PersistImage.IsPresent -IncludePng:($NoPngFormats.IsPresent -eq $false)
            }
            if ($SendPaste)
            {
                Invoke-Step "send_paste_image" { [ClipboardTrace.Native]::SendCtrlV() }
            }
            Observe-Phase -Name "after_image" -DurationMs $ImageSettleMs
        }
        "image_only_send" {
            Invoke-Step "set_image_clipboard" {
                Set-AppImageClipboard -ImageEntry $imageEntry -Persist:$PersistImage.IsPresent -IncludePng:($NoPngFormats.IsPresent -eq $false)
            }
            if ($SendPaste)
            {
                Invoke-Step "send_paste_image" { [ClipboardTrace.Native]::SendCtrlV() }
            }
            Observe-Phase -Name "after_image" -DurationMs $ImageSettleMs
        }
        "file_drop_send" {
            Invoke-Step "set_filedrop_clipboard" {
                Set-AppFileDropClipboard -Path $ImagePath -Persist:$PersistImage.IsPresent
            }
            if ($SendPaste)
            {
                Invoke-Step "send_paste_file" { [ClipboardTrace.Native]::SendCtrlV() }
            }
            Observe-Phase -Name "after_filedrop" -DurationMs $ImageSettleMs
        }
    }

    if ($SendEnter)
    {
        Invoke-Step "send_enter" { [ClipboardTrace.Native]::SendEnter() }
    }

    Observe-Phase -Name "tail" -DurationMs $ObserveTailMs
}
catch
{
    $scriptFailed = $true
    $fatalMessage = $_.Exception.Message
    Write-JsonLine @{
        occurredAt = (Get-Date).ToString("o")
        event = "fatal"
        message = $fatalMessage
        clipboard = Get-ClipboardState
    }
    Write-SummaryLine "Fatal: $fatalMessage"
}

if (Test-Path -LiteralPath $searchOverlayLogPath)
{
    $tailPath = Join-Path $outputDir "search-overlay-tail.jsonl"
    Get-Content -LiteralPath $searchOverlayLogPath |
        Select-Object -Skip $baselineLineCount |
        Out-File -FilePath $tailPath -Encoding utf8
    Write-SummaryLine "SearchOverlayTail: $tailPath"
}

Write-SummaryLine "TracePath: $tracePath"
Write-SummaryLine "OutputDir: $outputDir"
Write-Output $outputDir

if ($scriptFailed)
{
    exit 1
}
