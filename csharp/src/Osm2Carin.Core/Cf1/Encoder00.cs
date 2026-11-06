// CF=1 encoder for BLOCK_TYPE 0x00: C# port of OpenCarin's encode_type00 / _Enc00 / enc_text /
// choose_words (carin/parser/cf1/decoder_00.py at 1e5e077). Copyright (c) 2026 Flavio De Musso,
// MIT licence; see LICENSE-OpenCarin.txt. Mirrors the decoder pass by pass; every inherit flag
// is chosen by comparing the target with a shadow of what the decoder has written so far.
using System.Buffers.Binary;

namespace Osm2Carin.Core.Cf1;

public sealed class Encoder00
{
    readonly byte[] D;
    readonly IReadOnlyDictionary<int, int> table;
    readonly int dbrel, subrel;
    readonly byte[] sh;
    readonly BitWriter bw = new();
    readonly int pb;
    readonly byte[] widths;
    int? cacheS7, cacheS2;
    /// <summary>Bit positions (in the bit stream) of the 32-bit S6 neighbour BLOCK_IDs.</summary>
    readonly List<long> s6IdBits = new();
    Dictionary<string, int> pbits = new();

    Encoder00(byte[] decoded, IReadOnlyDictionary<int, int> table, int dbrel, int subrel)
    {
        D = decoded; this.table = table; this.dbrel = dbrel; this.subrel = subrel;
        sh = new byte[decoded.Length];
        pb = Bits.Needed(decoded.Length);
        widths = [decoded[6], decoded[7]];
    }

    int Tv(int i) => table[i];
    Entry Ent(int idx)
    {
        int b = Tv(T.DescBase) + 4 * idx;
        return new Entry(BinaryPrimitives.ReadUInt16BigEndian(D.AsSpan(b)), BinaryPrimitives.ReadUInt16BigEndian(D.AsSpan(b + 2)));
    }
    int Rw(int off) => BinaryPrimitives.ReadUInt16BigEndian(D.AsSpan(off));
    void W(int off, int v) => BinaryPrimitives.WriteUInt16BigEndian(sh.AsSpan(off), (ushort)(v & 0xFFFF));

    void Put(int width, long val, string what)
    {
        if (val < 0 || (width < 32 && (val >> width) != 0))
            throw new Cf1Exception($"{what}: 0x{val:x} does not fit in {width} bits");
        bw.Put(width, val);
    }

    bool Same(params (int o, int n)[] spans)
    {
        foreach (var (o, n) in spans)
            if (!D.AsSpan(o, n).SequenceEqual(sh.AsSpan(o, n))) return false;
        return true;
    }

    void Even(int off, string what)
    {
        int v = Rw(off);
        if ((v & 1) != 0) throw new Cf1Exception($"{what} at 0x{off:x}: odd value 0x{v:x}");
        Put(pb - 1, v >> 1, what);
        W(off, v);
    }

    void Index(int off, Entry e, int rec, int bits, string what)
    {
        int v = Rw(off);
        int d = (v - e.Off) & 0xFFFF;
        if (d % rec != 0) throw new Cf1Exception($"{what} at 0x{off:x}: 0x{v:x} is not a record of the section at 0x{e.Off:x}");
        Put(bits, d / rec, what);
        W(off, v);
    }

    void Delta(int off, int prev)
    {
        int v = Rw(off), width = widths[1];
        int up = (v - prev) & 0xFFFF, down = (prev - v) & 0xFFFF;
        if ((up >> width) == 0) { bw.Put(1, 0); bw.Put(width, up); }
        else if ((down >> width) == 0) { bw.Put(1, 1); bw.Put(1, 0); bw.Put(width, down); }
        else { bw.Put(1, 1); bw.Put(1, 1); bw.Put(16, v); }
        W(off, v);
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

    void CopySh(int dst, int src, int n) => Array.Copy(sh, src, sh, dst, n);

    // ---- pass 0x14 ------------------------------------------------------------
    void A14(int idx)
    {
        foreach (var (cur, prev, _) in Walk(idx, Tv(T.RecS0)))
        {
            if (idx == 0)
            {
                if (prev < 0) { bw.Put(1, 1); bw.Put(pb, 0); bw.Put(pb - 1, 0); }
                else bw.Put(1, 0);
            }
            else
            {
                if (prev >= 0) CopySh(cur + 2, prev + 2, 4);
                if (prev >= 0 && Same((cur + 2, 4))) bw.Put(1, 0);
                else
                {
                    bw.Put(1, 1);
                    Put(pb, Rw(cur + 2), $"S{idx} +2");
                    W(cur + 2, Rw(cur + 2));
                    Even(cur + 4, $"S{idx} +4");
                }
            }
            Put(pb, Rw(cur), $"S{idx} +0");
            W(cur, Rw(cur));
        }
    }

    void B14()
    {
        var e4 = Ent(4);
        var e2 = Ent(2); var e7 = Ent(7); var e10 = Ent(10); var e11 = Ent(11); var e12 = Ent(12);
        int rec = Tv(T.RecS4), tail = Tv(T.TailS4);
        pbits = new Dictionary<string, int>
        {
            ["s2"] = Bits.Needed(e2.Count), ["s4"] = Bits.Needed(e4.Count + 1), ["s7"] = Bits.Needed(e7.Count + 1),
            ["s10"] = Bits.Needed(e10.Count + 1), ["s11"] = Bits.Needed(e11.Count + 1), ["s12"] = Bits.Needed(e12.Count + 1),
        };
        int start = e4.Off, end = e4.Off + e4.Count * rec;
        (int, int)[] PtrSpans(int at) => [(at + 0x12, 2), (at + 0x14, 2), (at + tail + 4, 2)];

        void PtrGroup(int at)
        {
            Index(at + 0x12, e10, Tv(T.RecS10), pbits["s10"], "S4 +0x12");
            Index(at + 0x14, e12, Tv(T.RecS12), pbits["s12"], "S4 +0x14");
            Index(at + tail + 4, e11, Tv(T.RecS11), pbits["s11"], "S4 tail+4");
        }

        void S7(int at)
        {
            int v = Rw(at + 4);
            if (v == cacheS7) bw.Put(1, 0);
            else
            {
                bw.Put(1, 1);
                Index(at + 4, e7, Tv(T.RecS7), pbits["s7"], "S4 +4");
                cacheS7 = v;
            }
            W(at + 4, v);
        }

        int cur = start, prev = -1;
        while (cur < end)
        {
            if (cur != start) CopySh(cur, prev, rec);
            if (cur != start && Same(PtrSpans(cur))) bw.Put(1, 0);
            else { bw.Put(1, 1); PtrGroup(cur); }
            if (cur != start && Same((cur + 0x0A, 2), (cur + 0x10, 2), (cur + tail + 2, 2))) bw.Put(1, 0);
            else
            {
                bw.Put(1, 1);
                foreach (int o in (int[])[0x0A, 0x0B, 0x10, 0x11]) { bw.Put(8, D[cur + o]); sh[cur + o] = D[cur + o]; }
                bw.Put(16, Rw(cur + tail + 2)); W(cur + tail + 2, Rw(cur + tail + 2));
            }
            Even(cur, "S4 +0");
            Even(cur + 2, "S4 +2");
            S7(cur);
            foreach (int o in (int[])[0x06, 0x08])
            {
                int v = Rw(cur + o);
                if (v == 0) { Put(pbits["s4"], e4.Count, "S4 link"); W(cur + o, 0); }
                else Index(cur + o, e4, rec, pbits["s4"], "S4 link");
            }
            {
                int v = Rw(cur + 0x0C);
                if ((v >> widths[0]) != 0) { bw.Put(1, 1); bw.Put(16, v); }
                else { bw.Put(1, 0); bw.Put(widths[0], v); }
                W(cur + 0x0C, v);
            }
            foreach (int o in (int[])[0x0E, 0x0F]) { bw.Put(8, D[cur + o]); sh[cur + o] = D[cur + o]; }
            {
                int v = Rw(cur + tail);
                if (v == cacheS2) bw.Put(1, 0);
                else
                {
                    bw.Put(1, 1);
                    Index(cur + tail, e2, Tv(T.RecS0), pbits["s2"], "S4 tail+0");
                    cacheS2 = v;
                }
                W(cur + tail, v);
            }
            prev = cur; cur += rec;
        }
        if (prev >= 0)
            foreach (var (o, n) in PtrSpans(cur)) Array.Copy(sh, o - cur + prev, sh, o, n);
        if (prev >= 0 && Same(PtrSpans(cur))) bw.Put(1, 0);
        else { bw.Put(1, 1); PtrGroup(cur); }
        S7(cur);
    }

    void Xy(int idx, int rec, bool extra)
    {
        var e4 = Ent(4);
        foreach (var (cur, prev, first) in Walk(idx, rec))
        {
            if (prev >= 0) CopySh(cur + 6, prev + 6, 2);
            if (prev >= 0 && Same((cur + 6, 2))) bw.Put(1, 0);
            else
            {
                bw.Put(1, 1);
                Put(8, D[cur + 6], $"S{idx} +6");
                Put(3, D[cur + 7], $"S{idx} +7");
                sh[cur + 6] = D[cur + 6]; sh[cur + 7] = D[cur + 7];
            }
            Coords(cur, prev, first);
            Index(cur + 4, e4, Tv(T.RecS4), pbits["s4"], $"S{idx} +4");
            if (extra)
            {
                int o5 = Tv(T.RecS5);
                s6IdBits.Add(bw.Position);
                bw.Put(32, BinaryPrimitives.ReadUInt32BigEndian(D.AsSpan(cur + o5)));
                Array.Copy(D, cur + o5, sh, cur + o5, 4);
                Put(subrel >= 9 ? 16 : 14, Rw(cur + o5 + 4), $"S{idx} +{o5 + 4}");
                W(cur + o5 + 4, Rw(cur + o5 + 4));
            }
        }
    }

    void Coords(int cur, int prev, bool first)
    {
        foreach (int o in (int[])[0, 2])
        {
            if (first) { bw.Put(16, Rw(cur + o)); W(cur + o, Rw(cur + o)); }
            else Delta(cur + o, Rw(prev + o));
        }
    }

    void E7()
    {
        foreach (var (cur, prev, first) in Walk(7, Tv(T.RecS7)))
        {
            Coords(cur, prev, first);
            Put(3, D[cur + 4], "S7 +4");
            sh[cur + 4] = D[cur + 4];
        }
    }

    void F11()
    {
        foreach (var (cur, _, _) in Walk(11, Tv(T.RecS11)))
        {
            foreach (int o in (int[])[0, 2]) { Put(pb, Rw(cur + o), $"S11 +{o}"); W(cur + o, Rw(cur + o)); }
            Put(1, Rw(cur + 4), "S11 +4"); W(cur + 4, Rw(cur + 4));
        }
    }

    void TextPass()
    {
        int start = LayoutEnd(D, table, dbrel);
        int end = TextEnd(D, start);
        EncText(bw, D, start, end, pb, null);
        if (end >= start) Array.Copy(D, start, sh, start, end - start + 1);
    }

    // ---- pass 0x15 ------------------------------------------------------------
    void A15S0()
    {
        foreach (var (cur, prev, _) in Walk(0, Tv(T.RecS0)))
        {
            if (prev >= 0) CopySh(cur + 2, prev + 2, 2);
            if (prev >= 0 && Same((cur + 2, 2))) bw.Put(1, 0);
            else { bw.Put(1, 1); bw.Put(16, Rw(cur + 2)); W(cur + 2, Rw(cur + 2)); }
        }
    }

    void B15()
    {
        var e4 = Ent(4); var e13 = Ent(13);
        int bits = Bits.Needed(e13.Count + 1);
        int rec = Tv(T.RecS4);
        int cur = e4.Off, prev = -1, last = -1;
        foreach (var (c, p, _) in Walk(4, rec)) { S13Ptr(c, p, e13, bits); last = c; }
        if (e4.Count != 0) { cur = last + rec; prev = last; } else { cur = e4.Off; prev = -1; }
        S13Ptr(cur, prev, e13, bits);
    }

    void S13Ptr(int cur, int prev, Entry e13, int bits)
    {
        if (prev >= 0) CopySh(cur + 0x16, prev + 0x16, 2);
        if (prev >= 0 && Same((cur + 0x16, 2))) bw.Put(1, 0);
        else { bw.Put(1, 1); Index(cur + 0x16, e13, Tv(T.RecS13), bits, "S4 +0x16"); }
    }

    void S13()
    {
        foreach (var (cur, _, _) in Walk(13, Tv(T.RecS13)))
        {
            bw.Put(32, BinaryPrimitives.ReadUInt32BigEndian(D.AsSpan(cur)));
            Put(pb, Rw(cur + 4), "S13 +4");
            bw.Put(8, D[cur + 6]); bw.Put(8, D[cur + 7]);
            Array.Copy(D, cur, sh, cur, 8);
        }
    }

    // ---- pass 0x17 ------------------------------------------------------------
    void S14()
    {
        foreach (var (cur, prev, _) in Walk(14, Tv(T.RecS14)))
        {
            if (prev >= 0) CopySh(cur, prev, 4);
            foreach (var (o, n, width) in ((int, int, int)[])[(0, 2, pb), (2, 1, 8), (3, 1, 5)])
            {
                if (prev >= 0 && Same((cur + o, n))) bw.Put(1, 0);
                else
                {
                    int v = n == 2 ? Rw(cur) : D[cur + o];
                    bw.Put(1, 1); Put(width, v, $"S14 +{o}");
                    Array.Copy(D, cur + o, sh, cur + o, n);
                }
            }
        }
    }

    void A17S2()
    {
        int[] step = [0, 0], acc = [0, 0];
        foreach (var (cur, _, first) in Walk(2, Tv(T.RecS0)))
        {
            int k = 0;
            foreach (int off in (int[])[6, 8])
            {
                int need = (Rw(cur + off) - acc[k]) & 0xFFFF;
                if (need == step[k] && !first) bw.Put(1, 0);
                else
                {
                    if ((need & 1) != 0) throw new Cf1Exception($"S2 +{off} at 0x{cur:x}: odd step 0x{need:x}");
                    bw.Put(1, 1); Put(pb - 1, need >> 1, $"S2 +{off} step");
                    step[k] = need;
                }
                acc[k] = (step[k] + acc[k]) & 0xFFFF;
                W(cur + off, acc[k]);
                k++;
            }
        }
    }

    void A17(int idx, int[] offs)
    {
        foreach (var (cur, _, _) in Walk(idx, Tv(T.RecS0)))
            foreach (int o in offs) Even(cur + o, $"S{idx} +{o}");
    }

    // ---- helpers shared with the decoder side ---------------------------------
    static readonly (int idx, int key)[] SectRec =
    [
        (0, T.RecS0), (1, T.RecS0), (2, T.RecS0), (3, T.RecS3), (4, T.RecS4), (5, T.RecS5), (6, T.RecS6),
        (7, T.RecS7), (9, T.RecS9), (10, T.RecS10), (11, T.RecS11), (12, T.RecS12), (13, T.RecS13),
    ];

    /// <summary>First byte past every record section (sections 3 and 4 with their extra record).</summary>
    public static int LayoutEnd(byte[] d, IReadOnlyDictionary<int, int> table, int dbrel)
    {
        int b = table[T.DescBase];
        var recs = SectRec.ToList();
        if (dbrel >= 0x17) recs.Add((14, T.RecS14));
        int hi = 0;
        foreach (var (idx, key) in recs)
        {
            int off = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(b + 4 * idx));
            int count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(b + 4 * idx + 2));
            if ((idx == 12 || idx == 13) && count == 0) continue;
            if (idx == 13 && dbrel < 0x15) continue;
            int n = count + (idx == 3 || idx == 4 ? 1 : 0);
            if (n != 0 && table.TryGetValue(key, out var rec)) hi = Math.Max(hi, off + n * rec);
        }
        return hi;
    }

    /// <summary>Last byte of the name blob starting at `start`: the NUL closing its last string.</summary>
    public static int TextEnd(byte[] dst, int start)
    {
        int last = dst.Length - 1;
        while (last >= 0 && dst[last] == 0) last--;
        if (last < start) return start - 1;
        return Math.Min(last + 1, dst.Length - 1);
    }

    static int? CharBits(int c)
    {
        int i = T.CharIndex((byte)c);
        if (i >= 0 && i < 2) return 3;
        if (i >= 2 && i < 6) return 4;
        if (i >= 6 && i < 14) return 5;
        if ((i >= 14 && i < 14 + 0x1C) || (c > 0x26 && c < 0x80)) return 9;
        return null;
    }

    /// <summary>Greedy choice of up to n dictionary words, as OpenCarin's choose_words: the same windows,
    /// counted without overlap in the same order, the same gain and the same ties (the first best in
    /// length order, then in order of first occurrence). Windows are grouped by a rolling hash and
    /// compared byte for byte, so no strings are built.</summary>
    public static List<byte[]> ChooseWords(byte[] blob, int n = 6, int maxLen = 31)
    {
        int len0 = blob.Length;
        var work = new int[len0];
        for (int i = 0; i < len0; i++) work[i] = blob[i];
        var cost = new int[len0];
        var words = new List<byte[]>();
        // valid[p] = run of 7-bit unmasked bytes starting at p
        var run = new int[len0 + 1];
        var buckets = new Dictionary<ulong, int>();
        var firstPos = new List<int>();
        var cnt = new List<int>();
        var last = new List<int>();
        var nextInBucket = new List<int>();
        for (int round = 0; round < n; round++)
        {
            for (int p = len0 - 1; p >= 0; p--) run[p] = work[p] >= 0 && work[p] < 0x80 ? run[p + 1] + 1 : 0;
            for (int i = 0; i < len0; i++) cost[i] = work[i] >= 0 ? CharBits(work[i]) ?? 9 : 0;
            byte[]? best = null;
            int bestGain = 0;
            for (int L = 2; L <= maxLen && L <= len0; L++)
            {
                buckets.Clear(); firstPos.Clear(); cnt.Clear(); last.Clear(); nextInBucket.Clear();
                const ulong B = 1099511628211UL;
                ulong pw = 1;
                for (int j = 0; j < L; j++) pw *= B;
                ulong h = 0;
                for (int p = 0; p + L <= len0; p++)
                {
                    if (p == 0) { h = 0; for (int j = 0; j < L; j++) h = h * B + (ulong)(work[j] + 2); }
                    else h = h * B + (ulong)(work[p + L - 1] + 2) - pw * (ulong)(work[p - 1] + 2);
                    if (run[p] < L) continue;
                    int e = -1;
                    if (buckets.TryGetValue(h, out int head))
                        for (int c = head; c >= 0; c = nextInBucket[c])
                            if (Same(work, firstPos[c], p, L)) { e = c; break; }
                    if (e < 0)
                    {
                        e = firstPos.Count;
                        firstPos.Add(p); cnt.Add(0); last.Add(-L);
                        nextInBucket.Add(buckets.TryGetValue(h, out int hd) ? hd : -1);
                        buckets[h] = e;
                    }
                    if (p >= last[e] + L) { cnt[e]++; last[e] = p; }
                }
                for (int e = 0; e < firstPos.Count; e++)             // insertion order = first occurrence
                {
                    if (cnt[e] < 2) continue;
                    int bits = 0, f = firstPos[e];
                    for (int j = 0; j < L; j++) bits += cost[f + j];
                    int gain = cnt[e] * (bits - 9) - (5 + 7 * L);
                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        best = new byte[L];
                        for (int j = 0; j < L; j++) best[j] = (byte)work[f + j];
                    }
                }
            }
            if (best == null) break;
            words.Add(best);
            int q = 0;
            while (q <= work.Length - best.Length)
            {
                bool eq = true;
                for (int j = 0; j < best.Length; j++) if (work[q + j] != best[j]) { eq = false; break; }
                if (eq) { for (int j = 0; j < best.Length; j++) work[q + j] = -1; q += best.Length; }
                else q++;
            }
        }
        return words;
    }

    static bool Same(int[] w, int a, int b, int L)
    {
        for (int j = 0; j < L; j++) if (w[a + j] != w[b + j]) return false;
        return true;
    }

    public static void EncText(BitWriter bw, byte[] dst, int start, int end, int ptrbits, List<byte[]>? words)
    {
        if (end < start) { bw.Put(ptrbits, 0); bw.Put(ptrbits, 0); return; }
        var blob = dst.AsSpan(start, end - start + 1).ToArray();
        words ??= ChooseWords(blob);
        if (words.Count > 6 || words.Any(w => w.Length > 31 || w.Any(x => x >= 0x80)))
            throw new Cf1Exception("dictionary: at most six 7-bit words of up to 31 bytes");
        bw.Put(ptrbits, start);
        bw.Put(ptrbits, end);
        for (int k = 0; k < 6; k++)
        {
            var w = k < words.Count ? words[k] : [];
            bw.Put(5, w.Length);
            foreach (byte x in w) bw.Put(7, x);
        }
        var order = words.Where(w => w.Length > 0).OrderByDescending(w => w.Length).ToList();   // stable
        int p = start;
        while (p <= end)
        {
            byte[]? hit = null;
            foreach (var w in order)
            {
                if (p + w.Length <= end + 1 && dst.AsSpan(p, Math.Min(w.Length, dst.Length - p)).SequenceEqual(w)) { hit = w; break; }
            }
            if (hit != null)
            {
                bw.Put(2, 3); bw.Put(7, 0x21 + IndexOfRef(words, hit));
                p += hit.Length;
                continue;
            }
            int c = dst[p];
            int i = T.CharIndex((byte)c);
            if (i >= 0 && i < 2) { bw.Put(2, 0); bw.Put(1, i); }
            else if (i >= 2 && i < 6) { bw.Put(2, 1); bw.Put(2, i - 2); }
            else if (i >= 6 && i < 14) { bw.Put(2, 2); bw.Put(3, i - 6); }
            else if (i >= 14 && i < 14 + 0x1C) { bw.Put(2, 3); bw.Put(7, i - 14); }
            else if (c > 0x26 && c < 0x80) { bw.Put(2, 3); bw.Put(7, c); }
            else throw new Cf1Exception($"name blob byte 0x{c:x2} at 0x{p:x} has no CF=1 text code");
            p++;
        }
    }

    // Python's words.index(w): the first word equal to w (by value)
    static int IndexOfRef(List<byte[]> words, byte[] w)
    {
        for (int k = 0; k < words.Count; k++) if (words[k].AsSpan().SequenceEqual(w)) return k;
        return -1;
    }

    /// <summary>Re-encode a decoded type 0x00 block to CF=1 raw bytes (header included).</summary>
    public static byte[] Encode(byte[] decoded, IReadOnlyDictionary<int, int> table, int dbrel, int subrel, int sector) =>
        EncodeParts(decoded, table, dbrel, subrel, sector).Joined;

    /// <summary>An encoding split into its byte-copied head (prolog, widths, raw sections 3/9/10/12) and
    /// its bit stream, with where the S6 neighbour BLOCK_IDs sit in the stream. Block IDs only occur in
    /// fixed-width places (the head and those 32-bit fields), so a tile whose IDs change can be re-encoded
    /// by rebuilding the head (Head) and patching the stream (PatchIds) instead of encoding again.</summary>
    public sealed record Parts(byte[] Head, byte[] Bits, long[] S6IdBits)
    {
        public byte[] Joined => [.. Head, .. Bits];
    }

    /// <summary>The head of an encoding of `decoded` (cheap: byte copies only).</summary>
    public static byte[] Head(byte[] decoded, IReadOnlyDictionary<int, int> table, int sector)
    {
        int prologLen = table[T.Prolog];
        var head = new List<byte>(prologLen + 512);
        var prolog = decoded.AsSpan(0, prologLen).ToArray();
        prolog[6] = 1;
        prolog[7] = (byte)(decoded.Length / sector);
        head.AddRange(prolog);
        head.Add(decoded[6]); head.Add(decoded[7]);
        int b = table[T.DescBase];
        foreach (var (idx, key, plus1) in ((int, int, bool)[])[(3, T.RecS3, true), (9, T.RecS9, false), (10, T.RecS10, false), (12, T.RecS12, false)])
        {
            int off = BinaryPrimitives.ReadUInt16BigEndian(decoded.AsSpan(b + 4 * idx));
            int count = BinaryPrimitives.ReadUInt16BigEndian(decoded.AsSpan(b + 4 * idx + 2));
            if (idx == 12 && count == 0) continue;
            int n = count * table[key] + (plus1 ? table[key] : 0);
            head.AddRange(decoded.AsSpan(off, n).ToArray());
        }
        return head.ToArray();
    }

    /// <summary>Write the S6 neighbour BLOCK_IDs of `decoded` into a copy of the bit stream.</summary>
    public static byte[] PatchIds(Parts p, byte[] decoded, IReadOnlyDictionary<int, int> table)
    {
        var bits = (byte[])p.Bits.Clone();
        int b = table[T.DescBase];
        int off6 = BinaryPrimitives.ReadUInt16BigEndian(decoded.AsSpan(b + 24));
        int rec6 = table[T.RecS6], o5 = table[T.RecS5];
        for (int k = 0; k < p.S6IdBits.Length; k++)
        {
            uint id = BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(off6 + k * rec6 + o5));
            long pos = p.S6IdBits[k];
            for (int i = 0; i < 32; i++)
            {
                long bit = pos + i;
                int byteIdx = (int)(bit >> 3), sh = 7 - (int)(bit & 7);
                if (((id >> (31 - i)) & 1) != 0) bits[byteIdx] |= (byte)(1 << sh);
                else bits[byteIdx] &= (byte)~(1 << sh);
            }
        }
        return bits;
    }

    public static Parts EncodeParts(byte[] decoded, IReadOnlyDictionary<int, int> table, int dbrel, int subrel, int sector)
    {
        if (decoded.Length % sector != 0) throw new Cf1Exception("decoded length is not a whole number of sectors");
        var x = new Encoder00(decoded, table, dbrel, subrel);
        int prologLen = table[T.Prolog];
        var head = new List<byte>(prologLen + 256);
        var prolog = decoded.AsSpan(0, prologLen).ToArray();
        prolog[6] = 1;
        prolog[7] = (byte)(decoded.Length / sector);
        head.AddRange(prolog);
        head.AddRange(x.widths);
        foreach (var (idx, key, plus1) in ((int, int, bool)[])[(3, T.RecS3, true), (9, T.RecS9, false), (10, T.RecS10, false), (12, T.RecS12, false)])
        {
            var e = x.Ent(idx);
            if (idx == 12 && e.Count == 0) continue;
            int n = e.Count * table[key] + (plus1 ? table[key] : 0);
            head.AddRange(decoded.AsSpan(e.Off, n).ToArray());
            Array.Copy(decoded, e.Off, x.sh, e.Off, n);
        }
        Array.Copy(decoded, 0, x.sh, 0, prologLen);

        for (int idx = 0; idx < 3; idx++) x.A14(idx);
        x.B14();
        x.Xy(5, table[T.RecS5], false);
        x.Xy(6, table[T.RecS6], true);
        x.E7();
        if (x.Ent(11).Count != 0) x.F11();
        x.TextPass();
        if (dbrel >= 0x15)
        {
            x.A15S0();
            x.B15();
            x.S13();
        }
        if (dbrel >= 0x17)
        {
            x.S14();
            x.A17S2();
            x.A17(1, [6, 8]);
            x.A17(0, [4]);
            x.bw.Put(1, 0);
            x.bw.Put(1, 0);
        }
        if (dbrel >= 0x1B) throw new Cf1Exception("DB-REL >= 27 (pass 0x1B) is not ported");
        return new Parts(head.ToArray(), x.bw.ToArray(), x.s6IdBits.ToArray());
    }
}
