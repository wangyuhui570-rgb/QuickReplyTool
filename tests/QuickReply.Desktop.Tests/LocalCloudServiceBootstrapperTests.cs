using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class LocalCloudServiceBootstrapperTests
{
    [Fact]
    public void ResolveLocalServiceLaunchCommand_PrefersPackagedExecutable()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var appBaseDirectory = Path.Combine(tempRoot, "app");
            var packagedServiceDirectory = Path.Combine(appBaseDirectory, "LocalCloudService");
            Directory.CreateDirectory(packagedServiceDirectory);
            File.WriteAllText(Path.Combine(packagedServiceDirectory, "QuickReply.CloudService.exe"), string.Empty);

            var launch = LocalCloudServiceBootstrapper.ResolveLocalServiceLaunchCommand(appBaseDirectory);

            Assert.NotNull(launch);
            Assert.Equal(Path.Combine(packagedServiceDirectory, "QuickReply.CloudService.exe"), launch.Value.FileName);
            Assert.Equal("--urls http://127.0.0.1:5128", launch.Value.Arguments);
            Assert.Equal(packagedServiceDirectory, launch.Value.WorkingDirectory);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public void ResolveLocalServiceLaunchCommand_FallsBackToPackagedDll()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var appBaseDirectory = Path.Combine(tempRoot, "app");
            var packagedServiceDirectory = Path.Combine(appBaseDirectory, "LocalCloudService");
            Directory.CreateDirectory(packagedServiceDirectory);
            var dllPath = Path.Combine(packagedServiceDirectory, "QuickReply.CloudService.dll");
            File.WriteAllText(dllPath, string.Empty);

            var launch = LocalCloudServiceBootstrapper.ResolveLocalServiceLaunchCommand(appBaseDirectory);

            Assert.NotNull(launch);
            Assert.Equal("dotnet", launch.Value.FileName);
            Assert.Contains(dllPath, launch.Value.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--urls http://127.0.0.1:5128", launch.Value.Arguments, StringComparison.Ordinal);
            Assert.Equal(packagedServiceDirectory, launch.Value.WorkingDirectory);
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "QuickReplyToolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore cleanup failures in tests
        }
    }
}
