using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mica.Models;
using Mica.Services;

namespace Mica.Views;

// 笔记本主页（卡片墙）。2026-06 从 MainWindow 拆为独立 UserControl。
// 本控件自管：卡片单击/双击/右键、多选 UI、拖拽排序、卡片计数刷新、空状态。
// 「跨页」操作（进入笔记本/查看详情/新建/打开已有文件夹/重命名/在资源管理器显示/删除）
// 本控件做不了——经事件/回调交回 MainWindow（沿用 SettingsPage 的解耦模式）。
public sealed partial class NotebookHomePage : UserControl
{
    private readonly SettingsService _settings;

    // 单击→详情 vs 双击→进入笔记本 的区分计时器（双击的第一下也会触发单击，用短计时器区分）。
    private readonly DispatcherTimer _cardClickTimer;
    private Notebook? _pendingClickNotebook;
    private bool _multiSelect;

    // 跨页副作用：本控件做不了，回调到 MainWindow 处理。
    public event Action<Notebook>? ActivateRequested;    // 进入笔记本（设为活动工作区）
    public event Action<Notebook>? DetailRequested;      // 打开详情页
    public event Action? NewNotebookRequested;           // 新建笔记本对话框
    public event Action? OpenFolderRequested;            // 打开已有文件夹
    public Func<Notebook, Task>? RenameRequested;        // 重命名对话框
    public Action<string>? RevealRequested;              // 在资源管理器中显示（传路径）
    public event Action<IList<Notebook>>? MaintenanceRequested; // 图片维护所选（打开维护覆盖页）
    public Func<IList<Notebook>, Task>? DeleteRequested; // 删除（确认对话框 + 文件操作）

    public NotebookHomePage()
    {
        // SettingsService 走 DI 取（与 MainWindow 同一单例），供 x:Bind _settings.Notebooks。
        _settings = App.Services.GetRequiredService<SettingsService>();
        InitializeComponent();

        _cardClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _cardClickTimer.Tick += OnCardClickTimerTick;
    }

    // 进入主页时刷新每个笔记本的笔记数 + 空状态。
    public void Refresh()
    {
        foreach (var nb in _settings.Notebooks)
            nb.NoteCount = FileService.CountMarkdownFiles(nb.Path);

        NotebookEmptyState.Visibility = _settings.Notebooks.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- 卡片交互 ---

    private void NotebookCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_multiSelect) return; // 多选时由 GridView 处理选中
        if ((sender as FrameworkElement)?.DataContext is not Notebook nb) return;
        _pendingClickNotebook = nb;
        _cardClickTimer.Stop();
        _cardClickTimer.Start();
    }

    private void NotebookCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _cardClickTimer.Stop();
        _pendingClickNotebook = null;
        if (_multiSelect) return;
        if ((sender as FrameworkElement)?.DataContext is Notebook nb) ActivateRequested?.Invoke(nb);
    }

    private void OnCardClickTimerTick(object? sender, object e)
    {
        _cardClickTimer.Stop();
        var nb = _pendingClickNotebook;
        _pendingClickNotebook = null;
        if (nb is not null) DetailRequested?.Invoke(nb);
    }

    // 拖拽就地改了绑定集合，持久化新顺序。
    private void NotebookGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        => _settings.PersistOrder();

    private void NotebookCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Notebook nb } element) return;

        var flyout = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = "进入笔记本" };
        open.Click += (_, _) => ActivateRequested?.Invoke(nb);
        flyout.Items.Add(open);

        var detail = new MenuFlyoutItem { Text = "查看详情" };
        detail.Click += (_, _) => DetailRequested?.Invoke(nb);
        flyout.Items.Add(detail);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += (_, _) => { if (RenameRequested is not null) _ = RenameRequested(nb); };
        flyout.Items.Add(rename);

        var maintain = new MenuFlyoutItem { Text = "笔记本维护" };
        maintain.Click += (_, _) => MaintenanceRequested?.Invoke([nb]);
        flyout.Items.Add(maintain);

        var reveal = new MenuFlyoutItem { Text = "在资源管理器中显示" };
        reveal.Click += (_, _) => RevealRequested?.Invoke(nb.Path);
        flyout.Items.Add(reveal);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var remove = new MenuFlyoutItem { Text = "删除" };
        remove.Click += (_, _) => { if (DeleteRequested is not null) _ = DeleteRequested([nb]); };
        flyout.Items.Add(remove);

        flyout.ShowAt(element, e.GetPosition(element));
    }

    // --- 多选 ---

    private void OnToggleMultiSelect(object sender, RoutedEventArgs e)
    {
        _multiSelect = MultiSelectToggle.IsChecked == true;
        NotebookGrid.SelectionMode = _multiSelect
            ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        NotebookGrid.CanReorderItems = !_multiSelect;
        NotebookGrid.CanDragItems = !_multiSelect;
        NotebookGrid.AllowDrop = !_multiSelect;
        var vis = _multiSelect ? Visibility.Visible : Visibility.Collapsed;
        MaintainSelectedButton.Visibility = vis;
        DeleteSelectedButton.Visibility = vis;
    }

    // 删除完成后由 MainWindow 调用，复位多选 UI。
    public void ExitMultiSelect()
    {
        _multiSelect = false;
        MultiSelectToggle.IsChecked = false;
        NotebookGrid.SelectionMode = ListViewSelectionMode.None;
        NotebookGrid.CanReorderItems = true;
        NotebookGrid.CanDragItems = true;
        NotebookGrid.AllowDrop = true;
        MaintainSelectedButton.Visibility = Visibility.Collapsed;
        DeleteSelectedButton.Visibility = Visibility.Collapsed;
    }

    private void OnMaintainSelectedNotebooks(object sender, RoutedEventArgs e)
    {
        var selected = NotebookGrid.SelectedItems.OfType<Notebook>().ToList();
        if (selected.Count > 0) MaintenanceRequested?.Invoke(selected);
    }

    private void OnDeleteSelectedNotebooks(object sender, RoutedEventArgs e)
    {
        var selected = NotebookGrid.SelectedItems.OfType<Notebook>().ToList();
        if (selected.Count > 0 && DeleteRequested is not null) _ = DeleteRequested(selected);
    }

    private void OnNewNotebookClick(object sender, RoutedEventArgs e) => NewNotebookRequested?.Invoke();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenFolderRequested?.Invoke();
}
