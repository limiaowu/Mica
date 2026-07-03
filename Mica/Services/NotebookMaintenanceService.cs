using Mica.Models;

namespace Mica.Services;

// 笔记本维护子系统的「只读体检」层（P1 地基）。遍历一个笔记本根下所有 .md，用 ImageRefParser 解析，
// 汇总成 NotebookImageReport：各存储模式分布、断链、孤儿、内嵌、混用统计。
// **纯读**：不删不改任何文件（清理/迁移/导出是后续分期，会另起带二次确认+进度的方法）。
public sealed class NotebookMaintenanceService
{
    // 扫描一个笔记本。root = 笔记本根目录；name 仅用于报告展示。
    public NotebookImageReport ScanNotebook(string root, string name = "")
    {
        var rootFull = Path.GetFullPath(root);
        var report = new NotebookImageReport { Root = rootFull, NotebookName = name };
        if (!Directory.Exists(rootFull)) return report;

        // 1) 遍历所有笔记（跳过 .mica / dotfile 目录 / ~$ 临时文件），逐篇解析。
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 被引用到的本地图绝对路径
        foreach (var md in EnumerateNotes(rootFull))
        {
            string text;
            try { text = File.ReadAllText(md); }
            catch { continue; }

            var refs = ImageRefParser.Parse(text, md, rootFull);
            var rel = Path.GetRelativePath(rootFull, md).Replace('\\', '/');
            var info = new NoteImageInfo
            {
                NoteAbsPath = md,
                NoteRelPath = rel,
                Refs = refs,
            };

            // 存储分类 / 内嵌计数 / 被引用集合（与源码顺序无关，沿用原逻辑）。
            foreach (var r in refs)
            {
                info.Categories.Add(r.Category);
                if (r.ResolvedAbs is not null && r.Category is not ImageCategory.Embedded)
                    referenced.Add(Path.GetFullPath(r.ResolvedAbs));

                if (r.Category is ImageCategory.Embedded)
                    report.EmbeddedCount++;
            }

            // 待清理项（统一断链 + 空块），定位序号必须与编辑器 DOM 文档序一致 → 按源码序处理。
            var blocks = NoteIssueScanner.ScanBlocks(text);
            var fenced = NoteIssueScanner.FencedRanges(blocks);

            // ① 断链：按源码序数图片 DOM 序号；落在代码/公式围栏内的“伪图片”不渲染、跳过（不占序号、不算断链）。
            var imgDom = 0;
            foreach (var r in refs.OrderBy(r => r.MatchOffset))
            {
                if (NoteIssueScanner.InRange(r.MatchOffset, fenced)) continue;
                if (IsLocal(r.Category) && !r.Exists)
                {
                    report.Issues.Add(new NoteIssue
                    {
                        Kind = NoteIssueKind.BrokenRef,
                        NoteRelPath = rel,
                        NoteAbsPath = md,
                        FullMatch = r.FullMatch,
                        RevealKind = "image",
                        RevealIndex = imgDom,
                        Detail = r.RawSrc,
                    });
                    report.Broken.Add(new BrokenRef
                    {
                        NoteRelPath = rel,
                        NoteAbsPath = md,
                        RawSrc = r.RawSrc,
                        FullMatch = r.FullMatch,
                        ResolvedAbs = r.ResolvedAbs,
                    });
                }
                imgDom++;
            }

            // ② 空块（代码 / 公式 / 表格）。
            foreach (var b in blocks)
            {
                if (!b.Empty) continue;
                var (kind, detail) = b.Kind switch
                {
                    "code" => (NoteIssueKind.EmptyCode, "空代码块"),
                    "math" => (NoteIssueKind.EmptyMath, "空公式块"),
                    "table" => (NoteIssueKind.EmptyTable, "空表格"),
                    _ => (NoteIssueKind.EmptyGallery, "空图册"),
                };
                report.Issues.Add(new NoteIssue
                {
                    Kind = kind,
                    NoteRelPath = rel,
                    NoteAbsPath = md,
                    FullMatch = b.FullMatch,
                    RevealKind = b.Kind,
                    RevealIndex = b.Index,
                    Detail = detail,
                });
            }

            if (info.Categories.Count(IsLocal) > 1) report.MixedNoteCount++;
            report.Notes.Add(info);
        }

        // 2) 各类汇总（本地图按去重文件计大小，避免同图多引用重复计）。
        AggregateByCategory(report);

        // 3) .mica/assets 下未被任何笔记引用的孤儿文件（仅此目录可安全判定——它是 Mica 独占管理的）。
        CollectOrphans(rootFull, referenced, report);

        return report;
    }

    // 枚举笔记本下所有 .md，跳过 .mica、dotfile 目录、~$ 临时文件。
    private static IEnumerable<string> EnumerateNotes(string root)
    {
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var f in all)
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.Split('/').Any(seg => seg.StartsWith('.') || seg.StartsWith("~$"))) continue;
            yield return f;
        }
    }

    private static bool IsLocal(ImageCategory c) =>
        c is ImageCategory.Mica or ImageCategory.SameDir or ImageCategory.AssetsSub
            or ImageCategory.NoteAssets or ImageCategory.OtherLocal;

    private static void AggregateByCategory(NotebookImageReport report)
    {
        // 每类去重后的文件集合（本地图），用于算 DistinctFiles/TotalBytes。
        var seenPerCat = new Dictionary<ImageCategory, HashSet<string>>();
        foreach (var note in report.Notes)
            foreach (var r in note.Refs)
            {
                if (!report.ByCategory.TryGetValue(r.Category, out var stat))
                    report.ByCategory[r.Category] = stat = new CategoryStat();
                stat.RefCount++;

                if (IsLocal(r.Category) && r.ResolvedAbs is not null && r.Exists)
                {
                    if (!seenPerCat.TryGetValue(r.Category, out var seen))
                        seenPerCat[r.Category] = seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (seen.Add(Path.GetFullPath(r.ResolvedAbs)))
                    {
                        stat.DistinctFiles++;
                        stat.TotalBytes += r.SizeBytes;
                    }
                }
            }
    }

    private static void CollectOrphans(string root, HashSet<string> referenced, NotebookImageReport report)
    {
        var assetsRoot = Path.Combine(root, ".mica", "assets");
        if (!Directory.Exists(assetsRoot)) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories); }
        catch { return; }

        foreach (var f in files)
        {
            var full = Path.GetFullPath(f);
            if (referenced.Contains(full)) continue;
            long size = 0;
            try { size = new FileInfo(full).Length; } catch { /* ignore */ }
            report.Orphans.Add(new OrphanFile
            {
                AbsPath = full,
                RelFromRoot = Path.GetRelativePath(root, full).Replace('\\', '/'),
                SizeBytes = size,
            });
        }
    }
}
