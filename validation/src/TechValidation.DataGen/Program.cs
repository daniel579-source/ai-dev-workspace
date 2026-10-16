// 技術驗證（#15）T-02：虛構測試資料產生器。
// 依 docs/log-format-notes.md 的「共通格式」描述產生**虛構**資料；所有站點、設備、命令、IP、電話都是編造的，
// IP 使用文件專用網段 192.0.2.0/24，電話使用虛構號碼。不含任何真實 LOG 內容。
using System.Diagnostics;
using System.Globalization;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var opts = Args.Parse(args);
if (opts is null)
{
    Console.WriteLine("用法：TechValidation.DataGen --out <資料夾> [--days 7] [--mb-per-day 500] [--start 2030-01-07] [--seed 42]");
    return 1;
}
Directory.CreateDirectory(opts.Out);
var sw = Stopwatch.StartNew();
var jobs = new List<(int dayIdx, LoggerSpec spec)>();
for (int d = 0; d < opts.Days; d++)
    foreach (var s in LoggerSpec.All) jobs.Add((d, s));

long totalBytes = 0;
Parallel.ForEach(jobs.OrderByDescending(j => j.spec.Share), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, job =>
{
    var day = opts.Start.AddDays(job.dayIdx);
    var gen = new DayGenerator(day, job.dayIdx, opts.Seed, opts.BytesPerDay);
    var bytes = gen.Generate(job.spec, opts.Out);
    Interlocked.Add(ref totalBytes, bytes);
});
Console.WriteLine($"完成：{opts.Days} 天，{totalBytes / 1048576.0:F0} MB，{sw.Elapsed.TotalSeconds:F1} 秒 → {opts.Out}");
return 0;

sealed record Args(string Out, int Days, long BytesPerDay, DateTime Start, int Seed)
{
    public static Args? Parse(string[] a)
    {
        string? outDir = null; int days = 7; long mb = 500; var start = new DateTime(2030, 1, 7); int seed = 42;
        for (int i = 0; i + 1 < a.Length; i += 2)
        {
            switch (a[i])
            {
                case "--out": outDir = a[i + 1]; break;
                case "--days": days = int.Parse(a[i + 1]); break;
                case "--mb-per-day": mb = long.Parse(a[i + 1]); break;
                case "--start": start = DateTime.ParseExact(a[i + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(a[i + 1]); break;
                default: return null;
            }
        }
        return outDir is null ? null : new Args(outDir, days, mb * 1024 * 1024, start, seed);
    }
}

/// <summary>13 種 LOG（虛構內容），Share 依一天份樣本的大小比例。</summary>
sealed record LoggerSpec(string Module, string Logger, bool Big5, double Share, double SameTsRate, int BackwardPerDay, bool Continuous)
{
    public static readonly LoggerSpec[] All =
    [
        new("EXE", "WebServiceLOG", false, 1.6, 0.10, 50, false),
        new("EXE", "EEWLOG", false, 0.3, 0.02, 0, true),
        new("EXE", "EXEDebug", false, 0.1, 0.42, 0, false),
        new("EXE", "DBUPDATE_LOG", false, 0.8, 0.15, 0, false),
        new("EXE", "DBLOG", false, 0.06, 0.05, 0, false),
        new("PLC", "PLCLOG", true, 0.8, 0.30, 150, false),
        new("PLC", "PLCExecTimeLOG", true, 0.02, 0.05, 0, false),
        new("PLC", "BCR_LOG", true, 0.4, 0.20, 0, false),
        new("PLC", "DBUPDATE_LOG", true, 0.7, 0.15, 0, false),
        new("PLC", "WMSDebug", true, 0.0, 0, 0, false),
        new("Server", "WCSCMD", false, 0.03, 0.10, 0, false),
        new("Server", "WCSDebug", true, 0.2, 0.15, 0, false),
        new("Server", "DBUPDATE_LOG", true, 14.8, 0.10, 0, true),
    ];
    public static double TotalShare => All.Sum(s => s.Share);
}

sealed class DayGenerator
{
    const long RotateBytes = 10L * 1024 * 1024;
    static readonly string[] StatusNames = ["在席", "取物可", "下定位", "上定位", "置物可", "入庫要求", "WT回報要求", "順轉定位", "逆轉定位", "夾臂退回", "夾臂伸出"];
    static readonly string[] Cranes = Enumerable.Range(1, 8).Select(i => $"CRNA{i:00}").ToArray();
    static readonly string[] Shuttles = Enumerable.Range(1, 12).Select(i => $"SHTL{i:00}").ToArray();
    static readonly string[] Conveyors = Enumerable.Range(1, 20).Select(i => $"CONV{i:00}").ToArray();
    static readonly string[] Stations = Enumerable.Range(1, 48).Select(i => $"A-{i:00}").ToArray();

    readonly DateTime _day;
    readonly int _dayIdx;
    readonly int _seed;
    readonly long _bytesPerDay;
    readonly (long Start, long End, int CmdNo)[] _commands;

    public DayGenerator(DateTime day, int dayIdx, int seed, long bytesPerDay)
    {
        _day = day; _dayIdx = dayIdx; _seed = seed; _bytesPerDay = bytesPerDay;
        var rnd = new Random(HashCode.Combine(seed, dayIdx, "cmd"));
        int n = (int)Math.Max(50, 1000 * bytesPerDay / (500L * 1024 * 1024));
        _commands = Enumerable.Range(0, n).Select(i =>
        {
            long start = (long)(rnd.NextDouble() * TimeSpan.TicksPerDay);
            long dur = TimeSpan.TicksPerMinute * rnd.Next(2, 11);
            return (start, start + dur, 10000 + dayIdx * 2000 + i);
        }).OrderBy(c => c.start).ToArray();
    }

    // 作業空檔（O-008）：多數 LOG 在 02:10～06:53 沒有紀錄
    static readonly long GapStart = new TimeSpan(2, 10, 0).Ticks, GapEnd = new TimeSpan(6, 53, 0).Ticks;

    public long Generate(LoggerSpec spec, string outRoot)
    {
        var rnd = new Random(HashCode.Combine(_seed, _dayIdx, spec.Module, spec.Logger));
        var dir = Path.Combine(outRoot, _day.ToString("yyyyMMdd"), $"{spec.Module}-{_day:yyyy-MM-dd}");
        Directory.CreateDirectory(dir);
        var enc = spec.Big5 ? Encoding.GetEncoding(950) : new UTF8Encoding(false);
        var state = new GenState(rnd);

        // 估算事件數
        long target = (long)(_bytesPerDay * spec.Share / LoggerSpec.TotalShare);
        int count;
        if (spec.Share == 0) count = 1;
        else
        {
            long sample = 0;
            for (int i = 0; i < 300; i++) sample += enc.GetByteCount(Render(spec, 0, Message(spec, _day.Ticks + TimeSpan.TicksPerHour * 12, state, rnd)));
            count = (int)Math.Max(1, target / Math.Max(1, sample / 300));
        }
        var times = Times(spec, count, rnd);

        // 注入少量時間無效的紀錄（ADR-0003、ADR-0004）
        var invalidAt = spec.Logger == "EXEDebug" ? Enumerable.Range(0, 5).Select(_ => rnd.Next(count)).ToHashSet() : [];
        // Big5 檔中注入少量無法解碼的位元組
        var badBytesAt = spec.Big5 && count > 100 ? Enumerable.Range(0, 3).Select(_ => rnd.Next(count)).ToHashSet() : [];

        var path = Path.Combine(dir, spec.Logger + ".txt");
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        long written = 0, total = 0;
        for (int i = 0; i < count; i++)
        {
            long t = times[i];
            if (fs.Length >= RotateBytes)
            {
                // 輪替：舊檔移到 archives，檔名時間＝新檔第一筆紀錄的時間（log-format-notes 2.1）
                fs.Dispose();
                var arch = Path.Combine(dir, "archives");
                Directory.CreateDirectory(arch);
                File.Move(path, Path.Combine(arch, $"{spec.Logger}_{new DateTime(t):yyyy-MM-dd HH_mm_ss.ffff}.txt"));
                fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            }
            var msg = Message(spec, t, state, rnd);
            var text = Render(spec, t, msg, invalid: invalidAt.Contains(i));
            var bytes = enc.GetBytes(text);
            if (badBytesAt.Contains(i))
            {
                // 在訊息尾端插入 0xFF 0xFE（Big5 無效位元組）
                int insert = Array.IndexOf(bytes, (byte)'\r');
                if (insert > 0) bytes = [.. bytes[..insert], 0xFF, 0xFE, .. bytes[insert..]];
            }
            fs.Write(bytes);
            written += bytes.Length;
        }
        total = written;
        fs.Dispose();
        return total;
    }

    long[] Times(LoggerSpec spec, int count, Random rnd)
    {
        long dayStart = _day.Ticks;
        long active = spec.Continuous ? TimeSpan.TicksPerDay : TimeSpan.TicksPerDay - (GapEnd - GapStart);
        var raw = new long[count];
        for (int i = 0; i < count; i++)
        {
            long off = (long)(rnd.NextDouble() * active);
            if (!spec.Continuous && off >= GapStart) off += GapEnd - GapStart;
            raw[i] = dayStart + off / 1000 * 1000; // 精度 100 µs
        }
        Array.Sort(raw);
        for (int i = 1; i < count; i++)
            if (rnd.NextDouble() < spec.SameTsRate) raw[i] = raw[i - 1];
        // 同一檔案內的時間倒退（最多約 34 ms）
        for (int k = 0; k < spec.BackwardPerDay && count > 2; k++)
        {
            int i = rnd.Next(1, count);
            raw[i] = Math.Max(dayStart, raw[i - 1] - rnd.Next(1, 341) * 1000L);
        }
        return raw;
    }

    static string Render(LoggerSpec spec, long ticks, Msg msg, bool invalid = false)
    {
        var sb = new StringBuilder(256 + msg.Text.Length);
        if (invalid) sb.Append(new DateTime(ticks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" 25:61:99.0000");
        else sb.Append(new DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss.ffff", CultureInfo.InvariantCulture));
        sb.Append(" | ").Append(msg.Level).Append(" | ").Append(spec.Logger).Append(" | ");
        var lines = msg.Text.Split('\n');
        sb.Append(lines[0]).Append("  \r\n");
        for (int i = 1; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            sb.Append(msg.BareLf && i == 1 ? "\n" : "\r\n");
        }
        sb.Append("\r\n");
        return sb.ToString();
    }

    readonly record struct Msg(string Text, string Level = "DEBUG", bool BareLf = false);

    sealed class GenState(Random rnd)
    {
        public readonly Dictionary<(int, int), bool> Bits = new();
        public readonly Random Rnd = rnd;
    }

    int Cmd(long ticks, Random rnd)
    {
        long t = ticks - _day.Ticks;
        int lo = 0, hi = _commands.Length - 1, idx = 0;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (_commands[mid].Start <= t) { idx = mid; lo = mid + 1; } else hi = mid - 1; }
        for (int k = 0; k < 20 && idx - k >= 0; k++)
        {
            var c = _commands[idx - k];
            if (c.End >= t && rnd.Next(3) > 0) return c.CmdNo;
        }
        return _commands[idx].CmdNo;
    }

    static string CmdStr(int n) => $"000{n:00000}";
    static string Pallet(int cmd) => (100000000 + (long)cmd * 7919 % 899999999).ToString();
    static T Pick<T>(Random r, T[] a) => a[r.Next(a.Length)];
    static string Ip(Random r, int baseHost) => $"192.0.2.{baseHost + r.Next(5)}";

    Msg Message(LoggerSpec spec, long ticks, GenState st, Random r)
    {
        var cmd = CmdStr(Cmd(ticks, r));
        var stn = Pick(r, Stations);
        var crane = Pick(r, Cranes);
        var ts = new DateTime(ticks).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        switch (spec.Module, spec.Logger)
        {
            case ("EXE", "WebServiceLOG"):
            {
                int k = r.Next(100);
                if (k < 45)
                {
                    var body = $"{{\"cmdNo\":\"{cmd}\",\"fromStation\":\"{stn}\",\"toStation\":\"{Pick(r, Stations)}\",\"palletCmd\":\"{Pallet(int.Parse(cmd))}\",\"priority\":{r.Next(1, 6)},\"requestTime\":\"{ts}\"}}";
                    return new($"收到任務 POST /api/demo/task\nPOST /api/demo/task HTTP/1.1\nHost: {Ip(r, 10)}:8080\nContent-Type: application/json; charset=utf-8\nContent-Length: {body.Length}\nX-Demo-Request-Id: {r.Next():x8}\n\n{body}", "INFO");
                }
                if (k < 90) return new($"回應 {(r.Next(50) == 0 ? "500 Internal Server Error" : "200 OK")} cmdNo={cmd} elapsed={r.Next(3, 900)}ms", "INFO");
                // 簡訊通知：含告警文字與（虛構）電話號碼
                var alarm = r.Next(40) switch
                {
                    0 => "急停按鈕被按下",
                    1 => "錯誤碼 ZQX7781",
                    2 => "ｱﾗｰﾑ 搬送逾時",
                    _ => Pick(r, new[] { "設備門開啟異常", "搬送逾時", "條碼讀取失敗", "貨物尺寸超限" }),
                };
                var sms = $"{{\"to\":\"0900-000-{r.Next(1000):000}\",\"message\":\"[告警] {stn} {crane} {alarm}\"}}";
                return new($"簡訊通知 POST /api/demo/sms\nPOST /api/demo/sms HTTP/1.1\nHost: {Ip(r, 30)}\nContent-Type: application/json\n\n{sms}", "INFO", BareLf: r.Next(10) == 0);
            }
            case ("EXE", "EEWLOG"):
                return new($"Ping 收到 GET /health\nGET /health HTTP/1.1\nHost: {Ip(r, 50)}:9000\nUser-Agent: DemoMonitor/1.0", "INFO");
            case ("EXE", "EXEDebug"):
            {
                int k = r.Next(100);
                if (k < 3) return new($"ＥＲＲ－０４２ 寫入庫存失敗，重試中,StationNo={stn},CMD_NO={cmd}");
                if (k < 6) return new($"ERR-042 寫入庫存失敗，重試中,StationNo={stn},CMD_NO={cmd}");
                if (k < 55) return new($"SubmitCompleted,StationNo={stn},RMC_ID={crane},CMD_NO={cmd}");
                return new($"完成回報上位系統 {{\"cmdNo\":\"{cmd}\",\"station\":\"{stn}\",\"device\":\"{crane}\",\"result\":\"OK\"}}");
            }
            case ("EXE", "DBUPDATE_LOG"):
                if (r.Next(10) == 0)
                    return new($"UPDATE DEMO_CMD_HISTORY\nSET STATUS = '{r.Next(1, 9)}', UPD_TIME = '{ts}'\nWHERE CMD_NO = '{cmd}'");
                return new($"INSERT INTO DEMO_CMD_HISTORY (CMD_NO, STN_NO, EQP_ID, STATUS, MEMO, UPD_TIME) VALUES ('{cmd}', '{stn}', '{crane}', '{r.Next(1, 9)}', '{new string('x', r.Next(100, 600))}', '{ts}')");
            case ("EXE", "DBLOG"):
            {
                var sb = new StringBuilder("資料庫存取發生例外\nSystem.InvalidCastException: 無法將型別 'System.DBNull' 的物件轉換為型別 'System.String'。");
                for (int i = 0; i < 11; i++) sb.Append($"\n   at Demo.Data.Repository{i}.Load{r.Next(10)}(String cmdNo) in C:\\demo\\src\\Repo{i}.cs:line {r.Next(10, 900)}");
                return new(sb.ToString(), "INFO");
            }
            case ("PLC", "PLCLOG"):
            {
                int k = r.Next(100);
                if (k < 45)
                {
                    int s = r.Next(Stations.Length), b = r.Next(StatusNames.Length);
                    st.Bits.TryGetValue((s, b), out var on);
                    on = !on;
                    st.Bits[(s, b)] = on;
                    return new($"[{Stations[s]}][Station_Status_Change] => {StatusNames[b]} = {(on ? "ON" : "OFF")}");
                }
                if (k < 60) return new($"[{stn}-{crane}][Station_Status_Change] => 命令編號 = {cmd}");
                if (k < 80) return new($"[{stn}][Barcode_Inbound_Request] => 條碼=PLT{r.Next(10000000):0000000} 棧板命令={Pallet(int.Parse(cmd))}");
                if (k < 90) return new($"[{stn}][Outbound_Cycle] => 出庫循環 CMD_NO={cmd}");
                if (k < 95) return new($"[{stn}][RFID_Inbound_Request] => RFID=TAG{r.Next(1000000):000000}");
                return new($"[{stn}][Unload] => 卸貨完成 CMD_NO={cmd}");
            }
            case ("PLC", "PLCExecTimeLOG"):
                return new($"{r.Next(1, 20)} | {stn} | {cmd} | {r.Next(1, 4)} | {crane} | {Pick(r, new[] { "到達", "開始", "完成" })}");
            case ("PLC", "BCR_LOG"):
            {
                int k = r.Next(200);
                if (k == 0) return new($"條碼讀取器斷線 {Ip(r, 40)}:6000");
                if (k < 30) return new($"TCP 已連線 {Ip(r, 40)}:6000");
                if (k < 90) return new(r.Next(2) == 0 ? "開始讀取" : "停止讀取");
                return new($"收到資料 PLT{r.Next(10000000):0000000} 站點={stn}");
            }
            case ("PLC", "DBUPDATE_LOG"):
                return new($"[Barcode_Inbound_Request] UPDATE DEMO_STATION SET STATUS = '{r.Next(0, 5)}', CMD_NO = '{cmd}', MEMO = '{new string('y', r.Next(100, 500))}' WHERE STN_NO = '{stn}'");
            case ("PLC", "WMSDebug"):
                return new("刪除 90 天前的 LOG 資料夾", "INFO");
            case ("Server", "WCSCMD"):
                return new(r.Next(2) == 0 ? $"命令加入 {crane} 命令表 CMD_NO={cmd}" : $"命令鎖定 {crane} CMD_NO={cmd}");
            case ("Server", "WCSDebug"):
            {
                int k = r.Next(100);
                if (k < 2) return new($"ＥＲＲ０４２ 路徑規劃失敗 CMD_NO={cmd}");
                if (k < 60) return new($"路徑規劃 CMD_NO={cmd} 下一點={r.Next(1000, 9999)} 下一設備={Pick(r, Shuttles)} 下一站={r.Next(100000, 999999)}");
                return new($"挑選設備命令 CMD_NO={cmd} 類型={r.Next(1, 4)} 目的地={stn}");
            }
            case ("Server", "DBUPDATE_LOG"):
            {
                var eqp = Pick(r, r.Next(2) == 0 ? Shuttles : Conveyors);
                var sb = new StringBuilder($"同步設備訊息 DELETE FROM DEMO_EQP_MSG WHERE EQP_ID = '{eqp}'");
                int rows = r.Next(3, 9);
                for (int i = 0; i < rows; i++)
                    sb.Append($"\nINSERT INTO DEMO_EQP_MSG (EQP_ID, SEQ, MSG_TEXT, UPD_TIME) VALUES ('{eqp}', {i}, '設備 {eqp} 狀態 {Pick(r, new[] { "運轉中", "待機", "充電中", "保養中" })} 位置 {r.Next(1, 300)} {new string('z', r.Next(80, 200))}', '{ts}')");
                return new(sb.ToString());
            }
        }
        return new("未定義");
    }
}
