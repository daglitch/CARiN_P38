using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Osm2Carin.Core.Osm;

/// <summary>Tags of one OSM object (small: a list of pairs, looked up linearly).</summary>
public sealed class Tags(List<(string k, string v)> pairs)
{
    public readonly List<(string k, string v)> Pairs = pairs;

    /// <summary>The value or null (like osmium's tags.get).</summary>
    public string? Get(string key)
    {
        foreach (var (k, v) in Pairs) if (k == key) return v;
        return null;
    }

    public bool Has(string key) => Get(key) != null;
}

public record struct Member(char Type, long Ref, string Role);

public sealed class OsmWay(long id, Tags tags, long[] nodes)
{
    public long Id = id; public Tags Tags = tags; public long[] Nodes = nodes;
}

public sealed class OsmRelation(long id, Tags tags, Member[] members)
{
    public long Id = id; public Tags Tags = tags; public Member[] Members = members;
}

public sealed class OsmNode(long id, Tags? tags, int x7, int y7)
{
    public long Id = id; public Tags? Tags = tags;
    /// <summary>Location in 1e-7 degrees, as libosmium stores it.</summary>
    public int X7 = x7, Y7 = y7;
    public double Lon => X7 / 10000000.0;
    public double Lat => Y7 / 10000000.0;
    public bool Valid => X7 >= -1800000000 && X7 <= 1800000000 && Y7 >= -900000000 && Y7 <= 900000000;
}

/// <summary>What to extract from each primitive block.</summary>
public sealed class PbfFilter
{
    public bool Nodes, Ways, Relations;
    /// <summary>Only objects with at least one of these keys (null = all). Nodes without a key match
    /// are still returned when NodeIds contains them (a sorted id list), without tags.</summary>
    public HashSet<string>? Keys;
    public long[]? NodeIds;
    public HashSet<string>? NodeKeys;
}

public sealed class PbfBlock
{
    public readonly List<OsmNode> Nodes = new();
    public readonly List<OsmWay> Ways = new();
    public readonly List<OsmRelation> Relations = new();
    /// <summary>Ways and relations in file order.</summary>
    public readonly List<object> Items = new();
}

/// <summary>Minimal OpenStreetMap PBF reader (OSMData blocks: dense and plain nodes, ways,
/// relations), with blobs decoded in parallel and returned in file order. Coordinates are
/// converted as libosmium does: (offset + granularity * v) / 100, truncated, in 1e-7 degrees.</summary>
public static class Pbf
{
    public static IEnumerable<PbfBlock> Read(string path, PbfFilter filter, int window = 64)
    {
        using var f = File.OpenRead(path);
        var batch = new List<(string type, byte[] blob)>(window);
        var hdr = new byte[4];
        while (true)
        {
            batch.Clear();
            while (batch.Count < window)
            {
                if (f.ReadAtLeast(hdr, 4, throwOnEndOfStream: false) < 4) break;
                int hlen = BinaryPrimitives.ReadInt32BigEndian(hdr);
                var bh = new byte[hlen];
                f.ReadExactly(bh);
                string type = "";
                int dsize = 0;
                var r = new ProtoReader(bh);
                while (r.Next(out int fld, out int wt))
                {
                    if (fld == 1) type = r.String();
                    else if (fld == 3) dsize = (int)r.Varint();
                    else r.Skip(wt);
                }
                var blob = new byte[dsize];
                f.ReadExactly(blob);
                batch.Add((type, blob));
            }
            if (batch.Count == 0) yield break;
            var outp = new PbfBlock?[batch.Count];
            Parallel.For(0, batch.Count, i =>
            {
                if (batch[i].type == "OSMData") outp[i] = ParseBlock(Inflate(batch[i].blob), filter);
            });
            foreach (var b in outp) if (b != null) yield return b;
        }
    }

    static byte[] Inflate(byte[] blob)
    {
        var r = new ProtoReader(blob);
        byte[]? raw = null, z = null;
        int rawSize = 0;
        while (r.Next(out int fld, out int wt))
        {
            if (fld == 1) raw = r.Bytes();
            else if (fld == 2) rawSize = (int)r.Varint();
            else if (fld == 3) z = r.Bytes();
            else r.Skip(wt);
        }
        if (raw != null) return raw;
        if (z == null) throw new InvalidDataException("unsupported PBF blob compression");
        var o = new byte[rawSize];
        using var zs = new ZLibStream(new MemoryStream(z), CompressionMode.Decompress);
        zs.ReadExactly(o);
        return o;
    }

    static PbfBlock ParseBlock(byte[] data, PbfFilter flt)
    {
        var strings = new List<string>();
        var groups = new List<(int off, int len)>();
        long gran = 100, latOff = 0, lonOff = 0;
        var r = new ProtoReader(data);
        while (r.Next(out int fld, out int wt))
        {
            switch (fld)
            {
                case 1:
                    var st = new ProtoReader(r.Bytes());
                    while (st.Next(out int f2, out int w2))
                        if (f2 == 1) strings.Add(Encoding.UTF8.GetString(st.BytesSpan())); else st.Skip(w2);
                    break;
                case 2: groups.Add(r.SubRange()); break;
                case 17: gran = (long)r.Varint(); break;
                case 19: latOff = (long)r.Varint(); break;
                case 20: lonOff = (long)r.Varint(); break;
                default: r.Skip(wt); break;
            }
        }
        // string-table indices of the keys of interest
        bool[]? keyHit = flt.Keys == null ? null : strings.Select(s => flt.Keys.Contains(s)).ToArray();
        bool[]? nodeKeyHit = flt.NodeKeys == null ? null : strings.Select(s => flt.NodeKeys.Contains(s)).ToArray();
        var blk = new PbfBlock();
        int Coord(long v, long off) => (int)((off + gran * v) / 100);

        Tags MakeTags(List<uint> keys, List<uint> vals)
        {
            var p = new List<(string, string)>(keys.Count);
            for (int i = 0; i < keys.Count; i++) p.Add((strings[(int)keys[i]], strings[(int)vals[i]]));
            return new Tags(p);
        }

        bool Hit(bool[]? hit, List<uint> keys)
        {
            if (hit == null) return true;
            foreach (var k in keys) if (hit[k]) return true;
            return false;
        }

        foreach (var (goff, glen) in groups)
        {
            var g = new ProtoReader(data, goff, glen);
            while (g.Next(out int fld, out int wt))
            {
                if (fld == 2 && flt.Nodes)
                {
                    var d = new ProtoReader(g.Bytes());
                    List<long>? ids = null, lats = null, lons = null;
                    List<int>? kv = null;
                    while (d.Next(out int f2, out int w2))
                    {
                        if (f2 == 1) ids = d.PackedSInt64();
                        else if (f2 == 8) lats = d.PackedSInt64();
                        else if (f2 == 9) lons = d.PackedSInt64();
                        else if (f2 == 10) kv = d.PackedInt32();
                        else d.Skip(w2);
                    }
                    if (ids == null) continue;
                    long id = 0, lat = 0, lon = 0;
                    int kvi = 0;
                    var keys = new List<uint>(); var vals = new List<uint>();
                    for (int i = 0; i < ids.Count; i++)
                    {
                        id += ids[i]; lat += lats![i]; lon += lons![i];
                        keys.Clear(); vals.Clear();
                        if (kv != null)
                        {
                            while (kvi < kv.Count && kv[kvi] != 0) { keys.Add((uint)kv[kvi]); vals.Add((uint)kv[kvi + 1]); kvi += 2; }
                            kvi++;
                        }
                        bool want = flt.NodeIds != null && Array.BinarySearch(flt.NodeIds, id) >= 0;
                        bool tagged = nodeKeyHit != null && Hit(nodeKeyHit, keys);
                        if (want || tagged)
                            blk.Nodes.Add(new OsmNode(id, tagged ? MakeTags(keys, vals) : null, Coord(lon, lonOff), Coord(lat, latOff)));
                    }
                }
                else if (fld == 1 && flt.Nodes)
                {
                    var n = new ProtoReader(g.Bytes());
                    long id = 0, lat = 0, lon = 0;
                    List<uint> keys = new(), vals = new();
                    while (n.Next(out int f2, out int w2))
                    {
                        if (f2 == 1) id = ProtoReader.ZigZag(n.Varint());
                        else if (f2 == 2) keys = n.PackedUInt32();
                        else if (f2 == 3) vals = n.PackedUInt32();
                        else if (f2 == 8) lat = ProtoReader.ZigZag(n.Varint());
                        else if (f2 == 9) lon = ProtoReader.ZigZag(n.Varint());
                        else n.Skip(w2);
                    }
                    bool want = flt.NodeIds != null && Array.BinarySearch(flt.NodeIds, id) >= 0;
                    bool tagged = nodeKeyHit != null && Hit(nodeKeyHit, keys);
                    if (want || tagged)
                        blk.Nodes.Add(new OsmNode(id, tagged ? MakeTags(keys, vals) : null, Coord(lon, lonOff), Coord(lat, latOff)));
                }
                else if (fld == 3 && flt.Ways)
                {
                    var w = new ProtoReader(g.Bytes());
                    long id = 0;
                    List<uint> keys = new(), vals = new();
                    List<long>? refs = null;
                    while (w.Next(out int f2, out int w2))
                    {
                        if (f2 == 1) id = (long)w.Varint();
                        else if (f2 == 2) keys = w.PackedUInt32();
                        else if (f2 == 3) vals = w.PackedUInt32();
                        else if (f2 == 8) refs = w.PackedSInt64();
                        else w.Skip(w2);
                    }
                    if (!Hit(keyHit, keys)) continue;
                    var nodes = new long[refs?.Count ?? 0];
                    long acc = 0;
                    for (int i = 0; i < nodes.Length; i++) { acc += refs![i]; nodes[i] = acc; }
                    var ow = new OsmWay(id, MakeTags(keys, vals), nodes);
                    blk.Ways.Add(ow); blk.Items.Add(ow);
                }
                else if (fld == 4 && flt.Relations)
                {
                    var rl = new ProtoReader(g.Bytes());
                    long id = 0;
                    List<uint> keys = new(), vals = new();
                    List<int>? roles = null; List<long>? memids = null; List<int>? types = null;
                    while (rl.Next(out int f2, out int w2))
                    {
                        if (f2 == 1) id = (long)rl.Varint();
                        else if (f2 == 2) keys = rl.PackedUInt32();
                        else if (f2 == 3) vals = rl.PackedUInt32();
                        else if (f2 == 8) roles = rl.PackedInt32();
                        else if (f2 == 9) memids = rl.PackedSInt64();
                        else if (f2 == 10) types = rl.PackedInt32();
                        else rl.Skip(w2);
                    }
                    if (!Hit(keyHit, keys)) continue;
                    int nm = memids?.Count ?? 0;
                    var mem = new Member[nm];
                    long acc = 0;
                    for (int i = 0; i < nm; i++)
                    {
                        acc += memids![i];
                        char t = types![i] switch { 0 => 'n', 1 => 'w', _ => 'r' };
                        mem[i] = new Member(t, acc, strings[roles![i]]);
                    }
                    var orl = new OsmRelation(id, MakeTags(keys, vals), mem);
                    blk.Relations.Add(orl); blk.Items.Add(orl);
                }
                else g.Skip(wt);
            }
        }
        return blk;
    }
}

/// <summary>Protocol-buffer wire format reader over a byte range.</summary>
public struct ProtoReader
{
    readonly byte[] buf;
    int pos;
    readonly int end;

    public ProtoReader(byte[] b) : this(b, 0, b.Length) { }
    public ProtoReader(byte[] b, int off, int len) { buf = b; pos = off; end = off + len; }

    public bool Next(out int field, out int wire)
    {
        if (pos >= end) { field = wire = 0; return false; }
        ulong k = Varint();
        field = (int)(k >> 3);
        wire = (int)(k & 7);
        return true;
    }

    public ulong Varint()
    {
        ulong v = 0;
        int shift = 0;
        while (true)
        {
            byte b = buf[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return v;
            shift += 7;
        }
    }

    public static long ZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    public (int off, int len) SubRange()
    {
        int n = (int)Varint();
        var r = (pos, n);
        pos += n;
        return r;
    }

    public byte[] Bytes() { var (o, n) = SubRange(); return buf.AsSpan(o, n).ToArray(); }
    public ReadOnlySpan<byte> BytesSpan() { var (o, n) = SubRange(); return buf.AsSpan(o, n); }
    public string String() => Encoding.UTF8.GetString(BytesSpan());

    public void Skip(int wire)
    {
        switch (wire)
        {
            case 0: Varint(); break;
            case 1: pos += 8; break;
            case 2: { int n = (int)Varint(); pos += n; break; }
            case 5: pos += 4; break;
            default: throw new InvalidDataException($"protobuf wire type {wire}");
        }
    }

    public List<long> PackedSInt64()
    {
        var (o, n) = SubRange();
        var r = new ProtoReader(buf, o, n);
        var l = new List<long>();
        while (r.pos < r.end) l.Add(ZigZag(r.Varint()));
        return l;
    }

    public List<int> PackedInt32()
    {
        var (o, n) = SubRange();
        var r = new ProtoReader(buf, o, n);
        var l = new List<int>();
        while (r.pos < r.end) l.Add((int)(long)r.Varint());
        return l;
    }

    public List<uint> PackedUInt32()
    {
        var (o, n) = SubRange();
        var r = new ProtoReader(buf, o, n);
        var l = new List<uint>();
        while (r.pos < r.end) l.Add((uint)r.Varint());
        return l;
    }
}
