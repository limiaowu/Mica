import { createEditor, setActiveNote, executeCommand, saveNow, setSourceMode, scrollToHeading, revealNode, setEditorConfig, insertTable, repositionTableToolbar, placeImageSrcs, placeImageSrcsAt, insertEmptyGallery } from './editor/createEditor';
import { setTableOverlayDark } from './editor/tableToolbar';
import { runTableOp, checkInsertTable } from './editor/tableSetup';
import { runImageOp } from './editor/imageSetup';
import type { EditorConfig } from './editor/createEditor';
import { on, notify } from './ipc/bridge';
import type { EditorLoadParams, ThemeUpdateParams } from './ipc/protocol';
import 'katex/dist/katex.min.css';   // 公式渲染样式（KaTeX）
import './styles/editor.css';

const root = document.getElementById('editor')!;

// 记住每个笔记的滚动位置：切标签会重新读盘+重渲染，默认回到顶部。这里按「绝对路径」存滚动量，
// 切走时存、切回时恢复。全应用共用同一个 WebView，这个内存表跨标签切换一直在，无需 IPC。
const scrollPositions = new Map<string, number>();
let loadedPath: string | null = null;
const scroller = () => document.scrollingElement ?? document.documentElement;

// 「维护页定位」配套：editor.reveal 可能在 editor.load 还在渲染时到达（宿主先发 load 再发 reveal）。
// 加载中先暂存，等本次 createEditor 渲染完再执行，避免命中旧文档的同序号节点。
let loadInFlight = false;
let pendingReveal: { kind: string; index: number } | null = null;
// 抑制「本次 load 的滚动位置恢复」。**承重坑**：宿主定位时先发 load 后发 reveal，但 createEditor 若只经
// 微任务就 resolve（不让出宏任务），load 处理函数会在 reveal 消息任务之前**整体跑完**——此时 pendingReveal
// 还是 null，于是排了一次「恢复到 0」的 rAF；紧接着 reveal 跑起来开始定位滚动，那次迟到的恢复又把 scrollTop
// 砸回 0、还被定位的「用户手动滚动」守卫误判成用户在滚 → 停在开头（正是「只有某些文件 / 第一次定位失败、
// 回去再点就好」的真凶）。故 reveal 接管滚动时置此标志，让那次排队中的恢复自我跳过——不依赖 load/reveal 的先后。
let suppressRestore = false;

on('editor.load', async (params) => {
  const { relPath, body } = params as unknown as EditorLoadParams;
  // 存下即将离开的笔记的滚动位置
  if (loadedPath) scrollPositions.set(loadedPath, scroller().scrollTop);
  loadInFlight = true;
  suppressRestore = false; // 每次 load 重置：上一次定位的抑制不该影响这次正常切换的恢复
  setActiveNote(relPath);
  await createEditor(root, body, relPath);
  loadedPath = relPath || null;
  loadInFlight = false;
  if (pendingReveal) {
    // 维护页定位（reveal 在 load 渲染期间就到了）：本就要滚到某节点，跳过恢复，直接定位。
    const r = pendingReveal;
    pendingReveal = null;
    revealNode(r.kind, r.index);
  } else {
    // 渲染完成后恢复目标笔记的滚动位置（双 rAF 等布局稳定；新笔记默认 0）。
    // 若在排队期间来了 reveal（见 suppressRestore），由定位接管滚动、这次恢复自我跳过。
    const target = relPath ? (scrollPositions.get(relPath) ?? 0) : 0;
    requestAnimationFrame(() => requestAnimationFrame(() => {
      if (suppressRestore) { suppressRestore = false; return; }
      scroller().scrollTop = target;
    }));
  }
});

// 维护页「定位」：滚动到第 index 个 kind 类节点并高亮。若正加载，延后到 load 完成（见上）。
on('editor.reveal', (params) => {
  const r = params as unknown as { kind: string; index: number };
  if (loadInFlight) {
    pendingReveal = r;
  } else {
    // load 已跑完，它的滚动恢复可能正排在 rAF 队列里等执行 → 抑制它，由定位独占滚动。
    suppressRestore = true;
    revealNode(r.kind, r.index);
  }
});

on('theme.update', (params) => {
  const { vars, mode } = params as unknown as ThemeUpdateParams;
  const docRoot = document.documentElement;
  for (const [key, value] of Object.entries(vars)) {
    docRoot.style.setProperty(key, value);
  }
  document.body.classList.toggle('dark', mode === 'dark');
  // 表格浮层在 body 外（逃离 zoom），暗色变量靠它自带 .dark 命中——同步过去。
  setTableOverlayDark(mode === 'dark');
});

on('editor.focus', () => {
  const editor = root.querySelector('.ProseMirror') as HTMLElement | null;
  editor?.focus();
});

on('editor.command', (params) => {
  const { command } = params as { command: string };
  executeCommand(command);
});

on('editor.insertTable', (params) => {
  // 宿主 WinUI 启动器直接生成 <table> HTML（含合并/样式），这里解析插入。
  insertTable((params as { html?: string }).html ?? '');
});

on('editor.checkInsertTable', () => {
  // 宿主菜单「插入表格」点击 → 检查光标是否在表格里（不能嵌套），回 host.shortcut。
  checkInsertTable();
});

on('editor.tableOp', (params) => {
  const p = params as { op: string; value?: string | null };
  runTableOp(p.op, p.value);
});

on('editor.imageOp', (params) => {
  const p = params as { op: string; crop?: string | null; src?: string | null; value?: string | number | null; key?: string };
  runImageOp(p.op, { crop: p.crop, src: p.src, value: p.value, key: p.key });
});

// 宿主「插入图片」（多选文件落盘后）→ 按 placeImageSrcs 落点（选中图册则追加、否则单/多张插入）。
on('editor.insertImages', (params) => {
  placeImageSrcs((params as { srcs?: string[] }).srcs ?? []);
});

// 宿主「外部文件拖入」（资源管理器拖图，EditorHostView 接 XAML Drop 落盘后）→ 按落点视口坐标(x,y)放置。
on('editor.dropImages', (params) => {
  const p = params as { srcs?: string[]; x?: number; y?: number };
  placeImageSrcsAt(p.srcs ?? [], p.x ?? 0, p.y ?? 0);
});

// 宿主「新建图册」→ 在选区插入空图册（占位态）。
on('editor.newGallery', () => {
  insertEmptyGallery();
});

on('editor.toggleSource', (params) => {
  const { enabled } = params as { enabled: boolean };
  setSourceMode(enabled);
});

on('editor.save', () => {
  saveNow();
});

on('editor.scrollTo', (params) => {
  const { index } = params as { index: number };
  scrollToHeading(index);
});

on('editor.config', (params) => {
  setEditorConfig(params as unknown as EditorConfig);
});

// ===== 缩放（整体功能，非按页/按笔记）=====
// 用 CSS `zoom` 缩放整个编辑器内容；Ctrl+滚轮 / Ctrl+Shift+加减 / Ctrl+Shift+0 重置。
// WebView2 自带缩放已在宿主关掉（IsZoomControlEnabled=false），由这里完全接管，
// 以便提供「重置 / 状态栏显示百分比 / 点击直接设 / 开关 Ctrl+滚轮」。当前比例上报宿主状态栏。
const ZOOM_MIN = 0.5;
const ZOOM_MAX = 3;
let zoomFactor = 1;
let zoomWheelEnabled = true; // 是否允许 Ctrl+滚轮缩放（宿主设置可关）

function applyZoom() {
  // body 上的 CSS zoom：内容整体缩放，滚动区自适应、无横向溢出（已验证）
  (document.body.style as CSSStyleDeclaration & { zoom: string }).zoom = String(zoomFactor);
  notify('editor.zoom', { factor: zoomFactor }); // 上报状态栏
  // zoom 改了要重新摆放表格浮动工具条/把手（否则停旧坐标被缩放跑偏）。等本帧布局生效后再量。
  requestAnimationFrame(() => repositionTableToolbar());
}

function setZoom(factor: number) {
  const clamped = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, Math.round(factor * 100) / 100));
  if (clamped === zoomFactor) { notify('editor.zoom', { factor: zoomFactor }); return; }
  zoomFactor = clamped;
  applyZoom();
}

// Ctrl+滚轮缩放
window.addEventListener('wheel', (e) => {
  if (!zoomWheelEnabled || !(e.ctrlKey || e.metaKey)) return;
  e.preventDefault();
  setZoom(zoomFactor + (e.deltaY < 0 ? 0.1 : -0.1));
}, { passive: false });

// 宿主推送缩放设置：初始比例 + 是否启用滚轮缩放（WebReady / 设置变更 / 状态栏点选时）
on('editor.zoomConfig', (params) => {
  const p = params as { factor?: number; wheelEnabled?: boolean };
  if (typeof p.wheelEnabled === 'boolean') zoomWheelEnabled = p.wheelEnabled;
  if (typeof p.factor === 'number') setZoom(p.factor);
});

// app 级快捷键转发：编辑器（WebView2）持有焦点时，宿主顶部菜单的 XAML 快捷键收不到按键，
// 故在 web 端捕获后通过 IPC（host.shortcut）转发给宿主执行。焦点不在编辑器时（如目录树），
// 宿主的 XAML accelerator 本就能触发、web 收不到键，故不会重复。编辑器内容类快捷键
// （Ctrl+1~6 标题 / Ctrl+B/I 等）由 Milkdown/ProseMirror 自己处理，不在此列。
window.addEventListener('keydown', (e) => {
  const mod = e.ctrlKey || e.metaKey;
  // 缩放快捷键（web 端自理，不走宿主）：放大 Ctrl+Shift+= / 缩小 Ctrl+Shift+- / 重置 Ctrl+Shift+0。
  // 不用 Ctrl+加减/Ctrl+0，因为 Ctrl+0 是「正文」、且避免与浏览器缩放语义混淆。
  if (mod && e.shiftKey && (e.code === 'Equal' || e.key === '+' || e.key === '=')) { e.preventDefault(); setZoom(zoomFactor + 0.1); return; }
  if (mod && e.shiftKey && (e.code === 'Minus' || e.key === '_' || e.key === '-')) { e.preventDefault(); setZoom(zoomFactor - 0.1); return; }
  // 重置：Ctrl+Shift+0。注意：本段逻辑已实测正确（Ctrl+Alt+0 经同一处理器能正常重置），
  // 但部分机器上 Ctrl+Shift+0 在到达网页前被系统层吞掉（疑似输入法/系统占用，非本代码问题），
  // 此时改用状态栏「100%」点击或视图菜单「实际大小」重置。
  if (mod && e.shiftKey && (e.code === 'Digit0' || e.key === ')' || e.key === '0')) { e.preventDefault(); setZoom(1); return; }
  let action: string | null = null;
  if (mod && e.shiftKey && e.code === 'KeyE') action = 'toggleSidebar';
  else if (mod && e.shiftKey && e.code === 'KeyU') action = 'toggleSource';
  else if (mod && !e.shiftKey && e.code === 'KeyS') action = 'save';
  else if (mod && !e.shiftKey && e.code === 'KeyW') action = 'closeTab';
  else if (mod && !e.shiftKey && e.code === 'KeyN') action = 'newNote';
  else if (mod && !e.shiftKey && e.code === 'KeyO') action = 'openFolder';
  else if (mod && e.shiftKey && e.code === 'KeyI') action = 'insertImage';   // 插入图片（格式▸图像）
  else if (mod && e.shiftKey && e.code === 'KeyG') action = 'newGallery';    // 新建图册（格式▸图像）
  else if (e.key === 'F11') action = 'fullscreen';
  if (action) {
    e.preventDefault();
    notify('host.shortcut', { action });
  }
});

console.log('Mica editor initialized');
