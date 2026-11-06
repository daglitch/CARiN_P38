using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Osm2Carin.Core;

/// <summary>Region picker over Geofabrik's index, and the cached, resumable, MD5-checked download
/// (tools/osm2carin/regions.py and download.py; same cache file names).</summary>
public static class Regions
{
    public const string IndexUrl = "https://download.geofabrik.de/index-v1-nogeom.json";
    public const string UserAgent = "osm2carin/0.2 (CARiN P38 map disc builder)";
    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }

    public sealed record Region(string Id, string Name, string PbfUrl, string[] Codes, Region[]? Parts = null)
    {
        /// <summary>The extracts this region is built from: itself, or the regions it combines.</summary>
        public Region[] Extracts => Parts ?? [this];

        /// <summary>Display name ("Nevada" for us/nevada, whose index name is its id).</summary>
        public string Label => Name == Id
            ? string.Join(' ', Id.Split('/')[^1].Split('-').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w))
            : Name;
    }

    public static Dictionary<string, Region> LoadIndex(string cacheDir, bool refresh = false)
    {
        Directory.CreateDirectory(cacheDir);
        var path = Path.Combine(cacheDir, "geofabrik-index-v1-nogeom.json");
        if (refresh || !File.Exists(path) || DateTime.Now - File.GetLastWriteTime(path) > TimeSpan.FromDays(7))
        {
            var data = Http.GetByteArrayAsync(IndexUrl).GetAwaiter().GetResult();
            JsonNode.Parse(data);                                   // validate before replacing the cache
            File.WriteAllBytes(path, data);
        }
        var o = new Dictionary<string, Region>();
        foreach (var f in JsonNode.Parse(File.ReadAllText(path))!["features"]!.AsArray())
        {
            var p = f!["properties"]!;
            var codes = (p["iso3166-1:alpha2"]?.AsArray().ToArray() ?? [])
                .Concat(p["iso3166-2"]?.AsArray().ToArray() ?? []).Select(x => x!.GetValue<string>()).ToArray();
            var pbf = p["urls"]?["pbf"]?.GetValue<string>();
            if (pbf == null) continue;
            var id = p["id"]!.GetValue<string>();
            o[id] = new Region(id, p["name"]?.GetValue<string>() ?? id, pbf, codes);
        }
        return o;
    }

    /// <summary>"us/california,us/nevada": several regions built as one map (Id "us/california+us/nevada",
    /// Name "California and Nevada"); a single query resolves as before.</summary>
    public static Region ResolveAll(Dictionary<string, Region> index, string query)
    {
        var parts = query.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(q => Resolve(index, q)).DistinctBy(r => r.Id).ToArray();
        if (parts.Length == 1) return parts[0];
        return new Region(string.Join('+', parts.Select(r => r.Id)), string.Join(" and ", parts.Select(r => r.Label)), "",
                          parts.SelectMany(r => r.Codes).Distinct().ToArray(), parts);
    }

    public static Region Resolve(Dictionary<string, Region> index, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (index.TryGetValue(q, out var r)) return r;
        var norm = q.Replace(' ', '-');
        var hits = index.Values.Where(p => p.Codes.Any(c => c.ToLowerInvariant() == q) || p.Id.Split('/')[^1] == norm
                                           || p.Label.ToLowerInvariant() == q).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0) hits = index.Values.Where(p => p.Id.Contains(q) || p.Label.ToLowerInvariant().Contains(q)).ToList();
        throw new KeyNotFoundException($"region \"{query}\" is " + (hits.Count > 0
            ? "ambiguous: " + string.Join(", ", hits.Select(h => h.Id).Order(StringComparer.Ordinal).Take(20))
            : "unknown; try \"osm2carin regions <text>\""));
    }

    public static List<Region> Search(Dictionary<string, Region> index, string text = "")
    {
        var t = text.ToLowerInvariant();
        return index.Values.Where(p => t == "" || p.Id.Contains(t) || p.Label.ToLowerInvariant().Contains(t)
                                       || p.Codes.Any(c => c.ToLowerInvariant() == t))
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    public static long? RemoteSize(string url)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = Http.Send(req);
                return resp.Content.Headers.ContentLength;
            }
            catch (HttpRequestException) { Thread.Sleep(2000); }
        }
        return null;
    }

    public static string CachePath(string cacheDir, string id) => Path.Combine(cacheDir, id.Replace("/", "__") + "-latest.osm.pbf");

    static string Md5(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(MD5.HashData(f));
    }

    /// <summary>Download the region's extract into the cache unless a verified copy is there. -> (path, md5).</summary>
    public static (string path, string md5) Fetch(Region region, string cacheDir, bool refresh = false, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        Directory.CreateDirectory(cacheDir);
        var path = CachePath(cacheDir, region.Id);
        var want = Http.GetStringAsync(region.PbfUrl + ".md5").GetAwaiter().GetResult()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        if (File.Exists(path) && !refresh)
        {
            var have = Md5(path);
            if (have == want) { log($"cached: {path} (md5 ok)"); return (path, have); }
            log("cached copy is stale or damaged (md5 differs): downloading again");
            File.Delete(path);
        }
        var part = path + ".part";
        long done = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, region.PbfUrl);
        if (done > 0) req.Headers.Range = new RangeHeaderValue(done, null);
        log($"downloading {region.PbfUrl}");
        using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        if (done > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) done = 0;
        long? total = resp.Content.Headers.ContentLength + done;
        using (var src = resp.Content.ReadAsStream())
        using (var dst = new FileStream(part, done > 0 ? FileMode.Append : FileMode.Create))
        {
            var buf = new byte[1 << 20];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long got = 0;
            double last = -1;
            int n;
            while ((n = src.Read(buf)) > 0)
            {
                dst.Write(buf, 0, n);
                got += n;
                if (sw.Elapsed.TotalSeconds - last > 0.5)
                {
                    last = sw.Elapsed.TotalSeconds;
                    double rate = got / Math.Max(sw.Elapsed.TotalSeconds, 1e-6) / 1048576.0;
                    if (total is long t)
                    {
                        double pct = (double)(done + got) / t;
                        int bars = (int)(pct * 30);
                        Console.Error.Write($"\r  [{new string('#', bars)}{new string('.', 30 - bars)}] {pct,6:P1} {(done + got) / 1048576.0,8:F1} / {t / 1048576.0:F1} MiB  {rate,5:F1} MiB/s ");
                    }
                    else Console.Error.Write($"\r  {(done + got) / 1048576.0,8:F1} MiB  {rate,5:F1} MiB/s ");
                }
            }
            Console.Error.WriteLine();
        }
        var md5 = Md5(part);
        if (md5 != want)
        {
            File.Delete(part);
            throw new IOException($"MD5 mismatch for {region.PbfUrl}: got {md5}, expected {want}");
        }
        File.Move(part, path, true);
        log($"saved {path} (md5 ok)");
        return (path, md5);
    }
}
