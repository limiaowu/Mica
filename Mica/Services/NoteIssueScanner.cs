using System.Text.RegularExpressions;

namespace Mica.Services;

// 把一篇 .md 源文本扫成「块级节点」清单：代码块 / 公式块 / 表格 / 图册，按**源码顺序**逐类编号（Index），
// 并判定是否为空块。维护页据此：① 收集空块作为待清理项；② 用 per-kind Index 做精确定位
// （web 端 querySelectorAll 返回 DOM 文档序 == 这里的源码序，故下标直接对得上）。
//
// 关键点：必须先识别代码/公式围栏，因为围栏内部出现的 <table>/$$/图片都不会被编辑器渲染成真实节点——
// 它们既不该算空块，也不该占用 DOM 序号。故 ScanBlocks 先线性扫围栏，表格/图册再用正则但跳过落在围栏内的。
public sealed record ScanBlock(string Kind, int Index, int Start, int Length, string FullMatch, bool Empty);

public static class NoteIssueScanner
{
    private static readonly Regex TableRe = new(@"<table\b[\s\S]*?</table>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GalleryRe = new(@"<div\b[^>]*\bdata-mica-gallery\b[\s\S]*?</div>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImgTagRe = new(@"<img\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagRe = new(@"<[^>]*>", RegexOptions.Compiled);

    public static IReadOnlyList<ScanBlock> ScanBlocks(string md)
    {
        var blocks = new List<ScanBlock>();
        if (string.IsNullOrEmpty(md)) return blocks;

        var lines = md.Split('\n');
        var lineStart = new int[lines.Length];
        var acc = 0;
        for (var k = 0; k < lines.Length; k++) { lineStart[k] = acc; acc += lines[k].Length + 1; }

        // 代码/公式块覆盖的字符区间——给后面的表格扫描排除（围栏内的 <table> 不渲染）。
        var fenced = new List<(int s, int e)>();
        var codeIdx = 0;
        var mathIdx = 0;

        var i = 0;
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();

            // ① 代码围栏 ```… / ~~~…
            var fence = FenceMarker(trimmed);
            if (fence is not null)
            {
                var start = lineStart[i];
                var j = i + 1;
                var empty = true;
                while (j < lines.Length)
                {
                    if (IsClosingFence(lines[j].TrimStart(), fence)) break;
                    if (lines[j].Trim().Length > 0) empty = false;
                    j++;
                }
                var end = j < lines.Length ? lineStart[j] + lines[j].Length : md.Length;
                blocks.Add(new ScanBlock("code", codeIdx++, start, end - start, md[start..end], empty));
                fenced.Add((start, end));
                i = j + 1;
                continue;
            }

            // ② 公式块 $$…$$（行首 $$ 起；可能同行闭合，也可能跨行）
            if (trimmed.StartsWith("$$"))
            {
                var start = lineStart[i];
                var afterOpen = trimmed[2..];
                var sameLineClose = afterOpen.IndexOf("$$", StringComparison.Ordinal);
                int end;
                bool empty;
                if (sameLineClose >= 0)
                {
                    empty = afterOpen[..sameLineClose].Trim().Length == 0;
                    end = lineStart[i] + lines[i].Length;
                    i++;
                }
                else
                {
                    empty = afterOpen.Trim().Length == 0;
                    var j = i + 1;
                    while (j < lines.Length)
                    {
                        if (lines[j].TrimStart().StartsWith("$$")) break;
                        if (lines[j].Trim().Length > 0) empty = false;
                        j++;
                    }
                    end = j < lines.Length ? lineStart[j] + lines[j].Length : md.Length;
                    i = j + 1;
                }
                blocks.Add(new ScanBlock("math", mathIdx++, start, end - start, md[start..end], empty));
                fenced.Add((start, end));
                continue;
            }

            i++;
        }

        // ③ 表格：正则全文匹配，跳过落在代码/公式围栏内的。
        var tableIdx = 0;
        foreach (Match m in TableRe.Matches(md))
        {
            if (InRange(m.Index, fenced)) continue;
            blocks.Add(new ScanBlock("table", tableIdx++, m.Index, m.Length, m.Value, TableIsEmpty(m.Value)));
        }

        // ④ 图册：<div data-mica-gallery>…</div>，空＝内部无 <img>。同样跳过围栏内的。
        var galleryIdx = 0;
        foreach (Match m in GalleryRe.Matches(md))
        {
            if (InRange(m.Index, fenced)) continue;
            blocks.Add(new ScanBlock("gallery", galleryIdx++, m.Index, m.Length, m.Value, !ImgTagRe.IsMatch(m.Value)));
        }

        return blocks;
    }

    // 代码/公式块覆盖的字符区间（供调用方排除其中的“伪图片”引用）。
    public static List<(int s, int e)> FencedRanges(IReadOnlyList<ScanBlock> blocks) =>
        blocks.Where(b => b.Kind != "table").Select(b => (b.Start, b.Start + b.Length)).ToList();

    public static bool InRange(int offset, List<(int s, int e)> ranges)
    {
        foreach (var (s, e) in ranges)
            if (offset >= s && offset < e) return true;
        return false;
    }

    // 行首（去缩进后）的围栏标记：连续 >=3 个 ` 或 ~，返回该标记串；否则 null。
    private static string? FenceMarker(string trimmed)
    {
        if (trimmed.StartsWith("```")) return new string('`', RunLength(trimmed, '`'));
        if (trimmed.StartsWith("~~~")) return new string('~', RunLength(trimmed, '~'));
        return null;
    }

    private static int RunLength(string s, char c)
    {
        var n = 0;
        while (n < s.Length && s[n] == c) n++;
        return n;
    }

    // 闭合围栏：同种字符 >= 开围栏长度，且其后只剩空白（闭合围栏不带 info string）。
    private static bool IsClosingFence(string trimmed, string fence)
    {
        var c = fence[0];
        var n = RunLength(trimmed, c);
        return n >= fence.Length && trimmed[n..].Trim().Length == 0;
    }

    // 表格是否为空：剥掉所有标签 + 常见空白实体，余下无非空白字符即空表。
    private static bool TableIsEmpty(string html)
    {
        var inner = TagRe.Replace(html, "")
            .Replace("&nbsp;", " ")
            .Replace("&#160;", " ")
            .Replace("&#xa0;", " ");
        return inner.Trim().Length == 0;
    }
}
