# The puffer particle system, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-08-09/10. Every claim below names
the function it came from.

Everything here is a description of *behaviour and constants*. No decompiler output is reproduced;
the addresses are given so any claim can be re-checked at source.

**Where the other halves live.** The authored side, the `PUFFER_STATE` key list, the reader files,
the flipbook textures, the compiled-event schema, is [`formats/effects.md`](../formats/effects.md)
and [`formats/anim-definitions.md`](../formats/anim-definitions.md). Our implementation is
`CSVM/src/Effects/Puffer.cs`, whose entry in [`architecture.md`](../architecture.md) carries the
plumbing and the traps. This page is the original's runtime: what the engine does with those keys.

⚠ **This page is a decode, not a proposal.** Where it disagrees with a footage measurement, the
decode wins and the disagreement is a note. Where CSVM deliberately differs, that is listed at the bottom rather than hidden.

## Function map

| Address | Role |
|---|---|
| `FUN_004f7120` | `PUFFER_STATE` reader/parser (`zeff_anim_init.c`), one key-block per authored key, each setting a bit in the def's flag word at `+0x30` |
| `FUN_004e7e40` | Event applier, def → live puffer: flag-gated setter calls, and therefore the def-offset → object-offset map |
| `FUN_00550100` | Puffer object constructor (0xEC bytes), the unauthored defaults |
| `FUN_00550500` / `FUN_00550550` | The two fade-band setters; each stores the band, then its reciprocal |
| `FUN_0054ee10` | The global tick: gusting wind, then every emitter, then every particle |
| `FUN_0054f8b0` | Emitter tick, the emission accumulator, and the particle spawn with its draw order |
| `FUN_00550970` | Particle teardown (frees the per-particle texture-sequence vector) |
| `FUN_0054e6e0` | Per-particle draw: distance fade, colour ramp, scale ramp, final radius |
| `FUN_0057c5c0` | Screen-space sprite quad: `±r` in both axes, the 1-pixel cull, the UV clip |
| `FUN_0054ed10` | View-matrix setup, pre-scales the third column, which is why the draw's distance is a view-space DEPTH |
| `FUN_0054d9c0` | Writes the `PRIORITY` size constant `K`: `0.01` software, `0.02` hardware |
| `FUN_0053c110` | Hardware-path projection setup, makes the view/draw scale factors exact reciprocals |
| `FUN_005a8c00` | Installs the rasteriser dispatch table the draw's final call selects an entry of |
| `FUN_005a4b70` / `LAB_005a4580` | The two sprite entries themselves, ramp / ramp-less (see the blend section) |
| `FUN_004bc680` | The weather reader (`D:\zipper\Crimson\weather.cpp`), reads the `WIND` block that feeds the tick's gust, converting `RANDOM_ANG_VEL` degrees→radians by `0.017453292` on the way in |
| `FUN_00550690` / `FUN_005506b0` / `FUN_005506c0` / `FUN_005506d0` | The four one-line wind setters: `STATIC_VELOCITY`, `RANDOM_MAX_SPEED`, `RANDOM_ACCEL`, `RANDOM_ANG_VEL` |
| `FUN_005b80a0` | The debug console, exposes the same four as `GlobalWindStaticVelocity` / `GlobalWindRandomMaxSpeed` / `GlobalWindRandomAccel` / `GlobalWindRandomAngVel`, which independently confirms the mapping |

Two script-exposed globals, `PufferSetGlobalAgeFactor` (`00637a90`) and
`PufferSetGlobalFadeFactor` (`00637a94`), both default to `1.0`. Nothing in this install writes
either.

**What the fade factor actually multiplies:** the distance the **FAR** band is measured at, and
nothing else, both near comparisons in `FUN_0054e6e0` use the plain, unscaled depth. Below 1 it
pushes the far fade and its cutoff outward; above 1 it pulls them in. It is a script/debug-console
knob, so it ships at its own `1.0`; CSVM exposes it as `puffer.globalFadeFactor` rather than
reaching for a distance multiplier of our own.

**Parser flag bits** named by this pass, in the def's flag word at `+0x30` (`FUN_004f7120`):
`FADE_RANGE` and `FAR_FADE` are **two spellings of one bit**, `0x1000` (the install authors
`FADE_RANGE` 574 times and `FAR_FADE` exactly once, C3's `volcanosmoke`, so the alias is not
hypothetical); `NEAR_FADE` is `0x80000`, `WIND_FACTOR` `0x100000`, `PRIORITY` `0x400000`.

## The emitter object (0xEC bytes)

Offsets as mapped by the applier `FUN_004e7e40`:

| Offset | Key | Offset | Key |
|---|---|---|---|
| `+0x04` | `NUMBER` | `+0x64` | `DEVIATION_DISTANCE` |
| `+0x10`…`+0x18` | colour-ramp vector | `+0x68` | `FRICTION` |
| `+0x30`/`+0x34` | scale-sequence vector | `+0x6c` | `WIND_FACTOR` |
| `+0x3c` | interval is by distance (flag) | `+0x70` | `PRIORITY` |
| `+0x40`/`+0x44` | interval, `1/interval` | `+0x78` | previous emitter position |
| `+0x48` | the emission accumulator | `+0x84` | "has a previous position" flag |
| `+0x4c`/`+0x50` | `SIZE_RANGE` min/max | `+0x88`…`+0xcc` | six vec3s: translation, local vel, world vel, min/max random vel, world accel |
| `+0x54`/`+0x58` | `LIFETIME_RANGE` min/max | `+0xd0`…`+0xe4` | `NEAR_FADE`, `FAR_FADE`, and their reciprocals |
| `+0x5c`/`+0x60` | `START_AGE_RANGE` min/max | | |

**The constructor's defaults** (`FUN_00550100`) are the answer to every "what does an unauthored
key do?" question, and two of them contradict guesses this project had shipped:

| Field | Default | Note |
|---|---|---|
| `TIME_INTERVAL` (`+0x40`, `+0x44`) | **1.0** s | `0x3f800000` written to both the interval and its reciprocal, and ours. The setter (`FUN_00550460`, gated on flag `0x180`) also refuses a **zero**, which is what the compiled shape carries for a state that never authored the key |
| `NUMBER` (`+0x04`) | **1** | Settles `BL-218`: our fallback of 1 is the engine's own, and raising it would be a divergence |
| `SIZE_RANGE`, `LIFETIME_RANGE` | 1.0 | |
| `WIND_FACTOR` (`+0x6c`) | **1.0, not 0** | So an unauthored puffer is FULLY wind-carried; only an explicit `0.0` opts out |
| `NEAR_FADE` | `(0, 0)` | i.e. no near cull |
| `FAR_FADE` | `(FLT_MAX, FLT_MAX)` | The equal ends are why the reciprocal is stored as the raw difference (`0`) rather than an infinity |
| `PRIORITY` (`+0x70`) | 0 | So the size factor `1 + K·PRIORITY` is exactly 1 unless authored |

⚠ **Absent and explicit-zero must stay distinguishable, and in the shipped data they are.** The
`WIND_FACTOR` default is what 2,802 of the install's 2,863 friction-bearing compiled events run
on; reading an absent key as 0 would becalm all of them silently. The compiled surface writes
`wind_factor: null` when unauthored (4,423 of 4,535 events) and `0.0` when a puffer deliberately
opts out (6 names, 12 events, the `subdoors_puffer` family), and only 5 reader blocks in the
install author the key at all (3 at `0.3`, `torpuffertrail1`/`2` at `1.0`). The same rule holds for
`PRIORITY` (47 puffers, 192 compiled events author a non-zero value) and for both fade bands, whose
compiled fields are present-and-null on the great majority of events, a null far band read as zero
would discard every particle of every puffer that says nothing about distance.

## The particle (0x78 bytes)

Float indices `0..0x1d`:

| Index | Meaning | Index | Meaning |
|---|---|---|---|
| `[0..2]` | position | `[0x11]`/`[0x12]` | near reciprocal / far reciprocal |
| `[3..5]` | velocity | `[0x13]` | friction |
| `[6..8]` | acceleration | `[0x14]` | wind factor |
| `[9]` | **base size** | `[0x15]` | priority |
| `[10]` | age | `[0x16]` | **`TEXTURE_SEQUENCE` cursor**, stride 8 `(time, textureHandle)`, **stepped, never interpolated** |
| `[11]` | lifetime | `[0x17]` | a **byte** flag, not a float |
| `[12]` | `1/lifetime` | `[0x18..0x1a]` | the per-particle texture-sequence vector (begin/end/capacity), heap-allocated per particle in `FUN_0054f8b0` and freed in `FUN_00550970` |
| `[0xd]`/`[0xe]` | `NEAR_FADE[0]` / `NEAR_FADE[1]` | `[0x1b]` | colour cursor into `[0x1d]`+4/+8, stride `0x14` = `(r,g,b,a,time)`, time key at `+0x10` |
| `[0xf]`/`[0x10]` | `FAR_FADE[0]` / `FAR_FADE[1]` | `[0x1c]` | **the scale-ramp cursor**, into `[0x1d]`+0x14/+0x18, stride 8 `(time, scale)`, lerped |
| | | `[0x1d]` | the shared, refcounted ramp container |

⚠ **The fade copy from object to particle is not a contiguous six-float memcpy.** `FUN_0054f8b0`
writes the four bounds first and *then* the two reciprocals, so an implementer who mirrors the
object's `+0xd0`…`+0xe4` order onto the particle lands the wrong value in `[0xf]` and `[0x11]`.

⚠ `[0x16]` and `[0x18..0x1a]` are the **texture** sequence, not the scale ramp, this plan's first
draft read them as a per-particle copy of the scale ramp and was wrong.

## The tick (`FUN_0054ee10`)

Once per frame, in this order: derive the one global gusting wind vector; tick every emitter
(spawning); then integrate and reap every particle. The per-particle body is three steps, and the
**order is load-bearing**:

1. `pos += v · dt`, on the velocity the particle had at the top of the frame;
2. `v += a · dt`;
3. only when `FRICTION != 0` (the gate at `0054f016` is an explicit compare against 0.0, not an
   `exp(0) == 1` identity): `v = (v − wind·WIND_FACTOR) · exp(−FRICTION·dt) + wind·WIND_FACTOR`.

**Friction damps toward the WIND, not toward rest.** With a non-zero wind that gives a particle a
terminal velocity of `wind·WIND_FACTOR + a/FRICTION` rather than `a/FRICTION`, and a frictionless
puffer feels no wind at all, the gate is what keeps the two coherent.

**Reaping is `age >= lifetime`, with no sign test anywhere.** A particle born with a negative age
(`START_AGE_RANGE` authoring a negative minimum, e.g. `fire_at_zepskin3`'s `(−1.0, 0.1)`) therefore
simply lives `|age₀|` longer than its authored lifetime. It is *drawn* on the frame it is born,
because the draw clamps the ramp parameter (`if (0.0 < age) t = age/life; else t = 0.0f`, confirmed
in raw x87 at `0054e777`) rather than skipping. **A negative start age is a stagger-and-hold, not a
spawn delay**: the particle sits pinned to stop 0 of the scale ramp, the colour ramp and the life
envelope alike while still moving under velocity, friction and wind.

Only four puffers in the install author `START_AGE_RANGE` at all, so ~2,900 of them draw nothing
for it, which is why CSVM gates the extra draw on the key being present rather than always
drawing and multiplying by zero: a draw nobody needs re-scatters every particle downstream of it.

### The one global wind the tick derives first

The full decode of the gust, the four authored `WIND` keys, their globals and setters, the
random-walk model, the missing `dt` on the magnitude step, and the fact that the gust is purely
horizontal (`STATIC_VELOCITY.y` copied straight through at `0054ef5f`, only x/z composed from
`magnitude·cos/sin(heading)`), is written up where the authored keys are, in
[`formats/weather.md`](../formats/weather.md)'s `WIND` section.
Three properties belong here, with the tick that derives them:

- **Frame 0 is the static vector alone.** The heading and magnitude globals live in BSS, so the
  engine's first frame starts from heading 0, magnitude 0, no gust until the walk has stepped.
- **The heading wrap happens BEFORE the negative-magnitude reflection and is not re-applied
  after it**, so the stored heading can sit above 2π for a frame. Harmless (`cos`/`sin` do not
  care) and reproduced rather than tidied.
- ⚠ **The engine's `±1` draw is `rand()·3.051851e-05 + rand()·3.051851e-05 − 1.0` with one
  `rand()` result reused**, i.e. `rand()/16384 − 1` over `rand()`'s `0…32767`, giving
  `[−1, +0.99994]`: not quite symmetric, and quantised to 1/16384. This idiom is the wind's, and
  it is deliberately **NOT** what the spawn deviation uses, that one is `(rand01 − 0.5)`, a
  different draw with a different range. Do not unify them.

## The emission accumulator (`FUN_0054f8b0`)

Both continuous modes run one accumulator (`+0x48`), and the branch that feeds it is where they
differ:

```
if (byDistance)  { len = |pos - prevPos|;  if (len < 200.0) accum += len; }
else             {                                          accum += dt;  }
count = floor(accum * (1/interval));            // +0x44 holds the reciprocal
for (b = 0; b < count; b++) {                   // NO per-frame cap
    frac      = (b + 1) * interval / accum;     // position along prevPos -> pos
    ageOffset = (1 - frac) * dt;                // the sub-frame birth age
}
accum -= count * interval;                      // remainder carried
```

Four things this settles:

- **The 200 m test is a teleport guard, and it exists only on the distance arm.** A respawned or
  pooled emitter that jumps across the world lays no line of puffs along the jump. Time mode
  accumulates `dt` with no test of any kind. `200.0` is an inline float immediate, not a `_DAT_`
  global (the same decompile renders the `PRIORITY` constant as `_DAT_00a06fb0`), so there is no
  world-scale parameter behind it.
- **The guard suppresses the frame's emission entirely**, not just the jump's share: the carried
  remainder is by construction below one interval, so `count` is 0 on a guarded frame.
- **Nothing bounds `count`.** A long frame emits the whole catch-up in that frame. What stops that
  from being visible is not a cap but the spawn's own born-dead skip, below. ⚠ A per-frame cap is
  not a harmless safety net: at 60 fps the install's tightest authored interval,
  `torpufferblast`'s 1 ms, the torpedo trail, 8 compiled events, asks for **16 batches every
  ordinary frame**, so any cap below that is a permanent divergence wearing a safeguard's name,
  and on a real hitch a cap defers the catch-up into a burst the engine never produces (the engine
  emits only the tail of batches young enough to still be alive and is back to normal next frame).
  The trade the uncapped loop leaves is bounded: the loop's ITERATION count is unbounded in `dt`,
  while its OUTPUT is still bounded by how many particles can be born alive.
- **Emission is spread along the emitter's motion**, not stacked on the current pose: batch `b`
  spawns at `prevPos + (pos − prevPos)·frac` and is born `(1 − frac)·dt` old.

⚠ The emitter's previous position (`+0x78`) is written **unconditionally** at the end of the tick,
guarded frame or not, and the `+0x84` "have I a previous position" flag suppresses the whole emit
block on the emitter's first ever tick. A teleport costs exactly one frame of emission, and the
emitter resumes from its new pose with its remainder intact.

### The spawn, and its draw order

Per batch, `NUMBER` particles, **in both modes**: the distance arm changes only what feeds the
accumulator, never the spawn, so a distance trail lays `NUMBER` puffs per interval with
`LOCAL_VELOCITY` in the emitter node's frame and the sub-frame age like any time emitter. ⚠ CSVM's
trail path once had its own single-puff spawn that dropped all three; the smoke screen's
`smokerpuff` (`NUMBER 4`, `LOCAL_VELOCITY 0,0,10`) is the authored case that showed it, laying a
quarter of its cloud with the 10 m/s on the world axes. The engine draws **lifetime, then start
age**, before its position draws; each position axis is
`prev + delta·frac + (rand01 − 0.5)·DEVIATION_DISTANCE`.

- **`DEVIATION_DISTANCE` is a HALF-width**: the offset is `±0.5·d` per axis, not `±d`. (Reading it
  as `±d` scatters eight times the authored volume.)
- **The born-dead skip**: `if (age₀ >= lifetime)` the particle is **not created at all**, no slot,
  no draw. `age₀` is the `START_AGE_RANGE` draw *plus* the sub-frame `(1 − frac)·dt`, so the skip is
  reachable for **any** puffer on any long frame, with no `START_AGE_RANGE` authored anywhere. A 5 s
  hitch hands the earliest catch-up batch a start age of ~4.8 s, past every lifetime in the install:
  the catch-up burst mostly evaporates, which is the engine's own answer to the uncapped `count`
  above.

## The draw (`FUN_0054e6e0`), and the sprite quad (`FUN_0057c5c0`)

```
radius_px = projScaleX * (1 + K * PRIORITY) * baseSize * scaleSeq(ageFrac) / z_view
```

`projScaleX` is the same factor applied to the x-column of the view matrix in `FUN_0054ed10`, so the
world↔screen ratio cancels: **the sprite is equivalent to a camera-facing world quad of side
`2 × baseSize × scaleSeq(ageFrac)`.** `SIZE_RANGE` is a **radius**, `FUN_0057c5c0` draws at
`screenX ± r`, `screenY ± r`, which is the factor of 2 CSVM had been standing in for with a judged
constant.

`K` is `0.01` on the software path and `0.02` on the hardware path (`FUN_0054d9c0`). This project
has no software path, so `0.02` is ours.

`FUN_0057c5c0` also carries a **1-pixel cull** (a sub-pixel sprite is dropped) and clips UVs against
the 320×200 viewport bounds. Neither is reproduced here: the cull is a fill-rate defence for a
software rasteriser, and at modern resolutions it would discard sprites the original kept.

**The camera-distance fade**, the two bands, their field order, the deliberate cross-wire in the
near ramp's origin, and the fact that the near band is a *cull* rather than a fade with the shipped
data, is written up where the authored keys are, in
[`formats/effects.md`](../formats/effects.md#the-camera-distance-fade-fade_range--near_fade). The
distance is a view-space **depth**, not a euclidean range. Four properties of it belong here, with
the draw:

- **It is a DRAW rule, not a sim rule.** The engine evaluates it at the head of `FUN_0054e6e0`
  while `FUN_0054ee10` knows nothing about it, so a discarded particle keeps living, moving and
  ageing and is simply not written this frame. Reaping is unaffected.
- **The distance alpha MULTIPLIES whichever fade already owns the sprite and replaces neither**,
  folded into a `COLORS` ramp's own alpha (`local_2c * fVar5`) or into the ramp-less life envelope
  (`local_2c * (1 − ageFrac)`) at `0054e6e0`. Getting that precedence wrong makes every ramped
  puffer invisible.
- **The far ramp reaches zero at exactly the cutoff distance**, so the authored fade and the hard
  discard past the band are the same line: dropping the cull alone changes nothing visible,
  because the `alpha > 0` gate removes what it would have. Keeping distant puffers drawn on modern
  hardware takes disabling the ramp AND the cull together.
- ⚠ **`NEAR_FADE [40, 5]`'s descending pair is not a typo and must never be "repaired" into
  `[5, 40]`.** Index 0 is the hard cull cutoff and index 1 is where alpha would reach 1, an order
  settled at six independent points in `crimson.exe` (parser, applier, the two setters, the ctor
  defaults, the spawn copy, and the raw x87 comparisons in `FUN_0054e6e0`) and **not** inferable
  from the authored numbers, five of whose six near pairs run downwards (`70,30`; `70,20`;
  `70,50`; `40,5`; `30,10`).

### The three ramps

| Ramp | Cursor | Stride | Interpolation |
|---|---|---|---|
| `SCALE_SEQUENCE` (age → scale) | `[0x1c]` | 8 `(time, scale)` | **lerped** |
| `COLORS` | `[0x1b]` | `0x14` `(r,g,b,a,time)` | lerped |
| `TEXTURE_SEQUENCE` | `[0x16]` | 8 `(time, handle)` | **stepped, never interpolated** |

The parser accepts up to six `SCALE_SEQUENCE` stops and **synthesises exactly the two-stop ramp
`(0, 1), (1, GROWTH_FACTOR)`** when the key is absent, which it is, in every reader in this
install. That is why the compiled `growth_factors` array is `(age, scale)` pairs and never a
min/max range, and why a two-point lerp is correct for all 2,906 authored events. Ramp times are
**fractions of the particle's own lifetime**, not seconds.

⚠ **Do not "repair" a `growth_factors` entry whose `max` sits below its `min`.** 216 events author
a second entry like that, down to `(1.0, −0.2)`: coherent as a `(time, scale)` stop, incoherent as
a range, and swapping the pair silently rewrites the authored ramp. (An earlier "matches 172 of 177
puffers, five name collisions" survey of this field was **withdrawn**, it does not reproduce;
there were zero real mismatches, and the unexplained names were wildcard reader names expanding at
compile time.)

⚠ **Reading `TEXTURE_SEQUENCE` times as seconds rather than life fractions is a silent
catastrophe, not a rounding error.** No sequence in the install keys a frame past 0.8 across
lifetimes from 0.2 s to 5.5 s, and the `mag_gunhit` firepuffers key frames out to 0.5 with a
0.1–0.2 s lifetime, which under a seconds reading could never draw at all. Read as seconds, a 5 s
`fire_n_smoke` particle burned `fire_f01`→`f06` in a quarter second and then held the near-black
smoke frame for the remaining 95 % of its life, which is what collapsed `large_30sec_fire` into a
stationary ball instead of a climbing flame.

## Where CSVM deliberately differs

Everything here is a known, deliberate divergence, not a gap waiting to be closed.

| Divergence | Why |
|---|---|
| **The still-host synthetic `0.1 s` cadence** on `DISTANCE_INTERVAL` states | The engine has no such fallback: a distance emitter whose host never moves emits nothing. Ours sputters, so a damaged building smokes. ⚠ It rides `PufferState.TimeInterval` but is not that field's default: `StillHostSputterInterval` is ours and `TimeIntervalDefault` is the ctor's 1.0, and merging them would either slow every static sputter tenfold or speed every unauthored state tenfold |
| **`TrailBurnAt`**, spending *virtual* metres at a held pose | For hosts that cannot move (the damage lab's parked plane). The engine has no equivalent, which is also why the 200 m teleport guard is not applied to it: there is no motion length to test |
| **The 1-pixel cull** (`FUN_0057c5c0`), skipped | A software-rasteriser fill defence; at modern resolutions it would discard sprites the original drew |
| **`K = 0.02`** rather than the software `0.01` | This project has no software path |
| **The rim fade** (alpha eased to zero over the outer 12 % of the quad) | `FUN_0057c5c0` has no rim treatment. Ours hides the border pixels some frames leak (`fire_f01`'s edge reaches 247), which would otherwise draw faint rectangles on large grown quads. On `smoke101`..`103` it changes nothing measurable: their outer band averages alpha 0.03, and the exhaust plume measures the same darkening with it removed |
| **The life-fade envelope** (ease the additive glow in/out) | A render nicety with no authored key behind it. It is bypassed entirely whenever a `COLORS` ramp is present, since the ramp owns the alpha |
| **The soft-particle depth fade** (over the last ~1.5 m before the scene depth) | A render nicety with no counterpart in the original, softening the hard line where a tilted billboard dips into terrain. Off for a sprite set that dies dark, whose ground-level sites would otherwise fade every fresh puff to invisible against the terrain right behind it; on for the rest, which leak through it anyway |
| **Particle pools with a ceiling**, and pooled copies of each effect template | See the ⚠ below, INVENTED on both counts. A continuous emitter's pool doubles on demand up to `ContinuousPoolMax`, so only the ceiling is the divergence, not the starting size |
| **The `COLORS` ramp linearised in the shader** | Not a divergence but a translation: the ramp's bytes are DX7 framebuffer values (the smoke screen's `53,74,37`), and Godot's linear pipeline needs `csky_srgb_to_linear` on them to put the same byte back on screen, as every fullbright pass already does. Multiplied in raw, that ramp draws `109,126,92` against the reference's `50,68,35`, two shades too pale on every ramped puffer |
| **One alpha per particle across the panes** | The original evaluates the fade per particle per DRAW, so each splitscreen pane gets its own distances. Ours is one `MultiMesh` per emitter shared by every pane with the alpha written once per frame, so since `BL-339` the bands are run against EVERY pane's camera and the particle takes the most favourable answer: drawn if any pane should see it, at that pane's alpha. A pane can therefore see a puff its own camera would have faded further; per-pane alpha would take one MultiMesh per pane. Identical to the original wherever there is one viewer, which is every capture, freecam shot and single-player session. Relatedly, an emitter drawing on the very first frame of a session can beat the camera publish by one frame and draw unfaded, one frame of full alpha at session start, left alone rather than deferred |
| **One draw ORDER across the panes, taken from pane 0** | The same seam, one field further on: the instance buffer carries one order, so the back-to-front sort runs against the first pane's camera and every pane draws that order. Deliberately pane 0 rather than a most-favourable rule, because there is no favourable order, only a different one per viewer. A pane looking along a different axis can therefore see a nearer puff painted under a farther one. Exact wherever there is one viewer, and a per-pane order would take one MultiMesh per pane, the same price as per-pane alpha. With no camera published at all the order is the emitter's own, unsorted, which is the session's first frame and every unwired lab |
| **The mission's distance fog on a sprite** | The original's sprite quad is a pre-transformed vertex whose format (`0x1c4`, `FUN_005a4210`) carries a SPECULAR component, which is where D3D reads a per-vertex fog factor, and fog is enabled device-wide once at init (`FUN_005a0e00` sets state `0x1c` to 1 and `FUN_005a2f70(1)` sets LINEAR mode; nothing ever disables either). On the hardware path `FUN_0054ed10` writes that specular word as `0xff000000` on all four vertices, fog factor 255, so the original's sprite is unfogged. It rarely mattered: 238 puffers author `FADE_RANGE`, and every world emitter in C5 cuts off between 500 m and 1000 m against that zone's 1500–2250 m fog, so the particle is discarded before any fog could reach it. CSVM mixes the fog in anyway, because the cutoff only covers the emitters that author a band. A world `PUFFER_STATE` emitter is built with the session's own ambience, so it reads the mission wind and the pane cameras and its authored `FADE_RANGE` discards it short of the fog the way the original's did, but an unauthored range never culls, and such a puff reaches whatever distance its host and its lifetime carry it to. An unfogged puff out there would stand at full contrast against a hill that is already fog colour. Colour and cylinder are the sky's globals, so there is no second fog distance; at fog amount zero the mix is exact and an unfogged scene is byte-identical |
| ~~`puffer.fireRiseScale` / `fireLifetimeScale`~~ | **DELETED 2026-08-10.** The one invented multiplier this system carried, and it is gone, see below |

⚠ **Every pool size in this system is INVENTED, not decoded.** The particle pools (the trail
emitters' fixed cap, the sustained emitters' steady-state estimate `NUMBER × LIFETIME_max /
TIME_INTERVAL` and its floor/ceiling clamp) and the effect-template pools (how many copies of a
template the world-effects stage holds, in `CSVM/data/effect_pools.json`) have **no counterpart in
the original**, which copies its templates per call and bounds its particles only by what can be
born alive. Do not read any of those numbers back as an engine constant, and do not defend one by
citing this page, they are TUNE values, sized against measured live counts.

### The fire pair, measured and then DELETED (2026-08-10)

`large_30sec_fire`'s `fire_n_smoke` measured through the real emitter for its authored 30 s, still
host, heights above the emitter (suite `puffer-fire-column`). The tuned rows are the build as it
stood that morning; the pair was removed the same day:

| Arm | Centre apex | Drawn top | Drawn top at the pre-A1 sprite | Peak live |
|---|---|---|---|---|
| Authored, still air | 17.9 m | 25.9 m | 21.6 m | 50 |
| Authored, C1 IA1's wind `(0, 2, 0)` | 24.3 m | **32.1 m** | - | 50 |
| ~~Tuned (2.5× rise, 1.5× life), still air~~ | 50.7 m | 57.2 m | 53.7 m | 72 |
| ~~Tuned, C1 IA1's wind~~ | 60.1 m | **67.8 m** | - | 73 |

The decode moved the authored column from 21.6 m to 32.1 m, the doubled sprite (+4.3 m) and,
larger, the wind coupling: C1 IA1's `STATIC_VELOCITY` is `(0, 2, 0)`, straight **up**, and against
`FRICTION 0.6` with `WORLD_ACCELERATION −1` the terminal velocity is `2 − 1/0.6 = +0.33 m/s`, so the
column never turns over and climbs until the lifetime ends. **That +49 % is what made the invented
pair obsolete**: with it the plume drew to 67.8 m, i.e. the tune was contributing **2.11×**, and at
the controls that day the refuel-tank flames read as *"~twice the height of the originals"*. A
measured ratio and an eye agreeing to two decimals is as settled as this project gets, so both
scales and their config keys were deleted and the fire family now runs its authored numbers like
every other puffer.

⚠ **Do not re-add a rise or lifetime multiplier for the fire family.** If a fire reads wrong, the
candidates are the authored density (`BL-218`), the blend verdict (below), or the wind, not a
compensating constant keyed on one emitter's name.

⚠ **These are one seed's extremes, good to about ±1.5 m.** The height reported is the tallest
particle of the run, so it is a maximum over ~300 draws rather than a mean, and each emitter's RNG
stream is seeded by how many puffers were built before it. Read the ratios and the deltas, not the
last decimal.

### The blend verdict

**The blend rule is a property of the texture, not of the particle.** A sprite is drawn additively
if and only if bit 2 of its texture's render-flags word is set; otherwise it is alpha-mixed. The
full decode, including the texture header layout and the additive census, is
[`textures.md`](textures.md). `FUN_005a4210` is the explicit form: bit set gives `ONE, ONE`, bit
clear gives `SRCALPHA, INVSRCALPHA`. In the deferred transparent pass that particles actually take,
`FUN_005a6160` sets only `DESTBLEND`, so additive there is `SRCALPHA, ONE`.

**The `COLORS` ramp does not enter the blend decision.** `FUN_0054e6e0`'s final call is
`FUN_0057c5c0(pos, radius, texture, alpha, hasColourRamp)`, and that fifth argument does select
between two dispatch entries (`DAT_009be790` → `FUN_005a4b70` with a ramp, `DAT_009be78c` →
`LAB_005a4580` without, installed by `FUN_005a8c00`), but the two differ only in FLAT vs GOURAUD
shading and in whether the ramp colour survives into the vertices. Neither touches a blend register.
The ramp-less branch also forces vertex colour to white and alpha to `(1 − ageFrac)`, the engine's
own life envelope, which is unchanged from the earlier reading.

**Nothing about the sprite's darkness enters into it either.** `Puffer.Create` reads the flag once
per atlas column off the chapter's own `TextureArchive`, so the verdict is per particle and per
flipbook frame, and `MultiMeshEmitterRenderer` draws a column set spanning both blends as one
MultiMesh per blend. Godot's `blend_add` is `SRC_ALPHA, ONE`, which is exactly the sorted
transparent pass's additive.

**Not one puffer sprite in the install carries the flag**, so every emitter alpha-mixes. The
flagged textures all belong to other draw paths: the `fire101` … `fire112` mesh flipbook, the lens
flares, the impact rings and the HUD hilites.

### What the mixed sprite puts on screen

The rest of the draw path, from particle state to the pixel, on the hardware path
(`DAT_009be708 != 0`):

| Term | Rule | Where |
|---|---|---|
| Vertex alpha | `round(distanceAlpha × rampAlpha × 255)`, clamped to 0..255; the ramp-less branch uses `(1 − ageFrac) × distanceAlpha` | `0054ea98`–`0054eace` (`× 255` at `0x60414c`, `+ 0.5` at `0x6032e0`) |
| Vertex colour | each ramp channel **rounded as it stands**, `+ 0.5` (`0x603458`) and the `1.5 × 2^52` magic (`0x609158`), clamped to 0..255 and packed into the diffuse of all four vertices. No `× 255`: the ramp holds bytes. The ramp-less branch writes `0xffffff` | `0054e9e5`–`0054ead6` |
| Vertex fog factor | the specular word of all four vertices is `0xff000000`, fog factor 255, unfogged | `FUN_0054ed10` |
| Quad | `±r` about the projected centre, UVs 0..1 at the corners, clipped only at the screen bounds; no rim treatment | `FUN_0057c5c0` |
| Texture stage | `COLOROP` and `ALPHAOP` `MODULATE` (texture × diffuse), linear min, mag and mip filters. The flush's `SELECTARG1` alpha case needs texture kind 2, which the upload never assigns (`kind = hasAlpha ? 4 : 1` at `005a1e96`) | `FUN_005a0e00`, `005a6244`–`005a629b` |
| Surface format | an alpha texture WITH an alpha plane (object `+0x14`) uploads as ARGB8888 when the device offers it AND render-flags bit 3 is set (only `cloud1`, `cloud2`, `rotorblur`), else ARGB4444 when offered, else ARGB1555; one without a plane takes ARGB1555. `smoke101`..`103` carry graded plane alpha, so they take 4444 and their alpha is quantised to sixteenths, not thresholded | upload code at `0x005a1840`, `005a1adc`–`005a1c6f` |
| Blend | `SRCALPHA, INVSRCALPHA` over the framebuffer's bytes, depth writes off, sorted back to front | `FUN_005a0e00`, `FUN_005a6160` |

⚠ **The mixed blend runs on framebuffer bytes, and Godot's target is linear.** DX7 has no sRGB
framebuffer, so `SRCALPHA, INVSRCALPHA` mixes the gamma-encoded bytes. Godot's `blend_mix` mixes
linear values, and for a dark sprite over a bright background that is a much weaker darkening: a
black sprite at alpha 0.5 takes a byte-200 sky to byte 100 in the original and to byte 146 in a
linear mix. Mixed linearly, the exhaust plume darkens its background about half as much as the
original's.
`MultiMeshEmitterRenderer` therefore keeps `blend_mix` for the mixed layer and replaces its alpha:
against the opaque background it reads from `hint_screen_texture`, the source and the background go
to bytes and mix there, and the alpha written is the one at which a linear mix lands on that result
(on luminance, `(out − dst) / (src − dst)`). A single sprite over the opaque world is exact. A stack
of sprites is approximate, because each layer's alpha is taken against the opaque background rather
than against what the earlier layers left, and Godot's screen texture holds no transparent pass.
⚠ **The composite must stay a convex mix.** A premultiplied form that adds the byte-space change
as colour is exact for a black stack but adds, rather than mixes, for a light source, so a stack of
white mist sprites (the `c1-waterfall` golden) saturates to a flat glowing blob. The convex form bounds
every layer between its source and what is behind it. The cockpit panel's
`SceneBuilder.GammaBlendAlpha` is the same correction in the light-over-dark case.

⚠ **The exhaust ramp's colour is byte 1, not `7, 7, 9`.** `FUN_004afbc0` installs `7/255, 7/255,
9/255` into a ramp every other caller fills with bytes, so the rounding above turns each channel
into 1. `ExhaustSmoke.TrailState` carries the drawn byte.

**The two blends take the fog toward different values.** An alpha-mixed quad stands in for what is
behind it, so full fog leaves it at `csky_fog_color`, the value the hill behind it reaches. An
additive quad only brightens a background that already carries that colour, so mixing it toward the
fog colour would leave a distant fire brighter than the hill instead of sunk into it; full fog
leaves it contributing nothing. Since no puffer sprite in this install takes the additive path, that
half is a property of the shader and not of anything drawn here; the flagged textures the census
does list belong to other draw paths.

⚠ **The sprite-darkness measurement survives, and it decides the soft-particle fade alone.**
`Puffer.SmokeLuminance` thresholds the alpha-weighted luminance of the frame a particle dies on at
`16/255`, `fire_f06` 0.018 and `thickblksmoke` 0.004 below it, `fire101` 0.12, `exp_yel01` 0.17,
`smoke101` 0.22 and `fire_f01` 0.34 above, and a dark dying sprite turns the depth fade off,
because those sit at ground level where it would zero every fresh puff against the terrain behind
it. Wiring it back into blend would put the decoded rule back out.

**The draw order is the original's, in two halves.** `Puffer._Process` writes each frame's
particles to the renderer farthest-first on view depth, so an old near-black puff behind a young
bright flame no longer covers it; between emitters and against the world's transparent surfaces the
order is Godot's own per-camera transparent-object sort. What remains is granularity: Godot sorts
objects where the original sorts polygons, and one order serves every pane. The decode, the three
sort passes and both residuals are in [`textures.md`](textures.md), "The transparent list is
depth-sorted".

⚠ **A uniform-colour spray is order-invariant, so it cannot show an ordering change.** Alpha-mixing
quads that carry the same RGB and the same alpha gives the same pixel in any order, which is why the
waterfall and speed-cue goldens do not move for this and why the refuel tanks move only on the
frames where two flipbook columns are alive at once.
