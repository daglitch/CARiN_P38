using System.Buffers.Binary;
using Osm2Carin.Core;
using Osm2Carin.Core.Cf1;
using Xunit.Abstractions;

namespace Osm2Carin.Tests;

/// <summary>Phase 3d: how the original disc ties its house-number blocks (0x04) to the street tiles.</summary>
public class HouseNumberTests(ITestOutputHelper log)
{
    [Fact]
    public void OriginalHouseNumberBlocksMatchTheirTiles()
    {
        var iso = Fixtures.Iso();
        if (iso == null) return;
        var db = Carindb.ReadCarindb(iso);
        var blocks = Carindb.Walk(db).ToList();
        var tileAt = blocks.Where(b => b.Type == 0).ToDictionary(b => (uint)((b.Unit << 8) | b.Units));
        var hn = blocks.Where(b => b.Type == 4).ToList();
        var c = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        void Inc(string k) => c.AddOrUpdate(k, 1, (_, v) => v + 1);
        Parallel.ForEach(hn, b =>
        {
            var d = Carindb.Raw(db, b);
            int off = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8)), n = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10));
            uint tid = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12));
            if (off != 0x10) Inc("descriptor offset not 0x10");
            if (d[6] != 0 || d[7] != 0) Inc("bytes 6/7 not zero");
            if (0x10 + 10 * n > b.Units * 2048) Inc("records overflow the block");
            if ((0x10 + 10 * n + 2047) / 2048 != b.Units) Inc("block larger than needed");
            if (!tileAt.TryGetValue(tid, out var tb)) { Inc("tile missing"); return; }
            var raw = Carindb.Raw(db, tb);
            var td = (tb.Comp & 1) != 0 ? Decoder00.DecodeBlock(raw, Fmt.RecordSizeTable, 26, 8, 2048) : raw;
            var t = Tile26.Parse(td);
            int s4 = t.Secs[4].Count;
            Inc(n == s4 ? "count = S4 incl. sentinel" : n == s4 - 1 ? "count = S4 without sentinel" : "count other");
            uint back = BinaryPrimitives.ReadUInt32BigEndian(t.HdrWords);
            Inc(back == ((uint)((b.Unit << 8) | b.Units)) ? "tile +0x54 = this block" : "tile +0x54 other");
            Inc($"tile +0x70 = {t.HdrWords[0x1C]:X2}{t.HdrWords[0x1D]:X2}");
            for (int i = 0; i < Math.Min(n, s4); i++)
            {
                var r = d.AsSpan(0x10 + 10 * i);
                bool has = false;
                for (int f = 0; f < 4; f++) if (BinaryPrimitives.ReadUInt16BigEndian(r[(2 * f)..]) != 0x7FFF) has = true;
                int scheme = BinaryPrimitives.ReadUInt16BigEndian(r[8..]);
                bool bit = i < s4 - 1 && (t.Secs[4][i].Raw[0x1B] & 1) != 0;
                Inc($"record numbers={has} scheme={scheme} bit0={bit}{(i == s4 - 1 ? " (sentinel)" : "")}");
                if (has)
                {
                    // the four fields: start/end of side a, start/end of side b
                    int f0 = BinaryPrimitives.ReadUInt16BigEndian(r), f1 = BinaryPrimitives.ReadUInt16BigEndian(r[2..]);
                    int f2 = BinaryPrimitives.ReadUInt16BigEndian(r[4..]), f3 = BinaryPrimitives.ReadUInt16BigEndian(r[6..]);
                    if (scheme == 2 && f0 != 0x7FFF && f2 != 0x7FFF) Inc(f0 % 2 == f2 % 2 ? "s2 side a same parity" : "s2 side a mixed parity");
                    if (scheme == 2 && f0 != 0x7FFF && f1 != 0x7FFF) Inc(f0 % 2 != f1 % 2 ? "s2 sides opposite parity" : "s2 sides same parity");
                    if ((f0 == 0x7FFF) != (f2 == 0x7FFF)) Inc("side a half-set");
                    if (f0 == 0) Inc("number 0");
                }
            }
        });
        // tiles without a 0x04 block
        var linked = new HashSet<uint>(hn.Select(b => BinaryPrimitives.ReadUInt32BigEndian(Carindb.Raw(db, b).AsSpan(12))));
        foreach (var tb in blocks.Where(b => b.Type == 0))
            if (!linked.Contains((uint)((tb.Unit << 8) | tb.Units)))
            {
                var raw = Carindb.Raw(db, tb);
                var td = (tb.Comp & 1) != 0 ? Decoder00.DecodeBlock(raw, Fmt.RecordSizeTable, 26, 8, 2048) : raw;
                var t = Tile26.Parse(td);
                Inc($"no 0x04: +0x54={BinaryPrimitives.ReadUInt32BigEndian(t.HdrWords):X} +0x70={t.HdrWords[0x1C]:X2}{t.HdrWords[0x1D]:X2}");
                Inc($"no 0x04: any bit0={t.Secs[4].Any(r => (r.Raw[0x1B] & 1) != 0)}");
            }
        foreach (var kv in c.OrderBy(kv => kv.Key)) log.WriteLine($"{kv.Key}: {kv.Value:N0}");
        Assert.Equal(20702, hn.Count);
    }

    /// <summary>The original segments of every 8th street tile with house numbers, with their names, points
    /// and stored 0x04 record.</summary>
    public static List<(string name, List<(long x, long y)> pts, ushort[] rec)> OriginalSegments(byte[] db, int every = 8)
    {
        var blocks = Carindb.Walk(db).ToList();
        var tileAt = blocks.Where(b => b.Type == 0).ToDictionary(b => (uint)((b.Unit << 8) | b.Units));
        var hn = blocks.Where(b => b.Type == 4).Where((b, i) => i % every == 0).ToList();
        var o = new System.Collections.Concurrent.ConcurrentBag<(string, List<(long, long)>, ushort[])>();
        Parallel.ForEach(hn, b =>
        {
            var d = Carindb.Raw(db, b);
            int n = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10));
            var tb = tileAt[BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12))];
            var raw = Carindb.Raw(db, tb);
            var t = Tile26.Parse((tb.Comp & 1) != 0 ? Decoder00.DecodeBlock(raw, Fmt.RecordSizeTable, 26, 8, 2048) : raw);
            (long, long) G(byte[] r, int o) => (t.BBox.X1 + 64L * BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(o)),
                                                 t.BBox.Y1 + 64L * BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(o + 2)));
            string Text(int rel) { int e = Array.IndexOf(t.Blob, (byte)0, rel); return System.Text.Encoding.Latin1.GetString(t.Blob, rel, (e < 0 ? t.Blob.Length : e) - rel); }
            for (int i = 0; i < n; i++)
            {
                var rec = new ushort[5];
                for (int f = 0; f < 5; f++) rec[f] = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(0x10 + 10 * i + 2 * f));
                if (rec[4] == 0) continue;
                var s4 = t.Secs[4][i];
                if (s4.Refs.GetValueOrDefault(0x18) is not Ref s2 || t.Secs[2][s2.Idx].Refs.GetValueOrDefault(0) is not TextRef nm) continue;
                if (s4.Refs[0] is not Ref a || s4.Refs[2] is not Ref z || s4.Refs[4] is not Ref q0 || t.Secs[4][i + 1].Refs[4] is not Ref q1) continue;
                var pts = new List<(long, long)> { G(t.Secs[a.Sec][a.Idx].Raw, 0) };
                for (int q = q0.Idx; q < q1.Idx; q++) pts.Add(G(t.Secs[7][q].Raw, 0));
                pts.Add(G(t.Secs[z.Sec][z.Idx].Raw, 0));
                o.Add((Text(nm.Rel), pts, rec));
            }
        });
        return o.ToList();
    }

    /// <summary>TIGER ranges, matched to the original disc's own segments, against the numbers the disc stores:
    /// which side is A, how many segments get numbers, how close they are.</summary>
    [Fact]
    public void TigerAgreesWithTheOriginalDisc()
    {
        var iso = Fixtures.Iso();
        string tiger = Path.Combine(Paths.Repo ?? "", "out", "cache", "tiger");
        var zips = Osm2Carin.Core.Addr.Tiger.Files(tiger, ["06", "32"]);
        if (iso == null || zips.Count == 0) return;
        Progress.Enabled = false;
        // California and Nevada only (the disc also has other US metro areas, which these TIGER files don't cover)
        var segs = OriginalSegments(Carindb.ReadCarindb(iso)).Where(s =>
        {
            var (lon, lat) = Geo.ToLonLat(s.pts[0].x, s.pts[0].y);
            return lat > 32.5 && lat < 42 && lon > -124.5 && lon < -114;
        }).ToList();
        var hn = Osm2Carin.Core.Addr.HouseNumbers.Load(zips, s => log.WriteLine(s));
        log.WriteLine($"{segs.Count:N0} original segments with numbers; names e.g. {string.Join(" | ", segs.Take(8).Select(s => s.name))}");
        var parity = new Dictionary<bool, double>();
        foreach (bool leftIsA in new[] { true, false })
        {
            hn.LeftIsA = leftIsA;
            int got = 0, parityA = 0, parityBoth = 0, near = 0, sides = 0;
            foreach (var (name, pts, rec) in segs)
            {
                var r = hn.Lookup(name, pts);
                if (r == null) continue;
                got++;
                bool pa = true;
                for (int side = 0; side < 2; side++)
                {
                    if (rec[side] == 0x7FFF || r[side] == 0x7FFF) continue;
                    sides++;
                    if (rec[side] % 2 == r[side] % 2) parityBoth++; else pa = false;
                    int lo = Math.Min(rec[side], rec[side + 2]), hi = Math.Max(rec[side], rec[side + 2]);
                    int ml = Math.Min(r[side], r[side + 2]), mh = Math.Max(r[side], r[side + 2]);
                    if (ml <= hi + 100 && mh >= lo - 100) near++;
                }
                if (pa) parityA++;
            }
            parity[leftIsA] = (double)parityBoth / Math.Max(sides, 1);
            if (leftIsA) Assert.True(got > 0.7 * segs.Count, "TIGER numbers fewer than 70% of the disc's segments");
            if (leftIsA) Assert.True(near > 0.95 * sides, "ranges far from the disc's");
            log.WriteLine($"side A = {(leftIsA ? "left" : "right")}: numbered {got:N0} / {segs.Count:N0} ({100.0 * got / segs.Count:F1}%); " +
                          $"sides with the disc's parity {parityBoth:N0} / {sides:N0} ({100.0 * parityBoth / Math.Max(sides, 1):F1}%), " +
                          $"ranges within 100 of the disc's {near:N0} ({100.0 * near / Math.Max(sides, 1):F1}%)");
        }
        log.WriteLine(hn.Summary);
        Assert.True(parity[true] > 0.9 && parity[false] < 0.1, "side A is not the left side");
    }
}
