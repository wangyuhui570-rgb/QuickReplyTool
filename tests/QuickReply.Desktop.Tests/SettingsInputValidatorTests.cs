using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SettingsInputValidatorTests
{
    [Fact]
    public void ValidateSettings_ReturnsErrorsForBrokenPaths()
    {
        var settings = new AppSettings
        {
            EnableCloudSync = true,
            ServerBaseUrl = "",
            FileLibraryFolderPath = "relative\\kb",
            PhraseImageFolderPath = "images"
        };

        var errors = SettingsInputValidator.ValidateSettings(settings);

        Assert.Contains("资料文件夹需要填写完整的本地文件夹路径。", errors);
        Assert.Contains("短语图片目录需要填写完整的本地文件夹路径。", errors);
    }

    [Fact]
    public void ValidateSettings_AllowsUpdateChecksWithoutCustomServerAddress()
    {
        var settings = new AppSettings
        {
            EnableUpdateChecks = true,
            ServerBaseUrl = string.Empty
        };

        var errors = SettingsInputValidator.ValidateSettings(settings);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateSettings_FallsBackToBuiltInServerForUnsupportedScheme()
    {
        var settings = new AppSettings
        {
            EnableUpdateChecks = true,
            ServerBaseUrl = "ftp://updates.example.com/"
        };

        var errors = SettingsInputValidator.ValidateSettings(settings);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateSettings_FallsBackToBuiltInServerForRemoteHttpAddress()
    {
        var settings = new AppSettings
        {
            EnableCloudSync = true,
            ServerBaseUrl = "http://sync.example.com/"
        };

        var errors = SettingsInputValidator.ValidateSettings(settings);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateSettings_AcceptsValidDirectoriesAndHttpsServer()
    {
        var existingFolder = Path.Combine(Path.GetTempPath(), $"quickreply-validator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(existingFolder);

        try
        {
            var settings = new AppSettings
            {
                EnableCloudSync = true,
                EnableUpdateChecks = true,
                ServerBaseUrl = "https://sync.example.com/",
                FileLibraryFolderPath = existingFolder,
                PhraseImageFolderPath = Path.Combine(existingFolder, "phrase-images"),
                ScreenshotToolSaveDirectory = Path.Combine(existingFolder, "captures"),
                IssueReportDirectory = Path.Combine(existingFolder, "feedback")
            };

            var errors = SettingsInputValidator.ValidateSettings(settings);

            Assert.Empty(errors);
        }
        finally
        {
            Directory.Delete(existingFolder, recursive: true);
        }
    }

    [Fact]
    public void ValidateFirstRunFileLibraryFolder_RequiresExistingAbsoluteFolder()
    {
        var error = SettingsInputValidator.ValidateFirstRunFileLibraryFolder("relative\\kb");

        Assert.Equal("资料文件夹需要填写完整的本地文件夹路径。", error);
    }

    [Fact]
    public void ResolveConfiguredServerBaseUrl_UsesBuiltInDefaultWhenEmpty()
    {
        var resolved = SettingsInputValidator.ResolveConfiguredServerBaseUrl(string.Empty);

        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void ResolveOperationalServerBaseUrl_FallsBackToBuiltInDefaultWhenConfiguredAddressIsInvalid()
    {
        var resolved = SettingsInputValidator.ResolveOperationalServerBaseUrl("ftp://sync.example.com/");

        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void GetCloudActionAvailabilitySummary_StatesLocalOnly()
    {
        var summary = SettingsInputValidator.GetCloudActionAvailabilitySummary("ftp://sync.example.com/");
        Assert.Contains("本地工具", summary, StringComparison.Ordinal);
    }


}
