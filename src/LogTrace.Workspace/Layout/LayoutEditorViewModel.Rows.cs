using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Plants;

namespace LogTrace.Workspace.Layout;

/// <summary>排的配置表的一列（在選取 RM 的屬性面板中編輯，#123 使用者決定）。深淺：單深、淺、深。</summary>
public sealed partial class RowItemViewModel(string row, string depth, string pair) : ObservableObject
{
    public const string Single = "單深";
    public const string Shallow = "淺";
    public const string Deep = "深";

    public static IReadOnlyList<string> DepthChoices { get; } = [Single, Shallow, Deep];

    [ObservableProperty]
    public partial string Row { get; set; } = row;

    [ObservableProperty]
    public partial string Depth { get; set; } = depth;

    /// <summary>配對的排（雙深時填）。</summary>
    [ObservableProperty]
    public partial string Pair { get; set; } = pair;

    public LayoutRow ToModel(string rm) => new(Row.Trim(), rm,
        Depth switch { Shallow => RowDepth.Shallow, Deep => RowDepth.Deep, _ => null },
        Depth is Shallow or Deep && Pair.Trim().Length > 0 ? Pair.Trim() : null);

    public static RowItemViewModel From(LayoutRow r) =>
        new(r.Row, r.Depth switch { RowDepth.Shallow => Shallow, RowDepth.Deep => Deep, _ => Single }, r.Pair ?? "");
}

// 排的配置表（ADR-0019 第 3 點，選填）：選取 RM 時列出它負責的排，可以新增、修改、刪除；存檔時寫入 layout.rows。
public sealed partial class LayoutEditorViewModel
{
    List<LayoutRow> _rows = [];

    /// <summary>選取的 RM 負責的排。</summary>
    public ObservableCollection<RowItemViewModel> RmRows { get; } = [];

    public IReadOnlyList<string> RowDepthChoices => RowItemViewModel.DepthChoices;

    bool _refreshingRows;

    void LoadRows(LayoutSection layout) => _rows = [.. layout.Rows ?? []];

    List<LayoutRow>? RowsModel() => _rows.Count > 0 ? [.. _rows] : null;

    void RefreshRmRows()
    {
        _refreshingRows = true;
        try
        {
            foreach (var r in RmRows) r.PropertyChanged -= OnRowChanged;
            RmRows.Clear();
            if (SelectedElement is not { IsRm: true } rm) return;
            foreach (var r in _rows.Where(r => r.Rm == rm.Id))
            {
                var item = RowItemViewModel.From(r);
                item.PropertyChanged += OnRowChanged;
                RmRows.Add(item);
            }
        }
        finally
        {
            _refreshingRows = false;
        }
    }

    void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => WriteBackRows();

    /// <summary>把屬性面板的排寫回配置表（保持其他 RM 的排）。</summary>
    void WriteBackRows()
    {
        if (_refreshingRows || SelectedElement is not { IsRm: true } rm) return;
        _rows = [.. _rows.Where(r => r.Rm != rm.Id), .. RmRows.Select(r => r.ToModel(rm.Id))];
        IsDirty = true;
        Commit("rows:" + rm.Id); // 連續輸入算一步，可以 Ctrl+Z 復原
    }

    /// <summary>為選取的 RM 新增一排（排號為配置表中下一個沒用過的數字）。</summary>
    [RelayCommand]
    void AddRmRow()
    {
        if (SelectedElement is not { IsRm: true } rm) return;
        int n = 1;
        while (_rows.Any(r => r.Row.Trim() == n.ToString(System.Globalization.CultureInfo.InvariantCulture))) n++;
        var item = new RowItemViewModel(n.ToString(System.Globalization.CultureInfo.InvariantCulture), RowItemViewModel.Single, "");
        item.PropertyChanged += OnRowChanged;
        RmRows.Add(item);
        WriteBackRows();
        Status = $"已為「{rm.Name}」新增第 {item.Row} 排；雙深時請填深淺與配對的排。";
    }

    [RelayCommand]
    void RemoveRmRow(RowItemViewModel? item)
    {
        if (item is null || !RmRows.Remove(item)) return;
        item.PropertyChanged -= OnRowChanged;
        WriteBackRows();
    }

    /// <summary>刪除 RM 時，它的排一併從配置表移除。</summary>
    void RemoveRowsOf(IReadOnlySet<string> removedIds)
    {
        if (_rows.RemoveAll(r => r.Rm is { } rm && removedIds.Contains(rm)) > 0) IsDirty = true;
    }
}
