# Craters, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`). Every claim names the function it came from, so any of
them can be re-checked at source. No decompiler output is reproduced.

**Where the other halves live.** The authored side, meaning which weapons carry `CRATER` and what
this install ships in the block, is [`formats/weapons.md`](../formats/weapons.md). The detonation
that reaches this subsystem, including the flag word the gate reads and the animation suppression a
successful carve causes, is [`ordnanceTypes.md`](ordnanceTypes.md) "Detonation: the impact, then the
splash". The decorations a crater destroys are [`clutter.md`](clutter.md). Our implementation gates
a `WeaponDef.Crater` round on the struck node's flag as the original does, and keeps the carve
behind that gate: `CraterShape.cs` is the geometry, `CraterField.cs` the mission's
record list and the refusal, `TerrainCarve.cs` the mesh and collider surgery, `ClutterCull.cs` the
decoration destruction. What is faithful and what stands in for it is "How CSVM builds it" below.

**Scope.** This page covers the **engine-wide crater template**, the **weapon sub-block** that
overrides it, **what one carve actually builds**, the **three failure paths**, the
**bookkeeping** that decides how many craters a mission can hold, and the **node flag that keeps
every one of those from running in the shipped game**.

⚠ **Read "No shipped detonation reaches the carve" before building on anything above it.** The carve
is decoded, complete and unreachable from a played detonation: the gate is the struck node's
`CAN_MODIFY` flag, and no node in the install carries it. Everything this page describes about
building, clipping, tesselating and refusing a crater is code that runs only under the debug key.

## Function map

| Address | Role |
|---|---|
| `FUN_004e5590` | `zdec_init.cpp`: reads `declient.zrd`, fills the crater and quicksand templates and the texture table |
| `FUN_004e4870` | copies the crater template into a caller's request struct |
| `FUN_005ac690` | the weapon's carve request: fills a template copy from the hit and the weapon, then asks for the carve |
| `FUN_004ccb20` | `zclass/Class.c`: sets or clears a node's `CAN_MODIFY` bit, the flag the carve gates on |
| `FUN_004e4a10` | the entry point: an optional veto hook, then the instancer |
| `FUN_004e4890` | `zdec_crater.cpp`: the instancer, and the owner of the three failure strings |
| `FUN_004e4e00` | allocates the crater instance from the request |
| `FUN_004e4a40` | **build**: locates the terrain cell, clamps the centre, lays the rim ring, rejects overlaps |
| `FUN_004e4ea0` | **clip**: clips the rim ring against the terrain (`FUN_005272e0`) |
| `FUN_004e4f00` | **tesselate**: emits the bowl's polygons and names the scene node |
| `FUN_004e4830` | frees one instance (its point array and its mesh builder) |
| `FUN_004df560` | destroys the decorations inside the crater |
| `FUN_00526f10` | commits the built geometry into the terrain cells and bumps each cell's carve counter |
| `FUN_004e5e20` | appends the carve to the record list |
| `FUN_004e5d30` | removes every carve from the world and zeroes the per-cell counters |
| `FUN_004e5b40` | teardown: empties the record list and frees the texture table |
| `FUN_005ad630` | `zwep_ini.c`: the `.zrd` weapon dispatcher, which parses `CRATER` and `MAX_CRATER_RADIUS` |

## Two blocks are called `CRATER`, and only one of them is the weapon's

`FUN_004e5590` reads a block named `CRATER`, and so does `FUN_005ad630`, but they are different
blocks in different files. `FUN_004e5590` reads `declient.zrd`, an engine-wide configuration file,
and its `CRATER` block sets the defaults every crater in the game starts from. `FUN_005ad630` reads
the `CRATER` sub-block of one weapon entry in `weapons.zrd`, whose only job is to move three of
those defaults for that weapon.

⚠ **The weapon flag is `+0x74` bit `0x10`, not bit `0x2000`.** Bit `0x2000` is written by the key
parsed immediately before `CRATER` in the same dispatcher, `CATCHES_FIRE`
(`0x005ada81`; see [`formats/anim-definitions.md`](../formats/anim-definitions.md), "The behaviours
are dead data"). The `CRATER` branch begins at `0x005ada98`, sets bit `0x10` when the block is
present and clears it when it is absent. Both bits live in the `.zrd` dispatcher's flag word at
weapon `+0x74`, which is a different bit space from the extension struct's flags
([`ordnanceTypes.md`](ordnanceTypes.md), "A second flags word").

## The engine-wide template

`FUN_004e5590` opens `declient.zrd`, and if the file is missing it logs "Failed to read %s, using
defaults" and keeps the constants compiled into the function. The file exists, once per chapter, in
each chapter's `zrdr.zbd` and not in the top-level one, and **all eight chapters ship identical
numbers**, so the template is effectively a global. The template it fills is a 40-byte struct at
`DAT_00727cb0`; the quicksand template beside it at `DAT_00727cfc` has the same layout.

| Offset | Field | Key | Compiled fallback | Crater, as shipped | Quicksand, as shipped |
|---|---|---|---|---|---|
| `+0x00` | flags | none | `0x100c` / `0x1008` | `0x100c` | `0x1008` |
| `+0x04` | rim vertex count | `POINTS` | 7 | 7 | 7 |
| `+0x08` | struck material | none, the caller fills it | 0 | | |
| `+0x0c` | slope | `SLOPE` | 0.0 / 40.0 | 30.0 | 40.0 |
| `+0x10` | depth | `DEPTH` | 4.0 | 3.0 | 5.0 |
| `+0x14` | radius | `RADIUS` | 20.0 | 20.0 | 20.0 |
| `+0x18`…`+0x20` | centre x, y, z | none, the caller fills it | | | |
| `+0x24` | filled from the impact caller's fourth argument | none | | | |

The same block also carries `DEFAULT_TEXTURE`, `DEFAULT_ANIM` and a `TEXTURE_ANIM` list. Those build
a separate table at `DAT_00727cac` (`DAT_00727ca8` rows of 12 bytes: the ground material to match,
the texture the carve is skinned with, the effect the carve plays). Row 0 is the default pair, and
each `TEXTURE_ANIM` pair adds a row keyed on a ground material. `FUN_004e4e00` picks the row whose
key equals the request's `+0x08`, falling back to row 0, so **which texture and which effect a
crater shows is chosen by the ground that was hit**, not by the weapon.

⚠ **The shipped file spells the effect key `DEFAULT_EFFECT` and the engine reads `DEFAULT_ANIM`.**
The block authors `DEFAULT_EFFECT crater_explosion`, which `FUN_004e5590` never looks up, so row 0's
effect slot stays null and `FUN_004e4890`'s spawn is skipped on its own null test. **No crater in
the original plays an effect of its own**, and the name is dead twice over: `crater_explosion`
appears in no archive but the `declient.zrd` blocks themselves, so even a corrected key would
resolve to nothing. `DEFAULT_TEXTURE default` **is** read and does skin the carve. The file also
authors `TEXTURE_PARMS [1.0, 1.0]`, which the engine does not read, and no `TEXTURE_ANIM`, so the
table is one row and the ground material never selects anything.

⚠ **`SLOPE` has no reader on the crater path.** It is parsed into `+0x0c` and none of
`FUN_004e4890`, `FUN_004e4e00`, `FUN_004e4a40`, `FUN_004e4ea0` or `FUN_004e4f00` touches that slot.

## What the weapon's `CRATER` sub-block authors

The parse at `0x005adab0` does three things. It sets `+0x74` bit `0x10`; it copies the engine
template **twice**, into weapon `+0x194` and weapon `+0x1bc` (`FUN_004e4870`, 40 bytes each, so the
two copies are adjacent); and it then reads three keys, each a two-element list, out of the block:

| Key | Form | Minimum stored at | Span stored at |
|---|---|---|---|
| `points` | two ints | `+0x198` | `+0x1c0` |
| `radius` | two floats | `+0x1a8` | `+0x1d0` |
| `depth` | two floats | `+0x1a4` | `+0x1cc` |

Both destinations are seeded with the template's own value before the read, so an absent key leaves
minimum and maximum equal. The parse then stores element 0 as the minimum and `element 1 − element 0`
as the span. The first copy therefore ends up holding the three minima in their template slots, and
the second copy holds the three spans in the matching slots and nothing else of use.

`FUN_005ac690` spends them at detonation. It copies the **engine** template into a fresh request
(not the weapon's copy), then overrides each of the three fields only when its span is non-zero:

- `points` = `+0x198` + truncate(uniform draw × `+0x1c0`), gated on `+0x1c0 != 0`
- `depth` = `+0x1a4` + uniform draw × `+0x1cc`, gated on `+0x1cc > 0`
- `radius` = `+0x1a8` + uniform draw × `+0x1d0`, gated on `+0x1d0 > 0`

The uniform draw is `rand()` scaled by 1/32768, so the range is `[minimum, minimum + span)`.

⚠ **A zero span discards the authored minimum as well.** Because the request starts as a copy of the
engine template rather than of weapon `+0x194`, a weapon that authored `radius [10, 10]` would carve
at the engine's 20.0, not at 10.0. Only a genuine range reaches the request at all.

## The six carriers author nothing inside the block

Every one of `wep_04`, `wep_12`, `wep_25`, `wep_26`, `wep_27` and `wep_28` spells the key as
`CRATER [0]`: a bare scalar, with no `points`, `radius` or `depth` beneath it. All three spans are
therefore zero, all three overrides are skipped, and **every crater in the shipped game is the same
crater**: a 7-vertex rim, 20.0 radius, 3.0 depth. The randomisation exists and never fires.

## `MAX_CRATER_RADIUS` bounds nothing

It is a file-level key of `weapons.zrd`, not a weapon key. `FUN_005ad630` reads it at `0x005ad775`
into the global `DAT_00a1e170`, whose default `FUN_005ad4e0` sets to 30.0. **Nothing in the binary
reads that global.** An instruction sweep over the whole program finds exactly two references to the
address, the default store and the parse store, and no load. The install authors no
`MAX_CRATER_RADIUS` either, so the key is inert twice over: unauthored, and unread if it were
authored.

## Making one crater

`FUN_005ac690` runs from the direct-impact handler `FUN_005ac7a0` at two sites, `0x005ac83c` for the
struck surface and `0x005ac8bb` for the terrain record stashed in `DAT_00a1e17c`, both under
`+0x74` bit `0x10` and both skipped when the impact hook's suppression mask carries bit 2. It opens
with the node gate below, and only past it fills a request (position from the hit record's
`+0x0c`…`+0x14`, material from `+0x20`, the three sizes as above) and hands it to `FUN_004e4a10`,
which first offers the request to an optional veto callback at `DAT_00727ca4`. **No code in the
binary installs that callback**, so the veto never fires: `0x00727ca4` has one cross-reference
program-wide and it is `FUN_004e4a10`'s own read.

`FUN_004e4890` then runs the three stages, with a geometry tolerance global saved, set to 0.005 for
the duration and restored afterwards (`FUN_005600c0` / `FUN_005600d0`).

**Build (`FUN_004e4a40`).** Allocates the instance (`FUN_004e4e00`: an 80-byte record holding the
request at `+0x04`…`+0x2b`, a `points`-long vertex array at `+0x2c`, a mesh builder at `+0x44` and
the chosen texture row at `+0x4c`), queries the terrain for the cell under the centre
(`FUN_004da3b0`, then `FUN_004e6fc0`), clamps the centre inward so the whole disc stays inside the
cell's extents (nudging by 1.0 past the edge), and lays `points` vertices evenly around a circle of
`radius` at the impact height. It records the ring's 2D bounding box at `+0x30`…`+0x3c`, then walks
the cell's object list looking for overlaps (below).

**Clip (`FUN_004e4ea0`).** Hands the ring, the cell and the mesh builder to `FUN_005272e0`, which
clips the disc against the terrain's own polygons; on success the instance's vertex array and count
are replaced by the clipped boundary, so the rim follows the real ground rather than a flat circle.

**Tesselate (`FUN_004e4f00`).** Creates a scene node, names it `ZDEC_FEATURE`, and stores the
instance pointer in the node's `+0x40`. It then lowers the centre by `depth`, builds a mid ring
halfway between each rim vertex and the centre and lowers that by `depth` too, and puts the apex a
further `depth` below, so **the bowl is two rings deep and its floor sits `2 × depth` below the
impact**. It emits one quad per rim segment (rim pair to mid pair) and one triangle per segment (mid
pair to apex), skinned with the texture row's material and lit from the terrain where a lighting
probe succeeds and from a flat 0.8 where it does not.

On success `FUN_004e4890` appends the carve to the record list, commits the geometry into the
terrain cells (`FUN_004e5cd0`, then `FUN_00526f10`), would play the texture row's effect at the
crater centre through `FUN_004edc10` if the row carried one, destroys the decorations inside the
radius, and frees the instance; the geometry itself now belongs to the terrain.

## What a crater destroys

`FUN_004df560` takes the crater centre and radius, walks every terrain cell the bounding square
covers, and for each decoration whose origin lies within the radius (a squared-distance test,
`FUN_00538920`) sets bits `0x1` and `0x4` in the instance's word at `+0x48`. Those are the same bits
the clutter weapon-hit hook at `LAB_004df420` sets when a decoration's health runs out: `0x1` marks
it destroyed and `0x4` marks the cell's decoration set dirty. So a crater flattens the trees and
scenery inside it outright, with **no health test, no anim and no model swap**: `FUN_004df560` reads
no template field at all, which is why the `OnCrater` health/anim/model triple that `FUN_004deab0`
parses out of a `templates.zrd` chapter is not consumed on this path. No chapter authors `OnCrater`
either ([`clutter.md`](clutter.md)), so the block is dead twice over.

## The three failure paths

Each stage has its own message, logged through `FUN_00415330` with `zdec_crater.cpp` and a line
number, and each returns −1 to `FUN_004e4a10`.

| Message | Line | Cause | Cleanup |
|---|---|---|---|
| "Failed to instance crater: Build Failed" | 0x95 | `FUN_004e4a40` returned 0: no terrain object, the ground query missed, the cell's carve counter has reached the cap, or the footprint overlaps an existing carve | `FUN_004e4a40` freed the instance itself |
| "Failed to instance crater: Clip Failed" | 0xcd | `FUN_005272e0` produced fewer than one polygon, or the clipped result has no vertices | the instance is freed |
| "Failed to instance crater: Tesselation Failed" | 0xdc | the mesh builder could not be opened on the cell (`FUN_00527040` returned 0) | the instance is freed |

`FUN_005ac690` returns true only when the carve succeeded, and `FUN_005ac7a0` ANDs that into its
animation-suppression flag. So **a refused crater is invisible to the player twice over**: no
terrain changes, and the weapon's `ANIMATION` and `SURFACE_ANIMATION` slots play normally instead of
being suppressed. A successful crater suppresses both unless the weapon authors `ANIMATION_ALWAYS`,
which nothing in this install does ([`ordnanceTypes.md`](ordnanceTypes.md), "Half one, the direct
impact"). `zdec_qsand.cpp` carries the same three messages for `QUICK_SAND`, whose weapon flag
`+0x74` bit `0x40000` no entry sets.

## No shipped detonation reaches the carve

`FUN_005ac690` opens on a gate that stands in front of everything above. It reads the struck node off
the hit record (`hit + 0x24`, the field `FUN_004c7f50` fills with the node it recorded) and tests
that node's own flag word for bit `0x10000`:

| Address | Instruction | Meaning |
|---|---|---|
| `0x005ac698` | `MOV ECX,[ESI+0x24]` | the struck node, off the hit record |
| `0x005ac69b` | `MOV EDX,[ECX+0x24]` | that node's flag word |
| `0x005ac69e` | `XOR EAX,EAX` | the return value, pre-set to false |
| `0x005ac6a0` | `TEST EDX,0x10000` | bit 16, `CAN_MODIFY` |
| `0x005ac6a6` | `JZ 0x005ac795` | clear: return false, having touched nothing |

Bit 16 is **`CAN_MODIFY`**, and both halves of that identification are independent. Its setter is
`FUN_004ccb20` (`zclass/Class.c`), which ORs `0x10000` into node `+0x24` on a non-zero argument and
clears it otherwise; the GameGen keyword walk `FUN_004c2130` calls it at `0x004c2184` from the same
keyword table that fixes `INTERSECT_SURFACE` at 4, `LANDMARK` at 7 and `CLIP_TO` at 17
([`weaponRay.md`](weaponRay.md)). mech3ax's node-flag table names the same bit `CAN_MODIFY` and
glosses it "geometry can be modified by the destruction engine, this allows craters to be generated".

**No node in the install carries it.** Census over the extraction, every node of every tree:

| Tree | Nodes | `can_modify: true` |
|---|---|---|
| C1 | 7,064 | 0 |
| C1B | 5,603 | 0 |
| C1C | 5,644 | 0 |
| C2 | 4,956 | 0 |
| C2B | 4,901 | 0 |
| C3 | 5,408 | 0 |
| C4 | 8,289 | 0 |
| C5 | 11,438 | 0 |
| `planes` | 3,317 | 0 |

mech3ax's reader annotates the bit `CS never`, and the install round-trips byte-identically through
extract and repack, so the absence is exact rather than a reader gap.

**Nothing turns it on at runtime either.** `FUN_004ccb20` has exactly three callers. `FUN_004c2130`
is the load-time keyword applier above, which reads what the file authors. `FUN_004d7b90`
(`zclass/cls_util.c`) is the node-copy helper, which passes bit 16 of a source node to the setter on
a destination, so it can only propagate a bit already set. The third, at `0x005bad7c` inside
`FUN_005b80a0`, is the script command `NodeSetCanModify` (its name string at `0x0063eaf0`, beside
`NodeSetLighting`). A byte scan of every file in the retail install finds `NodeSetCanModify` only
inside `crimson.exe`, so no shipped archive invokes it.

**So the three failure paths are never reached.** A `CRATER` round in play returns from
`FUN_005ac690` at `0x005ac6a6` with false. `FUN_004e4a10` is never entered from a detonation, the
veto callback is beside the point, and `FUN_004e4890` never logs Build Failed, Clip Failed or
Tesselation Failed, because it never runs. The one live reach of `FUN_004e4a10` is the debug-key
handler `FUN_00443310`, which supplies its own template and world position and takes no hit record,
so the subsystem is exercisable in development and unreachable in play.

**A fused burst reaches the same gate and fails it too.** `FUN_005ac3a0` fills its synthetic hit
record's node field (`+0x24`) from the round's own `+0x0c` before calling `FUN_005ac7a0`, so a
`CRATER` weapon that detonates on its proximity fuse still enters `FUN_005ac690` and still fails the
flag test, rather than being kept out by a null record.

**Two of the four carve sites are dead a second way.** `FUN_005ac7a0`'s second crater site and both
quicksand sites read `DAT_00a1e17c` and `DAT_00a1e174`. Those globals are written in exactly three
places: `FUN_005abcf0`'s entry zeroes both (`0x005abcf8`, `0x005abcfe`), the init `FUN_005ad4e0`
zeroes both (`0x005ad53b`, `0x005ad540`), and `FUN_005abf80` would set them but **has no caller**.
Both are therefore always zero, and the only carve site with any reach at all is `0x005ac83c`.

**What that costs the presentation.** Because the carve never lands, `FUN_005ac690` always returns
false, `FUN_005ac7a0`'s suppression byte stays zero, and the six `CRATER` weapons always play their
`IMPACT` row's `ANIMATION` and `SURFACE_ANIMATION`. The "a successful crater suppresses both slots"
rule is real code with no reach in the shipped game.

## What a Choker ground hit shows

`wep_12`, the Choker, is one of the six `CRATER` carriers and the only one a human can fit, so it is
the case the question turns on. Its authored `IMPACT` table is four rows:

| Row | `ANIMATION` | `EFFECT` | `SOUND` |
|---|---|---|---|
| `default` | `scatter_effect` | none | `snd_missile_choker` |
| `water` | `bsplsh.flt` | none | `snd_bsplash` |
| `player` | `scatter_effect` | `rcochet1` | `snd_missile_choker` |
| `buildings` | `scatter_effect` | none | `snd_missile_choker` |

No row authors a `SURFACE_ANIMATION`, so nothing the Choker plays is ever rotated onto the struck
normal, and the `default` row authors no `EFFECT`.

The `TANGLER` parse installs the impact hook `LAB_004ba660`, which builds the choke cloud and returns
**1** ([`ordnanceTypes.md`](ordnanceTypes.md), "The choker, settled"). Bit 1 is the sound bit, so
`FUN_005ad100` is skipped and **a Choker impact is silent in the original**, on ground, water and
aircraft alike; what a player hears is the cloud's own presentation and the propeller wind-down of
whatever it catches. Bits 2 and 4 are clear, so the crater attempt and both animation slots are left
alone.

Flat C1 ground carries soil id `default`(0) or `dirt`(13), and `dirt` is named by no weapon in the
install, so it inherits `default`'s row whole ([`weaponImpact.md`](weaponImpact.md)). Either way the
row played is `default`. So one Choker on flat ground in the original spawns `scatter_effect` at the
hit point with no rotation, and nothing else: no sound, no row effect, no surface-laid animation and
no crater.

`scatter_effect` is the `ANIMATION_NAME` of the def `scatter_trails` in `scatter_control.zrd`. Its
sequence calls three things at the spawn point: `scatter_flashes` (a `scatter_light1` at colour
`0.95, 0.76, 0.24` and range 4 to 10, ramped out to 16 to 40 over 1.2 s and back over 0.8 s, then
made inactive), `call_scatter_trails`, and `large_black_smokeball`. **A gold flash, the scatter
trails and a large black smokeball is the whole of what the original shows**, which is the
"explosion then black smoke" a Choker drop reads as at the controls.

## Persistent, capped per cell, and never overlapping

A crater is **permanent for the mission**. Nothing ages it out, nothing recycles it, and there is no
fixed pool: the carve is merged into the terrain's own geometry and the record of it is appended to
a `std::vector` at `DAT_00727db4`…`DAT_00727dbc` that grows without a bound. Three separate
mechanisms hold the count down instead.

- **A per-cell cap.** Each successful carve increments a byte at terrain cell `+0x39`, once per cell
  it touched (`FUN_00526f10`). `FUN_004e4a40` refuses the build when that byte has reached the cap
  held on the terrain object at `world + 0x38`, byte `+0x5c`. Where that cap's value comes from was
  not traced.
- **No two craters may overlap.** Every carve leaves its `ZDEC_FEATURE` node in the cell with a
  back-pointer to its instance. `FUN_004e4a40` walks the cell's objects, and for each one named
  `ZDEC_FEATURE` compares the new ring's 2D bounding box against the recorded one, expanded by 5
  units on all four sides. Any overlap is a Build Failure. So repeatedly bombing the same spot
  produces one crater and then nothing, and craters cannot stack or interpenetrate.
- **Teardown.** `FUN_004e5b40` empties the record list (`FUN_004e60c0`) and frees the texture table.

The record list is also serialised. `FUN_004e5590` registers a writer and a reader under the name
`zDEClient` (`FUN_005bfce0`, into the archive registry `FUN_005c0100` builds). The writer walks the
list and emits one 52-byte entry per carve, named `Crater%d` for a crater and `QSand%d` for
quicksand; the reader replays each entry through `FUN_004e4890` with the effect argument zero, so a
restored crater is re-carved without replaying its animation. The reader's other arm, taken when the
registry's reset flag is set, calls `FUN_004e5d30`, which detaches every live carve's mesh, zeroes
the per-cell counters and deletes every `ZDEC_FEATURE` node in the world, then empties the list.

## The debug key

`FUN_00443310`, the debug-key handler, carries a case that copies the engine template, takes the
world position under the cursor and calls `FUN_004e4a10` directly, and another that does the same
for quicksand through `FUN_004e3bf0`. That path takes no weapon and so always carves at the engine
defaults.

## How CSVM builds it

**The gate is the original's, so no played round carves by default.** `GameZ` reads each node's
`flags.can_modify`, `SceneBuilder` stamps `CanModifyMeta` on every collider it attaches to a node
carrying it, and `CraterGate.For` tests that stamp on the struck collider before `ProjectilePool`
asks `CraterField` for anything ("No shipped detonation reaches the carve"). No shipped node carries
the flag, so a `CRATER` round on the ground leaves it intact and plays its `IMPACT` row, which for
the Choker is `scatter_effect` ("What a Choker ground hit shows"). `ImpactOutcome.Resolve`'s arm for
a carve that landed, which drops both animation slots and the stand-in as the original's AND does,
therefore never fires in play either.

⚠ **Do not remove the gate to get the bowl back.** Without it the first round on a fresh patch of
ground digs a crater and, through that same AND, loses its whole burst. The carve itself is kept
complete and callable directly (`CraterField.Request`, which the `crater-carve` suite drives), and
what it builds when asked, below, is what the original's own debug key builds.

**The remake's own carve has exactly two doors, and no menu row is one of them.** The options file's
`rocketCraters` key, set by hand, and the `--craters` flag each arm `CraterGate.Enabled` at boot,
under which every rocket warhead's ground burst carves whatever the struck node's flag says. ⚠ **No
screen offers it, deliberately**: the carve is remake-only chrome the original never shows in play,
so it is kept testable rather than advertised, and a build with no row is what leaves the default
behaving exactly as the paragraphs above describe. That is also what keeps every pinned golden clear
of a bowl (`--det` reads no saved option, and the flag is the one source that survives it). The ask
is typed rather than boolean (`CraterAsk`) so the option's carve stays purely additive: the burst
plays its own row over the new bowl, where the faithful path's AND drops both animation slots. An
aircraft's ground crash carving the same way is unjudged: it is deliberately not built, because the
crash fireball authors a scorch of its own and the two would have to be judged together.

What is faithful:

- The shape. Seven rim vertices at radius 20 on the ground the ring was clipped against, a mid ring
  halfway in and one `DEPTH` down, an apex two `DEPTH`s under the impact. No randomisation, because
  all six carriers author the block bare.
- The refusal. A new footprint is compared against every recorded one grown by 5 units on all four
  sides, and any overlap is refused outright. Bombing one spot twice produces one crater and then
  nothing, and that refusal is what bounds a mission's crater count.
- Permanence. The record list only grows, nothing ages a crater out, and the carve is merged into
  the terrain rather than pooled.
- The destruction. Every decoration whose origin is inside the radius dies outright, on the XZ
  distance alone with no height test and with no template field read.

What stands in for the original's own mechanism:

- **The terrain is a built scene, not the original's cell grid.** CSVM has no terrain cells, so
  there is no per-cell carve cap and no `ZDEC_FEATURE` node: the record list is per mission and the
  refusal is compared against all of it. With a 45-unit reach per crater the refusal alone holds the
  count to a bounded scatter, which is what the cap existed to do.
- **The carve rewrites the struck world node's own mesh and trimesh.** The ring is subtracted from
  every up-facing triangle it covers and the bowl is added as one further surface in the same skin,
  so the hole and the bowl share edges exactly. Both the mesh and the collision shape are replaced
  by private copies first, because `SceneBuilder` shares one of each per mesh index across every
  instance of a model.
- **Nothing is serialised.** The original writes its record list under `zDEClient` and replays it on
  load; CSVM's craters live for the mission and no save path reads them.

## Evidence & limits

- `FUN_004e5590`, `FUN_004e4870`, `FUN_004e4890`, `FUN_004e4a10`, `FUN_004e4a40`, `FUN_004e4e00`,
  `FUN_004e4ea0`, `FUN_004e4f00`, `FUN_004e4830`, `FUN_004e5b40`, `FUN_004e5cd0`, `FUN_004e5d30`,
  `FUN_004e5e20`, `FUN_004e60c0`, `FUN_004e5ac0`, `FUN_004df560`, `FUN_00526f10`, `FUN_00526230`,
  `FUN_005ac690` and `FUN_004deab0` were read in full.
- The `CRATER` parse at `0x005ada98`…`0x005adb69` and the `MAX_CRATER_RADIUS` read at `0x005ad775`
  were read from `FUN_005ad630`'s disassembly rather than its decompilation, because the dispatcher
  is too large to decompile usefully and the min/span arithmetic is clearer in the instructions.
  `CATCHES_FIRE` at `0x005ada81` was identified the same way and agrees with
  [`formats/anim-definitions.md`](../formats/anim-definitions.md)'s independent reading of the same
  bit.
- "Nothing reads `MAX_CRATER_RADIUS`" rests on an instruction sweep of the whole program for the
  address `0x00a1e170`: two hits, both stores.
- The `points` draw's scaling is inferred. The decompiler renders that branch as `rand()` followed
  by a bare `ftol()` with the multiply dropped, while it renders the `depth` and `radius` draws in
  full as `rand() × 1/32768 × span + minimum`. The `points` branch is read here as the integer form
  of the same expression, which is what its operands allow and what the two decoded siblings do.
- The three failure paths' cleanup was read from `FUN_004e4890`'s disassembly. The decompiler drops
  the argument to the Clip-Failed cleanup call and makes it look like a leak; the instruction at
  `0x004e4915` pushes the instance, so it is not one.
- The node gate was read from `FUN_005ac690`'s disassembly (`0x005ac698`…`0x005ac6a6`), and the bit
  named from `FUN_004ccb20`'s single store and from `FUN_004c2130`'s keyword walk, which reaches it
  beside the setters `weaponRay.md` already ties to `INTERSECT_SURFACE`, `INTERSECT_BBOX` and
  `LANDMARK`. "No shipped node carries it" is a count of `"can_modify": true` over every
  `nodes.json` the extraction produces, 56,620 nodes, zero hits. "Nothing sets it at runtime" rests
  on `FUN_004ccb20`'s three cross-references and on a byte scan of all 360 files of the retail
  install for `NodeSetCanModify`, which hits only `crimson.exe`.
- What the round's `+0x0c` holds, which `FUN_005ac3a0` copies into the synthetic hit record's node
  field, was not traced; the claim made from it is only that the field is non-null on the fused
  path, so the gate rather than a null check is what refuses the carve there.
- `FUN_005272e0` (the clip), `FUN_00527040` and `FUN_00526590` (the mesh builder and its polygon
  emitter), `FUN_00526350` (the lighting probe) and `FUN_004da3b0` / `FUN_004da430` (the terrain
  cell queries) were not opened; their roles are inferred from their arguments and from the
  arithmetic around the calls.
- Where the per-cell carve cap at terrain `+0x5c` gets its value was not traced. That it *is* the
  cap rests on the comparison in `FUN_004e4a40`, on `FUN_00526f10` incrementing the counter it is
  compared against, and on `FUN_004e5d30` zeroing that counter when it removes every carve.
- The clutter flag bits `0x1` and `0x4` were named from `LAB_004df420`, which sets the same two on
  a decoration whose health reaches zero. What reads them was not traced.
- The shipped `declient.zrd` numbers were read straight out of the retail `zrdr.zbd` of all eight
  chapters, by locating the key strings in the archive and decoding the reader node that follows
  each one. The project's extraction covers the top-level `zrdr.zbd` only, which does not carry the
  file, so `extracted/zrdr/` will not show it.
- The `DEFAULT_EFFECT` / `DEFAULT_ANIM` mismatch rests on both sides being read directly: the four
  key strings `FUN_004e5590` looks up sit at `0x0062eb90`…`0x0062ebd8`, and the block in the archive
  spells `DEFAULT_TEXTURE`, `DEFAULT_EFFECT`, `TEXTURE_PARMS`, `SLOPE`, `RADIUS`, `DEPTH` and
  `POINTS` and nothing else. That `crater_explosion` is defined nowhere rests on a byte scan of
  every `.zbd` in the install, which finds the name only inside the eight `declient.zrd` blocks.
