# Phase 0 car test: does the head unit accept a modified disc?

The images are in `C:\Git\CARiN_P38\out\phase0\` (git-ignored). Rebuild them with:

```
py tools\cdimage.py split P38_Navigation_CD_2002_California_Nevada.bin out\phase0\orig
py tools\phase0_patch.py out\phase0\orig\track1.iso out\phase0\orig\audio.raw third_party\OpenCarin out\phase0
```

Each image is 419 MiB (186,691 data + 300 audio sectors), so it fits any 74/80-min CD-R.

| Image | What changed | What it tests |
|---|---|---|
| `test-patched.cue/.bin` | One POI name, in place: **LAND ROVER SAN JOSE → LAND ROVER P38 TEST** | Does the unit refuse a disc whose data differs from the master (checksum / integrity check)? |
| `test-reencoded.cue/.bin` | The same change, plus street **STEVENS CREEK BLVD → STEVENS CREEK TEST** in 12 compressed street tiles, re-encoded by OpenCarin's encoder | Does the unit accept compressed tiles that *we* wrote? (The whole OSM project depends on this.) |
| `rebuilt.cue/.bin` | Nothing: byte-identical to the original | Control. Use it only if test-patched fails, to tell "CD-R not readable" apart from "modified data rejected". |

## Burning (ImgBurn)
1. **Mode → Write**. For the source, pick the **`.cue`**, not the `.bin`.
2. Write speed: the **lowest** your drive offers (4x–8x if possible). Tick **Verify**.
3. Media: a good-quality **CD-R** (not CD-RW), e.g. Verbatim DataLife Plus (AZO) or Taiyo Yuden/JVC. 74 or 80 min both work. Older laser units read low-speed burns on dye-quality media best.
4. Label the discs: "A patched", "B reencoded".

## In the car: test A first
1. Insert disc A in the navigation CD drive and switch on as usual.
2. **Boot.** Does the unit load the map the same way as with the original disc? Note any message such as "wrong CD", "CD error", "insert navigation CD", or the unit ejecting it. Note how long it takes compared with the original.
3. **Map.** The map should display as normal around your current position.
4. **The patched name.** Search the POIs for the car dealer:
   - **Destination → City: SAN JOSE → Special destinations / POI → Car dealer** (or "Automotive"), or a POI search by name for "LAND ROVER" if your unit has one.
   - Look for **LAND ROVER P38 TEST**, which replaces LAND ROVER SAN JOSE (5080 Stevens Creek Blvd, 408-247-7600).
   - Other dealers ("LAND ROVER REDWOOD CITY", "LAND ROVER MARIN" …) should be unchanged.
   - If you can, select it as a destination and let the unit calculate a route.

Report back for A:
- boots yes/no, plus any messages;
- the name shown (old or new);
- whether the route calculates.

## Then test B (only if A boots)
1. Boot as above.
2. Move the map cursor to **Stevens Creek Blvd, Cupertino / San Jose**. It runs east–west at about 37.32° N, between Hwy 85 and I-880, past Westfield Valley Fair.
3. Zoom to street level (the most detailed scale).
   - The road should be drawn exactly as before.
   - Its name, at the cursor or in the "current road" display, should read **STEVENS CREEK TEST**.
   - At zoomed-out scales it may still say BLVD: those levels were not changed.
4. Also check:
   - the map around it still draws completely (no holes, no frozen screen);
   - **Destination → Street** still lists "STEVENS CREEK BLVD": the street directory was deliberately left unchanged.

Report back for B:
- boots yes/no;
- the street map draws normally yes/no;
- the label shown (TEST / BLVD / nothing);
- any freeze or reboot.

## What the results mean
- **A boots and shows the new name:** there is no integrity check on POI data, so building our own disc is feasible.
- **A is rejected or shows an error:** burn `rebuilt.bin`.
  - If that boots, the unit checks the data.
  - If it doesn't, the problem is the media or the drive.
- **B shows TEST and draws normally:** the unit accepts our CF=1 encoder output, so we can go on to Phase 1.
- **B draws wrongly, freezes or is rejected (while A works):** our encoder output differs in a way the firmware cares about. We fix that before going further.
