# The weather, sky, fog and lighting runtime, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-08-07…08-10. Every claim below names the function
it came from, and every claim that came from a *measurement* instead, a matched-pose A/B against
original footage or a screenshot, says so on the line.

Everything here is a description of *behaviour and constants*. No decompiler output is reproduced;
the addresses are given so any claim can be re-checked at source.

**Where the other halves live.** The authored side, `weather.json`'s `CLOUD_COVER` / `WIND` /
per-zone `ZONE<n>` blocks, the precipitation block, the per-chapter zone-naming split, is
[`formats/weather.md`](../formats/weather.md); the fog volumes' own file is
[`formats/fogvol.md`](../formats/fogvol.md) and the `zone_id` field the gate reads is
[`formats/gamez.md`](../formats/gamez.md). Our implementation is `CSVM/src/Flight/Airframe/Weather.cs`
(the reader and the state machine), `CSVM/src/Session/World/WeatherRig.cs` (the per-frame rig: fog
apply, whiteout, deck, dome, band flicker), `CSVM/src/Mech3/ZoneGate.cs` (the visibility gate) and
`CSVM/src/Mech3/FogVolumes.cs` (the volumes and their whiteout). This page is the original's
runtime: what the engine does with those keys.

⚠ **This page is a decode, not a proposal.** Where it disagrees with a footage measurement, the
decode wins and the disagreement is a note. Where CSVM deliberately differs, that is listed at the
bottom rather than hidden.

## Function map

| Address | Role |
|---|---|
| `FUN_0042ee40` | The per-frame camera weather state machine (1/2/3), **and** the in-cloud whiteout opacity remap that flickers with it |
| `FUN_004e8540` | The `FOG_STATE` animation event's handler (dispatch slot 28): the same four fog setters the zone apply calls, one per flag bit |
| `FUN_004735b0` | World init, precomputes the whiteout core's bottom edge into `0071c2d0`, the altitude the state-2 test reads every frame |
| `FUN_00472ea0` | The zone apply: indexes `ZONE1`–`ZONE3` straight off the camera state and writes the fog parameters, then the sunlight |
| `FUN_004dc610` | Writes the `sunlight` gamez node's rotation triple (`zclass\Light.c`), the second half of the zone apply |
| `FUN_004dbdb0` / `FUN_004dbce0` | Write the same `sunlight` node's diffuse and ambient, called beside the rotation setter in that same zone apply |
| `FUN_004bc3e0` | `SUNLIGHT_ORIENTATION` reader: degrees × `0.017453292`, and the engine's own default bearing |
| `FUN_0053c610` | The euler `(pitch, yaw)` → direction helper, the binary's own statement of what a bearing *means* |
| `FUN_0056c430` | The `zone_id` visibility gate, called **per node** during the render walk |
| `FUN_004d62d0` | Arms the camera each frame with the zone set `{0, camera weather state}` |
| `FUN_004c7630` | The geometric point-in-zone fallback, runs only when no explicit state was armed |
| `FUN_0044e6f0` | Fog-volume signed distance, the approach/interior ramps, and the volume union |
| `FUN_0044e010` | `fogvol.zrd` loader: the `fog_zone` bool and the fade-distance defaults |
| `FUN_004bc680` | `WIND`'s `RANDOM_ANG_VEL` reader, converts degrees on the way into the global |
| `FUN_005506d0` | The gust turn-rate setter it calls |
| `FUN_0054ee10` | The global wind tick, at the head of the frame, see [`puffer.md`](puffer.md) |

Two globals carry the in-cloud flicker: `_DAT_0064efcc` (the blend parameter) and `_DAT_0062154c`
(its drift speed). Both are **single globals**, i.e. the engine assumes one camera.

## The per-frame camera weather state (`FUN_0042ee40`)

Once per frame, per camera, the engine resolves a **weather state** of 1, 2 or 3. Everything else
on this page keys off it.

1. **State 1**, the default. Below the cloud deck.
2. **State 2**, the mission authors a `CLOUD_COVER` band *and* the camera's altitude is at or
   above the **whiteout core's bottom edge**. That edge is `bandCentre − THICKNESS/2`, precomputed
   at world init by `FUN_004735b0` into `0071c2d0` and only compared against each frame.
3. **State 3**, the chapter's `fogvol.zrd` sets `fog_zone` *and* the camera is inside one of the
   `fvol*` volumes. The containment test is against the volume's **authored shape** (a half-space
   test over its own face planes), not its AABB.

**Precedence: 3 beats 2.** The binary computes and assigns state 3 *after* state 2, so an
in-volume camera is state 3 whatever its altitude. This never arbitrates in shipped data, C5 is
the only chapter arming `fog_zone` and its band sits at 9950–10150 m, roughly 9.8 km above its
highest street strip, but the order is the binary's and is reproduced.

⚠ **The state-2 threshold is a THIRD altitude, distinct from two others that sit near it.** The
core bottom (`FUN_0042ee40`'s test), the cloud band's own floor (`CLOUD_COVER`'s `BOTTOM`) and the
band *centre* (the deck's lit-variant flip) are three separate numbers; in C1 they fall within
80 m of each other, which makes them look like one quantity. The binary computes state 2 and the
deck flip as two independent thresholds. Do not collapse them.

⚠ **The state-2 test lives inside the "does this mission author a `CLOUD_COVER`?" check.** A
mission with no band never reaches state 2 at any altitude, including far below sea level.

## The `zone_id` visibility gate (`FUN_0056c430`)

`FUN_004d62d0` arms the camera each frame with the zone set `{0, camera weather state}`. The
render walk then draws a node **iff** its gamez `zone_id` is `−1` (always drawn) or is in that set.
So `−1` and `0` are ungated, and `1`/`2`/`3` are three buckets of which exactly one is live at a
time. Every chapter in this install authors ids in `−1/1/2/3` only (surveyed across all eight
gamez node tables).

⚠ **The gate is PER NODE and is not inherited down a subtree.** `FUN_0056c430` is called during
the walk with *that node's* id. The data's own counter-example is C1's `flaglite1`/`flaglite2`,
which are `zone_id −1` children of a zone-1 parent, a subtree-inherited gate hides them above the
deck and passes every other check.

Nothing else is gated by state. The geometric point-in-zone fallback `FUN_004c7630` runs only when
no explicit state was armed, which never happens on the normal path.

This is the mechanism behind several things that look like separate rules: the placed
`cloudparent` clusters, the `fvol` clutter field, the mission targets and the ground world are all
just zone-stamped nodes, and so are the cloud deck tiles (`zone_id 2`) and the horizon domes.

## The zone apply, and why it is an EDGE (`FUN_00472ea0`)

On a state *change*, the engine calls one zone-apply that indexes `ZONE1`–`ZONE3` straight off the
new state and writes, in a single call:

- the distance fog, colour, near/far ranges, the `FOG_ALTITUDE` band;
- the `SUNLIGHT`-derived world brightness;
- the sun's bearing, by calling `FUN_004dc610` on the `sunlight` node;
- that same node's diffuse and ambient, by calling `FUN_004dbdb0` and `FUN_004dbce0` beside it, so
  a zone's authored `SUNLIGHT_DIFFUSE`/`SUNLIGHT_AMBIENT` reach the light itself and not only the
  world scalar above. They swing across the install (`DIFFUSE` 0.4 to 2.0, `AMBIENT` 0.15 to 0.6),
  which is why C1B's night mission lights a plane visibly darker than C1C's daylight.

⚠ **It fires on change, not per frame.** Fog and sun travel together in one call by the binary's
own shape, a zone change that moved the fog and left the light behind would be a bug the single
call makes unrepresentable.

So, for a chapter with a reachable band, below the deck the world wears `ZONE1`'s ranges,
altitude, colour and `SUNLIGHT`, and above it `ZONE2`'s; inside an armed volume, `ZONE3`'s.

Two further notes on the fog itself, both settled at the controls of the original rather than in
the decompiler:

- The fog volume is a **vertical cylinder around the camera**, i.e. the distance is horizontal
  view distance, not a euclidean sphere radius (user-diagnosed).
- The altitude fade is by **fragment** altitude, full fog below `FOG_ALTITUDE[0]`, none above
  `FOG_ALTITUDE[1]`, so the deck and sky overhead stay clear. Settled in C2, the only chapter whose
  flown band (256–1024 m) sits inside the flight envelope.
- The near→far ramp is **linear**, per the gamez world node's own `fog_state == 1`.

⚠ **`ZONE3`'s `CLIP_RANGES` far of 300 is NOT applied.** The original hard-clips; the remake fogs
instead and keeps a much larger far plane, and that is a deliberate, standing divergence (B11's
kept divergence, re-asserted by C22). A future session that "completes" the decode by plumbing the
clip range through is changing behaviour, not fixing an omission.

### That far plane is the only range limit on an ordinary scene node

The zone apply writes the pair onto the camera's class data at `+0xb0`/`+0xb4` through
`CameraSetNearFarClip` (`FUN_004d2930`); a camera is born at 1.0/5000.0 (`FUN_004d2180`, the store
at `0x004d21f3`). Nothing narrower exists. The world walk `FUN_004d5910` draws whatever the chunk
gather handed it, and that gather (`FUN_004d4db0`, the engine's own "diamond tiler") admits a chunk
on three tests only: the camera frustum built from the camera's own corner points
(`FUN_0055ffa0`), the occluder planes (`FUN_00560060`), and a bucket cap of 50 Manhattan rings by
30 chunks. Ordinary nodes then draw through `FUN_004d4a20` with no distance test of their own, so
there is no per-node, per-material or per-texture cull range anywhere in that path. What ends the
world is the far plane, and what hides it before then is this fog.

For C5 that means 2500 m below the deck and 300 m inside an armed `fvol`, at `HIGH`'s `CLIP_SCALE`
of 1.0, with `ZONE1`'s fog reaching pure black (`FOG_COLOR 0 0 0`) at 2250 m, 250 m short of the
clip. All eight C5 missions ship the same numbers. The mip level a facade reads at those distances
is [textures.md](textures.md)'s.

⚠ **The fog parameters are GLOBAL, one set for the world.** The original has one such set and one
`sunlight` node, not one per view.

## The `FOG_STATE` animation event

The animation interpreter's dispatch slot 28 (`FUN_004e8540`, the table `FUN_004ee1a0` fills at
`0x727de0`) is the second writer of the same fog record. It reads a flag byte at event `+0x2c` and
calls, per set bit, one of the four setters the zone apply above calls: bit 1 the fog type
(`FUN_004da800`, word 4 of the record), bit 2 the colour (`FUN_004da820`, words 5 to 7), bit 4 the
altitude pair (`FUN_004da850`, words 10 and 11), bit 8 the range pair (`FUN_004da870`, words 8
and 9). Every setter raises a dirty bit in word 0, so a field the event omits keeps its last value.
The handler returns 2 (complete) immediately and takes no reset-versus-sequence branch, so it fires
from a `RESET_STATE` walk as it does from a sequence.

Consequences: the two writers are in last-writer order, the zone apply on a camera-state edge and
the event whenever its walker reaches it. The one shipped use (`drop_fog` in C1/M04's intro
definition, [`formats/anim-definitions.md`](../formats/anim-definitions.md#fogstate--an-inline-fog-written-over-the-zone))
sits in a reset block, so it lands over the zone at load and holds until the camera next crosses a
state boundary. The zone apply multiplies its range pair by the detail level's `FOG_SCALE`; the
event writes its range raw. Which of the altitude pair is the low edge is not distinguished by the
shipped use (10000 and 11000 m, above any C1 flight); CSVM reads `min` as `FogLow`.

## The in-cloud band flicker (`FUN_0042ee40`, `_DAT_0064efcc`/`_DAT_0062154c`)

The same function that resolves the state also remaps the cloud band's raw whiteout opacity, and
that remap is what makes the interior of a cloud shimmer rather than sit flat.

**Two curves**, both computed as `log2` in the binary, the base cancels in the ratio, so natural
log reproduces the first exactly:

| Curve | Shape |
|---|---|
| log curve | `ln(op·5 + 1) / ln(6)` |
| atan curve | `(atan((op − 0.5)·10) + 0.5) / (atan(5) + 0.5)` |

They are blended by a parameter `t` and the result is clamped to `[0,1]`: at `t = 0` the atan
curve, at `t = 1` the log curve.

`t` (`_DAT_0064efcc`) **drifts** in `[0,1]` at a speed (`_DAT_0062154c`) that is re-randomised at
each bound, and the sign is flipped at the upper bound only: the low-bound reset keeps a freshly
drawn **positive** speed, the high-bound clamp **negates** it. That is what turns the drift into a
ping-pong rather than a one-shot ramp.

⚠ **The whole remap block, including the drift update, is skipped when the opacity is exactly 0
or exactly 1.** A band pinned at a hard edge never drifts, and the remap therefore never moves the
band's edges, only its interior. (Both curves also agree at `op = 1`, and the clamp forces `op = 0`
to 0 whatever `t` is.)

⚠ **Neither curve is the identity at an interior opacity**, `atanCurve(0.5) = 0.267`, not 0.5. A
naive implementation that starts at `t = 0` therefore does *not* reproduce the unremapped opacity
on its first frame.

⚠ The remap's **rate** is read from a per-mission weather-struct field (≈ `+0x934`) that **no
reader decodes** and no capture pins a value for. It is the one genuinely unknown constant in this
subsystem (`BL-329`).

## The fog volumes: the in-volume whiteout (`FUN_0044e6f0`, `FUN_0044e010`)

Where `fogvol.zrd` sets `fog_zone` (**C5 alone** in this install), the engine computes **one
camera-space density per frame** from the `fvol*` volumes and blends the whole frame with it. It
never builds per-volume fog meshes.

The density comes from **two linear ramps over one signed distance** (outside-positive,
inside-negative, the largest signed distance to any face plane, which for a convex body is exact
inside and a lower bound outside):

- **Approach**, outside, density rises linearly from 0 at `fog_fade_dist` metres out to **1 at the
  wall**.
- **Interior**, inside, density **decays** from 1 at the wall to 0 at `interior_fog_fade_dist`
  metres of penetration.

⚠ **The interior ramp looks inverted and is correct.** It must not be "fixed" by inverting it: the
volume is a transition **curtain**, and what carries the look once the camera is properly inside is
`ZONE3`'s own fog (state 3). A whiteout that got *denser* with penetration would double up with it.

Volumes combine as **`a + b − a·b`**, the binary's own union, so a camera at a corner where two
strips meet is whited out by both rather than by the nearer one.

`FUN_0044e010` stores `fog_zone` as `value != 0`, it is a bool arming the whiteout **and** camera
state 3, and it is *not* a sky/fog zone selector. Its fade-distance **defaults** for a chapter that
arms `fog_zone` without authoring distances are `400` m approach and `20` m interior. No shipped
chapter is in that state (C5 authors 16/16), but the defaults are decoded, so they are stated
rather than invented at a call site.

All 65 shipped `fvol*` volumes are convex (verified across every chapter), which is what makes the
half-space containment test exact, and note that C1C's build-ups are rotated *tapering frusta* and
C5's street strips are polygonal prisms, so an AABB stands in for the shape only in C1/C2B/C4.

## The fog colour, and the two fallbacks

Two different absences resolve two different ways, and confusing them renders a fullbright,
fogless world:

1. **A zone the mission does not author.** `FUN_00472ea0` indexes the zone table by state. Since
   the zone names are per chapter, C1–C4 ship `ZONE1`+`ZONE2`, all eight C5 missions ship
   `ZONE1`+**`ZONE3`**, a state-2 lookup in a C5 mission finds nothing. An unguarded lookup lands
   on "no fog, world light 1 = fullbright", which is exactly the failure mode; the remake falls back
   to the weather file's **first** zone.
2. **A `fogvol.zrd` with no `fog_color`.** The engine's default for the in-volume whiteout is the
   mission's `CLOUD_COVER` `TOP_COLOR` (`FUN_0044e010`), chapter-scope geometry reaching for
   mission-scope data.

C5's authored `fog_color` is `[16,16,16]`, i.e. this "whiteout" is very nearly a **blackout**, and
it matches C5's `ZONE3` `FOG_COLOR` of the same 16 that takes over inside. All of these are DX7-era
sRGB framebuffer values.

Measured, not decoded: the original's fully-fogged C1 pixels are exactly `0.69 × 255 = 176` gray
(flat regions, std 0, `OriginalScreenshots/C1 IA1 Cloudcoverage 1.png`), and C4's in-cloud veil is a
flat 192 against C4's authored `TOP_COLOR`/`BOTTOM_COLOR` of `192,192,192` (CAP-12).

⚠ **The band colour's top→bottom LERP is inferred and this install cannot falsify it.** Of the four
chapters whose band you can reach (C1 970–1124, C1C 1055–1110, C2B 924–1124, C4 1000–1100) only C4
authors colours and its pair is *equal*, so every blend rule renders the same picture. The one
chapter that would discriminate (C5, top 220, bottom 64) puts its band at 9950–10150 m, unreachable,
so those values may never have been checked by their own authors either.

⚠ **`TOP_COLOR`/`BOTTOM_COLOR` are NOT deck-mesh face tints**, whatever the names suggest: three of
the four colour-carrying chapters (C1B, C3, C5) have no cloud-deck mesh at all. Nor are they a
skydome grade, the dome's colour is authored per vertex and anchored on the zone's `FOG_COLOR`.

## The sun: euler → direction, and the engine's default

`FUN_004bc3e0` reads `SUNLIGHT_ORIENTATION` and multiplies by `0.017453292`, the data is in
degrees. The pair means a direction by `FUN_0053c610`:

```
dir.x = −cos(pitch)·sin(yaw)
dir.y =  sin(pitch)
dir.z = −cos(pitch)·cos(yaw)
```

⚠ **The engine's own default, when the key is absent or short, is pitch −π/2: straight down.** Not
a plausible-looking oblique bearing.

The authored bearings vary by chapter (C1 `[-25, 90]`, C3 `[-25, 135]`, C5 `[-25, -135]`) and are
adopted with no tuning, they are authored data, not a look (`BL-324`). `ROLL` is optional in shape
but present install-wide and always 0; a directional light is rotationally symmetric about its own
beam, so roll cannot change the shading.

The gamez→Godot mapping is the **identity**, which is worth stating because it looks like it should
not be: the binary writes these three radians into an ordinary gamez node rotation triple
(`FUN_004dc610`, `zclass\Light.c`), gamez node eulers are read as `Basis.FromEuler(v, Yxz)`
verbatim, Godot's `Node3D` default order is YXZ, and a directional light shines along local `−Z`,
which reproduces `FUN_0053c610` exactly, pinned at `(0,0)`, `(−90,0)` and `(0,90)` in
`CSVM.Tests/SunOrientationTests.cs`.

⚠ **`SUNLIGHT_ORIENTATION` is the SHADING direction, not the sun object's position.** The gamez
`sun` billboard the lens flare anchors to is a different node entirely and disagrees with it in C3
by 90°. That disagreement is the original's, and reproducing it is correct (`WORLD-26`).

⚠ **Ground shadows do not use this direction either.** The top-level `SHADOW_ANGLES` outranks it in
the shadow renderer, and every shipped file authors straight down. The same node's diffuse and
ambient triples set how dark a shadow is. Both are [`shadows.md`](shadows.md).

### The world-brightness scalar

The original lights the baked-vertex world by the mission's `SUNLIGHT`
(`SUNLIGHT_AMBIENT + SUNLIGHT_DIFFUSE · (N·L)`). Measured, not decoded: averaged over the
predominantly up-facing world that directional term collapses to one per-mission brightness scalar,
and the average incidence that reproduces the original is 0.46, calibrated to C1/IA1
(`A=0.25, D=1.2 → 0.80`, matching the original's deck 210→169 and terrain →~57) and then confirmed
at 0.426 / 0.784 / clamp-1.0 across three missions (CAP-11 matched-pose A/B, 2026-08-07).

⚠ **The scalar is a collapse, and the original's own machinery is decoded separately.** The
`sunlight` node is one entry in the same light array as every `LIGHT_STATE` point light, and its
contribution is computed per vertex inside the polygon loop rather than baked. Which surfaces
receive it is decided on the hardware draw by one gate, the model's `lighting` flag; the texture's
alpha bit exempts a polygon in the software draw only, which no retail capture shows. Both draws
are in [`vertexLighting.md`](vertexLighting.md). **Water is lit in the original** and so is every
textured surface whose model carries the flag, alpha class or not. Night cloud sprites being
moonlit directionally (`BL-325`) is a separate, still-open reading.

### `FOG_COLOR` luminance as the night key (enhanced mode only)

The authored `SUNLIGHT` pair does not say whether a zone is day or night: C5 is a night city whose
zones author the install's modal **day** pair (1.5 / 0.5), its darkness coming from `FOG_COLOR` and
the art. `FOG_COLOR` does say it, and the two populations do not overlap. Over all 212 `ZONE*` and
`SW_ZONE*` blocks, Rec.709 luminance of the authored triple:

| Chapter | zone | fog luminance | authored `SUNLIGHT` diffuse / ambient |
|---|---|---|---|
| C1 | ZONE1, ZONE2 | 0.6900 | 1.2 / 0.25, and 1.5 / 0.2 in two missions |
| C1B | ZONE1, ZONE2 | 0.0942 | 0.6 / 0.15, and 0.65 / 0.35 in M03's ZONE1 |
| C1C | ZONE1, ZONE2 | 0.6900 | 0.4 / 0.6 and 2.0 / 0.6 |
| C2 | ZONE1 | 0.8461 | 1.1 / 0.5 |
| C2 | ZONE2 | 0.6900 | 0.4 / 0.6 through 2.0 / 0.6 |
| C2B | ZONE1, ZONE2 | 0.6900 | 0.4 / 0.6 and 2.0 / 0.2 |
| C3 | ZONE1 | 0.7900 | 1.5 / 0.3 |
| C3 | ZONE2 | 0.0942 | 1.5 / 0.3 |
| C4 | ZONE1, ZONE2 | 0.7529 | 1.5 / 0.5 |
| C5 | ZONE1 | 0.0000 | 1.5 / 0.5 |
| C5 | ZONE3 | 0.0627 | 1.5 / 0.5 |

Night runs 0.0000 to 0.0942 and day 0.6900 to 0.8461, with nothing in the gap, so any separator
inside it partitions the install the same way. C3's `ZONE2` is the in-cloud zone rather than a
night one, and its band sits at 9,000 to 10,000 m, above the flight ceiling.

⚠ **This is a proxy the original does not use.** The engine lights from `SUNLIGHT` and darkens from
`FOG_COLOR` independently, and nothing in the binary reads one off the other. Enhanced mode's
`WeatherRig.IsNightZone` reads it anyway, because a real Godot sun driven from C5's day-level pair
lights a night city at noon level and the data carries no other handle. **The skydome is not that
handle**: a daylit mission draws a moon and a star field too (C1 and C4 both wear `horizon/zone2`),
which is why the dome's night art cannot separate the two populations.

## The lens flare (measured, not decoded)

⚠ Everything in this section is **footage**, not the executable, no `FUN_` address backs it. It is
here because it is part of the same sky.

Two independent gates, read from two different files, that must agree: the flare needs (1) a gamez
node named `sun` in the chapter's `horizon` subtree and (2) `LensFlareTexture` slot registrations in
`support\<chapter>\init.gw`. Across the whole install both are true of **C2 and C3 and no other
chapter**, and the sun texture ships in exactly those two chapters as well. Nothing is keyed on a
chapter name; if the two gates ever disagree that is a real signal about the data.

⚠ **C2's flare is predicted, not verified.** The data says C2 should have one; only C3 was captured
(CAP-13, `CAP-13 C3.mp4`, analysed 2026-08-07).

Measured from CAP-13 at 1280×720: four elements strung along the sun→screen-centre vector at
fractions 0, 0.50, 0.90 and 2.0 (so the last is as far *past* the centre as the sun is short of
it), diameters ~63 / ~102 / ~45 / ~164 px. The full-screen white wash peaks at α 0.66 near centre
and falls off ~linearly, reaching zero around 430 px, pinned by arithmetic rather than judgement:
a dark fuselage `(38,2,9)` at α 0.66 predicts 181 and measures 179, and the compass strip
`(20,20,18)` predicts 175 and measures 178, two surfaces two orders of magnitude apart in brightness
both within ~2/255. The rig **pops in complete** when the sun core crosses into frame and fades out
over ~0.125 s as it leaves: instant attack, timed release.

⚠ The one measurement the additive blend overrules: the ring annulus reads `(194,226,254)` against a
200 sky, i.e. R *below* the background, which additive cannot do. That is 6/255 on a compressed
frame next to a saturated highlight, and is treated as capture noise, but it is the first
assumption to revisit if the intensities cannot be hit without blowing out the core.

Line of sight for the sprites (not the wash) is one ray to the sun's centre against the world's own
colliders: terrain and the flown plane block, billboard sprites and particles don't, so drifting
smoke or eruption puffs cannot occlude the flare, matching the footage. Testing the centre rather
than a multi-sample disc is chosen because it needs no invented coverage threshold, not because
either reading is decoded; the footage cannot distinguish them.

Ring-intensity calibration must match two things: the footage's ring Δlum was read off frames that
already carried the wash (a true difference `d` reads as `d·(1−α)`), and `measure.py`'s `sample()`
reports the brightest pixel in a window, not a median around the annulus, a median reads
systematically lower, so the calibration values below are brightest-pixel to match. Measured at a
328 px sun-to-centre distance (inside the footage's 311–340 px pose band), `--no-fog`, 1280×720:
Ring A 0.38 → 11.0 (target 10–12, nudged up from an initial 0.30), Ring B 0.45 → 17.3 (target
14–21, unchanged), Ring C 0.18 → 5.0 (target 3–9, unchanged). Only Ring A needed moving; B and C
landed mid-band and were deliberately left alone, since the bands are the spread across two poses,
not error bars, and fitting to the middle would be fitting to the estimator.

## The horizon dome, drawn camera-centred

The dome is a **pure zero-parallax backdrop**: it is re-centred on the camera in *all* axes every
frame, so the moon stays at its designed elevation against the dark cap instead of sliding into the
bright horizon band as the plane climbs.

The domes are ordinary zone-stamped geometry, so which one draws is the gate above, not a separate
rule: below the deck a deck chapter's camera is state 1, its `zone_id 2` dome is culled, and
`horizon/zone1`'s own camera-anchored, UV-scrolled geometry **is** both the sky and the overcast
ceiling the player flies under. A chapter with only one buildable dome keeps it at every state, "no
sky at all" is not a frame the original can render.

Read off the extraction, per chapter's zone-1 dome (nodes, flat ceiling-cap altitude in dome-local
metres, cap outer radius):

| Chapter | Zone | Nodes | Cap Y | Cap radius |
|---|---|---|---|---|
| C1 | zone1 | 2 | 2792.8 | 2608.7 |
| C1C | zone1 | 1 | 2374.7 | 1448.2 |
| C2B | zone1 | 1 | 2374.7 | 1448.2 |
| C4 | zone1 | 1 | 982.0 | 6400.0 |
| C5 | zone3 | 1 | 982.0 | 10137.1 |

C1 is the only chapter whose zone-1 dome carries a texture at all (`sky2.tif` on the vault) and the
only one that scrolls it. C1C and C2B share one untextured `FOG_COLOR` shell (identical vertex data,
different model index). C4's sole zone-1 node is **confusingly named `h_zone2scroll`**, a reused
name, not a scroll: its model's `texture_scroll` is 0.

⚠ **Every horizon model in every chapter is authored `fog: false`.** The below-deck ceiling is
therefore *unfogged* geometry, which is also the standing explanation for the "ceiling texture
survives to ~12.6 km" anomaly. Any measurement that fits a distance from the
fog ramp on that surface is measuring nothing.

⚠ **C1's below-deck ceiling is at +2792.8, not +396.4.** The 396.4 figure was `h_zone1scroll`'s
model `bbox_mid.y`, read as a "cap centre"; the bbox spans −2000…+2792.8 and there is **no geometry
within 2 km of its midpoint**. The mesh's actual flat cap is the 12-gon at +2792.8. This killed
B14's own opening premise.

Which zone a mission *flies* is in **no reader file** (searched exhaustively), so it is settled per
chapter by A/B against the original, with the chapter's own horizon contents as the tie-break where
a requested zone's subtree is a bare marker. C5 = `zone1`, confirmed by playtest, the user flew
C5/IA1 in the original and can see across the city, which `ZONE3`'s 50–250 m fog and 300 m clip
would make impossible.

### The dome's own colour against `FOG_COLOR`

The dome as drawn, sampled off `--freecam --det` captures in original mode (mean sRGB over a fixed
rect, 0-255), beside the `FOG_COLOR` the flown zone authors:

| Chapter / zone | dome top | dome at the horizon | authored `FOG_COLOR` |
|---|---|---|---|
| C1 zone2 | 174, 174, 174 | 176, 176, 176 | 176, 176, 176 |
| C1B zone1 | 35 median luminance under the puff field | 29, 37, 58 | 16, 24, 48 |
| C3 zone1 | 176, 209, 242 | 186, 193, 205 | 201, 201, 201 |
| C5 zone1 | 17, 18, 26 | 2, 2, 3 | 0, 0, 0 |

`FOG_COLOR` is the colour the dome fades into at eye level, and it holds as a whole-dome
approximation: the grey-sky chapters match it within 2 and 15 units, and both night chapters sit
within a few units of black like their own fog. Only C3 disagrees in hue, where the dome's blue top
carries almost exactly the fog's luminance (204 against 201) without its neutrality. No zone block
carries a sky colour of its own, so nothing finer is decoded: the dome's colours live in its
textures and vertex data, per chapter, not per zone. Enhanced mode paints its Environment sky this
colour (`WeatherRig.WriteSkyColor`), which is what a reflection reads.

## The cloud deck's two regimes

The deck tiles are **ordinary world meshes carrying `zone_id 2`** at their own authored altitude
(C1/C1C/C2B 960, C4 1050). Nothing in the decompile moves them, and below the deck the original
culls them outright through the gate above.

What the crossing at the band **centre** does decide is which face of the overcast the camera sees,
and therefore whether the sheet carries the mission's `SUNLIGHT` dimming: below it the dimmed
underside, at or above it the undimmed top. That is a *regime* rule, two different objects, rather
than face-dependent lighting, and it is measured both ways: the original's underside reads 167.7
(ours 168.9, dimmed), and **no pixel in any original above-band frame falls below `FOG_COLOR` 175**,
which a 168.9 surface cannot satisfy at any fog setting, fog being a pull *toward* the fog colour.

⚠ The lit variant **jumps** at the crossing. That is unobservable only because the crossing is the
band centre, which is the middle of the fully opaque whiteout core (C1: total in 1032–1062).

⚠ The deck flip is at the band **centre** while state 2 is at the core **bottom**, see the
three-altitude warning above. The two are 87 m apart in C1, the whole interval sits inside the
opaque core, and the sheet is culled for the entire below-band half anyway.

The four deck chapters ship the deck ~10 m below their `fvol1`–`fvol9` slab floor: C1 960/970.00,
C1C 960/970.73, C2B 960/970.00, C4 1050/1060.00. That mesh/slab relationship is authored and is not
something the runtime establishes.

## The wind

The `WIND` block is a steady base velocity plus a horizontal random-walk gust. It is stepped
**once per frame, at the head of the tick, ahead of every emitter and every particle**
(`FUN_0054ee10`, the same tick documented in [`puffer.md`](puffer.md)); there is **one wind for
the world**, not one per camera. `RANDOM_ANG_VEL` is authored in **degrees per second** and the
binary converts on the way into its global (`FUN_004bc680` → `FUN_005506d0(value × 0.017453292)`).

Every one of the install's 53 `weather.zrd.json` files authors all four keys identically:
`STATIC_VELOCITY (0, 2, 0)`, `RANDOM_MAX_SPEED 10`, `RANDOM_ACCEL 5`, `RANDOM_ANG_VEL 5`.

⚠ **Wind does not move the cloud clutter.** The ambient cloud field is the authored `fogvol.zrd`
geometry scattered through the `fvol` volumes, static world geometry, and no reader says wind
touches it. Wind's one decoded consumer is the puffer particle system.

## Where CSVM deliberately differs

Everything here is a known, deliberate divergence, not a gap waiting to be closed.

| Divergence | Why |
|---|---|
| **`ZONE3`'s `CLIP_RANGES` far (300 m) is not applied** | The remake fogs instead of clipping and keeps a far plane much larger than the original's; the fog is what hides distant terrain |
| **The gate is a per-camera CULL MASK, never `Node3D.Visible`** | Splitscreen panes can sit in different states at the same instant, and several other subsystems already read/write `Visible` on that same world content. The two camera-anchored per-rig singletons (deck, dome) are the exception and do use `Visible`, because a per-player copy is already private to one camera |
| **The flicker drift is per RIG, not one global** | The original's `_DAT_0064efcc`/`_DAT_0062154c` pair is a single global, which assumes one camera; two panes on opposite sides of the band must not share a drift phase |
| **The flicker amplitude ramps in over ~0.5 s per rig** | Neither curve is the identity at an interior opacity, so a fresh instance would otherwise pop; the ramp makes a rig's first tick return the unremapped opacity bit-for-bit, which is what static probes and golden shots were pinned against |
| **The flicker rate constant** | The engine reads it from an undecoded per-mission field; ours is picked so the drift traverses `[0,1]` in a few seconds (`BL-329`) |
| **Fog + sun are one global set, applied from rig 0** | Same as the original (one `sunlight` node, one fog set), but it means a splitscreen pane wears player 1's zone |
| **The whiteout is one screen-space overlay carrying BOTH the band and the volume curtain**, unioned `a + b − a·b` | The binary computes one camera-space density and blends the frame with it; the union is its own combiner between volumes. The two never coexist in shipped data, so this is unmeasurable today, it is a union rather than a pick so that a future chapter authoring both would not silently lose one |
| **That overlay draws UNDER the cockpit interior's own pass**, per pane | The original applies the density to the world draw, which the interior does not take, so the window whites out and the canopy, panel and gauges stay clear. An overlay above the pass whitens the panel too, which is not what the original does at the controls |
| **The world is rendered fullbright and dimmed by a scalar** | We do not reproduce the per-vertex `N·L` bake; the scalar is the data-driven collapse of it (see above) |
| **The band whiteout's fallback colour (0.95, 0.95, 0.96)** | A TUNE, and only where a mission authors no `CLOUD_COVER` colours. ⚠ Do not "unify" it with the zone `FOG_COLOR`: C1's fog is 0.69 = 176, which would darken a passing A/B by 70 units. CAP-12 puts C1's in-cloud interior at 248 in the original against our 243 |

## Retired and superseded findings

Kept because in each case a *measurement* died, not just a use, and the next reader must not
re-fit it.

### ⚠ `DeckCeilingHeight` — RETIRED (2026-08-09)

The height at which the deck sheet was hung above a below-band camera as the overcast **ceiling**.
**There is no such mechanism.** The deck tiles are ordinary `zone_id 2` world meshes, the original
culls them outright below the deck, and what the player sees overhead there is `horizon/zone1`'s own
camera-anchored, UV-scrolled dome. The right *behaviour* on the wrong *object*.

Both of its fits measured a surface the original does not fog:

- A7 read **K = 400 m** from apparent mottling scale against a texture period that was wrong by a
  factor of two.
- C21/C25 re-fit it to **110–155 m** (pick: 135) from the fog ramp on
  `OriginalScreenshots/C1 IA1 Fog river.png`, i.e. by assuming the surface overhead **fogs**. It
  does not: every horizon model in every chapter is authored `fog: false`.

⚠ The one live consumer of the number was the rim-annulus arithmetic (half-span 20,480 m, so the
below-band rim lands inside the fog-saturated band). **That geometry is unchanged**, it is still
what keeps the above-band floor's edge out of frame.

### ⚠ The band-centre deck pin, SUPERSEDED

The deck was pinned to the cloud band's centre. It sits at the tiles' **own authored altitude**,
read off the built data. C4 was unmoved by the change (its authored altitude *equals* its band
centre, 1050, a coincidence that made the pin look right); C1's 960 against the former 1047 pin was
an 87 m drop. Earlier still, until A6, the pin applied in *both* regimes, which buried the deck
inside the `fvol` slab and hung every sprite below it.

### ⚠ The altitude-keyed cloud-population gate, SUPERSEDED

A hand-rolled rule that decided whether the two ambient cloud populations rendered, keyed on camera
altitude. It was the `zone_id 2` special case of `FUN_0056c430`. The case that killed it is C2B's
`zone_id −1` fog volumes, which must keep rendering *below* its deck where the altitude rule hid
them. Do not re-add it: two owners of one visibility question was the failure.

### ⚠ The `fogRangeFactor = 2.0` halving — RETIRED (2026-08-08)

"This does not seem to be radius but diameter." The authored ranges **are** the ranges. The data
never supported the factor: `VIEWING_RANGE` ships `FOG_SCALE 1.0` at HIGH detail in all eight
chapters and every other multiplier in that block is ≤ 1 (MED 0.85, LOW 0.7), so nothing in the file
shortens a range at all. Measured, the halved range saturated the C1 overcast ceiling into flat fog
far too close in.

⚠ **Do not re-open that gap from the C3 residual.** The instrument that produced C3's
"106 against the original's 36.5" disagrees with the user's own eyes on the flown scene; the C3 murk
was closed at the controls on 2026-08-09 (`BL-321`), and the measurement boxes, not the fog, are
what is unreliable there.

⚠ **Do not re-diagnose the C1 river pose as fog either.** The original's overcast ceiling reads
166–175 in its own still while ours rendered 200–220 *before* any fog, that was the deck's own
underside brightness seen from below, and it is fixed (now 168–170 unfogged against 167.7). The
stills that settled it were framed before the external views took the decoded 46.8° vertical base
(`docs/org/cameraViews.md`), so a re-comparison must re-frame the pose rather than reuse them; the
brightness figures survive that, the framing does not.

### ⚠ The zone1 fog/band coincidence, RETIRED as evidence

"zone1's 970→1047 is exactly cloud-band bottom → whiteout centre" was used to corroborate the
altitude-fade reading. C1 flies **zone2**, whose band is 4000→5000 m, above the 2,500 m flight
ceiling, i.e. night fog at every flyable altitude. The identity lives in a zone C1 never flies. It
is real and unexplained, and it is not evidence.
