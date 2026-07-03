// 源码模式空行保真（对标 Typora）
//
// 问题：CommonMark 把块之间的连续空行折叠成「1 个分隔空行」——用户多敲的空行在「源码 ↔ 渲染」
// 往返、以及保存重开后全部丢失（解析侧吞、序列化侧又不还原）。
//
// 方案（不污染 .md、用真·空行；空行＝**空段落**节点，与 WYSIWYG 里按回车产生的空段一致）：
//   · 解析侧（mdast transformer）：用节点的 position 行号算出相邻根级块之间的空行数，
//     每超出「1 个常规分隔行」就补 1 个**空段落**节点。
//   · 序列化侧（定制 remark-stringify 的 join）：当「左节点是空段落」时分隔符只用 1 个换行（= 0 空行），
//     抵消 mdast-util-to-markdown 默认在每个块边界加的 1 空行。
//
// 二者互逆、幂等：k 个空段落 ⇄ k+1 个空行（= 1 个常规分隔行 + 用户加的 k 个空行）。
//   serialize [A, 空×k, B]：sep(A,空)='\n\n'(默认 1 空行) + k 个 sep(空,*)='\n'(0 空行) → 共 k+2 个换行
//     = A 与 B 间 k+1 个空行。
//   parse「A 与 B 间 k+1 个空行」：blanks=k+1 → 补 blanks-1=k 个空段落。
//
// 为何不用 Milkdown 自带的 remarkPreserveEmptyLinePlugin：它把空行序列化成 `<br />` 标记——
// 既污染 .md，又与本项目「`<br/>` 是行内 HTML 换行元素（htmlBrInputRule / coalesceInline）」的语义直接冲突。
// 故自实现真·空行方案。
//
// 范围（v1）：只处理**根级**直接子节点之间的空行。容器内部（列表项 / 引用块）的空行语义复杂
// （松散列表、引用续行等），贸然插空段会破坏渲染，故 v1 不碰。
//
// 与哨兵的已知小限制：源码→渲染时若光标恰落在某空行上，哨兵字符会占用那一行（该行变成有内容的段落）
// → 那一处少保留 1 个空行。罕见、只影响 1 行，暂不处理。
import { $remark } from '@milkdown/utils';

// 仅用到的 mdast 子集（带 position 行号）。
interface MdPos {
  start?: { line?: number };
  end?: { line?: number };
}
interface MdNode {
  type: string;
  children?: MdNode[];
  position?: MdPos;
}

// 空段落判定：type 为 paragraph 且无子节点（序列化时 Milkdown 对空段不写 children；
// 含 <br/> 等行内 atom 的段落 children 非空，不算空行）。
function isEmptyParagraph(n: MdNode | undefined): boolean {
  return !!n && n.type === 'paragraph' && (!n.children || n.children.length === 0);
}

// 序列化侧 join：左节点是空段落 → 分隔符 0 空行（返回 0 → between() 用单个 '\n'）；
// 其余返回 undefined 交回默认（1 空行）。见 mdast-util-to-markdown 的 container-flow.between。
function blankLineJoin(left: MdNode): number | undefined {
  return isEmptyParagraph(left) ? 0 : undefined;
}

// 解析侧：按根级子节点的行号补空段落。
function insertBlankParagraphs(root: MdNode): void {
  const children = root.children;
  if (!children || children.length < 2) return;
  const out: MdNode[] = [];
  for (let i = 0; i < children.length; i++) {
    const cur = children[i];
    out.push(cur);
    const next = children[i + 1];
    if (!next) break;
    const curEnd = cur.position?.end?.line;
    const nextStart = next.position?.start?.line;
    if (typeof curEnd !== 'number' || typeof nextStart !== 'number') continue;
    const blanks = nextStart - curEnd - 1; // 两块之间的空行数
    const extra = blanks - 1;              // 超出 1 个常规分隔行的部分 = 要补的空段数
    for (let k = 0; k < extra; k++) out.push({ type: 'paragraph', children: [] });
  }
  root.children = out;
}

// $remark 同时挂两侧：unified 插件（普通 function，靠 this=processor 挂 toMarkdownExtensions）+ 返回解析 transformer。
// 必须在所有「会改 mdast 结构」的 remark（图片/表格/HTML/折叠）**之前**注册，保证读到的是 remark-parse 的原始行号。
export const micaBlankLineRemark = $remark('micaBlankLine', () => {
  return function (this: unknown) {
    // 序列化侧：把 join 扩展挂到处理器的 toMarkdownExtensions（remark-stringify 会收集并应用）。
    const self = this as { data?: () => { toMarkdownExtensions?: unknown[] } };
    const data = self.data?.();
    if (data) {
      const exts = data.toMarkdownExtensions || (data.toMarkdownExtensions = []);
      exts.push({ join: [blankLineJoin] });
    }
    // 解析侧：插空段落 transformer。
    return (tree: unknown) => {
      insertBlankParagraphs(tree as MdNode);
    };
  };
});
