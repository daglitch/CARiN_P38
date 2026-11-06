using System.Buffers.Binary;

namespace Osm2Carin.Core.Roads;

/// <summary>The spatial index that leads the unit from a position to the road tile under it
/// (Phase 3): 0x07 layer directory -> 0x08 grid -> 0x09 cell node -> tile. Layout as on the
/// original disc (CD-ID 5944) and in OpenCarin docs/carindb/02-geo.md §7.3:
///   0x08: header, descriptor {0x0010, count} at +0x08, u32 cell side at +0x0C, then `count` u32
///         entries; one layer's N x N entries (column-major, x then y) run over consecutive 0x08
///         blocks of at most 4,092 entries (8 units); an entry is 0 or a 0x09 BLOCK_ID;
///   0x09: descriptors S0 {0x14, q²} and S1 {(0x14 + 2q² + 3) &amp; ~3, n}, u32 item side s at +0x10,
///         S0 = q² u16 item pointers (column-major; 0 or the offset of an S1 entry), S1 = n u32
///         tile BLOCK_IDs in the order first met in S0. s is the gcd of the clipped tile edges, so
///         every tile edge in the cell lies on an item boundary.
/// Block IDs are not known when the index is laid out; Build returns the blocks with their sizes,
/// and Fill writes the IDs once the tiles and index blocks have them.</summary>
public static class Spatial
{
    public const int MaxEntriesPerBlock = 4092;

    /// <summary>Grid cell side per road layer, as on the original disc (512² cells for the street
    /// layer over its root square, 128² / 64² / 32² for 0x03 / 0x02 / 0x01).</summary>
    public static readonly Dictionary<int, long> CellSide = new() { [0] = 1310720, [3] = 5242880, [2] = 10485760, [1] = 20971520 };

    public sealed class CellNode
    {
        public long Cx, Cy;                   // cell index
        public long ItemSide;
        public int Q;
        public int[] ItemTile = [];           // per item (column-major): index into Tiles, -1 = empty
        public List<TileSpec> Tiles = new();  // S1 order
        public uint BlockId;
        public int Units;

        public int Size
        {
            get
            {
                int s1 = (0x14 + 2 * Q * Q + 3) & ~3;
                return s1 + 4 * Tiles.Count;
            }
        }
    }

    public sealed class LayerIndex
    {
        public int Layer;
        public long X0, Y0, Side, Cell;
        public int N;
        public CellNode?[] Grid = [];          // N*N, column-major
        public List<CellNode> Nodes = new();   // physical order
        public List<(uint blockId, int units, int first, int count)> GridBlocks = new();

        public int GridUnits => GridBlocks.Sum(b => b.units);
        public int NodeUnits => Nodes.Sum(n => n.Units);
    }

    static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }

    /// <summary>Lay out one layer's index over the root square (x0, y0, side) for the given tiles.</summary>
    public static LayerIndex Build(int layer, List<TileSpec> tiles, long x0, long y0, long side)
    {
        // The disc's cell side, halved until no cell needs more than MaxQ x MaxQ items (the disc's
        // largest 0x09 has 64 x 64; our coarse tiles are unions of street tiles, so their edges can
        // be finer than the original's and need smaller cells to keep the items few).
        for (long cell = Math.Min(CellSide[layer], side); ; cell /= 2)
        {
            var li = BuildWith(layer, tiles, x0, y0, side, cell);
            if (li.Nodes.All(n => n.Q <= MaxQ) || cell <= CellSide[0]) return li;
        }
    }

    public const int MaxQ = 64;

    static LayerIndex BuildWith(int layer, List<TileSpec> tiles, long x0, long y0, long side, long cell)
    {
        int n = (int)(side / cell);
        var li = new LayerIndex { Layer = layer, X0 = x0, Y0 = y0, Side = side, Cell = cell, N = n, Grid = new CellNode?[n * n] };
        // tiles per cell
        var perCell = new Dictionary<long, List<TileSpec>>();
        foreach (var sp in tiles)
        {
            var (tx1, ty1, tx2, ty2) = sp.Tile.BBox;
            long cx0 = Geo.FloorDiv(tx1 - x0, cell), cx1 = Geo.FloorDiv(tx2 - 1 - x0, cell);
            long cy0 = Geo.FloorDiv(ty1 - y0, cell), cy1 = Geo.FloorDiv(ty2 - 1 - y0, cell);
            for (long cx = Math.Max(cx0, 0); cx <= Math.Min(cx1, n - 1); cx++)
                for (long cy = Math.Max(cy0, 0); cy <= Math.Min(cy1, n - 1); cy++)
                {
                    long k = cx * n + cy;
                    if (!perCell.TryGetValue(k, out var l)) perCell[k] = l = new();
                    l.Add(sp);
                }
        }
        foreach (var k in perCell.Keys.Order())
        {
            long cx = k / n, cy = k % n;
            long ccx = x0 + cx * cell, ccy = y0 + cy * cell;
            long g = cell;
            var clips = new List<(TileSpec sp, long a, long b, long c, long d)>();
            foreach (var sp in perCell[k])
            {
                var (tx1, ty1, tx2, ty2) = sp.Tile.BBox;
                long a = Math.Max(tx1, ccx) - ccx, b = Math.Max(ty1, ccy) - ccy;
                long c = Math.Min(tx2, ccx + cell) - ccx, d = Math.Min(ty2, ccy + cell) - ccy;
                clips.Add((sp, a, b, c, d));
                foreach (long v in (long[])[a, b, c, d]) g = Gcd(g, v);
            }
            int q = (int)(cell / g);
            var node = new CellNode { Cx = cx, Cy = cy, ItemSide = g, Q = q, ItemTile = new int[q * q] };
            var index = new Dictionary<TileSpec, int>(ReferenceEqualityComparer.Instance);
            for (int ix = 0; ix < q; ix++)
                for (int iy = 0; iy < q; iy++)
                {
                    long px = ix * g + g / 2, py = iy * g + g / 2;
                    int hit = -1;
                    foreach (var (sp, a, b, c, d) in clips)
                        if (a <= px && px < c && b <= py && py < d)
                        {
                            if (!index.TryGetValue(sp, out hit)) { hit = node.Tiles.Count; index[sp] = hit; node.Tiles.Add(sp); }
                            break;
                        }
                    node.ItemTile[ix * q + iy] = hit;
                }
            node.Units = (node.Size + Fmt.Unit - 1) / Fmt.Unit;
            li.Grid[k] = node;
            li.Nodes.Add(node);
        }
        for (int first = 0; first < n * n; first += MaxEntriesPerBlock)
        {
            int count = Math.Min(MaxEntriesPerBlock, n * n - first);
            li.GridBlocks.Add((0, (16 + 4 * count + Fmt.Unit - 1) / Fmt.Unit, first, count));
        }
        return li;
    }

    /// <summary>Give the layer's 0x08 and 0x09 blocks IDs from `pos` on (0x08 run, then the 0x09
    /// nodes). Returns the next free unit.</summary>
    public static int Allocate(LayerIndex li, int pos)
    {
        for (int i = 0; i < li.GridBlocks.Count; i++)
        {
            var b = li.GridBlocks[i];
            li.GridBlocks[i] = ((uint)((pos << 8) | b.units), b.units, b.first, b.count);
            pos += b.units;
        }
        foreach (var node in li.Nodes)
        {
            node.BlockId = (uint)((pos << 8) | node.Units);
            pos += node.Units;
        }
        return pos;
    }

    /// <summary>The layer's blocks as raw bytes (IDs filled), in physical order.</summary>
    public static List<byte[]> Write(LayerIndex li)
    {
        var o = new List<byte[]>();
        foreach (var (id, units, first, count) in li.GridBlocks)
        {
            var b = new byte[units * Fmt.Unit];
            BinaryPrimitives.WriteUInt32BigEndian(b, id);
            b[5] = 0x08;
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), 0x10);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), (ushort)count);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), (uint)li.Cell);
            for (int j = 0; j < count; j++)
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16 + 4 * j), li.Grid[first + j]?.BlockId ?? 0);
            o.Add(b);
        }
        foreach (var node in li.Nodes)
        {
            var b = new byte[node.Units * Fmt.Unit];
            BinaryPrimitives.WriteUInt32BigEndian(b, node.BlockId);
            b[5] = 0x09;
            int q2 = node.Q * node.Q, s1 = (0x14 + 2 * q2 + 3) & ~3;
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), 0x14);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), (ushort)q2);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), (ushort)s1);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(14), (ushort)node.Tiles.Count);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)node.ItemSide);
            for (int i = 0; i < q2; i++)
                if (node.ItemTile[i] >= 0)
                    BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0x14 + 2 * i), (ushort)(s1 + 4 * node.ItemTile[i]));
            for (int t = 0; t < node.Tiles.Count; t++)
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(s1 + 4 * t), node.Tiles[t].BlockId);
            o.Add(b);
        }
        return o;
    }

    /// <summary>The lookup the unit does (OpenCarin carin.parser.spatial): the tile BLOCK_ID at (x, y), or 0.
    /// `block` returns the bytes of the block starting at a unit.</summary>
    public static uint Lookup(long x0, long y0, long side, uint firstGridId, Func<int, byte[]> block, long x, long y)
    {
        int unit = (int)(firstGridId >> 8);
        var g0 = block(unit);
        long cell = BinaryPrimitives.ReadUInt32BigEndian(g0.AsSpan(12));
        long n = side / cell;
        long cx = Geo.FloorDiv(x - x0, cell), cy = Geo.FloorDiv(y - y0, cell);
        if (cx < 0 || cy < 0 || cx >= n || cy >= n) return 0;
        long k = cx * n + cy;
        while (true)
        {
            var g = block(unit);
            int count = BinaryPrimitives.ReadUInt16BigEndian(g.AsSpan(10));
            if (k < count)
            {
                uint e = BinaryPrimitives.ReadUInt32BigEndian(g.AsSpan(16 + 4 * (int)k));
                if (e == 0) return 0;
                var nb = block((int)(e >> 8));
                long s = BinaryPrimitives.ReadUInt32BigEndian(nb.AsSpan(16));
                long q = cell / s;
                long ix = Geo.FloorDiv(x - (x0 + cx * cell), s), iy = Geo.FloorDiv(y - (y0 + cy * cell), s);
                int p = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(0x14 + 2 * (int)(ix * q + iy)));
                return p == 0 ? 0 : BinaryPrimitives.ReadUInt32BigEndian(nb.AsSpan(p));
            }
            k -= count;
            unit += g.Length / Fmt.Unit;          // the run continues in the next physical block
        }
    }
}
