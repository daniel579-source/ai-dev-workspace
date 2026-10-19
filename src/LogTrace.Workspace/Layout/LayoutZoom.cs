namespace LogTrace.Workspace.Layout;

/// <summary>Layout 畫布的縮放（ADR-0006 第 11 點）：編輯器與回放畫面共用。縮放只改變顯示，吸附仍為 0.5 公尺。</summary>
public static class LayoutZoom
{
    public const double Min = 0.1;
    public const double Max = 4;
    const double Factor = 1.2;

    public static double Clamp(double zoom) => Math.Round(Math.Clamp(zoom, Min, Max), 3);

    /// <summary>Ctrl＋滾輪：往前放大、往後縮小一級。</summary>
    public static double Step(double zoom, bool zoomIn) => Clamp(zoomIn ? zoom * Factor : zoom / Factor);

    /// <summary>「符合視窗」：整張畫布放進可視範圍（像素）的縮放；可視範圍未知時維持 1。</summary>
    public static double Fit(double viewportWidth, double viewportHeight, double canvasWidth, double canvasHeight) =>
        viewportWidth <= 0 || viewportHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0
            ? 1
            : Clamp(Math.Min(viewportWidth / canvasWidth, viewportHeight / canvasHeight));

    /// <summary>狀態列顯示的百分比。</summary>
    public static string Text(double zoom) => $"{zoom * 100:0}%";
}
