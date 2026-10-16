using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TechValidation.UiSpike;

/// <summary>
/// 時間軸（ADR-0007 風險：時間軸自行繪製並只畫可見範圍）。
/// 每個像素欄以二分搜尋算出事件數，畫成密度長條；警報畫成紅色記號。
/// 滾輪縮放、拖曳平移。
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    long[] _ticks = [];
    long[] _alarms = [];
    long _viewStart, _viewSpan, _min, _max;
    Point? _dragFrom;
    WriteableBitmap? _bitmap;
    int[] _pixels = [];
    long _dragStart;

    static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1e, 0x22, 0x28));
    static readonly Brush Bar = new SolidColorBrush(Color.FromRgb(0x4f, 0x9d, 0xde));
    static readonly Pen AlarmPen = new(Brushes.OrangeRed, 1.5);
    static readonly Pen TickPen = new(new SolidColorBrush(Color.FromRgb(0x60, 0x66, 0x70)), 1);
    static readonly Typeface Face = new("Segoe UI");

    static TimelineControl()
    {
        Bg.Freeze(); Bar.Freeze(); AlarmPen.Freeze(); TickPen.Freeze();
    }

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetData(long[] ticks, long[] alarms)
    {
        _ticks = ticks;
        _alarms = alarms;
        _min = ticks.Length > 0 ? ticks[0] : 0;
        _max = ticks.Length > 0 ? ticks[^1] : 1;
        _viewStart = _min;
        _viewSpan = Math.Max(1, _max - _min);
        InvalidateVisual();
    }

    /// <summary>自我檢查用：模擬連續縮放與平移。</summary>
    public void Animate(int frame)
    {
        double phase = frame / 60.0;
        double zoom = Math.Pow(10, 2.5 * (0.5 - 0.5 * Math.Cos(phase))); // 1 倍 ～ 約 316 倍
        _viewSpan = (long)Math.Max(TimeSpan.TicksPerSecond, (_max - _min) / zoom);
        _viewStart = _min + (long)((_max - _min - _viewSpan) * (0.5 + 0.5 * Math.Sin(phase * 0.7)));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (_ticks.Length == 0 || w < 2) return;
        double barArea = h - 20;
        int cols = (int)w;
        var counts = new int[cols];
        int maxCount = 1;
        int lo = LowerBound(_ticks, _viewStart);
        for (int x = 0; x < cols; x++)
        {
            long t1 = _viewStart + (long)((double)_viewSpan * (x + 1) / cols);
            int hi = LowerBound(_ticks, t1);
            counts[x] = hi - lo;
            if (counts[x] > maxCount) maxCount = counts[x];
            lo = hi;
        }
        // 密度長條畫進點陣圖再一次送出：若改用每欄一個幾何圖形（約 1,400 個），
        // 繪製執行緒每格都要重新三角化，實測只有約 8 fps（見 results）。
        int bh = Math.Max(1, (int)barArea);
        if (_bitmap is null || _bitmap.PixelWidth != cols || _bitmap.PixelHeight != bh)
        {
            _bitmap = new WriteableBitmap(cols, bh, 96, 96, PixelFormats.Bgra32, null);
            _pixels = new int[cols * bh];
        }
        Array.Clear(_pixels);
        const int barColor = unchecked((int)0xFF4F9DDE);
        for (int x = 0; x < cols; x++)
        {
            if (counts[x] == 0) continue;
            int hgt = Math.Max(1, (int)(bh * Math.Sqrt(counts[x] / (double)maxCount)));
            for (int y = bh - hgt; y < bh; y++) _pixels[y * cols + x] = barColor;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, cols, bh), _pixels, cols * 4, 0);
        dc.DrawImage(_bitmap, new Rect(0, 0, cols, bh));

        int a0 = LowerBound(_alarms, _viewStart), a1 = LowerBound(_alarms, _viewStart + _viewSpan);
        for (int i = a0; i < a1; i++)
        {
            double x = (_alarms[i] - _viewStart) / (double)_viewSpan * w;
            dc.DrawLine(AlarmPen, new Point(x, 0), new Point(x, barArea));
        }

        // 刻度
        var step = NiceStep((long)(_viewSpan / Math.Max(1, w / 140)));
        long first = _viewStart / step * step + step;
        for (long t = first; t < _viewStart + _viewSpan; t += step)
        {
            double x = (t - _viewStart) / (double)_viewSpan * w;
            dc.DrawLine(TickPen, new Point(x, barArea), new Point(x, barArea + 4));
            var ft = new FormattedText(new DateTime(t).ToString(step >= TimeSpan.TicksPerDay ? "MM-dd" : step >= TimeSpan.TicksPerSecond ? "MM-dd HH:mm:ss" : "HH:mm:ss.fff"),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Brushes.Gainsboro, 1.0);
            dc.DrawText(ft, new Point(x + 2, barArea + 3));
        }
    }

    static long NiceStep(long raw)
    {
        long[] steps = [TimeSpan.TicksPerMillisecond, 10 * TimeSpan.TicksPerMillisecond, 100 * TimeSpan.TicksPerMillisecond,
            TimeSpan.TicksPerSecond, 10 * TimeSpan.TicksPerSecond, TimeSpan.TicksPerMinute, 10 * TimeSpan.TicksPerMinute,
            TimeSpan.TicksPerHour, 6 * TimeSpan.TicksPerHour, TimeSpan.TicksPerDay];
        foreach (var s in steps) if (s >= raw) return s;
        return TimeSpan.TicksPerDay * Math.Max(1, raw / TimeSpan.TicksPerDay);
    }

    static int LowerBound(long[] a, long v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) >>> 1; if (a[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double x = e.GetPosition(this).X / Math.Max(1, ActualWidth);
        long anchor = _viewStart + (long)(_viewSpan * x);
        double f = e.Delta > 0 ? 0.8 : 1.25;
        _viewSpan = Math.Clamp((long)(_viewSpan * f), TimeSpan.TicksPerMillisecond, Math.Max(1, _max - _min));
        _viewStart = anchor - (long)(_viewSpan * x);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(this);
        _dragStart = _viewStart;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } p) return;
        double dx = e.GetPosition(this).X - p.X;
        _viewStart = _dragStart - (long)(dx / Math.Max(1, ActualWidth) * _viewSpan);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }
}
