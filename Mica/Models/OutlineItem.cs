using Microsoft.UI.Xaml;

namespace Mica.Models;

// One heading in the outline of the currently open file. Pushed from the editor
// over IPC (editor.outline); Index is the heading's position among all headings
// in document order, used to scroll back to it (editor.scrollTo).
public sealed class OutlineItem
{
    public required int Level { get; init; }
    public required string Text { get; init; }
    public required int Index { get; init; }

    // Indent deeper headings so the structure reads as a tree.
    public Thickness Indent => new((Level - 1) * 14, 2, 4, 2);

    // 字号随层级递减，让大纲读起来有标题层次（H1 最大、H4+ 收敛到同一档）。
    public double FontSize => Level switch
    {
        1 => 14,
        2 => 13.5,
        3 => 13,
        _ => 12.5,
    };

    // H1/H2 半粗，其余常规——配合字号一起拉开层级，但保持克制不喧宾夺主。
    public Windows.UI.Text.FontWeight FontWeight =>
        Level <= 2 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
}
