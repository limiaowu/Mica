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

// Part of the MainWindow partial class. NavigationView rail, nav order, back/forward history, view switching.
public sealed partial class MainWindow
{

    // --- NavigationView (official) ---

    // Items: 工作区 (toggles the tree/大纲 panel; the tree is its own column, NOT in the pane),
    // 笔记本 (footer → home page), 设置 (built-in gear). The official selection indicator handles
    // the highlight; we just route invokes.
    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            NavigateTo(new ViewState(ViewKind.Settings, null));
            return;
        }

        var tag = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        switch (tag)
        {
            case "Workspace":
                // From an overlay page → back to the editor with the panel open; already in the
                // editor → toggle the tree/大纲 panel.
                if (!InEditorView)
                {
                    NavigateTo(new ViewState(ViewKind.Files, null));
                    SetSidebarVisible(true);
                }
                else
                {
                    SetSidebarVisible(TreePanel.Visibility != Visibility.Visible);
                }
                break;
            case "Notebooks":
                NavigateTo(new ViewState(ViewKind.Home, null));
                break;
        }
    }

    // --- View navigation history (title-bar back/forward) ---

    private void NavigateTo(ViewState target)
    {
        if (!_navigating)
        {
            // MRU 折叠：导航到的目标若已在历史里，先把它旧的那次删掉再压栈，让每个「目的地」
            // 最多出现一次。这样来回切笔记本↔设置时栈不会无限增长（在固定深度内震荡，后退 1~2 次
            // 即回编辑器），同时仍保留「笔记本主页→详情→后退回主页」这种钻取关系。
            // ViewState 是 record struct，天然值相等。目标==当前栈顶时只重新 ApplyViewState 刷新、不动栈。
            if (_history.Count == 0 || _history[_historyIndex] != target)
            {
                // 先丢弃前进分支（站在历史中段再导航 → 砍掉后面的）
                if (_historyIndex < _history.Count - 1)
                    _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                // 删掉目标在剩余回退栈里的旧出现（当前栈顶 != target，不会被误删），再压到栈顶。
                _history.RemoveAll(v => v == target);
                _history.Add(target);
                _historyIndex = _history.Count - 1;
            }
        }
        ApplyViewState(target);
        UpdateNavButtons();
    }

    private void OnNavBack(object sender, RoutedEventArgs e) => NavigateBack();

    // 后退一格（标题栏后退按钮 + 维护页「返回」共用）。
    private void NavigateBack()
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        _navigating = true;
        ApplyViewState(_history[_historyIndex]);
        _navigating = false;
        UpdateNavButtons();
    }

    private void OnNavForward(object sender, RoutedEventArgs e)
    {
        if (_historyIndex >= _history.Count - 1) return;
        _historyIndex++;
        _navigating = true;
        ApplyViewState(_history[_historyIndex]);
        _navigating = false;
        UpdateNavButtons();
    }

    private void UpdateNavButtons()
    {
        NavBackButton.IsEnabled = _historyIndex > 0;
        NavForwardButton.IsEnabled = _historyIndex < _history.Count - 1;
    }

    private void ApplyViewState(ViewState s)
    {
        switch (s.Kind)
        {
            case ViewKind.Home:
                ShowHomeInternal();
                break;
            case ViewKind.Files:
                ShowFilesInternal();
                break;
            case ViewKind.Detail:
                var nb = _settings.Notebooks.FirstOrDefault(n => n.Id == s.NotebookId);
                if (nb is not null) ShowDetailInternal(nb);
                else ShowHomeInternal();
                break;
            case ViewKind.Settings:
                ShowSettingsInternal();
                break;
            case ViewKind.Maintenance:
                var ids = (s.NotebookId ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                var nbs = ids
                    .Select(id => _settings.Notebooks.FirstOrDefault(n => n.Id == id))
                    .Where(n => n is not null).Cast<Notebook>().ToList();
                if (nbs.Count > 0) ShowMaintenanceInternal(nbs);
                else ShowHomeInternal();
                break;
        }
    }

    // --- View internals (no history side effects) ---

    private void ShowHomeInternal()
    {
        // 卡片计数/空状态的刷新已下沉到 NotebookHomePage.Refresh()。
        NotebookHomeView.Refresh();

        EditorSurface.Visibility = Visibility.Collapsed;
        NotebookDetailView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        MaintenanceView.Visibility = Visibility.Collapsed;
        NotebookHomeView.Visibility = Visibility.Visible;
        NavView.SelectedItem = NotebooksNavItem;
        // The centered notebook switcher is an editor-scoped affordance; hide the whole
        // DropDownButton (not just its text) on the notebook pages so only an empty
        // chevron isn't left dangling near the window caption buttons.
        UpdateVaultNameVisibility();
    }

    private void ShowFilesInternal()
    {
        NotebookHomeView.Visibility = Visibility.Collapsed;
        NotebookDetailView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        MaintenanceView.Visibility = Visibility.Collapsed;
        EditorSurface.Visibility = Visibility.Visible;
        UpdateVaultNameVisibility();
        // 工作区 represents the editor surface; keep it selected in the nav. The 工作区/大纲
        // switch lives on the panel pill.
        NavView.SelectedItem = WorkspaceNavItem;
        UpdateSidebarToggle();
    }

    private void ShowDetailInternal(Notebook nb)
    {
        // 填充字段/统计已下沉到 NotebookDetailPage.Show()。
        NotebookDetailView.Show(nb);

        EditorSurface.Visibility = Visibility.Collapsed;
        NotebookHomeView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        MaintenanceView.Visibility = Visibility.Collapsed;
        NotebookDetailView.Visibility = Visibility.Visible;
        NavView.SelectedItem = NotebooksNavItem;
        UpdateVaultNameVisibility();
    }

    // 维护覆盖页：扫描并显示一组笔记本的图片体检（纳入前进/后退历史）。
    private void ShowMaintenanceInternal(IList<Notebook> nbs)
    {
        MaintenanceView.Show(nbs);

        EditorSurface.Visibility = Visibility.Collapsed;
        NotebookHomeView.Visibility = Visibility.Collapsed;
        NotebookDetailView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        MaintenanceView.Visibility = Visibility.Visible;
        NavView.SelectedItem = NotebooksNavItem;
        UpdateVaultNameVisibility();
    }

    // 设置页：填充 SettingsPage 控件后显示它（覆盖 [TreePanel+编辑器]，纳入前进/后退历史）。
    private void ShowSettingsInternal()
    {
        SettingsView.LoadSettings(_themePref);

        EditorSurface.Visibility = Visibility.Collapsed;
        NotebookHomeView.Visibility = Visibility.Collapsed;
        NotebookDetailView.Visibility = Visibility.Collapsed;
        MaintenanceView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        NavView.SelectedItem = NavView.SettingsItem;
        UpdateVaultNameVisibility();
    }

}
