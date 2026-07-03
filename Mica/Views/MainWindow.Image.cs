using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Mica.IPC.Handlers;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Mica;

// MainWindow partial：图片右键菜单（WinUI 原生 CommandBarFlyout）。
// 由 web 端经 host.imageMenu 转发触发（右键点在图片上时 web 先选中该图，再把坐标+属性发来）。
// 复制图片/复制路径/在资源管理器显示 = 宿主侧直接做（已有绝对路径）；大小/圆角/边框/翻转/对齐/裁剪/删除 = 经
// editor.imageOp 回传 web 在选中图上执行。大小/圆角/边框做成子 Flyout 内嵌滑块（拖动预览、停手提交），
// 取代原图片浮动条（浮条已下线）。
public sealed partial class MainWindow
{
    // 图片右键菜单 = WinUI 官方 **CommandBarFlyout**（对标表格菜单、Win11 资源管理器右键）：
    //   · 顶部图标条（PrimaryCommands）= 可即时调的视觉项：大小 / 圆角 / 边框（各弹子 Flyout 内嵌滑块，
    //     拖动实时预览、不自动关）+ 裁剪（弹对话框）。这套「滑块进子 Flyout」复用底色调色板的子 Flyout 套路，
    //     从而把原图片浮动条的滑块/NumberBox 全部迁进右键菜单（浮条下线，见 task #19）。
    //   · 下方列表（SecondaryCommands）= 对齐 / 翻转 / 复制·路径·资源管理器 / 替换 / 删除。
    //   组内图（图册）大小由 flex 决定、对齐无意义 → 隐藏「大小 / 对齐」。
    //   **图标暂用 Segoe 占位字形**（与表格菜单一致，后续统一重做，不追求语义精确）。
    private void ShowImageContextMenu(ImageMenuHandler.ImageMenuInfo info)
    {
        if (!InEditorView) return;
        // web 传来的绝对路径可能是「正斜杠 + 未解析的 ..」（如 F:/test/日记/../.mica/x.png）——explorer /select 与部分
        // 文件 API 不认这种形式（用户反馈 5「只打开桌面」、反馈 4「复制不到」）。先 GetFullPath 规范成标准 Windows 路径。
        string abs = "";
        if (!string.IsNullOrEmpty(info.Abs))
        {
            try { abs = Path.GetFullPath(info.Abs); } catch { abs = info.Abs; }
        }
        bool hasFile = !string.IsNullOrEmpty(abs); // 网络图/内嵌 base64 无磁盘路径 → 禁用复制/路径/资源管理器项
        bool grouped = info.Grouped;

        var flyout = new CommandBarFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            AlwaysExpanded = true, // 顶部图标条 + 下方列表同时显示
        };

        // ---- 顶部图标条 ----
        var pc = flyout.PrimaryCommands;
        if (!grouped) // 大小：组内图大小由 flex 决定，不出
            pc.Add(new AppBarButton { Label = "大小", Icon = AppIcon(""), Flyout = BuildImageWidthFlyout(info.Width) });
        pc.Add(new AppBarButton { Label = "圆角", Icon = AppIcon(""), Flyout = BuildImageSliderFlyout("radius", 0, 48, info.Radius ?? 2, "圆角 (px)") });
        pc.Add(new AppBarButton { Label = "边框", Icon = AppIcon(""), Flyout = BuildImageSliderFlyout("border", 0, 6, info.Border ?? 0, "边框宽度 (px)") });
        var cropBtn = new AppBarButton { Label = "裁剪", Icon = AppIcon(""), IsEnabled = hasFile };
        cropBtn.Click += (_, _) => _ = ShowImageCropDialogAsync(abs, string.IsNullOrEmpty(info.Crop) ? null : info.Crop);
        pc.Add(cropBtn);

        // ---- 下方列表 ----
        var sc = flyout.SecondaryCommands;
        void SecSep() => sc.Add(new AppBarSeparator());

        // 对齐（组内图无意义 → 不出）：子菜单 左/中/右。
        if (!grouped)
            sc.Add(ImageSubBtn("对齐", AppIcon(""),
                ("左对齐", "alignLeft"), ("居中", "alignCenter"), ("右对齐", "alignRight")));

        // 翻转：水平/垂直镜像（ToggleMenuFlyoutItem 显示当前状态，点击 toggle）。
        var flipSub = new AppBarButton { Label = "翻转", Icon = AppIcon("") };
        var flipMf = Unbounded(new MenuFlyout());
        var flipHItem = new ToggleMenuFlyoutItem { Text = "水平翻转", IsChecked = info.FlipH };
        flipHItem.Click += (_, _) => _ipcRouter.SendNotification("editor.imageOp", new { op = "flipH" });
        var flipVItem = new ToggleMenuFlyoutItem { Text = "垂直翻转", IsChecked = info.FlipV };
        flipVItem.Click += (_, _) => _ipcRouter.SendNotification("editor.imageOp", new { op = "flipV" });
        flipMf.Items.Add(flipHItem);
        flipMf.Items.Add(flipVItem);
        flipSub.Flyout = flipMf;
        sc.Add(flipSub);
        SecSep();

        // 宿主侧直接做的项（已有绝对路径）：复制图片 / 复制路径 / 在资源管理器中显示。
        sc.Add(ImageHostBtn("复制图片", "", () => _ = CopyImageToClipboardAsync(abs), hasFile));
        sc.Add(ImageHostBtn("复制图片路径", "", () => CopyTextToClipboard(abs), hasFile));
        sc.Add(ImageHostBtn("在资源管理器中显示", "", () => RevealImageInExplorer(abs), hasFile));

        // 替换：二级菜单「从文件 / 从剪贴板」（对标 Word）。网络/base64 图也可替换（落成本地图）。
        var replaceSub = new AppBarButton { Label = "替换图片", Icon = AppIcon("") };
        var replaceMf = Unbounded(new MenuFlyout());
        var fromFile = new MenuFlyoutItem { Text = "从文件" };
        fromFile.Click += (_, _) => _ = ReplaceImageFromFileAsync();
        replaceMf.Items.Add(fromFile);
        var fromClip = new MenuFlyoutItem { Text = "从剪贴板", IsEnabled = ClipboardHasImage() };
        fromClip.Click += (_, _) => _ = ReplaceImageFromClipboardAsync();
        replaceMf.Items.Add(fromClip);
        replaceSub.Flyout = replaceMf;
        sc.Add(replaceSub);
        SecSep();

        sc.Add(ImageHostBtn("删除图片", "", () => _ipcRouter.SendNotification("editor.imageOp", new { op = "delete" })));

        flyout.ShowAt(EditorHost, new FlyoutShowOptions { Position = new Point(info.X, info.Y) });
    }

    // ===== 图片菜单项工厂 =====

    // 下方列表里宿主直接执行的项（带图标、可禁用）。
    private AppBarButton ImageHostBtn(string label, string glyph, Action onClick, bool enabled = true)
    {
        var b = new AppBarButton { Label = label, Icon = AppIcon(glyph), IsEnabled = enabled };
        b.Click += (_, _) => onClick();
        return b;
    }

    // 下方列表里的子菜单项（AppBarButton.Flyout = MenuFlyout），各子项点击发 editor.imageOp{op}。
    private AppBarButton ImageSubBtn(string label, IconElement? icon, params (string text, string op)[] items)
    {
        var b = new AppBarButton { Label = label, Icon = icon };
        var mf = Unbounded(new MenuFlyout());
        foreach (var (text, op) in items)
        {
            var it = new MenuFlyoutItem { Text = text };
            it.Click += (_, _) => _ipcRouter.SendNotification("editor.imageOp", new { op });
            mf.Items.Add(it);
        }
        b.Flyout = mf;
        return b;
    }

    // 「大小」子 Flyout：自适应开关 + 宽度百分比滑块。拖动只预览（改实时 DOM、不提交）、停手/关闭才提交，防卡顿
    // （沿用原浮条 input 预览 / change 提交套路，见 imageSetup.runImageOp 的 'preview' 分支）。
    private Flyout BuildImageWidthFlyout(string currentWidth)
    {
        var flyout = Unbounded(new Flyout());
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(8), Width = 220 };

        // 解析当前宽度：百分比 → 关自适应、滑块到该值；空/非% → 开自适应、滑块默认 60。
        int pct = 60; bool auto = true;
        if (!string.IsNullOrEmpty(currentWidth) && currentWidth.EndsWith("%")
            && int.TryParse(currentWidth.TrimEnd('%'), out var p))
        { pct = Math.Clamp(p, 10, 100); auto = false; }

        var toggle = new ToggleSwitch { OnContent = "自适应", OffContent = "指定百分比", IsOn = auto };
        var slider = new Slider { Minimum = 10, Maximum = 100, StepFrequency = 5, Value = pct, IsEnabled = !auto, Header = "显示宽度（占行宽 %）" };

        // 滑块拖动：实时预览 + 防抖提交（见 WireLiveSlider）。自适应开关单独提交。
        WireLiveSlider(slider, flyout,
            v => _ipcRouter.SendNotification("editor.imageOp", new { op = "preview", key = "width", value = $"{v}%" }),
            v => _ipcRouter.SendNotification("editor.imageOp", new { op = "width", value = $"{v}%" }),
            () => !toggle.IsOn); // 自适应时滑块禁用、不提交宽度
        toggle.Toggled += (_, _) =>
        {
            slider.IsEnabled = !toggle.IsOn;
            if (toggle.IsOn) _ipcRouter.SendNotification("editor.imageOp", new { op = "width", value = "" }); // 自适应
            else _ipcRouter.SendNotification("editor.imageOp", new { op = "width", value = $"{(int)Math.Round(slider.Value)}%" });
        };

        panel.Children.Add(toggle);
        panel.Children.Add(slider);
        flyout.Content = panel;
        return flyout;
    }

    // 「圆角 / 边框」子 Flyout：单滑块（0..max）。拖动实时预览、停手/关闭提交。op = radius / border（既是提交 op、也是预览 key）。
    private Flyout BuildImageSliderFlyout(string op, int min, int max, int current, string header)
    {
        var flyout = Unbounded(new Flyout());
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(8), Width = 200 };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = 1, Value = Math.Clamp(current, min, max), Header = header };
        WireLiveSlider(slider, flyout,
            v => _ipcRouter.SendNotification("editor.imageOp", new { op = "preview", key = op, value = v }),
            v => _ipcRouter.SendNotification("editor.imageOp", new { op, value = v }),
            null);
        panel.Children.Add(slider);
        flyout.Content = panel;
        return flyout;
    }

    // 给滑块挂「拖动实时预览 + 防抖提交」：ValueChanged 立刻 preview（改 DOM、不提交）并重置 260ms 防抖；
    // 停手 260ms 或子 Flyout 关闭时 commit（真正写回属性，触发一次事务）。canCommit 为可选门（自适应时挡掉宽度提交）。
    // 初值在调用前已设到 slider（此时还没挂 ValueChanged），故不会误触发首次提交。
    private void WireLiveSlider(Slider slider, Flyout flyout, Action<int> preview, Action<int> commit, Func<bool>? canCommit)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(260);
        timer.IsRepeating = false;
        bool pending = false;
        void DoCommit()
        {
            timer.Stop();
            if (!pending) return;
            pending = false;
            if (canCommit == null || canCommit()) commit((int)Math.Round(slider.Value));
        }
        slider.ValueChanged += (_, e) =>
        {
            if (canCommit != null && !canCommit()) return; // 自适应：滑块禁用态下不动作
            preview((int)Math.Round(e.NewValue));
            pending = true;
            timer.Stop(); timer.Start();
        };
        timer.Tick += (_, _) => DoCommit();
        flyout.Closing += (_, _) => DoCommit();
    }

    // 让子菜单 / 子 Flyout 能溢出应用窗口：FlyoutBase 默认 ShouldConstrainToRootBounds=true → 二级菜单被裁进窗口内
    // （还可能与父级 CommandBarFlyout 的弹出层抢同一层、互相遮挡）。设 false → 走独立 windowed popup（同顶部笔记本
    // 切换器的下拉），能超出窗口、且压在父级之上。图片/表格两个右键菜单的所有子 Flyout / MenuFlyout 都过这个。
    private static T Unbounded<T>(T fb) where T : Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase
    {
        fb.ShouldConstrainToRootBounds = false;
        return fb;
    }

    // ===== 宿主侧图片操作 =====

    // ===== 替换图片（右键菜单「替换图片 ▸ 从文件 / 从剪贴板」，对标 Word） =====
    // 仅右键菜单（浮动条不放，按用户要求避免越加越挤）。选/取新图字节 → 落盘到当前笔记 assets → 回传
    // editor.imageOp{op:'replace', src}；web 端换 src 并按「保持原显示框比例 → 居中裁剪」算 crop（见 imageSetup.replaceImage）。
    // 当前笔记取自活动标签（Tag = 绝对路径）；落盘复用粘贴那套（SavePastedImageAsync + 按存储模式）。

    // 活动标签对应的笔记绝对路径（无活动编辑标签 = null）。
    private string? ActiveNoteAbsPath =>
        EditorTabs.SelectedItem is TabViewItem { Tag: string p } && !string.IsNullOrEmpty(p) ? p : null;

    // 剪贴板是否有位图（决定「从剪贴板」菜单项可否点）。GetContent 偶发抛错 → 视为无。
    private static bool ClipboardHasImage()
    {
        try { return Clipboard.GetContent().Contains(StandardDataFormats.Bitmap); }
        catch { return false; }
    }

    // ===== 插入图片 / 新建图册（格式▸图像 菜单 + Ctrl+Shift+I / Ctrl+Shift+G）=====

    // 菜单「插入图片…」点击 = 快捷键 insertImage 同一逻辑。
    private void OnInsertImage(object sender, RoutedEventArgs e) => _ = InsertImagesFromFilesAsync();
    // 菜单「新建图册」点击 = 快捷键 newGallery 同一逻辑。
    private void OnNewGallery(object sender, RoutedEventArgs e) => InsertEmptyGallery();

    // 插入图片：多选文件 → 逐个落盘到当前笔记 assets → 回传 editor.insertImages。
    // web 端 placeImageSrcs 决定落点：选中图册时追加进册、否则单张普通图 / 多张自动成册。
    private async Task InsertImagesFromFilesAsync()
    {
        if (!InEditorView) return;
        var noteAbs = ActiveNoteAbsPath;
        if (noteAbs is null) return;
        try
        {
            var paths = await PickImagePathsAsync();
            if (paths.Count == 0) return; // 用户取消
            var root = _settings.ResolveNotebookRoot(noteAbs);
            var srcs = new List<string>();
            foreach (var path in paths)
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = "png";
                var src = await _fileService.SavePastedImageAsync(
                    noteAbs, root, bytes, ext, System.IO.Path.GetFileName(path), _settings.ImageStorageMode);
                if (!string.IsNullOrEmpty(src)) srcs.Add(src);
            }
            if (srcs.Count > 0) _ipcRouter.SendNotification("editor.insertImages", new { srcs });
        }
        catch (Exception ex) { Log.Warning(ex, "插入图片失败"); }
    }

    // 从资源管理器把图片文件拖进编辑器（EditorHostView 接 XAML Drop 抛 ImagesDropped）：过滤图片 → 逐个落盘到当前
    // 笔记 assets → 经 editor.dropImages{srcs,x,y} 回传 web，按落点(x,y)放置（命中图册则追加、否则独占一行/多张成册）。
    // WinUI3 未打包应用里 WebView2 转发外部拖放不可靠（一路 ⊘），故改走宿主侧落放，见 EditorHostView 类注释。
    private async void OnEditorImagesDropped(object? sender, Controls.ImagesDroppedEventArgs e)
    {
        if (!InEditorView) return;
        var noteAbs = ActiveNoteAbsPath;
        if (noteAbs is null) return;
        try
        {
            var root = _settings.ResolveNotebookRoot(noteAbs);
            var srcs = new List<string>();
            foreach (var path in e.Paths)
            {
                var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (!IsSupportedImageExt(ext)) continue; // 只收图片，忽略其它文件
                var bytes = await File.ReadAllBytesAsync(path);
                var src = await _fileService.SavePastedImageAsync(
                    noteAbs, root, bytes, ext, System.IO.Path.GetFileName(path), _settings.ImageStorageMode);
                if (!string.IsNullOrEmpty(src)) srcs.Add(src);
            }
            if (srcs.Count > 0)
                _ipcRouter.SendNotification("editor.dropImages", new { srcs, x = e.X, y = e.Y });
        }
        catch (Exception ex) { Log.Warning(ex, "拖入图片失败"); }
    }

    // 拖入文件是否为受支持的图片（按扩展名；与显示管道 ContentTypeFor 支持的格式一致）。
    private static bool IsSupportedImageExt(string ext) => ext switch
    {
        "png" or "jpg" or "jpeg" or "gif" or "webp" or "svg" or "bmp" or "ico" or "avif" or "tif" or "tiff" => true,
        _ => false,
    };

    // 新建空图册：web 端插入一个空 imageGroup（占位态，可后续点占位/粘贴/拖图加图）。
    private void InsertEmptyGallery()
    {
        if (!InEditorView) return;
        _ipcRouter.SendNotification("editor.newGallery", new { });
    }

    // 粘贴剪贴板图片（空图册占位「粘贴」按钮 → host.shortcut pasteImage）：落盘后 editor.insertImages 落点（选中图册则追加）。
    private async Task InsertImageFromClipboardAsync()
    {
        if (!InEditorView) return;
        var noteAbs = ActiveNoteAbsPath;
        if (noteAbs is null) return;
        try
        {
            var img = await ReadClipboardImageAsync();
            if (img is null) return; // 剪贴板无位图
            var root = _settings.ResolveNotebookRoot(noteAbs);
            var src = await _fileService.SavePastedImageAsync(
                noteAbs, root, img.Value.bytes, img.Value.ext, $"clipboard.{img.Value.ext}", _settings.ImageStorageMode);
            if (!string.IsNullOrEmpty(src)) _ipcRouter.SendNotification("editor.insertImages", new { srcs = new[] { src } });
        }
        catch (Exception ex) { Log.Warning(ex, "粘贴图片失败"); }
    }

    // 读剪贴板位图 → (字节, 扩展名)；无位图返回 null。供「替换·从剪贴板」与「图册占位·粘贴」共用。
    private static async Task<(byte[] bytes, string ext)?> ReadClipboardImageAsync()
    {
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Bitmap)) return null;
        var bmpRef = await view.GetBitmapAsync();
        using var stream = await bmpRef.OpenReadAsync();
        var ext = stream.ContentType switch
        {
            "image/jpeg" => "jpg",
            "image/bmp" => "bmp",
            "image/gif" => "gif",
            "image/webp" => "webp",
            _ => "png",
        };
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return (bytes, ext);
    }

    private async Task ReplaceImageFromFileAsync()
    {
        if (!InEditorView) return;
        var noteAbs = ActiveNoteAbsPath;
        if (noteAbs is null) return;
        try
        {
            var path = await PickImagePathAsync();
            if (string.IsNullOrEmpty(path)) return; // 用户取消
            var bytes = await File.ReadAllBytesAsync(path);
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "png";
            await ApplyReplaceAsync(noteAbs, bytes, ext, System.IO.Path.GetFileName(path));
        }
        catch (Exception ex) { Log.Warning(ex, "替换图片(从文件)失败"); }
    }

    private async Task ReplaceImageFromClipboardAsync()
    {
        if (!InEditorView) return;
        var noteAbs = ActiveNoteAbsPath;
        if (noteAbs is null) return;
        try
        {
            var img = await ReadClipboardImageAsync();
            if (img is null) return;
            await ApplyReplaceAsync(noteAbs, img.Value.bytes, img.Value.ext, $"clipboard.{img.Value.ext}");
        }
        catch (Exception ex) { Log.Warning(ex, "替换图片(从剪贴板)失败"); }
    }

    // 落盘 + 回传新 src（两个来源共用）。
    private async Task ApplyReplaceAsync(string noteAbs, byte[] bytes, string ext, string? sourceName)
    {
        var root = _settings.ResolveNotebookRoot(noteAbs);
        var src = await _fileService.SavePastedImageAsync(
            noteAbs, root, bytes, ext, sourceName, _settings.ImageStorageMode);
        _ipcRouter.SendNotification("editor.imageOp", new { op = "replace", src });
    }

    // 复制图片路径用 FileTree 的 CopyTextToClipboard（纯文本复制），不再重复定义。

    // 在资源管理器中定位选中该图片文件（explorer /select，区别于 Sidebar.RevealInExplorer 的「打开路径」）。
    private static void RevealImageInExplorer(string abs)
    {
        try
        {
            if (!File.Exists(abs)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{abs}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "在资源管理器中显示图片失败: {Path}", abs); }
    }

    // 把图片本身复制到剪贴板。用 StorageFile + CreateFromFile 提供位图，并附「文件」格式，最后 Flush 让内容在应用
    // 退出后仍留在系统剪贴板（用户反馈 4「复制后粘不出、系统剪贴板也空」——之前 MemoryStream 流方式部分场景不生效）。
    private static async System.Threading.Tasks.Task CopyImageToClipboardAsync(string abs)
    {
        try
        {
            if (!File.Exists(abs)) return;
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(abs);
            var dp = new DataPackage();
            dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file)); // 位图：粘进编辑器/聊天框
            dp.SetStorageItems(new[] { file });                            // 文件：粘进资源管理器
            Clipboard.SetContent(dp);
            Clipboard.Flush();
        }
        catch (Exception ex) { Log.Warning(ex, "复制图片到剪贴板失败: {Path}", abs); }
    }
}
