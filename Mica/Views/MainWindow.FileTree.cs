using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Mica.Models;
using Serilog;

namespace Mica;

// Part of the MainWindow partial class — the file tree.
//
// Design (rewritten clean, 2026-06):
//  • The tree is an ObservableCollection bound to FileTree.ItemsSource. Create / delete
//    / rename mutate this (and nested Children) IN PLACE — never a full rebuild — so
//    folders keep their expand state and the tree never flickers.
//  • "Current file" highlight uses the NATIVE TreeView selection (rounded row bg + left
//    accent bar = the nav-rail look). SelectionMode is Single and each TreeViewItem's
//    IsSelected is TwoWay-bound to FileNode.IsActive — backing it with a model property
//    (not TreeView.SelectedItem) keeps the highlight when an ancestor folder collapses and
//    its container is recycled. Opening a file sets IsActive + expands ancestors.
//  • New / rename names are entered in a small POPUP FLYOUT at the click location
//    (ShowNameFlyout: a TextBox + 确认/取消). Inline editing was dropped — its focus lived
//    in the tree row and kept falling through to the WebView2 editor on Enter. A flyout owns
//    focus while open and returns it to the anchor on close, so focus never drifts. The same
//    component is reused for new note / new folder / rename / the tab-strip + button.
//  • Everything (new / delete / rename / copy path) is reached via right-click; there are
//    no sidebar-header buttons. 文件→新建 (Ctrl+N) creates a note at the notebook root.
public sealed partial class MainWindow
{
    // The hierarchical tree (dirs hold Children). Source of truth for create/delete/rename;
    // the flat _visibleRows below is its projection honoring each folder's IsExpanded.
    private ObservableCollection<FileNode> _treeRoots = [];
    // The flat list of CURRENTLY VISIBLE rows bound to FileTree.ItemsSource (ItemsRepeater).
    // Maintained by RebuildVisibleRows / ExpandRow / CollapseRow / FlatInsertNode / FlatRemoveNode.
    private readonly ObservableCollection<FileNode> _visibleRows = [];
    // The node whose file is open in the active tab (drives the row highlight).
    private FileNode? _activeNode;

    // --- Multi-selection (P2) ---
    // Distinct from _activeNode (the open file). _selection holds the highlighted set; the
    // anchor is the pivot for Shift-range. _multiSelectMode (toolbar toggle) makes a plain
    // click toggle selection (instead of select+open), for easy consecutive picking.
    private readonly HashSet<FileNode> _selection = [];
    private FileNode? _selectionAnchor;
    private bool _multiSelectMode;

    private static bool IsKeyDown(VirtualKey k) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    // 选择模型（2026-06 定稿，**单击与多选两套、互不混用**）：
    //  ★ 单击（无修饰键 + 多选模式关）→ SelectOnly：**只选它自己，绝不级联**。点文件夹只高亮该文件夹。
    //  ★ 多选（Ctrl/Shift 点击，或多选模式开）→ 级联 materialized：
    //    选文件夹＝把它**和所有子孙真的加进 _selection**（不是派生/置灰），FileNode.IsSelected ⟺ ∈ _selection
    //    （二元、统一显示、无深浅）。三条规则：① 选父文件夹→内部全选；② 已选文件再选其父文件夹→父下全选；
    //    ③ 取消某子项→连带取消其自身+子孙+所有**祖先**文件夹，**兄弟项不动**；④ 不向上自动选。
    //  ★ 单击→多选的过渡：进入多选语义前先 NormalizeCascade()——把已单击选中的文件夹补成级联（连其子孙）。
    //    触发点：Ctrl/Shift 点击（ToggleInSelection/SelectRange 内）、或开启多选模式（OnToggleMultiSelect）。
    //    于是「单击文件夹→开多选/Ctrl点下一个」会一致地把该文件夹整体选中（对应用户要求）。
    //  • 操作（复制/剪切/删除/拖拽）走 TopLevelSelection() 去重：集合里文件夹盖掉其子孙 → 只搬一次。
    //    所以「是否多选」「复制路径」「右键单/多选菜单」一律看 TopLevelSelection().Count，不是 _selection.Count。

    // descRel 是否在 ancestorRel 之下（严格子孙）。relPath 用 '/' 分隔。
    private static bool IsAncestorRel(string ancestorRel, string descRel) =>
        descRel.StartsWith(ancestorRel + "/", StringComparison.OrdinalIgnoreCase);

    // node 的所有子孙（文件夹+文件，深度优先）。
    private static IEnumerable<FileNode> Descendants(FileNode node)
    {
        foreach (var c in node.Children)
        {
            yield return c;
            if (c.IsDirectory) foreach (var d in Descendants(c)) yield return d;
        }
    }

    private void MarkSelected(FileNode n) { if (_selection.Add(n)) n.IsSelected = true; }
    private void MarkDeselected(FileNode n) { if (_selection.Remove(n)) n.IsSelected = false; }

    // 选中 node（级联向下）：文件夹连同其所有子孙一起选中；文件仅自身。不动祖先（不向上自动选）。
    private void SelectCascade(FileNode node)
    {
        MarkSelected(node);
        if (node.IsDirectory) foreach (var d in Descendants(node)) MarkSelected(d);
    }

    // 取消 node：取消它自身 + 其所有子孙（若文件夹）+ 其所有祖先文件夹（祖先不再「整体选中」）。
    // 兄弟项与兄弟的子孙不动 —— 规则③。
    private void DeselectCascade(FileNode node)
    {
        MarkDeselected(node);
        if (node.IsDirectory) foreach (var d in Descendants(node)) MarkDeselected(d);
        var dir = ParentDir(node.RelPath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (FindNodeByRel(dir) is { } f) MarkDeselected(f);
            dir = ParentDir(dir);
        }
    }

    private void ClearSelection()
    {
        foreach (var n in _selection) n.IsSelected = false;
        _selection.Clear();
    }

    // 单击：只选自己，**不级联**（即使是文件夹）。
    private void SelectOnly(FileNode node)
    {
        ClearSelection();
        MarkSelected(node);
    }

    // 进入多选语义前的过渡：把当前已选中的**文件夹**补成级联（连带其子孙）。让「单击选中的单个文件夹」
    // 在转入多选（Ctrl/Shift/多选模式）时一致地变成「整个文件夹选中」。对文件无影响、幂等。
    private void NormalizeCascade()
    {
        foreach (var folder in _selection.Where(n => n.IsDirectory).ToList())
            foreach (var d in Descendants(folder)) MarkSelected(d);
    }

    // 多选切换（Ctrl 点击 / 多选模式下单击）：先把已选文件夹补成级联，再级联切换本节点。
    private void ToggleInSelection(FileNode node)
    {
        NormalizeCascade();
        if (_selection.Contains(node)) DeselectCascade(node);
        else SelectCascade(node);
    }

    // Select the contiguous visible range from the anchor to `to` (inclusive). 每个范围内节点级联选中
    // （文件夹带上其全部子孙）。Falls back to a single select if there's no valid anchor.
    private void SelectRange(FileNode to)
    {
        int a = _selectionAnchor is null ? -1 : _visibleRows.IndexOf(_selectionAnchor);
        int b = _visibleRows.IndexOf(to);
        if (a < 0 || b < 0) { SelectOnly(to); _selectionAnchor = to; return; }
        if (a > b) (a, b) = (b, a);
        ClearSelection();
        for (int i = a; i <= b; i++) SelectCascade(_visibleRows[i]);
    }

    // Ctrl+A：选「全部可见」（各行级联选中，文件夹连其子孙）。
    private void SelectAllVisible()
    {
        ClearSelection();
        foreach (var n in _visibleRows) SelectCascade(n);
    }

    // Segoe Fluent Icons 字形（右键菜单/工具条用）。一律写成 \uXXXX 转义＝VS 里直接看得到码点、好改；
    // 改图标就换这里的四位十六进制（对照 Segoe Fluent Icons 字体表）。
    private const string GlyphOpen = "\uE8E5";       // OpenFile（打开笔记）
    private const string GlyphNewNote = "\uE7C3";    // Page（新建笔记）
    private const string GlyphNewFolder = "\uE8F4";  // NewFolder
    private const string GlyphRename = "\uE8AC";     // Rename
    private const string GlyphDelete = "\uE74D";     // Delete
    private const string GlyphNotebook = "\uE8F1";   // Library（设为笔记本）
    private const string GlyphCopy = "\uE8C8";       // Copy
    private const string GlyphCut = "\uE8C6";        // Cut
    private const string GlyphPaste = "\uE77F";      // Paste
    private const string GlyphReveal = "\uE838";     // FolderOpen（在资源管理器显示）
    private const string GlyphRefresh = "\uE72C";    // Refresh
    private const string GlyphOpenFolder = "\uED25"; // OpenFolderHorizontal
    private const string GlyphOpenPane = "\uE8A0";   // 关闭目录树/大纲（按你指定的 EA80；注意 Segoe 的 OpenPane 其实是 E8A0）

    private const string GlyphClose = "";      // ChromeClose（关闭已打开文件）

    private async void OnOpenVaultClick(object sender, RoutedEventArgs e) => await OpenFolderAsync();

    // "打开文件夹": pick an existing folder, register it as a notebook (or reuse the
    // one already registered for that path), then land on its detail page — same
    // destination as 新建, so opening and creating feel consistent.
    private async Task OpenFolderAsync()
    {
        try
        {
            while (true)
            {
                var path = await PickFolderPathAsync();
                if (path is null) return;

                // 该目录已是笔记本：弹提示（写出路径+笔记本名）。用户可「重新选择目录」(继续循环) 或「显示笔记本」。
                if (_settings.FindNotebookByPath(path) is { } existed)
                {
                    if (await ShowNotebookExistsAsync(existed, path)) continue; // 重新选择目录
                    if (NotebookHomeView.Visibility == Visibility.Visible) ShowHomeInternal();
                    NavigateTo(new ViewState(ViewKind.Detail, existed.Id));
                    return;
                }

                var nb = _settings.AddOrGetNotebook(path);
                if (NotebookHomeView.Visibility == Visibility.Visible) ShowHomeInternal();
                NavigateTo(new ViewState(ViewKind.Detail, nb.Id));
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add notebook");
        }
    }

    // 文件菜单 → 新建（Ctrl+N）：在当前笔记本根目录建新笔记。
    private void OnNewNoteClick(object sender, RoutedEventArgs e) => StartNewNoteAtRoot();

    // TabView 标签条右侧的 + 按钮：和 文件→新建 / 右键新建笔记 同一条流程，方便随手加 md。
    private void EditorTabs_AddTabButtonClick(TabView sender, object args) => StartNewNoteAtRoot();

    // Create a new note at the notebook root. Pops the naming flyout anchored on the tab-strip
    // + button (the click location) — no need to reveal the tree, the flyout owns its own focus.
    private void StartNewNoteAtRoot()
    {
        // Guard Ctrl+N from firing on the 笔记本/设置 overlay pages (the menu's accelerator
        // still fires while MainMenuBar is Collapsed) — don't create a file off-editor.
        if (!InEditorView) return;
        if (_fileService.CurrentFolder is null) return;
        PromptCreate("", isFolder: false, RootCreateAnchor());
    }

    // --- Tree toolbar (路径区下方一行：新建笔记/文件夹、全部展开/收起) ---
    // 解决「目录树占满时根目录没空白可右键、建不了文件」——这里直接给入口，锚在按钮上。

    private void OnToolbarNewNote(object sender, RoutedEventArgs e)
    {
        if (_fileService.CurrentFolder is null) return;
        PromptCreate(ToolbarCreateDir(), isFolder: false, (FrameworkElement)sender);
    }

    private void OnToolbarNewFolder(object sender, RoutedEventArgs e)
    {
        if (_fileService.CurrentFolder is null) return;
        PromptCreate(ToolbarCreateDir(), isFolder: true, (FrameworkElement)sender);
    }

    // 工具条新建的目标目录（对标 Windows 资源管理器在选中文件夹内新建）：
    // 恰好选中「单个文件夹」→ 建在其下；否则（未选 / 选中的是文件 / 多选）→ 根目录。
    // 用 TopLevelSelection：级联选文件夹会把其子孙也塞进 _selection（>1 项），只看顶层项才是「单个文件夹」。
    private string ToolbarCreateDir() =>
        TopLevelSelection() is [{ IsDirectory: true } folder] ? folder.RelPath : "";

    // 工具条编辑组（仅多选模式可见）：作用于当前多选集，复用既有的复制/剪切/批量删除逻辑。
    private void OnToolbarCopy(object sender, RoutedEventArgs e) => ClipboardCopy(cut: false);
    private void OnToolbarCut(object sender, RoutedEventArgs e) => ClipboardCopy(cut: true);
    private void OnToolbarDeleteSelection(object sender, RoutedEventArgs e) => _ = DeleteSelectionAsync();

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        SetAllExpanded(_treeRoots, true);
        RebuildVisibleRows();
        ScrollActiveIntoView();
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        SetAllExpanded(_treeRoots, false);
        RebuildVisibleRows();
    }

    private static void SetAllExpanded(IEnumerable<FileNode> nodes, bool expanded)
    {
        foreach (var n in nodes)
            if (n.IsDirectory) { n.IsExpanded = expanded; SetAllExpanded(n.Children, expanded); }
    }

    // A stable anchor for root-level create popups (File→新建 / + button): the TabView's add
    // button if it's realized, else the tab strip, else the title bar.
    private FrameworkElement RootCreateAnchor() =>
        FindDescendant<Button>(EditorTabs, "AddButton")
        ?? (EditorTabs.Visibility == Visibility.Visible ? EditorTabs : (FrameworkElement)AppTitleBar);

    // --- Open on click / double-click (per OpenFileTrigger setting) ---

    // Row hover: ItemsRepeater has no built-in selection chrome. Drive a per-node IsHovered
    // flag → a {ThemeResource} hover Border in the row template (theme-correct under the
    // element-level RequestedTheme). Don't set Grid.Background from app resources here — that
    // brush resolves against the APP theme, not the element override, so it's wrong in the
    // opposite theme. See FileNode.IsHovered.
    private void FileRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode node }) node.IsHovered = true;
    }

    private void FileRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode node }) node.IsHovered = false;
    }

    // ItemsRepeater + x:Bind does NOT set each row's DataContext (x:Bind is compiled, so
    // rendering works WITHOUT a DataContext) — but our Tapped/RightTapped/DoubleTapped
    // handlers read the row's DataContext to get the FileNode. So set it ourselves here,
    // or every click/right-click silently no-ops (folder won't toggle, file won't open, and
    // the node menu falls through to the blank-area menu). Also reset the recycled hover bg.
    private void FileTree_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Grid g) return;
        if (args.Index >= 0 && args.Index < _visibleRows.Count)
        {
            var node = _visibleRows[args.Index];
            node.IsHovered = false; // 复用行可能带着上一个节点的 hover 残留，重置（指针此刻并不在该行）
            g.DataContext = node;
        }
    }

    // Single tap. Modifier/selection semantics (explorer-like):
    //   • Shift           → select the range from the anchor to here.
    //   • Ctrl / 多选模式  → toggle this node in/out of the selection (no open/expand).
    //   • plain           → select only this node; a folder toggles expand, a file opens
    //                       (single-click mode). The active 竖条 is unaffected (open file only).
    // Focus the scroller so Ctrl+A / Delete route to the tree; mark Handled so a row tap
    // doesn't bubble to FileTree_Tapped (which clears the selection on blank clicks).
    // 手动双击判定的状态（见 FileRow_Tapped 注释）。
    private FileNode? _lastTapNode;
    private long _lastTapMs;
    private const long DoubleTapMs = 400; // 双击间隔阈值（系统典型 ~500ms，取 400 偏稳）

    // 按下即把焦点交给滚动容器（供 Ctrl+A/Delete 路由）。用 FocusState.Pointer——它不会触发
    // bring-into-view 滚动（Keyboard/Programmatic 才会），故不挪动行、不干扰随后的点击判定。
    private void FileRow_PointerPressed(object sender, PointerRoutedEventArgs e)
        => FileTreeScroller.Focus(FocusState.Pointer);

    // 单/双击处理。**双击走「框架 DoubleTapped + Tapped 内手动判定」双保险**，原因是框架的 DoubleTapped 行为
    // 随焦点是否变化而不一致（行 CanDrag=True 放大了这点）：
    //   · 行未选中：首击 Tapped 里切焦点会打断手势识别 → 框架**不发** DoubleTapped、改发两次 Tapped → 由手动判定兜住；
    //   · 行已选中：首击不切焦点、手势不被打断 → 框架正常发 DoubleTapped → 由 FileTreeItem_DoubleTapped 处理。
    // 两条路对同一次双击**互斥触发**（发了 DoubleTapped 就不会有第二次 Tapped，反之亦然），不会重复打开。
    private void FileRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileNode node }) return;
        e.Handled = true;

        // 修饰键 = 多选语义，与双击无关；重置双击状态。
        if (IsKeyDown(VirtualKey.Shift)) { _lastTapNode = null; SelectRange(node); return; }
        if (IsKeyDown(VirtualKey.Control) || _multiSelectMode)
        {
            _lastTapNode = null;
            ToggleInSelection(node);
            _selectionAnchor = node;
            return;
        }

        var now = Environment.TickCount64;
        var isDouble = ReferenceEquals(_lastTapNode, node) && now - _lastTapMs <= DoubleTapMs;
        _lastTapNode = node;
        _lastTapMs = now;

        if (isDouble)
        {
            _lastTapNode = null; // 防止三击再次判成双击
            ActivateNode(node);
            return;
        }

        // 单击只选中（对标 Windows/PyCharm）：文件夹不展开（展开走小三角或双击名），文件按设置可单击打开。
        SelectOnly(node);
        _selectionAnchor = node;
        if (!node.IsDirectory && _settings.OpenFileTrigger == 0) OpenNode(node);
    }

    // 框架双击（行已选中、手势未被打断时走这条）。修饰键场景交给 Tapped 的多选分支、这里不抢。
    private void FileTreeItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileNode node }) return;
        e.Handled = true;
        if (IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.Control) || _multiSelectMode) return;
        _lastTapNode = null; // 与手动判定互斥：本次已由框架处理，清掉手动状态防紧随的 Tapped 再触发
        ActivateNode(node);
    }

    // 双击的动作：文件夹展开/收起；文件打开（单击打开模式下首击已开过=幂等无害）。
    private void ActivateNode(FileNode node)
    {
        if (node.IsDirectory) ToggleFolder(node);
        else OpenNode(node);
    }

    // 小三角单击：只展开/收起，并 Handled 掉，不让整行的 Tapped 再去改选中。
    private void Chevron_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileNode { IsDirectory: true } node })
        {
            ToggleFolder(node);
            e.Handled = true;
        }
    }

    // Click on empty space below the rows: clear the selection (row taps mark Handled, so this
    // only fires for true blank clicks).
    private void FileTree_Tapped(object sender, TappedRoutedEventArgs e) => ClearSelection();

    // Tree-scoped keyboard: Ctrl+A selects all visible rows, Delete deletes the selection.
    // Routed here only when the scroller has focus (a tree row was clicked), so it doesn't
    // clash with the editor's own Ctrl+A / Delete (WebView2 has focus there).
    private void FileTree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsKeyDown(VirtualKey.Control);
        if (e.Key == VirtualKey.A && ctrl)
        {
            SelectAllVisible();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete && _selection.Count > 0)
        {
            e.Handled = true;
            _ = DeleteSelectionAsync();
        }
        else if (ctrl && e.Key == VirtualKey.C && _selection.Count > 0)
        {
            ClipboardCopy(cut: false);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.X && _selection.Count > 0)
        {
            ClipboardCopy(cut: true);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.V && CanPaste())
        {
            e.Handled = true;
            _ = PasteIntoAsync(PasteTargetDir());
        }
        // F2：重命名（恰好选中单项时）。键盘触发没有行元素当锚点，锚在滚动容器上。
        else if (e.Key == VirtualKey.F2 && TopLevelSelection() is [{ } one])
        {
            e.Handled = true;
            PromptRename(one, FileTreeScroller);
        }
        // Enter：激活选中项（文件→打开，文件夹→展开/收起），对标资源管理器。
        else if (e.Key == VirtualKey.Enter && TopLevelSelection() is [{ } sel])
        {
            e.Handled = true;
            ActivateNode(sel);
        }
    }

    // Where Ctrl+V pastes: into the selected folder if exactly one folder is selected; else into
    // the parent dir of the (first) selected node; else the root.
    // 用 TopLevelSelection（级联选文件夹会把子孙也塞进 _selection，单看 _selection 会误判成多选）。
    private string PasteTargetDir()
    {
        var top = TopLevelSelection();
        if (top is [{ IsDirectory: true } folder]) return folder.RelPath;
        return top.Count > 0 ? ParentDir(top[0].RelPath) : "";
    }

    private void OnToggleMultiSelect(object sender, RoutedEventArgs e)
    {
        _multiSelectMode = MultiSelectToggle.IsChecked == true;
        // 左段切换：普通态显示「新建/定位」，多选态换成「复制/剪切/删除」；视图组常驻不动。
        CreateGroup.Visibility = _multiSelectMode ? Visibility.Collapsed : Visibility.Visible;
        EditGroup.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        // 各行显示/隐藏勾选框（让多选模式与普通 Ctrl+点击有可见区别）。
        SetCheckBoxesVisible(_multiSelectMode);
        // 开启多选：把已单击选中的文件夹补成级联（选中其全部内容）——单击→多选的过渡之一。
        if (_multiSelectMode) NormalizeCascade();
    }

    // 对全树节点统一开关勾选框（含未展开的子节点——它们展开后也得是对的）。
    private void SetCheckBoxesVisible(bool visible)
    {
        void Walk(IEnumerable<FileNode> nodes)
        {
            foreach (var n in nodes)
            {
                n.ShowCheckBox = visible;
                if (n.IsDirectory) Walk(n.Children);
            }
        }
        Walk(_treeRoots);
    }

    // Open a tree node's file as a tab, keyed by its absolute path.
    private void OpenNode(FileNode node)
    {
        if (_fileService.ResolveFullPath(node.RelPath) is { } full) _ = OpenNoteAsync(full);
    }

    // --- Tree (re)load + incremental mutation ---

    // Full (re)load — only on a real context switch (active folder changed) or manual refresh.
    private void LoadTree()
    {
        _activeNode = null;
        _selection.Clear();         // FileNode objects are recreated below; drop stale refs
        _selectionAnchor = null;
        if (FileTree.ItemsSource is null) FileTree.ItemsSource = _visibleRows; // bind once (stable instance)
        if (_fileService.CurrentFolder is null)
        {
            _treeRoots = [];
            _visibleRows.Clear();
            return;
        }
        _treeRoots = _fileService.GetTree();
        RebuildVisibleRows();
    }

    // Manual refresh (右键「刷新」): rebuild from disk but KEEP the user's expanded folders as-is
    // (#3 — refresh shouldn't reshuffle the tree). The open-file 竖条 is restored without forcing
    // any expand/scroll; use the 定位 button to jump to the open file.
    private void RefreshTree()
    {
        var expanded = CaptureExpandedRels();
        var token = (EditorTabs.SelectedItem as Microsoft.UI.Xaml.Controls.TabViewItem)?.Tag as string;
        LoadTree();
        ReapplyExpanded(expanded);
        if (token is not null) SetActiveFile(token);
    }

    // Snapshot the rel paths of currently-expanded folders (to restore across a full reload).
    private HashSet<string> CaptureExpandedRels()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Walk(IEnumerable<FileNode> nodes)
        {
            foreach (var n in nodes)
                if (n.IsDirectory) { if (n.IsExpanded) set.Add(n.RelPath); Walk(n.Children); }
        }
        Walk(_treeRoots);
        return set;
    }

    private void ReapplyExpanded(HashSet<string> expanded)
    {
        void Walk(IEnumerable<FileNode> nodes)
        {
            foreach (var n in nodes)
                if (n.IsDirectory) { if (expanded.Contains(n.RelPath)) n.IsExpanded = true; Walk(n.Children); }
        }
        Walk(_treeRoots);
        RebuildVisibleRows();
    }

    // --- Flat-row projection engine (the heart of the rewrite) ---
    // _visibleRows = _treeRoots flattened in display order, descending only into expanded
    // folders. Every row carries its Depth (for indent). Mutations splice _visibleRows in
    // place (Insert/RemoveAt) so ItemsRepeater updates incrementally — no full rebuild, no
    // flicker, no scroll jump (except the deliberate full rebuilds: LoadTree / 全部展开收起).

    // Append `nodes` (and, recursively, the visible subtree of any expanded folder) to `outp`.
    private static void FlattenInto(IEnumerable<FileNode> nodes, int depth, List<FileNode> outp)
    {
        foreach (var n in nodes)
        {
            n.Depth = depth;
            outp.Add(n);
            if (n.IsDirectory && n.IsExpanded) FlattenInto(n.Children, depth + 1, outp);
        }
    }

    private void RebuildVisibleRows()
    {
        var flat = new List<FileNode>();
        FlattenInto(_treeRoots, 0, flat);
        _visibleRows.Clear();
        foreach (var n in flat) _visibleRows.Add(n);
    }

    private void ToggleFolder(FileNode node)
    {
        if (node.IsExpanded) CollapseRow(node); else ExpandRow(node);
    }

    // Expand a folder: mark it expanded and splice its visible subtree right after its row.
    private void ExpandRow(FileNode node)
    {
        if (!node.IsDirectory) return;
        int idx = _visibleRows.IndexOf(node);
        node.IsExpanded = true;
        if (idx < 0) return; // folder itself not visible (an ancestor is collapsed) — nothing to splice
        var sub = new List<FileNode>();
        FlattenInto(node.Children, node.Depth + 1, sub);
        for (int i = 0; i < sub.Count; i++) _visibleRows.Insert(idx + 1 + i, sub[i]);
    }

    // Collapse a folder: mark it collapsed and remove the contiguous block of deeper rows.
    private void CollapseRow(FileNode node)
    {
        if (!node.IsDirectory) return;
        int idx = _visibleRows.IndexOf(node);
        node.IsExpanded = false;
        if (idx < 0) return;
        int end = idx + 1;
        while (end < _visibleRows.Count && _visibleRows[end].Depth > node.Depth) end++;
        for (int i = end - 1; i > idx; i--) _visibleRows.RemoveAt(i);
    }

    // Splice a newly-created node into _visibleRows (no-op if its parent is collapsed/hidden —
    // it'll appear when the parent expands). `dir` is the parent dir ("" = root).
    private void FlatInsertNode(string dir, FileNode node)
    {
        ObservableCollection<FileNode> siblings;
        int depth, parentFlatIndex;
        if (string.IsNullOrEmpty(dir))
        {
            siblings = _treeRoots; depth = 0; parentFlatIndex = -1;
        }
        else
        {
            if (FindNodeByRel(dir) is not { IsDirectory: true } parent) return;
            if (!parent.IsExpanded) return;
            parentFlatIndex = _visibleRows.IndexOf(parent);
            if (parentFlatIndex < 0) return;
            siblings = parent.Children; depth = parent.Depth + 1;
        }
        node.Depth = depth;
        int at = FlatInsertIndex(siblings, node, parentFlatIndex);
        _visibleRows.Insert(at, node);
        // Renamed expanded folders re-enter with their subtree; freshly-created ones are leaves/collapsed.
        if (node.IsDirectory && node.IsExpanded)
        {
            var sub = new List<FileNode>();
            FlattenInto(node.Children, node.Depth + 1, sub);
            for (int i = 0; i < sub.Count; i++) _visibleRows.Insert(at + 1 + i, sub[i]);
        }
    }

    // The flat index at which `node` (already sorted into `siblings`) should be inserted:
    // right after its previous sibling's whole subtree, or right after the parent row.
    private int FlatInsertIndex(ObservableCollection<FileNode> siblings, FileNode node, int parentFlatIndex)
    {
        int p = siblings.IndexOf(node);
        if (p <= 0) return parentFlatIndex + 1;
        var prev = siblings[p - 1];
        int prevIdx = _visibleRows.IndexOf(prev);
        if (prevIdx < 0) return parentFlatIndex + 1;
        int i = prevIdx + 1;
        while (i < _visibleRows.Count && _visibleRows[i].Depth > prev.Depth) i++;
        return i;
    }

    // Remove `node` (and its visible subtree) from _visibleRows.
    private void FlatRemoveNode(FileNode node)
    {
        int idx = _visibleRows.IndexOf(node);
        if (idx < 0) return;
        int end = idx + 1;
        while (end < _visibleRows.Count && _visibleRows[end].Depth > node.Depth) end++;
        for (int i = end - 1; i >= idx; i--) _visibleRows.RemoveAt(i);
    }

    // Scroll the active file's row into view (best effort). Rows are fixed-height (28 + 1
    // spacing), so we compute the offset directly — robust even when the row is virtualized
    // off-screen (TryGetElement would return null there).
    private void ScrollActiveIntoView()
    {
        if (_activeNode is null) return;
        int idx = _visibleRows.IndexOf(_activeNode);
        if (idx < 0) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (FileTreeScroller is null) return;
                const double rowH = 31; // Height 30 + StackLayout Spacing 1
                double target = idx * rowH;
                double top = FileTreeScroller.VerticalOffset;
                double bottom = top + FileTreeScroller.ViewportHeight;
                if (target < top || target + rowH > bottom)
                    FileTreeScroller.ChangeView(null, Math.Max(0, target - rowH), null, false);
            }
            catch (Exception ex) { Log.Debug(ex, "ScrollActiveIntoView failed"); }
        });
    }

    // Sort order: directories before files, then by name (mirrors ScanDirectory).
    private static int CompareNodes(FileNode a, FileNode b)
    {
        if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void InsertSorted(ObservableCollection<FileNode> coll, FileNode node)
    {
        var i = 0;
        while (i < coll.Count && CompareNodes(coll[i], node) <= 0) i++;
        coll.Insert(i, node);
    }

    private static string ParentDir(string relPath) =>
        Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";

    // The collection holding the children of dir ("" = root).
    private ObservableCollection<FileNode> CollectionForDir(string dir) =>
        string.IsNullOrEmpty(dir)
            ? _treeRoots
            : (FindNodeByRel(dir) is { IsDirectory: true } folder ? folder.Children : _treeRoots);

    // Rewrite a node's RelPath (and, for a folder, all descendants') after a rename.
    private static void Reparent(FileNode node, string newRel)
    {
        node.RelPath = newRel;
        if (!node.IsDirectory) return;
        foreach (var child in node.Children)
            Reparent(child, $"{newRel}/{Path.GetFileName(child.RelPath)}");
    }

    private FileNode? FindNodeByRel(string relPath) => FindNodeByRel(_treeRoots, relPath);

    private static FileNode? FindNodeByRel(IEnumerable<FileNode> nodes, string relPath)
    {
        foreach (var n in nodes)
        {
            if (n.RelPath == relPath) return n;
            var c = FindNodeByRel(n.Children, relPath);
            if (c is not null) return c;
        }
        return null;
    }

    private static FileNode? FindFileNode(IEnumerable<FileNode> nodes, string relPath)
    {
        foreach (var n in nodes)
        {
            if (!n.IsDirectory && n.RelPath == relPath) return n;
            var child = FindFileNode(n.Children, relPath);
            if (child is not null) return child;
        }
        return null;
    }

    // --- Active-file highlight (decoupled from TreeView selection) ---

    // Mark the open file's row active and expand its ancestors so it's visible. Called
    // when a tab gains focus / a file opens. `fullPath` is the tab's absolute token; the
    // highlight only lights up when that file lives inside the CURRENT notebook (otherwise
    // there's no matching tree node and the highlight clears — the open file belongs to a
    // different notebook).
    // Update ONLY the active-file 竖条 — never auto-expand ancestors or scroll. The tree is the
    // user's to arrange: opening/closing/deleting/cutting tabs must not reshuffle it (#3). If the
    // open file sits in a collapsed branch, its row simply isn't visible (no 竖条) until the user
    // expands it or hits the 定位 button (LocateActiveFile).
    private void SetActiveFile(string fullPath)
    {
        var rel = ToCurrentRel(fullPath);
        var match = rel is null ? null : FindFileNode(_treeRoots, rel);
        if (ReferenceEquals(_activeNode, match)) return;
        if (_activeNode is not null) _activeNode.IsActive = false;
        _activeNode = match;
        if (match is not null) match.IsActive = true;
    }

    // Toolbar 定位 button: reveal the open file — expand its ancestors and scroll it into view.
    // This is the ONE place that deliberately moves the tree to follow the open file.
    private void LocateActiveFile()
    {
        if (_activeNode is null) return;
        ExpandAncestors(_activeNode.RelPath);
        ScrollActiveIntoView();
    }

    private void OnLocateActive(object sender, RoutedEventArgs e) => LocateActiveFile();

    // Map an absolute file path back to a path relative to the current notebook root, or
    // null if it isn't under that root (file belongs to another notebook / no folder open).
    private string? ToCurrentRel(string fullPath)
    {
        var root = _fileService.CurrentFolder;
        if (root is null) return null;
        if (!Path.IsPathRooted(fullPath)) return fullPath; // defensive: already relative
        var rel = Path.GetRelativePath(root, fullPath);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        return rel.Replace('\\', '/');
    }

    private void ClearActiveFile()
    {
        if (_activeNode is null) return;
        _activeNode.IsActive = false;
        _activeNode = null;
    }

    // Expand every collapsed ancestor folder of relPath so its row becomes visible. Must go
    // root→leaf: an inner folder can only be spliced into _visibleRows once its outer parent
    // is already expanded (and thus present). Already-expanded ancestors are skipped (no-op,
    // no flicker) — the common "switch between open tabs" case touches nothing.
    private void ExpandAncestors(string relPath)
    {
        var chain = new List<FileNode>();
        var dir = ParentDir(relPath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (FindNodeByRel(dir) is { IsDirectory: true } folder) chain.Add(folder);
            dir = ParentDir(dir);
        }
        for (int i = chain.Count - 1; i >= 0; i--)
            if (!chain[i].IsExpanded) ExpandRow(chain[i]);
    }

    // --- Naming popup (new note / new folder / rename) ---
    //
    // One reusable flyout (TextBox + 确认/取消) shown at the click location. Inline tree
    // editing was dropped: its focus lived in a row TextBox and kept falling through to the
    // WebView2 editor on Enter. A flyout owns focus while open and hands it back to the anchor
    // on close, so focus never drifts. Nothing touches disk until 确认 (or Enter).

    // New note / folder under dir ("" = root). isFolder picks the verb + default name.
    private void PromptCreate(string dir, bool isFolder, FrameworkElement anchor, Point? position = null)
    {
        if (_fileService.CurrentFolder is null) return;
        // 标题带上目标位置，告诉用户建到哪个文件夹下（根目录显示笔记本/「根目录」名）。
        var where = string.IsNullOrEmpty(dir)
            ? (_settings.ActiveNotebook?.Name ?? "根目录")
            : Path.GetFileName(dir);
        ShowNameFlyout(anchor, position,
            (isFolder ? "新建文件夹" : "新建笔记") + $"（{where}）",
            isFolder ? "新文件夹" : "新笔记",
            isFile: !isFolder,
            name => _ = CreateEntryAsync(dir, isFolder, name));
    }

    // Rename a node: prefill the box with its current name (stem preselected).
    private void PromptRename(FileNode node, FrameworkElement anchor)
    {
        ShowNameFlyout(anchor, null, "重命名", node.Name, isFile: !node.IsDirectory,
            name => RenameNode(node, name));
    }

    // The shared component. `position` (when given) places it at the click point relative to
    // `anchor`; otherwise it attaches to the anchor. onConfirm gets the trimmed, non-empty name.
    private void ShowNameFlyout(FrameworkElement anchor, Point? position, string title,
        string initialName, bool isFile, Action<string> onConfirm)
    {
        var box = new TextBox { Text = initialName, Width = 240, MinHeight = 32 };
        // Preselect the stem (name minus .md for files) for quick overwrite.
        var stemLen = isFile ? Path.GetFileNameWithoutExtension(initialName).Length : initialName.Length;
        box.SelectionStart = 0;
        box.SelectionLength = stemLen > 0 ? stemLen : initialName.Length;

        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var okBtn = new Button
        {
            Content = "确认",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var cancelBtn = new Button { Content = "取消" };
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);

        var panel = new StackPanel { MinWidth = 240 };
        panel.Children.Add(titleBlock);
        panel.Children.Add(box);
        panel.Children.Add(btnRow);

        var flyout = new Flyout { Content = panel };

        var done = false;
        void Confirm()
        {
            if (done) return;
            done = true;
            var name = box.Text.Trim();
            flyout.Hide();
            if (!string.IsNullOrEmpty(name) && !HasInvalidChars(name)) onConfirm(name);
        }

        okBtn.Click += (_, _) => Confirm();
        cancelBtn.Click += (_, _) => flyout.Hide();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; Confirm(); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; flyout.Hide(); }
        };
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        // Open BELOW the click — Flyout defaults to Placement=Top, which put the box above
        // the cursor (felt wrong). Bottom puts the popup's top-edge centered on the point.
        flyout.Placement = FlyoutPlacementMode.Bottom;
        if (position is { } p)
            flyout.ShowAt(anchor, new FlyoutShowOptions { Position = p, Placement = FlyoutPlacementMode.Bottom });
        else
            flyout.ShowAt(anchor);
    }

    // Create the file/folder on disk and insert its node into the tree (sorted). Notes open
    // right away (absolute token, so delete can later find/close the tab). No-op on bad name.
    private async Task CreateEntryAsync(string dir, bool isFolder, string name)
    {
        if (!isFolder && !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name += ".md";
        var rel = string.IsNullOrEmpty(dir) ? name : $"{dir}/{name}";
        try
        {
            var actual = isFolder
                ? _fileService.CreateFolder(rel)
                : await _fileService.CreateFileAsync(rel);

            // Ensure the parent is expanded (via ExpandRow so its existing children are spliced
            // into _visibleRows) BEFORE inserting the new node, so the new row lands correctly.
            if (!string.IsNullOrEmpty(dir) && FindNodeByRel(dir) is { IsDirectory: true } parent && !parent.IsExpanded)
                ExpandRow(parent);
            var node = new FileNode
            {
                Name = Path.GetFileName(actual),
                RelPath = actual,
                IsDirectory = isFolder,
                ShowCheckBox = _multiSelectMode, // 多选模式下新建的节点也带勾选框
            };
            InsertSorted(CollectionForDir(dir), node);
            FlatInsertNode(dir, node);
            if (IsParentSelected(dir)) SelectCascade(node); // 父文件夹已选中 → 新建项也并入选中（维持级联一致）

            if (!isFolder && _settings.OpenNoteAfterCreate
                && _fileService.ResolveFullPath(actual) is { } full)
                await OpenNoteAsync(full);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Create failed: {Rel}", rel);
        }
    }

    // Rename on disk + retarget any open tab, then re-sort the node in place (identity kept,
    // so IsActive / IsExpanded survive). No-op on bad / unchanged name. Async because the rename
    // now carries .mica assets + rewrites <img src> (see FileService.RenameEntryAsync).
    private async void RenameNode(FileNode node, string name)
    {
        if (!node.IsDirectory && !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name += ".md";
        var parent = ParentDir(node.RelPath);
        var newRel = string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
        var oldRel = node.RelPath;
        if (string.Equals(newRel, oldRel, StringComparison.Ordinal)) return;

        try { await _fileService.RenameEntryAsync(oldRel, newRel); }
        catch (Exception ex) { Log.Warning(ex, "Rename failed: {Old} -> {New}", oldRel, newRel); return; }

        if (!node.IsDirectory) RetargetOpenTab(oldRel, newRel);
        var coll = CollectionForDir(parent);
        FlatRemoveNode(node);            // pull the old row (and subtree) out of the flat list
        coll.Remove(node);
        Reparent(node, newRel);
        node.Name = Path.GetFileName(newRel);
        InsertSorted(coll, node);
        FlatInsertNode(parent, node);    // re-insert at the new sorted position (subtree preserved)
    }

    // Generic visual-tree descendant search (used to anchor popups on a template part, e.g.
    // the TabView's "AddButton").
    private static T? FindDescendant<T>(DependencyObject root, string? name = null) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && (name is null || t.Name == name)) return t;
            var found = FindDescendant<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool HasInvalidChars(string name) =>
        name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

    private async Task DeleteNodeAsync(FileNode node)
    {
        var permanent = _settings.FileDeleteMode == 1;
        var action = permanent ? "永久删除" : "移入回收站";

        var dialog = new ContentDialog
        {
            Title = action,
            Content = permanent
                ? $"将永久删除「{node.Name}」，此操作不可恢复。"
                : $"将「{node.Name}」移入回收站。",
            PrimaryButtonText = action,
            CloseButtonText = "取消",
            DefaultButton = permanent ? ContentDialogButton.None : ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (permanent) ApplyDangerPrimary(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await DeleteNodeCoreAsync(node, permanent);
    }

    // Delete one node (no dialog): close its tabs, recycle its .mica assets, delete on disk,
    // then prune it from the flat list + tree. Shared by single delete and batch delete.
    private async Task DeleteNodeCoreAsync(FileNode node, bool permanent)
    {
        // 先关掉相关标签：否则被删笔记的标签还开着、WebView 正显示其图片，回收 .mica 资产目录可能因占用失败。
        CloseTabsUnder(node.RelPath, node.IsDirectory);

        try
        {
            // 删 .mica 资产（删笔记/文件夹连带删其图片，无孤儿），best-effort：失败只记日志，不阻断主删除。
            try
            {
                if (_fileService.ResolveFullPath(node.RelPath) is { } abs
                    && _fileService.DeleteNoteAssets(abs, _fileService.CurrentFolder, node.IsDirectory, permanent) is { } gone)
                    Log.Information("Deleted note assets: {Dir} (permanent={Permanent})", gone, permanent);
            }
            catch (Exception ex) { Log.Warning(ex, "Delete note assets failed: {Rel}", node.RelPath); }
            if (node.IsDirectory) await _fileService.DeleteFolderAsync(node.RelPath, permanent);
            else await _fileService.DeleteFileAsync(node.RelPath, permanent);
        }
        catch (Exception ex) { Log.Warning(ex, "Delete failed: {Rel}", node.RelPath); return; }

        if (ReferenceEquals(_activeNode, node)) _activeNode = null;
        _selection.Remove(node);
        FlatRemoveNode(node);
        CollectionForDir(ParentDir(node.RelPath)).Remove(node);
    }

    // Batch delete the current multi-selection (Delete key / multi-select context menu). Pares
    // the set down to top-level nodes first so a selected folder + a selected child inside it
    // isn't deleted twice.
    private async Task DeleteSelectionAsync()
    {
        var targets = TopLevelSelection();
        if (targets.Count == 0) return;
        if (targets.Count == 1) { await DeleteNodeAsync(targets[0]); return; }

        var permanent = _settings.FileDeleteMode == 1;
        var action = permanent ? "永久删除" : "移入回收站";
        var dialog = new ContentDialog
        {
            Title = action,
            Content = permanent
                ? $"将永久删除选中的 {targets.Count} 项，此操作不可恢复。"
                : $"将选中的 {targets.Count} 项移入回收站。",
            PrimaryButtonText = action,
            CloseButtonText = "取消",
            DefaultButton = permanent ? ContentDialogButton.None : ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (permanent) ApplyDangerPrimary(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var node in targets) await DeleteNodeCoreAsync(node, permanent);
        _selectionAnchor = null;
    }

    // Reduce the selection to nodes not contained in any other selected folder (so deleting a
    // folder + something inside it doesn't double-act on the child).
    private List<FileNode> TopLevelSelection()
    {
        var sel = _selection.ToList();
        return sel.Where(n => !sel.Any(o =>
            !ReferenceEquals(o, n) && o.IsDirectory &&
            n.RelPath.StartsWith(o.RelPath + "/", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // Context menu for a multi-selection: batch copy/cut/delete + copy paths.
    private void ShowMultiSelectionMenu(FrameworkElement anchor, Point position)
    {
        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "复制", GlyphCopy, () => ClipboardCopy(cut: false));
        AddMenuItem(flyout, "剪切", GlyphCut, () => ClipboardCopy(cut: true));
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, $"删除 {TopLevelSelection().Count} 项", GlyphDelete, () => _ = DeleteSelectionAsync());
        AddMenuItem(flyout, "复制路径", GlyphCopy, CopySelectionPaths);
        flyout.ShowAt(anchor, position);
    }

    private void CopySelectionPaths()
    {
        // 用 TopLevelSelection：materialized 级联会把子孙塞进 _selection，复制路径只要顶层项即可。
        var paths = TopLevelSelection()
            .Select(n => _fileService.ResolveFullPath(n.RelPath))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        if (paths.Count > 0) CopyTextToClipboard(string.Join(Environment.NewLine, paths));
    }

    // --- File copy / cut / paste + duplicate (P2b) ---
    // 内部剪贴板存**绝对源路径 + 源笔记本根**（不是相对路径）——这样切到别的笔记本再粘贴也能找回源、跨笔记本可用
    // （相对路径会被目标笔记本错误解析）。剪切一次性（粘完清空）；复制可重复粘到多处。落盘（含 .mica 资产搬运 +
    // <img src> 改写）统一走 FileService.TransferEntryAsync(srcAbs, srcRoot, ...)（跨根引擎）。
    private List<string> _clipboardAbs = [];
    private string? _clipboardSrcRoot;
    private bool _clipboardCut;

    private void ClipboardCopy(bool cut)
    {
        var targets = TopLevelSelection();
        if (targets.Count == 0 || _fileService.CurrentFolder is null) return;
        _clipboardAbs = targets
            .Select(n => _fileService.ResolveFullPath(n.RelPath))
            .Where(p => p is not null).Select(p => p!)
            .ToList();
        _clipboardSrcRoot = _fileService.CurrentFolder;
        _clipboardCut = cut;
        // 同步把文件列表镜像到系统剪贴板，让「Mica 复制/剪切 → 资源管理器粘贴」也通（双向）。
        _ = MirrorToSystemClipboardAsync(_clipboardAbs.ToList(), cut);
    }

    // 把内部剪贴板的文件/文件夹镜像到系统剪贴板（StorageItems + 复制/移动意图）。
    //  • RequestedOperation=Move 让资源管理器把「剪切」识别为移动（映射到 Preferred DropEffect）。
    //  • 路径要 GetFullPath 规范化，否则 StorageFile/Folder.GetFromPathAsync 对带 ../ 或正斜杠的路径会抛。
    //  • 置 _selfClipboardWrite，避免我们这次写触发 ContentChanged 把刚设好的内部剪贴板又清掉（见 OnSystemClipboardChanged）。
    private async Task MirrorToSystemClipboardAsync(List<string> paths, bool cut)
    {
        try
        {
            var items = new List<IStorageItem>();
            foreach (var p in paths)
            {
                var full = Path.GetFullPath(p);
                if (Directory.Exists(full)) items.Add(await StorageFolder.GetFolderFromPathAsync(full));
                else if (File.Exists(full)) items.Add(await StorageFile.GetFileFromPathAsync(full));
            }
            if (items.Count == 0) return;
            var pkg = new DataPackage
            {
                RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy,
            };
            pkg.SetStorageItems(items);
            _selfClipboardWrite = true;
            Clipboard.SetContent(pkg);
        }
        catch (Exception ex) { Log.Warning(ex, "Mirror to system clipboard failed"); }
    }

    // Duplicate a note in place → "xxx (1).md" (deduped), copying its assets, then open it.
    private async Task DuplicateNodeAsync(FileNode node)
    {
        if (node.IsDirectory || _fileService.CurrentFolder is null) return;
        try
        {
            var dest = await _fileService.TransferEntryAsync(node.RelPath, ParentDir(node.RelPath), isCopy: true);
            InsertNewEntry(ParentDir(node.RelPath), dest);
            if (_fileService.ResolveFullPath(dest) is { } full) await OpenNoteAsync(full);
        }
        catch (Exception ex) { Log.Error(ex, "Duplicate failed: {Rel}", node.RelPath); }
    }

    // 粘贴到 targetDir（""=根）。复制保留源；剪切移动（重定向标签、同笔记本时摘源行）后清空剪贴板。
    // 跨笔记本：源在剪贴板记录的 _clipboardSrcRoot 下，引擎按源根定位 .mica 资产、按目标位置重算 <img src>。
    private async Task PasteIntoAsync(string targetDir)
    {
        if (_fileService.CurrentFolder is null) return;
        // 内部剪贴板（Mica 自己复制/剪切，含「剪切=移动」语义）优先；为空则回退**系统剪贴板**——
        // 即从资源管理器复制来的文件/文件夹（一律复制进来，复用外部拖入那条搬运链路）。
        if (_clipboardAbs.Count > 0)
        {
            if (_clipboardSrcRoot is null) return;
            var cut = _clipboardCut;
            await TransferAbsIntoAsync(_clipboardAbs.ToList(), _clipboardSrcRoot, targetDir, isCopy: !cut);
            // 不动用户的选中（粘贴是用户决定的动作，但选中归用户管）。剪切是一次性，粘完清空。
            if (cut) { _clipboardAbs = []; _clipboardSrcRoot = null; _clipboardCut = false; }
            return;
        }
        await PasteFromSystemClipboardAsync(targetDir);
    }

    // 从系统剪贴板粘贴外部文件/文件夹（资源管理器复制的）。读剪贴板/读盘可能抛（被占用/虚拟项），吞掉记日志。
    private async Task PasteFromSystemClipboardAsync(string targetDir)
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.StorageItems)) return;
            var items = await view.GetStorageItemsAsync();
            await ImportExternalItemsAsync(items, targetDir);
        }
        catch (Exception ex) { Log.Warning(ex, "Paste from system clipboard failed"); }
    }

    // 系统剪贴板里是否有文件/文件夹（决定右键「粘贴」/Ctrl+V 是否可用）。GetContent 可能抛，吞掉当无。
    private static bool SystemClipboardHasFiles()
    {
        try { return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems); }
        catch { return false; }
    }

    // 当前是否有可粘贴的东西（内部剪贴板 或 系统剪贴板里的外部文件）。
    private bool CanPaste() => _clipboardAbs.Count > 0 || SystemClipboardHasFiles();

    // 拖拽（同笔记本内部树）：把当前笔记本的相对 rels 转成绝对路径，复用统一引擎（srcRoot=当前笔记本）。
    private async Task TransferRelsIntoAsync(List<string> rels, string targetDir, bool isCopy)
    {
        if (rels.Count == 0 || _fileService.CurrentFolder is null) return;
        var abs = rels.Select(r => _fileService.ResolveFullPath(r)).Where(p => p is not null).Select(p => p!).ToList();
        await TransferAbsIntoAsync(abs, _fileService.CurrentFolder, targetDir, isCopy);
    }

    // 统一搬运引擎（**粘贴 / 拖拽 / 同笔记本 / 跨笔记本通用**）：把每个绝对源 srcAbs（来自 srcRoot 笔记本）移/拷到
    // 当前笔记本的 targetDir 下。isCopy=false 移动（重定向标签；同笔记本时摘源行）。
    //  • 同笔记本：套用「自身/子孙/原父目录」逐项守卫（跳过不取消整批，多选拖到选中集里的目标文件夹＝跳过它进其余）。
    //  • 跨笔记本：源不在当前树里，无需守卫/摘行；落盘 + 资产搬运由 FileService 跨根引擎处理。
    private async Task TransferAbsIntoAsync(List<string> srcAbsList, string srcRoot, string targetDir, bool isCopy)
    {
        if (srcAbsList.Count == 0 || _fileService.CurrentFolder is null) return;
        targetDir = (targetDir ?? "").Replace('\\', '/').Trim('/');
        var sameNotebook = PathsEqual(srcRoot, _fileService.CurrentFolder);

        foreach (var srcAbs in srcAbsList)
        {
            var isDir = Directory.Exists(srcAbs);
            FileNode? srcNode = null;
            if (sameNotebook && ToCurrentRel(srcAbs) is { } srcRel)
            {
                // 守卫：不能搬进自身/自己的子目录；移动到原父目录是空操作（复制到原父→生成 xxx (1) 副本，允许）。
                if (targetDir == srcRel ||
                    targetDir.StartsWith(srcRel + "/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!isCopy && targetDir == ParentDir(srcRel)) continue;
                srcNode = FindNodeByRel(srcRel);
            }

            string destRel;
            try { destRel = await _fileService.TransferEntryAsync(srcAbs, srcRoot, targetDir, isCopy: isCopy); }
            catch (Exception ex) { Log.Error(ex, "Transfer failed: {Src} -> {Dir}", srcAbs, targetDir); continue; }

            if (!isCopy)
            {
                // 移动：重定向打开的标签（绝对 token，跨笔记本也对）；同笔记本时把源行从当前树摘掉。
                if (_fileService.ResolveFullPath(destRel) is { } destAbs)
                    RetargetTabsUnder(srcAbs, destAbs, isDir);
                if (sameNotebook && srcNode is not null)
                {
                    if (ReferenceEquals(_activeNode, srcNode)) _activeNode = null;
                    _selection.Remove(srcNode);
                    FlatRemoveNode(srcNode);
                    CollectionForDir(ParentDir(srcNode.RelPath)).Remove(srcNode);
                }
            }

            InsertNewEntry(targetDir, destRel);
        }
    }

    // 两个绝对路径是否指同一目录（规范化 + 去尾分隔符 + 忽略大小写）。判断剪贴板源根是否＝当前笔记本。
    private static bool PathsEqual(string? a, string? b) =>
        a is not null && b is not null && string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    // Build a FileNode for a freshly created dest path and splice it into the tree + flat list
    // (expanding the target folder first so the row lands visibly). Returns the new node.
    private FileNode? InsertNewEntry(string targetDir, string destRel)
    {
        var node = _fileService.BuildNode(destRel);
        if (node is null) return null;
        node.ShowCheckBox = _multiSelectMode; // 多选模式下移入/复制进来的节点也带勾选框
        if (!string.IsNullOrEmpty(targetDir)
            && FindNodeByRel(targetDir) is { IsDirectory: true } parent && !parent.IsExpanded)
            ExpandRow(parent);
        InsertSorted(CollectionForDir(targetDir), node);
        FlatInsertNode(targetDir, node);
        if (IsParentSelected(targetDir)) SelectCascade(node); // 目标文件夹已选中 → 移入项也并入选中（含其子孙）
        return node;
    }

    // 目标目录对应的文件夹节点是否已被选中（root="" 不是节点、永远 false）。
    private bool IsParentSelected(string dir) =>
        !string.IsNullOrEmpty(dir) && FindNodeByRel(dir) is { } f && _selection.Contains(f);

    // Retarget open tabs after a move: tokens are absolute, so rewrite any token equal to (or,
    // for a folder, under) the old absolute path to the new location.
    private void RetargetTabsUnder(string oldAbs, string newAbs, bool isDir)
    {
        var prefix = oldAbs + Path.DirectorySeparatorChar;
        foreach (var tab in EditorTabs.TabItems.OfType<TabViewItem>())
        {
            if (tab.Tag is not string tag) continue;
            string? moved = tag == oldAbs ? newAbs
                : isDir && tag.StartsWith(prefix, StringComparison.Ordinal)
                    ? newAbs + Path.DirectorySeparatorChar + tag[prefix.Length..]
                    : null;
            if (moved is null) continue;
            tab.Tag = moved;
            tab.Header = Path.GetFileName(moved);
        }
        PersistOpenTabs();
    }

    // --- Context menus ---

    private void FileTreeItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FileNode node) return;

        // Right-clicking a row that isn't part of the selection re-selects just it (explorer
        // behavior); right-clicking inside a multi-selection keeps it and acts on the whole set.
        if (!_selection.Contains(node)) { SelectOnly(node); _selectionAnchor = node; }

        // 「是否多选」看 TopLevelSelection（去重后的顶层项）——materialized 级联会把子孙也塞进 _selection，
        // 不能用 _selection.Count。单选时菜单作用于那个顶层项（如右键已选文件夹内的某文件，仍出文件夹菜单）。
        var top = TopLevelSelection();
        if (top.Count > 1)
        {
            ShowMultiSelectionMenu(fe, e.GetPosition(fe));
            e.Handled = true;
            return;
        }
        var menuNode = top.Count == 1 ? top[0] : node;

        var flyout = new MenuFlyout();
        // Create / rename open the naming flyout on this row. Deferred so the context menu has
        // fully closed before we show another flyout on the same anchor.
        if (menuNode.IsDirectory)
        {
            AddMenuItem(flyout, "新建笔记", GlyphNewNote, () => DeferOnTree(() => PromptCreate(menuNode.RelPath, isFolder: false, fe)));
            AddMenuItem(flyout, "新建文件夹", GlyphNewFolder, () => DeferOnTree(() => PromptCreate(menuNode.RelPath, isFolder: true, fe)));
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        else
        {
            // 笔记：右键也能打开（与单/双击同走 OpenNode → OpenNoteAsync）。
            AddMenuItem(flyout, "打开", GlyphOpen, () => OpenNode(menuNode));
            // 仅当该笔记已在标签里打开时给「关闭」入口（关闭本身在 TabView 里做，这里是便捷入口）。
            if (IsNoteOpen(menuNode.RelPath))
                AddMenuItem(flyout, "关闭", GlyphClose, () => CloseTabsUnder(menuNode.RelPath, isDirectory: false));
            AddMenuItem(flyout, "创建副本", GlyphCopy, () => _ = DuplicateNodeAsync(menuNode));
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        // 复制/剪切到内部剪贴板；文件夹还能把剪贴板内容粘进来。
        AddMenuItem(flyout, "复制", GlyphCopy, () => ClipboardCopy(cut: false));
        AddMenuItem(flyout, "剪切", GlyphCut, () => ClipboardCopy(cut: true));
        if (menuNode.IsDirectory && CanPaste())
            AddMenuItem(flyout, "粘贴", GlyphPaste, () => _ = PasteIntoAsync(menuNode.RelPath));
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "重命名", GlyphRename, () => DeferOnTree(() => PromptRename(menuNode, fe)));
        AddMenuItem(flyout, "删除", GlyphDelete, () => _ = DeleteNodeAsync(menuNode));
        flyout.Items.Add(new MenuFlyoutSeparator());
        if (menuNode.IsDirectory)
            AddMenuItem(flyout, "设为笔记本", GlyphNotebook, () => SetFolderAsNotebook(menuNode));
        AddMenuItem(flyout, "复制路径", GlyphCopy, () => CopyNodePath(menuNode));
        AddMenuItem(flyout, "在资源管理器中显示", GlyphReveal, () => RevealInExplorer(menuNode));

        flyout.ShowAt(fe, e.GetPosition(fe));
        e.Handled = true;
    }

    // 目录树空白处右键菜单。点在节点上时 FileTreeItem_RightTapped 已 e.Handled=true 截断。
    private void FileTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_fileService.CurrentFolder is null) return;

        var anchor = sender as FrameworkElement ?? FileTree;
        var pos = e.GetPosition(anchor);

        var flyout = new MenuFlyout();
        AddMenuItem(flyout, "新建笔记", GlyphNewNote, () => DeferOnTree(() => PromptCreate("", isFolder: false, anchor, pos)));
        AddMenuItem(flyout, "新建文件夹", GlyphNewFolder, () => DeferOnTree(() => PromptCreate("", isFolder: true, anchor, pos)));
        if (CanPaste())
            AddMenuItem(flyout, "粘贴", GlyphPaste, () => _ = PasteIntoAsync(""));
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "复制路径", GlyphCopy, CopyRootPath);
        AddMenuItem(flyout, "刷新", GlyphRefresh, RefreshTree);
        AddMenuItem(flyout, "在资源管理器中打开", GlyphOpenFolder, OpenCurrentFolderInExplorer);
        flyout.Items.Add(new MenuFlyoutSeparator());
        // 快速切换笔记本（复制/剪切后切到目标笔记本再粘贴；也兼作名称快切）。
        flyout.Items.Add(BuildSwitchNotebookSubmenu());
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(flyout, "关闭目录树", GlyphOpenPane, () => SetSidebarVisible(false));

        flyout.ShowAt(anchor, pos);
        e.Handled = true;
    }

    // 「切换笔记本」二级菜单：每个笔记本一项，当前活动项打勾；点其它项＝切过去（同 进入笔记本）。
    // 名称过长用省略号截断，完整名作 ToolTip 悬浮可见。
    private MenuFlyoutSubItem BuildSwitchNotebookSubmenu()
    {
        var sub = new MenuFlyoutSubItem { Text = "切换笔记本", Icon = new FontIcon { Glyph = GlyphNotebook } };
        if (_settings.Notebooks.Count == 0)
        {
            sub.Items.Add(new MenuFlyoutItem { Text = "（没有笔记本）", IsEnabled = false });
            return sub;
        }
        var activeId = _settings.ActiveNotebook?.Id;
        foreach (var nb in _settings.Notebooks)
        {
            var captured = nb;
            var item = new ToggleMenuFlyoutItem { Text = Ellipsize(nb.Name, 22), IsChecked = nb.Id == activeId };
            ToolTipService.SetToolTip(item, nb.Name);
            item.Click += (_, _) => { if (captured.Id != _settings.ActiveNotebook?.Id) ActivateNotebook(captured); };
            sub.Items.Add(item);
        }
        return sub;
    }

    private static string Ellipsize(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..(max - 1)] + "…";

    // Run an action on the next dispatcher turn — lets a just-clicked context menu finish
    // closing before we open the naming flyout on the same anchor.
    private void DeferOnTree(Action action) => DispatcherQueue.TryEnqueue(() => action());

    private static void AddMenuItem(MenuFlyout flyout, string text, string glyph, Action onClick) =>
        AddMenuItem(flyout, text, new FontIcon { Glyph = glyph }, onClick);

    // 自绘矢量图标版（PathIcon），与 Segoe 字形版混用：自绘的几个用它。
    private static void AddMenuItem(MenuFlyout flyout, string text, IconElement icon, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = icon };
        item.Click += (_, _) => onClick();
        flyout.Items.Add(item);
    }

    // --- Copy path to clipboard ---

    private void CopyNodePath(FileNode node)
    {
        var full = _fileService.ResolveFullPath(node.RelPath);
        if (!string.IsNullOrEmpty(full)) CopyTextToClipboard(full);
    }

    private void CopyRootPath()
    {
        var folder = _fileService.CurrentFolder;
        if (!string.IsNullOrEmpty(folder)) CopyTextToClipboard(folder);
    }

    private void CopyTextToClipboard(string text)
    {
        _selfClipboardWrite = true; // 标记：随后的 ContentChanged 是我们自己写的（复制路径），别清内部剪贴板
        var pkg = new DataPackage();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
    }

    // 系统剪贴板被外部改变（用户在资源管理器/别处复制了新东西）→ 作废 Mica 内部剪贴板，
    // 让「最新一次复制」生效。否则内部剪贴板复制后永不清空，本会话内会一直盖过系统剪贴板的外部文件。
    // 自身写剪贴板（复制路径）用 _selfClipboardWrite 排除。订阅在 MainWindow 构造函数。
    private bool _selfClipboardWrite;
    private void OnSystemClipboardChanged(object? sender, object e)
    {
        if (_selfClipboardWrite) { _selfClipboardWrite = false; return; }
        DispatcherQueue.TryEnqueue(() =>
        {
            _clipboardAbs = [];
            _clipboardSrcRoot = null;
            _clipboardCut = false;
        });
    }

    // Open the current notebook's folder itself in Explorer (no /select).
    private void OpenCurrentFolderInExplorer()
    {
        var folder = _fileService.CurrentFolder;
        if (folder is null || !Directory.Exists(folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "Open folder in explorer failed: {Path}", folder); }
    }

    private void SetFolderAsNotebook(FileNode node)
    {
        var full = _fileService.ResolveFullPath(node.RelPath);
        if (full is null || !Directory.Exists(full)) return;
        var nb = _settings.AddOrGetNotebook(full);
        if (_settings.NewNotebookEntry == 1) ActivateNotebook(nb);
        else NavigateTo(new ViewState(ViewKind.Detail, nb.Id));
    }

    private void RevealInExplorer(FileNode node)
    {
        var full = _fileService.ResolveFullPath(node.RelPath);
        if (full is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "Reveal in explorer failed: {Path}", full); }
    }

    private static SolidColorBrush HexBrush(string rgb) =>
        new(Windows.UI.Color.FromArgb(0xFF,
            Convert.ToByte(rgb.Substring(0, 2), 16),
            Convert.ToByte(rgb.Substring(2, 2), 16),
            Convert.ToByte(rgb.Substring(4, 2), 16)));

    // Make a ContentDialog's primary button a danger-red accent button whose
    // hover/pressed states just darken the red (rather than reverting to system blue).
    // Scoped to the dialog's own Resources so nothing else is recolored. Leave 取消
    // (Close) gray — callers set DefaultButton = None for destructive dialogs.
    private static void ApplyDangerPrimary(ContentDialog dialog)
    {
        dialog.PrimaryButtonStyle = Application.Current.Resources["AccentButtonStyle"] as Style;
        dialog.Resources["AccentButtonBackground"] = HexBrush("C42B1C");
        dialog.Resources["AccentButtonBackgroundPointerOver"] = HexBrush("B0261A");
        dialog.Resources["AccentButtonBackgroundPressed"] = HexBrush("9C2117");
        dialog.Resources["AccentButtonForeground"] = HexBrush("FFFFFF");
        dialog.Resources["AccentButtonForegroundPointerOver"] = HexBrush("FFFFFF");
        dialog.Resources["AccentButtonForegroundPressed"] = HexBrush("FFFFFF");
    }

    // --- Tab sync helpers ---

    private void RetargetOpenTab(string oldRel, string newRel)
    {
        // Tabs are keyed by absolute path; translate the renamed node's rel paths first.
        if (_fileService.ResolveFullPath(oldRel) is not { } oldFull ||
            _fileService.ResolveFullPath(newRel) is not { } newFull) return;
        foreach (var item in EditorTabs.TabItems)
        {
            if (item is TabViewItem { Tag: string tag } tab && tag == oldFull)
            {
                tab.Tag = newFull;
                tab.Header = Path.GetFileName(newFull);
            }
        }
        PersistOpenTabs();
    }

    // 该笔记（relPath）当前是否已在某个标签里打开（标签 token 是绝对路径）。供右键「关闭」入口判断显隐。
    private bool IsNoteOpen(string relPath)
    {
        if (_fileService.ResolveFullPath(relPath) is not { } full) return false;
        return EditorTabs.TabItems.OfType<TabViewItem>().Any(t => t.Tag is string tag && tag == full);
    }

    // Close tabs pointing at a deleted file, or any file under a deleted folder. Tab tokens
    // are absolute, so match against the node's absolute path.
    private void CloseTabsUnder(string relPath, bool isDirectory)
    {
        if (_fileService.ResolveFullPath(relPath) is not { } full) return;
        var prefix = full + Path.DirectorySeparatorChar;
        var toRemove = EditorTabs.TabItems
            .OfType<TabViewItem>()
            .Where(t => t.Tag is string tag &&
                        (tag == full || (isDirectory && tag.StartsWith(prefix, StringComparison.Ordinal))))
            .ToList();
        foreach (var tab in toRemove) EditorTabs.TabItems.Remove(tab);
        UpdateEditorChrome(); // 标签数变了，刷新编辑区/空状态显隐
        if (EditorTabs.TabItems.Count == 0)
        {
            _ipcRouter.SendNotification("editor.load", new { relPath = "", body = "" });
            ClearActiveFile();
        }
        PersistOpenTabs();
    }
}
