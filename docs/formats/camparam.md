# Camera tuning - `camparam.json`

Part of the [format documentation](README.md). This shared zrdr reader defines chase distance,
catch-up rates, third-person eye height and pitch, and look-behind, death, crash, and flyby camera
geometry.

## Contents

- [Shape](#shape)
- [Per-plane overrides](#per-plane-overrides)
- [Default block](#default-block)
- [Static death, crash, and flyby cameras](#static-death-crash-and-flyby-cameras)
- [The distance law](#the-distance-law)
- [Known limits](#known-limits)
- [Throttle transient](#throttle-transient)
- [Engine-read fields](#engine-read-fields)
## Shape

The root is a plain alternating `name, properties` list, the same layout `vehicle.json` uses for
its defs, with no wrapping list. Every value is a one-element list of a float:

```
[ "default", [ "dist", [13.0], "dist_factor", [0.01], … ],
  "Bloodhawk", [ "dist", [18.5], "dist_min", [18.5], "dist_max", [25.0] ],
  … ]
```

`default` comes first and carries every key. Each later block is one aircraft and carries **only
the keys it overrides**, three keys for most, five for the Balmoral. Resolution is therefore
`default` first, then the plane's own block layered on top.

⚠ **The blocks are keyed by the aircraft's DISPLAY name**, `"Bloodhawk"`, `"Firebrand"`, not by
the model node (`player_bhawk`) and not by the vehicle def (`pbloodhawk`). The node→display map is
in [markers.md](markers.md); in the engine it is `MarkerRig.PlayerAirframes`. Do not substitute
`PlaneRoster.PlaneDisplayName`: it strips a leading `p` and title-cases, which yields `"Fbrand"` for
`player_fbrand` and silently drops the Firebrand's override.

## Per-plane overrides

Seven of the eleven player airframes carry a block. The other four, Devastator, Hoplite,
Hellhound and Brigand, have none and take `default`'s 13.0.

| Aircraft | `dist` | `dist_min` | `dist_max` | also |
|---|---|---|---|---|
| Bloodhawk | 18.5 | 18.5 | 25.0 | |
| Fury | 17.0 | 17.0 | 25.0 | |
| Peacemaker | 18.0 | 18.0 | 30.0 | |
| Kestrel | 14.5 | 14.5 | 30.0 | |
| Firebrand | 20.5 | 20.5 | 30.0 | |
| Warhawk | 20.0 | 20.0 | 35.0 | |
| Balmoral | 25.0 | 25.0 | 30.0 | `thirdp_height` 0.2, `thirdp_pitch` 0.2 |

The distance tracks airframe size: the Kestrel is the smallest number and the Balmoral, a heavy
two-turret aircraft, the largest.

## Default block

| Key | Value | Reading |
|---|---|---|
| `dist` | 13.0 | Base chase distance, metres. |
| `dist_factor` | 0.01 | Metres of extra chase distance per m/s of airspeed: `d = dist + dist_factor·V`, V in metres per **sim** second. CAP-21's Bloodhawk staircase clips show: the measured slope 5.65e-4·d(0) per m/s implies 0.0105 against the shipped 0.01, 5% agreement. |
| `dist_vary` | 0.1 | The throttle transient's gain: metres of extra distance per unit of the gap between speed and its own lagged copy. See "The distance law". |
| `dist_min` / `dist_max` | 15.7 / 25.0 | The bounds the speed-driven distance is held inside for a forward-facing camera, so `dist_min` is also the pose the view rests at. See "The distance law". |
| `dist_catch_up` | 1.0 | The rate, per frame-second, at which the lagged speed copy `dist_vary` works against eases toward the real one, so also the throttle transient's relaxation rate. |
| `pos_catch_up` | 2.0 | Rate at which position eases, in the same exponential and the same clock. |
| `look_catch_up` | 3.0 | Rate at which the aim eases, likewise. |
| `thirdp_height` | 0.138 | Third-person eye height. Units unknown (not metres at this magnitude). |
| `thirdp_pitch` | 0.29 | Third-person pitch, in **degrees**: the reader multiplies it by π/180 on the way into the block, and the placement adds the result to the camera's smoothed elevation. 0.29° is a hair of tilt, not the 16.6° that reading the file's number as radians would suggest. |
| `back_dist_min` / `_max` | 15.5 / 55.0 | The look-behind view's distance bounds. Ships with no base-distance sibling, so the engine reads it as bounds on the shared chase radius: the min bites for the smallest airframes (a Kestrel's 15.0 m dynamic radius is lifted to 15.5), the max never in practice. A reading from the data's shape, not a capture-verified decode, no look-behind footage exists. |
| `death_interval` | 2.0 | Seconds of velocity projection in the death-camera placement: `speed · death_interval` becomes the third local offset component. It is not a re-frame timer. |
| `death_z` / `death_x` | 0 / 80 | Longitudinal addition / radius of the random local-plane offset used for the death camera. |
| `death_alt` / `death_min_alt` | 5 / 15.1 | World-Y addition / absolute world-Y floor applied after the local death-camera offset is transformed through the aircraft basis. |
| `crash_horiz` / `crash_y` | 30 / 45 | Crash-camera offset, metres: on a fatal crash the camera hard-cuts to `crash_horiz` m behind the impact (along the flight path's horizontal component) and `crash_y` m up, looking at the impact, then holds still. `C1 IA1 Crash.mp4` / `C1 IA1 Crash 2.mp4` show: instant cut, static elevated look-down (the implied 56° matches both clips), HUD hidden, and the near-vertical dive clip's overhead view is what the horizontal-component rule degenerates to in a dive. |
| `crash_chord_y` / `crash_elev` | 1000 / 40 | Shared static-camera world-collision probe: ray-start height above the candidate / clearance above the highest hit. The retail executable names the second key `crash_min_elev`; the extracted reader exposes it as `crash_elev`. Applies to crash, death, and flyby placement. |
| `flyby_min_watch_time` / `_max_` | 3.8 / 4.3 | Random seconds the flyby camera must watch before distance may request a re-site. |
| `flyby_min_radius` / `_max_` | 5.5 / 7.0 | How close the flyby camera sits to the flight path. |
| `flyby_min_interval` / `_max_` | 1.9 / 2.3 | Random seconds of velocity projection in the flyby placement: `speed · interval` becomes longitudinal distance. It is not time between re-sites. |
| `flyby_min_switch_dist` / `_max_` | 70 / 85 | Random distance threshold tested after the watch deadline; exceeding it requests a re-site on the next frame. |
| `flyby_z` / `flyby_y` / `flyby_min_alt` | 0 / 0.1 / 0.1 | Flyby placement offsets. |

## Static death, crash, and flyby cameras

All three cameras share one lifecycle: a world point chosen once, held, and re-aimed at the aircraft
every frame. The mode setter `FUN_0042c280` raises the shared "needs placement" flag `DAT_0064ef2c`
from a 10×10 from/to table at `00621380` (a zero entry means place); entering mode 8 or 9 from any of
the three player views is a zero entry, so it always places. Each mode's own per-frame handler
consumes the flag. **Every random draw is `rand() × 3.051851e-05`, which is `rand()/32767`, uniform
on [0, 1].**

All three then pass the candidate's Y through one world-collision clearance. `crash_chord_y` raises
the start of a vertical ray above the candidate; the ray ends **1000 m** below it, a hard-coded
literal rather than a field. When it hits terrain, the candidate rises to at least
`highest_hit_y + crash_elev`, and never falls. ⚠ **The probe hides the camera's own aeroplane
first** (`FUN_004cca30(DAT_0071c298 + 0xc, 0)`, restored from the saved flag bit afterwards), so an
aircraft is never its own obstacle. The executable calls the second field `crash_min_elev`, which
describes its role; `crash_elev` is the extracted reader's spelling. The rule is `FUN_0042c390`,
called through `FUN_0042c580` by crash (`0042e041`–`0042e04e`), death
(`0042e1d7`–`0042e1e4`), and flyby (`0042e387`–`0042e394`). ⚠ **No chase pose is lifted.** The chase
path calls a clearance hook of its own, `FUN_0042c5a0`, and in the retail build that function is a
stub: it computes `y = (y − 1) + 1` and returns 0.

The crash camera is **mode 5**, entered on a fatal ground impact. `FUN_0042ce00` places once through
`FUN_0042df90`, which offsets the aircraft's world position by `crash_horiz` along the horizontal
bearing of its own basis and `crash_y` up, then takes the shared clearance; the handler then holds
that point and re-aims at the aircraft, like modes 8 and 9.

The death camera is mode 8. On entry it chooses one fixed world point:

1. Draw `θ` uniformly on [0, 2π) and form the local offset
   `(death_x·cos θ, death_x·sin θ, −(speed·death_interval + death_z))`.
2. Transform that offset through the aircraft basis and add the aircraft world position.
3. Add `death_alt` to world Y, clamp Y to `death_min_alt`, then apply the shared clearance.

`FUN_0042e0b0` performs the placement (`0042e0c3`–`0042e1e4`). The camera holds the resulting
world point and re-aims at the aircraft every frame; it has no timer-driven re-frame. Mode 8 is
entered for the destroyed player when callback event `0x0f` finds the per-aircraft one-shot flag at
`obj + 0x91f` set on the player's own object (`DAT_0071c298`); the handler clears the flag and calls
the mode setter (`00470912`–`0047093c`, and a second path at `00480764`–`00480794`). The respawn
routine `FUN_0047f1f0` clears that flag and returns the camera to mode 6.

The flyby camera is mode 9, entered by the F7 handler at `00489430`–`00489447` while the player is
alive (`player + 0x91d` clear). Each re-site (`FUN_0042e1f0`, `0042e1f0`–`0042e3f6`):

1. Draws `a` uniformly on [−π, π) and forms `θ = a·(5/6) ± 0.2617994`, the sign taken from `a`'s.
   That leaves two 30°-wide excluded arcs, one centred on `θ = 0` and one on `θ = ±π`.
2. Draws a radius uniformly from `flyby_min_radius..flyby_max_radius` and forms the local offset
   `(radius·sin θ, radius·cos θ, −(speed·interval + flyby_z))`, `interval` drawn uniformly from
   `flyby_min_interval..flyby_max_interval`.
3. Transforms the offset through the aircraft basis, adds `flyby_y` to world Y, clamps to
   `flyby_min_alt`, and applies the shared clearance.
4. Draws a watch duration and a switch distance from their authored min/max pairs. The watch is
   stored as an ABSOLUTE game time (`DAT_0071c470 + watch`), not a countdown, and the switch
   distance is stored SQUARED, because the test it feeds is against a squared distance.

⚠ **Sine and cosine are swapped between the two placements.** The death camera puts the cosine on
the first local axis and the sine on the second; the flyby puts the sine first. With the flyby's
wedges excluded about `θ = 0` and `θ = ±π`, that keeps a flyby spot off the aircraft's own vertical
and out on its flanks, while the death camera's angle is unrestricted.

`FUN_0042db40` holds that world point and re-aims at the moving aircraft each frame. After the watch
deadline, distance beyond the chosen switch threshold requests a re-site; because the re-site check
precedes the distance check, the move occurs on the next frame. Every re-site redraws all five
random choices. Mode 9 has no internal exit; another camera-mode transition ends it.

`FUN_0042f700` maps the raw floats without conversion: death fields occupy table offsets
`+0x30..+0x40`, crash placement/clearance `+0x44..+0x50`, and flyby `+0x54..+0x7c`. Position fields
are metres. The two `*_interval` values are seconds because they multiply speed to produce metres;
only `flyby_*_watch_time` is a dwell.

The vector-component formulas and basis transform are exact. Which end of the flight path the third
local component points at is an interpretation: the executable names no axis, and the model frame's
own two readings disagree. **The flyby's lifecycle settles it as AHEAD.** Placed behind, a camera
starts `speed · interval` metres away (about 200 m at 100 m/s), already past the 70-85 m switch
distance, and would watch a receding dot for the whole 3.8-4.3 s watch time before re-siting.
Placed ahead, the aircraft closes over that same interval, passes at the authored 5.5-7.0 m radius,
and is past the switch distance roughly when the watch expires, which is what the four authored
ranges are proportioned for. Mapping the components straight onto this codebase's own plane basis
(nose at −Z, `docs/formats/gotchas.md`) gives exactly that, and the `flyby-camera` suite measures
the closest approach at 6.0 m to prove it.

## The distance law

The engine builds an external camera's distance in three steps, decoded in
[`../org/cameraViews.md`](../org/cameraViews.md), "The external camera's distance, and the zoom
axis that only goes outward": a speed law with an acceleration term, a clamp into an authored
bounds pair, then the pilot's zoom added outward on top. What that settles about this file:

- **`dist_min`/`dist_max` clamp the speed-driven distance**, and the pair is chosen by where the
  camera points: forward-facing takes these two, the look-behind takes `back_dist_min`/`_max`, and
  a partly swung view takes the linear blend. So the `default` block's `dist` 13.0 sitting *below*
  its own `dist_min` 15.7 is not a contradiction: the four airframes with no block of their own
  ride the near bound at 15.7 rather than 13.0, and the seven with a block author `dist` equal to
  `dist_min` so that the bound and the rest pose are the same number.
- **`dist_vary` is the throttle transient's gain and `dist_catch_up` its relaxation rate.** The
  term is `dist_vary·(V − V̄)`, with `V̄` a lagged copy of speed eased at `dist_catch_up` on the
  engine's per-frame dt, which is wall time (`0059c0c0`, a `GetTickCount()` delta). Under steady
  acceleration that settles at `dist_vary/dist_catch_up`
  metres of excess per m/s², i.e. the shipped 0.1, against CAP-21's measured **0.105**, 5%
  agreement. The clip's raw relaxation figure, **0.90 per wall-second**, is within 10% of the
  shipped `dist_catch_up` 1.0 in the same clock.
- **`pos_catch_up` 2.0 and `look_catch_up` 3.0** are the position and aim easing rates in the same
  exponential, scaled up by how far the view is swung off the flight path.

⚠ **`dist_max` is not the far end of the pilot's zoom.** The zoom is a flat 10 m added after the
clamp, so the travel available runs from `dist_min` out to `dist_min + 10`, and a fast enough
aircraft sits at `dist_max + 10` with the axis fully out.

## Known limits

⚠ **`thirdp_height`'s units are unknown**, so CSVM takes only the *radius* from this file.
`thirdp_pitch` IS the chase offset's own elevation, though: the placement adds it to the head's
smoothed elevation and builds the direction from that pair (`docs/org/cameraViews.md`, head-look
controller), so with the head settled the original's camera sits dead astern at 0.29° rather than at
the 15.7° CSVM's hand-picked pair holds. Correcting that is `BL-885`, since it moves every chase
shot and wants judging at the controls.

## Throttle transient

Beyond the authored `d = dist + dist_factor·V`, the original's chase distance carries a **transient
in the along-path acceleration**: slam the throttle open and the camera falls back, cut it and the
camera closes in, both relaxing back onto the speed law. It is `dist_vary`/`dist_catch_up` (see
"The distance law"), and the engine reads the two fields off `CamParams`. The Bloodhawk staircase
clips (`CAP-21`) are corroboration, not the source, and they agree to 5% and 10%:

| Quantity | Authored | Measured on the clips |
|---|---|---|
| Relaxation rate | `dist_catch_up` = **1.0 /s** (τ = 1.0 s) | **0.90 /wall-s** (τ = 1.11 wall-s) |
| Steady-state excess | `dist_vary`/`dist_catch_up` = **0.1 m per m/s²** | **0.105 m per m/s²**, residual-vs-`dV/dt` correlation **−0.79 to −0.85** in all four takes |
| Peak excursion | - | **≈ +15 % of the radius** on a full-throttle slam, **≈ −7 %** on a full cut, which is the part the eye actually sees |

⚠ **`dist_catch_up` is a REAL-second rate, and `k = 1.390` does not belong on it.** The engine eases
the lagged speed on `DAT_009ad744`, which `0059c0c0` builds from a `GetTickCount()` delta in
seconds, so the rate is per wall second and the clips' raw 0.90 is directly comparable to it. The
sim-converted 0.65 that `k` produces compares the wrong pair of numbers: verification `DET-11`'s
conversion applies to a duration read off a world that runs fast, not to an easing rate the engine
itself denominates in wall time. So the authored 1.0 goes in unconverted.

The radius advances once per SIM step and never per render frame, so the lag sees one cadence and a
halted or crashed sim freezes the radius with everything else. **That sim step is Godot's 60 Hz
physics tick, and it carries exactly one wall second per wall second in a realtime session**, so the
per-real-second rate is applied per real second and the easing is left on the sim clock: measured
over a flown C1 chase and over a C2/M02 flight with the mission's generators and zeppelins, the
tick held 60 steps per wall second at about 115 fps, and the transient took 2.15 seconds to fall
to 1/e on either clock. Godot drops sim time only when a rendered frame needs more than its
eight-step catch-up budget, which is below 7.5 fps. ⚠ Under `--det` the cadence is instead one
1/60 s step per rendered frame, so the transient plays at the render rate over 60 in wall terms and
a scripted capture pins its shape and not its pacing (verification `DET-15`).

⚠ **The look-behind inversion is not ported.** The original multiplies the transient by the view
direction factor, which the look-behind arm hard-codes to `−1`, so a slam pushes that view's camera
*in*. CSVM's look-behind takes the forward radius whole and has no direction factor to invert.

## Engine-read fields

`CSVM/src/Flight/Camera/CamParams.cs` parses the whole file and exposes every field;
`CSVM/src/Flight/Camera/CameraController.cs` applies:

- `dist` + `dist_factor`, the dynamic chase radius (shared by the numpad fixed views, so both
  cameras move together, dynamics included).
- `dist_vary` + `dist_catch_up`, the throttle transient on top of that radius (`DistTransient`),
  both per real second.
- `dist_min` / `dist_max`, the bounds that radius is held inside for every forward-facing pose,
  and so the pose the view rests at (`ExternalRadius`).
- `crash_horiz` / `crash_y`, the crash camera's hard-cut pose (`CrashView`).
- `back_dist_min` / `back_dist_max`, the look-behind view's distance bounds (`BackView`,
  numpad 0 / `--view=back`), which take no zoom.
- `crash_chord_y` / `crash_elev`, the shared terrain clearance
  (`StaticCameras.LiftClearOfWorld`), taken by the crash cut, the death camera and the flyby.
- every `death_*` field, the death camera (`StaticCameras.StepDeath`), entered when the player's
  own aircraft is destroyed and held while the wreck falls.
- every `flyby_*` field, the flyby (`StaticCameras.StepFlyby`), entered on F7 or `--view=flyby`.

The engine suites `death-camera` and `flyby-camera` fly both on the empty stage and read the spot
each one chose. What no instrument settles is PRESENTATION: whether the original's own death shot
and flyby read the way these do at the controls. See
[`docs/org/cameraViews.md`](../org/cameraViews.md) for the camera-state dispatch and lifecycle.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
