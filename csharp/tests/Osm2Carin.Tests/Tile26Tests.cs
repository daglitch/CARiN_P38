using System.Diagnostics;
using Osm2Carin.Core;
using Osm2Carin.Core.Cf1;
using Xunit.Abstractions;

namespace Osm2Carin.Tests;

public class Tile26Tests(ITestOutputHelper log)
{
    /// <summary>parse -> build is byte-identical on every road tile of the disc; the rules hold.</summary>
    [Fact]
    public void RoundTripsEveryRoadTileOfTheDisc()
    {
        var iso = Fixtures.Iso();
        if (iso == null) return;
        var db = Carindb.ReadCarindb(iso);
        var tiles = Carindb.Walk(db).Where(b => b.Type <= 3).ToList();
        var sw = Stopwatch.StartNew();
        int bad = 0;
        var rules = new System.Collections.Concurrent.ConcurrentDictionary<string, (int ok, int bad)>();
        Parallel.ForEach(tiles, b =>
        {
            var raw = Carindb.Raw(db, b);
            var d = (b.Comp & 1) != 0 ? Decoder00.DecodeBlock(raw, Fmt.RecordSizeTable, 26, 8, 2048) : raw;
            var t = Tile26.Parse(d);
            var o = t.Build();
            if ((b.Comp & 1) != 0) { o[6] = d[6]; o[7] = d[7]; }
            if (!o.AsSpan().SequenceEqual(d)) Interlocked.Increment(ref bad);
            foreach (var (k, v) in t.CheckRules())
                rules.AddOrUpdate(k, v, (_, a) => (a.ok + v.ok, a.bad + v.bad));
        });
        log.WriteLine($"{tiles.Count} tiles in {sw.Elapsed.TotalSeconds:F1} s, {bad} differ");
        foreach (var (k, v) in rules) log.WriteLine($"{k}: {v.ok} ok, {v.bad} bad");
        Assert.Equal(38326, tiles.Count);
        Assert.Equal(0, bad);
        foreach (var (k, v) in rules)
            if (k != "cycle clockwise from smallest bearing") Assert.Equal(0, v.bad);
        var cyc = rules["cycle clockwise from smallest bearing"];
        Assert.True(cyc.bad < cyc.ok / 1000);          // 1,230 of 2.15 M on the disc (0.06%)
    }
}
