using LogTrace.Tests.Support;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// Layout 回放：Layout 面板、詳細資訊、料架側視圖（#24）。測試資料：tests/fixtures/playback-prototype（虛構）
public sealed class PlaybackViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly MainViewModel _vm;

    public PlaybackViewModelTests() =>
        _vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), new FakeUserInteraction(), Path.Combine(_tmp.Path, "Cases"), _ => false);

    public void Dispose()
    {
        _vm.Dispose();
        _tmp.Dispose();
    }

    PlaybackViewModel P => _vm.Playback;

    PlaybackElementViewModel El(string id) => P.Elements.Single(e => e.Id == id);

    async Task OpenPrototypeCase()
    {
        var plants = Directory.CreateDirectory(Path.Combine(_tmp.Path, "Plants")).FullName;
        File.Copy(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"), Path.Combine(plants, "範例廠.json"));
        _vm.RefreshPlants();
        _vm.SelectedPlant = _vm.Plants.Single(p => p.Path is not null);
        _vm.NewCaseName = "回放";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
        await _vm.ImportCommand.ExecuteAsync(null);
    }

    /// <summary>選取訊息含 text 的事件：目前時刻移到這一筆之後。</summary>
    void MoveTo(string text) => _vm.SelectedEvent = _vm.Events.First(e => e.Event.Message.Contains(text));

    [Fact]
    public async Task Layout面板列出所有元素_回放開始時都是未知()
    {
        await OpenPrototypeCase();

        Assert.Null(P.Message);
        Assert.Equal(11, P.Elements.Count);
        _vm.SeekTime(DateTime.MinValue); // 第一筆事件之前
        Assert.StartsWith("案件開始", P.MomentText);
        Assert.All(P.Elements.Where(e => !e.IsAuxiliary), e => Assert.Equal(ElementLook.Unknown, e.Look));
        Assert.Equal("◌", El("st-1").PresenceLight);
        Assert.Null(El("vh-1").PresenceLight);
        Assert.Equal(ElementLook.Auxiliary, El("cv-1").Look);
    }

    [Fact]
    public async Task 目前時刻隨選取的事件移動_站點顯示在席燈與貨物()
    {
        await OpenPrototypeCase();

        MoveTo("讀到棧板條碼 PLT-EX-0001");

        Assert.Contains("10:00:06.0000", P.MomentText);
        Assert.Equal(("●", ElementLook.Occupied, "PLT-EX-0001"), (El("st-1").PresenceLight, El("st-1").Look, El("st-1").Summary));
        Assert.Equal(ElementLook.Unknown, El("st-2").Look);
    }

    [Fact]
    public async Task 不一致與搬送機執行中()
    {
        await OpenPrototypeCase();

        MoveTo("派送 00040001 第1段");
        Assert.Equal((ElementLook.Executing, "00040001 → 1003"), (El("vh-2").Look, El("vh-2").Summary));

        MoveTo("00040002 第1段 抵達 1004");
        Assert.True(El("st-4").IsInconsistent);
        Assert.Equal("○", El("st-4").PresenceLight);
    }

    [Fact]
    public async Task 詳細資訊_每個狀態都附上造成它的事件_可以跳過去()
    {
        await OpenPrototypeCase();
        MoveTo("00040002 第1段 抵達 1004");

        P.SelectElementCommand.Execute(El("st-4"));

        var presence = P.Detail.Single(d => d.Label == "在席");
        Assert.Equal("OFF", presence.Value);
        Assert.Contains("[A-04][Station_Status_Change] => 在席 = OFF", presence.CauseText);
        Assert.Contains(P.Detail, d => d.Label == "貨物" && d.Value.Contains("PLT-EX-0002") && d.Value.Contains("案件開始前已存在"));
        Assert.Contains(P.Detail, d => d.Label == "不一致");
        Assert.Equal("未知", P.Detail.Single(d => d.Label == "站點模式").Value); // 推不出來就是未知
        Assert.Null(P.Detail.Single(d => d.Label == "站點模式").Cause);

        P.GoToCauseCommand.Execute(presence.Cause);
        Assert.Equal(presence.Cause, _vm.SelectedEvent!.Sequence);
        Assert.Contains("在席 = OFF", _vm.SelectedEvent.Event.Message);
    }

    [Fact]
    public async Task 搬送機的詳細資訊_連線未知_命令案件範圍內未完成()
    {
        await OpenPrototypeCase();
        MoveTo("派送 00040003 第1段");

        P.SelectElementCommand.Execute(El("vh-2"));

        Assert.Equal("執行中：命令 00040003 → 1003", P.Detail.Single(d => d.Label == "作業").Value);
        Assert.Contains("案件範圍內未完成", P.Detail.Single(d => d.Label == "命令").Value);
        Assert.Contains(P.Detail, d => d.Label == "載著" && d.Value.StartsWith("PLT-EX-0003（桶 3 個"));

        MoveTo("派送 00040001 第1段");
        Assert.Equal("未知", P.Detail.Single(d => d.Label == "連線").Value);
    }

    [Fact]
    public async Task 警報_異常中_沒有復歸的標示未見復歸紀錄()
    {
        await OpenPrototypeCase();

        MoveTo("警報發生 SSCA01 E101");
        Assert.Equal(ElementLook.Alarm, El("vh-2").Look);
        MoveTo("警報復歸 SSCA01 E101");
        Assert.NotEqual(ElementLook.Alarm, El("vh-2").Look);

        MoveTo("警報發生 A-02 E205");
        P.SelectElementCommand.Execute(El("st-2"));
        Assert.Equal(ElementLook.Alarm, El("st-2").Look);
        Assert.Contains("未見復歸紀錄", P.Detail.Single(d => d.Label == "異常中").Value);
    }

    [Fact]
    public async Task 對接設備顯示已交出的棧板()
    {
        await OpenPrototypeCase();
        MoveTo("00040002 第2段 抵達 EQ901");

        P.SelectElementCommand.Execute(El("eq-1"));

        Assert.Equal("已交出 1", El("eq-1").Summary);
        Assert.Contains(P.Detail, d => d.Label == "已交出" && d.Value.StartsWith("PLT-EX-0002"));
    }

    [Fact]
    public async Task 料架以有貨的層數表示_點一列顯示側視圖()
    {
        await OpenPrototypeCase();
        MoveTo("00040001 第2段 抵達 0100302");
        var rack = El("rk-1");

        Assert.Equal(10, rack.Cells.Count);
        Assert.Equal(1, rack.Cells.Single(c => c.Column == 3).Occupied);
        Assert.Equal(0, rack.Cells.Single(c => c.Column == 5).Occupied); // 0100501 的棧板要到出庫命令建立後才知道：此時是未知，不猜測
        Assert.Equal(ElementLook.Occupied, rack.Look);

        P.SelectRackCell(rack.Cells.Single(c => c.Column == 3));

        Assert.Same(rack, P.SelectedElement);
        Assert.Contains("第 3 列", P.SideViewTitle);
        Assert.Equal([5, 4, 3, 2, 1], P.SideView.Select(l => l.Level));
        var level2 = P.SideView.Single(l => l.Level == 2);
        Assert.Equal(("0100302", "PLT-EX-0001"), (level2.StorageCode, level2.Pallet));
        Assert.Contains("抵達 0100302", level2.CauseText);
        Assert.All(P.SideView.Where(l => l.Level != 2), l => Assert.Null(l.Pallet));

        MoveTo("派送 00040003 第1段"); // 時刻移動時側視圖跟著更新
        Assert.Equal("PLT-EX-0001", P.SideView.Single(l => l.Level == 2).Pallet);
    }

    [Fact]
    public async Task 內建預設的案件沒有Layout_顯示說明()
    {
        _vm.NewCaseName = "預設";
        _vm.CreateCaseCommand.Execute(null);
        _vm.ImportSource = Path.Combine(TempFolder.Fixture("playback-prototype"), "logs");
        await _vm.ImportCommand.ExecuteAsync(null);

        Assert.Empty(P.Elements);
        Assert.Contains("內建預設", P.Message);
    }

    [Fact]
    public async Task 回放畫面的畫布依元素範圍調整大小_也可以縮放()
    {
        await OpenPrototypeCase();
        var plant = Engine.Plants.PlantConfigFile.Load(Path.Combine(TempFolder.Fixture("playback-prototype"), "plant.json"));

        Assert.Equal(Engine.Plants.LayoutGeometry.CanvasSize(plant.Layout.Elements), (P.CanvasWidth, P.CanvasHeight));
        Assert.True(P.CanvasWidth >= 60 && P.CanvasHeight >= 40);

        P.ZoomStep(zoomIn: false);
        Assert.Equal(0.833, P.Zoom);
        P.FitZoom(P.CanvasWidth * 16, P.CanvasHeight * 32, pixelsPerMeter: 32);
        Assert.Equal(0.5, P.Zoom);
    }

    [Fact]
    public void 輸送線連到搬送機時_回放畫面畫到交接點_其他端畫到中心()
    {
        var st = new Engine.Plants.LayoutElement { Id = "st-1", Type = Engine.Plants.LayoutElementType.Station, X = 1, Y = 1, W = 2, H = 2 };
        var vh = new Engine.Plants.LayoutElement { Id = "vh-1", Type = Engine.Plants.LayoutElementType.Vehicle, X = 10, Y = 1, W = 2.5, H = 1.2 };
        var cv = new Engine.Plants.LayoutElement
        {
            Id = "cv-1", Type = Engine.Plants.LayoutElementType.Conveyor, From = "st-1", To = "vh-1", ToPoint = new(10, 1.5),
        };

        var el = new PlaybackElementViewModel(cv, new Dictionary<string, Engine.Plants.LayoutElement> { ["st-1"] = st, ["vh-1"] = vh });

        Assert.Equal((2.0, 2.0, 10.0, 1.5), (el.LineX1, el.LineY1, el.LineX2, el.LineY2));
    }
}
