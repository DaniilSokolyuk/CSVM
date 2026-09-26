# Instant Action

Part of the [format documentation](README.md). This page describes the Instant Action mission
configuration surface: environments, mission types, militias and aircraft, wingman/wave/skill
ranges, the ace, and mission setup. Its sources are the Instant Action and wrap-up scripts,
layout data, UI strings, and each chapter's `IA1` reader data. [Spawns](spawns.md) holds the full
`ia.json` field census; [Instant Action wrap-up](instant-action/wrap-up.md) covers scoring and
friendly-fire rules.

## Contents

- [Screen controls](#screen-controls)
- [Table of Contents presets](#table-of-contents-presets)
- [Option strings](#option-strings)
- [Environment mapping](#environment-mapping)
- [Militias and aircraft](#militias-and-aircraft)
- [Ace](#ace)
- [Mission setup](#mission-setup)
- [Wave sequencer and mission end](#wave-sequencer-and-mission-end)
- [Wrap-up and scoring](instant-action/wrap-up.md)
- [Known UI limit](#known-ui-limit)
## Screen controls

`INSTANTACTION.SCRIPT` declares every widget and the engine callback that fills and reads it.
`LAYOUT.CSV`'s last column on a `D` (dropdown) row is the **visible-row count**, which for these
short lists equals the item count. Two rows are windows onto a longer list rather than counts:
`ia_d_planep` (see the trap below) and `ia_tl_contents`, whose 14 rows show part of a 19-item list.

| Widget | Fill / select callback | Rows | Contents |
|---|---|---|---|
| `ia_d_planep` | 2312 / 2313 | 20 | the 11 airframes, plus the player's saved custom planes |
| `ia_d_nwing` | 2314 / 2315 | 6 | 0 to 5 wingmen |
| `ia_d_planew` | 2316 / 2317 | 11 | the wingman aircraft; at a wingman count of 0 the box stands blank and inert while the count box beside it stays live |
| `ia_d_misstype` | 2318 / 2319 | 4 | the four mission types |
| `ia_d_environment` | 2320 / 2321 | 7 | the seven environments |
| `ia_d_nenemyN` (N in 0..3) | 2322 | 7 | 0 to 6 enemies, one per wave |
| `ia_d_egroupN` | 2324 | 13 | the militia flying that wave |
| `ia_d_planeeN` | 2330 / 2331 / 2332 / 2333 | 11 | that militia's aircraft. Each wave has its **own** list id, and selecting a militia resets the wave's plane index to 0 (`ZU[BA]`'s handler sets `AV[BA].QG = 0`), which is the script's own statement that the list depends on the militia |
| `ia_d_difficultyN` | 2326 | 4 | three skills in a four-row box |
| `ia_tl_contents` | 2300 / 2302 | 14 visible | the Table of Contents, 19 preset scenarios (decoded below; the screen itself is `BL-352`) |

Two behaviours matter beyond the option sets, both confirmed directly in the script. Selecting
mission type 0 (dogfighting an ace) disables every enemy control: `gui_init`'s per-wave loop tests
`0 == WT` and sends message `10000` to `ia_d_nenemyN`/`ia_d_egroupN`/`ia_d_planeeN`/`ia_d_difficultyN`,
and the `20001` mailbox handler (fired on a mission-type change) repeats the same test to send
`10000` or `10018` (enable), which is the script's own statement that **dogfighting an ace takes no
wave configuration**. A wave whose own count is 0 sends `10000` to its militia, skill and aircraft
and leaves its count box enabled (`gui_init`, and the `ia_d_nenemyN` arm of the `10015` handler).

⚠ **The script has two ways to take a box away, and they draw differently.** `mail (10000, box)`
disables it: the field sub-object `FV` sets its frame to `0x80000000` and stops printing its value,
while still filling its background, so the box keeps its place on the page, blank, with its arrow in
the disabled frame (`CAP-50.mkv` t=13.0 for the ace duel, t=70.6 for count-0 waves). `deactivate(box)`
removes it: the enemy rows are paged, mailbox `20002` calling `deactivate` on every row not on the
current page and `activate` on the rest, keyed off which of the `ia_b_up`/`ia_b_down` buttons was
pressed, and the film shows wave 0's row alone on page 1 (t=13.0) and waves 1 to 3 on page 2 with
page 1's rows gone, not blanked (t=70.6). Both paging buttons draw on both pages, the one with
nowhere to go disabled by `10000` rather than deactivated. The Weapon Loadout screen's missing gun
groups and unbought pylons are the `deactivate` case (`ORDINANCELAYOUT.SCRIPT`), so there the field
is not drawn at all and only its caption survives; the rule is the script's, keyed on the airframe's
own answer rather than on any one aircraft.

**⚠ `ia_d_planep`'s 20 is not twenty aircraft.** The script sizes that list from
`callback($$A$$, 1024)` (the player's saved-plane count) `+ 11`, and `gui_continue` re-checks the
same count each frame to decide whether the default selection is index 0 (no custom planes) or
index 11 (the first custom plane). 20 is `LAYOUT.CSV`'s visible-row window for that variable-length
list, not an item count. `ia_tl_contents` is the other row where the two differ, and there the
longer list is fixed at 19 rather than varying with the player's saved planes.

## Table of Contents presets

The list down the left of the screen offers **19 named preset scenarios**, and selecting one fills
every other control. No shipped data file carries them: `crimson.exe` holds the table.

**Where.** 19 records of `0x230` (560) bytes at **`0x0061b090`**, running to `0x0061da20`.
`FUN_004102c0(index)` copies one record over the live setup struct at **`0x0064ab5c`**, which has
the same layout, then recomputes the derived masks below. The three callbacks resolve inside the
`uiData` script-callback handler (registered under that name by `FUN_004075d0`, entry `0x004093a0`):
2300 fills the list, writing the literal count `19` at `0x0040c019` and fetching each row's text as
langui `3600 + index`; 2301 returns the stored index and mission type; 2302 stores the index, calls
`FUN_004102c0` and returns the new mission type.

**Record layout.** Every field is an `int32` unless noted, and offsets are from the record's start.

| Offset | Field |
|---|---|
| `+0x00` | the preset's own index |
| `+0x08`, `+0x0c` | the screen's current player / wingman aircraft selection, mirroring `+0x80` and `+0x84` |
| `+0x10` | 7 bytes, one allowed flag per environment. Derived, stored as zero |
| `+0x17` | 4 x 11 bytes, one allowed flag per aircraft per wave. Derived, stored as zero |
| `+0x44` | mission type, in the dropdown order (0 ace, 1 squadron, 2 stunt, 3 zeppelin) |
| `+0x48` | environment, in the dropdown order |
| `+0x4c` | wingman count |
| `+0x50` | 4 dwords, enemies per wave |
| `+0x60` | 4 dwords, militia per wave |
| `+0x70` | 4 dwords, skill per wave |
| `+0x80`, `+0x84` | player and wingman aircraft |
| `+0x88` | 4 dwords, enemy aircraft per wave |
| `+0x98`, `+0x164` | player and wingman plane records, 204 bytes each |

**Out-of-range values are sentinels, and applying a preset normalises them.** A militia of 13, a
skill of 3 and an aircraft of 11 are each one past their dropdown's last row and mean "unset".
`FUN_004102c0` rewrites an unset wave's militia to 4, its skill to 1 and its aircraft to 5, and the
mission builder `FUN_004175f0` then skips any wave whose enemy count is 0, so the substituted values
never reach a mission.

**The two derived masks are what a preset's mission type and militias then permit.**
`FUN_004103b0` rebuilds the 7-byte environment mask from the mission type, and `FUN_00410420(wave)`
rebuilds that wave's 11-byte aircraft mask from its militia (see "Militias and aircraft").

**The presets.** Waves read as `count x militia aircraft (skill)`.

| # | Name | Mission | Environment | Player | Wingmen | Waves |
|---|---|---|---|---|---|---|
| 0 | Girl Trouble | squadron | Sky Haven | Firebrand | 2 x Peacemaker | 4 x Medusa Kestrel (veteran); 2 x Black Swan Fury (ace) |
| 1 | Sour Grapes | stunt | Manhattan | Bloodhawk | 0 | 4 x Blake Aviation Bloodhawk (veteran) |
| 2 | Me and My Big Mouth | ace | the ocean | Autogyro | 0 | none |
| 3 | The Angry Luau | zeppelin | Hawaii | Fury | 4 x Hellhound | 6 x Fortune Hunter Devastator (ace) |
| 4 | Hat Trick | squadron | an airfield | Brigand | 0 | 3 x Black Hat Warhawk (veteran) |
| 5 | Seaside Show-Off | stunt | the ocean | Peacemaker | 2 x Kestrel | 4 x Hughes Aviation Fury (veteran) |
| 6 | Two to Tango | ace | Sky Haven | Hellhound | 0 | none |
| 7 | Death of the Gemini | zeppelin | the clouds | Kestrel | 4 x Kestrel | 6 x Hughes Aviation Bloodhawk (ace); 3 x Blake Aviation Peacemaker (veteran) |
| 8 | Hares and Tortoises | squadron | a movie studio | Bloodhawk | 2 x Bloodhawk | 4 x Studio Security Autogyro (novice); 4 x Black Hat Warhawk (veteran) |
| 9 | Swan's Gauntlet | stunt | an airfield | Fury | 0 | 4 x Black Swan Fury (ace) |
| 10 | Aloha, Ace! | ace | Hawaii | Devastator | 0 | none |
| 11 | Black Hats and Hoplites | zeppelin | Sky Haven | Autogyro | 2 x Autogyro | 6 x Black Hat Brigand (veteran) |
| 12 | From Russia with Hate | squadron | the clouds | Fury | 2 x Peacemaker | 4 x Russian Devastator (novice); 4 x Russian Devastator (veteran); 2 x Russian Devastator (ace) |
| 13 | Honor, Hollywood Style | ace | a movie studio | Brigand | 0 | none |
| 14 | Manhattan Tea Party | zeppelin | Manhattan | Warhawk | 4 x Devastator | 6 x British Peacemaker (veteran); 4 x Sacred Trust Hellhound (ace) |
| 15 | Let's You, Me, and Him Fight | squadron | the ocean | Hellhound | 3 x Firebrand | 4 x Russian Devastator (veteran); 4 x Fortune Hunter Kestrel (ace) |
| 16 | The Longest New York Minute | ace | Manhattan | Brigand | 0 | none |
| 17 | Rocky Mountain Hijinks | stunt | Sky Haven | Peacemaker | 0 | 6 x Sacred Trust Warhawk (veteran) |
| 18 | The Hollywood Brawl | zeppelin | a movie studio | Devastator | 4 x Kestrel | 6 x Hollywood Knight Firebrand (ace); 4 x Studio Security Autogyro (ace) |

The names are langui 3600 to 3618 in that order. All five ace presets carry no waves, which is the
same rule the script's `0 == WT` branch enforces on the screen. All four mission types and all seven
environments appear, and no environment is tied to one mission type, so the presets are not a
per-environment set.

**Presets fly stock airframes, so nothing here depends on the hangar.** A record's own two plane
records are not read: `FUN_004102c0` overwrites both from the stock airframe table at `0x00619f58`
(stride `0xcc`, the same 204 bytes a saved custom plane file carries, see
[paint.md](paint.md)) indexed by `+0x80` and `+0x84`, then sets each name to langui 134, `"Stock"`.

**⚠ *View Story* opens the wizard page, not a written story.** The path at `0x0040c07d` fetches
langui `3600 + index` and formats it through langui 1074 `IDS_IA_STORYTITLE`, whose whole text is
`%1!s!`. The script declares two story widgets and no prose pane: `ia_t_storytitle`, which receives
that formatted name, and `ia_t_storyinstr`, which is the fixed langui 1063, "Want to create and then
FLY your own mission? Use the drop-down lists below…". The second page is the configuration screen
with the preset's name as its heading, so there is no per-preset prose to find in `langui` or in
`crimson.rof`.

## Option strings

From the `langui` table, read out of `extracted/rof/ui_strings.json`.

| Block | Base id | Values |
|---|---|---|
| Environments (`IDS_IA_ENVIRONMENT`) | 3650 | an airfield, the clouds, Hawaii, Manhattan, the ocean, Sky Haven, a movie studio |
| Mission types (`IDS_IA_MISSIONTYPE`) | 3660 | dogfighting an ace, dogfighting a squadron, stunt flying, attacking a zeppelin |
| Militias (`IDS_IA_MILITIAS`) | 3670 | Black Hat, Black Swan, Blake Aviation, British, Fortune Hunter, Hollywood Knight, Hughes Aviation, Medusa, Russian, Sacred Trust, German, Studio Security, Broadway Bomber |
| Skills (`IDS_IA_DIFFICULTY`) | 3695 | novice, veteran, ace |
| Aircraft, plural forms (`IDS_IA_PLANES`) | 3700 | Hoplites, Hellhounds, Balmorals, Bloodhawks, Brigands, Devastators, Firebrands, Furys, Kestrels, Peacemakers, Warhawks |

Every block is contiguous and was walked id-by-id to confirm the count and order above; no gaps.

**The autogyro has two names in the shipped data.** The UI's plural list (id 3700) calls it a
**Hoplite**; `ia.json`'s `enemy_plane`/`player_plane`/`ace_plane` values use the singular vehicle
display names and call it **Autogyro** (see [spawns.md](spawns.md)). A def carries the `ia.json`
spelling; the setup and wrap-up UI render the plural. Treating these as two different aircraft
would make the plane list look like it has twelve entries.

`ground_target` is a fifth mission type every chapter's `ia.json` disallows (see the table below),
which is why the UI offers only four.

## Environment mapping

Seven strings, eight chapters. **The mapping is decoded, and so is the dropdown's order**: the
launcher `FUN_004174d0` switches on the environment dropdown index and writes a chapter id, so the
row order below is the executable's own (see "What the launcher maps"). `mission_type` and
`disallow_missions` are the two `ia.json` keys that decide what a chapter's Instant Action can host;
they are reproduced here (not duplicating [spawns.md](spawns.md)'s key-meaning census, which does
not carry per-chapter values) because they are what each row can then be *offered*.

| Row | Environment | Chapter | `mission_type` | `disallow_missions` |
|---|---|---|---|---|
| 0 | an airfield | C1 | dogfight_squadron | ground_target |
| 1 | the clouds | C2B | zeppelin_run | ground_target, stunt_flying |
| 2 | Hawaii | C3 | dogfight_squadron | ground_target |
| 3 | Manhattan | C5 | stunt_flying | ground_target |
| 4 | the ocean | C1B | stunt_flying | ground_target |
| 5 | Sky Haven | C4 | stunt_flying | ground_target |
| 6 | a movie studio | C2 | stunt_flying | ground_target |
| *(none)* | *(not offered)* | C1C | zeppelin_run | ground_target, stunt_flying |

⚠ **C1C is the chapter Instant Action omits, not C2B.** The launcher's map covers chapter ids
1, 5, 6, 8, 2, 7, 4 and never 3, and `CSVM/src/UI/Screens/LaunchMenu.cs` has carried this mapping
(`c1, c2b, c3, c5, c1b, c4, c2`, C1C campaign/MP only) since before this page existed.

**This page originally had two of these rows wrong**, pairing "the clouds" with C1C and calling C2B
the excluded chapter, on the strength of C2B being the only chapter whose `ia.json` omits
`player_plane` and `num_wingmen`. That argument does not hold: the world-content evidence never
discriminated between C1C and C2B in the first place (both bar stunt flying, both ship no `dzones`,
both run `zeppelin_run`), so the pairing rested on elimination, and the missing keys mean only that
the setup screen supplies them, which A3 showed it does for every chapter anyway. Do not
reintroduce either claim.

**The screen enforces one of these bans itself, without reading `ia.json`.** `FUN_004103b0`
rebuilds a 7-byte per-environment allow mask whenever the mission type changes: types 0, 1 and 3
allow all seven rows, and type 2 (stunt flying) clears row 1, the clouds. That is the same
exclusion C2B's `disallow_missions` carries, arrived at independently, and no stunt preset selects
row 1.

`num_wingmen` is `3` in all seven chapters that carry it (the eighth, C2B, carries neither
`player_plane` nor `num_wingmen`); the wingman range is 0 to 5 per `ia_d_nwing`'s row count above.

## Militias and aircraft

Decoded from `FUN_00410420`, which the setup screen calls once per wave: it switches on that wave's
militia index and writes an 11-byte allow mask, one flag per aircraft in the langui 3700 order, at
`0x0064ab73 + wave * 11`. The table below was first inferred by inverting
[paint.md](paint.md)'s "Patterns are per aircraft" table (measured over the `.BM` skins each pattern
ships in `crimson.rof`) and matching pattern names to the militia strings above; the binary agrees
with that inference on all thirteen militias, and every one of the 19 presets picks an enemy
aircraft inside its own militia's set.

| Militia | Pattern | Aircraft |
|---|---|---|
| Black Hat | `blackhat` | Warhawk, Brigand, Autogyro |
| Black Swan | `blckswan` | Fury |
| Blake Aviation | `blake` | Bloodhawk, Peacemaker |
| British | `british` | Peacemaker, Balmoral |
| Fortune Hunter | `player_fortune` | all eleven |
| Hollywood Knight | `hollywd` | Firebrand |
| Hughes Aviation | `hughes` | Bloodhawk, Kestrel, Fury |
| Medusa | `medusas` | Kestrel, Brigand |
| Russian | `cccp` | Devastator |
| Sacred Trust | `sactrust` | Warhawk, Hellhound |
| German | `german` | Hellhound |
| Studio Security | `studio` | Fury, Autogyro |
| Broadway Bomber | `BROADWAY` | Peacemaker |

### A militia also picks the wave's voice

⚠ **The militia is the only thing that gives an Instant Action wave a voice.** The same switch that
writes a wave's pattern, three decals and nine colour components writes that wave's
`enemy_accentID` beside them, one literal per militia, at wave record dword 6
(`0x00718dc4 + wave·0x60`). The thirteen writes are `0x00417ead`, `0x00417fe1`, `0x00418112`,
`0x00418249`, `0x00418367`, `0x0041847c`, `0x004185b6`, `0x004186c8`, `0x004187fb`, `0x00418926`,
`0x00418a51`, `0x00418b84` and `0x00418c14`, in militia order:

| Militia | Pattern | `enemy_accentID` |
|---|---|---|
| Black Hat | `blackhat` | 0 |
| Black Swan | `blckswan` | 1 |
| Blake Aviation | `blake` | 2 |
| British | `british` | 3 |
| Fortune Hunter | `fortune` | 12 |
| Hollywood Knight | `hollywd` | 7 |
| Hughes Aviation | `hughes` | 10 |
| Medusa | `medusas` | 8 |
| Russian | `cccp` | 5 |
| Sacred Trust | `sactrust` | 9 |
| German | `german` | 6 |
| Studio Security | `studio` | 10 |
| Broadway Bomber | `broadway` | 4 |

Two militias share accent 10, and Fortune Hunter's 12 is the wingman range's own base, so a wave
flying the player's militia speaks with the wingmen's voices, re-rolled to 12 to 16 per aircraft at
spawn (see "The ace and the waves"). Every one of these ids is a row in `voice.zrd`'s multi-id
range 0 to 10, so all thirteen reach a pilot who owns clips
([combat-voice.md](combat-voice.md)). A wave whose enemy count is 0 is skipped before the switch
runs, so its accent stays at the record reset's -1.

`ITSTAXI` is a fourteenth pattern folder and covers the Autogyro, but no militia string names it,
so it is not an Instant Action militia. `BROADWAY` and `ITSTAXI` ship no `paint_pattern` in
`vehicle.json` and therefore have no canonical colours; `PatternLibrary` already offers them with
whatever colours are current, which is the behaviour a Broadway Bomber wave inherits.

**⚠ This reading, not `vehicle.json`'s `paint_pattern` coverage, is the one to build against.**
Under the def reading the largest militia has three aircraft and Sacred Trust has Hellhound alone;
`FUN_00410420` gives Fortune Hunter all eleven and Sacred Trust two, so the def reading is the
wrong one. Do not restate the older argument for this table, that `ia_d_planeeN`'s eleven rows
require some militia to reach eleven: that dropdown's 11 is a `LAYOUT.CSV` row window like
`ia_d_planep`'s 20, and the mask, not the window, is the evidence.

**The mask's bit order is the aircraft dropdown's order**, which is the langui 3700 order
(Autogyro, Hellhound, Balmoral, Bloodhawk, Brigand, Devastator, Firebrand, Fury, Kestrel,
Peacemaker, Warhawk) filtered to the allowed flags, not the order the table above happens to list
them in.

## Ace

Every chapter's `ia.zrd.json` names one ace in full, not a random draw: `ace_name`, `ace_plane`,
`ace_skill` (always `"ace"`), `ace_stats` (always `[9,9,9,9,9,9,9,9,9]`), `ace_accentID`, and a
complete `ace_pattern`/`ace_colorN`/`ace_decalN` livery, authored in all 8 of 8 chapters, censused
The full field table, the `PaintScheme` mapping and the `ace_stats` order inference are
already on [spawns.md](spawns.md); this page does not repeat them.

## Mission setup

`crimson.exe` establishes this. The file half is loaded by `FUN_0045a150` (opens `ia.zrd`,
fills the per-mission-type spawn table, then calls the key parser `FUN_00459390` on the setup
record) and the mission is built by `FUN_0045a390`. The setup record is the global at
**`0x00718cd8`**; the per-mission-type table is `0x00718fe0`, five entries of 20 bytes, holding an
availability flag `disallow_missions` clears and that scenario's spawn-point vector.

### The setup screen is written over the file, every time

⚠ **The key parser's last act is to copy the setup screen over the record it has just filled.**
`FUN_00459390` ends with an unconditional call to `FUN_004175f0` at `0x00459e0c`, which reads the
screen's own live setup struct at `0x0064ab5c` (the same struct a Table of Contents preset is
copied into) and writes: the mission type, the player and wingman aircraft, the wingman count, the
player's three decals and three packed colours, and then per wave the enemy count
(`0x0064abac[wave]`), the aircraft (`0x0064abe4[wave]`), the skill (`0x0064abcc[wave]`), the
enemy's display name, and everything the militia (`0x0064abbc[wave]`) decides. A wave whose count
is 0 skips straight to the next, which is why a preset's unset-wave substitutions never reach a
mission.

So a shipped `ia.zrd` value is a *default*, not the launch: for every key the screen also writes,
the file's value survives only on a path that never opens the screen.

### Mission types have internal ids, and they are not the dropdown order

`FUN_00458c20` maps the scenario name to an id, and every consumer indexes by it:

| id | name |
|---|---|
| 0 | `dogfight_ace` |
| 1 | `dogfight_squadron` |
| 2 | `zeppelin_run` |
| 3 | `ground_target` |
| 4 | `stunt_flying` |
| 5 | *(unrecognised; the parser then leaves the field alone)* |

⚠ **The UI dropdown order is ace, squadron, stunt, zeppelin; the internal order is not that.**
`zeppelin_run` is id **2** and `stunt_flying` is id **4**. Anything in the executable that switches
on "mission type 2" is the zeppelin run, including `FUN_0045b9d0`'s generator-capacity branch.

### The built-in defaults

`FUN_00458ff0` resets the record before the file is read, so every key the file omits still has a
value. Worth knowing because four of the parsed keys are authored by no chapter (below).

| Field | Default |
|---|---|
| `mission_type` | 0 (`dogfight_ace`) |
| `player_plane` / `wingman_plane` / `ace_plane` | 5 (**Devastator**) |
| `num_wingmen` | 0 |
| `ace_name` | `Marshall Bill Redmann` |
| `ace_stats` | `[5, 6, 6, 8, 9, 6, 7, 6, 9]`, non-uniform |
| `ace_skill` | 1 (`veteran`) |
| `ace_accentID` | -1; decals -2; colours -1 |
| `ground_target_name` / `ground_target_node` | `Cargo Train` / `trcargo01` |
| the three `*_zeppelin` node names | `vostokzep` |
| `zeppelin_type` | 0 (**cargo**), `param_1[0x95] = 0`, i.e. record `+0x254`. An unrecognised string is rejected rather than stored (below), so this is also what a typo resolves to |
| per wave (`FUN_00458d00`) | `num_enemies` 0, `enemy_name` `Blake Firebrand`, `enemy_plane` 6 (**Firebrand**), skill 1, decals -2, colours -1 |

The aircraft index is the `IDS_IA_PLANES` order (0 Autogyro, 1 Hellhound, 2 Balmoral, 3 Bloodhawk,
4 Brigand, 5 Devastator, 6 Firebrand, 7 Fury, 8 Kestrel, 9 Peacemaker, 10 Warhawk), confirmed by
`FUN_00426d80`'s name table and corroborated by the wave default pairing `Blake Firebrand` with
index 6. A name that matches nothing resolves to `0xb` and the parser substitutes **5**.

Each aircraft row carries seven names; the two this path uses are `w<plane>` (`FUN_00426d60`, the
wingman def) and `<plane>` (`FUN_00426d20`, the plain AI def), beside the display name the file
spells and the `player_<plane>` def.

### Every actor is a synthetic `aiv` roster block

`FUN_0045a390` builds a roster-block record on the stack, fills it, and hands it to
**`FUN_0047c210`**, the same roster spawn story missions use ([ai-rosters.md](ai-rosters.md)). So
an Instant Action aircraft is an ordinary roster vehicle; the only difference is that its block is
computed rather than read from `aiv.zrd`. `FUN_00437360` is the block constructor and its defaults
stand wherever this path writes nothing (`-1` unset, decals `-2`, `enabled` 1, team 0, group 0).
The offsets confirm M4 B7's decode from the other direction: team at `+0x34`, group at `+0x38`,
`primary_target` at `+0x44`.

`FUN_0045a240` then overwrites all three activation volumes with radius **10000 m** and an altitude
band of **±10000 m** for every Instant Action actor, which is the engine's own way of saying they
are always awake, always willing to engage, and never return.

`netids` kept its `-1`). All three branches of `FUN_0045a390` write a one-entry `netids` list
holding the **first id in the chapter's net table** (`0x0045a8b4` for the wingmen, `0x0045ab18` for
the ace, `0x0045ae85` for the waves), so every Instant Action aircraft walks the chapter's first
patrol graph. That table is built in `neindex.zrd.json` FILE order, not sorted, so "first" is its
first pair: **net 10 `M4ReinfAce` (C1), 29 `Patrolboat3` (C1B), 25 `M1Defense` (C1C), net 1 in the
other five**. On C1B and C1C that is not the lowest id, which is 11 on both. The 10000 m
volumes above survive the net assignment: the net can overwrite a vehicle's volumes, but the roster
block is copied over it afterwards and this block authors all nine
([`org/aiPilot.md`](../org/aiPilot.md), "Net assignment"). Two consequences, both read off the code path rather than observed at the controls of
the original: the wingmen's `w<plane>` defs carry `mode wingman`, but a net demotes a wingman to
`jet` at spawn, so **the escort chain below is not flown as a formation in this mode**; it survives
as a target assignment only. [`org/aiPilot.md`](../org/aiPilot.md) has the demotion rule and the
formation law it suppresses.

⚠ **That first net is a CAMPAIGN MISSION's asset, not a generic patrol area** (censused
Net names are mission-scoped, the prefix naming the mission that uses them
([`ai-nets.md`](ai-nets.md), "Net names are mission-scoped"), and each chapter's first net is
referenced by exactly one mission, or by nothing at all:

| Chapter | First net | What owns it in the campaign |
|---|---|---|
| C1 | 10 `M4ReinfAce` | M04's `blakebloodhawk_8` (one `aiv` block, by id) |
| C1B | 29 `Patrolboat3` | M03's `objectives` only |
| C1C | 25 `M1Defense` | M01's `aiv` + its `egen` |
| C2 | 1 `M2First` | M01's `aiv` (4 blocks) |
| C2B | 1 `PirateZep1` | M04's `zeppelins`: it is `piratezep`'s OWN flight path |
| C3 | 1 `M1Medusas` | M01's `objectives` |
| C4 | 1 `M1Train` | M01's `objectives` |
| C5 | 1 `M1Bravo` | nothing, anywhere in the chapter |

So Instant Action does not hand out a patrol area designed for it. It takes index 0 of the chapter
table, whatever that happens to be: on C1 an ace's approach pattern, on C1B a patrol BOAT's route,
on C2B the pirate zeppelin's own course, on C5 a net no mission uses. That is the decoded
behaviour and it is faithful; it is recorded here so the odd shapes it produces are not read as a
bug in the follower.

### The player and the wingmen

The player is placed at a **uniformly random** entry of the scenario's own `spawn_points` list.
`num_wingmen` is clamped to 5 by the parser, and ⚠ **forced to 0 when `mission_type` is 0**, along
with all four wave counts: dogfighting an ace is a solo duel, in the data as well as in the UI.

Wingman `i` (0-based) is built as:

| i | def | roster name | `accentID` | `primary_target` | title (string id, key, text) |
|---|---|---|---|---|---|
| 0 | `w<plane>` | `w<plane>_ia0` | 12 | `player` | `0x32c9`, `MSG_JACK_NAME`, Jack |
| 1 | `<plane>` | `<plane>_ia1` | 14 | `player` | `0x32cd`, `MSG_TEX_NAME`, Tex |
| 2 | `w<plane>` | `w<plane>_ia2` | 15 | `<plane>_ia1` | `0x32cb`, `MSG_BUCK_NAME`, Buck |
| 3 | `<plane>` | `<plane>_ia3` | 13 | `player` | `0x32cc`, `MSG_BJOHN_NAME`, Big John |
| 4 | `w<plane>` | `w<plane>_ia4` | 16 | `<plane>_ia3` | `0x32e4`, `MSG_BETTY_NAME`, Betty |

⚠ **Slot 1's accent 14 is silent in the original too.** `voice.zrd` row 14 is the single pilot VO
id 5, for which the install ships neither a clip def nor a WAV, so Tex has no combat line to say
whatever the trigger ([combat-voice.md](combat-voice.md), "Where a pilot's voice comes from"). The
other four slots reach ids 2, 6, 4 and 7, all of which own clips. This is data, not a resolver gap.

The title is the block's `+0xc`, the name the target marker prints ([targeting.md](../org/targeting.md)):
each switch case of the wingman loop resolves its hardcoded string id through `FUN_0059ce40`
(`0x0045a502`, `0x0045a565`, `0x0045a5c8`, `0x0045a642`, `0x0045a6a2`) and `_strdup`s the text into
the block at `0x0045a7ae`, so a wingman's marker names its pilot, as the ace's (`ace_name`) and a
wave member's (`enemy_name`) do.

`<plane>` is `wingman_plane`'s index through the two name columns above; the roster name format is
`%s_ia%d` over the def and the index. **So the flight is not five aircraft on the player: 0, 1 and
3 escort the player, while 2 and 4 escort 1 and 3.** That is `primary_target` doing the work M4 B7
said it does, and it is the shipped Instant Action wingman mechanism. ⚠ **a
patrol net IS assigned** (see above), and the net demotes the `w<plane>` def's `wingman` mode to
`jet`, so on this path the chain is a target assignment rather than a flown formation. **The
nine-value skill vector is left unset**, so wingmen fly on the airframe's own AI defaults.

Every wingman is **team 1, group 0, not deactivated**, and is placed `100 · ((i >> 1) + 1)` metres
from the player's spawn at `±45°` off its heading, the sign being `+` when `i & 3` is 1 or 2 and
`−` otherwise, at the player's altitude. The same 100 m / 45° fan the wave sequencer uses.

Its livery is the **`fortune`** pattern with three decals from record dwords 6 to 8 and three RGB
triples unpacked from packed dwords 9 to 11. The `ia.zrd` parser never writes those six; the
screen overlay does, from `0x0064ac50`…`0x0064ac64`, which is offset `0x5c` onward inside the
screen struct's own **player** plane record at `0x0064abf4`. So the wingmen wear the player's
livery, which is what `fortune` being the player's militia pattern already suggested.

### The ace and the waves

The ace is spawned only for `mission_type` 0, on **team 2, group 1**, at a random spawn point drawn
as `rand() % (count − 1)` with the last index substituted if it collides with the player's, wearing
its authored `ace_pattern`/`ace_colorN`/`ace_decalN` and carrying `ace_stats` in the nine-value
skill vector and `ace_accentID` as its voice.

Wave `N` (1-based) puts `num_enemies` aircraft on **team 2, group N**, in the plain `<plane>` def,
with `primary_target` `player`. Wave 1 spawns live at a spawn point picked the same way and sets the
current-group counter `DAT_00718cd0` to 1; waves 2 to 4 are built **deactivated at the world
origin**, which is the inert state `FUN_0045b9d0` later teleports and reactivates.

⚠ **On `zeppelin_run` (id 2) even wave 1 is built deactivated at the origin**, and the wave block
runs whether or not the scenario has spawn points, whereas every other mission type needs a
non-empty list to build waves at all. So on that mode no enemy is airborne at mission start, and
every one of them waits on the zeppelin's own generator to launch it: the sequencer's teleport does
not run on that mode at all (below).

Each wave member takes its wave's militia livery (pattern, three decals,
nine colour components, set by the setup screen rather than by the file) and the wave's
`enemy_accentID`, ⚠ except that an `accentID` of exactly **12** is re-rolled as `12 + rand() % 5`,
the wingman accent range.

**A shipped wave's militia is not recoverable from the file.** Unlike the wingmen (always Fortune
Hunter), a wave's militia varies per chapter, and `enemy_name`'s `MSG_*` key is not a reliable
militia abbreviation (`MSG_VEH_<ABBREV>_<PLANE>` in five of the eight chapters,
`MSG_OBJ_*`/`MSG_DH_*` mission names in the other three). Such a wave member spawns with its own
shipped textures, the same `shippedSkins` flag that keeps it out of the player militia's Fortune
Hunter colours, which an enemy wearing would read as friendly.

⚠ **A militia never selects a vehicle def on this path. It selects paint.** `FUN_0045a390`, the
Instant Action build, takes each spawn's aircraft as an INDEX into an eleven-row plane table at
`0x00620c70` (seven pointers per row: display name, gamez node, `p*` player def, `r*` remote def,
`w*` wingman def, and the plain AI def twice). Wingmen read the `w*` slot through `FUN_00426d60`;
the ace and every wave member read the plain AI def through `FUN_00426d20` (`0x0045aa53`,
`0x0045ada8`), look it up by name in the vehicle-def list and spawn it through `FUN_0047c210`. No
militia def name appears anywhere in that path: an Instant Action Black Hat Warhawk is the plain
`warhawk` def, and the militia's own defs (`bhatwarhawk` and the rest) are flown only by the
campaign's mission rosters, which name them outright.

So the militia decides the livery and nothing else. `FUN_0045a390` fills the spawn's override record
with the wave's pattern, three decals and nine colour components from the setup-screen record at
`0x00718dcc`, which `FUN_0047c210` then resolves per field against the def
([`../org/paint.md`](../org/paint.md)). That is why a Sacred Trust Warhawk exists at all: it is
selectable in the original and wears the Sacred Trust colours (user, at the controls) even though
`vehicle.json` ships no def for one, because the paint never came from a def.

The pattern is read off whichever def of that militia names one, so it agrees with the def's own
paint wherever both exist. Two militias name none: Fortune Hunter (the player militia flies the
`p*` family, and the livery picker's own default covers it) and Broadway Bomber.

⚠ **A `_N`-suffixed def is a second flight, not a duplicate.** `bhatbrigand`, `bhatbrigand_2` and
`bhatbrigand_5` all carry the title "Black Hat Brigand", and they are different aeroplanes: the
pilot vectors differ (6 against 4 against 7 on most slots) and so does the armament
(`bhatbrigand` carries `wep_05` where `_2` carries `wep_06`; `blakepeace` a `wep_130` gun where
`_3` carries `wep_140`; `bsfury` a `wep_09` where `_5` carries `wep_06`). Each has its own
instances in the mission rosters, so the suffix is not an instance index either. This matters to the
campaign, which names these defs; Instant Action reaches none of them.

⚠ **Broadway Bomber is the one pair that stays unpainted.** Its `BROADWAY` folder ships six masks,
all `PEA_*`, so the aircraft is paintable, but no vehicle def anywhere authors `paint_pattern`
`broadway`, so the install carries the masks and no colours to put in them. That member keeps its
shipped skins until the colours are read off the original the way `player_fortune`'s were
(`formats/paint.md`).

⚠ **A wave enemy's nine pilot stats are drawn at random from a table of five, not from its skill.**
`FUN_0045a280(row, k)` reads `0x00607a3c + row·36 + k·4`, and the caller picks `row = rand() % 5`
per aircraft. The five rows, on the same 1-to-9 scale as `ace_stats`:

| row | values |
|---|---|
| 0 | 5, 7, 5, 3, 2, 1, 4, 4, 4 |
| 1 | 4, 3, 4, 5, 7, 5, 3, 5, 5 |
| 2 | 3, 4, 3, 7, 3, 6, 4, 5, 4 |
| 3 | 7, 5, 6, 3, 3, 2, 4, 4, 4 |
| 4 | 4, 4, 4, 4, 4, 4, 4, 4, 4 |

They land in the same nine record fields `ace_stats` does (`+0x7c`, `+0x80`, `+0x84`, `+0x88`,
`+0x8c`, `+0x90`, `+0x94`, `+0x9c`, `+0xa0` on the roster block; the float at `+0x98` sits inside
that run and is not one of them). Four hand-authored pilot personalities plus one flat average,
rolled per aircraft.

### What `novice` / `veteran` / `ace` becomes

Not a pilot stat. The string parses to **0 / 1 / 2** (`_stricmp` in `FUN_00459390`; the wave skill
lives in the wave's own record at dword 23 and is set by the setup screen), and the only thing
either the ace's or a wave's skill does is **stand in as the global difficulty setting for the
duration of that one spawn**:

```
saved = difficulty()          # FUN_00440710
difficulty(skill)             # 0 -> 0, 2 -> 2, anything else -> 1
FUN_0047c210(block)           # the spawn reads it back
difficulty(saved)
```

`FUN_0047c210` is where that lands, and it does two things with the setting. A hostile vehicle has
its armour and health maxima multiplied by **0.75 / 1.0 / 1.25** on difficulty 0 / 1 / 2
([`org/vehicleDamage.md`](../org/vehicleDamage.md)), and the same tier adds **-2 / 0 / +2** to each
of the pilot's nine skill ratings before they interpolate, clamped to `[0, 9]`
([`org/aiControlLaw.md`](../org/aiControlLaw.md#the-rating-the-interpolation-receives-is-not-the-authored-one)).
So the skill names reach a rating after all, as an offset on whatever the block and the def author,
never as a rating in their own right: nothing on this path converts `novice`/`veteran`/`ace` into a
1-to-9 value. ⚠ **The exemption the campaign's aces get does not apply here.** That is the roster's
slot 67, and the entity field it lands in is written only by the roster spawn and by the entity
constructor's own zero, so an Instant Action ace takes the offset like any other spawn. With
`ace_stats` a uniform 9 the offset is invisible at the top tier (the clamp holds it at 9) and costs
two points at `novice`.

## Wave sequencer and mission end

One function, ticked every frame, does two jobs. It advances the wave counter when the current wave
is gone, and it decides whether the mission is over. M4 B7 traced its main path
(the retired `analysis/m4-b7-group-slot/`,
`git show analysis-archive:analysis/m4-b7-group-slot/FINDINGS.md`); the full read establishes
settled the three questions that pass left open.

Two globals drive it. `DAT_00718cd8` is the setup record's first dword, the **`mission_type` id**,
and `DAT_00718cd0` is the **current-group counter**. Both arms below are reached only after the
counter has already been incremented.

### Who still counts as an enemy

Walking the global vehicle list at `DAT_0071dabc`, a vehicle is an enemy when its team (`+0x08`) is
**greater than 1**, and it still counts if any one of three bytes says so:

| byte | meaning | how it was read |
|---|---|---|
| `+0x945` ≠ 0 | `deactivated` | `FUN_004b0f61` sets it to 1, and `FUN_00452450`, the generator launch, reads it to find a parked airframe |
| `+0x91d` = 0 | not destroyed | cleared together with `+0x91e`/`+0x91f` when `FUN_0047fd50` moves the player into a new airframe |
| `+0x91f` ≠ 0 | death sequence still running | same site |

So a wave still parked at the world origin **counts as present**, which is what stops the mission
ending the moment wave 1 dies.

### Mission type 0 has no sequencer at all

Dogfighting an ace takes an early branch of its own: it returns while any enemy is alive or still
exploding, and otherwise goes straight to the end. The counter is never incremented and `+0x945` is
never consulted, which agrees with the parser forcing every wave count to 0 on that mode.

### Advancing a wave

On every other mode the walk clears a "no enemies left" flag for each enemy that still counts, and
**bails out at the first one whose group (`+0x388`) equals the current counter**: that wave is still
alive, so nothing advances and the tick falls through to the end checks. If the walk finishes having
seen no enemy at all, the same thing happens. Only when live enemies exist and none of them is in
the current group does `DAT_00718cd0` increment, and then **exactly one of two arms runs**.

⚠ **The two arms are exclusive: on `zeppelin_run` the generator feed replaces the teleport, it does
not supplement it.** The test is `CMP EBX,0x2` / `JNZ` at `0x0045ba9b` on the mission-type id, and
the type-2 arm ends `JMP 0x0045bd83`, past the whole teleport block. So on that one mode the
sequencer never picks a spawn point, never moves an aircraft, and never calls `FUN_004b0f40(0)`.
Every enemy on a zeppelin run reaches the air out of the zeppelin's bay or not at all.

### The teleport arm: mission types 1, 3 and 4

The spawn point is drawn in two steps. First every entry of the current mission type's spawn list is
tested against the **local player's** position, via vtable slot 0 of `DAT_0071c298`, and those at
squared distance ≥ `250000.0` (the float at `0x006036c0`, so **500 m**) are collected into a vector
of indices. Then one of those indices is taken as `rand() % n`.

⚠ **If no spawn point is 500 m away the index falls back to a literal 0**, the first entry of the
list, not a random one. With Instant Action's 4-to-8 entry lists that is reachable, and it is the
one case where two consecutive waves are guaranteed to arrive in the same place.

Every member of the new group is then reactivated with `FUN_004b0f40(0)` and written to that point:
position at `+0x204`/`+0x208`/`+0x20c`, attitude at `+0x1f8`/`+0x1fc`/`+0x200` with pitch and roll
zeroed and the yaw taken from the spawn entry's own heading. The members fan out in the **same 100 m
/ 45° pattern the wingmen use**: the first member sits exactly on the point, and member `k` after it
(`k` from 0) is placed `100 · ((k >> 1) + 1)` metres away at `±π/4` off the spawn heading, the sign
being `+` when `k & 3` is 1 or 2 (constants `100.0` at `0x00607af0` and `0.7853982` at
`0x00607af4`). For six aircraft that is 0, 100, 100, 200, 200 and 300 m on sides −, +, +, −, −. The
teleport writes position and orientation only; the three writes that follow (the `+0x6a4`…`+0x6a8`
list collapsed onto the new position, and the vec3 at `+0x6b0` zeroed from `DAT_0075d1b8`) are the
same trio the player's own airframe swap performs, so they read as a teleport artefact reset rather
than anything mission-specific.

### The generator arm: mission type 2

The wave is not moved. Instead the sequencer takes the **selected zeppelin**,
`(&DAT_00718fd0)[DAT_00718f2c]`, finds the generator hosted on that zeppelin's node, and

- adds the **new group's member count** to the generator's `capacityRemaining` (`+0x80`), and
- stamps the generator's group (`+0x64`) with the new counter value.

That is the whole arm. The generator's own launch path (`FUN_00452450`) then finds the parked
airframes whose group matches, reactivates them and drops them from the bay, one wave's worth of
capacity at a time. It is what makes `capacity 0` workable in Instant Action; see
[mission-entities.md](mission-entities.md)'s capacity section.

`FUN_00451780` is the lookup, over the generator manager at `0x00654170`, matching the generator's
host node (`+0x08`) against the zeppelin's node (`+0x1c`). It is a single-node match, not a subtree
walk (`FUN_004532a0` is the walking variant, used elsewhere).

⚠ **The counter advances whether or not the top-up lands.** Both writes sit behind "a generator was
found", but `DAT_00718cd0` was already incremented and the tick jumps to the end checks either way.
A `zeppelin_run` scenario whose selected zeppelin carries no `egen` generator therefore burns
through all four waves with nothing ever released.

### Which zeppelin, and which spawn list

`DAT_00718f2c` is setup record `+0x254`, written from **`zeppelin_type`** through `FUN_00458f60`:
`cargo` → 0, `passenger` → 1, `military` → 2, anything else → 3, and 3 is rejected rather than
stored, over a record the reset left at 0, so an unauthored or misspelled `zeppelin_type` is
**cargo**, not an error. All 8 chapters author `cargo`, so slot 0 is the one in play throughout this
install.
`DAT_00718fd0[0..2]` are the three world nodes the mission builder resolved from the
`cargo_zeppelin` / `passenger_zeppelin` / `military_zeppelin` values (the parser builds those keys by
appending the literal `_zeppelin` at `0x00625688` to each type name), all `multiplayer1zep` here.

⚠ **The builder deactivates all three zeppelins, and on `zeppelin_run` it ACTIVATES the selected
one.** After the wave loop, `FUN_0045a390` walks the three resolved nodes (`0x0045b8a2`) and
switches each off, skipping only the `zeppelin_type` pick on `mission_type == 2`. Then, immediately
after that loop, a **second block** (`0x0045b910`) runs for exactly that skipped node. The two
blocks call the same three functions with inverted arguments:

| | the loop's deactivation (`0x0045b8d6`) | the type-2 objective (`0x0045b928`) |
|---|---|---|
| `FUN_004bd780(zep, b)`, record byte `+0x6`, plus `+0x8 = now + 3.0` | `1` | `0` |
| `FUN_004cca30(zep->node, b)`, **`gwNodeSetActive`** (the string at `0x0062cd28` names it; the flag is bit 2 of the node's `+0x24`) | `0` | **`1`** |
| `FUN_004bf060(zep, b)` → `FUN_004bef70(zep->node, b)`, **the turret arm**: writes `ACTIVATED` on every turret standing anywhere in that node's subtree (below) | `0` | `1` |
| `FUN_0045a2a0(zep->node)`, recursive teardown of the vehicle/AI objects under that node | called | **not called** |

The objective then also gets byte `+0x4d` set on the object `FUN_004a3360` finds by its name, the
same "this is the mission's target" byte the stunt zones and the ground target get in this
function, and a `FUN_004edc50(…, 0, 0, 0)` motion reset on a third per-type slot
(`0x00718fc4 + type·4`) which the record reset zeroes (`param_1[0xbb..0xbd] = 0`) and nothing on the
`ia.json` path writes, so that last call does not fire in a file-driven launch.

**That answers how the builder composes with the mission script's own deactivation list.** The two
are independent and the builder wins, because it is a real activation rather than an omission:
`support\c1\ia1.gw` switches `multiplayer1zep` off at world load ([interp.md](interp.md)), and on a
zeppelin run Instant Action switches it back on. Note also byte `+0x6`, which the end-condition
table below reads as "the zeppelin is gone": the loop **sets** it on every deactivated zeppelin and
the objective block **clears** it, so the mode-2 end condition starts false only for the objective.

⚠ Nothing here is a wake-up of the record's own `deactivated` flag, and **the sequencer contains no
zeppelin wake-up either** (it touches the generator's counters and nothing else), which corrects the
standing note in [mission-entities.md](mission-entities.md) and `Session/AiGeneratorRuntime`.

### The turret arm is what arms the Instant Action zeppelin

⚠ **`FUN_004bf060` is a turret activation, and it is the only thing that puts working guns on the
zeppelin you attack.** `FUN_004bef70(node, b)` stores `b` in `DAT_0071df90` and recurses over the
node's children (`FUN_004bef20`): each visited node is looked up in the turret list at
`DAT_0071d910` (`FUN_004a9890`, matching the turret's own `+0xc` node pointer) and, on a hit, the
flag is written to **turret byte `+0x6e`**, the `ACTIVATED` slot the entry loader fills at
`0x004aa7bb` and the turret tick reads as its awake gate at `0x004aac16`
([turrets.md](turrets.md#being-alive-and-being-awake)).

That matters because **all four `multiplayer1zep` / `multiplayer2zep` entries in `ai.zrd` ship
`ACTIVATED 0`**, and Instant Action runs no objectives script (every chapter's `IA1/objectives.zrd`
is a `MISSION_TIMER` + `PLAYER_INIT` and three null anim lists), so no `WAKEUP_TURRETS` ever fires
there. Without this call the objective zeppelin would fly with fourteen dead gun rings. The
deactivation loop's `b = 0` is the same call stowing the other zeppelins' rings.

The objectives script reaches the same primitive from its own list: `FUN_00469af0`'s `+0x29c`
entries resolve a node by name and call `FUN_004bef70(node, 1)` (the subtree form,
`WAKEUP_ZEP_TURRETS`), beside the `+0x270` list's `FUN_004a97b0(turret)` per-turret form
(`WAKEUP_TURRETS`), which is why the two script ops exist separately.

The spawn list is `ia.json`'s own **`spawn_points`**, not a separate stored list. `FUN_0045a150`
walks that dict, maps each scenario key to a mission-type id through `FUN_00458c20`, and appends the
entries to the vector at `0x00718fe0 + id·0x14 + 8`; `FUN_00459ef0` writes them as 16-byte
`[x, y, z, heading]` records with the heading converted to radians (`× 0.017453292`). The sequencer
reads the same entry's `+8`/`+0xc` for the same mission type, so a wave can only ever land on a
spawn point of the scenario it is running. The table at `0x00718fe0` is five 20-byte entries indexed
by mission-type id, byte 0 being the availability flag `disallow_missions` clears.

`zeppelin_run`'s four entries, the one scenario with fewer than eight, are therefore never read by
the sequencer at all: that mode takes the other arm.

### The end conditions

Whatever the arm, the tick finishes by deciding whether the mission is over, and ends it with
`+0xc58 = 1` and a **3-second** timer on the object at `0x0071b480`. That timer is a live world, not
a pause: what it holds, and what follows it, is
[instant-action/wrap-up.md](instant-action/wrap-up.md), "The hold after the ending". The per-mode
predicates:

| mode | over when |
|---|---|
| 0 ace | every team-2 vehicle is destroyed and finished exploding |
| 1 squadron | the same walk found no enemy that still counts |
| 3 ground target | the object at `DAT_00718f50` reports through vtable slot `+0x14`, or there is no such object |
| 4 stunt | every entry of the danger-zone list at `DAT_00718f78`…`DAT_00718f7c` reports done (`+0x48` clear or `+0x40` set), clearing byte `+0x4d` of the parallel list at `DAT_00718f88` as it goes |
| 2 zeppelin | the selected zeppelin is gone, or its **live engine** list at `+0x4c`…`+0x50` is empty, or its hull-death byte `+0x6` is set |

The mode 2 row is also where the type-2 arm lands when the zeppelin pointer is null. The field
meanings in rows 3 and 4 are read from their use here only, not from their own modules, so treat
them as pointers rather than as a decoded end-condition model. Row 2 is decoded in full below.

#### The zeppelin run is won on the ENGINES

The two zeppelin fields resolve against the record reader (`FUN_004bd8d0`) and the zeppelin's own
per-frame update (`FUN_004bf9d0`), and the empty-list test is the mode's actual objective, not a
degenerate guard:

- **`+0x4c`…`+0x50` is the `engines` list, and it is the LIVE one.** The reader fills it from the
  record's `engines` key, one entry per nacelle's own `healthy` child node (`0x004bea8a`), and
  keeps the load-time count separately at `+0x98`. `FUN_004bf150`, run every frame while the
  zeppelin is alive and active, **erases** from the list every entry whose node has lost its active
  bit (`node[+0x24] & 4`), then scales the live limits from what is left:
  `+0xa0 = sqrt(alive/total) · max_speed` and `+0xac = (0.8·sqrt(alive/total) + 0.2) · max_accel`,
  the same curve [mission-entities.md](mission-entities.md) documents under "Engine loss". So the
  list running empty means **every engine has been destroyed**.
- **`+0x6` is the hull-death byte**, set by `FUN_004bf0b0` when the surviving `healthy` entries
  (`+0x38`…`+0x3c`) drop below `num_healthy_required` (`+0x44`). That is F18's gasbag threshold,
  unchanged.

So the mode has **two winning paths, tested in that order**: destroy all the engines, or kill the
hull on the gasbag threshold. The shipped text names the first and only the first,
`MSG_BRF_IAZ_OBJ2` is *"Destroy the zeppelin's engines to win!"*, `MSG_BRF_IAZ_OBJ1` is *"Cripple
the zep so your raiding parties can hit it"*, and all eight chapters' `IA1/zrdr/targets.zrd` give
the `multiplayer1zep` target `help_label` `MSG_OBJ_DISABLEENG` ("Disable Engines"). Neither the
mission's `objectives.zrd` (which carries only `MISSION_TIMER` and `PLAYER_INIT`) nor any zeppelin
record authors a threshold: the count is "all of them", because the test is on an empty vector.

⚠ **A record authoring no engines wins the mission on the first tick.** The list is empty from
load, and nothing distinguishes that from having emptied it. Unobservable in the shipped data (all
58 records author 12 or 14 engines) but it is the behaviour, not an accident.

⚠ **This was decoded, after G13 had already shipped the hull kill as the only path.**
The tell was in play: every engine on the objective zeppelin destroyed and the mission ran on.
The reason the error survived review is that the gasbags are behind the `DAMAGES_ZEPPELIN` gate
([weapons.md](weapons.md)) while engines are ordinary destructibles, so the hull-only reading made
the mode unwinnable for any pilot who had not fitted `wep_14` torpedoes.

### Keys parsed but never authored, and authored but never parsed

⚠ **`enemy_skill` is authored in all 8 chapters and read by nothing.** `FUN_00458e00`, the wave
parser, reads exactly `num_enemies`, `enemy_name`, `enemy_plane` and `enemy_accentID`; no
`enemy_skill` string exists anywhere in the executable. A file-launched wave therefore takes the
built-in skill default of 1 (`veteran`) whatever the file says. Same class of finding as
[turrets.md](turrets.md)'s unread `HEALTH`.

The reverse also holds. `wingman_plane`, `enemy_accentID`, `ground_target_name` and
`ground_target_node` are parsed but authored by no chapter. In retail none of the first two shows,
because the setup screen is written over the same record before the mission is built ("The setup
screen is written over the file, every time"): `ia_d_planew` is the wingman aircraft, and
`ia_d_egroupN` the wave's militia, which carries the livery **and the wave's `enemy_accentID`**.
It shows on any path that skips the screen: then the wingmen fly the **Devastator**, every wave
enemy has `accentID` -1 and skill `veteran`, and the wave livery is whatever the record last held.

## Wrap-up and scoring

See [Instant Action wrap-up](instant-action/wrap-up.md) for the wrap-up UI, scoring, and friendly-fire rules.

## Known UI limit

**"Total Kills" is defined but unwired** in the shipped UI. Do not add a fifth wrap-up row for it.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
