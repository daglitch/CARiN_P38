using System.Diagnostics;
using System.Text.Json.Nodes;
using Osm2Carin.Core;
using Osm2Carin.Core.Osm;
using Osm2Carin.Core.Roads;

namespace Osm2Carin.Cli;

/// <summary>osm2carin: OSM extract -> CARiN map disc (Range Rover P38, CD-ID 5944 format). C# port of
/// tools/osm2carin (same commands, same output files under out/).</summary>
public static class Program
{
    const string Usage = """
        osm2carin: OSM extract -> CARiN map disc (Range Rover P38, CD-ID 5944 format)

          osm2carin regions [text]                       list / search Geofabrik regions
          osm2carin estimate --region us/nevada          size estimate before downloading
          osm2carin download --region us/nevada          fetch the .osm.pbf (cached, MD5-checked)
          osm2carin model    --region us/nevada          road graph + POIs, report, model-based estimate
          osm2carin tiles    --region us/nevada          road levels 0-3 as CARiN tiles, checked, + viewer data
          osm2carin build    --region us/california [--force] [--estimate-only]
          osm2carin cd-split <orig.bin> <dir>             raw image -> track1.iso + audio.raw
          osm2carin cd-build <track1.iso> <out.bin> [--audio audio.raw]
          osm2carin cd-verify <image.bin>                 EDC/ECC of every data sector

        options: --out <dir> (default <repo>/out)  --profile default|full|compact  --no-poi
                 --refresh (re-download)  --plain (street tiles uncompressed)  --check-every N (validate every Nth tile)

        Phase status: 1 (download, road model, estimate) and 2 (road tiles, levels 0-3) are done;
        3-4 (search/index structures, packing, .bin/.cue) are not yet.
        """;

    sealed class Args
    {
        public string Cmd = "";
        public List<string> Pos = new();
        public string? Region, Audio;
        public string Out = DefaultOut(), Profile = "default";
        public bool NoPoi, Refresh, Force, EstimateOnly, Plain;
        public int CheckEvery = 1;
    }

    static string DefaultOut()
    {
        // <repo>/out: the repo is the first parent folder with tools/osm2carin
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tools", "osm2carin"))) return Path.Combine(d.FullName, "out");
        return Path.GetFullPath("out");
    }

    static string Repo(Args a) => Path.GetDirectoryName(Path.GetFullPath(a.Out))!;

    static Args Parse(string[] argv)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            string s = argv[i];
            string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{s} needs a value");
            switch (s)
            {
                case "--region": a.Region = Next(); break;
                case "--out": a.Out = Path.GetFullPath(Next()); break;
                case "--profile": a.Profile = Next(); break;
                case "--audio": a.Audio = Next(); break;
                case "--check-every": a.CheckEvery = int.Parse(Next()); break;
                case "--no-poi": a.NoPoi = true; break;
                case "--refresh": a.Refresh = true; break;
                case "--force": a.Force = true; break;
                case "--estimate-only": a.EstimateOnly = true; break;
                case "--plain": a.Plain = true; break;
                default:
                    if (s.StartsWith("--")) throw new ArgumentException($"unknown option {s}");
                    if (a.Cmd == "") a.Cmd = s; else a.Pos.Add(s);
                    break;
            }
        }
        if (!Attrs.Profiles.ContainsKey(a.Profile)) throw new ArgumentException($"unknown profile {a.Profile}");
        return a;
    }

    public static int Main(string[] argv)
    {
        Args a;
        try { a = Parse(argv); }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        try
        {
            return a.Cmd switch
            {
                "regions" => CmdRegions(a),
                "estimate" => CmdEstimate(a),
                "download" => CmdDownload(a),
                "model" => CmdModel(a),
                "tiles" => CmdTiles(a),
                "build" => CmdBuild(a),
                "cd-split" => CmdCdSplit(a),
                "cd-build" => CmdCdBuild(a),
                "cd-verify" => CmdCdVerify(a),
                _ => Help(),
            };
        }
        catch (KeyNotFoundException e) { Console.Error.WriteLine(e.Message); return 1; }
    }

    static int Help() { Console.WriteLine(Usage); return 2; }

    static string Slug(string id) => id.Replace('/', '_');
    static string Cache(Args a) => Path.Combine(a.Out, "cache");

    static Regions.Region Region(Args a)
    {
        if (a.Region == null) throw new KeyNotFoundException("--region is required");
        return Regions.Resolve(Regions.LoadIndex(Cache(a)), a.Region);
    }

    static void PrintEstimate(JsonObject est, JsonObject cap, string profile, string title)
    {
        Console.WriteLine($"\n{title}");
        Console.WriteLine($"  street links ~{JsonX.L(est["links"]):N0}, POIs ~{JsonX.L(est["pois"]):N0}");
        Console.WriteLine($"  carindb ~{JsonX.L(est["carindb_bytes"]) / 1048576.0:N0} MiB (incl. {JsonX.D(est["margin"]) - 1:P0} margin), " +
                          $"{JsonX.L(cap["total_sectors"]):N0} sectors = {Estimate.Percent(cap, "74 min")}% of a 74-min, {Estimate.Percent(cap, "80 min")}% of an 80-min CD-R");
        foreach (var l in Estimate.Advice(cap, profile)) Console.WriteLine("  " + l);
    }

    static int CmdRegions(Args a)
    {
        var idx = Regions.LoadIndex(Cache(a), a.Refresh);
        var hits = Regions.Search(idx, a.Pos.FirstOrDefault() ?? "");
        foreach (var p in hits) Console.WriteLine($"{p.Id,-45} {p.Label,-35} {string.Join(',', p.Codes)}");
        Console.WriteLine($"{hits.Count} regions");
        return 0;
    }

    static (JsonObject est, JsonObject cap)? PreEstimate(Args a, Regions.Region r)
    {
        var size = Regions.RemoteSize(r.PbfUrl);
        if (size == null) { Console.WriteLine("could not get the extract size; skipping the pre-download estimate"); return null; }
        Console.WriteLine($"{r.Id}: extract {size.Value / 1048576.0:N0} MiB");
        var est = Estimate.FromPbf(size.Value, a.Profile, !a.NoPoi);
        var cap = Estimate.Capacity(JsonX.L(est["carindb_bytes"]));
        PrintEstimate(est, cap, a.Profile, "Estimate before download:");
        return (est, cap);
    }

    static int CmdEstimate(Args a) { PreEstimate(a, Region(a)); return 0; }
    static int CmdDownload(Args a) { Regions.Fetch(Region(a), Cache(a), a.Refresh); return 0; }

    static (RoadModel m, JsonObject cap) RunModel(Args a, Regions.Region r)
    {
        var (pbf, md5) = Regions.Fetch(r, Cache(a), a.Refresh);
        var m = ModelBuilder.Build(pbf, a.Profile);
        m.Stats["region"] = r.Id;
        m.Stats["pbf_md5"] = md5;
        var d = Path.Combine(a.Out, Slug(r.Id));
        m.Save(Path.Combine(d, "model"));
        var est = Estimate.FromModel(JsonX.L(m.Stats["links"]), JsonX.L(m.Stats["shape_points"]), JsonX.L(m.Stats["pois"]), !a.NoPoi);
        var cap = Estimate.Capacity(JsonX.L(est["carindb_bytes"]));
        est["capacity"] = cap;
        est["advice"] = new JsonArray(Estimate.Advice(cap, a.Profile).Select(x => (JsonNode?)x).ToArray());
        m.Stats["estimate"] = est;
        var rp = Report.Write(m.Stats, d);
        PrintEstimate(est, cap, a.Profile, "Estimate from the road model:");
        Console.WriteLine($"\nreport: {rp}");
        return (m, cap);
    }

    static int CmdModel(Args a) { RunModel(a, Region(a)); return 0; }

    static int RunTiles(Args a, Regions.Region r, RoadModel? m = null)
    {
        var d = Path.Combine(a.Out, Slug(r.Id));
        if (m == null)
            m = File.Exists(Path.Combine(d, "model.npz")) ? RoadModel.Load(Path.Combine(d, "model")) : RunModel(a, r).m;
        var sw = Stopwatch.StartNew();
        var rt = Tiles.Build(m, m.Strings, r.Label);
        Console.WriteLine($"tiles built in {sw.Elapsed.TotalSeconds:F1} s");
        var specs = Link.Run(rt, 0, !a.Plain, withIndex: true);
        var tc = sw.Elapsed.TotalSeconds;
        var (res, problems) = Validate.Check(specs, !a.Plain, a.CheckEvery);
        Validate.CheckIndex(rt, res, problems);
        Console.WriteLine($"checked in {sw.Elapsed.TotalSeconds - tc:F1} s");
        var vstats = Validate.ExportViewer(specs, Path.Combine(d, "viewer"), Path.Combine(Repo(a), "viewer", "index.html"));
        using (var f = File.Create(Path.Combine(d, "roads.bin")))
            foreach (var b in rt.Blocks) f.Write(b);
        var by = new JsonObject();
        foreach (var g in specs.GroupBy(sp => sp.BType).OrderBy(g => Array.IndexOf(Link.Order, g.Key)))
            by[$"0x{g.Key:X2}"] = new JsonObject
            {
                ["blocks"] = g.Count(), ["units"] = g.Sum(sp => sp.Raw.Length / 2048), ["cf1"] = g.Count(sp => sp.Cf1),
                ["decoded_bytes"] = g.Sum(sp => (long)sp.Decoded.Length),
            };
        var tiles = JsonNode.Parse(rt.Stats.ToJsonString())!.AsObject();
        tiles["blocks"] = by; tiles["units"] = rt.EndUnit; tiles["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
        tiles["checks"] = res; tiles["problems"] = problems; tiles["viewer"] = vstats;
        tiles["checked_every"] = a.CheckEvery;
        var stats = JsonNode.Parse(m.Stats.ToJsonString())!.AsObject();
        stats["tiles"] = tiles;
        var rp = Report.Write(stats, d);
        Console.WriteLine($"road tiles: {specs.Count:N0} blocks, {rt.EndUnit:N0} units ({rt.EndUnit * 2048 / 1048576.0:N1} MiB) in {sw.Elapsed.TotalSeconds:F1} s");
        foreach (var (k, v) in by) Console.WriteLine($"  {k}: {v!["blocks"]} blocks, {v["units"]} units, {v["cf1"]} CF=1");
        bool bad = Validate.HasProblems(res, problems);
        Console.WriteLine("checks: " + (bad ? $"PROBLEMS {problems.ToJsonString()}" : a.CheckEvery == 1 ? "all passed" : $"all passed (every {a.CheckEvery}th tile checked)"));
        Console.WriteLine($"viewer: {Path.Combine(d, "viewer")}  (py -m http.server -d <that folder>)");
        Console.WriteLine($"report: {rp}");
        return bad ? 1 : 0;
    }

    static int CmdTiles(Args a) => RunTiles(a, Region(a));

    static int CmdBuild(Args a)
    {
        var r = Region(a);
        var pre = PreEstimate(a, r);
        if (pre != null && !Estimate.Fits(pre.Value.cap, "80 min") && !a.Force)
        {
            Console.WriteLine("\nStopping: the estimate does not fit a CD-R. Use --force to continue anyway.");
            return 1;
        }
        if (a.EstimateOnly) return 0;
        var (m, cap) = RunModel(a, r);
        if (!Estimate.Fits(cap, "80 min") && !a.Force)
        {
            Console.WriteLine("\nStopping: the model-based estimate does not fit a CD-R. Use --force to continue anyway.");
            return 1;
        }
        int rc = RunTiles(a, r, m);
        Console.WriteLine("\nPhases 3-4 (search/index structures, packing, .bin/.cue) are not implemented yet; stopped after the road tiles.");
        return rc != 0 ? rc : 2;
    }

    static int CmdCdSplit(Args a) { Console.WriteLine(CdImage.Split(a.Pos[0], a.Pos[1])); return 0; }

    static int CmdCdBuild(Args a)
    {
        int t1 = CdImage.Build(a.Pos[0], a.Pos[1], a.Audio);
        Console.WriteLine($"track 1: {t1:N0} sectors; wrote {a.Pos[1]} and .cue");
        return 0;
    }

    static int CmdCdVerify(Args a)
    {
        var v = CdImage.Verify(a.Pos[0]);
        Console.WriteLine($"Form 1: {v.Form1Ok:N0} ok, {v.Form1Bad:N0} bad; Form 2: {v.Form2Ok:N0} ok, {v.Form2Bad:N0} bad"
                          + (v.FirstBad is int fb ? $"; first bad LBA {fb}" : ""));
        return v.Form1Bad + v.Form2Bad > 0 ? 1 : 0;
    }
}
