# CARiN_P38

CARiN_P38 reverse engineers the Range Rover P38 navigation map disc and builds new map discs from OpenStreetMap.

The reference disc is the 2002 California/Nevada CD (CD-ID 5944, DB-REL 26, Navtech data, CARiN format). It has full streets for California and Nevada and major roads across the US. The project decodes the whole road database on that disc. It then writes OSM roads, cities, streets, house numbers and POIs back into the original database, and produces a burnable `.bin`/`.cue` image.

The disc format is documented in [carin-disc-findings.md](carin-disc-findings.md). Its "Current status" section comes first.

## Requirements

- **The original disc image**, saved in the repo root as `P38_Navigation_CD_2002_California_Nevada.bin` / `.cue`. It is copyrighted (about 440 MB) and is not in the repo, so you have to rip it yourself (for example with ImgBurn).
- **C# tools:** the .NET 10 SDK. Nothing else is needed, because the PBF reader and the CF=1 codec are built in. See [csharp/README.md](csharp/README.md).
- **Python tools:** Python 3.10+ with `numpy` and `osmium` (`py -m pip install numpy osmium`).
- **Street-level decoding** (in the Python tools and the viewer) uses [OpenCarin](https://github.com/fdemusso/OpenCarin) (MIT), cloned into `third_party/OpenCarin`. `run-viewer.bat` clones it automatically.

## Quick start

The C# version is the fast one:

```
dotnet build -c Release csharp
set OSM2CARIN=csharp\src\Osm2Carin.Cli\bin\Release\net10.0\osm2carin.exe

%OSM2CARIN% build --region us/nevada                                   # download + road model + tiles + report
%OSM2CARIN% cd-split P38_Navigation_CD_2002_California_Nevada.bin out\phase0\orig
%OSM2CARIN% disc --region us/nevada                                    # burnable .bin/.cue in out\us_nevada\disc
```

`--region` accepts Geofabrik regions, several at once, plus `:highways` / `:major` selections. For example, `us/california,us/nevada,us:highways` gives full detail in two states and the US highway network around them. All commands and options are listed in [csharp/README.md](csharp/README.md).

The Python version produces the same output as the C# one, byte for byte, but about 50 times slower. It covers Phases 1–2 and is the reference for the C# port:

```
cd tools
py -m osm2carin regions nevada                      # find a region
py -m osm2carin estimate --region us/california     # size check before downloading
py -m osm2carin build --region us/nevada            # download + road model + report
```

Output goes to `out/` (git-ignored). `out/cache/` holds the OSM extracts and the TIGER address files, and `out/<region>/` holds the model, `report.md`, the viewer data and `disc/`.

## Viewer

`run-viewer.bat` runs these steps:

1. extracts the original disc into `%TEMP%\p38_carin`;
2. exports road levels 1–3 to `viewer/data`;
3. on the first run only, decodes the street level with OpenCarin (about 15 minutes);
4. serves the viewer at http://127.0.0.1:8738/.

After a `tiles` or `disc` run, `out/<region>/viewer` holds the same view for a generated map, with house numbers included.

`launch.json` holds a dev-server config for the viewer (`py -m http.server 8738`).

## Status

| Phase | What | Doc |
|---|---|---|
| 0 | Byte-identical image rebuild, CF=1 round trip, name-patched test discs for the car | [phase0-car-test.md](phase0-car-test.md) |
| 1 | Region picker, cached download, road + POI model, size estimate | |
| 2 | Road tiles for levels 0–3, POI category codes | |
| 3a | OSM road layers grafted onto the original disc | [phase3a-car-test.md](phase3a-car-test.md) |
| 3b / 3c | OSM cities, streets (address entry) and POIs | [phase3c-car-test.md](phase3c-car-test.md) |
| 3d | House numbers from US Census TIGER address ranges | [phase3d-car-test.md](phase3d-car-test.md) |

The overall plan is in [phase3-plan.md](phase3-plan.md) and the project brief is in [prompts/osm-to-carin-disc.md](prompts/osm-to-carin-disc.md). Each car-test doc gives the build commands and the test order. Some questions can only be answered by testing in the car; [phase3-plan.md](phase3-plan.md) lists them.

## Repository layout

| Path | What |
|---|---|
| [carin-disc-findings.md](carin-disc-findings.md) | the disc format: block chain, parcel layouts, coordinates, CF=1, indexes |
| [parcel-coverage.png](parcel-coverage.png) | plot of the parcel coverage on the original disc |
| [tools/carin.py](tools/carin.py) | disc reader: ISO extraction, levels 1–3, street level via OpenCarin, viewer export |
| [tools/cdimage.py](tools/cdimage.py) | raw MODE2/2352 `.bin`/`.cue` builder and EDC/ECC checker |
| [tools/osm2carin/](tools/osm2carin/) | OSM → CARiN converter in Python (Phases 1–2); the reference for the C# port |
| `tools/phase0_*.py` | Phase 0: image round trip and patched test discs |
| [tools/addr_survey.py](tools/addr_survey.py), [tools/phase3a_check.py](tools/phase3a_check.py) | Phase 3d survey of the house-number data in an OSM extract; OpenCarin read-back check of a built disc |
| [tools/make_parity_fixtures.py](tools/make_parity_fixtures.py) | writes the OpenCarin CF=1 decode/encode fixtures for the C# parity tests |
| [tools/scratch/](tools/scratch/) | one-off scripts from the reverse-engineering work, kept for reference |
| [csharp/](csharp/) | the converter in C#, plus the CD image tools and Phases 3a–3d (`disc`) |
| [tests/](tests/) | Python tests |
| [run-viewer.bat](run-viewer.bat), `viewer/` | map viewer for the original disc (`viewer/` is generated and git-ignored) |
| [opencarin-contributions.md](opencarin-contributions.md), [opencarin-issue6-comment.md](opencarin-issue6-comment.md) | data points from this disc for the OpenCarin issue tracker |
| `third_party/` | OpenCarin clone (git-ignored) |
| `out/` | build output, caches and logs (git-ignored) |

## Tests

```
py -m unittest discover -s tests
dotnet test csharp
```

The C# tests check the output against the Python output and the original disc, byte for byte.

## Licence and data

- The original disc image is copyrighted and is not distributed here.
- Map data comes from [OpenStreetMap](https://www.openstreetmap.org/copyright) (ODbL) through [Geofabrik](https://download.geofabrik.de/). House numbers come from the US Census TIGER/Line address ranges (public domain).
- [OpenCarin](https://github.com/fdemusso/OpenCarin) is MIT-licensed. It is cloned at build time and is not vendored.
