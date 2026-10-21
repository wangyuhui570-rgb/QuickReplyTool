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

当前版本是**纯本地工具**：不包含（已移除的云同步）、知识审核服务、OTA 自动更新或远端问题上报。

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
- 粘贴完成后尽量恢复原剪贴板
- 对剪贴板访问增加简单重试，降低被其他程序暂时占用时的失败概率

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
