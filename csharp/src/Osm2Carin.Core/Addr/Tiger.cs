using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Osm2Carin.Core.Addr;

/// <summary>One side of a TIGER address range: the number at the start ("from") and at the end ("to")
/// of the edge as digitised, and its parity (E, O, B both, or blank).</summary>
public readonly record struct TigerSide(int From, int To, char Parity);

/// <summary>A TIGER/Line ADDRFEAT record: an edge (or part of one) with its street name, its shape in
/// CARiN units and the left / right address ranges of that direction.</summary>
public sealed class TigerEdge
{
    public string Name = "";
    public int[] X = [], Y = [];
    public TigerSide? L, R;
}

/// <summary>Reader for the US Census TIGER/Line ADDRFEAT files (one zipped shapefile per county,
/// https://www2.census.gov/geo/tiger/TIGER2025/ADDRFEAT/, public domain). Only the fields 3d needs:
/// FULLNAME, LFROMHN / LTOHN / RFROMHN / RTOHN, PARITYL / PARITYR, OFFSETL / OFFSETR.</summary>
public static class Tiger
{
    /// <summary>The ADDRFEAT zips of the given state FIPS codes in a folder ("06" California, "32" Nevada).</summary>
    public static List<string> Files(string dir, IEnumerable<string> stateFips) =>
        stateFips.SelectMany(s => System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, $"tl_*_{s}???_addrfeat.zip") : [])
            .OrderBy(f => f, StringComparer.Ordinal).ToList();

    public static readonly Dictionary<string, string> StateFips = new()
    {
        ["al"] = "01", ["ak"] = "02", ["az"] = "04", ["ar"] = "05", ["ca"] = "06", ["co"] = "08", ["ct"] = "09", ["de"] = "10",
        ["dc"] = "11", ["fl"] = "12", ["ga"] = "13", ["hi"] = "15", ["id"] = "16", ["il"] = "17", ["in"] = "18", ["ia"] = "19",
        ["ks"] = "20", ["ky"] = "21", ["la"] = "22", ["me"] = "23", ["md"] = "24", ["ma"] = "25", ["mi"] = "26", ["mn"] = "27",
        ["ms"] = "28", ["mo"] = "29", ["mt"] = "30", ["ne"] = "31", ["nv"] = "32", ["nh"] = "33", ["nj"] = "34", ["nm"] = "35",
        ["ny"] = "36", ["nc"] = "37", ["nd"] = "38", ["oh"] = "39", ["ok"] = "40", ["or"] = "41", ["pa"] = "42", ["ri"] = "44",
        ["sc"] = "45", ["sd"] = "46", ["tn"] = "47", ["tx"] = "48", ["ut"] = "49", ["vt"] = "50", ["va"] = "51", ["wa"] = "53",
        ["wv"] = "54", ["wi"] = "55", ["wy"] = "56",
    };

    public const string Year = "2025";
    public static string BaseUrl => $"https://www2.census.gov/geo/tiger/TIGER{Year}/ADDRFEAT/";

    /// <summary>Download the ADDRFEAT zips of these states that are not in dir yet (one per county, listed
    /// from the Census directory page); returns all of them.</summary>
    public static List<string> Fetch(IEnumerable<string> stateFips, string dir, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var fips = stateFips.Distinct().ToList();
        System.IO.Directory.CreateDirectory(dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Regions.UserAgent);
        var page = http.GetStringAsync(BaseUrl).GetAwaiter().GetResult();
        var names = System.Text.RegularExpressions.Regex.Matches(page, $@"tl_{Year}_(\d\d)\d\d\d_addrfeat\.zip")
            .Where(m => fips.Contains(m.Groups[1].Value)).Select(m => m.Value).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var missing = names.Where(n => !File.Exists(Path.Combine(dir, n))).ToList();
        if (missing.Count > 0)
        {
            log($"downloading {missing.Count} TIGER address-range files from {BaseUrl}");
            using var prog = new Progress("TIGER download", missing.Count, "files");
            foreach (var n in missing)
            {
                var bytes = http.GetByteArrayAsync(BaseUrl + n).GetAwaiter().GetResult();
                File.WriteAllBytes(Path.Combine(dir, n + ".part"), bytes);
                File.Move(Path.Combine(dir, n + ".part"), Path.Combine(dir, n), true);
                prog.Add();
            }
        }
        else log($"TIGER address ranges: {names.Count} county files cached in {dir}");
        return names.Select(n => Path.Combine(dir, n)).ToList();
    }

    public static List<TigerEdge> ReadZip(string path)
    {
        using var z = ZipFile.OpenRead(path);
        byte[] Read(string ext)
        {
            var e = z.Entries.First(e => e.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }
        var shp = Read(".shp"); var dbf = Read(".dbf");
        var rows = Dbf(dbf);
        var o = new List<TigerEdge>(rows.Count);
        int p = 100, i = 0;
        while (p + 8 <= shp.Length && i < rows.Count)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(p + 4)) * 2;
            var c = shp.AsSpan(p + 8, len);
            p += 8 + len;
            var row = rows[i++];
            if (BinaryPrimitives.ReadInt32LittleEndian(c) != 3) continue;          // PolyLine
            int nParts = BinaryPrimitives.ReadInt32LittleEndian(c[36..]), nPts = BinaryPrimitives.ReadInt32LittleEndian(c[40..]);
            int pts = 44 + 4 * nParts;
            var e = new TigerEdge { Name = row["FULLNAME"], X = new int[nPts], Y = new int[nPts] };
            for (int k = 0; k < nPts; k++)
            {
                double lon = BinaryPrimitives.ReadDoubleLittleEndian(c[(pts + 16 * k)..]);
                double lat = BinaryPrimitives.ReadDoubleLittleEndian(c[(pts + 16 * k + 8)..]);
                (e.X[k], e.Y[k]) = Geo.FromLonLat(lon, lat);
            }
            e.L = Side(row["LFROMHN"], row["LTOHN"], row["PARITYL"], row["OFFSETL"]);
            e.R = Side(row["RFROMHN"], row["RTOHN"], row["PARITYR"], row["OFFSETR"]);
            if (e.Name != "" && (e.L != null || e.R != null) && nPts >= 2) o.Add(e);
        }
        return o;
    }

    /// <summary>A side's range, or null when it has none, is an offset range, or is not a plain number
    /// the disc can store (u16 below 0x7FFF).</summary>
    static TigerSide? Side(string from, string to, string parity, string offset)
    {
        if (offset == "Y") return null;
        if (!int.TryParse(from, out int f) || !int.TryParse(to, out int t)) return null;
        if (f < 0 || t < 0 || f >= HouseNumbers.None || t >= HouseNumbers.None) return null;
        return new TigerSide(f, t, parity.Length > 0 ? parity[0] : ' ');
    }

    static List<Dictionary<string, string>> Dbf(byte[] d)
    {
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
        int hl = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)), rl = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(10));
        var f = new List<(string name, int len)>();
        for (int p = 32; d[p] != 0x0D; p += 32)
            f.Add((Encoding.ASCII.GetString(d, p, 11).TrimEnd('\0'), d[p + 16]));
        var o = new List<Dictionary<string, string>>(n);
        for (int r = 0; r < n; r++)
        {
            int p = hl + r * rl + 1;
            var row = new Dictionary<string, string>(f.Count);
            foreach (var (name, len) in f) { row[name] = Encoding.Latin1.GetString(d, p, len).Trim(); p += len; }
            o.Add(row);
        }
        return o;
    }
}
