# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 沟通语言

**始终用简体中文**回答（对话、解释、提交说明）。代码注释可保留英文，但**组件、样式相关的代码尽量写中文注释**，方便用户自己调样式。

## 项目定位

Mica 是 Windows 原生 Markdown 笔记应用。核心是**流畅的所见即所得书写体验（对标 Typora）+ 轻量的文件夹级笔记管理（文件系统即管理系统，类似 Obsidian）**。

**明确不做**：知识图谱、反向链接、插件系统、数据库。保持简单。功能上偏不确定时优先「砍」而不是「加」。

技术栈：WinUI 3（Windows 原生，承载所有 UI）+ WebView2 加载 Milkdown 编辑器（Web 仅用于编辑器本身）。视觉遵循 Fluent Design 2，窗口用 Mica 材质。

## 构建与运行

```powershell
# 构建（WinUI 3 必须指定 x64）
dotnet build Mica.sln -c Debug -p:Platform=x64

# 运行
dotnet run --project Mica/Mica.csproj -c Debug -p:Platform=x64
```

- **改了 web（ts/css）后重跑 `dotnet build`/`dotnet run` 即可**：`BuildEditor` 目标（BeforeBuild）自动跑 `build/build-editor.ps1`（`pnpm install && pnpm build` → 拷 `web/dist/*` 到 `Mica/Assets/Editor/` → Content glob 进 bin）。**别只单独 `pnpm build`**：那只生成 `web/dist`、不进 bin，看不到变化。
- 单独调 web：`cd web; pnpm dev`（Vite 开发服务器）。主产物用固定文件名（`assets/editor.js`/`editor.css`，见 `web/vite.config.ts`），无 stale hash 问题；代码块语言语法由 `@codemirror/language-data` 按需 `import()`，会码分割出一堆 `assets/*.js` 小 chunk（懒加载，不进主包）。
- 若手动删了 `Mica/Assets/Editor/`（或**新增/删了语言 chunk 等资源文件**），需 build 两次：MSBuild 的 Content glob 在项目加载时已评估，BeforeBuild 阶段新生成的文件本次不收录、下次构建才拷进 bin。
- **不写测试**：保证编译通过即可，功能测试由用户完成。

### 打包分发（绿色 exe，非 MSIX）

```powershell
powershell -ExecutionPolicy Bypass -File build\pack.ps1   # 产出 dist\Mica.zip，解压即 Mica\Mica.exe
```

- **绝不用 `dotnet publish`**：未打包（unpackaged）WinUI 3 的 publish 管线会**丢掉 `Mica.pri`（XAML 资源索引）和 `Assets\Editor`**，导致 exe 启动即在原生层崩溃——双击无反应、命令行零输出、连 Serilog 都没来得及写日志。`pack.ps1` 改用「自包含 build 的输出目录」当交付物（已含 PRI + 编辑器 + 运行时），并校验 PRI/Editor 存在、剔除 pdb 与坏 publish 子目录、复制成 `Mica\` 后压缩（避免多一层 `win-x64`）。
- **`PublishTrimmed` 必须为 false**（csproj 已硬设）：一旦开启裁剪，System.Text.Json 会自动关闭反射序列化（写进 runtimeconfig，连 `dotnet run` 都生效），`SettingsService` 读写 `settings.json` 直接抛 `InvalidOperationException`。WinUI + 反射式 JSON 本就不支持裁剪。
- **`WindowsAppSDKSelfContained=true`**（csproj，仅 Release）：把 Windows App Runtime 一起打进输出，否则目标机器须预装该运行时（开发机有、同学的纯净系统没有 → 表现为双击无反应）。需配合 `-r win-x64 --self-contained` 构建，`pack.ps1` 已带这些参数。
- **PowerShell 脚本含中文必须存为 UTF-8 带 BOM**：Windows PowerShell 5.1 默认按系统 GBK 码页读 `.ps1`，无 BOM 的中文会乱码致解析失败（纯英文脚本如 `build-editor.ps1` 不受影响）。

## 架构大图

### 单项目结构

`Mica/` 是唯一的 .NET 项目（RootNamespace `Mica`，未打包应用 `WindowsPackageType=None`）。`web/` 是独立的 Vite + TypeScript 编辑器，构建产物嵌入主项目。

- `Mica/Models/` — `FileNode`（文件树节点）、`Notebook`、`OutlineItem`，均 `INotifyPropertyChanged`
- `Mica/Services/` — `FileService`（纯文件系统读写）、`SettingsService`（`settings.json` 全局状态）
- `Mica/Controls/` — `EditorHostView`（WebView2 容器）、`ResizeGrip`（侧栏拖拽条）
- `Mica/IPC/` — JSON-RPC 2.0 通信层，`Handlers/` 内是各消息处理器
- `Mica/MainWindow.*` — 主窗口（见下「MainWindow 拆分」）
- `Mica/Views/SettingsPage.xaml(.cs)`、`NotebookHomePage.xaml(.cs)`、`NotebookDetailPage.xaml(.cs)` — **三个覆盖页都已拆为独立 UserControl**（非 partial，2026-06 从 MainWindow 拆出）。各自管视图逻辑，跨页副作用经事件+回调交回 MainWindow（在其构造函数接线）。MainWindow.xaml 里**仍用原 x:Name**（`SettingsView`/`NotebookHomeView`/`NotebookDetailView`），故 `Show*Internal` 显隐逻辑不变。详见下「覆盖页解耦」
- DI：`App.xaml.cs` 用 `Microsoft.Extensions.DependencyInjection` 注册所有服务与 IPC handler，`MainWindow` 也是 DI 单例。日志用 Serilog 写 `logs/`。

### MainWindow 按功能拆为 partial class

`MainWindow` 是一个 `public sealed partial class`，因单文件曾超 2300 行而拆开。**所有 partial 共享类名 `MainWindow` + `namespace Mica;`，靠类名链接、与文件位置无关**（csproj 用 SDK 默认 glob，无显式 Compile 项，移动文件零风险）。

- `MainWindow.xaml` + `MainWindow.xaml.cs`（**留在根目录**，XAML 与 core 代码隐藏成对：字段/构造/启动恢复/IPC 挂载/三个覆盖页接线）
- `Mica/Views/MainWindow.*.cs`（**7 个功能拆分文件**）：`.Navigation`（NavView/视图历史/前进后退/各 Show*Internal）、`.Notebooks`（**笔记本操作**：进入/新建编辑表单/重命名/删除/pickers/标题栏切换器——被文件树等共用，故留在窗口）、`.Sidebar`（侧栏显隐/宽度/大纲）、`.FileTree`（文件树增删改/右键菜单/命名 Flyout/高亮，**扁平模型见下**）、`.Tabs`（标签页/打开保存/会话恢复）、`.EditorTheme`（主题/编辑器配置推送）、`.Window`（窗口/标题栏/最小尺寸）
- 三个覆盖页（设置/笔记本主页/详情页）都已是独立 UserControl，不再 partial（见上及下「覆盖页解耦」）。

**改某功能先去对应 partial 文件找。** 每个文件顶部带完整 using 块（C# 不报未使用 using，全量 using 安全）。

### 覆盖页解耦（SettingsPage / NotebookHomePage / NotebookDetailPage）

三个覆盖页都遵循同一模式，**改它们去 `Mica/Views/` 对应的 UserControl，不在 MainWindow**：

- UserControl 经 `App.Services.GetRequiredService<SettingsService>()` 拿单例供 `x:Bind`（与 MainWindow 同实例）。
- 视图本地逻辑（卡片单/双击区分、多选、拖拽排序、计数刷新、封面填充、设置项即时保存）住在 UserControl 自己。
- **跨页操作**（需窗口句柄/对话框/导航历史/共用的笔记本操作）经 `event`/`Func` 回调交回 MainWindow，在构造函数接线，**绝不反向依赖窗口**。如 NotebookHomePage 的 `ActivateRequested`/`DetailRequested`/`NewNotebookRequested`/`OpenFolderRequested`/`RenameRequested`/`RevealRequested`/`DeleteRequested`；DetailPage 的 `ActivateRequested`/`EditRequested`/`RevealRequested`/`DeleteRequested`（`Show(nb)` 由 MainWindow 调）。
- MainWindow.xaml 里**保留原 x:Name**（`SettingsView`/`NotebookHomeView`/`NotebookDetailView`），所以 `Show*Internal` 的显隐切换零改动；覆盖页靠 `EditorSurface.Visibility=Collapsed` 才盖得住（App 背景透明）。
- `ActivateNotebook`/`ShowNotebookFormAsync`/`DeleteNotebooksAsync` 等留在 `.Notebooks` 是因为**文件树也在用**——不要为了「纯粹」把它们也搬进页面。

### 核心数据流与关键约定

- **文件系统是唯一数据源，没有数据库**。一个**笔记本 = 一个文件夹**。
- **单 WebView + 标签页懒渲染**：全应用只有一个共享 WebView2 实例。`TabViewItem` 只存 Header/Tag/Icon，**不含 Content**；只有当前激活标签的文档会被读盘并在 Milkdown 渲染，切标签 = 重新读盘 + `editor.load` + 重渲染。开 N 个标签 ≈ 同样内存。笔记本同理懒加载，非活动笔记本绝不读其 `.md`。
- **编辑器会话与笔记本解耦**：标签 token 是**绝对路径**（非相对路径），切换/删除笔记本时不动已打开的标签。`FileService.GetFullPath` 对 rooted 路径直接返回，故 save/load 跨笔记本都对。
- 编辑：Milkdown 输入 → debounce → `note.save {relPath, body}` → `IpcRouter` → `FileService.WriteFileAsync`。
- 打开：点文件树 → `FileService.ReadFileAsync` → `editor.load {relPath, body}` → Milkdown 渲染。
- **文件树＝自建扁平模型 + 官方 `ItemsRepeater`（虚拟化）**（2026-06 弃用 `TreeView` 重写：拖拽/多选/样式三大老大难）。`_treeRoots`（层级 `FileNode`，含 `Children`）是源；`_visibleRows`（扁平 `ObservableCollection`，每行带 `Depth`）是按各文件夹 `IsExpanded` 投影出的可见行，绑到 `FileTree.ItemsSource`。**所有变更原地 splice `_visibleRows`**（`ExpandRow`/`CollapseRow`/`FlatInsertNode`/`FlatRemoveNode`，见 `.FileTree`）——不全量重建、不闪、不跳滚动；仅 `LoadTree`/全部展开收起才整体 `RebuildVisibleRows`。当前文件竖条＝`FileNode.IsActive`（**永远指向打开的文件、目录不显示**，与点击解耦）。行 hover/点击/右键全在行模板上手动接（ItemsRepeater 无内建选择 chrome）。**⚠️ 承重坑：`ItemsRepeater` + `x:Bind` 不会给每行设 `DataContext`**（x:Bind 是编译绑定，渲染照常但 `DataContext==null`）——所有读 `sender.DataContext as FileNode` 的事件处理器会静默失效（点击不响应、右键菜单冒泡成空白菜单）。故在 `FileTree_ElementPrepared` 里**手动 `g.DataContext = _visibleRows[args.Index]`**，P2/P3 加交互务必沿用。**交互模型（对标 Windows/PyCharm）**：单击=只选中（文件夹不展开、文件按设置可单击打开）；展开/收起=点左侧小三角（`Chevron_Tapped` 自己 `e.Handled`、不触发选中）或双击文件夹名（`FileTreeItem_DoubleTapped`）。**`SetActiveFile` 只更新「打开文件」竖条、绝不自动展开/滚动**——开关/删/剪标签都不许重排用户的树（竖条仅在该行已可见时显示）；要跳到当前文件用工具条「定位」按钮（`LocateActiveFile`）。刷新（`RefreshTree`）经 `CaptureExpandedRels`/`ReapplyExpanded` 保留展开态。**拖拽（P3 已做，见 `.FileTreeDrag`）**：行/根都挂 `CanDrag`+`AllowDrop`，落盘复用粘贴的 `TransferRelsIntoAsync`（含自身/子孙/原父守卫，逐项跳过不取消整批——多选拖到选中集里的目标文件夹＝它跳过其余进去）；默认移动、Ctrl 复制；反馈＝高亮目标行（`FileNode.IsDropTarget`，**非插入线**：排序树落点由名字定）；悬停折叠文件夹 700ms 自动展开；边缘自动滚动；提示文字实时显示目录名/拒因。**固定「根目录行」`RootDropRow`**（树顶常驻、不随滚动消失）解决「目录全展开后没空白可右键/拖到根」——右键复用 `FileTree_RightTapped`、拖放复用根落点，名称由 `UpdateSidebarHeader` 同步（收尾可用它取代上方独立路径/名称区）。
- 大纲：Web 端解析 ATX 标题（`stripInlineMarkdown` 去掉 `**`/`` ` ``/`[]()` 等内联标记再推）→ `notifyOutline` → `OutlineHandler` → 侧栏大纲 ListView（字号/字重按 Level 分级）；点大纲 → `editor.scrollTo`。
- 全局状态存 `AppData/Local/Mica/settings.json`（`SettingsService` 统一管理：主题偏好 + 笔记本注册表 + 活动笔记本 id + 打开的标签会话）。

### IPC 通信

- Host → Web：`CoreWebView2.PostWebMessageAsJson`；Web → Host：`window.chrome.webview.postMessage`
- 消息是 JSON-RPC 2.0 子集（`web/src/ipc/protocol.ts` 与 `Mica/IPC/IpcMessage.cs`）
- Host 分发：`IpcRouter` 按 `method` 路由到注册的 `IIpcHandler`（在 `App.xaml.cs` 注册：`note.save`/`theme.update`/`log`/`editor.stats`/`notifyOutline`）
- Web 封装：`bridge.ts` 提供 `request()`/`notify()`/`on()`

### UI 结构（VS Code 风格）

- **NavigationView（左侧导航）**：只管导航——汉堡（图标↔图标+文字）/ 顶部搜索框（文件名快搜）/ 工作区 / 笔记本 / 设置齿轮 / 官方选中指示器。`PaneDisplayMode=Left`。
- **关键认知**：树/大纲是「编辑器的东西」不是「导航」，所以它**不住在导航栏里、单独成一列 `TreePanel`**（在 `NavView.Content` 内，可拖拽宽度）。`EditorSurface` 包住 `[TreePanel + 编辑器 FilesView]`；笔记本卡片页/详情页/设置页是覆盖页，显示时整层 `EditorSurface.Visibility=Collapsed`（因 App 背景透明透出 Mica，光靠 z 序盖不住，必须整层收起）。
- **TitleBar**：Mica 标题 + 前进/后退 + 菜单栏（文件/编辑/段落/格式/视图/帮助，**仅编辑器视图显示**）+ 居中笔记本切换器（DropDownButton 下拉快切）+ 右侧明暗切换 ToggleButton。
- **编辑器区 FilesView**：TabView（多标签，共享单 WebView2）+ WebView2 + 底部状态栏（字数统计 + 源代码模式开关）。
- **设置页**是独立页面（非对话框），改动**即时保存**到 `settings.json`。

## 设计原则与工作方式

1. **优先复用 WinUI 3 官方组件**（TabView/TreeView/MenuBar/NavigationView 等），不造轮子；官方不足再考虑 CommunityToolkit 或自定义。用法可查 [WinUI Gallery](https://github.com/microsoft/WinUI-Gallery) 和官方文档。
2. **修 bug 先找根因**，不要为修一个 bug 引入新 bug。改动前想清楚副作用。
3. **该解耦就解耦，但别过度抽象**：相关功能聚在一个 partial 文件，不相关的拆开；不引入企业级分层。用户偏好前端风格的扁平结构。
4. **遵循 Fluent Design 2，保持简单**：视觉用原生 Windows 风格，不堆砌。
5. **及时提出异议**：如果某个设计不合理、或当前阶段不该做某件事，直接说出来，避免走偏。

## 关键约定

- C# 4 空格缩进，`ImplicitUsings` + `Nullable` 已启用
- TypeScript 2 空格缩进，行尾 LF；**编辑器 web 端不引入 UI 框架**，仅用 CSS Variables
- WebView2 背景透明，Mica 材质由 host 窗口提供
- 文件夹排序：目录在前、文件在后，按名称排序
- **删除文件/文件夹/笔记本统一走 `FileService` + `System.IO`/`SHFileOperation`**，不用 `Windows.Storage`（未打包应用里 `StorageFile.DeleteAsync` 会静默失败）
- **移动/复制/创建副本/重命名/跨笔记本粘贴统一走资产搬运（2026-06 引擎已泛化为跨根）**：核心是 `FileService.TransferEntryAsync(srcFull, srcRoot, destParentRel, isCopy)`——**源用绝对路径 + 源笔记本根，目标永远是当前笔记本**，故同笔记本/跨笔记本一套搞定（同根→原生 Move；跨根→复制+删源，避免跨卷 Move 抛）。便捷重载 `TransferEntryAsync(srcRel, destParentRel, isCopy)`（源根=当前笔记本）保留给创建副本/拖拽。重命名走 `RenameEntryAsync`。都调同一私有 `CarryAssetsAsync(srcRel, srcRoot, destRel, destRoot, …, assetMove)` 搬 `.mica` 资产 + 改写 `<img src>`。**剪贴板存「绝对源路径 + 源笔记本根」**（`_clipboardAbs`/`_clipboardSrcRoot`，**不存相对路径**——切笔记本后相对路径会被目标笔记本错误解析）；`TransferAbsIntoAsync` 是窗口侧统一入口（粘贴/拖拽都走它，按 `PathsEqual(srcRoot, 当前)` 判同/跨笔记本：同笔记本套自身/子孙/原父守卫+移动摘源行，跨笔记本免守卫、源不在当前树）。**承重坑**：`.mica/assets/<笔记相对路径去扩展名>/` 把 rel+根嵌进了资产位置，所以**任何改变笔记 relPath 或所属根的操作都必须连带搬资产目录 + 改写 `<img src>`**。⚠️**`<img src>` 是「相对笔记目录」的路径（含 `../`），层数随笔记深度变化**——故**每篇受影响笔记都按自身新旧位置 `Path.GetRelativePath(noteDir, ResolveMicaAssetDir(note, root))` 重算完整前缀再替换**（两端各用自己的根），绝不只换中间段。Web 侧 `imageNode.ts` 用笔记**绝对路径**（`editor.load` 的 `relPath` 实为绝对 token）拼相对 src→`img.mica.local` 显示 URL，故只要 `<img src>` 相对新位置正确，跨笔记本打开图也正确。**已知限制**：同目录/assets 图片模式下单独移/拷一个 `.md` 仍丢图（图与 `.md` 同级、不在 `.mica`），仅文件夹整体移动才带得走
- **文件系统监视（FileService 自持）**：`OpenFolder` 时对**当前笔记本目录**起 `FileSystemWatcher`（切笔记本重挂、`CloseFolder` 停；其他笔记本切回时整树重载，不常驻监视）。**区分内外两招**：① `NotifyFilter` 只设 `FileName|DirectoryName`（**不含 `LastWrite/Size`**）→ 笔记保存(写内容)根本不触发，消除最高频噪声；② 每个改盘方法用 `BeginInternal/EndInternal` 括住（含尾随 `SuppressTail` 静默窗口）→ Mica 自身操作的事件被判为「自己干的」忽略。外部改动经 350ms 防抖后 raise `ExternalChanged`（线程池线程）→ MainWindow marshal 回 UI 调 `RefreshTree`（保留展开态）。`.mica`/dotfile/`~$` 路径一律忽略。
- **不引入**：Entity Framework、Avalonia、MAUI、Electron、SQLite（全文搜索是远期再议）
- 应用图标：未打包应用走传统路径，`<ApplicationIcon>` 嵌入 .exe + 运行时 `AppWindow.SetIcon`，矢量源是仓库根的 `Mica.svg`

## 几个架构要点

- **所有快捷键最终都在 web 端处理（WebView2 持有焦点时收不到 XAML `KeyboardAccelerator`）**：
  - 编辑器内命令（标题/正文/引用/代码块/加粗等）→ `web/src/editor/createEditor.ts` 的 `paragraphKeymap`（ProseMirror keymap）+ Milkdown 原生键位；菜单只用 `KeyboardAcceleratorTextOverride` 显示文字、不注册真 accelerator（避免与 web 重复触发）。
  - app 级命令（保存/关标签/新建/打开文件夹/侧栏/源码/全屏）→ `web/src/main.ts` 的 `keydown` 监听捕获后 `notify('host.shortcut',{action})` 转发；宿主 `ShortcutHandler`（IPC 单例）→ `MainWindow.RunShortcut(action)` 执行既有逻辑。焦点不在编辑器时（如目录树），宿主 XAML accelerator 本就能触发、web 收不到键，故不会重复。
  - `EditorHostView` 设了 `AreBrowserAcceleratorKeysEnabled = false`，放行被浏览器占用的 Ctrl+数字等。
  - **要抢在 Milkdown/commonmark 的 keymap 之前处理某个键**（如标题行首退格 → 直接变正文，对标 Typora）：用 `editorViewOptionsCtx` 的 `handleKeyDown`「直接 prop」挂载，而非再加一个 keymap 插件——ProseMirror 的 `view.someProp` 会先查 view 直接 prop，再查 state 插件，故直接 prop 稳定抢先，不用跟插件顺序/优先级较劲（ProseMirror 无 CodeMirror 那种 `Prec`）。见 `createEditor.ts` 的 `headingBackspaceToParagraph`。
- **菜单快捷键守卫**：app 级命令处理函数有 `if (!InEditorView) return;`（`InEditorView => EditorSurface.Visibility == Visible`）——`RunShortcut` 同样守卫；覆盖页（设置/笔记本）靠此挡掉编辑器命令，而非注销快捷键。
- **图片/表格的浮动工具条已全部下线（2026-06），入口统一收进宿主原生 CommandBarFlyout 右键菜单**（`MainWindow.Image.cs`/`MainWindow.Table.cs`）。详见记忆 [[toolbars-to-native-menus]]。要点：① 顶部 `PrimaryCommands`（图标按钮挂 `.Flyout`）放「需要自定义控件的项」（圆角/线宽/大小=Slider、行列数=NumberBox、位置=四选一）——**SecondaryCommands 里放自定义 Flyout 会被列表弹出层遮挡、要点两次**，故必须放顶部；普通 MenuFlyout 子项（列对齐/插入/移动/样式预设）才留列表。② 滑块拖动走 `preview`(只改实时 DOM 不提交) / 停手·关闭走 commit 的防卡套路（`WireLiveSlider` + web `runImageOp`/`runTableOp` 的 preview 分支）。③ 菜单弹出时 web 经 `host.imageMenu`/`host.tableMenu` 带当前属性回填控件。④ `tableToolbar.ts` 砍成「body 外非缩放浮层 + 行/列拖动重排管理器」——reorder 把手仍用该浮层，开关改为菜单「排序」项；`imageToolbar.ts` 已删。
- **公式/代码块、行内公式与行内代码（原子节点 + inlineEmbedView 三态）、HTML 表格（每表样式/三线表/多级表头/右键菜单整表样式/行列拖动重排/图标）、源代码模式（CM6 行号/语法高亮/光标往返）、页内锚点、整体缩放** 的实现细节与踩坑 → 见 [docs/编辑器实现笔记.md](docs/编辑器实现笔记.md)。
- **通用 HTML 渲染（对标 Typora，`web/src/editor/htmlNode.ts`）**：笔记里手写的 HTML 会真正渲染（自带 html 节点只显示原码）。两类自定义节点 `mica_html_block`（块级 atom）/`mica_html_inline`（行内 atom）。**编辑＝单击节点原地展开 CM6 行内原码框**（失焦/Ctrl+Enter 提交、Esc 取消、清空即删），渲染态不加块级选中框（像网页）。**跨模块承重坑**：`micaHtmlRemark` **必须排在图片/表格/图册 remark 之后**（`.use` 链最后）——前者先把 `<img>`/`<table>`/`<div data-mica-gallery>` retype 成专用类型，本 remark 只接管**剩余** `type==='html'` 的节点（故新增「会序列化成 HTML 的自定义节点」时，务必让其 remark 在 `micaHtmlRemark` 之前注册，否则会被当通用 HTML 吞掉）。渲染前过 `sanitizeHtml` 剥 script/style/iframe/on*/javascript:（无 DOMPurify）。**已知限制**：行内混排富内容（`<u>**x**</u>`）不渲染、原始 HTML 里的本地 `<img file://>` 不显示。细节见 docs/编辑器实现笔记.md。
- **图片（开发中，分期计划见 [docs/图片功能计划.md](docs/图片功能计划.md)）**：① **本地图显示走 host 管道**——编辑器挂在 `https://editor.mica.local` 这个 https 源，`<img src="file://…">` 会被 WebView2 拦掉（裂图），故 web 端图片 DOM src 一律写成 `https://img.mica.local/img?p=<encodeURIComponent(绝对路径)>`，`EditorHostView` 的 `WebResourceRequested` 拦截后读盘喂回（同表格「host 喂数据」思路）；.md 里仍存相对/绝对路径，渲染时才转显示 URL。② 图片节点**混合序列化**：普通图存干净 `![]()` Markdown、带样式图才存 `<img>` HTML（见 `web/src/editor/imageNode.ts`）。③ 默认存 `.mica/assets/<笔记相对路径去扩展名>/`（目录树隐藏 dotfile，不污染）；删笔记/文件夹经 `FileService.DeleteNoteAssets` 连带删图。④ **选中图浮动信息条**（`web/src/editor/imageToolbar.ts`，二期）：宽度/对齐/圆角/边框/删除，WinUI 亚克力风；实现细节与多轮踩坑（即时 DOM 预览防卡、按对齐锚边+吸顶定位、自绘滑块、底色实测反解 `rgba(255,255,255,0.84)`）见 [docs/图片功能计划.md](docs/图片功能计划.md) 与记忆 [[image-node-inline-final]]。⑤ **图片裁剪（三期，WinUI3 原生）**：image 节点加 `crop` 属性（相对原图的归一化分数 `"L,T,W,H"`，不动原图、只记显示哪块、可无限回原图重裁），存 `<img data-crop>`；显示靠 NodeView「**外层 wrap + 内层 clip 盒**」：img 绝对定位放大，内层 `.mica-image-clip{overflow:hidden}` 裁到可视窗（**别用 wrap overflow 也别用 clip-path**——前者会裁掉贴图竖线/光标伪元素+被图盖住、后者只裁视觉不裁布局溢出→放大的绝对 img 撑出文档产生横向滚动条）。NodeView 动态把同一 img 在 wrap↔clip 间移动（不重载）。竖线/光标在外层 wrap（不被裁、`z-index:1` 压在图上）；圆角=内层盒 border-radius，边框/选中框圆角=wrap border-radius。wrap 定宽 + `aspect-ratio`：绝对 img 不撑开 inline-block wrap，故 **width 为空时 NodeView 加载后按「裁剪区自然像素宽」补 wrap 宽**（max-width:100% 钉行宽内），否则自适应塌成极小框。**边框**：未裁剪挂 img、**裁剪图挂 wrap**（img 被裁切，边框挂 img 会被裁没）。入口：浮条按钮 / 右键「裁剪图片」→ `host.imageCrop`（`ImageCropHandler`）→ `MainWindow.ImageCrop.cs` 弹 `ContentDialog`（`Controls/CropSelector` 选框：暗化遮罩+8 把手+移动/缩放/重框）→ 经 `editor.imageOp{op:'crop'}` 回传 web 写 crop。**几个关键坑**：(a) 对话框入口必须 `Path.GetFullPath` 规范化 abs——浮条传来的正斜杠/未解析 `..` 会让 `StorageFile.GetFileFromPathAsync` 抛异常静默失败；(b) 读原图尺寸用 `BitmapDecoder`（不能 await 游离 `BitmapImage.ImageOpened`，未挂可视树不解码→永久挂起）；(c) `CropSelector` 的 Canvas 须比图四周留 `Pad`，否则边/角把手外半边落到 Canvas 外收不到指针。对话框含：顶部文件名 + 可编辑 `W×H`（NumberBox，编辑=从中心缩放裁剪框）+ 原图尺寸、比例预设（自由/1:1/4:3/3:2/16:9/9:16，锁比例缩放）、自建按钮条（弃内置按钮：还原全图靠左、裁剪/取消靠右正常宽）。⑥ **图片翻转**：image 节点加 `flipH/flipV`（CSS `transform:scaleX/scaleY(-1)` 挂 wrap，与裁剪同层→裁剪区一并镜像、位置不变），存 `<img data-flip="h"/"v"/"hv">`；浮条两个即时按钮（非裁剪弹窗内，避免与裁剪框坐标在模态里纠缠）。**不做旋转**（用户决定）。⑦ **已做**：`ImageStorageMode` 设置页下拉（宿主 `ImageSaveHandler` 落盘时直读、**无需推 editor.config**）、删除二次确认设置 UI。⑧ **替换图片（第四期第 1 步，已做）**：**仅右键菜单**「替换图片 ▸ 从文件…/从剪贴板」（二级菜单，对标 Word；浮条不放——用户要求避免越加越挤）。全程宿主侧（`MainWindow.Image.cs` 的 `ReplaceImageFromFileAsync`/`ReplaceImageFromClipboardAsync` → `ApplyReplaceAsync`），当前笔记取自活动标签 Tag=绝对路径（`ActiveNoteAbsPath`），落盘复用 `SavePastedImageAsync` → 回传 `editor.imageOp{op:'replace',src}` → web `replaceImage`：**保框居中裁剪**（旧图有显式 width 或 crop 才按旧框有效比例 `computeCenterCrop` 居中裁新图、绝不放大；纯自适应图直接换不裁）。`runImageOp` 现为 `(op,crop?,src?)`；`ResolveNotebookRoot` 已从 `ImageSaveHandler` 提到 `SettingsService` 供两处共用（无独立 IPC handler）。⑨ **图片组容器（第四期第 2 步，定稿为「行内原子」）**：**行内原子节点**（inline atom）`imageGroup`（`web/src/editor/imageGroup.ts`，attrs：`layout`(目前仅 row) / `images: ImageAttrs[]`(**子图存属性数组、不是 PM 子节点**) / `frameless`）。**两次 pivot 的最终形态 = atom + inline**：① **atom**（无内部内容）→ 文本光标永不落进组内、flex 坐标错乱（卡最右/组内游离光标）从构造上消失；容器自掌 DOM → 多行画廊（可上现成库）/多选/拖拽/磁吸都好做。② **inline 而非块级**（关键，第三轮才定）→ **与单图（image，行内 atom）完全同构**：容器活在一个段落里，组前=段落 offset 0、组后=offset 1，于是「删容器前/后的空行」**完全交给 PM 原生段落合并**（块级 atom 做不到 → 才有「退格选中容器/光标跳下一行」的反直觉），且段落是 textblock、容器与上方表格/块之间**不再产生横向 gapCursor**。**复用单图的独占/光标/删除逻辑**：`createEditor.ts` 里 `nodeHasImage`/`splitParagraphByImages`/`typeBesideImageToNewLine`/`backspace*`/`imageCaretPlugin`/`imageTargetForDelete` 全部经 `isImageLikeNode`（=image||imageGroup）一视同仁——容器和单图共享同一套「图旁打字另起一行 / 贴图竖线 / 删除两步」，不再为组单写。CSS：容器 `display:inline-flex;width:100%`（视觉占满整行、保留行内光标语义）。**渲染**：NodeView 自掌内部 DOM，每张子图一个 `createImageRenderer()`（从 imageNode 抽出，**独立图与组内图共用**裁剪/翻转/圆角/显示管道）；`mode='group'` 时 wrap `flex-grow=有效显示比例`（加载后写）→ 单行等高。**点击语义**（NodeView mousedown，`preventDefault` 阻止 PM 落光标）：点中图 → 选那张图（PM 选 `NodeSelection(容器)`，「激活第几张」由 `imageGroupActivePlugin` **插件状态** `{pos,index}` 记——经 node decoration 的 `spec.micaActiveIndex` 传给 NodeView 高亮；位置随事务自动 map、选区一离开容器自动清空。**别再用模块级变量**：外部改不动 NodeView 闭包、也触发不了重渲染）；点内边距/图间空白 → 选整个容器（meta=null）。**视觉**：容器选中＝框变强调色 + 淡底（`.ProseMirror-selectednode:not(.mica-has-active)`）；选中组内某图＝那张 `.mica-img-active` 蓝描边。**删除**：`handleKeyDown` 里 `deleteActiveGroupImage` **必须先于** `handleImageDelete`——有激活子图时删 `attrs.images[index]`（删光由 `imageGroupCleanupPlugin` 判 `images.length===0` 连带删容器）；无激活子图（容器整体选中 / 光标贴容器）时 `handleImageDelete` 删整组。**删单图后停在同级**（`deleteActiveGroupImage` 落点 = 顶上来的同位图、删末张则新末张），绝不跳到「选中整个容器」（组内还有图却选更高一级的容器不合理）。**方向键** `arrowWithinGroup`：激活某图时 ←/→ 选同级相邻图、到边界（首张再←/末张再→）退出到容器前/后文本光标位。序列化＝`<div data-mica-gallery>…<img>…</div>`（`serializeGroup`/`parseGroupHtml`，子图复用 `serializeImg`/`parseImgHtml`；`micaImageGroupRemark` 把这块 div html **包进 paragraph** 行内节点才认领得到，镜像单图 remark）。入口①＝多图粘贴自动成组（`buildImageGroupNode`）。⑩ **组内图浮条/裁剪/翻转/替换（第四期第 3 步，已做）**：引入 **`ImageTarget`**（`imageGroup.ts`：`{kind:'image',pos}` | `{kind:'group',pos,index}`）统一「当前聚焦的图」——`currentImageTarget`/`readTargetAttrs`/`writeTargetAttrs`/`targetDom`/`deleteTarget` 屏蔽独立图 vs 组内图差异，`imageToolbar` 与 `imageSetup`（右键菜单 + `runImageOp` 裁剪/翻转/替换/删除）**一套代码两态通吃**（组内图写 `images[index]` 并维持激活 index）。浮条改由 **`imageToolbarSyncPlugin`**（PluginView.update，状态提交后 view.state 最新时驱动）显隐回填——既躲开 `selectionUpdated` 慢一拍（[[table-toolbar-selection-staleness]]），又能捕获「组内激活图切换」（PM 选区不变、`selectionUpdated` 不触发）。组内图浮条隐藏对齐/宽度（大小由 flex 决定），保留圆角/边框/翻转/裁剪/删除；右键菜单经 `groupPosFromDom`（nodeDOM 全等比较定位容器）+ `data-index` 选中该子图。⑪ **组内图多选（第四期第 4 步，已做）**：`imageGroupActiveKey` 状态扩 `indices?:number[]`（`index`=锚点/最后点击、供方向键+范围选+浮条信息；`indices`=完整选中集）。**Ctrl/Cmd+点击**切换单张进/出、**Shift+点击**从锚点选连续范围（NodeView mousedown 据修饰键算 meta）。激活 decoration 改携 `micaActiveIndices`（选中集，NodeView 高亮多张）；浮条圆角/边框/翻转/删除经 `getGroupSelection`+`writeGroupImagesPatch`/`flipGroupImages`/`deleteGroupImages` **批量**作用于全部选中（圆角/边框即时预览同步所有选中图；翻转各自按自身状态独立镜像；信息行多选时显示「已选 N 张」、隐藏裁剪/替换=单图语义）。二次确认删除 `imageDeleteKey` 同扩 `indices`（标红多张）。右键菜单/方向键**收敛为单选**（点中那张/锚点±1）。⑫ **复制/粘贴语义（第四期第 5 步，已做）**：复制走 `transformCopied`（`transformGroupCopied`）——组内选 1 张→单 `image` 节点（粘成 `![]()`）、选 ≥2 张→新 `imageGroup`；粘贴落点统一为 `placeImageSrcs`——**选中容器时追加进组**（不替换），否则单张普通图/多张成组。⑬ **组内拖拽重排（第四期第 6 步，已做）**：NodeView 自绘指针拖拽（非 HTML5 drag），落点指示线 `.mica-group-drop-indicator` + `moveGroupImage` 改 `images[]` 顺序；img `draggable=false` 禁原生拖影。**用户拍板的关键决策**：① **不合并单图与容器、组内删到 1 张不自动拆**——容器是实体概念；② **容器用户可见名＝「图册」**（代码仍叫 `imageGroup`，菜单/文案一律「图册」）；③ **图册 `layout` 模式**＝ `row`(已做)/`justified`/`magnetic`/**`carousel` 轮播**(定高可调、自动手动轮播、图居中自适应填满)。⑭ **插入图片/新建图册菜单 + 空图册占位（第四期第 7 步，已做）**：格式▸**图像** 子菜单＝`插入图片…`(Ctrl+Shift+I)/`新建图册`(Ctrl+Shift+G)，web keydown 转 `host.shortcut{insertImage/newGallery}` → `RunShortcut` → `MainWindow.Image.cs`：`InsertImagesFromFilesAsync`(多选落盘) → `editor.insertImages{srcs}` → web `placeImageSrcs`；`InsertEmptyGallery` → `editor.newGallery` → `insertEmptyGallery`(插空 imageGroup)。**空图册是一等对象**：**已下线 `imageGroupCleanupPlugin`**（不再删空壳）、`parseMarkdown` 允许 `images:[]` 往返、删图到空只留 NodeView 占位（点占位→选中册+`host.shortcut{insertImage}`追加），要删整册得显式选容器删。⑮ **图片跨界拖拽 c2（第四期第 8 步，已做）**：统一控制器 `web/src/editor/imageDrag.ts`——`startImageDrag(view,e,src)`（src=独立 image 节点 / 图册内某图）自绘指针拖拽（非 HTML5 drag），`computeDropTarget` 经 `galleryRegistry`(图册 NodeView 登记 dom→getPos)+`elementFromPoint` 命中图册、否则 `posAtCoords` 文档落点，`performDrop` 一个事务「源移除+目标插入」。**关键**：图册是行内 atom（nodeSize 恒 1）→ 增删子图只改 attrs、不移动文档位置，仅文档级 insert/delete 用 `tr.mapping` 修正。覆盖册内重排/拖出成独立图/拖入册/册间互拖/独立图重定位。接线：`image` 节点 `draggable:false`（弃 PM 原生 drag）、`createImageNodeView(node,view,getPos)` wrap mousedown 起拖；图册 mousedown 无修饰键点图起拖；`moveGroupImage` 已删（并入 performDrop）。占位改「添加图片/粘贴」两按钮（粘贴=`host.shortcut{pasteImage}`→`InsertImageFromClipboardAsync`）。**未做**：多选拖动；#7 空单图占位（待用户定，现 Ctrl+Shift+I 直接开选择器）；#3 多选属性空显示；layout 切换 UI + justified + carousel；磁吸；容器样式（框线/圆角/间距）。细节见 [docs/图片功能计划.md](docs/图片功能计划.md)。
- **样式怎么改、Bug 修复记录** → 见 [docs/开发笔记.md](docs/开发笔记.md)。
- **主题系统（规划中，排在「文件树重写」之后做）** → 设计方案见 [docs/主题系统计划.md](docs/主题系统计划.md)：主题分三层（token / 装饰层 / 行为层），内置多套（Fluent2/四季/星露谷/Claude+桌宠），不开放可视化设计系统，主题定义随包发、选中 id 存 settings.json。含第三方 IP 版权备忘。

## 完成后

每次改完**用中文总结改动即可**，并**按需更新文档**——但分清放哪：

- **CLAUDE.md（本文件，常驻必读，保持精简）**：只记真正影响后续开发的**架构决策、跨模块约定、全局性的坑**。**不要把功能实现细节堆进来**（上次就是这样涨到 60KB 的）。
- **`docs/编辑器实现笔记.md`**：编辑器深水区（公式/代码/行内嵌入/表格/缩放等）的**实现细节与踩坑**。
- **`docs/开发笔记.md`**：样式怎么调、Bug 修复记录。

判断标准：「换个人接手必须先知道」→ CLAUDE.md；「改这个具体功能时才需要查」→ docs。
