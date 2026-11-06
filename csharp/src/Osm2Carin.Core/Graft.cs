using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Osm2Carin.Core.Roads;

namespace Osm2Carin.Core;

/// <summary>Phase 3a: put our road layers on the original disc (CD-ID 5944).
///
/// Every original block stays where it is, byte for byte, so no BLOCK_ID outside our layers changes.
/// Our layers (0x00, 0x03, 0x02, 0x01, each [0x08 grid][0x09 cells][tiles]) are appended to
/// `carindb` from its old end on, and the 0x07 layer directory's records 0, 2, 3, 4 are pointed at
/// our grids. The old road layers, house numbers, street directory, POI index and TMC stay on the
/// disc unreachable from the map, but intact: everything that pointed into them (0x0C city centres,
/// 0x0E street runs, 0x10 POI roads, 0x17 TMC, 0x0F/0x11 tries) still points at valid blocks rather
/// than into our data. OpenCarin found on a CNI1 that the unit reaches blocks only through their
/// BLOCK_IDs and that `carindb` may grow (01-architecture.md §4.4.1, "Moving a block").
///
/// The ISO: `carindb` is the last file on the disc, so only its directory record's size and the
/// volume space size in the volume descriptors change.</summary>
public static class Graft
{
    /// <summary>0x07 record index -> road layer (the disc's own order: 0x00, 0x06, 0x01, 0x02, 0x03, …).</summary>
    public static readonly (int record, int layer)[] RoadRecords = [(0, 0), (2, 1), (3, 2), (4, 3)];

    public const int DirectoryUnit = 2;

    public sealed class Iso
    {
        public byte[] Bytes = [];
        public int CarindbLba, CarindbSize, DirRecordOffset;   // DirRecordOffset: byte offset in the image
    }

    static int U32(byte[] b, int o) => (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

    /// <summary>Read the ISO and locate carindb and its directory record (root directory only).</summary>
    public static Iso Open(string isoPath)
    {
        var b = File.ReadAllBytes(isoPath);
        var pvd = b.AsSpan(16 * 2048, 2048);
        if (pvd[0] != 1 || !pvd.Slice(1, 5).SequenceEqual("CD001"u8)) throw new InvalidDataException("no primary volume descriptor");
        int rootLba = BinaryPrimitives.ReadInt32LittleEndian(pvd.Slice(156 + 2));
        int rootSize = BinaryPrimitives.ReadInt32LittleEndian(pvd.Slice(156 + 10));
        int start = rootLba * 2048;
        for (int i = 0; i < rootSize;)
        {
            int ln = b[start + i];
            if (ln == 0) { i = (i / 2048 + 1) * 2048; continue; }
            int nl = b[start + i + 32];
            var name = System.Text.Encoding.ASCII.GetString(b, start + i + 33, nl).Split(';')[0];
            if (name.Equals("carindb", StringComparison.OrdinalIgnoreCase))
                return new Iso
                {
                    Bytes = b, DirRecordOffset = start + i,
                    CarindbLba = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(start + i + 2)),
                    CarindbSize = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(start + i + 10)),
                };
            i += ln;
        }
        throw new InvalidDataException("carindb not in the root directory");
    }

    /// <summary>The unit our layers start at: the original carindb's length in units.</summary>
    public static int BaseUnit(Iso iso) => iso.CarindbSize / Fmt.Unit;

    /// <summary>Build the new ISO image. `rt` must have been linked with Link.Run(rt, BaseUnit(iso), …, withIndex: true).</summary>
    public static (byte[] iso, byte[] carindb, JsonObject report) Build(Iso iso, RoadTiles rt)
    {
        var rep = new JsonObject();
        long isoLen = iso.Bytes.Length;
        if (iso.CarindbSize % Fmt.Unit != 0) throw new InvalidDataException("carindb is not whole units");
        if ((long)iso.CarindbLba * 2048 + iso.CarindbSize != isoLen)
            throw new InvalidDataException("carindb is not the last thing in the image; moving files behind it is not implemented");
        int baseUnit = BaseUnit(iso);
        int first = (int)(BinaryPrimitives.ReadUInt32BigEndian(rt.Blocks[0]) >> 8);
        if (first != baseUnit) throw new InvalidOperationException($"road layers start at unit {first}, carindb ends at {baseUnit}: link with that base");
        if (rt.IndexRoot != Tiles.DiscRoot) throw new InvalidOperationException("the region is not inside the disc's quadtree root; 0x07 has one root per record");

        long added = rt.Blocks.Sum(b => (long)b.Length);
        if (added != (long)(rt.EndUnit - baseUnit) * Fmt.Unit) throw new InvalidOperationException("road blocks do not fill their units");
        var db = new byte[iso.CarindbSize + added];
        Array.Copy(iso.Bytes, (long)iso.CarindbLba * 2048, db, 0, iso.CarindbSize);
        long pos = iso.CarindbSize;
        foreach (var b in rt.Blocks) { b.CopyTo(db, pos); pos += b.Length; }

        // 0x07: records of 24 bytes from +0x10: BLOCK_ID, root x1 y1 x2 y2, two u16 parameters
        int o7 = DirectoryUnit * Fmt.Unit;
        if (BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(o7 + 4)) != 0x07) throw new InvalidDataException("unit 2 is not the 0x07 layer directory");
        var (rx, ry, rs) = rt.IndexRoot;
        var recs = new JsonArray();
        foreach (var (rec, lv) in RoadRecords)
        {
            int o = o7 + 0x10 + 24 * rec;
            uint old = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(o));
            int oldType = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan((int)(old >> 8) * Fmt.Unit + 4));
            if (oldType != 0x08) throw new InvalidDataException($"0x07 record {rec} does not point at a 0x08 grid");
            long x1 = U32(db, o + 4), y1 = U32(db, o + 8), x2 = U32(db, o + 12), y2 = U32(db, o + 16);
            if (x1 != rx || y1 != ry || x2 - x1 != rs || y2 - y1 != rs) throw new InvalidDataException($"0x07 record {rec}: root square differs from ours");
            if (!rt.Index.TryGetValue(lv, out var li) || li.GridBlocks.Count == 0) throw new InvalidOperationException($"no index for layer 0x0{lv}");
            uint id = li.GridBlocks[0].blockId;
            BinaryPrimitives.WriteUInt32BigEndian(db.AsSpan(o), id);
            recs.Add(new JsonObject
            {
                ["record"] = rec, ["layer"] = $"0x{lv:X2}", ["old"] = $"0x{old:x8}", ["new"] = $"0x{id:x8}",
                ["cell"] = li.Cell, ["grid"] = $"{li.N}x{li.N}", ["highest_class"] = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(o + 20)),
            });
        }
        rep["directory_records"] = recs;

        // ISO: old header area + new carindb; patch the sizes
        var outIso = new byte[(long)iso.CarindbLba * 2048 + db.Length];
        Array.Copy(iso.Bytes, outIso, (long)iso.CarindbLba * 2048);
        db.CopyTo(outIso, (long)iso.CarindbLba * 2048);
        int sectors = (int)(outIso.Length / 2048);
        void Both32(int off, int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(outIso.AsSpan(off), v);
            BinaryPrimitives.WriteInt32BigEndian(outIso.AsSpan(off + 4), v);
        }
        Both32(iso.DirRecordOffset + 10, db.Length);
        var vds = new JsonArray();
        for (int s = 16; s < 32; s++)
        {
            int o = s * 2048;
            if (!outIso.AsSpan(o + 1, 5).SequenceEqual("CD001"u8)) break;
            int t = outIso[o];
            if (t == 255) break;
            if (t == 1 || t == 2) { Both32(o + 80, sectors); vds.Add($"sector {s}, type {t}"); }
        }
        rep["volume_descriptors_patched"] = vds;
        rep["carindb_units_before"] = baseUnit;
        rep["carindb_units_after"] = db.Length / Fmt.Unit;
        rep["iso_sectors"] = sectors;
        return (outIso, db, rep);
    }

    /// <summary>Offline checks on the grafted carindb, read back from its bytes alone.</summary>
    public static (JsonObject res, List<string> problems) Check(byte[] db, byte[] original, RoadTiles rt, List<TileSpec> specs)
    {
        var res = new JsonObject();
        var problems = new List<string>();
        void Bad(string s) { if (problems.Count < 50) problems.Add(s); }
        int n = db.Length / Fmt.Unit;

        // 1. block chain: contiguous from unit 0 to the end
        var type = new Dictionary<int, (int units, int type)>();
        int u = 0, blocks = 0;
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
        int T(uint id) => id != 0 && type.TryGetValue((int)(id >> 8), out var t) && t.units == (int)(id & 0xFF) ? t.type : -1;

        // 2. the original blocks are unchanged except the 0x07 records' BLOCK_IDs
        int changed = 0;
        for (int i = 0; i < original.Length; i++)
            if (db[i] != original[i])
            {
                int rel = i - DirectoryUnit * Fmt.Unit - 0x10;
                bool allowed = rel >= 0 && RoadRecords.Any(r => rel >= 24 * r.record && rel < 24 * r.record + 4);
                if (!allowed) { Bad($"original byte {i} changed"); break; }
                changed++;
            }
        res["original bytes changed (0x07 BLOCK_IDs only)"] = changed;

        // 3. every reference in our blocks names a block of the expected type
        long refsOk = 0;
        void Ref(uint id, int want, string what)
        {
            if (id == 0) return;
            int t = T(id);
            if (t == want) refsOk++; else Bad($"{what}: 0x{id:x8} is type {t}, want 0x{want:X2}");
        }
        var child = new Dictionary<int, int> { [3] = 0, [2] = 3, [1] = 2 };
        var parent = new Dictionary<int, int> { [0] = 3, [3] = 2, [2] = 1 };
        using var prog = new Progress("check disc", specs.Count, "tiles");
        foreach (var sp in specs)
        {
            prog.Add();
            var d = sp.Decoded;
            Ref(sp.BlockId, sp.BType, "tile");
            if (!db.AsSpan((int)(sp.BlockId >> 8) * Fmt.Unit, sp.Raw.Length).SequenceEqual(sp.Raw)) Bad($"tile 0x{sp.BlockId:x8} bytes differ");
            if (parent.TryGetValue(sp.BType, out int pt)) Ref(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(0x58)), pt, "parent");
            (int off, int cnt) Sec(int i) => (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8 + 4 * i)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10 + 4 * i)));
            var (o6, n6) = Sec(6);
            for (int k = 0; k < n6; k++) Ref(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o6 + 16 * k + 8)), sp.BType, "S6 twin");
            var (o9, n9) = Sec(9);
            for (int k = 0; k < n9; k++) Ref(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o9 + 8 * k)), sp.BType, "S9 neighbour");
            if (child.TryGetValue(sp.BType, out int ct))
            {
                var (o8, n8) = Sec(8);
                for (int k = 0; k < n8; k++) Ref(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o8 + 4 * k)), ct, "S8 child");
            }
        }
        foreach (var (lv, li) in rt.Index)
        {
            foreach (var (id, _, first, count) in li.GridBlocks)
            {
                Ref(id, 0x08, "grid");
                int o = (int)(id >> 8) * Fmt.Unit;
                for (int j = 0; j < count; j++) Ref(BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(o + 16 + 4 * j)), 0x09, "grid entry");
            }
            foreach (var node in li.Nodes)
            {
                int o = (int)(node.BlockId >> 8) * Fmt.Unit;
                int s1 = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(o + 12)), c1 = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(o + 14));
                for (int j = 0; j < c1; j++) Ref(BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(o + s1 + 4 * j)), lv, "cell tile");
            }
        }
        res["references to blocks of the right type"] = refsOk;

        // 4. from 0x07 alone: the unit's lookup finds every tile at its centre
        int o7 = DirectoryUnit * Fmt.Unit;
        byte[] Block(int unit) => db.AsSpan(unit * Fmt.Unit, type[unit].units * Fmt.Unit).ToArray();
        long found = 0, missed = 0;
        foreach (var (rec, lv) in RoadRecords)
        {
            int o = o7 + 0x10 + 24 * rec;
            uint gid = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(o));
            long x1 = U32(db, o + 4), y1 = U32(db, o + 8), side = U32(db, o + 12) - x1;
            Ref(gid, 0x08, $"0x07 record {rec}");
            foreach (var sp in rt.Levels[lv])
            {
                var (a, b, c, d) = sp.Tile.BBox;
                uint hit = Spatial.Lookup(x1, y1, side, gid, Block, ((long)a + c) / 2, ((long)b + d) / 2);
                if (hit == sp.BlockId) found++; else { missed++; Bad($"0x07 record {rec}: lookup misses 0x{sp.BlockId:x8}"); }
            }
        }
        res["tiles found from 0x07"] = found;
        res["tiles missed from 0x07"] = missed;
        return (res, problems);
    }
}
