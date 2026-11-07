using System.Text;

namespace Osm2Carin.Core.Addr;

/// <summary>House numbers for road links, from TIGER address ranges (Phase 3d).
///
/// For a link (its street name and its points, start to end) the matcher finds the TIGER edge of that
/// name under the middle of the link (within MidM metres, roughly parallel), then the nearest edge of
/// that name at each end of the link, and reads the numbers there: TIGER's from / to are the numbers
/// at the start / end of the edge as digitised, interpolated linearly along it, and its left / right
/// sides swap when the edge runs the other way from the link.
///
/// The result is a 0x04 record (03-road-network.md §6.4): u16 f0 f1 f2 f3 f4, where (f0, f2) is side A
/// at the start / end of the link, (f1, f3) side B, 0x7FFF = none, and f4 the scheme (0 none, 1 mixed,
/// 2 each side one parity). Side A is the left of the start -> end direction (OpenCarin: 91-95% on
/// Bari; checked against this disc by HouseNumberTests).</summary>
public sealed class HouseNumbers
{
    public const int None = 0x7FFF;
    public double MidM = 30, EndM = 40, MidCos = 0.7, EndCos = 0.5;
    public bool LeftIsA = true;

    readonly List<TigerEdge> edges;
    readonly string[] key, core;
    readonly float[][] cum;                      // cumulative length along each edge, in (scaled) units
    readonly Dictionary<long, List<(int e, int s)>> grid = new();
    const int Cell = 10000;                      // ~200 m

    long statAsked, statMid, statNumbered, statCore;
    public string Summary => $"{statAsked:N0} named links asked, {statMid:N0} matched a TIGER street ({statCore:N0} by the name without " +
                             $"direction / type), {statNumbered:N0} got numbers";
    public long Numbered => Interlocked.Read(ref statNumbered);

    public int EdgeCount => edges.Count;

    public HouseNumbers(List<TigerEdge> edges)
    {
        this.edges = edges;
        key = new string[edges.Count]; core = new string[edges.Count]; cum = new float[edges.Count][];
        for (int e = 0; e < edges.Count; e++)
        {
            var E = edges[e];
            key[e] = Key(E.Name); core[e] = Core(key[e]);
            double kx = Kx(E.Y[0]);
            var c = new float[E.X.Length];
            for (int s = 1; s < E.X.Length; s++)
                c[s] = c[s - 1] + (float)Math.Sqrt(Sq((E.X[s] - E.X[s - 1]) * kx) + Sq(E.Y[s] - E.Y[s - 1]));
            cum[e] = c;
            for (int s = 0; s + 1 < E.X.Length; s++)
            {
                int cx0 = Math.Min(E.X[s], E.X[s + 1]) / Cell, cx1 = Math.Max(E.X[s], E.X[s + 1]) / Cell;
                int cy0 = FloorDiv(Math.Min(E.Y[s], E.Y[s + 1])), cy1 = FloorDiv(Math.Max(E.Y[s], E.Y[s + 1]));
                for (int cx = cx0; cx <= cx1; cx++)
                    for (int cy = cy0; cy <= cy1; cy++)
                    {
                        long k = ((long)cx << 32) | (uint)cy;
                        if (!grid.TryGetValue(k, out var l)) grid[k] = l = new();
                        l.Add((e, s));
                    }
            }
        }
    }

    public static HouseNumbers Load(IEnumerable<string> zips, Action<string>? log = null)
    {
        var all = new List<TigerEdge>();
        var files = zips.ToList();
        using var prog = new Progress("TIGER", files.Count, "files");
        var parts = new List<TigerEdge>[files.Count];
        Parallel.For(0, files.Count, i => { parts[i] = Tiger.ReadZip(files[i]); prog.Add(); });
        prog.Dispose();
        foreach (var p in parts) all.AddRange(p);
        var hn = new HouseNumbers(all);
        log?.Invoke($"house numbers: {all.Count:N0} TIGER address ranges from {files.Count} county files");
        return hn;
    }

    static int FloorDiv(int v) => v >= 0 ? v / Cell : (v - Cell + 1) / Cell;
    static double Sq(double v) => v * v;
    static double Kx(long y) => Math.Cos(y * Geo.UnitDeg * Math.PI / 180);
    const double MetresPerUnit = Geo.UnitDeg * 111195.0;

    // ---- names ---------------------------------------------------------------------------------

    static readonly Dictionary<string, string> Abbr = new()
    {
        ["north"] = "n", ["south"] = "s", ["east"] = "e", ["west"] = "w",
        ["northeast"] = "ne", ["northwest"] = "nw", ["southeast"] = "se", ["southwest"] = "sw",
        ["street"] = "st", ["avenue"] = "ave", ["av"] = "ave", ["drive"] = "dr", ["road"] = "rd", ["boulevard"] = "blvd",
        ["lane"] = "ln", ["court"] = "ct", ["place"] = "pl", ["circle"] = "cir", ["parkway"] = "pkwy", ["highway"] = "hwy",
        ["terrace"] = "ter", ["trail"] = "trl", ["square"] = "sq", ["expressway"] = "expy", ["freeway"] = "fwy",
        ["mount"] = "mt", ["mountain"] = "mtn", ["fort"] = "ft", ["saint"] = "st", ["route"] = "rte", ["point"] = "pt",
        ["heights"] = "hts", ["center"] = "ctr", ["centre"] = "ctr", ["crossing"] = "xing", ["junction"] = "jct",
        ["alley"] = "aly", ["plaza"] = "plz", ["ridge"] = "rdg", ["valley"] = "vly", ["view"] = "vw", ["creek"] = "crk",
        ["canyon"] = "cyn", ["estates"] = "est", ["glen"] = "gln", ["grove"] = "grv", ["harbor"] = "hbr", ["hill"] = "hl",
        ["hills"] = "hls", ["lake"] = "lk", ["meadow"] = "mdw", ["meadows"] = "mdws", ["cove"] = "cv", ["crescent"] = "cres",
        ["extension"] = "ext", ["landing"] = "lndg", ["station"] = "sta", ["village"] = "vlg", ["vista"] = "vis",
        ["county"] = "co", ["causeway"] = "cswy", ["court's"] = "ct", ["bypass"] = "byp", ["motorway"] = "mtwy",
        ["turnpike"] = "tpke", ["throughway"] = "trwy", ["gardens"] = "gdns", ["garden"] = "gdn", ["springs"] = "spgs",
        ["spring"] = "spg", ["island"] = "is", ["shores"] = "shrs", ["shore"] = "shr", ["park"] = "park",
    };
    static readonly HashSet<string> Directions = ["n", "s", "e", "w", "ne", "nw", "se", "sw"];
    static readonly HashSet<string> Types = ["st", "ave", "dr", "rd", "blvd", "ln", "ct", "pl", "cir", "pkwy", "hwy", "ter", "trl",
        "sq", "expy", "fwy", "way", "loop", "aly", "plz", "path", "walk", "row", "run", "pass", "pike", "xing", "cv", "cres",
        "byp", "tpke", "rte"];

    /// <summary>Comparable street name: lower-case ASCII words, with street types and directions abbreviated
    /// as TIGER writes them ("South Edmonds Drive" and "S Edmonds Dr" both give "s edmonds dr").</summary>
    public static string Key(string name)
    {
        var s = Roads.Tiles.CleanName(name);
        var sb = new StringBuilder();
        foreach (var w in s.Replace('-', ' ').Replace('.', ' ').Replace('\'', ' ').Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Abbr.TryGetValue(w, out var a) ? a : w);
        }
        return sb.ToString();
    }

    /// <summary>The key without leading / trailing directions and the trailing street type ("s edmonds dr" -> "edmonds").</summary>
    public static string Core(string key)
    {
        var w = key.Split(' ').ToList();
        if (w.Count > 1 && Directions.Contains(w[0])) w.RemoveAt(0);
        if (w.Count > 1 && Directions.Contains(w[^1])) w.RemoveAt(w.Count - 1);
        if (w.Count > 1 && Types.Contains(w[^1])) w.RemoveAt(w.Count - 1);
        return string.Join(' ', w);
    }

    // ---- matching ------------------------------------------------------------------------------

    readonly record struct Hit(int E, double T, double Dist, bool Same);

    /// <summary>The nearest segment of an edge whose name matches, within maxM metres of p and with
    /// |cos| of the angle to dir at least minCos.</summary>
    Hit? Nearest(string k, bool byCore, (double x, double y) p, (double x, double y) dir, double maxM, double minCos)
    {
        double kx = Kx((long)p.y);
        double dl = Math.Sqrt(Sq(dir.x * kx) + Sq(dir.y));
        if (dl == 0) return null;
        double dxu = dir.x * kx / dl, dyu = dir.y / dl;
        double maxU = maxM / MetresPerUnit;
        int cx = (int)Math.Floor(p.x / Cell), cy = (int)Math.Floor(p.y / Cell);
        Hit? best = null;
        for (int ix = cx - 1; ix <= cx + 1; ix++)
            for (int iy = cy - 1; iy <= cy + 1; iy++)
            {
                if (!grid.TryGetValue(((long)ix << 32) | (uint)iy, out var l)) continue;
                foreach (var (e, s) in l)
                {
                    if ((byCore ? core[e] : key[e]) != k) continue;
                    var E = edges[e];
                    double ax = E.X[s] * kx, ay = E.Y[s], bx = E.X[s + 1] * kx, by = E.Y[s + 1];
                    double vx = bx - ax, vy = by - ay, len = Math.Sqrt(vx * vx + vy * vy);
                    if (len == 0) continue;
                    double c = (vx * dxu + vy * dyu) / len;
                    if (Math.Abs(c) < minCos) continue;
                    double px = p.x * kx, py = p.y;
                    double u = Math.Clamp(((px - ax) * vx + (py - ay) * vy) / (len * len), 0, 1);
                    double d = Math.Sqrt(Sq(px - (ax + u * vx)) + Sq(py - (ay + u * vy)));
                    if (d > maxU || (best is Hit h && d >= h.Dist)) continue;
                    var cm = cum[e];
                    double t = cm[^1] > 0 ? (cm[s] + u * (cm[s + 1] - cm[s])) / cm[^1] : 0;
                    best = new Hit(e, t, d, c > 0);
                }
            }
        return best;
    }

    static int? At(TigerSide? side, double t, out char parity)
    {
        parity = ' ';
        if (side is not TigerSide s) return null;
        parity = s.Parity;
        double v = s.From + (s.To - s.From) * t;
        int lo = Math.Min(s.From, s.To), hi = Math.Max(s.From, s.To);
        int n;
        if (s.Parity is 'E' or 'O')
        {
            int par = s.Parity == 'O' ? 1 : 0;
            n = (int)Math.Round((v - par) / 2, MidpointRounding.AwayFromZero) * 2 + par;
            if (n < lo) n += 2;
            if (n > hi) n -= 2;
            if (n < lo || n > hi) n = s.From;
        }
        else n = Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), lo, hi);
        return n;
    }

    /// <summary>The 0x04 record of a link: [f0, f1, f2, f3, scheme], or null when it gets no numbers.</summary>
    public ushort[]? Lookup(string name, IReadOnlyList<(long x, long y)> pts)
    {
        if (pts.Count < 2 || string.IsNullOrWhiteSpace(name)) return null;
        Interlocked.Increment(ref statAsked);
        string k = Key(name);
        var (mp, md) = Middle(pts);
        bool byCore = false;
        if (Nearest(k, false, mp, md, MidM, MidCos) == null)
        {
            k = Core(k); byCore = true;
            if (k == "" || Nearest(k, true, mp, md, MidM, MidCos) == null) return null;
            Interlocked.Increment(ref statCore);
        }
        Interlocked.Increment(ref statMid);
        var p0 = ((double)pts[0].x, (double)pts[0].y); var p1 = ((double)pts[^1].x, (double)pts[^1].y);
        var d0 = Dir(pts, 0); var d1 = Dir(pts, pts.Count - 2);
        var h0 = Nearest(k, byCore, p0, d0, EndM, EndCos);
        var h1 = Nearest(k, byCore, p1, d1, EndM, EndCos);
        if (h0 is not Hit a || h1 is not Hit b) return null;
        TigerSide? SideOf(Hit h, bool left) => left == h.Same ? edges[h.E].L : edges[h.E].R;
        var o = new ushort[] { None, None, None, None, 0 };
        bool any = false, mixed = false;
        for (int side = 0; side < 2; side++)
        {
            bool left = (side == 0) == LeftIsA;
            int? s = At(SideOf(a, left), a.T, out char ps), e = At(SideOf(b, left), b.T, out char pe);
            if (s is not int sv || e is not int ev) continue;
            o[side] = (ushort)sv; o[side + 2] = (ushort)ev;
            any = true;
            if (ps is not ('E' or 'O') || pe != ps || sv % 2 != ev % 2) mixed = true;
        }
        if (!any) return null;
        o[4] = (ushort)(mixed ? 1 : 2);
        Interlocked.Increment(ref statNumbered);
        return o;
    }

    static (double x, double y) Dir(IReadOnlyList<(long x, long y)> pts, int s) =>
        (pts[s + 1].x - pts[s].x, pts[s + 1].y - pts[s].y);

    static ((double x, double y) p, (double x, double y) d) Middle(IReadOnlyList<(long x, long y)> pts)
    {
        double kx = Kx(pts[0].y);
        var len = new double[pts.Count - 1];
        double total = 0;
        for (int s = 0; s < len.Length; s++) total += len[s] = Math.Sqrt(Sq((pts[s + 1].x - pts[s].x) * kx) + Sq(pts[s + 1].y - pts[s].y));
        double half = total / 2, acc = 0;
        for (int s = 0; s < len.Length; s++)
        {
            if (acc + len[s] >= half && len[s] > 0)
            {
                double u = (half - acc) / len[s];
                return ((pts[s].x + u * (pts[s + 1].x - pts[s].x), pts[s].y + u * (pts[s + 1].y - pts[s].y)), Dir(pts, s));
            }
            acc += len[s];
        }
        return (((pts[0].x + pts[^1].x) / 2.0, (pts[0].y + pts[^1].y) / 2.0), Dir(pts, 0));
    }

    /// <summary>The (even, odd) ranges of a run of 0x04 records, as 0x0E S2 +8..+14 holds them
    /// (OpenCarin house_numbers.run_envelope): a mixed-scheme side contributes both parities of its interval.</summary>
    public static (int evLo, int evHi, int odLo, int odHi) Envelope(IEnumerable<ushort[]?> recs)
    {
        int evLo = int.MaxValue, evHi = int.MinValue, odLo = int.MaxValue, odHi = int.MinValue;
        void Ev(int a, int b) { evLo = Math.Min(evLo, a); evHi = Math.Max(evHi, b); }
        void Od(int a, int b) { odLo = Math.Min(odLo, a); odHi = Math.Max(odHi, b); }
        foreach (var r in recs)
        {
            if (r == null) continue;
            for (int side = 0; side < 2; side++)
            {
                int a = r[side], b = r[side + 2];
                if (a == None && b == None) continue;
                if (a == None) a = b; if (b == None) b = a;
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                if (r[4] == 1)
                {
                    int el = lo + lo % 2, eh = hi - hi % 2, ol = lo + 1 - lo % 2, oh = hi - 1 + hi % 2;
                    if (el <= eh) Ev(el, eh);
                    if (ol <= oh) Od(ol, oh);
                }
                else if (lo % 2 == 0) Ev(lo, hi); else Od(lo, hi);
            }
        }
        return (evLo == int.MaxValue ? None : evLo, evHi == int.MinValue ? None : evHi,
                odLo == int.MaxValue ? None : odLo, odHi == int.MinValue ? None : odHi);
    }
}
