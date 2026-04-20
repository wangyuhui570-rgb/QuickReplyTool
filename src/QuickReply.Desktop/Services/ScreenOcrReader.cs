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
    private static readonly string[] PreferredFallbackOcrLanguageTags =
    [
        "zh-Hans-CN",
        "zh-CN",
        "zh-Hans",
        "zh-Hant",
        "en-US"
    ];
    private static readonly Lazy<string> ScriptPath = new(EnsureScriptPath, LazyThreadSafetyMode.ExecutionAndPublication);

    public static async Task<string> TryReadTextAsync(string screenshotPath, string platformHint = "")
    {
        if (string.IsNullOrWhiteSpace(screenshotPath) || !File.Exists(screenshotPath))
        {
            return string.Empty;
        }

        var generatedVariantPaths = new List<string>();
        try
        {
            var mergedLines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var imagePath in BuildScanPlan(screenshotPath, platformHint, generatedVariantPaths))
            {
                var output = await TryReadSingleImageAsync(imagePath).ConfigureAwait(false);
                foreach (var line in SplitNormalizedLines(output))
                {
                    if (seen.Add(line))
                    {
                        mergedLines.Add(line);
                    }
                }
            }

            return string.Join(Environment.NewLine, mergedLines);
        }
        catch
        {
            return string.Empty;
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
                    new NormalizedCrop(0.34d, 0.18d, 0.44d, 0.60d),
                    new NormalizedCrop(0.34d, 0.26d, 0.44d, 0.42d)
                ]),
            "qianniu" or "jd_workbench" or "pdd_workbench" => new OcrProfile(
                "workbench",
                2.2d,
                [
                    new NormalizedCrop(0.22d, 0.18d, 0.43d, 0.62d),
                    new NormalizedCrop(0.24d, 0.28d, 0.39d, 0.38d)
                ]),
            _ => new OcrProfile(
                "generic",
                1.8d,
                [
                    new NormalizedCrop(0.24d, 0.18d, 0.52d, 0.60d)
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

    internal static IReadOnlyList<string> GetPreferredFallbackOcrLanguageTagsForTesting()
    {
        return PreferredFallbackOcrLanguageTags;
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

    private static async Task<string> TryReadSingleImageAsync(string screenshotPath)
    {
        foreach (var hostFileName in PowerShellHostCandidates)
        {
            var result = await TryReadSingleImageWithHostAsync(screenshotPath, hostFileName).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return string.Empty;
    }

    private static async Task<string?> TryReadSingleImageWithHostAsync(string screenshotPath, string hostFileName)
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

        using var process = new Process
        {
            StartInfo = processStartInfo
        };

        try
        {
            if (!process.Start())
            {
                return string.Empty;
            }
        }
        catch (Win32Exception)
        {
            return null;
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
            return string.Empty;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
        {
            return string.Empty;
        }

        return NormalizeOutput(output);
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

    private static string BuildPowerShellScript()
    {
        var fallbackLanguageList = string.Join(", ", PreferredFallbackOcrLanguageTags.Select(tag => $"'{tag}'"));
        return $$"""
param([string]$ImagePath)

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

function GetOcrEngine() {
    try {
        $engine = $OcrEngine::TryCreateFromUserProfileLanguages()
        if ($null -ne $engine) {
            return $engine
        }
    }
    catch {
    }

    foreach ($languageTag in @({{fallbackLanguageList}})) {
        $engine = TryCreateLanguageEngine $languageTag
        if ($null -ne $engine) {
            return $engine
        }
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
    $engine = GetOcrEngine

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
}
