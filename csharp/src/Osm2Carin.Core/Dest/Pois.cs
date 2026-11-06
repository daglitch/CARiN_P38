using Osm2Carin.Core.Roads;

namespace Osm2Carin.Core.Dest;

public sealed class PoiRec
{
    public int City, Category;
    public string Name = "", Street = "", House = "", Phone = "", Postcode = "";
    public int X, Y;
    public uint PoiId;
    public (TileSpec sp, int k)? Road;
    public PoiBlk? Blk;
    public int Index;
}

/// <summary>0x10 POI detail block (DB-REL 26): descriptors S0 {0x28, n}, S1 {o, n}, S2/S3 0, S4 {o, n}, S5 0;
/// +0x20 next / +0x24 previous 0x10 BLOCK_ID (02-geo.md §8.1.1). One POI = one record of each:
///   S0 8 B: u16 name, u16 type 2, u16 locality 0, u16 -> S1;
///   S1 40 B: i32 X, i32 Y, u16 -> S4, u16 1, u16 brand 0, u16 street, u16 city, u16 county 0,
///            u16 country, u32 0x00010000 (as on the disc), u16 house number, u32 POI ID, u16 phone,
///            u16 postcode, u32 0;
///   S4 8 B: u32 tile BLOCK_ID, u16 offset of the road's section 4 record in the decoded tile, u16 0.
/// String fields are byte offsets from the block start, 0 = none. Category copy only: no brands.</summary>
public sealed class PoiBlk : Blk
{
    public const int First = 0x28, Per = 8 + 40 + 8;
    public readonly List<PoiRec> Pois = new();
    public readonly TextPool Text = new();
    public PoiBlk? Next, Prev;
    public readonly List<string> CityNames;
    public PoiBlk(List<string> cityNames) => CityNames = cityNames;
    public override int Type => 0x10;
    public override int Size => First + Per * Pois.Count + Text.Length;

    IEnumerable<string> Strings(PoiRec p) => [p.Name, p.Street, CityNames[p.City], "united states", p.House, p.Phone, p.Postcode];

    public int SizeWith(IEnumerable<PoiRec> ps)
    {
        var tmp = new HashSet<string>();
        int extra = 0;
        foreach (var p in ps)
        {
            extra += Per;
            foreach (var s in Strings(p)) if (s != "" && tmp.Add(s)) extra += Text.Measure(s);
        }
        return Size + extra;
    }

    public void Add(PoiRec p)
    {
        p.Blk = this; p.Index = Pois.Count;
        Pois.Add(p);
        foreach (var s in Strings(p)) Text.Add(s);
    }

    public int S0Offset(PoiRec p) => First + 8 * p.Index;

    public override void Write(byte[] b)
    {
        int n = Pois.Count, s1 = First + 8 * n, s4 = s1 + 40 * n, txt = s4 + 8 * n;
        W16(b, 8, First); W16(b, 10, n); W16(b, 12, s1); W16(b, 14, n); W16(b, 24, s4); W16(b, 26, n);
        W32(b, 0x20, Next?.Id ?? 0); W32(b, 0x24, Prev?.Id ?? 0);
        int T(string s) => s == "" ? 0 : txt + Text.Add(s);
        foreach (var p in Pois)
        {
            int o0 = First + 8 * p.Index, o1 = s1 + 40 * p.Index, o4 = s4 + 8 * p.Index;
            W16(b, o0, T(p.Name)); W16(b, o0 + 2, 2); W16(b, o0 + 4, 0); W16(b, o0 + 6, o1);
            Wi32(b, o1, p.X); Wi32(b, o1 + 4, p.Y);
            W16(b, o1 + 8, p.Road != null ? o4 : 0); W16(b, o1 + 10, 1); W16(b, o1 + 12, 0);
            W16(b, o1 + 14, T(p.Street)); W16(b, o1 + 16, T(CityNames[p.City])); W16(b, o1 + 18, 0);
            W16(b, o1 + 20, T("united states")); W32(b, o1 + 22, 0x00010000); W16(b, o1 + 26, T(p.House));
            W32(b, o1 + 28, p.PoiId); W16(b, o1 + 32, T(p.Phone)); W16(b, o1 + 34, T(p.Postcode));
            if (p.Road is (TileSpec sp, int k)) { W32(b, o4, sp.BlockId); W16(b, o4 + 4, Directory.S4Offset(sp, k)); }
        }
        Text.CopyTo(b, txt);
    }
}

/// <summary>0x06 POI map tile (DB-REL 26): S0 {0x20, n}, bounding box at +0x10 (x1, y1, x2, y2), then
/// 20-byte records sorted by LOCAL_X: u32 0x10 BLOCK_ID, u16 offset of the POI's S0 record,
/// u16 LOCAL_X, u16 LOCAL_Y (step 64), u16 category, u16 brand 0, u16 0, u32 POI ID.</summary>
public sealed class PoiTileBlk : Blk
{
    public const int First = 0x20, MaxPois = 700;
    public long X1, Y1, Side;
    public readonly List<PoiRec> Pois = new();
    public TileSpec Spec = null!;                     // for the spatial index (BBox, BlockId)
    public override int Type => 0x06;
    public override int Size => First + 20 * Pois.Count + 1;

    public override void Write(byte[] b)
    {
        W16(b, 8, First); W16(b, 10, Pois.Count);
        Wi32(b, 0x10, X1); Wi32(b, 0x14, Y1); Wi32(b, 0x18, X1 + Side); Wi32(b, 0x1C, Y1 + Side);
        int i = 0;
        foreach (var p in Pois.OrderBy(p => (p.X - X1) / 64).ThenBy(p => (p.Y - Y1) / 64))
        {
            int o = First + 20 * i++;
            W32(b, o, p.Blk!.Id); W16(b, o + 4, p.Blk.S0Offset(p));
            W16(b, o + 6, (int)((p.X - X1) / 64)); W16(b, o + 8, (int)((p.Y - Y1) / 64));
            W16(b, o + 10, p.Category); W16(b, o + 12, 0); W16(b, o + 14, 0); W32(b, o + 16, p.PoiId);
        }
    }
}

public sealed partial class Directory
{
    public readonly List<PoiBlk> PoiBlocks = new();
    public List<TrieBlk> PoiTries = new();
    public readonly List<PoiTileBlk> PoiTiles = new();
    public Spatial.LayerIndex? PoiIndex;

    /// <summary>POIs of the fully detailed regions: named, with a category, placed in a city, each
    /// linked to the nearest street link. Per city and category, sorted by name, with one-level
    /// first-letter tries (0x11) as on the disc; and the 0x06 map tiles with their spatial index.</summary>
    public void PlanPois(Osm.RoadModel m, Cities cities, List<TileSpec> streetTiles, Action<string> log)
    {
        var recOf = CityRecs.ToDictionary(r => r.City.Index);
        if (!m.Arrays.Any(a => a.name == "poi_x")) return;
        var px = m.Get<int>("poi_x"); var py = m.Get<int>("poi_y"); var code = m.Get<byte>("poi_code");
        var nm = m.Get<int>("poi_name"); var brand = m.Get<int>("poi_brand"); var street = m.Get<int>("poi_street");
        var house = m.Get<int>("poi_housenumber"); var phone = m.Get<int>("poi_phone"); var post = m.Get<int>("poi_postcode");
        string S(int i) => i == 0 ? "" : m.Strings[i];

        // street links by position, for each POI's road
        var nx = m.Get<int>("node_x"); var ny = m.Get<int>("node_y");
        var frm = m.Get<int>("link_frm"); var to = m.Get<int>("link_to");
        var where = new List<(int x, int y, TileSpec sp, int k)>();
        foreach (var sp in streetTiles)
            for (int k = 0; k < sp.LinkSrc.Length; k++)
            {
                int l = sp.LinkSrc[k];
                where.Add(((int)(((long)nx[frm[l]] + nx[to[l]]) / 2), (int)(((long)ny[frm[l]] + ny[to[l]]) / 2), sp, k));
            }
        var roadGrid = new Cities.Grid(where.Select(w => (w.x, w.y)).ToList(), 50_000);       // ~1 km cells
        var grid = new Cities.Grid(cities.List.Select(x => (x.X, x.Y)).ToList());

        var pois = new List<PoiRec>();
        int noName = 0, noCity = 0, noRoad = 0;
        for (int i = 0; i < px.Length; i++)
        {
            string name = Tiles.CleanName(S(nm[i]));
            if (name == "") name = Tiles.CleanName(S(brand[i]));
            if (name == "") { noName++; continue; }
            int c = cities.Nearest(grid, px[i], py[i]);
            if (c < 0 || !recOf.ContainsKey(c)) { noCity++; continue; }
            var p = new PoiRec
            {
                City = c, Category = code[i], Name = name, X = px[i], Y = py[i],
                Street = Tiles.CleanName(S(street[i])), House = Tiles.CleanName(S(house[i])),
                Phone = Tiles.CleanName(S(phone[i])), Postcode = Tiles.CleanName(S(post[i])),
            };
            double best = double.MaxValue;
            foreach (int k in roadGrid.Near(p.X, p.Y, 15_000))       // about 300 m
            {
                double dx = where[k].x - p.X, dy = where[k].y - p.Y, d = dx * dx + dy * dy;
                if (d < best) { best = d; p.Road = (where[k].sp, where[k].k); }
            }
            if (p.Road == null) { noRoad++; continue; }               // not routable: leave it out
            pois.Add(p);
        }
        for (int i = 0; i < pois.Count; i++) pois[i].PoiId = (uint)((0x0200 + i / 60000) << 16 | (i % 60000 + 1));

        // 0x10 blocks: city, category, name; a first letter never straddles two blocks
        var cityNames = cities.List.Select(c => c.Name).ToList();
        PoiBlk? cur = null;
        var groups = pois.GroupBy(p => (p.City, p.Category)).OrderBy(g => g.Key.City).ThenBy(g => g.Key.Category);
        foreach (var g in groups)
        {
            var sorted = g.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
            foreach (var letter in sorted.GroupBy(p => p.Name[0]))
            {
                var ls = letter.ToList();
                foreach (var chunk in ls.Chunk(200))
                {
                    if (cur == null || cur.SizeWith(chunk) > MaxBlock)
                    {
                        var nb = new PoiBlk(cityNames) { Prev = cur };
                        if (cur != null) cur.Next = nb;
                        PoiBlocks.Add(cur = nb);
                    }
                    foreach (var p in chunk) cur.Add(p);
                }
            }
            var root = Trie.Build(sorted.Select(p => new Trie.Item(p.Name, p.Blk!, p.Blk!.S0Offset(p), 8)).ToList(), oneLevel: true);
            recOf[g.Key.City].Pois.Add((g.Key.Category, root));
        }
        PoiTries = Trie.Pack(0x11, CityRecs.SelectMany(r => r.Pois.Select(x => x.root)));

        // 0x06 tiles: quadtree cells of the disc's root square, at most MaxPois each, side <= the index cell
        var (rx, ry, rs) = Tiles.DiscRoot;
        void Split(long x, long y, long side, List<PoiRec> ps)
        {
            if (ps.Count == 0) return;
            if ((ps.Count <= PoiTileBlk.MaxPois && side <= Spatial.CellSide[6]) || side <= 40960)
            {
                var t = new PoiTileBlk { X1 = x, Y1 = y, Side = side };
                t.Pois.AddRange(ps.Take(4000));
                var tile = new Tile26(6) { BBox = ((int)x, (int)y, (int)(x + side), (int)(y + side)) };
                t.Spec = new TileSpec(6, new Cell(x, y, side, side), tile);
                PoiTiles.Add(t);
                return;
            }
            long h = side / 2;
            foreach (var (qx, qy) in new[] { (x, y), (x, y + h), (x + h, y), (x + h, y + h) })
                Split(qx, qy, h, ps.Where(p => p.X >= qx && p.X < qx + h && p.Y >= qy && p.Y < qy + h).ToList());
        }
        Split(rx, ry, rs, pois.Where(p => p.X >= rx && p.X < rx + rs && p.Y >= ry && p.Y < ry + rs).ToList());
        PoiIndex = Spatial.Build(6, PoiTiles.Select(t => t.Spec).ToList(), rx, ry, rs);

        Stats["pois"] = pois.Count;
        Stats["pois_without_name"] = noName; Stats["pois_without_city"] = noCity; Stats["pois_without_road"] = noRoad;
        log($"POIs: {pois.Count:N0} in {PoiBlocks.Count:N0} 0x10 + {PoiTries.Count:N0} 0x11 blocks, {PoiTiles.Count:N0} 0x06 tiles " +
            $"(left out: {noName:N0} unnamed, {noCity:N0} outside the cities, {noRoad:N0} without a road within 300 m)");
    }
}
