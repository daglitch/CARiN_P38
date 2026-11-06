using System.Text;
using System.Text.RegularExpressions;
using Osm2Carin.Core.Osm;

namespace Osm2Carin.Core.Roads;

/// <summary>A cell of the tiling tree of alternate halves (tiles.py Cell).</summary>
public sealed class Cell(long x1, long y1, long w, long h)
{
    public readonly long X1 = x1, Y1 = y1, W = w, H = h;
    public Cell[]? Kids;
    public int LeafId = -1;
    public long X2 => X1 + W;
    public long Y2 => Y1 + H;

    public Cell[] Split()
    {
        Kids = H == W
            ? [new Cell(X1, Y1, W, H / 2), new Cell(X1, Y1 + H / 2, W, H / 2)]
            : [new Cell(X1, Y1, W / 2, H), new Cell(X1 + W / 2, Y1, W / 2, H)];
        return Kids;
    }

    public int Side(double x, double y) => Kids![0].W == W ? (y >= Kids[1].Y1 ? 1 : 0) : (x >= Kids[1].X1 ? 1 : 0);
    public bool Contains(double x, double y) => X1 <= x && x < X2 && Y1 <= y && y < Y2;
}

/// <summary>An end of a piece: a graph node ('n', node id) or an edge node ('e', edge id).</summary>
public readonly record struct End(char Kind, int Id);

public sealed class Attr
{
    public int Cls, Sub, Form, Dirn, Spd, Jn, Flags, Name, Ref, Hw;
    public Attr Clone() => (Attr)MemberwiseClone();
}

/// <summary>A link or merged chain inside one tile (tiles.py Piece).</summary>
public sealed class Piece
{
    public int Tile; public End A, B; public List<(long x, long y)> Pts; public Attr Attr; public int Src;
    public double Length; public (byte, byte) Brg;

    public Piece(int tile, End a, End b, List<(long, long)> pts, Attr attr, int src)
    {
        Tile = tile; A = a; B = b; Pts = pts; Attr = attr; Src = src;
        Length = TileGeom.PolyLenM(pts);
        var rev = new List<(long, long)>(pts); rev.Reverse();
        Brg = (TileGeom.LeaveBearing(pts), TileGeom.LeaveBearing(rev));
    }
}

public sealed record EdgeNode(int Leaf, long X, long Y, int Twin);

/// <summary>A tile before linking: the Tile26 plus its symbolic cross references (tiles.py TileSpec).</summary>
public sealed class TileSpec(int btype, Cell cell, Tile26 tile)
{
    public readonly int BType = btype;
    public readonly Cell Cell = cell;
    public readonly Tile26 Tile = tile;
    public List<(int leaf, int twin)> EdgeTwins = new();
    public List<int> EdgeIds = new();
    /// <summary>The model link of each section 4 record, in record order (street directory, POI links).</summary>
    public int[] LinkSrc = [];
    public uint BlockId;
    public TileSpec? Parent;
    // link.py
    public List<(TileSpec nb, int u, int v)> S9 = new();
    public List<TileSpec?> S8 = new();
    public long S8Cell;
    public List<(TileSpec sp, int k)?> Twins = new();
    public byte[] Raw = [];
    public byte[] Decoded = [];
    public bool Cf1;
}

public sealed class RoadTiles
{
    public readonly Dictionary<int, List<TileSpec>> Levels = new();
    public readonly Dictionary<int, List<Cell>> Cells = new();
    public readonly Dictionary<int, Cell> Roots = new();
    public System.Text.Json.Nodes.JsonObject Stats = new();
    public int EndUnit;
    /// <summary>Spatial index per road layer (when linked with indexes) and its root square.</summary>
    public readonly Dictionary<int, Spatial.LayerIndex> Index = new();
    public (long x, long y, long side) IndexRoot;
    /// <summary>Every road-layer block in physical order: per layer [0x08 grid][0x09 nodes][tiles].</summary>
    public List<byte[]> Blocks = new();
}

/// <summary>Geometry helpers of tiles.py, with the same floating-point steps (numpy sums are
/// first element + pairwise, as numpy's add.reduce does).</summary>
public static class TileGeom
{
    public const long Grid = 64;

    /// <summary>numpy add.reduce of a float64 array: a[0] + pairwise_sum(a[1:]).</summary>
    public static double NpSum(ReadOnlySpan<double> a)
    {
        if (a.Length == 0) return 0.0;
        return a[0] + Pairwise(a[1..]);
    }

    static double Pairwise(ReadOnlySpan<double> a)
    {
        int n = a.Length;
        if (n < 8)
        {
            double res = 0.0;
            for (int i = 0; i < n; i++) res += a[i];
            return res;
        }
        if (n <= 128)
        {
            double r0 = a[0], r1 = a[1], r2 = a[2], r3 = a[3], r4 = a[4], r5 = a[5], r6 = a[6], r7 = a[7];
            int i;
            for (i = 8; i < n - n % 8; i += 8)
            {
                r0 += a[i]; r1 += a[i + 1]; r2 += a[i + 2]; r3 += a[i + 3];
                r4 += a[i + 4]; r5 += a[i + 5]; r6 += a[i + 6]; r7 += a[i + 7];
            }
            double res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++) res += a[i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return Pairwise(a[..n2]) + Pairwise(a[n2..]);
    }

    public static double PolyLenM(List<(long x, long y)> pts)
    {
        if (pts.Count < 2) return 0.0;
        var seg = new double[pts.Count - 1];
        var lon = new double[pts.Count]; var lat = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++) (lon[i], lat[i]) = Geo.ToLonLat(pts[i].x, pts[i].y);
        for (int i = 0; i + 1 < pts.Count; i++) seg[i] = Geo.Haversine(lon[i], lat[i], lon[i + 1], lat[i + 1]);
        return NpSum(seg);
    }

    public static byte LeaveBearing(List<(long x, long y)> pts)
    {
        var p = pts[0];
        for (int i = 1; i < pts.Count; i++)
        {
            var q = pts[i];
            if (q != p)
            {
                var (lo1, la1) = Geo.ToLonLat(p.x, p.y);
                var (lo2, la2) = Geo.ToLonLat(q.x, q.y);
                return Geo.Bearing256(lo1, la1, lo2, la2);
            }
        }
        return 0;
    }

    /// <summary>CPython 3.10 math.hypot (Modules/mathmodule.c vector_norm, two finite arguments), step for
    /// step, so Douglas-Peucker makes the same choices as the Python tools.</summary>
    public static double Hypot(double a, double b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        double max = Math.Max(a, b);
        if (double.IsInfinity(max)) return max;
        if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
        if (max == 0.0) return max;
        const double T27 = 134217729.0;
        int maxE = Math.ILogB(max) + 1;                     // frexp exponent
        double csum = 1.0, frac1 = 0.0, frac2 = 0.0, frac3 = 0.0, x, t, hi, lo, old;
        if (maxE >= -1023)
        {
            double scale = Math.ScaleB(1.0, -maxE);
            foreach (double v in (double[])[a, b])
            {
                x = v * scale;
                t = x * T27;
                hi = t - (t - x);
                lo = x - hi;
                x = hi * hi;
                old = csum; csum += x; frac1 += (old - csum) + x;
                x = 2.0 * hi * lo;
                old = csum; csum += x; frac2 += (old - csum) + x;
                frac3 += lo * lo;
            }
            double h = Math.Sqrt(csum - 1.0 + (frac1 + frac2 + frac3));
            x = h;
            t = x * T27;
            hi = t - (t - x);
            lo = x - hi;
            x = -hi * hi;
            old = csum; csum += x; frac1 += (old - csum) + x;
            x = -2.0 * hi * lo;
            old = csum; csum += x; frac2 += (old - csum) + x;
            x = -lo * lo;
            old = csum; csum += x; frac3 += (old - csum) + x;
            x = csum - 1.0 + (frac1 + frac2 + frac3);
            return (h + x / (2.0 * h)) / scale;
        }
        foreach (double v in (double[])[a, b])
        {
            x = v / max;
            x *= x;
            old = csum; csum += x; frac1 += (old - csum) + x;
        }
        return max * Math.Sqrt(csum - 1.0 + frac1);
    }

    /// <summary>Douglas-Peucker on grid points, end points kept (tiles.py simplify).</summary>
    public static List<(long x, long y)> Simplify(List<(long x, long y)> pts, double tolM)
    {
        if (pts.Count <= 2 || tolM <= 0) return pts;
        double lat = pts[0].y * Geo.UnitDeg;
        double kx = Geo.UnitDeg * 111320 * Math.Cos(lat * (Math.PI / 180.0));
        double ky = Geo.UnitDeg * 110540;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (i, j) = stack.Pop();
            var (x1, y1) = pts[i]; var (x2, y2) = pts[j];
            double ax = (x2 - x1) * kx, ay = (y2 - y1) * ky;
            double L = Hypot(ax, ay);
            double best = -1.0; int bk = -1;
            for (int k = i + 1; k < j; k++)
            {
                double bx = (pts[k].x - x1) * kx, by = (pts[k].y - y1) * ky;
                double dist = L != 0 ? Math.Abs(ax * by - ay * bx) / L : Hypot(bx, by);
                if (dist > best) { best = dist; bk = k; }
            }
            if (best > tolM)
            {
                keep[bk] = true;
                stack.Push((i, bk));            // Python: stack += [(i, bk), (bk, j)]; pop takes (bk, j) first
                stack.Push((bk, j));
            }
        }
        var o = new List<(long, long)>();
        for (int k = 0; k < pts.Count; k++) if (keep[k]) o.Add(pts[k]);
        return o;
    }
}

/// <summary>Road model -> CARiN road tiles for 0x00 and 0x03/0x02/0x01: port of tools/osm2carin/tiles.py.</summary>
public static partial class Tiles
{
    public const long Grid = 64, Base = 40960;
    public static readonly (long x, long y) Origin = (16384, 1280);
    public const long StreetMax = 1310720, CoarseMax = 2621440, StreetMinW = 81920;
    public static readonly Dictionary<int, int> LevelMaxClass = new() { [3] = 3, [2] = 2, [1] = 1 };
    public static readonly Dictionary<int, double> SimplifyM = new() { [3] = 20.0, [2] = 60.0, [1] = 150.0 };
    public const double StreetBudget = 9 * 1024;
    /// <summary>Point weight (40 per chain point) above which a coarse tile is split. 5 KiB matches the
    /// Python reference; more gives fewer, fuller coarse tiles (the original disc's are mostly full-size).</summary>
    public static double CoarseBudget = 5 * 1024;
    public const int S8Max = 256;
    public const int TileLimit = 8 * 2048 - 256;
    public const double StreetSimplifyM = 2.0;
    public const int S6Limit = 1 << 14;
    public static readonly double[] FitSimplifyM = [5.0, 10.0];
    public const uint NoCost = 2147483;
    public const int StreetHdr6C = 14;
    static readonly (string s, byte ln, byte ty)[] CountryS14 =
    [
        ("united states", 2, 0), ("estados unidos", 6, 2), ("estados unidos", 0x0F, 2),
        ("etats unis", 3, 2), ("vereinigte staaten", 4, 2), ("verenigde staten", 1, 2),
    ];
    const byte CountryIdUs = 0xE0;

    [GeneratedRegex(@"^(i|us|sr|ca|nv|hi|az|or|ut|id|wa|nm|tx)[ -]?\d|^\d")]
    private static partial Regex RouteName();

    // ------------------------------------------------------------------ tree
    /// <summary>The root square of the original disc's quadtree (its 0x07 layer directory): tiles sit at
    /// multiples of their own size from its corner. It covers North America.</summary>
    public static readonly (long x, long y, long side) DiscRoot = (1240326144, -171088640, 671088640);

    /// <summary>The smallest square of the disc's quadtree that holds the box; outside that root, a
    /// square aligned to multiples of its own side on the 40960 grid (tiles.py root_cell).</summary>
    public static Cell RootCell(long xmin, long ymin, long xmax, long ymax)
    {
        var (x0, y0, side) = DiscRoot;
        if (x0 <= xmin && y0 <= ymin && xmax <= x0 + side && ymax <= y0 + side)
        {
            while (side > Base)
            {
                long h = side / 2;
                long qx = x0 + (xmin >= x0 + h ? h : 0), qy = y0 + (ymin >= y0 + h ? h : 0);
                if (xmax <= qx + h && ymax <= qy + h && xmin >= qx && ymin >= qy) { x0 = qx; y0 = qy; side = h; }
                else break;
            }
            return new Cell(x0, y0, side, side);
        }
        long size = Base;
        while (true)
        {
            long ax = Geo.FloorDiv(xmin - Origin.x, size) * size + Origin.x;
            long ay = Geo.FloorDiv(ymin - Origin.y, size) * size + Origin.y;
            if (ax + size >= xmax && ay + size >= ymax) return new Cell(ax, ay, size, size);
            size *= 2;
        }
    }

    static void GrowStreet(Cell cell, double[] xs, double[] ys, double[] wts, List<Cell> leaves)
    {
        double tot = TileGeom.NpSum(wts);
        bool can = cell.W > StreetMinW || cell.H == cell.W;
        if ((tot <= StreetBudget && cell.W <= StreetMax) || !can)
        {
            cell.LeafId = leaves.Count;
            leaves.Add(cell);
            return;
        }
        var ks = cell.Split();
        bool byY = ks[0].W == cell.W;
        var m = new bool[xs.Length];
        for (int i = 0; i < m.Length; i++) m[i] = byY ? ys[i] < ks[1].Y1 : xs[i] < ks[1].X1;
        GrowStreet(ks[0], Pick(xs, m, true), Pick(ys, m, true), Pick(wts, m, true), leaves);
        GrowStreet(ks[1], Pick(xs, m, false), Pick(ys, m, false), Pick(wts, m, false), leaves);
    }

    static double[] Pick(double[] a, bool[] m, bool want)
    {
        int n = 0;
        foreach (bool b in m) if (b == want) n++;
        var o = new double[n];
        int j = 0;
        for (int i = 0; i < a.Length; i++) if (m[i] == want) o[j++] = a[i];
        return o;
    }

    static Cell GrowCoarse(Cell s, double[] xs, double[] ys, double[] wts, List<Cell> leaves, double budget, long maxW)
    {
        var c = new Cell(s.X1, s.Y1, s.W, s.H);
        if (s.Kids == null || (TileGeom.NpSum(wts) <= budget && s.W <= maxW))
        {
            c.LeafId = leaves.Count;
            leaves.Add(c);
            return c;
        }
        var (k0, k1) = (s.Kids[0], s.Kids[1]);
        bool byY = k0.W == s.W;
        var m = new bool[xs.Length];
        for (int i = 0; i < m.Length; i++) m[i] = byY ? ys[i] < k1.Y1 : xs[i] < k1.X1;
        var a = GrowCoarse(k0, Pick(xs, m, true), Pick(ys, m, true), Pick(wts, m, true), leaves, budget, maxW);
        var b = GrowCoarse(k1, Pick(xs, m, false), Pick(ys, m, false), Pick(wts, m, false), leaves, budget, maxW);
        c.Kids = [a, b];
        return c;
    }

    public static Cell FindLeaf(Cell root, double x, double y)
    {
        var c = root;
        while (c.Kids != null) c = c.Kids[c.Side(x, y)];
        return c;
    }

    // ------------------------------------------------------------------ geometry
    static long Quant(long v) => Geo.FloorDiv(v + Grid / 2, Grid) * Grid;

    static ((long x, long y) pt, (double x, double y) probe) ExitPoint(Cell c, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double bestT = 0; string? side = null;
        foreach (var (sd, bound, d, a0) in new (string, double, double, double)[]
                 { ("x2", c.X2, dx, ax), ("x1", c.X1, dx, ax), ("y2", c.Y2, dy, ay), ("y1", c.Y1, dy, ay) })
        {
            if ((sd == "x2" && d > 0) || (sd == "x1" && d < 0) || (sd == "y2" && d > 0) || (sd == "y1" && d < 0))
            {
                double t = (bound - a0) / d;
                if (t >= 0 && t <= 1 && (side == null || t < bestT)) { bestT = t; side = sd; }
            }
        }
        double tt = bestT;
        if (side is "x1" or "x2")
        {
            long x = side == "x2" ? c.X2 : c.X1;
            long y = Math.Min(Math.Max((long)Math.Round((ay + tt * dy) / Grid) * Grid, c.Y1), c.Y2);
            return ((x, y), (side == "x1" ? x - 1 : x, Math.Min(Math.Max(ay + tt * dy, c.Y1), c.Y2 - 1)));
        }
        else
        {
            long y = side == "y2" ? c.Y2 : c.Y1;
            long x = Math.Min(Math.Max((long)Math.Round((ax + tt * dx) / Grid) * Grid, c.X1), c.X2);
            return ((x, y), (Math.Min(Math.Max(ax + tt * dx, c.X1), c.X2 - 1), side == "y1" ? y - 1 : y));
        }
    }

    static List<Piece> Cut(Cell root, List<(long x, long y)> pts, End aKey, End bKey, Attr attr, int src, List<EdgeNode> edges)
    {
        var o = new List<Piece>();
        var cur = FindLeaf(root, pts[0].x, pts[0].y);
        var run = new List<(long, long)> { pts[0] };
        var start = aKey;
        for (int i = 1; i < pts.Count; i++)
        {
            var (ax, ay) = pts[i - 1];
            var (bx, by) = pts[i];
            while (!cur.Contains(bx, by))
            {
                var (cpt, probe) = ExitPoint(cur, ax, ay, bx, by);
                var nxt = FindLeaf(root, probe.x, probe.y);
                if (nxt == cur) nxt = FindLeaf(root, bx, by);
                int e1 = edges.Count, e2 = edges.Count + 1;
                edges.Add(new EdgeNode(cur.LeafId, cpt.x, cpt.y, e2));
                edges.Add(new EdgeNode(nxt.LeafId, cpt.x, cpt.y, e1));
                if (run[^1] != cpt) run.Add(cpt);
                o.Add(new Piece(cur.LeafId, start, new End('e', e1), run, attr, src));
                start = new End('e', e2);
                run = [cpt];
                cur = nxt;
            }
            if (run[^1] != (bx, by)) run.Add((bx, by));
        }
        if (run.Count == 1) run.Add(run[0]);
        o.Add(new Piece(cur.LeafId, start, bKey, run, attr, src));
        return o;
    }

    static (long, long) Nudge(Cell root, long x, long y)
    {
        var c = FindLeaf(root, x, y);
        return (x == c.X1 ? x + Grid : x, y == c.Y1 ? y + Grid : y);
    }

    // ------------------------------------------------------------------ chains
    static List<List<(int link, bool fwd)>> Chains(RoadModel m, bool[] keep)
    {
        var frm = m.Get<int>("link_frm"); var to = m.Get<int>("link_to");
        var cls = m.Get<byte>("link_cls"); var spd = m.Get<byte>("link_spd"); var form = m.Get<byte>("link_form");
        var dirn = m.Get<byte>("link_dirn"); var jn = m.Get<byte>("link_jn");
        var idx = Enumerable.Range(0, keep.Length).Where(i => keep[i]).ToArray();
        var inc = new Dictionary<int, List<int>>();
        var incOrder = new List<int>();
        void AddInc(int n, int i) { if (!inc.TryGetValue(n, out var l)) { inc[n] = l = new(); incOrder.Add(n); } l.Add(i); }
        foreach (int i in idx) { AddInc(frm[i], i); AddInc(to[i], i); }
        (int, int, int, int, int) Key(int i) => (cls[i], spd[i], form[i], dirn[i], jn[i] == 6 ? 1 : 0);

        bool IsBreak(int n)
        {
            var ls = inc[n];
            if (ls.Count != 2) return true;
            int i = ls[0], j = ls[1];
            if (Key(i) != Key(j)) return true;
            if (Key(i).Item4 == 1) return !((to[i] == n) ^ (to[j] == n));
            return false;
        }

        var used = new HashSet<int>();
        var outp = new List<List<(int, bool)>>();
        List<(int, bool)> Walk(int n, int i)
        {
            var seq = new List<(int, bool)>();
            int start = n;
            while (true)
            {
                used.Add(i);
                bool f = frm[i] == n;
                seq.Add((i, f));
                n = f ? to[i] : frm[i];
                if (n == start || IsBreak(n)) return seq;
                int ii = i;
                i = inc[n].First(j => j != ii);
                if (used.Contains(i)) return seq;
            }
        }

        foreach (int n in incOrder.Where(IsBreak).ToList())
            foreach (int i in inc[n])
                if (!used.Contains(i)) outp.Add(Walk(n, i));
        foreach (int i in idx)
            if (!used.Contains(i)) outp.Add(Walk(frm[i], i));
        var split = new List<List<(int, bool)>>();
        foreach (var seq in outp)
        {
            var (i0, f0) = seq[0]; var (i1, f1) = seq[^1];
            int a = f0 ? frm[i0] : to[i0], b = f1 ? to[i1] : frm[i1];
            if (a == b && seq.Count >= 2) { int h = seq.Count / 2; split.Add(seq[..h]); split.Add(seq[h..]); }
            else split.Add(seq);
        }
        for (int k = 0; k < split.Count; k++)
        {
            var (i0, f0) = split[k][0];
            if (dirn[i0] == 1 && !f0)
            {
                var r = split[k].Select(p => (p.Item1, !p.Item2)).ToList();
                r.Reverse();
                split[k] = r;
            }
        }
        return split;
    }

    // ------------------------------------------------------------------ main entry
    /// <summary>Street tiles name their links' cities: linkCity[model link] is the display name
    /// ("san jose, ca") or null/"" for none; without it every tile names regionLabel (Phase 2).</summary>
    public sealed record NameCtx(string Label, string?[]? LinkCity);

    public static RoadTiles Build(RoadModel m, List<string> strings, string regionLabel = "", Action<string>? log = null,
                                  string?[]? linkCity = null)
    {
        var nctx = new NameCtx(regionLabel, linkCity);
        log ??= Console.WriteLine;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string T() => $"  [{sw.Elapsed.TotalSeconds:F1} s]";
        var nxQ = m.Get<int>("node_x").Select(v => Quant(v)).ToArray();
        var nyQ = m.Get<int>("node_y").Select(v => Quant(v)).ToArray();
        var sxQ = m.Get<int>("shape_x").Select(v => Quant(v)).ToArray();
        var syQ = m.Get<int>("shape_y").Select(v => Quant(v)).ToArray();
        var frm = m.Get<int>("link_frm"); var to = m.Get<int>("link_to");
        var sStart = m.Get<long>("link_shape_start"); var sCount = m.Get<int>("link_shape_count");
        int nLinks = frm.Length;

        var xs = nxQ.Concat(sxQ).Select(v => (double)v).ToArray();
        var ys = nyQ.Concat(syQ).Select(v => (double)v).ToArray();
        double perNode = 8.0 + 36.0 * nLinks / Math.Max(nxQ.Length, 1);
        var wts = Enumerable.Repeat(perNode, nxQ.Length).Concat(Enumerable.Repeat(6.0, sxQ.Length)).ToArray();
        var root = RootCell((long)xs.Min(), (long)ys.Min(), (long)xs.Max() + 1, (long)ys.Max() + 1);
        var leaves = new List<Cell>();
        GrowStreet(root, xs, ys, wts, leaves);
        log($"street tiles: {leaves.Count:N0} (root {root.W:N0} units){T()}");

        var nodeXy = new (long x, long y)[nxQ.Length];
        Parallel.For(0, nxQ.Length, i => nodeXy[i] = Nudge(root, nxQ[i], nyQ[i]));
        var shapeXy = new (long x, long y)[sxQ.Length];
        Parallel.For(0, sxQ.Length, i => shapeXy[i] = Nudge(root, sxQ[i], syQ[i]));

        List<(long x, long y)> Polyline(int i, bool forward = true)
        {
            var o = new List<(long, long)> { nodeXy[frm[i]] };
            long s0 = sStart[i];
            for (long k = s0; k < s0 + sCount[i]; k++) if (shapeXy[k] != o[^1]) o.Add(shapeXy[k]);
            if (nodeXy[to[i]] != o[^1]) o.Add(nodeXy[to[i]]);
            if (o.Count == 1) o.Add(o[0]);
            if (!forward) o.Reverse();
            return o;
        }

        var cls = m.Get<byte>("link_cls"); var sub = m.Get<byte>("link_sub"); var form = m.Get<byte>("link_form");
        var dirn = m.Get<byte>("link_dirn"); var spd = m.Get<byte>("link_spd"); var jn = m.Get<byte>("link_jn");
        var flags = m.Get<byte>("link_flags"); var name = m.Get<int>("link_name"); var refA = m.Get<int>("link_ref");
        var hw = m.Get<byte>("link_hw");
        var attrs = new Attr[nLinks];
        for (int i = 0; i < nLinks; i++)
            attrs[i] = new Attr { Cls = cls[i], Sub = sub[i], Form = form[i], Dirn = dirn[i], Spd = spd[i], Jn = jn[i], Flags = flags[i], Name = name[i], Ref = refA[i], Hw = hw[i] };

        // street level (pieces are cut per link in parallel, then numbered in link order)
        var perLink = new List<Piece>[nLinks];
        var perEdges = new List<EdgeNode>[nLinks];
        Parallel.For(0, nLinks, i =>
        {
            var e = new List<EdgeNode>();
            var ps = Cut(root, Polyline(i), new End('n', frm[i]), new End('n', to[i]), attrs[i], i, e);
            foreach (var p in ps) p.Pts = TileGeom.Simplify(p.Pts, StreetSimplifyM);
            perLink[i] = ps; perEdges[i] = e;
        });
        var edges = new List<EdgeNode>();
        var pieces = new List<Piece>();
        for (int i = 0; i < nLinks; i++)
        {
            int baseE = edges.Count;
            foreach (var e in perEdges[i]) edges.Add(e with { Twin = e.Twin + baseE });
            foreach (var p in perLink[i])
            {
                if (p.A.Kind == 'e') p.A = new End('e', p.A.Id + baseE);
                if (p.B.Kind == 'e') p.B = new End('e', p.B.Id + baseE);
                pieces.Add(p);
            }
        }
        log($"street pieces: {pieces.Count:N0} from {nLinks:N0} links, edge nodes {edges.Count:N0}{T()}");

        // coarse levels
        var rt = new RoadTiles();
        var reach = new Dictionary<(long, long), int>();
        var coarse = new Dictionary<int, (Cell root, List<Cell> leaves, List<Piece> pieces, List<EdgeNode> edges)>();
        foreach (int lv in (int[])[3, 2, 1])
        {
            var keep = cls.Select(c => c <= LevelMaxClass[lv]).ToArray();
            var ch = Chains(m, keep);
            var cx = new List<double>(); var cy = new List<double>();
            foreach (var seq in ch) foreach (var (i, _) in seq) { cx.Add(nodeXy[frm[i]].x); cy.Add(nodeXy[frm[i]].y); }
            var cleaves = new List<Cell>();
            var croot = GrowCoarse(root, cx.ToArray(), cy.ToArray(), Enumerable.Repeat(40.0, cx.Count).ToArray(), cleaves, CoarseBudget, CoarseMax);
            var cedges = new List<EdgeNode>();
            var cpieces = new List<Piece>();
            foreach (var seq in ch)
            {
                var pts = new List<(long, long)>();
                foreach (var (i, f) in seq)
                    foreach (var p in Polyline(i, f))
                        if (pts.Count == 0 || p != pts[^1]) pts.Add(p);
                var (i0, f0) = seq[0]; var (i1, f1) = seq[^1];
                int a = f0 ? frm[i0] : to[i0], b = f1 ? to[i1] : frm[i1];
                foreach (var p in Cut(croot, pts, new End('n', a), new End('n', b), attrs[i0], i0, cedges))
                {
                    p.Pts = TileGeom.Simplify(p.Pts, SimplifyM[lv]);
                    cpieces.Add(p);
                }
            }
            foreach (var p in cpieces)
                foreach (var end in new[] { p.Pts[0], p.Pts[^1] })
                    reach[end] = Math.Max(reach.GetValueOrDefault(end), 4 - lv);
            coarse[lv] = (croot, cleaves, cpieces, cedges);
            log($"level 0x0{lv}: {keep.Count(k => k):N0} street links -> {ch.Count:N0} chains -> {cpieces.Count:N0} pieces in {cleaves.Count:N0} tiles{T()}");
        }

        rt.Roots[0] = root; rt.Cells[0] = leaves;
        var fits = new System.Text.Json.Nodes.JsonObject();
        var (l0, fit0) = MakeTiles(0, leaves, pieces, edges, reach, strings, nctx, m);
        rt.Levels[0] = l0;
        log($"street tiles written{T()}");
        if (fit0.Count > 0) fits["0x00"] = fit0;
        foreach (int lv in (int[])[3, 2, 1])
        {
            var c = coarse[lv];
            rt.Roots[lv] = c.root; rt.Cells[lv] = c.leaves;
            var (lt, fit) = MakeTiles(lv, c.leaves, c.pieces, c.edges, reach, strings, nctx, null);
            rt.Levels[lv] = lt;
            if (fit.Count > 0) fits[$"0x0{lv}"] = fit;
        }
        log($"coarse tiles written{T()}");
        var co = new System.Text.Json.Nodes.JsonObject();
        foreach (int lv in (int[])[3, 2, 1])
            co[$"0x0{lv}"] = new System.Text.Json.Nodes.JsonObject { ["tiles"] = coarse[lv].leaves.Count, ["links"] = coarse[lv].pieces.Count, ["edge_nodes"] = coarse[lv].edges.Count };
        rt.Stats = new System.Text.Json.Nodes.JsonObject
        {
            ["street_tiles"] = leaves.Count, ["street_pieces"] = pieces.Count, ["street_edge_nodes"] = edges.Count,
            ["coarse"] = co, ["fit"] = fits,
        };
        return rt;
    }

    // ------------------------------------------------------------------ tiles
    static int NodeFlags(int sec, int reachLv, int degree)
    {
        int hi = (3 - reachLv) << 6;
        if (sec == 5) hi |= 0x10 | (degree == 1 ? 1 : degree == 2 ? 5 : 0);
        else hi |= 0x20 | (degree == 1 ? 5 : 0x10);
        return hi << 8;
    }

    static int Width(Attr a)
    {
        bool one = a.Dirn is 1 or 2;
        if (a.Cls <= 1 && !one) return 3;
        if (one) return a.Cls <= 2 ? 2 : 0;
        return 1;
    }

    static (List<TileSpec>, System.Text.Json.Nodes.JsonObject) MakeTiles(int btype, List<Cell> leaves, List<Piece> pieces,
        List<EdgeNode> edges, Dictionary<(long, long), int> reach, List<string> strings, NameCtx regionLabel, RoadModel? m)
    {
        var byTile = new Dictionary<int, List<Piece>>();
        foreach (var p in pieces)
        {
            if (!byTile.TryGetValue(p.Tile, out var l)) byTile[p.Tile] = l = new();
            l.Add(p);
        }
        var bans = btype == 0 && m != null ? Bans(m) : new Dictionary<(int, int), List<(int, bool)>>();
        var specs = new TileSpec[leaves.Count];
        var fitLogs = new Dictionary<string, int>[leaves.Count];
        using (var prog = new Progress($"tiles 0x{btype:X2}", leaves.Count, "tiles"))
            Parallel.For(0, leaves.Count, li =>
            {
                var cell = leaves[li];
                var fl = new Dictionary<string, int>();
                specs[li] = FitTile(btype, cell, byTile.GetValueOrDefault(cell.LeafId) ?? new(), edges, reach, strings, regionLabel, bans, fl);
                fitLogs[li] = fl;
                prog.Add();
            });
        // merge the fit logs in leaf order, keys in first-seen order (the Python dict)
        var order = new List<string>();
        var sum = new Dictionary<string, int>();
        foreach (var fl in fitLogs)
            foreach (var (k, v) in fl)
            {
                if (!sum.ContainsKey(k)) { sum[k] = 0; order.Add(k); }
                sum[k] += v;
            }
        var j = new System.Text.Json.Nodes.JsonObject();
        foreach (var k in order) j[k] = sum[k];
        return (specs.ToList(), j);
    }

    static bool TooBig(Tile26 t)
    {
        var (start, _) = t.Layout();
        return t.DecodedSize() > TileLimit || start[6] + 16 * t.Secs[6].Count > S6Limit;
    }

    static void Inc(Dictionary<string, int> d, string k, int n = 1)
    {
        // keep first-insertion order for the merged log (Dictionary preserves insertion order when nothing is removed)
        d[k] = d.GetValueOrDefault(k) + n;
    }

    static TileSpec FitTile(int btype, Cell cell, List<Piece> ps, List<EdgeNode> edges, Dictionary<(long, long), int> reach,
        List<string> strings, NameCtx regionLabel, Dictionary<(int, int), List<(int, bool)>> bans, Dictionary<string, int> fitLog)
    {
        var spec = OneTile(btype, cell, ps, edges, reach, strings, regionLabel, bans);
        if (!TooBig(spec.Tile)) return spec;
        foreach (double tol in FitSimplifyM)
        {
            foreach (var p in ps) p.Pts = TileGeom.Simplify(p.Pts, tol);
            spec = OneTile(btype, cell, ps, edges, reach, strings, regionLabel, bans);
            Inc(fitLog, $"tiles simplified to {tol:0.##} m");
            if (!TooBig(spec.Tile)) return spec;
        }
        foreach (int c in (int[])[6, 5, 4])
        {
            var cand = ps.Where(p => p.Attr.Cls == c).OrderBy(p => p.Length).ToList();
            while (cand.Count > 0 && TooBig(spec.Tile))
            {
                int nd = Math.Max(1, cand.Count / 10);
                var drop = new HashSet<Piece>(cand.Take(nd), ReferenceEqualityComparer.Instance as IEqualityComparer<Piece>);
                cand = cand.Skip(nd).ToList();
                ps = ps.Where(p => !drop.Contains(p)).ToList();
                Inc(fitLog, $"class-{c} pieces dropped (tile full)", nd);
                spec = OneTile(btype, cell, ps, edges, reach, strings, regionLabel, bans);
            }
            if (!TooBig(spec.Tile)) return spec;
        }
        Inc(fitLog, "tiles still too big");
        return spec;
    }

    static Dictionary<(int, int), List<(int, bool)>> Bans(RoadModel m)
    {
        var o = new Dictionary<(int, int), List<(int, bool)>>();
        var f = m.Get<long>("tr_from_link"); var v = m.Get<long>("tr_via_node"); var t = m.Get<long>("tr_to_link"); var only = m.Get<long>("tr_only");
        for (int i = 0; i < f.Length; i++)
        {
            var k = ((int)f[i], (int)v[i]);
            if (!o.TryGetValue(k, out var l)) o[k] = l = new();
            l.Add(((int)t[i], only[i] != 0));
        }
        return o;
    }

    sealed class LinkW
    {
        public List<(long x, long y)> Pts = null!; public End A, B; public Attr Attr = null!; public (byte b0, byte b1) Brg;
        public double Length; public int Src;
    }

    sealed class NodeW
    {
        public int Sec; public (long x, long y) Pos; public List<(int link, int end)> Links = new(); public int Group;
    }

    static TileSpec OneTile(int btype, Cell cell, List<Piece> ps, List<EdgeNode> edges, Dictionary<(long, long), int> reach,
        List<string> strings, NameCtx regionLabel, Dictionary<(int, int), List<(int, bool)>> bans)
    {
        bool street = btype == 0;
        var t = new Tile26(btype) { BBox = ((int)cell.X1, (int)cell.Y1, (int)cell.X2, (int)cell.Y2) };
        var spec = new TileSpec(btype, cell, t);
        long x1 = cell.X1, y1 = cell.Y1;
        (long, long) Loc((long x, long y) p) => (Geo.FloorDiv(p.x - x1, Grid), Geo.FloorDiv(p.y - y1, Grid));

        var links = new List<LinkW>();
        foreach (var p in ps)
        {
            var pts = p.Pts; var a = p.A; var b = p.B; var attr = p.Attr.Clone(); var brg = p.Brg;
            if (Loc(pts[0]).CompareTo(Loc(pts[^1])) > 0)
            {
                pts = new List<(long, long)>(pts); pts.Reverse();
                (a, b) = (b, a);
                brg = (brg.Item2, brg.Item1);
                if (attr.Dirn is 1 or 2) attr.Dirn = 3 - attr.Dirn;
            }
            links.Add(new LinkW { Pts = pts, A = a, B = b, Attr = attr, Brg = brg, Length = p.Length, Src = p.Src });
        }

        var nodes = new Dictionary<End, NodeW>();
        var nodeOrder = new List<End>();
        for (int k = 0; k < links.Count; k++)
        {
            var L = links[k];
            foreach (var (end, key, pos) in new[] { (0, L.A, L.Pts[0]), (1, L.B, L.Pts[^1]) })
            {
                if (!nodes.TryGetValue(key, out var n)) { nodes[key] = n = new NodeW { Sec = key.Kind == 'e' ? 6 : 5, Pos = pos }; nodeOrder.Add(key); }
                n.Links.Add((k, end));
            }
        }
        foreach (var key in nodeOrder) nodes[key].Group = nodes[key].Links.Min(le => links[le.link].Attr.Cls);

        var order = Enumerable.Range(0, links.Count)
            .OrderBy(k => (links[k].Attr.Cls, Loc(links[k].Pts[0]), Loc(links[k].Pts[^1]))).ToArray();
        var posOf = new int[links.Count];
        for (int i = 0; i < order.Length; i++) posOf[order[i]] = i;
        links = order.Select(k => links[k]).ToList();
        spec.LinkSrc = links.Select(l => l.Src).ToArray();
        foreach (var key in nodeOrder) nodes[key].Links = nodes[key].Links.Select(le => (posOf[le.link], le.end)).ToList();
        var n5 = nodeOrder.Where(k => nodes[k].Sec == 5).OrderBy(k => (nodes[k].Group, Loc(nodes[k].Pos))).ToList();
        var n6 = nodeOrder.Where(k => nodes[k].Sec == 6).OrderBy(k => Loc(nodes[k].Pos)).ToList();
        var nref = new Dictionary<End, Ref>();
        for (int i = 0; i < n5.Count; i++) nref[n5[i]] = new Ref(5, i);
        for (int i = 0; i < n6.Count; i++) nref[n6[i]] = new Ref(6, i);

        var nxt = new Dictionary<(int, int), int?>();
        var first = new Dictionary<End, int>();
        foreach (var key in nodeOrder)
        {
            var n = nodes[key];
            var cyc = n.Links.OrderBy(le => le.end == 0 ? links[le.link].Brg.b0 : links[le.link].Brg.b1).ToList();
            first[key] = cyc[0].link;
            for (int jj = 0; jj < cyc.Count; jj++)
                nxt[cyc[jj]] = cyc.Count == 1 ? (n.Sec == 5 ? cyc[jj].link : null) : cyc[(jj + 1) % cyc.Count].link;
        }

        var s2Of = new int[links.Count];
        if (street) Names(t, links, strings, regionLabel, s2Of);
        else
        {
            foreach (int s in (int[])[0, 1, 2]) t.Secs[s].AddRange([new Rec(10), new Rec(10)]);
            t.ZeroOffset.Add(13); t.ZeroOffset.Add(14);
        }
        t.ZeroOffset.Add(13);

        // turn bans
        var s10 = new List<(int k2, int end)>();
        var s10Start = new int[links.Count];
        if (street && bans.Count > 0)
        {
            var atVia = new Dictionary<int, List<(int, int)>>();
            for (int k = 0; k < links.Count; k++)
                foreach (var (end, key) in new[] { (0, links[k].A), (1, links[k].B) })
                    if (key.Kind == 'n')
                    {
                        if (!atVia.TryGetValue(key.Id, out var l)) atVia[key.Id] = l = new();
                        l.Add((k, end));
                    }
            for (int k = 0; k < links.Count; k++)
            {
                s10Start[k] = s10.Count;
                foreach (var (end, key) in new[] { (0, links[k].A), (1, links[k].B) })
                {
                    if (key.Kind != 'n') continue;
                    if (!bans.TryGetValue((links[k].Src, key.Id), out var bl)) continue;
                    foreach (var (toLink, only) in bl)
                        foreach (var (k2, _) in atVia[key.Id])
                        {
                            bool hit = links[k2].Src == toLink;
                            if (k2 != k && hit != only) s10.Add((k2, end));
                        }
                }
            }
        }
        foreach (var (k2, end) in s10)
        {
            var r = new Rec(8) { Own = true };
            r.Refs[4] = new Ref(4, k2);
            r.Raw[7] = (byte)end;
            t.Secs[10].Add(r);
        }

        // S7, S4
        int recN = Tile26.Rec4(btype);
        for (int k = 0; k < links.Count; k++)
        {
            var L = links[k]; var a = L.Attr;
            var r = new Rec(recN);
            int bu = street && a.Cls >= 2 && cell.W <= 327680 ? 1 : 0;
            r.Raw[0x0A] = (byte)((a.Spd & 0x1F) | (bu << 7));
            r.Raw[0x0B] = (byte)((a.Form & 0x0F) | ((a.Dirn & 3) << 4) | ((a.Flags & 1) << 6));
            int len = (int)Math.Min(Math.Round(L.Length), 0xFFFF);
            r.Raw[0x0C] = (byte)(len >> 8); r.Raw[0x0D] = (byte)len;
            r.Raw[0x0E] = L.Brg.b0; r.Raw[0x0F] = L.Brg.b1;
            r.Raw[0x10] = (byte)((a.Cls & 0x0F) | ((a.Sub & 7) << 4));
            r.Raw[0x11] = (byte)((a.Jn & 0x0F) | 0x20);
            r.Refs[0] = nref[L.A]; r.Refs[2] = nref[L.B]; r.Refs[4] = new Ref(7, t.Secs[7].Count);
            r.Refs[6] = nxt[(k, 0)] is int n0 ? new Ref(4, n0) : null;
            r.Refs[8] = nxt[(k, 1)] is int n1 ? new Ref(4, n1) : null;
            r.Refs[0x12] = new Ref(10, s10Start[k]); r.Refs[0x14] = new Ref(12, 0);
            if (street)
            {
                r.Raw[0x1A] = (byte)(0x10 | ((a.Flags & 2) != 0 ? 0x08 : 0));
                r.Raw[0x1B] = (byte)((bu << 7) | (Width(a) << 1));
                r.Refs[0x18] = new Ref(2, s2Of[k]);
                r.Refs[0x1C] = new Ref(11, 0);
            }
            for (int q = 1; q < L.Pts.Count - 1; q++)
            {
                var (u, v) = Loc(L.Pts[q]);
                var s7 = new Rec(6);
                s7.Raw[0] = (byte)(u >> 8); s7.Raw[1] = (byte)u; s7.Raw[2] = (byte)(v >> 8); s7.Raw[3] = (byte)v;
                t.Secs[7].Add(s7);
            }
            t.Secs[4].Add(r);
        }
        var sent = new Rec(recN);
        sent.Refs[0] = null; sent.Refs[2] = null; sent.Refs[4] = new Ref(7, t.Secs[7].Count); sent.Refs[6] = null; sent.Refs[8] = null;
        sent.Refs[0x12] = new Ref(10, s10.Count); sent.Refs[0x14] = new Ref(12, 0);
        if (street) { sent.Refs[0x18] = null; sent.Refs[0x1C] = new Ref(11, 0); }
        t.Secs[4].Add(sent);

        Rec NodeRec(End key, int size)
        {
            var n = nodes[key];
            var (u, v) = Loc(n.Pos);
            var r = new Rec(size);
            r.Raw[0] = (byte)(u >> 8); r.Raw[1] = (byte)u; r.Raw[2] = (byte)(v >> 8); r.Raw[3] = (byte)v;
            int fl = NodeFlags(n.Sec, reach.GetValueOrDefault(n.Pos), n.Links.Count);
            r.Raw[6] = (byte)(fl >> 8); r.Raw[7] = (byte)fl;
            r.Refs[4] = new Ref(4, first[key]);
            return r;
        }
        foreach (var k in n5) t.Secs[5].Add(NodeRec(k, 8));
        foreach (var k in n6) t.Secs[6].Add(NodeRec(k, 16));
        spec.EdgeIds = n6.Select(k => k.Id).ToList();
        spec.EdgeTwins = n6.Select(k => (edges[edges[k.Id].Twin].Leaf, edges[k.Id].Twin)).ToList();

        for (int g = 0; g < 7; g++)
        {
            int l0 = links.FindIndex(L => L.Attr.Cls >= g); if (l0 < 0) l0 = links.Count;
            int q0 = n5.FindIndex(k => nodes[k].Group >= g); if (q0 < 0) q0 = n5.Count;
            var r = new Rec(4); r.Refs[0] = new Ref(4, l0); r.Refs[2] = new Ref(5, q0);
            t.Secs[3].Add(r);
        }
        { var r = new Rec(4); r.Refs[0] = new Ref(4, links.Count); r.Refs[2] = new Ref(5, n5.Count); t.Secs[3].Add(r); }

        var hwb = new byte[0x20];
        for (int i = 0; i < 3; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hwb.AsSpan(0x0C + 4 * i), NoCost);
        if (street)
        {
            hwb[0x18] = 0; hwb[0x19] = StreetHdr6C;
            hwb[0x1A] = 0; hwb[0x1B] = 10;
        }
        hwb[0x1C] = 0; hwb[0x1D] = 1;
        t.HdrWords = hwb;
        t.Head = new byte[8];
        t.Head[4] = (byte)(btype >> 8); t.Head[5] = (byte)btype;
        return spec;
    }

    static readonly Dictionary<char, string> Fold = new() { ['&'] = " and ", ['#'] = "no ", ['"'] = "", ['!'] = "", ['$'] = "", ['%'] = "" };

    /// <summary>Lower-case ASCII that the CF=1 text code can carry (tiles.py clean_name).</summary>
    public static string CleanName(string s)
    {
        var n = Py.Lower(s).Normalize(NormalizationForm.FormKD);
        var a = new StringBuilder();
        foreach (char c in n) if (c < 0x80) a.Append(c);
        var b = new StringBuilder();
        foreach (char c in a.ToString())
            b.Append(c < '\'' && c != ' ' ? (Fold.TryGetValue(c, out var f) ? f : c.ToString()) : c.ToString());
        var d = new StringBuilder();
        foreach (char c in b.ToString()) if (c == ' ' || (c >= 0x27 && c < 0x80)) d.Append(c);
        var o = string.Join(' ', d.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return o.Length > 60 ? o[..60] : o;
    }

    static void Names(Tile26 t, List<LinkW> links, List<string> strings, NameCtx ctx, int[] s2Of)
    {
        var names = links.Select(L =>
        {
            var a = L.Attr;
            string nm = a.Name != 0 ? strings[a.Name] : a.Ref != 0 ? strings[a.Ref] : "";
            return CleanName(nm);
        }).ToList();
        var distinct = names.Where(n => n != "").Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var cl = CleanName(ctx.Label);
        string fallback = ctx.LinkCity != null ? "united states" : cl != "" ? cl : "unknown";
        // the city of each link: its own (Phase 3b) or the region label
        var cityOf = links.Select(L => ctx.LinkCity?[L.Src] is string c && c != "" ? c : fallback).ToList();
        var cities = cityOf.Where((c, k) => names[k] != "").Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (cities.Count == 0) cities.Add(fallback);
        var cityIx = cities.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var blob = new List<byte>();
        var textAt = new Dictionary<string, int>();
        TextRef Text(string s)
        {
            if (!textAt.TryGetValue(s, out int at)) { at = textAt[s] = blob.Count; blob.AddRange(Encoding.Latin1.GetBytes(s)); blob.Add(0); }
            return new TextRef(at);
        }
        Rec R(byte[] raw, params (int o, object? v)[] refs) { var r = new Rec(raw); foreach (var (o, v) in refs) r.Refs[o] = v; return r; }

        var s14 = new List<Rec>();
        foreach (var (s, ln, ty) in CountryS14) s14.Add(R([0, 0, ln, ty], (0, Text(s))));
        int city0 = s14.Count;
        foreach (var c in cities) s14.Add(R([0, 0, 2, 0], (0, Text(c))));
        int street0 = s14.Count;
        foreach (var nm in distinct) s14.Add(R([0, 0, 2, (byte)(RouteName().IsMatch(nm) ? 0x10 : 0x08)], (0, Text(nm))));
        int n14 = s14.Count;
        var s0 = new List<Rec>
        {
            R(new byte[10], (0, null), (4, null)),
            R([0, 0, 0, CountryIdUs, 0, 0, 0, 0, 0, 0], (0, Text("united states")), (4, new Ref(14, 0))),
            R(new byte[10], (0, null), (4, new Ref(14, CountryS14.Length))),
        };
        var s1 = new List<Rec> { R(new byte[10], (0, null), (2, null), (4, new Ref(0, 0)), (6, null), (8, null)) };
        foreach (var (c, i) in cities.Select((c, i) => (c, i)))
            s1.Add(R(new byte[10], (0, Text(c)), (2, null), (4, new Ref(0, 1)), (6, new Ref(14, city0 + i)), (8, new Ref(14, n14))));
        s1.Add(R(new byte[10], (0, null), (2, null), (4, new Ref(0, 0)), (6, new Ref(14, street0)), (8, new Ref(14, n14))));
        bool single = ctx.LinkCity == null;          // Phase 2 layout: no locality, one city
        var s2 = new List<Rec> { R(new byte[10], (0, null), (2, null), (4, new Ref(1, 0)), (6, null), (8, null)) };
        var recOf = new Dictionary<(string, string), int>();
        if (names.Any(n => n == ""))
        {
            recOf[("", "")] = s2.Count;
            s2.Add(R(new byte[10], (0, null), (2, null), (4, new Ref(1, 1)), (6, new Ref(14, street0)), (8, new Ref(14, n14))));
        }
        var keys = names.Select((n, k) => (n, c: cityOf[k])).Where(x => x.n != "").Distinct()
            .OrderBy(x => x.n, StringComparer.Ordinal).ThenBy(x => x.c, StringComparer.Ordinal).ToList();
        foreach (var (n, c) in keys)
        {
            recOf[(n, c)] = s2.Count;
            int j = distinct.IndexOf(n);
            s2.Add(single
                ? R(new byte[10], (0, Text(n)), (2, null), (4, new Ref(1, 1)), (6, new Ref(14, street0 + j)), (8, new Ref(14, n14)))
                : R(new byte[10], (0, Text(n)), (2, Text(c)), (4, new Ref(1, 1 + cityIx[c])), (6, new Ref(14, street0 + j)), (8, new Ref(14, n14))));
        }
        for (int k = 0; k < names.Count; k++) s2Of[k] = names[k] == "" ? recOf[("", "")] : recOf[(names[k], cityOf[k])];
        t.Secs[0].AddRange(s0); t.Secs[1].AddRange(s1); t.Secs[2].AddRange(s2); t.Secs[14].AddRange(s14);
        t.Blob = blob.ToArray();
    }
}
