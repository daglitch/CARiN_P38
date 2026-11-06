namespace Osm2Carin.Core;

/// <summary>Format constants of the CD-ID 5944 database (DB-REL 26, CF=1 sub-revision 8, 2048-byte
/// units). RecordSizeTable is the superblock's RECORD_SIZE_TABLE (id -> bytes), as tools/osm2carin/fmt.py.</summary>
public static class Fmt
{
    public const int DbRel = 26;
    public const int SubRel = 8;
    public const int Unit = 2048;

    public static readonly IReadOnlyDictionary<int, int> RecordSizeTable = new Dictionary<int, int>
    {
        [1] = 12, [2] = 16, [3] = 8, [4] = 28, [5] = 8, [6] = 16, [7] = 8, [8] = 30, [9] = 24, [10] = 6, [11] = 116, [12] = 6, [13] = 4, [14] = 48, [15] = 8, [16] = 8, [17] = 60, [18] = 4, [19] = 6, [20] = 8, [21] = 6, [22] = 6, [23] = 312, [24] = 24, [25] = 296, [26] = 8, [27] = 52, [28] = 40, [29] = 4, [30] = 8, [31] = 20, [32] = 44, [33] = 16, [34] = 4, [35] = 4, [36] = 4, [37] = 20, [38] = 4, [39] = 8, [40] = 12, [41] = 12, [42] = 4, [43] = 40, [44] = 8, [45] = 8, [46] = 24, [47] = 40, [48] = 12, [49] = 8, [50] = 20, [51] = 32, [52] = 16, [53] = 8, [54] = 16, [55] = 10, [56] = 4, [57] = 4, [58] = 20, [59] = 4, [60] = 16, [61] = 52, [62] = 20, [63] = 24, [64] = 10, [65] = 4, [66] = 24, [67] = 28, [68] = 4, [69] = 16, [70] = 56, [71] = 16, [72] = 4, [73] = 4, [74] = 8, [75] = 20, [76] = 8, [77] = 12, [78] = 28, [79] = 4, [80] = 16, [81] = 40, [82] = 16, [83] = 4, [84] = 4, [85] = 8, [86] = 24, [87] = 12, [88] = 4, [89] = 4, [90] = 2
    };
}
