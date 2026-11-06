"""Fixtures for the C# port's parity tests (csharp/tests): OpenCarin's CF=1 decode and
re-encode of a sample of the original disc's compressed street tiles.

Usage: py tools/make_parity_fixtures.py <track1.iso> <OpenCarin dir> <out dir> [step]
Writes <out>/cf1/<unit>.dec (decoded block) and <unit>.enc (encode_type00 of it).
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from osm2carin import fmt  # noqa: E402


def main(iso, oc, out, step=50):
    sys.path.insert(0, oc)
    sys.modules.pop('carin', None)
    from carin.parser.iso import CarinVolume, open_image
    from carin.parser import cf1
    vol = CarinVolume(open_image(iso))
    d = os.path.join(out, 'cf1')
    os.makedirs(d, exist_ok=True)
    n = k = 0
    for b in vol.walk():
        if b.type != 0 or not b.comp & 1:
            continue
        n += 1
        if n % step:
            continue
        raw = vol.read_sectors(b.sector, b.length)
        dec = cf1.decode_block(raw, fmt.RECORD_SIZE_TABLE, 26, 8, 2048)
        enc = cf1.encode_type00(dec, fmt.RECORD_SIZE_TABLE, 26, 8, 2048)
        open(os.path.join(d, f'{b.sector}.dec'), 'wb').write(dec)
        open(os.path.join(d, f'{b.sector}.enc'), 'wb').write(enc)
        k += 1
    print(f'{k} tiles written to {d}')


if __name__ == '__main__':
    a = sys.argv[1:]
    main(a[0], a[1], a[2], int(a[3]) if len(a) > 3 else 50)
