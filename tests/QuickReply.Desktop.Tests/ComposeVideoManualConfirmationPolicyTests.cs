using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class ComposeVideoManualConfirmationPolicyTests
{
    [Fact]
    public void Evaluate_DouyinWorkbench_RequiresManualConfirmation()
    {
        var decision = ComposeVideoManualConfirmationPolicy.Evaluate(SendHostKind.DouyinWorkbench);

        Assert.True(decision.RequiresManualConfirmation);
        Assert.Equal("douyin_video_file_drop_manual_confirm", decision.StrategyName);
        Assert.Equal("douyin_video_upload_requires_manual_confirmation", decision.Reason);
        Assert.Equal("video_upload_preview_manual_confirmation", decision.StageName);
        Assert.Contains("预览", decision.UserGuidance, StringComparison.Ordinal);
        Assert.Equal(ComposeVideoManualConfirmationPolicy.UploadPreviewOperatorState, decision.OperatorState);
        Assert.Equal(ComposeVideoManualConfirmationPolicy.WaitPreviewThenManualSendAction, decision.NextAction);
    }

    [Fact]
    public void Evaluate_OtherHosts_DoNotRequireManualConfirmation()
    {
        Assert.False(ComposeVideoManualConfirmationPolicy.Evaluate(SendHostKind.GenericDesktopInput).RequiresManualConfirmation);
        Assert.False(ComposeVideoManualConfirmationPolicy.Evaluate(SendHostKind.BrowserHostedInput).RequiresManualConfirmation);
        Assert.False(ComposeVideoManualConfirmationPolicy.Evaluate(SendHostKind.PddWorkbench).RequiresManualConfirmation);
        Assert.False(ComposeVideoManualConfirmationPolicy.Evaluate(SendHostKind.QianNiu).RequiresManualConfirmation);
    }

    [Fact]
    public void BuildTransferPlan_SkipsAutomaticHotkeyForDouyinVideo()
    {
        var payload = ComposeFilePayloadClassifier.Classify([@"C:\files\install-guide.mp4"]);
        var plan = ComposeFilePayloadClassifier.BuildTransferPlan(
            payload,
            SendHostProfile.DouyinWorkbench,
            new AppSettings());

        Assert.True(payload.ContainsVideo);
        Assert.True(plan.SkipAutomaticSendHotkey);
        Assert.Equal("douyin_video_file_drop_manual_confirm", plan.StrategyName);
        Assert.Equal("douyin_video_upload_requires_manual_confirmation", plan.SkipAutomaticSendHotkeyReason);
        Assert.Equal("video_upload_preview_manual_confirmation", plan.ManualConfirmationStage);
        Assert.Contains("预览", plan.ManualConfirmationUserGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTransferPlan_KeepsBrowserVideoAutoSendThisRound()
    {
        var payload = ComposeFilePayloadClassifier.Classify([@"C:\files\install-guide.mp4"]);
        var plan = ComposeFilePayloadClassifier.BuildTransferPlan(
            payload,
            SendHostProfile.BrowserHostedInput,
            new AppSettings());

        Assert.True(payload.ContainsVideo);
        Assert.False(plan.SkipAutomaticSendHotkey);
        Assert.Equal("file_drop_send", plan.StrategyName);
    }

    [Fact]
    public void BuildTransferPlan_KeepsDouyinNonVideoFileAutoSend()
    {
        var payload = ComposeFilePayloadClassifier.Classify([@"C:\files\manual.pdf"]);
        var plan = ComposeFilePayloadClassifier.BuildTransferPlan(
            payload,
            SendHostProfile.DouyinWorkbench,
            new AppSettings());

        Assert.False(payload.ContainsVideo);
        Assert.False(plan.SkipAutomaticSendHotkey);
        Assert.Equal("safe_file_drop_send", plan.StrategyName);
    }
}
