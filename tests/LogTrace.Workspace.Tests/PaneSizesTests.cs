namespace LogTrace.Workspace.Tests;

// 拖動分隔線時，同一排的面板依比例一起縮放
public sealed class PaneSizesTests
{
    static readonly double[] NoMin = [0, 0, 0, 0];

    [Fact]
    public void 分隔線往右_左邊全部依比例放大_右邊全部依比例縮小()
    {
        // 左邊兩個面板共 300，右邊兩個共 300；分隔線往右 60
        var r = PaneSizes.Resize([100, 200, 200, 100], 2, 60, NoMin);

        Assert.Equal([120, 240, 160, 80], r);
        Assert.Equal(600, r.Sum());
    }

    [Fact]
    public void 分隔線往左_左邊縮小_右邊放大()
    {
        var r = PaneSizes.Resize([100, 100, 300], 1, -50, [0, 0, 0]);

        Assert.Equal([50, 112.5, 337.5], r);
    }

    [Fact]
    public void 縮放後不小於最小大小()
    {
        var r = PaneSizes.Resize([100, 400, 20, 80], 2, 50, [0, 0, 25, 25]);

        Assert.Equal(25, r[2]); // 20 × 0.5 = 10，取最小值 25
        Assert.Equal(40, r[3]);
    }

    [Fact]
    public void 移動距離超過一側的總大小時不改變()
    {
        Assert.Equal([100, 100], PaneSizes.Resize([100, 100], 1, 150, [0, 0]));
        Assert.Equal([100, 100], PaneSizes.Resize([100, 100], 1, -100, [0, 0]));
    }

    [Fact]
    public void 分隔線位置不合理時拋出例外()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PaneSizes.Resize([100, 100], 0, 10, [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PaneSizes.Resize([100, 100], 2, 10, [0, 0]));
    }
}
