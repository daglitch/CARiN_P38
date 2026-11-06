using Osm2Carin.Core;
using Osm2Carin.Core.Dest;
using Osm2Carin.Core.Osm;
using Osm2Carin.Core.Roads;
using Xunit.Abstractions;

namespace Osm2Carin.Tests;

/// <summary>Phases 3b/3c: Nevada's roads, cities, streets and POIs packed into the original disc. Needs
/// out/phase0/orig/track1.iso and out/us_nevada/model.npz + model.places.json; skipped otherwise.</summary>
public class PackTests(ITestOutputHelper log)
{
    [Fact]
    public void NevadaWithCitiesStreetsAndPois()
    {
        string repo = Paths.Repo ?? "";
        string isoPath = Path.Combine(repo, "out", "phase0", "orig", "track1.iso");
        string model = Path.Combine(repo, "out", "us_nevada", "model");
        if (!File.Exists(isoPath) || !File.Exists(model + ".places.json")) return;
        Progress.Enabled = false;

        var iso = Graft.Open(isoPath);
        var original = iso.Bytes.AsSpan(366 * 2048, iso.CarindbSize).ToArray();
        var free = Pack.FreeRanges(original);
        Assert.Equal((5, 168866), free[0]);
        Assert.Equal(186175, free[1].start);

        var m = RoadModel.Load(model);
        var cities = Cities.Build(m, s => log.WriteLine(s));
        Assert.Contains(cities.List, c => c.Name == "las vegas, nv");
        var rt = Tiles.Build(m, m.Strings, "Nevada", s => log.WriteLine(s), cities.LinkCityNames());
        var alloc = new UnitAllocator(free);
        var specs = Link.Run(rt, alloc, true, s => log.WriteLine(s), withIndex: true);
        Assert.Equal(5, (int)(rt.Blocks.Min(b => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b)) >> 8));

        var dir = new Core.Dest.Directory();
        dir.PlanStreets(m, cities, rt.Levels[0], s => log.WriteLine(s));
        dir.PlanPois(m, cities, rt.Levels[0], s => log.WriteLine(s));
        dir.PlanCities(s => log.WriteLine(s));
        var packed = Pack.Build(original, rt, dir, alloc, s => log.WriteLine(s));
        var (res, problems) = Pack.Check(packed.Db, rt, dir);
        foreach (var kv in res) log.WriteLine($"{kv.Key}: {kv.Value}");
        Assert.Empty(problems);
        Assert.True((bool)res["chain covers the file"]!);
        Assert.Equal(0L, (long)res["tiles missed from 0x07"]!);
        Assert.Equal((long)dir.CityRecs.Count, (long)res["cities reached from 0x0A"]!);
        Assert.Equal((long)dir.StreetBlocks.Sum(b => b.Streets.Sum(StreetBlk.Pairs)), (long)res["streets reached"]!);
        Assert.Equal((long)res["0x06 records"]!, (long)res["0x06 records matching their POI"]!);
        Assert.Equal((long)res["POIs reached from the cities"]!, (long)res["0x06 records"]!);

        // the kept blocks are untouched apart from the patched 0x07 / 0x0A
        for (int u = 0; u < 5; u++)
            if (u != Graft.DirectoryUnit && u != 4)
                Assert.True(packed.Db.AsSpan(u * 2048, 2048).SequenceEqual(original.AsSpan(u * 2048, 2048)), $"unit {u} changed");
        Assert.True(packed.Db.AsSpan(free[0].end * 2048, (free[1].start - free[0].end) * 2048)
            .SequenceEqual(original.AsSpan(free[0].end * 2048, (free[1].start - free[0].end) * 2048)), "background layers changed");
    }
}
