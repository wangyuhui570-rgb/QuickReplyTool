namespace QuickReply.Desktop.Services;

internal static class ComposeVideoManualConfirmationPolicy
{
    private const string ManualConfirmationStage = "video_upload_preview_manual_confirmation";
    internal const string UploadPreviewOperatorState = "video_pasted_waiting_upload_preview";
    internal const string WaitPreviewThenManualSendAction = "wait_preview_then_manual_send";

    public static ComposeVideoManualConfirmationDecision Evaluate(SendHostKind hostKind)
    {
        return hostKind switch
        {
            SendHostKind.DouyinWorkbench => new(
                true,
                "douyin_video_file_drop_manual_confirm",
                "douyin_video_upload_requires_manual_confirmation",
                ManualConfirmationStage,
                "抖店/飞鸽视频不会自动发送：已尝试粘贴，但不会自动按发送，以免误报成功。请确认输入框是否出现上传预览；若没有预览，请改用工作台视频上传按钮手动选择文件。",
                "请留在抖店/飞鸽聊天窗口查看是否出现视频上传预览。若预览未出现，请用工作台视频/上传按钮手动选文件，不要重复触发自动发送。",
                UploadPreviewOperatorState,
                WaitPreviewThenManualSendAction),
            _ => new(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty)
        };
    }
}

internal readonly record struct ComposeVideoManualConfirmationDecision(
    bool RequiresManualConfirmation,
    string StrategyName,
    string Reason,
    string StageName,
    string Message,
    string UserGuidance,
    string OperatorState,
    string NextAction);
