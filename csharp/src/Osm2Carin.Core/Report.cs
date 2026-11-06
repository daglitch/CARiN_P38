using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Osm2Carin.Core;

/// <summary>Build report (report.json + report.md), as tools/osm2carin/report.py.</summary>
public static class Report
{
    static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static double Num(JsonNode n) => double.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    static string N(JsonNode? n) => n == null ? "-" : n.GetValueKind() == JsonValueKind.Number
        ? (Num(n) % 1 == 0 ? Num(n).ToString("N0") : Num(n).ToString("N1")) : n.ToString();

    public static string Write(JsonObject s, string outDir)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "report.json"), s.ToJsonString(Pretty));
        var L = new List<string>
        {
            $"# osm2carin report: {s["region"]}", "",
            $"- extract: `{s["pbf"]}` ({Num(s["pbf_bytes"]!) / 1048576.0:N1} MiB, md5 {s["pbf_md5"]})",
            $"- profile: {s["profile"]}; model built in {s["seconds"]} s", "",
            "## In (OSM)", "",
            $"- road ways used: {N(s["road_ways"])}",
            $"- turn restrictions: {N(s["restrictions"]?["in_osm"])} (mapped {N(s["restrictions"]?["mapped"])})", "",
            "## Out (road model, street level)", "",
            $"- links: {N(s["links"])} ({N(s["road_km"])} km), graph nodes {N(s["graph_nodes"])}, shape points {N(s["shape_points"])}",
            $"- one-way {N(s["oneway_links"])}, closed {N(s["closed_links"])}, named {N(s["named_links"])}",
            $"- links over 65,535 m (split later): {s["long_links_over_65535m"]}", "",
            "| class | links | km |", "|---|---|---|",
        };
        foreach (var (c, v) in s["links_by_class"]!.AsObject()) L.Add($"| {c} | {N(v!["links"])} | {N(v["km"])} |");
        L.AddRange(["", "| highway | links | km |", "|---|---|---|"]);
        foreach (var (h, v) in s["links_by_highway"]!.AsObject()) L.Add($"| {h} | {N(v!["links"])} | {N(v["km"])} |");
        L.AddRange(["", $"## POIs: {N(s["pois"])} ({N(s["pois_named"])} named)", "", "| category | count |", "|---|---|"]);
        foreach (var (c, n) in s["poi_categories"]!.AsObject()) L.Add($"| {c} | {N(n)} |");
        if (s["estimate"] is JsonObject e)
        {
            var cap = e["capacity"]!.AsObject();
            L.AddRange(["", "## Size estimate", "",
                $"- carindb ~{Num(e["carindb_bytes"]!) / 1048576.0:N0} MiB (incl. {Num(e["margin"]!) - 1:P0} margin)",
                $"- total {N(cap["total_sectors"])} sectors ({cap["mib"]} MiB): {Estimate.Percent(cap, "74 min")}% of a 74-min CD-R, {Estimate.Percent(cap, "80 min")}% of an 80-min CD-R"]);
            foreach (var a in e["advice"]!.AsArray()) L.Add($"- {a}");
        }
        if (s["tiles"] is JsonObject t)
        {
            L.AddRange(["", "## Road tiles", "", $"- {N(t["units"])} units ({Num(t["units"]!) * 2048 / 1048576.0:N1} MiB), built in {t["seconds"]} s", "",
                "| block type | blocks | units | CF=1 |", "|---|---|---|---|"]);
            foreach (var (k, v) in t["blocks"]!.AsObject()) L.Add($"| {k} | {N(v!["blocks"])} | {N(v["units"])} | {N(v["cf1"])} |");
            L.AddRange(["", "Checks:", ""]);
            foreach (var (k, v) in t["checks"]!.AsObject()) L.Add($"- {k}: {N(v)}");
            if (t["fit"] is JsonObject fit && fit.Count > 0)
            {
                L.AddRange(["", "Reductions in full tiles:", ""]);
                foreach (var (lv, d) in fit) foreach (var (k, v) in d!.AsObject()) L.Add($"- {lv} {k}: {N(v)}");
            }
        }
        L.AddRange(["", "## Dropped", "", "| what | count |", "|---|---|"]);
        foreach (var (k, v) in s["dropped"]!.AsObject().OrderByDescending(kv => Num(kv.Value!))) L.Add($"| {k} | {N(v)} |");
        var md = Path.Combine(outDir, "report.md");
        File.WriteAllText(md, string.Join("\n", L) + "\n", new UTF8Encoding(false));
        return md;
    }
}
