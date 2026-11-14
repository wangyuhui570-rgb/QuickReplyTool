# QuickReplyTool

一个面向 Windows 客服场景的本地快捷回复桌面工具。当前版本聚焦短语管理、文件资料发送、搜索浮层和本地设置，整体保持轻量、直接的本地工作流。

## 项目结构说明

```text
回复效率工具.sln
README.md
docs/
src/
  QuickReply.Core/      共享模型与无 UI 业务逻辑
  QuickReply.Desktop/   WPF 本地桌面端
tests/
  QuickReply.Desktop.Tests/
scripts/                安装包 / 便携包等本地发布脚本
packaging/
截图工具源码/
```

当前版本是**纯本地工具**：不包含云同步、知识审核服务或远端问题上报。绿色版启动时会检查 GitHub Releases；发现更高版本并经确认后，下载便携包、校验、替换 `app` 文件夹并重启。设置和话术仍在 `%AppData%\QuickReplyTool`，不会被覆盖。

## 正式发布

版本号以 `src/QuickReply.Desktop/QuickReply.Desktop.csproj` 的 `<Version>` 为准（当前 2.1.36）。合并到 `main` 之后，在该提交上推送标签 `v2.1.36`（必须是 `vX.Y.Z`，不要带 `-test` 后缀）。`.github/workflows/release.yml` 会：

- 用 `scripts/build-portable.ps1` 打出 `QuickReplyTool-Portable-2.1.36.zip`（根目录下有 `app/QuickReplyTool.exe`）
- 用 `scripts/build-installer.ps1` 打出 `QuickReplyTool-Setup-v2.1.36.exe` 安装包，并附带 SHA256 校验文件
- 创建非草稿、非预发布的 GitHub Release，说明里写 `SHA256: <64位小写>`，更新器只认这种写法
- 用同一份信息覆盖 `update-manifest` 发布里的 `latest.json`（这个发布不能删），并用仓库密钥 `UPDATE_SIGNING_KEY` 给它签名

- 用 `scripts/build-update-patch.ps1` 和上一个正式版比较，打出只含变化文件的 `QuickReplyTool-Patch-<上一版>-to-<本版>.zip`（通常不到 1 MB），说明里写 `PatchSHA256:`，`latest.json` 里写 `patch`（单独签名）。这一步失败不影响发布，客户端会下完整包

装的正好是上一版时，客户端只下补丁：复制当前 `app` 到临时目录、覆盖变化文件、按补丁里的清单逐个核对新版所有文件的 SHA256，全部一致才替换；任何一步不对就改下完整包。跨版本升级也下完整包。

客户端先直连 GitHub；连不上时才经 `ghfast.top` 镜像取 `latest.json`，而镜像来的清单必须带有效签名（ECDSA P-256，公钥在 `AppUpdatePolicy.ManifestSigningPublicKey`），且下载地址必须在本仓库的 Releases 里，否则不认。没配 `UPDATE_SIGNING_KEY` 时清单不带签名，只能直连 GitHub 的用户才能更新。

已安装的 2.1.15 客户端比较的是程序集版本和标签里的数字。标签、zip 文件名或说明里的 SHA256 对不上时，检查会跳过这次发布，继续停在旧包上。

## 数据存储

SQLite 数据库默认位于 `%AppData%\\QuickReplyTool\\quick-reply.db`。

当前主要使用这些本地数据：

- `categories`
- `phrases`
- `app_settings`

其中 `categories` 和 `phrases` 保存短语内容，`app_settings` 保存发送方式、窗口行为、界面尺寸和本地路径等配置。

## 关键模块说明

- `src/QuickReply.Desktop/Services/SqliteAppDataStore.cs`
  统一封装 SQLite 建库、建表和本地配置持久化。
- `src/QuickReply.Desktop/Services/MessageComposeService.cs`
  负责剪贴板写入、切回目标窗口、粘贴、发送和恢复剪贴板。
- `src/QuickReply.Desktop/Services/ClipboardService.cs`
  封装剪贴板读写、备份、恢复和重试策略。
- `src/QuickReply.Desktop/Services/KeyboardInputSimulator.cs`
  封装 `Ctrl+V / Enter / Ctrl+Enter / Alt+S` 的按键模拟。
- `src/QuickReply.Desktop/ViewModels/MainViewModel.cs`
  承担主界面状态与命令协调。
- `src/QuickReply.Desktop/MainWindow.xaml`
  主界面布局和交互入口。

## Windows 输入和发送方案说明

### 剪贴板方案

- 使用 WPF 的 `System.Windows.Clipboard`
- 发送或输入前先备份当前剪贴板
- 将短语内容写入系统剪贴板
- 粘贴完成后尽量恢复原剪贴板；图片按原始字节备份（不解码），截图不会因为发了一条短语而丢失
- 对剪贴板访问增加简单重试，降低被其他程序暂时占用时的失败概率
- 恢复前在每次重试里都重新确认剪贴板仍是本工具写入的内容，用户中途复制的新内容不会被旧内容覆盖
- 连续发送时，后一次接管前一次还没执行的恢复，最终恢复的是用户自己原来的剪贴板，而不是上一条短语
- 图文分步粘贴时，等目标软件真正读走上一段（文字）再换成下一段（图片）：千牛、咚咚、拼多多是在处理 Ctrl+V 时才读剪贴板，换得太早会把图片贴两遍、文字丢失。临时内容用 OLE 延迟提供，能看到每次读取；但 Ctrl+V 后最先那次读取往往只是软件对"剪贴板变了"的响应，不是粘贴本身，所以粘贴后至少隔 0.45 秒、且最后一次读取后安静 0.15 秒才换下一段；没看到粘贴后的读取时等到 0.7～0.9 秒。发送日志的 `pasteGapLog` 记录每一段前的等待

### 抖店发送方案

- 发送图片和人工操作一样：先点一下聊天输入框（不点的话抖店不响应粘贴），Ctrl+V 弹出"发送图片"确认框，从粘贴起共留 0.9 秒给确认框（含等待剪贴板读取的时间），再按回车确认；不再去点工具栏的图片按钮
- 图片阶段不再做 Ctrl+A / Ctrl+C / Esc 的检查（Esc 会把确认框关掉，图片就没发出去）；点输入框只按坐标点击，不再用 UI Automation 遍历页面
- 发送开始时只切一次抖店窗口；之后任何一步发现用户已切到别的软件就停下来，不再抢焦点，也不再按键或点击

### 搜索与短语变量

- 搜索支持空格分隔多个关键词（都要命中），以及拼音首字母（输入 `th` 找"退货"，只匹配标题和标签），结果里命中的关键词会高亮
- 搜索浮层每页 9 条，数字键 1–9 选当前页；`←/→`（或 `PageUp/PageDown`）翻页，`Ctrl+←/→` 切换短语/文件/混合搜索
- 短语里写 `{单号}` 或 `{天数=3}` 就是变量：选中后弹出填写框，Tab 切换、Enter 发送、Esc 取消；不改就用默认值，没有默认值的必须填；每次都从默认值开始，不记住上次填的内容

### 按键模拟方案

- 粘贴快捷键优先使用 `SendKeys.SendWait("^v")`
- 如果 `SendKeys` 失败，则兜底使用 Win32 `SendInput`
- 发送键支持三种模式：
  - `Enter`
  - `Ctrl+Enter`
  - `Alt+S`

### 焦点窗口方案

点击本工具按钮时，焦点会先回到本工具自身，所以当前实现仍采用“最近外部前台窗口跟踪”方式：

1. 用户先点一下目标输入框
2. 工具记录最近的外部窗口
3. 点击“输入到当前窗口”或“发送到当前窗口”时，工具切回该窗口执行后续动作

### 发送延迟方案

- 发送流程是：写剪贴板 -> `Ctrl+V` -> 等待发送延迟 -> 发送键
- 发送延迟可配置，默认值保守设置为 `160ms`
- 配置保存在本地，下次启动仍保留

## 本阶段已完成内容

- 保留 V1.2 的输入能力
- 新增“发送到当前窗口”按钮
- 发送方式支持 `Enter / Ctrl+Enter / Alt+S`
- 新增发送延迟配置
- 发送配置会保存到本地并在下次启动时恢复
- 输入和发送已统一走同一个消息投递服务
- 选中短语校验、异常提示已保留并补齐

## 如何运行

开发运行：

```powershell
dotnet build QuickReplyTool.slnx
dotnet run --project .\src\QuickReply.Desktop\QuickReply.Desktop.csproj
```

如果只想双击测试，建议使用这一版发布程序：

- [QuickReplyTool.exe](C:/Users/Mayn/Desktop/回复效率工具/dist/QuickReplyTool-v1.3/QuickReplyTool.exe)

## 如何生成安装包

安装包发布：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
```

生成结果默认输出到：

`artifacts\installer\QuickReplyTool-Setup-<版本号>\installer\QuickReplyTool-Setup-v<版本号>.exe`

固定查找规则：

- 安装包 `exe` 固定在：`artifacts\installer\QuickReplyTool-Setup-<版本号>\installer\`
- 发布后的程序文件固定在：`artifacts\installer\QuickReplyTool-Setup-<版本号>\app\`

当前这次 `2.1.9` 的实际安装包路径：

`C:\Users\24173\Desktop\回复效率工具\回复效率工具\artifacts\installer\QuickReplyTool-Setup-2.1.9\installer\QuickReplyTool-Setup-v2.1.9.exe`

以后如果只是不记得文件放哪，优先直接打开这个总目录：

`C:\Users\24173\Desktop\回复效率工具\回复效率工具\artifacts\installer`

## 更新与发布文档

如果要接更新清单或发布安装包、补丁包，可以查看 `docs/` 下的发布文档。

## 如何初始化开发环境

补齐当前仓库需要的开发工具：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\bootstrap-dev-env.ps1
```

如果还想顺带安装常用辅助工具（如 `fd`、`bat`、`fzf`、`VS Code`）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\bootstrap-dev-env.ps1 -IncludeOptionalTools
```

## 如何修复中文乱码

如果机器上还会用到 Windows PowerShell 5.1，建议执行一次：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\fix-text-encoding.ps1
```

这条脚本会做两件事：

- 把仓库里关键文本文件统一成 `UTF-8 BOM`
- 给 `Windows PowerShell 5.1` 和 `PowerShell 7` 都写入 UTF-8 控制台配置

这样在终端里查看、编辑、运行脚本时，中文更不容易再变成乱码。

## 验收建议

1. 先在记事本、微信输入框或浏览器输入框里点一下输入位置。
2. 回到工具选择一条短语。
3. 点击“发送到当前窗口”。
4. 检查是否先粘贴，再按当前发送方式发送。
5. 切换 `Enter / Ctrl+Enter / Alt+S` 后重复测试。
6. 修改发送延迟并重启程序，检查配置是否仍然保留。

## 下阶段适合如何扩展

- 1.4：增加导入导出、标签筛选和短语排序
- 1.5：增加托盘常驻、全局热键呼出和快速搜索面板
- 2.0：增加变量模板、文件模板、日志和更多客服场景能力


## 短语导出与图片检查

- 导出 XLS 或 CSV 时，图片保存在表格旁边带唯一标识的 `<表格名>_assets_<标识>` 目录。移动备份时，请同时携带表格和它引用的图片目录。
- 重复导出保留旧图片目录，方便旧表格继续读取。导出失败时保留原表格及其配图。
- 话术声明的图片缺失时，发送、输入、复制和导出会提示补全图片。

- 新导出的 XLS 和 CSV 包含独立的“检索标签”列，空标签保持为空。旧文件没有此列时，继续用备注标签填充检索标签。
- 短语发送次数与文件使用次数仅在对应操作成功后记录，取消或失败保留原次数和时间。

- 导入涉及多个同名分类时，先选择归属。选择框显示分类在列表中的位置、话术数量和示例。取消选择时，话术和图片均不写入。

- 文件库一次同步中的新增和移除在同一事务中提交，失败时保留原记录。重复或并发加入相同路径会复用数据库中的文件标识。
- 搜索结果批量刷新，结果相同时保留列表。键入后同步更新结果和选中项。
