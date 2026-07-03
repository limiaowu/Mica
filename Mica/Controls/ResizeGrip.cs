using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Mica.Controls;

// A transparent Grid that shows the horizontal-resize (↔) cursor on hover, so the
// otherwise-invisible sidebar drag handle is discoverable. ProtectedCursor is only settable
// from a subclass, and Border is sealed, so we derive from Grid. Pointer drag logic lives in
// the page (OnPaneResize* in MainWindow.Sidebar.cs), updating TreePanel.Width.
public sealed partial class ResizeGrip : Grid
{
    public ResizeGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
