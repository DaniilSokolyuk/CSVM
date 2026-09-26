# The spyglass and the off-screen target marker, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`). Every claim below names the function or address it
came from, and the window and camera dimensions come from the extraction's own `gamez` nodes.

Everything here is a description of *behaviour*. No decompiler output is reproduced; the addresses
are given so any claim can be re-checked at source.

⚠ **This page is a decode, not a proposal.** Where it disagrees with a screenshot reading or with a
remembered behaviour, the decode wins and the disagreement is a note.

**Where the neighbours live.** Who the target is, how the cycle picks it, the bracket box, the
three label lines and the marker colour rule are [`targeting.md`](targeting.md). The player's own
view cameras, their field of view and the head-look controller are
[`cameraViews.md`](cameraViews.md). The command's binding and the whole action table are
[`input.md`](input.md). The fog range this page's gate is derived from is
[`weather.md`](weather.md).

## The headline

The **spyglass and the edge marker are one object**, not two. `DAT_0071d284` is a single 600-byte
allocation holding a camera, a window, a circle, a triangle, a line, the label anchor and the
off-screen flag, and one function (`FUN_0049d940`) drives all of them from the same target every
frame. So the arrow, the "N o'clock" tag and the round live picture are three faces of one
mechanism, and the picture is not a layer bolted onto a marker that exists without it.

Four findings will not be guessed from the footage:

- **The picture is not a rendered texture pasted at the marker.** The `sgwin` window node's origin
  is *moved every frame* to the marker, camera 2 renders the world through it, and a circle
  primitive of radius 48 masks the result. The window is 96 x 96 device pixels in every chapter,
  authored in the gamez, and nothing scales it with screen resolution.
- **The camera frames the target to a constant apparent size.** Its field of view is
  `2 * atan(1.1 * targetRadius / distance)`, clamped to `[1.5 deg, 90 deg]`, so a fighter fills the
  same fraction of the disc at 200 m as at 400 m and only starts shrinking past the 1.5 degree
  floor.
- **The range gate is the world's own fog range, not a tuned constant.**
  `fogNear + (fogFar - fogNear) * 0.8`, capped at 2000, with a 0.875 hysteresis factor while the
  picture is not already open.
- **The arrow is a line plus a head, and its two ends clamp into two different rectangles.** The
  shaft runs from the marker anchor (clamped into a 5%-inset rectangle) out to the target's point
  clamped to the *full* viewport; only the 20-pixel head on the end is a constant. That full
  viewport is also what the off-screen flag itself is tested against, so a target inside the inset
  band is on screen and draws no marker at all.

## Function map

| Address | Role |
|---|---|
| `LAB_00489260` | **The toggle**, action id `0x30`, registered at `FUN_004895a0` |
| `FUN_00463f40` | The level load. Allocates the spyglass object, resolves `spyglass`/`sgwin`, computes the circle's centre and radius |
| `FUN_00464520` | The level teardown. Deletes the object and nulls `DAT_0071d284` |
| `FUN_00455800` | **The subsystem master switch.** The only writer of the `+0xc` enable |
| `FUN_0049f240` | The per-frame entry. Gated on `+0xc`, calls the update, then the render |
| `FUN_0049d940` | **The whole update**: gates, camera pose, field of view, window placement, arrow, label anchor |
| `FUN_0049d8a0` | The clock hour (1-12) the label's third line carries |
| `FUN_0049d680` | Clamp a projected point into a rectangle, flipping a point behind the camera through the rectangle's centre first |
| `FUN_0049f1e0` | Draws the three primitives in the HUD pass, each on its own visibility bit |
| `FUN_004579e0` | Reads the label anchor and draws the three text lines ([`targeting.md`](targeting.md)) |
| `FUN_00440810` | The detail setter. Writes the render scale at `+0x8` and the tier at `+0x24c` |
| `FUN_00472ea0` | The weather zone apply. Sets camera 2's near/far clip alongside camera 1's |
| `FUN_0042b570` | The shared "set this view's field of view", used by both the main view and camera 2 |
| `FUN_004da920` | `GetFogRange` on the world node: class data `+0x20` / `+0x24` |
| `FUN_004d7130` / `FUN_004d71b0` | Set / get a `Window` node's origin (class data `+0` / `+4`) |
| `FUN_004d70a0` | Get a `Window` node's resolution (class data `+8` / `+0xc`) |
| `FUN_004d2710` / `FUN_004d2490` | Set a `Camera` node's translation / rotation |
| `0x004a59b0` | The `Target` slot-9 predicate, `XOR AL,AL; RET` |
| `0x004a6890` / `0x004a6750` | `TargetVehicle`'s clone and release, vtable `0x0060886c` slots `+0x44` / `+0x48` |

Globals:

| Global | What it is |
|---|---|
| `DAT_0071d284` | **The spyglass object**, 600 bytes, one per level |
| `DAT_0071c298` | The local player's plane (the same global targeting and the aim assist use) |
| `DAT_0071c0b8` | The `world1` node. Its class data carries the fog block the gate reads |
| `DAT_0064ef78` | The main view's camera/window holder (`camera1` / `window1`) |
| `DAT_0071d244` | The `alignment_point` node the window is parked on while hidden |
| `DAT_0064f66c` | Set in `FUN_0043d640`; while non-zero the master switch can never come on |
| `DAT_006541ec` | A mirror of the master switch, read by the getter at `0x004557f0` |

## The object's field map

Offsets are from `DAT_0071d284`. Everything not listed is a primitive's internals.

| Offset | What |
|---|---|
| `+0x00` | the `spyglass` camera node, resolved by name (`FUN_004d0280(9, "spyglass")`) |
| `+0x04` | the `sgwin` window node (`FUN_004d0280(0xf, "sgwin")`) |
| `+0x08` | the render scale, `1.0` at construction, rewritten per detail tier by `FUN_00440810` |
| `+0x0c` | **the subsystem master switch** |
| `+0x0d` | the "primitives built" latch, set on the first update |
| `+0x0e` | **the Shift+S toggle** |
| `+0x10` / `+0x14` | the window's top-left x, y, as ints |
| `+0x18` / `+0x1c` | the window's right and bottom, `x + w - 1` / `y + h - 1` |
| `+0x20` / `+0x24` | the window's width and height as floats, `96.0` / `96.0` |
| `+0x28` | the circle primitive (vtable `0x0060a8b4`), the round mask |
| `+0x5c` / `+0x60` | **the circle's radius and radius squared**, 48 and 2304 |
| `+0x7c` | the arrowhead polygon (vtable `0x0060a838`), 21-vertex capacity |
| `+0x1ac` | that polygon's live vertex count, **3** |
| `+0x1b4` | the arrow shaft, a line primitive (vtable `0x0060a7bc`) |
| `+0x200` / `+0x204` | **the label anchor** `FUN_004579e0` reads |
| `+0x208` | **the off-screen flag** |
| `+0x20c` | the held render subject, a cloned `Target` |
| `+0x210` | the high-water `1.1 * radius` for the held subject |
| `+0x214` … `+0x21c` | the occluder undo list and its count |
| `+0x22c` | the render-target texture name, `"spyglassTexture"` |
| `+0x24c` / `+0x250` / `+0x254` | the detail tier, a timestamp, the detail-changed latch |

Colour: `FUN_004a5f40` (`Target::GetColor`, the marker colour rule in
[`targeting.md`](targeting.md)) is packed to 16 bits and written into all three primitives, at
`+0x64`, `+0x1b0` and `+0x1f0`. The disc's rim, the shaft and the head therefore always carry the
target's own marker colour.

## The command and its two switches

Action id `0x30`, `MSG_CAM2_TOG` "Toggle Spyglass", default `0x41f` Shift+S. The handler is nine
instructions:

```
if (DAT_0071c298->[0x91d] != 0) return;      // the player's plane is dead
spyglass->[0x0e] = (spyglass->[0x0e] == 0);  // flip
```

`+0x91d` is the vehicle's dead flag ([`aiPilot.md`](aiPilot.md)), so a press while dead is
swallowed. ⚠ **The toggle reads nothing else**: not the range, not whether a target exists, not
whether it is on screen. Those gates all live in the update and are re-evaluated every frame.

⚠ **The toggle byte is `1` at construction** (`0x004642e4`), so the spyglass is ON when a level
loads and Shift+S turns it OFF first.

The second switch is `+0xc`, written only by `FUN_00455800(enable)` as
`enable && (DAT_0064f66c == 0)`. It gates `FUN_0049f240`, which is the only caller of the update,
so with `+0xc` clear **nothing** in this page runs, including the edge arrow and the "N o'clock"
tag. Turning it on activates the camera node, shows all three primitives and sets the `+0xd` latch;
turning it off releases the held subject. Its ten callers are the flight-state transitions
(`FUN_004a0220` is the per-frame flight update, the others are mission start, death, bail-out and
the pause and multiplayer doors).

## The window: size, shape and where it sits

`sgwin` is a `Window` node in every chapter's gamez, authored `origin (0, 0)`, `resolution
96 x 96`, and nothing in the binary rewrites the resolution. `window1`, the main view, is authored
`640 x 480`, so the disc is 15% of the design screen's width.

At load (`0x0046434d`-`0x004643f1`) the code reads the window's origin and resolution back out of
the node and derives:

- `+0x20` / `+0x24` = `(float)w` / `(float)h` = `96.0` / `96.0`;
- `+0x18` / `+0x1c` = `x + w - 1` / `y + h - 1`, the inclusive right and bottom;
- the circle's centre = `(x + w * 0.5, y + h * 0.5)`, the constant `0.5` at `0x006032e0`;
- **the circle's radius** = `ftol((w + 1.0) * 0.5)` = `ftol(48.5)` = **48**, the `1.0` at
  `0x006032dc`, stored with its square at `+0x5c` / `+0x60`.

So the picture is a disc of radius 48 inscribed in a 96 x 96 window, and both are absolute device
pixels at every screen resolution and every detail tier.

Each frame the window is *moved*. `FUN_0049d940` computes the marker anchor, subtracts half the
window's size to get a top-left, and when either axis has changed by more than a pixel
(`0x0049e313`-`0x0049e390` in the shown branch) writes the new origin into the node with
`FUN_004d7130` and re-centres the circle through the circle's vtable slot `0xc`. The window's
right/bottom cache at `+0x18` / `+0x1c` moves with it.

**Where the anchor is.** The target's projected point is clamped twice, into two different
rectangles:

- into the **full viewport** (`FUN_00460a40`'s rectangle, its far corner less `1.001`). That
  clamped point is the arrow's tip.
- into an **inset rectangle**: the viewport inset by `(right - left) * 0.05` horizontally and
  `(bottom - top) * 0.05` vertically, and then by half the window's width and height. At the design
  640 x 480 that is 32 and 24 pixels of margin, plus 48 more on every side once the disc is shown.
  That clamped point is the **window's centre and the label anchor**.

Both clamps go through `FUN_0049d680`, which mirrors a point behind the camera through the
rectangle's centre before clamping, then walks the segment from the centre out to the boundary.

While the disc is hidden the window is parked, not stopped: `FUN_0053df30` gives the camera the
plane's own orientation and the window goes to the `alignment_point` node's position plus
`0.75 * width` in x (`0x0049e1a2` region and the `local_12 == 0` branch).

## Camera 2: where it stands and where it looks

- **Position**: `FUN_004d2710(cameraNode, planeX, planeY, planeZ)`. The camera stands **at the
  player's aircraft**, at the same point `plane->GetPosition()` returns. Nothing offsets it, so
  anything between the player and the target is in the picture. That is the whole of the
  "an occluded target shows the terrain in front of it" reading; there is no see-through logic.
- **Orientation**: built from the vector `planePos - targetPos` combined with the plane's own frame
  at `+0x198` and `+0x150` through `FUN_0053fd40` / `FUN_0053f920` / `FUN_0053f850` /
  `FUN_0053fa40`, converted to Euler angles by `FUN_0053df30` and applied with `FUN_004d2490`.
- ⚠ **The roll angle is thrown away for anything but an aircraft.** The third Euler is zeroed
  unless the held subject casts to `TargetVehicle` *and* that vehicle's entity `+0x67c` is `0` or
  `4`. Every other target class is viewed level.
- **Near and far clip**: the weather zone apply writes camera 2's pair beside camera 1's
  (`0x00472f85`-`0x00472f9f`, both through `FUN_004d2930`), so the picture wears the same clip
  ranges and the same fog as the main view. A camera is born at `1.0` / `5000.0`.
- **Field of view**, computed before any gate and applied through the shared `FUN_0042b570`:

| Case | Value | Address |
|---|---|---|
| No target at all | `0.05236` rad (3 deg) | `0x3d567750` |
| Normal | `2 * atan(1.1 * R / d)` | the `fpatan` at the `local_18` assignment |
| Clamped above | `1.5707964` (90 deg) | `0x3fc90fdb` |
| Clamped below | `0.02617994` (1.5 deg) | `0x3cd67750` |

`R` is the target's scene-node radius at node `+0x6c` and `d` is the slant range. `1.1 * R` is kept
as a **high-water mark** at `+0x210` for as long as the same subject is held, so the frame never
tightens on a target whose radius drops, only on a new one.

`FUN_0042b570` also sets the camera's LOD multiplier from the render scale at `+0x8`, the window's
width and `tan(30 deg)` over the viewport width and `tan(fov/2)`, and restores the main camera's
state afterwards because the two share one routine.

⚠ **A fifth branch exists and is dead.** When the target's vtable slot 9 answers non-zero the code
takes a different path: the camera is offset 15 units along the plane's `+0x198` axis and the field
of view is fixed at `1.047198` (60 degrees, `0x3f860a92`). Every `Target` vtable in the image puts
the constant-zero stub `0x004a59b0` at that slot (its four data references are `0x00603618`,
`0x00608890`, `0x006088f0`, `0x00608944`), so no selectable target reaches it. Do not port it.

## The range gate

Inside the update, before anything is drawn:

```
FUN_004da920(DAT_0071c0b8, &fogNear, &fogFar);      // world class data +0x20 / +0x24
gate = fogNear + (fogFar - fogNear) * 0.8;
if (gate >= 2000.0) gate = 2000.0;                  // 0x44fa0000
if (nothing is currently held) gate = gate * 0.875;
if (distance <= gate) { if (nothing held) hold = target->clone(); }
else { hide the disc; release whatever is held; }
```

The world node's class data is the gamez `World` fog block one field at a time: `+0x10` fog state,
`+0x14`/`+0x18`/`+0x1c` fog colour, `+0x20`/`+0x24` fog range, `+0x28`/`+0x2c` fog altitude, `+0x30`
fog density (the accessors `FUN_004da8d0`, `FUN_004da8f0`, `FUN_004da920`, `FUN_004da940`,
`FUN_004da8b0` read exactly those). The range is therefore the **live, per-zone** fog range the
weather apply wrote, not the gamez file's authored zeros.

⚠ **The gate is asymmetric.** It engages at `0.875 * gate` and only releases past the full `gate`,
so the picture does not flicker at the boundary. Nothing else in the path is hysteretic.

⚠ **The held subject is a clone, not the selection.** `+0x20c` is a 20-byte copy of the `Target`
wrapper made through vtable slot `+0x44` (`0x004a6890` for a vehicle) and released through slot
`+0x48`. It is dropped when the wrapped entity pointer at `+4` stops matching the current
selection's, so switching target closes and reopens the picture and resets the radius high-water
mark.

**There is no target-class test anywhere in the gate.** Aircraft, mission structures, turrets and
tracked ordnance all reach it identically; the only class-sensitive line in the whole function is
the roll suppression above.

## The disc only appears when the target is off screen

The target's world position is transformed by the view matrix at `DAT_009fd680`…`DAT_009fd6ac`,
projected by `FUN_00460980`, and tested:

```
if (depth < 0 || sx < left || right < sx || sy < top || bottom < sy)
    spyglass->[0x208] = 1;          // off screen, the label goes to the edge anchor
else
    shown = 0;                      // on screen, the disc is suppressed
```

So the picture is **exactly the off-screen case of the marker**, and Shift+S with the target in
view does nothing visible. The bracket box hides in the same case for its own reason
(`FUN_004574d0`'s near-plane test), which is why the two never overlap.

## The arrow

Two primitives, both driven from the unit vector `u` from the anchor toward the tip:

- **The shaft**, the line at `+0x1b4`, from the tip `(tx, ty)` to the anchor `(ax, ay)`, set at
  `0x0049e2d6`-`0x0049e302`. When the disc is shown the anchor end is first pushed out to the disc's
  rim: `ax += ux * width * 0.5`, `ay += uy * height * 0.5`, i.e. 48 pixels, at
  `0x0049e2ae`-`0x0049e2cf`.
- **The head**, the 3-vertex polygon at `+0x7c`, set at `0x0049e31b`-`0x0049e3a1`:

```
v0 = (tx,                        ty)
v1 = (tx - ux*20 + uy*5,         ty - uy*20 - ux*5)
v2 = (tx - ux*20 - uy*5,         ty - uy*20 + ux*5)
```

The two constants are `20.0` at `0x006035e4` and `5.0` at `0x006036bc`. So the head is **20 pixels
long along the bearing and 10 pixels across its base**, and the shaft carries the rest of the
apparent length, which grows with how far outside the inset rectangle the target sits.

## The label anchor

Written at `0x0049e1b6`-`0x0049e222`, read by `FUN_004579e0`:

- `+0x200` = the anchor x, always, with no offset along the arrow's direction.
- `+0x204`, when the disc is **hidden**: `anchorY + 3.0` when the anchor is in the upper half of the
  viewport, `anchorY - 45.0` when it is in the lower half. The halves are split at
  `(top + bottom) * 0.5`; the constants are `0x006035d0` (3.0) and `0x006082d4` (45.0).
- `+0x204`, when the disc is **shown**: `windowY + 96.0 + 3.0` when `windowY + 96.0 + 48.0` still
  fits above the viewport's bottom, otherwise `windowY - 45`. The 48.0 is at `0x006082d8` and the
  45 is the literal `0x2d`.

45 is three 15-pixel label lines and 48 is that block plus the 3-pixel gap, so the rule is the same
"below unless the block would not fit, then above" the bracket box uses, measured against the disc
instead of the box. `FUN_004579e0` then clamps each line into the viewport with a 3-pixel margin
and centres the block on `+0x200`.

## The occluder pass is a no-op in the retail build

While the disc is shown, `FUN_004c8f70` runs a segment query from the plane to the target
(`0x0049e451` onward), and for every returned scene node of class 5 (`Object3d`) the code reads
that node's class-data field `+4` and flag bit 1, records both in the undo list at `+0x218`, and
then writes **the same two values back** (`0x0049e557` and `0x0049e57b` push the saved values into
`FUN_004d1640` and `FUN_004d17c0`). The next frame's undo pass (`FUN_0049f290`) restores the same
values again. The list is built, walked and torn down every frame and changes nothing.

⚠ Whatever this was meant to do to occluders, the shipped build does not do it. **Do not port it,
and do not read the footage as evidence that it works.**

## What this means for CSVM

| | Original | CSVM today |
|---|---|---|
| The picture | camera 2 rendered through a moved 96 x 96 `sgwin` window, masked by a radius-48 circle | `Flight/Camera/SpyglassView.cs`, a 96 x 96 `SubViewport` on the shared world, masked to a radius-48 circle by `TargetHud.DrawDisc`; the reference pixels scale by `HudMetrics`, and the mask is a generated polygon because no `sgwin` art ships |
| The toggle | action `0x30` on Shift+S, on at level load, blocked only while the player is dead | `InputAction.ToggleSpyglass`, on at level load, dispatched inside `StepTargeting`'s `InPlay` gate; Shift+S now that a binding carries its modifier, and the pad's Misc1 (`docs/controls.md`) |
| Gating | off-screen target, inside the fog-derived range, any target class | the same three, `TargetHud.UpdateSpyglass` |
| Range | `fogNear + (fogFar - fogNear) * 0.8`, capped 2000, engaging at 0.875 of that | the same, `Spyglass.RangeGate` over `WeatherRig.FogGlobals.Range`; a band whose far is no further than its near takes the 2000 m cap alone, which is the empty stage and the suite rigs |
| Field of view | `2 * atan(1.1 * R / d)` clamped to 1.5-90 degrees, radius kept as a high-water mark | the same clamp, `Spyglass.FovDeg`; the radius is the merged mesh box's half diagonal, measured once per hold, and a subject with no mesh takes the 3-degree no-target value |
| Camera pose | at the player's aircraft, aimed at the target, roll kept only for an aircraft | the same, `Spyglass.Pose` off `TargetHud.RenderPose`, the interpolated pose the aeroplane is drawn at, since the remake draws between sim steps and the original does not; the aim point is the target's own drawn pose of the same frame, read after the flight rigs have written it |
| The pilot's own aeroplane | nothing in the update touches per-object visibility, and the camera stands at the plane's own position, so on the decode alone the airframe is in the picture | left out: the airframe wears `UI.Boards.SplitScreen.OwnAirframeLayer` and `SpyglassView.DiscMask` drops that one layer. ⚠ Not decoded. The original's disc shows no part of the player's plane at the controls, and this follows that reading |
| Edge inset | 5% of the viewport per axis, plus half the disc when shown | the same, `EdgeMarker.InsetFraction` plus the `anchorInset` the disc passes; it places the anchor and nothing else |
| Off screen | the whole viewport, `+0x208`, nothing narrower | the same, `EdgeMarker.Resolve`'s `OnScreen` against the bare pane, which is what gates the arrow, the tag and the disc alike. `VersusHud`'s opponent markers read this one rule too; the original has no splitscreen, so the remake gives its second marker surface the decoded clamp rather than a second one |
| Arrow | a shaft from the anchor (or the disc's rim) out to the point clamped to the full viewport, plus a 20 x 10 pixel head at the tip | the same, `TargetHud.ShaftTail` and `ArrowHead` over `EdgeMarker`'s anchor and tip |
| Label placement | anchor x unchanged, y offset `+3` in the screen's upper half and `-45` in the lower, `windowBottom + 3` / `windowTop - 45` when the disc is up | the same both ways, `TargetHud.EdgeLabelAnchor`'s `disc` variant |
| Colour | `Target::GetColor` into all three primitives | the same rule, `TargetHud`'s `color`, the disc's rim included |
| Occluders | recorded and restored unchanged, a no-op | nothing |

The remake's `Ref*` constants are 1440p-reference pixels scaled by `HudMetrics`, and the original's
constants are absolute device pixels, so they transfer one for one into `Ref*` the way
`RefLabelGap`, `RefLabelPitch`, `RefLabelBlock` and `RefLabelAbove` already did.

## Falsifiable predictions

Each is checkable against the original at the controls or against a capture, and each would fail
loudly if the decode is wrong.

1. **The disc is 96 pixels across at every screen resolution**, so it covers a smaller fraction of a
   1024 x 768 screen than of a 640 x 480 one. Measure the disc in two captures at different
   resolutions: the pixel diameter is the same number.
2. **The disc's rim never comes within 32 pixels of the left or right edge, nor 24 of the top or
   bottom, at 640 x 480**, because the inset is 5% per axis plus the disc's own half-width.
3. **Shift+S with the target on screen changes nothing**, and the disc appears on the frame the
   target's projected point leaves the viewport.
4. **A target flies out of the disc's reach at the fog range times 0.8 and only comes back at 0.7 of
   it.** Approach a target from beyond the gate and note where the picture opens; withdraw and note
   where it closes. The two ranges differ by a factor of 0.875.
5. **The target's apparent size inside the disc is constant with range** until roughly 84 target
   radii, where the 1.5-degree floor takes over and it begins to shrink.
6. **A non-aircraft target is viewed level.** Bank hard with a structure or a boat selected and the
   horizon inside the disc stays flat; do the same with an aircraft selected and it rolls.
7. **The label jumps 48 pixels when the marker crosses the screen's horizontal centre line**, from
   3 pixels below the marker to 45 above.
8. **The arrow's visible length does not change with how far off screen the target is.** Both of
   its ends are clamped, the tail into the inset rectangle and the tip into the viewport, so the
   shaft is a ninth of the distance from the centre out to the tail (the inset rectangle is 90% of
   each axis) and depends on the bearing alone: longest toward a corner, shortest straight out of
   the nearer edge. A target one degree outside the viewport and one dead astern draw the same
   arrow.
9. **Everything in this page stops together.** There is no state in which the arrow and the tag are
   drawn but the whole subsystem is not running, because `+0xc` gates both.

## Not resolved

- Which `Target` subclass the dead slot-9 branch was written for. The four vtables found all carry
  the constant-zero stub; the sweep was the stub's data references, not an enumeration of every
  subclass.
- What `"spyglassTexture"` at `+0x22c` with the `1` at `+0x240` selects. The window is moved into
  the frame buffer directly, so the named texture may be a debug or software-path alternative.
- Whether `FUN_00460a40`'s last two ints are the viewport's far corner or its size. They coincide at
  the origin the game always uses, so no shipped configuration separates them.
- What `DAT_0064f66c` is. It is set from a comparison in `FUN_0043d640` and, while non-zero, holds
  the master switch off permanently.
- The exact matrix chain `FUN_0053fd40` / `FUN_0053f920` / `FUN_0053f850` / `FUN_0053fa40` builds
  for the camera's aim. The inputs and the output are established; the intermediate convention is
  not.
