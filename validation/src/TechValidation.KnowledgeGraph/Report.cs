using System.Text;

namespace TechValidation.KnowledgeGraph;

/// <summary>結果報告（Markdown）。比率以情境的正解為分母；沒有正解的項目不計入。</summary>
public static class Report
{
    sealed class Tally
    {
        public int Expected, Hit, Found;
        public void Add(IEnumerable<string> expected, IReadOnlySet<string>? found)
        {
            if (found is null) return;
            var e = expected.ToHashSet(StringComparer.Ordinal);
            Expected += e.Count;
            Hit += e.Count(found.Contains);
            Found += found.Count;
        }
        public string Recall => Expected == 0 ? "—" : $"{Hit}/{Expected}（{100.0 * Hit / Expected:0}%）";
        public string Precision => Found == 0 ? "—" : $"{Hit}/{Found}（{100.0 * Hit / Found:0}%）";
    }

    static string Cell(IEnumerable<string> expected, IReadOnlySet<string>? found)
    {
        if (found is null) return "—";
        var e = expected.ToHashSet(StringComparer.Ordinal);
        var extra = found.Count(f => !e.Contains(f));
        return e.Count == 0 && found.Count == 0 ? "✓（無）" : $"{e.Count(found.Contains)}/{e.Count}" + (extra > 0 ? $"，多 {extra}" : "");
    }

    static string Flag(bool expected, bool? found) => found is null ? "—" : found == expected ? "✓" : "✗";

    /// <summary>表格中顯示「代碼|說明」時改成「代碼：說明」，免得打斷 Markdown 表格。</summary>
    static string Show(string key) => key.Replace("|", "：");

    static bool ExactKb(Scenario s, Found f) => f.Kb.SetEquals(s.Expected.Kb);

    public static string Write(IReadOnlyList<(Scenario S, Found G, Found B, double Ms)> rows, int eventCount, int windowMinutes, int hops, int repeat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 知識檢索：規則關聯圖的虛構情境集驗證（#89，ADR-0015）");
        sb.AppendLine();
        sb.AppendLine($"- 日期：{DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine($"- 電腦：開發用電腦（{Environment.ProcessorCount} 執行緒，與 `summary-dev-pc.md` 相同）；.NET {Environment.Version}");
        sb.AppendLine($"- 資料：`validation/knowledge-scenarios`（**虛構**：一天 08:00～12:00、三個模組、{eventCount} 筆事件、12 個情境；由 `generate.py` 產生）");
        sb.AppendLine($"- 展開設定：佈局上下游 {hops} 層、時間窗前後 {windowMinutes} 分鐘（ADR-0015 第 3 點 2026-11-16 預設值）；關係在查詢時計算，不存邊表");
        sb.AppendLine("- 比較基準：只用問題文字做關鍵字比對（不看 LOG）。向量搜尋待使用者選定模型與套件後再比較（2026-11-16 使用者決定）");
        sb.AppendLine($"- 查詢時間：每個情境重複 {repeat} 次取中位數，不含匯入與建立世界模型");
        sb.AppendLine();

        sb.AppendLine("## 1. 各情境（規則關聯圖）");
        sb.AppendLine();
        sb.AppendLine("「a/b」為找到的正解數／正解數；「多 n」為正解以外多找到的項目。");
        sb.AppendLine();
        sb.AppendLine("| 情境 | 說明 | 知識庫 | 相關設備 | 警報 | 不該找到的警報 | 異常紀錄 | 安全清單 | 卡住 | 沒有 LOG | 展開大小 | 時間（ms） |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var (s, g, _, ms) in rows)
        {
            var bad = s.MustNot.Alarms.Count(a => g.Alarms!.Contains(a));
            sb.AppendLine($"| {s.Id} | {s.Title} | {Cell(s.Expected.Kb, g.Kb)} | {Cell(s.Expected.Devices, g.Devices)} | {Cell(s.Expected.Alarms, g.Alarms)} | " +
                $"{(s.MustNot.Alarms.Count == 0 ? "—" : bad == 0 ? "✓ 0" : $"✗ {bad}")} | {Cell(s.Expected.Incidents, g.Incidents)} | {Cell(s.Expected.Safety, g.Safety)} | " +
                $"{Flag(s.Expected.Stall, g.Stall)} | {Flag(s.Expected.NoLog, g.NoLog)} | {g.Events} | {ms:0.00} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 2. 彙總：規則關聯圖與關鍵字比對");
        sb.AppendLine();
        sb.AppendLine("| 項目 | 規則關聯圖：召回 | 規則關聯圖：精確度 | 關鍵字比對：召回 | 關鍵字比對：精確度 |");
        sb.AppendLine("|---|---|---|---|---|");
        void Line(string label, Func<Scenario, IEnumerable<string>> expected, Func<Found, IReadOnlySet<string>?> found)
        {
            Tally g = new(), b = new();
            foreach (var (s, gf, bf, _) in rows) { g.Add(expected(s), found(gf)); b.Add(expected(s), found(bf)); }
            sb.AppendLine($"| {label} | {g.Recall} | {g.Precision} | {(b.Found == 0 && b.Expected == 0 ? "不適用" : b.Recall)} | {b.Precision} |");
        }
        Line("知識庫條目", s => s.Expected.Kb, f => f.Kb);
        Line("相關設備", s => s.Expected.Devices, f => f.Devices);
        Line("相關警報", s => s.Expected.Alarms, f => f.Alarms);
        Line("異常紀錄（過去相似的異常）", s => s.Expected.Incidents, f => f.Incidents);
        Line("安全清單", s => s.Expected.Safety, f => f.Safety);
        var exactG = rows.Count(r => ExactKb(r.S, r.G));
        var exactB = rows.Count(r => ExactKb(r.S, r.B));
        sb.AppendLine();
        sb.AppendLine($"- **知識庫條目完全選對**（找到的正好等於正解，含「應該沒有」的情境）：規則關聯圖 {exactG}/{rows.Count}，關鍵字比對 {exactB}/{rows.Count}。");
        sb.AppendLine($"- **不該找到的警報**（時間窗外、不相連的設備）：規則關聯圖找到 {rows.Sum(r => r.S.MustNot.Alarms.Count(a => r.G.Alarms!.Contains(a)))} 個（共 {rows.Sum(r => r.S.MustNot.Alarms.Count)} 個）；關鍵字比對不看警報。");
        sb.AppendLine($"- **卡住、沒有 LOG 的判斷**：規則關聯圖 {rows.Count(r => r.G.Stall == r.S.Expected.Stall)}/{rows.Count}、{rows.Count(r => r.G.NoLog == r.S.Expected.NoLog)}/{rows.Count} 正確；關鍵字比對不判斷。");
        var sizes = rows.Select(r => r.G.Events!.Value).Order().ToList();
        var times = rows.Select(r => r.Ms).Order().ToList();
        sb.AppendLine($"- **展開大小**（時間窗內相關設備的事件＋知識庫、異常紀錄、安全清單）：最小 {sizes[0]}、中位數 {sizes[sizes.Count / 2]}、最大 {sizes[^1]}。");
        sb.AppendLine($"- **查詢時間**：最小 {times[0]:0.00} ms、中位數 {times[times.Count / 2]:0.00} ms、最大 {times[^1]:0.00} ms。虛構資料只有 {eventCount} 筆事件；真實樣本一天約 2.5 萬筆，展開時逐筆檢查效果，時間大致與事件數成正比，需要以實際量級另外量測。");
        sb.AppendLine();

        sb.AppendLine("## 3. 各情境的內容（虛構）");
        sb.AppendLine();
        sb.AppendLine("| 情境 | 時刻 | 問題設備 | 第一線的描述 | 正解：知識庫 | 正解：警報 | 規則關聯圖多找到的 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (s, g, _, _) in rows)
        {
            var extra = g.Kb.Where(k => !s.Expected.Kb.Contains(k)).Concat(g.Alarms!.Where(a => !s.Expected.Alarms.Contains(a)))
                .Concat(g.Incidents.Where(i => !s.Expected.Incidents.Contains(i))).ToList();
            sb.AppendLine($"| {s.Id} | {s.Time:HH:mm} | {s.Device} | {s.Question} | {(s.Expected.Kb.Count == 0 ? "（無）" : string.Join("；", s.Expected.Kb.Select(Show)))} | " +
                $"{(s.Expected.Alarms.Count == 0 ? "（無）" : string.Join("、", s.Expected.Alarms.Select(Show)))} | {(extra.Count == 0 ? "—" : string.Join("；", extra.Select(Show)))} |");
        }
        return sb.ToString();
    }
}
