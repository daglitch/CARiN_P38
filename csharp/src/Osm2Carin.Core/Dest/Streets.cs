using System.Buffers.Binary;
using Osm2Carin.Core.Roads;

namespace Osm2Carin.Core.Dest;

/// <summary>One street of a city: its name and the runs of road links it has in street tiles.</summary>
public sealed class Street
{
    public int City;
    public string Name = "";
    public readonly List<(TileSpec sp, int k0, int n)> Runs = new();
    public StreetBlk? Blk;
    public int S0Index;                                // first S0 record in Blk
}

/// <summary>0x0E street directory block, plain (DB-REL 26, as the disc's 48 plain ones):
///   +0x08 descriptors S0 {0x28, n0}, S1, S2; +0x20 next / +0x24 previous 0x0E BLOCK_ID;
///   S0 8 B: u16 name, u8 flags 0, u8 language 2 (English), u16 locality 0, u16 -> S1;
///   S1 4 B: u16 -> S2, u8 count, u8 has house numbers (0);
///   S2 24 B: i32 x, i32 y (target tile centre), 4 x u16 0x7FFF (no house numbers),
///            u32 tile BLOCK_ID, u16 offset of the first section 4 record in the decoded tile, u16 count;
///   then the names. A street with more than 255 runs gets one S0/S1 pair per 255.</summary>
public sealed class StreetBlk : Blk
{
    public const int First = 0x28;
    public readonly List<Street> Streets = new();
    public int N0, N2;
    public readonly TextPool Text = new();
    public StreetBlk? Next, Prev;
    public override int Type => 0x0E;
    public override int Size => First + 8 * N0 + 4 * N0 + 24 * N2 + Text.Length;

    public static int Pairs(Street s) => (s.Runs.Count + 254) / 255;

    public int SizeWith(Street s) => Size + 12 * Pairs(s) + 24 * s.Runs.Count + Text.Measure(s.Name);

    public void Add(Street s)
    {
        s.Blk = this; s.S0Index = N0;
        N0 += Pairs(s); N2 += s.Runs.Count;
        Text.Add(s.Name);
        Streets.Add(s);
    }

    public int S0Offset(Street s) => First + 8 * s.S0Index;

    public override void Write(byte[] b)
    {
        int s1 = First + 8 * N0, s2 = s1 + 4 * N0, txt = s2 + 24 * N2;
        W16(b, 8, First); W16(b, 10, N0); W16(b, 12, s1); W16(b, 14, N0); W16(b, 16, s2); W16(b, 18, N2);
        W32(b, 0x20, Next?.Id ?? 0); W32(b, 0x24, Prev?.Id ?? 0);
        int i0 = 0, i2 = 0;
        foreach (var s in Streets)
        {
            int name = txt + Text.Add(s.Name);
            for (int p = 0; p < Pairs(s); p++)
            {
                int o0 = First + 8 * i0, o1 = s1 + 4 * i0;
                var runs = s.Runs.Skip(255 * p).Take(255).ToList();
                W16(b, o0, name); b[o0 + 2] = 0; b[o0 + 3] = 2; W16(b, o0 + 4, 0); W16(b, o0 + 6, o1);
                W16(b, o1, s2 + 24 * i2); b[o1 + 2] = (byte)runs.Count; b[o1 + 3] = 0;
                foreach (var (sp, k0, n) in runs)
                {
                    int o = s2 + 24 * i2;
                    var (x1, y1, x2, y2) = sp.Tile.BBox;
                    Wi32(b, o, ((long)x1 + x2) / 2); Wi32(b, o + 4, ((long)y1 + y2) / 2);
                    for (int h = 0; h < 4; h++) W16(b, o + 8 + 2 * h, 0x7FFF);
                    W32(b, o + 16, sp.BlockId);
                    W16(b, o + 20, Directory.S4Offset(sp, k0)); W16(b, o + 22, n);
                    i2++;
                }
                i0++;
            }
        }
        Text.CopyTo(b, txt);
    }
}

/// <summary>A city: its street trie, its POI index (3c) and its centre.</summary>
public sealed class CityRec
{
    public Cities.City City = null!;
    public Trie.Node? StreetRoot;
    public readonly List<(int category, Trie.Node root)> Pois = new();
    public (TileSpec sp, int k)? Centre;
    public CityBlk? Blk;
    public int Index, S3Index;
}

/// <summary>0x0C city block (DB-REL 26): descriptors S0 {0x28, n}, S1, S2 0, S3, S4 0, S5 {o, 0};
/// +0x20 next / +0x24 previous 0x0C BLOCK_ID.
///   S0 8 B: u16 name, u16 flags 2, u16 post town 0, u16 -> S1;
///   S1 20 B: 0x0F root (u32 BLOCK_ID, u16 offset, u16 count), u16 -> S3, u16 S3 count,
///            u32 city-centre tile BLOCK_ID, u16 offset of a section 4 record in it, u16 0;
///   S3 12 B per POI category: 0x11 root (u32, u16 offset, u16 count), u16 category, u16 -> S5;
///   S5 (brands) empty; then the names.</summary>
public sealed class CityBlk : Blk
{
    public const int First = 0x28;
    public readonly List<CityRec> Cities = new();
    public int N3;
    public readonly TextPool Text = new();
    public CityBlk? Next, Prev;
    public override int Type => 0x0C;
    public override int Size => First + 28 * Cities.Count + 12 * N3 + Text.Length;
    public int SizeWith(CityRec c) => Size + 28 + 12 * c.Pois.Count + Text.Measure(c.City.Name);

    public void Add(CityRec c)
    {
        c.Blk = this; c.Index = Cities.Count; c.S3Index = N3;
        N3 += c.Pois.Count;
        Text.Add(c.City.Name);
        Cities.Add(c);
    }

    public int S0Offset(CityRec c) => First + 8 * c.Index;

    public override void Write(byte[] b)
    {
        int n = Cities.Count, s1 = First + 8 * n, s3 = s1 + 20 * n, s5 = s3 + 12 * N3, txt = s5;
        W16(b, 8, First); W16(b, 10, n); W16(b, 12, s1); W16(b, 14, n);
        W16(b, 20, s3); W16(b, 22, N3); W16(b, 28, s5); W16(b, 30, 0);
        W32(b, 0x20, Next?.Id ?? 0); W32(b, 0x24, Prev?.Id ?? 0);
        foreach (var c in Cities)
        {
            int o0 = First + 8 * c.Index, o1 = s1 + 20 * c.Index;
            W16(b, o0, txt + Text.Add(c.City.Name)); W16(b, o0 + 2, 2); W16(b, o0 + 4, 0); W16(b, o0 + 6, o1);
            if (c.StreetRoot is Trie.Node r) { W32(b, o1, r.Blk!.Id); W16(b, o1 + 4, r.Offset); W16(b, o1 + 6, r.Recs.Count); }
            W16(b, o1 + 8, c.Pois.Count > 0 ? s3 + 12 * c.S3Index : 0); W16(b, o1 + 10, c.Pois.Count);
            if (c.Centre is (TileSpec sp, int k)) { W32(b, o1 + 12, sp.BlockId); W16(b, o1 + 16, Directory.S4Offset(sp, k)); }
            for (int p = 0; p < c.Pois.Count; p++)
            {
                var (cat, root) = c.Pois[p];
                int o = s3 + 12 * (c.S3Index + p);
                W32(b, o, root.Blk!.Id); W16(b, o + 4, root.Offset); W16(b, o + 6, root.Recs.Count);
                W16(b, o + 8, cat); W16(b, o + 10, s5);
            }
        }
        Text.CopyTo(b, txt);
    }
}

/// <summary>Plans the destination structures for 3b (streets) and 3c (POIs).</summary>
public sealed partial class Directory
{
    public readonly List<StreetBlk> StreetBlocks = new();
    public readonly List<CityBlk> CityBlocks = new();
    public List<TrieBlk> StreetTries = new(), CityTrie = new();
    public readonly List<CityRec> CityRecs = new();
    public Trie.Node CityRoot = null!;
    public readonly System.Text.Json.Nodes.JsonObject Stats = new();

    /// <summary>Offset of section 4 record k in the decoded tile (30-byte street links).</summary>
    public static int S4Offset(TileSpec sp, int k)
    {
        int o4 = sp.Decoded.Length >= 26 ? BinaryPrimitives.ReadUInt16BigEndian(sp.Decoded.AsSpan(8 + 4 * 4)) : 0;
        return o4 + 30 * k;
    }

    /// <summary>Streets per city from the street tiles: every run of consecutive section 4 records with
    /// the same name and city is one link of that street.</summary>
    public void PlanStreets(Osm.RoadModel m, Cities cities, List<TileSpec> streetTiles, Action<string> log)
    {
        var nameIx = m.Get<int>("link_name"); var refIx = m.Get<int>("link_ref");
        string Nm(int src) => Tiles.CleanName(nameIx[src] != 0 ? m.Strings[nameIx[src]] : refIx[src] != 0 ? m.Strings[refIx[src]] : "");
        var byKey = new Dictionary<(int city, string name), Street>();
        var nx = m.Get<int>("node_x"); var ny = m.Get<int>("node_y");
        var frm = m.Get<int>("link_frm");
        var centre = new Dictionary<int, (double d, TileSpec sp, int k)>();
        int truncated = 0;
        foreach (var sp in streetTiles)
        {
            var src = sp.LinkSrc;
            int k = 0;
            while (k < src.Length)
            {
                int c = cities.LinkCity[src[k]];
                string name = c >= 0 ? Nm(src[k]) : "";
                int j = k + 1;
                while (j < src.Length && cities.LinkCity[src[j]] == c && (c < 0 || Nm(src[j]) == name)) j++;
                if (c >= 0 && name != "")
                {
                    if (!byKey.TryGetValue((c, name), out var st)) byKey[(c, name)] = st = new Street { City = c, Name = name };
                    st.Runs.Add((sp, k, j - k));
                    var ci = cities.List[c];
                    double dx = nx[frm[src[k]]] - ci.X, dy = ny[frm[src[k]]] - ci.Y, d = dx * dx + dy * dy;
                    if (!centre.TryGetValue(c, out var best) || d < best.d) centre[c] = (d, sp, k);
                }
                k = j;
            }
        }
        const int maxRuns = 600;                       // one entry must fit a block
        foreach (var st in byKey.Values) if (st.Runs.Count > maxRuns) { st.Runs.RemoveRange(maxRuns, st.Runs.Count - maxRuns); truncated++; }

        // city records for every city with a street; streets in city order, then by name
        var streets = byKey.Values.OrderBy(s => s.City).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        foreach (var c in cities.List)
            if (centre.ContainsKey(c.Index)) CityRecs.Add(new CityRec { City = c, Centre = (centre[c.Index].sp, centre[c.Index].k) });
        var recOf = CityRecs.ToDictionary(r => r.City.Index);

        StreetBlk? cur = null;
        foreach (var st in streets)
        {
            if (cur == null || cur.SizeWith(st) > MaxBlock)
            {
                var nb = new StreetBlk { Prev = cur };
                if (cur != null) cur.Next = nb;
                StreetBlocks.Add(cur = nb);
            }
            cur.Add(st);
        }
        foreach (var g in streets.GroupBy(s => s.City))
            recOf[g.Key].StreetRoot = Trie.Build(g.Select(s => new Trie.Item(s.Name, s.Blk!, s.Blk!.S0Offset(s), 8)).ToList());
        StreetTries = Trie.Pack(0x0F, CityRecs.Where(r => r.StreetRoot != null).Select(r => r.StreetRoot!));
        Stats["cities"] = CityRecs.Count;
        Stats["streets"] = streets.Count;
        Stats["street_links"] = streets.Sum(s => s.Runs.Count);
        Stats["streets_truncated"] = truncated;
        log($"streets: {streets.Count:N0} in {CityRecs.Count:N0} cities, {streets.Sum(s => s.Runs.Count):N0} links; " +
            $"{StreetBlocks.Count:N0} 0x0E + {StreetTries.Count:N0} 0x0F blocks");
    }

    const int MaxBlock = Blk.MaxSize;

    /// <summary>City blocks and the country's city trie (after PlanStreets and PlanPois).</summary>
    public void PlanCities(Action<string> log)
    {
        CityBlk? cur = null;
        foreach (var c in CityRecs)
        {
            if (cur == null || cur.SizeWith(c) > MaxBlock)
            {
                var nb = new CityBlk { Prev = cur };
                if (cur != null) cur.Next = nb;
                CityBlocks.Add(cur = nb);
            }
            cur.Add(c);
        }
        CityRoot = Trie.Build(CityRecs.Select(c => new Trie.Item(c.City.Name, c.Blk!, c.Blk!.S0Offset(c), 8)).ToList());
        CityTrie = Trie.Pack(0x0D, [CityRoot]);
        log($"cities: {CityRecs.Count:N0} in {CityBlocks.Count:N0} 0x0C blocks, city trie {CityTrie.Count} 0x0D block(s), {CityRoot.Recs.Count} root letters");
    }
}
