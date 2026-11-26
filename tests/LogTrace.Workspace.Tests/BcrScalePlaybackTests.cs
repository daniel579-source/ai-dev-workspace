using LogTrace.Engine.Events;
using LogTrace.Engine.Parsing;
using LogTrace.Engine.Playback;
using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;
using LogTrace.Workspace.Playback;

namespace LogTrace.Workspace.Tests;

// BCR 與磅秤的回放與編輯（ADR-0027）。LOG、編號、條碼、重量與時間全部是虛構的。
public sealed class BcrScalePlaybackTests
{
    static readonly DateTime T0 = new(2030, 1, 8, 10, 0, 0);

    // 事件序號 → 時間（秒）
    static readonly int[] Seconds = [0, 0, 5, 9, 12, 20, 25, 40, 41];

    static LogEvent Event(long seq) =>
        new(seq, new StableEventId("PLC", "20300108", "PLC/log.txt", (int)seq), T0.AddSeconds(Seconds[seq]), "", "INFO", "PLC", "PLCLOG", $"虛構事件 {seq}",
            new EventSource("PLC/log.txt", (int)seq, (int)seq, 0, 0), EventFlags.None);

    static EventEffect E(long seq, string effect, params (string Kind, string Value)[] p) =>
        new(seq, effect, p.ToDictionary(x => x.Kind, x => (IReadOnlyList<string>)[x.Value]));

    static readonly LayoutSection Layout = new()
    {
        Elements =
        [
            new() { Id = "st-1", Type = LayoutElementType.Conv, Name = "入庫口1", Aliases = ["A-01"], X = 0, Y = 0, W = 2, H = 2, Rotation = 0 },
            new() { Id = "bc-1", Type = LayoutElementType.Bcr, Name = "棧板 BCR", Owner = "st-1", X = 2.1, Y = 0, W = 0.5, H = 0.5, Rotation = 0 },
            new() { Id = "bc-2", Type = LayoutElementType.Bcr, Name = "桶 BCR", Owner = "st-1", Reads = BcrReads.Bin, Aliases = ["A-01_DRUM"], X = 2.1, Y = 1, W = 0.5, H = 0.5, Rotation = 0 },
            new() { Id = "sc-1", Type = LayoutElementType.Scale, Name = "磅秤", Owner = "st-1", X = 0, Y = 2.5, W = 1.5, H = 1.5, Rotation = 0 },
        ],
    };

    static PlaybackViewModel Load()
    {
        var effects = new List<EventEffect>
        {
            E(1, EffectKinds.PalletRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Pallet, "P-001")),
            E(2, EffectKinds.BcrReadStart, (FieldKinds.Bcr, "A-01")),
            E(3, EffectKinds.BcrRead, (FieldKinds.Bcr, "A-01"), (FieldKinds.Barcode, "PLT-EX-0000123")),
            E(4, EffectKinds.BcrRead, (FieldKinds.Bcr, "A-01_DRUM"), (FieldKinds.Barcode, "DR-01,DR-02,DR-03")),
            E(5, EffectKinds.WeightLimits, (FieldKinds.Pallet, "P-001"), (FieldKinds.MaxWeight, "500"), (FieldKinds.MinWeight, "100")),
            E(6, EffectKinds.ScaleRead, (FieldKinds.Conv, "A-01"), (FieldKinds.Weight, "612")),
            E(7, EffectKinds.BcrFailed, (FieldKinds.Conv, "A-01"), (FieldKinds.ErrorCode, "E901")),
            E(8, EffectKinds.BcrManual, (FieldKinds.Conv, "A-01"), (FieldKinds.Barcode, "PLT-EX-0002")),
        };
        var p = new PlaybackViewModel();
        p.Load(new WorldModel(effects, Layout, "在席"), Layout, Enumerable.Range(1, 8).Select(i => Event(i)).ToList(), hasStateRules: true);
        return p;
    }

    static PlaybackElementViewModel El(PlaybackViewModel p, string id) => p.Elements.Single(e => e.Id == id);

    [Fact]
    public void 讀取中顯示秒數與掃描光束_讀到後顯示條碼末碼與幾秒前()
    {
        var p = Load();

        p.SetMoment(2, T0.AddSeconds(8)); // 開始讀取後 3 秒
        var bcr = El(p, "bc-1");
        Assert.True(bcr.IsReading);
        Assert.Equal(ElementLook.Reading, bcr.Look);
        Assert.Equal("讀取中 3 秒", bcr.Summary);
        Assert.Equal(1, p.Elements.Count(e => e.IsReading && e.Id == "sc-1")); // 棧板 BCR 開始讀取：磅秤等待中（推估）
        Assert.True(El(p, "sc-1").IsEstimated);
        Assert.StartsWith("等待中（推估）", El(p, "sc-1").Summary);
        Assert.True(Assert.Single(p.SceneNow.Elements, s => s.Id == "bc-1").Reading);

        p.SetMoment(3, T0.AddSeconds(11));
        Assert.False(bcr.IsReading);
        Assert.Equal(ElementLook.ReadOk, bcr.Look);
        Assert.Equal("…000123（2 秒前）", bcr.Summary);
    }

    [Fact]
    public void 桶BCR標籤顯示桶數_元素狀態列出完整條碼與加入的棧板()
    {
        var p = Load();
        p.SetMoment(4);

        Assert.Equal("桶 3 個（0 秒前）", El(p, "bc-2").Summary);
        p.SelectedElement = El(p, "bc-2");
        Assert.Equal(["DR-01", "DR-02", "DR-03"], p.Detail.Where(d => d.Label == "條碼").Select(d => d.Value));
        Assert.Contains(p.Detail, d => d.Label == "加入棧板" && d.Value.StartsWith("P-001"));
        Assert.Contains(p.Detail, d => d.Label == "所屬 Conv" && d.Value == "入庫口1");
    }

    [Fact]
    public void 磅秤超重時紅色且列入只看異常_元素狀態列出上下限與判定()
    {
        var p = Load();
        p.SetMoment(6);

        var scale = El(p, "sc-1");
        Assert.Equal(ElementLook.Alarm, scale.Look);
        Assert.Equal("612 kg 超重（0 秒前）", scale.Summary);
        p.OnlyAbnormal = true;
        Assert.Equal(["sc-1"], p.Equipment.Select(e => e.Id));
        p.SelectedElement = scale;
        Assert.Contains(p.Detail, d => d.Label == "上下限" && d.Value == "下限 100 kg～上限 500 kg" && d.Cause == 5);
        Assert.Contains(p.Detail, d => d.Label == "判定" && d.Value == "超重（異常）");
    }

    [Fact]
    public void 讀取失敗算異常_手動輸入後改為橘色手動()
    {
        var p = Load();

        p.SetMoment(7);
        var bcr = El(p, "bc-1");
        Assert.Equal(ElementLook.Alarm, bcr.Look);
        Assert.True(bcr.IsDeviceAbnormal);
        Assert.Equal("讀取失敗 E901（0 秒前）", bcr.Summary);
        p.OnlyAbnormal = true;
        Assert.Contains(p.Equipment, e => e.Id == "bc-1");

        p.SetMoment(8);
        Assert.Equal(ElementLook.Manual, bcr.Look);
        Assert.False(bcr.IsDeviceAbnormal);
        Assert.Equal("手動 …X-0002（0 秒前）", bcr.Summary); // 條碼超過 8 個字只顯示末 6 碼
        Assert.DoesNotContain(p.Equipment, e => e.Id == "bc-1");
    }

    [Fact]
    public void 結果超過2分鐘的標籤變淡_讀取中與選取中不變淡()
    {
        var p = Load();

        p.SetMoment(3, T0.AddSeconds(9 + 120)); // 讀到後剛好 2 分鐘：不變淡
        var bcr = El(p, "bc-1");
        Assert.False(bcr.IsStale);
        p.SetMoment(3, T0.AddSeconds(9 + 121));
        Assert.True(bcr.IsStale);
        Assert.True(Assert.Single(p.SceneNow.Elements, s => s.Id == "bc-1").Stale);

        p.SelectedElement = bcr; // 選取中不變淡
        Assert.False(bcr.IsStale);
        Assert.False(Assert.Single(p.SceneNow.Elements, s => s.Id == "bc-1").Stale);
        p.SelectedElement = null;
        Assert.True(bcr.IsStale);

        p.SetMoment(2, T0.AddSeconds(5 + 300)); // 讀取中很久：不變淡
        Assert.True(bcr.IsReading);
        Assert.False(bcr.IsStale);
        Assert.False(El(p, "sc-1").IsStale); // 等待中（推估）也不變淡
    }

    [Fact]
    public void 異常的標籤超過2分鐘也變淡()
    {
        var p = Load();
        p.SetMoment(7, T0.AddSeconds(40 + 200));

        Assert.True(El(p, "bc-1").IsDeviceAbnormal);
        Assert.True(El(p, "bc-1").IsStale);
    }

    [Fact]
    public void 場景資料_BCR帶有所屬Conv台面中心的對準位置()
    {
        var scene = PlaybackScene.LayoutOf(Layout);

        Assert.Equal([1.0, 1.0, 0.8], scene.Elements.Single(e => e.Id == "bc-1").Aim!);
        Assert.Equal("scale", scene.Elements.Single(e => e.Id == "sc-1").Type);
    }
}

public sealed class BcrScaleEditorTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly PlantConfigLibrary _library;
    readonly LayoutEditorViewModel _vm;

    public BcrScaleEditorTests()
    {
        _library = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        _vm = new LayoutEditorViewModel(_library, new FakeUserInteraction());
    }

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void 先選取Conv再新增BCR與磅秤_自動指定所屬並放在旁邊_刪除Conv時清除所屬()
    {
        Assert.Contains(LayoutElementTypes.Groups[0].Types, t => t == LayoutElementType.Bcr);
        Assert.Contains(LayoutElementTypes.Groups[0].Types, t => t == LayoutElementType.Scale);
        _vm.AddElementCommand.Execute(LayoutElementType.Conv);
        var conv = _vm.SelectedElement!;

        _vm.AddElementCommand.Execute(LayoutElementType.Bcr);
        var bcr = _vm.SelectedElement!;
        Assert.Equal(("bc-1", "BCR", conv.Id), (bcr.Id, bcr.TypeLabel, bcr.Owner));
        Assert.True(bcr.HasOwner && bcr.CanHaveAliases);
        Assert.True(bcr.X >= conv.X + conv.W);
        Assert.Equal([conv.Id], _vm.OwnerChoices.Select(c => c.Id));
        bcr.IsBinBcr = true;
        Assert.Equal(BcrReads.Bin, bcr.ToModel().Reads);

        _vm.SelectedElement = conv;
        _vm.AddElementCommand.Execute(LayoutElementType.Scale);
        var scale = _vm.SelectedElement!;
        Assert.Equal(("磅秤", conv.Id), (scale.TypeLabel, scale.Owner));
        Assert.Null(scale.ToModel().Reads);

        _vm.SelectedElement = conv;
        _vm.DeleteSelectedCommand.Execute(null);
        Assert.Null(bcr.Owner);
        Assert.Null(scale.Owner);
    }

    [Fact]
    public void 沒有選取Conv時新增BCR_所屬未指定_可以從下拉選單選()
    {
        _vm.AddElementCommand.Execute(LayoutElementType.Conv);
        var conv = _vm.SelectedElement!;
        _vm.SelectedElement = null;

        _vm.AddElementCommand.Execute(LayoutElementType.Bcr);
        var bcr = _vm.SelectedElement!;
        Assert.Null(bcr.Owner);
        Assert.Contains(_vm.OwnerChoices, c => c.Id == conv.Id);
        bcr.Owner = conv.Id;
        _vm.ClearOwnerCommand.Execute(null);
        Assert.Null(bcr.Owner);
    }
}
