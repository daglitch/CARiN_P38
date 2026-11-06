using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Osm2Carin.Core;

/// <summary>numpy .npz read/write (1-D arrays of bool, u1, i4, i8), so the Python tools and the C# port
/// can load each other's models (tools/osm2carin/model.py save/load).</summary>
public static partial class Npz
{
    static string Descr(Array a) => a switch
    {
        bool[] => "|b1", byte[] => "|u1", int[] => "<i4", long[] => "<i8", double[] => "<f8",
        _ => throw new NotSupportedException(a.GetType().Name),
    };

    public static void Write(string path, IEnumerable<(string name, Array data)> arrays)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, a) in arrays)
        {
            var e = zip.CreateEntry(name + ".npy", CompressionLevel.Fastest);
            using var s = e.Open();
            string h = $"{{'descr': '{Descr(a)}', 'fortran_order': False, 'shape': ({a.Length},), }}";
            int total = 10 + h.Length + 1;
            h += new string(' ', (64 - total % 64) % 64) + "\n";
            s.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y']);   // not a u8 literal: U+0093 is two bytes in UTF-8
            s.WriteByte(1); s.WriteByte(0);
            s.Write(BitConverter.GetBytes((ushort)h.Length));
            s.Write(Encoding.ASCII.GetBytes(h));
            var bytes = new byte[Buffer.ByteLength(a)];
            Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
            s.Write(bytes);
        }
    }

    [GeneratedRegex(@"'descr':\s*'([^']+)'.*'shape':\s*\((\d*),?\)")]
    private static partial Regex HeaderRx();

    public static Dictionary<string, Array> Read(string path)
    {
        var o = new Dictionary<string, Array>();
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var b = ms.ToArray();
            int major = b[6];
            int hlen = major == 1 ? BitConverter.ToUInt16(b, 8) : BitConverter.ToInt32(b, 8);
            int hoff = major == 1 ? 10 : 12;
            var h = Encoding.ASCII.GetString(b, hoff, hlen);
            var m = HeaderRx().Match(h);
            if (!m.Success) throw new InvalidDataException($"npy header: {h}");
            string descr = m.Groups[1].Value;
            int n = m.Groups[2].Value == "" ? 1 : int.Parse(m.Groups[2].Value);
            Array a = descr switch
            {
                "|b1" => new bool[n], "|u1" => new byte[n], "<i4" => new int[n], "<i8" => new long[n], "<f8" => new double[n],
                _ => throw new NotSupportedException(descr),
            };
            Buffer.BlockCopy(b, hoff + hlen, a, 0, Buffer.ByteLength(a));
            o[Path.GetFileNameWithoutExtension(e.Name)] = a;
        }
        return o;
    }
}
