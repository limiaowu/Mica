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
using Mica.Models;
using Mica.Services;
using Mica.IPC;
using Mica.IPC.Handlers;
using Serilog;

namespace Mica;

// Part of the MainWindow partial class. Editor tabs (TabView): open note, session persist/restore.
public sealed partial class MainWindow
{
    // --- Tabs (TabView) ---

    // `fullPath` is an ABSOLUTE path (the tab token). Tabs are notebook-independent so an
    // open file keeps saving/loading after the active notebook changes; the absolute token
    // round-trips through editor.load → note.save and resolves regardless of current root.
    private Task OpenNoteAsync(string fullPath)
    {
        try
        {
            foreach (TabViewItem tab in EditorTabs.TabItems)
            {
                if (tab.Tag is string path && path == fullPath)
                {
                    EditorTabs.SelectedItem = tab;
                    UpdateEditorChrome();
                    return Task.CompletedTask;
                }
            }

            var tabItem = new TabViewItem
            {
                Header = Path.GetFileName(fullPath),
                Tag = fullPath,
                IsClosable = _settings.TabCloseButtonMode != 2,
                IconSource = new SymbolIconSource { Symbol = Symbol.Document }
            };
            // 把关闭按钮（×）的 ToolTip「(Ctrl+F4)」改成「(Ctrl+W)」。用原生 TabViewItem + Loaded 钩子，
            // 而非子类化——子类化会丢默认模板导致标签头不渲染。详见 OnTabCloseButtonTooltip。
            tabItem.Loaded += OnTabItemLoaded;
            EditorTabs.TabItems.Add(tabItem);
            EditorTabs.SelectedItem = tabItem;
            UpdateEditorChrome(); // 有笔记了，显示编辑区面板（直接调用，不依赖 TabItemsChanged 事件）
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open note: {FullPath}", fullPath);
        }
        return Task.CompletedTask;
    }

    private async void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (EditorTabs.SelectedItem is TabViewItem selectedTab && selectedTab.Tag is string relPath)
        {
            try
            {
                var body = await _fileService.ReadFileAsync(relPath);
                _ipcRouter.SendNotification("editor.load", new { relPath, body });
                SetActiveFile(relPath);
                PersistOpenTabs();
                FlushPendingReveal(); // 维护页「定位」：load 之后补发挂起的 editor.reveal
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load tab content: {RelPath}", relPath);
            }
        }
    }

    private void EditorTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        sender.TabItems.Remove(args.Tab);
        UpdateEditorChrome(); // 标签数变了，刷新编辑区/空状态显隐

        if (sender.TabItems.Count == 0)
        {
            _ipcRouter.SendNotification("editor.load", new { relPath = "", body = "" });
            ClearActiveFile();
        }
        // Removal already fires TabItemsChanged → PersistOpenTabs; persist here too for
        // the empty case (TabItemsChanged on Clear vs Remove both covered).
        PersistOpenTabs();
    }

    // Reflect tab adds / removes / drag-reorders into the active notebook's saved
    // session. Suppressed during programmatic teardown/restore (see _suppressTabPersist).
    private void EditorTabs_TabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
    {
        PersistOpenTabs();
        // Re-assert the close-button mode once a newly inserted tab's container is
        // realized, so it honors OnPointerOver instead of showing the × permanently.
        if (args.CollectionChange == Windows.Foundation.Collections.CollectionChange.ItemInserted)
            DispatcherQueue.TryEnqueue(ApplyTabCloseButton);
    }

    // 没有打开笔记时，整体隐藏编辑区面板（EditorPanel：TabView+WebView+状态栏），露出居中淡色
    // 提示和干净的 Mica 背景。**由每个增删标签的位置直接调用**（OpenNoteAsync / TabCloseRequested /
    // OnCloseCurrentTab / CloseTabsUnder）+ 启动恢复末尾（RestoreActiveNotebook）。不要改回挂在
    // TabItemsChanged 事件上——WinUI 的 TabView.TabItemsChanged 对直接操作 TabItems 集合触发不可靠，
    // 曾导致「打开笔记没反应、一直显示空状态」。
    private void UpdateEditorChrome()
    {
        var hasNote = EditorTabs.TabItems.Count > 0;
        EditorPanel.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
        EditorEmptyState.Visibility = hasNote ? Visibility.Collapsed : Visibility.Visible;
    }

    // Snapshot the open tabs (in order) + the active one into settings.
    private void PersistOpenTabs()
    {
        if (_suppressTabPersist) return;
        var open = new List<string>();
        foreach (TabViewItem t in EditorTabs.TabItems)
            if (t.Tag is string p) open.Add(p);
        var active = (EditorTabs.SelectedItem as TabViewItem)?.Tag as string;
        _settings.SetActiveOpenFiles(open, active);
    }

    // Reopen a saved set of tabs and select the previously active one. Persistence is
    // suppressed during the rebuild so we don't clobber the saved set mid-flight.
    private async Task RestoreTabsAsync(List<string> files, string? activeFile)
    {
        _suppressTabPersist = true;
        try
        {
            foreach (var f in files)
                await OpenNoteAsync(f);

            foreach (TabViewItem t in EditorTabs.TabItems)
            {
                if (t.Tag is string p && p == activeFile)
                {
                    EditorTabs.SelectedItem = t;
                    break;
                }
            }
        }
        finally
        {
            _suppressTabPersist = false;
        }
        PersistOpenTabs();
    }

    // Apply the tab close-button preference, mirroring WinUI's TabView design:
    // mode 2 (始终隐藏) makes every tab non-closable so no × shows; modes 0/1 keep
    // tabs closable and only differ in CloseButtonOverlayMode — OnPointerOver
    // (当前标签常显、其余悬停才显) vs. Always (全部常显).
    private void ApplyTabCloseButton()
    {
        var mode = _settings.TabCloseButtonMode;
        var closable = mode != 2;
        foreach (TabViewItem t in EditorTabs.TabItems)
            t.IsClosable = closable;
        // A freshly added tab can fail to inherit the TabView's current overlay mode
        // (WinUI quirk: it keeps the × always visible even when unselected). Setting
        // the same value is a no-op, so bounce through Auto to force a real property
        // change that re-propagates to every realized tab. Both writes happen before
        // the next render, so there's no visible flicker.
        EditorTabs.CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto;
        EditorTabs.CloseButtonOverlayMode = mode == 1
            ? TabViewCloseButtonOverlayMode.Always
            : TabViewCloseButtonOverlayMode.OnPointerOver;
    }

    // WinUI 把关闭按钮（×）的 ToolTip「关闭选项卡 (Ctrl+F4)」硬编码在内部资源里，没有公开属性可改，
    // (Ctrl+F4) 还是内置快捷键拼出来的。官方仓库（#8719/#4930）推荐：Loaded 时用 VisualTreeHelper
    // 找到模板里固定命名 "CloseButton" 的按钮，覆盖其 ToolTip。Ctrl+F4 内置键仍可用、只是不再显示。
    // 注意：必须用原生 TabViewItem（子类化会丢默认模板、标签头不渲染）；找不到按钮时静默跳过，
    // 最坏只是提示没改成 Ctrl+W，绝不影响标签本身。
    private void OnTabItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TabViewItem item && FindChildByName(item, "CloseButton") is { } closeButton)
            ToolTipService.SetToolTip(closeButton, "关闭标签页 (Ctrl+W)");
    }

    // 按名称在可视化树中递归查找子控件。
    private static FrameworkElement? FindChildByName(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return fe;
            if (FindChildByName(child, name) is { } found)
                return found;
        }
        return null;
    }

}
