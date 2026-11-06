// CF=1 codec for BLOCK_TYPE 0x00 (street tiles): a C# port of OpenCarin's
// carin/parser/cf1/core.py and decoder_00.py (decode_type00 / encode_type00, text coding,
// choose_words) at revision 1e5e077. Copyright (c) 2026 Flavio De Musso, MIT licence;
// see LICENSE-OpenCarin.txt in this folder. The port keeps the Python structure and
// order of operations, so its output is bit-identical to OpenCarin's (tests/Cf1Tests.cs).
using System.Buffers.Binary;

namespace Osm2Carin.Core.Cf1;

public class Cf1Exception(string message) : Exception(message);

public static class T
{
    public const int DescBase = 0x05, RecS6 = 0x06, RecS4 = 0x08, TailS4 = 0x09, Prolog = 0x0B, RecS7 = 0x0C,
        RecS9 = 0x0F, RecS5 = 0x10, RecS3 = 0x12, RecS11 = 0x13, RecS10 = 0x14, RecS12 = 0x15, RecS0 = 0x40,
        RecS13 = 0x4C, RecS14 = 0x59;

    public static readonly byte[] CharMap = Convert.FromHexString(
        "61657374720020646768696c6e6f" + "e0e1e2e3e4e5e7e8e9eaebecedeeeff1f2f3f4f5f6f8f9fafbfcfdac");

    public static int CharIndex(byte c) => Array.IndexOf(CharMap, c);
}

public static class Bits
{
    /// <summary>pbp+0x4a68: bits to represent 0..n-1, 16-bit arithmetic.</summary>
    public static int Needed(int n)
    {
        int v = n & 0xFFFF;
        if (v == 1) return 1;
        v = (v - 1) & 0xFFFF;
        int o = 0;
        while (v != 0) { v >>= 1; o++; }
        return o;
    }
}

/// <summary>MSB-first bit reader (BFEXTU convention); bits past the end read as 0.</summary>
public sealed class BitReader(byte[] data, int start)
{
    long pos;

    public int Get(int width)
    {
        if (width <= 0) return 0;
        long bit0 = (long)start * 8 + pos;
        pos += width;
        uint o = 0;
        for (int i = 0; i < width; i++)
        {
            long b = bit0 + i;
            int byteIdx = (int)(b >> 3);
            uint bit = byteIdx < data.Length ? (uint)(data[byteIdx] >> (7 - (int)(b & 7))) & 1u : 0u;
            o = (o << 1) | bit;
        }
        return (int)o;
    }

    public uint GetU(int width) => (uint)Get(width);
}

/// <summary>MSB-first bit writer.</summary>
public sealed class BitWriter
{
    readonly List<byte> buf = new(4096);
    long pos;

    public void Put(int width, long value)
    {
        for (int shift = width - 1; shift >= 0; shift--)
        {
            int byteIdx = (int)(pos >> 3);
            if (byteIdx >= buf.Count) buf.Add(0);
            if (((value >> shift) & 1) != 0) buf[byteIdx] |= (byte)(1 << (7 - (int)(pos & 7)));
            pos++;
        }
    }

    public byte[] ToArray() => buf.ToArray();
}

public readonly record struct Entry(int Off, int Count);

/// <summary>Decoder for BLOCK_TYPE 0x00 (db_pub+0x3d04), passes 0x14 / 0x15 / 0x17 / 0x1B by DB-REL.</summary>
public sealed class Decoder00
{
    readonly IReadOnlyDictionary<int, int> table;
    readonly byte[] src;
    public readonly byte[] Dst;
    int cursor;
    readonly int ptrbits;
    public byte[] Widths = [0, 0];
    BitReader bits = null!;
    readonly Dictionary<string, int> pb = new();
    readonly int dbrel, subrel;
    int cacheS7 = 1, cacheS2 = 1;

    Decoder00(byte[] raw, IReadOnlyDictionary<int, int> table, int dbrel, int subrel, int total)
    {
        this.table = table; src = raw; this.dbrel = dbrel; this.subrel = subrel;
        Dst = new byte[total];
        ptrbits = Bits.Needed(total);
    }

    int Tv(int idx) => table.TryGetValue(idx, out var v) ? v : throw new Cf1Exception($"RECORD_SIZE_TABLE has no entry {idx:x}");
    int G(int w) => bits.Get(w);
    void W(int off, int v) => BinaryPrimitives.WriteUInt16BigEndian(Dst.AsSpan(off), (ushort)(v & 0xFFFF));
    int Rw(int off) => BinaryPrimitives.ReadUInt16BigEndian(Dst.AsSpan(off));
    void B(int off, int v) => Dst[off] = (byte)(v & 0xFF);
    void L(int off, uint v) => BinaryPrimitives.WriteUInt32BigEndian(Dst.AsSpan(off), v);

    Entry Ent(int idx)
    {
        int b = Tv(T.DescBase) + 4 * idx;
        return new Entry(BinaryPrimitives.ReadUInt16BigEndian(Dst.AsSpan(b)), BinaryPrimitives.ReadUInt16BigEndian(Dst.AsSpan(b + 2)));
    }

    byte[] CopyRaw(int dstOff, int n)
    {
        if (cursor + n > src.Length) throw new Cf1Exception($"stream truncated: need {n} bytes at {cursor}");
        var chunk = src.AsSpan(cursor, n).ToArray();
        if (dstOff >= 0) chunk.CopyTo(Dst, dstOff);
        cursor += n;
        return chunk;
    }

    void CopySection(int idx, int rec, bool plus1)
    {
        var e = Ent(idx);
        CopyRaw(e.Off, e.Count * rec + (plus1 ? rec : 0));
    }

    IEnumerable<(int cur, int prev, bool first)> Walk(int idx, int rec)
    {
        var e = Ent(idx);
        int cur = e.Off, end = e.Off + e.Count * rec, prev = -1;
        while (cur < end)
        {
            yield return (cur, prev, cur == e.Off);
            prev = cur; cur += rec;
        }
    }

    int Delta(int prev)
    {
        int width = Widths[1];
        if (G(1) != 0)
        {
            if (G(1) != 0) return G(16);
            return (prev - G(width)) & 0xFFFF;
        }
        return (prev + G(width)) & 0xFFFF;
    }

    void A14(int idx)
    {
        int pbt = ptrbits;
        foreach (var (cur, prev, _) in Walk(idx, Tv(T.RecS0)))
        {
            if (G(1) != 0)
            {
                int v2 = G(pbt), v4 = G(pbt - 1) << 1;
                if (idx != 0) { W(cur + 2, v2); W(cur + 4, v4); }
            }
            else if (idx != 0 && prev >= 0)
            {
                W(cur + 2, Rw(prev + 2)); W(cur + 4, Rw(prev + 4));
            }
            W(cur, G(pbt));
        }
    }

    void A15S0()
    {
        foreach (var (cur, prev, _) in Walk(0, Tv(T.RecS0)))
        {
            if (G(1) != 0) W(cur + 2, G(16));
            else if (prev >= 0) W(cur + 2, Rw(prev + 2));
        }
    }

    void A17S0() { foreach (var (cur, _, _) in Walk(0, Tv(T.RecS0))) W(cur + 4, G(ptrbits - 1) << 1); }

    void A17S1()
    {
        foreach (var (cur, _, _) in Walk(1, Tv(T.RecS0)))
        {
            W(cur + 6, G(ptrbits - 1) << 1);
            W(cur + 8, G(ptrbits - 1) << 1);
        }
    }

    void A17S2()
    {
        int[] step = [0, 0], acc = [0, 0];
        foreach (var (cur, _, _) in Walk(2, Tv(T.RecS0)))
        {
            int k = 0;
            foreach (int off in (int[])[6, 8])
            {
                if (G(1) != 0) step[k] = (G(ptrbits - 1) << 1) & 0xFFFF;
                acc[k] = (step[k] + acc[k]) & 0xFFFF;
                W(cur + off, acc[k]);
                k++;
            }
        }
    }

    void Bpass(int kind)
    {
        var e4 = Ent(4);
        var e2 = Ent(2); var e7 = Ent(7); var e10 = Ent(10); var e11 = Ent(11); var e12 = Ent(12); var e13 = Ent(13);
        int rec = Tv(T.RecS4), tail = Tv(T.TailS4);
        int start = e4.Off, end = e4.Off + e4.Count * rec, cur = start, prev = -1;

        void S13Ptr(int at, int prv)
        {
            if (G(1) != 0) W(at + 0x16, e13.Off + G(pb["s13"]) * Tv(T.RecS13));
            else if (prv >= 0) W(at + 0x16, Rw(prv + 0x16));
        }

        void PtrGroup(int at)
        {
            W(at + 0x12, e10.Off + G(pb["s10"]) * Tv(T.RecS10));
            W(at + 0x14, e12.Off + G(pb["s12"]) * Tv(T.RecS12));
            W(at + tail + 4, e11.Off + G(pb["s11"]) * Tv(T.RecS11));
        }

        while (cur < end)
        {
            if (kind == 0x14)
            {
                if (cur != start) Array.Copy(Dst, prev, Dst, cur, rec);
                if (G(1) != 0) PtrGroup(cur);
                if (G(1) != 0)
                {
                    B(cur + 0x0A, G(8)); B(cur + 0x0B, G(8)); B(cur + 0x10, G(8)); B(cur + 0x11, G(8));
                    W(cur + tail + 2, G(16));
                }
                W(cur + 0x00, G(ptrbits - 1) << 1);
                W(cur + 0x02, G(ptrbits - 1) << 1);
                if (G(1) != 0) cacheS7 = e7.Off + G(pb["s7"]) * Tv(T.RecS7);
                W(cur + 0x04, cacheS7);
                foreach (int off in (int[])[0x06, 0x08])
                {
                    int t = G(pb["s4"]);
                    W(cur + off, t == e4.Count ? 0 : e4.Off + t * rec);
                }
                int width = G(1) != 0 ? 16 : Widths[0];
                W(cur + 0x0C, G(width));
                B(cur + 0x0E, G(8)); B(cur + 0x0F, G(8));
                if (G(1) != 0) cacheS2 = e2.Off + G(pb["s2"]) * Tv(T.RecS0);
                W(cur + tail, cacheS2);
            }
            else if (kind == 0x15) S13Ptr(cur, prev);
            else if (kind == 0x1B)
            {
                if (G(1) != 0) { B(cur + 0x18, G(8)); B(cur + 0x19, G(8)); }
                else if (prev >= 0) { Dst[cur + 0x18] = Dst[prev + 0x18]; Dst[cur + 0x19] = Dst[prev + 0x19]; }
            }
            prev = cur; cur += rec;
        }
        if (kind == 0x15) S13Ptr(cur, prev);
        if (kind != 0x14) return;
        if (G(1) != 0) PtrGroup(cur);
        else if (prev >= 0)
        {
            W(cur + 0x12, Rw(prev + 0x12)); W(cur + 0x14, Rw(prev + 0x14)); W(cur + tail + 4, Rw(prev + tail + 4));
        }
        if (G(1) != 0) cacheS7 = e7.Off + G(pb["s7"]) * Tv(T.RecS7);
        W(cur + 0x04, cacheS7);
    }

    void Xy(int idx, int recsize, bool extra)
    {
        var e4 = Ent(4);
        int o5 = Tv(T.RecS5);
        foreach (var (cur, prev, first) in Walk(idx, recsize))
        {
            if (G(1) != 0) { B(cur + 6, G(8)); B(cur + 7, G(3)); }
            else if (prev >= 0) { Dst[cur + 6] = Dst[prev + 6]; Dst[cur + 7] = Dst[prev + 7]; }
            if (first) { W(cur, G(16)); W(cur + 2, G(16)); }
            else { W(cur, Delta(Rw(prev))); W(cur + 2, Delta(Rw(prev + 2))); }
            W(cur + 4, e4.Off + G(pb["s4"]) * Tv(T.RecS4));
            if (extra)
            {
                L(cur + o5, bits.GetU(32));
                W(cur + o5 + 4, G(subrel >= 9 ? 16 : 14));
            }
        }
    }

    void E7()
    {
        foreach (var (cur, prev, first) in Walk(7, Tv(T.RecS7)))
        {
            if (first) { W(cur, G(16)); W(cur + 2, G(16)); }
            else { W(cur, Delta(Rw(prev))); W(cur + 2, Delta(Rw(prev + 2))); }
            B(cur + 4, G(3));
        }
    }

    void F11()
    {
        foreach (var (cur, _, _) in Walk(11, Tv(T.RecS11)))
        {
            W(cur, G(ptrbits)); W(cur + 2, G(ptrbits)); W(cur + 4, G(1));
        }
    }

    void S13()
    {
        foreach (var (cur, _, _) in Walk(13, Tv(T.RecS13)))
        {
            L(cur, bits.GetU(32)); W(cur + 4, G(ptrbits)); B(cur + 6, G(8)); B(cur + 7, G(8));
        }
    }

    void S14()
    {
        foreach (var (cur, prev, _) in Walk(14, Tv(T.RecS14)))
        {
            if (G(1) != 0) W(cur, G(ptrbits)); else if (prev >= 0) W(cur, Rw(prev));
            if (G(1) != 0) B(cur + 2, G(8)); else if (prev >= 0) Dst[cur + 2] = Dst[prev + 2];
            if (G(1) != 0) B(cur + 3, G(5)); else if (prev >= 0) Dst[cur + 3] = Dst[prev + 3];
        }
    }

    static readonly (int idx, int key)[] SectRec =
    [
        (0, T.RecS0), (1, T.RecS0), (2, T.RecS0), (3, T.RecS3), (4, T.RecS4), (5, T.RecS5), (6, T.RecS6),
        (7, T.RecS7), (9, T.RecS9), (10, T.RecS10), (11, T.RecS11), (12, T.RecS12), (13, T.RecS13),
    ];

    int SectionsEnd()
    {
        int hi = 0;
        foreach (var (idx, key) in SectRec)
        {
            var e = Ent(idx);
            if (e.Count == 0 || !table.TryGetValue(key, out var rec)) continue;
            hi = Math.Max(hi, e.Off + e.Count * rec);
        }
        return hi;
    }

    void Text()
    {
        int start = G(ptrbits), end = G(ptrbits);
        if (start == 0 && end == 0) return;
        var words = new byte[6][];
        for (int i = 0; i < 6; i++)
        {
            int n = G(5);
            words[i] = new byte[n];
            for (int j = 0; j < n; j++) words[i][j] = (byte)G(7);
        }
        int floor = SectionsEnd();
        bool ok = start <= end && end < Dst.Length && start >= floor;
        int p = start;
        while (p <= end && p < Dst.Length)
        {
            int code = G(2);
            int v;
            if (code == 0) v = T.CharMap[G(1)];
            else if (code == 1) v = T.CharMap[2 + G(2)];
            else if (code == 2) v = T.CharMap[6 + G(3)];
            else
            {
                v = G(7);
                if (v <= 0x26)
                {
                    if (v > 0x1B)
                    {
                        int wi = v - 0x21;
                        if (wi < 0) wi += 6;             // Python negative indexing
                        var w = words[wi];
                        if (ok) for (int j = 0; j < w.Length && p + j < Dst.Length; j++) Dst[p + j] = w[j];
                        p += w.Length;
                        continue;
                    }
                    v = T.CharMap[14 + v];
                }
            }
            if (ok) Dst[p] = (byte)v;
            p++;
        }
    }

    void Run()
    {
        CopyRaw(0, Tv(T.Prolog));
        pb["s2"] = Bits.Needed(Ent(2).Count);
        pb["s4"] = Bits.Needed(Ent(4).Count + 1);
        pb["s7"] = Bits.Needed(Ent(7).Count + 1);
        pb["s10"] = Bits.Needed(Ent(10).Count + 1);
        pb["s11"] = Bits.Needed(Ent(11).Count + 1);
        pb["s12"] = Bits.Needed(Ent(12).Count + 1);
        Widths = CopyRaw(-1, 2);
        CopySection(3, Tv(T.RecS3), true);
        CopySection(9, Tv(T.RecS9), false);
        CopySection(10, Tv(T.RecS10), false);
        if (Ent(12).Count != 0) CopySection(12, Tv(T.RecS12), false);
        bits = new BitReader(src, cursor);

        for (int idx = 0; idx < 3; idx++) A14(idx);
        Bpass(0x14);
        Xy(5, Tv(T.RecS5), false);
        Xy(6, Tv(T.RecS6), true);
        E7();
        if (Ent(11).Count != 0) F11();
        Text();
        if (dbrel < 0x15) return;
        pb["s13"] = Bits.Needed(Ent(13).Count + 1);
        A15S0();
        Bpass(0x15);
        S13();
        if (dbrel < 0x17) return;
        S14();
        A17S2();
        A17S1();
        A17S0();
        if (G(1) != 0) Text();
        if (G(1) != 0) Text();
        if (dbrel < 0x1B) return;
        Bpass(0x1B);
    }

    /// <summary>Decode a CF=1 type 0x00 block (header included). Bytes 6/7 of the result hold the two widths.</summary>
    public static byte[] DecodeBlock(byte[] raw, IReadOnlyDictionary<int, int> table, int dbrel, int subrel, int sector)
    {
        int btype = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4));
        if ((raw[6] & 1) == 0) throw new Cf1Exception($"block not compressed (COMPRESSION_FLAG={raw[6]})");
        if (btype != 0) throw new Cf1Exception($"BLOCK_TYPE {btype:x2}: only 0x00 is ported");
        var d = new Decoder00(raw, table, dbrel, subrel, raw[7] * sector);
        d.Run();
        d.Dst[6] = d.Widths[0];
        d.Dst[7] = d.Widths[1];
        return d.Dst;
    }
}
