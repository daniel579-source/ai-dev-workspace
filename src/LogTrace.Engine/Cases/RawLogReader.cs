using LogTrace.Engine.Parsing;

namespace LogTrace.Engine.Cases;

/// <summary>原始 LOG 的一行（行號從 1 起算；文字已去除行尾的換行字元）。</summary>
public readonly record struct RawLine(int LineNo, string Text);

/// <summary>
/// 依原始行序讀取案件內的原始檔複本（ADR-0008 第 6 點「原始 LOG」面板）。
/// 只保留最近讀取的一個檔案的行位置，連續選取同一檔案的事件時不必重讀。
/// </summary>
internal sealed class RawLogReader(string rawFolder)
{
    readonly Lock _lock = new();
    string? _path;
    byte[] _bytes = [];
    int[] _lineStarts = [];
    System.Text.Encoding _encoding = LogEncoding.Get(LogEncoding.Utf8Name);

    public int LineCount(string relativePath, string encoding)
    {
        lock (_lock)
        {
            Load(relativePath, encoding);
            return _lineStarts.Length;
        }
    }

    /// <summary>讀取第 firstLine 行起最多 count 行；超出檔案範圍的部分略過。</summary>
    public IReadOnlyList<RawLine> Read(string relativePath, string encoding, int firstLine, int count)
    {
        lock (_lock)
        {
            Load(relativePath, encoding);
            int from = Math.Max(1, firstLine), to = (int)Math.Min(_lineStarts.Length, (long)firstLine + count - 1);
            var list = new List<RawLine>(Math.Max(0, to - from + 1));
            for (int n = from; n <= to; n++)
            {
                int start = _lineStarts[n - 1];
                int end = n < _lineStarts.Length ? _lineStarts[n] : _bytes.Length;
                while (end > start && _bytes[end - 1] is (byte)'\n' or (byte)'\r') end--;
                list.Add(new RawLine(n, _encoding.GetString(_bytes, start, end - start)));
            }
            return list;
        }
    }

    void Load(string relativePath, string encoding)
    {
        if (_path == relativePath) return;
        var full = Path.GetFullPath(Path.Combine(rawFolder, relativePath));
        if (!full.StartsWith(Path.GetFullPath(rawFolder), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路徑不在案件的原始檔資料夾內", nameof(relativePath));
        var bytes = File.ReadAllBytes(full);
        var starts = new List<int> { 0 };
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] == '\n' && i + 1 < bytes.Length) starts.Add(i + 1);
        if (bytes.Length == 0) starts.Clear();
        (_path, _bytes, _lineStarts, _encoding) = (relativePath, bytes, starts.ToArray(), LogEncoding.Get(encoding));
    }
}
