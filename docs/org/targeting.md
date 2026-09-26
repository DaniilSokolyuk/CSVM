# Player target selection and the target marker, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-08-16. Every claim
below names the function or address it came from. The HUD geometry is additionally measured off
`OriginalScreenshots/Targeting HUD Kestrel.png` and `OriginalScreenshots/C1 M04 Zeppelin.png`, and
the measurements agree with the constants to the pixel.

Everything here is a description of *behaviour*. No decompiler output is reproduced; the addresses
are given so any claim can be re-checked at source.

⚠ **This page is a decode, not a proposal.** Where it disagrees with a screenshot reading or with a
remembered behaviour, the decode wins and the disagreement is a note.

**Where the neighbours live.** The gun aim assist, which walks the *same four entity lists* with a
different scorer, is [`aim-assist.md`](aim-assist.md). The AI's own standing target, which lives in
the same plane field but is chosen by entirely different rules, is [`aiPilot.md`](aiPilot.md). The
HUD elements this page does not cover (compass, gauges, pipper) are
[`../formats/hud.md`](../formats/hud.md). The off-screen marker's arrow geometry and the round live
picture Shift+S puts beside it, one object driven by one function, are
[`spyglass.md`](spyglass.md).

## The headline

The player's target is a **sticky, explicitly chosen** entry in a **per-frame rebuilt, sorted
candidate list**. Eleven input actions drive it: three classes (Enemy/Objective, Ally,
Non-Aircraft) times next/previous/nearest, plus "nearest crosshairs" and "target nothing". The
class is a global mode, not a per-press filter, and it persists until another class's key is
pressed.

Two findings will not be guessed correctly from the screenshots:

- **"Nearest" does not mean nearest.** It means *first in the cycle order*, and the cycle order is
  a four-sector angular sort (ahead, behind, left, right) with distance as the tie-break inside a
  sector, and with mission objectives promoted ahead of everything.
- **The bracket range gate is not a HUD constant.** The brackets draw exactly when the *selected
  gun group could reach the target's intercept point inside its authored `RANGE`*. It is the aim
  assist's own reachability test, re-run for the HUD.

## Function map

| Address | Role |
|---|---|
| `FUN_004895a0` | Input-action registration. Binds action ids `0x24`–`0x2e` to the eleven targeting handlers |
| `LAB_00488680` … `LAB_00488c20` | The nine class handlers, action ids `0x24`–`0x2c` (see the table below) |
| `FUN_00488db0` | Action `0x2d`, **Select Target Nearest Crosshairs** |
| `LAB_00488ca0` | Action `0x2e`, **Target Nothing** |
| `FUN_00488ce0` | The nearest-crosshairs score: 15° nose cone gate, then slant range |
| `FUN_004b5fb0` | **The per-frame targeting update.** Rebuilds the candidate list, sorts it, re-resolves the current selection, publishes the target pose |
| `FUN_004b5cd0` | Per-candidate class filter and list insert |
| `FUN_004bb9b0` / `FUN_004bc020` | The sort (introsort) over the candidate list |
| `FUN_004bbd60` | **The comparator.** This is the cycle order |
| `FUN_004b6490` | Step the cycle: find the current entry, move `±1` with wrap, return it |
| `FUN_004b6480` | The cycle's acceptance predicate. Returns constant `1`; the list is pre-filtered |
| `FUN_004a64e0` | The death/despawn hook. Drops a dead entity from every target field and both lists |
| `0x004a36e0` / `0x004a36f0` | A mission structure's position and velocity getters, vtable `0x00608848` slots 0 and 1: `LEA EAX, [ECX + 0x74]` / `[ECX + 0x80]` |
| `FUN_004a2570` | The structure constructor. Stores the scene node at `+0xc` and fills `+0x74` from `FUN_004cf2c0` |
| `FUN_004a2730` | The per-frame refresh of `+0x74`/`+0x80`, gated on the `+0x8f` moving flag |
| `FUN_004cf2c0` | Node → point: the active bbox (`FUN_004cd960`, six floats at `node + 0x70`), its midpoint (`FUN_004d8b10`), in the frame `FUN_004cef20` builds |
| `0x004ac720` | **The turret's cost function**, slot 0 of the query vtable `0x00608b68`. Range gate, then `1200 * weight + distance` |
| `FUN_004b9770` | The take-a-hit path. Appends the shooter to the attacker queue |
| `FUN_004a5f40` | `Target::GetColor`, the marker colour rule |
| `FUN_004574d0` | **Draws the bracket box** (six line sprites) and computes the label anchor |
| `FUN_004579e0` | **Draws the three label lines** (`targetHelp` / `targetTitle` / `targetPos` fonts) |
| `FUN_0049d940` | The off-screen case: the edge position and the `%d o'clock` bearing |
| `FUN_0049d8a0` | The clock hour itself (1–12) |
| `FUN_0049fe70` | The HUD frame that calls `FUN_004574d0` and `FUN_004579e0` |

Globals:

| Global | What it is |
|---|---|
| `DAT_0071c298` | The local player's plane (same global the aim assist uses) |
| plane `+0x948` | **The target.** A `Target*` wrapper. The AI uses the same field on its own plane |
| plane `+0x294` / `+0x298` | The target's published position and velocity, refreshed each frame |
| plane `+0x728` / `+0x72c` | The net-transmitted target kind and net id |
| `DAT_0071c478` … `DAT_0071c47b` | The four class-mode flags: Enemy, Ally, Non-Aircraft, Objective |
| `DAT_0071d378` (`+4` = `0071d37c`, `+8` = `0071d380`) | The candidate list, `vector<Target*>`, rebuilt every frame |
| `DAT_0071d388` (`+4` = `0071d38c`, `+8` = `0071d390`) | The **attacker queue**, appended to when the player is hit |
| `DAT_0071dabc` / `DAT_0071d914` / `DAT_0071d33c` / `DAT_0064f78c` | `VehicleList` / turrets / `MStructList` / live fused ordnance. The same four pools the aim assist scans |
| `DAT_0071dacb` | Set only by the `-too` command-line switch (`0x004a749e`). Zero in normal play |

The targeting state is reached once per frame through the flight state's update virtual
(vtable `0x00608310` slot 2 → `FUN_004a0a80` → `FUN_004a0220` → `FUN_004b5fb0`).

## The eleven actions

Action ids come from `FUN_004895a0`; the names come from `extracted/messages.json`, whose
`MSG_CMD_TARGET_*` ids run `10015`–`10023` in exactly the same order as ids `0x24`–`0x2c`.

| Id | Action | Class flags it sets | Step |
|---|---|---|---|
| `0x24` | Next Enemy/Objective | Enemy, if not already in that class | attacker queue first, else `+1` |
| `0x25` | Previous Enemy/Objective | Enemy, if not already (clears the target) | `−1` |
| `0x26` | Nearest Enemy/Objective | Enemy, always | head of list |
| `0x27` | Next Ally | Ally, if not already (clears the target) | `+1` |
| `0x28` | Previous Ally | Ally, if not already (clears the target) | `−1` |
| `0x29` | Nearest Ally | Ally, always | head of list |
| `0x2a` | Next Non-Aircraft | Non-Aircraft, if not already (clears the target) | `+1` |
| `0x2b` | Previous Non-Aircraft | Non-Aircraft, if not already (clears the target) | `−1` |
| `0x2c` | Nearest Non-Aircraft | Non-Aircraft, always | head of list |
| `0x2d` | Select Target Nearest Crosshairs | derived from what it picks | its own scan |
| `0x2e` | Target Nothing | all four cleared | clears the target |

The nine class handlers each play the `sg_switchtarget` sound at gain 1.0 (nine separate copies of
the string, `0x00628968`–`0x006289e8`). **`0x2d` and `0x2e` are silent**, and so is `0x24`'s
attacker-queue branch.

Note the asymmetry the code makes explicit: **"Nearest" always re-asserts its class and restarts
the cycle**, while "Next"/"Previous" only reset when the class actually changes. Pressing the same
class's Next repeatedly walks the list; pressing Nearest returns to its head.

### The controls the eleven ship on

`FUN_004936c0` authors the keybind screen's Targeting page, one
`FUN_00493630(command, messageId, keyboardA, keyboardB, joystick, mouse)` per row. A keyboard code
is `DIK | modifiers`, `0x200` for Ctrl and `0x400` for Shift, so one letter per class carries all
three of that class's directions and the modifier picks the direction:

| Id | Action | Keyboard | Joystick |
|---|---|---|---|
| `0x24` | Next Enemy/Objective | `0x012` E | 3 |
| `0x25` | Previous Enemy/Objective | `0x412` Shift+E | |
| `0x26` | Nearest Enemy/Objective | `0x212` Ctrl+E | |
| `0x27` | Next Ally | `0x011` W | |
| `0x28` | Previous Ally | `0x411` Shift+W | |
| `0x29` | Nearest Ally | `0x211` Ctrl+W | |
| `0x2a` | Next Non-Aircraft | `0x013` R | 6 |
| `0x2b` | Previous Non-Aircraft | `0x413` Shift+R | |
| `0x2c` | Nearest Non-Aircraft | `0x213` Ctrl+R | |
| `0x2d` | Select Target Nearest Crosshairs | `0x010` Q | |
| `0x2e` | Target Nothing | `0x014` T | |

The record shape, the four slots a row can hold and the Japanese-keyboard remap are in
[`input.md`](input.md), which carries the whole 64-row default table. Two facts about this page
matter to a port. The five letters are one unbroken run of the top row, `Q W E R T`, which is the
whole targeting scheme within reach of the hand that is not on the stick. And **only two of the
eleven carry a joystick button**, Next Enemy on 3 and Next Non-Aircraft on 6, so a pad player of
the original reaches one direction of two classes and nothing else.

⚠ **The candidate list lags the class flags by one frame.** A handler sets the flags and
immediately calls `FUN_004b6490` on the list that last frame's `FUN_004b5fb0` built under the
*previous* class. The next frame rebuilds under the new class, and because the old selection is no
longer in the list the re-resolve drops to the head of the new list. The visible result self-heals
in one frame, but a port that rebuilds the list synchronously inside the handler will not reproduce
that one-frame step exactly.

## The class model

Class is decided per candidate in `FUN_004b5cd0`, in this order:

1. The candidate's `Target` virtual at vtable `+0x14` (the aim assist's "dead / not yet live"
   predicate) rejects it outright.
2. Entity `+0x4d` set → **Objective** class. This overrides everything, including aircraft.
3. Entity `+0x4c` set → **Non-Aircraft** class. Also overrides the aircraft split.
4. Otherwise, the candidate must be a `TargetVehicle` or a `TargetProjectile`; a `TargetTurret` or
   `TargetStruct` with neither flag is **not selectable at all**. The player's own plane is
   excluded. Teams then split it: different, both non-zero → **Enemy**; same team, or either team
   is 0 → **Ally**. An own-team aircraft additionally has to have `+0x67c` in `{0, 4}`.

`+0x4c` and `+0x4d` are almost certainly the `aiv.zrd` `otherTarget` / `objectiveTarget` fields (the
format comment at `0x00622508` lists them adjacently, and the mission-script verbs
`ADD_OTHER_TARGET` / `ADD_OBJECTIVE_TARGET` / their `REMOVE_` partners exist at `0x00626704`). The
field-to-key binding is inference; the offsets and their effect are traced.

⚠ **"Non-Aircraft" names what the mission flagged, not what flies.** The class is decided by
`+0x4c` alone; step 4's `__RTDynamicCast` admits `TargetVehicle` and `TargetProjectile` and nothing
else, and `VehicleList` holds the AI ground and sea vehicles beside the aeroplanes (the turret
section below, and [`aim-assist.md`](aim-assist.md) "The four lists"). So an unflagged hostile boat,
ship or truck rides the **Enemy** cycle with the aeroplanes and is reached by Next Enemy, while a
turret or a mission structure is reachable at all only because a mission flagged it. A port that
reads the class name as a shape test puts hulls on the wrong cycle and leaves the Enemy cycle unable
to reach half of what is shooting at the player.

### The curated list is `targets.zrd`, and it is small

The two bytes are stamped by `FUN_004a2e00` from one file per mission, and `FUN_004a3a60` reads
`other_target` and `objective` as valueless keys on a record that also carries the three label
strings and a `nodes` list. So the "curated list" the Non-Aircraft cycle walks is that file's
`other_target` entries and nothing else, which is why an unflagged destructible standing beside a
flagged one is unreachable.

**The census over the install's 53 shipped `targets.zrd` files is 52 `other_target` entries**, and
no entry anywhere carries both flags, so the two cycles never contend for one record. Most of the
52 are one of three repeated records: `piratezep/rock_zeppelin` (Klondike, in twelve missions),
`zep_rearm_node_1`/`_2` (the multiplayer rearm bases, in eight `MP3` files each) and
`ap_transmitter` (the radio tower, in six `IA1` files). The rest are per-mission: C3/M02 flags
eleven of the work
camp's own buildings (`g_tower1`-`3`, `unit01`-`07`, `dock2`), C1B/M03 flags a tanker, a second
airship and a decoy lighthouse, C1/M02 a cargo train, C2/M05 a cargo zeppelin and C3/M04 a
shipwreck. ⚠ The count is what a mission AUTHORS, not what it shows: a record whose node the world
does not build offers nothing. Of the six `IA1` tables that flag `ap_transmitter`, only C1's world
carries a node of that name, so the radio tower is a live site in C1's Instant Action and in no
other chapter's; both `MP3` rearm bases resolve in all eight.

**Every mode reads its own file, and only the campaign edits it.** Which table a session gets is the
chapter and mission folder it launches with (`C1/IA1`, `C4/MP3`), so the same mode differs per map.
The campaign reads it under a director whose script can add and remove either half. Instant Action
and the multiplayer modes read it with no director at all: their `objectives.zrd` carries the
mission preamble (`MISSION_TIMER`, `PLAYER_INIT`, `RESTORE_ANIMS`) and not one target directive, so
the file's own flags are the whole curated list for the whole session.

**The script keys ship, and none of them sits on an objective that authors a point.** The census
over the install's 53 `objectives.zrd` files finds 43 other-target clauses (16 `ADD_OTHER_TARGET`,
27 `REMOVE_OTHER_TARGET`) in twelve missions: C1/M02, C1/M04, C1B/M03, C1C/M01, C2/M02, C2/M03,
C2/M05, C2B/M04, C3/M02, C4/M03, C4/M04 and C5/M04. Klondike (`piratezep/rock_zeppelin`) is the
repeated one, added or removed in eight of the twelve. Not one of those 43 objectives carries a
`TRAVELERS` clause at all, so no shipped other-target site has an objective point to stand on and
the two flags' keys reach the same point rule only as a guard. Nine objectives in the install author
a `TRAVELERS` point, and two of them name a target: C3/M01's `OBJECTIVE5` (the village trucks) and
its `OBJECTIVE9` (`grasshut2`), both on the `objective` half.

#### The campaign's Non-Aircraft list, mission by mission

Read through the same loader the flight uses, the list each campaign mission STARTS with is below.
Thirteen missions start with exactly one entry, nine start empty, CM08 starts with four and CM03
with twelve. CM06 is the one mission that ships no `targets.zrd` of its own and takes C1C's chapter
table, which flags nothing, so its Non-Aircraft key reaches nothing at all until its script adds
three airships. Not one mission's list holds a gun emplacement, in the install or in CSVM.

| Mission | Starts with | The script then |
|---|---|---|
| CM01 C3/M01 | `piratezep/rock_zeppelin` | nothing |
| CM02 C3/M05 | `piratezep/rock_zeppelin` | nothing |
| CM03 C3/M02 | `g_tower1`-`3`, `unit01`-`07`, `dock2`, `piratezep/rock_zeppelin` | removes the eleven camp structures as their objectives complete |
| CM04 C3/M03 | `piratezep/rock_zeppelin` | nothing |
| CM05 C3/M04 | `shipwreck` | nothing |
| CM06 C1C/M01 | nothing (C1C's chapter table) | adds `piratezep/rock_zeppelin`, `blackswanzep`, `workersvoyagezep` |
| CM07 C1/M02 | `trcargo01` | adds `ftank01` and two fuel trucks, then removes all four |
| CM08 C1B/M03 | `tanker`, `piratezep/rock_zeppelin`, `vostokzep`, `slhouse` | adds `lifeboat`, then removes the lifeboat, Klondike, `vostokzep` and the tanker |
| CM09 C1/M04 | `piratezep/rock_zeppelin` | adds and later removes Klondike |
| CM10 C1/M05 | `piratezep/rock_zeppelin` | nothing |
| CM11 C2/M02 | nothing | adds then removes `kidnapcar` |
| CM12 C2/M01 | nothing | nothing |
| CM13 C2/M03 | nothing | adds `piratezep/rock_zeppelin` |
| CM14 C2B/M04 | nothing | adds `piratezep/rock_zeppelin` |
| CM15 C2/M05 | `cargozep2` | removes `cargozep2` |
| CM16 C4/M01 | `piratezep/rock_zeppelin` | nothing |
| CM17 C4/M02 | `piratezep/rock_zeppelin` | nothing |
| CM18 C4/M03 | `piratezep/rock_zeppelin` | adds and later removes Klondike |
| CM19 C4/M04 | `piratezep/rock_zeppelin` | adds and later removes Klondike |
| CM20 C4/M05 | nothing | nothing |
| CM21 C5/M01 | `piratezep/rock_zeppelin` | nothing |
| CM22 C5/M02 | nothing | nothing |
| CM23 C5/M03 | nothing | nothing |
| CM24 C5/M04 | nothing | adds `piratezep/rock_zeppelin` and `blackswanzep` |

⚠ **A table entry offers nothing where the world builds no node of that name.** CM14 is the worked
case: the chapter's script adds Klondike, and what stands in C2B/M04's world decides whether the key
resolves. The `target-mission-list` suite flies CM01, CM06 and CM07 over their real worlds and pins
the cycle to the flagged keys those worlds resolve.

**A roster block that flags itself is its own candidate, not a second one.** `objectiveTarget` is a
field ON the entity, so an aeroplane whose `aiv` block authors slot 37 is offered once, as the
aeroplane, with the flag set: the block's slot-20 name on line 2, its slots 38 and 39 as the two
halves of line 1, Objective sorting it ahead of every Enemy Target, and the aeroplane's own presence
deciding whether it is selectable at all, so it appears at its wake and leaves at its death. CSVM
stamps the three slots onto the spawned `FlightController` (`Session/Roster/AiFlightAssembler.cs`), which is
also what carries a marker to a block a bay launches; `Session/Objectives/ObjectiveSites.cs` collects world
sites only, and `CampaignDirector` owns the label from there on, so a completing objective's
`REMOVE_OBJECTIVE_TARGET` clears the stamp and its `SET_HELP_LABEL` rewrites the category over the
block's slot 39, resolved once at the write. That relabel is not decoration: CM02's three Balmorals
read Destroy, then Dock, then Dock Escort as the mission advances, and CM24's Miles goes from Follow
to Destroy under his LAUNCH name (`stihellhound_5_eg0`), which is the name the script writes and the
name the launch is booked under. ⚠ Marking such
a block by resolving its name to a world node instead produces two entries on one silhouette, the
raw block name where the pilot's name belongs, and a target selectable before the aeroplane exists
and after it is gone.

`DAT_0071c47b` (the Objective flag) is set as a *companion* to whichever of the other two the
player is in:

- Enemy class sets `Objective = (DAT_0071dacb == 0)`.
- Non-Aircraft class sets `Objective = DAT_0071dacb`.

`DAT_0071dacb` is written only by the `-too` command-line switch (string `0x006298c4`, parsed at
`0x004a749e`) and is zeroed with its siblings at `0x004b3773`. **In normal play it is 0**, so
objectives ride with the Enemy cycle and not with the Non-Aircraft cycle. That is why the action is
called "Next Enemy/**Objective**"; the switch exists to move them onto the ground cycle instead.

**A zeppelin gasbag is an `MStructList` entry**, not a special case: `TargetStruct` is the only
class binding `0x004227a0` for the vtable `+0x1c` virtual, which returns the object's `+0x65` flag
and is what the AI overlay prints `Gasbag targeted: %s` from (`aiPilot.md`). So a gasbag is
selectable when, and only when, its mission structure carries `otherTarget` or `objectiveTarget`.

⚠ **There is no sub-part enumeration anywhere in the targeting path.** Everything selectable is one
entry in one of the four global pools. Engines and cannons appear only if the mission authors them
as their own `MStruct` entries; the decode does not show the engine walking a zeppelin's parts.

### A Stunt Flying Danger Zone is the player's own target, not a HUD of its own

`FUN_004579e0` is the only function that formats `"%s [%s] -"` (`0x006253ac`), and it draws off the
local player's `+0x948` selection alone. So the marker on
`OriginalScreenshots/C1 IA1 Cloudcoverage 1.png` (an edge arrow beside `Danger Zone [Fly Through] -`
over `Train Tunnel Mid` over `1 o'clock`) is the ordinary target label with a Danger Zone in the
target slot. Its blue is `Target::GetColor`'s answer for a category that is not one of the four
destructive ones, which is what `MSG_OBJ_DZ` resolves to. The three lines are the `targets.zrd`
record's own `category_label`, `help_label` and `description`, the same triple every objective site
is labelled from.

⚠ **How a zone reaches the selection was not traced.** No `dzN` entry in any of the eight shipped
`IA1` `targets.zrd` files carries `objective` or `other_target`, and `FUN_004a2e00` stamps
`+0x4c`/`+0x4d` from those two keys alone, so the class filter `FUN_004b5cd0` would refuse the
object that record builds. Every writer of `+0x948` was read and none is stunt-specific: the AI's
own acquisition `FUN_0041fe10`, the eleven action handlers between `0x00488737` and `0x00488cd3`,
the save restores `FUN_00472770` and `FUN_0047fd50`, the death and teardown clears, and the
per-frame re-resolve `FUN_004b5fb0`. Something outside that sweep sets the flag. CSVM offers each
unflown zone to the flying pilot's own pool as an objective-flagged candidate, which reproduces the
marker, the colour and the cycle position; a zone the pilot has cleared is simply not offered again,
so the re-resolve drops to the head like any other departed target.

## The team space

The engine has **one** team space, one field, and one hostility predicate. Aircraft, turrets and
world objects all store the same integer in the same place, and nothing anywhere remaps it per
entity kind.

**The vocabulary is three one-line constructors**, and they are the only things that mint a team id:

| Address | Meaning | Body |
|---|---|---|
| `FUN_004a3f80` | neutral | `*out = 0` |
| `FUN_004830c0` | ally (the player's side) | `*out = 1` |
| `FUN_0045c260` | **enemy from index** | `*out = index + 2` |
| `FUN_00453740` | raw / identity | `*out = value` |

So the space is `0` neutral, `1` ally, and enemy team *index* `N` becomes id `N + 2`.

**The field is combat-object `+0x8`**, written by the virtual setter at vtable slot 2 (base
implementation `FUN_00441b80`, the write at `0x00441b86`). Every kind reaches it:

| Entity | Where its team is written | Value |
|---|---|---|
| Aircraft and generic combat objects | `FUN_004a2570`, 4th argument, write at `0x004a259f` | from the caller |
| Vehicles | `FUN_004aff80`, write at `0x004affba` | neutral by default (`FUN_004a3f80` at `0x004affa7`) |
| Turrets | `FUN_004a9a60`, write at `0x004a9a99` | `FUN_0045c260(_, 0)` = **2** |
| Instant Action enemy aircraft | `FUN_0045a390`: enemy index 0 pushed at `0x0045b2ba`, `FUN_0045c260` at `0x0045b2bc`, passed to `FUN_004a2570` at `0x0045b2db` | **2** |
| Authored mission aircraft | `aiv.zrd` tuple slot 3, read verbatim by `FUN_00437620` at `0x00437723` | the authored integer, unmodified |
| Zeppelins and their parts | `FUN_004bd8d0` parses the record, `FUN_004bf030` → `FUN_004bee80` fans one value onto `+0x8` of every component and child | see below |
| World / scene objects | `FUN_004a3360` builds them through the same `FUN_004a2570`; the team comes from `FUN_004a32f0` at `0x004a3493` | see below |

⚠ **A turret's own default is exactly the id an Instant Action enemy fighter carries.** Both are
`2`, minted by the same `FUN_0045c260` at enemy index 0. That is the whole reason the original's
zeppelin turrets do not shoot the fighters that zeppelin launched: the two ids compare equal and
the predicate rejects the candidate.

The mission-script verb `SET_AI_TEAM` (`FUN_00469e20`) confirms the same identity handling. It
takes the script's raw integer and hands it straight to the virtual setter on a vehicle, or to a
zeppelin's `+0xE0` (`0x00469efe`), with no conversion on the way.

### The hostility predicate

Virtual slot `+0x34` on the target wrapper hierarchy. The base vtable at `0x006035f4` has
`__purecall` there; four concrete overrides exist and all four share one team core.

> A candidate is hostile if and only if its `+0x8` differs from the shooter's team **and** neither
> team is `0`.

The canonical implementation is `FUN_004a5b90` (**`TargetStruct`**, vtable `0x0060364c`), which
first requires the candidate's per-class targetable flags at `+0x8c` and `+0x90`, then makes the
three comparisons at `0x004a5bb0` (equality), `0x004a5bb4` (candidate is 0) and `0x004a5bb8`
(shooter is 0). The siblings share the team core and differ only in which two liveness bytes they
read first, so each class carries its own pair and none reads another's:

| Override | Class, vtable | Liveness bytes | Team comparisons |
|---|---|---|---|
| `FUN_004a5b20` | `TargetVehicle`, `0x0060886c` | `+0x91d` (the dead byte) must be CLEAR, `+0x944` (awake) must be SET | `0x004a5b40`/`44`/`48` |
| `FUN_004a5bd0` | `TargetTurret`, `0x006088cc` | `+0x6c` and `+0x6d` both set | `0x004a5bea`/`ee`/`f2` |
| `FUN_004a5b90` | `TargetStruct`, `0x0060364c` | `+0x8c` and `+0x90` both set | `0x004a5bb0`/`b4`/`b8` |
| `FUN_004a5c10` | emplacement hosts | none, the team test alone through the object at `+0x8` | `0x004a5c1e`/`22`/`26` |

`FUN_004a5c10`'s codegen inverts polarity through `XOR EAX,EAX / SETZ DL` and resolves to the same
truth table.

Two properties matter for a port:

- **Neutral is symmetric and total.** A team-0 object is never anyone's target, and a team-0
  shooter never acquires one. It is excluded from both sides of the test.
- **There are no bands and no magic values.** No override contains a `>=` test, a range check, or a
  special case for any id but `0`. The comparison is raw integer against raw integer.

This is the same rule the class model above applies when it splits Enemy from Ally.

### The turret gunner runs the same predicate

The range-gated picker is `FUN_0041f9c0`, a minimise-cost-over-candidates loop (best seeded to
`1e+20`, rejects scored `1e+21`) walking the four global pools. Its query object is built by
`FUN_00422850`, which stores the shooter's position at `query+4`…`+0xc` and **the shooter's team,
copied from `*(shooter+8)`, at `query+0x10`**; the picker passes `query+0x10` to the predicate.
The turret gun update `FUN_004aabb0` calls `FUN_00422850` at `0x004aaf5c` with its own `*(this+8)`,
calls the picker at `0x004aaf9b`, and stores the result to `turret+0x210` at `0x004aafd7`.
Predicate call sites inside the picker are `0x0041fb9e` (devirtualised straight to `FUN_004a5b90`),
`0x0041fc68` and `0x004228a0`.

So a turret compares its own raw `+0x8` against a candidate's raw `+0x8`, with no remap on either
side, through the same predicate the player HUD's scan uses.

### What a turret's candidate set holds

`FUN_0041f9c0`'s four passes, in the order it takes them, all against one running best cost so a
later pool wins only by scoring lower (ties to the later pool):

| Pass | Pool | Admitted |
|---|---|---|
| 1 | `VehicleList` (`DAT_0071dabc`) | **every entry, unconditionally.** The list holds aircraft AND AI ground/sea vehicles (see [`aim-assist.md`](aim-assist.md) "The four lists"), so a boat or a ship is a turret candidate exactly as an aeroplane is. The cost carries a per-entry bias from `entity+0x340` |
| 2 | Turrets (`DAT_0071d914`) | every entry, unconditionally |
| 3 | `MStructList` (`DAT_0071d33c`…`0x0071d340`) | only when the picker's 4th argument is set, and then only entries whose `+0x8d` is non-zero and whose `+0x65` gasbag flag is clear (the 3rd argument would admit gasbags; the turret passes `0`) |
| 4 | Live fused ordnance (`DAT_0064f78c`) | entries whose `+0x6c` tracking byte is set. A winner here returns immediately |

Every candidate passes `FUN_00422890` first: the hostility virtual `+0x34` against the query's
team, and then, when the query's `+0x14` byte is set, a rejection of any candidate whose Y sits
below the shooter's. That byte comes from the turret's own `+0x1e8`, so a gun can be authored to
refuse anything beneath it.

**The picker's 4th argument, the one that switches the structure pool on, is
`DAT_00629c20 != 0 || turretTeam != playerTeam`** (`0x004aaf71`). `DAT_00629c20` is set to `1` at
every mission load (`0x00475784`) and cleared again only when the loaded mission is **C2/M05**
(`0x004757be`, after string compares against `"c2"` at `0x00627c20` and `"m05"` at `0x00627c24`).
So mission structures are in every turret's set in every other mission, and in C2/M05 only for
turrets that are not on the player's team.

`+0x8d` is written `1` at `0x004a2ec9`, in `FUN_004a2e00`'s first pass over the flagged
target-node list `DAT_0071d35c`…`0x0071d360`. `FUN_004a2be0` fills that list, after parsing
`targets.zrd` into the record list, with every scene node carrying bit 31 of `node+0x28`. An
object that a `targets.zrd` record creates without such a node never gets `+0x8d` and is therefore
not a turret candidate, though it stays selectable on the player's Non-Aircraft cycle.

⚠ **Ships and vessels are in the turret's set, and they are there as vehicles.** They ride
`VehicleList`, the pool the picker walks first and never gates. Nothing in the path promotes a
hull to an aircraft or reads an airframe field off one, so a port that reaches the same behaviour
by registering a ship as an aircraft has ported the wrong mechanism. An AI pilot's own acquisition
(`FUN_0041fe10`, [`aiPilot.md`](aiPilot.md) "Target acquisition") delegates to this same
`FUN_0041f9c0`, so an aeroplane ranks a hull on the same terms; CSVM's
`FlightController.SelectRankedTarget` reads each candidate's name and flags off its own source
type for that reason.

A turret's `SetTeam` override (`FUN_004acb70`) clears its current target pointer `+0x210` whenever
the team actually changes (`0x004acb90`), so a retargeted turret drops a now-friendly lock rather
than keeping it.

### The two targetable bytes the predicate reads first

Before the team comparison, `FUN_004a5b90` requires the candidate entity's `+0x8c` **and** `+0x90`
to be non-zero. Both are on the mission-structure object, the `TargetStruct` arm alone (an
aeroplane takes `FUN_004a5b20`'s `+0x91d`/`+0x944` pair instead), and both are decoded:

- **`+0x8c` is the scene node's active bit, walked up the parent chain.** `FUN_004a2730` rewrites it
  every frame at `0x004a2840` from `FUN_004a32c0(node)`, which returns 1 only when the node and
  every ancestor still carry bit 2 of `node+0x24`, the same active bit a destroy sequence clears.
  The construction default at `0x004a2637` is 0, so an object is untargetable until its first
  refresh. This is what silences a turret the instant an airframe swap switches the flying model
  off, and it is the only one of the two that moves at runtime.
- **`+0x90` is a construction-time targetable byte.** `FUN_004a2570` writes 1 at `0x004a264f`, and
  exactly two sites clear it: `0x004a3000` in `FUN_004a2e00` (an object built from a `targets.zrd`
  record) and `0x004a34b5` in `FUN_004a3360` (the same fallback path for a scene object). Nothing
  in the mission-script verb table writes it, so a flagged mission-structure node keeps the 1 for
  its whole life and a `targets.zrd`-only object never had it.

⚠ **Neither byte asks whether the part's parent VEHICLE is alive**, only whether the scene chain
above it is still active, so a destroyed airship's surviving parts stay candidates. What removes
them in the original is the hull's own death choreography destroying them one by one, decoded in
[`aiPilot.md`](aiPilot.md) under "A dead vehicle's surviving parts stay candidates".

### The picker's cost, and the three weights it spends

`FUN_0041f9c0` asks its query object for each candidate's cost through the query vtable's slot 0.
For a turret that vtable is `0x00608b68` and the function is `0x004ac720`:

- **The range gate is first and is hard.** The candidate's own position getter against the query's
  position (`0x005388d0`), compared at `0x004ac73e` to the query's `+0x18`, which `FUN_004aabb0`
  fills from the turret's `+0x168`, the entry's authored `DETECTION_RANGE`. Anything further away
  returns the constant at `0x00608b40`, `1e+21`. That is the same value a candidate the predicate
  rejected scores, and it is above the loop's `1e+20` seed, so an out-of-range candidate can never
  win the minimisation.
- **In range the cost is `1200 * weight + distance`** (`0x00603534` holds `1200.0`, and the add is
  at `0x004ac81a`). The weight starts at `1.0` and is **replaced**, not accumulated:
  `0.8` when the candidate's entity is the local player's plane (`DAT_0071c298`, compared at
  `0x004ac75a`); `0.6` when the turret's own `+0x6f` byte is set and the candidate casts to
  `TargetProjectile`; `1.4` when it casts to `TargetVehicle` whose `+0x67c` is `4`, the `wingman`
  mode class, which is the same preference the AI pilot's scorer spends as an additive `+0.4`
  ([`aiPilot.md`](aiPilot.md)). A further `0.5` comes off whatever weight stands when the wrapper's
  `+0x1c` virtual reports the gasbag flag, and a `TargetTurret` candidate instead takes `42.0`
  (`0x00608b6c`) onto its **distance**.

So the weights are 240 m, 480 m and 600 m of slack in a distance race, not filters. A turret on the
player's team never reaches the `0.8` branch at all, because the predicate has already rejected the
player as friendly.

### Nothing in the turret path reads an objective, capture or pickup flag

⚠ **The pool membership, `FUN_00422890` and the cost function above are the entire filter.** Every
call the turret update `FUN_004aabb0` makes on the way to a lock was read end to end, and neither
`+0x4c` (`otherTarget`) nor `+0x4d` (`objectiveTarget`) appears in any of them. Those two bytes are
read in exactly one place, `FUN_004b5cd0`, the **player's own** candidate class filter, which is why
`ADD_OBJECTIVE_TARGET` moves a marker and changes nothing about who shoots. There is no capture
state, no "marked for pickup" bit, and no per-zeppelin exclusion list on the turret side: a
zeppelin record's `targets` list is the broadside's, parsed by `FUN_004bd8d0` and never consulted
by `FUN_004aabb0`.

**The worked example is C3/M05's Pandora.** `ai.zrd` gives the four `piratezep` turret entries
`TEAM 1` with `DETECTION_RANGE` 600 m and 800 m, and the mission's `OBJECTIVE1` wakes them two
seconds in (`WAKEUP_ZEP_TURRETS [piratezep]`). The three `britbalmoral_*` roster blocks author team
`2` in `aiv.zrd` slot 3, and their `britbalmoral` vehicle def inherits `mode jet` from
`basic_airplane`, so they take the plain `1.0` weight a Peacemaker takes. The mission's own script
never issues `SET_AI_TEAM` on them. A Balmoral flying its docking approach to `pzhookpoint` is
therefore both hostile to those rings and the nearest thing to them, and the original engages it.
The rings fall silent only when the docking sequence switches the flying airframe off and `+0x8c`
goes to 0.

### What a mission structure's team is

**A mission structure's team is written on the scene node, not in `targets.zrd` and not as a
default.** `FUN_004a2e00`'s first pass reads the flagged node's own word at `node+0x28`, shifts it
by `(2 * DAT_0071c0a0 - 2) & 0x1f` (the shift is computed once at `0x004a2e18`…`0x004a2e1f` and
applied at `0x004a2e41`…`0x004a2e4b`), masks two bits, and hands that raw integer to the identity
constructor `FUN_00453740` at `0x004a2e50`. The result is the 4th argument of the object
constructor `FUN_004a2570` at `0x004a2ea5`, which writes it to `+0x8` like every other team.

**`DAT_0071c0a0` is the mission's own number inside its chapter, 1-based.** It and
`DAT_0071c09c` (the chapter, also 1-based) are fields of the session object at `0x0071b480`, at
`+0xc20` and `+0xc1c`, which is why an xref search on the absolute address finds only reads: every
write goes through the object's base register. `FUN_004636a0` initialises both to `-1`
(`0x00463765`, `0x0046376b`) and `FUN_004638f0` sets them together (`0x00463907`, `0x0046390d`),
reached from the profile path through `FUN_0041a880`. Their ranges are readable straight off the
two name lookups: `FUN_004639d0` switches the chapter over cases 1-8 into the strings at
`0x00625a70` (`c1`, `c1b`, `c1c`, `c2`, `c2b`, `c3`, `c4`, `c5`), and `FUN_00463a50` switches the
mission over cases 1-5 into `0x00625a90` (`m01`…`m05`), or `mp1`…`mp5` and `ia1` for the other two
modes at `+0x700`. `FUN_0046b490` bounds them at 9 and 10 and formats the save name
`%s\Mission_%1d_%02d` from the pair.

> So the shift `2 * mission - 2` selects **the current mission's own two-bit slot**: `m01` reads
> bits 0-1, `m05` reads bits 8-9, and the eleven slots below bit 22 are one owner per mission of
> the chapter. The value is a team id directly, through the identity constructor: `0` neutral,
> `1` the player's side (`FUN_004830c0`), `2` and `3` the enemy indices (`FUN_0045c260` mints
> `index + 2`, so `2` is enemy 0, which is also what a turret defaults to at `0x004a9a99`).

**A slot of `0` means "this node names no owner", not "neutral by decision".** `FUN_004a32f0`, the
team resolution the world-object factory `FUN_004a3360` uses at `0x004a3493`, walks the node and
then its ancestors through `**(node+0x58)`, takes the first non-zero slot it finds, and only falls
through to `FUN_004a3f80` (neutral) when no ancestor authors one either. So a part inherits the
group it hangs under. ⚠ The mission-structure constructor itself does **not** walk: `FUN_004a2e00`
reads the flagged node's own slot and hands it over raw, so a flagged node authoring nothing for
this mission is built neutral.

The word is the node record's own field at file offset 40 (mech3ax `unk040`, the extraction's
`field040`), the dword after the flags word at 36 that carries `ACTIVE` and `INTERSECT_SURFACE`.
The name-driven authoring is `FUN_004a29a0`, which `FUN_004a2af0` registers as the scene reader's
per-node hook (`FUN_004c5b80` at `0x004a2af5`) beside the `MStructList` pool itself. It reads a
node name of the form `xyz_<letter><digit>`: `name[3]` must be `_`, `toupper(name[4])` indexes the
byte table at `0x004a2ad0` into the jump table at `0x004a2ab0`, and `name[5]`'s digit picks a
two-bit slot at shift `2 * digit - 2`, with `+` at `name[6]` filling that slot and every higher
one.

| Letter | Case | Effect |
|---|---|---|
| `A` | `0x004a29e4` | fill `0x55555555`, so every named slot reads `1`, the player's side |
| `E` | `0x004a29eb` | fill `0xAAAAAAAA`, so every named slot reads `2`, enemy index 0 |
| `O` | `0x004a29e0` | fill `0`, so every named slot reads unowned |
| `T` | `0x004a2a72` | set bit 31: this node is a mission structure |
| `V` | `0x004a2a7f` | set bit 30 |
| `G` | `0x004a2a8c` | set bit 22, the gasbag flag |
| `D` | `0x004a2a99` | set bit `22 + name[5]`, one of the five sibling flags |
| any other | `0x004a2aab` | nothing |

⚠ **The slot writes cannot touch the flags, and the flag writes cannot touch a slot.** The routine
saves the word on entry and, at `0x004a2a5e`…`0x004a2a6a`, restores bits 22-26, 30 and 31 from that
saved copy over whatever the slot write produced, clearing bit 29. So the eleven slots occupy bits
0-21 and everything above them is flags. Bits 27 and 28 are the only crossover: an `A`/`E` fill
runs into them and they survive the mask, which is why an ally fill reads back as `0x10155555`
rather than `0x00155555`.

**The census over the eight shipped chapters: 1196 flagged nodes, and each authors the same owner
in every one of its slots**, so no mission of a chapter sees a different set of sides from another.
Per chapter the split runs from 25 to 51 on the player's side, 42 to 148 on the enemy's, and 28 to
69 unowned. The flagged nodes are the state children inside a group (`healthy`, `gunback`,
`panels`, `healthy_part`, `tank`, `g7`, `part1`), which is where a group becomes a target:

- `world1/redcross/shipshape/healthy`, C1's Red Cross hospital ship, is `0x90155555`, **the
  player's side in every slot**, so an enemy gun is hostile to it and the player's own is not.
- `local_xyz/goose_engines/g_engineN/healthy_part`, C2's Spruce Goose, is the same word.
- `policeN/healthy/l1/g7` and a zeppelin's `rock_zeppelin/gasbagN/*/healthy` are `0x882AAAAA`, the
  enemy side in every slot.
- Every node authoring nothing at all (`0x80000000`, `0x80400000`) is a zeppelin `panels` or
  `gunback`, and a zeppelin's own record team is fanned over those parts anyway.

⚠ **The picker's 4th argument is what keeps a player-team turret off the pool in C2/M05.** With
`DAT_00629c20` cleared there, a turret on the player's team stops seeing mission structures at all,
while every other gun in every other mission keeps them.

**`targets.zrd` authors no team.** `FUN_004a3a60` reads `description`, `other_target`, `objective`,
`category_label`, `help_label`, `stickiness`, `colored_background`, `background_color`,
`ladder_pickup`, `fixed` and `nodes`, and nothing else. A record whose node is already a mission
structure finds that object in the shared registry (`FUN_004a2850` at `0x004a2f80`) and only stamps
`+0x4c`/`+0x4d` onto it, keeping its team; a record that has to build its own object builds it
**neutral** through `FUN_004a3f80` at `0x004a2fb8`, and that object never gets `+0x8d`. So an
objective site standing on an ordinary node is neutral in the original too.

### Zeppelins carry a record override, not a second space

A zeppelin's `+0xE0` (with flag byte `+0xDC`) is the mission record's team override, parsed by
`FUN_004bd8d0` from the strings `enemy` (`0x0062b6c4`), `ally` (`0x0062b6cc`) and `neutral`
(`0x0062b6d4`), or from a raw integer (writes at `0x004bd977`, `0x004bda02`, `0x004bda35`,
`0x004bda5e`). Note which constructor each string picks: `enemy` takes `FUN_0045c260(_, 0)` and is
therefore **2**, `ally` is `1`, `neutral` is `0`, and a raw integer is stored raw. `FUN_004bee80`
then fans that one value out onto `+0x8` across the whole airship, its turrets included.

### World objects are in the same space, and are normally neutral

Destructibles are not a separate class and are not excluded by list membership. A named scene node
becomes a combat object through the find-or-create factory `FUN_004a3360`, which tries the vehicle
registry (`0x004a3385`), the turret registry (`0x004a33cc`) and the combat-object vector registry
(`0x004a3415`), then falls back to resolving a type-7 scene node through `FUN_004d0280(7, name)` at
`0x004a3459`, allocating `0x94` bytes at `0x004a346b` and constructing it with the same
`FUN_004a2570` at `0x004a34a3`. The new object is pushed into the shared candidate registry
`DAT_0071d338` by `FUN_0045c590` at `0x004a34d1`, wrapped in the same `0x14`-byte node
(vtable `0x0060364c`) the Instant Action aircraft path uses at `0x0045b30c`.

Its team comes from `FUN_004a32f0` (called at `0x004a3493`): walk the node, then its ancestors via
`**(node+0x58)`, and at each one extract a **two-bit field** from `node+0x28` at bit offset
`(2 * DAT_0071c0a0 - 2) & 0x1f`, the slot belonging to the mission being flown ("What a mission
structure's team is" above). The first non-zero value found is the team id directly. If every
ancestor yields zero, it falls through to `FUN_004a3f80` and the object is **neutral**. Two runtime
writers `OR` into this word, `0x40000000` at `0x0048490c` and `0x10000000` at `0x004807ef`, but
both set bits above the eleven slots, so neither changes any object's team.

So hostility toward a world object is decided by team number like everything else, and an
unauthored one is untargetable because it is neutral, not because it sits outside the pools.

⚠ **An ownership slot is two bits wide**, so a scene node can only ever author `0`–`3` per mission.
That is a bound on *this* field, not on the space: the stored id at `+0x8` is a full integer, and an
`aiv.zrd` team is a raw integer read verbatim. What it corroborates is that the ids in play are
small and that no banding scheme exists anywhere.

## The candidate list

`FUN_004b5fb0`, once per frame:

1. Release every entry of the previous frame's list. **The list is rebuilt from scratch every
   frame**; nothing about it persists.
2. If any of the four class flags is set, walk all four pools and offer each candidate to
   `FUN_004b5cd0`:
   - `VehicleList` (`DAT_0071dabc`): every entry.
   - Turrets (`DAT_0071d914`): only if `+0x4c` or `+0x4d`.
   - `MStructList` (`DAT_0071d33c`): only if `+0x4c` or `+0x4d`.
   - Live fused ordnance (`DAT_0064f78c`): only if its `+0x6c` tracking byte is set. `FUN_00441830`
     sets that byte on the `TARGETABLE` path alone, so a round wrapped only because it is fused is
     on the list and unselectable. The wrapper's display name is the literal string id `0x2f6a`
     (`MSG_WEAP_AERIAL_TORPEDO`, "Aerial torpedo") for every such round, not the weapon's own
     `DESC`; see [ordnanceTypes.md](ordnanceTypes.md).
3. Sort with `FUN_004bb9b0` under the comparator `FUN_004bbd60`.
4. Re-resolve the current selection: `FUN_004b6490(currentTarget, 0)` finds the entry whose
   underlying entity matches, or returns the **first entry** if it is gone. Clone it, release the
   old, store at `+0x948`.
5. Publish the target's position and velocity to plane `+0x294`/`+0x298`, or zero both if there is
   no target.
6. In a network game, publish the target kind and net id to `+0x728`/`+0x72c`.

Step 4 is the entire lifecycle mechanism. See "Lifecycle" below.

## Where a mission structure is

Step 5 reads the entity's own position through vtable slot 0. For a mission structure that slot is
`0x004a36e0`, three bytes long: `LEA EAX, [ECX + 0x74]`. It returns a cached vector, and slot 1
(`+0x80`) returns the velocity the same cache derives.

**`+0x74` is the world-space CENTRE of the structure node's active bounding box, not the node's
origin.** The constructor `FUN_004a2570` stores the scene node at `+0xc` and fills `+0x74` from
`FUN_004cf2c0(node)`, which reads the node's active bbox (`FUN_004cd960` copies the six floats at
`node + 0x70`) and takes its midpoint (`FUN_004d8b10`: `(min + max) * 0.5` per axis) in the frame
`FUN_004cef20` builds up the parent chain. `FUN_004a2730` recomputes the same value every frame for
a structure whose `+0x8f` moving flag is set, and derives `+0x80` as the frame-to-frame delta over
the tick, which is why a structure on a flying node is marked where it now is.

⚠ **A group node's own origin is not its site.** Two shipped cases prove it: C2's `sghangar` carries
the no-transform `"Initial"` and so stands at the world origin 8 km from the building, and C1/M05's
nine `lifesaverNM` attack-balloon groups stand on the water with the balloon hung 16.4 m above them
and the lifeboat at the group's own origin, so a marker on the node reads as a marker on the boat.
The authored bbox is the answer in both: `lifesaver11`'s `child_bbox` spans y −1.411 to 24.745 in
group coordinates, putting its centre 11.667 m up.

## The cycle order

`FUN_004bbd60` compares two candidates against a 15-float snapshot of the player: position in
`[0..2]`, then the plane's basis rows from `+0x180` in `[3..14]`. For each candidate:

```
v      = targetPos − playerPos
a      = dot(v, row0)        // row0 = plane +0x180, the RIGHT axis
b      = dot(v, row2)        // row2 = plane +0x198, the NEGATED forward axis
theta  = atan2(b, a) + pi/4            // [0x00608038] = 0.7853982
theta  = wrap(theta, 0 .. 2pi)         // FUN_00460b10
q      = trunc(theta * 2/pi)           // [0x006035a8] = 0.63661975  ->  q in 0..3
key    = q == 0 ? 3 : q == 3 ? 0 : q   // swap the ahead and right sectors
```

with two overrides that set `key = −1`:

- the entity has `+0x4d` set (an objective), or
- the candidate is a `TargetProjectile` whose owner is on a different, non-zero team (incoming
  ordnance).

Candidate A sorts before B when `key(A) < key(B)`, or when the keys tie and `|vA|² < |vB|²`.

Because `row2` is the *negated* forward axis (the same convention `aim-assist.md` records for
`FUN_004b6530`'s seed), the resulting sector order is:

| key | Sector | Meaning |
|---|---|---|
| −1 | (none) | mission objectives and incoming hostile ordnance |
| 0 | `theta` in [5pi/4, 7pi/4) | **ahead**, the 90° quadrant centred on the nose |
| 1 | `theta` in [pi/4, 3pi/4) | **behind** |
| 2 | `theta` in [3pi/4, 5pi/4) | **left** |
| 3 | `theta` in [−pi/4, pi/4) | **right** |

Nearest first inside each sector. The sectors are 90° wide and centred on the axes, so a target
45° off the nose sits on a boundary.

**So "Nearest Enemy/Objective" returns: the nearest objective if any exists, otherwise the nearest
enemy in the forward quadrant, otherwise the nearest one behind, otherwise left, otherwise right.**
It is not a global nearest, and a target 200 m off the left wing loses to one 900 m ahead.

## Stepping the cycle

`FUN_004b6490(current, dir)`:

- Scan the list for the entry whose *underlying entity* matches `current`. Match by entity, not by
  wrapper pointer, because the wrappers are new objects every frame.
- Not found (or `current` is null): return the first entry, or 0 if the list is empty.
- Found: `dir > 0` steps forward with wrap to the head, `dir < 0` steps backward with wrap to the
  tail, `dir == 0` stays put.
- After each step, `FUN_004b6480` is consulted and returns constant `1`, so every entry is
  acceptable; the loop's "came back to where we started, give up" guard is the only exit.

## Next Enemy/Objective prefers whoever shot you

Action `0x24` alone consults a second list first, and this is the least guessable behaviour on the
page.

`FUN_004b9770` is the take-a-hit path. When the local player is hit by a shooter on a different,
non-zero team, it wraps that shooter as a `TargetVehicle` and appends it to the queue at
`DAT_0071d388` via `FUN_0045c590(DAT_0071d390, 1, …)`, the end insert. The queue is
cleared only at mission load (`0x00474b64`) and pruned by the death hook `FUN_004a64e0`.

`0x24` then walks that queue **backwards from the end**, i.e. most recent attacker first:

- Queue empty → fall through to the ordinary cycle step `+1`, and play `sg_switchtarget`.
- Current target not in the queue → select the **last** entry, the most recent attacker.
- Current target found in the queue → select the entry **before** it, an older attacker.
- Current target is the queue's first entry → fall through to the ordinary cycle step `+1`.

None of the queue branches play the switch sound; only the fall-through does.

⚠ `FUN_004bc1e0` is called on the wrapper immediately before the insert and is probably a
remove-if-present, which would move a repeat attacker to the end rather than duplicating it. Not
traced.

## Select Target Nearest Crosshairs

Action `0x2d`, `FUN_00488db0`. It ignores the class flags and the candidate list entirely and runs
its own scan over all four pools, scoring each with `FUN_00488ce0`:

```
v = targetPos − playerPos
d = |v|
if dot(v, row2) > d * −0.9659258   ->  reject (FLT_MAX)
else                               ->  score = d
```

`0.9659258` is `cos(15°)`, and `row2` is the negated forward axis, so the test accepts a target
**within a 15° half-angle cone of the nose** and scores it by plain slant range. The running best
starts at **2000.0**, which is therefore a hard 2 km maximum range for this action.

It scores against the **nose axis**, not against the gun pipper. The pipper is a separate,
velocity-derived point (`../formats/hud.md`, `aim-assist.md`), and nothing in this path reads it.

Candidate gates in the scan are looser than the cycle's:

- Aircraft: everything except the player's own plane, and except own-team aircraft whose `+0x67c`
  is neither 0 nor 4. **Friendlies are included**, which is why the action reaches an ally.
- Turrets, structures: only with `+0x4c` or `+0x4d`.
- Ordnance: only with the `+0x6c` tracking byte.

Having chosen, it *writes the class flags back* from what it found, so a subsequent Next/Previous
continues in that target's own class: ally → Ally, enemy → Enemy, turret/structure/`+0x4c` →
Non-Aircraft, and `+0x4d` → the Objective companion pair.

## Lifecycle

- **Mission start.** `0x00474a6b` sets the class to Enemy/Objective (`478 = 1`, `47b = 1` with
  `-too` absent) and clears both lists. The target itself is null.
- **Auto-acquire.** Because the class flags are non-zero from mission start, `FUN_004b5fb0` builds
  a list on the first frame and its re-resolve step returns the list head. **The player therefore
  starts a mission already targeting the head of the Enemy/Objective cycle**, with no input.
- **Target death or despawn.** `FUN_004a64e0` is the hook: it nulls the player's `+0x948` if it
  points at the dying entity, removes it from the candidate list and the attacker queue, nulls
  every AI's `+0x948`, and clears any turret's `+0x210`. The next frame's re-resolve then picks the
  **head of the current cycle**. So the original does switch on death, and it switches to the head,
  not to the neighbour of the dead entry.
- **Target merely leaving the list** (dying, going out of the class, or the class changing) has the
  same effect: re-resolve drops to the head.
- **Explicit clear.** `0x2e` nulls the target *and* all four class flags. With every flag zero,
  `FUN_004b5fb0` skips the collection pass, the list stays empty, and the re-resolve returns 0. So
  **`Target Nothing` stays cleared**, because the auto-acquire cannot fire again until a class key is
  pressed. This is the mechanism behind the sticky-clear, and it is worth pinning with a test.
- **Range, line of sight and field of view drop nothing.** No such gate exists anywhere in the
  path.
- **The player's own death** was not traced. `FUN_00421500` and `FUN_00469e20` both zero a plane's
  `+0x948`, and whether either runs on the player's respawn is unresolved.

### A remake-only setting: nearest after a kill

The head rule above is the default and the only decoded behaviour. Because the head of
Enemy/Objective is the nearest objective whenever one exists, every kill in a defence mission sends
the pilot back to the objective, which is the complaint the remake answers with one setting rather
than a change. `nearestAfterKill` (off by default, `OptionsStore`, the Next Target row on Original's
Game Options page and the second row of Built-in's Options screen, reaching the flight through
`SessionSpec.WithSavedNearestAfterKill` and `FlightRosterPolicy`) replaces the head with the nearest
live member of the current cycle by distance from the plane, and only on a re-resolve that *lost* a
selection it held under the same class. The mission-start auto-acquire and every class change keep
the decoded head either way, since neither had a selection to lose. With the setting off the
behaviour is the decoded one unchanged. `TargetSelection.NearestAfterKill` is where it stands, and
the `target-selection` suite pins both sides over one rig where the head and the nearest disagree.

⚠ **The player and the AI share the field, not the rules.** `+0x948` is one field on the shared
plane class; `FUN_0041fe10` writes it for an AI actor using rating biases and a standing-target
hysteresis (`aiPilot.md`), and `FUN_004b5fb0` writes it for the local player using this page's
rules. Reading `aiPilot.md`'s selection logic as the player's is the mistake this note exists to
prevent.

## The HUD: the bracket box

`FUN_004574d0` draws **six line sprites** forming a `[ ]` pair around the target's projected point.
The geometry is three absolute pixel constants:

| Constant | Value | Role |
|---|---|---|
| `0x00607a0c` | **10.0** | half-width |
| `0x00607a14` | **8.0** | half-height |
| `0x00607a10` | **4.0** | arm length |

so the box is a fixed **20 × 16 pixels** with 4-pixel arms, centred on the projected point. Nothing
scales it: not range, not resolution, not field of view. The only depth test is the near-plane
epsilon `0.1` at `0x006034a8`; behind the camera, all six sprites hide.

**Measured, and it matches exactly.** On `Targeting HUD Kestrel.png` the two vertical strokes sit at
x = 1943 and x = 1963 (20 px apart) and span rows 202–218 (16 px), with 4-pixel arms at both ends.
On `C1 M04 Zeppelin.png`, a different shot at a different resolution, the strokes sit at x = 1839
and x = 1859 and span rows 834–850. Same 20 × 16 box.

### The range gate is the selected gun's `RANGE`

This is the bracket range threshold, and it is not a HUD constant.

If a gun group is selected (plane `+0x604` index is non-negative and its weapon def resolves),
`FUN_004574d0`:

1. Solves the lead intercept with `FUN_00460e30`, the aim assist's own solver, on the target's
   position and relative velocity.
2. Forms the muzzle velocity vector `dir * VELOCITY + planeVelocity` (`VELOCITY` is def `+0x2c`).
3. Draws the box only when the solver succeeded **and** `|muzzleVel|² · t² <= RANGE²`
   (`RANGE²` is def `+0x20`, precomputed at parse, see `aim-assist.md`).

Otherwise all six sprites hide. If **no** weapon def resolves, the fallback gate is a plain
`distance <= 1e6`, which never fires in practice.

So the observed "brackets only under a range threshold" is exactly "the selected gun could reach
the target's intercept point inside its authored range". For the player guns that authored range is
`RANGE 1000` (`../formats/weapons.md`), so the box appears at roughly 1 km and closer, less
whatever the target's own motion costs in intercept time. A target outrunning the round gets no box
at any range, because the solver returns no intercept.

⚠ **This makes the marker weapon-dependent.** Switching gun groups changes when the box appears,
and a rocket-only loadout has no gun group to gate on. That is behaviour the screenshots cannot
show and a fixed-metres port would lose.

### Colour

`FUN_004a5f40` (`Target::GetColor`) returns one of three colours:

| Condition | Colour |
|---|---|
| No category label (`+0x40` clear), teams differ and both non-zero | **(200, 0, 0)** red |
| No category label, same team or either team 0 | **(0, 255, 0)** green |
| Category label is `Destroy`, `Disable`, `Disable Engines` or `Damage` | **(200, 0, 0)** red |
| Any other category label | **(0, 0, 255)** blue |

The four red-listed category ids are `0x1f42`, `0x1f46`, `0x1f49` and `0x1f8d`, which are
`extracted/messages.json` ids 8002 / 8006 / 8009 / 8077, i.e. `MSG_OBJ_DESTROY`, `MSG_OBJ_DISABLE`,
`MSG_OBJ_DISABLEENG`, `MSG_OBJ_DAMAGE`.

⚠ **A friendly target is green, not blue.** Blue is reserved for a non-destructive objective
(protect, escort, and the rest of the `MSG_OBJ_*` set). The `hud_v2.zrd` `HUD_COMMON` block's
`COLOR_RED (200,20,20)` / `COLOR_BLUE (40,40,240)` are a different pair and are not these.

## The HUD: the label

`FUN_004579e0` draws **three stacked text lines**, 15 pixels apart, each in its own font: line 1 in
`targetHelp`, line 2 in `targetTitle`, line 3 in `targetPos` (the font names come from
`FONT_TARGETHELP` / `FONT_TARGETTITLE` / `FONT_TARGETPOS`, `extracted/messages.json` ids
3880/3885/3890, and are looked up through `FUN_00530700`). All three hide when there is no target.

**Line 1** composes the entity's own label pair, `+0x28`/`+0x2c` (the name) and `+0x3c`/`+0x40`
(the category), through one of four format strings:

| Case | Format | Address |
|---|---|---|
| Both present | `"%s [%s] -"` | `0x006253ac` |
| Name only | `"%s -"` | `0x006253b8` |
| Category only | `"[%s] -"` | `0x006253c0` |
| Neither | blank | `DAT_006f1ee8` |

**Line 2** is the entity's own name string at `+0x14`/`+0x18`, and its source is settled: the
**roster block's own `title`** (`aiv.zrd` slot 20, the `MSG_*_NAME` pilot key). The block reader
`FUN_00437620` `_strdup`s element 20 into the block struct's `+0xc` (from `src+0xac`, the reader's
`list + 0xc + 8k`), and the spawn path `FUN_0047c210` reads it at `0x0047c9a2`, resolves it through
`FUN_0059cd20` (key to message id, stored at entity `+0x20`) and `FUN_0059ce40` (id to text), then
assigns the text into the `std::string` at entity `+0x10` at `0x0047c9c8`. A key the string table
does not know is copied verbatim instead (`0x0047ca98`–`0x0047cafd`).

⚠ **A block that authors an empty `title` gets no name line at all**, the branch at `0x0047c9a7`
leaves the string empty. That is 239 of the install's 414 roster blocks, so most enemies in the
original show a box and no name.

**Three authors write that string, and the vehicle definition is none of them.**

- **The campaign roster**, as above: `aiv` slot 20 through the block struct's `+0xc`.
- **Instant Action**, in `FUN_0045a390`, which builds the same block struct three times and writes
  `+0xc` in each: the player's flight from five hardcoded string ids (`0x32c9`, `0x32cb`, `0x32cc`,
  `0x32cd`, `0x32e4`) at `0x0045a7ae`, the ace from `ia.zrd`'s `ace_name` at `0x0045ab0a`, and each
  enemy group from its own `enemy_name` at `0x0045ae73`. `ia.zrd`'s parser resolves both keys at
  parse time (`0x0045946b`, `0x00458e36`), so they arrive as display text. This is why an Instant
  Action Kestrel carries a name line while an unnamed campaign block does not.
- **A template-less generator spawn**, in `FUN_00451bf0`, which assigns entity `+0x10` directly from
  the `egen.zrd` record's `vehicle`/`title` value at `0x004520ee`. That path takes the value RAW,
  with no string-table lookup, so whatever the file authors is displayed literally. The two are
  complementary: an `egen` record that resolved a roster block runs the roster path above instead.

⚠ **The `vehicle.zrd` def's own `title` (`MSG_VEH_*`) is read by none of them.** `FUN_00479240`,
the vehicle-definition parser, stores it at that definition's `+0xc` (`0x004792ca`–`0x00479305`),
a different object. ⚠ "No fourth author exists" is NOT established: the sweep covered the entity
constructor's site and all five of its callers, not the whole image.

**A surface hull is named by the same author, off its own block's slot 20.** `FUN_0047c210` is THE
vehicle spawn and not the aeroplane's: it allocates the 0xa20-byte entity, links it into
`VehicleList` and runs one body for every dynamics class. Its two arguments are the `vehicle.zrd`
DEFINITION, matched by name out of the definition list `DAT_0071daac`…`DAT_0071dab0` with the
record's trailing `_N` stripped, and the roster BLOCK; the campaign's record loop `FUN_004735b0`
calls it once per mission vehicle record at `0x0047531e` with no class test on the way in. The
definition's `mode` (def `+0xa4`, [`aiPilot.md`](aiPilot.md)) is read once inside, at `0x0047c2b4`,
and its `ship`/`tank` arm (`0x0047c2ca`–`0x0047c2fa`) only prepares the model before rejoining the
shared body at `0x0047c2fd`. The slot-20 read at `0x0047c9a2` and the assignment into entity
`+0x10` sit in that shared body, so a boat reaches them exactly as an aeroplane does.

The reader is class-blind at the other end too: `FUN_004579e0` takes the player's selection
`+0x948`, dereferences the target wrapper's `+4` (the wrapped entity, written by `FUN_004a6330`)
and reads the length at entity `+0x18` and the pointer at `+0x14`, with no RTTI test and no virtual
call on that path.

So the name line's split is per BLOCK and never per class. Of the install's 23 `mode ship` blocks
(`patrolboat_*`, `t_truck_*`) only C1B/M03's four author the slot, as `MSG_VEH_PATROLBOAT` and so
"Patrol boat"; C1/M05's twelve, C5/M01's six and C2/M01's `patrolboat_eg0` generator template leave
it empty and draw a box with no name over it, which is the same silence 239 of the 414 blocks ask
for.

**Line 3** is the `%d o'clock` bearing. `FUN_0049d940` computes the hour and formats
`MSG_N_OCLOCK` (`extracted/messages.json` id 132, `"%1!d! o'clock"`) into a 256-byte buffer, and
`FUN_0049d8a0` produces the hour itself: the difference between the plane's world heading and the
bearing to the target, wrapped to `[0, 2pi)`, converted to twelfths and truncated, with 0 mapped to
12.

⚠ **Nothing wraps.** The zeppelin's two lines are two *different elements* with different fonts, not
one string broken at a wrap width. There is no wrap width to port.

So `C1 M04 Zeppelin.png`'s two visible lines are line 1 (`Zeppelin [Destroy] -`) and line 2
(`Promised Land`), and `Targeting HUD Kestrel.png`'s single visible line is **line 2**. An
ordinary aircraft carries no category label, so line 1 renders blank and only the name shows.

### Where the label sits

`FUN_004574d0` computes the anchor from the bracket box:

- `anchor = boxBottom + 3` when `boxBottom + 33` still fits inside the viewport,
- otherwise `anchor = boxTop − 30`.

So **the label is below the box in the normal case and flips above it** when the box is within 33
pixels of the viewport's bottom edge. The three lines then sit at `anchor`, `anchor + 15`,
`anchor + 30`, and the block is horizontally centred on the box.

**Measured.** Kestrel: box bottom row 218, anchor ink at rows 221–222, the `Kestrel` glyphs at rows
239–247. That is `218 + 3 = 221` for the anchor and `221 + 15 = 236` for line 2, with ~3 px of
font leading before the ink. Zeppelin: box bottom row 850, line 1 ink at rows 856–866, line 2 ink at
rows 871–879, a 15-pixel line pitch and the same ~3 px leading. Both label blocks are centred on
their box to within a pixel (Kestrel ink centre 1953 against box centre 1953; zeppelin 1848.5
against 1849).

### Off screen

`FUN_0049d940` also computes a clamped **edge position** and stores it at `DAT_0071d284 + 0x200` /
`+ 0x204` behind a flag at `+ 0x208`. `FUN_004579e0` reads exactly those three and, when the flag is
set, places the label block at the edge position instead of at the projected point; it additionally
clamps each line into the viewport with a 3-pixel margin. The bracket box hides in this case,
because `FUN_004574d0`'s near-plane test fails. That is the `HUD.png` reading: an edge arrow with
the tag and the clock bearing stacked beside it.

**The edge arrow, and the round live picture beside it, are [`spyglass.md`](spyglass.md).**
`FUN_0049d940` is that page's function: it owns the arrow's shaft and head, the Shift+S toggle, the
96 x 96 camera-2 window and the label anchor this section reads, and the anchor's own vertical
offset (3 pixels below the marker in the screen's upper half, 45 above in the lower) is decoded
there.

## What this means for CSVM

| | Original | CSVM today |
|---|---|---|
| Who picks the target | the player, from eleven bound actions | the pilot, from the same eleven, each a named action the Controls door rebinds (`Bindings/DefaultBindings.cs`, dispatched in `FlightController.StepTargeting`). The keys differ: the original's three class letters and their Shift and Ctrl forms are all spent on flight here, and a binding is one control, so the five class and class-less actions ship on `T`/`Y`/`U`/`I`/`O` and the six per-class Previous and Nearest on the digit row above them (`../controls.md`) |
| Selection state | sticky in plane `+0x948`, survives everything except death and an explicit clear | the same, in `TargetSelection`, one instance per pane and owned by that pane's `FlightController` |
| Candidate pool | four typed pools, rebuilt and re-sorted every frame | `TargetPool`, rebuilt every frame off `AimCandidateSet`'s aeroplanes and live ordnance plus two curated feeds, the mission's flagged sites and the zeppelin sub-parts (those only while the pilot's selected ordnance carries `LOCK_ON`). The structure and turret lists are never walked, since a mission's table is what decides |
| Classes | Enemy / Ally / Non-Aircraft, plus an Objective companion flag | the same three, `TargetClass`, with the objective flag on the ref (`TargetRef.Classify`) |
| Team space | one space for everything: `0` neutral, `1` ally, enemy index `N` = `N + 2`, stored at `+0x8` on every combat object | the same space; an authored id is the runtime id |
| Hostility test | one predicate over raw ids: differ, and neither is `0` | `AimAssist.Hostile`, asked by both the gun assist and the turret gunner rather than restated at each gate |
| A turret's candidate set | all four pools (the table above): the whole `VehicleList`, every turret, the `+0x8d` mission structures, and tracked ordnance | `TurretController.AcquireTarget` walks the whole `VehicleList` through `ProjectilePool.CollectVehicleList`, so a hostile hull is a candidate beside the aircraft. The other three pools are not scanned yet |
| A turret's cost | a hard `DETECTION_RANGE` gate, then `1200 * weight + distance` with three class weights | `TurretController.AcquireTarget` gates on `Def.DetectionRange` and then scores plain distance, so the player, torpedo and `wingman` weights are not spent. Nothing in either version consults an objective or capture flag |
| A turret against a structure | admitted through `MStructList`, and hostile when the structure's team differs | `TurretController.AcquireTarget` walks the structure pool after the vehicles against the same running best, through `ProjectilePool.CollectMissionStructures`, and drops a gasbag as the decoded pass does |
| A mission structure's team | the node's own ownership slot for the mission being flown, inherited from the parent chain where it authors none | the same: `SceneBuilder` resolves the slot for the built mission (`GameZ.WorldObjectTeam`, `SceneBuilder.MissionSlot`) and stamps it, and `DestructibleRegistry.Register` reads it onto the pool, so C1/M05's hospital ship is the player's and a zeppelin's zones are the enemy's |
| Splitscreen pilots | no per-pilot ladder exists | a remake-only rule: pilot 0 is the player's side, further pilots land in `AimAssist.VersusTeamBand` so a `--vs` player cannot inherit the id the no-`TEAM` emplacements default to |
| World objects | neutral until a scene node authors two-bit ownership, and untargetable while neutral | the same: `AimCandidateSet.AddStructures` falls a pool with no authored team through to `AimAssist.NeutralTeam`. Two sources author one, a zeppelin record and the flagged node a pool stands on |
| Turrets and structures | selectable **only** when the mission flags them `otherTarget` / `objectiveTarget` | the same in every flown mission: `ObjectiveSites.CollectFlagged` reads that mission's own `targets.zrd` and puts each `other_target` entry on the Non-Aircraft cycle. A gun emplacement reaches no cycle at all, alive and hostile or not, because no shipped table names one; a loose destructible never reaches one either. The zeppelin sub-parts are the one stand-in for a flag nothing authors, a deliberate divergence narrowed to the capability it exists for: `TargetPool.Rebuild` offers them only while the pilot's selected ordnance carries `LOCK_ON`, so off the torpedo the cycle is the flagged list alone and a selected part is dropped rather than held. Instant Action and the multiplayer modes take the same feed with no director behind it |
| Cycle order | objectives first, then ahead / behind / left / right, nearest inside each sector | the same, `TargetSelection.SectorKey` and its sort |
| "Nearest" | head of that order, not a global nearest | `TargetSelection.Nearest`, on one key per class (`TargetNearestEnemy`, `TargetNearestAlly`, `TargetNearestNonAircraft`) and reachable through `--target=nearest` |
| Nearest-crosshairs | 15° nose cone, nearest inside it, 2000 m cap, friend or foe | the same, `TargetSelection.NearestCrosshairs`, on `TargetNearest` |
| Marker box | fixed 20 × 16 px with 4 px arms, gated on the selected gun's `RANGE` through a lead solve | the same shape and the same gate, scaled through `HudMetrics` rather than fixed in pixels (see below) |
| Label | three lines, 15 px pitch, below the box (above near the bottom edge), centred | the same, `TargetHud.LabelLines` and the flip-above test |
| Label content | `<name> [<category>] -` / proper name / `%d o'clock` | the same three lines, off `TargetRef`'s own label halves and display name |
| Name line's source | the roster block's `title` alone, aeroplane and surface hull alike; an unnamed block shows no name | a campaign spawn takes the block's `title` (`AiSpawn.PilotName`), and where it has none the remake keeps an airframe title the original does not print there. A hull takes the same slot through `SurfaceVehicleRuntime`'s own resolve into `SurfaceVehicle.MarkerName` and prints NOTHING where its block authors none, which is the original exactly |
| Colour | red hostile, green friendly, blue non-destructive objective | the same, `TargetHud.MarkerColor`, with the four destructive objective categories red and the rest blue |
| Off screen | edge position plus the same three lines, clamped with a 3 px margin | the same edge position, `EdgeMarker.Resolve`, with `TargetHud.EdgeLabelAnchor` hanging the three lines off it by the decoded +3 / -45; the per-line 3 px clamp is not ported |

⚠ **The "no reference to copy" claim once made in `VersusHud`'s module doc was false.** The
original draws an edge arrow with a stacked tag and clock bearing, which is what CSVM's
`DrawOpponent` edge branch already does, corrected in the code itself.

⚠ **Fixed 20 × 16 pixels does not port literally.** The original never scales its box, so on a
modern display it would be nearly invisible. Scaling through `HudMetrics` like every other CSVM HUD
element is the right port and is a deliberate divergence, not a fidelity loss.

## Not resolved

- What clears the player's target on the player's **own** death or respawn. `FUN_00421500` and
  `FUN_00469e20` both zero a plane's `+0x948`; neither was traced to the player's respawn path.
- Whether `FUN_004bc1e0` de-duplicates the attacker queue.
- The `aiv.zrd` key names behind entity `+0x28` (label), `+0x3c` (category), `+0x4c` and `+0x4d`.
  The name at `+0x14` is settled (above, slot 20 `title`), and the other two are read from the same
  block struct: `+0x28` from block `+0x114` (block element 38, `0x0047d53f`) and `+0x3c` from block
  `+0x118` (element 39, `0x0047d69e`). ⚠ Their SCHEMA names are still inference: the exe's format
  comment at `0x00622508` names three fewer fields between `primary_target` and `title` than
  `FUN_00437620` consumes, so aligning the comment's tail on `title = 20` (which would make 38
  `categoryLabel` and 39 `helpLabel`) is a `+3` shift that has not been proved.
- The text elements' font metrics. The ~3 px of leading between a line's anchor and its ink is
  measured on two shots, not decoded.
- Whether the box's absolute pixel constants interact with the resolution-doubling branch in
  `FUN_00457400` (`FUN_00440bb0`). Both measured screenshots render at about 2560 px wide, so they
  do not test a second resolution.
- Whether an Instant Action **wingman** reaches the ally constructor. That `1` is the ally id is
  certain, but `FUN_0045a390` never calls `FUN_004830c0`; its other three `FUN_004a2570` sites go
  through `FUN_004a3360` (`0x0045a9ba`, `0x0045b522`, `0x0045b962`) and derive the team from the
  scene node instead of a literal.
- Where an authored mission aircraft's loaded record field `+0x34` is transferred onto the runtime
  object's `+0x8`. The instantiation loop `FUN_004735b0` walks the record list
  (`0x00474cba`…`0x0047535d`), `__RTDynamicCast`s to the vehicle record at `0x00474cfb` and ends at
  `FUN_004a3360`, but reads no `+0x34` and calls no setter. That the authored integer is the engine
  id is confirmed at the file-format level (`0x00437723`, and the writer `FUN_00438010` printing
  `+0x34` straight through `%d`) and at the script level (`SET_AI_TEAM`), not end to end. No `+2`
  remap exists on that path.
- Whether the AI's own target selection uses a range-gated scan at all. `FUN_0041f9c0` has three
  callers: the turret update, and two reached from a draw callback registered at `0x004739e8`, that
  is the player HUD. The AI's standing target appears to come from mission data
  (`primary_target` / `otherTarget` / `objectiveTarget`) rather than a proximity picker, but that
  was not traced.
