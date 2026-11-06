using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Osm2Carin.Core;

/// <summary>
/// Raw CD image (MODE2/2352) builder and checker, port of tools/cdimage.py.
/// Track 1: N Mode 2 Form 1 data sectors (subheader 00 00 08 00) + 150 Form 2 postgap
/// sectors (00 00 20 00, zero data, EDC present); track 2: audio after a 2 s pregap.
/// EDC and P/Q ECC follow ECMA-130 (ECC computed with the 4 header bytes zeroed).
/// </summary>
public static class CdImage
{
    public const int Raw = 2352;
    public const int User = 2048;
    static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
    public static readonly byte[] SubForm1 = [0, 0, 0x08, 0];
    public static readonly byte[] SubForm2 = [0, 0, 0x20, 0];

    static readonly byte[] F = new byte[256];
    static readonly byte[] B = new byte[256];
    static readonly uint[] EdcLut = new uint[256];

    static CdImage()
    {
        for (int i = 0; i < 256; i++)
        {
            int j = ((i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0)) & 0xFF;
            F[i] = (byte)j;
            B[i ^ j] = (byte)i;
            uint e = (uint)i;
            for (int k = 0; k < 8; k++) e = (e >> 1) ^ ((e & 1) != 0 ? 0xD8018001u : 0u);
            EdcLut[i] = e;
        }
    }

    public static uint Edc(ReadOnlySpan<byte> data)
    {
        uint e = 0;
        foreach (byte b in data) e = (e >> 8) ^ EdcLut[(e ^ b) & 0xFF];
        return e;
    }

    static void EccBlock(ReadOnlySpan<byte> src, int majorCount, int minorCount, int majorMult, int minorInc, Span<byte> dest)
    {
        int size = majorCount * minorCount;
        for (int major = 0; major < majorCount; major++)
        {
            int index = (major >> 1) * majorMult + (major & 1);
            byte a = 0, b = 0;
            for (int minor = 0; minor < minorCount; minor++)
            {
                byte t = src[index];
                index += minorInc;
                if (index >= size) index -= size;
                a ^= t;
                b ^= t;
                a = F[a];
            }
            a = B[F[a] ^ b];
            dest[major] = a;
            dest[major + majorCount] = (byte)(a ^ b);
        }
    }

    static byte Bcd(int v) => (byte)(((v / 10) << 4) | (v % 10));

    static void Header(Span<byte> sec, int lba)
    {
        int l = lba + 150;
        sec[12] = Bcd(l / 4500);
        sec[13] = Bcd(l / 75 % 60);
        sec[14] = Bcd(l % 75);
        sec[15] = 2;
    }

    /// <summary>One Mode 2 Form 1 sector from 2048 bytes of user data.</summary>
    public static void Form1Sector(ReadOnlySpan<byte> user, int lba, Span<byte> sec, ReadOnlySpan<byte> sub = default)
    {
        if (sub.IsEmpty) sub = SubForm1;
        sec.Clear();
        Sync.CopyTo(sec);
        sub.CopyTo(sec[16..]);
        sub.CopyTo(sec[20..]);
        user[..User].CopyTo(sec[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(sec[2072..], Edc(sec[16..2072]));
        EccBlock(sec[12..], 86, 24, 2, 86, sec[2076..]);      // header bytes still zero
        EccBlock(sec[12..], 52, 43, 86, 88, sec[2248..]);
        Header(sec, lba);
    }

    /// <summary>A zero-filled Mode 2 Form 2 sector with EDC (the postgap).</summary>
    public static void Form2Sector(int lba, Span<byte> sec, ReadOnlySpan<byte> sub = default)
    {
        if (sub.IsEmpty) sub = SubForm2;
        sec.Clear();
        Sync.CopyTo(sec);
        sub.CopyTo(sec[16..]);
        sub.CopyTo(sec[20..]);
        BinaryPrimitives.WriteUInt32LittleEndian(sec[2348..], Edc(sec[16..2348]));
        Header(sec, lba);
    }

    public static string Msf(int lba) => $"{lba / 4500:00}:{lba / 75 % 60:00}:{lba % 75:00}";

    public static void WriteCue(string cuePath, string binName, int track1Sectors, int audioPregap = 150, bool hasAudio = true)
    {
        var lines = new List<string> { $"FILE \"{binName}\" BINARY", "  TRACK 01 MODE2/2352", "    INDEX 01 00:00:00" };
        if (hasAudio)
        {
            lines.Add("  TRACK 02 AUDIO");
            lines.Add($"    PREGAP {Msf(audioPregap)}");
            lines.Add($"    INDEX 01 {Msf(track1Sectors)}");
        }
        File.WriteAllText(cuePath, string.Join("\r\n", lines) + "\r\n");
    }

    /// <summary>ISO (Form 1 user data) [+ raw audio] -> .bin + .cue. Returns the track-1 sector count.</summary>
    public static int Build(string isoPath, string binPath, string? audioPath = null, int postgap = 150, int audioPregap = 150)
    {
        long isoLen = new FileInfo(isoPath).Length;
        if (isoLen % User != 0) throw new InvalidDataException("ISO size is not a multiple of 2048");
        int n = (int)(isoLen / User);
        const int chunk = 8192;
        using var input = File.OpenRead(isoPath);
        using var output = File.Create(binPath);
        var user = new byte[chunk * User];
        var raw = new byte[chunk * Raw];
        for (int lba = 0; lba < n; lba += chunk)
        {
            int m = Math.Min(chunk, n - lba);
            input.ReadExactly(user, 0, m * User);
            int baseLba = lba;
            Parallel.For(0, m, k => Form1Sector(user.AsSpan(k * User, User), baseLba + k, raw.AsSpan(k * Raw, Raw)));
            output.Write(raw, 0, m * Raw);
        }
        var sec = new byte[Raw];
        for (int k = 0; k < postgap; k++)
        {
            Form2Sector(n + k, sec);
            output.Write(sec);
        }
        if (audioPath != null)
            using (var a = File.OpenRead(audioPath)) a.CopyTo(output);
        int track1 = n + postgap;
        WriteCue(Path.ChangeExtension(binPath, ".cue"), Path.GetFileName(binPath), track1, audioPregap, audioPath != null);
        return track1;
    }

    public record Layout(int Form1, int PostgapForm2, int AudioSectors);

    /// <summary>Original .bin -> track1.iso (Form 1 user data), audio.raw, layout.json.</summary>
    public static Layout Split(string binPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int f1 = 0, f2 = 0, audio = 0;
        using var input = File.OpenRead(binPath);
        using var iso = File.Create(Path.Combine(outDir, "track1.iso"));
        using var aud = File.Create(Path.Combine(outDir, "audio.raw"));
        var s = new byte[Raw];
        while (input.ReadAtLeast(s, Raw, throwOnEndOfStream: false) == Raw)
        {
            if (audio > 0 || !s.AsSpan(0, 12).SequenceEqual(Sync)) { aud.Write(s); audio++; }
            else if ((s[18] & 0x20) != 0) f2++;
            else
            {
                if (f2 > 0) throw new InvalidDataException("Form 1 sector after Form 2 postgap");
                iso.Write(s, 24, User);
                f1++;
            }
        }
        var lay = new Layout(f1, f2, audio);
        File.WriteAllText(Path.Combine(outDir, "layout.json"),
            JsonSerializer.Serialize(new { form1 = f1, postgap_form2 = f2, audio_sectors = audio }));
        return lay;
    }

    public record VerifyResult(int Form1Ok, int Form1Bad, int Form2Ok, int Form2Bad, int? FirstBad);

    /// <summary>Regenerate every data sector from its own user data and compare.</summary>
    public static VerifyResult Verify(string binPath)
    {
        int ok1 = 0, bad1 = 0, ok2 = 0, bad2 = 0;
        int? firstBad = null;
        using var input = File.OpenRead(binPath);
        var s = new byte[Raw];
        var regen = new byte[Raw];
        while (input.ReadAtLeast(s, Raw, throwOnEndOfStream: false) == Raw)
        {
            if (!s.AsSpan(0, 12).SequenceEqual(Sync)) continue;
            int lba = Lba(s);
            if ((s[18] & 0x20) == 0)
            {
                Form1Sector(s.AsSpan(24, User), lba, regen, s.AsSpan(16, 4));
                if (regen.AsSpan().SequenceEqual(s)) ok1++;
                else { bad1++; firstBad ??= lba; }
            }
            else
            {
                uint e = Edc(s.AsSpan(16, 2332));
                if (BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(2348)) == e) ok2++; else bad2++;
            }
        }
        return new VerifyResult(ok1, bad1, ok2, bad2, firstBad);
    }

    static int Lba(byte[] s)
    {
        static int D(byte b) => (b >> 4) * 10 + (b & 15);
        return (D(s[12]) * 60 + D(s[13])) * 75 + D(s[14]) - 150;
    }

    public static string Sha1(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(f));
    }
}
