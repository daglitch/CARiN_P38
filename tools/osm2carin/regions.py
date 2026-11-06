"""Region picker over Geofabrik's extract index (https://download.geofabrik.de/index-v1-nogeom.json).

A region is named by its Geofabrik id ("us/california", "germany", "monaco"),
its display name ("Rhode Island"), or an ISO code ("US-NV", "DE").
"""
import json
import os
import time
import urllib.request

INDEX_URL = 'https://download.geofabrik.de/index-v1-nogeom.json'
INDEX_MAX_AGE = 7 * 86400
USER_AGENT = 'osm2carin/0.1 (CARiN P38 map disc builder)'


def _get(url, method='GET'):
    req = urllib.request.Request(url, method=method, headers={'User-Agent': USER_AGENT})
    return urllib.request.urlopen(req, timeout=60)


def load_index(cache_dir, refresh=False):
    os.makedirs(cache_dir, exist_ok=True)
    path = os.path.join(cache_dir, 'geofabrik-index-v1-nogeom.json')
    if refresh or not os.path.exists(path) or time.time() - os.path.getmtime(path) > INDEX_MAX_AGE:
        with _get(INDEX_URL) as r:
            data = r.read()
        json.loads(data.decode('utf-8'))          # validate before replacing the cache
        with open(path, 'wb') as f:
            f.write(data)
    with open(path, encoding='utf-8') as f:
        feats = json.load(f)['features']
    return {ft['properties']['id']: ft['properties'] for ft in feats}


def _label(p):
    name = p.get('name', p['id'])
    return name.split('/')[-1].replace('-', ' ').title() if name == p['id'] else name


def resolve(index, query):
    """-> region properties dict. Raises KeyError with suggestions when ambiguous or unknown."""
    q = query.strip().lower()
    if q in index:
        return index[q]
    norm = q.replace(' ', '-')
    hits = []
    for rid, p in index.items():
        codes = [c.lower() for c in p.get('iso3166-1:alpha2', []) + p.get('iso3166-2', [])]
        if q in codes or rid.split('/')[-1] == norm or _label(p).lower() == q:
            hits.append(p)
    if len(hits) == 1:
        return hits[0]
    if not hits:
        hits = [p for rid, p in index.items() if q in rid or q in _label(p).lower()]
    raise KeyError(f'region "{query}" is ' + ('ambiguous' if hits else 'unknown') +
                   (': ' + ', '.join(sorted(p['id'] for p in hits)[:20]) if hits else
                    '; try "py -m osm2carin regions <text>"'))


def search(index, text=''):
    t = text.lower()
    return sorted((p for rid, p in index.items()
                   if not t or t in rid or t in _label(p).lower()
                   or t in [c.lower() for c in p.get('iso3166-1:alpha2', []) + p.get('iso3166-2', [])]),
                  key=lambda p: p['id'])


def remote_size(url):
    """Content-Length of the extract (follows Geofabrik's redirect), or None."""
    for _ in range(3):
        try:
            with _get(url, 'HEAD') as r:
                n = r.headers.get('Content-Length')
                return int(n) if n else None
        except OSError:
            time.sleep(2)
    return None


def label(p):
    return _label(p)
