namespace LogTrace.Workspace;

/// <summary>
/// 拖動分隔線時，同一排的面板依比例一起縮放：分隔線前面（左或上）的面板全部依同一比例放大或縮小，
/// 後面（右或下）的面板也一樣，而不是只改變分隔線兩側的兩個面板。
/// </summary>
public static class PaneSizes
{
    /// <summary>
    /// 依比例重新分配大小。
    /// </summary>
    /// <param name="sizes">同一排面板目前的大小（像素），依排列順序。</param>
    /// <param name="split">分隔線前面的面板數；分隔線位於 sizes[split - 1] 與 sizes[split] 之間。</param>
    /// <param name="delta">分隔線移動的距離（往右或往下為正）。</param>
    /// <param name="minimums">每個面板的最小大小；縮放後不小於這個值。</param>
    /// <returns>新的大小；無法縮放（例如移動距離超過一側的總大小）時回傳原本的大小。</returns>
    public static double[] Resize(IReadOnlyList<double> sizes, int split, double delta, IReadOnlyList<double> minimums)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(split, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(split, sizes.Count - 1);
        if (minimums.Count != sizes.Count) throw new ArgumentException("每個面板都要有最小大小", nameof(minimums));

        double before = 0, after = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            if (i < split) before += sizes[i];
            else after += sizes[i];
        }
        if (before <= 0 || after <= 0 || before + delta <= 0 || after - delta <= 0) return [.. sizes];

        double scaleBefore = (before + delta) / before, scaleAfter = (after - delta) / after;
        var result = new double[sizes.Count];
        for (int i = 0; i < sizes.Count; i++)
            result[i] = Math.Max(minimums[i], sizes[i] * (i < split ? scaleBefore : scaleAfter));
        return result;
    }
}
