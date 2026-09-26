# The aircraft ground shadow, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-08-13, for the ground shadow port. Every claim
below names the function it came from.

Everything here is a description of *behaviour and constants*. No decompiler output is reproduced;
the addresses are given so any claim can be re-checked at source.

**Where the other halves live.** The authored side (the top-level `SHADOW_ANGLES` and `SHADE_*`
keys, and the `SUNLIGHT_DIFFUSE`/`SUNLIGHT_AMBIENT` pair the shadow colour is derived from) is
[`formats/weather.md`](../formats/weather.md); the sunlight node those two land on is
[`weather.md`](weather.md)'s "The sun". The implementation half is
[`../architecture/Flight.md`](../architecture/Flight.md)'s `GroundShadowLaw`/`GroundShadowPass`
entries, and the last section says what it takes from here and where it departs; the departures
are kept by decision (`git log --grep=BL-331`), and `BL-332` owns the authored light intensities.

⚠ **This page is a decode, not a proposal.** Where it disagrees with a footage measurement, the
decode wins and the disagreement is a note. Nothing on this page has been checked against footage;
the last section says what to look for.

## Function map

| Address | Role |
|---|---|
| `FUN_0049d3d0` | The per-frame shadow pass: the sun-occlusion test, then every aircraft, then the Spruce Goose |
| `FUN_0049d0a0` | One projected shadow: direction, both fades, the ground query, and the object's lifetime |
| `FUN_0049cf00` | The shadow colour, from the sunlight node's diffuse and ambient |
| `FUN_00565d80` | The projection, the footprint, the 32×32 raster, the blur and the ramp |
| `FUN_00565ce0` | Publishes the texture under the material name `gModShadow` |
| `FUN_005667c0` | Gathers the live shadow objects into the frame's array, 200 at most |
| `FUN_005668b0` | Selects, per world chunk, the shadows whose footprint overlaps it in x and z |
| `FUN_004d5910` / `FUN_004d39c0` | The world chunk and node draw that apply the selected set |
| `FUN_00565ae0` / `FUN_00565bf0` / `FUN_00565b60` | The shadow object: alloc, its 32×32 16-bit buffer, free |
| `FUN_00565aa0` | Decides **which** of the two shadow implementations is live |
| `FUN_004b3050` | The authored `shadow` node driver, the other implementation |
| `FUN_00476250` | Resolves `shadow` (or `f18shadow`) on the plane model at load, into `plane+0x2bc` |
| `FUN_0049cb90` | Resolves the `sprucegoose` node and its `shadow` child |
| `FUN_004c76e0` | The terrain column query at an (x, z): a count byte, then 0x2c-byte records with the hit height at `+0x10` |
| `FUN_004c7630` | Its wrapper that keeps the highest surface **at or below** the query point |
| `FUN_00538920` | Distance squared in **x and z only**; it never touches y |
| `FUN_0053bf40` | Euler → the 3×3 orientation matrix at `plane+0x180`, rows = the aircraft's local axes in world space |
| `FUN_0049ccb0` | The `SHADE_*` sun-occlusion test, adjacent to this and a different feature |

## Two implementations, one switch

`FUN_00565aa0` sets the global at `00a06f90` to 0, then to 1 only when `009be708 == 1` (the
hardware renderer, the same flag `FUN_00472ea0` uses to pick the `SW_ZONE` weather tables) **and**
`009c6820` is non-zero (a device capability). That one flag picks the implementation, and the two
are mutually exclusive:

| | Projected shadow | Authored card |
|---|---|---|
| Driver | `FUN_0049d0a0`, from the pass in `FUN_0049d3d0` | `FUN_004b3050`, from the per-aircraft update |
| Runs when | `00a06f90 != 0` | `00a06f90 == 0` |
| What it draws | the aircraft's silhouette, rasterised live into a 32×32 texture and modulated onto the ground the footprint covers | the `shadow` node authored on the plane model, moved down to ground level and flattened |

⚠ **The projected shadow is not a quad.** `FUN_005667c0` collects the frame's live shadow objects
(200 at most), and the world draw asks `FUN_005668b0` per chunk which of them overlap that chunk in
x and z (`FUN_004d5910`, passing the chunk's own bounds at `chunk+0x10`); the node draw then applies
the selected set as an extra modulate pass over that geometry's own polygons, with texture
coordinates from the footprint (`shadow+0x48`/`+0x4c` hold 1/width and 1/depth). So the shadow
conforms to whatever ground lies inside its footprint rather than lying on one flat plane, and a
node that is itself a caster is excluded from receiving its own shadow (`FUN_004d39c0` at
`004d3bba`). A remake that draws a quad instead owns the difference.

⚠ **The `shadow` node on the plane models is the software fallback, not the real shadow.** It is a
genuine authored card and it is genuinely used, but only on the non-accelerated path, and
`FUN_004b3050` hides it outright whenever the projected path is live. A remake reproduces the
projected path; excluding `shadow` from the built plane (`PlaneBuilder.cs`) stays correct.

## The projected shadow (`FUN_0049d0a0`)

The pass in `FUN_0049d3d0` calls it once per live aircraft, passing the plane's shadow-object slot
(`plane+0x2c0`), the model root to rasterise (`plane+0x0c`), the plane's position, an is-this-the-
player flag, the plane's orientation matrix (`plane+0x180`), and four range constants. It then calls
it once more for the Spruce Goose with its own four. The four differ per caller and nothing else
does:

| | near dist | far dist | full-shadow altitude | cutoff altitude |
|---|---|---|---|---|
| Aircraft | 1 | 200 | 60 | 250 |
| Spruce Goose (`sprucegoose`) | 300 | 600 | 180 | 750 |

⚠ **These six arguments are pushed for the position virtual call and deliberately left on the stack
as the tail arguments of `FUN_0049d0a0`.** Ghidra's decompiler misattributes them to the virtual
call; the disassembly at `0049d3fb`…`0049d42d`, with its single `ADD ESP,0x24` covering nine
dwords, is what settles the signature.

### Direction

The direction is the top-level `SHADOW_ANGLES` when the weather file authors one, and the sunlight
node's own direction otherwise. All 53 shipped weather files author `[-90, 0, 0]`, which through
`FUN_0053c610` is exactly straight down, so **in every shipped mission the projection is vertical**
([`formats/weather.md`](../formats/weather.md)). `FUN_00565d80` refuses to draw at all unless the
direction's y is negative, confirming the vector is the direction light *travels*.

Reading the globals this function tests also pins the parsed weather struct's base at `0071dcd0`:
`+0x28`/`+0x2c` are the `SHADOW_ANGLES` present-flag and vector, `+0x38`/`+0x3c` the `SHADE_ANGLES`
pair, and `+0x54` the `ZONE1` array that `FUN_00472ea0` indexes at `0071dd24` with stride `0x58`.

### The distance fade: horizontal, and linear in squared distance

Skipped entirely for the player's own aircraft. For everything else, with `d²` the **horizontal**
distance squared from the player (`FUN_00538920` differs x and z and ignores y):

- `d² ≤ near²` → factor 1;
- `d² ≥ far²` → the shadow object is destroyed and the function returns;
- between → `(far² − d²) / (far² − near²)`, i.e. linear in *squared* distance, so the fade is
  strongly weighted toward the far end.

### The ground height

`FUN_004c76e0` queries the terrain grid cell under the aircraft's (x, z) and returns a list of
surface records; `FUN_0049d0a0` takes the **plain maximum** height over all of them.

⚠ **That maximum is not "the surface below the aircraft".** `FUN_004c7630`, the wrapper the
authored-card path uses, does the correct thing and keeps the highest surface at or below the query
point. The projected path does not, so under a bridge or inside a canyon its shadow snaps to the
surface *above* the aircraft. This is a bug in the original, not a rule to reproduce.

### The altitude ramp

With `alt` the aircraft's height above that ground height, and the two altitude constants from the
table above: at or below the full-shadow altitude the factor is 1, at or above the cutoff it is 0
and the shadow object is destroyed, and between them it is
`(cutoff − alt) / (cutoff − full)`. For aircraft that is `(250 − alt) / 190`.

The two factors multiply into a single strength `A`. When `A` reaches 0 the shadow object is freed
(`FUN_00565d50` + `FUN_00565b60`) rather than drawn transparent, so a distant or high aircraft costs
nothing per frame.

### The colour (`FUN_0049cf00`)

The texture is a **modulate** map, so one end of its ramp is white (no darkening) and the other is
the colour computed here. Per channel, from the sunlight node's diffuse RGB (`+0xbc…+0xc4` of its
class data) and ambient RGB (`+0xc8…+0xd0`), which are the two triples `FUN_004dbdb0` and
`FUN_004dbce0` write, i.e. the mission's `SUNLIGHT_DIFFUSE` and `SUNLIGHT_AMBIENT` scaled by their
colours:

```
k       = ambient / (ambient − diffuse · dir.y)      // dir.y < 0, so the denominator is
                                                     // ambient + diffuse·|dir.y|
channel = clamp(round(255 · ((1 − A) + 0.8 · A · k)), 0, 255)
```

⚠ **`k` has a zero guard, and it does not fall back to 1.** A channel whose ambient is exactly 0,
or whose denominator cancels, keeps the raw ambient value in place of the ratio (`0049cf25`…
`0049cff0`), so an ambient of 0 gives `k = 0`, the darkest that channel can be rather than the
lightest. The 0.8 is not a literal inside this function either: it is the third argument
`FUN_0049d0a0` pushes as `0x3f4ccccd`.

`k` is the physical ratio "ambient only" over "ambient + diffuse·(N·L)" for flat ground with an up
normal. **The original darkens the ground by exactly the light the aircraft blocks**, times a fixed
0.8, faded toward white by `1 − A`. The literals are `1.0` at `6032dc`, the `0.5` rounding term at
`6032e0`, and `255.0` at `60414c`.

⚠ **Shadow darkness is per-mission, not a constant grey.** It follows the same
`SUNLIGHT_DIFFUSE`/`SUNLIGHT_AMBIENT` pair `BL-332` is about, and gets *lighter* as a mission's
ambient rises.

### The footprint and the size law (`FUN_00565d80`)

Each of the 8 corners of the model's bounding box is projected onto the ground plane along the
direction (`x + (ground − y)·dir.x/dir.y`, and the same for z), and the min/max of the projected
corners in x and z is the footprint.

⚠ **The `+2.0` is added to the footprint's TOP Y, not to its ground height.** `shadow+0x40` is the
maximum y of the eight corners, which the projection leaves untouched, and that is what `+2.0`
nudges; the ground height itself is stored unchanged at `shadow+0x1c`. It is the raster volume's
ceiling, not a lift off the terrain, and a remake gets no z-fighting remedy from it.

The footprint is then scaled about the projected origin by a factor that is **1 for every aircraft
except the player's own**, where it is `3 − 2·(altitude factor)`: 1× at or below 60 units, growing
linearly to **3× at 250 units**. Combined with the strength fade that reads as a shadow spreading
and softening as the player climbs.

### The texture

A 32×32, 16-bit buffer (`FUN_00565bf0` allocates `32·32·2` bytes), cleared and rebuilt every frame:

1. The model node is rasterised into it with the flattening projection above. For AI aircraft the
   whole model root is used; for the player's own aircraft the node named `geometry` is used
   instead when it exists (resolved once at `0071c31c`). The node's cull flag `0x8` is cleared
   first (`FUN_004ccf00`) so frame culling cannot skip the render.
2. A fixed 3×3 blur: the raster marks a covered texel with 1, then every texel still carrying that
   odd bit adds 4 to itself and 2 to each of its 8 neighbours. The border ring is never scanned,
   though it does receive, so accumulated values run 0…21.
3. Each texel is replaced by entry `value/2` of an 11-entry ramp built per channel as
   `255.5 − i·(25.5 − channel/10)`, rounded, for `i = 0…10`. That is exactly
   `mix(255, channel, i/10)` plus the half-unit rounding term, so entry 0 is white and entry 10 is
   the colour from `FUN_0049cf00`. An interior texel reaches 21 and saturates the ramp; a lone
   covered texel reaches 5, i.e. entry 2, which is what softens the silhouette's edge.

The result is published under the material name `gModShadow` (`FUN_00565ce0`).

Step 1 is the ordinary node draw, not a separate silhouette pass. `FUN_00565d80` sets the
shadow-render flag `DAT_00a07148` to 1 and the target buffer `DAT_009fda44`, then dispatches on the
node's rendering type at `node+0x34` into `FUN_004d6d80` (type 5) or `FUN_004d6e80` (type 6).
`FUN_004d6d80` pushes the node's own matrix, the 0x60-byte block at `classdata+0x18` off the class
data at `node+0x38`, rasterises the node's model through `FUN_00566340`, then walks the children at
`node+0x5c` (count at `node+0x56`) through `FUN_005662d0`, which dispatches back into
`FUN_004d6d80`. The visible render `FUN_004d39c0` pushes that identical `classdata+0x18` matrix, so
the shadow reads exactly the transforms the visible aircraft is drawn with, and a part animated this
frame (a propeller or rotor blur disc) is rasterised at the angle it has turned to. The footprint is
the exception: `FUN_004cd960` copies the node's *stored* bounding box from `*(node+0x70)`, so the
extent the texture is mapped onto does not follow the turn.

## The player's own aircraft is a special case, three times over

Worth collecting, because two of the three are the opposite of what a physical shadow would do:

1. **No distance fade** (it is always at distance 0 from itself, so this is only bookkeeping).
2. **The footprint grows to 3× by 250 units**, while every other aircraft's stays tight.
3. **The direction is skewed forward along the flight path.** Before use, `1.5 ×` the horizontal
   part of **row 2** of the orientation matrix `FUN_0053bf40` builds at `plane+0x180` is subtracted
   from the shadow direction (its x at `+0x198` and its z at `+0x1a0`) and the result renormalised.
   Since the vertical component is unchanged, the projection ratio becomes `1.5 ×` that row, and the
   shadow lands **1.5 × altitude ahead of the aircraft along its flight direction**.

⚠ **Row 2 is MINUS the nose, which is what makes this forward rather than backward.**
[`flightModel.md`](flightModel.md) proves it at a point of use: the thrust magnitude is negated and
then multiplied by that same row (`0x48fe91`), so `force = −magnitude · row2 = +magnitude · nose`.
Subtracting `1.5 × row2` therefore adds `1.5 × nose`, and `plane+0x1e0`, which caches the row
negated (`aiControlLaw.md`), holds the heading rather than the reverse of it. A reading that takes
row 2 for the nose puts the shadow behind the aircraft, where at any real altitude it sits behind
the chase camera and is never in frame; forward, it lies ahead of the nose in the default view,
which is where an altitude cue is worth drawing.

⚠ **Point 3 is decoded but unverified against footage.** The magnitude is large (45 units ahead, at
30 units of altitude). The falsification test is in the last section.

## The authored card (`FUN_004b3050`)

The software path, for completeness. `FUN_00476250` searches the plane's model root at load for
`f18shadow` when `plane+0x67c` is non-zero and `shadow` otherwise, and caches the node at
`plane+0x2bc`; a plane with no such node gets no shadow on this path at all. Per frame, when the
projected path is off:

- The player's own card additionally requires the node at `plane+0x3a0` to carry flag `0x4`.
- The ground comes from `FUN_004c7630`, correctly the highest surface at or below the aircraft.
- Above 100 units the card is hidden; at or below 20 units it is opaque; between them its node alpha
  is `(100 − alt) / 80`. Note these are much shorter ranges than the projected path's 60/250.
- The card is a child of the plane, so it is offset within the plane's local frame by
  `alt ×` (world down expressed in local coordinates), which is column 1 of the same orientation
  matrix. Its rotation is recomputed every frame so it stays flat to the world whatever the plane's
  attitude.
- Its X and Z scales are `clamp(|cos(angle)| / sqrt(1 − s²), 0.2, 1.0)` for roll and for pitch, Y
  stays 1: the card foreshortens as the plane banks or pitches, with a hard floor at 0.2.

There is no silhouette, no distance fade and no derived colour on this path. It is visibly the cheap
version.

The Spruce Goose sits between the two: it is drawn by the projected path, but its own child node
named `shadow` is used purely as an enable switch. If that node carries flag `0x4`, the shadow
object is destroyed instead of drawn.

## Sun occlusion: the `SHADE_*` keys (`FUN_0049ccb0`)

The other half of the shadow pass, and a different feature: whether the **player's aircraft** is in
shade. It traces from the player's position along `SHADE_ANGLES` (falling back to the sunlight
direction when the key is absent, which is every shipped mission) over `SHADE_DIST`, targets 1.0 for
lit and 0.0 for occluded, and moves the current value toward that target at `SHADE_CHANGE_RATE` per
second, re-testing every `SHADE_INTERVAL` seconds. The parser's defaults, in `FUN_004bc680`:
`SHADE_DIST 200.0`, `SHADE_INTERVAL 0.5`, `SHADE_CHANGE_RATE 4.0`. This is what darkens an aircraft
as it passes under a mountain or a zeppelin. CSVM does not implement it and no backlog item covers
it.

## What CSVM does today

The placement and the silhouette are implemented, in original graphics mode only
(`Flight/Airframe/GroundShadowLaw.cs` for the rule, `Flight/Airframe/GroundShadowPass.cs` for the drawing and
`Flight/Airframe/GroundShadowSilhouette.cs` for the raster; `docs/architecture/Flight.md`). What it takes
from this page and where it departs:

- **Godot shadow mapping cannot be the mechanism.** The original never casts a shadow map; it draws
  a projected silhouette onto the terrain. The world is built `fullbright: true` and an unshaded
  material receives nothing, so `BL-324`'s removal of `_sun.ShadowEnabled` stays correct and is not
  a regression to rediscover.
- Taken verbatim: the straight-down projection and the player's forward skew, the 1/200 horizontal
  distance fade, the 60/250 altitude ramp, the player's 3× growth, the colour formula with its zero
  guard, and the spread and ramp the coverage texture is built through.
- **"The player" above is the pane's own viewer.** The original has one local player and CSVM has
  up to four panes over one world, so the three exemptions (the skew, the growth and the exemption
  from the distance fade) are read per pane: an aeroplane draws the player shape in the pane whose
  pilot is flying it and an ordinary shadow in every other pane. That costs such an aeroplane a
  second quad, the two kept apart by the per-player visual layers (`UI/Boards/SplitScreen.cs`), and it
  chooses the silhouette node per pane with them, the `geometry` child for the pane's own pilot and
  the whole model root elsewhere.
- The silhouette is rasterised per frame from the aircraft's own triangles, off the node this page
  names (the `geometry` child for the player's own aircraft, the whole model root for every other),
  through the same texel mapping, spread and ramp. The triangles are read off the model once, on
  first sight, and the airframe's are held in the aircraft's own frame; a propeller or rotor blur
  disc is kept as a separate group and re-posed from its live node each frame, so it turns in the
  shadow the way the original's recursive draw turns it, without re-reading the whole model. Two
  departures inside it: both windings are filled where the original culls one, which is the same
  outline for a closed hull and differs only where a model's faces are inconsistently wound; and a
  hidden node rasterises nothing while still widening the bounding box, so a torn damage panel or
  the player's own interior cockpit mesh casts no silhouette.
- **The live texture is 64 texels on a side where the original rasters 32**, the one size in the
  port that is a choice rather than a decode. The original's step is coarse under the player's own
  aircraft, whose footprint grows to 3× as it climbs, and at the controls that reads blocky. The
  spread and the raster's own inset are written against the original's 32 and scaled with the step,
  so the edge softens over the same width of ground at either size and the picture is the decode's,
  finer. The spread itself is a weighted box rather than the mark-and-add above, which reproduces
  the original's numbers exactly at the original's step and is what allows a half-texel reach at a
  finer one.
- **64 rather than 128, on cost.** The spread's box grows with the step too, so the raster is
  quartic in the texture's edge, not square: one aircraft's raster and upload measures 0.11 ms at
  32, 0.32 ms at 64 and 3.65 ms at 128 per frame in a debug build, and the finest step also loses
  the raster's exact mirror symmetry to float rounding. At 64 the frame does not see it: on C2's
  M02 with eight AI aircraft the `--perf` per-pass cost is the same at 32 and at 64 to inside that
  instrument's noise, since only an aircraft inside 200 m and under 250 m of altitude casts at all.
  Re-posing the blur discs adds to that 0.32 ms only on an aircraft whose discs change the outline:
  0.05 ms per frame on the Hoplite, whose rotor is three wedges rather than a disc, and nothing
  measurable on a fixed-wing plane, whose main disc is a full circle about its own axis.
- The modulate lands on a **flat quad** at the probed ground height rather than on the world's own
  polygons, so it does not conform to a slope, and it is lifted clear by half a unit, a constant
  this page does not supply.
- The ground comes from one downward ray from the aircraft, so the original's own terrain-query bug
  (the plain maximum, which can snap a shadow to a surface above the aircraft) is not reproduced.
  Water and anything else that carries no collider answers nothing, and no shadow is drawn there.

## Open questions, and how to falsify this page

None of this has been checked against footage. No item owes the original-game A/B any more (the
port reads right at the controls), but the page keeps its specific predictions to shoot at:

1. The **player's own** shadow runs roughly 1.5 × its altitude AHEAD of the aircraft along the
   flight direction and grows to 3× its footprint by 250 units, while an AI aircraft's shadow stays
   tight and directly beneath it. A capture showing the player's shadow directly beneath the
   aircraft, or behind it, falsifies the skew term and it needs re-deriving.
2. Aircraft shadows vanish above 250 units, and other aircraft's shadows fade out past 200 units of
   horizontal distance from the player.
3. The Spruce Goose keeps its shadow to 750 units of altitude and 600 of distance.
4. Shadow darkness differs between missions, tracking `SUNLIGHT_AMBIENT`/`SUNLIGHT_DIFFUSE`.
