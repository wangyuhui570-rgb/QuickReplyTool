# 问题上报链路（第一阶段）

## 当前客户端行为

托盘菜单里的“问题记录”现在会同时做三件事：

1. 生成本地 markdown 反馈文件
2. 生成同目录 JSON 结构化问题包
3. 如果已配置服务器地址，则立即向 `/api/quickreply/issues/report` 尝试上传

本地文件目录沿用现有“问题反馈目录”配置。

## 客户端上传接口

```http
POST /api/quickreply/issues/report
Content-Type: application/json
Accept: application/json
X-QuickReply-ClientId: <syncClientId>
X-QuickReply-AppVersion: <appVersion>
Authorization: Bearer <syncApiToken>
```

## JSON 结构

顶层模型：`IssueReportPackage`

字段：

- `issueId`
- `createdAt`
- `clientId`
- `appVersion`
- `machineName`
- `processPath`
- `processId`
- `userDescription`
- `serverBaseUrl`
- `settingsSnapshot`
- `paths`
- `diagnostics`

### `settingsSnapshot`

当前 `AppSettings` 的字符串快照。

说明：

- token / password / secret 相关字段已做掩码
- 这部分主要给小龙虾和服务端复盘配置环境

### `paths`

- `dataRootPath`
- `logsRootPath`
- `feedbackRootPath`
- `databasePath`
- `conversationLogDatabasePath`
- `screenshotToolSettingsPath`

### `diagnostics`

- `screenshotToolSettingsText`
- `startupLogText`
- `exceptionLogTail`
- `searchOverlayLogTail`
- `logDirectorySnapshot`
- `recentConversationEntries`

### `recentConversationEntries`

每条包含：

- `occurredAt`
- `triggerSource`
- `actionMode`
- `payloadType`
- `resultSuccess`
- `resultMessage`
- `targetProcessName`
- `phraseTitle`
- `fileDisplayName`

## 服务端建议建表

### `issue_reports`

- `id` TEXT PRIMARY KEY
- `client_id` TEXT NOT NULL
- `app_version` TEXT NOT NULL
- `machine_name` TEXT NOT NULL
- `user_description` TEXT NOT NULL
- `server_base_url` TEXT NOT NULL
- `created_at` TEXT NOT NULL
- `status` TEXT NOT NULL
- `root_cause_type` TEXT NOT NULL
- `dedupe_key` TEXT NOT NULL
- `package_json` TEXT NOT NULL
- `created_at_server` TEXT NOT NULL
- `updated_at_server` TEXT NOT NULL

### `issue_report_events`

- `id` INTEGER PRIMARY KEY AUTOINCREMENT
- `issue_id` TEXT NOT NULL
- `event_type` TEXT NOT NULL
- `event_payload_json` TEXT NOT NULL
- `created_at` TEXT NOT NULL

建议事件类型：

- `received`
- `triaged`
- `assigned_to_agent`
- `patch_candidate_created`
- `build_passed`
- `build_failed`
- `published_to_test`
- `published_to_prod`
- `closed`

## 服务端分诊建议

收到问题包后先做轻分诊：

- `code_bug`
- `config_error`
- `knowledge_gap`
- `recommendation_gap`
- `environment_issue`
- `duplicate_issue`

如果是重复问题，直接挂到已有 issue，不要每条都触发一次小龙虾。

## 发给小龙虾的输入建议

不要直接把完整原始日志全量丢给小龙虾，建议服务端整理后再发。

输入结构建议：

- `issueSummary`
- `issueId`
- `clientId`
- `appVersion`
- `userDescription`
- `suspectedArea`
- `recentActions`
- `recentExceptions`
- `searchOverlayDiagnostics`
- `settingsSnapshot`
- `repoPath`
- `expectedOutput`

其中 `expectedOutput` 固定成：

- 根因判断
- 是否能复现
- 建议修复方案
- 是否需要改代码
- 如果改代码，列出涉及文件
- 编译和测试命令

## 下一阶段

第一阶段做完后，第二阶段继续补：

1. 服务端 `issue_reports` 接口实现
2. 小龙虾问题分诊任务
3. 自动建分支和修复候选
4. 自动编译和测试
