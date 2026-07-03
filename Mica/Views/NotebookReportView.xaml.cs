using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Mica.Models;
using Mica.Services;

namespace Mica.Views;

// 报告视图里几个列表/卡片的行 VM（x:Bind 需 public）。纯展示数据，不含画刷（避免主题坑）。
public sealed record StatCard(string Label, string Value, string? Sub = null)
{
    public Visibility HasSub => string.IsNullOrEmpty(Sub) ? Visibility.Collapsed : Visibility.Visible;
}
public sealed record CategoryRow(string Label, string CountText, string SizeText, double Percent);

// 未引用图片行。**行内不放缩略图**（上百张同时解码会卡）——预览改为点「预览」按钮时按需解码（见 OnPreviewOrphan）。
public sealed record OrphanRow(string RelPath, string SizeText, string AbsPath);

// 待清理项行（断链 + 各类空块统一）。NoteAbs+RevealKind+RevealIndex 用于「定位」精确滚动；
// FullMatch 用于「清理」从 .md 删掉整段原文。Detail 仅断链有（源 src），空块为空。
public sealed record IssueRow(string KindLabel, string Note, string Detail,
    string NoteAbs, string FullMatch, string RevealKind, int RevealIndex)
{
    public Visibility HasDetail => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
}

// 单个笔记本的图片体检报告视图（只读 + 清理/定位）。被 MaintenancePage 主从右侧复用。
// 本控件只负责渲染 + 行内即时动作（在资源管理器显示）；要跨页的（清理删盘 / 打开笔记）经事件交回 MainWindow。
public sealed partial class NotebookReportView : UserControl
{
    private NotebookImageReport? _report;

    public NotebookImageReport? Report => _report;

    public event Action<NotebookImageReport>? CleanOrphansRequested;       // 清理全部未引用图片
    public event Action<NotebookImageReport>? CleanAllIssuesRequested;     // 清理全部 Markdown 问题
    public event Action<NotebookImageReport>? MigrateRequested;            // 一键迁移到当前存储模式
    public event Action<(string noteAbs, string revealKind, int revealIndex)>? LocateRequested; // 打开笔记并滚到原处
    public event Action<(string noteAbs, string fullMatch, string kindLabel)>? CleanRefRequested; // 删除某条问题（删 FullMatch）

    public NotebookReportView()
    {
        InitializeComponent();
    }

    private static readonly (ImageCategory cat, string label)[] CategoryOrder =
    [
        (ImageCategory.Mica, ".mica 集中"),
        (ImageCategory.SameDir, "同级目录"),
        (ImageCategory.AssetsSub, "assets 子夹"),
        (ImageCategory.NoteAssets, "独立 .assets"),
        (ImageCategory.OtherLocal, "其它本地"),
        (ImageCategory.Embedded, "内嵌 base64"),
        (ImageCategory.External, "外部链接"),
    ];

    public void Show(NotebookImageReport report)
    {
        _report = report;

        TitleText.Text = string.IsNullOrEmpty(report.NotebookName) ? "笔记本" : report.NotebookName;
        PathText.Text = report.Root;

        // --- 概览统计卡 ---
        long localBytes = 0;
        var localFiles = 0;
        foreach (var kv in report.ByCategory)
            if (IsLocal(kv.Key)) { localFiles += kv.Value.DistinctFiles; localBytes += kv.Value.TotalBytes; }

        StatCards.ItemsSource = new List<StatCard>
        {
            new("笔记数", report.NoteCount.ToString()),
            new("本地图片", localFiles.ToString(), FormatSize(localBytes)),
            new("未引用", report.Orphans.Count.ToString(), FormatSize(report.OrphanBytes)),
            new("断链引用", report.Broken.Count.ToString()),
            new("空块", report.EmptyBlockCount.ToString()),
            new("内嵌图片", report.EmbeddedCount.ToString()),
        };

        // --- 存储分布 ---
        var totalRefs = report.ByCategory.Values.Sum(s => s.RefCount);
        var rows = new List<CategoryRow>();
        foreach (var (cat, label) in CategoryOrder)
        {
            if (!report.ByCategory.TryGetValue(cat, out var stat) || stat.RefCount == 0) continue;
            var pct = totalRefs > 0 ? stat.RefCount * 100.0 / totalRefs : 0;
            var sizeText = IsLocal(cat) && stat.TotalBytes > 0 ? FormatSize(stat.TotalBytes) : "";
            rows.Add(new CategoryRow(label, stat.RefCount.ToString(), sizeText, pct));
        }
        CategoryList.ItemsSource = rows;
        NoImagesHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // --- 一键迁移到当前存储模式 ---
        var mode = App.Services.GetRequiredService<SettingsService>().ImageStorageMode;
        var isFileMode = mode is >= 2 and <= 5;
        MigrateRow.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MigrateButton.Content = $"统一存储到「{ModeName(mode)}」";
        MigrateButton.IsEnabled = isFileMode;
        MigrateHint.Text = isFileMode
            ? $"把本笔记本所有图片搬到「{ModeName(mode)}」并改写引用；已在该位置的跳过。"
            : "当前存储设置为「不复制 / 内嵌」，没有统一的落盘位置，无法迁移。请先在设置里改为某种文件夹模式。";

        // --- 笔记问题（断链 + 空块），可折叠 ---
        var issues = report.Issues.Select(ToIssueRow).ToList();
        if (issues.Count > 0)
        {
            IssueList.ItemsSource = issues;
            IssueSummary.Text = $"{issues.Count} 项";
            IssueExpander.Visibility = Visibility.Visible;
            IssueExpander.IsExpanded = true; // 问题通常很少，默认展开
        }
        else
        {
            IssueList.ItemsSource = null;
            IssueExpander.Visibility = Visibility.Collapsed;
        }

        // --- 未引用图片（可折叠；很多时默认收起，避免一直往下翻） ---
        var orphans = report.Orphans
            .OrderByDescending(o => o.SizeBytes)
            .Select(o => new OrphanRow(o.RelFromRoot, FormatSize(o.SizeBytes), o.AbsPath))
            .ToList();
        OrphanList.ItemsSource = orphans;
        if (orphans.Count > 0)
        {
            OrphanSummary.Text = $"{orphans.Count} 个 · 共 {FormatSize(report.OrphanBytes)}";
            CleanButton.IsEnabled = true;
            OrphanExpander.IsExpanded = orphans.Count <= 24; // 条目多时默认收起
        }
        else
        {
            OrphanSummary.Text = "无 · 干净";
            CleanButton.IsEnabled = false;
            OrphanExpander.IsExpanded = false;
        }
    }

    // 未引用图片预览：点行内「预览」按钮才解码一张、弹 Flyout（避免列表常驻缩略图，快速滚动不再闪/卡）。
    private void OnPreviewOrphan(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OrphanRow row } fe) return;
        BitmapImage src;
        try { src = new BitmapImage(new Uri(row.AbsPath)) { DecodePixelWidth = 480 }; }
        catch { return; }
        var flyout = new Flyout
        {
            Content = new Image { Source = src, Stretch = Stretch.Uniform, MaxWidth = 380, MaxHeight = 380 },
            // 允许超出窗口边界显示（对标顶部笔记本切换的预览卡，不被窗口裁掉/挤小）。
            ShouldConstrainToRootBounds = false,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Left,
        };
        flyout.ShowAt(fe);
    }

    private static IssueRow ToIssueRow(NoteIssue issue)
    {
        var label = issue.Kind switch
        {
            NoteIssueKind.BrokenRef => "断链",
            NoteIssueKind.EmptyCode => "空代码块",
            NoteIssueKind.EmptyMath => "空公式块",
            NoteIssueKind.EmptyTable => "空表格",
            _ => "空图册",
        };
        // 各行只显示「类型徽章 + 笔记名」，统一不显示副文本（断链原本显示的 src 文本用户嫌多余，去掉）。
        return new IssueRow(label, issue.NoteRelPath, "",
            issue.NoteAbsPath, issue.FullMatch, issue.RevealKind, issue.RevealIndex);
    }

    private void OnCleanOrphans(object sender, RoutedEventArgs e)
    {
        if (_report is { Orphans.Count: > 0 }) CleanOrphansRequested?.Invoke(_report);
    }

    private void OnRevealOrphan(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OrphanRow row) RevealInExplorer(row.AbsPath);
    }

    private void OnLocateIssue(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IssueRow row && row.NoteAbs.Length > 0)
            LocateRequested?.Invoke((row.NoteAbs, row.RevealKind, row.RevealIndex));
    }

    private void OnCleanIssue(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IssueRow row && row.FullMatch.Length > 0)
            CleanRefRequested?.Invoke((row.NoteAbs, row.FullMatch, row.KindLabel));
    }

    private void OnCleanAllIssues(object sender, RoutedEventArgs e)
    {
        if (_report is { Issues.Count: > 0 }) CleanAllIssuesRequested?.Invoke(_report);
    }

    private void OnMigrate(object sender, RoutedEventArgs e)
    {
        if (_report is not null) MigrateRequested?.Invoke(_report);
    }

    // 存储模式中文名（与设置页下拉一致）。
    private static string ModeName(int mode) => mode switch
    {
        0 => "不复制",
        1 => "内嵌 base64",
        2 => "与笔记同级目录",
        3 => "同级 assets 文件夹",
        5 => "独立 .assets 文件夹",
        _ => ".mica 统一管理",
    };

    // 在资源管理器中定位并选中文件（纯 OS 动作，不涉及跨页，直接在视图里做）。
    private static void RevealInExplorer(string abs)
    {
        try
        {
            if (File.Exists(abs))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{abs}\"") { UseShellExecute = true });
        }
        catch { /* 忽略 */ }
    }

    private static bool IsLocal(ImageCategory c) =>
        c is ImageCategory.Mica or ImageCategory.SameDir or ImageCategory.AssetsSub
            or ImageCategory.NoteAssets or ImageCategory.OtherLocal;

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
