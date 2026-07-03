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
using Windows.System;
using Mica.Models;
using Mica.Services;
using Mica.IPC;
using Mica.IPC.Handlers;
using Serilog;

namespace Mica;

// Part of the MainWindow partial class. Menu commands, source mode, theme, editor stats.
public sealed partial class MainWindow
{
    // --- Note creation ---

    // True only while the editor surface (tree + editor) is showing. Editor-scoped menu
    // commands guard on this: their KeyboardAccelerators still fire even when MainMenuBar
    // is Collapsed (WinUI registers accelerators regardless of host visibility), so e.g.
    // Ctrl+N must not create a file while you're on the 笔记本/设置 overlay pages.
    private bool InEditorView => EditorSurface.Visibility == Visibility.Visible;

    // --- Menu: File ---

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!InEditorView) return;
        if (EditorTabs.SelectedItem is TabViewItem { Tag: string })
            _ipcRouter.SendNotification("editor.save", null);
    }

    private void OnCloseCurrentTab(object sender, RoutedEventArgs e)
    {
        if (!InEditorView) return;
        if (EditorTabs.SelectedItem is not TabViewItem tab) return;

        EditorTabs.TabItems.Remove(tab);
        UpdateEditorChrome(); // 标签数变了，刷新编辑区/空状态显隐
        if (EditorTabs.TabItems.Count == 0)
            _ipcRouter.SendNotification("editor.load", new { relPath = "", body = "" });
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // --- Menu: Edit / Paragraph / Format (editor commands via IPC) ---

    private void OnEditorCommand(object sender, RoutedEventArgs e)
    {
        if (!InEditorView) return;
        if (sender is MenuFlyoutItem { Tag: string command })
            _ipcRouter.SendNotification("editor.command", new { command });
    }

    // --- Menu: View ---

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        if (!InEditorView) return;
        SetSidebarVisible(TreePanel.Visibility != Visibility.Visible);
    }

    // Source mode is reachable from both the View menu and the status-bar toggle;
    // route both through SetSourceMode so the two controls stay in sync.
    private void OnToggleSourceMode(object sender, RoutedEventArgs e)
    {
        if (!InEditorView) return;
        if (sender is ToggleMenuFlyoutItem item) SetSourceMode(item.IsChecked);
    }

    private void OnSourceModeToggle(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn) SetSourceMode(btn.IsChecked == true);
    }

    private void SetSourceMode(bool enabled)
    {
        // Programmatic IsChecked assignment doesn't re-raise Click, so no recursion.
        SourceModeMenu.IsChecked = enabled;
        SourceModeToggle.IsChecked = enabled;
        _ipcRouter.SendNotification("editor.toggleSource", new { enabled });
        // 把焦点交还编辑器：点状态栏开关/菜单项切源码时 OS 焦点在 XAML 控件上，网页内 focus() 画不出光标
        // （用户反馈「切换后光标消失」）。延后到下一拍——等 web 收到 toggleSource 建好 CM/PM 并 focus 后再抢回 OS 焦点。
        DispatcherQueue.TryEnqueue(() => EditorHost.FocusEditor());
    }

    // app 级快捷键派发：来自 web 端 keydown 转发（host.shortcut）。编辑器（WebView2）持有焦点时，
    // 顶部菜单的 XAML accelerator 收不到键，故这些键在 web 捕获后转发到这里执行既有逻辑。
    // 焦点不在编辑器时（如目录树），XAML accelerator 本就能触发，web 收不到键、不会重复。
    private void RunShortcut(string action)
    {
        if (!InEditorView) return;
        switch (action)
        {
            case "save": OnSave(this, new RoutedEventArgs()); break;
            case "closeTab": OnCloseCurrentTab(this, new RoutedEventArgs()); break;
            case "newNote": StartNewNoteAtRoot(); break;
            case "openFolder": _ = OpenFolderAsync(); break;
            case "toggleSidebar": SetSidebarVisible(TreePanel.Visibility != Visibility.Visible); break;
            case "toggleSource": SetSourceMode(!(SourceModeToggle.IsChecked == true)); break;
            case "fullscreen": OnToggleFullscreen(this, new RoutedEventArgs()); break;
            case "insertTable": _ = ShowInsertTableDialogAsync(); break;
            case "insertImage": _ = InsertImagesFromFilesAsync(); break;
            case "newGallery": InsertEmptyGallery(); break;
            case "pasteImage": _ = InsertImageFromClipboardAsync(); break;
        }
    }

    // --- Theme ---

    // Title-bar quick toggle: flip between explicit Light and Dark. The full
    // 跟随系统/浅色/深色 choice lives in 设置.
    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        ApplyThemePreference(ThemeToggle.IsChecked == true ? "Dark" : "Light");
    }

    private void ApplyThemePreference(string pref)
    {
        _themePref = pref;
        if (Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = pref switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        UpdateThemeToggleVisual();
        UpdateCaptionButtonColors();
        _settings.ThemePref = pref;
        // ActualThemeChanged also fires when the theme flips; sync here too in case
        // it didn't change (e.g. selecting the theme that already matches the system).
        SyncEditorTheme();
    }

    // Reflect the effective (resolved) theme on the title-bar toggle: a moon when
    // dark, a sun when light, with IsChecked tracking dark.
    private void UpdateThemeToggleVisual()
    {
        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        ThemeToggle.IsChecked = isDark;
        ThemeToggleIcon.Glyph = isDark ? "" : ""; // moon / sun
    }

    private void OnContentActualThemeChanged(FrameworkElement sender, object args)
    {
        SyncEditorTheme();
        UpdateCaptionButtonColors();
    }

    // The system caption buttons (minimize / maximize / close) don't auto-adapt their glyph
    // color when we extend content into the title bar — under HDR + dark they render nearly
    // invisible (dark glyphs on a dark Mica surface). Set the foreground explicitly per theme;
    // background stays transparent so the Mica material still shows through. Re-run on theme
    // change. Mirrors what Notepad/other Fluent apps do for their custom title bars.
    private void UpdateCaptionButtonColors()
    {
        if (_appWindow?.TitleBar is not { } tb) return;
        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;

        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        if (isDark)
        {
            tb.ButtonForegroundColor = Microsoft.UI.Colors.White;
            tb.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            tb.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A);
        }
        else
        {
            tb.ButtonForegroundColor = Microsoft.UI.Colors.Black;
            tb.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x22, 0x00, 0x00, 0x00);
            tb.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x44, 0x00, 0x00, 0x00);
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x66, 0x66, 0x66);
        }
    }

    // Push editor appearance + auto-save settings to the web editor (editor.config).
    // 设置页（SettingsPage）改动字体/字号/行宽/自动保存时经 EditorConfigChanged 事件回调到这里；
    // WebReady 时也推一次。
    private void PushEditorConfig()
    {
        _ipcRouter.SendNotification("editor.config", new
        {
            fontFamily = _settings.EditorFontFamily,
            fontSize = _settings.EditorFontSize,
            lineWidth = _settings.EditorLineWidth,
            autoSaveMs = _settings.AutoSaveDebounceMs,
            showLineNumbers = _settings.EditorShowLineNumbers,
            firstLineIndent = _settings.EditorFirstLineIndent,
            imageDeleteConfirm = _settings.EditorImageDeleteConfirm,
        });
        // 格式菜单的「首行缩进」勾选跟随当前设置（设置页改了也会经此同步）。
        FirstLineIndentMenu.IsChecked = _settings.EditorFirstLineIndent;
    }

    // 格式菜单：切换首行缩进（与设置页是同一个 EditorFirstLineIndent；改完推 editor.config 让 web 即时生效）。
    private void OnToggleFirstLineIndent(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item) return;
        _settings.EditorFirstLineIndent = item.IsChecked;
        PushEditorConfig();
    }

    // Tell the editor which palette to use. Colors live in editor.css (.dark);
    // we only send the mode so there's a single source of truth for styling.
    private void SyncEditorTheme()
    {
        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        var vars = new Dictionary<string, string>();

        // 把 Windows 系统主题色（强调色）传给编辑器，用于任务列表勾选框、表格列宽手柄等，
        // 让这些控件与系统一致。深色模式取 AccentLight1 提升对比度。
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var accent = ui.GetColorValue(isDark
                ? Windows.UI.ViewManagement.UIColorType.AccentLight1
                : Windows.UI.ViewManagement.UIColorType.Accent);
            vars["--mica-accent"] = $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";
        }
        catch { /* 取不到就让 CSS 走 --mica-link 兜底 */ }

        _ipcRouter.SendNotification("theme.update", new
        {
            vars,
            mode = isDark ? "dark" : "light"
        });
    }

    // --- Word count (pushed from the editor over IPC) ---

    private void OnEditorStatsUpdated(int words, int chars)
    {
        DispatcherQueue.TryEnqueue(() =>
            StatusWordCount.Text = $"{words} 词 · {chars} 字符");
    }

    // --- Zoom（整体缩放，全应用一个值；缩放逻辑在 web 端用 CSS zoom 实现）---

    // 注册缩放快捷键（Ctrl+Shift+加/减/0）。**坑（上一版的 bug）**：把 KeyboardAccelerator 加到 MenuFlyoutItem
    // 上、却不写 Invoked，指望它自动触发菜单项的 Click——**代码后置添加的 accelerator 不会自动 invoke 菜单项**，
    // 结果它把按键「吃掉」（WebView 也收不到了）却什么都不做 → 三个快捷键全失效。
    // 正解：accelerator 挂到根元素（窗口作用域），并写**显式 Invoked**直接调缩放逻辑。
    //   · 焦点在别处（文件树/菜单/标题栏）→ accelerator 触发 → SetZoom → PushZoomConfig 下发给 web；
    //   · 焦点在 WebView 内 → 多数情况 WebView 吞键、accelerator 不触发，由 web keydown 自理（两条互补）；
    //     即便某些组合 accelerator 抢到了，也照样经宿主把缩放生效，不会「按了没反应」。
    // 主键盘 +/-/0 无具名 VirtualKey（用 0xBB/0xBD/Number0），并兼容小键盘 Add/Subtract/NumberPad0。
    private void WireZoomAccelerators()
    {
        if (Content is not UIElement rootEl) return;

        // 关掉「快捷键自动提示气泡」：把 accelerator 挂在根元素后，WinUI 会在指针进入/聚焦时
        // 自动弹一个写着键位（如「Ctrl+Shift++」）的 ToolTip，鼠标移出再移回编辑区就重复弹、
        // 还得点目录树才消失。设 Hidden 抑制这些自动提示（菜单里仍用 KeyboardAcceleratorTextOverride 显示文字）。
        rootEl.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        AddAccel((VirtualKey)0xBB, ZoomAction.In);      // 主键盘 = / +
        AddAccel(VirtualKey.Add, ZoomAction.In);        // 小键盘 +
        AddAccel((VirtualKey)0xBD, ZoomAction.Out);     // 主键盘 - / _
        AddAccel(VirtualKey.Subtract, ZoomAction.Out);  // 小键盘 -
        AddAccel(VirtualKey.Number0, ZoomAction.Reset); // 主键盘 0
        AddAccel(VirtualKey.NumberPad0, ZoomAction.Reset);

        void AddAccel(VirtualKey key, ZoomAction action)
        {
            var acc = new KeyboardAccelerator
            {
                Key = key,
                Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            };
            acc.Invoked += (_, e) =>
            {
                e.Handled = true;
                if (!InEditorView) return; // 覆盖页（设置/笔记本）不缩放编辑器
                switch (action)
                {
                    case ZoomAction.In: SetZoom(_settings.EditorZoom + 0.1); break;
                    case ZoomAction.Out: SetZoom(_settings.EditorZoom - 0.1); break;
                    case ZoomAction.Reset: SetZoom(1.0); break;
                }
            };
            rootEl.KeyboardAccelerators.Add(acc);
        }
    }

    private enum ZoomAction { In, Out, Reset }

    // 把缩放设置推给编辑器：初始/目标比例 + 是否允许 Ctrl+滚轮。
    private void PushZoomConfig()
    {
        _ipcRouter.SendNotification("editor.zoomConfig", new
        {
            factor = _settings.EditorZoom,
            wheelEnabled = _settings.EditorZoomWheelEnabled,
        });
        UpdateZoomIndicator(_settings.EditorZoom);
    }

    // 编辑器上报当前缩放（用户用 Ctrl+滚轮/快捷键改了）：存设置 + 刷新状态栏，不回推（避免环）。
    private void OnEditorZoomChanged(double factor)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _settings.EditorZoom = factor;
            UpdateZoomIndicator(factor);
        });
    }

    // 状态栏点选预设 → 设定缩放并下发给编辑器（web 应用后会回报，幂等）。
    private void OnZoomPreset(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out var factor))
        {
            SetZoom(factor);
        }
    }

    // 视图菜单：放大 / 缩小 / 实际大小。步进 0.1，范围 [0.5, 3]（与 web 端一致）。
    private void OnZoomIn(object sender, RoutedEventArgs e) { if (InEditorView) SetZoom(_settings.EditorZoom + 0.1); }
    private void OnZoomOut(object sender, RoutedEventArgs e) { if (InEditorView) SetZoom(_settings.EditorZoom - 0.1); }
    private void OnZoomReset(object sender, RoutedEventArgs e) { if (InEditorView) SetZoom(1.0); }

    private void SetZoom(double factor)
    {
        var clamped = System.Math.Clamp(System.Math.Round(factor * 100) / 100, 0.5, 3.0);
        _settings.EditorZoom = clamped;
        PushZoomConfig();
    }

    private void UpdateZoomIndicator(double factor)
    {
        ZoomButton.Content = $"{(int)System.Math.Round(factor * 100)}%";
    }

}
