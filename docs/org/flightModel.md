# The original flight model, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, 2 580 480 bytes, `language x86:LE:32:default`), 2026-08-09, extended 2026-08-14 with
ground blow, the collision impulse, and the live-versus-debug integrator correction. **This is the
authority for every flight quantity.** It superseded a set of figures reconstructed from cockpit-gauge
video, and that whole route is retired: footage cannot confirm a decode, it only ranks readings
(`docs/verification.md` DET-12). The reconstruction and the scripts behind it were deleted and are
recoverable with `git log -p`; treat any number sourced from them as withdrawn.

Everything below is a description of *behaviour and constants*. No decompiler output is
reproduced; the function addresses are given so any claim can be re-checked at source.

## Provenance

The build still carries the original source path `D:\zipper\gamez\zgamegen\gg_flight.c`, and,
more usefully, a **developer "Dynamics tuner" dialog that shipped in the retail exe**. It writes
a `dynamics.txt` whose header names the entire model:

```
PitchTorque, YawTorque, RollTorque, RecMomentsInertiaX, RecMomentsInertiaY, RecMomentsInertiaZ,
ReturnRate, DampingRate, FakeDynSpeed, ThrustFactor, DragFactor, Weight, RefArea,
TopSpeed, StallSpeed, AccelTime99, AccelTime75, AccelTime50, AccelTime25,
DecelTime25, DecelTime50, DecelTime75, DecelTime99,
Climb90, Climb45, Dive45, Dive90, Turn100, Turn75, Turn50, Turn25, Turn0
```

The first 13 are the authored `dynamics` block ([vehicle.md](../formats/vehicle.md)). The
remaining 18 are **measured by running the real flight model**, not authored, which is why they
never appear in the data files.

| Address | Role |
|---|---|
| `FUN_004897c0` | World tick: advances the clock, iterates the vehicle list |
| `FUN_00489ea0` | Vehicle-class dispatch on `obj+0x67c`; the flyable aeroplane classes are 0 and 4 |
| `FUN_0041c270` | The AI brain, a sibling of the dispatch in the same tick. Writes the stick; see [aiControlLaw.md](aiControlLaw.md) |
| `FUN_0048e580` | Per-tick integrator |
| `FUN_0048c470` | Torque accumulation, control limiters, stall flag |
| `FUN_0048bdd0` | Control authority vs speed |
| `FUN_0048c220` | Ground blow, the proximity control bias |
| `FUN_0048bf60` | Ground-blow probe: the nose ray, its falloff and its axis |
| `FUN_0048d7f0` | Collision sweep and the `bounce_factor` restitution |
| `FUN_0048fc40` | Force accumulation: thrust, drag, lift, gravity |
| `FUN_0041abd0` | Lift coefficient |
| `FUN_0041ada0` | Drag coefficient (drag polar) |
| `FUN_0041acf0` | Thrust available |
| `FUN_0041aca0` / `FUN_0041ac80` | Atmosphere / dynamic pressure |
| `FUN_004c8ec0` / `FUN_004c8f70` | World segment query: terrain grid walk plus per-cell object test |
| `FUN_005388d0` / `FUN_00538880` | Two-point distance / squared distance |
| `FUN_004735b0` | `player.json` global parser (token → constant, with fallbacks) |
| `FUN_00479240` | Per-plane `dynamics` block parser |

### ⚠ There are two integrators, and this table named the wrong one until 2026-08-14

There is **one** flight model. What exists twice is the integrator, and the second copy is the
offline measurement harness described under "The measurement harness" below. Four of this table's
addresses used to name that copy:

| Debug copy | Live counterpart | What the copy drops |
|---|---|---|
| `FUN_00491820` | `FUN_0048e580` | collision (`FUN_0048d7f0`), impact (`FUN_0048d2c0`), per-part update, view smoothing |
| `FUN_00490f70` | `FUN_0048c470` | **ground blow (`FUN_0048c220`)** and seven other world-facing calls |
| `FUN_00490e10` | `FUN_0048bdd0` | the fifth output, a reverse-authority factor (below) |
| `FUN_00445090` | none | the Dynamics tuner dialog itself, which has **no callers at all** |

`FUN_0048fc40` is genuinely shared, called from `FUN_0048c470` (live) and `FUN_00490f70` (debug),
which is the whole of the connection between the two families. The debug copy reads the same
globals and computes the same curves, so **every aerodynamic constant and formula in this document
stands**; what the copy cannot show is anything it strips. Ground blow and the collision impulse
were invisible from that branch, which is why the 2026-08-09 pass found neither.

The live chain for one frame of the player's aeroplane, each call site verified:

```
FUN_004897c0  clock += dt, iterate the vehicle list
 └ FUN_00489ea0            @0x00489bb6   dispatch; obj+0x67c == 0 for a player plane
    └ FUN_0048e580         @0x00489ef3   integrator
       ├ FUN_0048c470      @0x0048e6da   aero force + control torque
       │  ├ FUN_0048bdd0                 airspeed authority ramp
       │  ├ FUN_0048fc40                 thrust, drag, lift, weight
       │  └ FUN_0048c220   @0x0048cf95   GROUND BLOW
       ├ (orientation write)             @0x0048e880-0x0048e8ae
       ├ FUN_0048d7f0      @0x0048ea17   COLLISION SWEEP + bounce_factor
       └ (position write)                @0x0048ea81
```

The other dispatch classes are not aeroplanes: class 1 (`FUN_0048ffe0`) resolves collision but runs
no aerodynamics and reads no controls, and classes 2, 3 and 5 (`FUN_0048a880`, `FUN_0048b480`) call
neither, being animated movers. The token that names each class in the data files was not found.

⚠ **The rest of this document still cites the debug copy's addresses for the shared physics** (the
integrator ordering, the weathervane at `0x4916fe`-`0x4917f0`, the throttle slew, the limiters).
Those citations are still re-checkable at those addresses and the behaviour they describe is the
behaviour the game runs, because the two copies are the same code over the same globals. They were
left alone deliberately: mapping each one to its live-copy address is a separate mechanical pass,
and inventing the mapping without tracing it would be worse than the inconsistency. When reading an
address out of this document, check which family it belongs to first. **A1 (2026-08-15) corrected
the first of them:** the Atmosphere section's "the player force path zeroes the altitude" was a
debug-copy citation, and with it went the supposed player-versus-AI density divergence. **C22
(2026-08-15) mapped three more,** the ones it had to port: the player/AI guard on the airflow blend
(`0x48c520`), on the weathervane (`0x48cd3e`, the live twin of `0x4916fe`) and on the forward-speed
floor (`0x48e925`, in `FUN_0048e580`). The weathervane's is the only one this document had ever
quoted, and it quoted the debug copy's.

## Units and conventions

- Internal state is **metres, seconds, radians**. Display converts with `× 2.2369363` (m/s → mph).
- **Speeds in the data files are authored in MPH**, every speed token is multiplied by `0.44704`
  as it is parsed. Angle tokens are authored in **degrees** (`× 0.01745329251994`).
- Aerodynamic intermediates are computed in **imperial** units: altitude in feet
  (`× 3.28084`), velocity in ft/s, density in slug/ft³, dynamic pressure in lb/ft².
- Forces are carried in weight units. Acceleration is `force × 9.82 / Weight` (`0x491290`), with
  `Weight` the **raw authored `veh_weight`**, no unit conversion anywhere in the weight chain; see
  "The force scale, settled" below.
- Body axes: local **X = pitch axis**, **Y = yaw axis**, **Z = roll axis**; the nose points
  along **−Z**. (Consistent with the `collision` probe points in
  [vehicle.md](../formats/vehicle.md), which put the nose at −Z.)

### Gravity is exactly `nom_gravity`

Gravity is applied as a force of `(nom_gravity / 9.82) × Weight`, and the force-to-acceleration
step multiplies by `9.82 / Weight`. The two cancel, so the aircraft falls at **precisely
`nom_gravity`**. The parser's own fallback for `nom_gravity` is `9.82`; this install's
`player.json` sets **20 m/s²**. The arcade 2 g is therefore authored data, not a constant, and it
is the only gravity in the model.

## The integrator

Semi-implicit Euler, in this order each tick (`FUN_00491820`):

1. Compute linear acceleration, angular acceleration and the stall flag (`FUN_00490f70`).
2. Body angular velocity += angular acceleration.
3. Damp angular velocity, an **exponential decay** of `dt × ang_momentum_damp`, not a linear
   subtraction.
4. Transform body rates to world through the orientation matrix, scaling each axis by its
   `rec_moments_inertia` component (the reciprocal inertia, x = pitch, y = yaw, z = roll).
5. Rotate the orientation by `ω · dt`.
6. Velocity += acceleration `· dt`.
7. Position += velocity `· dt`.

The measurement harness pins `dt` to **0.01 s** (100 Hz) while it runs, so all the derived
figures in `dynamics.txt` are 100 Hz results.

⚠ **AI aircraft have a forward-speed floor.** For any aircraft that is not the player, the
velocity component along the nose axis is clamped to at least **4.4704 m/s (10 mph)** after
integration. The player is exempt.

**C22 (2026-08-15) read the LIVE site and ported it.** It is in `FUN_0048e580`, not in the debug
integrator: `cmp edi, [0x71c298]` at `0x48e925` skips the block for the player, and the block itself
runs `0x48e95e`–`0x48e998`. It compares the velocity's **`m[2]` component against the negated
constant at `0x608128` (`−4.4704`)** and, when that component is greater, adds
`(−4.4704 − v·m[2]) · m[2]`. `m[2]` is `−nose`, so both signs flip and the effect is "raise `v·nose`
to 4.4704 by adding along the nose". Three properties follow from the arithmetic and are easy to get
wrong: it is **one-sided** (it never slows anything), it **adds along one axis** rather than
rescaling, so the perpendicular components survive untouched, and it is **not a floor on speed**,
it sits before the velocity is stored to `obj+0x924` and `|v|` recomputed into `obj+0x934`, so the
speed that comes out is the length of the floored vector. A plane descending at 20 m/s with its nose
on the horizon has four times the floor in SPEED and none of it along the nose, and the original
pushes it forward.

**C24 landing note, the exponential form, implemented, and where it sits.** `FlightModel.Step`
now matches steps 2–3 exactly: `BodyRates += cmd · dt` (the stick, the bank coupling and the
weathervane, all three already summed into `cmd` before this line, C21/C22/C23), **then**
`BodyRates *= exp(−dt · ang_momentum_damp)` on the whole result, THIS TICK'S torque included, not
the explicit-Euler `(cmd − BodyRates·damp)·dt`, which only ever damped the rate carried over from
the previous frame and left each tick's own torque undamped until the next one. The two forms are
the same discretization to first order in `dt` per step (`exp(−x) = 1 − x + O(x²)`), but their
STEADY-STATE fixed points differ by more than that: explicit Euler's fixed point is `cmd/damp`
exactly, independent of `dt`; the exponential-form fixed point is `cmd·dt·k/(1−k)` with
`k = exp(−dt·damp)`, which is `cmd/damp · x/(eˣ−1)` for `x = dt·damp`, smaller by `≈ x/2` at
small `x`. At the remake's own physics tick (`dt = 1/60 s`) and the Bloodhawk's
`ang_momentum_damp = 5`, `x ≈ 0.083` and the stored half-angle rate is `≈4.1 %` below the
continuous fixed point. The timings formerly quoted here treated that half-angle state as physical
angular velocity and were invalid; the quaternion exponential below doubles it when attitude is
built. A large-`dt` case (`dt·damp = 5` on a released axis) shows the
qualitative point the item is about: the exponential form stays in `(0, 1)`, strictly decaying,
where the explicit-Euler factor `(1 − dt·damp) = −4` would flip the rate's sign and grow it every
tick, `CSVM.Tests/AngularDampingTests.cs` pins both the ordering (this tick's own torque is
damped, not exempted) and this divergence.

## A destroyed hull flies the same model

⚠ **Nothing in the flight path is gated on death.** `FUN_0048e580` (the aircraft integrator),
`FUN_0048c470` (the force and torque build) and `FUN_0048fc40` (thrust, drag, lift, gravity) do not
read `+0x91d` or `+0x91f` at any instruction: a program-wide scan finds 100+ sites reading `+0x91d`
and 39 reading `+0x91f`, and none of them lies inside those three functions. The only death test on
the movement path is the run-or-skip gate itself, `FUN_00489ea0` at `0x00489ea3` / `0x00489ead`, and
a vehicle that passes it dispatches the unmodified integrator for mode class 0 and 4. No lift term
is dropped, no drag term is added, no coefficient is swapped, and no ballistic path exists. Neither
does the death function `FUN_004b82d0` zero the throttle command or the control-surface
deflections; those are AI-written state (`FUN_0041b560`, `FUN_004209b0` slew `+0x124`) and the AI
think is what stops, so they **freeze at their last commanded values**.

Two state flags do change the force build, and death sets neither:

| Flag | Tested at | What it does | Set by |
|---|---|---|---|
| `+0x384` | `0x0048c4ba` | swaps the whole aerodynamic build for a velocity-match to `fd_speed · throttle` along the nose, the same arm any non-player over 1000 units from the player takes | the `-fd` switch and the console's `fd` / `ifon`, nothing else (see "`+0x384` is a developer switch") |
| `+0x2dc` bit `0x2` | `0x0048fdd0` | engine out: **thrust alone** goes to zero (`local_c` at `LAB_0048fdf3`); lift and drag are untouched | `FUN_004b1690` (the systems-damage setter), `FUN_004aff80` and `FUN_004b40c0` at spawn/reset |

So "a dead engine means no lift and high drag" is refuted twice over: there is no engine-out state
on the death path, and the real engine-out bit only removes thrust. `FUN_0048ad20` is a red
herring, confirmed: it is the terrain-conform update for surface vehicles, reached from
`FUN_00489ea0`'s class 2/3/5 arms and never for an aeroplane.

**What falls, then, is the anim.** The dead hull holds altitude and travels, for the three seconds
until `Callback 15` releases it; `randomdestseq`'s `ObjectMotion` (gravity −9.8, `impact_force`) is
what flies it down (`docs/org/vehicleDamage.md`, "A dead aircraft keeps flying itself until
`Callback 15`").

**The commands freeze; they are not neutralised.** What death stops is the AI think and the weapon
loop, so nothing writes the command vector and the integrator keeps reading its last value.
`FlightController.StepWreckFall` steps the model with `_lastInput` for that reason. A default
`FlightInput` there would fly the wreck on zero throttle and neutral surfaces, which is an
invention: the original has no neutralising step on the death path.

⚠ **Do not tune this against the recordings' downrange.** Freezing measures 323 m downrange and
−8 m of altitude on a headless kill, where neutralising measured 175 m and +1 m, and 175 m is the
figure a reference recording gave. That agreement
is not evidence for neutralising. It is a footage-derived distance, the class of measurement that
has failed here repeatedly and may not contest a decode, and the magnitude under freezing is a
function of **our** AI's last throttle rather than the original's, so neither number tests the
mechanism. If the downrange reads wrong at the controls, the open question is what throttle an AI
carries into its death, which is a decode of the AI's own throttle command and not whether to
reinstate a neutraliser the original never had.

⚠ **Decoded for an AI, assumed for a human.** What `FUN_004897c0` skips on death is the AI think
(`FUN_0041f810`/`FUN_0041c270`) and the weapon loop, so an AI's commands demonstrably stop being
written. Whether the original also stops reading a HUMAN's stick on death is not decoded, and
`StepWreckFall` serves both, so a dead player's hull flies the last stick position its pilot held.
The stakes are lower than they look, since `player-player` breaks the hull into four separately
flown pieces rather than falling as one, but a player killed holding full deflection is the case to
watch.

## Atmosphere

`FUN_0041aca0` is a **two-band step function, there is no altitude gradient at all**:

| Band | Density factor | ρ (slug/ft³) | Speed of sound |
|---|---|---|---|
| dense | 0.9544815 | 0.002377 × 0.9544815 = **2.2688e-3** | (0.9884208 + 1) × 558 = **1109.5 ft/s** (338.2 m/s) |
| thin | 0.057048105 | 0.002377 × 0.057048105 = **1.3560e-4** | (0.7348 + 1) × 558 = **968.0 ft/s** (295.1 m/s) |

Then:

```
q     = 0.5 · ρ · V_ft/s²          (dynamic pressure, lb/ft²)
Mach  = V_m/s / (a_ft/s · 0.3048)
```

⚠ **The band's factor `k` has a third consumer, not two.** The speed of sound is `(k + 1) · 558`,
and the thrust curve's `pow` base is `k · 1.33` (`0x41ad0f`), so a port that switches ρ and the
speed of sound on the band but pins the base to the dense `k` is half a band switch. See
"Thrust available".

**The dense band is the operative one for the whole flyable envelope, and the live process says so
directly.** The comparison is `alt_ft ≤ threshold → dense` (`0x41aca4`–`0x41acc4`) against the
threshold at `0x0071bb3c`, and a running retail process holds **6561.6796875** there, which is
2000 m converted at 3.28084 ft/m. Every altitude below 2000 m takes the dense band, so the thin
band is the regime above that ceiling rather than the regime above sea level. The arithmetic
agrees and always has: the dense band produces a correct ~76 mph stall from the code's own
fallback aircraft where the thin band gives a nonsensical several-hundred-mph figure, and the
level-equilibrium solve reproduces nine of eleven airframes' authored `fd_speed` under the dense
band while the thin band misses by ~4× (see "Drag" and the thrust curve's note at `0x71bb3c`).
The selection is also observed airborne: a passive 10 Hz sample through menu, mission load and
flight (3655 vehicle samples, 854–6936 ft) reads the threshold constant throughout, the dense
outputs on every sample at or below the line, the thin outputs (968.0 ft/s, 1.356e-4, 0.7348) on
every sample above it, and 130 clean transitions crossing the boundary in both directions; the
few stragglers sit within 1.3 ft of the threshold, the skew between the altitude and atmosphere
reads. `CSVM.Tests/AtmosphereBandTests.cs` reproduces the step function, its 2000 m boundary and
the discriminating stall case.

**A1 (2026-08-15): there is no player-versus-AI density divergence, and the earlier claim that the
player path pins the dense band was a debug-copy citation.** This paragraph replaces a ⚠ that read
"AI aerodynamics above sea level run on the thin band".

- The save / store-`0` / restore of `[obj+0x208]` at `0x491250`–`0x491284` is inside
  `FUN_00490f70`, which is the **debug copy** of the force function (see "There are two
  integrators"). Its only caller is `FUN_00491820` (`0x49184c`), whose only callers are
  `FUN_00492040`, `FUN_00491c60` and `FUN_00491d90`; the latter two are called only by
  `FUN_00492040`, and `FUN_00492040` returns immediately unless `DAT_0071c78a` is set. That is the
  Dynamics-tuner display flag, written only by the two dialog handlers `FUN_00443750` and
  `FUN_00445090`, and `0` in the shipped image. The zeroing is the measurement harness pinning
  itself to sea level for its report. **It never runs during flight, for the player or anyone
  else.**
- The live force path is shared: `FUN_004897c0` → `FUN_00489ea0` → `FUN_0048e580` →
  `FUN_0048c470` → `FUN_0048fc40` (`0x48c883`) → `FUN_0041aca0` (`0x48fc70`), and
  `FUN_0048fc40` passes `[obj+0x208] · 3.28084` (`0x48fc51`) with no zeroing anywhere in
  `FUN_0048c470` or `FUN_0048e580` (a program-wide instruction sweep for `+0x208` accesses finds
  none in either function). The player reaches this path exactly as an AI aircraft does
  (`FUN_004897c0` dispatches the player object through `FUN_00489ea0` like every other vehicle),
  and the class dispatch is on the vehicle class `obj+0x67c`, never on the player pointer. So
  whatever band the atmosphere selects, the player and the AI select the same one.
- `[obj+0x208]` is world altitude in metres, positive up: gravity is subtracted from world
  component 1 (`FUN_0048fc40`), the respawn writes `900.0` there or the terrain height when that is
  higher (`FUN_004969b0`, `0x496b75`), and `FUN_004704b0` pushes the object to terrain + 1 m when it
  is below the terrain.

**Why an address xref sweep cannot see the threshold's writer.** Cross-references to `0x0071bb3c`
are **one**, the read at `0x41aca4` (`FCOMP`), and a byte search for the little-endian address
`3c bb 71 00` across the whole image returns exactly one hit, `0x41aca6`, the displacement inside
that same instruction. The slot is written through a base register instead: `FUN_00463640` is a
`__thiscall` reset on the global object at `0x0071bb30` and stores the threshold at `0x46368b` as
`MOV dword ptr [EDI + 0xc], 0x45cd0d70`, the float bits of 6561.6796875. The same routine resets
`[obj]`, `[obj+4]`, `[obj+8]` and `[obj+9]`, and the live bytes at `0x71bb30` match that layout.
The address never appears as an operand, so no sweep keyed on the address can find the store.

The slot is also not a shipped initialiser. RVA `0x31bb3c` falls past the raw data of `.data`
(virtual size `0x4051d4`, raw size `0x02a000`), so it is zero-filled at load and a listing that
reports `0.0` is reporting the zero fill of a BSS variable. The band question is settled by
reading the live process, which is what the value above records; a static pass over the image
cannot settle it in either direction.

### ⚠ The literal reading puts every airborne aircraft on the thin band — RETIRED (2026-08-24)

This read the uninitialised `.data` slot at `0x0071bb3c` as a shipped `0.0` and recorded an open
conflict between that reading and the stall/equilibrium arithmetic. A live read of the retail
process disproves the premise: the slot holds 6561.6796875 ft, so the dense band covers everything
below 2000 m and there is no conflict left to record.

## Lift, the wings deliver the demanded G

This is the single most important finding, and it is not recoverable from video. The model is
written **backwards from the acceleration it wants**, not forwards from a force balance.

### Step 1, the relative wind is faked toward the nose (`liftAOAs`)

Before any force is computed, the model picks what it will treat as the oncoming airflow, keyed on
`cos α` where α is the angle between the nose and the velocity vector:

| Condition | Relative wind used |
|---|---|
| α ≤ `liftAOAs[0]` | the **true velocity vector** |
| α ≥ `liftAOAs[1]` | **speed × nose direction**, i.e. the airflow is pretended to come straight down the nose |
| between | linear blend on `cos α` between the two |

`liftAOAs` is authored in **degrees** (the parser takes its cosine; fallbacks 0.98 / 0.96 ≈
11.5° / 16.3°). So past the upper edge the game **discards the real airflow direction entirely**,
a large arcade assist that makes a hard-manoeuvring aircraft behave as though it has no sideslip
or incidence at all.

⚠ **This blend is player-only.** AI aircraft skip it and *always* use the nose-aligned wind,
AI effectively flies permanently at zero incidence.

**C22 (2026-08-15) located the LIVE guard and ported it.** In `FUN_0048c470`, `cmp esi, ecx` at
`0x48c520` (the player object loaded into `ecx` at `0x48c502`) jumps to `0x48c6e9` for anything that
is not the player, and `0x48c6e9`–`0x48c70a` builds `−speed · m[2]`, `m[2]` being `−nose`, that is
`speed · nose`, with no reference to the window constants `_DAT_0071c430`/`_DAT_0071c434` the
player branch reads. So it really is a skip of the whole block, not a saturated blend.
⚠ **The sign of the resulting difference is not fixed, and "the AI pulls harder" is wrong.** The
demand is `lift_accel_rate · (relativeWind − velocity)` **plus weight**, so at a climbing flight path
the fully-nose-aligned swing points down and partially cancels the weight term: measured on the
Bloodhawk at α = 8° with the path above the nose, the AI's demanded load factor is **0.26 G against
the player's 2.04 G**. Past `liftAOAs[1]` the two agree exactly, because there the player is
nose-aligned as well.

### Step 2, the demanded acceleration

```
demand = lift_accel_rate · (relativeWind − velocity)      [lift_accel_rate is a rate, 1/s]
demand.Y += nom_gravity                                   [world up — so lift also carries weight]
n = |demand projected onto the body X/Y plane| / 9.82      [the demanded load factor, in G]
```

`lift_accel_rate` (fallback **1.2**) is the rate at which the velocity vector is swung onto the
nose. Since the airflow difference is `≈ speed · α`, the load factor works out as
`n ≈ V · α · lift_accel_rate / g`, a centripetal acceleration expressed in G, which is
dimensionally exactly right.

### Step 3, lift is that demand, clamped

`FUN_0041abd0`:

```
n     = clamp(n, −5, +9)                                  [a −5 G / +9 G limit]
C_L   = clamp( n · Weight / (q · RefArea), −1.8, +1.8 )
C_L   = min( C_L, 0.75 − 0.15 · Mach )                    [the aerodynamic ceiling]

Lift force  = C_L · q · RefArea   (in the demand's direction, in the body X/Y plane)
```

Below the ceiling the middle two lines cancel, and the whole model collapses to:

> **Lift force = clamp(demanded G, −5, +9) × Weight**, delivered along the demanded direction,
> capped by `(0.75 − 0.15 · Mach) × q × RefArea`.

The self-consistency check: gravity is applied as a force of `(nom_gravity / 9.82) × Weight`, and
in straight level flight at α = 0 the demand is exactly `nom_gravity`, giving a lift force of
`(nom_gravity / 9.82) × Weight`. The two cancel **exactly**. Level flight is not a tuned
equilibrium, it is an identity in the algebra.

Consequences for a reimplementation:

- **The demanded G is the control variable.** The wings simply deliver it, up to a hard 9 G and
  the aerodynamic ceiling.
- `RefArea` and the density constant do **not** set the lift slope, they only set the *ceiling*,
  i.e. where the aircraft stalls. Lift below the ceiling is independent of both.
- The `±1.8` clamp on `C_L` binds only when `q · RefArea` is small relative to weight, where the
  compressibility ceiling is already lower. In practice `0.75 − 0.15·Mach` is the operative limit.
- **`n` is a magnitude, so the negative halves of both clamps are dead.** The callers hand this
  function the length of the demand vector and apply the resulting force along that vector's own
  normalised direction, so neither `−5` nor `−1.8` can be reached and the one-sided `min` against
  the compressibility ceiling governs a pushover exactly as it governs a pull.
- The delivered `C_L` is passed to the drag routine and **never read there** (see Drag). This model
  has no induced drag at all: a pull costs speed only through the lift vector's own tilt.

## Drag, the polar is in MACH, not `C_L`

`FUN_0041ada0` is 33 bytes, no branches, and has exactly **one** call site in the whole `.text`
(`0x48fd76`):

```
0041ada0  fld   dword [esp+4]      ; x  <-- arg1
0041ada4  fmul  dword [0x6032e0]   ; * 0.5
0041adaa  fadd  dword [0x6034b0]   ; + 0.8
0041adb0  fmul  dword [esp+4]      ; * x  <-- arg1 AGAIN (disp8 = 04, not 08)
0041adb4  fadd  dword [0x603490]   ; + 0.12
0041adba  fmul  dword [0x603474]   ; * 0.73
0041adc0  ret
```

```
C_D  = 0.73 · (0.12 + 0.8·M + 0.5·M²)        M = Mach
     = 0.0876 + 0.584·M + 0.365·M²

Drag = q · RefArea · DragFactor · C_D · k    (opposing the velocity vector; k = 0.8 when boosting)
```

⚠ **This corrects an earlier reading of this page that had the same three coefficients as a polar
in `C_L`.** The caller computes `C_L` at `0x48fd3f`, pushes it at `0x48fd74`, and cleans 8 bytes,
so a decompiler emits `Drag(Mach, C_L)` and the polar reading is the natural mistake. But the
pushes are `push eax` (`C_L`) then `push ecx` (Mach), and the **last** push is the lowest address,
so **Mach is `[esp+4]`** and `C_L` is the dead `[esp+8]`. Both `fld`/`fmul` in the function use
`disp8 = 0x04`. The `C_L` argument is a stale prototype, nothing more.

The difference is not cosmetic: under the `C_L` reading drag rises with commanded G and has a
speed-independent floor at fixed load factor; under the correct one drag is **independent of G**
and rises with speed only. Anything calibrated against the `C_L` version was calibrated against a
term the original does not have.

**No induced-drag term exists anywhere in `FUN_0048fc40`.** Every use of the `C_L` slot was
enumerated: the lift-force product, an optional load-factor out-param, and the dead push. There is
no `C_L²`, AOA-keyed or load-factor-keyed contribution to drag.

`DragFactor` is the per-aircraft tuning multiplier from the `dynamics` block. The parasite term
`0.0876` is fixed for every aircraft, airframes differ only through `DragFactor` and `RefArea`.

**Cross-check that settles it independently of the bytes.** With this polar, the thrust curve above
and `ThrustFactor = EnginePower` scaled by `RefArea`, the full-throttle level equilibrium
`EnginePower · T_avail(M) = q · DragFactor · C_D(M)` reproduces each airframe's **authored
`fd_speed`** with nothing fitted:

| Airframe | `fd_speed` | solved | ratio |
|---|---:|---:|---:|
| `pbloodhawk` | 302.0 mph | 300.5 | 0.995 |
| `ppeacemaker` | 290.8 | 290.0 | 0.997 |
| `pfury` | 281.9 | 281.0 | 0.997 |
| `pavenger` | 264.0 | 261.4 | 0.990 |
| `pdevastator` | 252.8 | 251.2 | 0.994 |
| `pbrigand` | 241.6 | 240.0 | 0.993 |
| `pautogyro` | 228.2 | 214.9 | 0.942 |
| `pkestrel` | 217.0 | 215.3 | 0.992 |
| `pfirebrand` | 208.0 | 206.7 | 0.994 |
| `pwarhawk` | 201.3 | 200.9 | 0.998 |
| `pbalmoral` | 176.7 | 125.9 | **0.712** |

Nine of eleven land inside 1 %. The autogyro and the Balmoral are the outliers, and they are the
two airframes that are not conventional fighters. Under the `C_L` reading the same solve is not
even close.

⚠ **The absolute scale against `CAP-05` does not close, and the force path is now verified
byte-complete, see "The force scale, settled" below.** The four zero-thrust decelerations
(0.36/1.11/2.82/3.74 m/s² at `V/fd_speed` = 0.25/0.35/0.46/0.50) come out **≈2–3.6× too strong**
against this curve. The earlier suspicion, a kg/lb mix-up of 2.2046 in the force→acceleration
divisor, is **disproven at source**: `veh_weight` runs raw end to end. The decode stands and the
residual is not a missing term: the footage figures it disagrees with are frame-derived and cannot
refute a decode (`docs/verification.md` DET-12).

## The force scale, settled (no conversion exists; the footage residual is discarded)

The B13 residual read "everything ≈2× too strong, invisible to the level-equilibrium ratio", and
the suspect was the force→acceleration divisor. **Re-read from the raw bytes, the whole weight
chain is conversion-free**, so there is no missing factor to implement:

| Address | What it shows |
|---|---|
| `0x47ae2d` | parser stores the `veh_weight` token (`0x628008`) to def `+0x130` with a plain `mov`, no FPU op, no 0.44704-style conversion (contrast `drag_fade_speed`, converted two instructions earlier) |
| `0x475c6a` | def `+0x130` → runtime `+0x674`, plain `mov` |
| `0x48ff8e` | gravity force = `nom_gravity / 9.82 × [obj+0x674]`, weight raw |
| `0x41ac13` | lift: `C_L = n · [obj+0x674] / (q·S)`, weight raw |
| `0x48fd63` | the optional delivered-load out-param divides by `[obj+0x674]` raw |
| `0x491290` | force → acceleration: `× 9.82 / [obj+0x674]`, weight raw (`9.82` at `0x6080d4`) |

The only other writers of `+0x674` are the Dynamics tuner dialog's write-backs (`0x443992`,
`0x443a5a`, `0x445146`), debug paths, not conversions. `[obj+0x934]`, the speed every aerodynamic
term keys on, is `sqrt(vel·vel)` stored by the integrator (`0x491b1b`), i.e. the true airspeed.
So the executable computes **exactly** what `FlightModel.cs` computes, constant for constant, and
the remake's force path needs no change.

**That settles the CAP-05 residual as a property of the footage, not a units bug in the model.**
The decode is authoritative here (`docs/verification.md` DET-12); the deficit is recorded because
its structure is interesting, not because a constant is owed. No constant can close it anyway:

- The deficit has clean structure: measured C_D at the four points is the polar **minus a constant
  ≈ 0.112** (deficit ∝ q; −0.1085/−0.1118/−0.1122/−0.1122 across x = 0.25…0.50). Nothing decoded
  produces a constant-C_D forward force: thrust available is flat-ish in Mach there (so residual
  lever or idle thrust is ruled out, the implied lever would have to fall 8.6% → 2% *within one
  clip*), and gravity-payback from a descent would be a constant force, not ∝ V².
- Scaling thrust+drag together (the only scale the level equilibrium tolerates) moves
  `accel-150-290` the wrong way: a ×0.5 that fixes `decel-290-150` (3.48 s → ≈7 s) blows the accel
  row out to ≈6.4 s against the same footage's 3.76 ± 0.40. The footage itself rejects every
  uniform force scale.
- The CAP-05 points are **fit outputs, not raw readings**: `FINDINGS.md` records the (g, C)
  degeneracy of the extraction and the +7° AoA of its descent leg. The one model-free anchor
  (6.24 m/s² lost at 152.6 mph in a +5° climb, engine off) still leaves the polar ≈1.5–1.8× strong
  once full `nom_gravity` is charged to the climb, smaller than the fit table's 2.05×, but real.

**Disposition (2026-08-09):** recorded, not fitted. The polar's coefficients are the binary's; the
zero-thrust footage disagrees below cruise by about half; whichever of the two is wrong, it is not
a constant in this model. The most likely reconciliations, the clip's γ accounting inside the
degenerate fit, or a mechanism outside the per-tick force path, are for a future re-decode of the
clip, not for the flight model.

## Stall

The stall flag is `1 − L(at 9° AOA) / Weight`; the aircraft is stalled once maximum available
lift can no longer carry its weight. The harness locates stall speed by stepping speed upward in
**0.044704 m/s (exactly 0.1 mph)** increments until the flag flips.

Solving the cap against weight gives a closed form:

```
V_stall = sqrt( 2 · Weight / (0.75 · ρ · RefArea) )
```

(the `0.15 · Mach` term is negligible at stall speed).

**Sanity check** with the parser's own fallback aircraft (`veh_weight` 3500, `ref_area` 335):

```
q_stall = 3500 / (0.75 · 335)      = 13.93 lb/ft²
V       = sqrt(2 · 13.93 / 2.2688e-3) = 110.8 ft/s = 33.8 m/s = 75.5 mph
```

A correct stall speed for these aircraft, and it only comes out right in the dense atmosphere
band, which is what settles that question.

`stall_mag` (fallback **0.45**) is a separate quantity, the magnitude of the stall departure; it
is not part of the lift calculation.

**B15 landing note, the G convention, and a discarded footage figure.** `Weight` above is the BARE
authored `veh_weight`, a load factor of exactly 1 G, not `nom_gravity / StandardG ≈ 2.037`, the
load factor level flight itself needs to cancel this install's arcade gravity (B11's identity: a
demand of exactly `nom_gravity` cancels weight at zero incidence, and `nom_gravity` here is
20 m/s² against `StandardG` 9.82). The two conventions differ by
`√(nom_gravity/StandardG) ≈ 1.43×`. This page's own sanity check settles which one is coded: 75.5
and 309 mph (dense/thin bands) both reproduce ONLY under the bare-Weight read; the
nom_gravity-scaled read gives 109/447 mph instead, numbers this page never quotes. So 1 G is what
`FUN_00490f70` actually compares against, not a simplification of it.
Evaluated against a REAL airframe rather than the fallback numbers, this surfaces a residual the
fallback coincidence hides: the Bloodhawk's own data (`veh_weight` 1900, `ref_area` 330) computes a
56.5 mph stall, against the "Stall 0% Thrust no input" clip's measured ~76 mph nose-drop
(`src/Flight/Airframe/FlightModel.cs`'s stall-threshold comment). The remake's retired `StallSpeedFrac = 0.25`
matched that clip only because 0.25 × the BLOODHAWK's fd_speed (302 mph) happens to sit close to
the FALLBACK aircraft's own stall speed (75.5–76.3 mph), the wing-loading coincidence this plan
warned not to read as validation, and it does not extend to the Bloodhawk's real numbers. **The
computed stall stands; the clip's ~76 mph does not contest it**, a frame-derived speed ranks
readings, it does not confirm or refute a decode (`docs/verification.md` DET-12). Not resolved by
switching G-conventions to fit one clip.

## The two stall cues, a lamp and a nose-drop, on two unrelated thresholds

The two cues sit on **different quantities**, and neither threshold is a tuning constant:

- **The nose-drop** fires below the airframe's own computed stall speed, the decoded
  `clMax · q · RefArea = Weight` solve above. **Decoded.**
- **The STALL lamp** lights below an available load factor of **2.35 g**. **Decoded**: the driver is
  `s = (plane+0xf4 + 1.35) × 0.425` at `0049f7e6`, and `plane+0xf4` holds `1 − n_avail`, written
  every frame at `0048e7dd` from the same out-parameter the nose-drop block below fills. The lamp is
  dark while `s ≤ 0`, i.e. while `n_avail ≥ 2.35`. ⚠ The earlier reading of a fixed
  **0.30 × `fd_speed`** (0.2989–0.2996 across four clips) was **measured off footage and is the
  wrong quantity**: it fits because lift goes as v², so the gate is equivalent to `1.533 × v₁g`, but
  only while the AoA cap is not binding. Read the lamp off the load factor, not off a speed
  fraction. Full decode and addresses in [`../formats/hud.md`](../formats/hud.md), "Cockpit gauges".

In the original's "Stall 0% Thrust no input" clip the lamp led the Bloodhawk's own break by
**2.64 sim s / 14.9 mph**. `CSVM.Tests/StallWarningTests.cs` asserts the ORDERING (warn leads
stall) and the MECHANISM (two independent thresholds on one margin), **not** that absolute lead,
the Bloodhawk's computed 56.5 mph stall does not reproduce the clip's ~76 mph nose-drop, and the
decoded solve is the answer with the clip's figure discarded beside it rather than something a
test papers over.

⚠ **Do not fold the two thresholds together**, and do not move the lamp onto the computed stall
speed to tidy the split away. They are unrelated by mechanism, not by oversight: the lamp is a
load-factor margin, the nose-drop an airspeed one.
⚠ **Do not exercise the split on the executable's fallback aircraft.** The fallback airframe's own
computed stall (75.5 mph, i.e. ≈0.30 of its own reference speed) sits almost exactly AT the warn
threshold, which *inverts* the split instead of testing it. The tests fly the Bloodhawk's real
1900 / 330.

**The lamp's blink is a rate ramp, decoded.** The half-period is
`0.4 − 0.3·s`, i.e. **`0.100375 + 0.1275 · n_avail` seconds**, bounded to (0.100, 0.400] by
construction and recomputed at each toggle, so it shortens with stall depth and lengthens again as
the aircraft accelerates back. ⚠ **The measured 643 ms / 296 ms half-periods (`CAP-06`) are
superseded.** They were quoted in sim seconds at k = 1.390, and the binary cannot produce 643 ms at
any input; taken as wall seconds the same two figures read 462 ms and 213 ms against the decoded
100–400 ms range. ⚠ **The lamp and the flight model share one dt**, copied bit-for-bit at
`004897d8`, so no k conversion separates the lamp's constants from the aero block's. Do not apply
one to these figures.

### The nose-drop's rate is `stall_mag`, and it is a TORQUE

The original's nose-drop is the last block of `FUN_0048c470`, behind the player-only guard. The stall
flag is written to the out-parameter as `1 − L(9°)/Weight`, and where it is positive the block builds
`axis = nose × worldUp` (`DAT_006379c0` reads `(0, 1, 0)`, confirmed from memory), cancels whatever
accumulated torque already opposes that axis, and adds along it:

```
Δ(torque accumulator) += axis · (stall_mag · stallFlag · dt)      [dt multiply at 0x48d11b]
```

`stall_mag` is `_DAT_0071c41c`, authored from the `stall_mag` token (string at `0x627854`, parsed at
`0x4742de`-`0x4742fe`), compiled fallback **0.45** (the immediate `0x3ee66666` stored at `0x4742fe`,
on the branch the parser takes only when the token is ABSENT). There is **no second rate constant**:
the one scalar is the whole of it. It is also a **single global, not a per-airframe field**: the one
write pair is in the player-globals parser `FUN_004735b0` and the force path reads the global
directly at `0x48d10d`, so no plane record can carry its own.

⚠ **This install authors 1.25, so the fallback is the wrong number to reason with** (corrections
table below). Every figure here is the authored value's.

Three consequences, and they are what separate this from the remake's version:

- **It enters the torque accumulator, not the attitude.** So it is scaled by `rec_moments_inertia`
  and damped by `ang_momentum_damp` downstream, exactly as "Torques and the limiters" warns for
  anything else entering that accumulator. On the Bloodhawk (`rec_moments_inertia.x` 1.18,
  `ang_momentum_damp` 5.0) full stall depth settles at `1.25 · 1.18 / 5.0` ≈ **0.295 rad/s**, under
  the fallback it would be `0.45 · 1.18 / 5.0` ≈ 0.106, and either way an order of magnitude under a
  direct rotation at the scalar itself.
- **The cancellation is what holds the nose down, not the magnitude.** Full-elevator pitch authority
  on the same airframe is ≈0.585 rad/s (`pitch-rate` 33.54 °/s), well over the deepest stall torque,
  so a straight contest would go to the elevator. There is no contest: the `FCOMP` at `0x48d0ce`
  tests the accumulated torque against the axis and, where it opposes, the projection
  `−(t·a)/(a·a)` removes that component **entirely** before the drop is added. A pull along the drop
  axis is deleted, not outvoted, so the nose cannot be raised while the flag is positive. That is
  the original's own version of "no raising the nose over the horizon in a stall", and it needs no
  separate cap, floor or minimum rate on top of it.
- **The axis is not normalised**, so the rate carries a `cos(nose elevation)` factor and falls away
  as the nose leaves the horizontal. The nose is pushed down about a horizontal axis; it does not
  chase world-down, and a bounded nose-down attitude is an equilibrium between this torque and the
  restoring terms rather than the end of a chase.
- **The depth measure is the lift-versus-weight flag**, `1 − L(9°)/Weight`, not a speed ratio.

**This is what the remake flies.** `FlightModel.StallNoseTorque` builds the term above and folds it
into the same accumulator the stick, the bank coupling and the weathervane feed, with the sign test
and the projection intact; `FlightModel.StallFlag` is the `1 − L(9°)/Weight` depth, which is the
already-computed lift cap subtracted from one. The `StallNoseRate` multiplier, the attitude rotation
it scaled and the hand-rolled over-the-horizon cap that used to follow it are all retired: the first
was a no-op on `stall_mag`, and the cancellation supersedes the third. `StallNoseDropTests` pins the
closed form, the equilibrium and the cancelled pull.

### Why the autogyro's low-speed nose-down is softer, term by term

The Hoplite autogyro's nose-down at low speed reads softer than another airframe's, and every term
it passes through is decoded and authored. **Nothing in the drop is per-airframe except where the
flag turns on.** `stall_mag` is a single global (1.25 here), the axis and the cancellation are
state-derived, and the two numbers an airframe could differ on are authored identical to the
Bloodhawk's:

| Authored | autogyro | Bloodhawk |
|---|---|---|
| `veh_weight` / `ref_area` | 500 / 800 | 1900 / 330 |
| wing loading (lb/ft²) | **0.625** | **5.76** |
| computed stall speed | **18.5 mph** | **56.5 mph** |
| `rec_moments_inertia.x` | 1.18 | 1.18 |
| `ang_momentum_damp` | 5.0 | 5.0 |
| `return_rate` | 3.0 | 3.0 |
| `pitch_torque` | 3.4 | 3.3 |

So at the same depth of its own stall the two airframes drop at the same rate to within the lift
cap's Mach term, and the whole of the difference is that the autogyro's ninth of the wing loading
puts its break at 18.5 mph. Through the 20–55 mph band a pilot calls low, the autogyro is not
stalled at all and its nose-drop term is exactly zero, where the Bloodhawk at 40 mph is already at
flag 0.50. Below the break the authored low-speed ramp has taken most of its elevator (21 % of pitch
and roll authority at 18.5 mph, against the Bloodhawk's 100 % at 56.5), so the aeroplane mushes with
neither a firm pull nor a hard break. The ramp scales the STICK only, so it never softens the drop.

**What actually carries the nose over is the weathervane**, not the drop, on both airframes. In a
matched 70 mph nose-high entry at idle the drop peaks at 33 °/s² on either aeroplane while the
weathervane reaches 144 °/s² on the autogyro and 85 °/s² on the Bloodhawk, both at
`return_rate · α/2 · rec_moments_inertia.x`. The AOA window is at its decoded strength through all
of it and touches neither term: it scales a pitch or yaw STICK command that opposes the closing
axis, so it cannot soften a centred stick.

**The AI arm has no plant nose-down to soften.** The drop (`0x48d158`) and the weathervane
(`0x48ce3d`) are both behind the player guard, so an AI-flown autogyro holds its attitude through a
stall with a centred stick and every degree of nose-down it flies is the control law's command. The
law's low-speed recovery arm keys on the BACKWARD axis's Y (`noseY < −0.5` and under 60 mph), so it
fires nose-UP and slow and commands the nose down there, and never in a dive.

**`is_autogyro` reaches no flight-plant term.** The authored flag (string `0x627d68`) parses to the
vehicle record's byte `+0x21c` at `0x4792c4`, and the whole program reads that byte twice. At
`0x4876f4`, inside the mouse-flying arm of `FUN_00487460` (reached only with the mouse control bit
of `DAT_0071c2a0` set and the free-look flag `DAT_00654120` clear), it exchanges and negates the
roll and yaw sources, so an autogyro yaws with sideways mouse motion where an aeroplane rolls; the
pitch write at `0x4876e1` has already happened and is untouched. At `0x420205`, in the AI maneuver
chooser `FUN_004201a0`, it gates each row of the 17-entry `maneuvers.zrd` table (base `0x71b210`,
stride `0x1c`) on that row's byte `+0x06`, dropping maneuvers an autogyro may not fly from the
candidate list. CSVM carries the first of those two in `MouseFlight.Read`, which the mouse
flight-control scheme reads through (`docs/controls.md`). Neither reaches a torque, an authority
curve or the stall. The class dispatch does
not single it out either: `pautogyro` authors no `mode` and inherits `basic_airplane`'s `mode jet`,
so the Hoplite flies the class-0 aeroplane arm like the other ten and the class-1 arm
`FUN_0048ffe0` never sees it.

`AutogyroStallNoseDownTests` pins the equal-depth equality with a softer-inertia control, the empty
nose-drop at 40 mph beside the Bloodhawk's, the ramp scaling the stick and not the drop, the
recovery arm's nose-high-only trigger, and the AI path holding its nose where the player path drops
it. `Probes.StallEntry` (`ZzAutogyroStallInstrument`) is the per-step readout the paragraphs above
quote.

## `lift_accel_rate` is a lag toward a target velocity

The nose-chase is real and authored: `lift_accel_rate` (string `0x6278b4`, global `_DAT_0071c448`,
compiled fallback `0x3f99999a` = **1.2**, this install authoring **0.75**) has two readers,
`0x48c746` in the force build `FUN_0048c470` and `0x49112a` in `FUN_00490f70`, and both are the same
block:

```
a  = lift_accel_rate · (targetVelocity − velocity)     [0x48c70d-0x48c776]
a.y += gravity                                         [0x48c77b, *(obj+0x64)+0xc4]
a  = a · orientation                                   [into body axes, 0x48c78a…]
```

`FUN_0048c470` splits at `0x48c522` on whether the object is the player (`ESI` against
`_DAT_0071c298`) and the two sides differ only in how they build `targetVelocity`. They rejoin at
`0x48c70a`, so the lag itself is unconditional.

**The player's `targetVelocity` is the `liftAOAs` relative wind, nothing more.** The three virtual
calls at `0x48c6b6` / `0x48c6c8` / `0x48c6db` are ONE virtual getter, vtable slot `+0x4` on the
aircraft object, returning a pointer to its world-velocity float3 (m/s); the compiler re-fetches it
once per vector component because the call can clobber the pointer. The block they sit in is the
blend's middle branch:

- `0x48c528`–`0x48c56d`: `cos α = (velocity · nose) / speed`, from the same getter dotted with
  `obj+0x198` (`m[2] = −nose`) and negated.
- `cos α ≥ _DAT_0071c430` (`liftAOAs[0]`): `targetVelocity = velocity` verbatim
  (`0x48c576`–`0x48c58e`).
- `cos α ≤ _DAT_0071c434` (`liftAOAs[1]`): `targetVelocity = speed · nose`
  (`0x48c648`–`0x48c65f`, then the jump at `0x48c670`).
- between: `t = (liftAOAs[0] − cos α) / (liftAOAs[0] − liftAOAs[1])` (`0x48c676`–`0x48c68f`), and
  the three calls accumulate `targetVelocity = t · speed · nose + (1 − t) · velocity`
  componentwise (`0x48c691`–`0x48c6e7`).

Every non-player object takes `−speed × m[2]` = `speed · nose` instead (`0x48c6e9`–`0x48c704`),
the blend's saturated end. This is exactly `FlightModel.Step`'s `relativeWind`; no thrust, throttle
or other contribution enters the target.

**What the lag vector then feeds is the demand path the remake already flies, not the
acceleration.** After the rotation into body axes, `FUN_0048c470` takes the body-X/Y (wing-plane)
components, forms `n = |(a·m0, a·m1)| / 9.82` and their unit direction, and skips the whole build
below 2.4384 m/s (8 ft/s, `n = 0`). `FUN_0048fc40` then composes the force: the atmosphere lookup
(`FUN_0041aca0`/`FUN_0041ac80`) into `q` and Mach; thrust
`engine_power · RefArea · curve(Mach) · throttle` with the attitude factors `1 + 0.24·m2.y`
(always) and `1 + 0.13·m2.y` (nose-up only); lift `FUN_0041abd0(Mach, q, n)` delivered along the
demanded in-plane direction; Mach-polar drag (`FUN_0041ada0`) opposing the velocity; weight
`(gravity / 9.82) · veh_weight` on world Y. The caller converts the force sum to acceleration by
`9.82 / veh_weight`, and the integrator `FUN_0048e580` does `velocity += a · dt`, applies the AI's
4.4704 m/s along-nose floor, and steps position. **No instruction in that chain rotates the
velocity direction onto the nose.** The plant's only kinematic velocity rotation is the
ground-blow steer (`FUN_00460700` at `2.0 · S` per second, see "Ground blow" below).

**The "spends the lag as the acceleration" reading is true only of the far-field branch.** The
first branch of `FUN_0048c470` (crashed player, or beyond 1 km of the player,
`FUN_00538920 > 1e6` m²) writes `a = throttle · fd_speed · nose − velocity` directly, with no
`lift_accel_rate`, no gravity and no force build; that is the simplified speed-hold plant, decoded
in full under "The far-field plant" below. The near-field path never does this.

⚠ **Nothing at either reader touches bank or wing verticality.** The remake's `KnifeAlignFloor`
weakened a chase by `|bodyUp·up|`; the binary has no such factor, so the constant and its
`wingVert` input are retired. That the knife-edge nose–path gap reads smaller than the footage's
puts the filmed gap in the same class as the figures beside `yaw-360` and `decel-290-150`:
discarded, and recorded as an annotation rather than tuned away.

⚠ **The remake's second chase is retired: `NoseChaseFactor` is decoded-absent and held at 0.**
`FlightModel.Step` used to rotate `VelocityDir` exponentially onto the nose at `lift_accel_rate`
on top of the delivered lift, which applies the same first-order swing twice when unclamped
(`ω = rate · α` on both paths) and bypasses the ±5/+9 G and ceiling clamps when the demand
saturates. The config key `flightModel.noseChaseFactor` is the A/B seam: 1 restores the old
composition (measured byte-identical to the pre-removal eleven-airframe dump), 0 is the decoded
default. The ground-blow steer keeps its own rotation, which is the original's.

**What the removal moved, and what it did not.** Equilibrium rows are unchanged on all eleven
(level top speed, terminal dive, altitude cap, eighth-throttle, decel). Saturated and
transient rows moved: Bloodhawk `pitch-rate` 35.87 → 32.43 °/s against the read 33.00, the
knife-edge drift 1.19–1.21 → 0.86–1.08 °/s against the filmed 0.69–0.89, `zoom-climb-min-speed`
176.7 → 117.9 mph against the read 127.9, and the max-pull turn now takes its rate from the
clamped 9 G lift (`sustained-turn-rate` 34.5 °/s, with `CAP-01`'s own rate discarded beside it).
The sustained climb did NOT move (204.03 mph plateau): its trajectory holds α = 0, where the
retired chase was a no-op, so the climb residual against the filmed 163.05 is NOT owned by this
path. The two owners it named next are settled too: the throttle spending is a plain linear
multiply that is 1 at the filmed full throttle (see "Part-throttle equilibrium"), and the band is
the dense one (see Atmosphere). What is left is the α the climb path holds.

⚠ **The clamp asymmetry is unreachable, and CSVM already matches.** `FUN_0041abd0` clamps `C_L` to
±1.8 and then applies the compressibility ceiling as a one-sided `min` with no sign handling
(`0x41ac5f`–`0x41ac7b`), which reads as a NEGATIVE ceiling of −1.8 against a positive
`0.75 − 0.15·M`. The negative side never runs: both call sites build the `n` this function receives
as the length of the demand vector (`0x48c821`–`0x48c852`, `0x49122e`–`0x491236`, a sum of squares
through the integer sqrt approximation, then `/ 9.82`), so `n ≥ 0` and `C_L ≥ 0` always. The sign of
the lift lives in the separately normalised direction the force is applied along, not in the
coefficient, so a pushover arrives as a positive `C_L` and meets the same `0.75 − 0.15·M` ceiling a
pull does. The −5 G clamp is dead for the same reason: the original limits total demand to 9 G in
either direction. `FlightModel` reproduces the structure exactly, `LoadFactorDemand` being a
`liftDir.Length()`, so capping both signs at the positive ceiling is the decode rather than a
departure from it.

## The far-field plant

`FUN_0048c470` opens on a test that decides which of two plants the aircraft flies for this step,
and it is not the AI/player split it resembles. The aircraft takes the **far-field** branch when its
`fd` flag `[obj+0x384]` is set (`0x48c4ba`), **or** when it is not the player and
`FUN_00538920(obj+0x204, player+0x204)` exceeds the float at `0x00607a18` (`0x48c4e9`–`0x48c4fc`).
That helper returns `Δx² + Δz²`, so the separation is **horizontal** and the vertical gap is dropped,
and the constant is **1e6 m²**, which is 1000 m. The compare is a strict `>` with no hysteresis and
no timer: an aircraft sitting on the line alternates plants frame by frame. Everything at or inside
1000 m, AI and player alike, flies the full aerodynamic path.

The far branch is a level-of-detail model rather than an AI interface. What it computes is one
target velocity and the gap to it:

```
target = nose · (throttle · fd_speed + 5)      [0x48c593-0x48c5d4, the 5 at 0x006036bc]
a      = target − velocity                     [0x48c5ec-0x48c603]
```

`fd_speed` is `[obj+0x668]` and the throttle is the current lever `[obj+0x128]`, so the speed the
aircraft holds is the one the data sets times the lever. The 5 m/s is added for anything that is not
the player (the `FCHS` at `0x48c5aa` runs before the `FSUB`, and `[obj+0x198]` is `m[2] = −nose`, so
the subtraction raises the along-nose speed rather than lowering it). The output is the same
linear-acceleration parameter the near path writes at `0x48c8a4`, so the lag rate is exactly **1/s**
and the integrator spends it as `velocity += a · dt` like any other acceleration.

What the far branch skips is fixed by two tests of the same flag (`[EBP+0x1b]`, set at `0x48c5a6`
and cleared at `0x48c50b`):

| Skipped | Where | Effect |
|---|---|---|
| the whole force build | the jump at `0x48c643` past `0x48c648`–`0x48c8c3` | no lift, no Mach drag, no thrust, no weight, and no `lift_accel_rate` |
| gravity | `0x48c77b`, inside that range | a distant aircraft does not fall |
| the authority curves | `0x48c8e5`, which sets all three factors and the reverse-authority factor to 1 instead of calling `FUN_0048bdd0` | no low-speed ramp and no high-speed pitch fade |
| the opposing-command limiter | the same jump, forcing its scalar to 1 past `0x48c93c`–`0x48ca79` | neither the AOA window nor the G ramp reaches the commands |
| bank coupling | `0x48cc56`, past `0x48cc61`–`0x48cd3d` | a distant aircraft's bank no longer turns its nose |

What still runs is as decoded as what does not. The three stick torques (`0x48ca7a`, `0x48caea`,
`0x48cba0`) accumulate normally, with their authority factors at 1; the ground blow `FUN_0048c220`
is called at `0x48cf95` for every aircraft but a crashed player; and the integrator's own along-nose
floor is outside this function entirely. The weathervane block at `0x48cd6c` is player-only
(`0x48cd3e` jumps away for anything else), so a far AI losing it changes nothing: the arm a
non-player takes instead is the never-authored `level_off_rate` auto-level at `0x48ce45`, which is
dead. ⚠ An earlier reading of this branch said it computes "no ground blow or weathervane at all";
the ground blow does run, and the weathervane was never the AI's to lose. ⚠ A second earlier reading
placed `0x48c520` inside this far-field test. It is not: that compare selects the `liftAOAs` wind
blend against the AI's saturated nose-aligned wind (`0x48c522` jumps to `0x48c6e9`), so it is a
player compare and remains the evidence `docs/architecture.md` cites for the AI force path. The
far-field test is the pair at `0x48c4d7` and `0x48c4e9`.

**How CSVM flies it.** `FlightModel.FarFieldPlant` is re-decided every step from
`FlightInput.NearestHumanDistSqM`, which `FlightController` fills from the session's
`PlayerPositions` snapshot. The original measures against its single player pointer; CSVM measures
against the **nearest human pilot**, deliberately widening a player-only behaviour to all four
human pilots. This is the only difference from the decode. The `[obj+0x384]` arm is deliberately not
ported, and that is now the decode rather than a gap: the flag is a developer switch no gameplay
event sets, so a wreck in the original flies the near-field plant exactly as CSVM's does. Two
constants of the plant's inventory come from here,
`FarFieldRangeM` and `FarFieldAiSpeedBonus`.

⚠ **This branch does not explain a hard-banking AI.** It was once built to test that hypothesis
against `BL-387`'s net-follower and measured not to: mean bank 66° against the near plant's 64°,
peak 90° on both. The bank is the AI law's direct output (`roll = −bx` rolls until the target sits
in the vertical plane), not a response to a turn requirement, so removing lift removes the need to
bank without touching the command to. The port stands on faithfulness alone.

### `+0x384` is a developer switch, not a crashed flag

The whole binary writes `[obj+0x384]` at four instructions, and every one of them is either object
construction or a developer input. No crash, ground contact, death, damage or mission-reset path
touches it.

| Write | Value | Runs when |
|---|---|---|
| `0x004b08c5` in `FUN_004aff80`, the vehicle constructor | 0 (`EBX`, zeroed at `0x004affaf` and never reloaded in the function) | always, inside the field-clearing run from `+0x2e8` to `+0x638` |
| `0x004753ef` in `FUN_004735b0`, the per-mission initialiser | 1 | the global byte `0x0071dac9` is set, on the player aircraft `[0x0071c298]` |
| `0x0043e5dc` in `FUN_0043d640`, the debug console | the parsed argument, 0 or 1 | the console line is exactly two tokens and the first is `fd` (`0x00622ee8`) |
| `0x0043e626` in the same console | 1 | the console line is `ifon` (`0x00622eec`) |

`0x0071dac9` is a command-line flag, not a game state. `0x004a74da` sets it when an argument matches
`-fd` (`0x006298d4`), and the defaults block at `0x004b3764`–`0x004b3773` clears it with its
neighbours before any argument is read. Its neighbour `0x0071dac8` is `-nodie` and drives
`[player+0x920] = 1` at `0x004753da`, the byte that skips the contact push-out; the console's `ifon`
sets that same pair, `+0x920` at `0x0043e617` and `+0x384` at `0x0043e626`. The mission initialiser
applies both immediately after it publishes the player aircraft into the world object
(`FUN_0042c250` at `0x004753c6`, the single pointer slot `[0x0064ef78]+0x150`), which is also the
object the console's `fd` command resolves. The `fd` argument goes through `FUN_005b7910`, which
returns 1 for `on` (`0x00639928`) or `true` (`0x0063992c`) and 0 for anything else, so `fd off` is
the only writer that can clear the flag after construction.

⚠ **No typed cheat reaches either byte.** The walkthroughs' in-flight invincibility word
`I AM THE ACE!!` is in the image in no encoding (ASCII, UTF-16 and the obfuscations the menu
strings use), no instruction compares a character against `'I'` as the first of a word, and every
writer of `+0x920` is a command-line flag or a debug console line, the pair named above among them.
The menu cheats the scripts
do carry are typed into a `gui_char` body and named in
[../formats/campaign-screens.md](../formats/campaign-screens.md); none of them is this one.

What the switch does is consistent across its readers: the aircraft holds `fd_speed · throttle`
along its nose with no aerodynamics (`0x48c4ba`), keeps no weathervane (`0x48cd52`), takes no ground
blow (`0x48cf8e`), no stall torque (`0x48d168`), no contact placement or impulse (`0x48dfc8`), no
collision damage or camera kick (`0x48d3aa`), and the keyboard handler widens the throttle clamp
from `[0, 1]` to `[−5, +5]` (`0x487ab6`). That is a fly-anywhere debug mode named after the
`fd_speed` key it flies on, which is why `docs/formats/hud.md` sees it force the stall lamp dark.

⚠ **"Crashed flag" was a guess at the name and it was wrong.** Everything the readers do is still
what the entries above and below say it is; what changes is that no wreck ever reaches those arms.
A crashed hull in the original flies the same near-field plant a live one does, which is the
"A destroyed hull flies the same model" finding reached from the other end.

## The keyboard stick is an accumulator, not a switch (`FUN_00487460`)

The player input handler runs once per frame from the tick function `FUN_004897c0`, which sets the
flight model's `dt` (`DAT_0071c56c`) from the same frame delta (`DAT_009ad744`) the handler ramps
with, input and flight share one clock. Each of the three stick axes is a stored deflection the
keys move, not a flag the keys set:

```
axis = 0                                     ; if the key is released, or the command opposes
                                             ;   the current sign — pitch: 0x48786a
axis += dt · dir · 2.5                       ; while a key is held — pitch: 0x487880-0x48788f,
                                             ;   the 2.5 at 0x006040a8
axis = clamp(axis, -1, +1)
```

Pitch is `obj+0x108`, roll `obj+0x100`, yaw `obj+0x10c`, all three the identical block (roll at
`0x48794c`, yaw at `0x487a18`); the throttle follows at `0x487a54`/`0x487a7d` with the same shape
and its own `±0.5·dt`. The three are then copied to the slots the force path reads, `obj+0x114`
roll, `obj+0x11c` pitch, `obj+0x120` yaw, through `FUN_00460890`, **which is an identity stub**:
its `0.5`/`1.0` arguments are dead, so no expo curve exists on the way out.

**The rule, and its asymmetry.** A held key takes **0.4 sim s** to reach full deflection; a released
or reversed key drops the axis to centre in a single frame. Gradual on, instant off. An analogue
axis bypasses the ramp entirely, the joystick path assigns its scaled value to the same slot and
suppresses that frame's zero-snap, so a stick's deflection is absolute where a key's is accumulated.

**What it settles.** A tap never reaches the deflection its key nominally commands: 115 ms of press
is `2.5 × 0.115` = **0.29** of full travel. The original's fast pitch cadences therefore fly a
much smaller stick than its slow ones, which is roll-off produced in the input stage before any
aerodynamics are involved, see the landing note below.

**Landing note, with the kinematic nose-chase retired, the roll-off gap is closed.** Ported as
`StickRamp`, applied to the keyboard axes in `FlightController.ReadKeyboard` (the gamepad's
analogue axes add on top, unramped, matching the joystick path). Driving `ZzCadenceSweep` through
the ramp on the current plant (`NoseChaseFactor` 0) reads the 1300 → 570 ms roll-off at **42.0×
against the original's 42×** (unramped 34.4×). The 1.57× deficit this note used to record was
measured with the retired chase still gluing the flight path to the nose, which suppressed the
low-cadence ripple; the two speculative candidates it listed (the `liftAOAs` blend under a
reversing demand, a resonance at 570 ms) are moot with the gap gone.
⚠ **Quote the sweep's WALL reading, not its sim reading.** The macro drove the keys in wall
milliseconds, so the period the game saw is that × 1.390 (`docs/verification.md` DET-11); the sim
column answers a question nobody flew. It used to be defensible to quote either, because with a
square wave on both sides the ratio barely moved between them, a rate limit destroys that, since
2.5/s is an absolute timescale that does not rescale with the cadence. The sim column reads 46.2×
for the same run.
⚠ **The 23.3× the C23 note above quotes is neither today's baseline nor the right column**, and
ratios are comparable only within one run.

## Control authority vs speed

`FUN_0048bdd0` derives three independent scalars from airspeed alone, reading nine globals at
`0x0071c3f8`-`0x0071c418`. (The debug copy `FUN_00490e10`, which this section used to name, reads
the same nine and computes the same curves.) Fallback values shown as authored (MPH):

- **Base ramp** `f`, 0 below `turn_fade_in` (**10**), rising linearly to 1 at `turn_fade_out`
  (**40**), then held at 1.
- **Roll authority** = `f`. **No high-speed fade**, roll never degrades with speed.
- **Pitch authority** = `f`, then faded by `high_speed_pitch_fade`: full until **500**, falling
  linearly to **zero at 600 mph**. The fallbacks are the immediates at `0x474179` / `0x474183`, and
  the authored path parses the `high_speed_pitch_fade` token (string at `0x6277f8`) at
  `0x4741f2`-`0x474231`, each field converted from MPH.
  ⚠ **Pitch and roll do NOT share one curve.** The second stage is pitch-only, and the two stages
  multiply. It is invisible on the fallback numbers, since 500 mph is above every airframe's
  `fd_speed`, which is also why the video reads pitch rate as flat with speed. An install that
  authors a lower `high_speed_pitch_fade` binds it.
  **Implemented** as `FlightModel.PitchAuthorityAt`, with `RollAuthorityAt` carrying the base ramp
  both axes start from.
- **Yaw authority**, piecewise, and deliberately **not monotone**:

  | Speed (mph) | Authority |
  |---|---|
  | ≤ `yaw_fade_in` (10) | `yaw_low_speed` = **0.05** |
  | 10 → `yaw_max` (22.5) | ramps 0.05 → **1.0** |
  | 22.5 → `yaw_fade_out` (45) | ramps 1.0 → `yaw_high_speed` = **0.1** |
  | ≥ 45 | **0.1** |

  The rudder is at full authority only in a 22.5–45 mph window and sits at **10 %** for all of
  normal flight. It is a ground-handling and low-speed control, not a flight control.

- **Reverse-authority factor** (decoded 2026-08-14; its consumer traced 2026-08-15).
  `FUN_0048bdd0` has a fifth output the debug copy lacks: above `yaw_max` (`0x0071c414`, authored
  **50 mph**) it returns `max(yawAuthority, 0.2)`, and **1.0** at or below it
  (`0x48bf16`–`0x48bf58`; the 0.2 is the immediate at `0x6034fc`). Because `yaw_max` is the speed at
  which the yaw curve peaks, it engages across the whole of normal flight: it tracks the declining
  yaw curve from 1.0 at 50 mph down to the **0.2** floor, which the curve reaches at ≈345 mph. At
  the Bloodhawk's 302 mph cruise it is ≈0.40.
  ⚠ **It is not a force term, and the "softens control forces that oppose the current velocity
  vector" this entry used to carry was a misattribution.** `FUN_0048c470` takes it as an
  out-parameter, writes it once and never reads it again; its own opposing-command softening is a
  DIFFERENT, local quantity (see "Torques and the limiters" below). The factor is passed out to
  `FUN_0048c470`'s only caller, `FUN_0048e580`, whose single use of it is at
  `0x48ec0b`–`0x48ec1d`: `rudderAngle = factor · yawInput · −0.61086524` rad (**−35°**, the
  immediate at `0x608120`), exponentially smoothed toward that target at 2/s by `FUN_00460490` into
  the angle slots `obj+0x634`/`+0x638`, which `FUN_004b2fe0` applies as a node rotation to the two
  rudder node lists at `obj+0xa04`/`+0xa14`. **It scales the VISIBLE rudder deflection, on the
  player's aircraft only, and touches no torque.** Ported at that site (`ControlSurfaceMix`), not
  in the force path.

### The original's control-surface animation

The same block in `FUN_0048e580` (`0x48eaf0`–`0x48ec92`, inside a `piVar3 == DAT_0071c298`
player-only guard) drives **six** angle slots off three stick channels: `obj+0x100` roll,
`obj+0x108` pitch and `obj+0x10c` yaw (the one the reverse-authority factor scales). Each slot is
smoothed exponentially toward its target at 2/s by `FUN_00460490`, and `FUN_004b2f00` /
`FUN_004b2f70` / `FUN_004b2fe0` apply the six as node rotations over six node lists:

| Slot | Node list | Nodes | Target angle | Clamp |
|---|---|---|---|---|
| `+0x63c` / `+0x640` | `+0x9c4` / `+0x9d4` | `l_aileronN` / `r_aileronN` | `−0.5·roll` / `+0.5·roll` | ±0.5 rad (28.6°) |
| `+0x62c` / `+0x630` | `+0x9e4` / `+0x9f4` | `l_elevatorN` / `r_elevatorN` | `−0.6·pitch − 0.18·roll` / `−0.6·pitch + 0.18·roll` | ±0.6 rad (34.4°) |
| `+0x634` / `+0x638` | `+0xa04` / `+0xa14` | `l_rudderN` / `r_rudderN` | `−0.61086524 · yaw · reverseAuthority` | none (−35° at full) |

**The lists are populated by name, not by geometry.** `FUN_004b27e0` (ailerons), `FUN_004b2a40`
(elevators) and `FUN_004b2ca0` (rudders) each run two `sprintf` loops over the six format strings
at `0x62aee8`–`0x62af2c` (`l_aileron%d`, `r_aileron%d`, `l_elevator%d`, `r_elevator%d`,
`l_rudder%d`, `r_rudder%d`), starting at index 1 and pushing every node `FUN_004d8cf0` finds until
a lookup misses. The six vectors sit at `obj+0x9c0`, `+0x9d0`, `+0x9e0`, `+0x9f0`, `+0xa00` and
`+0xa10` in that order, so the string order is the slot order. Nothing else writes them: the
constructor `FUN_004aff80` zeroes them and the destructor `FUN_004b0aa0` frees them.

**Named product exception: CSVM's name matching is wider than the original's.** The Fury's
deflecting rudder node is `l_rudder_rotate`, which no `l_rudder%d` lookup finds, so the original
flies that plane with a frozen rudder; the digitless `l_elevator` shape misses the same way.
CSVM's `RudderRe`/`ElevatorRe` accept those names and animate the surfaces, kept deliberately as
an improvement over the shipped behavior. The
mixing, angles and smoothing on every matched node remain the decoded values above.

The arithmetic per frame, with addresses:

- **Elevators** (`0x48eaf7`–`0x48eb25`): `roll · 0.18` (`0x6035a4`) is held while `pitch · −0.6`
  (`0x608124`) is formed; the left slot subtracts the roll term and the right adds it. Both are
  then clamped to ±0.6 (`0x48eb27`–`0x48eb87`).
- **Ailerons** (`0x48eb87`–`0x48eba2`): `roll · −0.5` (`0x6034f8`) and `roll · +0.5` (`0x6032e0`),
  clamped to ±0.5 (`0x48eba5`–`0x48ec05`).
- **Rudders** (`0x48ec05`–`0x48ec24`): `reverseAuthority · yaw · −0.61086524` (`0x608120`), one
  value written to both slots, with no clamp.
- **Smoothing** (`0x48ec27`, `0x48ec3c`, `0x48ec51`, `0x48ec66`, `0x48ec7b`, `0x48ec8d`): six
  `FUN_00460490(slot, target, 2.0)` calls, the rate the immediate `0x40000000`. That helper is
  `slot = target + (slot − target)·FUN_00460410(dt·rate)`, and `FUN_00460410` is `exp(−x)` (a cubic
  Taylor branch below 0.1, `FUN_0053e2e0` otherwise), so the smoothing is exact exponential decay
  with a 0.5 s time constant and is frame-rate independent.

Two consequences. The elevators MIX two channels, a common-mode pitch term with a differential roll
term at 36 % of the aileron gain, so they act as small ailerons and the ±0.6 clamp binds whenever
pitch and roll are commanded together on the same side. And the slot writes sit behind the player
guard while the three appliers do not (`0x48eca9`–`0x48ecb7`, past the guard's join at `0x48ec9c`),
so **an AI aircraft poses its surfaces every frame from slots that are never written**, which
leaves them frozen at neutral.

CSVM reproduces the table in `ControlSurfaceMix`, with `ControlSurfaceAnimator` owning only the
node side: which slot a node takes, its hinge axis, and the two frame flips a rotated mount and a
nose-mounted canard need. The guard is `FlightController.IsHumanPiloted` rather than a single
player pointer, which is this plan's Decision 3: every human pilot animates for splitscreen, AI
stays frozen as the original has it.

### The low-speed ramp, implemented 2026-08-15 (closing `BL-330`)

The base ramp `f` was the one part of `FUN_0048bdd0` the remake did not carry: `FlightModel.Step`
applied the authored yaw curve and **no speed term at all** to pitch or roll, so both held full
authority down to zero airspeed. It is now `FlightModel.RollAuthorityAt`, on the authored
`turn_fade_in` 10 / `turn_fade_out` 50 mph (the executable's fallback `turn_fade_out` is 40), a
scalar on the pitch and roll components of the stick command.

Three properties of the port, all read off `FUN_0048bdd0` and `FUN_0048c470` rather than assumed:

- **It scales the STICK COMMAND only.** `FUN_0048c470` multiplies the three authority scalars inside
  its three per-axis input blocks (`obj+0x114` roll, `obj+0x11c` pitch, `obj+0x120` yaw) and nowhere
  else, so the bank coupling, the weathervane and the ground blow enter the same accumulator at full
  strength. A slow aeroplane loses its controls and keeps the coupling.
- **The boundary is exclusive at the bottom.** `0x48bdd4` tests `speed > turn_fade_in`, so authority
  is exactly 0 *at* 10 mph, not merely small.
- **Pitch and roll start from the SAME scalar.** `0x48be20` writes it to the pitch output and
  `0x48be6c` to the roll output; the pitch output is then multiplied by `high_speed_pitch_fade`
  (`0x48be22`–`0x48be68`, the globals read at `0x48be26`/`0x48be3f`/`0x48be4c`/`0x48be56`/
  `0x48be5c`), which this install authors at [1000, 1001] mph and is unreachable.

Where 50 mph falls decides how visible this is, and it differs by airframe: nine of the eleven stall
at 52–57 mph, i.e. *above* the ramp's top, so for them the fade bites only once already stalling;
the Balmoral (45.5) reaches its stall at ≈89 % authority, and the autogyro (18.5) flies a long way
inside the ramp and stalls at roughly **21 %** of roll and pitch authority.

⚠ **"Roll never fades" above means never with HIGH speed.** Read as "roll authority is
speed-independent" it becomes the misreading that had `turn_fade_in`/`turn_fade_out` filed as a
candidate *bank* effect through four consecutive items, see the CORRECTION at the end of
"Bank-independent lift vs the measured knife-edge sag". The ramp is keyed on airspeed alone and is
saturated across the whole regime in which the footage reads a 1.6×-slower banked rotation.

## Torques and the limiters

Per axis, per tick:

```
Δω_axis = axis_torque · input · dt · authority_axis · rec_moments_inertia_axis
```

using `pitch_torque` / `rudder_torque` / `roll_torque` with the pitch / yaw / roll authority
scalars respectively.

One further scalar multiplies into **pitch and yaw only**, and only into a command that swings the
nose **further off the flight path**. `FUN_0048c470` builds the quaternion taking the nose onto `v̂`
(`FUN_0053fd40` at `0x48c9ae` on `nose` and `v̂`, converted by `FUN_0053fca0` at `0x48c9be`), which
is the same closing axis the weathervane uses, and compares the sign bits of the commanded torque
and of that axis' component on the axis being commanded (pitch at `0x48cb52`, yaw in the same shape
in the block from `0x48cba0`; roll's block at `0x48ca7a` has no such test). Opposite signs multiply
the command by the scalar; agreeing signs leave it alone.

The scalar is computed once per tick and shared by both axes. It starts at **zero** and is raised
only by the AOA term, so a standstill (`speed == 0`) removes a separating command outright:

- **AOA window**, `(cos α − cos maxAOA) / (1 − cos maxAOA)` at `0x48c9f4`–`0x48ca18`, zero at and
  beyond `maxAOA`. The fallback `maxAOA` cosine is **0.85** (≈ 31.8°); this install authors 46°.
- **G ramp**, read on the **delivered** load factor's body-up component, the value
  `FUN_0048fc40` writes to its seventh argument (`q · RefArea · C_L · (unit lift dir · body up) /
  Weight`), which is **signed**. Above `highGs[0]` it falls linearly to zero at `highGs[1]`
  (`0x48ca1e`–`0x48ca3a`); below `lowGs[0]` it mirrors to zero at `lowGs[1]`
  (`0x48ca45`–`0x48ca61`). Between the two starts the comparison is jumped entirely
  (`0x48ca50`), leaving the AOA term standing alone. It is **not floored at zero**: past
  `highGs[1]` it goes negative and reverses the command.

The smaller of the two is used (`0x48ca69`–`0x48ca78`).

⚠ **It damps ENTRY into a departure, not recovery from one.** A pull that raises α is softened; a
push that brings the nose back onto the path is not. This page carried the opposite conclusion for
as long as it read the sign test as one against the existing angular momentum, and that conclusion
did not survive the sign test's correction to the closing axis. Both directions are pinned in
`LatentControlAuthorityTests`, on pitch and on yaw.
⚠ This is also the quantity the reverse-authority factor was wrongly identified with; see that
bullet above.

**Implemented** as `FlightModel.OpposingCommandLimitAt`, applied to the pitch and yaw stick
components in `Step`. Both halves are live: the G ramp, and the AOA window at its decoded strength
(`FlightModel.AoaLimiterFactor` 1, config `flightModel.aoaLimiterFactor`; 0 is the A/B seam that
holds the window off). The window is not a threshold and binds on the shipped data; what it moves,
and why the filmed pitch rate is discarded rather than chased, are in "Corrected, the G ramp
grazes and the AOA window binds" and "The α a full pull holds" below.

⚠ **The G read is the SAME tick's delivered lift, which is what fixes the order of `Step`.** In
`FUN_0048c470` the force build `FUN_0048fc40` writes the delivered body-up load factor into its
seventh argument (`&param_1`, the call at `0x48c883`) and the ramp compares that value a few
instructions later (`0x48ca1e`, `FCOMP [EBP + 0x8]` against `highGs[0]`), all before the torques
accumulate; α comes off the entering velocity and attitude in the same call. The caller
`FUN_0048e580` then integrates both halves that one pass produced: the torque into the angular
velocity and the attitude quaternion first, the force onto the velocity vector second. So the
whole force and torque set is built from the attitude the tick ENTERS with, and only the AI's
nose-axis floor, the path alignment and the position step read the flown attitude.
`FlightModel.Step` is ordered the same way, and `LatentControlAuthorityTests` pins the alignment
by rebuilding each step's scalar from the state that step entered with.

⚠ **A fourth consumer exists and is unreachable.** The same scalar multiplies the `level_off`
torque (`obj+0x650`, the `level_off_rate` key, read at `0x48cedc`) when that torque opposes the
closing axis. The key is accepted by the parser and **never authored**, so the term is zero and the
remake carries neither it nor its limiter. `LevelOffRateAbsenceTests` pins both halves: the census
that all 24 shipped `dynamics` blocks author `return_rate` and none authors `level_off_rate`, and
the flight that a banked aircraft with centred sticks grows no roll rate.

`return_rate` is a separate centring torque, described below.

⚠ **`rec_moments_inertia` is applied downstream, not here.** `FUN_00490f70` accumulates a plain
`torque · input · dt · authority` world-frame vector; the integrator then rotates it into body
axes, scales each by `rec_moments_inertia` (`0x4918b9`–`0x491932`) and rotates it back. The
formula above folds the two steps together, which is exact, but it matters for anything else
that enters the same accumulator, because that too is scaled by the reciprocal inertia and damped
by `ang_momentum_damp`. The bank coupling below is exactly such a term.

Both are per-airframe fields of the plane record, parsed in `FUN_00479240`: `ang_momentum_damp`
(token at `0x627fdc`) into `+0x114` at `0x47add3`, and `rec_moments_inertia` (token at `0x628020`)
into `+0x118`/`+0x11c`/`+0x120` from list elements 0/1/2 at `0x47ae69`/`0x47ae75`/`0x47ae81`,
pitch, yaw, roll in that order. ⚠ **Neither has a compiled fallback**: each is a single store on
the token-present branch with no else, unlike `stall_mag`'s. `PlaneStats`' 5.0 and (0.8, 0.6, 1.3)
are therefore not the executable's defaults for these two, and an airframe that authored neither
would fly on whatever the record was initialised to; in practice every airframe authors both.

## Engine torque, every write to the angular accumulator, and none is one-sided

GDD §4.1.8 specifies a selective engine torque: nothing in level flight, nothing against the
torque direction, a faster turn with it. **The shipped executable carries no such term.** The proof
is the complete list of writes to the angular accumulator, the third argument of `FUN_0048c470`
(`[EBP+0x10]`, the vector `FUN_0048e580` adds to the persistent rate `obj+0x160`), with the inputs
of each. A one-sided term needs a sign that does not come from the aircraft's state: a constant
vector, a constant-signed scalar on a body axis, or a throttle factor. No write has one.

| Address | Term | Inputs | Fixed sign or throttle? |
|---|---|---|---|
| `0x48c4a6`–`0x48c4b7` | initialisation | the zero vector at `0x75d1b8` | no (zero) |
| `0x48c593`–`0x48c603` | far-field arm | writes the LINEAR output `[EBP+0xc]` only; the accumulator is untouched | no |
| `0x48cae2` | roll stick | `roll_torque [+0x644] · stick [+0x114] · rollAuthority · dt`, along `m[2]` (`+0x198`, −nose) | no (odd in the stick) |
| `0x48cb98` | pitch stick | `pitch_torque [+0x648] · stick [+0x11c] · pitchAuthority · dt`, along `m[0]` (`+0x180`), times the limiter scalar only when the command's sign bit differs from the closing axis' pitch component (`0x48cb52`) | no (odd in the stick; the limiter keys on state, not on a side) |
| `0x48cc4e` | yaw stick | `rudder_torque [+0x64c] · stick [+0x120] · yawAuthority · dt`, along `m[1]` (`+0x18c`), same limiter test at `0x48cc08` | no |
| `0x48ccb3` | bank coupling, yaw axis | `0.205 [0x6289f8] · m[0].y [+0x184] · dt`, along `m[1]` | no (odd in bank: a left bank yaws left, a right bank yaws right) |
| `0x48cd36` | bank coupling, pitch axis | `(0.165 [0x6289fc] · |m[0].y| − (m[1].y < 0 ? 0.205 · m[1].y : 0)) · dt`, along `m[0]` | no (even in bank: the same nose-up pull either side) |
| `0x48ce3d` | weathervane (player, not crashed, speed > 0) | `return_rate [+0x654] · dt` times the rotation vector from the nose onto `v̂` (`FUN_0053fd40`/`FUN_0053fca0`) | no (the axis is `nose × v̂`, which mirrors with the state) |
| `0x48cf76` | `level_off_rate` auto-level (byte `+0x12c` set, roll and pitch sticks both zero) | `level_off_rate [+0x650] · dt` times the rotation vector from `m[1]` onto world up (`0x6379c0`), times the limiter scalar when it opposes the closing axis | no, and the key is never authored, so the term is zero |
| `FUN_0048c220` at `0x48cf95` (every aircraft but a crashed player) | ground blow | the probe's `A · S` (surface normal × backward axis, `FUN_0048bf60`) scaled by `groundblow_mag` and the command's own projection (player) or by `ai_groundblow · groundblow_mag` (AI), plus the 0.15 factor above `0x71c470` | no (the axis comes from the struck surface) |
| `0x48d158` | stall nose-drop (player, not crashed, past the stall speed) | `stall_mag [0x71c41c] · stallFlag · dt` times the rotation vector from `m[2]` onto world down, with the projection that removes an opposing accumulated component | no (the axis is `nose × down`) |

The accumulator's downstream is as blind. `FUN_0048e580` adds it to `obj+0x160` (`0x48e6ef`),
damps the sum exponentially by `ang_momentum_damp` (`FUN_004606d0`), and scales each body
component by the reciprocal inertia; the only other writers of `obj+0x160` in the program are the
collision deposit in `FUN_0048d7f0` (`0x48e4d3`–`0x48e4d9`, decoded under "Collision response"),
the two reset loops `FUN_00491c60` and `FUN_00491d90` that zero it, and the dead debug integrator
family (`FUN_00491820`, `FUN_00492040`). The throttle `[obj+0x128]` is read exactly once in
`FUN_0048c470`, at `0x48c5a0` for the far-field cruise speed, which is a linear target and not a
torque; the near-field force build reaches the lever only through the thrust curve at `0x48fce7`.
No engine record, prop direction or handedness constant enters any row of the table.

**The stored vector is a quaternion half-angle rate, not physical angular velocity.** After the
body/world transforms, `FUN_0048e580` multiplies it by `dt` and passes the vector to
`FUN_0053fbf0`. That helper returns `(cos |v|, sin |v| · normalize(v))`; `FUN_0053fa40` converts the
quaternion to a matrix, rotating the attitude by **`2|v|`**. The old remake passed `|v|` directly to
Godot's axis-angle rotation and therefore turned pitch, yaw and roll at half strength while every
upstream accumulator value still matched. `FlightAxisReplayTests` independently evaluates all
three axes and has a half-angle able-to-fail control.

Two consequences follow. Every term above is a product of state-derived vectors, so the whole
rotational plant is mirror-symmetric under a left/right reflection of the state and the stick: a
roll or a rudder turn has the same rate in both directions at matched speed, and a centred stick
in level flight produces no rotation at any throttle. And the far-field plant inherits the same
property, since it keeps only the stick rows at authority 1 and the ground blow. The remake carries
the same symmetry and pins it in `CSVM.Tests/EngineTorqueAbsenceTests.cs`: matched full-stick rolls
and rudder turns in both directions at three throttles, a throttle sweep with a centred stick, and a
comparison of idle against full throttle with speed and flight path held (thrust otherwise moves
`v̂`, which the weathervane and the limiter read), with a METHOD-9 control that a 0.28 % one-sided
assist fails the roll pin. The design document's engine torque is unshipped intent, closed per the
parity plan's Decision 4, and any directional rate difference read off footage is a frame
measurement (`docs/verification.md` DET-11, DET-12) against a decoded, symmetric path.

## Bank coupling, resolved, including the inverted case

`FUN_00490f70` adds two more contributions to the same angular accumulator the stick commands
feed, after the three torque blocks and before the player-only weathervane (`0x491621` and
`0x49167b`). Both key off the **orientation matrix at `+0x180`**, whose rows are the body axes in
world coordinates, row 0 = starboard (pitch axis), row 1 = the aircraft's own up (yaw axis),
row 2 = −nose (roll axis), and both are multiplied by `dt` (`0x71c56c`), so each is an angular
**rate** contribution, i.e. an acceleration once the `dt` is divided back out:

```
bank   = m[0][1]                       ; starboard · worldUp  =  sin(bank), + = banked left
wingUp = m[1][1]                       ; bodyUp    · worldUp  =  cos(bank)

ω += 0.205 · bank                        · dt · m[1]          ; yaw  axis, SIGNED by bank
ω += ( 0.165 · |bank|
     + (wingUp < 0 ? −0.205 · wingUp : 0) ) · dt · m[0]        ; pitch axis, always nose-UP
```

⚠ **The inverted term is the SAME 0.205 constant applied to the PITCH axis**, not a third
number, and not an addition to the yaw term. `0x49167b` builds `|bank|·0.165·dt`, compares
`m[1][1]` against `0.0` (`0x6032c8`), and on the `wingUp < 0` branch subtracts
`0.205 · dt · wingUp`, a subtraction of a negative, so it *adds*. It therefore peaks
wings-level **inverted**, where the `0.165` bank term is identically zero: an aeroplane on its
back is pulled toward the ground rather than left to fly hands-off. Both terms vanish exactly at
wings-level upright, so level cruise is untouched by construction.

Consequences worth stating, because each is easy to get backwards:

- The **yaw** term is what drops the nose in a steep bank. At 90° of bank the body up axis is
  horizontal, so a yaw rate is a rotation in the *vertical* plane, this is the fall, and it has
  no equilibrium (nothing opposes it as the nose falls).
- The **pitch** term is what makes the turn. At 90° of bank the body pitch axis is vertical, so a
  nose-up rate is a pure heading change, in the direction of bank.
- Neither is gated by the AOA/G limiters or by any authority curve; they are added raw.

The two constants are **`.data`, not `.rdata`, because the retail build ships a developer console
that writes them**: `fall_off` (`0x622e94`) stores to `0x6289f8` at `0x43e299` and `bank_off`
(`0x622ea0`) stores to `0x6289fc` at `0x43e2dc`, in the same command table as `hide_plane`,
`kill` and `revive`. So they are tunable at runtime but authored nowhere, no data file reaches
them, and 0.205/0.165 are what every shipped flight uses. The whole block is inlined a second
time at `0x48cc61`–`0x48ccf4` on the AI flight path, guarded by a byte flag; identical
arithmetic, recorded for M4.

**C22 landing note, what implementing it settled, and what it did not.** Landed in
`FlightModel.cs` as an addition to the `BodyRates` command, carrying each axis' `RecInertia`
(which the original applies downstream, above) but **not** the remake's `*Tune`, since those
calibrate stick authority against video and are ours. Measured on the Bloodhawk: every
wings-level scenario is unmoved to the last printed digit across all eleven airframes
(`level-top-speed`, `terminal-dive`, `roll-360`, `pitch-rate`, `yaw-360`, `altitude-ceiling`,
`accel`/`decel`, `eighth-throttle`), the coupling cannot reach them, which is the cleanest
possible confirmation of the "vanishes at zero bank" property.
⚠ **It does NOT close the sustained-turn-rate difference, and it moves it the wrong way**: 32.35 →
34.71 °/s against the 18.95 measured off `CAP-01`, and up on ten of eleven airframes. Both terms
*add* heading rate in the direction of bank, so the plan's expectation that this item would explain
the footage's slower banked turn is disproved at the mechanism. **No authored field is a candidate
for the remaining difference either**: `highGs` is measured inert, and `turn_fade_*` is this
document's own base ramp, keyed on airspeed alone and saturated at 1 above 50 mph (see "Control
authority vs speed" and the correction note at the end of this section). The rotation path is
decoded; what disagrees with it is a frame measurement, which does not outrank one.
The one place it clearly improves fidelity is the **knife-edge**: a neutral-stick 90° bank held
for 35 s used to pin the nose at the bounded −4.01° sag and settle (−316 m); it now drifts
linearly at ≈1.08 °/s to −41.8° with no equilibrium (−1993 m), which is the *shape*
`BL-247` measured on the original (0.69–0.89 °/s to −27° over 36 s) and could not previously be
produced at all. Recorded for `D31`, not acted on, the drift is ≈1.2–1.6× the frame-measured rate,
which is a magnitude difference against video where it used to be a missing mechanism.

## Roll to pitch: where the roll command goes, and no pitch write is among its destinations

GDD §4.1.5 gives an aileron roll a "small but noticeable" nose-over. **The shipped executable has
no such term.** The proof here is the other half of the accumulator argument: rather than reading
every write and asking what its sign comes from, it starts at the roll command and follows every
program-wide read of it. The command lives in two slots of the aircraft record, the shaped input
channel `[obj+0x100]` and the clamped stick `[obj+0x114]`, and the pitch axis is reached by
neither.

The two command paths write the three sticks strictly axis by axis. Each stick is
`FUN_00460890(channel, 1.0, 0.5)` of its own channel, and the calls sit in one run: the player's in
`FUN_00487460` at `0x487dab` (roll from `+0x100`), `0x487dc7` (pitch from `+0x108`) and `0x487de3`
(yaw from `+0x10c`), the AI's in the identical shape at `0x41c02f`/`0x41c04b`/`0x41c067`
(`FUN_0041b560`) and `0x420fd8`/`0x420ff4`/`0x421010` (`FUN_004209b0`). The pitch stick `+0x11c`
therefore takes the pitch channel and nothing else on both paths, which is the first place a
command-level coupling could have lived and does not.

Every read of the roll command in the program, with what it reaches:

| Address | Function | What it does with the roll command | Axis reached |
|---|---|---|---|
| `0x48ca7a`, `0x48ca96` | `FUN_0048c470` | `roll_torque [+0x644] · stick · rollAuthority · dt` along the `m[2]` row of `+0x180` | roll only |
| `0x48ce53` | `FUN_0048c470` | a compare against `0.0`, gating the `level_off_rate` auto-level with the pitch stick's own zero test | none |
| `0x48eaf7`, `0x48eb87`, `0x48eb96` | `FUN_0048e580` | the surface targets: `∓0.5 · roll` into the two aileron slots, `∓0.18 · roll` into the two elevator ones | animation only |
| `0x491445`, `0x491461` | `FUN_00490f70` | the same roll torque in the dead debug integrator, whose only caller is `FUN_00491820` | roll only, unreachable |
| `0x48f724`–`0x48f79f` | `FUN_0048f720` | integrates the roll command into the turn-rate slot `+0x13c`, clamped by `+0x17c` | see below |
| `0x490050`, `0x490069`, `0x490091` | `FUN_0048ffe0` | the same integration into `+0x148` | see below |
| `0x487798`–`0x487e23` | `FUN_00487460` | writes the channel and the stick, then snaps the stick to ±1 past a threshold | input |
| `0x49225b`–`0x492b54` | `FUN_00492040` | writes the channel and stick pair in the dead debug family | input |
| `0x4abb13`–`0x4abbaf` | `FUN_004ab550` | poses a node transform, writing only the matrix slots at `[+0xf8]` | none |
| `0x41bff0`–`0x41c034`, `0x41c1de`–`0x41c23b`, `0x41d89a`–`0x41d8c1`, `0x41e509`–`0x41e58b`, `0x420f91`–`0x420fe3` | AI command builders | square the channel, then write the roll stick from it | input |
| `0x4b0603`, `0x4b0621` | `FUN_004aff80` | zeroes both slots on construction | none |

⚠ **`[+0x114]` is two different fields in two different structures.** On the plane RECORD the same
offset is `ang_momentum_damp` (`0x47add3`, and its `5.0` at `0x478bf5`), and `FUN_004d3010` writes
`+0x110` through `+0x134` as a 4×4 matrix. Neither is the stick, and an offset sweep that does not
separate them reports coupling that is not there.

`FUN_0048f720` and `FUN_0048ffe0` belong to the other motion models, not to the aeroplane.
`FUN_00489ea0` switches on `[obj+0x67c]`, a field copied from the vehicle record at `0x475abf`:
0 and 4 select the flight model `FUN_0048e580`, 1 selects `FUN_0048ffe0`, 2 selects `FUN_0048a880`
and 3 and 5 select `FUN_0048b480`. In all three of those the roll command becomes a HEADING rate
(the angle `+0x1fc`, integrated at `0x48b4c6`–`0x48b4cc` and in the same shape in the other two),
the bank angle `+0x200` is a
cosmetic function of turn rate and speed, and the pitch angle `+0x1f8` is either never written or,
in `FUN_0048ffe0`, taken from `atan2` of the velocity components. So even the simplified vehicles
put the roll command on heading rather than on pitch.

**The elevator mixing is animation, and that is measurable rather than assumed.** The six targets
`FUN_0048e580` builds at `0x48eaf0`–`0x48ec8b` are smoothed by `FUN_00460490` into `+0x62c`,
`+0x630`, `+0x634`, `+0x638`, `+0x63c` and `+0x640`. Those six slots are written nowhere else
except the constructors that zero them (`FUN_004aff80`, `FUN_0047f1f0`, `FUN_0047f740`), and the
only reads in the whole program are the three appliers `FUN_004b2f00` (`0x4b2f14`, `0x4b2f45`),
`FUN_004b2f70` (`0x4b2f84`, `0x4b2fb5`) and `FUN_004b2fe0` (`0x4b2ff4`, `0x4b3025`), which pose
nodes. The force build `FUN_0048fc40` and the torque accumulator `FUN_0048c470` read none of them,
and the mixing block runs after `FUN_0048c470` has already returned.

The attitude-driven pitch term at `0x48cd36` is a separate mechanism and stays where it is. It
takes `m[0].y` and `m[1].y`, which are bank and wing-up, so it produces a pitch rate during a roll
with no reference to the stick at all. That is why altitude or ADI movement across a filmed roll
cannot settle this question, and why the bank coupling being ported is not evidence that the GDD's
term shipped.

CSVM carries no roll-to-pitch term and `CSVM.Tests/RollToPitchCouplingTests.cs` pins that it does
not: a held roll stick at three magnitudes, flown wings-level and on the flight path so the bank
coupling and the weathervane read one unchanging state, leaves the pitch component of `BodyRates`
at exactly zero and identical to a centred-stick run, while the roll rate itself is live. The
`METHOD-9` control injects a nose-over at a fiftieth of `pitch_torque` and reads it back off the
accumulator, and a fourth test holds the elevator deflection a roll stick produces beside the
quiet pitch axis in the same state, so the two cannot be confused. The design document's roll-to-
pitch coupling is unshipped intent, closed per the parity plan's Decision 4.

## Weathervane centring, resolved, and the summary line above was wrong

`return_rate` is the last contribution `FUN_00490f70` makes to the angular accumulator, at
`0x4916fe`–`0x4917f0`, immediately after the bank coupling. It is guarded twice:

```
cmp esi, [0x71c298]        ; 0x4916fe — the PLAYER object. AI skips the whole block.
fld [ebp-0x10]; fcomp 0    ; 0x49170a — speed ([obj+0x934], the true |v|) must be > 0
```

**C22 (2026-08-15): the LIVE copy of this guard is `cmp esi, [0x71c298]` at `0x48cd3e` in
`FUN_0048c470`**, jumping past the whole block to `0x48ce45`. It is guarded three times there rather
than twice, player, `[obj+0x384]` (the `fd` developer switch) clear at `0x48cd4a`, and speed > 0,
and the block is `0x48cd3e`–`0x48ce45`, reading `return_rate` from `[obj+0x654]`. The addresses in
the listing above are the debug copy's and remain re-checkable there; this is the one the game runs, and
it is the guard `FlightModel.UsesAiForcePath` now stands for.

Then, in full:

```
n   = −m[2]                                  ; 0x49171e — the three floats at +0x198, sign bit
                                             ;   XORed. m[2] is −nose (thrust uses −m[2] too,
                                             ;   0x48fe91), so n IS THE NOSE.
v̂   = velocity · (1/speed)                   ; 0x491754 — 1/|v| times the vtable's velocity getter
q   = shortestArc(n, v̂)                      ; 0x53fd40 — half-vector quaternion:
                                             ;   ĥ = normalize(n + v̂); q.w = n·ĥ; q.v = n×ĥ
                                             ;   (degenerate n ≈ −v̂ → q.w = 0, q.v = normalize(n))
r   = (atan2(|q.v|, q.w) / |q.v|) · q.v      ; 0x53fca0 — quaternion log: (θ/2)·unit(n × v̂)
ω  += return_rate · dt · r                   ; 0x49179f — [obj+0x654] = ReturnRate, × dt (0x71c56c)
```

so, with α the angle between the nose and the flight path:

```
ω += return_rate · (α/2) · unit(nose × v̂)
```

⚠ **Three corrections to what this document said before.**

1. **The axis is `cross(nose, v̂)`, not `cross(−nose, v̂)`.** The old one-liner transcribed the
   code's `−m[2]` as "−nose", but `m[2]` is itself −nose, the same negation the thrust term
   applies to point along the nose. The sign matters completely: `cross(−nose, v̂)` drives the
   nose *away* from the flight path and is divergent.
2. **The angle is HALVED.** `0x53fca0` is a quaternion→rotation-vector helper and `atan2(|q.v|,
   q.w)` is the half angle; nothing doubles it back. So the effective spring rate is
   `return_rate/2`, not `return_rate`.
3. **It is proportional to the misalignment, and it enters the same accumulator as everything
   else**, so it carries `rec_moments_inertia` downstream and is damped by `ang_momentum_damp`.
   Torque ∝ displacement plus damping ∝ rate is a **spring-damper**: second order, where folding
   `return_rate` into the damping coefficient (what the remake did) is first order.

The axis is perpendicular to the nose by construction, so the term has **no roll component at any
attitude**, a weathervane cannot bank an aeroplane.

**C23 landing note, what implementing it settled, and what it did not.**

- **It moves the pitch transient the right way and does not account for all of it.** The square-wave
  pitch-cadence sweep run through our own build (same input, same estimator, so no transfer function
  is assumed on either side) falls **19.6×** between the 1300 ms and 570 ms cadences before the
  change and **23.3×** after, against the original's **42×**, about a sixth of the gap, on the sim
  reading the sweep then quoted. A second-order response is part of the answer and demonstrably not
  the whole of it; the stick ramp is most of the rest.
- ⚠ **The sweep also refutes a "3.5× steeper than any single first-order lag permits" reading of
  the original's roll-off.** That reasoning assumed the chain is *double integration + one lag*. The
  remake's is not, and never was: the flight path follows the nose through a **second** first-order
  lag (`lift_accel_rate`, then a kinematic chase, now the demand-side lift, the same `ω = rate · α`
  when unclamped), so the earlier build already rolled off 19.6×, 1.65× past
  that "ceiling", with `return_rate` still folded into the damping. The 3.5× figure is therefore
  not a measurement of *our* build's deficit, and the amplitude-for-amplitude comparison above
  replaces it.
- **It reaches two of the three pinned steady rates, and that is not a sign error.** A sustained
  full-stick manoeuvre holds a real misalignment, α ≈ 18° in a full pull, β ≈ 8° on full rudder,
  so the restoring torque opposes the stick there. Un-refit, `pitch-rate` fell 33.47 → 28.35 °/s
  and `yaw-360` rose 28.65 → 34.43 s. `PitchTune` 0.75 → **0.89** and `YawTune` 1.33 → **1.57**
  re-pin them (33.54 °/s, 28.55 s). `RollTune` is untouched: the torque provably cannot reach roll,
  and `roll-360` is 1.98 s before and after on all eleven airframes.
- **It accounts for a little over half of `PitchTune`'s existence.** The un-tuned formula
  `pitch_torque · rec_inertia / ang_momentum_damp` gives the Bloodhawk 44.6 °/s against a measured
  33; `PitchTune` 0.75 was the whole of that gap, and 0.89 is the part the weathervane does not
  explain. It is a smaller fudge than it was, not a retired one.
- **It steepens the knife-edge sag rather than opposing it.** In a sagging knife-edge the flight
  path is *below* the nose, so the weathervane pulls the nose down onto it: the 35 s neutral-stick
  90° hold goes −41.80° → **−44.61°** (−1991 → −2124 m), i.e. ≈1.17 °/s against `BL-247`'s measured
  0.69–0.89. Recorded for `D31`; no knife constant was touched.

## Bank-independent lift vs the measured knife-edge sag, reconciled

The conflict as it stood: the decoded lift demand is projected onto the body **X/Y plane** and its
magnitude is that projection's length, so at 90° of bank the body X axis is vertical and the wings
still carry full weight, the decode says lift does not depend on bank. Against that, the original's
own footage (`CAP-05`, two knife-edge takes at 143 and 300 mph) shows it sagging and eventually
spiralling in. **Both are true, and neither term is where the other one thought it was.**

**The sag is in the NOSE, not in the lift.** At 90° of bank the body yaw axis is horizontal, so the
bank→yaw coupling's `0.205` *is* a nose-sag rate; the weathervane then pulls the nose further down
onto the falling flight path. Neither term is bank-dependent lift and neither was written for the
knife-edge, they are the original's own constants, landed in C22 and C23 for other reasons, and
between them they reproduce the footage's shape: a drift that never finds an equilibrium.

**So the remake's own bounded nose-sag term is retired, and its removal IMPROVES the onset it was
fitted to.** `KnifeNoseSag` (0.07 rad) and `KnifeNoseRate` (0.2 rad/s) were a bounded ≈4° step at a
capped rate, added when nothing else dropped the nose in a knife-edge. Measured against the 143 mph
take, with the throttle trimmed for level flight at the entry speed:

| | original | with the sag term | without it |
|---|---:|---:|---:|
| nose at +3 s | −4.9° | −7.28° | **−4.94°** |
| sink at +3 s (ft/s) | 0.5 | 12.7 | **5.7** |
| nose drift 3→36 s (°/s) | 0.69 | 1.05 | 1.09 |
| altitude lost in 36 s (m) | 540 (in 38.9 s) | 1187 | **1087** |

The whole measured trajectory, from both takes at 143 and 300 mph, they agree to ~13%, so the
knife-edge is driven by time-since-roll-in rather than by airspeed:

| time since roll-in | nose | path | sink |
|---|---|---|---|
| 0–3 s | −4° step, then drifting | ≈0° | **0.5 ft/sim-s, genuinely holds altitude** |
| +12 s | −12.0° | −6.0° | 24 ft/sim-s |
| +24 s | −20.0° | −12.8° | 60 ft/sim-s |
| +36 s | −27.0° | −18.7° | 93 ft/sim-s, still steepening |

The sag is an immediate ≈4° step followed by an unbounded drift of 0.69–0.89 °/sim-s, which is why
the bounded pair is retired rather than retuned: a bounded term cannot produce a drift that never
settles.

⚠ **Do not back an absolute align rate out of this table**, for the retired `KnifeAlignFloor` or for
anything replacing it. The observable here is the path lagging the nose (4.8° at +3 s, 7.2° at +24 s, 8.3° at +36 s), and `CAP-05` cannot separate
that lag from gravity pulling the path down over the same interval. What the A/B above uses is only
the *direction* each row moves under a change on one build, which the confound cannot reverse:
gravity pulling the path down can only shrink the gap, so the inferred chase is an upper bound
either way.

It was also never really a knife-edge term: it keyed on `1 − |bodyUp·up|`, which is 0.29 at a 45°
nose-up attitude with the wings dead level, so it fought every pull at up to 11.5 °/s. Removing it
moves `zoom-climb` toward its measured 936 ft on **all eleven** airframes (Bloodhawk 1396 → 1338 ft,
Balmoral 3935 → 1791) and lets the Balmoral reach the altitude cap at all (4471 → 6572 ft).

**⚠ RETIRED (2026-08-24): `wingVert` is gone, and the reading below is superseded.** It survived on
the argument that the chase rate had no counterpart in the original's force path, so only footage
could speak to it. That argument was wrong: the chase IS in the force path, `lift_accel_rate`,
decoded above, and neither of its two readers scales by bank or verticality. The constant and its
input are removed, and the footage comparison in this paragraph is kept only as the record of what
was believed. Its own numbers: the original holds its nose 4.8° → 8.3°
**below** its flight path across the
36 s, a gap that grows. Ours runs 2.9° → 1.2°; with `wingVert` retired (chase floor 1.0) it
collapses to 1.9° → 0.5° and the 36 s altitude loss rises 1087 → 1334 m. Every knife-edge
observable moves the wrong way without it. Lowering the floor instead of removing it moves every
row toward the footage (at 0.10: gap 3.5° → 2.1°, drift 0.96 °/s, 874 m) and still cannot reach it,
and it walks the knife-edge α up to 5.36°, past `liftAOAs[0] = 5°`, where the airflow blend
starts engaging in a knife-edge.

**What the knife-edge reads at, with the whole chase retired.** With `NoseChaseFactor` at its
decoded 0 (see "`lift_accel_rate` is a lag toward a target velocity") the velocity follows the nose
through the delivered lift alone, so the knife-edge mush is real: α peaks 0.77–5.68° across the
eleven, and on the tightest airframes at the 143 mph entry (Bloodhawk 5.68°, Peacemaker 5.61°,
Fury 5.52°) the airflow blend engages past `liftAOAs[0] = 5°`, which is the original's own
arithmetic at that state and not a defect. The Balmoral peaks 2.28°, so the refuted "0.1° inside
the ramp" figure stays refuted. Drift reads 0.86–1.08 °/s on the filmed airframe against the
filmed 0.69–0.89, the closest it has measured, and the last-third share holds at 0.23–0.30, so the
knife-edge still never settles; `KnifeEdgeTests` pins the drift, the share and the α window on all
eleven. The filmed nose–path gap is discarded and kept as an annotation.

**The max-pull turn runs 1.82× the footage rate, and that is a note, not a gap.**
`sustained-turn-rate` reads 34.5 °/s against `CAP-01`'s 18.95, taken now from the clamped 9 G lift
rather than from a kinematic chase; the knife-edge drift, which used to share the overshoot,
reads 0.86–1.08 °/s against the filmed 0.69–0.89 with the chase retired, so the two manoeuvres no
longer move on one ratio. Every number on the original's side is frame-measured off video; the
rotation that produces our side is decoded from the force path (the torques, the limiters, the
bank coupling, the weathervane and the airspeed authority ramp all sit in this document). A decode
is not corrected by a footage measurement. Nothing is tuned to close it.

⚠ **Do not reintroduce a nose-sag term to deepen the knife-edge.** The decoded bank→yaw coupling
already drops the nose there, and the weathervane then pulls it onto the falling path; a second
nose-down term double-counts what is already present and re-creates the wings-level leak above (the
retired term rotated the nose down at up to 11.5 °/s in a plain 45° pull).
⚠ **Do not reintroduce a kinematic nose-chase, scaled or not, to close the footage difference.**
`KnifeAlignFloor` was a scale on such a chase and the chase itself is now retired
(`NoseChaseFactor` 0, decoded absent): the original rotates the velocity direction only through
the delivered lift and the ground-blow steer. What separates the two sides is the ROTATION rate,
the ≈1.6× above, and a chase constant would hide a rotation rate inside it.

⚠ **CORRECTION (2026-08-09): the authored candidates are exhausted, and this document said
otherwise for four items running.** C22, C23, D31 and D33 each parked this gap on "the unconsumed
`turn_fade_in`/`turn_fade_out`/`highGs` are the only authored fields shaped like it".
Both halves are now false. `highGs` is measured inert on every airframe. And `turn_fade_*` is
**already decoded in this very document**, "Control authority vs speed" above: `FUN_00490e10`'s
base ramp is a function of **airspeed alone**, 0 at `turn_fade_in` (10) rising to 1 at
`turn_fade_out` (50 authored), **held at 1 above that**, scaling roll and pitch authority with no
bank or load-factor term anywhere in it. The banked turn settles at 222–260 mph and the knife-edge
takes are at 143 and 300, so the ramp is saturated across the whole regime where the 1.6× appears
and cannot be its cause. The ramp is a real low-speed behaviour (`BL-330`, implemented 2026-08-15 by
C24), it is simply not this, and landing it moved no row of the envelope suite. **What is left over
is not a decode question**: the rotation path is decoded, and the only thing on the other side of the
comparison is frame-measured video. Recorded rather than quietly re-pointed: a difference that was
attributed to the same three fields four times is exactly the kind of inherited claim that stops
being re-checked.

⚠ **UPDATE (2026-08-15): the "not internally consistent with a coordinated level turn" half of that
paragraph was itself wrong, and `CAP-33` is what corrected it.** The apparent inconsistency was
`CAP-01`'s 58.7° implied bank (`V·ω` = 32.96 m/s² against `nom_gravity`) set against the ~100° its
ADI sky-centroid reads. `CAP-33` was flown with the pilot **holding a known 60–70° of bank** and
reporting it at the controls, which makes the bank independent of any instrument: over that turn the
ADI centroid reads a mean **105.1°** (range 79.5–125.4°) while `V·ω / nom_gravity` reads **62.2°**
(range 56.0–69.9°), both over the turn's 23 one-second bins. The ADI reading tracks the pitch cycle,
not the turn, binned per second, `r(ADI roll, climb rate) = +0.886` against
`r(ADI roll, heading rate) = −0.091`. So the ADI shows airframe attitude, which in a high-α pull is
tens of degrees away from the bank of the turn, and the original **is** flying coordinated closely
enough for the level-turn relation to recover the flown bank. `CAP-01`'s 58.7° was the good number;
its 100° should not be quoted as a bank.
⚠ The bank in `atan(V·ω / g)` takes **`nom_gravity` (20 m/s²)**, not 9.81; Earth gravity gives 73.4°
for `CAP-01` and breaks every comparison above. Full record: `git log --grep=BL-307` (the entry
itself is closed and deleted).

## The three arcade terms

None of these has an aerodynamic justification, and all three distort any model fitted from
observed video.

1. **Thrust varies with nose attitude.** Available thrust is scaled by
   `(1 + 0.24 · a)`, and *additionally* by `(1 + 0.13 · a)` when `a ≤ 0`, where `a` is the
   **world-up component of the body Z axis**, and the nose is **−Z**, so `a` is negative in a
   climb. Climbing loses thrust (0.6612× straight up) and diving gains it (1.24× straight down),
   **on top of** the real gravity term, not instead of it. Two separate coefficients, one of them
   one-sided. Fully recovered under "Attitude thrust, resolved, and the climb it settles" below.
2. **Bank coupling, hardcoded, not data-driven.** Constants **0.205** and **0.165** (at
   `0x6289f8` and `0x6289fc`) convert bank directly into yaw and pitch rate. This is the
   coordinated-turn cheat that makes banking turn the aircraft. Fully recovered under
   "Bank coupling, resolved" above, including the inverted case.
3. **Weathervane centring.** `return_rate` applies a torque along `cross(nose, v̂)`, HALF the
   misalignment angle, pulling the nose onto the velocity vector. **Player aircraft only**, AI
   does not get it (live guard `0x48cd3e`, ported in `C22`). Fully recovered under "Weathervane
   centring, resolved" above, where the `−nose` this line used to read is corrected.

There is also a **boost state**: it **replaces** the throttle multiplier with a flat **1.8** (not a
multiply, `mov [ebp+8], 1.8f` on the boost branch at `0x48fcb6`, where the normal branch loads the
throttle) and multiplies the drag coefficient by **0.8**. So boost is "throttle pinned to 180 %",
and boosting at part throttle is identical to boosting at full throttle.

## Attitude thrust, resolved, and the climb it settles

The two coefficients sit between the throttle multiply and the `ThrustFactor · RefArea` scaling, in
the force accumulator `FUN_0048fc40`. The whole block is eight instructions:

```
0048fce2  call 0x41acf0            ; thrustAvail(mach, atm)
0048fce7  fmul [ebp+8]             ; * throttle (or the boost 1.8)
0048fcea  fld  [esi+0x19c]         ; a = orientation row 2 . Y
0048fcf3  fcomp [0x6032c8]         ; compare a with 0.0, popping it
0048fcfe  je   0x48fd14            ; ZF set = neither C0 nor C3 = a > 0  -> SKIP
0048fd00  fld  [0x6080d8]          ; 0.13
0048fd06  fmul [esi+0x19c]
0048fd0c  fadd [0x6032dc]          ; 1 + 0.13*a
0048fd12  fmulp st(1)              ; avail *= that                       (only when a <= 0)
0048fd14  fld  [0x6080dc]          ; 0.24
0048fd1d  fmul [esi+0x19c]
0048fd30  fadd [0x6032dc]          ; 1 + 0.24*a
0048fd38  fmul st(1)               ; avail * that -> [ebp-8]
```

**The sign is settled from the bytes, not from the coefficient names.** `[esi+0x19c]` is the Y
component of **row 2** of the orientation matrix at `+0x180`, and row 2 is **−nose**, proved at the
point of use rather than assumed: at `0x48fe91` the thrust magnitude is `fchs`'d and *then*
multiplied by that same row (`0x198`/`0x19c`/`0x1a0`) before being added to the force vector, so
`force = −magnitude · row2 = +magnitude · nose`. Therefore `a = −nose.Y`: **negative climbing**, and
both branches reduce thrust there. A vertical climb keeps `(1 − 0.24)(1 − 0.13) = 0.6612`; a
vertical dive gets `1 + 0.24 = 1.24`, the one-sided term not applying. Nothing clamps `a`; it is a
unit-vector component already.

**Gravity is NOT attitude-scaled anywhere.** `0x48ff85`–`0x48ff9d` is the whole of it,
`[obj+0xc4] / 9.82 × [obj+0x674]` subtracted from `force.Y`, with no attitude read in the block.
This matters because the game's own design document describes gravity as pitch-scaled and *reduced
on upward pitch* so climbs stay flyable. The shipped executable does the opposite thing in a
different term: it penalises the climb through thrust. **The GDD is design intent; the binary is
behaviour, and they disagree in sign here.**

### The sustained climb, settled: the plant's climb is 204 mph and the filmed 163 is below its floor

The remake carried a fitted `ClimbGravityScale = 0.6` that spared a climbing aircraft, on the
reading that the original held speed in a climb better than plain energy exchange predicts. It runs
opposite to the decoded terms, so only a measurement could separate them.

The measurement is the original's own sustained full-throttle climb
(`OriginalScreenshots/Videos/Climp 90° 100% Thrust.mp4`, decoded to
`videodata/.../Climp 90° 100% Thrust.mp4.csv`, clip key `climb90`): entry **298.9 mph**, the flight
path settles at **56.3 ± 3.2°**, speed falls to a minimum of **152.4 mph at +6.5 s** and then
**recovers** onto a plateau, 163.05 mph across 12–18 s, still creeping to a flat **167.0 ± 0.5 mph**
by +36 s, climbing ≈12,000 fpm from 900 to 6,300 ft. It leaves that state at ≈6,600 ft, which is
the altitude ceiling (`CAP-03`) and not the climb.

All four arrangements, same build, same probe (`Probes.SustainedClimb`), plateau over 12–18 s
against the footage's **163.05**:

| arrangement | plateau | vs footage |
|---|---:|---:|
| `ClimbGravityScale` only (the pre-change model) | 276.66 | +69.7 % |
| neither mechanism | 257.74 | +58.1 % |
| both mechanisms | 232.20 | +42.4 % |
| attitude terms only (landed) | **204.04** | **+25.1 %** |

**The fitted constant makes the climb worse, and it makes it worse on its own**, removing it alone
moves 276.66 → 257.74 with no attitude term anywhere near it. That is what "it was absorbing the old
drag and thrust shapes' error" looks like from outside: B12/B13 replaced those shapes, and what the
constant was compensating went with them.

⚠ **The residual is real and is recorded, not tuned.** 204 against 163 is +25 %. The *shape*
difference is the entry and not the plant: the clip is a 90° pull from 298.9 mph, so its path swings
past the angle it settles on and the speed undershoots on the way back to it, where the probe sets
56.3° at entry and decays monotonically onto it. The along-path balance at the footage's own plateau
needs a thrust factor of **0.563**, and no attitude in the decoded formula reaches that, 0.6612
being its floor. Do not close the gap by moving 0.24/0.13; they are the binary's, and the dive side
of the same scale lands `terminal-dive` at 356.0 mph against a measured 355.2 ± 6 with nothing
fitted. ⚠ **No capture can settle this and none is owed.** The clip's ADI saturates above ≈+30°, and
a readable one would still only give a frame-derived angle, which cannot confirm a decode
(`docs/verification.md` DET-12).

⚠ **A sustained straight climb has exactly ONE state in this plant, and its α is zero, so no
attitude reading owns the residual.** Compose the near-field accelerations with the lag written out.
Lift is the demand `rate · t · (speed · nose − v) + g_up` projected across the nose, and the world
weight is that same `g_up` with the sign flipped, so the two gravity halves collapse to the demand's
own along-nose share and the whole sum is

```
a = (T − g · nose_y) · nose  −  D · v̂  −  rate · t · v_perp
```

whose component across the flight path is `sin α · [ (T − g · nose_y) + rate · t · speed · cos α ]`.
Straight flight needs that zero. Past the wind blend's 5° edge the lag term alone is 45 m/s² at the
filmed speed, against the 20 m/s² ceiling `nom_gravity` puts on the weight share, so the bracket
cannot vanish; inside the window the lag is zero and the weathervane closes α with the stick
neutral. The probe does not hold the nose on the path either way; the plant walks it there, and the
dump's α column reads 0.00 at every sample. **An instantaneous balance at a 90° nose against a 56°
path is therefore a transient, not a plateau**: the demand that pair carries turns the path onto the
nose at about 25 °/s, so it cannot persist for the twenty-four seconds the footage holds its climb.

⚠ **The residual has no home in the force accumulator, and the two neighbouring rows say the
accumulator is right.** Level flight (`a = 0`) and the 70.7° dive (`a = +0.94`) land on their
decoded targets with nothing fitted; only the climb at `a = −0.83` is 25 % fast. Each candidate that
could move a steady climb is ruled out by arithmetic rather than by preference: α by the derivation
above, the lag rate because it multiplies a vector that is identically zero at α = 0, the throttle
because it is 1 at the filmed full throttle, the band because the whole climb is below the edge, and
`veh_weight` because the weight that balances the climb at the filmed speed is 1.375 × the authored
one and moves `terminal-dive` off its measured 355.2 (weight cancels out of level flight but not out
of either climb or dive).

⚠ **The original spends nothing in a climb outside the force accumulator, and the filmed 163.05 mph
is below the plant's own floor.** The two sections below settle it from opposite directions: a
complete census of every write to the velocity triple, which finds no writer that can run in a
steady climb outside `FUN_0048e580`, and the original's own climb rig, which measures the manoeuvre
through the same `FUN_0048fc40` and reports 170.2 mph with the nose vertical. A climb settles slower
the steeper it is, so 170.2 is the floor of every sustained climb this plant can fly and the footage
sits 4.2 % under it. Run at the footage's own 56.3° path the rig reads **203.9 mph**, against the
free-flight probe's **204.03**, two rigs sharing only the force sum and agreeing to 0.06 %. The
residual is the footage's, and the executable overrules it.

⚠ **The throttle-spending candidate is disproven as well.** The lever is a plain linear multiply on
available thrust and touches no other term ("Part-throttle equilibrium" below), so at the clip's
full throttle it is a factor of 1 and no throttle law can move a full-throttle climb at all.

⚠ **The acceleration-path candidate for this residual is disproven.** The "original spends its lag
vector as the acceleration" hypothesis is settled in "`lift_accel_rate` is a lag toward a target
velocity": the near-field composition is the same demand → clamp → force sum this model flies, and
retiring the remake's extra kinematic chase moved the plateau not at all (204.04 → 204.03), because
a straight climb settles at α = 0, where the chase was a no-op. The throttle spending, the
atmosphere band and α itself are all settled above, and none of them carries the residual.

### Every write to the velocity triple, censused

The velocity lives at `+0x924` with its square at `+0x930` and its magnitude at `+0x934`. Nothing in
the image addresses those bytes with a direct memory operand; every access takes the address first,
at **37 sites**: 35 `LEA <reg>, [<obj> + 0x924]` plus two `ADD ECX, 0x924` inside `FUN_0047c210`
that a search for `LEA` alone misses. Exactly one of the 37 is a pure reader (`0x48b182`, the
speed-squared recomputation inside `FUN_0048ad20`); the rest write.

| group | sites | runs when |
|---|---|---|
| the aeroplane tick `FUN_0048e580` | `0x48e99b` | every frame, classes 0 and 4 |
| the other movement laws: `FUN_0048ffe0` (class 1), `FUN_0048a880`/`FUN_0048ad20` (class 2), `FUN_0048b480` (classes 3 and 5) | `0x490321`, `0x48a991`, `0x48b16b`, `0x48b182`, `0x48b56e` | the class switch in `FUN_00489ea0` picks exactly one |
| the scripted-path follower `FUN_0048a110` | `0x48a4e2` | `[obj+0xcc] != 0`, ahead of the switch |
| the rail mover `FUN_00490590` | `0x490810` | `[obj+0x358] == 5`, chosen ahead of the dispatch in `FUN_004897c0` |
| contact and ground: `FUN_0048d7f0`, `FUN_00486d60`, `FUN_004871e0`, `FUN_0048c220` | `0x48e470`, `0x4870c9`, `0x487219`, `0x48c41e` | a collision or a ground blow |
| spawn and reset: `FUN_0046a490`, `FUN_0047c210`, `FUN_004aff80`, `FUN_00451bf0`, `FUN_00452450`, `FUN_00498170`, `FUN_004b40c0`, `FUN_0041b560` | `0x46ab11`, `0x47d7de`, `0x47d870`, `0x4b033e`, `0x451f90`, `0x45251d`, `0x498499`, `0x4b41d6`, `0x41b7df`, `0x41bb16` | placement, not flight |
| cutscene writers on the player pointer `DAT_0071c298`: `FUN_0047e080`, `FUN_0047f1f0`, `FUN_0047f740`, `FUN_0047fd50` | `0x47e6ef`, `0x47f2b5`, `0x47f324`, `0x47f4ea`, `0x47f829`, `0x480246` | scripted camera work, never with the stick live |
| the Dynamics tuner | `0x491add`, `0x491c80`, `0x491e01`, `0x491f37`, `0x4922fb`, `0x4923ac`, `0x49247e`, `0x49269a`, `0x492a81` | the tuner overlay only |

The dispatch is the whole of it: `FUN_00489ea0` switches on `[obj+0x67c]` and sends classes 0 and 4
to `FUN_0048e580` and to nothing else, the three calls after it being player telemetry writers. So
in a steady climb of a class-0 aeroplane with no contact, no script and no rail, the only writer
that runs is the vehicle tick, and the tick's force sum is the enumerated accumulator.

### The shipped tuner's own climb rig (`FUN_00491c60`)

`FUN_00492040` is the Dynamics overlay the original ships, and its format strings name what each
rig measures: `Max climb 90 degrees: %5.1f MPH` at `0x628bbc`, the 45° climb and the two dives after
it, then `Max turn 100% top speed: %5.1f deg/sec` at `0x628c3c`. The climb and dive rows come from
**`FUN_00491c60`**, called with π/2, π/4, −π/4 and −π/2. `FUN_00491d90` is the **turn** rig, not the
climb one: it pins a 90° bank, holds the along-nose speed at the requested figure by rewriting the
velocity through the body frame each step (`0x491f37`), and reads the world angular rate back out of
`[obj+0x170]`.

What `FUN_00491c60` holds fixed, per step: the attitude, rebuilt from the requested angle alone
(`0x491ce5`); the position, written back to the origin (`0x491cf2`), so the whole sweep is a
**sea-level** measurement; the angular momentum, zeroed (`0x491d40`), so the aircraft cannot rotate
out of the attitude; the stall flag at `[obj+0xf4]`, cleared (`0x491d2a`). The throttle is pinned to
1.0 once at entry (`0x491c69`). What it leaves free is the velocity, zeroed once at `0x491c80` and
then owned by the integrator. What it reads back is `[obj+0x934]`, compared against its value before
the step; the rig stops at the first step that gains no speed, up to 10000 steps at the 0.01 s step
`FUN_00492040` forces. So **the original's own sustained climb is an equilibrium of the force sum
and nothing else**: no entry transient, no path angle distinct from the nose, no memory.

Its force path is `FUN_00491820` → `FUN_00490f70` → `FUN_0048fc40`, the same accumulator the live
tick reaches at `0x48c883`. A term the game spent in a climb but kept out of the accumulator would
be invisible to the tool the airframes were tuned with, so there is no such term.

⚠ **The tuner's numbers are sea-level numbers by construction, and the live tick's are not.**
`FUN_00490f70` saves `[obj+0x208]` (the world Y), writes zero into it at `0x491270`, calls the
accumulator at `0x49127a` and restores it at `0x491284`. `FUN_0048c470` does no such thing. Do not
read a tuner figure as an in-flight one at altitude.

`Probes.TunerEnvelope` runs this rig against the ported plant and `--dump-flight` prints it beside
the free-flight climb. For the Bloodhawk: 170.2 mph vertical, 203.9 at the filmed 56.3°, 223.7 at
45°, 343.8 and 358.8 mph in the two dives.

## Thrust available, resolved, `pow` operands recovered

`FUN_0041acf0` is `__cdecl float thrustAvail(float mach, Atmos *atm)`, two stack args, result in
`st(0)`, caller cleans (`add esp, 8` at `0x48fcf0`). Mach is floored at **0.1** and the clamp is
written back into the argument slot (`0x41ad02`), so every use below is of the floored value:

```
V_ref = (0.84·M + 0.112) · a                       ; a = atm->a, ft/s
q_ref = 0.5 · ρ · V_ref²                           ; FUN_0041ac80(atm, V_ref)
T     = q_ref · 0.73 · (0.12 − M/60) / ( M · pow(1.33 · atm->k, 1.41 · M) )
```

**The `pow` operands are now recovered from the raw bytes**, the decompiler had lost them behind
`call 0x5f7016`, a one-instruction thunk `jmp dword ptr [0xa20358]` that the import table resolves
to `MSVCRT!_CIpow` (base in `st(1)`, exponent in `st(0)`, pushed in that order at `0x41ad0f` /
`0x41ad18`):

- **base** = `atm->k · 1.33`, a constant per atmosphere state, **1.3146** on the dense band
- **exponent** = `Mach · 1.41`

⚠ **The base takes the BAND's `k`, which makes it the atmosphere step's third output.** `atm->k` is
the same slot the speed of sound is built from (`a = (k + 1) · 558`), so it switches with density
and sound speed at the 2000 m edge: 1.33 × 0.98842078 = **1.3146** dense, 1.33 × 0.7348 =
**0.97728** thin. The sign of the exponent's effect flips with it. Below the edge the base exceeds
1 and the divisor grows with Mach; above it the base is under 1 and the divisor shrinks, so holding
the dense base everywhere understates thin-band thrust by `(0.97728 / 1.3146)^(1.41·M)`, which is
10 % at the Bloodhawk's crossing Mach and more the faster the aircraft is. `FlightModel` carries
both bases and `AtmosphereBandTests` pins the selection.

Every float immediate, all `.rdata`:

| Address | Value | Role |
|---|---|---|
| `0x6034a8` | 0.1 | Mach floor |
| `0x6034a4` | 1.33 | × `atm->k` → `pow` base |
| `0x6034a0` | 1.41 | × Mach → `pow` exponent |
| `0x60349c` | 0.84 | `V_ref` slope |
| `0x603498` | 0.112 | `V_ref` intercept, in Mach |
| `0x603494` | 1/60 | Mach trim on the parasite term |
| `0x603490` | 0.12 | parasite coefficient |
| `0x603474` | 0.73 | the same drag scale the polar uses |

⚠ **The curve RISES with speed, it is not `T = P/V`.** The `/M` is real, but the numerator is `q`
at a reference speed that **tracks the current speed** (`V_ref = (0.84M + 0.112)·a`) rather than
sitting at a fixed design point, so `q_ref ∝ M²` and the net is roughly *linear* in Mach: +35 %
between 150 and 500 mph on the dense band. Read it as "the parasite drag force the airframe would
feel at `V_ref`, divided by Mach". What falls with speed in this model is the thrust **margin**,
because drag grows as `M²` where thrust grows as `M`. Anyone re-deriving this from the `/M` alone
will predict a falling curve and be wrong.

The atmosphere initialiser `FUN_0041aca0(alt_ft, atm)`, called at `0x48fc70` with
`alt_ft = position.y · 3.28084`, is what supplies `atm->k`:

```
if (alt_ft <= *(float*)0x71bb3c) { r = 0.9544815;  k = 0.98842078; }   ; dense
else                             { r = 0.05704810; k = 0.73480000; }   ; thin
atm->a   = (k + 1.0) · 558.0        ; 1109.54 ft/s dense, 968.02 thin
atm->rho = r · 0.002377             ; 2.2688e-3 dense, 1.35603e-4 thin
```

The band this resolves to is the dense one everywhere below 2000 m: `0x71bb3c` is a BSS slot whose
live value is 6561.6796875 ft, written through a base register by the reset routine
`FUN_00463640`, so a listing that shows `0.0` is showing the zero fill and not a threshold. See
the Atmosphere section. The arithmetic corroborates it here: with the dense band's `k`, the
decoded thrust curve and the decoded drag polar put the Bloodhawk's full-throttle level
equilibrium at **300.5 mph** against its authored `fd_speed` of 302.0 and its measured 300.4, with
no fitted constant anywhere. The thin band would miss by a factor of ~4.

**This also settles the `fd_speed` caveat below, in `fd_speed`'s favour** for the Bloodhawk: with
`ThrustFactor = EnginePower` and thrust scaled by `RefArea`, the level equilibrium lands on
`fd_speed` to 0.2 %. The equilibrium is independent of ρ and `a` entirely, both cancel, and
reduces to `EnginePower/DragFactor`:

```
EnginePower · (0.84M + 0.112)² · (0.12 − M/60) / (M³ · pow(1.33k, 1.41M))
    = DragFactor · (0.12 + 0.8M + 0.5M²)
```

The Balmoral still misses (it solves at ≈ 0.71 × its own `fd_speed`), so the caveat stands for the
rest of the set, but the "`fd_speed` is a pure normalising reference" reading is now the weaker
one.

## `ThrustFactor` is the engine's power factor, resolved

The tuner's header lists **13** authored fields but
[vehicle.md](../formats/vehicle.md) documents only **12** `dynamics` keys. The extra one is
`ThrustFactor`, and it is **not** an authored key: it is the `power` column of `engines.json`, for
the row the def's `engine` property selects. Traced end to end.

**1, the assembly.** In the force accumulator (`FUN_0048fc40`), thrust force is

```
mult   = boost ? 1.8 : throttle                     ; [obj+0x128], boost flag [obj+0x947]
avail  = thrustAvailable(Mach) · mult               ; FUN_0041acf0, call at 0x48fce2
avail *= attitude terms (0.24 / 0.13)               ; the arcade term above
if engine destroyed: avail = 0                      ; flag [obj+0x2dc]
Thrust = ThrustFactor · RefArea · avail             ; 0x48fde1: fld [obj+0x66c] · [obj+0x678]
```

⚠ **Thrust scales with `ref_area`, not with `1/veh_weight`.** That is what makes the thrust/drag
balance dimensionally consistent, drag is `q · RefArea · DragFactor · C_D`, so `RefArea` cancels
out of the equilibrium and the top speed depends only on `ThrustFactor / DragFactor` (and weight
through `C_L`). `FlightModel.ThrustAccelAt` carries the same arrangement:
`EnginePower · RefArea · curve(Mach) · lever`, converted to an acceleration by the force path's
own `× 9.82 / VehWeight`.

**2, the struct slot.** The plane *definition* struct carries the whole `dynamics` block at
`+0x100 … +0x134`, and the runtime flight object mirrors it at `+0x644 … +0x678`:

| Def | Runtime | Tuner column | Parser token |
|---|---|---|---|
| `+0x100` | `+0x644` | `RollTorque` | `roll_torque` |
| `+0x104` | `+0x648` | `PitchTorque` | `pitch_torque` |
| `+0x108` | `+0x64c` | `YawTorque` | `rudder_torque` |
| `+0x10c` | - | - | `level_off_rate` (accepted, **never authored**) |
| `+0x110` | `+0x654` | `ReturnRate` | `return_rate` |
| `+0x114` | `+0x658` | `DampingRate` | `ang_momentum_damp` |
| `+0x118`/`+0x11c`/`+0x120` | `+0x65c`/`+0x660`/`+0x664` | `RecMomentsInertiaX/Y/Z` | `rec_moments_inertia` |
| `+0x124` | `+0x668` | `FakeDynSpeed` | `fd_speed` |
| **`+0x128`** | **`+0x66c`** | **`ThrustFactor`** | **none, written from `engines.json`** |
| `+0x12c` | `+0x670` | `DragFactor` | `drag_factor` |
| `+0x130` | `+0x674` | `Weight` | `veh_weight` |
| `+0x134` | `+0x678` | `RefArea` | `ref_area` |

The def→runtime copy is the straight-line block at `0x475c40`–`0x475c76`, which fixes the mapping
without ambiguity. The tuner (`FUN_00445090`) writes its 13 dialog floats back to `+0x644 … +0x678`
in exactly this order, which is how the header names line up with the slots.

**3, who writes `+0x128`.** The vehicle-def parser (`FUN_00479240`) handles the def-level `engine`
key at `0x47acc0`: it looks the id (or name) up in the `engines.zrd` table, loaded by `0x449040`
into 0x18-byte records, searched by id at `0x449140` and by name at `0x449160`, reads the record's
`+0x14` float through the accessor `FUN_004861f0` (`fld [ecx+0x14]; ret`), and stores it with
`fstp [def+0x128]` at `0x47ad01`. Record `+0x14` is the third JSON column and defaults to `1.0`
(`0x4490a4`), i.e. **`power`**. The hangar's engine-swap path does the same thing directly to the
live object: `fld [rec+0x14]; fstp [player+0x66c]` at `0x43e97b`. Nothing else ever writes the
slot except the per-spawn AI spread below, which multiplies it by a random `1 ± 5 %`.

**4, no thrust key exists in the data.** The vehicle parser's token table
(`0x627f94`–`0x628020`) contains eleven `dynamics` tokens: `pitch_torque`, `roll_torque`,
`rudder_torque`, `level_off_rate`, `return_rate`, `ang_momentum_damp`, `fd_speed`, `drag_factor`,
`veh_weight`, `ref_area`, `rec_moments_inertia`. There is no thrust token, and the literal
`ThrustFactor` occurs exactly once in the whole 2.5 MB image, inside the tuner's CSV header.
A census of the shipped `vehicle.json` agrees: **24 defs carry a `dynamics` block, every one of
them authors the same ten keys, and none authors an eleventh.** (`level_off_rate` is the mirror
case, a token the parser accepts that the shipped data never uses.) So there is no inheritance
chain to walk: `kind_of` never has to supply a thrust value because no def has one.

**5, the data agrees, across all eleven airframes.** Independent of the binary: at a common speed
`RefArea` cancels and the level-flight equilibrium is set by `ThrustFactor / (DragFactor · C_D)`.
Ranking the eleven player airframes by that index against their authored `fd_speed` gives a
**Spearman ρ of exactly +1.000** when `ThrustFactor` is the stock engine's power. The two
alternatives fail: a uniform `ThrustFactor` of 1 gives ρ = +0.90, and one proportional to weight
gives ρ = +0.77.

| Airframe | Stock engine (id) | `power` | `drag_factor` | `fd_speed` | index `p/(df·C_D)` | null index |
|---|---|---|---|---|---|---|
| `pbloodhawk` | Bloodhawk Lvl-2 (11) | 0.62 | 0.37 | 302.0 mph | 15.21 | 24.53 |
| `ppeacemaker` | Peacemaker Lvl-2 (14) | 0.58 | 0.38 | 290.8 | 14.07 | 24.26 |
| `pfury` | Fury Lvl-2 (17) | 0.59 | 0.42 | 281.9 | 12.94 | 21.93 |
| `pavenger` | Hellhound Lvl-2 (20) | 0.64 | 0.55 | 264.0 | 10.54 | 16.47 |
| `pdevastator` | Devastator Lvl-2 (23) | 0.65 | 0.62 | 252.8 | 9.59 | 14.76 |
| `pbrigand` | Brigand Lvl-2 (26) | 0.68 | 0.73 | 241.6 | 8.43 | 12.39 |
| `pautogyro` | Hoplite gyro Lvl-2 (38) | 0.35 | 0.50 | 228.2 | 7.78 | **22.22** |
| `pkestrel` | Kestrel Lvl-2 (29) | 0.90 | 1.28 | 217.0 | 6.34 | 7.05 |
| `pfirebrand` | Firebrand Lvl-2 (32) | 1.00 | 1.58 | 208.0 | 5.91 | 5.91 |
| `pwarhawk` | Warhawk Lvl-2 (35) | 1.00 | 1.70 | 201.3 | 5.38 | 5.38 |
| `pbalmoral` | Balmoral bomber Lvl-2 (41) | 0.30 | 1.70 | 176.7 | 1.73 | 5.76 |

The **Hoplite autogyro is the discriminating case**: it has the fourth-lowest `drag_factor` in the
set but the fifth-*lowest* `fd_speed`, so drag alone ranks it third-fastest when it is authored
seventh. Only its engine, 0.35, the second-weakest in the game, puts it where the data says it
belongs. The Balmoral is the second discriminator at the other end.

Note that **every player airframe's stock engine is its Lvl-2 row**, not Lvl-1: ids 11, 14, 17, 20,
23, 26, 29, 32, 35, 38, 41. Solving any constant from a Lvl-1 row inflates it by ~30 %.

⚠ **A caveat, now largely retired, read the Drag section's solve table first.** With the drag
polar corrected to Mach and the thrust `pow` recovered, the absolute test below *does* close: nine
of eleven airframes solve to within 1 % of their own `fd_speed`. What follows is the state of the
question before those two corrections, kept because the Balmoral (0.71×) and the autogyro (0.94×)
still miss and the residual is unexplained.

The table above is a *rank* test, and it is
clean. The absolute test is not: assuming `fd_speed` is the full-throttle level equilibrium and
solving `power · T_avail(Mach) = q · DragFactor · C_D` at each airframe's own `fd_speed` should
give eleven samples of one smooth curve, and it does not, a log-log fit leaves ±18 % residuals
across most of the set and the Balmoral misses by **+60 %**. Either the unrecovered `pow` term has
strong curvature, or `fd_speed` is not the equilibrium. The second reading has support: the tuner
calls the field **`FakeDynSpeed`** and *measures* `TopSpeed` separately (if `fd_speed` were the top
speed there would be nothing to measure), and at runtime `fd_speed` is used as a **normalising
reference speed**, `speed/fd_speed` for gauge and effect fractions (`0x4b1e54`), a far-field
cruise speed `fd_speed · throttle` (`0x48c593`, A2 below; this used to read "an AI target speed",
which it is not), and an **initial** velocity for a spawning AI aircraft (`0x46aaf4`, the
mission-event "move object to node" action; ⚠ this used to read "a speed clamp", which it is not,
and it is the same `min(plane_speed_max, fd_speed)` rule the vehicle factory applies, item 13
above), never as a solved equilibrium. **Do not treat `fd_speed` as the original's top speed in
B12/B13 without settling this.**

**Bonus, and load-bearing for B13:** `[obj+0x124]` and `[obj+0x128]` are the **commanded** and
**current throttle**, `FUN_0048e585` rate-limits the current toward the commanded and burns fuel
at `[obj+0x134] -= dt · throttle · 5`, and `FUN_00491820` snaps them together. The current throttle
enters thrust as a **plain multiply** at `0x48fce7`. That is the linear-throttle claim, confirmed
at source in this item rather than inferred.

### A2 (2026-08-15): throttle is a lever for the AI too, and `fd_speed · throttle` belongs to a far-field model

**The throttle interface has no AI branch.** `FUN_0048fc40` reads the current throttle
`[obj+0x128]` and multiplies the Mach thrust curve by it (`0x48fcc6` loads it, `0x48fce7`
multiplies), with no player compare anywhere in the chain; boost (`[obj+0x947]`) replaces the lever
with a flat `1.8` and scales drag by `0.8` (`0x48fcb6`–`0x48fcbd`). The commanded/current pair
`[obj+0x124]`/`[obj+0x128]` and the 0.5/s slew below are likewise unbranched, in the **live**
integrator `FUN_0048e580` (`0x48e645`–`0x48e6bd`), so an AI aircraft's throttle moves under exactly
the same rate limit as the player's. The player's commanded value is written by the input handler
`FUN_00487460`; AI commanded values are written at spawn/placement (`FUN_0047f1f0` writes `0.4` at
`0x47f28b` and `1.0` at `0x47f3fb`). **`AiPilot.Throttle` as a 0..1 lever is the right shape; the
original has no AI speed-setpoint interface.**

**What `fd_speed · throttle` at `0x48c593` actually is: the far-field aircraft model.**
`FUN_0048c470` opens with a guard that skips the whole aerodynamic path when the aircraft carries
the `fd` developer switch (`[obj+0x384]`), **or** when it is not the player and its **horizontal**
distance from the player exceeds 1000 m (`FUN_00538920` returns `Δx² + Δz²`, compared against
`1e6`). In that branch the aircraft's velocity is driven toward the nose axis at `fd_speed · throttle`
(`0x48c593`–`0x48c5a0`), plus a flat **5 m/s** for anything that is not the player
(`0x48c5ae`, `[0x6036bc] = 5.0`), and the function's linear-acceleration output is set to
`target − current` velocity rather than to a force. So distant traffic cruises along its nose at a
speed the data sets, with no lift, drag or thrust computed at all.

This is a level-of-detail model, not the AI's control interface: it is keyed on distance from the
player and applies to the player's own aircraft only under the `fd` developer switch, which no
gameplay event sets. CSVM flies it, measured
against the nearest human pilot rather than a single player; the branch, the terms it skips and the
terms it keeps are in "The far-field plant" above.

**The throttle slews at 0.5/s, with no idle floor** (`0x48e652`/`0x48e698`: current ±= `0.5 · dt`
toward commanded, snapping exactly onto it when the step crosses). Cutting from full to zero takes
2 s of tapering thrust; slamming open takes the same. `FlightController` now applies the live-lever
slew to AI commands as well as keyboard commands, so a carrier release's 0.1 seed survives the AI's
first desired-full-throttle update.

## Part-throttle equilibrium, the decoded curve

**The lever enters the live force path exactly once.** Every read of the current throttle
`[obj+0x128]` inside the live chain (`FUN_0048e580` → `FUN_0048c470` →
`FUN_0048bdd0` / `FUN_0048fc40` / `FUN_0048c220`, with `FUN_0048d7f0` and `FUN_0048d2c0` on the
contact side) is one of these five, and only the first is a force:

| Address | Function | What the lever does there |
|---|---|---|
| `0x48fcc6` → `0x48fce7` | `FUN_0048fc40` | copied into the local multiplier slot, then `avail *= lever` on the thrust curve |
| `0x48c5a0` | `FUN_0048c470` | the far-field branch's cruise speed `throttle · fd_speed` |
| `0x48e59b` | `FUN_0048e580` | `commanded − current`, passed to `FUN_004afbc0` for each entry of the list at `+0x2b0`/`+0x2b4` |
| `0x48e603` | `FUN_0048e580` | fuel burn, `[obj+0x134] −= dt · throttle · 5`, player-only |
| `0x48e63f`–`0x48e6c3` | `FUN_0048e580` | the 0.5/s slew of current toward commanded |

Nothing else in the chain reads it. The fuel test at `0x48e5ec` guards more than the burn: a player
whose `[obj+0x134]` has reached zero jumps past the slew as well (`0x48e5f7` to `0x48e6c9`), so an
empty tank freezes the lever where it stands rather than closing it. CSVM flies both, on the human
lever path only, in `FuelTank` and `FlightController.ReadKeyboard`. The lever reaches drag only
through the boost flag
(`[obj+0x947]`), which does not scale the lever but **replaces** it with a flat 1.8 while setting
the drag multiplier `[ebp−0xc]` to 0.8 (`0x48fcb6`–`0x48fcbd`, against 1.0 on the normal branch at
`0x48fccf`). Lift (`FUN_0041abd0`), the weathervane, ground blow and the collision impulse carry no
throttle input at all. At a fixed lever the plant is therefore the full-throttle plant with exactly
one term scaled, which is what makes the equilibrium solvable in closed form.

**Where the tank comes from.** The aircraft carries a capacity at `[obj+0x130]` beside the
remaining fuel at `[obj+0x134]`, and the object initialiser `FUN_004aff80` zeroes both
(`0x4b064b`, `0x4b0651`). The capacity is copied off the def record at `0x475ca6`, from `[def+0x154]`,
the slot the vehicle-def parser fills from the `fuel` key (`0x47a7d7`, key string at `0x627f3c`, one
slot along from `nitro` at `+0x150`). The compiled default is 0 (`0x478c57`, with `EBX` cleared at
`0x478a1d`), and exactly one shipped def authors the key: `player_airplane`, at **54926**, which every
`player_*` airframe inherits through `kind_of`. No AI def authors it, which costs nothing because the
burn is player-only. The refill is at placement: `FUN_0047f1f0` (`0x47f6a7`) and `FUN_0047f740`
(`0x47fa56`) both write `[player+0x134] = [player+0x130]`. At a fully open lever 54926 units is
about three hours of flying, which is why nothing shipped runs a tank dry.

**The level balance.** In steady level flight at zero incidence the wings carry the weight, the
attitude scale is exactly 1 and thrust opposes drag along the path, so with the thrust curve and
the Mach polar written out, `RefArea`, `0.73` and the whole `½ρa²` in front of both sides cancel:

```
lever(M) = DragFactor · M³ · (0.12 + 0.8M + 0.5M²) · (1.33k)^(1.41M)
           ────────────────────────────────────────────────────────
                 ThrustFactor · (0.84M + 0.112)² · (0.12 − M/60)
```

Below the thrust curve's Mach floor the numerator loses one power of `M`, because the curve is then
evaluated at a fixed `M = 0.1` while drag still reads the true Mach:

```
lever(M < 0.1) = DragFactor · M² · (0.12 + 0.8M + 0.5M²) · 0.1 · (1.33k)^0.141
                 ───────────────────────────────────────────────────────────
                        ThrustFactor · (0.196)² · (0.12 − 0.1/60)
```

The floor is written back into `thrustAvail`'s own argument slot (`0x41ad02`), a copy of the value
the caller pushed at `0x48fce1`, and the drag call at `0x48fd76` re-reads the unfloored Mach from
`0x71c554`. Drag is never floored.

Three properties follow directly. The curve is **strictly increasing in Mach** in both regimes and
continuous at the join, so it inverts to one equilibrium speed per lever and the lever is a
monotone speed control. The airframe enters **only through `ThrustFactor / DragFactor`**: two
airframes sharing that ratio hold the same Mach at the same lever whatever their weight or wing
area. Air density and the speed of sound cancel out of the balance entirely, so the equilibrium is
fixed in Mach and only its conversion to a speed reads the atmosphere.

Inverting the curve on the dense band, per airframe, in mph:

| Airframe | 1/8 | 1/4 | 1/2 | 3/4 | full | `fd_speed` | level floor | floor lever |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `pbloodhawk` | 134.5 | 176.1 | 230.4 | 269.3 | 300.5 | 302.0 | 80.9 | 0.033 |
| `ppeacemaker` | 129.7 | 169.8 | 222.2 | 259.8 | 290.0 | 290.8 | 77.8 | 0.033 |
| `pfury` | 125.6 | 164.4 | 215.2 | 251.7 | 281.0 | 281.9 | 78.0 | 0.036 |
| `pavenger` | 116.8 | 152.8 | 200.1 | 234.1 | 261.4 | 264.0 | 81.3 | 0.049 |
| `pdevastator` | 112.2 | 146.8 | 192.1 | 224.8 | 251.2 | 252.8 | 79.3 | 0.051 |
| `pbrigand` | 107.2 | 140.2 | 183.5 | 214.8 | 240.0 | 241.6 | 81.6 | 0.061 |
| `pautogyro` | 96.0 | 125.5 | 164.2 | 192.3 | 214.9 | 228.2 | 26.5 | 0.006 |
| `pkestrel` | 96.2 | 125.7 | 164.5 | 192.6 | 215.3 | 217.0 | 82.1 | 0.083 |
| `pfirebrand` | 92.4 | 120.7 | 157.9 | 184.9 | 206.7 | 208.0 | 75.1 | 0.073 |
| `pwarhawk` | 89.8 | 117.3 | 153.5 | 179.7 | 200.9 | 201.3 | 79.5 | 0.091 |
| `pbalmoral` | *55.2* | 73.8 | 96.3 | 112.6 | 125.9 | 176.7 | 65.2 | 0.185 |

The full-throttle column is the same solve the Drag section publishes, which is the cross-check
that this form is that one with a lever added. The remake's plant flies to every cell of the table:
the eleven-airframe dump's `level-top-speed` and `eighth-throttle-speed` rows agree with it to the
0.1 mph printed here on all eleven except the Balmoral's 1/8, and `PartThrottleEquilibriumTests` asserts
the whole eight-lever sweep from both sides at 0.5 %. **No code changed for this item.** The
remake already spends the lever as a plain multiply on `ThrustAccelAt`, which is what the decode
says the original does.

⚠ **The curve is a LEVEL-flight solution, and it runs out at the bottom.** Level flight also needs
the wings to carry `nom_gravity`, so the solution is reachable only above the speed where the
aerodynamic ceiling delivers `nom_gravity / 9.82` G, which is `√(nom_gravity/9.82) = 1.427` times
the 1 G stall speed at this install's authored 20 m/s². That is the "level floor" column, and its
"floor lever" is the smallest lever whose solution clears it. The Balmoral's 1/8 is the only stock
combination below its own floor (55.2 mph solved against a 65.2 mph floor, italicised above): the
plant cannot hold it level, and settles instead into a descent that trades height for the missing
thrust. **A settled descent is not a second equilibrium curve.** Its path angle is not fixed by
statics, because lift matching `g · cos γ` leaves the along-path balance one equation short; the
angle the run reaches comes from its own transient, so no number from that state may be quoted as
a decoded target.

The far-field branch has its own, unrelated throttle equilibrium: `throttle · fd_speed` along the
nose, plus 5 m/s for anything that is not the player, reached as a rate rather than a force
(`0x48c593`–`0x48c5ae`). It is a different plant, ported and documented under "The far-field
plant".

**This closes the eighth-throttle row's target question.** `Probes.eighth-throttle-speed` reports
134.52 mph for the Bloodhawk against the 134.5 solved here, and both of the numbers that were
previously proposed for the row (137.9 mph and a later ≈135) came off video. The row stays
informational because its "original" column is reserved for footage, and the decoded target is
asserted in the test suite instead.

⚠ **The sustained-climb residual is NOT owned by throttle spending, and this is a disproof rather
than a fix.** The lever multiplies available thrust linearly and nothing else, so at the filmed
clip's full throttle it contributes a factor of exactly 1: **no throttle law of any shape can
change a full-throttle climb**, because every candidate is 1 at the top of its own range. The
along-path balance at the footage's own 163.05 mph plateau and 56.3° path needs 0.567 of the
decoded thrust, and the two decoded terms that can supply it are the attitude scale (0.6612 at its
floor) and the nose-to-path cosine, whose product at a 90° nose is 0.550. The remaining owner is
therefore the α the original's climb path holds, exactly as the climb section states, and with
`A4` settling the band on the dense side, no owner outside that one is left standing.

## The per-spawn jitter, eleven slots, non-player aircraft only (IMPLEMENTED)

At the tail of the spawn/reset function `FUN_00476250` (`0x477340`–`0x4773f0`) sits a block that
gives every non-player aircraft its own slightly-different airframe. It runs only when all three
of these hold:

- the object's own name (`obj+0xc`) is not `"player"`, a 7-byte compare at `0x477326`;
- `FUN_00440ad0()` returns 0. That function is `return *DAT_0064f750`, the first dword of the
  **`"Network"` subsystem object** looked up at `0x44023d` and stored at `0x440247`
  (`docs/org/aim-assist.md` names the same flag), i.e. **not a network game**;
- the vehicle class `obj+0x67c` is **0 or 1**. That field is the def's `mode` key, resolved by the
  parser's own string table at `0x47afc0`–`0x47b081` (`0x62808c` onward): **`jet` = 0, `heli` = 1,
  `tank` = 2, `ship` = 3, `wingman` = 4, `plane` = 5**, and it is copied to the runtime object at
  `0x475abf` from def `+0xa4`. So the jitter reaches aeroplanes and autogyros and skips ships,
  ground vehicles, and **the shipped `w*` wingman family**, which authors `mode wingman` (class 4)
  and so flies unjittered even though the dispatch flies it down the same aeroplane arm as class 0.
  Everything else in the shipped file inherits `basic_airplane`'s `jet`; only `basic_airplane`,
  `patrolboat`, `t_truck` and the eleven `w*`/`wingman`/`bswingman` defs author `mode` at all.

Then, for each of **eleven** runtime slots in this order, it draws `rand()` and multiplies the slot
in place by `((2r − 1) · 0.05 + 1)` where `r = rand() · 3.051851e-05` (`1/32768`): an independent
uniform **1 ± 5 %** per slot, eleven separate draws.

| # | Runtime | Def | What it is |
|---|---|---|---|
| 1 | `+0x2cc` → mirrored to `+0x2d0` | - | whole-vehicle health max (and its "current" mirror) |
| 2 | `+0x2c4` → mirrored to `+0x2c8` | - | whole-vehicle armour max (and its "current" mirror) |
| 3 | `+0x668` | `+0x124` | `fd_speed` |
| 4 | `+0x66c` | `+0x128` | `ThrustFactor` (the stock engine's `power`) |
| 5 | `+0x670` | `+0x12c` | `drag_factor` |
| 6 | `+0x648` | `+0x104` | `pitch_torque` |
| 7 | `+0x644` | `+0x100` | `roll_torque` |
| 8 | `+0x680` | `+0xe8` | `rates[0]` |
| 9 | `+0x684` | `+0xec` | `rates[1]` |
| 10 | `+0x688` | `+0xf8` | `turns[0]` |
| 11 | `+0x68c` | `+0xfc` | `turns[1]` |

⚠ **`veh_weight` (`+0x674`) and `ref_area` (`+0x678`) are NOT in the list**, which is why nothing
computed once from that pair (stall speed) can go stale behind the jitter.

⚠ **The per-part damage pools are not touched either**, only the whole-vehicle pair is, and it is
the pair the kill test reads (`docs/org/vehicleDamage.md`).

**Slots 8–11 are inert on an aeroplane.** `rates` and `turns` are the surface-driving integrator's
acceleration and steering rates with their clamps: `FUN_0048f7d0` accelerates toward the throttle
input at `rates[0]` and clamps to `+0x178` (seeded from `rates[1]`), `FUN_0048f720` does the same
for steering at `turns[0]` against `+0x17c`. Both are reached only from the class-2 (`FUN_0048a880`)
and class-3/5 (`FUN_0048b480`) arms of the class dispatch `FUN_00489ea0`, plus the class-1 autogyro
arm `FUN_0048ffe0` which reads `+0x680`/`+0x684`/`+0x688` directly. The aeroplane arm
`FUN_0048e580` only copies `+0x684` into `+0x178` at `0x48e8bc` and never reads it back. The keys
are nonetheless authored on `basic_airplane` (`rates` 10/42, `turns` 4.6/6.5) and so every aircraft
carries them; the shipped data authors them on exactly three defs, `basic_airplane`, `patrolboat`
and `t_truck`.

**In the remake:** `PlaneStats.WithAiSpawnJitter`, applied at `FlightRoster.SpawnAi` to a
COPY of the session's shared per-airframe stats. The name test becomes `IsHumanPiloted` (C21's
recorded divergence, since this engine flies up to four humans); the network gate holds trivially
(no network play) and the class gate holds by construction (every airframe it can fly is class 0).
Seven of the eleven have a field to move; slots 8–11 have no consumer here, exactly as they have
none on the original's aeroplane arm. The draw is keyed by the aircraft's spawn ordinal off
`Rng.Spawn`, not taken from a shared stream, so a `--det` replay reproduces it and it cannot shift
any other subsystem's sequence.

⚠ **The class-4 wingman exemption has no analogue here and is a recorded divergence.** This engine
flies every aircraft, hostile or wingman, off a *player* def (`PlaneStats.Load` requires
`player_airplane` in the `kind_of` chain), and every one of those resolves `mode jet`, so the class
gate holds trivially and an Instant Action wingman is jittered where the original's own `w*`
wingman would not be. Selecting on team instead would be inventing a mapping the data does not
carry.

## The measurement harness, a validation route

`FUN_00492040` is worth understanding on its own, because it defines what the original considered
its own performance envelope:

- **Stall speed**, step up from rest in 0.1 mph increments until lift carries weight.
- **Accel times**, from stall speed at full throttle, time to 25 / 50 / 75 / 99 % of the span
  between stall and top speed.
- **Decel times**, from top speed at zero throttle, the mirror.
- **Climb / dive speeds**, steady-state speed held at pitch angles of **+90°, +45°, −45°, −90°**
  (the angles appear as the immediates π/2 and π/4).
- **Turn rates**, maximum sustained yaw rate at **100 / 75 / 50 / 25 %** of top speed and at
  stall speed, reported in deg/s.

⚠ **The tuner shipped in the retail executable, but the dialog is dead code.** Resolved 2026-08-14:
`FUN_00445090`, the dialog that writes `dynamics.txt`, has **no callers anywhere in the image**. The
harness itself is still reachable, from `FUN_004897c0`'s tail via `FUN_00493dc0`, gated on the debug
flags `DAT_0071c78a` and `DAT_00628a00` being set with `DAT_0071c788`, `DAT_0071c789` and
`DAT_00654120` clear; it reports through `sprintf` plus `FUN_00458770` ("Top speed = %5.1f MPH",
`0x00628a6c`) rather than to a file. So the 18 figures are obtainable by flipping flags in a
debugger, not by finding a menu.

**What the harness does to the world, and why it found no ground blow.** It saves the aircraft's
orientation, position, throttle, velocity, angular rates and all six stick inputs; **overrides the
global timestep to a fixed 0.01 s**; zeroes the stick and resets position and orientation each
iteration; steps its own stripped integrator (`FUN_00491820`) in closed loops of up to 10 000 and
30 000 iterations; then restores everything. That stripped integrator is the second copy documented
under "There are two integrators" above, and it has collision and ground blow removed.

## ⚠ Authored values vs the executable's fallbacks

**Everything above quotes the fallbacks compiled into `crimson.exe`. This install authors different
numbers, and in four places the difference changes the conclusion.** The authored set is the table
below, read back out of `extracted/zrdr/player.zrd.json` and pinned by
`CSVM.Tests/PlaneStatsFlightGlobalsTests`; read it as the operative one, and treat the fallbacks as
evidence of intent only.

| Key | Fallback | **Authored** | Why it matters |
|---|---|---|---|
| `yaw_fade_out` | 45 mph | **400 mph** | The yaw curve is **not** flat across the envelope, see below |
| `yaw_max` | 22.5 mph | **50 mph** | |
| `yaw_low_speed` / `yaw_high_speed` | 0.05 / 0.1 | **0.0625 / 0.17** | |
| `high_speed_pitch_fade` | [500, 600] mph | **[1000, 1001] mph** | The pitch fade is **unreachable**, implemented, inert |
| `highGs` / `lowGs` | [5, 9] / [−5, −9] | **[9, 15] / [−6, −9]** | The positive ramp is unreachable; the negative one is grazed |
| `lift_accel_rate` | 1.2 | **0.75** | |
| `turn_fade_in` / `_out` | 10 / 40 mph | **10 / 50 mph** | |
| `maxAOA` | 31.8° | **46.0°** | |
| `drag_factor` (global) | 3.0 | **1.5** | Dead in the executable either way (see below) |
| `stall_mag` | 0.45 | **1.25** | |
| `groundblow_elev` | 100 m | **400 m** | A four-times-longer nose ray, and a four-times-flatter falloff |
| `groundblow_mag` | 1.5 | **10** | 11× amplification of an away-from-surface command at contact, against 2.5× |
| `ai_groundblow` | 0.9 | **0.5** | The AI's fixed push is 5.0·S authored, against 1.35·S under both fallbacks |
| `bounce_factor` | 0.8 | **0.6** | The ceiling on effective normal restitution |

**Corrected, the yaw curve declines, it does not go flat.** With the authored numbers, rudder
authority is 0.0625 up to 10 mph, ramps to **1.0 at 50 mph**, then falls linearly to **0.17 at
400 mph** and holds. At the Bloodhawk's 302 mph cruise that is ≈ **0.40**, and it varies across the
whole flight envelope. The earlier "flat 0.1 above 45 mph" reading was an artefact of the fallbacks.
This substantially rehabilitates the remake's `eff = 1.4 − clamp(v/fd, 0.25, 1.15)`, which is also a
declining function of speed, the *shape* was right, only the curve is wrong.

**Implemented.** `FlightModel.YawAuthorityAt` is this table, built from A1's already-plumbed
`PlaneStats` fields, applied to yaw only. The refit `YawTune` needed was tiny (1.32 → 1.33), at the
290 mph cruise the yaw-360 measurement was fit against, the two curves are within a point of each
other (`eff` ≈ 0.44, the new curve ≈ 0.43), which is exactly the "shape was right" finding above.
The two curves diverge sharply away from cruise: at 60 mph (just above stall) `eff` was pinned to
its 1.15 ceiling against the new curve's 0.98; at 336 mph (the model's own achieved terminal-dive
speed) `eff` gave 0.29 against the new curve's 0.32.

**Corrected, the pitch high-speed fade never fires.** Authored at 1000/1001 mph against a maximum
attainable dive speed of ~528 mph, it cannot engage. It is real code on a threshold this game never
reaches. **It is nevertheless implemented** (`FlightModel.PitchAuthorityAt`), because the shape is
decoded, an install authoring a lower pair binds it, and a curve that silently is not there is the
kind of absence a later change trips over. `LatentControlAuthorityTests` pins the curve on a
synthetic airframe that authors the window into the flyable band, and pins that on all eleven stock
airframes pitch and roll authority are the identical number at every speed up to
`MaxDiveSpeedFrac × fd_speed`. Landing it left the eleven-airframe dump SHA256-identical.

**C24 landing note, confirmed unreachable for all eleven player airframes, and nothing was
implemented.** `MaxDiveSpeedFrac` (1.75 × `fd_speed`) is the model's own hard numerical ceiling on
`Speed`, never redirected, never faded, a plain clamp applied every tick, so it is the most
generous "could this airframe ever reach the fade" test available, more generous than any
aerodynamically-settled terminal dive (itself well below it on every airframe measured, per
`POST-B14.md`). `1.75 × fd_speed` for all eleven, against `high_speed_pitch_fade`'s authored
[1000, 1001] mph window:

| Airframe | fd_speed (mph) | 1.75 × fd_speed (mph) | measured terminal dive (mph) | reaches 1000 mph? |
|---|---:|---:|---:|---|
| bhawk (Bloodhawk) | 302.0 | 528.5 | 336.36 | no |
| devastator | 252.8 | 442.4 | 280.68 | no |
| fury | 281.9 | 493.3 | 314.65 | no |
| warhawk | 201.3 | 352.3 | 217.95 | no |
| autogyro | 228.2 | 399.4 | 221.02 | no |
| avenger | 264.0 | 462.0 | 293.55 | no |
| balmoral | 176.7 | 309.2 | 149.49 | no |
| brigand | 241.6 | 422.8 | 268.85 | no |
| firebrand | 208.0 | 364.0 | 222.39 | no |
| kestrel | 217.0 | 379.8 | 236.27 | no |
| peacemaker | 290.8 | 508.9 | 324.77 | no |

The Bloodhawk's own hard ceiling (528.5 mph) is the highest of the eleven and sits at little over
**half** of `high_speed_pitch_fade[0]` (1000 mph), every other airframe's ceiling is lower still.
**Nothing was implemented.** Untestable code on a threshold no capture of the original could ever
exercise is exactly the invented content this project's ground rules forbid; this document and
`CSVM.Tests/ControlLimiterTests` (which fails if a data edit brings one into reach) carry the closed
finding, so a future session reading the decode does not mistake the fade for a missing feature.

### ⚠ Both the G and AOA limiters are inert — RETIRED (2026-08-24)

The reading was that the lift clamp is a hard ±5/9 G while `highGs` begins at 9 G and `lowGs` at
−6 G, so neither limiter can engage before lift is already capped. Two things were wrong with it.
The G ramp reads the **signed body-up component** of the delivered load factor, not the demand's
length, and a sustained outside push carries two airframes past `lowGs[0]`. And the AOA half is not
a threshold at all: it is a window that is below 1 at every non-zero α, so it binds throughout
normal manoeuvring. Both corrections and their measurements are in the next section; the margin
table below is retained because it is the α measurement that settles the second one.

**D33 landing note, measured on all eleven airframes.** The
structural argument above is real but it is not what settles this: the delivered load factor is
clamped at +9/−5 G, which is *exactly* where `highGs`'s ramp starts, so the argument only ever
proves the reduction is zero at the boundary. What settles it is the measurement. The remake's
`FlightModel.LoadFactorDemand` reports the lift demand's body X/Y projection **before** both
clamps, the most generous available reading of "the G this aircraft is pulling", and
`CSVM.Tests/ControlLimiterTests.cs` flies five max-performance manoeuvres per airframe (the pull at
`fd_speed` and entered at 1.5 × `fd_speed`, the same pull banked, a full forward push, full rudder;
600 steps at 1/60 s each, more than a full loop) and takes the peak of each:

| Airframe | peak demanded G | `highGs[0]` | margin | peak α | `maxAOA` | margin |
|---|---:|---:|---:|---:|---:|---:|
| bhawk (Bloodhawk) | **6.43** | 9.0 | 2.57 | **40.5°** | 46.0° | 5.5° |
| devastator (`pfighter`) | 4.88 | 9.0 | 4.12 | 28.1° | 46.0° | 17.9° |
| fury | 5.73 | 9.0 | 3.27 | 34.2° | 46.0° | 11.8° |
| warhawk | 3.30 | 9.0 | 5.70 | 14.6° | 46.0° | 31.4° |
| autogyro | 3.54 | 9.0 | 5.46 | 15.4° | 46.0° | 30.6° |
| avenger | 5.23 | 9.0 | 3.77 | 31.0° | 46.0° | 15.0° |
| balmoral | 2.60 | 9.0 | 6.40 | 12.6° | 46.0° | 33.4° |
| brigand | 4.33 | 9.0 | 4.67 | 23.2° | 46.0° | 22.8° |
| firebrand (`fbrand`) | 3.32 | 9.0 | 5.68 | 14.4° | 46.0° | 31.6° |
| kestrel | 3.63 | 9.0 | 5.37 | 17.6° | 46.0° | 28.4° |
| peacemaker | 6.10 | 9.0 | 2.90 | 37.6° | 46.0° | 8.4° |

The G peak is a full-forward **push** at 1.5 × `fd_speed` on the eight fastest airframes and a pull
on the rest; the α peak is the pull at 1.5 × `fd_speed` on all eleven. The retired kinematic
nose-chase used to hold every α small, so these margins are much tighter than they once measured
(the Bloodhawk's α margin is 5.5°), but no manoeuvre crosses either threshold and
`ControlLimiterTests` still fails if one comes into reach. The suite's own instruments agree from
the other side: with the AOA window held off the sustained pitch-rate row reports
α = 32.0/32.7/33.1° at 120/200/280 mph, `zoom-climb` 38.2° at its minimum speed, the sustained
turn 31.8°, and the knife-edge probe peaks at 0.77–5.68°; with the window live (the shipped
default) those read 24.5/24.8/25.0°, 25.4° and 23.7°.

⚠ **The margin against the executable's own fallbacks is one hundredth of a G.** The Bloodhawk's
5.01 G peak is 0.2 % **past** the compiled fallback `highGs[0] = 5`, under the fallbacks the
limiter would engage, but a fraction of a percent into a 4 G-wide ramp. What puts the mechanism out
of reach is the **authored 9**, not the model's inability to pull hard. This is the cleanest example
in the whole decode of why a fallback is evidence of intent and not of behaviour.

**Corrected, the G ramp grazes and the AOA window binds.** Both halves are now implemented
(`FlightModel.OpposingCommandLimitAt`, "Torques and the limiters" above), and implementing them
settled their reachability by measurement rather than by argument.

**The pitch fade and the G ramp leave the stock envelope untouched.** With both live and the AOA
window held at its neutral 1, the eleven-airframe flight dump is SHA256-identical to the tree before
the change. Spending the AOA window instead moves 286 of its 891 lines, so the instrument sees the
difference in both directions.

**The G ramp's negative side is grazed, not unreachable.** Measured on the quantity the original
reads, `FlightModel.BodyUpLoadFactor`, the delivered lift's signed body-up component, over the
same five max-performance manoeuvres:

| Airframe | max body-up G | `highGs[0]` | min body-up G | `lowGs[0]` | into the −6 → −9 ramp |
|---|---:|---:|---:|---:|---:|
| bhawk | 6.43 | 9.0 | **−6.23** | −6.0 | **7.7 %** |
| devastator (`pfighter`) | 4.38 | 9.0 | −4.88 | −6.0 | - |
| fury | 5.15 | 9.0 | −5.73 | −6.0 | - |
| warhawk | 3.23 | 9.0 | −3.28 | −6.0 | - |
| autogyro | 3.54 | 9.0 | −3.45 | −6.0 | - |
| avenger | 4.69 | 9.0 | −5.23 | −6.0 | - |
| balmoral | 2.60 | 9.0 | −1.47 | −6.0 | - |
| brigand | 3.97 | 9.0 | −4.33 | −6.0 | - |
| firebrand (`fbrand`) | 3.25 | 9.0 | −3.30 | −6.0 | - |
| kestrel | 3.49 | 9.0 | −3.63 | −6.0 | - |
| peacemaker | 5.77 | 9.0 | **−6.08** | −6.0 | **2.7 %** |

A sustained full forward push at 1.5 × `fd_speed` carries the Bloodhawk and the Peacemaker a few
percent into a 3 G-wide ramp, so a separating pitch or yaw command there keeps 92–97 % of its
authority. No envelope scenario flies that manoeuvre, which is why no row moves.
`ControlLimiterTests` pins the graze as a bounded fraction of the ramp with a halved-`lowGs` control
that must break it, rather than pinning an unreachability that is not true.

**The AOA window is not a threshold, and it binds.** `(cos α − cos maxAOA) / (1 − cos maxAOA)` is
below 1 at every non-zero α: 0.80 at 20°, 0.56 at 30°, 0.23 at 40°, zero at the authored 46°.
Against the α the held-off plant reaches in a sustained pull (32.0–33.1° at 120/200/280 mph,
38.2° at the zoom-climb's minimum speed) that is a factor of about a half on the elevator, and
with it live the pull settles at a smaller α (24.8°) where the window reads 0.70.

**The window is live at its decoded strength (`FlightModel.AoaLimiterFactor` 1), and this is
what it moves.** The eleven-airframe dump with the window held off is SHA256 `7BF4C7AE…`; with it
live, `D2D682D8…`, 143 of 891 lines differing. On every airframe the same seven rows move and no
other: the pitch rate, the yaw-360 time, the sustained turn's speed, sink and rate, and the zoom
climb's height and minimum speed. Every equilibrium row (level top speed, terminal dive, altitude
cap, near-cap speed, eighth-throttle, decel, roll-360, stall departure, knife-edge) is unchanged,
which is the window's shape: it multiplies only a pitch or yaw command that opens the nose/path
angle, and those rows hold none.

| Airframe | `pitch-rate` °/s | `sustained-turn-rate` °/s | `sustained-turn-speed` mph | `sustained-turn-sink` ft/s | `zoom-climb` ft | `zoom-climb-min-speed` mph | `yaw-360` s |
|---|---:|---:|---:|---:|---:|---:|---:|
| bhawk | 32.43 → 22.51 | 34.52 → 25.83 | 218.03 → 249.87 | 14.26 → −7.51 | 821.44 → 1279.51 | 117.90 → 143.88 | 52.18 → 54.08 |
| devastator (`pfighter`) | 25.65 → 20.97 | 27.12 → 22.70 | 216.70 → 228.37 | −8.25 → 2.01 | 1020.40 → 1317.53 | 126.16 → 134.93 | 38.92 → 40.47 |
| fury | 28.91 → 21.88 | 30.55 → 24.17 | 222.89 → 244.22 | −2.08 → −2.30 | 933.55 → 1320.16 | 124.36 → 140.95 | 46.00 → 47.75 |
| warhawk | 18.13 → 17.03 | 19.25 → 18.11 | 194.68 → 194.29 | −3.62 → −9.82 | 1396.57 → 1514.80 | 135.46 → 136.24 | 48.98 → 50.05 |
| autogyro | 39.32 → 36.03 | 19.64 → 17.10 | 210.95 → 211.69 | −0.65 → 3.51 | 762.57 → 828.58 | 169.96 → 170.57 | 18.62 → 19.00 |
| avenger | 26.95 → 21.29 | 28.48 → 23.21 | 216.47 → 232.66 | −7.97 → 0.81 | 963.27 → 1296.66 | 121.60 → 133.29 | 41.13 → 42.77 |
| balmoral | 11.29 → 10.83 | 12.00 → 11.55 | 118.74 → 119.60 | −11.90 → −8.91 | 1054.52 → 1114.37 | 51.83 → 51.77 | 51.47 → 52.92 |
| brigand | 21.62 → 18.83 | 22.73 → 20.09 | 218.99 → 222.77 | 2.73 → 0.39 | 1197.60 → 1442.24 | 126.67 → 131.42 | 36.82 → 38.30 |
| firebrand (`fbrand`) | 18.80 → 17.67 | 20.04 → 18.91 | 200.97 → 200.62 | 0.00 → −5.72 | 1413.04 → 1528.62 | 143.58 → 144.32 | 31.07 → 32.05 |
| kestrel | 19.42 → 17.80 | 20.63 → 19.00 | 205.87 → 205.86 | 2.05 → −5.22 | 1334.23 → 1496.23 | 135.00 → 136.72 | 32.62 → 33.83 |
| peacemaker | 30.94 → 22.31 | 32.86 → 25.18 | 220.22 → 246.10 | 8.49 → −5.61 | 863.19 → 1286.60 | 120.48 → 141.81 | 48.67 → 50.48 |

Against the Bloodhawk's footage, `sustained-turn-rate` moves toward its 18.95 and `pitch-rate`,
`zoom-climb` and `zoom-climb-min-speed` move away from their 33.00, 936 ft and 127.9 mph. All four
of those filmed figures are discarded: the rows are informational in `Probes.FlightEnvelope` and
report the decoded plant's own number, while `FlightEnvelopeTests` asserts the five rows that carry
a decoded target. The pitch rate's attribution is the next section.

**Two of the parsed globals are dead in the executable.** The global `drag_factor` (→ `0x71c44c`,
fallback 3.0) and `drag_fade_speed` (→ `0x71c450`, parsed × 0.44704, fallback 40 mph) are written
by the `player.json` parser at `0x4744c0`/`0x4744f0` and **read by nothing anywhere in the image**,
a full dword-reference scan finds only the parser's own two writes for each. There is no global
drag multiplier and no low-speed drag fade; the per-plane `drag_factor` is the only drag scale.
Checked while hunting the CAP-05 deficit (a fade below ~300 mph would have produced exactly its
shape); the hunt is what proved the keys dead.

## The α a full pull holds

With the AOA window live the Bloodhawk's sustained full-elevator pull reads 22.5 °/s against a
filmed 33.00, and this section says what bounds α in the original's pull, measures the remake's
pull step by step, and names where the residual lives. The instrument is
`Probes.PullToLimit` (written by `ZzPullInstrument` to `CSVM_PULL_OUT`): full back stick and
full throttle from 200 mph level, one line per sim step with α, the window, the limiter scalar,
the demanded and delivered G against the lift ceiling, the nose's pitch rate, the flight path's
turn rate and the weathervane's torque, plus the mean rate over the first 360° of nose rotation,
which is the shape the footage's figure was binned in.

**What bounds α in the original, and what does not.**

- **The `liftAOAs` blend** (`_DAT_0071c430`/`_DAT_0071c434`, the cos α at `0x48c528`–`0x48c56d`)
  is authored 5°/9°, so past 9° the target velocity is `speed · nose` in full and the demand is
  `lift_accel_rate · speed · 2 sin(α/2)` plus weight. It sets how much path turn a given α buys,
  not a bound on α.
- **`FUN_0041abd0`** clamps the demanded G to −5/+9, the coefficient to ±1.8 and then takes a
  one-sided `min` against `0.5 · FUN_0041ad80(Mach)` = `0.5 · (1.5 − 0.3 M)`, the `0.75 − 0.15 M`
  ceiling. None of the three touches α, and in this pull none binds: the demand peaks at 4.5 G
  with the ceiling between 12 and 16 G, so the delivered G is the demand at every step.
- **The 8 ft/s gate** (`local_84 <= 2.4384` at the top of the near-field build, `n = 0` and a
  placeholder direction) is a low-speed cut on the lift build. The pull never goes below 140 mph.
- **The limiter's G read** is the same tick's delivered lift (see "Torques and the limiters"),
  and it is out of reach here: the pull peaks at 5.83 G against `highGs[0]` 9, so the AOA window
  is the whole of the limiter in this manoeuvre.

So nothing clamps α directly. α in a held pull is an equilibrium: the nose rate, which is the
elevator torque times the window at α (`0x48c9f4`) minus the weathervane's `return_rate · α/2`,
settled against `ang_momentum_damp`, must equal the path's turn rate, which the lag rate
`lift_accel_rate` (`_DAT_0071c448`, authored 0.75/s) buys from that same α. Every term in that
balance is decoded or authored.

**The per-step readout.** Window live, 200 mph entry:

| t (s) | mph | α° | window | demand G | cap G | nose °/s | path °/s |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 0.25 | 215.8 | 4.6 | 0.989 | 2.03 | 13.95 | 29.94 | 2.06 |
| 0.53 | 227.2 | 12.1 | 0.928 | 3.59 | 15.42 | **35.99** | 13.48 |
| 1.00 | 229.6 | 20.0 | 0.803 | 4.44 | 15.74 | 30.92 | 19.50 |
| 2.00 | 204.3 | 24.8 | 0.697 | 4.07 | 12.55 | 22.56 | 21.00 |
| 4.00 | 152.2 | 25.8 | 0.673 | 1.98 | 7.07 | 20.73 | 20.83 |
| 6.00 | 144.9 | 24.7 | 0.700 | 0.45 | 6.41 | 23.77 | 25.01 |
| 12.00 | 320.6 | 21.3 | 0.777 | 5.07 | 29.91 | 26.64 | 26.25 |

The nose rate peaks at 35.99 °/s half a second in, while α is still 12° and the window 0.93, then
falls as α opens and the window closes, and settles where the nose and path rates meet: 22.5 °/s at
α 24.8° with the window at 0.70. The first 360° of nose rotation takes 14.22 s, a loop mean of
**25.32 °/s**. Window held off (the seam at 0), the same pull peaks at 37.77 °/s, settles at
33.9 °/s at α 26.9° and loops in 10.82 s, **33.28 °/s**.

**The footage band.** The 33.00 is the mean of four bins read round a loop, 37.9 / 33.7 / 30.7 /
36.5 °/s (see "The `*Tune` rates"), so the band the row can be judged against is 30.7–37.9 °/s, and
the reading is a frame-derived rate that ranks readings and cannot refute a decode
(`docs/verification.md` DET-11, DET-12). The held-off plant's 33.28 sits inside that band; the
window-live plant's 25.32 sits 5.4 °/s below its floor, and its settled 22.5 is 8.2 below the
lowest bin.

**Attribution: the α equilibrium owns the difference, and no fitted term closes it.** With the
window live, a 33 °/s wings-level pull at 200 mph needs a path rate of 0.576 rad/s; weight buys
about 0.11 of that (`g / V`), so the lag must supply 0.47 rad/s, which at `lift_accel_rate` 0.75
means `2 sin(α/2)` = 0.62, α ≈ 36°. The window at 36° is 0.37, and the weathervane at that α is
larger than at 25°, so the elevator cannot hold the nose at 33 °/s there: the arithmetic has no
solution at 33 °/s with these values, and the plant's 22.5 °/s at 24.8° is the balance those
values do have. The named terms are the window at `0x48c9f4`–`0x48ca18` on the authored 46°
`maxAOA` (`_DAT_0071c42c`) and the lag rate at `_DAT_0071c448`. Neither is weakened (plan
Decision 1): the plant's value is the executable's, and the filmed figure is discarded rather
than carried as a conflict, as are the filmed figures beside `yaw-360` and `decel-290-150`.

**The lag-rate lead is closed by a live read.** The lag rate is the one term in the balance with
a compiled fallback (1.2, `0x3f99999a`) far from its authored value (0.75), and a faster lag needs
less α for the same path rate: the instrument's diagnostic run with 1.2 in place of 0.75 settles
at 31.7 °/s at α 18.5° and loops at 29.75 °/s, inside the row's band. The retail process does not
run it. A passive sample of a live retail process through 200 s of mission flight with full pulls
(Bloodhawk, Mach to 0.467) reads `_DAT_0071c448` at 0.75 on every in-mission sample and the
`liftAOAs` thresholds `_DAT_0071c430`/`_DAT_0071c434` at cos 5° and cos 9°, all three slots BSS
that read 0.0 until a plane loads (the same shape as the atmosphere threshold, "Atmosphere"). The
pull is therefore the executable's own and the filmed figure is discarded; the same-tick lift
read above remains a port-fidelity difference on its own merit. The climb residual of "The sustained
climb" is a separate question and not this one: a straight climb settles at α = 0, so no α
equilibrium reaches it.

## The resting altitude cap is the atmosphere band edge

The original's flight has a ceiling: the sustained climb above leaves its plateau at ≈6,600 ft
(`CAP-03`). That ceiling is the atmosphere's own band boundary at 2000 m, not a clamp. There is no
altitude clamp of any kind on an aeroplane in `crimson.exe`, and the thin band's force collapse is
sufficient on its own to stop a climb at the boundary.

**No clamp exists.** The integrator `FUN_0048e580` writes the position triple at `0x0048ea81` as
three unconditional adds of velocity times `dt` into `[obj+0x204]`, `[obj+0x208]` and `[obj+0x20c]`,
with no comparison of any component against anything; the one velocity clamp in that function is the
`AiNoseSpeedFloor` block (`0x608128`, `0x48e95e`-`0x48e998`), a non-player along-nose floor unrelated
to height. Every access to the altitude field `[obj+0x208]` on a vehicle is accounted for elsewhere
in this document: the atmosphere read at `0x48fc51`, the measurement harness's save, zero and restore
at `0x491250`, `0x491270` and `0x491284`, the respawn store of `900.0` at `0x496b6d`/`0x496b75`, the
terrain push at `0x470515`/`0x470530`, and the terrain-follow write in `FUN_0048bc40`. None of them
compares the altitude against a ceiling. The constants agree: the float `2003.0` (`0x44fa6000`) is
absent from the image entirely, the five occurrences of `2000.0` (`0x44fa0000`, at `0x453303`,
`0x488dda`, `0x49dcd7`, `0x4c23e5` and `0x4c2456`) all sit outside the flight and vehicle-integrate
paths, and `6561.6796875` (`0x45cd0d70`) occurs exactly once, the band threshold's store at
`0x46368b`. A clamp needs both a constant and a comparison, and the executable has neither.

**The band ceilings the climb by itself.** Above 2000 m `FUN_0041aca0` returns the thin band (see
"Atmosphere"), and both of the forces that carry an aircraft upward read it. Dynamic pressure is
`0.5 · ρ · V²` (`FUN_0041ac80`), so the lift force `q · S · C_L` scales with ρ, and `FUN_0041abd0`
ceilings `C_L` at `min(±1.8, 0.5 · (0.75 − 0.15 M))`, so the deliverable load factor scales with ρ
too and cannot be bought back by demanding more G. Thrust available (`FUN_0041acf0`) evaluates
`FUN_0041ac80` at `(0.84 M + 0.112) · a`, so it scales as `ρ · a²`. Crossing the boundary therefore
divides lift by **16.73** (ρ 2.2688e-3 to 1.3560e-4) and thrust available by **22.0**
(`0.0597687 × (968.02 / 1109.54)² = 0.04549`), and the Mach terms take more still, since the thin
band's lower sound speed raises Mach at a fixed true airspeed by 1.146 and thrust falls with Mach.
Holding altitude needs a load factor of 1, so the airspeed required scales as one over the square
root of the density ratio, a factor of **4.09**: against the Bloodhawk's level equilibrium of
300.4 mph measured and 300.5 solved, level flight in the thin band would take about **1,229 mph**.
No stock airframe is within a factor of four of that. It is the same 4.09 that moves the fallback
airframe's stall from 75.5 to 309 mph. An aircraft under power reaches 2000 m and stops there
because lift and thrust collapse together, with nothing holding it down.

**Above the line the aircraft coasts, and how far is bought with the climb, not set by a constant.**
Drag falls by the same 16.73, so a crossing continues very nearly ballistically, reaching
`v_y² / 2g` above the edge. **The original does this at the controls, which is what distinguishes the
band from any clamp**: a sustained ~160 mph climb tops out around **7,000 ft** (133 m above the
edge), while a Bloodhawk built for the strongest engine and least weight, on nitro and crossing over
300 mph, reaches nearly **9,000 ft** (743 m above it). A clamp gives one ceiling however you arrive.
The filmed **6712 ft** figure is a third such apex, from a take that crossed slower still, and the
`42.8 m` (~140 ft) `Position.Y` backstop once sized to it never engaged: removing it left the
eleven-airframe flight dump byte-identical, while shrinking it to 0.05 m moved 22 of the dump's
lines, which is the control that says the instrument can see the ceiling at all.

**What the plant reaches.** The dense band is bit-identical to the single band the plant carried
before, so nothing below 2000 m moves: `level-top-speed` 300.46 mph, `terminal-dive` 356.00 mph and
`zoom-climb` 949 ft are unchanged, and the level equilibrium 12 m under the edge still solves to
300.46 mph. A 22° full-throttle hold now apexes at **6,884 ft** on the Bloodhawk, and across the
eleven airframes at 2009 to 2106 m, each one the coast its own crossing rate buys (17.8 to 45.4 m/s).
`FlightConstantInventoryTests` measures the apex against the frictionless `v_y² / 2g` per airframe,
so a clamp returning would collapse it toward nothing and a band that stopped biting would leave it
unbounded.

⚠ **The autogyro's ceiling sits well above the others, and that is the decode, not a defect.** Its
wing loading is low enough to keep flying in the thin band (an 18.5 mph dense-band stall becomes
75.7 mph up there, against a 228 mph `fd_speed`), so a shallow hold climbs a long way past the edge,
to 6,290 m over a 240 s probe, before it stalls. It is the one airframe that cruises up there, so it
is also the one the thin band's own `pow` base moves (see "Thrust available"). The original does the
same at the controls. Do not add a clamp to bound it.

⚠ **CSVM crosses the edge faster than the original does, so its apex runs high.** The plant's
sustained climb plateaus at 204 mph against the filmed 163, a residual recorded under "The sustained
climb", where every term that could move a steady climb is ruled out by arithmetic and the owner is
still unnamed. The ceiling inherits that error and corrects itself when the climb does. Do not close
the gap by putting a constant back.

### ⚠ The ceiling is a measurement with no counterpart in the executable — RETIRED (2026-09-06)

This section recorded the ≈6,600 ft ceiling as a footage measurement that nothing in `crimson.exe`
had been traced to, and read its mechanism as a deletion of climbing velocity, on the strength of a
filmed 22° pull that gains no height while airspeed bleeds. A sweep of the aeroplane path disproves
the premise: no altitude clamp exists, and the thin band above 2000 m cuts lift by 16.73 and thrust
by 22.0, which stops a climb at the boundary on the forces alone. A collapse of lift and thrust
produces the same filmed bleed, so that clip never discriminated the two readings. The ≈6,600 ft
figure is where the powered climb ends, not where the aircraft stops: flown at the controls, the
original coasts hundreds of feet past it, further the faster it arrives.

⚠ **The dive-speed cap is the same kind of object and must not bind.** `MaxDiveSpeedFrac`
(1.75 × `fd_speed`) is a numerical backstop against a loop energy pump or a `dt` spike, not a
terminal speed, a cap that binds replaces a measured terminal with a guess. It does not bind: the
Bloodhawk's full-throttle 71° dive terminates at **1.11 × `fd_speed`** on the aerodynamics alone.
Across all eleven airframes, the fastest of a vertical dive, a 70.7° dive and a held loop, each
entered at `fd_speed` and flown two minutes, peaks between **0.996 and 1.191 × `fd_speed`**, so the
narrowest margin to the cap is 0.56 `fd_speed` (`FlightConstantInventoryTests`). Entering a dive
above terminal does not test it either, since the aircraft only decelerates from there
(`docs/verification.md` METHOD-21).
It is also the ceiling the `high_speed_pitch_fade` unreachability table below is computed from,
precisely because it is the most generous "could this airframe ever get there" test available.

## Ground blow, a control bias, not a force (`FUN_0048bf60`, `FUN_0048c220`)

Decoded 2026-08-14. All three keys are **raw scalars**: plain dword stores, no `× 0.44704`, no
cosine.

| Key | Global | Parser store | Fallback | **Authored** |
|---|---|---|---|---|
| `groundblow_elev` | `0x0071c420` | `0x00474320` | 100.0 | **400** |
| `groundblow_mag` | `0x0071c424` | `0x0047434a` | 1.5 | **10** |
| `ai_groundblow` | `0x0071c428` | `0x00474373` | 0.9 | **0.5** |

⚠ **`groundblow_elev` is a length in METRES, and it is not a threshold.** It is the length of a ray,
not an altitude, not a range that anything is compared against, and not feet. World units are metres
(see "Units and conventions"; the identification is positive, from `nom_gravity`'s 9.82 reference
divisor at `0x006080d4`, not from the absence of a conversion).

**The probe.** `FUN_0048bf60` casts a ray from the aircraft origin along the **nose**, of length
`groundblow_elev` metres, into the world segment query `FUN_004c8ec0` at `0x0048c01f`. The direction
is `−(obj+0x198)`, and `obj+0x198` is the +Z body axis, so this is `−Z`, matching the nose convention
above. A hit qualifies only if the surface faces back toward the aircraft (`dot(b, n) > 0` strictly,
`0x0048c0ff`). One threshold, no hysteresis, no latch. The same value is the falloff's denominator,
so raising it both lengthens the ray and flattens the ramp.

With `n` the hit normal, `b` the backward body axis, `d` the straight-line distance to the hit point
(`FUN_005388d0`), `c = dot(b, n)`:

```
S = sqrt(c) · (elev − d) / elev     proximity: 1 at contact, falling to 0 at the ray's end
A = normalize(n × b)                unit axis rotating the nose away from the surface
V = A · S                           the scaled axis, which is what the probe hands back
```

⚠ **The probe returns `S` and writes `V = A · S`, not `A`** (corrected 2026-08-15). Its
out-parameter takes the normalised cross product multiplied by `S` (`FUN_00422690`, then the three
scalings before the adds), and `S` itself is the return value. The caller uses that one scaled
vector **twice**, once in the dot and once in the add, which is where the second power of `S` in the
player law comes from. The out-parameter is zero-initialised from `DAT_0075d1b8`, a zero vector, and
is written only on a qualifying hit, so a miss, a `c ≤ 0` rejection and the vehicle-filter abandon
all leave a zero vector and contribute nothing on either path.

**Where it goes, which is the whole question.** `FUN_0048c220` writes into the same accumulator the
three stick channels were summed into one call earlier in `FUN_0048c470`; `FUN_0048e580` then adds
that accumulator to `obj+0x160`, the persistent angular-velocity state. The linear acceleration is a
**different argument** of `FUN_0048c470`, reaching velocity separately. Ground blow is therefore a
bias on **control response**, not an applied force, which is what the GDD's §4.1.7 describes and what
`CAP-02` inferred.

Player path (`0x0048c30f`), with `p = dot(accum, V)`:

```
p ≥ 0 (commanding away):   accum += V · |p| · groundblow_mag
p < 0 (commanding into):   accum += V · 0.05·|p| · groundblow_mag,  and S is zeroed
```

Both push along `+A`, away from the surface. Written against the unscaled command component
`u = dot(accum, A)`, the same two lines are `u → u · (1 + mag·S²)` and
`u → u · (1 − 0.05·mag·S²)`, since `p = S·u` and the add carries a further `S`. Three consequences,
each matching a design claim:

- **It cannot overpower the stick.** An into-obstacle command is scaled by `1 − 0.5·S²` at the
  authored 10, so it is exactly halved at contact, cut less than that further out, and never
  reversed (reversal would need a `groundblow_mag` above 20).
- **It cannot save a head-on.** As the approach becomes perpendicular, `n → b`, so `n × b → 0` and
  the whole term vanishes (`FUN_00422690` leaves a zero vector untouched).
- **Commanding away is amplified** by `1 + 10·S²`, i.e. 11× at contact with the authored 10.

**A second, smaller effect.** After the accumulator write, the velocity *direction* is steered
exponentially toward the nose at `DAT_00622bbc · S` per second (`FUN_00460700`, speed preserved,
writing `obj+0x924`/`obj+0x934`). `DAT_00622bbc` is **2.0** and its only writer is the debug console
command `gbc` (string `0x00622c84`, handler `0x0043db9d`), so it is not a data key. It is suppressed
on the player path whenever the pilot is commanding into the obstacle, because `S` is zeroed there.

**The AI path is a different law, not a scaled one** (`0x0048c317`):

```
accum += V · (ai_groundblow · groundblow_mag)           = A · S · 5.0 as authored
```

It is independent of the AI's own command (a fixed push, where the player's is proportional to what
the pilot asked for), **linear** in proximity rather than quadratic, and **not multiplied by `dt`**
anywhere in the chain, so it is frame-rate dependent. Both the factor and `S` are cut by a further
**×0.15** (5.0 → 0.75, not down TO 0.15, confirmed by decompile) after the `obj+0xAC`
collision/ground-blow gate has expired but while the clock is inside `obj+0xB4` (below). Together
these make a carrier drop: 1.5 s with no AI ground blow, then 1.0 s at ×0.15, then full strength.
⚠ **This window IS reachable in the remake.** A zeppelin's fighter-drop launch
(`AiGeneratorRuntime` → `FlightRoster.SpawnAi`, the "Zeppelins" section below) is this engine's
carrier drop, and a freshly-dropped fighter flies the same `UsesAiForcePath` plant this law reads.
The port carries this timer on each carrier-released aircraft, so the ×0.15 cut applies for its
full 2.5 s window.

**Gates on the whole effect.** `obj[0xd6] != 4`; for non-player objects the clock must be past
`obj+0xAC`; and `FUN_0048c470` skips the call entirely when the player is flagged crashed
(`obj[0xe1] != 0`).

`obj+0x358` (dword `0xd6`) is the **AI mode enum**, read off the debug HUD's jump table at
`0x0041d1b8` in `FUN_0041c470` and the string each target pushes:

| Value | Meaning |
|---|---|
| 0 | patrol / evade / pursue / lay off, chosen by `obj+0x948`, `obj+0xBA`, `obj+0x2F0` |
| 1 | evasive maneuver |
| 2 | approaching danger zone |
| 3 | avoid crash |
| **4** | **stunned** |
| 5 | navigating danger zone |

**What reads this enum, and what writes the stick, is decoded on its own page.** See
[aiControlLaw.md](aiControlLaw.md) (2026-08-15): the AI brain `FUN_0041c270` runs from the
world tick just before this vehicle's integrator, dispatches on the order at `obj+0x2f0` and this
mode, and reaches the stick through `FUN_0041b560`. `obj+0x948` is the target pointer rather than a
flag, and `obj+0xBA` is the evade flag the damage handler sets.

State 4 is set by `FUN_004200d0` ("Stunned for %f seconds based on stun recovery", `0x00620518`),
which zeroes the control inputs and sets `obj+0xC0 = clock + duration`; the related tokens are
`stun_recovery`, `stun_recovery_interval` and the smokescreen weapon's `smokescreen_stun_*`, so it
is a weapon effect. A stunned AI therefore has its controls zeroed **and** its ground blow
suppressed, so it flies into terrain. That reads as deliberate.

**Emitters.** `FUN_004c8f70` walks the terrain grid and tests, per cell, the terrain geometry and
every scene node whose flag word at `node+0x24` carries both bits `0x4` and `0x10`, which are
`ACTIVE` and `INTERSECT_SURFACE` (`tools/mech3ax/crates/api-types/src/gamez/nodes.rs`);
`FUN_004c8ec0` keeps the nearest hit. Terrain and ordinary scenery always qualify.

**The vehicle filter, and what it excludes** (decoded 2026-08-15). Inside the player-only branch
guarded by `param_1 == DAT_0071c298` at `0x0048c047`, a hit node carrying bit `0x40000000` at
`node+0x28` is resolved through the vehicle registry at `0x0071dab8` (`FUN_004afee0`, matched on the
entity's node pointer at `+0xc`, walking up the parent chain on a miss). If an entity is found and
its byte at `+0xcc` is **zero**, the whole term is abandoned and the function returns 0
(`0x0048c0ac`). Everything else falls through and repels: an unmarked node, a marked node with no
registry entity above it, and a marked node whose entity carries a non-zero `+0xcc`.

⚠ **The `0x40000000` mark is applied at spawn, never authored.** Its only setter is `FUN_004848f0`
(`0x0048490c`), which ORs it into the node and its whole child subtree, and its only external caller
is the vehicle spawner `FUN_0047c210`. No node in the shipped world data carries it: all 7,064 nodes
in `extracted/C1/gamez/nodes.json` have `update_flags` 0 or 1.

**The byte at `+0xcc` is a mode flag, not a transient. It means "this vehicle is following a scripted
path instead of being flight-simulated".** The update dispatcher `FUN_00489ea0` reads it first and
calls the path follower `FUN_0048a110` **instead of** the movement law selected by `obj+0x67C`
(`0`/`4` being `FUN_0048e580`, the flight integrator that consumes ground blow). Its writers:

| Address | Function | Effect |
|---|---|---|
| `0x004b005e` | `FUN_004aff80`, the constructor | 0, so the default is not an emitter |
| `0x0047c568` | `FUN_0047c210`, the spawner | 1 when the spawn record carries a path, with `+0xd4 = 1` at `0x0047c57e` |
| `0x00452275` | `FUN_00451bf0` | 1, path taken from the placement record's `+0x24`, leaving `+0xd4` alone |
| `0x0049427c` | `FUN_004940d0` | 1 with `+0xd4 = 0`, called from the mission-goal runtime `FUN_0046a490` |
| `0x0048a863` | `FUN_0048a110` | 0, the only clear, and only on reaching the **last** waypoint within 5.0 m |

A non-zero `+0xd4` makes `FUN_0048a110` return on its first line, so the vehicle neither moves nor
clears `+0xcc`; the release is `FUN_0046a2b0` (`0x0046a2c3`), a mission-goal action. A vehicle
spawned with a path is therefore a frozen emitter from placement until a goal releases it, and stops
being one the moment it completes the path and drops into the flight model. No vehicle type holds the
flag by identity. The follower itself is the next section.

**Zeppelins: yes, and by the default rather than by a zeppelin rule.** `extracted/zrdr/vehicle.zrd.json`
names no zeppelin, blimp or airship type, so a zeppelin is never a spawned registry vehicle in this
install. IA1's is the C1 gamez scene node `multiplayer1zep` (`extracted/C1/gamez/nodes.json`), driven
by `mis_anim` animations, with `update_flags` 1 and both `active` and `intersect_surface` set. It
never reaches the registry filter at all, and repels the player exactly as terrain does. The GDD's
naming of zeppelins describes the outcome, not a mechanism.

⚠ **The vehicle filter is player-only**, since it sits inside the `param_1 == DAT_0071c298` test. On
the AI path nothing is filtered and every hit the sweep returns repels, other aircraft in ordinary
flight included.

**Implemented 2026-08-15** (`BL-359` player path; `C23` AI path; `git log --grep=BL-359`,
`git log --grep=C23`). `FlightController.ProbeGroundBlow` casts the ray, the SAME cast and falloff
for both paths, and `FlightModel.GroundBlowTerm` branches the response on `UsesAiForcePath`, added
to the command accumulator after the bank coupling and the weathervane and before
`BodyRates += cmd * dt`, which is this function's own ordering. Things the port does differently on
purpose: the emitter filter is the probe's `CollisionLayers.World` mask rather than a registry lookup
(only aircraft bodies carry the Aircraft layer, so terrain, scenery and the zeppelin repel and
aeroplanes do not, which is the same set the filter above produces for both paths, this engine has
no scripted-path vehicle carrying the original's vehicle-filter mark, so there is nothing for the
AI's "unfiltered" sweep to disagree with), and the second effect rides the model's velocity-steer
seam as an align rate of `2·S` (player, suppressed when commanding into the obstacle) or `2·S`
un-suppressed (AI); with `NoseChaseFactor` at its decoded 0 the ground-blow steer is the ONLY
rotation that seam applies, which is the original's own arrangement. `CSVM.Tests`'
`GroundBlowTests` pins both laws, including the player's `S²` power against the AI's linear `S`, the
AI's independence from command sign, and the body-frame conversion.
⚠ **`ai_groundblow` alone is not the AI factor.** `FlightModel.GroundBlowTerm`'s response is
`ai_groundblow · groundblow_mag` (5.0 authored), matching the correction above; the AI probe is
additionally gated on `AiModeMachine.Mode != AiMode.Stunned` (`0x0048c317`'s own mode check,
flightModel.md above), reproducing "a stunned AI flies into terrain". Carrier releases now carry
both timers: the 2.5 s post-drop ×0.15 window and the per-object `obj+0xAC` collision grace. The
carrier grace also suppresses AI ground blow; its probe and both output terms begin only after it.

## The scripted-path follower (`FUN_0048a110`), the second movement law

A placed vehicle can be a puppet on an authored waypoint list rather than a simulated aeroplane. The
aircraft update dispatcher `FUN_00489ea0` branches on `obj+0xcc` **before** it reaches any flight
law: non-zero and the object is driven by the path follower `FUN_0048a110`, zero and it runs the
movement law selected by `obj+0x67C` (`0`/`4` being `FUN_0048e580`, the flight integrator). The two
are exclusive, so nothing in the follower is a steering input to the flight model, and it is not AI
behaviour either.

**The lifecycle.** The spawner `FUN_0047c210` sets `+0xcc = 1` when the spawn record carries a path
(`0x0047c568`) together with a freeze flag `+0xd4 = 1` (`0x0047c57e`), so the vehicle sits motionless
at its first waypoint until a mission goal releases it (`FUN_0046a2b0`, `0x0046a2c3`, reached from
the goal-action runtime `FUN_0046a490`). That same runtime can attach a path at any time with
`FUN_004940d0` (`0x0049427c`), which sets `+0xcc = 1` and `+0xd4 = 0` so the vehicle starts moving at
once. Only `FUN_0048a110` clears `+0xcc` (`0x0048a863`), and only on the final leg.

**The placement is the path's, not the spawn record's.** Both entries overwrite the vehicle's
position and facing before they set the path flag: the spawner at `0x0047c3a5` and `FUN_004940d0` at
`0x004940f9` read waypoint 0, add the vehicle type's ride height at `type+0x218` to its Y, and take
the attitude from the normalised leg into waypoint 1 (`FUN_0053de20`). The spawn record's own
coordinates and yaw are used only on the branch where no path resolves. A roster block that authors
a path is therefore free to author coordinates nowhere near the apron, and C1/M04's four do: their
`aiv` positions sit 140 to 240 m above an airfield whose `pp1`–`pp4` waypoints are all at y=160.
⚠ **The freeze flag `+0xd4` is a separate flag from the path flag `+0xcc`.** A design folding the two
into one boolean cannot express "placed and waiting", which is the state most authored path vehicles
spend most of a mission in.
⚠ `FUN_004afd00` gates AI radio chatter on `+0xcc`, so a path-driven vehicle is silent. Do not model
the movement and leave the voice on.

**The law**, per tick, with `dt` = `DAT_009ad744`:

    target   = next waypoint, y raised by the vehicle type's ride height at type+0x218
               (a flat 0.2 m for movement classes other than 0/4)
    heading += clamp(headingError / 60°, ±1) · dt        radians, so ≥60° of error gives 1 rad/s
    speed    = 17.8816 m/s, which is exactly 40 mph      held until the final leg
    forward  = speed · (1 − |clamped heading error|)     it barely advances while turning hard
    velocity = forward, pitched by atan2(target.y − pos.y, horizontal distance to target)
               and yawed by the heading (local_5c, then FUN_0053e160/FUN_0053e1e0)
    advance the leg when dot(target − pos, normalize(target − wp[leg])) ≤ 5.0

On the **final** leg the steering target is replaced by the point **300 m** from the waypoint
BEHIND the leg along the leg's direction (`local_50 + normalize(next − local_50) · 300`), its y
gains `(speed/110mph − 0.4) · 83.3` once speed passes 0.4 of 110 mph (44 mph), and the speed term
becomes `speed += 4.0302024 · dt` instead of the fixed 40 mph. The advance test above is measured
against that replaced target, so reaching the **300 m point**, not the last waypoint, clears the
path flag (`0x0048a863`), which is the handoff to the flight model. On a run whose final leg is
shorter than 300 m the vehicle therefore flies past its last waypoint, accelerating and climbing
the whole way: C1/M02's `eag31` has a 48 m final leg, so its launch hands off 252 m past the last
point at about 53 m/s and 54 m above the strip, which is the climb-out the original's airfield
launches make.

The constants: `17.8816` is 40 mph exactly, `0.020335784` is 1/110 mph and `0.95492965` is 3/π, the
60° heading-error normaliser. ⚠ **`4.0302024` and the `83.3` climb gain were read but not
identified.** They are used as read, not as tuning knobs, and nothing may retune them; what is open
is which authored quantity they come from.

**The path source.** The roster's `taxiPath` slot ([formats/ai-rosters.md](../formats/ai-rosters.md))
names a path (`pp1`); the chapter's gamez carries it as the transform-only subtree `pp1_aipath`,
whose `pp1_aipN` children are the waypoints in ordinal order. Ten vehicles carry one, in three
missions: C1/M04 (`blakepeace_2_3`…`_6` on `pp1`…`pp4`), C2/M02 (five) and C5/M01 (one). C1/M04's
four sit on an airfield runway and its `objectives.zrd` releases them with `START_TAXI` two to three
seconds apart, which is a flight taking off one aeroplane at a time. Fixed taxi speed, a ground-height
offset, a final-leg acceleration with a climb-out and a handoff to the flight model read as the
runway takeoff run, though `FUN_004940d0` shows a goal can attach a path for any purpose. Instant
Action places no vehicle on a path (`ia.zrd.json`'s `dzpath1`–`dzpath5` are danger-zone gates), so no
golden can see it.

**What CSVM ports of this.** `Flight/Ai/PathFollower.cs` is the law with every constant above,
`Mech3/ScriptedPath.cs` the route, and `Session/World/ScriptedPathVehicles.cs` the lifecycle, released by
`CampaignDirector`'s `START_TAXI`. Pinned by the `scripted-path` suite over C1's real `pp1`.
The altitude between waypoints and the finish test are both the decode's: the motion is pitched at
the steering target's height over the horizontal distance to it, and the leg-advance test is
measured to the steering target, so on the final leg the 300 m point both steers and ends the run.
⚠ **The ride height for movement classes 0 and 4 is not identified.** Those are the aircraft classes,
so it is the one every shipped path vehicle needs, and `vehicle.json` has no field traced to
`type+0x218`. The port leaves it at zero and says so rather than reusing the 0.2 m the other classes
take.
The roster spawner calls `ScriptedPathVehicles.Place`, so C1/M04 really does put four aeroplanes on
`pp1`–`pp4` and its `START_TAXI` chain really does release them.
A surface vehicle (`mode ship`, the patrol boats) is driven by the same law for its whole life
(`Flight/Ai/SurfaceVehicle.cs`): the follower steers it over an unbounded route, a generator's
take-off run and then a walk of its net's edges, so it never reaches the final leg's acceleration
and climb-out, and its height is pinned to the water rather than taken from the route. The 40 mph
taxi speed is the only speed it has; the def's own `rates` are not consumed.
⚠ Ground blow's own emitter test reads `+0xcc`, so a spawned vehicle put
on a path stops repelling the player the moment it completes the path; ground blow shipped
without the registry filter (its player probe simply excludes aircraft), so whether a path-driven
vehicle needs to become an emitter in this build is open.

**A non-zeppelin enemy generator launches down a path of its own**, which is the same law from a
second entry. `FUN_004518d0` derives the path name from the host node with `sprintf("%.2s%.3s")` at
`0x0045197c`, the first two characters and the last three, so `eairg31` asks for `eag31` and resolves
the host-relative subtree `eag31_aipath`; the result is kept on the generator at `+0x24`, and a
non-zeppelin generator whose path is missing or holds fewer than two waypoints does not load at all.
The launch `FUN_00451bf0` then takes the same waypoint-0 placement as the roster spawner (a flat
0.2 m added to Y at `0x00451fa1` rather than the type's ride height), a zero velocity, a full 1.0
throttle lever at `+0x124`/`+0x128`, and sets `+0xcc = 1` with `+0xd4` untouched, so the aeroplane
runs the strip immediately instead of waiting for a goal. C1/M04's two airfields each author a
five-point run about 260 m long. The consumer is the same follower: `FUN_00489ea0` reads the
launch's `+0xcc` and runs `FUN_0048a110` over the generator's path at `+0xc8` from the leg index
zeroed at `+0xd0`, and `FUN_004b0f40`'s activation skips the net-nearest-node snap while `+0xcc`
is set, so the launch is placed by the run and not by its net. CSVM ports this entry in
`AiGeneratorRuntime.Spawn`: the launched aircraft is held and driven by `PathFollower` over the
path nodes' live positions from the launch pose, released into the flight model at the final leg's
300 m point at the speed and climb the run reached with the 1.0 lever, its net reseated there. The
ride height on this entry is the launch's own 0.2 m lift, decaying to the points' height over the
first leg, and the nose while held is the follower's own motion, the leg's climb and then the
final leg's climb-out.

## Collision response and `bounce_factor` (`FUN_0048d7f0`)

Decoded 2026-08-14, **impulse implemented 2026-08-15** (retiring `BL-172`), **completed for `C21`**
with the placement, the angular impulse and the partition's inertia correction below (retiring
`BL-381`): `FlightModel.BounceNormalSpeed`/`BounceRateKick` are the law and `FlightModel.Collide`
the site, gated on `IsHumanPiloted` and not-already-crashed. The sweep runs on every other sim
step, as the original's does, with the skipped step's motion carried into the next sweep
(`SweepCadence`, "What the parity is ported as" below), and every contact it resolves spends the
damage pair.

`bounce_factor` lives in `player.json`'s `crash` block, is a **raw scalar**, and
lands in global `0x0071c35c` from the parser store at `0x00473c38`. Its default is pre-set at
`0x00473bb5` *before* the block is looked up, so an absent `crash` block leaves the fallback
standing. Fallback **0.8**, this install authors **0.6**.

`FUN_0048d7f0` overlap-tests the aircraft's contact spheres (`obj[0x1a9]..obj[0x1aa]`, stride
`0x24`) at this frame's moved position and resolves the **single deepest** contact. On a contact
frame it **rewrites the caller's translation vector in place** (`0x48e065`–`0x48e0bb` player,
`0x48db30`–`0x48db7c` non-player): the frame's whole motion is replaced by whatever lands the
struck sphere exactly at its contact point, plus **0.03 m** along the normal for the player alone
(the literal at `0x006080c4`, applied `0x48dfce`–`0x48e020`; a non-player rests exactly at the
point, and the byte at `obj+0x920` skips even that at `0x48dac1`). The skipped-frame accumulator
at `obj+0x6b0` is subtracted so the placement holds across the parity, and a player under the `fd`
developer switch (`obj+0x384`, tested `0x48dfbe`) gets severity and no placement or impulse at all,
an arm no shipped play reaches. One resolution
per aircraft per sweep, no sub-stepping. **Ported** as `Collide`'s placement: the swept stop plus
`ContactPushOut` 0.03 for a human pilot, the stop exactly for an AI. The fitted graze trio
(`GrazeFriction` 0.35, `GrazeKick` 1.2, `GrazePushOut` 0.15) is retired with it: the push-out's
decoded value replaces the 0.15, and the other two stood in for terms the original does not have
(the disproof below).

⚠ **An object sweeps only every OTHER frame, on a random per-object phase.** The sentence above
said "per tick", which is wrong. `FUN_0048d7f0` returns immediately unless
`((obj[0x1af] ^ DAT_009be6f8) & 1) == 1`, that is unless the object's own phase byte at `obj+0x6BC`
matches the parity of the global frame counter. The phase is seeded from `rand()` in the entity
constructor `FUN_004aff80`, so it differs per object and per run. On a skipped frame the sweep does
not simply do nothing: it accumulates that frame's translation into `obj+0x6B0`…`obj+0x6B8` and
returns severity `0.0`, and the next sweep that does run applies the accumulated motion. This halves
the collision rate and is why two aircraft in the same contact do not necessarily resolve on the
same frame. **Ported as a sweep** (`SweepCadence`): the airframe sweeps on every other sim step,
and the sweep after a skipped step runs from the pose the skipped step entered with, so the carried
motion is swept whole and nothing tunnels through the gap. The phase is not random here: every
airframe starts on a sweeping step and a respawn restarts the phase.

### What the parity is ported as

The original spends the damage pair on every frame its sweep resolves and on no other
(`0x48ed79` gates the `FUN_0048d2c0` call at `0x48ed8b` on a positive severity, and nothing else
gates it: there is no cooldown, no per-spend timer and no grace clock on a player). So a scrape
costs one pair per two frames for as long as it closes, and that cadence is the sweep's alone.
`AircraftContactResolver` spends on every contact it is handed, and the every-other-step cadence
sits on `FlightController`'s sweep through `SweepCadence`. The retired 0.3 s `DamageCooldown` was a
wall-clock stand-in for this and made a scrape roughly nine times cheaper than the decode allows.

⚠ **The parity gates the sweep, never the spend.** An earlier port ran the sweep every step and
gated the SPEND on the parity, reasoning that a contact resolved a step early is the same contact.
It is not: a contact resolved on a non-spending step still got the placement and the impulse, which
put the airframe 0.03 m off the surface with its normal velocity removed, so a single bounce off a
building cost nothing at all and a scrape whose re-contacts landed on those steps never spent once.
At the controls that read as an airframe that grazes once and is invulnerable afterwards
(`SweepCadenceTests` holds the scrape and its locked-free control). The decode above was right and
the port was wrong; the port now matches it.

⚠ **The per-second damage rate follows the step rate, in the original as here.** The original's
cadence is frame-coupled (`fps/2` spends per second), so no port is rate-independent, and a
constant chosen to match one frame rate is a fit rather than a decode. Do not reintroduce one.

⚠ **Only the player bounces.** The impulse branch is entered only when `obj == DAT_0071c298` and the
player is not already crashed. AI aircraft get position correction and an impact cosine, and no
impulse at all. Ported as `IsHumanPiloted && !crashed`, the same widening of a single-global-
player-pointer guard to every human-piloted aircraft that `C21` recorded for the force path; pinned
by the `graze-bounce` suite, which flies a player rig and an AI rig down the same trajectory into the
same floor and measures `e = 0.56` against `0.00`.

With `r` the contact point minus `obj+0x204`, `ω` the body rates at `obj+0x16c`, and
`I⁻¹ = (obj[0x197], obj[0x198], obj[0x199])`:

```
vp    = v + 2·(ω × r)                        contact-point velocity, rotational term DOUBLED
J     = −(n · vp) · n                        normal only; no tangential or friction term
u     = (r × J) / |r|²                       zero vector if |r|² == 0
ΔL    = R-sandwiched  u / recI               the deposit is INERTIA-multiplied (FDIVs at
                                             0x48e2e9/0x48e2fb/0x48e307 by obj[0x197..0x199])
L     = 2.25 · |J|   (literal at 0x00608108, hardcoded, no data origin)
A     = |ΔL|
f_lin = L/(L+A) ,  f_ang = A/(L+A)           L == 0 → 0/1 ;  A == 0 → 1/0

v          += J  · (1 + f_lin · bounce_factor)                    0x0048e429
obj+0x160  += ΔL · (1 + f_ang · bounce_factor) · 0.5              0x0048e4bc
```

The `0.5` is the shared literal at `0x006032e0`, also hardcoded. The angular deposit goes into
`obj+0x160`, the same accumulator the stick and ground blow write to; the next frame's integrator
multiplies that accumulator back by the reciprocal moments to make rates, so the inertia weighting
cancels and the NET body-rate change is `u · (1 + f_ang · bounce_factor) · 0.5` exactly.

⚠ **The partition's angular share is the inertia-MULTIPLIED (angular-momentum-like) vector, and
the earlier `Δω = R·I⁻¹·Rᵀ·u` reading of it is withdrawn (corrected 2026-08-24 while porting the
kick for `C21`).** The three instructions are `FDIV`s by the slots `rec_moments_inertia` lands in,
so a stiff axis weighs the angular share heavier, not lighter. The partition therefore compares
two momenta, `2.25·|J|` against `|I·u|`, which is also the physically coherent reading. With the
Bloodhawk's reciprocal moments near 1.1 the correction moves `f_lin` a few points up
(`f_lin = 2.25/(2.25 + sinθ/(recI·|r|))`: ≈0.93 at a 5 m arm, ≈0.71 at 1 m), so every direction
claim below survives it unchanged. Ported: `BounceImpulse` divides by `RecInertia` for the share
and applies the net kick with no inertia factor; `BounceRateKick` exposes it and `Collide` spends
it on the body rates in place of the retired fitted `GrazeKick`.

Effective normal restitution for a non-rotating contact is **`f_lin · bounce_factor`**, bounded by
`[0, 0.6]` as authored.

⚠ **The partition runs the opposite way round to the summary sentence this decode has been quoted
with ("a short lever arm rebounds at up to 0.6 while a wingtip throws most of the impact into
rotation"). Corrected 2026-08-15 while implementing it.** `u = (r × J)/|r|²` has magnitude
`|J|·sinθ/|r|`, which **falls as 1/|r|**: the `/|r|²` is a point-mass moment of inertia, not a
lever. So `A` shrinks as the arm lengthens and `f_lin = L/(L+A)` rises toward 1, a wingtip rebounds
HARDER than a contact near the centre, and nothing here converts a wingtip strike into spin. With
the inertia-multiplied share it reads `f_lin = 2.25/(2.25 + sinθ/(recI·|r|))`: ≈0.93 at a 5 m arm,
≈0.71 at 1 m, exactly 1 when `r ∥ n` (a contact directly under the centre of mass, where `r × J`
vanishes). The formulas above are unchanged; only their reading was wrong.

⚠ **The impulse is not a rigid-body impulse, and that defect is the mechanism behind `CAP-14`'s
split.** It is computed from the *contact point's* velocity, with the rotational term doubled, then
applied in full to the *centre of mass* with no reaction term removing the rotational share. Taking
the normal component with `k = f_lin · bounce_factor`:

> `n·v_after = −k·(n·v) − (1 + k)·2·n·(ω × r)`

The first term is the bounded restitution. The second is unbounded and is not restitution at all:
whenever the contact point closes faster than the centre of mass, which is the normal case for a
belly or nose contact carrying any nose-down pitch rate, the aircraft leaves the surface faster than
`bounce_factor` permits.

⚠ **There is no surface dependence anywhere in the code.** No test on the normal's verticality, no
per-surface-type table, no material lookup, no friction term. `CAP-14`'s measured split (0.75–0.86
on flat ground against 0.06–0.18 on vertical faces, `BL-172`) must therefore never be implemented as
a per-surface coefficient.

⚠ **The lever-arm partition does not produce that split either, and the claim that it does was
withdrawn 2026-08-15 on the arithmetic above.** With `A ∝ 1/|r|`, `f_lin` is ≈0.9 in both
orientations at any contact geometry an airframe actually presents, so the two rebound at nearly the
same coefficient: the `graze-bounce` suite flies both and measures `e ≈ 0.56` on flat ground against
`e ≈ 0.59` on a vertical face (both move a point or two with the partition's inertia correction
above). What differs on a wall is the AXIS, the rebound is horizontal, so an
altimeter reads nothing across the contact (measured `vy 0.00 → 0.00` there), and that, not a
coefficient, is what a vertical-face clip shows. The flat-ground magnitude above `bounce_factor`
still comes from the doubled rotational term, which `bounce_factor` cannot produce.

⚠ **The contact path has no tangential term, no friction and no vertical-speed edit anywhere, and
that closes `BL-381`'s open question as a disproof.** The path is traced end to end: the per-frame
integrator `FUN_0048e580` builds forces (`FUN_0048c470`), integrates `v += a·dt` into `obj+0x924`,
turns it into the frame's translation, calls the sweep at `0x48ea17` (which may rewrite that
translation and change `v` only through the normal impulse above), adds the translation to the
position, and hands the returned severity to the damage law `FUN_0048d2c0` at the very end, which
writes no velocity at all. So nothing in the executable removes a wall-tangential sink from the
VELOCITY: what `CAP-14` measured is position-derived, and the placement rewrite is what produces
it, cancelling the whole frame's motion against the (drifting) contact point while the stored
velocity keeps only its normal edit. The filmed multi-tick scrape (144.5 → 86.7 mph over 0.47 s)
is the same two mechanisms iterated: the plant keeps steering into the wall, each resolved sweep
frame re-places the aircraft at the surface and the impulse re-spends the re-accumulated closing
component, with no per-contact friction anywhere. `CollideResponseTests` pins the exact per-tick
outcome (`speed' = speed·√(cos²θ + (bounce_factor·sinθ)²)`), the tangential exactness a friction
term of any size fails, and the no-re-steer control that stops bleeding entirely. Damage repeats
only while the severity cosine stays positive, so a scrape that has stopped closing spends
nothing further.

**Ruled out as sources of the excess, each traced:** multiple resolutions per frame (one per aircraft
per tick, `FUN_004897c0`'s head); successive-frame stacking (once the contact velocity is outgoing
`J` points back *into* the surface, so repeats damp rather than compound); a separate ground-support,
landing or gear path (a whole-image scan for float stores to `[reg+0x160]` returns exactly one site,
inside `FUN_0048d7f0` itself; the `Landing` classes at `0x00625790` are mission landing-zone volumes
and `touchdown_` is only a name prefix); and gravity ordering (the resolver is the **last writer of
velocity in the frame**, so lift and weight cannot add to the rebound within the contact frame). The
0.03 push-out is real but is a position placement, worth roughly 0.9 m/s of spurious upward velocity
in a single inter-frame interval at 30 fps, and it does not accumulate.

**Two per-object timers**, both absolute seconds against the game clock at `0x0071c470`:

- **`obj+0xAC`, a collision grace window.** `FUN_0048d7f0` returns immediately while the clock is
  inside it (`0x0048d7fd`), so the object has **no collision at all**, and the AI's ground blow is
  skipped too. Set to clock + **1.5** at spawn (**5.0** on the alternate placement path,
  `0x0045243c`), and to clock + **1.0** on **both** parties after an entity-versus-entity impact
  (`0x0048d383`, `0x0048d395`), the same branch that cuts that impact's damage terms to 20 %
  ("Collision damage" below).
- **`obj+0xB4`, a post-drop settling window.** Clock + **2.5**, written on the spawn paths only.
  `FUN_00452450` gives the context: the entity is repositioned, yawed to −π/2, and given the launch
  velocity minus 22.352 m/s vertically, which is a drop from a carrier at 50 mph. `AiGeneratorRuntime`
  samples the host's live velocity each sim step and seeds this vector after the released aircraft's
  respawn, so the generic spawn speed cannot overwrite it. The same routine seeds both throttle fields
  to **0.1**, which `FlightController` carries through the release rather than its ordinary spawn throttle.
  The first 1.5 s overlaps `+0xAC`, so ground blow is completely absent; only the final second runs
  at 15 %.

Neither is a damage-invulnerability timer; `obj+0xAC` disables collision itself.

## Collision damage (`FUN_0048d2c0`)

The section above is the response; this is the damage. `FUN_0048d7f0` returns an impact severity and
`FUN_0048d2c0` turns it into a damage pair. **Ported**, as `Flight/Airframe/CollisionDamage.cs` with the
authored ranges read in `PlaneStats` and the contact resolved in `AircraftContactResolver`;
the struck party's damage runs through `AnimRuntime.CollideDamageAt` and the striker's through
`PlaneDamage.Apply`. One part of the section below is knowingly not ported and says so where it
appears: the object's own `+0xbc` damage reduction.

**The severity is a cosine.** `FUN_0048d7f0` normalises the velocity through `FUN_00422690` (at
`0x0048df8b` on the player path, `0x0048dba0` / `0x0048dbba` otherwise) **before** dotting it with
the contact normal, and returns `s = -(v̂ · n̂)`, dimensionless and in `(0, 1]`. Nothing registers
unless `s > 0` (`0x006032c8`).

```
armorDmg = healthDmg = max(300 · s³, 50)          cube at 0x0048d4c1
                                                   scale  0x0071c34c / 0x0071c354
                                                   floor  0x0071c350 / 0x0071c358
if the striker is not the player and it hit an aeroplane:  both × 0.2
```

**The camera kick is the same function's other law, and it is not the pair's.** Before the pair is
computed, `0x0048d3cc`–`0x0048d409` kicks shake block 5 with
`min(speed · s · 0.03, 0.15)` radians: the true airspeed at `obj+0x934`, the RAW severity cosine
rather than its cube, the literal `0.03` at `0x006080c4` and the ceiling `0.15` at `0x006036a8`. Two
guards stand over it and nothing else does: the object is the player (`0x0048d3c4`) and its `fd`
switch `obj+0x384` is clear (`0x0048d3aa`), which it always is in play, so every contact the
caller's positive-severity gate lets
through kicks the camera, a graze included. At any flight speed the ceiling is reached by a cosine
around 0.05, so the shallowest contacts already saturate. **Ported** as
`CollisionDamage.ContactShake` feeding `PlaneShake.ContactHit`, widened from the player to every
human pilot the way the bounce is; the oscillator law block 5 kicks is in
[`shakes.md`](shakes.md).

⚠ **There is no airspeed term anywhere in the damage pair** (the shake above is the one place in
this function that reads speed, and it spends no damage). A 400 mph belly-flop and a 90 mph belly-flop at the
same attitude deal identical damage. The whole law is a function of the angle of incidence, which is
why a remake that scales collision damage by closing speed cannot be made to match by retuning a
constant. The floor dominates below `s ≈ 0.550`, so any contact shallower than about 33° off the
surface deals a flat 50.

The four numbers are **data, not constants**: they are `player.json`'s `crash` block, the same block
`bounce_factor` comes from, parsed by `FUN_004735b0` under keys `armor_damage_range` (`0x006275a0`)
and `health_damage_range` (`0x006275b4`), with element 0 landing in the floor slot and element 1 in
the scale slot. Compiled fallbacks are `[15, 200]` (`0x00473b8d`, `0x00473b97`, `0x00473ba1`,
`0x00473bab`); this install authors `[50, 300]` for both, so the fallbacks never stand. This is the
same authored-vs-fallback split the "Authored values vs the executable's fallbacks" section covers.

**A collision is delivered as a weapon hit.** `FUN_0048d2c0` looks up the weapon record named
`wep_24` (string `0x00628a14`, by-name scan `FUN_005abfd0`, stride `0x214`) and hands the pair to
the ordinary applier `FUN_005abcf0` at `0x0048d55b`, the same one a rocket uses. The call is gated
only on the sweep having resolved a node (`contact+0x24 != 0`): there is **no** test on object
class, on a destructible's `ACTIVATION`, or on whether the striker survives. Two consequences
follow, and both are structural rather than incidental. A rammed object dies through its ordinary
`DAMAGE_SEQUENCE` thresholds, so collision damage is severity-scaled rather than an instant kill.
And any gate that sits downstream of the applier catches collisions for free, which is exactly how
the gasbag exemption works: the gasbag damage handler `FUN_004c0640` tests `DAMAGES_ZEPPELIN`
(`weaponRecord+0x210`, flags `& 0x1000`, `TEST AH,0x10` at `0x004c0665`) before subtracting
anything, `wep_24` does not carry the flag, and so ramming a gasbag deals it nothing while still
killing the plane. The zeppelin cannon and turret handler `FUN_004c0880` has no such test and
subtracts at `0x004c090b`, so every zeppelin part except the gasbags is rammable.

**Both parties are damaged, by the same law.** The struck party takes the pair on the `wep_24` path
above; for an aircraft target that runs `FUN_004b9750` → `FUN_004b9770` → `FUN_004b9b30` →
`FUN_004b9bc0` and lands on the identical primitives the striker applies to itself. The striker then
takes the same pair: per-part first where the airframe has a part list (`FUN_004b3950` picks the
struck node's own part, else the part nearest the contact; `FUN_004b3bf0` at `0x0048d724`), then the
remainder in aggregate (`FUN_004b8070` at `0x0048d783`). The armour-to-health split is
`FUN_004b7f80`: with `f = min(1, armor / armorDmg)`, armour drops by `armorDmg` floored at zero and
health drops by `(1 - f) · healthDmg`, so a **fully-armoured contact costs no health at all**.
Survival is then `health > 0` (`obj+0x2d0`, tested `0x0048d78b`).

⚠ **Those two tests are the WHOLE death rule on contact, and three CSVM inventions died against
that.** `local_11` and `health > 0` are the only inputs to the destruction call at `0x48d7cc`;
nothing on the path reads a speed, a vertical speed, a slide, an overlap or an attempt count.
- **A speed threshold does not exist.** The damage law carries no airspeed term at all, and the
  integrator hands it a cosine. CSVM's `CrashSpeed` 25 m/s is removed. It had already decayed into
  a log line inside the no-damage-data arm, which crashes at any speed, and no shipped airframe
  reaches that arm (`FlightConstantInventoryTests.NoStockAirframeFliesWithoutADamageLedger`: every
  player load authors four zones, every AI load an armour/health pair).
- **A ground-stop speed does not exist.** CSVM's `GrazeStopSpeed` 12 m/s is removed. It guarded a
  plane grinding along the ground collecting free contacts, and the decoded law makes that
  unreachable on its own: a contact that closes at all costs at least the authored floor (50 here),
  the pair re-spends on every sweep step for as long as the scrape closes, and the striker's pool is
  bounded, so the ledger runs out and the decoded health rule ends the slide. The impulse edits
  only the normal component, so a slide keeps its tangential speed and dies long before it could
  grind to a halt (`AircraftContactResolverTests`, with the spends-nothing control beside it).
- **An embed rule does not exist**, because the original's placement cannot produce the state: it
  lands one sphere exactly at its own contact point. A swept multi-box airframe can stay
  overlapping, so `AircraftContactResolver`'s un-embed loop and its destruction after three
  failed pushes are kept as a named product exception, bound by that suite's own row.

⚠ **An invulnerable striker still destroys what it hits.** The `obj+0x920` early-out at `0x0048d563`
sits *after* the struck object has been damaged, so invulnerability protects the rammer only.

⚠ **The player is deliberately asymmetric, in three ways that travel together.** A player-owned
striker jumps past the entity-detection block at `0x0048d2ed` entirely. So the player never takes the
0.2 cut that scales an AI's ram to a fifth (`0x0048d51a`, `0x0048d526`); the player writes no
`obj+0xAC` grace clock, so the contact can re-resolve on following frames while the two aircraft are
still overlapped; and the player is never subject to `local_11`, the flag that forces destruction
regardless of remaining health (`0x0048d79e`). `local_11` is set for a non-player striker that did
*not* resolve an aeroplane, so an AI that rams terrain or a building always dies, while an AI that
rams another aircraft survives if health remains. Entity detection requires node flag `0x40000000`
and a dispatch class at `+0x67c` of 0 or 4, the two aeroplane classes (`0x0048d360`). This is the
same single-global-player-pointer guard the bounce impulse uses, and widening it to every
human-piloted aircraft is the same decision `C21` recorded for the force path.

**What a rammed destructible actually takes.** The handler is `0x004e7220`, registered per
animation record by `FUN_005230d0`, the animation-definition loader (`D:\zipper\gamez\zEffect\zeff_ani…`,
`0x0062ee28`), which is the right place because a destructible IS an animation definition
([`../formats/destructibles.md`](../formats/destructibles.md)). Records live in the array at
`DAT_009fd14c`, count `DAT_009fd14a`, stride `0x110`, and the registration is keyed on the record's
`+0xa1` byte: 0 registers slot 0 only, 1 registers slot 1 only (`FUN_004e7150`), 2 registers both.
`FUN_005abcf0` dispatches through **slot 0**, and `0x004e7220` re-checks the byte itself, accepting
`+0xa1 ∈ {0, 2}` and refusing everything else (`0x004e7234`). That byte is the `ACTIVATION` enum,
with `WeaponHit` 0 and `WeaponOrCollideHit` 2, so a ram damages **both** kinds and the enum decides
only the plane's fate.

With `+0xb4` the max health, `+0xb8` the current pool and `+0xbc` a per-object damage reduction:

```
net = healthDmg - animRecord[+0xbc]      subtracted, and written BACK into the pair (0x004e7259)
if net > 0:  animRecord[+0xb8] -= net
             re-evaluate DAMAGE_SEQUENCE (FUN_004e71e0)
             dead at +0xb8 <= 0 -> death sequence FUN_004ed730
```

⚠ **A destructible consumes the health term only.** `armorDmg` is never read on this path, and
`healthDmg` is reduced by the object's own `+0xbc` before anything is subtracted. **`+0xbc` is not
ported**: our destructible data carries no equivalent field, and the law reproduces the original's
observed graze and ram outcomes on C1's 60 HP buildings without it, so it is zero or near zero on
those defs. Every non-aircraft
handler behaves this way (the two zeppelin handlers above, and clutter's `0x004df420` from
`FUN_004deab0`, `D:\zipper\gamez\zclass\cls_clutter.cpp`); only the aircraft handler `0x004b9750`
consumes both through the armour split.

⚠ **The `+0x400` gate refuses damage when the bit is SET, not when it is clear.** `0x004e7227` tests
`weaponRecord+0x74 & 0x400` and jumps *into* the damage body when it is zero (`JZ` at `0x004e722a`
over the `XOR EAX,EAX; RET` at `0x004e722c`). Bit `0x400` is the `FREEZE` impact type, set only by
that keyword in the weapon parser `FUN_005ad630`, paired with `0x40` `HEAT` and `0x1000` `DESIGNATE`.
No shipped weapon carries `FREEZE`, and `wep_24` in particular does not, so collisions do reach
destructibles. Read the other way round this would say collisions damage nothing, so the direction
matters.

⚠ **A ram borrows the HE rocket's record, so it borrows its impact effects.** `wep_24` is
`MSG_WEAP_HEXPLOSIVE_ROCKET`, `NAME "BOOM"`. Its authored `ARMOR_DAMAGE 100` / `HEALTH_DAMAGE 100`
are NOT used (`FUN_005abcf0` forwards the caller's pair, which `FUN_0048d2c0` filled with the
severity law above), but its `IMPACT` block still fires: ramming a building plays `large_fireball`,
ramming water plays `bsplsh.flt`. A remake that models the damage without the effect will look
wrong on contact.

**What the sweep can hit.** The sweep removes the object's own node (`gwNodeSetActive`,
`FUN_004cca30`, bit `0x4` of `node+0x24`) and then queries the world through `FUN_004ca320`, or
`FUN_004c8f70` for a single probe. That query has two halves: a terrain-grid walk over the cells the
swept AABB covers, and then an unconditional loop over the world root's DIRECT CHILD list
(`root+0x5c`, count `root+0x56`) carrying **no spatial test and no class test**, admitting a node on
bits `0x4` and `0x10` plus the layer filter `FUN_0056c430` and recursing into its children through
`FUN_004cad00`. `FUN_004dae80` decides which of those two lists an object registers into by whether
its cell range spans more than one cell, so a large moving object lives in the root child list and is
descended into on every sweep.

This is why a zeppelin's sub-parts are rammable, which they are here too: a cannon mount takes ram
damage and a gasbag takes none, the exemption holding because a ram carries no `DAMAGES_ZEPPELIN`
ordnance. They are ordinary descendants of the zeppelin root
in the hierarchy the recursion walks; `gwNodeNew` stamps every node `node+0x24 = 0x0108001C` and
`node+0x30 = 0xFF` at birth, so both predicate bits and the layer filter pass by default, and neither
the zeppelin parser nor its two part-registration helpers clears them on a part. There is no
"static world geometry only" filter anywhere on the path.

**Open:** what writes dispatch class 1 to `entity+0x67c`. Class 1 runs `FUN_0048ffe0`, which sweeps
at `0x00490441` but never calls `FUN_0048d2c0`, so class-1 objects take position correction and no
collision damage at all; every write to that field found so far stores 0.

## The `*Tune` rates, none of the three exists in the executable

The remake's per-axis control-rate calibration. The steady stored half-angle rate is
`torque · rec_moments_inertia · Tune / ang_momentum_damp` (× the yaw authority curve on yaw). The
physical rate is **twice** that value because the attitude is built through the quaternion
exponential above.

⚠ **No axis carries a calibration factor.** Read out of the LIVE force function `FUN_0048c470`, the
three stick torques are built in three guarded blocks and each is exactly four factors, with nothing
else multiplying in:

| Axis | Guard | Chain | `dt` multiply |
|---|---|---|---|
| Roll | `obj+0x114` ≠ 0 | `roll_torque [+0x644] · stick [+0x114] · rollAuthority · dt` | `0x48caa2` |
| Pitch | `obj+0x11c` ≠ 0 | `pitch_torque [+0x648] · stick [+0x11c] · pitchAuthority · dt` | `0x48cb19` |
| Yaw | `obj+0x120` ≠ 0 | `rudder_torque [+0x64c] · stick [+0x120] · yawAuthority · dt` | `0x48cbd5` |

The roll chain is visible instruction by instruction at `0x48ca8d` (`FLD [ESI+0x644]`), `0x48ca96`
(`FMUL [ESI+0x114]`), `0x48ca9f` (`FMUL [EBP-0x18]`, the authority scalar) and `0x48caa2`; the pitch
chain the same way at `0x48cb07` and `0x48cb19`. `rec_moments_inertia` and `ang_momentum_damp` enter
downstream in the integrator, as "Torques and the limiters" describes. Pitch and yaw differ from roll
only in which authority scalar `FUN_0048bdd0` hands them and in the opposing-command test, neither of
which is a constant factor.

So the authored numbers are the whole of it on every axis. The Bloodhawk's `roll_torque 7.5` and
`rec_moments_inertia.z 1.10` against `ang_momentum_damp 5.0` give **90.7 °/s stored**, **181.4 °/s
physical**, and a stepped 360° roll in **2.18 s** against the decoded 2.08 s target and the
original's 2.05 s ADI stopwatch reading.

| Constant | Value | Standing |
|---|---:|---|
| `PitchTune` | was **0.89** | **No counterpart in the binary**, now 1. With the quaternion correction the sustained physical pitch rate is **24.01 °/s**; faster nose motion opens the AOA window further, so this axis is not a simple 2× output |
| `YawTune` | was **1.57** | **No counterpart in the binary**, now 1. With the quaternion correction yaw-360 is **30.45 s** against the original's 28.60, rather than the invalid half-angle reading of 49.12 s |
| `RollTune` | **1.0** | already retired on this evidence; a 2.12 that used to sit here is gone |

The retired 2.12 `RollTune` almost exactly compensated for the missed quaternion double angle. Its
agreement with the original was evidence about the decoded integrator, not permission to discard
the measurement. Pitch and yaw still carry no calibration factor; their remaining behavior comes
from the same decoded limiter and weathervane terms as before.

⚠ **`yaw-360` is now INFORMATIONAL, and the 28.6 s was NOT rewritten.** The footage figure stays
recorded exactly as measured, but it does not gate anything: the remaining 1.60 s disagrees with the decode, and a
frame-derived duration cannot refute one (`docs/verification.md` DET-12). The row joins
`accel-150-290` and `decel-290-150` as informational rather than as a target refitted to whatever
the model now produces. Do not restore a multiplier to chase the residual.

⚠ **One golden moved with this: `c1-flight`, re-pinned.** It flies `--hold=0.2,0.1,0,1`, held pitch
and roll, zero rudder, so it moved on the pitch change alone, and the other fifteen shots are
untouched, which is what confines the change to the control rates. The capture was inspected before
the re-pin: same scene, same HUD, a different flown attitude.

⚠ **This halves the roll rate of every airframe, the player's included**, since the other ten move
with the Bloodhawk as they always have. It is a decode correction and not a feel change, so the
at-the-controls read of it belongs in a playtest rather than in a re-tune.

**The video also closed an open question: the original's pitch rate does NOT fall off with speed.**
Binned round a loop it reads 37.9 / 33.7 / 30.7 / 36.5 °/s over 120–280 mph, flat within the
noise, so speed-independent pitch is right, and this is the measurement behind "pitch carries no
high-speed fade" above.

The refits are **Bloodhawk-pinned**, as they always were; the other ten airframes have no measured
target of their own and simply move with them.

⚠ **Do not chase the pitch transient's residual roll-off through these constants.** They set the
STEADY rate, which matches; a transient chased through them breaks the thing that currently works.
The square-wave cadence sweep is the measurement that belongs to that gap, see the stick-ramp
section's landing note.

## Nitro, the boost lifecycle, traced whole

The force side was known (the flag `[obj+0x947]` replaces the lever with 1.8 and sets the drag
multiplier to 0.8 at `0x48fcb6`–`0x48fcbd`); this section is the rest: who sets the flag, what
charge it spends, and what the data does and does not author. The dynamics are executable-resident
constants, and every one of them is listed here with its address.

**The charge is a 30-unit tank burned at 4/s and refilled at 1/s.** The vehicle constructor
`FUN_004aff80` writes the four slots at `0x4b02c4`–`0x4b02e9`: the cap `[obj+0x8b4]` = 30.0
(`0x41f00000`), the charge `[obj+0x8b8]` = 30.0 (spawns full), the burn rate `[obj+0x8bc]` = 4.0
and the recharge rate `[obj+0x8c0]` = 1.0. Nothing else writes any of the four, and no data key
reaches them: the vehicle-def token table has no nitro token, and `vehicle.json`/`player.json`
author none. The per-vehicle update `FUN_0049f6a0` spends them at `0x49f810`–`0x49f89f`, every
frame for every aircraft:

```
if boosting:  charge -= dt · 4                       ; 0x49f810–0x49f826
charge += dt · 1                                     ; 0x49f82c–0x49f83e, unconditionally
charge = clamp(charge, 0, 30)                        ; 0x49f84a–0x49f87a
if charge < 0.05 · 30:  SetNitro(0)                  ; 0x49f882–0x49f89f, the 5 % cutoff
```

The recharge line is not gated on the boost, so the net burn while boosting is **3/s**: a full
tank runs 30 → 1.5 in **9.5 s**, and the refill from the cutoff back to the re-arm line (below)
takes **28.2 s**. Boosting burns no fuel: the fuel line at `0x48e603` reads the lever, which the
boost does not touch.

**Activation is a one-shot.** The human handler `FUN_00487460` tests the command at
`0x487e91`–`0x487efc`, after the eight-notch throttle quadrant, with command index `0x12`
(`MSG_CMD_NITROUS`, "Use Nitro-Booster"):

```
if !installed [obj+0x946] or charge < 0.05 · cap:   SetNitro(0)       ; 0x487e91–0x487eb2, 0x487ef9
elif charge < 0.99 · cap:                            ; 0x487eb4–0x487ecb
    if boosting: SetNitro(1)   else: nothing         ; 0x487eeb–0x487ef7
else (charge >= 99 %):
    if command pressed or held: SetNitro(1)          ; 0x487ecd–0x487ee9
```

The two fractions are the literals at `0x6034d8` (0.05) and `0x6080a8` (0.99), and the same
0.05 literal is the cutoff in the vehicle update.

So the key engages the boost only from a tank at or above **99 %**, and once engaged there is no
input that stops it: the middle arm re-asserts the flag until the 5 % cutoff turns it off, and
releasing the key does nothing. A burn is therefore always the full 9.5 s, and the tank must refill
to 99 % before the next one. The player-only shake (block 6, `docs/org/shakes.md`) and the
`NitroStart` force-feedback effect (`FUN_004814f0`, `0x4b21f4`) ride the engage edge.

**The state machine is `FUN_004b2110(want)`**, a vehicle method with seven callers. Per call:

```
if want and engine out ([obj+0x2dc] & 2):            refuse (return)     ; 0x4b2136
if want and anim handle [+0x288] != 0 and !active:   want = 0            ; 0x4b2143–0x4b2153
timer [+0x28c] += dt;  boosting [+0x947] = want                          ; 0x4b2157–0x4b2169
if !want:                       active [+0x27c] = 0                      ; 0x4b21fb
elif anim handle == 0:          active = 1; timer = 0                    ; 0x4b2181–0x4b219a
    play nitro_boost def [+0x280] on the plane node;  animAlive [+0x27d] = 1
    non-player: play medium_aishake (FUN_00473430(1))                    ; 0x4b21b2
    player: shake block 6 with nitro.magnitude; NitroStart force feedback
if anim handle != 0 and !active and animAlive and timer > 1.0 [def+0x188]:
    stop the boost anim; animAlive = 0                                   ; 0x4b221b–0x4b2248
    play nitro_decay def [+0x284], its completion clearing the handle    ; 0x4b2250–0x4b2271
if snd_nitro [def+0x184] and animAlive:
    keyed 3D loop at the plane, refreshed for 0.1 s (FUN_0045e470)       ; 0x4b2279–0x4b22eb
```

The two def slots are resolved at vehicle load in `FUN_0047c210` by name: `nitro_boost` and
`nitro_decay` (`0x62836c`/`0x628378`, stored at `0x47c792`/`0x47c7a0`), the ON_CALL defs
`plane_props.zrd` authors; `def+0x184` is `snd_nitro` looked up by literal (`0x627f5c` at
`0x47a827`) and `def+0x188` is the constant 1.0 (`0x47a838`), the minimum life of the boost
animation after an engage. The `ai_nitro_*` wrappers in the data are not referenced by the
executable; the AI plays the same two defs. The loop sound expires 0.1 s after its last refresh,
and the refresh sits inside `SetNitro`, so it plays for as long as something calls the method
every frame: the human handler does (one of its three arms fires on every frame while the
injector is installed), the AI path does not (below). The engage timer advances only inside the
method as well, so for an AI the boost animation and the loop outlive the maneuver by one second
of the per-frame release calls that follow it, and the loop is otherwise a 0.1 s blip at the
engage. A re-engage is refused for as long as the boost or decay animation is alive.

**The boost animation is anchored per vehicle, and that is why the disc swap never showed on a
flyable airframe.** The play at `0x4b21a0` is `FUN_004edda0(def [+0x280], [obj+0xc], 0, 0, 0)`: the
def slot and the vehicle's OWN scene node, which is the only scope the instance is ever given (the
AI shake at `0x4b21b2` hands `FUN_00473430` the same `[obj+0xc]`). The call reaches the
instantiation through `FUN_004edc50`, then `FUN_004ed8c0`, which clones the definition
(`FUN_00520910`) and binds the clone to that node (`FUN_00521180`). Three separate rules decide
what a name reaches, and none of them is a global search over the world for this class of def:

- **The def's own anchor NAME is resolved once at load, not per play.** The loader calls
  `FUN_004efaf0` at `0x51e15a` and stores the result at `def+0x6c`. When the NAME resolves nothing
  it warns (`zeff_anim.cpp:0x2ec6`) and sets `def+0x6c = def+0x48`, the definition's own root, then
  copies the definition's own name over the anchor name (`0x51e16d`–`0x51e1b2`).
- **A clone anchors on the node the caller passed whenever the template's anchor IS its root.**
  `FUN_00520910` compares the template's `+0x6c` against its `+0x48` (`0x520a2e`): equal takes the
  caller's node directly and adopts its name; otherwise it searches THAT node's subtree for the
  authored NAME with `FUN_004efa70` (`0x520a5a`), and a miss kills the instance outright (state 5,
  `zeff_anim.cpp:0x33be`). `FUN_00521180` applies the same rule on a re-bind (`0x52122e`,
  `zeff_anim.cpp:0x3524`). `FUN_004efa70` is a recursive depth-first name compare over one subtree
  (the node's name at offset 0, children counted at `+0x56` and listed at `+0x5c`) and has no
  global tier at all.
- **A per-event target name walks a five-tier cascade whose last tier is gated by the data.**
  `FUN_004efaf0` tries the anchor subtree (`+0x6c`), then the instance's bound node subtree
  (`+0x48`) when the two differ, then two instance-local tables (`FUN_004ee7e0` over `+0xec`,
  `FUN_004ee770` over `+0xf4`), and only when its third argument is zero the global by-name lookup
  `FUN_004d0280(7, name)` over the class-7 object registry. That argument is the
  `LOCAL_NODES_ONLY` bit: the token sets `0x200000` in the definition's flag word `+0x9c` at
  `0x51f0ec`, and `0x51e14b`–`0x51e157` shifts it down by 21 and pushes it.

`nitro_boost` and `nitro_decay` are authored `LOCAL_NODES_ONLY`, so their global tier is closed and
every name they touch must lie inside the flown vehicle's own subtree. No flyable `player_*`
airframe carries a `nitropropN` node, so the authored disc swap (`OBJECT_ACTIVE_STATE` and
`OBJECT_OPACITY_FROM_TO` on `nitropropN`, the `spin_nitrorotorN` motions, `snd_nitrostart AT_NODE
nitroprop1`) reached nothing on a flown aircraft in the original either; the discs sit on the
separate bare-named library root, which no per-vehicle resolution can see. What a flown airframe's
own nodes do carry is the rest of the definition, and that is what an engage shows: the
`nitropuffN` exhaust puffers at `exhaust1..4` and the `prop1..3` opacity fade, each authored to run
for one second (every `PUFFER_STATE … ACTIVE` is paired with its own `INACTIVE` at
`ANIMATION_OFFSET 1`, alongside the 1.0 s ramps and the 1.0 s minimum animation life at
`def+0x188`).

**Eligibility is the injector flag `[obj+0x946]`, set from the engine choice.** The player's
comes from the hangar pick: engine ids 3–5 (the "… nitro" variants, `docs/org/hangar.md`) set it
at `0x47d4f0` in `FUN_0047c210`, the wingman/MP mirror at `0x47e8d4`, and the debug console's
"You now have the nitrous injector" at `0x43dd99`/`0x43de2c`. An AI's comes from its roster
block's `nitro` slot, copied by the spawner `FUN_00475820` at `0x475c9a`. The gauge
(`nitrogauge`, `FUN_00456a40` at `0x49f8b7`) is shown only when the flag is set, and
`FUN_004aff80` zeroes both flags at `0x4b0393`/`0x4b0399`. Losing the engine
(`FUN_004b1690` with bit 2) calls `SetNitro(0)` at `0x4b16f1`, and an engine-out aircraft cannot
re-engage.

**The AI boosts with a nitro-flagged maneuver.** The maneuver starter `FUN_004201a0` calls
`SetNitro(1)` at `0x420928` when the chosen maneuver's `nitro` flag (`0x71b215 + 0x1c·i`, the
library's one flagged entry is `nitro_evade`) is set; a flagged maneuver is not selectable at all
without the injector or with the engine out (`0x4202d5`–`0x4202ea`). The per-frame vehicle loop
`FUN_004897c0` calls `SetNitro(0)` at `0x4899d1` for every non-player aircraft in AI mode 0 or 4
that is not executing a nitro-flagged maneuver (mode `[obj+0x358]` 1), so the AI's boost lasts
the maneuver or the tank, whichever ends first. There is no 99 % gate on the AI arm: a
nitro-flagged maneuver engages from any charge above the cutoff.

**The gauge feed is `FUN_004568c0`**, called from the vehicle update at `0x49f8ca` with
`charge / cap` and the boost flag, for any aircraft with the gauge node set (`[obj+0x4e0]`, the
cockpit's `nitrogauge`). The `nitro_boost` needle's third Euler component chases −3.7699 rad
(−216°) while boosting and 0 otherwise, through the shared exponential `FUN_00460490` at rate
3/s; the `nitro_charge` needle chases `(1 − charge/cap) · 3.7699` at rate 1.5/s. The
`MSG_HUD_NITRO` text ("Nitrous: boost: %1 charge: %2", id 190) is written only under the debug
HUD flag `DAT_00624df0`, with boost as the needle angle × −26.5259 (100 at full sweep) and charge
in percent; it is a debug readout, not a shipped HUD element.

**The AI's shake and its loop sound both ride the same call.** `FUN_00473430(this, 1)` at
`0x4b21b2` plays `medium_aishake` on the vehicle's own node where a person gets shake block 6, one
shake at a time per aircraft; the shake player itself, and the four other kicks that carry the same
AI twin, are in [`shakes.md`](shakes.md), "The seven component blocks and every kicker". The loop
sound is keyed rather than driven: the refresh at `0x4b2279`–`0x4b22eb` gives it 0.1 s (the
argument at `0x4b22b8`) and sits inside the method, so the AI arm sounds it at the engage and again
through the per-frame release calls that follow the maneuver, and not during the maneuver itself,
where nothing calls the method.

**What CSVM implements.** `Flight/Airframe/NitroSystem.cs` is the state machine above, engine-free:
tank, burn, recharge, the 99 % arm, the 5 % cutoff, the engine-out refusal and the boost-animation
edges, with `LoopRefreshedThisTick` standing for the refresh call. `FlightController.AdvanceNitro`
plays `medium_aishake` on a non-human pilot's own rig (`EffectCatalogue.AiShakeAnim`, guarded by
the runtime's own `ANIM_STATE` as the original is guarded by its one handle), drives the AI's
positional loop through `AiEngineAudio.RefreshNitroLoop`, and ends the re-engage lockout when the
`nitro_decay` INSTANCE ends rather than on a clock of its own, which is one sim step behind the
original's completion callback because the step polls once. `nitro-ai-edges` measures all three.
⚠ The original has no edges: the play, the player shake and the force-feedback effect all
run inside `SetNitro` itself, so CSVM's `EngagedThisTick`/`ReleasedThisTick` stand in for that one
call and must survive from the arm that raises them to the reader at the end of the same step.
`NitroSystem.BeginStep` is where they are cleared, at the top of `FlightController.AdvanceNitro`
and nowhere later; clearing them in the tank update deletes the engage, which costs the boost its
animation, its shake and its loop sound while the aircraft still accelerates. The plan's Decision 3 widens the player-only arms (the human command path, the shake) to
every human pilot. The selection-side gate is `AiModeMachine.NitroUsable`, wired to the flown
aircraft's injector and engine: a nitro-flagged entry is culled from the picker's draw without it,
which is what keeps a pilot that cannot boost from answering a hit with six wings-level seconds.
The force couplings are `FlightModel.BoostLever` 1.8 and `BoostDragFactor` 0.8,
reached through `FlightInput.Boost`; the far-field cruise target reads the lever, not the boost,
so a distant AI's `nitro_evade` changes nothing there, which is the decode
(`0x48c5a0` reads `[obj+0x128]`). The injector flag is the hangar engine pick's nitrous bit for a
human and the roster `nitro` slot for an AI.

## The plant's constant inventory

Every number the live translational and rotational path and the contact rules carry that no data
file authors, with the class it falls in. `CSVM.Tests/FlightConstantInventoryTests` holds the same table and fails when a
constant is added, dropped or moved off its recorded value, so a new number cannot arrive here
without a provenance. Five classes are used:

- **decoded** reads out of `crimson.exe` at the address given, and the sections above carry the
  mechanism.
- **authored** mirrors a key in the extracted data.
- **unit** is a conversion factor or an arithmetic identity, with no behaviour of its own.
- **exception** is a CSVM invention kept deliberately, with a reason and a reachability
  measurement.
- The former **contact** class is empty: the plant's contact terms are decoded, so the file carries
  no fitted number any more. The contact rules' own constants are censused with the plant, on
  `CollisionDamage` and `AircraftContactResolver`; the invented crash, stop and cooldown laws that
  used to sit beside them are gone (the death-rule note in "Collision damage").

| Constant | Value | Class | Evidence |
|---|---:|---|---|
| `ThrustMachFloor` | 0.1 | decoded | `0x6034a8`, the Mach floor written back into the argument at `0x41ad02` |
| `ThrustVRefSlope` | 0.84 | decoded | `0x60349c` |
| `ThrustVRefMach` | 0.112 | decoded | `0x603498` |
| `ThrustMachTrim` | 1/60 | decoded | `0x603494` |
| `ThrustPowMach` | 1.41 | decoded | `0x6034a0`, the `_CIpow` exponent |
| `DenseThrustPowBase` | 1.33 × 0.98842078 | decoded | `0x6034a4` times the dense band's `k`, multiplied at `0x41ad0f` |
| `ThinThrustPowBase` | 1.33 × 0.7348 | decoded | `0x6034a4` times the thin band's `k`, the same multiply |
| `AttitudeThrustBoth` | 0.24 | decoded | `0x6080dc`, applied at `0x48fd14` |
| `AttitudeThrustUp` | 0.13 | decoded | `0x6080d8`, the one-sided branch at `0x48fd00` |
| `LiftGMin` / `LiftGMax` | −5 / 9 | decoded | the lift clamp in `FUN_0041abd0` |
| `ClMaxStatic` / `ClMaxMach` | 0.75 / 0.15 | decoded | the aerodynamic ceiling in `FUN_0041abd0` |
| `BandThresholdFt` | 6561.6796875 | decoded | `0x0071bb3c`, stored at `0x46368b`; 2000 m in feet |
| `DenseDensitySlugPerFt3` / `DenseSoundFps` | 2.2688e-3 / 1109.5 | decoded | `FUN_0041aca0`, dense band |
| `ThinDensitySlugPerFt3` / `ThinSoundFps` | 1.3560e-4 / 968.0 | decoded | `FUN_0041aca0`, thin band |
| `FeetPerMetre` / `MetresPerFoot` | 3.28084 / 0.3048 | unit | the altitude and Mach conversions the aero path runs in |
| `StandardG` | 9.82 | decoded | the force-to-acceleration multiply at `0x491290` |
| `StallWarnFrac` | 0.30 | exception | the STALL lamp's threshold, measured at 0.2989–0.2996 over four clips. A cue, not a force term |
| `MaxDiveSpeedFrac` | 1.75 | exception | a numerical backstop against a loop energy pump or a `dt` spike, measured non-binding on all eleven (above) |
| `GroundBlowIntoFactor` | 0.05 | decoded | the immediate in the player branch of `FUN_0048c220` |
| `GroundBlowVelocitySteer` | 2.0 | decoded | a global whose only writer is the `gbc` debug console command |
| `NoseChaseFactor` | 0 | decoded | decoded-absent: no instruction in `FUN_0048c470`/`FUN_0048fc40`/`FUN_0048e580` rotates the velocity direction onto the nose; see "`lift_accel_rate` is a lag toward a target velocity" |
| `AoaLimiterFactorDefault` | 1 | decoded | the decoded AOA window at full strength, `0x48c9f4`–`0x48ca18`; 0 is the A/B seam. It binds on every stock airframe, which is why the filmed pitch rate is discarded; see "The α a full pull holds" |
| `BounceLeverScale` | 2.25 | decoded | the literal at `0x00608108`, no data origin |
| `ContactPushOut` | 0.03 | decoded | the literal at `0x006080c4`, the player placement's offset along the normal (`0x48dfce`); the non-player arm has none |
| `BounceAngularHalf` | 0.5 | decoded | the shared literal at `0x006032e0` on the angular deposit (`0x48e4bc`) |
| `DragPolarScale` | 0.73 | decoded | `0x603474`, shared with the thrust curve |
| `DragPolarParasite` / `Linear` / `Quad` | 0.12 / 0.8 / 0.5 | decoded | `FUN_0041ada0`, the polar in Mach |
| `PitchTune` / `YawTune` / `RollTune` | 1 / 1 / 1 | decoded | absent from `FUN_0048c470`'s torque chains; see "The `*Tune` rates" |
| `BankYawCoupling` / `BankPitchCoupling` | 0.205 / 0.165 | decoded | `0x6289f8` / `0x6289fc` |
| `WeathervaneHalfAngle` | 0.5 | decoded | the quaternion-log halving at `0x4916fe`–`0x4917f0` |
| `AiNoseSpeedFloor` | 4.4704 | decoded | `0x608128`, the block at `0x48e95e`–`0x48e998` |
| `ReverseAuthorityFloor` | 0.2 | decoded | `0x6034fc`, `FUN_0048bdd0`'s fifth output |
| `FarFieldRangeM` | 1000 | decoded | `0x00607a18` holds 1e6, the squared metres `FUN_00538920`'s horizontal separation is compared against at `0x48c4ee`; see "The far-field plant" |
| `FarFieldAiSpeedBonus` | 5 | decoded | `0x006036bc`, subtracted from the negated cruise speed at `0x48c5ae` on the non-player arm |
| `BoostLever` | 1.8 | decoded | `0x48fcb6`, the lever the boost flag substitutes for the throttle; see "Nitro" |
| `BoostDragFactor` | 0.8 | decoded | `0x48fcbd`, the drag multiplier on the same branch (1.0 at `0x48fccf` otherwise) |
| `NitroSystem.Capacity` | 30 | decoded | `0x4b02c4`, written to both the cap `[obj+0x8b4]` and the spawn charge `[obj+0x8b8]` |
| `NitroSystem.BurnRate` | 4 | decoded | `0x4b02d5`, `[obj+0x8bc]`, spent at `0x49f820` while boosting |
| `NitroSystem.RechargeRate` | 1 | decoded | `0x4b02df`/`0x4b02e9`, `[obj+0x8c0]`, added at `0x49f838` unconditionally |
| `NitroSystem.EngageFraction` | 0.99 | decoded | `0x6080a8`, the human arm's engage line at `0x487eba` |
| `NitroSystem.CutoffFraction` | 0.05 | decoded | `0x6034d8`, the cutoff at `0x487ea1` and `0x49f888` |
| `NitroSystem.MinBoostAnimSeconds` | 1.0 | decoded | `0x47a838`, `def+0x188`, compared against the engage timer at `0x4b2224` |
| `NitroSystem.LoopKeyedSeconds` | 0.1 | decoded | `0x4b22b8`, the keyed `snd_nitro` refresh's own argument |
| `PhysicsConstants.NomGravity` | 20 | authored | `player.json`'s `nom_gravity`, mirrored for ballistics |
| `PhysicsConstants.MphToMs` | 0.44704 | decoded | the parser's own speed-token scale |
| `StickRamp.Rate` | 2.5 | decoded | `FUN_00487460`, 0.4 s of held key to full deflection |
| `CollisionDamage.EntityCut` | 0.2 | decoded | `0x48d51a`/`0x48d526`, the non-player-into-aeroplane cut |
| `CollisionDamage.EntityGrace` | 1.0 | decoded | `0x48d383`/`0x48d395`, written to both parties |
| `CollisionDamage.SpawnGrace` | 1.5 | decoded | the spawn write of `obj+0xAC` |
| `CollisionDamage.ContactShakeFactor` | 0.03 | decoded | `0x6080c4`, read at `0x48d3dc`; the contact shake's per-m/s term |
| `CollisionDamage.ContactShakeCap` | 0.15 | decoded | `0x6036a8`, compared at `0x48d3eb`; the shake's ceiling in radians |
| `AircraftContactResolver.EmbedPushOut` | 0.3 | exception | m per un-embed attempt; the loop itself has no counterpart, the original's placement cannot leave an airframe overlapping |
| `AircraftContactResolver.EmbedTries` | 3 | exception | attempts before the airframe is destroyed instead of left inside the world; bound by `AircraftContactResolverTests` |

The `flightModel.*` config block overrides eight of these: `pitchTune`, `yawTune`, `rollTune`,
`stallWarnFrac`, `liftGMin`, `liftGMax`, `noseChaseFactor` and `aoaLimiterFactor`.
A key is a development seam for an A/B at the controls and says nothing about provenance; the three
`*Tune` keys exist so a decoded 1 can be compared against a fitted value by hand, `noseChaseFactor`
so the decoded 0 can be compared against the retired kinematic chase (1), and `aoaLimiterFactor` so
the held-off AOA window (0) can be compared against the decode (1). The inventory test pins all
five at their recorded values so a fit cannot return quietly. `FlightConstantInventoryTests` also asserts the block's key set,
so a key added without an inventory row fails rather than appearing in a `--dump-config` template
nobody reads.

⚠ **A constant with no evidence column is a fitted constant.** That is what the census test enforces:
it does not check that a number is right, only that somebody classified it, which is the step that
was skipped every time a fitted multiplier survived a rewrite here.

## What the test suite pins, and why each test can fail

Every decoded mechanism above has an able-to-fail assertion behind it, and several of those tests
encode a specific wrong reading rather than merely re-stating the right one. Recorded here because
the tests, not the prose, are what stops a mechanism being quietly re-derived.

- **`AngularDampingTests`**, `FUN_00491820` steps 2–3. Pins the ORDERING (this tick's own torque
  is damped too, not exempted until the next tick: a decay-then-add implementation reads the same
  two lines and fails here) and the FORM. ⚠ The two forms agree to first order in `dt`, so a small
  timestep cannot separate them; the case that can is `dt · damp = 5`, past the linear form's
  stability edge at **`dt · damp = 2`**, where `(1 − dt·damp) = −4` flips the rate's sign and grows
  it every tick while `exp(−dt·damp)` stays in `(0, 1)` and only decays.
- **`StickRampTests`**, `FUN_00487460`, the three per-axis blocks. Pins the RATE (0.4 s of held key
  is full deflection, and half that time is half deflected, so a linear ramp cannot be swapped for an
  exponential approach) and, twice over, the ASYMMETRY: release centres the axis in one frame, and a
  reversal ramps from centre rather than counting down from the old deflection. ⚠ A symmetric ramp
  agrees on a held key and differs on every release and every reversal, which is the whole of the
  cadence roll-off, so the held-key case alone would pin nothing that matters.
- **`WeathervaneTests`**, `FUN_00490f70`, `0x4916fe`–`0x4917f0`. Reads the torque directly, or the
  body rates after ONE step from rest where the arithmetic is closed form, so a sign flip, a missing
  halving or a leak into roll fails exactly instead of being absorbed a hundred frames later. Pins:
  it VANISHES on the flight path (exactly zero, which is what leaves level cruise untouched by
  construction rather than by scale); the sign CLOSES the misalignment (a reversed weathervane is
  divergent and still looks plausible in one frame); the magnitude is `return_rate × HALF` the
  angle; the roll component is identically zero at every bank. ⚠ And it pins that a released axis
  decays at **`ang_momentum_damp` ALONE**, folding `return_rate` back into the damping coefficient
  is the first-order lag this replaced, and the failure message prints that number alongside.
- **`KnifeEdgeTests`**, the retired sag term's own footprint is asserted gone: wings level, nose
  45° up, path on the nose, stick centred, every torque in the model is identically zero, so one
  step must not rotate the attitude at all. The bounded term keyed on `1 − |bodyUp·up|`, which is
  0.29 in exactly that attitude, so it rotated the nose down at up to **11.5 °/s**, a nose-down
  bias in every pull at any bank, the "knife-at-zero-bank leak". Also pinned: the
  knife-edge never settles on any of the eleven (a bounded sag puts almost none of its total in the
  last third of a 36 s hold, a genuine drift about a third), and α stays inside the `liftAOAs`
  WINDOW on all eleven, peak **0.77–5.68°** against the authored 10° upper edge; with the
  kinematic chase retired the blend may engage past the 5° low edge on the tightest airframes,
  which is the original's own arithmetic there, but must never saturate. The Balmoral is
  additionally pinned under the low edge (peak 2.28°), because that pin **replaced a lost prose
  figure** ("the Balmoral knife-edges at α = 5.1°, 0.1° inside the ramp") no instrument could
  reproduce. A fourth assertion, that the nose stays well below the path, is retired with
  `wingVert`: its bound was a footage anchor. The probe recipe lives in `Probes.KnifeEdge`, it was
  lost once as prose and is code now precisely so that it cannot be again.
- **`AttitudeThrustTests`**, the 0.24 / 0.13 coefficients, and the SIGN read out of the integrator
  rather than off the formula's argument name: throttle touches only the thrust term, so
  differencing a full-throttle step against a zero-throttle step from an identical state isolates it
  to the last bit. ⚠ This is the one place in the force path where a dropped sign produces flight
  that still looks entirely plausible, it merely swaps climb for dive. The four-arrangement climb
  table above is asserted as a BOUND that separates the four, not one the shipped arrangement merely
  passes, and the probe fails the run outright if the altitude clamp binds (a clamped run measures
  the clamp, not the climb). The same class pins the tuner's own rig (`Probes.TunerEnvelope`): that
  the vertical climb, the floor of every climb angle, stays above the filmed 163.05 mph, and that
  the pinned rig and the free climb agree at the filmed path. They share only the force sum, so a
  gap between them means one of the two has stopped measuring the equilibrium.
- **`PartThrottleEquilibriumTests`** covers the level-flight curve of "Part-throttle equilibrium", solved
  from the decoded constants in the test itself and flown on all eleven airframes at eight lever
  positions from both above and below, so an equilibrium that is right by construction rather than
  by arithmetic fails. Also pinned: the curve rises with every lever step (a fold would make the
  lever unusable as a speed control), it depends on nothing but `ThrustFactor / DragFactor`, the
  Mach floor shapes the lowest levers, and the Balmoral's 1/8 solves below its own level-flight
  floor. ⚠ Its able-to-fail control is a lever off by 5 %, which must miss the equilibrium at every
  position (`METHOD-9`); without it the tolerance could admit any curve of roughly this shape.
- **`ControlLimiterTests`**, the G ramp's reachability, on the signed body-up load factor the
  implementation actually reads. The positive side stays clear of `highGs[0]` on all eleven; the
  negative side is grazed by two airframes and is pinned as a bounded fraction of the ramp, so the
  suite fails when the graze deepens rather than asserting an unreachability that is not true. ⚠ It
  carries two able-to-fail controls (`METHOD-9`): halved thresholds must make the α and demand
  disproofs fail, and a halved `lowGs` pair must break the graze bound.
- **`LatentControlAuthorityTests`**, the pitch-only high-speed fade and the opposing-command
  limiter, each on a SYNTHETIC airframe that authors it into reach, because no stock airframe can
  exercise either. Pins the fade's three regions, that roll keeps what pitch loses, that the two
  stages multiply where they overlap, and the fade reaching body rates; then the AOA window's shape
  and floor, the G ramp mirrored about its authored band, that the SMALLER of the two applies, and
  the sign rule flown on both pitch and yaw, a separating command is softened and the closing one
  is untouched. ⚠ Its stock-side control is that on all eleven airframes pitch and roll authority
  are the identical number at every speed up to `MaxDiveSpeedFrac × fd_speed`, which is what says
  the fade cannot move an envelope row.
- **`FarFieldPlantTests`**, the far-field branch, each skipped term alone against a near-field
  control in the identical state, because one trajectory difference cannot say which of the five was
  dropped. Pins the boundary as a strict `>` at 1000 m, that a human's plant never takes the branch,
  that it is re-decided every step in both directions, the held speed from above and below, the 1/s
  rate on an airframe whose `lift_accel_rate` is 4, no gravity, no bank coupling, full control
  authority, a forced limiter scalar, and the two load-factor readouts left where the last
  near-field step put them. ⚠ Two of its cases are controls rather than claims: the ground blow
  still runs far-field, and an AI at 999 m integrates identically to one standing on the human.
  The in-engine half, which is the session plumbing, is the `ai-far-field-plant` suite.
- **`FlightConstantInventoryTests`**, the inventory table above, as a census over the plant's own
  const fields plus the `flightModel.*` config block. It checks provenance, not correctness: a
  constant added, dropped or moved fails until somebody classifies it, which is the step skipped
  every time a fitted multiplier survived a rewrite here. Beside the census it measures what the
  two reachability claims rest on, per airframe: the fastest manoeuvre's peak against
  `MaxDiveSpeedFrac`, and the height reached above the altitude cap against one frame of climb.
  ⚠ Its own able-to-fail control is that the fastest manoeuvre still exceeds `fd_speed`; a scenario
  gone gentle would pass the dive-cap disproof while measuring nothing (`METHOD-9`).
- **`FlightEnvelopeTests`**, the Bloodhawk's flown envelope against its DECODED targets. The count
  of asserted scenarios is **pinned at 4** so that silently demoting one to informational cannot
  read as a green run. Two of the four targets are solved in the test itself from the thrust curve,
  the Mach polar and gravity, the level equilibrium at lever 1, and the 70.7° dive's along-path
  balance, so a target that drifted toward the plant it judges, or back toward the footage figure
  it replaced, fails there rather than passing as a row that agrees with itself; its own able-to-fail
  control drops the attitude-thrust term and must miss the dive target (`METHOD-9`). The other twelve
  rows report the decoded plant's number with nothing independent to compare it to; `altitude-ceiling`
  is one of them, because the height above the band edge is the coast the climb rate buys.
  ⚠ A demotion to informational is never the quiet way to make a run green.
- **`ParityLedgerTests`**, the ledger below, as a census: a plant constant with no class or no
  source fails, a probe row with no ledger class fails, and no class may go empty. It is also what
  GENERATES the published tables, to `CSVM_LEDGER_OUT`.

## Parity ledger

Every mechanism and every envelope row, on every stock airframe, in exactly one of three classes.

- **decoded**, the value the executable or the shipped data carries. The Source column gives the
  address, global or data key. A footage figure that disagrees with a traced mechanism does not
  change the class: it is **discarded**, and kept only as an annotation beside the decoded value.
- **exception**, a named product decision, with the reason in the Source column.
- **unsupported**, decoded but not ported, or not decoded.

**There is no conflict class.** A traced mechanism is the answer, so no instrument may gate on a
footage figure: `FlightEnvelopeTests`, the probe's targets and the tables here all pin the decoded
plant's own values.

The constant inventory's four finer classes collapse into these three: `decoded`, `authored` and
`unit` are all values CSVM did not invent and read as **decoded** here, each keeping its own source,
while `exception` separates out. The constants table is above, "The plant's constant inventory";
the regenerated ledger repeats it with the short Source column and adds the four tables below.

**Regenerate it** with the whole-envelope run and the ledger census:

```
$env:CSVM_DATA_ROOT="Z:\CSVM"
$env:CSVM_LEDGER_OUT=".scratch/parity-ledger.md"
dotnet test CSVM/CSVM.sln --filter "FullyQualifiedName~ParityLedger"
```

The same report `--dump-flight=all` prints is what the ledger is read off, so the dump a plant
change is diffed against and the tables here cannot disagree. `ParityLedgerTests` fails when a plant
constant has no class or no source, when a probe row has no ledger row, or when a class goes empty.

### Mechanisms

The numbers are in the constant inventory; this is the behaviour they sit in, plus the disproofs,
which are findings rather than code.

| Mechanism | Class | Source |
|---|---|---|
| lift as a clamped demanded load factor | decoded | `FUN_0041abd0`, `FUN_0048fc40` |
| the `liftAOAs` relative-wind blend, player only | decoded | `0x48c520`, the blend in `FUN_0048c470` |
| drag as a Mach polar with no induced term | decoded | `FUN_0041ada0` |
| attitude-scaled thrust | decoded | `0x48fd00`, `0x48fd14` |
| the thrust-available Mach curve | decoded | `FUN_0041abd0` into `0x48fce7` |
| the throttle lever, linear, and its 0.5/s slew | decoded | `0x48fce7`, `0x48e63f`–`0x48e6c3` |
| atmosphere band selection at 2000 m | decoded | `0x0071bb3c`, written by `FUN_00463640` |
| the thin band above 2000 m, and the ceiling it makes | decoded | `FUN_0041aca0`'s second arm; no altitude clamp exists, the position write at `0x0048ea81` is unconditional |
| the stall flag and its nose-drop torque | decoded | `_DAT_0071c41c`, the torque at `0x48d158` |
| the low-speed authority ramp | decoded | `FUN_0048bdd0` |
| the pitch-only high-speed fade | decoded | `0x48be22`–`0x48be68`, `0x0071c400` / `0x0071c404` |
| the opposing-command limiter's AOA window | decoded | `0x48c9f4`–`0x48ca18`, `0x0071c42c` |
| the opposing-command limiter's G ramp | decoded | `0x48ca1e`–`0x48ca61`, min at `0x48ca69` |
| the G ramp reads the SAME tick's delivered lift | decoded | `0x48c883` writes it before the `0x48ca1e` read, and `FUN_0048e580` integrates both halves afterwards, so `Step` builds its forces from the entering attitude too |
| the limiter's separating-command sign rule | decoded | `FUN_0053fd40` at `0x48c9ae`, pitch at `0x48cb52` |
| bank coupling into yaw and into pitch | decoded | `0x48ccb3`, `0x48cd36` |
| weathervane centring, player only | decoded | `FUN_00490f70`, applied at `0x48ce3d` |
| angular damping and the reciprocal inertias | decoded | `FUN_00491820` |
| the far-field speed-hold plant | decoded | `0x48c4e9`–`0x48c603` |
| ground blow as a control bias | decoded | `FUN_0048c220`, called at `0x48cf95` |
| the keyboard stick accumulator | decoded | `FUN_00487460` |
| the mouse-flying arm's `is_autogyro` roll/yaw exchange | decoded | `0x4876f4`, inside `FUN_00487460`'s mouse arm; `MouseFlight.Read` exchanges and negates the same two sources. ⚠ One port deviation rides on it: the original gates the yaw slot at `0x48774c`'s 0.3 whatever the exchange put there, and with no third mouse axis here that slot is an autogyro's only cursor-fed lateral source, so it takes the cursor travel's own 0.1 instead (`docs/controls.md`) |
| the six-slot control-surface mix and its 2/s exponential | decoded | `FUN_004b27e0` / `FUN_004b2a40` / `FUN_004b2ca0`, smoothing `FUN_00460490` |
| contact placement, normal impulse and angular deposit | decoded | `FUN_0048d7f0`, `0x48e4bc` |
| collision damage, armour before health | decoded | `FUN_0048d2c0` |
| the per-contact camera shake | decoded | `FUN_0048d2c0`'s block-5 kick at `0x48d409`, `min(speed·s·0.03, 0.15)` |
| the every-other-frame contact sweep | decoded | the parity gate at `0x48ed79` |
| the nitro tank and its state machine | decoded | `FUN_004aff80`, `FUN_004b2110` |
| engine torque: none exists | decoded | every write to `FUN_0048c470`'s angular accumulator |
| roll-to-pitch coupling: none exists | decoded | every read of `[obj+0x100]` and `[obj+0x114]` |
| ambient turbulence: nothing ships | decoded | shake block 5, the five xrefs of `FUN_0042c070` |
| the one-sided negative `C_L` ceiling is unreachable | decoded | `0x48c821`–`0x48c852` builds `n` as a vector length, so `FUN_0041abd0` is never handed a negative `C_L` |
| the `level_off_rate` auto-level torque is unreachable | decoded | `0x48cedc` / `0x48cf76` read `def+0x650`, a token the parser accepts and no shipped `dynamics` block authors, so the term is zero on all eleven airframes (`LevelOffRateAbsenceTests`) |
| the AI's `medium_aishake` on a nitro engage | decoded | `FUN_00473430(1)`, the middle def of the `0071c2f4` table, on the aircraft's own node |
| the AI's positional `snd_nitro` blip | decoded | the keyed loop's 0.1 s refresh inside `FUN_004b2110`, reached at the engage and by the release calls after the maneuver |
| the nitro decay lockout on the decay instance | decoded | the completion callback registered at `0x4b2271` (handler `0x4b20f0`); CSVM reads the instance's own `ANIM_STATE` instead |
| a dead AI's throttle and surfaces freeze at their last commanded values | decoded | `FUN_004b82d0` zeroes neither `+0x124` nor the surface deflections; `StepWreckFall` steps `_lastInput` unchanged |
| a wreck flies the near-field plant | decoded | `obj+0x384`'s only writers are the `-fd` switch, the console's `fd` / `ifon` and the constructor's zero, so no crash ever takes the `0x48c4ba` arm |
| far-field range is measured to the NEAREST human pilot | exception | plan Decision 3; the original presumes one player |
| control surfaces, shake and nitro edges run for EVERY human pilot | exception | plan Decision 3; the original's guard is the single player |
| the Fury's rudder animates | exception | CSVM also matches `l_rudder_rotate` and a digitless `l_elevator`, which the `%d` lookups miss |
| a live producer for an AI's nitro injector | unsupported | `AiSpawn.Nitro` reads roster slot 34; the mission spawner does not read roster blocks yet |

### Envelope rows

Sixteen scenarios, flown identically on all eleven airframes. The Discarded footage column is the
annotation the probe prints in the row's own text; none of it gates anything.

| Row | Class | Bounding term | Discarded footage |
|---|---|---|---|
| `level-top-speed` | decoded | thrust = drag; no clamp binds | 300.40 mph |
| `accel-150-290` | decoded | the thrust curve against the Mach polar | 3.76 s |
| `terminal-dive` | decoded | thrust × attitude scale + gravity = drag | 355.20 mph |
| `roll-360` | decoded | roll torque against `ang_momentum_damp` | 2.05 s off the ADI |
| `pitch-rate` | decoded | the AOA window and the lift-demand lag | 33.00 °/s |
| `yaw-360` | decoded | yaw torque times the authored authority curve | 28.60 s |
| `altitude-ceiling` | decoded | the 2000 m band edge, coasted past | ~6,600 ft plateau |
| `level-speed-near-cap` | decoded | thrust = drag, with the band edge 12 m above | 300.40 mph |
| `sustained-turn-speed` | decoded | the AOA window and the `C_L` ceiling | 222.94 mph, 449.8° |
| `sustained-turn-sink` | decoded | delivered lift against `nom_gravity` | 1.85 ft/s |
| `sustained-turn-rate` | decoded | the AOA window and the bank coupling | 18.95 °/s |
| `eighth-throttle-speed` | decoded | thrust × lever = drag | none; every candidate was footage |
| `decel-290-150` | decoded | the Mach polar alone | 7.04 s |
| `zoom-climb` | decoded | the AOA window and the lift demand's clamps | 936 ft, apex at 6.5 s |
| `zoom-climb-min-speed` | decoded | the same loop's energy split | 127.9 mph |
| `stall-departure` | decoded | the `C_L` ceiling and the `stall_mag` torque | none |

### Per airframe

| Airframe | rows | decoded | exception | unsupported | branches reached |
|---|---:|---:|---:|---:|---:|
| `player_bhawk` | 16 | 15 | 1 | 0 | 7/16 |
| `player_pfighter` | 16 | 15 | 1 | 0 | 7/16 |
| `player_fury` | 16 | 15 | 1 | 0 | 7/16 |
| `player_warhawk` | 16 | 15 | 1 | 0 | 7/16 |
| `player_autogyro` | 16 | 15 | 1 | 0 | 8/16 |
| `player_avenger` | 16 | 15 | 1 | 0 | 7/16 |
| `player_balmoral` | 16 | 15 | 1 | 0 | 7/16 |
| `player_brigand` | 16 | 15 | 1 | 0 | 7/16 |
| `player_fbrand` | 16 | 15 | 1 | 0 | 7/16 |
| `player_kestrel` | 16 | 15 | 1 | 0 | 7/16 |
| `player_peacemaker` | 16 | 15 | 1 | 0 | 7/16 |

The class is a property of the mechanism, so it is the same on all eleven; what varies is
reachability. The autogyro is the one airframe whose scenarios enter the low-speed authority ramp,
which its wing loading (500 kg over 800 m², a ninth of the Bloodhawk's) is what buys.

### Branch coverage

Which decoded branches the dump's own scenarios drive, and which instrument drives each of the
rest. An unreached branch here is a statement about this scenario set, not an untested path.

| Branch | Airframes reaching it | Instrument that drives it |
|---|---:|---|
| `dense-band` | 11/11 | `AtmosphereBandTests` |
| `thin-band` | 11/11 | `AtmosphereBandTests`, `FlightConstantInventoryTests.TheBandEdgeCeilingsTheClimb` |
| `low-speed-ramp` | 1/11 | `ControlAuthorityRampTests` |
| `pitch-fade` | 0/11 | `LatentControlAuthorityTests`, on a synthetic airframe |
| `aoa-window` | 11/11 | `LatentControlAuthorityTests`, `ControlLimiterTests` |
| `g-ramp` | 0/11 | `ControlLimiterTests`, which measures the graze as a bounded fraction |
| `g-clamp` | 0/11 | `ControlLimiterTests`; no stock manoeuvre demands ±9 G |
| `cl-ceiling` | 11/11 | `StallNoseDropTests`, `PartThrottleEquilibriumTests`' level-flight floor |
| `stall` | 0/11 | `StallNoseDropTests`, `AutogyroStallNoseDownTests` |
| `dive-cap` | 0/11 | `FlightConstantInventoryTests.TheDiveSpeedCapNeverBinds` |
| `weathervane` | 11/11 | `WeathervaneTests` |
| `bank-coupling` | 11/11 | `BankCouplingTests` |
| `far-field` | 0/11 | `FarFieldPlantTests` and the `ai-far-field-plant` suite |
| `boost` | 0/11 | `NitroSystemTests` |
| `ground-blow` | 0/11 | `GroundBlowTests` |

Three of the unreached branches are structurally out of a single-aircraft data probe's reach:
`far-field` needs an AI and a human more than a kilometre apart, `boost` needs a nitrous injector,
and `ground-blow` needs terrain contact. The other five are the plant's own reachability findings:
`pitch-fade` and `g-clamp` are authored out of reach on every stock airframe, `g-ramp` is grazed
only in a sustained outside push, `dive-cap` is the backstop measured non-binding on all eleven,
and `stall` needs a flight slower than any of these scenarios holds.

## What this changes for the remake

Checked against [`src/Flight/Airframe/FlightModel.cs`](../../CSVM/src/Flight/Airframe/FlightModel.cs) and
[`src/Flight/Airframe/PlaneStats.cs`](../../CSVM/src/Flight/Airframe/PlaneStats.cs), the items most likely to differ:

1. **Lift is the demanded G, delivered.** The remake's `liftFrac` cancels a *share* of gravity's
   cross-path component; the original builds a demand vector, clamps it to ±5/9 G and applies it.
   The remake's `AlignRate` (TUNE, 4/s) is the same quantity as the original's authored
   `lift_accel_rate` (fallback 1.2/s), it should be read from `player.json`, not tuned.
2. **`liftAOAs` is an airflow blend, not a load-factor ramp.** The remake reads `[5, 9]` as the
   edges of a G ramp; the original uses them as the window over which the relative wind is faked
   toward the nose. Same numbers, different mechanism. This also **settles the units question for
   these keys**: the parser takes their cosine, so `liftAOAs` and `maxAOA` are confirmed **degrees**,
   while `highGs`/`lowGs` are stored raw and are plain **G**.
3. **The two hardcoded bank constants (0.205, 0.165).** These are in no data file, they are
   developer-console variables, so no amount of data extraction would have surfaced them. Landed
   in `C22`; see "Bank coupling, resolved". ⚠ They make the banked turn **faster**, not slower,
   so they do not account for the footage reading a slower banked turn than ours.
4. **The attitude-dependent thrust (0.24 / 0.13).** A video fit would absorb these into gravity
   or drag and then fail in the opposite manoeuvre.
5. **Rudder at 10 % authority in flight**, full only between 22.5 and 45 mph.
6. **Pitch authority reaching zero at the second `high_speed_pitch_fade` speed**, roll never fading.
   Landed in `B11`; unreachable on this install's authored [1000, 1001] mph.
7. **Stall speed is per-airframe**, `sqrt(2W / (0.75 ρ S))`, not a fixed fraction of `fd_speed`.
8. **`return_rate` is a weathervane torque** toward the velocity vector, not extra axis damping on
   a released stick. Landed in `C23`; see "Weathervane centring, resolved". ⚠ It reaches every
   sustained full-stick manoeuvre, because those hold a real nose/path misalignment, it is not a
   released-stick-only term in any sense.
9. **The limiter gating only SEPARATING input**, a subtle asymmetry that damps entry into a
   departure and leaves recovery from one free. Landed in `B11`. ⚠ The G ramp is live and grazed by
   two airframes in a sustained outside push; the AOA window is live at its decoded strength and
   binds on every stock airframe, which is what makes the filmed pitch rate discardable rather than
   a target, see "Corrected, the G ramp grazes and the AOA window binds" and "The α a full pull
   holds".
10. **Thrust scales with `ref_area`, not `1/veh_weight`.** The remake divides engine power by
    weight; the original multiplies it by reference area, which is what makes `RefArea` cancel
    against drag. See `ThrustFactor` above.
11. **Drag is a polar in Mach with no induced term.** Any model that makes drag rise with the pull
    is adding a mechanism the original does not have. See Drag above. The remake briefly did:
    a `sin²α` term (`InducedDragCoef` 10.75) was fitted on 2026-08-07, two
    days before this decode, and removed with no successor when
    the decoded Mach polar replaced the fitted drag law. `FlightModel.cs` now carries the same
    no-induced-drag statement in its own comments.
12. **The throttle lever slews at 0.5/s with no idle floor** (2 s full-to-idle). `FlightController`
    applies it to both keyboard and AI desired-throttle commands; carrier releases start both fields
    at 0.1, then ramp if the AI immediately requests full power.
13. **Spawn speed is the mission's own, and it is not plane-dependent.** ⚠ This item previously
    read "the original's is plane-dependent"; that is false at source, and the remake now reads
    the authored value. The player spawn routine `FUN_0047f1f0` takes its speed from the
    mission's own `PLAYER_INIT[4] × 0.1` (18 m/s in 48 of 51 records) and touches no per-aircraft
    data at all: no reference to `fd_speed` (object `+0x668`) or to the def pointer appears in any
    of its 349 instructions. The remake's 53.6 m/s traces to `−53.6448` at `0060803c`, read only
    by the cheat dispatcher `FUN_0047e080`'s case `0x3b7` (teleport player to camera), which is
    120.000 mph exactly. Spawn throttle is `PLAYER_INIT[3]` (0.8 in 49 of 51 records), replacing
    the remake's 0.5. Full decode, including the mode-3 Instant Action branch and the reset paths:
    [../formats/spawns.md](../formats/spawns.md), "Story mission spawns".
    ⚠ **The spawn sits in the sub-cruise band the force scale is least corroborated in.** A start at 18 m/s is
    well below cruise, where the polar reads 2–3.6× too strong against `CAP-05`, and the aircraft
    accelerates through its own computed stall speed at about 4.4 G rather than dropping. The
    climb-out reads right at the controls, so this is a dependency to know about rather than a
    defect: a change to the sub-cruise force path moves the feel of every mission's first seconds.
    The spawn speed is authored data and is not the knob to compensate with.
    The per-airframe spawn rule that does exist belongs to **AI** aircraft: the vehicle factory
    `FUN_0047c210` gives a pathless aircraft `min(plane_speed_max, fd_speed)` along its nose
    (comparison at `0047d84f`) and gives one with an authored waypoint path zero velocity and
    throttle 1.0 (`0047d82d`). `plane_speed` is a two-value authored tag at def `+0x1e4`/`+0x1e8`,
    each scaled by `0.44704` on parse (`00607b2c`), which the AI speed controller then uses as the
    lower and upper clamps.

## Confidence

- **Read directly from the executable:** all constants and formulas above, except as marked. This
  now includes the `ThrustFactor` chain (parser → def `+0x128` → runtime `+0x66c` → force
  assembly), the linear throttle multiply and its 0.5/s slew, the thrust `pow` operands
  (`MSVCRT!_CIpow`, base `1.33·atm->k`, exponent `1.41·M`), the drag polar's variable being
  **Mach**, the last read off the raw bytes rather than out of a decompiler, which is what
  corrected it, the conversion-free weight chain ("The force scale, settled"), the attitude
  quaternion's doubled half-angle, and the weathervane's axis, its half-angle and
  its player-only gate ("Weathervane centring, resolved", which corrects a sign this document
  previously carried).
- **Read directly from the executable, 2026-08-15:** that the atmosphere call is on the
  shared live path with no player/AI branch and no altitude zeroing, that the altitude-zeroing site
  belongs to the debug copy behind a dialog flag, that the band threshold `0x71bb3c` has one read
  reference and no writer named by address, that the throttle lever and its slew are unbranched,
  and the far-field cruise model behind `fd_speed · throttle`.
- **Read out of a live retail process:** the band threshold at `0x71bb3c` holds 6561.6796875 ft
  (2000 m), not the `0.0` a listing of the uninitialised slot shows, so the dense band covers
  every altitude below 2000 m for player and AI alike. The former disagreement between the byte
  reading and the stall/equilibrium arithmetic is closed.
- **Decoded, with the footage figure discarded:** the absolute force scale below cruise,
  `CAP-05`'s zero-thrust points read the polar ≈2–3.6× too strong (a constant-ΔC_D deficit), the
  weight chain is byte-verified conversion-free, and the footage's own accel row rejects any
  uniform rescale. See "The force scale, settled".
- **Verified by arithmetic:** the dense atmosphere band, via the stall-speed check against the
  parser's fallback aircraft **and** via the level-equilibrium solve (the thin band misses by ~4×),
  now also proven from the bytes for the player path; `ThrustFactor` = engine power, via a
  perfect rank correlation with `fd_speed` across all eleven player airframes and an absolute
  solve landing nine of eleven inside 1 %.
- **Resolved 2026-08-14 (was "untested"):** the shipped Dynamics tuner **dialog** is dead code, with
  no callers; the measurement harness behind it is reachable from the world tick behind five debug
  flags and prints rather than writing `dynamics.txt`.
- **Read directly from the executable, 2026-08-14:** the ground-blow probe, its falloff, its axis and
  both application paths; the `bounce_factor` impulse and its doubled rotational term; the two
  per-object timers; the AI mode enum; the live-versus-debug integrator split. World units are
  metres, identified positively from `nom_gravity`'s 9.82 reference divisor rather than from the
  absence of a conversion.
- **Read directly from the executable, 2026-08-15:** the per-spawn jitter's full eleven-slot list,
  its three gates and the `mode`→`obj+0x67c` class table (`jet`/`heli`/`tank`/`ship`/`wingman`/
  `plane` = 0/1/2/3/4/5), which also settles what was recorded below as an undetermined data token.
- **Read directly from the executable, 2026-08-24:** the contact placement's in-place translation
  rewrite with its player-only 0.03 m offset and skipped-frame accumulator; the partition's
  inertia-multiplied angular share (the `I⁻¹` reading withdrawn); and the end-to-end trace showing
  the contact path carries no tangential, friction or vertical-speed term, which closed `BL-381`'s
  sink-removal question as a disproof.
- **Not determined:** which registry entities keep the `+0xcc` emitter flag set permanently, so the
  GDD's naming of zeppelins as ground-blow emitters is neither confirmed nor refuted; which axes the
  reverse-authority factor reaches.
