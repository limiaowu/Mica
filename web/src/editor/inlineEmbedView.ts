// 行内可编辑「嵌入」节点的通用 nodeView（行内公式 / 行内代码共用）。
//
// 设计背景（对标 Typora）：
//   行内公式（math_inline）与行内代码（inline_code）在 ProseMirror 里都是 atom 节点——区分符（`$`/反引号）
//   由本 nodeView 渲染、不是文档里的真实字符，故整体增删、可靠进出；**原子节点的光标语义干净**（节点前后
//   是清晰文本位置、方向键整体跳过），这正是行内代码用「标记」会卡边界/进不去缝的根因。两者交互完全一致，
//   只是「渲染函数 + 是否带预览卡片 + 区分符」不同，于是抽成这一个工厂。
//
// 两种显示态（由「光标与本节点的相对位置」决定，相邻判断在 createEditor 的 reveal 插件里算好，
// 经 node decoration 的 spec.embedActive 传进来，见本文件 update）：
//   1. 渲染态：光标不在附近 → 只显示渲染结果（KaTeX / <code>）。
//   2. 显露/编辑态：光标相邻 或 节点被选中 → 显示「区分符 + 可编辑 <input>(+预览)」。
//      · 相邻(active)：input 显示但**不抢焦点**，PM 光标仍在节点前/后；点 input 即原生聚焦并把
//        光标落到点击处（**一步到位**，修「明明显示了 $、点一下先跳末尾、要点两下」）。
//      · 选中/进入(editing)：input 聚焦，可改；失焦 / 方向键到边界即提交。
//   关键：input 的事件被 stopEvent 隔离，PM 不会在 input 上建 NodeSelection，故点 input 直接由浏览器
//   定位光标（不会被 PM 抢去选成整节点再落到末尾）。
//
// 存储：值统一存在节点的「文本内容」(node.textContent)，content 为 'text*'。
// 关键：**空内容在提交时直接删除整个节点**——空行内公式会被序列化成 `$$`（再读盘变行间公式）、
// 空行内代码会被序列化成成对反引号，都会破坏 .md 往返，故不保留空节点（对标 Typora 占位即弃）。
import type { Node as ProseNode } from '@milkdown/prose/model';
import type { Decoration, EditorView, NodeView } from '@milkdown/prose/view';
import { NodeSelection, Selection } from '@milkdown/prose/state';

export interface InlineEmbedOptions {
  nodeName: string;                 // 'math_inline' | 'inline_code'
  rootClass: string;                // 根元素附加类（用于按类型调样式）
  delimiter: string;                // 区分符：'$' 或 '`'
  // 把已提交的值渲染进渲染态元素（KaTeX / 纯文本）
  renderValue: (el: HTMLElement, value: string) => void;
  // 可选：编辑/显露时的实时预览卡片（公式用，代码不需要）。不传 = 无预览。
  renderPreview?: (el: HTMLElement, value: string) => void;
}

// 方向键从「左侧」进入嵌入节点编辑时，光标落源码开头（紧贴开区分符）；从「右侧」进入或点击落末尾。
// createEditor 的 enterEmbedOnArrow 在派发 NodeSelection 前调用此 setter，openEditor 消费一次后复位。
let nextOpenAtStart = false;
export function setNextOpenAtStart(v: boolean) {
  nextOpenAtStart = v;
}

export function createInlineEmbedView(opts: InlineEmbedOptions) {
  return (node: ProseNode, view: EditorView, getPos: () => number | undefined): NodeView => {
    const dom = document.createElement('span');
    dom.className = `mica-embed ${opts.rootClass}`;

    // 渲染态结果
    const rendered = document.createElement('span');
    rendered.className = 'mica-embed-render';

    // 显露/编辑态容器：区分符 + <input> + 区分符
    const reveal = document.createElement('span');
    reveal.className = 'mica-embed-reveal';
    const delimOpen = document.createElement('span');
    delimOpen.className = 'mica-embed-delim';
    delimOpen.textContent = opts.delimiter;
    const delimClose = document.createElement('span');
    delimClose.className = 'mica-embed-delim';
    delimClose.textContent = opts.delimiter;
    const input = document.createElement('input'); // 单行输入框（与正文同基线）
    input.type = 'text';
    input.className = 'mica-embed-src';
    input.spellcheck = false;
    reveal.append(delimOpen, input, delimClose);

    // 预览卡片（仅公式）
    const preview = opts.renderPreview ? document.createElement('span') : null;
    if (preview) preview.className = 'mica-embed-preview';

    dom.append(rendered, reveal);
    if (preview) dom.append(preview);

    let current = node;
    let editing = false;        // input 聚焦中
    let active = false;         // 光标相邻 / 节点被选中（来自 decoration）

    const getValue = (n: ProseNode) => n.textContent;

    const autoSize = () => {
      // 优先 CSS field-sizing:content（精确贴合、与闭区分符无空隙、空也几乎零宽）；旧内核退回 ch 估算
      if (typeof CSS !== 'undefined' && CSS.supports && CSS.supports('field-sizing', 'content')) {
        input.style.width = '';
      } else {
        input.style.width = `${input.value.length}ch`; // 空 → 0ch（两区分符紧贴）
      }
    };

    const syncPreview = (value: string) => {
      if (preview && opts.renderPreview) opts.renderPreview(preview, value);
    };

    // 按 editing/active 切显示态。
    const applyState = () => {
      if (editing || active) {
        dom.classList.add('is-revealed');
        dom.classList.toggle('is-editing', editing);
        rendered.style.display = 'none';
        reveal.style.display = '';
        input.style.display = 'inline-block';
        // 未编辑（仅相邻显露）时把 input 内容同步成节点值，保证显示正确、点击落点准确
        if (!editing) { input.value = getValue(current); autoSize(); }
        if (preview) { preview.style.display = ''; syncPreview(editing ? input.value : getValue(current)); }
      } else {
        dom.classList.remove('is-revealed', 'is-editing');
        rendered.style.display = '';
        reveal.style.display = 'none';
        if (preview) preview.style.display = 'none';
        opts.renderValue(rendered, getValue(current));
      }
    };

    // 删除整个节点，光标落原位。空内容提交也走这里（防止空节点序列化破坏 markdown）。
    const deleteSelf = () => {
      const pos = getPos();
      if (pos == null) return;
      const n = view.state.doc.nodeAt(pos);
      if (!n || n.type.name !== opts.nodeName) return;
      const tr = view.state.tr.delete(pos, pos + n.nodeSize);
      tr.setSelection(Selection.near(tr.doc.resolve(Math.min(pos, tr.doc.content.size)), -1));
      view.dispatch(tr);
      view.focus();
    };

    // 把值写回节点内容（无变化则跳过）。空 → 删除整个节点。
    const writeValue = (value: string) => {
      if (!value.trim()) { deleteSelf(); return; }
      const pos = getPos();
      if (pos == null) return;
      const n = view.state.doc.nodeAt(pos);
      if (!n || n.type.name !== opts.nodeName) return;
      if (n.textContent === value) return;
      const from = pos + 1;
      const to = pos + n.nodeSize - 1;
      view.dispatch(view.state.tr.replaceWith(from, to, view.state.schema.text(value)));
    };

    // 聚焦 input 并把光标落到指定端（NodeSelection 进入路径用：方向键/插入/点渲染结果）。
    const focusInput = (atStart: boolean) => {
      const focus = () => {
        input.focus({ preventScroll: true }); // 别让获焦连带滚动页面（滚动交给 PM scrollIntoView，见 mathView 注释）
        const caret = atStart ? 0 : input.value.length;
        input.setSelectionRange(caret, caret);
      };
      // 延迟聚焦：selectNode 在视图更新期间调用；setTimeout 兜底——从原生菜单触发插入时
      // WebView 此刻可能还没拿回 OS 焦点，单次 rAF 的 focus 会落空。
      requestAnimationFrame(focus);
      setTimeout(focus, 60);
    };

    const openEditor = () => {
      if (editing) return;
      editing = true;
      input.value = getValue(current);
      applyState();
      autoSize();
      const atStart = nextOpenAtStart;
      nextOpenAtStart = false;
      focusInput(atStart);
    };

    // 退出编辑并把光标移到节点之前(-1)/之后(+1)：blur 同步提交，再移选区。
    const exitTo = (dir: -1 | 1) => {
      input.blur(); // 同步触发 blur → 提交（可能删空节点）
      const pos = getPos();
      if (pos == null) { view.focus(); return; } // 节点被删（空）→ 光标已在删除处
      const n = view.state.doc.nodeAt(pos);
      const size = n ? n.nodeSize : 1;
      const target = dir < 0 ? pos : Math.min(pos + size, view.state.doc.content.size);
      view.dispatch(view.state.tr.setSelection(Selection.near(view.state.doc.resolve(target), dir)).scrollIntoView());
      view.focus();
    };

    // 选中本节点（NodeSelection）→ selectNode 打开编辑框。用于点渲染结果/区分符。
    const selectSelf = () => {
      if (editing) return;
      const pos = getPos();
      if (pos == null) return;
      view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, pos)));
    };
    // 用 click 而非 mousedown：mousedown 上 preventDefault 会破坏拖拽选区。
    rendered.addEventListener('click', selectSelf);
    delimOpen.addEventListener('click', selectSelf);
    delimClose.addEventListener('click', selectSelf);

    // 点 input（显露态，未聚焦）→ 原生聚焦，浏览器把光标落到点击处；这里只补上 editing 态切换。
    input.addEventListener('focus', () => {
      if (!editing) { editing = true; applyState(); }
    });
    input.addEventListener('input', () => {
      autoSize();
      if (editing) syncPreview(input.value);
    });
    input.addEventListener('blur', () => {
      editing = false;
      writeValue(input.value); // 含空→删除
      applyState();
    });
    input.addEventListener('keydown', (e: KeyboardEvent) => {
      const caret = input.selectionStart ?? 0;
      const len = input.value.length;
      if (e.key === 'Escape' || e.key === 'Enter') {
        e.preventDefault();
        exitTo(1);
      } else if (e.key === 'Backspace' && len === 0) {
        e.preventDefault();
        deleteSelf();
      } else if (e.key === 'ArrowLeft' && caret === 0) {
        e.preventDefault();
        exitTo(-1);
      } else if (e.key === 'ArrowRight' && caret === len) {
        e.preventDefault();
        exitTo(1);
      }
    });

    applyState();

    return {
      dom,
      // input 内的事件交给它自己（PM 不在 input 上建 NodeSelection，故点击直接由浏览器定位光标）
      stopEvent: (e) => input.contains(e.target as HTMLElement),
      ignoreMutation: () => true,
      // 节点被选中即进入编辑（聚焦）；选区移走即收起并提交（脱离当前 update 周期，避免嵌套 dispatch）
      selectNode: () => openEditor(),
      deselectNode: () => {
        if (!editing) return;
        const value = input.value;
        editing = false;
        applyState();
        queueMicrotask(() => writeValue(value));
      },
      // decorations 第 2 参带本节点的 node decoration；reveal 插件给「光标相邻」的节点打 spec.embedActive
      update: (updated: ProseNode, decos?: readonly Decoration[]) => {
        if (updated.type.name !== opts.nodeName) return false;
        current = updated;
        active = Array.isArray(decos) && decos.some((d) => d.spec && d.spec.embedActive);
        if (editing) syncPreview(input.value);
        else applyState();
        return true;
      },
      destroy: () => {
        rendered.remove();
        reveal.remove();
        preview?.remove();
      },
    };
  };
}
