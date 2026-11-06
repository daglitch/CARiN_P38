"""Road tile model for DB-REL 26 (CD-ID 5944): block types 0x00 (street) and 0x01-0x03 (coarse).

`Tile.parse(decoded)` turns a decoded tile into sections of records whose
pointers are symbolic (section, record) or text references; `Tile.build()` lays
the sections out again and writes every pointer. On the original disc
`build(parse(d)) == d` byte for byte (tests/test_tile26.py), which pins down
the layout rules below. Adapted for the 30/24-byte records of DB-REL 26 from
OpenCarin's carin/parser/cf1/tile00.py (MIT; DB-REL >= 27, 32/26-byte records).

Layout (all offsets absolute within the decoded block, big-endian):
  +0x00  u32 BLOCK_ID (start unit << 8 | units), u16 type, u8 compression, u8 units unpacked
  +0x08  15 x (u16 offset, u16 count) section descriptors
  +0x44  bbox x1 y1 x2 y2 (s32 global units)          +0x54 .. +0x74 header words (HDR_WORDS)
  +0x74  sections in the order 0 1 2 3 4 5 6 7 9 8 10 11 12 13 14, then the text blob
         (each section starts at the end of the previous one, rounded up to ALIGN[s];
          an empty S13 / S14 of a coarse tile has offset 0)

Records (bytes)          pointer fields (byte offset in record -> target)
  S0  admin, country 10  +0 text, +4 S14
  S1  admin, state   10  +0 +2 text, +4 S0, +6 +8 S14 range
  S2  street names   10  +0 +2 text, +4 S1, +6 S14
  S3  level groups    4  +0 S4, +2 S5          (count + 1 records, the last the end marker)
  S4  links       30/24  +0 +2 S5|S6 node, +4 S7, +6 +8 S4 next at start/end node,
                         +0x12 S10, +0x14 S12, street only: +0x18 S2, +0x1C S11
                         (count + 1 records, the last the sentinel: end of S7/S10/S12/S11)
  S5  nodes           8  +4 S4
  S6  edge nodes     16  +4 S4 (+8 u32 neighbour BLOCK_ID, +12 u16 twin offset in it)
  S7  shape points    6  -
  S8  level-down grid 4  - (u32 BLOCK_IDs)
  S9  neighbours      8  - (u32 BLOCK_ID, u16 u, u16 v)
  S10 turn bans       8  +4 S4 when +0 is the tile's own BLOCK_ID
  S11 signposts       6  +0 +2 text
  S12 (TMC?)          6  -
  S13 -               8  -
  S14 name index      4  +0 text
"""
import struct

ORDER = (0, 1, 2, 3, 4, 5, 6, 7, 9, 8, 10, 11, 12, 13, 14)
PROLOG = 116
HDR_WORDS = (0x54, 0x74)
REC = {0: 10, 1: 10, 2: 10, 3: 4, 5: 8, 6: 16, 7: 6, 8: 4, 9: 8, 10: 8, 11: 6, 12: 6, 13: 8, 14: 4}
EXTRA = {3: 1, 4: 1}                     # sections stored with one more record than counted
ALIGN = {6: 4, 8: 4, 9: 4, 10: 4, 13: 4}  # sections holding u32 fields start 4-aligned, others 2


def rec4(btype):
    return 30 if btype == 0 else 24


# pointer fields: section -> [(byte offset, kind, target)], kind 'p' = section pointer,
# 't' = text pointer. A section pointer may point at the end of the target section.
def ptr_fields(btype):
    f = {
        0: [(0, 't', None), (4, 'p', 14)],
        1: [(0, 't', None), (2, 't', None), (4, 'p', 0), (6, 'p', 14), (8, 'p', 14)],
        2: [(0, 't', None), (2, 't', None), (4, 'p', 1), (6, 'p', 14), (8, 'p', 14)],
        3: [(0, 'p', 4), (2, 'p', 5)],
        4: [(0, 'p', 56), (2, 'p', 56), (4, 'p', 7), (6, 'p', 4), (8, 'p', 4), (0x12, 'p', 10), (0x14, 'p', 12)],
        5: [(4, 'p', 4)],
        6: [(4, 'p', 4)],
        11: [(0, 't', None), (2, 't', None)],
        14: [(0, 't', None)],
    }
    if btype == 0:
        f[4] += [(0x18, 'p', 2), (0x1C, 'p', 11)]
    return f


class TileError(Exception):
    pass


class Ref:
    """Symbolic pointer: section `sec`, record `idx` (idx == count means 'end of section')."""
    __slots__ = ('sec', 'idx')

    def __init__(self, sec, idx):
        self.sec, self.idx = sec, idx

    def __eq__(self, o):
        return isinstance(o, Ref) and (self.sec, self.idx) == (o.sec, o.idx)

    def __repr__(self):
        return f'S{self.sec}[{self.idx}]'


class Text:
    """Text pointer relative to the start of the blob."""
    __slots__ = ('rel',)

    def __init__(self, rel):
        self.rel = rel

    def __eq__(self, o):
        return isinstance(o, Text) and self.rel == o.rel

    def __repr__(self):
        return f'T+{self.rel}'


class Tile:
    def __init__(self, btype):
        self.btype = btype
        self.block_id = 0
        self.head = bytearray(8)
        self.bbox = (0, 0, 0, 0)
        self.hdr_words = bytes(HDR_WORDS[1] - HDR_WORDS[0])
        # section -> list of records; each record is [bytearray raw, {offset: Ref|Text|None}]
        self.secs = {s: [] for s in ORDER}
        self.zero_offset = set()         # empty sections written with offset 0
        self.blob = b''
        self.n_units = 1                 # decoded size in 2048-byte units
        self.literals = 0                # pointer fields whose value hit no record (kept as is)

    # ------------------------------------------------------------- parse
    @classmethod
    def parse(cls, d, unit=2048):
        btype = struct.unpack_from('>H', d, 4)[0]
        if btype > 3:
            raise TileError(f'not a road tile: type {btype:#x}')
        t = cls(btype)
        t.head = bytearray(d[:8])
        t.block_id = struct.unpack_from('>I', d, 0)[0]
        t.bbox = struct.unpack_from('>4i', d, 0x44)
        t.hdr_words = bytes(d[HDR_WORDS[0]:HDR_WORDS[1]])
        t.n_units = len(d) // unit
        desc = {s: struct.unpack_from('>HH', d, 8 + 4 * s) for s in range(15)}
        size = {**REC, 4: rec4(btype)}
        start = {}
        pos = PROLOG
        layout = []
        for s in ORDER:
            off, cnt = desc[s]
            n = cnt + EXTRA.get(s, 0)
            if off == 0 and cnt == 0:
                t.zero_offset.add(s)          # takes no space and no alignment
                start[s] = pos
                layout.append((s, pos, n))
                continue
            if off != _align(pos, s):
                raise TileError(f'S{s} at {off}, expected {_align(pos, s)} (after {pos})')
            start[s] = off
            layout.append((s, off, n))
            pos = off + n * size[s]
        blob_start = pos
        end_of = {s: start[s] + (desc[s][1] + EXTRA.get(s, 0)) * size[s] for s in ORDER}
        t.blob_start = blob_start
        # the blob runs to the last non-zero byte (the rest is padding)
        nz = len(d.rstrip(b'\0')) if isinstance(d, bytes) else len(bytes(d).rstrip(b'\0'))
        t.blob = bytes(d[blob_start:max(nz, blob_start)])
        fields = ptr_fields(btype)

        def resolve(val, target):
            if val == 0:
                return None
            secs = (5, 6) if target == 56 else (target,)
            for ts in secs:
                if start[ts] <= val < end_of[ts] and (val - start[ts]) % size[ts] == 0:
                    return Ref(ts, (val - start[ts]) // size[ts])
            for ts in secs:
                if val == end_of[ts]:
                    return Ref(ts, (end_of[ts] - start[ts]) // size[ts])
            t.literals += 1
            return val                # not a pointer here (kept as a literal value)

        for s, off, n in layout:
            for k in range(n):
                raw = bytearray(d[off + k * size[s]: off + (k + 1) * size[s]])
                refs = {}
                for o, kind, target in fields.get(s, ()):
                    if s == 4 and o >= len(raw):
                        continue
                    v = struct.unpack_from('>H', raw, o)[0]
                    if kind == 't':
                        if v == 0:
                            refs[o] = None
                        elif v < blob_start:
                            raise TileError(f'S{s}[{k}] +{o}: text pointer {v} before the blob')
                        else:
                            refs[o] = Text(v - blob_start)
                    else:
                        refs[o] = resolve(v, target)
                    raw[o:o + 2] = b'\0\0'
                if s == 10 and struct.unpack_from('>I', raw, 0)[0] == t.block_id:
                    v = struct.unpack_from('>H', raw, 4)[0]
                    refs[4] = resolve(v, 4)
                    refs['own'] = True
                    raw[0:4] = b'\0\0\0\0'
                    raw[4:6] = b'\0\0'
                t.secs[s].append([raw, refs])
        return t

    # ------------------------------------------------------------- build
    def layout(self):
        """-> ({section: start offset}, blob start) for the current record counts."""
        size = {**REC, 4: rec4(self.btype)}
        start, pos = {}, PROLOG
        for s in ORDER:
            n = len(self.secs[s])
            if s in self.zero_offset and n == 0:
                start[s] = pos
                continue
            start[s] = _align(pos, s)
            pos = start[s] + n * size[s]
        return start, pos

    def decoded_size(self):
        return self.layout()[1] + len(self.blob)

    def build(self, unit=2048, pad_units=None):
        size = {**REC, 4: rec4(self.btype)}
        start, blob_start = self.layout()
        total = blob_start + len(self.blob)
        n_units = pad_units or max(self.n_units, -(-total // unit))
        if total > n_units * unit:
            raise TileError(f'tile needs {total} bytes, more than {n_units} units')
        d = bytearray(n_units * unit)
        d[:8] = self.head
        struct.pack_into('>I', d, 0, self.block_id)
        for s in range(15):
            n = len(self.secs[s]) - EXTRA.get(s, 0)
            off = 0 if (s in self.zero_offset and n == 0) else start[s]
            struct.pack_into('>HH', d, 8 + 4 * s, off, max(n, 0))
        struct.pack_into('>4i', d, 0x44, *self.bbox)
        d[HDR_WORDS[0]:HDR_WORDS[1]] = self.hdr_words

        def addr(ref):
            if ref is None:
                return 0
            if isinstance(ref, int):
                return ref
            if isinstance(ref, Text):
                return blob_start + ref.rel
            return start[ref.sec] + ref.idx * size[ref.sec]

        for s in ORDER:
            for k, (raw, refs) in enumerate(self.secs[s]):
                r = bytearray(raw)
                for o, ref in refs.items():
                    if o == 'own':
                        continue
                    if s == 10 and refs.get('own'):
                        struct.pack_into('>I', r, 0, self.block_id)
                    struct.pack_into('>H', r, o, addr(ref))
                d[start[s] + k * size[s]: start[s] + (k + 1) * size[s]] = r
        d[blob_start:blob_start + len(self.blob)] = self.blob
        return bytes(d)


def _align(pos, s):
    a = ALIGN.get(s, 2)
    return -(-pos // a) * a


# ---------------------------------------------------------------- accessors / rules
def u16(raw, o):
    return struct.unpack_from('>H', raw, o)[0]


def link_class(raw):
    return raw[0x10] & 0x0F


def check_rules(t):
    """Count violations of the ordering rules the unit needs (OpenCarin 03 section 6.7:
    breaking the first two crashed a CNI1). Returns {rule: (ok, bad)}."""
    links = t.secs[4][:-1]
    node = lambda ref: t.secs[ref.sec][ref.idx][0]
    res = {}

    def tally(name, cond):
        ok, bad = res.get(name, (0, 0))
        res[name] = (ok + bool(cond), bad + (not cond))

    # R1: start node has the lower (u, v)
    for raw, refs in links:
        a, b = node(refs[0]), node(refs[2])
        tally('start node lower (u,v)', (u16(a, 0), u16(a, 2)) <= (u16(b, 0), u16(b, 2)))
    # R2/R3: S3 level groups: links of class k, S5 nodes sorted by (u, v) in each group
    groups = t.secs[3]
    for g in range(len(groups) - 1):
        l0, l1 = groups[g][1][0].idx, groups[g + 1][1][0].idx
        n0, n1 = groups[g][1][2].idx, groups[g + 1][1][2].idx
        for raw, _ in links[l0:l1]:
            tally('link class == group', link_class(raw) == g)
        uv = [(u16(t.secs[5][k][0], 0), u16(t.secs[5][k][0], 2)) for k in range(n0, n1)]
        for p, q in zip(uv, uv[1:]):
            tally('S5 sorted by (u,v) in group', p <= q)
    # R4: node cycles clockwise by leaving bearing, starting from the smallest
    for sec in (5, 6):
        for k, (raw, refs) in enumerate(t.secs[sec]):
            first = refs.get(4)
            if not isinstance(first, Ref):
                continue
            cyc, cur, seen = [], first, set()
            while isinstance(cur, Ref) and cur.idx not in seen and cur.idx < len(links):
                seen.add(cur.idx)
                lraw, lrefs = links[cur.idx]
                at_start = lrefs[0] == Ref(sec, k)
                cyc.append(lraw[0x0E] if at_start else lraw[0x0F])
                cur = lrefs[6] if at_start else lrefs[8]
            if len(cyc) >= 2:
                tally('cycle clockwise from smallest bearing', cyc == sorted(cyc))
    return res
