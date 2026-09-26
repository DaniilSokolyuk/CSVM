# Cutscenes: the `letterbox` node and `CALLBACK` codes

Part of: [animation definitions](../anim-definitions.md).

Source: the `letterbox`/`camera1` nodes in every chapter's `gamez.zbd`, the shared readers
`zrdr/letterbox.zrd`, `zrdr/generic_intro.zrd` and each chapter's `zrdr/landings.zrd`, the
per-mission intro readers (`C1/M04/zrdr/intro.zrd` and `scenes.zrd` are the worked example),
and `crimson.exe`. Scope: how a scripted cutscene presents itself (black bars, camera, suspended
simulation) and what the `CALLBACK` event kind means. The event's own reader syntax and its
compiled shape are on the [landing page](../anim-definitions.md) and in
[`org/sequences.md`](../../org/sequences.md).

## Contents

- [Conceptual model](#conceptual-model)
- [The `letterbox` node](#the-letterbox-node)
- [The reparent is how a cutscene is composed](#the-reparent-is-how-a-cutscene-is-composed)
  - [A composition frame is a bodiless gamez node the `world1` walk never reaches](#a-composition-frame-is-a-bodiless-gamez-node-the-world1-walk-never-reaches)
  - [`AT_NODE_XYZ` takes the host's ROTATION FIELDS, `AT_NODE_MATRIX` its composed matrix](#at_node_xyz-takes-the-hosts-rotation-fields-at_node_matrix-its-composed-matrix)
  - [The wing walk's cast, and where each figure lives](#the-wing-walks-cast-and-where-each-figure-lives)
- [`player`, and the two pointer spaces a definition addresses](#player-and-the-two-pointer-spaces-a-definition-addresses)
- [`CALLBACK`: the dispatch chain](#callback-the-dispatch-chain)
- [`CALLBACK` code reference](#callback-code-reference)
  - [The re-placement code 951](#the-re-placement-code-951)
  - [Handoff and skip](#handoff-and-skip)
- [The intro defs' eight dispatches](#the-intro-defs-eight-dispatches)
- [Reader rules and edge cases](#reader-rules-and-edge-cases)
- [Evidence and limits](#evidence-and-limits)

## Conceptual model

A cutscene in this engine is an ordinary animation definition that happens to drive `camera1`,
plus two mechanisms that make it read as a movie instead of as gameplay.

**The bars are data.** `letterbox` is a pair of opaque black quads shipped in every chapter's
gamez and a four-line shared animation definition that activates them and glues them to the
camera. The string `letterbox` does not occur anywhere in `crimson.exe`; nothing engine-side
knows the feature exists. Any def that wants bars calls the def by name.

**The state changes are notifications.** A `CALLBACK` event carries one integer and nothing else.
The engine's handler passes that integer to whatever native function is registered on the running
animation instance, so **the authored value is the whole message and its meaning belongs to the
registered host**, not to the def it sits in. The host that mission scripting installs is a single
interpreter with a switch over the integer; a different host (the vehicle-death one) reads the
same integers as something else entirely.

The two mechanisms are independent. The bars need no callback, and the callbacks say nothing
about the bars.

## The `letterbox` node

### The geometry

`letterbox` is node index 6 in every chapter's `nodes.json`, sitting among the engine roots
(`world1`, `display`, `window1`, `camera1`, `sgwin`, `spyglass`). It carries no model of its own
and exactly one child, `g1`, which carries model index 0. That model is eight vertices forming
two quads in the camera's own frame:

| quad | x | y | z |
|---|---|---|---|
| top bar | ±6.6461835 | 2.490714 to 4.0474105 | −7.5 |
| bottom bar | ±6.6461835 | −4.0474105 to −2.460203 | −7.5 |

Every vertex colour is white, every UV is `(0, 0)`, and both polygons use **material 0, a
`Colored` material of `rgb (0, 0, 0)` at `alpha 255`**, opaque black with no texture. The card as a
whole is 13.2924 wide by 8.0948 tall with a 4.9509-tall gap between the bars, so the open window
is about 61 % of the card's height and the bars overhang the card's own width by design. The
record is bit-identical in all eight chapters (C1, C1B, C1C, C2, C2B, C3, C4, C5), model 0 in
each.

### Why the node ships `active: false`

`zrdr/letterbox.zrd` holds one definition, and its `RESET_STATE` is a single
`OBJECT_ACTIVE_STATE { NAME letterbox, STATE INACTIVE }`.

`RESET_STATE` is the object's authored base state, applied at load
([landing page](../anim-definitions.md#definition-fields)). The gamez therefore ships the node in
exactly the state its own definition asserts for it: **the `active: false` flag is not an opt-out
and not dead data, it is the def's base state baked into the scene**. The bars exist, fully built,
from mission load onward and are simply switched off until something calls the def. This is the
same node pair `world-structure.md` counts as one of the two parentless roots shipped inactive.

### What the def does

```
ANIMATION_DEFINITION NAME[letterbox] ACTIVATION[ON_CALL] EXECUTION_PRIORITY[5] RESET_TIME[0]
  RESET_STATE          OBJECT_ACTIVE_STATE letterbox INACTIVE
  SEQUENCE_DEFINITION  OBJECT_ACTIVE_STATE letterbox ACTIVE
  SEQUENCE_DEFINITION  OBJECT_TRANSLATE_STATE letterbox AT_NODE[camera1]
                       OBJECT_ROTATE_STATE    letterbox AT_NODE_MATRIX[camera1, 0, 0, 0]
                       LOOP LOOP_COUNT[-1]
```

The first sequence turns the bars on, in one event with no tween: **the bars are simply there from
the moment the mission load ends**, and the intro's own `fadefromblack` FBFX then fades up behind
them. There is no bars-in animation to reproduce (answered at the controls against the original).

The second is the `LOOP{-1}` re-assert idiom: every tick it
copies `camera1`'s position onto the node and adopts `camera1`'s orientation matrix with a zero
offset. **The node is never reparented** (no `OBJECT_ADD_CHILD` anywhere targets it), so the bars
stay a parentless root that tracks the camera by transform copy, and the quads' authored `z = −7.5`
puts them 7.5 m in front of the eye, well inside `camera1`'s `clip_near` of 1.0.

⚠ **"Every tick" is not a moment, and in CSVM the moment is what matters.** The pin copies a
transform, so its value depends on where in the frame it is read, and the node it reads is a moving
target: a cutscene composes itself by reparenting `camera1` under something animated (the section
below), so `camera1`'s global transform changes whenever that parent does. `AnimRuntime.Advance`
walks its live instances newest-first, and the `letterbox` instance is created by the cutscene's own
`CALL_ANIMATION`, so it is the newer of the two and re-pins before the definition that composed it
gets its turn. The rig cameras take `camera1`'s pose later still, from
`CutsceneController.Tick` at `ProcessPriority` 1000, after the whole advance. Left at that, the bars
clad the previous frame's frame while the eye is already in this one, and the world shows along
whichever edge the camera is moving away from for as long as it keeps moving. The frame is fitted by
the card's height, so the vertical margin is `CardOverscan`, 2 % of the card, on every window, and
the horizontal one is 2 % as well from the card's own 1.642 ratio up and more below it: the leak
reads on any edge. The host
therefore re-asserts the pin itself, in the same instant it hands the pose to the rig cameras
(`CutsceneController.PinBars`); the definition's own pin is unchanged and the two agree whenever the
walk already got the order right.

The def is `ON_CALL` and every call site gives it the local name `local_letterbox`. Nothing in the
install ever stops it: a scan of every `STOP_ANIMATION`, `INVALIDATE_ANIMATION` and
`RESET_ANIMATION` in every reader finds **zero** that names `letterbox` or `local_letterbox`. The
bars go away when the node returns to `INACTIVE`, which is what the def's own `RESET_STATE` does.
`RESET_TIME [0]` is the scheduling field whose detail this project has not decoded, so the exact
moment of that reset is an open question; see [limits](#evidence-and-limits).

### Where it is called from

27 `CALL_ANIMATION letterbox` sites across 18 readers:

| reader | sites | what it is |
|---|---|---|
| `generic_intro` | 1 | the shared story-mission intro, in `start_script` |
| `C1/M04` `scenes.zrd` | 5 | the bespoke M04 intro's scene beats |
| `hangar_drop` | 4 | C1/M02 hangar drop |
| `blacke_drop`, `bhmhookup` | 2 each | C4 drop and hookup |
| `pzep_hookup`, `copilot_pkup`, `pickford_pickup`, `paratroopers`, `tex_drop`, `destroy_cargozep`, `wingwalk`, `wv_tailhook`, `sparks_pkup`, `cghookup`, `carney_pkup`, `nypd_drop`, `miles_drop` | 1 each | mid-mission pickup, hookup and drop cutscenes |

Only one of the 27 is an intro. **Letterbox is the engine's general cutscene idiom**, and the
mid-mission pickup and hookup sequences are cutscenes by the same construction as the intros.

## The reparent is how a cutscene is composed

A cutscene's camera keyframes are written in the frame of the node the shot is about, and
`OBJECT_ADD_CHILD` / `OBJECT_DELETE_CHILD` are what put the camera in that frame. Both take a
`parent` and a `child` name; an add moves the child under the parent and a delete detaches it back
to the world root, and **neither preserves the child's world pose**, the local transform is the
whole point.

`camera1-generic_intro`'s `start_script` opens with `OBJECT_DELETE_CHILD [world1, camera1]`,
the same for `player`, then `OBJECT_ADD_CHILD [piratezep, camera1]` and `[piratezep, player]`;
its `RESET_STATE` deletes all three of `camera1`, `player` and `piratefighter` from `piratezep`
and re-adds only `player` to `world1`, which is consistent with a delete meaning "detach", since
`camera1` and `piratefighter` are parentless roots in the gamez to begin with. C3/M01's
`player-texdrop` does the same around a per-shot aiming node: it adds `player` under
`do_direction`, runs an SI script on it, deletes it again, and its third sequence adds `camera1`
under `do_direction` for the shot and deletes it after.

The offsets only make sense read this way. `gi_cam1`'s first keyframe is
`(-155.7, -62.0, -768.9)` and `piratezep` sits at `(-1401, 500, -1412)`: a y of −62 is an offset
inside a node at y = 500, and in world space it is 62 m under the sea.

⚠ The reparent is authored as a live sequence event. A `RESET_STATE` walk carries the undo, and a
consumer that applies it during a bootstrap pass moves shipped nodes off a parent no definition
has changed yet.

### A composition frame is a bodiless gamez node the `world1` walk never reaches

`piratezep` and `do_direction` are placed world content, so a consumer that walks `world1` has them
in hand. Two shots instead compose inside a node that is nowhere in that walk: a **parentless,
childless, model-less `Object3d`** whose only job is to be the frame a shot is written in. C3's
`wingwalk_parent` (node 661) and C5's `carney_pickup_parent` (node 5633) are the whole set in this
install, and both are addressed the same way, named as an `OBJECT_ADD_CHILD` parent by their
mission's own definitions, and posed onto the vehicle the scene is about before anything plays.

`britbalmoral_1-ww_balmoral1` is the worked example. Its second sequence poses `wingwalk_parent`
`AT_NODE britbalmoral_1` (translate and `AT_NODE_XYZ` rotate), moves `britbalmoral_1` under it and
zeroes that node's local translation, then calls `wingwalk` `AT_NODE wingwalk_parent` and its own
`ww_balmoral` SI script before raising 967. `wingwalk_parent-wingwalk` calls `ww_player`,
`ww_ladder` and `ww_zachary` and drives its own node 30 m/s forward for **19.25 s** between codes
913 and 914; `player-ww_player` adds both `player` and `camera1` under `wingwalk_parent` and runs
SI scripts `ww_cam1`–`ww_cam4` on `camera1`. So the entire shot, the camera, the player's
aeroplane, the wing-walk figures, is written in the frame of one node whose authored transform is
the map origin, and every one of those keyframes reads as a position over the water unless that
node is standing in the world and has been posed onto the captured aeroplane.

CSVM stands such a frame up beside `camera1` and the `letterbox` bars
(`WorldSession.BuildCompositionFrames`), found off the bound program rather than by name, and the
mission's roster rig answers for the vehicle's own library-root name so the `AT_NODE` pose has a
host (`Mech3/RosterMarkers.cs`). ⚠ The frame is placed rather than authored, so its motion must
launch from where the definition put it: see `docs/org/objectMotion.md`'s re-home rule.

### `AT_NODE_XYZ` takes the host's ROTATION FIELDS, `AT_NODE_MATRIX` its composed matrix

`OBJECT_ROTATE_STATE` is dispatch slot 9, `004e8b80` (the 47-slot table at `DAT_00727de0`, populated
by `FUN_004ee1a0`; see [org/sequences.md](../../org/sequences.md)). The event's flag word at
`+0x00` selects the basis, and the two `AT_NODE` bits reach different sources:

| flag | branch | what it reads off the host |
|---|---|---|
| `0x2` `AT_NODE_XYZ` | `004e8bdc` | `Object3d::GetRotation` (`004d1b40`, class data `+0x18/+0x1c/+0x20`) or, for a `Camera` host, `Camera::GetRotation` (`004d2680`, `+0x20/+0x24/+0x28`), the host's own stored euler triple |
| `0x4` `AT_NODE_MATRIX` | `004e8c76` | `gwNodeBuildNodeToAncestorMatrix` (`004cef20`) then matrix→euler (`0053df30`), the host's composed world orientation |

Both branches then add the authored `state` triple componentwise and write the result through
`Object3d::SetRotation` (`004d1a30`), which stores the target's own euler fields. A host that fails
to resolve leaves the three floats at zero, which is the world-axis fallback.

**The euler fields are not the same thing as the drawn orientation.** `Object3d::SetMatrix`
(`004d1f90`) writes twelve floats into class data `+0x30` and sets flag bit `0x10`; it leaves
`+0x18/+0x1c/+0x20` alone, and `GetRotation` does not recompute them. The flight model places a
vehicle's node exactly that way (`FUN_0048e580` calls `004d1f90` at `0048ecca`), so an
`AT_NODE_XYZ` rotate off a flying aeroplane reads the rotation it was last SCRIPTED to, never the
attitude it is banking at. The translate side is the other way round: `OBJECT_TRANSLATE_STATE`
(slot 7, `004e8de0`) takes the host's position through `004cf490`, which builds the accumulated
matrix, so it IS live.

That asymmetry is the whole shape of a wing-walk shot. The frame lands on the aeroplane wherever it
is, and stays level however the aeroplane is banking. CSVM implements it as
`PoseChannel.PoseAtNode`'s `scripted` arm reading `AnimRuntime.PlacedRotationOf`, the rotation the
roster placed a spawned vehicle at; every other host contributes its live frame, as `AT_NODE_MATRIX`
always does.

⚠ All 180 `AT_NODE_XYZ`/`AT_NODE_MATRIX` rotates in this install author a zero `state` triple, so
the componentwise add is unexercised and whether a non-zero offset composes before or after the
host's rotation is undecided.

### The wing walk's cast, and where each figure lives

`wingwalk_parent-wingwalk` calls three definitions, and their roots sit in two different archives:

| definition | root | where | reached by |
|---|---|---|---|
| `ww_player` | `player` | aircraft archive 1418 (C3 ptr 8918) | `AircraftStage`'s bodiless marker |
| `ww_ladder` | `rope_ladder` | aircraft archive 241 (C3 ptr 7741) | `AircraftStage.FigureNodes` |
| `ww_zachary` | `pickup_cpilot` | aircraft archive 2269 (C3 ptr 9769) | `AircraftStage.FigureNodes` |

`rope_ladder` fans to `ladder_roll` → `rung1`–`rung6` (archive 36/27/40/30/29/634/2) and ships
`INACTIVE`, which is why `ww_ladder` opens by activating it. `pickup_cpilot` fans to
`cpilot_parent` → `cpilot_drop` → the thirteen `cp_*` limbs (2270–2287) and ships ACTIVE; nothing
ever activates it, because in the original a parentless library root is simply not reached from
`world1` until an `OBJECT_ADD_CHILD` moves it into the shot. CSVM reproduces that by hanging both
under a switched-off holder: indexed and resolvable, drawn only once the reparent happens.

The capture's own definition, `britbalmoral_1-ww_balmoral1`, addresses three more nodes that are
**chapter** nodes rather than archive ones: `body` (C3 1058, under `britbalmoral_1` → `healthy` →
`nearest` → `pilot_pos` → `pilot`), `head` (1075) and `hatch` (1089, under `nearest` → `nose` →
`hatchparent`). They live inside the chapter's own copy of the vehicle, which the world never
places, so the compiled claim binds nothing; the aeroplane the mission's roster spawned carries the
same names in its own subtree, off the shared `balmoral`/`player_balmoral` airframe. The original
finds them because its first resolution tier is a depth-first walk of the definition's own root
subtree ([org/sequences.md](../../org/sequences.md), "The tier chain"), and that root IS the
vehicle. CSVM keeps the rig's subtree as a **scoped alias** rather than putting those names in the
shared index, `pilot`, `body` and `healthy` are the commonest names in the archive, and a global
index of them would let any definition claim them (`AnimRuntime.IndexSpawnedVehicle`).

One more thing the capture needs from the vehicle beyond its root: its first sequence re-asserts
`OBJECT_ACTIVE_STATE [britbalmoral_1, true]` inside a `Loop{1000}` at 0.01 s, ten seconds of it.
That is what keeps the captured aeroplane drawn against code 913's AI park, so the active bit has
to reach the aircraft's own drawn state and not only the rig node
(`AnimRuntime.SetTargetActive`).

## `player`, and the two pointer spaces a definition addresses

### A cross-archive `ptr` is the aircraft archive's index plus a per-chapter base

A compiled definition's symbol table gives every name a `ptr`, and those pointers do not all index
the same table. A chapter node's `ptr` is its position in that chapter's own `nodes.json`:
`world1` 0, `camera1` 3, `letterbox` 6, and C3's `piratezep` 1020. A node from the shared aircraft
archive (`planes/nodes.json`, 3317 nodes) is its position there plus a base, and the base is that
chapter's own node count rounded up to the next multiple of 2500:

| chapter | `nodes.json` | base | `player` | `piratefighter` |
|---|---|---|---|---|
| C1 | 7064 | 7500 | 8918 | 9824 |
| C1B | 5603 | 7500 | 8918 | 9824 |
| C1C | 5644 | 7500 | 8918 | 9824 |
| C2 | 4956 | 5000 | 6418 | 7324 |
| C2B | 4901 | 5000 | 6418 | 7324 |
| C3 | 5408 | 7500 | 8918 | 9824 |
| C4 | 8289 | 10000 | 11418 | 12324 |
| C5 | 11438 | 12500 | 13918 | 14824 |

Eight chapters, one rule, no exceptions. Within C3 the same base resolves nine cross-archive names:

| name in `camera1-generic_intro` and its called defs | `ptr` | `planes/nodes.json` |
|---|---|---|
| `cockpit1` | 7549 | 49 |
| `player_balmoral` | 7525 | 25 |
| `healthy` | 7649 | 149 |
| `shadow` | 7701 | 201 |
| `destroyed` | 7702 | 202 |
| `player_warhawk` | 8135 | 635 |
| `player` | 8918 | 1418 |
| `piratefighter` | 9824 | 2324 |
| `staticprop1` | 9963 | 2463 |

The base always clears the chapter's own table, so the two spaces cannot overlap. Reading a
cross-archive `ptr` against the chapter's table is what makes `player` look like a name with no
node behind it, and it is why the same name carries a different number in every chapter while the
aircraft archive it points into is one shared file.

⚠ The rule is measured over these eight chapters and nothing else. No site in `crimson.exe`
computing the rounding has been traced, so a ninth chapter's base would be a prediction. CSVM
implements it as `(count / 2500 + 1) * 2500` (`AircraftStage.PointerBaseOf`), which reproduces all
eight; a count that is already an exact multiple of 2500 does not occur in this install, so which
way the rounding breaks there is undecided.

### `player` is the player's own aircraft

`player` (aircraft-archive node 1418) is a parentless `Object3d` whose single child is
`player_pfighter`. The airframe name table at `0x00620cc0` in `crimson.exe` carries seven name
pointers per airframe (display name, player-model node, `p<name>`, `r<name>`, `w<name>`, remote
node, def name), and `player_pfighter` is the **Devastator's** player-model node: that row reads
`Devastator`, `player_pfighter`, `pdevastator`, `rdevastator`, `wingman`, `piratefighter`,
`devastator`, the only row whose fifth and sixth fields break the `w<name>` / `<name>` pattern.
Every other airframe's player model is a parentless root of its own (`player_bhawk`,
`player_fury`); `player_pfighter` is the one that ships inside a wrapper.

The name is what resolves, not the pointer. `FUN_004d0280(7, "player")` walks node table 7
comparing strings, and `FUN_0042e5e0` calls it to switch the node off around a render pass and
back on after. The mission setup path builds the player's own vehicle record and passes that same
literal with it: `FUN_004136e0` ends with `FUN_00414f40("player", <record>)`, once per arm of a
two-way choice over the loadout, and that function packs the record and calls
`FUN_0041a320(<record>, "player")`.

The intro's own use of the name agrees from three further directions. `callback_sequence` opens
with `OBJECT_ACTIVE_STATE [player, false]` beside code 11, which is what takes the player out of
flight, and `RESET_STATE` closes with `[player, true]` beside codes 1 and 10, the handoff and the
systems restore. `start_script` moves `player` out of `world1` and under `piratezep`, and the undo
puts it back under `world1`, which is where the flown aircraft lives. And codes 965, 966 and 967
swap the player onto a named airframe, which only means anything if the node they swap is the
player's.

⚠ So the shipped `player` node wraps a Devastator because that is what sat in the slot when the
aircraft archive was built, not because the intro is about a Devastator. What airframe the intro
actually shows is the one the player flies, which is also why C1/M04's intro branches on
`check_balmoral` / `check_warhawk`.

### What a `generic_intro` stages

Two aircraft, both from the aircraft archive rather than the chapter's gamez.

- **`piratefighter`.** `gi_pfighter1` activates it, sets `healthy` on and `destroyed` / `shadow` /
  `staticprop1` off, calls `wing_lights_blink` and `spinprops` on it, adds it under `piratezep`,
  and runs its own SI script. `gi_pfighter2` repeats that for the later shot.
- **`player`.** `gi_1stperson` activates it, sets `healthy` on and `cockpit1` off, and runs
  `gi_player1`; `gi_playerdrop` runs `gi_player2` over the launch, with `snd_droplaunch` and a
  `wing_lights_blink` addressed to `player_pfighter` **by name**, the one place the authored data
  reaches past the slot to the Devastator's model directly.

CSVM stages both, from the aircraft archive, only for a mission that bootstraps an intro
(`Mech3/AircraftStage.cs`). `piratefighter` is built as a prop with no pilot and no flight model,
drawn in the archive's own shipped state: the node is ACTIVE in `planes.zbd`, the shared
`piratefighter` reader def's `RESET_STATE` asserts its children's states and never its own, and
`gi_pfighter1`'s `OBJECT_ACTIVE_STATE [piratefighter, true]` re-asserts what already holds. That
is what C1/M04's own intro relies on: its `pfighter11`..`pfighter13` (called by `scene1`,
`playerdrop` and `playerthruclouds`) set the children, call `wing_lights_blink`, and fly the prop
on an SI script, with no activation and no `OBJECT_ADD_CHILD` at all, so a prop built switched
off shows that intro's launch and dive with the wingman missing. Where a script leaves it is where
it stays after the handoff (`pfighter13` ends 95 m over the water past the dive), in this engine
and the original alike. `player` is a bodiless marker, since the aeroplane
it stands for is the one the pilot flies and that model belongs to the flown `FlightController`.
Every staged node's compiled pointer is rebased onto the chapter's own base, so the definition's
symbol table binds the names it addresses. `OBJECT_ACTIVE_STATE [player, false]` stays callback 11's
out-of-flight state, and the pose half is the marker: while that state holds, the flown airframe is
drawn on the marker's world pose and put back where the mission spawned it at the handoff. The
drop's own end pose is not what a CSVM session starts flying from.

⚠ The cross-archive names below `player`, `healthy`, `cockpit1`, `shadow`, `destroyed`, are the
DEVASTATOR's copies, since that is the airframe the archive's `player` wrapper holds. A pilot flying
anything else leaves those four events unresolved, which is correct: the states they assert are the
model's own base states, and the flown airframe already carries them.

### A mid-mission drop's `chuteman` is the same staging gap

The intro is not the only definition that addresses a name below the aircraft archive's base.
C3/M01's drop-off (`tex_drop.zrd`) plays `do_approachN` → `player-texdrop`, whose `CALL_ANIMATION
[tdchute]` starts the `chuteman`-named definition compiled as `chuteman-tdchute.json`: it deletes
`chuteman` from `world1`, adds it under `do_direction`, switches it `ACTIVE`, and runs an SI script
over `chutemanparent`, `pilot` and `stamp` in turn (`snd_chuteopen` follows). `chuteman` is
aircraft-archive node **2288** (a parentless `Object3d`, model-less, one child `chutemanparent` at
2289, which fans to `pilot` at 2290, the parachutist's own mesh, and `stamp`, the parachute canopy,
at 2291), the same archive `player`/`piratefighter` come from, addressed the same way: C3's base
7500 makes the compiled def's symbol table read `chuteman` 9788, `chutemanparent` 9789, `pilot`
9790, `stamp` 9791, each exactly `2288..2291 + 7500`. The shared `chuteman.zrd` reader ships the
node `INACTIVE` as its own `RESET_STATE`, which is where it differs from `piratefighter`: that
node ships ACTIVE and no `RESET_STATE` touches it.

Before `BL-540`, `AircraftStage` staged `player`/`piratefighter` alone, so `chuteman`'s subtree
never joined the runtime's node table: the drop's own definition claimed a symbol with a null
binding and the parachutist was invisible, seen at the controls with no error (the resolver's
"claimed-but-unbuilt index" guard is deliberately silent, `AnimRuntime.Targets`). CSVM now stages
`chuteman` beside the other two, under the same gate (a mission whose start-anims name an intro,
C3/M01 has one), switched off until the drop's own `CALL_ANIMATION` reparents and activates it. A
mid-mission drop in a mission with no intro of its own is not staged by this path; see
`docs/architecture.md`'s `AircraftStage` entry.

### C2/M05's `balmoral` is the same gap, with no intermediate call

CM15's capture cutscene, `balmoral-drop_paratroopers` (`anim_root_name: "balmoral"`, node list
`cargozep2`, `balmoral`, `camera1`), reached the same unstaged-symbol gap without going through a
`chuteman`-style redirect: the definition is rooted on `balmoral` itself, so `PlayMissionTrigger`
resolves the anchor directly, and the definition's own `Initial` sequence carries the
`OBJECT_ADD_CHILD`/`OBJECT_MOTION_FROM_TO`/`OBJECT_DELETE_CHILD` triple that reparents it onto
`cargozep2`, moves it through the shot, and hands it back to the world root, with no separate
caller involved. `balmoral` is aircraft-archive node **2381** (a parentless `Object3d`, model-less,
five children), ships ACTIVE and authors no `RESET_STATE`, the same base state `piratefighter`
ships in. Unlike `piratefighter`, no OTHER mission's own script ever names `balmoral`, so
`AircraftStage` stages it under a switched-off holder rather than at the world root: own `Visible`
matching the archive's ACTIVE state, rebased the same way, but drawn nowhere until the drop's own
`OBJECT_ADD_CHILD` reparents it out from under that holder. A build that stages it at the world
root directly, the way `piratefighter` is, finds the node the drop needs but also draws an idle
Balmoral at the archive's build origin in every other mission that stages an aircraft.

### What a staged prop is painted in

A staged prop is a second model of an aeroplane the mission already flies, so it wears that
aeroplane's livery rather than the archive's shipped skins. Two of the staged nodes carry aircraft
skins at all, which is what a livery needs: `balmoral` (prefix `bal`) and `piratefighter` (`dev`).
`chuteman`, `rope_ladder`, `pickup_cpilot`, `anim_bloodhawk` and `bloodhawk_gear` carry no decal
placeholder to read a prefix off, so no scheme can reach them. The two hangar hand-over props stay
in their shipped skins by decision: the floor Bloodhawk is a few dozen pixels in shadow, and the
aeroplane the gear rides on the lift is rebuilt without a scheme, so the gear has no livery to
disagree with. `anim_bloodhawk` does carry `blo_*` skins, but its one decal texture is
`bldhwk_decal.tif`, not a logo placeholder; `bloodhawk_gear` is skinned from `hollybomber01.tif`.

Where each of the two takes its scheme from differs, and only one of them rests on authored data:

| staged node | stands in for | what decides the livery |
|---|---|---|
| `balmoral` | CM15's `balmoral_1` roster block, Tex's bomber, the aeroplane `drop_paratroopers` is filmed around | the block's own spawned rig. Its def chain (`balmoral` → `basic_airplane`) authors **no** `paint_pattern`, and the block itself authors no livery slots (it ships 66 fields, short of slot 68), so the rig wears CSVM's default pattern rather than a value read out of a file |
| `piratefighter` | no roster block: an intro prop the SI scripts fly | the `devastator` def, whose own `nodename` **is** `piratefighter` and which authors `paint_pattern player_fortune`; `wingman` derives from it and repeats the same pattern |

Both therefore come out Fortune Hunters in the shipped campaign, but for different reasons, and a
mission whose stand-in block flies for another militia would take that militia's skins instead. A
staged node whose stand-in the mission never spawns keeps the shipped skins.

## `CALLBACK`: the dispatch chain

| stage | where | what happens |
|---|---|---|
| reader parse | `FUN_00518470`, reached from the event-name dispatch in `FUN_0051c3e0` | `CALLBACK [VALUE[n]]` (plus optional `START_TIME`) becomes a 16-byte event of kind `0x23` (35) carrying `n` at `+0xc`. Parsing one also sets the def's has-callbacks flag, bit `0x10` at def `+0x9c`. |
| runtime dispatch | slot 35, `FUN_004ec5e0` | calls the animation instance's registered native function at `anim+0x74` with `(anim, anim+0x78 context, value)`. **If none is registered the event does nothing.** |
| host registration | `FUN_004ee160(anim, fn, ctx)` | the only setter, 13 call sites install-wide. |
| the mission-script host | `0045e0f0` → `FUN_0047e080` | the interpreter with the code switch below. |

Of the 13 registration sites, exactly one installs the mission-script host: **`FUN_0045df60`, the
`landings.zrd` trigger**. The other twelve belong to vehicles, weapons and the danger-zone system,
and each installs its own handler with its own reading of the integers (the vehicle-death handler
`LAB_00480710` takes 0, 15 and 16 only, see [`org/vehicleDamage.md`](../../org/vehicleDamage.md)).

### `landings.zrd` is the cutscene trigger table

Each chapter ships a `zrdr/landings.zrd` of records shaped
`anim [name], node [name], angle [deg] speed [min, max]` or `anim, node, auto`. C1's three:

```
anim[hooked_to_klondike] node[pz_manual_land] angle[35] speed[50, 220]
anim[hooked_to_klondike] node[pz_auto_land]   auto
anim[lookat_copilotpkup] node[agent_approach_cone] angle[45] speed[50, 250]
```

`FUN_0045df60` ticks these every frame: with no cutscene already running and the player not already
in the uncontrolled state, it tests the player against the named approach node's condition object
and speed band, starts the named animation, and registers the mission-script host on it. That
registration is why a mid-mission cutscene's `CALLBACK`s are live. 32 distinct anim and node names
appear across the eight files, all of them approach cones, hookups and landings; **no intro
definition is named in any `landings.zrd`**.

The started definition's call closure can carry actors that are parentless gamez library roots.
C1/M02's `lookat_copilotpkup` calls `got_the_pilot`, whose `pickup_objective` root becomes inactive
and satisfies OBJECTIVE3, and `cabpkup_player`, whose player and camera choreography runs beside the
caboose. Resolving the row itself without materializing those callees starts presentation but leaves
the camera at an unrelated world pose and the primary objective uncleared.

#### The condition object is an authored triangle

The loader `FUN_0045d8f0` reads the file and hands each record to the parser `FUN_0045da80`, which
resolves the row into a nine-field record and drops the row outright when a field fails to resolve.
The keys it reads are exactly `node`, `anim`, `angle`, `speed` and `auto`.

| record field | source | meaning |
|---|---|---|
| `[0]` | `node [name]` | the approach node, whose world pose the attitude test measures against |
| `[1]` | its `land_on` descendant | the arming gate: the row is dead while that node's active bit (bit 2 of `+0x24`, `gwNodeSetActive`) is clear |
| `[2]` | its `cone`, `half_cone` or `sphere` descendant | the shape node, whose world pose the volume is expressed in |
| `[3]` | built from `[2]`'s model | the condition object, one of three classes with a `Contains` virtual at vtable slot 0 |
| `[4]` | `angle [deg]` | `(deg · π/180 ÷ 2)²`; `FLT_MAX` when unauthored, which skips the attitude test |
| `[5]`, `[6]` | `speed [min, max]` | mph × `0.44704`, so metres per second; `∓FLT_MAX` when unauthored |
| `[7]` | `auto` | the auto-land flag |
| `[8]` | `anim [name]` | the animation definition, resolved by `FUN_00523820` |

The shape node is **deactivated at parse time** (`FUN_004cca30(node, 0)`), so the marker never
renders in a mission; CSVM never builds it either, because a lone untextured triangle is what
`GameZ.IsMarkerGizmo` culls.

**The volume is that node's single authored triangle.** The parser requires the shape node's model
to carry exactly one polygon of exactly three vertices and reads the three as:

- **`cone`** (`PTR_FUN_00607b18`, 0x2c bytes): apex `v0`, base centre `v1`, axis `normalize(v1 − v0)`,
  base radius `|v2 − v1|`. `FUN_0045d5b0` tests a point `p` by `t = dot(axis, p − v1) / dot(axis, v0 − v1)`,
  requiring `0 ≤ t ≤ 1` and `|(p − v1) − (v0 − v1)·t| ≤ (1 − t)·R`. `t` runs 0 at the base disc to 1 at
  the apex, so the radius closes to nothing exactly at the target.
- **`half_cone`** (`PTR_FUN_00607b24`, 0x38 bytes): the same cone plus the half-space
  `dot(p − v1, normalize(perp)) ≥ 0`, where `perp` is the triangle's own component perpendicular to
  the axis. `FUN_0045d730` applies the half-space first.
- **`sphere`** (`PTR_FUN_00607b0c`, 0x14 bytes): centre `v0`, radius `|v1 − v0|`. `FUN_0045cf60`
  tests `|p − centre| < r` with the centre put through the shape node's world transform.

All 34 shipped rows across the eight chapters resolve: one node match each, one shape child each,
one `land_on` each. Six of C3's are `cone` (the `do_approachN` drop ring, 250.79 m deep and 271.76 m
at the base, a 47.3° half-angle, all six sharing model 490 at one point with 60° of yaw between
them), the two `hooked_to_klondike` rows are a 96 × 32 m `half_cone` and a 500 m `sphere`, and every
other chapter's rows are `half_cone` except its own auto row.

#### The per-frame test

`FUN_0045df60` runs one record and gates in this order:

1. the player's `+0x91d` cutscene flag is clear, and the landings slot `DAT_0071b1dc` is empty;
2. the record's `land_on` node is absent or active;
3. `[5] ≤ player+0x934 ≤ [6]`, the speed band, inclusive;
4. the attitude: `FUN_0053f610` turns the approach node's Euler rotation into a quaternion,
   `FUN_0053f9b0` multiplies it against the player's own at `player+0x150`, and `FUN_0053fca0` takes
   that relative quaternion's log map, whose magnitude is exactly half the rotation angle. Comparing
   its square against `[4]` is therefore the exact test **"the player's whole orientation is within
   `angle` degrees of the approach node's"**, roll included, not just heading;
5. the condition object's `Contains`, with the player's position in the shape node's live world
   frame.

A row that passes and carries `auto` sets `DAT_00719109` instead of starting anything; the next
frame `FUN_0045e120` turns that into the on-screen auto-land prompt. Every other row starts its
animation with `FUN_004edda0` and registers the mission-script host on it with `FUN_004ee160`.

##### The prompt's own wording

`FUN_0045e120` reads command `0x6a` (Auto-Dock, [`../../org/input.md`](../../org/input.md)) out of
the binding manager's typed slots in the manager's own order and formats the message id that slot
implies through `FUN_0059cd70`, the `LoadStringA` + `FormatMessageA` pair:

| Slot read | Name formatter | Message |
|---|---|---|
| keyboard A, then B (`FUN_005370d0`) | `GetKeyNameTextA`, modifier prefixes prepended | `0xb5` |
| joystick, bits 22-25 (`FUN_00537130`) | the 16-entry button-name table at `0075cb10` | `0xb5` |
| mouse, bits 26-27 (`FUN_00537150`) | the 4-entry button-name table at `0075cb50` | `0xb6` |

The joystick slot is read only while the input mode at `DAT_0064f6c0` is 2, and the mouse slot only
while `DAT_0071c2a0 & 2` is set. Nothing resolving means no prompt at all, not a keyless one.

Both ids live in the **message table**, not in `langui.dll`: `0xb5` is `MSG_PRESS_AUTOLAND`
("Press %1 to autodock") and `0xb6` is `MSG_CLICK_AUTOLAND` ("Click %1 to autodock"), so the second
wording is the **mouse** one rather than a pad one. The id-to-row mapping is
[`../missions.md`](../missions.md#message-table)'s.

##### The prompt's own placement

The prompt is a **text object of its own** at `0x00719110`, not a line of the speed and altitude
readout. `FUN_0045d8f0`, the landings rig's setup, gives it the `autoland` font
(`FUN_00530700("autoland")` into `FUN_005c7430` at `0x0045d9cc`-`0x0045d9da`; in
`fonts.zrd.json` that face is Arial, height -16, weight 700, **shadow false**, with no colour of
its own), sets the centring flag at the object's `+0x1044` to 1 (`0x0045d9f2`), writes both of its
gradient colours at `+0x104c` and `+0x1050` to `COLORREF 0x0040ffff` (`0x0045da03` and
`0x0045da08`), a pale yellow of R 255 G 255 B 64 that leaves the line flat rather than graded, and
hides the object (`FUN_005c54f0`, `0x0045da18`).

`FUN_0045e120` then places it once per offered frame. `FUN_00460a40` fills the viewport rect,
whose third and fourth ints are its width and height; both are **doubled** while the
high-resolution flag `FUN_00440bb0` (`*DAT_0064f748`) is nonzero (`0x0045e207`-`0x0045e219`). The
object's x at `+0x14` takes `width × 0.5` (`0x006032e0`, `0x0045e221`) and its y at `+0x18`
`height × 0.3` (`0x006034ac`, holding `0x3e99999a`, `0x0045e235`), both through `ftol`, so the
line centres on the middle of the screen three tenths of the way down. `FUN_005c54a0(-1.0)`
(`0x0045e24f`) shows it with **no lifetime**: a negative argument clears the object's hidden bit
without setting the expiry bit at `+0xc`, so the line stands until something hides it. That
something is the same routine: the approach test sets `DAT_00719109` per passing frame and
`FUN_0045e120` consumes it (`0x0045e136`), so the first frame the `auto` row stops passing takes
the `FUN_005c54f0` path at `0x0045e26b` and the line goes.

#### Arming is the mission script's job

The gate node is what a mission opens and closes. C3/M01 lists `disable_dropoff` in
`NEW_GAME_START`, which sets all six `do_approachN/land_on` `INACTIVE`, so the drop ring is dead
from mission load. `OBJECTIVE21`'s `WAKE_ANIM [enable_dropoff]` sets them `ACTIVE`, and
`OBJECTIVE22`'s `WAKE_ANIM [disable_dropoff]` closes them again once the drop has played. The
klondike hookup is armed the same way, by `OBJECTIVE14`'s `WAKE_ANIM [pzhomebase]`. **The approach
table is the mechanism; the objective script decides when each row is live.**

CM07 adds a pickup gate in front of that arming step, and it runs on the train's own clock. The
chapter's `train.zrd` definition `train_on_track` (a `NEW_GAME_START` anim) opens with
`CALL_ANIMATION [pickup_timing]` before it starts the consist's `tr_*.zan` track loops, so the
mission's `pickup_timing` definition runs from mission load in step with the train. Its authored
sequence toggles `copilot_pickup_switch`, `ladder_pickup_sensor` and `agent_approach_cone/land_on`
together through the loop (open at 12.36 s, 28.9 s, 61.36 s, 105.5 s, 216 s, 259.3 s and 291.73 s
of the run, closed between), which is the set of track phases where the pickup is flyable. Nothing
starts the timing off the player: `C1/M02/zrdr/pickups.zrd`, the compact table
`[[["ladder_pickup_sensor", 100.0]]]` (sensor node plus radius in metres), feeds only the rope
ladder's switch. Once `trigger_copilot` has staged the sensor and the passenger on the caboose,
the ordinary `landings.zrd` test owns the final approach and cutscene start.

The same sensor is the rope ladder's gate. The exe's own reader of `pickups.zrd`
(`FUN_00471830`) builds the sensor list the native ladder switch tests every frame, and nothing
else in the image reads that list; the switch, its attitude gate and the `CALLBACK 123` both
ladder definitions raise to settle it are decoded in [`../../org/ladderSwitch.md`](../../org/ladderSwitch.md).

The passenger's own choreography is what the switch selects. `caboosewave` (called by
`trigger_copilot`) parents `pickup_agent` and the switch under the caboose and runs its
`wave_or_drop` fork: switch active is `waveloop`, which calls `pickup_flare` (`ballflare.flt` added
under the passenger's `cp_lh` hand) and asserts the `flaretrail` puffer on that hand; switch
inactive is `hit_the_deck`, which polls the switch every 0.5 s and stands the passenger back up
through `get_up` into `waveloop`. All of these move the person through the `ROOT`/`ALL_NAMES` form
of `OBJECT_MOTION_SI_SCRIPT`, one record per body part
([`compiled-archives.md`](compiled-archives.md)). The pickup itself, `lookat_copilotpkup`, calls
`cabpkup_ladder`, `cabpkup_player` and then `caboosepickup` with `WAIT_FOR_COMPLETION`, and the
person's climb is that last definition's one sequence, so the hold spans the whole climb.

#### The hookup poses the flown airframe's own parts

A zeppelin hookup is one definition for eleven aeroplanes, and everything that differs between them
is authored as a branch on which `player_<airframe>` node is ACTIVE. C3's `hooked_to_klondike`
(`player` root, started by both `pz_manual_land` and `pz_auto_land`) is the worked example:

| what the shot shows | where it is authored |
|---|---|
| the docking hook coming out | `CALL_ANIMATION [player_extend_hook]` in the opening sequence |
| how high the aeroplane hangs | the `OBJECT_TRANSLATE_STATE` inside that def's own airframe branch |
| a Balmoral folding its wings | `IF NODE_ACTIVE[8]` → `CALL_ANIMATION [bal_wing_foldup]` in `move_player` |

`player_extend_hook` is a single `test_player` sequence of eleven `IF NODE_ACTIVE[n]` arms over its
own eleven-name node list, each arm setting an absolute `OBJECT_TRANSLATE_STATE` on that airframe's
node and then calling that airframe's hook definition before stopping the sequence:

| airframe | mount offset | hook def |
|---|---|---|
| `player_balmoral` | `(0, −2.172, −0.4)` | `bal_hook_extend` |
| `player_autogyro` | `(0, −0.948, −0.305)` | `gyro_hook_extend` |
| `player_avenger` | `(0, −0.07, 0.5)` | `avenger_hook_extend` |
| `player_bhawk` | `(0, 0, 0.8)` | `blood_hook_extend` |
| `player_brigand` | `(0, −0.105, −0.37)` | `brig_hook_extend` |
| `player_fbrand` | `(0, −0.4, 1.0)` | `fire_hook_extend` |
| `player_fury` | `(0, −0.105, −0.37)` | `fury_hook_extend` |
| `player_kestrel` | `(0, 0.27, 0.5)` | `kest_hook_extend` |
| `player_peacemaker` | `(0, −0.377, −0.6)` | `peace_hook_extend` |
| `player_pfighter` | `(0, 0, 0.5)` | `pirate_hook_extend` |
| `player_warhawk` | `(0, −1.5, 0.2)` | `war_hook_extend` |

The offset is the airframe's local transform inside the `player` wrapper, and `player_retract_hook`
is the same eleven arms writing `(0, 0, 0)` back. **This is the height the aeroplane hangs at on
the trapeze**: `player` is reparented under `pzhookpoint` and flown there by an SI script, and the
airframe's own offset is the last term of that composition. It is authored, not a scale.

Each `<x>_hook_extend` is rooted on that airframe's `<x>_hook` group node, which the shared archive
ships INACTIVE with its arms and door already modelled; the definition's first sequence activates
the group and the rest tweens the arms out. The wing fold is the same shape: `bal_wing_foldup` is
rooted on `player_balmoral` and turns `rwingbend` and `lwingbend` ±1.9198622 rad over two seconds.
`move_player` gives the Balmoral and Warhawk branches their own two-second wait and then `all_done`,
so those two airframes end the episode sooner than the rest.

**One swing per docking is a property of the merge, not of a runtime guard.** The fork and each of
its branches exist exactly once in a loaded mission's program, so the single `CALL_ANIMATION` in
`hooked_to_klondike` starts one fork and that fork's arm starts one hook. `AnimProgram` loads the
compiled archives first and then drops any later definition repeating a loaded one's (`NAME`,
`ANIMATION_NAME`) pair, whatever scope it came from, which is what makes the shared `player_hook.zrd`
copy of `player_extend_hook` and the `-1` twins of `blood_hook_extend`, `brig_hook_extend` and
`brig_hook_startup` cost nothing. The census line naming `(mission-scope + NAME1)` describes the two
gates that run BEFORE that drop rather than the drop itself: a mission-scope reader definition is
skipped unless the mission's compiled `mis_anim` lists its pair, and a shared- or chapter-scope
`NAME1` multi-target definition is skipped because the compiler expands it per instance. A
plain-`NAME` shared definition passes both gates and is then deduplicated against its compiled twin,
so it cannot become a second instance. `landings-hookup-airframe` reads the loaded count for the fork
and for the flown airframe's branch, and counts both over a driven docking.

**Which aeroplane is the flown one is a question the original never asks and CSVM has to.** The
original has one player vehicle and names it; CSVM has a field of humans, and the node table the
arms resolve `player_<airframe>` through carries one airframe at a time, the episode owner's, put
there as the episode takes the session and taking the previous one back out. Without that a
co-op landing swings the hook on whichever seat was staged when the rigs were bound, which is
seat 0's. `campaign-coop-hookup-seat` flies two humans in the same airframe and reads the table's
own answer beside both hooks.

**No `<x>_hook_startup` runs in a player docking.** Four airframes author one (`bal`, `gyro`, `brig`,
`war`), every one of them `ON_CALL`, and the only definitions that call them are C4/M04's
black-market hookup and its AI airframe states (`bhmhookup.zrd`, `bhm_warhawks.zrd`,
`anim2_*-ai_*_state`, `player-bm_unhook_player`), which pose a hook on an AI aeroplane. What parks a
player's hook is the aircraft archive's own inactive bit on the `<x>_hook` group, and all eleven
airframes ship that bit clear, so the seven authoring no startup are parked exactly like the four
that do.

⚠ **What parks a hook's ARMS is `<x>_hook_retract`'s `RESET_STATE`, where it is complete and right
(neither always holds).** Each extend definition activates its group and arm nodes in its `state`
sequence at t=0 while its `control` sequence starts the arms' own motions a moment later, and each
motion's `from` is a collapsed pose (`pirate_hook_extend` takes `l_arm3` and `r_arm3` from
`(1, 0, 0)`, `bal_hook_extend` takes `l_arm1`/`r_arm1` from `(1, 1, 0.5)` scale and `m_arm1`/`m_arm2`
from a rotation of `0`). Nothing writes a pose before that motion fires: the sequence stepper holds
its cursor on a start-gated event and calls no handler until the gate opens, the
`OBJECT_MOTION_FROM_TO` handler writes `from` on its first dispatched tick, and `OBJECT_ACTIVE_STATE`
writes no pose at all ([`../anim-definitions.md`](../anim-definitions.md), "A `from` pose is written
on the event's first dispatched tick"). A node therefore holds whatever posed it last, which is
usually the matching retract's `RESET_STATE`, bootstrap pass 1 applying it to every loaded
definition: `pirate_hook_retract` parks `l_arm3`/`r_arm3` at rotation `(0, 0, 0)` and scale
`(1, 0, 0)`, the pose its extend starts from, and switches the group, both doors and both arms off.

**Five of eleven airframes' `RESET_STATE` does not cover every node its own extend swings.**
`bal_hook_retract`/`war_hook_retract` author no `ObjectScaleState` at all, so `l_arm1`/`r_arm1` sit
at the archive's own `(1, 1, 1)` against a `from` of `(1, 1, 0.5)`, and `m_arm1`/`m_arm2` are never
reset at all. `brig_hook_retract`/`fury_hook_retract`/`peace_hook_retract` park `l_arm1`/`r_arm1` on
the wrong axis (their own scale reset moves `y`, the extend definition's own `from` moves `z`).
Every one of these is a full-length or wrongly-posed node held for the opening second, exactly the
shape `OriginalScreenshots/Videos/CM04.mkv`'s Balmoral docking shows: over its opening shot the
centre mast draws at archive length for a frame, vanishes, and only then grows and swings.
`AnimRuntime.ParkDockingHook` closes the gap by seeding every node its group's own `<x>_hook_extend`
moves from that definition's own first FROM pose, which wins wherever a `RESET_STATE` omits a node
or disagrees with it, never by editing the authored `RESET_STATE` data itself. The seed reaches
each node exactly the way the dispatch that later moves the same node does, through that
definition's compiled symbol table: the table's `l_arm1`/`r_arm1` claims for these three bind at the
park's own staging point, so the seed needs no plain-name walk and cannot take a same-named arm off
another group. ⚠ The wrong-axis park and the arms' nesting are separate facts about these three,
not one: `brig_hook`/`fury_hook`/`peace_hook` carry an extra `l_arm`/`r_arm` level and their own
`l_door`/`r_door`, where `bal_hook`/`war_hook` hang `l_arm2`/`r_arm2` straight off the group.

**All three therefore need the flown aeroplane's own subtree in the animation runtime's node
table.** CSVM indexes it there when the flight rigs are built, and again after an airframe swap,
rebased onto the chapter's cross-archive base the same way the staged intro aircraft are
(`AircraftStage.StageFlown`); the docking-hook group is built for a human rig and parked at its
archive-authored inactive bit (`Mech3/PlaneBuilder.cs`). ⚠ That rebased index deliberately runs no
general `RESET_STATE` pass, since a chapter definition anchoring on a generic airframe node name must
not re-pose a live aeroplane, so `StageFlown` applies the two scoped passes the arms need through
`AnimRuntime.ParkDockingHook`. ⚠ The airframe node's own visibility is that ACTIVE bit, so the flight
rig writes its presence one node higher, on the shake pivot: an aircraft a cutscene holds `Inert`
while posing it must not read as "no airframe at all".

⚠ **A rotate-only and a scale-only `FROM_TO` on the same node in the same tick is not two channels
racing.** `bal_hook_extend`'s `control` sequence calls `move_left_arm1` (rotate) immediately
followed by `scale_left_arm1` (scale), both on `l_arm1`, dispatched on the same `Advance` since a
`CALL_SEQUENCE` a higher-indexed sequence slot answers runs within the same walk
(`docs/org/sequences.md`). `MotionSet`'s one-motion-per-`(Target, Channel)` rule would otherwise
evict the rotate tween before it ever ticks, leaving the arm's rotation frozen at whatever it held
going in while its scale animates normally and reaches its authored end. A `.Scale` readback taken
only at the end therefore reads correct; only the rotation shows the defect. `FromToMotion.Create`
carries the evicted motion's own channel and run time into its replacement instead of losing it.

#### Limits and readings

- **Undecoded: whether the host reaches a called definition.** `FUN_004ee160` writes the host on the
  animation instance the trigger starts, and it is the only writer install-wide. The `do_approachN`
  definitions author no `CALLBACK` at all: they set `ObjectRotateState do_direction` and then
  `CallAnimation texdrop`, and it is `player-texdrop` that raises 11, 951, 2 and 1 plus the
  `letterbox` call. No propagation of `anim+0x74` from a caller to a callee was found. CSVM takes
  the **reading** that the host answers for a started row's definition *and* its `CALL_ANIMATION`
  closure, because the drop is a letterboxed movie that has to hide the chrome and take the player
  out of flight, and no other mechanism does that.
- **The slot holds the row's definition, and the player's flags are the codes'.** C1C/M01's
  `wv_initiate_hookup` is the one shipped row whose definition authors no `CALLBACK`: it calls
  `wv_hookup_player` (2 and 11 at t=0, a 7.3 s SI script on `player`), then the drop, the hook
  state and `wv_unhook_player` with a trailing `WAIT_FOR_COMPLETION`, and that callee raises 1 and
  951 after its own SI script, 2.6 s later. A trailing wait holds no runner open
  ([`org/sequences.md`](../../org/sequences.md)), so the row's instance ends before the unhook
  does. In the original nothing about that matters: `+0x91d` is set by 11 and cleared by 1, and
  no definition ending touches it. CSVM's episode therefore belongs to the started row
  (`CutsceneController.Own`, written by the trigger before the start) and its end-of-definition
  handoff waits until every code-authoring definition in the row's call closure has ended as
  well; booking the episode to the first raiser handed the player flight at 7.3 s with the
  aeroplane still hung, and the unhook's 951 then re-placed them when it ran out.
  The slot is not the landings table's alone. `DAT_0071b1dc` holds whatever instance the trigger
  started, and the mission-script host is installed the same way whichever trigger started it, so
  CSVM writes the slot from `AnimRuntime.PlayMissionTrigger` itself (the approach rows, the
  objective script's `WAKE_ANIM`, the ladder switch) and from the world build for the opening
  cutscene, which starts inside the start-list walk instead. C5/M02's ending is the shipped
  objective-path case: `nypd_southward` raises no code and calls `nypd_player`, which raises 11, 2
  and 13. No shipped `WAKE_ANIM` target reaches more than one code-authoring definition, so the
  multi-raiser shape CM06 shows exists on the landings path alone.
  A definition whose closure authors no `CALLBACK` never claims the slot: an ordinary `WAKE_ANIM`
  goes through the same call, and the original's slot only ever holds a cutscene.
- **Undecoded: when the landings slot clears.** `DAT_0071b1dc` holds the running instance and is
  cleared only by `SceneAnimCallback_0045e0f0` seeing callback **0**, which nothing authors. Read
  literally, one landings cutscene per mission load would lock out the rest, which C3/M01 (drop,
  then hookup) contradicts. CSVM instead re-arms as soon as the started definition ends, which is
  what the mission needs.
- **A reading: the volume's frame is static.** The original re-reads the shape node's world pose
  every frame. Nothing in the install animates a `cone`/`half_cone`/`sphere` marker or its approach
  node, so CSVM folds the shape's local chain into the approach node once at load and follows one
  node.
- **A divergence: CSVM arms the table only in a story mission.** The original's mission load
  (`FUN_00464680`) arms it for every mission type, and the anim-not-found rejection is what keeps
  most Instant Action missions out. It does not keep all of them out: C3/IA1 carries
  `hooked_to_klondike` and C3 ships `pz_manual_land/land_on` active, so an Instant Action sortie
  there would take a docking cutscene. Whether the original means that is undecoded, so CSVM scopes
  by session (`WorldSession.Options.LandingTriggers`).

### The intro defs run without a host

Intro definitions are bootstrapped from `StartAnims.zrd`, whose two sections are `NEW_GAME_START`
and `LOAD_GAME_START` (C1/M04 lists `mission_intro_animation` under `NEW_GAME_START` only, so the
intro plays on a fresh start of the mission and not on a load). The loader is `FUN_0046c370`
(`mission.cpp`), and it starts each named def with `FUN_004edda0(def, 0, 0, 0, 0)` and **no**
`FUN_004ee160` call, which is the only writer of the host slot at `anim+0x74`. The start itself is
not where a start-list definition gets a host, though it is not the only chance one has: see the
warning below on `FUN_004735b0`'s own walk.

The engine does the equivalent imperatively instead. `FUN_004654e0`, the new-mission start,
runs `FUN_0041f250` / `FUN_004a95f0` / `FUN_004516e0(0)` / `FUN_00453660(0)`, which is code 913's
body verbatim, then `FUN_00455800(0)` (chrome off) and sets the player's `+0x91d` cutscene flag,
and only then calls `FUN_0046c370(StartAnims.zrd, NEW_GAME_START)`. The intro's authored
notifications restate a state the engine has already entered.

**That park belongs to the mission start, not to the intro, and not to a mission type.** The other
`StartAnims` section is reached the same way: `FUN_00464680`, the mission-data load, runs the same
four calls in the same order, and the savegame path `FUN_0046c140` calls it (through
`FUN_00465370`) before dispatching `FUN_0046c370(StartAnims.zrd, LOAD_GAME_START)`. Those two are
the only sites in the exe that dispatch a `StartAnims` section, and in each the park and the
dispatch sit in straight-line code with no branch between them. Neither reads the mission type.
The type is the mission object's `+0x700`, written by the constructor `FUN_004636a0` and by the
savegame restore and read by `FUN_00463a50` to pick the mission directory (1 gives `m01` to `m05`,
2 gives `mp1` to `mp5`, 3 gives `ia1`) and by the predicates `FUN_004639a0`, `FUN_004639c0` and
`FUN_004639b0`. An Instant Action sortie sets it to 3 in `FUN_004174d0` and a campaign mission sets
it to 1 in `FUN_00417090`, and both then reach `FUN_00416f40` and `FUN_004654e0`. So every mission
of every type opens in the same state, with the AI parked, the chrome off and the player's `+0x91d`
cutscene flag set, before its own start list runs, and an Instant Action bootstrap is parked exactly
as a story intro's is. The roster is standing at that moment rather than empty: `FUN_004735b0`
builds the mission's vehicles through `FUN_0047c210` earlier in the same function.

**What lifts it is the bootstrap definition's own reset, and nothing else.** The unpark is
`FUN_0041f2e0`: it walks the same vehicle list under the same three-way filter the park uses
(`+0x91d` clear, not the player at `DAT_0071c298`, `+0xf8` clear), clears the hold flag `+0x354`,
sets the next-think time `+0x300` to the mission clock plus a randomised 0 to 2 s, and reactivates
the scene node through `FUN_004cca30(node, 1)`. The park at `FUN_0041f250` is its exact inverse,
`+0x354` to 1 and `+0x300` to the clock plus 3e10. **It is keyed on a script code, never on a timer
and never on an episode ending**: the three call sites of `FUN_0041f2e0` are the mission-script
host's code 914 (`0047e510`) and two arms of the console command table (`0043e4e1` and `0043ecd0`,
each paired with a park arm on the neighbouring command). The only other writer of `+0x354` is the
vehicle constructor `FUN_004aff80`, and the per-tick vehicle walk `FUN_004897c0` reads it at
`004899e0` as the gate on whether a vehicle is simulated at all.

So the lift comes from a definition raising 914, and what a mission opens on decides when that
lands. A story mission's intro raises it in its own `RESET_STATE`, at the film's end. A mission
opening without a movie bootstraps `camera1-player_setup`, whose `callback_sequence` restates the
park with 913 and whose `RESET_STATE` raises 914 with `RESET_TIME [0]` and no timed event in
between, so that mission's park is entered and left inside its own start. Either way the park and
the lift belong to the start of a mission of any type, and neither is a story intro's to own.

⚠ **A start-list definition is not hostless by construction.** `FUN_0046c370` installs no host of
its own, but `FUN_004735b0` has already walked the animation-definition table with `FUN_00523920`
and called `FUN_004ee160(def, FUN_0047e080, 0)` on every entry it yields (`00474a96`), earlier in
the same `FUN_004654e0`. Which entries that walk yields is gated on the definition record's `+0x9c`
bit `0x10` and on its `+0xa0` type byte not being 5, and neither is decoded, so whether a given
start-list definition's codes reach the host is an open question. What does not depend on it is the
park and the lift above, which the engine and the bootstrap definition assert between them.

For a remake this matters in one direction only: **the codes are still the authoritative
description of the cutscene's shape** (what is hidden, when the simulation stops, when control
returns), which is what a cutscene player has to reproduce. They are not a set of messages that
must be delivered to an existing listener.

The same path reaches a definition the start list never names. C3/M03's `NEW_GAME_START` lists
`calldestroy_the_cargozep` (root `cargozep1`, no callbacks of its own), whose one sequence calls
`cgzep_camera` at once, `movebridge1`/`movebridge2`, and `destroy_the_cargozep` 0.5 s later.
`player-cgzep_camera` (root `player`, objects `player`, `cockpit1`, `camera1`) is the mission's
opening movie: it calls `letterbox`, deactivates `player` and `cockpit1`, raises 20, 11, 14, 913
and 2, and flies `camera1` on five SI scripts (`campath1`..`campath5`, absolute world keyframes
beside `cargozep1`, no reparent) under `snd_IntrosceneHAch4`; its `RESET_STATE` restores both
nodes and raises 1, 10 and 914. Nothing in `FUN_0046c370`'s start registers a host on the called
instance either, so the reading above covers it unchanged. CSVM hosts it by name beside the two
intros (`CutsceneController.IntroAnims`), and reads the start list's call closure rather than the
list when deciding to stand up `camera1`, the bars and the `player` marker
(`WorldSession.BootstrapsCutscene`). ⚠ The zeppelin's destruction is that start anim's own later
call and plays under the camera from it; the host issues no second call.

## `CALLBACK` code reference

The mission-script host `FUN_0047e080`, installed on EVERY animation definition the world build
walks (`FUN_004735b0` at `0x00474a96` hands `FUN_004ee160` this address for each in turn), so a
definition that is no cutscene at all still reaches it. Counts are occurrences in the loose `zrdr`
readers of this install; the compiled archives carry the same events.

| code | count | what the host does |
|---|---|---|
| 0 | 0 | clears the active-cutscene slot if this animation owns it. Never authored. |
| 1 | 20 | `FUN_0042e5c0`: `FUN_00493e40` clears the player's `+0x91d`/`+0x91e`/`+0x91f` cutscene flags and restores the view target, then `FUN_00455800(1)` puts the cockpit and HUD chrome back. **The end-of-cutscene handoff to gameplay.** |
| 2 | 22 | `FUN_0042e5a0`: clears the view target (`FUN_0042c250(0)`) and `FUN_00455800(0)` hides the cockpit and HUD chrome. **The cutscene presentation, on.** |
| 3 | 2 | The case at `0x0047e0ec`. `FUN_0042c280(DAT_0064ef78, 0)` selects camera mode 0, the chase view, and returns at once if the selection is already there; the case then clears the head-look angles `DAT_0064ef60` (elevation) and `DAT_0064ef64` (azimuth) whatever the selector did ([cameraViews.md](../../org/cameraViews.md), "Head-look controller"). Both occurrences are in the player aeroplane's own `player-player`, at the head of `destroy` and of `random_destroy`, so **this is what takes a pilot out of the cockpit as their own aeroplane comes apart** and, since `+0x14c` is the SELECTION, what makes them respawn in the chase view. CSVM answers it through `AnimRuntime.ResetPilotView`, bound by the rig that plays the def to `CameraController.ResetToChase` (the `callback-events` suite). The crash camera and the respawn's head re-centre were already native (`FlightController.CutToCrashView`, `CameraController.Snap`); the selection change is what the code adds. |
| 10 | 5 | restores the in-flight systems as a block: engine audio (`FUN_004a0af0`/`FUN_004a0a30`), `FUN_00455800(1)` chrome on, `FUN_00494b20`, `FUN_00443d60(1)`, `FUN_004696f0`, and `FUN_004b24d0` on the player. |
| 11 | 21 | `FUN_004b1510` releases the vehicle's four sound handles, sets the player's `+0x91d` and `+0x91e` cutscene flags, `FUN_00455800(0)` chrome off. **Takes the player out of flight.** |
| 12 | 4 | The case at `0x0047e1ed`, gated on the player vehicle existing with its out-of-flight byte `+0x91d` SET and `+0x91f` clear, so it does nothing while the pilot is flying. In multiplayer (`FUN_00440ad0`) it clears the active-cutscene slot if this animation owns it and hands control back through `FUN_00470a10`. In single player it silences the vehicle's sound handle at `+0x3a0`, then reads `DAT_0071bb68` for a crash animation to start: **that global is read at `0x0047e263` and written nowhere in the binary**, so `FUN_00523820` is never reached, `DAT_0071bb6c` cannot hold an instance, and the case falls straight through to `FUN_00443090`, the mission-end path ([objectives.md](../objectives.md)). All four occurrences are `RESET_STATE` events, of `player-player` and of the three `player_crash_*` defs. **Declined by design** in CSVM: a `RESET_STATE` raises no callbacks in this runtime, and the player's death ending and crash choreography are both native (`AircraftLifecycle`, `CampaignDirector`'s death wiring), so the code has no work left to do. |
| 13 | 3 | The case at `0x0047e2ba`: `FUN_00494b20` (the player back in flight, the three cutscene/death flags cleared), `DAT_0071d290 = 1` (one frame of the presenter park), `FUN_00463c10(1)`, the same call the objectives runtime makes when a primary objective completes, then the mission-end path `FUN_00443090` (decoded in full in [objectives.md](../objectives.md), "The mission-end path"). **The mission-completion code.** ⚠ The player IS put back in flight here, but the frame that would show it is rendered and never presented, so the fade runs over the film's own last frame; objectives.md walks the park. The three reader sources are `hooked_to_klondike` (the shared docking, compiled into twenty missions), C4/M01's `carpkup_player` and C5/M02's `nypd_player`, and no objective in the shipped data completes on a landing or on either drop, so this code is the only ending those missions have. **It takes no wrap-up:** the case sets the won flag and calls `FUN_00443090` in the same breath, never touching the wrap-up timer at mission `+0xc40` that the objective endings run down (0.1 s for `INSTANTWIN`, 3 s otherwise), so the ending lands on the frame the film raises it and the world stops there, under the mission-end path's own 2 s fade. CSVM hosts it into `ObjectiveGraph.NotifyDockingComplete`, and the hold after it is `CampaignDirector.LeavingHoldS`. |
| 14 | 6 | **not handled.** Falls through the host's switch. See the gap note below. |
| 15, 16 | 12, 12 | not this host's: the vehicle-death handler `LAB_00480710` takes these ([`org/vehicleDamage.md`](../../org/vehicleDamage.md), [`org/objectMotion.md`](../../org/objectMotion.md)). |
| 20 | 4 | stores this animation in `DAT_0071c50c`, the active-cutscene slot. **Consequence: the per-frame world update `FUN_004897c0` and the objectives update `FUN_0046a490` both return immediately while the slot is set, so the simulation is suspended for the duration.** |
| 86 | 1 | The case at `0x0047e336`. `FUN_005aef00` walks all `DAT_00a1d788` weapon records at `DAT_00a1d78c` (stride `0x214`, the records [ordnanceTypes.md](../../org/ordnanceTypes.md) decodes) and empties each one's live-round list at `+0x88` through `FUN_005aee40`: per round, the two animation instances it holds are force-stopped, its scene node is detached from the world and the round goes back on the free list, with **no detonation, no impact effect and no damage**. The one exemption, `+0x74 & 2`, is a bit the `.zrd` dispatcher `FUN_005ad630` sets for no key, so no weapon in this install is exempt. The teardown `FUN_005ae690(0)` calls the same function, which is what it is: clearing the air of ordnance. The one occurrence is C1/M02's `hangar_3-hangar_drop`, **the same definition that raises 965**, so the drop discards what the outgoing aeroplane fired. CSVM answers it from `CutsceneController.ClearOrdnance` into `ProjectilePool.Clear` (the `cutscene-clear-ordnance` suite). One divergence: that clear also drops the muzzle, impact and smoke sprite lists, which the original keeps in separate systems this code does not reach. |
| 123 | 8 | **not handled.** |
| 666, 667 | 2, 3 | `DAT_00621378` gates automatic application of the camera-parameter profile when the view mode changes (see [camparam.md](../camparam.md); the applier is `FUN_00472ea0`). 666 clears the gate so a cutscene's own camera work is not overwritten; 667 restores it and resets the view mode to 0. |
| 701–704 | 1, 1, 0, 0 | ⚠ **Not a camera-parameter set; an earlier reading of this row said so and was wrong.** The case at `0x0047e35a` takes the whole range `0x2bd`–`0x2c0` and calls `FUN_0049a210(code − 700)`, which reaches the **multiplayer flag list**: `FUN_0049a1e0` finds the object carrying that id in the linked list at `DAT_0071c794` (built by `FUN_00494f40`, torn down with the rest of the network session's lists in `FUN_004966c0`), zeroes its `+0x44`, and `FUN_0049a240` broadcasts the whole list as network message `0x1d` through `FUN_005b2640`, the same message id `FUN_004966c0` registers `FUN_0049a300` as the receiver for. The list's own per-frame walk `FUN_00499e50` is a pickup-and-drop test at 625 m² against each entry. The whole case sits behind `FUN_005b4210`, false unless a network session object exists at `DAT_009c7860` and this machine is on its thread, **so the case does nothing at all in single player**. The two authored occurrences are `player-flg_throw1` and `player-flg_throw2`, in the `MP2` mission of every chapter. **Declined by design**: CSVM hosts no multiplayer session, and the original's own case is inert without one. |
| 800–803 | 1 each | Colorado-specific hooks. **800 credits a launch budget:** it looks the generator `cargozep1` up by name and adds 5 to its remaining capacity (`+0x80`), the only credit C4/M03's generator ever receives, so the five freed-crew Furies launch during the beauty shot that raises it (`cg_beauty_shot`, called by the docking film `cg_hookup_player`; [enemy-generators.md](../mission-entities/enemy-generators.md), "Capacity rule and limit"). 801 to 803 reactivate the first still-deactivated `bhatwarhawk_1..6`, `bhatbrigand_n` and `bhatgyro_1..3` (`FUN_004b0f40(0)`, the primitive `WAKEUP_ENEMIES` uses); authored in C4's `bhm_warhawks`. CSVM answers 800 from `Session/Roster/AiGeneratorRuntime.cs`'s place in the `CALLBACK` host chain (five launches on `cargozep1`, the `generator-callback-credit` suite) and 801 to 803 from `Session/Campaign/CampaignDirector.cs`'s own link ahead of it, which is the whole of what puts a CM19 Black Hat into the air, since no objective names one in `WAKEUP_ENEMIES` (the `campaign-launch-hook` suite). |
| 913 | 7 | `FUN_0041f250` parks every AI vehicle that is not the player and not itself in a cutscene: sets its hold flag, pushes its next-think time far out, and deactivates its scene node. Plus `FUN_004a95f0` (detaches the wave director's node update), `FUN_004516e0(0)` and `FUN_00453660(0)`. **Clears the world of AI aircraft for the duration of the movie.** |
| 914 | 8 | the exact inverse (`FUN_0041f2e0`, `FUN_004a9610`, `FUN_004516e0(1)`, `FUN_00453660(1)`), clearing the hold flag `+0x354`, setting the next-think time `+0x300` to the clock plus a randomised 0 to 2 s and reactivating each AI vehicle's scene node. Skipped in multiplayer. **It is also what lifts the engine's own mission-start park**, from whichever definition the mission bootstraps (above, "The intro defs run without a host"); the only other callers of `FUN_0041f2e0` are two arms of the console command table. |
| 950, 951 | 14 for 951 | 951 puts the player's aeroplane on its own `player` node's world pose and rebuilds its motion state from there. **The re-placement.** Decoded in full [below](#the-re-placement-code-951). 950 (the case at `0x0047e53e`) reads the raising definition's own root node (`FUN_00523990`) and hands it to `FUN_00422a70`, which **blocks a named AI-net edge**: `FUN_004228f0` builds the table at `DAT_0064ee54` of (scene node, net id, edge index) triples for every net edge whose name `+0x08` resolves to a gamez node, this case finds the entries whose node is the raising def's, sets that edge's blocked byte (edge `+0x20`, stride `0x24` off net `+0x5c`) and re-runs the net's goal routing `FUN_00431b70`, whose Dijkstra walk skips any edge carrying that byte. **Nothing in this install authors 950**, and no shipped net carries the GOAL nodes the re-solve exists to fill ([aiPilot.md](../../org/aiPilot.md)), so both halves are dead here. Declined by design. |
| 965, 966, 967 | 1, 1, 3 | swap the player onto a specific airframe (`pbloodhawk`/`player_bhawk`, `pwarhawk`/`player_warhawk`, `pbalmoral`/`player_balmoral`) with its armour and hardpoint table, and set the cutscene flags. The data-side counterpart of the intro defs' `check_balmoral`/`check_warhawk` branches. Decoded in full [below](#the-airframe-swap-codes-965-966-and-967). |
| 968 | 1 | The case at `0x0047f133`: `FUN_004aff10` looks the vehicle `bswingman_1` up by name and, if it is there with its out-of-flight byte `+0x91d` clear, `FUN_004b0f40(1)` hides it, setting the hidden bit `+0x945` and deactivating its scene node. The one occurrence is C4/M03's `player-cg_hookup_player`, two events into the docking film and **one event ahead of its own `snd_RM3SwanDown`**: `bswingman_1` is Swan (`MSG_BSWAN_NAME`), the wingman that mission's `aiv.zrd` seats on the player at 216 m, and the code is what takes her out of the world as the line plays. CSVM answers it from `Session/Campaign/CampaignDirector.cs`'s link in the host chain, beside 801 to 803, as the deactivate rather than the cutscene park (the `campaign-wingman-removal` suite). |

**The two gaps, named.** Codes **14** (6 occurrences, four of them in the intro defs) and **123**
(8 occurrences) reach no case in `FUN_0047e080`; its switch covers 0, 1, 2, 3, 10, 11, 12, 13 and
20 below `0x15`, and 4–9 and 14–19 fall through. No other host that can be installed on a cutscene
animation takes them either, since the only host the cutscene trigger installs is this one. They
are recorded as gaps, not guessed at: nothing in the exe tells us what 14 or 123 were meant to do.

**Where each code lives in CSVM.** 1, 2, 10, 11, 13, 20, 86, 666, 667, 913, 914 and 951 are
`CutsceneController`'s, which also carries the engine half of 913 and 914 as
`ParkAtMissionStart`, the park a mission of any type enters at its start and leaves at its
bootstrap definition's reset (the `cutscene-ai-park-mission-start` suite over an Instant Action
wave); 3 is `AnimRuntime.ResetPilotView`, reached from the crash rig rather than
the cutscene host because the definition that raises it is no cutscene; 800 is
`AiGeneratorRuntime`'s and 801 to 803 and 968 are `CampaignDirector`'s; 965 to 967 are
`AirframeSwap`'s; 15 and 16 are the runtime's own vehicle-death seams. Four are **declined by
design**, each for a mechanism reason rather than a missing seam: **12** because a `RESET_STATE`
raises nothing here and the player's ending is native anyway, **701 to 704** because they are the
multiplayer flag list and the original's own case is inert outside a network session, **950**
because no definition authors it and no net carries the goal nodes its re-solve fills, and
**0** because it is the free arm nothing authors and acting on it would delete a live wreck. 14 and
123 are the two named gaps above, answered as gaps so a census can tell "authored and ignored" from
"never authored".

### The airframe swap codes 965, 966 and 967

The three codes are the only mechanism in the shipped data that changes what the player is flying
without ending the mission. Their five occurrences are `C1/M02`'s `hangar_3-hangar_drop` (965),
`C4/M04`'s `player-bm_unhook_player` (966) and the three `britbalmoral_<n>-ww_balmoral<n>` capture
definitions in `C3/M05` (967), where the code is the last event of the sequence that flies the wing
walk.

Each case does the same five things, in this order.

1. **Measure the outgoing airframe.** `FUN_0047bcd0(name, &armour, &structure)` is a `__thiscall`
   on the player vehicle (`DAT_0071c298`) that reads one hull section's CURRENT pair off the
   section array (at `veh+0x9c`, stride `0x58`, name at `+4`, armour max/current at `+0x24`/`+0x28`
   and structure max/current at `+0x2c`/`+0x30`), answering −1/−1 for a section the airframe has
   none of. The four sections are `nose`, `tail`, `leftwing` and `rightwing` (`0x00628640` in the
   967 case), and the two sums are kept for step 5. 965 skips this step.
2. **Rebuild the vehicle.** `FUN_0047fd50(<def>, <planes.zbd node>)` returns immediately if the
   player is already in that def. Otherwise it saves the attitude quaternion (`+0x54`), the velocity
   (`+0x81`) and two further fields, tears the old vehicle body down (`FUN_0047bab0`), builds the
   new one from the plane record (`FUN_0047c210`), **walks the global target list and re-points
   every `TargetVehicle` that pointed at the player onto the new object** (both the `+0x948` and the
   `+0x2fc` slot), re-registers the collision and landing sound handles, restores the saved motion
   state and re-applies the camera-parameter profile. It clears `+0x91d`/`+0x91e`/`+0x91f` on the
   way through, which is why step 4 re-asserts them.
3. **Write the new airframe's weapon table.** The twelve ints at `DAT_0062ae28` are the player's
   weapon table, four gun slots then eight hardpoints, `−1` for a slot the airframe has none of.
   They are `wep_NN` **ids**, not counts: `FUN_004b2550` renders the value's two digits into the
   template `wep_??` at `0x0062ae5c`, resolves that name, and derives the round count from the
   resolved weapon through `FUN_004bad90` whenever the caller passes `−1` for it, which all three
   cases do. The Bloodhawk case (`0x0047e7d4`) writes 40, 30, −1, −1 and six into two hardpoints;
   the Warhawk (`0x0047ea55`) 70, 50, −1, −1 and six into all eight; the Balmoral (`0x0047ed00`)
   50, 50, 30, 30 and six into all eight. In the `wep_30`–`wep_73` gun matrix the low digit is the
   ammunition (`0` slug, `1` dumdum, `2` armour-piercing, `3` magnesium), so every case hands over
   **slug guns and `wep_06` high-explosive rockets**, each airframe's own stock fit, and the
   sortie's Ammo Selection picks reach none of it. The four bytes at `DAT_0062ae58` ride the four
   gun slots. `FUN_004b24d0` pushes the table onto the player and `FUN_004b2350`
   re-picks the selected gun and the selected hardpoint as the first slot in each half carrying a
   weapon, so the readouts follow. `FUN_0047bd90(name, armour, −1)` then sets each of the
   four hull sections to that airframe's own armour, max and current together: 20 across for the
   Bloodhawk, 30 across for the Warhawk, 40/35/25/25 for the Balmoral, which is the same row CSVM's
   own stat table carries for `player_balmoral`. Last, `FUN_00449140(<airframe id>)` reads a scalar
   off the stat row (id 0xb, 0x23, 0x29) into `player+0x66c`.
4. **Set the cutscene flags.** `player+0x91d = 1` and `player+0x91e = 1`, then `FUN_0042e5a0()`.
   Those are exactly the state **code 11** and **code 2** assert between them: the player out of
   flight, and the chrome and view target off. Nothing in the case ends that state; the definition
   ending does, through the ordinary handoff. 965 additionally sets `player+0x946`, the nitrous
   injector bit.
5. **Hand the outgoing airframe to `wingman_4`** (966 and 967 only). `DAT_0071c4f0` is resolved once
   at mission start by `FUN_004735b0`, which at `0x00475018` compares each roster name against
   `wingman_4` (`0x00627b34`) and then the chapter/mission pair against `c3`/`m05` and `c4`/`m04`
   (`0x00627b40`–`0x00627b4c`), holding nothing everywhere else. Only inside that arm does it give
   the record **the player's own aircraft type and livery** (`DAT_0071daec`, with airframe id 0xb
   substituted by 5, and the paint pair at `DAT_0071daf0`/`DAT_0071daf4`). Both cases then give it
   the section sums measured in step 1 (`+0x2c8` armour, `+0x2d0` structure) and reveal it
   (`FUN_004b0f40(0)`, which clears the hidden bit `+0x945` and the three cutscene flags and
   reactivates the scene node). **Only 967 places it**: 100 m along the bearing `yaw − 45°` with its
   own nose left on the player's `yaw`, so the two are flying alongside rather than converging.
   967 alone also resolves the raising animation's own vehicle (`FUN_00523990(anim)` reads the root
   node at `anim+0x48`, `FUN_004afee0` finds the vehicle whose `+0xc` is that node), hides it with
   `FUN_004b0f40(1)`, and scales the new airframe's four sections by that vehicle's WHOLE armour
   and structure fractions (`+0x2c8/+0x2c4` and `+0x2d0/+0x2cc`) through `FUN_0047bf70`, so the
   captured plane's damage carries onto the one the player is now flying. It also copies the
   captured vehicle's `+0x388` onto the player, the field `FUN_0047c210` writes at build and the
   wave and targeting code reads as the vehicle's cohort.

**CSVM implements all five steps.** The rig is rebuilt through the flight roster's own assembler on
the named airframe, at the pose, heading, throttle and speed the outgoing aircraft held, with that
airframe's stock fit at full ammunition and its own armour pools; the cutscene flags land on the
aircraft the swap built. Step 2's re-point walk is `FlightRoster.RepointHolders`: every AI pilot
whose escort leader or standing quarry was the outgoing aircraft holds the replacement (CSVM has
no global target list; those two fields are where an AI holds a vehicle), and the campaign
director re-subscribes its death and damage hooks on the rebuilt rig. 967's `+0x388` copy is
`FlightController.Group`, stamped from the captured aircraft's roster plan onto the replacement
(`AirframeHandover.CarriesCapturedGroup`), and the `DEDG` walk counts a human rig carrying the
counted group as one live member, which is what keeps `C3/M05`'s `DEDG [5, 0]` from completing
and napping the instant loss once the player is flying the last bomber. `Session/Roster/AirframeSwap.cs`'s
`AirframeHandover` carries the mission gate, the 100 m / −45° placement and the capture test;
`FlightRoster.RunSwap` runs the whole order, and the definition's root node reaches it through
`AnimRuntime.CallbackHost`. Step 3's table is also why the rebuild reads no `LoadoutChoice`
(`HumanFlightAdapter.MenuFitFor`): the sortie's Ammo Selection picks belong to the aeroplane the
pilot left, and composing them over the handed-over airframe would fly the pilot's own rockets
where the case writes `wep_06`, which is what the `campaign-hangar-handover` suite reads for all
three codes. Three divergences, each deliberate:

- **The airframe and livery are decided at the roster spawn, not at the swap.** The original writes
  them at mission start and so does CSVM (`CampaignRosterPlan.Build`'s `handover` argument), which
  is why `wingman_4` is a Devastator in `player_fortune` paint everywhere else and the player's own
  aeroplane in these two missions.
- **The handed-over sums are capped at the receiving aircraft's own maxima.** The original needs no
  cap: its `wingman_4` flies the player's airframe, so the sums cannot exceed its pools. CSVM reads
  one airframe's pools as a zone sum on a human rig and as the AI def's authored pair on an AI one
  (`docs/org/vehicleDamage.md`), so an undamaged hand-over lands at the receiver's full pools rather
  than at the player's larger number.
- **967's rebuild carries the captured aircraft's own `PaintScheme` onto the player's new hull**
  (`BL-543`, `BL-554`). This is NOT in the executable: case 967 (`FUN_0047e080`, `0x3c7`) rebuilds
  through `FUN_0047fd50(s_pbalmoral, s_player_balmoral)`, which reads no paint field off the
  captured vehicle, and `DAT_0071daf0`/`DAT_0071daf4` (the paint pair step 5 writes) are touched
  only in `FUN_004735b0` at mission start, giving `wingman_4` the PLAYER's own scheme, the opposite
  direction. The original keeps the Fortune Hunters Balmoral seen at the controls; CSVM's carry is
  the user's own at-the-controls reading, taken over that trace by this project's standing rule
  that a human's seen result outranks an instrument. `FlightController.Scheme`/`ShippedSkins`
  record what painted a rig, so `FlightRoster.RunSwap` can read the captured rig's back even when a
  real enemy roster spawn's `ShippedSkins` reading resolved it to no scheme at all: that null has to
  carry too, or the rebuild falls back to the player's own default livery.

- **965's rebuild flies the Blue Streak build in its shipped skins, unpainted.** The case's tables
  (40/30 with both twin bytes, two hardpoints of six, 20 armour across, the injector bit) are the
  special-plane template for airframe 3 (`docs/org/hangar.md`), so CSVM assembles the rebuild
  from `CampaignProgression.AwardBuild(3)` (`AirframeSwapCode.AwardAirframe`) rather than the
  stock fit, and the same swap onto `player_bhawk` with no build stays a stock Bloodhawk with no
  injector. The livery is the absence of one: `pbloodhawk` authors no `paint_pattern`, and
  `FUN_0047c210` tests the pattern string's length and jumps past the whole scheme composite on
  zero (`0x0047db0c`; the `player_fortune` branch that copies the launched plane's scheme from
  `0x0071db08` is never reached), so the original draws the `blo_*` skin textures as shipped: the
  desaturated blue-grey body with the yellow-olive stripe on the wingtips and fin
  (`docs/formats/paint.md`), which is the aeroplane the hangar clip shows. No shipped scheme
  produces that look (`blake` is light blue-grey with near-white trim, `hughes` a yellow body).
  CSVM's code carries `ShippedSkins` (`AirframeSwapCode.ShippedSkins`), the same reading a real
  enemy spawn draws under, so the rebuild composites nothing over the archive's skins.

⚠ **967's hide has to outlive the reveal of code 914.** In `C3/M05` the capture definition raises
967 from the same sequence that calls `wingwalk`, and `wingwalk_parent-wingwalk` brackets its own
19.25 s motion with **913 then 914**: the captured aircraft is parked by 913, hidden by 967 while it
is already parked, and then handed back by 914 at the end of the wing walk. The original keeps the
two apart, since 913/914 drive the vehicle's hold flag and scene node while 967's
`FUN_004b0f40(1)` sets the vehicle's own hidden bit `+0x945` and its dead byte `+0x91d`; CSVM
carries the presence half of both on one `Inert` flag, with `Parked` beside it for the hold flag
(`+0x354`, which `FUN_0041f250` sets and the DEDG walk never reads), so `CutsceneController` drops
whatever the swap hid out of its parked list and clears its `Parked` instead. Measured on
CM02's own definition, played through the runtime: without that drop the Balmoral comes back
**19.25 s** after the swap, which is the aeroplane sitting in front of the player at the cut back to
flight.

⚠ Neither the hand-over nor the damage carry-over is asked for by anything in the shipped data.
Both are keyed on the chapter and mission strings above, so a search of the data for a trigger comes
back empty and **that emptiness is not evidence they do not exist.**

### The re-placement code 951

A cutscene that flies the pilot somewhere ends by leaving them there, and 951 is how. Case `0x3b7`
of `FUN_0047e080` reads the player vehicle's own scene node (`DAT_0071c298+0xc`, which IS the
`player` node the definition has been animating) and writes that node's world pose back into the
vehicle's physics state:

1. `FUN_004cf200(node, &pos)` and `FUN_004cf380(node, &rot)` read the node's WORLD position and
   rotation, off its world matrix (`+0x84` and `+0x60`) or, for a node the walk has to resolve, off
   a rebuilt one. Visibility does not enter it.
2. `pos` goes to the vehicle's position `+0x204`, its previous position `+0x1a4`, and into every
   entry of the trail list at `+0x6a4`..`+0x6a8` (stride `0x24`, offset `+0x18`), so no smoothing
   drags the aeroplane back from where it was put.
3. `rot` builds a quaternion at `+0x150` (`FUN_0053f610`) which `FUN_0053fa40` expands into the 3×3
   basis at `+0x180`.
4. The velocity `+0x924` is that basis's third axis (`+0x198`) times **-53.6448**, with `+0x930` and
   `+0x934` its square and its length. The axis is unit length, so the aeroplane is released at
   53.6448 units/s along its own nose. The same figure, rounded, is CSVM's fallback spawn speed.
5. `FUN_004d1d50`/`FUN_004d1a30` write the pose back onto the node as a LOCAL transform, which is
   what re-seats it after the definition's `OBJECT_ADD_CHILD` has put it back under `world1`.

**Where the placement is authored: in the definition, not in the mission script.** `C3/M01`'s
`player-texdrop` is the worked example. Its player sequence reparents `player` from `world1` under
the chapter node `do_direction`, flies it there on SI script `td_player1` (local coordinates, ending
about 123 m out), reparents it back under `world1`, and flies it on `td_player2`, whose keyframe
bases are absolute world coordinates and which ends at **(-8469.09, 170.02, -4790.26)** on a fixed
quaternion. Only then does it raise 951. The pose the pilot resumes at is therefore the last SI keyframe of the
last script the definition runs in `world1`'s frame, and it is independent of the heading the pilot
flew in on. `objectives.zrd` carries nothing about it; `WARP_VEHICLE` is a different verb on a
different subject ([objectives.md](../objectives.md)).

The 14 occurrences are one per mission's `piratezep-pzep_launch_player` (12 missions) plus every
mid-mission definition that moves the pilot: the drops, the hookups and unhooks, the pickups, the
wing walk and the trailer. `pzep_launch_player` never puts `player` back under `world1`, so its 951
lands the pilot on `pzhookpoint`; **no mission in this install calls it**, and `generic_intro` raises
no 951 at all, which is why the intro's own handoff is a hand-back to the authored spawn rather than
a re-placement.

**Two shipped carriers raise it while naming no `player` node at all**, so there is no placement
anywhere in them to read: `C2/M05`'s `balmoral-drop_paratroopers` (nodes `cargozep2`, `balmoral`,
`camera1`) and `C5/M04`'s `stihellhound_eg0-milesdrop` (nodes `stihellhound_eg0`, `camera1`). In the
original that costs nothing, because the node the case reads IS the player's vehicle: it writes the
aeroplane's own pose back onto itself and only the release speed changes.

**CSVM.** `CutsceneController.ReplacePlayer` reads the staged `player` marker's world transform and
hands it to `FlightController.ResumeAt`, which moves the staging's hand-back target. The move is
applied at the hand-back rather than at the callback, because the out-of-flight hold re-asserts its
pin at zero speed on every step it runs and would wipe a speed written earlier. A session that
staged no `player` marker (a mid-mission drop in a mission with no intro of its own, the limit named
under [the `chuteman` staging gap](#a-mid-mission-drops-chuteman-is-the-same-staging-gap)) logs the
callback and re-places nothing.

⚠ **The marker is a bodiless stand-in, not the aeroplane, so an unposed one is not a placement.**
CSVM builds it under the world root at identity and every handoff parks it back there, which is
where the two carriers above leave it. Read as a pose, that is the world origin: measured over
`drop_paratroopers`, the handoff moved the aeroplane 12567.5 m, from 424.4 m above the surface to
56.5 m below it, with no way back up through the single-sided terrain. `ReplacePlayer` therefore
declines a marker still parked at its home, which leaves the pilot where the original leaves
them.

### Handoff and skip

Two exe paths end a cutscene without a `CALLBACK`, and a cutscene player needs both behaviours:

- **Handoff.** `FUN_00480480(vehicle)` clears `+0x91d`/`+0x91e`/`+0x91f` and force-stops the
  animation recorded at `vehicle+0x6d0`. Its caller `FUN_00470a10` follows that with
  `FUN_00455800(1)` (chrome back) and a view-mode restore.
- **Skip.** The per-frame state core `FUN_004a0220` (`StateCore.cpp`) checks the active-cutscene
  slot against an input poll each frame: while `DAT_0071c50c` is set and `FUN_00536000` reports an
  event, it clears the slot and force-stops the animation through `FUN_004ed480`.

#### ⚠ Only a definition that raises 20 can be skipped, and a skip drops the rest of its codes

`DAT_0071c50c` is written in five places and armed in exactly one: `FUN_0047e080`'s
`if (param_3 == 0x14) DAT_0071c50c = param_1` at `0047e2e6`, the world-hold code. The other four
(`0047e0bc` on code 0, `0047e22a` on code 12, `004a02c8` in the poll itself, `00472e5f` on mission
teardown) all clear it. The key latch the poll reads, `DAT_0075cb60`, is consumed by `FUN_00536000`,
which has one caller in the whole binary, and none of `FUN_004ed480`'s other fifteen callers reads
that latch. **A definition that never raises 20 cannot be skipped by the player at all**, and there
is no second input path to it.

What the stop then runs is the node restore `FUN_004ed090` plus the `RESET_SEQUENCE` record at
`anim+0xd0`, stepped by ordinary frame `dt` (`LAB_004ed2a0`). The numbered sequences carrying the
timeline's `CALLBACK` events are never stepped again, so **every code still ahead of the skip is
dropped**. The `RESET_SEQUENCE` reaches the stepper through the same dispatch table, so a callback
authored there would still raise; whether any shipped definition authors one is a data question.

Across the shipped campaign three definitions raise 20: the shared `generic_intro`, C1/M04's
`mission_intro_animation` and C3/M03's `player-cgzep_camera`. Every mid-mission definition that
changes the player's aeroplane or its place (CM02's `ww_balmoral1` with 967, CM01's `texdrop` with
951) raises none, which is why dropping the remaining codes costs the original nothing. An intro's own
remainder past the arm is 2, 11 and 14, and its `RESET_STATE` authors 1, 914, 10 and 667.

#### A remake-only rule: a held input fast-forwards a scene that arms no skip

CSVM adds a way through the scenes the paragraph above leaves unskippable, and it is **not** the
original's skip. Nothing in the executable raises a rate, and this rule stands beside the livery
carry in [the airframe swap codes](#the-airframe-swap-codes-965-966-and-967) as something the
project decided rather than decoded. While an input the skip declined stays down, the episode's
own definitions run at up to **4x**, reached over a **0.25 s** ramp and released over the same
ramp; the definition's ambient `SOUND_NODE` emitters and the one-shot `SOUND`s it fires are
pitched by the same number, so the sound rises with the picture. Both figures are design choices
with nothing behind them, and both are constants on `Mech3/Anim/CutsceneFastForward.cs`.

Three properties make it a fast-forward rather than a cut:

- **Every authored code still fires, in order, at its own authored time.** The rate multiplies the
  DEFINITION's dt, so 913/914, 951, the swap codes and the `RESET_STATE` block arrive at the same
  point of the definition's clock and only sooner in real seconds. A skip, by contrast, drops every
  code still ahead of it.
- **The rate is scoped to the episode's `CALL_ANIMATION` closure.** The world outside it, the
  flight models and the session clock keep real time; `GameClock.SimHeld` is untouched. A
  mid-mission scene raises no hold code, so the world around it is live and must not speed up
  with it.
- **The raised rate is spent as repeated passes of the runtime's whole instance walk**, never as
  one longer step. Two codes an authored frame apart would otherwise land in one pass and be
  ordered by the walk rather than by their own times, which is the order 967's hide and 914's
  reveal depend on above.

⚠ **It is never offered where the original arms a real skip.** Callback 20 clears the rate as it
sets `Skippable`, so an intro takes the key press as the force-stop the original performs and
nothing else. The `campaign-cutscene-fast-forward` suite plays CM02's capture at both rates over
its built world and reads the two against each other.

## The intro defs' eight dispatches

`camera1-generic_intro` (shared; 12 of the 13 story missions bootstrap it) authors exactly **eight**
`CALLBACK` events, and they are the eight the project's census counts per chapter:

| where in the def | codes, in authored order |
|---|---|
| `RESET_STATE` | 1, 914, 10, 667 |
| `SEQUENCE_DEFINITION callback_sequence` | 20, 2, 11, 14 |

C1/M04's bespoke `camera1-mission_intro_animation` authors the same eight in the same two places,
with `RESET_STATE` ordered 1, 10, 914, 667 and a ninth, **913**, closing `callback_sequence`.

A tenth code, **666**, is authored once more inside `generic_intro`, in the `gi_1stperson` sub-def.
That def's `NAME` is `player`, not `camera1`, which is why a census anchored on `camera1` does not
count it. It sits immediately before a `CAMERA_STATE` that pulls `camera1`'s near clip in to 1 m,
which is exactly the case where the automatic camera-parameter profile must not overwrite the
authored value.

### ⚠ The codes do not identify a cutscene

`camera1-player_setup` authors **the same nine codes**, 1, 10 and 914 in `RESET_STATE` and 20, 2,
11, 14 and 913 in `callback_sequence`, and it is not one of the 13 story-mission intros the
`generic_intro` / `mission_intro_animation` census counts. **A consumer that decides "this is a
cutscene" from the authored codes therefore gives every mission in the install a letterbox and a
suspended world.** Ask by definition name.

It is also not an Instant Action definition. It is the bootstrap of a mission that opens without a
movie, and of a mission resumed from a save. All eight `ia1` missions list it under
`NEW_GAME_START`, and so do the ten story missions with no opening film (C1/M02, C2/M01, C2/M02,
C2/M03, C2/M05, C3/M04, C4/M03, C4/M05, C5/M02 and C5/M03); all 24 story missions list it under
`LOAD_GAME_START`, which the `ia1` and `mp` missions leave null. The multiplayer counterpart is
`multiplayer_setup`, which every `mp` mission bootstraps in its place.

Beside its nine codes it does two `OBJECT_ACTIVE_STATE` events: `RESET_STATE` switches the `player`
node **ACTIVE** and calls `speed_cue`, and `callback_sequence` switches `player` **INACTIVE**.
`ACTIVATION` is `ON_CALL`, `AUTO_ADD_TO_WORLD` is `OFF` and `RESET_TIME` is `[0, -1]`. The AI park
a reader might expect from its 913 is real, and comes from the engine, which parks before any start
list of any mission type runs; its `RESET_STATE` 914 is what lifts that park, and with no timed
event between the two the whole park lands inside the mission start
([above](#the-intro-defs-run-without-a-host)).

Reading the eight as a pair of state transitions:

- `RESET_STATE` (applied at load) asserts the **gameplay** end state: hand control back and restore
  the chrome (1), restore the in-flight systems (10), reveal the AI vehicles (914), restore the
  camera-parameter gate and view mode (667).
- `callback_sequence` (runs when the definition plays) asserts the **movie** state: suspend the
  simulation (20), hide the chrome and detach the view target (2), take the player out of flight
  (11), and 14.

The def named the sequence `callback_sequence` itself, which is the author's own label for "the
beat that notifies the host".

### The C1/M04 worked example

M04's intro is spread over three readers, all listed by its `mis_anim.zrd`:

- `intro.zrd` holds `camera1-mission_intro_animation`: the callbacks above, a
  [`FogState`](../anim-definitions.md#fogstate--an-inline-fog-written-over-the-zone) `drop_fog`, the
  pirate zeppelin's start pose, then `start_script`, which calls `scene1`, stops it at 15.9 s, calls
  `scene2`, stops it 12.4 s after that event, and branches on which airframe the player is flying
  (`check_balmoral` → `check_warhawk` → `drop_planes`).
- `scenes.zrd` holds the beats. Five of them call `letterbox`.
- `startanims.zrd` puts `mission_intro_animation` in `NEW_GAME_START`. Both of its sections also
  name `pure_panic`, the C1/M02 hangar-crowd definition (`hangar_panic.zrd`, root
  `hangar_panic_scream`); no M04 reader file lists that reader, so the mission's compiled archive
  holds no such definition and the loader's lookup finds nothing. The same dead name sits in
  C1/IA1's and C1/MP1's `startanims.zrd`, which is the shape of a copied template rather than of
  an intent. The original plays nothing for it in M04, and neither does CSVM
  (`undefined here: [pure_panic]` in the start-anims log line).

The zeppelin's two visible jumps are the cuts between those beats, and its disappearance is the last
beat deactivating it; the smooth phase between them is
`introanm/pzep1-piratezep.zan`, 48 frames at 1/3 s on a straight line.

## Reader rules and edge cases

- **Never read a callback's meaning from the def it sits in.** The authored `VALUE` is the whole
  message. The same integer means different things to different hosts, and one host answers only a
  subset of the integers authored against it.
- **A `CALLBACK` with no host is a no-op, in the original too.** Absence of an effect is not
  evidence the code is meaningless.
- **The bars are world geometry, not a UI overlay.** Opaque black quads on material 0, 7.5 m ahead
  of the camera, wider than the frame. Model 0 carries `facade_mode: CylindricalY` and
  `flags.lighting`/`flags.fog` both true; at 7.5 m the fog contribution is negligible, and the def
  pins the node's full orientation to `camera1` every tick regardless of the facade mode, so a
  builder should copy the transform and not rely on billboarding to keep the bars square.
- **`AT_NODE` on a pose event is spelled differently in each front-end, and the compiled rotate
  itself has two spellings.** The reader writes `OBJECT_TRANSLATE_STATE … AT_NODE [camera1]` and
  `OBJECT_ROTATE_STATE … AT_NODE_MATRIX [camera1, 0, 0, 0]`; the compiled twin writes a flat
  `at_node: "camera1"` on the translation, with `state` carrying the offset inside that frame
  (zero for the bars) rather than an absolute pose. On the rotation the compiled field is nested
  under `basis`, but its key is `AtNodeMatrix` on the eight chapters' letterbox (8 sites) and
  `AtNodeXYZ` everywhere else in the install (172 sites, `britbalmoral_1-ww_balmoral1`'s wing-walk
  frame among them), the reader-normalizing front-end (`AnimDefs.AddAtNode`) only ever emits the
  first spelling, so the second reached no compiled extraction until the rotate handler was taught
  to read both. **The compiled def wins**, so a consumer that reads only the reader spelling sees
  the target teleported to its parent's origin. Install-wide there are 190 non-null `at_node`
  translations; `INPUT_NODE` appears as an `at_node` value and is a sentinel, not a node name.
  The two rotate spellings are **different rules**, decoded below.
- **A change to `generic_intro` is verified by what disappears, not by what looks right.** Twelve
  missions share it, and an 8-chapter freecam regression cannot see any of it: no Instant Action or
  multiplayer mission bootstraps an intro, so that regression is inert here by construction
  (`docs/verification.md` DIAG-10). Check per mission.
- **The letterbox node is a library root.** Parentless, no spatial-partition reference, and the def
  never reparents it, so a renderer that only walks `world1` will never reach it. See
  [`world-structure.md`](../world-structure.md) for the library-root test.
- **Code 20 stops the world, not just the camera.** Vehicles, AI and the objectives runtime all
  early-out while the active-cutscene slot is set. A player that only moves the camera will let the
  mission run underneath the movie.
- ⚠ **The story-mission intro defs must play.** `generic_intro` (12 missions) and
  `mission_intro_animation` (C1/M04) are the missions' authored intro movies and are working assets;
  nothing on this page is a reason to suppress one, and skipping them at bootstrap has been ruled
  out. C1/M04's pirate zeppelin flying above the overcast is an accepted artifact of the intro
  playing without a player, not a defect to work around.

## Evidence and limits

- Geometry, material and node flags read from `extracted/<Cx>/gamez/{nodes,models,materials}.json`
  for all eight chapters; the `letterbox` subtree is identical in each.
- Reader values read from `extracted/zrdr/{letterbox,generic_intro,player_setup}.zrd.json`,
  `extracted/<Cx>/zrdr/landings.zrd.json`, `extracted/C1/M02/zrdr/pickups.zrd.json` and `extracted/C1/M04/zrdr/{intro,scenes,mis_anim,
  startanims}.zrd.json`. Counts are over every `*.zrd.json` in the extraction; the section census
  behind the bootstrap claims is over every `*/zrdr/startanims.zrd.json` in it, all 53.
- Exe claims name the function they came from. The registration census is complete: `FUN_004ee160`
  is the only writer of the host pointer at `anim+0x74` on an animation instance, and its 13 call
  sites were each read.
- **Undecoded: `RESET_TIME`.** The letterbox def's `RESET_TIME [0]` is the scheduling field the
  landing page also marks undecoded, so *when* a running letterbox returns to its `INACTIVE` base
  state inside a mission is not established here. Nothing stops the def by name, so a consumer has
  to retract the bars itself at the handoff (`CutsceneController`, `docs/architecture.md`).
- **Undecoded: what field of view a cutscene is framed at.** The bars are a fixed card 7.5 m ahead
  of the eye, 13.2924 by 8.0948, so the frame they letterbox is 56.7° vertical on any window, the
  card's own height, and the card overhangs it horizontally up to its own 1.642 ratio and has to be
  stretched sideways above that. `camera1`'s own gamez `Camera` record carries `fov_h_base` /
  `fov_v_base` of 0 (runtime-filled), and the `CAMERA_STATE` events in the intro readers set only
  `NEAR_CLIP` and `LOD_MULTIPLIER`, so the number itself is not in the data.
- The `player` reading above rests on the aircraft archive's own `nodes.json` (the base rule holds
  for all eight chapters, and within C3 for all nine cross-archive pointers the intro's symbol
  tables carry), on the airframe name table at `0x00620cc0`, and on three exe sites: `FUN_004d0280`
  (name lookup over node table 7), `FUN_0042e5e0` (its caller, hiding the node for a render pass)
  and `FUN_004136e0` (mission setup, passing the literal with the loadout record). **What
  `FUN_0041a320` does with the pair was not read**, so "the record is created under that name" is
  the shape of the call, not a traced construction.
- **Undecoded: what computes the base.** The rounding rule is read off the eight chapters' own
  numbers; no exe site that computes it was traced, and no chapter's node count lands exactly on a
  multiple of 2500, so whether the rounding is strict or inclusive is undetermined. Resolve these
  names by name rather than by arithmetic on the pointer.
- **`camera1-player_setup` is the bootstrap of a mission that opens without a movie**, and of a
  mission resumed from a save, not an Instant Action definition. Its `RESET_STATE` 914 is what lifts
  the engine's mission-start AI park, and its own work beside the codes is the two `player`
  active-state events (above, "The codes do not identify a cutscene"). **Undecoded: what the
  original shows while it runs**, and whether the definition-table host walk in `FUN_004735b0`
  reaches it, which turns on the `+0x9c` bit `0x10` gate in `FUN_00523920`.
- **Undecoded: how the original draws a parentless active root.** `gwNodeSetActive`
  (`FUN_004cca30`) only flips the node's active bit; the traversal that reaches `letterbox` without
  it being anyone's child was not traced.
- **Named gaps:** callback codes 14 and 123 are authored but reach no case in the mission-script
  host, and no other installable host takes them.
