# Prompt: OSM → CARiN map disc builder for the Range Rover P38

Paste everything below the line into a new Claude Code session opened in `C:\Git\CARiN_P38`.

---

## Goal

Build a command-line tool that:

1. lets me pick a **US state (or a country)**;
2. downloads the matching **OpenStreetMap** extract;
3. converts it into the CARiN road levels: **level 0** street parcels (block type 0x00) and **levels 1–3** coarser road parcels (types 0x01–0x03), in the same layout as the original disc;
4. repacks everything into a **CARiN database** (`/carindb` + companion files);
5. outputs a **burnable `.bin` + `.cue`** pair that ImgBurn can write to CD-R, for my 2001 Range Rover P38's Philips/VDO CARiN navigation unit.

It must **estimate and check the output size early** and warn me clearly when the result won't fit on a CD-R. It should also suggest how to make it fit (smaller region, fewer layers).

## What we already know (read these first)

- **`carin-disc-findings.md`**: complete notes on the original disc. Read the **"Current status"** section first; the history below it is background.
- **`tools/carin.py`**: our reader.
  - `iso`: raw `.bin` → ISO.
  - `extract`: ISO files.
  - `export`: road levels 1–3 → viewer JSON.
  - `streets`: all street parcels → viewer cells, via OpenCarin.
  - `Parcel` parses the unpacked road-parcel layout: sections 4/5/6/7 = links/nodes/edge nodes/shape points.
- **`viewer/`**: Leaflet viewer (`run-viewer.bat`). Use it to compare our output against the original disc and OSM.
- **OpenCarin**: https://github.com/fdemusso/OpenCarin (MIT). It already decodes **and encodes** CARiN blocks (`carin/parser/cf1/decoder_00.py` has `encode_type00`; there's also `encode_type0E`, a serializer package and an OSM ingestion package). Its stated goal is OSM → bootable discs. Read `README.md`, `docs/CARINDB_BLUEPRINT_EN.md` and `docs/carindb/*.md` (especially `01-architecture.md`, `03-road-network.md`, `04-cf1-codec.md`, `06-objectives-roadmap.md`) and the `carin/osm` and `carin/serializer` packages before writing anything. **Reuse it rather than re-implementing**; extend it where it falls short, and keep its MIT notice. `run-viewer.bat` clones it into `third_party/OpenCarin`. You have my permission to run its code.

**Key facts about the original disc** (CD-ID 5944):

| Fact | Value |
|---|---|
| DB-REL | 26 (0x1A) |
| CF=1 sub-revision | 8 (superblock `+0x22`); means 14-bit width in section 6 |
| Units | 2048-byte, single `/carindb` |
| Supplier / build | Navtech 2002, GDF 3.0 |
| Coordinates | `lat = Y/K`, `lon = X/K − 30`, K = 2e9/360. Inside a parcel `X = x1 + 64·u` |
| Block header | `u24 start unit, u8 units, u16 type, u8 flags (bit0 = CF=1), u8 unpacked units` |
| Record sizes | from the superblock `RECORD_SIZE_TABLE`; 30-byte links for type 0x00, 24-byte for 0x01–0x03 |

- **Original image layout:**
  - Track 1: `MODE2/2352`, 186,691 sectors, data in Mode 2 Form 1 (2048 bytes user data), last 150 sectors Form 2 postgap.
  - Track 2: short AUDIO track (about 4 s) after a 2 s pregap.
  - ISO9660 volume: system ID `CARIN CD_5944`, volume `NAV_DB`, publisher `Siemens VDO Automotive AG`.
  - Root files: `BIBLIOGR`, `PRODUCT`, `COPYRIGH`, `ABSTRACT`, `CARINET`, `carindb`.
- `BIBLIOGR`/`PRODUCT` list the unit software versions the disc claims to support (`BSW-REL 91.0 … 98 10`). The head unit may check these, the CD-ID or the DB-REL.

## Hard constraints and unknowns (treat as risks, test early)

1. **The head unit has never been shown to accept a modified disc.** Phase 0 below exists to find out cheaply before the big build.
2. **Keep the original's format exactly:** DB-REL 26, sub-revision 8, 2048-byte units, the same superblock/record-size table, the same companion files, the same ISO volume identifiers and the same track structure. Change content, not format, unless a test proves otherwise.
3. **The unit needs more than road geometry.** Without the following, the car will display roads but not route or search:
   - spatial index (0x07 → 0x08 → 0x09);
   - name/search tries (0x0B, 0x0C, 0x0D, 0x0F, 0x0E street directory, 0x10/0x11 POI);
   - house numbers (0x04);
   - routing topology (adjacency, turn restrictions, one-way, road class/speed);
   - background layers (0x14–0x1E);
   - TMC tables (0x17/0x18; can stay empty).
   Plan these in phases. Document which ones the unit strictly requires to boot and draw a map, and which are needed for routing and destination entry.
4. **Burnable output must be a real Mode 2 Form 1 raw image:** sync, MSF header, subheader, and **correct EDC and ECC (P/Q parity)** in every 2352-byte sector. Wrong ECC = unreadable disc. Verify by:
   - re-reading our generated `.bin` with `tools/carin.py iso`;
   - checking EDC/ECC with an independent implementation (or by regenerating the original image's sectors from its own user data and getting byte-identical output);
   - generating the `.cue` in the original's format, with the audio track copied over unchanged.
5. **Capacity checks.** A CD-R holds 74 min = 333,000 sectors or 80 min = 360,000 sectors. At 2048 user bytes per sector that's about 650 / 703 MiB of data, minus the ISO overhead, companion files, the audio track and pregaps. The original uses 186,691 sectors (about 365 MiB).
   - Before downloading, estimate the output size from the OSM extract size, using ratios measured on our own conversions (start with the original: California + Nevada ≈ 3.0 M street links in about 340 MiB `carindb`).
   - After building, report the exact sector count and the percentage of a 74-min and an 80-min disc.
   - Refuse to write a `.bin` that won't fit, unless a `--force` flag is given.

## Plan (do it in phases; stop and report after each)

**Phase 0: prove a modified disc can work** (no OSM yet).
1. **Raw image round trip:** rebuild the original `.bin` from our extracted ISO (regenerate every sector's sync/header/EDC/ECC and the cue). It must be **byte-identical** to the original `.bin`.
2. **Block round trip:** decode → re-encode → decode on a sample of original CF=1 type 0x00 parcels with OpenCarin, at DB-REL 26 / subrel 8. Decoded output must match. Report whether re-encoded bytes match the original, or only match after decoding.
3. **Patch test disc:** change one street or POI name in place (same length), rebuild the image, and produce `test-patched.bin/.cue` for me to burn and try in the car. Write down exactly what to look for on the unit's screen.

**Phase 1: OSM download and road model.**
- Region picker: US states and countries, from a Geofabrik region list (`/index-v1.json`). Download the `.osm.pbf` with a cache, a progress bar and checksum verification.
- Use `osmium`/`pyosmium` (OpenCarin already depends on it). Build a routable road graph:
  - split ways at junctions;
  - map `highway=*` to CARiN road classes, using the attribute values seen on the original disc as targets;
  - carry one-way, names, refs and turn restrictions.
- Give the size estimate **now**, before the heavy work.

**Phase 2: tile and generalise into levels 0–3.**
- **Quadtree tiling** as on the original:
  - square tiles 40,960 × 2^k units;
  - local unit 64;
  - level-0 tiles adapted to density;
  - parcels sized to the original's typical 1–8 units.
- **Level selection:**
  - level 0 = all roads;
  - levels 1–3 = progressively higher road classes with simplified geometry, matching the original's style. Compare with our decoded original: only about 0.2% of level-0 shape points appear in levels 1–3.
- Parent links between levels, tile-edge nodes with neighbour block IDs, sections 9/10/11, and the name index (section 14).
- **Validation:** decode our own output with `tools/carin.py` and OpenCarin, and load it in `viewer/` next to the original disc's California data.

**Phase 2b: Points of Interest (required, not optional).**
The output disc must carry POIs: shown on the map and searchable as destinations, as on the original.
- **Source:** OSM nodes and areas tagged `amenity`, `shop`, `tourism`, `fuel`, `parking`, `car dealer` (`shop=car`), hotels, restaurants, hospitals, police, airports, railway stations, etc.
  - Map OSM tags to the original disc's POI categories; read the category codes from its 0x06/0x10 blocks.
  - Carry name, brand, phone, house number, street and city (`addr:*`), as the original's 0x10 records do.
- **On-disc structures, from the original:**
  - **0x06** POI records (brand names, plain);
  - **0x10** POI index (name / phone / house number / street / city strings);
  - **0x11** POI search tries;
  - POI references from the city table (0x0C), which lists brands per city.
  - Decode each with a round trip against the original first, as for the road parcels.
- **Known recipe:** OpenCarin `docs/carindb/01-architecture.md` §4.4.2 (issue #24). A from-scratch POI index was listed, searched and routed to on a CNI1.
  - City category ranges (0x0C S3) and brand ranges (S5).
  - 0x11 first-letter leaves.
  - Two 0x10 copies of branded POIs (category copy, brand copy).
  - 0x06 map records pointing at the copy the disc uses.
  - Rules: names sorted inside each letter leaf; every first letter gets a leaf; leaves in letter order; road levels linked near the car.
- **Category codes:** 0x07 S0 lists the codes present. This disc uses 39 of them (`carin-disc-findings.md` "POI categories"); `tools/osm2carin/poi.py` maps OSM tags to them.
- **Snapping:** attach each POI to its nearest level-0 link (routing to a POI needs a link and side).
- **Size:** count POIs in the size estimate (Phase 1) and in the build report (POIs in / out per category, dropped and why). Add an `--no-poi` / `--poi-categories` option so a large region can still fit on a CD-R.
- **Validation:** decode our POI blocks back and show them in `viewer/` as a layer.
- **Car test:** the unit lists them under the POI menu and routes to one.

**Phase 3: the rest of the database.**
Spatial index, name tries, city/country tables, superblock and record-size table, and `0x13` CD info. Base each one on the original's structures, and populate the companion files (`ABSTRACT`, `BIBLIOGR`, `PRODUCT`, `CARINET`) from the original unless there's a reason not to.

**Phase 4: pack and burn.**
- Write `/carindb` with the block chain.
- Compress type 0x00 parcels with CF=1 (OpenCarin's encoder) where it saves space. Otherwise store plain, as the original does for about 2,000 parcels.
- Build the ISO9660 image, then the raw MODE2/2352 `.bin` + `.cue`.
- Run the capacity check and print an ImgBurn how-to:
  - Write image file to disc;
  - lowest write speed;
  - verify on;
  - recommended CD-R media for older laser units.

## Deliverables

- `tools/osm2carin/` package (or an extension of `tools/carin.py`) with a single CLI:

  ```
  py -m osm2carin build --region us/california --out out/ [--force] [--levels 0-3] [--estimate-only]
  ```
- Unit tests for every decoder/encoder pair (round trip) and for EDC/ECC sector generation, using the original disc as the fixture.
- A **build report** for every run:
  - region;
  - counts of OSM ways/nodes in and links/nodes/parcels per level out;
  - POIs in / out per category;
  - block counts by type;
  - `carindb` size, total sectors, % of 74/80-min CD-R;
  - any data dropped and why.
- Updates to `carin-disc-findings.md` covering anything new learned about the format, kept in its current "Current status" + history structure.

## Working rules

- The original `.bin`/`.cue` are git-ignored. Never commit them or generated images. Write large outputs under `out/` (git-ignored).
- Verify, don't assume: every claim about the format should be backed by a round trip against the original disc or by OpenCarin's docs. Say clearly what is unverified (especially anything only the head unit can confirm).
- Prefer reusing OpenCarin. Where it lacks DB-REL 26 / CD support, extend it in a way that could be contributed upstream.
- Ask me before anything that needs the car or a physical burn. I'll do those tests and report back.
