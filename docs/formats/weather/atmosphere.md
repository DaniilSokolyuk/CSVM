# Weather atmosphere controls

Part of: [weather](../weather.md).

## Cloud cover (`CLOUD_COVER`)

The whiteout band (a vertical altitude band the plane vanishes inside), bare-scalar block:

| Key | Meaning |
|---|---|
| `TOP` / `BOTTOM` | band edges (metres altitude); sight is clear at both |
| `THICKNESS` | depth of the fully-opaque **core**, centred on the band midpoint, **not** an edge transition. C1/IA1 970–1124 ±30 ⇒ clear at 970/1124, total only in 1032–1062, linear ramps between |
| `TOP_COLOR` / `BOTTOM_COLOR` | *(optional)* the **band's** colours (integer RGB), what the in-cloud whiteout paints. Absent in C1/IA1. Decoded into `CloudTopColor`/`CloudBottomColor` and consumed by `WeatherState.WhiteoutColor` |

⚠ **`TOP_COLOR`/`BOTTOM_COLOR` are not the deck mesh's face tints**, whatever the names
suggest (this page said so until). Three of the four chapters that author them,
C1B, C3, C5, ship **no `CloudDeck` mesh at all** (`WorldBuilder`'s coverage table), so there
is nothing there to tint. They track the cloud band, and the render confirms it: C4 authors
`[192]³` and the original's in-cloud veil measures a flat 192 (`CAP-12`'s C4 take).

**The band's MIDPOINT is load-bearing twice over** (`WeatherState.CloudBandCentre`, one spelling
for both; `A7`). It centres the opaque core above, and it is also the altitude at
which the cloud **deck** changes regime, below it the deck is a ceiling carried with the camera,
at/above it a world-fixed floor sitting exactly on the centre, and both ambient cloud populations
are hidden below / shown above, per camera. That is a rendering rule, not a datum: nothing in
`CLOUD_COVER` says so, and it is decoded from the original at the controls. What makes it
invisible is that the two are the same altitude, the deck's jump happens in the middle of the
fully opaque core, so **moving the band or thinning `THICKNESS` exposes a hard pop**. C1/IA1:
flip at 1047, core 1032–1062. See `WeatherRig.DeckRegime` and `docs/architecture.md`'s
`WeatherRig.cs` entry for the mechanism and the ceiling distance's derivation.

**Correction (decompile): the behaviour was measured right, but "one mesh relocated
by the engine" is not the mechanism, it is two different objects, swapped by the `zone_id`
gate** ([decoded above](../weather.md#zone-selection-at-runtime)). Above the deck, what renders is
the **authored world-fixed tiles at their own authored altitude**, C1/C1C/C2B 960 m, C4 1050 m,
not a mesh re-pinned to the band centre; A7's "exactly on the centre" reading was **C4's own
coincidence**, because C4 happens to author `CLOUD_COVER` centre = 1050 = its tile altitude. C1's
centre is 1047, its tiles 960, an 87 m gap the centre-pin model was silently absorbing. Below
the deck, the ceiling the player sees is **not the deck mesh at all**, it is
`horizon/zone1`'s own geometry, camera-anchored and UV-scrolled (`tex_fx.gw`, `Object3DSetScroll
on 0.07 0.0`, [the horizon's own geometry](../weather.md#horizon-geometry)
section above). ~~In C1 that geometry (`h_zone1scroll`, model 768) has an authored cap centre of
**396.4 m dome-local** (`bbox_mid.y` of the model, spanning Y −2000…2792.8), which is A7's
measured "~400 m above the camera" to instrument precision.~~ **396.4
is that bbox's MIDPOINT and no polygon sits near it, the mesh's flat ceiling cap is at
+2792.8 m dome-local, and the agreement with A7's "~400 m" was a coincidence twice over (A7 was
measuring the deck sheet, and `C25` later re-fit that same reading to 110–155 m).** See
[the zone-1 ceiling table](../weather.md#the-3964-m-cap-centre-is-a-bounding-box-midpoint).
The two objects are swapped by the
gate, not carried/relocated by `WeatherRig.Tick`, the deck tiles are `zone_id 2` (culled below
the deck), the zone-1 dome is `zone_id 1` (culled above it), and each renders only when the
camera state makes it visible. The install-wide survey, [the deck census
below](../weather.md#deck-census), found C1's `h_zone1scroll`
+ `o28` skirt pairing is **not** reproduced identically in C1C/C2B/C4: those three each carry a
**single** zone-1 mesh (not the two-piece dome+skirt), so the ceiling geometry is per chapter, not a
shared constant, `B14` measured all five (the four deck chapters plus C5's `zone3`) into
[the zone-1 ceiling table](../weather.md#the-3964-m-cap-centre-is-a-bounding-box-midpoint).

> **Landed.** The remake builds a dome per gateable
> horizon zone (`WorldBuilder.DomeZonesToBuild`) and shows the one matching each camera's own
> weather state, so below the deck a deck chapter now renders `horizon/zone1` and above it
> `horizon/zone2`. The scroll needed no code: `h_zone1scroll`'s model carries `texture_scroll`
> 0.07 in the shipped gamez (the `tex_fx.gw` statement is already baked in), and the build path
> already honours that field. The deck's own below-band relocation, A7's `cam+K` ceiling
> reconstruction and the `DeckCeilingHeight` TUNE with it, is deleted: the tiles are world-fixed at
> their authored altitude at every camera altitude, and the original culls them below the deck
> anyway. Measured at the C1 river pose (`-7325,192,-3829`, `--det`): looking straight up the frame
> goes from the `zone2` night sky (64,72,100) to flat `FOG_COLOR` **176**, the zone-1 cap, across
> **100 %** of pixels; a 1 s pair at +30° pitch differs on **47 %** of the frame (2 s: 55 %) as the
> `sky2.tif` vault scrolls, while C2B's unscrolled zone-1 shell moves only its rain (3.5 %,
> uniformly distributed). The pinned above-deck C1 pose, C1B and C5 are **byte-identical**.

This also **un-retires** the "C1 `zone1` 970/1047 `FOG_ALTITUDE` identity" note below as a
flown-zone fact: `zone1` is flown below the deck in every deck chapter, so its `FOG_ALTITUDE`
pair is live data during below-deck flight, not an artifact of an unflown zone. See the ⚠
un-retirement at that paragraph.

**Decoded (was inferred): the whiteout lerps `BOTTOM_COLOR` → `TOP_COLOR` across the
band by camera altitude, that is exactly what the binary computes.** `FUN_0042ee40` (the
per-frame atmosphere update, see the zone-state section above): for camera altitude between
`BOTTOM` and `TOP`, colour = `TOP_COLOR·f + BOTTOM_COLOR·(1−f)` with `f = (alt − BOTTOM) /
(TOP − BOTTOM)`; opacity ramps linearly 0→1 from `BOTTOM` to the core's bottom (centre −
`THICKNESS`/2), holds 1.0 through the core, and ramps back down to `TOP`, byte-for-byte the
`THICKNESS`-core rule this page already carried. Two engine details on top of the data: the
loader defaults an absent `TOP`/`BOTTOM` to 280/240 and colours to white, and the final opacity
is remapped through a log2/atan curve pair blended by a `rand()`-driven drifting parameter, an
animated in-cloud turbulence flicker with no data behind it (a TUNE-shaped fact about the
original; the remake does not reproduce it). The shipped data alone could never falsify the lerp
(of the reachable bands only C4 authors colours, and equal ones), which is why this stayed
marked inferred until the decompile.

### An object on the far side of the band is not drawn at all

**Decoded.** The band does more than veil the camera's own view. Every flown object earns a
`zone_id` once per frame from **its own** altitude, and a camera draws only the zone it is
standing in, so an aeroplane or an airship sitting wholly on the other side of the band is
culled outright rather than fogged.

`FUN_00489f60` is the assignment, reached from 19 object-update sites. It takes the object's
world-space vertical span (the node's bounding box under its transform, `FUN_00492fc0`, with an
oriented-box arm inlined for the rotated case) and compares it to the band's **midpoint**
`_DAT_0071c2cc`, then writes the verdict onto the node with `FUN_004cd3b0` (node + 0x30):

| condition | `zone_id` |
|---|---|
| the object sits in an armed `fvol` volume (`DAT_0064ff40`, `FUN_0044ded0`) | **3**, tested first |
| span max < midpoint | **1** |
| midpoint < span min | **2** |
| the span straddles the midpoint | **0xff** (−1, drawn at every state) |
| the mission authors no `CLOUD_COVER` (`DAT_0071dcd0` clear) | **0xff** |

The midpoint is computed once at world init, `004736a5`: `_DAT_0071c2cc = BOTTOM +
(TOP − BOTTOM) × 0.5`, the `0.5` being the constant at `006032e0`. The next four instructions
write the opaque core's edges from it, `0071c2d0 = midpoint − THICKNESS × 0.5` and
`0071c2d4 = midpoint + THICKNESS × 0.5`.

The gate is [`FUN_0056c430`](../weather.md#zone-selection-at-runtime), which draws a node only
when its `zone_id` is 0xff or sits in the camera's armed set `{0, camera state}`. A camera below
the band is state 1 and draws nothing carrying zone 2; above the core's bottom edge it is state 2
and draws nothing carrying zone 1.

⚠ **The object's threshold and the camera's are different altitudes.** The object is judged
against the MIDPOINT, the camera's state flips at the core's bottom edge (`0042eed3`). The window
between the two lies inside the fully-opaque core, which is what keeps the mismatch invisible;
moving the band or thinning `THICKNESS` exposes it, the same way it exposes the deck's regime
flip above. C1C/M01: band 1055–1110, midpoint 1082.5, core 1067.5–1097.5.

**Ruled out, with the readers enumerated.** There is no per-object cull against `TOP`/`BOTTOM`
themselves and no fog term keyed on an object's height. Every reader of `0071dcd4` (`TOP`),
`0071dcd8` (`BOTTOM`), `0071c2d0` and `0071c2d4` is: world init `FUN_004735b0`, the per-frame
atmosphere update `FUN_0042ee40`, the zone applier `FUN_00472ea0`, and the sun-flare occlusion
test `FUN_0042b7c0`, which stops casting the flare ray once the camera passes
`BOTTOM + (TOP − BOTTOM) × 0.8` (`0042b81a`). `ZONE1`'s `FOG_ALTITUDE` fades fog **out** with
height, so it cannot be what hides anything above the band either.

**An object standing INSIDE the band** takes a separate per-object whiteout instead.
`FUN_005782d0` takes the object's own centre and radius and returns 0 when the sphere clears the
band, `1.0` (`_DAT_00a06f74`) through the core, and the linear edge ramps between;
`FUN_00551d90` raises the object's draw flag 0x800 once that exceeds 1/255. The four edges are
pushed into the particle module at world init as `_DAT_00a06f78` (`TOP`), `_DAT_00a06f7c` (core
top), `_DAT_00a06f84` (`BOTTOM`) and `_DAT_00a06f88` (core bottom) with their reciprocals, armed
by `DAT_00a06f70`, which is set only where the mission authors a band.

> **Landed.** The remake ports the cull. `WeatherState.ObjectZone` is the rule above and
> `Session.Objectives.ObjectZoneGate` applies it per frame to the session's aircraft and zeppelins, moving
> each object's meshes onto the zone's visual layer the camera cull mask already narrows to, so
> the effect meshes hanging under an aircraft ride with it. A mesh already wearing a layer of its
> own (the own-airframe hide, a per-pane copy) stays ungated, since a cull mask ORs its bits and
> those layers are camera-local anyway. `--no-fog` opens the gate along with the whiteout, and the
> pane overlay is unchanged: the per-object in-band whiteout above is **not** ported, the remake
> whites the whole pane out from the camera's altitude instead.

## Wind (`WIND`)

Bare-scalar block, four keys, **all four present in all 53 `weather.zrd.json` files in the
install and all 53 authoring the same values**:

| Key | Shape | Every mission | Global | Setter |
|---|---|---|---|---|
| `STATIC_VELOCITY` | `[x,y,z]` m/s | `(0, 2, 0)` | `00763db4`/`b8`/`bc` | `FUN_00550690` |
| `RANDOM_MAX_SPEED` | float, m/s | `10.0` | `00763dc8` | `FUN_005506b0` |
| `RANDOM_ACCEL` | float, m/s per **frame** | `5.0` | `00763dd0` | `FUN_005506c0` |
| `RANDOM_ANG_VEL` | float, **degrees**/s | `5.0` | `00763dcc` | `FUN_005506d0` |

The reader is `FUN_004bc680` (its assert string names `D:\zipper\Crimson\weather.cpp`), which
reads the four keys straight into those setters and multiplies `RANDOM_ANG_VEL` by `0.017453292`
on the way in, **the key is in degrees per second and the global is radians per second.** The
same four setters are exposed on the debug console (`FUN_005b80a0`) as
`GlobalWindStaticVelocity` / `GlobalWindRandomMaxSpeed` / `GlobalWindRandomAccel` /
`GlobalWindRandomAngVel`, which is the original's own name for the mechanism and independently
confirms the mapping.

**The model** (`FUN_0054ee10`, at the head of the one puffer tick, before any emitter or particle
is touched, so there is exactly one wind for the whole world, re-derived once per frame):

```
heading  += rand(-1,+1) * RANDOM_ANG_VEL_rad * dt      // wrapped into [0, 2pi)
magnitude += rand(-1,+1) * RANDOM_ACCEL                //  <-- no dt (see below)
if (magnitude < 0) { heading += pi; magnitude = -magnitude; }
if (magnitude > RANDOM_MAX_SPEED) magnitude = RANDOM_MAX_SPEED;

wind = ( magnitude*cos(heading) + STATIC_VELOCITY.x,
                                  STATIC_VELOCITY.y,      // vertical is the static value verbatim
         magnitude*sin(heading) + STATIC_VELOCITY.z )
```

⚠ **The magnitude step carries no `dt` and the heading step does.** Raw x87: `0054ee3c`
`FMUL [00763dcc]` then `FMUL [EBP-0x10]` (the frame delta) for the heading; `0054eea1`
`FMUL [00763dd0]` and nothing else for the magnitude. The gust magnitude therefore takes one
`±RANDOM_ACCEL` jump **per frame**, which at the shipped 5 m/s step against a 10 m/s ceiling makes
it effectively re-drawn every frame and frame-rate dependent. Reproduced as traced (CSVM's
`Effects.WorldWind`), not smoothed, a `dt` nobody wrote would be an invented breeze.

⚠ **The gust is horizontal.** Only x and z carry it; y is `STATIC_VELOCITY.y` copied straight
through (`0054ef5f MOV [00763dac], ECX`). With the shipped data that is a steady +2 m/s updraft
under a gust that wanders anywhere in a 10 m/s disc.

⚠ **Not the same thing as the `PARTICLES` block's `WIND_DIR`/`WIND_VEL`** further down the same
file. Those are precipitation drift (see below) and touch nothing else.

**Its one consumer is the puffer particle system.** `FUN_0054ee10` damps each particle's velocity
toward `wind × WIND_FACTOR` rather than toward rest, and only when the puffer authors a non-zero
`FRICTION`. `WIND_FACTOR` defaults to **1**, not 0 (the puffer object's ctor `FUN_00550100` writes
`1.0` to `+0x6c`), so 2,802 of the install's 2,863 friction-bearing compiled `PufferState` events
are fully wind-carried; only 12 events across the six-strong `subdoors_puffer` family author an
explicit `0.0` to opt out.

It still does **not** move the cloud clutter. It used to drive the drift of the hand-tuned
`CloudPuffs` field, which the authored `fogvol.zrd` clutter does not use (`BL-273`,
[fogvol.md](../fogvol.md)), that field is static world geometry and no reader says wind moves it.

## Precipitation

Some missions end with a precipitation block, the last thing in the root dict, **after
`SHADOW_ANGLES`**, as bare-scalar top-level siblings (not nested under a key, and not a
sub-dict). Decoded fully on this install:

| Key | Type | Meaning |
|---|---|---|
| `TYPE` | string | `SNOW` or `RAIN` (its presence is what gates the whole block) |
| `PARTICLES` | int | **RAIN only**, density hint (all RAIN missions: `100`). SNOW omits it |
| `COLOR` | int-RGB triple | particle tint, every mission seen is `[128, 128, 128]` (mid-gray) |
| `WIND_DIR` | float | drift heading, degrees (all seen: `0.0`), the precipitation's *own* wind, separate from the cloud `WIND` block |
| `WIND_VEL` | float | drift speed, data units (all seen: `0.8`) |
| `GRAVITY` | float | fall-rate multiplier, data units, **SNOW `1.0`, RAIN `3.0`** (rain falls ~3× faster) |
| `ALPHA_GRADIENT` | float pair | `[0.5, 0.0]` everywhere, `[0]` is the peak opacity (the field is quite translucent) |

Observed values (this install): **SNOW**, C4/IA1 + C4/M01. **RAIN**, C1C/IA1 + C2B/IA1
(byte-identical to each other). C1/C5 IA1 carry **no** block. (The user recalled "rain" in
the Rocky Mountains while the data says SNOW, at flight speed gray streaking flakes read
either way; the data drives it, the A/B confirms.)

**Parsing gotcha**, same as `CLOUD_COVER`/`WIND`: these keys pair with *bare* scalars
(`"TYPE", "SNOW"`, `"WIND_DIR", 0.0`), so `ZrdrDict.FromAlternating` (which needs list
values) can't read them, it treats a bare-scalar key as a valueless flag and drops the
value. `Weather.cs` walks the raw `inner` list instead (`StringAfter`/`ScalarAfter`/
`ListAfter`/`Vec2After`). The keys are unique at `inner`'s top level (the zone sub-lists'
`FOG_COLOR`/`SUNLIGHT_*` are nested one level down, which the flat walkers never descend
into), so first-match is always the right one. Note all numbers arrive as `float` via
mech3ax's `GetSingle()`, so `PARTICLES 100` is `100.0f`, read with `ScalarAfter` and cast.

`COLOR` is dual-encoded like the other colour triples (here always the integer form) and
normalized by `ParseColor`; it's a DX7 sRGB framebuffer value, so the renderer converts it
sRGB→linear (same as `FOG_COLOR`).

Rendered by `CSVM/src/Effects/Precipitation.cs` as one camera-following MultiMesh
field the plane flies through (SNOW = billboarded flakes, RAIN = fall-aligned streak quads);
the data→look scale factors (fall m/s, particle count, box size, streak length) are marked
`TUNE` there.
