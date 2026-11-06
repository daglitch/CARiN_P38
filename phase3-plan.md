# Phase 3 plan: the rest of the database

Status 2026-11-06:
- **Done:** road layers 0x00–0x03, on the disc's quadtree, with their spatial index (0x08 grids, 0x09 cells). Every lookup finds its tile.
- **3a done offline** (`osm2carin disc`, `phase3a-car-test.md`). It deviates from the steps below:
  - every original block stays in place and our layers are appended, so nothing is moved or rewritten;
  - only 0x07 records 0, 2, 3, 4 change;
  - the old cities, streets, POIs and TMC keep pointing at the old roads, which stay on the disc. So nothing is neutralised yet.

  Car test pending.
- **Not started:** 3b, 3c, 3d.

## Approach: graft onto the original database

Build our `carindb` from the original, block by block.
- **Keep the parts that don't depend on the road network:**
  - superblock layout and record-size table;
  - 0x13 CD info;
  - 0x0A/0x0B country tables;
  - background layers 0x14–0x1E with their indexes;
  - TMC 0x17/0x18;
  - `CARINET`.
- **Replace the road layers and everything that points into them.**

Each step ends in a disc that can be built and checked offline. The first one is also worth a car test.

## 3a. Roads only (first car-testable OSM disc)

**Goal:** the unit draws our roads and routes along them (destination picked on the map). Address and POI search are switched off.

1. **0x07 layer directory**
   - Records 0, 2, 3, 4 (layers 0x00, 0x01, 0x02, 0x03) get the BLOCK_IDs of our 0x08 grids.
   - The root square stays the disc's, since it covers the US.
   - Parameter 0 stays 6 / 1 / 2 / 3.
   - Record 1 (POIs, 0x06) is pointed at an empty grid.
2. **Structures that point into the old road tiles**, from OpenCarin's xref catalogue (03-road-network §6.7, `carin/parser/refs.py`). Each is dropped or emptied:

   | Block | Points into old tiles via | Action |
   |---|---|---|
   | 0x04 house numbers | its tile's header `+0x54` | drop all; our tiles have `+0x54` = 0 |
   | 0x0E street directory | S2 → 0x00 tile and link offset | empty the street ranges in 0x0C city records |
   | 0x10 POI details | S4 road links | the city POI roots become (0, 0) |
   | 0x06 POI tiles | 0x10 | record counts become 0 |
   | 0x17 TMC locations | tiles and link offsets | keep the records but clear the links, or drop TMC |
   | 0x01–0x03 S8, 0x09, header `+0x58` | old tiles | already ours |

   OpenCarin tested both ingredients on a CNI1: an emptied street level, and a cut-off POI index ("every city's POI root (0, 0) and every 0x06 record count 0").
3. **Pack (the start of Phase 4)**
   - Physical order as on the disc:
     1. system blocks;
     2. cities and tries;
     3. street directory and POI index;
     4. 0x06;
     5. our street layer: 0x08, 0x09, tiles;
     6. our 0x03, 0x02, 0x01 layers;
     7. the background layers.
   - Kept blocks move, so every BLOCK_ID that names them must be rewritten. That needs the full reference catalogue for kept blocks too: the superblock S0, 0x07, the 0x08/0x09 of the background layers, and the city → trie links.
   - The superblock's total-units fields follow.
4. **ISO**
   - The ISO9660 header area is copied.
   - `carindb` is the last file, at LBA 366, so only its directory-record size, the volume size in the PVD and the path tables change. The companion files are copied.
   - Then `cdimage` builds the `.bin`/`.cue`.
5. **Checks**
   - Walk the block chain; every BLOCK_ID points at a block of the expected type.
   - Every road tile is reached from 0x07.
   - OpenCarin and `tools/carin.py` read the disc.
   - The viewer shows our roads.
6. **Car test** (`phase3a-car-test.md`): does it boot, draw the map at every zoom, and route to a point picked on the map? What happens in the destination menus (expected: empty city / POI lists)?

## 3b. Cities and streets (address entry)

- **City and admin model from OSM.**
  - `place=city/town/village/hamlet` nodes, plus `boundary=administrative` (`admin_level` 4 state, 6 county, 8 city).
  - Each street link gets its city: the boundary polygon it lies in, else the nearest place.
- **Structures from OpenCarin's documentation:**
  - 0x0C city records (S1 city, S3/S5 POI ranges);
  - 0x0D city tries;
  - 0x0E street directory, its S2 → our tile and link;
  - 0x0F street-name tries (one per city);
  - street-tile S1 (city) and S2 (street + locality) carrying real cities.
- **Each one is round-tripped against the original first,** as the road tiles were (parse → build byte-identical).
- **Car test:** enter city → street → destination, then route.

## Space for POIs (2026-11-06)

- **Space now:** the CA + NV + US-highways 3a disc is 333,480 sectors. An 80-minute CD-R holds 360,000, so about 26,500 units are free.
- **What the POIs need:**
  - the original's POI data (0x06 + 0x10 + 0x11) is about 19,300 units for 249,000 POIs;
  - OSM has 326,420 POIs in California and Nevada, so about 25,000 units at the same density. That only just fits next to the 3a layout.
- **Plan: reclaim the old data.** Once 3b and 3c replace the cities, street directory and POI index, nothing points at the old roads. Then:
  - drop the old road span (units 49,196–168,866, 119,670 units), the old street directory 0x0E (25,489 units) and the old POI data;
  - pack our blocks into the space;
  - rewrite the kept background blocks' BLOCK_IDs (the Phase 4 pack).

  That frees about 140,000 units.
- **US-wide POIs don't fit.** OSM has about 2.7 million, roughly 200,000 units. POIs cover the fully detailed regions only, as on the original.

## 3c. POIs

Following OpenCarin `01-architecture.md` §4.4.2, which was tested from scratch on a CNI1:
- 0x10 POI details (category copy and brand copy, each with a road link to our tile);
- 0x11 first-letter leaves;
- 0x0C S3/S5 category and brand ranges;
- 0x06 map records;
- 0x07 S0 category list.

Categories come from `poi.py` (OSM → CARiN codes, 39 on this disc).

**Car test:** list, search and route to a POI.

## 3d. Optional

- **House numbers (0x04)** from OSM `addr:*` / interpolation lines.
- **Signposts (S11)** from `destination=*`.
- **TMC:** rebuilt, or dropped.

## Open questions (only the unit can answer)

- Does it accept a modified disc at all? (the Phase 0 test)
- Does it accept our CF=1 encodings, including the new per-tile widths?
- The street-tile header values we set without knowing their meaning: `+0x60..0x6B` and `+0x6C`.
