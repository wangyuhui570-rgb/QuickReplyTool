# 云同步、已发布知识与更新发布对接说明

## 目标

这份文档说明仓库里仍然保留的服务端骨架能力，以及如果你要接自己的服务器，当前最小需要对齐哪些接口。

当前默认桌面主流程已经回到本地工具模式，不再把这些能力当成必需路径；`src/QuickReply.CloudService` 保留为可选的同步、审核和运维骨架。

## 当前保留的能力

- 更新检查与 OTA 清单对接
- 已发布知识卡增量同步
- 知识候选审核队列、来源、历史和下架操作
- 本地或远端 `QuickReply.CloudService` 运行态检查

当前不再包含额外的生成式扩展接口或登录类控制面接口。

## 桌面端与本地服务的关系

- 桌面程序可以只作为本地快捷回复工具运行
- 如果保留更新检查或知识审核相关能力，可以连接远端服务
- 安装包、便携包、OTA 包仍可携带本机 `LocalCloudService` 运行时，便于本地联调或离线审核

## 建议保留的桌面端请求

### 1. 更新清单

```http
GET /api/quickreply/update/manifest?clientId=<id>&appVersion=<version>&knowledgeVersion=<version>
```

建议继续返回：

```json
{
  "latestAppVersion": "2.2.0",
  "latestAppDownloadUrl": "https://your-server/downloads/ota/QuickReplyTool-AppUpdate-v2.2.0.zip",
  "latestAppPackageKind": "zip_ota",
  "latestAppSha256": "SHA256",
  "latestAppSizeBytes": 73400320,
  "latestAppReleaseChannel": "stable",
  "latestAppMandatory": false,
  "latestAppReleaseNotes": "修复发送稳定性和本地工具体验。",
  "knowledgeVersion": "knowledge-r12",
  "knowledgePackageUrl": "https://your-server/knowledge/knowledge-r12",
  "knowledgeSummary": "新增一批已审核知识卡。"
}
```

说明：

- `latestAppPackageKind` 仍建议优先使用 `zip_ota`
- 如果返回普通下载页，客户端会保守退回到“打开下载页”
- `knowledgeVersion` 仅用于提示有新的知识包或知识页

### 2. 已发布知识卡同步

```http
GET /api/quickreply/knowledge/published?sinceRevision=<long>&cursor=<cursor>&limit=<n>
```

当前服务骨架返回的是分页增量同步模型，核心字段包括：

- `knowledgeVersion`
- `generatedAt`
- `latestRevision`
- `nextCursor`
- `hasMore`
- `cards`
- `removedCardIds`

建议保留：

- 按 `revision` 单调递增
- 分页语义稳定
- `removedCardIds` 用于客户端删除本地已失效卡片

### 3. 运行态检查

```http
GET /healthz
GET /readyz
GET /version
GET /info
```

当前语义：

- `/healthz` 只看服务是否存活
- `/readyz` 看数据库是否可初始化
- `/version` 返回版本号和管理鉴权开关摘要
- `/info` 返回更完整的运行态摘要，并受管理鉴权保护

## 当前服务骨架的管理接口

这些接口主要给审核或运维使用，不建议直接暴露给普通客户端：

```http
GET  /api/quickreply/knowledge/review/queue
GET  /api/quickreply/knowledge/review/{id}/sources
GET  /api/quickreply/knowledge/review/{id}/history
POST /api/quickreply/knowledge/review/{id}/approve
POST /api/quickreply/knowledge/review/{id}/reject
POST /api/quickreply/knowledge/published/{id}/remove
POST /api/quickreply/knowledge/jobs/import-logs
GET  /api/quickreply/knowledge/jobs/runs
```

建议：

- `review/*`、`jobs/*` 和 `/info` 都走统一管理鉴权
- 远端部署默认不要匿名开放
- 只在明确的本机联调或内网场景下允许 loopback 免鉴权

## 部署建议

- 远端服务优先使用 HTTPS
- `ManagementApiToken` 不要留空
- 本地联调时可以保留 `AllowLoopbackWithoutAuth=true`
- 生产环境应关闭 loopback 免鉴权，并把数据库放到独立持久目录

## 数据与表设计建议

如果你要继续用这套审核骨架，建议至少保留这些概念分层：

- `knowledge_candidates`
- `knowledge_candidate_sources`
- `knowledge_review_actions`
- `published_knowledge_cards`
- `published_knowledge_changes`
- `knowledge_job_runs`

这些表的职责应保持分离，不要把审核队列和正式发布卡片混成一张表。

## 当前最值得优先打通的链路

1. 更新清单接口
2. 已发布知识卡增量同步
3. 审核队列与发布/下架闭环

先把这三条链路跑通，再决定是否继续扩展更多后台能力。
