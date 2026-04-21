# 正式版本 OTA 发布流程

这份文档对应普通版本的一键在线升级能力，不是问题热修复 patch。

当前规则：

- 普通版本更新使用 `zip_ota`
- 问题热修复继续使用 `issue_patch`
- 两者都走客户端现有的自动更新执行器，但发布入口和版本语义分开

## 1. 本地生成 OTA 包

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-app-update-package.ps1 `
  -AppVersion "2.2.0" `
  -ReleaseChannel "stable" `
  -ReleaseNotes "修复发送稳定性，补充正式 OTA 更新链路"
```

如果你要走当前推荐的一步式 staging 流程，直接执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-app-update-release.ps1 `
  -AppVersion "2.2.0" `
  -ReleaseChannel "stable" `
  -ReleaseNotes "修复发送稳定性，补充正式 OTA 更新链路" `
  -PublicDownloadBaseUrl "https://your-server/downloads/ota"
```

这条脚本会一次性做完：

- 生成 OTA zip
- 填好对外 `latestAppDownloadUrl`
- 把 zip staged 到 `downloads/ota/`
- 生成对外可登记的 `update-manifest.json`

默认输出目录：

`artifacts\ota\QuickReplyTool-AppUpdate-<appVersion>\`

关键文件：

- `artifacts\ota\QuickReplyTool-AppUpdate-<appVersion>\QuickReplyTool-AppUpdate-v<appVersion>.zip`
- `artifacts\ota\QuickReplyTool-AppUpdate-<appVersion>\package\app-update-info.json`
- `artifacts\ota\QuickReplyTool-AppUpdate-<appVersion>\release-manifest.snippet.json`

一步式 staging 脚本默认还会额外生成：

- `artifacts\published-ota\downloads\ota\QuickReplyTool-AppUpdate-v<appVersion>.zip`
- `artifacts\published-ota\update-manifest.json`

发布前建议同时留存这 3 类信息：

- 包内 `app-update-info.json`
- 对外发布用 `release-manifest.snippet.json`
- zip 本身的 SHA256 / `sizeBytes`，并和 manifest 中的值逐项对齐

说明：

- zip 内部会包含 `app\QuickReplyTool.exe` 和完整运行文件
- zip 内部现在也会包含 `app\LocalCloudService\QuickReply.CloudService.exe`，用于正式安装环境里的本机审核和知识同步 fallback
- `app-update-info.json` 用于包内自描述
- `release-manifest.snippet.json` 用于给服务端登记 OTA 元数据

如果你要把发布前的签名 smoke 和 rollback drill 一并做掉，继续执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\sign-release-artifact.ps1 `
  -ArtifactPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\QuickReplyTool-AppUpdate-v2.2.0.zip `
  -MetadataPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\package\app-update-info.json

powershell -ExecutionPolicy Bypass -File .\scripts\verify-release-artifact-signature.ps1 `
  -SignaturePath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\QuickReplyTool-AppUpdate-v2.2.0.zip.signature.json `
  -ArtifactPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\QuickReplyTool-AppUpdate-v2.2.0.zip `
  -MetadataPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\package\app-update-info.json

powershell -ExecutionPolicy Bypass -File .\scripts\build-rollback-package.ps1 `
  -CurrentArtifactPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\QuickReplyTool-AppUpdate-v2.2.0.zip `
  -CurrentMetadataPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\package\app-update-info.json `
  -BaselineVersion 2.1.9 `
  -RollbackVersion 2.2.0-rollback-smoke

powershell -ExecutionPolicy Bypass -File .\scripts\sign-release-artifact.ps1 `
  -ArtifactPath .\artifacts\rollback\QuickReplyTool-Rollback-2.2.0-rollback-smoke\QuickReplyTool-Rollback-v2.2.0-rollback-smoke.zip `
  -MetadataPath .\artifacts\rollback\QuickReplyTool-Rollback-2.2.0-rollback-smoke\rollback-package-info.json

powershell -ExecutionPolicy Bypass -File .\scripts\verify-release-artifact-signature.ps1 `
  -SignaturePath .\artifacts\rollback\QuickReplyTool-Rollback-2.2.0-rollback-smoke\QuickReplyTool-Rollback-v2.2.0-rollback-smoke.zip.signature.json `
  -ArtifactPath .\artifacts\rollback\QuickReplyTool-Rollback-2.2.0-rollback-smoke\QuickReplyTool-Rollback-v2.2.0-rollback-smoke.zip `
  -MetadataPath .\artifacts\rollback\QuickReplyTool-Rollback-2.2.0-rollback-smoke\rollback-package-info.json

powershell -ExecutionPolicy Bypass -File .\scripts\invoke-rollback-drill.ps1 `
  -RollbackPackagePath .\artifacts\rollback\QuickReplyTool-Rollback-2.2.0-rollback-smoke\QuickReplyTool-Rollback-v2.2.0-rollback-smoke.zip `
  -CurrentArtifactPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\QuickReplyTool-AppUpdate-v2.2.0.zip `
  -CurrentMetadataPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\package\app-update-info.json `
  -SignaturePath .\artifacts\rollback\QuickReplyTool-Rollback-2.2.0-rollback-smoke\QuickReplyTool-Rollback-v2.2.0-rollback-smoke.zip.signature.json
```

完整说明见 [release-signing-and-rollback-smoke.md](./release-signing-and-rollback-smoke.md)。

## 2. 上传 OTA zip

把 `QuickReplyTool-AppUpdate-v<appVersion>.zip` 上传到可直接访问的下载目录，例如：

`https://your-server/downloads/ota/QuickReplyTool-AppUpdate-v2.2.0.zip`

## 3. 更新服务端 manifest

打开 `release-manifest.snippet.json`，把这些字段写入服务端当前版本记录：

- `latestAppVersion`
- `latestAppDownloadUrl`
- `latestAppPackageKind`
- `latestAppSha256`
- `latestAppSizeBytes`
- `latestAppReleaseChannel`
- `latestAppMandatory`
- `latestAppReleaseNotes`

其中：

- `latestAppDownloadUrl` 替换成你上传后的真实公网地址
- `latestAppPackageKind` 固定写 `zip_ota`
- 第一版 `latestAppMandatory` 固定 `false`

## 4. 客户端实际行为

当客户端点击“立即检查更新”或启动时命中新的 `zip_ota` manifest：

1. 弹窗显示版本号和更新说明
2. 用户确认后，客户端下载 OTA zip
3. 如果安装目录在 `Program Files`，会请求管理员权限
4. 主程序退出
5. 更新器覆盖安装目录
6. 主程序自动重启
7. 启动后托盘提示“自动更新已完成”

如果服务端返回的是 `installer_exe` 或普通 URL：

- 客户端仍然会保守退回到“打开更新下载页”

## 5. 验证建议

发布前至少做一次：

1. 便携目录安装 -> 立即检查更新 -> OTA 成功 -> 用户数据保留
2. `Program Files` 安装 -> 触发 UAC -> OTA 成功
3. 服务端改成 `installer_exe` -> 客户端仍然打开下载页，不崩
4. 损坏 zip -> 客户端提示失败并给出本地日志路径
5. smoke 签名 sidecar -> 验签 receipt 成功
6. rollback scaffold -> dry-run rollback drill 成功

## 6. 和 patch 的边界

- 普通版本更新：`build-app-update-package.ps1`
- 问题热修复：`build-patch-package.ps1`

不要把普通版本 OTA 直接登记到问题 patch 接口里，也不要把 issue patch 当成正式版本 manifest。
