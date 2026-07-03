using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mica.Services;

namespace Mica.Views;

// 独立设置页（2026-06 从 MainWindow partial 拆出）。即时保存：每个控件改动直接写 SettingsService
// 属性（其 setter 会落盘）。_loadingSettings 守卫在填充控件时抑制 change 事件。
// 跨页副作用（主题切换 / 标签关闭按钮 / 编辑器配置推送 / 选目录需窗口句柄）本控件自己做不了，
// 通过下面的事件 + 回调交回 MainWindow。
public sealed partial class SettingsPage : UserControl
{
    private readonly SettingsService _settings;
    private bool _loadingSettings;

    // 编辑器字体：下拉序号 → CSS font-family 字符串。
    private static readonly string[] FontCssValues =
    [
        "'Segoe UI Variable', system-ui, sans-serif",
        "'Microsoft YaHei', sans-serif",
        "'SimSun', serif",
        "'Cascadia Code', Consolas, monospace",
    ];

    // 交回 MainWindow 处理的跨页副作用：
    public event Action<string>? ThemeChangeRequested;  // 主题（System/Light/Dark）→ ApplyThemePreference
    public event Action? TabCloseModeChanged;            // 标签关闭按钮模式变了 → ApplyTabCloseButton
    public event Action? EditorConfigChanged;            // 字体/字号/行宽/自动保存变了 → PushEditorConfig
    public event Action? ZoomConfigChanged;              // Ctrl+滚轮缩放开关变了 → PushZoomConfig
    public Func<Task<string?>>? PickFolderAsync;         // 选文件夹（需窗口句柄，由 MainWindow 提供）

    public SettingsPage()
    {
        // SettingsService 是 DI 单例，和 MainWindow 用的是同一个实例。
        _settings = App.Services.GetRequiredService<SettingsService>();
        InitializeComponent();
    }

    // 用保存的状态填充控件，由 MainWindow 在显示设置页时调用（themePref 由 MainWindow 维护）。
    public void LoadSettings(string themePref)
    {
        _loadingSettings = true;
        SettingsThemeCombo.SelectedIndex = themePref switch { "Light" => 1, "Dark" => 2, _ => 0 };
        var fontIdx = Array.IndexOf(FontCssValues, _settings.EditorFontFamily);
        SettingsFontCombo.SelectedIndex = fontIdx >= 0 ? fontIdx : 0;
        SettingsFontSizeBox.Value = _settings.EditorFontSize;
        SettingsLineWidthCombo.SelectedIndex = LineWidthToIndex(_settings.EditorLineWidth);
        SettingsLineNumbersToggle.IsOn = _settings.EditorShowLineNumbers;
        SettingsFirstLineIndentToggle.IsOn = _settings.EditorFirstLineIndent;
        SettingsImageStorageCombo.SelectedIndex = _settings.ImageStorageMode is >= 0 and <= 5 ? _settings.ImageStorageMode : 4;
        SettingsImageDeleteConfirmToggle.IsOn = _settings.EditorImageDeleteConfirm;
        SettingsZoomWheelToggle.IsOn = _settings.EditorZoomWheelEnabled;
        SettingsTableMenuStyleCombo.SelectedIndex = _settings.TableMenuStyle;
        SettingsTabCloseCombo.SelectedIndex = _settings.TabCloseButtonMode;
        SettingsOpenAfterCreateToggle.IsOn = _settings.OpenNoteAfterCreate;
        SettingsOpenTriggerCombo.SelectedIndex = _settings.OpenFileTrigger;
        SettingsFileDeleteCombo.SelectedIndex = _settings.FileDeleteMode;
        SettingsNewNotebookEntryCombo.SelectedIndex = _settings.NewNotebookEntry;
        SettingsNotebookDeleteCombo.SelectedIndex = _settings.NotebookDeleteMode;
        SettingsSubfolderToggle.IsOn = _settings.CreateInNewSubfolder;
        SettingsDefaultParentBox.Text = _settings.DefaultParentDir ?? "";
        SettingsRestoreToggle.IsOn = _settings.RestoreLastNotebook;
        SettingsAutoSaveCombo.SelectedIndex = AutoSaveToIndex(_settings.AutoSaveDebounceMs);
        _loadingSettings = false;
    }

    private static int LineWidthToIndex(int px) => px switch { <= 720 => 0, >= 900 => 2, _ => 1 };
    private static int IndexToLineWidth(int idx) => idx switch { 0 => 680, 2 => 960, _ => 800 };
    private static int AutoSaveToIndex(int ms) => ms switch { <= 300 => 0, <= 500 => 1, <= 1000 => 2, _ => 3 };
    private static int IndexToAutoSave(int idx) => idx switch { 0 => 300, 2 => 1000, 3 => 2000, _ => 500 };

    private void SettingsTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        ThemeChangeRequested?.Invoke(SettingsThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" });
    }

    private void SettingsFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var i = SettingsFontCombo.SelectedIndex;
        if (i < 0 || i >= FontCssValues.Length) return;
        _settings.EditorFontFamily = FontCssValues[i];
        EditorConfigChanged?.Invoke();
    }

    private void SettingsFontSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingSettings || double.IsNaN(args.NewValue)) return;
        _settings.EditorFontSize = (int)args.NewValue;
        EditorConfigChanged?.Invoke();
    }

    private void SettingsLineWidth_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.EditorLineWidth = IndexToLineWidth(SettingsLineWidthCombo.SelectedIndex);
        EditorConfigChanged?.Invoke();
    }

    private void SettingsZoomWheel_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.EditorZoomWheelEnabled = SettingsZoomWheelToggle.IsOn;
        ZoomConfigChanged?.Invoke();
    }

    private void SettingsLineNumbers_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.EditorShowLineNumbers = SettingsLineNumbersToggle.IsOn;
        EditorConfigChanged?.Invoke();
    }

    private void SettingsFirstLineIndent_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.EditorFirstLineIndent = SettingsFirstLineIndentToggle.IsOn;
        EditorConfigChanged?.Invoke();
    }

    // 图片存储位置：下拉序号直接 == ImageStorageMode（0 不复制 / 1 base64 / 2 同级 / 3 共享assets / 4 .mica / 5 每篇独立 .assets）。
    // 宿主在 image.save 落盘时直读该字段，无需推 editor.config。
    private void SettingsImageStorage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var i = SettingsImageStorageCombo.SelectedIndex;
        _settings.ImageStorageMode = i is >= 0 and <= 5 ? i : 4;
    }

    private void SettingsImageDeleteConfirm_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.EditorImageDeleteConfirm = SettingsImageDeleteConfirmToggle.IsOn;
        EditorConfigChanged?.Invoke();
    }

    private void SettingsTabClose_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var i = SettingsTabCloseCombo.SelectedIndex;
        _settings.TabCloseButtonMode = i < 0 ? 0 : i;
        TabCloseModeChanged?.Invoke();
    }

    private void SettingsOpenAfterCreate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.OpenNoteAfterCreate = SettingsOpenAfterCreateToggle.IsOn;
    }

    private void SettingsOpenTrigger_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var i = SettingsOpenTriggerCombo.SelectedIndex;
        _settings.OpenFileTrigger = i < 0 ? 0 : i;
    }

    private void SettingsTableMenuStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var i = SettingsTableMenuStyleCombo.SelectedIndex;
        _settings.TableMenuStyle = i < 0 ? 0 : i;
    }

    private void SettingsFileDelete_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var i = SettingsFileDeleteCombo.SelectedIndex;
        _settings.FileDeleteMode = i < 0 ? 0 : i;
    }

    private void SettingsNewNotebookEntry_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var i = SettingsNewNotebookEntryCombo.SelectedIndex;
        _settings.NewNotebookEntry = i < 0 ? 0 : i;
    }

    private void SettingsNotebookDelete_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var i = SettingsNotebookDeleteCombo.SelectedIndex;
        _settings.NotebookDeleteMode = i < 0 ? 0 : i;
    }

    private void SettingsSubfolder_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.CreateInNewSubfolder = SettingsSubfolderToggle.IsOn;
    }

    private async void SettingsBrowseParent_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolderAsync is null) return;
        var p = await PickFolderAsync();
        if (p is not null) { _settings.DefaultParentDir = p; SettingsDefaultParentBox.Text = p; }
    }

    private void SettingsClearParent_Click(object sender, RoutedEventArgs e)
    {
        _settings.DefaultParentDir = null;
        SettingsDefaultParentBox.Text = "";
    }

    private void SettingsRestore_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.RestoreLastNotebook = SettingsRestoreToggle.IsOn;
    }

    private void SettingsAutoSave_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.AutoSaveDebounceMs = IndexToAutoSave(SettingsAutoSaveCombo.SelectedIndex);
        EditorConfigChanged?.Invoke();
    }
}
