"""OSM tags -> CARiN street-link attributes.

Field meanings: OpenCarin docs/carindb/03-road-network.md section 6.7.
The class/speed targets were checked against the original disc's Nevada links
(`py -m osm2carin.survey`, see carin-disc-findings.md "Phase 1").

  cls   +0x10 & 0x0F  road class 0 (motorway) .. 6 (not for through traffic)
  sub   +0x10 >> 4    class-6 subtype: 1 restricted/service, 3 private, 5 pedestrian
  dirn  +0x0B >> 4    0 two-way, 1 only along the stored direction, 3 closed
  form  +0x0B & 0x0F  0 motorway, 1/2/3 on/off/motorway-motorway slip, 4 dual
                      carriageway, 8/9/0xA slips elsewhere, 0xB main / 0xC other single carriageway
  spd   +0x0A & 0x1F  speed category (~km/h / 4, table SPEED_POINTS)
  jn    +0x11 & 0x0F  0 ordinary, 6 roundabout, 5 small roundabout (<= 66 m)
  flags bit0 toll, bit1 bridge, bit2 tunnel, bit3 unpaved
"""
import re

import numpy as np

# highway -> (class, class-6 subtype, default km/h). Codes are the list index.
# Matched to the original disc's Nevada street level (222,921 links, 135,904 km):
#   class 0 motorway (speed 31), 1 / 2 US + state highways (22), 3 secondary and
#   freeway ramps (16), 4 local streets (11, 16 on collectors), 5 alleys (5),
#   6/1 rural dirt roads and service roads (11; 106,000 km, mostly TIGER tracks).
HIGHWAYS = {
    'motorway': (0, 0, 113), 'motorway_link': (3, 0, 64),
    'trunk': (1, 0, 80), 'trunk_link': (3, 0, 64),
    'primary': (2, 0, 80), 'primary_link': (3, 0, 64),
    'secondary': (3, 0, 64), 'secondary_link': (4, 0, 48),
    'tertiary': (4, 0, 64), 'tertiary_link': (4, 0, 48),
    'unclassified': (4, 0, 48), 'residential': (4, 0, 48), 'road': (4, 0, 48),
    'living_street': (5, 0, 32),
    'service': (6, 1, 48), 'track': (6, 1, 48),
    'pedestrian': (6, 5, 8),
}
HIGHWAY_CODES = list(HIGHWAYS)
HW_CODE = {h: i for i, h in enumerate(HIGHWAY_CODES)}
LINK_HW = {HW_CODE[h] for h in HIGHWAYS if h.endswith('_link')}

PROFILES = {
    # like the original Navtech 2002 disc: public roads, service roads except
    # driveways / parking aisles / private, and tracks that are TIGER-imported,
    # named or grade 1-3 (the disc's rural dirt roads); no footways
    'default': dict(exclude={'pedestrian'}, tracks='selected',
                    exclude_service={'driveway', 'parking_aisle', 'drive-through', 'emergency_access',
                                     'private', 'yes'}),
    # everything drivable in OSM
    'full': dict(exclude=set(), tracks='all', exclude_service=set()),
    # big regions: no class-6 roads at all
    'compact': dict(exclude={'service', 'track', 'pedestrian'}, tracks='none', exclude_service=set()),
}

# km/h -> speed category, from OpenCarin's OSM match (20 mph 5, 30 mph 11, 40 mph 16,
# 80 km/h 22, 60 mph 26, 70 mph 31); interpolated in between.
SPEED_POINTS = ([0, 8, 32, 48, 64, 80, 97, 113, 130], [1, 2, 5, 11, 16, 22, 26, 31, 31])

last_skip_reason = ''


def highway_table(profile):
    p = PROFILES[profile]
    t = {h: v for h, v in HIGHWAYS.items() if h not in p['exclude']}
    t['_profile'] = p
    return t


def speed_category(kmh):
    return int(round(float(np.interp(kmh, *SPEED_POINTS))))


_MPH = re.compile(r'^\s*(\d+(?:\.\d+)?)\s*mph\s*$')
_KMH = re.compile(r'^\s*(\d+(?:\.\d+)?)\s*(?:km/h|kmh|kph)?\s*$')


def parse_maxspeed(v):
    """-> km/h or None. Handles '35 mph', '50', '50 km/h', 'US:urban' style is ignored."""
    if not v:
        return None
    v = v.split(';')[0]
    m = _MPH.match(v)
    if m:
        return float(m.group(1)) * 1.609344
    m = _KMH.match(v)
    if m:
        return float(m.group(1))
    return None


def _yes(v):
    return v in ('yes', 'true', '1')


def way_attrs(hw, spec, tags):
    """-> dict of per-way attributes, or None (reason in last_skip_reason)."""
    global last_skip_reason
    cls, sub, kmh = spec
    g = tags.get
    if g('area') == 'yes':
        last_skip_reason = 'area=yes'
        return None
    prof = _current[0]
    if hw == 'service':
        st = g('service') or ''
        excl = prof['exclude_service']
        if st in excl or (excl and g('access') in ('private', 'no')):
            last_skip_reason = f'service={st or "-"} access={g("access") or "-"}'
            return None
        if st == 'alley':
            cls, sub, kmh = 5, 0, 32
    if hw == 'track' and prof['tracks'] == 'selected' and not (
            g('tiger:cfcc') or g('name') or g('tracktype') in ('grade1', 'grade2', 'grade3')):
        last_skip_reason = 'no name / TIGER origin / grade 1-3 (profile default)'
        return None
    access = g('motor_vehicle') or g('motorcar') or g('vehicle') or g('access') or ''
    oneway = g('oneway') or ''
    jn_tag = g('junction') or ''
    dirn, rev = 0, 0
    if access == 'no':
        dirn = 3
    elif oneway in ('-1', 'reverse'):
        dirn, rev = 1, 1
    elif _yes(oneway) or (oneway == '' and (jn_tag in ('roundabout', 'circular') or hw == 'motorway')):
        dirn = 1
    elif oneway == 'reversible':
        dirn = 3
    if access in ('private', 'customers', 'delivery', 'destination') and cls >= 5:
        cls, sub = 6, 3
    # The original stores a default per road type (OpenCarin: maxspeed does not
    # improve the match), so OSM maxspeed is not used.
    spd = speed_category(kmh)
    flags = 0
    if _yes(g('toll') or ''):
        flags |= 1
    if (g('bridge') or 'no') not in ('no', ''):
        flags |= 2
    if (g('tunnel') or 'no') not in ('no', '') or g('covered') == 'yes':
        flags |= 4
    if (g('surface') or '') in ('unpaved', 'dirt', 'gravel', 'ground', 'sand', 'grass', 'compacted',
                                'fine_gravel', 'earth', 'mud'):
        flags |= 8
    jn = 6 if jn_tag in ('roundabout', 'circular') else 0
    name = (g('name') or '').lower()
    ref = (g('ref') or '').lower()
    return dict(hw=HW_CODE[hw], cls=cls, sub=sub, dirn=dirn, rev=rev, jn=jn, flags=flags, spd=spd,
                name=name, ref=ref)


_current = [PROFILES['default']]


def set_profile(profile):
    _current[0] = PROFILES[profile]


def form_of_way(L, n_nodes):
    """Form of way per link (+0x0B & 0x0F), slip roles from the graph."""
    hw, cls, dirn = L['hw'], L['cls'], L['dirn']
    n = len(hw)
    # 0xB = main road: on the disc it marks classes 1-3 and the class-4 collectors
    # (speed 16), i.e. OSM tertiary; 0xC the rest
    form = np.where((cls <= 3) | (hw == HW_CODE['tertiary']), 0xB, 0xC).astype(np.uint8)
    motorway = hw == HW_CODE['motorway']
    form[motorway] = 0
    dual = (dirn == 1) & np.isin(hw, [HW_CODE['trunk'], HW_CODE['primary']])
    form[dual] = 4
    is_link = np.isin(hw, list(LINK_HW))
    if not is_link.any():
        return form
    # nodes touched by main (non-slip) roads: motorway / any main road
    main_mw = np.zeros(n_nodes, bool)
    main_any = np.zeros(n_nodes, bool)
    for arr in (L['frm'], L['to']):
        main_mw[arr[motorway]] = True
        main_any[arr[~is_link]] = True
    # Trace each slip link along slip links, in its direction of travel, to the
    # first nodes that touch a main road: forward gives where it ends, backward
    # where it starts (two-way slips are followed both ways).
    idx = np.flatnonzero(is_link)
    frm, to = L['frm'], L['to']
    out_s, in_s = {}, {}
    for i in idx:
        f, t = int(frm[i]), int(to[i])
        out_s.setdefault(f, []).append(t)
        in_s.setdefault(t, []).append(f)
        if dirn[i] == 0:
            out_s.setdefault(t, []).append(f)
            in_s.setdefault(f, []).append(t)

    def reaches(node, adj, main, limit=500):
        """Follow slip links from `node`; stop at nodes on a main road. True if
        one of those is a `main` node (motorway, or class <= 3)."""
        seen, stack = {node}, [node]
        while stack and len(seen) < limit:
            nd = stack.pop()
            if main_any[nd]:
                if main[nd]:
                    return True
                continue
            for nx in adj.get(nd, ()):
                if nx not in seen:
                    seen.add(nx)
                    stack.append(nx)
        return False

    mw_main = main_mw
    main_hi = np.zeros(n_nodes, bool)          # roads of class <= 3 (for non-motorway slips)
    for arr in (frm, to):
        main_hi[arr[(~is_link) & (cls <= 3)]] = True
    for i in idx:
        f, t = int(frm[i]), int(to[i])
        if hw[i] == HW_CODE['motorway_link']:
            s_, e_ = reaches(f, in_s, mw_main), reaches(t, out_s, mw_main)
            form[i] = 3 if s_ and e_ else 2 if s_ else 1
        elif hw[i] == HW_CODE['trunk_link']:
            s_, e_ = reaches(f, in_s, main_hi), reaches(t, out_s, main_hi)
            form[i] = 0xA if s_ and e_ else 9 if s_ else 8
        else:
            # primary/secondary/tertiary links (turn lanes): the original disc maps them
            # as ordinary roads (forms 8/9/0xA: 50 of 222,921 Nevada links)
            form[i] = 0xB
    return form
