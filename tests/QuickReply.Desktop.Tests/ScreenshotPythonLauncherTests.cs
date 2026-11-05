using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class ScreenshotPythonLauncherTests
{
    private static readonly PythonCandidate Python314 = new(@"C:\Py\Python314\pythonw.exe", string.Empty);
    private static readonly PythonCandidate Python312 = new(@"C:\Py\Python312\pythonw.exe", string.Empty);

    [Fact]
    public void Select_SkipsANewerPythonThatLacksTheDependencies()
    {
        var probed = new List<PythonCandidate>();
        var selection = ScreenshotPythonLauncher.Select(
            [Python314, Python312],
            candidate =>
            {
                probed.Add(candidate);
                return candidate == Python314 ? ["win32gui", "win32con", "win32process"] : [];
            });

        Assert.NotNull(selection);
        Assert.Equal(Python312, selection!.Candidate);
        Assert.True(selection.IsComplete);
        Assert.Equal([Python314, Python312], probed);
    }

    [Fact]
    public void Select_StopsAtTheFirstCompletePython()
    {
        var probed = new List<PythonCandidate>();
        var selection = ScreenshotPythonLauncher.Select(
            [Python314, Python312],
            candidate =>
            {
                probed.Add(candidate);
                return [];
            });

        Assert.Equal(Python314, selection!.Candidate);
        Assert.Equal([Python314], probed);
    }

    [Fact]
    public void Select_WhenNoneIsComplete_ReportsTheFirstRunnableOneAndWhatItLacks()
    {
        var selection = ScreenshotPythonLauncher.Select(
            [Python314, Python312],
            candidate => candidate == Python314 ? null : ["win32gui"]);

        Assert.NotNull(selection);
        Assert.Equal(Python312, selection!.Candidate);
        Assert.False(selection.IsComplete);
    }

    [Fact]
    public void Select_ReturnsNullWhenNoPythonRuns()
    {
        Assert.Null(ScreenshotPythonLauncher.Select([Python314], _ => null));
    }

    [Fact]
    public void MissingModulesMessage_NamesPipPackagesAndTheConsolePython()
    {
        var message = ScreenshotPythonLauncher.BuildMissingModulesMessage(
            new PythonLauncherSelection(Python314, ["win32gui", "win32con", "PIL"]));

        Assert.Contains(@"""C:\Py\Python314\python.exe"" -m pip install pywin32 Pillow", message);
    }

    [Fact]
    public void ParseProbeOutput_ReadsTheCommaSeparatedModuleList()
    {
        Assert.Empty(ScreenshotPythonLauncher.ParseProbeOutput(""));
        Assert.Equal(["win32gui", "win32con"], ScreenshotPythonLauncher.ParseProbeOutput("win32gui,win32con"));
    }
}
