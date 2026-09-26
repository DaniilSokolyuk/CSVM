# HUD: compass tape and cockpit gauges

Part of the [format documentation](README.md). This page records the HUD textures, compass-tape
rendering, cockpit and weapon gauges, bitmap font, and aiming reticle. Evidence comes from the
HUD captures and aircraft gauge subtrees; the implementation lives in `CompassTape.cs` and
`GaugeCluster.cs`.

## Contents

- [Textures](#textures)
- [Compass rendering](#compass-rendering)
- [Cockpit gauges](#cockpit-gauges)
- [Weapon gauges](#weapon-gauges)
- [Bitmap font](#bitmap-font)
- [Aiming reticle](#aiming-reticle)
- [Known uncertainty](#known-uncertainty)
## Textures

The compass ships as two small textures in **every chapter's `texture.zbd`** (not in
`rimage.zbd`, which holds only menu/briefing UI):

- **`compassticks2`** (64×16, opaque, black background), one repeating tick segment
  covering **15° of heading**:
  - The **tall tick** straddles the tile seam: solid-255 core at columns 62–63,
    soft falloff continuing at columns 0–3 of the next tile. Vertical span rows 6–15.
  - Four **minor ticks** (one per 3°), 255 cores at columns 11–12 / 25–26 / 38–39 /
    50–51, visible only in rows ~11–15.
  - Tiling segments edge-to-edge automatically produces a tall tick at every 15°
    multiple. The pattern is mirror-symmetric, so flip orientation is irrelevant.
- **`compasstxt`** (128×32, soft alpha), cream (~197,194,150) letter atlas laid out
  as the four pre-kerned intercardinal pairs, **"NE SE SW NW"**, at x 1–25 / 27–51 /
  52–83 / 84–115 (letters within: N 84–95, E 40–51, S 52–63, W 64–83; singles are cut
  from the pairs). A 5 px white→black vertical gradient block sits at x 123–127,
  purpose unknown; it does not appear in the in-flight compass.

## Compass rendering

- The tape is a **cylindrical drum viewed edge-on** showing exactly 180° of heading:
  a mark Δ° from the current heading renders at `x = center − R·sin(Δ)`. At 1440p the
  fit over every visible tall tick gives **R ≈ 127.6 px**; bar rect ≈ 263×40 px with
  its top at y=35, horizontally centered (scales with screen height).
- **Headings increase to the left** (W renders left of SW, S right, a real
  whiskey-compass card, mirrored vs a modern heading tape).
- Brightness falls off with Δ for ticks and labels alike, and the falloff is **flat across
  the inner half of the bar and near black in the outer quarter**. The comb's mean
  luminance over its own rows, folded about the bar centre and normalised, reads
  1.00 / 0.93 / 0.83 / 0.69 / 0.49 / 0.28 / 0.15 at 0 / 60 / 80 / 88 / 100 / 112 / 120 px
  from the centre, within a few percent in all three reference screenshots. A plain cos(Δ)
  is too gentle out there (0.88 / 0.78 / 0.72 / 0.62 / 0.48 / 0.34 at the same points);
  **min(1, 1.35·cos(Δ)^2.1)** fits the measured profile with a quarter of cos's error
  (rms 0.026 against 0.102). No gain/MODULATE2X, the cores are already full-white in the
  texture. Single labels peak 207 → 152 → 105 at Δ 4.5°/40.5°/49.5°, which is the same
  curve rather than cos.
- The tick tile is drawn **at bar height less a 3 px hem**, so the bar's own black shows
  under the comb: the comb's last lit row is the 37th of the bar's 40 in every reference
  screenshot, and a tall tick's lit span is then 22 rows.
- Tick filtering is effectively **point-sampled** (the comb's hard 1–2 px edges and
  per-tick brightness lottery are minification aliasing); the labels are drawn
  smooth (bilinear) at their drum x, scaled 0.625× of the atlas (20 px tall at 1440p) with
  the box top ~3 px below the bar top, and **squeezed with the drum** to the ticks' own
  horizontal cos(Δ). The stills alone do not settle it (at 3× on the Kestrel shot the edge
  `E`, Δ about 44°, is about 0.8 of the centre `E` while the edge `S` is full width); the
  squeezed letters are the reading chosen against the upright ones at the controls.
- **Neither bar end is capped.** All three screenshots run dark to the rim; the ~192 at the
  very edge a bright rim tick was read from is `HUD.png`'s own sky value, outside the bar.
- Labels every 45° (octants), no numeric readout, no lubber line, the current
  heading is read from the centered, brightest label.

## Cockpit gauges

**The gauge dials are 3D models inside each player plane's tree in planes.zbd**, a
`gauges` subtree under the (otherwise skipped) cockpit, one per plane, with mostly the
same child names everywhere: `altimeter`, `speedometer`, `damageindicator`, plus `comp`,
`horizn`, `gungauge`, `missilegauge`, `nitrogauge`. ⚠ `comp` and `horizn` are the DATA's own
node names, and neither is what `crimson.exe` looks up by: the binary's strings are `compass` and
`pfhorizon` (see below), and `horizn` itself is only the dial-face name on 5 of the 11 player
airframes, `horiz` on the other 6. All dial meshes are flat polygons
in dial-local coordinates (x right, y up, **bezel radius = 1**, z ≈ 0); the interp
`support\cockpit.gw` boot script wires their dynamic behavior via `FindSubNode` +
`CycleTextureSet` texture swaps. Texture pixels live in **every chapter's
`texture.zbd`** (not rimage.zbd).

- **Face**: each dial's face is a 12-gon (radius 1) mapping the full face texture,
  `altimeter.tif` / `speedometer.tif` / the per-plane `<short>_damage.tif`
  (`bldhwk_damage`, `ke_damage`, `pm_damage`, `agyro_damage`, `avenger_damage`,
  `bal_damage`, `fury_damage`, + AI planes). The face texture includes the bezel ring
  and the *unlit* (dark) LOW ALT / STALL windows; 12-gon corners cut the texture's
  square corners. Draw priority 1.
- **Needles are single textured quads, the taper and the hub are painted in
  `needle.tif`, not meshed.** Quad x −0.055…0.052,
  y −0.245…0.510 (pivot at the origin, tip +y = texture top). The altimeter has
  two: `hundreds` (long, priority 9, z 0.05) and
  `thousands` (short/wider: x ±0.07, y −0.181…0.368, priority 8, z 0.025, same
  texture); the speedometer one (`speed`, priority 8). The nodes' modeled rest
  rotations are arbitrary; the engine sets absolute angles. ⚠ **The pointer shape
  lives only in the `rtexture*` tiers' copy of `needle.tif`** :
  the base `texture.zbd` copy is a 32×128 RGB flat full-width slab with no alpha,
  but every `rtextureN` tier ships a same-size **RGBA** copy with different art
  (beveled lance, rimmed hub discs) whose alpha channel is the complete antialiased
  here claimed the shape was applied engine-side; it is simply in the archives the
  engine actually renders from (see `docs/tooling.md` on the tiers).
- **The needle laws are decoded.** Both needle sets are rotated about the node's third axis by
  `FUN_004d1a30(node, 0, 0, angle_rad)`, with a negative (clockwise) angle. The altimeter's rates
  are per METRE of world height: `hundreds` −0.020614125 rad/m (`00607704`) and `thousands`
  −0.0020614124 rad/m (`00607700`), i.e. **0.36°/ft over 1,000 ft per revolution** and
  **0.036°/ft over 10,000 ft**. The speedometer's is −0.02811017 rad per m/s (`006076e8`), which
  with the 2.2369363 mph per m/s factor at `006076e4` is **0.7199957°/mph**, exactly 500 mph per
  revolution. Both are driven from `FUN_0049f6a0`, the altimeter at `00453c5f`/`00453c84`
  (`FUN_00453c50`) and the speedometer at `00453a07` (`FUN_004539f0`); the speed input is the
  plane's speed magnitude with no branch on it.
  ⚠ **The needles read absolute world Y (ASL) while the LOW ALT lamp beside them reads AGL.** The
  needle feed is `plane+0x208`, pushed at `0049f7a2`. The `ALTIMETER` debug text readout prints the
  AGL figure in feet (×3.28084 at `006076f0`), so that string is not evidence about the needles.
- **Warning overlays** `lowalt_on` / `stallwarning_on` (priority 7, *under* the
  needles): the lit window quad (`lowalt.tif` / `stall.tif`, 64×32, red) **plus two
  red bezel slashes** (`redhilite.tif` quads at the dial edge, left+right of the
  window's side). The whole node toggles/blinks.
- **Both instrument sets run off one state in the remake.** `GaugeCluster` owns the readings and
  the lamps' blink phase for the screen-space dials, and `CockpitGauges` mirrors the same values
  onto the authored 3D panel inside `cockpit1` (five needle nodes, both lamps) on the frames the
  interior is on the screen. ⚠ The cluster processes ahead of the `FlightController` for that
  reason; the two copies of a lamp must toggle on the same frame.
- **Both lamps are decoded, and both are player-only.** Each is a plain visibility toggle
  (`FUN_004cca30` on bit `0x4` of the node's flag word at `+0x24`) driven once per frame from the
  cockpit update `FUN_0049f6a0`, against an absolute deadline stored beside the lamp. Neither has a
  second timer, a frame counter or an every-Nth-tick guard, so the observed half-period is the
  computed one rounded up to the next frame boundary. Both deadlines run on the clock at
  `DAT_0071c470`, which the world tick `FUN_004897c0` advances by `DAT_009ad744` at `004897d1`.
  ⚠ **That is the same dt the flight model uses**: the next instruction (`004897d8`) copies
  `DAT_009ad744` bit-for-bit into `DAT_0071c56c`, which is what the aero block multiplies by at
  `0x48d11b`. The lamps and the flight model are therefore in ONE time base, and no conversion
  separates them. Read `GaugeCluster`'s lamp constants in the same units as `FlightModel`'s dt.
- **STALL is gated on available load factor, not on a speed fraction.** The driver is
  `s = (plane+0xf4 + 1.35) × 0.425` (`1.35` at `00608338`, `0.425` at `00608334`, applied at
  `0049f7e6`), where `plane+0xf4` holds `1 − n_avail`: `n_avail` is the instantaneously available
  load factor, the wing's maximum lift at the current airspeed over weight, capped at 9 g and by
  the AoA limit. It is written every frame, player-only, at `0048e7dd` in `FUN_0048e580` from the
  out-parameter `FUN_0048c470` fills, which is the same quantity and the same block as the
  nose-drop's stall flag (`docs/org/flightModel.md`). So:

      lamp dark    when n_avail >= 2.35            (s <= 0 hides the node outright)
      half_period  = 0.4 − 0.3·s = 0.100375 + 0.1275·n_avail   seconds

  `0.4` at `00603538` and `0.3` at `006034ac`; the deadline is `now + half_period`, written at
  `00453a78` in `FUN_004539f0` and recomputed from the current frame's value at each toggle, so it
  does not catch up. **The half-period is bounded to (0.100, 0.400] s by construction**, since
  `s` lies in (0, 1]. A separate flag at `plane+0x384` forces the lamp off entirely; it is the `-fd`
  developer switch (`docs/org/flightModel.md`, "`+0x384` is a developer switch"), never set by
  gameplay, and it also switches the throttle clamp from `[0, 1]` to `[−5, +5]`.
  Because lift goes as v², an equivalent speed form is that the lamp lights below `1.533 × v₁g`,
  but that holds only while the AoA cap is not binding, which is exactly the condition a
  speed-fraction port drops.
  ⚠ **The 0.30 fd threshold and the 2.10 s-per-fd-fraction blink are superseded.** They came from
  four clips (`BL-148`, `CAP-06`, the two `CAP-05` stall clips) and are the wrong quantity: the
  engine gates on load factor. The measurement's shape survives (dark in cruise, blinking faster
  with stall depth, no hysteresis, and it lengthens again as the aircraft accelerates back), and so
  does its finding that **brightness is binary** at a 0.50 duty cycle, the lit plate reading
  211.0 ± 0.2 red against 41.7 ± 0.2 unlit, with no opacity ramp. What does not survive is the
  magnitude: the clips' 643 ms and 296 ms half-periods were quoted in sim seconds at k = 1.390,
  and the binary cannot produce 643 ms at any input. Taken as wall seconds the same two figures are
  462 ms and 213 ms, against a decoded range of 100 to 400 ms, which is the reading the shared time
  base above supports.
- **LOW ALT lights below 60.0 m above ground, and its blink ramps with height.** The gate is the
  constant at `006076fc`, compared at `00453cb0` in `FUN_00453c50`. The AGL feed is built by the
  caller at `0049f763`–`0049f78f` as the smallest non-negative `plane_Y − terrain_sample_height`
  over the terrain query `FUN_004c76e0`; where that query answers nothing the value stays `FLT_MAX`
  and the lamp cannot light. The blink is **not** a fixed period:

      half_period = 0.14 + 0.006 · agl_metres      (0.14 at 006076f4, 0.006 at 006076f8)

  280 ms full period at ground contact, widening to 1.0 s just under the gate, recomputed at each
  toggle from the height at that instant so the ramp tracks the aircraft continuously. ⚠ Above the
  gate the lamp is extinguished only once the pending half-period expires (`00453d00`–`00453d19`),
  so climbing through 60 m leaves it lit for up to one more half-period rather than snapping off.
  This supersedes the plain fixed 400 ms blink and the 50 m threshold, neither of which was ever
  measured against the original.
- **Damage display**: the dial's face is a single untextured 12-gon (the dark backing
  disc). ⚠ **Where it is parented differs per aircraft**, verified across the whole
  roster: on `player_bhawk` it is the `damageindicator` node's *own* mesh,
  but on **every other player plane** that node is mesh-less (`mesh_index` −1) and the
  identical 12-gon hangs off an extra generically-named child instead (`g951` on the
  Fury, `g927` Kestrel, `g1156` Balmoral, `g992` Warhawk, `g843` Devastator, …). A
  reader that only looks at the dial node's own mesh therefore draws a backing disc for
  the Bloodhawk and bare floating zone shapes for all ten other aircraft. The safe rule
  is the one the other two dials already need: **anything under the dial that is not a
  recognised functional child is face geometry.** Its
  four children `nosedamage` / `taildamage` / `leftwingdamage` / `rightwingdamage`
  each carry exactly two polygons (priority 7): a **border bar** at the bezel edge
  (`greenhilite.tif`; nose = top bar, tail = bottom, wings = left/right slanted bars)
  and a **part-shaped hatch fill** tracing that part on this plane's silhouette
  (`grn_hatchptrn.tif`, 8×8, tiled UVs up to ~5×). `cockpit.gw` gives each zone a
  4-map texture cycle, green/yellow/**orange**/red `*hilite` + `*_hatchptrn`, the
  color change IS a texture swap. **So part positions are per-plane mesh data,
  nothing is computed from the silhouette texture.**
- **Thresholds**: every player part's vehicle.json `injure_anims` carry
  `*_damage_green` at 0.72, `*_damage_yellow` at 0.46, `*_damage_red` at 0.20 (the
  anims themselves live in the undecoded cam_anim.zbd). The display uses **all four
  cycle colors** (orange confirmed in the original), each anim
  threshold steps to the *next* color: green > 0.72, yellow ≤ 0.72, orange ≤ 0.46,
  red ≤ 0.20 (red on a still-flying plane matches the damage reference shot; the
  anim names lag their effect by one state). **The scale is the zone's COMBINED
  armor+health pool** (`BL-085`), not health alone: at stock (armor == hp) 0.72
  falls while 56 % of the armor is gone, 0.46 just past armor zero (8 % of the
  airframe), 0.20 at 60 % of the airframe, each inside the band the game manual's
  Crispen Mark V description gives it (yellow ≤ 50 % armor gone; orange = armor
  half-to-fully gone with ≤ 25 % airframe gone; red = 25–100 % airframe gone). The
  manual's figures are each band's envelope, not its boundary; the shipped fracs are
  the boundaries, and they sit inside. On health alone the manual's yellow band
  would be unreachable, nothing would react while armor is being stripped. Blink: the original blinks a zone
  (fill + border) for ~5 s after it takes a hit, even inside green (user-observed).
- **Scales** (measured off the face textures): altimeter 0–9 clockwise from top, 36°
  per digit, long needle 360°/1,000 ft, short 360°/10,000 ft; speedometer labels
  0/100/200/300 at ≈0°/69°/143°/216° clockwise → **≈0.72°/mph** linear.
- **Screen layout** (HUD.png, 2556×1440, bezel dark-span scans): all three dials
  share **radius ≈ 85 px**; altimeter center (425.5, 1108.5), damage dial
  (426.5, 1299), speedometer mirrored ≈ 420 px from the right edge, same height as
  the altimeter. (The two reference screenshots place the cluster slightly
  differently, HUD.png is the canonical one, matching the compass metrics.)
  ⚠ Those left and right edges are the reference frame's, so the port measures
  them from `HudMetrics.ReadingBox` (that frame's 16:9 centred in the pane), not
  from the pane, which on an ultrawide screen is far wider than the frame.
  ⚠ A pane NARROWER than that frame has no room for these margins: they are a
  16:9 frame's, and at 4:3 they take a far larger share of a narrower width, which
  reads as the columns pulled in off the border. Each column keeps only
  `HudMetrics.MinimumColumnMargin` there (`GaugeCluster.ColumnAnchors`). That
  minimum is a placement choice, not a measurement: the reference captures run
  through dgVoodoo at 16:9, so there is no 4:3 still to measure.
- **The artificial horizon is decoded and driven as a node rotation, on the ball mesh named
  `pfhorizon`, never `horizn`.** Neither `horizn`, the plain `horiz` some airframes use instead,
  nor `comp` ever appears in `crimson.exe`; the binary's own names are `pfhorizon` (the ball) and
  `compass` (the drum). `pfhorizon` sits under a mesh-less container (named `horiz` on 10 of the 11
  player airframes, `g1167` on the eleventh) that is itself the child of a dial-face node named
  `horizn` on 5 airframes and `horiz` on the other 6; `CockpitGauges` finds it by searching for the
  name `pfhorizon` anywhere under `gauges`, never by its parents' names, since those vary. Each
  frame (`FUN_0049f6a0`, player aircraft only) the original decomposes the aircraft's own
  orientation basis into pitch and roll with `FUN_0053df30` (`0049f8e0`-`0049f98f`): pitch =
  `asin(-m[7])`, roll = `atan2(m[1], m[4])`, with a gimbal branch (`|m[7]| >= 1`) of pitch =
  `-copysign(pi/2, m[7])`, roll = 0. Heading is discarded for this dial. The node's rotation is set
  to `N = Rz(-roll) . Rx(pitch)` (quaternions built at `0049f92e`/`0049f93d`, multiplied at
  `0049f951`) and written straight into the node's Euler fields under its own `R = Ry . Rx . Rz`
  convention, no gain, offset, clamp or smoothing anywhere in the law. The camera's own pitch
  offset and any look-around never enter: the source is the aircraft's attitude alone. The
  `gungauge` / `missilegauge` are decoded below; `nitrogauge` (face, `nitro_backplate`, needles
  `nitro_boost` / `nitro_charge`) is driven by `GaugeCluster` off the nitro decode in
  `docs/org/flightModel.md`, "Nitro". The `compass` drum (`FUN_004d1a30(node, 0, -heading, 0)` at
  `0049f8fe`) turns about the node's own Y axis by that argument. ⚠ **The argument is `-heading` in
  the engine's Euler frame and `+heading` in this port's**, and the two must not be conflated: the
  card is fixed to true north, so the local write exists to cancel the parent's yaw, and this port
  derives heading as `Atan2(nose.X, -nose.Z)`, under which the aircraft node's own yaw is already
  `-heading`. Writing the engine's literal a second time turns the drum the wrong way at the right
  rate, which reads correct at rest (both signs give north) and inverted in a turn, the shape of
  fault that survives a sign "settled by reasoning" and dies at the controls. `CockpitGauges` finds
  the drum by the binary's own name, `compass`, first (present as the mesh-bearing node on every
  player airframe checked in `extracted/planes/nodes.json`) and falls back to `comp` (the data's own
  container name on the same airframes, present but never looked up by the binary) if that search
  ever misses. Heading is `GaugeCluster.HeadingDeg`, the same value `CompassTape` reads, so the 3D
  drum and the screen-space tape turn off one number, matching the tape's own "headings increase to
  the left" card behaviour.
- **The compass window's rim fade is geometry, not a shader term.** On all 11 player airframes the
  drum (`compass`) is an octagonal prism, radius about 1.457, wrapped in `compassticks2` at three
  15° tiles per 45° face, with the eight octant letters as `compasstxt` quads just proud of it.
  A node `g730` beside it (a child of the outer `comp`) carries a grey `bezel` back plate behind the
  drum and two priority-8 quads in front of it, from x ±0.625 to ±1.385, sampling `compasstxt`
  at u 0.980–0.996. Those atlas columns (123–127) are black with alpha rising linearly from 0 at
  row 3 to 255 at row 29, and the quads map that ramp along x, so each end of the drum darkens
  from clear at the inner edge to opaque at the outer. The ramp works only blended: a 1-bit cut
  turns each quad into an opaque dark bar from its 50% point outward, hiding the outer quarter of
  the comb, so the interior builder blends `compasstxt` (the original never alpha-tests,
  [../org/textures.md](../org/textures.md)). Blended, the lit comb spans about 0.87 of the
  window's inner width at 720p, against 0.84 measured on the original's cockpit footage and 0.74
  with the cut.
- ⚠ **The artificial horizon's face, `horizonindicator`, must blend and never scissor.** That
  128×128 texture is the glass in front of the ball, not the ball: its RGB carries the bezel ring,
  the fixed wings bar, the pitch-ladder rungs and the top tick, and its alpha is the mask that lets
  the ball through, opaque over the ring and the painted marks, zero over the dome. Inside the
  dome's upper rim it paints a golden haze on an alpha ramp that crosses 0.5, and it measures 0.51
  opaque over its ink, just over the port's soft-alpha cutoff, so the pixel rule alone classifies it
  as a 1-bit cutout. Scissored, that ramp becomes a hard opaque band of the haze across the top of
  the dome, which is a golden line the original does not draw; blended, the dome reads as the clean
  disc with its thin top tick that
  `OriginalScreenshots/ArtificalHorizonDevastatorCockpit.png` shows. The original alpha-tests
  nothing ([../org/textures.md](../org/textures.md)), so `PlaneBuilder` blends this name in the
  interior the same way it blends `compasstxt`.
- ⚠ **`nitrogauge` is the one dial not authored in normalized dial coords.** Its
  `nitro_backplate` mesh spans x ±1.4489 and y −0.1292…3.6746, so the bezel is centred at
  y ≈ 2.2078 with radius ≈ 1.4489 rather than at the origin with radius 1; the plate carries
  a stem below the dial, which is the rest of that y span. Both needle nodes carry the bezel
  centre as their own translation (`0.0017953524, 2.2078001`) and their meshes sit about that
  pivot, so a needle takes the scale alone while the face takes the recentre as well. Read the
  centre off the needle node and the radius off the face rather than assuming the shared rule.
- The gauge's screen placement: the bottom of the right column, one dial-pitch below the
  speedometer, mirroring the damage dial's row on the left (user-confirmed against the
  original). It is not above the GUNS dial, the column fills downward.
- The needles' decoded angles are Euler-z, counter-clockwise-positive, so a screen-space draw
  whose rotation is clockwise-positive has to negate them.
- The `hud_v2.zrd` `SW_GAUGES` block's `POSITION_1ST` / `POSITION_3RD` keys are **not** dial
  placement, despite the name. `FUN_00454e70` reads them into a text widget built per section
  (`AIR_SPEED`, `ALTIMETER`, `GUNS`, `MISSILES`, `HEALTH`, `NITRO`), and the six values share
  one x at 0.02 spacing in y, a debug text column, written only under `DAT_00624df0`.

## Weapon gauges

Read from the planes.zbd `gungauge` / `missilegauge` subtrees +
`support\cockpit.gw` (the interp boot script that wires their texture cycles);
remake implementation extends `src/Flight/Hud/GaugeCluster.cs`. Screen placement is
ours (measured off `OriginalScreenshots/HUD.png`, the Warhawk): the **ROCKETS**
dial sits one dial-pitch (190.5 px, the alt→damage spacing) above the altimeter,
the **GUNS** dial the same above the speedometer; both share the other dials'
radius (≈85 px at 1440p).

Both are circular dials laid out **identically on all 11 flyable models** (unlike
the damage dial, there is no per-plane parenting quirk, verified across the whole
roster): the `gungauge`/`missilegauge` node is always mesh-less (`model_index` −1)
and the labelled face (`gungauge.tif` / `missilegauge.tif`, a 12-gon, priority 1,
carrying the baked **GUNS** / **ROCKETS** legend) hangs off a generically-named
child (`g815` / `g819`). The safe reader rule is the damage dial's:
**anything under the dial that is not a recognised functional child is face.**

The functional children, and how `cockpit.gw` drives each:

- **`4char_ammo`**, a row of **4 digit quads** (priority 7, lower centre). Each
  carries `CycleTextureSet` 11 mapping `zero.tif`…`nine.tif` then `SPACE.tif`
  (frames 0–10); the engine sets each cell's frame to spell the count. The remake
  shows it **right-aligned, space-padded**. **Guns: the *selected* gun group's own
  rounds** (per group, the Balmoral's two .50s count independently). **Rockets:
  the *per-pylon* rounds of the pylon the arrow points at (the next to fire), NOT
  the sum across pylons** (a full HE pylon reads `3`; the original's Warhawk shows
  `BOOM 3`, not `24`).
- **`6char_type`**, a row of **6 glyph quads** (priority 7, upper centre).
  `CycleTextureSet` 37 maps `A.tif`…`Z.tif`, then `zero.tif`…`nine.tif`, then
  `SPACE.tif`. Shows the selected weapon's short **`NAME`** from `weapons.json`,
  upper-cased and left-aligned (`30slug`→`30SLUG`, `BOOM`, `SONIC`).
- **`ggindicator0..3`** (gun, 4) / **`mgindicator0..7`** (missile, 8), the **belt
  lights**, one per gun slot / pylon, arranged around the ring: index 0 at the top
  (90°) and running **counter-clockwise** (gun 90° apart, missile 45°). **`mgindicator`
  index _i_ is pylon _i+1_** (`BL-294`), the hardpoint gauge's `WeaponGauge.Slots` is
  always the ring's full 8 entries indexed by `Hardpoint.Index − 1`, never the compacted
  position within a plane's bound-hardpoints list (`loadouts.md`'s fill order): a
  partial stock fit leaves the unfitted ring positions red, spread around the ring
  rather than trailing at its end. Each is a
  `Xhilite.tif` bezel bar + a `Xindicator.tif` light, both carrying a **3-frame**
  cycle green→yellow→red (`CycleTextureSet` 3). **Every belt light is always lit**,
  none stays dark, and **steps the colour by that slot's remaining fraction**: green
  healthy, yellow low, red empty; a per-pylon HE rocket (3 rounds) steps
  green(3/2)→yellow(1)→red(0). A position the airframe **does not fit at all** (a
  2-gun plane's slots 2–3; turret gun groups are inert, so they don't count) reads
  **red**, indistinguishable from a fitted-but-spent slot, that is what the original
  shows. The
  green/yellow/red **thresholds are a TUNE** pending an original playtest. Guns are the only class
  with a yellow tier at all (`GunIndicatorColor`, `BL-024`), hardpoints/pylons step straight
  green→red. The gun yellow threshold (`GaugeCluster.IndicatorLowFrac`) is tuned
  (`BL-142`) from 0.34, a value inherited from the unrelated 3-round rocket-pylon coincidence
  (1/3), never watched against a real gun belt, to **0.15**, judged from a screenshot sweep of a
  scaled belt drain (`--ammo=200 --gun-select=0 --fire`): at 0.34 yellow lit with ~119 sim s of
  sustained fire still left at the real 2800-round `CLUSTER_SIZE`/8 rounds-per-s, reading as
  premature; 0.15 (~53 sim s left) reads as genuinely low. Still no capture to trace either number
  to, an eyes-on playtest against the original remains owed.
- **`ggarrow`/`mgarrow`**, a `smallneedle.tif` pointer (priority 49, rest points
  up at slot 0) rotated about the dial centre to the selected slot: the gun arrow to
  the **selected gun group**, the missile arrow to the **next pylon that will fire**.
  Unlike the dial needles, the arrow's shape IS its mesh: a single 5-vertex polygon
  (pointed tip at +y, two shoulders, a base) whose wrapping UVs (u 0.98–2.02,
  v 0.50–3.10) smear the tiny 16×16 texture across it. **The pointer sweeps, it does
  not snap** (`BL-184`, `CAP-18`, : a single constant rate shared
  by both gauges, **168.7 ± 1.6 °/sim-s**, routed the shortest way round
  (`GaugeCluster.TweenArrow`); the numeric readout above still snaps on the sweep's
  first frame. CAP-18's own end-to-end capture also carries a ~97 ms sim ease at each
  end (not a smoothstep) that the remake does not reproduce, its shape is unmeasured
  beyond "not a smoothstep", so a pure constant-rate sweep runs a 90° step in ~533 ms
  against the capture's ~633 ms; owed a follow-up if the still-outstanding capture A/B
  reads as visibly off at the sweep's ends.

⚠ **The belt-light art is alpha-only, its alpha never reaches zero, and the panel must blend it
in GAMMA space.** `greenhilite.tif` and its two siblings are 32×16 with one flat saturated colour
across the whole RGB plane; the bar's whole shape is the alpha channel, a crescent peaking at
162/255 over a surround that never falls below about 20/255. `greenindicator.tif` and its siblings
are 32×32 four-point star glows built the same way. Two rules follow. A 1-bit cut on an alpha with
no zero paints the entire quad, so these must stay blended. And the blend itself has to reproduce
the original's, which mixed sRGB values straight into the framebuffer: that surround contributes
8 % of its colour there, against about 31 % when the same texel is mixed in the linear space Godot
composites in, and 31 % of a saturated green over a dark dial is the square block the bar is not.
`SceneBuilder.GammaBlendAlpha`, which the interior builder sets, linearises ALPHA so the composite
lands where the original's did; the screen-space dials need nothing, since 2D canvas draws already
blend in gamma space. Measured on the guns dial, texel alpha 20/255 reads 29 in the screen-space
dial and 61 in the uncorrected panel, and the arc's 162/255 peak reads 158 against 201.

⚠ The digit/letter/indicator textures (`zero.tif`…, `A.tif`…, `greenindicator.tif`,
`greenhilite.tif`, `smallneedle.tif`, `gungauge.tif`, `missilegauge.tif`) live in
**every chapter's `texture.zbd`**, like the other gauge art, not in `rimage.zbd`.

### The text readout (`MSG_HUD_GUNGAUGE` / `MSG_HUD_MISSLES`)

The message table (`extracted/messages.json`) carries a parallel **text** form of the gauges,
alongside `MSG_HUD_AIRSPEED` / `MSG_HUD_ALTIMETER` / `MSG_HUD_HEALTH` (the "hudSWGauges" software
gauges):

- **`MSG_HUD_GUNGAUGE`** (id 188) = `"GUNS: %1: %2!d!"`
- **`MSG_HUD_MISSLES`** (id 189) = `"MISSILES: %1: %2!d!"` (the table's own misspelling)

`%1` names the **gun group / rocket type**, which is *why* these strings exist: the counters are
per gun group and per pylon, so the readout has to say *which* one. `%2!d!` is the integer count.
The remake does not draw this text form; the two dial gauges carry the same counts. `%1` would
take the gun group's **mount name** (`Inner Wing Guns`, from `IDS_AIRFRAMEGUNGROUPNAMES`) or the
rocket's resolved **display name** (`High-explosive rocket`, from its `MSG_WEAP_*` `DESC`), and
`%2` the selected group's per-group rounds / the next-to-fire pylon's per-pylon rounds. The
placeholder grammar (`%N`, a trailing `!spec!` consumed, `%%` → literal `%`) is handled by
`Messages.Fill`.

## Bitmap font

Decoded by pixel-probing the atlas; remake reader `src/Flight/Hud/HudFont.cs`.

Two textures in **`extracted/rimage/`** (the menu/UI image set, *not* the chapter texture
archives that carry the compass/gauge art):

- **`5pointhud.png`**, the normal font.
- **`5pointhudbrite.png`**, the brighter highlight variant, **pixel-for-pixel the same
  geometry**, differing only in green level.

Both are **463×6**, a proportional **1-bit** font. Glyphs occupy **rows 0–4** (five pixels tall,
hence "5point"); row 5 is blank spacing. Colours are exactly two green levels plus a dim outline
on black: normal core **(0,150,0)**, highlight core **(0,255,0)**, both edged with **(0,32,0)**;
the background is pure black.

**Character range: printable ASCII `0x20`–`0x7e`.** Space (`0x20`) is a blank leading cell, so the
atlas holds **94 ink glyphs, one per code `0x21`–`0x7e` laid left-to-right in code order**, i.e.
`glyph(code)` is the `(code − 0x21)`-th maximal run of inked columns. (The PLAN's shorthand
"`0123456789:;<=>?@A…z`" understates it: the set begins at `!` and runs through `~`, digits and
punctuation included.) **Letters are uppercase-only**, the `a`–`z` cells carry the `A`–`Z`
shapes. No glyph has a fully-blank interior column, so the run-per-code segmentation is exact
(94 runs = 94 codes, verified); glyph widths vary **1–6 px**, inter-glyph gaps **1–3 px**.

The reader segments the source rects at load (one per inked-column run, assigned from `0x21` up),
keys the black background to transparent (every non-black texel kept as-is, so a white modulate
reproduces the original green), and draws each glyph with `DrawTextureRectRegion` under a
**Nearest** filter (a pixel font). It inserts a **1 px tracking** gap after each glyph and treats
space / unrepresented codes as a **3 px** advance. Sizing routes through `HudMetrics` like every
other HUD element, so a splitscreen pane damps the text the same way the dials do.

## Aiming reticle

The aiming pipper is a single image in **`extracted/rimage/`** (the UI set, alongside the
`5pointhud` font, *not* the chapter archives): **`impact_point.png`**, a **32×32 RGBA**
sprite. It is a filled warm-white disc, core `(255,247,222)`, ring `(247,227,181)`, with a
**cross-shaped transparent notch** cut through the centre (the PLAN's "four tick marks around an
open centre"). The alpha channel is authored (transparent background, anti-aliased edges), so it
draws directly with no colour-keying, unlike the black-backed font atlas.

**Behaviour (remake E37, `src/Flight/Hud/ImpactReticle.cs`).** The reticle is **not pinned to screen
centre.** It marks the **projected ballistic impact point of the selected gun group's rounds at a
fixed convergence distance**, computed with the *same* `VELOCITY`/`ACCELERATION`/`GRAVITY`
integration `ProjectilePool` fires each round with, the pipper and the rounds agree exactly, since
a round leaves the muzzle with no scatter at all (`CANNON_SPREAD` is the unbuilt gun aim assist's
acceptance cone, not a dispersion term; see [`org/aim-assist.md`](../org/aim-assist.md)), from the
averaged muzzle pose. Because the rounds inherit the
plane's velocity, which lags the nose during a hard roll or pull, the reticle **trails the nose**
in a hard manoeuvre and sits on the rounds in steady flight (measured: `nose→reticle = 0.00°`
level, up to `~0.77°` below the nose toward the velocity vector at ~15° angle-of-attack; the small
angle is physics, bullets travel ~900 m/s against a ~55 m/s plane). It is a fixed-screen-size HUD
element (drawn via `Camera3D.UnprojectPosition` at `_Draw` time so it never lags the chase camera),
scaled through `HudMetrics` like every other widget, one per player pane.

⚠ **The convergence distance is a TUNE, not in the data.** `weapons.json` carries no
harmonisation/convergence field (player guns are `RANGE 1000`, `VELOCITY 750–1000`, no
`ACCELERATION`/`GRAVITY`). The remake uses **250 m** (`GunConvergenceDist` in `FlightController`),
pending an original-game playtest. Note the on-screen *trailing angle* is set by the
velocity/bullet-speed ratio and is essentially independent of this distance; the distance mainly
sets where a toed-in mount would harmonise and the pipper's parallax off screen-centre.

## Known uncertainty

Which world axis is compass **north**: the remake assumes **−Z** (consistent with the
map layout and motion), but the original's convention has not been verified in-game.
If it differs, the fix is the one heading line in `FlightController`.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
