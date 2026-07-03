using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mica.Models;
using Mica.Services;
using Serilog;

namespace Mica;

// MainWindow 的「笔记本维护」部分：维护覆盖页（MaintenanceView）抛回来的跨页副作用在此处理——
// 清理未引用图片、定位（打开笔记）、清理断链引用。
public sealed partial class MainWindow
{
    // 清理全部未引用图片（移到回收站）→ 重扫。
    private async void OnCleanOrphansRequested(NotebookImageReport report)
    {
        var paths = report.Orphans.Select(o => o.AbsPath).ToList();
        if (paths.Count == 0) return;

        var ok = await ConfirmAsync(
            "清理未引用图片",
            $"将把「{report.NotebookName}」中 {paths.Count} 个未被任何笔记引用的图片移到回收站"
                + $"（共 {FormatBytes(report.OrphanBytes)}）。\n\n"
                + "这些文件位于 .mica/assets，删除后笔记显示不受影响；如有误删可从回收站还原。",
            "移到回收站", danger: true);
        if (!ok) return;

        var n = await Task.Run(() => _fileService.DeleteOrphanFiles(paths, report.Root, permanent: false));
        Log.Information("清理未引用图片 {N}/{Total} 个：{Root}", n, paths.Count, report.Root);

        await MaintenanceView.Rescan();
    }

    // 定位：打开该笔记并滚到原处。标签会话与笔记本解耦、token 是绝对路径，故跨笔记本也能直接打开。
    // reveal 必须在 editor.load 渲染完之后下发——若需新开/切标签，存进 _pendingReveal，由 EditorTabs_
    // SelectionChanged 发完 editor.load 后立刻补发（保证 web 端先 load 后 reveal，命中新文档）；
    // 若该笔记已是当前激活标签（不会触发 SelectionChanged），直接发。
    private void OnLocateNote((string noteAbs, string revealKind, int revealIndex) t)
    {
        var (noteAbs, kind, index) = t;
        if (string.IsNullOrEmpty(noteAbs) || !File.Exists(noteAbs)) return;

        var already = EditorTabs.SelectedItem is TabViewItem tab && tab.Tag is string p
            && string.Equals(Path.GetFullPath(p), Path.GetFullPath(noteAbs), StringComparison.OrdinalIgnoreCase);

        NavigateTo(new ViewState(ViewKind.Files, _settings.ActiveNotebook?.Id));

        if (already)
        {
            _ipcRouter.SendNotification("editor.reveal", new { kind, index });
        }
        else
        {
            _pendingReveal = (kind, index);
            _ = OpenNoteAsync(noteAbs);
        }
    }

    // 待补发的定位（见 OnLocateNote）。由 EditorTabs_SelectionChanged 发完 editor.load 后消费。
    private (string kind, int index)? _pendingReveal;

    // 由 EditorTabs_SelectionChanged 在发完 editor.load 后调用，补发挂起的定位。
    private void FlushPendingReveal()
    {
        if (_pendingReveal is not { } pr) return;
        _pendingReveal = null;
        _ipcRouter.SendNotification("editor.reveal", new { kind = pr.kind, index = pr.index });
    }

    // 一键迁移：把该笔记本所有图统一搬到「当前图片存储设置」对应位置 + 改写 src + 删除迁移后全局无人引用的源（回收站）。
    // dry-run 预览 → 确认 → 模态进度（阻断编辑）→ 迁移 → 重载打开的标签 → 重扫。
    private async void OnMigrateRequested(NotebookImageReport report)
    {
        var mode = _settings.ImageStorageMode;
        if (!FileService.IsFileStorageMode(mode))
        {
            await ShowMessageAsync("无法迁移",
                "当前图片存储设置为「不复制 / 内嵌」，没有统一的落盘位置。请先在设置里改成某种文件夹模式，再来迁移。");
            return;
        }

        var (images, bytes, notes) = await Task.Run(() => _fileService.PlanImageMigration(report.Root, mode));
        if (images == 0)
        {
            await ShowMessageAsync("无需迁移", "这个笔记本的图片已经都在目标位置了，没有需要搬运的图片。");
            return;
        }

        var modeName = MigrateModeName(mode);
        var ok = await ConfirmAsync(
            "统一图片存储位置",
            $"将把「{report.NotebookName}」中 {images} 处图片引用（约 {FormatBytes(bytes)}，涉及 {notes} 篇笔记）"
                + $"搬到「{modeName}」并改写引用。\n\n"
                + "已在目标位置的图片会跳过；搬运后若某张原图再无任何笔记引用，将移入回收站（可恢复）。\n"
                + "内嵌（base64）与外部链接图片不受影响。",
            "开始迁移", danger: false);
        if (!ok) return;

        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 320, IsIndeterminate = true };
        var status = new TextBlock { Text = "正在迁移图片，请勿关闭…", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(bar);
        panel.Children.Add(status);
        var dlg = new ContentDialog { Title = "迁移图片", Content = panel, XamlRoot = Content.XamlRoot };
        var progress = new Progress<double>(v => { bar.IsIndeterminate = false; bar.Value = v; });

        var showTask = dlg.ShowAsync();
        (int images, int notes) result = (0, 0);
        Exception? failure = null;
        try { result = await _fileService.MigrateImagesAsync(report.Root, mode, progress, default); }
        catch (Exception ex) { failure = ex; Log.Error(ex, "迁移图片失败: {Root}", report.Root); }
        finally { dlg.Hide(); }
        await showTask; // 等进度框真正关闭再弹下一个（同时只能开一个 ContentDialog）

        if (failure is not null)
        {
            await ShowMessageAsync("迁移失败", $"迁移过程中出错：\n{failure.Message}\n\n部分图片可能已迁移，建议重新打开维护页查看。");
            return;
        }

        // 当前打开的标签若属于本笔记本，重载让 web 按新 src 重渲染（非活动标签切回时本就重读盘）。
        if (EditorTabs.SelectedItem is TabViewItem { Tag: string tok } && IsPathUnder(report.Root, tok))
            await ReloadOpenTabAsync(tok);

        // 迁移在 watcher 抑制下进行，且新增/删除的是可见文件（assets/.assets 夹、源图）→ 若迁的是活动笔记本，刷新树。
        if (PathsEqual(_fileService.CurrentFolder, report.Root)) RefreshTree();

        await MaintenanceView.Rescan();
        await ShowMessageAsync("迁移完成", $"已搬运 {result.images} 处图片引用，更新 {result.notes} 篇笔记。");
    }

    // 存储模式中文名（确认/完成文案用）。
    private static string MigrateModeName(int mode) => mode switch
    {
        2 => "与笔记同级目录",
        3 => "同级 assets 文件夹",
        5 => "独立 .assets 文件夹",
        _ => ".mica 统一管理",
    };

    // 清理某条 Markdown 问题：从 .md 删掉整段原文（断链 ![]()/<img>、空块等），若该笔记正打开则重载编辑器，再重扫。
    // kindLabel = 该问题类型（断链/空代码块/空公式块/空表格/空图册），用于对话框文案，不再硬编码「断链」。
    private async void OnCleanRefRequested((string noteAbs, string fullMatch, string kindLabel) t)
    {
        var (noteAbs, fullMatch, kindLabel) = t;
        if (string.IsNullOrEmpty(noteAbs) || string.IsNullOrEmpty(fullMatch) || !File.Exists(noteAbs)) return;

        var isBroken = kindLabel == "断链";
        var title = isBroken ? "清理断链引用" : $"清理{kindLabel}";
        var message = isBroken
            ? "将从笔记中删除这条失效的图片引用（图片文件本就不存在，删除不影响其它内容）。"
            : $"将从笔记中删除这个{kindLabel}（其中没有内容，删除不影响其它内容）。";

        var ok = await ConfirmAsync(title, message, "删除", danger: true);
        if (!ok) return;

        try
        {
            var text = await File.ReadAllTextAsync(noteAbs);
            var idx = text.IndexOf(fullMatch, StringComparison.Ordinal);
            if (idx < 0) return; // 文件已变，找不到原文 → 放弃（重扫会反映最新）
            var updated = text.Remove(idx, fullMatch.Length);
            await File.WriteAllTextAsync(noteAbs, updated, new UTF8Encoding(false));
            await ReloadOpenTabAsync(noteAbs);
        }
        catch (Exception ex) { Log.Error(ex, "清理 Markdown 问题失败: {Note}", noteAbs); }

        await MaintenanceView.Rescan();
    }

    // 清理全部 Markdown 问题：一次确认 → 按笔记分组、每篇一次读写删掉所有问题原文 → 重载 → 重扫。
    private async void OnCleanAllIssuesRequested(NotebookImageReport report)
    {
        if (report.Issues.Count == 0) return;

        var ok = await ConfirmAsync(
            "清理全部 Markdown 问题",
            $"将从「{report.NotebookName}」的笔记中删除全部 {report.Issues.Count} 项问题"
                + "（断链引用 + 各类空块）。\n\n删除的是失效引用 / 空内容本身，不影响其它内容。"
                + "若想逐条查看，请改用每行的「定位」。",
            "全部删除", danger: true);
        if (!ok) return;

        var touched = new List<string>();
        foreach (var grp in report.Issues.GroupBy(i => i.NoteAbsPath))
        {
            if (!File.Exists(grp.Key)) continue;
            try
            {
                var text = await File.ReadAllTextAsync(grp.Key);
                foreach (var issue in grp)
                {
                    var idx = text.IndexOf(issue.FullMatch, StringComparison.Ordinal);
                    if (idx >= 0) text = text.Remove(idx, issue.FullMatch.Length);
                }
                await File.WriteAllTextAsync(grp.Key, text, new UTF8Encoding(false));
                touched.Add(grp.Key);
            }
            catch (Exception ex) { Log.Error(ex, "清理全部问题失败: {Note}", grp.Key); }
        }

        foreach (var note in touched) await ReloadOpenTabAsync(note);
        await MaintenanceView.Rescan();
    }

    // 若某绝对路径正是当前激活标签，重新读盘推给编辑器（非激活标签切回时本就重读，无需处理）。
    private async Task ReloadOpenTabAsync(string noteAbs)
    {
        if (EditorTabs.SelectedItem is TabViewItem tab && tab.Tag is string p
            && string.Equals(Path.GetFullPath(p), Path.GetFullPath(noteAbs), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var body = await _fileService.ReadFileAsync(p);
                _ipcRouter.SendNotification("editor.load", new { relPath = p, body });
            }
            catch (Exception ex) { Log.Warning(ex, "重载笔记失败: {Note}", noteAbs); }
        }
    }

    // 通用确认对话框：按钮正常大小、右对齐（不像默认 ContentDialog 那样两个按钮拉满整行）。
    // **标题/正文/按钮全放进 Content 自绘**，并把 ContentDialogPadding 清零——否则空的原生命令按钮区
    // 会在自绘按钮下方留一大块固定空白（短文本时尤其明显）。经 TaskCompletionSource 返回结果；danger 时主按钮红。
    private async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger)
    {
        var tcs = new TaskCompletionSource<bool>();

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

        var cancel = new Button { Content = "取消", MinWidth = 84 };
        var ok = new Button
        {
            Content = confirmText,
            MinWidth = 84,
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
        };
        if (danger)
        {
            ok.Resources["AccentButtonBackground"] = HexBrush("C42B1C");
            ok.Resources["AccentButtonBackgroundPointerOver"] = HexBrush("B0261A");
            ok.Resources["AccentButtonBackgroundPressed"] = HexBrush("9C2117");
            ok.Resources["AccentButtonForeground"] = HexBrush("FFFFFF");
            ok.Resources["AccentButtonForegroundPointerOver"] = HexBrush("FFFFFF");
            ok.Resources["AccentButtonForegroundPressed"] = HexBrush("FFFFFF");
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { MinWidth = 300, Padding = new Thickness(24) };
        panel.Children.Add(titleBlock);
        panel.Children.Add(msg);
        panel.Children.Add(buttons);

        var dlg = new ContentDialog { Content = panel, XamlRoot = Content.XamlRoot };
        dlg.Resources["ContentDialogPadding"] = new Thickness(0); // 去掉空命令区留白：自己用 panel.Padding 控边距
        // 兜底：直接折叠模板里的空命令按钮区（无原生按钮时它仍占一块固定高度 = 自绘按钮下方那块空白）。
        dlg.Opened += (_, _) =>
        {
            if (FindByName(dlg, "CommandSpace") is { } cs) cs.Visibility = Visibility.Collapsed;
        };

        ok.Click += (_, _) => { tcs.TrySetResult(true); dlg.Hide(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dlg.Hide(); };
        dlg.Closing += (_, _) => tcs.TrySetResult(false); // Esc / 点遮罩

        await dlg.ShowAsync();
        return await tcs.Task;
    }

    // 在可视树里按名字找后代元素（用于折叠 ContentDialog 模板里的 CommandSpace）。
    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
