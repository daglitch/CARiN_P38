namespace Osm2Carin.Core.Osm;

/// <summary>OSM tags -> CARiN street-link attributes; port of tools/osm2carin/attrs.py (see there for the
/// field meanings and how the mapping was matched to the original disc's Nevada street level).</summary>
public static class Attrs
{
    public static readonly (string hw, int cls, int sub, int kmh)[] Highways =
    [
        ("motorway", 0, 0, 113), ("motorway_link", 3, 0, 64), ("trunk", 1, 0, 80), ("trunk_link", 3, 0, 64),
        ("primary", 2, 0, 80), ("primary_link", 3, 0, 64), ("secondary", 3, 0, 64), ("secondary_link", 4, 0, 48),
        ("tertiary", 4, 0, 64), ("tertiary_link", 4, 0, 48), ("unclassified", 4, 0, 48), ("residential", 4, 0, 48),
        ("road", 4, 0, 48), ("living_street", 5, 0, 32), ("service", 6, 1, 48), ("track", 6, 1, 48),
        ("pedestrian", 6, 5, 8),
    ];
    public static readonly string[] HighwayCodes = Highways.Select(h => h.hw).ToArray();
    public static readonly Dictionary<string, int> HwCode = HighwayCodes.Select((h, i) => (h, i)).ToDictionary(p => p.h, p => p.i);
    public static readonly HashSet<int> LinkHw = Highways.Where(h => h.hw.EndsWith("_link")).Select(h => HwCode[h.hw]).ToHashSet();

    public sealed record Profile(HashSet<string> Exclude, string Tracks, HashSet<string> ExcludeService);

    public static readonly Dictionary<string, Profile> Profiles = new()
    {
        ["default"] = new(["pedestrian"], "selected", ["driveway", "parking_aisle", "drive-through", "emergency_access", "private", "yes"]),
        ["full"] = new([], "all", []),
        ["compact"] = new(["service", "track", "pedestrian"], "none", []),
    };

    static readonly double[] SpeedX = [0, 8, 32, 48, 64, 80, 97, 113, 130];
    static readonly double[] SpeedY = [1, 2, 5, 11, 16, 22, 26, 31, 31];

    /// <summary>np.interp then Python round (half to even).</summary>
    public static int SpeedCategory(double kmh)
    {
        double v;
        if (kmh <= SpeedX[0]) v = SpeedY[0];
        else if (kmh >= SpeedX[^1]) v = SpeedY[^1];
        else
        {
            int j = 1;
            while (SpeedX[j] < kmh) j++;
            double x0 = SpeedX[j - 1], x1 = SpeedX[j], y0 = SpeedY[j - 1], y1 = SpeedY[j];
            double slope = (y1 - y0) / (x1 - x0);
            v = slope * (kmh - x0) + y0;
            if (kmh == x1) v = y1;
        }
        return (int)Math.Round(v);
    }

    public sealed class Way
    {
        public int Hw, Cls, Sub, Dirn, Rev, Jn, Flags, Spd;
        public string Name = "", Ref = "";
    }

    static bool Yes(string v) => v is "yes" or "true" or "1";
    static string Or(params string?[] vals) { foreach (var v in vals) if (!string.IsNullOrEmpty(v)) return v; return ""; }

    /// <summary>Per-way attributes, or null with the reason in skip.</summary>
    public static Way? WayAttrs(string hw, (int cls, int sub, int kmh) spec, Tags tags, Profile prof, out string skip)
    {
        skip = "";
        var (cls, sub, kmh) = spec;
        string? G(string k) => tags.Get(k);
        if (G("area") == "yes") { skip = "area=yes"; return null; }
        if (hw == "service")
        {
            string st = G("service") ?? "";
            var excl = prof.ExcludeService;
            if (excl.Contains(st) || (excl.Count > 0 && G("access") is "private" or "no"))
            {
                skip = $"service={(st == "" ? "-" : st)} access={(string.IsNullOrEmpty(G("access")) ? "-" : G("access"))}";
                return null;
            }
            if (st == "alley") (cls, sub, kmh) = (5, 0, 32);
        }
        if (hw == "track" && prof.Tracks == "selected" &&
            !(!string.IsNullOrEmpty(G("tiger:cfcc")) || !string.IsNullOrEmpty(G("name")) || G("tracktype") is "grade1" or "grade2" or "grade3"))
        {
            skip = "no name / TIGER origin / grade 1-3 (profile default)";
            return null;
        }
        string access = Or(G("motor_vehicle"), G("motorcar"), G("vehicle"), G("access"));
        string oneway = G("oneway") ?? "";
        string jnTag = G("junction") ?? "";
        int dirn = 0, rev = 0;
        if (access == "no") dirn = 3;
        else if (oneway is "-1" or "reverse") { dirn = 1; rev = 1; }
        else if (Yes(oneway) || (oneway == "" && (jnTag is "roundabout" or "circular" || hw == "motorway"))) dirn = 1;
        else if (oneway == "reversible") dirn = 3;
        if (access is "private" or "customers" or "delivery" or "destination" && cls >= 5) { cls = 6; sub = 3; }
        int flags = 0;
        if (Yes(G("toll") ?? "")) flags |= 1;
        if ((Or(G("bridge")) is var b) && b != "" && b != "no") flags |= 2;
        if (((Or(G("tunnel")) is var t) && t != "" && t != "no") || G("covered") == "yes") flags |= 4;
        if ((G("surface") ?? "") is "unpaved" or "dirt" or "gravel" or "ground" or "sand" or "grass" or "compacted"
            or "fine_gravel" or "earth" or "mud") flags |= 8;
        return new Way
        {
            Hw = HwCode[hw], Cls = cls, Sub = sub, Dirn = dirn, Rev = rev, Jn = jnTag is "roundabout" or "circular" ? 6 : 0,
            Flags = flags, Spd = SpeedCategory(kmh), Name = Py.Lower(G("name") ?? ""), Ref = Py.Lower(G("ref") ?? ""),
        };
    }

    /// <summary>Form of way per link (+0x0B &amp; 0x0F), slip roles from the graph (attrs.form_of_way).</summary>
    public static byte[] FormOfWay(byte[] hw, byte[] cls, byte[] dirn, int[] frm, int[] to, int nNodes)
    {
        int n = hw.Length;
        var form = new byte[n];
        int mw = HwCode["motorway"], tert = HwCode["tertiary"], trunkLink = HwCode["trunk_link"], mwLink = HwCode["motorway_link"];
        bool[] isLink = new bool[n];
        for (int i = 0; i < n; i++)
        {
            form[i] = (byte)(cls[i] <= 3 || hw[i] == tert ? 0xB : 0xC);
            if (hw[i] == mw) form[i] = 0;
            if (dirn[i] == 1 && (hw[i] == HwCode["trunk"] || hw[i] == HwCode["primary"])) form[i] = 4;
            isLink[i] = LinkHw.Contains(hw[i]);
        }
        if (!isLink.Any(b => b)) return form;
        var mainMw = new bool[nNodes];
        var mainAny = new bool[nNodes];
        var mainHi = new bool[nNodes];
        for (int i = 0; i < n; i++)
        {
            foreach (int nd in (int[])[frm[i], to[i]])
            {
                if (hw[i] == mw) mainMw[nd] = true;
                if (!isLink[i]) mainAny[nd] = true;
                if (!isLink[i] && cls[i] <= 3) mainHi[nd] = true;
            }
        }
        var idx = Enumerable.Range(0, n).Where(i => isLink[i]).ToArray();
        var outS = new Dictionary<int, List<int>>();
        var inS = new Dictionary<int, List<int>>();
        void Add(Dictionary<int, List<int>> d, int k, int v) { if (!d.TryGetValue(k, out var l)) d[k] = l = new(); l.Add(v); }
        foreach (int i in idx)
        {
            Add(outS, frm[i], to[i]);
            Add(inS, to[i], frm[i]);
            if (dirn[i] == 0) { Add(outS, to[i], frm[i]); Add(inS, frm[i], to[i]); }
        }

        bool Reaches(int node, Dictionary<int, List<int>> adj, bool[] main, int limit = 500)
        {
            var seen = new HashSet<int> { node };
            var stack = new List<int> { node };
            while (stack.Count > 0 && seen.Count < limit)
            {
                int nd = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (mainAny[nd])
                {
                    if (main[nd]) return true;
                    continue;
                }
                if (adj.TryGetValue(nd, out var l))
                    foreach (int nx in l)
                        if (seen.Add(nx)) stack.Add(nx);
            }
            return false;
        }

        foreach (int i in idx)
        {
            if (hw[i] == mwLink)
            {
                bool s = Reaches(frm[i], inS, mainMw), e = Reaches(to[i], outS, mainMw);
                form[i] = (byte)(s && e ? 3 : s ? 2 : 1);
            }
            else if (hw[i] == trunkLink)
            {
                bool s = Reaches(frm[i], inS, mainHi), e = Reaches(to[i], outS, mainHi);
                form[i] = (byte)(s && e ? 0xA : s ? 9 : 8);
            }
            else form[i] = 0xB;
        }
        return form;
    }
}

/// <summary>Python string semantics where they matter for identical output.</summary>
public static class Py
{
    /// <summary>str.lower(): full Unicode lower-casing (close to ToLowerInvariant; differs only on
    /// a handful of special letters such as U+0130).</summary>
    public static string Lower(string s) => s.ToLowerInvariant();
}
