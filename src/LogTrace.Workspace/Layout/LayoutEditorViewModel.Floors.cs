using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

/// <summary>
/// 樓層檢視的本機設定（不寫進廠別設定檔）：回放的「跟隨」（ADR-0017）、編輯器的「只顯示目前樓層」（ADR-0018）。
/// 2D 時期的單層／並排、橫向／垂直已移除；舊設定檔中的這些欄位讀取時忽略。
/// </summary>
public sealed record FloorViewSettings(bool Follow = false, bool OnlyCurrentFloor = false);

// 樓層（ADR-0006 第 13 點、ADR-0018 第 6 點）：每個元素屬於一層；3D 中所有樓層上下堆疊。
// 編輯一律在「目前樓層」的平面上：滑鼠操作傳進來的座標是目前樓層的座標（公尺）；點了別層的元素時，目前樓層先切到那一層。
public sealed partial class LayoutEditorViewModel
{
    Action<FloorViewSettings>? _saveView;

    /// <summary>樓層名稱，順序即樓層順序。</summary>
    public ObservableCollection<string> Floors { get; } = [];

    IReadOnlyList<string> FloorList => Floors.Count > 0 ? Floors : [LayoutFloors.Default];

    /// <summary>目前樓層：編輯的平面；新增與貼上的元素放在這一層，框選只選這一層的元素。</summary>
    [ObservableProperty]
    public partial string CurrentFloor { get; set; } = LayoutFloors.Default;

    /// <summary>只顯示目前樓層（ADR-0018 第 6 點，預設關，記在本機）：避免上層遮擋。</summary>
    [ObservableProperty]
    public partial bool OnlyCurrentFloor { get; set; }

    /// <summary>樓層列中輸入的名稱（改名用）；切換樓層時帶入目前樓層的名稱。</summary>
    [ObservableProperty]
    public partial string FloorNameText { get; set; } = LayoutFloors.Default;

    /// <summary>選取多個元素時，要移到的樓層。</summary>
    [ObservableProperty]
    public partial string? MoveTargetFloor { get; set; }

    public bool HasMultipleSelection => SelectionCount > 1;

    /// <summary>套用本機的檢視設定；save 在使用者切換時呼叫。</summary>
    public void UseViewSettings(FloorViewSettings settings, Action<FloorViewSettings>? save)
    {
        _saveView = null;
        OnlyCurrentFloor = settings.OnlyCurrentFloor;
        _saveView = save;
    }

    partial void OnOnlyCurrentFloorChanged(bool value) => _saveView?.Invoke(new FloorViewSettings(OnlyCurrentFloor: value));

    partial void OnCurrentFloorChanged(string value)
    {
        FloorNameText = value;
        FloorHeightText = HeightText(value);
        RefreshLayoutView();
        // 換到別層時，別層的元素取消選取：避免被 Del 刪除或被拖曳一起移動（Jev，0.78）
        if (Elements.Any(e => e.IsSelected && !e.IsOnCanvas)) SetSelection(Selection.Where(e => e.IsOnCanvas));
    }

    /// <summary>
    /// 元素所在的樓層（清單中的寫法）：輸送線取起點（沒有時取終點）的樓層；電梯取第一個服務的樓層（ADR-0006 第 15 點）。
    /// </summary>
    public string FloorOf(LayoutElementViewModel e)
    {
        if (e.IsConveyor) return (Find(e.From) ?? Find(e.To)) is { } end ? FloorOf(end) : FloorList[0];
        if (e.IsElevator) return ServedFloorsOf(e).FirstOrDefault() ?? FloorList[0];
        return LayoutFloors.Canonical(e.Floor, FloorList) ?? FloorList[0];
    }

    /// <summary>電梯服務的樓層（依樓層順序，清單中的寫法）。</summary>
    IReadOnlyList<string> ServedFloorsOf(LayoutElementViewModel e) =>
        FloorList.Where(f => e.ServedFloors.Any(s => SameFloor(s.Trim(), f))).ToList();

    /// <summary>元素是否畫在 floor：電梯在每個服務的樓層，其他元素在所在的樓層。</summary>
    bool OnFloor(LayoutElementViewModel e, string floor) =>
        e.IsElevator ? ServedFloorsOf(e).Any(f => SameFloor(f, floor)) : SameFloor(FloorOf(e), floor);

    /// <summary>屬性面板的「服務的樓層」勾選框：選取電梯時列出所有樓層。</summary>
    public ObservableCollection<ServedFloorChoice> ServedFloorChoices { get; } = [];

    void RefreshServedFloorChoices()
    {
        var items = SelectedElement is { IsElevator: true } ev
            ? FloorList.Select(f => (f, ServedFloorsOf(ev).Any(s => SameFloor(s, f)))).ToList()
            : [];
        if (items.SequenceEqual(ServedFloorChoices.Select(c => (c.Floor, c.IsChecked)))) return;
        ServedFloorChoices.Clear();
        foreach (var (f, on) in items)
        {
            var choice = new ServedFloorChoice(f, on);
            choice.PropertyChanged += (_, _) => SetServedFloor(choice.Floor, choice.IsChecked);
            ServedFloorChoices.Add(choice);
        }
    }

    /// <summary>勾選或取消一個服務的樓層（依樓層順序寫回）。</summary>
    void SetServedFloor(string floor, bool on)
    {
        if (SelectedElement is not { IsElevator: true } ev) return;
        var served = FloorList.Where(f => SameFloor(f, floor) ? on : ev.ServedFloors.Any(s => SameFloor(s.Trim(), f))).ToList();
        ev.ServedFloors = served;
        Status = served.Count < 2 ? "電梯至少要服務兩個樓層（存檔前會檢查）。" : $"「{ev.Name}」服務 {string.Join("、", served)}。";
    }

    bool SameFloor(string a, string b) => LayoutFloors.NameComparer.Equals(a, b);

    void LoadFloors(LayoutSection layout)
    {
        Floors.Clear();
        _floorHeights.Clear();
        foreach (var f in LayoutFloors.Of(layout)) Floors.Add(f);
        foreach (var f in layout.Floors ?? [])
            if (f.Height is { } h) _floorHeights[f.Name] = h;
        CurrentFloor = Floors[0];
        FloorHeightText = HeightText(CurrentFloor);
    }

    // ---- 樓板高度（ADR-0017 第 6 點）：3D 回放把樓層上下堆疊時使用；沒有填時為 5 公尺 ----

    readonly Dictionary<string, double> _floorHeights = new(LayoutFloors.NameComparer);

    /// <summary>樓層填的樓板高度；沒有填時為 null（使用預設 5 公尺）。</summary>
    public double? HeightOf(string floor) => _floorHeights.TryGetValue(floor, out var h) ? h : null;

    string HeightText(string floor) => HeightOf(floor) is { } h ? h.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>目前樓層的樓板高度（公尺）；空白＝使用預設 5 公尺。</summary>
    [ObservableProperty]
    public partial string FloorHeightText { get; set; } = "";

    /// <summary>設定目前樓層的樓板高度：空白時恢復預設；不是大於 0 的數字時不套用。</summary>
    [RelayCommand]
    void SetFloorHeight()
    {
        var text = FloorHeightText.Trim();
        var floor = CurrentFloor;
        if (text.Length == 0)
        {
            if (!_floorHeights.Remove(floor)) return;
            FloorsChanged($"樓層「{floor}」的樓板高度恢復預設 {LayoutFloors.DefaultHeight:0.###} 公尺。");
            return;
        }
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
            || !(h > 0 && h <= PlantConfigFile.MaxFloorHeight))
        {
            Status = $"樓板高度要是大於 0、不超過 {PlantConfigFile.MaxFloorHeight:0} 的數字（公尺）。";
            return;
        }
        if (HeightOf(floor) == h) return;
        _floorHeights[floor] = h;
        FloorsChanged($"樓層「{floor}」的樓板高度為 {h:0.###} 公尺。");
    }

    /// <summary>
    /// 重算每個元素是否在目前樓層（框選與換層取消選取用）、輸送線兩端的位置，以及導軌的把手。
    /// 3D 中所有樓層都畫出來（ADR-0018），元素座標就是樓層座標；位移固定為 0。
    /// </summary>
    void RefreshLayoutView()
    {
        if (!FloorList.Any(f => SameFloor(f, CurrentFloor))) CurrentFloor = FloorList[0];
        foreach (var e in Elements)
        {
            e.IsOnCanvas = OnFloor(e, CurrentFloor);
            (e.OffsetX, e.OffsetY) = (0, 0);
        }
        RefreshServedFloorChoices();
        var byId = Elements.Where(e => e.HasGeometry).ToDictionary(e => e.Id);
        foreach (var c in Elements.Where(e => e.IsConveyor))
        {
            if (c.From is { } f && byId.TryGetValue(f, out var a))
                (c.LineX1, c.LineY1) = a.Type == LayoutElementType.Vehicle && c.FromPoint is { } p ? (p.X, p.Y) : (a.CenterX, a.CenterY);
            if (c.To is { } t && byId.TryGetValue(t, out var b))
                (c.LineX2, c.LineY2) = b.Type == LayoutElementType.Vehicle && c.ToPoint is { } q ? (q.X, q.Y) : (b.CenterX, b.CenterY);
        }
        RefreshRails();
        LayoutChanged?.Invoke();
    }

    /// <summary>Layout 的內容或目前樓層改變（3D 畫面重送場景）。</summary>
    public event Action? LayoutChanged;

    void FloorsChanged(string status)
    {
        IsDirty = true;
        RefreshLayoutView();
        Commit(null);
        Status = status;
    }

    /// <summary>點樓層頁籤。</summary>
    [RelayCommand]
    void SelectFloor(string? floor)
    {
        if (floor is not null) CurrentFloor = floor;
    }

    /// <summary>新增樓層：名稱為下一個沒用過的「nF」，加在最上面一層之後。</summary>
    [RelayCommand]
    void AddFloor()
    {
        if (Floors.Count == 0) Floors.Add(LayoutFloors.Default);
        int n = Floors.Count + 1;
        while (Floors.Any(f => SameFloor(f, $"{n}F"))) n++;
        Floors.Add($"{n}F");
        CurrentFloor = Floors[^1];
        FloorsChanged($"已新增樓層「{CurrentFloor}」。");
    }

    /// <summary>目前樓層改名：所有元素的樓層一併更新。名稱不可空白、不可重複（大小寫視為相同）。</summary>
    [RelayCommand]
    void RenameFloor()
    {
        var name = FloorNameText.Trim();
        var old = CurrentFloor;
        int i = Floors.IndexOf(old);
        if (i < 0 || name == old) return;
        if (name.Length == 0)
        {
            Status = "樓層名稱不可空白。";
            return;
        }
        if (Floors.Where((_, k) => k != i).Any(f => SameFloor(f, name)))
        {
            Status = $"已經有樓層「{name}」（大小寫視為相同）。";
            return;
        }
        Batch(() =>
        {
            foreach (var e in Elements.Where(e => !e.IsConveyor && !e.IsElevator && SameFloor(FloorOf(e), old))) e.Floor = name;
            foreach (var e in Elements.Where(e => e.IsElevator && e.ServedFloors.Any(s => SameFloor(s.Trim(), old))))
                e.ServedFloors = e.ServedFloors.Select(s => SameFloor(s.Trim(), old) ? name : s).ToList(); // 電梯服務的樓層一併改名
            if (_floorHeights.Remove(old, out var height)) _floorHeights[name] = height; // 樓板高度跟著改名
            Floors[i] = name;
            CurrentFloor = name;
        });
        FloorsChanged($"樓層「{old}」已改名為「{name}」。");
    }

    [RelayCommand]
    void MoveFloorUp() => MoveFloor(+1);

    [RelayCommand]
    void MoveFloorDown() => MoveFloor(-1);

    /// <summary>上移＝往較高的樓層（清單中往後）。</summary>
    void MoveFloor(int by)
    {
        int i = Floors.IndexOf(CurrentFloor), j = i + by;
        if (i < 0 || j < 0 || j >= Floors.Count) return;
        Floors.Move(i, j);
        FloorsChanged($"已調整樓層順序：{string.Join("、", Floors)}。");
    }

    /// <summary>刪除目前樓層：還有元素時擋下；至少保留一層。</summary>
    [RelayCommand]
    void DeleteFloor()
    {
        int i = Floors.IndexOf(CurrentFloor);
        if (i < 0) return;
        if (Floors.Count == 1)
        {
            Status = "至少要有一個樓層。";
            return;
        }
        int count = Elements.Count(e => !e.IsConveyor && !e.IsElevator && SameFloor(FloorOf(e), CurrentFloor));
        var elevators = Elements.Where(e => e.IsElevator && OnFloor(e, CurrentFloor)).ToList();
        if (count > 0)
        {
            Status = $"樓層「{CurrentFloor}」還有 {count} 個元素，請先把它們移到其他樓層或刪除。";
            return;
        }
        if (elevators.Count > 0)
        {
            Status = $"樓層「{CurrentFloor}」是 {string.Join("、", elevators.Select(e => $"「{e.Name}」"))}服務的樓層，請先從電梯的服務樓層移除。";
            return;
        }
        var name = CurrentFloor;
        _floorHeights.Remove(name);
        Floors.RemoveAt(i);
        CurrentFloor = Floors[Math.Max(0, i - 1)];
        FloorsChanged($"已刪除樓層「{name}」。");
    }

    /// <summary>把選取的元素移到 MoveTargetFloor（座標不變）。</summary>
    [RelayCommand]
    void MoveSelectionToFloor()
    {
        if (MoveTargetFloor is not { } target) return;
        var items = Selection.Where(e => (e.HasGeometry || e.IsRail) && !e.IsElevator).ToList(); // 電梯以服務的樓層表示
        Batch(() =>
        {
            foreach (var e in items) e.Floor = target;
        });
        Status = $"已把 {items.Count} 個元素移到「{target}」。";
    }
}

/// <summary>屬性面板「服務的樓層」的一個勾選框。</summary>
public sealed partial class ServedFloorChoice(string floor, bool isChecked) : ObservableObject
{
    public string Floor { get; } = floor;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;
}
