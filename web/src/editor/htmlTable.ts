// HTML 表格节点（替换 GFM 管道表格）。
//
// 为什么换：GFM 管道表格无法表达「合并单元格 / 多级表头 / 每表样式」。本模块用 prosemirror-tables 的
// tableNodes 自建一套节点，**不强制表头行**（td/th 可任意混排 → 天然支持多级表头），并把整棵表
// 序列化成真实 `<table>…</table>` HTML 存进 .md。
//
// 存储约定（见 docs/表格HTML改造计划.md）：
//   - 结构性（colspan/rowspan、列对齐 text-align、整表对齐 margin）→ 标准内联 HTML/style，跨应用可移植；
//   - 装饰性（圆角 radius、线宽 border、预设 preset）→ table 的 data-* 属性 + class，.md 干净、每表单独设，
//     别的渲染器忽略时仍是一张普通表格。
//
// 关键：**toDOM（编辑器内实时渲染）** 可以把 radius/border 直接写成内联 style 好让编辑器即时生效；
// **serializeTable（写进 .md）** 则写成 data-* 属性保持 .md 干净。两者都从同一份 node.attrs 派生。
import { DOMParser as PMDOMParser, DOMSerializer, Fragment, Slice } from '@milkdown/prose/model';
import type { Node as ProseNode, Schema, ResolvedPos } from '@milkdown/prose/model';
import {
  tableNodes,
  tableEditing,
  goToNextCell,
  isInTable,
  selectedRect,
  findTable,
  CellSelection,
  TableMap,
  mergeCells,
  splitCell,
  addRowBefore,
  addRowAfter,
  addColumnBefore,
  addColumnAfter,
  addRow,
  removeRow,
  addColumn,
  removeColumn,
  deleteRow,
  deleteColumn,
  deleteTable,
} from '@milkdown/prose/tables';
import { keymap } from '@milkdown/prose/keymap';
import { Plugin, Selection, TextSelection, NodeSelection } from '@milkdown/prose/state';
import type { EditorState, Transaction } from '@milkdown/prose/state';
import { Decoration, DecorationSet } from '@milkdown/prose/view';
import type { EditorView as ProseEditorView } from '@milkdown/prose/view';
import { $nodeSchema, $prose, $remark } from '@milkdown/utils';

export type CellAlign = 'left' | 'center' | 'right';
export type TableAlign = 'left' | 'center' | 'right';
export type TableWidth = 'full' | 'auto'; // full=占满编辑行宽，auto=自适应内容宽
export type TablePreset = 'plain' | '3line' | 'grid' | 'borderless' | 'rainbow' | 'zebra';

// ===== prosemirror-tables 基础节点（含自定义 alignment 单元格属性）=====
// cellContent 用 'block+'：单元格可放段落/列表等块；DOMParser 会把裸内联自动包进段落。
const base = tableNodes({
  tableGroup: 'block',
  cellContent: 'block+',
  cellAttributes: {
    alignment: {
      default: null,
      getFromDOM: (dom) => (dom as HTMLElement).style.textAlign || null,
      setDOMAttr: (value, attrs) => {
        if (value) attrs.style = `${(attrs.style as string) ?? ''}text-align:${value};`;
      },
    },
    // 单元格底色：存内联 background-color（标准、可移植，跨格多选可批量设）。
    background: {
      default: null,
      getFromDOM: (dom) => (dom as HTMLElement).style.backgroundColor || null,
      setDOMAttr: (value, attrs) => {
        if (value) attrs.style = `${(attrs.style as string) ?? ''}background-color:${value};`;
      },
    },
    // 单元格文字颜色：存内联 color（整格文字色，非行内片段；与底色同套机制）。
    textColor: {
      default: null,
      getFromDOM: (dom) => (dom as HTMLElement).style.color || null,
      setDOMAttr: (value, attrs) => {
        if (value) attrs.style = `${(attrs.style as string) ?? ''}color:${value};`;
      },
    },
  },
});

// ===== 工具：attrs ↔ 样式/HTML =====

function tableClass(preset: unknown): string {
  return preset && preset !== 'plain' ? `mica-table mica-table-${preset}` : 'mica-table';
}

// 整表对齐 → margin（标准、可移植）。左对齐是默认，不写。
function tableAlignStyle(align: unknown): string {
  if (align === 'center') return 'margin-left:auto;margin-right:auto;';
  if (align === 'right') return 'margin-left:auto;margin-right:0;';
  return '';
}

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
function escapeAttr(s: string): string {
  return escapeHtml(s).replace(/"/g, '&quot;');
}

// 单段落单元格解包成纯内联（`<td><p>x</p></td>` → `<td>x</td>`），让 .md 更干净。
// 重新解析时 DOMParser 会把裸内联自动包回段落，往返安全。
function unwrapSingleParagraph(html: string): string {
  const m = html.match(/^<p>([\s\S]*)<\/p>$/);
  if (m && !m[1].includes('<p>')) return m[1];
  return html;
}

// PM 表格节点 → 单行 `<table>` HTML（**无空行**，否则 remark 会把它拆成多个 html 节点而解析失败）。
function serializeTable(node: ProseNode): string {
  const serializer = DOMSerializer.fromSchema(node.type.schema);
  const doc = window.document;
  const rowsHtml: string[] = [];
  node.forEach((row) => {
    const cellsHtml: string[] = [];
    row.forEach((cell) => {
      const tag = cell.type.name === 'table_header' ? 'th' : 'td';
      const a: string[] = [];
      const colspan = cell.attrs.colspan as number;
      const rowspan = cell.attrs.rowspan as number;
      if (colspan && colspan !== 1) a.push(`colspan="${colspan}"`);
      if (rowspan && rowspan !== 1) a.push(`rowspan="${rowspan}"`);
      // 单元格内联样式：对齐 + 底色 + 文字色（都从 cell.attrs 派生，往返靠各属性的 getFromDOM/setDOMAttr）。
      const styleParts: string[] = [];
      const align = cell.attrs.alignment as CellAlign | null;
      if (align && align !== 'left') styleParts.push(`text-align:${align}`);
      const bg = cell.attrs.background as string | null;
      if (bg) styleParts.push(`background-color:${bg}`);
      const fg = cell.attrs.textColor as string | null;
      if (fg) styleParts.push(`color:${fg}`);
      if (styleParts.length) a.push(`style="${styleParts.join(';')}"`);
      const frag = serializer.serializeFragment(cell.content, { document: doc });
      const holder = doc.createElement('div');
      holder.appendChild(frag);
      const inner = unwrapSingleParagraph(holder.innerHTML);
      cellsHtml.push(`<${tag}${a.length ? ' ' + a.join(' ') : ''}>${inner}</${tag}>`);
    });
    rowsHtml.push(`<tr>${cellsHtml.join('')}</tr>`);
  });

  const { align, width, radius, border, preset } = node.attrs;
  const noRadius = preset === '3line' || preset === 'borderless'; // 三线表/无框线强制直角，不写圆角
  const tableAttrs: string[] = [`class="${tableClass(preset)}"`];
  if (width === 'auto') tableAttrs.push('data-width="auto"');
  if (radius != null && !noRadius) tableAttrs.push(`data-radius="${radius}"`);
  if (border != null) tableAttrs.push(`data-border="${border}"`);
  // 整表对齐只在 auto 宽度下有意义（full 时占满，margin 无效）；style 仍写出以保可移植。
  const style = width === 'auto' ? tableAlignStyle(align) : '';
  if (style) tableAttrs.push(`style="${style}"`);
  return `<table ${tableAttrs.join(' ')}><tbody>${rowsHtml.join('')}</tbody></table>`;
}

// `<table>` HTML 串 → PM 表格节点。用 PM 的 DOMParser（依各节点 parseDOM 规则还原 colspan/对齐/标记等）。
// 也供 WinUI 启动器经 editor.insertTable 直接发 HTML 插入复用。
export function parseHtmlTable(schema: Schema, html: string): ProseNode | null {
  const trimmed = html.trim();
  if (!trimmed) return null;
  const wrapper = window.document.createElement('div');
  wrapper.innerHTML = trimmed;
  if (!wrapper.querySelector('table')) return null;
  const parser = PMDOMParser.fromSchema(schema);
  const docNode = parser.parse(wrapper);
  let result: ProseNode | null = null;
  docNode.descendants((n) => {
    if (result) return false;
    if (n.type.name === 'table') {
      result = n;
      return false;
    }
    return true;
  });
  return result;
}

// 粘贴进表格时归一化剪贴板 slice：若 slice 里含 table 节点（不论被 .mica-table-scroll 外层 div、段落
// 或其它包了几层），抽出该 table 重建成「裸 table」slice（openStart/openEnd=0）。这样 prosemirror-tables
// 的 handlePaste→pastedCells 才能**稳定**识别成「单元格」去铺填（自动扩行列），而非把整张表当成块插进
// 单元格里嵌套——bug「有时正常铺、有时整表嵌套」的根因正是：ProseMirror 默认解析出的 preProcessedSlice
// 时而是干净 table-role 结构（识别成单元格）、时而被外层 div/段落包裹（识别不出 → 退化成整表嵌套）。
// 仅当光标在表格内才介入；不在表内原样返回（粘进正文仍是整表）。经 transformPasted 接入，故 tableEditing
// 与 milkdown clipboard 拿到的都是这份归一化后的 slice。
export function normalizeTablePasteSlice(view: ProseEditorView, slice: Slice): Slice {
  if (!isInTable(view.state)) return slice;
  let table: ProseNode | null = null;
  const scan = (frag: Fragment) => {
    frag.forEach((n) => {
      if (table) return;
      if (n.type.name === 'table') table = n;
      else scan(n.content);
    });
  };
  scan(slice.content);
  return table ? new Slice(Fragment.from(table), 0, 0) : slice;
}

// ===== 节点 schema 注册（Milkdown $nodeSchema）=====

// row/cell/header：整表已由 table 节点统一 序列化/反序列化，故它们的 md runner 永不匹配（占位）。
// parseDOM/toDOM 仍来自 base，供 PM 的 DOMParser/DOMSerializer 用。
const neverMd = {
  parseMarkdown: { match: () => false, runner: () => {} },
  toMarkdown: { match: () => false, runner: () => {} },
} as const;

export const tableSchema = $nodeSchema('table', () => ({
  ...base.table,
  attrs: {
    align: { default: null },
    width: { default: 'full' }, // 'full' 占满 / 'auto' 自适应
    radius: { default: null },
    border: { default: null },
    preset: { default: null },
  },
  parseDOM: [
    {
      tag: 'table',
      getAttrs: (dom: HTMLElement) => {
        const radiusRaw = dom.getAttribute('data-radius');
        const borderRaw = dom.getAttribute('data-border');
        let preset: string | null = null;
        dom.classList.forEach((c) => {
          if (c.startsWith('mica-table-')) preset = c.slice('mica-table-'.length);
        });
        const width = dom.getAttribute('data-width') === 'auto' ? 'auto' : 'full';
        const ml = dom.style.marginLeft;
        const mr = dom.style.marginRight;
        let align: TableAlign | null = null;
        if (ml === 'auto' && mr === 'auto') align = 'center';
        else if (ml === 'auto') align = 'right';
        return {
          align,
          width,
          radius: radiusRaw != null && radiusRaw !== '' ? Number(radiusRaw) : null,
          border: borderRaw != null && borderRaw !== '' ? Number(borderRaw) : null,
          preset,
        };
      },
    },
  ],
  toDOM: (node: ProseNode) => {
    const { align, width, radius, border, preset } = node.attrs;
    const noRadius = preset === '3line' || preset === 'borderless'; // 三线表/无框线强制直角（圆角会让贯穿线显得断/弯）
    const attrs: Record<string, string> = { class: tableClass(preset) };
    if (width === 'auto') attrs['data-width'] = 'auto';
    if (radius != null && !noRadius) attrs['data-radius'] = String(radius);
    if (border != null) attrs['data-border'] = String(border);
    // 实时渲染：把宽度/对齐/圆角/线宽直接写成内联 style（圆角靠 border-collapse:separate+overflow:hidden
    // 裁切，见 editor.css）；线宽给单元格用的 CSS 变量。full 宽度 → width:100%；auto → 不写宽度（CSS auto）+ margin 对齐。
    const styleParts: string[] = [];
    if (width === 'auto') styleParts.push(tableAlignStyle(align));
    else styleParts.push('width:100%;');
    if (radius != null && !noRadius) styleParts.push(`border-radius:${radius}px;`);
    if (border != null) styleParts.push(`--mica-table-border-width:${border}px;`);
    const style = styleParts.join('');
    if (style) attrs.style = style;
    // 外包一层 overflow-x:auto 容器：宽表只在「这层」内横向滚动，不再撑出整个文档的横向滚动条（见 editor.css）。
    // 仅实时渲染加这层；序列化(serializeTable)/反序列化(parseHtmlTable) 都不涉及它，.md 里仍是裸 <table>。
    return ['div', { class: 'mica-table-scroll' }, ['table', attrs, ['tbody', 0]]];
  },
  parseMarkdown: {
    match: (node) => node.type === 'mica_html_table',
    runner: (state, node) => {
      const pmNode = parseHtmlTable(state.schema, String(node.value ?? ''));
      if (pmNode) state.push(pmNode);
    },
  },
  toMarkdown: {
    match: (node) => node.type.name === 'table',
    runner: (state, node) => {
      state.addNode('html', undefined, serializeTable(node));
    },
  },
}));

export const tableRowSchema = $nodeSchema('table_row', () => ({ ...base.table_row, ...neverMd }));
export const tableCellSchema = $nodeSchema('table_cell', () => ({ ...base.table_cell, ...neverMd }));
export const tableHeaderSchema = $nodeSchema('table_header', () => ({ ...base.table_header, ...neverMd }));

// ===== remark 变换：统一把表格 mdast 节点改成唯一类型 mica_html_table =====
// ① `<table` 开头的 html 节点（我们自己存的）；② 粘贴/遗留的 gfm 管道表格 `table` 节点 → 渲染成 HTML 串。
// 改成唯一类型可避开「commonmark 的 html 节点匹配任意 type==='html'」的冲突。
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  align?: (string | null)[];
  url?: string;
  alt?: string;
}

function isTableHtml(value: unknown): boolean {
  return typeof value === 'string' && /^\s*<table[\s>]/i.test(value);
}

function phrasingToHtml(nodes: MdNode[] | undefined): string {
  if (!nodes) return '';
  return nodes.map(phrasingNode).join('');
}
function phrasingNode(n: MdNode): string {
  switch (n.type) {
    case 'text': return escapeHtml(String(n.value ?? ''));
    case 'strong': return `<strong>${phrasingToHtml(n.children)}</strong>`;
    case 'emphasis': return `<em>${phrasingToHtml(n.children)}</em>`;
    case 'delete': return `<del>${phrasingToHtml(n.children)}</del>`;
    case 'inlineCode': return `<code>${escapeHtml(String(n.value ?? ''))}</code>`;
    case 'break': return '<br>';
    case 'link': return `<a href="${escapeAttr(String(n.url ?? ''))}">${phrasingToHtml(n.children)}</a>`;
    case 'image': return `<img src="${escapeAttr(String(n.url ?? ''))}" alt="${escapeAttr(String(n.alt ?? ''))}">`;
    default: return n.children ? phrasingToHtml(n.children) : escapeHtml(String(n.value ?? ''));
  }
}

// gfm 管道表格 mdast → HTML 串（首行表头，列对齐取 node.align）。
function mdastTableToHtml(table: MdNode): string {
  const align = table.align ?? [];
  const rows = table.children ?? [];
  const rowsHtml = rows.map((row, ri) => {
    const tag = ri === 0 ? 'th' : 'td';
    const cells = row.children ?? [];
    const cellsHtml = cells.map((cell, ci) => {
      const al = align[ci];
      const style = al && al !== 'left' ? ` style="text-align:${al}"` : '';
      return `<${tag}${style}>${phrasingToHtml(cell.children)}</${tag}>`;
    });
    return `<tr>${cellsHtml.join('')}</tr>`;
  });
  return `<table class="mica-table"><tbody>${rowsHtml.join('')}</tbody></table>`;
}

function isTableHtmlNode(n: MdNode): boolean {
  return n.type === 'html' && isTableHtml(n.value);
}

// 把表格 mdast 节点统一改成块级 mica_html_table（含粘贴的 gfm `table`）。返回是否已处理为表格。
function retypeTableNode(node: MdNode): boolean {
  if (isTableHtmlNode(node)) {
    node.type = 'mica_html_table';
    node.children = undefined;
    return true;
  }
  if (node.type === 'table') {
    node.value = mdastTableToHtml(node);
    node.type = 'mica_html_table';
    node.children = undefined;
    return true;
  }
  return false;
}

// remark 往返后，独立的 `<table>` 常被当成「行内 html」包进一个段落里（段落只能装内联，表格塞进去会让
// 解析器报 "Cannot create node for paragraph"）。故把段落里的表格 html 提升到块级，前后非空文本仍留作段落。
function liftTablesFromParagraph(p: MdNode): MdNode[] {
  const result: MdNode[] = [];
  let buffer: MdNode[] = [];
  const flush = () => {
    if (buffer.some((b) => !(b.type === 'text' && !String(b.value ?? '').trim()))) {
      result.push({ type: 'paragraph', children: buffer });
    }
    buffer = [];
  };
  for (const k of p.children ?? []) {
    if (isTableHtmlNode(k)) {
      flush();
      result.push({ type: 'mica_html_table', value: k.value });
    } else {
      buffer.push(k);
    }
  }
  flush();
  return result;
}

function transformTree(node: MdNode): void {
  if (!node.children) return;
  const out: MdNode[] = [];
  for (const child of node.children) {
    if (retypeTableNode(child)) {
      out.push(child);
      continue;
    }
    if (child.type === 'paragraph' && child.children?.some(isTableHtmlNode)) {
      out.push(...liftTablesFromParagraph(child));
      continue;
    }
    transformTree(child);
    out.push(child);
  }
  node.children = out;
}

export const micaTableRemark = $remark('micaHtmlTable', () => () => (tree: unknown) => {
  transformTree(tree as MdNode);
});

// 边缘单元格边框去重：按 TableMap 给「触到最右列 / 最末行」的单元格打 .mica-edge-right / .mica-edge-bottom，
// CSS 据此去掉它们的右/下内框线，避免与表格外框叠成双线（见 editor.css）。**用 TableMap 而非 :last-child**
// 才对合并单元格安全：cellRect.right===map.width 才是真·最右、bottom===map.height 才是真·最末，
// 被 rowspan 盖住而「碰巧成为某行最后一个 <td>」但其实不在最右的格不会被误判（那个诡异缺线 bug 的根因）。
function tableEdgeDecorations(state: EditorState): DecorationSet {
  const decos: Decoration[] = [];
  state.doc.descendants((node, pos) => {
    if (node.type.name !== 'table') return true; // 继续下钻找表格（表格不嵌套，命中即不再进内部）
    const map = TableMap.get(node);
    const tableStart = pos + 1;
    const seen = new Set<number>();
    for (const rel of map.map) {
      if (seen.has(rel)) continue;
      seen.add(rel);
      const r = map.findCell(rel);
      const classes: string[] = [];
      if (r.right === map.width) classes.push('mica-edge-right');
      if (r.bottom === map.height) classes.push('mica-edge-bottom');
      if (!classes.length) continue;
      const cellPos = tableStart + rel;
      const cell = state.doc.nodeAt(cellPos);
      if (cell) decos.push(Decoration.node(cellPos, cellPos + cell.nodeSize, { class: classes.join(' ') }));
    }
    return false; // 不进入表格内部
  });
  return decos.length ? DecorationSet.create(state.doc, decos) : DecorationSet.empty;
}

// ===== prose 插件：cell-selection / 良构维护 + Tab 单元格导航 =====
export const tableProsePlugins = [
  $prose(() => tableEditing()),
  $prose(() => keymap({ Tab: goToNextCell(1), 'Shift-Tab': goToNextCell(-1) })),
  // 边缘格去重边框（合并安全），见上 tableEdgeDecorations。
  $prose(() => new Plugin({ props: { decorations: (state) => tableEdgeDecorations(state) } })),
  // 表头覆盖维护：任意改文档（合并/插入/粘贴）后，自动把「被表头 rowspan 盖住」的行提升为表头。
  // 无改动时返回 null → 不会自我循环（提升后下一轮已满足不变式）。载入已有表格由 createEditor 兜底跑一次。
  $prose(() =>
    new Plugin({
      appendTransaction: (trs, _old, newState) =>
        trs.some((t) => t.docChanged) ? enforceHeaderCoverage(newState) : null,
    }),
  ),
];

// ===== 编辑器内表格操作（右键菜单经 editor.tableOp 回传 op 后调用）=====

// 设某列对齐：无 keepTableAlignPlugin，故逐个写该列所有单元格（合并单元格去重）。
function setColumnAlign(view: ProseEditorView, align: CellAlign) {
  const { state } = view;
  if (!isInTable(state)) return;
  const rect = selectedRect(state);
  const map = rect.map;
  let tr = state.tr;
  const seen = new Set<number>();
  for (let col = rect.left; col < rect.right; col++) {
    for (let row = 0; row < map.height; row++) {
      const cellPos = rect.tableStart + map.map[row * map.width + col];
      if (seen.has(cellPos)) continue;
      seen.add(cellPos);
      const cell = state.doc.nodeAt(cellPos);
      if (cell) tr = tr.setNodeMarkup(cellPos, undefined, { ...cell.attrs, alignment: align === 'left' ? null : align });
    }
  }
  if (tr.docChanged) view.dispatch(tr);
}

// 设整表对齐（写 table 节点 align 属性 → toDOM 渲染 margin）。
function setTableAlign(view: ProseEditorView, align: TableAlign) {
  const found = findTable(view.state.selection.$from);
  if (!found) return;
  view.dispatch(
    view.state.tr.setNodeMarkup(found.pos, undefined, {
      ...found.node.attrs,
      align: align === 'left' ? null : align,
    }),
  );
}

// 设整表宽度（full 占满 / auto 自适应）。
function setTableWidth(view: ProseEditorView, width: TableWidth) {
  const found = findTable(view.state.selection.$from);
  if (!found) return;
  view.dispatch(view.state.tr.setNodeMarkup(found.pos, undefined, { ...found.node.attrs, width }));
}

// ===== 浮动工具条用：按「表格节点 pos」直接改整表属性 / 增减行列（不依赖选区）=====
// 浮动条操作的是「鼠标悬停的那张表」（按 pos 定位），而非光标所在表，故都收 pos 参数。
// 全部在 web 内执行（无 IPC）；改完 view.focus() 让后续编辑顺畅。

export interface TableInfo {
  align: TableAlign | null;   // 整表「位置」对齐（margin，仅 auto 宽度有效）
  content: CellAlign | null;  // 整表「内容」对齐（所有单元格统一时报该值，混合则 null → 不高亮）
  width: TableWidth;
  radius: number | null;
  border: number | null;
  preset: TablePreset | null;
  rows: number;
  cols: number;
}

// 读某张表的当前属性 + 行列数（供工具条回填状态/高亮当前项）。
export function getTableInfoAt(view: ProseEditorView, pos: number): TableInfo | null {
  const node = view.state.doc.nodeAt(pos);
  if (!node || node.type.name !== 'table') return null;
  const map = TableMap.get(node);
  // 整表内容对齐：扫所有唯一单元格，全一致才报该值（混合 → null，按钮不高亮）。
  const tableStart = pos + 1;
  const seen = new Set<number>();
  let uniform: CellAlign | undefined;
  let mixed = false;
  for (const rel of map.map) {
    const abs = tableStart + rel;
    if (seen.has(abs)) continue;
    seen.add(abs);
    const cell = view.state.doc.nodeAt(abs);
    const a = (cell?.attrs.alignment as CellAlign | null) ?? 'left';
    if (uniform === undefined) uniform = a;
    else if (uniform !== a) mixed = true;
  }
  return {
    align: (node.attrs.align as TableAlign | null) ?? null,
    content: mixed ? null : (uniform ?? 'left'),
    width: (node.attrs.width as TableWidth) ?? 'full',
    radius: node.attrs.radius == null ? null : (node.attrs.radius as number),
    border: node.attrs.border == null ? null : (node.attrs.border as number),
    preset: (node.attrs.preset as TablePreset | null) ?? null,
    rows: map.height,
    cols: map.width,
  };
}

// 设整表「内容」对齐：把表内所有单元格的 alignment 统一设为 align（left 还原成 null）。
export function setAllCellsAlignAt(view: ProseEditorView, pos: number, align: CellAlign) {
  const node = view.state.doc.nodeAt(pos);
  if (!node || node.type.name !== 'table') return;
  const map = TableMap.get(node);
  const tableStart = pos + 1;
  let tr = view.state.tr;
  const seen = new Set<number>();
  for (const rel of map.map) {
    const abs = tableStart + rel;
    if (seen.has(abs)) continue;
    seen.add(abs);
    const cell = view.state.doc.nodeAt(abs);
    if (cell) tr = tr.setNodeMarkup(abs, undefined, { ...cell.attrs, alignment: align === 'left' ? null : align });
  }
  if (tr.docChanged) view.dispatch(tr);
  view.focus();
}

// 合并 patch 到某张表的 attrs（对齐/宽度/圆角/线宽/预设均走这条）。
export function setTableAttrsAt(view: ProseEditorView, pos: number, patch: Record<string, unknown>) {
  const node = view.state.doc.nodeAt(pos);
  if (!node || node.type.name !== 'table') return;
  view.dispatch(view.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, ...patch }));
  view.focus();
}

// 把某张表调整到 targetRows × targetCols：**从右/下边缘**增减（对标用户要求），最大程度保留已有内容与
// 合并——加列追加到最右、加行追加到最底；减列从最右删、减行从最底删（removeColumn/Row 对跨格合并会
// 自动缩 colspan/rowspan，不会炸表）。每步都用「当前 tr.doc」重建 TableRect，因增删后表尺寸/映射会变。
export function resizeTableAt(view: ProseEditorView, pos: number, targetRows: number, targetCols: number) {
  targetRows = Math.max(1, Math.min(50, Math.round(targetRows)));
  targetCols = Math.max(1, Math.min(20, Math.round(targetCols)));
  let tr = view.state.tr;
  const rectOf = () => {
    const table = tr.doc.nodeAt(pos);
    if (!table || table.type.name !== 'table') return null;
    const map = TableMap.get(table);
    return { map, table, tableStart: pos + 1, left: 0, top: 0, right: map.width, bottom: map.height };
  };
  let rect = rectOf();
  if (!rect) return;
  let cols = rect.map.width;
  while (cols < targetCols) { const rc = rectOf(); if (!rc) break; tr = addColumn(tr, rc, rc.map.width); cols++; }
  while (cols > targetCols) { const rc = rectOf(); if (!rc) break; removeColumn(tr, rc, rc.map.width - 1); cols--; }
  let rows = rectOf()?.map.height ?? 0;
  while (rows < targetRows) { const rc = rectOf(); if (!rc) break; tr = addRow(tr, rc, rc.map.height); rows++; }
  while (rows > targetRows) { const rc = rectOf(); if (!rc) break; removeRow(tr, rc, rc.map.height - 1); rows--; }
  if (tr.docChanged) view.dispatch(tr);
  view.focus();
}

// ===== 行/列拖动重排（浮动条「排序」开关启用，详见 tableReorder.ts）=====
// **块模型**：「干净边界」（不被任何跨格跨过的边界）天然把表切成若干**原子块**——两条相邻干净边界之间
// 的行/列因被合并绑在一起、不能拆开，正好是一个「整体」。于是**每个块都可拖动**（拖到任意其它干净边界都合法），
// 合并的行/列只是变成更大的块，不再有「不可拖」的概念。对标用户直觉「跨行/跨列合并的视为一个整体拖动」。
// findCell(map.map[idx]) 返回该网格位被哪个单元格覆盖的矩形（top/left/right/bottom 为网格坐标）。

// 行边界 0..height 是否「干净」（无 rowspan 跨过该边界）→ 可作块分界 / 落点。
export function getCleanRowBoundaries(node: ProseNode): boolean[] {
  const map = TableMap.get(node);
  const res: boolean[] = [];
  for (let b = 0; b <= map.height; b++) {
    if (b === 0 || b === map.height) { res.push(true); continue; }
    let ok = true;
    for (let col = 0; col < map.width; col++) {
      if (map.findCell(map.map[b * map.width + col]).top !== b) { ok = false; break; } // 上方 rowspan 续延进 b 行
    }
    res.push(ok);
  }
  return res;
}

// 列边界 0..width 是否「干净」（无 colspan 跨过该边界）→ 可作块分界 / 落点。
export function getCleanColBoundaries(node: ProseNode): boolean[] {
  const map = TableMap.get(node);
  const res: boolean[] = [];
  for (let b = 0; b <= map.width; b++) {
    if (b === 0 || b === map.width) { res.push(true); continue; }
    let ok = true;
    for (let row = 0; row < map.height; row++) {
      if (map.findCell(map.map[row * map.width + b]).left !== b) { ok = false; break; } // 左侧 colspan 续延进 b 列
    }
    res.push(ok);
  }
  return res;
}

// 把干净边界数组转成「块」区间 [start,end)（每对相邻干净边界之间一个块）。
export function boundariesToBlocks(clean: boolean[]): Array<[number, number]> {
  const blocks: Array<[number, number]> = [];
  let start = 0;
  for (let b = 1; b < clean.length; b++) {
    if (clean[b]) { blocks.push([start, b]); start = b; }
  }
  return blocks;
}

// 把行块 [from,from+count) 移到边界 to（0..height，须为干净边界）。行是真实 table_row 节点：重排子节点数组后整表重建。
export function moveTableRowsAt(view: ProseEditorView, pos: number, from: number, count: number, to: number) {
  const node = view.state.doc.nodeAt(pos);
  if (!node || node.type.name !== 'table') return;
  if (to === from || to === from + count) return; // 原地，免造无意义历史
  const rows: ProseNode[] = [];
  node.forEach((r) => rows.push(r));
  if (from < 0 || from + count > rows.length) return;
  const seg = rows.splice(from, count);
  const insertAt = to > from ? to - count : to; // 删除块后索引前移
  rows.splice(Math.max(0, Math.min(rows.length, insertAt)), 0, ...seg);
  const newTable = node.type.create(node.attrs, rows);
  const tr = view.state.tr.replaceWith(pos, pos + node.nodeSize, newTable);
  // 把光标落回新表内（否则 replaceWith 后选区被映射到表「外」下一行 → syncToCaret 判定不在表里、浮动条消失）。
  tr.setSelection(Selection.near(tr.doc.resolve(pos + 1), 1));
  view.dispatch(tr.scrollIntoView());
  view.focus();
}

// 把列块 [from,from+count) 移到边界 to（0..width，须为干净边界）。列不是节点：逐行把「起始于此块」的连续单元格
// 节点段搬到 to 边界处；被上方 rowspan 盖住块内某列的行只含部分/不含节点、原样按现有顺序保留（「洞」随被移动
// 的合并格一起换列，节点相对顺序不变 → 仍良构，左移右移皆成立）。
export function moveTableColumnsAt(view: ProseEditorView, pos: number, from: number, count: number, to: number) {
  const node = view.state.doc.nodeAt(pos);
  if (!node || node.type.name !== 'table') return;
  if (to === from || to === from + count) return;
  const map = TableMap.get(node);
  const width = map.width;
  const newRows: ProseNode[] = [];
  let rowIdx = 0;
  node.forEach((row) => {
    // 本行「起始于此」的单元格的列号（升序，与 row 子节点顺序一一对应）
    const startCols: number[] = [];
    for (let col = 0; col < width; col++) {
      const r = map.findCell(map.map[rowIdx * width + col]);
      if (r.top === rowIdx && r.left === col) startCols.push(col);
    }
    const cells: ProseNode[] = [];
    row.forEach((c) => cells.push(c));
    // 块内节点段 [sliceStart,sliceEnd)：起始列落在 [from,from+count)（边界干净 → 必是连续段）
    let sliceStart = 0;
    while (sliceStart < startCols.length && startCols[sliceStart] < from) sliceStart++;
    let sliceEnd = sliceStart;
    while (sliceEnd < startCols.length && startCols[sliceEnd] < from + count) sliceEnd++;
    // 目标节点位：起始列 >= to 的首个节点
    let toIdx = 0;
    while (toIdx < startCols.length && startCols[toIdx] < to) toIdx++;
    const seg = cells.splice(sliceStart, sliceEnd - sliceStart); // 可能为空（该行块内全被盖住）→ 原样
    const insertAt = toIdx > sliceStart ? toIdx - seg.length : toIdx;
    cells.splice(Math.max(0, Math.min(cells.length, insertAt)), 0, ...seg);
    newRows.push(row.type.create(row.attrs, cells));
    rowIdx++;
  });
  const newTable = node.type.create(node.attrs, newRows);
  const tr = view.state.tr.replaceWith(pos, pos + node.nodeSize, newTable);
  // 同上：光标落回新表内，保持浮动条不消失。
  tr.setSelection(Selection.near(tr.doc.resolve(pos + 1), 1));
  view.dispatch(tr.scrollIntoView());
  view.focus();
}

// 判断某行是否含表头单元格（th）。
function rowHasHeader(state: ProseEditorView['state'], rowTop: number): boolean {
  if (!isInTable(state)) return false;
  const rect = selectedRect(state);
  const map = rect.map;
  if (rowTop < 0 || rowTop >= map.height) return false;
  for (let col = 0; col < map.width; col++) {
    const cell = state.doc.nodeAt(rect.tableStart + map.map[rowTop * map.width + col]);
    if (cell && cell.type.name === 'table_header') return true;
  }
  return false;
}

// 把某行所有单元格转成 th / td（用于表头插入时的「上移表头」）。读实时 state。
function convertRowCells(view: ProseEditorView, rowTop: number, toHeader: boolean) {
  const { state } = view;
  if (!isInTable(state)) return;
  const rect = selectedRect(state);
  const map = rect.map;
  if (rowTop < 0 || rowTop >= map.height) return;
  const newType = toHeader ? state.schema.nodes.table_header : state.schema.nodes.table_cell;
  let tr = state.tr;
  const seen = new Set<number>();
  for (let col = 0; col < map.width; col++) {
    const cellPos = rect.tableStart + map.map[rowTop * map.width + col];
    if (seen.has(cellPos)) continue;
    seen.add(cellPos);
    const cell = state.doc.nodeAt(cellPos);
    if (cell && cell.type !== newType) tr = tr.setNodeMarkup(cellPos, newType, cell.attrs);
  }
  if (tr.docChanged) view.dispatch(tr);
}

// 自动提升表头（全文扫描）：把每张表里「被来自上方的表头(th)rowspan 盖住」的行，其真正起始的单元格
// 也提升为 th。对标用户直觉「表头往下合并，下面的行天然成为表头」（顶部表头优先）。**规则安全的前提**：
// Markdown/Typora/Obsidian 只有顶部表头、无侧边表头，故 th 的 rowspan 只会是「顶部表头块向下延伸」，被盖
// 的行就是子表头——不会误伤「左侧标签纵向跨数据行」（那种左标签是普通合并 td、不是 th，不触发本规则）。
// 多级表头的二级表头(val/testA…)由此自动成为 th，三线表 CSS 才能画出贯穿中线 + 组分隔线。
// 经 appendTransaction 插件（合并/插入/粘贴等任意改文档时）+ 载入后一次性兜底（已有表格）触发，故全自动。
// 返回需应用的 tr（无改动 → null）。setNodeMarkup 不改节点尺寸（th↔td 等大），故多处/多表的绝对位置不失效。
export function enforceHeaderCoverage(state: EditorState): Transaction | null {
  const headerType = state.schema.nodes.table_header;
  if (!headerType) return null;
  let tr = state.tr;
  let changed = false;
  state.doc.descendants((node, pos) => {
    if (node.type.name !== 'table') return true;
    const map = TableMap.get(node);
    const tableStart = pos + 1;
    for (let row = 1; row < map.height; row++) {
      // 该行是否被「上方 th」的 rowspan 盖住
      let coveredByHeader = false;
      for (let col = 0; col < map.width; col++) {
        const rel = map.map[row * map.width + col];
        if (map.findCell(rel).top < row) {
          const c = state.doc.nodeAt(tableStart + rel);
          if (c && c.type === headerType) { coveredByHeader = true; break; }
        }
      }
      if (!coveredByHeader) continue;
      // 提升本行「真正起始」的单元格为 th（跳过来自上方的 rowspan 续延，避免误改上一行的合并单元格）
      const seen = new Set<number>();
      for (let col = 0; col < map.width; col++) {
        const rel = map.map[row * map.width + col];
        if (map.findCell(rel).top !== row) continue;
        const abs = tableStart + rel;
        if (seen.has(abs)) continue;
        seen.add(abs);
        const cell = state.doc.nodeAt(abs);
        if (cell && cell.type !== headerType) { tr = tr.setNodeMarkup(abs, headerType, cell.attrs); changed = true; }
      }
    }
    return false; // 不进入表格内部（不允许嵌套表）
  });
  return changed ? tr : null;
}

// 在上方插入行：若当前是「含表头的首行」，对标 Typora——新行成为表头、原表头降为普通行
// （否则照常插入），避免「表头跑到中间」。
function addRowBeforeSmart(view: ProseEditorView) {
  const { state } = view;
  let promote = false;
  if (isInTable(state)) {
    const rect = selectedRect(state);
    promote = rect.top === 0 && rowHasHeader(state, 0);
  }
  addRowBefore(state, view.dispatch);
  if (promote) {
    convertRowCells(view, 0, true);  // 新的首行 → 表头
    convertRowCells(view, 1, false); // 原表头（现在第二行）→ 普通行
  }
}

// 右键菜单「上移/下移行、左移/右移列」：把当前单元格所在的「块」与相邻块对调（dir<0 上/左，dir>0 下/右）。
// 复用块模型——干净边界把表切成原子块，移动整块到相邻块的外边界即「跳过相邻块一格」，跨行/跨列合并整体走。
function moveBlockStep(view: ProseEditorView, axis: 'row' | 'col', dir: number) {
  const state = view.state;
  if (!isInTable(state)) return;
  const rect = selectedRect(state);
  const tablePos = rect.tableStart - 1; // table 节点 pos（tableStart 是表内首位）
  const node = state.doc.nodeAt(tablePos);
  if (!node || node.type.name !== 'table') return;
  const clean = axis === 'row' ? getCleanRowBoundaries(node) : getCleanColBoundaries(node);
  const blocks = boundariesToBlocks(clean);
  const at = axis === 'row' ? rect.top : rect.left; // 当前单元格起始行/列
  const idx = blocks.findIndex((b) => at >= b[0] && at < b[1]);
  if (idx < 0) return;
  const [bs, be] = blocks[idx];
  const count = be - bs;
  if (dir < 0) {
    if (idx === 0) return;
    const to = blocks[idx - 1][0]; // 移到上/左一块之前
    if (axis === 'row') moveTableRowsAt(view, tablePos, bs, count, to);
    else moveTableColumnsAt(view, tablePos, bs, count, to);
  } else {
    if (idx === blocks.length - 1) return;
    const to = blocks[idx + 1][1]; // 移到下/右一块之后
    if (axis === 'row') moveTableRowsAt(view, tablePos, bs, count, to);
    else moveTableColumnsAt(view, tablePos, bs, count, to);
  }
}

// ===== 单元格底色 / 文字色（单元格级，作用于跨格多选或当前格）=====

// 遍历「当前要作用的单元格」：跨格 CellSelection → 全部选中格；否则 → 光标所在的那一格。
function eachSelectedCell(state: EditorState, cb: (cell: ProseNode, pos: number) => void) {
  const sel = state.selection;
  if (sel instanceof CellSelection) {
    sel.forEachCell((cell, pos) => cb(cell, pos));
    return;
  }
  if (!isInTable(state)) return;
  const rect = selectedRect(state);
  const pos = rect.tableStart + rect.map.map[rect.top * rect.map.width + rect.left];
  const cell = state.doc.nodeAt(pos);
  if (cell) cb(cell, pos);
}

// 给选中单元格设某个样式属性（background / textColor）。value 为空 → 清除该属性（还原透明/默认）。
function setCellsAttr(view: ProseEditorView, attr: 'background' | 'textColor', value: string | null) {
  const { state } = view;
  if (!isInTable(state) && !(state.selection instanceof CellSelection)) return;
  let tr = state.tr;
  const seen = new Set<number>();
  eachSelectedCell(state, (cell, pos) => {
    if (seen.has(pos)) return;
    seen.add(pos);
    tr = tr.setNodeMarkup(pos, undefined, { ...cell.attrs, [attr]: value || null });
  });
  if (tr.docChanged) view.dispatch(tr);
}

// ===== 手动设/取消表头行 =====
// 把光标所在行整行在 th/td 间切换（已含 th → 全转 td；否则全转 th）。解决「PPT 粘贴无表头」。
// 注意：enforceHeaderCoverage 只会「补」th（被上方表头 rowspan 盖住的行），不会移除手动 td；故取消顶部
// 表头行可行，唯一边缘情况是取消「被上方表头合并盖住的行」会被自动规则重新提升（合理，少见）。
function toggleHeaderRow(view: ProseEditorView) {
  const { state } = view;
  if (!isInTable(state)) return;
  const rect = selectedRect(state);
  const isHeader = rowHasHeader(state, rect.top);
  convertRowCells(view, rect.top, !isHeader);
}

// ===== 复制整表 =====
// 选中整张 table 节点（NodeSelection）后触发浏览器 copy——复用编辑器自身的剪贴板序列化（HTML + markdown），
// 故粘贴回来（含粘进别的表，配合 normalizeTablePasteSlice）都正确。execCommand 在 WebView2 同步可用。
function copyTable(view: ProseEditorView) {
  const found = findTable(view.state.selection.$from);
  if (!found) return;
  view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, found.pos)));
  view.focus();
  try { view.root.ownerDocument?.execCommand?.('copy') ?? document.execCommand('copy'); }
  catch { document.execCommand('copy'); }
}

// ===== Ctrl+A 渐进选择（对标 Typora：单元格 → 整表 → 整文档）=====
// 返回 true = 已处理（阻止默认 selectAll）；返回 false = 交回默认（选整个文档）。
// 三态：① 光标在某格、未全选该格内容 → 选该格内容；② 已全选某格内容 / 单格 CellSelection → 选整表（CellSelection）；
// ③ 已是「整表」CellSelection → 返回 false → 由 createEditor 兜底 selectAll 选整文档。
export function tableSelectAll(view: ProseEditorView): boolean {
  const { state } = view;
  const sel = state.selection;
  if (!isInTable(state) && !(sel instanceof CellSelection)) return false;
  const rect = selectedRect(state);
  const map = rect.map;
  const selectWholeTable = () => {
    const first = rect.tableStart + map.map[0];
    const last = rect.tableStart + map.map[map.width * map.height - 1];
    view.dispatch(state.tr.setSelection(CellSelection.create(state.doc, first, last)));
  };
  if (sel instanceof CellSelection) {
    const spansAll = rect.left === 0 && rect.top === 0 && rect.right === map.width && rect.bottom === map.height;
    if (spansAll) return false; // 已选整表 → 第三次：交回默认选整文档
    selectWholeTable();
    return true;
  }
  // 文本选区：判断是否已全选当前格内容
  const $from = sel.$from;
  const d = cellDepthOf($from);
  if (d < 0) return false;
  const cellStart = $from.start(d);
  const cellEnd = $from.end(d);
  if (sel.from <= cellStart && sel.to >= cellEnd) {
    selectWholeTable(); // 已全选该格 → 升到整表
    return true;
  }
  view.dispatch(state.tr.setSelection(TextSelection.create(state.doc, cellStart, cellEnd)));
  return true;
}

type ProseCommand = (state: ProseEditorView['state'], dispatch: ProseEditorView['dispatch']) => boolean;

// 直接改动 PM 管理的 DOM（如表格圆角/线宽的预览态改内联样式）时，先停掉 PM 的 DOMObserver 再改、改完恢复——
// 否则 PM 会把这次属性变更当成「外部 DOM 改动」flush 进来、重建整张表 DOM（每次拖动都重建 = 卡顿）。
// domObserver 是 prosemirror-view 内部成员（无公开类型），故 as 取用并做存在性兜底（取不到就直接改，退回原行为不崩）。
function mutateSilently(view: ProseEditorView, fn: () => void) {
  const obs = (view as unknown as { domObserver?: { stop(): void; start(): void } }).domObserver;
  if (!obs) { fn(); return; }
  obs.stop();
  try { fn(); } finally { obs.start(); }
}

// 在给定 view 上执行表格 op（op 名与宿主右键菜单约定一致）。value 仅颜色类 op 用（十六进制色，空串=清除）。
export function runTableOp(view: ProseEditorView, op: string, value?: string | null) {
  const cmd = (c: ProseCommand) => c(view.state, view.dispatch);
  switch (op) {
    case 'addRowBefore': addRowBeforeSmart(view); break;
    case 'addRowAfter': cmd(addRowAfter); break;
    case 'addColBefore': cmd(addColumnBefore); break;
    case 'addColAfter': cmd(addColumnAfter); break;
    case 'deleteRow': cmd(deleteRow); break;
    case 'deleteCol': cmd(deleteColumn); break;
    case 'deleteTable': cmd(deleteTable); break;
    case 'mergeCells': cmd(mergeCells); break; // 合并后被表头盖住的行由 headerCoveragePlugin 自动提升
    case 'splitCell': cmd(splitCell); break;
    case 'alignLeft': setColumnAlign(view, 'left'); break;
    case 'alignCenter': setColumnAlign(view, 'center'); break;
    case 'alignRight': setColumnAlign(view, 'right'); break;
    case 'tableAlignLeft': setTableAlign(view, 'left'); break;
    case 'tableAlignCenter': setTableAlign(view, 'center'); break;
    case 'tableAlignRight': setTableAlign(view, 'right'); break;
    case 'tableWidthFull': setTableWidth(view, 'full'); break;
    case 'tableWidthAuto': setTableWidth(view, 'auto'); break;
    // 整表「位置对齐 + 占满」四选一（从浮动条迁入右键菜单，对标 Word 行对齐）：靠左/居中/靠右 = 自适应宽度下定位；
    // 占满 = 占满编辑宽度。各发一个 setNodeMarkup（同时改 width+align），与原浮条 setTableAttrsAt 语义一致。
    case 'tablePosLeft': { const f = findTable(view.state.selection.$from); if (f) setTableAttrsAt(view, f.pos, { width: 'auto', align: null }); break; }
    case 'tablePosCenter': { const f = findTable(view.state.selection.$from); if (f) setTableAttrsAt(view, f.pos, { width: 'auto', align: 'center' }); break; }
    case 'tablePosRight': { const f = findTable(view.state.selection.$from); if (f) setTableAttrsAt(view, f.pos, { width: 'auto', align: 'right' }); break; }
    case 'tablePosFull': { const f = findTable(view.state.selection.$from); if (f) setTableAttrsAt(view, f.pos, { width: 'full' }); break; }
    // 样式预设 / 圆角 / 线宽 / 行列数（同样从浮条迁入）。value: preset 名（''→null 即普通）、px 数、目标行/列数。
    case 'tablePreset': { const f = findTable(view.state.selection.$from); if (f) setTableAttrsAt(view, f.pos, { preset: value ? value : null }); break; }
    case 'tableRadius': { const f = findTable(view.state.selection.$from); if (f) setTableAttrsAt(view, f.pos, { radius: Number(value) }); break; }
    case 'tableBorder': { const f = findTable(view.state.selection.$from); if (f) setTableAttrsAt(view, f.pos, { border: Number(value) }); break; }
    // 圆角/线宽的「拖动实时预览」：只改 <table> 的内联 style（borderRadius / --mica-table-border-width，与 toDOM 一致）、
    // 不提交、不抢焦点。松手由宿主再发 tableRadius/tableBorder 提交。
    // **卡顿根因 + 修法**：表格无 NodeView（由 toDOM 渲染），直接改 tableEl.style 会被 PM 的 DOMObserver 当成「外部 DOM
    // 改动」→ 每拖一下重建整张表 DOM = 卡。图片那侧 preview 改的是 NodeView 内 DOM（ignoreMutation:()=>true 天然跳过）
    // 故丝滑。这里手动 mutateSilently（停 DOMObserver 再改）让 PM 忽略这次纯预览改动，达到同样丝滑。
    case 'tablePreviewRadius':
    case 'tablePreviewBorder': {
      const f = findTable(view.state.selection.$from);
      if (f) {
        const dom = view.nodeDOM(f.pos);
        const tableEl = dom instanceof HTMLElement ? (dom.tagName === 'TABLE' ? dom : dom.querySelector('table')) : null;
        if (tableEl instanceof HTMLElement) {
          mutateSilently(view, () => {
            if (op === 'tablePreviewRadius') tableEl.style.borderRadius = `${Number(value)}px`;
            else tableEl.style.setProperty('--mica-table-border-width', `${Number(value)}px`);
          });
        }
      }
      return;
    }
    case 'tableRows': { const f = findTable(view.state.selection.$from); if (f) { const info = getTableInfoAt(view, f.pos); if (info) resizeTableAt(view, f.pos, Number(value), info.cols); } break; }
    case 'tableCols': { const f = findTable(view.state.selection.$from); if (f) { const info = getTableInfoAt(view, f.pos); if (info) resizeTableAt(view, f.pos, info.rows, Number(value)); } break; }
    case 'moveRowUp': moveBlockStep(view, 'row', -1); break;
    case 'moveRowDown': moveBlockStep(view, 'row', 1); break;
    case 'moveColLeft': moveBlockStep(view, 'col', -1); break;
    case 'moveColRight': moveBlockStep(view, 'col', 1); break;
    case 'cellBg': setCellsAttr(view, 'background', value ?? null); break;
    case 'cellColor': setCellsAttr(view, 'textColor', value ?? null); break;
    case 'toggleHeader': toggleHeaderRow(view); break;
    case 'copyTable': copyTable(view); return; // copy 自己管选区，不再 view.focus 抢
    default: return;
  }
  view.focus();
}

// ===== 表格边界的键盘交互（对标 Typora）=====
// 表格是块级节点（单元格内含可编辑内容，无法像图片那样做成行内 atom）。PM 默认在「表格相邻处」的退格/Del/Enter
// 会选中整表、卡住、或删不掉空行。这里把四种边界场景显式接管，全部由 createEditor 的 handleKeyDown 先于默认调用。
// 返回 true = 已处理、阻止默认。互斥靠 isInTable：表内 → backspaceInCellToPrev / exitTableOnEnter；表外 →
// backspaceIntoTableEnd / deleteEmptyParagraphBeforeTable。

// 从 $pos 向上找「单元格」所在深度（td/th）。找不到返回 -1。
function cellDepthOf($pos: ResolvedPos): number {
  for (let d = $pos.depth; d > 0; d--) {
    const name = $pos.node(d).type.name;
    if (name === 'table_cell' || name === 'table_header') return d;
  }
  return -1;
}

// Bug 1：光标在「表格正下方」某块的行首按退格 → 进入表格最后一个单元格末尾（而非 PM 默认的选中整表）。
// 当前块若是空段落则顺带删掉（= 删空行，对标 Typora「正常删除」）；非空则只移光标进表、保留文字。
export function backspaceIntoTableEnd(view: ProseEditorView): boolean {
  const { state } = view;
  if (isInTable(state)) return false; // 表内归 backspaceInCellToPrev
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parentOffset !== 0) return false; // 须在某块行首
  const boundary = $from.before();
  const prev = state.doc.resolve(boundary).nodeBefore;
  if (!prev || prev.type.name !== 'table') return false;
  const tablePos = boundary - prev.nodeSize;
  const map = TableMap.get(prev);
  const lastRel = map.map[map.width * map.height - 1]; // 右下角网格 → 覆盖它的单元格 rel
  const lastCellPos = tablePos + 1 + lastRel;
  const lastCell = state.doc.nodeAt(lastCellPos);
  if (!lastCell) return false;
  let tr = state.tr;
  // 空段落顺带删除（表格在删除点之前，tablePos / lastCellPos 不受影响）
  if ($from.parent.type.name === 'paragraph' && $from.parent.content.size === 0) {
    tr = tr.delete(boundary, $from.after());
  }
  const end = lastCellPos + lastCell.nodeSize - 1; // 末单元格内容末尾
  tr = tr.setSelection(Selection.near(tr.doc.resolve(end), -1));
  view.dispatch(tr.scrollIntoView());
  return true;
}

// Bug 4：表格前只有一行空段落、且该空段落是文档首块（前面无块可合并）时，PM 默认退格无反应。
// 这里删掉该空段落 → 表格上移成首块，光标落进表格第一个单元格。（有前块时交回默认：默认能正常合并删空行。）
export function deleteEmptyParagraphBeforeTable(view: ProseEditorView): boolean {
  const { state } = view;
  if (isInTable(state)) return false;
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parent.type.name !== 'paragraph' || $from.parent.content.size !== 0) return false; // 须空段落
  const boundary = $from.before();
  if (state.doc.resolve(boundary).nodeBefore) return false; // 有前块 → 默认能合并删空行，交回默认
  const next = state.doc.resolve($from.after()).nodeAfter;
  if (!next || next.type.name !== 'table') return false;    // 下一块须是表
  let tr = state.tr.delete(boundary, $from.after());        // 删空段落 → 表上移
  tr = tr.setSelection(Selection.near(tr.doc.resolve(boundary + 1), 1)); // 光标进表第一个单元格
  view.dispatch(tr.scrollIntoView());
  return true;
}

// Bug 2：在表格内任意单元格、任意位置按 **Shift+Enter** → 直接跳出表格，光标落到表格下方第一行（对标用户要求）。
// Enter 不拦截 → 单元格内一律正常加段落。表格下方若紧跟一个空段落则复用它（避免反复 Shift+Enter 堆空行），否则
// 在表格后新建空段落；都让光标落到那行行首，可立即输入。
export function exitTableBelow(view: ProseEditorView): boolean {
  const { state } = view;
  if (!isInTable(state)) return false;
  const rect = selectedRect(state);
  const tablePos = rect.tableStart - 1;
  const table = state.doc.nodeAt(tablePos);
  if (!table || table.type.name !== 'table') return false;
  const paragraph = state.schema.nodes.paragraph;
  if (!paragraph) return false;
  const after = tablePos + table.nodeSize;
  const next = state.doc.resolve(after).nodeAfter;
  let tr = state.tr;
  if (!(next && next.type.name === 'paragraph' && next.content.size === 0)) {
    tr = tr.insert(after, paragraph.createAndFill()!); // 下方无现成空行 → 新建
  }
  tr = tr.setSelection(TextSelection.create(tr.doc, after + 1)); // 落到表格下方第一行行首
  view.dispatch(tr.scrollIntoView());
  return true;
}

// Bug 5：光标在某单元格「内容最前」（第一个块的行首）按退格 → 跳到上一个单元格末尾（不删行、不合并、不删单元格），
// 对标 Typora「逐格往前跳、只删内容不删空行」。当前单元格是某行第一格时，上一个单元格 = 上一行最后一格（自然覆盖
// 「删完一行内容跳上一行末格」）。表格第一个单元格（左上角）则跳到表格前（表格是首块时吞掉按键、避免默认选中整表）。
export function backspaceInCellToPrev(view: ProseEditorView): boolean {
  const { state } = view;
  if (!isInTable(state)) return false;
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parentOffset !== 0) return false; // 须在某块行首
  const d = cellDepthOf($from);
  if (d < 0) return false;
  if ($from.before() !== $from.start(d)) return false; // 当前块须是单元格第一个块（光标在单元格内容最前）
  const rect = selectedRect(state);
  const map = rect.map;
  const curIdx = rect.top * map.width + rect.left; // 当前单元格左上格的线性索引
  const tablePos = rect.tableStart - 1;
  if (curIdx === 0) {
    // 左上角单元格 → 跳到表格前（有前块落其末尾）；表格是首块则吞掉按键，避免默认选中整表
    if (tablePos <= 0) return true;
    view.dispatch(state.tr.setSelection(Selection.near(state.doc.resolve(tablePos), -1)).scrollIntoView());
    return true;
  }
  const prevCellPos = rect.tableStart + map.map[curIdx - 1]; // 上一个网格格所属单元格
  const prevCell = state.doc.nodeAt(prevCellPos);
  if (!prevCell) return false;
  const end = prevCellPos + prevCell.nodeSize - 1;
  view.dispatch(state.tr.setSelection(Selection.near(state.doc.resolve(end), -1)).scrollIntoView());
  return true;
}
