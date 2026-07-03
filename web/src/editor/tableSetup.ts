// 表格右键菜单接线：菜单本身是 WinUI 原生 MenuFlyout（宿主侧），这里只负责
// ① 右键点在表格内时接管（preventDefault + 落选区）并把坐标/上下文转发给宿主弹原生菜单；
// ② 收到宿主回传的 editor.tableOp 后，在当前选区上执行对应命令（实际命令在 htmlTable.ts 的 runTableOp）。
import type { EditorView as ProseEditorView } from '@milkdown/prose/view';
import { TextSelection } from '@milkdown/prose/state';
import { CellSelection, isInTable } from '@milkdown/prose/tables';
import { runTableOp as runOp, getTableInfoAt } from './htmlTable';
import { toggleTableReorder, isTableReorderOn } from './tableToolbar';
import { notify } from '../ipc/bridge';

// createEditor 每次重建编辑器后 view 会变，故存「取实时 view」的函数。
let getViewRef: (() => ProseEditorView | null) | null = null;

// 宿主菜单项点击后回传 op（颜色类 op 带十六进制色 value），在「右键时已落好的选区」上执行（命令实现见 htmlTable.runTableOp）。
export function runTableOp(op: string, value?: string | null) {
  const view = getViewRef?.();
  if (!view) return;
  // 「排序」开关不是表格内容命令，单独路由到 reorder 控制器（在 tableToolbar.ts）。
  if (op === 'tableReorderToggle') { toggleTableReorder(view); return; }
  runOp(view, op, value);
}

// 宿主菜单「插入表格」点击 → 经 editor.checkInsertTable 问这里：光标在表格里则不能嵌套。
// 不在表格 → 回 host.shortcut insertTable 弹启动器；在表格 → 什么都不做（对标 Typora，无任何提示）。
export function checkInsertTable() {
  const view = getViewRef?.();
  if (view && isInTable(view.state)) return;
  notify('host.shortcut', { action: 'insertTable' });
}

// 给编辑器容器挂右键监听。仅当右键点在表格内时接管，否则放行默认行为。getView 返回当前 ProseMirror view。
export function setupTableContextMenu(container: HTMLElement, getView: () => ProseEditorView | null) {
  getViewRef = getView;
  // 右键的 mousedown(button 2) 会先于 contextmenu 触发，ProseMirror 默认会据此把跨格选区收回到点中的单元格，
  // 等 contextmenu 时选区已没了、合并不了。故在「捕获阶段」抢先拦截：已有跨格 CellSelection 时阻止该 mousedown
  // 冒泡到 PM（capture + stopPropagation 让 PM 的监听器收不到），从而保住选区，鼠标移到别的格再右键也能合并。
  container.addEventListener('mousedown', (e) => {
    if (e.button !== 2) return;
    const view = getView();
    if (view && view.state.selection instanceof CellSelection) {
      e.preventDefault();
      e.stopPropagation();
    }
  }, true);
  container.addEventListener('contextmenu', (e) => {
    const view = getView();
    if (!view) return;
    const found = view.posAtCoords({ left: e.clientX, top: e.clientY });
    if (!found) return;
    const $pos = view.state.doc.resolve(found.pos);
    // 向上找：是否在表格里 + 点中的是不是表头单元格（th）+ 表格节点 pos（供取整表信息回填菜单 NumberBox）。
    // 多级表头后行操作都安全，inHeader 仅供宿主参考。
    let inTable = false;
    let inHeader = false;
    let tablePos = -1;
    for (let d = $pos.depth; d > 0; d--) {
      const name = $pos.node(d).type.name;
      if (name === 'table_header') inHeader = true;
      if (name === 'table') { inTable = true; tablePos = $pos.before(d); break; }
    }
    if (!inTable) return; // 不在表格里：放行浏览器/编辑器默认右键

    e.preventDefault();
    // 容错：只要当前已是跨单元格 CellSelection（拖选了多格、准备合并），右键点在表格任意处都**保留**该选区，
    // 不强行把选区收回到右键点中的单元格（修「选完后鼠标稍微移到别的格再右键，多选就没了、合并不了」）。
    // 仅当没有多格选区时，才把选区落到右键点中的单元格，让命令作用在正确位置。
    const sel = view.state.selection;
    const keepCellSel = sel instanceof CellSelection;
    if (!keepCellSel) {
      view.dispatch(view.state.tr.setSelection(TextSelection.near(view.state.doc.resolve(found.pos))));
    }
    // 让宿主弹原生 CommandBarFlyout（坐标用 WebView 视口 CSS 像素，≈ EditorHost 内 DIP）。
    // 一并带整表信息：行/列数、样式预设、圆角、线宽、位置态（left/center/right/full）——供菜单的「整表对齐/样式/
    // 圆角/线宽/行列」子项回填当前值（思路同图片菜单 host.imageMenu）。
    const info = tablePos >= 0 ? getTableInfoAt(view, tablePos) : null;
    const pos = info ? (info.width === 'full' ? 'full' : (info.align ?? 'left')) : 'left';
    notify('host.tableMenu', {
      x: e.clientX, y: e.clientY, inHeader,
      rows: info?.rows ?? 0, cols: info?.cols ?? 0,
      preset: info?.preset ?? '', radius: info?.radius ?? null, border: info?.border ?? null, pos,
      reorderOn: isTableReorderOn(),
    });
  });
}
