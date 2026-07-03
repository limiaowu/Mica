using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mica.Models;
using Mica.Services;

namespace Mica.Views;

// 笔记本详情页。2026-06 从 MainWindow 拆为独立 UserControl。
// 本控件自管：填充封面/名称/简介/统计、移除封面、编辑后刷新。
// 「跨页」操作（进入笔记本/编辑信息对话框/在资源管理器显示/删除）经事件/回调交回 MainWindow。
public sealed partial class NotebookDetailPage : UserControl
{
    private readonly SettingsService _settings;

    // x:Bind 的封面绑定解析到这个字段（Show 时 Bindings.Update 刷新）。
    private Notebook? _detailNotebook;

    public event Action<Notebook>? ActivateRequested;    // 进入笔记本
    public Func<Notebook, Task>? EditRequested;          // 编辑信息对话框（MainWindow 弹，返回后本页刷新）
    public Action<string>? RevealRequested;              // 在资源管理器中显示
    public Action<Notebook>? MaintenanceRequested;       // 图片维护（打开维护覆盖页）
    public Func<IList<Notebook>, Task>? DeleteRequested; // 删除

    public NotebookDetailPage()
    {
        _settings = App.Services.GetRequiredService<SettingsService>();
        InitializeComponent();
    }

    // 显示某笔记本详情：填充所有字段 + 统计。
    public void Show(Notebook nb)
    {
        _detailNotebook = nb;
        Bindings.Update(); // 刷新 x:Bind 封面绑定

        DetailInitial.Text = nb.Initial;
        DetailName.Text = nb.Name;
        DetailCreated.Text = nb.CreatedText;
        DetailDescription.Text = nb.HasDescription ? nb.Description : "暂无简介";
        DetailPath.Text = nb.Path;

        var (notes, folders) = ComputeNotebookStats(nb.Path);
        DetailNoteCount.Text = notes.ToString();
        DetailFolderCount.Text = folders.ToString();
        DetailLastOpened.Text = nb.LastOpened == default
            ? "—" : nb.LastOpened.LocalDateTime.ToString("MM-dd");
    }

    private static (int notes, int folders) ComputeNotebookStats(string path)
    {
        var notes = FileService.CountMarkdownFiles(path);
        var folders = 0;
        try
        {
            if (Directory.Exists(path))
                folders = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                    .Count(d => !Path.GetFileName(d).StartsWith('.'));
        }
        catch { /* best effort */ }
        return (notes, folders);
    }

    private async void OnDetailEdit(object sender, RoutedEventArgs e)
    {
        if (_detailNotebook is { } nb && EditRequested is not null)
        {
            await EditRequested(nb);
            Show(nb); // 编辑信息后刷新本页
        }
    }

    private void OnDetailRemoveCover(object sender, RoutedEventArgs e)
    {
        if (_detailNotebook is { } nb) { nb.CoverImagePath = null; _settings.Save(); Show(nb); }
    }

    private void OnDetailReveal(object sender, RoutedEventArgs e)
    {
        if (_detailNotebook is { } nb) RevealRequested?.Invoke(nb.Path);
    }

    private void OnDetailMaintenance(object sender, RoutedEventArgs e)
    {
        if (_detailNotebook is { } nb) MaintenanceRequested?.Invoke(nb);
    }

    private void OnDetailOpenWorkspace(object sender, RoutedEventArgs e)
    {
        if (_detailNotebook is { } nb) ActivateRequested?.Invoke(nb);
    }

    private void OnDetailDelete(object sender, RoutedEventArgs e)
    {
        if (_detailNotebook is { } nb && DeleteRequested is not null) _ = DeleteRequested([nb]);
    }
}
