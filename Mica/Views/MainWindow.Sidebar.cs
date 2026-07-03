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
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Mica.Models;
using Mica.Services;
using Mica.IPC;
using Mica.IPC.Handlers;
using Serilog;

namespace Mica;

// Part of the MainWindow partial class. Sidebar (file tree / outline) header, outline, search, splitter.
public sealed partial class MainWindow
{
    // --- Sidebar views (目录树 / 大纲), chosen from the SelectorBar above the panel ---

    // 防止 UpdateSidebarToggle 程序化设置 SelectedItem 时回环触发 SelectionChanged。
    private bool _syncingSelector;

    // 用户点切换条：仅在与当前状态不一致时切视图（避免回环 + 重复切换）。
    private void SidebarSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingSelector) return;
        if (ReferenceEquals(sender.SelectedItem, SelectorOutline)) { if (!_outlineMode) ShowSidebarOutline(); }
        else { if (_outlineMode) ShowSidebarFiles(); }
    }

    // Reflect _outlineMode on the SelectorBar (no-op if already on the right item).
    private void UpdateSidebarToggle()
    {
        _syncingSelector = true;
        SidebarSelector.SelectedItem = _outlineMode ? SelectorOutline : SelectorFiles;
        _syncingSelector = false;
    }

    // --- Tree/大纲 panel resize (drag the grip on TreePanel's right edge → TreePanel.Width) ---
    // The grip is HorizontalAlignment=Right inside TreePanel, so it tracks the edge with no manual
    // positioning, and hides with the panel when collapsed.

    private bool _resizingPane;
    private double _paneResizeStartX;
    private double _paneResizeStartWidth;

    // 侧栏（工作区）最小宽度：抬到 224，容纳工具条「左段(3 键)+视图组(3 键)」，避免多选/编辑按钮被裁
    // （2026-06，原为 200 会裁掉部分按钮）。窗口最小宽度仍是 MinWidthDip(800)。
    private const double SidebarMinWidth = 224;

    // 把侧栏宽夹到 [224, min(520, 可用宽)]。
    // ★原先这里还减去 EditorMinWidth(500) 给编辑区留「表格浮动条三行可见」的硬下限——已按用户要求取消
    //   （该下限反而让表格到达后仍被继续压窄；浮动条逻辑用户后续会重做）。编辑区不被侧栏挤爆，改由
    //   「侧栏最多 520」这一上限来兜底。拖动与窗口缩放共用本方法。
    private double ClampSidebarWidth(double w)
    {
        double upper = Math.Max(SidebarMinWidth, Math.Min(520, EditorSurface.ActualWidth));
        return Math.Clamp(w, SidebarMinWidth, upper);
    }

    private void OnPaneResizePressed(object sender, PointerRoutedEventArgs e)
    {
        _resizingPane = true;
        _paneResizeStartX = e.GetCurrentPoint((UIElement)Content).Position.X;
        _paneResizeStartWidth = TreePanel.ActualWidth;
        PaneResizeGrip.CapturePointer(e.Pointer);
    }

    private void OnPaneResizeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingPane) return;
        var x = e.GetCurrentPoint((UIElement)Content).Position.X;
        TreePanel.Width = ClampSidebarWidth(_paneResizeStartWidth + (x - _paneResizeStartX));
    }

    // 窗口/编辑面板变窄时主动收紧侧栏——否则固定像素的 TreePanel 不动、只压编辑区，且之后一拖侧栏
    // 就因上限（min(520,可用宽)）骤降而跳变。SizeChanged 时按当前可用宽重夹一次。
    private void EditorSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_resizingPane) return;                              // 拖动中由 OnPaneResizeMoved 管，避免抖动
        if (TreePanel.Visibility != Visibility.Visible) return; // 侧栏收起时不动它
        double target = ClampSidebarWidth(TreePanel.ActualWidth);
        if (Math.Abs(target - TreePanel.ActualWidth) > 0.5)
        {
            TreePanel.Width = target;
            _settings.SidebarWidth = target;
        }
    }

    private void OnPaneResizeReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizingPane = false;
        PaneResizeGrip.ReleasePointerCapture(e.Pointer);
        _settings.SidebarWidth = TreePanel.Width; // persist
    }

    private void ShowSidebarFiles()
    {
        _outlineMode = false;
        UpdateSidebarHeader();
        UpdateSidebarToggle();
        TreeToolbar.Visibility = Visibility.Visible;
        RootDropRow.Visibility = Visibility.Visible;
        FileTreeScroller.Visibility = Visibility.Visible;
        OutlineList.Visibility = Visibility.Collapsed;
        OutlineEmptyState.Visibility = Visibility.Collapsed;
        SetSidebarVisible(true);
    }

    private void ShowSidebarOutline()
    {
        _outlineMode = true;
        UpdateSidebarHeader();
        UpdateSidebarToggle();
        TreeToolbar.Visibility = Visibility.Collapsed;
        RootDropRow.Visibility = Visibility.Collapsed;
        FileTreeScroller.Visibility = Visibility.Collapsed;
        SetSidebarVisible(true);
        UpdateOutlineVisibility();
    }

    // 侧栏头部（原路径区+笔记本名）已删（2026-06 美化）。现仅同步树顶「根目录行」的名称——
    // 笔记本名标题栏已显示，路径不再在侧栏重复。活动笔记本名取不到时退回「工作区」。
    private void UpdateSidebarHeader()
    {
        var name = _settings.ActiveNotebook?.Name ?? _fileService.FolderName;
        RootRowName.Text = string.IsNullOrEmpty(name) ? "工作区" : name;
        // 悬浮根目录行＝显示笔记本完整路径（名称太长也能看全）。无打开文件夹时不挂 ToolTip。
        ToolTipService.SetToolTip(RootDropRow, _fileService.CurrentFolder);
    }

    // Title-bar chrome that's editor-only. Called from every view switch (Show*Internal,
    // FolderChanged, SetSidebarVisible) so the title bar reflects the current surface:
    //  • MainMenuBar  — File/Edit/Paragraph/... only make sense in the editor; hidden on the
    //    notebook/settings overlay pages (this also disables their keyboard accelerators,
    //    so e.g. Ctrl+N can't create a file while you're in 设置).
    //  • VaultSwitcher (centered notebook switcher) — shown only in the editor with a notebook
    //    open, so no dangling chevron is left near the caption buttons on the overlay pages.
    private void UpdateVaultNameVisibility()
    {
        var inEditor = EditorSurface.Visibility == Visibility.Visible;
        MainMenuBar.Visibility = inEditor ? Visibility.Visible : Visibility.Collapsed;
        var showSwitcher = inEditor && _settings.ActiveNotebook is not null;
        VaultSwitcher.Visibility = showSwitcher ? Visibility.Visible : Visibility.Collapsed;
    }

    // Reserve a column the width of the system caption buttons (min/max/close) so the
    // centered notebook name sits in the gap between the menu and those buttons, not
    // under them. RightInset is in physical pixels → divide by rasterization scale.
    private void UpdateTitleBarRightInset()
    {
        if (_appWindow?.TitleBar is not { } tb || AppTitleBar.XamlRoot is null) return;
        var scale = AppTitleBar.XamlRoot.RasterizationScale;
        if (scale <= 0) scale = 1;
        TitleBarRightInsetColumn.Width = new GridLength(tb.RightInset / scale);
    }

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e) => UpdateTitleBarRightInset();

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarRightInset();

    private void UpdateOutlineVisibility()
    {
        var hasItems = _outline.Count > 0;
        OutlineList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        OutlineEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOutlineUpdated(IReadOnlyList<OutlineItem> items)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 增量更新而非 Clear()+全量重填：后者会把整个 ListView 拆掉重建，导致每打一个字
            // （大纲随内容变化推送）整列闪一下。逐项 diff，只替换/增删真正变化的行；正文打字时
            // 标题不变 → 零改动 → 不闪。OutlineItem 不可变，按值（Level/Text/Index）比对。
            int n = items.Count;
            for (int i = 0; i < n; i++)
            {
                if (i < _outline.Count)
                {
                    var cur = _outline[i];
                    var next = items[i];
                    if (cur.Level != next.Level || cur.Index != next.Index || cur.Text != next.Text)
                        _outline[i] = next; // 只触发该行的 Replace，不动其余
                }
                else
                {
                    _outline.Add(items[i]);
                }
            }
            // 末尾多余的旧项删掉
            for (int i = _outline.Count - 1; i >= n; i--)
                _outline.RemoveAt(i);

            if (_outlineMode) UpdateOutlineVisibility();
        });
    }

    private void OutlineList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineItem oi)
            _ipcRouter.SendNotification("editor.scrollTo", new { index = oi.Index });
    }

    // 大纲右键菜单：目前就一个「关闭大纲」（收起整个侧栏，与目录树「关闭目录树」一致）。
    private void OutlineList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var anchor = sender as FrameworkElement ?? OutlineList;
        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "关闭大纲", GlyphOpenPane, () => SetSidebarVisible(false));
        flyout.ShowAt(anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to reveal in explorer: {Path}", path);
        }
    }

    // --- Pane search (filename quick-open within the current folder) ---
    // Scope is intentionally just filename-in-current-folder for now; full-text /
    // cross-project search is a future decision (see CLAUDE.md).

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        if (_fileService.CurrentFolder is null) { sender.ItemsSource = null; return; }

        var query = sender.Text?.Trim() ?? "";
        var files = FlattenFiles(_fileService.GetTree());
        sender.ItemsSource = query.Length == 0
            ? null
            : files.Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                   .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                   .Take(50)
                   .ToList();
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is FileNode node)
            sender.Text = node.Name;
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        FileNode? target = args.ChosenSuggestion as FileNode;
        if (target is null)
        {
            // Enter pressed without picking a suggestion: open the first match.
            var query = sender.Text?.Trim() ?? "";
            if (query.Length == 0) return;
            target = FlattenFiles(_fileService.GetTree())
                .FirstOrDefault(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        if (target is not null && _fileService.ResolveFullPath(target.RelPath) is { } full)
            _ = OpenNoteAsync(full);
    }

    private static IEnumerable<FileNode> FlattenFiles(IEnumerable<FileNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsDirectory)
            {
                foreach (var child in FlattenFiles(node.Children))
                    yield return child;
            }
            else
            {
                yield return node;
            }
        }
    }

    // The tree/大纲 is its own column (TreePanel). Show/hide just toggles its Visibility (the
    // Auto column collapses to 0). Toggled by the 工作区 nav item / View menu / Ctrl+Shift+E.
    private void SetSidebarVisible(bool visible)
    {
        TreePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggleMenu.IsChecked = visible;
    }

}
