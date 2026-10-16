using System.Text;
using TechValidation.Core;

namespace TechValidation.Tests;

// 所有測試資料都是虛構的。
public class NormalizerTests
{
    public static TheoryData<string, string, string> Cases()
    {
        var d = new TheoryData<string, string, string>();
        foreach (var c in NormalizationCases.All) d.Add(c.Name, c.Input, c.Expected);
        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Nfkc_and_case_fold(string name, string input, string expected)
    {
        Assert.True(TextNormalizer.Normalize(input) == expected, name);
    }

    [Fact]
    public void Self_check_passes() => Assert.True(TextNormalizer.SelfCheck(out _));
}

public class ParserTests
{
    static LogParser.FileResult Parse(byte[] bytes) =>
        LogParser.ParseFile(new LogFile { FileId = 1, Module = "PLC", Logger = "PLCLOG", RelativePath = "x", FullPath = "x" }, bytes);

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Continuation_lines_and_blank_separators()
    {
        var text =
            "2030-01-07 08:00:00.0001 | INFO | WebServiceLOG | 收到任務 POST /api/demo/task  \r\n" +
            "POST /api/demo/task HTTP/1.1\r\n" +
            "Host: 192.0.2.10\r\n" +
            "\r\n" +
            "{\"cmdNo\":\"00010001\"}\r\n" +
            "\r\n" +
            "2030-01-07 08:00:00.0002 | DEBUG | WebServiceLOG | 回應 200 OK cmdNo=00010001  \r\n" +
            "\r\n";
        var r = Parse(Utf8(text));
        Assert.Equal(2, r.Events.Count);
        Assert.Equal(1, r.Events[0].LineStart);
        Assert.Equal(5, r.Events[0].LineEnd);         // 內部空行保留、尾端空行排除
        Assert.Contains("host: 192.0.2.10", r.Events[0].NormText);
        Assert.Equal([10001L], r.Events[0].Commands!);
        Assert.Equal(7, r.Events[1].LineStart);
        Assert.Equal("UTF-8", r.Encoding);
    }

    [Fact]
    public void Byte_offset_and_length_read_back_the_record()
    {
        var text = "2030-01-07 08:00:00.0001 | DEBUG | PLCLOG | 第一筆  \r\n\r\n2030-01-07 08:00:00.0002 | DEBUG | PLCLOG | 第二筆  \r\n\r\n";
        var bytes = Utf8(text);
        var ev = Parse(bytes).Events[1];
        var back = Encoding.UTF8.GetString(bytes, (int)ev.ByteOffset, ev.ByteLength);
        Assert.Equal("2030-01-07 08:00:00.0002 | DEBUG | PLCLOG | 第二筆", back);
    }

    [Fact]
    public void Time_backward_and_invalid_time_are_flagged()
    {
        var text =
            "2030-01-07 08:00:00.0100 | DEBUG | PLCLOG | a  \r\n\r\n" +
            "2030-01-07 08:00:00.0050 | DEBUG | PLCLOG | b  \r\n\r\n" +
            "2030-01-07 25:61:99.0000 | DEBUG | PLCLOG | c  \r\n\r\n";
        var r = Parse(Utf8(text));
        Assert.Equal(3, r.Events.Count);
        Assert.True(r.Events[1].Flags.HasFlag(EventFlags.TimeBackward));
        Assert.True(r.Events[2].Flags.HasFlag(EventFlags.TimeInvalid));
        Assert.Equal(r.Events[1].Ticks, r.Events[2].Ticks); // 沿用前一筆有效時間
    }

    [Fact]
    public void Big5_is_detected_and_decoded_and_bad_bytes_flagged()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var big5 = Encoding.GetEncoding(950);
        var good = big5.GetBytes("2030-01-07 08:00:00.0001 | DEBUG | PLCLOG | [A-07][Station_Status_Change] => 在席 = ON  \r\n\r\n");
        var bad = big5.GetBytes("2030-01-07 08:00:00.0002 | DEBUG | PLCLOG | 路徑規劃").Concat(new byte[] { 0xFF, 0xFE }).Concat(Encoding.ASCII.GetBytes("  \r\n\r\n")).ToArray();
        var r = Parse([.. good, .. bad]);
        Assert.Equal("Big5", r.Encoding);
        Assert.Equal(2, r.Events.Count);
        Assert.Equal(new StationChange("a-07", "在席", true), r.Events[0].Station);
        Assert.False(r.Events[0].Flags.HasFlag(EventFlags.EncodingError));
        Assert.True(r.Events[1].Flags.HasFlag(EventFlags.EncodingError));
    }

    [Fact]
    public void File_without_records_is_unparsable_and_keeps_nothing_in_timeline()
    {
        var r = Parse(Utf8("這不是 LOG 格式\r\n第二行\r\n"));
        Assert.True(r.Unparsable);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void Search_query_index_eligibility()
    {
        Assert.True(new SearchQuery { All = ["ZQX7781"] }.Normalized().CanUseIndex);
        Assert.False(new SearchQuery { All = ["在席"] }.Normalized().CanUseIndex);
        Assert.True(new SearchQuery { All = ["station", "在席"] }.Normalized().CanUseIndex);
        Assert.False(new SearchQuery { Any = ["station", "在席"] }.Normalized().CanUseIndex);
        Assert.False(new SearchQuery { Not = ["station"] }.Normalized().CanUseIndex);
    }

    [Fact]
    public void Fts_expression_for_column_detail_uses_trigrams()
    {
        var q = new SearchQuery { All = ["abcd"] };
        Assert.Equal("\"abcd\"", SqliteSearch.FtsExpression(q, "full"));
        Assert.Equal("(\"abc\" AND \"bcd\")", SqliteSearch.FtsExpression(q, "column"));
    }
}
