using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Mica.IPC.Handlers;
using Windows.Foundation;
using Windows.UI;

namespace Mica;

// MainWindow partial：表格的插入对话框（交互式启动器）+ 编辑器右键菜单（均为 WinUI 原生）。
// 表格用 HTML 表格节点（见 web/htmlTable.ts）：插入对话框直接生成 <table> HTML 经 editor.insertTable 发给
// 编辑器；右键菜单经 editor.tableOp 回传 op 在真实选区上执行（增删行列/合并拆分/对齐/宽度/删表）。
public sealed partial class MainWindow
{
    // 行/列上限（够用即可，避免巨表）：行 ≤ 50、列 ≤ 20。
    private const int TableMaxRows = 50;
    private const int TableMaxCols = 20;

    // 插入对话框「记住上一次的选项」：进程内静态，重开对话框沿用上次设置（重置按钮恢复默认）。
    private static int _tblRows = 3, _tblCols = 3, _tblRadius = 8, _tblBorder = 1;
    private static bool _tblHeader = true;
    private static int _tblCellAlign = 0; // 0 左 1 中 2 右
    private static int _tblWidth = 0;     // 0 最大宽度(full) 1 自适应(auto)
    private static int _tblTableAlign = 1; // 0 左 1 中 2 右（仅 auto 生效）
    private static int _tblPreset = 0;    // 0 普通 1 三线 2 全框 3 无框 4 斑马 5 彩虹

    // ===== 插入对话框（交互式启动器，单向：确定后按当前预览生成一次 <table> HTML 发给编辑器）=====
    //
    // 预览是「真实的表格预览」：可 +行/+列（行加在最下、列加在最右）、可点选相邻单元格后合并/拆分。
    // 不编辑文本——它只是个初始化器；插入后编辑器内的表格与此预览不再有任何关联。
    // 输出 = 直接拼 `<table>` HTML（含 class/data-*/style + 合并的 colspan/rowspan），web 端用 parseHtmlTable 还原。

    // 菜单「插入表格」/ 快捷键 Ctrl+Shift+T → **先问 web 光标是否在表格里**（不能嵌套表格）：
    // 经 editor.checkInsertTable 让 web 检查 isInTable；不在表格才回 host.shortcut insertTable 弹启动器，
    // 在表格里则 web 端直接吞掉、什么都不做（对标 Typora，无任何提示）。
    // 快捷键路径在 web 端 keymap 里已自查（见 createEditor 的 Mod-Shift-t），故菜单走这条 IPC 即可。
    private void OnInsertTable(object sender, RoutedEventArgs e)
    {
        if (!InEditorView) return;
        _ipcRouter.SendNotification("editor.checkInsertTable", new { });
    }

    private async Task ShowInsertTableDialogAsync()
    {
        if (!InEditorView) return;

        // ---- 预览模型：网格 + 合并信息（行/列数沿用上次记住的）----
        int gRows = _tblRows, gCols = _tblCols;
        bool[,] covered = new bool[0, 0];   // 被某个合并锚点覆盖的格子（不渲染）
        int[,] rowSpan = new int[0, 0];     // 锚点格的行跨度（被覆盖格无意义）
        int[,] colSpan = new int[0, 0];     // 锚点格的列跨度
        var selected = new System.Collections.Generic.HashSet<int>(); // 选中待合并的格子（key=r*100+c）

        void ResetModel(int r, int c)
        {
            gRows = r; gCols = c;
            covered = new bool[r, c];
            rowSpan = new int[r, c];
            colSpan = new int[r, c];
            for (int i = 0; i < r; i++)
                for (int j = 0; j < c; j++) { rowSpan[i, j] = 1; colSpan[i, j] = 1; }
            selected.Clear();
        }
        ResetModel(gRows, gCols);

        // ---- 限额提醒：达上下限时在预览上方给一行黄色提示，下一次有效操作时清除 ----
        var statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = ResBrush("SystemFillColorCautionBrush", 255),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        void ShowLimit(string msg) { statusText.Text = msg; statusText.Visibility = Visibility.Visible; }
        void ClearLimit() { statusText.Visibility = Visibility.Collapsed; }

        // ---- 数字输入：NumberBox + Compact（与设置页同款，旋钮只在编辑/悬停时弹出，平时只显数字）----
        // 上下限交给 NumberBox（Min/Max 自动夹取 + 失焦覆盖非法输入）；到达边界时在状态条给一句提醒。
        var rowsBox = MakeNum("行数", _tblRows, 1, TableMaxRows);
        var colsBox = MakeNum("列数", _tblCols, 1, TableMaxCols);
        var radiusBox = MakeNum("圆角", _tblRadius, 0, 24);
        var borderBox = MakeNum("线宽", _tblBorder, 0, 6);
        int RowsVal() => NumOr(rowsBox.Value, 3, 1, TableMaxRows);
        int ColsVal() => NumOr(colsBox.Value, 3, 1, TableMaxCols);

        var headerSwitch = new ToggleSwitch { Header = "首行为表头", IsOn = _tblHeader, MinWidth = 0 };

        var cellAlignCombo = MakeCombo("内容对齐", new[] { "左对齐", "居中", "右对齐" }, _tblCellAlign);
        var widthCombo = MakeCombo("整表宽度", new[] { "最大宽度", "自适应" }, _tblWidth);
        var tableAlignCombo = MakeCombo("整表对齐", new[] { "左", "居中", "右" }, _tblTableAlign);
        var presetCombo = MakeCombo("样式预设", new[] { "普通", "三线表", "全框线", "无框线", "斑马纹", "彩虹" }, _tblPreset);

        string PresetKey() => presetCombo.SelectedIndex switch { 1 => "3line", 2 => "grid", 3 => "borderless", 4 => "zebra", 5 => "rainbow", _ => "plain" };

        // 整表对齐仅在「自适应内容」下有意义（占满时铺满，对齐无效）
        void SyncTableAlignEnabled() => tableAlignCombo.IsEnabled = widthCombo.SelectedIndex == 1;

        // 三列等宽布局：控件铺满各自列（修「数字框太窄、右侧留白」）。
        static Grid Cols3(FrameworkElement a, FrameworkElement b, FrameworkElement c)
        {
            var g = new Grid { ColumnSpacing = 14 };
            for (int k = 0; k < 3; k++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(a, 0); Grid.SetColumn(b, 1); Grid.SetColumn(c, 2);
            g.Children.Add(a); g.Children.Add(b); g.Children.Add(c);
            return g;
        }

        var headerCell = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { headerSwitch } };
        var row1 = Cols3(rowsBox, colsBox, headerCell);
        var row2 = Cols3(cellAlignCombo, widthCombo, tableAlignCombo);
        var row3 = Cols3(presetCombo, radiusBox, borderBox);

        // ---- 预览区：tableFrame(整表边框/圆角/宽度对齐) → previewGrid(单元格) ----
        var previewGrid = new Grid();
        var tableFrame = new Border { Child = previewGrid, VerticalAlignment = VerticalAlignment.Center };
        // innerHost：撑到 ≥ 滚动视口高度（MinHeight 由滚动区 SizeChanged 设），表格少时把 tableFrame 垂直居中、
        // 表格多时随表格变高 → 滚动。这样背景卡片(previewBorder)可一直占满，表格仍居中（修「卡片该一开始就最大」）。
        var innerHost = new Grid();
        innerHost.Children.Add(tableFrame);
        var previewScroller = new ScrollViewer
        {
            Content = innerHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        previewScroller.SizeChanged += (_, e) => innerHost.MinHeight = e.NewSize.Height;
        var previewBorder = new Border
        {
            BorderBrush = ResBrush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = ResBrush("CardBackgroundFillColorSecondaryBrush", 16),
            Padding = new Thickness(10),
            // 背景卡片一开始就占满预览区（Stretch），表格在其中垂直居中（靠 innerHost）。
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = previewScroller,
        };

        // 颜色（尽量贴近 editor.css 的预设；都用半透明灰/彩，自动适配明暗）
        var stroke = new SolidColorBrush(Color.FromArgb(110, 128, 128, 128));     // 普通/全框线 内框线
        var strong = ResBrush("TextFillColorPrimaryBrush", 255);                  // 三线表 上下粗线
        var headerBg = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));    // 普通表头底
        var zebraBg = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));     // 斑马纹隔行
        var selBg = new SolidColorBrush(Color.FromArgb(150, 80, 150, 255));       // 选中（盖在底色之上）
        var transparent = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));        // 透明但可点击命中
        var bar = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128));        // 单元格内「文字」指示条
        var rainbowHeader = new SolidColorBrush(Color.FromArgb(120, 124, 92, 230));
        Color[] rainbowRow =
        {
            Color.FromArgb(26, 255, 99, 99), Color.FromArgb(26, 255, 175, 64), Color.FromArgb(26, 95, 200, 120),
            Color.FromArgb(26, 64, 150, 255), Color.FromArgb(26, 175, 110, 255),
        };

        void Render()
        {
            previewGrid.Children.Clear();
            previewGrid.RowDefinitions.Clear();
            previewGrid.ColumnDefinitions.Clear();

            bool full = widthCombo.SelectedIndex == 0;
            string preset = PresetKey();
            double bw = NumOr(borderBox.Value, 1, 0, 6);                    // 线宽
            int radius = (preset is "3line" or "borderless") ? 0 : NumOr(radiusBox.Value, 8, 0, 24); // 三线表/无框线无圆角
            bool hdrOn = headerSwitch.IsOn;

            // 整表边框 + 圆角 + 宽度对齐
            tableFrame.CornerRadius = new CornerRadius(radius);
            tableFrame.BorderBrush = preset == "3line" ? strong : stroke;
            tableFrame.BorderThickness = preset switch
            {
                "grid" or "plain" => new Thickness(bw),
                "3line" => new Thickness(0, Math.Max(1, bw + 1), 0, Math.Max(1, bw + 1)),
                _ => new Thickness(0),
            };
            tableFrame.HorizontalAlignment = full
                ? HorizontalAlignment.Stretch
                : tableAlignCombo.SelectedIndex switch { 1 => HorizontalAlignment.Center, 2 => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };

            for (int j = 0; j < gCols; j++)
                previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = full ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            for (int i = 0; i < gRows; i++)
                previewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var alignH = cellAlignCombo.SelectedIndex switch { 1 => HorizontalAlignment.Center, 2 => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };

            // 三线表多级表头：算「有效表头行」——row0 是表头；被「起始于表头行的单元格」纵向 rowspan 盖住的行也是
            // 表头（对标 web enforceHeaderCoverage / 顶部表头优先）。据此画：末表头行下贯穿全宽中线 + 上层组表头
            // (colspan、其下还有表头行)内缩分隔线；与编辑器实际渲染一致。
            var originR = new int[gRows, gCols];
            for (int i = 0; i < gRows; i++)
                for (int j = 0; j < gCols; j++)
                    originR[i, j] = -1;
            for (int i = 0; i < gRows; i++)
                for (int j = 0; j < gCols; j++)
                {
                    if (covered[i, j]) continue;
                    for (int di = 0; di < rowSpan[i, j]; di++)
                        for (int dj = 0; dj < colSpan[i, j]; dj++)
                            originR[i + di, j + dj] = i;
                }
            var headerRow = new bool[gRows];
            if (hdrOn && gRows > 0) headerRow[0] = true;
            for (int i = 1; i < gRows; i++)
                for (int j = 0; j < gCols; j++)
                {
                    int r = originR[i, j];
                    if (r >= 0 && r < i && headerRow[r]) { headerRow[i] = true; break; }
                }
            int lastHeaderRow = 0;
            for (int i = 0; i < gRows; i++) if (headerRow[i]) lastHeaderRow = i;

            for (int i = 0; i < gRows; i++)
            {
                for (int j = 0; j < gCols; j++)
                {
                    if (covered[i, j]) continue;
                    bool isHeader = hdrOn && i == 0;
                    int key = i * 100 + j;
                    bool isSel = selected.Contains(key);
                    int dataIdx = hdrOn ? i - 1 : i;   // 正文行序号（用于斑马/彩虹隔行）

                    // 背景：选中 > 表头 > 预设隔行 > 透明（透明也要可点击命中）
                    Brush cellBg = transparent;
                    if (isSel) cellBg = selBg;
                    else if (isHeader) cellBg = preset == "rainbow" ? rainbowHeader : (preset == "borderless" ? headerBg : (preset is "3line" ? transparent : headerBg));
                    else if (preset == "zebra" && dataIdx >= 0 && dataIdx % 2 == 1) cellBg = zebraBg;
                    else if (preset == "rainbow" && dataIdx >= 0) cellBg = new SolidColorBrush(rainbowRow[dataIdx % rainbowRow.Length]);

                    // 单元格边框：全框线/普通只画「内部」右+下线——最右列不画右、最末行不画下，
                    // 由外层 tableFrame 的四周边框收边，避免边缘与外框叠成双倍宽（修「右侧/底部线明显更粗」）。
                    bool atRight = j + colSpan[i, j] >= gCols;
                    bool atBottom = i + rowSpan[i, j] >= gRows;
                    // 三线表：本格属表头(含被表头 rowspan 盖住的子表头行)时——其底落在「末表头行」→ 画贯穿全宽中线；
                    // 落在更上层(其下还有表头行)且是组表头 → 改画内缩分隔线（overlay，不画整段底线）。
                    bool hdrCell3 = hdrOn && headerRow[i];
                    int bottomRow3 = i + rowSpan[i, j] - 1;
                    bool full3 = hdrCell3 && bottomRow3 == lastHeaderRow;
                    bool inset3 = hdrCell3 && bottomRow3 < lastHeaderRow;
                    Thickness cellBorder = preset switch
                    {
                        "grid" or "plain" => new Thickness(0, 0, atRight ? 0 : bw, atBottom ? 0 : bw),
                        "3line" => full3 ? new Thickness(0, 0, 0, Math.Max(1, bw)) : new Thickness(0),
                        _ => new Thickness(0),
                    };

                    var content = new Border
                    {
                        Height = 4, Width = 18, CornerRadius = new CornerRadius(2),
                        Background = bar, HorizontalAlignment = alignH, VerticalAlignment = VerticalAlignment.Center,
                    };
                    var cell = new Border
                    {
                        BorderBrush = preset == "3line" ? strong : stroke,
                        BorderThickness = cellBorder,
                        Background = cellBg,
                        Padding = new Thickness(8, 7, 8, 7),
                        MinWidth = 36, MinHeight = 24,
                        Child = content,
                    };
                    Grid.SetRow(cell, i);
                    Grid.SetColumn(cell, j);
                    if (rowSpan[i, j] > 1) Grid.SetRowSpan(cell, rowSpan[i, j]);
                    if (colSpan[i, j] > 1) Grid.SetColumnSpan(cell, colSpan[i, j]);
                    cell.Tapped += (_, _) =>
                    {
                        if (!selected.Add(key)) selected.Remove(key);
                        Render();
                    };
                    previewGrid.Children.Add(cell);

                    // 上层组表头的「内缩分隔线」：在同一 grid 格内叠一条底对齐、左右内缩 6px 的细线（左右留缝、不与相邻组相接）。
                    // 用 overlay 而非 BorderThickness，因为后者只能画整段、做不出内缩；底对齐使它落在该行底边、与中线同高。
                    if (inset3)
                    {
                        var insetLine = new Border
                        {
                            Height = Math.Max(1, bw),
                            Background = strong,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Margin = new Thickness(6, 0, 6, 0),
                            IsHitTestVisible = false,
                        };
                        Grid.SetRow(insetLine, i);
                        Grid.SetColumn(insetLine, j);
                        if (rowSpan[i, j] > 1) Grid.SetRowSpan(insetLine, rowSpan[i, j]);
                        if (colSpan[i, j] > 1) Grid.SetColumnSpan(insetLine, colSpan[i, j]);
                        previewGrid.Children.Add(insetLine);
                    }
                }
            }
        }

        // 到达上下限时给一句提醒（NumberBox 已自动夹取，这里只在边界值上提示）。
        void LimitNote(double v, int min, int max, string name)
        {
            if (double.IsNaN(v)) return;
            if (v >= max) ShowLimit($"{name}最多 {max}");
            else if (v <= min) ShowLimit($"{name}最少 {min}");
            else ClearLimit();
        }

        // 改行/列数：重建模型（清空合并，避免越界的脏合并）。+行/+列按钮则保留合并、只在边缘追加。
        // suppress：+行/+列 回写 NumberBox 时别再触发重建（否则清空刚保留的合并）。
        bool suppress = false;
        void OnSizeChanged()
        {
            if (suppress) return;
            LimitNote(rowsBox.Value, 1, TableMaxRows, "行数");
            LimitNote(colsBox.Value, 1, TableMaxCols, "列数");
            ResetModel(RowsVal(), ColsVal());
            Render();
        }
        rowsBox.ValueChanged += (_, _) => OnSizeChanged();
        colsBox.ValueChanged += (_, _) => OnSizeChanged();
        radiusBox.ValueChanged += (_, _) => { LimitNote(radiusBox.Value, 0, 24, "圆角"); Render(); };
        borderBox.ValueChanged += (_, _) => { LimitNote(borderBox.Value, 0, 6, "线宽"); Render(); };
        cellAlignCombo.SelectionChanged += (_, _) => Render();
        headerSwitch.Toggled += (_, _) => Render();
        widthCombo.SelectionChanged += (_, _) => { SyncTableAlignEnabled(); Render(); };
        tableAlignCombo.SelectionChanged += (_, _) => Render();
        // 三线表/无框线强制直角，圆角输入对它们无意义 → 禁用圆角框（对标用户希望「圆角对三线表灰掉」）。
        void SyncRadiusEnabled() => radiusBox.IsEnabled = PresetKey() is not ("3line" or "borderless");
        presetCombo.SelectionChanged += (_, _) => { SyncRadiusEnabled(); Render(); };
        SyncTableAlignEnabled();
        SyncRadiusEnabled();

        // 重置为默认（标题栏「重置」按钮）：所有选项回到出厂值。
        void ResetDefaults()
        {
            suppress = true; rowsBox.Value = 3; colsBox.Value = 3; suppress = false;
            ResetModel(3, 3);
            headerSwitch.IsOn = true;
            cellAlignCombo.SelectedIndex = 0;
            widthCombo.SelectedIndex = 0;
            tableAlignCombo.SelectedIndex = 1;
            presetCombo.SelectedIndex = 0;
            radiusBox.Value = 8;
            borderBox.Value = 1;
            ClearLimit();
            SyncTableAlignEnabled();
            SyncRadiusEnabled();
            Render();
        }

        // 追加一行/一列（保留已有合并）：扩大数组、新格子 span=1。同步步进器显示但不触发重建模型。
        void GrowRows()
        {
            if (gRows >= TableMaxRows) { ShowLimit($"行数最多 {TableMaxRows}"); return; }
            ClearLimit();
            var nc = new bool[gRows + 1, gCols]; var nr = new int[gRows + 1, gCols]; var ncs = new int[gRows + 1, gCols];
            for (int i = 0; i < gRows; i++) for (int j = 0; j < gCols; j++) { nc[i, j] = covered[i, j]; nr[i, j] = rowSpan[i, j]; ncs[i, j] = colSpan[i, j]; }
            for (int j = 0; j < gCols; j++) { nr[gRows, j] = 1; ncs[gRows, j] = 1; }
            covered = nc; rowSpan = nr; colSpan = ncs; gRows++;
            suppress = true; rowsBox.Value = gRows; suppress = false;
            Render();
        }
        void GrowCols()
        {
            if (gCols >= TableMaxCols) { ShowLimit($"列数最多 {TableMaxCols}"); return; }
            ClearLimit();
            var nc = new bool[gRows, gCols + 1]; var nr = new int[gRows, gCols + 1]; var ncs = new int[gRows, gCols + 1];
            for (int i = 0; i < gRows; i++) for (int j = 0; j < gCols; j++) { nc[i, j] = covered[i, j]; nr[i, j] = rowSpan[i, j]; ncs[i, j] = colSpan[i, j]; }
            for (int i = 0; i < gRows; i++) { nr[i, gCols] = 1; ncs[i, gCols] = 1; }
            covered = nc; rowSpan = nr; colSpan = ncs; gCols++;
            suppress = true; colsBox.Value = gCols; suppress = false;
            Render();
        }
        // 合并选中格的外包矩形（吸收其中已有的子合并）；拆分则还原。
        void MergeSelected()
        {
            if (selected.Count < 2) return;
            int minR = int.MaxValue, minC = int.MaxValue, maxR = -1, maxC = -1;
            foreach (var k in selected) { int r = k / 100, c = k % 100; minR = Math.Min(minR, r); minC = Math.Min(minC, c); maxR = Math.Max(maxR, r); maxC = Math.Max(maxC, c); }
            // 把包含已合并锚点的外延也算进矩形（避免切断已有合并）
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = minR; i <= maxR; i++)
                    for (int j = minC; j <= maxC; j++)
                    {
                        if (covered[i, j]) continue;
                        if (i + rowSpan[i, j] - 1 > maxR) { maxR = i + rowSpan[i, j] - 1; grew = true; }
                        if (j + colSpan[i, j] - 1 > maxC) { maxC = j + colSpan[i, j] - 1; grew = true; }
                    }
            }
            for (int i = minR; i <= maxR; i++)
                for (int j = minC; j <= maxC; j++)
                {
                    covered[i, j] = !(i == minR && j == minC);
                    rowSpan[i, j] = 1; colSpan[i, j] = 1;
                }
            rowSpan[minR, minC] = maxR - minR + 1;
            colSpan[minR, minC] = maxC - minC + 1;
            selected.Clear();
            Render();
        }
        void SplitSelected()
        {
            foreach (var k in new System.Collections.Generic.List<int>(selected))
            {
                int r = k / 100, c = k % 100;
                if (r >= gRows || c >= gCols || covered[r, c]) continue;
                int rs = rowSpan[r, c], cs = colSpan[r, c];
                for (int i = r; i < r + rs; i++)
                    for (int j = c; j < c + cs; j++) { covered[i, j] = false; rowSpan[i, j] = 1; colSpan[i, j] = 1; }
            }
            selected.Clear();
            Render();
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(MakeToolBtn("+ 行", GrowRows));
        tools.Children.Add(MakeToolBtn("+ 列", GrowCols));
        tools.Children.Add(MakeToolBtn("合并", MergeSelected));
        tools.Children.Add(MakeToolBtn("拆分", SplitSelected));
        tools.Children.Add(MakeToolBtn("清除选择", () => { selected.Clear(); Render(); }));

        Render();

        // ---- 顶部选项区（固定，不随行数变化滚动/位移）----
        var optionsPanel = new StackPanel { Spacing = 18 }; // 行间距大一点，让「描述+控件」成组、不与上一行混淆
        optionsPanel.Children.Add(row1);
        optionsPanel.Children.Add(row2);
        optionsPanel.Children.Add(row3);

        var topPanel = new StackPanel { Spacing = 10 };
        topPanel.Children.Add(optionsPanel);
        topPanel.Children.Add(new TextBlock { Text = "预览（点击单元格选中、相邻多选后「合并」；+行加在底部、+列加在右侧）", FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
        topPanel.Children.Add(tools);
        topPanel.Children.Add(statusText);

        // 自定义页脚按钮：ContentDialog 自带「插入/取消」会拉满页脚整列、过长；改成内容底部右对齐的小按钮。
        bool confirmed = false;
        var okBtn = new Button { Content = "插入", MinWidth = 96 };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var ab) && ab is Style accent) okBtn.Style = accent;
        var cancelBtn = new Button { Content = "取消", MinWidth = 96 };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(okBtn); btnRow.Children.Add(cancelBtn);

        // 固定尺寸的内容根：顶部选项(Auto) + 预览(Star，背景卡片占满、表格居中其中) + 按钮(Auto)。
        // 固定大小后加行/加列不改变弹窗高度、控件不位移（修「连点 +行 弹窗变高、鼠标下按钮从加变减」）。
        var contentRoot = new Grid { Width = 540, Height = 560, RowSpacing = 12 };
        contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(topPanel, 0); Grid.SetRow(previewBorder, 1); Grid.SetRow(btnRow, 2);
        contentRoot.Children.Add(topPanel); contentRoot.Children.Add(previewBorder); contentRoot.Children.Add(btnRow);

        // 标题栏：左「插入表格」+ 最右「重置」（恢复所有选项为默认）。定宽与内容一致，让重置靠到最右。
        var titleBar = new Grid { Width = 540 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock { Text = "插入表格", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var resetBtn = new Button { Content = "重置", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        resetBtn.Click += (_, _) => ResetDefaults();
        Grid.SetColumn(titleText, 0); Grid.SetColumn(resetBtn, 1);
        titleBar.Children.Add(titleText); titleBar.Children.Add(resetBtn);

        var dialog = new ContentDialog
        {
            Title = titleBar,
            Content = contentRoot,
            XamlRoot = Content.XamlRoot,
        };
        // 默认 ContentDialog 最大宽度偏小，会把 540 宽的内容右侧（取消按钮/表格右边）裁掉且无横向滚动。放宽到 720。
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        okBtn.Click += (_, _) => { confirmed = true; dialog.Hide(); };
        cancelBtn.Click += (_, _) => dialog.Hide();
        await dialog.ShowAsync();
        if (!confirmed) return;

        // 记住本次选项，供下次打开沿用。
        _tblRows = gRows; _tblCols = gCols; _tblHeader = headerSwitch.IsOn;
        _tblCellAlign = cellAlignCombo.SelectedIndex; _tblWidth = widthCombo.SelectedIndex;
        _tblTableAlign = tableAlignCombo.SelectedIndex; _tblPreset = presetCombo.SelectedIndex;
        _tblRadius = NumOr(radiusBox.Value, 8, 0, 24); _tblBorder = NumOr(borderBox.Value, 1, 0, 6);

        string html = BuildTableHtml(
            gRows, gCols, covered, rowSpan, colSpan,
            headerOn: headerSwitch.IsOn,
            cellAlign: cellAlignCombo.SelectedIndex switch { 1 => "center", 2 => "right", _ => "left" },
            width: widthCombo.SelectedIndex == 1 ? "auto" : "full",
            tableAlign: tableAlignCombo.SelectedIndex switch { 1 => "center", 2 => "right", _ => "left" },
            preset: PresetKey(),
            radius: NumOr(radiusBox.Value, 8, 0, 24),
            border: NumOr(borderBox.Value, 1, 0, 6));
        _ipcRouter.SendNotification("editor.insertTable", new { html });
    }

    // 按预览模型 + 样式选项拼出 `<table>` HTML（单行无空行，web 端 parseHtmlTable 还原）。
    private static string BuildTableHtml(int rows, int cols, bool[,] covered, int[,] rowSpan, int[,] colSpan,
        bool headerOn, string cellAlign, string width, string tableAlign, string preset, int radius, int border)
    {
        var sb = new System.Text.StringBuilder();
        string cls = preset == "plain" ? "mica-table" : $"mica-table mica-table-{preset}";
        bool noRadius = preset is "3line" or "borderless"; // 三线表/无框线强制直角，不写圆角
        sb.Append($"<table class=\"{cls}\"");
        if (width == "auto") sb.Append(" data-width=\"auto\"");
        if (!noRadius) sb.Append($" data-radius=\"{radius}\"");
        sb.Append($" data-border=\"{border}\"");
        if (width == "auto" && tableAlign != "left")
            sb.Append(tableAlign == "center" ? " style=\"margin-left:auto;margin-right:auto;\"" : " style=\"margin-left:auto;margin-right:0;\"");
        sb.Append("><tbody>");
        for (int i = 0; i < rows; i++)
        {
            sb.Append("<tr>");
            for (int j = 0; j < cols; j++)
            {
                if (covered[i, j]) continue;
                string tag = headerOn && i == 0 ? "th" : "td";
                sb.Append('<').Append(tag);
                if (colSpan[i, j] > 1) sb.Append($" colspan=\"{colSpan[i, j]}\"");
                if (rowSpan[i, j] > 1) sb.Append($" rowspan=\"{rowSpan[i, j]}\"");
                if (cellAlign != "left") sb.Append($" style=\"text-align:{cellAlign}\"");
                sb.Append("></").Append(tag).Append('>');
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    // ---- 小控件工厂 ----

    private static ComboBox MakeCombo(string header, string[] items, int selected)
    {
        // 左对齐 + 自适应内容宽度（不铺满列），让设置项之间留白、控件不会过宽。
        var c = new ComboBox { Header = header, MinWidth = 92, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var it in items) c.Items.Add(it);
        c.SelectedIndex = selected;
        return c;
    }

    private static Button MakeToolBtn(string text, Action onClick)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => onClick();
        return b;
    }

    // 数字输入：NumberBox + Compact 旋钮（与设置页字号同款——平时只显数字、悬停/编辑才弹 ▲▼，无清除按钮挡数字）。
    // 铺满所在列；上下限由 Min/Max 自动夹取，非法输入失焦后被覆盖。
    private static NumberBox MakeNum(string header, double value, int min, int max) => new()
    {
        Header = header,
        Value = value,
        Minimum = min,
        Maximum = max,
        SmallChange = 1,
        LargeChange = 5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        HorizontalAlignment = HorizontalAlignment.Left,
        Width = 96, // 只显两位数字，窄一点；旋钮 Compact 悬停才弹
    };

    // ===== 编辑器右键菜单（原生 MenuFlyout）=====

    // 由 web 端经 host.tableMenu 转发触发（坐标相对 WebView 视口的 CSS 像素 ≈ EditorHost 内 DIP）。
    // 改用 HTML 表格后表头不再强制在第一行（td/th 可混排），故行操作一律安全，不再按 inHeader 隐藏。
    // inHeader 仅保留作未来「表头相关项」的参考，本轮不使用。
    //
    // 菜单形态 = WinUI 官方 **CommandBarFlyout**（对标 Win11 资源管理器右键菜单）：顶部图标条只放最高频的
    // 底色/字色（各弹调色板）；其余（合并/拆分/列对齐/插入/移动/复制表格/切换表头/删除）都在下方列表。
    // 列表里「插入/移动/删除」是否收进子菜单由设置项 TableMenuStyle 决定（0 分组默认 / 1 平铺）。
    // **图标全用 FontIcon + Segoe 字形（Unicode 码位，源码写 \uXXXX 转义、不写会乱码的 PUA 字符）**——当前是占位
    // 字形，后续会统一重做（故不追求语义精确，只求尺寸一致、不再用大小不一的自绘 PathIcon）。
    private void ShowTableContextMenu(TableMenuHandler.TableMenuInfo info)
    {
        if (!InEditorView) return;
        _ = info.InHeader;

        var flyout = new CommandBarFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            AlwaysExpanded = true, // 顶部图标条 + 下方列表同时显示，不藏在「⌄」后面
        };

        // ---- 顶部图标条：只放底色/字色（点开弹调色板）----
        flyout.PrimaryCommands.Add(new AppBarButton { Label = "底色", Icon = AppIcon("\uE790"), Flyout = BuildColorFlyout("cellBg", BgSwatches, asRgba: true, "无填充") });
        flyout.PrimaryCommands.Add(new AppBarButton { Label = "字色", Icon = AppIcon("\uE70F"), Flyout = BuildColorFlyout("cellColor", TextSwatches, asRgba: false, "自动") });

        // ---- 整表级（2026-06 从浮动工具条迁来）：圆角/线宽/行列数放顶部按钮区——SecondaryCommands 里的
        // 自定义 Flyout（Slider/NumberBox 面板）会被列表弹出层遮挡、要点两次；放 PrimaryCommands 则像图片菜单一样开得顺。
        // 圆角/线宽 = 滑块实时预览（同图片，见 BuildTableSliderFlyout）；行列数 = NumberBox。
        // 「位置」原也在顶部，因顶部太挤已下移到下方列表（它是 MenuFlyout 单选、悬停即展开、无遮挡，放列表没问题）。
        flyout.PrimaryCommands.Add(new AppBarButton { Label = "圆角", Icon = AppIcon(""), Flyout = BuildTableSliderFlyout("tableRadius", "tablePreviewRadius", 0, 24, info.Radius ?? 8, "圆角 (px)") });
        flyout.PrimaryCommands.Add(new AppBarButton { Label = "线宽", Icon = AppIcon(""), Flyout = BuildTableSliderFlyout("tableBorder", "tablePreviewBorder", 0, 6, info.Border ?? 1, "线宽 (px)") });
        // 「行列数」改放下方列表（见下）——测试 ShouldConstrainToRootBounds=false 后自定义 Flyout 在 SecondaryCommands
        // 里是否还会被遮挡。顶部按钮区现只剩 底色/字色/圆角/线宽 四项。

        // ---- 下方列表（次命令）----
        var sc = flyout.SecondaryCommands;
        void SecSep() => sc.Add(new AppBarSeparator());

        // 单元格级常用项：合并/拆分/列对齐
        sc.Add(SecOp("合并单元格", "mergeCells", AppIcon("\uE73F")));
        sc.Add(SecOp("拆分单元格", "splitCell", AppIcon("\uE740")));
        sc.Add(SubBtn("列对齐", AppIcon("\uE8E3"),
            ("左对齐", "alignLeft", "\uE8E4"), ("居中对齐", "alignCenter", "\uE8E3"), ("右对齐", "alignRight", "\uE8E2")));
        SecSep();

        if (_settings.TableMenuStyle == 1)
        {
            // 平铺：插入/移动全展开为一级项
            sc.Add(SecOp("在上方插入行", "addRowBefore", AppIcon("\uE710")));
            sc.Add(SecOp("在下方插入行", "addRowAfter", AppIcon("\uE710")));
            sc.Add(SecOp("在左侧插入列", "addColBefore", AppIcon("\uE710")));
            sc.Add(SecOp("在右侧插入列", "addColAfter", AppIcon("\uE710")));
            SecSep();
            sc.Add(SecOp("上移此行", "moveRowUp", AppIcon("\uE70E")));
            sc.Add(SecOp("下移此行", "moveRowDown", AppIcon("\uE70D")));
            sc.Add(SecOp("左移此列", "moveColLeft", AppIcon("\uE76B")));
            sc.Add(SecOp("右移此列", "moveColRight", AppIcon("\uE76C")));
        }
        else
        {
            // 分组：插入/移动收进子菜单（AppBarButton.Flyout = MenuFlyout），菜单更短
            sc.Add(SubBtn("插入", AppIcon("\uE710"),
                ("在上方插入行", "addRowBefore", "\uE710"), ("在下方插入行", "addRowAfter", "\uE710"),
                ("在左侧插入列", "addColBefore", "\uE710"), ("在右侧插入列", "addColAfter", "\uE710")));
            sc.Add(SubBtn("移动", AppIcon("\uE8AB"),
                ("上移此行", "moveRowUp", "\uE70E"), ("下移此行", "moveRowDown", "\uE70D"),
                ("左移此列", "moveColLeft", "\uE76B"), ("右移此列", "moveColRight", "\uE76C")));
        }
        SecSep();
        sc.Add(SecOp("复制表格", "copyTable", AppIcon("\uE8C8")));
        sc.Add(SecOp("切换表头行", "toggleHeader", AppIcon("\uE7C1")));
        SecSep();

        // ---- 整表外观：位置 / 行列数 / 表格样式 / 排序 ----
        // 位置/表格样式是 MenuFlyout（悬停即展开、无遮挡）；行列数是自定义 Flyout（NumberBox），原放顶部因 SecondaryCommands
        // 会遮挡，现 ShouldConstrainToRootBounds=false 后挪回列表测试是否还遮。排序改普通按钮（见 BuildReorderToggle）。
        sc.Add(BuildTablePosButton(info.Pos));    // 位置：整表对齐+占满四选一（单选）
        sc.Add(new AppBarButton { Label = "行列数", Icon = AppIcon(""), Flyout = BuildTableSizeFlyout(info.Rows, info.Cols) });
        sc.Add(BuildTablePresetSub(info.Preset)); // 表格样式预设（单选）
        sc.Add(BuildReorderToggle(info.ReorderOn));

        SecSep();
        if (_settings.TableMenuStyle == 1)
        {
            sc.Add(SecOp("删除行", "deleteRow", AppIcon("\uE74D")));
            sc.Add(SecOp("删除列", "deleteCol", AppIcon("\uE74D")));
            sc.Add(SecOp("删除表格", "deleteTable", AppIcon("\uE74D")));
        }
        else
        {
            sc.Add(SubBtn("删除", AppIcon("\uE74D"),
                ("删除行", "deleteRow", "\uE74D"), ("删除列", "deleteCol", "\uE74D"), ("删除表格", "deleteTable", "\uE74D")));
        }
        // 注：整表对齐 / 样式 / 圆角 / 线宽 / 行列数已从浮动工具条迁回本菜单（浮条下线）；行/列/单元格操作在上方。

        flyout.ShowAt(EditorHost, new FlyoutShowOptions { Position = new Point(info.X, info.Y) });
    }

    // ===== 整表样式菜单项工厂（从浮动工具条迁来）=====

    // 样式预设子菜单：普通/三线表/全框线/无框线/斑马纹/彩虹。**用 RadioMenuFlyoutItem 单选**（同组互斥、只亮一个圆点；
    // ToggleMenuFlyoutItem 是各自独立的勾，会显示成「多选」语义不对）。点击发 tablePreset，value=preset 名，普通→空串即 null。
    private AppBarButton BuildTablePresetSub(string current)
    {
        var b = new AppBarButton { Label = "表格样式", Icon = AppIcon("") };
        var mf = Unbounded(new MenuFlyout());
        (string text, string value)[] presets =
        {
            ("普通", ""), ("三线表", "3line"), ("全框线", "grid"),
            ("无框线", "borderless"), ("斑马纹", "zebra"), ("彩虹", "rainbow"),
        };
        foreach (var (text, value) in presets)
        {
            var it = new RadioMenuFlyoutItem { Text = text, GroupName = "micaTablePreset", IsChecked = current == value };
            it.Click += (_, _) => SendTableOp("tablePreset", value);
            mf.Items.Add(it);
        }
        b.Flyout = mf;
        return b;
    }

    // 「位置」子菜单（下方列表）：整表对齐+占满四选一。**用 RadioMenuFlyoutItem 单选**（同组互斥、只亮一个圆点；
    // 原 ToggleMenuFlyoutItem 是独立勾、表现为「多选」语义不对）。pos: left/center/right/full。点击发 tablePos*。
    private AppBarButton BuildTablePosButton(string pos)
    {
        var b = new AppBarButton { Label = "位置", Icon = AppIcon("") };
        var mf = Unbounded(new MenuFlyout());
        (string text, string op, string match)[] items =
        {
            ("靠左（自适应）", "tablePosLeft", "left"),
            ("居中（自适应）", "tablePosCenter", "center"),
            ("靠右（自适应）", "tablePosRight", "right"),
            ("占满编辑宽度", "tablePosFull", "full"),
        };
        foreach (var (text, op, match) in items)
        {
            var it = new RadioMenuFlyoutItem { Text = text, GroupName = "micaTablePos", IsChecked = pos == match };
            it.Click += (_, _) => SendTableOp(op);
            mf.Items.Add(it);
        }
        b.Flyout = mf;
        return b;
    }

    // 圆角/线宽子 Flyout（顶部按钮区）：滑块 + 实时预览（同图片）。拖动发 previewOp（只改 <table> 内联 style、不提交，
    // 见 htmlTable runTableOp 的 tablePreview*）、停手/关闭发 commitOp 提交。复用图片那套 WireLiveSlider（同一 partial 类）。
    private Flyout BuildTableSliderFlyout(string commitOp, string previewOp, int min, int max, int current, string header)
    {
        var flyout = Unbounded(new Flyout());
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(8), Width = 200 };
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = 1, Value = Math.Clamp(current, min, max), Header = header };
        WireLiveSlider(slider, flyout,
            v => SendTableOp(previewOp, v.ToString()),
            v => SendTableOp(commitOp, v.ToString()),
            null);
        panel.Children.Add(slider);
        flyout.Content = panel;
        return flyout;
    }

    // 「行/列排序」开关（下方列表）：切换行/列拖动重排把手。状态在 web（reorder 控制器），由菜单上下文回填。
    // **不能用 AppBarToggleButton**——它会让整个 SecondaryCommands 列表预留「勾选指示列」、所有项左移出现一条空白带
    // （图片菜单没 toggle 故无空白）。改用普通 AppBarButton，靠「开启时图标变强调色 + 标签末尾加对勾」表达当前态。
    // 点击发 tableReorderToggle（tableSetup 路由到 toggleTableReorder）。
    private AppBarButton BuildReorderToggle(bool on)
    {
        var icon = AppIcon(""); // Sort 图标
        if (on) icon.Foreground = ResBrush("AccentFillColorDefaultBrush"); // 开启 → 强调色
        var b = new AppBarButton { Label = on ? "行/列排序  ✓" : "行/列排序", Icon = icon };
        b.Click += (_, _) => SendTableOp("tableReorderToggle");
        return b;
    }

    // 行列数子 Flyout：两个 NumberBox（行数 / 列数），各自改值发 tableRows / tableCols（从右/下边缘增减，见 web）。
    private Flyout BuildTableSizeFlyout(int rows, int cols)
    {
        var flyout = Unbounded(new Flyout());
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(8), Width = 180 };
        panel.Children.Add(new TextBlock { Text = "行数" });
        panel.Children.Add(MakeTableNumberBox("tableRows", 1, TableMaxRows, rows < 1 ? 1 : rows));
        panel.Children.Add(new TextBlock { Text = "列数" });
        panel.Children.Add(MakeTableNumberBox("tableCols", 1, TableMaxCols, cols < 1 ? 1 : cols));
        flyout.Content = panel;
        return flyout;
    }

    // 表格菜单专用 NumberBox：内联 +/- 步进、夹取范围；ValueChanged 发 op（首次设初值不发——initializing 守卫）。
    private NumberBox MakeTableNumberBox(string op, int min, int max, int current)
    {
        var nb = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = 1,
            LargeChange = 1,
            Value = Math.Clamp(current, min, max),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        bool initializing = true;
        nb.ValueChanged += (_, e) =>
        {
            if (initializing) return;
            if (double.IsNaN(e.NewValue)) return;
            SendTableOp(op, ((int)Math.Round(e.NewValue)).ToString());
        };
        initializing = false;
        return nb;
    }

    // ===== CommandBarFlyout 项工厂 =====

    // 下方列表的一级命令项（带可选图标）。
    private AppBarButton SecOp(string label, string op, IconElement? icon = null)
    {
        var b = new AppBarButton { Label = label, Icon = icon };
        b.Click += (_, _) => SendTableOp(op);
        return b;
    }

    // 下方列表的「子菜单」项（AppBarButton.Flyout = MenuFlyout，对标分组模式）。
    private AppBarButton SubBtn(string label, IconElement? icon, params (string text, string op, string glyph)[] items)
    {
        var b = new AppBarButton { Label = label, Icon = icon };
        var mf = Unbounded(new MenuFlyout());
        foreach (var (text, op, glyph) in items) mf.Items.Add(MakeOpItem(text, op, glyph));
        b.Flyout = mf;
        return b;
    }

    // 调色板浮出：清除项 + 5 列色块网格；点色块/清除发 editor.tableOp{op,value} 并关闭。
    // asRgba=true（底色）→ value 写 rgba(...)（半透明，明暗自适应）；false（字色）→ value 写 #RRGGBB。
    private Flyout BuildColorFlyout(string op, Color[] colors, bool asRgba, string clearLabel)
    {
        var flyout = Unbounded(new Flyout());
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(4), Width = 196 };

        var clearBtn = new Button { Content = clearLabel, HorizontalAlignment = HorizontalAlignment.Stretch };
        clearBtn.Click += (_, _) => { SendTableOp(op, ""); flyout.Hide(); };
        panel.Children.Add(clearBtn);

        const int cols = 5;
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < colors.Length; i++)
        {
            int r = i / cols, c = i % cols;
            while (grid.RowDefinitions.Count <= r) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var col = colors[i];
            string value = CssColor(col, asRgba);
            var sw = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(col),
                BorderBrush = ResBrush("ControlStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            };
            ToolTipService.SetToolTip(sw, value);
            sw.Click += (_, _) => { SendTableOp(op, value); flyout.Hide(); };
            Grid.SetRow(sw, r); Grid.SetColumn(sw, c);
            grid.Children.Add(sw);
        }
        panel.Children.Add(grid);
        flyout.Content = panel;
        return flyout;
    }

    // 发表格 op（颜色类带 value，空串=清除）。
    private void SendTableOp(string op, string? value = null)
    {
        if (value is null) _ipcRouter.SendNotification("editor.tableOp", new { op });
        else _ipcRouter.SendNotification("editor.tableOp", new { op, value });
    }

    // 菜单图标统一工厂：FontIcon（Segoe MDL2 Assets / Win11 回退 Segoe Fluent Icons），默认尺寸保证大小一致。
    private static FontIcon AppIcon(string glyph) => new() { Glyph = glyph };

    // Color → CSS 颜色串。底色用 rgba（半透明、明暗自适应）；字色用 #RRGGBB。
    private static string CssColor(Color c, bool rgba) => rgba
        ? $"rgba({c.R},{c.G},{c.B},{(c.A / 255.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})"
        : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // 底色色板（半透明 ~0x3A，叠在单元格上明暗都柔和可读）。
    private static readonly Color[] BgSwatches =
    {
        Color.FromArgb(0x3A, 0xFF, 0x56, 0x56), Color.FromArgb(0x3A, 0xFF, 0xA5, 0x40),
        Color.FromArgb(0x3A, 0xFF, 0xD6, 0x40), Color.FromArgb(0x3A, 0x5F, 0xC8, 0x78),
        Color.FromArgb(0x3A, 0x40, 0xC8, 0xC8), Color.FromArgb(0x3A, 0x40, 0x96, 0xFF),
        Color.FromArgb(0x3A, 0xAF, 0x6E, 0xFF), Color.FromArgb(0x3A, 0xFF, 0x78, 0xB4),
        Color.FromArgb(0x33, 0x80, 0x80, 0x80), Color.FromArgb(0x3A, 0x9E, 0x7B, 0x53),
    };
    // 字色色板（实色）。
    private static readonly Color[] TextSwatches =
    {
        Color.FromArgb(0xFF, 0xE5, 0x39, 0x35), Color.FromArgb(0xFF, 0xFB, 0x8C, 0x00),
        Color.FromArgb(0xFF, 0xC9, 0xA2, 0x27), Color.FromArgb(0xFF, 0x43, 0xA0, 0x47),
        Color.FromArgb(0xFF, 0x00, 0x89, 0x7B), Color.FromArgb(0xFF, 0x1E, 0x88, 0xE5),
        Color.FromArgb(0xFF, 0x8E, 0x24, 0xAA), Color.FromArgb(0xFF, 0xD8, 0x1B, 0x60),
        Color.FromArgb(0xFF, 0x75, 0x75, 0x75), Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
    };

    // Segoe 字形图标（用于子菜单 MenuFlyoutItem）。空/缺省 → 返回 null（不显示图标）。
    private static FontIcon? Glyph(string? glyph) =>
        string.IsNullOrEmpty(glyph) ? null : new FontIcon { Glyph = glyph, FontSize = 15 };

    private MenuFlyoutItem MakeOpItem(string text, string op, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (Glyph(glyph) is { } icon) item.Icon = icon;
        item.Click += (_, _) => _ipcRouter.SendNotification("editor.tableOp", new { op });
        return item;
    }

    // ===== 工具 =====

    // 读 NumberBox 值并夹取到范围（空输入会是 NaN，回退默认值）。
    private static int NumOr(double value, int fallback, int min, int max)
    {
        if (double.IsNaN(value)) return fallback;
        int v = (int)Math.Round(value);
        return Math.Clamp(v, min, max);
    }

    // 取主题资源画刷，缺失时回退到半透明灰，避免某些 key 不存在导致崩溃。
    private static Brush ResBrush(string key, byte fallbackAlpha = 255)
    {
        if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b) return b;
        return new SolidColorBrush(Color.FromArgb(fallbackAlpha, 128, 128, 128));
    }
}
