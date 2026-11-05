"""Decoder for Philips/VDO CARiN map discs (carindb), as found on the
Range Rover P38 navigation CD (CD-ID 5944, DB-REL 26).

Format knowledge here is reverse-engineered; see carin-disc-findings.md.

Usage:
    python carin.py iso     <image.bin> <out.iso>      # raw .bin -> plain ISO
    python carin.py extract <image.bin> <out_dir>      # raw .bin -> carindb etc.
    python carin.py export  <carindb> <out_dir>        # road levels 1-3 -> viewer data
    python carin.py streets <out.iso> <OpenCarin dir> <out_dir> [limit]
                                                       # street level 0 -> viewer cells
                                                       # (needs github.com/fdemusso/OpenCarin)
"""
import json
import os
import struct
import sys

SECTOR = 2048
RAW_SECTOR = 2352
UNIT_DEG = 1.8e-7          # 1 global unit = 1.8e-7 degrees (90 deg = 500,000,000)
LON_OFFSET_DEG = 390.0     # lon = x * UNIT_DEG - 390  (calibrated on Key West, ~0.05 deg)
LOCAL_UNIT = 64            # parcel-local coordinate step, in global units


def to_lonlat(x, y):
    return x * UNIT_DEG - LON_OFFSET_DEG, y * UNIT_DEG


# --------------------------------------------------------------------------
# Disc image handling
# --------------------------------------------------------------------------

def bin_to_iso(bin_path, iso_path):
    """Strip MODE2/2352 raw sectors down to 2048-byte user data.
    Stops at the first sector that is not a data sector (audio track)."""
    with open(bin_path, 'rb') as f, open(iso_path, 'wb') as o:
        while True:
            s = f.read(RAW_SECTOR)
            if len(s) < RAW_SECTOR or s[:12] != b'\x00' + b'\xff' * 10 + b'\x00':
                break
            o.write(s[24:24 + SECTOR])


def iso_files(iso_path):
    """Minimal ISO9660 root-directory reader -> {name: (lba, size)}."""
    with open(iso_path, 'rb') as f:
        f.seek(16 * SECTOR)
        pvd = f.read(SECTOR)
        root = pvd[156:190]
        lba, size = struct.unpack('<I', root[2:6])[0], struct.unpack('<I', root[10:14])[0]
        f.seek(lba * SECTOR)
        data = f.read(size)
    out, i = {}, 0
    while i < len(data):
        ln = data[i]
        if ln == 0:
            i = (i // SECTOR + 1) * SECTOR
            continue
        rec = data[i:i + ln]
        name = rec[33:33 + rec[32]].split(b';')[0].decode('ascii', 'replace')
        if name not in ('\x00', '\x01'):
            out[name] = (struct.unpack('<I', rec[2:6])[0], struct.unpack('<I', rec[10:14])[0])
        i += ln
    return out


def extract(bin_path, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    iso = os.path.join(out_dir, 'disc.iso')
    bin_to_iso(bin_path, iso)
    with open(iso, 'rb') as f:
        for name, (lba, size) in iso_files(iso).items():
            f.seek(lba * SECTOR)
            with open(os.path.join(out_dir, name), 'wb') as o:
                o.write(f.read(size))
            print(f'{name:10s} {size:>12,d}')
    os.remove(iso)


# --------------------------------------------------------------------------
# carindb structure
# --------------------------------------------------------------------------

def walk_blocks(db):
    """Yield (sector, n_sectors, type, compressed, unpacked_sectors).
    Block header: u24 start sector, u8 sector count, u16 type, u8 compressed flag,
    u8 unpacked size (sectors)."""
    s, n = 0, len(db) // SECTOR
    while s < n:
        bid, typ, cf, us = struct.unpack_from('>IHBB', db, s * SECTOR)
        ln = bid & 0xff
        if bid >> 8 != s or ln == 0:
            s += 1
            continue
        yield s, ln, typ, cf, us
        s += ln


class Parcel:
    """A road-network parcel (block types 0-3). Pass `buf` for a block that was
    already decompressed (see `streets`), otherwise it is read from `db`."""

    def __init__(self, db, sector, n_sectors, typ, buf=None):
        self.p = memoryview(buf) if buf is not None else \
            memoryview(db)[sector * SECTOR:(sector + n_sectors) * SECTOR]
        self.typ = typ
        self.sector = sector
        pairs = struct.unpack_from('>30H', self.p, 8)
        self.sections = [(pairs[i], pairs[i + 1]) for i in range(0, 30, 2)]
        self.x1, self.y1, self.x2, self.y2 = struct.unpack_from('>4i', self.p, 0x44)

    def glob(self, u, v):
        return self.x1 + u * LOCAL_UNIT, self.y1 + v * LOCAL_UNIT

    def nodes(self):
        """Section 5: 8-byte nodes (u, v, first link offset, flags), keyed by offset."""
        off, n = self.sections[5]
        out = {off + 8 * k: struct.unpack_from('>4H', self.p, off + 8 * k) for k in range(n)}
        # Section 6: 16-byte tile-edge nodes (u, v, link offset, flags, neighbour ref...)
        off, n = self.sections[6]
        out.update({off + 16 * k: struct.unpack_from('>4H', self.p, off + 16 * k) for k in range(n)})
        return out

    def links(self):
        """Section 4: 24-byte links. Returns list of dicts."""
        off, n = self.sections[4]
        size = 30 if self.typ == 0 else 24   # level 0 links carry 3 extra words
        out = []
        for k in range(n):
            f = struct.unpack_from('>%dH' % (size // 2), self.p, off + size * k)
            out.append(dict(frm=f[0], to=f[1], shape=f[2], attr=f[5], raw=f))
        return out

    def polylines(self):
        """Yield (attr, [(x, y), ...]) in global units for every link."""
        nodes = self.nodes()
        links = self.links()
        s7, n7 = self.sections[7]
        s7_end = s7 + 6 * n7
        # The link's shape field is a running cursor into section 7: link i owns
        # points [shape_i, shape_{i+1}), with the last link running to section end.
        ends = [l['shape'] for l in links[1:]] + [s7_end]
        for l, end in zip(links, ends):
            a, b = nodes.get(l['frm']), nodes.get(l['to'])
            if a is None or b is None:
                continue
            pts = [self.glob(a[0], a[1])]
            so = l['shape']
            if s7 <= so <= end <= s7_end:
                for o in range(so, end, 6):
                    u, v, _ = struct.unpack_from('>3H', self.p, o)
                    pts.append(self.glob(u, v))
            pts.append(self.glob(b[0], b[1]))
            yield l['attr'], pts


def export(db_path, out_dir, levels=(0, 1, 2, 3)):
    """Write one JSON file per level: {"attrs": [...], "lines": [[lon,lat,...], ...]}."""
    db = open(db_path, 'rb').read()
    os.makedirs(out_dir, exist_ok=True)
    per = {lv: {'attrs': [], 'lines': []} for lv in levels}
    bad = 0
    for s, ln, typ, cf, _ in walk_blocks(db):
        if typ not in per or cf:
            continue
        try:
            par = Parcel(db, s, ln, typ)
            for attr, pts in par.polylines():
                flat = []
                for x, y in pts:
                    lon, lat = to_lonlat(x, y)
                    flat += [round(lon, 5), round(lat, 5)]
                per[typ]['attrs'].append(attr)
                per[typ]['lines'].append(flat)
        except struct.error:
            bad += 1
    for lv, data in per.items():
        path = os.path.join(out_dir, f'level{lv}.json')
        with open(path, 'w') as f:
            json.dump(data, f, separators=(',', ':'))
        print(f'level {lv}: {len(data["lines"]):,} links -> {path}')
    if bad:
        print('parcels skipped (parse error):', bad)


CELL = 0.1   # degrees; street tiles for the viewer are grouped into CELL x CELL files


def streets(iso_path, opencarin_dir, out_dir, limit=None):
    """Decode every level-0 (street) parcel, compressed or not, and write
    out_dir/l0/<ix>_<iy>.json grouped by CELL-degree cells, plus an index.

    Compressed (CF=1) parcels are decoded with OpenCarin's CF=1 decoder
    (https://github.com/fdemusso/OpenCarin, MIT licence), which this disc's
    DB-REL 26 supports through its 0x14/0x15/0x17 passes.
    """
    sys.path.insert(0, opencarin_dir)
    from carin.parser.iso import open_image, CarinVolume
    from carin.parser import cf1

    vol = CarinVolume(open_image(iso_path))
    vol.calibrate(32)
    print(f'DB-REL {vol.db_rel}, sub-revision {vol.subrel}, unit {vol.sector_size}')
    cells, n_ok, n_bad, n_links = {}, 0, 0, 0
    heads = [b for b in vol.walk() if b.type == 0]
    if limit:
        heads = heads[:limit]
    for i, b in enumerate(heads):
        raw = vol.read_sectors(b.sector, b.length)
        try:
            buf = cf1.decode_block(raw, vol.layout, vol.db_rel, vol.subrel, vol.sector_size) \
                if b.comp & 1 else raw
            par = Parcel(None, b.sector, b.length, 0, buf=buf)
            for attr, pts in par.polylines():
                # integer 1e-5 degree units: first point absolute, then deltas
                flat, px, py = [], 0, 0
                for x, y in pts:
                    lon, lat = to_lonlat(x, y)
                    ix, iy = round(lon * 1e5), round(lat * 1e5)
                    flat += [ix - px, iy - py]
                    px, py = ix, iy
                key = f'{int((flat[0] / 1e5 + 180) // CELL)}_{int((flat[1] / 1e5 + 90) // CELL)}'
                c = cells.setdefault(key, {'attrs': [], 'lines': []})
                c['attrs'].append(attr)
                c['lines'].append(flat)
                n_links += 1
            n_ok += 1
        except Exception:
            n_bad += 1
        if i % 2000 == 0:
            print(f'  {i:,}/{len(heads):,} parcels, {n_links:,} links')
    d = os.path.join(out_dir, 'l0')
    os.makedirs(d, exist_ok=True)
    for key, data in cells.items():
        with open(os.path.join(d, key + '.json'), 'w') as f:
            json.dump(data, f, separators=(',', ':'))
    with open(os.path.join(d, 'index.json'), 'w') as f:
        json.dump({'cell': CELL, 'cells': sorted(cells), 'format': 'delta-1e-5'}, f)
    print(f'street parcels decoded {n_ok:,}, failed {n_bad:,}; {n_links:,} links in {len(cells):,} cells')


if __name__ == '__main__':
    cmd = sys.argv[1] if len(sys.argv) > 1 else ''
    if cmd == 'iso':
        bin_to_iso(sys.argv[2], sys.argv[3])
    elif cmd == 'extract':
        extract(sys.argv[2], sys.argv[3])
    elif cmd == 'export':
        export(sys.argv[2], sys.argv[3], levels=(1, 2, 3))
    elif cmd == 'streets':
        streets(sys.argv[2], sys.argv[3], sys.argv[4],
                int(sys.argv[5]) if len(sys.argv) > 5 else None)
    else:
        print(__doc__)
