using System.Buffers.Binary;
using Osm2Carin.Core;
using Osm2Carin.Core.Roads;
using Xunit.Abstractions;

namespace Osm2Carin.Tests;

public class SpatialTests(ITestOutputHelper log)
{
    /// <summary>Spatial.Build, given the disc's own street tiles, reproduces the disc's 0x09 cell nodes
    /// (item side, items, tile order), and Lookup finds every street tile at its centre.</summary>
    [Fact]
    public void ReproducesTheDiscsStreetIndex()
    {
        var iso = Fixtures.Iso();
        if (iso == null) return;
        var db = Carindb.ReadCarindb(iso);
        var blocks = Carindb.Walk(db).ToDictionary(b => b.Unit);
        byte[] Blk(int unit) => Carindb.Raw(db, blocks[unit]);
        var (x0, y0, side) = Tiles.DiscRoot;
        var specs = new List<TileSpec>();
        foreach (var b in blocks.Values.Where(b => b.Type == 0))
        {
            var raw = Blk(b.Unit);
            var t = new Tile26(0) { BBox = (BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(0x44)), BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(0x48)),
                                            BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(0x4C)), BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(0x50))) };
            specs.Add(new TileSpec(0, new Cell(0, 0, 1, 1), t) { BlockId = BinaryPrimitives.ReadUInt32BigEndian(raw) });
        }
        var li = Spatial.Build(0, specs, x0, y0, side);
        // the disc's grid for layer 0 starts at unit 49196
        uint grid = (uint)(49196 << 8 | blocks[49196].Units);
        int same = 0, diff = 0;
        foreach (var node in li.Nodes)
        {
            long k = node.Cx * li.N + node.Cy;
            int unit = 49196;
            while (k >= BinaryPrimitives.ReadUInt16BigEndian(Blk(unit).AsSpan(10))) { k -= BinaryPrimitives.ReadUInt16BigEndian(Blk(unit).AsSpan(10)); unit += blocks[unit].Units; }
            uint e = BinaryPrimitives.ReadUInt32BigEndian(Blk(unit).AsSpan(16 + 4 * (int)k));
            var disc = Blk((int)(e >> 8));
            node.BlockId = e;
            var mine = Spatial.Write(new Spatial.LayerIndex { Nodes = [node], Grid = [], GridBlocks = [] })[0];
            int len = node.Size;
            if (mine.AsSpan(0, len).SequenceEqual(disc.AsSpan(0, len))) same++; else diff++;
        }
        log.WriteLine($"{li.Nodes.Count} cell nodes: {same} identical to the disc, {diff} differ");
        Assert.Equal(0, diff);
        int found = 0;
        foreach (var sp in specs)
        {
            var (a, b, c, d) = sp.Tile.BBox;
            if (Spatial.Lookup(x0, y0, side, grid, Blk, ((long)a + c) / 2, ((long)b + d) / 2) == sp.BlockId) found++;
        }
        log.WriteLine($"lookup finds {found} of {specs.Count} street tiles");
        Assert.Equal(specs.Count, found);
    }
}
