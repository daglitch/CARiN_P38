"""Calibration survey of the original disc (CD-ID 5944).

Measures what the converter must imitate and what the size estimator needs:
  * carindb bytes and block counts per block type;
  * per road level: parcels, links, nodes, shape points, bytes on disc;
  * for links inside Nevada (fully covered at street level): counts per level,
    road class / form of way / direction / speed histograms, total length.

Field offsets (30-byte street link at DB-REL 26, 24-byte coarse link) follow
OpenCarin docs/carindb/03-road-network.md section 6.7:
  +0x0A bits 0-4 speed category (~km/h / 4), bit 7 built-up
  +0x0B bits 0-3 form of way, bits 4-5 direction, bit 6 toll
  +0x0C length (m), +0x10 bits 0-3 road class, bits 4-6 class-6 subtype
  +0x11 low nibble junction type (6/5 roundabout)

Usage: py -m osm2carin.survey <track1.iso> <OpenCarin dir> <out.json>
"""
import collections
import json
import os
import struct
import sys

from . import geo

# Nevada, rough polygon (lon, lat); good enough to pick parcels and links.
NEVADA = [(-120.0, 42.0), (-114.04, 42.0), (-114.04, 36.15), (-114.75, 35.08),
          (-114.63, 35.0), (-120.0, 39.0)]


def _hist():
    return collections.Counter()


def survey(iso, oc_dir, region=NEVADA):
    sys.path.insert(0, oc_dir)
    sys.modules.pop('carin', None)
    from carin.parser.iso import open_image, CarinVolume
    from carin.parser import cf1

    vol = CarinVolume(open_image(iso))
    vol.calibrate(32)
    rel, sub, unit = vol.db_rel, vol.subrel, vol.sector_size
    lon0 = min(p[0] for p in region); lon1 = max(p[0] for p in region)
    lat0 = min(p[1] for p in region); lat1 = max(p[1] for p in region)

    by_type = collections.defaultdict(lambda: [0, 0, 0])   # blocks, units, cf1 blocks
    lv = {t: dict(parcels=0, links=0, nodes=0, edge_nodes=0, shape_points=0, units=0)
          for t in range(4)}
    reg = {t: dict(parcels=0, links=0, length_m=0, cls=_hist(), form=_hist(), dirn=_hist(),
                   speed_by_class=collections.defaultdict(_hist), junction=_hist(),
                   subtype6=_hist(), toll=0, builtup=0) for t in range(4)}
    rec_size = {0: vol.layout[8], 1: 24, 2: 24, 3: 24}
    for b in vol.walk():
        bt = by_type[b.type]
        bt[0] += 1; bt[1] += b.length; bt[2] += b.comp & 1
        if b.type > 3:
            continue
        raw = vol.read_sectors(b.sector, b.length)
        x1, y1, x2, y2 = struct.unpack_from('>4i', raw, 0x44)
        (a, c), (bb, d) = geo.to_lonlat(x1, y1), geo.to_lonlat(x2, y2)
        inside_box = not (bb < lon0 or a > lon1 or d < lat0 or c > lat1)
        buf = cf1.decode_block(raw, vol.layout, rel, sub, unit) if b.comp & 1 else raw
        sec = [struct.unpack_from('>HH', buf, 8 + 4 * i) for i in range(15)]
        L = lv[b.type]
        L['parcels'] += 1; L['units'] += b.length
        L['links'] += sec[4][1]; L['nodes'] += sec[5][1]; L['edge_nodes'] += sec[6][1]
        L['shape_points'] += sec[7][1]
        if not inside_box:
            continue
        R = reg[b.type]
        off4, n4 = sec[4]
        off5 = sec[5][0]
        used = False
        for k in range(n4):
            r = buf[off4 + rec_size[b.type] * k: off4 + rec_size[b.type] * (k + 1)]
            frm = struct.unpack_from('>H', r, 0)[0]
            # position of the start node (section 5 or 6 record: u, v first)
            u, v = struct.unpack_from('>HH', buf, frm)
            lon, lat = geo.to_lonlat(x1 + 64 * u, y1 + 64 * v)
            if not geo.point_in_poly(lon, lat, region):
                continue
            used = True
            spd, fw, ln, cl, jn = r[0x0A], r[0x0B], struct.unpack_from('>H', r, 0x0C)[0], r[0x10], r[0x11]
            R['links'] += 1; R['length_m'] += ln
            R['cls'][cl & 15] += 1; R['form'][fw & 15] += 1; R['dirn'][(fw >> 4) & 3] += 1
            R['speed_by_class'][cl & 15][spd & 31] += 1
            R['junction'][jn & 15] += 1
            if cl & 15 == 6:
                R['subtype6'][(cl >> 4) & 7] += 1
            R['toll'] += (fw >> 6) & 1; R['builtup'] += spd >> 7
        R['parcels'] += used
    total_units = sum(v[1] for v in by_type.values())
    out = dict(
        disc=dict(db_rel=rel, subrel=sub, unit=unit, carindb_units=total_units,
                  block_types={f'0x{t:02X}': dict(blocks=v[0], units=v[1], cf1=v[2])
                               for t, v in sorted(by_type.items())}),
        levels={str(t): v for t, v in lv.items()},
        region=dict(name='Nevada (rough polygon)', polygon=region,
                    levels={str(t): _plain(v) for t, v in reg.items()}),
    )
    return out


def _plain(d):
    out = {}
    for k, v in d.items():
        if isinstance(v, collections.Counter):
            out[k] = {str(a): b for a, b in sorted(v.items())}
        elif isinstance(v, dict):
            out[k] = {str(a): {str(x): y for x, y in sorted(b.items())} for a, b in sorted(v.items())}
        else:
            out[k] = v
    return out


if __name__ == '__main__':
    res = survey(sys.argv[1], sys.argv[2])
    os.makedirs(os.path.dirname(os.path.abspath(sys.argv[3])), exist_ok=True)
    with open(sys.argv[3], 'w') as f:
        json.dump(res, f, indent=1)
    print(json.dumps(res['levels'], indent=1))
    for t, v in res['region']['levels'].items():
        print(t, v['links'], v['length_m'] // 1000, 'km', v['cls'])
