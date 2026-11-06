namespace Osm2Carin.Tests;

/// <summary>Where the fixtures are: the original disc image (git-ignored) and OpenCarin.
/// CARIN_BIN / CARIN_REPO override; otherwise the first parent folder holding the .bin.</summary>
static class Paths
{
    public const string BinName = "P38_Navigation_CD_2002_California_Nevada.bin";

    public static string? Bin
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CARIN_BIN");
            if (env != null && File.Exists(env)) return env;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, BinName);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    public static string? Repo
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CARIN_REPO");
            if (env != null) return env;
            var b = Bin;
            return b == null ? null : Path.GetDirectoryName(b);
        }
    }

    /// <summary>A scratch folder under out/ (git-ignored) next to the disc image.</summary>
    public static string Scratch(string name)
    {
        var d = Path.Combine(Repo ?? Path.GetTempPath(), "out", "csharp-tests", name);
        Directory.CreateDirectory(d);
        return d;
    }
}
