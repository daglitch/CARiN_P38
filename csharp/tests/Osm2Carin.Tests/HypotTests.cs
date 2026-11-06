using Osm2Carin.Core.Roads;

namespace Osm2Carin.Tests;

public class HypotTests
{
    /// <summary>TileGeom.Hypot equals CPython's math.hypot bit for bit (fixture written by Python).</summary>
    [Fact]
    public void MatchesCPython()
    {
        var p = Path.Combine(Paths.Repo ?? "", "out", "parity", "hypot.bin");
        if (!File.Exists(p)) return;
        var b = File.ReadAllBytes(p);
        int bad = 0;
        for (int o = 0; o < b.Length; o += 24)
        {
            double x = BitConverter.ToDouble(b, o), y = BitConverter.ToDouble(b, o + 8), h = BitConverter.ToDouble(b, o + 16);
            if (TileGeom.Hypot(x, y) != h) bad++;
        }
        Assert.Equal(0, bad);
    }
}
