# CARiN_P38
CARiN_P38 is a tool for reverse engineering the P38 Navigation disc and adding the ability to update/create a new map based on OSM.

## Quick start

The fast way is the C# version (`csharp/`, .NET 10 SDK, no other dependencies; see `csharp/README.md`):

```
dotnet build -c Release csharp
csharp\src\Osm2Carin.Cli\bin\Release\net10.0\osm2carin.exe build --region us/nevada
csharp\src\Osm2Carin.Cli\bin\Release\net10.0\osm2carin.exe cd-split P38_Navigation_CD_2002_California_Nevada.bin out\phase0\orig
csharp\src\Osm2Carin.Cli\bin\Release\net10.0\osm2carin.exe disc --region us/nevada     # Phase 3a: burnable .bin/.cue in out\us_nevada\disc
```

It produces the same output as the Python tools below, byte for byte, about 50 times faster. The Python version needs Python 3.10+ with `numpy` and `osmium` (`py -m pip install numpy osmium`). Street-level decoding and CF=1 encoding use [OpenCarin](https://github.com/fdemusso/OpenCarin) (MIT), cloned into `third_party/OpenCarin`.

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
| `phase3-plan.md`, `phase3a-car-test.md`, `tools/phase3a_check.py` | Phase 3: the plan; the 3a disc (our roads on the original disc), its car test, and the OpenCarin read-back check |
| `tools/osm2carin/` | OSM → CARiN converter in Python (Phases 1–2: regions, download, road + POI model, size estimate, road tiles for levels 0–3); the reference for the C# port |
| `csharp/` | the same converter in C#, plus the CD image tools; tested byte for byte against the Python output and the disc |
| `tests/` | `py -m unittest discover -s tests` |
| `prompts/osm-to-carin-disc.md` | the project brief and phase plan |
