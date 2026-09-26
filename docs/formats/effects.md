# Effects: `PUFFER_STATE` emitters, effect readers, flipbook textures

Part of the [format documentation](README.md). Covers the original's fully data-driven
effect system (the crash sequence and damage trails; no binary anim
format needed for any of it). Consumed by `CSVM/src/Effects/Puffer.cs`.

**This page is the authored side**, the keys, the files, the textures. What the original's
*runtime* does with them (the object and particle layouts, the constructor's defaults, the
integration order, the emission accumulator, the render equation) is decoded from the executable in
[`../org/puffer.md`](../org/puffer.md), which is outside this directory and its licence.

The system has three layers, all in zrdr + the chapter `texture.zbd`:

1. **Sequencing scripts**, `ANIMATION_DEFINITIONS` readers (`ai_plane_destruct.json`,
   `player_plane_destruct.json`) defining per-plane, surface-sensitive crash sequences
   (`player_crash_default`/`_dirt`/`_water`) that toggle model subtrees
   (healthy→destroyed), play named sounds at nodes, and `CALL_ANIMATION` into effect
   scripts. Schema in [anim-definitions.md](anim-definitions.md).
2. **Particle emitters**, `PUFFER_STATE` blocks (below) in `flame_ball.json` (16 states,
   e.g. `large_fireball`), `fire.json` (`fire_n_smoke` sustained wreck fire),
   `pufftrails.json` (`dense_firetrail` smoke/fire pair, `short_firetrail` panel trails),
   `flak_trails.json`, `zepskinfire.json`, plus inline states in vehicle anims (the
   train's steam in `train.json`).
3. **Flipbook textures** in the chapter `texture.zbd`: `fire_f01–06` (fireball),
   `fire101–112` (sustained fire), `smoke101–103`, `exp_yel01`/`exp_red01`/`exp_blu02`/
   `exp_gre01` (flash sprites), `fireflare1`.

The gamez effect-prototype nodes (`ball_of_fire`, `flak_explosion`, `dense_firetrail`,
`explode_here1–9`, …) are mostly empty parentless Object3d anchors the emitters attach
to at runtime, they are not world scenery (see [world-structure.md](world-structure.md)).


## Contents

- [Emitter schema](#emitter-schema)
- [Emission accumulator](#emission-accumulator)
- [Camera-distance fade](#camera-distance-fade)
- [Aircraft speed-cue wisps](#aircraft-speed-cue-wisps)
- [Aircraft throttle-rise exhaust](#aircraft-throttle-rise-exhaust)
- [Texture flipbooks](#texture-flipbooks)
## Emitter schema

A state is a **fully-defined emitter iff it has `NUMBER` (burst) or `DISTANCE_INTERVAL`
(trail)**; the readers also hold *stop stubs* sharing the same `NAME` but carrying only
`ACTIVE_STATE`, skip those when looking a definition up by name.

| Key | Value | Meaning |
|---|---|---|
| `NAME` | string | referenced by anims' `PUFFER_STATE` calls |
| `AT_NODE` | `[nodeName, dx?, dy?, dz?]` | attach point; the optional trailing offset is in the host node's own frame, same convention as `LOCAL_VELOCITY`, what spreads C1's three waterfall splash puffers ±11 m either side of the shared anchor `waterfall01` instead of stacking them on one point (fix: the offset was parsed nowhere and silently dropped, in both the compiled-event and reader-event front-ends, 862 of 4387 PUFFER_STATE events in this install carry a non-zero one) |
| `NUMBER` | int | burst mode: sprites spawned per `TIME_INTERVAL` |
| `TIME_INTERVAL` | s | burst/sustained spawn period. Unauthored it is **1.0**, the puffer ctor's own value (`FUN_00550100` writes `0x3f800000` to `+0x40` and to its reciprocal at `+0x44`) and ours. ⚠ In the compiled shape an unauthored key arrives as a **zero** in the garbage interval field, which the applier's setter refuses; a reader `PUFFER_STATE` with no `TIME_INTERVAL` is always a `DISTANCE_INTERVAL` block, where our still-host sputter uses the field instead and keeps its own 0.1 s. The smallest authored value in the install is 0.001 s (`torpufferblast`, the torpedo trail) |
| `DISTANCE_INTERVAL` | m | trail mode: one sprite per N meters of the followed node's motion (`dense_firetrail`: smoke 1.0 m / fire 0.25 m). ⚠ Only accumulated when the frame's motion is **under 200 m**, the teleport guard, see the emission-accumulator section below |
| `LOCAL_VELOCITY` / `WORLD_VELOCITY` | xyz | initial velocity, emitter-local / world frame |
| `MIN_RANDOM_VELOCITY` / `MAX_RANDOM_VELOCITY` | xyz | per-sprite random velocity range |
| `WORLD_ACCELERATION` | xyz | constant acceleration (buoyant smoke rises) |
| `FRICTION` | float | velocity damping |
| `SIZE_RANGE` | [min, max] m | initial sprite size |
| `LIFETIME_RANGE` | [min, max] s | sprite life |
| `GROWTH_FACTOR` | float | size growth over life |
| `DEVIATION_DISTANCE` | m | positional jitter |
| `TEXTURE_SEQUENCE` | [(name, t)…] | **flipbook**: frames keyed by **fraction of the sprite's own lifetime**, 0–1, *not* seconds (see below) |
| `TEXTURES` | [name…] | **static pool**: each sprite picks one at random (smoke101/102/103) |
| `COLORS` | [[lifeFrac, r, g, b, a]…] | colour-over-age ramp; rgb dual-encoded (the [weather.md](weather.md) rule: any component > 1 ⇒ ÷255), alpha 0–1. `dense_firetrail`'s smoke is born orange (255,164,90) → near-black |
| `FADE_RANGE` (= `FAR_FADE`) | [rampStart, cutoff] m | camera-distance fade: full alpha up to `[0]`, linear to zero at `[1]`, discarded beyond (`fire_n_smoke`: 1500–1700). Two spellings of one block, 574 readers say `FADE_RANGE`, exactly one (C3's `volcanosmoke`) says `FAR_FADE`. **Implemented** (`PufferState.FarFadeStart`/`FarFadeEnd`); the install's widest authored key at 2,508 compiled events over 238 puffers |
| `NEAR_FADE` | [cutoff, fullAlpha] m | near-camera band, compiled name `unk_range`. `[0]` is the **hard discard cutoff** and `[1]` the distance alpha would reach 1, ⚠ **do not read that order off the values**, five of the six authored pairs are descending (`70,20` almost everywhere). **Implemented** (`PufferState.NearFadeStart`/`NearFadeEnd`). With the shipped data it is a **cull, never a partial alpha**, see the distance-fade section below |
| `START_AGE_RANGE` | [min, max] s | random birth age, a particle is born at `Rand(min, max)` instead of age 0, negative values included (`fire_at_zepskin3`: −1.0 to 0.1). **Implemented** (`PufferState.StartAgeMin`/`StartAgeMax`); authored by only 4 puffers in the install, 80 compiled events total. ⚠ The key is not the whole birth age: the engine's `age0` is this draw **plus** `(1 - frac)·dt`, the sub-frame term of the time-cadence spawn, and it discards the particle outright when `age0 >= life`, so that skip fires on a long frame for **any** puffer, authored key or not |
| `WIND_FACTOR` | float | how strongly the world's wind carries this puffer's particles; **defaults to 1, not 0**, and is inert unless `FRICTION` is non-zero. **Implemented**, see [architecture.md](../architecture.md)'s `Effects/WorldWind.cs` entry |
| `PRIORITY` | float | a per-puffer sprite-size nudge, `1 + K·PRIORITY` with `K = 0.02` (the hardware-path constant, this project has no software path), folded into `BaseSize` at spawn. **Implemented** (`PufferState.Priority`, `Puffer.PriorityScaleDefault`), 192 compiled events over 47 puffers; default 0, so an unauthored puffer's factor is exactly 1. Almost certainly a depth-priority constant reused for size, this trace found only the size use |

## Emission accumulator

Both continuous modes run one accumulator on the emitter object, and the branch that feeds it is
where they differ: a **distance** emitter adds the frame's motion length, but only when that length
is under 200 m; a **time** emitter adds `dt`. Emission is then the whole
`floor(accumulator / interval)` with the remainder carried. The decoded form, offsets and
addresses are in [`../org/puffer.md`](../org/puffer.md#the-emission-accumulator-fun_0054f8b0)
.

Three things this settles for a reader of the authored keys:

- **The 200 m test is a teleport guard, and it exists only on the distance arm.** Time mode
  accumulates `dt` with no test of any kind. So the guard and a per-frame batch cap are not
  alternatives, they are not even on the same branch.
- **Nothing bounds `count`.** A long frame emits the whole catch-up in that frame; what stops it
  from being visible is not a cap but the spawn's own **born-dead skip** (`age0 >= life` ⇒ no
  particle), which the age offset above makes reachable for any puffer on any long frame.
- **The guard suppresses the frame's emission entirely**, not just the jump's share: the carried
  remainder is by construction below one interval, so `count` is 0 on a guarded frame.

⚠ The emitter's previous position (`+0x78`) is written **unconditionally** at the end of the tick,
guarded frame or not, and a `+0x84` "have I a previous position" flag suppresses the whole emit
block on the emitter's first ever tick. A teleport therefore costs exactly one frame of emission,
and the emitter resumes from the new pose with its remainder intact.

Distance mode is 1,523 of the install's 4,535 compiled `PufferState` events (33.6 %); the crash
debris trails `spurtpuffer1..5` are among them, which is the pooled-and-teleported case the guard
was written for.

## Camera-distance fade

Decoded from the head of `FUN_0054e6e0`, the original's per-particle draw, and implemented in
`Puffer.DistanceAlpha`. The distance `d` is the **view-space DEPTH** along the camera's forward
axis, in metres, not the euclidean range. (`FUN_0054ed10` pre-scales the view matrix's third column
by `_DAT_009fd5d0`, the draw multiplies by `_DAT_009fd5c0`, and on the hardware path `FUN_0053c110`
makes those exact reciprocals; on the software path they do not cancel and the distances come out
scaled, but this project has no software path.) The evaluation, in order:

1. discard if `d·globalFadeFactor >= FADE_RANGE[1]`;
2. if `d·globalFadeFactor <= FADE_RANGE[0]` the **near** band decides: discard if `d <= NEAR_FADE[0]`,
   full alpha if `d >= NEAR_FADE[1]`, else ramp;
3. otherwise ramp across the far band: `alpha = (FADE_RANGE[1] − d·globalFadeFactor) / (FADE_RANGE[1] − FADE_RANGE[0])`;
4. discard if the resulting alpha is `<= 0`.

The alpha **multiplies** whatever the `COLORS` ramp or the life envelope already produced; it
replaces neither.

⚠ **A cross-wire in the original, reproduced deliberately.** The NEAR ramp's origin is
`FADE_RANGE[0]`, not `NEAR_FADE[0]` (`0054e7b5 FSUB [ESI+0x3c]` against the near reciprocal at
`ESI+0x44`, read in raw assembly). It is consistent with `FADE_RANGE` being the original field and
`NEAR_FADE` bolted on later by copy-pasting the far-band line. C3's `volcanosmoke` is the only puffer
in the install whose near pair ascends and therefore the only one that reaches that ramp, where the
wrong origin drives its alpha negative, so it is culled below `NEAR_FADE[1]` and pops in at 75 m.
Every other near pair descends and takes the alpha-1 exit. **The near band is therefore a hard cull
on every puffer in the install**, and "fixing" the cross-wire would invent a 1→75 m fade-in the
original does not have.

⚠ **Almost every explosion effect in `flame_ball.zrd.json` authors `NEAR_FADE [70, 20]`**,
`fierypuffer`, `trailpuffer2`, `fire_n_smoke` and the ball family. Within 70 m of the camera the
original draws none of them, which is a large, visible consequence at any close chase-camera pose;
`puffer.nearCull` exists to switch it off, and defaults on because that is what the original does.

**Three config switches, all defaulting to the original's behaviour** (`puffer.distanceFade` the
authored far ramp, `puffer.farCull` the hard discard past the band, `puffer.nearCull` the near
discard), plus `puffer.globalFadeFactor`, the original's own `PufferSetGlobalFadeFactor`
(`00637a94`, default 1.0), which scales the FAR band only. ⚠ `puffer.farCull:false` alone changes
nothing visible: the authored ramp reaches zero at exactly the cutoff distance, so the two are the
same line. Keeping distant puffers drawn takes `distanceFade:false` and `farCull:false` together.

**`TEXTURE_SEQUENCE` times are lifetime fractions, not seconds.** Across all 1,750 flipbook
`PufferState` events in this install the largest key is **0.8** and none exceeds 1.0, while
`LIFETIME_RANGE` maxima run from 0.2 s to 5.5 s, so under a seconds reading nobody ever wrote a
sequence longer than 0.8 s for a 5.5 s sprite, 1,750 times running; and the 16 `mag_gunhit`
`firepuffer` events key frames out to 0.5 with a 0.1–0.2 s lifetime, which under that reading
could never draw at all. Read as seconds, `large_30sec_fire`'s `fire_n_smoke` burned
`fire_f01 → fire_f06` in a quarter second and then held the near-black smoke frame for the
other ~95 % of a 3.5–5.5 s life, which is what collapsed the game's most-called death effect
into a stationary red ball instead of a climbing flame.

**Rendering note: no `PUFFER_STATE` key states a blend mode, because blend is not the emitter's to
state.** A sprite draws additively exactly when bit 2 of its texture's render-flags word is set,
which makes the verdict per particle and per flipbook frame rather than per emitter; the decode is
[`../org/textures.md`](../org/textures.md). No texture any puffer in this install names carries the
bit, so every authored emitter alpha-mixes. Neither the `COLORS` ramp nor the sprite's own
brightness enters into it.

## Aircraft speed-cue wisps

Each chapter's `speed_cue.zrd` authors the pale puffs that appear ahead of the player's aircraft.
Although the animation and its three puffer sequences are `ON_CALL`, player setup starts
`speed_cue`; its controller then loops every 0.1 s. This is why a census that excludes `ON_CALL`
puffers incorrectly misses an ambient effect.

The controller disables the effect within 50 m of the ground. By camera altitude it selects a
30 m interval / 18 m deviation puffer below 800 m, 15 m / 25 m from 800–900 m, 8 m / 30 m from
900–1200 m, and 15 m / 25 m from 1200–1500 m. Each emitter is attached to `player` at local
`(0,0,-60)`, 60 m ahead of the aircraft. All use zero base velocity, ±0.8 m/s random velocity,
2.5–4.5 m initial size, 3–4 s lifetime, growth 1.25, and random static
`smoke101`/`smoke102`/`smoke103` textures. Their colour ramp is transparent white → low-alpha
white at half-life → transparent black. C1 uses peak alpha 0.4/0.5/0.5; C4 uses 0.6/0.7/0.7.

The generic distance-puffer update leaves emitted particles in world space. Consequently the
aircraft passes through each puff, and its screen-visible duration falls approximately inversely
with airspeed. This effect is separate from both chapter cloud-card populations and the
hard-coded throttle-rise exhaust below. CSVM implements it in `Flight.Hud.SpeedCue`, loading the
chapter reader verbatim and assigning one private renderer set to each player rig.

## Aircraft throttle-rise exhaust

`crimson.exe` also constructs one puffer outside the authored `PUFFER_STATE` readers. Aircraft
initialization (`FUN_00476250`) resolves `exhaust%d` model nodes (`exhaust1`, `exhaust2`, …)
and calls `FUN_004af9e0` → `FUN_004afa20` once per node. The latter allocates a generic puffer
through `FUN_00550100` and attaches it at zero local offset.

It is a distance trail: one particle per **0.4 m**, random velocity **−0.1..+0.1 m/s** on each
axis, initial size **0.2..0.3 m**, lifetime **0.5..1.5 s**, friction **1.2**, deviation **0.001 m**,
and normalized scale **1.0 → 3.45** over life. Each particle chooses `smoke101`, `smoke102` or
`smoke103`; the colour ramp is near-black at age 0.2 and transparent black at age 1, with a
**200..300 m** far fade (`FUN_00550550`, the `+0xdc` band).

**The drive has no threshold.** Every frame of normal flight, `FUN_0048e580` (`0x48e58f`–`0x48e5d2`)
takes commanded minus live throttle (`[obj+0x124] − [obj+0x128]`), before that frame's 0.5/s slew,
and hands it to `FUN_004afbc0` for each exhaust wrapper; scripted path flight (`FUN_00490590`) hands
0. `FUN_004afbc0` adds `dt · gap` (`DAT_009ad744`, the frame dt) to an intensity at wrapper `+0x8`
when the gap is not negative, then multiplies it by `exp(−1.5 · dt)` (`FUN_00460470` →
`FUN_00460410`, a three-term series below 0.1). At or below **0.01** it switches the emitter off
(`FUN_00550350(…, 0)`, which also drops the trail's previous position). Above it, it rebuilds the
colour ramp: RGB `(7, 7, 9)/255` with alpha `min(intensity, 1)` at age 0.2, transparent black at
age 1. The ramp container is refcounted and each particle holds the one current at its birth
(`FUN_0054f8b0`, particle `[0x1d]`; `FUN_0054f6d0` drops the emitter's reference on a rewrite), so
the opacity is fixed per particle at spawn.

What follows from the constants at a 60 Hz step: an idle-to-full digit slam peaks at intensity
0.356 about 0.9 s in and is off 3.9 s after the command; idle to 5/8 peaks at 0.18; a single 1/8
step peaks at 0.013 and is off within 0.4 s, which is why the CAP-21 footage shows no plume for it.
A held throttle-up key moves the commanded lever at the slew's own rate, so the gap stays one
step's slew and the intensity settles below the 0.01 floor. The generic puffer update
`FUN_0054ee10` → `FUN_0054f8b0` samples each exhaust node's world transform and leaves emitted
particles in world space, which is why they pass behind the moving aircraft.

`FUN_00476250` builds the wrappers for every aircraft that carries `exhaust%d` markers, not only
the player's. CSVM implements it in `CSVM/src/Flight/Airframe/ExhaustSmoke.cs` and builds it on the human
and the AI rig alike, each fed its own pilot's gap. The trails' particle scatter draws on the
shared puffer seed stream, as the original's generic particle spawn (`FUN_0054f8b0`) calls the CRT
`rand` every other puffer calls, so each launched aircraft with exhaust markers re-seeds the
emitters built after it.
The full Ghidra trace of this emitter and the speed-cue wisps, and of the mechanisms ruled out on
the way to them, is in `analysis/bl-317-plane-wisps/FINDINGS.md`.


## Texture flipbooks

The install animates textures through **three** distinct mechanisms. They are easy to confuse
because they share the same frame sets (`fire101-112` etc.), so:

1. **Puffer flipbooks** - `PUFFER_STATE`'s `TEXTURES`/`TEXTURE_SEQUENCE`, played per *particle*.
   Implemented (`src/Effects/Puffer.cs`); this is what animates crash fireballs and damage trails.
2. **Material cycles** - a gamez material's own `cycle` block: `texture_indices` (the frame
   list), `speed` (fps), `looping`. Played on the *surface*.`r`n (`src/Mech3/TextureCycler.cs`).
   Only 1-7 materials per chapter carry one, but they cover the animated sea: C1B has `wtr00000` x16
   @10 fps over 695 polygons and `srf0001` x16 @9 over 375, plus `wakefront1` x5 @12 (boat wakes)
   and `turb01` x6 @12 (turbulence); C1 has `splash01` x3 @4 and the `bmanwalk`/`bmanrun` x6 @9
   crowd sprites. `ObjectCycleTexture{name, reset}` (144 events, carrying no frame list of its own)
   is the anim-side trigger for these - still unimplemented, so cycles currently run free rather than being started/reset by animation.
3. **The `EFFECTS` reader** - `extracted/zrdr/effects.zrd.json`, the same idea reached through a
   proxy NODE:

   ```
   ["fire1.flt", "NAME", ["fire1"], "SPEED", [10.0], "LOOPING", ["ON"],
    "MAPS", ["fire101.tif", ..., "fire112.tif"]]
   ["fire2.flt", "NAME", ["fire2"], "SPEED", [5.0],  "LOOPING", ["ON"],
    "MAPS", ["fire101.tif", ..., "fire106.tif"]]
   ```

   Exactly two entries exist, and no compiled anim definition anywhere has a non-null `effects`
   array, so this reader is the only source. `fire1`/`fire2` are single `Facade`/`CylindricalY`
   quads (`fire101.tif`/`fire102.tif`) under **parentless template nodes** loaded from
   `common\effects\models\` by `support\load.gw`.

   **The entry names a node, but what it animates is that node's MATERIAL** (decoded out of
   `crimson.exe`, see [`anim-definitions.md`](anim-definitions.md#fire-animations)).
   The engine resolves the node, walks to the first mesh under it, and installs the frame list on
   surface 0's material record, which is the same per-material cycle block as (2); the draw loop
   then tests the material's own cycled bit per polygon. Materials are one record per texture, so
   `fire1.flt` is a **proxy** exactly like the interp's `watersetup`/`surfsetup`, and the cycle
   reaches every polygon on that material. `src/Mech3/EffectCycles.cs` implements this,
   which applies both entries to their gamez materials before the world build so the existing
   `TextureCycler` picks them up. It needs no `OBJECT_ADD_CHILD` and no burn site.

**C1's refinery gas flare IS a flipbook.** `flame01` shares material 88 (`fire101.tif`) with the
`fire1` template, so the EFFECTS cycle installed on that material animates it: 12 frames at 10 fps,
looping, from load, with no trigger. `mb1` and `mb_spinflame` are on the same material and flip in
lockstep with it. Its `LIGHT_STATE` flicker is real and separate - `refinery_fire.zrd.json` cycles
`orange_light`'s range 2->11, 3->15, 1.5->10, 2.5->14 in a tight loop - so the flare both animates
its texture and pulses its spill. ⚠ This entry claimed `flame01` was a static billboard
whose apparent motion was only the light; that was wrong, and the muzzle-flash observation behind it
did not survive the draw-loop decode.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
