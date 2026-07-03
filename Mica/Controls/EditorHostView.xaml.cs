using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Mica.Controls;

// 外部文件拖入事件参数：被拖入的文件绝对路径 + 落点（相对 WebView 视口的 DIP ≈ web 的 CSS 像素坐标）。
public sealed class ImagesDroppedEventArgs : EventArgs
{
    public IReadOnlyList<string> Paths { get; }
    public double X { get; }
    public double Y { get; }
    public ImagesDroppedEventArgs(IReadOnlyList<string> paths, double x, double y) { Paths = paths; X = x; Y = y; }
}

public sealed partial class EditorHostView : UserControl
{
    public event EventHandler<string>? WebMessageReceived;
    // Raised once the editor page has finished loading and is ready to receive
    // host notifications (e.g. an initial theme push).
    public event EventHandler? WebReady;
    // 从资源管理器把文件拖进编辑器时触发（宿主侧接管，见类注释 / OnRootDrop）。MainWindow 订阅后落盘+回传 web。
    public event EventHandler<ImagesDroppedEventArgs>? ImagesDropped;
    private bool _initialized;

    public EditorHostView()
    {
        InitializeComponent();
        EditorWebView.CoreWebView2Initialized += OnCoreWebView2Initialized;
        Loaded += OnLoaded;

        // 外部文件拖放兜底：本 SDK 的 WinUI3 WebView2 元素未暴露 AllowExternalDrop，无法显式关掉它的外部拖放。
        // 故两条路都接上、互斥生效（OLE 单一落点）：① WebView2 把外部拖放转发进网页 → 由 web 端 document 放置区处理；
        // ② WebView2 不转发、拖放落到这层 RootGrid（AllowDrop=True）→ 由下面两个处理器接管。谁收到谁处理，不会重复。
        RootGrid.DragOver += OnRootDragOver;
        RootGrid.Drop += OnRootDrop;
    }

    // 拖入悬停：含文件（StorageItems）时接受为「复制」，去掉 ⊘ 并显示「插入图片」提示。
    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } ui)
        {
            ui.Caption = "插入图片";
            ui.IsContentVisible = true;
            ui.IsGlyphVisible = true;
        }
        e.Handled = true;
    }

    // 放下：取文件绝对路径 + 落点坐标，抛 ImagesDropped 交宿主处理（落盘 → editor.dropImages 回传 web 插入）。
    // 异步取 StorageItems 期间用 Deferral 保活数据；坐标须在 await 前同步取（事件参数 await 后会失效）。
    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var pos = e.GetPosition(EditorWebView); // 相对 WebView 视口的 DIP，约等于 web clientX/clientY
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = new List<string>();
            foreach (var item in items)
                if (item is StorageFile file && !string.IsNullOrEmpty(file.Path))
                    paths.Add(file.Path);
            if (paths.Count > 0)
                ImagesDropped?.Invoke(this, new ImagesDroppedEventArgs(paths, pos.X, pos.Y));
        }
        catch (Exception ex) { Log.Warning(ex, "处理拖入文件失败"); }
        finally { deferral.Complete(); }
    }

    public bool IsReady => EditorWebView.CoreWebView2 is not null;

    public void PostWebMessageAsJson(string json)
    {
        if (EditorWebView.CoreWebView2 is null)
        {
            Log.Warning("WebView2 not ready, message dropped: {Json}", json);
            return;
        }

        EditorWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    // 把键盘焦点交还给 WebView2，让网页里的 view.focus() 真正显示光标。点状态栏「源代码模式」开关等
    // XAML 控件会把 OS 焦点抢走，此后网页内 focus() 只设 activeElement、却不画光标（用户反馈「切换后光标消失」）。
    public void FocusEditor() => EditorWebView.Focus(FocusState.Programmatic);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;

        try
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await EditorWebView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize WebView2");
        }
    }

    // 拦截 https://img.mica.local/img?p=<abs> → 从磁盘读图同步返回（本地笔记图不大；
    // WebResourceRequested 在 UI 线程，同步设 Response 最简单可靠）。失败/不存在返回 404。
    private void OnImageResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            var abs = TryResolveImagePath(args.Request.Uri);
            if (abs is null || !File.Exists(abs))
            {
                args.Response = sender.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", "Access-Control-Allow-Origin: *");
                return;
            }

            var stream = new MemoryStream(File.ReadAllBytes(abs)).AsRandomAccessStream();
            var headers =
                $"Content-Type: {ContentTypeFor(abs)}\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Access-Control-Allow-Origin: *";
            args.Response = sender.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to serve image for {Uri}", args.Request.Uri);
            try
            {
                args.Response = sender.Environment.CreateWebResourceResponse(
                    null, 500, "Error", "Access-Control-Allow-Origin: *");
            }
            catch { /* core gone */ }
        }
    }

    // 从 img.mica.local/img?p=<encoded> 取出 p、解码、规范化成绝对路径。
    private static string? TryResolveImagePath(string requestUri)
    {
        var uri = new Uri(requestUri);
        var query = uri.Query; // 形如 ?p=xxx
        const string key = "p=";
        var idx = query.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        var raw = query[(idx + key.Length)..];
        var amp = raw.IndexOf('&');
        if (amp >= 0) raw = raw[..amp];
        var decoded = Uri.UnescapeDataString(raw);
        if (string.IsNullOrWhiteSpace(decoded)) return null;
        try { return Path.GetFullPath(decoded); }
        catch { return null; }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };

    private void OnCoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (sender.CoreWebView2 is null)
        {
            Log.Error("CoreWebView2 is null after initialization");
            return;
        }

        _initialized = true;

        sender.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

        // 关掉 WebView2 自带的「浏览器快捷键」：Ctrl+1~9（切标签）、Ctrl+0/Ctrl+加减（缩放）、
        // Ctrl+F/P/S/O（查找/打印/保存/打开）、F5（刷新）、Ctrl+Shift+K 等。否则编辑器获得焦点时，
        // 这些键会被 WebView2 吞掉，传不到顶部菜单栏的 KeyboardAccelerator，导致「段落」里
        // Ctrl+1~6 / Ctrl+0 / Ctrl+Shift+Q/K 等快捷键全部失效。关掉后它们会冒泡回 XAML 由菜单处理。
        // 注意：这只影响浏览器级快捷键，编辑用的 Ctrl+C/V/X/Z（复制粘贴撤销）不受影响，仍正常工作。
        sender.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // 关掉 WebView2 自带的 Ctrl+滚轮缩放，改由 web 层用 CSS zoom 完全接管（main.ts），
        // 这样才能提供「重置 / 状态栏百分比 / 点击直接设 / 开关 Ctrl+滚轮」等功能。
        sender.CoreWebView2.Settings.IsZoomControlEnabled = false;

        var editorPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Editor");
        if (!Directory.Exists(editorPath))
        {
            Log.Warning("Editor assets not found at {Path}", editorPath);
            return;
        }

        sender.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "editor.mica.local",
            editorPath,
            CoreWebView2HostResourceAccessKind.Allow);

        // 本地图片显示管道：编辑器挂在 https 源，https 页面里 <img src="file://…"> 会被
        // WebView2 安全策略拦掉 → 裂图。故 web 端图片 DOM src 统一写成
        // https://img.mica.local/img?p=<encodeURIComponent(绝对路径)>，由 host 拦截后从磁盘
        // 读字节流喂回（同表格「host 喂数据」思路）。这样任意位置的本地图都能显示，无需 base64。
        sender.CoreWebView2.AddWebResourceRequestedFilter(
            "https://img.mica.local/*", CoreWebView2WebResourceContext.Image);
        sender.CoreWebView2.WebResourceRequested += OnImageResourceRequested;

        sender.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            var json = args.WebMessageAsJson;
            WebMessageReceived?.Invoke(this, json);
        };

        sender.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            WebReady?.Invoke(this, EventArgs.Empty);
        };

        sender.Source = new Uri("https://editor.mica.local/index.html");
        Log.Information("WebView2 initialized, loading editor from {Path}", editorPath);
    }
}
