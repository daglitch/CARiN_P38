"""Road model -> CARiN road tiles: street level 0x00 and coarse levels 0x03 / 0x02 / 0x01 (Phase 2).

Tiling, as on the original disc (carin-disc-findings.md "Phase 2"):
  * every coordinate sits on the 64-unit grid of the parcel-local frame;
  * tiles are cells of one tree of alternate halves: a square w x w splits into two
    w x w/2 halves (bottom, top), a w x w/2 half into two w/2 squares (left, right);
    sides are 40960 * 2^k units, corners on the disc's grid origin (16384, 1280) mod 40960;
  * street tiles: 81920 x 40960 up to 1310720 square, split until the estimated decoded
    size fits STREET_BUDGET; coarse tiles: unions of street tiles up to 2621440 square;
  * a link that crosses a tile edge is cut there; the halves end on twin edge nodes
    (section 6) at the same absolute position, one in each tile.
Levels: 0x03 keeps road classes <= 3, 0x02 <= 2, 0x01 <= 1 (the disc's Nevada data).
A coarse link is a chain of street links merged through nodes where exactly two kept
roads meet and class, speed, form and direction stay the same; coarse tile edges end
a chain, and coarse tiles are unions of street tiles, so every coarse node lies on a
street node. Coarse shapes are simplified (Douglas-Peucker, SIMPLIFY_M).

`build()` returns tiles with symbolic cross references; `link.py` assigns block IDs
and fills them in (parents, section 8 grids, section 9 neighbours, edge-node twins).
"""
import math
import re
from collections import defaultdict

import numpy as np

from . import geo, tile26
from .tile26 import Ref, Text

GRID = 64
BASE = 40960
ORIGIN = (16384, 1280)
STREET_MAX = 1310720
COARSE_MAX = 2621440
STREET_MIN_W = 81920
LEVEL_MAX_CLASS = {3: 3, 2: 2, 1: 1}
SIMPLIFY_M = {3: 20.0, 2: 60.0, 1: 150.0}
STREET_BUDGET = 9 * 1024
COARSE_BUDGET = 5 * 1024
S8_MAX = 256
TILE_LIMIT = 8 * 2048 - 256              # decoded bytes (the disc's largest street tile: 8 units)
STREET_SIMPLIFY_M = 2.0                   # street shapes (OSM has 2.4x the shape points of the 2002 data)
S6_LIMIT = 1 << 14                        # edge-node twin offsets are 14-bit at sub-revision 8
FIT_SIMPLIFY_M = (5.0, 10.0)
NO_COST = 2147483                         # header u32s of coarse tiles on the disc
STREET_HDR_6C = 14                        # unknown header u16 of street tiles (5..17 on the disc)

COUNTRY_S14 = [('united states', 2, 0), ('estados unidos', 6, 2), ('estados unidos', 0x0F, 2),
               ('etats unis', 3, 2), ('vereinigte staaten', 4, 2), ('verenigde staten', 1, 2)]
COUNTRY_ID_US = 0xE0
ROUTE_NAME = re.compile(r'^(i|us|sr|ca|nv|hi|az|or|ut|id|wa|nm|tx)[ -]?\d|^\d')
ATTR_KEYS = ('cls', 'sub', 'form', 'dirn', 'spd', 'jn', 'flags', 'name', 'ref', 'hw')


# ======================================================================== the tree
class Cell:
    __slots__ = ('x1', 'y1', 'w', 'h', 'kids', 'leaf_id', 'twin')

    def __init__(self, x1, y1, w, h):
        self.x1, self.y1, self.w, self.h = x1, y1, w, h
        self.kids = None
        self.leaf_id = -1
        self.twin = None                  # the matching cell of the street tree

    x2 = property(lambda s: s.x1 + s.w)
    y2 = property(lambda s: s.y1 + s.h)

    def split(self):
        if self.h == self.w:
            h = self.h // 2
            self.kids = (Cell(self.x1, self.y1, self.w, h), Cell(self.x1, self.y1 + h, self.w, h))
        else:
            w = self.w // 2
            self.kids = (Cell(self.x1, self.y1, w, self.h), Cell(self.x1 + w, self.y1, w, self.h))
        return self.kids

    def side(self, x, y):
        k = self.kids
        return int(y >= k[1].y1) if k[0].w == self.w else int(x >= k[1].x1)

    def contains(self, x, y):
        return self.x1 <= x < self.x2 and self.y1 <= y < self.y2


def root_cell(xmin, ymin, xmax, ymax):
    """Smallest 40960 * 2^k square on the 40960 grid covering the box. On the disc only
    the 40960 grid is fixed: bigger tiles are not aligned to multiples of their size."""
    x0 = (xmin - ORIGIN[0]) // BASE * BASE + ORIGIN[0]
    y0 = (ymin - ORIGIN[1]) // BASE * BASE + ORIGIN[1]
    size = BASE
    while x0 + size <= xmax or y0 + size <= ymax:
        size *= 2
    return Cell(x0, y0, size, size)


def grow_street(cell, xs, ys, wts, leaves):
    tot = float(wts.sum())
    can = cell.w > STREET_MIN_W or cell.h == cell.w
    if (tot <= STREET_BUDGET and cell.w <= STREET_MAX) or not can:
        cell.leaf_id = len(leaves)
        leaves.append(cell)
        return
    a, b = cell.split()
    m = ys < b.y1 if a.w == cell.w else xs < b.x1
    grow_street(a, xs[m], ys[m], wts[m], leaves)
    grow_street(b, xs[~m], ys[~m], wts[~m], leaves)


def grow_coarse(scell, xs, ys, wts, leaves, budget, max_w):
    """Mirror the street tree `scell`, stopping where the content fits: every coarse
    leaf is a union of street leaves."""
    c = Cell(scell.x1, scell.y1, scell.w, scell.h)
    c.twin = scell
    if not scell.kids or (float(wts.sum()) <= budget and scell.w <= max_w):
        c.leaf_id = len(leaves)
        leaves.append(c)
        return c
    k0, k1 = scell.kids
    m = ys < k1.y1 if k0.w == scell.w else xs < k1.x1
    c.kids = (grow_coarse(k0, xs[m], ys[m], wts[m], leaves, budget, max_w),
              grow_coarse(k1, xs[~m], ys[~m], wts[~m], leaves, budget, max_w))
    return c


def find_leaf(root, x, y):
    c = root
    while c.kids:
        c = c.kids[c.side(x, y)]
    return c


# ======================================================================== geometry
def quant(v):
    return (np.asarray(v, np.int64) + GRID // 2) // GRID * GRID


def exit_point(cell, ax, ay, bx, by):
    """Where segment a -> b (a inside `cell`, b outside) leaves the cell: a grid point on
    the boundary, and a probe point just beyond it to find the next cell."""
    dx, dy = bx - ax, by - ay
    best = None
    for side, bound, d, a0 in (('x2', cell.x2, dx, ax), ('x1', cell.x1, dx, ax),
                               ('y2', cell.y2, dy, ay), ('y1', cell.y1, dy, ay)):
        if (side == 'x2' and d > 0) or (side == 'x1' and d < 0) or (side == 'y2' and d > 0) or (side == 'y1' and d < 0):
            t = (bound - a0) / d
            if 0 <= t <= 1 and (best is None or t < best[0]):
                best = (t, side)
    t, side = best
    if side in ('x1', 'x2'):
        x = cell.x2 if side == 'x2' else cell.x1
        y = min(max(int(round((ay + t * dy) / GRID)) * GRID, cell.y1), cell.y2)
        probe = (x - 1 if side == 'x1' else x, min(max(ay + t * dy, cell.y1), cell.y2 - 1))
    else:
        y = cell.y2 if side == 'y2' else cell.y1
        x = min(max(int(round((ax + t * dx) / GRID)) * GRID, cell.x1), cell.x2)
        probe = (min(max(ax + t * dx, cell.x1), cell.x2 - 1), y - 1 if side == 'y1' else y)
    return (x, y), probe


def poly_len_m(pts):
    if len(pts) < 2:
        return 0.0
    a = np.asarray(pts, np.float64)
    lon, lat = geo.to_lonlat(a[:, 0], a[:, 1])
    p1, p2 = np.radians(lat[:-1]), np.radians(lat[1:])
    dl = np.radians(lon[1:] - lon[:-1])
    h = np.sin((p2 - p1) / 2) ** 2 + np.cos(p1) * np.cos(p2) * np.sin(dl / 2) ** 2
    return float((2 * geo.EARTH_R * np.arcsin(np.minimum(1, np.sqrt(h)))).sum())


def leave_bearing(pts):
    """Bearing (1/256 turn) leaving pts[0] towards the first distinct point."""
    p = pts[0]
    for q in pts[1:]:
        if q != p:
            lo1, la1 = geo.to_lonlat(*p)
            lo2, la2 = geo.to_lonlat(*q)
            return geo.bearing256(lo1, la1, lo2, la2)
    return 0


def simplify(pts, tol_m):
    if len(pts) <= 2 or tol_m <= 0:
        return pts
    lat = pts[0][1] * geo.UNIT_DEG
    kx = geo.UNIT_DEG * 111320 * math.cos(math.radians(lat))
    ky = geo.UNIT_DEG * 110540
    keep = [False] * len(pts)
    keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    while stack:
        i, j = stack.pop()
        (x1, y1), (x2, y2) = pts[i], pts[j]
        ax, ay = (x2 - x1) * kx, (y2 - y1) * ky
        L = math.hypot(ax, ay)
        best, bk = -1.0, -1
        for k in range(i + 1, j):
            bx, by = (pts[k][0] - x1) * kx, (pts[k][1] - y1) * ky
            dist = abs(ax * by - ay * bx) / L if L else math.hypot(bx, by)
            if dist > best:
                best, bk = dist, k
        if best > tol_m:
            keep[bk] = True
            stack += [(i, bk), (bk, j)]
    return [p for p, k in zip(pts, keep) if k]


# ======================================================================== pieces
class Piece:
    """A link or merged chain inside one tile. Ends are ('n', node id) graph nodes or
    ('e', edge id) edge nodes; pts is the full grid polyline including both ends;
    dirn 1 means passable along pts."""
    __slots__ = ('tile', 'a', 'b', 'pts', 'attr', 'src', 'length', 'brg')

    def __init__(self, tile, a, b, pts, attr, src):
        self.tile, self.a, self.b, self.pts, self.attr, self.src = tile, a, b, pts, attr, src
        self.length = poly_len_m(pts)
        self.brg = (leave_bearing(pts), leave_bearing(pts[::-1]))


def cut(root, pts, a_key, b_key, attr, src, edges):
    """Cut a grid polyline at tile edges -> [Piece]. `edges` collects edge nodes as
    [leaf id, x, y, twin edge id]. Crossings are computed from the polyline's own
    vertices, so the street level and the coarse levels (whose chains run through the
    same vertices) cut a road at the same grid points."""
    out = []
    cur = find_leaf(root, *pts[0])
    run, start = [pts[0]], a_key
    for i in range(1, len(pts)):
        ax, ay = pts[i - 1]
        bx, by = pts[i]
        while not cur.contains(bx, by):
            (cx, cy), probe = exit_point(cur, ax, ay, bx, by)
            nxt = find_leaf(root, *probe)
            if nxt is cur:                       # probe rounded back inside: step to b's cell
                nxt = find_leaf(root, bx, by)
            e1, e2 = len(edges), len(edges) + 1
            edges.append([cur.leaf_id, cx, cy, e2])
            edges.append([nxt.leaf_id, cx, cy, e1])
            if run[-1] != (cx, cy):
                run.append((cx, cy))
            out.append(Piece(cur.leaf_id, start, ('e', e1), run, attr, src))
            start, run, cur = ('e', e2), [(cx, cy)], nxt
        if run[-1] != (bx, by):
            run.append((bx, by))
    if len(run) == 1:
        run.append(run[0])
    out.append(Piece(cur.leaf_id, start, b_key, run, attr, src))
    return out


def nudge(root, x, y):
    """Move a vertex off the lower edges of its tile (cells are half-open)."""
    c = find_leaf(root, x, y)
    return (x + GRID if x == c.x1 else x), (y + GRID if y == c.y1 else y)


# ======================================================================== chains (coarse)
def chains(m, keep):
    """Maximal runs of kept links between break nodes, oriented along one-way traffic.
    -> list of [(link, forward)]."""
    frm, to = m['link_frm'], m['link_to']
    idx = np.flatnonzero(keep)
    inc = defaultdict(list)
    for i in idx:
        inc[int(frm[i])].append(int(i))
        inc[int(to[i])].append(int(i))
    key = lambda i: (int(m['link_cls'][i]), int(m['link_spd'][i]), int(m['link_form'][i]),
                     int(m['link_dirn'][i]), int(m['link_jn'][i] == 6))

    def is_break(n):
        ls = inc[n]
        if len(ls) != 2:
            return True
        i, j = ls
        if key(i) != key(j):
            return True
        if key(i)[3] == 1:                # one-way: one link must enter n, the other leave it
            return not ((int(to[i]) == n) ^ (int(to[j]) == n))
        return False

    used = set()
    out = []

    def walk(n, i):
        seq = []
        start = n
        while True:
            used.add(i)
            f = int(frm[i]) == n
            seq.append((i, f))
            n = int(to[i]) if f else int(frm[i])
            if n == start or is_break(n):
                return seq
            i = [j for j in inc[n] if j != i][0]
            if i in used:
                return seq

    breaks = [n for n in inc if is_break(n)]
    for n in breaks:
        for i in inc[n]:
            if i not in used:
                out.append(walk(n, i))
    for i in idx:                          # closed loops without a break node
        i = int(i)
        if i not in used:
            out.append(walk(int(frm[i]), i))
    # a closed chain (start node == end node) is split in two
    split = []
    for seq in out:
        i0, f0 = seq[0]
        i1, f1 = seq[-1]
        a = int(frm[i0]) if f0 else int(to[i0])
        b = int(to[i1]) if f1 else int(frm[i1])
        if a == b and len(seq) >= 2:
            h = len(seq) // 2
            split += [seq[:h], seq[h:]]
        else:
            split.append(seq)
    out = split
    # orient one-way chains along their direction
    for k, seq in enumerate(out):
        i0, f0 = seq[0]
        if int(m['link_dirn'][i0]) == 1 and not f0:
            out[k] = [(i, not f) for i, f in reversed(seq)]
    return out


# ======================================================================== main entry
class RoadTiles:
    def __init__(self):
        self.levels = {}           # block type -> list of TileSpec
        self.cells = {}            # block type -> leaf cells
        self.roots = {}
        self.stats = {}


class TileSpec:
    """A tile before linking: the tile26.Tile plus the symbolic cross references."""

    def __init__(self, btype, cell, tile):
        self.btype, self.cell, self.tile = btype, cell, tile
        self.edge_twins = []       # per S6 record: (neighbour leaf id, twin edge id)
        self.edge_ids = []         # per S6 record: own edge id
        self.block_id = 0
        self.parent = None         # TileSpec one level up
        self.encoded = None


def build(model, strings, region_label='', log=print):
    m = model
    NX, NY = quant(m['node_x']), quant(m['node_y'])
    SX, SY = quant(m['shape_x']), quant(m['shape_y'])
    n_links = len(m['link_frm'])

    xs = np.concatenate([NX, SX]).astype(np.float64)
    ys = np.concatenate([NY, SY]).astype(np.float64)
    per_node = 8.0 + 36.0 * n_links / max(len(NX), 1)
    wts = np.concatenate([np.full(len(NX), per_node), np.full(len(SX), 6.0)])
    root = root_cell(int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1)
    leaves = []
    grow_street(root, xs, ys, wts, leaves)
    log(f'street tiles: {len(leaves):,} (root {root.w:,} units)')

    node_xy = [nudge(root, int(x), int(y)) for x, y in zip(NX, NY)]
    shape_xy = [nudge(root, int(x), int(y)) for x, y in zip(SX, SY)]

    def polyline(i, forward=True):
        s0, sc = int(m['link_shape_start'][i]), int(m['link_shape_count'][i])
        pts = [node_xy[int(m['link_frm'][i])]] + shape_xy[s0:s0 + sc] + [node_xy[int(m['link_to'][i])]]
        out = [pts[0]]
        for p in pts[1:]:
            if p != out[-1]:
                out.append(p)
        if len(out) == 1:
            out.append(out[0])
        return out if forward else out[::-1]

    attrs = [{k: int(m['link_' + k][i]) for k in ATTR_KEYS} for i in range(n_links)]

    # ---------------------------------------------------- street level
    edges, pieces = [], []
    for i in range(n_links):
        ps = cut(root, polyline(i), ('n', int(m['link_frm'][i])), ('n', int(m['link_to'][i])),
                 attrs[i], i, edges)
        for p in ps:
            p.pts = simplify(p.pts, STREET_SIMPLIFY_M)
        pieces += ps
    log(f'street pieces: {len(pieces):,} from {n_links:,} links, edge nodes {len(edges):,}')

    # ---------------------------------------------------- coarse levels
    out = RoadTiles()
    reach = defaultdict(int)
    coarse = {}
    for lv in (3, 2, 1):
        keep = m['link_cls'] <= LEVEL_MAX_CLASS[lv]
        ch = chains(m, keep)
        cpts, cw = [], []
        for seq in ch:
            for i, f in seq:
                cpts.append(node_xy[int(m['link_frm'][i])])
        cpts = np.array(cpts, np.float64).reshape(-1, 2)
        cleaves = []
        croot = grow_coarse(root, cpts[:, 0], cpts[:, 1], np.full(len(cpts), 40.0), cleaves,
                            COARSE_BUDGET, COARSE_MAX)
        cedges, cpieces = [], []
        for seq in ch:
            pts = []
            for i, f in seq:
                for p in polyline(i, f):
                    if not pts or p != pts[-1]:
                        pts.append(p)
            i0, f0 = seq[0]
            i1, f1 = seq[-1]
            a = int(m['link_frm'][i0]) if f0 else int(m['link_to'][i0])
            b = int(m['link_to'][i1]) if f1 else int(m['link_frm'][i1])
            for p in cut(croot, pts, ('n', a), ('n', b), attrs[i0], i0, cedges):
                full = p.pts
                p.pts = simplify(full, SIMPLIFY_M[lv])
                cpieces.append(p)
        for p in cpieces:
            for end in (p.pts[0], p.pts[-1]):
                reach[end] = max(reach[end], 4 - lv)
        coarse[lv] = (croot, cleaves, cpieces, cedges)
        log(f'level 0x0{lv}: {int(keep.sum()):,} street links -> {len(ch):,} chains -> '
            f'{len(cpieces):,} pieces in {len(cleaves):,} tiles')

    out.roots[0], out.cells[0] = root, leaves
    out.levels[0], fit0 = make_tiles(0, leaves, pieces, edges, reach, strings, region_label, m)
    fits = {'0x00': fit0}
    for lv in (3, 2, 1):
        croot, cleaves, cpieces, cedges = coarse[lv]
        out.roots[lv], out.cells[lv] = croot, cleaves
        out.levels[lv], fits[f'0x0{lv}'] = make_tiles(lv, cleaves, cpieces, cedges, reach, strings,
                                                      region_label, None)
    out.stats = dict(street_tiles=len(leaves), street_pieces=len(pieces), street_edge_nodes=len(edges),
                     coarse={f'0x0{lv}': dict(tiles=len(coarse[lv][1]), links=len(coarse[lv][2]),
                                              edge_nodes=len(coarse[lv][3])) for lv in (3, 2, 1)},
                     fit={k: v for k, v in fits.items() if v})
    return out


# ======================================================================== tile records
def _node_flags(sec, reach_lv, degree):
    hi = (3 - reach_lv) << 6
    if sec == 5:
        hi |= 0x10 | (1 if degree == 1 else 5 if degree == 2 else 0)
    else:
        hi |= 0x20 | (5 if degree == 1 else 0x10)
    return hi << 8


def _width(attr):
    one = attr['dirn'] in (1, 2)
    if attr['cls'] <= 1 and not one:
        return 3
    if one:
        return 2 if attr['cls'] <= 2 else 0
    return 1


def make_tiles(btype, leaves, pieces, edges, reach, strings, region_label, m):
    by_tile = defaultdict(list)
    for p in pieces:
        by_tile[p.tile].append(p)
    edges_by_tile = defaultdict(list)
    for eid, (leaf, x, y, twin) in enumerate(edges):
        edges_by_tile[leaf].append(eid)
    bans = _bans(m, pieces) if (btype == 0 and m is not None) else {}
    specs = []
    fit_log = defaultdict(int)
    for cell in leaves:
        specs.append(_fit_tile(btype, cell, by_tile[cell.leaf_id], edges, reach, strings, region_label, bans,
                               fit_log))
    return specs, dict(fit_log)


def _too_big(t):
    start, _ = t.layout()
    return t.decoded_size() > TILE_LIMIT or start[6] + 16 * len(t.secs[6]) > S6_LIMIT


def _fit_tile(btype, cell, ps, edges, reach, strings, region_label, bans, fit_log):
    """Build a tile; if it is too big (it is already as small as tiles get), simplify the
    street shapes, then drop the shortest class-6 then class-5 roads, until it fits."""
    spec = _one_tile(btype, cell, ps, edges, reach, strings, region_label, bans)
    if not _too_big(spec.tile):
        return spec
    for tol in FIT_SIMPLIFY_M:
        for p in ps:
            p.pts = simplify(p.pts, tol)
        spec = _one_tile(btype, cell, ps, edges, reach, strings, region_label, bans)
        fit_log[f'tiles simplified to {tol:g} m'] += 1
        if not _too_big(spec.tile):
            return spec
    for cls in (6, 5, 4):
        cand = sorted((p for p in ps if p.attr['cls'] == cls), key=lambda p: p.length)
        while cand and _too_big(spec.tile):
            drop = cand[:max(1, len(cand) // 10)]
            cand = cand[len(drop):]
            ids = set(map(id, drop))
            ps = [p for p in ps if id(p) not in ids]
            fit_log[f'class-{cls} pieces dropped (tile full)'] += len(drop)
            spec = _one_tile(btype, cell, ps, edges, reach, strings, region_label, bans)
        if not _too_big(spec.tile):
            return spec
    fit_log['tiles still too big'] += 1
    return spec


def _bans(m, pieces):
    """Turn restrictions -> {(from link, via node): [(to link, only?)]} for the street level."""
    out = defaultdict(list)
    for f, v, t, only in zip(m['tr_from_link'], m['tr_via_node'], m['tr_to_link'], m['tr_only']):
        out[(int(f), int(v))].append((int(t), bool(only)))
    return out


def _one_tile(btype, cell, ps, edges, reach, strings, region_label, bans):
    street = btype == 0
    t = tile26.Tile(btype)
    t.bbox = (cell.x1, cell.y1, cell.x2, cell.y2)
    spec = TileSpec(btype, cell, t)
    x1, y1 = cell.x1, cell.y1

    def loc(p):
        return ((p[0] - x1) // GRID, (p[1] - y1) // GRID)

    # orient: start node has the lower (u, v)
    links = []
    for p in ps:
        pts, a, b, attr, brg = p.pts, p.a, p.b, dict(p.attr), p.brg
        if loc(pts[0]) > loc(pts[-1]):
            pts, a, b, brg = pts[::-1], b, a, (brg[1], brg[0])
            if attr['dirn'] in (1, 2):
                attr['dirn'] = 3 - attr['dirn']
        links.append(dict(pts=pts, a=a, b=b, attr=attr, brg=brg, length=p.length, src=p.src))

    # nodes
    nodes = {}
    for k, L in enumerate(links):
        for end, key, pos in ((0, L['a'], L['pts'][0]), (1, L['b'], L['pts'][-1])):
            n = nodes.setdefault(key, dict(sec=6 if key[0] == 'e' else 5, pos=pos, links=[]))
            n['links'].append((k, end))
    for key, n in nodes.items():
        n['group'] = min(links[k]['attr']['cls'] for k, _ in n['links'])

    order = sorted(range(len(links)), key=lambda k: (links[k]['attr']['cls'], loc(links[k]['pts'][0]),
                                                     loc(links[k]['pts'][-1])))
    pos_of = {k: i for i, k in enumerate(order)}
    links = [links[k] for k in order]
    for n in nodes.values():
        n['links'] = [(pos_of[k], e) for k, e in n['links']]
    n5 = sorted((k for k, n in nodes.items() if n['sec'] == 5), key=lambda k: (nodes[k]['group'], loc(nodes[k]['pos'])))
    n6 = sorted((k for k, n in nodes.items() if n['sec'] == 6), key=lambda k: loc(nodes[k]['pos']))
    nref = {}
    for i, k in enumerate(n5):
        nref[k] = Ref(5, i)
    for i, k in enumerate(n6):
        nref[k] = Ref(6, i)

    # cycles: links at a node clockwise by leaving bearing, from the smallest
    nxt = {}                     # (link, end) -> next link at that end
    first = {}
    for key, n in nodes.items():
        cyc = sorted(n['links'], key=lambda le: links[le[0]]['brg'][le[1]])
        first[key] = cyc[0][0]
        for j, le in enumerate(cyc):
            if len(cyc) == 1:
                nxt[le] = le[0] if n['sec'] == 5 else None
            else:
                nxt[le] = cyc[(j + 1) % len(cyc)][0]

    # names (street level)
    s2_of = {}
    if street:
        _names(t, links, strings, region_label, s2_of)
    else:
        for s in (0, 1, 2):
            t.secs[s] = [[bytearray(10), {}] for _ in range(2)]
        t.zero_offset.update({13, 14})
    t.zero_offset.add(13)

    # turn bans (S10), per owner link
    s10 = []
    s10_start = []
    if street and bans:
        at_via = defaultdict(list)
        for k, L in enumerate(links):
            for end, key in ((0, L['a']), (1, L['b'])):
                if key[0] == 'n':
                    at_via[key[1]].append((k, end))
        for k, L in enumerate(links):
            s10_start.append(len(s10))
            for end, key in ((0, L['a']), (1, L['b'])):
                if key[0] != 'n':
                    continue
                for to_link, only in bans.get((L['src'], key[1]), ()):
                    for k2, end2 in at_via[key[1]]:
                        hit = links[k2]['src'] == to_link
                        if k2 != k and (hit != only):
                            s10.append((k2, end))
    else:
        s10_start = [0] * len(links)
    t.secs[10] = [[bytearray(8), {4: Ref(4, k2), 'own': True}] for k2, end in s10]
    for rec, (k2, end) in zip(t.secs[10], s10):
        rec[0][7] = end

    # S7 shape points, S4 links
    s7 = []
    rec_n = tile26.rec4(btype)
    s4 = []
    for k, L in enumerate(links):
        a = L['attr']
        raw = bytearray(rec_n)
        bu = 1 if (street and a['cls'] >= 2 and cell.w <= 327680) else 0
        raw[0x0A] = (a['spd'] & 0x1F) | (bu << 7)
        raw[0x0B] = (a['form'] & 0x0F) | ((a['dirn'] & 3) << 4) | ((a['flags'] & 1) << 6)
        raw[0x0C:0x0E] = min(int(round(L['length'])), 0xFFFF).to_bytes(2, 'big')
        raw[0x0E], raw[0x0F] = L['brg']
        raw[0x10] = (a['cls'] & 0x0F) | ((a['sub'] & 7) << 4)
        raw[0x11] = (a['jn'] & 0x0F) | 0x20
        refs = {0: nref[L['a']], 2: nref[L['b']], 4: Ref(7, len(s7)),
                6: Ref(4, nxt[(k, 0)]) if nxt[(k, 0)] is not None else None,
                8: Ref(4, nxt[(k, 1)]) if nxt[(k, 1)] is not None else None,
                0x12: Ref(10, s10_start[k]), 0x14: Ref(12, 0)}
        if street:
            raw[0x1A] = 0x10 | (0x08 if a['flags'] & 2 else 0)
            raw[0x1B] = (bu << 7) | (_width(a) << 1)
            refs[0x18] = Ref(2, s2_of[k])
            refs[0x1C] = Ref(11, 0)
        for p in L['pts'][1:-1]:
            u, v = loc(p)
            s7.append([bytearray(u.to_bytes(2, 'big') + v.to_bytes(2, 'big') + b'\0\0'), {}])
        s4.append([raw, refs])
    sent = {0: None, 2: None, 4: Ref(7, len(s7)), 6: None, 8: None, 0x12: Ref(10, len(s10)), 0x14: Ref(12, 0)}
    if street:
        sent.update({0x18: None, 0x1C: Ref(11, 0)})
    s4.append([bytearray(rec_n), sent])
    t.secs[4], t.secs[7] = s4, s7

    # S5 / S6 nodes
    def node_rec(key, size):
        n = nodes[key]
        u, v = loc(n['pos'])
        raw = bytearray(size)
        raw[0:4] = u.to_bytes(2, 'big') + v.to_bytes(2, 'big')
        raw[6:8] = _node_flags(n['sec'], reach.get(n['pos'], 0), len(n['links'])).to_bytes(2, 'big')
        return [raw, {4: Ref(4, first[key])}]

    t.secs[5] = [node_rec(k, 8) for k in n5]
    t.secs[6] = [node_rec(k, 16) for k in n6]
    spec.edge_ids = [k[1] for k in n6]
    spec.edge_twins = [(edges[edges[k[1]][3]][0], edges[k[1]][3]) for k in n6]

    # S3 level groups 0..6 + end
    s3 = []
    for g in range(7):
        l0 = next((i for i, L in enumerate(links) if L['attr']['cls'] >= g), len(links))
        q0 = next((i for i, k in enumerate(n5) if nodes[k]['group'] >= g), len(n5))
        s3.append([bytearray(4), {0: Ref(4, l0), 2: Ref(5, q0)}])
    s3.append([bytearray(4), {0: Ref(4, len(links)), 2: Ref(5, len(n5))}])
    t.secs[3] = s3

    # header words (block IDs filled by link.py)
    hw = bytearray(0x20)
    if street:
        hw[0x0C:0x18] = b''.join(NO_COST.to_bytes(4, 'big') for _ in range(3))
        hw[0x18:0x1A] = STREET_HDR_6C.to_bytes(2, 'big')
        hw[0x1A:0x1C] = (10).to_bytes(2, 'big')
        hw[0x1C:0x1E] = (1).to_bytes(2, 'big')
    else:
        hw[0x0C:0x18] = b''.join(NO_COST.to_bytes(4, 'big') for _ in range(3))
        hw[0x1C:0x1E] = (1).to_bytes(2, 'big')
    t.hdr_words = bytes(hw)
    t.head = bytearray(8)
    t.head[4:6] = btype.to_bytes(2, 'big')
    return spec


_FOLD = {'&': ' and ', '#': 'no ', '"': '', '!': '', '$': '', '%': ''}


def clean_name(s):
    """Lower-case ASCII that the CF=1 text code can carry (bytes 0x27-0x7f, space)."""
    import unicodedata
    s = unicodedata.normalize('NFKD', s.lower()).encode('ascii', 'ignore').decode('ascii')
    s = ''.join(_FOLD.get(c, c) if (c < "'" and c != ' ') else c for c in s)
    s = ''.join(c for c in s if c == ' ' or 0x27 <= ord(c) < 0x80)
    return ' '.join(s.split())[:60]


def _names(t, links, strings, region_label, s2_of):
    """Sections 0, 1, 2 and 14 plus the text blob, in the original's layout."""
    names = []
    for L in links:
        a = L['attr']
        nm = strings[a['name']] if a['name'] else (strings[a['ref']] if a['ref'] else '')
        names.append(clean_name(nm))
    distinct = sorted(set(n for n in names if n))
    city = clean_name(region_label) or 'unknown'
    blob = bytearray()
    text_at = {}

    def text(s):
        if s not in text_at:
            text_at[s] = len(blob)
            blob.extend(s.encode('latin-1', 'replace') + b'\0')
        return Text(text_at[s])

    s14 = []
    for s, ln, ty in COUNTRY_S14:
        s14.append([bytearray([0, 0, ln, ty]), {0: text(s)}])
    s14.append([bytearray([0, 0, 2, 0]), {0: text(city)}])
    street0 = len(s14)
    for nm in distinct:
        s14.append([bytearray([0, 0, 2, 0x10 if ROUTE_NAME.match(nm) else 0x08]), {0: text(nm)}])
    n14 = len(s14)
    s0 = [[bytearray(10), {0: None, 4: None}],
          [bytearray([0, 0, 0, COUNTRY_ID_US, 0, 0, 0, 0, 0, 0]), {0: text('united states'), 4: Ref(14, 0)}],
          [bytearray(10), {0: None, 4: Ref(14, len(COUNTRY_S14))}]]
    s1 = [[bytearray(10), {0: None, 2: None, 4: Ref(0, 0), 6: None, 8: None}],
          [bytearray(10), {0: text(city), 2: None, 4: Ref(0, 1), 6: Ref(14, len(COUNTRY_S14)), 8: Ref(14, n14)}],
          [bytearray(10), {0: None, 2: None, 4: Ref(0, 0), 6: Ref(14, street0), 8: Ref(14, n14)}]]
    s2 = [[bytearray(10), {0: None, 2: None, 4: Ref(1, 0), 6: None, 8: None}]]
    rec_of = {}
    if any(not n for n in names):
        rec_of[''] = len(s2)
        s2.append([bytearray(10), {0: None, 2: None, 4: Ref(1, 1), 6: Ref(14, street0), 8: Ref(14, n14)}])
    for j, nm in enumerate(distinct):
        rec_of[nm] = len(s2)
        s2.append([bytearray(10), {0: text(nm), 2: None, 4: Ref(1, 1), 6: Ref(14, street0 + j), 8: Ref(14, n14)}])
    for k, nm in enumerate(names):
        s2_of[k] = rec_of[nm]
    t.secs[0], t.secs[1], t.secs[2], t.secs[14] = s0, s1, s2, s14
    t.blob = bytes(blob)
