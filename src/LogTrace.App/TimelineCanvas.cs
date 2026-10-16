using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LogTrace.Workspace.Playback;

namespace LogTrace.App;

/// <summary>
/// 時間軸（ADR-0008 第 3 點）的繪製：總覽（事件密度、警報期間、時間不確定、搜尋符合、目前時刻）
/// 或細部（目前時刻前後 30 秒的刻度）。點擊或拖曳時把位置換成時間，呼叫 Seek。
/// </summary>
public sealed class TimelineCanvas : FrameworkElement
{
    static readonly Brush[] ModuleBrushes = [Freeze(0x3B82F6), Freeze(0x16A34A), Freeze(0xEA580C), Freeze(0x9333EA)];
    static readonly Brush Background = Freeze(0xF8FAFC);
    static readonly Brush DensityBrush = Freeze(0x94A3B8);
    static readonly Brush AlarmBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xDC, 0x26, 0x26));
    static readonly Pen UncertainPen = new(Freeze(0xB45309), 1);
    static readonly Pen SearchPen = new(Freeze(0xEAB308), 2);
    static readonly Pen CursorPen = new(Brushes.Black, 2);
    static readonly Pen BorderPen = new(Freeze(0xCBD5E1), 1);

    static SolidColorBrush Freeze(int rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    public static readonly DependencyProperty TimelineProperty = DependencyProperty.Register(
        nameof(Timeline), typeof(TimelineViewModel), typeof(TimelineCanvas), new PropertyMetadata(null, OnTimelineChanged));

    public TimelineViewModel? Timeline
    {
        get => (TimelineViewModel?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    /// <summary>true＝細部；false＝總覽。</summary>
    public bool IsDetail { get; set; }

    /// <summary>點擊或拖曳到某個時刻時呼叫。</summary>
    public Action<DateTime>? Seek { get; set; }

    static void OnTimelineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (TimelineCanvas)d;
        if (e.OldValue is TimelineViewModel old) old.Changed -= canvas.OnChanged;
        if (e.NewValue is TimelineViewModel now) now.Changed += canvas.OnChanged;
        canvas.InvalidateVisual();
    }

    void OnChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Background, BorderPen, new Rect(0, 0, w, h));
        if (Timeline is not { HasData: true } t || w <= 0) return;

        if (IsDetail)
        {
            foreach (var tick in t.DetailTicks)
            {
                var pen = new Pen(ModuleBrushes[Math.Max(0, tick.ModuleIndex) % ModuleBrushes.Length], tick.IsChange ? 2 : 1);
                double x = tick.Fraction * w, top = tick.IsChange ? 2 : h * 0.55;
                dc.DrawLine(pen, new Point(x, top), new Point(x, h - 2));
            }
            dc.DrawLine(CursorPen, new Point(w / 2, 0), new Point(w / 2, h));
            Label(dc, $"−{TimelineViewModel.DetailHalfWidth.TotalSeconds:0} 秒", 3, h);
            Label(dc, $"+{TimelineViewModel.DetailHalfWidth.TotalSeconds:0} 秒", w - 40, h);
            return;
        }

        // 事件密度
        double bw = w / t.Density.Count;
        for (int i = 0; i < t.Density.Count; i++)
            if (t.Density[i] > 0) dc.DrawRectangle(DensityBrush, null, new Rect(i * bw, h - t.Density[i] * (h - 14), Math.Max(1, bw - 0.5), t.Density[i] * (h - 14)));
        // 警報期間（紅色區段）
        foreach (var span in t.AlarmSpans)
            dc.DrawRectangle(AlarmBrush, null, new Rect(span.From * w, 0, Math.Max(2, (span.To - span.From) * w), 8));
        // 時間不確定的位置
        foreach (var f in t.UncertainMarks) dc.DrawLine(UncertainPen, new Point(f * w, h - 6), new Point(f * w, h));
        // 搜尋符合的位置
        foreach (var f in t.SearchMarks) dc.DrawLine(SearchPen, new Point(f * w, 9), new Point(f * w, 15));
        // 目前時刻
        dc.DrawLine(CursorPen, new Point(t.Cursor * w, 0), new Point(t.Cursor * w, h));
        Label(dc, t.Start.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), 3, h);
        Label(dc, t.End.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture), w - 90, h);
    }

    void Label(DrawingContext dc, string text, double x, double h)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.DimGray,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(Math.Max(0, x), h - ft.Height - 1));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        SeekAt(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) SeekAt(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    void SeekAt(double x)
    {
        if (Timeline is not { HasData: true } t || ActualWidth <= 0) return;
        double f = x / ActualWidth;
        Seek?.Invoke(IsDetail ? t.DetailTime(f) : t.OverviewTime(f));
    }
}
