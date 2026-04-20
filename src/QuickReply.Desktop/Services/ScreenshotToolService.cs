using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace QuickReply.Desktop.Services;

public readonly record struct ScreenshotToolConfiguration(bool Enabled, string Hotkey, string SaveDirectory);

public readonly record struct ScreenshotToolStateChangeResult(bool Success, string Message);

public sealed class ScreenshotToolService : IDisposable
{
    private const int SingleInstancePort = 47625;
    private static readonly TimeSpan StartupWaitTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(3);

    private readonly object _syncRoot = new();
    private Process? _managedProcess;
    private bool _startedByApp;
    private bool _disposed;

    public ScreenshotToolStateChangeResult ApplyConfiguration(ScreenshotToolConfiguration configuration)
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            var normalizedConfiguration = NormalizeConfiguration(configuration);
            var settingsWriteResult = TryWriteSettings(normalizedConfiguration);
            if (!settingsWriteResult.Success)
            {
                return settingsWriteResult;
            }

            if (!normalizedConfiguration.Enabled)
            {
                return StopCore(forceStop: true, appExit: false);
            }

            if (IsInstanceResponsive())
            {
                if (TryRequestReload())
                {
                    _startedByApp = true;
                    return new ScreenshotToolStateChangeResult(true, string.Empty);
                }

                var restartResult = RestartCore();
                if (restartResult.Success)
                {
                    return restartResult;
                }

                _startedByApp = true;
                return new ScreenshotToolStateChangeResult(false, "截图工具正在运行，但重新加载快捷键失败。");
            }

            return EnsureStartedCore();
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ = StopCore(forceStop: false, appExit: true);
        }
    }

    private ScreenshotToolStateChangeResult EnsureStartedCore()
    {
        if (IsInstanceResponsive())
        {
            return new ScreenshotToolStateChangeResult(true, string.Empty);
        }

        var scriptPath = ResolveScriptPath();
        if (scriptPath is null)
        {
            return new ScreenshotToolStateChangeResult(false, "未找到截图工具脚本，无法自动启动截图功能。");
        }

        if (!TryResolvePythonLauncher(out var launcherPath, out var launcherArguments))
        {
            return new ScreenshotToolStateChangeResult(false, "未找到可用的 Python 启动器，无法启动截图工具。");
        }

        var arguments = string.IsNullOrWhiteSpace(launcherArguments)
            ? $"{QuoteArgument(scriptPath)} --managed"
            : $"{launcherArguments} {QuoteArgument(scriptPath)} --managed";

        var startInfo = new ProcessStartInfo
        {
            FileName = launcherPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            CleanupManagedProcess();
            _managedProcess = Process.Start(startInfo);
            _startedByApp = true;
        }
        catch (Exception ex)
        {
            CleanupManagedProcess();
            _startedByApp = false;
            return new ScreenshotToolStateChangeResult(false, $"启动截图工具失败：{ex.Message}");
        }

        if (WaitUntil(IsInstanceResponsive, StartupWaitTimeout))
        {
            return new ScreenshotToolStateChangeResult(true, string.Empty);
        }

        var message = _managedProcess is { HasExited: true }
            ? $"截图工具启动后立即退出，退出码 {_managedProcess.ExitCode}。"
            : "截图工具未在预期时间内完成启动。";

        CleanupManagedProcess();
        _startedByApp = false;
        return new ScreenshotToolStateChangeResult(false, message);
    }

    private static ScreenshotToolStateChangeResult TryWriteSettings(ScreenshotToolConfiguration configuration)
    {
        var targetPath = GetSettingsPath();
        var targetDirectory = Path.GetDirectoryName(targetPath);

        try
        {
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var payload = new
            {
                hotkey = configuration.Hotkey,
                save_dir = configuration.SaveDirectory
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(targetPath, json, Encoding.UTF8);
            return new ScreenshotToolStateChangeResult(true, string.Empty);
        }
        catch (Exception ex)
        {
            return new ScreenshotToolStateChangeResult(false, $"写入截图工具设置失败：{ex.Message}");
        }
    }

    internal static ScreenshotToolConfiguration NormalizeConfiguration(ScreenshotToolConfiguration configuration)
    {
        return new ScreenshotToolConfiguration(
            configuration.Enabled,
            ScreenshotHotkeyBinding.NormalizeOrDefault(configuration.Hotkey),
            string.IsNullOrWhiteSpace(configuration.SaveDirectory) ? GetDefaultSaveDirectory() : configuration.SaveDirectory.Trim());
    }

    private ScreenshotToolStateChangeResult StopCore(bool forceStop, bool appExit)
    {
        if (appExit && !_startedByApp)
        {
            CleanupManagedProcess();
            return new ScreenshotToolStateChangeResult(true, string.Empty);
        }

        if (!IsInstanceResponsive())
        {
            CleanupManagedProcess();
            _startedByApp = false;
            return new ScreenshotToolStateChangeResult(true, string.Empty);
        }

        TryRequestShutdown();

        if (!WaitUntil(() => !IsInstanceResponsive(), ShutdownWaitTimeout) && forceStop && _managedProcess is { HasExited: false })
        {
            try
            {
                _managedProcess.Kill(entireProcessTree: true);
                _managedProcess.WaitForExit(1000);
            }
            catch
            {
            }
        }

        var success = !IsInstanceResponsive();
        CleanupManagedProcess();
        _startedByApp = false;

        return success
            ? new ScreenshotToolStateChangeResult(true, string.Empty)
            : new ScreenshotToolStateChangeResult(false, "截图工具未能按预期退出。");
    }

    private ScreenshotToolStateChangeResult RestartCore()
    {
        var stopResult = StopCore(forceStop: true, appExit: false);
        if (!stopResult.Success)
        {
            return stopResult;
        }

        return EnsureStartedCore();
    }

    private static string? ResolveScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ScreenshotTool", "lite_shot.py"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "截图工具源码", "lite_shot.py"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickReplyTool",
            "ScreenshotTool",
            "lite_shot_settings.json");
    }

    private static string GetDefaultSaveDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            return desktop;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static bool TryResolvePythonLauncher(out string path, out string arguments)
    {
        foreach (var candidate in EnumeratePythonCandidates())
        {
            if (!File.Exists(candidate.Path))
            {
                continue;
            }

            path = candidate.Path;
            arguments = candidate.Arguments;
            return true;
        }

        path = string.Empty;
        arguments = string.Empty;
        return false;
    }

    private static IEnumerable<(string Path, string Arguments)> EnumeratePythonCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pythonRoot = Path.Combine(localAppData, "Programs", "Python");
        if (Directory.Exists(pythonRoot))
        {
            foreach (var pythonwPath in Directory.EnumerateFiles(pythonRoot, "pythonw.exe", SearchOption.AllDirectories).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return (pythonwPath, string.Empty);
            }

            foreach (var pythonPath in Directory.EnumerateFiles(pythonRoot, "python.exe", SearchOption.AllDirectories).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return (pythonPath, string.Empty);
            }

            foreach (var pywPath in Directory.EnumerateFiles(pythonRoot, "pyw.exe", SearchOption.AllDirectories).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return (pywPath, "-3");
            }

            foreach (var pyPath in Directory.EnumerateFiles(pythonRoot, "py.exe", SearchOption.AllDirectories).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return (pyPath, "-3");
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pythonw = Path.Combine(directory, "pythonw.exe");
            if (File.Exists(pythonw))
            {
                yield return (pythonw, string.Empty);
            }

            var python = Path.Combine(directory, "python.exe");
            if (File.Exists(python))
            {
                yield return (python, string.Empty);
            }

            var pyw = Path.Combine(directory, "pyw.exe");
            if (File.Exists(pyw))
            {
                yield return (pyw, "-3");
            }

            var py = Path.Combine(directory, "py.exe");
            if (File.Exists(py))
            {
                yield return (py, "-3");
            }
        }
    }

    private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(120);
        }

        return predicate();
    }

    private static bool IsInstanceResponsive()
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", SingleInstancePort);
            if (!connectTask.Wait(TimeSpan.FromMilliseconds(250)))
            {
                return false;
            }

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void TryRequestShutdown()
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", SingleInstancePort);
            if (!connectTask.Wait(TimeSpan.FromMilliseconds(400)) || !client.Connected)
            {
                return;
            }

            using var stream = client.GetStream();
            var payload = Encoding.ASCII.GetBytes("shutdown");
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
        catch
        {
        }
    }

    private static bool TryRequestReload()
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", SingleInstancePort);
            if (!connectTask.Wait(TimeSpan.FromMilliseconds(400)) || !client.Connected)
            {
                return false;
            }

            using var stream = client.GetStream();
            stream.ReadTimeout = 600;
            var payload = Encoding.ASCII.GetBytes("reload");
            stream.Write(payload, 0, payload.Length);
            stream.Flush();

            var buffer = new byte[32];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                return false;
            }

            var response = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim();
            return string.Equals(response, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void CleanupManagedProcess()
    {
        try
        {
            _managedProcess?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _managedProcess = null;
        }
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains('"') || value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ScreenshotToolService));
        }
    }
}
