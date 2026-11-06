using Osm2Carin.Core.Osm;
using Osm2Carin.Core.Roads;

namespace Osm2Carin.Core.Dest;

/// <summary>Phase 3b: the city list and which city each named road and POI belongs to.
///
/// Cities are OSM place=city/town/village/hamlet nodes of the fully detailed regions, named as on the
/// original disc: "san jose, ca" (Tiles.CleanName of the name, then ", " and the state code). Places of
/// one state with the same display name are one city. Every named street link, and every POI, goes to
/// the nearest city by weighted distance (distance / weight: city 3, town 2, village 1.5, hamlet 1),
/// within MaxKm; beyond that it has no city (e.g. highways of other states).
/// OSM has city boundaries only for incorporated places, so a nearest-place rule is the simple
/// approximation; it is what the unit shows as the street's locality and lists it under.</summary>
public sealed class Cities
{
    public sealed class City
    {
        public string Name = "";        // display name, "san jose, ca"
        public int X, Y;                // the place node
        public string Kind = "";
        public double Weight;
        public int Index;               // in List (sorted by name)
    }

    public readonly List<City> List = new();
    /// <summary>Per model link: city index, or -1 (unnamed link, or no city within reach).</summary>
    public int[] LinkCity = [];

    public const double MaxKm = 40;
    static readonly Dictionary<string, double> Weights = new() { ["city"] = 3, ["town"] = 2, ["village"] = 1.5, ["hamlet"] = 1 };
    const double UnitsPerKm = 2e9 / 40075.0;      // carin units per km along the equator (X shrinks with cos(lat))

    public string?[] LinkCityNames() => LinkCity.Select(c => c >= 0 ? List[c].Name : null).ToArray();

    public static string DisplayName(string name, string state)
    {
        var n = Tiles.CleanName(name);
        if (state != "" && n.Length + 2 + state.Length > 60) n = n[..(60 - 2 - state.Length)].TrimEnd();
        return state == "" ? n : $"{n}, {state}";
    }

    public static Cities Build(RoadModel m, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var c = new Cities();
        var byName = new Dictionary<string, City>();
        foreach (var p in m.Places)
        {
            if (!Weights.TryGetValue(p.Kind, out double w)) continue;
            var name = DisplayName(p.Name, p.State);
            if (name.Length < 3 || name.StartsWith(',')) continue;
            if (byName.TryGetValue(name, out var have) && have.Weight >= w) continue;
            byName[name] = new City { Name = name, X = p.X, Y = p.Y, Kind = p.Kind, Weight = w };
        }
        c.List.AddRange(byName.Values.OrderBy(x => x.Name, StringComparer.Ordinal));
        for (int i = 0; i < c.List.Count; i++) c.List[i].Index = i;

        var grid = new Grid(c.List.Select(x => (x.X, x.Y)).ToList());
        var nx = m.Get<int>("node_x"); var ny = m.Get<int>("node_y");
        var frm = m.Get<int>("link_frm"); var to = m.Get<int>("link_to");
        var name0 = m.Get<int>("link_name"); var ref0 = m.Get<int>("link_ref");
        c.LinkCity = new int[frm.Length];
        Parallel.For(0, frm.Length, i =>
        {
            if (name0[i] == 0 && ref0[i] == 0) { c.LinkCity[i] = -1; return; }
            long x = ((long)nx[frm[i]] + nx[to[i]]) / 2, y = ((long)ny[frm[i]] + ny[to[i]]) / 2;
            c.LinkCity[i] = c.Nearest(grid, x, y);
        });
        int named = c.LinkCity.Count(v => v >= 0);
        log($"cities: {c.List.Count:N0} ({string.Join(", ", c.List.GroupBy(x => x.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}"))}); " +
            $"{named:N0} named links placed in a city");
        return c;
    }

    /// <summary>The city for a point, or -1.</summary>
    public int Nearest(Grid g, long x, long y)
    {
        double lat = y * 360.0 / 2e9 * Math.PI / 180;
        double kx = Math.Cos(lat);
        int best = -1; double bestS = double.MaxValue;
        void Scan(double km)
        {
            foreach (int k in g.Near(x, y, (long)(km / Math.Max(kx, 0.2) * UnitsPerKm)))
            {
                var ci = List[k];
                double dx = (ci.X - x) * kx / UnitsPerKm, dy = (ci.Y - y) / UnitsPerKm;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d > MaxKm * ci.Weight / 1.5) continue;
                double s = d / ci.Weight;
                if (s < bestS || (s == bestS && k < best)) { bestS = s; best = k; }
            }
        }
        // a small radius first; a heavier place further away can still win up to bestS * max weight
        const double first = 15, maxW = 3;
        Scan(first);
        double need = best >= 0 ? bestS * maxW : MaxKm * maxW / 1.5;
        if (need > first) { best = -1; bestS = double.MaxValue; Scan(Math.Min(need, MaxKm * maxW / 1.5)); }
        return best;
    }

    /// <summary>A bucket grid over points (carin units), cell ~0.1 degree.</summary>
    public sealed class Grid
    {
        readonly long Cell;
        readonly Dictionary<(long, long), List<int>> cells = new();

        public Grid(List<(int x, int y)> pts, long cell = 555_556)
        {
            Cell = cell;
            for (int i = 0; i < pts.Count; i++)
            {
                var k = (Geo.FloorDiv(pts[i].x, Cell), Geo.FloorDiv(pts[i].y, Cell));
                if (!cells.TryGetValue(k, out var l)) cells[k] = l = new();
                l.Add(i);
            }
        }

        public IEnumerable<int> Near(long x, long y, long radius)
        {
            long cx = Geo.FloorDiv(x, Cell), cy = Geo.FloorDiv(y, Cell), r = radius / Cell + 1;
            for (long i = cx - r; i <= cx + r; i++)
                for (long j = cy - r; j <= cy + r; j++)
                    if (cells.TryGetValue((i, j), out var l))
                        foreach (int k in l) yield return k;
        }
    }
}
