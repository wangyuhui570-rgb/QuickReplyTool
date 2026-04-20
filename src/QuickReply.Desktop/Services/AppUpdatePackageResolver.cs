using System.IO;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal enum AppUpdatePackageKind
{
    Unknown = 0,
    ZipOta,
    InstallerExe,
    ExternalUrl
}

internal readonly record struct AppUpdatePackageDescriptor(
    AppUpdatePackageKind Kind,
    string PackageKindText,
    string DownloadUrl,
    string ExpectedSha256,
    long? ExpectedSizeBytes,
    string ReleaseChannel,
    bool Mandatory)
{
    public bool CanSelfApply => Kind == AppUpdatePackageKind.ZipOta && !string.IsNullOrWhiteSpace(DownloadUrl);
}

internal static class AppUpdatePackageResolver
{
    public const string ZipOtaKind = "zip_ota";
    public const string InstallerExeKind = "installer_exe";
    public const string ExternalUrlKind = "external_url";

    public static AppUpdatePackageDescriptor Resolve(CloudUpdateManifest manifest)
    {
        var downloadUrl = manifest.LatestAppDownloadUrl?.Trim() ?? string.Empty;
        var packageKindText = manifest.LatestAppPackageKind?.Trim() ?? string.Empty;
        var kind = ResolveKind(packageKindText, downloadUrl);
        var expectedSha256 = NormalizeSha256(manifest.LatestAppSha256);
        var expectedSizeBytes = manifest.LatestAppSizeBytes > 0
            ? manifest.LatestAppSizeBytes
            : null;

        return new AppUpdatePackageDescriptor(
            kind,
            packageKindText,
            downloadUrl,
            expectedSha256,
            expectedSizeBytes,
            manifest.LatestAppReleaseChannel?.Trim() ?? string.Empty,
            manifest.LatestAppMandatory);
    }

    public static AppUpdatePackageKind ResolveKind(string? packageKindText, string? downloadUrl)
    {
        var normalizedKind = NormalizePackageKind(packageKindText);
        if (!string.IsNullOrWhiteSpace(normalizedKind))
        {
            return normalizedKind switch
            {
                ZipOtaKind => AppUpdatePackageKind.ZipOta,
                InstallerExeKind => AppUpdatePackageKind.InstallerExe,
                ExternalUrlKind => AppUpdatePackageKind.ExternalUrl,
                _ => AppUpdatePackageKind.Unknown
            };
        }

        if (!Uri.TryCreate(downloadUrl?.Trim(), UriKind.Absolute, out var packageUri))
        {
            return AppUpdatePackageKind.Unknown;
        }

        var extension = Path.GetExtension(packageUri.AbsolutePath).ToLowerInvariant();
        return extension switch
        {
            ".zip" => AppUpdatePackageKind.ZipOta,
            ".exe" => AppUpdatePackageKind.InstallerExe,
            ".msi" => AppUpdatePackageKind.InstallerExe,
            _ => AppUpdatePackageKind.ExternalUrl
        };
    }

    public static bool IsZipOta(string? packageKindText, string? downloadUrl)
    {
        return ResolveKind(packageKindText, downloadUrl) == AppUpdatePackageKind.ZipOta;
    }

    private static string NormalizePackageKind(string? packageKindText)
    {
        return packageKindText?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string NormalizeSha256(string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return string.Empty;
        }

        return new string(expectedSha256
            .Where(Uri.IsHexDigit)
            .ToArray())
            .ToUpperInvariant();
    }
}
