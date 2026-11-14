using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Plants;
using LogTrace.Workspace.Layout;

namespace LogTrace.Workspace.Playback;

// 回放的樓層（ADR-0006 第 13 點、ADR-0017 第 6 點）：3D 中所有樓層一律上下堆疊。
// 原本 2D 的單層／並排、橫向／垂直已移除；「跟隨」改為選取元素時相機轉向該元素（預設關，#113 使用者決定）。
public sealed partial class PlaybackViewModel
{
    Action<FloorViewSettings>? _saveView;
    bool _applyingView;

    /// <summary>Layout 的樓層名稱（依順序）。</summary>
    public ObservableCollection<string> Floors { get; } = [LayoutFloors.Default];

    /// <summary>
    /// 跟隨：從設備狀態表、命令或事件選取元素時，3D 相機轉向該元素。在 3D 畫面上直接點選的元素不轉向。
    /// </summary>
    [ObservableProperty]
    public partial bool FollowFocus { get; set; }

    /// <summary>要求 3D 相機轉向某個元素（元素 id）；由畫面處理。</summary>
    public event Action<string>? FocusRequested;

    /// <summary>套用本機的檢視設定（只使用「跟隨」）；save 在使用者切換時呼叫。</summary>
    public void UseViewSettings(FloorViewSettings settings, Action<FloorViewSettings>? save)
    {
        _applyingView = true;
        try
        {
            FollowFocus = settings.Follow;
            CargoByPallet = settings.CargoByPallet;
        }
        finally
        {
            _applyingView = false;
        }
        _saveView = save;
    }

    partial void OnFollowFocusChanged(bool value) => SaveView();

    void SaveView()
    {
        if (_applyingView) return;
        _saveView?.Invoke(new FloorViewSettings(Follow: FollowFocus, CargoByPallet: CargoByPallet));
    }

    /// <summary>搬送中貨物的標示（#125）：false＝命令編號（預設）、true＝棧板編號；記在本機。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CargoLabelIndex))]
    public partial bool CargoByPallet { get; set; }

    /// <summary>工具列下拉選單的索引：0＝命令編號、1＝棧板編號。</summary>
    public int CargoLabelIndex
    {
        get => CargoByPallet ? 1 : 0;
        set => CargoByPallet = value == 1;
    }

    public static IReadOnlyList<string> CargoLabelChoices { get; } = ["命令編號", "棧板編號"];

    partial void OnCargoByPalletChanged(bool value)
    {
        SaveView();
        RefreshSceneState();
    }

    void LoadFloors(LayoutSection layout)
    {
        Floors.Clear();
        foreach (var f in LayoutFloors.Of(layout)) Floors.Add(f);
    }

    /// <summary>開啟跟隨時，相機轉向 id。</summary>
    void Follow(string id)
    {
        if (FollowFocus) FocusRequested?.Invoke(id);
    }

    /// <summary>設備狀態表選取的一列：選取 Layout 上的對應元素。</summary>
    [ObservableProperty]
    public partial EquipmentRowViewModel? SelectedEquipment { get; set; }

    partial void OnSelectedEquipmentChanged(EquipmentRowViewModel? value) => SelectElementById(value?.Id);

    /// <summary>點設備狀態表的一列（或命令的搬送機）：選取 Layout 上的元素，開啟跟隨時相機轉向它。</summary>
    public void SelectElementById(string? id)
    {
        if (id is not null && Elements.FirstOrDefault(e => e.Id == id) is { } el)
        {
            SelectedElement = el;
            Follow(el.Id);
        }
    }

    /// <summary>
    /// 在 3D 畫面上點選：id 為元素（null＝點在空白處，不改變選取）；料架另外帶入點到的列（column）。相機不轉向。
    /// </summary>
    public void SelectFromScene(string? id, int? column = null)
    {
        if (id is null || Elements.FirstOrDefault(e => e.Id == id) is not { } el) return;
        if (el.IsRack && column is { } c && el.Cells.FirstOrDefault(x => x.Column == c) is { } cell) SelectRackCell(cell);
        else SelectedElement = el;
    }

    /// <summary>
    /// 選取事件時：事件的欄位中第一個能以別名對應到 Layout 元素的編號，開啟跟隨時相機轉向該元素（不改變選取的元素）。
    /// </summary>
    public void FollowEvent(IReadOnlyList<EventField> fields)
    {
        if (!FollowFocus || _model is null) return;
        foreach (var f in fields.Where(f => FieldKinds.Identifiers.Contains(f.Kind)))
            if (_model.Resolve(f.Value) is { } target && Elements.FirstOrDefault(e => e.Id == target.Id) is { } el)
            {
                Follow(el.Id);
                return;
            }
    }
}
