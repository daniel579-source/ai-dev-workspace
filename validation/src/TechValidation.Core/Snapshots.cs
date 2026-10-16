using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace TechValidation.Core;

/// <summary>
/// 狀態快照（ADR-0005、T-10）的簡化版：只追蹤站點的 ON／OFF 位元狀態。
/// 快照 = 套用序號 ≤ snapshot.seq 的所有變化之後的狀態。
/// </summary>
public static class Snapshots
{
    public sealed class WorldState
    {
        public Dictionary<int, ulong> StationBits { get; } = new();

        public void Apply(int station, int status, bool on)
        {
            StationBits.TryGetValue(station, out var b);
            var mask = 1UL << (status % 64);
            StationBits[station] = on ? b | mask : b & ~mask;
        }

        public byte[] Serialize()
        {
            var buf = new byte[4 + StationBits.Count * 12];
            BinaryPrimitives.WriteInt32LittleEndian(buf, StationBits.Count);
            int p = 4;
            foreach (var (k, v) in StationBits.OrderBy(x => x.Key))
            {
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(p), k);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(p + 4), v);
                p += 12;
            }
            return buf;
        }

        public static WorldState Deserialize(ReadOnlySpan<byte> buf)
        {
            var s = new WorldState();
            int n = BinaryPrimitives.ReadInt32LittleEndian(buf);
            for (int i = 0, p = 4; i < n; i++, p += 12)
                s.StationBits[BinaryPrimitives.ReadInt32LittleEndian(buf[p..])] = BinaryPrimitives.ReadUInt64LittleEndian(buf[(p + 4)..]);
            return s;
        }

        public string Fingerprint() => string.Join(",", StationBits.OrderBy(x => x.Key).Where(x => x.Value != 0).Select(x => $"{x.Key}:{x.Value:x}"));
    }

    /// <summary>每 interval 筆事件存一份快照。</summary>
    public static (int Count, long Bytes) Build(CaseStore store, int interval)
    {
        using var c = store.OpenCase();
        CaseStore.Exec(c, "DELETE FROM snapshots;");
        var seqs = new List<long>();
        using (var cmd = c.CreateCommand())
        {
            // 依事件數取樣快照位置
            cmd.CommandText = "SELECT seq FROM events ORDER BY seq";
            using var r = cmd.ExecuteReader();
            long n = 0;
            while (r.Read())
            {
                if (++n % interval == 0) seqs.Add(r.GetInt64(0));
            }
        }

        var state = new WorldState();
        int count = 0; long bytes = 0;
        using var tx = c.BeginTransaction();
        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT INTO snapshots VALUES($s,$d)";
        var ps = ins.Parameters.Add("$s", SqliteType.Integer);
        var pd = ins.Parameters.Add("$d", SqliteType.Blob);
        using var sel = c.CreateCommand();
        sel.CommandText = "SELECT seq, station_id, status_id, value FROM station_changes ORDER BY seq";
        using var rd = sel.ExecuteReader();
        int si = 0;
        bool has = rd.Read();
        while (si < seqs.Count)
        {
            while (has && rd.GetInt64(0) <= seqs[si])
            {
                state.Apply(rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3) == 1);
                has = rd.Read();
            }
            var data = state.Serialize();
            ps.Value = seqs[si]; pd.Value = data;
            ins.ExecuteNonQuery();
            count++; bytes += data.Length;
            si++;
        }
        tx.Commit();
        return (count, bytes);
    }

    /// <summary>從最近的快照開始推導指定序號時的狀態。</summary>
    public static WorldState StateAt(SqliteConnection c, long seq)
    {
        WorldState state;
        long from = long.MinValue;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT seq, data FROM snapshots WHERE seq <= $s ORDER BY seq DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$s", seq);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                from = r.GetInt64(0);
                state = WorldState.Deserialize((byte[])r.GetValue(1));
            }
            else state = new WorldState();
        }
        Replay(c, state, from, seq);
        return state;
    }

    /// <summary>從頭逐筆推導（驗證快照正確性用）。</summary>
    public static WorldState StateAtFull(SqliteConnection c, long seq)
    {
        var s = new WorldState();
        Replay(c, s, long.MinValue, seq);
        return s;
    }

    static void Replay(SqliteConnection c, WorldState state, long afterSeq, long toSeq)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT station_id, status_id, value FROM station_changes WHERE seq > $a AND seq <= $b ORDER BY seq";
        cmd.Parameters.AddWithValue("$a", afterSeq);
        cmd.Parameters.AddWithValue("$b", toSeq);
        using var r = cmd.ExecuteReader();
        while (r.Read()) state.Apply(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2) == 1);
    }
}
