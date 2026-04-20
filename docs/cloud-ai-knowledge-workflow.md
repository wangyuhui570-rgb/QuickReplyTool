# 云端异步 AI QA 资料库工作流

## 目标

这套工作流用于把现有聊天日志、识别纠错和推荐采纳记录，整理成“可审核、可发布、可同步到客户端”的 AI QA 资料库。

第一期原则：

- 客户端只消费“已发布知识卡”
- 云端整理是异步批处理，不影响前台客服工作
- AI 只引用已审核知识，不直接引用候选库
- 不处理 PDF/Word/txt 文档切片，这部分留到下一阶段
- 当前仓库已提供一个本地联调骨架：`src/QuickReply.CloudService`

## 数据来源

第一期只使用这些稳定来源：

- `conversation_logs_raw`
  - 只保留识别文本可靠、最终发送成功的样本
- `recognition_corrections_raw`
  - 只保留人工修正后有明确客户问题/上下文的样本
- `recommendation_events_raw`
  - 重点使用 `accepted`、`sent` 这类真正被采用的记录

不建议第一期直接把这些内容作为知识发布：

- 低置信度识别结果
- 失败发送记录
- 未采用推荐
- 未人工确认的 AI 草稿

## 云端数据模型

### `knowledge_candidates`

候选知识，不直接下发客户端。

第一期建议至少区分两类候选：

- `qa`
  - 标准问答卡，要求能整理成完整自然语言问题
- `resource`
  - 资料/教程/下载/安装说明类卡片，不和标准问答混用

建议字段：

- `id`
- `status`
  - `draft / reviewed / approved / rejected / archived`
- `platform`
- `product_key`
- `question_normalized`
- `answer_draft`
- `answer_final`
- `tags_json`
- `confidence`
- `risk_level`
- `source_count`
- `cluster_key`
- `created_at`
- `updated_at`

### `knowledge_candidate_sources`

记录候选知识来自哪些原始样本。

建议字段：

- `id`
- `candidate_id`
- `source_type`
  - `conversation / correction / recommendation_accept`
- `source_id`
- `excerpt`
- `created_at`

### `published_knowledge_cards`

这是客户端唯一会同步和引用的正式知识表。

建议字段：

- `id`
- `candidate_kind`
  - `qa / resource`
- `platform`
- `product_key`
- `question`
- `answer`
- `synonyms_json`
- `tags_json`
- `usage_notes`
- `version`
- `updated_at`

## 异步处理流程

### 1. extract

从原始同步表中抽样本。

建议过滤条件：

- `conversation_logs_raw.result_success = true`
- `recognition_confidence >= 0.45`
- `recognized_customer_text` 非空
- 识别到的文本不能只是窗口标题或平台噪声
- 不能只是寒暄、缩写、单个内部关键词或低价值搜索词
- 教程/下载/视频/安装说明类样本应标成 `resource`，不要混入 `qa`

### 2. cluster

先按下面维度分桶，再做问题归一化聚类：

- `platform`
- `product_key`
- `question_normalized`

目标是避免：

- 企业微信和拼多多的问题混在一起
- 不同商品售后规则混在一起

### 3. synthesize

由 AI 为每个 cluster 生成一张知识候选卡。

至少产出：

- 标准问题
- 标准答案
- 同义问法
- 标签
- 风险等级
- 证据摘要

注意：

- AI 只能整理候选，不能直接发布
- 高风险问题默认要求人工编辑
- `qa` 候选应先经过“完整问题 + 足够证据”门槛再进 AI 综合
- `resource` 候选优先走保守整理，不要求模型强行改写成问答
- 当前服务端支持 4 种综合 provider：
  - `codex_cli`
  - `openai_api`
  - `ollama`
  - `auto`
- `codex_cli` 是当前推荐默认值：
  - 通过官方 `codex login` / `codex login status` / `codex logout` 管理登录态
  - 通过官方 `codex exec` 执行异步综合
  - 优先复用本机已登录的 ChatGPT / Codex CLI 会话
- `openai_api` 仍保留，但它只是 API key 后备路径，不是默认模式。
- `auto` 的推荐语义是：优先使用已登录的 `codex_cli`；若 CLI 不可用，再回退到已配置的 `openai_api`；若仍不可用，再回退到本地 `ollama`；最后才退回启发式综合。
- 说明：
  - `codex_cli` 属于官方支持能力的复用：登录、状态检测、退出登录和非交互 `codex exec` 都走官方 CLI。
  - `openai_api` 属于官方公开 API：使用标准 OpenAI API key 和 Responses API。
  - 当前服务端不自己实现私有 OAuth，也不声称直接复用 ChatGPT 桌面版或未文档化订阅额度接口。

### 4. review / publish

人工审核后台只做两件事：

- `approve`
  - 写入 `published_knowledge_cards`
- `reject`
  - 保留候选和原因，不下发客户端

## 客户端只读同步约定

第一期客户端通过这个接口同步正式知识：

```http
GET /api/quickreply/knowledge/published?sinceRevision=<long>&cursor=<cursor>&limit=<n>
Accept: application/json
X-QuickReply-ClientId: <SyncClientId>
X-QuickReply-AppVersion: <当前程序版本>
Authorization: Bearer <SyncApiToken>
```

建议返回：

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
      "synonyms": ["发货时效", "今天发货"],
      "tags": ["发货", "时效"],
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
  - 本次新增或更新的正式知识
- `removedCardIds`
  - 本次需要从客户端缓存中移除的正式知识
- `knowledgeVersion`
  - 当前正式知识发布版本
- `latestRevision`
  - 当前服务端已发布知识变更流的最新 revision
- `nextCursor / hasMore`
  - 用于客户端分页拉取 published knowledge 增量
- 当已发布知识被人工下线时，服务端应通过新的 revision 变更把对应 `id` 放进 `removedCardIds`，客户端再按增量删除本地只读缓存

## 审核后台接口建议

第一期审核和发布都在云端后台完成，客户端不直接访问这些接口，但服务端实现时建议固定这 3 个入口：

### 1. 获取审核队列

```http
GET /api/quickreply/knowledge/review/queue
Accept: application/json
Authorization: Bearer <后台审核 Token>
```

说明：

- `review/*`、`jobs/*`、`ai-auth/*` 都应视为控制面接口
- 这些接口在真实远程服务上不应匿名暴露
- 当前本地联调骨架允许 loopback 免鉴权，便于桌面端本机 fallback

建议返回字段：

- `items`
  - 当前页候选
- `nextCursor`
- `hasMore`
- `items[*].id`
- `items[*].status`
- `items[*].candidateKind`
- `items[*].platform`
- `items[*].productKey`
- `items[*].questionNormalized`
- `items[*].answerDraft`
- `items[*].tags`
- `items[*].confidence`
- `items[*].riskLevel`
- `items[*].sourceCount`
- `items[*].clusterKey`
- `items[*].evidenceSummary`
- `items[*].updatedAt`

### 2. 审核通过

```http
POST /api/quickreply/knowledge/review/{id}/approve
Content-Type: application/json
Authorization: Bearer <后台审核 Token>
```

建议请求体至少包含：

- `answerFinal`
- `synonyms`
- `tags`
- `usageNotes`
- `reviewComment`
- `version`

通过后：

- 候选状态改成 `approved`
- upsert 到 `published_knowledge_cards`
- 生成新的 `knowledgeVersion`
- 写入一条审核历史事件，记录 reviewer、reviewSessionId、before/after snapshot 和审核备注

### 3. 审核拒绝

```http
POST /api/quickreply/knowledge/review/{id}/reject
Content-Type: application/json
Authorization: Bearer <后台审核 Token>
```

建议请求体至少包含：

- `reason`
- `reviewComment`

拒绝后：

- 候选状态改成 `rejected`
- 保留原始来源和审核意见，方便后续复盘
- 写入一条审核历史事件，记录 reviewer、reviewSessionId、拒绝原因和审核备注

### 4. 下线已发布知识

```http
POST /api/quickreply/knowledge/published/{id}/remove
Content-Type: application/json
Authorization: Bearer <后台审核 Token>
```

建议请求体至少包含：

- `reason`
- `reviewComment`
- `reviewer`
- `reviewSessionId`

处理建议：

- 从 `published_knowledge_cards` 删除当前正式知识
- 追加一条 published change revision，并把该 `id` 计入后续 `removedCardIds`
- 将对应候选归档为 `archived`（若候选仍存在）
- 写入一条 `unpublished` 类型的审核历史事件

### 5. 审核历史

```http
GET /api/quickreply/knowledge/review/{id}/history?cursor=<cursor>&limit=<n>
Authorization: Bearer <后台审核 Token>
```

建议返回字段：

- `candidateId`
- `items[*].id`
- `items[*].actionType`
  - `approved / rejected / unpublished`
- `items[*].reviewer`
- `items[*].reviewSessionId`
- `items[*].reviewComment`
- `items[*].reviewReason`
- `items[*].beforeSnapshotJson`
- `items[*].afterSnapshotJson`
- `items[*].createdAt`
- `nextCursor`
- `hasMore`

## 本地联调骨架当前已实现的任务入口

`src/QuickReply.CloudService` 目前除了审核与发布接口，还自带这几个任务入口：

- `GET /api/quickreply/ai-auth/status`
  - 查看本机 Codex CLI 是否可用，以及当前登录模式（ChatGPT / API key / 未登录）
- `POST /api/quickreply/ai-auth/login`
  - 触发官方 Codex CLI 登录流程；默认走 `--device-auth`
- `POST /api/quickreply/ai-auth/logout`
  - 触发官方 Codex CLI 退出登录
- `POST /api/quickreply/knowledge/jobs/import-logs`
  - 输入 `logs.zip` 或 `conversation-logs.db`
  - 按第一期规则先做 `extract + cluster`
- `POST /api/quickreply/knowledge/jobs/synthesize-drafts`
  - 把当前 `draft` 候选整理成 `reviewed` 草稿
  - 当前实现为 `Codex CLI` 优先，随后是 `OpenAI API key`、`Ollama`，最后启发式兜底
  - 当前实现会先对 `draft` 候选加 lease，再进入综合，避免并发 worker 重复处理同一批候选
- `GET /api/quickreply/knowledge/jobs/runs`
  - 查看最近任务统计

当前这条 `synthesize` 会优先调用本机已登录的 `Codex CLI` 生成候选问答草稿；如果 CLI 不可用或未登录，则再看是否配置了 `OpenAI API key`；若仍不可用，则回退到本地 `Ollama`，最后才退回启发式整理器，保证审核流不中断。

审核动作当前会记录 `reviewer`、`reviewSessionId`、审核原因/备注以及 `before/after snapshot` 这类最小审计元数据，供审核历史接口分页读取。

## 客户端消费规则

客户端只做这些事：

- 把 `published_knowledge_cards` 缓存到本地 SQLite
- 在搜索浮层打开时，按当前客户消息命中知识卡
- AI 草稿生成时，把“短语候选 + 已发布知识卡”一起作为 grounding

客户端不做：

- 本地审核
- 本地编辑正式知识
- 直接引用 `knowledge_candidates`

## 审核与发布建议

第一期推荐这样落地：

- 每天或每几小时跑一次异步整理任务
- 审核员只需要处理高置信度候选
- 发布时打 `knowledgeVersion`
- 客户端在下一次云同步时拉取正式知识卡增量

建议每批异步任务额外记录这些批处理指标，便于后续评估资料库质量：

- `sample_count`
- `candidate_count`
- `publish_count`
- `reject_count`
- `avg_confidence`
- `failed_cluster_count`
- `failed_synthesize_count`

## 风险控制

这些内容不建议自动发布：

- 赔付、退款、补偿承诺
- 平台政策解释
- 时效/库存/价格类敏感答复
- 来自低置信度 OCR 的问题

第一期默认：

- AI 生成候选可以快
- 正式发布必须保守
