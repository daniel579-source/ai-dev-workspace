using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LogTrace.Workspace;

namespace LogTrace.App;

/// <summary>
/// 醒目標示（ADR-0010 第 4 點）：把 TextSegment 清單畫成文字，搜尋符合處加上底色。
/// 用在 TextBlock（事件清單）與唯讀的 RichTextBox（詳細資訊，可以選取與複製）。
/// </summary>
public static class Highlight
{
    static readonly Brush MatchBackground = new SolidColorBrush(Color.FromRgb(0xFD, 0xE0, 0x47));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<TextSegment>), typeof(Highlight), new PropertyMetadata(null, OnSegmentsChanged));

    public static IReadOnlyList<TextSegment>? GetSegments(DependencyObject d) => (IReadOnlyList<TextSegment>?)d.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject d, IReadOnlyList<TextSegment>? value) => d.SetValue(SegmentsProperty, value);

    static IEnumerable<Inline> Inlines(IReadOnlyList<TextSegment>? segments) =>
        (segments ?? []).Select(s => s.IsMatch ? new Run(s.Text) { Background = MatchBackground } : new Run(s.Text));

    static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var segments = e.NewValue as IReadOnlyList<TextSegment>;
        switch (d)
        {
            case TextBlock tb:
                tb.Inlines.Clear();
                tb.Inlines.AddRange(Inlines(segments));
                break;
            case RichTextBox rtb:
                // 保留原本的 Document、只換內容：整個換掉時 UI Automation 仍指向舊的 Document
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                paragraph.Inlines.AddRange(Inlines(segments));
                rtb.Document.PagePadding = new Thickness(4);
                rtb.Document.Blocks.Clear();
                rtb.Document.Blocks.Add(paragraph);
                break;
        }
    }
}
