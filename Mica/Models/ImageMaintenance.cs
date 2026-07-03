namespace Mica.Models;

// 笔记本维护子系统（图片规范化 / 孤儿清理 / 导出）的只读数据模型。
// 这一层全是纯 DTO：由 ImageRefParser 解析单篇、NotebookMaintenanceService.ScanNotebook 汇总成报告，
// 上层 UI（计划中的维护覆盖页）只读展示，不在这里做任何文件系统副作用。

// 引用写法：Markdown 的 ![alt](src) 还是 HTML 的 <img src>。改写时按各自语法处理。
public enum ImageRefKind
{
    Markdown,
    Html,
}

// 图片引用按「存储位置」分类——决定它能不能被安全清理/迁移。
//   Mica      = <根>/.mica/assets/<笔记rel>/ 下（Mica 独占管理，孤儿清理只对它安全）
//   SameDir   = 与 .md 同级目录
//   AssetsSub = <笔记目录>/assets/ 子文件夹（同级笔记共享）
//   NoteAssets= <笔记目录>/<笔记名>.assets/ 子文件夹（每篇独立、随笔记同级）
//   OtherLocal= 笔记本内、但不属于上面几种约定位置（少见，谨慎对待）
//   Embedded  = data:base64 内嵌，无独立文件
//   External  = http(s)/file 等 URI，或解析到笔记本外的绝对路径（一律不碰）
public enum ImageCategory
{
    Mica,
    SameDir,
    AssetsSub,
    NoteAssets,
    OtherLocal,
    Embedded,
    External,
}

// 一条本地/外部图片引用的解析结果。改写（P3）时**按 RawSrc 精确替换**，避免编码/括号问题。
// 注：这些 DTO 的属性不用 required——它们会被 XAML 类型信息生成器（经绑定类型的公开属性传递可达）
// 生成无参激活器，required 成员会让生成代码编译失败。用默认值 + 对象初始化器即可。
public sealed class ImageRef
{
    public ImageRefKind Kind { get; init; }

    // .md 里**原样**的 src 字符串（Markdown 含可能的 <...> 包裹；HTML 为引号内内容）。改写按它替换。
    public string RawSrc { get; init; } = "";

    public ImageCategory Category { get; init; }

    // 整段原文匹配（Markdown 为完整 ![alt](src)；HTML 为完整 <img …>）。清理某条引用时按它从 .md 删除。
    public string FullMatch { get; init; } = "";

    // 解析成的绝对文件路径（本地图才有；Embedded/纯 URI External 为 null）。已规范化 ../。
    public string? ResolvedAbs { get; init; }

    // 本地图文件在不在（找断链）。Embedded/External 恒 false（无对应文件需校验）。
    public bool Exists { get; init; }

    // 存在的本地图文件大小（字节）；否则 0。
    public long SizeBytes { get; init; }

    // 该引用在 .md 源文本里的起始字符偏移（用于按源码序给图片编 DOM 序号，供「定位」精确滚动）。
    public int MatchOffset { get; init; }
}

// 一条「待清理项」的类型。断链引用 + 各类空块——都能在维护页里定位到原处或一键清理。
public enum NoteIssueKind
{
    BrokenRef,      // 断链：本地图引用，但文件不存在
    EmptyCode,      // 空代码块（```…``` 内无内容）
    EmptyMath,      // 空公式块（$$…$$ 内无内容）
    EmptyTable,     // 空表格（<table> 所有单元格为空）
    EmptyGallery,   // 空图册（<div data-mica-gallery> 内无 <img>）
}

// 一条待清理项。统一断链与空块：FullMatch 用于精确删除（从 .md 移除整段原文）；
// RevealKind/RevealIndex 用于「定位」——web 端按 DOM 文档序滚到第 RevealIndex 个 RevealKind 节点。
public sealed class NoteIssue
{
    public NoteIssueKind Kind { get; init; }
    public string NoteRelPath { get; init; } = "";
    public string NoteAbsPath { get; init; } = "";
    public string FullMatch { get; init; } = "";

    // 定位用：web 的节点选择器类别（image/code/math/table）+ 同类节点里的 0 基序号（文档序）。
    public string RevealKind { get; init; } = "";
    public int RevealIndex { get; init; }

    // 列表里显示的小细节（断链=源 src；空块=空白提示）。
    public string Detail { get; init; } = "";
}

// 单篇笔记的图片信息。
public sealed class NoteImageInfo
{
    public string NoteAbsPath { get; init; } = "";
    public string NoteRelPath { get; init; } = "";   // 相对笔记本根，正斜杠
    public List<ImageRef> Refs { get; init; } = [];

    // 本篇用到的存储模式集合（>1 即「混用」，是不一致报告的重点）。
    public HashSet<ImageCategory> Categories { get; init; } = [];
}

// 某一类图片的汇总（计数 + 去重后的总大小）。
public sealed class CategoryStat
{
    public int RefCount { get; set; }          // 引用次数（同图被多处引用算多次）
    public int DistinctFiles { get; set; }     // 去重后的不同文件数（仅本地图有意义）
    public long TotalBytes { get; set; }       // 去重后的总大小
}

// 一个 .mica/assets 下未被任何笔记引用的孤儿文件。
public sealed class OrphanFile
{
    public string AbsPath { get; init; } = "";
    public string RelFromRoot { get; init; } = "";  // 相对笔记本根，正斜杠
    public long SizeBytes { get; init; }
}

// 一条断链引用（本地图但文件不存在）。带笔记绝对路径 + 整段原文，供「定位」打开笔记、「清理」删除该引用。
public sealed class BrokenRef
{
    public string NoteRelPath { get; init; } = "";
    public string NoteAbsPath { get; init; } = "";
    public string RawSrc { get; init; } = "";
    public string FullMatch { get; init; } = "";
    public string? ResolvedAbs { get; init; }
}

// 整个笔记本的图片体检报告（只读）。
public sealed class NotebookImageReport
{
    public string Root { get; init; } = "";
    public string NotebookName { get; init; } = "";

    public List<NoteImageInfo> Notes { get; init; } = [];
    public Dictionary<ImageCategory, CategoryStat> ByCategory { get; init; } = [];

    public List<BrokenRef> Broken { get; init; } = [];
    public List<OrphanFile> Orphans { get; init; } = [];

    // 统一的「待清理项」（断链 + 各类空块），维护页用它渲染一个列表。Broken 保留供计数/向后兼容。
    public List<NoteIssue> Issues { get; init; } = [];

    public int NoteCount => Notes.Count;
    public int EmbeddedCount { get; set; }

    // 空块（非断链）的待清理项数量。
    public int EmptyBlockCount => Issues.Count(i => i.Kind != NoteIssueKind.BrokenRef);

    // 混用多种存储模式的笔记篇数（Categories 里本地三类 Mica/SameDir/AssetsSub/OtherLocal 出现 >1 种）。
    public int MixedNoteCount { get; set; }

    public long OrphanBytes => Orphans.Sum(o => o.SizeBytes);
}
