"""Checks on our own road tiles (Phase 2) and an export for viewer/.

Every tile is read back the way the unit would see it:
  * CF=1 tiles are decoded again with OpenCarin's decoder and compared with the tile we
    encoded; every tile also re-parses and rebuilds byte for byte (tile26);
  * the ordering rules the unit needs (tile26.check_rules) hold everywhere;
  * the geometry is read with tools/carin.py's own parcel reader (independent of the
    writer) and must stay inside the tile;
  * edge-node twins sit at the same absolute position and point back at each other;
  * every coarse node lies on a street node; parents contain their children.
"""
import importlib.util
import json
import os
import shutil
import struct
from collections import Counter, defaultdict

from . import fmt, geo, tile26

_carin = None


def carin_reader():
    """tools/carin.py, loaded under another name (OpenCarin also has a 'carin' package)."""
    global _carin
    if _carin is None:
        path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'carin.py')
        spec = importlib.util.spec_from_file_location('carin_disc_reader', path)
        _carin = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(_carin)
    return _carin


def check(specs, oc_cf1=None):
    R = carin_reader()
    res = Counter()
    problems = defaultdict(list)
    by_id = {sp.block_id: sp for sp in specs}
    street_pos = set()
    decoded = {}
    for sp in specs:
        d = sp.decoded
        decoded[sp.block_id] = d
        # read back what is on the disc
        if sp.cf1:
            back = oc_cf1.decode_block(sp.raw, fmt.RECORD_SIZE_TABLE, fmt.DB_REL, fmt.SUBREL, fmt.UNIT)
            same = back[8:] == d[8:len(back)] and back[:6] == sp.raw[:6]
            res['cf1 decode == encoded tile'] += same
            if not same:
                problems['cf1 mismatch'].append(hex(sp.block_id))
        else:
            res['plain tiles'] += 1
        t = tile26.Tile.parse(d)
        rt_ok = t.build(fmt.UNIT, pad_units=len(d) // fmt.UNIT) == d
        res['tile26 parse/build identical'] += rt_ok
        if not rt_ok:
            problems['tile26 round trip'].append(hex(sp.block_id))
        for rule, (ok, bad) in tile26.check_rules(t).items():
            res[f'rule ok: {rule}'] += ok
            if bad:
                res[f'RULE BROKEN: {rule}'] += bad
                problems[rule].append(hex(sp.block_id))
        # independent geometry reader
        par = R.Parcel(None, 0, 0, sp.btype, buf=d)
        x1, y1, x2, y2 = t.bbox
        n = 0
        for attr, pts in par.polylines():
            n += 1
            if not all(x1 <= x <= x2 and y1 <= y <= y2 for x, y in pts):
                res['polylines outside the tile'] += 1
                problems['outside'].append(hex(sp.block_id))
        res['links read by carin.py'] += n
        if n != len(t.secs[4]) - 1:
            problems['carin.py link count'].append(hex(sp.block_id))
        if sp.btype == 0:
            for sec in (5, 6):
                for raw, _ in t.secs[sec]:
                    u, v = struct.unpack_from('>HH', raw, 0)
                    street_pos.add((x1 + 64 * u, y1 + 64 * v))
    # twins
    for sp in specs:
        d = decoded[sp.block_id]
        x1, y1 = struct.unpack_from('>2i', d, 0x44)
        o6, n6 = struct.unpack_from('>HH', d, 8 + 4 * 6)
        for k in range(n6):
            u, v, _, _, bid, off = struct.unpack_from('>4HIH', d, o6 + 16 * k)
            if bid == 0:
                res['edge nodes without a twin'] += 1
                continue
            nb = decoded.get(bid)
            if nb is None:
                res['TWIN IN NO TILE'] += 1
                continue
            nx1, ny1 = struct.unpack_from('>2i', nb, 0x44)
            tu, tv, _, _, bbid, boff = struct.unpack_from('>4HIH', nb, off)
            ok = (nx1 + 64 * tu, ny1 + 64 * tv) == (x1 + 64 * u, y1 + 64 * v) and bbid == sp.block_id \
                and boff == o6 + 16 * k
            res['twins ok' if ok else 'TWINS BROKEN'] += 1
    # coarse nodes on street nodes; parents
    for sp in specs:
        if sp.btype:
            t = tile26.Tile.parse(decoded[sp.block_id])
            x1, y1 = t.bbox[:2]
            for sec in (5, 6):
                for raw, _ in t.secs[sec]:
                    u, v = struct.unpack_from('>HH', raw, 0)
                    on = (x1 + 64 * u, y1 + 64 * v) in street_pos
                    res['coarse nodes on a street node' if on else 'COARSE NODE OFF THE STREETS'] += 1
        if sp.parent is not None:
            a, b = sp.tile.bbox, sp.parent.tile.bbox
            inside = b[0] <= a[0] and b[1] <= a[1] and a[2] <= b[2] and a[3] <= b[3]
            res['parent contains tile' if inside else 'parent does not contain tile'] += 1
    return dict(res), {k: v[:10] for k, v in problems.items()}


def export_viewer(specs, out_dir, viewer_html=None):
    """Write viewer data (same format as tools/carin.py export / streets) for our tiles."""
    R = carin_reader()
    data = os.path.join(out_dir, 'data')
    os.makedirs(os.path.join(data, 'l0'), exist_ok=True)
    per = {lv: {'attrs': [], 'lines': []} for lv in (1, 2, 3)}
    cells = {}
    for sp in specs:
        par = R.Parcel(None, 0, 0, sp.btype, buf=sp.decoded)
        for attr, pts in par.polylines():
            if sp.btype:
                flat = []
                for x, y in pts:
                    lon, lat = geo.to_lonlat(x, y)
                    flat += [round(lon, 5), round(lat, 5)]
                per[sp.btype]['attrs'].append(attr)
                per[sp.btype]['lines'].append(flat)
            else:
                flat, px, py = [], 0, 0
                for x, y in pts:
                    lon, lat = geo.to_lonlat(x, y)
                    ix, iy = round(lon * 1e5), round(lat * 1e5)
                    flat += [ix - px, iy - py]
                    px, py = ix, iy
                key = f'{int((flat[0] / 1e5 + 180) // R.CELL)}_{int((flat[1] / 1e5 + 90) // R.CELL)}'
                c = cells.setdefault(key, {'attrs': [], 'lines': []})
                c['attrs'].append(attr)
                c['lines'].append(flat)
    for lv, dd in per.items():
        with open(os.path.join(data, f'level{lv}.json'), 'w') as f:
            json.dump(dd, f, separators=(',', ':'))
    for key, dd in cells.items():
        with open(os.path.join(data, 'l0', key + '.json'), 'w') as f:
            json.dump(dd, f, separators=(',', ':'))
    with open(os.path.join(data, 'l0', 'index.json'), 'w') as f:
        json.dump({'cell': R.CELL, 'cells': sorted(cells), 'format': 'delta-1e-5'}, f)
    if viewer_html and os.path.exists(viewer_html):
        shutil.copyfile(viewer_html, os.path.join(out_dir, 'index.html'))
    return dict(cells=len(cells), **{f'level{lv}_links': len(per[lv]['lines']) for lv in per})
