"""Phase 0 tests against the original disc (skipped when it is not present).

    py -m unittest discover -s tests -v
Fixture: P38_Navigation_CD_2002_California_Nevada.bin in the repo root (git-ignored);
OpenCarin in third_party/OpenCarin for the CF=1 test.
"""
import os
import shutil
import struct
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import cdimage  # noqa: E402

BIN = os.environ.get('CARIN_BIN', os.path.join(ROOT, 'P38_Navigation_CD_2002_California_Nevada.bin'))
OC = os.environ.get('OPENCARIN', os.path.join(ROOT, 'third_party', 'OpenCarin'))
N_FORM1 = 186541


def raw_sectors(lba, n):
    with open(BIN, 'rb') as f:
        f.seek(lba * cdimage.RAW)
        return f.read(n * cdimage.RAW)


@unittest.skipUnless(os.path.exists(BIN), 'original disc image not present')
class SectorCodec(unittest.TestCase):
    def check_form1(self, lba, n):
        raw = raw_sectors(lba, n)
        user = b''.join(raw[i * 2352 + 24:i * 2352 + 2072] for i in range(n))
        self.assertEqual(cdimage.form1_sectors(user, lba), raw)

    def test_form1_start(self):         # PVD, directory, companion files
        self.check_form1(0, 600)

    def test_form1_middle(self):
        self.check_form1(93_000, 300)

    def test_form1_last(self):
        self.check_form1(N_FORM1 - 300, 300)

    def test_form2_postgap(self):
        self.assertEqual(cdimage.form2_sectors(150, N_FORM1), raw_sectors(N_FORM1, 150))

    def test_verify_detects_corruption(self):
        raw = bytearray(raw_sectors(1000, 1))
        user = bytes(raw[24:2072])
        raw[500] ^= 1
        self.assertNotEqual(cdimage.form1_sectors(user, 1000), bytes(raw))


@unittest.skipUnless(os.path.exists(BIN) and os.path.isdir(OC), 'disc image or OpenCarin missing')
class Cf1Type00RoundTrip(unittest.TestCase):
    def test_sample(self):
        sys.path.insert(0, OC)
        sys.modules.pop('carin', None)
        from carin.parser import cf1
        from carin.parser.iso import CarinVolume, open_image
        tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, tmp, True)
        cdimage.split(BIN, tmp)
        img = open_image(os.path.join(tmp, 'track1.iso'))
        self.addCleanup(img.close)
        vol = CarinVolume(img)
        vol.calibrate(32)
        self.assertEqual((vol.db_rel, vol.subrel, vol.sector_size), (26, 8, 2048))
        blocks = [b for b in vol.walk() if b.type == 0 and b.comp & 1][::2000]
        self.assertGreater(len(blocks), 10)
        for b in blocks:
            raw = vol.read_sectors(b.sector, b.length)
            d = cf1.decode_block(raw, vol.layout, 26, 8, 2048)
            e = cf1.encode_type00(d, vol.layout, 26, 8, 2048)
            self.assertLessEqual(len(e), b.length * 2048)
            d2 = cf1.decode_block(e + bytes(-len(e) % 2048), vol.layout, 26, 8, 2048)
            self.assertEqual(d2[4:], d[4:], f'block {b.sector}')
            self.assertEqual(struct.unpack_from('>4i', e, 0x44), struct.unpack_from('>4i', raw, 0x44))


if __name__ == '__main__':
    unittest.main()
