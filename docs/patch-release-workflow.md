# 补丁发布流程

这套流程对应现在客户端已经接好的能力：

- 客户端可从服务器拿到 `patchVersion / downloadUrl / releaseNotes`
- 客户端支持下载 `zip` 补丁包
- 客户端支持退出主程序、自动替换文件、自动重启

这份文档只讲问题热修复 patch。
普通版本 OTA 请看 [app-ota-release-workflow.md](/C:/Users/24173/Desktop/回复效率工具/回复效率工具/docs/app-ota-release-workflow.md)。

注意：

- 当前这不是“二进制差分补丁”
- 当前补丁包本质上是一个可自动安装的 `zip` 发布包
- 对用户来说效果仍然是“点更新后自动套用，不需要重新手动下载完整安装包”

## 1. 本地生成补丁包

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-patch-package.ps1 `
  -PatchVersion "2.1.9-hotfix1" `
  -ReleaseChannel "stable" `
  -IssueIds "issue-test-001" `
  -ReleaseNotes "修复问题记录补丁下载和自动安装链路"
```

生成结果默认在：

`artifacts\patches\QuickReplyTool-Patch-<patchVersion>\`

关键文件：

- `artifacts\patches\QuickReplyTool-Patch-<patchVersion>\QuickReplyTool-Patch-v<patchVersion>.zip`
- `artifacts\patches\QuickReplyTool-Patch-<patchVersion>\package\patch-info.json`
- `artifacts\patches\QuickReplyTool-Patch-<patchVersion>\patch-package-info.json`

`zip` 里会包含：

- `app\` 目录
- `patch-info.json`

客户端自动更新时会从 `zip` 中找到 `QuickReplyTool.exe` 所在目录，并把整包覆盖到当前安装目录。

其中 `patch-package-info.json` 是发布侧车元数据，建议和服务端登记记录一起留档。它至少包含：

- `packageKind`
- `releaseChannel`
- `sha256`
- `sizeBytes`

如果你要在发布前补上 smoke 签名和回滚演练，可以继续执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\sign-release-artifact.ps1 `
  -ArtifactPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\QuickReplyTool-Patch-v2.1.9-hotfix1.zip `
  -MetadataPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\patch-package-info.json

powershell -ExecutionPolicy Bypass -File .\scripts\verify-release-artifact-signature.ps1 `
  -SignaturePath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\QuickReplyTool-Patch-v2.1.9-hotfix1.zip.signature.json `
  -ArtifactPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\QuickReplyTool-Patch-v2.1.9-hotfix1.zip `
  -MetadataPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\patch-package-info.json

powershell -ExecutionPolicy Bypass -File .\scripts\build-rollback-package.ps1 `
  -CurrentArtifactPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\QuickReplyTool-Patch-v2.1.9-hotfix1.zip `
  -CurrentMetadataPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\patch-package-info.json `
  -BaselineVersion 2.1.9 `
  -RollbackVersion 2.1.9-hotfix1-rollback-smoke

powershell -ExecutionPolicy Bypass -File .\scripts\sign-release-artifact.ps1 `
  -ArtifactPath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\QuickReplyTool-Rollback-v2.1.9-hotfix1-rollback-smoke.zip `
  -MetadataPath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\rollback-package-info.json

powershell -ExecutionPolicy Bypass -File .\scripts\verify-release-artifact-signature.ps1 `
  -SignaturePath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\QuickReplyTool-Rollback-v2.1.9-hotfix1-rollback-smoke.zip.signature.json `
  -ArtifactPath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\QuickReplyTool-Rollback-v2.1.9-hotfix1-rollback-smoke.zip `
  -MetadataPath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\rollback-package-info.json

powershell -ExecutionPolicy Bypass -File .\scripts\invoke-rollback-drill.ps1 `
  -RollbackPackagePath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\QuickReplyTool-Rollback-v2.1.9-hotfix1-rollback-smoke.zip `
  -CurrentArtifactPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\QuickReplyTool-Patch-v2.1.9-hotfix1.zip `
  -CurrentMetadataPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\patch-package-info.json `
  -SignaturePath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\QuickReplyTool-Rollback-v2.1.9-hotfix1-rollback-smoke.zip.signature.json
```

完整说明见 [release-signing-and-rollback-smoke.md](./release-signing-and-rollback-smoke.md)。

## 2. 上传到服务器

你可以把补丁包上传到 nginx 可直接访问的目录，例如：

`/var/www/quickreply-downloads/patches/`

假设服务器公网地址是：

`http://45.207.201.102`

那么最终下载地址可以是：

`http://45.207.201.102/downloads/patches/QuickReplyTool-Patch-v2.1.9-hotfix1.zip`

## 3. 在服务器登记补丁

补丁包上传完成后，调用你已经有的接口：

`POST /api/quickreply/issues/patch/register`

示例：

```bash
curl -X POST http://127.0.0.1:18080/api/quickreply/issues/patch/register \
  -H 'Authorization: Bearer aWyh1314' \
  -H 'X-QuickReply-ClientId: test-client' \
  -H 'Content-Type: application/json' \
  --data '{
    "issueId":"issue-test-001",
    "patchVersion":"2.1.9-hotfix1",
    "releaseChannel":"stable",
    "downloadUrl":"http://45.207.201.102/downloads/patches/QuickReplyTool-Patch-v2.1.9-hotfix1.zip",
    "releaseNotes":"修复问题记录补丁下载和自动安装链路"
  }'
```

登记后：

- `/api/quickreply/issues/status/batch` 会返回这个补丁信息
- 客户端启动或手动刷新问题状态时，会看到该补丁
- 客户端若检测到下载地址是 `zip`，会直接走自动安装流程

## 4. 客户端实际行为

当客户端拿到补丁记录后：

1. 启动时提示发现问题修复补丁
2. 用户确认后，客户端下载 `zip`
3. 如果安装目录在 `Program Files`，会请求管理员权限
4. 主程序退出
5. 临时更新器替换文件
6. 主程序自动重新启动
7. 启动后托盘提示“自动更新已完成”

## 5. 推荐发布规则

- 普通功能版本继续用安装包：`QuickReplyTool-Setup-v<version>.exe`
- 问题热修复优先用补丁包：`QuickReplyTool-Patch-v<patchVersion>.zip`
- `patchVersion` 建议格式：
  - `2.1.9-hotfix1`
  - `2.1.9-hotfix2`
  - `2.1.10-hotfix1`

## 6. 当前限制

- 还没有做真正的二进制差分
- 已经有 smoke 级别的签名 sidecar / 验签 receipt 流程，但还没有接真实证书或签名服务
- 已经有 rollback package scaffold 和 dry-run drill，但还没有接真实自动回滚执行器
- 当前补丁包仍然是“完整应用文件覆盖式更新”

这套方案的优点是先落地，足够稳定，且和你现在的 Inno Setup 安装包体系不冲突。
