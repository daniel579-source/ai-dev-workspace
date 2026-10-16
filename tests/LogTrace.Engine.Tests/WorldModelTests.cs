using LogTrace.Engine.Cases;
using LogTrace.Engine.Events;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;

namespace LogTrace.Engine.Tests;

// 世界模型（ADR-0005）。測試資料：tests/fixtures/playback-prototype（虛構，情境沿用 prototype/playback-ui 原型）
public sealed class WorldModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly Case _case;
    readonly PlantConfigDocument _plant;
    readonly List<LogEvent> _events;
    readonly WorldModel _world;

    public WorldModelTests()
    {
        _plant = PlantConfigFile.Load(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"));
        _case = new CaseLibrary(Path.Combine(_tmp.Path, "Cases")).Create("回放", _plant.ToPlantConfig());
        _case.Import(Path.Combine(TempFolder.Fixture("playback-prototype"), "logs"));
        _events = _case.GetEvents(1, int.MaxValue).ToList();
        _world = new WorldModel(_case.GetEffects(), _plant.Layout, _plant.PresenceSignal, snapshotEvery: 4);
    }

    public void Dispose()
    {
        _case.Dispose();
        _tmp.Dispose();
    }

    /// <summary>訊息含 text 的事件序號（唯一）。</summary>
    long Seq(string text) => _events.Single(e => e.Message.Contains(text)).Sequence;

    WorldState At(string text) => _world.StateAt(Seq(text));

    string MessageOf(long? sequence) => _events.Single(e => e.Sequence == sequence).Message;

    [Fact]
    public void 回放開始時_所有站點與搬送機都是未知()
    {
        var w = _world.StateAt(0);

        Assert.Empty(w.Stations);
        Assert.Empty(w.Vehicles);
        Assert.Empty(w.Pallets);
    }

    [Fact]
    public void 站點模式與在席燈()
    {
        var w = At("站點模式代號 -> 1");
        Assert.Equal("1", w.Stations["st-1"].Mode);
        Assert.Null(w.Stations["st-1"].Presence); // 在席還沒有紀錄：未知

        w = _world.StateAt(_events.First(e => e.Message.Contains("[A-01][Station_Status_Change] => 在席 = ON")).Sequence); // 第一次
        Assert.True(w.Stations["st-1"].Presence);
        Assert.False(w.Stations.ContainsKey("st-2")); // 其他站點仍是未知
    }

    [Fact]
    public void 入庫_讀到棧板_派給搬送機_抵達站點_抵達儲位_完成()
    {
        var w = At("讀到棧板條碼 PLT-EX-0001");
        var p = w.Pallets["PLT-EX-0001"];
        Assert.Equal((LocationKind.Station, "st-1"), (p.Location, p.LocationId));
        Assert.Equal(["D-0001-1", "D-0001-2", "D-0001-3", "D-0001-4"], p.Bins);
        Assert.False(p.Preexisting);

        w = At("派送 00040001 第1段");
        Assert.Equal((LocationKind.Vehicle, "vh-2"), (w.Pallets["PLT-EX-0001"].Location, w.Pallets["PLT-EX-0001"].LocationId));
        Assert.Equal((VehicleWork.Executing, "00040001", "1003"), (w.Vehicles["vh-2"].Work, w.Vehicles["vh-2"].Command, w.Vehicles["vh-2"].Destination));
        Assert.Equal((CommandStatus.SegmentRunning, 1), (w.Commands["00040001"].Status, w.Commands["00040001"].Segment));

        w = At("00040001 第1段 抵達 1003");
        Assert.Equal((LocationKind.Station, "st-3"), (w.Pallets["PLT-EX-0001"].Location, w.Pallets["PLT-EX-0001"].LocationId));
        Assert.Equal(VehicleWork.Idle, w.Vehicles["vh-2"].Work);

        w = At("00040001 第2段 抵達 0100302");
        Assert.Equal((LocationKind.Storage, "0100302"), (w.Pallets["PLT-EX-0001"].Location, w.Pallets["PLT-EX-0001"].LocationId));
        Assert.Equal("PLT-EX-0001", w.Storage["0100302"]);
        Assert.Equal(CommandStatus.SegmentArrived, w.Commands["00040001"].Status);

        w = At("完成回報 00040001");
        Assert.Equal(CommandStatus.Completed, w.Commands["00040001"].Status);
        Assert.False(_world.IsUnfinishedInCase("00040001"));
    }

    [Fact]
    public void 搬送機的連線與模式_第一次切換之前是未知()
    {
        var w = At("SRCA01 連線狀態 ON-LINE");
        Assert.Equal(("ON-LINE", "自動"), (w.Vehicles["vh-1"].Connection, w.Vehicles["vh-1"].Mode));
        Assert.Null(w.Vehicles["vh-2"].Connection); // SSCA01 已經執行過命令，但連線狀態還沒有紀錄

        Assert.Equal("手動", At("SSCA01 連線狀態 ON-LINE，手動模式").Vehicles["vh-2"].Mode);
        var after = At("SSCA01 切換為自動模式").Vehicles["vh-2"];
        Assert.Equal(("ON-LINE", "自動"), (after.Connection, after.Mode)); // 只改模式，連線維持
    }

    [Fact]
    public void 出庫_案件開始前已存在的棧板_從儲位取走()
    {
        var w = At("建立任務 00040002");
        var p = w.Pallets["PLT-EX-0002"];
        Assert.True(p.Preexisting);
        Assert.Null(p.Bins); // 桶清單不知道
        Assert.Equal((LocationKind.Storage, "0100501"), (p.Location, p.LocationId));
        Assert.Equal("PLT-EX-0002", w.Storage["0100501"]);

        w = At("派送 00040002 第1段");
        Assert.False(w.Storage.ContainsKey("0100501"));
        Assert.Equal(LocationKind.Vehicle, w.Pallets["PLT-EX-0002"].Location);
    }

    [Fact]
    public void 命令說貨物抵達站點_但在席一直是OFF_標示不一致()
    {
        var w = At("00040002 第1段 抵達 1004");

        Assert.Equal((LocationKind.Station, "st-4"), (w.Pallets["PLT-EX-0002"].Location, w.Pallets["PLT-EX-0002"].LocationId));
        Assert.False(w.Stations["st-4"].Presence);
        Assert.True(w.IsInconsistent("st-4"));
        Assert.False(w.IsInconsistent("st-1"));
    }

    [Fact]
    public void 警報_發生到復歸為異常中_沒有復歸的到案件結束都是異常()
    {
        var raised = At("警報發生 SSCA01 E101");
        Assert.Equal("E101", raised.Alarms["vh-2"].Code);
        Assert.False(_world.IsAlarmNeverReset("vh-2", raised.Alarms["vh-2"]));

        Assert.False(At("警報復歸 SSCA01 E101").Alarms.ContainsKey("vh-2"));

        var station = At("警報發生 A-02 E205").Alarms["st-2"];
        Assert.True(_world.Final.Alarms.ContainsKey("st-2"));
        Assert.True(_world.IsAlarmNeverReset("st-2", station)); // 「未見復歸紀錄」
    }

    [Fact]
    public void 交給對接設備_棧板已交出_命令完成()
    {
        var w = At("00040002 第2段 抵達 EQ901");

        Assert.Equal((LocationKind.Delivered, "eq-1"), (w.Pallets["PLT-EX-0002"].Location, w.Pallets["PLT-EX-0002"].LocationId));
        Assert.Equal(CommandStatus.Delivered, w.Commands["00040002"].Status);
        Assert.Equal(CommandStatus.Delivered, At("完成回報 00040002").Commands["00040002"].Status); // 完成回報不會蓋掉「已交出」
    }

    [Fact]
    public void 案件結束時還沒完成的命令_標示案件範圍內未完成()
    {
        var w = _world.Final;

        Assert.Equal(CommandStatus.SegmentRunning, w.Commands["00040003"].Status);
        Assert.True(_world.IsUnfinishedInCase("00040003"));
        Assert.Equal((LocationKind.Vehicle, "vh-2"), (w.Pallets["PLT-EX-0003"].Location, w.Pallets["PLT-EX-0003"].LocationId));
    }

    [Fact]
    public void 沒有對應到Layout的編號_不產生元素狀態()
    {
        var w = _world.Final;

        Assert.DoesNotContain(w.Stations.Keys, k => !k.StartsWith("st-"));
        Assert.DoesNotContain(w.Vehicles.Keys, k => !k.StartsWith("vh-"));
        Assert.Equal(["st-1", "st-3", "st-4"], w.Stations.Keys); // A-02 只有警報，沒有在席等紀錄
    }

    [Fact]
    public void 從頭逐筆推導與從快照開始推導_每一筆事件的結果完全相同()
    {
        Assert.True(_world.SnapshotCount > 3);

        foreach (var e in _events.Prepend(null))
        {
            long seq = e?.Sequence ?? 0;
            Assert.Equal(_world.ReplayFromStart(seq).Fingerprint(), _world.StateAt(seq).Fingerprint());
        }
    }

    [Fact]
    public void 每個顯示的狀態都能追溯到造成它的事件()
    {
        var w = _world.Final;

        Assert.Contains("在席 = OFF", MessageOf(w.Stations["st-1"].PresenceCause));
        Assert.Contains("站點模式代號", MessageOf(w.Stations["st-1"].ModeCause));
        Assert.Contains("取物可 = ON", MessageOf(w.Stations["st-1"].Signals["取物可"].Cause));
        Assert.Contains("切換為自動模式", MessageOf(w.Vehicles["vh-2"].ModeCause));
        Assert.Contains("派送 00040003", MessageOf(w.Vehicles["vh-2"].WorkCause));
        Assert.Contains("警報發生 A-02", MessageOf(w.Alarms["st-2"].Cause));
        Assert.Contains("00040002 第2段 抵達 EQ901", MessageOf(w.Pallets["PLT-EX-0002"].LocationCause));
        Assert.Contains("完成回報 00040001", MessageOf(w.Commands["00040001"].StatusCause));
        Assert.All(w.Pallets.Values, p => Assert.Contains(_events, e => e.Sequence == p.LocationCause));
    }

    [Theory]
    [InlineData("RRCCCLL", "0100802", "01", 8, 2)]
    [InlineData("RRCCCLL", "0100302", "01", 3, 2)]
    [InlineData("LLRRCCC", "0201015", "01", 15, 2)]
    public void 儲位編號的拆解與組合(string rule, string code, string row, int col, int level)
    {
        Assert.True(StorageCode.TryParse(rule, code, out var parts));
        Assert.Equal((row, col, level), parts);
        Assert.Equal(code, StorageCode.Format(rule, row, col, level));
    }

    [Theory]
    [InlineData("RRCCCLL", "010080")]
    [InlineData("RRCCCLL", "01008A2")]
    [InlineData(null, "0100802")]
    public void 不符合儲位規則的編號不是儲位(string? rule, string code)
    {
        Assert.False(StorageCode.TryParse(rule, code, out _));
    }

    [Fact]
    public void 效果規則缺少必要的欄位時_檢查會指出()
    {
        var problem = Parsing.FieldExtractor.Check(new ParseRule("派送", null, null, @"派送 (?<command>[0-9]{8})", Parsing.EffectKinds.SegmentDispatched));

        Assert.Contains("需要欄位", problem);
        Assert.Contains("vehicle", problem);
        Assert.Contains("不認得", Parsing.FieldExtractor.Check(new ParseRule("x", null, null, @"(?<command>[0-9]+)", "teleport")));
    }
}
