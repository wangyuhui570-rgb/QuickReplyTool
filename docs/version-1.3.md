# 版本 1.3 说明

## 版本目标

在 `1.2` 的“输入到当前窗口”基础上，补齐真正可配置的“发送功能”：

1. 选择短语
2. 点击“发送到当前窗口”
3. 程序切回最近的外部输入窗口
4. 写入剪贴板并发送 `Ctrl+V`
5. 短暂延迟
6. 再按当前配置发送 `Enter / Ctrl+Enter / Alt+S`

## 本阶段实现

- 新增“发送到当前窗口”按钮
- 发送前检查是否已选中短语
- 支持 `Enter / Ctrl+Enter / Alt+S` 三种发送方式
- 新增发送前延迟配置
- 发送配置持久化到本地
- 输入与发送统一收口到 `MessageComposeService`

## 核心代码入口

- [MessageComposeService.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/Services/MessageComposeService.cs)
- [SqliteAppDataStore.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/Services/SqliteAppDataStore.cs)
- [MainViewModel.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/ViewModels/MainViewModel.cs)
- [MainWindow.xaml](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/MainWindow.xaml)

## 验证方式

1. 在记事本里先点一下输入位置
2. 选择一条短语，点击“发送到当前窗口”
3. 观察是否表现为“粘贴后回车”
4. 切换成 `Ctrl+Enter` 和 `Alt+S` 再测
5. 修改发送延迟和发送方式后关闭程序重启，确认配置仍保留
