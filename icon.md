为了满足您在 WinUI 3 软件开发中的设计需求，我为您精心设计了这一整套**符合 WinUI 3 (Segoe Fluent Icons) 简约、单线（Monoline）风格的图标**。

在 WinUI 3 中，最优雅且性能最好的图标实现方式是使用 **`<Path>` 配合 `<Viewbox>`**（而不是直接载入外部 `.svg` 文件）。这种方式可以让图标完美支持系统的深色/浅色主题（通过绑定 `Foreground`），并且在任何高 DPI 屏幕下都绝不模糊。

以下为您提供这套图标的 **WinUI 3 XAML 直接可用代码**，以及对应的 **标准 SVG 源码** 和 **Segoe 字体替代方案**。

---

### 一、 WinUI 3 极简矢量 XAML 代码（推荐，直接复制可用）

这些代码在 `20x20` 的视口中进行精密对齐，线条粗细设定为 `1.2` 像素，边缘带有自然的微圆角（Fluent 风格）。

#### 1. 插入系列 (Insert Group)
通过“虚线”代表即将插入的新行/新列，并带有精致的 `+` 号定位，能非常直观地看出方向和行列的区别：

*   **在上方插入行 (Insert Row Above)**
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 3,9.5 H 17 V 17 H 3 Z M 10,9.5 V 17 M 3,3 H 17 V 8 H 3 Z M 10,4.5 V 6.5 M 9,5.5 H 11"/>
    </Viewbox>
    ```

*   **在下方插入行 (Insert Row Below)**
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 3,3 H 17 V 10.5 H 3 Z M 10,3 V 10.5 M 3,12 H 17 V 17 H 3 Z M 10,13.5 V 15.5 M 9,14.5 H 11"/>
    </Viewbox>
    ```

*   **在左侧插入列 (Insert Column Left)**
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 9.5,3 H 17 V 17 H 9.5 Z M 9.5,10 H 17 M 3,3 H 8 V 17 H 3 Z M 5.5,9 V 11 M 4.5,10 H 6.5"/>
    </Viewbox>
    ```

*   **在右侧插入列 (Insert Column Right)**
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 3,3 H 10.5 V 17 H 3 Z M 3,10 H 10.5 M 12,3 H 17 V 17 H 12 Z M 14.5,9 V 11 M 13.5,10 H 15.5"/>
    </Viewbox>
    ```

#### 2. 删除系列 (Delete Group)
“删除行”和“删除列”均使用三条格线表示，在中间待删除的单元上叠放极简的 `x` 符号；“删除表格”则使用经典的“田字格”加上贯穿的倾斜删除斜线：

*   **删除行 (Delete Row)**
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 3,4 H 17 V 16 H 3 Z M 3,8 H 17 M 3,12 H 17 M 8.5,8.5 L 11.5,11.5 M 11.5,8.5 L 8.5,11.5"/>
    </Viewbox>
    ```

*   **删除列 (Delete Column)**
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 4,3 H 16 V 17 H 4 Z M 8,3 V 17 M 12,3 V 17 M 8.5,8.5 L 11.5,11.5 M 11.5,8.5 L 8.5,11.5"/>
    </Viewbox>
    ```

*   **删除表格 (Delete Table)**
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 4,4 H 16 V 16 H 4 Z M 10,4 V 16 M 4,10 H 16 M 2,18 L 18,2"/>
    </Viewbox>
    ```

#### 3. 移动与列对齐 (Move & Align) [附加提供]
*   **移动 (Move)** - 标准的 Fluent 风格四向移动箭头：
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 10,3 V 17 M 3,10 H 17 M 7,6 L 10,3 L 13,6 M 7,14 L 10,17 L 13,14 M 6,7 L 3,10 L 6,13 M 14,7 L 17,10 L 14,13"/>
    </Viewbox>
    ```

*   **列对齐 (Column Align)** - 顶部基准对齐线，下方为三列粗细一致、高低错落对齐的垂直立柱，极具学术和工业软件质感：
    ```xml
    <Viewbox Width="16" Height="16">
        <Path Stroke="{ThemeResource SystemControlForegroundBaseMediumHighBrush}" StrokeThickness="1.2" StrokeLineJoin="Round" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
              Data="M 3,4 H 17 M 5,6 H 7.4 V 16 H 5 Z M 9,6 H 11.4 V 12 H 9 Z M 13,6 H 15.4 V 14 H 13 Z"/>
    </Viewbox>
    ```

---

### 二、 如果您需要标准的 `.svg` 源码文件

如果您需要在项目中建立外部 `.svg` 资产，这些是对应的矢量源码文件（可直接放入任意文本编辑器保存为 `.svg`）：

<details>
<summary>点击展开：全套 8 个 SVG 源码文件内容</summary>

**1. insert_row_above.svg**
```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round">
  <rect x="3" y="9.5" width="14" height="7.5" rx="1"/>
  <line x1="10" y1="9.5" x2="10" y2="17"/>
  <rect x="3" y="3" width="14" height="5" rx="1" stroke-dasharray="2 1"/>
  <line x1="10" y1="4.5" x2="10" y2="6.5"/>
  <line x1="9" y1="5.5" x2="11" y2="5.5"/>
</svg>
```

**2. insert_row_below.svg**
```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round">
  <rect x="3" y="3" width="14" height="7.5" rx="1"/>
  <line x1="10" y1="3" x2="10" y2="10.5"/>
  <rect x="3" y="12" width="14" height="5" rx="1" stroke-dasharray="2 1"/>
  <line x1="10" y1="13.5" x2="10" y2="15.5"/>
  <line x1="9" y1="14.5" x2="11" y2="14.5"/>
</svg>
```

**3. insert_col_left.svg**
```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round">
  <rect x="9.5" y="3" width="7.5" height="14" rx="1"/>
  <line x1="9.5" y1="10" x2="17" y2="10"/>
  <rect x="3" y="3" width="5" height="14" rx="1" stroke-dasharray="2 1"/>
  <line x1="5.5" y1="9" x2="5.5" y2="11"/>
  <line x1="4.5" y1="10" x2="6.5" y2="10"/>
</svg>
```

**4. insert_col_right.svg**
```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round">
  <rect x="3" y="3" width="7.5" height="14" rx="1"/>
  <line x1="3" y1="10" x2="10.5" y2="10"/>
  <rect x="12" y="3" width="5" height="14" rx="1" stroke-dasharray="2 1"/>
  <line x1="14.5" y1="9" x2="14.5" y2="11"/>
  <line x1="13.5" y1="10" x2="15.5" y2="10"/>
</svg>
```

**5. delete_row.svg**
```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round">
  <rect x="3" y="4" width="14" height="12" rx="1"/>
  <line x1="3" y1="8" x2="17" y2="8"/>
  <line x1="3" y1="12" x2="17" y2="12"/>
  <line x1="8.5" y1="8.5" x2="11.5" y2="11.5"/>
  <line x1="11.5" y1="8.5" x2="8.5" y2="11.5"/>
</svg>
```

**6. delete_col.svg**
```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round">
  <rect x="4" y="3" width="12" height="14" rx="1"/>
  <line x1="8" y1="3" x2="8" y2="17"/>
  <line x1="12" y1="3" x2="12" y2="17"/>
  <line x1="8.5" y1="8.5" x2="11.5" y2="11.5"/>
  <line x1="11.5" y1="8.5" x2="8.5" y2="11.5"/>
</svg>
```

**7. delete_table.svg**
```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round">
  <rect x="4" y="4" width="12" height="12" rx="1.5"/>
  <line x1="10" y1="4" x2="10" y2="16"/>
  <line x1="4" y1="10" x2="16" y2="10"/>
  <line x1="2" y1="18" x2="18" y2="2" stroke-width="1.5"/>
</svg>
```

**8. column_align.svg**
```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 20 20" width="20" height="20" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" stroke-linejoin="round">
  <line x1="3" y1="4" x2="17" y2="4" stroke-width="1.5"/>
  <rect x="5" y="6" width="2.4" height="10" rx="0.5"/>
  <rect x="9" y="6" width="2.4" height="6" rx="0.5"/>
  <rect x="13" y="6" width="2.4" height="8" rx="0.5"/>
</svg>
```
</details>

---

### 三、 WinUI 3 系统内置图标替代方案 (FontIcon)

如果您更倾向于减少矢量代码体积、直接使用系统内置的 **Segoe Fluent Icons** 字体库，以下是 Windows 11 中最契合这几个操作的标准 Unicode（Glyph）映射表：

| 操作项                    | 内置图标名称 / 解释          | XAML 字符写法 (Glyph)                  |
| :------------------------ | :--------------------------- | :------------------------------------- |
| **通用增加 / 基础加号**   | Add (标准的细加号)           | `Glyph="&#xE710;"`                     |
| **删除表格 / 取消表格**   | Table (带有对角取消线的表格) | `Glyph="&#xE840;"` (可考虑结合 Delete) |
| **基础删除 / 基础垃圾桶** | Delete (标准的单线垃圾桶)    | `Glyph="&#xE74D;"`                     |
| **移动 (Move)**           | Move (单线标准四向移动箭头)  | `Glyph="&#xE7C2;"`                     |
| **列对齐 (Align)**        | AlignLeft (标准的左对齐段落) | `Glyph="&#xE7E2;"`                     |

*注：因为系统的默认 Segoe 字体在一些旧版 Win10 上可能会因为字体版本较低而缺失非常高级的表格专用微调图标，所以在表格菜单中，采用我上面提供的 **第一套 XAML 矢量路径数据** 能够完全消除平台兼容性隐患，确保所有用户看到的 UI 100% 一致。*