// 图片组容器节点（第四期）。设计见 docs/图片功能计划.md「第四期」与 CLAUDE.md。
//
// **架构（2026-06 第三轮定稿：原子 + 行内）**：imageGroup 是**行内原子节点（inline atom）**，子图存在节点的 `images`
//   属性里（`ImageAttrs[]`），**不是 ProseMirror 子节点**。NodeView 自己掌管内部 DOM（每张图一个
//   `createImageRenderer()`，与独立图共用同一套裁剪/翻转/圆角/显示管道）。
// 为什么 atom + inline（两次 pivot 后的最终形态）：
//   · **atom**（无内部内容）：文本光标永不落进组内/组末尾，flex 坐标错乱（卡最右、组内游离光标）从构造上消失；
//     容器自掌 DOM 后，多行画廊（可上现成库）、多选、拖拽排序、磁吸都好做。
//   · **inline**（而非块级）：与单图（image，行内 atom）同构——容器活在一个段落里，组前=段落 offset 0、组后=offset 1，
//     于是「删容器前/后的空行」完全交给 PM 原生段落合并（块级 atom 做不到→才有「退格选中容器/光标跳下一行」的反直觉）；
//     且段落是 textblock，容器与上方表格/块之间不再产生横向 gapCursor。独占一行（组旁打字→另起一行）复用 createEditor
//     里给单图写的那套独占逻辑（image/imageGroup 一视同仁，见 isImageLikeNode）。
// 排版（Option B：用户掌控的行）：images 扁平存（选区/裁剪/翻转/删除/多选全沿用扁平 index，零改动），行结构另存
//   `rows: number[]`（每行图数，Σ==图数；一行=扁平数组的连续切片）。NodeView 把图 wrap 按 rows 分装进多个
//   `.mica-gallery-row`（容器 flex-column 堆叠）；每行 nowrap + 每图 `flex-grow=宽高比`（renderer 加载后写）→ 行内各图
//   等高、整行填满（每行都填满，含末行）。用户拖图改行结构（拖到行间/顶/底=新建行）；工具条两个快捷按钮：单行(rows=[N])
//   / 自动排序(autoArrangeRows 按宽高比 DP 最优分行)。后续可加 carousel 轮播 / magnetic 磁吸。
// 序列化：`<div data-mica-gallery data-rows="3,2">…<img>…</div>`（沿用表格「整块 HTML + remark 往返」；remark 把这块
//   div html 包进 paragraph，行内节点才认领得到——见 transformTree）。旧图册（无 data-rows）→ normalizeRows 回退单行。
import type { Node as ProseNode, Schema } from '@milkdown/prose/model';
import type { NodeView, EditorView } from '@milkdown/prose/view';
import { Decoration, DecorationSet } from '@milkdown/prose/view';
import { Plugin, PluginKey, NodeSelection } from '@milkdown/prose/state';
import { $nodeSchema, $prose, $remark } from '@milkdown/utils';
import { serializeImg, parseImgHtml, createImageRenderer } from './imageNode';
import type { ImageAttrs } from './imageNode';
import { notify } from '../ipc/bridge';
import { startImageDrag, galleryRegistry } from './imageDrag';

// ===== 行结构（Option B：用户掌控的行）=====
// images 扁平存（选区/裁剪/翻转/删除/多选全沿用扁平 index，零改动），行结构另存 rows:number[]=每行图数。一行 = 扁平
// 数组的连续切片。Σrows 必须 == images.length；缺失/不一致 → 规整为单行 [N]（normalizeRows 会修）。
export function normalizeRows(images: ImageAttrs[], rows: unknown): number[] {
  const n = images.length;
  if (n === 0) return [];
  const arr = Array.isArray(rows) ? (rows as unknown[]).map((x) => Math.floor(Number(x) || 0)).filter((x) => x > 0) : [];
  const sum = arr.reduce((a, b) => a + b, 0);
  return sum === n && arr.length > 0 ? arr : [n]; // 不一致/空 → 单行兜底（删除/插入路径都会主动维护 rows、不会走到这）
}
// images + rows → 二维网格（每行一个数组）。
export function toGrid(images: ImageAttrs[], rows: number[]): ImageAttrs[][] {
  const grid: ImageAttrs[][] = [];
  let k = 0;
  for (const size of rows) { grid.push(images.slice(k, k + size)); k += size; }
  return grid;
}
// 二维网格 → images + rows（丢弃空行）。
export function flattenGrid(grid: ImageAttrs[][]): { images: ImageAttrs[]; rows: number[] } {
  const nonEmpty = grid.filter((r) => r.length > 0);
  return { images: nonEmpty.flat(), rows: nonEmpty.map((r) => r.length) };
}
// 扁平 index → {row, col}（供拖拽把「第 N 张」定位到具体行）。
export function flatToRowCol(rows: number[], index: number): { row: number; col: number } {
  let k = 0;
  for (let r = 0; r < rows.length; r++) { if (index < k + rows[r]) return { row: r, col: index - k }; k += rows[r]; }
  return { row: Math.max(0, rows.length - 1), col: 0 };
}
// 删除若干扁平 index（图已从扁平数组移除）后维护 rows——必须在对的行减计数、并丢空行，否则切片错位。
export function removeImagesFromGroup(images: ImageAttrs[], rows: unknown, flatIndices: number[]): { images: ImageAttrs[]; rows: number[] } {
  const grid = toGrid(images, normalizeRows(images, rows));
  const del = new Set(flatIndices);
  let flat = 0;
  const next = grid.map((row) => row.filter(() => !del.has(flat++)));
  return flattenGrid(next);
}
// 在扁平位置 flatPos 处插入若干图（落进该位置所在行的对应列），维护 rows。
export function insertImagesInGroup(images: ImageAttrs[], rows: unknown, flatPos: number, add: ImageAttrs[]): { images: ImageAttrs[]; rows: number[] } {
  const norm = normalizeRows(images, rows);
  const grid = toGrid(images, norm);
  if (grid.length === 0) return { images: add.slice(), rows: add.length ? [add.length] : [] };
  const pos = Math.max(0, Math.min(flatPos, images.length));
  const { row, col } = pos >= images.length
    ? { row: grid.length - 1, col: grid[grid.length - 1].length } // 末位 → 末行行尾
    : flatToRowCol(norm, pos);
  grid[row].splice(col, 0, ...add);
  return flattenGrid(grid);
}
// 自动排序：按每图宽高比做 order-preserving 最优分行（Flickr/Google 相册式「等高 justified」），返回 rows:number[]。
// 算法：每行图按各自宽高比拉伸填满整行 → 行高 = (行宽 − 间距) / Σ宽高比；DP 找一种分行，使「各行行高 与 理想行高」
// 的平方和最小。不打乱顺序（保序更不突兀），仅挑全局最优断点。
// **理想行高 = 320px**（原 220 太矮 → 少量图被压成一行、图太小，用户实测嫌不顺眼）：理想行高越高 → 每行图越少 →
// 行越多、图越大。320 能让「3 近正方 + 2 横图」这类 5 张图自动排成 3+2（行高≈320/270），复现手排的好看效果；
// 而「平方和」目标会让「最后一行只剩一张横图被拉到 500+px」这种行因偏离理想太远、代价爆炸而被自动避开（故不用贪心填行）。
export function autoArrangeRows(aspects: number[], containerWidth: number, idealRowHeight = 320, gap = 6): number[] {
  const n = aspects.length;
  if (n <= 1) return n === 1 ? [1] : [];
  const W = Math.max(1, containerWidth);
  // 一行 [i,j) justified 填满 W 时行高 = (W - gap*(cnt-1)) / Σaspect；badness = (行高-理想)²。
  const cost = (i: number, j: number): number => {
    let sumA = 0;
    for (let k = i; k < j; k++) sumA += aspects[k] > 0 ? aspects[k] : 1.5;
    const h = (W - gap * (j - i - 1)) / sumA;
    return (h - idealRowHeight) * (h - idealRowHeight);
  };
  const best = new Array(n + 1).fill(Infinity);
  const back = new Array(n + 1).fill(0);
  best[0] = 0;
  for (let j = 1; j <= n; j++) {
    for (let i = j - 1; i >= 0; i--) {
      const c = best[i] + cost(i, j);
      if (c < best[j]) { best[j] = c; back[j] = i; }
    }
  }
  const out: number[] = [];
  let j = n;
  while (j > 0) { const i = back[j]; out.unshift(j - i); j = i; }
  return out;
}

// 默认子图属性（src 之外全默认）。导出供 createEditor 往「已选中的容器」追加图时复用。
export function defaultImage(src: string): ImageAttrs {
  return { src, alt: '', title: null, width: null, align: null, radius: null, border: null, crop: null, flipH: false, flipV: false };
}

// ===== 从 `<div data-mica-gallery>…</div>` HTML 串解析出容器属性 =====
// 行结构存 data-rows="3,2"（每行图数）。旧图册（只有 data-layout、无 data-rows）→ normalizeRows 回退单行。
function parseGroupHtml(html: string): { rows: number[]; frameless: boolean; images: ImageAttrs[] } {
  const frameless = /\bdata-frameless\b/i.test(html);
  const images: ImageAttrs[] = [];
  const re = /<img\b[^>]*>/gi;
  let m: RegExpExecArray | null;
  while ((m = re.exec(html)) !== null) {
    const a = parseImgHtml(m[0]);
    if (a) images.push(a);
  }
  const rowsAttr = /data-rows\s*=\s*"([^"]*)"/i.exec(html)?.[1] || '';
  const rows = normalizeRows(images, rowsAttr.split(',').map((x) => Number(x)));
  return { rows, frameless, images };
}

// ===== 序列化：imageGroup PM 节点 → `<div data-mica-gallery data-rows="3,2">…<img>…</div>`（markdown）=====
function serializeGroup(node: ProseNode): string {
  const images = (node.attrs.images as ImageAttrs[]) || [];
  const rows = normalizeRows(images, node.attrs.rows);
  const frameless = node.attrs.frameless ? ' data-frameless' : '';
  return `<div data-mica-gallery data-rows="${rows.join(',')}"${frameless}>${images.map(serializeImg).join('')}</div>`;
}

// ===== 节点 schema（行内原子；子图存 images 属性）=====
export const imageGroupNode = $nodeSchema('imageGroup', () => ({
  inline: true,      // 行内：活在段落里，组前/后是段落 offset 0/1 → 删空行走 PM 原生段落合并、无 gapCursor（与单图同构）
  group: 'inline',
  atom: true,        // 叶子节点：无内部内容、无内部光标位（这是修一票光标 bug 的关键）
  marks: '',
  selectable: true,
  draggable: false, // 拖拽改走自绘指针拖拽（imageDrag.ts）；schema 不声明 draggable，否则 PM 给 NodeView dom 加
                    // draggable="true" → 起手时浏览器原生 drag 介入、显示 ⊘ 禁止图标（反馈 3）
  attrs: {
    rows: { default: [] as number[] },   // 每行图数（Σ==images.length）；空=按单行渲染
    images: { default: [] as ImageAttrs[] },
    frameless: { default: false },
  },
  parseDOM: [
    {
      tag: 'div[data-mica-gallery]',
      getAttrs: (dom: HTMLElement) => {
        const { rows, frameless, images } = parseGroupHtml(dom.outerHTML);
        return { rows, frameless, images };
      },
    },
  ],
  // toDOM 仅用于复制到剪贴板 / parseDOM 往返（编辑显示走 NodeView）：直接吐 `<div data-mica-gallery data-rows>…<img>…</div>`。
  toDOM: (node: ProseNode) => {
    const div = document.createElement('div');
    div.className = 'mica-image-group';
    div.setAttribute('data-mica-gallery', '');
    div.setAttribute('data-rows', normalizeRows((node.attrs.images as ImageAttrs[]) || [], node.attrs.rows).join(','));
    if (node.attrs.frameless) div.setAttribute('data-frameless', '');
    for (const a of (node.attrs.images as ImageAttrs[]) || []) div.insertAdjacentHTML('beforeend', serializeImg(a));
    return div;
  },
  parseMarkdown: {
    match: (node) => node.type === 'mica_image_group',
    runner: (state, node, type) => {
      const { rows, frameless, images } = parseGroupHtml(String(node.value ?? ''));
      // 空图册（images=[]）也认领：空容器是一等对象、可独立存在（占位态），存盘往返不丢。
      state.addNode(type, { rows, frameless, images });
    },
  },
  toMarkdown: {
    match: (node) => node.type.name === 'imageGroup',
    runner: (state, node) => {
      state.addNode('html', undefined, serializeGroup(node));
    },
  },
}));

// 新建图片组节点（多图粘贴/插入用）。子图一律默认属性（大小交给 flex）。默认单行（rows=[N]），用户再拖/自动排序成多行。
export function buildImageGroupNode(schema: Schema, srcs: string[]): ProseNode | null {
  const type = schema.nodes.imageGroup;
  if (!type || srcs.length === 0) return null;
  return type.create({ rows: [srcs.length], images: srcs.map(defaultImage), frameless: false });
}

// ===== 「当前激活的组内图」= 插件状态（位置随事务自动映射、选区离开自动清空）=====
// 子图不是 PM 节点，故 PM 选区只能是 NodeSelection(整个容器)；「具体激活哪一张」由这个插件状态补。放插件状态而非
// 模块变量的关键好处：① 方向键/删除等外部操作改它后，下面的 decorations 会变 → NodeView 收到新 deco 即时重渲染高亮；
// ② 容器位置随文档编辑自动 map；③ 选区一旦离开该容器（不再是 NodeSelection 落在它身上）自动清空，无需手动维护。
// index = 锚点（最后点击的那张，供方向键/范围选/浮条信息行用）；indices = 完整选中集（多选时含 index，升序去重）。
// 单选时 indices 省略 = [index]。多选（Ctrl/Shift+点击）时 indices 列出全部选中图。
export interface ActiveGroupImage { pos: number; index: number; indices?: number[] }
export const imageGroupActiveKey = new PluginKey<ActiveGroupImage | null>('mica-image-group-active');

// 读当前激活的组内图（供 createEditor 的删除/方向键/浮条接线用）。
export function getActiveGroupImage(view: EditorView): ActiveGroupImage | null {
  return imageGroupActiveKey.getState(view.state) ?? null;
}

// 规整选中集：含锚点 index、升序去重、过滤越界（删除/改组后保持合法）。
export function groupSelectionIndices(a: ActiveGroupImage, len: number): number[] {
  const raw = a.indices && a.indices.length ? a.indices : [a.index];
  const set = new Set<number>();
  for (const i of raw) if (i >= 0 && i < len) set.add(i);
  if (a.index >= 0 && a.index < len) set.add(a.index);
  return [...set].sort((x, y) => x - y);
}

// 当前组内选中集（锚点 + 全部选中下标），供浮条批量改属性/删除用。无激活组内图则 null。
export interface GroupSelection { pos: number; index: number; indices: number[] }
export function getGroupSelection(view: EditorView): GroupSelection | null {
  const a = getActiveGroupImage(view);
  if (!a) return null;
  const node = view.state.doc.nodeAt(a.pos);
  if (!node || node.type.name !== 'imageGroup') return null;
  const len = ((node.attrs.images as ImageAttrs[]) || []).length;
  const indices = groupSelectionIndices(a, len);
  if (indices.length === 0) return null;
  const index = indices.includes(a.index) ? a.index : indices[indices.length - 1];
  return { pos: a.pos, index, indices };
}

// ===== 统一「当前聚焦的图」抽象（浮条/裁剪/翻转/替换/右键菜单共用，屏蔽独立图 vs 组内图差异）=====
// 独立图：image PM 节点（pos）；组内图：imageGroup.attrs.images[index]（不是 PM 节点）。两者读写/取 DOM/删除
// 的入口完全不同，故抽成 ImageTarget，让上层（imageToolbar/imageSetup）一套代码两态通吃。
export type ImageTarget =
  | { kind: 'image'; pos: number }
  | { kind: 'group'; pos: number; index: number };

// 当前聚焦的图：优先组内激活图（NodeSelection 落在容器上 + 激活 index），否则独立图 NodeSelection。
export function currentImageTarget(view: EditorView): ImageTarget | null {
  const a = getActiveGroupImage(view);
  if (a) return { kind: 'group', pos: a.pos, index: a.index };
  const sel = view.state.selection;
  if (sel instanceof NodeSelection && sel.node.type.name === 'image') return { kind: 'image', pos: sel.from };
  return null;
}

export function sameImageTarget(a: ImageTarget | null, b: ImageTarget | null): boolean {
  if (!a || !b || a.kind !== b.kind || a.pos !== b.pos) return false;
  return a.kind === 'group' ? a.index === (b as { index: number }).index : true;
}

// 读目标图属性（独立图取节点 attrs，组内图取 images[index]）。
export function readTargetAttrs(view: EditorView, t: ImageTarget): ImageAttrs | null {
  const node = view.state.doc.nodeAt(t.pos);
  if (!node) return null;
  if (t.kind === 'image') return node.type.name === 'image' ? (node.attrs as unknown as ImageAttrs) : null;
  if (node.type.name !== 'imageGroup') return null;
  return ((node.attrs.images as ImageAttrs[]) || [])[t.index] ?? null;
}

// 写目标图属性（patch 合并）。保持选区：独立图重选该图；组内图重选容器并维持激活 index（浮条不消失、可连续调）。
// 默认不 scrollIntoView、不 focus（供浮条连续微调用）；scroll:true 时滚动到位（供右键菜单/裁剪回传用）。
export function writeTargetAttrs(view: EditorView, t: ImageTarget, patch: Partial<ImageAttrs>, opts?: { scroll?: boolean }): boolean {
  const node = view.state.doc.nodeAt(t.pos);
  if (!node) return false;
  let tr;
  if (t.kind === 'image') {
    if (node.type.name !== 'image') return false;
    tr = view.state.tr.setNodeMarkup(t.pos, undefined, { ...node.attrs, ...patch });
    tr = tr.setSelection(NodeSelection.create(tr.doc, t.pos));
  } else {
    if (node.type.name !== 'imageGroup') return false;
    const imgs = ((node.attrs.images as ImageAttrs[]) || []).slice();
    if (t.index < 0 || t.index >= imgs.length) return false;
    imgs[t.index] = { ...imgs[t.index], ...patch };
    tr = view.state.tr.setNodeMarkup(t.pos, undefined, { ...node.attrs, images: imgs });
    tr = tr.setSelection(NodeSelection.create(tr.doc, t.pos)).setMeta(imageGroupActiveKey, { pos: t.pos, index: t.index });
  }
  view.dispatch(opts?.scroll ? tr.scrollIntoView() : tr);
  return true;
}

// 目标图的实时 DOM（img + 外层 wrap）：独立图 nodeDOM(pos) 即 wrap；组内图取容器内 data-index 的 wrap。
export function targetDom(view: EditorView, t: ImageTarget): { img: HTMLImageElement; wrap: HTMLElement } | null {
  const dom = view.nodeDOM(t.pos);
  if (!(dom instanceof HTMLElement)) return null;
  const wrap = t.kind === 'image'
    ? (dom.classList.contains('mica-image-wrap') ? dom : null)
    : (dom.querySelector(`.mica-image-wrap[data-index="${t.index}"]`) as HTMLElement | null);
  if (!wrap) return null;
  const img = wrap.querySelector('img.mica-image') as HTMLImageElement | null;
  return img ? { img, wrap } : null;
}

// 删除目标图：独立图删节点；组内图删 images[index]（删剩则激活同位下一张；删光只留空图册占位、不删容器——空图册是一等对象）。
// 不含二次确认（确认逻辑在键盘删除路径 deleteActiveGroupImage / handleImageDelete；浮条/菜单是显式点击，直接删）。
export function deleteTarget(view: EditorView, t: ImageTarget, opts?: { scroll?: boolean }): boolean {
  const node = view.state.doc.nodeAt(t.pos);
  if (!node) return false;
  let tr;
  if (t.kind === 'image') {
    if (node.type.name !== 'image') return false;
    tr = view.state.tr.delete(t.pos, t.pos + node.nodeSize);
  } else {
    if (node.type.name !== 'imageGroup') return false;
    const cur = (node.attrs.images as ImageAttrs[]) || [];
    if (t.index < 0 || t.index >= cur.length) return false;
    const { images, rows } = removeImagesFromGroup(cur, node.attrs.rows, [t.index]); // 同步维护 rows（在对的行减计数、丢空行）
    tr = view.state.tr.setNodeMarkup(t.pos, undefined, { ...node.attrs, images, rows });
    if (images.length > 0) {
      const ni = Math.min(t.index, images.length - 1);
      tr = tr.setSelection(NodeSelection.create(tr.doc, t.pos)).setMeta(imageGroupActiveKey, { pos: t.pos, index: ni });
    } else {
      tr = tr.setMeta(imageGroupActiveKey, null);
    }
  }
  view.dispatch(opts?.scroll ? tr.scrollIntoView() : tr);
  return true;
}

// ===== 组内多选：批量改属性 / 批量翻转 / 批量删除（浮条 & 键盘删除用）=====
// 提交后保持 NodeSelection(容器) + 维持锚点 anchor 与选中集 indices（浮条不消失、可连续调）。
// patch 合并到所有选中图。
export function writeGroupImagesPatch(view: EditorView, pos: number, indices: number[], anchor: number, patch: Partial<ImageAttrs>, opts?: { scroll?: boolean }): boolean {
  const node = view.state.doc.nodeAt(pos);
  if (!node || node.type.name !== 'imageGroup') return false;
  const imgs = ((node.attrs.images as ImageAttrs[]) || []).slice();
  let changed = false;
  for (const i of indices) if (i >= 0 && i < imgs.length) { imgs[i] = { ...imgs[i], ...patch }; changed = true; }
  if (!changed) return false;
  let tr = view.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, images: imgs });
  tr = tr.setSelection(NodeSelection.create(tr.doc, pos)).setMeta(imageGroupActiveKey, { pos, index: anchor, indices });
  view.dispatch(opts?.scroll ? tr.scrollIntoView() : tr);
  return true;
}

// 批量翻转：每张选中图按**自身**当前状态独立 toggle（axis='h'|'v'），故混合状态下各自镜像。
export function flipGroupImages(view: EditorView, pos: number, indices: number[], anchor: number, axis: 'h' | 'v', opts?: { scroll?: boolean }): boolean {
  const node = view.state.doc.nodeAt(pos);
  if (!node || node.type.name !== 'imageGroup') return false;
  const imgs = ((node.attrs.images as ImageAttrs[]) || []).slice();
  let changed = false;
  for (const i of indices) if (i >= 0 && i < imgs.length) {
    imgs[i] = axis === 'h' ? { ...imgs[i], flipH: !imgs[i].flipH } : { ...imgs[i], flipV: !imgs[i].flipV };
    changed = true;
  }
  if (!changed) return false;
  let tr = view.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, images: imgs });
  tr = tr.setSelection(NodeSelection.create(tr.doc, pos)).setMeta(imageGroupActiveKey, { pos, index: anchor, indices });
  view.dispatch(opts?.scroll ? tr.scrollIntoView() : tr);
  return true;
}

// 批量删除选中图：降序 splice；删剩 → 锚点落到「最小被删下标处顶上来的图」（clamp）；删光 → 只留空图册占位（容器不删）。
export function deleteGroupImages(view: EditorView, pos: number, indices: number[], opts?: { scroll?: boolean }): boolean {
  const node = view.state.doc.nodeAt(pos);
  if (!node || node.type.name !== 'imageGroup') return false;
  const cur = (node.attrs.images as ImageAttrs[]) || [];
  const del = [...new Set(indices)].filter((i) => i >= 0 && i < cur.length).sort((a, b) => b - a);
  if (del.length === 0) return false;
  const minDel = del[del.length - 1];
  const { images: imgs, rows } = removeImagesFromGroup(cur, node.attrs.rows, del); // 同步维护 rows
  let tr = view.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, images: imgs, rows });
  if (imgs.length > 0) {
    const ni = Math.min(minDel, imgs.length - 1);
    tr = tr.setSelection(NodeSelection.create(tr.doc, pos)).setMeta(imageGroupActiveKey, { pos, index: ni });
  } else {
    tr = tr.setMeta(imageGroupActiveKey, null); // 删光 → 只留空图册占位（容器是一等对象，要删整册得显式选容器再删）
  }
  view.dispatch(opts?.scroll ? tr.scrollIntoView() : tr);
  return true;
}

// 注：组内重排（含跨界拖出/拖入）统一由 imageDrag.ts 的 startImageDrag/performDrop 处理（见该文件）。

// 激活状态插件：apply 据 meta 设/清，并校验「选区仍是落在该容器上的 NodeSelection 且 index 合法」否则清空（多选下标越界自动过滤）；
// decorations 给当前激活容器打一个带 spec.micaActiveIndices（选中集）的 node decoration（attrs 里塞会变的 data-aidx 以确保
// 选中集变化时 decoration 集合判不等 → NodeView.update 必被调用、刷新高亮）。
export const imageGroupActivePlugin = $prose(() =>
  new Plugin<ActiveGroupImage | null>({
    key: imageGroupActiveKey,
    state: {
      init: () => null,
      apply(tr, value, _old, newState) {
        const meta = tr.getMeta(imageGroupActiveKey) as ActiveGroupImage | null | undefined;
        let next: ActiveGroupImage | null;
        if (meta !== undefined) next = meta;                                    // 显式设/清（pos 已是本 tr 结果坐标）
        else if (value && tr.docChanged) next = { pos: tr.mapping.map(value.pos, -1), index: value.index, indices: value.indices };
        else next = value;
        if (!next) return null;
        const sel = newState.selection;                                        // 只在「该容器被 NodeSelection 选中」时有效
        if (!(sel instanceof NodeSelection) || sel.from !== next.pos) return null;
        const node = newState.doc.nodeAt(next.pos);
        if (!node || node.type.name !== 'imageGroup') return null;
        const len = ((node.attrs.images as ImageAttrs[]) || []).length;
        if (next.index < 0 || next.index >= len) return null;
        // 过滤越界的多选下标（删除/改组后保持合法）；过滤后空 → 退化为单选锚点。
        if (next.indices) {
          const filtered = next.indices.filter((i) => i >= 0 && i < len);
          next = filtered.length ? { ...next, indices: filtered } : { pos: next.pos, index: next.index };
        }
        return next;
      },
    },
    props: {
      decorations(state) {
        const a = imageGroupActiveKey.getState(state);
        if (!a) return null;
        const node = state.doc.nodeAt(a.pos);
        if (!node || node.type.name !== 'imageGroup') return null;
        const indices = groupSelectionIndices(a, ((node.attrs.images as ImageAttrs[]) || []).length);
        // attrs 里塞会变的 data-aidx（选中集 join）确保选中集变化时 decoration 集合判不等 → NodeView.update 必触发刷高亮。
        return DecorationSet.create(state.doc, [
          Decoration.node(a.pos, a.pos + node.nodeSize, { 'data-aidx': indices.join(',') }, { micaActiveIndices: indices }),
        ]);
      },
    },
  }),
);

// 从 node decoration 数组里读激活子图集（imageGroupActivePlugin 打的 spec.micaActiveIndices）；无则 []。
function readActiveIndices(decos: readonly Decoration[] | undefined): number[] {
  for (const d of decos ?? []) {
    const idx = (d.spec as { micaActiveIndices?: number[] } | undefined)?.micaActiveIndices;
    if (idx) return idx;
  }
  return [];
}

// 读「待删子图集」（imageDeletePlugin 二次确认时打的 spec.micaPendingDeleteIndices）；无则 []。
function readPendingDeleteIndices(decos: readonly Decoration[] | undefined): number[] {
  for (const d of decos ?? []) {
    const idx = (d.spec as { micaPendingDeleteIndices?: number[] } | undefined)?.micaPendingDeleteIndices;
    if (idx) return idx;
  }
  return [];
}

// ===== NodeView：容器自掌内部 DOM；点图选图、点空白选容器；组内永不落文本光标 =====
// 激活高亮（哪张图被选中）不存 NodeView 闭包，而是从 imageGroupActivePlugin 的 node decoration 读
// （update 第 2 参带 deco）——这样方向键/删除等外部操作改了激活态，PM 会带新 deco 调 update，高亮即时跟上。
export function createImageGroupNodeView() {
  return (node: ProseNode, view: EditorView, getPos: () => number | undefined, decorations?: readonly Decoration[]): NodeView => {
    const dom = document.createElement('span'); // 行内节点 → 行内级元素；CSS .mica-image-group 设 flex-column 堆叠多行
    dom.className = 'mica-image-group';
    dom.draggable = false; // 双保险：禁原生 drag（自绘指针拖拽接管），免起手出 ⊘ 禁止图标
    if (node.attrs.frameless) dom.dataset.frameless = '';

    const renderers: ReturnType<typeof createImageRenderer>[] = [];
    let activeIndices = new Set<number>(readActiveIndices(decorations));
    let pendingDeleteIndices = new Set<number>(readPendingDeleteIndices(decorations));

    const syncActive = () => {
      renderers.forEach((r, i) => {
        r.wrap.classList.toggle('mica-img-active', activeIndices.has(i));               // 选中（可多张）蓝描边
        r.wrap.classList.toggle('mica-pending-delete', pendingDeleteIndices.has(i));    // 二次确认：待删那些标红框
      });
      dom.classList.toggle('mica-has-active', activeIndices.size > 0); // 有激活子图时不画「整容器选中」底色
    };

    // 空图册占位：无图时显示两个小按钮——「添加图片」（开文件选择器）/「粘贴」（粘剪贴板图）。点击都先选中本图册，
    // 再通知宿主对应动作；宿主落盘后经 editor.insertImages 追加进本图册（placeImageSrcs 见选中的是 imageGroup → 追加）。
    let placeholder: HTMLElement | null = null;
    const showPlaceholder = () => {
      if (!placeholder) {
        placeholder = document.createElement('span');
        placeholder.className = 'mica-image-group-empty';
        placeholder.contentEditable = 'false';
        // 两个并列 WinUI3 风按钮：都是「添加图片」，区别在来源——从文件 / 从剪贴板。文案与图标区分清楚（反馈 6）。
        const folderIcon = '<svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M1.6 4.2a1 1 0 0 1 1-1h3l1.4 1.6h5.4a1 1 0 0 1 1 1v6.4a1 1 0 0 1-1 1h-10.2a1 1 0 0 1-1-1z"/></svg>';
        const clipIcon = '<svg viewBox="0 0 16 16" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><rect x="3.2" y="2.4" width="9.6" height="12" rx="1.4"/><rect x="5.6" y="1.2" width="4.8" height="2.6" rx="1"/></svg>';
        placeholder.innerHTML =
          `<button type="button" class="mica-image-group-empty-btn" data-act="add"><span class="mica-image-group-empty-icon">${folderIcon}</span>从文件添加</button>`
          + `<button type="button" class="mica-image-group-empty-btn" data-act="paste"><span class="mica-image-group-empty-icon">${clipIcon}</span>从剪贴板添加</button>`;
        placeholder.addEventListener('click', (e) => {
          e.preventDefault();
          const btn = (e.target as HTMLElement).closest('[data-act]') as HTMLElement | null;
          const act = btn?.dataset.act;
          if (!act) return;
          const pos = getPos();
          if (pos != null) view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, pos)).setMeta(imageGroupActiveKey, null));
          notify('host.shortcut', { action: act === 'paste' ? 'pasteImage' : 'insertImage' });
        });
      }
      if (placeholder.parentElement !== dom) dom.appendChild(placeholder);
    };
    const removePlaceholder = () => { placeholder?.remove(); };

    // 按 images 增删 renderer（扁平、按全局 index），再按 rows 分行装进 .mica-gallery-row 容器；逐个 render（src 不变不重载）。
    // 每行 = 「nowrap + flex-grow=宽高比」→ 行内各图等高、整行填满；多行靠容器 flex-column 堆叠。
    const build = (n: ProseNode) => {
      const imgs = (n.attrs.images as ImageAttrs[]) || [];
      if (imgs.length === 0) {
        while (renderers.length) renderers.pop()!.wrap.remove();
        dom.querySelectorAll(':scope > .mica-gallery-row').forEach((r) => r.remove());
        showPlaceholder();
        // 关键：清掉残留的 mica-has-active（删光/拖光最后一张前曾高亮某子图 → 该 class 留在 dom 上，会经 CSS
        // `:not(.mica-has-active)` 守卫把「整容器选中框 + 浮条」全压住，导致删空后的图册选中了却看不见框/浮条、
        // 重开才好。此处 renderers 已空、activeIndices 已被 update 同步为空 → syncActive 会把它清除。
        syncActive();
        return;
      }
      removePlaceholder();
      while (renderers.length > imgs.length) renderers.pop()!.wrap.remove();
      while (renderers.length < imgs.length) renderers.push(createImageRenderer());
      // 先把所有 wrap 摘下（保留 renderer/img、不销毁→不重载），清掉旧行容器，再按 rows 重新分行装回。
      renderers.forEach((r) => r.wrap.remove());
      dom.querySelectorAll(':scope > .mica-gallery-row').forEach((r) => r.remove());
      const rows = normalizeRows(imgs, n.attrs.rows);
      let k = 0;
      for (const size of rows) {
        const rowEl = document.createElement('span');
        rowEl.className = 'mica-gallery-row';
        for (let c = 0; c < size && k < renderers.length; c++, k++) rowEl.appendChild(renderers[k].wrap);
        dom.appendChild(rowEl);
      }
      imgs.forEach((a, i) => { renderers[i].render(a, 'group'); renderers[i].wrap.dataset.index = String(i); });
      syncActive();
    };
    build(node);
    if (typeof getPos === 'function') galleryRegistry.set(dom, getPos); // 登记供跨界拖拽命中本册时反查位置

    // ===== 工具条（容器选中/悬浮时浮右上角；NodeView 自掌 DOM，绝对定位、不占行流）=====
    // 行结构由用户拖拽掌控，按钮只是快捷排列：单行（强制一行）/ 自动排序（按宽高比最优分行）+ 隐藏边框开关。
    const oneRowIcon = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3"><rect x="1.5" y="5" width="3.4" height="6" rx="1"/><rect x="6.3" y="5" width="3.4" height="6" rx="1"/><rect x="11.1" y="5" width="3.4" height="6" rx="1"/></svg>';
    const autoIcon = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3"><rect x="1.5" y="2.5" width="6" height="4.5" rx="1"/><rect x="8.5" y="2.5" width="6" height="4.5" rx="1"/><rect x="1.5" y="9" width="9" height="4.5" rx="1"/><rect x="11.5" y="9" width="3" height="4.5" rx="1"/></svg>';
    const frameIcon = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3"><rect x="2.5" y="2.5" width="11" height="11" rx="1.6"/></svg>';
    const bar = document.createElement('span');
    bar.className = 'mica-gallery-bar';
    bar.contentEditable = 'false';
    bar.innerHTML =
      `<button type="button" class="mica-gallery-bar-btn" data-act="oneRow" title="单行">${oneRowIcon}</button>`
      + `<button type="button" class="mica-gallery-bar-btn" data-act="auto" title="自动排序">${autoIcon}</button>`
      + '<span class="mica-gallery-bar-sep"></span>'
      + `<button type="button" class="mica-gallery-bar-btn" data-act="frameless" title="隐藏边框">${frameIcon}</button>`;
    // 各图实际显示宽高比：renderer 在 onMetrics 已把它写进 wrap 的 --mica-aspect（裁剪图按裁剪区）；未加载兜底 1.5。
    const aspectOf = (r: ReturnType<typeof createImageRenderer>) => {
      const v = parseFloat(r.wrap.style.getPropertyValue('--mica-aspect'));
      return v > 0 ? v : 1.5;
    };
    // mousedown 阶段就接管：stopPropagation 挡掉容器 mousedown 的「重设选区/起拖」，preventDefault 免失焦/落文本光标。
    bar.addEventListener('mousedown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      const act = ((e.target as HTMLElement).closest('[data-act]') as HTMLElement | null)?.dataset.act;
      if (!act) return;
      const pos = getPos();
      if (pos == null) return;
      const cur = view.state.doc.nodeAt(pos);
      if (!cur || cur.type.name !== 'imageGroup') return;
      const imgs = (cur.attrs.images as ImageAttrs[]) || [];
      const attrs = { ...cur.attrs };
      if (act === 'frameless') attrs.frameless = !cur.attrs.frameless;
      else if (act === 'oneRow') attrs.rows = imgs.length ? [imgs.length] : [];           // 强制全放一行
      else if (act === 'auto') attrs.rows = autoArrangeRows(renderers.map(aspectOf), (dom.clientWidth || 600) - 12); // 最优分行（减容器左右 padding）
      const tr = view.state.tr.setNodeMarkup(pos, undefined, attrs);
      tr.setSelection(NodeSelection.create(tr.doc, pos)); // 改完仍选中容器 → 条继续显示、可连续点
      view.dispatch(tr);
      view.focus();
    });
    dom.appendChild(bar);
    const syncBar = (n: ProseNode) => {
      bar.querySelector('[data-act="frameless"]')?.classList.toggle('mica-active', !!n.attrs.frameless);
    };
    syncBar(node);

    // 点击：点中某张图 → 选那张图（NodeSelection(容器) + meta 记 index）；点内边距/图间空白 → 选整个容器（meta=null）。
    // Ctrl/Cmd+点击 = 切换该图进/出选中集；Shift+点击 = 从锚点到该图选连续范围（对标文件管理器多选）。
    // preventDefault 阻止 PM 在原子上做默认选区/拖拽起手，自己 dispatch → 组内永不出现文本光标。激活态由插件 + deco 反映。
    dom.addEventListener('mousedown', (e) => {
      const pos = getPos();
      if (pos == null) return;
      const wrapEl = (e.target as HTMLElement).closest('.mica-image-wrap') as HTMLElement | null;
      e.preventDefault();
      // wrap 现嵌在 .mica-gallery-row 里（不再是 dom 的直接子）；只要在本容器内且带 data-index 即为某张图。
      const idx = wrapEl && dom.contains(wrapEl) && wrapEl.dataset.index != null ? Number(wrapEl.dataset.index) : -1;
      const tr = view.state.tr.setSelection(NodeSelection.create(view.state.doc, pos));
      if (idx < 0) {
        tr.setMeta(imageGroupActiveKey, null);                              // 点空白 → 选整个容器
      } else {
        const cur = imageGroupActiveKey.getState(view.state);
        const sameGroup = cur && cur.pos === pos;
        let meta: ActiveGroupImage | null;
        if (e.shiftKey && sameGroup) {                                      // 范围：锚点 cur.index → idx
          const lo = Math.min(cur!.index, idx), hi = Math.max(cur!.index, idx);
          const range: number[] = [];
          for (let i = lo; i <= hi; i++) range.push(i);
          meta = { pos, index: idx, indices: range };
        } else if ((e.ctrlKey || e.metaKey) && sameGroup) {                 // 切换该图进/出选中集
          const set = new Set(cur!.indices && cur!.indices.length ? cur!.indices : [cur!.index]);
          if (set.has(idx)) {
            set.delete(idx);
            const arr = [...set].sort((a, b) => a - b);
            meta = arr.length ? { pos, index: arr[arr.length - 1], indices: arr } : null; // 取消最后一张 → 选容器
          } else {
            set.add(idx);
            meta = { pos, index: idx, indices: [...set].sort((a, b) => a - b) };
          }
        } else {
          meta = { pos, index: idx };                                       // 普通点击 → 单选
        }
        tr.setMeta(imageGroupActiveKey, meta);
      }
      view.dispatch(tr);
      view.focus();

      // 拖拽：仅对「无修饰键点中某张图」启动（Ctrl/Shift 留给多选）。统一控制器 startImageDrag 接管
      // 册内重排 / 拖出册成独立图 / 拖到别的册（超阈值才算拖、否则当普通点击）。
      if (idx >= 0 && !e.shiftKey && !e.ctrlKey && !e.metaKey) {
        startImageDrag(view, e, { kind: 'group', dom, getPos, index: idx });
      }
    });

    return {
      dom,
      update: (n: ProseNode, decos?: readonly Decoration[]) => {
        if (n.type.name !== 'imageGroup') return false;
        activeIndices = new Set(readActiveIndices(decos));
        pendingDeleteIndices = new Set(readPendingDeleteIndices(decos));
        if (n.attrs.frameless) dom.dataset.frameless = ''; else delete dom.dataset.frameless;
        build(n);
        syncBar(n);
        return true;
      },
      ignoreMutation: () => true,
      // 让 PM 忽略容器上的鼠标事件：选区完全由上面的 mousedown 掌控。否则 PM 的 mouseup 命中判定会覆盖我们设的
      // 选区——点图上一致（都选原子）不覆盖，但点内边距/右侧空白会被 PM 判成「容器后的文本光标」，满宽容器→落到
      // 下一行（用户反馈「点右侧光标跳下一行」的真凶）。键盘事件 target 是编辑器根、不在本 NodeView，故不受影响。
      stopEvent: () => true,
      selectNode: () => { dom.classList.add('ProseMirror-selectednode'); },
      deselectNode: () => { dom.classList.remove('ProseMirror-selectednode'); }, // 激活态由插件随选区离开自动清空
      destroy: () => { galleryRegistry.delete(dom); }, // 注销跨界拖拽登记
    };
  };
}

// 注：原 imageGroupCleanupPlugin（删光图后连带删空壳容器）已下线——空图册现在是一等对象、可独立存在
// （占位态，见 NodeView showPlaceholder）。删图删到空只留占位；要删整个图册得显式选中容器再删（handleImageDelete）。

// ===== remark：把 `<div data-mica-gallery>` HTML 块改成 mica_image_group 类型（供 parseMarkdown 认领）=====
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
}
function isGroupHtml(value: unknown): boolean {
  return typeof value === 'string' && /^\s*<div\b[^>]*\bdata-mica-gallery\b/i.test(value);
}
function isGroupHtmlNode(n: MdNode): boolean {
  return n.type === 'html' && isGroupHtml(n.value);
}
// imageGroup 现在是**行内**节点（mica_image_group 须在 paragraph 内才认领得到），故与单图 <img> 同处理：
//   · 块级 `<div data-mica-gallery>` html（root/blockquote/list 等块容器的直接子）→ 包成 paragraph[mica_image_group]；
//   · 行内 `<div ...>`（理论上夹在段落文字间，实际 div 是块 html、罕见）→ 就地换成 mica_image_group。
function groupMdNode(value: string | undefined): MdNode {
  return { type: 'mica_image_group', value };
}
function transformTree(node: MdNode): void {
  if (!node.children) return;
  const out: MdNode[] = [];
  for (const child of node.children) {
    if (isGroupHtmlNode(child)) {
      out.push({ type: 'paragraph', children: [groupMdNode(child.value)] });
      continue;
    }
    if (child.children) {
      child.children = child.children.map((g) => (isGroupHtmlNode(g) ? groupMdNode(g.value) : g));
      transformTree(child);
    }
    out.push(child);
  }
  node.children = out;
}

export const micaImageGroupRemark = $remark('micaImageGroup', () => () => (tree: unknown) => {
  transformTree(tree as MdNode);
});
