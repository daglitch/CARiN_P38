using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Osm2Carin.Core.Roads;

namespace Osm2Carin.Core.Dest;

/// <summary>Phase 3b/3c + 4: a carindb with our roads, cities, streets and POIs.
///
/// Kept in place, byte for byte: the system blocks at the start (0x12 superblock, 0x13 CD info,
/// 0x07 layer directory, 0x0B letter index, 0x0A country) and the background layers and TMC at the
/// end (0x14-0x16, 0x1C-0x1E with their 0x08/0x09, 0x18, 0x17). Nothing in them points at the blocks
/// in between (checked: carin-disc-findings.md, Phase 3a), so everything between them (the old roads,
/// house numbers, cities, tries, street directory and POIs) is free space. Our blocks fill it first
/// and continue behind the old end; a gap a block does not fit gets a filler block.
/// Patched: 0x07 records 0-4 (road layers and POI layer) and its TMC list (cut: the TMC locations
/// point into the old roads); the 0x0A country record's city-trie root (and its country-wide POI
/// tries, cut).</summary>
public static class Pack
{
    static readonly HashSet<int> HeadTypes = [0x12, 0x13, 0x07, 0x0B, 0x0A];
    static readonly HashSet<int> TailTypes = [0x14, 0x15, 0x16, 0x1C, 0x1D, 0x1E];

    /// <summary>The free ranges of the original carindb: between the head of system blocks and the
    /// first background-layer block, and everything behind its end.</summary>
    public static List<(int start, int end)> FreeRanges(byte[] db)
    {
        int n = db.Length / Fmt.Unit, head = 0, tail = n, u = 0;
        bool inHead = true;
        while (u < n)
        {
            uint bid = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(u * Fmt.Unit));
            int t = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(u * Fmt.Unit + 4));
            if (inHead && HeadTypes.Contains(t)) head = u + (int)(bid & 0xFF); else inHead = false;
            if (TailTypes.Contains(t)) { tail = u; break; }
            u += (int)(bid & 0xFF);
        }
        return [(head, tail), (n, int.MaxValue)];
    }

    public sealed class Result
    {
        public byte[] Db = [];
        public JsonObject Report = new();
        public List<Blk> Ours = new();
    }

    /// <summary>Allocate the planned destination blocks (roads are already allocated by Link.Run with
    /// the same allocator), then assemble the carindb.</summary>
    public static Result Build(byte[] original, RoadTiles rt, Directory dir, UnitAllocator alloc, Action<string> log)
    {
        var res = new Result();
        var blks = new List<Blk>();
        blks.AddRange(dir.CityTrie); blks.AddRange(dir.CityBlocks); blks.AddRange(dir.StreetTries); blks.AddRange(dir.StreetBlocks);
        blks.AddRange(dir.PoiTries); blks.AddRange(dir.PoiBlocks);
        foreach (var b in blks) b.Id = alloc.AllocId(b.Units);
        if (dir.PoiIndex != null && dir.PoiTiles.Count > 0)
        {
            foreach (var t in dir.PoiTiles) { t.Id = alloc.AllocId(t.Units); t.Spec.BlockId = t.Id; }
            Spatial.Allocate(dir.PoiIndex, alloc);
        }
        blks.AddRange(dir.PoiTiles);
        alloc.Finish();
        int end = Math.Max(alloc.Pos, original.Length / Fmt.Unit);
        var db = new byte[(long)end * Fmt.Unit];

        // kept blocks: everything outside the free ranges we used
        var free = FreeRanges(original);
        Array.Copy(original, 0, db, 0, (long)free[0].start * Fmt.Unit);
        Array.Copy(original, (long)free[0].end * Fmt.Unit, db, (long)free[0].end * Fmt.Unit, (long)(free[1].start - free[0].end) * Fmt.Unit);
        void Put(byte[] b) => b.CopyTo(db, (long)(BinaryPrimitives.ReadUInt32BigEndian(b) >> 8) * Fmt.Unit);
        foreach (var b in rt.Blocks) Put(b);
        foreach (var b in blks) Put(b.Bytes());
        if (dir.PoiIndex != null && dir.PoiTiles.Count > 0) foreach (var b in Spatial.Write(dir.PoiIndex)) Put(b);
        int filler = 0;
        foreach (var (start, units) in alloc.Gaps)
            for (int u = start, left = units; left > 0;)
            {
                int k = Math.Min(255, left);
                var f = new FillerBlk(k) { Id = (uint)((u << 8) | k) };
                Put(f.Bytes()); filler += k; u += k; left -= k;
            }

        // 0x07: road layers 0x00/0x01/0x02/0x03 = records 0/2/3/4, POI layer = record 1; TMC list cut
        int o7 = Graft.DirectoryUnit * Fmt.Unit;
        foreach (var (rec, lv) in Graft.RoadRecords)
            BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(o7 + 0x10 + 24 * rec), rt.Index[lv].GridBlocks[0].blockId);
        uint poiGrid = dir.PoiIndex != null && dir.PoiTiles.Count > 0 ? dir.PoiIndex.GridBlocks[0].blockId : 0;
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(o7 + 0x10 + 24 * 1), poiGrid);
        BinaryPrimitives.WriteUInt16BigEndian(db.AsSpan(o7 + 0x0E), 0);             // S1 (TMC tables) count

        // 0x0A: the country's city trie; country-wide POI tries cut (S3)
        int oA = CountryUnit(db) * Fmt.Unit;
        int rec0 = oA + BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(oA + 12));    // S1: the country record
        BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(rec0), dir.CityRoot.Blk!.Id);
        BinaryPrimitives.WriteUInt16BigEndian(db.AsSpan(rec0 + 4), (ushort)dir.CityRoot.Offset);
        BinaryPrimitives.WriteUInt16BigEndian(db.AsSpan(rec0 + 6), (ushort)dir.CityRoot.Recs.Count);
        BinaryPrimitives.WriteUInt16BigEndian(db.AsSpan(rec0 + 0x18), 0);
        BinaryPrimitives.WriteUInt16BigEndian(db.AsSpan(rec0 + 0x1A), 0);
        BinaryPrimitives.WriteUInt16BigEndian(db.AsSpan(oA + 22), 0);               // S3 descriptor count

        res.Db = db;
        res.Ours = blks;
        res.Report["free_ranges"] = $"{free[0].start}-{free[0].end}, {free[1].start}-";
        res.Report["filler_units"] = filler;
        res.Report["carindb_units"] = end;
        foreach (var g in blks.GroupBy(b => b.Type).OrderBy(g => g.Key))
            res.Report[$"0x{g.Key:X2}"] = new JsonObject { ["blocks"] = g.Count(), ["units"] = g.Sum(b => b.Units) };
        log($"packed: {end:N0} units ({filler:N0} filler); our destination blocks {blks.Sum(b => b.Units):N0} units");
        return res;
    }

    static int CountryUnit(byte[] db)
    {
        int u = 0;
        while (true)
        {
            uint bid = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(u * Fmt.Unit));
            if (BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(u * Fmt.Unit + 4)) == 0x0A) return u;
            u += (int)(bid & 0xFF);
        }
    }

    /// <summary>Checks read back from the packed bytes alone: the block chain; the unit's lookups
    /// from 0x07 for every road and POI tile; and the destination chain from the 0x0A country record:
    /// city trie -> city -> street trie -> street -> road links, each landing on a section 4 record
    /// of a street tile whose name is the street's; POI tries -> POI -> road link, and the 0x06
    /// records -> POI with the same position and ID.</summary>
    public static (JsonObject res, List<string> problems) Check(byte[] db, RoadTiles rt, Directory dir)
    {
        var res = new JsonObject();
        var problems = new List<string>();
        void Bad(string s) { if (problems.Count < 50) problems.Add(s); }
        int n = db.Length / Fmt.Unit, u = 0, blocks = 0;
        var type = new Dictionary<int, (int units, int type)>();
        while (u < n)
        {
            uint bid = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(u * Fmt.Unit));
            int ln = (int)(bid & 0xFF);
            if (bid >> 8 != u || ln == 0) { Bad($"chain broken at unit {u}"); break; }
            type[u] = (ln, BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(u * Fmt.Unit + 4)));
            u += ln; blocks++;
        }
        res["blocks"] = blocks;
        res["chain covers the file"] = u == n;
        byte[] Block(int unit) => db.AsSpan(unit * Fmt.Unit, type[unit].units * Fmt.Unit).ToArray();
        int T(uint id) => id != 0 && type.TryGetValue((int)(id >> 8), out var t) && t.units == (int)(id & 0xFF) ? t.type : -1;
        int U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
        uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
        string Str(byte[] b, int o) { if (o == 0) return ""; int e = o; while (e < b.Length && b[e] != 0) e++; return Encoding.Latin1.GetString(b, o, e - o); }

        // spatial lookups from 0x07
        int o7 = Graft.DirectoryUnit * Fmt.Unit;
        long found = 0, missed = 0;
        foreach (var (rec, lv) in Graft.RoadRecords.Append((1, 6)))
        {
            int o = o7 + 0x10 + 24 * rec;
            uint gid = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(o));
            if (gid == 0) continue;
            long x1 = BinaryPrimitives.ReadInt32BigEndian(db.AsSpan(o + 4)), y1 = BinaryPrimitives.ReadInt32BigEndian(db.AsSpan(o + 8));
            long side = BinaryPrimitives.ReadInt32BigEndian(db.AsSpan(o + 12)) - x1;
            var tiles = lv == 6 ? dir.PoiTiles.Select(t => t.Spec).ToList() : rt.Levels[lv];
            foreach (var sp in tiles)
            {
                var (a, b, c, d) = sp.Tile.BBox;
                if (Spatial.Lookup(x1, y1, side, gid, Block, ((long)a + c) / 2, ((long)b + d) / 2) == sp.BlockId) found++;
                else { missed++; Bad($"0x07 record {rec}: lookup misses 0x{sp.BlockId:x8}"); }
            }
        }
        res["tiles found from 0x07"] = found;
        res["tiles missed from 0x07"] = missed;

        // the name of section 4 record at `off` of a street tile (decoded): S4 +0x18 -> S2 -> +0 name
        var decoded = rt.Levels[0].ToDictionary(sp => sp.BlockId, sp => sp.Decoded);
        string LinkName(uint tile, int off, out bool ok)
        {
            ok = false;
            if (!decoded.TryGetValue(tile, out var d)) return "";
            int o4 = U16(d, 8 + 16), n4 = U16(d, 10 + 16);
            if (off < o4 || off >= o4 + 30 * n4 || (off - o4) % 30 != 0) return "";
            ok = true;
            int s2 = U16(d, off + 0x18);
            return s2 == 0 || s2 + 2 > d.Length ? "" : Str(d, U16(d, s2));
        }

        // walk a trie level: (letter, leaf, block, offset, count) records
        IEnumerable<(uint id, int off, int count)> Leaves(uint id, int off, int count, int want, int depth = 0)
        {
            if (depth > 64) yield break;
            var b = Block((int)(id >> 8));
            for (int i = 0; i < count; i++)
            {
                int o = off + 12 * i;
                uint tid = U32(b, o); int leaf = b[o + 5], to = U16(b, o + 6), tc = U16(b, o + 8);
                if (leaf == 1) { if (T(tid) != want) Bad($"trie leaf to type {T(tid)}"); yield return (tid, to, tc); }
                else foreach (var x in Leaves(tid, to, tc, want, depth + 1)) yield return x;
            }
        }

        int oA = CountryUnit(db) * Fmt.Unit;
        var bA = db.AsSpan(oA, Fmt.Unit).ToArray();
        int rec0 = U16(bA, 12);
        long cities = 0, streets = 0, links = 0, linksNamed = 0, poisSeen = 0, poiLinks = 0;
        var poiAt = new Dictionary<(uint, int), (int x, int y, uint id)>();
        foreach (var (cid, coff, ccount) in Leaves(U32(bA, rec0), U16(bA, rec0 + 4), U16(bA, rec0 + 6), 0x0C))
        {
            var cb = Block((int)(cid >> 8));
            for (int i = 0; i < ccount; i++)
            {
                int s0 = coff + 8 * i, s1 = U16(cb, s0 + 6);
                cities++;
                uint centre = U32(cb, s1 + 12);
                if (centre != 0 && T(centre) != 0x00) Bad($"city centre not a street tile");
                if (U32(cb, s1) != 0)
                    foreach (var (eid, eoff, ecount) in Leaves(U32(cb, s1), U16(cb, s1 + 4), U16(cb, s1 + 6), 0x0E))
                    {
                        var eb = Block((int)(eid >> 8));
                        for (int j = 0; j < ecount; j++)
                        {
                            int e0 = eoff + 8 * j, e1 = U16(eb, e0 + 6);
                            string name = Str(eb, U16(eb, e0));
                            streets++;
                            int s2 = U16(eb, e1), cnt = eb[e1 + 2];
                            for (int r = 0; r < cnt; r++)
                            {
                                int o = s2 + 24 * r;
                                uint tile = U32(eb, o + 16); int off = U16(eb, o + 20), num = U16(eb, o + 22);
                                links++;
                                if (T(tile) != 0x00) { Bad($"street {name}: link to type {T(tile)}"); continue; }
                                for (int q = 0; q < num; q++)
                                {
                                    string ln = LinkName(tile, off + 30 * q, out bool ok);
                                    if (!ok) Bad($"street {name}: offset {off + 30 * q} not a section 4 record");
                                    else if (ln == name) linksNamed++;
                                    else Bad($"street {name}: link named '{ln}'");
                                }
                            }
                        }
                    }
                int p3 = U16(cb, s1 + 8), n3 = U16(cb, s1 + 10);
                for (int k = 0; k < n3; k++)
                {
                    int o = p3 + 12 * k;
                    foreach (var (pid, poff, pcount) in Leaves(U32(cb, o), U16(cb, o + 4), U16(cb, o + 6), 0x10))
                    {
                        var pb = Block((int)(pid >> 8));
                        for (int j = 0; j < pcount; j++)
                        {
                            int q0 = poff + 8 * j, q1 = U16(pb, q0 + 6);
                            poisSeen++;
                            poiAt[(pid, q0)] = (BinaryPrimitives.ReadInt32BigEndian(pb.AsSpan(q1)), BinaryPrimitives.ReadInt32BigEndian(pb.AsSpan(q1 + 4)), U32(pb, q1 + 28));
                            int l4 = U16(pb, q1 + 8);
                            if (l4 != 0)
                            {
                                uint tile = U32(pb, l4);
                                LinkName(tile, U16(pb, l4 + 4), out bool ok);
                                if (ok) poiLinks++; else Bad($"POI link not on a section 4 record");
                            }
                        }
                    }
                }
            }
        }
        res["cities reached from 0x0A"] = cities;
        res["streets reached"] = streets;
        res["street links"] = links;
        res["street links landing on a road of that name"] = linksNamed;
        res["POIs reached from the cities"] = poisSeen;
        res["POI road links on a street link"] = poiLinks;

        long poiTileRecs = 0, poiTileOk = 0;
        foreach (var t in dir.PoiTiles)
        {
            var b = Block((int)(t.Id >> 8));
            int cnt = U16(b, 10);
            long x1 = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(0x10)), y1 = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(0x14));
            for (int i = 0; i < cnt; i++)
            {
                int o = 0x20 + 20 * i;
                poiTileRecs++;
                if (poiAt.TryGetValue((U32(b, o), U16(b, o + 4)), out var at) && at.id == U32(b, o + 16)
                    && Math.Abs(at.x - (x1 + 64L * U16(b, o + 6))) < 64 && Math.Abs(at.y - (y1 + 64L * U16(b, o + 8))) < 64) poiTileOk++;
                else Bad($"0x06 record does not match its POI");
            }
        }
        res["0x06 records"] = poiTileRecs;
        res["0x06 records matching their POI"] = poiTileOk;
        return (res, problems);
    }
}
