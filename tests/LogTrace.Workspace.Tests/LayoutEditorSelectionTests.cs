using LogTrace.Engine.Plants;
using LogTrace.Tests.Support;
using LogTrace.Workspace.Layout;

namespace LogTrace.Workspace.Tests;

// Layout 編輯器的框選、剪貼簿與復原（ADR-0006 第 10 點，#64）。內容全部是虛構的。
public sealed class LayoutEditorSelectionTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly LayoutEditorViewModel _vm;

    public LayoutEditorSelectionTests() =>
        _vm = new LayoutEditorViewModel(new PlantConfigLibrary(Path.Combine(_tmp.Path, "Plants")), new FakeUserInteraction());

    public void Dispose() => _tmp.Dispose();

    LayoutElementViewModel Add(LayoutElementType type, double x, double y)
    {
        _vm.AddElementCommand.Execute(type);
        var e = _vm.SelectedElement!;
        (e.X, e.Y) = (x, y);
        return e;
    }

    LayoutElementViewModel Conveyor(LayoutElementViewModel from, LayoutElementViewModel to)
    {
        _vm.AddElementCommand.Execute(LayoutElementType.Conveyor);
        var c = _vm.SelectedElement!;
        (c.From, c.To) = (from.Id, to.Id);
        return c;
    }

    /// <summary>站點 st-1 (1,1)、st-2 (6,1)、st-3 (1,10)，各 2 × 2；輸送線 cv-1：st-1 → st-2，cv-2：st-2 → st-3。</summary>
    (LayoutElementViewModel A, LayoutElementViewModel B, LayoutElementViewModel C, LayoutElementViewModel AB, LayoutElementViewModel BC) Sample()
    {
        var a = Add(LayoutElementType.Station, 1, 1);
        var b = Add(LayoutElementType.Station, 6, 1);
        var c = Add(LayoutElementType.Station, 1, 10);
        return (a, b, c, Conveyor(a, b), Conveyor(b, c));
    }

    [Fact]
    public void 框選_碰到就算_輸送線也算_Ctrl加選_屬性面板只在單選時顯示()
    {
        var (a, b, c, ab, bc) = Sample();

        _vm.SelectInRect(0, 0, 1.5, 1.5, add: false); // 只碰到 st-1 的角
        Assert.Same(a, _vm.SelectedElement);
        Assert.Equal(1, _vm.SelectionCount);

        _vm.SelectInRect(4, 1.5, 4.5, 2.5, add: false); // 只碰到 st-1 → st-2 的線（中心 y = 2）
        Assert.Equal([ab], _vm.Selection);

        _vm.SelectInRect(0, 0, 9, 3, add: false); // st-1、st-2 與兩者之間的線；st-2 → st-3 的線從 (7,2) 開始也碰到
        Assert.Equal([a, b, ab, bc], _vm.Selection);
        Assert.Null(_vm.SelectedElement); // 多選時屬性面板不顯示
        Assert.False(_vm.HasSelection);
        Assert.Contains("已選取 4 個元素", _vm.SelectionHint);

        _vm.SelectInRect(0, 9, 4, 12, add: true); // Ctrl：加選 st-3
        Assert.Contains(c, _vm.Selection);
        _vm.ToggleSelect(c); // Ctrl＋點選：取消
        Assert.DoesNotContain(c, _vm.Selection);
        Assert.False(c.IsSelected);

        _vm.SelectInRect(30, 30, 40, 40, add: false); // 沒碰到任何元素
        Assert.Empty(_vm.Selection);
    }

    [Fact]
    public void 多選時拖曳一起移動_整組不超出左上角_調整大小只改拖曳的那個()
    {
        var (a, b, c, _, _) = Sample();
        _vm.SelectInRect(0, 0, 9, 3, add: false);

        _vm.BeginDrag(b);
        _vm.DragMoveBy(-3, 2);
        _vm.EndDrag();
        Assert.Equal((0.0, 3.0, 5.0, 3.0), (a.X, a.Y, b.X, b.Y)); // 整組只能往左移 1，保持相對位置
        Assert.Equal((1.0, 10.0), (c.X, c.Y)); // 沒選取的不動

        _vm.BeginDrag(b);
        _vm.DragResizeBy(1, 1);
        _vm.EndDrag();
        Assert.Equal((3.0, 3.0, 2.0, 2.0), (b.W, b.H, a.W, a.H));

        _vm.BeginDrag(c); // 拖曳沒選取的元素：改為只選取它
        Assert.Equal([c], _vm.Selection);
        _vm.EndDrag();
    }

    [Fact]
    public void Del刪除所有選取的元素與連到它們的輸送線_Ctrl_Z一次復原()
    {
        var (a, b, c, ab, bc) = Sample();
        _vm.SelectedElement = a;
        _vm.ToggleSelect(c);

        _vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal(["st-2"], _vm.Elements.Select(e => e.Id)); // 兩條輸送線都連到被刪除的站點
        Assert.Contains("2 個元素", _vm.Status);
        Assert.Contains("2 條輸送線", _vm.Status);
        Assert.False(_vm.DeleteSelectedCommand.CanExecute(null));

        _vm.UndoCommand.Execute(null);
        Assert.Equal(["st-1", "st-2", "st-3", "cv-1", "cv-2"], _vm.Elements.Select(e => e.Id));
        Assert.Equal(("st-2", "st-3"), (_vm.Elements[4].From, _vm.Elements[4].To));
    }

    [Fact]
    public void 複製貼上_新id_別名與屬性照抄_以滑鼠位置為整組左上角_兩端都選取的輸送線改連到新元素()
    {
        var (a, b, _, _, _) = Sample();
        _vm.SelectedElement = a;
        _vm.NewAlias = "A-01";
        _vm.AddAliasCommand.Execute(null);
        _vm.AddPropertyCommand.Execute(null);
        a.Name = "入庫站";
        a.Rotation = 90;
        _vm.SelectInRect(0, 0, 9, 3, add: false); // st-1、st-2、cv-1、cv-2（cv-2 的 st-3 端沒選取）

        _vm.CopyCommand.Execute(null);
        _vm.Paste(20.2, 30.4);

        var pasted = _vm.Selection;
        Assert.Equal(["st-4", "st-5", "cv-3", "cv-4"], pasted.Select(e => e.Id)); // 貼上後選取新元素
        var (na, nb, nab, nbc) = (pasted[0], pasted[1], pasted[2], pasted[3]);
        Assert.Equal(("入庫站", 90, 2.0, 2.0), (na.Name, na.Rotation, na.W, na.H));
        Assert.Equal(["A-01"], na.Aliases);
        Assert.Single(na.Props);
        Assert.Equal((20.0, 30.5, 25.0, 30.5), (na.X, na.Y, nb.X, nb.Y)); // 吸附後的滑鼠位置是整組的左上角
        Assert.Equal(("st-4", "st-5"), (nab.From, nab.To)); // 兩端都選取：改連到新元素
        Assert.Equal(("st-5", "st-3"), (nbc.From, nbc.To)); // 只有一端選取：另一端連回原本的元素
        Assert.Equal((a.X, a.Y), (1.0, 1.0)); // 原本的不動

        // 別名重複不在貼上時擋，由存檔前檢查擋下
        _vm.PlantName = "範例廠";
        _vm.SaveCommand.Execute(null);
        Assert.True(_vm.IsDirty);
        Assert.Contains("別名「A-01」", _vm.Status);

        _vm.UndoCommand.Execute(null); // 貼上算一步
        Assert.Equal(5, _vm.Elements.Count);
    }

    [Fact]
    public void 貼上_滑鼠不在畫布上時放在原位置往右下1公尺_交接點隨搬送機平移()
    {
        var st = Add(LayoutElementType.Station, 1, 1);
        var vh = Add(LayoutElementType.Vehicle, 10, 1);
        var cv = Conveyor(st, vh);
        var point = cv.ToPoint!;
        _vm.SelectInRect(0, 0, 13, 3, add: false);

        _vm.CopyCommand.Execute(null);
        _vm.Paste(null, null);

        var pasted = _vm.Selection;
        Assert.Equal((2.0, 2.0, 11.0, 2.0), (pasted[0].X, pasted[0].Y, pasted[1].X, pasted[1].Y));
        Assert.Equal(new LayoutPoint(point.X + 1, point.Y + 1), pasted[2].ToPoint);
        Assert.Equal(new LayoutPoint(point.X, point.Y), cv.ToPoint);
    }

    [Fact]
    public void 剪貼簿是空的時不貼上()
    {
        _vm.Paste(1, 1);

        Assert.Empty(_vm.Elements);
        Assert.Contains("剪貼簿是空的", _vm.Status);
    }

    [Fact]
    public void 復原_新增移動屬性修改各一步_同一欄位連續輸入合併_存檔後清空()
    {
        Assert.False(_vm.UndoCommand.CanExecute(null));
        _vm.AddElementCommand.Execute(LayoutElementType.Station);
        var st = _vm.SelectedElement!;
        var (x0, y0) = (st.X, st.Y);

        _vm.BeginDrag(st);
        _vm.DragMoveBy(1, 0);
        _vm.DragMoveBy(2, 0);
        _vm.DragMoveBy(3, 0);
        _vm.EndDrag();
        foreach (var name in new[] { "入", "入庫", "入庫站" }) st.Name = name; // 每打一個字更新一次
        st.Rotation = 90;
        Assert.Equal(4, _vm.UndoCount);

        _vm.UndoCommand.Execute(null);
        st = _vm.Elements.Single();
        Assert.Equal((0, "入庫站"), (st.Rotation, st.Name));
        _vm.UndoCommand.Execute(null);
        st = _vm.Elements.Single();
        Assert.Equal("新站點 1", st.Name); // 連續輸入一次復原
        _vm.UndoCommand.Execute(null);
        st = _vm.Elements.Single();
        Assert.Equal((x0, y0), (st.X, st.Y)); // 整個拖曳一次復原
        _vm.UndoCommand.Execute(null);
        Assert.Empty(_vm.Elements);
        Assert.False(_vm.UndoCommand.CanExecute(null));

        _vm.AddElementCommand.Execute(LayoutElementType.Station);
        _vm.PlantName = "範例廠";
        _vm.SaveCommand.Execute(null);
        Assert.False(_vm.UndoCommand.CanExecute(null)); // 存檔後不能再復原
    }

    [Fact]
    public void 復原最多50步()
    {
        Add(LayoutElementType.Station, 0, 0);
        for (var i = 1; i <= 60; i++)
        {
            _vm.SelectedElement = null; // 換選取：下一次修改另算一步
            _vm.SelectedElement = _vm.Elements.Single();
            _vm.Elements.Single().X = i;
        }

        Assert.Equal(LayoutEditorViewModel.MaxUndoSteps, _vm.UndoCount);
        for (var i = 0; i < 50; i++) _vm.UndoCommand.Execute(null);
        Assert.Equal(10.0, _vm.Elements.Single().X); // 最早的 10 步已經丟掉
    }
}
