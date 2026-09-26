# Per-view cameras and base field of view, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-08-18. The player's first-person camera
positions are additionally read from the decoded plane scene graphs under
`extracted/…/planes/nodes.json`. Every claim below names the function or address it came from.

Everything here is a description of *behaviour*. No decompiler output is reproduced; the
addresses are given so any claim can be re-checked at source.

⚠ **This page is a decode, not a proposal.** Where it disagrees with a remembered behaviour, the
decode wins and the disagreement is a note.

**Where the neighbours live.** The engine's *external-camera tuning* (distance, catch-up, third-person
eye height and pitch, look-behind, death/crash/flyby placement) is the shared zrdr reader
[`../formats/camparam.md`](../formats/camparam.md), that page carries the field rules but
no FOV and no first-person data. The crash, death and flyby geometry and lifecycle live on that
page too, field formulas included.

## The headline

The original has **exactly two base horizontal field-of-view numbers, 60° and 80°**, and which one
applies is decided **per camera mode** (the live mode at `camera + 0x14c`). The wide 80° belongs to
**exactly one view, the Cockpit view (mode 6)**; every other mode, including the Nose view (mode 7)
and all 3rd-person/chase modes, uses 60°.

Two findings will not be guessed correctly:

- **The Nose view and the Cockpit view are the same camera point.** Both place the camera at the
  plane's **`cockpit_camera` marker**, there is **no separate nose camera marker and no per-mode
  camera offset** anywhere. Mode 7 (nose) is the same physical position as mode 6 (cockpit); it
  differs in *rendering* (the cockpit interior and some plane nodes are hidden), runs player
  head-look without the Cockpit-only autohead, and uses the narrower 60° FOV.
- **A single 62° vertical FOV, which the port assumed before this decode, does not exist in the
  binary.** The 62°-in-radians constant `1.082104` (`63 82 8a 3f`) is absent. The base is **60°
  horizontal FOV**, with the single **80°** first-person cockpit exception.

## How the per-view FOV is gated

The live camera mode lives at **`camera + 0x14c`** (a byte of the camera object
`DAT_0064ef78`; the same register that decides first-person placement). The per-frame FOV getter is
`FUN_0042b660`:

| Gate | FOV function | Constant |
|---|---|---|
| `camera + 0x150 != 0` **and** `camera + 0x14c == 6` | `FUN_006024d9` | **80°** horizontal (`1.3962634` rad) |
| any other mode | `FUN_00602508` | **60°** horizontal (`1.0471976` rad) |

`camera + 0x150` is the plane bound to the camera (non-zero in normal play on the player's plane),
so in practice the gate is purely "is the mode 6".

## The camera modes

Every mode `0`–`9` is dispatched by `FUN_0042c5c0` (the camera-object position dispatcher;
the scene-object twin `FUN_0042dc20` mirrors it) to a per-mode placement, then hands to the
shared render-camera `FUN_0042ba70`. Only **modes `6` and `7` are first-person**, and only
**mode `6` is 80°**.

| Mode | Position dispatcher | View | FOV (H) | ~V @ 4:3 | Interior | Head-look |
|---|---|---|---|---|---|---|
| `0` | `FUN_0042c7f0` | **Chase / 3rd-person** (smoothed, `camparam` distance) | 60° | 46.8° | - | look-around, floor `−π/2` |
| `1` | `FUN_0042ca50` | Chase behind (fixed world angle) | 60° | 46.8° | - | - |
| `2` | `FUN_0042c7f0` +flag | Chase variant (fixed-scale) | 60° | 46.8° | - | - |
| `3` | `FUN_0042cb70` | Chase behind, turn-flippable (+side) | 60° | 46.8° | - | - |
| `4` | `FUN_0042cb70` +flag | Chase, same base, −side offset | 60° | 46.8° | - | - |
| `5` | `FUN_0042ce00` | **Crash**, one world point `crash_horiz`/`crash_y` off the impact, continuously aimed at the aircraft | 60° | 46.8° | - | - |
| **`6`** | `FUN_0042d980` | **Cockpit** | **80°** | 64.4° | **drawn** | look-around + autohead, floor `0` |
| **`7`** | `FUN_0042d980` | **Nose** | 60° | 46.8° | hidden | look-around, floor `0` (autohead off) |
| `8` | `FUN_0042cf10` | **Death**, one fixed world point chosen on callback event `0x0f`, continuously aimed at the destroyed player | 60° | 46.8° | - | - |
| `9` | `FUN_0042db40` | **Flyby**, camera holds a **world** position, re-aims at the plane, re-sites to a new spot on the `camparam` flyby trigger | 60° | 46.8° | - | - |

The modes pair up around shared handlers: `(0,2)`, `(3,4)` and `(6,7)` share a placement
function and differ only by the boolean flag each passes in, in `(6,7)`'s case the difference is
the cockpit-vs-nose split below. Modes `1`, `5`, `8`, `9` each have a handler of their own. All of
the non-first-person handlers read distance/eye geometry from the `camparam.json` chase table
(`DAT_0064efd0`), so they are all chase/external poses rather than first-person ones.

### Modes 5, 8 and 9 are the STATIC cameras, and they share one lifecycle

The crash cut, the death camera and the flyby are the three poses that hold a WORLD point instead of
riding the aeroplane. Each per-frame handler opens the same way: if the shared placement flag
`DAT_0064ef2c` is set, choose a point and clear the flag; then sit on that point and re-aim at the
aircraft's world position. The mode setter `FUN_0042c280` raises the flag from a 10×10 from/to table
at `00621380`, and entering `8` or `9` from any of `0`, `6`, `7` raises it. They also share one
terrain clearance, which no chase pose takes: `FUN_0042c5a0`, the chase path's own clearance hook,
is a stub in the retail build. The field-level formulas, the clearance and the flyby's re-site
cadence are in [`camparam.md`](../formats/camparam.md).

### Only three views are player-selectable: Chase `0`, Cockpit `6`, Nose `7`

The player-facing view selector falls back to exactly three valid modes. `FUN_0042c210` rejects
any candidate that is not `0`, `6` or `7` (it stores the accepted value in `DAT_0064ef94`);
`FUN_0042c1f0()` just reads that stored value back. The selector key handler `FUN_004414a0` takes
the requested view and, if it is not one of `{0, 6, 7}`, falls back to **6** (cockpit) before
applying it through the setter `FUN_0042c280`. `FUN_00443de0` applies the stored desired view at
game start / on the cycle key. So the selectable set is:

| Mode | Player-selected view |
|---|---|
| `0` | **Chase** (3rd person) |
| `6` | **Cockpit** (interior, 80°, free-look) |
| `7` | **Nose** (no interior, 60°, player head-look with autohead off) |

Modes `1`, `2`, `3`, `4`, `5`, `8`, `9` are **not** accepted by the player's three-view cycle,
they are internal or context camera modes, which is why `FUN_0042c210` rejects them. Mode 9 has a
separate direct input path: the alive-player F7 handler enters it at `00489430`–`00489447`. The
controls data names F7 **"Access Chase View"** (`MSG_LOOK_FLYBY`) and the cycle binding **"Cycle
Cockpit Views"** (`MSG_LOOK_FORWARD`), which walks Cockpit → Nose → Chase.

**"Access Chase View" is the flyby, a world-fixed, re-siting camera**, not the ordinary following
chase. F7 enters mode **9** through the alive-player input path at `00489430`–`00489447`. Its
handlers are `FUN_0042db40` (per-frame hold/aim/transition) and `FUN_0042e1f0` (re-site).
`FUN_0042e1f0` chooses a fresh world position from the authored random radius and
velocity-projected longitudinal ranges, applies the shared static-camera world-collision clearance, and
stashes it in `DAT_0064ef08/0c/10`. `FUN_0042db40` holds that position and re-aims at the aircraft
each frame. Once the randomized watch deadline has passed, exceeding the randomized switch distance
requests a re-site on the next frame; every re-site redraws the placement, watch, and threshold.
The exact field formulas and provenance are in [`camparam.md`](../formats/camparam.md). The options menu labels the
positions "external" (`MSG_OPT_3RD_PERSON`), "cockpit" (`MSG_OPT_COCKPIT`) and "default view"
(`MSG_OPT_DEF_VIEW`).

### Mode 8 is the death camera

Both death-effect callback paths consume event `0x0f`, require the armed entity to be the player,
and enter mode 8 (`00470912`–`0047093c`, `00480764`–`00480794`). The gate is a per-aircraft one-shot
flag at `obj + 0x91f`, which the handler clears, plus the object being the player's own
(`DAT_0071c298`). `FUN_0042e0b0` chooses one fixed world point from the `death_*` fields when the
mode is entered. `FUN_0042cf10` then holds that point and re-aims at the destroyed player every
frame; there is no periodic re-frame. The respawn routine `FUN_0047f1f0` clears the flag and returns
the camera to mode 6. The friendly semantic name of callback event `0x0f` remains unknown; its gate
and effect do not.

⚠ **This label collides with the chase camera's own look-around key set.** The chase camera runs
the same head-look controller decoded below through
`FUN_0042c7f0`, driven by the same numpad snap cluster and menu-labelled `F9`-`F12` **External
Camera** keys, and the menu's `F7` **Access Chase View** binding is exactly this section's
"Access Chase View", i.e. the flyby (mode 9), not the look-around. CSVM binds `F7` to the flyby for
that reason, and the snap cluster, the centre key and the mouse to the one head in every view; the
four **External Camera** keys are left unbound, since they are not among the controller's own key
slots and CSVM already spends `F10`-`F12` on its own instruments.

### The in-binary strings expose no view-name tokens

Beyond those binding labels, the executable holds **no friendly view-name strings** for the modes
(`Chase`, `Cockpit`, `Nose`, … used as display text). The HUD initializer `FUN_00454e70` reads only
two keys from the HUD data archive (`hud_v2.zrd`): `POSITION_1ST` (`00624f28`) and
`POSITION_3RD` (`00624f38`). ⚠ **Despite the names, these are not a per-view gauge layout.**
`FUN_00454e70` reads them into a text widget built per section (`AIR_SPEED`, `ALTIMETER`, `GUNS`,
`MISSILES`, `HEALTH`, `NITRO`), the six values share one x at 0.02 spacing in y, and the column is
written only under `DAT_00624df0`: a debug text readout, not dial placement
(`docs/formats/hud.md`, "Cockpit gauges"). So the small per-view display names seen in-game come
from the HUD/video-menu **data files**, not the ship binary, but the *selection wiring* above pins
which mode is which player view.

### Modes 6 and 7 are the only first-person views

- Both route through the first-person placement `FUN_0042d980`; `FUN_0042dc20`/`FUN_0042c5c0`
  dispatch themselves, neither uses chase-position math.
- Both set the first-person flag `DAT_009fd17c` via `FUN_004e7100`.
- The mode setter `FUN_0042c280` treats exactly `6` and `7` as the first-person pair (both reach
  `FUN_004e7100` with a set arg; the other modes branch elsewhere).

### Mode 6 = Cockpit (interior rendered)

The decisive signal is a **render gate in the per-frame player draw** `FUN_0049fb00` (its sole
caller is the main game tick `FUN_004a0220`): the **cockpit interior model `cockpit1`
(`DAT_0071c314`) is drawn only when `camera + 0x14c == 6`**. The interior node is shown via
`FUN_004cca30(..., 1)` before the interior draw and hidden again via `FUN_004cca30(..., 0)` after
it. So mode 6 is the interior cockpit view, and it is the only view that draws it. Mode 6 also runs
the wider 80° FOV.

### Mode 7 = Nose (no interior, autohead off)

- The interior is **not** drawn (the `FUN_0049fb00` gate requires mode 6).
- `FUN_0042d980` calls the head-look controller `FUN_0042d010` **unconditionally in both
  first-person modes**, mode 7 does not disable head-look. What mode 7 gates off is only the
  **autohead** (idle turn lead) branch inside that controller; player-driven look (snap,
  free-look, center key) is identical in Cockpit and Nose. A third caller runs the same controller
  for the chase camera: `FUN_0042c7f0` calls `FUN_0042d010(0xbfc90fdb, 0)`, the literal
  `0xbfc90fdb` is `−π/2`, so chase gets a full elevation range where first person is floored at
  level (0). Decoded fully, with the states/constants/smoothing law, in
  ["Head-look controller"](#head-look-controller) below.
- In the per-view object handle `FUN_0042e5e0`, mode 7 additionally hides the `dontmove`
  (`DAT_0071c30c`) and `markers` (`DAT_0071c310`) scene nodes during the render (and, like mode 6,
  hides the plane's `healthy` body `DAT_0071c308` during the render), leaving an unobstructed
  forward look. **Contents, census over all 11 airframes:**
  `markers` is 25 nodes, 24 mesh-less reference points (`cockpit_camera`, `target`, `ground_level`,
  `pylon1-8`, `firepoint1-8`, `map`, `exhaust1/2`, `ladder_pos`, `cf_light`) plus one mesh,
  `cockpit_light`, the cockpit lamp; `dontmove` is the propeller group (`wing_flare1/2`,
  `staticprop1`, `prop1`, `prop1b`, `nitroprop1`). So mode 7's extra hiding is the prop disc plus
  the cockpit lamp and whatever hangs on the marker nodes (mounted pylon ordnance, muzzle flashes).

## FOV constants and aspect correction

The two base numbers are stored **in radians as horizontal half-angle constants**, side by side in
a data table at `0060409c` (`60°`, `80°`, `50.0`, `2.5`):

| Constant | Float | Bytes | Degrees (H) |
|---|---|---|---|
| `1.0471976` | half of 60° | `92 0a 86 3f` | **60°** |
| `1.3962634` | half of 80° | (table at `0060409c`) | **80°** |

The `0x3f860a92` 60° literal is also used by the player aim-camera `FUN_0049d940` and the main
tick `FUN_004a0220`; `FUN_0042b570` (frustum/projection setup) carries `0.5235987755982` = 30° =
60°/2. FOV is stored in radians, the animated-in-script loader `FUN_00502da0` converts degrees to
radians via `0.017453292`.

The two FOV functions aspect-correct the stored **horizontal** angle to the stored **vertical**
value. The original ships 4:3 modes only (a 16:9 capture is dgVoodoo presenting that 4:3 render),
so there is one aspect in play and the correction is the ordinary one at it:

```
vertical = 2 · atan( tan(H/2) / (4/3) )
```

Applying it: 60° H → **46.8° V**; 80° H → **64.4° V**. Those verticals are the **4:3** frustum's,
not a 16:9 one's. The projection is built out of the horizontal half-angle, so the *base* number to
store or port is horizontal.

⚠ **The factor 0.75 has two readings and only one is real.** `1/(4:3)` and `(4/3)/(16/9)` are both
exactly 0.75, so a live-aspect divisor fits the pinned numbers just as well as the plain conversion
does and cannot be told apart by arithmetic. The 4:3-only mode list is what separates them: there
was never a second aspect for the engine to divide by. Reading the coincidence the other way makes
the horizontal angle invariant and the vertical shrink as a viewport widens, which is what CSVM
shipped until `CameraController.HorizontalToVerticalFovDeg` dropped the live-aspect term.

CSVM's port therefore takes these verticals as fixed and lets the horizontal grow with the viewport:
80° across at 4:3, 96.4° at 16:9, 131.8° at 32:9, one law for a fullscreen window and a splitscreen
pane alike. Aspects past 4:3 are outside anything the original ran, so this is the port's own choice
about a case the decode does not cover, not a reading of the binary.

⚠ **The two `CAMERA_STATE` / `CAMERA_FROM_TO` functions (`FUN_00502da0`, `FUN_00503e70`) are
animated/in-script FOV changes only** (`.ani` `H_FOV`/`V_FOV` events). They are not the base
per-view FOV and must not be wired to the engine's base FOV. The `.ani` cockpit sequence
(`player-gi_1stperson`) carries **no** FOV, it only shows/hides the interior and plane, so the
base FOV is not authored in animation data.

## Where the first-person camera sits

Both modes 6 and 7 run the same placement `FUN_0042d980`:

```
camera_world = plane_world_pos + plane_rotation · (DAT_0071c328, DAT_0071c32c, DAT_0071c330)
```

`(DAT_0071c328, 32c, 330)` is the plane-local offset of its **`cockpit_camera` marker**, read out
by the cockpit loader `FUN_00473480` from the `cockpit_camera` scene node (via `FUN_004d1cc0`),
with a fallback of `DAT_0075d1b8/bc/c0` = `(0,0,0)` (the plane origin) when a plane has no such
node.

⚠ **No wobble is added at placement, the first-person camera inherits it from the plane.**
`FUN_0042d980` reads the plane's raw orientation basis directly and applies zero shake of its own;
the random-walk wobble state is written onto the **camera object** (component blocks via
`FUN_0042c070`, e.g. high_speed block 4 at `camera+0xd4/+0xd8/+0xdc`) and consumed by
`FUN_0042c0e0` (in the render layer) to rock the **plane node's** rendered rotation. So both 6 and
7, being plane-mounted, inherit the wobble automatically and are not handled differently from each
other, see
[`shakes.md`](shakes.md). The chase/3rd-person modes read the plane position/attitude but sit
outside the rocking node, which is why `damage_shakes` gives the chase camera its own authored
half.

### Per-plane `cockpit_camera` offsets (local body frame)

Decoded from the plane scene graphs (`extracted/…/planes/nodes.json`). The marker sits on the
fuselage centerline, a bit above the local origin; the exact number is authored per plane. The
current player fighter `player_pfighter` uses `(0, +0.75, −0.2)`, ~0.75 up, ~0.2 aft of the local
origin. Selected values:

| Plane node | `cockpit_camera` translate (x, y, z) |
|---|---|
| `player_pfighter` (default fighter) | `(0, +0.75, −0.2)` |
| (another) | `(0, +0.70, −0.34)` |
| (another) | `(0, +0.83, +0.72)` |
| (another) | `(1.98, +0.75, −1.21)` |
| (another) | `(−1.8, +0.95, −0.44)` |
| (another) | `(0, +2.31, +3.30)` |

### Axis convention (fixed from the default fighter's cockpit sub-nodes)

- **+Z = forward (nose)**, the elevators `rt_elev2`/`lft_elev2` sit at **z = −37** (the tail) and
  the pilot seat `pass_st` at **z = +37.86** (forward).
- **±X = wingspan**, the ailerons sit at **x = ±63**.
- **+Y = up**.

So a cockpit camera at `(0, 0.75, −0.2)` is on the fuselage centerline, 0.75 units above the local
origin and marginally toward the tail. There is **no `nose_camera` node and no per-mode offset**,
mode 7 reuses the exact `cockpit_camera` point.

⚠ **This is the ORIGINAL BINARY's own internal convention, not Godot's.** It disagrees with this
codebase's own, far more broadly established one: `docs/formats/gotchas.md` (censused over 6,728
`AT_NODE`/`PUFFER_STATE` uses) puts the extracted frame's nose at **−Z**. `PlaneBuilder`/
`SceneBuilder` never axis-flip a GameZ node's local transform when building the Godot tree, so a
raw translate like `cockpit_camera`'s lands in Godot's frame unswapped and is taken as-is, CSVM
places a Godot camera relative to a Godot-space plane using Godot-space marker data, the same as
every firepoint and pylon, without ever needing to resolve which convention the original binary
used internally.

## The external camera's distance, and the zoom axis that only goes outward

`FUN_0042c7f0` places every external camera on the aircraft, the look-behind included: its second
argument is the look-behind flag, and the two arms differ only in which bounds pair they take and
whether the zoom is applied. Field offsets into the resolved `camparam` block (`FUN_0042f700`,
the live block pointer `DAT_0064efd0`) are `+0x00` `dist`, `+0x04` `dist_factor`, `+0x08`
`dist_vary`, `+0x0c` `dist_catch_up`, `+0x10` `dist_min`, `+0x14` `dist_max`, `+0x18`/`+0x1c` the
`back_dist` pair. Distance is built in three steps, in this order:

1. **A speed law with an acceleration term** (`0042c993`-`0042c9ae`):
   `d = dist + dist_factor·V + dist_vary·(V − V̄)·f`. `V` is the aircraft's own speed
   (`[obj+0x934]`); `V̄` is a lagged copy of it in `DAT_0064ef14`, eased toward `V` every frame at
   `dist_catch_up` through the shared exponential `FUN_00460490`; `f` is the direction factor
   below. So `dist_vary` is the throttle transient's gain and `dist_catch_up` its relaxation rate,
   and the ratio `dist_vary / dist_catch_up` is the steady-state metres of excess per m/s² of
   along-path acceleration.
2. **A clamp into an authored pair** (`0042c9ae`-`0042c9d3`), chosen by where the camera is
   pointed. The forward-facing pair is `[dist_min, dist_max]` and the look-behind pair
   `[back_dist_min, back_dist_max]`; a partly swung view takes the linear blend
   `(back + fwd)/2 + (fwd − back)/2·f` on each end (`0042c8f7`-`0042c934`), with the same `f` in
   step 1. `f` is the view direction's own longitudinal component (×`0.988936`): `+1` looking
   straight back down the flight path, `−1` in the look-behind arm, which hard-codes it.
3. **The zoom axis, added OUTWARD** (`0042c9db`-`0042c9ea`): `d += zoom · 10.0`, the metre span a
   literal at `0x00603390`. It is a flat span, not a fraction of the airframe's own distance.
   `0042c9d6` skips this step entirely when the look-behind flag is set.

⚠ **The zoom axis rests at its NEAR end and can only travel outward.** Its value is `[0, 1]` with
`0` the rest pose, so the authored `dist_min` is where the view opens and `dist_min + 10` is the
far end of the pilot's travel. The far end of the pilot's travel is therefore NOT `dist_max`:
`dist_max` bounds the speed-driven distance of step 2, before the zoom is added on top of it.

The axis itself is the tail of `FUN_0042d010`. Key `0x43` (**External Camera Zoom In**, numpad
`+` in the shipped table, [input.md](input.md)) drives the target `DAT_0064ef30` toward `0` at
`2·dt`, key `0x44` (**Zoom Out**, numpad `−`) toward `1` at the same rate, clamped `[0, 1]`; the
shown value `DAT_0064ef38` then eases toward it at `1.5/s`. Zoom In is the dead direction at rest.
Three sites put the axis back to `0`: camera init (`FUN_0042b730` at `0042b779`), entering chase
mode `0` from any other mode (`FUN_0042c280` at `0042c33d`), and the head-look centre key `0x3e`
in free-look. Nothing else writes it, so a mode change always returns the pilot to the near end.

⚠ **The easing dt is WALL time, not a fixed step.** `FUN_00460490` multiplies its rate by
`DAT_009ad744`, and `FUN_0059c0c0` (`0059c0c0`-`0059c141`) builds that global once per rendered
frame as a `GetTickCount()` delta in seconds, optionally held between a min and max frame time
(`0063b160` gates the clamp, `0063b158`/`0063b15c` are its ends) and multiplied by a one-shot scale
that resets to `1.0` every frame. The only writer of that scale is `FUN_0059c1f0`, called with
`2.0` from the toggled double-speed key at `0048810f`. So every rate in this page, the head-look
smoothing and the zoom included, is **per real second**, and no measured-to-sim conversion belongs
on one.

CSVM's port is `CameraController.ExternalRadius` (steps 2 and 3), `UpdateDynamics` (step 1's speed
law) and `DistTransient` (step 1's transient, reading `dist_vary`/`dist_catch_up` straight off
`CamParams`). The direction factor `f` has no port: CSVM's look-behind takes the forward radius
whole, so the transient is never inverted and the blended bounds are never formed.

## Head-look controller

Decoded from `FUN_0042d010`. Three callers share it: the
first-person placement `FUN_0042d980` (both Cockpit and Nose, elevation floor `0`, autohead flag
cleared for Nose); the chase placement `FUN_0042c7f0` (floor `−π/2` via the literal `0xbfc90fdb`,
autohead off, at `0042c877`-`0042c87c`). CSVM's port is `HeadLook` (`src/Flight/Camera/HeadLook.cs`), one
instance per pilot, floored per frame by whichever view places it
(`CameraController.StepHead`), and turned into a chase offset by `CameraController.ChaseSwing`.

⚠ **The chase placement's own elevation is the head's plus the authored `thirdp_pitch`.**
`FUN_0042c7f0` loads the shown elevation `DAT_0064ef58` at `0042c881`, adds the camparam block's
`+0x28` at `0042c88d` (the reader converts `thirdp_pitch` degrees to radians on the way in,
`docs/formats/camparam.md`) and hands that with the shown azimuth `DAT_0064ef5c` to the direction
builder `FUN_0053f550` at `0042c8a2`. So a settled head leaves the camera dead astern at the
authored pitch, `0.29°` on every plane but Balmoral's `0.2°`, not at the `15.7°` CSVM's own
`BaseUp`/`BaseBack` pair sits at. The look-behind arm is the exception that proves the routing: with
the back flag set the routine never calls `FUN_0042d010` at all and writes the fixed direction
`(0, 0, 1)` instead.

- **State byte** `DAT_0064ef68`: `0` snap, `1` free-look, `2` padlock. CSVM carries all three as
  `HeadLook.LookMode`, written by the same three keys and padlock's exit alone, never by a device
  moving, so the numpad and the remake's mouse look both obey the mode the keys chose.
- **Mode writers.** Three command handlers own the byte, and every change passes through one of
  them: `0x00489450` writes `0` (its keybind page's "Access Snap Look Mode", key `K`);
  `0x00489460` zeroes the targets and the shown angles (`DAT_0064ef60/64/58/5c`) and then writes
  `1` ("Access Smooth Look Mode", key `J`); `0x004894a0` writes `mode = (mode == 2) ? 0 : 2`
  (`SUB EAX,2; NEG EAX; SBB EAX,EAX; AND EAX,2`, no recognised function there, read with
  `disassemble_bytes`), the padlock toggle (Track Target, key `L`, command slot `0x38`,
  `docs/org/input.md`, `MSG_CMD_PADLOCK_WATCH`, keyboard only with no joystick binding). Unlike the
  `J` handler it zeroes nothing, so the head enters and leaves padlock from wherever it was
  pointed. Camera init `FUN_0042b730` writes `0` and zeroes the
  shown angles at `0042b747`, so a level load starts in snap. A snap direction pressed while
  padlocked writes `0` through the first of these, which is how state `2` leaves by the writers it
  arrived through.
- **A frame with no look input** reads differently per state, which is the modes' real
  behavioural difference: state `0` zeroes both angles, so a released snap direction returns the
  head, while state `1` leaves them untouched, so a pan parks the head where it was pointed until
  the centre slot `0x3e` or a state `0` writer moves it.
- **Angles.** `DAT_0064ef60` is elevation above level (`0` = level, `π/2` = straight up, clamped to
  `[0, π/2]` in the input paths, the original's head never looks below level in front of the
  clamp; the caller-supplied floor above is a SEPARATE, per-caller bound); `DAT_0064ef64` is
  azimuth, wrapped to `±π` (`FUN_00460ab0`).
- **Snap (state 0).** The POV/keyboard direction becomes an angle in hundredths of degrees;
  azimuth is that angle negated, ×0.01×π/180. Elevation: within 0.1° of forward → straight up
  (`π/2`); within ±0.09° of a 45°/135°/225°/315° diagonal → 45° up (`0.7853982`), all four
  diagonals, fore and aft alike; any other direction → level. Nine key slots (`0x3a`-`0x42`)
  compose an (x, y) direction; `0x3e` is the center slot (zeroes elevation, azimuth and the zoom
  value in free-look).
- **Free-look (state 1).** Elevation `+= 2·dt·cos(hat angle)`, azimuth `+= 2·dt·sin(hat angle)` per
  frame, a pan rate of **2 rad/s** (~114.6°/s), `DAT_009ad744` the per-frame dt (appears as
  `dt + dt`). The hat angle is negated first (`×−0.01×π/180`), so pointing right pans right. The
  same eight key slots feed this arm: their composed (left, forward) pair is normalised by
  `FUN_00538aa0` and used as the (sin, cos) pair directly, so a held numpad key pans the head at
  the same rate, a diagonal included, and a frame with no slot held leaves the angles alone.
  CSVM routes the numpad through this arm in `LookMode.FreeLook` and through the snap table in
  `LookMode.Snap`.
- **Padlock (state 2).** `0042d016`/`0042d020` (`CMP [0x0064ef68],2` / `JNZ 0042d2d1`) make the
  padlock arm the fall-through; it reads the local player's target wrapper at
  `DAT_0071c298 + 0x948` (`0042d026`-`0042d033`).
  - **With a target:** the wrapper's `+4` entity yields a position through virtual slot `0`
    (`0042d054`-`0042d059`); the delta against the plane's `+0x204`/`+0x208`/`+0x20c`
    (`0042d07c`-`0042d093`) is rotated into the plane's frame by the 3×3 at `+0x180`
    (`0042d09b`-`0042d0f7`, rows `+0x180`/`+0x18c`/`+0x198`); then
    `DAT_0064ef60 = atan2(y, sqrt(x² + z²))` (`0042d10e` `FSQRT`, `0042d119`) and
    `DAT_0064ef64 = atan2(−x, −z)` (`0042d12b`).
  - **The caller's floor is the only clamp** (`0042d133`-`0042d146`,
    `if (elevation < param_1) elevation = param_1`): no ceiling, since `atan2` already bounds
    elevation to `±π/2`, and **no azimuth limit at all**, so the head reaches dead astern.
  - **No rate limit and no padlock-specific smoothing.** The targets snap to the bearing every
    frame; the shared shown-angle exponential above is the whole of the lag.
  - **No target:** `0042d035`/`0042d03f` write both targets `0.0` and `JMP 0042d14b`, skipping the
    floor clamp, and the autohead gate below then accepts the frame, so a padlocked head with
    nothing selected idles exactly as a released snap does.
  - **Leaving.** The exit scan `0042d14b`-`0042d2cc` polls the POV hat (`FUN_00536c70(0)` at
    `0042d157`, valid when `≠ 0xffff`) and the eight direction slots `0x3a`-`0x3d`, `0x3f`-`0x42`;
    slot `0x3e` (centre) is polled and its result **discarded**, so the centre key is inert while
    padlocked. `0042d2a1` (`OR ESI,EBX` / `JZ`) keeps the state when nothing is pressed, and
    `0042d2c2` (`MOV [0x0064ef68],0`) makes **any** look direction leave into snap. The scan runs
    after the bearing, so the frame a direction arrives on still aims at the target.
- **The tail wrap.** Before the exponential, `FUN_00460b10(&target, shown − π)` wraps the azimuth
  target onto the near side of the shown angle by `_DAT_0071b3fc` = `2π`, and `FUN_00460ab0` wraps
  the result back to `±π`. It runs in all three states, but only padlock produces a target at the
  `±π` seam, so a target crossing dead astern is followed **across the tail** rather than swung
  back through the nose. CSVM scopes the same wrap to its padlock arm alone, which leaves the
  snap and free-look paths (whose targets are clamped well inside `±π`) exactly where they were.
- **Smoothing.** The displayed angles (`DAT_0064ef58/5c`) approach their targets exponentially,
  `shown = target + (shown − target)·e^(−rate·dt)` (`FUN_00460490`; `FUN_00460410` a cubic Taylor
  `e^(−x)` for `x < 0.1`): elevation rate **3.0/s**, azimuth rate **5.0/s** (τ ≈ 0.33 s / 0.20 s).
  The external camera's zoom value smooths at 1.5/s, moves at `2·dt` on keys `0x43`/`0x44`,
  clamped `[0, 1]`, the section above has its direction and what it feeds.
- **Autohead** (the idle turn lead, the gated tail block `0042d61c`-`0042d7c5`): the gate is
  `(mode == 2 && no target) || (mode == 0 && no direction)` (`0042d627`-`0042d64f`), so it never
  runs in free-look. With that met and the caller's autohead argument set (the option byte
  `DAT_0071dacc` AND mode ≠ 7, so Nose gates the whole branch off, not just the input), the head is
  aimed where the nose will point `autohead_turn_time` seconds on at the present turn rate:
  1. **The vector is the angular velocity, not the linear one.** `0042d655`-`0042d660` read the
     camera subject's `+0x16c` (the plant's world-frame angular rate in its quaternion half-angle
     units, [`formats/vehicle.md`](../formats/vehicle.md) "The engine slot's pitch and gain";
     `org/flightModel.md`, "The stored vector is a quaternion half-angle rate"). `0042d68b`-`0042d6d3`
     rotate it into the body frame by the ROWS of `+0x180` (`+x` right, `+y` up, `+z` aft).
  2. **Scale and cap.** `0042d6d5`-`0042d6f6` multiply all three components by `autohead_turn_time`
     (`_DAT_0071c464`); `0042d6fe`-`0042d747` cap the vector's magnitude at `autohead_turn_max`
     (`_DAT_0071c468`), scaling all three when it is strictly greater. The roll component stays in
     the magnitude, so a fast roll shrinks the pitch and yaw share of a capped lead.
  3. **Rotate the nose.** `0042d754` passes the vector to `FUN_0053fbf0`, the plant's own exp map
     `(cos |v|, sin |v| · v/|v|)`, identity at `|v| = 0`, which turns by `2|v|`; `0042d766` rotates
     the forward vector `(0, 0, −1)` at `0x00604080` by it through `FUN_0053fb40` (`v' = qvq*`).
     The integrator `FUN_0048e580` applies the same exp map to `ω·dt` and pre-multiplies the
     orientation (`FUN_0053f920`, `q ⊗ orient`), so the rotated vector is the nose's body-frame
     direction after `turn_time` at a constant rate. The head therefore leads into a turn, and a
     plane whose rates have died has the head straight ahead.
  4. **Angles.** Elevation `atan2(y, sqrt(x² + z²))` at `0042d78e`, azimuth `atan2(−x, −z)` at
     `0042d7a0`, the padlock's own pair; elevation floored at `autohead_turn_min_pitch`
     (`_DAT_0071c46c`, `0042d7ae`-`0042d7bb`), below the input paths' own `0` floor.

  All three are `player.json` keys (loader `FUN_004735b0`, globals `0071c464/468/46c`): shipped
  `0.75`, `2.86°` (stored ×π/180 and doubled by `FADD ST0,ST0` at `004745f5` = 0.0998 half-angle,
  so the head leads the nose by at most 0.1996 rad = 11.4°; the compiled fallback `0x3dcccccd` =
  0.1 at `004745ff` is not doubled), `−3.0°` (stored −0.0524 rad). CSVM's port is
  `HeadLook.AutoheadTarget`, fed `FlightModel.BodyRates`, which is already the body-frame
  half-angle rate.
- **Fixed head-pitch offset.** `FUN_0042d980` applies a constant extra rotation of
  **−0.08203 rad = −4.70°** (`0xbda7ff58`) about the elevation axis when building the view basis,
  in both first-person modes.

## What this means for CSVM

| | Original | CSVM today |
|---|---|---|
| Per-view base FOV | two horizontal constants: **60°** base and **80°** only for cockpit mode 6 | matched: `CameraController.HorizontalToVerticalFovDeg` drives Cockpit from the 80° and every other view, first-person Nose and external alike, from the 60° base (`CameraController.ExternalFovDeg`, written by `GameSession` and `Launcher` onto every world camera) |
| FOV axis | stored/ported as **horizontal** half-angle, converted at the 4:3 it ran | matches: one horizontal base converted once, the vertical then held at every viewport shape |
| First-person pair | modes 6/7 share the `cockpit_camera` position; differ in interior render, head-look, FOV | `PilotViewMode` implements Cockpit/Nose as camera modes |
| Cockpit interior gate | drawer (`FUN_0049fb00`) draws `cockpit1` only in mode 6 | `CockpitVisibility` enforces the same mode gate |
| **Flyby** ("Access Chase View") | world-fixed, re-siting camera (mode `9`): holds a world point, re-aims at the plane, re-sites on `camparam` flyby trigger | landed as `StaticCameras`, on `F7` and `--view=flyby` |
| **Death camera** | mode `8`: one spot from the `death_*` fields when the player is destroyed, held while the wreck falls | landed as `StaticCameras`, entered by the player's own destruction |
| Static-camera terrain clearance | `crash_chord_y`/`crash_elev`, taken by the crash cut, the death camera and the flyby alike | landed as `StaticCameras.LiftClearOfWorld`, taken by all three |
| Camera position | per-plane authored `cockpit_camera` offset, read from the model (`player_pfighter` `(0,0.75,−0.2)`) | landed: `MarkerRig.FindNamedMarker` / `PlaneBuilder.CockpitCameraOffset` (A2) |
| Head-look controller | snap, free-look, padlock, center key, autohead, one shared state machine, three callers (first person + chase) | landed as `HeadLook`, one head for every view: the snap cluster, the centre key and the mouse aim the cockpit and swing the chase camera alike, each frame floored by the view that places it, and `K`/`L`/`J` state the mode the original's three selectors state, which the numpad and the mouse both obey |
| Padlock (Track Target) | state `2`: the head snaps onto the selection's bearing every frame, floored by the caller and unlimited in azimuth, any look direction returning it to snap | landed as `LookMode.Padlock`, reading `TargetSelection.Current` through `HeadLook.TargetOffset` and following the same tail-crossing wrap |
| Chase base elevation | the head's elevation plus the authored `thirdp_pitch`, i.e. dead astern at `0.29°` with the head settled | a hand-picked `15.7°` from `BaseUp`/`BaseBack` (`BL-885`) |

The camera is placed faithfully today: the plane's `cockpit_camera` offset read from the model (no
hardcoded 0.75), both first-person views sitting at it, the interior drawn + head-look + 80° for
Cockpit, the interior hidden + head-look with autohead off + 60° for Nose, and 60° for every
3rd-person mode, the chase and the nine fixed numpad poses, look-behind, the pad look-around, the
crash and death cuts and the flyby, plus the debug freecam and animation lab that draw the same
world. The static model viewer keeps its own 50° framing: it shows a model on a stage, not one of
the original's views.

⚠ **Two fits made against the old 62° assumption are owed a re-judgement, not a re-measurement.**
The overcast sky match and the tracer screen-size floor (`docs/org/tracers.md`, "How the floor
itself is computed") were each settled by eye or by arithmetic against a 62° vertical frustum, and
a 46.8° one magnifies the same world by 1.39× at the same distance. Re-fitting either against the
same footage without re-judging what it should look like would carry the old base forward inside a
new number.

**The interior is authored in its own space, and the two spaces are not a similarity apart.** The
eye sits at `cockpit1`'s origin looking down −Z (`extracted/zrdr/instruments.zrd.json` places the
instrument panel at z −17.5 straight ahead of it), while the interior's own elevators sit at
y −10.5 where the exterior's sit at −0.40. It is a stylised model built to be looked at from one
point, not a scaled copy of the aircraft, so the framing is scale-invariant and
`PlaneBuilder.InteriorScale` chooses only how the interior composites against world geometry. The
original draws the interior in its own pass from the interior origin along the interior's own −Z,
which is why a single-pass renderer says the same thing by mounting the subtree at the −4.70°
head-pitch tilt: that tilt is what puts the gunsight on the guns, and head-look is deliberately not
applied to the mount.

## Not resolved

- **The non-selectable modes (`1`, `2`, `3`, `4`) still have no friendly name or
  identity.** The player-view selector (`FUN_0042c210`) rejects all of them, so none is a
  player-selected view; they are internal/context camera poses. Modes `5`, `8` and `9` are settled:
  the **crash** cut, the **death camera** and the **flyby**, the three static cameras above.
- Which of the 22 `cockpit_camera` offsets corresponds to each named player airframe by display
  name (the node→display map lives in `../formats/markers.md`); only `player_pfighter`'s is
  pinned here.
- **The tilted interior mount overshoots by 0.60°.** It leaves the gun pipper about 6 px above the
  sight ring's crosshair at 720p where the original has them coincident. The exact fit is a 3.82°
  tilt, but that is a Bloodhawk-fitted number with no decode behind it and the sight's height is
  per-airframe geometry, so the decoded constant is what ships. Measuring the same offset on a
  second airframe's cockpit footage is what would settle whether the constant should become a TUNE.
