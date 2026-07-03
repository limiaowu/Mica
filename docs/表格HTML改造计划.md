# 表格 HTML 改造计划（2026-06 重写）

> 上一版计划把 WinUI 预览理解成「与 Web 双向同步」而过度复杂化。**本版纠正**：WinUI 插入对话框只是
> **单向启动器**（初始化表格用，确定后生成一次，之后不再与编辑器内的表格同步）。

## 总目标

GFM 管道表格无法表达**合并单元格 / 多级表头 / 每表样式**，故**全面改用 HTML 表格**（用户已认可
「统一用 HTML、不走 markdown 语法也行」）。存储 = `.md` 里嵌真实 `<table>`：

- **结构性**（colspan/rowspan、列对齐 `text-align`、整表对齐 margin）→ **标准内联 HTML/style**，
  GitHub/Typora/Obsidian 都能渲染，可移植。
- **装饰性**（圆角、线宽、三线表预设）→ **`data-` 属性 + class**（`.md` 干净、每表单独设），
  别的渲染器忽略 → 显示为普通表格，但内容/合并/对齐完整。
- **无旧 GFM 管道表格需保留**（用户确认）；但保留「粘贴 markdown 管道表格」自动转 HTML 表格的能力。

## 技术方案（关键决策，别再绕）

### Web 端（编辑器内核）= `web/src/editor/htmlTable.ts`

- 用 `prosemirror-tables` 的 `tableNodes()` 自建节点：`table` / `table_row` / `table_cell` / `table_header`
  （自带 rowspan/colspan/colwidth + `mergeCells`/`splitCell`/`CellSelection`/`columnResizing`/`tableEditing`）。
  **无需新依赖**（`@milkdown/prose/tables` 已随 gfm 装）。
- **不强制表头行**（gfm 才强制 `table_header_row` 必须第一行）。行内 `table_cell`(td)/`table_header`(th)
  可任意混排 → 天然支持多级表头、任意位置表头。
- **序列化（PM→md）**：`table` 节点的 `toMarkdown` runner 自己把整棵表拼成 `<table>…</table>` 字符串
  （**无空行**，否则 remark 会把它拆成多个 html 节点），用 `state.addNode('html', undefined, htmlStr)`
  发出。单元格**内部内容**用 `DOMSerializer.fromSchema(schema)` 序列化（正确处理 strong/em/link 等标记），
  单段落 `<p>` 解包成纯内联。row/cell 节点的 toMarkdown 用 `match:()=>false` 占位（整表已由 table 节点输出，
  序列化器不会单独访问它们）。
- **反序列化（md→PM）**：
  1. `$remark` 变换器遍历 mdast，把 `<table` 开头的 `html` 节点**改类型**为 `mica_html_table`；
     顺带把（粘贴产生的）`table` mdast 节点也渲染成 HTML 串、改类型为 `mica_html_table`。
     —— 这样避开「commonmark 的 html 节点匹配任意 `type==='html'`」的冲突。
  2. `table` 节点的 `parseMarkdown.match` = `node.type==='mica_html_table'`；runner 用
     `DOMParser.fromSchema(schema).parse(<div包着table>)` 还原成 PM table 节点，再 `state.push(node)` 注入。
- **从 gfm 里按引用剔除**：`tableSchema/tableHeaderRowSchema/tableRowSchema/tableHeaderSchema/tableCellSchema`
  + `keepTableAlignPlugin/autoInsertSpanPlugin/tableEditingPlugin`（**保留 `remarkGFMPlugin`**，删除线/任务列表/
  autolink 仍需要它）。剔除手法同 inlineCode：把元组型导出展开后排除（见 createEditor 的 `commonmarkNoInlineCode`）。
- 注册 `$prose(() => tableEditing())` + `$prose(() => columnResizing())`。
- 列对齐：**无** keepTableAlignPlugin，故设列对齐要**逐个写该列所有单元格**（用 `TableMap` 找整列），
  不能只写表头。

### WinUI 端（弹窗 + 右键菜单走原生，其余 web）

- **右键菜单**（已是原生 MenuFlyout，`MainWindow.Table.cs` + `TableMenuHandler`）：增删行列/对齐/删表
  已通；本轮加**合并/拆分单元格**项（`editor.tableOp` 加 `mergeCells`/`splitCell`）。多级表头后不再需要
  `inHeader` 守卫（行操作都安全）。
- **插入对话框**待重做成**真实交互预览启动器**：行/列/表头/圆角/线宽/整表对齐/预设样式（三线表等）+
  可加行加列、可框选相邻单元格合并的真实预览 → 确定后生成「表格 spec」经 `editor.insertTable` 发给 web。

### 浮动工具条（Web 渲染，对标 Typora）

- 悬停表格上方显示小工具条：整表左/中/右对齐等。**用 web 实现**（跟随表格位置/滚动，WinUI 浮层难定位）。

## 分阶段任务清单（勾掉已完成）

- [x] **P1 Web 核心**（2026-06 完成，tsc + 整体 build 通过）：`htmlTable.ts`（节点 + 序列化 + 反序列化 +
      remark 变换 + buildTableNode + runTableOp）；集成进 createEditor（剔除 gfm 表格一组、注册新节点/remark/
      tableEditing/Tab 导航、重写 `insertTable(spec)`）；`tableSetup.ts` 改用新节点名（th=表头）+ 保留 CellSelection；
      editor.css 适配每表 border-width/radius + 三线表预设；右键菜单加合并/拆分/整表对齐；菜单去省略号。
      **未运行时验证**（用户测试）。
- [x] **P2 WinUI 启动器对话框**（2026-06 完成）：交互式预览——行/列数 + +行/+列（边缘追加保留合并）+
      点选相邻格合并/拆分；选项：首行表头开关、内容对齐、整表宽度(占满/自适应)、整表对齐、样式预设
      (普通/三线表/全框线/无框线/斑马纹/彩虹)、圆角、线宽。**确定后直接拼 `<table>` HTML 发 `editor.insertTable {html}`**
      → web `parseHtmlTable` 还原插入（启动器不再走 buildTableNode）。**遗留**：启动器里只有「全表统一内容对齐」，
      未做「逐列对齐」（插入后可右键改）；改尺寸会清空已设合并（+行/+列 保留）。
- [x] **P2 对话框打磨**（2026-06）：① 数字输入改**自定义 `NumStepper`**（用 TextBox + 竖排 ▲▼ RepeatButton，
      **不用 NumberBox**——其自带 × 清除按钮把窄框里的数字挡没了；数字居中显示、无清除按钮）；上下限夹取，
      越界经 `LimitHit` 在预览上方黄字提醒（行≤50/列≤20/圆角0–24/线宽0–6；+行/+列 同限，**web 端右键加行列无限**）。
      ② 三列等宽 `Cols3` 布局，控件铺满不留白。③ 插入/取消按钮用 `ShortButtonStyle`（基于框架默认样式保留强调色 +
      居中定宽 124）收窄。④ **预览真实反映样式**：`tableFrame` 按预设画整表边框/圆角/宽度对齐，单元格按预设画
      内框线 + 表头/斑马/彩虹底色；占满→星列铺满，自适应→Auto 列 + 按整表对齐定位。⑤ 单元格背景一律给透明刷
      （`Argb(0,…)`）以保证**整格可点击命中**（修「只点中间小条才选中」——`Background=null` 的 Border 空白区不命中）。
- [x] **P3 右键菜单**（2026-06 完成）：合并/拆分已接通（`mergeCells`/`splitCell`）；表头上方插入行已对标 Typora
      （新行变表头、原表头降普通行，`addRowBeforeSmart`）；选区高亮改 `::after` 叠加层（修 hover 灰盖蓝）。
      整表对齐/宽度子菜单**已从右键移除**（移到浮动条，分工明确：右键只管行/列/单元格）。
- [x] **P4 浮动工具条（web）**（2026-06 完成）：鼠标悬停某张表 → 其上方浮出工具条（`web/src/editor/tableToolbar.ts`），
      管「整表的事」：整表对齐（左/中/右，占满时灰掉）、整表宽度（占满/自适应）、**行列数步进器**（边缘增减——
      加列追加最右、加行追加最底；减则从右/下边缘删，最大保留内容与合并）、样式预设（下拉）、圆角/线宽步进器、
      删除整表。纯 CSS + 内联 SVG（无 UI 框架），`flex-wrap` 窄屏自动折行。全 web 内执行（无 IPC），按
      `view.posAtDOM` 拿到悬停表 pos，经 htmlTable 的 `setTableAttrsAt`/`resizeTableAt`/`getTableInfoAt` 操作。
- [ ] **P3 余项（未做）**：拖动行/列重排序 + 右键「上/下移行、左/右移列」（`moveTableRow`/`moveTableColumn`）。
- [ ] **P5 收尾**：`|a|b|` 输入规则（打字即转 HTML 表格）；段落>表格 二级菜单（插入表格入口归类）。
      注：原全局 `EditorTableRadius` 设置项已删除，圆角改为每表属性（浮动条/启动器设），故 P5 不再涉及「新表默认圆角」。

## 已知遗留 / 坑

- 单元格内的行内公式/行内代码（自定义原子节点）经 HTML 往返可能不完美，v1 先保证纯文本 + strong/em/link/
  删除线可靠；复杂嵌入留后。
- 高缩放下宽表格不居中（旧遗留）。
- 序列化的 `<table>` 必须**无空行**，否则 remark 把它拆成多个 html 节点导致解析失败。
