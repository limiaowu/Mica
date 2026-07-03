using System.Text.RegularExpressions;
using Mica.Models;

namespace Mica.Services;

// 笔记本维护子系统的地基：把一篇 .md 文本解析成「图片引用」列表。
// 纯静态函数、无副作用、不读盘内容（只 File.Exists/长度查目标）。所有维护功能（孤儿清理、迁移、导出、
// 搬运携带散图）都建立在它之上。
//
// 承重坑（务必处理）：
//  ① 两种语法 ![alt](src) 与 <img src="…">；
//  ② Markdown 路径可能 <带空格的路径> 包裹、或 %20 编码 → 解析前去包裹 + UnescapeDataString；
//  ③ 改写时按 ImageRef.RawSrc **原样精确替换**（这里只负责解析、保留 RawSrc，改写在 P3）；
//  ④ .mica 的 src 形如「../.mica/assets/<rel>/img.png」，../ 层数随笔记深度变化 → 解析成绝对路径再归类。
public static class ImageRefParser
{
    // Markdown 图片：![alt](url "title")。url 可能被 <...> 包裹；后面可能跟 "title"/'title'/(title)。
    // url 组：要么 <...>（含空格），要么连续非空白非右括号串。
    private static readonly Regex MdImage = new(
        @"!\[(?<alt>[^\]]*)\]\(\s*(?<url><[^>]*>|[^)\s]+)(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*\)",
        RegexOptions.Compiled);

    // <img ... src="..."> 或 src='...'（大小写不敏感）。匹配**整个标签**（到 >），FullMatch 才能用于整段删除。
    private static readonly Regex HtmlImgSrc = new(
        @"<img\b[^>]*?\bsrc\s*=\s*(?:""(?<u>[^""]*)""|'(?<u2>[^']*)')[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 形如 scheme://… 的 URI（http/https/file/ftp…）。
    private static readonly Regex UriScheme = new(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://", RegexOptions.Compiled);

    // 解析一篇笔记。noteAbsPath = 笔记绝对路径；notebookRoot = 所属笔记本根（定位 .mica）。
    public static List<ImageRef> Parse(string md, string noteAbsPath, string notebookRoot)
    {
        var refs = new List<ImageRef>();
        if (string.IsNullOrEmpty(md)) return refs;

        var noteFull = Path.GetFullPath(noteAbsPath);
        var noteDir = Path.GetDirectoryName(noteFull)!;
        var rootFull = Path.GetFullPath(notebookRoot);
        // 本篇的 <笔记名>.assets 目录（NoteAssets 模式）——用笔记文件名拼，故在此算好传下去。
        var noteAssetsDir = Path.Combine(noteDir, Path.GetFileNameWithoutExtension(noteFull) + ".assets");

        foreach (Match m in MdImage.Matches(md))
            refs.Add(Build(ImageRefKind.Markdown, m.Groups["url"].Value, m.Value, m.Index, noteDir, rootFull, noteAssetsDir));

        foreach (Match m in HtmlImgSrc.Matches(md))
        {
            var raw = m.Groups["u"].Success ? m.Groups["u"].Value : m.Groups["u2"].Value;
            refs.Add(Build(ImageRefKind.Html, raw, m.Value, m.Index, noteDir, rootFull, noteAssetsDir));
        }

        return refs;
    }

    private static ImageRef Build(ImageRefKind kind, string rawSrc, string fullMatch, int matchOffset, string noteDir, string rootFull, string noteAssetsDir)
    {
        var (cat, abs) = Categorize(rawSrc, noteDir, rootFull, noteAssetsDir);
        var exists = false;
        long size = 0;
        if (abs is not null && cat is not ImageCategory.Embedded)
        {
            try
            {
                var fi = new FileInfo(abs);
                if (fi.Exists) { exists = true; size = fi.Length; }
            }
            catch { /* 非法路径等 → 视为不存在 */ }
        }

        return new ImageRef
        {
            Kind = kind,
            RawSrc = rawSrc,
            FullMatch = fullMatch,
            Category = cat,
            ResolvedAbs = abs,
            Exists = exists,
            SizeBytes = size,
            MatchOffset = matchOffset,
        };
    }

    // 归类 + 解析绝对路径。External/Embedded 不算「本地图」，abs 可能为 null（纯 URI/内嵌）或越界绝对路径。
    private static (ImageCategory cat, string? abs) Categorize(string rawSrc, string noteDir, string rootFull, string? noteAssetsDir)
    {
        var s = rawSrc.Trim();
        if (s.Length >= 2 && s[0] == '<' && s[^1] == '>') s = s[1..^1].Trim();   // 去 <...> 包裹
        if (s.Length == 0) return (ImageCategory.External, null);

        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return (ImageCategory.Embedded, null);
        if (UriScheme.IsMatch(s))
            return (ImageCategory.External, null);

        var decoded = Uri.UnescapeDataString(s);

        string abs;
        try
        {
            abs = Path.IsPathRooted(decoded)
                ? Path.GetFullPath(decoded)
                : Path.GetFullPath(Path.Combine(noteDir, decoded));
        }
        catch
        {
            return (ImageCategory.External, null); // 非法路径，当外部不碰
        }

        if (!IsUnder(rootFull, abs))
            return (ImageCategory.External, abs); // 笔记本外

        if (IsUnder(Path.Combine(rootFull, ".mica", "assets"), abs))
            return (ImageCategory.Mica, abs);

        // <笔记目录>/<笔记名>.assets/ —— 每篇独立同级夹（排在 assets/ 共享夹之前判，互不包含）。
        if (noteAssetsDir is not null && IsUnder(noteAssetsDir, abs))
            return (ImageCategory.NoteAssets, abs);

        if (IsUnder(Path.Combine(noteDir, "assets"), abs))
            return (ImageCategory.AssetsSub, abs);

        var parent = Path.GetDirectoryName(abs);
        if (parent is not null && PathEq(parent, noteDir))
            return (ImageCategory.SameDir, abs);

        return (ImageCategory.OtherLocal, abs);
    }

    // child 是否在 parent 之下（或相等）。两端已规范化绝对路径。
    private static bool IsUnder(string parent, string child)
    {
        var p = parent.TrimEnd('\\', '/');
        var c = child.TrimEnd('\\', '/');
        if (PathEq(p, c)) return true;
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(p + '/', StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEq(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
