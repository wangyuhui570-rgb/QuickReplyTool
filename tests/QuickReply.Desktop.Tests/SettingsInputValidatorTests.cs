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

    [Theory]
    [InlineData("https://sync.example.com/", true)]
    [InlineData("http://sync.example.com/api", true)]
    [InlineData("http://localhost:5128/", true)]
    [InlineData("http://127.0.0.1:5128/", true)]
    [InlineData("ftp://sync.example.com/", true)]
    [InlineData("not-a-url", true)]
    [InlineData("", true)]
    public void CanRunCloudSyncActions_RequiresHttpsOrLocalHttpBaseUrl(string serverBaseUrl, bool expected)
    {
        var canRun = SettingsInputValidator.CanRunCloudSyncActions(serverBaseUrl);

        Assert.Equal(expected, canRun);
    }

    [Fact]
    public void ResolveConfiguredServerBaseUrl_UsesBuiltInDefaultWhenEmpty()
    {
        var resolved = SettingsInputValidator.ResolveConfiguredServerBaseUrl(string.Empty);

        Assert.Equal(QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl, resolved);
    }

    [Fact]
    public void ResolveOperationalServerBaseUrl_FallsBackToBuiltInDefaultWhenConfiguredAddressIsInvalid()
    {
        var resolved = SettingsInputValidator.ResolveOperationalServerBaseUrl("ftp://sync.example.com/");

        Assert.Equal(QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl, resolved);
    }

    [Fact]
    public void GetCloudActionAvailabilitySummary_ExplainsFallbackWhenConfiguredAddressIsInvalid()
    {
        var summary = SettingsInputValidator.GetCloudActionAvailabilitySummary("ftp://sync.example.com/");

        Assert.Contains("已自动回退到内置服务地址", summary, StringComparison.Ordinal);
        Assert.Contains(QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl, summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://sync.example.com/", true)]
    [InlineData("", true)]
    [InlineData("ftp://sync.example.com/", false)]
    [InlineData("not-a-url", false)]
    public void CanRunUpdateActions_AllowsDefaultUpdateServerWhenEmpty(string serverBaseUrl, bool expected)
    {
        var canRun = SettingsInputValidator.CanRunUpdateActions(serverBaseUrl);

        Assert.Equal(expected, canRun);
    }
}
