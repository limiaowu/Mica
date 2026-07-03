// 一次性脚本：把 icon.md 里的「描边线稿」表格图标转成 PathIcon 可用的「填充」几何。
// WinUI PathIcon 用 Foreground 填充路径（不描边），故描边矩形→四条细填充条、线→细填充条、对角线→细四边形。
// 原图坐标系 0–20，按 ×0.8 缩到 0–16（贴近现有 FontSize 15 的字体图标尺寸；PathIcon 不会自动缩放）。
// 输出每个图标的 path 数据字符串，手动粘进 MainWindow.Table.cs 的字典。

const S = 0.5;
const r2 = (n) => Math.round(n * 100) / 100;
const sx = (v) => r2(v * S);
const P = (x, y) => `${sx(x)},${sx(y)}`;

// 轴对齐填充矩形（原坐标 x1,y1–x2,y2）
const rect = (x1, y1, x2, y2) => `M${P(x1, y1)} H${sx(x2)} V${sx(y2)} H${sx(x1)} Z`;
// 水平/垂直细线（中心线 + 厚度 t，原坐标）
const hline = (x1, x2, y, t) => rect(x1, y - t / 2, x2, y + t / 2);
const vline = (y1, y2, x, t) => rect(x - t / 2, y1, x + t / 2, y2);
// 描边矩形 → 四条细填充条（厚度 t 向内）
const frame = (x1, y1, x2, y2, t) =>
  [rect(x1, y1, x2, y1 + t), rect(x1, y2 - t, x2, y2), rect(x1, y1, x1 + t, y2), rect(x2 - t, y1, x2, y2)].join(' ');
// 对角线 → 细四边形（厚度 t）
const diag = (ax, ay, bx, by, t) => {
  const dx = bx - ax, dy = by - ay, len = Math.hypot(dx, dy);
  const nx = (-dy / len) * (t / 2), ny = (dx / len) * (t / 2);
  return `M${P(ax + nx, ay + ny)} L${P(bx + nx, by + ny)} L${P(bx - nx, by - ny)} L${P(ax - nx, ay - ny)} Z`;
};

const T = 0.5;  // 主线宽（原图 stroke-width）
const T2 = 1; // 删除表格斜线 / 对齐参考线更粗

const icons = {
  // 插入：实体表块 + 待插入的「新行/列」（带 + 号）
  insert_row_above: [frame(3, 9.5, 17, 17, T), vline(9.5, 17, 10, T), frame(3, 3, 17, 8, T), vline(4.5, 6.5, 10, T), hline(9, 11, 5.5, T)],
  insert_row_below: [frame(3, 3, 17, 10.5, T), vline(3, 10.5, 10, T), frame(3, 12, 17, 17, T), vline(13.5, 15.5, 10, T), hline(9, 11, 14.5, T)],
  insert_col_left:  [frame(9.5, 3, 17, 17, T), hline(9.5, 17, 10, T), frame(3, 3, 8, 17, T), vline(9, 11, 5.5, T), hline(4.5, 6.5, 10, T)],
  insert_col_right: [frame(3, 3, 10.5, 17, T), hline(3, 10.5, 10, T), frame(12, 3, 17, 17, T), vline(9, 11, 14.5, T), hline(13.5, 15.5, 10, T)],
  // 删除行/列：三格线 + 中间叉；删除表格：田字格 + 贯穿斜线
  delete_row:   [frame(3, 4, 17, 16, T), hline(3, 17, 8, T), hline(3, 17, 12, T), diag(8.5, 8.5, 11.5, 11.5, T), diag(11.5, 8.5, 8.5, 11.5, T)],
  delete_col:   [frame(4, 3, 16, 17, T), vline(3, 17, 8, T), vline(3, 17, 12, T), diag(8.5, 8.5, 11.5, 11.5, T), diag(11.5, 8.5, 8.5, 11.5, T)],
  delete_table: [frame(4, 4, 16, 16, T), vline(4, 16, 10, T), hline(4, 16, 10, T), diag(2, 18, 18, 2, T2)],
  // 移动：四向箭头（横竖主轴 + 四个箭头）
  move: [
    vline(3, 17, 10, T), hline(3, 17, 10, T),
    diag(7, 6, 10, 3, T), diag(10, 3, 13, 6, T),     // 上
    diag(7, 14, 10, 17, T), diag(10, 17, 13, 14, T), // 下
    diag(6, 7, 3, 10, T), diag(3, 10, 6, 13, T),     // 左
    diag(14, 7, 17, 10, T), diag(17, 10, 14, 13, T), // 右
  ],
  // 列对齐：顶部基准线 + 三根高低错落的实心立柱（本就是填充设计）
  column_align: [hline(3, 17, 4, T2), rect(5, 6, 7.4, 16), rect(9, 6, 11.4, 12), rect(13, 6, 15.4, 14)],
};

for (const [name, parts] of Object.entries(icons)) {
  console.log(`${name}\tF1 ${parts.join(' ')}`);
}
