using System.Text;

namespace LogTrace.Engine.Playback;

// 世界狀態（ADR-0005）。每個狀態值都記錄造成它的事件序號（Cause），讓畫面可以追溯到原始事件。
// 沒有任何事件確定過的值為 null（未知），不猜測（ADR-0005 第 7 點）。

/// <summary>站點的狀態（ADR-0005 第 3 點）。</summary>
public sealed class StationState
{
    /// <summary>在席：true＝ON、false＝OFF、null＝未知。</summary>
    public bool? Presence { get; set; }
    public long? PresenceCause { get; set; }

    /// <summary>站點模式代號；代號的意義待確認，直接顯示。</summary>
    public string? Mode { get; set; }
    public long? ModeCause { get; set; }

    /// <summary>運轉狀態（例如自動、手動），照 LOG 的文字；和站點模式分開（#70）。</summary>
    public string? Status { get; set; }
    public long? StatusCause { get; set; }

    /// <summary>其他訊號（取物可、置物可等）的 ON／OFF。</summary>
    public SortedDictionary<string, (bool On, long Cause)> Signals { get; } = new(StringComparer.Ordinal);

    public StationState Clone()
    {
        var c = new StationState { Presence = Presence, PresenceCause = PresenceCause, Mode = Mode, ModeCause = ModeCause, Status = Status, StatusCause = StatusCause };
        foreach (var (k, v) in Signals) c.Signals[k] = v;
        return c;
    }
}

/// <summary>搬送機的作業（ADR-0005 第 4 點）。</summary>
public enum VehicleWork
{
    Unknown,
    Idle,
    Executing,
}

/// <summary>搬送機的狀態：連線、模式、作業三層，彼此獨立（ADR-0005 第 4 點）。</summary>
public sealed class VehicleState
{
    /// <summary>ON-LINE／OFF-LINE；null＝未知（第一次切換之前）。</summary>
    public string? Connection { get; set; }
    public long? ConnectionCause { get; set; }

    /// <summary>自動／手動；null＝未知。</summary>
    public string? Mode { get; set; }
    public long? ModeCause { get; set; }

    public VehicleWork Work { get; set; }
    public long? WorkCause { get; set; }
    public string? Command { get; set; }
    public string? Destination { get; set; }

    public VehicleState Clone() => (VehicleState)MemberwiseClone();
}

/// <summary>未復歸的警報（ADR-0005 第 5 點）。</summary>
public sealed record AlarmState(string? Code, long Cause);

/// <summary>貨物所在位置的種類（ADR-0005 第 2 點）。</summary>
public enum LocationKind
{
    /// <summary>位置不明（例如 Layout 中沒有對應的編號）。</summary>
    Unknown,
    Station,
    Vehicle,
    Storage,
    /// <summary>已交給對接設備（已交出），從畫面上移除。</summary>
    Delivered,
}

/// <summary>貨物（棧板與桶清單，ADR-0005 第 1、2 點）。</summary>
public sealed class PalletState
{
    public required string Id { get; init; }

    /// <summary>桶清單；null＝不知道（例如案件開始前就存在的棧板）。</summary>
    public IReadOnlyList<string>? Bins { get; set; }

    public LocationKind Location { get; set; }

    /// <summary>位置的識別：站點、搬送機、對接設備為 Layout 元素 id；儲位為儲位編號；不明時為 LOG 中的編號。</summary>
    public string? LocationId { get; set; }

    public long LocationCause { get; set; }

    /// <summary>LOG 第一次提到時就已經在某處（例如出庫命令的來源儲位）：案件開始前已存在（ADR-0005 第 2 點）。</summary>
    public bool Preexisting { get; init; }

    public PalletState Clone() => (PalletState)MemberwiseClone();
}

/// <summary>命令的狀態（ADR-0005 第 6 點）。</summary>
public enum CommandStatus
{
    Created,
    SegmentRunning,
    SegmentArrived,
    /// <summary>一段作業完成，但 LOG 沒有記錄地點（不推導，#53）。</summary>
    SegmentFinished,
    Completed,
    /// <summary>已完成，貨物已交給對接設備。</summary>
    Delivered,
    /// <summary>已取消（LOG 樣式待確認；由廠別設定的規則判斷）。</summary>
    Cancelled,
}

/// <summary>命令（一趟搬送任務，分成多段）。</summary>
public sealed class CommandState
{
    public required string Id { get; init; }
    public string? Type { get; set; }
    public string? Pallet { get; set; }
    public string? Source { get; set; }
    public string? Destination { get; set; }
    public CommandStatus Status { get; set; }
    public long StatusCause { get; set; }
    public int? Segment { get; set; }
    public string? Vehicle { get; set; }
    public string? SegmentDestination { get; set; }

    public bool IsFinished => Status is CommandStatus.Completed or CommandStatus.Delivered or CommandStatus.Cancelled;

    public CommandState Clone() => (CommandState)MemberwiseClone();
}

/// <summary>某一時刻的世界狀態。</summary>
public sealed class WorldState
{
    /// <summary>最後套用的事件序號；0 表示還沒有任何事件。</summary>
    public long Sequence { get; set; }

    public SortedDictionary<string, StationState> Stations { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, VehicleState> Vehicles { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, AlarmState> Alarms { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, PalletState> Pallets { get; } = new(StringComparer.Ordinal);

    /// <summary>儲位編號 → 棧板（逐格記錄，ADR-0005 第 1 點）。</summary>
    public SortedDictionary<string, string> Storage { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, CommandState> Commands { get; } = new(StringComparer.Ordinal);

    public WorldState Clone()
    {
        var c = new WorldState { Sequence = Sequence };
        foreach (var (k, v) in Stations) c.Stations[k] = v.Clone();
        foreach (var (k, v) in Vehicles) c.Vehicles[k] = v.Clone();
        foreach (var (k, v) in Alarms) c.Alarms[k] = v;
        foreach (var (k, v) in Pallets) c.Pallets[k] = v.Clone();
        foreach (var (k, v) in Storage) c.Storage[k] = v;
        foreach (var (k, v) in Commands) c.Commands[k] = v.Clone();
        return c;
    }

    public IEnumerable<PalletState> PalletsAt(LocationKind kind, string id) =>
        Pallets.Values.Where(p => p.Location == kind && string.Equals(p.LocationId, id, StringComparison.Ordinal));

    /// <summary>不一致（ADR-0005 第 3 點）：命令說貨物在站點上，但站點的在席是 OFF。不判斷哪一邊才對。</summary>
    public bool IsInconsistent(string stationId) =>
        Stations.TryGetValue(stationId, out var s) && s.Presence == false && PalletsAt(LocationKind.Station, stationId).Any();

    /// <summary>完整內容的文字表示，用來比對兩個狀態是否完全相同（測試用）。</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        sb.Append("seq=").Append(Sequence).Append('\n');
        foreach (var (k, s) in Stations)
            sb.Append($"st {k} p={s.Presence}@{s.PresenceCause} m={s.Mode}@{s.ModeCause} r={s.Status}@{s.StatusCause} {string.Join(",", s.Signals.Select(x => $"{x.Key}={x.Value.On}@{x.Value.Cause}"))}\n");
        foreach (var (k, v) in Vehicles)
            sb.Append($"vh {k} c={v.Connection}@{v.ConnectionCause} m={v.Mode}@{v.ModeCause} w={v.Work}@{v.WorkCause} {v.Command}->{v.Destination}\n");
        foreach (var (k, a) in Alarms) sb.Append($"al {k} {a.Code}@{a.Cause}\n");
        foreach (var (k, p) in Pallets)
            sb.Append($"pl {k} {p.Location}:{p.LocationId}@{p.LocationCause} pre={p.Preexisting} bins={(p.Bins is null ? "?" : string.Join(",", p.Bins))}\n");
        foreach (var (k, v) in Storage) sb.Append($"sg {k}={v}\n");
        foreach (var (k, c) in Commands)
            sb.Append($"cm {k} {c.Type} {c.Pallet} {c.Source}->{c.Destination} {c.Status}@{c.StatusCause} seg={c.Segment} {c.Vehicle}->{c.SegmentDestination}\n");
        return sb.ToString();
    }
}
