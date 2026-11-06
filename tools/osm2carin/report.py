"""Build report (JSON + Markdown) for a run."""
import json
import os


def write(stats, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, 'report.json'), 'w', encoding='utf-8') as f:
        json.dump(stats, f, indent=1, ensure_ascii=False)
    md = os.path.join(out_dir, 'report.md')
    s = stats
    e = s.get('estimate', {})
    cap = e.get('capacity', {})
    L = [f'# osm2carin report: {s.get("region")}', '',
         f'- extract: `{s["pbf"]}` ({s["pbf_bytes"] / 2**20:,.1f} MiB, md5 {s.get("pbf_md5")})',
         f'- profile: {s["profile"]}; model built in {s["seconds"]} s', '',
         '## In (OSM)', '',
         f'- road ways used: {s["road_ways"]:,}',
         f'- turn restrictions: {s["restrictions"]["in_osm"]:,} (mapped {s["restrictions"]["mapped"]:,})', '',
         '## Out (road model, street level)', '',
         f'- links: {s["links"]:,} ({s["road_km"]:,} km), graph nodes {s["graph_nodes"]:,}, '
         f'shape points {s["shape_points"]:,}',
         f'- one-way {s["oneway_links"]:,}, closed {s["closed_links"]:,}, named {s["named_links"]:,}',
         f'- links over 65,535 m (split later): {s["long_links_over_65535m"]}', '',
         '| class | links | km |', '|---|---|---|']
    L += [f'| {c} | {v["links"]:,} | {v["km"]:,} |' for c, v in s['links_by_class'].items()]
    L += ['', '| highway | links | km |', '|---|---|---|']
    L += [f'| {h} | {v["links"]:,} | {v["km"]:,} |' for h, v in s['links_by_highway'].items()]
    L += ['', f'## POIs: {s["pois"]:,} ({s["pois_named"]:,} named)', '', '| category | count |', '|---|---|']
    L += [f'| {c} | {n:,} |' for c, n in s['poi_categories'].items()]
    if e:
        L += ['', '## Size estimate', '',
              f'- carindb ~{e["carindb_bytes"] / 2**20:,.0f} MiB (incl. {e["margin"] - 1:.0%} margin)',
              f'- total {cap["total_sectors"]:,} sectors ({cap["mib"]:,} MiB): '
              f'{cap["percent"]["74 min"]}% of a 74-min CD-R, {cap["percent"]["80 min"]}% of an 80-min CD-R']
        L += [f'- {a}' for a in e.get('advice', [])]
    L += ['', '## Dropped', '', '| what | count |', '|---|---|']
    L += [f'| {k} | {v:,} |' for k, v in sorted(s['dropped'].items(), key=lambda kv: -kv[1])]
    with open(md, 'w', encoding='utf-8') as f:
        f.write('\n'.join(L) + '\n')
    return md
