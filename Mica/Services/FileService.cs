using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Mica.Models;

namespace Mica.Services;

public sealed class FileService
{
    public string? CurrentFolder { get; private set; }
    public string? FolderName => CurrentFolder is null ? null : Path.GetFileName(CurrentFolder);

    public event EventHandler? FolderChanged;
    // 外部（资源管理器等）改动当前笔记本目录结构时触发——已防抖、已排除 Mica 自身操作。订阅方据此刷新树。
    public event EventHandler? ExternalChanged;

    // 只监视「当前活动笔记本」一个目录（切笔记本时重挂；其他笔记本切回时整树重载，无需常驻监视）。
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _fsDebounce;
    private int _internalBusy;                              // >0：Mica 正在自己改盘
    private DateTime _suppressUntil = DateTime.MinValue;    // 自身操作结束后的尾随静默窗口
    private static readonly TimeSpan SuppressTail = TimeSpan.FromMilliseconds(600);

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(path);
        CurrentFolder = path;
        StartWatching(path);
        FolderChanged?.Invoke(this, EventArgs.Empty);
    }

    // Detach from the current folder (e.g. after deleting the active notebook) so
    // the file tree clears immediately rather than lingering until restart.
    public void CloseFolder()
    {
        StopWatching();
        CurrentFolder = null;
        FolderChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===== 文件系统监视（外部改动自动刷新树）=====
    // 区分内外的关键两招：① 只监视 FileName/DirectoryName（结构变化），**不含 LastWrite/Size**——
    // 故笔记保存(写内容)根本不触发事件，消除最高频的内部噪声；② 每个改盘方法用 BeginInternal/EndInternal
    // 括住，期间+尾随 SuppressTail 内的事件都判为「自己干的」忽略。两招叠加后剩下的就只有真正的外部改动。

    // 标记一次内部磁盘操作的开始/结束（用 try/finally 包住每个改盘方法）。
    private void BeginInternal() => System.Threading.Interlocked.Increment(ref _internalBusy);
    private void EndInternal()
    {
        if (System.Threading.Interlocked.Decrement(ref _internalBusy) == 0)
            _suppressUntil = DateTime.UtcNow + SuppressTail;
    }
    private bool IsSelfActivity =>
        System.Threading.Volatile.Read(ref _internalBusy) > 0 || DateTime.UtcNow < _suppressUntil;

    private void StartWatching(string path)
    {
        StopWatching();
        try
        {
            _fsDebounce = new System.Threading.Timer(
                OnDebounceTick, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024, // 批量改动时防缓冲溢出丢事件
            };
            _watcher.Created += OnRawFsEvent;
            _watcher.Deleted += OnRawFsEvent;
            _watcher.Renamed += OnRawFsEvent;
            _watcher.Error += (_, _) => ScheduleDebounce(); // 溢出等异常：兜底整体刷一次
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // 某些路径（网络盘/无权限）不支持监视：静默降级为「手动刷新」，不影响其他功能。
            StopWatching();
        }
    }

    private void StopWatching()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _fsDebounce?.Dispose();
        _fsDebounce = null;
    }

    private void OnRawFsEvent(object sender, FileSystemEventArgs e)
    {
        // 忽略与目录树无关的改动（.mica 资产/dotfile/Office 临时文件）。重命名两端都无关才忽略。
        if (ShouldIgnorePath(e.FullPath)
            && (e is not RenamedEventArgs r || ShouldIgnorePath(r.OldFullPath)))
            return;
        ScheduleDebounce();
    }

    private void ScheduleDebounce() =>
        _fsDebounce?.Change(350, System.Threading.Timeout.Infinite); // 350ms 内的连串事件合并成一次刷新

    private void OnDebounceTick(object? _)
    {
        // 仍是 Mica 自己在改盘：往后推一点再判，绝不打断/重复自身操作。
        if (IsSelfActivity) { _fsDebounce?.Change(250, System.Threading.Timeout.Infinite); return; }
        ExternalChanged?.Invoke(this, EventArgs.Empty);
    }

    // 与目录树无关的路径（树只显示文件夹 + .md；.mica/dotfile/~$ 一律不进树）。
    private bool ShouldIgnorePath(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (name.StartsWith('.') || name.StartsWith("~$")) return true;
        if (CurrentFolder is null) return true;
        var rel = Path.GetRelativePath(CurrentFolder, fullPath).Replace('\\', '/');
        return rel == ".mica" || rel.StartsWith(".mica/", StringComparison.OrdinalIgnoreCase);
    }

    public ObservableCollection<FileNode> GetTree()
    {
        if (CurrentFolder is null) return [];
        return ScanDirectory(CurrentFolder, CurrentFolder);
    }

    public async Task<string> ReadFileAsync(string relPath, CancellationToken ct = default)
    {
        return await File.ReadAllTextAsync(GetFullPath(relPath), Encoding.UTF8, ct);
    }

    public async Task WriteFileAsync(string relPath, string content, CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(GetFullPath(relPath), content, Encoding.UTF8, ct);
    }

    public async Task<string> CreateFileAsync(string relPath, string? initialContent = null, CancellationToken ct = default)
    {
        BeginInternal();
        try
        {
            var fullPath = GetFullPath(relPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var actualPath = relPath;
            if (File.Exists(fullPath))
            {
                var name = Path.GetFileNameWithoutExtension(relPath);
                var ext = Path.GetExtension(relPath);
                var parent = Path.GetDirectoryName(relPath) ?? "";
                for (var i = 1; File.Exists(GetFullPath(actualPath)); i++)
                    actualPath = Path.Combine(parent, $"{name} ({i}){ext}").Replace('\\', '/');
                fullPath = GetFullPath(actualPath);
            }

            var content = initialContent ?? $"# {Path.GetFileNameWithoutExtension(actualPath)}\n";
            await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);
            return actualPath;
        }
        finally { EndInternal(); }
    }

    // Create a new subfolder under relPath's parent. Auto-dedupes the name with a
    // " (i)" suffix (mirrors CreateFileAsync) and returns the actual relPath used.
    public string CreateFolder(string relPath)
    {
        BeginInternal();
        try
        {
            var parent = Path.GetDirectoryName(relPath) ?? "";
            var name = Path.GetFileName(relPath);
            var actual = relPath;
            for (var i = 1; Directory.Exists(GetFullPath(actual)); i++)
                actual = Path.Combine(parent, $"{name} ({i})").Replace('\\', '/');
            Directory.CreateDirectory(GetFullPath(actual));
            return actual;
        }
        finally { EndInternal(); }
    }

    // Rename/move a file or folder. Works for both since File.Move/Directory.Move
    // share the same semantics here; we pick based on what exists at the source.
    // 连带搬其 .mica 资产 + 改写笔记里的 <img src>（复用 CarryAssetsAsync，与拖拽/粘贴同一套），
    // 否则重命名带图笔记会让 .mica 图变孤儿、src 断链。
    public async Task RenameEntryAsync(string relPath, string newRelPath, CancellationToken ct = default)
    {
        BeginInternal();
        try
        {
            relPath = relPath.Replace('\\', '/').Trim('/');
            newRelPath = newRelPath.Replace('\\', '/').Trim('/');
            var src = GetFullPath(relPath);
            var newFull = GetFullPath(newRelPath);
            var newDir = Path.GetDirectoryName(newFull);
            if (newDir is not null && !Directory.Exists(newDir))
                Directory.CreateDirectory(newDir);
            var isDir = Directory.Exists(src);
            if (isDir) Directory.Move(src, newFull);
            else File.Move(src, newFull);

            // 重命名＝同笔记本移动，源/目标根都是 CurrentFolder，资产用移动。
            await CarryAssetsAsync(relPath, CurrentFolder!, newRelPath, CurrentFolder!, src, newFull, isDir, assetMove: true, ct);
        }
        finally { EndInternal(); }
    }

    // Delete a file to the recycle bin (permanent=false) or permanently.
    public Task DeleteFileAsync(string relPath, bool permanent)
    {
        BeginInternal();
        try { DeletePath(GetFullPath(relPath), isDir: false, permanent); }
        finally { EndInternal(); }
        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(string relPath, bool permanent)
    {
        BeginInternal();
        try { DeletePath(GetFullPath(relPath), isDir: true, permanent); }
        finally { EndInternal(); }
        return Task.CompletedTask;
    }

    // Delete an absolute path (file or folder). Used by both the file tree (via the
    // relPath wrappers above) and the notebook-folder delete (absolute path).
    //
    // NOTE: we deliberately do NOT use Windows.Storage (StorageFile/StorageFolder).
    // In an UNPACKAGED WinUI app those async APIs frequently throw (identity/ACL
    // issues), which the call sites swallowed — so deletes silently no-op'd and
    // files survived (and survived restart). System.IO + SHFileOperation is robust.
    public static void DeletePath(string fullPath, bool isDir, bool permanent)
    {
        if (isDir ? !Directory.Exists(fullPath) : !File.Exists(fullPath)) return;

        if (permanent)
        {
            if (isDir) Directory.Delete(fullPath, recursive: true);
            else File.Delete(fullPath);
            return;
        }

        // Recycle bin: Win32 SHFileOperation with FOF_ALLOWUNDO. pFrom must be
        // double-null terminated; throws on a non-zero return code.
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = fullPath + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var rc = SHFileOperation(ref op);
        if (rc != 0)
            throw new IOException($"SHFileOperation(delete) failed with code 0x{rc:X} for {fullPath}");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    // Absolute, normalized path for a tree node (used by 在资源管理器中显示 / 复制路径 /
    // 设为新笔记本, and as the notebook-independent token for editor tabs). Normalizing via
    // Path.GetFullPath collapses the mixed '/'+'\' separators that Path.Combine leaves.
    public string? ResolveFullPath(string relPath)
    {
        if (Path.IsPathRooted(relPath)) return Path.GetFullPath(relPath);
        return CurrentFolder is null ? null : Path.GetFullPath(GetFullPath(relPath));
    }

    // Recursive .md count for a folder (used by the notebook home cards). Best
    // effort: swallow access errors and just report what we can enumerate.
    public static int CountMarkdownFiles(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath)) return 0;
            return Directory.EnumerateFiles(folderPath, "*.md", SearchOption.AllDirectories)
                .Count(f =>
                {
                    var name = Path.GetFileName(f);
                    return !name.StartsWith('.') && !name.StartsWith("~$");
                });
        }
        catch
        {
            return 0;
        }
    }

    // ===== 图片 =====
    // 把粘贴/拖入的图片字节按存储模式落盘，返回要写进 .md 的 src（相对笔记目录的正斜杠路径，或 data URI）。
    //   mode 0 不复制（粘贴无原路径 → 退化到 .mica 默认） / 1 内嵌 base64 / 2 当前目录 / 3 assets/ / 4 .mica 统一管理
    // notebookRoot：笔记所属笔记本根目录（用于 .mica 定位），由调用方经笔记本注册表解析；为 null 时退回笔记目录。
    public async Task<string> SavePastedImageAsync(
        string noteAbsPath, string? notebookRoot, byte[] bytes, string ext, string? sourceName, int mode,
        CancellationToken ct = default)
    {
        ext = SanitizeExt(ext);
        if (mode == 1) // 内嵌 base64
            return $"data:{MimeForExt(ext)};base64,{Convert.ToBase64String(bytes)}";

        BeginInternal();
        try
        {
            var note = Path.GetFullPath(noteAbsPath);
            var noteDir = Path.GetDirectoryName(note)!;
            var imageDir = ResolveImageDirForMode(note, notebookRoot, mode);
            Directory.CreateDirectory(imageDir);

            var dest = DedupePath(Path.Combine(imageDir, MakeImageFileName(sourceName, ext)));
            await File.WriteAllBytesAsync(dest, bytes, ct);
            return Path.GetRelativePath(noteDir, dest).Replace('\\', '/');
        }
        finally { EndInternal(); }
    }

    // 某存储模式下，一篇笔记的图片落盘目录。0/1（不复制/base64）无目录概念，退回 .mica 兜底（0 退化、1 不会走到这）。
    //   2 同级目录 / 3 同级 assets（共享）/ 4 .mica 统一 / 5 <笔记名>.assets（每篇独立、随笔记同级）。
    // 被 SavePastedImageAsync（新图落盘）与迁移命令共用，保证「新存 / 迁移」落点一致。
    private static string ResolveImageDirForMode(string noteAbsPath, string? notebookRoot, int mode)
    {
        var noteDir = Path.GetDirectoryName(noteAbsPath)!;
        return mode switch
        {
            2 => noteDir,
            3 => Path.Combine(noteDir, "assets"),
            5 => Path.Combine(noteDir, Path.GetFileNameWithoutExtension(noteAbsPath) + ".assets"),
            _ => ResolveMicaAssetDir(noteAbsPath, notebookRoot), // 0/4
        };
    }

    // <根>/.mica/assets/<笔记相对路径去扩展名>/ —— 每篇笔记独立子文件夹（删笔记即删此夹、无同名冲突）。
    private static string ResolveMicaAssetDir(string noteAbsPath, string? notebookRoot)
    {
        var root = notebookRoot is not null ? Path.GetFullPath(notebookRoot) : Path.GetDirectoryName(noteAbsPath)!;
        var relNoExt = Path.ChangeExtension(Path.GetRelativePath(root, noteAbsPath), null); // 保留子目录、去扩展名
        return Path.Combine(root, ".mica", "assets", relNoExt);
    }

    // 删除/移动/重命名笔记时联动其 .mica 资产文件夹（保证：删笔记删图、移笔记不丢图）。
    // 仅处理默认 .mica 布局；当前目录/assets 模式的图与 .md 同级，由资源管理器层面的移动自然带走。
    //
    // isDirectory=true（删文件夹）：该文件夹下所有笔记的资产都在 .mica/assets/<文件夹相对路径>/ 之下，
    // 故删这整棵子树即可（文件夹名按原样、不去扩展名——文件夹可能含 . 不能误切）。
    // 返回实际删掉的资产目录（用于调用点记日志）；目录不存在返回 null。
    public string? DeleteNoteAssets(string absPath, string? notebookRoot, bool isDirectory, bool permanent)
    {
        var full = Path.GetFullPath(absPath);
        var dir = isDirectory
            ? Path.Combine(notebookRoot ?? Path.GetDirectoryName(full)!, ".mica", "assets",
                Path.GetRelativePath(notebookRoot ?? Path.GetDirectoryName(full)!, full))
            : ResolveMicaAssetDir(full, notebookRoot);
        if (!Directory.Exists(dir)) return null;
        DeletePath(dir, isDir: true, permanent);
        return dir;
    }

    // 删除一批孤儿图片文件（维护页「清理孤儿」用）。默认回收站（permanent=false）。
    // 删完顺手清理 .mica/assets 下变空的目录。返回成功删除的文件数。
    // 孤儿都在 .mica 下、watcher 本就忽略该目录，仍用 BeginInternal 兜底。
    public int DeleteOrphanFiles(IEnumerable<string> absPaths, string notebookRoot, bool permanent = false)
    {
        var n = 0;
        BeginInternal();
        try
        {
            foreach (var p in absPaths)
            {
                try
                {
                    var full = Path.GetFullPath(p);
                    if (File.Exists(full)) { DeletePath(full, isDir: false, permanent); n++; }
                }
                catch { /* 单个失败跳过，不中断整批 */ }
            }
            PruneEmptyAssetDirs(notebookRoot);
        }
        finally { EndInternal(); }
        return n;
    }

    // 清掉 .mica/assets 下变空的目录（深的先删）。
    private static void PruneEmptyAssetDirs(string notebookRoot)
    {
        var assets = Path.Combine(Path.GetFullPath(notebookRoot), ".mica", "assets");
        if (!Directory.Exists(assets)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(assets, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
    }

    // 复制/剪切粘贴 + 创建副本 + 拖拽 的统一搬运引擎（**同笔记本 / 跨笔记本通用**）。
    //  srcFull = 源绝对路径；srcRoot = 源所属笔记本根（定位源 .mica 资产）；
    //  目标 = 当前笔记本(CurrentFolder) 的 destParentRel 下；isCopy=false 移动。返回目标 relPath（相对当前笔记本，已去重）。
    //  同根（同笔记本）→ 原生 Move（快）；跨根 → 复制 + 删源（避免跨卷 Directory.Move 抛异常）。
    //
    // 关键：.mica 资产目录是 <根>/.mica/assets/<笔记相对路径去扩展名>/（文件夹则 .../<文件夹相对路径>/），把 rel 嵌进了
    // 路径——所以 rel 或根一变，既要搬资产目录，又要把笔记里所有指向旧资产的相对 <img src> 改成新的。笔记里存的相对
    // src 形如「../.mica/assets/<rel>/img.png」，且 ../ 层数随笔记深度变化，故**每篇笔记按自身新旧位置重算完整相对前缀**
    // 再整体替换（不能只换中间段，否则跨层级/跨笔记本后 ../ 层数算错→「文件都对但图裂」）。当前目录/assets 模式的图与
    // .md 同级、随文件夹一起拷走、无需改写。
    public async Task<string> TransferEntryAsync(string srcFull, string srcRoot, string destParentRel, bool isCopy, CancellationToken ct = default)
    {
        if (CurrentFolder is null) throw new InvalidOperationException("No folder open.");
        BeginInternal();
        try
        {
            srcFull = Path.GetFullPath(srcFull);
            srcRoot = Path.GetFullPath(srcRoot);
            var destRoot = Path.GetFullPath(CurrentFolder);
            destParentRel = (destParentRel ?? "").Replace('\\', '/').Trim('/');

            var isDir = Directory.Exists(srcFull);
            var name = Path.GetFileName(srcFull);

            var baseRel = destParentRel.Length == 0 ? name : $"{destParentRel}/{name}";
            var destRel = DedupeRel(baseRel, isDir);
            var destFull = GetFullPath(destRel);
            var destParentDir = Path.GetDirectoryName(destFull);
            if (destParentDir is not null) Directory.CreateDirectory(destParentDir);

            var sameRoot = string.Equals(srcRoot.TrimEnd('\\', '/'), destRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            var physicalMove = !isCopy && sameRoot; // 同根移动才用原生 Move；跨根移动＝复制+删源

            // 1) 本体
            if (physicalMove)
            {
                if (isDir) Directory.Move(srcFull, destFull);
                else File.Move(srcFull, destFull);
            }
            else
            {
                if (isDir) CopyDirectory(srcFull, destFull);
                else File.Copy(srcFull, destFull, overwrite: false);
            }

            // 2+3) .mica 资产搬运 + 改写受影响笔记的 <img src>（支持跨根）。
            var srcRel = Path.GetRelativePath(srcRoot, srcFull).Replace('\\', '/');
            await CarryAssetsAsync(srcRel, srcRoot, destRel, destRoot, srcFull, destFull, isDir, assetMove: physicalMove, ct);

            // 4) 任务1：单个 .md 还要携带其引用的同级/assets 散图（文件夹整体搬运时这些图本就在夹内、随之而走，无需处理）。
            if (!isDir && destFull.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                await CarryScatteredImagesAsync(srcFull, srcRoot, destFull, destRoot, ct);

            // 跨根移动：本体已复制、资产已复制 → 删源本体 + 源 .mica 资产。
            if (!isCopy && !sameRoot)
            {
                if (isDir) Directory.Delete(srcFull, recursive: true); else File.Delete(srcFull);
                DeleteAssetDir(srcRoot, isDir ? srcRel : Path.ChangeExtension(srcRel, null)!);
            }

            return destRel;
        }
        finally { EndInternal(); }
    }

    // 同笔记本便捷重载（保留既有调用方：创建副本/拖拽/同笔记本粘贴）。srcRel 相对当前笔记本。
    public Task<string> TransferEntryAsync(string srcRel, string destParentRel, bool isCopy, CancellationToken ct = default)
    {
        if (CurrentFolder is null) throw new InvalidOperationException("No folder open.");
        return TransferEntryAsync(GetFullPath(srcRel.Replace('\\', '/').Trim('/')), CurrentFolder, destParentRel, isCopy, ct);
    }

    // 搬运笔记/文件夹时联动其 .mica 资产并改写笔记里的 <img src>——被搬运引擎与「重命名」共用，支持两端在不同笔记本根下。
    // assetMove=true 时移动资产目录（同根快路径）、false 时复制（跨根 / 复制粘贴）。
    private async Task CarryAssetsAsync(
        string srcRel, string srcRoot, string destRel, string destRoot,
        string srcFull, string destFull, bool isDir, bool assetMove, CancellationToken ct)
    {
        // .mica 资产目录键（文件去扩展名、文件夹原样）
        var oldKeyRel = (isDir ? srcRel : Path.ChangeExtension(srcRel, null)!).Replace('\\', '/');
        var newKeyRel = (isDir ? destRel : Path.ChangeExtension(destRel, null)!).Replace('\\', '/');

        var oldAssetDir = AssetDir(srcRoot, oldKeyRel);
        var newAssetDir = AssetDir(destRoot, newKeyRel);
        if (Directory.Exists(oldAssetDir)
            && !string.Equals(oldAssetDir, newAssetDir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newAssetDir)!);
            if (Directory.Exists(newAssetDir)) Directory.Delete(newAssetDir, recursive: true); // 目标已存在则覆盖
            if (assetMove) Directory.Move(oldAssetDir, newAssetDir);
            else CopyDirectory(oldAssetDir, newAssetDir);
        }

        // 改写受影响笔记里的 <img src> 前缀（仅 .mica 集中式布局；每篇按自身新旧位置重算完整相对前缀，支持跨根）。
        var notes = isDir
            ? Directory.EnumerateFiles(destFull, "*.md", SearchOption.AllDirectories).ToList()
            : [destFull];
        foreach (var newNoteAbs in notes)
        {
            // 该笔记搬运前的原始绝对路径（在源根下；仅用于算路径，move 后物理已不在也无妨）
            var oldNoteAbs = isDir
                ? Path.Combine(srcFull, Path.GetRelativePath(destFull, newNoteAbs))
                : srcFull;
            var oldPrefix = Path.GetRelativePath(Path.GetDirectoryName(oldNoteAbs)!,
                ResolveMicaAssetDir(oldNoteAbs, srcRoot)).Replace('\\', '/') + "/";
            var newPrefix = Path.GetRelativePath(Path.GetDirectoryName(newNoteAbs)!,
                ResolveMicaAssetDir(newNoteAbs, destRoot)).Replace('\\', '/') + "/";
            if (string.Equals(oldPrefix, newPrefix, StringComparison.Ordinal)) continue;

            var txt = await File.ReadAllTextAsync(newNoteAbs, ct);
            if (txt.Contains(oldPrefix, StringComparison.Ordinal))
                await File.WriteAllTextAsync(newNoteAbs, txt.Replace(oldPrefix, newPrefix), Encoding.UTF8, ct);
        }
    }

    // ===== 任务1：搬运单个 .md 时携带其引用的「同级 / assets 子夹」散图 =====
    // 这些图与 .md 同级（或在兄弟 assets/ 夹）、不在 .mica，单文件移动不会带走 → 旧版丢图。这里逐图把它们
    // 复制到「相对新笔记的同样位置」+ 内容感知去重；只有去重改了名才需改写 .md 的 src。
    // **一律复制不移动**：同级图可能被同目录其它笔记共享，移走会害兄弟笔记；留下的原图最坏成孤儿（无害、可后续清理）。
    // OtherLocal（笔记本内别处的图）不搬文件，只在换目录后把相对 src 重算指回原文件，避免相对路径错位裂图。
    // 由 TransferEntryAsync 在 CarryAssetsAsync（处理 .mica）之后、仅对单个 .md 调用。
    private async Task CarryScatteredImagesAsync(string srcNoteAbs, string srcRoot, string destNoteAbs, string destRoot, CancellationToken ct)
    {
        var srcNoteDir = Path.GetDirectoryName(Path.GetFullPath(srcNoteAbs))!;
        var destNoteDir = Path.GetDirectoryName(Path.GetFullPath(destNoteAbs))!;

        string md;
        try { md = await File.ReadAllTextAsync(destNoteAbs, ct); }
        catch { return; }

        // 用「源笔记位置」解析 → 散图的原始绝对路径正确（图还在旧位置）。
        var refs = ImageRefParser.Parse(md, srcNoteAbs, srcRoot);
        var edits = new List<(int offset, string oldFull, string newFull)>();

        foreach (var r in refs)
        {
            if (r.ResolvedAbs is null || !r.Exists) continue;

            string newRel;
            if (r.Category is ImageCategory.SameDir or ImageCategory.AssetsSub)
            {
                // 相对「源笔记目录」的子路径（保留 assets/ 段）；复制到新笔记下的同样子路径，内容感知去重。
                var relUnderNote = Path.GetRelativePath(srcNoteDir, r.ResolvedAbs).Replace('\\', '/');
                var subDir = Path.GetDirectoryName(relUnderNote)?.Replace('\\', '/') ?? "";
                var destImgDir = subDir.Length == 0 ? destNoteDir : Path.Combine(destNoteDir, subDir.Replace('/', Path.DirectorySeparatorChar));
                var finalName = PlaceImageFileCopy(r.ResolvedAbs, destImgDir);
                newRel = subDir.Length == 0 ? finalName : $"{subDir}/{finalName}";
            }
            else if (r.Category is ImageCategory.OtherLocal)
            {
                // 不搬文件，只把相对 src 重新对准原文件（笔记换了目录，旧相对路径会错位）。
                newRel = Path.GetRelativePath(destNoteDir, r.ResolvedAbs).Replace('\\', '/');
            }
            else continue; // .mica（CarryAssetsAsync 管）/ External / Embedded → 不碰

            // 仅当新相对路径与原始（去包裹+解码后）不同才改写 src。
            if (!string.Equals(newRel, DecodeRel(r.RawSrc), StringComparison.Ordinal))
                edits.Add(MakeSrcEdit(r, newRel));
        }

        if (edits.Count == 0) return;
        var updated = ApplySrcEdits(md, edits);
        if (!string.Equals(updated, md, StringComparison.Ordinal))
            await File.WriteAllTextAsync(destNoteAbs, updated, Encoding.UTF8, ct);
    }

    // 把单个图片**复制**进 destDir：同名同内容→复用（返回现名、不拷）；同名异内容→加 -1/-2 后缀；否则原名拷入。
    // 返回最终文件名（仅名）。源==目标（同目录复制副本场景）直接返回原名。
    private static string PlaceImageFileCopy(string srcAbs, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var name = Path.GetFileName(srcAbs);
        var destPath = Path.Combine(destDir, name);

        if (PathEq(srcAbs, destPath)) return name; // 同目录副本：图就在原地，无需拷

        if (File.Exists(destPath))
        {
            if (FilesEqual(srcAbs, destPath)) return name; // 同名同内容 → 复用
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var i = 1; ; i++)
            {
                var cand = $"{stem}-{i}{ext}";
                var candPath = Path.Combine(destDir, cand);
                if (FilesEqual(srcAbs, candPath)) return cand;     // 后缀候选里已有同内容副本 → 复用
                if (!File.Exists(candPath)) { File.Copy(srcAbs, candPath, overwrite: false); return cand; }
            }
        }

        File.Copy(srcAbs, destPath, overwrite: false);
        return name;
    }

    private static bool PathEq(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    // 两文件内容是否相同（先比长度，再逐块比字节）。任一不存在/异常 → false。
    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sa = fa.OpenRead();
            using var sb = fb.OpenRead();
            var ba = new byte[8192];
            var bb = new byte[8192];
            int ra;
            while ((ra = sa.Read(ba, 0, ba.Length)) > 0)
            {
                var rb = 0;
                while (rb < ra)
                {
                    var n = sb.Read(bb, rb, ra - rb);
                    if (n == 0) return false;
                    rb += n;
                }
                for (var i = 0; i < ra; i++) if (ba[i] != bb[i]) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // RawSrc 去 <…> 包裹 + 解码 → 相对路径字符串（用于和新算出的相对路径比较）。
    private static string DecodeRel(string rawSrc)
    {
        var s = rawSrc.Trim();
        if (s.Length >= 2 && s[0] == '<' && s[^1] == '>') s = s[1..^1].Trim();
        try { s = Uri.UnescapeDataString(s); } catch { /* 非法编码原样 */ }
        return s.Replace('\\', '/');
    }

    // 构造一条 src 改写：把 FullMatch 里的旧 RawSrc 段换成新相对路径（Markdown 且含空格 → <…> 包裹）。
    private static (int offset, string oldFull, string newFull) MakeSrcEdit(ImageRef r, string newRel)
    {
        var newRaw = r.Kind == ImageRefKind.Markdown && newRel.Contains(' ') ? $"<{newRel}>" : newRel;
        var newFull = ReplaceFirst(r.FullMatch, r.RawSrc, newRaw);
        return (r.MatchOffset, r.FullMatch, newFull);
    }

    private static string ReplaceFirst(string text, string search, string replace)
    {
        var i = text.IndexOf(search, StringComparison.Ordinal);
        return i < 0 ? text : text.Remove(i, search.Length).Insert(i, replace);
    }

    // 按 offset 降序原地拼接改写（避免前面替换让后面的 offset 失效）；带安全校验：原位必须正好是 oldFull。
    private static string ApplySrcEdits(string md, List<(int offset, string oldFull, string newFull)> edits)
    {
        foreach (var (offset, oldFull, newFull) in edits.OrderByDescending(e => e.offset))
        {
            if (offset < 0 || offset + oldFull.Length > md.Length) continue;
            if (!string.Equals(md.Substring(offset, oldFull.Length), oldFull, StringComparison.Ordinal)) continue;
            md = md.Remove(offset, oldFull.Length).Insert(offset, newFull);
        }
        return md;
    }

    // ===== P3 下半：一键把存量图迁到「目标存储模式」位置 =====
    // 仅文件模式（2 同级 / 3 共享assets / 4 .mica / 5 独立.assets）可迁移；0/1（不复制/base64）无目标目录概念。
    // base64 内嵌图本轮不处理（抽出与否待用户定）。外部链接/断链跳过。

    // 遍历笔记本下所有 .md（跳 .mica/dotfolder/~$ 临时文件）。
    private static IEnumerable<string> EnumerateNotes(string root)
    {
        List<string> files;
        try { files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).ToList(); }
        catch { return []; }
        return files.Where(f =>
            !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar, '/')
                .Any(s => s.StartsWith('.') || s.StartsWith("~$")));
    }

    public static bool IsFileStorageMode(int mode) => mode is >= 2 and <= 5;

    // 迁移预览（dry-run，不动盘）：数「不在 targetMode 目标位置」的本地图引用数 + 去重字节 + 涉及笔记数。
    public (int images, long bytes, int notes) PlanImageMigration(string root, int targetMode)
    {
        root = Path.GetFullPath(root);
        var images = 0; long bytes = 0; var notes = 0;
        if (!IsFileStorageMode(targetMode)) return (images, bytes, notes);

        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in EnumerateNotes(root))
        {
            string md;
            try { md = File.ReadAllText(note); } catch { continue; }
            var targetDir = Path.GetFullPath(ResolveImageDirForMode(note, root, targetMode));
            var any = false;
            foreach (var r in ImageRefParser.Parse(md, note, root))
            {
                if (r.ResolvedAbs is null || !r.Exists) continue;                     // 外部/内嵌/断链
                if (r.Category is ImageCategory.External or ImageCategory.Embedded) continue;
                if (PathEq(Path.GetDirectoryName(r.ResolvedAbs)!, targetDir)) continue; // 已在目标
                images++; any = true;
                if (counted.Add(Path.GetFullPath(r.ResolvedAbs))) bytes += r.SizeBytes;
            }
            if (any) notes++;
        }
        return (images, bytes, notes);
    }

    // 执行迁移：每篇笔记的本地图复制到 targetMode 目标位置 + 内容感知去重 + 改写 src；迁移后**全局再无人引用**的
    // 源文件移入回收站（用全局引用计数判定，安全可恢复）。返回 (迁移图次数, 改写笔记数)。
    public async Task<(int images, int notes)> MigrateImagesAsync(
        string root, int targetMode, IProgress<double>? progress, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        if (!IsFileStorageMode(targetMode)) return (0, 0);

        BeginInternal();
        try
        {
            var noteList = EnumerateNotes(root).ToList();

            // 第一遍：全局引用计数（每个本地图被多少处引用）——迁移后据此判源文件是否还有人用、能否删。
            var refCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var note in noteList)
            {
                string md0;
                try { md0 = await File.ReadAllTextAsync(note, ct); } catch { continue; }
                foreach (var r in ImageRefParser.Parse(md0, note, root))
                    if (r.ResolvedAbs is not null && r.Exists
                        && r.Category is not (ImageCategory.External or ImageCategory.Embedded))
                    {
                        var key = Path.GetFullPath(r.ResolvedAbs);
                        refCount[key] = refCount.GetValueOrDefault(key) + 1;
                    }
            }

            var movedImages = 0; var touchedNotes = 0;
            var maybeDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var ni = 0; ni < noteList.Count; ni++)
            {
                ct.ThrowIfCancellationRequested();
                var note = noteList[ni];
                string md;
                try { md = await File.ReadAllTextAsync(note, ct); } catch { continue; }
                var noteDir = Path.GetDirectoryName(note)!;
                var targetDir = Path.GetFullPath(ResolveImageDirForMode(note, root, targetMode));
                var edits = new List<(int, string, string)>();

                foreach (var r in ImageRefParser.Parse(md, note, root))
                {
                    if (r.ResolvedAbs is null || !r.Exists) continue;
                    if (r.Category is ImageCategory.External or ImageCategory.Embedded) continue;
                    var srcAbs = Path.GetFullPath(r.ResolvedAbs);
                    if (PathEq(Path.GetDirectoryName(srcAbs)!, targetDir)) continue; // 已在目标

                    var finalName = PlaceImageFileCopy(srcAbs, targetDir);
                    var newRel = Path.GetRelativePath(noteDir, Path.Combine(targetDir, finalName)).Replace('\\', '/');
                    if (!string.Equals(newRel, DecodeRel(r.RawSrc), StringComparison.Ordinal))
                        edits.Add(MakeSrcEdit(r, newRel));
                    movedImages++;
                    // 这处引用已不再指向 srcAbs → 计数减一；归零候选删（最后再统一按最终计数确认）。
                    if (refCount.TryGetValue(srcAbs, out var c)) { refCount[srcAbs] = c - 1; if (c - 1 <= 0) maybeDelete.Add(srcAbs); }
                }

                if (edits.Count > 0)
                {
                    var updated = ApplySrcEdits(md, edits);
                    if (!string.Equals(updated, md, StringComparison.Ordinal))
                    {
                        await File.WriteAllTextAsync(note, updated, Encoding.UTF8, ct);
                        touchedNotes++;
                    }
                }
                progress?.Report((ni + 1.0) / Math.Max(1, noteList.Count));
            }

            // 迁移后全局无人引用的源文件 → 回收站（最终计数仍 >0 的不删，绝不误删仍被引用的图）。
            foreach (var src in maybeDelete)
            {
                if (refCount.GetValueOrDefault(src) > 0) continue;
                try { if (File.Exists(src)) DeletePath(src, isDir: false, permanent: false); } catch { /* 单个失败跳过 */ }
            }
            PruneEmptyAssetDirs(root); // 清掉迁空的 .mica/assets 子目录

            return (movedImages, touchedNotes);
        }
        finally { EndInternal(); }
    }

    // <根>/.mica/assets/<keyRel>/ 的绝对路径。
    private static string AssetDir(string root, string keyRel) =>
        Path.Combine(Path.GetFullPath(root), ".mica", "assets", keyRel.Replace('/', Path.DirectorySeparatorChar));

    private static void DeleteAssetDir(string root, string keyRel)
    {
        var dir = AssetDir(root, keyRel);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // 迁移整个笔记本文件夹（含 .mica，所有笔记+图）到 newRoot。**整夹搬运→笔记内相对 <img src> 全不变、无需改写**。
    //  同卷 → 原生 Directory.Move（瞬时）；跨卷 → 逐文件复制（progress 报 0..1）+ 删源（Directory.Move 跨卷会抛）。
    //  若 oldRoot 正是当前被监视的活动笔记本，先停监视避免移动时与 watcher 冲突——**调用方迁移成功后须 OpenFolder(newRoot) 重挂**。
    public async Task MigrateFolderAsync(string oldRoot, string newRoot, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        oldRoot = Path.GetFullPath(oldRoot);
        newRoot = Path.GetFullPath(newRoot);
        if (!Directory.Exists(oldRoot)) throw new DirectoryNotFoundException(oldRoot);
        if (Directory.Exists(newRoot)) throw new IOException($"目标已存在：{newRoot}");

        var repoint = CurrentFolder is not null && string.Equals(
            Path.GetFullPath(CurrentFolder).TrimEnd('\\', '/'), oldRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        if (repoint) StopWatching();
        BeginInternal();
        try
        {
            var parent = Path.GetDirectoryName(newRoot);
            if (parent is not null) Directory.CreateDirectory(parent);

            var sameVolume = string.Equals(Path.GetPathRoot(oldRoot), Path.GetPathRoot(newRoot), StringComparison.OrdinalIgnoreCase);
            if (sameVolume)
            {
                Directory.Move(oldRoot, newRoot);
                progress?.Report(1.0);
            }
            else
            {
                await CopyTreeWithProgressAsync(oldRoot, newRoot, progress, ct);
                Directory.Delete(oldRoot, recursive: true);
            }
        }
        catch
        {
            if (repoint) StartWatching(oldRoot); // 失败回滚监视（源大概率还在）
            throw;
        }
        finally { EndInternal(); }
    }

    // 逐文件复制整棵树并报告进度（跨卷迁移用）。先建好所有子目录，再按文件数推进 0..1。
    private static async Task CopyTreeWithProgressAsync(string src, string dst, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));

        var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToList();
        var done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(dst, Path.GetRelativePath(src, f));
            await Task.Run(() => File.Copy(f, target, overwrite: false), ct);
            progress?.Report((double)++done / Math.Max(1, files.Count));
        }
    }

    // Build a FileNode (full subtree for a folder) for one existing path — used to splice a
    // freshly copied/pasted entry into the tree without a full reload.
    public FileNode? BuildNode(string relPath)
    {
        if (CurrentFolder is null) return null;
        var rel = relPath.Replace('\\', '/');
        var full = GetFullPath(rel);
        if (Directory.Exists(full))
            return new FileNode { Name = Path.GetFileName(rel), RelPath = rel, IsDirectory = true, Children = ScanDirectory(full, CurrentFolder) };
        if (File.Exists(full))
            return new FileNode { Name = Path.GetFileName(rel), RelPath = rel, IsDirectory = false };
        return null;
    }

    // Append " (i)" until the rel path is free (mirrors CreateFileAsync/CreateFolder dedupe).
    private string DedupeRel(string rel, bool isDir)
    {
        bool Taken(string r) => isDir ? Directory.Exists(GetFullPath(r)) : File.Exists(GetFullPath(r));
        if (!Taken(rel)) return rel;
        var parent = (Path.GetDirectoryName(rel) ?? "").Replace('\\', '/');
        var stem = isDir ? Path.GetFileName(rel) : Path.GetFileNameWithoutExtension(rel);
        var ext = isDir ? "" : Path.GetExtension(rel);
        for (var i = 1; ; i++)
        {
            var cand = (parent.Length == 0 ? "" : $"{parent}/") + $"{stem} ({i}){ext}";
            if (!Taken(cand)) return cand;
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in Directory.EnumerateFileSystemEntries(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(src, entry));
            if (Directory.Exists(entry)) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private static string SanitizeExt(string ext)
    {
        ext = (ext ?? "").TrimStart('.').ToLowerInvariant();
        ext = new string(ext.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(ext) ? "png" : ext;
    }

    // 尾随时间戳（- 后跟 14~17 位数字：yyyyMMddHHmmss[ fff]）。重复粘贴同一张已带时间戳的图时用于剥旧戳、换新戳，
    // 避免名字无限变长（clip-2026…-2026… 这种）。
    private static readonly Regex TrailingTimestamp = new(@"-\d{14,17}$", RegexOptions.Compiled);

    // 新图文件名 = 干净基名 + 「-时间戳(到毫秒)」。**时间戳保唯一**（对标 Typora，从此新图天生不撞名）；
    // DedupePath 仍作同毫秒批量粘贴的兜底。已带尾随时间戳的源名先剥掉旧戳再换新戳（不叠加）。
    private static string MakeImageFileName(string? sourceName, string ext)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName)
            ? null
            : Path.GetFileNameWithoutExtension(sourceName);
        if (!string.IsNullOrEmpty(baseName))
        {
            var invalid = Path.GetInvalidFileNameChars();
            baseName = new string(baseName.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            baseName = TrailingTimestamp.Replace(baseName, ""); // 剥掉已有尾随时间戳，换新的
        }
        if (string.IsNullOrEmpty(baseName)) baseName = "image";
        return $"{baseName}-{DateTime.Now:yyyyMMddHHmmssfff}.{ext}";
    }

    private static string DedupePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    private static string MimeForExt(string ext) => ext switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "svg" => "image/svg+xml",
        "bmp" => "image/bmp",
        "avif" => "image/avif",
        "tif" or "tiff" => "image/tiff",
        _ => "application/octet-stream",
    };

    private string GetFullPath(string relPath)
    {
        // Editor tabs pass an ALREADY-absolute path (so an open file keeps saving/loading
        // correctly after the user switches the active notebook). Honor it as-is and don't
        // require a folder to be open.
        if (Path.IsPathRooted(relPath)) return relPath;
        if (CurrentFolder is null)
            throw new InvalidOperationException("No folder open.");
        return Path.Combine(CurrentFolder, relPath);
    }

    private static ObservableCollection<FileNode> ScanDirectory(string dirPath, string root)
    {
        var dirs = new List<FileNode>();
        var files = new List<FileNode>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(dirPath))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.') || name.StartsWith("~$")) continue;

            var relPath = Path.GetRelativePath(root, entry).Replace('\\', '/');

            if (Directory.Exists(entry))
            {
                dirs.Add(new FileNode
                {
                    Name = name,
                    RelPath = relPath,
                    IsDirectory = true,
                    Children = ScanDirectory(entry, root)
                });
            }
            else if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(new FileNode
                {
                    Name = name,
                    RelPath = relPath,
                    IsDirectory = false
                });
            }
        }

        dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var result = new ObservableCollection<FileNode>();
        foreach (var d in dirs) result.Add(d);
        foreach (var f in files) result.Add(f);
        return result;
    }
}
