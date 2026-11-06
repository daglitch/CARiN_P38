namespace Osm2Carin.Tests;

static class Fixtures
{
    /// <summary>track1.iso of the original disc (from Phase 0, or from the CdImage test).</summary>
    public static string? Iso()
    {
        foreach (var p in new[]
        {
            Path.Combine(Paths.Repo ?? "", "out", "phase0", "orig", "track1.iso"),
            Path.Combine(Paths.Repo ?? "", "out", "csharp-tests", "cdimage", "track1.iso"),
        })
            if (File.Exists(p)) return p;
        return null;
    }
}
