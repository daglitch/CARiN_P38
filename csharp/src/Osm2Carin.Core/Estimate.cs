using System.Text.Json.Nodes;

namespace Osm2Carin.Core;

/// <summary>Output size estimate and CD-R capacity check (tools/osm2carin/estimate.py, same calibration.json).</summary>
public static class Estimate
{
    public const int Sector = 2048;
    public static readonly (string name, int sectors)[] Cdr = [("74 min", 333_000), ("80 min", 360_000)];
    public const int OverheadSectors = 366 + 150 + 150 + 300;
    public const double Safety = 1.15;

    static JsonObject? cal;
    public static JsonObject Calibration =>
        cal ??= JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "calibration.json")))!.AsObject();

    static double D(string k) => JsonX.D(Calibration[k]);

    public static double CarindbBytes(double links, double shape, double pois, bool withPois = true) =>
        links * D("bytes_per_link") + shape * D("bytes_per_shape_point") + (withPois ? pois * D("bytes_per_poi") : 0) + D("fixed_bytes");

    public static JsonObject FromPbf(long pbfBytes, string profile = "default", bool withPois = true)
    {
        double links = pbfBytes * JsonX.D(Calibration["links_per_pbf_byte"]![profile]);
        double shape = pbfBytes * JsonX.D(Calibration["shape_points_per_pbf_byte"]![profile]);
        double pois = withPois ? pbfBytes * D("pois_per_pbf_byte") : 0;
        return new JsonObject
        {
            ["method"] = "extract size", ["pbf_bytes"] = pbfBytes, ["links"] = (long)Math.Round(links),
            ["shape_points"] = (long)Math.Round(shape), ["pois"] = (long)Math.Round(pois),
            ["carindb_bytes"] = (long)Math.Round(CarindbBytes(links, shape, pois, withPois) * Safety), ["margin"] = Safety,
        };
    }

    public static JsonObject FromModel(long links, long shape, long pois, bool withPois = true) => new()
    {
        ["method"] = "road model", ["links"] = links, ["shape_points"] = shape, ["pois"] = withPois ? pois : 0,
        ["carindb_bytes"] = (long)Math.Round(CarindbBytes(links, shape, pois, withPois) * Safety), ["margin"] = Safety,
    };

    public static JsonObject Capacity(long carindbBytes)
    {
        long units = (carindbBytes + Sector - 1) / Sector;
        long total = units + OverheadSectors;
        var pct = new JsonObject();
        var fits = new JsonObject();
        foreach (var (n, s) in Cdr) { pct[n] = Math.Round(100.0 * total / s, 1); fits[n] = total <= s; }
        return new JsonObject
        {
            ["carindb_units"] = units, ["total_sectors"] = total, ["mib"] = Math.Round(total * Sector / 1048576.0, 1),
            ["percent"] = pct, ["fits"] = fits,
        };
    }

    public static bool Fits(JsonObject cap, string size) => cap["fits"]![size]!.GetValue<bool>();
    public static double Percent(JsonObject cap, string size) => JsonX.D(cap["percent"]![size]);

    public static List<string> Advice(JsonObject cap, string profile)
    {
        var l = new List<string>();
        double p74 = Percent(cap, "74 min"), p80 = Percent(cap, "80 min");
        if (Fits(cap, "74 min"))
        {
            l.Add($"OK: fits a 74-min CD-R ({p74}%).");
            if (p74 > 85) l.Add("Close to full: keep a margin, the estimate is +/- ~15%.");
        }
        else if (Fits(cap, "80 min")) l.Add($"WARNING: needs an 80-min CD-R ({p80}%); it will not fit a 74-min disc ({p74}%).");
        else
        {
            double over = JsonX.L(cap["total_sectors"]) / 360000.0;
            l.Add($"ERROR: too big for any CD-R: {p80}% of an 80-min disc ({over:F1}x).");
            l.Add("To make it fit, try one or more of:");
            l.Add("  - a smaller region (a state instead of a country, e.g. \"osm2carin regions <name>\");");
            if (profile != "compact") l.Add("  - --profile compact (drops service roads: class 6);");
            l.Add("  - --no-poi or --poi-categories with fewer categories;");
            l.Add("  - --levels 1-3 (major roads only, no street level).");
        }
        return l;
    }
}
