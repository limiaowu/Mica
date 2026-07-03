// 图片右键菜单接线（对标 tableSetup.ts）：菜单本身是 WinUI 原生 MenuFlyout（宿主侧），这里只负责
//   ① 右键点在 .mica-image 上时接管（preventDefault + 选中该图）并把坐标/路径转发给宿主弹原生菜单；
//   ② 收到宿主回传的 editor.imageOp 后，在选中的图片节点上执行（删除 / 左中右对齐）。
// 复制图片 / 复制路径 / 在资源管理器中显示 由宿主侧直接完成（宿主已从菜单请求拿到绝对路径）。
import type { EditorView as ProseEditorView } from '@milkdown/prose/view';
import { NodeSelection } from '@milkdown/prose/state';
import { notify } from '../ipc/bridge';
import { imageAbsPath, imageDisplayUrl, parseCropAttr, computeCenterCrop } from './imageNode';
import type { ImageAttrs } from './imageNode';
import {
  currentImageTarget,
  readTargetAttrs,
  writeTargetAttrs,
  deleteTarget,
  targetDom,
  imageGroupActiveKey,
} from './imageGroup';
import type { ImageTarget } from './imageGroup';

// createEditor 每次重建编辑器后 view 会变，故存「取实时 view」的函数。
let getViewRef: (() => ProseEditorView | null) | null = null;

// 宿主菜单/子 Flyout 控件回传 op，在「右键时已聚焦的图」（独立图或组内激活图）上执行。
// opts.crop：仅 op==='crop' 时携带——裁剪分数串 "L,T,W,H"（裁剪）或 null（还原全图）。
// opts.src：仅 op==='replace' 时携带——宿主落盘后回传的新图存储路径。
// opts.value/opts.key：宽度/圆角/边框这类「带数值」的 op 携带——见下「样式微调」。
//   · op==='preview'（拖动滑块时）：仅改实时 DOM、**不提交**（避免每次 setNodeMarkup 重序列化全文卡顿，
//     沿用原浮条「input 预览 / change 提交」防卡套路）；松手由宿主再发对应 commit op。
//   · op==='width'/'radius'/'border'：提交属性（写回节点）。
//   · op==='flipH'/'flipV'：在当前值上 toggle 镜像。
export interface ImageOpOpts {
  crop?: string | null;
  src?: string | null;
  value?: string | number | null;
  key?: string;
}
export function runImageOp(op: string, opts: ImageOpOpts = {}) {
  const view = getViewRef?.();
  if (!view) return;
  const t = currentImageTarget(view); // 统一目标：独立图 NodeSelection / 组内激活图（右键时已设好）
  if (!t) return;

  if (op === 'replace') {
    replaceImage(t, opts.src ?? null);
    return;
  }
  if (op === 'delete') {
    deleteTarget(view, t, { scroll: true });
    view.focus();
    return;
  }
  if (op === 'crop') {
    // 裁剪只改 crop 属性、**不动 width**（保持「自适应/指定宽度」原状）。wrap 不塌缩由 NodeView 保证：
    // 裁剪且 width 为空时，加载后按「裁剪区自然像素宽」补 wrap 宽（见 imageNode.ts createImageNodeView）。
    writeTargetAttrs(view, t, { crop: opts.crop ?? null }, { scroll: true }); // 选回同图，便于连续调整/再裁
    view.focus();
    return;
  }
  if (op === 'alignLeft' || op === 'alignCenter' || op === 'alignRight') {
    // 居中 = 默认观感 → 存 align:null（保持干净 Markdown）；左/右才写 align（→ 序列化成带 data-align 的 <img> HTML）。
    // 注：组内图对齐无意义（大小由 flex 决定），但 writeTargetAttrs 会忠实写入；右键菜单对组内图不出对齐项即可。
    const align = op === 'alignLeft' ? 'left' : op === 'alignRight' ? 'right' : null;
    writeTargetAttrs(view, t, { align }, { scroll: true });
    view.focus();
    return;
  }

  // ---- 样式微调（宽度/圆角/边框/翻转）：从浮条迁入右键菜单的子 Flyout 控件 ----
  // 拖动预览：只改实时 DOM、不提交（防卡）。逻辑从原 imageToolbar 的即时预览原样搬来。
  if (op === 'preview') {
    const d = targetDom(view, t);
    if (!d) return;
    const key = opts.key;
    if (key === 'width') {
      d.wrap.style.width = (opts.value as string) || ''; // '' = 自适应
    } else if (key === 'radius') {
      const v = Number(opts.value);
      d.img.style.borderRadius = `${v}px`;
      d.wrap.style.borderRadius = `${Math.max(v, 2)}px`; // 选中框圆角钳到 ≥2，与提交后 render 一致（防回弹闪烁）
    } else if (key === 'border') {
      const v = Number(opts.value);
      const val = v === 0 ? '' : `${v}px solid`;
      // 裁剪图边框挂 wrap（img 被放大裁切、挂 img 会被裁没）；普通图挂 img。
      if (d.wrap.classList.contains('mica-cropped')) { d.wrap.style.border = val; d.img.style.border = ''; }
      else { d.img.style.border = val; d.wrap.style.border = ''; }
    }
    return;
  }
  if (op === 'width') {
    writeTargetAttrs(view, t, { width: (opts.value as string) || null }); // '' / null = 自适应
    return;
  }
  if (op === 'radius') {
    writeTargetAttrs(view, t, { radius: Number(opts.value) }); // 0 = 直角（仍算带样式）
    return;
  }
  if (op === 'border') {
    const v = Number(opts.value);
    writeTargetAttrs(view, t, { border: v === 0 ? null : v }); // 0 = 无边框（回到干净 Markdown）
    return;
  }
  if (op === 'flipH' || op === 'flipV') {
    const a = readTargetAttrs(view, t);
    if (!a) return;
    writeTargetAttrs(view, t, op === 'flipH' ? { flipH: !a.flipH } : { flipV: !a.flipV });
    return;
  }
}

// 替换图片：宿主选好新图落盘后回传 src。换 src 时**保持原显示框比例**——若旧图有显式宽度或裁剪
// （= 用户刻意定的框），按旧框有效比例对新图居中裁剪（绝不放大，只取子区域）；普通自适应图则直接换 src、
// 不裁（显示新图本身，避免无谓裁切）。独立图/组内图统一走 ImageTarget。
function replaceImage(t: ImageTarget, src: string | null) {
  const view = getViewRef?.();
  if (!view || !src) return;
  const a = readTargetAttrs(view, t);
  if (!a) return;
  const hasFrame = a.width != null || a.crop != null;

  // 提交：在目标图上换 src + crop（重新选回同图，浮条不消失、可继续调）。
  const commit = (crop: string | null) => {
    const v = getViewRef?.(); if (!v) return;
    writeTargetAttrs(v, t, { src, crop } as Partial<ImageAttrs>, { scroll: true });
    v.focus();
  };

  if (!hasFrame) { commit(a.crop ?? null); return; } // 自适应普通图：直接换、不裁

  // 旧框有效显示比例 = 旧图自然尺寸（取自实时 DOM）× 旧裁剪分数。
  const dom = targetDom(view, t);
  const onw = dom?.img.naturalWidth ?? 0, onh = dom?.img.naturalHeight ?? 0;
  const oc = parseCropAttr(a.crop);
  const effW = oc ? oc.W * onw : onw;
  const effH = oc ? oc.H * onh : onh;
  const targetAspect = effW > 0 && effH > 0 ? effW / effH : 0;
  if (targetAspect <= 0) { commit(null); return; } // 拿不到旧比例 → 退化为直接换（不裁）

  // 加载新图拿自然尺寸 → 按旧框比例居中裁剪（比例已≈一致则 computeCenterCrop 返回 null，不裁）。
  const probe = new Image();
  probe.onload = () => commit(computeCenterCrop(probe.naturalWidth, probe.naturalHeight, targetAspect));
  probe.onerror = () => commit(null);
  probe.src = imageDisplayUrl(src);
}

// 从右键事件定位被点的图片节点位置（-1 表示没点在图片上）。优先用 DOM 反查，再退回坐标命中。
function imagePosFromEvent(view: ProseEditorView, imgEl: HTMLElement, e: MouseEvent): number {
  try {
    const p = view.posAtDOM(imgEl, 0);
    if (view.state.doc.nodeAt(p)?.type.name === 'image') return p;
    if (p > 0 && view.state.doc.nodeAt(p - 1)?.type.name === 'image') return p - 1;
  } catch {
    /* posAtDOM 偶发抛错 → 退回坐标命中 */
  }
  const found = view.posAtCoords({ left: e.clientX, top: e.clientY });
  if (found) {
    const $pos = view.state.doc.resolve(found.pos);
    if ($pos.nodeAfter?.type.name === 'image') return found.pos;
    if ($pos.nodeBefore?.type.name === 'image') return found.pos - $pos.nodeBefore.nodeSize;
  }
  return -1;
}

// 扫描文档找出某个 imageGroup NodeView 外层 DOM 对应的节点位置（nodeDOM 全等比较，最稳）。
function groupPosFromDom(view: ProseEditorView, groupEl: HTMLElement): number {
  let result = -1;
  view.state.doc.descendants((node, pos) => {
    if (result >= 0) return false;
    if (node.type.name === 'imageGroup') {
      if (view.nodeDOM(pos) === groupEl) { result = pos; return false; }
      return false; // 行内原子，无需深入
    }
    return undefined; // 其余（段落等）继续下钻
  });
  return result;
}

// 给编辑器容器挂右键监听。仅当右键点在图片上时接管，否则放行（交给表格菜单/默认右键）。
export function setupImageContextMenu(container: HTMLElement, getView: () => ProseEditorView | null) {
  getViewRef = getView;
  container.addEventListener('contextmenu', (e) => {
    const view = getView();
    if (!view) return;
    const imgEl = (e.target as HTMLElement | null)?.closest?.('img.mica-image') as HTMLElement | null;
    if (!imgEl) return; // 不是图片：放行

    // 坐标用 WebView 视口 CSS 像素（≈ EditorHost 内 DIP）；abs 为磁盘绝对路径（网络/base64 图为空串）；
    // crop 让宿主裁剪对话框预选上次的裁剪框（可在原图上重裁）；grouped=true 时宿主菜单隐藏「对齐/大小」（组内图大小由 flex 决定）。
    // width/radius/border/flipH/flipV 一并带上，供宿主菜单的「大小/圆角/边框」子 Flyout 滑块回填当前值、翻转项打勾。
    const fireMenu = (a: ImageAttrs, grouped: boolean) =>
      notify('host.imageMenu', {
        x: e.clientX, y: e.clientY,
        src: a.src ?? '', abs: imageAbsPath(a.src ?? ''), crop: a.crop ?? '', grouped,
        width: a.width ?? '', radius: a.radius, border: a.border, flipH: !!a.flipH, flipV: !!a.flipV,
      });

    // 组内图：img 在 .mica-image-group 容器 NodeView 内。选中容器 + 设激活 index，让 runImageOp 命中该子图。
    const groupEl = imgEl.closest('.mica-image-group') as HTMLElement | null;
    const wrapEl = imgEl.closest('.mica-image-wrap') as HTMLElement | null;
    if (groupEl && wrapEl) {
      const gpos = groupPosFromDom(view, groupEl);
      if (gpos < 0) return;
      const index = Number(wrapEl.dataset.index);
      const gnode = view.state.doc.nodeAt(gpos);
      const a = ((gnode?.attrs.images as ImageAttrs[]) || [])[index];
      if (!a) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      view.dispatch(
        view.state.tr
          .setSelection(NodeSelection.create(view.state.doc, gpos))
          .setMeta(imageGroupActiveKey, { pos: gpos, index }),
      );
      fireMenu(a, true);
      return;
    }

    // 独立图。
    const pos = imagePosFromEvent(view, imgEl, e);
    if (pos < 0) return;
    e.preventDefault();
    e.stopImmediatePropagation(); // 抢在表格右键监听之前（图片可能嵌在表格单元格里），让图片菜单优先
    view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, pos)));
    const node = view.state.doc.nodeAt(pos);
    if (node) fireMenu(node.attrs as unknown as ImageAttrs, false);
  });
}
