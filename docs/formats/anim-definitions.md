# Animation definition readers

Part of the [format documentation](README.md). The zrdr sources define mission start states and
animation behavior; the project fork also decodes their compiled per-mission `mis_anim.zbd`
archives. This page contains field tables and small illustrative values only.

## Contents

- [Definition locations](#definition-locations)
- [File shape](#file-shape)
- [Definition fields](#definition-fields)
- [State operations](#state-operations)
- [Animation calls](#animation-calls)
- [Sequence stopping](#sequence-stopping)
- [Fire animations](#fire-animations)
- [Point lights](#point-lights)
- [Ambient audio](#ambient-audio)
- [Mission library scope](#mission-library-scope)
- [Start animations](#start-animations)
- [Zeppelin states](#zeppelin-states)
- [Compiled animation archives](anim-definitions/compiled-archives.md)
- [Cutscenes: the `letterbox` node and `CALLBACK` codes](anim-definitions/cutscenes.md)

## Definition locations

Three zrdr scopes are visible to a mission (the remake scans all reader files in each
whose content mentions `ANIMATION_DEFINITIONS`):

| Scope | Examples |
|---|---|
| shared `zrdr.zbd` | `small_building`, `medium_building`, `aa_gun`, `wing_light`, `plane_props`, zeppelin part anims (`multi1_*`, `pzep_*`, …) |
| chapter `<Cx>/zrdr.zbd` | C1: `hangar3`, `train`, `fuel_tanks`, `ref_fueltanks`, `train_signal`, `ap_*` airfield buildings |
| mission `<Cx>/<mission>/zrdr.zbd` | `zepstate` (per-mission base object states), `mis_anim` (mostly `ANIMATION_DEFINITION_FILE` references), mission one-offs (`hangar_panic` in C1/M02) |

A mission's `mis_anim.json` lists `ANIMATION_DEFINITION_FILE` paths into the *source*
data tree (`..\data\common\zrdr\zeps\*.zrd`); those files are the shared-scope readers
under their extracted names, following the references is unnecessary when scanning all
three scopes.

## File shape

```
[["ANIMATION_DEFINITIONS", [
    ("GRAVITY", [-9.8])?  ("ANIMATION_PATH", ["..\\data\\…"])?
    "ANIMATION_LIST", [
        "ANIMATION_DEFINITION", [ …def… ],     ; repeated
        "ANIMATION_DEFINITION_FILE", ["path"], ; mis_anim only
        … ] ] ]]
```

Alternating key/value-list pairs **with meaningful duplicate keys** (multiple
`SEQUENCE_DEFINITION`s per def, repeated op kinds per sequence), a collapsing dict
view loses data; walk the raw list. A key followed by `null` (or by another key) is a
bare flag (`LOCAL_NODES_ONLY`).

## Definition fields

| Key | Value | Meaning |
|---|---|---|
| `NAME` | 1 string | The world/object node(s) this def anchors to. Wildcards make one anim instance per matching node: each `*` = at most one digit (`ftank0*` ↔ `ftank01`, `s_build**` ↔ `s_build12`, `lkshadow*` ↔ the compiled `lkshadow`; the original's odometer stamps one digit per star, [org/sequences.md](../org/sequences.md), so `crate**` never reaches `craterlake`), `#` = run of digits (`air_gen#`). Node names may be given without their `.flt` model suffix (`ap_radiotwr` ↔ gamez node `ap_radiotwr.flt`). |
| `NAME1` | list of (wildcard, [path…]) pairs | Multi-target form (zeppelin nacelle/turret sets): maps anim-instance name patterns to node paths inside a parent object. Per-object anims, part 2 scope. |
| `ANIMATION_NAME` | 1 string | The name `startanims.json` / `CALL_ANIMATION` refer to. May itself carry a wildcard in template defs (`ftank_boom*`). |
| `ANIMATION_ROOT_NAME` | 1 string | The node inside each instance the anim attaches to (`s_bld_healthy`, bare `healthy`). Needed to locate instances whose roots have free names: `m_build**` instances in C1 are `apbuild01.flt`/`aphngr01.flt`/…, found via their `m_bld_healthy` child. |
| `LOCAL_NODES_ONLY` | bare flag | Op node names resolve inside the instance subtree only (building/vehicle templates). |
| `ACTIVATION` | `ON_STARTUP` \| `ON_CALL` | ON_STARTUP defs run their sequences at mission load (`zepstate`); ON_CALL waits for `CALL_ANIMATION`/game events. Sequences may carry their own `ACTIVATION ON_CALL` (run only via `CALL_SEQUENCE`). |
| `EXECUTION_BY_RANGE` | 1 float, **metres** | Proximity gate: the def executes only with a player within this distance of its anchor. Compiled as `execution: {ByRange: {min, max}}` in **metres SQUARED** (reader 50 ↔ compiled 2500), the same reader↔compiled unit divergence as `PLAYER_RANGE`; `min` is 0 across the install. On an ON_STARTUP def the runtime defers the start until a player first enters the band (the C3 `spiderweb_gone` 50 m fade; the 300 m nacelle-prop spins; C1's reader-only `cloudparent#` at 1900 m). 883 compiled defs carry it. |
| `ACTIVATION_PREREQUISITE` | `OPTIONS [MINIMUM_TO_SATISFY n, ANIMATION_LIST …]` or `REQUIRED`/`OPTIONS [OBJECT_ACTIVE_LIST [[path…]…], OBJECT_INACTIVE_LIST […]]` | A start gate. The anim-list form is a counter over the definition's own callers (`all_pzep_gasbags`: 3 of the 6 `finish_pzepgasbag*`); see [The anim-list form counts the definition's callers](#the-anim-list-form-counts-the-definitions-callers). The node-state form names node paths and the active state each must read for the definition to start (see [The node form reads the node's own live flag](#the-node-form-reads-the-nodes-own-live-flag)); compiled as a run of `Parent` entries closed by one `Object` leaf carrying `active_raw`/`required`, where the state is BIT 0 of `active_raw` (bit 1 is the def's LOCAL_NODES_ONLY scope, so a local INACTIVE entry reads 2, which mech3ax's `active` field misreports as true; [compiled-archives.md](anim-definitions/compiled-archives.md)). The shipped weight of the node form is the zeppelin gasbag finishers (`finished_lkgasbag0*`, `finish_pzepgasbag*`: `REQUIRED [OBJECT_INACTIVE_LIST [[gasbag, panelleftb1], [gasbag, panelrightb1]]]`, local), called by the burn that switched those panels off, and C1/M04's `hk_zep` (no zeppelin record) sinks only through them: door death, gasbag burn, finisher, three finishers satisfy `finish_locklear`. C1/M02's hangar drop is the shipped fork: `hangar_drop` calls both `hdplayer1` (`REQUIRED [OBJECT_INACTIVE_LIST [[hdrop_direction]]]`) and `hdplayer1b` (the same node ACTIVE), and `hdchute1`/`hdchute1b` likewise, so the direction sensor's state picks one camera leg of each pair. 112 distinct definitions carry the node form (gasbag panel finishers, `pzep_cargo_point`'s cargo stop, C1C/M01's docking legs), 437 definition files once every `mis_anim`/`cam_anim` archive's copy is counted, holding 1028 node entries of which 784 are REQUIRED; the reader spelling appears in 26 `zrdr` files, 52 keys. The runtime enforces the REQUIRED node entries at `Start`, each leaf bound through its compiled `ptr` (the definition's own node, whatever anchor the call arrived on: a finisher starts on the dead cannon's anchor, and C5/M04's Dante shares every panel name with nine other zeppelins) and by name path only for a reader definition or an unbuilt index, and the anim-list count at `CALL_ANIMATION`; optional node entries and a `MINIMUM_TO_SATISFY` over nodes are parsed only. |
| `RESET_TIME` | 1–2 floats (−1 common) | Reset scheduling (undecoded detail). |
| `RESET_STATE` | op list | The object's **base state**, applied at load: healthy variants ACTIVE, `destroyed` variants INACTIVE, doors at rest pose. This is what fixes the destroyed-over-healthy coplanar flicker. |
| `SEQUENCE_DEFINITION` | op list (repeatable) | One timeline of ops; optional `NAME`, optional `ACTIVATION`. |
| `HEALTH`, `DAMAGE_SEQUENCE` | | Destructible-object HP + damage-threshold script (`IF ANIM_HEALTH n … CALL_ANIMATION sputter_fire_smoke_obj …`). |
| `SAVE_LOG` | `ON` \| `OFF` | The def's state goes into the engine's **state log**, the record that survives a mission reload and a save/restore. See "`SAVE_LOG` / `PERSIST_LOG` are the cross-mission state log". |
| `PERSIST_LOG` | `ON` | The def's state **additionally crosses mission boundaries**: a later mission in the same chapter loads with it applied. A strict subset of `SAVE_LOG ON` (62 defs, all of them fixed world scenery). Same section. |
| `EXECUTION_PRIORITY`, `AUTO_RESET_NODE_STATES`, `AUTO_ADD_TO_WORLD` | | Engine bookkeeping, undecoded detail. |

## State operations

| Op | Body | Meaning |
|---|---|---|
| `OBJECT_ACTIVE_STATE` | `NAME` [node…], `STATE` [`ACTIVE`\|`INACTIVE`] | Show/hide a subtree (and its collidability). A multi-entry NAME is a parent→child path (`["piratezep","interior"]`). |
| `OBJECT_TRANSLATE_STATE` | `NAME`, `STATE` [x,y,z], `RELATIVE` | **Absolute** position in the node's parent frame (see below). `RELATIVE` is `false` in all 1143 uses in this install. |
| `OBJECT_ROTATE_STATE` | `NAME`, `STATE` [x,y,z], `BASIS` | **Absolute** orientation in the parent frame, **radians compiled, degrees in the zrdr sources** (see "rotations" below). `BASIS` is `"Absolute"` in 6430 of ~6600 uses; the rest are `AtNodeXYZ`/`AtNodeMatrix` look-at forms (zeppelins, cameras). |
| `OBJECT_MOTION_FROM_TO` | `NAME`, `TRANSLATE`/`ROTATE`/`SCALE` `{from,to}`, `RUN_TIME` [s] | Timed motion between two **absolute** parent-frame poses (C1 hangar 3: four `h3_dr*` doors over 9–10 s). The rotate channel shares `OBJECT_ROTATE_STATE`'s unit split. |
| `OBJECT_MOTION` | `NAME`, `XYZ_ROTATION` [ix,iy,iz,dx,dy,dz], optional `RUN_TIME` [s], plus the `GRAVITY`/`TRANSLATION[_RANGE]`/`FORWARD_ROTATION`/`SCALE`/`BOUNCE_SEQUENCE` channels | Two ops in one: a **steady spin** (`XYZ_ROTATION` alone, at `initial` rad/s, deg/s in the reader, endless without `RUN_TIME`; the zeppelin nacelle props) OR a **ballistic body** (translate/launch + scale ramp + tumble under gravity; the crash pieces and debris arcs). See "`OBJECT_MOTION` is two ops sharing one event". |

### `OBJECT_OPACITY_STATE` is translucency, not visibility

`state` is whether translucency is **enabled**; `opacity` is the alpha while it is. The data
settles this: across all 168 compiled uses, `state=false` pairs with `opacity=1.0` **136 times
and with 0.0 never**, so `false` means "render normally", not "disappear". Hiding is
`OBJECT_ACTIVE_STATE`'s job and the data uses both side by side. The three shapes shipped are
`(false, 1.0)` ×136, `(true, 1.0)` ×16 and `(true, 0.4)` ×16.

**The reader writes the token and the value in EITHER ORDER, and the value is optional.** All
five spellings occur: `["ON", 0.6]`, `[0.4, "ON"]`, `["OFF", 1]`, `[1, "OFF"]` and a bare
`["OFF"]` (value defaults to 1). So a normalizer must scan the list by type, not by position.

`OBJECT_OPACITY_FROM_TO` tweens the same pair with a `RUN_TIME`; a fade-out is
`(true,1.0) → (false,0.0)` ×5073 and a fade-in `(true,0.0) → (false,1.0)` ×4609, i.e. the end
state disables translucency once the object is fully opaque again (or is deactivated outright).
**`AnimRuntime.OpacityFade`** handles the largest kind (9,917
events): a linear lerp of the two `opacity` numbers over `RUN_TIME`, driven through the same
per-instance `csky_opacity` parameter `OBJECT_OPACITY_STATE` writes. ⚠ **The endpoint `state`
flag does NOT invert the value**, surveyed across all 9,917, `(state=false, opacity=0)` fades to
invisible and `(state=false, opacity=1)` to opaque, so it is a literal lerp of the opacity, not
the "false → render normally (1.0)" rule `OBJECT_OPACITY_STATE` uses. `opacity_delta` is null in
100% of them, so nothing here says what it would have meant (unlike
`OBJECT_MOTION_FROM_TO`'s `*_delta`, which ships values and is decoded below). Opacity is a
separate channel from the transform, so a fade coexists with a live motion on the same node (the
crash `dust` scales and fades at once). **Only ONE of the 9,917 is ever reached at bootstrap**,
C3's `spiderweb_gone` (`ON_STARTUP`, fades `spiderweb` 1→0 over 0.7 s so the web is gone by
default); the rest are `ON_CALL`/`WEAPON_HIT`, fired by a crash or (in M3) a kill.

**What is reachable:** 683 dispatches across C1/C3/C4/C5, **zero unresolvable**. C1's
`cloudparent#`, `ON_STARTUP`, `EXECUTION_BY_RANGE 1900`, a `LOOP{-1}` re-asserting every frame,
sets the cloud sprites to **0.6**, and is the single largest use in the game; C5 sets `wl_glw`
and `cfglow` to 0.4; C3/C4 drive the barrage balloons (`bont*`/`balloon_t*`/`tether*`) and
`bhf_support*`, all at `state=false` (i.e. normal).

⚠ **Observing the C1 clouds needs two things switched off**, which is why a first pass wrongly
concluded the event had no visible effect: the opaque `cloudlayer` **`CloudDeck` occludes them
from below**, and at any normal viewing distance **fog washes them to exactly `FOG_COLOR`**. With
the deck hidden and fog off (static `--viewer --chapter=C1`, no `--sky-zone`) the effect is
obvious, 74,129 px change, the clouds going from hard opaque white to translucent.

### `FogState`, an inline fog written over the zone

The compiled archives carry a `FogState` event kind: mid-mission weather change is a real engine
capability. **The data uses it exactly once install-wide**,
`extracted/C1/M04/mis_anim/camera1-mission_intro_animation.json`, `reset_state/events[4]`
(across all 12,746 `mis_anim` + 3,368 `cam_anim` files):

```json
{"FogState": {"name": "drop_fog", "type_": null,
              "color": {"r": 0.69, "g": 0.69, "b": 0.69},
              "altitude": {"min": 10000.0, "max": 11000.0},
              "range": {"min": 1000.0, "max": 1500.0}}}
```

Note what it is *not*: it carries its fog parameters **inline** and matches neither of C1's
weather.json zones (zone1 1000–1750 alt 970–1047, zone2 1000–4000 alt 4000–5000). So it is an
ad-hoc third fog state applied to a cutscene camera, **not a zone selector**, it does not answer
"which zone does a mission fly", which remains engine-side (see
[weather.md](weather.md#mission-zone-selection)).
**What the handler does** (dispatch slot 28, `FUN_004e8540`): it tests a flag byte at event
`+0x2c` and, per set bit, calls one of the four fog setters the zone apply itself uses on the same
fog record: bit 1 the fog type (`FUN_004da800`, `type_`, null in the shipped event), bit 2 the
colour (`FUN_004da820`), bit 4 the altitude pair (`FUN_004da850`), bit 8 the range pair
(`FUN_004da870`). Each setter writes its fields and raises a dirty bit on the record; a field the
event omits is left as the last writer set it. The handler returns 2 (complete) at once and takes no
`instant` branch, so it fires from a `RESET_STATE` walk exactly as from a sequence, which is the
only way the shipped use ever fires. The zone apply (`FUN_00472ea0`) writes the same record on a
camera-state edge, so the two are in last-writer order: the intro's reset writes `drop_fog` over
the zone at load, and the next camera-state edge writes the zone back. One difference in what is
written: the zone apply multiplies its range by the detail level's `FOG_SCALE` (1.0 at HIGH);
the event's range goes in raw. Decode notes: [org/weather.md](../org/weather.md#the-fog_state-animation-event).

CSVM: `AnimRuntime` raises the event through `FogStateSink` (`AnimRuntime.FogStateChange`, one
nullable field per flag bit) and `Session.World.WeatherRig.ApplyFogState` writes the carried fields onto
the `csky_fog_*` globals; an event raised inside the world bootstrap, before the rig exists, is held
and applied after the zone. The next fog-zone edge re-applies the zone over it. The `fog-state`
suite pins it on this definition. The `altitude` pair is read as `FogLow`/`FogHigh` in
`min`/`max` order, the reading mech3ax's field names give; nothing in the shipped use distinguishes
the two, since both sit far above any C1 flight.

### `OBJECT_MOTION` is two ops sharing one event

`OBJECT_MOTION` is the original's rigid-body descriptor, and its 7,442 uses split cleanly into
two jobs that have nothing to do with each other. Across all 8 chapters:

| Shape | Count | What it is |
|---|---:|---|
| `XYZ_ROTATION` only (± `RUN_TIME`) | 3,521 | A **steady spin** about the node's own axes. |
| `+ GRAVITY`/`TRANSLATION[_RANGE]`/`FORWARD_ROTATION`/`BOUNCE_SEQUENCE` | 3,807 | **Ballistic debris** thrown by a kill. |
| `SCALE` only | 114 | A scale ramp (`ballflare`, the explosion flare, 65→75 over 1.75 s). |

**The split is exactly the reachability boundary.** All 590 `ON_STARTUP` uses are
rotation-only; every ballistic use is `ON_CALL`/`WEAPON_HIT`, fired by a crash or (in M3) a kill.
So the rotation half runs through the lightweight `AnimRuntime.SpinMotion` (unchanged, the
ambient world boots byte-for-byte the same), and the ballistic/scale/tumble half is **implemented
** through `AnimRuntime.MotionRuntime`, a full rigid body in the node's parent frame,
seeded from the node's **live** pose (the original's update adds each step to the node's own
translation and writes no start position, so a chain of events on one node continues leg from
leg; a pooled effect copy is returned to its spawn pose by the checkout, not by the launch):

- `TRANSLATION.initial` is the launch **velocity** (a crash piece leaves at y=10 m/s); `rnd_xz` is
  the compiled launch **direction**, the cache the tumble reads back, and **not a random spread**:
  the source spells `TRANSLATION azimuth elevation speed delta` and the parser compiles it through
  the same polar construction as `TRANSLATION_RANGE`, so `initial = direction × speed` and nothing
  on the vector form is drawn at runtime (the name is mech3ax's, [`../org/objectMotion.md`](../org/objectMotion.md));
  `delta` a constant **acceleration** along the launch, in m/s², **not** a ramp
  divided by `RUN_TIME`, which is how it was once read (non-zero on 92 of the 757
  vector-form events).
- `TRANSLATION_RANGE` is a ballistic launch in **polar form**, **`xz` is an AZIMUTH and `y` an
  ELEVATION, both in DEGREES, and `initial` is the launch SPEED in m/s** (`delta` the same constant
  acceleration, non-zero on 233 of 1,226). This is an acceleration, not a distance reading:
  threw debris hundreds of metres; census + evidence in the retired `analysis/object-motion-range/`
  (`git show analysis-archive:analysis/object-motion-range/FINDINGS.md`).
  ⚠ **The elevation is LINEAR, not spherical**, and that was corrected: the direction
  is `dirY = elevation/90` with the horizontal taking the remainder `1 − |elevation|/90`, so it is
  deliberately **not unit length** (0.707 at 45°) and only the azimuth goes through trigonometry. Do
  not normalise it, the unit-sphere reading launches 60–70° debris 20–25 % too fast. The mechanism
  is in [`../org/objectMotion.md`](../org/objectMotion.md). Over all 1,217 events install-wide
  every `xz` lies in [−170, 359] and every `y` but one in [−90, 90]; `y` goes negative exactly where
  the thing falls (a balloon turret's parts at −70…−90 against a collapsing dock platform's +70…+80);
  the five `fly_trailN` of one explosion carry evenly spaced azimuth bands (35–55, 85–105, 135–165,
  185–205, 235–255), the starburst the original's HE impact shows; and `gunshell` reads ±10° of
  bearing at −75…−85° elevation and 1.5–1.8 m/s, i.e. brass dropping out of the gun port.
  `TRANSLATION_RANGE_MIN_ONLY` (112 events) marks rows whose `max` fields are all 0 and meaningless,
  there the min IS the value. ⚠ 623 of 3,651 ranges have `min > max`, so interpolate rather than
  clamp. ⚠ Which world bearing azimuth 0 points along (+X in the engine) is a choice, not a decode:
  the data fixes the trails' spacing relative to each other, never their absolute compass.
- `GRAVITY.value` (negative) accelerates the launch, and is an **absolute m/s², not an offset to the
  aircraft's arcade `nom_gravity` of 20**, the census carries a literal −9.8 on 173 events (and −10
  on 400); the weak −1/−2/−3 values sit on smoke trails, where floating is the authored look.
  ⚠ **A ground-contact test is the DEFAULT, and `DO_INTERSECTIONS` only upgrades it**, from a
  vertical column under the body to a full geometry sweep, which is what lands a piece on a rooftop
  or stops it against a wall. `NO_ALTITUDE` is the opt-out and `gunshell` alone authors it. This was
  read the other way round, the test gated on `DO_INTERSECTIONS`, `NO_ALTITUDE` as something about
  spawn altitude; the mechanism is in
  [`../org/objectMotion.md`](../org/objectMotion.md). Every gravity-bearing body therefore lands,
  including the free-falling shapes (zeppelin gasbags, lifeboats, `chuteman` descents) that had been
  deferred as `BL-245` on the older reading.
  A third launch shape is worth knowing: **neither `RUN_TIME` nor `BOUNCE_SEQUENCE`**, where the
  event's duration is what the sequence's NEXT (null-start) event waits on, in 159 of 167 cases
  install-wide, the flying piece's own `ACTIVE_STATE 0`. Censused (`BL-257`,
  `analysis/bl-257-nulled-launch/`): a body with no `RUN_TIME` reports the parabola's return to
  launch height as its duration, so these are hidden when they land rather than on the launch tick.
  `dblcannon_flying_parts` is the reachable case, eight parts at elevation 10–70°, 17–25 m/s, each
  switched off by its own following event. ⚠ 8 of the 167 have no apex and report no duration; the
  vertical speed is `(elevation/90)·speed`, so **a negative speed range inverts an upward elevation**
  (`fuelboxbreaks`' rockerarm, elevation 90° at speed −45…45).
- `FORWARD_ROTATION.Time.initial` is a tumble **RATE in rad/s** and `delta` its acceleration, so
  `RUN_TIME` never enters the derivation. The compiled numbers fly as they are, the reader's
  authored `initial` is converted deg→rad at parse and `delta` stored raw. **The axis is decoded,
  not chosen**: the body turns about the horizontal perpendicular of its own `TRANSLATION_RANGE`
  launch direction, unnormalised, so its length is that launch's `1 − |elevation|/90` and a steep
  throw turns slowly off the same number.
  ⚠ A `TRANSLATION` (vector) launch never fills that direction, so those bodies **do not tumble at
  all**, 495 of the install's 1,399 tumbles, the `player_crash_dirt` pieces among them. ⚠
  `FORWARD_ROTATION DISTANCE` (the same shape driven by the step rather than by time) is authored
  **nowhere** in this install: all 1,399 author `Time`. Mechanism and addresses in
  [`../org/objectMotion.md`](../org/objectMotion.md).
- `SCALE.initial`/`delta` a linear scale ramp that is an **OFFSET from unit scale, not an absolute
  size**: `scale = 1 + initial + delta·u`. Unlike `OBJECT_SCALE_STATE`/`OBJECT_SCALE_FROM_TO`, which
  are absolute. **Settled** by the install's commonest value, a bare `(-0.1, -0.1, -0.1)`
  with zero delta on **30 of the 45 distinct SCALE events** (every `h2twr`/`radiotwr`/`transmitter`
  collapse, every `gullfly`): as an absolute that is a *negative* scale, i.e. the piece inside-out at
  a tenth of its size and effectively invisible; as an offset it is a clean 10 % shrink. Confirmed
  visually, killing C1's `ap_h2otwr1` under the absolute reading leaves only the legs standing (the
  tank and roof sections vanish), under the offset reading the tank tumbles away intact. The crash
  `dust` ramps (4.5,11,4.5) → (3.5,6,3.5) over 6 s. ⚠ The base is 1, and whether it should instead be
  the node's own authored scale is **undecided**: every node carrying this channel is authored at
  exactly unit scale in this install, so the two readings coincide and nothing can separate them.

Verified in `--anim-lab --play-anim=player_crash_dirt` (seeded, fixed-dt): the five
`fly_trail*` debris anchors integrate outward and the two carrying `FORWARD_ROTATION` tumble while
the others do not (those two are `TRANSLATION_RANGE` launches; the crash's own `pieceN`, which fly
the vector form, stopped tumbling), `dust` runs a scale ramp and an `OpacityFade` at once, `flydirt`
sinks on its `TRANSLATION`. An 8-chapter ambient regression is byte-identical bar C3's one reachable
opacity fade, because nothing ambient fires the ballistic half (see the reachability boundary above).

What the reachable spins are: 576 of 590 are zeppelin nacelle propellers, `spin` at −40°/s and
`counterspin` at +30°/s about local Z, counter-rotating, plus rotating signage (`ammosign`,
`jsign` at 24°/s about Y) and a few `prop`/`rotor` pairs. The companion `propoff` sequence
deactivates the static `propstill` disc and activates the `spin`/`counterspin` blur meshes: the
same static-disc-vs-blur-layer split `PlaneBuilder` already does for the player's own aircraft.

**Units diverge between the front-ends, as usual.** The reader writes one flat six-number list
in **degrees/second**, `XYZ_ROTATION [0, 0, -40, 0, 0, 0]` = initial triple then delta triple;
the compiled form nests it as `{initial:{x,y,z}, delta:{x,y,z}}` in **radians/second**
(−0.6981317 = −40°). Converted once in `AnimDefs.Spin`, the same way `PLAYER_RANGE` (m vs m²)
and `ANIMATION_LOD` (`HIGH` vs `2`) are.

**`initial` is a rate, not a pose**, the question worth settling, since `delta` is zero in 589
of the 590 reachable events, which would make them all *static* under a pose reading. The
autogyro's destruction tumble writes `XYZ_ROTATION [55, 20, -175, 0, 0, 0]` on a wreck falling
under `GRAVITY [COMPLEX, DO_INTERSECTIONS]`; a falling wreck with a fixed pose is not a thing,
and the sequences are named `spin_rotor`. Verified in flight: the rendered prop advances
−40.5°/−81.0° at 1 s/2 s, i.e. exactly the authored −40°/s.

**The player aircraft's own prop/rotor discs (`Mech3.PropParts`) go through the same
conversion.** Their rates are the authored `spin_rotorN`/`spin_rotorNb` values from
`plane_props.json` (`spinprops`) and `autogyro.json` (`agyro_rotors`), settled fact, not a
tuned guess: `PropAnimator` converts them via the identical `Mathf.DegToRad` +
accumulate-from-rest path this section's `AnimDefs.Spin` does, and the rendered rotation
matches the authored deg/s exactly, the same check made above for the ambient spins.

**`delta` is NOT decoded and is deliberately not guessed.** It reads as acceleration on a blown
chassis (`+15°/s` added to a 30°/s spin), as a *decelerating* ramp on `chuteman_sway`
(initial `(-10,0,10)`, delta `(+10,0,-10)`, `RUN_TIME` 2, a parachutist swaying back), and
could equally be a random spread, which this data uses elsewhere. Nothing reachable needs it, so
it is counted as `ObjectMotion(rotation delta)` and reported, the same call `Object3DRotate`'s
ambiguous angle unit got in `interp.md`.

### Transform channels are absolute; rotations are radians compiled, degrees in the sources

**Every transform channel, `translate`, `rotate`, `scale`, in both the `*_STATE` events and
`OBJECT_MOTION_FROM_TO`, is an absolute value in the node's own parent frame, not an offset
from its authored rest pose.** Nothing in this data is relative: the `*_delta` channels that
look like they would be are the same tween's rate, see the `*_delta` section just below.

Evidence, surveyed over all 8 chapters :

- C1's `mafia` car moves `from (-6796, 128, -5958) to (-6521, 128, -5958)`; its gamez node's
  authored translate is `(-6795.828, 128.0, -5956.72)` and its parent is the `world1` root.
  Adding the channel to the rest pose put the C1 traffic at `(-13574, 256, -11914)`, exactly
  double, y included, i.e. ~7 km off the map.
- C5's `m_gerter` crane hook moves between `(21.3, 19.9, -20.2)` and `(21.3, 7.9, -20.2)`,
  small numbers because its parent is `m_crane`. C1's `sinker` moves `(0,0,0) → (0,-7.5,3.5)`
  in its parent boat's frame.
- For `OBJECT_TRANSLATE_STATE`, 627 of the resolvable uses have `STATE` **exactly equal to**
  the node's authored rest translate, the doubling case again. The 367 zero-valued uses are
  no-ops only because those nodes rest at the origin.
- "Does `from` match the node's rest pose" is a **bad test**: nodes whose animation is what
  places them (C2's `sailboat2`, C1's `car_go_home`) rest at their parent's origin and
  legitimately don't match. Absolute-in-parent-frame is the rule that covers both families.

Rotation units differ between the two spellings (the C2 roadblock spin):

- **Compiled** archives are **radians**: the maximum magnitude in that data is `15.708 = 5π`,
  99.93% of values are ≤ 2π, and 228 sit on exact π/2 multiples. Converting them with
  `DegToRad` makes every rotation ~57× too small, visually, nothing turns.
- **Reader (zrdr) sources** are **degrees**, the same reader↔compiled divergence as
  `XYZ_ROTATION`, `PLAYER_RANGE` (m vs m²) and `ANIMATION_LOD` (`HIGH` vs `2`). Surveyed over
  every `.zrd.json` in the install: 1,388 of 1,428 nonzero `OBJECT_ROTATE_STATE`/`ROTATE_FROM`/
  `ROTATE_TO` values exceed 2π, maximum 900, on clean multiples of 5°/15°/45°. C2
  `police_blockade`'s `[0,135,0]` against its compiled `mis_anim` twin's `2.3561945` (= 135°)
  settles it, fed through unconverted, the roadblock swerve spun each car ~9 turns. The
  remake's reader front-end (`AnimDefs`) converts at parse, so handlers see radians from both.

**An absent `OBJECT_MOTION_FROM_TO` channel means HOLD the node's current value**, not
"return to the authored rest pose", the reader spelling's "a missing FROM means from
wherever the object currently is", generalised to the whole channel. Surveyed install-wide:
883 of 1,802 FROM_TO events carry no rotate channel, and on 89 of them (26 nodes) the held
value differs from rest, C1's traffic and firetrucks, C2's ten studebakers, its sailboats
and yachts (up to `sailboat1`'s 300 s leg held 180° from rest). Seeded from rest, C1's
`black_car1` (one ROTSTATE 180° then a single 16 s translate-only loop) drives its whole
route exactly sideways.

### A `from` pose is written on the event's first dispatched tick, never earlier

Decoded from `crimson.exe`. `OBJECT_MOTION_FROM_TO` is dispatch slot 11 (`FUN_004e9ee0`) of the
47-slot table at `DAT_00727de0` that `FUN_004ee1a0` populates, the slot after `OBJECT_MOTION`'s
`FUN_004e8fa0`. Its channel flag word is at `event+0xc`: `0x1` translate, `0x2` rotate, `0x4` scale,
`0x8` morph. The handler writes the `from` pose only on its first call, guarded by the sequence run
state at `run+0x20` being zero, and in that same call it also integrates one frame of the authored
`*_delta` rate. The `to` pose is snapped on the call where the per-event clock at `run+0x28` reaches
`RUN_TIME` (`event+0x8c`), which is the call that reports the event finished.

That first call lands at the event's own start time, because the sequence stepper (`FUN_004ecbb0`)
is a cursor rather than a scheduler. Between events it reads the next event's start-time mode at
`event+1` and value at `event+8`, comparing against the animation instance clock (`inst+0xb0`,
mode 1), the sequence clock (`run+0x24`, mode 2) or the per-event clock (`run+0x28`, mode 3). Until
the gate opens it returns without dispatching, so the handler does not run and every event behind
the cursor waits with it. There is no run list a pending event could be posed from ahead of time.

⚠ **Nothing else writes the node's pose while a gated motion waits.** `OBJECT_ACTIVE_STATE` (slot
6, `FUN_004e8f40`) calls `gwNodeSetActive` (`FUN_004cca30`, named by its own error string), which
only toggles bit `0x4` of `node+0x24`. So a definition that switches a node on at t=0 and starts a
collapsed-`from` scale motion at t=1 draws that node for the whole second at whatever posed it last,
which is normally another definition's `RESET_STATE` rather than the archive's own pose; the docking
hooks are the worked example in
[`anim-definitions/cutscenes.md`](anim-definitions/cutscenes.md).

A `LOOP` rewind is the one re-entry that does not rewrite `from`. `FUN_004ebfd0` calls the sequence
reset `FUN_004ebfa0` and returns state 4, which the stepper stores at `run+0x20`, and the gate block
treats 4 like 0 without clearing it. The next dispatch therefore sees a non-zero run state, so a
`FROM_TO` event re-entered by a loop tweens from wherever the node currently stands. No shipped hook
definition loops, so nothing in the landing path depends on this.

### `*_delta` is the same channel's RATE, not a second motion

Decoded from an install-wide census of all 16,114 compiled anim files
(the retired `analysis/bl-050-fromto-delta/`,
`git show analysis-archive:analysis/bl-050-fromto-delta/FINDINGS.md`). The compiled `*_delta` channels arrive as a bare `{x,y,z}`
vector, **not** a `{from,to}` pair, on **51** events (15 `translate_delta`, 17 `rotate_delta`,
19 `scale_delta`; 29 in `cam_anim`, 22 in `mis_anim`; 33 distinct authored signatures). The
reader sources spell no `*_DELTA` token at all, 0 of 1,355 reader JSON files contain the
substring, so this is a compiled-form-only field.

**`*_delta == (channel.to − channel.from) / run_time`**, i.e. the sibling absolute channel's
per-second rate, precomputed by the original's compiler. Verified against all 51: zero
mismatches, worst relative residual 4e-6 (float32 rounding), and **every one of the 51 ships
the absolute channel it is the rate of**, there is no event the delta alone describes. It
holds on the awkward cases too, which is what settles it: C3's `studebaker4` swerve carries a
non-axis-aligned rotate `(−0.7156, 0.8552, 0) → (−0.5236, −0.5236, 0)` over 0.35 s and a delta
of `(0.5485, −3.9395, 0)`; C5's `litemast_dest`/`wire2` scales `(1,1,1) → (0.7, 0.1, 8)` over
2.2 s with a delta of `(−0.1364, −0.4091, 3.1818)`.

⚠ **So a consumer must NOT read it.** It carries no information the tween does not already
have, and composing it as an extra offset, the reading a bare vector invites, "the `to` with
an implied zero `from`", runs every one of these 51 motions at double speed. CSVM reads the
three absolute channels only (`FromToMotion`); the delta plumbing was removed so a
later reader who notices a `{from,to}` parser returning `(null, null)` on a bare vector does
not "fix" it back.

### `IF`/`ELSEIF` conditions are all evaluable

Ten condition kinds appear across the install. None of them is opaque gameplay state that a
branch. **All ten are evaluated** (`AnimRuntime.EvaluateCondition`).

⚠ **The engine has fourteen, and the missing four are unused data, not unimplemented code.**
`PLAYER_UNDERCOVER`, `PLAYER_BELOW_ALT`, `PLAYER_LINED_UP` and `PLAYER_SPEED` have parser branches
and evaluator branches in `crimson.exe` and are authored nowhere in the install (swept:
0 occurrences against 1,097 for `PLAYER_RANGE`). The full bit-to-token table is in
[`org/sequences.md`](../org/sequences.md); this table is the census of what ships, and the two do
not disagree.

The compiled payload is a one-key union under `data.If.condition`, e.g.
`{"AnimationLod": 2}`; the reader spells the same conditions with its own vocabulary and, for
two of them, **different units**, that conversion happens once, in `AnimDefs.ReaderCondition`,
so the runtime has a single convention.

| Count | Condition | Reader spelling | Rule used |
|---:|---|---|---|
| 4537 | `RandomWeight` (0..1) | `RANDOM_WEIGHT [w]` | `draw <= w`, re-rolled per evaluation. The original draws from a 200-entry ring that is itself `rand()`-filled per run, so there is no sequence to match, see [`org/sequences.md`](../org/sequences.md)'s condition flag word. |
| 4007 | `AnimHealth` | `ANIM_HEALTH [n]` | `health <= n`, "worn down to n". Full health in a world build, so uniformly false. |
| 1052 | `PlayerRange` | `PLAYER_RANGE [m]` | `dist²(anchor, player) <= value`. **Compiled is metres SQUARED** (reader 270 ↔ compiled 72900, exact across the install; the parser squares it, confirmed in the exe). No scale factor rides on the comparison. |
| 717 | `NodeActive` | `NODE_ACTIVE [name]` | the node is visible. Compiled carries an INDEX, reader a name, see below. |
| 473 | `NodeUndercover` | `NODE_NEAR_GROUND [name, d]` | a vertical probe of `d` metres from the node meets world geometry. `d` is SIGNED and arrives as a float bit pattern, see below. |
| 124 | `AnimHealthRange` | - | `min <= health <= max`. Same as `AnimHealth`: false at full health. |
| 120 | `AnimationLod` | `ANIMATION_LOD [HIGH]` | `ourLod >= n`. **Our setting, not the data's**, see below. |
| 120 | `PlayerFirstPerson` | `PLAYER_1ST_PERSON` | our camera mode: true while any human pilot has the Cockpit or Nose view selected, false with no view seam wired (a lab, a test). |
| 28 | `NodeBelowAlt` | `NODE_BELOW_ALT [name, alt]` | node world Y < alt. |
| 17 | `HwRender` | `HW_RENDER` | true. |

`HW_RENDER` and `PLAYER_1ST_PERSON` take **no reader argument** and every compiled instance
stores `false` in the shared 4-byte value slot, so that slot is unused for them and the
condition is the runtime flag itself. (The one piece of counter-evidence is C1B's
`four_bulletholes`, whose branches read backwards under that rule, but it is an `ON_CALL`
player-cockpit def that never runs in a world build, so nothing turns on it.)

`AnimationLod` is a **quality setting**: 2 is the only value anywhere in the install (the
reader spells it `HIGH`), so the project defaults to 2 and every LOD-gated branch passes,
the hardware has no reason to hide detail the original hid only for performance.
`--anim-lod=N` lowers it for A/B comparison.

**`NODE_UNDERCOVER`'s `distance` is a signed length in metres carried as a raw u32.** The compiled
record's shared 4-byte value slot holds the IEEE-754 bit pattern, and the extraction types the
field as an unsigned integer, so it is not a length until it is reinterpreted. All 473 shipped
occurrences decode to round values:

| Raw u32 | Metres | Uses |
|---:|---:|---:|
| 3229614080 | −4 | 354 |
| 3258449920 | −46 | 30 |
| 3263299584 | −65 | 22 |
| 3262119936 | −60 | 20 |
| 3259498496 | −50 | 18 |
| 1107296256 | +32 | 8 |
| 3245342720 | −15 | 8 |
| 3266052096 | −86 | 5 |
| 3265265664 | −80 | 3 |
| 3251109888 | −25 | 3 |
| 3263430656 | −66 | 1 |
| 3261071360 | −56 | 1 |

The sign is the probe direction: negative casts DOWN, which is the reader spelling
`NODE_NEAR_GROUND`, and positive casts UP, which is "undercover". `chuteman` is the definition that
shows both in one chain, and it is the only carrier of the eight positive uses: `If` the ground is
within 15 m below, deactivate the parachutist; `Elseif` something is within 32 m above, deactivate
him; `Else` deploy the chute and start his drift. C1/M04's `killpzep` is the plainest negative
carrier, gating the whole zeppelin breakup on 65 m under the hull and each engine's own break on
4 m under that engine. The probe itself, with the addresses, is in
[`org/sequences.md`](../org/sequences.md).

**Condition node references are 1-based indices into the definition's own `nodes` support
array**, not gamez node indices and not names, mech3ax resolves index→name for every other
event kind but leaves conditions raw. Two negative sentinels ride in the same u32 field:
`-100` `MAIN_ROOT_NODE` and `-200` `INPUT_NODE` (arriving as 4294967196 / 4294967096), both
meaning "the node this definition was invoked on" = the anchor. float32 JSON parsing cannot
tell those two apart, which does not matter since they resolve identically.

This matters because the branches gate real content: C1's `refinery_fire_always` and
`ref_light_always1..6` wrap their entire light sequence in `If { AnimationLod: 2 }`,
`litehouse_sparking` gates its spark bursts on `If { RandomWeight: 0.7 }`, and C1/MP1's
`rearm_node_1/call_door` is a **poll**, `If { PlayerRange: 625 } → CallAnimation; Endif;
Loop{-1}` — that fires the rearm-bay door when the player closes to 25 m. Skipping branches
meant none of it ran.

That poll idiom pins down one more semantic: **`CALL_ANIMATION` does not restart an animation
that is already running** on the same anchor. The call is re-issued every frame the condition
holds, so restarting would freeze the 2 s door at its first frame for as long as you hover.

**The forward skip counts no nesting depth, and nesting ships.** `FUN_004ec080`, on a false
condition, walks forward event by event and breaks at the **first** `ELSE` (0x20), `ELSEIF` (0x21)
or `ENDIF` (0x22), landing on an `ELSEIF` it loads that condition and re-tests it in the same loop;
landing on anything else it returns and the stepper steps past. The `ELSE`/`ELSEIF` fall-through
(`004ec5a0`, shared by both opcodes) likewise scans to the first `ENDIF`. Neither keeps a depth
counter, and `ENDIF` (`004ec5d0`) is inert, so a nested chain is not skipped over, it is walked
into. **48 sequences nest**, all one shape, in every chapter's `gunhit-*slug_gunhit` and
`mag_gunhit-*`, played on every gun impact:

```
If AnimationLod 2 / If PlayerRange 1000000 / If RandomWeight 0.2
    LIGHT_STATE gunhit_lt (range 0.5..21.25) ; OBJECT_ACTIVE_STATE gunhit_lt off
Elseif RandomWeight 0.2
    LIGHT_STATE gunhit_lt (range 0.9..12.25) ; OBJECT_ACTIVE_STATE gunhit_lt off
Else Endif / Else Endif / Endif
```

Both `ELSE` bodies are empty, which is what makes the missing depth counter observable: a false
`ANIMATION_LOD` or a false `PLAYER_RANGE` lands on the inner `ELSEIF` and **re-tests it**, so the
dimmer impact light still fires on its 20 % roll with either outer gate failed. A nesting-aware
reading skips the whole block and fires nothing. The original's reading is the one CSVM runs
(`SequenceRunner.Scan`); it reads like a compiler bug and it is the shipped behaviour.

No `RESET_STATE` in either source contains control flow (verified across the install), so the
instantaneous base-state pass never has to interpret a branch.

Playback ops seen and deferred: `SOUND` (the one-shot form, see below),
`OBJECT_CYCLE_TEXTURE`, `CAMERA_STATE`, `DETONATE_WEAPON`.
(`FBFX_COLOR_FROM_TO` landed, see [`org/sequences.md`](../org/sequences.md)'s
"FBFX_COLOR_FROM_TO is a full-screen wash";
`LIGHT_STATE`/`LIGHT_ANIMATION` landed, see below;
`SOUND_NODE` + the sound half of `OBJECT_ADD_CHILD` landed, see "SOUND_NODE is a
three-event triple", and the node-reparent half of `OBJECT_ADD_CHILD`/`OBJECT_DELETE_CHILD`
with it, see [`anim-definitions/cutscenes.md`](anim-definitions/cutscenes.md)'s
"The reparent is how a cutscene is composed"; `OBJECT_MOTION`'s rotation half landed and its
ballistic/scale/tumble half, see "OBJECT_MOTION is two ops in one";
`OBJECT_OPACITY_STATE` landed and `OBJECT_OPACITY_FROM_TO`, see
"OBJECT_OPACITY_STATE is translucency";
`CALLBACK` landed for the two vehicle-death codes, 16 and 15 (see
[`org/vehicleDamage.md`](../org/vehicleDamage.md)'s "What happens to the wreck"), while every other
authored code is counted and ignored.)

`CALL_ANIMATION` dispatched from the start but **ignored its target node** until,
see "CALL_ANIMATION carries a target node" below; that is the data's template-instancing
mechanism and `OBJECT_ADD_CHILD` is **not** (surveyed: the fire templates are never its
children, and 75% of its 1,152 uses attach sound *definitions* rather than nodes).

## Animation calls

Surveyed and implemented. A call may name **another node to run the callee on**,
and that is how one authored definition serves many sites. Three spellings, two shapes:

| Reader | Compiled | Uses |
|---|---|---|
| `WITH_NODE [name]` | `parameters: {"WithNode": {node, position}}` | 29,633 |
| `AT_NODE [name]` | `parameters: {"AtNode": {node, position, translate}}` | 7,640 |
| `OPERAND_NODE [name]` | `operand_node: "name"` (bare, not nested) | 179 |

`parameters` is a one-key union, so `AnimData.Union()` reads it directly. The reader front-end
normalizes `WITH_NODE`/`AT_NODE` into the compiled shape (`AnimDefs.AddCallTarget`) so the
runtime has a single path. **The target is written in the CALLER's namespace** and resolves
through the caller's definition and scope.

Why it matters, with the case that proves it: C1/M05's eight zeppelin engines each run
`stop_wvzreng1..4`/`stop_wvzleng1..4`, and each calls the **generic** definition `gen_zep`/
`random_prop`, whose whole body is "rotate the node named `propstill` to a random angle",
targeting its own `propstill`. Ignore the target and all eight calls resolve `propstill`
globally to whichever one is first, so eight engines share one prop angle. Honour it and each
engine gets its own. The same mechanism is what places effect templates:
`CALL_ANIMATION [NAME [huge_30sec_fire], WITH_NODE [rc*_dbase1]]` burns one ship section.

The two node spellings differ in what the callee receives, not in where it resolves: both write
the same reference slot, and the handler delivers a node or a position by its flag bits (the
parser at `FUN_00515c00`, [org/sequences.md](../org/sequences.md)). A carried site is where the
question matters. A zeppelin's gun ring dies with `large_30sec_fire WITH_NODE doublecannon4
(0, 2, 0)` and `large_fireball`/`dblcannon_flying_parts AT_NODE doublecannon4`, and at the
original's controls the whole death, the fire, the fireballs and the debris, moves with the hull
as it flies on. The runtime places a death's callee to follow its call site under either spelling
(`TemplateStage.PlaceFollowing`, docs/architecture.md); a site that never moves reads the same
either way.

Two implementation notes. Instance identity is `(definition, anchor)`, so retargeting is also
what lets one definition run concurrently on many sites, the `IsLive` check must use the
**resolved** anchor, not the caller's, or the second site is swallowed as a duplicate. And an
unresolvable target falls back to the caller's anchor rather than dropping the call, so a node
the builder skipped cannot make an effect vanish; the fallback is counted and reported.

**There is no alternate call-target index form.** Animations are dispatched by name string
everywhere in this data, relevant because the four `fire.zrd.json` definitions are called by
nothing (see below). The compiled wait index described next is symbol-table bookkeeping for that
same named callee, not another way to choose the target.

### `WAIT_FOR_COMPLETION` blocks on the named callee

The reader's bare `WAIT_FOR_COMPLETION` token makes a call synchronous: the caller's sequence does
not advance past the call until that callee completes. Compiled e24 stores flag `0x10` plus a
zero-based index into the **caller's** `anim_refs` table. The index does not select a different
connector: across all **3,731** flagged `CallAnimation` events, every index is in range and every
indexed ref names the call's own `name` (including all 92 nonzero indices). Thus `0` and `null` are
different authored states: `0` waits for ref zero; `null` has no wait flag and returns immediately.

Install-wide compiled distribution: `null` ×53,019, `0` ×3,639, `1` ×32, `2` ×19, `5` ×16,
`3` ×9, `4` ×8, `6` ×8. The reader sources carry the bare token on 147 `CALL_ANIMATION` bodies and
33 local `CALL_SEQUENCE` bodies. The e24 slot also contains **unflagged stale values** in Crimson
Skies; the fork exposes those separately as `wait_for_raw` (525 non-null events), and consumers
must not treat them as waits. Census and the 92-row dump instrument:
`analysis/wait-for-completion/`.

**The hold gates the caller's NEXT event, it is not a lifetime hold on the sequence.** Implemented
 (`BL-228`), and the scope is the data's, not a decision: censusing what each flagged call
is asked to wait FOR (`analysis/wait-for-completion/`, `callee_shapes.py`) splits the 2,852 flagged
calls in the blocks the runtime executes into a cross-tab with an empty cell.

| | callee never terminates | callee terminates |
|---|---:|---:|
| flagged call is the LAST event of its block | 2,662 | 25 |
| flagged call has events behind it | **0** | **165** |

Every flagged call naming a callee that never finishes is the last event of its block, and there
are only four such callees, `sputter_fire`, `sputter_black_smoke`, `sputter_fire_smoke`,
`gen_drop_ladder`, the `LOOP{-1}` sustain idiom. The reader front-end reproduces the same one-sided
split independently over its own 147 flagged bodies. Read as a lifetime hold, 2,770 authored calls
would wedge their sequence open forever; read as a next-event gate, the install is consistent with
zero exceptions. So only **165 calls (5.8 %), in 29 distinct (caller, callee) pairs**, can shift any
timing, median authored hold 3.0 s, longest 36.01 s (`start_gb3` → `cg1zepright_gasbag3`).

"Completes" is the callee's INSTANCE ending, every non-`OnCall` sequence it started plus everything
those reached by `CALL_SEQUENCE`, which is what makes the hold outlast the call's own t=0 burst. A
call that reaches no live instance (a callee whose whole choreography fires at t=0, 16 of the 165)
holds for nothing, by design.

**Decode status: confirmed at the state-machine level, not pinned to a `CALL_ANIMATION`-specific
address.** A handler returning **1** ("still running") from the stepper `FUN_004ecbb0` keeps the
caller re-dispatching the same event every tick without advancing, exactly the "gates the caller's
next event, not a lifetime hold" behaviour this census derived independently from data. The
`CALL_ANIMATION` dispatch slot's own handler address was not individually decoded (see the opcode
table in [`org/sequences.md`](../org/sequences.md)), so this is confirmed by the general contract
every handler obeys, not by reading the wait-specific code.

All flagged compiled owners are `OnCall` (2,844) or `WeaponHit` (8 in these blocks), never
`OnStartup`, so no ambient world boot arms one, measured: an 8-chapter `--freecam` regression arms
zero holds and leaves every bootstrap count identical. The clearest timing case is
`player_crash_water`, where flagged `plane_big_splash` precedes `large_steam_spray`. **That case is
reachable as of** (surface-aware crash selection), and a captured sea dive showed the
divergence directly: the two retargeted on the *same tick*, so the steam spray started with the
splash instead of after it. The splash's own choreography runs 3.0 s (`plane_sp_polys`' scale +
`plane_sp_polyfade`' opacity ramp); the landed hold measures **3.050 s** on real gamez data
(`wait-for-completion` suite). It is the only crash def in the install carrying the flag, the eight
chapters' `player_crash_default`/`player_crash_dirt` are all `null`.

One spelling is counted and deliberately NOT honoured: 33 reader `CALL_SEQUENCE` bodies carry the
bare token, which the compiled form never does (56,750/56,750 of the field's occurrences are on
`CallAnimation`) and so has no decoded semantics. The 879 flagged calls in the `unknown_seq`
destruction slot dispatch since `BL-276`, the slot is loaded as
`AnimDefinition.DeathSlot` and runs at death (see [destructibles.md](destructibles.md)), so its
flags behave like any dispatched call's (a routed effect call's hold is counted, not honoured).

**An `OBJECT_ACTIVE_STATE` pair around a call is a scope, not a lifetime.** The data's idiom for
"emit here" is three events with no `START_TIME` between them, activate a bare node, call the
emitter definition onto it, switch the node off again, and the author means the emitter to keep
running: the four splash definitions (`big_splash`, `huge_splash`, `med_splash`,
`plane_big_splash`, all on `sp_1`) wrap `hg_splasher`, which authors a 0.5 s `STOP_SEQUENCE` plus its
own `PUFFER_STATE 0` 0.1 s later. An absent `START_TIME` gates the moment the previous event
completes, not, as the encoding might suggest, `EVENT_OFFSET 0` (see "Event scheduling" below),
so all three land in one instant regardless, and a consumer that reads the deactivation as "stop the
emitters here" kills the effect on the tick it starts. The same-shaped pair a few seconds APART
means the opposite and is the far
larger population, a `partN` activated, given a debris trail, flown by a 3.5–5 s `OBJECT_MOTION`
and only then switched off, where that deactivation is the trail's ONLY authored stop. Censused
install-wide (`analysis/bl-229-emitter-host-deactivation/`): 414 pairs, 32 same-instant in 4 shapes
against 382 a median 3.5 s later, smallest later gap 1 ms, nothing in between. The reader source
carries the triple by hand, so it is an authoring idiom rather than a compiler artefact.

**Placing an effect template means moving its root.** An effect template hosts its puffers on
its OWN root subtree, `small_yellow_sparks`' puffer `at_node` is `yellow_spark_01`,
`call_crash_trails`' are `fly_trail1..5`, so re-scoping the callee's name resolution to the
call target is not enough: the template's root node must be relocated to the call site, or the
effect emits at the template's authored gamez origin. `AT_NODE`'s `position` is an offset in the
target node's frame, added to the site (world position is what matters, the puffers key off the
host origin). The original instantiates by *copying* the template mesh; a consumer that relocates
the single shared template instead must expect overlapping same-template calls to collapse onto
the last site.

**The offset triple needs no axis swap: it is already `(x, y, z)` in the mesh frame**, right-handed,
Y up, nose −Z, exactly the convention [gotchas.md](gotchas.md) settles for coordinates. Censused
over all 6,728 `AT_NODE`-style positions in the install (the retired `analysis/at-node-axis-order/`,
`git show analysis-archive:analysis/at-node-axis-order/FINDINGS.md`), across
every event kind that carries one: `CALL_ANIMATION`'s `parameters.AtNode.position`, `PUFFER_STATE`'s
`translate`, `LIGHT_STATE`/`SOUND_NODE`'s `translate.AtNode.pos`, `SOUND`/`DETONATE_WEAPON`'s
`at_node.pos`, and the reader's flat `AT_NODE [name, dx, dy, dz]`. 770 of them can tell a Y-up read
apart from a Z-up one, and the data is one-sided: `wv_turrets.zrd`'s `wvutur*`/`wvctur*` are the
same definition differing only in the sign of the middle component, applied to turrets sitting at
y ≈ +43…+57 on the gasbags versus y ≈ −30…−82 under a parent named `underneath`, the middle
component tracks up/down. `he_ground_effect` lifts its fireball 12 m over a ring whose mesh is
0.1 m thick; `muzzle_burst_*` puts its flash 1 m along −Z, out of the barrel; `shipsink` spreads
seven explosions over 165 m of a 231 m hull at constant height. **A reading that is 8 m too high is
therefore authored, not mis-parsed**, look at where the def's host was staged, not at the axes.

### The anim-list form counts the definition's callers

`ACTIVATION_PREREQUISITE OPTIONS [MINIMUM_TO_SATISFY n, ANIMATION_LIST …]` is not a general
start gate but a counter, and the counting is done by the callers themselves. Censused over
every `mis_anim` archive in the install, **18 definitions** carry the anim-list form, and each
one is called by exactly the animations its own list names, nothing else calls it. The list
is therefore the set of events that can advance the count, `MINIMUM_TO_SATISFY` picks which of
those calls the definition answers, and every earlier call is refused in silence.

| Carrier | Minimum | The callers it counts |
|---|---|---|
| `all_*zep_gasbags` (9 hulls), `all_wvzep_gasbags` | 3 of 5, 3 of 6, 5 of 10 | that hull's `finish_*gasbag*` burns |
| `finish_locklear` | 3 of 4 | `finished_lkgasbag0*` (C1/M04's `hk_zep`, which carries no zeppelin record) |
| `goose_cooked` | 4 of 8 | `g_engine_destroy1..8` (C2/M01's Spruce Goose) |
| `stein_first_sound`, `stein_second_sound`, `stein_third_sound` | 1, 2, 3 of 4 | `sbox_destroy1..4` (C5/M01), three radio lines over one set of supply boxes, staggered by their minimums alone |
| `cargozep_floatfree` | 5 of 5 | `breaktiedown01..04`, `breakmainclamp` (C4/M03) |

The count reads **started**, not completed. The last caller's own animation is on the list it
must satisfy, `breakmainclamp` calls `cargozep_floatfree` from its own first event, so a rule
waiting for completion would refuse the very call that completes the count, and nothing would
call the definition again.

CM18's release is what the mechanism is for and what an unenforced count costs. The Black Swan
is moored by four tie-downs and a main clamp; ungated, the first one shot tears it loose, and
`cargozep_floatfree`'s own clocks (`activate_cghookup_node` at +20 s, `start_gb1`/`2`/`3` at
+60/+125/+190 s, the last of which calls `too_late_to_hook`) run out while the player is still
shooting the other four. The hookup closes, and with it OBJECTIVE12, which is the only wake for
the mission's whole back half.

⚠ The count gates a `CALL_ANIMATION` and nothing else. A zeppelin hull death carrying this
shape is also reached from the zeppelin damage runtime, which owns that kill off
`num_healthy_required` rather than off the animation count (`ZeppelinRuntime.PlayHullDeath`
selects the def by this prerequisite's presence), and gating that path would leave every
zeppelin in the game unkillable.

### The node form reads the node's own live flag

`REQUIRED [OBJECT_ACTIVE_LIST …]` compares a node's live active state against the state the entry
asks for, and nothing about the node's placement enters it. The gate is `FUN_004edac0`, called
from the definition start `FUN_004ed8c0` immediately before the instance is created: it walks the
48-byte prerequisite entries (count at `def+0xe0`, `MINIMUM_TO_SATISFY` at `def+0xe1`, array
pointer at `def+0x100`), reads the kind byte at `entry+4` (1 animation, 2 node), and for a node
entry compares bit 2 of the flags word at `node+0x24` against bit 0 of the entry's state word at
`entry+8`. The node is reached only through the compiled pointer at `entry+0x2c`, and a null
pointer there skips the entry rather than refusing it. Bit 2 is the bit `gwNodeSetActive`
(`FUN_004cca30`) writes, so the gate reads exactly what `OBJECT_ACTIVE_STATE` wrote.

**A node's state at world load is its own gamez flag, copied verbatim.** `gClsBlockReadNode`
(`FUN_004e3690`, `cls_zbd.c`) reads the 212-byte node record and copies its first 208 bytes into
the live node object, so `node+0x24` is the record's own flags dword and bit 2 is
[gamez.md](gamez.md)'s `flags.active`. No transform is read and no parent is walked, so there is
no placement rule to agree or disagree with. Four authored channels move the bit afterwards, in
the order the bootstrap runs them: the mission's `support\<chapter>\<mission>.gw` `NodeSetActive`
([interp.md](interp.md)), a definition's `RESET_STATE`, an `ACTIVATION ON_STARTUP` definition, and
the mission's `startanims` list.

**What closes a fork is an authored channel, not the flag.** Censused over every `mis_anim` and
`cam_anim` archive, all 1028 node entries (784 REQUIRED, 23 of which ask for ACTIVE) name a node
its chapter gamez ships `active: true`, so a sensor that has to start closed is closed by data.
C1C/M01's docking sensors are switched off by the `wv_hookup_state` start animation, whose first
two events call `deactivate_pickup_node` and `deactivate_dropoff_node`, and OBJECTIVE11 and
OBJECTIVE15 wake them again. C1's `hdrop_direction` is switched off by its own definition, C4's
`blk_e_marker` and `fighters_destroyed` by `ON_STARTUP` definitions, and C2's `marypickford` and
C4's `lookat_sparkspkup` by `RESET_STATE` blocks. Where the sensor is a destructible's own healthy
variant the same channel holds it ACTIVE instead, so the fork runs until the thing dies: C1B's
`phut_healthy` and C2's `snkchk`. C4's `sparks_ready` is the one sensor no channel writes at load,
and its flag leaves it ACTIVE. The two entries that resolve to no chapter node name
`player/pylon1`, which lives in the plane archive as 23 copies, every one of them `active: true`.

**Distance from the world origin changes nothing, and shipped data says so.** Three forks sit far
out and still start closed: `hdrop_direction` under `hangar_3` at (−4336, 128, −6288),
`blk_e_marker` a world root placed at (−7724, 387, −2341), and `phut_healthy` under `powerhut` at
(−7717, 48, −5348). The remaining sensors hang under the origin-parked library roots
(`multiplayer1zep`, `piratezep`, `cargozep1`, `workersvoyagezep`, `tugandbarge01`, `trcar02`),
and `blk_e_marker` is the only REQUIRED entry naming a world root at all.

⚠ **CSVM applies `flags.active` at world roots only.** `WorldBuilder.Add` writes
`built.Visible = node.Active` on each child of `world1`, and `SceneBuilder.BuildSubtree` never
writes visibility, so a node below a world root builds shown whatever its flag says. Every
prerequisite node ships active, so the gate cannot see the difference, but 32 world nodes across
the eight chapters do sit in that gap.

## Sequence stopping

The wire format is identical to `CALL_SEQUENCE`, a 36-byte struct carrying only the name, and so
is the name resolution. `004eb610` resolves the name to an index in the definition's own sequence
array and then unconditionally writes that sequence's state byte to **2 (done)**. There is no
start-if-not-running path anywhere in the handler.

**`STOP_SEQUENCE [NAME [x]]`: halt every active runner of sequence `x` on this instance,
including the sequence carrying the event.** Because `CALL_SEQUENCE` can only start a sequence
from the parked state (3), stopping an `ON_CALL` sequence that was never called *disables* it: no
later call reaches it until the whole definition resets. Two authored idioms use the halt, and a
third, the "stopper", turns out to author a teardown that never runs:

- **Break** (`test_player`×33, `setprop`×8): a sequence stops *itself* inside an `IF` branch,
  `random_prop` picks one of 8 random prop rotations and `STOP_SEQUENCE [setprop]` ends the
  taken pass so the remaining branches never evaluate. Requires the halt reading on self.
- **Halt a running sibling** (`large_30sec_fire`'s `fire_n_smoke`, `zepskinfire`×5,
  `flame_light_seq`): the target is genuinely running, a `LOOP -1` poll or a sequence started
  by `CALL_SEQUENCE`. The halt matters beyond bookkeeping: a `PUFFER_STATE`
  re-assert *revives* a stopped emitter (the damage-stage sputter contract), so the
  `PUFFER_STATE INACTIVE` these stops pair with cannot end the fire alone, the un-halted poll
  would re-light it one frame later.
- **Stopper** (`flame_ball.zrd`'s `stop_p1trail`): the target is `ACTIVATION ON_CALL`, not
  running at fire time, and its body is pure teardown (`PUFFER_STATE … INACTIVE`,
  `OBJECT_ACTIVE_STATE … INACTIVE`). The survey read this as a fall-through to a call,
  on the strength of the same file reaching the same sequence by a literal
  `CALL_SEQUENCE [stop_p1trail]` elsewhere (`moving_fire_ball_01`'s `fly_flare`). The exe says
  otherwise: the stop marks it done and the teardown never dispatches. The two events are *not*
  interchangeable, a call reaches a stopper, a stop buries it. **16 definitions** author this
  (`flame_ball_01`/`flame_ball_02` → `stop_p1trail`, in all 8 chapters), and the effects those
  sequences would have switched off are instead left to their own authored lifetimes.

Halting a runner never retracts what its events already launched. Puffers, lights and the
tweened motions run out their own authored lifetimes (the same independence that keeps a rocket
ring's scale motion alive after its launching sequence ends). ⚠ A ballistic `OBJECT_MOTION` is the
exception: it is integrated only while its sequence is still dispatched, so a stop freezes the
body where it stands, which is how every zeppelin wreck halts over the sea
([sequences.md](../org/sequences.md)).

⚠ **CSVM does not persist the disable.** It halts matching runners and reports whether the name
resolved; it does not remember that a sequence was stopped, so a later `CALL_SEQUENCE` still
starts it where the original's done state would refuse. 123 definitions name one sequence in both
a call and a stop (mostly `flame_light_seq`), but whether any of them reaches the stop *before*
the call at run time is a control-flow question the static census cannot answer.

### `INVALIDATE_ANIMATION` is a one-shot latch, not a stop

The original keeps a state byte on each animation record (`+0xa0`): 2 = running,
6 = running-and-invalidated, 3 = `ANIM_STATE_EXECUTED`, 4 = `ANIM_STATE_INVALID` (both names are
the exe's own log strings, `zeff_anim.c:0x215`/`0x235`). The start (`FUN_004ed8c0`) refuses states
4 and 6 outright, the stop (`FUN_004ebbb0`) carries the invalidated mark into the terminal state
(2→3, 6→4), and only `RESET_ANIMATION` clears it (`FUN_004ed480`: 6→2, 4→3).

So the event never stops a running animation; it latches the definition off so no later start takes
it. The latch is keyed by **definition**, not by (definition, anchor): the event resolves one
animation record through `FUN_004ebc80`'s `ANIM_REFS`/world-list lookup and never walks the
per-node copies chained off it.

⚠ A definition's own sequence may invalidate itself early in the burst it is starting (the
"consume the trigger" idiom). Tearing the instance down there orphans the calls that burst has
already scheduled, so CSVM protects the top-of-stack instance on the death path only; the ambient
world boot needs the tolerant behaviour for its self-invalidating startup animations.

## Fire animations

Decoded while chasing the user's "there is a fire flipbook at the refinery" report;
was wrong** (see "What changed" below). Three separate layers, none of which is
`OBJECT_ADD_CHILD`:

- **Templates.** `fire1`/`fire2` (C1 nodes 494/496) sit under the **parentless roots**
  `fire1.flt`/`fire2.flt` (493/495), real single-polygon `Facade`/`CylindricalY` meshes on
  materials 88/133 (`fire101.tif`/`fire102.tif`). They are not chapter geometry:
  `support\load.gw` lines 357/360 load them from `common\effects\models\`, and every *other*
  effect root (`large_firetrail`, `lg_fireball`, `flame_ball_01`, `fire_here`, `zep_skin_fire1`–`4`)
  is loaded from `dummy.flt`, i.e. is an empty anchor that draws nothing by construction.
  `WorldBuilder` builds only World children plus partition-referenced subtrees, so none of them
  is ever in the scene.
- **The flipbook, `effects.zrd.json`.** `["fire1.flt", NAME ["fire1"], SPEED [10.0], LOOPING
  ["ON"], MAPS [fire101.tif … fire112.tif]]` and `fire2` with 6 maps @ 5 fps. Exactly two entries
  exist install-wide. The maps resolve **by filename against the texture archive**, not through
  the gamez texture table: every chapter's `textures.json` registers only `fire101`/`fire102`,
  while `extracted/<ch>/texture/` ships all twelve `fire1NN.png`.
- **Behaviours, `fire.zrd.json`.** Four `ON_CALL` definitions, **all anchored on `fire2.flt`**:
  `timed_big_fire`, `persistent_big_fire`, `persistent_small_fire`, `timed_small_fire`. They
  scale the template up and back down and flicker a `big_fire_light`.

### The flipbook is state on the MATERIAL, and the material is shared

`zeff_ini.c`'s reader (`FUN_00523ac0`) resolves the entry's node name, walks down to the first
mesh under it (`FUN_00525d40`), takes surface 0's material, and installs the frame list on the
**material record** through `gmod_matl.c`'s `SetCycleTextureCount/Map/Loop/Speed`
(`FUN_0055b0c0`/`0055b160`/`0055b280`/`0055b2c0`). The material's cycle block hangs off `+0x24`:

| Offset | Field |
|---|---|
| `+0x00` | looping |
| `+0x04` | frame stamp (the global frame counter this material last advanced on) |
| `+0x08` | accumulated time |
| `+0x0c` | speed, frames/sec (15.0 until `SPEED` overwrites it) |
| `+0x10` | frame count |
| `+0x14` | fill count, while the maps are being appended |
| `+0x18` | the texture-handle array |

`SetCycleTextureCount` sets `0x100` (textured) and `0x400` (cycled) in the material's own flag
word. The polygon draw loop (`FUN_005524d0`) tests that word per polygon, calls the advance
(`FUN_0055b1a0`) when `0x400` is set, and samples the material's live texture at `+0x10`. The
advance is `mat[+0x10] = tex_array[ftol(acc) % count]; acc += dt * speed`, guarded by the frame
stamp so it runs at most once per material per frame.

**So a flipbook reaches every polygon that references the material, not the node that named it.**
Materials are one record per texture (C1's 570-entry table has exactly one duplicate pair), and
the gamez zbd is a memory snapshot (records carry live `cycle_ptr`/`tex_map_ptr`/`current_frame`,
polygons carry `matl_refs_ptr`), so that sharing is the runtime state, not a file-format artifact.
Material 88 is referenced by four models: `fire1` (the template), `flame01` (the refinery vent,
node 2998 under `vent1` → `refinery.flt` → `refinery` → `world1`), `mb1` and `mb_spinflame`.
Material 133 is used by `fire2` alone.

`fire1.flt` is therefore a **proxy node**: it exists to name a material, exactly like the interp
scripts' `watersetup`/`surfsetup` (`support\<ch>\tex_fx.gw`), which install the water, surf, wake,
turbulence and walking-crowd cycles through the same four setters via the
`CycleTextureSetOn/Speed/Looping/Map` commands in `zinterp.c` (`FUN_005b80a0`). The interp also
has `CreateUniqueMaterials` for the case where sharing is *not* wanted; `support\cockpit.gw` is
the only caller, de-sharing the gauge materials so each instrument indexes its own frame set
(those run at `SPEED 0`, i.e. a frame set the game indexes explicitly rather than a flipbook).

### Effect-cycle mechanism

The reading said "EFFECTS binds to the NODE, not the texture or material", concluded
that `flame01` is a static base flame, and that the animated fire the user saw was a placed
`fire2` instance. The draw loop settles it the other way: **`flame01` plays `fire101` to `fire112`
at 10 fps from load, with no trigger and no placement**, and `mb1`/`mb_spinflame` play the same
cycle in lockstep because the frame stamp gives every polygon on material 88 the identical frame.

The observation that drove the old reading, a sustained muzzle flash never leaving `fire101`,
is contradicted by the code. It is not a strong observation either way: `mb_spinflame` is a small
three-polygon sprite that is already spinning and flashing, and the twelve `fire1NN` frames are
variations of one shape rather than a sequence with obvious motion. **The clean A/B is the
refinery vent**, which is a still object where the frames should visibly roll.

### The behaviours are dead data, and CSVM does not run them

All four `fire.zrd.json` names appear in exactly one file, their own. No compiled archive, no
other reader, and there is no index-based call form. `CATCHES_FIRE` is a real ZWEP weapon key
(`zwep_ini.c` `FUN_005ad630`, flag bit 13) but **no weapon in this install sets it**, so it is not
the missing trigger either. Nothing in the shipped data ever reaches these four definitions, and
they read as leftovers from the engine's earlier game rather than content this one uses.

**CSVM therefore has no damaged-object catch-fire path, by decision.** Any condition we wired
would be our invention, not a port, and an invented trigger is the kind of fidelity guess this
project does not make. The always-on refinery flame is unaffected: it is the material cycle above,
installed by `EffectCycles`, and needs no behaviour, no burn site and no trigger. What a damaged
object does show is the `sputter_fire`/`sputter_black_smoke`/`sputter_fire_smoke` callees its
`HEALTH`/`DAMAGE_SEQUENCE` script actually names.

## Point lights

Surveyed across the whole install : **1,468 `LIGHT_STATE` events, every one of them
`type_: "PointSource"`**, no directional or spot lights exist in this data. A definition's
`lights` array is its symbol table for them, exactly as `objects`/`nodes` are for geometry, so
a light name is scoped to the definition instance (two refineries each own an `orange_light`).

| Field | Notes |
|---|---|
| `name` | Index into the def's own `lights` array. |
| `active_state` | On/off. true 1147× / false 321×. |
| `translate` | `{AtNode:{name,pos}}` (761×) or null (707×), a gamez node plus a local offset, the same shape and frame as a puffer's `AT_NODE`. |
| `range` | `{min,max}`, full brightness inside `min`, nothing past `max`. Present on exactly the 1147 "on" events. **Every startup light in this install has a `max` between 2 and 22 m.** |
| `color` | `{r,g,b}`, present 765×. A DX7 sRGB value, so it needs linearising like `FOG_COLOR`. |
| `ambient`, `diffuse` | Null except on the 20 MP2 flag-light events (0.3 / 1.0). An absent one keeps the light's value, 1.0 / 0 on a fresh light; the point term scales the colour by their sum ([`../org/vertexLighting.md`](../org/vertexLighting.md)). |
| `directional`, `saturated`, `subdivide`, `lightmap`, `static_`, `bicolored`, `orientation`, `ambient_color` | Null or false almost everywhere; nothing in this install depends on them. |

**The semantic that matters is that a `LIGHT_STATE` is a PARTIAL update.** A fire or refinery
flicker is a stream of `{name, range}` events 0.03–0.07 s apart that must leave position,
colour and active state untouched, the reader spells this the same way
(`"LIGHT_STATE", ["NAME", […], "RANGE", […]]` and nothing else). The compiled nulls line up
exactly: `range` is null on precisely the 321 events that switch a light *off*. So a handler
must apply only the fields present and never default the absent ones. 66 reader files also
carry `LIGHT_STATE`, so the zrdr front-end needs the same normalizer (`AnimDefs.AddLightState`),
skipping it would repeat the `PUFFER_STATE` bug in a subtler form.

`LIGHT_ANIMATION` (535 events) ramps a light over `run_time`, and its `range`/`color` are
**signed deltas, not targets**: C1B's `ap_light` pulse runs `{min +50, max +160}` over 0.1 s
then `{min −50, max −160}` over 0.05 s, and a negative range is not a value a light can hold.
The reader's `RANGE` carries four numbers (`[min, max, altMin, altMax]`) where the compiled
form splits the trailing pair into `range_alt` (null throughout this install).

**The ramp is the event's DURATION, it holds its sequence** (decoded). The
handler is dispatch slot 5, `004e82b0`. On its first dispatch (`seq+0x20 == 0`, i.e. state
*starting*) it copies the authored per-second deltas into the event's working slots
(`+0x30/0x34 → +0x40/0x44` for the range pair, `+0x48/0x4c/0x50 → +0x60/0x64/0x68` for the
colour triple); on every dispatch it reads the light's current range and colour back out of the
light object, adds one tick's worth of delta, clamps each colour channel to `0…1` and writes
them back, with the last tick shortened to the remainder (`dt − (event_timer − run_time)`) so
the ramp lands exactly on its end value. It then ends
`return (seq->event_timer < run_time) ? 1 : 2`, **still running until the run time is up**,
which is the same gate `FBFX_COLOR_FROM_TO` (`004ec6a0`) uses and the same one the
handler-return state machine above describes.

So a chain of `LIGHT_ANIMATION`s is a *timed* chain, not a burst. CSVM ramps the light
asynchronously instead (`AnimLight.TweenLeft`, ticked in `AnimRuntime.TickLights`), which draws
the same picture **only if the sequence is also held**, and until D31 it was not: the handler
reported duration 0, so every step of a chain fired in one instant and each re-armed the tween
the previous one had just started. The observable cost was total, not subtle. C1's `red_police`
(`police_lights`) authors `LIGHT_STATE` red `{0…10}` / `LIGHT_ANIMATION {max +40}` over 0.25 s /
`LIGHT_ANIMATION {max −40}` over 0.1 s / `LOOP −1`, a 0.35 s flashing beacon. Collapsed, the
two ramps cancelled each other every animation frame and the police light did not flash at all;
held, it flashes at its authored rate (and the `LOOP −1` paces off the ramps instead of running
one instantaneous pass per `AnimFrame`). `he_ground_effect`'s `he_light_seq` is the same shape
with seven ramps over 0.41 s.

**Which chapters actually light anything:** only **C1**. It is the sole chapter with
`OnStartup` definitions containing `LIGHT_STATE` (36 of them, the refinery flare, six
docklights, six reflights, the police light); every other chapter's light events sit in
`ON_CALL` combat/destruction effects (`gunhit_lt`, `muzzle_lt`, `fuel_light`) that a bootstrap
never reaches. C1 reports 35 lights at startup, growing to ~57 as delayed and looping
sequences fire. The `ON_CALL` effect lights (`he_light`, `gunhit_lt`, `fuel_light`, `flak_light1`,
`torp_light` and the rest the world-effects runtime binds) light in every chapter when a burst
plays them: that runtime submits into the world's own `WorldLights`, on both presentations.

**What a point light is FOR here.** The visible flare at a light's own position is *already*
separate gamez geometry, C1's `docklight_flare` is a `Facade`/`SphericalY` mesh textured
`dock_liteflare.tif`, and the refinery's `flame01` is a `Facade`/`CylindricalY` mesh textured
`fire101.tif`. Both render without any animation. So `LIGHT_STATE` is not what draws the lamp;
it is the **light falling on surrounding geometry**, the per-vertex point term the original adds
to the same authored vertex colour our world shader reads (the law is in
[`../org/vertexLighting.md`](../org/vertexLighting.md), "Point lights"). Rendering notes in
`docs/architecture/Mech3.md` under `WorldLights.cs`.

**Cost warning for anyone adding a handler here.** These events are not occasional. C1 fires
~2,700 `LIGHT_STATE`s per second at steady state, because each fire's flicker re-issues its
*full* event, `AT_NODE` included, every loop iteration. Resolving that node name per event
put ~1,740 calls/second through `AnimRuntime.ResolveOne`'s full-world fallback scan (7,064
nodes against a regex matcher, ~12M comparisons/second) and cost ~7 ms/frame on its own. The
name is what identifies the target, so the resolution is cached per light and only redone when
the name changes.

## Ambient audio

Implemented (`src/Mech3/WorldSounds.cs`). This is the waterfall roar, the train, the
firetruck and police sirens, the zeppelin nacelle engines, the fire crackle and the cockpit
warning beeper.

**`SOUND` and `SOUND_NODE` are different animals, and only one of them is ambient world audio.**
Surveyed across the whole install:

| | `SOUND_NODE` | `SOUND` |
|---|---|---|
| events | 1,244 | 6,091 |
| distinct names | **10** | 87 |
| all present in `sounds.json` | yes (10/10) | no (21 missing) |
| `3D` | 10/10 | 41/87 |
| `LOOPED` | 9/10 (`snd_freighter` is the exception) | 3/87 |
| activation | 951 `OnCall`, **293 `OnStartup`** | 4,378 `OnCall`, 1,650 `WeaponHit`, **8 `OnStartup`** |

So `SOUND_NODE` is a small, fully-resolvable set of looping positional emitters bound to nodes,
and `SOUND` is one-shot combat/destruction audio, gated behind the weapon hits and death
sequences M3 now produces, and 21 of its names are not plain `sounds.json` entries at all but
`DYNAMIC_WEIGHTS` groups (`air_mixed_exp_sg` picks one of five `snd_exp_hit*` at random) needing
their own decode. The ambient half landed first; the one-shot half **landed in M3 D31**,
`AnimRuntime.HandleSound` fires a fire-and-forget `WorldSounds.PlayOneShot` at the event's
AT_NODE, resolving a `SOUND_GROUPS` name through the decode now in
[sounds.md](sounds.md#sound-groups). The event's NAME is a sound
*definition* or a group, never a gamez node (the lone reader-scope one-shot names
`snd_waterfall`, a definition, and resolves zero node targets.

**The reader spells one emitter as three consecutive events**, which is the whole shape of the
feature:

```
SOUND_NODE          ["NAME", ["snd_waterfall"]]                        -- declare the emitter
OBJECT_ACTIVE_STATE ["NAME", ["snd_waterfall"], "STATE", ["ACTIVE"]]   -- switch it on
OBJECT_ADD_CHILD    ["PARENT_CHILD", ["waterfall01", "snd_waterfall"]] -- attach it to a world node
```

The middle event is an ordinary `OBJECT_ACTIVE_STATE` whose NAME is a **sounds.json definition,
not a gamez node**, letting it fall through to the normal node resolution scans the world for
`snd_waterfall`, finds nothing, and books an unresolved op. The third is what positions the
emitter.

**The compiled form carries the same three facts inline**, `{name, active_state, translate}`,
but only sometimes: `translate` is an `AtNode` on **379** events and null on exactly **865**, and
865 is also exactly the number of `OBJECT_ADD_CHILD` events that attach a sound definition
(`snd_zepengine`→`spin` alone is 849). The two counts matching to the event is what proves
`SOUND_NODE` and the sound three-quarters of `OBJECT_ADD_CHILD` are **one mechanism**, which is
why they had to land together, and it is the concrete form of the dependency noted when
`OBJECT_ADD_CHILD` was withdrawn ("it is mostly the positioning layer for sound emitters, so it
should follow `SOUND`, not precede it").

Two field traps:

- **Compiled `active_state` is a JSON boolean**, where `PUFFER_STATE`'s identically-named field is
  numeric. Reading it with a number accessor returns null for `true`, and an absent-means-leave-
  alone default then leaves every emitter in the world switched off. That is exactly what it did
  until the headless log showed 38 correctly-placed emitters all reading "off".
- **The reader form carries no `active_state` at all** (its ACTIVE is the next event), so absent
  must mean "leave alone" and not `?? 0` = OFF, the same shape as the bug that silently killed
  the C1 waterfall's splash puffers.

**Reader-only coverage is dormant.** Of 252 reader `SOUND_NODE` definitions across the chapters,
231 have a compiled twin (compiled wins in `AnimProgram`), and all 21 that do not, `sprucegoose`/
`g_enginesound`, `locklear_gasbag`, the zep nacelles, are `ON_CALL`, which the bootstrap never
reaches. So the reader triple path is implemented and correct by construction but is not
exercised in a default session. The reader spelling of `NODE_UNDERCOVER` sits the same way: the
install ships no `NODE_NEAR_GROUND` at all, so `AnimDefs.ReaderCondition`'s branch for it is
implemented against the parser's vocabulary rather than against shipped data.

**An emitter is silent while its host is not visible in tree**, the same rule the point lights
use: C1/IA1 deactivates both multiplayer zeppelins, so 36 of its 38 emitters are built and
stopped, and only the waterfall and the train sound. That is also what makes the counts safe,
C5 builds 108 emitters and plays none.

## Mission library scope

A mission folder ships reader files it never uses. **A mission-scope reader definition applies
only if the mission's compiled `mis_anim` archive contains it** (matched on the compiled
extraction's own file naming, `<anchor>-<anim_name>.json`).

C1/IA1 carries a `zepstate.zrd.json` that hides `dliner1` and `cargotrain`, yet in the
original both are present in Instant Action, `dliner1` is the zeppelin inside the Passenger
Hangar (`dz1` is 140 m from it) and `cargotrain` is the consist parked in the cut below the
terminal at (-5102, 128, -3852). Its `mis_anim` compiles neither. Verified over every
zepstate in this install:

| Mission | zepstate defs | in compiled `mis_anim`? |
|---|---|---|
| C1/IA1, C1B/IA1, C1C/IA1, C2/IA1, C2B/IA1, C4/IA1 | `dliner1`, `cargotrain` | **unused** |
| C1/M02 | `hk_zep`, `lkshadow`, `tethershadow`, `tethertower` | COMPILED |
| C1/M04 | `dliner1`, `cargotrain` | COMPILED |
| C3/IA1, C3/M02, C3/M03, C4/M03 | `cargozep1` | COMPILED |

C3/IA1 compiles `cargozep1`, so this is **not** "Instant Action ignores zepstate", the
compiled set is the authority. Two user observations of the original corroborate it: C1/M04
shows the field zeppelin with an empty hangar and no parked train (what compiling both defs
produces), and C1/M02 is the only mission where the tether tower disappears, the only
mission that compiles `tethertower`.

which was generalised from C1/IA1. It is compiled into the missions that use it; being
uncompiled is exactly the signal that the mission does not instantiate it.

### The scope gates run before a (NAME, ANIMATION_NAME) deduplication

Three separate rules decide whether a reader definition becomes an instance, and the census line
`N reader def(s) superseded by this mission's compiled manifest (mission-scope + NAME1), not
instantiated` names only the first two:

1. **Mission scope** is gated on the compiled manifest, above: the definition is skipped unless the
   mission's `mis_anim` lists its (`NAME`, `ANIMATION_NAME`) pair.
2. **Shared and chapter scope** are gated by file first (the `ANIMATION_DEFINITION_FILE` lists,
   below), and then a `NAME1` multi-target definition is skipped because the compiler expands it per
   instance into `mis_anim`, and the reader form would anchor sub-parts the loaded mission never
   authors.
3. **Everything that survives is deduplicated on the (`NAME`, `ANIMATION_NAME`) pair**
   (`AnimProgram.Add`), and because the compiled archives are loaded first the compiled form always
   wins. This rule is not in the census line and is what keeps a plain-`NAME` shared definition from
   coexisting with its compiled twin: `player_hook.zrd`'s `player_extend_hook` passes both gates and
   is then dropped against `cam_anim`'s `player-player_extend_hook`. It is also why the archives'
   own duplicate files (`blood_hook_extend`, `brig_hook_extend` and `brig_hook_startup`, each
   shipped twice a chapter as `<name>.json` and `<name>-1.json` carrying the same pair) cost nothing.

All three gates apply only with a compiled mission manifest present, so a reader-only extraction
still loads every definition it has.

### Shared-scope files are listed per mission too

The shared `zrdr.zbd` is not one runtime-global set. Its 190 `ANIMATION_DEFINITIONS` files
split three ways by the `ANIMATION_DEFINITION_FILE` lists (every chapter's `cam_anim.zrd`
lists `..\data\common\zrdr\anim.zrd`, the shared index, plus its own chapter files; every
mission's `mis_anim.zrd` lists what it adds):

| Shared files | How they reach a mission |
|---|---|
| 88 | the `anim.zrd` index closure (effects, splashes, the plane and generic building sets): every mission |
| 96 | listed by individual `mis_anim.zrd`/`cam_anim.zrd` files only: the zeppelin sets (`pzep_*`, `cg1_*`, `multi1_*`, …), the balloons (`balloon_*`, C3/M02 and C4's `cam_anim.zrd`), the patrol boats, `spruce_destroy*`, `zepskinfire`/`partial_damage` |
| 6 | listed nowhere (`aa_car`, `armytruck_destruct`, `fire`, `mp2_fighter_release`, `player-1`, `wingman`); `player-1.zrd.json` is the extraction's duplicate-name copy of the listed `planes\player.zrd` and is kept by that stem |

A shared file outside a mission's lists is not part of that mission: its compiled `mis_anim`
carries none of its defs, and the reader form is compiler input, not a runtime library. The
barrage balloons are the worked case. `support\c3\m03.gw` switches `bont1..6`/`b_turret1..6`
off, and M03 lists no `balloon_*.zrd`; loading them anyway ran `balloon_down`'s `RESET_STATE`
(`bont* ACTIVE`) over the switch, registered six `ball_kaboom*` HP pools on canopies that are
not in play, and made the AI gunners engage the balloon turrets. `AnimProgram.ListedSharedFiles`
applies this rule to the shared scope when a compiled mission manifest is present; the log line
`N shared reader file(s) no ANIMATION_DEFINITION_FILE list of this mission names, not loaded`
is the census.

**The chapter scope carries the same rule**, gated the same way (present only when a compiled
mission manifest loaded) by `AnimProgram.ListedChapterFiles`: a chapter file is listed by its own
`cam_anim.zrd`, or added directly by an individual mission's `mis_anim.zrd`, exactly as the shared
scope's 96 mission-only files are. Unlike the shared scope there is no chapter-level index file to
walk a closure from, every chapter file that lists further files is `cam_anim.zrd` itself, so an
empty listed set gates the whole chapter scope shut rather than leaving it ungated. `AnimRuntime`
prints a matching `N chapter reader file(s) no ANIMATION_DEFINITION_FILE list of this mission
names, not loaded` census line beside the shared one. C1's `clouds`, `lightning`, `spotlights` and
`train_smoke`, C2's `game_targets`/`police_blockade*`/`police_destroy`/`security_destroy` (some
missions list a subset), C3's `flag_british`/`flag_rollout`/`hydrogen`, C4's
`bhmhookup`/`bhm_warhawks` and C5's `steinmann` are the chapter files no mission's list names, so
none of their defs load in any mission. C1's `cloudparent#` 0.6 opacity was sourced from
`clouds.zrd`; with the gate applied, the clusters lose that dim and render at full opacity, matching
the original, whose compiled archives derive from these same lists and so never ran the file
either.

### Mission-spawned entities

Scenery props are hidden by compiled `zepstate` defs as above. *Entities*, the zeppelins,
the CTF props, the vehicles and guns, are governed by a different system entirely: the
**per-mission interp boot script** `support\<chapter>\<mission>.gw`, documented in
[interp.md](interp.md). They are present by default and the script switches them off, which
is the same polarity as `zepstate`, not the mirror image of it.

C1's `hk_zep` (the Hollywood Knights zeppelin, at (-5248, 200, -5208) beside `tethertower`)
has no def in IA1 scope at all, and needs none: `support\c1\ia1.gw` contains
`FindNode hk_zep` / `NodeSetActive off`, while `support\c1\m04.gw` does not, which is
exactly why it is on the field in M04 and nowhere else. The CTF props are switched off by
every mission script except `mp2.gw`.

⚠ **This corrects the roster hypothesis recorded here.** Neither candidate could
have gated anything, and both were checked before implementing:

- **`aiv.zrd.json`** is the AI *vehicle* table, not a spawn roster. Its only mention of
  `hk_zep` anywhere in the install is inside a wingman's target-priority list in C1/M02; C1/M04,
  the one mission that shows the zeppelin, does not name it at all.
- **`zeppelins.zrd.json`** is the flyable-zeppelin gameplay config (position/yaw/engines/
  cannons/gasbags). C1/IA1 lists `multiplayer1zep`, a node that mission's boot script
  switches *off*, and C1/M04 lists only `piratezep`.

The `dliner1` caveat that motivated the roster idea also dissolves: no name pattern is
involved, the script names its nodes outright.

## Start animations

```
[["NEW_GAME_START", [["player_setup"], ["train_on_track"], …],
  "LOAD_GAME_START", null]]
```

Anim names (matched against `ANIMATION_NAME`) run at mission start, in list order,
order matters: C1/IA1 runs `hangar3_doors` (doors to ±50) then `mp_hangar3_open`
(same doors to ±25); last write wins. Names may be **undefined** in the mission's
visible scopes (C1/IA1 lists `pure_panic`, defined only in C1/M02's folder), the
engine evidently tolerates the miss; skip and log.

## Zeppelin states

Ordinary ANIMATION_DEFINITIONs, `ACTIVATION ON_STARTUP`, whose sequences hold only
`OBJECT_ACTIVE_STATE`s, the per-mission roster of world objects present: C1/IA1
deactivates `dliner1` (the passenger zeppelin in the shed) and `cargotrain`. The
chapter gamez contains *every* mission's objects; without applying these states,
phantom zeppelins/trains render in every mission.

## `SAVE_LOG` / `PERSIST_LOG` are the cross-mission state log

A mission does not always start from `RESET_STATE`. The engine keeps a **state log** of
flagged definitions, and a mission load applies it on top of the bootstrap, which is how the
original shows scenery already destroyed in a mission where nothing could have destroyed it.

**User A/B in the original (C3, the `susp_bridge` suspension bridge, destroying it is an
`M01` objective, see `C3/M01/zrdr/objectives.zrd.json`):**

| sequence | result | what it shows |
|---|---|---|
| M01, destroy bridge → IA1 | destroyed | a campaign mission **writes** the log; IA **reads** it |
| → M01 again | whole | a campaign load **restores the baseline at the start of that mission** |
| → quit → IA1 | whole | IA applied the restored baseline |
| IA1, destroy something → quit → IA1 | reset | **IA never writes** to the log |
| alt+F4, restart, load M02 | bridge destroyed | the log **commits to the save** and M02's baseline includes M01's result |

So: **every mission load applies the log; only campaign missions write it; the commit survives
a process restart.** Note the last row is not authored into M02, `support\c3\m02.gw` names no
bridge node at all, and the bridge appears in no `M02` reader.

**The flags are the per-definition opt-in.** `C3/zrdr/susp_bridge.zrd.json` opens
`NAME susp_bridge, SAVE_LOG ON, PERSIST_LOG ON, NETWORK_LOG ON, ANIMATION_NAME rope1burn,
ANIMATION_ROOT_NAME rope1, HEALTH 7`. Counted across all 1533 reader
`ANIMATION_DEFINITION`s in the install:

| | count |
|---|---|
| `SAVE_LOG` absent (default off) | 962 |
| `SAVE_LOG ON`, no `PERSIST_LOG` | 506 |
| `SAVE_LOG ON` **and** `PERSIST_LOG ON` | 62 |
| `SAVE_LOG OFF` | 3 |

`PERSIST_LOG` is a **strict subset** of `SAVE_LOG`, nothing persists without also being saved,
which is why the two read as a hierarchy rather than two independent switches. The three
`SAVE_LOG OFF` defs are pure cosmetic loops with no state worth recording (`hotelsign_loop`,
`refinery_fire_always`, `chuteman`).

**The 62 `PERSIST_LOG` defs are a curated list of fixed world scenery**, i.e. exactly the things
whose destruction a later mission should remember: `susp_bridge`, water/radio towers
(`ap_h2otwr`, `ap_radiotwr`, `ap_transmitter`, `col_tower#`), guard towers (`g_tower*`), the
generic building templates (`s_build**`, `m_build**`), boats and yachts (`leasure*`, `sailboat*`,
`yacht*`), grass huts, the Hollywood sign, `ramses`, the studio gates, trains (`train01/02`),
`mineshack`, `sluice`, `shaft`, `airdock2`, AA guns (`aagun**`, `maagun**`), army/fuel trucks,
and C1's fuel depot, `ftank0*` (`fuel_tanks.zrd.json`) plus `fuelbox*` for **both**
`fuelboxconnect*` and `fuelboxbreaks*` (`fueltruck.zrd.json`). The 506 save-only defs are the
transient layer: zeppelin turrets, gasbags, engine nacelles, balloons, player cockpit panels.

**Consequences worth keeping.**
- A mission's rendered world is `RESET_STATE` + `zepstate` + `startanims` + its `.gw` **+ the
  log**. Only the first four are recoverable from files; the fifth is session/save state, so a
  screenshot of the original is not by itself evidence about the shipped data (this is what
  `BL-099`'s C1 fuel depot turned out to be).
- Because `fuelboxconnect*` is itself a persisted def, a *running* pump animation is part of what
  carries, persisted state is not limited to a static destroyed/healthy flag.
- CSVM builds every session from the bootstrap and keeps the persisted half of that log in the
  campaign profile, applied to a later mission of the same chapter
  (`Session/Campaign/CampaignPersistLog.cs`). An instant action loaded after a campaign mission in the same
  run reads nothing, which is where it still diverges from the original.

**Not yet pinned:** whether the commit happens at damage time or at mission completion (destroy,
abort without completing, restart, load the next mission), and a direct A/B separating the two
flags (destroy a `PERSIST_LOG` object and a save-only one in the same campaign mission, then load
an IA, the first should carry, the second should not).

## Scenario dimension limit

Instant-action scenario names (`zeppelin_run`, `dogfight_ace`, …) appear **only** in
`ia.json` spawn lists (plus `disallow_missions`). No reader carries scenario-conditional
world state: the IA world state is per-mission (`zepstate` + `startanims`), identical
across scenarios; `zeppelins.json` is the gameplay config of the always-present IA
zeppelin (`multiplayer1zep`), not a state selector. Scenario selection evidently drives
spawns/objectives/AI at engine level, not the world build.

## Compiled animation archives

See [Compiled animation archives](anim-definitions/compiled-archives.md) for the container, record, event, and SI-script reference.

## Consuming the extraction

Everything above is about *decoding* the archives. This section is what the Godot side needed
in order to **run** them - four facts that
are not visible from the byte format alone, each measured against this install.

- **The two sources are complementary; neither is sufficient.** The compiled archives are the
  better data (typed events, resolved refs, the SI scripts), but a mission's `mis_anim.zbd`
  compiles only the defs its `mis_anim.json` lists, C1/IA1 is 160 defs, all of them the eight
  zeppelin files. **`zepstate` and `startanims` are never compiled into any archive**; they stay
  zrdr readers the engine loads at runtime. So a player has to merge both, preferring compiled
  on collision (keyed by anchor name + animation name, which is exactly how the extraction names
  its files: `<name>-<anim_name>.json`).
  ⚠ **Carry `PERSIST_LOG` onto the def that wins such a collision.** The compiled record has no
  field for it, so preferring compiled and dropping the reader outright loses the flag for every
  persisted def whose name is exact rather than a wildcard (`susp_bridge`, `train01`, the studio
  gates, the yachts), and their nodes then cross no mission boundary at all. A wildcard reader
  (`t_truck**`, `aagun**`) collides with no compiled per-instance name and keeps its own flag,
  which is why the loss shows up on single objects among carried neighbours.
- **Reader op keys and compiled event tags are the same vocabulary under a spelling change.**
  SNAKE_CASE → PascalCase converts one to the other exactly, across the whole event set
  (`OBJECT_ACTIVE_STATE` → `ObjectActiveState`, `OBJECT_MOTION_SI_SCRIPT` →
  `ObjectMotionSiScript`, `FBFX_COLOR_FROM_TO` → `FbfxColorFromTo`, `IF`/`ELSEIF`/`ENDIF` →
  `If`/`Elseif`/`Endif`). Only the payload *field* names need per-kind mapping, and upstream
  spells the target field inconsistently per event type (`node` on ObjectActiveState/
  ObjectTranslateState, `name` on ObjectRotateState/ObjectMotionFromTo).
- **A support-array `ptr` IS the flat gamez node index, this is the correct binding.**
  Verified exactly: **136,048 references across all 8 chapters' `cam_anim` + every `mis_anim`
  resolve to a node whose name matches**, with the only apparent exceptions being the fork's own
  reversible `~N` duplicate-name suffixes (which the event names carry too, so lookups still
  hit). Events name their target as a *string*, which is ambiguous in the world, C1 has a
  `caboose` (the real consist, a child of the world root) and a `caboose.flt` (an unrelated rail-
  yard instance under a different parent), and name matching drives both, putting one of them
  somewhere wrong. A def's `objects`/`nodes` arrays are therefore its **symbol table**: resolve
  the event's name through them to get the exact index. Reader-sourced defs have no such table
  and keep the wildcard name matching (`ftank0*`, `s_build**`, `air_gen#`).
  ⚠ **Counter-example: the generic plane defs' indices are non-portable.** The compiled
  `player_crash_dirt` call closure references `piece1..4` at node indices 7703–7706, out of
  range for the shipping planes gamez, because the def is generic across all 11 aircraft and no
  single plane's index space can satisfy it. The 136,048-reference verification above covers the
  world-scope `cam_anim`/`mis_anim` defs; a consumer binding plane-scope defs must resolve by
  NAME (and note a plane subtree staged into a world scene mixes two index spaces that collide,
  world index 400 and plane index 400 are different nodes).
  ⚠ **Do not add a "fireball leads the crash explosion sound" spec claim.** The authored
  `player_crash_dirt` choreography puts the `Sound snd_exp_ground_a` event **before** the
  `large_fireball` calls (which cascade at +0, +0.25, +0.25, +0.25), and `large_fireball` carries no
  sound of its own, a lead in `FlightAudio.OnCrash` is a spec claim the shipped data contradicts.
- **Event scheduling, confirmed against `crimson.exe`:** each event's optional `start` is
  `{offset, time}` with `offset` ∈ `Animation` (since the animation started, gated against
  `anim+0xb0`) / `Sequence` (since this sequence started, gated against `seq+0x24`) / `Event`
  (since the **previous event completed**, gated against `seq+0x28`), `FUN_004ecbb0` evaluates
  whichever origin is named exclusively against the event that carries the `start`. An absent
  `start`, 174,938 of the install's events, encodes as `Animation + 0.0` (mech3ax's `common.rs`
  collapses that pair to `None`), not `Event + 0`; it behaves as "as soon as the previous event
  finishes" only because the gate above is evaluated exclusively once the previous event has
  reported completion, regardless of which origin it names. The discriminating case is the C1
  train: each car's sequence is `[ObjectMotionSiScript, Loop{-1}]` with no start offsets, and only
  "after the previous event completes" turns that into the surveyed ~327 s track loop instead of a
  zero-length infinite loop. A definition's sequences run **concurrently**, confirmed the same
  way, the train drives its four cars from four sibling `Initial` sequences, each with its own
  script and its own loop.
  **`Animation` and `Sequence` are two different clocks, and the difference is reachable.**
  `anim+0xb0` belongs to the *definition instance* and is shared by all its sequences; `seq+0x24`
  belongs to the sequence and starts at zero whenever that sequence starts. They coincide only for
  a sequence the bootstrap starts with the instance, for one a later `CALL_SEQUENCE` starts, the
  animation clock is already running. Censused over the whole install
  (`git show analysis-archive:analysis/anim-interpreter-decode/start_origin_census.py`, a
  retired instrument): of the 3,934 events carrying an
  explicit `start`, **1,091 name `Animation`**, every one of them with a non-zero time, since the
  zero pair is what mech3ax collapses to `None`, and **191 of those sit in an `OnCall`
  sequence**, the reachable case (the other 900 are in `Initial` sequences, where the two clocks
  agree). The 191 are the rocket/torpedo/sonic trail puffers shutting off at `Animation 10.0`,
  `ap_light_seq`/`torp_light_seq`'s `LightAnimation` chains at `Animation 0.25`, `chuteman_drop`,
  `car_dust`, the `gen_flare_yellow` family's `light_loop`, and `generate_smokescreen`'s two
  emitters. CSVM resolves `Animation` against `AnimInstance.Clock` for exactly this reason.
  A present `start` gates **the event it is attached to**, not its successor, also confirmed,
  including the first event of a sequence, and including control-flow events (`LOOP`/`IF`), which
  take no run time and therefore do not advance the "previous event completed" base. The
  discriminating case is C1's bowl sign (`bowl`, gamez 4868, def `on_off`): nine strict on/off SWAP
  pairs where only the first event of each pair carries a timestamp, gating the *successor*
  instead shifts every sequence in the install by one slot (timestamped events fire a slot early,
  their unstamped partners a slot late, the sign spends 38% of frames blank), and a loop back-jump
  that resets the gate to zero discards the trailing `Loop {Event 1.2}`'s inter-cycle pause.
- **`LOOP` has two spellings of "infinite": `-1` and `0`, confirmed by mechanism.** `004ebfd0`
  maintains a **u16** counter that counts *up* and terminates when `counter == authored`, with `-1`
  special-cased infinite; `0` is infinite only as a consequence of that mechanism, a u16 starting
  at 0 cannot match an authored `0` until pass 65,536, not because the data merely happens to use it
  that way. `-1` is the common spelling; `0` is *not* "run zero more times". The install-wide count
  distribution corroborates the mechanism rather than standing in for it: across the compiled
  `cam_anim`/`mis_anim` of the whole install the count distribution is **`-1` × 2,919, `0` × 26,
  positive N × 530**, and all 26 zeros sit in 25 defs that are, without exception, **ground-vehicle
  route animations**, C1's `police_car`/`mafia`/`black_car1`/`truck1`/`car_loop1`/`car_go_home`,
  C2's ten `studebaker*`, C3/M02's nine `stude_move*`. Every one is `activation: OnStartup`, and in
  every one the `LOOP` is the **last event of its sequence**, over a body of `ObjectMotionFromTo`
  legs carrying explicit from/to (so a replay re-seats the car at the route start). Nothing that
  must terminate uses it: no door, gate, one-shot, hangar, bomb or explosion def carries `Count: 0`.
  Reading `0` as "stop" makes every car in the game drive its route once and freeze.
  **The reader (`zrdr`) scope never uses it**, 703 `LOOP` events there, `LOOP_COUNT` ∈ {`-1`
  at all; it has 703. The usable fact is the absence of `0`, not the absence of `LOOP`.)
- **A positive `LOOP` count over an instantaneous body is a timer denominated in ANIMATION
  FRAMES, and the frame is 1/60 s, measured against the original .** The
  *denomination* is forced: a loop whose body schedules no time can only advance one pass per
  engine update, so `LOOP n` spends n updates and the counts are durations, not iteration budgets.
  The update RATE was the open half until `CAP-19` closed it.
  **The measurement.** `ref_fueltanks`' `fire_n_smoke` (`[PUFFER_STATE, LOOP 200]`, from the
  *untimed* set, which is the only set that can answer this) burns ~3 s in the original, giving
  200/3 ≈ 60. That figure alone could not tell a fixed ~60 Hz sequence tick from one pass per
  *rendered* frame, since it was taken on modern hardware where the original visibly runs fast or
  slow between sessions. The same effect was then timed twice, at **60 fps fullscreen and 120 fps
  windowed**: the burn took **the same time in both**. Per-rendered-frame ticking would have halved
  it at 120. **The sequence tick is decoupled from rendering, and 1/60 is the original's own
  number**, every untimed count is a real authored duration.

  **Decode status: confirmed by mechanism, not by a rate constant**, `004ebfd0` returns state 4
  unconditionally after completing a loop pass, and the stepper (`FUN_004ecbb0`) treats state 4 as
  "re-gate from the top, then stop for this tick" (the handler state machine is in
  [`org/sequences.md`](../org/sequences.md)). That is
  exactly the coupling this fps-doubling measurement inferred from outside the process: one pass per
  engine update, never more, regardless of render rate. The `1/60` figure itself is a measurement,
  not a constant read from the exe; nothing in the decode pins the update rate to a cited address.
  ⚠ The one hypothesis those two points cannot exclude is a tick of `min(render rate, 60)`, capped
  at 60, coupled below it, because no rate below 60 could be provoked (and the run went through
  dgVoodoo). It changes nothing: CSVM paces against SIM time at a fixed 1/60, so there is no
  sub-60 rate to couple to, and the case would describe a period machine failing to keep up rather
  than authored intent.
  **Scope, the 530 positive counts are not one thing.** **462 carry no period of their own** and
  are the frames-denominated set (`LOOP 70` ≈ 1.2 s, `LOOP 200` ≈ 3.3 s at 1/60); 376 of them sit
  in `sequences`, the list the runtime executes, and 86 in the undecoded `unknown_seq`, which it
  does not. The other **68 carry a `START_TIME` on the `Loop` event itself, the per-iteration
  period in SECONDS** (`huge_fireball` 10 × 0.05 s, `sputter_fire_obj` 100 × 0.2 s, `shipsink`
  7 × 2.5 s). Their authored periods span 0.01–5.0 s, and **0.01 s is below a 1/60 frame**, so the
  timing layer is real-valued seconds, not tick-quantised, whatever the tick turns out to be.
  **That 68 counts only the FINITE ones.** Install-wide there are **599** timed loops; the other 531
  are infinite, and **126 of them carry a 0.02 s period** (`patrolboat`, `ptboat*`, `ftank_boom*`,
  `m_build0*`, `pass_plane0*`, `sub_destruction`, `balloont_die*`, `refinery_fire_always`,
  `refuel*`). 104 more carry `Sequence 1.0` and are the ground-vehicle routes.
  ⚠ **A remake must pace an untimed loop against SIM time, not its own frame rate**, or every
  authored timer scales with the client's hardware, at 240 Hz they run 4× fast and at 144 Hz they
  quantise to 48 Hz (3 render frames per 1/60 s pass). CSVM pins the pass rate to
  `SequenceRunner.AnimFrame`.
  ⚠ **A loop that carries its own period is NOT automatically safe**, this doc said so until
  `BL-237` measured otherwise. A period is only honoured if the rollover carries its
  overshoot instead of resetting the clock, and if "did this iteration schedule time?" is asked of
  the DATA rather than of `_due > _clock`, which becomes a question about the step as soon as the
  step is coarser than the period. Missing either, a 0.02 s period costs 2 steps at 60 Hz instead of
  1.2 (60% speed) and an absolute `Sequence 0.01` collects the untimed-loop frame floor (16.7 s for
  an authored 10 s). Both are fixed in `SequenceRunner`; the traps are why the seconds/frames split
  is not the whole story.
- **JSON-layer trap: the `.zan` rotate quaternion's field labels are shifted.** mech3ax reads the
  file's `(w, x, y, z)` float order straight into a `#[repr(C)] struct Quaternion {x, y, z, w}`,
  so in the emitted JSON **real `w` = json `x`, real `x` = json `y`, real `y` = json `z`, real
  `z` = json `w`**. Byte-identical round-trip is unaffected (the bytes never change meaning), so
  nothing on the mech3ax side reveals it. Verified on the C1 passenger engine: under that remap
  every base quaternion is unit-norm and the yaw tracks the frame-to-frame chord heading to ~1°
  (frame 1 quat-yaw −33.11° against a chord of −43.12°, spanned by the frame's own −0.0505 rad/s
  rate); read literally the values are not even normalised. Undo the shift at the parse boundary.
- **Compiled `PUFFER_STATE` payloads** (consumed): the event carries the emitter's
  full parameter set inline, so no reader lookup is needed, cross-checked field-for-field
  against `train.json`'s own `steampuffer` (interval 0.03, LOCAL_VELOCITY 0/15/0, SIZE_RANGE
  0.8–1.5, LIFETIME_RANGE 0.5–4.5, friction 3, the five texture names, the three-stop colour
  ramp: all identical). Four shape facts: the emission interval is **always** in
  `interval_garbage.interval_value` (`interval` itself is null in all 4,387 PUFFER_STATE events
  of this install); the number in `interval_garbage.interval_value` is **seconds** for a Time
  emitter but **meters** for an `interval_type: "Distance"` trail (the crash-debris
  `spurtpuffer`s), and its flag shape is inverted, `has_interval_value` is **false** even when
  `interval_value` holds the real distance, so key off `interval_type`, never the flag. An
  `interval_value` of **0** with both flags clear is the "never authored" shape, which the engine's
  own setter refuses (`FUN_00550460`), leaving the puffer constructor's 1 s standing; reading that
  zero as a cadence puts the emitter on its emission floor instead. All 5 such events in the install
  are texture-less stubs, so nothing shipped is built from one;
  `GROWTH_FACTOR` arrives as a two-entry `growth_factors` array, which is an **`(age, scale)`
  ramp and not a min/max pair**, see the next bullet, which corrects what this one used to
  claim; and an event whose `textures` array is **empty** is an
  adjust/stop stub referencing a puffer another event defines, the readers have the same idiom
  (C1's `truck1dust_puffer` and `black_exhaust_puffer`). A `textures[]` entry's `run_time` is a
  **fraction of the sprite's lifetime**, not a second count, the survey that settles it, and what
  reading it as seconds did to `large_30sec_fire`, are in
  [effects.md](effects.md#emitter-schema). `at_node` is the attach point, and is
  NOT the event's `name` (that is the puffer's own name, a separate namespace). `ACTIVE_STATE`
  1 starts a continuous emitter and 0 stops it; definitions re-assert their puffers on every
  loop iteration, so a consumer must treat re-assertion as idempotent.
- **`unk_range` is `NEAR_FADE`.**
  The compiled `PufferState` payload carries the near camera-distance band under that placeholder
  name, immediately before `fade_range` (which is the reader's `FADE_RANGE`/`FAR_FADE`). Proven by
  matching both surfaces on one effect: C1's `black_smoke_ball_01-large_black_smokeball.json` has
  `unk_range {min: 70, max: 20}` and its reader block authors `NEAR_FADE [70, 20]`. ⚠ The `min`/
  `max` labels are the mech3ax field names, not a range: `min` is the hard discard cutoff and `max`
  the distance alpha would reach 1, so almost every event in the install has `max < min`. The
  semantics live in [effects.md](effects.md).
- **`growth_factors[i]` is `(age_i, scale_i)`, not `(min, max)`.**
  This bullet read the entry as a size *range* and cited a "matches 172 of 177 puffers" survey;
  **both statements are withdrawn**, the reading was wrong and the survey does not reproduce (see
  the wildcard bullet below for what it was actually seeing).
  **The data alone proves the reading**, independent of the disassembly: **216 compiled events
  author a second entry whose "max" is *less* than its "min"**, `(1, 0.25)` ×71, `(1, −0.2)`
  ×39, `(1, 0.5)` ×26, `(1, 0.45)`/`(1, 0.2)`/`(1, 0.15)`/`(1, 0.0)` ×16 each, `(1, 0.1)`/
  `(1, 0.6)` ×8. `(1.0, −0.2)` (`fire_at_zepskin3`) is a coherent *stop at age 1, scale −0.2*
  and an incoherent *range*. **The parser agrees**: `crimson.exe`'s `PUFFER_STATE` reader
  `FUN_004f7120` accepts a `SCALE_SEQUENCE` key of up to six `(age, scale)` stops
  (`if (5 < i) break`), and when that key is absent falls through to `GROWTH_FACTOR` and
  **synthesises exactly the degenerate two-stop ramp `count = 2, (0.0, 1.0), (1.0, G)`**. So the
  compiled `growth_factors` array is not a growth *parameter* at all, it is `SCALE_SEQUENCE`,
  always, with `GROWTH_FACTOR` as its two-stop spelling. `FUN_0054e6e0` walks the stops with a
  per-particle cursor and **lerps** between the bracketing pair, clamping past the last stop.
  ⚠ **Do not "repair" a descending pair.** Under the old reading `(1, −0.2)` looks like corrupt
  data; it is a puffer that shrinks to nothing and then inverts, exactly as authored.
  **Reachability, measured over the whole install:** the literal `SCALE_SEQUENCE` appears in
  **0** of the 17,569 extracted JSON files, reader *and* compiled, and of the **4,535**
  compiled events carrying a `growth_factors` key, **2,906 author it and every single one has
  exactly two stops**, with entry 0 equal to `(0.0, 1.0)` in all 2,906 (29 distinct arrays;
  commonest `G` 3.0 ×666, 2.0 ×492, 2.5 ×279, 1.5 ×210, 8.5 ×160, 1.0 ×153). **A consumer that
  lerps `1 → G` over the particle's life is therefore correct for 100% of this install** and
  needs no ramp walk; the multi-stop machinery is real in the engine and unreachable in the data.
  Read the array as stops anyway, a single-entry synthesis of the form `[(0, G)]` encodes the
  *wrong* reading even where it happens to yield the right number.
- **A reader `PUFFER_STATE` `NAME` may itself carry a `*` wildcard, and it expands at compile
  time** (found). The [name-wildcard convention](README.md#shared-conventions-zrdr-readers)
  is documented for scene-*node* references; the puffer name is a separate namespace (it is not
  `AT_NODE`), and it takes wildcards too. Three definitions in this install use one:
  `rc_smokn_stacks*` (`C1/M05/zrdr/redcross_ship.zrd.json`), `stack_puffer*`
  (`C4/zrdr/bhfchimney_smoke.zrd.json`) and `torch_puffer*` (`C5/zrdr/steam.zrd.json`). They are
  the whole explanation for the compiled surface's apparent orphans: of the **253** distinct
  compiled puffer names, exactly **7** have no reader definition, `rc_smokn_stacks1/2`,
  `stack_puffer0/1/2`, `torch_puffer1/2`, i.e. precisely those three patterns expanded, and
  nothing else. Conversely **9** reader names are never compiled: those same 3 patterns plus 6
  genuinely unused defs (`aa_car_puffer`, `fly01_puff`, `fly02_puff`, `fly03_puff`,
  `train_puffer2`, `zrapid01_puffer`). ⚠ **A reader→compiled name join that does not expand
  wildcards will report these 7 as unexplained mismatches**, which is what the withdrawn
  "172 of 177" survey above was doing.
  **Names genuinely collide across readers, separately from this.** Of the 879 reader
  `PUFFER_STATE` blocks (255 distinct names; 688 carry parameters, 191 are name+`ACTIVE_STATE`
  re-assertion stubs), **17 names carry more than one distinct `GROWTH_FACTOR`**,
  `trailpuffer1/2/3`, `spurtpuffer1/2`, `fire_n_smoke` (G ∈ {1.5, 2.5, 3.5}), `firepuffer`
  (5 values), `smokerpuff` (4.0 and 85.0), `smokepuffer`, `smokepuffer2`, `lgpuffer`,
  `blacksmokepuffer`, `whitehotpuffer`, `black_smoke`, `pandust`, `splasher`, `unit_fire`.
  Each is resolved per *file*, so a global name→definition table silently picks one at random.
  Compiled↔reader disagreements, once the wildcards are expanded and the per-file resolution
  respected: **none, on any key.**
- **Only `nodes` and `objects` carry node indices.** `lights`, `puffers` and `dynamic_sounds`
  hold runtime pointers instead, measured over C1/C2/C4/C5, **every one** of their 2,616 `ptr`
  values is outside the node-array range, while `nodes`/`objects` resolve 46,481/46,481 and
  31,323/31,323. Admitting the other three into a name→index table silently binds a puffer's
  own name to a bogus index.
- One plan-evidence correction: the `ANIMATION_PATH` key in `mis_anim.json` is the
  **directory** the engine resolves anim sources from (`..\data\c1\ia1\zrdr\zeps`), not a
  waypoint-motion primitive; no waypoint-path op exists in any C1 reader, path motion is
  SI scripts.

### The runtime decode lives in [`org/sequences.md`](../org/sequences.md)

Everything above this point in the section was inferred from the shipped data. The original's
interpreter has since been located and read directly out of `crimson.exe` with Ghidra, and each
paragraph above now states the mechanism the exe actually uses rather than the census that stood in
for it. Where the decode a stated mechanism (the null-`start` encoding, `LOOP 0`), the
paragraph asserts the one directly and keeps the census as corroborating measurement,
never as the sole justification.

**The decode itself is not repeated here.** It is one page,
[`docs/org/sequences.md`](../org/sequences.md): the function map and the live `SeqDefInfoC` offsets,
the handler state machine, the 47-slot opcode table, the three clocks, the LOOP and its remainder
carry, the depth-free IF scan, the condition flag word (fourteen kinds, of which this install
authors ten), `CALL_SEQUENCE`/`STOP_SEQUENCE`, `WAIT_FOR_COMPLETION`, `FBFX_COLOR_FROM_TO`, the four
shipped event kinds with no handler, and the list of places CSVM deliberately differs. This file
keeps the authored side: what the bytes mean and how the reader and compiled forms diverge.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.