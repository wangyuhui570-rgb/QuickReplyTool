# 版本 1.2 说明

## 版本目标

在 `1.1` 的 SQLite 本地持久化基础上，补齐真正可用的“输入功能”：

1. 选择短语
2. 点击“输入到当前窗口”
3. 程序切回最近的外部输入窗口
4. 将短语内容通过剪贴板 + `Ctrl+V` 输入进去
5. 尽量恢复用户原来的剪贴板

## 本阶段实现

- 新增“输入到当前窗口”按钮
- 新增输入服务模块 `InputActionService`
- 新增剪贴板服务模块 `ClipboardService`
- 新增键盘模拟模块 `KeyboardInputSimulator`
- 输入前自动备份剪贴板
- 粘贴后尽量恢复原剪贴板
- 当前未选中短语或短语内容为空时给出提示
- 双击短语列表时也会执行“输入到当前窗口”

## Windows 方案说明

### 剪贴板方案

- 使用 WPF 的 `System.Windows.Clipboard`
- 输入前先读取并克隆当前剪贴板数据
- 再把短语文本写入系统剪贴板
- 输入动作完成后尝试把备份内容恢复回去
- 对剪贴板操作增加了简单重试，降低“剪贴板正在被占用”导致的失败概率

### 按键模拟方案

- 主路径使用 `SendKeys.SendWait("^v")`
- 兜底路径使用 Win32 `SendInput`
- 目标是提高在记事本、浏览器输入框、微信等常见输入场景中的兼容性

### 焦点窗口方案

由于用户点击本工具按钮时，焦点会先回到本工具自身，因此本阶段采用“最近外部前台窗口跟踪”方式：

1. 用户先点一下目标输入框
2. 工具记住最近的外部窗口
3. 点击“输入到当前窗口”时，工具自动切回该窗口并执行粘贴

## 核心代码入口

- [InputActionService.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/Services/InputActionService.cs)
- [ClipboardService.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/Services/ClipboardService.cs)
- [KeyboardInputSimulator.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/Services/KeyboardInputSimulator.cs)
- [MainViewModel.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/ViewModels/MainViewModel.cs)

## 验证方式

1. 先打开记事本、微信输入框或浏览器输入框
2. 在目标输入框里先点一下
3. 回到工具选择一条短语
4. 点击“输入到当前窗口”
5. 检查是否成功粘贴，以及原剪贴板是否尽量恢复
