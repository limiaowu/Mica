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

// 左列一项：「总览」或某个笔记本。扫描完成后 Report/Subtitle 才填好，故 ItemsSource 一次性设、x:Bind OneTime。
public sealed class MaintTarget
{
    public string Title { get; init; } = "";
    public string Subtitle { get; set; } = "";
    public bool IsOverview { get; init; }
    public Notebook? Notebook { get; init; }
    public NotebookImageReport? Report { get; set; }
}

// 笔记本维护覆盖页（主从容器）。详情页菜单（单个）/ 主页多选（批量）两入口复用本页。
// 扫描走后台线程；单笔记本时左列收起、直接显示报告；≥2 个时左列含「总览」聚合。
// 跨页副作用（返回 / 清理孤儿）经事件交回 MainWindow。
public sealed partial class MaintenancePage : UserControl
{
    private readonly NotebookMaintenanceService _maint;

    private List<Notebook> _notebooks = [];
    private string _key = "";
    private int _scanToken;

    public event Action<NotebookImageReport>? CleanOrphansRequested; // 清理未引用图片（MainWindow 弹确认 + 删 + 重扫）
    public event Action<NotebookImageReport>? CleanAllIssuesRequested; // 清理全部 Markdown 问题
    public event Action<(string noteAbs, string revealKind, int revealIndex)>? LocateRequested; // 打开笔记并滚到原处
    public event Action<(string noteAbs, string fullMatch, string kindLabel)>? CleanRefRequested; // 删除某条问题
    public event Action<NotebookImageReport>? MigrateRequested; // 一键迁移到当前存储模式

    public MaintenancePage()
    {
        _maint = App.Services.GetRequiredService<NotebookMaintenanceService>();
        InitializeComponent();
        ReportView.CleanOrphansRequested += r => CleanOrphansRequested?.Invoke(r);
        ReportView.CleanAllIssuesRequested += r => CleanAllIssuesRequested?.Invoke(r);
        ReportView.LocateRequested += t => LocateRequested?.Invoke(t);
        ReportView.CleanRefRequested += t => CleanRefRequested?.Invoke(t);
        ReportView.MigrateRequested += r => MigrateRequested?.Invoke(r);
    }

    // 入口调用：展示一组笔记本的维护视图。相同集合再次进入不重扫（back/forward 不闪）。
    public void Show(IList<Notebook> notebooks)
    {
        var key = string.Join(",", notebooks.Select(n => n.Id));
        if (key == _key && TargetList.ItemsSource is not null) return; // 同一批，沿用上次扫描结果
        _key = key;
        _notebooks = notebooks.ToList();
        _ = ScanAndRenderAsync();
    }

    // 重新扫描当前集合（顶栏按钮 / 清理孤儿后由 MainWindow 调）。
    public async Task Rescan()
    {
        if (_notebooks.Count == 0) return;
        await ScanAndRenderAsync();
    }

    private async Task ScanAndRenderAsync()
    {
        var token = ++_scanToken;
        var notebooks = _notebooks;

        // 进入扫描态
        LoadingPanel.Visibility = Visibility.Visible;
        OverviewScroll.Visibility = Visibility.Collapsed;
        ReportView.Visibility = Visibility.Collapsed;
        TargetList.ItemsSource = null;

        var reports = new List<NotebookImageReport>();
        foreach (var nb in notebooks)
        {
            LoadingText.Text = $"正在扫描「{nb.Name}」…";
            var report = await Task.Run(() => _maint.ScanNotebook(nb.Path, nb.Name));
            if (token != _scanToken) return; // 期间又发起了新扫描 → 丢弃本次
            reports.Add(report);
        }

        // 构建左列目标
        var targets = new List<MaintTarget>();
        var multiple = notebooks.Count > 1;
        if (multiple)
            targets.Add(new MaintTarget { Title = $"总览（{notebooks.Count} 个笔记本）", IsOverview = true });
        for (var i = 0; i < notebooks.Count; i++)
        {
            var r = reports[i];
            targets.Add(new MaintTarget
            {
                Title = notebooks[i].Name,
                Subtitle = TargetSubtitle(r),
                Notebook = notebooks[i],
                Report = r,
            });
        }

        LeftPanel.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;

        TargetList.ItemsSource = targets;
        TargetList.SelectedIndex = 0; // 总览（多个）或唯一笔记本（单个）→ 触发 OnTargetSelected 渲染
    }

    private void OnTargetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TargetList.SelectedItem is not MaintTarget target) return;
        if (target.IsOverview)
        {
            RenderOverview();
            OverviewScroll.Visibility = Visibility.Visible;
            ReportView.Visibility = Visibility.Collapsed;
        }
        else if (target.Report is { } report)
        {
            ReportView.Show(report);
            ReportView.Visibility = Visibility.Visible;
            OverviewScroll.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderOverview()
    {
        var notebookTargets = (TargetList.ItemsSource as IEnumerable<MaintTarget>)?
            .Where(t => !t.IsOverview && t.Report is not null).ToList() ?? [];
        var reports = notebookTargets.Select(t => t.Report!).ToList();

        OverviewTitle.Text = $"总览（{reports.Count} 个笔记本）";

        long localBytes = 0;
        var localFiles = 0;
        var orphans = 0;
        long orphanBytes = 0;
        var broken = 0;
        var empties = 0;
        var embedded = 0;
        var notes = 0;
        foreach (var r in reports)
        {
            notes += r.NoteCount;
            broken += r.Broken.Count;
            empties += r.EmptyBlockCount;
            embedded += r.EmbeddedCount;
            orphans += r.Orphans.Count;
            orphanBytes += r.OrphanBytes;
            foreach (var kv in r.ByCategory)
                if (IsLocal(kv.Key)) { localFiles += kv.Value.DistinctFiles; localBytes += kv.Value.TotalBytes; }
        }

        OverviewCards.ItemsSource = new List<StatCard>
        {
            new("笔记本", reports.Count.ToString()),
            new("笔记数", notes.ToString()),
            new("本地图片", localFiles.ToString(), FormatSize(localBytes)),
            new("未引用", orphans.ToString(), FormatSize(orphanBytes)),
            new("断链引用", broken.ToString()),
            new("空块", empties.ToString()),
        };
        OverviewList.ItemsSource = notebookTargets;
    }

    // 点总览里某行 → 切到该笔记本的报告。
    private void OnOverviewRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MaintTarget target)
            TargetList.SelectedItem = target;
    }

    private void OnRescan(object sender, RoutedEventArgs e) => _ = Rescan();

    private static string TargetSubtitle(NotebookImageReport r)
    {
        var localFiles = r.ByCategory.Where(kv => IsLocal(kv.Key)).Sum(kv => kv.Value.DistinctFiles);
        var parts = new List<string> { $"{localFiles} 张图" };
        if (r.Orphans.Count > 0) parts.Add($"{r.Orphans.Count} 未引用");
        if (r.Broken.Count > 0) parts.Add($"{r.Broken.Count} 断链");
        if (r.EmptyBlockCount > 0) parts.Add($"{r.EmptyBlockCount} 空块");
        return string.Join(" · ", parts);
    }

    private static bool IsLocal(ImageCategory c) =>
        c is ImageCategory.Mica or ImageCategory.SameDir or ImageCategory.AssetsSub or ImageCategory.OtherLocal;

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
