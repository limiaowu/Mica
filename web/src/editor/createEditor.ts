import { Editor, rootCtx, defaultValueCtx, editorViewCtx, editorViewOptionsCtx, serializerCtx, parserCtx } from '@milkdown/core';
import { commonmark } from '@milkdown/preset-commonmark';
import {
  toggleStrongCommand,
  toggleEmphasisCommand,
  wrapInHeadingCommand,
  turnIntoTextCommand,
  wrapInBlockquoteCommand,
  createCodeBlockCommand,
  wrapInBulletListCommand,
  wrapInOrderedListCommand,
  insertHrCommand,
} from '@milkdown/preset-commonmark';
// 行内代码 = 原子节点（与行内公式同构，共用 inlineEmbedView 三态 nodeView）。原子节点的光标语义干净
// （节点前/后是清晰的文本位置、方向键整体跳过、不会像 code 标记那样卡在边界/进不去 a`c`b 的缝），这正是
// 标记方案的致命伤。为可靠对接 mdast `inlineCode`，需把 commonmark 自带的 inlineCode「标记」插件过滤掉
// （否则标记与原子节点都认领 `inlineCode` 而冲突）。
import {
  inlineCodeAttr,
  inlineCodeSchema,
  inlineCodeInputRule,
  inlineCodeKeymap,
  toggleInlineCodeCommand,
} from '@milkdown/preset-commonmark';
// 图片改用自定义节点（混合序列化：普通图 ![]()、带样式图 <img>），故把 commonmark 自带的 image
// schema/输入规则/命令按引用剔除（同 inlineCode 套路），否则两个 image 节点冲突。
import {
  imageAttr,
  imageSchema,
  insertImageCommand,
  insertImageInputRule,
  updateImageCommand,
} from '@milkdown/preset-commonmark';
// 空行保真：剔除 commonmark 自带的「空行保留」插件。它把空段落序列化成 `<br />` 标记——既污染 .md，
// 又与本项目「<br/> 是行内 HTML 换行元素」冲突，还导致源码里敲的「真·空行」不被保留。剔除后空段落
// 序列化成真·空段，改由 micaBlankLineRemark（解析补空段 + 序列化 join 定制）做真·空行往返（见 blankLine.ts）。
import { remarkPreserveEmptyLinePlugin } from '@milkdown/preset-commonmark';
import { gfm } from '@milkdown/preset-gfm';
import { toggleStrikethroughCommand } from '@milkdown/preset-gfm';
// 全面改用 HTML 表格（见 htmlTable.ts / docs/表格HTML改造计划.md）：把 gfm 自带的表格节点/插件/输入规则/
// 键位/命令按引用从 gfm 数组里剔除（保留删除线/任务列表/autolink 与 remarkGFM）。
import {
  tableSchema as gfmTableSchema,
  tableHeaderRowSchema as gfmTableHeaderRowSchema,
  tableRowSchema as gfmTableRowSchema,
  tableHeaderSchema as gfmTableHeaderSchema,
  tableCellSchema as gfmTableCellSchema,
  keepTableAlignPlugin,
  autoInsertSpanPlugin,
  tableEditingPlugin as gfmTableEditingPlugin,
  insertTableInputRule,
  tableKeymap as gfmTableKeymap,
  goToNextTableCellCommand,
  goToPrevTableCellCommand,
  exitTable,
  insertTableCommand,
  moveRowCommand,
  moveColCommand,
  selectRowCommand,
  selectColCommand,
  selectTableCommand,
  deleteSelectedCellsCommand,
  addRowBeforeCommand,
  addRowAfterCommand,
  addColBeforeCommand,
  addColAfterCommand,
  setAlignCommand,
} from '@milkdown/preset-gfm';
import {
  tableSchema as micaTableSchema,
  tableRowSchema as micaTableRowSchema,
  tableCellSchema as micaTableCellSchema,
  tableHeaderSchema as micaTableHeaderSchema,
  micaTableRemark,
  tableProsePlugins,
  parseHtmlTable,
  enforceHeaderCoverage,
  backspaceIntoTableEnd,
  deleteEmptyParagraphBeforeTable,
  exitTableBelow,
  backspaceInCellToPrev,
  normalizeTablePasteSlice,
  tableSelectAll,
} from './htmlTable';
import { isInTable } from '@milkdown/prose/tables';
import { history } from '@milkdown/plugin-history';
import { undoCommand, redoCommand } from '@milkdown/plugin-history';
import { listener, listenerCtx } from '@milkdown/plugin-listener';
import { clipboard } from '@milkdown/plugin-clipboard';
import { trailing } from '@milkdown/plugin-trailing';
import { math, mathBlockSchema, mathInlineSchema } from '@milkdown/plugin-math'; // KaTeX 公式：$...$ 行内、$$ 围栏块级
import { codeBlockComponent, codeBlockConfig } from '@milkdown/components/code-block'; // CodeMirror 6 代码块
import { languages } from '@codemirror/language-data';                         // 语言选择器列表 + 语法按需懒加载
import { buildCodeMirrorExtensions, codeBlockIcons } from './codeSetup';
import { createMathBlockView, renderMathInline, renderMathPreview } from './mathView';
import { createInlineEmbedView, setNextOpenAtStart } from './inlineEmbedView'; // 行内公式/行内代码共用可编辑 nodeView（三态显露）
import {
  imageNode,
  micaImageRemark,
  imageInputRule,
  setImageBaseDir,
  buildImageNode,
  createImageNodeView,
} from './imageNode'; // 图片节点（替换 commonmark image）+ 显示 URL 基准目录 + NodeView（原地改样式不重载）
import type { ImageAttrs } from './imageNode';
import {
  imageGroupNode,
  micaImageGroupRemark,
  createImageGroupNodeView,
  imageGroupActivePlugin,
  imageGroupActiveKey,
  buildImageGroupNode,
  defaultImage,
  insertImagesInGroup,
  getActiveGroupImage,
  getGroupSelection,
  deleteGroupImages,
} from './imageGroup'; // 图片组容器节点（第四期：行内 atom，子图存 attrs.images；NodeView 自掌 DOM）
import { galleryRegistry, imageDropKey } from './imageDrag'; // 图册登记表 + 文档落点占位条位置 key
import {
  htmlBlockNode,
  htmlInlineNode,
  micaHtmlRemark,
  createHtmlBlockView,
  createHtmlInlineView,
  htmlPairedInputRule,
  htmlSelfCloseInputRule,
  htmlBrInputRule,
  htmlCloseTagInputRule,
  detailsNode,
  detailsSummaryNode,
  createDetailsView,
} from './htmlNode'; // 通用 HTML：块级/行内自定义节点 + 渲染 NodeView + remark 接管 + 即时输入规则（对标 Typora）
import { micaBlankLineRemark } from './blankLine'; // 源码模式空行保真：解析补空段 + 序列化定制 join（对标 Typora）
import { setupTableContextMenu } from './tableSetup'; // 表格右键菜单（增删行列/对齐/删表）
import { setupImageContextMenu } from './imageSetup'; // 图片右键菜单（复制/路径/资源管理器/对齐/删除）
import { setupTableToolbar } from './tableToolbar'; // 表格非缩放浮层 + 行/列拖动重排管理（浮条已下线，仅 reorder）
import { getMarkdown, $prose, $view, $nodeSchema } from '@milkdown/utils';
import { Fragment, Slice } from '@milkdown/prose/model';
import type { Node as ProseNode } from '@milkdown/prose/model';
import { selectAll } from '@milkdown/prose/commands';
import { wrapInList } from '@milkdown/prose/schema-list'; // 任务列表：先包无序列表再设 checked
import { keymap } from '@milkdown/prose/keymap';
import { InputRule, inputRules } from '@milkdown/prose/inputrules'; // 行内代码「`x` 闭合反引号 → 原子节点」输入规则
import { gapCursor } from '@milkdown/prose/gapcursor'; // 让光标能停到块（代码块/公式块）上下空隙，可在首行块前另起一行
import { TextSelection, NodeSelection, Selection, Plugin, PluginKey } from '@milkdown/prose/state';
import type { Command, EditorState, Transaction } from '@milkdown/prose/state';
import { Decoration, DecorationSet } from '@milkdown/prose/view';
import type { EditorView as ProseEditorView } from '@milkdown/prose/view';
import { notifyContentChange, notifyStats, notifyOutline, notify, request } from '../ipc/bridge';
import { createSourceView, getSourceDoc, setSourceDoc } from './sourceMode'; // 源代码模式 = CodeMirror 6（行号/语法高亮）
import type { EditorView as CmEditorView } from '@codemirror/view';

let currentRelPath: string | null = null;
let debounceTimer: ReturnType<typeof setTimeout> | null = null;
let editorInstance: Editor | null = null;
// 当前 ProseMirror view（create 后缓存）。供代码块 CM 扩展跨层拿到 PM view 用（见 codeSetup）。
let proseView: ProseEditorView | null = null;
let lastSelection: { from: number; to: number } | null = null;
let editorFocused = false;
// Mutable so the host can retune the auto-save debounce from the settings page
// (editor.config IPC). Defaults to 500ms.
let debounceMs = 500;
// 代码块是否显示行号（来自设置，editor.config 推送）。改动需重建编辑器才作用于已有代码块。
let showLineNumbers = true;
// 图片删除二次确认（来自设置 editor.config）。开：光标在图前/后按 Backspace/Del 先标红框、再按才删；关：直接删（默认）。
let imageDeleteConfirm = false;

// Source-code mode: the outer #editor element holds two children — a div that
// Milkdown mounts into (WYSIWYG) and a container (.mica-source) that hosts a
// CodeMirror 6 instance showing the raw markdown (行号 + 语法高亮，见 sourceMode.ts)。
// We toggle their visibility instead of tearing the editor down each time。
let rootEl: HTMLElement | null = null;
let wysiwygEl: HTMLDivElement | null = null;
// 表格 reorder 管理器句柄（单例、跨文件复用）：随光标跟表、文档变化刷新把手、zoom 后重定位（浮条已下线，仅 reorder）。
let tableToolbar: { onDocChanged: () => void; onSelectionChanged: (sel?: Selection) => void; reposition: () => void } | null = null;
let sourceEl: HTMLDivElement | null = null;       // .mica-source 容器（CodeMirror 挂载点）
let sourceView: CmEditorView | null = null;       // 源码模式 CodeMirror 实例（按需创建）
let sourceMode = false;

// Milkdown $command plugins get .run() set after editor.create()
type CmdPlugin = { run?: (payload?: unknown) => boolean };

const commandRegistry: Record<string, { cmd: CmdPlugin; payload?: unknown }> = {
  bold: { cmd: toggleStrongCommand as unknown as CmdPlugin },
  italic: { cmd: toggleEmphasisCommand as unknown as CmdPlugin },
  // inlineCode 不走 registry：它是原子节点，由 executeCommand 专门处理（无选区→插空节点开编辑、有选区→包裹）。
  strikethrough: { cmd: toggleStrikethroughCommand as unknown as CmdPlugin },
  heading1: { cmd: wrapInHeadingCommand as unknown as CmdPlugin, payload: 1 },
  heading2: { cmd: wrapInHeadingCommand as unknown as CmdPlugin, payload: 2 },
  heading3: { cmd: wrapInHeadingCommand as unknown as CmdPlugin, payload: 3 },
  heading4: { cmd: wrapInHeadingCommand as unknown as CmdPlugin, payload: 4 },
  heading5: { cmd: wrapInHeadingCommand as unknown as CmdPlugin, payload: 5 },
  heading6: { cmd: wrapInHeadingCommand as unknown as CmdPlugin, payload: 6 },
  paragraph: { cmd: turnIntoTextCommand as unknown as CmdPlugin },
  blockquote: { cmd: wrapInBlockquoteCommand as unknown as CmdPlugin },
  codeBlock: { cmd: createCodeBlockCommand as unknown as CmdPlugin },
  bulletList: { cmd: wrapInBulletListCommand as unknown as CmdPlugin },
  orderedList: { cmd: wrapInOrderedListCommand as unknown as CmdPlugin },
  hr: { cmd: insertHrCommand as unknown as CmdPlugin },
  // 表格不走 registry：改用 HTML 表格节点，由 insertTable() 直接建节点插入（见下）。
  undo: { cmd: undoCommand as unknown as CmdPlugin },
  redo: { cmd: redoCommand as unknown as CmdPlugin },
};

// ===== 行内代码：原子节点（替换 commonmark 的 inlineCode 标记）=====
// 与公式 math_inline 同构：inline atom 节点、值存文本内容、parse/serialize 对接 mdast `inlineCode`。
// 反引号由 nodeView（inlineEmbedView）渲染，故进出/选择/整体增删可靠（原子节点的光标语义干净，
// 不会有 code 标记那种「卡在边界、a`c`b 缝里点不进」的 bug）。代价：反引号整体增删、不能单独删一个（已认可）。
const inlineCodeNode = $nodeSchema('inline_code', () => ({
  group: 'inline',
  content: 'text*',
  inline: true,
  atom: true,
  parseDOM: [
    {
      tag: 'code[data-type="inline_code"]',
      getContent: (dom: Node, schema: import('@milkdown/prose/model').Schema) =>
        Fragment.from(
          schema.text((dom as HTMLElement).dataset.value ?? (dom as HTMLElement).textContent ?? ''),
        ),
    },
  ],
  toDOM: (node) => {
    const el = document.createElement('code');
    el.dataset.type = 'inline_code';
    el.dataset.value = node.textContent;
    el.textContent = node.textContent;
    return el;
  },
  parseMarkdown: {
    match: (node) => node.type === 'inlineCode',
    runner: (state, node, type) => {
      state.openNode(type).addText((node.value as string | undefined) ?? '').closeNode();
    },
  },
  toMarkdown: {
    match: (node) => node.type.name === 'inline_code',
    runner: (state, node) => {
      state.addNode('inlineCode', undefined, node.textContent);
    },
  },
}));

// 从 commonmark 插件数组里剔除「inlineCode 标记」那组插件（按引用过滤）。
// 注意：$markSchema/$useKeymap 的返回值是 [ctx, schema] 这种「元组」，而 commonmark 数组是 .flat()
// 过的——里面装的是元组里的**内部元素**、不是元组本身。故这里把数组型导出展开后再加入排除集，
// 否则 schema/keymap 删不掉、inlineCode 标记会和上面的原子节点同时认领 mdast `inlineCode` 而冲突。
// 同时剔除 inlineCode 标记组 + image 组（都换成自定义节点）。元组型导出展开后排除（见上注释）。
const commonmarkExclusions = new Set<unknown>();
for (const e of [
  inlineCodeAttr, inlineCodeSchema, inlineCodeInputRule, inlineCodeKeymap, toggleInlineCodeCommand,
  imageAttr, imageSchema, insertImageCommand, insertImageInputRule, updateImageCommand,
  remarkPreserveEmptyLinePlugin, // 空行保真：剔除自带 <br /> 空行机制，改用真·空行（见上方导入注释 / blankLine.ts）
]) {
  if (Array.isArray(e)) e.forEach((x) => commonmarkExclusions.add(x));
  else commonmarkExclusions.add(e);
}
const commonmarkFiltered = (commonmark as unknown as unknown[]).filter(
  (p) => !commonmarkExclusions.has(p),
) as typeof commonmark;

// 同理从 gfm 数组里剔除「表格」那组插件（schema/plugin/输入规则/键位/命令均按引用排除，元组型展开）。
// 保留 remarkGFM（删除线/任务列表/autolink 仍需）、删除线 mark/命令、任务列表扩展。
const gfmTableExclusions = new Set<unknown>();
for (const e of [
  gfmTableSchema, gfmTableHeaderRowSchema, gfmTableRowSchema, gfmTableHeaderSchema, gfmTableCellSchema,
  keepTableAlignPlugin, autoInsertSpanPlugin, gfmTableEditingPlugin,
  insertTableInputRule, gfmTableKeymap,
  goToNextTableCellCommand, goToPrevTableCellCommand, exitTable, insertTableCommand,
  moveRowCommand, moveColCommand, selectRowCommand, selectColCommand, selectTableCommand,
  deleteSelectedCellsCommand, addRowBeforeCommand, addRowAfterCommand,
  addColBeforeCommand, addColAfterCommand, setAlignCommand,
]) {
  if (Array.isArray(e)) e.forEach((x) => gfmTableExclusions.add(x));
  else gfmTableExclusions.add(e);
}
const gfmNoTable = (gfm as unknown as unknown[]).filter((p) => !gfmTableExclusions.has(p)) as typeof gfm;

// `x` 闭合反引号 → 行内代码原子节点（对标 plugin-math 的 mathInlineInputRule：输入闭合区分符才触发）。
// 这是修「单个反引号即触发、吞掉 ``` 围栏」的关键：不再用 handleTextInput 拦开头反引号，改成闭合触发。
// inputRules 机制：textBefore = 块内光标前文本 + 刚输入的反引号；匹配 `(`)([^`]+)(`)$` 后，[start,end]
// 覆盖文档里已有的「`code」（不含刚输入的闭合反引号，它被消费掉），整段替换为含 code 文本的原子节点。
const inlineCodeInputRuleAtom = new InputRule(/(?:`)([^`]+)(?:`)$/, (state, match, start, end) => {
  const type = state.schema.nodes.inline_code;
  const content = match[1];
  if (!type || !content) return null;
  // 在代码块（CodeMirror）里不触发——那里的输入由 CM 处理，不会进到这套 PM 输入规则，这里仅作保险。
  const $start = state.doc.resolve(start);
  if (!$start.parent.inlineContent) return null;
  return state.tr.replaceWith(start, end, type.create(null, state.schema.text(content)));
});

// 去掉多行文本的「公共前导缩进」：取所有非空行里最小的前导空白长度，逐行切掉。
// 用于粘贴代码——从 VS Code 等编辑器复制的片段会带着它在源文件里的缩进，整体右移很难看。
function dedentCommonIndent(text: string): string {
  const lines = text.split('\n');
  let min = Infinity;
  for (const line of lines) {
    if (!line.trim()) continue; // 空行/纯空白行不参与计算
    const m = /^[ \t]*/.exec(line);
    const n = m ? m[0].length : 0;
    if (n < min) min = n;
  }
  if (!Number.isFinite(min) || min === 0) return text;
  return lines.map((l) => (l.trim() ? l.slice(min) : l)).join('\n');
}

// 粘贴拦截：从 VS Code（及任何写 `vscode-editor-data` 剪贴板格式的编辑器）复制代码时，milkdown 的
// clipboard 插件会据此自动建代码块、并把文本**原样**插入——于是源文件里的缩进被一并带进来，整段右移
// （用户反馈「偏移太严重」）。这里以「直接 view prop」抢在 clipboard 插件之前接管该情形：去掉公共
// 前导缩进后再建块插入，代码块就贴左对齐。其它粘贴一律 return false，交回 clipboard 插件默认处理。
function handleCodePaste(view: ProseEditorView, event: ClipboardEvent): boolean {
  const cd = event.clipboardData;
  if (!cd) return false;
  const vscodeData = cd.getData('vscode-editor-data');
  if (!vscodeData) return false;
  let language: string | undefined;
  try { language = JSON.parse(vscodeData)?.mode; } catch { return false; }
  const text = cd.getData('text/plain');
  if (!text || !language) return false;
  // 已经在代码节点（CodeMirror 代码块）里粘贴 → 交回默认，避免代码块套代码块
  if (view.state.selection.$from.node().type.spec.code) return false;
  const codeType = view.state.schema.nodes.code_block;
  if (!codeType) return false;
  const dedented = dedentCommonIndent(text.replace(/\r\n?/g, '\n'));
  // 直接「带内容」建代码块（文本作为子节点一次创建），替换选区——而非「先插空块→挪光标→insertText」那套：
  // 后者的挪光标用 `selection.from - 2` 估算块内位置，遇到块在文档边界等情况会落到块外，于是文本被插到
  // 别处、只剩一个空代码块（用户反馈「跑到上方 / 空代码块」的根因）。带内容直接建则插哪儿都在光标处、内容必在块内。
  const codeNode = codeType.create({ language }, dedented ? view.state.schema.text(dedented) : null);
  view.dispatch(view.state.tr.replaceSelectionWith(codeNode).scrollIntoView());
  view.focus();
  return true;
}

// ===== 粘贴图片 → 落盘 → 插入图片节点 =====
// 编辑器无法写磁盘，故把图片字节（base64）经 IPC image.save 交给宿主按存储模式落盘，宿主回传要写进 .md
// 的 src（相对/绝对路径或 data URI），web 再插入图片节点。落盘是异步的，故 handlePaste 返回 true
// 表示「已接管」后用 editorInstance.action 取最新 view 异步插入（原 view 可能已过期）。（拖入暂未实现，见下）
function extFromImageType(type: string): string | null {
  switch (type) {
    case 'image/png': return 'png';
    case 'image/jpeg': return 'jpg';
    case 'image/gif': return 'gif';
    case 'image/webp': return 'webp';
    case 'image/svg+xml': return 'svg';
    case 'image/bmp': return 'bmp';
    case 'image/avif': return 'avif';
    case 'image/tiff': return 'tiff';
    default: return null;
  }
}

function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => {
      const s = String(r.result);
      const i = s.indexOf(','); // 去掉 "data:<type>;base64," 前缀，只留 base64 主体
      resolve(i >= 0 ? s.slice(i + 1) : s);
    };
    r.onerror = () => reject(r.error ?? new Error('read failed'));
    r.readAsDataURL(file);
  });
}

// 把若干图片 File 落盘后放进文档（保序：Promise.all 后按原序取 src）。
async function saveAndPlaceImages(files: File[]) {
  if (!currentRelPath || !editorInstance) return;
  try {
    const srcs = (await Promise.all(files.map(saveImageFile))).filter((s): s is string => !!s);
    placeImageSrcs(srcs);
  } catch (e) {
    console.warn('image save/insert failed:', e);
  }
}

// 把一个图片 File 落盘，返回要写进 .md 的 src（失败返回 null）。供单图/多图共用。
async function saveImageFile(file: File): Promise<string | null> {
  if (!currentRelPath) return null;
  const dataBase64 = await fileToBase64(file);
  const ext = extFromImageType(file.type)
    ?? (file.name.match(/\.([a-zA-Z0-9]+)$/)?.[1]?.toLowerCase() ?? 'png');
  const res = await request<{ src?: string }>('image.save', {
    noteAbsPath: currentRelPath, dataBase64, ext, sourceName: file.name ?? '',
  });
  return res?.src ?? null;
}

// 把若干已落盘的图 src 放进文档：
//   ① 当前选中的是图片组容器（NodeSelection 落在 imageGroup 上）→ 追加进该组（用户直觉：往组里加图，而非替换整组）；
//   ② 否则按数量插入——1 张→普通行内图，≥2 张→新建图片组（第四期入口①：多选粘贴自动成组）。
// 导出供宿主「插入图片」（editor.insertImages）复用：宿主多选文件落盘后回传 srcs，落点规则同上。
export function placeImageSrcs(srcs: string[]) {
  if (srcs.length === 0 || !editorInstance) return;
  editorInstance.action((ctx) => {
    const v = ctx.get(editorViewCtx);
    const sel = v.state.selection;
    // ① 选中容器 → 追加到组尾（保持容器选中，便于连续追加）。
    if (sel instanceof NodeSelection && sel.node.type.name === 'imageGroup') {
      const pos = sel.from;
      const node = sel.node;
      const cur = (node.attrs.images as ImageAttrs[]) || [];
      // 追加到末行行尾（维护 rows，否则 Σrows≠图数会被规整成单行、把已有多行布局打散）。
      const { images, rows } = insertImagesInGroup(cur, node.attrs.rows, cur.length, srcs.map(defaultImage));
      const tr = v.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, images, rows });
      tr.setSelection(NodeSelection.create(tr.doc, pos)).scrollIntoView();
      v.dispatch(tr);
      v.focus();
      return;
    }
    // ② 否则按数量插入。
    const node = srcs.length === 1
      ? buildImageNode(v.state.schema, srcs[0], '')
      : buildImageGroupNode(v.state.schema, srcs);
    if (!node) return;
    v.dispatch(v.state.tr.replaceSelectionWith(node).scrollIntoView());
    v.focus();
  });
}

// 宿主外部文件拖入（资源管理器拖图 → EditorHostView 落盘 → editor.dropImages{srcs,x,y}）：先据视口坐标(x,y)把选区
// 落到目标（命中图册→追加进册、否则光标落到文档落点），再复用 placeImageSrcs 插入。x/y 是相对 WebView 视口的 DIP
// ≈ CSS 像素（同 host.imageMenu 的坐标约定），posAtCoords/elementFromPoint 直接可用。
export function placeImageSrcsAt(srcs: string[], x: number, y: number) {
  if (srcs.length === 0 || !editorInstance) return;
  const view = proseView;
  if (view) selectDropTarget(view, x, y);
  placeImageSrcs(srcs);
}

// 新建空图册（宿主「新建图册」/ Ctrl+Shift+G → editor.newGallery）：在选区插入一个空 imageGroup（占位态）。
// 空图册不再被 cleanup 删除（已下线该插件），可后续点占位/粘贴/拖图加图。
export function insertEmptyGallery() {
  if (!editorInstance) return;
  editorInstance.action((ctx) => {
    const v = ctx.get(editorViewCtx);
    const type = v.state.schema.nodes.imageGroup;
    if (!type) return;
    const grp = type.create({ rows: [], images: [], frameless: false });
    const tr = v.state.tr.replaceSelectionWith(grp);
    // 选中刚插入的空图册（占位可见被选中态）：插入点在 selection.from 处。
    tr.setSelection(NodeSelection.create(tr.doc, v.state.selection.from)).scrollIntoView();
    v.dispatch(tr);
    v.focus();
  });
}

// 复制组内图：PM 选区是 NodeSelection(整个容器)，默认复制会吐出整个容器 div（用户反馈「复制一张却粘出整组」）。
// 改写复制内容以贴合直觉「选一张就是普通图片，选两张以上才是创建容器」：选 1 张 → 单图节点（粘贴成 ![]() 普通图）；
// 选 ≥2 张 → 仅含这些图的新容器（粘贴=新建组）。无组内选中（独立图/文本）则原样返回交回默认。
function transformGroupCopied(slice: Slice, view: ProseEditorView): Slice {
  const sel = getGroupSelection(view);
  if (!sel) return slice;
  const node = view.state.doc.nodeAt(sel.pos);
  if (!node || node.type.name !== 'imageGroup') return slice;
  const imgs = (node.attrs.images as ImageAttrs[]) || [];
  const picked = sel.indices.map((i) => imgs[i]).filter((a): a is ImageAttrs => !!a);
  if (picked.length === 0) return slice;
  const schema = view.state.schema;
  if (picked.length === 1) {
    const img = schema.nodes.image?.create({ ...picked[0] }); // 带完整属性（裁剪/翻转/边框圆角一并复制）
    return img ? new Slice(Fragment.from(img), 0, 0) : slice;
  }
  const grp = schema.nodes.imageGroup?.create({
    rows: [picked.length], frameless: node.attrs.frameless, images: picked.map((a) => ({ ...a })), // 复制出的新册默认单行
  });
  return grp ? new Slice(Fragment.from(grp), 0, 0) : slice;
}

// 剪切组内图：PM 原生 cut = 复制（transformCopied 已正确给单图/多图）+ deleteSelection（删整个容器）。删整册与
// 「复制只取选中的几张」不一致（用户反馈：剪一张却把整册剪掉）。这里接管 cut：剪贴板照常写（复用 serializeForClipboard
// → 经 transformGroupCopied 得单图/多图），但只删当前激活的那几张（删光由 cleanup 收尾），与复制语义一致。
// 无组内选中（独立图 / 文本 / 整册整体选中）→ 返回 false 交回 PM 默认 cut。
function handleGroupCut(view: ProseEditorView, event: ClipboardEvent): boolean {
  const sel = getGroupSelection(view);
  if (!sel) return false;
  const data = event.clipboardData;
  if (!data) return false; // 无 clipboardData（极少）→ 交回默认，至少不丢内容
  const { dom, text } = view.serializeForClipboard(view.state.selection.content());
  event.preventDefault();
  data.clearData();
  data.setData('text/html', dom.innerHTML);
  data.setData('text/plain', text);
  deleteGroupImages(view, sel.pos, sel.indices); // 只删选中的图（与复制一致），不删整册
  return true;
}

// 粘贴里有图片文件 → 接管（落盘+放置），返回 true 阻止默认粘贴。getAsFile 必须在事件内同步取。
// 放置规则见 placeImageSrcs：选中容器→追加进组；否则单张→普通图、多张→自动成组。
function handleImagePaste(_view: ProseEditorView, event: ClipboardEvent): boolean {
  const items = event.clipboardData?.items;
  if (!items) return false;
  const files: File[] = [];
  for (const it of items) {
    if (it.kind === 'file' && it.type.startsWith('image/')) {
      const f = it.getAsFile();
      if (f) files.push(f);
    }
  }
  if (files.length === 0) return false;
  void saveAndPlaceImages(files);
  return true;
}
// ===== 原生拖放（直接挂 wysiwygEl 的 DOM 事件，见 createEditor 接线处注释）=====
// dragstart：禁掉编辑器内一切原生 HTML5 drag（图片/图册/文本统一走自绘指针拖拽 imageDrag）。根治原生 drag 劫持指针
// 导致的「松手不落 / ⊘ / 图册变灰（原生选区 ::selection 灰底）」。
function onEditorDragStart(e: DragEvent) {
  e.preventDefault();
}
// dragenter/dragover：preventDefault 才能接收外部文件拖放（否则 WebView2 显示 ⊘）。挂在 document、覆盖全视口，故须
// **只接管「含文件」的拖放**——否则会把源码模式 textarea 里的文本拖放也 preventDefault 掉。若某些情况下 WebView2
// dragover 期 types 不含 'Files'（历史疑虑），web 路这里不接管也无妨：宿主 RootGrid 路会兜底收下（两路互斥）。
function onEditorDragOver(e: DragEvent) {
  if (!e.dataTransfer || !Array.from(e.dataTransfer.types).includes('Files')) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = 'copy';
}
// 把选区落到落点（命中某图册→选中它，使 placeImageSrcs 追加进册；否则光标落到文档落点）。供 web 原生 drop 与
// 宿主外部文件拖入（placeImageSrcsAt）共用。
function selectDropTarget(view: ProseEditorView, x: number, y: number) {
  const gdom = (document.elementFromPoint(x, y) as HTMLElement | null)?.closest('.mica-image-group') as HTMLElement | null;
  const gpos = gdom ? galleryRegistry.get(gdom)?.() : undefined;
  if (gpos != null) {
    try { view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, gpos))); } catch { /* 忽略 */ }
  } else {
    const coords = view.posAtCoords({ left: x, top: y });
    if (coords) {
      try { view.dispatch(view.state.tr.setSelection(Selection.near(view.state.doc.resolve(coords.pos)))); } catch { /* 忽略非法落点 */ }
    }
  }
}

// drop：取图片文件，落盘 + 插入。落到某图册上 → 选中它（placeImageSrcs 见 NodeSelection(imageGroup) 会**追加进册**）；
// 否则把光标落到落点 → placeImageSrcs 在此插入，单图经独占逻辑自动独占一行。
// 外部文件拖入有两条可能路径，互斥（OLE 单一落点）：① WebView2 把外部拖放转发进 DOM → 走这里；② WebView2 不转发、
// 拖放落到宿主 XAML（EditorHostView 的 RootGrid）→ 走宿主 editor.dropImages → placeImageSrcsAt。两条都接上，谁收到谁处理。
function onEditorDrop(e: DragEvent) {
  const dt = e.dataTransfer;
  if (!dt || !dt.files || dt.files.length === 0) return;
  const files = Array.from(dt.files).filter((f) => f.type.startsWith('image/'));
  if (files.length === 0) return;
  e.preventDefault();
  const view = proseView;
  if (!view) return;
  selectDropTarget(view, e.clientX, e.clientY);
  void saveAndPlaceImages(files);
}

// 外部文件拖入的放置区：挂 document（覆盖全视口 → 任何位置都 preventDefault，去掉 ⊘）。只挂一次——document 跨编辑器
// 重建持续存在，避免每次 createEditor 叠加重复监听。处理器在事件触发时读 proseView 取最新编辑器实例。
let documentDropWired = false;
function wireDocumentExternalDrop() {
  if (documentDropWired) return;
  documentDropWired = true;
  document.addEventListener('dragenter', onEditorDragOver);
  document.addEventListener('dragover', onEditorDragOver);
  document.addEventListener('drop', onEditorDrop);
}

// 按住 Ctrl/Cmd 时给 body 挂 `mica-mod-held` 类 → CSS 把链接的鼠标指针换成手型（提示「可点击跳转/打开」，
// 对标 VS Code/Typora，用户 #11）。松开/失焦即移除。挂 document 一次（跨编辑器重建持续有效）。
let modKeyCursorWired = false;
function wireModKeyCursor() {
  if (modKeyCursorWired) return;
  modKeyCursorWired = true;
  const sync = (on: boolean) => document.body.classList.toggle('mica-mod-held', on);
  document.addEventListener('keydown', (e) => { if (e.key === 'Control' || e.key === 'Meta' || e.ctrlKey || e.metaKey) sync(true); });
  document.addEventListener('keyup', (e) => { if (e.key === 'Control' || e.key === 'Meta') sync(false); if (!e.ctrlKey && !e.metaKey) sync(false); });
  window.addEventListener('blur', () => sync(false)); // 切走窗口时清掉，避免卡在手型
}

export async function createEditor(root: HTMLElement, initialMarkdown: string, relPath: string) {
  // 兜底剥除哨兵字符：历史上若某次源码↔渲染切换把哨兵焊进了行内原子（<sub>2</sub> 等）并存进了 .md，
  // 它只在「下次切源码」时才被 scanSentinels 清掉、普通打开文件不清 → 渲染成「空」方框（用户 #2）。
  // 故每次打开文件都先剥一遍，保证任何遗留哨兵自愈。正文几乎不会真出现 U+E000，安全。
  if (initialMarkdown.includes(CURSOR_SENTINEL)) initialMarkdown = initialMarkdown.split(CURSOR_SENTINEL).join('');
  currentRelPath = relPath;
  setImageBaseDir(relPath); // 图片显示 URL 的基准目录（relPath = 笔记绝对路径）
  lastSelection = null;
  editorFocused = false;
  rootEl = root;

  // Tear down any previous editor (switching files reuses #editor).
  if (editorInstance) {
    try { await editorInstance.destroy(); } catch { /* ignore */ }
    editorInstance = null;
    proseView = null;
  }
  // 销毁上一文件的源码模式 CodeMirror（root.innerHTML 清空只移 DOM、不释放 CM 监听）。
  if (sourceView) {
    try { sourceView.destroy(); } catch { /* ignore */ }
    sourceView = null;
  }
  root.innerHTML = '';

  wysiwygEl = document.createElement('div');
  wysiwygEl.className = 'mica-wysiwyg';
  root.appendChild(wysiwygEl);

  // 源码模式容器：CodeMirror 实例首次进入源码模式时才创建（见 showSourceView）。
  sourceEl = document.createElement('div');
  sourceEl.className = 'mica-source';
  sourceEl.style.display = 'none';
  root.appendChild(sourceEl);

  // IME composition tracking (see computeAndSendStats). WYSIWYG 的监听挂在每文件新建的
  // wysiwygEl 上（不叠加）；源码模式的监听在 createSourceView 内挂到 CM 的 contentDOM。
  wysiwygEl.addEventListener('compositionstart', onCompositionStart);
  wysiwygEl.addEventListener('compositionend', onCompositionEnd);
  // 点击正文最后一块「下方」的空白区 → 自动到文末（末块不是空段落就补一个空段落），对标 Typora。
  wysiwygEl.addEventListener('mousedown', onClickBelowContent);
  // dragstart 一律 preventDefault → 禁掉编辑器内一切原生 HTML5 drag（图片/图册/文本），改走自绘指针拖拽（imageDrag）。
  // 这是根治「松手不落、⊘ 禁止图标、图册变灰」的关键：原生 drag 会劫持指针让 mouseup 不触发。挂 wysiwygEl（每文件新建、不叠加）。
  wysiwygEl.addEventListener('dragstart', onEditorDragStart);
  // 外部文件拖入的放置区挂 document（见 wireDocumentExternalDrop）：全视口覆盖，避免只在编辑器小块上才不显示 ⊘。
  wireDocumentExternalDrop();
  // 按住 Ctrl/Cmd 时链接显示手型指针（提示可点击跳转/打开，用户 #11）。
  wireModKeyCursor();
  // 图片右键菜单：右键点在图片上时弹出（复制/路径/资源管理器/对齐/删除）。先于表格菜单注册 → 图片优先。
  setupImageContextMenu(wysiwygEl, () => proseView);
  // 表格右键菜单：右键点在表格单元格内时弹出（增删行列/对齐/删表）。getView 取实时 PM view。
  setupTableContextMenu(wysiwygEl, () => proseView);
  // 表格 reorder 管理器：维护非缩放浮层 + 行/列拖动重排把手（开关在右键菜单「排序」）。
  tableToolbar = setupTableToolbar(wysiwygEl, () => proseView);

  await buildMilkdown(initialMarkdown);

  // Preserve the source/WYSIWYG preference across file switches.
  if (sourceMode) {
    wysiwygEl.style.display = 'none';
    showSourceView(initialMarkdown);
  }

  computeAndSendStats(initialMarkdown);
  return editorInstance;
}

// Word/character counts for the status bar. Counted on the raw markdown — good
// enough; words split on whitespace, chars exclude whitespace so CJK text (which
// has no spaces) still gets a meaningful number.
//
// IME-aware, matching Typora: while composing pinyin/IME input, ProseMirror fires
// intermediate transactions (the latin spelling appears, then collapses into the
// CJK char), which made the count jump up and back down. Instead of recounting the
// in-progress spelling, we freeze on the last committed count and show "+1 char"
// (one character is being composed); the real count is recomputed only once the
// character actually commits.
let composing = false;
let committedWords = 0;
let committedChars = 0;

function computeAndSendStats(md: string) {
  if (composing) return; // ignore intermediate composition transactions
  const text = md ?? '';
  const words = (text.match(/\S+/g) ?? []).length;
  const chars = text.replace(/\s/g, '').length;
  committedWords = words;
  committedChars = chars;
  notifyStats(words, chars);
  computeAndSendOutline(text);
}

// 去掉标题文本里的内联 markdown 标记，让大纲显示「效果后」的纯文本而非字面量
// （如 `**重要**` → `重要`、`[链接](url)` → `链接`）。顺序要紧：先处理图片/链接，
// 再处理强调/代码/删除线/行内公式的成对定界符。
function stripInlineMarkdown(s: string): string {
  return s
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')   // 图片 ![alt](url) → alt
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')     // 链接 [text](url) → text
    .replace(/(\*\*|__)(.*?)\1/g, '$2')           // 加粗 **x** / __x__
    .replace(/(\*|_)(.*?)\1/g, '$2')              // 斜体 *x* / _x_
    .replace(/~~(.*?)~~/g, '$1')                   // 删除线 ~~x~~
    .replace(/`([^`]*)`/g, '$1')                   // 行内代码 `x`
    .replace(/\$([^$]*)\$/g, '$1')                 // 行内公式 $x$
    .trim();
}

// Extract ATX headings from the raw markdown (works for both WYSIWYG and source
// mode) and push them to the host outline panel. Fenced code blocks are skipped so
// commented-out "# foo" inside ``` doesn't show up as a heading.
function computeAndSendOutline(md: string) {
  const items: { level: number; text: string }[] = [];
  let inFence = false;
  let fence = '';
  for (const rawLine of (md ?? '').split('\n')) {
    const line = rawLine;
    const fenceMatch = line.match(/^\s*(```|~~~)/);
    if (fenceMatch) {
      if (!inFence) { inFence = true; fence = fenceMatch[1]; }
      else if (line.trimStart().startsWith(fence)) { inFence = false; }
      continue;
    }
    if (inFence) continue;
    const m = line.match(/^(#{1,6})\s+(.*?)\s*#*\s*$/);
    if (m) {
      items.push({ level: m[1].length, text: stripInlineMarkdown(m[2].trim()) });
    }
  }
  notifyOutline(items);
}

// Scroll the Nth heading (document order) into view; driven by the host outline
// panel clicking an entry. Works in WYSIWYG mode (DOM headings); in source mode
// there is no rendered heading element, so this is a no-op.
export function scrollToHeading(index: number) {
  if (sourceMode) return;
  const headings = document.querySelectorAll('.ProseMirror :is(h1,h2,h3,h4,h5,h6)');
  const el = headings[index] as HTMLElement | undefined;
  el?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

// 「笔记本维护」定位：把第 index 个某类节点滚入视野并闪烁高亮。kind 与宿主扫描器对齐：
//   image=断链图片(img) / code=代码块(.milkdown-code-block 外壳) / math=公式块(.mica-math-block) / table / gallery。
// DOM querySelectorAll 返回文档序 == 宿主按 .md 源码序数出的同类序号，故直接用下标命中（对标 scrollToHeading）。
// 笔记可能刚 editor.load 还在渲染，故用 rAF 轮询几帧等元素出现（main.ts 已保证 reveal 在 load 完成后才执行）。
export function revealNode(kind: string, index: number) {
  if (sourceMode) return;
  const sel =
    kind === 'image' ? '.ProseMirror img'
    // 代码块用 NodeView 外壳 `.milkdown-code-block`，**不要用 `.cm-editor`**——后者是 CodeMirror 自己的
    // DOM、由 milkdown code-block 组件懒挂载（屏幕外的块可能一直不创建），会造成「找不到元素→不滚动→
    // 块永不进视口→CM 永不挂载」的死锁（日志实测 found:0/tries:91）。外壳随文档渲染即在、与可见性无关。
    : kind === 'code' ? '.ProseMirror .milkdown-code-block'
    : kind === 'math' ? '.ProseMirror .mica-math-block'
    : kind === 'table' ? '.ProseMirror table'
    : kind === 'gallery' ? '.ProseMirror .mica-image-group'
    : '';
  if (!sel) return;

  let tries = 0;
  const run = () => {
    const el = document.querySelectorAll(sel)[index] as HTMLElement | undefined;
    if (!el) {
      if (tries++ < 90) requestAnimationFrame(run); // 笔记可能刚 load 还在渲染
      return;
    }
    slowScrollToCenter(el, () => flashReveal(el));
  };
  requestAnimationFrame(run);
}

// 把元素平滑地滚到视口中央，并在停稳后回调（画高亮）。**速度模型＝加速→匀速巡航→临近减速**（越滚越快、
// 长文档不拖沓），每帧重新读元素位置——故笔记里大图经 host 管道异步加载、边滚边把元素往下推也没关系，
// 目标位置每帧重算、平滑跟上，不会像 smooth scrollIntoView 那样被布局变化半路打断停在中途。
// **关键坑**：动画期间必须把滚动容器的 `overflow-anchor` 关掉——否则异步图片在视口内/上方撑开高度时，
// 浏览器的「滚动锚定」会**自动改 scrollTop** 保持视觉位置，这会被下面的「用户手动滚动」守卫误判成用户在滚、
// 直接停掉（这正是「滚一半/停在开头」的真凶）。关掉锚定后 scrollTop 只在我们手动设时变，守卫才可靠。
function slowScrollToCenter(el: HTMLElement, onSettled: () => void) {
  const sc = (document.scrollingElement ?? document.documentElement) as HTMLElement;
  const prevAnchor = sc.style.overflowAnchor;
  sc.style.overflowAnchor = 'none'; // 动画期间禁用滚动锚定（异步图片/布局位移不再自动改 scrollTop）

  // **只认真·用户输入才让位**（滚轮 / 触摸 / 翻页键）。绝不再用「scrollTop 变了」来推断用户滚动——
  // 程序性的 scrollTop 变化太多了：load 后的滚动位置恢复、ProseMirror/CodeMirror 聚焦时把文首光标
  // scrollIntoView、浏览器滚动锚定……任何一个都会让旧版守卫误判成「用户在滚」而中断，定位停在开头。
  let userInterrupted = false;
  const onUser = () => { userInterrupted = true; };
  // 只把「明确的滚动意图」当中断：滚轮 / 触摸 / 翻页&方向&Home/End/空格 等真·滚动键。**别把所有 keydown 都算**
  // ——否则用 Enter/空格 激活「定位」按钮的那次按键、或快捷键，会被误当成「用户在滚」而中断。
  const onKey = (e: KeyboardEvent) => {
    if (['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' ', 'Spacebar'].includes(e.key)) userInterrupted = true;
  };
  window.addEventListener('wheel', onUser, { passive: true });
  window.addEventListener('touchmove', onUser, { passive: true });
  window.addEventListener('keydown', onKey, true);
  const finish = () => {
    sc.style.overflowAnchor = prevAnchor;
    window.removeEventListener('wheel', onUser);
    window.removeEventListener('touchmove', onUser);
    window.removeEventListener('keydown', onKey, true);
    onSettled();
  };

  let vel = 0;            // 当前速度（px/帧）
  const accel = 9;        // 加速度（px/帧²）——从 0 起步快速拉到巡航速（越滚越快）
  const maxSpeed = 150;   // 巡航速上限（≈ 9000px/s）：长文档也能 1~2s 到位
  let frames = 0;
  let alignedFrames = 0;  // 连续「已对齐到中央」的帧数
  const minFrames = 30;   // 至少持续追约 0.5s：跨过首屏异步渲染（CodeMirror 测量/表格挂载/图片撑高）的
                          //   多帧间隙——杜绝「渲染到一半文档临时变矮、元素恰好居中就早早收尾」的误停。
  // **不要用「跑够 N 帧就兜底停」**——那会变成「最多只能滚 N×maxSpeed 像素」的隐形距离上限，长文档没滚到
  // 就被掐停（实测 180 帧×90 ≈ 16000px，超长文档停在半路）。改为「只要还在朝目标推进就不停」：用 bestDist
  // 记录历史最近距离，连续 stallLimit 帧都没再拉近（真卡死/振荡）才兜底，外加一个很宽松的绝对帧上限保命。
  let bestDist = Infinity;
  let stall = 0;
  const stallLimit = 150;   // 连续 ~2.5s 毫无进展才判卡死
  const hardCapFrames = 1800; // ~30s 绝对保命上限

  const step = () => {
    if (userInterrupted) { finish(); return; } // 用户真的滚了 → 让位

    // 每帧重算目标并**夹在 [0, maxScroll] 内**：底部元素够不到正中时夹到底，不会卡在算出的越界值。
    const r = el.getBoundingClientRect();
    const maxScroll = Math.max(0, sc.scrollHeight - sc.clientHeight);
    let target = sc.scrollTop + r.top + r.height / 2 - sc.clientHeight / 2;
    if (target < 0) target = 0; else if (target > maxScroll) target = maxScroll;
    const delta = target - sc.scrollTop;
    const dist = Math.abs(delta);

    // 进展检测：只要还在拉近就重置 stall；长时间毫无进展才算卡死（防真死循环，但不限制滚动距离）。
    if (dist < bestDist - 1) { bestDist = dist; stall = 0; } else stall++;

    if (dist < 1) {
      sc.scrollTop = target;
      alignedFrames++;
    } else {
      alignedFrames = 0;
      // 速度 = min(加速后, 巡航上限, 还能及时刹停的速度)：起步加速、中段巡航、临近平滑减速。
      // 任何程序性位移把元素挪走 → 下一帧 dist 变大、自动重新追上（不中断，这才是真正抗布局抖动的关键）。
      const decelSpeed = Math.sqrt(2 * accel * dist);
      vel = Math.min(vel + accel, maxSpeed, decelSpeed);
      sc.scrollTop += Math.sign(delta) * Math.min(vel, dist);
    }

    frames++;
    if (stall >= stallLimit || frames >= hardCapFrames) { finish(); return; } // 卡死兜底（非距离上限）
    // 收尾：跑够最短时长（跨过渲染间隙）后，连续对齐稳定若干帧才停。
    if (frames >= minFrames && alignedFrames >= 8) { finish(); return; }
    requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
}

// 在元素当前视口位置画一个会渐隐的高亮框（position:fixed → 不受编辑器内任何滚动/overflow 裁剪，
// 对代码块/公式/表格/图/图册一视同仁；表格的 box-shadow 曾被 .mica-table-scroll 的 overflow 裁掉，故弃用元素自身描边）。
function flashReveal(el: HTMLElement) {
  const r = el.getBoundingClientRect();
  if (r.width === 0 && r.height === 0) return;
  const ov = document.createElement('div');
  ov.className = 'mica-reveal-overlay';
  ov.style.left = `${r.left}px`;
  ov.style.top = `${r.top}px`;
  ov.style.width = `${r.width}px`;
  ov.style.height = `${r.height}px`;
  document.body.appendChild(ov);
  setTimeout(() => ov.remove(), 1500);
}

// 点击正文最后一块下方的空白 → 光标到文末；若末块不是空段落，先补一个空段落（对标 Typora，便于在
// 表格/代码块/公式块等结尾后继续写）。只处理「点在最后一块底部以下」，点在内容上交回 ProseMirror 默认。
function onClickBelowContent(e: MouseEvent) {
  if (sourceMode || !proseView) return;
  if (e.button !== 0) return;
  const pmDom = proseView.dom as HTMLElement;
  const last = pmDom.lastElementChild as HTMLElement | null;
  if (!last) return;
  if (e.clientY <= last.getBoundingClientRect().bottom) return; // 点在内容上/中间，正常处理

  e.preventDefault();
  const { state } = proseView;
  const lastNode = state.doc.lastChild;
  let tr = state.tr;
  if (!(lastNode && lastNode.type.name === 'paragraph' && lastNode.content.size === 0)) {
    const para = state.schema.nodes.paragraph?.createAndFill();
    if (para) tr = tr.insert(state.doc.content.size, para);
  }
  tr = tr.setSelection(Selection.atEnd(tr.doc));
  proseView.dispatch(tr.scrollIntoView());
  proseView.focus();
}

function onCompositionStart() {
  composing = true;
  // Show the committed total + 1 for the single character being composed,
  // regardless of how many pinyin letters are typed.
  notifyStats(committedWords, committedChars + 1);
}

function onCompositionEnd() {
  // markdownUpdated / input fires right after with the committed text, which
  // recomputes the real count via computeAndSendStats.
  composing = false;
  // 注：图片改块级独占后，IME 永远在普通段落里输入、不会把文字混进图块，故不再需要组合结束后的「图独占规范化 /
  // 光标重定向」补做（旧行内方案才需要，已随重定向一起删除）。
}

// 段落类快捷键（Ctrl+1~6 标题 / Ctrl+0 正文 / Ctrl+Shift+Q 引用 / Ctrl+Shift+K 代码块）。
// 必须在 web 端绑定：WebView2 持有编辑器焦点时，顶部菜单栏的 XAML 快捷键收不到这些键，
// 所以这里用 ProseMirror keymap 直接调用对应命令（与 Ctrl+B/I 走 Milkdown 原生键位同理）。
// 想加/改段落快捷键就改这里的键位映射；'Mod' 在 Windows 上 = Ctrl。
function runCmd(command: string) {
  return (): boolean => {
    executeCommand(command);
    return true;
  };
}

const paragraphKeymap = $prose(() =>
  keymap({
    'Mod-1': runCmd('heading1'),
    'Mod-2': runCmd('heading2'),
    'Mod-3': runCmd('heading3'),
    'Mod-4': runCmd('heading4'),
    'Mod-5': runCmd('heading5'),
    'Mod-6': runCmd('heading6'),
    'Mod-0': runCmd('paragraph'),
    'Mod-Shift-q': runCmd('blockquote'),
    'Mod-Shift-k': runCmd('codeBlock'),
    'Mod-Shift-m': runCmd('mathBlock'),    // 公式块（对标 Typora 的 Ctrl+Shift+M）
    'Mod-Shift-[': runCmd('orderedList'),  // 有序列表（对标 Typora）
    'Mod-Shift-]': runCmd('bulletList'),   // 无序列表（对标 Typora）
    'Mod-Shift-x': runCmd('taskList'),     // 任务列表（对标 Typora）
    'Mod-Shift-d': (state, dispatch) => insertDetailsBlock(state, dispatch),  // 插入折叠块（details）
    // 插入表格（Ctrl+Shift+T）：打开宿主的 WinUI 插入对话框（行/列/对齐），而非直接插默认表格。
    // 编辑器持有焦点时菜单的 XAML 快捷键收不到键，故经 host.shortcut 转发给宿主弹窗。
    // **光标在表格里则不嵌套**：直接吞掉按键、什么都不做（对标 Typora，无任何提示）。
    'Mod-Shift-t': (state) => {
      if (!isInTable(state)) notify('host.shortcut', { action: 'insertTable' });
      return true;
    },
    // 行内代码（对标 Typora 的 Ctrl+Shift+`）。真实键盘按下时 Shift+反引号的 event.key 是 `~`，
    // prosemirror-keymap 按 key 匹配，故必须同时绑 `~`，只绑反引号在真机不命中。
    'Mod-Shift-`': runCmd('inlineCode'),
    'Mod-Shift-~': runCmd('inlineCode'),
    // 行内公式（自定 Ctrl+Shift+$，正好对应 $…$ 语法）。$ = Shift+4，故 event.key 是 `$`；
    // 同理同时绑基础键 `4`（prosemirror-keymap 的 baseName 回退路径），保证真机命中。
    'Mod-Shift-$': runCmd('mathInline'),
    'Mod-Shift-4': runCmd('mathInline'),
    // Ctrl+A 渐进选择（对标 Typora）：表格内第1次选单元格、第2次整表、第3次整文档；表外/兜底走 selectAll 选整文档。
    'Mod-a': (state, dispatch, view) => (view && tableSelectAll(view)) || selectAll(state, dispatch),
  }),
);

// 标题行首退格 → 直接变正文（对标 Typora）。
// Milkdown 默认在标题行首退格会 join 到上一块/逐级降，不符合 Typora「一下到正文」习惯。
// 用 setNodeMarkup 把整个 heading 节点原地改成 paragraph（保留文字）。
// 注意：这个命令通过 editorViewOptionsCtx 的 handleKeyDown「直接 prop」挂载——
// ProseMirror 的 someProp 会先于所有 state 插件（含 commonmark 的 keymap）查直接 prop，
// 故能稳定抢先处理，不必跟 commonmark 的默认 Backspace 拼插件顺序。
const headingBackspaceToParagraph: Command = (state, dispatch) => {
  const { selection } = state;
  if (!selection.empty) return false;
  const { $from } = selection;
  if ($from.parent.type.name !== 'heading') return false;
  if ($from.parentOffset !== 0) return false; // 仅在标题最前面触发
  const paragraph = state.schema.nodes.paragraph;
  if (!paragraph) return false;
  if (dispatch) dispatch(state.tr.setNodeMarkup($from.before(), paragraph));
  return true;
};

// 插入折叠块（mica_details = 空标题 summary + 一个空正文段），光标落进 summary 直接打标题。
// 用 function 声明保证被上方 paragraphKeymap（懒求值）引用时已提升。
function insertDetailsBlock(state: EditorState, dispatch?: (tr: Transaction) => void): boolean {
  const detailsType = state.schema.nodes.mica_details;
  const summaryType = state.schema.nodes.details_summary;
  const para = state.schema.nodes.paragraph;
  if (!detailsType || !summaryType || !para) return false;
  const node = detailsType.create({ open: true }, [summaryType.create(), para.create()]);
  if (dispatch) {
    let tr = state.tr.replaceSelectionWith(node);
    // node 之后 = tr.selection.from；回退 nodeSize 得 node 起点，+2（进 details、进 summary）= summary 空内容位。
    const start = tr.selection.from - node.nodeSize + 2;
    try { tr = tr.setSelection(TextSelection.create(tr.doc, Math.max(1, Math.min(start, tr.doc.content.size)))); } catch { /* ignore */ }
    dispatch(tr.scrollIntoView());
  }
  return true;
}

// 折叠标题里按 Enter → 跳到正文第一块（不在 summary 内换行、也不会再生成第二个 summary）。
function enterInDetailsSummary(view: ProseEditorView): boolean {
  const { selection } = view.state;
  if (!selection.empty) return false;
  const { $from } = selection;
  if ($from.parent.type.name !== 'details_summary') return false;
  const after = $from.after($from.depth);           // details_summary 之后（details 内、正文前）
  const sel = Selection.near(view.state.doc.resolve(after), 1);
  view.dispatch(view.state.tr.setSelection(sel).scrollIntoView());
  return true;
}

// 退格「跳进」行内公式/行内代码/公式块（对标代码块：删到其边界自动进入而非直接删）。
// 不依赖 ProseMirror 默认 selectNodeBackward（对 atom 行为不稳），显式判断：
//   · 光标紧跟在 math_inline / inline_code 之后 → 选中它（其 nodeView 的 selectNode 打开编辑框）；
//   · 光标在文本块行首、上一个兄弟是 math_block → 选中它。
const enterEmbedOnBackspace: Command = (state, dispatch) => {
  const { selection } = state;
  if (!selection.empty) return false;
  const { $from } = selection;
  const nb = $from.nodeBefore;
  // 行内公式/行内代码：光标紧跟其后 → 退格先选中、再退格才删（对标 Typora、避免误删）。
  // （图片的退格/前删/方向键已全部移到 handleImageKeys 统一处理，这里不再涉及 image。）
  if (nb && (nb.type.name === 'math_inline' || nb.type.name === 'inline_code')) {
    const pos = $from.pos - nb.nodeSize;
    if (dispatch) dispatch(state.tr.setSelection(NodeSelection.create(state.doc, pos)));
    return true;
  }
  if ($from.parentOffset === 0) {
    const before = $from.before();
    const prev = state.doc.resolve(before).nodeBefore;
    if (prev && prev.type.name === 'math_block') {
      const pos = before - prev.nodeSize;
      if (dispatch) dispatch(state.tr.setSelection(NodeSelection.create(state.doc, pos)));
      return true;
    }
  }
  return false;
};

// 方向键进入行内公式/行内代码编辑（atom 节点无法内部逐字走，按方向键进入编辑框）。
// 从「左」按 → 进入落源码开头（紧贴开区分符）；从「右」按 ← 进入落末尾（见 setNextOpenAtStart）。
const INLINE_EMBED_NAMES = new Set(['math_inline', 'inline_code']);
function enterEmbedOnArrow(view: ProseEditorView, dir: 1 | -1): boolean {
  const { selection } = view.state;
  if (!selection.empty) return false;
  const { $from } = selection;
  if (dir > 0) {
    const after = $from.nodeAfter;
    if (after && INLINE_EMBED_NAMES.has(after.type.name)) {
      setNextOpenAtStart(true);
      view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, $from.pos)));
      return true;
    }
  } else {
    const before = $from.nodeBefore;
    if (before && INLINE_EMBED_NAMES.has(before.type.name)) {
      setNextOpenAtStart(false);
      view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, $from.pos - before.nodeSize)));
      return true;
    }
  }
  return false;
}

// 在「空区分符对」中间输入内容字符 → 转成行内代码/公式原子节点并打开编辑框。
// 修「先打两个反引号（或两个 $）、把光标放中间、再往里输入时不渲染」（用户反馈 item 3）。
// **触发极窄**：① 单个、非空白、非区分符本身的字符；② 光标前一字符 == 后一字符 == 同一个区分符（`/$）。
// 注意这**不是**「拦开头区分符」（历史一切 bug 的根因——见 CLAUDE.md）：它要求用户已显式摆好一对空
// 区分符、并把光标置于其间，再输入内容时才转换。故不影响：
//   · 正常从左往右打 `code`/`$x$`（仍走各自的「闭合区分符」输入规则）；
//   · 三反引号 ``` 代码围栏 / `$$ ` 公式块（输入的是区分符本身，直接 return false 交回默认）；
//   · 字面 `$5`（只打了一个 $、光标不在空对中间，不触发）。
function fillEmptyEmbedPair(view: ProseEditorView, from: number, to: number, text: string): boolean {
  if (from !== to) return false;                        // 有选区覆盖输入 → 交回默认
  if (text.length !== 1 || !text.trim()) return false; // 仅单个非空白字符
  if (text === '`' || text === '$') return false;       // 输入区分符本身 → 交回默认（建围栏/字面字符）
  const { state } = view;
  const $from = state.doc.resolve(from);
  if (!$from.parent.inlineContent) return false;
  // 光标前后都得在同一文本块内有字符（排除块边界）
  if ($from.parentOffset < 1 || $from.parentOffset >= $from.parent.content.size) return false;
  const before = state.doc.textBetween(from - 1, from);
  const after = state.doc.textBetween(from, from + 1);
  if (before !== after) return false; // 必须是「一对相同区分符」夹住光标
  const typeName = before === '`' ? 'inline_code' : before === '$' ? 'math_inline' : null;
  if (!typeName) return false;
  const type = state.schema.nodes[typeName];
  if (!type) return false;
  // 用「含刚输入字符的原子节点」替换这对空区分符，并选中它 → nodeView.selectNode 打开编辑框、光标落末尾，
  // 之后的输入直接进入该节点的编辑框。
  const tr = state.tr.replaceWith(from - 1, from + 1, type.create(null, state.schema.text(text)));
  tr.setSelection(NodeSelection.create(tr.doc, from - 1));
  view.dispatch(tr.scrollIntoView());
  return true;
}

// `$$ ` / `$$`+Enter 经官方 mathBlockInputRule（正则 /^\$\$\s$/）把段落转成**空**公式块，但它只改
// blockType、不选中新块，于是 nodeView 停在「点击编辑公式」渲染态、不自动进编辑框（用户反馈 item 5）。
// 这里在该事务之后补一个 NodeSelection 把空公式块选中 → 触发 nodeView.selectNode 自动打开编辑框。
// **触发极窄**：要求事务确实改了文档(docChanged) + 光标「落在空 math_block 内部」或「紧贴其前」——
// 正常只有刚被 input rule 转换的瞬间才会出现这种位置，故不影响其它编辑（纯导航无 docChanged、被排除）。
const isEmptyMathBlock = (n: import('@milkdown/prose/model').Node | null | undefined) =>
  !!n && n.type.name === 'math_block' && !(((n.attrs.value as string) ?? '').trim());

const mathBlockAutoEditPlugin = $prose(() =>
  new Plugin({
    appendTransaction: (trs, _old, newState) => {
      if (!trs.some((tr) => tr.docChanged)) return null;
      const sel = newState.selection;
      if (sel instanceof NodeSelection || !sel.empty) return null;
      const $from = sel.$from;
      let blockPos: number | null = null;
      if (isEmptyMathBlock($from.parent)) blockPos = $from.before();
      else if (isEmptyMathBlock($from.nodeAfter)) blockPos = $from.pos;
      if (blockPos == null) return null;
      return newState.tr.setSelection(NodeSelection.create(newState.doc, blockPos));
    },
  }),
);

// 插入 atom 嵌入节点（公式 / 行内代码，供菜单/快捷键/输入 `$`、反引号调用）。插入后把 NodeSelection
// 落到新节点上 → 其 nodeView 的 selectNode 自动打开编辑框，「插入即可输入」。选区移开/失焦再提交。
function insertAtomNode(typeName: 'math_inline' | 'math_block' | 'inline_code'): Command {
  return (state, dispatch) => {
    const type = state.schema.nodes[typeName];
    if (!type) return false;
    if (dispatch) {
      const from = state.selection.from;
      let tr = state.tr.replaceSelectionWith(type.create());
      // 插入块级节点可能拆分段落，新节点不一定就在光标前；在插入点附近扫描定位它再选中。
      let foundPos = -1;
      tr.doc.nodesBetween(
        Math.max(0, from - 2),
        Math.min(tr.doc.content.size, from + 3),
        (n, pos) => {
          if (foundPos < 0 && n.type === type) foundPos = pos;
        },
      );
      if (foundPos >= 0) tr = tr.setSelection(NodeSelection.create(tr.doc, foundPos));
      dispatch(tr.scrollIntoView());
    }
    return true;
  };
}
const insertMathInline = insertAtomNode('math_inline');
const insertMathBlock = insertAtomNode('math_block');
const insertInlineCode = insertAtomNode('inline_code');

// ===== 行内公式/行内代码「光标靠近才显露源码/反引号」=====（对标 Typora，配合 inlineEmbedView 三态）
// 给「光标紧贴在某行内嵌入节点之前/之后」或「该节点被 NodeSelection 选中」的 math_inline / inline_code
// 打一个 node decoration（spec.embedActive）。nodeView 据此切到显露/编辑态。相邻判断天然满足：光标在
// 首个区分符前一位 / 末个区分符后一位才触发，再远一字符（nodeBefore/After 不是嵌入节点）就不触发。
const isInlineEmbed = (n: import('@milkdown/prose/model').Node | null | undefined) =>
  !!n && (n.type.name === 'math_inline' || n.type.name === 'inline_code');

const inlineEmbedRevealPlugin = $prose(() =>
  new Plugin({
    props: {
      decorations(state) {
        const sel = state.selection;
        const decos: Decoration[] = [];
        const mark = (from: number, to: number) =>
          decos.push(Decoration.node(from, to, { class: 'mica-embed-active' }, { embedActive: true }));
        if (sel instanceof NodeSelection && isInlineEmbed(sel.node)) {
          mark(sel.from, sel.to);
        } else if (sel.empty) {
          const $pos = sel.$from;
          const before = $pos.nodeBefore;
          const after = $pos.nodeAfter;
          if (isInlineEmbed(before)) mark($pos.pos - before!.nodeSize, $pos.pos);
          if (isInlineEmbed(after)) mark($pos.pos, $pos.pos + after!.nodeSize);
        }
        return decos.length ? DecorationSet.create(state.doc, decos) : DecorationSet.empty;
      },
    },
  }),
);

// ===== 图片（行内原子节点，见 imageNode.ts）：严格独占一行 + 贴图竖线 + 绿/蓝点击 + 删除 =====
// 模型（2026-06 最终定稿）：图是行内 atom，图前 = 段落 offset 0（图左侧）、图后 = offset 1（图右侧）。
//   · 删空行：完全交给 PM 原生段落合并（图前/两图间/末尾空行都能自然删掉，零掩盖代码）——这是回行内的根本理由。
//   · 独占一行：靠「图旁打字→另起一行」保证。优先 proactive（typeBesideImageToNewLine：打字前就把字送到上/下一段，
//     英文无跳跃），兜底 reactive（imageOwnLinePlugin 的 appendTransaction + compositionend：拼音组合后把混进图段的
//     文字拆成独占段，组合期 view.composing 跳过以免打断 IME）。
//   · 贴图竖线：imageCaretPlugin 自绘（原生 caret 只有文字高、贴高图底角看不清）。
//   · 绿/蓝点击：imageClickPlugin 按点击横坐标落到图前/图后。
//   · 删除：图前 Del / 图后退格 走 PM 原生「先选中图、再删」两步；二次确认（设置开启）走 handleImageDelete 红框。

// 「图类」行内原子：独立图 image 与图片组 imageGroup 都按「独占一行」处理（组就是「多图当一张大图」对待）。
function isImageLikeNode(n: ProseNode | null | undefined): boolean {
  return !!n && (n.type.name === 'image' || n.type.name === 'imageGroup');
}

// 一个节点（段落）里是否含图类（image / imageGroup）子节点。
function nodeHasImage(node: ProseNode): boolean {
  let has = false;
  node.forEach((c) => { if (isImageLikeNode(c)) has = true; });
  return has;
}

// 把「含图类 + 其它内容」的段落，按图类边界拆成多个块：每个图类节点独占一段，文字各自成段。
function splitParagraphByImages(schema: import('@milkdown/prose/model').Schema, para: ProseNode): ProseNode[] {
  const paragraph = schema.nodes.paragraph;
  const out: ProseNode[] = [];
  let run: ProseNode[] = [];
  const flush = () => { if (run.length) { out.push(paragraph.create(para.attrs, run)); run = []; } };
  para.forEach((child) => {
    if (isImageLikeNode(child)) { flush(); out.push(paragraph.create(para.attrs, child)); }
    else run.push(child);
  });
  flush();
  return out.length ? out : [para];
}

// 兜底规范化：扫出所有「图片没独占一段」的段落（图 + 别的内容混在一段），拆成「每图独占一段」。
// 从后往前替换以免位置漂移；选区用 mapping 贴回最近的合法位置。返回 null 表示无需改动。
function buildImageOwnLineTr(state: EditorState): Transaction | null {
  const violations: { pos: number; node: ProseNode }[] = [];
  state.doc.descendants((node, pos) => {
    if (node.type.name !== 'paragraph') return undefined;
    if (!nodeHasImage(node)) return false;        // 段内无图，无需深入（图只可能在段落里）
    if (node.childCount === 1) return false;       // 已是「独占一图」段
    violations.push({ pos, node });
    return false;
  });
  if (violations.length === 0) return null;
  const tr = state.tr;
  for (let i = violations.length - 1; i >= 0; i--) {
    const { pos, node } = violations[i];
    tr.replaceWith(pos, pos + node.nodeSize, splitParagraphByImages(state.schema, node));
  }
  const mapped = tr.mapping.map(state.selection.from, 1);
  tr.setSelection(Selection.near(tr.doc.resolve(Math.min(mapped, tr.doc.content.size)), -1));
  return tr;
}

const imageOwnLineKey = new PluginKey('mica-image-ownline');
// reactive 兜底：任何把「图 + 别的内容」混进一段的编辑发生后，拆成每图独占。组合期跳过（compositionend 再补）。
const imageOwnLinePlugin = $prose(() =>
  new Plugin({
    key: imageOwnLineKey,
    appendTransaction: (trs, _old, newState) => {
      if (!trs.some((tr) => tr.docChanged)) return null;
      if (proseView?.composing) return null; // IME 组合中不动结构，避免打断输入法（首字母复制等）
      return buildImageOwnLineTr(newState);
    },
  }),
);

// proactive 独占：光标紧贴图片时直接打字 → 文字进上一段末尾 / 下一段开头（无相邻文本段则新建一段）。
// 英文/数字/直接输入走这里，打字前就落到正确位置、无跳跃。组合输入（拼音）不在此触发（走 compositionend 兜底）。
function typeBesideImageToNewLine(view: ProseEditorView, from: number, to: number, text: string): boolean {
  if (from !== to) return false;
  if (view.composing) return false;
  const state = view.state;
  const $from = state.doc.resolve(from);
  if (!$from.parent.inlineContent) return false;
  if ($from.parent.type.name !== 'paragraph') return false;
  const paragraph = state.schema.nodes.paragraph;
  if (!paragraph) return false;
  const textNode = state.schema.text(text);
  const after = $from.nodeAfter;
  const before = $from.nodeBefore;

  // 图前打字（光标在段首、其后第一个是图类）→ 送到「上一段（普通文本段）末尾」，否则在图段上方新建段。
  if (isImageLikeNode(after) && $from.parentOffset === 0) {
    const boundary = $from.before();
    const prev = state.doc.resolve(boundary).nodeBefore;
    if (prev && prev.type.name === 'paragraph' && !nodeHasImage(prev)) {
      const at = boundary - 1; // 上一段内容末尾
      const tr = state.tr.insert(at, textNode);
      tr.setSelection(TextSelection.create(tr.doc, at + text.length));
      view.dispatch(tr.scrollIntoView());
    } else {
      const tr = state.tr.insert(boundary, paragraph.create(null, textNode));
      tr.setSelection(TextSelection.create(tr.doc, boundary + 1 + text.length));
      view.dispatch(tr.scrollIntoView());
    }
    return true;
  }

  // 图后打字（光标在段末、其前一个是图类）→ 送到「下一段（普通文本段）开头」，否则在图段下方新建段。
  if (isImageLikeNode(before) && $from.parentOffset === $from.parent.content.size) {
    const boundary = $from.after();
    const next = state.doc.resolve(boundary).nodeAfter;
    if (next && next.type.name === 'paragraph' && !nodeHasImage(next)) {
      const at = boundary + 1; // 下一段内容开头
      const tr = state.tr.insert(at, textNode);
      tr.setSelection(TextSelection.create(tr.doc, at + text.length));
      view.dispatch(tr.scrollIntoView());
    } else {
      const tr = state.tr.insert(boundary, paragraph.create(null, textNode));
      tr.setSelection(TextSelection.create(tr.doc, boundary + 1 + text.length));
      view.dispatch(tr.scrollIntoView());
    }
    return true;
  }
  return false;
}

// 拼音「直接落到下一行」：组合开始时若光标紧贴图片，把光标移到真实文本位（上一段末尾 / 下一段开头 / 新空段），
// 让组合在那进行。与 typeBesideImageToNewLine（英文）同样的落点策略，区别是这里不带文字、只移光标（组合随后落字）。
function relocateCaretBesideImageForComposition(view: ProseEditorView): void {
  const state = view.state;
  const sel = state.selection;
  if (!sel.empty) return;
  const $from = sel.$from;
  if (!$from.parent.inlineContent) return;
  if ($from.parent.type.name !== 'paragraph') return;
  const paragraph = state.schema.nodes.paragraph;
  if (!paragraph) return;
  const after = $from.nodeAfter;
  const before = $from.nodeBefore;

  // 图前（段首、其后是图类）→ 上一段末尾，否则图上方新空段。
  if (isImageLikeNode(after) && $from.parentOffset === 0) {
    const boundary = $from.before();
    const prev = state.doc.resolve(boundary).nodeBefore;
    if (prev && prev.type.name === 'paragraph' && !nodeHasImage(prev)) {
      view.dispatch(state.tr.setSelection(TextSelection.create(state.doc, boundary - 1)));
    } else {
      const tr = state.tr.insert(boundary, paragraph.create());
      tr.setSelection(TextSelection.create(tr.doc, boundary + 1));
      view.dispatch(tr);
    }
    return;
  }
  // 图后（段末、其前是图类）→ 下一段开头，否则图下方新空段。
  if (isImageLikeNode(before) && $from.parentOffset === $from.parent.content.size) {
    const boundary = $from.after();
    const next = state.doc.resolve(boundary).nodeAfter;
    if (next && next.type.name === 'paragraph' && !nodeHasImage(next)) {
      view.dispatch(state.tr.setSelection(TextSelection.create(state.doc, boundary + 1)));
    } else {
      const tr = state.tr.insert(boundary, paragraph.create());
      tr.setSelection(TextSelection.create(tr.doc, boundary + 1));
      view.dispatch(tr);
    }
    return;
  }
}

// 图前退格（光标在段首、其后是图 = 图前竖线）：图前 ≡ 上一段末尾，故退格删上一段的最后一个字；
// 上一段是独占图 → 选中那张图（再退才删，两步删除）；上一段是空段 / 文首 → 交回 PM 默认（空段+图原生合并，图独占不变）。
function backspaceBeforeImage(view: ProseEditorView): boolean {
  const state = view.state;
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parentOffset !== 0 || !isImageLikeNode($from.nodeAfter)) return false;
  const boundary = $from.before();
  const prev = state.doc.resolve(boundary).nodeBefore;
  if (!prev || prev.type.name !== 'paragraph') return false; // 文首/非段落 → 默认
  if (prev.childCount === 1 && isImageLikeNode(prev.firstChild)) {
    view.dispatch(state.tr.setSelection(NodeSelection.create(state.doc, boundary - prev.nodeSize + 1)).scrollIntoView());
    return true;
  }
  if (prev.content.size > 0 && prev.lastChild?.isText) {
    view.dispatch(state.tr.delete(boundary - 2, boundary - 1).scrollIntoView()); // 删上一段最后一个字符
    return true;
  }
  return false; // 空段 → 默认（原生合并删空段）
}

// 图后前删（光标在段末、其前是图 = 图后竖线）：图后 ≡ 下一段开头，故 Delete 删下一段的第一个字；
// 下一段是独占图 → 选中那张图；下一段是空段 / 文末 → 交回 PM 默认（原生合并删空段）。
function deleteForwardAfterImage(view: ProseEditorView): boolean {
  const state = view.state;
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parentOffset !== $from.parent.content.size || !isImageLikeNode($from.nodeBefore)) return false;
  const boundary = $from.after();
  const next = state.doc.resolve(boundary).nodeAfter;
  if (!next || next.type.name !== 'paragraph') return false;
  if (next.childCount === 1 && isImageLikeNode(next.firstChild)) {
    view.dispatch(state.tr.setSelection(NodeSelection.create(state.doc, boundary + 1)).scrollIntoView());
    return true;
  }
  if (next.content.size > 0 && next.firstChild?.isText) {
    view.dispatch(state.tr.delete(boundary + 1, boundary + 2).scrollIntoView()); // 删下一段第一个字符
    return true;
  }
  return false;
}

// ===== 块级原子（mica_html_block 等）相邻空行删除（用户 #15/#3a）=====
// 块级 atom（如 HTML 块）无法把光标放进/前后，PM 默认在「贴着块的空段落」里按删除键会 selectNodeBackward/
// Forward 选中块、而不是删掉那个空行（用户反馈「块前后空行删不掉」）。这里专门处理「空段落正对着块」的两个方向：
// 删掉空段落、并把块**选中**（NodeSelection）——既消掉空行，又满足 #4「让用户看清选中了哪个块」。
function isBlockAtom(n: ProseNode | null | undefined): boolean {
  // 块级 atom（含 mica_html_block）；图类是行内 atom，不在此列（它们走 image 那套）。
  return !!n && n.isBlock && n.isAtom;
}

// Backspace：光标在空段落首、其前是块 atom → 删空段 + 选中前面那个块。
function backspaceEmptyParaByBlockAtom(view: ProseEditorView): boolean {
  const state = view.state;
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parent.type.name !== 'paragraph' || $from.parent.content.size !== 0) return false; // 须空段
  const boundary = $from.before();
  const prev = state.doc.resolve(boundary).nodeBefore;
  if (!isBlockAtom(prev)) return false;
  const blockPos = boundary - prev!.nodeSize;
  let tr = state.tr.delete(boundary, $from.after());            // 删空段
  try { tr = tr.setSelection(NodeSelection.create(tr.doc, blockPos)); } catch { /* ignore */ }
  view.dispatch(tr.scrollIntoView());
  return true;
}

// Delete：光标在空段落首、其后是块 atom → 删空段 + 选中后面那个块。删空段后块上移到 boundary 处。
function deleteEmptyParaByBlockAtom(view: ProseEditorView): boolean {
  const state = view.state;
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parent.type.name !== 'paragraph' || $from.parent.content.size !== 0) return false; // 须空段
  const boundary = $from.before();
  const next = state.doc.resolve($from.after()).nodeAfter;
  if (!isBlockAtom(next)) return false;
  let tr = state.tr.delete(boundary, $from.after());            // 删空段 → 块上移到 boundary
  try { tr = tr.setSelection(NodeSelection.create(tr.doc, boundary)); } catch { /* ignore */ }
  view.dispatch(tr.scrollIntoView());
  return true;
}

// 选中图片（NodeSelection）按 Enter：在图**下方**新建空段并把光标落进去（默认 PM 是在图前断行、空行落到图上方，
// 不符合「回车=在图后另起一行继续写」的直觉）。
function enterOnSelectedImage(view: ProseEditorView): boolean {
  const state = view.state;
  const sel = state.selection;
  if (!(sel instanceof NodeSelection) || sel.node.type.name !== 'image') return false;
  const paragraph = state.schema.nodes.paragraph;
  if (!paragraph) return false;
  const after = state.doc.resolve(sel.from).after(); // 图所在段落之后
  const tr = state.tr.insert(after, paragraph.create());
  tr.setSelection(TextSelection.create(tr.doc, after + 1));
  view.dispatch(tr.scrollIntoView());
  return true;
}

// 退格到「图后文本行」的行首（光标在某非空文本段段首、上一段是独占图）：不要让 PM 把本段并进图段（会触发
// 独占拆分、光标错跳到行末——bug 2 的真凶）。直接把光标干净地移到图后（图段末尾），再退一次由 handleImageDelete 删图。
// 空段不拦（交回默认：空段+图原生合并 = 删掉空行，光标落图后，本就正常）。
function backspaceAtStartAfterImage(view: ProseEditorView): boolean {
  const state = view.state;
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parentOffset !== 0 || $from.nodeBefore) return false; // 须在段首
  if ($from.parent.content.size === 0) return false;              // 空段交回默认
  if (isImageLikeNode($from.parent.firstChild)) return false;     // 本段是图段（图前）→ 归 backspaceBeforeImage
  const boundary = $from.before();
  const prev = state.doc.resolve(boundary).nodeBefore;
  if (!prev || prev.type.name !== 'paragraph') return false;
  if (!(prev.childCount === 1 && isImageLikeNode(prev.firstChild))) return false; // 上一段须是独占图类
  view.dispatch(state.tr.setSelection(TextSelection.create(state.doc, boundary - 1)).scrollIntoView()); // 落到图后
  return true;
}

// Del 到「图前文本行」的行尾（光标在某非空文本段段末、下一段是独占图）：对称于上——不让本段并进图段，
// 把光标干净地移到图前（图段开头），再 Del 一次由 handleImageDelete 删图（bug 3：原来会错跳到图后）。
function deleteAtEndBeforeImage(view: ProseEditorView): boolean {
  const state = view.state;
  const sel = state.selection;
  if (!sel.empty) return false;
  const $from = sel.$from;
  if ($from.parentOffset !== $from.parent.content.size || $from.nodeAfter) return false; // 须在段末
  if ($from.parent.content.size === 0) return false;               // 空段交回默认
  if (isImageLikeNode($from.parent.lastChild)) return false;       // 本段末是图（图后）→ 归 deleteForwardAfterImage
  const boundary = $from.after();
  const next = state.doc.resolve(boundary).nodeAfter;
  if (!next || next.type.name !== 'paragraph') return false;
  if (!(next.childCount === 1 && isImageLikeNode(next.firstChild))) return false; // 下一段须是独占图类
  view.dispatch(state.tr.setSelection(TextSelection.create(state.doc, boundary + 1)).scrollIntoView()); // 落到图前
  return true;
}

// 贴图竖线（光标可见）：光标紧贴图片（offset 0/1）时给图打 mica-caret-before/after，CSS 在图左/右缘画整高竖线。
// 待删红框（mica-pending-delete）也用 ::after，激活时让位（返回 null）。
const imageCaretPlugin = $prose(() =>
  new Plugin({
    props: {
      decorations(state) {
        const sel = state.selection;
        if (!sel.empty) return null;
        if (imageDeleteKey.getState(state)?.pos != null) return null;
        const $pos = sel.$from;
        const after = $pos.nodeAfter;
        const before = $pos.nodeBefore;
        // 图类（image / imageGroup）前/后画整高竖线：组也是行内 atom、占满整行，竖线落在容器左/右缘
        // → 解决「容器前只有横向 gapCursor」（点 2）：现在是正常文本光标位 + 自绘竖向光标。
        if (isImageLikeNode(after)) {
          return DecorationSet.create(state.doc, [
            Decoration.node($pos.pos, $pos.pos + after!.nodeSize, { class: 'mica-caret-before' }),
          ]);
        }
        if (isImageLikeNode(before)) {
          return DecorationSet.create(state.doc, [
            Decoration.node($pos.pos - before!.nodeSize, $pos.pos, { class: 'mica-caret-after' }),
          ]);
        }
        return null;
      },
    },
  }),
);

// 点击图片本身 → 选中它（蓝框 NodeSelection）。图两侧的「绿/蓝空白区」点击落光标到图前/图后由 PM 原生处理
// （图独占居中段、段落占满整行，点图左空白 → offset 0=图前，点图右空白 → offset 1=图后），无需自己接管。
const imageClickPlugin = $prose(() =>
  new Plugin({
    props: {
      handleClickOn: (view, _pos, node, nodePos, _event, direct) => {
        // 点中独立图 → 选中它（蓝框 NodeSelection）。图片组（imageGroup）的点击由其 NodeView 自己接管（点图选图、
        // 点空白选容器），不在这处理。
        if (!direct || node.type.name !== 'image') return false;
        view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, nodePos)));
        return true;
      },
    },
  }),
);

// 点「满宽图/图片组的右侧空白」→ 光标落到它之后（offset 1，容器右缘，imageCaretPlugin 画竖线），而非跳到下一行文字。
// 为什么需要：独立图独占段是**居中**的（两侧有空白），点右空白 PM 原生就落 offset 1；但图片组满宽（width:100%）、
// 两侧无空白，点其右缘外的编辑器留白时 PM 的命中测试会误判到「下一段开头」（用户反馈：点容器后区域光标跳下一行）。
// 这里据点击坐标找「点击点垂直区间内、整个在点击点左侧」的图类节点，把光标矫正到它之后。满宽单图同理受益。
const clickAfterImagePlugin = $prose(() =>
  new Plugin({
    props: {
      handleClick: (view, _pos, event) => {
        const e = event as MouseEvent;
        let hit: number | null = null;
        view.state.doc.descendants((node, pos) => {
          if (!isImageLikeNode(node)) return true;
          const dom = view.nodeDOM(pos) as HTMLElement | null;
          if (!dom || typeof dom.getBoundingClientRect !== 'function') return false;
          const r = dom.getBoundingClientRect();
          // 点在该图/组的垂直区间内、且 x 落在其右缘之外（右侧空白）→ 命中。
          if (e.clientY >= r.top && e.clientY <= r.bottom && e.clientX > r.right) {
            hit = pos + node.nodeSize; // 落到节点之后（offset 1）
          }
          return false; // 图类是 atom，不再下钻
        });
        if (hit == null) return false;
        view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, hit)).scrollIntoView());
        return true;
      },
    },
  }),
);

// 文档落点占位条：拖图到正文时，imageDrag 经 imageDropKey meta 给出落点 pos，这里渲染一个 widget decoration
// （`.mica-image-drop-spacer`）插进文档流——自然把下方内容往下推、明确示意落点（反馈 3），且能落到任意嵌套深度
// （列表项之间也行，反馈 7）。decoration 不改文档、不进历史，纯视觉。拖拽结束 imageDrag 会 setMeta(null) 清掉。
const imageDropSpacerPlugin = $prose(() =>
  new Plugin<number | null>({
    key: imageDropKey,
    state: {
      init: () => null,
      apply(tr, value) {
        const m = tr.getMeta(imageDropKey) as number | null | undefined;
        if (m !== undefined) return m;                          // 显式设/清
        if (value == null) return value;
        return tr.docChanged ? tr.mapping.map(value) : value;   // 跟随文档变化
      },
    },
    props: {
      decorations(state) {
        const pos = imageDropKey.getState(state);
        if (pos == null) return null;
        const widget = Decoration.widget(pos, () => {
          const el = document.createElement('div');
          el.className = 'mica-image-drop-spacer';
          return el;
        }, { side: -1, key: 'mica-image-drop-spacer' });
        return DecorationSet.create(state.doc, [widget]);
      },
    },
  }),
);

// 注：容器（imageGroup）现在是**行内 atom**（活在段落里），「删容器前/后的空行」完全交给 PM 原生段落合并
// （空段退格 → 并进容器所在段、光标落容器后；与单图同），故旧的 deleteEmptyLineAroundGroup 整块删除逻辑已不需要。

// 「待删图片位置」插件状态（二次确认删除用）。pos = 节点位置（独立图 / 图片组容器）；
// indices 仅图片组用：标某些子图待删（子图不是 PM 节点 → 经 NodeView 标红，见 imageDeletePlugin / imageGroup NodeView）；
// 无 indices = 整容器待删。
interface PendingDelete { pos: number | null; indices?: number[] | null }
const imageDeleteKey = new PluginKey<PendingDelete>('mica-image-delete');

// 二次确认删除：维护「待删图片位置」，CSS 据此画红框+半透明红覆盖（见 handleImageDelete）。
// 文档/选区一变就撤销待删（移开光标即取消）。
const imageDeletePlugin = $prose(() =>
  new Plugin<PendingDelete>({
    key: imageDeleteKey,
    state: {
      init: () => ({ pos: null }),
      apply(tr, value) {
        const meta = tr.getMeta(imageDeleteKey) as PendingDelete | undefined;
        if (meta !== undefined) return meta;            // 显式设/清（第一次标红、第二次删时）
        if (value.pos == null) return value;
        if (tr.docChanged || tr.selectionSet) return { pos: null };  // 移开光标/切图/改文档即取消待删
        return value;
      },
    },
    props: {
      decorations(state) {
        const s = imageDeleteKey.getState(state);
        if (!s || s.pos == null) return null;
        const node = state.doc.nodeAt(s.pos);
        if (!node) return null;
        if (node.type.name === 'image') {                 // 独立图：红框装饰直接挂节点
          return DecorationSet.create(state.doc, [
            Decoration.node(s.pos, s.pos + node.nodeSize, { class: 'mica-pending-delete' }),
          ]);
        }
        if (node.type.name === 'imageGroup') {
          if (!s.indices || s.indices.length === 0) {     // 整个容器待删：class 由 PM 应用到 NodeView dom（见 editor.css）
            return DecorationSet.create(state.doc, [
              Decoration.node(s.pos, s.pos + node.nodeSize, { class: 'mica-pending-delete' }),
            ]);
          }
          // 容器内某些子图待删：子图非 PM 节点，经 spec.micaPendingDeleteIndices 传给 NodeView 标红那几张
          // （attrs 里塞会变的 data-pdel 确保 decoration 集合判不等 → NodeView.update 必触发、刷新红框）。
          return DecorationSet.create(state.doc, [
            Decoration.node(s.pos, s.pos + node.nodeSize, { 'data-pdel': s.indices.join(',') }, { micaPendingDeleteIndices: s.indices }),
          ]);
        }
        return null;
      },
    },
  }),
);

// Ctrl+A（AllSelection）或拖选范围覆盖到图片时，给图加 mica-selected 描边——原子在原生 selection 下不一定
// 高亮（contentEditable=false），故手动标记，让全选/范围选也能看到图被选中。空选区与 NodeSelection
// 已分别由「无」与原生 ProseMirror-selectednode 处理，这里只补「范围选区完整包住图」的情形。
const imageSelectionPlugin = $prose(() =>
  new Plugin({
    props: {
      decorations(state) {
        const sel = state.selection;
        if (sel.empty || sel instanceof NodeSelection) return null;
        const decos: Decoration[] = [];
        state.doc.nodesBetween(sel.from, sel.to, (node, pos) => {
          // 只标独立 image 节点（组内图是容器的 attrs、不是 PM 节点，不会被扫到）。
          if (node.type.name === 'image' && pos >= sel.from && pos + node.nodeSize <= sel.to) {
            decos.push(Decoration.node(pos, pos + node.nodeSize, { class: 'mica-selected' }));
          }
        });
        return decos.length ? DecorationSet.create(state.doc, decos) : null;
      },
    },
  }),
);

// 删「组内当前激活的那张图」：图片组是原子节点、子图存 attrs.images，删单图 = 从数组里抠掉该 index。
// 删完**仍停在同级**：默认激活「顶上来的那张」（= 原位置 index，删的是末张则选新末张），让用户清楚删到哪了——
// 而不是跳到「选中整个容器」（容器是更高一级、组内还有图却选容器不合理，用户反馈 2）。删光最后一张 → 整组由 cleanup 删。
// 仅当有「激活的组内图」时生效；否则（容器整体被选中/光标贴容器）交回 handleImageDelete 删整组。
function sameIndexSet(a: number[] | null | undefined, b: number[]): boolean {
  const sa = a ?? [];
  if (sa.length !== b.length) return false;
  const set = new Set(sa);
  return b.every((i) => set.has(i));
}

function deleteActiveGroupImage(view: ProseEditorView, event: KeyboardEvent): boolean {
  if (event.key !== 'Backspace' && event.key !== 'Delete') return false;
  const sel = getGroupSelection(view); // 锚点 + 全部选中下标（多选时含多张）
  if (!sel) return false;
  // 二次确认（设置开启）：第一次只把选中的子图们标红待删（不动选区，故 selectionSet=false、待删不被自身清掉），
  // 第二次（同一组仍待删）才真删；切图/移光标会令 imageDeleteKey 因 selectionSet 清空 → 自动取消。
  if (imageDeleteConfirm) {
    const pending = imageDeleteKey.getState(view.state);
    if (!(pending && pending.pos === sel.pos && sameIndexSet(pending.indices, sel.indices))) {
      view.dispatch(view.state.tr.setMeta(imageDeleteKey, { pos: sel.pos, indices: sel.indices }));
      return true;
    }
  }
  // 删除（删光由 deleteGroupImages 内连带删容器）；其 tr docChanged → imageDeletePlugin 自动清待删态。
  deleteGroupImages(view, sel.pos, sel.indices, { scroll: true });
  view.focus();
  return true;
}

// 组内激活某图时按 ←/→：选同级的上/下一张（同级才合理，对标方向键在兄弟间移动）。到边界（第一张再←/最后一张再→）
// → 退出容器、落文本光标到容器前/后（offset 0 / offset 1，与单图一致），让用户能继续往外走。
function arrowWithinGroup(view: ProseEditorView, dir: 1 | -1): boolean {
  const a = getActiveGroupImage(view);
  if (!a) return false;
  const grp = view.state.doc.nodeAt(a.pos);
  if (!grp || grp.type.name !== 'imageGroup') return false;
  const len = ((grp.attrs.images as unknown[]) || []).length;
  const ni = a.index + dir;
  if (ni >= 0 && ni < len) {
    const tr = view.state.tr
      .setSelection(NodeSelection.create(view.state.doc, a.pos)) // 仍选中容器
      .setMeta(imageGroupActiveKey, { pos: a.pos, index: ni });  // 激活态移到同级相邻图
    view.dispatch(tr.scrollIntoView());
    return true;
  }
  // 边界 → 退出到容器前/后的文本光标位
  const exitPos = dir < 0 ? a.pos : a.pos + grp.nodeSize;
  view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, exitPos)).scrollIntoView());
  return true;
}

// 三种「删到图」的删除统一处理（解决行内图原生 Backspace 直接删原子、不走两步的问题）：
//   ① 图后按退格（光标在图后、nodeBefore 是图）；② 图前按 Del（光标在图前、nodeAfter 是图）；③ 图被选中（NodeSelection）。
// 关二次确认 → 直接删该图；开二次确认 → 第一次「选中 + 标红框」、第二次才删（移开光标即取消，见 imageDeletePlugin）。
function imageTargetForDelete(state: EditorState, key: string): { pos: number; size: number } | null {
  const sel = state.selection;
  if (sel instanceof NodeSelection && isImageLikeNode(sel.node)) {
    return { pos: sel.from, size: sel.node.nodeSize };
  }
  if (sel.empty) {
    const $f = sel.$from;
    if (key === 'Backspace' && isImageLikeNode($f.nodeBefore)) {
      return { pos: $f.pos - $f.nodeBefore!.nodeSize, size: $f.nodeBefore!.nodeSize };
    }
    if (key === 'Delete' && isImageLikeNode($f.nodeAfter)) {
      return { pos: $f.pos, size: $f.nodeAfter!.nodeSize };
    }
  }
  return null;
}

function handleImageDelete(view: ProseEditorView, event: KeyboardEvent): boolean {
  if (event.key !== 'Backspace' && event.key !== 'Delete') return false;
  const { state } = view;
  const target = imageTargetForDelete(state, event.key);
  if (!target) return false;
  if (!imageDeleteConfirm) {
    view.dispatch(state.tr.delete(target.pos, target.pos + target.size).scrollIntoView());
    return true;
  }
  // 二次确认：第一次选中该图 + 标红待删；第二次（已是同一张待删图）才真正删。
  if (imageDeleteKey.getState(state)?.pos !== target.pos) {
    view.dispatch(
      state.tr
        .setSelection(NodeSelection.create(state.doc, target.pos))
        .setMeta(imageDeleteKey, { pos: target.pos })
        .scrollIntoView(),
    );
    return true;
  }
  view.dispatch(state.tr.delete(target.pos, target.pos + target.size).setMeta(imageDeleteKey, { pos: null }).scrollIntoView());
  return true;
}

// 任务列表（GFM）：gfm 只扩展了 list_item 的 `checked` 属性 + 输入规则，没有现成命令。
// 这里自写：① 已在 list_item 内 → 切换 task ↔ 普通项（checked: false ↔ null）；
// ② 不在列表 → 先包成无序列表，再把新 list_item 的 checked 设为 false（变成任务项）。
const toggleTaskListCommand: Command = (state, dispatch) => {
  const bulletList = state.schema.nodes.bullet_list;
  const listItem = state.schema.nodes.list_item;
  if (!bulletList || !listItem) return false;

  const { $from, $to } = state.selection;
  // 找最近的 list_item 祖先
  let liDepth = -1;
  for (let d = $from.depth; d > 0; d--) {
    if ($from.node(d).type.name === 'list_item') { liDepth = d; break; }
  }

  if (liDepth > 0) {
    if (!dispatch) return true;
    const cur = $from.node(liDepth).attrs.checked;
    const target = cur == null ? false : null; // task ↔ 普通
    const tr = state.tr;
    if (state.selection.empty) {
      // 折叠选区：只切当前所在项
      const liPos = $from.before(liDepth);
      const n = state.doc.nodeAt(liPos);
      if (n) tr.setNodeMarkup(liPos, undefined, { ...n.attrs, checked: target });
    } else {
      // 跨多项选区：切选区内的所有 list_item（attr-only 改动，位置稳定）
      state.doc.nodesBetween($from.pos, $to.pos, (node, pos) => {
        if (node.type === listItem) tr.setNodeMarkup(pos, undefined, { ...node.attrs, checked: target });
      });
    }
    dispatch(tr.scrollIntoView());
    return true;
  }

  // 不在列表里：先包无序列表，再把 list_item 标成任务项
  if (!dispatch) return wrapInList(bulletList)(state);
  return wrapInList(bulletList)(state, (tr) => {
    const sel = tr.selection;
    tr.doc.nodesBetween(sel.from, sel.to, (node, pos) => {
      if (node.type === listItem) tr.setNodeMarkup(pos, undefined, { ...node.attrs, checked: false });
    });
    dispatch(tr.scrollIntoView());
  });
};

// 点击任务项左侧的复选框区域 → 切换 checked。复选框是 CSS 画的（见 editor.css），
// 故这里靠点击坐标判断：只有点在 li 左侧约 1.5em 的勾选框区才切换，点正文不切换。
const taskCheckboxClickPlugin = $prose(() =>
  new Plugin({
    props: {
      handleClickOn: (view, pos, _node, _nodePos, event) => {
        const liEl = (event.target as HTMLElement)?.closest?.('li[data-item-type="task"]') as HTMLElement | null;
        if (!liEl) return false;
        const rect = liEl.getBoundingClientRect();
        const em = parseFloat(getComputedStyle(liEl).fontSize) || 16;
        if (event.clientX > rect.left + 1.5 * em) return false; // 点在内容区，不切换
        const $pos = view.state.doc.resolve(pos);
        for (let d = $pos.depth; d > 0; d--) {
          const n = $pos.node(d);
          if (n.type.name === 'list_item' && n.attrs.checked != null) {
            const liPos = $pos.before(d);
            view.dispatch(view.state.tr.setNodeMarkup(liPos, undefined, { ...n.attrs, checked: !n.attrs.checked }));
            return true;
          }
        }
        return false;
      },
    },
  }),
);

// 标题行提示（对标 Typora）：光标落在某标题行时，在其左侧空白处用灰字标 H1~H6。
// 实现：node decoration 给该标题节点挂 data-mica-hlevel="Hn"，CSS（editor.css）用 ::before 在
// 左 padding 区画出灰字；不在标题内则无装饰。仅一个标题被标（光标所在的那个）。
const headingHintPlugin = $prose(() =>
  new Plugin({
    props: {
      decorations(state) {
        const $head = state.selection.$head;
        for (let d = $head.depth; d > 0; d--) {
          const node = $head.node(d);
          if (node.type.name === 'heading') {
            const pos = $head.before(d);
            const level = (node.attrs.level as number) || 1;
            return DecorationSet.create(state.doc, [
              Decoration.node(pos, pos + node.nodeSize, { 'data-mica-hlevel': `H${level}` }),
            ]);
          }
        }
        return DecorationSet.empty;
      },
    },
  }),
);

// 标题 → 锚点 slug（GitHub 风：小写、去标点、空白转连字符；保留字母/数字/CJK）。
function headingSlug(text: string): string {
  return text.trim().toLowerCase()
    .replace(/[^\p{L}\p{N} \-_]/gu, '')
    .replace(/\s+/g, '-');
}

// 点击页内锚点链接（href 以 # 开头）→ 滚到目标。供 anchorLinkPlugin 用。
// 匹配优先级：① HTML 锚点（渲染后 HTML 块/行内里的 [id] 或 <a name>，id 大小写敏感）；
//            ② 标题 slug（GitHub 风，小写）；③ 标题纯文本去空格兜底。
function scrollToAnchor(rawHref: string): boolean {
  const raw = decodeURIComponent(rawHref.replace(/^#/, ''));
  if (!raw) return false;
  // ① HTML 锚点：id 大小写敏感（先精确、再小写兜底）。CSS.escape 防特殊字符破坏选择器。
  const escId = (s: string) => `.ProseMirror [id="${CSS.escape(s)}"], .ProseMirror [name="${CSS.escape(s)}"]`;
  const htmlEl =
    (document.querySelector(escId(raw)) as HTMLElement | null) ??
    (raw !== raw.toLowerCase() ? (document.querySelector(escId(raw.toLowerCase())) as HTMLElement | null) : null);
  if (htmlEl) {
    htmlEl.scrollIntoView({ behavior: 'smooth', block: 'start' });
    return true;
  }
  // ②③ 标题 slug。
  const target = raw.toLowerCase();
  const headings = Array.from(
    document.querySelectorAll('.ProseMirror :is(h1,h2,h3,h4,h5,h6)'),
  ) as HTMLElement[];
  const el =
    headings.find((h) => headingSlug(h.textContent || '') === target) ??
    headings.find((h) => (h.textContent || '').trim().toLowerCase().replace(/\s+/g, '') === target.replace(/-/g, ''));
  if (!el) return false;
  el.scrollIntoView({ behavior: 'smooth', block: 'start' });
  return true;
}

// 链接点击（对标 Typora / VS Code，用户 #11）：**单击 = 编辑**（不劫持，光标落进链接文本去改）；
// **Ctrl/Cmd+单击 = 跳转/打开**——`#锚点` 在 web 内滚到目标，外链（http/https/mailto）转发宿主用系统默认
// 浏览器打开（`host.openExternal`，宿主侧白名单校验 scheme）。这样既能改链接、又能跟随链接，互不打架。
const linkClickPlugin = $prose(() =>
  new Plugin({
    props: {
      handleClick: (_view, _pos, event) => {
        const a = (event.target as HTMLElement)?.closest?.('a') as HTMLAnchorElement | null;
        const href = a?.getAttribute('href') || '';
        if (!href) return false;
        // 无修饰键：交回默认 → 光标落进链接文本，可直接编辑。
        if (!(event.ctrlKey || event.metaKey)) return false;
        // Ctrl/Cmd+点击：页内锚点滚动，外链交宿主开浏览器。
        if (href.startsWith('#')) return scrollToAnchor(href);
        if (/^(https?:|mailto:):?/i.test(href)) { notify('host.openExternal', { url: href }); return true; }
        return false;
      },
    },
  }),
);

// 粘贴含 HTML 标签 / markdown 链接·图片语法的纯文本 → 走 markdown 解析管道（含 micaHtmlRemark）后插入，
// 即时渲染，不再变成转义原码（对标 Typora）。否则放行给默认/clipboard（普通文本仍按字面插入）。
// markdown 链接：`[文字](url)`（含 `[去章节](#锚点)`）/ 图片 `![](url)`——否则字面粘贴会被序列化加 `\[ \]( \)` 转义、不渲染（用户 #1）。
const htmlPastePlugin = $prose((ctx) =>
  new Plugin({
    props: {
      handlePaste: (view, event) => {
        const text = event.clipboardData?.getData('text/plain') ?? '';
        if (!text) return false;
        const hasTag = /<\/?[a-zA-Z][a-zA-Z0-9-]*(?:\s[^<>]*)?\/?>/.test(text);
        const hasMdLink = /!?\[[^\]\n]*\]\([^)\s]+\)/.test(text);
        if (!hasTag && !hasMdLink) return false; // 没有标签也没有 md 链接 → 不接管
        let parsed: ProseNode | null = null;
        try { parsed = ctx.get(parserCtx)(text) as ProseNode; } catch { return false; }
        if (!parsed || !parsed.content.size) return false;
        // 解析结果若是**单个段落**（如粘贴 `<sup>a</sup>`/`<u>x</u>` 这类纯行内 HTML）→ 取其行内内容
        // 按**行内**插入（Slice openStart/openEnd=1 → 并进当前段落），否则会多出一个块边界＝空行（用户 #3）。
        // 多块内容（多段/含块级）则保持整块 slice。
        const onlyPara = parsed.childCount === 1 && parsed.firstChild?.type.name === 'paragraph';
        const slice = onlyPara
          ? new Slice(Fragment.from(parsed.firstChild!), 1, 1) // 包住段落、两端各开 1 层＝正常行内复制的 slice，并进当前段落
          : parsed.slice(0, parsed.content.size);
        view.dispatch(view.state.tr.replaceSelection(slice).scrollIntoView());
        return true;
      },
    },
  }),
);

// 健壮的行内 HTML 即时渲染（用户 #1）：输入规则只在「键入闭合 `>` 那一刻」触发，但用户**编辑/内部复制**
// 拼出完整标签（如复制两个 `<sup>` 再改后一个）时不会重新触发。这里用 appendTransaction 兜底——每次 doc
// 变更后扫描光标所在文本块，把**完整且非空**的行内 HTML（`<tag>文字</tag>` / `<tag/>`）就地转成 atom，
// 但跳过「光标正落在其内部」的那个（用户还在编辑、等改完或移出再转）。每次只转一个、靠后续轮次转完其余。
const htmlAutoConvertPlugin = $prose(() =>
  new Plugin({
    appendTransaction: (trs, _old, newState) => {
      if (!trs.some((tr) => tr.docChanged)) return null;
      const sel = newState.selection;
      const $from = sel.$from;
      if (!$from.parent.isTextblock || !$from.parent.inlineContent) return null;
      const type = newState.schema.nodes.mica_html_inline;
      if (!type) return null;
      const blockStart = $from.start();
      // leafText 用 1 字符占位符（U+FFFC），保证已有原子节点不破坏「字符↔文档位置」对齐、也不会误配标签。
      const text = newState.doc.textBetween(blockStart, $from.end(), '￼', '￼');
      const re = /<([a-zA-Z][a-zA-Z0-9-]*)(?:\s[^<>]*)?>([^<>]+)<\/\1>|<([a-zA-Z][a-zA-Z0-9-]*)(?:\s[^<>]*)?\/>/g;
      let m: RegExpExecArray | null;
      while ((m = re.exec(text))) {
        const name = (m[1] || m[3] || '').toLowerCase();
        if (name === 'img') continue;            // 图片走自己的管道
        const from = blockStart + m.index;
        const to = from + m[0].length;
        // 成对标签：只在「光标还落在开标签/内容里」时跳过（用户还在编辑内容）；光标落到闭合标签内或之后
        // （如刚给后一个标签补上 `/` 凑成 </tag>）则照常转——这样「补斜杠完成闭合」也即时渲染（用户 #4）。
        // 自闭合（m[3]）无内容，直接转。
        if (m[1]) {
          const closeStart = to - (m[1].length + 3); // </tag> 的起点
          if (sel.from < closeStart) continue;
        }
        return newState.tr.replaceWith(from, to, type.create({ value: m[0] }));
      }
      return null;
    },
  }),
);

async function buildMilkdown(initialMarkdown: string) {
  if (!wysiwygEl) return;
  editorInstance = await Editor.make()
    .config((ctx) => {
      ctx.set(rootCtx, wysiwygEl!);
      ctx.set(defaultValueCtx, initialMarkdown);

      const lc = ctx.get(listenerCtx);

      lc.markdownUpdated((_, markdown) => {
        computeAndSendStats(markdown);
        tableToolbar?.onDocChanged(); // 撤销/重做/增删行列后刷新 reorder 把手位置
        if (!currentRelPath) return;
        const targetPath = currentRelPath;
        if (debounceTimer) clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
          notifyContentChange(targetPath, markdown);
        }, debounceMs);
      });

      // Track selection only while the editor has focus, so a blur
      // (e.g. clicking a native menu) doesn't overwrite it with a collapse.
      lc.focus(() => { editorFocused = true; });
      lc.blur(() => { editorFocused = false; });
      lc.selectionUpdated((_, selection) => {
        if (editorFocused) {
          lastSelection = { from: selection.from, to: selection.to };
        }
        // 光标移动（点进/离开表格、表间切换）时让 reorder 把手跟随当前表（仅 reorder 开启时有效）。
        // 传 selection（= tr.selection 当前真选区）进去——此刻 view.state 还没提交、读它会慢一拍（见 tableToolbar 注释）。
        tableToolbar?.onSelectionChanged(selection);
      });

      // 标题行首退格 → 正文：用 handleKeyDown 直接 prop 挂载（先于 state 插件，见命令注释）。
      ctx.update(editorViewOptionsCtx, (prev) => ({
        ...prev,
        handleKeyDown: (view, event) => {
          // 删「组内当前激活的那张图」——必须先于 handleImageDelete：点中组里某张图时 PM 选区是 NodeSelection(整个容器)，
          // 若先跑 handleImageDelete 会把整组删掉。有激活子图时这里删单图、删光由 cleanup 收尾。
          if (deleteActiveGroupImage(view, event)) return true;
          // 图片/容器删除（二次确认仅设置开启时拦截；关闭直接删）。容器整体被选中（无激活子图）/ 光标贴容器 → 删整组。
          if (handleImageDelete(view, event)) return true;
          // 折叠标题里按 Enter → 跳到正文第一块（先于默认的「分裂段落」，避免在 summary 内换行/生成第二个 summary）。
          if (event.key === 'Enter' && !event.shiftKey && enterInDetailsSummary(view)) return true;
          // 选中图片按 Enter → 在图下方另起空行（而非默认的图上方）
          if (event.key === 'Enter' && enterOnSelectedImage(view)) return true;
          // 表格内任意单元格按 Shift+Enter → 直接跳出表格到下方第一行（Enter 不拦截，单元格内正常加段落）
          if (event.key === 'Enter' && event.shiftKey && exitTableBelow(view)) return true;
          if (event.key === 'Backspace') {
            // 表格边界退格（对标 Typora，都先于图片/默认）：单元格内行首→跳上一格；表格下方行首→进末单元格；表格前空行→删空行
            // 图前退格（删上一段末字/选上一图）/ 退到图后文本行首（移光标到图后、不合并）→ 标题→正文 → 跳进公式；都不命中交回默认
            return (
              backspaceInCellToPrev(view) ||
              backspaceIntoTableEnd(view) ||
              deleteEmptyParagraphBeforeTable(view) ||
              backspaceEmptyParaByBlockAtom(view) ||
              backspaceBeforeImage(view) ||
              backspaceAtStartAfterImage(view) ||
              headingBackspaceToParagraph(view.state, view.dispatch) ||
              enterEmbedOnBackspace(view.state, view.dispatch)
            );
          }
          // 图后前删（删下一段首字/选下一图）/ Del 到图前文本行尾（移光标到图前、不合并）/ 块前空行删除；都不命中交回默认
          if (event.key === 'Delete') return deleteEmptyParaByBlockAtom(view) || deleteForwardAfterImage(view) || deleteAtEndBeforeImage(view);
          // 方向键：先在图片组内同级移动（激活时←/→选相邻图、到边界退出），否则进入行内公式编辑（落正确一端）
          if (event.key === 'ArrowRight') return arrowWithinGroup(view, 1) || enterEmbedOnArrow(view, 1);
          if (event.key === 'ArrowLeft') return arrowWithinGroup(view, -1) || enterEmbedOnArrow(view, -1);
          return false;
        },
        // 粘贴：先看是否有图片文件（落盘+插入），再处理 VS Code 代码缩进，其余交回 clipboard 插件。
        // 直接 view prop 的 handlePaste 先于所有插件执行，故能抢在 milkdown clipboard 之前接管。
        handlePaste: (view, event) =>
          handleImagePaste(view, event as ClipboardEvent) || handleCodePaste(view, event as ClipboardEvent),
        // 复制：组内选中 1 张→单图、≥2 张→新容器（贴合「选一张就是普通图、选多张才成组」），见 transformGroupCopied。
        transformCopied: (slice, view) => transformGroupCopied(slice, view),
        // 粘贴进表格时归一化 slice：把含 table 的剪贴板内容抽成裸 table，让 prosemirror-tables 稳定铺单元格、
        // 不再嵌套整表（见 normalizeTablePasteSlice）。不在表内时原样返回。
        transformPasted: (slice, view) => normalizeTablePasteSlice(view, slice),
        // 在「空区分符对」中间输入内容 → 转成行内代码/公式节点（修「先打两个反引号/$ 再往里填不渲染」）；
        // 再看是否「贴图打字」→ 文字送到上/下一段（proactive 独占，英文无跳跃）。
        // 直接 prop 先于 inputRules 插件执行；输入区分符本身时 return false，闭合输入规则照常生效（见函数注释）。
        handleTextInput: (view, from, to, text) =>
          fillEmptyEmbedPair(view, from, to, text) || typeBesideImageToNewLine(view, from, to, text),
        handleDOMEvents: {
          // 剪切组内图：只剪选中的图（与复制一致），不剪整册。返回 true 时 PM 跳过内置 cut（不会再 deleteSelection 删整册）。
          cut: (view, event) => handleGroupCut(view, event as ClipboardEvent),
          // 拼音「直接出现在下一行」：组合刚开始、还没落字时就把光标移到目标文本位（上一段末尾/下一段开头/新空段），
          // 让整个拼音组合在那进行 → 字母与汉字都显示在正确的行，不再先显示在图旁再跳。
          // 万一某些输入法下重定位会打断组合，下面的 compositionend 兜底仍会把误入图段的字拆出去（退回旧行为）。
          compositionstart: (view) => { relocateCaretBesideImageForComposition(view); return false; },
          // 兜底：组合结束后补一次独占规范化（compositionstart 没拦住时把混进图段的字拆成独占段）。
          compositionend: (view) => {
            // setTimeout(0)：等 PM 把组合文本真正提交进文档后再规范化，避免与提交事务竞争。
            setTimeout(() => {
              if (!view.dom.isConnected) return; // 编辑器可能已销毁/重建
              const tr = buildImageOwnLineTr(view.state);
              if (tr) view.dispatch(tr.scrollIntoView());
            }, 0);
            return false;
          },
        },
        // 行内公式/行内代码的「输入即触发」交给官方输入规则：
        //   · `$x$` 闭合 $ → plugin-math 的 mathInlineInputRule（已含在 math 插件里）转成公式节点；
        //   · `$$ ` 行首 → mathBlockInputRule 转成公式块；
        //   · `` `x` `` 闭合反引号 → commonmark 的 inlineCodeInputRule 转成行内代码标记。
        // 故这里不再拦截开头的 `$`/反引号（旧做法会盖掉官方规则 → 单字符即触发、产生空节点、吞掉 ``` 围栏）。
      }));

      // 代码块（CodeMirror）配置：只覆盖 extensions/languages/图标文案，其余保留默认。
      // 此回调在所有插件注入完 ctx 默认值之后运行，故 codeBlockConfig.key 已存在。
      ctx.update(codeBlockConfig.key, (prev) => ({
        ...prev,
        ...codeBlockIcons,
        extensions: buildCodeMirrorExtensions(showLineNumbers, () => proseView),
        languages,
        copyText: '复制',
        searchPlaceholder: '搜索语言…',
        noResultText: '无匹配语言',
      }));
    })
    .use(commonmarkFiltered)     // commonmark 去掉 inlineCode 标记 + image（改用下面的自定义节点）
    .use(gfmNoTable)             // gfm 去掉表格（改用下面的 HTML 表格节点）
    .use(micaBlankLineRemark)    // 空行保真：须最早注册，读 remark-parse 原始行号补空段；并挂序列化侧 join 定制

    .use(imageNode)              // 图片节点（混合序列化：普通图 ![]()、带样式图 <img>）
    .use($view(imageNode.node, () => createImageNodeView())) // 图片 NodeView：改样式原地更新、不重设 src（消除卡顿）
    .use(micaImageRemark)        // 把带样式 <img> HTML 还原成 image 节点
    .use(imageGroupNode)         // 图片组容器节点（行内 atom，子图存 attrs.images；单行多图等高 inline-flex）
    .use($view(imageGroupNode.node, () => createImageGroupNodeView())) // 图片组 NodeView：自掌内部 DOM、每张子图一个 renderer
    .use(micaImageGroupRemark)   // 把 <div data-mica-gallery> HTML（包进 paragraph）还原成 imageGroup 行内节点
    // HTML 表格：节点 + remark 变换（统一表格 mdast → mica_html_table）+ cell-selection/Tab 导航
    .use(micaTableSchema)
    .use(micaTableRowSchema)
    .use(micaTableCellSchema)
    .use(micaTableHeaderSchema)
    .use(micaTableRemark)
    .use(tableProsePlugins)
    // 通用 HTML（块级 + 行内）：注册在图片/表格/图册 remark 之后，只接管「剩余」的 html 节点。
    .use(htmlBlockNode)
    .use($view(htmlBlockNode.node, () => createHtmlBlockView()))
    .use(htmlInlineNode)
    .use($view(htmlInlineNode.node, () => createHtmlInlineView()))
    // 折叠块（<details>/<summary> 原生可编辑节点）：schema 须在 micaHtmlRemark 之前注册（remark 解析时要用到这俩节点类型）。
    .use(detailsSummaryNode)
    .use(detailsNode)
    .use($view(detailsNode.node, () => createDetailsView()))
    .use(micaHtmlRemark)
    .use(math)                   // 含 mathInlineInputRule（$x$）/ mathBlockInputRule（$$ ）
    .use(inlineCodeNode)         // 行内代码原子节点（替换标记）
    .use($prose(() => inputRules({ rules: [inlineCodeInputRuleAtom, imageInputRule, htmlPairedInputRule, htmlSelfCloseInputRule, htmlBrInputRule, htmlCloseTagInputRule] }))) // `x`→行内代码、![]()→图片、<u>x</u>/<br>→行内 HTML、</→补全最近未闭合标签
    // 行内公式 / 行内代码共用通用可编辑 nodeView（区分符由 nodeView 渲染、三态显露）
    .use($view(mathInlineSchema.node, () => createInlineEmbedView({
      nodeName: 'math_inline', rootClass: 'mica-math-inline', delimiter: '$',
      renderValue: renderMathInline, renderPreview: renderMathPreview,
    })))
    .use($view(inlineCodeNode.node, () => createInlineEmbedView({
      nodeName: 'inline_code', rootClass: 'mica-inline-code', delimiter: '`',
      renderValue: (el, value) => { el.textContent = value; }, // 行内代码渲染态 = 纯文本（CSS 给等宽/底色）
    })))
    .use($view(mathBlockSchema.node, () => createMathBlockView())) // 块级公式 nodeView
    .use(codeBlockComponent)
    .use($prose(() => gapCursor())) // 块（代码/公式）上下空隙可落光标、可在首行块前另起一行
    .use(taskCheckboxClickPlugin)   // 点击任务项复选框切换勾选
    .use(headingHintPlugin)         // 光标在标题行时左侧标 H1~H6（对标 Typora）
    .use(linkClickPlugin)           // 单击=编辑链接、Ctrl/Cmd+单击=跳锚点/系统浏览器开外链
    .use(inlineEmbedRevealPlugin)   // 行内公式/代码：光标相邻显露区分符
    .use(imageOwnLinePlugin)        // 图片：兜底独占（appendTransaction 把混进图段的内容拆成每图独占）
    .use(imageCaretPlugin)          // 图片：贴图竖线（光标在图前/图后时自绘整高竖线）
    .use(imageClickPlugin)          // 图片：绿/蓝点击（按横坐标落光标到图前/图后）
    .use(clickAfterImagePlugin)     // 图片/组：点满宽图右侧空白 → 光标落其后（offset 1），不跳下一行
    .use(imageDeletePlugin)         // 图片：二次确认删除的红框待删状态
    .use(imageSelectionPlugin)      // 图片：全选/范围选时给图描边（原生 selection 不高亮 atom）
    .use(imageGroupActivePlugin)    // 图片组：激活子图状态（node decoration 携 index，供 NodeView 高亮 + 方向键/删除/右键菜单定位）
    .use(imageDropSpacerPlugin)     // 图片：拖图到正文时的落点占位条（widget decoration，把下方内容推开示意落点）
    .use(mathBlockAutoEditPlugin)   // `$$ ` 建出的空公式块自动打开编辑框
    .use(history)
    .use(listener)
    .use(htmlPastePlugin)        // 粘贴含 HTML 标签的纯文本 → 解析渲染（须在 clipboard 前，先接管）
    .use(htmlAutoConvertPlugin)  // 编辑/复制拼出的完整行内 HTML → appendTransaction 兜底转 atom（用户 #1）
    .use(clipboard)
    .use(trailing)
    .use(paragraphKeymap)
    .create();

  // 缓存 ProseMirror view，供代码块 CM 扩展跨层使用（首行块退出新建行，见 codeSetup）
  try { proseView = editorInstance.ctx.get(editorViewCtx); } catch { proseView = null; }
  // 开发期调试：把 view 挂到 window，方便在普通浏览器里读取/驱动选区（生产无害，仅多一个全局引用）。
  // import.meta.env 在本项目 tsconfig 未引入 vite/client 类型，故手动收窄类型避免 tsc 报错。
  if ((import.meta as unknown as { env?: { DEV?: boolean } }).env?.DEV) {
    (window as unknown as { __pmView: unknown }).__pmView = proseView;
  }

  // 载入兜底：appendTransaction 插件不在「初始文档」上跑，故已有表格（从 .md 载入、含被表头 rowspan 盖住
  // 的二级表头行）需在此显式提升一次表头，三线表才会立刻正确（合并/插入等后续改动由插件维护）。
  if (proseView) {
    const fix = enforceHeaderCoverage(proseView.state);
    if (fix) proseView.dispatch(fix);
  }

  return editorInstance;
}

export function setActiveNote(relPath: string) {
  currentRelPath = relPath;
}

// Debounced save while editing raw markdown in 源码模式（CodeMirror 文档变化回调）。
function handleSourceChange(md: string) {
  computeAndSendStats(md);
  if (!currentRelPath) return;
  const targetPath = currentRelPath;
  if (debounceTimer) clearTimeout(debounceTimer);
  debounceTimer = setTimeout(() => notifyContentChange(targetPath, md), debounceMs);
}

// Apply editor appearance + auto-save settings pushed from the host (editor.config).
// Font/size/width are CSS variables read by editor.css; the debounce is plain state.
export interface EditorConfig {
  fontFamily?: string;
  fontSize?: number;   // px
  lineWidth?: number;  // px (writing-canvas max-width)
  autoSaveMs?: number;
  showLineNumbers?: boolean; // 代码块行号
  firstLineIndent?: boolean; // 段落首行缩进 2 字符
  imageDeleteConfirm?: boolean; // 删除图片是否二次确认（红框提醒）
}

export function setEditorConfig(cfg: EditorConfig) {
  const root = document.documentElement;
  if (cfg.fontFamily) root.style.setProperty('--mica-font-body', cfg.fontFamily);
  if (typeof cfg.fontSize === 'number') root.style.setProperty('--mica-font-size', `${cfg.fontSize}px`);
  if (typeof cfg.lineWidth === 'number') root.style.setProperty('--mica-editor-width', `${cfg.lineWidth}px`);
  if (typeof cfg.autoSaveMs === 'number' && cfg.autoSaveMs >= 0) debounceMs = cfg.autoSaveMs;
  // 首行缩进：纯 CSS（body 上挂类，editor.css 给段落 text-indent），无需重建编辑器。
  if (typeof cfg.firstLineIndent === 'boolean') document.body.classList.toggle('first-line-indent', cfg.firstLineIndent);
  // 图片删除二次确认：纯运行时标志，被 handleImageDelete 读取，无需重建编辑器。
  if (typeof cfg.imageDeleteConfirm === 'boolean') imageDeleteConfirm = cfg.imageDeleteConfirm;
  // 行号开关变了：CodeMirror 实例不随配置热重建，故重建编辑器（用当前文档）让代码块按新设置重渲染。
  if (typeof cfg.showLineNumbers === 'boolean' && cfg.showLineNumbers !== showLineNumbers) {
    showLineNumbers = cfg.showLineNumbers;
    if (editorInstance && !sourceMode) {
      const md = editorInstance.action(getMarkdown());
      void rebuildWysiwyg(md);
    }
  }
}

// 缩放（CSS zoom）变化后重新摆放 reorder 把手：把手在 zoom 外的浮层上，锚点随 zoom 变须重算。由 main.ts applyZoom 调。
export function repositionTableToolbar() {
  tableToolbar?.reposition();
}

export function isSourceMode() {
  return sourceMode;
}

// Switch between WYSIWYG and raw-markdown source editing. Driven by the host
// (which owns the menu checkbox), so this just makes the view match `enabled`.
// 整篇文档的滚动容器（与 main.ts 一致）：源码 CodeMirror 与 WYSIWYG 一样不自带内部滚动、
// 统一由页面(body)滚动，故两模式共用这一个滚动容器。
const docScroller = () => document.scrollingElement ?? document.documentElement;

// ===== 两模式光标 / 滚动同步（对标 Typora）=====
// 思路：用一个「哨兵字符」做 PM 文档位置 ↔ markdown 偏移的精确映射（借真实序列化/解析器，
// 不自造 source-map）。再记下切换前光标在视口中的 Y，切换后把光标滚回同一 Y——这样图片/表格
// 高度差也不影响（只关心光标本身的位置）。映射失败时退化为「按偏移比例滚动」。
const CURSOR_SENTINEL = ''; // 私有区单字符：几乎不会出现在正文、也不易破坏 markdown 标记

// WYSIWYG → 序列化为 markdown，并算出当前光标对应的 markdown 偏移 + 光标视口 Y。
function serializeWithCursor(): { md: string; offset: number; anchorY: number | null; ok: boolean } {
  const result = { md: '', offset: 0, anchorY: null as number | null, ok: false };
  if (!editorInstance) return result;
  editorInstance.action((ctx) => {
    const view = ctx.get(editorViewCtx);
    const serializer = ctx.get(serializerCtx);
    result.md = serializer(view.state.doc); // 干净 md（不含哨兵）
    try {
      const from = view.state.selection.head;
      const coords = view.coordsAtPos(from);
      // 在光标处插入哨兵（仅构 tr、不 dispatch），序列化后哨兵的下标即光标在干净 md 中的偏移
      // （哨兵之前的内容与干净 md 完全一致）。
      const withMark = serializer(view.state.tr.insertText(CURSOR_SENTINEL, from).doc);
      const idx = withMark.indexOf(CURSOR_SENTINEL);
      if (idx >= 0) { result.offset = idx; result.anchorY = coords ? coords.top : null; result.ok = true; }
    } catch { /* 光标在原子节点等不可插文本处：退化为无光标映射 */ }
  });
  return result;
}

// 把 markdown 偏移挪到「本行前导标记之后」，避免哨兵插在行首标记里（如 `## `/`> `/`- `）破坏块结构。
function safeSourceOffset(md: string, offset: number): number {
  let o = Math.max(0, Math.min(offset, md.length));
  const lineStart = md.lastIndexOf('\n', o - 1) + 1;
  const line = md.slice(lineStart);
  const m = line.match(/^\s*(?:#{1,6}\s+|>\s?|[-*+]\s+(?:\[[ xX]\]\s+)?|\d+\.\s+)?/);
  const minPos = lineStart + (m ? m[0].length : 0);
  o = Math.max(o, minPos);
  // 别让哨兵落进某个 `<...>` 标签内部（会被解析进原子节点的 value 串、定位失准）：
  // 若 o 之前有未闭合的 `<`（其 `>` 在 o 之后），把 o 推到该 `>` 之后。
  const lt = md.lastIndexOf('<', o - 1);
  if (lt >= 0) {
    const gt = md.indexOf('>', lt);
    if (gt >= o) o = gt + 1;
  }
  return Math.min(o, md.length);
}

// 在 PM 文档里扫描所有哨兵（文本节点 + 原子节点的 value 属性），返回首个文本哨兵位置（供定位光标）。
// **承重坑**：哨兵按 markdown 偏移插入时可能落进原子节点（图片/表格/HTML 块行内）的序列化串里，
// 解析后变成 `node.attrs.value` 的一部分——光删文本节点的会漏掉它、致其被永久焊进正文渲染成 `□`。
// 故这里**连原子 value 一起剥**，保证零残留（已损坏的笔记下次切源码即自愈）。
interface SentinelScan {
  textHits: number[];                                  // 文本节点里哨兵的文档位置（升序）
  attrHits: { pos: number; node: ProseNode }[];        // value 属性含哨兵的原子节点
}
function scanSentinels(doc: ProseNode): SentinelScan {
  const textHits: number[] = [];
  const attrHits: { pos: number; node: ProseNode }[] = [];
  doc.descendants((node, pos) => {
    if (node.isText && node.text && node.text.includes(CURSOR_SENTINEL)) {
      let i = node.text.indexOf(CURSOR_SENTINEL);
      while (i >= 0) { textHits.push(pos + i); i = node.text.indexOf(CURSOR_SENTINEL, i + 1); }
    } else if (typeof node.attrs?.value === 'string' && (node.attrs.value as string).includes(CURSOR_SENTINEL)) {
      attrHits.push({ pos, node });
    }
    return true;
  });
  return { textHits, attrHits };
}

// 某文档位置若落在「原子节点」内部（如行内代码 inline_code 把文本存成 content、行内公式等），返回该原子**之后**的位置；
// 否则返回 null。用于源码↔渲染光标定位：原子内部放不进真光标，哨兵落里面时光标须落到原子之后（用户 #2 行内代码）。
function enclosingAtomAfter(doc: ProseNode, pos: number): number | null {
  const $p = doc.resolve(Math.min(Math.max(pos, 0), doc.content.size));
  for (let d = $p.depth; d > 0; d--) {
    if ($p.node(d).isAtom) return $p.after(d);
  }
  return null;
}

// 显示源码模式：首次进入时创建 CodeMirror，之后复用同实例（切文件时 setSourceDoc 换内容）。
function showSourceView(doc: string) {
  if (!sourceEl) return;
  sourceEl.style.display = 'block';
  if (!sourceView) {
    sourceView = createSourceView(sourceEl, doc, {
      onChange: handleSourceChange,
      onCompositionStart,
      onCompositionEnd,
    });
  } else {
    setSourceDoc(sourceView, doc);
  }
  // 显示后量一次几何（防止此前 display:none 期间测量为 0）。
  sourceView.requestMeasure();
}

function hideSourceView() {
  if (sourceEl) sourceEl.style.display = 'none';
}

export function setSourceMode(enabled: boolean) {
  if (enabled === sourceMode || !rootEl || !wysiwygEl || !sourceEl) return;

  if (enabled) {
    // WYSIWYG -> source：序列化 + 取光标偏移/视口 Y（须在隐藏 wysiwyg 前量坐标）。
    const cur = serializeWithCursor();
    const md = cur.md;
    // 比例兜底（映射失败时用）。
    const sc = docScroller();
    const ratio = sc.scrollHeight > sc.clientHeight ? sc.scrollTop / (sc.scrollHeight - sc.clientHeight) : 0;
    wysiwygEl.style.display = 'none';
    showSourceView(md);
    sourceMode = true;
    const off = cur.ok ? Math.min(cur.offset, sourceView!.state.doc.length) : 0;
    if (cur.ok && sourceView) sourceView.dispatch({ selection: { anchor: off } });
    sourceView?.focus();
    requestAnimationFrame(() => {
      // 关键：布局生效后再 requestMeasure——CM 在 display:none→显示瞬间测量会得到 0 视口、
      // 只渲染光标附近几行（用户反馈「切过来一片空白、点一下才出来」）。这里强制重量一次。
      sourceView?.requestMeasure();
      if (cur.ok && cur.anchorY != null && sourceView) {
        try {
          const c = sourceView.coordsAtPos(off);
          if (c) { docScroller().scrollTop += c.top - cur.anchorY; return; }
        } catch { /* 落到比例兜底 */ }
      }
      const s = docScroller();
      s.scrollTop = ratio * Math.max(0, s.scrollHeight - s.clientHeight);
    });
    // 再补一帧 measure：滚动调整后视口变了，确保新进入视口的行都渲染出来。
    requestAnimationFrame(() => requestAnimationFrame(() => sourceView?.requestMeasure()));
  } else {
    // source -> WYSIWYG：取源码光标偏移 + 视口 Y，重建后把光标滚回同一 Y。
    const md = sourceView ? getSourceDoc(sourceView) : '';
    let offset: number | null = null;
    let anchorY: number | null = null;
    if (sourceView) {
      const head = sourceView.state.selection.main.head;
      try { const c = sourceView.coordsAtPos(head); if (c) { offset = head; anchorY = c.top; } } catch { /* ignore */ }
    }
    sourceMode = false;
    hideSourceView();
    wysiwygEl.style.display = '';
    void rebuildWysiwygSynced(md, offset, anchorY);
  }
}

// 源码→渲染：用哨兵把源码光标精确还原到渲染文档，并把光标滚回切换前的视口 Y。
async function rebuildWysiwygSynced(markdown: string, offset: number | null, anchorY: number | null) {
  if (!wysiwygEl) return;
  if (editorInstance) {
    try { await editorInstance.destroy(); } catch { /* ignore */ }
    editorInstance = null;
  }
  wysiwygEl.innerHTML = '';
  // 在安全偏移处插哨兵后再渲染；渲染完找到哨兵 → 删除 + 落光标。
  const useSentinel = offset != null;
  let buildMd = markdown;
  if (useSentinel) {
    const o = safeSourceOffset(markdown, offset!);
    buildMd = markdown.slice(0, o) + CURSOR_SENTINEL + markdown.slice(o);
  }
  await buildMilkdown(buildMd);
  computeAndSendStats(markdown);                                    // 统计/大纲用干净 md
  if (currentRelPath) notifyContentChange(currentRelPath, markdown); // 存干净 md

  let placed = false;
  // buildMilkdown 已重建并赋值 editorInstance，但上面 `editorInstance = null` 的窄化在 await 后
  // 未被 TS 控制流还原（buildMilkdown 是顶层函数、非嵌套），故用断言读回真实值。
  const inst = editorInstance as Editor | null;
  // 始终扫描并剥除所有哨兵（含历史损坏遗留在原子 value 里的），即使本次没插哨兵也自愈。
  if (inst) {
    inst.action((ctx) => {
      const view = ctx.get(editorViewCtx);
      const origDoc = view.state.doc;
      const { textHits, attrHits } = scanSentinels(origDoc);
      // 先在**原始**文档里定下「光标锚点」（再随清理事务 map 到最终位置，避免删除位移算错）：
      //   ① 有文本哨兵：若它落在某原子内部（行内代码把文本存成 content → 哨兵进了原子里，光标放进去会
      //      不显示，用户 #2）→ 锚到该原子**之后**；否则锚到哨兵原位（普通文本，精确）。
      //   ② 无文本哨兵、但哨兵被吞进某原子的 value（<sub>2</sub> 这类）→ 锚到该原子之后。
      //   ③ 实在没有任何哨兵 → 才按偏移比例估算（绝不让光标消失）。
      let anchorOrig: number | null = null;
      if (textHits.length) {
        anchorOrig = enclosingAtomAfter(origDoc, textHits[0]) ?? textHits[0];
      } else if (attrHits.length) {
        anchorOrig = attrHits[0].pos + attrHits[0].node.nodeSize;
      }
      let tr = view.state.tr;
      // ① 清原子 value 里的哨兵（setNodeMarkup 不改 size）。
      for (const h of attrHits) {
        const cleaned = (h.node.attrs.value as string).split(CURSOR_SENTINEL).join('');
        tr.setNodeMarkup(h.pos, undefined, { ...h.node.attrs, value: cleaned });
      }
      // ② 删文本里的哨兵（含行内代码 content 里的；从后往前删避免位移）。
      for (const p of [...textHits].reverse()) tr = tr.delete(p, p + CURSOR_SENTINEL.length);
      const docSize = tr.doc.content.size;
      // 把锚点经事务 mapping 映射到最终位置（删除/清理后的位移由 mapping 自动修正）。
      let caret: number;
      if (anchorOrig != null) {
        caret = tr.mapping.map(anchorOrig, -1);
      } else {
        const ratio = markdown.length > 0 && offset != null ? Math.min(1, Math.max(0, offset / markdown.length)) : 0;
        caret = Math.round(ratio * docSize);
      }
      try { tr.setSelection(Selection.near(tr.doc.resolve(Math.min(Math.max(caret, 0), docSize)), 1)); } catch { /* ignore */ }
      // **关键**：哨兵清理事务**不进撤销历史**（addToHistory:false）——否则它是新编辑器历史里的第一条可撤销事务，
      // Ctrl+Z 会把它撤销→哨兵 □ 复活 + 光标跳回文档开头（用户报）。标记后哨兵永不可被 undo 带回。
      tr.setMeta('addToHistory', false);
      view.dispatch(tr);
      view.focus();                       // 始终聚焦，保证 caret 可见（修「切换后光标消失」）
      placed = true;
      // 等布局稳定后把光标滚回切换前的视口 Y（双 rAF；图片异步加载导致的偏差用户已接受）。
      requestAnimationFrame(() => requestAnimationFrame(() => {
        try {
          const c = view.coordsAtPos(view.state.selection.head);
          if (c && anchorY != null) docScroller().scrollTop += c.top - anchorY;
        } catch { /* ignore */ }
      }));
    });
  }
}

async function rebuildWysiwyg(markdown: string, restoreScrollTop?: number) {
  if (!wysiwygEl) return;
  // 重建前若没传还原值，就保住当前滚动位置（如行号开关热重建时别跳顶）。
  const targetScroll = restoreScrollTop ?? docScroller().scrollTop;
  if (editorInstance) {
    try { await editorInstance.destroy(); } catch { /* ignore */ }
    editorInstance = null;
  }
  wysiwygEl.innerHTML = '';
  await buildMilkdown(markdown);
  computeAndSendStats(markdown);
  // The text may have changed while in source mode; persist it.
  if (currentRelPath) notifyContentChange(currentRelPath, markdown);
  // 还原滚动位置（双 rAF 等布局稳定）。
  requestAnimationFrame(() => requestAnimationFrame(() => {
    docScroller().scrollTop = targetScroll;
  }));
}

// Restore the user's last selection and focus before running editor commands,
// because clicking a WinUI menu blurs the WebView2 and clears the selection.
function restoreSelectionAndFocus() {
  if (!editorInstance) return;
  editorInstance.action((ctx) => {
    const view = ctx.get(editorViewCtx);
    if (lastSelection) {
      const { from, to } = lastSelection;
      const size = view.state.doc.content.size;
      // 仅当两端都落在「可放置文本的 inline 容器」内才用 TextSelection，否则会触发
      // "TextSelection endpoint not pointing into a node with inline content" 警告。
      if (from <= size && to <= size) {
        const $from = view.state.doc.resolve(from);
        const $to = view.state.doc.resolve(to);
        if ($from.parent.inlineContent && $to.parent.inlineContent) {
          view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, from, to)));
        }
      }
    }
    view.focus();
  });
}

// Commands that operate on the current selection/cursor; these need the
// user's selection restored after a menu click blurs the editor.
const SELECTION_DEPENDENT = new Set([
  'bold', 'italic', 'inlineCode', 'strikethrough',
  'heading1', 'heading2', 'heading3', 'heading4', 'heading5', 'heading6',
  'paragraph', 'blockquote', 'codeBlock', 'bulletList', 'orderedList', 'hr', 'table',
  'mathInline', 'mathBlock', 'taskList', 'selectAll',
]);

// 插入 HTML 表格（由宿主 WinUI 启动器驱动，经 editor.insertTable IPC）。宿主直接生成 `<table>` HTML 串
// （含行列/表头/合并/对齐/预设/圆角/线宽），这里用 parseHtmlTable 还原成 PM 节点插到光标处。插入后样式/合并
// 都在节点属性里，序列化时落回 `<table>` 的 style/data-*（见 htmlTable.ts）。
export function insertTable(html: string) {
  if (!editorInstance || sourceMode || !html) return;
  restoreSelectionAndFocus();
  editorInstance.action((ctx) => {
    const view = ctx.get(editorViewCtx);
    const node = parseHtmlTable(view.state.schema, html);
    if (!node) return;
    const from = view.state.selection.from;       // 表格插入起点（替换选区后表格从这里开始）
    let tr = view.state.tr.replaceSelectionWith(node);
    // 光标落到第一个单元格：from+1 已进入表格节点边界，near(+1) 向前找到首格里的文本位（默认会落到末格，故显式置首）。
    const $cell = tr.doc.resolve(Math.min(from + 1, tr.doc.content.size));
    tr = tr.setSelection(Selection.near($cell, 1)).scrollIntoView();
    view.dispatch(tr);
    view.focus();
  });
  focusEditor();
}

export function executeCommand(command: string) {
  if (!editorInstance) return;
  // Formatting commands target the WYSIWYG editor; ignore them in source mode.
  if (sourceMode) return;

  // Copy/cut must read the *current* selection. ProseMirror keeps the selection in
  // its state even after the menu click blurs the editor, so we read it directly.
  // We deliberately do NOT restoreSelectionAndFocus() here: restoring the tracked
  // lastSelection could be stale/larger than what the user actually selected (it
  // grabbed extra lines), and refocusing first can make ProseMirror resync from a
  // collapsed DOM selection. Just read state.selection.
  if (command === 'copy' || command === 'cut') {
    handleClipboard(command);
    return;
  }

  if (SELECTION_DEPENDENT.has(command)) {
    // 仅当编辑器已失焦（菜单点击会 blur WebView 并清掉选区）才恢复上次选区；
    // 键盘快捷键触发时编辑器仍有焦点、选区是实时正确的，绝不能用（可能过期的）lastSelection 覆盖它
    // ——否则在代码块附近会把光标错误地拉回上一个代码块/导致命令落在错误位置（用户反馈 #7）。
    if (!editorFocused) restoreSelectionAndFocus();
    else focusEditor();
  } else {
    focusEditor();
  }

  // 行内代码（原子节点）：无选区 → 插入空代码节点并打开编辑框（对标行内公式）；
  // 有选区 → 把选中文本包成一个行内代码节点（渲染态，光标落其后）。
  if (command === 'inlineCode') {
    editorInstance.action((ctx) => {
      const view = ctx.get(editorViewCtx);
      const { state } = view;
      const type = state.schema.nodes.inline_code;
      if (!type) return;
      const sel = state.selection;
      if (sel.empty) {
        insertInlineCode(state, view.dispatch);
      } else {
        const text = state.doc.textBetween(sel.from, sel.to, '', '');
        const node = type.create(null, text ? state.schema.text(text) : null);
        let tr = state.tr.replaceSelectionWith(node);
        const after = Math.min(sel.from + node.nodeSize, tr.doc.content.size);
        tr = tr.setSelection(Selection.near(tr.doc.resolve(after), 1));
        view.dispatch(tr.scrollIntoView());
      }
      view.focus();
    });
    return;
  }

  // Milkdown command via .run() (set after editor.create())
  const entry = commandRegistry[command];
  if (entry?.cmd?.run) {
    try {
      entry.cmd.run(entry.payload);
    } catch (e) {
      console.warn(`Milkdown command '${command}' failed:`, e);
    }
    focusEditor();
    return;
  }

  // ProseMirror-level commands for clipboard/selection
  try {
    editorInstance.action((ctx) => {
      const view = ctx.get(editorViewCtx);
      const { state, dispatch } = view;

      switch (command) {
        case 'selectAll':
          selectAll(state, dispatch);
          break;
        case 'mathInline':
          insertMathInline(state, dispatch);
          break;
        case 'mathBlock':
          insertMathBlock(state, dispatch);
          break;
        case 'taskList':
          toggleTaskListCommand(state, dispatch);
          break;
        case 'insertDetails':
          insertDetailsBlock(state, dispatch);
          break;
        case 'paste':
          navigator.clipboard.readText().then(text => {
            if (!text || !editorInstance) return;
            editorInstance.action((ctx2) => {
              const v = ctx2.get(editorViewCtx);
              v.dispatch(v.state.tr.insertText(text).scrollIntoView());
              v.focus();
            });
          }).catch(() => {});
          break;
        default:
          console.warn(`Unknown command: ${command}`);
      }

      view.focus();
    });
  } catch (e) {
    console.warn(`Command '${command}' failed:`, e);
  }
}

// Copy/cut the live ProseMirror selection (preserved across the menu-click blur).
function handleClipboard(command: 'copy' | 'cut') {
  if (!editorInstance) return;
  try {
    editorInstance.action((ctx) => {
      const view = ctx.get(editorViewCtx);
      const { state } = view;
      const sel = state.selection;
      if (sel.empty) return;
      const text = state.doc.textBetween(sel.from, sel.to, '\n', '\n');
      navigator.clipboard.writeText(text).catch(() => {});
      if (command === 'cut') {
        view.dispatch(state.tr.deleteSelection().scrollIntoView());
        view.focus();
      }
    });
  } catch (e) {
    console.warn(`Clipboard '${command}' failed:`, e);
  }
}

export function saveNow() {
  if (!currentRelPath) return;

  if (debounceTimer) {
    clearTimeout(debounceTimer);
    debounceTimer = null;
  }

  try {
    const markdown = sourceMode && sourceView
      ? getSourceDoc(sourceView)
      : editorInstance?.action(getMarkdown());
    if (markdown != null) notifyContentChange(currentRelPath, markdown);
  } catch (e) {
    console.warn('Save failed:', e);
  }
}

function focusEditor() {
  const el = document.querySelector('.ProseMirror') as HTMLElement | null;
  el?.focus();
}
