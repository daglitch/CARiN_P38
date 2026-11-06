"""osm2carin: OSM extract -> CARiN map disc (Range Rover P38, CD-ID 5944 format).

Run from the tools/ folder (or with tools/ on PYTHONPATH):

  py -m osm2carin regions [text]                  list / search Geofabrik regions
  py -m osm2carin estimate --region us/nevada     size estimate before downloading
  py -m osm2carin download --region us/nevada     fetch the .osm.pbf (cached, MD5-checked)
  py -m osm2carin model    --region us/nevada     road graph + POIs, report, model-based estimate
  py -m osm2carin tiles    --region us/nevada     road levels 0-3 as CARiN tiles, checked, + viewer data
  py -m osm2carin build    --region us/california --out out/ [--force] [--levels 0-3]
                           [--estimate-only] [--profile default|full|compact] [--no-poi]

Phase status: 1 (download, road model, estimate) and 2 (road tiles for levels 0-3)
are implemented; 3-4 (the other DB structures, packing and burning) are not yet,
so `build` stops after the road tiles with a report.
"""
import argparse
import json
import os
import sys

from . import download, estimate, regions

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_OUT = os.path.join(REPO, 'out')


def slug(region_id):
    return region_id.replace('/', '_')


def _region(args):
    idx = regions.load_index(os.path.join(args.out, 'cache'))
    try:
        return regions.resolve(idx, args.region)
    except KeyError as e:
        sys.exit(str(e).strip('"\''))


def _print_estimate(est, cap, profile, title):
    print(f'\n{title}')
    print(f'  street links ~{est["links"]:,}, POIs ~{est["pois"]:,}')
    print(f'  carindb ~{est["carindb_bytes"] / 2**20:,.0f} MiB (incl. {est["margin"] - 1:.0%} margin), '
          f'{cap["total_sectors"]:,} sectors = {cap["percent"]["74 min"]}% of a 74-min, '
          f'{cap["percent"]["80 min"]}% of an 80-min CD-R')
    for line in estimate.advice(cap, est, profile):
        print('  ' + line)


def cmd_regions(args):
    idx = regions.load_index(os.path.join(args.out, 'cache'), refresh=args.refresh)
    hits = regions.search(idx, args.text or '')
    for p in hits:
        codes = ','.join(p.get('iso3166-1:alpha2', []) + p.get('iso3166-2', []))
        print(f'{p["id"]:45s} {regions.label(p):35s} {codes}')
    print(f'{len(hits)} regions')


def pre_estimate(args, region):
    size = regions.remote_size(region['urls']['pbf'])
    if size is None:
        print('could not get the extract size; skipping the pre-download estimate')
        return None
    print(f'{region["id"]}: extract {size / 2**20:,.0f} MiB')
    est = estimate.from_pbf(size, args.profile, with_pois=not args.no_poi)
    cap = estimate.capacity(est['carindb_bytes'])
    _print_estimate(est, cap, args.profile, 'Estimate before download:')
    return est, cap


def cmd_estimate(args):
    pre_estimate(args, _region(args))


def cmd_download(args):
    download.fetch(_region(args), os.path.join(args.out, 'cache'), refresh=args.refresh)


def run_model(args, region):
    from . import model, report
    pbf, md5 = download.fetch(region, os.path.join(args.out, 'cache'), refresh=args.refresh)
    m, strings, stats = model.build(pbf, args.profile)
    stats.update(region=region['id'], pbf_md5=md5)
    d = os.path.join(args.out, slug(region['id']))
    model.save(m, strings, stats, os.path.join(d, 'model'))
    est = estimate.from_model(stats['links'], stats['shape_points'], stats['pois'], with_pois=not args.no_poi)
    cap = estimate.capacity(est['carindb_bytes'])
    stats['estimate'] = dict(est, capacity=cap, advice=estimate.advice(cap, est, args.profile))
    rp = report.write(stats, d)
    _print_estimate(est, cap, args.profile, 'Estimate from the road model:')
    print(f'\nreport: {rp}')
    return stats, cap


def cmd_model(args):
    run_model(args, _region(args))


def opencarin(args):
    """OpenCarin's CF=1 codec (third_party/OpenCarin, cloned by run-viewer.bat), or None."""
    oc = args.opencarin or os.path.join(REPO, 'third_party', 'OpenCarin')
    if not os.path.isdir(os.path.join(oc, 'carin')):
        print(f'OpenCarin not found in {oc}: street tiles are stored plain (larger)')
        return None
    sys.path.insert(0, oc)
    sys.modules.pop('carin', None)
    from carin.parser import cf1
    return cf1


def run_tiles(args, region, stats=None):
    import time
    from . import link, model, report, tiles, validate
    d = os.path.join(args.out, slug(region['id']))
    if not os.path.exists(os.path.join(d, 'model.npz')):
        stats, _ = run_model(args, region)
    m, strings, mstats = model.load(os.path.join(d, 'model'))
    t0 = time.time()
    rt = tiles.build(m, strings, regions.label(region))
    cf1 = opencarin(args)
    specs = link.link(rt, 0, cf1)
    res, problems = validate.check(specs, cf1)
    vstats = validate.export_viewer(specs, os.path.join(d, 'viewer'),
                                    os.path.join(REPO, 'viewer', 'index.html'))
    # the tiles, back to back as they will sit in carindb (provisional base unit 0)
    with open(os.path.join(d, 'roads.bin'), 'wb') as f:
        for sp in specs:
            f.write(sp.raw)
    by = {}
    for sp in specs:
        k = f'0x{sp.btype:02X}'
        b = by.setdefault(k, dict(blocks=0, units=0, cf1=0, decoded_bytes=0))
        b['blocks'] += 1
        b['units'] += len(sp.raw) // 2048
        b['cf1'] += int(bool(sp.cf1))
        b['decoded_bytes'] += len(sp.decoded)
    mstats['tiles'] = dict(rt.stats, blocks=by, units=rt.end_unit, seconds=round(time.time() - t0, 1),
                           checks=res, problems=problems, viewer=vstats)
    rp = report.write(mstats, d)
    bad = {k: v for k, v in res.items() if k.isupper() or 'BROKEN' in k or 'OFF' in k or 'NO TILE' in k}
    print(f'road tiles: {len(specs):,} blocks, {rt.end_unit:,} units ({rt.end_unit * 2048 / 2**20:,.1f} MiB)')
    for k, v in by.items():
        print(f'  {k}: {v["blocks"]:,} blocks, {v["units"]:,} units, {v["cf1"]:,} CF=1')
    print('checks: ' + ('all passed' if not bad and not problems else f'PROBLEMS {bad} {problems}'))
    print(f'viewer: {os.path.join(d, "viewer")}  (py -m http.server -d <that folder>)')
    print(f'report: {rp}')
    return specs


def cmd_tiles(args):
    run_tiles(args, _region(args))


def cmd_build(args):
    region = _region(args)
    pre = pre_estimate(args, region)
    if pre and not pre[1]['fits']['80 min'] and not args.force:
        print('\nStopping: the estimate does not fit a CD-R. Use --force to continue anyway.')
        return 1
    if args.estimate_only:
        return 0
    stats, cap = run_model(args, region)
    if not cap['fits']['80 min'] and not args.force:
        print('\nStopping: the model-based estimate does not fit a CD-R. Use --force to continue anyway.')
        return 1
    print('\nPhases 2-4 (levels 0-3, database, .bin/.cue) are not implemented yet; stopped after the model.')
    return 2


def main(argv=None):
    ap = argparse.ArgumentParser(prog='osm2carin', description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--out', default=DEFAULT_OUT, help='output folder (default: <repo>/out)')
    sp = ap.add_subparsers(dest='cmd', required=True)
    p = sp.add_parser('regions'); p.add_argument('text', nargs='?'); p.add_argument('--refresh', action='store_true')
    for name in ('estimate', 'download', 'model', 'tiles', 'build'):
        p = sp.add_parser(name)
        p.add_argument('--region', required=True)
        p.add_argument('--out', default=argparse.SUPPRESS)
        p.add_argument('--profile', default='default', choices=['default', 'full', 'compact'])
        p.add_argument('--no-poi', action='store_true')
        p.add_argument('--refresh', action='store_true', help='re-download even if cached')
        p.add_argument('--opencarin', help='OpenCarin checkout (default: <repo>/third_party/OpenCarin)')
        if name == 'build':
            p.add_argument('--force', action='store_true')
            p.add_argument('--levels', default='0-3')
            p.add_argument('--estimate-only', action='store_true')
    args = ap.parse_args(argv)
    args.out = os.path.abspath(args.out)
    return {'regions': cmd_regions, 'estimate': cmd_estimate, 'download': cmd_download,
            'model': cmd_model, 'tiles': cmd_tiles, 'build': cmd_build}[args.cmd](args)


if __name__ == '__main__':
    sys.exit(main() or 0)
