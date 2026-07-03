using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Mica.Models;
using Serilog;

namespace Mica;

// Part of MainWindow — 文件树拖拽（P3）。
//
// 设计要点：
//  • 复用引擎：落盘全部走 TransferRelsIntoAsync（即粘贴用的那套），含「自身/子孙/原父目录」逐项守卫——
//    所以多选拖到「选中集里的某个文件夹」上，那个文件夹本身被跳过、其余项照常进去（与粘贴语义一致）。
//  • 排序树无「插入位置」概念（目录在前、按名排序，落点由名字定），故反馈＝高亮「目标行」而非插入指示线。
//  • 默认移动，按住 Ctrl 复制（对标 Windows，光标/提示文字随之变）。
//  • 悬停展开（spring-load）：拖拽悬在折叠文件夹上 0.7s 自动展开，方便丢进深层。
//  • 仅处理本树内部拖拽（_dragRels 非空才接管）；外部（资源管理器）文件拖入暂不支持。
public sealed partial class MainWindow
{
    // 当前正在拖拽的源（相对路径，已收敛成 TopLevelSelection）。拖拽结束（Drop/取消）即清空。
    private List<string> _dragRels = [];
    // 当前高亮的落点行（文件夹本身，或被拖到的文件所在目录的那一行）。
    private FileNode? _dropTargetNode;
    // 悬停展开计时：悬在折叠文件夹上 _springTimer 到点即展开它。
    private FileNode? _springNode;
    private DispatcherTimer? _springTimer;
    // 拖拽时边缘自动滚动：记录指针相对滚动区的 Y，计时器按 Y 靠近上/下边缘持续滚动（#4）。
    private DispatcherTimer? _autoScrollTimer;
    private double _dragPointerY;
    private bool _dragPointerValid;

    // 落点目录：拖到文件夹→丢进它；拖到文件→丢进它所在目录。
    private string DropDirForRow(FileNode node) => node.IsDirectory ? node.RelPath : ParentDir(node.RelPath);

    private bool IsValidDropPair(string targetDir, string srcRel, bool isCopy)
    {
        // 不能把文件夹丢进自己或自己的子孙；移动到原父目录是空操作（复制到原父目录则生成 xxx (1) 副本，允许）。
        if (targetDir == srcRel ||
            targetDir.StartsWith(srcRel + "/", StringComparison.OrdinalIgnoreCase)) return false;
        if (!isCopy && targetDir == ParentDir(srcRel)) return false;
        return true;
    }

    // 这一落点是否至少有一项可放（决定接受/拒绝整次拖拽，呈现对应光标）。
    private bool AnyValidDropTarget(string targetDir, bool isCopy) =>
        _dragRels.Any(r => IsValidDropPair(targetDir, r, isCopy));

    // 落点目录的显示名（根目录 / 文件夹名），用于拖拽提示文字。
    private string DropTargetName(string targetDir) =>
        string.IsNullOrEmpty(targetDir) ? "根目录" : Path.GetFileName(targetDir);

    // 评估这一落点：可放→(true, "移动到 xxx"/"复制到 xxx")；不可放→(false, 原因文字)。
    private (bool ok, string caption) EvalDrop(string targetDir, bool isCopy)
    {
        if (AnyValidDropTarget(targetDir, isCopy))
            return (true, (isCopy ? "复制到 " : "移动到 ") + DropTargetName(targetDir));

        // 整批都不可放，按第一项给出原因（最常见是单项拖拽）。
        var r = _dragRels[0];
        if (targetDir == r || targetDir.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase))
            return (false, "不能移到自身或其子目录");
        return (false, _dragRels.Count > 1 ? "已在当前目录" : $"「{Path.GetFileName(r)}」已在当前目录");
    }

    // 把评估结果写进系统拖拽提示（光标旁的小标签 + 接受/禁止图标）。
    private static void ApplyDropCaption(DragEventArgs e, bool ok, string caption, bool isCopy)
    {
        e.AcceptedOperation = ok
            ? (isCopy ? DataPackageOperation.Copy : DataPackageOperation.Move)
            : DataPackageOperation.None;
        if (e.DragUIOverride is { } o)
        {
            o.Caption = caption;
            o.IsCaptionVisible = true;
            o.IsGlyphVisible = true;
        }
    }

    // --- 外部拖入（资源管理器 → 目录树，一律复制） ---

    // 这次拖拽是否来自本树之外、且携带文件/文件夹（StorageItems）。本树内部拖拽只读 _dragRels，
    // 故「_dragRels 为空 + 含 StorageItems」即外部拖入。
    private static bool IsExternalDrag(DragEventArgs e) =>
        e.DataView.Contains(StandardDataFormats.StorageItems);

    // 外部 Drop：读 StorageItems（异步，需 Deferral 撑住）后复制进 targetDir。读盘期间先清落点高亮。
    private async Task HandleExternalDropAsync(DragEventArgs e, string targetDir)
    {
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            SetDropTarget(null);
            RootRowHighlight.Visibility = Visibility.Collapsed;
            await ImportExternalItemsAsync(items, targetDir);
        }
        catch (Exception ex) { Log.Error(ex, "External drop import failed"); }
        finally { deferral.Complete(); ClearDragState(); }
    }

    // 把任意外部文件/文件夹复制进当前笔记本 targetDir。复用搬运引擎（srcRoot=源父目录——外部源无 .mica
    // 资产，CarryAssetsAsync 空转）；逐项去重命名，落盘后原地 splice 节点。
    private async Task ImportExternalItemsAsync(IReadOnlyList<IStorageItem> items, string targetDir)
    {
        if (items.Count == 0 || _fileService.CurrentFolder is null) return;
        targetDir = (targetDir ?? "").Replace('\\', '/').Trim('/');
        foreach (var item in items)
        {
            var srcAbs = item.Path;
            if (string.IsNullOrEmpty(srcAbs)) continue;            // 虚拟项（无文件系统路径）跳过
            var srcRoot = Path.GetDirectoryName(srcAbs);
            if (string.IsNullOrEmpty(srcRoot)) continue;
            string destRel;
            try { destRel = await _fileService.TransferEntryAsync(srcAbs, srcRoot, targetDir, isCopy: true); }
            catch (Exception ex) { Log.Error(ex, "External import failed: {Src} -> {Dir}", srcAbs, targetDir); continue; }
            InsertNewEntry(targetDir, destRel);
        }
    }

    // --- 起拖 ---

    private void FileRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (_fileService.CurrentFolder is null || sender is not FrameworkElement { DataContext: FileNode node })
        { args.Cancel = true; return; }

        // 拖一个在选中集里的行 → 拖整批；拖一个不在选中集里的行 → 只拖它并选中它（对标 Explorer）。
        List<FileNode> dragged;
        if (_selection.Contains(node)) dragged = TopLevelSelection();
        else { SelectOnly(node); _selectionAnchor = node; dragged = [node]; }

        _dragRels = dragged.Select(n => n.RelPath).ToList();
        if (_dragRels.Count == 0) { args.Cancel = true; return; }

        args.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
        // 放点文本占位防止 DragStarting 取消（内部拖拽只读 _dragRels，不依赖剪贴板内容）。
        args.Data.SetText(string.Join(Environment.NewLine, _dragRels));
        StartAutoScroll();
    }

    // 源元素：拖拽结束（落下或取消/Esc）都会触发，统一在此清状态——比只靠 Drop 更稳。
    private void FileRow_DropCompleted(UIElement sender, DropCompletedEventArgs args) => ClearDragState();

    // --- 落点反馈（行） ---

    private void FileRow_DragEnter(object sender, DragEventArgs e) => UpdateRowDragFeedback(sender, e);
    private void FileRow_DragOver(object sender, DragEventArgs e) => UpdateRowDragFeedback(sender, e);

    private void UpdateRowDragFeedback(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileNode node }) return;
        var targetDir = DropDirForRow(node);

        if (_dragRels.Count == 0)
        {
            // 非本树发起：仅当是资源管理器文件/文件夹拖入才接管（一律复制，目标=该行对应目录）。
            if (IsExternalDrag(e))
            {
                e.Handled = true;
                ApplyDropCaption(e, true, "复制到 " + DropTargetName(targetDir), isCopy: true);
                SetDropTarget(node);
            }
            return;
        }
        e.Handled = true; // 截断，别冒泡到 ScrollViewer 的根目录落点
        TrackDragPointer(e);

        var copy = IsKeyDown(VirtualKey.Control);
        var (ok, caption) = EvalDrop(targetDir, copy);
        ApplyDropCaption(e, ok, caption, copy);
        SetDropTarget(ok ? node : null); // 不可放就不高亮
    }

    private void FileRow_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode node } && ReferenceEquals(_dropTargetNode, node))
            SetDropTarget(null);
    }

    private async void FileRow_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: FileNode node }) { ClearDragState(); return; }
        var targetDir = DropDirForRow(node);

        if (_dragRels.Count == 0)
        {
            if (IsExternalDrag(e)) await HandleExternalDropAsync(e, targetDir);
            else ClearDragState();
            return;
        }
        var copy = IsKeyDown(VirtualKey.Control);
        var rels = _dragRels.ToList();
        ClearDragState();
        await TransferRelsIntoAsync(rels, targetDir, isCopy: copy);
    }

    // --- 落点反馈（根目录：ScrollViewer 空白处） ---

    private void FileTreeRoot_DragOver(object sender, DragEventArgs e)
    {
        if (_dragRels.Count == 0)
        {
            if (IsExternalDrag(e))
                ApplyDropCaption(e, true, "复制到 " + DropTargetName(""), isCopy: true);
            return;
        }
        TrackDragPointer(e);
        var copy = IsKeyDown(VirtualKey.Control);
        var (ok, caption) = EvalDrop("", copy);
        ApplyDropCaption(e, ok, caption, copy);
        SetDropTarget(null); // 根目录无行可高亮
    }

    private async void FileTreeRoot_Drop(object sender, DragEventArgs e)
    {
        if (_dragRels.Count == 0)
        {
            if (IsExternalDrag(e)) await HandleExternalDropAsync(e, "");
            return;
        }
        var rels = _dragRels.ToList();
        ClearDragState();
        if (rels.Count == 0) return;
        var copy = IsKeyDown(VirtualKey.Control);
        await TransferRelsIntoAsync(rels, "", isCopy: copy);
    }

    // --- 固定「根目录行」落点（树顶常驻行，丢这里＝丢到根目录；带专属高亮） ---

    private void RootRow_DragOver(object sender, DragEventArgs e)
    {
        if (_dragRels.Count == 0)
        {
            if (IsExternalDrag(e))
            {
                e.Handled = true;
                ApplyDropCaption(e, true, "复制到 " + DropTargetName(""), isCopy: true);
                RootRowHighlight.Visibility = Visibility.Visible;
            }
            return;
        }
        e.Handled = true;
        TrackDragPointer(e);
        var copy = IsKeyDown(VirtualKey.Control);
        var (ok, caption) = EvalDrop("", copy);
        ApplyDropCaption(e, ok, caption, copy);
        RootRowHighlight.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        SetDropTarget(null);
    }

    private void RootRow_DragLeave(object sender, DragEventArgs e) =>
        RootRowHighlight.Visibility = Visibility.Collapsed;

    private async void RootRow_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        RootRowHighlight.Visibility = Visibility.Collapsed;
        if (_dragRels.Count == 0)
        {
            if (IsExternalDrag(e)) await HandleExternalDropAsync(e, "");
            return;
        }
        var rels = _dragRels.ToList();
        ClearDragState();
        if (rels.Count == 0) return;
        var copy = IsKeyDown(VirtualKey.Control);
        await TransferRelsIntoAsync(rels, "", isCopy: copy);
    }

    // --- 高亮 + 悬停展开 ---

    private void SetDropTarget(FileNode? node)
    {
        if (ReferenceEquals(_dropTargetNode, node)) return;
        if (_dropTargetNode is not null) _dropTargetNode.IsDropTarget = false;
        _dropTargetNode = node;
        if (node is not null) node.IsDropTarget = true;
        ResetSpring(node); // 目标变了就重排悬停展开计时
    }

    // 目标是「折叠的文件夹」时启动悬停展开计时；否则停掉。
    private void ResetSpring(FileNode? node)
    {
        _springTimer?.Stop();
        _springNode = null;
        if (node is { IsDirectory: true, IsExpanded: false })
        {
            _springNode = node;
            _springTimer ??= CreateSpringTimer();
            _springTimer.Start();
        }
    }

    private DispatcherTimer CreateSpringTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            // 到点仍悬在同一折叠文件夹上才展开（中途离开则被 ResetSpring 清掉，不会误展开）。
            if (_springNode is { IsDirectory: true, IsExpanded: false } n && ReferenceEquals(n, _dropTargetNode))
                ExpandRow(n);
        };
        return t;
    }

    private void ClearDragState()
    {
        _dragRels = [];
        SetDropTarget(null); // 连带清高亮 + 停悬停展开
        RootRowHighlight.Visibility = Visibility.Collapsed;
        StopAutoScroll();
    }

    // --- 拖拽边缘自动滚动（#4） ---

    private void TrackDragPointer(DragEventArgs e)
    {
        _dragPointerY = e.GetPosition(FileTreeScroller).Y;
        _dragPointerValid = true;
    }

    private void StartAutoScroll()
    {
        _dragPointerValid = false;
        _autoScrollTimer ??= CreateAutoScrollTimer();
        _autoScrollTimer.Start();
    }

    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
        _dragPointerValid = false;
    }

    private DispatcherTimer CreateAutoScrollTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) =>
        {
            if (!_dragPointerValid || FileTreeScroller is null) return;
            const double edge = 32;  // 上/下边缘感应带高度
            const double step = 16;   // 每 tick 滚动量
            var vp = FileTreeScroller.ViewportHeight;
            var offset = FileTreeScroller.VerticalOffset;
            if (_dragPointerY < edge && offset > 0)
                FileTreeScroller.ChangeView(null, Math.Max(0, offset - step), null, true);
            else if (_dragPointerY > vp - edge && offset < FileTreeScroller.ScrollableHeight)
                FileTreeScroller.ChangeView(null, Math.Min(FileTreeScroller.ScrollableHeight, offset + step), null, true);
        };
        return t;
    }
}
