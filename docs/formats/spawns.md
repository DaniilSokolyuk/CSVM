# Player spawns, the Instant Action config & the campaign mission map

Part of the [format documentation](README.md). Covers where a mission places the player and how
an instant-action mission is configured:
`ia.json` (instant action) and `objectives.json` (story missions), both in the **mission's
own zrdr archive** (`<chapter>/<mission>/zrdr.zbd`, a different archive than the shared
top-level zrdr). Spawn positions and headings are verified byte-exact
against the data for C1/IA1 `zeppelin_run` and side-by-side in-game for C3/M01. Consumed
by `CSVM/src/Flight/Modes/SpawnPoints.cs`.

## Instant Action spawns

`spawn_points` is a dict mapping scenario name → list of spawn entries
`[x, y, z, heading°]`. The original picks one entry at **random** per launch (e.g.
C1/IA1 `zeppelin_run` = 4 spawns, which are the first 4 of `dogfight_ace`; the other seven
chapters author the two lists independently). Scenario
names seen: `dogfight_ace`, `dogfight_squadron`, `stunt_flying`, `zeppelin_run`. No
throttle/speed fields here: an Instant Action spawn takes throttle 1.0 from the engine and
its speed from the same `PLAYER_INIT[4]` the story missions use (see "Story mission spawns").

⚠ **The distance between two authored entries is never a rule.** The builder `FUN_0045a390`
places at most three aircraft from the list: the player on `rand() % count`, the ace on
`rand() % (count - 1)` and the enemy flight's leader on another `rand() % (count - 1)`, each of the
latter two bumped to the last entry when it collides with the player's index. That collision bump
is the only separation the executable enforces. Everyone else is placed **relative to one of those
three**, on a fan off that entry's heading: member `i` sits `((i >> 1) + 1) × 100` m out on the
heading rotated by `±0.7853982` rad, the sign alternating with `i & 3`, at the entry's own altitude.
So a list's own pairwise spacing constrains nothing, and reading one list as "tighter" than another
describes the authoring, not a behaviour. Across the eight `IA1` files the closest authored pair is
176 m (C1B `dogfight_ace`) and the widest nearest-neighbour is 3047 m (C4 `zeppelin_run`);
`dogfight_ace` is the denser list in seven of eight chapters simply because it holds eight points
where `zeppelin_run` holds four.

Scenario names appear **only** in `ia.json` (spawn lists + `disallow_missions`); no
reader carries scenario-conditional world state, the world build is per-mission,
identical across scenarios (analysis in [anim-definitions.md](anim-definitions.md)).

`ia.json` also carries the stunt-mode `dzones` (fly-through Danger Zone) list, see
[missions.md](missions.md).

**A multiplayer mission (`MP1`–`MP3`) has no `ia.json`.** Its spawn list is `net.zrd`, which uses
this same `[x, y, z, heading°]` record and is documented in [net-spawns.md](net-spawns.md). It
also overrides the `PLAYER_INIT` throttle and speed those folders still ship.

## Instant Action configuration

The setup and wrap-up **UI** built around this data, the screen's dropdowns and their option
strings, environment ↔ chapter, the thirteen militias' aircraft lists, and which wrap-up rows are
actually wired, is [instant-action.md](instant-action.md), not this page.

`spawn_points` and `dzones` are two keys of many. Every chapter's `IA1/zrdr/ia.json` is a
complete, data-driven definition of that chapter's instant-action mission: which scenario it
runs, what the player flies, how many wingmen, four enemy waves, and a named ace with a full
livery. Key census over all 8 chapters (only `player_plane`/`num_wingmen`, absent on C2B, and
`dzones`, absent on C1C/C2B, are not present in all 8):

| Key | Value | Meaning |
|---|---|---|
| `mission_type` | one scenario name | which scenario this chapter's IA1 runs |
| `disallow_missions` | scenario names | scenarios this map cannot host (C1C bars `stunt_flying`, consistent with its having no `dzones`) |
| `player_plane` | display name (`"Bloodhawk"`) | the player's aircraft, the UI name, not a `vehicle.json` def |
| `num_wingmen` | `3` in all 7 | friendly flight size |
| `group1`…`group4` | nested dict | one enemy wave each: `num_enemies` (clamped to 6), `enemy_name` (a `MSG_*` key), `enemy_plane` (display name), `enemy_skill` (`novice`/`veteran`/`ace`). ⚠ **`enemy_skill` is read by nothing**, see [instant-action.md](instant-action.md) |
| `zeppelin_type` | `"cargo"` in all 8 | which of the three zeppelin slots the scenario uses |
| `cargo_zeppelin` / `passenger_zeppelin` / `military_zeppelin` | node name | the world node each type resolves to (`multiplayer1zep` throughout this install) |
| `ace_name` | `MSG_*` key | the named ace's display name (`MSG_PALBLAKE_NAME`) |
| `ace_plane` / `ace_skill` | display name / `"ace"` | the ace's aircraft |
| `ace_stats` | 9 numbers | the ace's pilot-skill modifiers (below) |
| `ace_accentID` | integer | voice accent id, same field as `vehicle.json`'s `accentID` |
| `ace_pattern`, `ace_color1..3`, `ace_decal1..3` | livery | the ace's paint scheme |

**The ace livery keys are our `PaintScheme` fields under different names.** `ace_pattern` is a
`paint_pattern` name (`blake`, `blckswan`, `hughes`, `hollywd`, `broadway`), `ace_colorN` are
0–255 RGB triples and `ace_decalN` are numbered decal indices, the same encoding
[paint.md](paint.md) documents for `vehicle.json`, so they feed `PaintScheme`/`PlanePainter`
directly with no translation.

**`ace_stats`, 9 values, inferred mapping.** Every chapter stores `[9,9,9,9,9,9,9,9,9]`, so
the data cannot discriminate the order. The structural evidence is strong: `vehicle.json`
carries exactly nine pilot-skill keys and all 26 defs that have them emit them in one identical
order, `dare_devil, natural_touch, sixth_sense, dead_eye, quick_draw, steady_hand,
stun_recovery, talker, constitution` — with `accentID` broken out separately, exactly as
`ia.json` breaks out `ace_accentID` after `ace_stats`. The original design describes the same
nine as its NPC pilot-skill modifiers, with higher = better; `9` is a maxed-out ace.
**Read as inferred, not decoded**, a chapter with a non-uniform `ace_stats` would settle it,
and this install has none. Non-uniform sets do exist inside `crimson.exe` (the record's own
`ace_stats` default and the five-row table Instant Action rolls wave pilots from, both on
[instant-action.md](instant-action.md)), but they are as unlabelled as the data is, so they
corroborate the count and the scale and leave the order where it was.

**What the whole `ia.json` is turned into** is the "setup path" section of
[instant-action.md](instant-action.md): the setup record, the mission-type ids, the synthetic roster
block every actor is spawned from, and which of these keys the engine never reads.

## Story mission spawns

```
PLAYER_INIT  [1, [x, y, z], [pitch, yaw, roll]°, throttle, speed]
```

Five elements. Only the yaw of the rotation varies between missions. Position + yaw
([1]/[2]) are confirmed correct: C3/M01's spawn matched the original side-by-side.

**Fields [3] and [4] are the player's spawn throttle and spawn speed**, each with exactly
one reader. The parse site is `FUN_00466b70` (the `objectives.zrd` loader; the string
`PLAYER_INIT` at `0062609c` has a single xref, from `0046777d`), which stores the five
elements into the mission singleton at `0071b480`:

| element | stored at | transform on the way in |
|---|---|---|
| [0] | `0071bb40` (`00467801`) | none |
| [1] x, y, z | `0071bb44/48/4c` | none |
| [2] pitch, yaw, roll | `0071bb50/54/58` | `× 0.017453292519943295` (double at `006040e8`), degrees to radians |
| [3] throttle | `0071bb5c` (`00467879`) | none, verbatim float |
| [4] speed | `0071bb60` (`0046788b`) | **`× 0.1`** (float at `006034a8`) |

**Field [3] is the throttle lever setting**, read at `0047f450` and copied to the player
aircraft's `+0x124` at `0047f45c`. That offset is the lever without ambiguity: the AI
throttle servo (`FUN_004209b0`) walks it at `dt × 0.35`, and the human throttle input path
(`FUN_0041b560`) adds into the same field.

**Field [4] × 0.1 is the spawn speed in metres per second**, read at `0047f4da`. The value
is negated and used as the scale on the nose axis (`FUN_0053fb40` rotates the unit vector at
`006379d0` by the spawn orientation), giving velocity at player `+0x924/928/92c`, speed² at
`+0x930` and its root at `+0x934`. The rotated vector is unit length, so the resulting speed
is exactly `field[4] × 0.1`. The unit is confirmed independently: the AI cruise setpoint
compared against `+0x934` is `80.4672` (`006036b8`), which is 180 mph × 0.44704 exactly.

**Field [0] is a pending flag, not the constant `1`.** `FUN_00443de0` tests it at `004440ab`
and only then calls the spawn routine, which clears it at `0047f3e2`. It means "PLAYER_INIT
not yet applied", consumed once per mission load.

### The authored values

Across the 51 `PLAYER_INIT` records in this install (every chapter's `M0x`, `MP0x` and `IA1`):

| field | value | records |
|---|---|---|
| [3] throttle | **0.8** | 49 |
| | 0.5 | C1/M02 |
| | 1.0 | C3/M01 |
| [4] × 0.1 speed | **18 m/s** (40 mph) | 48 |
| | 58 m/s (130 mph) | C1C/M01, C2B/M04 |
| | 15 m/s (34 mph) | C1/M02 |

### Which spawn path reads what

The player spawn/reset routine is `FUN_0047f1f0(apply)`. All of its "place the player"
branches share the one velocity computation above, so **the speed always comes from field [4]**;
the branches differ only in position, rotation and throttle.

| condition | position / rotation | throttle | speed |
|---|---|---|---|
| `apply != 0`, no override, mode ≠ 3 (story, multiplayer) | `PLAYER_INIT` [1]/[2] | `PLAYER_INIT` [3] | field [4] |
| `apply != 0`, no override, **mode 3 (Instant Action)** | random point via `FUN_0045a390` | **1.0** (`0047f3fb`) | field [4] |
| override spawn (byte at `0071daca` set) | `0071dad0`…`0071dae4` | 0 (`0047f3d2`) | field [4] |
| `apply == 0`, grounded | unchanged | 0 (`0047f313`) | 0 |
| `apply == 0`, airborne reset | altitude `+= 100.0` (`006032e4`) | 0.4 (`0047f28b`) | 20 m/s (`00608040`) |

⚠ **A multiplayer match does not end on the row that names it.** `FUN_0047f1f0` applies
`PLAYER_INIT` on load as the table says, and the network session init then re-places the pilot off
`net.zrd` on its own throttle and speed ([net-spawns.md](net-spawns.md)).

The mode test is `FUN_004639b0`, a one-line comparison of the mission object's `+0x700`
against 3, called at `0047f3e8`. Instant Action therefore ignores the authored throttle
(every `IA1` folder still carries one, at 0.8) but takes its speed from the same field as
everything else. `apply != 0` is reached only from `004440bf`; the two reset call sites
(`0047e15f`, `004804d9`) pass 0 and never read `PLAYER_INIT`.

⚠ **Spawn speed is NOT plane-dependent, and an earlier reading here said it was.** A sweep of
all 349 instructions of `FUN_0047f1f0` finds no reference to `fd_speed` (object `+0x668`) or to
the aircraft def pointer: the routine never touches per-aircraft data. The per-airframe rule that
does exist belongs to AI aircraft in the vehicle factory (`FUN_0047c210`), which spawns a
pathless aircraft at `min(plane_speed_max, fd_speed)`, and is documented in
[../org/flightModel.md](../org/flightModel.md).

⚠ **The remake's 53.6 m/s is a developer teleport constant, not a spawn rule.** `−53.6448` at
`0060803c` is read at three sites, all inside case `0x3b7` of the cheat-command dispatcher
`FUN_0047e080`, which teleports the player to the camera. 53.6448 / 0.44704 is 120.000 mph
exactly. The retired "candidate: 0.4 × `fd_speed` for the Bloodhawk" was a coincidence of that
number against one airframe, not a mechanism.

**The remake reads both fields.** `SpawnPoints.LoadPlayerInit` returns the whole record,
`SpawnPicker.StartState` answers the throttle and speed for the whole field (substituting 1.0 for
Instant Action), and `FlightController.Setup` carries them to the flight model's reset. Rigs with
no mission to read (AI aircraft, the labs, the unit tests) keep an older fixed start instead: the
original gives an AI aircraft `min(plane_speed_max, fd_speed)`, so borrowing the player's number
there would be a third invented answer rather than that rule.

⚠ **The start is below the wing's own stall speed, and that is correct.** 18 m/s sits under the
Bloodhawk's computed stall of about 25 m/s ([../org/flightModel.md](../org/flightModel.md)), so the
player is dropped in slow and accelerates out: 18 to 61 m/s in the first second, essentially level,
which is roughly 4.4 G along the flight path. The climb-out reads right at the controls. It is the
same sub-cruise band where that page records the force scale as a decode-versus-footage conflict,
so a future change to the force path will move this start's feel; the spawn speed is authored data
and is not the knob to compensate with.

## Campaign mission map

Each `objectives.json` carries `BRF_<REGION>M<n>` objective codes, the campaign's own
mission addressing. (The prose titles are `MSG_` keys resolved from a string table not
present in these extracts.) The region code + mission number does **not** always match
the folder's M-number:

| Region | Meaning | Missions → folders |
|---|---|---|
| `NW` | Northwest / Sea Haven (confirmed) | 1→C1C/M01, 2→C1/M02, 3→C1B/M03, 4→C1/M04, 5→C1/M05 |
| `HW` | Hollywood? | 1→C2/M02, 2→C2/M01 *(swapped)*, 3→C2/M03, 4→C2B/M04, 5→C2/M05 |
| `HA` | Hawaii / islands (confirmed) | 1–5→C3/M01–M05 |
| `RM` | Rocky Mountains / Colorado? | 1–5→C4/M01–M05 |
| `NY` | New York / Empire State? | 1–4→C5/M01–M04 |

Region *names* after NW/HA are inferred from the codes. Verified in-game: HA mission 1
(C3/M01) spawn position + heading matches the original side-by-side.

**A region's lettered folders are separate worlds, not lighting variants of one.** C1, C1B and
C1C all carry `BRF_NW*` missions, but they are three different terrain databases: the Sea Haven
airfield nodes (`ap_radiotwr`, `ap_transmitter`, `aphngr01.flt`, `apbuild01.flt`,
`ap_h2otwr.flt`, `refinery_flare`) exist in C1's gamez and in **neither** C1B's nor C1C's, and
their danger-zone name sets are disjoint, C1 is the airport and rail tunnels (Passenger
Hangar, Train Tunnel East/Mid/West, Bloodhawk Hangar), C1B is a coast of natural arches and sea
caves (Rock Archway, Mermaid's/Bootlegger's/Pirate's/Hobo's Tunnel). C1C and C2B carry no `dz*`
markers and no `dzpaths` group at all. C2 vs C2B is starker still: **zero** terrain meshes with
identical vertex data, and C2's studio-backlot landmarks (`ramses`, `sghangar`) are absent from
C2B. So one campaign region = several distinct sub-maps sharing a story region and an asset
library, reused across that region's 4–5 missions.

⚠ **`location.json` and `map.json` cannot tell chapters apart, both are stale copy-paste.**
Seven of eight chapters name the same `map_c1m04` minimap. C1, C1C and C2B ship a byte-identical
`Airport_terminal`/`Passenger_hangar`/`Crops`/`Coast` camera-preset list, and C2 ships those same
four plus a `Race Start`, Sea Haven airport bookmarks on two Hollywood maps. (C1B reuses C3's
list.) They are dev bookmarks left un-updated; use gamez node names or danger zones to identify
a world.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
