"""Output size estimate and CD-R capacity check.

Two estimates, both from ratios measured on our own data (calibration.json):
  * before download: extract size (bytes) -> street links -> carindb bytes;
  * after the road model: counted links and POIs -> carindb bytes.

carindb bytes = links * bytes_per_link + shape_points * bytes_per_shape_point
                + pois * bytes_per_poi + fixed
  bytes_per_link / bytes_per_shape_point: a least-squares fit of the original's
                  compressed street parcels (per link, per shape point, per parcel),
                  plus the per-link share of the other blocks that scale with the
                  street network (coarse road parcels, house numbers, street
                  directory, spatial index). OSM has ~2.4x the shape points per
                  link of the 2002 data, so shape points are counted separately;
  bytes_per_poi:  original disc, POI blocks (0x06, 0x10, 0x11) / POIs (distinct
                  phone numbers in 0x10, a lower bound, so this errs large);
  fixed:          everything else (superblock, country/city tables, background
                  layers, TMC), kept from the original.
links_per_pbf_byte is measured by our own OSM conversion of a reference region.
"""
import json
import os

CAL_PATH = os.path.join(os.path.dirname(__file__), 'calibration.json')

SECTOR = 2048
CDR = {'74 min': 333_000, '80 min': 360_000}
# Non-carindb sectors on the original layout: ISO header + companion files
# (carindb starts at LBA 366), Form 2 postgap, audio pregap and audio track.
OVERHEAD_SECTORS = 366 + 150 + 150 + 300
SAFETY = 1.15        # estimate margin before the real build


def calibration():
    with open(CAL_PATH, encoding='utf-8') as f:
        return json.load(f)


def carindb_bytes(links, shape_points, pois, cal=None, with_pois=True):
    cal = cal or calibration()
    return (links * cal['bytes_per_link'] + shape_points * cal['bytes_per_shape_point']
            + (pois * cal['bytes_per_poi'] if with_pois else 0) + cal['fixed_bytes'])


def from_pbf(pbf_bytes, profile='default', cal=None, with_pois=True):
    """Rough: assumes the region has the reference region's roads per extract byte."""
    cal = cal or calibration()
    links = pbf_bytes * cal['links_per_pbf_byte'][profile]
    shape = pbf_bytes * cal['shape_points_per_pbf_byte'][profile]
    pois = pbf_bytes * cal['pois_per_pbf_byte'] if with_pois else 0
    return dict(method='extract size', pbf_bytes=pbf_bytes, links=round(links), shape_points=round(shape),
                pois=round(pois), carindb_bytes=round(carindb_bytes(links, shape, pois, cal, with_pois) * SAFETY),
                margin=SAFETY)


def from_model(links, shape_points, pois, cal=None, with_pois=True):
    return dict(method='road model', links=links, shape_points=shape_points, pois=pois if with_pois else 0,
                carindb_bytes=round(carindb_bytes(links, shape_points, pois, cal, with_pois) * SAFETY),
                margin=SAFETY)


def capacity(carindb_bytes):
    """-> dict with total sectors and % of each CD-R size."""
    units = -(-carindb_bytes // SECTOR)
    total = units + OVERHEAD_SECTORS
    return dict(carindb_units=units, total_sectors=total, mib=round(total * SECTOR / 2**20, 1),
                percent={k: round(100 * total / v, 1) for k, v in CDR.items()},
                fits={k: total <= v for k, v in CDR.items()})


def advice(cap, est, profile):
    """Human-readable warnings and suggestions."""
    lines = []
    p74, p80 = cap['percent']['74 min'], cap['percent']['80 min']
    if cap['fits']['74 min']:
        lines.append(f'OK: fits a 74-min CD-R ({p74}%).')
        if p74 > 85:
            lines.append('Close to full: keep a margin, the estimate is +/- ~15%.')
    elif cap['fits']['80 min']:
        lines.append(f'WARNING: needs an 80-min CD-R ({p80}%); it will not fit a 74-min disc ({p74}%).')
    else:
        over = cap['total_sectors'] / 360_000
        lines.append(f'ERROR: too big for any CD-R: {p80}% of an 80-min disc ({over:.1f}x).')
        lines.append('To make it fit, try one or more of:')
        lines.append('  - a smaller region (a state instead of a country, e.g. "py -m osm2carin regions <name>");')
        if profile != 'compact':
            lines.append('  - --profile compact (drops service roads: class 6);')
        lines.append('  - --no-poi or --poi-categories with fewer categories;')
        lines.append('  - --levels 1-3 (major roads only, no street level).')
    return lines
