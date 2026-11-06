# Phase 3a car test: does the unit draw and route on our OSM roads?

**Run the Phase 0 test first.** If the unit rejects a modified disc, or our compressed street tiles (`test-reencoded`), this test can't work either.

## The images

They are in `C:\Git\CARiN_P38\out\<region>\disc\` (git-ignored). Each is a `.bin` + `.cue` pair in the same format as an ImgBurn rip of the original: one MODE2/2352 data track, then the original audio track. Burn the `.cue`. The `work\` subfolder holds the ISO the image was made from; it isn't needed for burning.

Rebuild them with:

```
osm2carin cd-split P38_Navigation_CD_2002_California_Nevada.bin out\phase0\orig
osm2carin disc --region us/california,us/nevada,us:highways --coarse-budget 20
osm2carin disc --region us/california,us/nevada
osm2carin disc --region us/california
osm2carin disc --region us/nevada
```

The first needs the US-wide extract (11.7 GB download, about 10 minutes to build).

| Image | Size | Sectors (incl. audio) | Fits |
|---|---|---|---|
| `us_california+us_nevada+us-highways\disc\us_california+us_nevada+us-highways-3a.cue/.bin` | 748 MiB | 333,480 (74.1 min) | **80-min (700 MB) CD-R only** |
| `us_california+us_nevada\disc\us_california+us_nevada-3a.cue/.bin` | 645 MiB | 287,658 (63.9 min) | 74- and 80-min CD-R |
| `us_california\disc\us_california-3a.cue/.bin` | 619 MiB | 276,040 (61.3 min) | 74- and 80-min CD-R |
| `us_nevada\disc\us_nevada-3a.cue/.bin` | 447 MiB | 199,638 (44.4 min) | 74- and 80-min CD-R |

Burn **California + Nevada + US highways** on an 80-minute (700 MB) CD-R. Like the original disc, it has every road in the two states and the highway network across the rest of the US. If that disc won't read, try **California + Nevada**: it is smaller and stays further from the disc's outer edge. The single-state images are there in case you want a smaller disc still.

## What is on the disc

It is the original disc with the **road map replaced by OpenStreetMap** (California and Nevada, or a single state):

- **Our roads:** all four road levels, from the most detailed street level up to the motorway-only overview. They are the roads the unit draws, matches the car to and routes along.
- **Kept from the original:** everything else, byte for byte. That includes:
  - the background map (water, parks, land use, place-name labels);
  - the city list, street directory and POIs;
  - traffic (TMC) data.

  The original roads are also still on the disc, but nothing on the map leads to them any more. Only the old address and POI search still points at them.
- **Elsewhere in the US:**
  - on the highways disc, motorways and trunk roads only (interstates, freeways, US highways and their ramps), at every zoom level;
  - on the other discs, no roads at all.

  The original also has the primary state roads and some metro areas in full; ours doesn't yet, for space.

What to expect:
- **Picking a destination on the map** works on our roads. This is the main test.
- **Address and POI search** still lists the original 2002 cities, streets and POIs. Those entries point at the original roads, not ours, so routing to them may fail, may say "no route", or may behave oddly. That is expected at this stage; Phase 3b/3c replace them.
- **Street names** at the cursor come from OSM and are lower-case ASCII, as the original's are. Characters the format can't store are spelled out (e.g. `&` becomes "and").

## Burning (ImgBurn)
As for Phase 0:
1. **Write** mode, with the **`.cue`** as the source.
2. The lowest speed (4x–8x), with **Verify** ticked.
3. A good-quality CD-R.

Label the disc "3a CA+NV".

## In the car
1. **Boot.** Insert the disc and switch on as usual. Does the unit load the map? Note any message ("wrong CD", "CD error"…), and how long it takes compared with the original.
2. **Map at every zoom.** Around your position, step through every scale from the most detailed to the most zoomed out:
   - Are roads drawn at each scale?
   - Do they line up with the real roads and with the background map (rivers, coast, parks)?
   - Do the tile edges join up, with no gaps or roads cut off at straight lines?
   - Pan a few km in each direction, then stop on a busy area: does it redraw without freezing?
3. **Position.** While driving, does the car symbol sit on the road you are on? Does the current-road display show its name?
4. **Route to a point on the map.**
   - Use the map cursor, or "destination from map" / "select on map", whatever your unit calls it.
   - Pick a point on a road about 5–20 km away and start guidance.
   - Does the route calculate, and how long does it take?
   - Does it follow sensible roads? Is the guidance (arrows, voice, distances) right at the first few turns?
   - Try one longer route (50+ km, along a freeway) to exercise the coarse levels.
   - On the highways disc, also scroll the map to another state (e.g. Arizona or Oregon), pick a point on an interstate there, and check that a route calculates. Those roads join ours at the state line.
5. **Destination menus (just observe).** Enter a city and a street, or pick a POI. Then note:
   - what is listed;
   - what happens when you start guidance: a route, "no route", an error, a freeze?

## Report back
- boots yes/no, plus any messages;
- for each zoom level: drawn yes/no, and whether it lines up;
- car position on the road, and the road name shown;
- route to a map point: calculated yes/no, the time it took, and the guidance quality;
- the long route: yes/no;
- the destination menus: what happened;
- any freeze, reboot or eject, and what you were doing at the time.

## What the results mean
- **Draws and routes:** the unit accepts our road levels and spatial index. Next come cities and streets (3b) and POIs (3c).
- **Boots but draws nothing at some scales:** that road level or its index is at fault. The coarse levels' index cells are smaller than the original's (finer tile edges), which is the first thing to check.
- **Draws but can't route:** the cause is in the road graph between tiles, i.e. the edge-node twins or the links between levels.
- **Crashes when the map loads:** a tile breaks a rule the firmware relies on. We check those rules on every tile, so this would point to one we don't know yet.
- **Address/POI routing misbehaves:** expected; see "What to expect" above.
