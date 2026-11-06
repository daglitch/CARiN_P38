"""Phase 3a: read a grafted disc back with OpenCarin's own reader (independent of the C# writer).

    py -I tools/phase3a_check.py <grafted .iso> <OpenCarin dir> [--base 186175]

Checks:
  * OpenCarin's CarinVolume opens the ISO, finds /carindb, detects 2048-byte units, DB-REL 26 and
    CF=1 sub-revision 8, and walks the whole block chain;
  * every road tile from the base unit on (ours) decodes with OpenCarin's decoder (CF=1 included);
  * OpenCarin's spatial lookup (carin.parser.spatial, the unit's 0x07 -> 0x08 -> 0x09 -> tile path),
    run from the 0x07 records 0, 2, 3, 4, finds each of our tiles at its centre, and every hit is ours;
  * a few named places resolve to our tiles.
spatial.py is written for DB-REL 34 (28-byte 0x07 records at +0x14, 0x07 at sector 3); this disc has
24-byte records at +0x10 and 0x07 at unit 2 (carin-disc-findings.md, Phase 3), so those constants
are set here.
"""
import argparse
import json
import struct
import sys
from collections import Counter

ROAD_RECORDS = {0: 0x00, 2: 0x01, 3: 0x02, 4: 0x03}
PLACES = {'San Jose': (-121.886, 37.338), 'Los Angeles': (-118.243, 34.052), 'San Francisco': (-122.419, 37.775),
          'Las Vegas': (-115.140, 36.170), 'Reno': (-119.813, 39.529)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('iso')
    ap.add_argument('opencarin')
    ap.add_argument('--base', type=int, default=186175, help='first unit of our layers (original carindb length)')
    ap.add_argument('--out', help='write the results as JSON here')
    a = ap.parse_args()
    sys.path.insert(0, a.opencarin)
    from carin.parser import spatial
    from carin.parser.iso import CarinVolume, IsoImage, to_wgs84

    vol = CarinVolume(IsoImage(a.iso))
    res = {'sector_size': vol.sector_size, 'db_rel': vol.db_rel}
    res['subrel'] = vol.calibrate()
    types, ours, n = Counter(), [], 0
    for b in vol.walk():
        n += 1
        types[b.type] += 1
        if b.sector >= a.base and b.type in (0, 1, 2, 3):
            ours.append((b.sector, b.length, b.type))
    res['blocks'] = n
    res['units'] = sum(vol.sectors)
    res['our_tiles'] = dict(Counter(f'0x{t:02X}' for _, _, t in ours))

    spatial.DIR_OFFSET, spatial.DIR_RECORD_SIZE, spatial.DIR_SECTOR = 0x10, 24, 2
    idx = object.__new__(spatial.SpatialIndex)
    idx.vol, idx._grids, idx._cells = vol, {}, {}
    idx.layers = spatial.parse_layer_directory(vol.block(2).payload)
    rec_of = {t: r for r, t in ROAD_RECORDS.items()}
    res['directory'] = {r: f'0x{idx.layers[r].grid:08x}' for r in ROAD_RECORDS}

    decoded = found = missed = 0
    problems = []
    for sector, length, t in ours:
        blk = vol.block(sector)
        d = blk.data
        if d is None:
            problems.append(f'no decode {sector}')
            continue
        decoded += 1
        x1, y1, x2, y2 = struct.unpack_from('>4i', d, 0x44)
        hit = idx.tile_at_xy(rec_of[t], (x1 + x2) // 2, (y1 + y2) // 2)
        if hit == (sector << 8 | length):
            found += 1
        else:
            missed += 1
            if len(problems) < 20:
                problems.append(f'lookup {sector}: got {hit}')
    res['decoded by OpenCarin'] = decoded
    res['found by OpenCarin lookup'] = found
    res['missed'] = missed
    places = {}
    for name, (lon, lat) in PLACES.items():
        x, y = spatial.to_carin(lon, lat)
        bid = idx.tile_at_xy(0, x % 2_000_000_000, y)          # this disc wraps X to [0, 2e9) (findings: Coordinates)
        places[name] = None if bid is None else {'block': f'0x{bid:08x}', 'ours': (bid >> 8) >= a.base}
    res['places (street layer)'] = places
    res['problems'] = problems
    print(json.dumps(res, indent=1))
    if a.out:
        with open(a.out, 'w') as f:
            json.dump(res, f, indent=1)
    return 1 if problems or missed else 0


if __name__ == '__main__':
    sys.exit(main())
