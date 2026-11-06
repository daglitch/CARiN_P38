using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Osm2Carin.Core.Osm;

/// <summary>Interned string table; id 0 = empty.</summary>
public sealed class Strings
{
    public readonly List<string> Items = [""];
    readonly Dictionary<string, int> ids = new() { [""] = 0 };

    public int this[string? s]
    {
        get
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (!ids.TryGetValue(s, out int i)) { i = ids[s] = Items.Count; Items.Add(s); }
            return i;
        }
    }
}

/// <summary>Counts by reason, in first-seen order (the Python dict).</summary>
public sealed class Dropped
{
    public readonly List<string> Order = [];
    public readonly Dictionary<string, long> Count = new();

    public void Add(string reason, long n = 1)
    {
        if (!Count.ContainsKey(reason)) { Order.Add(reason); Count[reason] = 0; }
        Count[reason] += n;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject();
        foreach (var k in Order) o[k] = Count[k];
        return o;
    }
}

/// <summary>A road model: named 1-D arrays (the same names and dtypes as model.py), the string
/// table and the stats. Saved as model.npz + model.json, readable by tools/osm2carin/model.py.</summary>
public sealed class RoadModel
{
    public readonly List<(string name, Array data)> Arrays = new();
    public List<string> Strings = new();
    public JsonObject Stats = new();

    public T[] Get<T>(string name) => (T[])Arrays.First(a => a.name == name).data;
    public void Set(string name, Array data)
    {
        int i = Arrays.FindIndex(a => a.name == name);
        if (i >= 0) Arrays[i] = (name, data); else Arrays.Add((name, data));
    }

    public void Save(string prefix)
    {
        Npz.Write(prefix + ".npz", Arrays);
        var meta = new JsonObject { ["stats"] = JsonNode.Parse(Stats.ToJsonString()), ["strings"] = new JsonArray(Strings.Select(s => (JsonNode?)s).ToArray()) };
        File.WriteAllText(prefix + ".json", meta.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    public static RoadModel Load(string prefix)
    {
        var m = new RoadModel();
        foreach (var (k, v) in Npz.Read(prefix + ".npz")) m.Arrays.Add((k, v));
        var meta = JsonNode.Parse(File.ReadAllText(prefix + ".json"))!;
        m.Strings = meta["strings"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        m.Stats = meta["stats"]!.AsObject();
        return m;
    }
}

/// <summary>OSM .osm.pbf -> road model + POIs: port of tools/osm2carin/model.py, step for step.</summary>
public static class ModelBuilder
{
    static readonly string[] AttrKeys = ["hw", "cls", "sub", "dirn", "rev", "jn", "flags", "spd"];

    sealed class PoiRec
    {
        public long Osm; public char Kind; public int Key, Value, Cat, Name, Brand, Phone, Housenumber, Street, City, Postcode;
        public int X, Y;
    }

    static PoiRec Record(Strings S, long id, char kind, (string key, string value, string? cat) pt, Tags tags)
    {
        string G(string k) => tags.Get(k) ?? "";
        string Or(string a, string b) => a != "" ? a : b;
        var r = new PoiRec { Osm = id, Kind = kind };
        r.Key = S[pt.key]; r.Value = S[pt.value]; r.Cat = S[pt.cat];
        r.Name = S[Py.Lower(G("name"))]; r.Brand = S[Py.Lower(G("brand"))];
        r.Phone = S[Or(G("phone"), G("contact:phone"))];
        r.Housenumber = S[G("addr:housenumber")]; r.Street = S[Py.Lower(G("addr:street"))];
        r.City = S[Py.Lower(G("addr:city"))]; r.Postcode = S[G("addr:postcode")];
        return r;
    }

    public static RoadModel Build(string pbf, string profile = "default", Action<string>? log = null) => Build([pbf], profile, log);

    public static RoadModel Build(IReadOnlyList<string> pbfs, string profile = "default", Action<string>? log = null) =>
        Build(pbfs.Select(p => new Extract(p)).ToList(), profile, log);

    /// <summary>An extract to read. Only != null: just these highway=* values, no POIs, and only nodes
    /// inside the disc's quadtree root (Tiles.DiscRoot). Used for the rest of the country around fully
    /// detailed regions, as the original disc has the US highway network.</summary>
    public sealed record Extract(string Path, IReadOnlySet<string>? Only = null)
    {
        public bool MajorOnly => Only != null;
    }

    /// <summary>Road selections for a partial region: "highways" = motorways and trunk roads
    /// (interstates, freeways, US highways), "major" adds primary roads.</summary>
    public static readonly Dictionary<string, HashSet<string>> Selections = new()
    {
        ["highways"] = ["motorway", "motorway_link", "trunk", "trunk_link"],
        ["major"] = ["motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link"],
    };

    /// <summary>One road model from several extracts (e.g. two neighbouring states). Geofabrik extracts
    /// overlap at their borders: a way crossing it is in both, with all its nodes, so ways, relations
    /// and nodes are taken once per OSM ID (first file wins). With one file nothing is dropped.</summary>
    public static RoadModel Build(IReadOnlyList<Extract> extracts, string profile = "default", Action<string>? log = null)
    {
        var pbfs = extracts.Select(e => e.Path).ToList();
        bool multi = pbfs.Count > 1;
        var (rx0, ry0, rside) = Roads.Tiles.DiscRoot;
        var seenWays = new HashSet<long>(); var seenRels = new HashSet<long>(); var seenPoiNodes = new HashSet<long>();
        log ??= Console.WriteLine;
        var sw = Stopwatch.StartNew();
        var prof = Attrs.Profiles[profile];
        var hwTable = Attrs.Highways.Where(h => !prof.Exclude.Contains(h.hw)).ToDictionary(h => h.hw, h => (h.cls, h.sub, h.kmh));
        var S = new Strings();
        var drop = new Dropped();

        // ------------------------------------------------ pass A: ways + relations
        var refs = new List<long>();
        var wayStart = new List<long>();
        var wayId = new List<long>();
        var wAttr = AttrKeys.ToDictionary(k => k, _ => new List<byte>());
        var wName = new List<int>();
        var wRef = new List<int>();
        var poiWayRefs = new List<long[]>();
        var poiWayMeta = new List<PoiRec>();
        var restrictions = new List<(long rid, long fr, long via, long to, string kind)>();
        int nHw = 0;
        var keys = new HashSet<string>(Poi.Keys) { "highway", "type" };
        foreach (var ex in extracts)
        using (var prog = Progress.Bytes("ways " + System.IO.Path.GetFileName(ex.Path).Replace("-latest.osm.pbf", ""), new FileInfo(ex.Path).Length))
        foreach (var blk in Pbf.Read(ex.Path, new PbfFilter { Ways = true, Relations = true, Keys = keys }, progress: prog))
        {
            foreach (var o in blk.Items)
            {
                if (ex.Only != null && o is OsmWay mw && !ex.Only.Contains(mw.Tags.Get("highway") ?? "")) continue;
                if (multi && !(o is OsmRelation r0 ? seenRels.Add(r0.Id) : seenWays.Add(((OsmWay)o).Id))) continue;
                if (o is OsmRelation rel)
                {
                    if (rel.Tags.Get("type") != "restriction") continue;
                    string r = !string.IsNullOrEmpty(rel.Tags.Get("restriction")) ? rel.Tags.Get("restriction")! : rel.Tags.Get("restriction:motorcar") ?? "";
                    if (r == "") { drop.Add("restriction without restriction=* tag"); continue; }
                    long? fr = null, toW = null, via = null;
                    bool viaWay = false;
                    foreach (var mb in rel.Members)
                    {
                        if (mb.Role == "from" && mb.Type == 'w') fr = mb.Ref;
                        else if (mb.Role == "to" && mb.Type == 'w') toW = mb.Ref;
                        else if (mb.Role == "via") { via = mb.Ref; viaWay = mb.Type == 'w'; }
                    }
                    if (fr == null || toW == null || via == null) drop.Add("restriction with missing members");
                    else if (viaWay) drop.Add("restriction via a way (not supported yet)");
                    else restrictions.Add((rel.Id, fr.Value, via.Value, toW.Value, r));
                    continue;
                }
                var w = (OsmWay)o;
                var tags = w.Tags;
                string? hw = tags.Get("highway");
                if (!string.IsNullOrEmpty(hw))
                {
                    nHw++;
                    if (!hwTable.TryGetValue(hw, out var spec)) drop.Add($"highway={hw} (not a road for this profile)");
                    else
                    {
                        var a = Attrs.WayAttrs(hw, spec, tags, prof, out var skip);
                        if (a == null) drop.Add($"highway={hw} with {skip}");
                        else
                        {
                            var nodes = w.Nodes;
                            if (nodes.Length < 2) drop.Add("way with fewer than 2 nodes");
                            else
                            {
                                if (a.Rev != 0) { nodes = (long[])nodes.Clone(); Array.Reverse(nodes); }
                                wayStart.Add(refs.Count);
                                refs.AddRange(nodes);
                                wayId.Add(w.Id);
                                wAttr["hw"].Add((byte)a.Hw); wAttr["cls"].Add((byte)a.Cls); wAttr["sub"].Add((byte)a.Sub);
                                wAttr["dirn"].Add((byte)a.Dirn); wAttr["rev"].Add((byte)a.Rev); wAttr["jn"].Add((byte)a.Jn);
                                wAttr["flags"].Add((byte)a.Flags); wAttr["spd"].Add((byte)a.Spd);
                                wName.Add(S[a.Name]);
                                wRef.Add(S[a.Ref]);
                            }
                            continue;
                        }
                    }
                }
                var pt = Poi.PoiTags(tags);
                if (pt != null && pt.Value.cat == null)
                    drop.Add(PoiDropReason(pt.Value.value));
                else if (pt != null && w.Nodes.Length >= 3)
                {
                    poiWayRefs.Add(w.Nodes);
                    poiWayMeta.Add(Record(S, w.Id, 'w', pt.Value, tags));
                }
            }
        }
        wayStart.Add(refs.Count);
        int nWays = wayId.Count;
        var refsA = refs.ToArray();
        log($"pass A: {nWays:N0} road ways ({nHw:N0} highway ways seen), {refsA.Length:N0} node refs, " +
            $"{restrictions.Count:N0} restrictions, {poiWayMeta.Count:N0} POI areas  [{sw.Elapsed.TotalSeconds:F0}s]");

        // ------------------------------------------------ passes B + C: node coordinates and POI nodes
        var need = refsA.Concat(poiWayRefs.SelectMany(x => x)).Distinct().ToArray();
        Array.Sort(need);
        var nids = new List<long>(); var nxs = new List<int>(); var nys = new List<int>();
        var pois = new List<PoiRec>();
        foreach (var ex in extracts)
        using (var prog = Progress.Bytes("nodes " + System.IO.Path.GetFileName(ex.Path).Replace("-latest.osm.pbf", ""), new FileInfo(ex.Path).Length))
        foreach (var blk in Pbf.Read(ex.Path, new PbfFilter { Nodes = true, NodeIds = need, NodeKeys = ex.MajorOnly ? null : new HashSet<string>(Poi.Keys) }, progress: prog))
        {
            foreach (var n in blk.Nodes)
            {
                if (n.Valid && Array.BinarySearch(need, n.Id) >= 0)
                {
                    var (x, y) = Geo.FromLonLat(n.Lon, n.Lat);
                    // outside the disc's root square: left out, so its ways count as having missing nodes
                    if (ex.MajorOnly && !(x >= rx0 && x < rx0 + rside && y >= ry0 && y < ry0 + rside)) continue;
                    nids.Add(n.Id); nxs.Add(x); nys.Add(y);
                }
                if (n.Tags == null || ex.MajorOnly) continue;
                if (multi && !seenPoiNodes.Add(n.Id)) continue;
                var pt = Poi.PoiTags(n.Tags);
                if (pt != null && pt.Value.cat == null) drop.Add(PoiDropReason(pt.Value.value));
                else if (pt != null && n.Valid)
                {
                    var rec = Record(S, n.Id, 'n', pt.Value, n.Tags);
                    (rec.X, rec.Y) = Geo.FromLonLat(n.Lon, n.Lat);
                    pois.Add(rec);
                }
            }
        }
        var order = Enumerable.Range(0, nids.Count).OrderBy(i => nids[i]).ToArray();   // stable
        if (multi)                                     // a node in two extracts: keep the first file's copy
            order = order.Where((i, k) => k == 0 || nids[order[k - 1]] != nids[i]).ToArray();
        var ids = order.Select(i => nids[i]).ToArray();
        var X = order.Select(i => nxs[i]).ToArray();
        var Y = order.Select(i => nys[i]).ToArray();
        log($"pass B: {ids.Length:N0} / {need.Length:N0} node locations  [{sw.Elapsed.TotalSeconds:F0}s]");

        int SearchSorted(long v)
        {
            int i = Array.BinarySearch(ids, v);
            if (i >= 0) { while (i > 0 && ids[i - 1] == v) i--; return i; }
            return ~i;
        }

        for (int i = 0; i < poiWayRefs.Count; i++)
        {
            long sx = 0, sy = 0; int cnt = 0;
            foreach (long r in poiWayRefs[i])
            {
                int k = Math.Min(SearchSorted(r), ids.Length - 1);
                if (ids.Length > 0 && ids[k] == r) { sx += X[k]; sy += Y[k]; cnt++; }
            }
            if (cnt == 0) { drop.Add("POI area without node locations"); continue; }
            var rec = poiWayMeta[i];
            rec.X = (int)((double)sx / cnt);
            rec.Y = (int)((double)sy / cnt);
            pois.Add(rec);
        }
        log($"pass C: {pois.Count:N0} POIs  [{sw.Elapsed.TotalSeconds:F0}s]");

        // ------------------------------------------------ split ways into links
        int nRefs = refsA.Length;
        var ci = new int[nRefs];
        var present = new bool[nRefs];
        for (int i = 0; i < nRefs; i++)
        {
            int k = SearchSorted(refsA[i]);
            if (k >= ids.Length) k = 0;
            ci[i] = k;
            present[i] = ids.Length > 0 && ids[k] == refsA[i];
        }
        var wayOf = new int[nRefs];
        for (int w = 0; w < nWays; w++) for (long i = wayStart[w]; i < wayStart[w + 1]; i++) wayOf[i] = w;
        var badWay = new bool[nWays];
        for (int i = 0; i < nRefs; i++) if (!present[i]) badWay[wayOf[i]] = true;
        int nBad = badWay.Count(b => b);
        if (nBad > 0) drop.Add("way with nodes missing from the extract", nBad);
        var keep = new bool[nRefs];
        for (int i = 0; i < nRefs; i++) keep[i] = !badWay[wayOf[i]];
        var counts = new Dictionary<long, int>();
        for (int i = 0; i < nRefs; i++) if (keep[i]) counts[refsA[i]] = counts.GetValueOrDefault(refsA[i]) + 1;
        var split = new bool[nRefs];
        for (int i = 0; i < nRefs; i++)
        {
            bool shared = keep[i] && counts[refsA[i]] > 1;
            split[i] = shared && keep[i];
        }
        for (int w = 0; w < nWays; w++)
        {
            if (wayStart[w + 1] <= wayStart[w]) continue;
            if (keep[wayStart[w]]) split[wayStart[w]] = true;
            if (keep[wayStart[w + 1] - 1]) split[wayStart[w + 1] - 1] = true;
        }
        (List<int> a, List<int> b) Pairs()
        {
            var P = new List<int>();
            for (int i = 0; i < nRefs; i++) if (split[i]) P.Add(i);
            var A = new List<int>(); var B = new List<int>();
            for (int k = 0; k + 1 < P.Count; k++) if (wayOf[P[k]] == wayOf[P[k + 1]]) { A.Add(P[k]); B.Add(P[k + 1]); }
            return (A, B);
        }
        for (int it = 0; it < 3; it++)
        {
            var (A, B) = Pairs();
            var mids = new List<int>();
            int nLoop = 0;
            for (int k = 0; k < A.Count; k++)
            {
                if (refsA[A[k]] != refsA[B[k]]) continue;
                nLoop++;
                int mid = (A[k] + B[k]) / 2;
                if (mid > A[k] && mid < B[k]) mids.Add(mid);
            }
            if (nLoop == 0) break;
            if (mids.Count == 0) { drop.Add("degenerate loop link", nLoop); break; }
            foreach (int mid in mids) split[mid] = true;
        }
        var (A0, B0) = Pairs();
        var la = new List<int>(); var lb = new List<int>();
        int notOk = 0;
        for (int k = 0; k < A0.Count; k++)
        {
            if (refsA[A0[k]] != refsA[B0[k]]) { la.Add(A0[k]); lb.Add(B0[k]); } else notOk++;
        }
        if (notOk > 0) drop.Add("zero-length or loop link", notOk);
        int nLinks = la.Count;
        var lw = la.Select(i => wayOf[i]).ToArray();

        // graph nodes
        var gid = la.Select(i => refsA[i]).Concat(lb.Select(i => refsA[i])).Distinct().ToArray();
        Array.Sort(gid);
        int Gi(long v) => Array.BinarySearch(gid, v);
        var frm = la.Select(i => Gi(refsA[i])).ToArray();
        var to = lb.Select(i => Gi(refsA[i])).ToArray();
        var NX = gid.Select(g => X[SearchSorted(g)]).ToArray();
        var NY = gid.Select(g => Y[SearchSorted(g)]).ToArray();

        // shape points
        var ns = new int[nLinks];
        var shpStart = new long[nLinks];
        long acc = 0;
        for (int k = 0; k < nLinks; k++) { ns[k] = lb[k] - la[k] - 1; shpStart[k] = acc; acc += ns[k]; }
        var SX = new int[acc]; var SY = new int[acc];
        for (int k = 0; k < nLinks; k++)
            for (int j = 0; j < ns[k]; j++) { SX[shpStart[k] + j] = X[ci[la[k] + 1 + j]]; SY[shpStart[k] + j] = Y[ci[la[k] + 1 + j]]; }

        // lengths: one running sum over every node reference, as numpy does
        var lon = new double[nRefs]; var lat = new double[nRefs];
        for (int i = 0; i < nRefs; i++) (lon[i], lat[i]) = Geo.ToLonLat(X[ci[i]], Y[ci[i]]);
        var cum = new double[nRefs];
        double c = 0;
        for (int i = 1; i < nRefs; i++) { c += Geo.Haversine(lon[i - 1], lat[i - 1], lon[i], lat[i]); cum[i] = c; }
        var lengthM = new int[nLinks];
        var brg0 = new byte[nLinks]; var brg1 = new byte[nLinks];
        for (int k = 0; k < nLinks; k++)
        {
            int a = la[k], b = lb[k];
            lengthM[k] = (int)Math.Round(cum[b] - cum[a]);
            brg0[k] = Geo.Bearing256Np(lon[a], lat[a], lon[a + 1], lat[a + 1]);
            brg1[k] = Geo.Bearing256Np(lon[b], lat[b], lon[b - 1], lat[b - 1]);
        }

        var L = AttrKeys.ToDictionary(k => k, k => lw.Select(w => wAttr[k][w]).ToArray());
        var linkWay = lw.Select(w => wayId[w]).ToArray();
        var linkName = lw.Select(w => wName[w]).ToArray();
        var linkRef = lw.Select(w => wRef[w]).ToArray();
        var form = Attrs.FormOfWay(L["hw"], L["cls"], L["dirn"], frm, to, gid.Length);
        log($"links: {nLinks:N0}, graph nodes: {gid.Length:N0}, shape points: {SX.Length:N0}  [{sw.Elapsed.TotalSeconds:F0}s]");

        // ------------------------------------------------ turn restrictions
        var tr = MapRestrictions(restrictions, linkWay, frm, to, gid, drop);
        log($"restrictions mapped: {tr["from_link"].Length:N0} / {restrictions.Count:N0}");

        var m = new RoadModel { Strings = S.Items };
        m.Arrays.AddRange(
        [
            ("node_x", NX), ("node_y", NY), ("node_osm", gid), ("shape_x", SX), ("shape_y", SY),
            ("link_frm", frm), ("link_to", to), ("link_shape_start", shpStart), ("link_shape_count", ns),
            ("link_length_m", lengthM), ("link_brg0", brg0), ("link_brg1", brg1), ("link_way", linkWay),
            ("link_name", linkName), ("link_ref", linkRef),
        ]);
        foreach (var k in AttrKeys) m.Arrays.Add(("link_" + k, L[k]));
        m.Arrays.Add(("link_form", form));
        foreach (var k in (string[])["from_link", "via_node", "to_link", "only", "osm"]) m.Arrays.Add(("tr_" + k, tr[k]));
        m.Arrays.Add(("poi_osm", pois.Select(p => p.Osm).ToArray()));
        m.Arrays.Add(("poi_x", pois.Select(p => p.X).ToArray()));
        m.Arrays.Add(("poi_y", pois.Select(p => p.Y).ToArray()));
        m.Arrays.Add(("poi_key", pois.Select(p => p.Key).ToArray()));
        m.Arrays.Add(("poi_value", pois.Select(p => p.Value).ToArray()));
        m.Arrays.Add(("poi_cat", pois.Select(p => p.Cat).ToArray()));
        m.Arrays.Add(("poi_name", pois.Select(p => p.Name).ToArray()));
        m.Arrays.Add(("poi_brand", pois.Select(p => p.Brand).ToArray()));
        m.Arrays.Add(("poi_phone", pois.Select(p => p.Phone).ToArray()));
        m.Arrays.Add(("poi_housenumber", pois.Select(p => p.Housenumber).ToArray()));
        m.Arrays.Add(("poi_street", pois.Select(p => p.Street).ToArray()));
        m.Arrays.Add(("poi_city", pois.Select(p => p.City).ToArray()));
        m.Arrays.Add(("poi_postcode", pois.Select(p => p.Postcode).ToArray()));
        m.Arrays.Add(("poi_is_way", pois.Select(p => p.Kind == 'w').ToArray()));
        m.Arrays.Add(("poi_code", pois.Select(p => (byte)Poi.CodeOf[S.Items[p.Cat]]).ToArray()));

        m.Stats = MakeStats(pbfs, profile, drop, L, lengthM, linkName, pois, S, gid.Length, SX.Length, nWays,
                            restrictions.Count, tr["from_link"].Length);
        m.Stats["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
        return m;
    }

    static string PoiDropReason(string kv) =>
        kv.Split('=')[0] is "amenity" or "shop" or "tourism" ? $"POI tag not used: {kv}" : "other tagged object (not a navigation POI)";

    static Dictionary<string, long[]> MapRestrictions(List<(long rid, long fr, long via, long to, string kind)> rs,
        long[] linkWay, int[] frm, int[] to, long[] gid, Dropped drop)
    {
        var o = new Dictionary<string, List<long>> { ["from_link"] = new(), ["via_node"] = new(), ["to_link"] = new(), ["only"] = new(), ["osm"] = new() };
        if (rs.Count > 0)
        {
            var byWay = new Dictionary<long, List<int>>();
            for (int i = 0; i < linkWay.Length; i++)
            {
                if (!byWay.TryGetValue(linkWay[i], out var l)) byWay[linkWay[i]] = l = new();
                l.Add(i);
            }
            foreach (var (rid, fw, via, tw, kind) in rs)
            {
                int vi = Array.BinarySearch(gid, via);
                if (vi < 0) { drop.Add("restriction whose via node is not a junction"); continue; }
                List<int> Touching(long w) => byWay.TryGetValue(w, out var l) ? l.Where(i => frm[i] == vi || to[i] == vi).ToList() : [];
                var f = Touching(fw); var t = Touching(tw);
                if (f.Count != 1 || t.Count != 1) { drop.Add("restriction whose ways are not roads here (or ambiguous)"); continue; }
                o["from_link"].Add(f[0]); o["via_node"].Add(vi); o["to_link"].Add(t[0]);
                o["only"].Add(kind.StartsWith("only_") ? 1 : 0); o["osm"].Add(rid);
            }
        }
        return o.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    static JsonObject MakeStats(IReadOnlyList<string> pbfs, string profile, Dropped drop, Dictionary<string, byte[]> L, int[] lengthM, int[] linkName,
        List<PoiRec> pois, Strings S, int nNodes, int nShape, int nWays, int nRestr, int nMapped)
    {
        var cls = L["cls"];
        double Km(IEnumerable<int> idx) => Math.Round(idx.Sum(i => (long)lengthM[i]) / 1000.0, 1);
        var byCls = new JsonObject();
        for (int c = 0; c < 7; c++)
        {
            var idx = Enumerable.Range(0, cls.Length).Where(i => cls[i] == c).ToList();
            if (idx.Count > 0) byCls[c.ToString()] = new JsonObject { ["links"] = idx.Count, ["km"] = Km(idx) };
        }
        var byHw = new JsonObject();
        foreach (var code in L["hw"].Distinct().OrderBy(x => x))
        {
            var idx = Enumerable.Range(0, cls.Length).Where(i => L["hw"][i] == code).ToList();
            byHw[Attrs.HighwayCodes[code]] = new JsonObject { ["links"] = idx.Count, ["km"] = Km(idx) };
        }
        var poiCat = new Dictionary<string, int>();
        var catOrder = new List<string>();
        foreach (var p in pois)
        {
            var k = S.Items[p.Cat];
            if (!poiCat.ContainsKey(k)) { poiCat[k] = 0; catOrder.Add(k); }
            poiCat[k]++;
        }
        var cats = new JsonObject();
        foreach (var k in catOrder.OrderByDescending(k => poiCat[k])) cats[k] = poiCat[k];
        return new JsonObject
        {
            ["pbf"] = string.Join(",", pbfs.Select(Path.GetFullPath)), ["pbf_bytes"] = pbfs.Sum(p => new FileInfo(p).Length), ["profile"] = profile,
            ["dropped"] = drop.ToJson(),
            ["road_ways"] = nWays, ["links"] = cls.Length, ["graph_nodes"] = nNodes, ["shape_points"] = nShape,
            ["road_km"] = Math.Round(lengthM.Sum(x => (long)x) / 1000.0, 1),
            ["links_by_class"] = byCls, ["links_by_highway"] = byHw,
            ["oneway_links"] = L["dirn"].Count(d => d == 1), ["closed_links"] = L["dirn"].Count(d => d == 3),
            ["named_links"] = linkName.Count(n => n > 0),
            ["restrictions"] = new JsonObject { ["in_osm"] = nRestr, ["mapped"] = nMapped },
            ["pois"] = pois.Count, ["pois_named"] = pois.Count(p => p.Name != 0),
            ["poi_categories"] = cats,
            ["long_links_over_65535m"] = lengthM.Count(x => x > 65535),
        };
    }
}
