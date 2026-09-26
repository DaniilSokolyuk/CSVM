# Aircraft definitions - `vehicle.json`

Part of the [format documentation](README.md). `vehicle.json` is the shared zrdr reader for
aircraft definitions. Its root alternates `defName, [properties...]`; definitions inherit through
`kind_of` and resolve properties nearest-first.

## Contents

- [Definition structure and inheritance](#definition-structure-and-inheritance)
- [Units, dynamics, and engines](#units-dynamics-and-engines)
- [Player global blocks](vehicle/player-globals.md)
- [The engine audio's slots](#the-engine-audios-slots)
- [Destroyable parts](#destroyable-parts)
- [Definition injury animations](#definition-injury-animations)
- [Collision probes](#collision-probes)
- [Effect emitters](#effect-emitters)
- [Weapons, damage, and AI keys](#weapons-damage-and-ai-keys)
## Definition structure and inheritance

Each def's property list is an alternating key/value-list dict. `kind_of` names the parent
def; properties resolve **nearest-first** through the chain (e.g. `pbloodhawk` →
`player_airplane` → `basic_airplane`). Player planes are the defs whose chain contains
`player_airplane`; `nodename` names the GameZ model root (`player_bhawk`). AI variants
(wingmen, pirates) are separate defs with the same shape but different numbers.

Keys the remake consumes (see `src/Flight/Airframe/PlaneStats.cs`):

| Key | Meaning |
|---|---|
| `kind_of` | parent def (inheritance chain) |
| `nodename` | planes.zbd model root node |
| `engine` | engines.json row id → power factor |
| `engine_sound` / `cockpit_engine_sound` / `prop_sound` | the three sound-def names (SETS in sounds.json) the engine audio's two slots draw from, see [The engine audio's slots](#the-engine-audios-slots) below. `prop_sound` is authored by no shipped def; `cockpit_engine_sound` is selected while the pilot's SELECTED view is the full Cockpit (`EngineAudioCurves.EngineDefFor`, `FlightAudio.UpdateEngineSlot`) |
| `damaged_engine_sound` | `[[soundName, pitchLo, pitchHi]]`, an array of candidates that REPLACE the engine slot's definition while the airframe is damaged. One shared `basic_airplane` entry (`snd_damagedengine`, 0.0, 1.0) covers every plane; the two floats are the pitch-multiplier draw range |
| `dynamics` | nested dict: `pitch_torque`, `roll_torque`, `rudder_torque`, `return_rate`, `ang_momentum_damp`, `rec_moments_inertia` (xyz), `fd_speed` (m/s), `drag_factor`, `veh_weight`, `ref_area`. The parser also accepts `level_off_rate`, which **no shipped def authors**, see below |
| `spin_props_anim` / `stop_props_anim` | prop-disc anim names (plane_props.json), stored at `def+0x18c`/`def+0x190` and swapped on the disabled-systems mask's bit-`0x2` edges, at spawn and at death. The stop side carries the `snd_propstop` one-shot and a blur-disc-to-still-blade cross-fade; the spin side is silent and instant. CSVM plays the anim names directly rather than reading these keys. Decode: [org/ordnanceTypes.md](../org/ordnanceTypes.md#what-the-masks-bit-2-edges-run) |
| `start_anims` | anims run at spawn (`wing_lights_blink`, `reset_bulletholes`) |
| `injure_anims` | def-level damage thresholds (below) |
| `destroyable_parts` | the damage model (below) |
| `collision` | 6 collision probe points (below) |
| `bullethole_anims`, `weapons`, `turrets`, `cannon_jam`, `armor`/`health`, AI tuning | only `turrets` and the count of `bullethole_anims` consumed, [Weapons, damage & AI keys](#weapons-damage-and-ai-keys) below |

## Units, dynamics, and engines

Units are meters/seconds: `fd_speed` 135 m/s ≈ 302 mph matches the Bloodhawk's published
top speed; `flight_ceiling` 2500 m. `player.json` holds player-global values,
`nom_gravity` = 20 m/s² (an arcade 2 g), plus the sound curve blocks
([sounds.md](sounds.md)).

The `dynamics` block: `rec_moments_inertia` is the *reciprocal* inertia per axis
(x = pitch, y = yaw, z = roll); steady-state rotation rate = torque · recInertia /
`ang_momentum_damp` (Bloodhawk roll ≈ 1.65 rad/s). `return_rate` is extra centering
applied when the stick is released.

⚠ `fd_speed` was documented here as "the full-throttle level-speed equilibrium". **Treat that as
unconfirmed.** The original's own name for the field is `FakeDynSpeed`, its tuner *measures*
`TopSpeed` separately from it, and at runtime it is read as a normalising reference speed
(`speed/fd_speed` fractions, an AI target speed, a clamp) rather than solved for. Working the
decoded drag polar backwards from each airframe's `fd_speed` also fails to close, the Balmoral
misses by 60 %. See [org/flightModel.md](../org/flightModel.md#thrustfactor-is-the-engines-power-factor--resolved);
the flight-model plan's B12/B13 own the question.

**The 13th `dynamics` field: `ThrustFactor` is not authored, it is engine power.** The shipped
Dynamics tuner names thirteen per-plane values where only twelve are authored keys. The extra one,
`ThrustFactor`, has **no parser token at all** (the literal appears exactly once in `crimson.exe`,
inside the tuner's CSV header) and **no shipped def authors a thrust-like key**: all 24 `dynamics`
blocks in this install author the same ten keys. The slot is filled at load from `engines.json`
via the def's `engine` property, and the hangar's engine-swap writes it directly. Confirmed both
in the binary and by data: ranking the eleven player airframes by
`power / (drag_factor · C_D)` reproduces their `fd_speed` order exactly (Spearman +1.000), which a
uniform thrust factor does not. `level_off_rate` is the mirror case, a token the parser accepts
that no def uses.

`engines.json` is a flat list of rows `[id, name, power]`; a plane def's `engine`
property picks its stock engine by id. ⚠ **Every player airframe's stock engine is its Lvl-2 row**
(ids 11, 14, 17, 20, 23, 26, 29, 32, 35, 38, 41, Bloodhawk: 11 = Lvl-2, power 0.62), never Lvl-1;
solving a constant from a Lvl-1 row inflates it by ~30 %. `power` is the plane's `ThrustFactor`,
and the original applies it as `Thrust = power · ref_area · thrustAvailable(Mach) · throttle`,
scaled by **reference area**, not divided by weight.

## Player global blocks

See [player global blocks](vehicle/player-globals.md) for the player-global reader reference.

## The engine audio's slots

One per-frame routine drives every aircraft's engine audio, the player's and each AI's, and it
holds **two** sound handles per vehicle. Both are positional or not by the sound definition's own
`3D` flag, never by a player check.

| Slot | Definition key | Notes |
|---|---|---|
| 0 | `engine_sound` | pitch and volume off the player-global `engine_sound` throttle curves |
| 0, in the Cockpit view | `cockpit_engine_sound` | swapped in while the camera is in the full Cockpit mode only, the Nose view keeps the plain def, confirmed at the controls of the original (an earlier "either cockpit mode" reading is retired); CSVM keys this to the pilot's SELECTED view being Cockpit, not the per-frame camera pose, so a held numpad key or look-behind does not retrigger it. No `*_cp` definition carries `FREQUENCY`, so the throttle pitch curve does not reach this one |
| 0, while damaged | `damaged_engine_sound[]` | the damage edge silences the slot and a random entry replaces the definition once the re-arm timer fires, then holds while the vehicle's disabled-systems mask is nonzero (below, "What makes an airframe damaged" and "The damaged engine's phases"); the entry's pitch range is drawn once and multiplies the throttle pitch curve, and on the shipped `snd_damagedengine` that product reaches nothing, since the definition carries no `FREQUENCY` flag ([sounds.md](sounds.md#a-definition-is-pitched-only-when-it-carries-frequency)); CSVM's port decision is that this wins over the cockpit swap when both apply, since no def authors a damaged cockpit variant and the interaction is not itself decoded |
| 1 | `prop_sound` | the overspeed whine, off the player-global `prop_sound` speed curves |

An AI vehicle's arm adds exactly one thing: both handles stop past **2000 world units** from the
player (compared as a squared distance against 4000000) and start again on the way back in. Rattle,
the collision one-shots and the landing one-shots are not part of this routine and are gated
elsewhere: the rattle on the vehicle the camera is watching, past `1.0× fd_speed`, at the engine
slot's own level ([org/shakes.md](../org/shakes.md#the-rattle-sound-is-a-gate-at-full-level-not-the-authored-ramp)).
⚠ **The pitch multiplier is not an AI/player fork.** Both arms rejoin at the damage test
(`0x004b19e2`), so an AI's damaged engine draws the same multiplier a player's does; the literal
1.0 store at `0x004b1b33` is the HEALTHY case for both, which the decompiler's branch order hides.

⚠ **One of the four rows is unreachable in the retail install.** No shipped def authors
`prop_sound` and the field has no compiled default, so slot 1 is never assigned and the whine never
plays for anybody. Every def does author `engine_sound` AND `cockpit_engine_sound` (`basic_airplane`
carries both as the fallback every plane either inherits or overrides), and every def inherits
`basic_airplane`'s single `damaged_engine_sound` entry, `cockpit_engine_sound` is fully reachable
in the retail data and is now selected by CSVM too, once the pilot has a view to select it with (D31).

### What makes an airframe damaged

The swap's condition is not "took a hit". `FUN_004b18a0` reads the vehicle's **disabled-systems
mask** at `+0x2dc` as a whole dword and takes the damaged arm when it is nonzero
(`MOV EAX,[ESI+0x2dc]; TEST EAX,EAX; JNZ`, at `0x004b194e` for the player and `0x004b19e2` for an
AI). Two of the mask's bits reach it in ordinary play: bit `0x2` is engine-out, raised by the
choker ([org/ordnanceTypes.md](../org/ordnanceTypes.md), "The choker, settled"), and bit `0x1` is
the damage state, raised by `FUN_004b1790` from the health ledger:

```
if mode class is 0 or 4 and bit 0x1 is clear:
    if the vehicle has destroyable parts:
        worst = min over parts whose flag byte [part+0x21] is set of  part[+0x30] / part[+0x2c]
    else:
        worst = vehicle[+0x2d0] / vehicle[+0x2cc]        ; whole-vehicle health current / max
    if worst < def[+0xbc]:  set bit 0x1
```

**`def+0xbc` is a quarter, and it is not authored.** No key in `FUN_00479240` writes the field; its
only writer is the def constructor `FUN_00478a00`, which stores `0.25` (`0x3e800000` at
`0x00478b44`), and the `kind_of` copy constructor `FUN_00477b70` propagates it down the chain
unchanged. It is the same shape as `ThrustFactor` and `level_off_rate`: a slot with no parser
token. Every def in the install therefore uses 0.25, and the test is strictly below it, so a zone
sitting exactly at a quarter health is still healthy.

⚠ **It is health only, and it is the worst ZONE, not the hull.** The divisor pair is the part's
health max and current; the armour pool at `+0x24`/`+0x28` is not read here. On an airframe that
resolves no `destroyable_parts` (every AI-roster chain) the whole-vehicle health fraction stands in
for it, which is the fallback the function itself has rather than a port's substitution. The
`critical` flag byte at `[part+0x21]` is what selects a zone into the minimum, and all 88 part
entries in the shipped `vehicle.zrd` carry it, so in this install the minimum is over every zone.

**The pitch multiplier is a draw, not a derivation.** On the frame the swap is made, the chosen
entry's flag byte `[entry+0x10]` gates a single

```
veh[+0x68] = lo + (hi - lo) * rand() / 32767        ; lo = entry[+0x14], hi = entry[+0x18]
```

which then multiplies the throttle pitch curve's output every frame until the mask clears, at which
point `0x004b1b33` puts the multiplier back to 1.0 (the product reaches
`IDirectSoundBuffer::SetFrequency` through `FUN_00597740`, called at `0x004b1e03`). Nothing in the
expression reads how hurt the airframe is, and the shipped entry's range is the full 0.0 to 1.0.
A cleared flag byte leaves the multiplier at 1 rather than drawing a zero.

⚠ **Nobody hears the draw in the retail install, and a port that plays it runs the damaged engine
too low.** `snd_damagedengine` carries no `FREQUENCY` flag, so its buffer is created without
`DSBCAPS_CTRLFREQUENCY` and every frequency write on it is refused
([sounds.md](sounds.md#a-definition-is-pitched-only-when-it-carries-frequency)). The loop sounds at
`engine_damaged.wav`'s own 22050 Hz, the multiplier and the throttle curve alike. The footage
confirms it: across six windows of three separate damaged flights in the CAP-14 captures, a
log-frequency spectral fit of the loop against the asset puts the playback ratio at 0.997 to 1.002
and the band peak at 183.0 Hz, the asset's own. The draw would have spread those samples from 0.18
(the mixer's floor) to 1.0. The port therefore computes the multiplier, so the decode stays
modelled, and passes the slot's pitch only where the definition accepts a write
(`EngineAudioCurves.SlotIsPitched`).

**The mask's edges do the swapping, not a per-frame comparison.** `FUN_004b1690` stops the slot-0
handle and re-runs the audio routine when the mask goes from zero to nonzero, and on the way back
restores `engine_sound` (never `cockpit_engine_sound`, which the next frame's healthy arm re-picks)
before re-running. The damaged arm additionally holds a re-arm timer on the **definition** at
`def+0x88`: it only starts a damaged loop once that timer passes a threshold redrawn as
`3.0 + 2·rand()/32767` seconds, so a damaged engine that has been silenced (an AI stopped past the
cull) waits three to five seconds before it sounds again. A looped `snd_damagedengine` that is
still playing never reaches the timer.

### The damaged engine's phases

What a player hears as "the engine goes out, sputters, restarts and runs stuttering" is three states
of slot 0 and one cue. There is no sputter or restart sound on the slot.

| Phase | Entered by | Slot 0 plays | Lasts |
|---|---|---|---|
| Healthy | spawn, or the mask clearing | `engine_sound` (or `cockpit_engine_sound`) | while the mask is zero |
| Out | the mask's 0-to-nonzero edge, or a damaged handle found dead | nothing | until the re-arm timer fires, 3 to 5 s from a zeroed timer |
| Damaged | the re-arm timer firing | a `damaged_engine_sound[]` entry at full level | while the handle plays and the mask stays nonzero |

- **Out.** `FUN_004b1690` sees the edge and calls `FUN_004b14e0(0)`, which stops slot 0, then re-runs
  `FUN_004b18a0`. From then on the damaged arm finds the slot-0 handle (`veh+0x70`) dead or not
  playing (`FUN_00593c90` is the is-playing query) and runs the timer instead of starting anything.
- **The timer.** Each frame the handle is dead the arm tests the value from before this frame's add:
  `if (t <= 3.0 || t <= 3.0 + 2·rand()·3.051851e-05) t += dt; else fire`. The `rand()` is drawn only
  once `t` is past 3.0, and because the threshold is redrawn every frame the fire in practice lands a
  little past 3 s, not uniformly in 3 to 5 s. The timer is the definition's (`def+0x88`), so a second
  airframe of the same type damaged later inherits the running total and restarts sooner. It resets
  to zero only on a fire; a heal leaves it where it was.
- **Damaged.** On the fire the arm picks a random entry, sets it on slot 0 with `FUN_004b1540(0, name)`
  and starts it with `FUN_004b1470(0, 1.0)`: full level, no `snd_propstart`, no ramp. The pitch
  multiplier is drawn on that frame (above). `snd_damagedengine` is `engine_damaged.wav`, a looped
  5.69 s recording of an engine running rough with no silence inside it, so the stutter is the
  asset's, not a modulation the routine applies. While the handle plays the timer is not read.
- **Healthy again.** The mask's nonzero-to-0 edge stops slot 0, restores `def+0x6c` (`engine_sound`)
  through `FUN_004b1560`, and re-runs the routine, whose healthy arm starts the handle with
  `FUN_004b1470(0, 1.0)` on that same frame and stores the multiplier 1.0 at `0x004b1b33`. There is no
  wait in this direction. Bit `0x1` clears on a heal because `FUN_004b8180` rewrites it from the
  whole-vehicle fraction against `def+0xbc` (`0x004b82af` sets, `0x004b82bd` clears).
- **The cull.** An AI past the 2000-unit cull skips the timer altogether, so a culled damaged engine
  is Out with a frozen timer until it comes back in range.
- **Bit `0x2` does not add a phase.** Only the choker sets it (`FUN_004b9bc0`, `0x004b9e34`); no graze
  or damage path does. `FUN_0048c470` counts `veh+0x2e0` down while it is set (`0x0048c606` to
  `0x0048c63e`) and clears it through `FUN_004b1690(0, 2, 0)` at zero. Its edges add the prop stop and
  spin (`FUN_004b15c0`, `FUN_004b1630`) but run the same slot-0 edges as bit `0x1`.

The original's Balmoral nose-graze capture (under `OriginalScreenshots/Videos`) shows the sequence. The healthy
band runs to the graze at about 23.7 s, the slot is quiet but for the graze's own low impact pulses,
and at about 26.8 s a steady band at 180 to 220 Hz and 340 Hz with the `engine_damaged.wav` spectrum
starts and holds. The 3.1 s gap matches the timer's floor from zero, and the band's 183.0 Hz peak
matches the asset played at its own rate (above, the pitch multiplier's draw).

### The engine slot's pitch and gain are not throttle alone

The throttle curve is only the first term. On an aircraft (mode class 0 or 4, the same gate the
death path uses) `FUN_004b18a0` adds a **manoeuvre** term and an **attitude** term to each curve's
normalised parameter, at `0x004b1c83`–`0x004b1d6b`:

```
w      = M(+0x180) · ω(+0x16c)              ; world→body, by ROWS
q      = fastsqrt(w.x² + w.y²)              ; rows 0 and 1 only
a      = [+0x19c]                           ; orientation row 2 . Y, and row 2 is −nose
tVol   = clamp(frac(throttle, volCurve)   + 0.26·q − 0.15·a, 0, 1.5)
tPitch = clamp(frac(throttle, pitchCurve) + 0.25·q − 0.15·a, 0, 1.5)
volume = volY0   + (volY1   − volY0)  · tVol
pitch  = pitchY0 + (pitchY1 − pitchY0)· tPitch
```

`fastsqrt` is the exponent-halving bit trick, not a library call: the sum is stored as a float and
reloaded as an int, shifted right one and offset by `0x1fc00000`, then reloaded as a float
(`0x004b1d09`–`0x004b1d19`). Constants read from the image: `0.26` at `0x00608b98`, `0.25` at
`0x006034f4`, `0.15` at `0x006036a8`, the clamp pair `0.0`/`1.5` at `0x006032c8`/`0x00603460`.

⚠ **`ω` is ANGULAR velocity, so `q` is a turn rate and there is no airspeed in this anywhere.**
`+0x16c` is written as torque × the reciprocal inertia at `+0x197`..`+0x199` (`FUN_0048e580`), which
is what fixes it as angular. The true speed scalar sits at `+0x934` and this routine does read it,
but only for the whine slot, never here. ⚠ **The component about the nose is dropped**: only the two
rows perpendicular to it are squared, so `q` is pitch rate and yaw rate combined and **roll rate
does not raise the engine note at all**.

The attitude term's sign is settled by row 2 being −nose ([org/flightModel.md](../org/flightModel.md),
"The sign is settled from the bytes"), so `a = −nose.Y` and `−0.15·a` **rises with climb and falls
with dive**.

⚠ **In the retail install the 0.26 volume term does nothing**, because the shipped `engine_sound`
volume curve is flat 1.0 ([sounds.md](sounds.md), "Player curves"): the term moves `tVol`, and
`volY1 − volY0` is zero, so the remap discards it. Only the pitch term is audible. Do not read the
two coefficients as a matched pair that both landed.

Boost short-circuits both curves rather than scaling them: with the boost flag `+0x947` set
(`0x004b1c51`), `tVol` is replaced by **1.17** and `tPitch` by **1.25** outright, which is what the
`1.5` clamp headroom above 1.0 exists for.

## Destroyable parts

A list of part entries:

```
[name, hp, hp, flags…,
 "got_hit_anim", [animName, rootName],
 "injure_anims", [[fraction, animName, rootName], …]]
```

- `name`: `nose` / `tail` / `leftwing` / `rightwing` for every player plane.
- **The pair is (hit points, armor)**, `[1]` is the zone's hit points, `[2]` its **armor pool**,
  spent first. Settled against the original's armory; see [below](#armor-and-hit-points).
- **The two values are equal in every entry**, all 88 parts across the 22 defs that carry
  `destroyable_parts` (11 player `p*` + 11 AI `r*`), measured; values 15/20/25/30/35/40. Equal
  because armor is **purchasable** and these are the *stock* allocations, not because the number is
  duplicated. `PlaneStats` reads both values (`DestroyablePart.MaxHp`/`MaxArmor`); the two-pool
  `PlaneDamage.Apply(part, healthDamage, armorDamage)`, armour first, 1:1 overflow, landed.
  this install has an unequal pair.)*
- Flags: `critical`, the plane is destroyed when this part reaches 0 HP (all four player
  parts carry it); `engine`, engine damage/power loss on that part.
  ⚠ **The `critical` reading is from the flag's name and the executable decode does not
  support it** ([`org/vehicleDamage.md`](../org/vehicleDamage.md)): the death path tests only
  whole-vehicle health, no code on it reads a part flag, and one zone at zero leaves the
  whole-vehicle summary at 75 %. Either the flag is consumed somewhere not yet found, or the
  reading is wrong. Do not build a kill rule on it without settling that first. Not tail-only: it sits on
  the tail for `pbloodhawk`/`pdevastator` but on the nose for `pautogyro`/`pbrigand`/`pfury`/
  `ppeacemaker`/`pbalmoral`/`pwarhawk` and on **both wings** for
  `pfirebrand`/`pavenger`/`pkestrel`, it
  marks the part the engine(s) physically live in. Handling penalties are unmodelled **by
  design**, not deferred: the original design states damage does not degrade an aircraft's
  performance, a plane on its last legs keeping full performance and lethality. The shipped
  flag may still drive something (sound, effects) the design text does not cover.
- `injure_anims`: **descending HP fractions**; when the part's HP fraction crosses one,
  the named anim runs. Two families interleave:
  - `<part>_damage_green` (0.72) / `_yellow` (0.46) / `_red` (0.20), the cockpit
    damage-indicator texture cycle (unwired until a cockpit exists). The retail manual describes
    this indicator (the "Crispen Mark V") as colouring each of nose/tail/left/right wing over the
    **combined** progression of both pools: yellow = up to half the zone's armor gone, orange =
    the rest of the armor plus the first quarter of the airframe, red = beyond that. With armor
    equal to hp at stock those bands break at 0.75 and 0.375 of the combined pool, and
    `_damage_green` firing at **0.72** is the expected one-state name lag (see the standing rule in
    `backlog.md`). ⚠ **A reading, not a decode**, `_damage_yellow` at 0.46 sits mid-band rather
    than on 0.375, so the correspondence is suggestive and does not pin the mapping down.
    Source: Crimson Skies PC manual, damage-indicator section,
    <https://manualmachine.com/gamespc/crimsonskies/1119420-user-manual/>.
  - `<part>_damage_effects` (0.99), **not part of that cycle, despite the neighbouring
    thresholds: this is the per-impact spark burst.** All four (`nose`/`tail`/`leftwing`/
    `rightwing`) are one-event shims calling `random_gun_impact` (anim root `player`) with no
    parameters, so the part identity is discarded by the data itself. `random_gun_impact` is an
    IF/ELSEIF `RANDOM_WEIGHT 0.4` / `0.4` pair choosing `yellow_sparks_follow WITH_NODE pdp1` or
    `pdp2`, **followed by an unconditional third call at `pdp4`**, so a hit sparks one panel or
    two, never none. `yellow_sparks_follow` (root `yellow_spark_02`) emits `trailpuffer2` +
    `chippuffer1` at its INPUT_NODE (the chosen panel) under a 50/50 pick between two
    `snd_ricochet1–4` sequences. ⚠ **Only `player_pfighter` ships the 0.99 entries**, measured
    install-wide, exactly 4 occurrences, all in the def whose `nodename` is `player_pfighter`
    (`title MSG_VEH_DEVASTATOR`), while each of the 11 planes spells its own `got_hit_anim`.
    Inheritance does not spread it: all 22 defs carrying `destroyable_parts` (11 `player_*`
    flyables + 11 lowercase AI variants) are `kind_of` a base that carries no parts list of its
    own. The other 10 aircraft have no per-impact spark at all.
    At 0.99 it fires on the *first scratch*, which is authored, not a threshold to retune.

    ⚠ **`random_gun_impact`'s real home is `weapons.json`, not here, read this entry as a probable
    authoring leftover (hypothesis).** It is the `player` **IMPACT surface animation**
    for `wep_03` (60slug), "what a bullet does when it hits the player's aircraft"
    ([weapons.md](weapons.md)), the counterpart of the `enemy` and `default`/`buildings` classes.
    That is a general mechanism gated on being shot at, which nothing can do in M3. One plane of
    eleven ALSO firing it off a damage threshold fits a leftover better than a per-aircraft
    feature, but no capture of the original settles it, so it is a reading, not a finding, and the
    entry is shipped data either way. Do not "fix" the other ten planes by adding the entry to them;
    that would be inventing content.
  - `pdpanelN`, flips the exterior torn-skin panel `pdpN` (planes.zbd nodes; the anims
    live in the plane's own reader, e.g. player-1.json). Left wing: pdpanel5 @0.5,
    pdpanel4 @0.3, pdpanel3 @0.15; right wing: pdpanel6 @0.4, pdpanel1 @0.3,
    pdpanel2 @0.15; nose pdpanel7 / tail pdpanel8 @0.15.

The `pdpanelN` anim (`ANIMATION_DEFINITION`, ON_CALL, one shared def per panel in
player-1.json, re-rooted per plane via the injure entry's rootName) sets
`OBJECT_ACTIVE_STATE pdpN ACTIVE` **without** deactivating the healthy `pdpN_h` twin,
and calls effect anims at the panel: `gimmeflakes` debris, `yellow_sparks_follow`,
`small_fireball_follow`, `short_firetrail` / `loop_short_firetrail`, the discrete-puff
fire trail streaming from every damaged panel (clearly visible in
`OriginalScreenshots/Videos/C1 IA1 Crash.mp4`). In fact **no zrdr data ever deactivates an
`_h` node**, yet `player_destruct_reset.json` (`plane_reset`) re-ACTIVEs
`pdp2_h`/`pdp3_h` alongside setting every `pdpN`/`pcdpN` INACTIVE, so the original
engine must hide the healthy skins at damage time by an engine-side rule. Beware: the
`pdpN`↔`pdpN_h` numbering is crossed on three plane models, pair torn↔healthy by mesh
position, not by name (measurements in `gamez.md`, "Player-plane damage states").

### Armor and hit points

**The two numbers on a `destroyable_parts` entry are that zone's hit points `[1]` and its armor
pool `[2]`, armor spent first.** Settled.

**How it was settled.** Every pair in this install is *equal*, so no measurement over the shipped
data can separate (armor, hp) from (hp, hp) or (max, current), the reading stood as a hypothesis
for that reason. The original's **armory breaks the tie, because it varies armor independently of
health**: its per-zone allocation is in units that are armor points 1:1, and a **stock** airframe
reads the same per-zone numbers the zrdr def carries (a stock Bloodhawk shows ~20 units on each of
its four zones; `pbloodhawk`'s parts are 20/20/20/20). Observed at the controls,.

**Confirmed end-to-end by `CAP-19`** (observed at the controls). Three results:

1. **Armor depletes before health.** The ordering retail string 3372 states, and that a dominance
   argument derives, is now *directly observed*, not inferred.
2. **The armory's per-zone cap is 60 units**, uniform across a plane's four zones. (Whether the cap
   varies by airframe is untested, one airframe was read.)
3. **A stripped zone falls far faster** than an armored one, green→red in visibly less time, more
   damage per hit. **Direction only, and deliberately not timed:** a live sortie moves ammo type,
   hit distribution, graze damage and pilot skill at once, so it cannot isolate a time-to-kill
   figure, and does not need to. With armor equal to hp at stock and armor spent first with 1:1
   overflow, the **2× effective pool is entailed by the model**, not a separate quantity to measure.

`CAP-19` is discharged and retired from [`playtest.md`](../../playtest.md).

⚠ **The zrdr number is the *stock* allocation, not a fixed property of the airframe.** A player
buys more. Every pair being equal is a fact about stock loadouts, **not** a licence to fold armor
into hp, see `BL-085`.

Corroborating evidence, all data-confirmed:

1. **The HUD showed two pools.** `messages.json` `MSG_HUD_HEALTH` = `Armor: %1%% Health: %2%%`,
   the shipped in-flight readout has an armor bar *and* a health bar.
2. **Weapons carry both damage figures, and they differ.** Every one of the 46
   `weapons.json` `BALLISTICS` entries with damage carries `ARMOR_DAMAGE` **and**
   `HEALTH_DAMAGE`, and 18 of them differ, the ammo matrix is built out of the split:
   `wep_N1` (dum-dum) is armor-light/health-heavy (`wep_31` 1.5 / 4.5), `wep_N2` (AP) is the
   mirror (`wep_32` 4.5 / 1.5), `wep_N3` (magnesium) is between. A two-pool target is the only
   thing that makes those numbers mean different things.
3. **Crash damage is two-pool too.** `player.json`'s `crash` block is
   `armor_damage_range [50,300]` + `health_damage_range [50,300]` + `bounce_factor`.
4. **Retail shipped a per-zone armor purchase.** `rof/ui_strings.json` id 1039 `IDS_AR_TITLE`
   = "3) ADD ARMOR", ids 1044–1047 = Nose / Tail / Left Wing / Right Wing, exactly the
   `destroyable_parts` names. Id 1155 `IDS_PX_ARMORINFO` prices and weighs armor per unit and
   warns "Left and right wings must be balanced!" (and indeed `leftwing == rightwing` in all 22
   defs); id 1170 `IDS_PX_ARMORUNITS` = "%1!d! units"; id 206 `IDS_PX_SWITCHAIRFRAMES` speaks of
   "the **default** armor, engine, and guns for this new airframe", a stock allocation exists.
   *(This replaces an appeal to the pre-release design spec, which
   [`playtest.md`](../../playtest.md) flags as unreliable as a class for HUD/damage material.)*
5. **Retail states armour-first depletion outright**, `ui_strings.json` id 3372 (AP: "hardened
   tip designed for shredding and destroying armor. WARNING: AP rounds tend to punch clean through
   unarmored surfaces, inflicting very little damage"), id 3371 (dum-dum: "very useful for
   finishing off aircraft that have already been damaged"), id 3410 (AP rocket: "remove most, if
   not all, of the armor from an aircraft but has no noticeable effect on unarmored surfaces").
   A gate, not a damage reducer. A dominance argument derives the same ordering; this is the
   direct statement.

**Still open: what `ARMOR: Standard (N/T/W)` is, and it is now known *not* to be the cap.** Five of
the eleven airframe blurbs carry a per-zone armor triple (`ui_strings.json` ids 40115 Balmoral
400/400/350, 40116 Bloodhawk 400/300/200, 40118 Fury 400/400/350, 40120 Warhawk 700/500/700, 40122
Autogyro 300/300/200). It is **not** the stock allocation, stock is ~20, and `CAP-19` has now ruled
out the per-zone-cap reading that stood in its place: the observed cap is **60 units, uniform across
a plane's four zones**, while every blurb triple is both far larger and *unequal* across zones.
Three measured constraints on what it can be: retail-triple ÷ zrdr-part-sum is
13.75 / 16.7 / 12.0 / 21.7 / 16.7 across the five, so **no linear map** relates them; triple ÷ 60 is
ragged for the same reason; and at the armory's observed 4 lbs/unit a **fully** armored airframe is
4 × 60 × 4 = **960 lbs** against a `veh_weight` of 1900, a real trade-off, where the
blurb-as-cap reading implied 1100 units and 4,400 lbs on a 1900 lb plane. That weight arithmetic was
already one of the two arguments against blurb-as-cap; the measured 60 replaces it with a figure the
weight model can carry.

**The armory's own constants are now decoded out of `crimson.exe`**, and they agree with `CAP-19`:
armour is **$4 and 4 lb per unit**, and the **per-zone cap is 60 units**, uniform across the four
zones and across all eleven airframes, because the cap is the armour dropdown's own row count
(13 rows labelled `r × 5` units) and nothing per-airframe gates it. `ui_strings.json` ships only
the printf templates; the numbers, the airframe/engine/gun/hardpoint price tables, the sell rule
and the campaign's starting funds live in the executable and are written up in
[`../org/hangar.md`](../org/hangar.md), "The economy" and "The campaign wallet". Two consequences
for this page: a fully armoured airframe is 240 units, $960 and 960 lb, and the per-zone triple in
the airframe blurbs (`ui_strings.json` 40115 and friends) stays **unexplained**, since 60 is a flat
cap and those triples are both larger and unequal across zones.

## Definition injury animations

```
"injure_anims", [[0.10, "player_smoketrail"], [0.85, "player_fuelleak"]]
```

Whole-plane effects. **The fractions are the whole-vehicle health fraction**, decoded from the
had them as any single part's fraction, on the argument that a total-HP reading could never reach
0.10 if a critical part killed the plane at 75 % total. The decode removes that argument: the
whole-vehicle fraction is itself the parts-weighted total, and nothing on the death path reads the
`critical` flag, so a plane reaches 0.10 by having all four zones nearly gone. Staging is also
**reversible** rather than a latch: the driver stops an anim again if the fraction climbs back above
its threshold. `player_smoketrail` starts the `dense_firetrail` pair (pufftrails.json)
following `prop1`: a black-smoke trail (COLORS ramp: born orange 255,164,90 → near-black
5,5,5) plus a fire trail, both `DISTANCE_INTERVAL` emitters (one puff per N meters of the
node's motion, 1.0 m smoke / 0.25 m fire). `player_fuelleak` runs a `fuel_trail` at a
random pdp panel (unwired).

## Collision probes

Each player def carries a `collision` list of six xyz points in the plane's local frame
(nose −Z, right +X, meters), the original's own collision representation: the contact
probes `FUN_0048d7f0` carries from the previous pose to this frame's pose and tests against
the world, the earliest strike along the motion winning (the list at vehicle `+0x6a4`, one
0x24-byte entry per point: local, world, previous world). pbloodhawk:

```
(0, 0, −5.68)  nose        (0, −0.20, 4.55)  tail
(+5.71, −0.82, +1.87) right wing   (0, +0.80, −1.46) top (canopy)
(−5.71, −0.82, −1.87) left wing    (0, −1.80, +3.28) belly
```

Note the left/right pair is point-symmetric (both z signs flipped), not mirrored,
probably hand-authored.

⚠ **Only the `p*` player defs author the list.** `basic_airplane` carries a single probe at
the origin, `(0, 0, 0)`, and no AI def (`fury`, `hafury`, `secfury`, the `w*` wingmen, the
`r*` remotes) overrides it, so every AI aeroplane in the shipped game collides as ONE point at
its centre: its wings and tail pass through anything. That is how the CM13 racers thread the
`dbase` arch on `dzpath2` (9.7 m wide at rail height, against the 10.4 m between the pfury
wing probes) and how any
AI clears a gap a player airframe cannot. The remake reads the list as
`PlaneStats.CollisionProbes` (nearest def in the damage chain, so the AI chain on an AI load)
and sweeps exactly those probes for an AI aircraft (`FlightController.SweepProbes`); a human
rig keeps the mesh-derived hull sweep, which is wider than the six points but never narrower
in a way a flown stunt has shown.

## Effect emitters

The effect emitters these anims call (`short_firetrail`, `dense_firetrail`,
`large_fireball`, …) are `PUFFER_STATE` definitions, full schema in
[effects.md](effects.md).

## Weapons, damage, and AI keys

The airframe half of the combat data, of which only `turrets` is consumed by the remake so
far. Player defs carry `weapons` (as a catalogue), `cannon_jam`, `turrets` and
`bullethole_anims`; the `armor`/`health` pair and the AI-tuning keys live only on the AI
variant defs.

**`weapons`** is a list of 5-tuples
`[weapon_id, rounds_carried, refire_interval_s, min_range_m, max_range_m]`, ids into
[weapons.md](weapons.md). On `player_airplane` it is a **capability catalogue, not a
loadout**: all 39 buyable ids at once (`wep_00`–`15`, `25`/`27`/`28`, and the full
`wep_30`–`73` player matrix), each with position-5 range `10000`, a UI sentinel, since the
real per-plane loadout is executable-resident. On an AI def it is the actual armament:
`bloodhawk` = `[["wep_04",4,200,30,800],["wep_07",2,200,30,800],["wep_00",9000,0.05,1,900]]`, so
one gun at 8000–9000 rounds over 1–900 m, plus one or two ordnance entries of 2–8 rounds over a
200–800 m **band** (the boat and truck carry a gun alone, 1–500 m). **All five fields are
decoded** from the builder `FUN_004b59b0`, which squares the last two into the vehicle's weapon
slot ([org/aiPilot/aiWeapons.md](../org/aiPilot/aiWeapons.md)); the per-def census is
`analysis/ai-ordnance-census/`.

⚠ **Five base defs author positions 3 and 4 transposed** against all 25 militia variants:
`firebrand`, `bloodhawk`, `brigand`, `fury` and `autogyro` say `200, 30` where the variants say
`30, 200`, so the engine gives them a 200-second ordnance refire at a 30 m floor. Shipped data,
not a reader bug.

**`cannon_jam`** (`player_airplane`), `heat_safe_limit 1000`, `heat_dissipation_rate 50`,
`jam_chance 0.1`; reads as a gun-overheating model paired with `FIRING_HEAT` in
[weapons.md](weapons.md). ⚠ **Dead data: the original executable has no reader for it**
(decoded). None of `cannon_jam`, `heat_safe_limit`, `heat_dissipation_rate` or
`jam_chance` exists as a string in `crimson.exe`, and the zrdr readers look keys up by string
(`FUN_0057a090(dict, "KEY")`), so no lookup is possible. Sibling keys `bullethole_anims`
(`0x00627ec4`) and `destroyable_parts` (`0x00627d7c`) are present, which is the calibration
that makes the absence meaningful. Not implemented here either, and reproducing it would be
invention rather than restoration.

**`armor` / `health`**, the AI two-pool damage model (fighters `64/64`…`100/100`, always
equal; `patrolboat`/`t_truck` `0/40`, unarmoured soft targets). Carried by the 12 base aircraft
defs plus the boat and truck, 15 in all. `PlaneStats` does not read either. Armour is spent
before health, the same ordering as the per-part pools.

⚠ **The whole-vehicle pair and the per-part pools are not alternatives, 11 defs resolve both.**
An `r*` AI variant chains to its base def (`rbloodhawk → bloodhawk → basic_airplane`), so it
inherits `armor 64` *and* carries its own 4×20/20 `destroyable_parts`. No **player** def resolves
a whole-vehicle pair at all (`pbloodhawk → player_airplane → basic_airplane` carries none in the
chain), so for player planes the per-part pools are the whole model.

⚠ **Those 11 both-resolvers are the `r*` remote-player family, and no roster spawns one**, so no
AI aircraft in the shipped campaign resolves both. Every def named by the 414 `aiv` blocks is a
bare AI def or a militia variant of one, and none of those chains authors `destroyable_parts`: an
AI aircraft is **zone-less**, carrying its authored pair alone. Census and consequences in
[`org/vehicleDamage.md`](../org/vehicleDamage.md).

**A vehicle spends both, in a fixed relationship: the per-part pools are the ledger and the
whole-vehicle pair is a running summary of them.** Decoded from the executable, full
write-up in [`org/vehicleDamage.md`](../org/vehicleDamage.md). A weapon hit carries two damage
numbers (armour and health, not one figure) and, sometimes, a zone id. When it names a zone, the
damage is spent against that zone's pools, armour first with 1:1 overflow into health, and the
whole-vehicle current values are then recomputed as the parts' fraction of their own maxima times
the whole-vehicle maxima. When it names no zone, it is spent against the whole-vehicle pools
directly, through the same armour-first helper. So the `armor 64 / health 64` on an AI Bloodhawk is
not a second, competing pool; it is the scale its four 20/20 zones are expressed in.

Everything downstream reads the summary rather than the parts. **Death is one test: whole-vehicle
health at or below zero.** (the take-hit wrapper loops the unabsorbed
leftover back into the whole pair zone-less, and a dead zone redirects to a surviving one, so the
kill can arrive with zones still healthy, every zone exhausted is sufficient, not necessary;
[`org/vehicleDamage.md`](../org/vehicleDamage.md)'s correction section has the full contract.) The
def-level `injure_anims` stage off the same fraction (see below), as do the AI's damage reactions
and the pilot radio lines. The one shipped datum that would invert this relationship, an `aiv`
block's four per-zone `(armor, health)` pairs, would set the zones directly and then re-derive the
whole-vehicle pair as their plain **sum** rather than a fraction, making the zones authoritative;
all 414 shipped blocks leave those eight slots at `-1`, so that path never runs.

Two spawn-time modifiers scale the authored numbers. An enemy vehicle (one whose team differs from
the player's) has both maxima multiplied by a difficulty factor of **0.75, 1.0 or 1.25** on the
campaign's Normal / Hard / Hardest ([`org/vehicleDamage.md`](../org/vehicleDamage.md) has the
branch: `1 + k * 0.125` with `k` of -2, 0 or +2). On top of
that, an **aircraft or autogyro that is not the player** draws a fresh uniform **±5 %** on both
maxima (and on nine other def-derived numbers) every time it spawns, which is why two AI planes of
the same type are never quite identical. Ships and ground vehicles are excluded from the jitter, so
a patrol boat is exactly its authored 40 times the difficulty factor: 30, 40 or 50.

**The patrol boat has two sets of hit points because it is authored as two things, and both are
live** (settled; the decode is [`org/vehicleDamage.md`](../org/vehicleDamage.md)). A boat
spawned from an `aiv` roster is a **vehicle** and reads the 40 on this page, with the 0.60/0.30
`injure_anims` above it; a boat *placed* in the world is a **destructible** and reads the `HEALTH 20`
of the anim def whose wildcard `NAME` catches it, with that def's own `ANIM_HEALTH` stages. C1 ships
both: three placed `ptboat1`–`3` at the refinery, and 12 roster boats in M05. Which model applies is
decided by how the instance was created and by nothing else; the executable does not know a boat
from a water tower. A third def, the `patrolboat` in `zrdr/patrol_boat_destroy.zrd`, is compiled
onto an unparented prototype node and is neither: it is the vehicle's **death animation**, and its
own `HEALTH` is never read. Same shape for `t_truck`.

**`turrets`**, on 16 defs: the five player turret airframes (`pavenger`, `pbalmoral`,
`pbrigand`, `pfirebrand`, `pkestrel`, both viewpoints), their six AI variants and five `r*`
remote-player variants (`thirdp` only). A viewpoint-keyed list (`firstp`/`thirdp`) of
`[title <MSG_TUR_*>, node <turretNode>]` entries; the Balmoral is the only two-turret plane
(`balmoral_turret0`–`3`). A turret entry carries a title and a node and nothing else, the
gunner's whole behaviour, arcs included, lives in the `ai.zrd` row the title names
([turrets.md](turrets.md)). **Consumed since M4 C9a**: `PlaneStats` parses the block and
`TurretController` drives the `thirdp` rig as a live gunner.

**`gun_pitch` / `gun_yaw` are the AI's forward-gun traverse limits, not a turret arc.** Both keys
appear exactly 12 times, always together, always `[-11, 11]` (degrees), and always on an AI
airframe def, a census settles which:

- **7 of the 12 carriers have no turret at all** (`bswingman`, `bloodhawk`, `fury`, `autogyro`,
  `devastator`, `peacemaker`, `warhawk`), so presence cannot be tracking turrets.
- **60 of the 63 non-player defs resolve the cone** through `kind_of`; the 3 that do not are
  `basic_airplane` (the abstract root) and the two surface vehicles `patrolboat` / `t_truck`.
  So: every AI *aircraft*, turret or not.
- **0 of the 12 player defs carry or inherit it, including all five turret airframes**
  (`pavenger`, `pbalmoral`, `pbrigand`, `pfirebrand`, `pkestrel`). A turret arc would have to be
  on the plane that mounts the turret; this is on the plane that has an AI pilot.

±11° is how far the AI's gun mount may be brought off the nose. It bounds the aim rather than
vetoing the shot: the engine clamps the lead into the band and gates on the residual the clamp
leaves, so the angle an AI will actually fire across is wider than the band
([`../org/aiPilot/aiWeapons.md`](../org/aiPilot/aiWeapons.md)). The design's gunnery model backs
the reading, an NPC's Dead Eye statistic sets the radius of a lead sphere it will shoot into.

**`bullethole_anims`**, per player plane, the ON_CALL cockpit-glass hit-decal anims
`bullet1`…`bullet5` (see [anim-definitions.md](anim-definitions.md)). The engine holds them as a
vector of `{anim handle, used}` pairs at `def + 0x1b8` and opens at most one per
`warning_shot_interval` that closed with a gun hit, which is also what sounds `window_hit_sg`:
[org/weaponFire.md](../org/weaponFire.md), "The incoming-fire cues". The remake runs that cadence
for the cue; the decals themselves are not drawn yet.

**`mass`** parses to the def at `+0x9c`, and the parser stores its reciprocal beside it at `+0xa0`
(`0x0047afae`), which is the form every consumer reads. Three defs author it: `basic_airplane` at
`0.6`, inherited by every aircraft, and the two surface vehicles at `40.5`. Both readers are
ground-vehicle code that scales a push by the pushed vehicle's mass, the blast knockback decoded in
[`../org/ordnanceTypes.md`](../org/ordnanceTypes.md) and the vehicle-to-vehicle collision transfer
`FUN_004872d0`, so the key changes nothing an aeroplane does.

**`mode`**, the dynamics class, and the one key that decides which AI behaviour an aircraft flies.
Parsed from a string (`FUN_00479240`, `0x0047afe8`): `jet` 0, `heli` 1, `tank` 2, `ship` 3,
`wingman` 4, `plane` 5. Only four defs author it and the rest inherit through `kind_of`:
`basic_airplane` is `jet` (so are all 11 player defs, all 11 base AI aircraft and all 39 militia
variants, including `autogyro`), `patrolboat` and `t_truck` are `ship`, and 12 defs are `wingman`
(`wingman`, `bswingman`, and the eleven Instant Action `w<plane>` defs). Nothing ships `heli`,
`tank` or `plane`. A `wingman` is a full aeroplane on the same integrator as a `jet`; what differs
is that **a `jet` flies a patrol net and a netless `wingman` flies a formation station on its
`primary_target`**. A `wingman` that is given a net is demoted to `jet` at spawn.
[`org/aiPilot.md`](../org/aiPilot.md) has the mechanism, the station offsets and the constants.
`mode_alt` parses to the def struct alongside it (`0.0` on `basic_airplane`) and no consumer of it
was found.

**`preferred_engagement_altitude`** (300.0 on `basic_airplane`, inherited by every aircraft) is the
fallback for the roster's `pref_engage_alt` slot. ⚠ It is a **weight on the evasive-maneuver draw**,
not an altitude order: nothing steers toward it ([`org/aiPilot.md`](../org/aiPilot.md)).

**AI-combatant tuning** (AI variant defs, M4): pilot skill/personality (`dare_devil`,
`dead_eye`, `quick_draw`, `steady_hand`, `sixth_sense`, `natural_touch`, `stun_recovery`,
`talker`, `constitution`, `accentID`), the same nine-stat vector the mission rosters author
per pilot, decoded in [ai-rosters.md](ai-rosters.md#the-skill-vector), so a def value here is the
airframe-level default a roster entry overrides; flight/behaviour (`mode`/`mode_alt`, `target_bias`,
`struct_bias`, `pursuit_range`, `attack`/`attack_dwell`/`not_pursuit_dwell`, `rates`,
`turns`, `*_damping`, `mass`, `friction`, `chas_*`, `ai_input_*` / `ai_emerg_input_*` limits
and scales, `preferred_engagement_altitude`/`return_range`, `activation` = spawn/aggro
range). The boat and truck add surface-vehicle motion keys (`platform`, `collision_d`,
`a_damping`). Paint keys (`paint_pattern`, `paint_colorN`, `paint_decalN`) set the AI
liveries, see [paint.md](paint.md). A few airframe oddballs round out the set: `fuel`,
`is_autogyro`, `rudder_tol`, `pilot`, `flight_ceiling`, `title`.

**`fuel`** is a full tank, in the burn units the player's lever draws at five per second times the
lever. **Exactly one def authors it, `player_airplane` at 54926**, so every `player_*` airframe
inherits the same tank and no AI def has one; the compiled default is 0. The tank is refilled
wherever the aircraft is placed, only the local player's burns, and a dry one freezes the throttle
lever where it stands instead of closing it, all decoded in
[../org/flightModel.md](../org/flightModel.md) under "Part-throttle equilibrium". At a fully open
lever the authored tank lasts about three hours, so no shipped mission runs one dry.

**`ai_input_*` / `ai_emerg_input_*`** are the AI control law's per-axis output stage, decoded in
[../org/aiControlLaw.md](../org/aiControlLaw.md): the three `scale` keys multiply the law's roll,
pitch and yaw commands and the three `limit` keys clamp them, with the `emerg` set substituted
during crash recovery. ⚠ **The def struct holds them in roll/pitch/yaw order** (`+0x264`…`+0x278`)
while the roster's twelve slots are in pitch/roll/yaw order; a roster value of `-1.0` falls through
to the def, which is what every shipped roster block does. The shipped defs author only
`ai_input_limit_pitch` (11 defs, 0.79–0.91) and one `ai_input_limit_yaw` (0.79); the rest inherit
down the `kind_of` chain as one six-slot block.

**`rudder_tol`** selects between the law's two steering branches, and **a higher value means MORE
rudder**: clearing the threshold picks the bank branch, so raising it makes banking harder to
reach. **Default 0.2**, at which any target ahead is banked toward and the rudder is reserved for
targets nearly dead astern. Exactly two defs author it, `autogyro` and `balmoral`, both at `1.0`,
which is the ceiling the compared quantity can never exceed: on those two a lateral-dominant aim
error goes on the rudder even when the target is straight ahead. The `autogyro` is class 1 and
never reaches this law, so `balmoral` is the one aeroplane that turns onto a target with rudder
rather than bank.

**`target_bias`** and **`struct_bias`** are the AI acquisition's two class-dependent rank terms,
decoded in [../org/aiPilot.md](../org/aiPilot.md). The spawn path copies `def+0x138` to the
entity's `+0x340` and `def+0x13c` to `+0x344` (`FUN_00475820`), and the picker adds `+0x340` to
this entity's own rank when it is a candidate vehicle and the scorer's `+0x344` to every turret
and structure candidate. Both go in raw, in the same rank units as metres of distance, so the
ranking's `1200` weight scale does not touch them. ⚠ **Both are authored NEGATIVE and the rank is
MINIMISED, so both attract.** `target_bias` is `-300.0` on `player_pfighter` and `-100.0` on the
twelve AI aeroplane defs, unauthored on `patrolboat`, `t_truck` and the zeppelins; `struct_bias` is
`-200.0` on eleven aeroplane defs and unauthored elsewhere. The net effect is that an aeroplane
authoring both ranks an equidistant structure about 100 m ahead of an equidistant AI aeroplane.
The vehicle constructor zeroes both, so an unauthored def spends nothing, and a turret's own picker
passes a literal `0` in place of `struct_bias`. CSVM reads both into
`PlaneStats.AiTargetBias`/`AiStructBias` off the def chain the vehicle spawns as.

**Def defaults for the block above** (from the def initialiser, not from any key): scales `3.5`,
limits `1.0`, the emergency set identical, `rudder_tol` `0.2`. The AI speed clamp that sits beside
them is fixed at `0`…`111.76 m/s` (250 mph) for every airframe and has no token at all.

**`mode` is the vehicle class**, and the parser maps it to a small enum the whole object update
dispatches on: **`jet` = 0, `heli` = 1, `tank` = 2, `ship` = 3, `wingman` = 4, `plane` = 5**
(`0x47afc0`–`0x47b081`; classes 0 and 4 fly the aeroplane path, 1 the autogyro path, 2 the ground
path, 3/5 the ship path). Only `basic_airplane` (`jet`), `patrolboat`/`t_truck` (`ship`) and the
eleven `w*`/`wingman`/`bswingman` defs (`wingman`) author it; everything else inherits `jet`.
The class is what gates the per-spawn ±5 % jitter of eleven runtime slots, `fd_speed`,
`ThrustFactor`, `drag_factor`, `pitch_torque`, `roll_torque`, the two `rates` and two `turns`
values, and the whole-vehicle armour/health maxima, which runs on classes 0 and 1 only, for
vehicles not named `player`, outside a network game (docs/org/flightModel.md, "The per-spawn
jitter"; implemented C26). `rates` and `turns` are the surface-driving integrator's acceleration
and steering rates with their clamps: `basic_airplane` authors them (10/42 and 4.6/6.5) and every
aircraft therefore carries them, but the aeroplane arm never reads them.
## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
