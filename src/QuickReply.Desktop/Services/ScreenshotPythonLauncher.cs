using System.Diagnostics;
using System.IO;

namespace QuickReply.Desktop.Services;

public readonly record struct PythonCandidate(string Path, string Arguments);

/// <summary>Which Python the screenshot tool runs on, and what it lacks when none is complete.</summary>
public sealed record PythonLauncherSelection(PythonCandidate Candidate, IReadOnlyList<string> MissingModules)
{
    public bool IsComplete => MissingModules.Count == 0;
}

/// <summary>
/// Picks a Python that can actually run lite_shot.py. Taking the first pythonw.exe found broke the
/// screenshot tool when a newer Python (e.g. 3.14) was installed next to the one holding PySide6,
/// Pillow and pywin32: the new one sorted first, lacked pywin32 and the tool exited at import.
/// </summary>
public static class ScreenshotPythonLauncher
{
    /// <summary>Modules lite_shot.py imports at startup, mapped to the pip package that provides each.</summary>
    public static readonly IReadOnlyList<(string Module, string Package)> RequiredModules =
    [
        ("PySide6", "PySide6"),
        ("PIL", "Pillow"),
        ("win32gui", "pywin32"),
        ("win32con", "pywin32"),
        ("win32process", "pywin32")
    ];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <param name="probe">Returns the missing modules for a candidate, or null when it could not be run.</param>
    public static PythonLauncherSelection? Select(
        IEnumerable<PythonCandidate> candidates,
        Func<PythonCandidate, IReadOnlyList<string>?> probe)
    {
        PythonLauncherSelection? fallback = null;
        foreach (var candidate in candidates)
        {
            var missing = probe(candidate);
            if (missing is null)
            {
                continue;
            }

            if (missing.Count == 0)
            {
                return new PythonLauncherSelection(candidate, missing);
            }

            // Keep the first runnable one so the user is told what to install into it.
            fallback ??= new PythonLauncherSelection(candidate, missing);
        }

        return fallback;
    }

    public static IReadOnlyList<string> PackagesFor(IEnumerable<string> missingModules) =>
        missingModules
            .Select(module => RequiredModules.FirstOrDefault(entry => entry.Module == module).Package ?? module)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string BuildMissingModulesMessage(PythonLauncherSelection selection)
    {
        var packages = string.Join(" ", PackagesFor(selection.MissingModules));
        var python = ConsolePythonFor(selection.Candidate.Path);
        return $"截图工具缺少 Python 依赖 {packages}，Alt+A 截图暂不可用。请在命令行运行：\"{python}\" -m pip install {packages}，装完重启本工具。";
    }

    /// <summary>pythonw.exe / pyw.exe have no console; point the user at python.exe / py.exe next to them.</summary>
    public static string ConsolePythonFor(string launcherPath)
    {
        var directory = Path.GetDirectoryName(launcherPath) ?? string.Empty;
        var name = Path.GetFileName(launcherPath);
        if (name.Equals("pythonw.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(directory, "python.exe");
        }

        return name.Equals("pyw.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(directory, "py.exe")
            : launcherPath;
    }

    /// <summary>Runs the candidate and asks which required modules it cannot find (without importing them).</summary>
    public static IReadOnlyList<string>? ProbeMissingModules(PythonCandidate candidate)
    {
        var modules = string.Join(",", RequiredModules.Select(entry => $"'{entry.Module}'"));
        var code = $"import importlib.util as u,sys;m=[n for n in ({modules}) if u.find_spec(n) is None];sys.stdout.write(','.join(m));sys.exit(0)";
        var startInfo = new ProcessStartInfo
        {
            FileName = ConsolePythonFor(candidate.Path) is var console && File.Exists(console) ? console : candidate.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (!string.IsNullOrWhiteSpace(candidate.Arguments))
        {
            startInfo.ArgumentList.Add(candidate.Arguments);
        }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            return ParseProbeOutput(stdout.GetAwaiter().GetResult());
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> ParseProbeOutput(string? output) =>
        (output ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
