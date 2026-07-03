// 源代码模式编辑器（CodeMirror 6）——替换原来的纯 <textarea>。
//
// 为什么换：纯 textarea 既没法显示行号、也没语法高亮，且在整体缩放（body.style.zoom）下
// 会和外层一起出两个滚动条。CodeMirror 行号 / Markdown 语法高亮都是标配，且能与 WYSIWYG
// 一样「随内容增高、由页面(body)统一滚动」——故不给它内部滚动条（auto-height + scroller
// overflow:visible），根治源码模式 160% 缩放下的双滚动条问题。
//
// 配色全走 CSS 变量（--mica-* / --cm-*），跟随 body.dark 自动明暗，无需重建实例。
import { EditorView, lineNumbers, highlightActiveLineGutter, keymap, drawSelection } from '@codemirror/view';
import { EditorState } from '@codemirror/state';
import { history, defaultKeymap, historyKeymap, indentWithTab } from '@codemirror/commands';
import { markdown, markdownLanguage } from '@codemirror/lang-markdown';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags as t } from '@lezer/highlight';

// Markdown token → 颜色/字号（CSS 变量驱动，明暗自动）。标题用 Windows 强调色（对标 Typora）。
// tag 名以 @lezer/markdown 的 styleTags 为准：标题正文 heading1~6、标记符（#/>/*/`/[] 等定界符）
// 统一是 processingInstruction（弱化为次要色，让正文更突出）。
// 标题【放大字号】体现大小对比（对标 Typora，用户要的）——行高变化导致的行号对齐问题，
// 由 CSS 把 gutter 行号【垂直居中】解决（见 sourceTheme 的 .cm-lineNumbers gutterElement）。
const sourceHighlight = HighlightStyle.define([
  { tag: t.heading1, color: 'var(--mica-accent)', fontWeight: '700', fontSize: '1.45em' },
  { tag: t.heading2, color: 'var(--mica-accent)', fontWeight: '700', fontSize: '1.3em' },
  { tag: t.heading3, color: 'var(--mica-accent)', fontWeight: '700', fontSize: '1.15em' },
  { tag: t.heading4, color: 'var(--mica-accent)', fontWeight: '700', fontSize: '1.08em' },
  { tag: [t.heading5, t.heading6], color: 'var(--mica-accent)', fontWeight: '700' },
  { tag: t.strong, fontWeight: '700' },
  { tag: t.emphasis, fontStyle: 'italic' },
  { tag: t.strikethrough, textDecoration: 'line-through' },
  { tag: [t.link, t.url], color: 'var(--mica-accent)', textDecoration: 'underline' },
  { tag: t.monospace, color: 'var(--cm-string)', fontFamily: 'var(--mica-font-mono)' },
  { tag: t.quote, color: 'var(--mica-fg-muted)', fontStyle: 'italic' },
  // 列表【不上强调色】（用户反馈：太多）。`-`/`*`/`1.` 等列表标记本就是 processingInstruction → 走次要色。
  { tag: t.contentSeparator, color: 'var(--mica-fg-muted)' },
  // 标记符 / 链接标签：弱化为次要色。
  { tag: [t.processingInstruction, t.labelName], color: 'var(--mica-fg-muted)' },
  { tag: t.comment, color: 'var(--cm-comment)', fontStyle: 'italic' },
]);

// 行号 gutter：仅「光标所在行」「第 1 行」「整十行(10/20/30…)」显示数字，其余留空（对标 Typora）。
// 选区移动时 gutter 会重渲染（highlightActiveLineGutter 提供的 gutterLineClass 随光标行变），
// formatNumber 因此重算、当前行号实时跟随。注意：gutter 宽度撑量也调 formatNumber(最大行号)，
// 整十兜底未必命中→可能返回空串，故 CSS 给 .cm-lineNumbers 的 gutterElement 设 minWidth 兜底宽度。
const sourceLineNumbers = lineNumbers({
  formatNumber: (lineNo, state) => {
    const active = state.doc.lineAt(state.selection.main.head).number;
    if (lineNo === active || lineNo === 1 || lineNo % 10 === 0) return String(lineNo);
    return '';
  },
});

// 布局/光标/选区主题；色值走 CSS 变量。不设固定高度、scroller overflow:visible →
// 编辑器随内容增高、由页面(body)统一滚动（与 WYSIWYG 一致，避免双滚动条）。
const sourceTheme = EditorView.theme({
  '&': {
    backgroundColor: 'transparent',
    color: 'var(--mica-fg)',
    fontSize: 'calc(var(--mica-font-size) - 1px)',
  },
  '.cm-scroller': {
    fontFamily: 'var(--mica-font-mono)',
    lineHeight: '1.7',
    // 纵向不自带滚动条（由页面 body 滚——避免 body.zoom 下双滚动条）；
    // 横向 clip：消除「行号 gutter 横向粘连重合到文字上」——根因是页面横向溢出 +
    // sticky gutter，clip 掉横向溢出即可（lineWrapping 已软换行，正常内容不会真溢出）。
    overflowX: 'clip',
    overflowY: 'visible',
  },
  // 文本列与 WYSIWYG 同宽居中（切换源码模式不横移）；行号 gutter 在最左、不进居中列。
  // min-height 让正文下方空白处点击也能聚焦（与 .milkdown .editor 一致）。
  '.cm-content': {
    padding: '24px max(48px, calc((100% - var(--mica-editor-width)) / 2))',
    minHeight: 'calc(100vh - 48px)',
    caretColor: 'var(--mica-fg)',
    overflowWrap: 'anywhere',
  },
  '&.cm-focused': { outline: 'none' },
  '.cm-gutters': { backgroundColor: 'transparent', border: 'none', color: 'var(--mica-fg-muted)' },
  // 行号【垂直居中】到所在行——标题行更高时数字仍与文字对齐（不会顶对齐跑到上面）。
  '.cm-lineNumbers .cm-gutterElement': {
    padding: '0 6px 0 14px',
    minWidth: '32px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
  },
  // 当前行号高亮成强调色（不加整行背景，对标 Typora 源码模式的克制风格）。
  '.cm-activeLineGutter': { backgroundColor: 'transparent', color: 'var(--mica-accent)' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--mica-fg)' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': { backgroundColor: 'rgba(120, 120, 120, 0.3)' },
});

export interface SourceCallbacks {
  onChange: (doc: string) => void;
  onCompositionStart: () => void;
  onCompositionEnd: () => void;
}

// 创建源码模式 CodeMirror 实例。parent 是 .mica-source 容器；doc 为初始 Markdown。
export function createSourceView(parent: HTMLElement, doc: string, cb: SourceCallbacks): EditorView {
  const updateListener = EditorView.updateListener.of((u) => {
    if (u.docChanged) cb.onChange(u.state.doc.toString());
  });
  const view = new EditorView({
    parent,
    state: EditorState.create({
      doc,
      extensions: [
        sourceLineNumbers,
        highlightActiveLineGutter(),
        history(),
        drawSelection(),
        keymap.of([...defaultKeymap, ...historyKeymap, indentWithTab]),
        markdown({ base: markdownLanguage }), // GFM 基（含任务列表/删除线/表格语法）
        syntaxHighlighting(sourceHighlight),
        EditorView.lineWrapping, // 软换行，长行不出横向滚动条（对标 Typora）
        sourceTheme,
        updateListener,
      ],
    }),
  });
  // IME 统计：组合输入期间冻结字数（与 WYSIWYG 共用 onCompositionStart/End）。
  view.contentDOM.addEventListener('compositionstart', cb.onCompositionStart);
  view.contentDOM.addEventListener('compositionend', cb.onCompositionEnd);
  return view;
}

export function getSourceDoc(view: EditorView): string {
  return view.state.doc.toString();
}

// 整篇替换内容（切文件时复用同一实例）。
export function setSourceDoc(view: EditorView, doc: string): void {
  view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: doc } });
}
