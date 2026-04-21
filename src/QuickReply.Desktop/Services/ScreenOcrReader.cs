using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace QuickReply.Desktop.Services;

internal static class ScreenOcrReader
{
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(8);
    private static readonly string[] PowerShellHostCandidates = ["powershell.exe", "pwsh.exe"];
    private static readonly string[] PreferredChineseOcrLanguageTags =
    [
        "zh-Hans-CN",
        "zh-CN",
        "zh-Hans",
        "zh-Hant"
    ];
    private static readonly string[] FallbackLatinOcrLanguageTags = ["en-US"];
    private static readonly Lazy<string> ScriptPath = new(EnsureScriptPath, LazyThreadSafetyMode.ExecutionAndPublication);

    public static async Task<string> TryReadTextAsync(string screenshotPath, string platformHint = "")
    {
        var result = await TryReadTextDetailedAsync(screenshotPath, platformHint).ConfigureAwait(false);
        return result.Text;
    }

    internal static async Task<OcrReadResult> TryReadTextDetailedAsync(string screenshotPath, string platformHint = "")
    {
        if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
        {
            return OcrReadResult.Failure("missing_image");
        }

        var generatedVariantPaths = new List<string>();
        try
        {
            var mergedLines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bestResult = OcrReadResult.Failure("ocr_empty");
            foreach (var imagePath in BuildScanPlan(screenshotPath, platformHint, generatedVariantPaths))
            {
                var result = await TryReadSingleImageDetailedAsync(imagePath, platformHint).ConfigureAwait(false);
                bestResult = PickPreferredResult(bestResult, result);
                foreach (var line in SplitNormalizedLines(result.Text))
                {
                    if (seen.Add(line))
                    {
                        mergedLines.Add(line);
                    }
                }
            }

            if (mergedLines.Count > 0)
            {
                return bestResult with
                {
                    Success = true,
                    Status = "ocr_read",
                    Text = string.Join(Environment.NewLine, mergedLines)
                };
            }

            return bestResult;
        }
        catch
        {
            return OcrReadResult.Failure("ocr_error");
        }
        finally
        {
            foreach (var generatedVariantPath in generatedVariantPaths)
            {
                try
                {
                    if (File.Exists(generatedVariantPath))
                    {
                        File.Delete(generatedVariantPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static string EnsureScriptPath()
    {
        var scriptDirectory = Path.Combine(Path.GetTempPath(), "QuickReplyTool");
        Directory.CreateDirectory(scriptDirectory);

        var scriptPath = Path.Combine(scriptDirectory, "screen-ocr-reader.ps1");
        File.WriteAllText(scriptPath, BuildPowerShellScript(), new UTF8Encoding(false));
        return scriptPath;
    }

    private static string NormalizeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            output
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitNormalizedLines(string output)
    {
        return NormalizeOutput(output)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static IEnumerable<string> BuildScanPlan(
        string screenshotPath,
        string platformHint,
        ICollection<string> generatedVariantPaths)
    {
        yield return screenshotPath;

        foreach (var variantPath in CreatePlatformVariants(screenshotPath, platformHint))
        {
            generatedVariantPaths.Add(variantPath);
            yield return variantPath;
        }
    }

    private static IEnumerable<string> CreatePlatformVariants(string screenshotPath, string platformHint)
    {
        using var image = Image.FromFile(screenshotPath);
        var profile = ResolveProfile(platformHint);
        if (profile.CropRectangles.Count == 0)
        {
            yield break;
        }

        using var bitmap = new Bitmap(image);
        foreach (var normalizedCrop in profile.CropRectangles)
        {
            var cropRect = ToAbsoluteRectangle(bitmap.Width, bitmap.Height, normalizedCrop);
            if (cropRect.Width <= 10 || cropRect.Height <= 10)
            {
                continue;
            }

            using var cropped = bitmap.Clone(cropRect, bitmap.PixelFormat);
            using var prepared = PrepareBitmapForOcr(cropped, profile.ScaleMultiplier);
            var variantPath = Path.Combine(
                Path.GetTempPath(),
                "QuickReplyTool",
                $"{Path.GetFileNameWithoutExtension(screenshotPath)}_{profile.Name}_{Guid.NewGuid():N}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(variantPath)!);
            prepared.Save(variantPath, ImageFormat.Png);
            yield return variantPath;
        }
    }

    private static OcrProfile ResolveProfile(string platformHint)
    {
        return platformHint switch
        {
            "wecom" or "wechat" => new OcrProfile(
                "wecom",
                2.0d,
                [
                    new NormalizedCrop(0.30d, 0.10d, 0.48d, 0.74d),
                    new NormalizedCrop(0.33d, 0.18d, 0.46d, 0.60d),
                    new NormalizedCrop(0.35d, 0.30d, 0.42d, 0.36d)
                ]),
            "qianniu" or "jd_workbench" or "pdd_workbench" => new OcrProfile(
                "workbench",
                2.2d,
                [
                    new NormalizedCrop(0.18d, 0.10d, 0.50d, 0.74d),
                    new NormalizedCrop(0.21d, 0.18d, 0.46d, 0.60d),
                    new NormalizedCrop(0.24d, 0.30d, 0.40d, 0.36d)
                ]),
            _ => new OcrProfile(
                "generic",
                1.8d,
                [
                    new NormalizedCrop(0.20d, 0.10d, 0.56d, 0.72d),
                    new NormalizedCrop(0.24d, 0.22d, 0.50d, 0.52d)
                ])
        };
    }

    internal static IReadOnlyList<(double X, double Y, double Width, double Height)> GetNormalizedCropRectanglesForTesting(string platformHint)
    {
        return ResolveProfile(platformHint)
            .CropRectangles
            .Select(crop => (crop.X, crop.Y, crop.Width, crop.Height))
            .ToArray();
    }

    internal static IReadOnlyList<string> GetPowerShellHostCandidatesForTesting()
    {
        return PowerShellHostCandidates;
    }

    internal static IReadOnlyList<string> GetPreferredOcrLanguageTagsForTesting(string platformHint)
    {
        return ResolvePreferredOcrLanguageTags(platformHint);
    }

    internal static int GetResultPriorityForTesting(string status)
    {
        return GetResultPriority(status);
    }

    private static Rectangle ToAbsoluteRectangle(int width, int height, NormalizedCrop crop)
    {
        var x = Math.Max(0, (int)Math.Round(width * crop.X));
        var y = Math.Max(0, (int)Math.Round(height * crop.Y));
        var cropWidth = Math.Min(width - x, Math.Max(1, (int)Math.Round(width * crop.Width)));
        var cropHeight = Math.Min(height - y, Math.Max(1, (int)Math.Round(height * crop.Height)));
        return new Rectangle(x, y, cropWidth, cropHeight);
    }

    private static Bitmap PrepareBitmapForOcr(Bitmap source, double scaleMultiplier)
    {
        var targetWidth = Math.Max(source.Width, (int)Math.Round(source.Width * scaleMultiplier));
        var targetHeight = Math.Max(source.Height, (int)Math.Round(source.Height * scaleMultiplier));
        var target = new Bitmap(targetWidth, targetHeight);
        using var graphics = Graphics.FromImage(target);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.Clear(Color.White);
        graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
        return target;
    }

    private static async Task<OcrReadResult> TryReadSingleImageDetailedAsync(string screenshotPath, string platformHint)
    {
        var bestResult = OcrReadResult.Failure("host_missing");
        foreach (var hostFileName in PowerShellHostCandidates)
        {
            var result = await TryReadSingleImageWithHostAsync(screenshotPath, platformHint, hostFileName).ConfigureAwait(false);
            if (result.Success)
            {
                return result;
            }

            bestResult = PickPreferredResult(bestResult, result);
        }

        return bestResult;
    }

    private static async Task<OcrReadResult> TryReadSingleImageWithHostAsync(string screenshotPath, string platformHint, string hostFileName)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = hostFileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        processStartInfo.ArgumentList.Add("-NoLogo");
        processStartInfo.ArgumentList.Add("-NoProfile");
        processStartInfo.ArgumentList.Add("-ExecutionPolicy");
        processStartInfo.ArgumentList.Add("Bypass");
        processStartInfo.ArgumentList.Add("-File");
        processStartInfo.ArgumentList.Add(ScriptPath.Value);
        processStartInfo.ArgumentList.Add(screenshotPath);
        processStartInfo.ArgumentList.Add(platformHint);

        using var process = new Process
        {
            StartInfo = processStartInfo
        };

        try
        {
            if (!process.Start())
            {
                return OcrReadResult.Failure("ocr_error", hostFileName);
            }
        }
        catch (Win32Exception)
        {
            return OcrReadResult.Failure("host_missing", hostFileName);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(OcrTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            return OcrReadResult.Failure("ocr_timeout", hostFileName);
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return OcrReadResult.Failure("ocr_error", hostFileName, NormalizeOutput(error));
        }

        var normalizedOutput = NormalizeOutput(output);
        return string.IsNullOrWhiteSpace(normalizedOutput)
            ? OcrReadResult.Failure("ocr_empty", hostFileName, NormalizeOutput(error))
            : OcrReadResult.SuccessResult(normalizedOutput, hostFileName, NormalizeOutput(error));
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static IReadOnlyList<string> ResolvePreferredOcrLanguageTags(string platformHint)
    {
        var languages = new List<string>();
        if (platformHint is "wecom" or "wechat" or "qianniu" or "jd_workbench" or "pdd_workbench")
        {
            languages.AddRange(PreferredChineseOcrLanguageTags);
        }

        languages.AddRange(FallbackLatinOcrLanguageTags);
        return languages
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildPowerShellScript()
    {
        var chineseLanguageList = string.Join(", ", PreferredChineseOcrLanguageTags.Select(tag => $"'{tag}'"));
        var latinLanguageList = string.Join(", ", FallbackLatinOcrLanguageTags.Select(tag => $"'{tag}'"));
        return $$"""
param([string]$ImagePath, [string]$PlatformHint = '')

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$StorageFile = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$FileAccessMode = [Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime]
$RandomAccessStream = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$BitmapDecoder = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$BitmapPixelFormat = [Windows.Graphics.Imaging.BitmapPixelFormat, Windows.Graphics, ContentType = WindowsRuntime]
$BitmapAlphaMode = [Windows.Graphics.Imaging.BitmapAlphaMode, Windows.Graphics, ContentType = WindowsRuntime]
$SoftwareBitmap = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType = WindowsRuntime]
$Language = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
$OcrEngine = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$OcrResult = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime]
$PreferredChineseLanguageTags = @({{chineseLanguageList}})
$FallbackLatinLanguageTags = @({{latinLanguageList}})

$asTaskGeneric = [System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.IsGenericMethodDefinition -and
        $_.GetGenericArguments().Count -eq 1 -and
        $_.GetParameters().Count -eq 1
    } |
    Select-Object -First 1

function AwaitGeneric([Type]$ResultType, [object]$AsyncOperation) {
    if ($null -eq $AsyncOperation) {
        return $null
    }

    $task = $asTaskGeneric.MakeGenericMethod($ResultType).Invoke($null, @($AsyncOperation))
    return $task.GetAwaiter().GetResult()
}

function TryCreateLanguageEngine([string]$LanguageTag) {
    if ([string]::IsNullOrWhiteSpace($LanguageTag)) {
        return $null
    }

    try {
        return $OcrEngine::TryCreateFromLanguage(($Language::new($LanguageTag)))
    }
    catch {
        return $null
    }
}

function GetPreferredLanguageTags([string]$PlatformHint) {
    $normalizedPlatform = $PlatformHint.Trim().ToLowerInvariant()
    if ($normalizedPlatform -in @('wecom', 'wechat', 'qianniu', 'jd_workbench', 'pdd_workbench')) {
        return @($PreferredChineseLanguageTags + $FallbackLatinLanguageTags)
    }

    return @($FallbackLatinLanguageTags)
}

function GetOcrEngine([string]$PlatformHint) {
    foreach ($languageTag in GetPreferredLanguageTags $PlatformHint) {
        $engine = TryCreateLanguageEngine $languageTag
        if ($null -ne $engine) {
            return $engine
        }
    }

    try {
        $engine = $OcrEngine::TryCreateFromUserProfileLanguages()
        if ($null -ne $engine) {
            return $engine
        }
    }
    catch {
    }

    return $null
}

try {
    if ([string]::IsNullOrWhiteSpace($ImagePath) -or -not (Test-Path -LiteralPath $ImagePath)) {
        exit 0
    }

    $file = AwaitGeneric $StorageFile ($StorageFile::GetFileFromPathAsync($ImagePath))
    $stream = AwaitGeneric $RandomAccessStream ($file.OpenAsync($FileAccessMode::Read))
    $decoder = AwaitGeneric $BitmapDecoder ($BitmapDecoder::CreateAsync($stream))
    $bitmap = AwaitGeneric $SoftwareBitmap ($decoder.GetSoftwareBitmapAsync($BitmapPixelFormat::Bgra8, $BitmapAlphaMode::Premultiplied))
    $engine = GetOcrEngine $PlatformHint

    if ($null -eq $engine) {
        exit 0
    }

    $result = AwaitGeneric $OcrResult ($engine.RecognizeAsync($bitmap))
    @($result.Lines |
        ForEach-Object { $_.Text } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
}
catch {
    Write-Error $_
    exit 1
}
""";
    }

    private sealed record NormalizedCrop(double X, double Y, double Width, double Height);

    private sealed record OcrProfile(string Name, double ScaleMultiplier, IReadOnlyList<NormalizedCrop> CropRectangles);

    internal readonly record struct OcrReadResult(
        bool Success,
        string Status,
        string Text,
        string HostFileName,
        string Detail)
    {
        public static OcrReadResult SuccessResult(string text, string hostFileName, string detail = "")
        {
            return new OcrReadResult(true, "ocr_read", text, hostFileName, detail);
        }

        public static OcrReadResult Failure(string status, string hostFileName = "", string detail = "")
        {
            return new OcrReadResult(false, status, string.Empty, hostFileName, detail);
        }
    }

    private static OcrReadResult PickPreferredResult(OcrReadResult current, OcrReadResult candidate)
    {
        if (!current.Success && candidate.Success)
        {
            return candidate;
        }

        if (current.Success && !candidate.Success)
        {
            return current;
        }

        return GetResultPriority(candidate.Status) >= GetResultPriority(current.Status)
            ? candidate
            : current;
    }

    private static int GetResultPriority(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "ocr_read" => 5,
            "ocr_error" => 4,
            "ocr_timeout" => 3,
            "ocr_empty" => 2,
            "host_missing" => 1,
            "missing_image" => 0,
            _ => 0
        };
    }
}
