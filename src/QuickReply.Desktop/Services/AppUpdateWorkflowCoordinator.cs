using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed class AppUpdateWorkflowCoordinator
{
    private readonly IUpdateCheckStateService? _updateCheckStateService;
    private readonly IPackageUpdateService? _packageUpdateService;
    private readonly IPatchInstallationStateService? _patchInstallationStateService;
    private readonly IAppUpdateWorkflowUi _workflowUi;

    public AppUpdateWorkflowCoordinator(
        IUpdateCheckStateService? updateCheckStateService,
        IPackageUpdateService? packageUpdateService,
        IPatchInstallationStateService? patchInstallationStateService,
        IAppUpdateWorkflowUi workflowUi)
    {
        _updateCheckStateService = updateCheckStateService;
        _packageUpdateService = packageUpdateService;
        _patchInstallationStateService = patchInstallationStateService;
        _workflowUi = workflowUi;
    }

    public Task<PackageUpdateLaunchResult> ApplyIssuePatchUpdateAsync(IssueReportIndexEntry entry)
    {
        return ApplyRemotePackageUpdateAsync(
            "问题修复",
            entry.PatchVersion,
            new AppUpdatePackageDescriptor(
                AppUpdatePackageResolver.ResolveKind(string.Empty, entry.PatchDownloadUrl),
                string.Empty,
                entry.PatchDownloadUrl?.Trim() ?? string.Empty,
                string.Empty,
                null,
                string.Empty,
                false),
            "issue_patch");
    }

    public async Task HandlePendingPackageUpdateStatusAsync()
    {
        if (_packageUpdateService is null)
        {
            return;
        }

        var status = _packageUpdateService.ConsumeLastStatus();
        if (!status.HasValue)
        {
            return;
        }

        if (status.Value.Success &&
            _patchInstallationStateService is not null &&
            string.Equals(status.Value.SourceKind, "issue_patch", StringComparison.OrdinalIgnoreCase))
        {
            await _patchInstallationStateService.MarkPatchVersionInstalledAsync(status.Value.VersionLabel).ConfigureAwait(false);
        }

        var title = string.Equals(status.Value.SourceKind, "issue_patch", StringComparison.OrdinalIgnoreCase)
            ? "问题修复"
            : "程序更新";
        var message = status.Value.Success
            ? (string.IsNullOrWhiteSpace(status.Value.VersionLabel)
                ? "自动更新已完成。"
                : $"自动更新已完成：{status.Value.VersionLabel}")
            : $"自动更新失败：{status.Value.Message}";

        await _workflowUi.ShowBackgroundNotificationAsync(title, message, isWarning: !status.Value.Success).ConfigureAwait(false);
    }

    public async Task HandleUpdateCheckResultAsync(AppSettings settingsSnapshot, UpdateCheckResult updateResult)
    {
        if (updateResult.Manifest is null)
        {
            return;
        }

        if (updateResult.HasAppUpdate)
        {
            var signatureStatus = UpdateManifestSignatureVerifier.Verify(updateResult.Manifest);
            if (signatureStatus is UpdateManifestSignatureStatus.Missing or UpdateManifestSignatureStatus.Invalid)
            {
                await _workflowUi.ShowWarningAsync(
                    "程序更新",
                    signatureStatus == UpdateManifestSignatureStatus.Missing
                        ? "服务器返回的更新信息没有签名，为了安全已拒绝本次更新。请联系管理员。"
                        : "更新信息签名校验失败，可能被篡改，已拒绝本次更新。请联系管理员。").ConfigureAwait(false);
                return;
            }

            var packageDescriptor = UpdateCheckService.ResolveAppUpdatePackage(updateResult.Manifest);
            var downloadUrl = packageDescriptor.DownloadUrl;
            var releaseNotes = updateResult.Manifest.LatestAppReleaseNotes?.Trim() ?? string.Empty;
            var canSelfApplyAppUpdate = _packageUpdateService?.CanSelfApplyPackage(downloadUrl, packageDescriptor.PackageKindText) == true;

            var message = $"发现新版本：{updateResult.Manifest.LatestAppVersion}{Environment.NewLine}{Environment.NewLine}";
            if (!string.IsNullOrWhiteSpace(releaseNotes))
            {
                message += $"{releaseNotes}{Environment.NewLine}{Environment.NewLine}";
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                await _workflowUi.ShowInfoAsync("程序更新", $"{message}请联系管理员获取更新包。").ConfigureAwait(false);
            }
            else
            {
                message += canSelfApplyAppUpdate
                    ? "是否现在下载并自动安装更新？"
                    : "是否现在打开更新下载页？";

                if (await _workflowUi.ConfirmAsync("程序更新", message).ConfigureAwait(false))
                {
                    await ApplyRemotePackageUpdateAsync(
                        "程序更新",
                        updateResult.Manifest.LatestAppVersion,
                        packageDescriptor,
                        "app_update").ConfigureAwait(false);
                }
            }
        }

        if (updateResult.HasKnowledgeUpdate)
        {
            var knowledgeVersion = updateResult.Manifest.KnowledgeVersion?.Trim() ?? string.Empty;
            var packageUrl = updateResult.Manifest.KnowledgePackageUrl?.Trim() ?? string.Empty;
            var summary = updateResult.Manifest.KnowledgeSummary?.Trim() ?? string.Empty;
            var message = $"发现新的知识库分析结果：{knowledgeVersion}{Environment.NewLine}{Environment.NewLine}";
            if (!string.IsNullOrWhiteSpace(summary))
            {
                message += $"{summary}{Environment.NewLine}{Environment.NewLine}";
            }

            if (string.IsNullOrWhiteSpace(packageUrl))
            {
                await _workflowUi.ShowInfoAsync("知识库更新", $"{message}服务器未提供下载地址，请联系管理员查看。").ConfigureAwait(false);
            }
            else
            {
                if (await _workflowUi.ConfirmAsync("知识库更新", $"{message}是否现在打开知识包或分析页面？").ConfigureAwait(false))
                {
                    await _workflowUi.OpenExternalUrlAsync(packageUrl).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(knowledgeVersion) &&
                _updateCheckStateService is not null)
            {
                await _updateCheckStateService.MarkKnowledgeVersionSeenAsync(settingsSnapshot, knowledgeVersion).ConfigureAwait(false);
            }
        }
    }

    private async Task<PackageUpdateLaunchResult> ApplyRemotePackageUpdateAsync(
        string title,
        string versionLabel,
        AppUpdatePackageDescriptor packageDescriptor,
        string sourceKind)
    {
        var normalizedUrl = packageDescriptor.DownloadUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUrl))
        {
            var message = "服务器未提供可用的更新地址。";
            await _workflowUi.ShowWarningAsync(title, message).ConfigureAwait(false);
            return new PackageUpdateLaunchResult(false, false, message, string.Empty, string.Empty);
        }

        if (_packageUpdateService is null)
        {
            var message = "自动更新服务尚未初始化。";
            await _workflowUi.ShowWarningAsync(title, message).ConfigureAwait(false);
            return new PackageUpdateLaunchResult(false, false, message, string.Empty, string.Empty);
        }

        if (!_packageUpdateService.CanSelfApplyPackage(normalizedUrl, packageDescriptor.PackageKindText))
        {
            await _workflowUi.OpenExternalUrlAsync(normalizedUrl).ConfigureAwait(false);
            return new PackageUpdateLaunchResult(true, false, "当前更新地址不支持一键 OTA，已改为打开更新下载页。", string.Empty, string.Empty);
        }

        if (string.Equals(sourceKind, "app_update", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(packageDescriptor.ExpectedSha256))
        {
            var message = "服务器未提供更新包的 SHA256 校验值，为了安全已拒绝自动安装。请联系管理员。";
            await _workflowUi.ShowWarningAsync(title, message).ConfigureAwait(false);
            return new PackageUpdateLaunchResult(false, false, message, string.Empty, string.Empty);
        }

        await _workflowUi.ShowBackgroundNotificationAsync(title, "正在下载更新包，请稍候。").ConfigureAwait(false);

        var result = await _packageUpdateService
            .DownloadAndLaunchAsync(
                normalizedUrl,
                versionLabel,
                sourceKind,
                packageDescriptor.ExpectedSha256,
                packageDescriptor.ExpectedSizeBytes,
                packageDescriptor.PackageKindText)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            var failureMessage = string.IsNullOrWhiteSpace(result.LogPath)
                ? result.Message
                : $"{result.Message}{Environment.NewLine}{Environment.NewLine}日志：{result.LogPath}";
            await _workflowUi.ShowWarningAsync(title, failureMessage).ConfigureAwait(false);
            return result;
        }

        await _workflowUi.ShowInfoAsync(
            title,
            $"更新包已下载完成。程序现在会退出并自动安装 {versionLabel}，安装完成后会自动重新启动。").ConfigureAwait(false);
        _workflowUi.PrepareForRestart();
        _workflowUi.Shutdown();
        return result;
    }
}
