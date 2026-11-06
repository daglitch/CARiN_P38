# CARiN_P38
CARiN_P38 is a tool for reverse engineering the P38 Navigation disc and adding the ability to update/create a new map based on OSM.

## Quick start

Needs Python 3.10+ with `numpy` and `osmium` (`py -m pip install numpy osmium`). Street-level decoding and CF=1 encoding use [OpenCarin](https://github.com/fdemusso/OpenCarin) (MIT), cloned into `third_party/OpenCarin`.

```
cd tools
py -m osm2carin regions nevada                      # find a region
py -m osm2carin estimate --region us/california     # size check before downloading
py -m osm2carin build --region us/nevada            # download + road model + report (Phase 1)
```

Output goes to `out/` (git-ignored): `out/cache/` holds the extracts, `out/<region>/` the model and `report.md`.

| Path | What |
|---|---|
| `carin-disc-findings.md` | the disc format; "Current status" first |
| `tools/carin.py` | disc reader (ISO, levels 1-3, street level via OpenCarin) |
| `tools/cdimage.py` | raw MODE2/2352 `.bin`/`.cue` builder and EDC/ECC checker |
| `tools/phase0_*.py`, `phase0-car-test.md` | Phase 0: modified test discs for the car |
| `tools/osm2carin/` | OSM → CARiN converter (Phase 1 done: regions, download, road + POI model, size estimate) |
| `tests/` | `py -m unittest discover -s tests` |
| `prompts/osm-to-carin-disc.md` | the project brief and phase plan |
