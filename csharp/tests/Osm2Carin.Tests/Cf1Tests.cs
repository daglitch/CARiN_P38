using System.Diagnostics;
using Osm2Carin.Core;
using Osm2Carin.Core.Cf1;
using Xunit.Abstractions;

namespace Osm2Carin.Tests;

public class Cf1Tests(ITestOutputHelper log)
{
    static string? Iso()
    {
        var p = Path.Combine(Paths.Repo ?? "", "out", "phase0", "orig", "track1.iso");
        if (File.Exists(p)) return p;
        p = Path.Combine(Paths.Repo ?? "", "out", "csharp-tests", "cdimage", "track1.iso");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Bit-identical to OpenCarin's Python codec on its own fixtures (tools/make_parity_fixtures.py).</summary>
    [Fact]
    public void MatchesOpenCarinPython()
    {
        var iso = Iso();
        var fix = Path.Combine(Paths.Repo ?? "", "out", "csharp-tests", "cf1");
        if (iso == null || !Directory.Exists(fix)) return;
        var db = Carindb.ReadCarindb(iso);
        var blocks = Carindb.Walk(db).ToDictionary(b => b.Unit);
        int n = 0;
        foreach (var f in Directory.GetFiles(fix, "*.dec"))
        {
            int unit = int.Parse(Path.GetFileNameWithoutExtension(f));
            var raw = Carindb.Raw(db, blocks[unit]);
            var dec = Decoder00.DecodeBlock(raw, Fmt.RecordSizeTable, 26, 8, 2048);
            Assert.Equal(File.ReadAllBytes(f), dec);
            var enc = Encoder00.Encode(dec, Fmt.RecordSizeTable, 26, 8, 2048);
            Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(f, ".enc")), enc);
            n++;
        }
        log.WriteLine($"{n} tiles identical to OpenCarin");
        Assert.True(n > 400);
    }

    /// <summary>decode -> encode -> decode on every CF=1 street tile of the disc.</summary>
    [Fact]
    public void RoundTripsEveryCompressedStreetTile()
    {
        var iso = Iso();
        if (iso == null) return;
        var db = Carindb.ReadCarindb(iso);
        var tiles = Carindb.Walk(db).Where(b => b.Type == 0 && (b.Comp & 1) != 0).ToList();
        var sw = Stopwatch.StartNew();
        int bad = 0;
        long bytesIn = 0, bytesOut = 0;
        Parallel.ForEach(tiles, b =>
        {
            var raw = Carindb.Raw(db, b);
            var d = Decoder00.DecodeBlock(raw, Fmt.RecordSizeTable, 26, 8, 2048);
            var e = Encoder00.Encode(d, Fmt.RecordSizeTable, 26, 8, 2048);
            var pad = new byte[(e.Length + 2047) / 2048 * 2048];
            e.CopyTo(pad, 0);
            var d2 = Decoder00.DecodeBlock(pad, Fmt.RecordSizeTable, 26, 8, 2048);
            if (!d.AsSpan(4).SequenceEqual(d2.AsSpan(4)) || e.Length > b.Units * 2048) Interlocked.Increment(ref bad);
            Interlocked.Add(ref bytesIn, raw.AsSpan().TrimEnd((byte)0).Length);
            Interlocked.Add(ref bytesOut, e.Length);
        });
        log.WriteLine($"{tiles.Count} tiles in {sw.Elapsed.TotalSeconds:F1} s, {bytesOut / (double)bytesIn:P1} of the original size, {bad} bad");
        Assert.Equal(24028, tiles.Count);
        Assert.Equal(0, bad);
    }
}
