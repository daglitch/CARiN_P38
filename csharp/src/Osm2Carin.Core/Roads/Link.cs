using System.Buffers.Binary;
using Osm2Carin.Core.Cf1;

namespace Osm2Carin.Core.Roads;

/// <summary>Block IDs and cross references for road tiles: port of tools/osm2carin/link.py.</summary>
public static class Link
{
    public static readonly int[] Order = [0, 3, 2, 1];
    static readonly Dictionary<int, int> Child = new() { [3] = 0, [2] = 3, [1] = 2 };
    static readonly Dictionary<int, int> ParentLv = new() { [0] = 3, [3] = 2, [2] = 1 };

    /// <summary>Leaves sharing a stretch of edge with `cell` -> (leaf, u, v) at the shared stretch's midpoint.</summary>
    static List<(Cell leaf, int u, int v)> Neighbours(Cell root, Cell cell)
    {
        long step = Tiles.Base / 2;
        var order = new List<(Cell, char)>();
        var spans = new Dictionary<(Cell, char), (Cell leaf, char side)>();
        var sides = new List<(char, List<(long, long)>)>();
        var L = new List<(long, long)>(); var R = new List<(long, long)>(); var B = new List<(long, long)>(); var T = new List<(long, long)>();
        for (long y = cell.Y1 + step / 2; y < cell.Y2; y += step) { L.Add((cell.X1 - 1, y)); R.Add((cell.X2, y)); }
        for (long x = cell.X1 + step / 2; x < cell.X2; x += step) { B.Add((x, cell.Y1 - 1)); T.Add((x, cell.Y2)); }
        sides.Add(('L', L)); sides.Add(('R', R)); sides.Add(('B', B)); sides.Add(('T', T));
        foreach (var (side, probes) in sides)
            foreach (var (x, y) in probes)
            {
                if (!root.Contains(x, y)) continue;
                var leaf = Tiles.FindLeaf(root, x, y);
                var k = (leaf, side);
                if (!spans.ContainsKey(k)) { spans[k] = (leaf, side); order.Add(k); }
            }
        var o = new List<(Cell, int, int)>();
        foreach (var k in order)
        {
            var (leaf, side) = spans[k];
            int u, v;
            if (side is 'L' or 'R')
            {
                long a = Math.Max(cell.Y1, leaf.Y1), b = Math.Min(cell.Y2, leaf.Y2);
                u = side == 'L' ? 0 : (int)(cell.W / Tiles.Grid);
                v = (int)Geo.FloorDiv(Geo.FloorDiv(a + b, 2) - cell.Y1, Tiles.Grid);
            }
            else
            {
                long a = Math.Max(cell.X1, leaf.X1), b = Math.Min(cell.X2, leaf.X2);
                v = side == 'B' ? 0 : (int)(cell.H / Tiles.Grid);
                u = (int)Geo.FloorDiv(Geo.FloorDiv(a + b, 2) - cell.X1, Tiles.Grid);
            }
            o.Add((leaf, u, v));
        }
        return o.OrderBy(e => (e.Item2, e.Item3)).ToList();
    }

    static (long s, List<Cell> leaves) Grid(Cell cell, Cell childRoot, List<Cell> childCells)
    {
        var inside = childCells.Where(c => c.X1 >= cell.X1 && c.X2 <= cell.X2 && c.Y1 >= cell.Y1 && c.Y2 <= cell.Y2).ToList();
        long s = inside.Count > 0 ? inside.Min(c => Math.Min(c.W, c.H)) : Math.Min(cell.W, cell.H);
        s = Math.Min(s, Math.Min(cell.W, cell.H));
        while ((cell.W / s) * (cell.H / s) > Tiles.S8Max) s *= 2;
        long gw = cell.W / s, gh = cell.H / s;
        var leaves = new List<Cell>();
        for (long cx = 0; cx < gw; cx++)
            for (long cy = 0; cy < gh; cy++)
                leaves.Add(Tiles.FindLeaf(childRoot, cell.X1 + cx * s + s / 2, cell.Y1 + cy * s + s / 2));
        return (s, leaves);
    }

    public static void Prepare(RoadTiles rt)
    {
        var byLeaf = new Dictionary<(int, Cell), TileSpec>();
        foreach (int lv in Order)
        {
            rt.Levels[lv] = rt.Levels[lv].Where(sp => sp.Tile.Secs[4].Count > 1).ToList();
            foreach (var sp in rt.Levels[lv]) byLeaf[(lv, sp.Cell)] = sp;
        }
        foreach (int lv in Order)
        {
            var root = rt.Roots[lv];
            Parallel.ForEach(rt.Levels[lv], sp =>
            {
                sp.S9 = Neighbours(root, sp.Cell)
                    .Select(e => (byLeaf.GetValueOrDefault((lv, e.leaf)), e.u, e.v))
                    .Where(e => e.Item1 != null).Select(e => (e.Item1!, e.u, e.v)).ToList();
                sp.Tile.Secs[9].Clear();
                foreach (var _ in sp.S9) sp.Tile.Secs[9].Add(new Rec(8));
                if (Child.TryGetValue(lv, out int c))
                {
                    var (s, leaves) = Grid(sp.Cell, rt.Roots[c], rt.Cells[c]);
                    sp.S8Cell = s;
                    sp.S8 = leaves.Select(l => byLeaf.GetValueOrDefault((c, l))).ToList();
                    sp.Tile.Secs[8].Clear();
                    foreach (var _ in sp.S8) sp.Tile.Secs[8].Add(new Rec(4));
                }
                else { sp.S8 = new(); sp.S8Cell = 0; }
                if (ParentLv.TryGetValue(lv, out int p))
                {
                    long cx = (sp.Cell.X1 + sp.Cell.X2) / 2, cy = (sp.Cell.Y1 + sp.Cell.Y2) / 2;
                    sp.Parent = rt.Roots[p].Contains(cx, cy) ? byLeaf.GetValueOrDefault((p, Tiles.FindLeaf(rt.Roots[p], cx, cy))) : null;
                }
            });
        }
        foreach (int lv in Order)
        {
            var where = new Dictionary<int, (TileSpec, int)>();
            foreach (var sp in rt.Levels[lv])
                for (int k = 0; k < sp.EdgeIds.Count; k++) where[sp.EdgeIds[k]] = (sp, k);
            foreach (var sp in rt.Levels[lv])
                sp.Twins = sp.EdgeTwins.Select(et => where.TryGetValue(et.twin, out var w) ? ((TileSpec, int)?)w : null).ToList();
        }
    }

    static void Fill(TileSpec sp)
    {
        var t = sp.Tile;
        t.BlockId = sp.BlockId;
        var hw = (byte[])t.HdrWords.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(hw.AsSpan(4), sp.Parent?.BlockId ?? 0);
        if (sp.BType != 0) BinaryPrimitives.WriteUInt32BigEndian(hw.AsSpan(8), (uint)sp.S8Cell);
        t.HdrWords = hw;
        for (int i = 0; i < sp.S9.Count; i++)
        {
            var raw = t.Secs[9][i].Raw;
            BinaryPrimitives.WriteUInt32BigEndian(raw, sp.S9[i].nb.BlockId);
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(4), (ushort)sp.S9[i].u);
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(6), (ushort)sp.S9[i].v);
        }
        for (int i = 0; i < sp.S8.Count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(t.Secs[8][i].Raw, sp.S8[i]?.BlockId ?? 0);
        for (int i = 0; i < sp.Twins.Count; i++)
        {
            var raw = t.Secs[6][i].Raw;
            if (sp.Twins[i] is not var (nb, k)) { Array.Clear(raw, 8, 6); continue; }
            var (start, _) = nb.Tile.Layout();
            BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(8), nb.BlockId);
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(12), (ushort)(start[6] + 16 * k));
        }
    }

    static (byte[] raw, bool packed, byte[] plain) Encode(TileSpec sp, bool cf1)
    {
        var t = sp.Tile;
        BinaryPrimitives.WriteUInt32BigEndian(t.Head, sp.BlockId);
        t.Head[4] = (byte)(sp.BType >> 8); t.Head[5] = (byte)sp.BType;
        t.Head[6] = t.Head[7] = 0;
        int units = (t.DecodedSize() + Fmt.Unit - 1) / Fmt.Unit;
        var plain = t.Build(Fmt.Unit, units);
        int used = plain.Length;
        while (used > 0 && plain[used - 1] == 0) used--;
        var plainRaw = used > 0 ? plain[..used] : plain[..8];
        if (sp.BType != 0 || !cf1) return (plainRaw, false, plain);
        var packed = Encoder00.Encode(plain, Fmt.RecordSizeTable, Fmt.DbRel, Fmt.SubRel, Fmt.Unit);
        return packed.Length < used ? (packed, true, plain) : (plain[..used], false, plain);
    }

    /// <summary>Allocate block IDs from baseUnit and finalise every tile (link.py link).</summary>
    public static List<TileSpec> Run(RoadTiles rt, int baseUnit = 0, bool cf1 = true, Action<string>? log = null, int maxRounds = 6)
    {
        log ??= Console.WriteLine;
        Prepare(rt);
        var specs = Order.SelectMany(lv => rt.Levels[lv]).ToList();
        foreach (var sp in specs) sp.BlockId = 0x00FFFF01;
        foreach (var sp in specs) Fill(sp);
        var units = new int[specs.Count];
        Parallel.For(0, specs.Count, i => units[i] = Math.Max(1, (Encode(specs[i], cf1).raw.Length + Fmt.Unit - 1) / Fmt.Unit));
        for (int rnd = 0; rnd < maxRounds; rnd++)
        {
            int pos = baseUnit;
            for (int i = 0; i < specs.Count; i++)
            {
                if (units[i] > 255) throw new InvalidOperationException("tile larger than 255 units");
                specs[i].BlockId = (uint)((pos << 8) | units[i]);
                pos += units[i];
            }
            foreach (var sp in specs) Fill(sp);
            int grown = 0;
            Parallel.For(0, specs.Count, i =>
            {
                var sp = specs[i];
                var (raw, packed, plain) = Encode(sp, cf1);
                int n = Math.Max(1, (raw.Length + Fmt.Unit - 1) / Fmt.Unit);
                if (n > units[i]) { units[i] = n; Interlocked.Increment(ref grown); }
                sp.Cf1 = packed; sp.Decoded = plain;
                var o = new byte[units[i] * Fmt.Unit];
                raw.CopyTo(o, 0);
                sp.Raw = o;
            });
            log($"link round {rnd + 1}: {specs.Count:N0} tiles, {pos - baseUnit:N0} units, {grown} grew");
            if (grown == 0) { rt.EndUnit = pos; return specs; }
        }
        throw new InvalidOperationException("block allocation did not settle");
    }
}
