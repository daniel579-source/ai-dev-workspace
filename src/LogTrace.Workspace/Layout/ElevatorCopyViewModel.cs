using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LogTrace.Workspace.Layout;

/// <summary>
/// 並排檢視中，電梯在其他服務樓層的分身（ADR-0006 第 15 點）：和本體是同一個元素，只是畫在另一層的區域。
/// 畫面上的位置＝元素的位置＋這一層的位移；元素移動時跟著移動。Element 是編輯器或回放的元素 ViewModel。
/// </summary>
public sealed partial class ElevatorCopyViewModel : ObservableObject
{
    readonly Func<double> _x, _y;

    public ElevatorCopyViewModel(INotifyPropertyChanged element, string floor, double offsetX, double offsetY, Func<double> x, Func<double> y)
    {
        Element = element;
        (Floor, OffsetX, OffsetY, _x, _y) = (floor, offsetX, offsetY, x, y);
        element.PropertyChanged += OnElementChanged;
    }

    void OnElementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "X" or "Y" or "ViewX" or "ViewY")
        {
            OnPropertyChanged(nameof(ViewX));
            OnPropertyChanged(nameof(ViewY));
        }
    }

    /// <summary>不再使用時取消訂閱元素的變更。</summary>
    public void Detach() => Element.PropertyChanged -= OnElementChanged;

    /// <summary>同一個元素、同一層、同一個位移時視為相同（重建時不必換掉）。</summary>
    public bool SameAs(INotifyPropertyChanged element, string floor, double offsetX, double offsetY) =>
        ReferenceEquals(Element, element) && Floor == floor && OffsetX == offsetX && OffsetY == offsetY;

    public INotifyPropertyChanged Element { get; }
    public string Floor { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
    public double ViewX => _x() + OffsetX;
    public double ViewY => _y() + OffsetY;
}
