using System.Buffers.Binary;
using System.Text;

namespace TechValidation.Core;

/// <summary>
/// 比較用的「自訂順序檔」（研究文件方案 E 的掃描部分，T-06）：
/// 依事件序號順序存放正規化文字。紀錄格式：[seq:int64][len:int32][UTF-8 bytes]。
/// 旁邊的 .idx 每 <see cref="Stride"/> 筆記一個位移，供平行分段掃描。
/// </summary>
public static class FlatNormFile
{
    public const int Stride = 4096;

    public static void Write(string path, IEnumerable<(long Seq, string Text)> records)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var offsets = new List<long>();
        Span<byte> head = stackalloc byte[12];
        byte[] buf = new byte[4096];
        long n = 0;
        foreach (var (seq, text) in records)
        {
            if (n % Stride == 0) offsets.Add(fs.Position);
            int max = Encoding.UTF8.GetMaxByteCount(text.Length);
            if (buf.Length < max) buf = new byte[max * 2];
            int len = Encoding.UTF8.GetBytes(text, buf);
            BinaryPrimitives.WriteInt64LittleEndian(head, seq);
            BinaryPrimitives.WriteInt32LittleEndian(head[8..], len);
            fs.Write(head);
            fs.Write(buf, 0, len);
            n++;
        }
        using var idx = new BinaryWriter(File.Create(path + ".idx"));
        idx.Write(fs.Position);
        idx.Write(offsets.Count);
        foreach (var o in offsets) idx.Write(o);
    }

    /// <summary>傳回分段的位元組範圍（以紀錄邊界切開）。</summary>
    public static List<(long Start, long End)> Partitions(string path, int parts)
    {
        using var r = new BinaryReader(File.OpenRead(path + ".idx"));
        long total = r.ReadInt64();
        int count = r.ReadInt32();
        var offs = new long[count];
        for (int i = 0; i < count; i++) offs[i] = r.ReadInt64();
        parts = Math.Max(1, Math.Min(parts, count));
        var result = new List<(long, long)>();
        for (int p = 0; p < parts; p++)
        {
            int a = (int)((long)count * p / parts);
            int b = (int)((long)count * (p + 1) / parts);
            long start = offs[a];
            long end = b < count ? offs[b] : total;
            if (end > start) result.Add((start, end));
        }
        return result;
    }

    /// <summary>依序讀取一個範圍內的紀錄，對每筆呼叫 visitor(seq, utf8)。</summary>
    public static void Scan(string path, long start, long end, Func<long, ReadOnlySpan<byte>, bool> visitor, CancellationToken ct = default)
    {
        const int Chunk = 8 << 20;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        fs.Position = start;
        var buf = new byte[Chunk];
        int filled = 0;
        long remaining = end - start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buf.Length - filled, remaining);
            int read = toRead > 0 ? fs.Read(buf, filled, toRead) : 0;
            remaining -= read;
            filled += read;
            int p = 0;
            while (filled - p >= 12)
            {
                long seq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(p));
                int len = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(p + 8));
                if (filled - p - 12 < len)
                {
                    if (len + 12 > buf.Length) { Array.Resize(ref buf, len + 12); }
                    break;
                }
                if (!visitor(seq, buf.AsSpan(p + 12, len))) return;
                p += 12 + len;
            }
            if (p < filled) Buffer.BlockCopy(buf, p, buf, 0, filled - p);
            filled -= p;
            if (read == 0 && remaining <= 0) break;
        }
    }
}
