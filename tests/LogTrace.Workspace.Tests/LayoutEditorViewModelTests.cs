using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;

namespace LogTrace.Workspace.Tests;

// Layout 編輯器（ADR-0006）。內容全部是虛構的。
public sealed class LayoutEditorViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new();
    readonly PlantConfigLibrary _library;
    readonly LayoutEditorViewModel _vm;

    public LayoutEditorViewModelTests()
    {
        _library = new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants"));
        _vm = new LayoutEditorViewModel(_library, _ui);
    }

    public void Dispose() => _tmp.Dispose();

    LayoutElementViewModel Add(LayoutElementType type)
    {
        _vm.AddElementCommand.Execute(type);
        return _vm.SelectedElement!;
    }

    [Fact]
    public void 新增各種元素_有預設尺寸並選取()
    {
        foreach (var type in LayoutElementTypes.Addable.Where(t => t != LayoutElementType.Conveyor))
        {
            var e = Add(type);
            Assert.Equal(type, e.Type);
            Assert.True(e.W > 0 && e.H > 0);
        }
        var conveyor = Add(LayoutElementType.Conveyor); // 已有站點、搬送機與對接設備，可以加輸送線

        Assert.Equal(LayoutElementType.Conveyor, conveyor.Type);
        Assert.Equal(["st-1", "vh-1"], new[] { conveyor.From!, conveyor.To! });
        Assert.NotNull(conveyor.ToPoint); // 連到搬送機的那一端有交接點
        Assert.Equal(7, _vm.Elements.Count);
        Assert.Equal(7, _vm.Elements.Select(e => e.Id).Distinct().Count());
        Assert.True(_vm.IsDirty);
    }

    [Fact]
    public void 元素清單先依類型_同類型依名稱自然排序_名稱空白的在前_不改變畫布與存檔順序()
    {
        var dock = Add(LayoutElementType.Dock);
        dock.Name = "出貨口";
        var s10 = Add(LayoutElementType.Station);
        s10.Name = "站點10";
        var vehicle = Add(LayoutElementType.Vehicle);
        var s2 = Add(LayoutElementType.Station);
        s2.Name = "站點2";
        var blank = Add(LayoutElementType.Station);
        blank.Name = "";
        var conveyor = Add(LayoutElementType.Conveyor);
        var created = _vm.Elements.ToList();

        Assert.Equal([blank, s2, s10, vehicle, dock, conveyor], _vm.ElementList);
        Assert.Equal([blank, s2, s10, vehicle, dock], _vm.ConveyorEndpoints);
        Assert.Equal(created, _vm.Elements); // 畫布上下層與存檔順序不變
    }

    [Fact]
    public void 改名稱後重新排序_選取與輸送線兩端不變()
    {
        var a = Add(LayoutElementType.Station);
        a.Name = "A-4";
        var b = Add(LayoutElementType.Station);
        b.Name = "A-12";
        var conveyor = Add(LayoutElementType.Conveyor);
        (conveyor.From, conveyor.To) = (a.Id, b.Id);
        _vm.SelectedElement = b;

        b.Name = "A-3";

        Assert.Equal([b, a], _vm.ConveyorEndpoints);
        Assert.Equal([b, a, conveyor], _vm.ElementList);
        Assert.Same(b, _vm.SelectedElement);
        Assert.Equal((a.Id, b.Id), (conveyor.From, conveyor.To));
    }

    [Theory]
    [InlineData("站點2", "站點10")]
    [InlineData("A-4", "A-12")]
    [InlineData("a-1", "B-1")]
    [InlineData("", "A")]
    [InlineData("站點2", "站點2A")]
    public void 自然排序(string first, string second)
    {
        Assert.True(LayoutElementTypes.NaturalCompare(first, second) < 0);
        Assert.True(LayoutElementTypes.NaturalCompare(second, first) > 0);
    }

    [Fact]
    public void 畫布依元素範圍自動加大_拖到靠近邊緣時跟著變大_大小不寫進設定檔()
    {
        Assert.Equal((60.0, 40.0), (_vm.CanvasWidth, _vm.CanvasHeight)); // 空白時為最小值
        var e = Add(LayoutElementType.Station);
        (e.X, e.Y) = (50, 30);

        _vm.BeginDrag(e);
        _vm.DragMoveBy(30, 15); // 拖到原本畫布的外面
        _vm.EndDrag();

        Assert.Equal((80.0, 45.0), (e.X, e.Y));
        Assert.Equal((80 + e.W + 20, 45 + e.H + 20), (_vm.CanvasWidth, _vm.CanvasHeight));
        Assert.Equal($"{_vm.CanvasWidth:0} × {_vm.CanvasHeight:0} 公尺", _vm.CanvasSizeText);

        _vm.PlantName = "範例廠";
        _vm.SaveCommand.Execute(null);
        Assert.False(_vm.IsDirty, _vm.Status);
        Assert.DoesNotContain("canvas", File.ReadAllText(_vm.FilePath!), StringComparison.OrdinalIgnoreCase);
        Assert.Equal((102.0, 67.0), (_vm.CanvasWidth, _vm.CanvasHeight)); // 重新開啟時依元素範圍算出

        _vm.Elements.Single().X = 1; // 移回來：寬度縮回最小值
        Assert.Equal((60.0, 67.0), (_vm.CanvasWidth, _vm.CanvasHeight));
    }

    [Fact]
    public void 縮放_Ctrl滾輪一級為1點2倍_有上下限_符合視窗_不影響吸附()
    {
        _vm.ZoomStep(zoomIn: true);
        Assert.Equal(1.2, _vm.Zoom);
        Assert.Equal("120%", _vm.ZoomText);
        for (var i = 0; i < 50; i++) _vm.ZoomStep(zoomIn: false);
        Assert.Equal(LayoutZoom.Min, _vm.Zoom);
        _vm.Zoom = 99;
        Assert.Equal(LayoutZoom.Max, _vm.Zoom);

        _vm.FitZoom(960, 1000, pixelsPerMeter: 32); // 畫布 60 × 40 公尺 = 1920 × 1280 像素
        Assert.Equal(0.5, _vm.Zoom);

        var e = Add(LayoutElementType.Station);
        (e.X, e.Y) = (3, 4);
        _vm.BeginDrag(e);
        _vm.DragMoveBy(1.3, -0.74); // 拖曳位移以公尺計，縮放後吸附仍為 0.5 公尺
        Assert.Equal((4.5, 3.5), (e.X, e.Y));
    }

    [Fact]
    public void 沒有兩個站點或對接設備時_不能加輸送線()
    {
        Add(LayoutElementType.Station);

        _vm.AddElementCommand.Execute(LayoutElementType.Conveyor);

        Assert.DoesNotContain(_vm.Elements, e => e.IsConveyor);
        Assert.Contains("至少兩個", _vm.Status);
    }

    [Fact]
    public void 拖曳移動_吸附到半公尺格線_不超出左上角()
    {
        var e = Add(LayoutElementType.Station);
        (e.X, e.Y) = (3, 4);

        _vm.BeginDrag(e);
        _vm.DragMoveBy(1.3, -0.74);
        Assert.Equal((4.5, 3.5), (e.X, e.Y));

        _vm.DragMoveBy(0.2, 0.26); // 位移以開始拖曳時為準，不累加
        Assert.Equal((3.0, 4.5), (e.X, e.Y));

        _vm.DragMoveBy(-10, -10);
        Assert.Equal((0.0, 0.0), (e.X, e.Y));
        _vm.EndDrag();

        _vm.DragMoveBy(5, 5); // 結束拖曳後不再移動
        Assert.Equal((0.0, 0.0), (e.X, e.Y));
    }

    [Fact]
    public void 調整大小_吸附到格線_最小一格()
    {
        var e = Add(LayoutElementType.Area);
        (e.W, e.H) = (6, 4);

        _vm.BeginDrag(e);
        _vm.DragResizeBy(1.2, -0.3);
        Assert.Equal((7.0, 3.5), (e.W, e.H));

        _vm.DragResizeBy(-100, -100);
        Assert.Equal((0.5, 0.5), (e.W, e.H));
    }

    [Fact]
    public void 旋轉90度後調整大小_畫面橫向對應元素的深()
    {
        var e = Add(LayoutElementType.Rack);
        (e.W, e.H) = (12, 1.5);
        _vm.SetRotationCommand.Execute(90);

        _vm.BeginDrag(e);
        _vm.DragResizeBy(0, 2); // 畫面往下拉：旋轉 90 度時是元素的寬

        Assert.Equal((14.0, 1.5), (e.W, e.H));
    }

    [Fact]
    public void 旋轉只允許0_90_180_270度()
    {
        var e = Add(LayoutElementType.Vehicle);

        _vm.RotateClockwiseCommand.Execute(null);
        _vm.RotateClockwiseCommand.Execute(null);
        Assert.Equal(180, e.Rotation);
        _vm.RotateClockwiseCommand.Execute(null);
        _vm.RotateClockwiseCommand.Execute(null);
        Assert.Equal(0, e.Rotation);

        _vm.SetRotationCommand.Execute(270);
        Assert.Equal(270, e.Rotation);
        _vm.SetRotationCommand.Execute(45);
        Assert.Equal(270, e.Rotation);
        Assert.Contains("只能是 0、90、180、270", _vm.Status);
    }

    [Fact]
    public void 移動站點時_輸送線的端點跟著移動()
    {
        var a = Add(LayoutElementType.Station);
        var b = Add(LayoutElementType.Station);
        (b.X, b.Y) = (10, 1);
        var cv = Add(LayoutElementType.Conveyor);

        _vm.BeginDrag(a);
        _vm.DragMoveBy(2, 3);

        Assert.Equal((a.CenterX, a.CenterY), (cv.LineX1, cv.LineY1));
        Assert.Equal((b.CenterX, b.CenterY), (cv.LineX2, cv.LineY2));
    }

    [Fact]
    public void 輸送線連到搬送機_交接點在搬送機邊緣_線畫到交接點_改回站點時清除()
    {
        var st = Add(LayoutElementType.Station);
        (st.X, st.Y) = (1, 1);
        var vh = Add(LayoutElementType.Vehicle);
        (vh.X, vh.Y, vh.W, vh.H) = (10, 1, 2.5, 1.2);
        var st2 = Add(LayoutElementType.Station);
        var cv = Add(LayoutElementType.Conveyor);
        Assert.Equal(["st-1", "vh-1"], new[] { cv.From!, cv.To! });

        Assert.Equal(new LayoutPoint(10, 2), cv.ToPoint); // 站點中心 (2, 2) 在左邊：搬送機的左邊緣
        Assert.Null(cv.FromPoint);
        Assert.Equal((10.0, 2.0), (cv.LineX2, cv.LineY2));
        Assert.Equal((st.CenterX, st.CenterY), (cv.LineX1, cv.LineY1));

        cv.To = st2.Id;
        Assert.Null(cv.ToPoint);
        Assert.Equal((st2.CenterX, st2.CenterY), (cv.LineX2, cv.LineY2));

        cv.From = vh.Id; // 起點改成搬送機
        Assert.NotNull(cv.FromPoint);
        Assert.Equal((cv.FromPoint!.X, cv.FromPoint.Y), (cv.LineX1, cv.LineY1));
    }

    [Fact]
    public void 拖曳交接點_吸附到半公尺_搬送機移動時交接點不動_存檔讀回不變()
    {
        Add(LayoutElementType.Station);
        var vh = Add(LayoutElementType.Vehicle);
        (vh.X, vh.Y) = (10, 1);
        var cv = Add(LayoutElementType.Conveyor);
        cv.Bidirectional = true;

        _vm.BeginTransferPointDrag(cv, from: false);
        _vm.DragTransferPointTo(11.3, 0.8);
        _vm.EndDrag();
        Assert.Equal(new LayoutPoint(11.5, 1), cv.ToPoint);
        Assert.Equal((11.5, 1.0), (cv.LineX2, cv.LineY2));

        _vm.BeginDrag(vh);
        _vm.DragMoveBy(3, 3);
        _vm.EndDrag();
        Assert.Equal(new LayoutPoint(11.5, 1), cv.ToPoint);

        _vm.PlantName = "範例廠";
        _vm.SaveCommand.Execute(null);
        Assert.False(_vm.IsDirty, _vm.Status);
        var saved = PlantConfigFile.Load(_vm.FilePath!).Layout.Elements.Single(e => e.Id == cv.Id);
        Assert.True(saved.Bidirectional);
        Assert.Equal(new LayoutPoint(11.5, 1), saved.ToPoint);
    }

    [Fact]
    public void 刪除搬送機時_連到它的輸送線一起刪除()
    {
        Add(LayoutElementType.Station);
        var vh = Add(LayoutElementType.Vehicle);
        Add(LayoutElementType.Conveyor);

        _vm.SelectedElement = vh;
        _vm.DeleteSelectedCommand.Execute(null);

        Assert.DoesNotContain(_vm.Elements, e => e.IsConveyor);
    }

    [Fact]
    public void 別名_可以有多個_已屬於其他元素時擋下並指出是哪一個()
    {
        var st = Add(LayoutElementType.Station);
        st.Name = "入庫口1";
        _vm.NewAlias = "A-01";
        _vm.AddAliasCommand.Execute(null);
        _vm.NewAlias = "1001";
        _vm.AddAliasCommand.Execute(null);
        Assert.Equal(["A-01", "1001"], st.Aliases);

        var vh = Add(LayoutElementType.Vehicle);
        _vm.NewAlias = "a-01"; // 大小寫視為相同
        _vm.AddAliasCommand.Execute(null);

        Assert.Empty(vh.Aliases);
        Assert.Contains("已經屬於「入庫口1」", _vm.Status);
    }

    [Fact]
    public void 輔助元素不能加別名()
    {
        Add(LayoutElementType.Area);
        _vm.NewAlias = "Z-01";

        _vm.AddAliasCommand.Execute(null);

        Assert.Empty(_vm.SelectedElement!.Aliases);
        Assert.Contains("不能加別名", _vm.Status);
    }

    [Fact]
    public void 自訂屬性_新增與移除()
    {
        var e = Add(LayoutElementType.Station);

        _vm.AddPropertyCommand.Execute(null);
        _vm.AddPropertyCommand.Execute(null);
        e.Props[0].Key = "樓層";
        e.Props[0].Value = "1F";
        _vm.RemovePropertyCommand.Execute(e.Props[1]);

        Assert.Equal([("樓層", "1F")], e.Props.Select(p => (p.Key, p.Value)));
    }

    [Fact]
    public void 存檔再從清單開啟_元素都在_修訂號遞增()
    {
        _vm.PlantName = "範例廠";
        var st = Add(LayoutElementType.Station);
        _vm.NewAlias = "A-01";
        _vm.AddAliasCommand.Execute(null);
        _vm.AddPropertyCommand.Execute(null);
        Add(LayoutElementType.Rack);

        _vm.SaveCommand.Execute(null);
        Assert.Equal(1, _vm.Revision);
        Assert.False(_vm.IsDirty);
        _vm.SaveCommand.Execute(null);
        Assert.Equal(2, _vm.Revision);

        var other = new LayoutEditorViewModel(_library, _ui);
        var path = Assert.Single(other.Configs);
        other.OpenConfigCommand.Execute(path);

        Assert.Equal("範例廠", other.PlantName);
        Assert.Equal(2, other.Revision);
        Assert.Equal(["st-1", "rk-1"], other.Elements.Select(e => e.Id));
        Assert.Equal(["A-01"], other.Elements[0].Aliases);
        Assert.Equal("屬性1", other.Elements[0].Props.Single().Key);
        Assert.Equal(st.X, other.Elements[0].X);
    }

    [Fact]
    public void 站點模式名稱_新增移除_存檔後讀回_代號重複時無法存檔()
    {
        _vm.PlantName = "範例廠";
        _vm.AddStationModeNameCommand.Execute(null);
        (_vm.StationModeNames[0].Code, _vm.StationModeNames[0].Name) = (" 1 ", "虛構模式甲");
        _vm.AddStationModeNameCommand.Execute(null);
        _vm.RemoveStationModeNameCommand.Execute(_vm.StationModeNames[1]);
        Assert.True(_vm.IsDirty);

        _vm.SaveCommand.Execute(null);
        var other = new LayoutEditorViewModel(_library, _ui);
        other.OpenConfigCommand.Execute(Assert.Single(other.Configs));
        Assert.Equal([("1", "虛構模式甲")], other.StationModeNames.Select(r => (r.Code, r.Name)));

        other.AddStationModeNameCommand.Execute(null);
        (other.StationModeNames[1].Code, other.StationModeNames[1].Name) = ("1", "虛構模式乙");
        other.SaveCommand.Execute(null);
        Assert.Contains("代號「1」重複", other.Status);
        Assert.Equal(1, other.Revision);
    }

    [Fact]
    public void 別名重複時無法存檔_標出衝突的元素()
    {
        _vm.PlantName = "範例廠";
        var a = Add(LayoutElementType.Station);
        a.Aliases.Add("A-01");
        var b = Add(LayoutElementType.Dock);
        b.Aliases.Add("A-01"); // 繞過輸入檢查，例如手動修改過的檔案
        var c = Add(LayoutElementType.Station);

        _vm.SaveCommand.Execute(null);

        Assert.Null(_vm.FilePath);
        Assert.Empty(_vm.Configs);
        Assert.StartsWith("無法存檔：", _vm.Status);
        Assert.Contains("A-01", _vm.Status);
        Assert.True(a.HasIssue);
        Assert.True(b.HasIssue);
        Assert.False(c.HasIssue);
        Assert.Same(a, _vm.SelectedElement);
    }

    [Fact]
    public void 開啟較新格式版本的檔案時_明確提示_編輯中的內容不變()
    {
        var path = Path.Combine(_tmp.Path, "新版.json");
        File.WriteAllText(path, """{ "formatVersion": 9, "plant": "虛構" }""");
        Add(LayoutElementType.Station);
        _ui.ConfirmResult = true;
        _ui.PlantConfigFileResult = path;

        _vm.OpenFileCommand.Execute(null);

        Assert.Contains("格式版本是 9", _vm.Status);
        Assert.Single(_vm.Elements);
    }

    [Fact]
    public void 有未存檔的修改時_新建要先確認()
    {
        Add(LayoutElementType.Station);

        _ui.ConfirmResult = false;
        _vm.NewCommand.Execute(null);
        Assert.Single(_vm.Elements);

        _ui.ConfirmResult = true;
        _vm.NewCommand.Execute(null);
        Assert.Empty(_vm.Elements);
        Assert.False(_vm.IsDirty);
    }

    [Fact]
    public void 刪除站點時_連到它的輸送線一起刪除()
    {
        var a = Add(LayoutElementType.Station);
        Add(LayoutElementType.Station);
        Add(LayoutElementType.Conveyor);
        _vm.SelectedElement = a;

        _vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal(["st-2"], _vm.Elements.Select(e => e.Id));
    }

    [Fact]
    public void 另存新檔()
    {
        _vm.PlantName = "範例廠";
        Add(LayoutElementType.Station);
        var path = Path.Combine(_tmp.Path, "分享", "範例廠.json");
        _ui.PlantConfigSavePathResult = path;

        _vm.SaveAsCommand.Execute(null);

        Assert.Equal(path, _vm.FilePath);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void 主畫面可以開啟Layout編輯器()
    {
        using var main = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, Path.Combine(_tmp.Path, "Cases"), _ => false);

        main.OpenLayoutEditorCommand.Execute(null);

        Assert.NotNull(_ui.ShownEditor);
    }
}
