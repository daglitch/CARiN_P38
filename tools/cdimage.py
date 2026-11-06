"""Raw CD image (MODE2/2352) builder and checker for CARiN map discs.

The original P38 disc (CD-ID 5944) is laid out as:
  track 1  MODE2/2352: N data sectors in Mode 2 Form 1 (2048 user bytes,
           subheader 00 00 08 00), then 150 Form 2 postgap sectors
           (subheader 00 00 20 00, zero data, EDC computed);
  track 2  AUDIO after a 2 s pregap (not stored in the .bin).

Every Form 1 sector gets sync, BCD MSF header, subheader, EDC and the P/Q
Reed-Solomon parity (ECMA-130 Annex A / B). ECC is computed with the 4 header
bytes taken as zero, as Mode 2 requires. Vectorised with numpy across sectors.

Usage:
  py tools/cdimage.py split   <orig.bin> <out_dir>          # -> track1.iso, audio.raw, layout.json
  py tools/cdimage.py build   <in.iso> <out.bin> [--audio audio.raw] [--postgap 150]
                              [--audio-pregap 150]          # also writes <out>.cue
  py tools/cdimage.py verify  <image.bin>                   # check sync/header/EDC/ECC of every data sector
  py tools/cdimage.py compare <a.bin> <b.bin>
"""
import argparse
import hashlib
import json
import os
import sys

import numpy as np

RAW = 2352
USER = 2048
SYNC = bytes([0]) + b'\xff' * 10 + bytes([0])
SUB_FORM1 = bytes([0, 0, 0x08, 0])
SUB_FORM2 = bytes([0, 0, 0x20, 0])
CHUNK = 8192          # sectors per numpy batch

# ---------------------------------------------------------------- tables
_F = np.zeros(256, np.uint8)
_B = np.zeros(256, np.uint8)
_EDC = np.zeros(256, np.uint32)
for _i in range(256):
    _j = ((_i << 1) ^ (0x11D if _i & 0x80 else 0)) & 0xFF
    _F[_i] = _j
    _B[_i ^ _j] = _i
    _e = _i
    for _ in range(8):
        _e = (_e >> 1) ^ (0xD8018001 if _e & 1 else 0)
    _EDC[_i] = _e


def _ecc_index(major_count, minor_count, major_mult, minor_inc):
    size = major_count * minor_count
    idx = np.zeros((major_count, minor_count), np.int64)
    for major in range(major_count):
        index = (major >> 1) * major_mult + (major & 1)
        for minor in range(minor_count):
            idx[major, minor] = index
            index += minor_inc
            if index >= size:
                index -= size
    return idx


_P_IDX = _ecc_index(86, 24, 2, 86)
_Q_IDX = _ecc_index(52, 43, 86, 88)


def edc(block):
    """block: (n, L) uint8 -> (n,) uint32 CD-ROM EDC."""
    e = np.zeros(block.shape[0], np.uint32)
    for k in range(block.shape[1]):
        e = (e >> 8) ^ _EDC[(e ^ block[:, k]) & 0xFF]
    return e


def _ecc_block(src, idx):
    """src: (n, size) uint8; idx: (major, minor) -> (n, 2*major) parity bytes."""
    n, (major, minor) = src.shape[0], idx.shape
    a = np.zeros((n, major), np.uint8)
    b = np.zeros((n, major), np.uint8)
    for m in range(minor):
        t = src[:, idx[:, m]]
        a ^= t
        b ^= t
        a = _F[a]
    a = _B[_F[a] ^ b]
    return np.concatenate([a, a ^ b], axis=1)


def _bcd(v):
    return ((v // 10) << 4) | (v % 10)


def _headers(lba0, n, mode=2):
    lba = np.arange(lba0, lba0 + n) + 150
    m, s, f = lba // 4500, (lba // 75) % 60, lba % 75
    h = np.zeros((n, 4), np.uint8)
    h[:, 0] = [_bcd(int(x)) for x in m]
    h[:, 1] = [_bcd(int(x)) for x in s]
    h[:, 2] = [_bcd(int(x)) for x in f]
    h[:, 3] = mode
    return h


def form1_sectors(user, lba0, subheader=SUB_FORM1):
    """user: bytes, a multiple of 2048 -> raw 2352-byte Mode 2 Form 1 sectors."""
    n = len(user) // USER
    sec = np.zeros((n, RAW), np.uint8)
    sec[:, 0:12] = np.frombuffer(SYNC, np.uint8)
    sec[:, 16:20] = np.frombuffer(subheader, np.uint8)
    sec[:, 20:24] = np.frombuffer(subheader, np.uint8)
    sec[:, 24:24 + USER] = np.frombuffer(user, np.uint8).reshape(n, USER)
    e = edc(sec[:, 16:2072])
    sec[:, 2072:2076] = e.view(np.uint8).reshape(n, 4)      # little-endian
    # header bytes are zero while computing ECC (Mode 2)
    sec[:, 2076:2248] = _ecc_block(sec[:, 12:12 + 2064], _P_IDX)
    sec[:, 2248:2352] = _ecc_block(sec[:, 12:12 + 2236], _Q_IDX)
    sec[:, 12:16] = _headers(lba0, n)
    return sec.tobytes()


def form2_sectors(n, lba0, subheader=SUB_FORM2):
    """Zero-filled Mode 2 Form 2 sectors with EDC (used for the postgap)."""
    sec = np.zeros((n, RAW), np.uint8)
    sec[:, 0:12] = np.frombuffer(SYNC, np.uint8)
    sec[:, 16:20] = np.frombuffer(subheader, np.uint8)
    sec[:, 20:24] = np.frombuffer(subheader, np.uint8)
    sec[:, 2348:2352] = edc(sec[:, 16:2348]).view(np.uint8).reshape(n, 4)
    sec[:, 12:16] = _headers(lba0, n)
    return sec.tobytes()


def msf(lba):
    return f'{lba // 4500:02d}:{(lba // 75) % 60:02d}:{lba % 75:02d}'


def write_cue(cue_path, bin_name, track1_sectors, audio_pregap=150, has_audio=True):
    lines = [f'FILE "{bin_name}" BINARY', '  TRACK 01 MODE2/2352', '    INDEX 01 00:00:00']
    if has_audio:
        lines += ['  TRACK 02 AUDIO', f'    PREGAP {msf(audio_pregap)}',
                  f'    INDEX 01 {msf(track1_sectors)}']
    with open(cue_path, 'w', newline='\r\n') as f:
        f.write('\n'.join(lines) + '\n')


def build(iso_path, bin_path, audio_path=None, postgap=150, audio_pregap=150):
    """ISO (Form 1 user data) [+ raw audio] -> .bin + .cue. Returns track-1 sector count."""
    n_iso = os.path.getsize(iso_path) // USER
    if os.path.getsize(iso_path) % USER:
        raise ValueError('ISO size is not a multiple of 2048')
    with open(iso_path, 'rb') as f, open(bin_path, 'wb') as o:
        lba = 0
        while lba < n_iso:
            chunk = f.read(CHUNK * USER)
            o.write(form1_sectors(chunk, lba))
            lba += len(chunk) // USER
        if postgap:
            o.write(form2_sectors(postgap, lba))
        if audio_path:
            with open(audio_path, 'rb') as a:
                o.write(a.read())
    track1 = n_iso + postgap
    write_cue(os.path.splitext(bin_path)[0] + '.cue', os.path.basename(bin_path),
              track1, audio_pregap, bool(audio_path))
    return track1


def split(bin_path, out_dir):
    """Original .bin -> track1.iso (Form 1 user data only), audio.raw, layout.json."""
    os.makedirs(out_dir, exist_ok=True)
    n_f1 = n_f2 = n_audio = 0
    with open(bin_path, 'rb') as f, open(os.path.join(out_dir, 'track1.iso'), 'wb') as iso, \
            open(os.path.join(out_dir, 'audio.raw'), 'wb') as aud:
        while True:
            s = f.read(RAW)
            if len(s) < RAW:
                break
            if n_audio or s[:12] != SYNC:
                aud.write(s)
                n_audio += 1
            elif s[18] & 0x20:
                n_f2 += 1
            else:
                if n_f2:
                    raise ValueError('Form 1 sector after Form 2 postgap')
                iso.write(s[24:24 + USER])
                n_f1 += 1
    lay = dict(form1=n_f1, postgap_form2=n_f2, audio_sectors=n_audio)
    with open(os.path.join(out_dir, 'layout.json'), 'w') as f:
        json.dump(lay, f, indent=1)
    return lay


def verify(bin_path):
    """Regenerate each data sector from its own user data and compare. Returns
    (n_form1_ok, n_form1_bad, n_form2_ok, n_form2_bad, first_bad_lba)."""
    ok1 = bad1 = ok2 = bad2 = 0
    first_bad = None
    with open(bin_path, 'rb') as f:
        lba = 0
        while True:
            raw = f.read(CHUNK * RAW)
            if not raw:
                break
            arr = np.frombuffer(raw[:len(raw) // RAW * RAW], np.uint8).reshape(-1, RAW)
            data = arr[(arr[:, :12] == np.frombuffer(SYNC, np.uint8)).all(1)]
            f1 = data[(data[:, 18] & 0x20) == 0]
            f2 = data[(data[:, 18] & 0x20) != 0]
            if len(f1):
                lba1 = _lba_of(f1[0])
                regen = np.frombuffer(form1_sectors(f1[:, 24:24 + USER].tobytes(), lba1,
                                                    bytes(f1[0, 16:20])), np.uint8).reshape(-1, RAW)
                eq = (regen == f1).all(1)
                ok1 += int(eq.sum())
                bad1 += int((~eq).sum())
                if first_bad is None and not eq.all():
                    first_bad = lba1 + int(np.argmin(eq))
            if len(f2):
                e = edc(f2[:, 16:2348]).view(np.uint8).reshape(-1, 4)
                eq = (e == f2[:, 2348:2352]).all(1)
                ok2 += int(eq.sum())
                bad2 += int((~eq).sum())
            lba += arr.shape[0]
    return ok1, bad1, ok2, bad2, first_bad


def _lba_of(sec):
    d = lambda b: (b >> 4) * 10 + (b & 15)
    return (d(int(sec[12])) * 60 + d(int(sec[13]))) * 75 + d(int(sec[14])) - 150


def sha1(path):
    h = hashlib.sha1()
    with open(path, 'rb') as f:
        for blk in iter(lambda: f.read(1 << 22), b''):
            h.update(blk)
    return h.hexdigest()


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sp = ap.add_subparsers(dest='cmd', required=True)
    p = sp.add_parser('split'); p.add_argument('bin'); p.add_argument('out_dir')
    p = sp.add_parser('build'); p.add_argument('iso'); p.add_argument('bin')
    p.add_argument('--audio'); p.add_argument('--postgap', type=int, default=150)
    p.add_argument('--audio-pregap', type=int, default=150)
    p = sp.add_parser('verify'); p.add_argument('bin')
    p = sp.add_parser('compare'); p.add_argument('a'); p.add_argument('b')
    a = ap.parse_args(argv)
    if a.cmd == 'split':
        print(split(a.bin, a.out_dir))
    elif a.cmd == 'build':
        t1 = build(a.iso, a.bin, a.audio, a.postgap, a.audio_pregap)
        print(f'track 1: {t1:,} sectors; wrote {a.bin} and .cue')
    elif a.cmd == 'verify':
        ok1, bad1, ok2, bad2, fb = verify(a.bin)
        print(f'Form 1: {ok1:,} ok, {bad1:,} bad; Form 2: {ok2:,} ok, {bad2:,} bad'
              + (f'; first bad LBA {fb}' if fb is not None else ''))
        return 1 if bad1 or bad2 else 0
    elif a.cmd == 'compare':
        same = os.path.getsize(a.a) == os.path.getsize(a.b) and sha1(a.a) == sha1(a.b)
        print('identical' if same else 'DIFFERENT')
        return 0 if same else 1


if __name__ == '__main__':
    sys.exit(main())
