"""Phase 0, step 2: CF=1 type 0x00 round trip with OpenCarin.

For a sample of compressed street parcels on the original disc:
    raw --decode--> D --encode_type00--> E --decode--> D2
and report: D2 == D (bytes 4..), E == raw (bit-exact re-encode), and whether
E fits in the block's original sector extent.

Usage: py tools/phase0_roundtrip.py <track1.iso> <OpenCarin dir> [step] [report.json]
    step = test every step-th CF=1 type 0x00 block (default 50; 1 = all)
"""
import json
import sys
import time


def main(iso, oc_dir, step=50, report=None):
    sys.path.insert(0, oc_dir)
    from carin.parser.iso import open_image, CarinVolume
    from carin.parser import cf1

    vol = CarinVolume(open_image(iso))
    vol.calibrate(32)
    rel, sub, unit = vol.db_rel, vol.subrel, vol.sector_size
    print(f'DB-REL {rel}, sub-revision {sub}, unit {unit}')
    assert (rel, sub, unit) == (26, 8, 2048), 'unexpected volume parameters'

    blocks = [b for b in vol.walk() if b.type == 0 and b.comp & 1]
    sample = blocks[::step]
    res = dict(db_rel=rel, subrel=sub, unit=unit, cf1_type00_total=len(blocks), tested=0,
               decode_match=0, bit_exact=0, fits_extent=0, failures=[],
               bytes_orig=0, bytes_reenc=0)
    t0 = time.time()
    for i, b in enumerate(sample):
        raw = vol.read_sectors(b.sector, b.length)
        try:
            d = cf1.decode_block(raw, vol.layout, rel, sub, unit)
            e = cf1.encode_type00(d, vol.layout, rel, sub, unit)
            d2 = cf1.decode_block(e + bytes(-len(e) % unit), vol.layout, rel, sub, unit)
        except Exception as ex:          # noqa: BLE001 - report every failure
            res['failures'].append(dict(sector=b.sector, error=f'{type(ex).__name__}: {ex}'))
            continue
        res['tested'] += 1
        used = len(raw.rstrip(b'\0'))
        res['bytes_orig'] += used
        res['bytes_reenc'] += len(e)
        if d2[4:] == d[4:]:
            res['decode_match'] += 1
        else:
            res['failures'].append(dict(sector=b.sector, error='decode(encode(D)) != D'))
        if e.rstrip(b'\0') == raw.rstrip(b'\0'):
            res['bit_exact'] += 1
        if len(e) <= b.length * unit:
            res['fits_extent'] += 1
        if i % 100 == 0:
            print(f'  {i}/{len(sample)}  {time.time() - t0:.0f}s')
    res['seconds'] = round(time.time() - t0, 1)
    print(json.dumps({k: v for k, v in res.items() if k != 'failures'}, indent=1))
    print('failures:', len(res['failures']), res['failures'][:5])
    if report:
        with open(report, 'w') as f:
            json.dump(res, f, indent=1)
    return res


if __name__ == '__main__':
    a = sys.argv[1:]
    main(a[0], a[1], int(a[2]) if len(a) > 2 else 50, a[3] if len(a) > 3 else None)
