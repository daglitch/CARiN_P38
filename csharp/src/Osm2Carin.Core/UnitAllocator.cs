namespace Osm2Carin.Core;

/// <summary>Hands out carindb units for new blocks from a list of free ranges, in order. A request
/// that does not fit what is left of the current range moves on to the next range; the skipped tail
/// is remembered as a gap, to be filled with a filler block so the block chain stays contiguous.
/// With one open range from a base unit it allocates exactly like a plain counter (Phase 2/3a).</summary>
public sealed class UnitAllocator
{
    readonly List<(int start, int end)> ranges;     // [start, end); the last may end at int.MaxValue
    int ri, pos;
    readonly List<(int start, int units)> gaps = new();

    public UnitAllocator(IEnumerable<(int start, int end)> free)
    {
        ranges = free.Where(r => r.end > r.start).OrderBy(r => r.start).ToList();
        if (ranges.Count == 0) throw new ArgumentException("no free units");
        pos = ranges[0].start;
    }

    public static UnitAllocator From(int baseUnit) => new([(baseUnit, int.MaxValue)]);

    public int Pos => pos;
    public IReadOnlyList<(int start, int units)> Gaps => gaps;
    public IReadOnlyList<(int start, int end)> Ranges => ranges;

    /// <summary>The start unit of `units` consecutive units.</summary>
    public int Alloc(int units)
    {
        if (units <= 0) throw new ArgumentOutOfRangeException(nameof(units));
        while ((long)pos + units > ranges[ri].end)
        {
            if (ranges[ri].end > pos) gaps.Add((pos, ranges[ri].end - pos));
            if (++ri >= ranges.Count) throw new InvalidOperationException("out of free units");
            pos = ranges[ri].start;
        }
        int s = pos;
        pos += units;
        return s;
    }

    /// <summary>The block ID of a new block of `units` units.</summary>
    public uint AllocId(int units)
    {
        if (units > 255) throw new InvalidOperationException($"block of {units} units (max 255)");
        return (uint)((Alloc(units) << 8) | units);
    }

    public (int ri, int pos, int gaps) Save() => (ri, pos, gaps.Count);

    public void Restore((int ri, int pos, int gaps) m)
    {
        (ri, pos) = (m.ri, m.pos);
        gaps.RemoveRange(m.gaps, gaps.Count - m.gaps);
    }

    /// <summary>Close the current range: its unused tail becomes a gap (call once all blocks are placed).</summary>
    public void Finish()
    {
        if (ranges[ri].end != int.MaxValue && ranges[ri].end > pos) gaps.Add((pos, ranges[ri].end - pos));
        for (int k = ri + 1; k < ranges.Count; k++)
            if (ranges[k].end != int.MaxValue) gaps.Add((ranges[k].start, ranges[k].end - ranges[k].start));
    }
}
