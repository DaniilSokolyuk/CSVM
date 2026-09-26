# The hangar screens, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`), 2026-08-21, in two passes: the BL-354/BL-067 `/backlog` decode of the named
callbacks, then a fresh-context prospecting pass over the four customisation screens' scripts
and every callback id they invoke. Every claim below names the function or address it came
from; no decompiler output is reproduced.

**Where the other halves live.** The 204-byte saved-plane record and its field layout are
[`formats/paint.md`](../formats/paint.md) "Saved custom planes" (the file is a verbatim dump of
the in-memory record this page's screens edit). The paint system itself (region masks, colour
formula, decals) is the rest of that page. The UI scripts that lay the screens out
(`GUNS.SCRIPT`, `HARDPOINTS.SCRIPT`, `ARMOR.SCRIPT`, `PURCHASE.SCRIPT`) are pure layout in
`extracted/rof/ASSETS/SCRIPTS/`; everything they display comes from the engine through the
callbacks below. `AIRFRAME.SCRIPT` is the exception, carrying the airframe swap's own question in
script rather than in the engine ("When the airframe swap asks").

⚠ **This page is a decode, not a proposal.** Where it disagrees with an observation at the
controls or a screenshot, the decode wins and the disagreement is a note.

## The callback dispatchers

Screen scripts call `callback(widget, id, ...)`. Two engine routines answer:

- **The widget dispatcher at `0x004093a0`** (no function defined in the Ghidra database; a raw
  routine with a 0x1820-byte frame). It reads an argument block (`[+0]` id, `[+4]` widget arg,
  `[+8]`/`[+0xc]` value in/out). Ids 2000–2038 jump through the table at `0x40f518`; id 2099
  has a dedicated branch; ids 2100–2413 index the byte table at `0x40f788` (by `id − 2100`)
  whose entry indexes the dword jump table at `0x40f5b4`. To resolve any id in that range by
  hand: byte at `0x40f788 + (id − 2100)`, then dword at `0x40f5b4 + entry*4`.
- **The screen-flow dispatcher `FUN_00407670`**, a plain switch over ids 1–31 and 1001–1045.
  Case 22 is the save-custom-plane commit; case 0x400 (1024) is the saved-plane count, calling
  the `Planes\*.*` directory scan `FUN_00415000` over the 24-slot name index at `0x648534`
  (33-byte records, bound `0x64884c`).

**List/dropdown protocol.** A list control invokes its fill callback with the widget arg
pointing at an index; index −1 (`EDX = −1` set at `0x004093b6`) means "write the row count
back". Fixed counts: gun dropdown 11 (`0x0040be24`), hardpoint dropdown 5 (`0x0040b826`),
armour dropdown 13. The PURCHASE list's row counts are dynamic (equipped-gun count from
`FUN_0040fdf0`, non-zero armour zones, non-empty wings).

**The ARMOR tab draws four boxes over three independent values.** The layout gives the tab one
combo box per zone (`AR_D_POINT0` to `AR_D_POINT3`: nose, tail, left wing, right wing), and the
two wing boxes move together, so changing either changes the other. That pairing is not in
callback 2247, whose SET arm writes exactly one zone dword per box (`0x0040acc2`, `0x0040acd6`,
`0x0040acea`, `0x0040acfe`), so it lives above the callback in the tab's own wiring. The remake
holds it in `HangarFeature.SetZoneUnits`, which both presentations set armour through. The record
still keeps four dwords, which is what lets the mission side damage one wing at a time, and a
saved plane whose wings disagree keeps them until a wing box is set.

## Callback map

All four screens, id → handler (widget dispatcher unless noted):

| Screen | Id | Handler | Role |
|---|---|---|---|
| AIRFRAME | 2200 | `0x0040c74c` | airframe dropdown labels and count |
| AIRFRAME | 2216 | `0x0040d28d` | set airframe: dropdown index → airframe id via `FUN_00410120`, stored to record +0x2c (`0x0064cba4`) |
| AIRFRAME | 2217 | `0x0040d272` | get airframe |
| AIRFRAME | 2223 | `0x0040b89b` | airframe description text |
| GUNS | 2248 | `0x0040be1b` | gun dropdown labels (11 rows; names `langui` 3310+type, twin rows prefixed with format 506, `"(%1!d!) "`) |
| GUNS | 2249 | `0x0040bea2` | get/set a slot's gun (record +0x84/+0x88) |
| GUNS | 2250 | `0x0040bf72` | slot title: reads the airframe stat table's +0x1c+slot*4 string id, tail at `0x0040ef84` |
| GUNS | 2220 | `0x0040bbe6` | titles |
| GUNS | 2226 | `0x0040b993` | gun description text |
| ENGINE | 2218 | `0x0040bce1` | engine dropdown labels and count |
| ENGINE | 2224 | `0x0040bb01` | engine description text |
| ARMOR | 2225 | `0x0040ba8d` | armour description text |
| HARDPOINTS | 2244 | `0x0040b81d` | dropdown labels (5 rows: `langui` 1165 "None", 1168 "1 Hardpoint", 1169 "%d Hardpoints") |
| HARDPOINTS | 2245 | `0x0040ad0f` | get/set a wing's hardpoint count (record +0x34/+0x38) |
| HARDPOINTS | 2227 | `0x0040bac6` | description text |
| ARMOR | 2246 | `0x0040b7bd` | dropdown labels (13 rows: row 0 is string 1165 "None", rows 1-12 show `row×5` units via format 1170, the display scale is the record's own ×5, not pounds) |
| ARMOR | 2247 | `0x0040ac3d` | get/set the four zone values (record +0x74..+0x80, stored premultiplied by 5) |
| PAINT | 2229 | `0x0040d30e` | colour dropdown fill: 27 swatch chips from the table at `0x0061dd48` |
| PAINT | 2230 | `0x0040d473` | the current effective colour chip per slot |
| PAINT | 2232-2234 | `0x0040d388` | shade dropdown fill per slot (the colour's own ramp) |
| PAINT | 2235 | `0x0040d2b8` | pattern dropdown fill: availability-masked rows, labels langui 3425+pattern |
| PAINT | 2236 | `0x0040d437` | shade get/set (record +0x50 array) |
| PAINT | 2237 | `0x0040d3e2` | colour get/set (record +0x44 array; resets the slot's shade to the colour's default) |
| PAINT | 2238 | `0x0040d4ba` | pattern get/set: SET loads the pattern entry's six colour/shade defaults |
| PAINT | 2239 | `0x0040d5b7` | decal get/set (record +0x5c array; grid `row*5+col` = the flat decal index) |
| PAINT | 2240 | `0x0040d61c` | decal grid row count (`ceil(N/5)`, N = 50 shipped names at `0x0061da20`) |
| PAINT | | | the swatch/pattern-default tables: [`formats/paint.md`](../formats/paint.md) "The swatch table and the pattern defaults" |
| PURCHASE | 2251 | `0x0040af39` | airframe row (name `langui` 3000+af, cost/weight from the stat table) |
| PURCHASE | 2252 | `0x0040afd8` | engine row (name `langui` 3100+af*6+engine, or 1171 "No Engine Selected") |
| PURCHASE | 2253/2254/2255 | `0x0040b0e4` | armour rows, name/weight/cost (zone names `langui` 1191–1194) |
| PURCHASE | 2256/2257/2258 | `0x0040b1f3` | gun rows (helpers: name `FUN_0040fe40`, weight `0x004055c0`, cost `0x00405700`) |
| PURCHASE | 2259/2260/2261 | `0x0040b2b4` | hardpoint rows (names `langui` 1176/1177 "Left/Right Wing: %d") |
| PURCHASE | 2262 | `0x0040b368` | totals (record +0x28 cost, +0x3c weight) |
| PURCHASE | 2263 | `0x0040b56c` | commit the purchase |
| PURCHASE | 2264 | `0x0040b418` | the problems gate: funds, weight capacity, engine present |
| shared | 2210/2211/2213/2214/2215/2221/2265 | `0x0040d260`/`0x0040a26b`/`0x0040add0`/`0x0040ae33`/`0x0040ad9b`/`0x0040b6b3`/`0x0040b3d4` | navigation and shared chrome |

## The airframe stat table at `0x00619bb0`

11 records (airframe ids 0–10), stride 0x2c, 11 dwords each. Field meanings, each from a
reader in code:

| Offset | Meaning | Reader |
|---|---|---|
| +0x00 | cost ($) | 2251 at `0x0040af57`; total-cost `FUN_00405680` |
| +0x04 | airframe weight (lb) | 2251 at `0x0040af67`; total-weight `FUN_00405550` |
| +0x08 | weight capacity (lb) | validator 2264 at `0x0040b4bc` ("OVERWEIGHT", `langui` 1227) |
| +0x0c | agility rating base | star rating `FUN_0040faf0` case 3: `(val − 1)/4`, clamped to 4 stars |
| +0x10 | armour rating base | `FUN_0040faf0` case 2: `(val + Σ record armour dwords − 1)/0x49`, the record dwords being units premultiplied by 5; C-truncating division, clamped to 4 |
| +0x14 | availability threshold (campaign progress) | `FUN_00410120` compares `DAT_0064b678 + 1` against it when mapping dropdown index to airframe id |
| +0x18 | per-gun-slot turret bitmask (bit i = slot i is a turret) | gun cost `0x00405700` tests bit `slot` to pick the turret price column |
| +0x1c..+0x28 | four `langui` string ids, one per gun slot (slot titles) | GUNS callback 2250 tail at `0x0040ef8c` |

The values (id: name / cost / weight / capacity / agility / armour / availability /
turret mask / slot-title ids):

| Id | Airframe | Cost | Weight | Capacity | Agi | Arm | Avail | Turrets | Slot titles |
|---|---|---|---|---|---|---|---|---|---|
| 0 | Hoplite | 6800 | 1400 | 4160 | 20 | 60 | 12 | 0x00 | 3061, 3070, 3062, 3069 |
| 1 | Hellhound | 4548 | 3255 | 9525 | 12 | 95 | 16 | 0x08 | 3071, 3072, 3061, 3073 |
| 2 | Balmoral | 1870 | 5460 | 15760 | −2 | 125 | 3 | 0x0c | 3061, 3062, 3060, 3073 |
| 3 | Bloodhawk | 5568 | 2415 | 6545 | 19 | 80 | 8 | 0x00 | 3061, 3062, 3063, 3064 |
| 4 | Brigand | 3783 | 3885 | 11015 | 8 | 105 | 6 | 0x08 | 3062, 3069, 3061, 3073 |
| 5 | Devastator | 4250 | 3500 | 10100 | 10 | 100 | 1 | 0x00 | 3074, 3075, 3076, 3077 |
| 6 | Firebrand | 2763 | 4725 | 14035 | 3 | 110 | 12 | 0x08 | 3061, 3079, 3062, 3073 |
| 7 | Fury | 5015 | 2870 | 7610 | 16 | 90 | 7 | 0x00 | 3062, 3069, 3061, 3070 |
| 8 | Kestrel | 2890 | 4620 | 13780 | 4 | 110 | 2 | 0x08 | 3065, 3078, 3062, 3073 |
| 9 | Peacemaker | 5228 | 2695 | 7205 | 17 | 85 | 3 | 0x00 | 3065, 3066, 3067, 3068 |
| 10 | Warhawk | 2423 | 5005 | 14675 | 2 | 120 | 19 | 0x00 | 3061, 3070, 3062, 3069 |

Slot-title strings are `extracted/rof/ui_strings.json` (3060 Nose Turret, 3061 Inner Wing
Guns, 3062 Outer Wing Guns, 3073 Rear Turret, …). The turret bitmask lines up with which
titles are turrets: the Hellhound/Brigand/Firebrand/Kestrel slot 3 is the Rear Turret (bit 3),
the Balmoral's slots 2 and 3 are Nose plus Rear Turret (0x0c).

## There is no per-airframe slot count

The premise that airframes differ in slot **counts** is disproven at three levels:

- `GUNS.SCRIPT` creates exactly 4 gun dropdowns in a fixed `for (R = 0; R < 4; R++)` loop,
  `HARDPOINTS.SCRIPT` exactly 2 rows; no callback gates either loop.
- The get/set handlers (2249 at `0x0040bea2`, 2245 at `0x0040ad0f`) read and write all slots
  unconditionally.
- The airframe stat table's 11 dwords are fully accounted for above; none is a count. The
  purchase validator 2264 checks only funds, weight capacity and engine presence.

Every airframe has 4 gun slots and 2 per-wing hardpoint groups. What varies is the slot
*titles*, which slots are *turrets* (pricing and weight), and the weight capacity that limits
what fits. A stock plane that appears to carry fewer guns is a prebuilt record with the empty
gun id (5) in some slots.

## The economy

Total cost (`FUN_00405680`, written to record +0x28) =
airframe cost + engine cost + gun cost + (Σ zone dwords)×4 + (hpLeft + hpRight)×410.
Total weight (`FUN_00405550`, written to +0x3c) =
airframe weight + engine weight + gun weight + (Σ zone dwords)×4 + hardpoints×480.

**Guns: table at `0x00619e68`**, stride 0x1c, indexed by gun id 0–4 (`langui` 3310–3314:
.30 Zephyr, .40 Carver, .50 Barret, .60 Cheyenne, .70 Goliath). Per record: +0x00 cost wing,
+0x04 cost turret (chosen when the airframe's turret bit for that slot is set), +0x08 weight
wing, +0x0c weight turret, +0x10/+0x14 non-price stats (10/2800, 9/2400, 8/2000, 7/1600,
6/1200 down the calibres), +0x18 a 1000 constant. A twinned gun (record +0x84 bit set) doubles
both cost and weight (`0x00405700`/`0x004055c0`).

| Gun | Wing $ / lb | Turret $ / lb |
|---|---|---|
| .30 Zephyr | 240 / 280 | 440 / 520 |
| .40 Carver | 320 / 380 | 530 / 620 |
| .50 Barret | 410 / 480 | 610 / 720 |
| .60 Cheyenne | 490 / 580 | 700 / 820 |
| .70 Goliath | 580 / 680 | 780 / 920 |

**Engines** (`FUN_004057c0`, weight at `0x0040b01c`): a per-airframe base table at
`0x00619d98`, stride 0xc (+0x00 base cost, +0x04 base weight, +0x08 power rating). Rows for
airframes 0–10: (850, 1000, 200), (1700, 2000, 261), (2550, 3000, 126), (850, 1000, 300),
(1700, 2000, 240), (1700, 2000, 251), (2550, 3000, 207), (850, 1000, 281), (2550, 3000, 215),
(850, 1000, 290), (2550, 3000, 201). Engine id 0–5 then offsets: cost + {−425, 0, +425, +5,
+430, +855}; weight + {−500, 0, +500, 0, +500, +1000} (dword table at `0x00619e1c`). Id 6 is
"no engine" (cost and weight 0) and blocks purchase. Engine names are `langui`
3100 + airframe×6 + engineId.

**Armour**: the four zone dwords at record +0x74 Nose, +0x78 Tail, +0x7c Left Wing, +0x80 Right
Wing each hold a **unit count, 0–60 in steps of five**. That count is what the screen labels
"units", never pounds. The dropdown's 13 rows are mapped onto it by callback 2247
(`0x0040ac3d`), whose SET arm multiplies the row by five (`LEA EAX,[EAX + EAX*0x4]` at
`0x0040acaf`) and whose GET arm divides the dword by five (the `0x66666667` reciprocal at
`0x0040ac84`). Every reader then takes the dword at face value, so armour is priced and weighed
at units×4 (handler `0x0040b0e4`, cost `LEA EDX,[ECX*4]` at `0x0040b1a6`, weight `units*20/5` at
`0x0040b188`), and the armour star formula adds the dwords unscaled (`FUN_0040faf0` case 2).
**One row of the dropdown is therefore five units, $20 and 20 lb.** Zone names `langui`
1191–1194.

**Hardpoints**: $410 and 480 lb each (handler `0x0040b2b4`; the constants resolve at
`0x0040b31d`, 0x19a and 0x1e0, and reappear in both totals functions).

**The purchase gate is the button, not the commit.** The commit callback 2263 (`0x0040b56c`)
re-checks nothing: it recomputes the derived record fields, deducts the total cost from the
funds global `0x0064b788`, copies the scratch record into its slot (`rep movsd`, 0x33 dwords)
and selects it. The block lives in `PURCHASE.SCRIPT`: `gui_init` calls the problems callback
2264 and mails `pur_b_purchase` 10000 (enable) on a clean answer or 10018 (disable) on
problems, with `pur_t_problems` carrying the text. An overweight or engineless build therefore
cannot be bought in the original; the button greys out.

## The description box on every tab

Each tab writes one scroll-text box (the `S` widget at 412,334 across 325 by 165, the airframe
tab's three pixels wider and two taller, the paint tab's shorter at 412,398 by 105) from **one
shipped format string that already carries the box's heading**, and the three tabs whose subject is
a picked component then append that component's own
prose row to it. The heading is therefore never composed: it is the tail of the figures string,
which is why hardpoints reads `HISTORY` where the other five read `DESCRIPTION`.

| Tab | Figures string | Arguments | Prose |
|---|---|---|---|
| AIRFRAME (2223, `0x0040b89b`) | 1153 `IDS_PX_AIRFRAMEINFO` | cost, weight, capacity, agility word, base armour word, turrets | 3040 + airframe |
| ENGINE (2224, `0x0040bb01`) | 1154 `IDS_PX_ENGINEINFO` | cost, weight, top speed, nitro word | 3240 + airframe×6 + engine id |
| ARMOR (2225, `0x0040ba8d`) | 1155 `IDS_PX_ARMORINFO` | 20, 5, 20, 5 | inside 1155 |
| GUNS (2226, `0x0040b993`) | 1156 `IDS_PX_GUNINFO` | cost, weight, calibre, fire rate, range, ammunition | 3330 + gun id |
| HARDPOINTS (2227, `0x0040bac6`) | 1157 `IDS_PX_HARDPOINTINFO` | 410, 480 | inside 1157, under `HISTORY` |
| PAINT (`PAINT.SCRIPT`) | 1158 `IDS_PX_PAINTINFO` | none | inside 1158 |

The paint box takes no callback at all: `PAINT.SCRIPT` fills `pt_s_paintdesc` with
`callback($$NB$$, 1158, PLA.BC)`, the string fetch, so the whole box is that one row.

**A body longer than the box scrolls inside it.** The `S` row's fourth, fifth and sixth fields are
its slider and its two arrows (`PH_B_ScrollBar.png`, `PH_B_ScrollUp.png`, `PH_B_ScrollDown.png`),
and `OriginalScreenshots/CustomPlane Armor.png` draws all three in a column inside the box's right
edge, the arrows at its top and foot and a thumb between them, over a last visible line clipped
mid-glyph. The armour and hardpoint bodies are the two the shipped box cannot hold whole: 1155
carries 560 characters of prose under its `DESCRIPTION` heading and 1157 about 330 under `HISTORY`,
against room for roughly six wrapped lines under the figures.

**The prose blocks and how they are indexed.** `IDS_AIRFRAMEDESCRIPTION` is 3040 to 3050, one row
per airframe id. `IDS_ENGINEDESCRIPTION` is **3240 to 3305**, 66 rows on the same
`airframe×6 + engine id` index the engine names take at 3100, so an airframe's six rows are its own
manufacturer's three displacements and their three nitrous twins; the handler recomputes that index
at `0x0040bb5c` (`EBX = widget arg + 6×airframe`) before adding 0xca8. `IDS_GUNDESCRIPTION` is 3330
to 3335 in the gun order 3310 names, the last row being the empty mount's. **A no-pick row shows
its own string alone, with no figures over it**: engine id 6 jumps to `0x0040bbbb` for 3307
`IDS_NOENGINEDESCR` and a gun id at or past 5 skips the whole figures block at `0x0040b9a2` for
3335, both of which read `No Information Available`.

**The figures the tables do not already carry.**

- **TURRETS** is the popcount of the airframe's turret mask (`0x0040b90d`, the mask byte at stat
  row +0x18) written as a single ASCII digit, or 1165 `None` when the mask is zero.
- **AGILITY and BASE ARMOR** are `FUN_0040faf0` cases 3 and 2 over a **zeroed 204-byte record
  carrying nothing but the airframe id** (`0x0040b8b4` clears it, `0x0040b8db` writes the id), so
  they rate the bare airframe and not the build standing on the screen.
- **TOP SPEED** is the stat line at `0x0040bb3e`: the engine base table's power rating times the
  engine id's own double from `0x00619e38`, truncated, in m.p.h.
- **NITRO-BOOST** is 1167 `Yes` for engine ids 3 to 5 and 1166 `No` below them (`0x0040bb10`).
- **CALIBER** is `(gun id + 3) × 10` (`0x0040ba4c`).
- **FIRE RATE** is the gun table's +0x10 column (10, 9, 8, 7, 6 down the calibres) formatted
  `%1.1f` (`0x0061f34c`) and **halved for a twin mount** (the float 0.5 at `0x006032e0`), so a twin
  .70 reads 3.0/sec.
- **AMMO** is the +0x14 column (2800 down to 1200) whole for a twin mount and **halved for a
  single** one (`0x0040ba28`), so a twin .70 reads 1200 rounds.
- **RANGE** is the +0x18 column, 1000 ft down every calibre.

⚠ **Every tab has prose, the three with no component included.** Armour, hardpoints and paint carry
theirs inside their own figures string rather than in a separate row, which is why reading only the
ENGINE and GUNS captures suggests those three have none.

The remake composes all six in `CSVM/src/UI/Menu/HangarDescriptions.cs`, which splits the filled
string at its own blank line into figures, heading and prose, so the heading is still the shipped
string's and never a literal.

## The decal picker is a five-across grid

The three decal dropdowns (`PT_D_DECALS0` to `2`, authored 87 wide by an `ItemHeight` of 73 with
`TotalDisplayed` 2) open **one grid of tiles over the page**, not a column of rows under the box
that was pressed. The decode is callbacks 2239 and 2240 in the map above: the picked decal is the
grid's own `row × 5 + column` and the list's row count is `ceil(50 / 5)`, ten rows of five.

The cell is the **decal sheet's own frame**: `PX_P_DECALS.TGA` is 66 by 3300 over 50 frames, so a
tile is 66 by 66 and the five columns come to 330 pixels, far wider than the 87-pixel box they hang
from. The authored 87 by 73 is the closed box with its dropdown arrow and padding around one tile,
and nothing in the layout describes the open grid, so its rectangle is measured off
`OriginalScreenshots/CustomPlane Decal Select.png`: the panel's frame runs 406,374 to 753,507 (348
by 134), the first cell sits one pixel inside it at 407,375, and the 16-pixel scroll column with
its two arrows and its thumb stands inside the right edge over the last column. The thumb fills its
track in proportion to the window (two of ten rows), not at the art's own height.

That capture also shows **the window on the grid's last two rows with the picked tile heading it**:
its nose decal is 40, which is the first cell of row 8. The remake opens a grid on the row its pick
stands in for that reason, and scrolls by whole rows of five.

## The four rating words

`FUN_0040faf0(record, which, flags)` is the whole of it: one switch, one case per rating, reading
one 204-byte plane record and its airframe's stat row. `which` is 1 to 4 in the order the plane
selection screen prints them, and the case picks the caption it prepends, `langui` 1018
`IDS_GN_TOPSPEED` "TOP SPEED: ", 1019 `IDS_GN_ARMOR`, 1020 `IDS_GN_AGILITY` and 1132
`IDS_GN_OFFENSE`, each followed by a one-space pad from `0x0061f428`. Every case ends in the same
tail: the index becomes `langui` 0x1f5 + index, the five-word run 501 to 505 `IDS_QUALITY`
(`Poor`, `Fair`, `Average`, `Good`, `Excellent`), appended to the caption in the shared buffer at
`0x0064e064`. `flags` 0x20 asks for the caption line, 0x40 for the bare index. The caller is the
plane screen's `uiData` 2015 at `0x004096ab`, which calls it four times over the profile's plane
slot (`0x0064b78c` + 204×slot) and copies each answer into one of the script's four out-strings.

Every case clamps at 4 and none clamps at 0, so an index the arithmetic drives below zero names an
id outside the five rating words: -1 is `langui` 500 and everything lower is absent from the table,
which appends nothing visible after the caption.

| Word | Case | Value, C-truncating throughout |
|---|---|---|
| TOP SPEED | 1 | engine id 6 (none) rates 0 outright; else `ftol(power × factor − 1.0) / 0x55`, the airframe's power rating from the engine base table at `0x00619da0` times the engine id's own double from the factor table at `0x00619e38` (0.9, 1.0, 1.1, 1.197, 1.33, 1.463) |
| ARMOR | 2 | `(armour base + Σ four zone dwords − 1) / 0x49`, the zone dwords being units |
| AGILITY | 3 | `(agility base − 1) / 4`, the one word no part of the build moves |
| OFFENSE | 4 | `(gun weight + hardpoints × 0x1e0) / 0x80c`, the gun weight being `FUN_004055c0(record, −1)`, the same per-slot wing-or-turret weight column the total weight uses, doubled for a twin mount |

So top speed reads the engine alone, offense weighs the armament and nothing else reads cost. The
two reference captures agree cell for cell, `OriginalScreenshots/Campaign Flight Check Change
Plane.png` and `Campaign Flight Check Change Plane Combo Box.png`: the seeded Devastator (airframe
5, engine 1, 100 armour units, three twin wing mounts and four hardpoints) reads Average four
times, at 250/85, 199/73, 9/4 and 4200/2060; the awarded Blue Streak (airframe 3, engine 4, 80
units, twin .40 and twin .30, two hardpoints) reads Average, Excellent and Fair, at 159/73, 18/4
and 2280/2060.

⚠ **The Blue Streak's TOP SPEED line prints no word in that capture, and no plane record can do
that.** Its 300 × 1.33 comes to Excellent, and the neighbouring AGILITY line proves that word
loads, so the index is not 4. Reaching an id `langui` has no string for takes an index of -2 or
lower: -1 is `langui` 500 `Nathan Zachary`, the default player name, while 215 to 499 are absent
from the table, so anything from -2 down appends nothing visible after the caption. The record
cannot drive the index there. Engine id 6 is short-circuited to `Poor` at `0x0040fb1f` without the
arithmetic, and every other id indexes the factor table unchecked
(`FMUL double ptr [EAX*0x8 + 0x619e38]` at `0x0040fb2e`): the dwords on both sides of its six
doubles read as tiny positive denormals, so an out-of-range id gives `power × ~0 - 1.0`, an `ftol`
of -1, and `-1 / 0x55` truncating to 0, which is `Poor` again. An index of -2 needs `ftol` at or
below -170, so a factor at or below -0.5633, and no slot within reach holds a negative double. What
is left is an `ftol` of `INT_MIN`, which the CRT's `ftol` returns for an indefinite: a full x87
stack makes the case's `FILD` at `0x0040fb27` produce one, and `INT_MIN / 0x55` is -25264513, whose
id is -25264012. The capture's other three words are the Blue Streak template's own fields, and that
template's engine is 4 (`0x0061ab80`), so the record is the template and the blank line is the FPU
state the menu inherited, not a field. `CSVM/src/UI/Hangar/PlaneRatings.cs` clamps at both ends and cannot
reproduce it.

## Into the mission: what the build changes on the spawned vehicle

The consumer trace past the launch bridge. Armour and engine are live combat data; total
weight and the stat-table power rating are hangar-only.

**Armour (record +0x74/+0x78/+0x80/+0x7c order nose, tail, left wing, right wing).**
`FUN_00417090` converts the four ints to floats with no rescaling (the x5 premultiply
survives) into globals `0x0071daf8..db04` (player) / `0x0071db58..64` (wingman); the
multiplayer writer `FUN_004136e0` (`0x00413acd..0x00413af1`) fills the same globals from the
MP config block at `0x00643044`. The mission loader `FUN_004735b0` patches them onto the
matched `CCEVeh` record at +0xdc/+0xe4/+0xec/+0xf4 in three name-matched branches ("player" at
`0x00474db4`, "wingman_1" at `0x00474edf`, the campaign branch at `0x0047510d`), each store
guarded by `value >= 0.0`: **a negative armour global means "leave the mission file's value
alone"**. The vehicle spawn `FUN_0047c210` (`0x0047cb67..0x0047cc63`) hands them to
`FUN_0047bd90(veh, zoneName, armour, structure)` for the four zones named `nose` / `tail` /
`leftwing` / `rightwing` (strings at `0x0062838c..0x006283a8`). Each zone (vector at
`veh+0x9c`, stride 0x58) holds two pools as (max, current) pairs: armour at zone+0x24/+0x28,
structure at +0x2c/+0x30; an argument of `-1.0` leaves that pool alone, and the hangar sets
only the armour pool (structure comes from the mission file, `CCEVeh+0xe0/+0xe8/+0xf0/+0xf8`).
Vehicle totals (`veh+0x2c4/+0x2c8` armour max/current, `+0x2cc/+0x2d0` structure) are
recomputed on every zone write as the sum over zones of values > 0, so they are derived state,
never independent. Difficulty scales an enemy's pools and totals by 0.75 / 1.0 / 1.25
(`FUN_00440710` mapped through `k*0.125 + 1.0` at `0x0047cc00..0x0047cd10`, where `k` is -2 / 0 / +2
and the middle tier skips the block; see [`vehicleDamage.md`](vehicleDamage.md)); the player's own
side and a neutral are never scaled. ⚠ **The roster's `ace` flag (`CCEVeh+0xa4`) does not suppress
this.** Its read is at `0x0047cde2`, past the armour block, and what it zeroes is the same `k`'s
offset on the pilot's skill ratings
([`aiControlLaw.md`](aiControlLaw.md#the-rating-the-interpolation-receives-is-not-the-authored-one)):
an ace's hull scales like any other enemy's. Both pools feed combat: the
damage-callout thresholds read `(armourCur + structCur) / (armourMax + structMax)` against
0.3/0.5/0.7 (`FUN_00498170` at `0x00498513..59`), and the totals are read across the damage
and HUD paths (`0x0047ee30`, `0x0049fa42`, `0x004b80d1..`, among others). The per-hit
application order (zone vs total, armour vs structure) is not decoded here.

**Engine (record +0x30).** The registry the trace ends in is authored data, shipped and
extracted: `extracted/zrdr/engines.zrd.json` is the `[key, name, power]` list the runtime
vector at `0x0064fb80` loads, and `FUN_00416e10`'s base ids land exactly on its per-airframe
"Lvl-1" rows (Bloodhawk 10, Peacemaker 13, Fury 16, Hellhound 19, Devastator 22, Brigand 25,
Kestrel 28, Firebrand 31, Warhawk 34, Hoplite 37, Balmoral 40; generic rows 0-4 are the
mission-side "Normal/Large/Small/Tiny/Super" engines). Power scalars run 0.23 (Balmoral
Lvl-1) to 1.28 (Warhawk and Firebrand Lvl-3). `FUN_00416ee0` decomposes the pick: engine ids
0/1/2 are three power tiers (base + 0/1/2 = Lvl-1/2/3), 3/4/5 the same tiers plus a
**nitrous injector boolean**, 6 is stock (no offset, no nitrous, no cost or weight). The
airframe maps to that base registry id via `FUN_00416e10`
(eleven-entry switch, base 0x0a stepping by 3: airframe 0 -> 0x25, 1 -> 0x13, 2 -> 0x28,
3 -> 0x0a, 4 -> 0x19, 5 -> 0x16, 6 -> 0x1f, 7 -> 0x10, 8 -> 0x1c, 9 -> 0x0d, 10 -> 0x22) and
the tier adds 0/1/2. Result and nitrous flag ride globals `0x0071daf0`/`0x0071daf4` (player,
mirrors for wingman and MP) onto `CCEVeh+0x10c`/`+0x108` (id -1 = no override), and
`FUN_0047c210` (`0x0047d4e0..17`) resolves the id in the **engine registry at `0x0064fb80`**
(entry stride 0x18, key +0x00, name +0x08, **power float +0x14**) writing the power into
`veh+0x66c`, inside the flight-tuning block `+0x654..+0x678` (spawn-jittered by
`FUN_00476250`, read in the force path `FUN_0048fc40` at `0x0048fde1` multiplied by
`veh+0x678`). Nitrous sets `veh+0x946`. Both identifications corroborated by the debug
console `FUN_0043d640` ("Choosing engine %s.", "You now have the nitrous injector").

**Total weight (record +0x3c) is hangar-only.** Written by `FUN_00405550`; its only consumer
is the overweight indicator at `0x0040b4ab..c8` comparing against the stat table's capacity.
Neither launch bridge reads +0x3c, and the plane-record array is addressed nowhere in flight
code (33 sites, all menu-band, plus `FUN_00443de0`, the in-mission weapon wiring, which reads
only +0x84 and the dword runs +0x88..+0xa4 / +0xa8..+0xc4). Weight is not mass, thrust or
drag anywhere.

**The stat-table power rating (`0x00619d98+8`) is display-only**: two readers program-wide,
the hangar's power stat line at `0x0040bb3e` and the star rating `FUN_0040faf0`. The stat
line multiplies the rating by a per-engine-id double table at `0x00619e38`: 0.9 / 1.0 / 1.1
for the three tiers, 1.197 / 1.33 / 1.463 for the same tiers with nitrous (exactly x1.33),
then truncates. The sim's engine power comes from the separate registry above.

**The type-8 spawn message is paint, not stats.** `FUN_004084a0(8, buf)` dispatches to
`FUN_00401e80`, which builds `assets\graphics\<pattern>\...` texture paths from descriptor
+0x40 (pattern, a 14-entry name table at `0x0060301c`), +0x2c and +0x68; `FUN_0041a320`
registers three 0x34-byte texture entries per plane from the +0x5c/+0x60/+0x64 picks against
the logo/decal name table at `0x0061da20`. Armour and engine ride the global block, not this
message.

## The campaign wallet

The player's money is one dword, `0x0064b788`, exposed to the GUI scripts under the name
`nPlayerCash` by the script-global binder `FUN_00401fc0` (which registers the whole
`nMission` / `fIAConstruction` / `fMultiPlayerConstruction` / `fAllowAll` / `szSaveGameDir`
family the same way). **Program-wide the executable has exactly four writers of its own**, so the
first four rows of the ledger below are complete, not a sample. The fifth is the hub script's,
which reaches the same dword through the scripting interface rather than through code.

| Site | Effect |
|---|---|
| `0x0041146b` (`FUN_004113b0`) | fresh profile: `nPlayerCash = 0` |
| `0x0041160b` (same function) | `nPlayerCash = 250000`, only when `fAllowAll` is set |
| `0x00405db9` (`FUN_00405ce0`) | mission wrap-up: `+=` the objective reward below |
| `0x0040a067` (sell handler) | `+=` the sold plane's full build cost |
| `0x0040b5f3` (commit 2263) | `-=` the scratch build's total cost |
| `PLANECONSTRUCTION.SCRIPT`'s `gui_char` body | the typed `gimme`: `+= 25000` while the wallet holds under 50000 |

**Starting funds are $0.** `FUN_004113b0` is the fresh-start initialiser (called from
`FUN_004112b0` at boot and from the screen-flow dispatcher `FUN_00407670`); it zeroes the
plane-slot array, resets campaign progress `0x0064b678`, and sets the wallet to zero. The
`250000` on the next branch is not a campaign figure: `fAllowAll` (`0x00647b5c`) is the flag
that also switches off every airframe availability threshold in `FUN_00410120` /
`FUN_004100d0` / `FUN_00410170`, and the same branch fills plane slots 2 to 12 with the eleven
stock airframe templates. It is the unlock-everything mode, and its budget is the only
literal money constant in the image. What sets `fAllowAll` from the menus is the hidden pilot
name `crashcheat!` on the profile screen, which `CAMPAIGN.SCRIPT` compares before the name
validator runs.

**The campaign instead starts with two aircraft, not with cash.** The same initialiser copies
prebuilt records 11 and 12 (204 bytes each) out of the template array at `0x00619f58` into
plane slots 0 and 1, naming them from `langui` 511 `IDS_PILOTPLANENAME` "Gypsy Magic" and 512
`IDS_WINGPLANENAME` "The Knave". Both records carry airframe id 5 (Devastator), engine id 1
(the Lvl-2 tier) and 2 hardpoints per wing. Templates 0 to 10 in that array are the eleven
stock airframe builds, one per airframe id.

### The cash note

The money on hand is drawn by the Plane Construction hub's own chrome, not by any tab:
`PLANECONSTRUCTION.SCRIPT` creates `px_t_cashtitle` (`langui` 1149 `IDS_PX_CASH_TITLE`, `$$$ on
Hand:`, authored at 615,0, 120 wide, centred, black) and `px_t_cash` (the figure, at 615,25,
120 by 55, centred), and the sticky-note art they sit on is part of `PX_BackGround.jpg`. Because
the hub hosts every tab script (`AIRFRAME.SCRIPT` to `PAINT.SCRIPT`) and the totals page inside
it, the note stands on every construction screen, beside `px_t_planecost` (`langui` 1036 `PLANE
COST:  $%1!d!`) in the header. The two `OriginalScreenshots\Campaign CAP-40 Plane Construction
*.png` captures show exactly that: the Engine and Armor tabs of a build, `$$$ on Hand` / `$21840`
on the note and `PLANE COST: $9930` / `$9190` in the header. The title's authored box is only 25
high, so those captures and the film alike show the note reading `$$$ on` over the figure with the
rest of the line clipped.

**The wallet-free doors carry the same note over a fixed $50000.** `gui_init` builds the figure as
string 91 followed by `"$"` and the funds, and substitutes `50000` for `nPlayerCash` when
`fIAConstruction` or `fMultiPlayerConstruction` is set, which is the `$$$ on $50000` the Instant
Action stills show on every tab. String 91 is an empty entry carrying nothing but its font tag, so
the figure reads as the number alone in the note's own handwriting face. Nothing on that path
checks a price against the 50000: the only funds gate in the image is callback 2264 at the
Purchase button, which the export door never reaches.

**No dropdown row is marked in the original.** The tab scripts' fill callbacks (2200, 2218,
2246, 2248, 2244) write names alone, and the only funds check anywhere on the path is callback
2264's `INSUFFICIENT FUNDS` at the Purchase button. Both presentations' mark on a row the wallet
cannot cover (`HangarFeature.UnaffordableMark`) and Built-in's wallet line beside the totals are
remake additions, and by that decode they are never a gate: every marked row stays pickable.

### The two red figures

`PLANECONSTRUCTION.SCRIPT` colours two of the header's figures off its own checks, and the colour
is a literal in the script rather than anything in `LAYOUT.CSV`'s colour tail. The hub captures the
authored colours of both widgets at build time (`YMA = RNA.HD` for `px_t_planecost`, `ZMA = VNA.HD`
for `px_t_currentweight`) and its mailbox writes one or the other back:

| Arm | Check | Colour |
|---|---|---|
| 12001 | `callback(2213, RMA, RNA.BC)` at `0x0040add0` | the captured authored colour when it answers true **or** `fMultiPlayerConstruction` / `fIAConstruction` is set, else `0xffff0000` |
| 12002 | `callback(2214, TNA.BC, UNA.BC, VNA.BC)` at `0x0040ae33` | the captured colour when it answers true, else `0xffff0000` |

**2213 answers "the wallet covers this build".** It totals the record through `FUN_00405680` into
`0x0064cba0`, writes the `langui` 1036 cost line, then compares that total against `nPlayerCash`
(`0x0064b788`) and returns the `SETLE` at `0x0040ae26`: true while cost is at most the funds.

**2214 answers "this build is inside its capacity".** It writes the airframe line (`langui`
3000 + airframe), the capacity line off the stat table's `+0x08` (`0x00619bb8` indexed by the
airframe's 11-dword stride) and the weight line, then compares the total weight (`0x0064cbb4`,
`FUN_00405550`) against that capacity at `0x0040af15`: true while the weight is at most the
capacity. A dword at `0x00647ba4`, written by the set-airframe callback at `0x0040d2a9`, swaps the
weight line for `langui` 1032's own `CURRENT WEIGHT: Pending` and makes 2214 answer true whatever
the weight (`0x0040af1d`), so a pending weight line is never red.

Both figures therefore carry the same opaque red on every screen the hub hosts, and the wallet-free
doors take the authored colour unconditionally, which is the funds gate's own rule: nothing on that
path checks a price. Arm 12001 also runs from `gui_continue` and from the `gimme` cheat's `+25000`,
so the cost line is re-inked whenever the hub is resumed or the purse changes. `CAP-50` shows the
weight line red over capacity.

The remake draws both through `BoardInk.Alarm`, which is pure red on every board and takes no
palette, the way `BoardInk.Dialog` is white on every board: a palette entry would be a screen colour
where the script has a literal. Two remake additions stand beside them and are not this. The cash
figure's own mark (the note going to the problems ink over a build the wallet cannot cover) is ours,
as is the preview: Original's hub prices the row under the cursor in an open list as though it were
taken, so PLANE COST, WEIGHT CAPACITY and the AIRFRAME line follow the focused row the way the
description box and the blueprint already do. The cash mark previews with them, off the one
previewed bill, so the note and the cost line can never disagree on screen. Nothing is written by a
preview, so leaving a list without a pick restores every figure.

**The airframe list is the one row the weight line does not follow.** A candidate airframe is what
the pending dword above marks, so the open airframe list puts `langui` 1032 on the weight line and
leaves it in the page's ink however heavy the build is, which is the swap's own reading: the weight
a build carries is not a figure to judge against the airframe it was built on. Every other list
keeps the comparison, since 2214 runs on each of those changes with the dword clear, so an engine,
armour, gun or hardpoint row still shows the previewed weight and still reddens past the capacity.

### The sell price is the full build cost

Both the confirmation prompt and the credit compute it the same way, and neither applies a
depreciation factor: `FUN_00405680(record)` (the same total-cost function the Purchase screen
displays) is taken through an `FILD` / `ftol` round trip that is arithmetically an identity, a
compiler artifact of a float-typed cost expression. The prompt is at `0x0040a1e6`, formatting
`langui` 700 `IDS_PS_QUERYSELL` ("Your %1!s! is worth $%2!d!") with the airframe's short name
(`langui` 3020 + airframe id) and that figure; the credit is at `0x0040a052`, adding the same
figure to `nPlayerCash` and clearing the slot's class dword. **So a plane is worth exactly what
it cost, and rebuilding is free of loss.**

A record whose class dword `+0x00` is `2` is a **special** plane: the sell handler branches at
`0x0040a1d9` to `langui` 704 `IDS_PS_SPECIALPLANE` ("This %1!s!, %2!s!, cannot be sold")
instead. `langui` 701 carries the separate floor, "you must keep at least two planes in your
hangar". How much of the slot array the purchase side may take is the section below.

### The slot cap reserves the five awards

The profile's plane records run 25 (`0x0064b78c` to `0x0064cb77`, stride 204, the loop bound
`CMP EDX,0x64cb78` at `0x0041121e` and the `CMP ECX,0x19` at `0x0040608c`), and two finders share
them.
The award half `FUN_00406060` takes the first record whose class dword `+0x00` is `0`, scanning all
25 with nothing held back. The purchase half `FUN_004111f0` walks the same 25 with a counter that
starts at `-5` and rises once per record that is empty or special (class `2`), and returns the
first empty record's index, or `-1` when that counter never reaches 1.

So a purchase needs six of the 25 records to be empty or special: one for the plane being bought,
five for the mission awards. **An already granted award keeps counting towards the six**, its own
record being the reservation it was holding, so the specials cancel out of the arithmetic and what
the finder enforces is a cap on **bought** planes alone: 20 of them, whatever the profile has been
awarded. A profile holding 19 bought planes and all five awards may still buy its twentieth,
filling the array exactly; a profile holding 20 bought planes and no award at all is refused, and
its five empty records stay held for awards that have not arrived.

The refusal is `langui` 204 `IDS_PN_TOOMANYPLANES`, "You have reached your hangar limit of planes.
Click Sell Planes, and sell one or more planes." The id is pushed nowhere in the image (every
`PUSH 0xcc` in it is a 204-byte record copy): the callback ending at `0x0040d260` calls the finder
and returns its value as its own, and the message is chosen above that from the `-1`. The purchase
commit calls the same finder at `0x0040b5d0` and copies the scratch record into the slot it names
(`0x0040b60f`, `rep movsd` of 0x33 dwords, then class dword `1` at `0x0040b613`), so the gate sits
at the button and not at the write.

### The inventory screen's own strings

The campaign's plane-selection screen is an inventory over the owned slots, and its `langui`
symbols name every verb it offers. They are the words a reimplementation should use, in
preference to inventing labels for the same actions.

| Id | Symbol | Text |
|---|---|---|
| 1257 | `IDS_HA_TITLE` | `INVENTORY` |
| 1258 | `IDS_HA_VALUE` | `Value: $%1!d!` |
| 1256 | `IDS_HA_SELLEXPORT` | `Sell or Export a Plane` |
| 1003 | `IDS_PS_B_SELL` | `Sell` |
| 1139 | `IDS_PS_B_EXPORT` | `Export` |
| 1149 | `IDS_PX_CASH_TITLE` | `$$$ on Hand:` |
| 204 | `IDS_PN_TOOMANYPLANES` | the slot-cap refusal, pointing at Sell Planes |
| 703 | `IDS_PX_PURCHASEPLANE` | `This %1!s! has been purchased and delivered to your hangar.` |
| 702 | `IDS_PS_EXPORTPLANE` | the export confirmation, naming Multiplayer and Instant Action |

**700 and 704 both carry arguments and are unusable raw.** 700 is `Your %1!s! is worth
<B>$%2!d!<b>.  Are you sure you want to sell it?` and 704 is `This %1!s!, %2!s!, cannot be sold.`;
%1 is the airframe's short name (`langui` 3020 + id) in both, %2 the plane's own name. 700 also
carries the original's bold markup inline, which a renderer without it should strip rather than
show. 701 is the only one of the three that reads correctly with no formatting at all.

**Export is a verb the campaign has and Instant Action does not.** A campaign plane is not
visible to Multiplayer or Instant Action until it is exported (702), which is the original's
separation between the two inventories over one plane store.

### The mission reward table at `0x0061ae80`

Ten live records of five dwords (stride `0x14`), terminated by a record whose mission id is 0.
Read by `FUN_00405ce0` (the cash half) and `FUN_00405f00` (the aircraft half).

| Mission | Objective bit | Cash | Awarded airframe |
|---|---|---|---|
| 1 | 12 | $900 | none |
| 2 | 1 | 0 | 2 Balmoral, `langui` 513 "Jumping Jane" |
| 5 | 1 | $20,000 | none |
| 6 | 3 | $5,000 | none |
| 7 | 1 | 0 | 3 Bloodhawk, `langui` 514 "Blue Streak" |
| 12 | 1 | $10,000 | none |
| 13 | 0 | 0 | 7 Fury, `langui` 515 "Red Hot Spender" |
| 17 | 0 | 0 | 0 Hoplite, `langui` 516 "Minx" |
| 19 | 1 | $5,000 | 10 Warhawk, `langui` 517 "Accipiter Annie" |
| 24 | 1 | $100,000 | none |

Field `+0x00` is the mission id in the `nMission` numbering, `+0x04` an objective bit index, which
is an `IDENTITY` priority rather than a display-row position
([`../formats/objectives.md`](../formats/objectives.md), "IDENTITY and the objectives display"),
`+0x08` the cash, `+0x0c` an airframe id with **11 meaning no aircraft award**, and `+0x10` a
pointer into the dword run at `0x00646384` that **no code reads** (one slot per record; left as
an unread field, not interpreted).

The cash is paid at `0x00405db9` only when the record's mission is the mission just flown, the
run set that objective bit, and the cumulative per-mission bit mask at `0x0064cbfc + mission*0xa8`
did **not** already have it: each bonus pays once per profile, replays included. The same sum is
accumulated into the mission's own stats record, which is what the scoreboard's "Cash Earned"
(`langui` 1206 / 1211) shows. Bit 0 means "mission completed", with no separate objective gate.

The aircraft half runs in `FUN_00405f00`: on the same gate it copies the matching template out
of the special-plane array at `0x0061a9b8` (records whose class dword is `2`, stride 204 bytes,
matched on their airframe id), names it from the string above, and marks a per-airframe
already-awarded byte at `0x0064cc44 + airframeId` so it is granted once. The five names decode
their own mapping: the `langui` symbols are `IDS_HW5BALMORALNAME`, `IDS_NW2BLOODHAWKNAME`,
`IDS_HW3FURYNAME`, `IDS_RM2HOPLITENAME`, `IDS_RM4WARHAWKNAME`.

**Total campaign cash income is $140,900**, from six paying objectives, plus whatever the
awarded aircraft would fetch if they were sellable, which they are not.

### The five special-plane templates at `0x0061a9b8`

Five live 204-byte records, stride `0xcc`, in the layout
[`../formats/paint.md`](../formats/paint.md) decodes for a saved custom plane. An award is a whole
aircraft, not just an ownership row: the grant copies one of these wholesale and only then writes
the name over `+0x04`, whose shipped value is the placeholder `??`. Read directly out of the
executable's data.

| Plane | Airframe | Engine | Hardpoints L/R | Armour rows (nose/tail/left/right), five units each | Guns |
|---|---|---|---|---|---|
| Minx | 0 Hoplite | 1 | 1/1 | 3/3/3/3 | twin 30 cal |
| Jumping Jane | 2 Balmoral | 1 | 4/4 | 8/7/5/5 | twin 50, twin 50, 30, 30 |
| Blue Streak | 3 Bloodhawk | 4 | 1/1 | 4/4/4/4 | twin 40, twin 30 |
| Red Hot Spender | 7 Fury | 1 | 2/1 | 5/5/4/4 | twin 70, twin 30 |
| Accipiter Annie | 10 Warhawk | 1 | 4/4 | 6/6/6/6 | twin 70, twin 50 |

**Only the Blue Streak carries a nitrous engine.** Engine ids 3 to 5 are the three tiers with the
injector and its 4 is the middle one; the other four templates take id 1, a plain Lvl-2 engine. A
stock Bloodhawk has no injector at all, so the nitrous is the Blue Streak's own build and never a
property of the airframe. The in-mission hand-over agrees: C1/M02's callback 965
(`FUN_0047e080`, case `0x3c5`) rebuilds the player on `pbloodhawk` and then writes this row's fit
by hand, 40/30 with both twin bytes, two hardpoints of six, 20 armour on all four sections and
the injector bit at `player+0x946`, which is the same build the debrief award copies whole
([`../formats/anim-definitions/cutscenes.md`](../formats/anim-definitions/cutscenes.md)).

The paint is one authoring shared by all five, byte for byte: pattern 4, swatch rows 1, 26 and 26
at shade variants 8, 0 and 9, and decals 40 (nose), 8 (tail) and 7 (wing).

The ammunition field at `+0x98` and the eight pylon cells at `+0xa8` are the commit-derived pair
described in [`../formats/paint.md`](../formats/paint.md), and every template carries exactly what
that derivation produces from its own gun ids and hardpoint counts: `4` in the slots whose gun id
is `5`, and a live pylon inside each wing's count with `11` past it. They restate the fields above
rather than adding to them.

### The eleven stock builds at `0x00619f58`

Templates 0 to 10 of the same array, one per airframe id, are what an aircraft carries when nobody
has built it: the profile's two starters are copies of template 5, and the ratings on the plane
screen read them as they read any other record. All eleven take engine id 1, the plain middle tier,
so no stock aircraft carries an injector. Armour is in the record's own units, five to a dropdown
row.

| Id | Airframe | Hardpoints L/R | Armour nose/tail/left/right | Twin mask |
|---|---|---|---|---|
| 0 | Hoplite | 1/1 | 15/15/15/15 | 0x1 |
| 1 | Hellhound | 2/1 | 30/25/20/20 | 0x3 |
| 2 | Balmoral | 4/4 | 40/35/25/25 | 0x3 |
| 3 | Bloodhawk | 2/1 | 20/20/20/20 | 0x3 |
| 4 | Brigand | 2/2 | 30/35/20/20 | 0xb |
| 5 | Devastator | 2/2 | 25/25/25/25 | 0x7 |
| 6 | Firebrand | 3/3 | 30/30/25/25 | 0xb |
| 7 | Fury | 2/1 | 25/25/20/20 | 0x3 |
| 8 | Kestrel | 3/2 | 30/30/20/20 | 0xb |
| 9 | Peacemaker | 2/1 | 25/20/20/20 | 0x3 |
| 10 | Warhawk | 4/4 | 30/30/30/30 | 0x3 |

The four zones sum to the airframe's own armour rating base in ten of the eleven rows, the Kestrel
(100 against a base of 110) the only one parting from it, so a stock aircraft's ARMOR word comes
out of very nearly twice its base. `CSVM/src/Flight/Hangar/HangarEconomy.cs` carries those sums as
`StockArmourUnits`, which is all the rating needs; the guns and hardpoints of the same builds are
in `CSVM/data/stock_loadouts.json`, where they agree with these rows slot for slot.

### What Load Default Configuration loads

`PLANECONSTRUCTION.SCRIPT`'s `gui_create` reads the name screen's checkbox (`mail(10008,
@planename@OOA)`) and hands its state to callback **2212**, whose handler is at `0x0040aacc`
(the widget dispatcher's table: byte `0x40f788 + (2212 - 2100)` is `0x28`, and the dword at
`0x40f5b4 + 0x28*4` is that address). The handler does four things in order:

1. It reads the build record's own airframe field at `0x0064cba4` (record `+0x2c`) and copies the
   204-byte stock template at `0x00619f58 + airframe*204` over the whole record (`0x0040aaff` to
   `0x0040ab2c`), preserving the typed name across the copy through the string helper at
   `0x00a1fc84`.
2. The paint block (thirteen dwords from `0x0064cbb8`) is saved and put back when `0x00647b94` is
   set; otherwise the pattern's own colour and shade defaults are reloaded from `0x0061daf4` and
   `0x0061db00`.
3. Only then is the checkbox consulted, at `0x0040ab92`. **With the box clear** the template just
   copied is stripped back to a bare airframe: engine `6`, the no-engine value (`0x0040abed`),
   both hardpoint counts and all four armour zones zero, the four gun slots set to the empty gun
   `5`, the ammunition dwords to `11` and `4`.
4. The finished record is copied to `0x006480cc`, the "as opened" copy CANCEL and the
   changed-since test read.

**So the checkbox never picks an airframe.** It chooses between the stock extras and a bare
airframe, and the airframe is whatever the record already carried. The only direct writer of
`+0x2c` is the AIRFRAME tab's own callback 2216 (`0x0040d296`); everything else arrives as a whole
record, and the one the surrounding screens use is callback **2014** (`0x00409518`, the copy at
`0x00409567`), which loads a plane into the build record. `HANGAR.SCRIPT` calls it on every change
of the inventory's plane dropdown, and the pilot-plane pick elsewhere makes a plane current the
same way. The default configuration is therefore the **stock build of the plane that was current
when the door was pressed**, which is why one take opened on a Balmoral from Instant Action and
another on a Bloodhawk from the cabin. A fresh profile's record is zeroed, so its airframe is 0.

The remake carries the rule as `HangarFeature.StartDefaultPlane(int airframe)` over the airframe
the door names: Instant Action's Pilot Plane pick on that door, the seated profile's own aircraft
on the cabin's, and `HangarFeature.DefaultAirframe` where no plane is current.

### When the airframe swap asks, and what its three answers do

`AIRFRAME.SCRIPT` is the one screen script that is not pure layout: its `gui_mailbox` case `10015`
(the dropdown's own selection-changed mail) carries the whole rule, and the only `0x8` messagebox
mask in the shipped scripts is at its line 48.

The pick first writes the airframe through callback **2216** (`0x0040d28d`, commit flag 1, the only
direct writer of record `+0x2c`) and compares the id it returns against the one held since
`gui_create`. **An id that has not moved ends the case there**: no box, no default configuration,
nothing. `CAP-50.mkv` shows the other half at t=124.8 to 127.0, an airframe swapped from Balmoral
to Fury with no dialog at all, because the record had only just been opened.

Where the id did move, callback **2221** (`0x0040b6b3`) is the gate: it compares the live build
record at `0x0064cb78` against its "as opened" copy at `0x006480cc`, field by field at the same
offsets, and answers whether anything was edited. The compared fields are the engine (`+0x30`),
both hardpoint counts (`+0x34`, `+0x38`), the paint pattern (`+0x40`) with its three colours,
shades and decals (`+0x44`, `+0x50`, `+0x5c`, three dwords each), the four armour zones (`+0x74`
to `+0x80`), the twin-mount byte (`+0x84`) and the four gun ids (`+0x88` to `+0x94`). **The
airframe is deliberately not among them**, since 2216 has already written it; neither are the name
and the ammunition dwords (`+0x98` up). The copy is retaken by every default-configuration load
(the `rep movsd` at `0x0040ac2c`, 51 dwords) and by callback 2014's plane load (`0x0040961b`), so
an answered question and a freshly loaded plane are both unedited again.

With the gate false the script skips the box and posts the answer the name screen's own checkbox
already gave (`@planeconstruction@UMA`, `PLANECONSTRUCTION.SCRIPT:126`): 3 with the box set, 4
with it clear. With the gate true it raises `messagebox.script` on mask `0x8` over the words
callback **2222** (`0x0040b62c`) builds, `langui` 206 formatted with two **short** airframe names
(`langui` 3020 + id): `%1` the airframe just picked, read off the live record, and `%2` the one
the as-opened copy carries. `OriginalScreenshots/Plane Construction Default Values Dialog.png`
shows it as "(Bloodhawk)" over "the Brigand you were building".

`MESSAGEBOX.SCRIPT`'s `0x8` mask activates all three buttons, gives them `langui` 102, 103 and 101
(Yes, No, Cancel) across `mb_b_left`, `mb_b_center` and `mb_b_right`, draws the query icon
(`mb_p_icon` frame 0, the frame `0x4` takes too), focuses the left button and posts 3, 4 and 2 for
them; its Escape posts 2, the right button's own answer. Back in `AIRFRAME.SCRIPT`:

| Answer | Posted | What it does to the record |
|---|---|---|
| Yes | 3 | `callback(2212, 1)`: the new airframe's stock template over the whole record |
| No | 4 | `callback(2212, 0)`: the same template, then stripped to a bare airframe |
| Cancel | 2 | `callback(2216, F, 1)` puts the previous airframe back and the dropdown with it |

**So No is not "keep my picks".** Both Yes and No rebuild from the template, which is why 206 tells
the pilot that Cancel is the way to keep an edit. The remake carries the rule in
`HangarFeature`: `EditedSinceOpened` over the same field set, `ApplyDefaultConfiguration(bool)` as
2212's two arms and `CancelDefaultsAsk` as the third, with the box drawn through
`CampaignBoards.Dialog`'s three messagebox slots.

### Export on the inventory writes nothing here

`HANGAR.SCRIPT` creates `ha_b_exportp` and never deactivates it, so Export draws live beside Sell
whatever the store holds. Pressing it runs `callback($$A$$, 22, -1)`, the screen-flow dispatcher's
save-custom-plane commit, and then the one-button messagebox over the string callback 2105
(`0x0040a0f7`) builds: `langui` 702 formatted with the plane's short airframe name, `langui`
`3020 + airframe` read at `0x0040a123`. The original's export is what makes a campaign plane
visible to Multiplayer and Instant Action, and both of the remake's presentations already pick out
of one saved-plane store, so the remake answers with the same confirmation and writes nothing.

### The purchase gate and what a build costs

Callback 2264 (`0x0040b477`) computes `FUN_00405680` over the scratch record at `0x0064cb78`
and reports `langui` 1226 "INSUFFICIENT FUNDS" when `nPlayerCash < cost`, alongside 1227
"OVERWEIGHT" and the missing-engine problem. The commit 2263 subtracts that same total and
writes the scratch record into a **new** slot, so a build is always paid in full: there is no
partial-upgrade price, and changing an owned plane means selling it (at full value) and
building again.

Per-unit constants, all from `FUN_00405680` / `FUN_00405550` and the two purchase-row handlers:

| Item | Cost | Weight |
|---|---|---|
| Airframe | stat table `0x00619bb0 +0x00` | `+0x04` |
| Engine | `FUN_004057c0` over `0x00619d98` plus the tier offsets | same table |
| Gun | `0x00619e68`, wing or turret column, doubled when twinned | same |
| Armour | **$4 per unit** | **4 lb per unit** |
| Hardpoint | **$410 each** | **480 lb each** |

The armour arithmetic in `FUN_00405680` is `(Σ of the four zone dwords) × 20 / 5`, and the zone
dword is the displayed unit count, so it reduces to $4 per unit. **The per-zone cap is 60
units**: the dropdown (callback 2246, `0x0040b7bd`) offers 13 rows numbered 0 to 12 and labels
row `r` as `r × 5` units. A fully armoured airframe is therefore 240 units, $960 and 960 lb,
which is the figure [`../formats/vehicle.md`](../formats/vehicle.md) reasons about against a
1900 lb `veh_weight`. Each wing hardpoint group offers 5 rows (0 to 4 hardpoints), matching the
eight rocket slots the Ammo Selection screen lays out.

### Ammunition and rockets are free

`ORDINANCELAYOUT.SCRIPT` (the Ammo Selection screen) invokes callbacks 2010, 2011, 2014, 2027,
2028, 2030 to 2037 only: plane icon, plane info line, gun names, the ammo and rocket dropdown
get/set pairs, their description panels, and accept/cancel. **No cost callback, no funds
readout and no wallet reference appear anywhere in it**, and no writer of `nPlayerCash` sits on
that path. Ammunition type and rocket type are a free per-sortie choice in the original; money
is spent in Plane Construction alone.

## Open

- The per-hit damage application order across zone/total and armour/structure pools is not
  decoded (entry points `FUN_004b9b30` / `FUN_004b9bc0` / `FUN_004b3800`).
- (Resolved elsewhere: the paint picks at +0x44..+0x64 are the colour/shade/decal index
  arrays, settled in [`formats/paint.md`](../formats/paint.md) "The swatch table and the
  pattern defaults" together with the table-shift note on pattern entries 10/12.)
- The gun table's +0x10/+0x14 stats: +0x14 matches the shipped `CLUSTER_SIZE` magazine series
  (2800/2400/2000/1600/1200) exactly; +0x10 is rate-of-fire-shaped; neither is traced to a
  combat consumer.
