using System.Buffers.Binary;

namespace Osm2Carin.Core;

/// <summary>Symbolic pointer: section Sec, record Idx (Idx == count means the end of the section).</summary>
public readonly record struct Ref(int Sec, int Idx);

/// <summary>Text pointer, relative to the start of the name blob.</summary>
public readonly record struct TextRef(int Rel);

/// <summary>A record: its bytes (pointer fields zeroed) and its pointer fields by byte offset.
/// A pointer value is a Ref, a TextRef, an int literal (a value that hit no record) or null (0).</summary>
public sealed class Rec(byte[] raw)
{
    public byte[] Raw = raw;
    public readonly SortedDictionary<int, object?> Refs = new();
    public bool Own;                     // S10: +0 holds the tile's own BLOCK_ID

    public Rec(int size) : this(new byte[size]) { }
}

public class TileException(string m) : Exception(m);

/// <summary>Road tile model for DB-REL 26 (types 0x00-0x03): port of tools/osm2carin/tile26.py.
/// Parse(decoded).Build() is byte-identical on every road tile of the original disc.</summary>
public sealed class Tile26
{
    public static readonly int[] Order = [0, 1, 2, 3, 4, 5, 6, 7, 9, 8, 10, 11, 12, 13, 14];
    public const int Prolog = 116;
    public const int HdrStart = 0x54, HdrEnd = 0x74;
    static readonly Dictionary<int, int> RecSize = new()
    {
        [0] = 10, [1] = 10, [2] = 10, [3] = 4, [5] = 8, [6] = 16, [7] = 6, [8] = 4, [9] = 8, [10] = 8,
        [11] = 6, [12] = 6, [13] = 8, [14] = 4,
    };
    static readonly HashSet<int> Align4 = [6, 8, 9, 10, 13];

    public static int Rec4(int btype) => btype == 0 ? 30 : 24;
    public static int Extra(int s) => s is 3 or 4 ? 1 : 0;
    public int Size(int s) => s == 4 ? Rec4(BType) : RecSize[s];
    static int AlignUp(int pos, int s) { int a = Align4.Contains(s) ? 4 : 2; return (pos + a - 1) / a * a; }

    public enum Kind { Ptr, Text }

    public static List<(int off, Kind kind, int target)> PtrFields(int btype, int sec)
    {
        // target 56 = section 5 or 6 (a node)
        return sec switch
        {
            0 => [(0, Kind.Text, 0), (4, Kind.Ptr, 14)],
            1 => [(0, Kind.Text, 0), (2, Kind.Text, 0), (4, Kind.Ptr, 0), (6, Kind.Ptr, 14), (8, Kind.Ptr, 14)],
            2 => [(0, Kind.Text, 0), (2, Kind.Text, 0), (4, Kind.Ptr, 1), (6, Kind.Ptr, 14), (8, Kind.Ptr, 14)],
            3 => [(0, Kind.Ptr, 4), (2, Kind.Ptr, 5)],
            4 => btype == 0
                ? [(0, Kind.Ptr, 56), (2, Kind.Ptr, 56), (4, Kind.Ptr, 7), (6, Kind.Ptr, 4), (8, Kind.Ptr, 4), (0x12, Kind.Ptr, 10), (0x14, Kind.Ptr, 12), (0x18, Kind.Ptr, 2), (0x1C, Kind.Ptr, 11)]
                : [(0, Kind.Ptr, 56), (2, Kind.Ptr, 56), (4, Kind.Ptr, 7), (6, Kind.Ptr, 4), (8, Kind.Ptr, 4), (0x12, Kind.Ptr, 10), (0x14, Kind.Ptr, 12)],
            5 or 6 => [(4, Kind.Ptr, 4)],
            11 => [(0, Kind.Text, 0), (2, Kind.Text, 0)],
            14 => [(0, Kind.Text, 0)],
            _ => [],
        };
    }

    public int BType;
    public uint BlockId;
    public byte[] Head = new byte[8];
    public (int X1, int Y1, int X2, int Y2) BBox;
    public byte[] HdrWords = new byte[HdrEnd - HdrStart];
    public readonly Dictionary<int, List<Rec>> Secs = Order.ToDictionary(s => s, _ => new List<Rec>());
    public readonly HashSet<int> ZeroOffset = [];
    public byte[] Blob = [];
    public int NUnits = 1;
    public int Literals;

    public Tile26(int btype) { BType = btype; }

    static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d[o..]);

    public static Tile26 Parse(byte[] d, int unit = 2048)
    {
        int btype = U16(d, 4);
        if (btype > 3) throw new TileException($"not a road tile: type {btype:x}");
        var t = new Tile26(btype)
        {
            Head = d[..8],
            BlockId = BinaryPrimitives.ReadUInt32BigEndian(d),
            BBox = (BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0x44)), BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0x48)),
                    BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0x4C)), BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0x50))),
            HdrWords = d[HdrStart..HdrEnd],
            NUnits = d.Length / unit,
        };
        var desc = new (int off, int cnt)[15];
        for (int s = 0; s < 15; s++) desc[s] = (U16(d, 8 + 4 * s), U16(d, 10 + 4 * s));
        var start = new Dictionary<int, int>();
        var layout = new List<(int s, int off, int n)>();
        int pos = Prolog;
        foreach (int s in Order)
        {
            var (off, cnt) = desc[s];
            int n = cnt + Extra(s);
            if (off == 0 && cnt == 0)
            {
                t.ZeroOffset.Add(s);
                start[s] = pos;
                layout.Add((s, pos, n));
                continue;
            }
            if (off != AlignUp(pos, s)) throw new TileException($"S{s} at {off}, expected {AlignUp(pos, s)} (after {pos})");
            start[s] = off;
            layout.Add((s, off, n));
            pos = off + n * t.Size(s);
        }
        int blobStart = pos;
        var endOf = Order.ToDictionary(s => s, s => start[s] + (desc[s].cnt + Extra(s)) * t.Size(s));
        int nz = d.Length;
        while (nz > 0 && d[nz - 1] == 0) nz--;
        t.Blob = d[blobStart..Math.Max(nz, blobStart)];

        object? Resolve(int val, int target)
        {
            if (val == 0) return null;
            int[] secs = target == 56 ? [5, 6] : [target];
            foreach (int ts in secs)
                if (start[ts] <= val && val < endOf[ts] && (val - start[ts]) % t.Size(ts) == 0)
                    return new Ref(ts, (val - start[ts]) / t.Size(ts));
            foreach (int ts in secs)
                if (val == endOf[ts]) return new Ref(ts, (endOf[ts] - start[ts]) / t.Size(ts));
            t.Literals++;
            return val;
        }

        foreach (var (s, off, n) in layout)
        {
            var fields = PtrFields(btype, s);
            int size = t.Size(s);
            for (int k = 0; k < n; k++)
            {
                var r = new Rec(d[(off + k * size)..(off + (k + 1) * size)]);
                foreach (var (o, kind, target) in fields)
                {
                    if (o + 2 > size) continue;
                    int v = U16(r.Raw, o);
                    if (kind == Kind.Text)
                        r.Refs[o] = v == 0 ? null : v < blobStart ? throw new TileException($"S{s}[{k}] +{o}: text pointer {v} before the blob") : new TextRef(v - blobStart);
                    else r.Refs[o] = Resolve(v, target);
                    r.Raw[o] = r.Raw[o + 1] = 0;
                }
                if (s == 10 && BinaryPrimitives.ReadUInt32BigEndian(r.Raw) == t.BlockId)
                {
                    r.Refs[4] = Resolve(U16(r.Raw, 4), 4);
                    r.Own = true;
                    Array.Clear(r.Raw, 0, 6);
                }
                t.Secs[s].Add(r);
            }
        }
        return t;
    }

    /// <summary>Section start offsets and blob start for the current record counts.</summary>
    public (Dictionary<int, int> start, int blobStart) Layout()
    {
        var start = new Dictionary<int, int>();
        int pos = Prolog;
        foreach (int s in Order)
        {
            int n = Secs[s].Count;
            if (ZeroOffset.Contains(s) && n == 0) { start[s] = pos; continue; }
            start[s] = AlignUp(pos, s);
            pos = start[s] + n * Size(s);
        }
        return (start, pos);
    }

    public int DecodedSize() => Layout().blobStart + Blob.Length;

    public byte[] Build(int unit = 2048, int padUnits = 0)
    {
        var (start, blobStart) = Layout();
        int total = blobStart + Blob.Length;
        int nUnits = padUnits > 0 ? padUnits : Math.Max(NUnits, (total + unit - 1) / unit);
        if (total > nUnits * unit) throw new TileException($"tile needs {total} bytes, more than {nUnits} units");
        var d = new byte[nUnits * unit];
        Head.AsSpan(0, 8).CopyTo(d);
        BinaryPrimitives.WriteUInt32BigEndian(d, BlockId);
        for (int s = 0; s < 15; s++)
        {
            int n = Secs[s].Count - Extra(s);
            int off = ZeroOffset.Contains(s) && n == 0 ? 0 : start[s];
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(8 + 4 * s), (ushort)off);
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(10 + 4 * s), (ushort)Math.Max(n, 0));
        }
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x44), BBox.X1);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x48), BBox.Y1);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x4C), BBox.X2);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(0x50), BBox.Y2);
        HdrWords.CopyTo(d, HdrStart);

        int Addr(object? r) => r switch
        {
            null => 0,
            int i => i,
            TextRef t => blobStart + t.Rel,
            Ref p => start[p.Sec] + p.Idx * Size(p.Sec),
            _ => throw new TileException("bad pointer"),
        };

        foreach (int s in Order)
        {
            int size = Size(s);
            var list = Secs[s];
            for (int k = 0; k < list.Count; k++)
            {
                var rec = list[k];
                var span = d.AsSpan(start[s] + k * size, size);
                rec.Raw.AsSpan(0, size).CopyTo(span);
                if (rec.Own) BinaryPrimitives.WriteUInt32BigEndian(span, BlockId);
                foreach (var (o, r) in rec.Refs) BinaryPrimitives.WriteUInt16BigEndian(span[o..], (ushort)Addr(r));
            }
        }
        Blob.CopyTo(d, blobStart);
        return d;
    }

    // ------------------------------------------------------------------ rules
    public static int LinkClass(byte[] raw) => raw[0x10] & 0x0F;

    /// <summary>Ordering rules the unit needs (OpenCarin 03 §6.7): rule -> (ok, bad).</summary>
    public Dictionary<string, (int ok, int bad)> CheckRules()
    {
        var res = new Dictionary<string, (int ok, int bad)>();
        void Tally(string name, bool cond)
        {
            res.TryGetValue(name, out var v);
            res[name] = cond ? (v.ok + 1, v.bad) : (v.ok, v.bad + 1);
        }
        var links = Secs[4].Take(Secs[4].Count - 1).ToList();
        byte[] Node(object? r) { var p = (Ref)r!; return Secs[p.Sec][p.Idx].Raw; }
        (int, int) Uv(byte[] raw) => (U16(raw, 0), U16(raw, 2));
        foreach (var l in links)
            Tally("start node lower (u,v)", Uv(Node(l.Refs[0])).CompareTo(Uv(Node(l.Refs[2]))) <= 0);
        var groups = Secs[3];
        for (int g = 0; g + 1 < groups.Count; g++)
        {
            int l0 = ((Ref)groups[g].Refs[0]!).Idx, l1 = ((Ref)groups[g + 1].Refs[0]!).Idx;
            int n0 = ((Ref)groups[g].Refs[2]!).Idx, n1 = ((Ref)groups[g + 1].Refs[2]!).Idx;
            for (int i = l0; i < l1; i++) Tally("link class == group", LinkClass(links[i].Raw) == g);
            for (int k = n0; k + 1 < n1; k++)
                Tally("S5 sorted by (u,v) in group", Uv(Secs[5][k].Raw).CompareTo(Uv(Secs[5][k + 1].Raw)) <= 0);
        }
        foreach (int sec in (int[])[5, 6])
        {
            var nodes = Secs[sec];
            for (int k = 0; k < nodes.Count; k++)
            {
                if (nodes[k].Refs.GetValueOrDefault(4) is not Ref cur) continue;
                var cyc = new List<int>();
                var seen = new HashSet<int>();
                object? c = cur;
                while (c is Ref r && r.Idx < links.Count && seen.Add(r.Idx))
                {
                    var l = links[r.Idx];
                    bool atStart = l.Refs[0] is Ref a && a == new Ref(sec, k);
                    cyc.Add(atStart ? l.Raw[0x0E] : l.Raw[0x0F]);
                    c = atStart ? l.Refs[6] : l.Refs[8];
                }
                if (cyc.Count >= 2)
                {
                    bool sorted = true;
                    for (int i = 1; i < cyc.Count; i++) if (cyc[i] < cyc[i - 1]) sorted = false;
                    Tally("cycle clockwise from smallest bearing", sorted);
                }
            }
        }
        return res;
    }
}
