// 图片跨界拖拽（第四期 c2）：统一处理「图册内重排 / 拖图出册成独立图 / 拖独立图入册 / 册间互拖 / 独立图重定位」。
// 自绘指针拖拽（非 HTML5 drag）：源（独立 image 节点 / 图册内某图）的 mousedown 调 startImageDrag → 移动超阈值
// 进入拖拽 → 实时算落点 → 松手 performDrop 在一个事务里「源移除 + 目标插入」。
//
// ★为什么彻底弃用原生 HTML5 drag：原生 drag 一旦由浏览器在 contenteditable 里发起，会**劫持指针**——`mouseup`
//   不再触发（改触发 dragend），于是自绘拖拽的 onUp 永不执行：图保持选中、松手不落、要再点一次才落，还一路显示
//   ⊘ 禁止图标，并把图册内容做成原生选区（::selection 灰底 → 整组变暗）。根治办法是在 createEditor 里 `dragstart`
//   一律 preventDefault（见那边的 onEditorDragStart），原生 drag 从源头不启动，本控制器的 mousemove/mouseup 才可靠。
//
// 落点指示：
//   · 命中某图册 → 册内竖线（in-flow，挂图册 DOM）。
//   · 否则文档落点 → **行内占位条（widget decoration）**：把一个 `.mica-image-drop-spacer` 插进文档流，自然把下方
//     内容往下推、明确示意落点（对标 Notion 块拖放）。比「挂 body 的横线」好：① 不必处理 body 的 CSS zoom 坐标换算；
//     ② 列表视为整体，只落到列表上/下边界（不插进项间——插项会带编号、且落点抖动闪烁，用户拍板回退）；③ 推开
//     下方内容、落点一目了然（反馈 3）。占位条位置由本控制器
//     经 imageDropKey meta 通知 createEditor 里的 imageDropSpacerPlugin 渲染。
// 关键便利：imageGroup 是行内 atom（nodeSize 恒 1）→ 从图册增删子图只改 attrs、不移动文档位置；只有文档级
//   insert/delete 移动位置，用 tr.mapping 处理。
import type { EditorView } from '@milkdown/prose/view';
import type { EditorState, Transaction } from '@milkdown/prose/state';
import { NodeSelection, Selection, PluginKey } from '@milkdown/prose/state';
import type { ImageAttrs } from './imageNode';
import { imageGroupActiveKey, toGrid, flattenGrid, normalizeRows, flatToRowCol, removeImagesFromGroup } from './imageGroup';

// 活动图册注册表：图册 NodeView 创建时登记 dom→getPos、销毁时移除。拖拽命中某 .mica-image-group 时反查其文档位置。
export const galleryRegistry = new Map<HTMLElement, () => number | undefined>();

// 文档落点占位条的位置（由本控制器 setMeta，imageDropSpacerPlugin 据此渲染 widget）。null = 不显示。
export const imageDropKey = new PluginKey<number | null>('mica-image-drop-spacer');

// 拖拽源：独立图（image 节点）或图册内某图（imageGroup + index）。
export type DragSource =
  | { kind: 'image'; getPos: () => number | undefined }
  | { kind: 'group'; dom: HTMLElement; getPos: () => number | undefined; index: number };

// 落点（Option B 行结构）：
//   · gallery     = 落进图册第 row 行的第 insertBefore 张之前（行内插入/重排）。
//   · gallery-newrow = 在图册第 atRow 行位置新建一行（0..行数；拖到行间/顶/底）。
//   · doc         = 文档某位置 pos 处插一个独立段落（拖出图册）。
type DropTarget =
  | { kind: 'gallery'; pos: number; dom: HTMLElement; row: number; insertBefore: number }
  | { kind: 'gallery-newrow'; pos: number; dom: HTMLElement; atRow: number }
  | { kind: 'doc'; pos: number };

const clamp = (n: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, n));

// ===== 落点指示 =====
let galleryIndicator: HTMLElement | null = null;   // 行内插入：竖线（图高）
let newRowIndicator: HTMLElement | null = null;    // 新建行：横线（容器宽）
let dropIntoEl: HTMLElement | null = null;         // 空图册落区：整块高亮（class，无行内容画不了线）

function clearDropInto() { dropIntoEl?.classList.remove('mica-gallery-drop-into'); dropIntoEl = null; }
function clearGalleryIndicator() { galleryIndicator?.remove(); newRowIndicator?.remove(); clearDropInto(); }

// 空图册落点提示：册内无行内容、画不了竖线，故给整个容器加高亮 class（CSS 画强调描边+淡底），示意「松手放进这个图册」。
function showEmptyGalleryDropInto(groupDom: HTMLElement) {
  galleryIndicator?.remove();
  newRowIndicator?.remove();
  if (dropIntoEl && dropIntoEl !== groupDom) clearDropInto();
  groupDom.classList.add('mica-gallery-drop-into');
  dropIntoEl = groupDom;
}

// 行内插入竖线：贴目标图左缘（末尾贴右缘），**只跟那张图等高**（offsetTop/Left 相对定位的 group，row 非定位故同空间）。
function showGalleryIndicator(groupDom: HTMLElement, rowEl: HTMLElement, col: number) {
  newRowIndicator?.remove();
  clearDropInto();
  const wraps = Array.from(rowEl.querySelectorAll(':scope > .mica-image-wrap')) as HTMLElement[];
  if (!galleryIndicator) { galleryIndicator = document.createElement('span'); galleryIndicator.className = 'mica-group-drop-indicator'; }
  if (galleryIndicator.parentElement !== groupDom) groupDom.appendChild(galleryIndicator);
  const ref = wraps.length === 0 ? null : (col < wraps.length ? wraps[col] : wraps[wraps.length - 1]);
  const atEnd = col >= wraps.length;
  galleryIndicator.style.left = `${ref ? (atEnd ? ref.offsetLeft + ref.offsetWidth : ref.offsetLeft) : 8}px`;
  galleryIndicator.style.top = `${ref ? ref.offsetTop : 6}px`;
  galleryIndicator.style.height = `${ref ? ref.offsetHeight : 60}px`;
}

// 新建行横线：钉在第 atRow 行位置的行间空隙（顶上方/行间/底下方）。
function showNewRowIndicator(groupDom: HTMLElement, atRow: number) {
  galleryIndicator?.remove();
  clearDropInto();
  const rows = Array.from(groupDom.querySelectorAll(':scope > .mica-gallery-row')) as HTMLElement[];
  if (!newRowIndicator) { newRowIndicator = document.createElement('span'); newRowIndicator.className = 'mica-gallery-newrow-indicator'; }
  if (newRowIndicator.parentElement !== groupDom) groupDom.appendChild(newRowIndicator);
  let top = 6;
  if (rows.length > 0) {
    if (atRow <= 0) top = rows[0].offsetTop - 3;
    else if (atRow >= rows.length) { const last = rows[rows.length - 1]; top = last.offsetTop + last.offsetHeight + 3; }
    else { const prev = rows[atRow - 1]; top = prev.offsetTop + prev.offsetHeight + 3; }
  }
  newRowIndicator.style.top = `${top}px`;
}

// 据指针 x 算「插到该行第几张之前」（0..该行图数）。
function insertBeforeInRow(rowEl: HTMLElement, clientX: number): number {
  const wraps = Array.from(rowEl.querySelectorAll(':scope > .mica-image-wrap')) as HTMLElement[];
  for (let i = 0; i < wraps.length; i++) {
    const rect = wraps[i].getBoundingClientRect();
    if (clientX < rect.left + rect.width / 2) return i;
  }
  return wraps.length;
}

// 图册内落点：按 y 命中行 → 行内插入（x 定列）；y 在行间/顶上/底下 → 新建行。
function galleryDrop(dom: HTMLElement, pos: number, x: number, y: number): DropTarget {
  const rows = Array.from(dom.querySelectorAll(':scope > .mica-gallery-row')) as HTMLElement[];
  if (rows.length === 0) return { kind: 'gallery', pos, dom, row: 0, insertBefore: 0 }; // 空册（占位）—— 不该走到，兜底
  const rects = rows.map((r) => r.getBoundingClientRect());
  if (y < rects[0].top) return { kind: 'gallery-newrow', pos, dom, atRow: 0 };                              // 顶上方 → 新行
  if (y > rects[rects.length - 1].bottom) return { kind: 'gallery-newrow', pos, dom, atRow: rows.length };  // 底下方 → 新行
  for (let i = 0; i < rects.length; i++) {
    if (y <= rects[i].bottom) {
      if (y < rects[i].top) return { kind: 'gallery-newrow', pos, dom, atRow: i };                          // 落在第 i 行上方的行间 → 新行
      return { kind: 'gallery', pos, dom, row: i, insertBefore: insertBeforeInRow(rows[i], x) };            // 行内插入
    }
  }
  const last = rows.length - 1;
  return { kind: 'gallery', pos, dom, row: last, insertBefore: insertBeforeInRow(rows[last], x) };
}

// 据某块的 DOM 上/下半区决定「插到它之前还是之后」。
function decideBefore(view: EditorView, blockStartPos: number, y: number): boolean {
  const dom = view.nodeDOM(blockStartPos);
  if (dom instanceof HTMLElement && typeof dom.getBoundingClientRect === 'function') {
    const r = dom.getBoundingClientRect();
    return y < r.top + r.height / 2;
  }
  return true;
}

// 文档落点（不命中图册时）：用 posAtCoords 命中最内层位置，再按嵌套结构找合适的块边界：
//   · 在列表里 → 落到「整个列表的上/下边界」（列表视为整体，不插进列表项之间——插项会带编号、且占位条在窄项间
//     反复抖动闪烁，体验差；用户拍板回退为此）；
//   · 否则落到最近的叶子块（段落/标题/图段…）的上/下边界，插一个独立段落（图独占一行）。
function computeDocDrop(view: EditorView, x: number, y: number): DropTarget | null {
  const doc = view.state.doc;
  if (doc.childCount === 0) return null;
  const res = view.posAtCoords({ left: x, top: y });
  if (!res) return { kind: 'doc', pos: doc.content.size }; // 落在所有内容之下 → 文末
  const $pos = doc.resolve(res.pos);
  // ① 列表里 → 找最外层列表（从外往里第一个命中即最外层），落到它的上/下边界。
  let listDepth = -1;
  for (let dd = 1; dd <= $pos.depth; dd++) {
    const name = $pos.node(dd).type.name;
    if (name === 'bullet_list' || name === 'ordered_list') { listDepth = dd; break; }
  }
  if (listDepth >= 0) {
    const listStart = $pos.before(listDepth);
    const listNode = $pos.node(listDepth);
    const before = decideBefore(view, listStart, y);
    return { kind: 'doc', pos: before ? listStart : listStart + listNode.nodeSize };
  }
  // ② 否则落到最近的叶子块边界。
  let d = $pos.depth;
  while (d > 0 && !$pos.node(d).type.isBlock) d--;
  if (d === 0) return { kind: 'doc', pos: res.pos };
  const bStart = $pos.before(d);
  const bNode = $pos.node(d);
  const before = decideBefore(view, bStart, y);
  return { kind: 'doc', pos: before ? bStart : bStart + bNode.nodeSize };
}

function computeDropTarget(view: EditorView, x: number, y: number): DropTarget | null {
  // 命中某已登记图册（含其占位/内边距/子图）→ 册内落点。
  const el = document.elementFromPoint(x, y) as HTMLElement | null;
  const gdom = el?.closest('.mica-image-group') as HTMLElement | null;
  if (gdom && galleryRegistry.has(gdom)) {
    const pos = galleryRegistry.get(gdom)!();
    if (pos != null) return galleryDrop(gdom, pos, x, y);
  }
  return computeDocDrop(view, x, y);
}

// 读源图属性（独立图取节点 attrs，组内图取 images[index]）。
function readSourceAttrs(view: EditorView, src: DragSource): ImageAttrs | null {
  const pos = src.getPos();
  if (pos == null) return null;
  const node = view.state.doc.nodeAt(pos);
  if (!node) return null;
  if (src.kind === 'image') return node.type.name === 'image' ? (node.attrs as unknown as ImageAttrs) : null;
  if (node.type.name !== 'imageGroup') return null;
  return ((node.attrs.images as ImageAttrs[]) || [])[src.index] ?? null;
}

// 独立图的删除范围：若它独占一个段落（图独占一行的常态）→ 删整段（不留空行）；否则只删图节点。
// 但若全文档只剩这一个块，删段会让文档空（非法）→ 退回只删图节点（留空段）。
function imageRemovalRange(state: EditorState, pos: number): { from: number; to: number } {
  const node = state.doc.nodeAt(pos)!;
  const $pos = state.doc.resolve(pos);
  const parent = $pos.parent;
  if (parent.type.name === 'paragraph' && parent.childCount === 1 && state.doc.childCount > 1) {
    return { from: $pos.before(), to: $pos.after() };
  }
  return { from: pos, to: pos + node.nodeSize };
}

// 落点动画：给刚落下的那张图加一次性淡入（反馈 6）。只动 opacity（不碰 transform，避免与翻转 scaleX/scaleY 打架）。
function animateDropped(wrap: HTMLElement | null | undefined) {
  if (!wrap) return;
  wrap.classList.add('mica-just-dropped');
  wrap.addEventListener('animationend', () => wrap.classList.remove('mica-just-dropped'), { once: true });
}

// 把一张图按 DropTarget 插进图册网格（行内插入 / 新建行）。原地改 grid。
function insertIntoGrid(grid: ImageAttrs[][], drop: DropTarget, img: ImageAttrs) {
  if (drop.kind === 'gallery-newrow') {
    grid.splice(clamp(drop.atRow, 0, grid.length), 0, [img]);
  } else if (drop.kind === 'gallery') {
    const r = clamp(drop.row, 0, Math.max(0, grid.length - 1));
    if (!grid[r]) grid[r] = [];
    grid[r].splice(clamp(drop.insertBefore, 0, grid[r].length), 0, img);
  }
}

// 提交某图册的最终 images+rows，选回容器、激活落到 movedRef 那张图、加落点动画。
function commitGallery(view: EditorView, tr: Transaction, pos: number, baseAttrs: Record<string, unknown>, grid: ImageAttrs[][], movedRef: ImageAttrs) {
  const flat = flattenGrid(grid);
  const at = flat.images.indexOf(movedRef);
  tr.setNodeMarkup(pos, undefined, { ...baseAttrs, images: flat.images, rows: flat.rows })
    .setMeta(imageGroupActiveKey, { pos, index: at });
  tr.setSelection(NodeSelection.create(tr.doc, pos));
  view.dispatch(tr.scrollIntoView());
  animateDropped((view.nodeDOM(pos) as HTMLElement | null)?.querySelector(`.mica-image-wrap[data-index="${at}"]`) as HTMLElement | null);
}

// 落点执行：一个事务里完成「源移除 + 目标插入」（维护 rows），并把选区/激活态落到结果图上。
function performDrop(view: EditorView, src: DragSource, drop: DropTarget) {
  const attrs = readSourceAttrs(view, src);
  if (attrs == null) return;
  const srcPos = src.getPos();
  if (srcPos == null) return;
  const state = view.state;

  // 独立图重定位到「自身所在段落的同一块边界」→ 无操作（图本就独占该段、放回原边界没意义）。
  if (src.kind === 'image' && drop.kind === 'doc') {
    const $s = state.doc.resolve(srcPos);
    if ($s.parent.type.name === 'paragraph' && $s.parent.childCount === 1) {
      if (drop.pos === $s.before() || drop.pos === $s.after()) return;
    }
  }

  // ===== 同一图册内（重排 / 拆出新行）：单次 setNodeMarkup，在同一个 grid 上「移除源 → 插入目标」 =====
  if (src.kind === 'group' && (drop.kind === 'gallery' || drop.kind === 'gallery-newrow') && drop.pos === srcPos) {
    const node = state.doc.nodeAt(srcPos);
    if (!node || node.type.name !== 'imageGroup') return;
    const images = (node.attrs.images as ImageAttrs[]) || [];
    const rows = normalizeRows(images, node.attrs.rows);
    const grid = toGrid(images, rows);
    const { row: sr, col: sc } = flatToRowCol(rows, src.index);
    const [moved] = grid[sr].splice(sc, 1); // 只删元素、不删空行 → 目标行索引仍按原 grid 有效
    if (!moved) return;
    if (drop.kind === 'gallery') {
      let col = drop.insertBefore;
      if (drop.row === sr && col > sc) col -= 1; // 同行且落点在源右侧 → 移除后左移一位
      insertIntoGrid(grid, { ...drop, insertBefore: col }, moved);
    } else {
      insertIntoGrid(grid, drop, moved);
    }
    commitGallery(view, state.tr, srcPos, node.attrs, grid, moved); // flattenGrid 丢空行
    return;
  }

  const moved = { ...attrs }; // 落点用副本（identity 找新 index）
  const tr = state.tr;

  // 1) 源移除（图册：改 attrs 不移动文档位置；独立图：删段/删节点会移动其后位置，靠 tr.mapping 修正）
  if (src.kind === 'group') {
    const node = state.doc.nodeAt(srcPos);
    if (node && node.type.name === 'imageGroup') {
      const { images, rows } = removeImagesFromGroup((node.attrs.images as ImageAttrs[]) || [], node.attrs.rows, [src.index]);
      tr.setNodeMarkup(srcPos, undefined, { ...node.attrs, images, rows });
    }
  } else {
    const range = imageRemovalRange(state, srcPos);
    tr.delete(range.from, range.to);
  }

  // 2) 目标插入
  if (drop.kind === 'gallery' || drop.kind === 'gallery-newrow') {
    const gPos = tr.mapping.map(drop.pos);
    const node = tr.doc.nodeAt(gPos);
    if (node && node.type.name === 'imageGroup') {
      const grid = toGrid((node.attrs.images as ImageAttrs[]) || [], normalizeRows((node.attrs.images as ImageAttrs[]) || [], node.attrs.rows));
      insertIntoGrid(grid, drop, moved);
      commitGallery(view, tr, gPos, node.attrs, grid, moved);
      return;
    }
  } else {
    // 文档落点：图包成**独立段落**插到块边界 → 永远独占一行。光标落到图后（imageCaretPlugin 画竖线）。
    const at = tr.mapping.map(drop.pos);
    const schema = state.schema;
    const image = schema.nodes.image?.create({ ...attrs });
    const paragraph = schema.nodes.paragraph;
    if (image && paragraph) {
      const para = paragraph.create(null, image);
      tr.insert(at, para);
      try { tr.setSelection(Selection.near(tr.doc.resolve(at + para.nodeSize - 1), -1)); } catch { /* 忽略 */ }
      view.dispatch(tr.scrollIntoView());
      animateDropped((view.nodeDOM(at) as HTMLElement | null)?.querySelector('.mica-image-wrap') as HTMLElement | null);
      return;
    }
  }

  if (tr.docChanged) view.dispatch(tr.scrollIntoView());
}

// 启动拖拽（源 wrap 的 mousedown 调用）。移动超阈值才接管，否则交回默认（点击=选中图）。
export function startImageDrag(view: EditorView, e: MouseEvent, src: DragSource) {
  const startX = e.clientX, startY = e.clientY;
  let dragging = false;
  let drop: DropTarget | null = null;
  let lastSpacerPos: number | null = null;
  const srcWrap = src.kind === 'group'
    ? (src.dom.querySelector(`.mica-image-wrap[data-index="${src.index}"]`) as HTMLElement | null) // wrap 现嵌在 .mica-gallery-row 内，非直接子
    : (view.nodeDOM(src.getPos() ?? -1) as HTMLElement | null);

  // 文档落点占位条：经 meta 通知 imageDropSpacerPlugin 渲染/清除（仅位置变化时 dispatch，避免每像素都发）。
  const setSpacer = (pos: number | null) => {
    if (pos === lastSpacerPos) return;
    lastSpacerPos = pos;
    view.dispatch(view.state.tr.setMeta(imageDropKey, pos).setMeta('addToHistory', false));
  };

  const onMove = (me: MouseEvent) => {
    if (!dragging) {
      if (Math.abs(me.clientX - startX) + Math.abs(me.clientY - startY) < 5) return; // 阈值前不算拖
      dragging = true;
      document.body.classList.add('mica-image-dragging');
      srcWrap?.classList.add('mica-img-dragging');
      window.getSelection()?.removeAllRanges(); // 清掉拖拽起步那几像素可能起的文本选区
    }
    drop = computeDropTarget(view, me.clientX, me.clientY);
    if (!drop) { clearGalleryIndicator(); setSpacer(null); return; }
    if (drop.kind === 'gallery') {
      setSpacer(null);
      const rowEl = drop.dom.querySelectorAll(':scope > .mica-gallery-row')[drop.row] as HTMLElement | undefined;
      if (rowEl) showGalleryIndicator(drop.dom, rowEl, drop.insertBefore);
      else showEmptyGalleryDropInto(drop.dom); // 空图册（无行）→ 整块高亮，而非清掉指示（原 bug：拖进空册没标识）
    } else if (drop.kind === 'gallery-newrow') {
      setSpacer(null);
      showNewRowIndicator(drop.dom, drop.atRow);
    } else { clearGalleryIndicator(); setSpacer(drop.pos); }
  };
  const onUp = () => {
    document.removeEventListener('mousemove', onMove);
    document.removeEventListener('mouseup', onUp);
    if (!dragging) return; // 没拖过 = 普通点击，交回默认选中
    document.body.classList.remove('mica-image-dragging');
    srcWrap?.classList.remove('mica-img-dragging');
    clearGalleryIndicator();
    setSpacer(null); // 先清占位条（不影响文档位置：它是 decoration）
    if (drop) performDrop(view, src, drop);
    view.focus();
  };
  document.addEventListener('mousemove', onMove);
  document.addEventListener('mouseup', onUp);
}
