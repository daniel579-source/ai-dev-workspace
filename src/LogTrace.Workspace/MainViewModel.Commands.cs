using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Parsing;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace;

/// <summary>命令清單的一列（ADR-0008 第 6 點）。</summary>
/// <param name="CurrentStatus">目前時刻的狀態；推不出來時為「—」。</param>
/// <param name="FinalStatus">到案件結束時的狀態（已完成，或「案件範圍內未完成」）。</param>
public sealed partial class CommandRowViewModel(string id, long eventCount, string finalStatus) : ObservableObject
{
    public string Id { get; } = id;
    public long EventCount { get; } = eventCount;
    public string FinalStatus { get; } = finalStatus;

    [ObservableProperty]
    public partial string CurrentStatus { get; set; } = "—";
}

/// <summary>回放畫面的「未對應」清單：目前時刻附近出現、但 Layout 沒有對應元素的編號。</summary>
/// <param name="NearbyCount">目前時刻前後 30 秒內出現的事件數。</param>
/// <param name="TotalCount">整個案件中出現的事件數。</param>
public sealed record UnmappedNearbyViewModel(string KindLabel, string Value, long NearbyCount, long TotalCount);

/// <summary>命令清單與未對應清單（ADR-0005 第 6 點、ADR-0008 第 4、6 點）。</summary>
public sealed partial class MainViewModel
{
    IReadOnlyList<(string Kind, string Value, long Count)> _identifierTotals = [];

    /// <summary>案件中出現的命令（依命令編號）。</summary>
    public ObservableCollection<CommandRowViewModel> Commands { get; } = [];

    [ObservableProperty]
    public partial CommandRowViewModel? SelectedCommand { get; set; }

    /// <summary>以選取的命令編號串起來的所有跨模組事件（依時間）。</summary>
    public ObservableCollection<EventRowViewModel> CommandEvents { get; } = [];

    /// <summary>目前時刻前後 30 秒內出現、但 Layout 沒有對應元素的站點、搬送機、對接設備編號。</summary>
    public ObservableCollection<UnmappedNearbyViewModel> UnmappedNearby { get; } = [];

    partial void OnSelectedCommandChanged(CommandRowViewModel? value)
    {
        CommandEvents.Clear();
        if (value is null || _case is null) return;
        var seqs = _case.FindSequences([FieldKinds.Command], [value.Id]).ToHashSet();
        foreach (var r in _allRows.Where(r => seqs.Contains(r.Sequence))) CommandEvents.Add(r);
    }

    /// <summary>點命令的一筆事件：跳到該事件的時刻（被篩選掉時改為顯示全部）。</summary>
    [RelayCommand]
    void GoToCommandEvent(EventRowViewModel? row)
    {
        if (row is not null) GoToEvent(row.Sequence);
    }

    /// <summary>開啟案件後：命令清單與各命令到案件結束時的狀態。</summary>
    void LoadCommands()
    {
        Commands.Clear();
        CommandEvents.Clear();
        SelectedCommand = null;
        UnmappedNearby.Clear();
        if (_case is null) return;
        _identifierTotals = _case.GetIdentifierCounts();
        var final = Playback.Model?.Final;
        foreach (var (id, count) in _case.GetCommandCounts().OrderBy(c => c.Command, StringComparer.Ordinal))
        {
            string finalStatus = final?.Commands.TryGetValue(id, out var c) == true
                ? (c.IsFinished ? Playback.StatusText(c) : $"案件範圍內未完成（{Playback.StatusText(c)}）")
                : "—";
            Commands.Add(new CommandRowViewModel(id, count, finalStatus));
        }
        UpdateMomentPanels();
    }

    /// <summary>目前時刻改變時：命令的目前狀態、未對應清單。</summary>
    void UpdateMomentPanels()
    {
        var state = Playback.Model is null ? null : Playback.State;
        foreach (var row in Commands)
            row.CurrentStatus = state?.Commands.TryGetValue(row.Id, out var c) == true ? Playback.StatusText(c) : "—";
        UpdateUnmappedNearby();
    }

    void UpdateUnmappedNearby()
    {
        UnmappedNearby.Clear();
        if (_case is null || CurrentTime is not { } now || _allRows.Count == 0) return;
        var from = LastAtOrBefore(_allRows, now - TimelineViewModel.DetailHalfWidth.Add(TimeSpan.FromTicks(1)));
        var to = LastAtOrBefore(_allRows, now + TimelineViewModel.DetailHalfWidth);
        long fromSeq = from is null ? _allRows[0].Sequence : from.Sequence + 1, toSeq = to?.Sequence ?? 0;
        if (toSeq < fromSeq) return;
        var aliases = Playback.Elements.SelectMany(e => e.Model.Aliases ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var totals = _identifierTotals.ToLookup(x => x.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, value, count) in _case.GetIdentifierCounts(fromSeq, toSeq).Where(x => !aliases.Contains(x.Value)))
            UnmappedNearby.Add(new UnmappedNearbyViewModel(FieldKinds.Label(kind), value, count, totals[value].Sum(t => t.Count)));
    }
}
