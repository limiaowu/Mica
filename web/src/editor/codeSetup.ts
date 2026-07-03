// CodeMirror 6 代码块配置（供 @milkdown/components 的 codeBlockComponent 使用）。
//
// 关键设计——语法配色用「CSS 变量」驱动：
// 代码块里的 CodeMirror 实例由组件按需创建/销毁、且【不随明暗主题重建】，所以不能用
// oneDark 之类的静态主题（切主题时无法热更新）。改为把每个 token 的颜色写成 var(--cm-*)，
// 真正的色值在 styles/editor.css 的 :root / body.dark 里定义，跟随 body.dark 经 CSS 级联
// 自动明暗切换，无需重建编辑器。想改配色就去 editor.css 调那批 --cm-* 变量。
import { EditorView, lineNumbers, highlightActiveLine, highlightActiveLineGutter, keymap } from '@codemirror/view';
import { history, defaultKeymap, historyKeymap, indentWithTab } from '@codemirror/commands';
import { bracketMatching, indentOnInput, HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags as t } from '@lezer/highlight';
import { Prec } from '@codemirror/state';
import type { Extension } from '@codemirror/state';
import { TextSelection } from '@milkdown/prose/state';
import type { EditorView as PmEditorView } from '@milkdown/prose/view';

// 代码块位于文档首行时，组件自带的 ArrowUp/ArrowLeft 退出会执行 TextSelection.near(resolve(0),-1)，
// 落到 doc 顶层（非 inline 容器）——既报 "TextSelection endpoint not pointing into a node with
// inline content (doc)" 警告，又移不动光标。这里以 Prec.highest 抢先拦截：仅当「代码块是文档首块、
// 光标在其首行/最前」时，在它前面插入一个空段落并把光标放进去（对标 Typora，与公式块一致）。
// 其它情况一律 return false，交回组件默认退出逻辑（上方有内容时它本就能正确处理）。
function escapeToLeadingParagraph(
  cm: EditorView,
  getProseView: () => PmEditorView | null,
  unit: 'line' | 'char',
): boolean {
  const main = cm.state.selection.main;
  if (!main.empty) return false;
  const atTop = unit === 'line' ? cm.state.doc.lineAt(main.head).from === 0 : main.head === 0;
  if (!atTop) return false;
  const view = getProseView();
  if (!view) return false;
  // 仅当「文档首块就是代码块」且「触发的正是这个首块的 CM」时才接管。
  // 不用 posAtDOM（它对 nodeView 内部 DOM 返回的是块内位置 depth=1，判断不出「块前」），
  // 改用 DOM 包含判断：第一个 .milkdown-code-block 是否含当前 cm.dom。
  if (view.state.doc.firstChild?.type.name !== 'code_block') return false;
  const firstBlock = view.dom.querySelector('.milkdown-code-block');
  if (!firstBlock || !firstBlock.contains(cm.dom)) return false;
  const paragraph = view.state.schema.nodes.paragraph?.createAndFill();
  if (!paragraph) return false;
  const tr = view.state.tr.insert(0, paragraph);          // 在文档最前插入空段落
  tr.setSelection(TextSelection.create(tr.doc, 1)).scrollIntoView(); // 光标落进新段落
  view.dispatch(tr);
  view.focus();
  return true;
}

// 供 buildCodeMirrorExtensions 使用：拦截首行块的向上/向左退出（见上）。
function leadingEscapeKeymap(getProseView: () => PmEditorView | null): Extension {
  return Prec.highest(
    keymap.of([
      { key: 'ArrowUp', run: (cm) => escapeToLeadingParagraph(cm, getProseView, 'line') },
      { key: 'ArrowLeft', run: (cm) => escapeToLeadingParagraph(cm, getProseView, 'char') },
    ]),
  );
}

// token → CSS 变量。颜色值见 editor.css 的 --cm-* 定义。
const micaHighlight = HighlightStyle.define([
  { tag: [t.keyword, t.modifier, t.controlKeyword, t.operatorKeyword, t.moduleKeyword], color: 'var(--cm-keyword)' },
  { tag: [t.string, t.special(t.string), t.regexp, t.character], color: 'var(--cm-string)' },
  { tag: [t.comment, t.lineComment, t.blockComment, t.docComment], color: 'var(--cm-comment)', fontStyle: 'italic' },
  { tag: [t.number, t.bool, t.null, t.atom], color: 'var(--cm-number)' },
  { tag: [t.function(t.variableName), t.function(t.propertyName), t.labelName], color: 'var(--cm-function)' },
  { tag: [t.typeName, t.className, t.namespace], color: 'var(--cm-type)' },
  { tag: [t.propertyName, t.attributeName], color: 'var(--cm-property)' },
  { tag: [t.tagName, t.angleBracket], color: 'var(--cm-tag)' },
  { tag: [t.definition(t.variableName), t.definition(t.propertyName)], color: 'var(--cm-def)' },
  { tag: [t.meta, t.documentMeta, t.annotation, t.processingInstruction], color: 'var(--cm-meta)' },
  { tag: [t.operator, t.punctuation, t.separator, t.bracket, t.derefOperator], color: 'var(--cm-punct)' },
  { tag: [t.link, t.url], color: 'var(--cm-keyword)', textDecoration: 'underline' },
  { tag: t.strong, fontWeight: 'bold' },
  { tag: t.emphasis, fontStyle: 'italic' },
  { tag: t.strikethrough, textDecoration: 'line-through' },
  { tag: t.invalid, color: 'var(--cm-invalid)' },
]);

// 仅做布局/光标/选区，背景透明融进代码块卡片；色值同样走 CSS 变量。
const micaCmTheme = EditorView.theme({
  '&': { backgroundColor: 'transparent', color: 'var(--cm-fg)' },
  '.cm-scroller': { fontFamily: 'var(--mica-font-mono)', fontSize: '13px', lineHeight: '1.6' },
  '.cm-content': { padding: '6px 0', caretColor: 'var(--cm-fg)' },
  '&.cm-focused': { outline: 'none' },
  '.cm-gutters': { backgroundColor: 'transparent', border: 'none', color: 'var(--mica-fg-muted)' },
  '.cm-lineNumbers .cm-gutterElement': { padding: '0 8px 0 4px', minWidth: '20px' },
  '.cm-activeLine': { backgroundColor: 'rgba(128,128,128,0.07)' },
  '.cm-activeLineGutter': { backgroundColor: 'rgba(128,128,128,0.07)' },
  // 失焦时不要再高亮「当前行」，否则光标移出代码块后仍残留一条阴影（用户反馈）
  '&:not(.cm-focused) .cm-activeLine': { backgroundColor: 'transparent' },
  '&:not(.cm-focused) .cm-activeLineGutter': { backgroundColor: 'transparent' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--cm-fg)' },
  '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': { backgroundColor: 'rgba(120,120,120,0.3)' },
  '.cm-matchingBracket, &.cm-focused .cm-matchingBracket': { backgroundColor: 'rgba(120,120,120,0.25)', outline: 'none' },
});

// 叠加在组件内置扩展（readOnly/drawSelection/退出键位/语言加载）之上。
// 不重复 drawSelection；内置 keymap 在前、优先级更高，边界导航不会被这里覆盖。
// 行号是否显示由设置控制（editor.config → showLineNumbers）；因 CodeMirror 实例由组件
// 管理、不随配置热重建，切换该项需重建编辑器（createEditor 里改设置时会重载当前文档）。
export function buildCodeMirrorExtensions(
  showLineNumbers: boolean,
  getProseView: () => PmEditorView | null,
): Extension[] {
  return [
    leadingEscapeKeymap(getProseView), // 首行代码块向上/向左退出时在其前面新建一行（见上）
    ...(showLineNumbers ? [lineNumbers(), highlightActiveLineGutter()] : []),
    highlightActiveLine(),
    history(),
    bracketMatching(),
    indentOnInput(),
    keymap.of([...defaultKeymap, ...historyKeymap, indentWithTab]),
    syntaxHighlighting(micaHighlight),
    micaCmTheme,
  ];
}

// 代码块工具栏图标（语言选择展开箭头 / 复制 / 搜索 / 清除）。
// 组件用 DOMPurify.sanitize 后 innerHTML 渲染，故可直接传 SVG；用 currentColor 跟随文字色。
const svg = (path: string) =>
  `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round">${path}</svg>`;

export const codeBlockIcons = {
  expandIcon: svg('<path d="M4 6l4 4 4-4"/>'),
  copyIcon: svg('<rect x="5.5" y="5.5" width="7.5" height="7.5" rx="1.2"/><path d="M10.5 5.5V4a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v5.5a1 1 0 0 0 1 1h1.5"/>'),
  searchIcon: svg('<circle cx="7" cy="7" r="4"/><path d="M13 13l-3-3"/>'),
  clearSearchIcon: svg('<path d="M4 4l8 8M12 4l-8 8"/>'),
};
