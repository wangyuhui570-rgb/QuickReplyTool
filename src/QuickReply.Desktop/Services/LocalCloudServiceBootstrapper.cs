using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace QuickReply.Desktop.Services;

internal static class LocalCloudServiceBootstrapper
{
    public const string DefaultLocalServiceBaseUrl = "http://127.0.0.1:5128";
    private const string PackagedServiceFolderName = "LocalCloudService";
    private const string PackagedServiceExeName = "QuickReply.CloudService.exe";
    private const string PackagedServiceDllName = "QuickReply.CloudService.dll";

    public static async Task<bool> EnsureLocalServiceRunningAsync(CancellationToken cancellationToken)
    {
        if (await IsLocalServiceHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var launch = ResolveLocalServiceLaunchCommand();
        if (launch is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launch.Value.FileName,
            Arguments = launch.Value.Arguments,
            WorkingDirectory = launch.Value.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(startInfo);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            if (await IsLocalServiceHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<bool> IsLocalServiceHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient(SharedHttpMessageHandler.Instance, disposeHandler: false)
            {
                BaseAddress = new Uri($"{DefaultLocalServiceBaseUrl}/", UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(2)
            };
            using var response = await client.GetAsync("/healthz", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static (string FileName, string Arguments, string WorkingDirectory)? ResolveLocalServiceLaunchCommand()
    {
        return ResolveLocalServiceLaunchCommand(AppContext.BaseDirectory);
    }

    internal static (string FileName, string Arguments, string WorkingDirectory)? ResolveLocalServiceLaunchCommand(string appBaseDirectory)
    {
        var appBase = string.IsNullOrWhiteSpace(appBaseDirectory)
            ? AppContext.BaseDirectory
            : appBaseDirectory;

        var packagedServiceDirectory = Path.GetFullPath(Path.Combine(appBase, PackagedServiceFolderName));
        var packagedServiceExePath = Path.Combine(packagedServiceDirectory, PackagedServiceExeName);
        if (File.Exists(packagedServiceExePath))
        {
            return (
                packagedServiceExePath,
                $"--urls {DefaultLocalServiceBaseUrl}",
                packagedServiceDirectory);
        }

        var packagedServiceDllPath = Path.Combine(packagedServiceDirectory, PackagedServiceDllName);
        if (File.Exists(packagedServiceDllPath))
        {
            return (
                "dotnet",
                $"\"{packagedServiceDllPath}\" --urls {DefaultLocalServiceBaseUrl}",
                packagedServiceDirectory);
        }

        var candidateCsprojPaths = new[]
        {
            Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "QuickReply.CloudService", "QuickReply.CloudService.csproj")),
            Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "..", "src", "QuickReply.CloudService", "QuickReply.CloudService.csproj"))
        };

        foreach (var csprojPath in candidateCsprojPaths)
        {
            if (File.Exists(csprojPath))
            {
                return (
                    "dotnet",
                    $"run --project \"{csprojPath}\" --no-build --urls {DefaultLocalServiceBaseUrl}",
                    Path.GetDirectoryName(csprojPath)!);
            }
        }

        return null;
    }
}
