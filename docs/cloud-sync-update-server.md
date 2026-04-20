# 云同步、推荐分析与更新发布对接说明

## 目标

这份文档用于说明当前桌面端已经具备的云同步和更新检查能力，以及服务端要怎么接，才能完成下面这条业务链：

1. 客户端把当天新增或变更的知识库数据上传到服务器
2. 客户端把客户问题、推荐点击、识别纠正等训练数据上传到服务器
3. 服务端做清洗、聚合、统计和推荐分析
4. 服务端判断数据是否足够、哪些问题最常见、哪些推荐回复要补
5. 你根据分析结果修改软件或短语库，版本号加 1 并发布 OTA 包
6. 客户重启软件后，程序提示是否有新版本或新的知识分析结果

## 当前客户端已实现的部分

当前仓库里的桌面端已经完成这些能力：

- 本地保存云同步开关、更新检查开关、服务器地址、客户端标识、API Token
- 启动后后台执行一次云同步和更新检查
- 同一天内默认只自动执行一次
- 设置页支持手动立即同步、立即检查更新
- 发现新版本后弹出提示；如果服务端登记的是 `zip_ota` 更新包，会在线下载、自动安装并重启
- 如果服务端返回的是普通下载页或安装包地址，客户端会退回到“打开下载页”
- 发现新的知识分析结果后弹出提示，支持打开知识包或分析页面
- 仓库里现在还提供了一个本地可跑的服务端骨架：`src/QuickReply.CloudService`
  - 已包含 `published knowledge sync` 和审核 `approve/reject` 的最小接口
  - 适合先本地联调客户端知识同步与 AI grounding，不等完整生产后台全部做完
  - 现在正式安装/绿色版/OTA/patch 包也会一起带上本机 `LocalCloudService` 运行时，不再只依赖源码树里的 `dotnet run`

对应代码位置：

- [App.xaml.cs](/C:/Users/24173/Desktop/回复效率工具/回复效率工具/src/QuickReply.Desktop/App.xaml.cs)
- [KnowledgeSyncService.cs](/C:/Users/24173/Desktop/回复效率工具/回复效率工具/src/QuickReply.Desktop/Services/KnowledgeSyncService.cs)
- [UpdateCheckService.cs](/C:/Users/24173/Desktop/回复效率工具/回复效率工具/src/QuickReply.Desktop/Services/UpdateCheckService.cs)
- [CloudSyncModels.cs](/C:/Users/24173/Desktop/回复效率工具/回复效率工具/src/QuickReply.Desktop/Models/CloudSyncModels.cs)
- [SettingsWindow.xaml](/C:/Users/24173/Desktop/回复效率工具/回复效率工具/src/QuickReply.Desktop/SettingsWindow.xaml)

## 先说清楚的一点

你最开始的想法是“每天零点自动上传当天数据”。

当前客户端代码还不是这个行为。现在实现的是：

- 客户端每天首次启动时自动跑一次
- 用户在设置页里也可以手动点“立即同步一次”

这意味着：

- 如果客户当天没有启动软件，就不会在 00:00 自动上传
- 如果你接受“每天开机或首次打开软件时上传”，现在这版已经够做第一阶段
- 如果你一定要“到了 00:00 不管软件在不在前台都自动上传”，要额外做以下其中一种：
  - 保持主程序常驻托盘，并在本地定时器到点后执行
  - 安装一个 Windows 计划任务，在 00:00 启动程序的后台同步模式
  - 单独做一个轻量后台同步代理

建议第一阶段先用“每天首次启动自动同步 + 手动补跑”，先验证服务器分析链路能不能出结果，不要一开始就把复杂度堆到定时任务上。

## 客户端向服务端发的请求

### 通用规则

基础地址来自设置里的 `ServerBaseUrl`。

所有请求都会带这些 Header：

```http
Accept: application/json
X-QuickReply-ClientId: <SyncClientId>
X-QuickReply-AppVersion: <当前程序版本>
Authorization: Bearer <SyncApiToken>
```

说明：

- `Authorization` 只有配置了 `SyncApiToken` 才会带
- 建议服务端要求 HTTPS
- 建议服务端同时校验 `Bearer Token` 和 `X-QuickReply-ClientId`
- 如果要启用 AI QA 资料库，请额外实现“正式知识卡只读同步”接口，候选库和审核后台不直接暴露给客户端

### 1. 上传知识库增量

```http
POST /api/quickreply/sync/knowledge-delta
Content-Type: application/json
```

请求体模型对应：

- `KnowledgeSyncPayload`
- `KnowledgeCategorySyncItem`
- `KnowledgePhraseSyncItem`
- `KnowledgeQuickTagSyncItem`
- `KnowledgeFileSyncItem`

核心字段：

- `clientId`
- `appVersion`
- `generatedAt`
- `since`
- `isFullSync`
- `categories`
- `phrases`
- `quickTags`
- `fileItems`

服务端最低要求：

- 能接收 JSON
- 成功时返回任意 `2xx`
- 失败时返回非 `2xx`

当前客户端不会读取这个接口的响应体，所以第一版服务端不用急着返回复杂内容。

建议服务端做的事：

- 原样落库一份原始同步记录
- 按 `category.id`、`phrase.id`、`fileItem.id` 做幂等 upsert
- 记录 `clientId`、`generatedAt`、`since`
- 保留最近一次成功同步时间

### 2. 上传问题日志与推荐训练数据

```http
POST /api/quickreply/sync/conversation-delta
Content-Type: application/json
```

请求体模型对应：

- `ConversationSyncPayload`
- `ConversationLogSyncItem`
- `RecommendationEventSyncItem`
- `RecognitionCorrectionSyncItem`

包含三类数据：

1. `conversationLogs`
   记录客户问题、实际发送内容、窗口来源、识别结果、是否成功等
2. `recommendationEvents`
   记录推荐展示、点击、发送、命中关键词、样本数、置信度等
3. `recognitionCorrections`
   记录“识别错了，人工纠正成什么”的样本

服务端最低要求：

- 成功时返回任意 `2xx`
- 按 `id` 做幂等去重
- 允许同一个 `clientId` 多次重传相同时间段数据

建议服务端拆成三张明细表：

- `conversation_logs_raw`
- `recommendation_events_raw`
- `recognition_corrections_raw`

这样后面做统计、回放和 AI 训练都方便。

### 3. 拉取更新清单

```http
GET /api/quickreply/update/manifest?clientId=<id>&appVersion=<version>&knowledgeVersion=<version>
```

返回 JSON 对应：

- `CloudUpdateManifest`

字段如下：

```json
{
  "latestAppVersion": "2.1.10",
  "latestAppDownloadUrl": "https://your-server/downloads/QuickReplyTool-AppUpdate-v2.1.10.zip",
  "latestAppPackageKind": "zip_ota",
  "latestAppSha256": "8A3A5A0F6F4D7E0D98A6D4A2D5597A8C57B251D6D7C2E89AE91D9D8FA8B37F11",
  "latestAppSizeBytes": 73400320,
  "latestAppReleaseChannel": "stable",
  "latestAppMandatory": false,
  "latestAppReleaseNotes": "修复推荐回复统计与同步状态展示，补充云同步联调入口。",
  "knowledgeVersion": "2026-04-04-nightly",
  "knowledgePackageUrl": "https://your-server/knowledge/2026-04-04-nightly",
  "knowledgeSummary": "新增 36 个高频问题聚类，建议补充 12 条快捷回复，当前样本量已达到可接入 AI 辅助回复的阈值。"
}
```

客户端行为：

- `latestAppVersion` 比本地版本新时，提示用户升级
- `latestAppPackageKind = zip_ota` 且下载地址可用时，客户端会走一键 OTA：下载 zip、退出、替换文件、自动重启
- `latestAppPackageKind = installer_exe / external_url` 时，客户端会保守退回到打开下载页
- `knowledgeVersion` 与本地记录不同且不为空时，提示用户查看新的知识分析结果

注意：

- 当前客户端已经支持 `zip_ota` 的在线更新
- 还没有强制升级、自动回滚、后台预下载、知识包自动套用

所以你现在最稳的发布方式是：

- 程序更新：优先上传 `zip_ota` 更新包；只有临时兼容场景才返回安装包或网页地址
- 知识分析：上传一个网页地址、PDF 地址、下载页地址或后台管理页地址

### 4. 拉取已发布知识卡增量

```http
GET /api/quickreply/knowledge/published?sinceRevision=<long>&cursor=<cursor>&limit=<n>
```

这个接口用于客户端同步“已审核、已发布”的 AI QA 资料卡。

建议返回 JSON：

```json
{
  "knowledgeVersion": "knowledge-r12",
  "generatedAt": "2026-04-16T12:00:00+08:00",
  "latestRevision": 12,
  "nextCursor": "",
  "hasMore": false,
  "cards": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "candidateKind": "qa",
      "platform": "wecom",
      "productKey": "SKU-1",
      "question": "这款今天能发吗",
      "answer": "您好，这款今天可以安排发出。",
      "synonyms": ["发货时效"],
      "tags": ["发货"],
      "usageNotes": "仅限现货",
      "version": "v1",
      "revision": 12,
      "updatedAt": "2026-04-16T12:00:00+08:00"
    }
  ],
  "removedCardIds": []
}
```

说明：

- `cards`
  - 本次新增或更新的正式知识卡
- `candidateKind`
  - 现在建议服务端显式返回 `qa / resource`，客户端会按知识类型消费
- `latestRevision`
  - published knowledge 变更流的最新 revision；客户端现在会把它持久化到本地设置中
- `nextCursor / hasMore`
  - 支持客户端分页拉取 published knowledge 增量，而不是假设单次全量返回
- `removedCardIds`
  - 需要从客户端只读缓存中删除的知识卡
- 当运营后台执行“下线已发布知识”后，该接口应通过新的 published revision 把对应卡片 `id` 放入 `removedCardIds`
- 这个接口只下发 `published_knowledge_cards`
- `knowledge_candidates` 和审核队列不直接给客户端

### 4.1 运行态和健康检查接口

当前 `QuickReply.CloudService` 还提供这几个运维/诊断接口：

- `GET /healthz`
  - 最轻量的存活探测
- `GET /readyz`
  - 返回数据库初始化、AI provider 可用性、Codex CLI 状态等运行态摘要
- `GET /version`
  - 返回服务版本、当前 provider、是否启用 loopback bypass 等静态摘要
- `GET /info`
  - 返回更完整的运行态信息；这是管理面接口，真实远程环境应要求鉴权

### 5. 云端审核后台接口

这 3 个接口是服务端后台管理入口，不给桌面端直接使用，但第一期 AI QA 资料库建议一起实现：

- `GET /api/quickreply/knowledge/review/queue`
  - 返回待审核的 `knowledge_candidates`
- `GET /api/quickreply/knowledge/review/{id}/sources`
  - 返回单条候选的来源样本；当前接口也支持 `limit + cursor`
- `GET /api/quickreply/knowledge/review/{id}/history`
  - 返回单条候选的审核历史；当前接口也支持 `limit + cursor`
- `POST /api/quickreply/knowledge/review/{id}/approve`
  - 审核通过后 upsert 到 `published_knowledge_cards`
- `POST /api/quickreply/knowledge/review/{id}/reject`
  - 审核拒绝并记录原因
- `POST /api/quickreply/knowledge/published/{id}/remove`
  - 下线已发布知识，并通过后续 `removedCardIds` 增量通知客户端删除本地缓存
- `POST /api/quickreply/knowledge/jobs/import-logs`
  - 从 `logs.zip` 或 `conversation-logs.db` 提取第一期候选知识
- `POST /api/quickreply/knowledge/jobs/synthesize-drafts`
  - 把 `draft` 候选整理成 `reviewed` 状态的问答草稿
  - 当前本地联调骨架实现为 `Codex CLI` 优先、随后 `OpenAI API key`、`Ollama`，最后启发式兜底
  - 当前实现会先对候选加 lease，再进入综合，避免多 worker 重复处理同一条 draft
- `GET /api/quickreply/knowledge/jobs/runs`
  - 查看最近几批导入/整理任务统计

安全建议：

- `review/*`、`jobs/*`、`ai-auth/*` 都属于管理接口
- `published/{id}/remove` 也属于管理接口
- 真实远程环境应要求 Bearer token 或等价鉴权
- 当前本地联调服务允许 `127.0.0.1 / localhost` loopback 免鉴权，方便桌面端本机审核和 AI 认证 fallback

建议规则：

- 只有 `published_knowledge_cards` 允许客户端同步
- 候选库、审核状态、审核备注只在服务端后台可见
- 审核历史只给后台审核面板消费，不直接下发普通客户端
- 发布后统一刷新对外的 `knowledgeVersion`

## 服务端最小落地方案

如果你想先快速跑通，不要一开始做太复杂，最小方案建议如下。

### 接口层

准备 3 个接口：

- `POST /api/quickreply/sync/knowledge-delta`
- `POST /api/quickreply/sync/conversation-delta`
- `GET /api/quickreply/update/manifest`

### 存储层

至少准备这些表：

- `client_registry`
  - 记录客户、门店、机器、授权状态
- `knowledge_sync_batches`
  - 每次知识库同步的批次信息
- `knowledge_categories`
- `knowledge_phrases`
- `knowledge_quick_tags`
- `knowledge_files`
- `conversation_logs_raw`
- `recommendation_events_raw`
- `recognition_corrections_raw`
- `knowledge_candidates`
- `knowledge_candidate_sources`
- `published_knowledge_cards`
- `knowledge_job_runs`
  - 异步整理任务的批次统计
- `analysis_reports`
  - 每次分析结果摘要
- `release_manifests`
  - 当前对外发布的更新清单

### 分析任务

建议服务端每天夜里跑一次分析任务，按 `clientId` 或客户分组做：

1. 统计客户最常见问题
2. 统计哪些问题没有命中推荐
3. 统计哪些推荐展示了但从来没被采用
4. 统计哪些人工输入文本反复出现，可以沉淀成快捷短语
5. 统计识别纠正样本，看 OCR / 上下文识别哪类错误最多
6. 判断样本量是否足够
7. 把稳定问题聚类整理成 `knowledge_candidates`
8. 由人工审核后发布到 `published_knowledge_cards`

建议每批知识整理任务至少记录这些统计字段：

- `sample_count`
- `candidate_count`
- `publish_count`
- `reject_count`
- `avg_confidence`
- `failed_cluster_count`
- `failed_synthesize_count`

建议给出这些输出字段：

- `dataSufficiency`
  - 如 `insufficient / borderline / sufficient`
- `recommendationReadiness`
  - 是否足以继续优化规则推荐
- `aiReadiness`
  - 是否建议接入 AI 智能回复
- `topCustomerIssues`
- `missingReplySuggestions`
- `lowQualityRecommendations`
- `repeatedManualReplies`
- `recognitionFailurePatterns`
- `suggestedActions`

## “数据够不够”建议怎么判断

你现在 10 天还没出推荐，通常不是代码坏了，而是样本还不够密，或者关键词分散。

服务端可以先用一个保守标准：

- 最近 7 天有效 `conversationLogs` 少于 200 条：明显不够
- 最近 7 天有效 `recommendationEvents` 少于 100 条：推荐评估不稳定
- 同一个问题聚类少于 5 次：不适合直接沉淀固定推荐
- 同一条人工输入回复在 30 天内重复 3 次以上：适合提示沉淀成快捷短语
- 某类识别纠正累计超过 20 次：应该优先修识别规则

这只是第一版经验阈值，后面可以再调。

## 版本发布与更新提示流程

建议你每次发布都按这个顺序做：

1. 在本地修改版本号
2. 打包新的 OTA zip 包
3. 上传 OTA zip 到固定下载地址
4. 更新服务端的 `release_manifests`
5. 把 `latestAppVersion`、`latestAppDownloadUrl`、`latestAppPackageKind`、`latestAppSha256`、`latestAppSizeBytes`、`latestAppReleaseChannel`、`latestAppReleaseNotes` 改成新值
6. 如果这次同时产出了新的知识分析结果，再更新 `knowledgeVersion`、`knowledgePackageUrl`、`knowledgeSummary`
7. 客户下次启动软件时，程序就会自动提示

注意：

- 你现在这套不是“二进制差分补丁更新”
- 是“发现新版本后，客户端在线下载完整 zip 包并自动覆盖安装”
- 普通版本 OTA 与问题补丁 patch 可以并存，不冲突

如果以后你要做真正的差分更新、强制升级或自动回滚，再单独升级这一套，不建议第一阶段同时堆进去。

## 推荐的第一阶段交付顺序

为了尽快看到结果，建议按下面顺序落地。

### 第 1 阶段

- 服务端先把 3 个接口接起来
- 原始数据能稳定入库
- `manifest` 能返回固定 JSON
- 客户端能手动同步成功、能检查到更新

### 第 2 阶段

- 做基础统计页
- 按客户 / 门店查看高频问题
- 输出“建议新增哪些快捷回复”
- 增加 AI QA 候选审核后台
- 审核通过后，客户端能同步正式知识卡

### 第 3 阶段

- 做问题聚类
- 做重复人工回复识别
- 做推荐效果分析

### 第 4 阶段

- 当样本足够后，再决定是否接 AI
- 如果接 AI，优先做“AI 草稿 + 人工确认”，不要一开始就全自动发送
- AI 只引用 `published_knowledge_cards`，不要直接引用候选库

## 联调建议

你现在可以按这个顺序联调：

1. 在客户端设置里填服务器地址、客户端标识、Token
2. 打开“维护与诊断”
3. 点击“立即同步一次”
4. 看服务端有没有收到两类请求
5. 点击“立即检查更新”
6. 改一下服务端返回的 `latestAppVersion` 或 `knowledgeVersion`
7. 确认客户端是否弹出提示

## 结论

你当前最应该做的不是马上接 AI，而是先把下面三件事跑通：

1. 数据稳定上传
2. 服务端每天出分析结论
3. 你能根据分析结论快速补短语、发版本、让客户收到更新提示

这三步跑通后，再谈 AI，成功率会高很多。
