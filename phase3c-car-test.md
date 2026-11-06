# Phase 3c car test: cities, streets and POIs from OSM

This disc goes further than the 3a disc (`phase3a-car-test.md`). It replaces the destination menus too:
- **Cities and streets:** destination by city → street, from OpenStreetMap.
- **POIs:** points of interest by city → category → name.
- **Roads:** as on the 3a disc, our roads for the map and routing.

**Run the Phase 0 test first.** If the unit rejects a modified disc, nothing later can work. If you can, also try the 3a disc: if this one fails, 3a tells us whether the roads or the new menus are to blame.

## The images

They are in `C:\Git\CARiN_P38\out\<region>\disc\` (git-ignored), as `.bin` + `.cue` in the same format as an ImgBurn rip of the original. Burn the `.cue`. Rebuild them with:

```
osm2carin cd-split P38_Navigation_CD_2002_California_Nevada.bin out\phase0\orig
osm2carin disc --region us/california,us/nevada,us:highways --coarse-budget 20
osm2carin disc --region us/nevada
```

| Image | Sectors | Fits |
|---|---|---|
| `us_california+us_nevada+us-highways\disc\us_california+us_nevada+us-highways-3c.cue/.bin` | 208,998 (46.4 min, 468 MiB) | 74- and 80-min CD-R |
| `us_nevada\disc\us_nevada-3c.cue/.bin` | 187,141 (41.6 min) | 74- and 80-min CD-R |

Burn the **California + Nevada + US highways** disc.

## What is on the disc

- **Roads:**
  - every road in California and Nevada;
  - motorways and trunk roads (interstates, freeways, US highways) across the rest of the US;
  - all zoom levels.
- **Cities:** OSM cities, towns, villages and hamlets of California and Nevada, named like the original (`san jose, ca`). Each street and POI belongs to the nearest one, which is usually right but not always: OSM has no city boundaries for many places.
- **Streets:** every named road in a city, listed under that city. **No house numbers yet:** pick the street itself.
- **POIs:** named OSM POIs of California and Nevada (petrol, hotels, restaurants, parking, hospitals…), each linked to its nearest road.
  - **Brands:** none. A POI's name is shown alone, with no brand in front.
  - **Country-wide POI name search:** switched off; POIs are found through a city.
- **Kept from the original:** the background map (water, parks, land use, labels).
- **Gone:**
  - the old roads, cities, streets and POIs;
  - traffic messages (TMC), whose data pointed into the old roads.

## Burning (ImgBurn)
1. **Write** mode, with the `.cue` as the source.
2. The lowest speed (4x–8x), with **Verify** ticked.
3. A good CD-R of the size in the table above.

Label the disc "3c".

## In the car
1. **Boot**, and check the map at every zoom, as in the 3a test.
2. **City.** Destination → City.
   - Type the first letters of your town (e.g. `S A N J`). Does the keyboard offer only the letters that can follow?
   - Is your town listed as `san jose, ca`? Pick it.
3. **Street.**
   - Type a street you know. Is it in the list? Pick it.
   - What does the destination screen show (street, city)?
   - Start guidance: does a route calculate to that street?
4. **City centre.** Pick a city and, if the unit offers it, a destination without a street (city centre). Does it route?
5. **POIs.**
   - Destination → Special destination / POI → your city → a category (petrol, restaurants, hotels).
   - Is it listed, by name? Pick one and start guidance.
   - Are POI icons drawn on the map (only some categories may get icons)?
6. **Another state.** Scroll the map to Arizona or Oregon and route to a point on an interstate there (no cities or streets outside California and Nevada).

## Report back
- boots yes/no, and whether the map draws at every zoom;
- **city:**
  - is your town listed?
  - does the letter-by-letter entry work?
- **street:**
  - is the street listed?
  - what does the destination screen show?
  - does a route calculate?
- **city centre:** routes yes/no;
- **POIs:**
  - are categories and names listed?
  - does a route calculate?
  - are icons drawn on the map?
- any freeze, reboot or eject, and what you were doing at the time.

## What the results mean
- **Everything works:** next come house numbers (from OSM addresses), brands, and better city boundaries.
- **The city list is empty, or the unit freezes when it opens:** the city trie (0x0D) or the city blocks (0x0C) break a rule we don't know.
- **Cities work but streets are empty or crash:** the street tries (0x0F) or the street directory (0x0E). We write 0x0E uncompressed, which the original does in 48 of its 8,250 blocks.
- **Streets are listed but no route:** the street's link into the road tile (S2 tile + offset).
- **POI menus empty:** the POI index (0x0C S3, 0x11, 0x10). Missing icons with working lists point to the 0x06 map tiles or their index.
