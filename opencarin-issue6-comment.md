Data point for `+0x22` from a disc that needs sub-revision 8 (a CD, DB-REL 26, North America):

**Disc:** CD-ID 5944, DB-REL 26, "us1-10_hw_nt_ald_02I_20021105b.cpr" (Navtech, 2002), Range Rover P38 / CARiN CD. 2048-byte units, single `/carindb`.

Superblock bytes `0x00`–`0x2F` (sector 0 of `/carindb`):

```
00000000: 0000 0001 0012 0000 0034 0001 0000 0301
00000010: 000c 0003 0000 0101 0001 001a 0000 000c
00000020: 0001 0008 003c 001f 007a 005a 0800 0001
```

- `+0x1A` = `0x001a` = 26 (matches `BIBLIOGR` "DB-REL 26")
- **`+0x22` = `0x0008`**
- `+0x24` = `{0x003c, 0x001f}`: a 31-entry list at `0x3c` (`0000 0001 0002 … 001e`), same shape as your `{0x68, 0x1f}`
- `+0x28` = `{0x007a, 0x005a}`: the record-size table at `0x7a`, 90 pairs

`CarinVolume.calibrate()` picks **subrel 8** on this disc. Decoding a spread sample of 60 CF=1 type `0x00` blocks and counting blocks whose name blob contains street-type words (st/ave/rd/dr/blvd/…):

| subrel | blocks with street-name text |
|---|---|
| 8 | 51 / 60 |
| 9 | 5 / 60 |

So this disc needs the 14-bit width, and its `+0x22` is 8. That's consistent with `+0x22` being the sub-revision read by `db_bh_read`. It also argues against `+0x22` being an offset: read as one, `0x0008` would point into the block header, and the `+0x24` list starts at `0x3c` here.

Also confirming that `decode_type00` works on DB-REL 26 (passes `0x14`, `0x15`, `0x17`; `0x1B` skipped). Names decode correctly across California, Nevada, Hawaii and the US metro coverage, e.g. "los angeles, ca … bartlett st … ca 110 north ramp", "hayward, ca … union city, ca".

On the coordinate frame: on this disc, roads decoded from the plain level 1–3 parcels with `lon = x/K − 30`, `lat = y/K` sit within a few metres of OpenStreetMap at zoom 17 (Las Vegas Strip, Golden Gate Bridge). The frame looks exact; the 2 km RMS in the README probably reflects where city labels sit, not frame error.
