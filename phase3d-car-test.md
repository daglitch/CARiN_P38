# Phase 3d car test: house numbers

This disc is the 3c disc (`phase3c-car-test.md`) plus **house numbers**: Destination → City → Street → **House number**.

**Test in this order:**
1. the Phase 0 disc;
2. the 3c disc;
3. this one.

If cities and streets don't work on the 3c disc, house numbers can't work either. If the 3c disc works and this one doesn't, the house numbers are to blame.

## The images

They are in `C:\Git\CARiN_P38\out\<region>\disc\` (git-ignored), as `.bin` + `.cue`, the same format as an ImgBurn rip of the original. Burn the `.cue`.

Rebuild with:

```
osm2carin cd-split P38_Navigation_CD_2002_California_Nevada.bin out\phase0\orig
osm2carin disc --region us/california,us/nevada,us:highways --coarse-budget 20
osm2carin disc --region us/nevada
```

`disc` downloads the US Census TIGER address ranges of the states it needs into `out\cache\tiger\` the first time: 75 county files, 200 MB, for California and Nevada. Add `--no-house-numbers` to build the 3c disc instead.

| Image | Sectors | Fits |
|---|---|---|
| `us_california+us_nevada+us-highways\disc\us_california+us_nevada+us-highways-3d.cue/.bin` | 249,056 (55.3 min, 558 MiB) | 74- and 80-min CD-R |
| `us_nevada\disc\us_nevada-3d.cue/.bin` | 187,141 (41.6 min) | 74- and 80-min CD-R |

## What is new on the disc

- **House numbers for streets in California and Nevada.** Each road segment stores the numbers at its two ends on each side, as on the original disc. They come from the US Census TIGER address ranges (2025), matched to the OSM roads by street name, distance and direction.
- **Coverage:** 61% of the named street segments (64% in Nevada) have numbers. A street without numbers can still be picked as before; the unit may then not ask for a number.
- **Numbers are block ranges,** as in most navigation data: e.g. 2201–2299 along a block, interpolated along it. A house is placed in proportion along its block, not at its exact door.
- Everything else is as on the 3c disc.

## Burning (ImgBurn)

1. Write mode, with the `.cue` as the source.
2. The lowest speed (4x–8x), with **Verify** ticked.

Label the disc "3d".

## In the car

1. **Boot,** and check the map at a few zoom levels.
2. **An address you know.**
   - Destination → City → your town → Street → your street.
   - Does the unit now ask for a **house number**? What range does it offer or accept?
   - Enter your number and start guidance.
   - Where does the destination flag sit: the right block, the right side of the street, near the door?
3. **A few more addresses,** ideally:
   - a long street (a boulevard several km long), with a low and a high number;
   - an address in Nevada (Las Vegas or Reno).
4. **A number outside the range.** Enter one that can't exist (e.g. 99999). What does the unit do?
5. **A street without numbers,** if you find one (a small rural road): does picking the street still work as on the 3c disc?

## Report back

- does the unit ask for a house number? the range it shows;
- for each address: the right block? the right side? how far from the door?
- what happens with an impossible number;
- any freeze, reboot or eject, and what you were doing at the time.

## What the results mean

- **Asks for a number and lands on the right block and side:** house numbers work. Next could be OSM address points to fill streets TIGER misses, and the POIs' house numbers.
- **Right block, wrong side** (odd and even swapped): side A is the other side on this unit. One switch in the matcher (`HouseNumbers.LeftIsA`). This is unlikely, because the original disc agrees with left = A on 95% of 220,000 sides.
- **Right street, wrong end** (low numbers where the high ones are): start and end are swapped. Also one switch.
- **Never asks for a number:** the street directory's flag or ranges (0x0E S1 / S2) aren't what the unit reads. Our 0x04 blocks may still be fine.
- **Freezes when entering a number:** a 0x04 block breaks a rule we don't know yet. Note the street.
