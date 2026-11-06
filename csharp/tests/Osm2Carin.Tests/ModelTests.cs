using System.Diagnostics;
using Osm2Carin.Core;
using Osm2Carin.Core.Osm;
using Xunit.Abstractions;

namespace Osm2Carin.Tests;

/// <summary>The C# road model against the Python one (out/parity/&lt;region&gt;-py, written by
/// tools/osm2carin/model.py): same arrays, same strings.</summary>
public class ModelTests(ITestOutputHelper log)
{
    static string Cache(string f) => Path.Combine(Paths.Repo ?? "", "out", "cache", f);
    static string Parity(string f) => Path.Combine(Paths.Repo ?? "", "out", "parity", f);

    void Compare(string pbf, string name, int maxDiffs)
    {
        if (!File.Exists(pbf) || !File.Exists(Parity(name + "-py.npz"))) return;
        var sw = Stopwatch.StartNew();
        var m = ModelBuilder.Build(pbf, log: s => log.WriteLine(s));
        log.WriteLine($"C# model in {sw.Elapsed.TotalSeconds:F1} s");
        m.Save(Parity(name + "-cs"));
        var py = Npz.Read(Parity(name + "-py.npz"));
        var pyStrings = RoadModel.Load(Parity(name + "-py")).Strings;
        Assert.Equal(pyStrings.Count, m.Strings.Count);
        Assert.Equal(pyStrings, m.Strings);
        int total = 0;
        foreach (var (k, a) in m.Arrays)
        {
            Assert.True(py.ContainsKey(k), $"Python has no {k}");
            var b = py[k];
            Assert.Equal(b.Length, a.Length);
            Assert.Equal(b.GetType(), a.GetType());
            int diffs = 0;
            for (int i = 0; i < a.Length; i++) if (!Equals(a.GetValue(i), b.GetValue(i))) diffs++;
            if (diffs > 0) log.WriteLine($"{k}: {diffs} of {a.Length} differ");
            total += diffs;
        }
        Assert.Equal(py.Count, m.Arrays.Count);
        log.WriteLine($"{total} values differ in total");
        Assert.True(total <= maxDiffs);
    }

    [Fact] public void MonacoMatchesPython() => Compare(Cache("monaco-test.osm.pbf"), "monaco", 0);

    // floating point (numpy's vs .NET's sin/cos) may flip a rounding in a very rare case
    [Fact] public void NevadaMatchesPython() => Compare(Cache("us__nevada-latest.osm.pbf"), "us_nevada", 10);
}
