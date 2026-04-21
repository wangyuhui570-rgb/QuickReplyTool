namespace QuickReply.ReplayEval.Tests;

public sealed class ReplayEvalOptionsTests
{
    [Fact]
    public void Parse_UsesDefaultsWhenArgsAreMissing()
    {
        var options = ReplayEvalOptions.Parse([]);

        Assert.Equal(300, options.SampleLimit);
        Assert.Equal(8, options.MinimumSampleCount);
        Assert.Null(options.PlatformHint);
        Assert.Null(options.SampleSource);
        Assert.Null(options.OutputDirectory);
    }

    [Fact]
    public void Parse_ClampsNumericValuesAndReadsNamedArguments()
    {
        var options = ReplayEvalOptions.Parse(
        [
            "--limit", "0",
            "--min-samples", "-5",
            "--platform", "wechat",
            "--source", "failed",
            "--out", "artifacts\\replay"
        ]);

        Assert.Equal(1, options.SampleLimit);
        Assert.Equal(0, options.MinimumSampleCount);
        Assert.Equal("wechat", options.PlatformHint);
        Assert.Equal(ReplaySampleSources.FailedSend, options.SampleSource);
        Assert.Equal("artifacts\\replay", options.OutputDirectory);
    }

    [Fact]
    public void Parse_ReadsLogHealthArguments()
    {
        var options = ReplayEvalOptions.Parse(
        [
            "--log-health",
            "--logs-root", "artifacts\\sample-logs",
            "--out", "artifacts\\log-health"
        ]);

        Assert.True(options.RunLogHealthCheck);
        Assert.Equal("artifacts\\sample-logs", options.LogRootPath);
        Assert.Equal("artifacts\\log-health", options.OutputDirectory);
    }
}
