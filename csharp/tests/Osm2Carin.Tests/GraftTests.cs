using System.Buffers.Binary;
using Osm2Carin.Core;
using Osm2Carin.Core.Osm;
using Osm2Carin.Core.Roads;
using Xunit.Abstractions;

namespace Osm2Carin.Tests;

/// <summary>Phase 3a: Nevada's road layers grafted onto the original disc. Needs out/phase0/orig/track1.iso
/// (osm2carin cd-split) and out/us_nevada/model.npz (osm2carin model); skipped otherwise.</summary>
public class GraftTests(ITestOutputHelper log)
{
    [Fact]
    public void NevadaOnTheOriginalDisc()
    {
        string repo = Paths.Repo ?? "";
        string isoPath = Path.Combine(repo, "out", "phase0", "orig", "track1.iso");
        string model = Path.Combine(repo, "out", "us_nevada", "model");
        if (!File.Exists(isoPath) || !File.Exists(model + ".npz")) return;

        var iso = Graft.Open(isoPath);
        Assert.Equal(366, iso.CarindbLba);
        Assert.Equal(186175, Graft.BaseUnit(iso));
        var m = RoadModel.Load(model);
        var rt = Tiles.Build(m, m.Strings, "Nevada");
        var specs = Link.Run(rt, Graft.BaseUnit(iso), true, withIndex: true);
        var (newIso, db, rep) = Graft.Build(iso, rt);
        var original = iso.Bytes.AsSpan(366 * 2048, iso.CarindbSize).ToArray();
        var (res, problems) = Graft.Check(db, original, rt, specs);
        foreach (var kv in res) log.WriteLine($"{kv.Key}: {kv.Value}");
        Assert.Empty(problems);
        Assert.True((bool)res["chain covers the file"]!);
        Assert.Equal((long)specs.Count, (long)res["tiles found from 0x07"]!);

        // ISO: same header area apart from the sizes; carindb is the new one; the root reader sees its new size
        Assert.Equal(366L * 2048 + db.Length, newIso.LongLength);
        Assert.Equal(newIso.Length / 2048, BinaryPrimitives.ReadInt32LittleEndian(newIso.AsSpan(16 * 2048 + 80)));
        Assert.Equal(newIso.Length / 2048, BinaryPrimitives.ReadInt32BigEndian(newIso.AsSpan(16 * 2048 + 84)));
        string tmp = Path.Combine(Paths.Scratch("graft"), "nevada-3a.iso");
        File.WriteAllBytes(tmp, newIso);
        Assert.Equal((366, db.Length), Carindb.IsoFiles(tmp)["carindb"]);
        int differ = 0;
        for (int i = 0; i < 366 * 2048; i++) if (newIso[i] != iso.Bytes[i]) differ++;
        log.WriteLine($"ISO header area: {differ} bytes differ");
        Assert.InRange(differ, 1, 24);           // PVD volume size (8 bytes) + directory record size (8 bytes)
    }
}
