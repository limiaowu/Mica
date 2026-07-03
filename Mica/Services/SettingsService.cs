using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mica.Models;
using Serilog;

namespace Mica.Services;

// Single source of truth for global app state, persisted to one settings.json
// (theme preference + the notebook registry + which notebook is active). Replaces
// the old loose theme.txt / last-folder.txt, which are migrated in on first run.
// Pure file system, no database — consistent with the project's design.
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string MicaDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mica");

    private static string SettingsPath => System.IO.Path.Combine(MicaDir, "settings.json");
    private static string LegacyThemePath => System.IO.Path.Combine(MicaDir, "theme.txt");
    private static string LegacyFolderPath => System.IO.Path.Combine(MicaDir, "last-folder.txt");

    private AppState _state = new();

    // Live collection bound to the notebook home GridView. Reorders made by the
    // user mutate this directly; we persist the resulting order on save.
    public ObservableCollection<Notebook> Notebooks { get; } = [];

    public string ThemePref
    {
        get => _state.Theme;
        set { _state.Theme = value; Save(); }
    }

    public string? ActiveNotebookId
    {
        get => _state.ActiveNotebookId;
        set { _state.ActiveNotebookId = value; Save(); }
    }

    // Default for the "新建笔记本" dialog: true → create a same-named subfolder under
    // the chosen directory; false → use the chosen directory as-is. Read is free
    // (in-memory); only written to disk on Save(), so toggling in the dialog doesn't
    // thrash settings.json.
    public bool CreateInNewSubfolder
    {
        get => _state.CreateInNewSubfolder;
        set { _state.CreateInNewSubfolder = value; Save(); }
    }

    // Default parent directory pre-filled in the 新建笔记本 dialog (so the user doesn't
    // have to browse every time). Null = no default.
    public string? DefaultParentDir
    {
        get => _state.DefaultParentDir;
        set { _state.DefaultParentDir = value; Save(); }
    }

    // --- Editor appearance (pushed to the web editor over IPC: editor.config) ---

    // A CSS font-family string applied to the WYSIWYG canvas.
    public string EditorFontFamily
    {
        get => _state.EditorFontFamily;
        set { _state.EditorFontFamily = value; Save(); }
    }

    // Editor body font size in px.
    public int EditorFontSize
    {
        get => _state.EditorFontSize;
        set { _state.EditorFontSize = value; Save(); }
    }

    // Writing-canvas max width in px (narrow / medium / wide).
    public int EditorLineWidth
    {
        get => _state.EditorLineWidth;
        set { _state.EditorLineWidth = value; Save(); }
    }

    // Debounce before an edit is flushed to disk (ms).
    public int AutoSaveDebounceMs
    {
        get => _state.AutoSaveDebounceMs;
        set { _state.AutoSaveDebounceMs = value; Save(); }
    }

    // 代码块是否显示行号（默认显示）。
    public bool EditorShowLineNumbers
    {
        get => _state.EditorShowLineNumbers;
        set { _state.EditorShowLineNumbers = value; Save(); }
    }

    // 段落首行缩进 2 字符（默认关）。
    public bool EditorFirstLineIndent
    {
        get => _state.EditorFirstLineIndent;
        set { _state.EditorFirstLineIndent = value; Save(); }
    }

    // 删除图片是否二次确认（图前按 Del / 图后按退格 / 选中图后按删除键，先标红框、再按才删；默认关 = 直接删）。
    public bool EditorImageDeleteConfirm
    {
        get => _state.EditorImageDeleteConfirm;
        set { _state.EditorImageDeleteConfirm = value; Save(); }
    }

    // 表格右键菜单样式：0 = 分组（插入/移动/删除收进子菜单，菜单短，默认）; 1 = 平铺（所有操作展开为一级项）。
    public int TableMenuStyle
    {
        get => _state.TableMenuStyle;
        set { _state.TableMenuStyle = value; Save(); }
    }

    // 粘贴/拖入图片时的存储模式（对标 Typora 图片设置下拉）：
    //   0 = 不复制（用原绝对路径） / 1 = 内嵌 base64 / 2 = 当前目录（与 .md 同级）
    //   3 = assets/ 子文件夹（与 .md 同级） / 4 = .mica 统一管理（默认，落 .mica/assets/<笔记>/）
    public int ImageStorageMode
    {
        get => _state.ImageStorageMode;
        set { _state.ImageStorageMode = value; Save(); }
    }

    // 编辑器缩放比例（整体功能，全应用一个值）。1.0 = 100%。
    public double EditorZoom
    {
        get => _state.EditorZoom;
        set { _state.EditorZoom = value; Save(); }
    }

    // 是否允许 Ctrl+滚轮缩放。
    public bool EditorZoomWheelEnabled
    {
        get => _state.EditorZoomWheelEnabled;
        set { _state.EditorZoomWheelEnabled = value; Save(); }
    }

    // How the × close button behaves on editor tabs, following WinUI's TabView design:
    //   0 = 悬停时显示（OnPointerOver：当前标签常显，其余标签悬停时才显）— 默认
    //   1 = 始终显示（Always）
    //   2 = 始终隐藏（所有标签 IsClosable=false；仍可用 Ctrl+W 关闭）
    public int TabCloseButtonMode
    {
        get => _state.TabCloseButtonMode;
        set { _state.TabCloseButtonMode = value; Save(); }
    }

    // Width (px) of the sidebar column; the drag grip on its right edge persists this.
    public double SidebarWidth
    {
        get => _state.SidebarWidth;
        set { _state.SidebarWidth = value; Save(); }
    }

    // When the workspace file tree is collapsed, show the active notebook name centered
    // in the title bar (so the user still knows which notebook is open). Only takes
    // effect while the editor view is showing AND the sidebar is collapsed.
    public bool ShowTitleBarNotebookName
    {
        get => _state.ShowTitleBarNotebookName;
        set { _state.ShowTitleBarNotebookName = value; Save(); }
    }

    // How笔记/文件夹 deletes behave: 0 = 移入回收站（默认，安全）; 1 = 永久删除.
    public int FileDeleteMode
    {
        get => _state.FileDeleteMode;
        set { _state.FileDeleteMode = value; Save(); }
    }

    // How笔记本 deletes behave: 0 = 仅移除（默认，保留磁盘文件）; 1 = 移入回收站; 2 = 永久删除.
    public int NotebookDeleteMode
    {
        get => _state.NotebookDeleteMode;
        set { _state.NotebookDeleteMode = value; Save(); }
    }

    // After creating a new note, open it in a tab right away.
    public bool OpenNoteAfterCreate
    {
        get => _state.OpenNoteAfterCreate;
        set { _state.OpenNoteAfterCreate = value; Save(); }
    }

    // How a file is opened from the workspace tree: 0 = 单击（默认）; 1 = 双击.
    public int OpenFileTrigger
    {
        get => _state.OpenFileTrigger;
        set { _state.OpenFileTrigger = value; Save(); }
    }

    // After creating a new notebook: 0 = 进入详情页（默认）; 1 = 直接作为工作区进入.
    public int NewNotebookEntry
    {
        get => _state.NewNotebookEntry;
        set { _state.NewNotebookEntry = value; Save(); }
    }

    // --- Startup behavior ---

    // Re-open the last active notebook on launch; false = always land on the home page.
    public bool RestoreLastNotebook
    {
        get => _state.RestoreLastNotebook;
        set { _state.RestoreLastNotebook = value; Save(); }
    }

    // User-defined order of the reorderable top-rail buttons, by Tag ("Files"/"Outline").
    // 笔记本 is in the footer (fixed) and 编辑器 was removed, so neither is here. Search box
    // + hamburger toggle are fixed too. Reordered from the settings page.
    public List<string> NavOrder
    {
        get => _state.NavOrder;
        set { _state.NavOrder = value; Save(); }
    }

    public Notebook? ActiveNotebook =>
        Notebooks.FirstOrDefault(n => n.Id == _state.ActiveNotebookId);

    // 找包含某笔记的笔记本根目录（最长前缀匹配），用于把图片落到该笔记本的 .mica 下。
    // 粘贴落盘（ImageSaveHandler）与替换图片（MainWindow.ReplaceImageAsync）共用，避免重复。
    public string? ResolveNotebookRoot(string noteAbsPath)
    {
        string note;
        try { note = Path.GetFullPath(noteAbsPath); } catch { return null; }
        string? best = null;
        foreach (var nb in Notebooks)
        {
            string root;
            try { root = Path.GetFullPath(nb.Path).TrimEnd('\\', '/'); }
            catch { continue; }
            if (note.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && (best is null || root.Length > best.Length))
                best = root;
        }
        return best;
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _state = JsonSerializer.Deserialize<AppState>(json, JsonOpts) ?? new AppState();
            }
            else
            {
                MigrateLegacy();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings.json; starting fresh");
            _state = new AppState();
        }

        Notebooks.Clear();
        foreach (var nb in _state.Notebooks)
            Notebooks.Add(nb);
    }

    // First run after upgrading: fold theme.txt + last-folder.txt into the new model.
    private void MigrateLegacy()
    {
        try
        {
            if (File.Exists(LegacyThemePath))
            {
                var pref = File.ReadAllText(LegacyThemePath).Trim();
                if (pref is "System" or "Light" or "Dark") _state.Theme = pref;
            }

            if (File.Exists(LegacyFolderPath))
            {
                var path = File.ReadAllText(LegacyFolderPath).Trim();
                if (Directory.Exists(path))
                {
                    var nb = NewNotebook(path);
                    _state.Notebooks.Add(nb);
                    _state.ActiveNotebookId = nb.Id;
                }
            }

            Save();
            Log.Information("Migrated legacy theme.txt / last-folder.txt into settings.json");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Legacy settings migration failed");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(MicaDir);
            _state.Notebooks = Notebooks.ToList();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_state, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings.json");
        }
    }

    // Find a registered notebook for the given folder path (normalized compare), or null.
    // 供「新建/打开文件夹」在动手前判断该位置是否已是笔记本，好提示用户而不是默默重用。
    public Notebook? FindNotebookByPath(string folderPath)
    {
        var normalized = System.IO.Path.GetFullPath(folderPath).TrimEnd('\\', '/');
        return Notebooks.FirstOrDefault(n =>
            string.Equals(System.IO.Path.GetFullPath(n.Path).TrimEnd('\\', '/'), normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    // Add a folder as a notebook (or return the existing one if already registered).
    public Notebook AddOrGetNotebook(string folderPath)
    {
        if (FindNotebookByPath(folderPath) is { } existing) return existing;

        var normalized = System.IO.Path.GetFullPath(folderPath).TrimEnd('\\', '/');
        var nb = NewNotebook(normalized);
        Notebooks.Add(nb);
        Save();
        return nb;
    }

    public void RemoveNotebook(Notebook notebook)
    {
        Notebooks.Remove(notebook);
        if (_state.ActiveNotebookId == notebook.Id)
            _state.ActiveNotebookId = null;
        Save();
    }

    public void TouchActive(string id)
    {
        var nb = Notebooks.FirstOrDefault(n => n.Id == id);
        if (nb is not null) nb.LastOpened = DateTimeOffset.Now;
        _state.ActiveNotebookId = id;
        Save();
    }

    // Persist the current (possibly user-reordered) collection order.
    public void PersistOrder() => Save();

    // The editor is now notebook-independent (a single global tab session, absolute paths),
    // so the open-tab set lives at the app root rather than per-notebook.
    public IReadOnlyList<string> OpenFiles => _state.OpenFiles;
    public string? ActiveFile => _state.ActiveFile;

    // Remember the full set of open tabs (in order) + which is active, so the whole editor
    // session can be reopened next launch. No-op (no save) when nothing actually changed,
    // to avoid churn from rapid tab switches.
    public void SetActiveOpenFiles(IReadOnlyList<string> openFiles, string? activeFile)
    {
        if (_state.ActiveFile == activeFile && _state.OpenFiles.SequenceEqual(openFiles)) return;
        _state.OpenFiles = openFiles.ToList();
        _state.ActiveFile = activeFile;
        Save();
    }

    private static Notebook NewNotebook(string path) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path,
        Path = path,
        CreatedAt = DateTimeOffset.Now,
        LastOpened = DateTimeOffset.Now,
    };

    // Serialized shape of settings.json.
    private sealed class AppState
    {
        public string Theme { get; set; } = "System";
        public string? ActiveNotebookId { get; set; }
        public bool CreateInNewSubfolder { get; set; } = true;
        public string? DefaultParentDir { get; set; }
        public string EditorFontFamily { get; set; } = "'Segoe UI Variable', system-ui, sans-serif";
        public int EditorFontSize { get; set; } = 15;
        public int EditorLineWidth { get; set; } = 800;
        public int AutoSaveDebounceMs { get; set; } = 500;
        public bool EditorShowLineNumbers { get; set; } = true;
        public bool EditorFirstLineIndent { get; set; } = false;
        public bool EditorImageDeleteConfirm { get; set; } = false;
        public int TableMenuStyle { get; set; } = 0; // 0 分组 / 1 平铺
        public int ImageStorageMode { get; set; } = 4; // 默认 .mica 统一管理
        public double EditorZoom { get; set; } = 1.0;
        public bool EditorZoomWheelEnabled { get; set; } = true;
        public int TabCloseButtonMode { get; set; } = 0;
        public double SidebarWidth { get; set; } = 280;
        public bool ShowTitleBarNotebookName { get; set; } = true;
        public int FileDeleteMode { get; set; } = 0;
        public int NotebookDeleteMode { get; set; } = 0;
        public bool OpenNoteAfterCreate { get; set; } = true;
        public int OpenFileTrigger { get; set; } = 0;
        public int NewNotebookEntry { get; set; } = 0;
        public bool RestoreLastNotebook { get; set; } = true;
        public List<string> NavOrder { get; set; } = ["Files", "Outline"];
        // Global editor session (absolute paths) — notebook-independent so open tabs
        // survive switching the active notebook. (Notebook.OpenFiles/ActiveFile are now
        // unused, kept only for json back-compat.)
        public List<string> OpenFiles { get; set; } = [];
        public string? ActiveFile { get; set; }
        public List<Notebook> Notebooks { get; set; } = [];
    }
}
