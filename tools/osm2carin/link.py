"""Give road tiles block IDs and fill in every cross reference (Phase 2).

A BLOCK_ID is `start unit << 8 | units on disc`, so IDs depend on the encoded sizes,
and the encoded sizes (CF=1) depend a little on the IDs written inside the tiles.
`link()` therefore:
  1. adds section 9 (neighbour tiles) and section 8 (the grid of next-lower-level tiles
     of a coarse tile) with symbolic targets; record counts are then final, so every
     tile's layout, and so every edge node's twin offset, is known;
  2. encodes each tile with provisional IDs to size it, allocates units in the original
     disc's order (street tiles, then 0x03, 0x02, 0x01), writes the real IDs (own ID,
     parent, S6 twins, S8, S9, S10) and encodes again, growing any tile that no longer
     fits, until nothing moves.
Street tiles are stored CF=1 (OpenCarin's encode_type00) when that is smaller, as on
the original (92% of its street tiles); coarse tiles are stored plain, as on the original.
"""
import struct

from . import fmt, tiles as T
from .tile26 import Ref

ORDER = (0, 3, 2, 1)
CHILD = {3: 0, 2: 3, 1: 2}          # coarse level -> next level down (section 8)
PARENT = {0: 3, 3: 2, 2: 1}


def _neighbours(root, cell):
    """Leaves sharing a stretch of edge with `cell` -> [(leaf, u, v)] (midpoint of the
    shared stretch, local units)."""
    spans = {}
    step = T.BASE // 2
    sides = (
        ('L', [(cell.x1 - 1, y) for y in range(cell.y1 + step // 2, cell.y2, step)]),
        ('R', [(cell.x2, y) for y in range(cell.y1 + step // 2, cell.y2, step)]),
        ('B', [(x, cell.y1 - 1) for x in range(cell.x1 + step // 2, cell.x2, step)]),
        ('T', [(x, cell.y2) for x in range(cell.x1 + step // 2, cell.x2, step)]),
    )
    for side, probes in sides:
        for x, y in probes:
            if not root.contains(x, y):
                continue
            leaf = T.find_leaf(root, x, y)
            k = (id(leaf), side)
            lo, hi = (y, y) if side in 'LR' else (x, x)
            if k in spans:
                spans[k][1] = min(spans[k][1], lo)
                spans[k][2] = max(spans[k][2], hi)
            else:
                spans[k] = [leaf, lo, hi, side]
    out = []
    for leaf, lo, hi, side in spans.values():
        # widen to the true shared stretch
        if side in 'LR':
            a, b = max(cell.y1, leaf.y1), min(cell.y2, leaf.y2)
            u = 0 if side == 'L' else cell.w // T.GRID
            v = ((a + b) // 2 - cell.y1) // T.GRID
        else:
            a, b = max(cell.x1, leaf.x1), min(cell.x2, leaf.x2)
            v = 0 if side == 'B' else cell.h // T.GRID
            u = ((a + b) // 2 - cell.x1) // T.GRID
        out.append((leaf, u, v))
    return sorted(out, key=lambda e: (e[1], e[2]))


def _grid(cell, child_root, child_cells):
    """Section 8 grid over `cell`: square cells, at most S8_MAX, column-major."""
    inside = [c for c in child_cells if c.x1 >= cell.x1 and c.x2 <= cell.x2 and c.y1 >= cell.y1 and c.y2 <= cell.y2]
    s = min([min(c.w, c.h) for c in inside] or [min(cell.w, cell.h)])
    s = min(s, cell.w, cell.h)
    while (cell.w // s) * (cell.h // s) > T.S8_MAX:
        s *= 2
    gw, gh = cell.w // s, cell.h // s
    leaves = []
    for cx in range(gw):
        for cy in range(gh):
            leaves.append(T.find_leaf(child_root, cell.x1 + cx * s + s // 2, cell.y1 + cy * s + s // 2))
    return s, leaves


def prepare(rt, keep_empty=False):
    """Drop empty tiles, add sections 8 and 9 (symbolic), map leaves to specs."""
    rt.by_leaf = {}
    for lv in ORDER:
        specs = [sp for sp in rt.levels[lv] if keep_empty or len(sp.tile.secs[4]) > 1]
        rt.levels[lv] = specs
        for sp in specs:
            rt.by_leaf[(lv, id(sp.cell))] = sp
    for lv in ORDER:
        root = rt.roots[lv]
        for sp in rt.levels[lv]:
            sp.s9 = [(rt.by_leaf.get((lv, id(leaf))), u, v) for leaf, u, v in _neighbours(root, sp.cell)]
            sp.s9 = [e for e in sp.s9 if e[0] is not None]
            sp.tile.secs[9] = [[bytearray(8), {}] for _ in sp.s9]
            if lv in CHILD:
                c = CHILD[lv]
                s, leaves = _grid(sp.cell, rt.roots[c], rt.cells[c])
                sp.s8_cell = s
                sp.s8 = [rt.by_leaf.get((c, id(leaf))) for leaf in leaves]
                sp.tile.secs[8] = [[bytearray(4), {}] for _ in sp.s8]
            else:
                sp.s8, sp.s8_cell = [], 0
            if lv in PARENT:
                p = PARENT[lv]
                cx, cy = (sp.cell.x1 + sp.cell.x2) // 2, (sp.cell.y1 + sp.cell.y2) // 2
                sp.parent = rt.by_leaf.get((p, id(T.find_leaf(rt.roots[p], cx, cy)))) \
                    if rt.roots[p].contains(cx, cy) else None
    # edge node twins: (neighbour spec, twin index in its S6)
    for lv in ORDER:
        where = {}
        for sp in rt.levels[lv]:
            for k, eid in enumerate(sp.edge_ids):
                where[eid] = (sp, k)
        for sp in rt.levels[lv]:
            sp.twins = [where.get(twin) for _, twin in sp.edge_twins]


def _fill(sp):
    """Write the IDs and offsets into sp.tile from the linked specs."""
    t = sp.tile
    t.block_id = sp.block_id
    hw = bytearray(t.hdr_words)
    hw[4:8] = (sp.parent.block_id if sp.parent else 0).to_bytes(4, 'big')
    if sp.btype != 0:
        hw[8:12] = sp.s8_cell.to_bytes(4, 'big')
    t.hdr_words = bytes(hw)
    for rec, (nb, u, v) in zip(t.secs[9], sp.s9):
        rec[0][:] = struct.pack('>IHH', nb.block_id, u, v)
    for rec, child in zip(t.secs[8], sp.s8):
        rec[0][:] = struct.pack('>I', child.block_id if child else 0)
    for rec, tw in zip(t.secs[6], sp.twins):
        if tw is None:
            rec[0][8:14] = bytes(6)
            continue
        nb, k = tw
        start, _ = nb.tile.layout()
        rec[0][8:14] = struct.pack('>IH', nb.block_id, start[6] + 16 * k)


def choose_widths(d):
    """The two CF=1 bit widths (decoded bytes 6/7) that make the encoding smallest: width 0 for
    link lengths (S4 +0x0C), width 1 for coordinate deltas (S5, S6, S7). Exact bit cost of
    encode_type00 for each width 0..16; the smallest width wins ties. The disc's own tiles use
    about 8-12 and 6-9; leaving both at 0 forces a 16-bit escape for every value."""
    def sec(i):
        return struct.unpack_from('>HH', d, 8 + 4 * i)
    o4, n4 = sec(4)
    lengths = [struct.unpack_from('>H', d, o4 + 30 * k + 0x0C)[0] for k in range(n4)]
    deltas = []
    for i, rec in ((5, 8), (6, 16), (7, 6)):
        off, n = sec(i)
        for k in range(1, n):
            for o in (0, 2):
                v = struct.unpack_from('>H', d, off + rec * k + o)[0]
                pv = struct.unpack_from('>H', d, off + rec * (k - 1) + o)[0]
                deltas.append(((v - pv) & 0xFFFF, (pv - v) & 0xFFFF))

    def len_bits(w):
        return sum(1 + (16 if v >> w else w) for v in lengths)

    def delta_bits(w):
        return sum(1 + w if not up >> w else 2 + w if not down >> w else 18 for up, down in deltas)

    w0 = min(range(17), key=lambda w: (len_bits(w), w))
    w1 = min(range(17), key=lambda w: (delta_bits(w), w))
    return w0, w1


def _encode(sp, oc_cf1):
    """-> raw on-disc bytes (header included, not padded)."""
    t = sp.tile
    t.head[0:4] = sp.block_id.to_bytes(4, 'big')
    t.head[4:6] = sp.btype.to_bytes(2, 'big')
    t.head[6] = t.head[7] = 0
    units = -(-t.decoded_size() // fmt.UNIT)
    plain = t.build(fmt.UNIT, pad_units=units)
    if sp.btype != 0 or oc_cf1 is None:
        return plain.rstrip(b'\0') or plain[:8], False, plain
    wide = bytearray(plain)                    # the widths live only in the CF=1 form
    wide[6:8] = bytes(choose_widths(plain))
    wide = bytes(wide)
    packed = oc_cf1.encode_type00(wide, fmt.RECORD_SIZE_TABLE, fmt.DB_REL, fmt.SUBREL, fmt.UNIT)
    if len(packed) < len(plain.rstrip(b'\0')):
        return packed, True, wide
    return plain.rstrip(b'\0'), False, plain


def link(rt, base_unit=0, oc_cf1=None, log=print, max_rounds=6):
    """Allocate block IDs from `base_unit` and finalise every tile. Sets sp.raw (on-disc
    bytes padded to whole units), sp.block_id, sp.cf1 and sp.decoded."""
    prepare(rt)
    specs = [sp for lv in ORDER for sp in rt.levels[lv]]
    for sp in specs:
        sp.block_id = 0x00FFFF01               # provisional: a large ID
    for sp in specs:
        _fill(sp)
    units = {}
    for sp in specs:
        raw, _, _ = _encode(sp, oc_cf1)
        units[id(sp)] = max(1, -(-len(raw) // fmt.UNIT))
    for rnd in range(max_rounds):
        pos = base_unit
        for sp in specs:
            n = units[id(sp)]
            if n > 255:
                raise ValueError('tile larger than 255 units')
            sp.block_id = (pos << 8) | n
            pos += n
        for sp in specs:
            _fill(sp)
        grown = 0
        for sp in specs:
            raw, packed, plain = _encode(sp, oc_cf1)
            n = max(1, -(-len(raw) // fmt.UNIT))
            if n > units[id(sp)]:
                units[id(sp)] = n
                grown += 1
            sp.cf1, sp.decoded = packed, plain
            sp.raw = raw + bytes(units[id(sp)] * fmt.UNIT - len(raw)) if n <= units[id(sp)] else raw
        log(f'link round {rnd + 1}: {len(specs):,} tiles, {pos - base_unit:,} units, {grown} grew')
        if not grown:
            rt.end_unit = pos
            return specs
    raise RuntimeError('block allocation did not settle')
