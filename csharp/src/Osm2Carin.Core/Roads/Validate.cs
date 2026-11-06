using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Osm2Carin.Core.Cf1;

namespace Osm2Carin.Core.Roads;

/// <summary>tools/carin.py's Parcel reader (independent of the writer): polylines of a decoded road tile.</summary>
public static class ParcelReader
{
    static int U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));

    public static List<(int attr, List<(long x, long y)> pts)> Polylines(byte[] p, int typ)
    {
        var sec = new (int off, int n)[15];
        for (int i = 0; i < 15; i++) sec[i] = (U16(p, 8 + 4 * i), U16(p, 10 + 4 * i));
        long x1 = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0x44)), y1 = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0x48));
        var nodes = new Dictionary<int, (int u, int v)>();
        for (int k = 0; k < sec[5].n; k++) { int o = sec[5].off + 8 * k; nodes[o] = (U16(p, o), U16(p, o + 2)); }
        for (int k = 0; k < sec[6].n; k++) { int o = sec[6].off + 16 * k; nodes[o] = (U16(p, o), U16(p, o + 2)); }
        int size = typ == 0 ? 30 : 24;
        int s7 = sec[7].off, s7End = s7 + 6 * sec[7].n;
        var links = new List<(int frm, int to, int shape, int attr)>();
        for (int k = 0; k < sec[4].n; k++)
        {
            int o = sec[4].off + size * k;
            links.Add((U16(p, o), U16(p, o + 2), U16(p, o + 4), U16(p, o + 10)));
        }
        var o2 = new List<(int, List<(long, long)>)>();
        for (int k = 0; k < links.Count; k++)
        {
            var l = links[k];
            int end = k + 1 < links.Count ? links[k + 1].shape : s7End;
            if (!nodes.TryGetValue(l.frm, out var a) || !nodes.TryGetValue(l.to, out var b)) continue;
            var pts = new List<(long, long)> { (x1 + a.u * 64L, y1 + a.v * 64L) };
            if (s7 <= l.shape && l.shape <= end && end <= s7End)
                for (int o = l.shape; o < end; o += 6) pts.Add((x1 + U16(p, o) * 64L, y1 + U16(p, o + 2) * 64L));
            pts.Add((x1 + b.u * 64L, y1 + b.v * 64L));
            o2.Add((l.attr, pts));
        }
        return o2;
    }
}

/// <summary>Checks on our road tiles and the viewer export (port of tools/osm2carin/validate.py).</summary>
public static class Validate
{
    public static (JsonObject res, JsonObject problems) Check(List<TileSpec> specs, bool cf1 = true, int sampleEvery = 1)
    {
        var res = new ConcurrentDictionary<string, long>();
        var problems = new ConcurrentDictionary<string, ConcurrentBag<string>>();
        void Add(string k, long n = 1) => res.AddOrUpdate(k, n, (_, v) => v + n);
        void Problem(string k, uint id) => problems.GetOrAdd(k, _ => new()).Add($"0x{id:x}");
        var decoded = specs.ToDictionary(sp => sp.BlockId, sp => sp.Decoded);
        var streetPos = new ConcurrentDictionary<(long, long), byte>();
        var sample = specs.Where((_, i) => i % sampleEvery == 0).ToList();
        Parallel.ForEach(specs, sp =>
        {
            if (sp.BType != 0) return;
            var t = Tile26.Parse(sp.Decoded);
            foreach (int sec in (int[])[5, 6])
                foreach (var r in t.Secs[sec])
                    streetPos.TryAdd((t.BBox.X1 + 64L * BinaryPrimitives.ReadUInt16BigEndian(r.Raw), t.BBox.Y1 + 64L * BinaryPrimitives.ReadUInt16BigEndian(r.Raw.AsSpan(2))), 0);
        });
        Parallel.ForEach(sample, sp =>
        {
            var d = sp.Decoded;
            if (sp.Cf1)
            {
                var back = Decoder00.DecodeBlock(sp.Raw, Fmt.RecordSizeTable, Fmt.DbRel, Fmt.SubRel, Fmt.Unit);
                bool same = back.AsSpan(8).SequenceEqual(d.AsSpan(8, back.Length - 8)) && back.AsSpan(0, 6).SequenceEqual(sp.Raw.AsSpan(0, 6));
                if (same) Add("cf1 decode == encoded tile"); else Problem("cf1 mismatch", sp.BlockId);
            }
            else Add("plain tiles");
            var t = Tile26.Parse(d);
            if (t.Build(Fmt.Unit, d.Length / Fmt.Unit).AsSpan().SequenceEqual(d)) Add("tile26 parse/build identical");
            else Problem("tile26 round trip", sp.BlockId);
            foreach (var (rule, (ok, bad)) in t.CheckRules())
            {
                Add($"rule ok: {rule}", ok);
                if (bad > 0) { Add($"RULE BROKEN: {rule}", bad); Problem(rule, sp.BlockId); }
            }
            var (x1, y1, x2, y2) = t.BBox;
            int n = 0;
            foreach (var (_, pts) in ParcelReader.Polylines(d, sp.BType))
            {
                n++;
                if (!pts.All(p => x1 <= p.x && p.x <= x2 && y1 <= p.y && p.y <= y2)) { Add("polylines outside the tile"); Problem("outside", sp.BlockId); }
            }
            Add("links read by carin.py", n);
            if (n != t.Secs[4].Count - 1) Problem("carin.py link count", sp.BlockId);

            // twins
            int o6 = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8 + 24)), n6 = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(8 + 26));
            for (int k = 0; k < n6; k++)
            {
                int o = o6 + 16 * k;
                int u = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o)), v = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o + 2));
                uint bid = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o + 8));
                int off = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o + 12));
                if (bid == 0) { Add("edge nodes without a twin"); continue; }
                if (!decoded.TryGetValue(bid, out var nb)) { Add("TWIN IN NO TILE"); continue; }
                int nx1 = BinaryPrimitives.ReadInt32BigEndian(nb.AsSpan(0x44)), ny1 = BinaryPrimitives.ReadInt32BigEndian(nb.AsSpan(0x48));
                int tu = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(off)), tv = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(off + 2));
                uint bbid = BinaryPrimitives.ReadUInt32BigEndian(nb.AsSpan(off + 8));
                int boff = BinaryPrimitives.ReadUInt16BigEndian(nb.AsSpan(off + 12));
                bool ok = nx1 + 64L * tu == x1 + 64L * u && ny1 + 64L * tv == y1 + 64L * v && bbid == sp.BlockId && boff == o;
                Add(ok ? "twins ok" : "TWINS BROKEN");
            }
            if (sp.BType != 0)
                foreach (int sec in (int[])[5, 6])
                    foreach (var r in t.Secs[sec])
                    {
                        bool on = streetPos.ContainsKey((x1 + 64L * BinaryPrimitives.ReadUInt16BigEndian(r.Raw), y1 + 64L * BinaryPrimitives.ReadUInt16BigEndian(r.Raw.AsSpan(2))));
                        Add(on ? "coarse nodes on a street node" : "COARSE NODE OFF THE STREETS");
                    }
            if (sp.Parent != null)
            {
                var (a, b) = (sp.Tile.BBox, sp.Parent.Tile.BBox);
                bool inside = b.X1 <= a.X1 && b.Y1 <= a.Y1 && a.X2 <= b.X2 && a.Y2 <= b.Y2;
                Add(inside ? "parent contains tile" : "parent does not contain tile");
            }
        });
        var r = new JsonObject();
        foreach (var k in res.Keys.OrderBy(k => k, StringComparer.Ordinal)) r[k] = res[k];
        var p = new JsonObject();
        foreach (var (k, v) in problems) p[k] = new JsonArray(v.Take(10).Select(s => (JsonNode?)s).ToArray());
        return (r, p);
    }

    public static bool HasProblems(JsonObject res, JsonObject problems) =>
        problems.Count > 0 || res.Any(kv => kv.Key.Contains("BROKEN") || kv.Key.Contains("OFF THE") || kv.Key.Contains("NO TILE"));

    /// <summary>Viewer data in the format of tools/carin.py export / streets.</summary>
    public static JsonObject ExportViewer(List<TileSpec> specs, string outDir, string? viewerHtml = null)
    {
        const double Cell = 0.1;
        string data = Path.Combine(outDir, "data");
        Directory.CreateDirectory(Path.Combine(data, "l0"));
        var per = new Dictionary<int, (List<int> attrs, List<List<double>> lines)> { [1] = (new(), new()), [2] = (new(), new()), [3] = (new(), new()) };
        var cells = new SortedDictionary<string, (List<int> attrs, List<List<long>> lines)>(StringComparer.Ordinal);
        foreach (var sp in specs)
            foreach (var (attr, pts) in ParcelReader.Polylines(sp.Decoded, sp.BType))
            {
                if (sp.BType != 0)
                {
                    var flat = new List<double>();
                    foreach (var (x, y) in pts) { var (lo, la) = Geo.ToLonLat(x, y); flat.Add(Math.Round(lo, 5)); flat.Add(Math.Round(la, 5)); }
                    per[sp.BType].attrs.Add(attr); per[sp.BType].lines.Add(flat);
                }
                else
                {
                    var flat = new List<long>();
                    long px = 0, py = 0;
                    foreach (var (x, y) in pts)
                    {
                        var (lo, la) = Geo.ToLonLat(x, y);
                        long ix = (long)Math.Round(lo * 1e5), iy = (long)Math.Round(la * 1e5);
                        flat.Add(ix - px); flat.Add(iy - py); px = ix; py = iy;
                    }
                    string key = $"{(long)Math.Floor((flat[0] / 1e5 + 180) / Cell)}_{(long)Math.Floor((flat[1] / 1e5 + 90) / Cell)}";
                    if (!cells.TryGetValue(key, out var c)) cells[key] = c = (new(), new());
                    c.attrs.Add(attr); c.lines.Add(flat);
                }
            }
        static string Json<T>(List<int> attrs, List<List<T>> lines)
        {
            var sb = new StringBuilder("{\"attrs\":[");
            sb.Append(string.Join(',', attrs)).Append("],\"lines\":[");
            sb.Append(string.Join(',', lines.Select(l => "[" + string.Join(',', l.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))) + "]")));
            return sb.Append("]}").ToString();
        }
        foreach (var (lv, d) in per) File.WriteAllText(Path.Combine(data, $"level{lv}.json"), Json(d.attrs, d.lines));
        foreach (var (k, d) in cells) File.WriteAllText(Path.Combine(data, "l0", k + ".json"), Json(d.attrs, d.lines));
        File.WriteAllText(Path.Combine(data, "l0", "index.json"),
            "{\"cell\": 0.1, \"cells\": [" + string.Join(", ", cells.Keys.Select(k => $"\"{k}\"")) + "], \"format\": \"delta-1e-5\"}");
        if (viewerHtml != null && File.Exists(viewerHtml)) File.Copy(viewerHtml, Path.Combine(outDir, "index.html"), true);
        return new JsonObject { ["cells"] = cells.Count, ["level1_links"] = per[1].lines.Count, ["level2_links"] = per[2].lines.Count, ["level3_links"] = per[3].lines.Count };
    }
}
