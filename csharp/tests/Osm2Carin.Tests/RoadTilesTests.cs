using System.Diagnostics;
using Osm2Carin.Core.Osm;
using Osm2Carin.Core.Roads;
using Xunit.Abstractions;

namespace Osm2Carin.Tests;

/// <summary>C# road tiles against Python's (scratch script pyroads.py writes out/parity/&lt;name&gt;-py.roads
/// and .decoded from the Python model): byte-identical on-disc blocks and decoded tiles.</summary>
public class RoadTilesTests(ITestOutputHelper log)
{
    static string Parity(string f) => Path.Combine(Paths.Repo ?? "", "out", "parity", f);

    void Compare(string name, string label)
    {
        if (!File.Exists(Parity(name + "-py.roads"))) return;
        var sw = Stopwatch.StartNew();
        var m = RoadModel.Load(Parity(name + "-py"));
        var rt = Tiles.Build(m, m.Strings, label, s => log.WriteLine(s));
        var specs = Link.Run(rt, 0, true, s => log.WriteLine(s));
        log.WriteLine($"C# tiles + link in {sw.Elapsed.TotalSeconds:F1} s");
        var raw = specs.SelectMany(s => s.Raw).ToArray();
        var dec = specs.SelectMany(s => s.Decoded).ToArray();
        var pyRaw = File.ReadAllBytes(Parity(name + "-py.roads"));
        var pyDec = File.ReadAllBytes(Parity(name + "-py.decoded"));
        log.WriteLine($"{specs.Count} tiles; raw {raw.Length} vs {pyRaw.Length}; decoded {dec.Length} vs {pyDec.Length}");
        int first = -1;
        for (int i = 0; i < Math.Min(dec.Length, pyDec.Length); i++) if (dec[i] != pyDec[i]) { first = i; break; }
        if (first >= 0) log.WriteLine($"decoded differs first at byte {first}");
        int pos = 0, nDiff = 0, shown = 0;
        foreach (var sp in specs)
        {
            int n = sp.Decoded.Length;
            var mine = dec[pos..(pos + n)]; var theirs = pyDec[pos..(pos + n)];
            if (!mine.AsSpan().SequenceEqual(theirs))
            {
                nDiff++;
                if (shown++ < 5)
                {
                    var t = Osm2Carin.Core.Tile26.Parse(sp.Decoded);
                    var (start, blob) = t.Layout();
                    var diffs = Enumerable.Range(0, n).Where(i => mine[i] != theirs[i]).ToList();
                    string Where(int o) { var s = start.Where(kv => kv.Value <= o).OrderBy(kv => kv.Value).LastOrDefault(); return o >= blob ? $"blob+{o - blob}" : $"S{s.Key}+{o - s.Value}"; }
                    log.WriteLine($"tile 0x{sp.BlockId:x} type {sp.BType}: {diffs.Count} bytes differ, e.g. " +
                        string.Join("; ", diffs.Take(8).Select(o => $"{Where(o)} {mine[o]:x2}/{theirs[o]:x2}")));
                }
            }
            pos += n;
        }
        log.WriteLine($"{nDiff} tiles differ");
        Assert.Equal(pyDec.Length, dec.Length);
        Assert.True(first < 0);
        Assert.Equal(pyRaw, raw);
        var (res, problems) = Validate.Check(specs);
        foreach (var kv in res) log.WriteLine($"{kv.Key}: {kv.Value}");
        Assert.False(Validate.HasProblems(res, problems), problems.ToJsonString());
    }

    [Fact] public void MonacoMatchesPython() => Compare("monaco", "monaco");
    [Fact] public void NevadaMatchesPython() => Compare("us_nevada", "Nevada");
}
