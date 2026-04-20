# 版本 1.1 说明

## 版本目标

在不推翻 `1.0` 界面结构的前提下，把原来的演示型本地数据改造成真正可持久化的 SQLite 存储。

## 本阶段实现

- 引入 SQLite 数据库
- 首次启动自动建库
- 自动插入基础测试数据
- 分类列表从数据库读取
- 短语列表从数据库读取
- 右侧短语内容编辑区从数据库读取
- 新增分类写入数据库
- 新增短语写入数据库
- 编辑短语写回数据库
- 删除短语写回数据库
- 删除分类前校验是否存在短语

## 核心数据库表

### `categories`

存储客服回复分类。

### `phrases`

存储具体短语内容，并通过 `category_id` 关联到分类。

## 核心代码入口

- [SqliteAppDataStore.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/Services/SqliteAppDataStore.cs)
- [MainViewModel.cs](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/ViewModels/MainViewModel.cs)
- [MainWindow.xaml](C:/Users/Mayn/Desktop/回复效率工具/src/QuickReply.Desktop/MainWindow.xaml)

## 验证方式

1. 启动程序，确认自动生成 SQLite 数据库文件
2. 新增一个分类并新增几条短语
3. 修改短语标题、标签、内容
4. 删除一条短语
5. 关闭程序再重新打开，确认数据仍存在
6. 尝试删除仍有短语的分类，确认程序提示不允许直接删除
