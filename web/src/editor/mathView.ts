// 公式（KaTeX）nodeView。
//
// 行内公式（math_inline）已改用通用的 inlineEmbedView（见 inlineEmbedView.ts，与行内代码共用一套
// 交互），本文件只保留 **块级公式（math_block）** 的 nodeView，以及供行内公式用的 KaTeX 渲染器。
//
// 块级公式交互：选中节点（点击 / 插入 / 退格）→ 显示 `$$ [textarea] $$` + 下方预览；不做相邻显露
// （块级本身就是独立一块）。存储：LaTeX 存在 attrs.value（与行内的「存文本内容」不同，别搞反）。
import katex from 'katex';
import type { Node as ProseNode } from '@milkdown/prose/model';
import type { EditorView, NodeView } from '@milkdown/prose/view';
import { Selection, TextSelection } from '@milkdown/prose/state';

// 行内公式渲染器：把 LaTeX 渲染进行内元素（displayMode=false）。供 createEditor 组装 inlineEmbed 用。
export function renderMathInline(el: HTMLElement, latex: string) {
  if (!latex.trim()) {
    el.innerHTML = '<span class="mica-embed-empty">空公式</span>';
    return;
  }
  try {
    katex.render(latex, el, { throwOnError: false, displayMode: false });
  } catch {
    el.textContent = latex;
  }
}

// 行内公式预览卡片渲染器（displayMode=true，展示更大的居中公式）。
export function renderMathPreview(el: HTMLElement, latex: string) {
  if (!latex.trim()) {
    el.innerHTML = '<span class="mica-embed-empty">预览…</span>';
    return;
  }
  try {
    katex.render(latex, el, { throwOnError: false, displayMode: true });
  } catch {
    el.textContent = latex;
  }
}

// ===== 块级公式 nodeView =====
export function createMathBlockView() {
  return (node: ProseNode, view: EditorView, getPos: () => number | undefined): NodeView => {
    const dom = document.createElement('div');
    dom.className = 'mica-math mica-math-block';

    const rendered = document.createElement('div');
    rendered.className = 'mica-math-render';

    const reveal = document.createElement('div');
    reveal.className = 'mica-math-reveal';
    const delimOpen = document.createElement('span');
    delimOpen.className = 'mica-math-delim';
    delimOpen.textContent = '$$';
    const delimClose = document.createElement('span');
    delimClose.className = 'mica-math-delim';
    delimClose.textContent = '$$';
    const input = document.createElement('textarea');
    input.className = 'mica-math-src';
    input.spellcheck = false;
    input.rows = 1;
    reveal.append(delimOpen, input, delimClose);

    const preview = document.createElement('div');
    preview.className = 'mica-math-preview';

    dom.append(rendered, reveal, preview);

    let current = node;
    let editing = false;

    const getLatex = (n: ProseNode) => (n.attrs.value as string) ?? '';

    const renderInto = (el: HTMLElement, latex: string, hint: string) => {
      if (!latex.trim()) { el.innerHTML = `<span class="mica-math-empty">${hint}</span>`; return; }
      try { katex.render(latex, el, { throwOnError: false, displayMode: true }); }
      catch { el.textContent = latex; }
    };

    const autoSize = () => {
      input.style.height = 'auto';
      input.style.height = `${input.scrollHeight}px`;
    };

    const applyState = () => {
      if (editing) {
        dom.classList.add('is-active', 'is-editing');
        rendered.style.display = 'none';
        reveal.style.display = '';
        preview.style.display = '';
        renderInto(preview, input.value, '预览…');
      } else {
        dom.classList.remove('is-active', 'is-editing');
        rendered.style.display = '';
        reveal.style.display = 'none';
        preview.style.display = 'none';
        renderInto(rendered, getLatex(current), '点击编辑公式…');
      }
    };

    const writeLatex = (latex: string) => {
      const pos = getPos();
      if (pos == null) return;
      const n = view.state.doc.nodeAt(pos);
      if (!n || n.type !== current.type) return;
      if (getLatex(n) === latex) return;
      view.dispatch(view.state.tr.setNodeMarkup(pos, undefined, { value: latex }));
    };

    const openEditor = () => {
      if (editing) return;
      editing = true;
      input.value = getLatex(current);
      applyState();
      autoSize();
      const focusInput = () => {
        // preventScroll：编辑框获焦时别再把页面滚一段——插入公式块时滚动已由 PM 的 scrollIntoView 负责，
        // 这里的原生 focus 滚动会和它叠加，表现为「组件出来后滚动条又往上跳一段」（用户反馈）。
        input.focus({ preventScroll: true });
        const end = input.value.length;
        input.setSelectionRange(end, end);
      };
      requestAnimationFrame(focusInput);
      setTimeout(focusInput, 60);
    };

    const closeEditor = () => { editing = false; applyState(); };

    // 退出编辑：先 blur 提交，再移选区。向上退出且本块是文档最前时，前面插一个空段落（对标 Typora）。
    const exitTo = (dir: -1 | 1) => {
      const pos = getPos();
      input.blur();
      if (pos == null) { view.focus(); return; }
      const state = view.state;
      const n = state.doc.nodeAt(pos);
      const size = n ? n.nodeSize : 1;
      let tr = state.tr;
      if (dir < 0) {
        const $pos = state.doc.resolve(pos);
        if ($pos.depth === 0 && !$pos.nodeBefore) {
          const para = state.schema.nodes.paragraph?.createAndFill();
          if (para) {
            tr = tr.insert(pos, para);
            tr = tr.setSelection(TextSelection.create(tr.doc, pos + 1));
            view.dispatch(tr.scrollIntoView());
            view.focus();
            return;
          }
        }
        tr = tr.setSelection(Selection.near(state.doc.resolve(pos), -1));
      } else {
        const after = Math.min(pos + size, state.doc.content.size);
        tr = tr.setSelection(Selection.near(state.doc.resolve(after), 1));
      }
      view.dispatch(tr.scrollIntoView());
      view.focus();
    };

    const deleteSelf = () => {
      const pos = getPos();
      if (pos == null) return;
      const n = view.state.doc.nodeAt(pos);
      if (!n) return;
      const tr = view.state.tr.delete(pos, pos + n.nodeSize);
      tr.setSelection(Selection.near(tr.doc.resolve(Math.min(pos, tr.doc.content.size)), -1));
      view.dispatch(tr);
      view.focus();
    };

    rendered.addEventListener('click', () => {
      const pos = getPos();
      if (pos == null || editing) return;
      view.dispatch(view.state.tr.setSelection(Selection.near(view.state.doc.resolve(pos))));
      openEditor();
    });

    input.addEventListener('input', () => { autoSize(); renderInto(preview, input.value, '预览…'); });
    input.addEventListener('blur', () => writeLatex(input.value));
    input.addEventListener('keydown', (e: KeyboardEvent) => {
      const collapsed = input.selectionStart === input.selectionEnd;
      const caret = input.selectionStart ?? 0;
      const onFirstLine = input.value.lastIndexOf('\n', caret - 1) === -1;
      const onLastLine = input.value.indexOf('\n', caret) === -1;
      if (e.key === 'Escape' || (e.key === 'Enter' && (e.ctrlKey || e.metaKey))) {
        e.preventDefault();
        exitTo(1);
      } else if (e.key === 'Backspace' && input.value.length === 0) {
        e.preventDefault();
        deleteSelf();
      } else if (collapsed && e.key === 'ArrowLeft' && caret === 0) {
        e.preventDefault();
        exitTo(-1);
      } else if (collapsed && e.key === 'ArrowRight' && caret === input.value.length) {
        e.preventDefault();
        exitTo(1);
      } else if (collapsed && e.key === 'ArrowUp' && onFirstLine) {
        e.preventDefault();
        exitTo(-1);
      } else if (collapsed && e.key === 'ArrowDown' && onLastLine) {
        e.preventDefault();
        exitTo(1);
      }
    });

    applyState();

    return {
      dom,
      stopEvent: (e) => input.contains(e.target as HTMLElement),
      ignoreMutation: () => true,
      selectNode: () => openEditor(),
      deselectNode: () => {
        if (!editing) return;
        const latex = input.value;
        closeEditor();
        queueMicrotask(() => writeLatex(latex));
      },
      update: (updated: ProseNode) => {
        if (updated.type !== current.type) return false;
        current = updated;
        if (editing) renderInto(preview, input.value, '预览…');
        else applyState();
        return true;
      },
      destroy: () => { rendered.remove(); reveal.remove(); preview.remove(); },
    };
  };
}
