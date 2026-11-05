# Data points for OpenCarin issues from CD-ID 5944

Ready-to-paste comments for https://github.com/fdemusso/OpenCarin/issues. One section per issue. #6 is drafted separately in `opencarin-issue6-comment.md`.

**Disc used throughout:** CD-ID 5944, DB-REL 26, Navtech, North America ("us1-10_hw_nt_ald_02I_20021105b.cpr", content "usa-dca-1-10 (usa)", oem "aftersales", conv tool "blof_1.1_20021030", converted 2002-11-05). Range Rover P38 CARiN CD; single `/carindb`, 2048-byte units; one country (United States). Sector numbers below are 2048-byte units in `/carindb`.

---

## #26 COUNTRY_ID: first Navtech/US data point, consistent with the ISO-English-order reading

`0x0A` at sector 4 has one country record at `+0x58`. On this DB-REL 26 disc the record is **44 bytes**; the next section starts at `+0x84`.

```
+0x58: 0000 0505 000c 001a 0000 000b 0000 0016 0000 0021 0000 002c
+0x70: 0084 0007 01f4 012c 03e8 01f4 0000 0000 00e0 0000
```

- **COUNTRY_ID (`+0x28`) = `0x00E0` = 224 for United States.** United Kingdom is `0xDF` (223) on your discs. UK and US are consecutive in English-name ISO 3166 order, so this fits electricduck's reading, and it comes from a Navtech disc, not TeleAtlas.
- The record ends at `+0x2C`, so this DB-REL has no COVERAGE (`+0x2C`) or ISO-code (`+0x2E`) field.
- The `0x07` → `0x18` TMC records (end of `0x07`, sector 2) also carry `0x00E0` (see #17).

## #25 `0x0A +0x1C` and `+0x24`

- `+0x1C..+0x23` = **500, 300, 1000, 500** for the United States too. The same values on a US (miles) disc as on the European ones suggests they aren't per-country unit or distance defaults.
- `+0x24` = **0** for the United States (right-hand traffic, not an island state). That's consistent with both readings, so it doesn't settle the question.
- `+0x08..+0x17` = **11, 22, 33, 44** (u32): the same placeholder as on DB-REL 34, so it goes back at least to DB-REL 26.

## #19 `0x13` CD info: same layout on DB-REL 26, but stored uncompressed

Here `0x13` is at **sector 1** (`BLOCK_ID 0x00000101`) with **CF = 0** (plain, not zlib).

```
00000800: 0000 0101 0013 0000 0010 0001 001c 0001
00000810: 0001 001a 0038 0009 0041 000f 069f 6bc7
00000820: 0d3e d78e 13de 4355 1a7d af1c 0001 0001
00000830: 001a 0050 01e1 0000 "no label\0" "no description\0" ...
```

- Descriptors `{0x10, 1}`, `{0x1C, 1}` as on your discs.
- Section 0 = `1, 26, 0x38, 9, 0x41, 15`: the DB-REL is 26 here, so that field tracks DB-REL.
- Section 1 = the same 16 bytes (**111,111,111 / 222,222,222 / 333,333,333 / 444,444,444**), then `1, 1, 26, 0x50, 0x1E1 (481), 0`. The constant therefore goes back to DB-REL 26 / 2002.
- Text = 481 bytes, from `0x50` to `0x231` (includes "13. conditional access: false", "gdf version 3.0", "rds/tmc included").

## #18 `0x0B`: last u16 also 0 on DB-REL 26

`0x0B` at sector 3, descriptor `{0x000C, 3}`:

```
0000 0401 6501 0028 0003 0000   'e' leaf=1 -> 0x0A @ sector 4, offset 0x28, count 3
0000 0401 7501 0040 0001 0000   'u' leaf=1 -> offset 0x40, count 1
0000 0401 7601 0048 0002 0000   'v' leaf=1 -> offset 0x48, count 2
```

These point at the country name list (estados unidos / etats unis / … / united states / vereinigte staaten / verenigde staten). Every record has leaf = 1 and **last u16 = 0**, which supports the "align" reading on a third disc and DB-REL.

## #17 / #22 TMC on a DB-REL 26 US disc

**Block counts:** 20 × `0x17`, 3 × `0x18`, and **no `0x19` / `0x1A` / `0x1B`** (like the BeNeLux CD).

**`0x18` table IDs (`+0x0C`):** `0x051`, `0x061`, `0x191`, i.e. **CC 1 (USA) with LTN 5, 6, 25**. S0 entries are `{u32 0x17 BLOCK_ID, u16 first code, u16 0}` with a `{0, last + 1, 0}` terminator, as in #17. Example (table `0x051`, sector `0x2D6A8`):

```
02d6 ab08 0001 0000 | 02d6 b308 1064 0000 | 02d6 bb08 1106 0000 | ...
```

**`0x07` → `0x18` (end of `0x07`, sector 2):** 20-byte records:

```
02d6 a801 | 0051 | 0001 | 028d | 0100 | 00e0 | 0000 | 0260 | 0001
BLOCK_ID    table          name   ?     COUNTRY_ID
```

`+0x0A` = `0x0100` here as well.

**`0x17` records are 56 bytes on DB-REL 26, not 100.** Block `0x2D6AB` has S0 `{0x1C, 170}`, and the string blob starts at `0x1C + 56 × 170 = 0x254C`, which equals the smallest name pointer. The field order matches Tintom1's table for the first `0x24` bytes:

| Offset | Example (code 4098) | Meaning |
|---|---|---|
| `+0x00` u16 | `0x1002` | location code |
| `+0x02` u8 | 26 | type (0 = area, 1 = line, others = point) |
| `+0x05` u8 | 2 | |
| `+0x06` / `+0x08` / `+0x0A` / `+0x0C` u16 | → "CA-12", –, "WRIGHT RD", – | road number, road name, location name, second name |
| `+0x10` u16 | `0x0041` (65) | road code (line 65 = "CA-12 WESTBOUND/EASTBOUND") |
| `+0x14` u16 | `0x1003` | next point |
| **`+0x20` / `+0x24` u32** | `0x587D790F`, `0x0CB989B0` | **location X, Y**: 38.4274° N, 122.7690° W, which is CA-12 at Wright Rd, Santa Rosa |
| `+0x28`..`+0x37` | `0108f403 00000000 049a0000 2d32 2d3c` | not decoded (last two u16 look like string pointers) |

So the DB-REL 26 record has the single location point at `+0x20` and none of the five additional coordinate pairs seen in the 100-byte DB-REL 34 record. Area names here are US regions: "SAN FRANCISCO METRO", "SACRAMENTO/RENO/TAHOE METRO", "CENTRAL CALIFORNIA"…

## Coordinate frame

Roads from the plain level-1/2/3 parcels drawn with `lon = x/K − 30`, `lat = y/K` sit within a few metres of OpenStreetMap at zoom 17 (Las Vegas Strip, Golden Gate Bridge). The TMC point above also lands on the right junction. The frame looks exact for North America too.
