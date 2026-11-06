"""Phase 1 tests: road model, attributes, POIs, estimate, regions. No download needed.

    py -m unittest discover -s tests -v
"""
import os
import shutil
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'tools'))

import numpy as np          # noqa: E402
import osmium               # noqa: E402

from osm2carin import attrs, estimate, geo, model, poi, regions   # noqa: E402

# A small town at Las Vegas: a motorway, an on-ramp, a residential grid, a one-way
# street tagged oneway=-1, a roundabout loop, a driveway (dropped), a track
# without name (dropped by the default profile), a turn restriction and POIs.
BASE_LON, BASE_LAT = -115.17, 36.11
D = 0.001   # ~100 m


def P(i, j):
    return BASE_LON + i * D, BASE_LAT + j * D


NODES = {
    1: P(0, 0), 2: P(1, 0), 3: P(2, 0), 4: P(3, 0),            # residential row y=0
    5: P(1, 1), 6: P(1, 2),                                     # residential column x=1
    7: P(2, 1),                                                 # oneway=-1 from 3 -> 7 reversed
    10: P(0, 5), 11: P(2, 5), 12: P(4, 5),                      # motorway y=5
    13: P(2, 3), 14: P(2, 4),                                   # ramp 6? no: ramp 13 -> 14 -> 11
    20: P(5, 0), 21: P(5.2, 0.2), 22: P(5, 0.4), 23: P(4.8, 0.2),   # roundabout around (5, .2)
    30: P(3, 1),                                                # driveway end
    40: P(6, 6), 41: P(7, 7),                                   # track
    90: P(1.1, 0.1),                                            # POI fuel
    91: P(1.2, 0.1),                                            # bench (no POI)
}
WAYS = [
    (100, [1, 2, 3, 4], dict(highway='residential', name='Main St')),
    (101, [2, 5, 6], dict(highway='residential', name='First Ave')),
    (102, [7, 3], dict(highway='residential', name='Back St', oneway='-1')),
    (103, [10, 11, 12], dict(highway='motorway', ref='I 15')),
    (104, [6, 13, 14, 11], dict(highway='motorway_link')),
    (105, [4, 20], dict(highway='residential', name='East St')),
    (106, [20, 21, 22, 23, 20], dict(highway='residential', junction='roundabout')),
    (107, [4, 30], dict(highway='service', service='driveway')),
    (108, [40, 41], dict(highway='track')),
    (109, [22, 41], dict(highway='track', name='Desert Rd')),
]


def write_osm(path):
    w = osmium.SimpleWriter(path)
    for nid, (lon, lat) in NODES.items():
        tags = {}
        if nid == 90:
            tags = dict(amenity='fuel', brand='Chevron', name='Chevron')
        if nid == 91:
            tags = dict(amenity='bench')
        w.add_node(osmium.osm.mutable.Node(id=nid, location=(lon, lat), tags=tags))
    for wid, nds, tags in WAYS:
        w.add_way(osmium.osm.mutable.Way(id=wid, nodes=nds, tags=tags))
    # valid: both ways end at the via node
    w.add_relation(osmium.osm.mutable.Relation(
        id=500, members=[('w', 105, 'from'), ('n', 4, 'via'), ('w', 100, 'to')],
        tags=dict(type='restriction', restriction='no_left_turn')))
    # ambiguous: way 100 passes through the via node (invalid in OSM): dropped
    w.add_relation(osmium.osm.mutable.Relation(
        id=501, members=[('w', 100, 'from'), ('n', 2, 'via'), ('w', 101, 'to')],
        tags=dict(type='restriction', restriction='no_left_turn')))
    w.close()


class RoadModel(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cls.path = os.path.join(cls.tmp, 'town.osm')
        write_osm(cls.path)
        cls.m, cls.S, cls.st = model.build(cls.path, 'default', log=lambda *a: None)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, True)

    def links_of(self, way):
        return np.flatnonzero(self.m['link_way'] == way)

    def node_osm(self, i):
        return int(self.m['node_osm'][i])

    def test_split_at_junctions(self):
        # Main St 1-2-3-4: junctions at 2 (First Ave) and 3 (Back St): 3 links
        ls = self.links_of(100)
        self.assertEqual(len(ls), 3)
        ends = [(self.node_osm(self.m['link_frm'][i]), self.node_osm(self.m['link_to'][i])) for i in ls]
        self.assertEqual(ends, [(1, 2), (2, 3), (3, 4)])

    def test_shape_points_and_length(self):
        (i,) = self.links_of(101)          # 2 -> 5 -> 6, node 5 is a shape point
        self.assertEqual(self.m['link_shape_count'][i], 1)
        s0 = self.m['link_shape_start'][i]
        x, y = self.m['shape_x'][s0], self.m['shape_y'][s0]
        self.assertEqual((int(x), int(y)), geo.from_lonlat(*NODES[5]))
        want = geo.haversine_m(*NODES[2], *NODES[5]) + geo.haversine_m(*NODES[5], *NODES[6])
        self.assertAlmostEqual(int(self.m['link_length_m'][i]), want, delta=1)

    def test_oneway_reverse(self):
        (i,) = self.links_of(102)          # oneway=-1 on 7 -> 3: stored 3 -> 7, one-way
        self.assertEqual((self.node_osm(self.m['link_frm'][i]), self.node_osm(self.m['link_to'][i])), (3, 7))
        self.assertEqual(self.m['link_dirn'][i], 1)

    def test_classes_and_forms(self):
        (mw,) = self.links_of(103)[:1]
        self.assertEqual((self.m['link_cls'][mw], self.m['link_form'][mw], self.m['link_spd'][mw]), (0, 0, 31))
        self.assertEqual(self.m['link_dirn'][mw], 1)          # motorway implies one-way
        ramp = self.links_of(104)
        self.assertTrue((self.m['link_cls'][ramp] == 3).all())
        self.assertTrue((self.m['link_form'][ramp] == 1).all())   # on-slip: ends at the motorway
        res = self.links_of(100)
        self.assertTrue((self.m['link_cls'][res] == 4).all() and (self.m['link_form'][res] == 0xC).all())

    def test_roundabout_loop_split(self):
        ls = self.links_of(106)
        self.assertGreaterEqual(len(ls), 2)                   # a closed way is never a self-loop
        self.assertTrue((self.m['link_frm'][ls] != self.m['link_to'][ls]).all())
        self.assertTrue((self.m['link_jn'][ls] == 6).all() and (self.m['link_dirn'][ls] == 1).all())

    def test_profile_drops(self):
        self.assertEqual(len(self.links_of(107)), 0)          # driveway
        self.assertEqual(len(self.links_of(108)), 0)          # unnamed track
        self.assertEqual(len(self.links_of(109)), 1)          # named track kept, class 6/1
        (i,) = self.links_of(109)
        self.assertEqual((self.m['link_cls'][i], self.m['link_sub'][i]), (6, 1))
        self.assertIn('highway=service with service=driveway access=-', self.st['dropped'])

    def test_restriction(self):
        self.assertEqual(len(self.m['tr_from_link']), 1)
        f, v, t = self.m['tr_from_link'][0], self.m['tr_via_node'][0], self.m['tr_to_link'][0]
        self.assertEqual(self.m['link_way'][f], 105)
        self.assertEqual(self.m['link_way'][t], 100)
        self.assertEqual(self.node_osm(v), 4)
        self.assertEqual(self.node_osm(self.m['link_frm'][t]), 3)      # the Main St link 3 -> 4
        self.assertFalse(self.m['tr_only'][0])
        self.assertEqual(self.st['dropped'].get('restriction whose ways are not roads here (or ambiguous)'), 1)

    def test_pois(self):
        self.assertEqual(self.st['pois'], 1)
        self.assertEqual(self.S[self.m['poi_cat'][0]], 'petrol station')
        self.assertEqual(self.S[self.m['poi_brand'][0]], 'chevron')
        self.assertEqual(self.st['dropped'].get('POI tag not used: amenity=bench'), 1)

    def test_names_lowercase(self):
        (i,) = self.links_of(101)
        self.assertEqual(self.S[self.m['link_name'][i]], 'first ave')
        (mw,) = self.links_of(103)[:1]
        self.assertEqual(self.S[self.m['link_ref'][mw]], 'i 15')

    def test_save_load(self):
        pref = os.path.join(self.tmp, 'model')
        model.save(self.m, self.S, self.st, pref)
        m2, s2, st2 = model.load(pref)
        self.assertEqual(s2, self.S)
        self.assertTrue(all(np.array_equal(self.m[k], m2[k]) for k in self.m))


class Units(unittest.TestCase):
    def test_coordinates(self):
        for lon, lat in [(-115.17, 36.11), (7.42, 43.73), (-179.9, -45), (179.9, 60), (-30, 0)]:
            x, y = geo.from_lonlat(lon, lat)
            self.assertTrue(0 <= x < 2 ** 31)
            lo, la = geo.to_lonlat(x, y)
            self.assertAlmostEqual(lo, lon, places=6)
            self.assertAlmostEqual(la, lat, places=6)
        # same as the disc reader's US convention
        sys.path.insert(0, os.path.join(ROOT, 'tools'))
        import carin
        x, y = geo.from_lonlat(-118.25, 34.05)
        self.assertAlmostEqual(carin.to_lonlat(x, y)[0], -118.25, places=6)

    def test_speed_categories(self):
        self.assertEqual([attrs.speed_category(k) for k in (32, 48, 64, 80, 113)], [5, 11, 16, 22, 31])

    def test_poi_category(self):
        self.assertEqual(poi.category('amenity', 'fuel', False), 'petrol station')
        self.assertIsNone(poi.category('amenity', 'restaurant', False))     # unnamed restaurant
        self.assertIsNone(poi.category('amenity', 'bench', True))

    def test_estimate_and_capacity(self):
        cal = estimate.calibration()
        e = estimate.from_model(1_000_000, 5_000_000, 50_000, cal)
        want = (1e6 * cal['bytes_per_link'] + 5e6 * cal['bytes_per_shape_point'] + 5e4 * cal['bytes_per_poi']
                + cal['fixed_bytes']) * estimate.SAFETY
        self.assertAlmostEqual(e['carindb_bytes'], want, delta=1)
        cap = estimate.capacity(333_000 * 2048 - estimate.OVERHEAD_SECTORS * 2048)
        self.assertEqual(cap['total_sectors'], 333_000)
        self.assertTrue(cap['fits']['74 min'])
        self.assertFalse(estimate.capacity(400_000 * 2048)['fits']['80 min'])
        self.assertTrue(any('too big' in a for a in estimate.advice(estimate.capacity(400_000 * 2048), e, 'default')))

    def test_region_resolve(self):
        idx = {'us/nevada': {'id': 'us/nevada', 'name': 'us/nevada', 'iso3166-2': ['US-NV']},
               'germany': {'id': 'germany', 'name': 'Germany', 'iso3166-1:alpha2': ['DE']},
               'us/new-york': {'id': 'us/new-york', 'name': 'us/new-york', 'iso3166-2': ['US-NY']}}
        self.assertEqual(regions.resolve(idx, 'US-NV')['id'], 'us/nevada')
        self.assertEqual(regions.resolve(idx, 'nevada')['id'], 'us/nevada')
        self.assertEqual(regions.resolve(idx, 'New York')['id'], 'us/new-york')
        self.assertEqual(regions.resolve(idx, 'de')['id'], 'germany')
        with self.assertRaises(KeyError):
            regions.resolve(idx, 'us')


if __name__ == '__main__':
    unittest.main()
