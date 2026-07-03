---
name: editor-architecture-decision
description: 编辑器选型（Milkdown vs CodeMirror6 vs Vditor）的分析与用户真实底线，决策待用户下次给答案
metadata:
  type: project
---

2026-06-08 用户提出：现状 Milkdown(ProseMirror) 做 Typora 式所见即所得「很拧巴」，问要不要换 CodeMirror6 或 Vditor。web 侧当时约 2280 行，重写成本尚低。

**用户的真实底线（关键）**：不在乎语法符号是不是「真实字符/可逐字选」，**只要保存正确 + 渲染正确即可**。最怕的是表格/图片/超链接等后续功能会不会重现行内代码/公式那种痛。

**我的分析结论：建议留在 Milkdown，不重写。** 理由：
- Milkdown 交税的类别很窄 = 「需要在行内露出并逐字符编辑原始语法符号」的元素，**只有行内代码 + 行内公式**，用户已用原子节点方案付完税（取舍：整体增删、不能删单个反引号——不违反「保存/渲染正确」的底线）。
- 表格/图片/超链接**都不在这个类别**：Typora 里它们也不靠行内露语法编辑（表格按网格、图片/链接走弹窗）。这些恰是 ProseMirror 主场，Milkdown 白送、且比从零 CM6 自造更好。所以「以后还会不会遇到这个痛」——不会，痛点类别已封闭。
- 换 CM6 = 为一个用户不需要的属性（真实字符），放弃最难自造的部分（可编辑高亮代码块/表格网格/嵌套列表实时渲染）。亏。Vditor 是黑盒，做精品改不动深层行为。

**自测判据**（评估任何新功能是否会撞 Milkdown 税）：编辑它需不需要让用户在行内看见并用光标穿过原始 Markdown 语法字符？需要才交税，且这类只有行内代码/公式两个、已解决。

**唯一会让我改口建议 CM6 的情形**：用户哪天想给加粗/斜体/标题也做「光标靠近露出 `**`/`#`」——那说明真实诉求是字符级保真，届时再换。按当前表述不会走到这步。

留 Milkdown 的实现建议：表格/图片/链接借鉴 crepe 组件思路（link-tooltip / image-block / table 行列控制），对齐 Typora 观感且不再产生 nodeView 苦工。

**状态（2026-06-09 第二轮）：留在 Milkdown。行内代码方案最终定为「原子节点 + 闭合反引号输入规则」。**

中间踩过的坑（务必别再绕）：第一轮我把行内代码从原子节点改成 **commonmark 原生 mark**，结果用户实测一堆 bug：`code:true` 标记**光标卡边界、进不去 `a`c`b` 缝、整行只有代码退不出来**——这些是 ProseMirror mark 的老毛病，标记方案 bug 更多。**第二轮回到原子节点**（与行内公式同构、共用 inlineEmbedView，公式那套用户确认好用），但把触发从历史的「`handleTextInput` 拦开头反引号」改成 **`InputRule(/(?:`)([^`]+)(?:`)$/)` 闭合反引号触发**——这才是修「单反引号即触发/吞 ``` 围栏/空节点」的正解。

**最终结论：行内「半渲染」元素（代码/公式）在 ProseMirror 里用原子节点最稳；标记方案的光标坑已验证，别再试。** 用户诉求 = 保存/渲染正确 + 书写流畅，不要求字符级（反引号整体增删可接受）。

本轮同时修/加：
- 导航历史去重（`NavigateTo` 比较栈顶，连点同一项不堆叠）——已验证 OK。
- **缩放快捷键血泪坑**：accelerator 不能只加到 MenuFlyoutItem 上（代码后置加的不会自动 invoke 菜单项 → 吃键不动作，三键全失效）；必须挂**根元素 `Content` + 写显式 `Invoked`**。`WireZoomAccelerators` 已改。
- 点击正文最末块下方空白 → 自动到文末（`onClickBelowContent`，对标 Typora）。
- 段落首行缩进设置（`EditorFirstLineIndent` → `firstLineIndent` → body 类 + CSS）。

**第四轮（2026-06-09）已修 item 3 + item 5**：
- **item 3（先摆好空区分符对再往里填不渲染）= `fillEmptyEmbedPair`（handleTextInput 直接 prop）**：极窄触发——输入单个非空白非区分符字符 + 光标前后是同一区分符 → 替换空对为原子节点并打开编辑。不违反「绝不拦开头区分符」（要求用户已显式摆好空对、光标在其间；输入区分符本身 return false）。三反引号围栏 / `$$ ` / 字面 `$5` 不受影响。**行内公式同理**（`$|$` 中间输入也转）。
- **item 5（`$$ ` 空公式块不自动进编辑框）= `mathBlockAutoEditPlugin`（appendTransaction）**：官方 `mathBlockInputRule` 只 setBlockType 不选中，故事后补 NodeSelection（docChanged + 光标在空 math_block 内/紧贴其前）。不替换官方规则、不误触。
- **item 4（单个反引号即自动转原子=Typora 自动补全）未做**：会落回「拦开头区分符」脆弱路径；且 `$` 自动补全会让每个「$5」变公式（用户先前就反感）。`fillEmptyEmbedPair` 已覆盖 item 3 的实际缺口，是更安全的等价收益。

**第三轮（2026-06-09）补充**：
- 行内代码空节点中间一大段空白 = `.mica-embed-src` 的 `min-width:1ch`，改 `0`（field-sizing 兜底）。
- 缩放快捷键提示气泡（指针移回编辑区就弹「Ctrl+Shift++」）= accelerator 挂根元素的副作用，`rootEl.KeyboardAcceleratorPlacementMode=Hidden` 抑制。
- **`Ctrl+Shift+0` 复位失效（放大/缩小正常）的真正根因 = Windows「切换输入法/键盘布局」热键（`Ctrl+Shift+<数字>`）在系统层吞键**，web(`e.code==='Digit0'`)/host(`VirtualKey.Number0`)代码都对、救不了；用状态栏 100% 或菜单「实际大小」复位。**不要再为此加代码。**
- 首行缩进新增「格式」菜单入口（与设置页同一设置）；设置页把编辑器内容项从「外观」挪到「编辑器」分类。
- 列表在首行缩进下不额外缩进（CSS 只缩 `.ProseMirror>p`），与 Typora 一致，用户认可、不改。
- **行内代码 Typora 式「自动补全反引号 + 边打边渲染」未做**：会重新落回「拦截开头反引号按键」的脆弱路径（历史一切 bug 的根因），违反「别引入未知 bug」。当前「完整打 `` `code` `` 闭合即转节点」已稳定，用户认可「没完美方案就算了」。先打两个反引号再往里填不渲染（item 2）= 同理不修。

详见 CLAUDE.md「公式=KaTeX，代码块=CodeMirror 6」章节。
