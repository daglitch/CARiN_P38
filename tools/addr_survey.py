"""Phase 3d survey: how much house-number data an OSM extract has.

Counts, for the routable named streets of a .osm.pbf:
  - ways carrying a TIGER line ID (tiger:tlid), which ADDRFEAT address ranges are keyed by;
  - OSM addresses (addr:housenumber + addr:street) on nodes and ways, and interpolation lines;
  - named streets (name, rounded to a ~5 km cell) that have at least one address of that name nearby.

    py tools/addr_survey.py out/cache/us__nevada-latest.osm.pbf
"""
import sys, re, collections
import osmium

ROADS = {"motorway", "trunk", "primary", "secondary", "tertiary", "unclassified", "residential",
         "living_street", "service", "motorway_link", "trunk_link", "primary_link", "secondary_link",
         "tertiary_link", "road"}
CELL = 0.05  # degrees, ~5 km


def norm(s):
    return re.sub(r"\s+", " ", s.lower()).strip()


class Survey(osmium.SimpleHandler):
    def __init__(self):
        super().__init__()
        self.c = collections.Counter()
        self.streets = set()          # (name, cx, cy)
        self.addr = set()             # (street, cx, cy)
        self.tlid_km = 0.0
        self.km = 0.0

    def _addr(self, tags, lon, lat):
        st = tags.get("addr:street")
        if st and lon is not None:
            self.addr.add((norm(st), int(lon / CELL), int(lat / CELL)))

    def node(self, n):
        if "addr:housenumber" in n.tags:
            self.c["addr nodes"] += 1
            if "addr:street" in n.tags:
                self.c["addr nodes with street"] += 1
                self._addr(n.tags, n.location.lon, n.location.lat)

    def way(self, w):
        t = w.tags
        if "addr:interpolation" in t:
            self.c["interpolation lines"] += 1
        if "addr:housenumber" in t:
            self.c["addr ways"] += 1
            if "addr:street" in t:
                self.c["addr ways with street"] += 1
                try:
                    nd = w.nodes[0]
                    self._addr(t, nd.location.lon, nd.location.lat)
                except Exception:
                    pass
        hw = t.get("highway")
        if hw not in ROADS or "name" not in t or hw == "service":
            return
        self.c["named street ways"] += 1
        try:
            ln = osmium.geom.haversine_distance(w.nodes) / 1000
            nd = w.nodes[len(w.nodes) // 2]
            self.streets.add((norm(t["name"]), int(nd.location.lon / CELL), int(nd.location.lat / CELL)))
        except Exception:
            ln = 0
        self.km += ln
        if "tiger:tlid" in t:
            self.c["named street ways with tiger:tlid"] += 1
            self.tlid_km += ln
        if "tiger:reviewed" in t and t["tiger:reviewed"] == "no":
            self.c["tiger:reviewed=no"] += 1


def main(path):
    s = Survey()
    s.apply_file(path, locations=True, idx="flex_mem")
    for k, v in sorted(s.c.items()):
        print(f"{k:40s} {v:12,d}")
    print(f"{'named street km':40s} {s.km:12,.0f}")
    print(f"{'  of which with tiger:tlid':40s} {s.tlid_km:12,.0f}  ({100 * s.tlid_km / max(s.km, 1):.1f}%)")
    near = 0
    for (nm, cx, cy) in s.streets:
        if any((nm, cx + dx, cy + dy) in s.addr for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
            near += 1
    print(f"{'named streets (name, 5 km cell)':40s} {len(s.streets):12,d}")
    print(f"{'  with an OSM address of that name':40s} {near:12,d}  ({100 * near / max(len(s.streets), 1):.1f}%)")


if __name__ == "__main__":
    main(sys.argv[1])
