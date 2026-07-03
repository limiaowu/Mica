// 表格非缩放浮层 + 行/列拖动重排管理（2026-06：浮动工具条已下线，整表样式/对齐/圆角/线宽/行列/排序全部
// 迁回宿主右键菜单 CommandBarFlyout，见 MainWindow.Table.cs / docs）。本文件只剩两件事：
//   ① 维护一个 body 外的非缩放浮层（reorder 把手挂它，逃离 body 的 CSS zoom，坐标全用视口坐标、无 z 换算）；
//   ② 包一层 reorder 控制器（setupTableReorder），并把「开关 / 随光标跟表 / 文档变化刷新」暴露给上层。
// reorder 开关由右键菜单「排序」项触发：editor.tableOp{op:'tableReorderToggle'} → tableSetup.runTableOp → toggleTableReorder。
import type { EditorView as ProseEditorView } from '@milkdown/prose/view';
import type { Selection } from '@milkdown/prose/state';
import { findTable } from '@milkdown/prose/tables';
import { setupTableReorder } from './tableReorder';

// ===== 非缩放浮层（body 外，逃离 zoom）=====
// 整个应用共享一个浮层，承载 reorder 把手层。挂在 documentElement 上、position:fixed、铺满视口、
// pointer-events:none（只有里面的把手能点）。单例、跨文件复用。
let overlayEl: HTMLDivElement | null = null;
function getOverlay(): HTMLDivElement {
  if (overlayEl && overlayEl.isConnected) return overlayEl;
  overlayEl = document.createElement('div');
  overlayEl.className = 'mica-table-overlay';
  // 暗色变量定义在 body.dark 上；本层在 body 外收不到，故自带 .dark（初始跟随当前 body）。
  if (document.body.classList.contains('dark')) overlayEl.classList.add('dark');
  document.documentElement.appendChild(overlayEl);
  return overlayEl;
}
// 由 main.ts theme.update 调：把暗色状态同步到浮层（否则浮层始终亮色配色）。
export function setTableOverlayDark(on: boolean) { getOverlay().classList.toggle('dark', on); }
// 供其它浮动 UI 复用同一个 body 外非缩放浮层（逃离 zoom、自带 .dark）。当前仅 reorder 用。
export function getEditorOverlay(): HTMLDivElement { return getOverlay(); }

// ===== reorder 管理器 =====
// createEditor 每次切文件都调 setupTableToolbar（保留旧名以少改接线）；控制器/监听只建一次，之后复用，重入只 reset。
let built: {
  onDocChanged: () => void;
  onSelectionChanged: (sel?: Selection) => void;
  reposition: () => void;
  reset: () => void;
} | null = null;
// 给右键菜单「排序」开关调用（经 tableSetup.runTableOp 路由）。
let toggleRef: ((view: ProseEditorView) => void) | null = null;
let isOnRef: (() => boolean) | null = null;

export function setupTableToolbar(_container: HTMLElement, getView: () => ProseEditorView | null) {
  if (built) { built.reset(); return built; }
  const overlay = getOverlay();
  const reorder = setupTableReorder(overlay, getView);
  let enabled = false;
  let pos: number | null = null;

  // 当前光标所在表格 pos（无则 null）。必须用传入的 sel（milkdown selectionUpdated 慢一拍，见旧注释/记忆）。
  function caretTablePos(sel?: Selection): number | null {
    const v = getView();
    if (!v) return null;
    const found = findTable((sel ?? v.state.selection).$from);
    return found ? found.pos : null;
  }

  function toggleReorder(view: ProseEditorView) {
    enabled = !enabled;
    reorder.setEnabled(enabled);
    if (enabled) { pos = caretTablePos(); reorder.setTable(pos); }
    else reorder.hide();
  }
  function onSelectionChanged(sel?: Selection) {
    if (!enabled) return;
    const p = caretTablePos(sel);
    if (p !== pos) { pos = p; reorder.setTable(p); } // 光标移到别的表 → 把手跟过去（无表则收起）
  }
  function onDocChanged() { if (enabled) reorder.refresh(); }
  function reposition() { if (enabled) reorder.refresh(); }
  function reset() { enabled = false; reorder.setEnabled(false); reorder.setTable(null); pos = null; }

  toggleRef = toggleReorder;
  isOnRef = () => enabled;
  built = { onDocChanged, onSelectionChanged, reposition, reset };
  return built;
}

// 右键菜单「排序」开关 → 切换当前光标所在表的 reorder 把手。
export function toggleTableReorder(view: ProseEditorView) { toggleRef?.(view); }
// 供 tableSetup 弹菜单时回填「排序」开关的勾选态。
export function isTableReorderOn(): boolean { return isOnRef?.() ?? false; }
