"""OSM .osm.pbf -> routable road model + POI list (Phase 1).

Three passes with pyosmium (filters run in C++, so only needed objects reach Python):
  A  ways with a `highway` tag (+ POI areas) and turn-restriction relations;
  B  coordinates of exactly the nodes those ways use (IdFilter);
  C  POI nodes (KeyFilter on the POI keys).
Ways are then split at junctions (nodes shared by two or more road ways, way
ends, and the middle of loops) into links, vectorised with numpy.

Every link carries the CARiN street-record attributes (OpenCarin
docs/carindb/03-road-network.md section 6.7) chosen from OSM tags:
road class / class-6 subtype (+0x10), form of way and direction (+0x0B),
speed category (+0x0A), junction type (+0x11), toll, bridge, tunnel,
length (m) and the two bearings (+0x0E/+0x0F). The mapping tables are in
`attrs.py`. Output: one .npz (arrays) + one .json (strings, stats).
"""
import array
import json
import os
import time

import numpy as np
import osmium

from . import attrs, geo, poi

POI_KEYS = ('amenity', 'shop', 'tourism', 'aeroway', 'railway', 'leisure', 'historic',
            'office', 'craft', 'healthcare', 'public_transport')


class Strings:
    """Interned string table; id 0 = empty."""

    def __init__(self):
        self.ids = {'': 0}
        self.items = ['']

    def __call__(self, s):
        if not s:
            return 0
        i = self.ids.get(s)
        if i is None:
            i = self.ids[s] = len(self.items)
            self.items.append(s)
        return i


def _poi_tags(tags):
    """-> (key, value, category) of the first navigation-POI tag, or None.
    A tagged object that is no navigation POI returns ('', 'k=v', None)."""
    named = bool(tags.get('name') or tags.get('brand'))
    first = None
    for k in POI_KEYS + ('highway',):
        v = tags.get(k)
        if not v or v == 'no':
            continue
        c = poi.category(k, v, named, tags.get('information'))
        if c:
            return k, v, c
        if first is None and k != 'highway':
            first = ('', f'{k}={v}', None)
    return first


def build(pbf, profile='default', log=print):
    t0 = time.time()
    hw_table = attrs.highway_table(profile)
    attrs.set_profile(profile)
    S = Strings()
    stats = dict(pbf=os.path.abspath(pbf), pbf_bytes=os.path.getsize(pbf), profile=profile,
                 dropped={})
    drop = stats['dropped']

    def dropped(reason, n=1):
        drop[reason] = drop.get(reason, 0) + n

    # ------------------------------------------------------------ pass A: ways + relations
    refs = array.array('q')
    way_start = array.array('q')
    way_id = array.array('q')
    w_attr = {k: array.array('B') for k in ('hw', 'cls', 'sub', 'dirn', 'rev', 'jn', 'flags', 'spd')}
    w_name = array.array('i')
    w_ref = array.array('i')
    poi_way_refs, poi_way_meta = [], []
    restrictions = []
    n_hw_seen = 0
    fp = osmium.FileProcessor(pbf, osmium.osm.WAY | osmium.osm.RELATION) \
        .with_filter(osmium.filter.KeyFilter('highway', 'type', *POI_KEYS))
    for o in fp:
        tags = o.tags
        if o.is_relation():
            if tags.get('type') == 'restriction':
                r = tags.get('restriction') or tags.get('restriction:motorcar')
                if not r:
                    dropped('restriction without restriction=* tag')
                    continue
                fr = to = via = None
                via_way = False
                for m in o.members:
                    if m.role == 'from' and m.type == 'w':
                        fr = m.ref
                    elif m.role == 'to' and m.type == 'w':
                        to = m.ref
                    elif m.role == 'via':
                        via, via_way = m.ref, m.type == 'w'
                if fr is None or to is None or via is None:
                    dropped('restriction with missing members')
                elif via_way:
                    dropped('restriction via a way (not supported yet)')
                else:
                    restrictions.append((o.id, fr, via, to, r))
            continue
        hw = tags.get('highway')
        if hw:
            n_hw_seen += 1
            spec = hw_table.get(hw)
            if spec is None:
                dropped(f'highway={hw} (not a road for this profile)')
            else:
                a = attrs.way_attrs(hw, spec, tags)
                if a is None:
                    dropped(f'highway={hw} with {attrs.last_skip_reason}')
                else:
                    nodes = [n.ref for n in o.nodes]
                    if len(nodes) < 2:
                        dropped('way with fewer than 2 nodes')
                    else:
                        if a['rev']:
                            nodes.reverse()
                        way_start.append(len(refs))
                        refs.extend(nodes)
                        way_id.append(o.id)
                        for k in w_attr:
                            w_attr[k].append(a[k])
                        w_name.append(S(a['name']))
                        w_ref.append(S(a['ref']))
                    continue
        pt = _poi_tags(tags)
        if pt and pt[2] is None:
            dropped(f'POI tag not used: {pt[1]}' if pt[1].split('=')[0] in ('amenity', 'shop', 'tourism')
                    else 'other tagged object (not a navigation POI)')
        elif pt and len(o.nodes) >= 3:
            poi_way_refs.append(np.array([n.ref for n in o.nodes], np.int64))
            poi_way_meta.append(_poi_record(S, o.id, 'w', pt, tags))
    way_start.append(len(refs))
    refs = np.frombuffer(refs, np.int64)
    way_start = np.frombuffer(way_start, np.int64)
    n_ways = len(way_id)
    log(f'pass A: {n_ways:,} road ways ({n_hw_seen:,} highway ways seen), '
        f'{len(refs):,} node refs, {len(restrictions):,} restrictions, '
        f'{len(poi_way_meta):,} POI areas  [{time.time() - t0:.0f}s]')

    # ------------------------------------------------------------ pass B: node coordinates
    need = np.unique(np.concatenate([refs] + poi_way_refs)) if poi_way_refs else np.unique(refs)
    ids = array.array('q')
    xs = array.array('i')
    ys = array.array('i')
    fp = osmium.FileProcessor(pbf, osmium.osm.NODE).with_filter(osmium.filter.IdFilter(need.tolist()))
    for n in fp:
        loc = n.location
        if loc.valid():
            x, y = geo.from_lonlat(loc.lon, loc.lat)
            ids.append(n.id); xs.append(x); ys.append(y)
    ids = np.frombuffer(ids, np.int64)
    order = np.argsort(ids, kind='stable')
    ids, X, Y = ids[order], np.frombuffer(xs, np.int32)[order], np.frombuffer(ys, np.int32)[order]
    log(f'pass B: {len(ids):,} / {len(need):,} node locations  [{time.time() - t0:.0f}s]')

    # ------------------------------------------------------------ pass C: POI nodes
    pois = []
    fp = osmium.FileProcessor(pbf, osmium.osm.NODE).with_filter(osmium.filter.KeyFilter(*POI_KEYS))
    for n in fp:
        pt = _poi_tags(n.tags)
        if pt and pt[2] is None:
            dropped(f'POI tag not used: {pt[1]}' if pt[1].split('=')[0] in ('amenity', 'shop', 'tourism')
                    else 'other tagged object (not a navigation POI)')
        elif pt and n.location.valid():
            rec = _poi_record(S, n.id, 'n', pt, n.tags)
            rec['x'], rec['y'] = geo.from_lonlat(n.location.lon, n.location.lat)
            pois.append(rec)
    for wrefs, rec in zip(poi_way_refs, poi_way_meta):
        k = np.minimum(np.searchsorted(ids, wrefs), len(ids) - 1)
        k = k[ids[k] == wrefs] if len(ids) else k[:0]
        if not len(k):
            dropped('POI area without node locations')
            continue
        rec['x'], rec['y'] = int(X[k].astype(np.int64).mean()), int(Y[k].astype(np.int64).mean())
        pois.append(rec)
    log(f'pass C: {len(pois):,} POIs  [{time.time() - t0:.0f}s]')

    # ------------------------------------------------------------ split ways into links
    ci = np.searchsorted(ids, refs)
    ci[ci >= len(ids)] = 0
    present = ids[ci] == refs
    way_of = np.repeat(np.arange(n_ways), np.diff(way_start))
    bad_way = np.zeros(n_ways, bool)
    bad_way[way_of[~present]] = True
    if bad_way.any():
        dropped('way with nodes missing from the extract', int(bad_way.sum()))
    keep = ~bad_way[way_of]

    uniq, inv, cnt = np.unique(refs[keep], return_inverse=True, return_counts=True)
    shared = np.zeros(len(refs), bool)
    shared[np.flatnonzero(keep)] = cnt[inv] > 1
    is_end = np.zeros(len(refs), bool)
    is_end[way_start[:-1]] = True
    is_end[way_start[1:] - 1] = True
    split = (shared | is_end) & keep

    for _ in range(3):                         # split loops (from == to) in the middle
        P = np.flatnonzero(split)
        same_way = way_of[P[:-1]] == way_of[P[1:]]
        a, b = P[:-1][same_way], P[1:][same_way]
        loop = refs[a] == refs[b]
        if not loop.any():
            break
        mid = (a[loop] + b[loop]) // 2
        mid = mid[(mid > a[loop]) & (mid < b[loop])]
        if not len(mid):
            dropped('degenerate loop link', int(loop.sum()))
            break
        split[mid] = True
    P = np.flatnonzero(split)
    same_way = way_of[P[:-1]] == way_of[P[1:]]
    a, b = P[:-1][same_way], P[1:][same_way]
    ok = refs[a] != refs[b]
    if (~ok).sum():
        dropped('zero-length or loop link', int((~ok).sum()))
    a, b = a[ok], b[ok]
    lw = way_of[a]
    n_links = len(a)

    # graph nodes
    gid, gnode = np.unique(np.concatenate([refs[a], refs[b]]), return_inverse=True)
    frm, to = gnode[:n_links], gnode[n_links:]
    gci = np.searchsorted(ids, gid)
    NX, NY = X[gci], Y[gci]

    # shape points: positions a+1 .. b-1
    ns = b - a - 1
    shp_start = np.concatenate([[0], np.cumsum(ns)[:-1]]).astype(np.int64)
    pos = np.repeat(a + 1 - shp_start, ns) + np.arange(ns.sum())
    SX, SY = X[ci[pos]], Y[ci[pos]]

    # lengths (vectorised haversine over every consecutive pair of the way)
    lon_all, lat_all = geo.to_lonlat(X[ci].astype(np.float64), Y[ci].astype(np.float64))
    seg = _haversine(lon_all[:-1], lat_all[:-1], lon_all[1:], lat_all[1:])
    cum = np.concatenate([[0.0], np.cumsum(seg)])
    length = cum[b] - cum[a]

    # bearings: start node -> next point, end node -> previous point
    nxt, prv = a + 1, b - 1
    brg0 = _bearing256(lon_all[a], lat_all[a], lon_all[nxt], lat_all[nxt])
    brg1 = _bearing256(lon_all[b], lat_all[b], lon_all[prv], lat_all[prv])

    L = {k: np.frombuffer(v, np.uint8)[lw] for k, v in w_attr.items()}
    links = dict(frm=frm.astype(np.int32), to=to.astype(np.int32),
                 shape_start=shp_start, shape_count=ns.astype(np.int32),
                 length_m=np.round(length).astype(np.int32), brg0=brg0, brg1=brg1,
                 way=np.frombuffer(way_id, np.int64)[lw],
                 name=np.frombuffer(w_name, np.int32)[lw], ref=np.frombuffer(w_ref, np.int32)[lw],
                 **L)
    links['form'] = attrs.form_of_way(links, n_nodes=len(gid))
    log(f'links: {n_links:,}, graph nodes: {len(gid):,}, shape points: {len(SX):,}  '
        f'[{time.time() - t0:.0f}s]')

    # ------------------------------------------------------------ turn restrictions
    tr = _map_restrictions(restrictions, links, gid, dropped)
    log(f'restrictions mapped: {len(tr["from_link"]):,} / {len(restrictions):,}')

    model = dict(node_x=NX, node_y=NY, node_osm=gid, shape_x=SX, shape_y=SY,
                 **{'link_' + k: v for k, v in links.items()},
                 **{'tr_' + k: v for k, v in tr.items()},
                 **_poi_arrays(pois))
    model['poi_code'] = np.array([poi.CODE_OF[S.items[p['cat']]] for p in pois], np.uint8)
    stats.update(_stats(links, pois, S, len(gid), len(SX), n_ways, len(restrictions), tr))
    stats['seconds'] = round(time.time() - t0, 1)
    return model, S.items, stats


def _haversine(lon1, lat1, lon2, lat2):
    p1, p2 = np.radians(lat1), np.radians(lat2)
    dp, dl = p2 - p1, np.radians(lon2 - lon1)
    h = np.sin(dp / 2) ** 2 + np.cos(p1) * np.cos(p2) * np.sin(dl / 2) ** 2
    return 2 * geo.EARTH_R * np.arcsin(np.minimum(1.0, np.sqrt(h)))


def _bearing256(lon1, lat1, lon2, lat2):
    p1, p2 = np.radians(lat1), np.radians(lat2)
    dl = np.radians(lon2 - lon1)
    y = np.sin(dl) * np.cos(p2)
    x = np.cos(p1) * np.sin(p2) - np.sin(p1) * np.cos(p2) * np.cos(dl)
    return (np.round(np.degrees(np.arctan2(y, x)) % 360 * 256 / 360).astype(np.int64) & 0xFF).astype(np.uint8)


def _poi_record(S, oid, kind, pt, tags):
    g = tags.get
    return dict(osm=oid, kind=kind, key=S(pt[0]), value=S(pt[1]), cat=S(pt[2]),
                name=S((g('name') or '').lower()), brand=S((g('brand') or '').lower()),
                phone=S(g('phone') or g('contact:phone') or ''),
                housenumber=S(g('addr:housenumber') or ''), street=S((g('addr:street') or '').lower()),
                city=S((g('addr:city') or '').lower()), postcode=S(g('addr:postcode') or ''))


POI_FIELDS = ('osm', 'x', 'y', 'key', 'value', 'cat', 'name', 'brand', 'phone', 'housenumber', 'street',
              'city', 'postcode')


def _poi_arrays(pois):
    out = {}
    for f in POI_FIELDS:
        dt = np.int64 if f == 'osm' else np.int32
        out['poi_' + f] = np.array([p[f] for p in pois], dt) if pois else np.zeros(0, dt)
    out['poi_is_way'] = np.array([p['kind'] == 'w' for p in pois], bool)
    return out


def _map_restrictions(restrictions, links, gid, dropped):
    """(from way, via node, to way) -> (from link ending at via, to link starting at via)."""
    out = dict(from_link=[], via_node=[], to_link=[], only=[], osm=[])
    if not restrictions:
        return {k: np.zeros(0, np.int64) for k in out}
    ways = links['way']
    order = np.argsort(ways, kind='stable')
    sw = ways[order]
    for rid, fw, via, tw, kind in restrictions:
        vi = np.searchsorted(gid, via)
        if vi >= len(gid) or gid[vi] != via:
            dropped('restriction whose via node is not a junction')
            continue

        def touching(w):
            lo, hi = np.searchsorted(sw, w), np.searchsorted(sw, w, 'right')
            return [int(i) for i in order[lo:hi] if links['frm'][i] == vi or links['to'][i] == vi]

        f, t = touching(fw), touching(tw)
        if len(f) != 1 or len(t) != 1:
            dropped('restriction whose ways are not roads here (or ambiguous)')
            continue
        out['from_link'].append(f[0]); out['via_node'].append(int(vi)); out['to_link'].append(t[0])
        out['only'].append(kind.startswith('only_')); out['osm'].append(rid)
    return {k: np.array(v, np.int64) for k, v in out.items()}


def _stats(links, pois, S, n_nodes, n_shape, n_ways, n_restr, tr):
    cls = links['cls']
    km = links['length_m'].astype(np.int64)
    by_cls = {str(c): dict(links=int((cls == c).sum()), km=round(int(km[cls == c].sum()) / 1000, 1))
              for c in range(7) if (cls == c).any()}
    hw_names = attrs.HIGHWAY_CODES
    by_hw = {}
    for code in np.unique(links['hw']):
        m = links['hw'] == code
        by_hw[hw_names[code]] = dict(links=int(m.sum()), km=round(int(km[m].sum()) / 1000, 1))
    poi_cat = {}
    for p in pois:
        k = S.items[p['cat']]
        poi_cat[k] = poi_cat.get(k, 0) + 1
    return dict(road_ways=n_ways, links=len(cls), graph_nodes=n_nodes, shape_points=n_shape,
                road_km=round(int(km.sum()) / 1000, 1), links_by_class=by_cls, links_by_highway=by_hw,
                oneway_links=int((links['dirn'] == 1).sum()), closed_links=int((links['dirn'] == 3).sum()),
                named_links=int((links['name'] > 0).sum()),
                restrictions=dict(in_osm=n_restr, mapped=len(tr['from_link'])),
                pois=len(pois), pois_named=sum(1 for p in pois if p['name']),
                poi_categories=dict(sorted(poi_cat.items(), key=lambda kv: -kv[1])),
                long_links_over_65535m=int((links['length_m'] > 65535).sum()))


def save(model, strings, stats, out_prefix):
    os.makedirs(os.path.dirname(os.path.abspath(out_prefix)), exist_ok=True)
    np.savez_compressed(out_prefix + '.npz', **model)
    with open(out_prefix + '.json', 'w', encoding='utf-8') as f:
        json.dump(dict(stats=stats, strings=strings), f, ensure_ascii=False)


def load(out_prefix):
    with np.load(out_prefix + '.npz') as z:
        model = {k: z[k] for k in z.files}
    with open(out_prefix + '.json', encoding='utf-8') as f:
        meta = json.load(f)
    return model, meta['strings'], meta['stats']
