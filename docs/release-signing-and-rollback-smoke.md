> Note: App-update / OTA package scripts were removed with the local-only refactor. Prefer portable zip and installer signing only.

# 发布签名与回滚 Smoke

这套脚本不是生产级证书签名，也不会真的替换安装目录。它解决的是两件事：

- 让 `installer / portable / ota / patch` 产物在本地和 CI 都能走一遍统一的“签名 sidecar + 验签 receipt”流程
- 让 OTA/patch 发布前至少能产出一份可审计的 `rollback-plan.json` 和 dry-run 回滚演练结果

当前默认 signer 是脚本内置的 smoke secret，只适合流程演练和 CI。后续如果接真实证书或签名服务，可以在不改 test gate 入口的前提下替换实现。

## 统一入口

如果你已经在跑本地门禁，直接用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-test-gates.ps1 -IncludePackagingChecks
```

这条命令现在会额外执行：

- 对 portable / OTA / patch 产物做 smoke 签名和验签
- 对 OTA / patch 额外生成 rollback package scaffold
- 对 rollback scaffold 再做一次签名、验签和 dry-run drill
- 如果同时带 `-IncludeInstallerCheck`，installer 也会纳入 smoke 签名和验签

CI 也走同一个入口，所以 `artifacts\test-gates\...\signing-smoke\` 和 `artifacts\test-gates\...\rollback-smoke\` 的结构在本地和 GitHub Actions 里是一致的。

## 单独签名与验签

以 OTA 包为例：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\sign-release-artifact.ps1 `
  -ArtifactPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\QuickReplyTool-AppUpdate-v2.2.0.zip `
  -MetadataPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\package\app-update-info.json
```

默认会在同目录生成：

- `QuickReplyTool-AppUpdate-v2.2.0.zip.signature.json`

继续验签：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release-artifact-signature.ps1 `
  -SignaturePath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\QuickReplyTool-AppUpdate-v2.2.0.zip.signature.json `
  -ArtifactPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\QuickReplyTool-AppUpdate-v2.2.0.zip `
  -MetadataPath .\artifacts\ota\QuickReplyTool-AppUpdate-2.2.0\package\app-update-info.json
```

默认会再生成：

- `QuickReplyTool-AppUpdate-v2.2.0.zip.signature.receipt.json`

同样的模式也适用于：

- portable：`portable-package-info.json`
- patch：`patch-package-info.json`
- installer：`installer-package-info.json`

## 生成回滚包骨架

以 patch 包为例：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-rollback-package.ps1 `
  -CurrentArtifactPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\QuickReplyTool-Patch-v2.1.9-hotfix1.zip `
  -CurrentMetadataPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\patch-package-info.json `
  -BaselineVersion 2.1.9 `
  -RollbackVersion 2.1.9-hotfix1-rollback-smoke
```

输出目录里会有：

- `QuickReplyTool-Rollback-v<rollbackVersion>.zip`
- `rollback-package-info.json`
- zip 内的 `rollback-plan.json`

这是一个 smoke scaffold，不带真正 restore payload。它的目标是把回滚需要的版本、文件 hash、步骤和证据要求先固化下来。

## 执行 dry-run 回滚演练

如果你已经对 rollback 包做了 smoke 签名，可以继续执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\invoke-rollback-drill.ps1 `
  -RollbackPackagePath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\QuickReplyTool-Rollback-v2.1.9-hotfix1-rollback-smoke.zip `
  -CurrentArtifactPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\QuickReplyTool-Patch-v2.1.9-hotfix1.zip `
  -CurrentMetadataPath .\artifacts\patches\QuickReplyTool-Patch-2.1.9-hotfix1\patch-package-info.json `
  -SignaturePath .\artifacts\rollback\QuickReplyTool-Rollback-2.1.9-hotfix1-rollback-smoke\QuickReplyTool-Rollback-v2.1.9-hotfix1-rollback-smoke.zip.signature.json
```

输出会包含：

- `rollback-drill-summary.json`
- `rollback-drill-report.md`

它只验证：

- rollback plan 结构是否完整
- 当前待回滚包和 plan 快照是否一致
- rollback scaffold 自己的签名 sidecar 是否能通过验签

它不会真的停进程、覆盖目录或修改 `%AppData%`。

## Release Evidence

如果用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-release-evidence.ps1 -VersionLabel 2.2.0
```

`packaging-upgrade\` 里现在会多出两份模板：

- `signature-verification-checklist.md`
- `rollback-drill-checklist.md`

如果是通过 `run-test-gates.ps1 -ReleaseEvidenceVersion ...` 生成证据目录，签名和回滚 smoke 的日志、receipt、summary 也会一起归档进去。
