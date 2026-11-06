# Phase 3a car test: does the unit draw and route on our OSM roads?

**Run the Phase 0 test first.** If the unit rejects a modified disc, or our compressed street tiles (`test-reencoded`), this test can't work either.

## The images

They are in `C:\Git\CARiN_P38\out\<region>\disc\` (git-ignored). Rebuild them with:

```
osm2carin cd-split P38_Navigation_CD_2002_California_Nevada.bin out\phase0\orig
osm2carin disc --region us/california
osm2carin disc --region us/nevada
```

| Image | Size | Sectors (incl. audio) | Fits |
|---|---|---|---|
| `us_california\disc\us_california-3a.cue/.bin` | 619 MiB | 276,040 (61.3 min) | 74- and 80-min CD-R |
| `us_nevada\disc\us_nevada-3a.cue/.bin` | 447 MiB | 199,638 (44.4 min) | 74- and 80-min CD-R |

Burn **California** first. Nevada is a fallback if you can drive there, or if you want a second, smaller test.

## What is on the disc

It is the original disc with the **road map replaced by OpenStreetMap** (California only, or Nevada only):

- **Our roads:** all four road levels, from the most detailed street level up to the motorway-only overview. They are the roads the unit draws, matches the car to and routes along.
- **Kept from the original:** everything else, byte for byte. That includes:
  - the background map (water, parks, land use, place-name labels);
  - the city list, street directory and POIs;
  - traffic (TMC) data.

  The original roads are also still on the disc, but nothing on the map leads to them any more. Only the old address and POI search still points at them.
- **Elsewhere in the US:** no roads at all, because our data covers one state.

What to expect:
- **Picking a destination on the map** works on our roads. This is the main test.
- **Address and POI search** still lists the original 2002 cities, streets and POIs. Those entries point at the original roads, not ours, so routing to them may fail, may say "no route", or may behave oddly. That is expected at this stage; Phase 3b/3c replace them.
- **Street names** at the cursor come from OSM and are lower-case ASCII, as the original's are. Characters the format can't store are spelled out (e.g. `&` becomes "and").

## Burning (ImgBurn)
As for Phase 0:
1. **Write** mode, with the **`.cue`** as the source.
2. The lowest speed (4x–8x), with **Verify** ticked.
3. A good-quality CD-R.

Label the disc "3a CA".

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
