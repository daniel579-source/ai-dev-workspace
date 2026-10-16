using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TechValidation.UiSpike;

/// <summary>
/// 2D Layout 畫布（ADR-0007 第 4 項）：約 600 個元素（站點、料架、搬送機），
/// 滾輪縮放、右鍵拖曳平移、左鍵拖曳元素（吸附 10 px 格線）。
/// </summary>
public sealed class LayoutCanvasHost : Border
{
    readonly Canvas _canvas = new() { Width = 3000, Height = 2000, Background = Brushes.White };
    readonly ScaleTransform _scale = new(1, 1);
    readonly TranslateTransform _pan = new();
    readonly List<Border> _items = new();
    Border? _dragItem;
    Point _dragOffset;
    Point? _panFrom;
    Point _panStart;

    public LayoutCanvasHost()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(0xf3, 0xf4, 0xf6));
        _canvas.RenderTransform = new TransformGroup { Children = { _scale, _pan } };
        Child = _canvas;
        Build();
    }

    void Build()
    {
        var rnd = new Random(3);
        // 料架：20 排 × 20 格
        for (int row = 0; row < 20; row++)
            for (int col = 0; col < 20; col++)
                Add(100 + col * 60, 100 + row * 70, 50, 40, Color.FromRgb(0xd6, 0xe4, 0xf0), $"R{row:00}{col:00}", rnd.Next(10) == 0);
        // 站點
        for (int i = 0; i < 48; i++) Add(1400 + (i % 8) * 110, 100 + (i / 8) * 120, 90, 70, Color.FromRgb(0xd9, 0xf2, 0xd9), $"A-{i + 1:00}", rnd.Next(6) == 0);
        // 輸送帶
        for (int i = 0; i < 150; i++) Add(100 + (i % 50) * 50, 1600 + (i / 50) * 60, 45, 30, Color.FromRgb(0xee, 0xe6, 0xd2), $"C{i:000}", false);
    }

    void Add(double x, double y, double w, double h, Color fill, string text, bool alarm)
    {
        var b = new Border
        {
            Width = w, Height = h,
            Background = new SolidColorBrush(fill),
            BorderBrush = alarm ? Brushes.OrangeRed : Brushes.SlateGray,
            BorderThickness = new Thickness(alarm ? 3 : 1),
            CornerRadius = new CornerRadius(3),
            Child = new TextBlock { Text = text, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Cursor = Cursors.SizeAll,
        };
        Canvas.SetLeft(b, x);
        Canvas.SetTop(b, y);
        b.MouseLeftButtonDown += (s, e) =>
        {
            _dragItem = b;
            var p = e.GetPosition(_canvas);
            _dragOffset = new Point(p.X - Canvas.GetLeft(b), p.Y - Canvas.GetTop(b));
            b.CaptureMouse();
            e.Handled = true;
        };
        b.MouseMove += (s, e) =>
        {
            if (_dragItem != b) return;
            var p = e.GetPosition(_canvas);
            Canvas.SetLeft(b, Math.Round((p.X - _dragOffset.X) / 10) * 10);
            Canvas.SetTop(b, Math.Round((p.Y - _dragOffset.Y) / 10) * 10);
        };
        b.MouseLeftButtonUp += (s, e) => { _dragItem = null; b.ReleaseMouseCapture(); };
        _items.Add(b);
        _canvas.Children.Add(b);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        double f = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomAt(p, f);
        e.Handled = true;
    }

    void ZoomAt(Point p, double f)
    {
        double ns = Math.Clamp(_scale.ScaleX * f, 0.1, 8);
        f = ns / _scale.ScaleX;
        _pan.X = p.X - (p.X - _pan.X) * f;
        _pan.Y = p.Y - (p.Y - _pan.Y) * f;
        _scale.ScaleX = _scale.ScaleY = ns;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        _panFrom = e.GetPosition(this);
        _panStart = new Point(_pan.X, _pan.Y);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panFrom is not { } f) return;
        var p = e.GetPosition(this);
        _pan.X = _panStart.X + p.X - f.X;
        _pan.Y = _panStart.Y + p.Y - f.Y;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        _panFrom = null;
        ReleaseMouseCapture();
    }

    // ---- 自我檢查用的模擬操作 ----

    public void SimulateDrag(int frame)
    {
        var b = _items[frame % 3 == 0 ? 0 : 405];
        Canvas.SetLeft(b, Math.Round((200 + 150 * Math.Sin(frame / 20.0)) / 10) * 10);
        Canvas.SetTop(b, Math.Round((200 + 150 * Math.Cos(frame / 20.0)) / 10) * 10);
    }

    public void SimulateZoomPan(int frame)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        ZoomAt(center, frame % 120 < 60 ? 1.03 : 1 / 1.03);
        _pan.X += Math.Sin(frame / 15.0) * 8;
        _pan.Y += Math.Cos(frame / 15.0) * 5;
    }
}
