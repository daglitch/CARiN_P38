# P38 CARiN map disc: findings

Source: `P38_Navigation_CD_2002_California_Nevada.bin` / `.cue` (raw image, 439,802,832 bytes)

Tags: **[seen]** = read directly from tool output; **[inferred]** = hypothesis, not yet verified.

---

## Current status (2026-11-04): the whole road database decodes

**Every road on the disc now decodes and displays correctly:**
- the plain coarse road levels 1–3: 730k links, decoded by our own `tools/carin.py`;
- all **26,023 street-level (type 0x00) parcels**: 1,995 plain + 24,028 compressed, **0 failures, 3,008,859 links**. The compressed ones are decoded with OpenCarin's CF=1 decoder.

**Checks:**
- Decoded geometry lies on OpenStreetMap to within metres: Las Vegas Strip, Golden Gate, downtown LA grid, Silver Lake curved streets at zoom 17, downtown Reno.
- Street names decode correctly ("los angeles, ca", "bartlett st", "ca 110 north ramp", "hayward, ca"…).

### Disc facts
| Item | Value | Source |
|---|---|---|
| CD-ID | 5944 | `BIBLIOGR`, `0x13` |
| DB-REL | 26 (0x1A) | `BIBLIOGR`, superblock `+0x1A`, `0x13` section 0 |
| CF=1 sub-revision | **8** → 14-bit width in section 6 | superblock `+0x22` = `0x0008`; `CarinVolume.calibrate()` agrees |
| Data supplier / build | Navtech, `us1-10_hw_nt_ald_02I_20021105b.cpr`, GDF 3.0, conv tool `blof_1.1_20021030`, conversion 2002-11-05 | `0x13` text |
| Coverage | Full streets for California and Nevada; major roads and metro areas across the US (incl. Hawaii, Detroit, Florida…) | parcel plot |
| Sector unit | 2048-byte units in a single `/carindb` (186,175 units, 96,187 blocks) | block chain |
| Conditional access | false (no copy protection declared) | `0x13` text |

### Coordinates (exact)
- `lat = Y / K`, `lon = X / K − 30` (equivalently `X × 1.8e-7 − 390`), with **K = 2e9 / 360** (1 unit = 1.8e-7°). Verified to within metres against OSM.
- Inside a road parcel: `X = x1 + u × 64`, `Y = y1 + v × 64`, where (x1, y1) is the parcel bounding box at `+0x44`.

### Block chain and block header (verified)
`u24 start_unit | u8 n_units | u16 type | u8 flags (bit 0 = CF=1 compressed) | u8 unpacked units`. Walking from unit 0 covers all 96,187 blocks with no gaps.

| Type | Count on this disc | Content |
|---|---|---|
| 0x12 / 0x13 | 1 / 1 | superblock (incl. `RECORD_SIZE_TABLE`) / CD info |
| 0x07, 0x0B, 0x0A, 0x0D, 0x0C, 0x0F, 0x0E, 0x10, 0x11 | small → large | spatial/layer index, letter index, country, tries, cities, street-name directory, POI index |
| **0x00** | 26,023 (24,028 CF=1) | **street-level road parcels** |
| **0x01 / 0x02 / 0x03** | 3,151 / 3,656 / 5,496 (all plain) | coarser road levels, each with a parent-parcel link |
| 0x04 | 20,702 | per-segment house-number ranges |
| 0x06 | 1,704 | POIs (plain text visible) |
| 0x08 / 0x09 | 96 / 15,245 | spatial grid / cell matrix |
| 0x14–0x16, 0x1C–0x1E | mixed CF=0/1 | background layers (water, land use, "pacific ocean") |
| 0x17 / 0x18 | 20 / 3 | TMC location records / TMC table index (no 0x19/0x1A/0x1B on this disc) |

### Road parcel layout (types 0x00–0x03, after CF=1 decoding)
Section table at `+0x08`: 15 × (u16 offset, u16 count). Bounding box at `+0x44`. Record sizes come from the superblock `RECORD_SIZE_TABLE`; on this disc id `0x08` (links) = 30 bytes for type 0x00 and 24 bytes for types 0x01–0x03.

| Sec | Content | Record |
|---|---|---|
| 0/1/2 | admin hierarchy (country → state/county → city) | 10 B: parent pointer, string offsets, name-index offsets |
| 3 | (link-range, node-range) pairs per road-class group | 4 B, +1 sentinel |
| 4 | links | from, to, shape cursor, adjacency A/B, attr, **length (m)**, **heading bytes** (start, end+128; 256 per circle), … name refs. Count+1 records (sentinel) |
| 5 | nodes | 8 B: u, v, first link, flags (e.g. 0xD500, 0x9000) |
| 6 | tile-edge nodes | 16 B: u, v, link, flags, neighbour block ID, neighbour offset |
| 7 | shape points | 6 B: u, v, flags. Link *i* owns `[shape_i, shape_{i+1})` |
| 9 / 10 / 11 / 12 | neighbour parcels / cross-tile link refs / extra tables | 8 / 8 / 6 / 6 B |
| 14 | name index | 4 B: string offset, language/type; strings follow |

A road is drawn as from-node → its shape points → to-node.

### Compressed (CF=1) street parcels
CF=1 is **structure-driven bit-packing**, not a general-purpose compressor. Each section has its own field decoder over an MSB-first bitstream. Its main features:
- pointers stored as record indices;
- inheritance ("same as previous") flags;
- absolute/delta coordinate codes;
- two per-block adaptive widths (the two plain bytes after the 0x74 prologue);
- a prefix-coded street-name blob at the end.

The decoder was recovered from head-unit firmware by **OpenCarin** (https://github.com/fdemusso/OpenCarin, MIT licence). It decodes in passes by format revision; DB-REL 26 uses passes 0x14, 0x15 and 0x17. Our independent hand analysis (history below) had already reached the same structure: index pointers, inheritance flags, the absolute/delta coordinate code with width = b1, length and heading fields, and optional attribute words. OpenCarin's decoder made the full decode possible.

### Tools and data in this folder
| Path | What |
|---|---|
| `tools/carin.py` | `iso` (raw .bin → ISO), `extract` (ISO files), `export` (road levels 1–3 → viewer JSON), `streets` (all street parcels → viewer cells, via OpenCarin) |
| `viewer/index.html` | Leaflet viewer over an OSM basemap. Levels 1–3 always; street cells (green) load on demand at zoom ≥ 13 |
| `viewer/data/level{1,2,3}.json` | coarse road levels (~48 MB) |
| `viewer/data/l0/<ix>_<iy>.json` + `index.json` | street level, 27,675 cells of 0.1°, integer 1e-5° delta coordinates (~200 MB) |
| `run-viewer.bat` | builds whatever data is missing (clones OpenCarin into `third_party/` if needed), then serves http://127.0.0.1:8738/ |
| `tools/scratch/`, `tools/scratch/level0/` | exploration scripts from the hand reverse-engineering |
| `opencarin-issue6-comment.md`, `opencarin-contributions.md` | drafted contributions to OpenCarin issues (not posted) |

### Other block types checked on this disc (contributed to OpenCarin)
- **Superblock** `+0x1A` = DB-REL 26, **`+0x22` = 0x0008 = CF=1 sub-revision**. Decoding 60 sampled street parcels: 51 give street-name text with subrel 8, 5 with subrel 9. This answers OpenCarin issue #6. `+0x24` = {0x3C, 31}; `+0x28` = {0x7A, 90} record-size table.
- **0x13 CD info** (unit 1, CF=0): section 0 = `1, 26, 0x38, 9, 0x41, 15`; section 1 = the 111,111,111 / 222,222,222 / 333,333,333 / 444,444,444 placeholder + `1, 1, 26, 0x50, 481, 0`.
- **0x0B letter index** (unit 3): 3 records (`e`, `u`, `v` → 0x0A), leaf = 1, last u16 = 0.
- **0x0A country** (unit 4): one 44-byte record at `+0x58`. **COUNTRY_ID `+0x28` = 0xE0 (United States)**, next to the UK's 0xDF in English ISO 3166 order. `+0x08` = 11/22/33/44 placeholder; `+0x1C` = 500/300/1000/500; `+0x24` = 0. No COVERAGE/ISO fields at this DB-REL.
- **0x07 → 0x18** TMC links: 20-byte records {0x18 BLOCK_ID, table ID, 1, name offset, 0x0100, COUNTRY_ID 0xE0, 0, …}.
- **0x18 TMC tables:** IDs 0x051, 0x061, 0x191 = **CC 1 (USA), LTN 5, 6, 25**.
- **0x17 TMC locations:** **56-byte records** at DB-REL 26 (vs 100 at DB-REL 34). Fields: code, type, road number/road name/location name/second name pointers, road code, next point, and **location X/Y at `+0x20/+0x24`**. Example: "CA-12 / WRIGHT RD" decodes to 38.4274° N, 122.7690° W. Area names are US regions ("SAN FRANCISCO METRO"…).

### Phase 0 (2026-11-05): rebuilding and patching the disc
Tools: `tools/cdimage.py` (raw image split/build/verify), `tools/phase0_roundtrip.py`, `tools/phase0_patch.py`, tests in `tests/test_phase0.py`. Output under `out/phase0/`.

1. **Raw image rebuilt byte-identically [verified].** `cdimage.py split` → `track1.iso` (186,541 Form 1 sectors of user data = the ISO volume size in the PVD) + `audio.raw` (300 sectors). `cdimage.py build` regenerates sync, BCD MSF header (LBA + 150), subheader, EDC and P/Q ECC for every sector, adds 150 zero Form 2 postgap sectors, appends the audio and writes the cue. The result has the same SHA-1 as the original `.bin`, and the cue matches line for line (CRLF). Sector facts:
   - every data sector is Mode 2 Form 1 with subheader `00 00 08 00` (no EOR/EOF bits, not even at file ends);
   - postgap: subheader `00 00 20 00`, zero data, **EDC present** (`3f13b0be`);
   - ECC computed with the 4 header bytes zeroed (Mode 2 rule). An independent check: every original sector regenerates from its own user data.
2. **CF=1 type 0x00 round trip with OpenCarin [verified on a sample].** DB-REL 26, subrel 8, 2048-byte units. Every 5th CF=1 street parcel (**4,806 of 24,028**): decode → `encode_type00` → decode matched on **4,806/4,806**, 0 errors, all fit their extent; re-encoded 18.90 MB vs 19.04 MB original (−0.75%). About 0.2 s per parcel (`out/phase0/roundtrip-step5.json`).
   - Re-encoded bytes are **never bit-identical** to the disc: OpenCarin uses no text dictionary and picks inheritance flags afresh. They match **after decoding** (bytes 4.. of the decoded block), and the re-encoded block is 0.75% *smaller*, so it always fits its original extent.
   - Header bytes 0–7 and the 116-byte plain prolog (incl. the bounding box at `+0x44`) are unchanged.
3. **Test images [built; car test pending].** Nothing moves: same blocks, sectors, ISO directory and file sizes.
   - `test-patched.bin/.cue`: POI name `land rover san jose` → `land rover p38 test`, in place in 4 plain 0x10 (POI index) blocks (ISO sectors 31643, 31837, 43368, 43376). The dealer is at 5080 Stevens Creek Blvd, San Jose, 408-247-7600. Tests integrity checks.
   - `test-reencoded.bin/.cue`: the above, plus the street `stevens creek blvd` → `stevens creek test` in the 12 CF=1 street parcels that carry it (Cupertino → San Jose, lat ≈ 37.32, lon −122.09 … −121.93), re-encoded with `encode_type00`. Tests whether the unit accepts OpenCarin's bitstream. The street directory (0x0E), POI addresses (0x10) and coarse levels are not changed, so destination entry still lists "stevens creek blvd".
   - Both: EDC/ECC verified on all 186,691 data sectors; reading back the `.bin` (`cdimage.py split` and `carin.py iso`) gives exactly the patched ISO.
4. OpenCarin notes: `carin/osm` and `carin/serializer` are **empty packages** in the cloned revision (`1e5e077`), so Phase 1+ has to write the OSM ingestion and the DB serializer. `carin/parser/volume_edit.py` (in-place block rewrite) matches what `phase0_patch.py` does. Its `carin` package name clashes with our `tools/carin.py` when both are imported; import ours under another name.

### Phase 1 (2026-11-05): OSM download, road model, size estimate
Package `tools/osm2carin/` (run from `tools/`: `py -m osm2carin regions | estimate | download | model | build`). Tests in `tests/test_osm2carin.py` (a synthetic OSM town, no download needed).
- **Regions:** Geofabrik `index-v1-nogeom.json` (cached 7 days). A region is named by its id (`us/california`), its name ("Rhode Island") or an ISO code (`US-NV`, `DE`). Downloads are cached in `out/cache/`, resumable, and MD5-checked against Geofabrik's `.md5`.
- **Road model** (`model.py`, pyosmium 4.3, filters in C++):
  - three passes: road ways + restrictions; node coordinates (IdFilter); POI nodes;
  - ways are split at shared nodes and way ends, and closed ways in the middle;
  - output: links with from/to node, shape, length (m), both bearings, class, subtype, form, direction, speed, junction type, toll/bridge/tunnel/unpaved, name and ref;
  - turn restrictions are mapped to (from link, via node, to link); restrictions via a way are dropped and counted;
  - Nevada takes ~65 s.
- **Original disc's Nevada street level [seen]:** 222,921 links, 135,904 km (`osm2carin.survey`, rough NV polygon). km by class:

  | Class | km | Speed category | Notes |
  |---|---|---|---|
  | 0 | 1,914 | 31 | |
  | 1 | 3,345 | 22 | |
  | 2 | 2,755 | 22 | |
  | 3 | 3,946 | 16 | |
  | 4 | 17,307 | 11, 16 on collectors | |
  | 5 | 621 | 5 | |
  | 6 subtype 1 | 106,017 | 11 | rural dirt roads |

  - Freeway ramps are class **3** (some 2/4), forms 1/2.
  - Forms 8/9/0xA occur on only 50 links; form 7 on 2,038 (OpenCarin: a compiler artefact, write 0xB/0xC).
  - One-way links use direction 1 and 2 about equally: the geometry is not reversed. Our model reverses the geometry and always writes 1.
  - 0xB marks classes 1–3 plus the speed-16 class-4 collectors.
- **Mapping chosen** (`attrs.py`):

  | OSM `highway` | Class | Speed category |
  |---|---|---|
  | motorway | 0 | 31 |
  | trunk | 1 | 22 |
  | primary | 2 | 22 |
  | secondary | 3 | 16 |
  | `*_link` ramps | 3 | 16 |
  | tertiary | 4 (form 0xB) | 16 |
  | residential, unclassified | 4 | 11 |
  | living_street, service=alley | 5 | 5 |
  | service, track | 6/1 | 11 |
  | private access | 6/3 | |

  - Speed is a default per road type, as on the disc; OSM `maxspeed` is not used.
  - Slip roles come from tracing along the ramp to the first main road at each end.
  - The default profile keeps:
    - tracks that are TIGER-imported, named or grade 1–3;
    - service roads, except driveways, parking aisles, drive-throughs, emergency access and private ones.
  - Result for Nevada: **130,810 km** (disc 135,904 km).
    - We give OSM `unclassified` (26k km, mostly TIGER rural roads) class 4, where Navtech used 6/1. That keeps them routable (the planner rejects class 6).
    - OSM has 2× the links (more junctions) and 2.4× the shape points per link.
- **POIs:** a whitelist of navigation categories (`poi.py`: fuel, parking, car dealers, hotels, restaurants, hospitals, police, airports, casinos…). Nodes and area centroids carry name, brand, phone and address. Everything else (benches, bins…) is counted as dropped. Nevada: 27,357 POIs. The disc's 0x10 has ≥160,240 POIs (distinct phone numbers).
- **Size model** (`estimate.py`, `calibration.json`):
  - Original CF=1 street parcels, least squares over 1,212: **21.5 B/link + 3.14 B/shape point + 1,348 B/parcel** (≈125 links per parcel; error 13%).
  - Per-link share of the other blocks that scale with the network (0x01–0x04, 0x08, 0x09, 0x0E, 0x0F): 60.3 B.
  - POI blocks: 246 B per POI.
  - Everything else is fixed: 36.6 MB.
  - The estimate before download uses Nevada's links/shape points/POIs per extract byte. A 15% margin is added.
  - Sector overhead outside `carindb`: 366 + 150 + 150 + 300.
  - Nevada: ~102 MiB → 16% of a 74-min CD-R. California (1.27 GB extract, model in 8 min):
    - 4,011,100 links, 675,258 km, 16.9 M shape points, 299,142 POIs, 65,836 / 70,878 restrictions mapped;
    - **~587 MiB → 90.5% of a 74-min / 83.7% of an 80-min CD-R**, so it fits, close to full. The extract-size estimate before download said 706 MiB, ~20% high: Nevada has more road per extract byte than California.
    - California + Nevada together, like the original disc, would come to about 648 MiB: at the 74-min limit, ~92% of an 80-min disc. That needs multi-region input, which isn't built yet.

### Phase 2 (2026-11-05): road tiles for levels 0–3
Code: `tools/osm2carin/tile26.py` (DB-REL 26 tile model), `tiles.py` (OSM model → tiles), `link.py` (block IDs, cross references, encoding), `validate.py` (checks, viewer export), `fmt.py` (record-size table). CLI: `py -m osm2carin tiles --region …`. Tests: `tests/test_tiles.py`.

**Tile layout at DB-REL 26 [verified: parse → build is byte-identical on all 38,326 road tiles of the disc, plain and CF=1 decoded].**
- Sections follow each other in the order 0 1 2 3 4 5 6 7 9 8 10 11 12 13 14, then the text blob. Each starts at the end of the previous one.
  - Sections holding u32s (6, 8, 9, 10, 13) are 4-aligned; the others are 2-aligned.
  - An empty S13 (and S14 in coarse tiles) has offset 0 and takes no space.
- S3 and S4 hold count + 1 records.
- Pointer fields (absolute offsets):
  - S4 link `+0/+2` S5|S6 node, `+4` S7, `+6/+8` next link at start/end node, `+0x12` S10, `+0x14` S12;
  - street only: `+0x18` S2, `+0x1C` S11;
  - S5/S6 `+4`, S3 `+0/+2`;
  - S0 `+0` text, `+4` S14;
  - S1 `+0/+2` text, `+4` S0, `+6/+8` S14;
  - S2 `+0` street name, `+2` alternate locality, `+4` S1, `+6/+8` S14;
  - S14 `+0` text;
  - S11 `+0/+2` text.
  - A few fields hold non-pointer values (S2 `+8` = 4 in CF=1 tiles); these are kept as literals.
- Street link record (30 B), beyond OpenCarin's table:
  - `+0x11` high nibble always 2; `+0x16` always 0;
  - `+0x1A` = 0x10 | bridge bits;
  - `+0x1B` = built-up (same as `+0x0A` bit 7) | width << 1 | house numbers.
- Coarse links are 24 B: up to `+0x16`, no names.
- Node flags (S5/S6 `+6`):
  - bits 15–14 = 3 − the highest coarse level the node reaches;
  - bit 12 S5, bit 13 S6;
  - bits 8–11 by degree: S5 1 / 5 / 0; S6 with one link 5.
  - Checked on 50k nodes.
- Ordering rules, all holding on the disc:

  | Rule | Holds |
  |---|---|
  | Link start node has the lower (u, v) | 73,686 / 73,686 sampled |
  | Links grouped by class in S3 | always |
  | S5 nodes sorted by (u, v) inside each group | always |
  | Node cycles clockwise by leaving bearing, from the smallest | 99.96% |

- Header words:
  - street tile `+0x54` = its house-number block (0x04), `+0x58` = its 0x03 parent, `+0x5C` = 0;
  - street tile `+0x60/+0x64/+0x68` = three u32s, **meaning unknown** (they correlate with the longest link, r ≈ 0.5–0.66). Coarse tiles hold 2,147,483 there;
  - street tile `+0x6C` u16 = 5–17, **unknown**; `+0x6E` = 10; `+0x70` = 0x0101 with house numbers, else 1;
  - coarse tile `+0x58` = the next coarser tile (0 on 0x01), `+0x5C` = the S8 grid cell size, `+0x70` = 1.
- Tiles:
  - Street tiles are 81,920 × 40,960 up to 1,310,720 square; coarse up to 2,621,440. Sides are 40,960·2ᵏ, w = h or w = 2h.
  - Corners lie on (16384, 1280) mod 40,960. Bigger tiles are *not* aligned to multiples of their size.
  - Decoded street tiles are at most 8 units, coarse at most 5.
  - Sub-revision 8 stores the S6 twin offset in 14 bits, so a twin's S6 record must start below 16,384.
- Levels: 0x03 keeps classes ≤ 3, 0x02 ≤ 2, 0x01 ≤ 1. S8 of a coarse tile is a column-major grid of next-lower-level BLOCK_IDs.
- Names in a street tile:
  - S0 = [null, country (`+2` = 0xE0 USA), end];
  - S1 = [null, city (+ county), end];
  - S2 = one record per street name (shared by its links), sorted;
  - S14 = country + 5 other-language names, city, streets (type 0x08; 0x10 for route numbers), county, localities;
  - the blob ends on a NUL.
- Physical order on the disc:
  1. per road level, its 0x09 spatial-index cells, then the tiles (street tiles, then house numbers 0x04);
  2. 0x03, 0x02, 0x01;
  3. the background layers.

**What `osm2carin tiles` writes.**
- Tiling:
  - The same tree of alternate halves on the same grid. Street tiles split until the estimate fits.
  - Shapes are simplified at 2 m (5/10 m in tiles that are still full).
  - Links are cut at tile edges into twin S6 edge nodes. Coarse tiles are unions of street tiles, so coarse edge nodes are street edge nodes.
- Coarse links: chains through nodes where only two kept roads with the same class/speed/form/direction meet, simplified at 20 / 60 / 150 m.
- Records follow every rule above:
  - node cycles, node flags and S3 groups;
  - S10 turn bans from OSM restrictions (owner = from link; flag 0/1 = the owner's start/end node; `only_*` bans every other exit);
  - names in the original's layout, ASCII-folded to what the CF=1 text code can carry (no `!"#$%&`).
- Street tiles are stored CF=1 (OpenCarin `encode_type00`), coarse tiles plain. Block IDs are allocated in the disc's order.
- Checks on every run: CF=1 decode = what we encoded; tile26 round trip; all rules; geometry read back with `tools/carin.py`; twins; coarse nodes on street nodes; parents contain children.

**Nevada:**
- **6,380 tiles in 15,246 units (29.8 MiB):**
  - 4,939 CF=1 street tiles (13,680 units);
  - 859 / 469 / 113 tiles for 0x03 / 0x02 / 0x01.
- Every check passes.
- In the densest Las Vegas tiles (already the smallest size on the disc) the tile has to shed links:
  - 16,153 class-6 pieces dropped (service roads, which the planner can't route on anyway);
  - 164 class-5 pieces dropped;
  - 655 class-4 pieces dropped (0.13% of links).
- About 25 minutes, mostly CF=1 encoding.

**Assumptions to test on the unit** (not on the disc in this form):
- the three unknown street-tile u32s, set to the coarse tiles' 2,147,483;
- `+0x6C` = 14;
- the built-up bit (tile ≤ 327,680 and class ≥ 2);
- one "city" per tile (the region name) until Phase 3 brings real admin areas;
- S12 (6-byte entries on the disc, probably TMC) left empty;
- no house numbers (`+0x54` = 0).

### C# port (2026-11-05)
`csharp/` holds the same pipeline in C#, with its own PBF reader and a port of OpenCarin's CF=1 type 0x00 codec. It produces the Python tools' output byte for byte:
- the Nevada road model, in every array;
- the Nevada road tiles, all 6,380 blocks, on disc and decoded.

The checks against the original disc pass too:
- the `.bin` rebuild;
- the CF=1 codec, matching OpenCarin on 480 tiles and round-tripping all 24,028;
- the tile model, round-tripping all 38,326 road tiles.

Exact parity needed three things reproduced exactly: numpy's pairwise float sums, CPython's `math.hypot` (Douglas-Peucker near-ties), and Python's floor-modulo and rounding. Nevada:

| Step | Python | C# |
|---|---|---|
| Road model | 55 s | 7 s |
| Tiles + link + checks | ~25 min | 18 s |

Each street tile is CF=1-encoded once: block IDs only sit in fixed-width fields, so they are patched in after allocation.

**California in C#** (1.27 GB extract):
- road model in 46 s: 4,011,475 links, 299,219 POIs;
- road tiles in 95 s: **49,366 blocks, 97,716 units (190.9 MiB)**, every check passing;
- the fullest tiles dropped 79,789 class-6, 3,429 class-5 and 1,325 class-4 pieces (0.03% of links).

Python would take an estimated 3–4 hours for the tiles.

Found on the way: our tiles leave decoded bytes 6/7 (the two CF=1 bit widths) at 0. The encoder then writes every link length and coordinate delta as a 16-bit escape. Choosing the widths per tile, as the disc does, would shrink the street tiles noticeably. Not done yet, so that both implementations stay identical.

### Phase 3 (2026-11-06): smaller tiles, the disc's quadtree, the spatial index
- **CF=1 widths.** Decoded bytes 6/7 of a street tile are the two adaptive bit widths: one for link lengths, one for coordinate deltas.
  - The disc's tiles use about 8–12 and 6–9.
  - We now choose them per tile by the encoder's exact bit cost (`choose_widths`, `Link.ChooseWidths`).
  - Street tiles shrink about 15%: California's road tiles go from 190.9 to 167.7 MiB.
  - Python and C# stay byte-identical (Nevada, Monaco).
- **One quadtree [verified].** 0x07's layer directory gives every layer the same root square: corner (1,240,326,144, −171,088,640), side 671,088,640 = 40,960·2¹⁴, covering North America.
  - Every tile sits at a multiple of its own size from that corner. The earlier note "bigger tiles are not aligned" measured from 0, not from the root.
  - Our tiles are now cells of that quadtree (`DISC_ROOT`; outside it, a size-aligned square on the same grid).
- **Spatial index at DB-REL 26 [verified].** It is as OpenCarin documents for the DVDs (02-geo §7.3), with two differences on this disc:
  - 0x07 records are 24 B (`T[0x18]`) with two parameters. Parameter 0 is the highest road class a layer keeps: 6 / 1 / 2 / 3 for 0x00 / 0x01 / 0x02 / 0x03. The 12 records plus an 8-byte trailer fill `T[0x19]` = 296 bytes.
  - 0x08 blocks hold up to 4,092 entries (8 units).

  The disc's layers:

  | Layer | Cell side | Grid |
  |---|---|---|
  | 0x00 | 1,310,720 | 512² |
  | 0x06 | 2,621,440 | 256² |
  | 0x03 | 5,242,880 | 128² |
  | 0x02 | 10,485,760 | 64² |
  | 0x01 | 20,971,520 | 32² |
  | 0x14 | 167,772,160 | 4² |
  | 0x15 | 41,943,040 | 16² |
  | 0x16 | 20,971,520 | 32² |
  | 0x1C | 41,943,040 | 16² |
  | 0x1D | 335,544,320 | 2² |
  | 0x1E | 805,306,368 | 1² (on a root of its own) |

  Record 8 is empty (no 0x1B on this disc). The superblock's S0 record 0 is the BLOCK_ID of 0x07 (unit 2).
- **Our index** (`Roads/Spatial.cs`):
  - Given the disc's own 26,023 street tiles, it rebuilds all **9,073 of the disc's 0x09 cell nodes byte for byte**, and the lookup finds every tile.
  - `osm2carin tiles` writes each road layer as [0x08 grid][0x09 nodes][tiles].
  - Our coarse tiles have finer edges than the original's, so a coarse layer's cell side is halved until no cell needs more than 64 × 64 items (the disc's maximum).
  - Checked on every run: the index finds every tile at its centre (Nevada 6,415 / 6,415; California 49,372 / 49,372).

### POI categories (2026-11-05, OpenCarin issue #24)
Block 0x07 section 0 is the list of POI category codes on the disc (6 B records: code, name offset, 0; every name is empty, so the unit uses its own names). On CD-ID 5944 it holds **39 codes**, and the 0x06 POI records (20 B, `T[0x32]`, `+0x0A` category) use exactly those 39 (249,000 records).

Names: * = read on a CNI1 (issue #24); the rest come from the POIs filed under the code on this disc.

| Code | Category | Code | Category |
|---|---|---|---|
| 11 | auto services / dealers | 37 | landmarks* |
| 12 | petrol* | 38 | amusement |
| 13 | car rental* | 39 | parks* |
| 14 | parking* | 40 | convention / business facilities |
| 15 | park & ride | 41 | hospitals* |
| 16 | rest areas* | 42 | police |
| 17 | auto clubs (AAA) | 43 | city halls |
| 20 | tourist attractions | 47 | shopping* |
| 21 | hotels* | 48 | towns* |
| 22 | restaurants* | 50 | golf* |
| 23 | banks | 51 | rail* |
| 24 | civic centres | 52 | airports* |
| 25 | libraries | 53 | ferries* |
| 26 | courts | 54 | bus* |
| 30 | banks / cash (same POIs as 23) | 55 | marinas |
| 31 | tourist info* | 56 | schools / universities* |
| 32 | museums* | 57 | theatres, casinos, leisure* |
| 35 | sports* | 58 | border crossings |
| | | 59 | BMW dealers |
| | | 60 | same POIs as 11 |
| | | 61 | business / commercial |

`poi.py` maps our OSM categories onto these codes. OpenCarin `01-architecture.md` §4.4.2 documents the whole POI index:
- city category ranges (0x0C S3), brand ranges (S5);
- 0x11 letter tries, two 0x10 copies of branded POIs, the 0x06 map records;
- the writer's rules: names sorted inside each first-letter leaf; road levels linked near the car.

That index, written from scratch, was listed, searched and routed to on a CNI1. Phase 2b follows it.

### Still open
1. **Does the head unit check data integrity?** That could be a per-block CRC, a whole-DB checksum, or the CD-ID/BSW-REL list. The test images are built (Phase 0 above); the car test is pending.
2. **Writing a new disc (the OSM converter).** OpenCarin targets this ("compile OpenStreetMap data into bootable navigation discs") and includes a CF=1 encoder for type 0x0E, plus `encode_type00`. Next steps:
   - check its serializer against DB-REL 26 and 2048-byte units;
   - round-trip one of our parcels (decode → encode → decode);
   - then build a small test disc and try it in the car.
3. Fields not needed for display but needed for routing/search: turn restrictions, one-way bits and the name/search indexes (0x0C–0x11). OpenCarin documents most of these.
4. CARINET (RDS-TMC event catalogue) contents. Not needed for maps.

---

# History: hand reverse-engineering notes (rounds 1–9)

The sections below are kept as a record of the independent analysis. Where they conflict with the summary above, **the summary is correct**. Several early guesses were later revised (e.g. "not compressed", "records run across block boundaries", the "24-bit word count", "Nevada" for the Detroit parcel).

## Round 1: disc layout
- Track 1: MODE2/2352 data, 186,691 sectors, all Form 1 except the last 150 (the postgap). **[seen]**
- Track 2: short audio track (about 300 sectors / 4 s), starting at 41:29:16. Probably a "this is not an audio CD" warning track. **[inferred]**
- ISO9660 volume. System ID `CARIN CD_5944`, volume `NAV_DB`, volume set `CARIN`, publisher `Siemens VDO Automotive AG`, created 2002-11-13. **[seen]**

| File | Size | What it is |
|---|---|---|
| BIBLIOGR / PRODUCT | 140 | `CD-ID 5944`, `DB-REL 26`, list of compatible unit software versions (`BSW-REL 91.0 … 98 10`) **[seen]** |
| COPYRIGH | 321 | Siemens VDO copyright notice **[seen]** |
| ABSTRACT | 494 | Mastering info: tool `CDprepare V4.5`, nav DB `us1-10_hw_nt_ald_02I_20021105b.cpr`, says the disc also works with "CARiN on PC" and "CARSiM" **[seen]** |
| CARINET | 696,320 | RDS-TMC traffic-event tables. It contains Dutch traffic messages and is not the unit's software. **[seen strings / inferred purpose]** |
| carindb | 381,286,400 | The map database: 186,175 blocks of 2,048 bytes **[seen]** |

## carindb
- Big-endian, structured binary. **[seen]**
- **Not encrypted.** Entropy never goes above about 6.9 bits/byte, and around 1.5 million plain lowercase ASCII strings are present: place names, streets, POI names, phone numbers. **[seen]** *(Revised: about 30% of blocks, including 24,028 street parcels, are CF=1 bit-packed; see the summary.)*
- The metadata block at 0x838 includes `conditional access: false`, `supplier: navtech`, `conv tool version: blof_1.1_20021030`, `gdf version 3.0`, `rds/tmc included`, `db release number: 26`. **[seen]**
- Offset 0x0: a schema-like table of about 90 (record-type ID, size) pairs, IDs 0x01–0x5A. **[seen layout / inferred meaning]**
- Blocks 0–4 start with `00 00 NN 01`, where NN is the block number. Many later blocks don't have this header, so records seem to run across block boundaries. **[seen / inferred]** *(Revised: every block has the header. Blocks are 1–8 units long, so not every 2 KB unit starts one.)*
- Block 2 (0x1000) holds repeated 4×int32 groups such as `49EDE000 F5CD6500 71EDE000 1DCD6500`. These are probably bounding boxes, in 1e-7 degree units, of square quadtree tiles. **[inferred, not yet checked against a known location]** *(Revised: they are bounding boxes, but the unit is 1.8e-7°, with a −30° longitude origin.)*
- There is a nationwide US place index (for example `abbeville, al`), even though the detailed map is California/Nevada. **[seen]**

## Round 2: coordinates and map parcels (history)

### Coordinate system
- Coordinates are signed 32-bit big-endian integers. **[seen]**
- **Latitude = Y × 180 / 1,000,000,000**, so 1 unit = 1.8e-7°. Evidence: the root box's top edge `0x1DCD6500` = 500,000,000 = exactly 90°N; the densest clusters come out at 33.9°N (LA) and 37.5°N (Bay Area); the far-west tiles come out at 21.4°N (Honolulu). **[inferred, strong]**
- **Longitude ≈ (X − ~2.1667e9) × 180 / 1e9**, the same scale with an offset. The offset, fitted from the LA and Honolulu clusters, is about ±0.5° accurate. Pin it exactly against known road geometry. **[inferred]**
- X increases eastward and Y northward. **[seen in plot]**

### Map parcels (tiles)
- Map data is stored in **parcels**: square quadtree tiles of 40,960 × 2^k units, for k = 0…7, roughly 0.0074° to 0.94° across. **[seen]**
- `parcels.py` finds about 48,500 parcel boxes. `parcel-coverage.png` plots the small ones: detailed California and Nevada, plus city clusters across the US and Hawaii. **[seen]**
- Parcel header example: parcel at file offset 0x0800_0800. **[seen layout / inferred meaning]**
  - `+0x00` `01 0001 02`: byte 0 = ?, bytes 1–2 = parcel sequence number, byte 3 = **length in 2 KB blocks** (here 2 = 4,096 bytes).
  - `+0x06` u16: parcel type (seen 0x0103, 0x0104, 0x0105).
  - `+0x08`: about 16 (u16 offset, u16 count) pairs, one per section. The first offset is always 0x74, the header length.
  - `+0x44`: 4×int32 bounding box (x1, y1, x2, y2).
  - `+0x54…0x73`: unknown. It may describe the bit widths of packed fields.
- **Open problem: the section offsets go past the parcel's end.** For example, offsets reach 0x1F28 but the data ends at 0xF1F. That points to the body being **compressed (ratio about 2:1) or bit-packed**. Sections 0x74–0x114 read as plain 10-byte records; after that the bytes look bit-packed. Decoding this body is the next milestone, and the road geometry is inside it.

## Round 3: road network decoded (uncompressed levels), viewer working

### Block chain [seen]
Header layout from the xoutpost "data structure carindb" thread, confirmed here:
- `u24 start_sector, u8 n_sectors, u16 type, u8 compressed, u8 unpacked_sectors`
- Walking the chain from sector 0 covers all **96,187 blocks** with no gaps (`walk_blocks()` in `tools/carin.py`).
- Road parcel types: **0** = full detail (24,028 compressed + 1,995 plain), **1/2/3** = coarser road levels (all plain: 3,151 / 3,656 / 5,496). Each level-2/3 parcel header at +0x58 holds the block ID of its parent parcel.

### Calibrated coordinates [verified against OSM to within a few metres at zoom 17, Las Vegas & SF]
- `lat = Y × 1.8e-7`, `lon = X × 1.8e-7 − 390`
- Inside a parcel: `X = x1 + u × 64`, `Y = y1 + v × 64` (u, v u16; max 40,960 for full-size level-1–3 tiles)

### Parcel sections (levels 1–3, and plain level 0 so far as tested)
Section table at +0x08: 15 × (u16 offset, u16 count). Bounding box at +0x44.

| # | Content | Record |
|---|---|---|
| 4 | road links | 24 B: from-node off, to-node off, shape cursor, adjacency (2 words), attr (road class?), …, name ref |
| 5 | nodes | 8 B: u, v, first link off, flags |
| 6 | tile-edge nodes | 16 B: u, v, link off, flags, neighbour block ID (u32), neighbour offset |
| 7 | shape points | 6 B: u, v, flags. Link *i* owns `[shape_i, shape_{i+1})` (running cursor) |

A road is drawn as from-node → its shape points → to-node.

### Viewer
`run-viewer.bat` extracts the disc (first run only), exports `viewer/data/level{1,2,3}.json`, and serves `viewer/index.html` at http://127.0.0.1:8738/. It uses Leaflet with an OSM basemap underneath for comparison. Levels 1–3 give highways and arterials (about 730k links). **Residential streets are only in level 0**, which is mostly compressed.

### Level-0 compression: OpenCarin (external reference)
https://github.com/fdemusso/OpenCarin (MIT licence, last commit 2026-10-09) documents and implements the CF=1 decoder, recovered from head-unit firmware (CC-93 `pbp` m68k, Mk3/RR `db_pub` MIPS). Key points that match and explain what's above:
- CF=1 is **structure-driven bit-packing** (MSB-first). Each section has its own field decoder. Pointers are stored as record **indices**, with width `bits_needed(count+1)`; `PTRBITS = bits_needed(usize × sector size)`.
- Record sizes come from the superblock `RECORD_SIZE_TABLE`, the (id, size) table at the start of `carindb`.
- The two plain bytes after the 0x74 prologue are "two adaptive widths per block" (our b0/b1).
- Coordinates: first record absolute 16-bit, then a 1-bit absolute/delta flag, a sign bit and a `widths[1]`-bit magnitude.
- Street names are a prefix-coded text blob at the end of the stream (2-bit class + 1/2/3/7-bit codes, plus a 6-entry per-block dictionary). Readable names are the validation check.
- Type 0x00 is decoded in **passes by format revision**: 0x14 (DB-REL 20), 0x15 (21), 0x17 (23), 0x1B (27). **Our disc is DB-REL 26 (0x1A): passes 0x14, 0x15 and 0x17 apply.**
- A sub-revision switch (14 vs 16 bits in section 6) is detected from the data (`CarinVolume.calibrate()`). CD images use 2048-byte units, which it detects.
- Their frame: lon = x/K − 30, lat = y/K, with K = 2e9/360. This is the same as ours (lon = x × 1.8e-7 − 390, a full turn apart).
- Relevant files: `carin/parser/cf1/decoder_00.py` (`decode_type00`), `core.py`, `iso.py` (`CarinVolume`, `open_image`), docs `docs/carindb/04-cf1-codec.md`.
- **Run against this disc (2026-11-04): it works.** `CarinVolume` detects DB-REL 26 and 2048-byte units, and `calibrate()` picks sub-revision 8. Test tiles decode with correct street names: downtown LA (0x132A1: "los angeles, ca", bartlett st, ca 110 ramps…), Hayward (0x10001), Detroit (0x1C8C2), Columbus NE (0x1A2A2). A 300-parcel trial had 0 failures. The decoded buffers use the same layout as plain level-0 parcels (30-byte links, per the disc's record-size table), so `carin.Parcel` reads them unchanged.
- `tools/carin.py streets` uses it to build `viewer/data/l0/<ix>_<iy>.json` (0.1° cells, integer 1e-5° delta coordinates). `run-viewer.bat` clones OpenCarin into `third_party/` when needed.

### Level-0 compression: round 9, link grammar refined; generality survey
Code: `tools/scratch/level0/linkgrammar.py` (`parse_links`), `survey.py`.

**Grammar (current):**
```
from PW | to PW | X(1) | cursor: X ? 12 bits : 3-bit delta | P1 14 | LEN (b0 bits) | h0 8 | h1 8
fA [A 6] | fB [B: BW bits] | fC [w5 16, w8 16, w13 16]
```
- **LEN width = b0**, the first plain byte at parcel offset 0x74 (10 in 0x1C8C2, 9 in 0x194DA). The coordinate delta width is b1.
- **B width varies by parcel:** 16 bits in 0x1C8C2, 19 in 0x194DA. B looks like a packed counter (it steps by 64 or 1), probably a name reference. The rule for its width is unknown.
- Road-class groups come from the raw sec3 pairs (e.g. 0x1C8C2: 2 / 77 / 103 / 31 / 2 links). One grammar covers all groups.
- 0x1C8C2: **214 of 215 links parse in sequence** from bit 6,516. The last record (bit 33,213) is cut short: only 27 bits remain where 48 are expected for C.
- 0x194DA: 83 consecutive records with BW = 19. It then hits a record with **113 extra bits between B and C**, another variable-length optional field (D) that isn't identified yet.
- Small parcels (0x1A2A2) use 11-bit byte-offset pointers and don't fit this grammar at all yet.
- **Survey: 0 of 40 random compressed parcels decode end to end** with the current grammar (links + nodes). The grammar is not general enough yet.

**What blocks a general decoder:**
1. The rule for B's width (and possibly other per-parcel widths). Look for it in the header words at 0x54–0x73 or derive it from section sizes.
2. The optional field D and its flag.
3. Small-parcel pointer width and record variant.
4. The node predictor's starting values.
5. Edge records and the shape stream (not started).

### Level-0 compression: round 8, link record grammar (partial)
Parcel 0x1C8C2. Measured against 206 record gaps from a heuristic chain of record starts. **[seen]**

```
from 13 | to 13 | X (1)
if X: cursor 12
block 24 = [13–14 bits ?][LENGTH 10–11 bits, metres]
heading 16 = start byte, end byte (+128)
if X: fA (1) [A: 6 bits]
fB (1) [B: 16 bits]     – increasing value, probably the name/table reference
fC (1) [C: 48 bits]     – three attribute words, e.g. 1a10 0120 1004 / 1a20 0120 1904
```
- Each optional field is present only when its flag bit is 1; 0 means "same as the previous link".
- This grammar matches **116 of 206** records.
- Known gaps:
  - Some X = 1 records have 13 more bits after C (159/165-bit records). Perhaps a name offset that's only written when it changes.
  - X = 0 records often carry 4 extra bits before fC.
  - From about bit 16,989 a **second record family** appears (other road classes; attribute words such as `2201`) with a different tail layout. Typical length 121 bits.
- **Length field:** for all 11 perfectly straight links, the 10-bit value at block bits 14–23 equals the ground distance between the end nodes (± 2 m), using 1 local unit = 1.282 m × (cos(lat) for u).
- The first 13–14 bits of the block are not link pointers (tested as byte/word offsets). Possibly adjacency stored as small link-index codes.

### Level-0 compression: round 7, link records located and drawn
Reference parcel 0x1C8C2 is in **northwest Detroit, MI** (Southfield Fwy / I-96 / Davison), not Nevada as earlier notes said. Bit positions count from parcel offset 0x74.

**Link record (sec4)**, starting about bit 6,516 and running to the node section at 33,328: **[seen]**
- `from` and `to` node pointers, **13 bits each, as word offsets (byte offset ÷ 2)** in this parcel (unpacked size > 8 KB). The small parcel 0x1A2A2 uses byte offsets in the same 13 bits. (402 adjacent valid pairs against ~90 expected by chance.)
- 1 flag bit. If set, a 12-bit increasing value follows (6, 14, 24, 42, 59…, reaching 723 by link ~35). It's monotonic, but too large to be a shape-point index (449 points); possibly a bit offset into the shape stream.
- A **58-bit block**. Bits 24–39 are the **two heading bytes** (w7): start heading and end heading + 128, 256 steps per circle. These are exact for straight links (N5→N8: 127/255; N2→N5: 126/254). Bits 0–23 probably hold length and adjacency, not split yet. The last ~18 bits hold an increasing counter (name/table reference).
- An optional 6-bit field (104/152-bit records).
- 1 flag bit. If set, **three literal 16-bit attribute words** follow, e.g. `1a10 0120 1004` or `1a10 0120 1604`, the same words as w5/w8/w13 in uncompressed links.
- Common record lengths: 98 (base), 104, 121, 136/137, 146 (+attributes), 152.
- In uncompressed parcels, **w6 = link length** (≈1.2–1.27 × local units, i.e. metres) and **w7 = heading bytes**. Links run in chains along roads.

**Viewer test:** a heuristic scan for record starts found 207 of 215 links, and 183 were drawn as straight lines between decoded nodes (links to edge nodes left out). Most follow the freeways in OSM exactly. A few long false diagonals come from mis-detected record starts, which an exact grammar will remove.

**Remaining for level 0:** exact link grammar (bits 0–23 of the block, the optional 6-bit field), the edge-node coordinates, the shape-point stream (probably the `1100`-literal + 20-bit-delta rows after the edges), the predictor starting values (u+66 in this parcel's first node run), and names.

### Level-0 compression: round 6, node section decoded
Reference parcel: sector 0x1C8C2 (Detroit; 215 links, 164 nodes, 22 edges, 449 shape points; b0 = 10, b1 = 7; local tile size 10,240, so W = 14). Bit positions here count from parcel offset 0x74.

**Value code, used for node coordinates** **[seen, verified on 113+ known junctions]**
- `0` + D bits: previous + value
- `10` + D bits: previous − value
- `1100` + W bits: absolute value
- **D = b1** (the second plain byte at parcel offset 0x75). Confirmed: D = 7 in 0x1C8C2 and D = 11 in 0x1A2A2.

**Node record (sec5)** = `u-code, v-code, link (LW bits), F (1 bit), [if F: 11 bits]` **[seen]**
- LW = bits needed for the link count (8 for 215 links, 5 for 30).
- F = 1: the 11 bits are **flags high byte (8) + 3-bit low group**, e.g. `11010101 001` = 0xD501. F = 0: flags unchanged from the previous node.
- Nodes are sorted by u within runs. A new run starts with a literal u (the value drops).
- 0x1C8C2: nodes occupy bits **33,328 to 39,443**, exactly 164 records (26 delta-coded from the start, then the first literal at 34,366).
- 0x1A2A2: 26 nodes at bits 3,420 to 4,288. Plotted over OSM near Columbus, Nebraska, they sit on the section-line road grid.
- **Open issue: the predictor's starting value isn't always 0.** In 0x1C8C2 the first run's u must be offset by +66 to match known junctions (7/10 then match). Where 66 comes from is unknown: not in the header words or the raw sections. It may be carried over from a preceding section.

**Edge-node records (sec6)** follow straight after the nodes. Each contains the neighbour's 32-bit block ID (matching sec9 entries) followed by a 14-bit offset into that neighbour (e.g. 0x1A2C). The link/flags/coordinate parts are not split yet. **[seen]**

**Link records (sec4)** come before the nodes. In 0x1A2A2 (node offsets fit in 11 bits) rows start `[2][from 11][to 11, low bit = flag][1]`. In 0x1C8C2 only 40 of 215 rows contain two absolute 14-bit node pointers, so from/to are probably delta- or order-coded there. **Not cracked.**

**Unidentified region:** after the edges in 0x1C8C2 (bits ~41,046–52,224) come rows of `1100+u, 1100+v, 3 bits, 0–3 × 20-bit chunks`, 39/59/79/99 bits long. Their coordinates follow the main highway, often in carriageway pairs. They may be sec11/12-related, or shape anchors.

### Level-0 compression: round 5 findings (partially cracked)
Scripts: `tools/scratch/level0/`. Bit positions below count from the start of the bitstream (the `80 00 00 00` marker) unless noted.

**It is a schema-aware coder, not a generic compressor.** Each section is coded row by row, using fixed-width fields whose widths depend on the parcel, plus prefix-coded variable fields. Predictable columns are left out.
- **Pointer width P** = bits needed for the unpacked buffer size: 13 for 2 sectors, 13 for 3, 14 for 5. **[seen]**
- **Section 0 sits in bits 0–50** (in the 2-sector examples). The value I earlier read as a "24-bit word count" is really the 13-bit **string-pool start offset** (0x758 in sector 0x1A2A2, which equals sec14 end). **[seen]**
- **Section 1** (sector 0x1A2A2, 5 entries): 36-bit rows = `[11-bit string offset][1][11-bit field][13-bit parent pointer]`. The parent pointers decode to exactly 0x74, 0x7E, 0x7E, 0x7E, 0x74. Columns that only count up (+4 per row) are omitted. **[seen]**
- **Repeat flag:** a pointer's low bit (offsets are always even) set to 1 means "next row repeats this one". That next row is then coded in about 13 bits (sector 0x16656, sec1 entry 1→2). **[seen]**
- **Section 2** follows straight after, with the same row shape for its first rows (parent pointers 0x92, 0xB0, 0x9C), then changes form. **[seen]**
- **Links (sec4)** (sector 0x1A2A2, bits ~692–3080): rows of 45–99 bits = `[2 bits][from-node, 11 bits][to-node, 11 bits; low bit = flag][1-bit flag][variable rest]`. Example chain: N7→N8→N9→N10→N11→edge E0. Many 64-bit rows end in the same 15 bits (`000001100000000`), probably the default attributes. The node-pointer width (11 bits) is set by the node section's size. **[seen]**
- **Node coordinates:** literal-coded as **`1100` + 14-bit u, `1100` + 14-bit v** (14 = bits for a local tile size of 10,240). Confirmed against 18 junctions shared with levels 1–3 in sector 0x1C8C2 (164 nodes, 156 known from coarser levels). Literal pairs cluster in bits ~34,366–52,224 (from 0x74). Some rows are followed by 3 bits plus 20-bit chunks, and the paired coordinates look like the two carriageways of a divided highway. Nodes that aren't literal-coded presumably use shorter delta codes. **[seen / inferred]**
- **Shape points are not shared with levels 1–3:** only 237 of 127,656 plain level-0 shape points occur in L1–3, which store simplified geometry. So shape deltas can't be confirmed from coarser levels. Fixed-width and Rice-coded searches found nothing, as expected given this. Node ends do match (24,431 / 64,618). **[seen]**

**Next steps for level 0**
1. Fully parse sector 0x1C8C2's node section. Walk literal rows, work out the codes between them (link pointer and flags per node), and find how non-literal nodes are coded, using the 156 known node positions.
2. Use the decoded node table to fix the link rows (from/to are node pointers) and measure their lengths. That separates the shape-count field from the attributes.
3. Shape points: take links whose ends are known nodes. The delta stream between them must sum to (to − from), which gives a self-check for candidate codings.

### Level-0 compression: round 4 findings
Worked example: parcel at file offset 0x0800_0800 (sector 0x10001, 2 sectors, unpacked 5).
Its section table: 0:(0x74,3) 1:(0x92,4) 2:(0xba,59) 3:(0x308,7) 4:(0x328,181) 5:(0x187c,117) 6:(0x1c24,32) 7:(0x1e24,22) 9:(0x1ea8,4) 10:(0x1ec8,12) 14:(0x1f28,67).

**Unpacked level-0 layout** (read from plain level-0 parcels, e.g. sector 0xF2D6) **[seen]**
- sec0/1/2: 10-byte admin-hierarchy entries (country → state/county → city). Columns include a parent pointer (0x74/0x7e…, 0x92/0x9c…), string-table offsets and sec14 offsets.
- sec3: 8 pairs of (link-range start, node-range start).
- sec4: links, **30 bytes = 15 words**: from, to, shape cursor, adjA, adjB, attr (`1f20`/`1f10`…), ?, ?, `0120`, sec10 off, sec14 off, 0, ?, ?, sec11 off. There are count+1 records (sentinel last). Node link pointers point to record start.
- sec5 nodes 8 B, sec6 edge nodes 16 B, sec7 shape points 6 B: same as levels 1–3.
- sec9: 8 B neighbour-parcel records (block ID + edge position pair). sec10/11: 8 B (block ID, link offset, direction).
- sec14: 4 B name index (u16 string offset, u8 lang/type, u8). Plain-text strings follow sec14.

**Compressed stream layout** (after the 0x74-byte header, which is stored plain) **[seen]**
1. 2 bytes: b0 (6–13) and b1 (4–12). b0 rises with shape-point count and b1 falls with it, so they are probably Rice/Golomb-style coding parameters. **[inferred]**
2. 32 bytes: sec3's 8 pairs, stored raw.
3. sec9 then sec10 records, stored raw (8 B each, counts from the header). In 18,984 / 24,028 parcels, the next 4 bytes are `80 00 00 00`. The rest have further raw 8-byte sections (sec11/12) first.
4. A bitstream, starting at that marker:
   - bits 32–55: **24-bit count of 16-bit words in the unpacked parcel, up to the end of sec14.** Parcel 0x0800_0800: 0x00101A = 4,122 words = 0x2034 bytes = exactly sec14 end. Strings are not included. **[seen]**
   - then a nearly constant run (`00 0080 0003 a…` with a variable number of zero bits), then a 16-bit value about equal to the stream's length in bits (0x3159 = 12,633 vs about 12,360 measured). **[inferred]**
   - later: an increasing list of 15-bit values (55 in the example: 621, 649, 677 … 2718, steps 20–80). Probably bit offsets of compressed strings. **[inferred]**
- Ruled out: deflate/zlib at any offset. Fixed-width 11-bit (u,v) node records at any stride from 22 to 80 bits. LZ-style text (strings never even partly visible in any compressed block type).
- The other compressed block types (0x0e, 0x14–0x1e: water and land-use polygons, "pacific ocean") use the same scheme. Their streams contain long runs of 1-bits (`5fff ffff`), which is consistent with unary-coded parts.

**Suggested next steps**
- Decode the bitstream header fields exactly, using the parcels whose prefixes differ by only a few bits (e.g. compare 0x0800_0800 with the parcels at sectors 0x12CED, 0x159A8 and 0x11D8F).
- Test Rice/Golomb decoding with k = b0 or b1 on column-wise deltas of the expected sec0 contents (mostly zeros plus a few offsets).
- Look harder for a reference decoder ("CARiN on PC", CARSiM, VDO Dayton PC tools). A head-unit firmware dump (EPROM) would settle it outright.

### Level-0 compression (round 3 notes)
- Not deflate/zlib at any offset. Dense regions are dominated by bytes with few bits set, which suggests custom variable-length bit packing.
- The stored stream does **not** follow the unpacked section layout. It starts with `08 08` + 8 (u16, u16) pairs equal to unpacked section 3, then 8-byte neighbour references (block ID + offset), then bit-packed data. So the compressor re-encodes per section rather than running a generic algorithm over the whole parcel.
- The 1,995 plain level-0 parcels can provide known plaintext for building a decoder.

## Round 1 open questions and leads (superseded by "Still open" in the summary)
1. Head-unit integrity check: **still open**.
2. Coordinate encoding and the tile/quadtree index: **solved** (coordinates exact; tiles and block chain decoded).
3. Road network records: **solved for display** (links, nodes, shape points, length, heading, attributes). OpenCarin documents turn restrictions and one-way bits.
4. Name and address indexes: street names decode. OpenCarin documents the search indexes (0x0C–0x11).
- The "CARiN on PC" / firmware lead was the right one: OpenCarin recovered the CF=1 decoder from head-unit firmware.
