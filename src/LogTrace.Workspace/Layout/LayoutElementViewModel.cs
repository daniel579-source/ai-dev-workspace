using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

/// <summary>自訂屬性的一列（鍵＝值，值一律是文字；ADR-0006 第 6 點）。</summary>
public sealed partial class PropertyItemViewModel(string key, string value) : ObservableObject
{
    [ObservableProperty]
    public partial string Key { get; set; } = key;

    [ObservableProperty]
    public partial string Value { get; set; } = value;
}

/// <summary>
/// Layout 編輯器中的一個元素。單位為公尺；X、Y 為未旋轉時左上角的位置，W 為寬、H 為深，旋轉以中心點為準。
/// 輸送線沒有位置與尺寸，畫面上以 Line* 連接兩端元素的中心；連到搬送機的那一端改畫到交接點。
/// </summary>
public sealed partial class LayoutElementViewModel : ObservableObject
{
    readonly Dictionary<string, JsonElement>? _extra;

    public LayoutElementViewModel(LayoutElement e)
    {
        Id = e.Id;
        Type = e.Type;
        Name = e.Name ?? "";
        X = e.X ?? 0;
        Y = e.Y ?? 0;
        W = e.W ?? 0;
        H = e.H ?? 0;
        Rotation = e.Rotation ?? 0;
        Height = e.Height ?? 0;
        Row = e.Row ?? "";
        ColFrom = e.ColFrom ?? 1;
        ColTo = e.ColTo ?? 1;
        Levels = e.Levels ?? 1;
        LevelHeight = e.LevelHeight ?? 0;
        From = e.From;
        To = e.To;
        Bidirectional = e.Bidirectional == true;
        FromPoint = e.FromPoint;
        ToPoint = e.ToPoint;
        Aliases = new ObservableCollection<string>(e.Aliases ?? []);
        Props = new ObservableCollection<PropertyItemViewModel>((e.Props ?? []).Select(p => new PropertyItemViewModel(p.Key, p.Value)));
        Aliases.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AliasesText));
        _extra = e.Extra;
    }

    /// <summary>畫布與清單上顯示的別名；沒有別名時為 null（不顯示）。</summary>
    public string? AliasesText => Aliases.Count == 0 ? null : string.Join("・", Aliases);

    /// <summary>是否為編輯器中選取的元素（畫面以框線標出）。</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Id { get; }
    public LayoutElementType Type { get; }
    public string TypeLabel => LayoutElementTypes.Label(Type);

    public bool CanHaveAliases => LayoutElement.CanHaveAliases(Type);
    public bool IsRack => Type == LayoutElementType.Rack;
    public bool IsConveyor => Type == LayoutElementType.Conveyor;
    public bool HasGeometry => !IsConveyor;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CenterX))]
    public partial double X { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CenterY))]
    public partial double Y { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CenterX))]
    public partial double W { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CenterY))]
    public partial double H { get; set; }

    /// <summary>0、90、180、270 度。</summary>
    [ObservableProperty]
    public partial int Rotation { get; set; }

    [ObservableProperty]
    public partial double Height { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RackSummary))]
    public partial string Row { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RackSummary))]
    public partial int ColFrom { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RackSummary))]
    public partial int ColTo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RackSummary), nameof(RackHeight))]
    public partial int Levels { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RackHeight))]
    public partial double LevelHeight { get; set; }

    /// <summary>料架的高度＝層數 × 每層高度（ADR-0006 第 5 點）。</summary>
    public double RackHeight => Levels * LevelHeight;

    public string RackSummary => $"排 {Row}　列 {ColFrom}～{ColTo}　{Levels} 層";

    [ObservableProperty]
    public partial string? From { get; set; }

    [ObservableProperty]
    public partial string? To { get; set; }

    /// <summary>輸送線是否雙向；否則方向為起點 → 終點。</summary>
    [ObservableProperty]
    public partial bool Bidirectional { get; set; }

    /// <summary>起點是搬送機時的交接點（公尺）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFromPoint), nameof(TransferPointsText))]
    public partial LayoutPoint? FromPoint { get; set; }

    /// <summary>終點是搬送機時的交接點（公尺）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToPoint), nameof(TransferPointsText))]
    public partial LayoutPoint? ToPoint { get; set; }

    public bool HasFromPoint => FromPoint is not null;
    public bool HasToPoint => ToPoint is not null;

    /// <summary>屬性面板顯示的交接點；沒有連到搬送機時為 null。</summary>
    public string? TransferPointsText =>
        FromPoint is null && ToPoint is null ? null
        : string.Join("　", new[] { FromPoint is { } f ? $"起點 ({f.X:0.##}, {f.Y:0.##})" : null, ToPoint is { } t ? $"終點 ({t.X:0.##}, {t.Y:0.##})" : null }.OfType<string>());

    public ObservableCollection<string> Aliases { get; }
    public ObservableCollection<PropertyItemViewModel> Props { get; }

    /// <summary>存檔檢查時發現問題（例如別名重複），畫面上以醒目的框線標出。</summary>
    [ObservableProperty]
    public partial bool HasIssue { get; set; }

    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    // 輸送線：兩端元素的中心（由編輯器更新）
    [ObservableProperty]
    public partial double LineX1 { get; set; }

    [ObservableProperty]
    public partial double LineY1 { get; set; }

    [ObservableProperty]
    public partial double LineX2 { get; set; }

    [ObservableProperty]
    public partial double LineY2 { get; set; }

    public LayoutElement ToModel()
    {
        if (IsConveyor)
            return new LayoutElement
            {
                Id = Id, Type = Type, Name = string.IsNullOrWhiteSpace(Name) ? null : Name, From = From, To = To,
                Bidirectional = Bidirectional ? true : null, FromPoint = FromPoint, ToPoint = ToPoint, Props = PropsModel(), Extra = _extra,
            };
        return new LayoutElement
        {
            Id = Id,
            Type = Type,
            Name = Name,
            Aliases = CanHaveAliases ? [.. Aliases] : null,
            X = X,
            Y = Y,
            W = W,
            H = H,
            Rotation = Rotation,
            Height = IsRack ? null : Height,
            Row = IsRack ? Row : null,
            ColFrom = IsRack ? ColFrom : null,
            ColTo = IsRack ? ColTo : null,
            Levels = IsRack ? Levels : null,
            LevelHeight = IsRack ? LevelHeight : null,
            Props = PropsModel(),
            Extra = _extra,
        };
    }

    List<KeyValuePair<string, string>>? PropsModel() =>
        Props.Count == 0 ? null : Props.Select(p => new KeyValuePair<string, string>(p.Key.Trim(), p.Value)).ToList();
}

/// <summary>元素種類的名稱與新增時的預設值（沿用 Layout 編輯器原型）。</summary>
public static class LayoutElementTypes
{
    public static IReadOnlyList<LayoutElementType> Addable { get; } =
    [
        LayoutElementType.Station, LayoutElementType.Rack, LayoutElementType.Vehicle, LayoutElementType.Dock,
        LayoutElementType.Conveyor, LayoutElementType.Area, LayoutElementType.Label,
    ];

    public static string Label(LayoutElementType type) => type switch
    {
        LayoutElementType.Station => "站點",
        LayoutElementType.Rack => "料架",
        LayoutElementType.Vehicle => "搬送機",
        LayoutElementType.Dock => "對接設備",
        LayoutElementType.Conveyor => "輸送線",
        LayoutElementType.Area => "區域",
        LayoutElementType.Label => "文字",
        _ => type.ToString(),
    };

    public static string IdPrefix(LayoutElementType type) => type switch
    {
        LayoutElementType.Station => "st",
        LayoutElementType.Rack => "rk",
        LayoutElementType.Vehicle => "vh",
        LayoutElementType.Dock => "eq",
        LayoutElementType.Conveyor => "cv",
        LayoutElementType.Area => "ar",
        LayoutElementType.Label => "tx",
        _ => "el",
    };

    /// <summary>預設尺寸（寬、深、高，公尺）。</summary>
    public static (double W, double H, double Height) DefaultSize(LayoutElementType type) => type switch
    {
        LayoutElementType.Station => (2, 2, 1.2),
        LayoutElementType.Rack => (12, 1.5, 0),
        LayoutElementType.Vehicle => (2.5, 1.2, 2),
        LayoutElementType.Dock => (2, 2, 1.5),
        LayoutElementType.Area => (6, 4, 0),
        LayoutElementType.Label => (3, 1, 0),
        _ => (0, 0, 0),
    };
}
