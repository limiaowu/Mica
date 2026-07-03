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

// Part of the MainWindow partial class. Notebook operations shared by the home/detail pages
// (Views/NotebookHomePage, Views/NotebookDetailPage) AND the file tree: activate, CRUD form,
// rename, delete dialog, pickers, title-bar 切换器. The card/detail VIEW logic lives in those
// UserControls; they raise events/callbacks wired up in the MainWindow ctor.
public sealed partial class MainWindow
{
    // 笔记本名称/简介字数上限（超长标题会撑爆顶部切换器与卡片布局）。
    private const int NameMaxLength = 20;
    private const int DescMaxLength = 200;

    // "进入笔记本"/双击/详情页都走这里：point the FileService at its folder (refreshing the
    // tree and tabs via FolderChanged), mark it active, and switch to the editor.
    private void ActivateNotebook(Notebook nb)
    {
        if (!Directory.Exists(nb.Path))
        {
            _ = ShowNotebookMissingAsync(nb);
            return;
        }
        _settings.TouchActive(nb.Id);
        _fileService.OpenFolder(nb.Path);
        NavigateTo(new ViewState(ViewKind.Files, nb.Id));
        // Entering a notebook should always reveal its 工作区, regardless of whether the
        // sidebar happened to be collapsed (or in 大纲 mode) beforehand.
        ShowSidebarFiles();
    }

    // Title-bar notebook switcher: rebuild the dropdown each time it opens so it always
    // reflects the current registry + active notebook. One toggle item per notebook;
    // picking a different one activates it (same path as 双击/进入笔记本).
    private void VaultSwitcherFlyout_Opening(object sender, object e)
    {
        VaultSwitcherFlyout.Items.Clear();
        if (_settings.Notebooks.Count == 0)
        {
            VaultSwitcherFlyout.Items.Add(new MenuFlyoutItem { Text = "（没有笔记本）", IsEnabled = false });
            return;
        }
        var activeId = _settings.ActiveNotebook?.Id;
        var previewTemplate = (DataTemplate)((FrameworkElement)Content).Resources["NotebookPreviewTemplate"];
        foreach (var nb in _settings.Notebooks)
        {
            var captured = nb;
            var item = new ToggleMenuFlyoutItem { Text = nb.Name, IsChecked = nb.Id == activeId };
            item.Click += (_, _) => { if (captured.Id != _settings.ActiveNotebook?.Id) ActivateNotebook(captured); };

            // Hover preview: reuse the notebook-card visuals inside a borderless tooltip,
            // shown to the right so it doesn't cover the menu list.
            var tip = new ToolTip
            {
                Content = new ContentControl { ContentTemplate = previewTemplate, Content = captured },
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Placement = PlacementMode.Right,
            };
            ToolTipService.SetToolTip(item, tip);

            VaultSwitcherFlyout.Items.Add(item);
        }
    }

    // 标题栏切换器名（VaultNameText）与侧栏工作区头（SidebarNotebookName）是命令式赋值的——
    // 只在 FolderChanged / 切视图时刷新，**不随 Notebook.Name 的属性通知更新**（卡片是绑定的，
    // 所以卡片会变、这两处不会）。故重命名/编辑「当前活动笔记本」后需手动刷新这两处。
    private void RefreshActiveNotebookChrome(Notebook nb)
    {
        if (_settings.ActiveNotebook?.Id != nb.Id) return;
        VaultNameText.Text = nb.Name;
        UpdateSidebarHeader();
    }

    private async Task RenameNotebookAsync(Notebook nb)
    {
        var box = new TextBox { Text = nb.Name, SelectionStart = nb.Name.Length, MaxLength = NameMaxLength };
        var dialog = new ContentDialog
        {
            Title = "重命名笔记本",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = box.Text.Trim();
            if (name.Length > 0)
            {
                nb.Name = name; // INotifyPropertyChanged updates the card
                _settings.Save();
                RefreshActiveNotebookChrome(nb); // 同步标题栏切换器/侧栏头
            }
        }
    }

    // Shared create/edit form. When `existing` is null we create a new notebook
    // (name + storage location + optional cover + intro), make the folder and
    // register it; otherwise we edit name/cover/intro in place — the location can't
    // be changed (per the agreed design).
    private async Task ShowNotebookFormAsync(Notebook? existing)
    {
        var isNew = existing is null;
        string? coverPath = existing?.CoverImagePath;
        // For a new notebook, seed the parent dir from the saved default (if it still
        // exists) so the user usually doesn't have to browse at all.
        string? parentLocation = isNew && !string.IsNullOrEmpty(_settings.DefaultParentDir)
                                 && Directory.Exists(_settings.DefaultParentDir)
            ? _settings.DefaultParentDir
            : null;

        var nameBox = new TextBox
        {
            Header = $"名称（{NameMaxLength} 字以内）",
            Text = existing?.Name ?? "",
            PlaceholderText = "例如：论文笔记",
            MaxLength = NameMaxLength, // 限制标题长度，避免切换器/卡片里超长标题撑爆布局
        };
        // 实时字数计数（#3）。硬限制由 TextBox 内建 MaxLength 完成（新输入超过即被拦下，
        // 无需自己数）；这里只做可见反馈。Text.Length 与 MaxLength 都按 UTF-16 字符计，中文
        // 算 1 个字。注意：MaxLength 只拦「手动输入」，不裁剪预填文本——编辑早于限制时创建的
        // 超长旧名时，计数会显示 25/20 并变红，提示用户当前已超限、需自行删减。
        var nameCounter = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        void UpdateNameCounter()
        {
            var len = nameBox.Text.Length;
            nameCounter.Text = $"{len}/{NameMaxLength}";
            nameCounter.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                len >= NameMaxLength ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        }
        UpdateNameCounter();
        var nameGroup = new StackPanel { Spacing = 4 };
        nameGroup.Children.Add(nameBox);
        nameGroup.Children.Add(nameCounter);

        // New-only: directly use the chosen folder, or create a same-named subfolder
        // under it. Defaults to the saved preference; the actual working directory is
        // previewed live below. The toggle here is transient — the persisted default
        // is only written once, on confirm (so flipping it doesn't thrash the json).
        // Declared before browseBtn so its Click handler can reference UpdatePreview.
        var subfolderToggle = new ToggleSwitch
        {
            Header = "存放方式",
            OnContent = "在所选目录下新建同名子文件夹",
            OffContent = "直接使用所选目录",
            IsOn = _settings.CreateInNewSubfolder,
            Visibility = isNew ? Visibility.Visible : Visibility.Collapsed,
        };
        var previewText = new TextBlock
        {
            FontSize = 12,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var previewGroup = new StackPanel
        {
            Spacing = 2,
            Visibility = isNew ? Visibility.Visible : Visibility.Collapsed,
        };
        previewGroup.Children.Add(new TextBlock { Text = "笔记本目录", FontSize = 12 });
        previewGroup.Children.Add(previewText);

        // 内联冲突提示（#1，不弹窗）：当「笔记本目录」已是已注册笔记本时显示，并禁用「创建」，
        // 提供「查看该笔记本」按钮。InfoBar 关闭时自身折叠不占位，所以初始隐藏即可。
        var conflictEnterBtn = new Button { Content = "查看该笔记本" };
        var conflictBar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsOpen = false,
            IsClosable = false,
            Title = "该位置已是笔记本",
            ActionButton = conflictEnterBtn,
            Visibility = Visibility.Collapsed,
        };

        ContentDialog? formDialog = null; // 下面赋值；RefreshConflict 用它禁用「创建」按钮
        Notebook? conflictNotebook = null;
        string? targetPath = null; // 当前「笔记本目录」的实际路径（随名称/存放方式实时变化）

        // 重新评估目标目录是否已是笔记本，刷新提示条与「创建」可用性（仅新建时生效）。
        void RefreshConflict()
        {
            conflictNotebook = isNew && !string.IsNullOrEmpty(targetPath)
                ? _settings.FindNotebookByPath(targetPath!)
                : null;
            if (conflictNotebook is not null)
            {
                conflictBar.Message = $"已存在笔记本「{conflictNotebook.Name}」，不能在同一目录重复创建。";
                conflictBar.IsOpen = true;
                conflictBar.Visibility = Visibility.Visible;
            }
            else
            {
                conflictBar.IsOpen = false;
                conflictBar.Visibility = Visibility.Collapsed;
            }
            if (formDialog is not null) formDialog.IsPrimaryButtonEnabled = conflictNotebook is null;
        }

        void UpdatePreview()
        {
            if (string.IsNullOrEmpty(parentLocation))
            {
                targetPath = null;
                previewText.Text = "（请先选择存放位置）";
                RefreshConflict();
                return;
            }
            var leaf = SanitizeFolderName(nameBox.Text.Trim());
            targetPath = subfolderToggle.IsOn
                ? System.IO.Path.Combine(parentLocation, leaf)
                : parentLocation;
            previewText.Text = targetPath;
            RefreshConflict();
        }

        // 「查看该笔记本」：关掉本对话框，定位到那个已存在的笔记本详情页。
        conflictEnterBtn.Click += (_, _) =>
        {
            var target = conflictNotebook;
            formDialog?.Hide();
            if (target is not null)
            {
                if (NotebookHomeView.Visibility == Visibility.Visible) ShowHomeInternal();
                NavigateTo(new ViewState(ViewKind.Detail, target.Id));
            }
        };

        // 编辑模式下选了新位置则记录迁移目标（新的完整笔记本根 = 所选父目录 + 原文件夹名）；
        // 与原路径相同则保持 null（不迁移）。新建模式不用它。
        string? editMigrateTarget = null;

        var locationBox = new TextBox
        {
            Header = isNew ? "存放位置" : "存放位置（改这里会迁移整个笔记本）",
            IsReadOnly = true,
            PlaceholderText = "选择一个文件夹…",
            Text = existing?.Path ?? parentLocation ?? "",
        };
        var browseBtn = new Button
        {
            Content = "浏览",
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        browseBtn.Click += async (_, _) =>
        {
            var folder = await PickFolderPathAsync();
            if (folder is null) return;
            if (isNew)
            {
                parentLocation = folder;
                locationBox.Text = folder;
                // Convenience: pre-fill the name from the folder leaf if still blank.
                if (nameBox.Text.Trim().Length == 0)
                    nameBox.Text = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
                UpdatePreview();
            }
            else
            {
                // 编辑：所选 folder 当作新「父目录」，把笔记本文件夹（保留原名）迁进去。
                var leaf = System.IO.Path.GetFileName(existing!.Path.TrimEnd('\\', '/'));
                var target = System.IO.Path.Combine(folder, leaf);
                editMigrateTarget = PathsEqual(target, existing.Path) ? null : target;
                locationBox.Text = target;
            }
        };
        var locGrid = new Grid { ColumnSpacing = 8 };
        locGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(locationBox, 0);
        Grid.SetColumn(browseBtn, 1);
        locGrid.Children.Add(locationBox);
        locGrid.Children.Add(browseBtn);

        nameBox.TextChanged += (_, _) =>
        {
            UpdatePreview();
            UpdateNameCounter();
        };
        subfolderToggle.Toggled += (_, _) => UpdatePreview();
        UpdatePreview();

        var coverText = new TextBlock
        {
            Text = coverPath is null ? "未选择" : System.IO.Path.GetFileName(coverPath),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var pickCoverBtn = new Button { Content = "选择图片" };
        var clearCoverBtn = new Button { Content = "移除" };
        pickCoverBtn.Click += async (_, _) =>
        {
            var p = await PickImagePathAsync();
            if (p is not null) { coverPath = p; coverText.Text = System.IO.Path.GetFileName(p); }
        };
        clearCoverBtn.Click += (_, _) => { coverPath = null; coverText.Text = "未选择"; };
        var coverRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        coverRow.Children.Add(pickCoverBtn);
        coverRow.Children.Add(clearCoverBtn);
        coverRow.Children.Add(coverText);
        var coverGroup = new StackPanel { Spacing = 4 };
        coverGroup.Children.Add(new TextBlock { Text = "封面（可选）", FontSize = 12 });
        coverGroup.Children.Add(coverRow);

        var descBox = new TextBox
        {
            Header = $"简介（可选，{DescMaxLength} 字以内）",
            Text = existing?.Description ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            PlaceholderText = "写点简介…",
            MaxLength = DescMaxLength, // 限制简介长度
        };
        // 简介实时字数计数（同名称：硬限制走 MaxLength，这里只做可见反馈）。
        var descCounter = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        void UpdateDescCounter()
        {
            var len = descBox.Text.Length;
            descCounter.Text = $"{len}/{DescMaxLength}";
            descCounter.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                len >= DescMaxLength ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        }
        UpdateDescCounter();
        var descGroup = new StackPanel { Spacing = 4 };
        descGroup.Children.Add(descBox);
        descGroup.Children.Add(descCounter);
        descBox.TextChanged += (_, _) => UpdateDescCounter();

        var errorText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };

        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(nameGroup);
        panel.Children.Add(locGrid);
        panel.Children.Add(subfolderToggle);
        panel.Children.Add(previewGroup);
        panel.Children.Add(conflictBar);
        panel.Children.Add(coverGroup);
        panel.Children.Add(descGroup);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = isNew ? "新建笔记本" : "编辑笔记本信息",
            Content = new ScrollViewer { Content = panel, HorizontalScrollMode = ScrollMode.Disabled },
            PrimaryButtonText = isNew ? "创建" : "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        formDialog = dialog;
        RefreshConflict(); // 初始按已选目录决定「创建」是否可用（含直接打开表单时种子目录已是笔记本的情况）

        // Validate without closing on failure.
        dialog.Closing += (_, args) =>
        {
            if (args.Result != ContentDialogResult.Primary) return;
            if (nameBox.Text.Trim().Length == 0)
            {
                errorText.Text = "请输入名称"; errorText.Visibility = Visibility.Visible; args.Cancel = true; return;
            }
            if (isNew && string.IsNullOrEmpty(parentLocation))
            {
                errorText.Text = "请选择存放位置"; errorText.Visibility = Visibility.Visible; args.Cancel = true; return;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var newName = nameBox.Text.Trim();
        var desc = descBox.Text.Trim();

        if (isNew)
        {
            try
            {
                var fullPath = subfolderToggle.IsOn
                    ? System.IO.Path.Combine(parentLocation!, SanitizeFolderName(newName))
                    : parentLocation!;

                // 防御性兜底：冲突时「创建」按钮已被 RefreshConflict 禁用，正常走不到这；
                // 万一并发变化导致目录已是笔记本，直接放弃，不重复创建。
                if (_settings.FindNotebookByPath(fullPath) is not null) return;

                Directory.CreateDirectory(fullPath); // no-op when the folder already exists
                // Remember the choice as the new default (single write on confirm).
                _settings.CreateInNewSubfolder = subfolderToggle.IsOn;
                var nb = _settings.AddOrGetNotebook(fullPath);
                nb.Name = newName;
                nb.Description = desc;
                nb.CoverImagePath = coverPath;
                _settings.Save();
                if (NotebookHomeView.Visibility == Visibility.Visible) ShowHomeInternal();
                NavigateTo(new ViewState(ViewKind.Detail, nb.Id));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create notebook");
            }
        }
        else
        {
            existing!.Name = newName;
            existing.Description = desc;
            existing.CoverImagePath = coverPath;
            _settings.Save();
            RefreshActiveNotebookChrome(existing); // 同步标题栏切换器/侧栏头
            // 选了新位置 → 迁移整个笔记本文件夹（含图）到新路径（带进度、阻断编辑）。
            if (editMigrateTarget is not null)
                await MigrateNotebookAsync(existing, editMigrateTarget);
        }
    }

    // 迁移笔记本：把整个文件夹（含 .mica，所有笔记+图）搬到 newRoot。**整夹搬→笔记内相对 <img src> 全不变、无需改写**。
    // 用模态进度对话框阻断编辑（避免边迁移边写盘冲突）。活动笔记本迁完：重挂目录(OpenFolder)、重定向打开的标签、重载当前编辑器内容（让 web 按新路径重解析图片 URL）。
    private async Task MigrateNotebookAsync(Notebook nb, string newRoot)
    {
        var oldRoot = nb.Path;
        if (PathsEqual(oldRoot, newRoot)) return;
        if (IsPathUnder(oldRoot, newRoot)) { await ShowMessageAsync("无法迁移", "不能把笔记本迁移到它自己的子目录里。"); return; }
        if (Directory.Exists(newRoot)) { await ShowMessageAsync("无法迁移", $"目标已存在同名文件夹：\n{newRoot}"); return; }

        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 320, IsIndeterminate = true };
        var status = new TextBlock { Text = "正在迁移，请勿关闭…", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(bar);
        panel.Children.Add(status);
        var dlg = new ContentDialog { Title = "迁移笔记本", Content = panel, XamlRoot = Content.XamlRoot };
        var progress = new Progress<double>(v => { bar.IsIndeterminate = false; bar.Value = v; });
        var wasActive = PathsEqual(_fileService.CurrentFolder, oldRoot);

        var showTask = dlg.ShowAsync();
        Exception? failure = null;
        try
        {
            await _fileService.MigrateFolderAsync(oldRoot, newRoot, progress);
            nb.Path = newRoot;
            _settings.Save();
            RetargetTabsUnder(oldRoot, newRoot, isDir: true); // 打开的标签 token：oldRoot→newRoot
            if (wasActive)
            {
                _fileService.OpenFolder(newRoot); // 重载树 + 重挂监视（迁移时已停）
                // 重载当前标签内容：让 web 按笔记新绝对路径重算图片显示 URL（旧 URL 指向已移走的路径）。
                if (EditorTabs.SelectedItem is TabViewItem { Tag: string tok })
                {
                    var body = await _fileService.ReadFileAsync(tok);
                    _ipcRouter.SendNotification("editor.load", new { relPath = tok, body });
                    SetActiveFile(tok);
                }
                RefreshActiveNotebookChrome(nb);
            }
        }
        catch (Exception ex) { failure = ex; Log.Error(ex, "Migrate notebook failed: {Old} -> {New}", oldRoot, newRoot); }
        finally { dlg.Hide(); }

        await showTask; // 等进度对话框真正关闭，再弹错误（同时只能开一个 ContentDialog）
        if (failure is not null)
            await ShowMessageAsync("迁移失败", $"迁移过程中出错：\n{failure.Message}\n\n笔记本仍在原位置，请检查目标权限/磁盘空间后重试。");
    }

    // path 是否在 root 之下（含 root 自身的子目录）。用于挡「迁移到自身子目录」。
    private static bool IsPathUnder(string root, string path)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dlg = new ContentDialog { Title = title, Content = message, CloseButtonText = "知道了", XamlRoot = Content.XamlRoot };
        await dlg.ShowAsync();
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length > 0 ? clean : "新笔记本";
    }

    private async Task<string?> PickFolderPathAsync()
    {
        try
        {
            var picker = new FolderPicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex) { Log.Warning(ex, "Folder pick failed"); return null; }
    }

    private async Task<string?> PickImagePathAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" })
                picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception ex) { Log.Warning(ex, "Image pick failed"); return null; }
    }

    // 多选图片（插入图片 / 往图册加图用）。取消或失败返回空列表。
    private async Task<IReadOnlyList<string>> PickImagePathsAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" })
                picker.FileTypeFilter.Add(ext);
            var files = await picker.PickMultipleFilesAsync();
            return files?.Select(f => f.Path).ToList() ?? new List<string>();
        }
        catch (Exception ex) { Log.Warning(ex, "Image multi-pick failed"); return Array.Empty<string>(); }
    }

    // Three-way delete (single or batch): keep files (just unregister), delete files
    // too (to the recycle bin), or cancel.
    private async Task DeleteNotebooksAsync(IList<Notebook> targets)
    {
        if (targets.Count == 0) return;

        var names = targets.Count == 1
            ? $"「{targets[0].Name}」"
            : string.Join("、", targets.Select(t => t.Name));

        // Behavior is decided by the setting; the dialog is just confirm/cancel.
        // 0 = 仅移除（保留磁盘文件）, 1 = 移入回收站, 2 = 永久删除.
        var mode = _settings.NotebookDeleteMode;
        var (action, detail) = mode switch
        {
            2 => ("永久删除", "并永久删除磁盘上的文件夹，此操作不可恢复。"),
            1 => ("删除", "并把磁盘上的文件夹移入回收站。"),
            _ => ("移除", "（磁盘上的文件夹会保留）。"),
        };
        var noun = targets.Count == 1 ? "笔记本" : $"{targets.Count} 个笔记本";

        var dialog = new ContentDialog
        {
            Title = $"{action}{noun}",
            Content = $"将{action} {names}{detail}",
            PrimaryButtonText = action,
            CloseButtonText = "取消",
            DefaultButton = mode == 2 ? ContentDialogButton.None : ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (mode == 2) ApplyDangerPrimary(dialog);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return; // 取消

        var activeId = _settings.ActiveNotebook?.Id;
        var deletingActive = targets.Any(t => t.Id == activeId);

        foreach (var nb in targets)
        {
            // Unify all delete modes: close the editor tabs whose files live under this
            // notebook's folder (移除/回收站/永久 all clear that notebook's open notes —
            // user: 三种删除都应关闭该笔记本里打开的笔记). The global editor session is
            // otherwise untouched (tabs from OTHER notebooks stay open across a switch).
            CloseTabsUnder(nb.Path, isDirectory: true);

            if (mode != 0)
            {
                // System.IO + SHFileOperation（见 FileService.DeletePath）。**不要**用
                // Windows.Storage.StorageFolder.DeleteAsync——未打包应用里它会静默失败，
                // 文件夹根本删不掉（之前删除「无效」的根因）。mode 1=回收站 / 2=永久。
                try
                {
                    FileService.DeletePath(nb.Path, isDir: true, permanent: mode == 2);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to delete notebook folder: {Path}", nb.Path);
                }
            }
            _settings.RemoveNotebook(nb);
        }

        // If the active notebook was just deleted, switch to the next surviving notebook
        // (and actually OPEN its folder so the file tree reloads — TouchActive alone only
        // sets the active id, leaving FileService with no open folder → empty tree).
        if (deletingActive)
        {
            _fileService.CloseFolder();
            var next = _settings.Notebooks.FirstOrDefault(n => Directory.Exists(n.Path));
            if (next is not null)
            {
                _settings.TouchActive(next.Id);
                _fileService.OpenFolder(next.Path);
            }
        }

        NotebookHomeView.ExitMultiSelect();
        NavigateTo(new ViewState(ViewKind.Home, null));
    }

    // 「打开文件夹」选到一个已注册的目录时弹此提示：写出目录路径（次要色/等宽）+ 笔记本名（强调色），
    // 让用户一眼看清是哪个目录、对应哪个笔记本。返回 true=「重新选择目录」，false=「显示笔记本」。
    private async Task<bool> ShowNotebookExistsAsync(Notebook nb, string path)
    {
        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "你选择的目录已经是一个笔记本，无需重复打开。",
            TextWrapping = TextWrapping.Wrap,
        });

        var dirLine = new TextBlock { TextWrapping = TextWrapping.Wrap };
        dirLine.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "目录：" });
        dirLine.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = path,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(dirLine);

        var nameLine = new TextBlock { TextWrapping = TextWrapping.Wrap };
        nameLine.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "笔记本：" });
        nameLine.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = nb.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        });
        panel.Children.Add(nameLine);

        var dialog = new ContentDialog
        {
            Title = "笔记本已存在",
            Content = panel,
            PrimaryButtonText = "查看该笔记本",
            CloseButtonText = "重新选择目录",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        // 主按钮=显示笔记本(false)；关闭按钮=重新选择目录(true)。
        return await dialog.ShowAsync() != ContentDialogResult.Primary;
    }

    private async Task ShowNotebookMissingAsync(Notebook nb)
    {
        var dialog = new ContentDialog
        {
            Title = "找不到文件夹",
            Content = $"「{nb.Name}」的文件夹不存在或已被移动：\n{nb.Path}\n\n是否从列表中移除？",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.RemoveNotebook(nb);
            NavigateTo(new ViewState(ViewKind.Home, null));
        }
    }

}
