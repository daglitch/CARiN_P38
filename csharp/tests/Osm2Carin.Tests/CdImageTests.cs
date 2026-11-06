using Osm2Carin.Core;

namespace Osm2Carin.Tests;

public class CdImageTests
{
    [Fact]
    public void RebuildsOriginalByteIdentical()
    {
        var bin = Paths.Bin;
        if (bin == null) return;              // original disc image not present
        var dir = Paths.Scratch("cdimage");
        var lay = CdImage.Split(bin!, dir);
        Assert.Equal(186541, lay.Form1);
        Assert.Equal(150, lay.PostgapForm2);
        Assert.Equal(300, lay.AudioSectors);
        var outBin = Path.Combine(dir, "rebuilt.bin");
        int t1 = CdImage.Build(Path.Combine(dir, "track1.iso"), outBin, Path.Combine(dir, "audio.raw"));
        Assert.Equal(186691, t1);
        Assert.Equal(CdImage.Sha1(bin!), CdImage.Sha1(outBin));
        var cue = File.ReadAllText(Path.ChangeExtension(outBin, ".cue"));
        Assert.Contains("INDEX 01 41:29:16", cue);
        var v = CdImage.Verify(outBin);
        Assert.Equal((186541, 0, 150, 0), (v.Form1Ok, v.Form1Bad, v.Form2Ok, v.Form2Bad));
    }

    [Fact]
    public void DetectsCorruption()
    {
        var user = new byte[2048];
        new Random(1).NextBytes(user);
        var a = new byte[2352];
        CdImage.Form1Sector(user, 1000, a);
        user[500] ^= 1;
        var b = new byte[2352];
        CdImage.Form1Sector(user, 1000, b);
        Assert.NotEqual(a, b);
    }
}
