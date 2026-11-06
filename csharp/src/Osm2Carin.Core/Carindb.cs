using System.Buffers.Binary;

namespace Osm2Carin.Core;

/// <summary>Minimal ISO9660 root reader and carindb block walker (tools/carin.py iso_files / walk_blocks).</summary>
public static class Carindb
{
    public record struct Block(int Unit, int Units, int Type, int Comp, int Usize);

    public static Dictionary<string, (int Lba, int Size)> IsoFiles(string isoPath)
    {
        using var f = File.OpenRead(isoPath);
        var pvd = new byte[2048];
        f.Position = 16 * 2048;
        f.ReadExactly(pvd);
        int lba = BinaryPrimitives.ReadInt32LittleEndian(pvd.AsSpan(156 + 2));
        int size = BinaryPrimitives.ReadInt32LittleEndian(pvd.AsSpan(156 + 10));
        var data = new byte[size];
        f.Position = (long)lba * 2048;
        f.ReadExactly(data);
        var o = new Dictionary<string, (int, int)>();
        int i = 0;
        while (i < data.Length)
        {
            int ln = data[i];
            if (ln == 0) { i = (i / 2048 + 1) * 2048; continue; }
            int nl = data[i + 32];
            var name = System.Text.Encoding.ASCII.GetString(data, i + 33, nl).Split(';')[0];
            if (name != "\0" && name != "\u0001")
                o[name] = (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i + 2)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i + 10)));
            i += ln;
        }
        return o;
    }

    public static byte[] ReadCarindb(string isoPath)
    {
        var (lba, size) = IsoFiles(isoPath)["carindb"];
        using var f = File.OpenRead(isoPath);
        f.Position = (long)lba * 2048;
        var db = new byte[size];
        f.ReadExactly(db);
        return db;
    }

    public static IEnumerable<Block> Walk(byte[] db)
    {
        int s = 0, n = db.Length / 2048;
        while (s < n)
        {
            uint bid = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(s * 2048));
            int ln = (int)(bid & 0xFF);
            if ((bid >> 8) != s || ln == 0) { s++; continue; }
            int typ = BinaryPrimitives.ReadUInt16BigEndian(db.AsSpan(s * 2048 + 4));
            yield return new Block(s, ln, typ, db[s * 2048 + 6], db[s * 2048 + 7]);
            s += ln;
        }
    }

    public static byte[] Raw(byte[] db, Block b) => db.AsSpan(b.Unit * 2048, b.Units * 2048).ToArray();
}
