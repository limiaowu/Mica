using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using Mica.Controls;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Mica;

// MainWindow partial：图片裁剪对话框（第三期，WinUI3 原生）。
// 由 web（浮动条「裁剪」按钮 / 右键菜单「裁剪图片」）经 host.imageCrop / 菜单触发，传图的磁盘绝对路径 + 当前裁剪框。
// 对话框在**原图**上叠 CropSelector 选框，确定后把归一化裁剪分数经 editor.imageOp{op:'crop'} 回传 web，
// web 写进图片节点的 crop 属性（CSS overflow + 定位实现显示裁剪，原图文件绝不改动 → 可无限次回原图重裁）。
//
// ★布局：自建内容（顶部信息条 + 比例预设 + 选框 + 底部按钮条），**不用 ContentDialog 内置按钮**——这样才能做到
//   「还原全图」靠左、「裁剪/取消」靠右且按钮为正常宽度（内置按钮会等分拉满整宽）。按钮点击置 _cropChoice 后 Hide。
public sealed partial class MainWindow
{
    private async Task ShowImageCropDialogAsync(string abs, string? currentCrop)
    {
        if (!InEditorView || string.IsNullOrEmpty(abs)) return;

        // 浮动条传来的 abs 可能是「正斜杠 + 未解析 ..」（imageAbsPath 的原样）。StorageFile.GetFileFromPathAsync 对路径
        // 格式严格，这类形式会抛异常 → 之前浮动条入口静默失败（右键路径在 ShowImageContextMenu 已规范化故能开）。
        try { abs = Path.GetFullPath(abs); } catch { /* 保留原值再试 */ }
        try { if (!File.Exists(abs)) return; } catch { return; }

        // 取原图自然像素尺寸（裁剪分数相对原图）。**不能**靠「游离 BitmapImage 的 ImageOpened」——未挂可视树的
        // BitmapImage 不解码、ImageOpened 永不触发，await 会永久挂起。改用 BitmapDecoder 直接从文件流读尺寸。
        uint natW, natH;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(abs);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            natW = decoder.PixelWidth;
            natH = decoder.PixelHeight;
            if (natW == 0 || natH == 0) return;
        }
        catch (Exception ex) { Log.Warning(ex, "裁剪：读取图片尺寸失败 {Path}", abs); return; }

        // 显示用 BitmapImage(Uri)——与笔记本封面同路径，挂进对话框可视树后正常渲染。
        var bmp = new BitmapImage(new Uri(abs));
        var selector = new CropSelector(bmp, natW, natH, maxW: 760, maxH: 520);
        if (ParseCrop(currentCrop) is { } c) selector.SetCrop(c.L, c.T, c.W, c.H);

        var content = BuildCropContent(selector, Path.GetFileName(abs), natW, natH);

        var dialog = new ContentDialog
        {
            Title = "裁剪图片",
            Content = content,
            XamlRoot = Content.XamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1100.0;

        // 自建按钮的结果：0 取消 / 1 裁剪 / 2 还原全图。
        _cropChoice = 0;
        _cropDialog = dialog;
        await dialog.ShowAsync();
        _cropDialog = null;

        if (_cropChoice == 1)
        {
            // 选框≈整图 → 视为没裁，存 null（回到干净显示/Markdown）。
            string? cropStr = selector.IsFullImage() ? null : FormatCrop(selector.GetCrop());
            _ipcRouter.SendNotification("editor.imageOp", new { op = "crop", crop = cropStr });
        }
        else if (_cropChoice == 2)
        {
            _ipcRouter.SendNotification("editor.imageOp", new { op = "crop", crop = (string?)null }); // 还原全图
        }
        // 0 取消：什么都不做。
    }

    private int _cropChoice;
    private ContentDialog? _cropDialog;

    // 构建裁剪对话框内容：顶部信息条 + 比例预设按钮 + 选框 + 底部按钮条。
    private FrameworkElement BuildCropContent(CropSelector selector, string fileName, uint natW, uint natH)
    {
        var root = new Grid { RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 信息条
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 比例预设
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 选框
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮条

        // —— 信息条：左 文件名，右 可编辑「W × H」（裁剪区原图像素，编辑=从中心缩放裁剪框）+ 原图尺寸 ——
        var infoBar = new Grid();
        infoBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameText = new TextBlock
        {
            Text = fileName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        infoBar.Children.Add(nameText);

        var rightInfo = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var wBox = new NumberBox { Minimum = 1, Maximum = natW, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, MinWidth = 78, SmallChange = 1, LargeChange = 10 };
        var hBox = new NumberBox { Minimum = 1, Maximum = natH, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, MinWidth = 78, SmallChange = 1, LargeChange = 10 };
        ToolTipService.SetToolTip(wBox, "裁剪宽度（原图像素，从中心缩放）");
        ToolTipService.SetToolTip(hBox, "裁剪高度（原图像素，从中心缩放）");
        rightInfo.Children.Add(wBox);
        rightInfo.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        rightInfo.Children.Add(hBox);
        rightInfo.Children.Add(new TextBlock { Text = $"·  原图 {natW}×{natH}", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        Grid.SetColumn(rightInfo, 1);
        infoBar.Children.Add(rightInfo);

        bool suppressSize = false;     // 程序回填 NumberBox 时挡掉 ValueChanged，避免与拖动互相打架
        void UpdateInfo()
        {
            // 拖动中跳过 NumberBox 回填——每次 move 改 NumberBox.Value 会触发其格式化/文字重排，大幅拖动时明显卡顿；
            // 松手时 OnReleased 的最终 Layout 会再触发一次 CropChanged，那时 IsDragging=false 正常补刷。
            if (selector.IsDragging) return;
            var (cw, ch) = selector.CropPixelSize();
            suppressSize = true;
            wBox.Value = cw; hBox.Value = ch;
            suppressSize = false;
        }
        void OnSizeEdited()
        {
            if (suppressSize) return;
            int w = double.IsNaN(wBox.Value) ? 0 : (int)Math.Round(wBox.Value);
            int h = double.IsNaN(hBox.Value) ? 0 : (int)Math.Round(hBox.Value);
            if (w < 1 || h < 1) return;
            selector.SetCropPixelSize(w, h); // 内部 Layout 触发 CropChanged → UpdateInfo 回填（受 suppress 保护）
        }
        wBox.ValueChanged += (_, _) => OnSizeEdited();
        hBox.ValueChanged += (_, _) => OnSizeEdited();
        selector.CropChanged += UpdateInfo;
        UpdateInfo();
        Grid.SetRow(infoBar, 0);
        root.Children.Add(infoBar);

        // —— 比例预设：自由 / 1:1 / 4:3 / 3:2 / 16:9 / 9:16；选中后锁比例，再点「自由」解锁 ——
        var ratioBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var ratios = new (string label, double? r)[]
        {
            ("自由", null), ("1:1", 1.0), ("4:3", 4.0 / 3), ("3:2", 3.0 / 2), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
        };
        var ratioBtns = new List<ToggleButton>();
        foreach (var (label, r) in ratios)
        {
            var tb = new ToggleButton { Content = label, MinWidth = 0, Padding = new Thickness(12, 4, 12, 4) };
            tb.Click += (_, _) =>
            {
                foreach (var o in ratioBtns) o.IsChecked = false;
                tb.IsChecked = true;
                selector.SetAspectRatio(r);
            };
            ratioBtns.Add(tb);
            ratioBar.Children.Add(tb);
        }
        ratioBtns[0].IsChecked = true; // 默认「自由」
        Grid.SetRow(ratioBar, 1);
        root.Children.Add(ratioBar);

        // —— 选框 ——
        Grid.SetRow(selector, 2);
        root.Children.Add(selector);

        // —— 按钮条：左「还原全图」，右「裁剪 / 取消」（正常宽度） ——
        var btnBar = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        btnBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fullBtn = new Button { Content = "还原全图", HorizontalAlignment = HorizontalAlignment.Left };
        fullBtn.Click += (_, _) => { _cropChoice = 2; _cropDialog?.Hide(); };

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var cropBtn = new Button { Content = "裁剪", MinWidth = 96 };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style accentStyle)
            cropBtn.Style = accentStyle;
        cropBtn.Click += (_, _) => { _cropChoice = 1; _cropDialog?.Hide(); };
        var cancelBtn = new Button { Content = "取消", MinWidth = 96 };
        cancelBtn.Click += (_, _) => { _cropChoice = 0; _cropDialog?.Hide(); };
        rightBtns.Children.Add(cropBtn);
        rightBtns.Children.Add(cancelBtn);
        Grid.SetColumn(rightBtns, 1);

        btnBar.Children.Add(fullBtn);
        btnBar.Children.Add(rightBtns);
        Grid.SetRow(btnBar, 3);
        root.Children.Add(btnBar);

        return root;
    }

    // "L,T,W,H"（不变文化、最多 4 位小数）解析；非法/宽高<=0 返回 null。
    private static (double L, double T, double W, double H)? ParseCrop(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',');
        if (parts.Length != 4) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            return null;
        if (w <= 0 || h <= 0) return null;
        return (l, t, w, h);
    }

    private static string FormatCrop((double L, double T, double W, double H) c)
    {
        static string F(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return $"{F(c.L)},{F(c.T)},{F(c.W)},{F(c.H)}";
    }
}
