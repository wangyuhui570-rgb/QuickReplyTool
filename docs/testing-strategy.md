# QuickReplyTool 测试策略

## 目标

这份策略面向当前仓库的真实能力，而不是只参考早期 1.3 版本文档。当前产品范围至少包括：

- 短语与分类管理
- 文件库与图片发送
- 搜索面板与推荐
- 会话日志与回放评估
- 问题上报与状态刷新
- 云同步
- 更新检查、正式 OTA 与 zip 补丁自更新
- 截图工具联动

短期目标不是一次性补全所有自动化，而是建立一套可以持续扩展的门禁。

## 分层

### L0 构建与打包校验

- `dotnet build 回复效率工具.sln`
- `dotnet build src\QuickReply.Desktop\QuickReply.Desktop.csproj`
- `dotnet build src\QuickReply.ReplayEval\QuickReply.ReplayEval.csproj`
- 可选打包检查：
  - `scripts\build-installer.ps1`
  - `scripts\build-portable.ps1`
  - `scripts\build-app-update-package.ps1`
  - `scripts\build-patch-package.ps1`
- 打包产物现在都应附带侧车元数据，至少覆盖：
  - `packageKind`
  - `releaseChannel`
  - `sha256`
  - `sizeBytes`

### L1 纯逻辑测试

优先覆盖不依赖真实桌面环境的逻辑：

- `AppSettings` 归一化与范围钳制
- `ReplyPhrase` 图片路径序列化
- `ReplayEvalOptions` 参数解析
- `ReplayEvaluationReportWriter` 汇总输出

### L2 服务集成测试

优先覆盖真实数据结构与序列化边界：

- `SqliteAppDataStore` 初始化、设置持久化、文件项回写
- `UpdateCheckService` 清单解析与时间戳落盘
- `KnowledgeSyncService` 知识增量与相对路径生成
- `IssueReportService` 问题包生成、脱敏、状态刷新
- `SelfUpdateService` 下载、SHA256/zip 结构校验、计划文件生成

### L3 受控副作用测试

使用 test doubles 覆盖高风险链路，而不是依赖真实微信、浏览器或服务器：

- `MessageComposeService` 的剪贴板写入分支
- 目标窗口缺失或焦点丢失时的失败提示
- 发送热键切换
- 文件发送和文本发送的 paste 行为

### L4 手工回归矩阵

Windows 真实环境下做最终确认，重点验证：

- 输入/发送体验
- 焦点切回与剪贴板恢复
- 文件/图片发送
- 搜索推荐
- 云同步、问题上报、更新、自更新
- 截图工具联动

回归步骤详见 [manual-regression-matrix.md](./manual-regression-matrix.md)。

### 日志健康检查

除了本地自动化和手工回归，现在还支持把其他机器导出的 `logs.zip` 或日志目录做一次离线健康体检。这个步骤专门用来补足“测试都绿了，但真实宿主上仍然有焦点/剪贴板/识别退化”的盲区。

- 统一入口：`scripts\check-log-health.ps1`
- 支持输入：解压后的 `logs\` 目录，或直接传 `logs.zip`
- 默认输出：`artifacts\log-health\<timestamp>\`
- 当前会重点拦截：
  - 终止型异常
  - 发送失败率过高
  - 长时间 `low_confidence` 且抓取内容明显像窗口标题
  - 搜索面板动作很多，但 `recommendation_events` 仍为 0
- 体检摘要会额外按宿主展示发送健康度，包括失败率、焦点丢失、剪贴板失败，以及自动补救成功率，便于单独盯住拼多多、千牛这类高风险宿主。
- 体检摘要现在还会展示关键流程漏斗，至少覆盖：
  - `update_check`
  - `cloud_sync`
  - `issue_report_create`
  - `issue_report_refresh`
  - `issue_report_retry_upload`
- 关键流程漏斗会记录尝试数、成功数、失败数、跳过数、平均耗时和主要掉点阶段，方便直接定位更新、同步、问题反馈这类非发送链路的退化。

### ReplayEval 样本来源

`QuickReply.ReplayEval` 现在不只回放“成功发送”的历史短语，还会把两类更有优化价值的样本一起纳入：

- `successful_send`：原本成功发送的历史短语样本
- `failed_send`：发送失败但仍记录了识别上下文与实际短语的样本
- `correction_backfill`：由 `recognition_corrections + recommendation_events(accepted)` 自动回灌出来的纠错样本

常用方式：

- 默认混合三类来源一起评估
- 如果只想盯失败发送，可加 `--source failed`
- 如果只想看纠错回灌，可加 `--source correction`

ReplayEval 输出目录里现在也会额外生成：

- `successful-samples.jsonl`
- `failed-samples.jsonl`
- `correction-backfill-samples.jsonl`
- `successful-misses.jsonl`
- `failed-misses.jsonl`
- `correction-backfill-misses.jsonl`

## 优先级

### P0

- 发送主链路
- 启动与恢复
- 问题记录与更新

### P1

- 搜索与推荐
- 云同步
- ReplayEval 指标输出

### P2

- 截图工具联动
- 更细粒度的外部窗口兼容性

## 本地门禁

推荐使用统一脚本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-test-gates.ps1
```

如果要把便携包、正式 OTA 包和补丁包的烟测一起纳入门禁，可以执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-test-gates.ps1 -IncludePackagingChecks
```

如果当前机器安装了 Inno Setup，或者希望在 CI / 手工触发时把 installer 也纳入固定 smoke，可以再补上：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-test-gates.ps1 -IncludePackagingChecks -IncludeInstallerCheck
```

如果手上还有其他机器导出的日志包，希望一起做离线体检，可以补上 `-LogHealthInputPath`：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-test-gates.ps1 -LogHealthInputPath .\logs.zip
```

如果希望在跑门禁的同时直接生成一份发布证据目录，并把日志与 `.trx` 结果归档进去，可以执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-test-gates.ps1 -IncludePackagingChecks -ReleaseEvidenceVersion 2.1.9
```

脚本默认执行：

1. 解决方案构建
2. `QuickReply.Desktop.Tests`
3. `QuickReply.ReplayEval.Tests`
4. 如果传入 `-LogHealthInputPath`，再额外执行日志健康检查
5. 如果传入 `-IncludePackagingChecks`，再额外校验 portable / OTA / patch 的产物结构和侧车元数据
6. 如果同时传入 `-IncludeInstallerCheck`，再额外校验 installer 产物和侧车元数据

输出日志会写入 `artifacts\test-gates\<timestamp>\`。
测试结果同时会输出 `.trx` 文件，方便本地留档和 CI 上传。
如果传入 `-ReleaseEvidenceVersion`，脚本还会自动生成 `artifacts\release-evidence\...` 目录，并把构建日志、测试日志、`.trx` 结果和打包烟测日志归档进去。
这份证据目录现在会额外带上可直接勾选的 `manual-regression-checklist.md`，以及按安装包、便携包、正式 OTA 包、补丁包拆分的 package-specific checklists，方便候选发布和正式发布时逐项留证。

如果仓库托管在 GitHub，可以直接复用 `.github/workflows/ci.yml`，让云端 CI 与本地门禁保持同一入口。
手动触发工作流时还可以填写 `releaseEvidenceVersion`，让 CI 同步上传 `artifacts\release-evidence\...` 作为独立 artifact。
如果手动触发时把 `includeInstallerCheck` 打开，工作流会先安装 Inno Setup，再跑 installer smoke。
CI 上传的 artifact 现在会优先带上 `releaseEvidenceVersion`；如果没有提供版本号，则会退化成 `run-<run_number>`，方便区分多次触发的结果。

如果只想单独分析一包日志，不跑整套门禁，也可以直接执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-log-health.ps1 -InputPath .\logs.zip
```

## 发布门禁

### 开发门禁

- L0 全通过
- P0 的自动化测试全通过

### 候选发布门禁

- P0 + P1 自动化测试通过
- 执行完整 L4 冒烟回归

### 正式发布门禁

- 安装包、便携包、补丁包分别做一次真实安装/升级验证
- 保留四类证据：
  - 构建结果
  - 自动测试结果
  - 手工回归勾选表
  - 打包与升级验证记录

可以先用下面的脚本生成证据目录脚手架：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-release-evidence.ps1 -VersionLabel 2.1.9
```

如果只想自定义输出路径，也可以补上 `-OutputRoot`：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-release-evidence.ps1 -VersionLabel 2.1.9 -OutputRoot .\artifacts\release-evidence\2.1.9
```

## 后续扩展方向

- 为 `KnowledgeSyncService` 增补非 2xx 与半成功分支
- 为 `IssueReportService` 增补重试上传与安装标记测试
- 为 `MessageComposeService` 增补更多焦点恢复与剪贴板恢复场景
- 在自动化稳定后，再考虑 WinAppDriver/FlaUI 一类的桌面 UI 自动化
