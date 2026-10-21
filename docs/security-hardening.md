# 安全加固说明（2026-10）

本次加固修复了代码审查中发现的问题。下面列出需要在服务器或发布流程上配合完成的操作。

## 1. 管理接口本机免鉴权

- 客户端代码已修复：不再根据 `Host` 请求头判断是否为本机访问，只信任 TCP 对端 IP；带 `X-Forwarded-For`、`X-Real-IP`、`Forwarded` 头的请求（经过反向代理的请求）一律要求 Token。
- **服务器上必须操作**：线上通过 nginx 反向代理到 `127.0.0.1:18080`。nginx 默认不会添加 `X-Forwarded-For` 头，所以线上请保持下面这项配置为关闭：

  ```
  KnowledgeCloud__AllowLoopbackWithoutAuth=false
  KnowledgeCloud__ManagementApiToken=<新的随机 Token>
  ```

  桌面端自带的本地云服务（`127.0.0.1:5128`）不经过代理，仍然可以免 Token 访问，不受影响。
- **Token 轮换**：`docs/patch-release-workflow.md` 的历史版本中出现过明文 Token，已从文档中移除，但它仍保留在 Git 历史里。请在服务器上更换为新 Token，例如用 `openssl rand -hex 32` 生成。

## 2. 程序更新校验

- 程序正式更新（`app_update`）现在要求更新清单提供 `latestAppSha256`，否则拒绝自动安装。`build-app-update-package.ps1` 已经会自动写入这个字段。
- 新增更新清单签名，用来防止明文 HTTP 链路上的中间人同时替换更新包和 SHA256：

  1. 生成密钥（只需一次，私钥不要放进仓库）：
     ```powershell
     powershell -ExecutionPolicy Bypass -File .\scripts\new-update-signing-key.ps1 -PrivateKeyPath D:\keys\quickreply-update.signing-key.xml
     ```
  2. 把输出的公钥 XML 粘贴到 `src/QuickReply.Desktop/Services/UpdateManifestSignatureVerifier.cs` 的 `TrustedPublicKeyXml`。
  3. 发布时带上私钥：
     ```powershell
     .\scripts\publish-app-update-release.ps1 ... -SigningKeyPath D:\keys\quickreply-update.signing-key.xml
     ```

  在 `TrustedPublicKeyXml` 为空之前，签名校验不会启用，旧流程不受影响。填入公钥后，没有签名或签名无效的程序更新都会被拒绝。请先发布一个带公钥、并且已签名的版本，再依赖它保护后续更新。
- 推荐同时给更新服务器配置 HTTPS（域名加 Let's Encrypt），然后把 `UpdateCheckService.DefaultUpdateServerBaseUrl` 改为 `https://`。这样同步 Token 也不会被明文传输。
- 尚未覆盖：问题补丁（issue patch）接口目前不返回 SHA256，补丁下载仍然没有完整性校验。需要服务端在 `/api/quickreply/issues/status` 的返回中增加 `patchSha256` 字段后，再在客户端启用校验。

## 3. 其他稳定性改动

- SQLite：`quick-reply.db` 和 `conversation-logs.db` 启用 WAL，主库设置 `busy_timeout=5000`。
- 对话日志：建表和列迁移只在每个进程中执行一次。原来每写一条日志都要执行约 50 次 `PRAGMA table_info`。
- 托盘菜单："刷新问题状态"、"重试问题上传"、"生成问题记录"、"应用补丁"改为异步执行，网络慢时界面不再卡死。
- HTTP：所有服务共用一个连接池（`SharedHttpMessageHandler`）。
- 仓库：移除了提交进来的 `quickreply-cloud.db` 和 `__pycache__`，并加入 `.gitignore`。
