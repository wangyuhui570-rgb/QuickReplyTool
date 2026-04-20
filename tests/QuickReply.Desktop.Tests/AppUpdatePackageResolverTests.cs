using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class AppUpdatePackageResolverTests
{
    [Fact]
    public void Resolve_PrefersExplicitZipOtaKind()
    {
        var descriptor = AppUpdatePackageResolver.Resolve(new CloudUpdateManifest
        {
            LatestAppDownloadUrl = "https://updates.example.com/download?id=2.2.0",
            LatestAppPackageKind = "zip_ota",
            LatestAppSha256 = "aa-bb cc",
            LatestAppSizeBytes = 123456,
            LatestAppReleaseChannel = "stable",
            LatestAppMandatory = false
        });

        Assert.Equal(AppUpdatePackageKind.ZipOta, descriptor.Kind);
        Assert.True(descriptor.CanSelfApply);
        Assert.Equal("AABBCC", descriptor.ExpectedSha256);
        Assert.Equal(123456, descriptor.ExpectedSizeBytes);
        Assert.Equal("stable", descriptor.ReleaseChannel);
    }

    [Fact]
    public void Resolve_FallsBackToZipExtensionWhenPackageKindMissing()
    {
        var descriptor = AppUpdatePackageResolver.Resolve(new CloudUpdateManifest
        {
            LatestAppDownloadUrl = "https://updates.example.com/QuickReplyTool-AppUpdate-v2.2.0.zip"
        });

        Assert.Equal(AppUpdatePackageKind.ZipOta, descriptor.Kind);
        Assert.True(descriptor.CanSelfApply);
    }

    [Fact]
    public void Resolve_DetectsInstallerExecutableWhenPackageKindMissing()
    {
        var descriptor = AppUpdatePackageResolver.Resolve(new CloudUpdateManifest
        {
            LatestAppDownloadUrl = "https://updates.example.com/QuickReplyTool-Setup-v2.2.0.exe"
        });

        Assert.Equal(AppUpdatePackageKind.InstallerExe, descriptor.Kind);
        Assert.False(descriptor.CanSelfApply);
    }

    [Fact]
    public void Resolve_TreatsOtherAbsoluteUrlsAsExternal()
    {
        var descriptor = AppUpdatePackageResolver.Resolve(new CloudUpdateManifest
        {
            LatestAppDownloadUrl = "https://updates.example.com/releases/2.2.0"
        });

        Assert.Equal(AppUpdatePackageKind.ExternalUrl, descriptor.Kind);
        Assert.False(descriptor.CanSelfApply);
    }
}
