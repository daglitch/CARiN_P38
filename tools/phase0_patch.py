"""Phase 0, step 3: same-length name-patched test images for the head unit.

Image A  test-patched.bin   : POI name patched in place, plain (CF=0) blocks
                              only. Tests whether the unit checks data integrity.
Image B  test-reencoded.bin : A + one street renamed inside every CF=1 street
                              parcel that carries it, re-encoded with
                              OpenCarin's encode_type00. Tests whether the unit
                              accepts our CF=1 bitstreams.

Nothing moves: every changed block keeps its sectors (block chain, ISO
directory and file sizes are unchanged).

Usage: py tools/phase0_patch.py <track1.iso> <audio.raw> <OpenCarin dir> <out_dir>
"""
import json
import os
import shutil
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import carin as ctool   # noqa: E402  (our reader; OpenCarin also has a 'carin' package)
import cdimage          # noqa: E402
del sys.modules['carin']

POI_OLD, POI_NEW = b'land rover san jose', b'land rover p38 test'
ST_OLD, ST_NEW = b'stevens creek blvd', b'stevens creek test'
# San Jose / Santa Clara / Cupertino, degrees (lon0, lat0, lon1, lat1)
ST_BOX = (-122.10, 37.25, -121.85, 37.36)
assert len(POI_OLD) == len(POI_NEW) and len(ST_OLD) == len(ST_NEW)


def carindb_extent(iso_path):
    lba, size = ctool.iso_files(iso_path)['carindb']
    return lba * cdimage.USER, size


def patch_poi(iso_path, log):
    """Replace POI_OLD with POI_NEW wherever it occurs inside a plain block."""
    base, size = carindb_extent(iso_path)
    with open(iso_path, 'r+b') as f:
        f.seek(base)
        db = bytearray(f.read(size))
        hits = []
        for s, ln, typ, cf, _ in ctool.walk_blocks(db):
            if cf & 1:
                continue
            lo, hi = s * 2048, (s + ln) * 2048
            i = db.find(POI_OLD, lo, hi)
            while i >= 0:
                # whole string only: NUL before and after
                if db[i - 1] == 0 and db[i + len(POI_OLD)] == 0:
                    db[i:i + len(POI_NEW)] = POI_NEW
                    f.seek(base + i)
                    f.write(POI_NEW)
                    hits.append(dict(block=s, type=typ, offset=i))
                i = db.find(POI_OLD, i + 1, hi)
    log['poi'] = dict(old=POI_OLD.decode(), new=POI_NEW.decode(), hits=hits)
    print(f'POI "{POI_OLD.decode()}" -> "{POI_NEW.decode()}": {len(hits)} occurrences')
    return hits


def patch_street(iso_path, oc_dir, log):
    """Rename ST_OLD in every CF=1 type 0x00 parcel in ST_BOX and re-encode it."""
    sys.path.insert(0, oc_dir)
    from carin.parser.iso import open_image, CarinVolume
    from carin.parser import cf1

    vol = CarinVolume(open_image(iso_path))
    vol.calibrate(32)
    rel, sub, unit = vol.db_rel, vol.subrel, vol.sector_size
    base, _ = carindb_extent(iso_path)
    done = []
    for b in vol.walk():
        if b.type != 0 or not b.comp & 1:
            continue
        raw = vol.read_sectors(b.sector, b.length)
        x1, y1, x2, y2 = struct.unpack_from('>4i', raw, 0x44)   # plain prolog
        lon0, lat0 = ctool.to_lonlat(x1, y1)
        lon1, lat1 = ctool.to_lonlat(x2, y2)
        if lon1 < ST_BOX[0] or lon0 > ST_BOX[2] or lat1 < ST_BOX[1] or lat0 > ST_BOX[3]:
            continue
        d = cf1.decode_block(raw, vol.layout, rel, sub, unit)
        n = 0
        p = bytearray(d)
        i = p.find(ST_OLD)
        while i >= 0:
            if p[i - 1] == 0 and p[i + len(ST_OLD)] == 0:
                p[i:i + len(ST_NEW)] = ST_NEW
                n += 1
            i = p.find(ST_OLD, i + 1)
        if not n:
            continue
        e = cf1.encode_type00(bytes(p), vol.layout, rel, sub, unit)
        room = b.length * unit
        if len(e) > room:
            raise RuntimeError(f'block {b.sector}: re-encoded {len(e)} > {room}')
        e += bytes(room - len(e))
        if cf1.decode_block(e, vol.layout, rel, sub, unit)[4:] != bytes(p)[4:]:
            raise RuntimeError(f'block {b.sector}: round trip mismatch')
        if e[:8] != raw[:8]:
            raise RuntimeError(f'block {b.sector}: header changed')
        with open(iso_path, 'r+b') as f:
            f.seek(base + b.sector * unit)
            f.write(e)
        done.append(dict(block=b.sector, units=b.length, names=n,
                         orig_bytes=len(raw.rstrip(b'\0')), new_bytes=len(e.rstrip(b'\0')),
                         bbox=[round(lon0, 4), round(lat0, 4), round(lon1, 4), round(lat1, 4)]))
    log['street'] = dict(old=ST_OLD.decode(), new=ST_NEW.decode(), box=ST_BOX, blocks=done)
    print(f'street "{ST_OLD.decode()}" -> "{ST_NEW.decode()}": {len(done)} CF=1 parcels re-encoded')
    return done


def make_image(iso, audio, out_bin, log):
    t1 = cdimage.build(iso, out_bin, audio)
    ok1, bad1, ok2, bad2, _ = cdimage.verify(out_bin)
    # read back: raw -> ISO must equal what we put in
    back = out_bin + '.readback'
    os.makedirs(back, exist_ok=True)
    cdimage.split(out_bin, back)
    same = cdimage.sha1(os.path.join(back, 'track1.iso')) == cdimage.sha1(iso)
    shutil.rmtree(back)
    log[os.path.basename(out_bin)] = dict(track1_sectors=t1, form1_ok=ok1, form1_bad=bad1,
                                          form2_ok=ok2, form2_bad=bad2, readback_identical=same,
                                          sha1=cdimage.sha1(out_bin))
    print(f'{out_bin}: EDC/ECC {ok1:,}+{ok2} ok, {bad1 + bad2} bad; read-back identical: {same}')


def diff_sectors(a, b):
    out = []
    with open(a, 'rb') as fa, open(b, 'rb') as fb:
        lba = 0
        while True:
            x, y = fa.read(2048), fb.read(2048)
            if not x:
                break
            if x != y:
                out.append(lba)
            lba += 1
    return out


def main(iso, audio, oc_dir, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    log = {}
    a_iso = os.path.join(out_dir, 'test-patched.iso')
    shutil.copyfile(iso, a_iso)
    patch_poi(a_iso, log)
    log['changed_iso_sectors_A'] = diff_sectors(iso, a_iso)
    make_image(a_iso, audio, os.path.join(out_dir, 'test-patched.bin'), log)

    b_iso = os.path.join(out_dir, 'test-reencoded.iso')
    shutil.copyfile(a_iso, b_iso)
    patch_street(b_iso, oc_dir, log)
    log['changed_iso_sectors_B'] = diff_sectors(iso, b_iso)
    make_image(b_iso, audio, os.path.join(out_dir, 'test-reencoded.bin'), log)
    for p in (a_iso, b_iso):
        os.remove(p)
    with open(os.path.join(out_dir, 'patch-report.json'), 'w') as f:
        json.dump(log, f, indent=1)


if __name__ == '__main__':
    main(*sys.argv[1:5])
