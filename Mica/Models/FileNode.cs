using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mica.Models;

public sealed class FileNode : INotifyPropertyChanged
{
    public required string RelPath { get; set; }
    public required bool IsDirectory { get; init; }
    // ObservableCollection so the TreeView updates this branch incrementally when we
    // add/remove a child — no full rebuild, so folders keep their expand state and
    // there's no flicker.
    public ObservableCollection<FileNode> Children { get; init; } = [];

    public string IconGlyph => IsDirectory ? "\xE8B7" : "\xE8A5";

    // --- 扁平化树渲染（ItemsRepeater）---
    // 树重写后用「扁平模型」：可见行是一个扁平 ObservableCollection，每行带 Depth（层级），
    // 由窗口侧 RebuildVisibleRows / ExpandRow / CollapseRow 维护。下面几个派生属性供行模板 x:Bind。

    // 层级深度（根=0），决定内容左缩进。展开/折叠时节点对象复用，故 Depth 稳定。
    private int _depth;
    public int Depth
    {
        get => _depth;
        set { if (_depth == value) return; _depth = value; OnPropertyChanged(nameof(Depth)); OnPropertyChanged(nameof(ContentMargin)); }
    }

    // 每层缩进 16px。竖条/hover 背景仍贴行最左、不随缩进移动（导航栏风格）。
    public Thickness ContentMargin => new(Depth * 16, 0, 0, 0);

    // 只有文件夹显示展开箭头；文件用等宽占位列对齐，不渲染箭头。
    public Visibility ChevronVisibility => IsDirectory ? Visibility.Visible : Visibility.Collapsed;

    // 箭头朝向：折叠→指右(0°)、展开→指下(90°)。在 IsExpanded 改变时一并通知。
    public double ChevronAngle => _isExpanded ? 90 : 0;

    private string _name = "";
    public required string Name
    {
        get => _name;
        set { if (_name == value) { return; } _name = value; OnPropertyChanged(nameof(Name)); }
    }

    // "Current file" highlight. TwoWay-bound to the TreeViewItem's IsSelected so the row
    // uses the NATIVE selection chrome (rounded bg + left accent bar = the nav-rail look).
    // Backing the selection with a model property (rather than TreeView.SelectedItem) keeps
    // the highlight when an ancestor folder collapses and its container is recycled. Set by
    // the page when a file opens / a tab gains focus; also follows the user's own clicks.
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ActiveVisibility));
        }
    }

    // Drives the custom "current file" indicator (left accent bar + subtle rounded bg) in
    // the row template. The indicator is fully decoupled from native TreeView selection
    // (SelectionMode="None"): it only ever marks the open file, never whatever was clicked.
    public Visibility ActiveVisibility => _isActive ? Visibility.Visible : Visibility.Collapsed;

    // Multi-selection highlight (P2), independent of IsActive (the open file). A node can be
    // both selected and active. Drives a selection background Border in the row template.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(SelectionVisibility));
        }
    }

    public Visibility SelectionVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

    // 鼠标悬停高亮（驱动行模板里一个 {ThemeResource} 底色 Border）。
    // ★为什么不在代码里设 Grid.Background：原先 hover 画刷从 Application.Current.Resources 取，
    //   那是按「应用级」主题解析的，不跟随我们用的「元素级」RequestedTheme（明暗切换走它）——
    //   切到与系统相反的主题时画刷就用错了色。改成模型状态 + XAML {ThemeResource} 后，颜色随元素主题走，暗色才正确。
    private bool _isHovered;
    public bool IsHovered
    {
        get => _isHovered;
        set { if (_isHovered == value) return; _isHovered = value; OnPropertyChanged(nameof(IsHovered)); OnPropertyChanged(nameof(HoverVisibility)); }
    }

    public Visibility HoverVisibility => _isHovered ? Visibility.Visible : Visibility.Collapsed;

    // 多选模式：开启时每行左侧显示只读勾选框（让「多选模式」与普通 Ctrl+点击有可见区别）。
    // 由窗口 OnToggleMultiSelect 统一对全树节点赋值；勾选框 IsHitTestVisible=False，点整行即切换选中。
    private bool _showCheckBox;
    public bool ShowCheckBox
    {
        get => _showCheckBox;
        set { if (_showCheckBox == value) return; _showCheckBox = value; OnPropertyChanged(nameof(ShowCheckBox)); OnPropertyChanged(nameof(CheckBoxVisibility)); }
    }

    public Visibility CheckBoxVisibility => _showCheckBox ? Visibility.Visible : Visibility.Collapsed;

    // 拖拽落点高亮（P3）：拖拽悬停在这一行（文件夹=丢进它，文件=丢进它所在目录）时点亮，
    // 与选中/打开态独立。驱动行模板里一个描边 Border。拖拽结束/离开行即清除。
    private bool _isDropTarget;
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value) return;
            _isDropTarget = value;
            OnPropertyChanged(nameof(IsDropTarget));
            OnPropertyChanged(nameof(DropTargetVisibility));
        }
    }

    public Visibility DropTargetVisibility => _isDropTarget ? Visibility.Visible : Visibility.Collapsed;

    // 选中行＝文字/图标染强调色（对标用户最初的效果，不加粗、不变大）。
    // ★ 染色不在代码里取画刷：之前用 Application.Current.Resources 取的是「应用级」主题画刷，
    //   不跟随元素级 RequestedTheme，切暗色时取错色（树文字看不清）。
    //   现在改由行模板「叠一层强调色副本（Visibility=SelectionVisibility）」实现，
    //   颜色全用 XAML {ThemeResource AccentTextFillColorPrimaryBrush}，跟随元素主题、暗色正确。
    //   所以这里没有任何颜色/字重派生属性——纯靠 IsSelected → SelectionVisibility 驱动叠层显隐。

    // Folder expand/collapse state, two-way bound to TreeViewItem.IsExpanded. Lives on
    // the model so create/delete/rename (which mutate the bound collections in place)
    // never disturb it — folders keep their expand state across edits.
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) { return; } _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); OnPropertyChanged(nameof(ChevronAngle)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
