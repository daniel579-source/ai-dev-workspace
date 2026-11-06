using LogTrace.Engine.Plants;

namespace LogTrace.Engine.Tests;

// 導軌（ADR-0006 第 14 點，#100）：幾何計算、存檔與檢查。內容全部是虛構的。
public sealed class RailTests
{
    const double Tol = 1e-6;

    [Fact]
    public void 直線_全長_依長度取位置_投影()
    {
        var path = new RailPath(RailPath.StraightTemplate(1, 2), closed: false);

        Assert.Equal(20, path.Length, Tol);
        var p = path.PointAt(5);
        Assert.Equal((6d, 2d), (Math.Round(p.X, 6), Math.Round(p.Y, 6)));
        Assert.Equal(20, path.PointAt(99).Distance, Tol); // 不閉合：超出取端點
        var q = path.Project(8, 5);
        Assert.Equal((8d, 2d, 7d, 3d), (Math.Round(q.X, 6), Math.Round(q.Y, 6), Math.Round(q.Distance, 6), Math.Round(q.Offset, 6)));
    }

    [Fact]
    public void 半圓_凸度1往行進方向的左邊彎_全長為半圓周()
    {
        // 從 (0,0) 往右到 (2,0)：左邊（畫面座標）是上方 y < 0
        var path = new RailPath([new(0, 0, 1), new(2, 0)], closed: false);

        Assert.Equal(Math.PI, path.Length, Tol);
        var mid = path.PointAt(Math.PI / 2);
        Assert.Equal(1, mid.X, Tol);
        Assert.Equal(-1, mid.Y, Tol);
        var (_, top, _, bottom) = path.Bounds();
        Assert.Equal(-1, top, 3);
        Assert.Equal(0, bottom, 3);

        var right = new RailPath([new(0, 0, -1), new(2, 0)], closed: false); // 凸度 -1：往右邊（y > 0）
        Assert.Equal(1, right.PointAt(Math.PI / 2).Y, Tol);
    }

    [Fact]
    public void 弧線的投影_弧上的點與弧外的點取較近的端點()
    {
        var path = new RailPath([new(0, 0, 1), new(2, 0)], closed: false); // 圓心 (1,0)、半徑 1，經過 (1,-1)

        var onArc = path.Project(1, -3);
        Assert.Equal((1d, -1d), (Math.Round(onArc.X, 6), Math.Round(onArc.Y, 6)));
        Assert.Equal(Math.PI / 2, onArc.Distance, Tol);
        Assert.Equal(2, onArc.Offset, Tol);

        var below = path.Project(1.9, 0.5); // 在弧的另一側：取較近的端點 (2,0)
        Assert.Equal(2, below.X, Tol);
        Assert.Equal(Math.PI, below.Distance, Tol);
    }

    [Fact]
    public void 跑道形環_閉合_全長為兩條直線加一個圓周_繞回起點()
    {
        var path = new RailPath(RailPath.LoopTemplate(3, 2), closed: true);

        Assert.Equal(4, path.SegmentCount);
        Assert.Equal(40 + Math.PI * 6, path.Length, Tol);
        var (l, t, r, b) = path.Bounds();
        Assert.Equal((0d, 2d, 26d, 8d), (Math.Round(l, 3), Math.Round(t, 3), Math.Round(r, 3), Math.Round(b, 3))); // 半圓往外鼓 3 公尺
        var wrap = path.PointAt(path.Length + 5); // 閉合：繞回
        Assert.Equal(8, wrap.X, Tol);
        Assert.Equal(2, wrap.Y, Tol);
        var right = path.PointAt(20 + Math.PI * 3 / 2); // 右邊半圓的中點
        Assert.Equal(26, right.X, Tol);
        Assert.Equal(5, right.Y, Tol);
    }

    [Fact]
    public void 插入點_形狀不變_弧線拆成同一個圓上的兩段()
    {
        var half = new RailPath([new(0, 0, 1), new(2, 0)], closed: false);

        var (points, index) = half.InsertAt(half.SegmentMiddle(0));
        Assert.Equal(1, index);
        Assert.Equal(3, points.Count);
        Assert.Equal((1d, -1d), (points[1].X, points[1].Y)); // 半圓的頂點
        var tan = Math.Tan(Math.PI / 8); // 各 90 度：凸度 tan(90° / 4)
        Assert.Equal(tan, points[0].Bulge, 4);
        Assert.Equal(tan, points[1].Bulge, 4);
        Assert.Equal(half.Length, new RailPath(points, false).Length, 3);
        Assert.Equal(-1, half.InsertAt(0).Index); // 落在既有的點上：不插入

        var line = new RailPath(RailPath.StraightTemplate(0, 0), closed: false);
        var (lp, li) = line.InsertAt(5);
        Assert.Equal((1, 5d, 0d), (li, lp[1].X, lp[1].Y));
        Assert.All(lp, p => Assert.Null(p.B));
    }

    [Fact]
    public void 路徑語法_直線L_弧線A_閉合Z_加上平移()
    {
        var loop = new RailPath(RailPath.LoopTemplate(0, 0), closed: true);

        Assert.Equal("M 10,5 L 30,5 A 3,3 0 0 1 30,11 L 10,11 A 3,3 0 0 1 10,5 Z", loop.ToPathData(10, 5));
    }

    [Fact]
    public void 弧高換成凸度_最多半圓_點在弦的哪一邊()
    {
        Assert.Equal(1, RailPath.BulgeFromHeight(6, 3), Tol);
        Assert.Equal(-1, RailPath.BulgeFromHeight(6, -9), Tol); // 限制在半圓
        Assert.Equal(0.5, RailPath.BulgeFromHeight(4, 1), Tol);
        Assert.Equal(1, RailPath.SignedHeight(new(0, 0), new(2, 0), 1, -1), Tol); // 上方＝行進方向的左邊
        Assert.Equal(-1, RailPath.SignedHeight(new(0, 0), new(2, 0), 1, 1), Tol);
    }

    [Fact]
    public void 旋轉90度_以範圍中心順時針_凸度不變_框選碰到弧線()
    {
        var loop = RailPath.LoopTemplate(0, 0);
        var rotated = RailPath.Rotate90(loop, closed: true);
        var before = new RailPath(loop, true).Bounds();
        var after = new RailPath(rotated, true).Bounds();

        Assert.Equal((before.Right - before.Left), after.Bottom - after.Top, 3); // 寬深對調
        Assert.Equal(loop.Select(p => p.B), rotated.Select(p => p.B));
        Assert.Equal(new RailPath(loop, true).Length, new RailPath(rotated, true).Length, 6);

        var path = new RailPath(loop, true);
        Assert.True(path.TouchesRect((22.5, 2.5, 23.5, 3.5))); // 右邊半圓最外側 x = 23
        Assert.False(path.TouchesRect((21.5, 2.5, 22.5, 3.5))); // 半圓內側，沒碰到弧線
        Assert.False(path.TouchesRect((5, 2, 6, 4))); // 環的中間
    }

    [Fact]
    public void 存檔讀回_格式版本4_凸度0不寫出_搬送機的導軌()
    {
        var doc = new PlantConfigDocument
        {
            Plant = "範例廠",
            Layout = new LayoutSection
            {
                Elements =
                [
                    new() { Id = "rl-1", Type = LayoutElementType.Rail, Name = "導軌 1", Points = RailPath.LoopTemplate(3, 2), Closed = true },
                    new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Name = "搬送機 1", X = 5, Y = 1, W = 2.5, H = 1.2, Rotation = 0, Rail = "rl-1" },
                ],
            },
        };

        var json = PlantConfigFile.Serialize(doc);
        Assert.Contains("\"formatVersion\": 4", json);
        Assert.Contains("\"type\": \"rail\"", json);
        Assert.DoesNotContain("bulge", json);
        Assert.Contains("\"b\": 1", json);
        Assert.DoesNotContain("\"b\": 0", json);
        var back = PlantConfigFile.Deserialize(json).Layout.Elements;
        Assert.Equal(RailPath.LoopTemplate(3, 2), back.Single(e => e.Id == "rl-1").Points);
        Assert.True(back.Single(e => e.Id == "rl-1").Closed);
        Assert.Equal("rl-1", back.Single(e => e.Id == "vh-1").Rail);
        // 畫布包含弧線鼓出去的部分：右邊半圓最外側 x = 30 + 20 + 3 = 53，再加 20 公尺
        var far = new LayoutElement { Id = "rl-2", Type = LayoutElementType.Rail, Points = RailPath.LoopTemplate(30, 2), Closed = true };
        Assert.Equal(73, LayoutGeometry.CanvasSize([far]).Width);
    }

    [Fact]
    public void 存檔前檢查_點太少_超過半圓_導軌不存在或不同層()
    {
        var doc = new PlantConfigDocument
        {
            Plant = "範例廠",
            Layout = new LayoutSection
            {
                Floors = ["1F", "2F"],
                Elements =
                [
                    new() { Id = "rl-1", Type = LayoutElementType.Rail, Floor = "1F", Points = [new(0, 0)] },
                    new() { Id = "rl-2", Type = LayoutElementType.Rail, Floor = "2F", Points = [new(0, 0, 1.5), new(5, 0)] },
                    new() { Id = "vh-1", Type = LayoutElementType.Vehicle, Floor = "1F", X = 1, Y = 1, W = 2, H = 1, Rotation = 0, Rail = "rl-2" },
                    new() { Id = "vh-2", Type = LayoutElementType.Vehicle, Floor = "1F", X = 5, Y = 1, W = 2, H = 1, Rotation = 0, Rail = "rl-9" },
                ],
            },
        };

        var issues = PlantConfigFile.Validate(doc);

        Assert.Contains(issues, i => i.Message.Contains("至少要有 2 個點") && i.ElementIds.SequenceEqual(["rl-1"]));
        Assert.Contains(issues, i => i.Message.Contains("超過半圓") && i.ElementIds.SequenceEqual(["rl-2"]));
        Assert.Contains(issues, i => i.Message.Contains("不在同一層") && i.ElementIds.SequenceEqual(["vh-1", "rl-2"]));
        Assert.Contains(issues, i => i.Message.Contains("「rl-9」不存在"));
    }
}
