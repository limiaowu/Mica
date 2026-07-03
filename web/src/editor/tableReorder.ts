// 表格行/列拖动重排（浮动条「排序」开关启用）。
//
// 交互：开关点亮后，编辑中的那张表四周浮出「把手」——左侧每个**行块**一个、顶部每个**列块**一个。抓住把手
// 拖动时画落点指示线、松手即重排。再次点开关收起。这样和「平常拖选单元格 / 拖拽复制」不冲突（只有抓把手才进入重排）。
//
// 块模型（见 htmlTable.ts）：「干净边界」把表切成原子块——跨行/跨列合并把相关行/列绑成一个块、整体拖动；
// 干净的单行/单列自成一个块。故**每个块都可拖**，无「不可拖」概念。判定/移动事务都在 htmlTable.ts（模型层），
// 本模块只管把手 DOM、几何定位、拖拽手势。纯 web、无 IPC、不引入 UI 框架。
//
// ★坐标系：把手层挂在非缩放浮层（body 外、视口固定）上，**坐标全用视口(visual)坐标、无 z 换算**（见 tableToolbar
//   顶部架构说明）。把手尺寸直接取自被 zoom 缩放后的表格 rect → 把手长度天然随表格缩放对齐（用户要的效果）。
import type { EditorView as ProseEditorView } from '@milkdown/prose/view';
import type { Node as ProseNode } from '@milkdown/prose/model';
import { TableMap } from '@milkdown/prose/tables';
import {
  getCleanRowBoundaries, getCleanColBoundaries, boundariesToBlocks,
  moveTableRowsAt, moveTableColumnsAt,
} from './htmlTable';

const THICK = 14; // 把手厚度(px，视口固定——不随 zoom 变，始终好抓)
// 抓握点（6 个小圆点）。行把手竖窄条用默认 2×3；列把手横短条旋 90° 成 3×2（CSS 处理）。
const GRIP = '<svg viewBox="0 0 16 16" width="10" height="10" fill="currentColor"><circle cx="5.5" cy="4" r="1.05"/><circle cx="10.5" cy="4" r="1.05"/><circle cx="5.5" cy="8" r="1.05"/><circle cx="10.5" cy="8" r="1.05"/><circle cx="5.5" cy="12" r="1.05"/><circle cx="10.5" cy="12" r="1.05"/></svg>';

// overlay：非缩放浮层（由 tableToolbar 传入，挂在 documentElement 上）。
export function setupTableReorder(overlay: HTMLElement, getView: () => ProseEditorView | null) {
  const layer = document.createElement('div');
  layer.className = 'mica-table-handles';
  layer.style.display = 'none';
  const dropLine = document.createElement('div');
  dropLine.className = 'mtb-drop-line';
  dropLine.style.display = 'none';
  layer.appendChild(dropLine);
  overlay.appendChild(layer);

  let enabled = false;
  let pos: number | null = null;       // 当前作用的表格节点 pos
  let dragging = false;
  const handles: HTMLElement[] = [];

  function ensureAttached() { if (!layer.isConnected) overlay.appendChild(layer); }
  function clearHandles() { for (const h of handles) h.remove(); handles.length = 0; }
  function hideHandles() { clearHandles(); dropLine.style.display = 'none'; layer.style.display = 'none'; }

  // nodeDOM(pos) 是横向滚动外层 .mica-table-scroll；取其内部真正的 <table>（行/单元格 rect 都来自它）。
  function getInnerTable(v: ProseEditorView, p: number): HTMLElement | null {
    const dom = v.nodeDOM(p);
    if (!(dom instanceof HTMLElement)) return null;
    return dom.tagName === 'TABLE' ? dom : (dom.querySelector('table') as HTMLElement | null);
  }
  // 外层横向滚动框 .mica-table-scroll（= 可见视口；宽表横向滚动时它不动）。用它做「裁剪 / 自动滚动」。
  function getScrollEl(v: ProseEditorView, p: number): HTMLElement | null {
    const dom = v.nodeDOM(p);
    return dom instanceof HTMLElement ? dom : null;
  }

  // 算各列边界 x（视口坐标，长度 width+1）：DOM 单元格按 PM「起始列」配对（DOM td/th 顺序 == PM 子节点顺序）。
  // 干净列边界处必有某行的单元格边缘落在此 → colX 被填；非干净边界（被 colspan 跨过）保持 null。
  function computeColX(node: ProseNode, tableEl: HTMLElement): (number | null)[] {
    const map = TableMap.get(node);
    const width = map.width;
    const colX: (number | null)[] = new Array(width + 1).fill(null);
    const trs = tableEl.querySelectorAll(':scope > tbody > tr');
    let rowIdx = 0;
    node.forEach((row) => {
      const tr = trs[rowIdx] as HTMLElement | undefined;
      if (!tr) { rowIdx++; return; }
      const startCols: number[] = [];
      for (let col = 0; col < width; col++) {
        const r = map.findCell(map.map[rowIdx * width + col]);
        if (r.top === rowIdx && r.left === col) startCols.push(col);
      }
      const cellEls = tr.children;
      for (let k = 0; k < startCols.length && k < cellEls.length; k++) {
        const rect = (cellEls[k] as HTMLElement).getBoundingClientRect();
        const sc = startCols[k];
        const span = (row.child(k).attrs.colspan as number) || 1;
        if (colX[sc] == null) colX[sc] = rect.left;
        if (colX[sc + span] == null) colX[sc + span] = rect.right;
      }
      rowIdx++;
    });
    return colX;
  }

  // 行块各边界 y（视口坐标，长度 height+1）：取每行 <tr> 的上/下沿。
  function computeRowY(tableEl: HTMLElement, height: number): number[] {
    const trs = tableEl.querySelectorAll(':scope > tbody > tr');
    const rowY: number[] = [];
    for (let b = 0; b <= height; b++) {
      const tr = trs[b === 0 ? 0 : b - 1] as HTMLElement | undefined;
      if (!tr) { rowY.push(0); continue; }
      const rect = tr.getBoundingClientRect();
      rowY.push(b === 0 ? rect.top : rect.bottom);
    }
    return rowY;
  }

  // axis: 'row'|'col'，block=[start,end)
  function makeHandle(axis: 'row' | 'col', block: [number, number]): HTMLElement {
    const h = document.createElement('div');
    h.className = `mtb-handle mtb-${axis}h`;
    h.innerHTML = GRIP;
    const span = block[1] - block[0];
    h.title = axis === 'row'
      ? (span > 1 ? `拖动以移动这 ${span} 行（合并组）` : '拖动以移动此行')
      : (span > 1 ? `拖动以移动这 ${span} 列（合并组）` : '拖动以移动此列');
    h.addEventListener('mousedown', (e) => startDrag(e, axis, block));
    return h;
  }

  // 重新生成并摆放把手（表属性/尺寸/滚动变化后都调）。坐标全是视口坐标（浮层不缩放）。
  function refresh() {
    if (!enabled || pos == null) { hideHandles(); return; }
    const v = getView(); if (!v) { hideHandles(); return; }
    const node = v.state.doc.nodeAt(pos);
    if (!node || node.type.name !== 'table') { hideHandles(); return; }
    const tableEl = getInnerTable(v, pos);
    if (!tableEl) { hideHandles(); return; }
    ensureAttached();
    layer.style.display = 'block';
    clearHandles();
    const tRect = tableEl.getBoundingClientRect();
    // 可见视口 = 外层滚动框；宽表横向滚动时内表会移出它。把手定位/裁剪都以它为准（修「把手露在滚动框外」）。
    const scrollEl = getScrollEl(v, pos);
    const vp = (scrollEl ?? tableEl).getBoundingClientRect();
    const map = TableMap.get(node);
    // 行块把手：贴**可见视口**左缘（非内表左缘——横向滚动后内表左缘会跑到视口外），覆盖块内各行的合并高度。
    const rowY = computeRowY(tableEl, map.height);
    const rowBlocks = boundariesToBlocks(getCleanRowBoundaries(node));
    // 贴「表的可见左缘」= max(表真实左缘, 视口左缘)：居中/自适应表用表左缘（否则把手甩到滚动框最左、与表脱开）；
    // 宽表横向滚动后表左缘跑出视口 → 用视口左缘兜底（把手留在可见区左侧）。
    const rowHandleLeft = Math.max(tRect.left, vp.left) - THICK;
    for (const blk of rowBlocks) {
      const h = makeHandle('row', blk);
      h.style.left = `${rowHandleLeft}px`;
      h.style.top = `${rowY[blk[0]]}px`;
      h.style.width = `${THICK}px`;
      h.style.height = `${rowY[blk[1]] - rowY[blk[0]]}px`;
      layer.appendChild(h); handles.push(h);
    }
    // 列块把手：贴表格顶缘，覆盖块内各列宽；**横向裁剪到可见视口**——滚出视口的列把手整段不画、半截的裁掉一边。
    const colX = computeColX(node, tableEl);
    const colBlocks = boundariesToBlocks(getCleanColBoundaries(node));
    for (const blk of colBlocks) {
      let x0 = colX[blk[0]]; let x1 = colX[blk[1]];
      if (x0 == null || x1 == null) continue;
      x0 = Math.max(x0, vp.left); x1 = Math.min(x1, vp.right);
      if (x1 - x0 <= 1) continue; // 整段在视口外
      const h = makeHandle('col', blk);
      h.style.left = `${x0}px`;
      h.style.top = `${tRect.top - THICK}px`;
      h.style.width = `${x1 - x0}px`;
      h.style.height = `${THICK}px`;
      layer.appendChild(h); handles.push(h);
    }
  }

  // 拖拽某把手：实时找最近的「干净落点边界」画指示线，松手时移动整块。坐标全是视口坐标。
  // ★每帧重算边界（而非起拖时算一次）：宽表横向自动滚动后列位置会变，落点线须跟着动。
  function startDrag(e: MouseEvent, axis: 'row' | 'col', block: [number, number]) {
    if (e.button !== 0) return;
    e.preventDefault(); e.stopPropagation();
    const v = getView(); if (!v || pos == null) return;
    const node = v.state.doc.nodeAt(pos); if (!node || node.type.name !== 'table') return;
    const tableEl = getInnerTable(v, pos); if (!tableEl) return;
    const scrollEl = getScrollEl(v, pos);
    dragging = true;
    document.body.style.userSelect = 'none';
    const map = TableMap.get(node);
    const clean = axis === 'row' ? getCleanRowBoundaries(node) : getCleanColBoundaries(node);
    const [from, end] = block; // 块占 [from,end)
    let target = from;
    let lastX = e.clientX, lastY = e.clientY;
    let autoRAF = 0;

    // 按当前（可能已滚动后的）rect 重算边界 + 重绘落点线。
    function recompute() {
      const tRect = tableEl!.getBoundingClientRect();
      const boundaryPos = axis === 'row'
        ? computeRowY(tableEl!, map.height)
        : computeColX(node!, tableEl!).map((x) => x ?? tRect.left);
      const coord = axis === 'row' ? lastY : lastX;
      let best = -1, bestD = Infinity;
      for (let b = 0; b < boundaryPos.length; b++) {
        if (!clean[b]) continue;
        const d = Math.abs(boundaryPos[b] - coord);
        if (d < bestD) { bestD = d; best = b; }
      }
      if (best < 0) return;
      target = best;
      const noop = best === from || best === end; // 落回原块边界 → 灰示意
      const vp = (scrollEl ?? tableEl!).getBoundingClientRect();
      dropLine.style.display = 'block';
      if (axis === 'row') {
        // 横线裁剪到可见视口横向范围（宽表滚动时表左缘可能在视口外）
        const lx = Math.max(tRect.left, vp.left), rx = Math.min(tRect.right, vp.right);
        dropLine.className = `mtb-drop-line mtb-drop-h${noop ? ' mtb-drop-noop' : ''}`;
        dropLine.style.left = `${lx}px`; dropLine.style.width = `${Math.max(0, rx - lx)}px`;
        dropLine.style.top = `${boundaryPos[best] - 1}px`; dropLine.style.height = '';
      } else {
        dropLine.className = `mtb-drop-line mtb-drop-v${noop ? ' mtb-drop-noop' : ''}`;
        dropLine.style.top = `${tRect.top}px`; dropLine.style.height = `${tRect.bottom - tRect.top}px`;
        dropLine.style.left = `${boundaryPos[best] - 1}px`; dropLine.style.width = '';
      }
    }

    // 列拖拽时把手贴近视口左右边缘 → 自动横向滚动表格（修「拖到边缘表格不自动滚」）。行拖拽不涉及内部滚动。
    // ★速度按「指针扎进边缘区的深浅」给（-1..1，符号=方向、绝对值=强度）：贴边最快、触发区内边界≈0——
    //   于是把指针停在浅处就几乎不滚、可精确停在中间（修「一下从头滚到尾、停不下」）。
    const EDGE = 36, MAX_SPEED = 9;
    function edgeIntensity(): number {
      if (axis !== 'col' || !scrollEl) return 0;
      const r = scrollEl.getBoundingClientRect();
      const maxScroll = scrollEl.scrollWidth - scrollEl.clientWidth;
      if (lastX < r.left + EDGE && scrollEl.scrollLeft > 0) return -Math.min(1, (r.left + EDGE - lastX) / EDGE);
      if (lastX > r.right - EDGE && scrollEl.scrollLeft < maxScroll) return Math.min(1, (lastX - (r.right - EDGE)) / EDGE);
      return 0;
    }
    function tickAutoScroll() {
      const t = edgeIntensity();
      if (t === 0) { autoRAF = 0; return; }
      scrollEl!.scrollLeft += t * MAX_SPEED;
      recompute(); // 列已滚动 → 落点线跟着动
      autoRAF = requestAnimationFrame(tickAutoScroll);
    }
    function maybeStartAutoScroll() {
      if (autoRAF === 0 && edgeIntensity() !== 0) autoRAF = requestAnimationFrame(tickAutoScroll);
    }

    const onMove = (ev: MouseEvent) => {
      lastX = ev.clientX; lastY = ev.clientY;
      recompute();
      maybeStartAutoScroll();
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      if (autoRAF) { cancelAnimationFrame(autoRAF); autoRAF = 0; }
      document.body.style.userSelect = '';
      dropLine.style.display = 'none';
      dragging = false;
      const vv = getView();
      if (vv && pos != null && target >= 0) {
        const count = end - from;
        if (axis === 'row') moveTableRowsAt(vv, pos, from, count, target);
        else moveTableColumnsAt(vv, pos, from, count, target);
      }
      requestAnimationFrame(refresh); // 移动后由 onDocChanged 也会刷；这里补一次兜底
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }

  // 整页滚动 / 宽表内部横向滚动时把手要跟着表格走。scroll 不冒泡，用捕获相在 window 上聆听（一网打尽）。rAF 节流。
  let rafPending = false;
  window.addEventListener('scroll', () => {
    if (!enabled || dragging || pos == null || rafPending) return;
    rafPending = true;
    requestAnimationFrame(() => { rafPending = false; refresh(); });
  }, true);

  return {
    isEnabled: () => enabled,
    isDragging: () => dragging,
    setEnabled(on: boolean) { enabled = on; if (!on) hideHandles(); },
    setTable(p: number | null) { pos = p; if (enabled) refresh(); else hideHandles(); },
    refresh,
    hide: hideHandles,
  };
}
