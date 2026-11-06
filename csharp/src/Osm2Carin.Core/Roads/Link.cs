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

    /// <summary>Allocate block IDs from baseUnit and finalise every tile (link.py link).
    /// Each street tile is CF=1-encoded once, with provisional IDs: block IDs only occur in fixed-width
    /// places of the encoding, so after allocation the head is rebuilt from the final tile and the S6
    /// neighbour IDs are patched into the bit stream (Encoder00.Parts). The result equals a fresh
    /// encoding of the final tile (the parity test against the Python tools checks this).</summary>
    public static List<TileSpec> Run(RoadTiles rt, int baseUnit = 0, bool cf1 = true, Action<string>? log = null, int maxRounds = 6,
                                     bool withIndex = false)
    {
        log ??= Console.WriteLine;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Prepare(rt);
        log($"link: prepared in {sw.Elapsed.TotalSeconds:F1} s");
        var specs = Order.SelectMany(lv => rt.Levels[lv]).ToList();
        if (withIndex)
        {
            // one root square for every layer: the disc's own when the region lies inside it
            var r = rt.Roots[0];
            var (dx, dy, ds) = Tiles.DiscRoot;
            rt.IndexRoot = r.X1 >= dx && r.Y1 >= dy && r.X2 <= dx + ds && r.Y2 <= dy + ds ? Tiles.DiscRoot : (r.X1, r.Y1, r.W);
            foreach (int lv in Order)
                rt.Index[lv] = Spatial.Build(lv, rt.Levels[lv], rt.IndexRoot.x, rt.IndexRoot.y, rt.IndexRoot.side);
            log($"link: spatial index laid out ({string.Join(", ", Order.Select(lv => $"0x0{lv}: {rt.Index[lv].GridBlocks.Count} grid + {rt.Index[lv].Nodes.Count} cell blocks"))})");
        }
        var layerStart = new Dictionary<int, int>();
        { int i0 = 0; foreach (int lv in Order) { layerStart[lv] = i0; i0 += rt.Levels[lv].Count; } }
        foreach (var sp in specs) sp.BlockId = 0x00FFFF01;
        foreach (var sp in specs) Fill(sp);
        var units = new int[specs.Count];
        var parts = new Encoder00.Parts?[specs.Count];
        var widths = new (byte, byte)[specs.Count];
        Parallel.For(0, specs.Count, i =>
        {
            var sp = specs[i];
            var plain = PlainOf(sp, out int used);
            int len = used;
            if (sp.BType == 0 && cf1)
            {
                widths[i] = ChooseWidths(plain);
                (plain[6], plain[7]) = widths[i];          // the widths live only in the CF=1 form
                var p = Encoder00.EncodeParts(plain, Fmt.RecordSizeTable, Fmt.DbRel, Fmt.SubRel, Fmt.Unit);
                if (p.Head.Length + p.Bits.Length < used) { parts[i] = p; len = p.Head.Length + p.Bits.Length; }
            }
            units[i] = Math.Max(1, (Math.Max(len, 8) + Fmt.Unit - 1) / Fmt.Unit);
        });
        log($"link: encoded in {sw.Elapsed.TotalSeconds:F1} s");
        for (int rnd = 0; rnd < maxRounds; rnd++)
        {
            int pos = baseUnit;
            for (int i = 0; i < specs.Count; i++)
            {
                if (withIndex)
                    foreach (int lv in Order)
                        if (layerStart[lv] == i && rt.Levels[lv].Count > 0) pos = Spatial.Allocate(rt.Index[lv], pos);
                if (units[i] > 255) throw new InvalidOperationException("tile larger than 255 units");
                specs[i].BlockId = (uint)((pos << 8) | units[i]);
                pos += units[i];
            }
            foreach (var sp in specs) Fill(sp);
            int grown = 0;
            Parallel.For(0, specs.Count, i =>
            {
                var sp = specs[i];
                var plain = PlainOf(sp, out int used);
                byte[] raw;
                if (parts[i] is Encoder00.Parts p)
                {
                    (plain[6], plain[7]) = widths[i];
                    var head = Encoder00.Head(plain, Fmt.RecordSizeTable, Fmt.Unit);
                    raw = [.. head, .. Encoder00.PatchIds(p, plain, Fmt.RecordSizeTable)];
                    if (head.Length != p.Head.Length)            // cannot happen: re-encode to be safe
                        raw = Encoder00.Encode(plain, Fmt.RecordSizeTable, Fmt.DbRel, Fmt.SubRel, Fmt.Unit);
                    sp.Cf1 = true;
                }
                else { raw = used > 0 ? plain[..used] : plain[..8]; sp.Cf1 = false; }
                int n = Math.Max(1, (raw.Length + Fmt.Unit - 1) / Fmt.Unit);
                if (n > units[i]) { units[i] = n; Interlocked.Increment(ref grown); }
                sp.Decoded = plain;
                var o = new byte[units[i] * Fmt.Unit];
                raw.CopyTo(o, 0);
                sp.Raw = o;
            });
            log($"link round {rnd + 1}: {specs.Count:N0} tiles, {pos - baseUnit:N0} units, {grown} grew  [{sw.Elapsed.TotalSeconds:F1} s]");
            if (grown == 0)
            {
                rt.EndUnit = pos;
                rt.Blocks = new List<byte[]>();
                foreach (int lv in Order)
                {
                    if (withIndex && rt.Levels[lv].Count > 0) rt.Blocks.AddRange(Spatial.Write(rt.Index[lv]));
                    rt.Blocks.AddRange(rt.Levels[lv].Select(sp => sp.Raw));
                }
                return specs;
            }
        }
        throw new InvalidOperationException("block allocation did not settle");
    }

    /// <summary>The two CF=1 bit widths (decoded bytes 6/7) that make the encoding smallest (link.py
    /// choose_widths): exact encoder bit cost for widths 0..16, smallest width on ties.</summary>
    public static (byte w0, byte w1) ChooseWidths(byte[] d)
    {
        (int off, int n) Sec(int i) => (BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8 + 4 * i)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(10 + 4 * i)));
        var (o4, n4) = Sec(4);
        var lengths = new int[n4];
        for (int k = 0; k < n4; k++) lengths[k] = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o4 + 30 * k + 0x0C));
        var deltas = new List<(int up, int down)>();
        foreach (var (i, rec) in ((int, int)[])[(5, 8), (6, 16), (7, 6)])
        {
            var (off, n) = Sec(i);
            for (int k = 1; k < n; k++)
                foreach (int o in (int[])[0, 2])
                {
                    int v = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(off + rec * k + o));
                    int pv = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(off + rec * (k - 1) + o));
                    deltas.Add(((v - pv) & 0xFFFF, (pv - v) & 0xFFFF));
                }
        }
        int best0 = 0, best1 = 0;
        long c0 = long.MaxValue, c1 = long.MaxValue;
        for (int w = 0; w <= 16; w++)
        {
            long lb = 0, db = 0;
            foreach (int v in lengths) lb += 1 + ((v >> w) != 0 ? 16 : w);
            foreach (var (up, down) in deltas) db += (up >> w) == 0 ? 1 + w : (down >> w) == 0 ? 2 + w : 18;
            if (lb < c0) { c0 = lb; best0 = w; }
            if (db < c1) { c1 = db; best1 = w; }
        }
        return ((byte)best0, (byte)best1);
    }

    static byte[] PlainOf(TileSpec sp, out int used)
    {
        var t = sp.Tile;
        BinaryPrimitives.WriteUInt32BigEndian(t.Head, sp.BlockId);
        t.Head[4] = (byte)(sp.BType >> 8); t.Head[5] = (byte)sp.BType;
        t.Head[6] = t.Head[7] = 0;
        int units = (t.DecodedSize() + Fmt.Unit - 1) / Fmt.Unit;
        var plain = t.Build(Fmt.Unit, units);
        used = plain.Length;
        while (used > 0 && plain[used - 1] == 0) used--;
        return plain;
    }
}
