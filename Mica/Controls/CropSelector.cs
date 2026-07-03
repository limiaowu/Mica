using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Mica.Controls;

// 裁剪选框控件（第三期，图片裁剪面板的核心）。在原图上叠：① 区外暗化遮罩（四块半透明黑）；② 可拖动/缩放的选框；
// ③ 8 个把手（四角 + 四边中点）。鼠标在把手附近=缩放对应边/角，在选框内=整体移动，在框外空白=从该点重新框选。
// 输出相对**显示图**的归一化矩形（L,T,W,H ∈ 0~1）——因显示是原图的均匀缩放，故等同相对原图的分数。**不改原图**。
//
// ★坐标系：选框内部坐标 _x/_y/_w/_h 用「图像空间」（0.._dispW / 0.._dispH）。Canvas 比图四周大一圈 _pad，图画在
//   (_pad,_pad)。原因：边/角把手中心落在图边缘，若 Canvas 与图等大，把手外半边会落到 Canvas 外、收不到指针
//   （用户反馈：只有靠图内那点才拖得动）。留出 _pad 后整把手都在 Canvas 命中区内。摆元素时统一 +_pad，读指针时 -_pad。
// ★比例锁 _ratio（W/H）：null=自由；非空时拖动任意把手都按该比例缩放（锚对侧/对角；边把手在交叉轴居中）。
public sealed class CropSelector : Grid
{
    private readonly Canvas _canvas = new();
    private readonly Image _image = new();
    private readonly Rectangle _maskTop = NewMask(), _maskBottom = NewMask(), _maskLeft = NewMask(), _maskRight = NewMask();
    private readonly Rectangle _selBorder = new();
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly double _dispW, _dispH;     // 显示图尺寸（DIP）

    // 原图自然像素（供「当前裁剪尺寸」换算）。
    public double NatW { get; }
    public double NatH { get; }

    // 选框（显示像素坐标，图像空间）
    private double _x, _y, _w, _h;

    private double? _ratio;                       // 锁定宽高比（W/H）；null=自由

    // 裁剪框变化时通知（信息条刷新当前尺寸）。
    public event Action? CropChanged;

    // 是否正在拖动（按下未松手）。供信息条在拖动期间跳过较重的回填（NumberBox 每次 move 改值会卡），松手再刷新。
    public bool IsDragging { get; private set; }

    private const double HandleSize = 12;
    private const double HitRadius = 14;        // 把手命中半径（略大于视觉方块，好点）
    private const double MinSize = 24;          // 选框最小边长
    private const double Pad = 16;              // 图四周留白（容纳边缘把手的命中区）

    private enum Mode { None, Move, N, S, E, W, NE, NW, SE, SW, NewDraw }
    private Mode _mode = Mode.None;
    private Point _startPtr;                     // 按下时指针（图像空间坐标）
    private double _sx, _sy, _sw, _sh;           // 按下时的选框（拖动基准）

    // 8 个把手对应的中心计算 + 模式。顺序：NW N NE E SE S SW W。
    private static readonly Mode[] HandleModes =
        { Mode.NW, Mode.N, Mode.NE, Mode.E, Mode.SE, Mode.S, Mode.SW, Mode.W };

    public CropSelector(ImageSource src, double natW, double natH, double maxW, double maxH)
    {
        NatW = natW; NatH = natH;

        var scale = Math.Min(Math.Min(maxW / natW, maxH / natH), 1.0);
        if (!(scale > 0)) scale = 1.0;
        _dispW = Math.Max(1, Math.Round(natW * scale));
        _dispH = Math.Max(1, Math.Round(natH * scale));

        _image.Source = src;
        _image.Width = _dispW; _image.Height = _dispH; _image.Stretch = Stretch.Fill;
        Canvas.SetLeft(_image, Pad); Canvas.SetTop(_image, Pad);

        // 选框边：白色细线。命中统一由 canvas 计算，自身不参与命中。
        _selBorder.Stroke = new SolidColorBrush(Colors.White);
        _selBorder.StrokeThickness = 1.4;
        _selBorder.Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // ≈透明
        _selBorder.IsHitTestVisible = false;

        _canvas.Width = _dispW + Pad * 2; _canvas.Height = _dispH + Pad * 2;
        _canvas.Background = new SolidColorBrush(Colors.Transparent);      // 整片（含留白）可接收指针
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_maskTop); _canvas.Children.Add(_maskBottom);
        _canvas.Children.Add(_maskLeft); _canvas.Children.Add(_maskRight);
        _canvas.Children.Add(_selBorder);
        for (int i = 0; i < 8; i++) { _handles[i] = NewHandle(); _canvas.Children.Add(_handles[i]); }

        // 默认选框 = 整图
        _x = 0; _y = 0; _w = _dispW; _h = _dispH;

        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_canvas);

        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.PointerCaptureLost += (_, _) => { _mode = Mode.None; IsDragging = false; };

        Layout();
    }

    private static Rectangle NewMask() => new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
        IsHitTestVisible = false,
    };
    private static Rectangle NewHandle() => new()
    {
        Width = HandleSize, Height = HandleSize,
        Fill = new SolidColorBrush(Colors.White),
        Stroke = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), StrokeThickness = 1,
        RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
    };

    // 预选已有的裁剪（右键/浮条传来的上次裁剪框），让用户在原图上接着调。
    public void SetCrop(double l, double t, double w, double h)
    {
        _x = Clamp(l, 0, 1) * _dispW; _y = Clamp(t, 0, 1) * _dispH;
        _w = Clamp(w, 0, 1) * _dispW; _h = Clamp(h, 0, 1) * _dispH;
        if (_w < MinSize) _w = Math.Min(_dispW, MinSize);
        if (_h < MinSize) _h = Math.Min(_dispH, MinSize);
        if (_x + _w > _dispW) _x = _dispW - _w;
        if (_y + _h > _dispH) _y = _dispH - _h;
        if (_x < 0) _x = 0; if (_y < 0) _y = 0;
        Layout();
    }

    // 设置/解除比例锁。设置时把当前框就地调成该比例（保持中心、收进图内）。
    public void SetAspectRatio(double? ratio)
    {
        _ratio = ratio is > 0 ? ratio : null;
        if (_ratio is double r)
        {
            double cx = _x + _w / 2, cy = _y + _h / 2;
            double newW = _w, newH = newW / r;
            if (newH > _dispH) { newH = _dispH; newW = newH * r; }
            if (newW > _dispW) { newW = _dispW; newH = newW / r; }
            _w = newW; _h = newH;
            _x = Clamp(cx - newW / 2, 0, _dispW - newW);
            _y = Clamp(cy - newH / 2, 0, _dispH - newH);
            Layout();
        }
    }

    // 当前裁剪（归一化分数）。
    public (double L, double T, double W, double H) GetCrop()
        => (_x / _dispW, _y / _dispH, _w / _dispW, _h / _dispH);

    // 当前裁剪区对应的原图像素尺寸（信息条显示用）。
    public (int W, int H) CropPixelSize()
        => ((int)Math.Round(_w / _dispW * NatW), (int)Math.Round(_h / _dispH * NatH));

    // 按原图像素尺寸设置裁剪框（信息条直接编辑用）：**从中心缩放**，钳到图内与最小尺寸。不动原图。
    public void SetCropPixelSize(int wPx, int hPx)
    {
        double dw = Clamp(wPx / NatW * _dispW, MinSize, _dispW);
        double dh = Clamp(hPx / NatH * _dispH, MinSize, _dispH);
        double cx = _x + _w / 2, cy = _y + _h / 2;
        _w = dw; _h = dh;
        _x = Clamp(cx - dw / 2, 0, _dispW - dw);
        _y = Clamp(cy - dh / 2, 0, _dispH - dh);
        Layout();
    }

    // 选框是否≈整图（用于「裁剪」时判定其实没裁 → 存 null）。
    public bool IsFullImage()
        => _x <= 1 && _y <= 1 && _w >= _dispW - 1 && _h >= _dispH - 1;

    // ===== 指针交互 =====
    private Point ToImageSpace(Point p) => new(p.X - Pad, p.Y - Pad);

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = ToImageSpace(e.GetCurrentPoint(_canvas).Position);
        _startPtr = pt;
        _sx = _x; _sy = _y; _sw = _w; _sh = _h;
        _mode = HitTest(pt);
        if (_mode == Mode.NewDraw) { _x = Clamp(pt.X, 0, _dispW); _y = Clamp(pt.Y, 0, _dispH); _w = 0; _h = 0; }
        IsDragging = true;
        _canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_mode == Mode.None) return;
        var pt = ToImageSpace(e.GetCurrentPoint(_canvas).Position);
        double dx = pt.X - _startPtr.X, dy = pt.Y - _startPtr.Y;

        if (_mode == Mode.Move)
        {
            _x = Clamp(_sx + dx, 0, _dispW - _sw);
            _y = Clamp(_sy + dy, 0, _dispH - _sh);
        }
        else if (_mode == Mode.NewDraw)
        {
            NewDrawTo(pt);
        }
        else if (_ratio is double rr)
        {
            ResizeLocked(rr, dx, dy);
        }
        else // 自由缩放：按模式动对应边，角=两边一起动
        {
            double l = _sx, t = _sy, r = _sx + _sw, b = _sy + _sh;
            if (_mode is Mode.W or Mode.NW or Mode.SW) l = Clamp(_sx + dx, 0, r - MinSize);
            if (_mode is Mode.E or Mode.NE or Mode.SE) r = Clamp(_sx + _sw + dx, l + MinSize, _dispW);
            if (_mode is Mode.N or Mode.NW or Mode.NE) t = Clamp(_sy + dy, 0, b - MinSize);
            if (_mode is Mode.S or Mode.SW or Mode.SE) b = Clamp(_sy + _sh + dy, t + MinSize, _dispH);
            _x = l; _y = t; _w = r - l; _h = b - t;
        }
        Layout();
    }

    // 比例锁缩放：角把手以水平拖动为主驱动，边把手以自身轴为主驱动；锚对侧/对角，边把手在交叉轴保持居中。
    private void ResizeLocked(double r, double dx, double dy)
    {
        double l = _sx, t = _sy, rt = _sx + _sw, b = _sy + _sh;
        bool left = _mode is Mode.W or Mode.NW or Mode.SW;
        bool right = _mode is Mode.E or Mode.NE or Mode.SE;
        bool top = _mode is Mode.N or Mode.NW or Mode.NE;
        bool bottom = _mode is Mode.S or Mode.SW or Mode.SE;

        double newW, newH;
        if (top != bottom && left == right) // 纯 N/S 边：高为主
        {
            newH = bottom ? Clamp(_sh + dy, MinSize, _dispH) : Clamp(_sh - dy, MinSize, _dispH);
            newW = newH * r;
        }
        else // 角 或 纯 E/W 边：宽为主
        {
            newW = right ? Clamp(_sw + dx, MinSize, _dispW) : Clamp(_sw - dx, MinSize, _dispW);
            newH = newW / r;
        }
        // 收进图内（保持比例）
        if (newW > _dispW) { newW = _dispW; newH = newW / r; }
        if (newH > _dispH) { newH = _dispH; newW = newH * r; }

        double nx = left ? rt - newW : right ? l : _sx + (_sw - newW) / 2;   // N/S 边水平居中
        double ny = top ? b - newH : bottom ? t : _sy + (_sh - newH) / 2;    // E/W 边垂直居中
        _x = Clamp(nx, 0, _dispW - newW);
        _y = Clamp(ny, 0, _dispH - newH);
        _w = newW; _h = newH;
    }

    // 重新框选：自由时为矩形；有比例锁时按比例（以起点为锚角）。
    private void NewDrawTo(Point pt)
    {
        if (_ratio is double r)
        {
            double w = Math.Min(Math.Abs(pt.X - _startPtr.X), _dispW);
            double h = w / r;
            if (h > _dispH) { h = _dispH; w = h * r; }
            double nx = pt.X >= _startPtr.X ? _startPtr.X : _startPtr.X - w;
            double ny = pt.Y >= _startPtr.Y ? _startPtr.Y : _startPtr.Y - h;
            _x = Clamp(nx, 0, _dispW - w); _y = Clamp(ny, 0, _dispH - h); _w = w; _h = h;
        }
        else
        {
            double l = Clamp(Math.Min(_startPtr.X, pt.X), 0, _dispW);
            double t = Clamp(Math.Min(_startPtr.Y, pt.Y), 0, _dispH);
            double rr = Clamp(Math.Max(_startPtr.X, pt.X), 0, _dispW);
            double bb = Clamp(Math.Max(_startPtr.Y, pt.Y), 0, _dispH);
            _x = l; _y = t; _w = rr - l; _h = bb - t;
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        // 重新框选若拉得太小，钉到最小尺寸（避免出现 0 尺寸选框）。
        if (_mode == Mode.NewDraw)
        {
            if (_w < MinSize) { _w = Math.Min(_dispW, MinSize); if (_x + _w > _dispW) _x = _dispW - _w; }
            if (_h < MinSize) { _h = Math.Min(_dispH, MinSize); if (_y + _h > _dispH) _y = _dispH - _h; }
        }
        _mode = Mode.None;
        IsDragging = false;
        Layout();   // 松手后最终重排 + 触发 CropChanged，让信息条把拖动期间跳过的尺寸补刷一次
        _canvas.ReleasePointerCapture(e.Pointer);
    }

    // 命中测试（图像空间）：先看 8 把手，再看是否在选框内（移动），否则在框外 → 重新框选。
    private Mode HitTest(Point p)
    {
        var c = HandleCenters();
        for (int i = 0; i < 8; i++)
            if (Math.Abs(p.X - c[i].X) <= HitRadius && Math.Abs(p.Y - c[i].Y) <= HitRadius)
                return HandleModes[i];
        if (p.X >= _x && p.X <= _x + _w && p.Y >= _y && p.Y <= _y + _h) return Mode.Move;
        return Mode.NewDraw;
    }

    private Point[] HandleCenters()
    {
        double l = _x, t = _y, r = _x + _w, b = _y + _h, mx = _x + _w / 2, my = _y + _h / 2;
        return new[]
        {
            new Point(l, t), new Point(mx, t), new Point(r, t), new Point(r, my),
            new Point(r, b), new Point(mx, b), new Point(l, b), new Point(l, my),
        };
    }

    // 重新布局遮罩/选框/把手（图像空间坐标统一 +Pad 摆到 Canvas）。
    private void Layout()
    {
        // 选框
        Canvas.SetLeft(_selBorder, _x + Pad); Canvas.SetTop(_selBorder, _y + Pad);
        _selBorder.Width = Math.Max(0, _w); _selBorder.Height = Math.Max(0, _h);

        // 区外暗化（上下整宽，左右只在选框高度区间）
        Place(_maskTop, 0, 0, _dispW, _y);
        Place(_maskBottom, 0, _y + _h, _dispW, _dispH - (_y + _h));
        Place(_maskLeft, 0, _y, _x, _h);
        Place(_maskRight, _x + _w, _y, _dispW - (_x + _w), _h);

        // 把手
        var c = HandleCenters();
        for (int i = 0; i < 8; i++)
        {
            Canvas.SetLeft(_handles[i], c[i].X + Pad - HandleSize / 2);
            Canvas.SetTop(_handles[i], c[i].Y + Pad - HandleSize / 2);
        }

        CropChanged?.Invoke();
    }

    private static void Place(Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x + Pad); Canvas.SetTop(r, y + Pad);
        r.Width = Math.Max(0, w); r.Height = Math.Max(0, h);
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
