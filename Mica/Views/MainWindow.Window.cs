using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Mica.Models;
using Mica.Services;
using Mica.IPC;
using Mica.IPC.Handlers;
using Serilog;

namespace Mica;

// Part of the MainWindow partial class. Fullscreen/always-on-top/about + Win32 min-size subclass.
public sealed partial class MainWindow
{

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            _appWindow.SetPresenter(AppWindowPresenterKind.Default);
        else
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private void OnToggleAlwaysOnTop(object sender, RoutedEventArgs e)
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
            AlwaysOnTopMenu.IsChecked = presenter.IsAlwaysOnTop;
        }
    }

    // --- Menu: Help ---

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "关于 Mica",
            Content = "Mica — Markdown 笔记应用\n版本 0.1.0",
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // --- Minimum window size (WM_GETMINMAXINFO subclass) ---
    // The Windows App SDK has no built-in minimum size, so we subclass the
    // window proc and clamp the min track size (DPI-scaled).

    private const int MinWidthDip = 800;
    private const int MinHeightDip = 600;
    private const uint WM_GETMINMAXINFO = 0x0024;

    // Held in a field so the GC doesn't collect the delegate while native code holds it.
    private SUBCLASSPROC? _subclassProc;

    private delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void EnableMinWindowSize(IntPtr hWnd)
    {
        _subclassProc = SubclassWndProc;
        SetWindowSubclass(hWnd, _subclassProc, 1, IntPtr.Zero);
    }

    private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO)
        {
            var scale = GetDpiForWindow(hWnd) / 96.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = (int)(MinWidthDip * scale);
            mmi.ptMinTrackSize.Y = (int)(MinHeightDip * scale);
            Marshal.StructureToPtr(mmi, lParam, false);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
