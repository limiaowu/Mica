// 通用 HTML 支持（对标 Typora）：让笔记里手写的 HTML 真正「渲染」而非显示成转义原码。
//
// 背景：Milkdown 自带的 html 节点（commonmark）只把 HTML 当**纯文本**显示（escape 后 textContent），
// 所以 `<p align="center">` 这类只会看到原码。本模块新增两类自定义节点 + 一个 remark 变换，
// 把「我们的图片/表格/图册之外的」剩余 HTML 接管成可渲染节点：
//   · mica_html_block —— 块级 HTML（块 atom），NodeView 渲染 sanitize 后的 HTML。
//   · mica_html_inline —— 行内 HTML（行内 atom），渲染成对且内部纯文本的标签（<u>/<kbd>/<sub>/<sup>/<mark>/<span>…）。
//
// 编辑（对标 Typora）：**单击**节点 → 原地展开一个 **CodeMirror6 行内原码编辑器**（与源码模式同款好看），
// 失焦 / Ctrl+Enter 提交并重渲染，Esc 取消；提交后内容为空则删除该节点。渲染时**不加块级选中框**，像普通网页。
//
// 安全：这是单人本地笔记、渲染的是用户自己写的内容，但仍做**聚焦 sanitize**——剥掉 <script>/<style>/<iframe>
// 等危险标签、on* 事件属性、javascript: URL（编辑器跑在 https://editor.mica.local 源、带 IPC 桥，防脚本逃逸）。
// 不引入 DOMPurify（避免新依赖 + 代码分割 chunk 的双构建问题；后续要更硬可再换）。
//
// remark 顺序：本模块的 micaHtmlRemark **必须在** 图片/表格/图册 remark 之后注册——那三个先把各自的
// HTML（<img>/<table>/<div data-mica-gallery>）retype 成专用类型，剩下 type==='html' 的才是「通用 HTML」。
import { $nodeSchema, $remark } from '@milkdown/utils';
import type { Node as ProseNode } from '@milkdown/prose/model';
import type { EditorView } from '@milkdown/prose/view';
import { Selection } from '@milkdown/prose/state';
import { InputRule } from '@milkdown/prose/inputrules';
import { EditorView as CmView, keymap, drawSelection } from '@codemirror/view';
import { EditorState as CmState } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap } from '@codemirror/commands';
import { markdown, markdownLanguage } from '@codemirror/lang-markdown';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags as t } from '@lezer/highlight';

// ===== sanitize：剥离危险标签/属性 =====
// 整块/整段 HTML 危险标签（连同内容移除）。<style> 一并禁（可污染全应用样式）。
const BLOCKED_TAGS = new Set([
  'script', 'style', 'iframe', 'object', 'embed', 'link', 'meta', 'base',
  'form', 'input', 'button', 'textarea', 'select', 'option', 'noscript',
]);

// 渲染前 sanitize：用 <template> 离屏解析，遍历元素剥危险标签 + on* 事件 + javascript: URL。
export function sanitizeHtml(raw: string): string {
  const tpl = document.createElement('template');
  tpl.innerHTML = raw;
  const els = Array.from(tpl.content.querySelectorAll('*'));
  for (const el of els) {
    const tag = el.tagName.toLowerCase();
    if (BLOCKED_TAGS.has(tag)) {
      el.remove();
      continue;
    }
    for (const attr of Array.from(el.attributes)) {
      const name = attr.name.toLowerCase();
      const val = attr.value;
      if (name.startsWith('on')) {
        el.removeAttribute(attr.name); // 事件处理器一律剥
      } else if ((name === 'href' || name === 'src' || name === 'xlink:href') && /^\s*javascript:/i.test(val)) {
        el.removeAttribute(attr.name); // javascript: 协议
      } else if (name === 'style' && /expression\s*\(|javascript:/i.test(val)) {
        el.removeAttribute(attr.name); // 老 IE expression / 内联 js
      }
    }
  }
  return tpl.innerHTML;
}

function escapeHtmlText(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// 是否「可渲染的完整行内 HTML」＝单个自闭合标签，或开闭同名的成对标签（内部纯文本、无 < >）。
// 用于行内 atom 编辑提交时判断：不满足就降级为纯文本（避免坏 atom）。
function isRenderableInlineHtml(v: string): boolean {
  const s = v.trim();
  if (/^<[a-zA-Z][a-zA-Z0-9-]*(?:\s[^<>]*)?\/>$/.test(s)) return true; // 自闭合
  const m = /^<([a-zA-Z][a-zA-Z0-9-]*)(?:\s[^<>]*)?>([^<>]*)<\/([a-zA-Z][a-zA-Z0-9-]*)>$/.exec(s);
  return !!m && m[1].toLowerCase() === m[3].toLowerCase();        // 成对同名
}

// ===== 锚点（页内跳转目标）识别与渲染 =====
// 用户思路：把锚点做成「可识别的标识」（像行内代码/公式/标题等级那样），有文字显示文字、空的显示 #id，
// 都带锚点图标→用户一眼知道「这里是锚点」；单击进原码编辑、悬浮(title)显示原码（保持可编辑）。
// 船锚图标（用户提供的 anchor.svg，填满 viewBox 无留白）；fill:currentColor 跟随文字色，由 .mica-anchor-icon 给强调色。
const ANCHOR_ICON_SVG =
  '<svg viewBox="0 0 1024 1024" width="12" height="12" fill="currentColor" xmlns="http://www.w3.org/2000/svg">'
  + '<path d="M512 128a85.312 85.312 0 1 0 0 170.688A85.312 85.312 0 0 0 512 128zM341.312 213.312a170.688 170.688 0 1 1 213.376 165.312V448h192v85.312h-192v360.896c133.632-11.392 240.768-76.672 290.24-180.416l-52.48-52.48 172.672-172.672-5.76 110.912c-12.544 242.688-214.592 381.76-447.36 381.76S77.184 842.24 64.64 599.552L58.88 488.64l172.672 172.672-52.48 52.48c49.472 103.744 156.544 168.96 290.24 180.416V533.312h-192V448h192V378.624c-73.6-18.944-128-85.76-128-165.312z"/>'
  + '</svg>';

// 检测：容器内**唯一**元素、带 id/name、无 href，且（为空 或 是 <a>）→ 视为纯锚点。
// 「唯一元素 + 无 href」是为避免误判：带样式的富文本（如 <span id style>文字</span>）保持原渲染、
// 带 href 的是链接（交 linkClickPlugin）。空元素或 <a> 无 href 才是明确的页内跳转锚点。
function findAnchorEl(container: HTMLElement): HTMLElement | null {
  const els = container.querySelectorAll('*');
  if (els.length !== 1) return null;
  const el = els[0] as HTMLElement;
  if (!(el.hasAttribute('id') || el.hasAttribute('name'))) return null;
  if (el.hasAttribute('href')) return null;             // 带 href＝链接，交 linkClickPlugin
  if (el.hasAttribute('style')) return null;            // 带样式＝富文本，保持原渲染、不当锚点
  // 含 id/name、无 href/style 即锚点（用户选定方案：有内文＝内文作 chip 标签、无内文＝显示 #id）。
  return el;
}

// 渲染锚点 chip：链接图标 + 标签（有文字＝文字、空＝#id）。**把 id/name 保留到 chip 上**——
// scrollToAnchor 的 `[id=...]`/`[name=...]` 选择器仍能定位到此处，页内跳转不破。
function renderAnchor(dom: HTMLElement, el: HTMLElement): void {
  const id = el.getAttribute('id') || '';
  const name = el.getAttribute('name') || '';
  const idVal = id || name;
  const text = (el.textContent || '').trim();
  dom.classList.add('mica-anchor-host');
  const chip = document.createElement('span');
  chip.className = 'mica-anchor' + (text ? '' : ' mica-anchor-empty');
  if (idVal) chip.dataset.anchorId = idVal;              // 自绘悬浮框显示 #id（CSS :hover::after，非原生 title）
  if (id) chip.id = id;                                   // 保留为滚动目标
  if (name) chip.setAttribute('name', name);
  // 只显示船锚图标 + 内文；**无内文＝只显示空船锚图标**（不再回退显示 id，用户 #2），id 仅悬浮框里看。
  const labelHtml = text ? `<span class="mica-anchor-label">${escapeHtmlText(text)}</span>` : '';
  chip.innerHTML =
    `<span class="mica-anchor-icon" aria-hidden="true">${ANCHOR_ICON_SVG}</span>`
    + labelHtml;
  dom.innerHTML = '';
  dom.appendChild(chip);
}

// ===== 节点 schema =====
// 块级 HTML：块级 atom（无内部可编辑内容）。可选中/可删；**不可拖**（draggable 会和「单击进编辑」抢 mousedown）。
export const htmlBlockNode = $nodeSchema('mica_html_block', () => ({
  group: 'block',
  atom: true,
  selectable: true,
  draggable: false,
  isolating: true,
  attrs: { value: { default: '' } },
  parseDOM: [{
    tag: 'div[data-mica-html-block]',
    getAttrs: (dom) => ({ value: (dom as HTMLElement).getAttribute('data-value') ?? '' }),
  }],
  // 兜底 toDOM（实际显示由 NodeView 接管；这里给复制/序列化一个可逆形态）。
  toDOM: (node: ProseNode) => ['div', { 'data-mica-html-block': '', 'data-value': node.attrs.value as string }],
  parseMarkdown: {
    match: ({ type }: { type: string }) => type === 'mica_html_block',
    runner: (state: any, node: any, type: any) => {
      state.addNode(type, { value: String(node.value ?? '') });
    },
  },
  toMarkdown: {
    match: (node: ProseNode) => node.type.name === 'mica_html_block',
    runner: (state: any, node: ProseNode) => {
      // 存回原始 HTML 块（remark-stringify 原样输出 html 节点的 value）。
      state.addNode('html', undefined, String(node.attrs.value ?? ''));
    },
  },
}));

// 行内 HTML：行内 atom。
export const htmlInlineNode = $nodeSchema('mica_html_inline', () => ({
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,
  attrs: { value: { default: '' } },
  parseDOM: [{
    tag: 'span[data-mica-html-inline]',
    getAttrs: (dom) => ({ value: (dom as HTMLElement).getAttribute('data-value') ?? '' }),
  }],
  toDOM: (node: ProseNode) => ['span', { 'data-mica-html-inline': '', 'data-value': node.attrs.value as string }],
  parseMarkdown: {
    match: ({ type }: { type: string }) => type === 'mica_html_inline',
    runner: (state: any, node: any, type: any) => {
      state.addNode(type, { value: String(node.value ?? '') });
    },
  },
  toMarkdown: {
    match: (node: ProseNode) => node.type.name === 'mica_html_inline',
    runner: (state: any, node: ProseNode) => {
      state.addNode('html', undefined, String(node.attrs.value ?? ''));
    },
  },
}));

// ===== 折叠块（<details>/<summary>，原生可编辑节点，对标 Typora，完整版）=====
// 结构：mica_details = details_summary（行内可编辑标题）+ block+（富文本正文，可放标题/列表/代码/图片等）。
// NodeView 直接用原生 <details>：summary 是 <details> 的真子节点 → 浏览器原生折叠/展开生效；
// 点「左侧三角区」切换折叠、点标题文字只编辑不折叠（对标 Typora）。
// 序列化成 CommonMark 可往返的规范多行形（<details> 与 <summary> 同块、正文用空行分隔成真 markdown）。
export const detailsSummaryNode = $nodeSchema('details_summary', () => ({
  content: 'inline*',
  defining: true,
  parseDOM: [{ tag: 'summary' }],
  toDOM: () => ['summary', { class: 'mica-details-summary' }, 0],
  parseMarkdown: {
    match: ({ type }: { type: string }) => type === 'details_summary',
    runner: (state: any, node: any, type: any) => {
      state.openNode(type);
      if (node.children?.length) state.next(node.children);
      state.closeNode();
    },
  },
  // 序列化由父 mica_details 接管（拼进 <summary> 字符串），这里空 runner 占位避免「找不到 serializer」报错。
  toMarkdown: {
    match: (node: ProseNode) => node.type.name === 'details_summary',
    runner: () => { /* handled by parent mica_details */ },
  },
}));

export const detailsNode = $nodeSchema('mica_details', () => ({
  group: 'block',
  content: 'details_summary block+',
  defining: true,
  attrs: { open: { default: true } },
  parseDOM: [{
    tag: 'details',
    getAttrs: (dom) => ({ open: (dom as HTMLElement).hasAttribute('open') }),
  }],
  toDOM: (node: ProseNode) => ['details', node.attrs.open ? { open: '', class: 'mica-details' } : { class: 'mica-details' }, 0],
  parseMarkdown: {
    match: ({ type }: { type: string }) => type === 'mica_details',
    runner: (state: any, node: any, type: any) => {
      state.openNode(type, { open: node.open ?? true });
      state.next(node.children);
      state.closeNode();
    },
  },
  toMarkdown: {
    match: (node: ProseNode) => node.type.name === 'mica_details',
    runner: (state: any, node: ProseNode) => {
      const open = node.attrs.open ? ' open' : '';
      const summary = node.childCount > 0 ? node.child(0).textContent : '';
      // <details> 与 <summary> 之间**无空行**＝CommonMark 同一个 HTML 块（一个 html 节点 value），回读时 summary 在开标签同节点里。
      state.addNode('html', undefined, `<details${open}>\n<summary>${escapeHtmlText(summary)}</summary>`);
      // 正文（summary 之后的块）当作普通 mdast 兄弟序列化 → remark-stringify 自动以空行分隔，正是「富正文」所需（与 </details> 也隔空行）。
      if (node.childCount > 1) state.next(node.content.cut(node.child(0).nodeSize));
      state.addNode('html', undefined, '</details>');
    },
  },
}));

// 折叠 NodeView：原生 <details>（contentDOM 即自身，summary+正文都是 PM 子节点）。
export function createDetailsView() {
  return (node: ProseNode, view: EditorView, getPos: GetPos) => {
    const dom = document.createElement('details');
    dom.className = 'mica-details';
    let current = node;
    if (node.attrs.open) dom.open = true;
    // 用户点三角切换 <details> → 写回 open 属性（序列化/撤销一致）。guard 防止 update() 里我们改 dom.open 又回环。
    dom.addEventListener('toggle', () => {
      if (dom.open === current.attrs.open) return;
      const pos = getPos();
      if (typeof pos !== 'number') return;
      view.dispatch(view.state.tr.setNodeMarkup(pos, undefined, { ...current.attrs, open: dom.open }));
    });
    // 点 summary：左侧三角区(<22px) → 放行原生折叠；文字区 → 阻止原生折叠、只编辑标题（光标已在 mousedown 落好）。
    dom.addEventListener('click', (e) => {
      const summary = dom.querySelector(':scope > summary') as HTMLElement | null;
      if (!summary) return;
      const target = e.target as Node;
      if (target !== summary && !summary.contains(target)) return; // 点正文，不干预
      const rect = summary.getBoundingClientRect();
      if ((e.clientX - rect.left) >= 22) e.preventDefault();
    });
    return {
      dom,
      contentDOM: dom,
      // 参数须用 ProseMirror 的联合类型（MutationRecord | selection），否则 strictFunctionTypes 下逆变报错。
      ignoreMutation: (m: MutationRecord | { type: 'selection'; target: Node }) =>
        m.type === 'attributes' && (m as MutationRecord).attributeName === 'open',
      update: (n: ProseNode) => {
        if (n.type.name !== current.type.name) return false;
        current = n;
        if (dom.open !== n.attrs.open) dom.open = n.attrs.open;
        return true;
      },
    };
  };
}

// ===== 行内 CodeMirror6 原码编辑器（块/行内 HTML 共用，对标源码模式好看）=====
type GetPos = () => number | undefined;

// 极简高亮：HTML 标签/属性/字符串用次要色与强调色，正文常色（与源码模式同套 CSS 变量）。
const miniHighlight = HighlightStyle.define([
  { tag: [t.angleBracket, t.tagName, t.processingInstruction], color: 'var(--mica-accent)' },
  { tag: [t.attributeName, t.propertyName], color: 'var(--mica-fg-muted)' },
  { tag: [t.string, t.attributeValue], color: 'var(--cm-string, var(--mica-fg-muted))' },
  { tag: t.comment, color: 'var(--cm-comment, var(--mica-fg-muted))', fontStyle: 'italic' },
]);

const miniTheme = CmView.theme({
  '&': { backgroundColor: 'transparent', color: 'var(--mica-fg)' },
  '.cm-scroller': { fontFamily: 'var(--mica-font-mono)', lineHeight: '1.6', overflow: 'visible' },
  '.cm-content': { padding: '0', caretColor: 'var(--mica-fg)', overflowWrap: 'anywhere' },
  '&.cm-focused': { outline: 'none' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--mica-fg)' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': { backgroundColor: 'rgba(120, 120, 120, 0.3)' },
});

// 在 host 容器里建一个 CM 实例编辑 raw HTML。onCommit/onCancel 由 NodeView 接管去留。
// wrap：是否软换行。**块级用 true**（满宽、长 HTML 自动折行）；**行内务必 false**——行内框是 inline-block、
// 宽度由内容撑（shrink-to-fit），一旦开 lineWrapping，CM 会以为可用宽=box 的 min-width(≈2ch) 而把标签
// 折成「每行一两个字符」，行数爆炸→框被撑成几百 px 高（用户 #1「前面空一大片」的真凶，实为高度爆炸串行）。
// 关行内换行后 CM 用 white-space:pre，框横向 shrink-to-fit 贴合内容、单行显示。
function createMiniHtmlEditor(
  parent: HTMLElement,
  doc: string,
  wrap: boolean,
  cb: { onCommit: (v: string, below?: boolean) => void; onCancel: () => void },
): CmView {
  const view = new CmView({
    parent,
    state: CmState.create({
      doc,
      extensions: [
        history(),
        drawSelection(),
        markdown({ base: markdownLanguage }), // 含内嵌 HTML 高亮（不另引 lang-html）
        ...(wrap ? [CmView.lineWrapping] : []),
        keymap.of([
          { key: 'Mod-Enter', run: () => { cb.onCommit(view.state.doc.toString()); return true; } },
          // Shift+Enter：提交并跳到块下一行（块级专用，#3）。于是 Enter 专注于「块内换行」。
          { key: 'Shift-Enter', run: () => { cb.onCommit(view.state.doc.toString(), true); return true; } },
          { key: 'Escape', run: () => { cb.onCancel(); return true; } },
          ...defaultKeymap,
          ...historyKeymap,
        ]),
        syntaxHighlighting(miniHighlight),
        miniTheme,
        CmView.domEventHandlers({ blur: () => { cb.onCommit(view.state.doc.toString()); return false; } }),
      ],
    }),
  });
  return view;
}

// 模块级：同一时刻只允许一个 HTML 原码编辑器打开。**承重坑**：mousedown 里 preventDefault 会阻止
// 焦点转移→上一个 CM 收不到 blur 而保持展开（用户反馈「点别处还展开」）。故打开新框前主动提交上一个。
let activeHtmlCommit: (() => void) | null = null;

// ===== NodeView：块级 / 行内共用（单击进 CM6 行内编辑、自然渲染、空则删）=====
function createHtmlNodeView(isBlock: boolean) {
  return (node: ProseNode, view: EditorView, getPos: GetPos) => {
    const dom = document.createElement(isBlock ? 'div' : 'span');
    dom.className = isBlock ? 'mica-html-block' : 'mica-html-inline';
    dom.setAttribute(isBlock ? 'data-mica-html-block' : 'data-mica-html-inline', '');
    let current = node;
    let editing = false;
    let cm: CmView | null = null;

    const render = (n: ProseNode) => {
      dom.classList.remove('mica-html-editing');
      dom.classList.remove('mica-anchor-host');
      const raw = String(n.attrs.value ?? '');
      dom.innerHTML = sanitizeHtml(raw);
      // 锚点（带 id/name、无 href/style 的单个元素）→ 渲染成 chip（船锚图标 + 内文），单击编辑、悬浮自绘框看 #id。
      const anchorEl = findAnchorEl(dom);
      if (anchorEl) { renderAnchor(dom, anchorEl); return; }
      // 渲染为空（空串/被 sanitize 清掉）时给可点占位，避免高度塌成 0 点不中。
      if (!dom.textContent?.trim() && !dom.querySelector('img,hr,br,details,iframe')) {
        dom.innerHTML = `<span class="mica-html-empty">${escapeHtmlText(isBlock ? '空 HTML 块，点击编辑' : '⌗')}</span>`;
      }
    };
    render(node);

    const closeCm = () => { if (cm) { try { cm.destroy(); } catch { /* ignore */ } cm = null; } };

    // below：仅块级 Shift+Enter 用——提交后在块下方新建/复用空段并把光标落进去（块内换行交给 Enter）。
    const placeAfter = (tr: import('@milkdown/prose/state').Transaction, pos: number, below: boolean) => {
      // 块级 + below：落到块下一行（无空段则建一个）。
      if (below && isBlock) {
        const para = view.state.schema.nodes.paragraph;
        const after = pos + 1; // 块 atom nodeSize=1
        const next = tr.doc.resolve(Math.min(after, tr.doc.content.size)).nodeAfter;
        if (para && !(next && next.type.name === 'paragraph' && next.content.size === 0)) {
          tr = tr.insert(after, para.create());
        }
        try { tr = tr.setSelection(Selection.near(tr.doc.resolve(Math.min(after + 1, tr.doc.content.size)), 1)); } catch { /* ignore */ }
        return tr;
      }
      // 否则光标落到节点之后（而非把节点留成 NodeSelection→否则会显示选中态/全选）。
      try { tr = tr.setSelection(Selection.near(tr.doc.resolve(Math.min(pos + 1, tr.doc.content.size)), 1)); } catch { /* ignore */ }
      return tr;
    };

    const commit = (save: boolean, valueOverride?: string, below = false) => {
      if (!editing) return;
      const value = save ? (valueOverride ?? cm?.state.doc.toString() ?? String(current.attrs.value ?? '')) : String(current.attrs.value ?? '');
      editing = false;
      activeHtmlCommit = null;             // 同一时刻仅一个在编辑，提交即清
      closeCm();
      const pos = getPos();
      // 保存且内容为空 → 删除该节点（提供「清空即删」的删除路径）。
      if (save && !value.trim() && typeof pos === 'number') {
        const n = view.state.doc.nodeAt(pos);
        view.dispatch(view.state.tr.delete(pos, pos + (n ? n.nodeSize : 1)));
        view.focus();
        return;
      }
      if (save && value !== current.attrs.value && typeof pos === 'number') {
        // 行内 atom：改后若不再是「可渲染的完整行内标签」(成对/自闭合) → 立即降级为纯文本（用户 #3：
        // 编辑时把 sup 删成 sp、或删掉斜杠，应当场变普通文本，而不是留个坏 atom 撑到下次源码往返才变）。
        if (!isBlock && !isRenderableInlineHtml(value)) {
          const n = view.state.doc.nodeAt(pos);
          const end = pos + (n ? n.nodeSize : 1);
          let tr = view.state.tr.replaceWith(pos, end, view.state.schema.text(value));
          const caret = Math.min(pos + value.length, tr.doc.content.size);
          try { tr = tr.setSelection(Selection.near(tr.doc.resolve(caret), 1)); } catch { /* ignore */ }
          view.dispatch(tr.scrollIntoView());
          view.focus();
          return;
        }
        let tr = view.state.tr.setNodeMarkup(pos, undefined, { ...current.attrs, value });
        tr = placeAfter(tr, pos, below);
        view.dispatch(tr);
        view.focus();
        return; // update() 会重渲染
      }
      render(current); // 无变化 / 取消：原地恢复渲染
      // 光标落到节点之后（或 below 时落到下一行），避免节点保持被选中（渲染态不显示任何框）。
      if (typeof pos === 'number') {
        try { view.dispatch(placeAfter(view.state.tr, pos, below)); } catch { /* ignore */ }
      }
      view.focus();
    };

    const openEditor = () => {
      if (editing) return;
      if (activeHtmlCommit) { const prev = activeHtmlCommit; activeHtmlCommit = null; prev(); } // 先收起上一个
      editing = true;
      dom.classList.add('mica-html-editing');
      dom.innerHTML = '';
      cm = createMiniHtmlEditor(dom, String(current.attrs.value ?? ''), isBlock, {
        onCommit: (v, below) => commit(true, v, below),
        onCancel: () => commit(false),
      });
      activeHtmlCommit = () => commit(true);
      requestAnimationFrame(() => { cm?.focus(); });
    };

    // 单击进编辑（对标 Typora）。preventDefault 阻止 PM 落 NodeSelection（不出块级选中框）。
    dom.addEventListener('mousedown', (e) => {
      if (editing) return; // 编辑中点 CM 内部，放行
      // 点 <summary>（折叠标题/三角）→ 放行原生 <details> 折叠，**不进原码编辑**（对标 Typora：
      // 点「点我」永远是展开/收起，只有点内容才编辑）。此时不可 preventDefault，否则浏览器不切换折叠。
      if ((e.target as HTMLElement)?.closest?.('summary')) return;
      // Ctrl/Cmd+点击渲染 HTML 里的链接 → 放行给 PM 的 linkClickPlugin 开系统浏览器（不进原码编辑、
      // 不可 preventDefault/stopPropagation，否则 handleClick 收不到）。普通单击仍进编辑。
      if (((e as MouseEvent).ctrlKey || (e as MouseEvent).metaKey) && (e.target as HTMLElement)?.closest?.('a')) return;
      e.preventDefault();
      e.stopPropagation();
      openEditor();
    });

    return {
      dom,
      ignoreMutation: () => true,          // 内部 DOM（渲染 / CM）自管
      stopEvent: () => editing,            // 编辑中让 CM 自己吃事件
      update: (n: ProseNode) => {
        if (n.type.name !== current.type.name) return false;
        current = n;
        if (!editing) render(n);           // 编辑中不打断
        return true;
      },
      // 渲染态不显示任何选中框（用户 #3）：故 selectNode 不加样式。
      destroy: () => closeCm(),
    };
  };
}

export function createHtmlBlockView() { return createHtmlNodeView(true); }
export function createHtmlInlineView() { return createHtmlNodeView(false); }

// ===== remark：把剩余 html 节点接管成 mica_html_block / mica_html_inline =====
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  open?: boolean; // mica_details 折叠展开态
}

function isHtmlNode(n: MdNode): boolean {
  return n.type === 'html' && typeof n.value === 'string';
}

// 解析单个标签字符串的种类。
function tagInfo(v: string): { kind: 'open' | 'close' | 'self'; name: string } | null {
  const s = v.trim();
  let m = /^<\/([a-zA-Z][a-zA-Z0-9-]*)\s*>$/.exec(s);
  if (m) return { kind: 'close', name: m[1].toLowerCase() };
  m = /^<([a-zA-Z][a-zA-Z0-9-]*)\b[^>]*\/>$/.exec(s);
  if (m) return { kind: 'self', name: m[1].toLowerCase() };
  m = /^<([a-zA-Z][a-zA-Z0-9-]*)\b[^>]*>$/.exec(s);
  if (m) return { kind: 'open', name: m[1].toLowerCase() };
  return null; // 多标签/不完整片段 → 不认（留原样）
}

// 段落子节点里：把「成对开闭标签 + 内部纯文本」合并成单个 mica_html_inline；无法配对的**降级为纯文本**。
// 配不上（混入其它行内节点、找不到闭合、不完整片段）的行内 html 节点若留作 type:'html'，Milkdown 行内放不下
// 会**静默丢内容**（用户「不成对标签切回渲染就消失」）。故收尾把残留 html 节点统一转成 text——既不丢用户输入、
// 又显示原码可继续编辑（序列化时 `<` 会被 remark 转义成 `\<`，仍可逆，下次加载原样回来）。
function coalesceInline(children: MdNode[]): MdNode[] {
  const out: MdNode[] = [];
  let i = 0;
  while (i < children.length) {
    const c = children[i];
    if (isHtmlNode(c)) {
      // <br> / <br/> / </br> 各种写法统一归一成稳定的行内 atom `<br/>`（否则 <br>/</br> 配不上对、
      // 仍是 commonmark html 节点，切源码再回来会丢失换行，用户 #6）。
      if (/^<\/?br\s*\/?>$/i.test(c.value!.trim())) {
        out.push({ type: 'mica_html_inline', value: '<br/>' });
        i++;
        continue;
      }
      const info = tagInfo(c.value!);
      if (info?.kind === 'self') {
        out.push({ type: 'mica_html_inline', value: c.value });
        i++;
        continue;
      }
      if (info?.kind === 'open') {
        let depth = 1;
        let j = i + 1;
        const inner: MdNode[] = [];
        let textOnly = true;
        for (; j < children.length; j++) {
          const d = children[j];
          if (isHtmlNode(d)) {
            const di = tagInfo(d.value!);
            if (di?.kind === 'open' && di.name === info.name) { depth++; inner.push(d); continue; }
            if (di?.kind === 'close' && di.name === info.name) { depth--; if (depth === 0) break; inner.push(d); continue; }
            textOnly = false; inner.push(d); continue; // 内部混了别的标签
          }
          if (d.type === 'text') { inner.push(d); continue; }
          textOnly = false; inner.push(d); // 内部混了别的行内节点（强调/链接等）
        }
        if (j < children.length && depth === 0 && textOnly) {
          const innerHtml = inner.map((n) => escapeHtmlText(String(n.value ?? ''))).join('');
          out.push({ type: 'mica_html_inline', value: `${c.value}${innerHtml}${children[j].value}` });
          i = j + 1;
          continue;
        }
        // 配不上 → 原样
        out.push(c);
        i++;
        continue;
      }
    }
    out.push(c);
    i++;
  }
  // 收尾：残留的未配对行内 html 节点 → 纯文本（防 Milkdown 行内丢内容；保留原码可编辑）。
  return out.map((n) => (isHtmlNode(n) ? { type: 'text', value: n.value } : n));
}

// 锚点属性判定：含 id/name、无 href、无 style 才是「纯锚点」（带 href＝链接、带 style＝富文本，都不算）。
function isAnchorAttrs(attrs: string): boolean {
  return /\b(?:id|name)\s*=/.test(attrs) && !/\bhref\s*=/.test(attrs) && !/\bstyle\s*=/.test(attrs);
}

// 把「块级 HTML = 单个锚点元素（可带后随文字）」拆成行内锚点 + 后随文字（用户：锚点必须是行内、不该是块）。
// 用户选定方案：**锚点内的文字＝chip 标签**（有内文显示内文、无内文显示 #id），不再把 div 内文强制挪到外面。
// · <a id>文字</a> / <div id>文字</div> 一视同仁 → 整个标签作锚点（renderAnchor 用其 textContent 当标签）。
// · 后随文字（标签之后的部分）仍作普通文本跟在 chip 后。
// 配不上（多元素/含嵌套/非锚点）→ 返回 null，仍按块级 mica_html_block 处理。
function splitInlineAnchor(value: string): { anchorHtml: string; trailing: string } | null {
  const s = value.trim();
  // 自闭合：<tag .../>
  let m = /^<([a-zA-Z][a-zA-Z0-9-]*)\b([^<>]*?)\/>/.exec(s);
  if (m) {
    if (!isAnchorAttrs(m[2])) return null;
    return { anchorHtml: m[0], trailing: s.slice(m[0].length).trim() };
  }
  // 成对：<tag attrs>inner</tag>（inner 仅纯文本、无 < >）
  m = /^<([a-zA-Z][a-zA-Z0-9-]*)\b([^<>]*)>([^<>]*)<\/\1>/.exec(s);
  if (!m) return null;
  const [whole, , attrs] = m;
  if (!isAnchorAttrs(attrs)) return null;
  return { anchorHtml: whole, trailing: s.slice(whole.length).trim() }; // 内文随标签保留，后随文字作普通文本
}

// 块级 HTML → 节点：若是「锚点 + 文字」则降级为行内锚点段落，否则块级 atom。
function emitFromHtmlValue(value: string, out: MdNode[]): void {
  const split = splitInlineAnchor(value);
  if (split) {
    const children: MdNode[] = [{ type: 'mica_html_inline', value: split.anchorHtml }];
    if (split.trailing) children.push({ type: 'text', value: split.trailing });
    out.push({ type: 'paragraph', children });
    return;
  }
  out.push({ type: 'mica_html_block', value });
}

// 取「这个 child 代表的块级 HTML 原码」：html 节点本身、或 commonmark 把块级 HTML 包进的 paragraph。
function htmlValueOf(child: MdNode): string | null {
  if (isHtmlNode(child)) return child.value!;
  if (child.type === 'paragraph' && child.children && child.children.length === 1 && isHtmlNode(child.children[0])) {
    return child.children[0].value!;
  }
  return null;
}

// 折叠块：从 startIdx 的 `<details …>`（含 <summary>）一路吃到 `</details>`，中间的块＝富正文。
// 返回 mica_details mdast 节点 + 闭合标签下标；配不上规范形（无 summary / 行内含闭合 / 找不到闭合）→ null（交通用块）。
function consumeDetails(children: MdNode[], startIdx: number, openValue: string): { node: MdNode; nextIndex: number } | null {
  const s = openValue.trim();
  const om = /^<details\b([^>]*)>/i.exec(s);
  if (!om) return null;
  if (/<\/details>/i.test(s)) return null; // 行内单节点写法（开闭同节点）→ 交通用块当静态可折叠
  const sm = /<summary[^>]*>([\s\S]*?)<\/summary>/i.exec(s);
  if (!sm) return null;                     // 无 summary → 非规范形
  const open = /(^|\s)open(\s|=|$)/i.test(om[1]);
  const summaryText = sm[1].replace(/<[^>]*>/g, '').trim(); // summary 内只取纯文本（剥内部标签，v1）
  const body: MdNode[] = [];
  let j = startIdx + 1;
  let found = false;
  for (; j < children.length; j++) {
    const cv = htmlValueOf(children[j]);
    if (cv && /^<\/details>\s*$/i.test(cv.trim())) { found = true; break; }
    body.push(children[j]);
  }
  if (!found) return null;                   // 没闭合 → 交通用块
  const detailsMd: MdNode = {
    type: 'mica_details',
    open,
    children: [
      { type: 'details_summary', children: summaryText ? [{ type: 'text', value: summaryText }] : [] },
      ...(body.length ? body : [{ type: 'paragraph', children: [] }]), // 正文至少一个空段（content 要求 block+）
    ],
  };
  transform(detailsMd);                      // 递归处理正文里的 html / 行内 / 嵌套折叠
  return { node: detailsMd, nextIndex: j };
}

function transform(node: MdNode): void {
  if (!node.children) return;
  const children = node.children;
  const out: MdNode[] = [];
  for (let i = 0; i < children.length; i++) {
    const child = children[i];
    // ⓪ 折叠块：<details>…</details> 区间 → mica_details（含富正文）。须先于通用块判定。
    const hv = htmlValueOf(child);
    if (hv && /^<details\b/i.test(hv.trim())) {
      const consumed = consumeDetails(children, i, hv);
      if (consumed) { out.push(consumed.node); i = consumed.nextIndex; continue; } // continue→i++ 跳过 </details>
    }
    // ① 块级：段落只含「单个 html 子节点」（commonmark 的 remarkHtmlTransformer 把块级 HTML 包进了 paragraph）。
    if (child.type === 'paragraph' && child.children && child.children.length === 1 && isHtmlNode(child.children[0])) {
      emitFromHtmlValue(String(child.children[0].value ?? ''), out);
      continue;
    }
    // ② 极少：html 直接挂在块容器下（未被包进 paragraph）→ 块级。
    if (isHtmlNode(child)) {
      emitFromHtmlValue(String(child.value ?? ''), out);
      continue;
    }
    // ③ 段落内行内 html → 合并成对的。
    if (child.children && child.children.some(isHtmlNode)) {
      child.children = coalesceInline(child.children);
    }
    transform(child);
    out.push(child);
  }
  node.children = out;
}

export const micaHtmlRemark = $remark('micaHtml', () => () => (tree: unknown) => {
  transform(tree as MdNode);
});

// ===== 输入规则：在 WYSIWYG 里手打完整 HTML 标签即时转成 mica_html_inline（对标 Typora 边打边渲染）=====
// 触发：键入闭合 `>` 时，块内光标前文本匹配「成对行内标签 + 纯文本」或「自闭合标签」即就地替换成行内 atom。
// 只处理**行内** HTML：块级靠回车成段/粘贴/加载时由 micaHtmlRemark 渲染（行内即时渲染收益最大、风险最小）。
// 内部含 `<` `>` 或嵌套标签（如 <u>**x**</u>）不匹配 → 保持原文字，等下次加载由 remark 接管。
function makeInlineHtmlRule(re: RegExp, exclude?: (name: string) => boolean): InputRule {
  return new InputRule(re, (state, match, start, end) => {
    const type = state.schema.nodes.mica_html_inline;
    if (!type) return null;
    if (exclude?.((match[1] || '').toLowerCase())) return null;
    return state.tr.replaceWith(start, end, type.create({ value: match[0] }));
  });
}
// 成对：<u>文字</u>（\1 反向引用确保开闭同名；内部仅纯文本、无 < >）。
export const htmlPairedInputRule = makeInlineHtmlRule(/<([a-zA-Z][a-zA-Z0-9-]*)(?:\s[^<>]*)?>([^<>]*)<\/\1>$/);
// 自闭合：<br/> / <span … /> 等。排除 <img>（走图片管道，别被当通用 HTML 吞掉）。
export const htmlSelfCloseInputRule = makeInlineHtmlRule(/<([a-zA-Z][a-zA-Z0-9-]*)(?:\s[^<>]*)?\/>$/, (n) => n === 'img');
// <br> / </br>（无斜杠/闭合形）也即时换行——统一归一成 `<br/>` atom（用户 #6）。
export const htmlBrInputRule = new InputRule(/<\/?br\s*>$/i, (state, _match, start, end) => {
  const type = state.schema.nodes.mica_html_inline;
  if (!type) return null;
  return state.tr.replaceWith(start, end, type.create({ value: '<br/>' }));
});

// ===== 标签补全：输入 `</` 自动补出「最近一个未闭合标签」的闭合形 =====
// 用户澄清：不要弹窗让用户选，只需补全最近的未闭合标签即可。故不做下拉补全 UI，
// 用 InputRule：键入 `/`（紧接 `<` 成 `</`）时，扫描块内光标前文本算出最近未闭合标签名，补成 `</tag>`。
// 空元素（br/img/hr 等）无需闭合、不参与；已配对的标签会出栈。
const VOID_TAGS = new Set([
  'br', 'img', 'hr', 'input', 'meta', 'link', 'area', 'base', 'col', 'embed', 'source', 'track', 'wbr',
]);

// 扫描 text 里的标签，返回栈顶（=最近一个未闭合标签名）；都闭合/无标签 → null。
function nearestUnclosedTag(text: string): string | null {
  const re = /<(\/?)([a-zA-Z][a-zA-Z0-9-]*)(?:\s[^<>]*)?(\/?)>/g;
  const stack: string[] = [];
  let m: RegExpExecArray | null;
  while ((m = re.exec(text))) {
    const slash = m[1];
    const name = m[2].toLowerCase();
    const selfClose = m[3];
    if (selfClose || VOID_TAGS.has(name)) continue; // 自闭合 / 空元素不入栈
    if (slash) {
      // 闭合标签：从栈顶往下找同名的弹出（容忍中间未闭合的异常嵌套）。
      for (let k = stack.length - 1; k >= 0; k--) {
        if (stack[k] === name) { stack.length = k; break; }
      }
    } else {
      stack.push(name);
    }
  }
  return stack.length ? stack[stack.length - 1] : null;
}

// `</` → `</tag>`：补出最近未闭合标签的闭合形，光标落到补全之后。
// **承重坑**：InputRule 触发时只有 `<` 在文档里（在 [start,end]），刚打的 `/` 被规则消费、不会自动插入。
// 故必须补 `/${tag}>`（含斜杠），否则会得到 `<sup>ok<sup>`（丢了斜杠＝又一个开标签，不渲染）。
export const htmlCloseTagInputRule = new InputRule(/<\/$/, (state, _match, start, end) => {
  const $start = state.doc.resolve(start);
  if (!$start.parent.isTextblock) return null;
  // **冲突修复**：光标后若已紧跟 `tagname>`（如 `<sup>ok<|sup>` 里在 `<` 后插 `/`），说明用户是把现有 `<tag>`
  // 改成 `</tag>`、只想插一个 `/`——此时**不补全**（返回 null 让 `/` 正常插入），否则会多补 `/sup>` 致 `sup>` 落单。
  // 补出的 `</tag>` 由 htmlAutoConvertPlugin 即时渲染。仅当光标后不是「现成标签收尾」时才主动补全。
  const after = state.doc.textBetween(end, $start.end(), '\n', '\n');
  if (/^[a-zA-Z][a-zA-Z0-9-]*\s*>/.test(after)) return null;
  const before = state.doc.textBetween($start.start(), start, '\n', '\n'); // `<`（`</` 的 `<`）之前的块内文本
  const tag = nearestUnclosedTag(before);
  if (!tag) return null;
  return state.tr.insertText(`/${tag}>`, end);
});
