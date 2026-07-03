using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Mica.Models;
using Mica.Services;
using Mica.IPC;
using Mica.IPC.Handlers;
using Serilog;

namespace Mica;

public sealed partial class MainWindow : Window
{
    private readonly IpcRouter _ipcRouter;
    private readonly FileService _fileService;
    private readonly SettingsService _settings;
    private readonly IServiceProvider _services;
    private readonly AppWindow _appWindow;
    private bool _ipcAttached;
    private string _themePref = "System"; // System | Light | Dark

    // View navigation history (back/forward in the title bar).
    private enum ViewKind { Home, Files, Detail, Settings, Maintenance }
    // NotebookId 对 Maintenance 视图存「逗号分隔的 id 列表」（批量），其余视图存单个 id。
    private readonly record struct ViewState(ViewKind Kind, string? NotebookId);
    private readonly List<ViewState> _history = [];
    private int _historyIndex = -1;
    private bool _navigating;

    // Outline of the currently open file (大纲 sidebar tab).
    private readonly System.Collections.ObjectModel.ObservableCollection<OutlineItem> _outline = [];
    private bool _outlineMode;

    // Editor readiness + the tab session to reopen once it becomes ready.
    private bool _webReady;
    private List<string>? _pendingRestoreFiles;
    private string? _pendingActiveFile;
    // Suppresses tab-session persistence during programmatic teardown/restore.
    private bool _suppressTabPersist;

    public MainWindow()
    {
        // SettingsService must be loaded before InitializeComponent so the
        // x:Bind to _settings.Notebooks (notebook home GridView) has its source.
        _services = App.Services;
        _settings = _services.GetRequiredService<SettingsService>();
        _settings.Load();

        InitializeComponent();

        _ipcRouter = _services.GetRequiredService<IpcRouter>();
        _fileService = _services.GetRequiredService<FileService>();

        // 设置页是独立 UserControl（Views/SettingsPage）：它自己即时保存到 SettingsService，
        // 但主题切换/标签关闭按钮/编辑器配置推送/选目录这些跨页副作用做不了，回调到本窗口处理。
        SettingsView.ThemeChangeRequested += ApplyThemePreference;
        SettingsView.TabCloseModeChanged += ApplyTabCloseButton;
        SettingsView.EditorConfigChanged += PushEditorConfig;
        SettingsView.ZoomConfigChanged += PushZoomConfig;
        SettingsView.PickFolderAsync = PickFolderPathAsync;

        // 笔记本主页/详情页也是独立 UserControl：它们自管卡片/多选/封面等视图逻辑，但
        // 「进入/查看详情/新建/打开文件夹/重命名/在资源管理器显示/删除」这些跨页操作做不了，
        // 经事件/回调交回本窗口（这些方法仍在 MainWindow.Notebooks.cs，且被文件树等共用）。
        NotebookHomeView.ActivateRequested += ActivateNotebook;
        NotebookHomeView.DetailRequested += nb => NavigateTo(new ViewState(ViewKind.Detail, nb.Id));
        NotebookHomeView.NewNotebookRequested += () => _ = ShowNotebookFormAsync(null);
        NotebookHomeView.OpenFolderRequested += () => _ = OpenFolderAsync();
        NotebookHomeView.RenameRequested = RenameNotebookAsync;
        NotebookHomeView.RevealRequested = RevealInExplorer;
        NotebookHomeView.DeleteRequested = DeleteNotebooksAsync;

        NotebookDetailView.ActivateRequested += ActivateNotebook;
        NotebookDetailView.EditRequested = ShowNotebookFormAsync;
        NotebookDetailView.RevealRequested = RevealInExplorer;
        NotebookDetailView.DeleteRequested = DeleteNotebooksAsync;

        // 笔记本维护覆盖页：详情页菜单（单个）/ 主页多选工具条（批量）两入口，复用同一份页面。
        NotebookDetailView.MaintenanceRequested = nb =>
            NavigateTo(new ViewState(ViewKind.Maintenance, nb.Id));
        NotebookHomeView.MaintenanceRequested += nbs =>
            NavigateTo(new ViewState(ViewKind.Maintenance, string.Join(",", nbs.Select(n => n.Id))));
        MaintenanceView.CleanOrphansRequested += OnCleanOrphansRequested;
        MaintenanceView.CleanAllIssuesRequested += OnCleanAllIssuesRequested;
        MaintenanceView.LocateRequested += OnLocateNote;
        MaintenanceView.CleanRefRequested += OnCleanRefRequested;
        MaintenanceView.MigrateRequested += OnMigrateRequested;

        // 监听系统剪贴板：外部（资源管理器等）复制新内容时作废 Mica 内部剪贴板，使粘贴用最新一次复制（见 OnSystemClipboardChanged）。
        Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += OnSystemClipboardChanged;

        // Apply the saved tab close-button mode to the TabView up front (sets
        // CloseButtonOverlayMode so new tabs inherit hover/always behavior).
        ApplyTabCloseButton();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1400, 900));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "mica.ico");
        if (File.Exists(iconPath))
            _appWindow.SetIcon(iconPath);

        EnableMinWindowSize(hWnd);

        // Caption-button colors (transparent bg + theme-aware glyph foreground). Without an
        // explicit foreground these can render nearly invisible under HDR + dark. Re-applied
        // on theme change via OnContentActualThemeChanged.
        UpdateCaptionButtonColors();

        EditorHost.Loaded += OnEditorHostLoaded;

        // 从资源管理器把图片拖进编辑器：宿主侧落盘后经 editor.dropImages 回传 web 在落点插入（见 MainWindow.Image.cs）。
        EditorHost.ImagesDropped += OnEditorImagesDropped;

        // Push the current light/dark palette to the editor once the page is up,
        // and keep it in sync if the system theme changes while we follow it.
        EditorHost.WebReady += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _webReady = true;
            SyncEditorTheme();
            PushEditorConfig();
            PushZoomConfig(); // 初始缩放比例 + 是否启用 Ctrl+滚轮
            // If we restored a notebook on launch, reopen its whole tab session now that
            // the editor can receive editor.load (sending it before WebReady is lost).
            if (_pendingRestoreFiles is { } files)
            {
                var active = _pendingActiveFile;
                _pendingRestoreFiles = null;
                _pendingActiveFile = null;
                _ = RestoreTabsAsync(files, active);
            }
        });
        if (Content is FrameworkElement rootElement)
        {
            rootElement.ActualThemeChanged += OnContentActualThemeChanged;
        }

        OutlineList.ItemsSource = _outline;

        WireZoomAccelerators();

        // Restore the saved tree-panel width (the drag grip persists it).
        if (_settings.SidebarWidth is >= 180 and <= 520)
            TreePanel.Width = _settings.SidebarWidth;

        // Restore the saved theme preference (System/Light/Dark).
        ApplyThemePreference(_settings.ThemePref);

        _fileService.FolderChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                VaultNameText.Text = _settings.ActiveNotebook?.Name ?? _fileService.FolderName ?? "Mica";
                UpdateSidebarHeader();
                UpdateVaultNameVisibility();
                // The editor is notebook-independent now: switching notebooks rebuilds the
                // tree but leaves the open tabs alone. LoadTree clears _activeNode, so
                // re-light the highlight for the current tab if its file is in this notebook.
                LoadTree();
                if (EditorTabs.SelectedItem is TabViewItem { Tag: string token })
                    SetActiveFile(token);
            });
        };

        // 外部（资源管理器等）改动当前笔记本目录结构 → 刷新树（已在 FileService 防抖 + 排除自身操作）。
        // ExternalChanged 在线程池线程触发，须 marshal 回 UI 线程；RefreshTree 保留展开态/定位。
        _fileService.ExternalChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(() => { if (_fileService.CurrentFolder is not null) RefreshTree(); });

        RestoreActiveNotebook();
    }

    // --- Startup / notebook restore ---

    private void RestoreActiveNotebook()
    {
        try
        {
            var active = _settings.ActiveNotebook;
            if (_settings.RestoreLastNotebook && active is not null && Directory.Exists(active.Path))
            {
                _fileService.OpenFolder(active.Path);
                NavigateTo(new ViewState(ViewKind.Files, active.Id));
            }
            else
            {
                NavigateTo(new ViewState(ViewKind.Home, null)); // land on the home page
            }

            // Reopen the GLOBAL editor session (notebook-independent, absolute paths). Defer
            // until the editor is ready (WebReady); sending editor.load before then is lost.
            // Drop any files deleted since last run.
            if (_settings.RestoreLastNotebook)
            {
                var files = _settings.OpenFiles.Where(File.Exists).ToList();
                if (files.Count > 0)
                {
                    var saved = _settings.ActiveFile;
                    var activeFile = files.Contains(saved ?? "") ? saved : files[0];
                    if (_webReady) _ = RestoreTabsAsync(files, activeFile);
                    else { _pendingRestoreFiles = files; _pendingActiveFile = activeFile; }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore active notebook");
            NavigateTo(new ViewState(ViewKind.Home, null));
        }
        // 初始显隐：若本次没有恢复任何标签，TabItemsChanged 不会触发，需在这里把编辑区收成空状态。
        // 有标签要恢复时是延迟到 WebReady 才加，加时会触发 TabItemsChanged 再切回来。
        UpdateEditorChrome();
    }

    // --- IPC ---

    private void OnEditorHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_ipcAttached) return;
        _ipcAttached = true;

        var channel = new WebView2IpcChannel(EditorHost);
        _ipcRouter.AttachChannel(channel);
        _ipcRouter.Register(_services.GetRequiredService<NoteSaveHandler>());
        _ipcRouter.Register(_services.GetRequiredService<ImageSaveHandler>());
        _ipcRouter.Register(_services.GetRequiredService<ThemeUpdateHandler>());
        _ipcRouter.Register(_services.GetRequiredService<LogHandler>());
        // 外部链接：web 端 Ctrl/Cmd+点击外链 → host.openExternal → 系统默认浏览器打开（无需 UI 线程/窗口）。
        _ipcRouter.Register(_services.GetRequiredService<OpenExternalHandler>());

        // EditorStatsHandler is a singleton so the instance we subscribe to is the
        // same one the router dispatches to (word-count updates from the editor).
        var statsHandler = _services.GetRequiredService<EditorStatsHandler>();
        statsHandler.StatsUpdated += OnEditorStatsUpdated;
        _ipcRouter.Register(statsHandler);

        // 缩放比例由编辑器上报（Ctrl+滚轮 / Ctrl+Shift+±0）；singleton 保证订阅与分发同实例。
        var zoomHandler = _services.GetRequiredService<EditorZoomHandler>();
        zoomHandler.ZoomChanged += OnEditorZoomChanged;
        _ipcRouter.Register(zoomHandler);

        // Outline (current file's headings) is pushed from the editor; singleton so
        // the subscribed instance matches the one the router dispatches to.
        var outlineHandler = _services.GetRequiredService<OutlineHandler>();
        outlineHandler.OutlineUpdated += OnOutlineUpdated;
        _ipcRouter.Register(outlineHandler);

        // app 级快捷键由 web 端 keydown 捕获后转发（编辑器持有焦点时 XAML accelerator 收不到键）。
        var shortcutHandler = _services.GetRequiredService<ShortcutHandler>();
        shortcutHandler.ShortcutInvoked += a => DispatcherQueue.TryEnqueue(() => RunShortcut(a));
        _ipcRouter.Register(shortcutHandler);

        // 表格右键菜单：web 转发坐标+上下文，宿主弹原生 MenuFlyout（见 MainWindow.Table.cs）。
        var tableMenuHandler = _services.GetRequiredService<TableMenuHandler>();
        tableMenuHandler.MenuRequested += info =>
            DispatcherQueue.TryEnqueue(() => ShowTableContextMenu(info));
        _ipcRouter.Register(tableMenuHandler);

        // 图片右键菜单：web 转发坐标+绝对路径+当前样式，宿主弹原生 CommandBarFlyout（见 MainWindow.Image.cs）。
        var imageMenuHandler = _services.GetRequiredService<ImageMenuHandler>();
        imageMenuHandler.MenuRequested += info =>
            DispatcherQueue.TryEnqueue(() => ShowImageContextMenu(info));
        _ipcRouter.Register(imageMenuHandler);

        // 图片裁剪：web（右键/浮动条）请求 → 宿主弹 WinUI 裁剪对话框（见 MainWindow.ImageCrop.cs）。
        var imageCropHandler = _services.GetRequiredService<ImageCropHandler>();
        imageCropHandler.CropRequested += (abs, crop) =>
            DispatcherQueue.TryEnqueue(() => _ = ShowImageCropDialogAsync(abs, crop));
        _ipcRouter.Register(imageCropHandler);

        Log.Information("IPC router attached to WebView2 channel");
    }
}
