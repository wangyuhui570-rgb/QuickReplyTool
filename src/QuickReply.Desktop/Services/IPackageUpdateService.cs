namespace QuickReply.Desktop.Services;

internal interface IPackageUpdateService
{
    bool CanSelfApplyPackage(string packageUrl, string? packageKind = null);

    Task<PackageUpdateLaunchResult> DownloadAndLaunchAsync(
        string packageUrl,
        string versionLabel,
        string sourceKind,
        string? expectedSha256 = null,
        long? expectedSizeBytes = null,
        string? packageKind = null,
        CancellationToken cancellationToken = default);

    PackageUpdateStatus? ConsumeLastStatus();
}
