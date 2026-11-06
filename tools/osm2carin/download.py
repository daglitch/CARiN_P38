"""Cached .osm.pbf download with a progress bar, resume and MD5 verification."""
import hashlib
import os
import sys
import time
import urllib.request

from .regions import USER_AGENT, _get


def cache_path(cache_dir, region_id):
    return os.path.join(cache_dir, region_id.replace('/', '__') + '-latest.osm.pbf')


def _md5(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        for blk in iter(lambda: f.read(1 << 22), b''):
            h.update(blk)
    return h.hexdigest()


def _expected_md5(url):
    with _get(url + '.md5') as r:
        return r.read().decode('ascii').split()[0].lower()


def _bar(done, total, t0, out=sys.stderr):
    rate = done / max(time.time() - t0, 1e-6)
    if total:
        pct = done / total
        n = int(pct * 30)
        eta = (total - done) / rate if rate else 0
        out.write(f'\r  [{"#" * n}{"." * (30 - n)}] {pct:6.1%} {done / 2**20:8.1f} / '
                  f'{total / 2**20:.1f} MiB  {rate / 2**20:5.1f} MiB/s  ETA {eta:4.0f}s ')
    else:
        out.write(f'\r  {done / 2**20:8.1f} MiB  {rate / 2**20:5.1f} MiB/s ')
    out.flush()


def fetch(region, cache_dir, refresh=False, verify=True, log=print):
    """Download region['urls']['pbf'] into the cache (unless a verified copy is there).
    Returns (path, md5)."""
    os.makedirs(cache_dir, exist_ok=True)
    url = region['urls']['pbf']
    path = cache_path(cache_dir, region['id'])
    want = _expected_md5(url) if verify else None
    if os.path.exists(path) and not refresh:
        if not verify:
            return path, None
        have = _md5(path)
        if have == want:
            log(f'cached: {path} (md5 ok)')
            return path, have
        log('cached copy is stale or damaged (md5 differs): downloading again')
        os.remove(path)
    part = path + '.part'
    done = os.path.getsize(part) if os.path.exists(part) else 0
    headers = {'User-Agent': USER_AGENT}
    if done:
        headers['Range'] = f'bytes={done}-'
    req = urllib.request.Request(url, headers=headers)
    log(f'downloading {url}')
    with urllib.request.urlopen(req, timeout=120) as r:
        if done and r.status != 206:          # server ignored the range: start over
            done = 0
        total = r.headers.get('Content-Length')
        total = int(total) + done if total else None
        t0, last = time.time(), 0
        with open(part, 'ab' if done else 'wb') as f:
            got = 0
            while True:
                blk = r.read(1 << 20)
                if not blk:
                    break
                f.write(blk)
                got += len(blk)
                if time.time() - last > 0.5:
                    _bar(done + got, total, t0)
                    last = time.time()
        _bar(done + got, total, t0)
        sys.stderr.write('\n')
    have = _md5(part) if verify else None
    if verify and have != want:
        os.remove(part)
        raise IOError(f'MD5 mismatch for {url}: got {have}, expected {want}')
    os.replace(part, path)
    log(f'saved {path}' + (' (md5 ok)' if verify else ''))
    return path, have
