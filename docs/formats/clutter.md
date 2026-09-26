# Clutter: `interp.json` boot scripts & clutter templates

Part of the [format documentation](README.md). Covers how the original populates
forests/bushes (and city blocks) without a single placed tree node in the gamez.
Consumed by `CSVM/src/Mech3/Clutter.cs`.

**The decorations' own authored properties, substitution tables, scale ranges, fade distances,
are a separate file**, `templates.zrd`, keyed by decoration model rather than by template. See
[templates.md](templates.md).

**A second, independent clutter system uses the same template roots**: `fogvol.zrd` scatters
`cloudsprite1`/`cloudsprite2` through the world's `fvol*` fog volumes, with its own reader-side
weights and ranges rather than `AddClutterTemplates`. Template-root lookup is shared
(`ClutterBuilder.FindTemplateRoot`); everything else is separate, see [fogvol.md](fogvol.md).

## Boot scripts

`interp.zbd` (extracted by `unzbd cs interp`) is a JSON array of scripts
`{ "name": "support\\<chapter>\\adjust.gw", "lines": [ … ] }`, the engine's boot/setup
command lists. The chapter's `adjust.gw` script registers clutter templates via lines of
the form:

```
AddClutterTemplates terpat02 river1 river2
```

(one or more template names per line; C1 registers `terpat02`, `river1`, `river2`).
The same script loads the templates into the gamez as **parentless, unreferenced
subtrees**, which is why a placed-node walk of the world never sees them, and why
template roots must be looked up among parentless Object3d nodes by name (the world also
contains unrelated same-named leaf nodes).

## Template subtree shape

```
templateRoot (parentless Object3d, e.g. terpat02)
└─ ground node — first descendant with a mesh: a flat quad whose
   │             • texture names the terrain texture it decorates
   │             •   (terpat02.tif = C1's forest texture)
   │             • UV parameterisation is the DOMAIN each decoration's
   │             •   position is normalised against — never a metric
   │             •   spacing (see "no world grid" below)
   └─ decoration nodes — one local transform each (position within the patch),
      └─ decoration mesh, one level below: either a single vertical
         one-sided quad (tree/bush billboard, e.g. firtree1.tif) or a 3D
         building/vehicle mesh (e.g. cb02a.flt, resbuild11.flt, c_studebaker2.flt)
         └─ further meshes of the SAME decoration, on sibling and child nodes
            (`o2`, and a `g8` under an `l2` LOD whose range starts at 0):
            the rest of a city block, not an alternate of it
```

**Only the decoration node carries a transform.** Across every decoration of every
template in C2 and C5: 0 of 249 mesh nodes put a non-identity transform on the mesh node.
So one node's local transform is the whole placement.

**A decoration's geometry is not always one mesh.** 12 of the install's 175 template
decorations, all of them C5 city blocks (`cb04a`, `cb12a`, `cb13a`, `cb15a`, `cb16a`,
`cb21a`, `cb22a`, `cb23a`, `cb24a`), carry 15 further mesh nodes holding 88 polygons: a
street wall or two and a roof cap that the node reached first does not contain. C1 to C4
carry none. Every one of those further meshes sits in the first mesh's own frame (identity
to within 0.06°), and the `g8` ones hang under an `l2` LOD node whose range starts at 0,
which is the nearest level and is drawn, not a far silhouette. A consumer that renders the
first mesh under a decoration and stops leaves those blocks open on two sides.

**Authored orientation is identity, the variety is in the model list, not in rotation.**
Same survey: every 3D decoration's local basis is identity to within **0.108°**, and the
one node stored as a matrix rather than Euler angles is a 0.03° rotation. A city block
varies because the template names 17–28 *different* building models, not because it turns
them. (The remake keeps the authored basis anyway, it is the correct thing to consume,
and it costs nothing, but be aware that a screenshot cannot tell a correct implementation
from one that dropped the basis on this data.)

Decoration Y offsets are likewise near-zero: template ground quads sit at exactly `y = 0`
and building bases sit at `−0.02 … +3.67` relative to it.

The engine dresses **every world polygon textured with a template's ground texture** with that
template's decorations. The decorations are single one-sided cards, so the original must render
them as upright (Y-axis) billboards.

## Placement model

**undecoded**, and that UV-space placement was ruled out because "the world's UV tiling is wildly
non-uniform on hillsides, so it would visibly stretch the clutter". Both claims are dead.
Stretching with the UV is precisely what the original does.

`FUN_004dd230` stores each decoration's position as its **interpolated texture UV on the ground
quad**, wrapped into [0,1), which is why the quad is a domain and not a distance. `FUN_004dd6e0`
then stamps it **once per integer UV cell of each world triangle**: take the triangle's UV bounding
box, floor it to an integer lattice, test containment *in UV space*, and recover the world XYZ
through the triangle's own affine UV→world map. The clutter therefore rotates, mirrors and
stretches with the painted ground texture, and a building sits in its painted block wherever that
block lands.

There is **no world-space grid and no global clutter origin** in the original at all. The remake's
fixed X/Z grid was a fiction with no counterpart, and it cost C1 roughly 4× its trees, its terrain
is painted with `terpat02` at half the template quad's scale, so one repeat spans ~260 m where the
grid stepped 512. The measurements are in
`analysis/bl-305-clutter-uv/FINDINGS-A1.md` / `-A2.md`.

**Which polygons get dressed is a per-polygon decision**, made by the `no_clutter` flag (raw
polygon bit `0x800`, `FUN_004de2c0`). It does not mean "leave this ground bare": where two coplanar
layers are painted over each other it selects which one decorates. See `docs/architecture.md`'s
`src/Mech3/Clutter.cs` entry and [gamez.md](gamez.md).

**Sprite vs non-sprite is in the data, not the shape.** A decoration is a billboard card
iff its model carries `model_type: "Facade"`, the original engine's own billboard flag
(see [gamez.md](gamez.md) and the `FacadeMode` axis values). Every tree/bush/palm
decoration in the install is `Facade` + `CylindricalY`, one polygon, four vertices, flat
in local Z; every 3D building decoration is `model_type: "Default"` with 2–27 polygons.
**Do not classify on `facade_mode` alone**, the 3D building decorations carry a *stale*
`CylindricalY` in that field while being `Default`, so the type is the discriminator.

## Three-dimensional decorations

C2's `filmblock*` / `resblock*` / `parklot*` and C5's `cblock*` templates carry **3D
building meshes** as decorations, not sprite quads. They are placed by exactly the same
rule as the sprites, the same UV lattice, the same containment test, the same affine recovery of
the world position, and differ only in what is drawn and whether it is solid (a 3D decoration also
keeps its authored basis and Y, which a sprite drops). The remake uses this model (polish-3
item 6); before that they were skipped, which is why C2 and C5 rendered painted city-block
ground with nothing standing on it.

Counts are the remake's; a grid approach produced
10,261/37,167 and 71,326/124,072 respectively.

| chapter | templates | 3D decorations placed | sprites placed |
|---|---|---|---|
| C2 | `filmblock1-5`, `resblock1-6`, `parklot1-2` | 10,346 | 36,406 |
| C5 | `cblock1-7`, all registered and all placed (see below) | 67,836 | 110,668 |

`parkpat` (C2) and every other chapter's templates are sprite-only, so no other
chapter gains or loses anything. **C2B's `adjust.gw` registers six templates, `terpat01`,
`terpat03`, `terpat04`, `resblock2`, `filmblock1`, `filmblock2`, and its gamez ships not one of
those roots** (the original logs `ClutterLoadTemplates(): cannot find node for template %s` six
times). The boot script and the gamez disagree, C2B has always placed no clutter at all, and its
`templates.zrd` is consistently empty. That is retail data, not a bug in the reader.

**A block's variety comes from its model list.** `cblock1` names 28 3D decorations drawn
from 11 distinct models; each is stamped once per integer UV repeat of `cblock1.tif` across
every triangle painted with it.

**C5's `cblock4/5/6` are placed like every other district**, with no map-wide exemption removing
them. An exemption once stood there because the remake stamped both members of every coplanar
overlay/base pair, matching a template
to a polygon by texture name only, never reading the polygon's flag, which doubled the city's
buildings (the retired `analysis/bl-058-clutter-doubling/`,
`git show analysis-archive:analysis/bl-058-clutter-doubling/FINDINGS.md`). The real mechanism is the `no_clutter`
flag: flagged ground is dressed by `cblock4/5/6` (low-rise) and clear ground by `cblock1/2/3/7`
(towers), measured at odds ratio 1,037× and confirmed at the controls
(`analysis/bl-305-clutter-uv/FINDINGS-layer-pairing.md`). ⚠ Neither half works alone, the flag
gate with a district exemption still in force empties C5's downtown, because the ground there IS
the flagged layer and its replacement is the district such an exemption removes. ⚠ The building
sets are **not** disjoint by
name range: `cblock5/6`
also place `cb06a`/`cb11a`, and the *visible* `cblock7` places `cb12a`/`13a`/`14a`, census by
template root, never by `cbNNa` name range.

**`cbNNa` and `cbNNdet01` are co-located halves of one building, not a LOD pair.** Every
`det` decoration in `cblock1` sits at the *exact* same local origin as its `a` sibling, but
with a different footprint and a different texture family (`roof01`/`bldg*` for `a`,
`genbldg3`/`nosegun5` for `det`). Both are drawn. The `det` meshes are 40% of a block's
collision triangles, so an implementation looking to cut collision cost will be tempted to
drop them, note first that they are geometry, not decoration, and that "det" is a name
heuristic with nothing in the data behind it.

## Collision



> Trees are collidable in the original (`spruce_destroy` anims exist; destruction is
> weapons-era behavior).

**That was a misreading of the data.** The only two `spruce_destroy` strings in the whole
install are `..\data\common\zrdr\planes\spruce_destroy1.zrd` (C2/M01) and
`..\data\common\zrdr\planes\spruce_destroy2.zrd` (C5/M03), in the **`planes\`** directory.
Reading the files settles it: they define `g_engine*` animations with `prop_part`, `spin` /
`counterspin`, `snd_propstart` and engine puffers. This is the **Spruce Goose**, Howard
Hughes' flying boat and the C2/M01 mission object, whose folder siblings are
`sprucegoose-fly_the_goose`, `free_the_goose`, `goose_cooked` and `spruce_enginedest`.

**There is no spruce-*tree* animation, and no tree-destruction animation of any kind,
anywhere in the install.** The remake therefore gives clutter **sprites** no collider at
all, consistent with every other billboard, which is a flat
card whose collider would be a phantom wall wherever the card happens to be facing.

**The 3D decorations are the opposite case and DO collide** :
they are real multi-polygon geometry with real sides, they do not turn, and flying through
a skyscraper is not something the original permits.

**How they collide: one shared shape per model, attached per placement** (reworked
The first implementation transformed every
vertex of every placement into world space and merged the result into one trimesh per
1024 m region, 2,554,455 collision triangles in C5 and 202,303 in C2, built from about
2,300 and 1,400 distinct ones respectively. It was written that way to avoid ~80k
collision bodies, which is a real problem, but body count and shape count are not the same
thing: a `Shape3D` is shareable and can be attached to a body with its own transform and no
scene-tree node of its own. The remake now builds one shape per distinct decoration mesh
and attaches it once per placement, keeping the per-region body split only for broadphase
locality and for a locating name in the crash log.

That distinction matters to any reimplementation because **the cost was never in the
vertex arithmetic**. In C5: 3,796 ms to build the merged version, of
which only 271 ms was transforming those 2.55M vertices and **3,403 ms was building the
concave shapes' BVHs**. Sharing the shapes removed essentially all of it, the BVHs being
built are ~40 triangles each, and C5's `--fly` load fell from ~7,100 ms to ~3,550 ms.

Note that the geometry is unchanged: the same triangles, still concave, still
`BackfaceCollision`. Approximating a block with a convex hull or a box was considered and
rejected on the data, not on cost, `cbNNdet01` decorations enclose almost no volume
(measured fill fractions 0.003–0.05 of their bounding box) because they are open detail
shells, so a hull or box of one would be a phantom solid up to 62 m tall where the data
intends a facade.

The map-edge continuation carries the border tiles' clutter along, sprites and buildings
alike. Sprites stay pass-through there as everywhere; **the buildings are solid** since,
 attaching an existing shared shape at each mirrored placement costs one call
per building (measured: 0.5–0.6 ms for the ~3,900 buildings of a boundary-crossing
rebuild, inside a rebuild that already cost ~4.1 ms), where rebuilding a merged region
trimesh on that frame would have been a visible hitch. See
[world-structure.md](world-structure.md).

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
