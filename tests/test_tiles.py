"""Phase 2 tests: the DB-REL 26 tile model against the original disc, and our own tiles.

    py -m unittest discover -s tests -v
The original-disc tests need P38_Navigation_CD_2002_California_Nevada.bin (repo root or
CARIN_BIN) and OpenCarin (third_party/OpenCarin or OPENCARIN); the rest need nothing.
"""
import os
import shutil
import struct
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import cdimage                                                     # noqa: E402
from osm2carin import fmt, link, model, tile26, tiles, validate    # noqa: E402
from test_osm2carin import write_osm                               # noqa: E402

BIN = os.environ.get('CARIN_BIN', os.path.join(ROOT, 'P38_Navigation_CD_2002_California_Nevada.bin'))
OC = os.environ.get('OPENCARIN', os.path.join(ROOT, 'third_party', 'OpenCarin'))


def opencarin():
    if not os.path.isdir(OC):
        return None
    sys.path.insert(0, OC)
    sys.modules.pop('carin', None)
    from carin.parser import cf1
    return cf1


@unittest.skipUnless(os.path.exists(BIN) and os.path.isdir(OC), 'disc image or OpenCarin missing')
class OriginalTiles(unittest.TestCase):
    """parse -> build is byte-identical on the disc's own road tiles, the ordering rules hold."""

    @classmethod
    def setUpClass(cls):
        cls.cf1 = opencarin()
        from carin.parser.iso import CarinVolume, open_image
        cls.tmp = tempfile.mkdtemp()
        cdimage.split(BIN, cls.tmp)
        cls.img = open_image(os.path.join(cls.tmp, 'track1.iso'))
        cls.vol = CarinVolume(cls.img)
        cls.vol.calibrate(32)

    @classmethod
    def tearDownClass(cls):
        cls.img.close()
        shutil.rmtree(cls.tmp, True)

    def test_table_matches_disc(self):
        self.assertEqual(self.vol.layout, fmt.RECORD_SIZE_TABLE)

    def test_round_trip_and_rules(self):
        n = 0
        for b in self.vol.walk():
            if b.type > 3:
                continue
            n += 1
            if n % 400:
                continue
            raw = self.vol.read_sectors(b.sector, b.length)
            d = self.cf1.decode_block(raw, fmt.RECORD_SIZE_TABLE, 26, 8, 2048) if b.comp & 1 else raw
            t = tile26.Tile.parse(d)
            out = t.build()
            if b.comp & 1:
                out = out[:6] + bytes(d[6:8]) + out[8:]
            self.assertEqual(out, bytes(d), f'block {b.sector}')
            for rule, (ok, bad) in tile26.check_rules(t).items():
                if rule != 'cycle clockwise from smallest bearing':     # 99.96% on the disc
                    self.assertEqual(bad, 0, f'{rule} in block {b.sector}')
        self.assertGreater(n, 30000)


class OurTiles(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        path = os.path.join(cls.tmp, 'town.osm')
        write_osm(path)
        m, S, _ = model.build(path, 'default', log=lambda *a: None)
        cls.rt = tiles.build(m, S, 'testville', log=lambda *a: None)
        cls.cf1 = opencarin()
        cls.specs = link.link(cls.rt, 100, cls.cf1, log=lambda *a: None)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, True)

    def test_levels_present(self):
        types = {sp.btype for sp in self.specs}
        self.assertIn(0, types)
        self.assertIn(3, types)              # motorway + ramp + class <= 3

    def test_checks_pass(self):
        res, problems = validate.check(self.specs, self.cf1)
        self.assertEqual(problems, {})
        self.assertFalse([k for k in res if k.isupper() or 'BROKEN' in k or 'OFF THE' in k])
        self.assertEqual(res['links read by carin.py'], sum(len(sp.tile.secs[4]) - 1 for sp in self.specs))

    def test_block_ids(self):
        pos = 100
        for sp in self.specs:
            self.assertEqual(sp.block_id >> 8, pos)
            self.assertEqual(len(sp.raw), (sp.block_id & 0xFF) * 2048)
            self.assertEqual(struct.unpack_from('>I', sp.raw, 0)[0], sp.block_id)
            pos += sp.block_id & 0xFF

    def test_street_names_in_blob(self):
        blobs = b''.join(sp.tile.blob for sp in self.specs if sp.btype == 0)
        for name in (b'main st\0', b'first ave\0', b'testville\0', b'united states\0'):
            self.assertIn(name, blobs)

    def test_parent_of_street_tile(self):
        street = [sp for sp in self.specs if sp.btype == 0]
        with_parent = [sp for sp in street if sp.parent is not None]
        self.assertTrue(with_parent)
        for sp in with_parent:
            self.assertEqual(sp.parent.btype, 3)
            self.assertEqual(struct.unpack_from('>I', sp.decoded, 0x58)[0], sp.parent.block_id)

    def test_tiles_on_grid(self):
        for sp in self.specs:
            x1, y1, x2, y2 = sp.tile.bbox
            self.assertEqual((x1 - tiles.ORIGIN[0]) % tiles.BASE, 0)
            self.assertEqual((y1 - tiles.ORIGIN[1]) % tiles.BASE, 0)
            w, h = x2 - x1, y2 - y1
            self.assertIn(h, (w, w // 2))

    def test_clean_name(self):
        self.assertEqual(tiles.clean_name('Main St & 5th #2'), 'main st and 5th no 2')
        self.assertEqual(tiles.clean_name('Ñandú  Ave'), 'nandu ave')

    def test_viewer_export(self):
        out = os.path.join(self.tmp, 'viewer')
        st = validate.export_viewer(self.specs, out)
        self.assertTrue(os.path.exists(os.path.join(out, 'data', 'l0', 'index.json')))
        self.assertGreater(st['cells'], 0)


if __name__ == '__main__':
    unittest.main()
