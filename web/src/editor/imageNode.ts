// 图片节点（替换 commonmark 的 image）。
//
// 设计（见 docs/图片功能计划.md）：
//   - **混合序列化**：普通图（无 width/align/radius/border 等样式）写成干净的 `![alt](url "title")` Markdown；
//     带样式的图才写成 `<img …>` HTML。理由：表格必须 HTML（合并单元格无 Markdown 等价），但图片不必——
//     绝大多数粘贴进来的普通图保持 .md 干净，只有真正调过样式的才用 HTML。
//   - **显示管道**：编辑器挂在 https 源，`<img src="file://…">` 会被 WebView2 拦掉。故 DOM 里的 src 永远写成
//     `https://img.mica.local/img?p=<encodeURIComponent(绝对路径)>`，由宿主拦截读盘喂回（见 EditorHostView）。
//     .md 里存的仍是相对/绝对路径（attrs.src），DOM 渲染时才转成显示 URL。
//   - 内部复制粘贴：toDOM 额外写 `data-src=原始路径`，parseDOM 优先读它 → 复制图片不会把显示 URL 当成路径存回。
import type { Node as ProseNode } from '@milkdown/prose/model';
import type { NodeView, EditorView } from '@milkdown/prose/view';
import { InputRule } from '@milkdown/prose/inputrules';
import { $nodeSchema, $remark } from '@milkdown/utils';
import { startImageDrag } from './imageDrag';

const IMG_HOST = 'https://img.mica.local/img?p=';

// 当前笔记所在目录（绝对路径），用于把相对 src 解析成显示 URL。每次 editor.load 由 createEditor 设。
let noteDir = '';
export function setImageBaseDir(noteAbsPath: string) {
  noteDir = noteAbsPath ? noteAbsPath.replace(/[\\/][^\\/]*$/, '') : '';
}

function isAbsolutePath(p: string): boolean {
  return /^[a-zA-Z]:[\\/]/.test(p) || /^\\\\/.test(p) || p.startsWith('/');
}

// 把存储的 src（相对/绝对路径，或网络/data URI）转成 DOM <img> 实际加载的地址。
//   · http(s)/data: 直接用（网络图/内嵌 base64，不走宿主管道）；
//   · 本地路径：拼成 img.mica.local 显示 URL（相对路径用 noteDir 拼绝对——宿主侧 Path.GetFullPath 会规范化 `..`，
//     故这里不必自己处理 `..`/分隔符）。
export function imageDisplayUrl(src: string): string {
  if (!src) return '';
  if (/^(https?:|data:)/i.test(src)) return src;
  const abs = isAbsolutePath(src) ? src : (noteDir ? `${noteDir}/${src}` : src);
  return IMG_HOST + encodeURIComponent(abs);
}

// 把存储的 src 解析成「磁盘绝对路径」（供右键菜单的复制路径/在资源管理器显示/复制图片用，由宿主读盘）。
// 网络图/内嵌 base64 无磁盘路径 → 返回空串（宿主据此禁用相关菜单项）。
export function imageAbsPath(src: string): string {
  if (!src || /^(https?:|data:)/i.test(src)) return '';
  return isAbsolutePath(src) ? src : (noteDir ? `${noteDir}/${src}` : src);
}

// ===== HTML 实体 编解码（与 htmlTable 一致的转义风格）=====
function escapeAttr(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
function decodeEntities(s: string): string {
  return s
    .replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
}

export interface ImageAttrs {
  src: string;
  alt: string;
  title: string | null;
  width: string | null;  // 显示宽度（如 "60%" / "320px"），存到外层 wrap 的 style.width（= 行宽百分比）
  align: string | null;  // left/center/right（居中=null，保持干净 Markdown）
  radius: number | null; // 圆角 px（null = 用 CSS 默认 2px，不算「带样式」）
  border: number | null; // 边框宽度 px（null = 无边框；>0 = `border:Npx solid`，颜色随正文 currentColor 自动明暗）
  // 裁剪矩形（第三期）：相对**原图自然尺寸**的归一化分数 "L,T,W,H"（0~1），null=不裁。原图文件绝不改动，
  // 只记「显示哪一块」（CSS overflow + 缩放定位），可无限次回原图重裁。存进 <img> 的 data-crop。
  crop: string | null;
  // 翻转：水平/垂直镜像（CSS transform: scaleX/scaleY(-1)，挂外层 wrap → 与裁剪同存于 wrap，裁剪区也跟着镜像、
  // 位置不变）。存进 <img> 的 data-flip（"h"/"v"/"hv"），别的渲染器忽略 → 优雅降级显示原向。
  flipH: boolean;
  flipV: boolean;
}

// 一张图是否「带样式」（需要写成 HTML，而非 ![]() Markdown）。任何一项非默认即带样式。
function isStyled(a: { width?: unknown; align?: unknown; radius?: unknown; border?: unknown; crop?: unknown; flipH?: unknown; flipV?: unknown }): boolean {
  return a.width != null || a.align != null || a.radius != null || a.border != null || a.crop != null
    || a.flipH === true || a.flipV === true;
}

// 翻转 → data-flip 串（"h"/"v"/"hv"/""）与 CSS transform。
export function flipDataAttr(h: boolean, v: boolean): string {
  return `${h ? 'h' : ''}${v ? 'v' : ''}`;
}
export function flipCss(h: boolean, v: boolean): string {
  const p: string[] = [];
  if (h) p.push('scaleX(-1)');
  if (v) p.push('scaleY(-1)');
  return p.join(' ');
}

// 解析裁剪分数串 "L,T,W,H" → 四元组（非法/宽高<=0 返回 null）。
export function parseCropAttr(v: string | null | undefined): { L: number; T: number; W: number; H: number } | null {
  if (!v) return null;
  const p = v.split(',').map(Number);
  if (p.length !== 4 || p.some((n) => !Number.isFinite(n))) return null;
  const [L, T, W, H] = p;
  if (W <= 0 || H <= 0) return null;
  return { L, T, W, H };
}

// 居中裁剪：让 natW×natH 的图按 targetAspect（宽/高比）显示所需的归一化裁剪框 "L,T,W,H"。
// 用于「替换图片·保持原显示框比例」：新图与旧框比例一致 → 返回 null（不裁、保持干净 Markdown）；
// 否则保留长边、从中心切掉多出来的那维（绝不放大，只选子区域）。
export function computeCenterCrop(natW: number, natH: number, targetAspect: number): string | null {
  if (!(natW > 0 && natH > 0 && targetAspect > 0)) return null;
  const newAspect = natW / natH;
  if (Math.abs(newAspect - targetAspect) / targetAspect < 0.01) return null; // 比例已≈一致 → 不裁
  let L = 0, T = 0, W = 1, H = 1;
  if (newAspect > targetAspect) { W = targetAspect / newAspect; L = (1 - W) / 2; } // 图更宽 → 切两侧
  else { H = newAspect / targetAspect; T = (1 - H) / 2; }                          // 图更高 → 切上下
  const f = (n: number) => Number(n.toFixed(4));
  return `${f(L)},${f(T)},${f(W)},${f(H)}`;
}

// 从 CSS 长度串（"8px"/"8"）取数字，取不到返回 null。
function parseLen(v: string | null | undefined): number | null {
  if (!v) return null;
  const m = /(-?[0-9.]+)/.exec(v);
  return m ? Number(m[1]) : null;
}

// ===== 节点 schema =====
export const imageNode = $nodeSchema('image', () => ({
  // **行内原子节点**（2026-06 最终定稿，见 docs/图片功能计划.md「行内 + 严格独占一行」）：图片是行内 atom，
  // 但靠 createEditor 的独占逻辑保证「一张图独占一个段落」。这样兼得两边的好处：
  //   · 删空行 = PM 原生段落合并（图前/两图间/末尾的空行都能自然删掉，零掩盖代码）——块级 atom 做不到这点；
  //   · 图前 = 段落 offset 0（图左侧）、图后 = offset 1（图右侧），与用户「点图左→光标在前、点图右→光标在后」的
  //     直觉天然一致（块级的「前/后」只能落在上一段/下一段，空间错位、反直觉）。
  // 贴图竖线（光标可见）由 createEditor 的 imageCaretPlugin 自绘；点击定位由 imageClickPlugin（绿/蓝半区）；
  // 「图旁打字→另起一行」由独占逻辑（proactive handleTextInput + 兜底 appendTransaction）保证。
  // 序列化：普通图就是 mdast `image`（![]()，本就在 paragraph 内），无需提升；带样式图存行级 `<img>` HTML。
  inline: true,
  group: 'inline',
  atom: true,
  marks: '',
  selectable: true,
  draggable: false, // 拖拽改走自绘指针拖拽（imageDrag.ts，支持拖入图册），不用 PM 原生 node drag
  attrs: {
    src: { default: '' },
    alt: { default: '' },
    title: { default: null },
    width: { default: null },
    align: { default: null },
    radius: { default: null },
    border: { default: null },
    crop: { default: null },
    flipH: { default: false },
    flipV: { default: false },
  },
  parseDOM: [
    {
      tag: 'img[src]',
      getAttrs: (dom: HTMLElement) => {
        // 内部复制：优先读 data-src（原始路径），避免把显示 URL 当路径；外部粘贴的 <img> 读 src。
        const stored = dom.getAttribute('data-src');
        const src = stored ?? dom.getAttribute('src') ?? '';
        // width 存在外层 wrap 的 style.width 上（行宽百分比）；内部复制时 wrap 同在，故优先读 wrap，
        // 退回 img 自身 style/width 属性（外部粘贴的 <img>）。radius/border 一律读 img 自身的内联样式。
        const wrap = dom.closest('.mica-image-wrap') as HTMLElement | null;
        const width = wrap?.style.width || dom.style.width || dom.getAttribute('width') || null;
        const flip = dom.dataset.flip ?? wrap?.dataset.flip ?? '';
        return {
          src,
          alt: dom.getAttribute('alt') ?? '',
          title: dom.getAttribute('title'),
          width,
          align: dom.dataset.align ?? wrap?.dataset.align ?? null,
          radius: parseLen(dom.style.borderRadius),
          border: parseLen(dom.style.borderWidth),
          crop: dom.dataset.crop ?? wrap?.dataset.crop ?? null,
          flipH: flip.includes('h'),
          flipV: flip.includes('v'),
        };
      },
    },
  ],
  toDOM: (node: ProseNode) => {
    const { src, alt, title, width, align, radius, border, crop, flipH, flipV } = node.attrs as unknown as ImageAttrs;
    const attrs: Record<string, string> = {
      src: imageDisplayUrl(src),
      'data-src': src,
      class: 'mica-image',
    };
    if (alt) attrs.alt = alt;
    if (title) attrs.title = title;
    if (align) attrs['data-align'] = align;
    if (crop) attrs['data-crop'] = crop; // 裁剪信息随 DOM 往返（复制/parseDOM）；实际裁剪视觉由 NodeView 渲染
    // 圆角/边框是「图片本身的视觉」→ 挂 img 的内联样式。border 不写颜色 → 用 currentColor（随正文色自动明暗，
    // 且 `<img>` 存进 .md 后别的渲染器也认）。
    const imgStyle: string[] = [];
    if (radius != null) imgStyle.push(`border-radius:${radius}px`);
    if (border != null) imgStyle.push(`border:${border}px solid`);
    if (imgStyle.length) attrs.style = imgStyle.join(';') + ';';
    // 外层 span（行内容器）：图片是行内 atom，独占一行靠独占逻辑保证；居中/对齐由 CSS 据父段落 + data-align 控制。
    // **显示宽度挂 wrap**（而非 img）：wrap 的包含块是段落=行宽，故 width:60% = 行宽的 60%，居中/对齐由 text-align
    //   带着 wrap 走（img width:100% 填满 wrap）；若挂 img 则百分比基准是 shrink-wrap 的 wrap、循环依赖、不稳。
    const wrap: Record<string, string> = { class: 'mica-image-wrap' };
    if (align) wrap['data-align'] = align;
    // 翻转挂 wrap（transform + data-flip）：与裁剪同层，裁剪区随之镜像、位置不变。
    const flipStr = flipDataAttr(flipH, flipV);
    if (flipStr) wrap['data-flip'] = flipStr;
    const wrapStyle: string[] = [];
    if (width) wrapStyle.push(`width:${width}`);
    const tf = flipCss(flipH, flipV);
    if (tf) wrapStyle.push(`transform:${tf}`);
    if (wrapStyle.length) wrap.style = wrapStyle.join(';') + ';';
    return ['span', wrap, ['img', attrs]];
  },
  parseMarkdown: {
    match: (node) => node.type === 'image',
    runner: (state, node, type) => {
      // width/align 仅当该 image 是由「带样式 <img> HTML」经 micaImageRemark 转来时才有（暂存在 node.data）。
      const data = (node.data as Record<string, unknown> | undefined) ?? {};
      state.addNode(type, {
        src: (node.url as string | undefined) ?? '',
        alt: (node.alt as string | undefined) ?? '',
        title: (node.title as string | undefined) ?? null,
        width: (data.micaWidth as string | undefined) ?? null,
        align: (data.micaAlign as string | undefined) ?? null,
        radius: (data.micaRadius as number | undefined) ?? null,
        border: (data.micaBorder as number | undefined) ?? null,
        crop: (data.micaCrop as string | undefined) ?? null,
        flipH: (data.micaFlipH as boolean | undefined) ?? false,
        flipV: (data.micaFlipV as boolean | undefined) ?? false,
      });
    },
  },
  toMarkdown: {
    match: (node) => node.type.name === 'image',
    runner: (state, node) => {
      const a = node.attrs as unknown as ImageAttrs;
      if (isStyled(a)) {
        // 带样式 → <img> HTML（往返时由 micaImageRemark 还原成 image 节点）。image 是行内节点，此刻序列化 state
        // 已在其所在 paragraph 内，故直接 addNode('html')，remark 会就地 stringify 成一行 `<img …>`。
        state.addNode('html', undefined, serializeImg(a));
      } else {
        // 普通图 → 干净的 ![alt](url "title")。image 是**行内** PM 节点、本就在段落里，直接 addNode 即可
        // （不再像块级那样包 paragraph）。
        state.addNode('image', undefined, undefined, {
          url: a.src,
          alt: a.alt || '',
          title: a.title || null,
        });
      }
    },
  },
}));

// 带样式的图 → 行内 <img> HTML 串。width/radius/border 都写进 style（便携、别的渲染器也认），align 用 data-align。
// 导出供图片组（imageGroup）序列化子图复用。
export function serializeImg(a: ImageAttrs): string {
  const parts: string[] = [`src="${escapeAttr(a.src)}"`];
  if (a.alt) parts.push(`alt="${escapeAttr(a.alt)}"`);
  if (a.title) parts.push(`title="${escapeAttr(a.title)}"`);
  const styles: string[] = [];
  if (a.width) styles.push(`width:${a.width}`);
  if (a.radius != null) styles.push(`border-radius:${a.radius}px`);
  if (a.border != null) styles.push(`border:${a.border}px solid`);
  if (styles.length) parts.push(`style="${escapeAttr(styles.join(';'))}"`);
  if (a.align) parts.push(`data-align="${escapeAttr(a.align)}"`);
  // 裁剪走 data-crop（非标准 CSS 属性，无法塞进 style）；别的渲染器忽略它、显示全图，原图文件本就完整 → 优雅降级。
  if (a.crop) parts.push(`data-crop="${escapeAttr(a.crop)}"`);
  // 翻转走 data-flip（同理无法进 style，别的渲染器忽略 → 显示原向，优雅降级）。
  const flipStr = flipDataAttr(a.flipH, a.flipV);
  if (flipStr) parts.push(`data-flip="${flipStr}"`);
  return `<img ${parts.join(' ')}>`;
}

// ===== 从 <img> HTML 串解析属性（供 remark 变换还原） =====
function attr(html: string, name: string): string | null {
  const m = html.match(new RegExp(`\\b${name}\\s*=\\s*"([^"]*)"`, 'i'));
  return m ? decodeEntities(m[1]) : null;
}
export function parseImgHtml(html: string): ImageAttrs | null {
  if (!/^\s*<img\b/i.test(html)) return null;
  const src = attr(html, 'src');
  if (src == null) return null;
  const style = attr(html, 'style') ?? '';
  // width 排除 border-width（用否定前瞻避免误命中 `border-width:`）。
  const widthFromStyle = style.match(/(?:^|;)\s*width\s*:\s*([^;]+)/i)?.[1]?.trim() ?? null;
  const radiusFromStyle = parseLen(style.match(/border-radius\s*:\s*([^;]+)/i)?.[1] ?? null);
  const borderFromStyle = parseLen(style.match(/border(?:-width)?\s*:\s*([^;]+)/i)?.[1] ?? null);
  const flip = attr(html, 'data-flip') ?? '';
  return {
    src,
    alt: attr(html, 'alt') ?? '',
    title: attr(html, 'title'),
    width: widthFromStyle ?? attr(html, 'width'),
    align: attr(html, 'data-align'),
    radius: radiusFromStyle,
    border: borderFromStyle,
    crop: attr(html, 'data-crop'),
    flipH: flip.includes('h'),
    flipV: flip.includes('v'),
  };
}

// ===== remark 变换：把 <img> HTML 节点还原成 mdast image 节点 =====
// 普通图本就以 mdast `image` 存在（![]()），无需处理；只有「带样式存成 <img> HTML」的图，remark 会解析成
// html 节点（行内在段落内、块级直接挂 root），这里统一转回 image 节点，好让上面的 parseMarkdown 认领。
interface MdNode {
  type: string;
  value?: string;
  url?: string;
  alt?: string;
  title?: string | null;
  data?: Record<string, unknown>;
  children?: MdNode[];
}

function isImgHtmlNode(n: MdNode): boolean {
  return n.type === 'html' && typeof n.value === 'string' && /^\s*<img\b/i.test(n.value);
}

// 把解析出的样式塞进 mdast image 节点的 data（hName/hProperties 不走，这里用自定义字段，
// 经 parseMarkdown 时我们读不到 data——故带样式的图改走「仍保留为 html」更稳）。
// 实际策略：带样式 <img> 直接保留成 image 节点 + 把 width/align 暂存 node.data，由 parseMarkdown 读。
function imgHtmlToImageNode(html: string): MdNode | null {
  const a = parseImgHtml(html);
  if (!a) return null;
  return {
    type: 'image',
    url: a.src,
    alt: a.alt,
    title: a.title,
    data: {
      micaWidth: a.width, micaAlign: a.align, micaRadius: a.radius, micaBorder: a.border, micaCrop: a.crop,
      micaFlipH: a.flipH, micaFlipV: a.flipV,
    },
  };
}

// 图片是**行内** PM 节点（image 是 mdast phrasing，本就在 paragraph 内）。普通图 `![]()` 解析后已是 paragraph>image，
// 无需任何处理；只需把「带样式存成 <img> HTML」的图还原成 image：
//   · 块级 <img> HTML（独占一行、remark 解析成 root 下的 html 块）→ 包成 paragraph[image]（行内图独占一行）；
//   · 行内 <img> HTML（夹在段落文字间）→ 就地换成 image 节点。
function transformTree(node: MdNode): void {
  if (!node.children) return;
  const out: MdNode[] = [];
  for (const child of node.children) {
    // 块级 <img> HTML（直接在 root/blockquote/list 等块容器下）→ 段落[image]。
    if (isImgHtmlNode(child)) {
      const img = imgHtmlToImageNode(child.value!);
      out.push(img ? { type: 'paragraph', children: [img] } : child);
      continue;
    }
    if (child.children) {
      // 段落/其它容器：把行内 <img> HTML 子节点就地换成 image，再递归处理更深层（blockquote/list 内）。
      child.children = child.children.map((g) =>
        isImgHtmlNode(g) ? (imgHtmlToImageNode(g.value!) ?? g) : g,
      );
      transformTree(child);
    }
    out.push(child);
  }
  node.children = out;
}

export const micaImageRemark = $remark('micaImage', () => () => (tree: unknown) => {
  transformTree(tree as MdNode);
});

// ===== `![alt](url)` 闭合输入规则 → 图片节点（对标行内代码的闭合触发） =====
// 用户手敲标准 Markdown 图片语法，敲到闭合 `)` 时升格为图片节点。绝大多数情况是粘贴，故这是兜底。
export const imageInputRule = new InputRule(
  /!\[([^\]]*)\]\(([^)\s]+)(?:\s+"([^"]*)")?\)$/,
  (state, match, start, end) => {
    const type = state.schema.nodes.image;
    if (!type) return null;
    const [, alt, url, title] = match;
    if (!url) return null;
    const $start = state.doc.resolve(start);
    if (!$start.parent.inlineContent) return null;
    // image 是行内节点：直接用它替换刚敲的 markdown 文本（光标自动落到图后）。若该段还有别的文字，
    // createEditor 的独占逻辑（appendTransaction）会把图拆成独占一行。
    const img = type.create({ src: url, alt: alt ?? '', title: title ?? null });
    return state.tr.replaceWith(start, end, img);
  },
);

// ===== 图片 NodeView：属性变化时**原地改 style、绝不重设 src** =====
// 关键性能修复：没有 nodeView 时，改 width/align/radius/border 走 setNodeMarkup，PM 默认会按 toDOM **重建整个图片
// DOM（新建 <img>）→ 浏览器重新加载/解码图片** → 浮动条上调圆角/边框/宽度有肉眼可见的卡顿与闪烁。用 nodeView 的
// update() 在**同一个 <img> 元素**上打补丁（src 不变就不碰），样式改动即时生效、零重载。
// 注：toDOM 仍保留（用于复制到剪贴板时的序列化 + parseDOM 往返），与此处渲染逻辑一致。
// 一张图的「wrap + img(+裁剪内层盒)」渲染器（从旧 createImageNodeView 抽出）。**独立图与图片组子图共用**：
//   · 独立 image 节点的 NodeView 用 mode='standalone'（宽度走 attrs.width）；
//   · 图片组（imageGroup，原子节点、自己掌管内部 DOM）为每张子图建一个渲染器、mode='group'（宽度交给 flex，
//     flex-grow=有效显示比例 → 单行等高）。
// 这样裁剪/翻转/圆角/边框/显示管道（img.mica.local）对组内外**完全一致**，无需在组里重写一套。
export function createImageRenderer() {
  const wrap = document.createElement('span');
  wrap.className = 'mica-image-wrap';
  const img = document.createElement('img');
  img.className = 'mica-image';
  img.draggable = false; // 禁原生图片拖拽（否则会与图册组内自绘指针拖拽重排打架、出现浏览器拖影）
  wrap.appendChild(img);
  // 裁剪时把放大的 img 包进 overflow:hidden 的内层盒（收住溢出、防横向滚动条）；非裁剪时 img 直挂 wrap。懒建。
  let clip: HTMLSpanElement | null = null;

  const render = (a: ImageAttrs, mode: 'standalone' | 'group') => {
    const grp = mode === 'group';
    const url = imageDisplayUrl(a.src);
    if (img.getAttribute('src') !== url) img.setAttribute('src', url); // 仅 src 真变才设 → 不触发重新加载
    img.setAttribute('data-src', a.src);
    if (a.alt) img.setAttribute('alt', a.alt); else img.removeAttribute('alt');
    if (a.title) img.setAttribute('title', a.title); else img.removeAttribute('title');
    // 裁剪视觉：img 绝对定位 + 放大（width=1/W、左移 L/W、上移 T/H，基准是内层盒宽/高），内层 .mica-image-clip
    //   （overflow:hidden）裁到可视窗。放大的绝对 img 会撑出文档（clip-path 只裁视觉不裁布局），故必须用 overflow:hidden
    //   的盒子「实裁」；该盒子是**内层**、不是 wrap——选中框/翻转挂 wrap 才不被裁。圆角/边框：未裁剪挂 img；裁剪图圆角
    //   挂内层盒、边框挂 wrap。
    const crop = parseCropAttr(a.crop);
    let s = '';
    if (crop) {
      s += `position:absolute;height:auto;max-width:none;`
        + `width:${(100 / crop.W).toFixed(4)}%;`
        + `left:${(-crop.L / crop.W * 100).toFixed(4)}%;`
        + `top:${(-crop.T / crop.H * 100).toFixed(4)}%;`;
    } else {
      if (a.radius != null) s += `border-radius:${a.radius}px;`;
      if (a.border != null) s += `border:${a.border}px solid;`;
    }
    img.style.cssText = s;
    if (crop) {
      if (!clip) { clip = document.createElement('span'); clip.className = 'mica-image-clip'; }
      if (img.parentElement !== clip) clip.appendChild(img);
      if (clip.parentElement !== wrap) wrap.appendChild(clip);
      clip.style.cssText = `position:absolute;inset:0;overflow:hidden;border-radius:${a.radius != null ? a.radius : 0}px;`;
    } else if (clip && clip.parentElement === wrap) {
      wrap.appendChild(img);   // 先把 img 移回 wrap（脱离 clip）
      wrap.removeChild(clip);
    }
    // 组内：大小由 flex 控制（flex-grow=有效显示比例），不设 width。独立图：宽度走 wrap style.width。
    wrap.style.width = grp ? '' : (a.width || '');
    if (!grp) wrap.style.flexGrow = ''; // 独立图清掉残留 flex-grow（组内由下方 onMetrics 设）
    // 选中框/边框圆角永远 ≥2px（与 CSS 默认一致）：即使把图片圆角调到 0，选中描边仍保留一点圆角（用户要求）。
    //   图片本身圆角按 a.radius 精确走 img/clip（0=直角），故这里只影响 wrap 的选中框/边框观感。
    wrap.style.borderRadius = a.radius != null ? `${Math.max(a.radius, 2)}px` : '';
    wrap.style.border = (crop && a.border != null) ? `${a.border}px solid` : '';
    if (a.align) wrap.dataset.align = a.align; else delete wrap.dataset.align;
    if (crop) { wrap.classList.add('mica-cropped'); wrap.dataset.crop = a.crop!; }
    else { wrap.classList.remove('mica-cropped'); delete wrap.dataset.crop; wrap.style.aspectRatio = ''; }
    const onMetrics = () => {
      const nw = img.naturalWidth, nh = img.naturalHeight;
      if (!(nw && nh)) return;
      const ar = crop ? (crop.W * nw) / (crop.H * nh) : nw / nh; // 有效显示宽高比（裁剪图按裁剪区）
      if (crop) wrap.style.aspectRatio = String(ar);
      if (grp) {
        wrap.style.flexGrow = String(ar);                     // 单行(row)等高：flex-grow=宽高比
        wrap.style.setProperty('--mica-aspect', String(ar));  // 多行(justified)：flex-basis=宽高比×目标行高（见 css）
      } else if (crop && !a.width) wrap.style.width = `${Math.round(nw * crop.W)}px`;
    };
    if (img.complete && img.naturalWidth) onMetrics();
    else img.addEventListener('load', onMetrics, { once: true });
    const fd = flipDataAttr(a.flipH, a.flipV);
    wrap.style.transform = flipCss(a.flipH, a.flipV);
    if (fd) wrap.dataset.flip = fd; else delete wrap.dataset.flip;
  };
  return { wrap, img, render };
}

// 独立图片节点的 NodeView：属性变化时原地补丁、绝不重设 src（见上「关键性能修复」）。
export function createImageNodeView() {
  return (node: ProseNode, view: EditorView, getPos: () => number | undefined): NodeView => {
    const r = createImageRenderer();
    r.render(node.attrs as unknown as ImageAttrs, 'standalone');
    // 拖拽：无修饰键左键按下→自绘指针拖拽（imageDrag）。超阈值才接管，普通点击仍由 imageClickPlugin 选中图。
    r.wrap.addEventListener('mousedown', (e) => {
      if (e.button !== 0 || e.shiftKey || e.ctrlKey || e.metaKey) return;
      startImageDrag(view, e, { kind: 'image', getPos });
    });
    return {
      dom: r.wrap,
      update: (n: ProseNode) => {
        if (n.type.name !== 'image') return false;
        r.render(n.attrs as unknown as ImageAttrs, 'standalone');
        return true; // 原地更新成功 → PM 不重建 DOM
      },
      ignoreMutation: () => true, // 原子节点，无内部可编辑内容
    };
  };
}

// 在当前选区处插入一张图片（供粘贴/拖入图片落盘后调用）。
export function buildImageNode(
  schema: import('@milkdown/prose/model').Schema,
  src: string,
  alt = '',
): ProseNode | null {
  const type = schema.nodes.image;
  if (!type) return null;
  return type.create({ src, alt, title: null, width: null, align: null });
}
