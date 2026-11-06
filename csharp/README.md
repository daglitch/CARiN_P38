# osm2carin (C#)

C# port of `tools/osm2carin` and `tools/cdimage.py`: OSM extract → CARiN road tiles for the Range Rover P38 map disc (CD-ID 5944 format). Same commands, same output files under `out/`. Needs the .NET 10 SDK. No other dependencies: the PBF reader and the CF=1 codec are part of the code.

```
dotnet build -c Release csharp
csharp\src\Osm2Carin.Cli\bin\Release\net10.0\osm2carin.exe build --region us/nevada
```

Or `dotnet run -c Release --project csharp/src/Osm2Carin.Cli -- <command>`. The commands:

| Command | What |
|---|---|
| `regions [text]` | list / search Geofabrik regions |
| `estimate --region R` | size estimate before downloading |
| `download --region R` | fetch the `.osm.pbf` (cached in `out/cache`, resumable, MD5-checked) |
| `model --region R` | road graph + POIs, report, model-based estimate |
| `tiles --region R` | road levels 0–3 as CARiN tiles, checked, plus viewer data |
| `build --region R [--force] [--estimate-only]` | all of the above; stops when the estimate doesn't fit a CD-R unless `--force` |
| `disc --region R [--orig <dir>]` | Phase 3a (C# only): road layers grafted onto the original disc, checked, then `out/<region>/disc/<region>-3a.bin/.cue` (burn the `.cue`; the ISO it was made from is in `disc/work/`). `--orig` holds `track1.iso` and `audio.raw` from `cd-split` (default `out/phase0/orig`) |
| `cd-split <bin> <dir>`, `cd-build <iso> <bin> [--audio a.raw]`, `cd-verify <bin>` | raw MODE2/2352 image tools |

Options:
- `--region R`: a Geofabrik region, or several separated by commas (`us/california,us/nevada`), built as one map. Their extracts overlap at the borders; each OSM way, relation and node is taken once.
- `--out <dir>`: the default is `<repo>/out`.
- `--profile default|full|compact`.
- `--no-poi`.
- `--refresh`: download again.
- `--plain`: store street tiles uncompressed.
- `--check-every N`: run the per-tile read-back checks on every Nth tile only. The default, 1, checks all tiles.
- `--orig <dir>`: for `disc`, where `cd-split` put the original disc.

## Same output as the Python tools

The port follows the Python step for step. The tests check it byte for byte against the Python output and the original disc:

| Test | Checks |
|---|---|
| `CdImageTests` | rebuilds the original `.bin` from its ISO: same SHA-1 |
| `Cf1Tests` | the CF=1 codec decodes and re-encodes 480 disc tiles exactly as OpenCarin's Python does, and round-trips all 24,028 compressed street tiles |
| `Tile26Tests` | parse → build is byte-identical on all 38,326 road tiles of the disc; the ordering rules hold |
| `ModelTests` | the Nevada road model equals the Python one in every array and string |
| `RoadTilesTests` | the Nevada road tiles (6,380 blocks) equal the Python ones, on disc and decoded |
| `HypotTests` | CPython's `math.hypot`, ported bit for bit |
| `GraftTests` | Nevada grafted onto the original disc: chain intact, only the 0x07 BLOCK_IDs changed, every reference of the right type, every tile found from 0x07, ISO sizes patched |

To match Python exactly, the port reproduces three things from it:
- numpy's pairwise float sums;
- CPython's `math.hypot`;
- Python's floor-modulo and round-half-even.

Without them, simplification and lengths would round differently in rare near-ties.

The tests need the disc image, OpenCarin's fixtures, and the Python reference output in `out/`. Without them they pass without checking anything (xUnit v2 has no runtime skip).
- `tools/make_parity_fixtures.py` writes `out/csharp-tests/cf1`.
- The Python `model.py` and `tiles.py`/`link.py` write `out/parity/*-py.*`.

Run them with `dotnet test -c Release csharp`.

## Speed (Nevada, 124 MB extract, 12 cores)

| Step | Python | C# |
|---|---|---|
| Road model | 55 s | 7 s |
| Road tiles + link + checks | ~25 min | 18 s |

Where the speed comes from:
- the PBF is decoded in parallel;
- links are cut and tiles built in parallel;
- each street tile is CF=1-encoded once, not twice;
- the dictionary-word search uses rolling hashes instead of building strings.

**Encoding once:** block IDs only occur in fixed-width fields of the encoding, so after allocation the encoder's head is rebuilt and the S6 neighbour IDs are patched into the bit stream (`Encoder00.Parts`).

**Licence:** `src/Osm2Carin.Core/Cf1/` is a port of OpenCarin's CF=1 codec (https://github.com/fdemusso/OpenCarin, MIT). Its licence is in that folder.
