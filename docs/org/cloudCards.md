# What the original applies to an `fvol` cloud sprite, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`). Every claim names the function or the shipped byte it
came from. No decompiler output is reproduced; the addresses are given so any claim can be
re-checked at source.

This page answers one question: **what does the original apply to a cloud sprite card between its
authored data and the pixel?** The gate that decides whether a card takes the mission sun, and what
the sun does to a lit one, is [`vertexLighting.md`](vertexLighting.md); the reader and the volumes
the cards are scattered through are [`../formats/fogvol.md`](../formats/fogvol.md); the fog colour
and the dome the faded field reveals are [`weather.md`](weather.md).

## The answer in one paragraph

**Nothing touches a cloud card's colour.** The card's authored per-vertex 240 reaches the Direct3D
vertex diffuse unchanged, the texture is a constant-RGB alpha mask, and the only per-card terms the
engine applies are alpha terms. The one that matters is a **draw distance scaled by the viewing
angle**: every sprite carries a pointer to the plane normal of the `fvol` polygon it was scattered
on, and its fade band is multiplied by the cosine between that normal and the direction from the
sprite to the eye. On a horizontal slab that makes the field a disc whose radius is the **geometric
mean of the camera's height above the cards and the sprite's own far distance**, so a camera 118 m
above the deck sees the field end at about 600 m while one 610 m above it sees 1,400 m. Each
sprite's fade band is also drawn per placement, interpolated between the reader's **two**
`far_fade_range` pairs rather than selected from them, so the field's edge is a population-wide
statistical ramp and never a rim.

## Function map

| Address | Role |
|---|---|
| `FUN_0044e010` | Loads `fogvol.zrd` under `srand(0x9b3a9ce2)`, hides every `fvol*` node, and pushes each `clutter` block into the block vector at `DAT_0064ff24`…`DAT_0064ff28` |
| `FUN_0044dc90` | Walks the `fvol` mesh **polygon by polygon** and calls the scatter once per polygon (once per strip triangle for a triangle strip) |
| `FUN_0044e870` | Builds that polygon's plane normal, transforms it to world space and interns it in a shared list (`DAT_0064ff14`); the sprite keeps a pointer to the interned copy |
| `FUN_0044c780` | The scatter: the staggered lattice, the weighted kind draw, the fade band, the perpendicular offset, the perturbation, the scale, and the instance the grid is handed |
| `FUN_0044c310` | Point-in-polygon test in the polygon's own plane, over its edges against its normal |
| `FUN_004db010` | Files the finished instance in the world's clutter grid cell |
| `FUN_0044c1c0` | The per-instance view-angle term: `max(0, dot(polygon normal, unit(eye - sprite)))` |
| `FUN_004d5de0` | The per-instance clutter draw: evaluates the fade, sets the global opacity around the draw, restores it after, and lends the template node this instance's own facade quaternion |
| `FUN_00553700` | The model-type switch at the head of the draw: `1` is a facade, and the mode word at model `+0x04` picks which pose routine runs |
| `FUN_00539390` | The `SphericalY` pose: the shortest-arc tracker, and the per-instance quaternion it accumulates |
| `FUN_0053fd40` | The shortest-arc quaternion between two unit vectors, and the antiparallel fallback |
| `FUN_00539040` | That fallback's arbitrary perpendicular |
| `FUN_005408e0` | The `CylindricalY` pose: a turn about the model's own Y by the camera heading less the model's |
| `FUN_004d6010` | The clutter quadtree walk, which culls a whole subtree on the **unscaled** band |
| `FUN_0054e0e0` | Writes the global opacity `DAT_00a06f98` (default 1.0) |
| `FUN_004d2120` | Writes the clutter distance factor `DAT_0062d170` |
| `FUN_00440750` | Turns the `EffectsLevel` setting into that factor: 1.0, 4.0 or 9.0 |
| `FUN_0043fb50` | Reads `detail.zrd`; `EffectsLevel_HW` is the key that reaches `FUN_00440750` on the hardware path |
| `0x006232c8` | The settings name table: `HIGH` 0, `MEDIUM` 1, `LOW` 2 |
| `FUN_00554550` | The hardware model draw: packs the vertex diffuse and the specular fog factor |
| `FUN_00552020` | The per-model mask the draw keys on (lighting, distance fog, band fog) |
| `FUN_005a0e00` | Device set-up: stage 0 colour op MODULATE of texture by diffuse |
| `0x005a6160` | The transparent-queue drain, which sets the blend states a card is drawn under |

## The pose: a shortest-arc tracker, and the camera's basis never enters it

A cloud card is a `Facade` model in `SphericalY` mode (`model_type` 1, the mode word at model
`+0x04` = 1; the census is in [`../formats/gamez.md`](../formats/gamez.md)). The draw reaches
`FUN_00553700`, which switches on those two words and hands a `SphericalY` model to `FUN_00539390`
before the geometry is submitted.

`FUN_00539390` does **not** build a basis out of the camera's. It keeps a **quaternion per card**,
a lazily allocated 16 bytes hanging off the node at `+0xc8`, seeded from the identity at
`DAT_006379a0` = `(1, 0, 0, 0)`. Each frame it rotates the reference facing `DAT_006379d0` =
`(0, 0, 1)` by the stored quaternion (`FUN_0053fb40`) to recover where the card is pointing now,
takes the direction from the card to the eye (read out of the modelview translation and brought
back out of view space by the 3x3 inverse `FUN_0053dfc0`, which is where the camera's own basis
cancels), builds the **shortest arc** between those two directions (`FUN_0053fd40`, the half-vector
construction), composes it onto the stored quaternion (`FUN_0053f920`), normalizes
(`FUN_0053f850`), stores it back, and turns it into the model's basis (`FUN_0053fa40`). Since the
arc is the minimal rotation from the card's own previous facing, nothing about the eye except its
**position** reaches the card, and rolling the aircraft leaves every card exactly where it was.

The per-card quaternion is per **instance**, not per template: the clutter draw `FUN_004d5de0`
swaps the instance's own slot at `+0x44` into the shared template node's `+0xc8` before the draw
and writes it back after, so ten thousand scattered cards each track their own facing through one
model.

⚠ **There is no up vector, so the pose has no zenith or nadir degeneracy.** A card looked at from
straight above is an ordinary shortest arc like any other, which is the opposite of what a
`lookAt(world up)` billboard does. The single degenerate input is the exact 180 degree reversal, an
eye directly behind the card's current facing: `FUN_0053fd40` finds `1 + dot` at zero, sets `w = 0`
and takes an arbitrary perpendicular from `FUN_00539040` (which returns `(1, 0, 0)` unless the
vector's own x is nonzero), giving a half turn about it. Reaching it requires the eye to cross the
card's facing exactly, and the frame after it the tracker is continuous again.

For contrast, a `CylindricalY` facade (`FUN_005408e0`, the tree and lamppost cards) is not a
tracker at all: it is a single yaw about the model's own Y by the camera heading
`*(float *)(DAT_009fddc4 + 0x3c)` less the model's own heading (`atan2` via `FUN_0053def0`),
applied by `FUN_0053ac80`. Its up is the model's Y by construction, and it likewise never sees the
camera's roll.

## The colour: the authored 240 and nothing else

`FUN_00554550` fills each vertex's work slot with a colour and then packs it into the Direct3D
diffuse as `(int)(c + 0.5)` clamped to 0..255. Which colour depends only on the submit record's own
lighting flags:

- **no lighting flags at all** (the state a `lighting: false` card is in, since `FUN_00552020`
  returns a zero mask for it): the authored per-vertex colour is **copied** into the work slot. The
  packed diffuse is exactly 240.
- **lighting flags set, per-vertex shading unavailable**: the authored colour is multiplied by one
  flat light triple taken from the first vertex.
- **lighting flags set, per-vertex shading available**: the authored colour is multiplied by that
  vertex's own accumulator, which is what [`vertexLighting.md`](vertexLighting.md) decodes.

There is no material-colour term on a textured polygon (the polygon's own colour triple is used
only when the material is untextured), no per-instance colour, and no distance term. The clutter
draw `FUN_004d5de0` writes a scalar opacity and never a colour.

⚠ **The cloud texture carries no colour variation, so a card has exactly one colour.** Read off the
shipped `cloud1.png`/`cloud2.png` in every tier of C1's texture archives: **every texel is RGB 239**
except the fully transparent ones, which are RGB 0, and the alpha channel carries the whole image
(maximum 254, so no texel is ever fully opaque). The card is an alpha mask on a flat grey. Its
rendered ceiling is therefore one number, `239 x 240/255 = 224.9` in the framebuffer's own space,
and **any brightness a frame shows below that is accumulated alpha over whatever is behind, not a
colour**. A brightness gap on this population cannot be closed by a colour scale.

## The alpha: three terms, one of them the whole far-field story

A card's Direct3D vertex alpha is `0xff` unless the global opacity `DAT_00a06f98` is below 1.0, in
which case it is that opacity times 255, clamped. The fog factor rides the **specular** alpha
instead (`255 - fog x 255`), and `FUN_00552020` only sets the fog mask bits from the model's own
flag word, so a card authored `fog: false` is submitted with a specular alpha of 255 and takes no
fog at any distance. The texture's own alpha is the third term, applied by the device's stage 0
alpha op.

The transparent-queue drain at `0x005a6160` sets, per polygon: shade mode Gouraud, alpha blending
on, z-write off, the stage 0 alpha op to MODULATE (or the material's own stored op when the first
vertex's diffuse is not opaque), and the destination blend to INVSRCALPHA, or to ONE for a polygon
flagged additive. So a card composites the ordinary way, in the framebuffer's own space.

### The fade band is drawn per sprite, not selected per detail level

`FUN_0044c780` draws **one** random `t` per placement and interpolates **both** `far_fade_range`
pairs with it:

```
near = near0 + t * (near1 - near0)
far  = far0  + t * (far1  - far0 )
```

and stores `near^2`, `far^2` and `1/(far^2 - near^2)` in the instance at `+0x34`, `+0x38` and
`+0x3c`. For C1's authored `[[2000, 3000], [3100, 3500]]` that gives a sprite whose fade starts
anywhere in 2,000 to 3,100 m and ends anywhere in 3,000 to 3,500 m, the two correlated through the
one `t`. The pair is **not** a per-detail-level choice and the engine never picks one of the two.

### The view-angle term

Each instance carries the address of a function at `+0x58` and a pointer at `+0x5c`. The scatter
sets them to `FUN_0044c1c0` and to the interned world-space normal of the `fvol` polygon the sprite
was scattered on. `FUN_0044c1c0` returns

```
c = max(0, dot(n, unit(eye - p)))
```

and `FUN_004d5de0` uses its square, with `S = c * c`, `D = DAT_0062d170 * |eye - p|^2`:

| Condition | Result |
|---|---|
| `S * near^2 >= D` | opacity 1, and the global is left alone because the draw skips it above 0.996 |
| `S * far^2 <= D` | the sprite is **not drawn at all**, which includes every sprite with `c = 0` |
| between | `opacity = (far^2 - D/S) / (far^2 - near^2)`, dropped entirely below 0.004 |

On a horizontal polygon (a slab's top face, normal `+Y`) and a camera `dy` above the sprite,
`c = dy / d`, so the conditions collapse to distances in closed form:

```
fully opaque out to   d = sqrt(dy * near) / k^(1/4)
gone past             d = sqrt(dy * far ) / k^(1/4)
```

with `k = DAT_0062d170`. The field is a disc whose radius is the geometric mean of the camera's
height above the cards and the sprite's own far distance, and a sprite in the polygon's own plane or
on its far side is dropped. C1's cards sit at their slab's 1,090.55 m top plus the reader's
`perp_dist_range`, so at `k = 1`:

| Camera altitude | `dy` | opaque out to | gone past |
|---|---|---|---|
| 1,208 m | 118 m | 486 m | 595 to 643 m |
| 1,219 m | 129 m | 508 m | 622 to 672 m |
| 1,698 m | 608 m | 1,103 m | 1,350 to 1,459 m |

The band in the last column is the spread the per-sprite `t` opens, and the perpendicular offset and
the sprite's own position spread it further. That is what makes the field's far edge a deep
statistical ramp rather than an edge: at a grazing pose the population thins out over hundreds of
metres, and past it the surface the frame shows is the fogged deck sheet and the dome.

### The same term over C5's street prisms

C5 authors no slab, so the closed form above lands somewhere else. Its seventeen volumes are 646 m
prisms with their floor at −463 m and a flat top at 183 m, and the upward skin the scatter sees is
that one top face plus, on fifteen of the seventeen, one to five sloped rim faces (29 in all)
climbing from 37 m to the 183 m top at about 37° off the horizontal. 17,095 of the chapter's 19,197
sprites sit on the tops and 2,102 on the rims. With `perp_dist_range` −5 to 10 m and up to 10 m of
perturbation the field is one sheet at about 185 m over the streets, fringed down the rims to about
40 m, and the authored band `[[1000, 1500], [1200, 1800]]` gives each sprite a `far` of 1,500 to
1,800 m. Three readings follow, each measured by painting `cloud1.tif`/`cloud2.tif` flat red under
`--no-fog` and counting the strongly reddened pixels of a 1,280 × 720 freecam frame, which is a
lower bound on what the field covers:

- **Under the sheet a flat top never draws at all.** The eye is below the cards' own plane there, so
  `c` is 0 and the sprite is dropped before its band is consulted. Over the middle of a prism at
  20 m, 100 m and 180 m, looking up as well as down, the field paints 0 pixels, and it stays 0 with
  the city clutter off and with the zone gate off, so neither occlusion nor the gate is what removes
  it.
- **A rim face is what a street-level eye can see.** Its plane tilts about 37°, so a camera outboard
  of the rim's foot stands above that plane and its cards pass the test: 250 m out over the water at
  20 m altitude, one `fvol9` rim paints 339,489 pixels of 921,600, the same count with the clutter
  off.
- **Above the sheet the field is a disc** of radius `sqrt((eye − 185) × far)`, which grows with
  altitude and then closes: once the eye is more than `far` above the cards, no sprite can satisfy
  `d < sqrt(dy × far)`, since `d` is never less than `dy`. Straight down over a prism the field
  thins from 872,278 pixels at 400 m to 112,289 at 1,700 m, 41,775 at 1,800 m, 9,882 at 1,900 m and
  0 from 2,000 m up, against the 183 + 1,800 m ceiling the band predicts.

A C5 pose that shows no cards is therefore the law working rather than a fault: a street-level camera
away from a rim, and any camera further from the nearest prism than that altitude's disc radius, both
read zero. C5's `fvol` nodes author `zone_id` 1, so the field also rides the zone-1 visual layer and a
camera inside a prism (weather state 3) loses it, which costs nothing measurable because the angle
term has already dropped every card such an eye could reach.

### The distance factor, and the one setting that moves it

`DAT_0062d170` multiplies the squared distance, so it divides the field's radius by its square root:
1.0 leaves the authored metres alone, 4.0 halves the radius, 9.0 thirds it. `FUN_00440750` writes it
from the `EffectsLevel` setting, which on the hardware path is `detail.zrd`'s `EffectsLevel_HW`:
`HIGH` when `CPU_MHZ >= 600`, `MEDIUM` at 400, `LOW` below. `HIGH` resolves to 0 through the name
table at `0x006232c8`, and 0 is the case that writes 1.0. **Every retail capture this project
measures against is `HIGH`, so the authored metres apply as they stand**; the other two settings
shrink the whole field and are not a faithful target.

The clutter quadtree walk `FUN_004d6010` applies the same squared-distance test to a subtree's
bounding sphere **without** the view-angle term, using the block's own band. It is a conservative
early-out, not a second rule.

## The scatter, which is where the normal comes from

`FUN_0044dc90` iterates the `fvol` mesh's polygons, skipping any polygon flagged `0x800` and
splitting a triangle strip (`0x2000`) into its triangles, and calls `FUN_0044c780` for each. So the
field is scattered **over the volume mesh's faces**, not through its interior. Per polygon:

- the basis is the polygon's own: `U = unit(v1 - v0)`, `V = cross(U, n)`, and the loop runs over the
  polygon's vertices projected onto the two;
- the steps are `distance * sqrt(3) / 2` along `U` and `distance` along `V`, each extent divided
  into a whole number of steps, and **every other row is offset by half a step**, which is a
  staggered lattice rather than a square one. In full (`0x0044c9b3`..`0x0044ca3e`): an extent
  wider than its nominal step takes `step = extent / ftol(extent / nominal + 0.5)`, a narrower one
  is its own single step; rows start half a row step in from the minimum and run while they stay
  at or below the maximum, columns start at the minimum (plus half a column step on odd rows);
- the kind is drawn by weight twice, once over the blocks and once over a block's nodes, and then
  the fade band;
- the point is displaced **along `unit(p - ref)`** by a random draw over `perp_dist_range`,
  where `ref` is a per-volume reference point the caller passes from the volume record's `+0x64`
  (untraced beyond that; for a slab's top face the direction it gives is up near the middle and
  increasingly outward towards the rim);
- it is then perturbed by **one** draw `m` over `perturb_dist_range` (`0x0044cde0`..`0x0044cdf9`,
  the block's `+0x24`/`+0x28`) and one draw per axis, `p += (rand() · 3.051851e-05 − 0.5) · m`
  (`0x0044cdfc`..`0x0044ce50`, the constants at `0x00603598` and `0x006032e0` = 0.5), so each axis
  moves uniformly over `[−m/2, m/2]`. On C1 and C1C (`distance` 130, `perturb_dist_range`
  `[10, 20]`) that is at most ±10 m per axis against a 130 m column step, so the original's own
  lattice is regular to within 8 % of a step;
- the scale is one uniform random draw over `scale_range`, applied to all three axes;
- only then is the final, displaced and perturbed point tested by `FUN_0044c310` against the
  polygon's own outline (`0x0044ce5e`..`0x0044ce87`, the argument is the perturbed point), and the
  placement skipped when it falls outside. Every draw above is taken for every lattice cell,
  accepted or not.

Every `rand()` above runs inside the fixed-seed window `FUN_0044e010` opens, so the whole field is
the same every launch.

⚠ **The `0x800` skip is what shapes the field, not a detail of the walk.** Every shipped `fvol`
mesh is a CLOSED box or prism, and its walls and floor carry that bit, so the polygons the scatter
actually sees are the upward skin alone: one flat top quad per deck slab piece, the four sloping
sides of a C1C build-up, the tops and ramps of a C5 street prism. The per-chapter census is in
[`../formats/fogvol.md`](../formats/fogvol.md). A walk that ignored the bit would scatter the
underside and the walls too and place several times the field.

## Where CSVM stands

`FogVolumeClutter` and its generated card shader carry the pose, the colour path and the alpha path
above.

- **The pose is a world-up look-at standing in for the tracker.** `csky_facade_spherical`
  in [`../../CSVM/shaders/csky_facade.gdshaderinc`](../../CSVM/shaders/csky_facade.gdshaderinc)
  points the card's `+Z` at the eye and takes its `+Y` as the world's up projected off that line,
  and every `SphericalY` population takes it: the `fvol` deck cards, the `cloudparent` facades,
  the stamped clutter glows, and the glow sprites that share their dispatch. The eye's basis is
  not read, so the camera's roll cannot reach a card, which is the property the decode above turns
  on, and the up hint is constant, so translating past a card cannot roll it either. A shader
  holds no state, so the tracker itself is not reproduced; each of its steps is twist-free and
  keeps the roll a card started with, so away from the pole this look-at is the pose it settles
  into. ⚠ A closed-form shortest arc from a fixed axis is not that pose: its twist depends on
  where the eye stands, and sliding sideways past a card that lies behind the axis rolls it
  through most of a half turn. The one direction this look-at is degenerate in is the eye straight
  above or below a card, where the up hint falls back to the world's `+Z`; a round puff seen face
  on hides the roll it takes there.
- **The colour is the authored 240, unscaled, and the only thing that ever multiplies it is the
  original's own per-vertex directional term.** A chapter authoring its card `lighting: true` (C1C,
  C2B and C5) takes `AMBIENT + DIFFUSE · max(N·L, 0)` per corner on the card's three authored
  normals, turned by the same facade basis the quad takes, clamped after the multiply the way the
  original clamps it ([`vertexLighting.md`](vertexLighting.md)); a chapter authoring it false (C1,
  C4) reaches the shader as the data authors it. There is no other colour term and no brightness
  constant: a brightness gap on this population is read as coverage rather than corrected as a
  colour.
- **The fade band is drawn per sprite.** The scatter draws one `t` per placement and hands it to
  the shader as instance custom data; the shader interpolates both authored `far_fade_range` pairs
  with it and ramps linearly in squared distance, not as a smoothstep.
- **The scatter runs over the authored faces**, the walk above: the `fvol` mesh's polygons, the
  `0x800`-flagged ones skipped and triangle strips split, each carrying the staggered lattice in
  its own plane on `U = unit(v1 − v0)`, `V = cross(U, n)`, with every point tested against that
  polygon's outline. So a placement sits ON a face and the normal it carries is that face's own,
  for every volume shape, not only for a slab.
- **The view-angle term is applied.** Each placement also carries the outward normal it is scaled
  against, packed into the same custom-data slot, and the shader evaluates `c = max(0, dot(n,
  unit(eye − p)))` and the `c²·near²` / `c²·far²` tests above. `DAT_0062d170` is the same global
  the templates clutter already takes from the graphics `EffectsLevel`, so `HIGH` leaves the
  authored metres literal and the two clutter populations cannot drift apart.
- **The card takes no fog in either build**, which is the one place the two agree by construction.

⚠ **The port lays the lattice with two known differences, neither of which bears on rows.** It
steps the nominal `distance · sqrt(3)/2` by `distance` from the face's minimum corner rather than
the whole-number fit above with its half-step row start, and it tests the undisplaced lattice point
against the outline before drawing, where the original tests the perturbed point after every draw.
The perturbation itself is the original's to the constant: one magnitude, then `[−m/2, m/2]` per
axis. Fixing either difference moves the placed field and every cloud golden, and is a separate
change.

**The shipped field is the decoded lattice plus a remake-only 30 m offset** (`--cloud-jitter=<m>`,
default 30, `FogVolumeClutter.ShippedJitter`). Because the original's own ±10 m on a 130 m lattice
leaves its rows standing, a regular lattice seen along the deck reads as rows in either build. At
30 m the rows stop reading along the C1 and C1C decks at the controls, which is what sets the
value. The offset is uniform on X/Z, up to `m` metres per axis, applied to each lattice card after
every decoded draw off its own `cloudjitter` stream, so every value of the knob lays the same seeded
field; the map-edge ring, whose cards are already uniform in their cells, is left alone.
`--cloud-jitter=0` renders the decoded lattice itself, which is the control a cloud render is
differenced against.

⚠ **Two quantities are still inferred.** The per-volume reference point the perpendicular offset
runs away from is taken as the volume's own bounds centre, which gives the direction the decode
describes on a slab's top face (up in the middle, tilting outward at the rim) but is not traced to
the record field. And a face's outward sense is decided by the volume's vertex centroid rather than
by the polygon's winding, because a mesh's winding convention is not guaranteed here.

⚠ **The field is culled, not merely faded, wherever the angle closes.** A sprite whose face points
away from the eye is dropped outright, which is the original's own rule and is why a slab's field
disappears from below rather than fading out. No shipped `fvol` polygon faces downward or
sideways, so this is a property of the whole install and not of the deck chapters alone. The
map-edge continuation's ring is still bounded at the largest authored `far`, which stays
conservative: the disc's horizontal reach `sqrt(dy·far − dy²)` peaks at `far/2`.
