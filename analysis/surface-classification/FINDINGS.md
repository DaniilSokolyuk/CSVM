# Why some C2 buildings were water and some water was a building — and the fix

`SceneBuilder.SurfaceForMesh` tags each collidable mesh `water`, `buildings` or null (terrain) from
its polygons' material textures. `ColliderOverlay` colours by that tag, and `Projectile` picks the
impact sound and effect from it — the same metadata, so a mis-tag is not an overlay bug, it changes
what the player hears and sees when they shoot the thing.

`census.py` replicates the shipped classifier over a chapter's extracted gamez and compares it
against the count vote it replaced.

## The original mechanism (the bug)

The old vote counted polygons per tag and **skipped unclassified polygons entirely**, so it was not
a majority of the mesh — it was a majority of whatever happened to match a name pattern. A mesh
whose texture was overwhelmingly unrecognised was tagged by however few stray polygons did match,
with no threshold and no notion of "mostly unclassified means unclassified". The thinnest votes
were single polygons: mesh 33 was tagged **water on 1 of 76 polygons (1.3 %)** — it turned out to
be `puffertexture`, the fire/effect atlas mesh, "water" via its one `splash` polygon (C1, C2 and
C4 all carry it, so it was shared geometry rather than one bad map).

## The shipped rule (2026-07-30)

**Area-weighted vote with a quorum**: each polygon contributes its triangulated area (strip order
for tri_strips, a fan otherwise — matching `EmitPolygon`; a strip's raw index list is not an
outline) to its texture's class, unclassified polygons abstain but still count toward the
whole, and the winning tag must cover **at least half the mesh's total surface area** or the mesh
stays null/terrain. Area rather than count because area is the static analogue of what
`--tex-census` measures — which texture actually covers the surface — and because it keeps real
buildings a count quorum would drop: C1 mesh 433 (a terminal) is `buildings` on 78 % of its area
but only 16 % of its polygons (few large hangar walls, many small trim polys).

## Measured, before → after

| chapter | meshes | old count vote | tagged on a MINORITY of own polygons | shipped area quorum |
|---|---|---|---|---|
| C1 | 2237 | 109 (51 water, 58 buildings) | **42** | 75 (37 water, 38 buildings) |
| C2 | 1765 | 190 (79 water, 111 buildings) | **96 — 51 % of everything tagged** | 89 (57 water, 32 buildings) |
| C4 | 2431 | 106 (13 water, 93 buildings) | **70** | 39 (11 water, 28 buildings) |

Minority tags are 0 by construction under the quorum, so the evidence is in *what* got
reclassified. Everything dropped to default is a structure or shoreline the old rule mis-tagged:
the `puffertexture` fire mesh (was water), beach/cliff shoreline tiles with a couple of `wtr00000`
polys (was water), the wooden docks and the boardwalk carrying `building4`/`sign_awning` textures
(was water — the "buildings drawn in the water colour" sighting), and cranes, tugboats, water
towers, oil pipes and bridges each tagged by a single `hangar*`/`bld*` poly (was buildings).
Everything kept is the open-water tiles and the real building sets (C2's `nycity` film set wins on
98 %+ of its area).

In-engine confirmation (scripted `--det` dives in C2, `Projectile` impact-log breadcrumbs): open
water tile `g29239` → 8/8 `Water` + `splash1.flt` instanced; `nycity` → 8/8 `Buildings`; the
reclassified dock `g36347` → 8/8 `Default`, no splash.

**Known residual, accepted:** a handful of small huts/houses whose wall textures match no pattern
(`houseside1`, `thatched_wall`) and whose only classified polys are a `*roof*` texture now fall to
default — a plain ricochet instead of the buildings binding. Widening the name patterns is the
documented trap (measure with `--tex-census` first); revisit only if a playtest actually hears it.

## Two candidate fixes that were checked and rejected

**The `soil` field is not the answer.** Every `Textured` material carries one, which looks like the
original's own surface classification — but it is almost entirely `Default`: C2 has exactly **one**
`Water` material out of 484, C1 three of 548. The other values (`Mech`, `NoSlip`, `Silt`) are
MechWarrior 3 soil types inherited from mech3ax's origins, not Crimson Skies surfaces. Reading
`soil` instead of texture names would classify essentially nothing.

**The design document does not describe the mechanism.** It confirms the system's *shape* — impact
effect and sound are chosen by what was hit, water gets a rising column and a splash, everything
else ricochets, with `splashsm`/`splashbg` and `ricco1`–`ricco4` named in its sound table — but says
nothing about how the engine determined the surface. Searched for collision-mesh, terrain-database,
per-polygon-flag and material-to-sound wording; nothing. Per the standing trust rule it is
authoritative for shape and never for mechanism, so this is a dead end, not a gap to re-search.

## 2026-07-31 — the area quorum's own bug: a real water polygon can still lose the mesh it's on (`BL-204`)

PT-05 (2026-07-31): C2's turquoise (open) water splashed, its blue (near-shore) water didn't — read
at first as a `BL-041` name-pattern gap (some water texture never matching `classify()`). It wasn't:
every water texture C2 actually uses (`wtr00000`, `srf0001`, `watersquirt`) already matches, and no
other texture family is used for water anywhere in the chapter — extending the patterns would
reclassify nothing, a **measured disproof**, not a finding to build on.

The real mechanism is the area quorum itself. It answers "what does this WHOLE MESH count as", and
a coastal tile is mostly beach/cliff/dock by area with only a fringe of real, non-trivial water
polygons — so the tile loses the vote outright and every polygon on it, water included, reads
`default`. This is structurally the same failure the quorum was built to fix (`BL-041`'s single-vote
mesh 33), just from the other direction: instead of a stray sliver dragging a whole mesh's tag UP to
`water`, a real water area gets dragged DOWN to `default` because the mesh it happens to share with
land is mostly not water.

**Measured, stranded area under the whole-mesh vote** (`census.py`'s per-polygon-split section —
every polygon's own texture area vs. what the area-quorum vote actually reaches):

| chapter | water total area | quorum reached | stranded | buildings total area | quorum reached | stranded |
|---|---|---|---|---|---|---|
| C1 | 37,317,186 | 35,406,462 | 5.1% | 1,041,637 | 903,798 | 13.2% |
| C2 | 54,611,451 | 50,305,379 | 7.9% | 259,537 | 91,785 | 64.6% |
| C4 | 3,801,845 | 3,442,421 | 9.5% | 173,081 | 23,092 | 86.7% |

Water's stranded fraction is the C2 symptom (7.9%, real and player-visible along the coast);
buildings' is far larger in relative terms (up to 86.7% in C4) because small building clusters
sharing a mesh with open terrain are exactly the shape that loses a whole-mesh vote — this was
latent in every install chapter, not something `BL-204`'s report singled out.

**The fix drops the vote, not the classifier.** `ClassifySurface` (the name-pattern function) is
unchanged. `SceneBuilder.CollidersForMesh` now builds one collider PER SURFACE CLASS actually
present in a mesh — each polygon's own texture decides which trimesh it joins — instead of forcing
the whole mesh under one winning tag. A polygon with a merely name-matching but literally zero-area
texture reference (mesh 33's stray `splash` poly, area 0.5 of 38) still contributes nothing, because
its own triangulated area is what a collider is built from — no separate area threshold was needed
to keep that case fixed.

In-engine confirmation (C2, `--collision=show` overlay, `--det`): water colliders 75→104,
buildings colliders 35→80, and the coastal fringe that used to draw as plain unclassified terrain
now draws in the water/buildings wireframe colour matching what it visually is. `RunTests.ps1`
green throughout (312 units, 12/12 suites, 13/13 goldens hash-identical — collision never touches
a rendered pixel).

## 2026-08-01 — the film-set skyscrapers never classified `buildings` (`BL-203` buildings half)

A square-on `--det` fire probe at C2's `nycity` towers logged `-> Default` on `nycity/col`: the
landmark tower walls texture as `empire1`/`chrysler1`/`chrysler2`, which match no name pattern, so
under the per-polygon split their polygons join the default trimesh — and a gun hit there showed
the dirt stand-in, not the buildings binding. Measured before widening (the `BL-041` gate): those
are the **only 3 matching textures install-wide** (C2 + C5, nothing else contains either
substring), moving one model per chapter — C2 `nycity` 22,694 area units (empire1 15,344 +
chrysler2 6,221 + chrysler1 1,129), C5 53,652 (chrysler2 45,996 + chrysler1 7,656) — from default
to `buildings`; nothing else can drift. `classify()` here and `SceneBuilder.ClassifySurface` both
gained `empire*`/`chrysler*`. Post-change the same probe logs 8/8 `-> Buildings` on
`nycity/col_buildings`. Still-unclassified residual on `nycity`, accepted as before: `tankerdeck`
(a ship deck), `aphagar01/05`, `woodsupport*`, `oldroad1`, signage (~18 k area total).

## 2026-08-01 — how much of each chapter is `buildings` at all (`BL-019` disproof)

`class_area_share.py <CHAPTER…>` runs the shipped classifier over every polygon of a chapter's
extracted gamez and reports polygon counts, triangulated area share per class, and the building
texture histogram. It answers the question a "buildings and dirt look identical" report raises
first: **is the buildings class reachable in the map you were flying over?**

| | C1 | C1B | C1C | C2 | C2B | C3 | C4 | C5 |
|---|---|---|---|---|---|---|---|---|
| `buildings` polys | 1197 | 99 | **0** | 490 | **0** | 332 | 558 | 4238 |
| `buildings` area share | 0.07 % | 0.00 % | — | 0.07 % | — | 0.00 % | 0.01 % | 5.85 % |
| `water` area share | 1.99 % | 4.16 % | 8.56 % | 6.33 % | 8.25 % | 32.55 % | 0.18 % | 2.46 % |

**C1C and C2B carry no building-classed geometry at all** — open-water/mountain maps — and
everywhere but C5 the class is well under a percent of the collidable surface. So a probe that
sprays rockets at random terrain and reports "always `Default`" has measured the map, not the
classifier. Aim at named geometry: C1's `g306` hangar wall at ≈ `(-4258, 172, -6405)` logs
`-> Buildings` on `g306/col_buildings` every time.

⚠ **The material `soil` enum is not this classification.** `materials.json` carries a per-material
`soil` (`Default`/`Grass`/`Water`/`Silt`/`NoSlip`/`Fire`/`Mech`): it has no `buildings` value at
all, and its `Water` count is 1–3 materials per chapter against the hundreds of water polygons the
texture-name rule finds. Do not re-chase it as the "real" source **of the `water`/`buildings` impact
class** — that part stands. But the 2026-08-11 section below retires the "MechWarrior-3 leftover"
half of this note: the field is not junk, it is the original engine's own surface type id, and it
governs a different decision (which crash/touchdown choreography plays).

## 2026-08-11 — the `soil` field is the engine's surface type id, and it picks the crash def

Decoded out of `crimson.exe` (read-only ghidra-mcp session) and then confirmed against the shipped
data. Two separate claims; both are settled.

**1. The crash choreography is a table lookup on the material's surface id, not a three-way branch.**
`FUN_00476250` (plane setup, gated on the anim node being named `player`) builds a vector by
concatenating the literal `"player_crash_"` (`0x00627cf8`) with every name in a global surface-name
registry — count at `0x00637b10`, `char*` array at `0x00637b14` — so `vector[i]` is
`player_crash_<registry name i>`. `FUN_004735b0` does the identical thing with `"touchdown_"`
(`0x006274ec`) into `DAT_0071c2e8`. Six names are compiled in:

| id | 0 | 1 | 2 | 3 | 4 | 5 |
|---|---|---|---|---|---|---|
| name | `default` | `water` | `seafloor` | `quicksand` | `lava` | `fire` |

`FUN_0055b7b0` appends more from level data into slots 6–99 (cap tested at `0x0055b8b6`);
`FUN_0055aa90` resets the count to 6. **`dirt` is not a compiled-in name** — it does not appear as a
standalone token anywhere in `crimson.exe`.

Selection is `FUN_0048b920` at `0x0048bac5`–`0x0048bb00`. Given the impact record, take the struck
material `*(record+0x20)` and its surface id `*(material+0x20)`, then:

- material null, **or** `id < 0`, **or** `id >= vector length`, **or** `vector[id]` empty
  → **`vector[0]`, i.e. `player_crash_default`**;
- vector empty or `vector[0]` empty → the bare anim name `player` (`0x00627cf0`);
- otherwise → `vector[id]`.

The id is a **signed dword** — `MOV EDX,[EAX+0x20]` at `0x0048bab0` with no sign-extension, `JL`
against zero at `0x0048bab5`, an *unsigned* upper-bound `JNC` at `0x0048bad2`, and
`MOV EDI,[ECX+EDX*4]` at `0x0048bada`. The touchdown handler `FUN_0048d2c0` repeats the same test
byte-for-byte at `0x0048d425`–`0x0048d460`.

**So `player_crash_default` is the fallback arm, not an "air/no-impact" variant.** It is what plays
for any material whose id is 0, negative, out of range, or names a def that does not exist.

**2. That id is the `soil` field mech3ax already extracts.** The material record is `0x2c` bytes and
`FUN_00559e20` (the GameZ material buffer reader) **bulk-`fread`s the whole block** with no
per-field parse; the fixup pass afterwards touches only `+0x02`/`+0x10` (colour / texture handle)
and the `+0x24` cycle block. `+0x20` arrives from disk untouched — the last dword of the 9-dword
payload `FUN_0055b310` copies. Corroborated inside the engine by `FUN_004e5590`, which builds the
quicksand surface material and writes the literal `3` into it via `FUN_0055b0a0` — registry index 3
is `quicksand`.

`soil_id_probe.py` proves it from the data end. It locates the material block in the raw
`gamez.zbd` by a stride-`0x2c` signature (a run of `Textured` materials' `texture_index` as dwords
at `+0x10`), which yields **exactly one candidate offset per chapter**, then tabulates the dword at
`+0x20` against mech3ax's label. Across all 8 chapters / 4,715 materials the mapping is 1:1 and
identical everywhere — **no label ever took two different ids**:

| label | `Default` | `Water` | `Fire` | `Grass` | `Mech` | `Silt` | `NoSlip` |
|---|---|---|---|---|---|---|---|
| id | 0 | 1 | 5 | 8 | 11 | 12 | 13 |

⚠ **mech3ax's `Dirt` is NOT Crimson Skies' `dirt`.** The fork's `Soil` enum
(`tools/mech3ax/crates/api-types/src/gamez/materials.rs:14-30`) is a complete, explicitly-numbered
`u32` covering 0–13, so the extraction is **lossless** — the label is a faithful encoding of the raw
dword, which is why the cross-tab below is 1:1 with no gaps. But the *names* are MechWarrior 3's:
mech3ax calls id 6 `Dirt` and id 7 `Mud`, while Crimson Skies calls 6 `player`, 7 `enemy`, and its
real `dirt` is **13** (mech3ax `NoSlip`). Anyone who opens `materials.json`, sees `"soil": "Dirt"`
and reasons about the dirt crash gets the wrong material entirely. Nothing in the shipped data uses
id 6, so this cannot bite at runtime — it bites a *reader*. Deliberately not renamed (2026-08-11):
renaming forces a full 8-chapter re-extract and invalidates every doc, script and table that says
`NoSlip`/`Silt`/`Grass`, to buy legibility this warning buys for free. Always convert label → id
through the table below before reasoning about a surface.

Reading that table: `Default`/`Water`/`Fire` are the engine's own `default`/`water`/`fire` at
0/1/5. **Slots 2/3/4 (`seafloor`, `quicksand`, `lava`) are used by no material in any shipped
chapter.** Ids 8/11/12/13 are level-supplied slots, and **mech3ax's names for those are wrong** —
`Grass`/`Mech`/`Silt`/`NoSlip` are MechWarrior 3 labels applied to whatever Crimson Skies registered
in that slot. The ids are real; those four labels are not. The real names are below.

**3. The level-supplied names, recovered.** Names ≥ 6 are appended by `FUN_0055b7b0`, whose sole
caller is the script command **`LoadSoils <filename>`** (`FUN_005b80a0`, call at `0x005ba4ae`; key
literal `0x0063ec54`, alongside `MakeShadows`/`LoadGame`/`LODSetRange` in the same dispatch table).
The filename is not in the executable — it is script data — so the list has to be read out of the
shipped install. It is in the **global `ZBD/zrdr.zbd` at `0xe631c`**, eight names, contiguous:

`player`, `enemy`, `airstrip`, `opensesame`, `death`, `buildings`, `dzone`, `dirt`

The append rule is a linear scan that appends only absent names, so ids run 6,7,8,… in file order.
(The match test is `_strnicmp` over `strlen(registryName)` accepting a trailing NUL *or digit* at
`0x0055b874` — so `water2` would collapse onto `water`. No shipped name hits that case.)
`soils_list.py` reproduces the derivation:

| id | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| name | `default` | `water` | `seafloor` | `quicksand` | `lava` | `fire` | `player` | `enemy` | `airstrip` | `opensesame` | `death` | `buildings` | `dzone` | `dirt` |
| used by materials | ✓ | ✓ | | | | ✓ | | | ✓ | | | ✓ | ✓ | ✓ |

**The eight names fill exactly slots 6–13, and 13 is the highest id any shipped material carries** —
the list and the measurement close on each other with nothing left over. Three independent checks
agree: `dzone` lands at 12, and id 12 is carried by **exactly one material in every chapter that has
one** (a danger-zone marker — and `crimson.exe` caches `dzone`'s id at `DAT_0064fb70`, `FUN_004459f0`
`0x00445a16`); `buildings` lands at 11 and is also a weapon `IMPACT` key; `player`/`enemy` land at
6/7 and are carried by no world material at all.

**So `dirt` = 13**, and `player_crash_dirt` is reachable — for the 3–9 materials per chapter tagged
with it, and nothing else.

⚠ **The original's ordinary ground crash is `player_crash_default`, not `_dirt`.** Ids 5, 8, 11 and
12 (`fire`, `airstrip`, `buildings`, `dzone`) name a `player_crash_<name>` def that does not exist,
so by the cascade they fall back to slot 0 — and so does id 0, which is 98 % of every chapter's
materials. `_dirt` is the *exception*, played only where a material is explicitly `dirt`-tagged. Our
build has this backwards: it plays the `_dirt` variant as the default ground crash.

⚠ **Do not hardcode 13 for `dirt` without its provenance.** Ids ≥ 6 are script-data-dependent; the
exe fixes nothing above 5. This list comes from the shipped `zrdr.zbd`, and holds for this install.

⚠ **The weapon `IMPACT` table shares this index space** — `FUN_005ad630` at `0x005ae216`–`0x005ae29b`
walks the same registry, parsing one 100-byte row per surface id into `weapon+0x15c`, and an absent
key **inherits the `default` row wholesale** (`0x005ae268`, `MOV ECX,0x19` + `REP MOVSD`). So IMPACT
keys are looked up *by registry name*, and their order in the `.zrd` means nothing. An IMPACT key
that is not a registry name is never read at all.

## 2026-08-11 — A1: what faithful crash-def selection actually resolves per chapter

PLAN-crash-surface-id A1. The section above settles the *mechanism* — a table lookup on
the material's `soil` id, not a texture-name branch. This measures its *consequence*: joining
materials → polygons → meshes by `soil` id (never by texture name — that would re-create the exact
conflation the plan exists to undo), to know what a player actually flies over per id before B11
changes what plays.

**Method.** `analysis/surface-classification/soil_area_by_mesh.py` (new; siblings
`soil_id_probe.py`/`soils_list.py` unchanged) iterates every mesh (`models.json`, 1:1 with
`SceneBuilder`'s mesh index) in a chapter's extracted `gamez`, resolves each polygon's material
`soil` label to the id table above, and accumulates polygon count and triangulated area per id.
Triangulation matches `SceneBuilder.PolygonArea`/`EmitCollisionFaces` exactly — strip order for
`tri_strip` polygons, a fan from vertex 0 otherwise — not `class_area_share.py`'s simplified
always-fan `area()`, which mis-measures a strip. "Collidable area" here means every polygon of
every mesh, each mesh counted **once** regardless of how many nodes place it in the world — the
same convention `class_area_share.py` uses, so its water/buildings area shares (2026-08-01 section)
are directly comparable to the id-1/id-13 numbers below. Mesh names come from `nodes.json`'s
`model_index` → `name`; a mesh placed by more than one node is marked `+N more node` in the top-5
lists so that is visible rather than silently averaged away.

**Per-chapter area share by surface id** (of each chapter's total triangulated collidable area;
`—` means no material in that chapter carries the id):

| chapter | `default`(0) | `water`(1) | `fire`(5) | `airstrip`(8) | `buildings`(11) | `dzone`(12) | `dirt`(13) | **slot 0 share** |
|---|---|---|---|---|---|---|---|---|
| C1  | 91.51% | 2.09% | 0.00% | 0.04% | 0.00% | 0.00% | 6.35%  | **91.55%** |
| C1B | 95.83% | 4.15% | 0.00% | 0.01% | —     | 0.00% | —      | **95.85%** |
| C1C | 91.44% | 8.56% | 0.00% | —     | —     | —     | —      | **91.44%** |
| C2  | 82.60% | 6.83% | 0.00% | 0.40% | —     | 0.01% | 10.17% | **83.00%** |
| C2B | 91.75% | 8.25% | 0.00% | 0.00% | —     | —     | —      | **91.75%** |
| C3  | 63.88% | 33.27%| 0.00% | 0.01% | —     | 0.01% | 2.82%  | **63.90%** |
| C4  | 90.13% | 0.26% | 0.00% | —     | —     | 0.04% | 9.57%  | **90.16%** |
| C5  | 96.72% | 3.24% | 0.00% | —     | —     | 0.04% | —      | **96.76%** |

"Slot 0 share" sums ids `{0, 5, 8, 11, 12}` — every id whose `player_crash_<name>` def does not
exist in the shipped install, so `FUN_0048b920`'s cascade falls each of them back to `vector[0]`
alongside id 0 itself. It is 63.9–96.8% of every chapter's collidable area. **Four of eight
chapters (C1B, C1C, C2B, C5) carry no `dirt`-tagged geometry at all** — `player_crash_dirt` would
never fire there under the faithful rule.

**(a) Which mesh(es) carry `water`(1), and is it the sea the player dives into?** Yes, directly
confirmed. C2's `g29239` — the exact open-water tile the 2026-07-31 section's in-engine `--det`
dive test used to confirm the sea splash (`Water` 8/8, `splash1.flt` instanced) — resolves **100%
`soil=Water`** (mesh#392, area 1,048,576, its one and only id). Every chapter's top water-area
meshes are a long, flat list of `g#####`-named tiles each carrying almost exactly 1,048,576 area
units (the standard terrain-tile size, the same size the `default` and `dirt` top lists also show)
— the same shape of geometry as `g29239`, not a special-cased "splash volume". That pattern is
strong circumstantial evidence the open sea is `soil=Water` in every chapter, but **only C2's
`g29239` was directly probed in-engine**; the other seven chapters' water tiles are inferred from
the naming/size pattern, not independently confirmed by a `--det` dive.

**This measurement disproves the plan's biggest stated fear about the sea.** A1's evidence section
worried the `soil=Water` population (1–3 materials/chapter) might be far smaller than the
texture-classified `water` population (hundreds of polygons/chapter) and might not even overlap the
flyable open water. Measured: the `soil`-id water area share (2.1–33.3%, table above) and the
texture-name `water` area share (2026-08-01 section: 1.99–32.55%) are close in magnitude in every
chapter, and the one landmark both methods were checked against — C2's open water — is 100%
`soil=Water`. The earlier "exactly one `Water` material out of 484" note (2026-08-01 section,
"candidate fixes rejected") is not in tension with this: it counts distinct *materials*, and one
material texture-shared across hundreds of polygons/dozens of tiles is exactly how a 6.83% area
share comes from so few materials.

**(b) Which carry `dirt`(13), and is it anywhere a player would plausibly crash?** Yes, on the
evidence available. `dirt`-tagged meshes are the same shape of geometry as the `default`/`water`
ground tiles — numbered `g#####` names at the standard ~1,048,576-unit tile size, not clutter or
marker geometry (contrast the `fvol*` fog-volume and `dzpath*` danger-zone-path names that dominate
some chapters' small-area rows). C3's `dirt` set names `volcano1` outright — real terrain on a
volcanic-mountain level. This is ordinary ground the player flies over and can plausibly crash into,
just a small, real minority of it: 0% (four chapters), 2.82% (C3), 6.35% (C1), 9.57% (C4), 10.17%
(C2). No in-engine dive/crash was run against a named `dirt` tile — this is a data-side read, not an
in-engine confirmation, and should be one of B11's targeted `--det` checks.

**(c) Slot 0 share** — see the table's last column, 63.9–96.8%, i.e. **faithful selection makes
`player_crash_default` the ground/building crash for the large majority of every chapter's
collidable surface**, exactly as PLAN-crash-surface-id's Decision 6 states.

**Landmark cross-checks, per A1's verify criteria.** `docs/formats/weapon-effects.md`'s two named
landmarks were looked up directly (not sampled from a top-5 list), to test whether the
texture-name `buildings`/IMPACT tag and the `soil`-id registry's own `buildings`(11) slot agree —
they are different name spaces (the plan's Decision 3 / Constraints), so no agreement was assumed:

- **C1's `g306` hangar** (mesh#424) is **65.0% `airstrip`(8)** by area, 31.8% `default`(0), 3.2%
  `dirt`(13) — **not** `buildings`(11) at all, despite `SceneBuilder.ClassifySurface` tagging its
  `hangar*` texture `buildings` for the IMPACT table. Under the faithful crash cascade this tile
  resolves to slot 0 everywhere except its small dirt-tagged sliver — consistent, since `airstrip`
  has no `player_crash_airstrip` def either.
- **C2's `nycity` film-set towers** (mesh#548) are **100% `default`(0)** — no distinguishing
  `soil` tag at all, despite being the chapter's clearest `buildings`-by-texture landmark
  (2026-08-01 section, 8/8 `Buildings` on `nycity/col_buildings`). Ramming a skyscraper and ramming
  a hillside would play the identical crash def under the faithful rule.
- `buildings`(11) itself is carried by measurable area in only **one of eight chapters** (C1,
  0.00% share — 273 polygons, 16,066 area units across 40 meshes, table above) and by none of the
  other seven. The soil registry's `buildings` slot is essentially unused by any shipped material;
  it is not what makes a mesh "look like a building" to the player or to the IMPACT classifier.

**What this changes at the controls, in plain language.** Today, every ordinary crash that is not a
sea dive plays the `_dirt` choreography — terrain, hangars, skyscrapers, all of it, because the
current code only distinguishes water from everything else. Switching to the original's rule keeps
that water/not-water split working the same way (the open sea really is tagged `water` in the data,
confirmed directly for C2 and consistent everywhere else), so **the sea splash should look and sound
the same as it does now.** What changes is the "everything else" crash: it stops being `_dirt` and
becomes `_default` for the large majority of terrain and every building — 64–97% of each chapter's
ground, varying by level (C3, the most water-heavy chapter, has the least default-ground exposure;
C5 the most). `_dirt` still plays, but only on real, comparatively small patches of actual
dirt-tagged ground — a handful of tiles per chapter, up to about a tenth of the chapter's surface at
most (C2), none at all in half the chapters (C1B, C1C, C2B, C5). A player who reliably notices the
difference between the two ground crash choreographies (sound/anim, not implemented yet at time of
writing per Decision 6) would hear the `_default` variant far more often than today, and would only
ever hear `_dirt` in specific patches rather than everywhere on the ground — including on buildings,
which never get a variant of their own either way.

## 2026-08-11 — what A2's body-granularity surface id costs (the BL-204 shape, measured)

A2 stamps each collider body with the polygon-count-dominant `soil` id among its own polygons
rather than splitting colliders per id (about 2.3 % of meshes install-wide have a texture-class
bucket spanning more than one id). That is structurally the failure `BL-204` removed on 2026-07-31
— a dominant vote loses the minority — so it was measured before `B11` rather than after.
`soil_bucket_strand.py` replays `CollidersForMesh`'s bucketing and A2's dominance rule, and reports
the share of collidable area where the body-granularity id resolves a **different crash def** than a
per-polygon id would. Def accuracy, not id accuracy, is the metric: ids 0/5/8/11/12 all resolve
`_default`, so an `airstrip` polygon in a `default`-dominant bucket is not an error at all.

| | C1 | C1B | C1C | C2 | C2B | C3 | C4 | C5 |
|---|---|---|---|---|---|---|---|---|
| wrong-def area | 1.221 % | 0 % | 0 % | 0.909 % | 0 % | 0.942 % | **1.373 %** | 0 % |
| of `dirt`'s own area, stranded | 16.4 % | — | — | 6.9 % | — | 13.8 % | 8.3 % | — |

**Accepted — the analogy does not bite.** `BL-204` stranded 5.1–86.7 % of a class because the vote
was per *mesh*; these buckets are already per texture class per mesh, an order of magnitude finer,
so the worst chapter misplays **1.373 %** of its collidable area and four of eight misplay none.
`water`(1) is misattributed **nowhere in any chapter** — the sea dive is exact, not approximately
right. The error is also roughly symmetric (C4: 0.792 % `_dirt`→`_default`, 0.581 % the other way),
so it is not a systematic erosion of `_dirt`.

⚠ **The dirt-side number is the one to quote, not the area-side one.** Up to **16.4 %** of C1's
dirt-tagged ground plays `_default` under this approximation. That is invisible in a 1.2 %
whole-chapter figure, and it is the number that would explain a "this dirt patch doesn't kick up
dust" report at the controls. Splitting colliders per id is the fix if that ever matters; it grows
the collider tree and breaks the `col` name's per-parent uniqueness, so it is a change with its own
regression surface, not a tidy-up.

## 2026-08-12 — the touchdown vector's last arm plays nothing (B12)

Decoded out of `crimson.exe` (read-only ghidra-mcp session) while implementing `PLAN-crash-surface-id`
B12. The 2026-08-11 section said `FUN_0048d2c0` "repeats the same test byte-for-byte" as the crash
handler. It repeats the *selection*, but not the whole cascade, and the difference decides what our
code has to do when nothing resolves.

**The builder is the same shape.** `FUN_004735b0` (a level-init function, alongside the sunlight and
cloud-floor setup and the `opensesame`/`death`/`dzone` soil lookups) walks the surface registry by
count `FUN_00559650()` and name `FUN_00559660(i)`, prepends the literal `"touchdown_"` (`0x006274ec`),
interns the result with `FUN_00523820` — the same intern the crash builder uses — and pushes that
handle into the vector at `DAT_0071c2ec`. So "empty slot" means "this install ships no such def"
here exactly as it does for the crash family.

**The selection is the same test.** `0x0048d425`–`0x0048d460`: null material or a signed-negative id
takes the fallback path; otherwise the length is `(DAT_0071c2ec - DAT_0071c2e8) >> 2` and an id at or
beyond it, or a slot holding a null handle, falls to the same path; anything else is
`DAT_0071c2e8[id]`.

**The last arm differs.** The crash handler resolves a bare `player` anim handle when the vector
cannot answer. The touchdown fallback instead tests `vector == null || length == 0 || vector[0] == 0`
and, on any of those, `goto LAB_0048d4c1` — jumping **past** its play call `FUN_004edc10` entirely.
The graze family has no last-resort def: it plays nothing, and therefore makes no sound either,
because the bark is authored inside the def. Modelled as a null `lastResort` on `SurfaceDefTable`.

Unreachable in this install: all eight chapters ship `spark_touchdown-touchdown_default.json`, so
slot 0 always resolves. Implemented and pinned anyway (`EffectCatalogueTests
.AnEmptyTouchdownVectorPlaysNothing`), because it is the one place the two families genuinely part.

## 2026-08-12 — the weapon IMPACT table is surface-id-indexed too (`BL-344`)

Decoded the same session, prompted by the question "shouldn't the weapon impact also be classified by
the surface id?". It should. The 2026-08-11 write-up and `PLAN-crash-surface-id`'s scope note both say
the weapons IMPACT keys are "a different name space" and that `buildings` "is not a surface-registry
name at all". The second claim contradicts this file's own registry table (`buildings` is id 11), and
the first does not survive the decode.

**Parse time — the table is built by walking the registry.** `FUN_005ad630` is the `.zrd` token
dispatcher. On `IMPACT` (`0x0063cf98`, matched at `0x005ae1cf`) it takes the weapon's table base from
`weapon + 0x15c` and:

- `0x005ae1ea` matches the block's surface name against `registry[0]` (`[0x00637b14]`, i.e.
  `default`) and parses into the base row;
- `0x005ae1fd`–`0x005ae24e` loads the registry count `[0x00637b10]`, walks `EDI = 0x00637b18`
  (`&registry[1]`) with `ESI` stepping `0x64`, and on a name match parses into
  `base + i*100`.

So each weapon's IMPACT table is an **array indexed by surface type id, stride 100 bytes**, and a
block whose name is not a registry name is parsed into nothing. That is the fate of `fault`, which
also exists nowhere as a literal in `crimson.exe` — an authored row that can never be selected, the
same shape as the eleven crash slots with no def.

**Runtime — the index is the struck material's soil id.** `FUN_005acf60` resolves the hit, then:

```
surfaceId = hit->material ? *(int *)(material + 0x20) : 0
FUN_005ad100(weapon, hit, surfaceId, 1.0f)      // row = weapon[0x15c] + surfaceId * 100
```

the same dword at material offset `0x20` and the same null-material-to-0 arm `FUN_0048b920` and
`FUN_0048d2c0` use. `FUN_005ad100` then reads the row's variant count at `+0x2c` and picks one of
`+0x30[k]` by `rand()`; `FUN_005ad160` does the same over a second list at `+0x40`/`+0x44`.
Independently, `FUN_005ad330` tests `*(material + 0x20) == 1` (water) as a special case on the impact
path — the weapon code reads the soil id directly, in two places.

⚠ **The IMPACT fallback is applied when the table is built, not when it is read.** The parse loop's
miss arm copies the whole `default` row over every id the weapon names no block for, so `dirt`(13),
`fire`(5), `airstrip`(8) and `dzone`(12) geometry plays the weapon's ordinary `default` impact.
`FUN_005ad100`'s gate on `row[0x2c]` is real but unreachable for those ids, and it is the SOUND half
of the row specifically. Full decode: [`docs/org/weaponImpact.md`](../../docs/org/weaponImpact.md).

## 2026-08-13 — the `ai_crash_<name>` family: built per AI vehicle-params, selected by the same cascade (M4 G21)

Decoded out of `crimson.exe` (read-only ghidra-mcp session), closing the dispatch question
`BL-347`/G21 carried: the third registry-indexed vector found at `FUN_00478a00` is dispatched by
the SAME selector as the player crash family — there is no second cascade.

**Build.** `FUN_00478a00` is the constructor of the 0x298-byte AI vehicle-params object (one per
`aiv`/vehicle-type entry; sole caller `FUN_004735b0`, the level-init function that also builds the
touchdown global). It walks the surface registry (`FUN_00559650` count / `FUN_00559660` name),
prepends the literal `"ai_crash_"` (`0x00627d40`), interns each result (`FUN_00523820`, the same
intern the other two families use) and pushes the handle into the params object's vector at
`+0x160`/`+0x164`. The params parser `FUN_00479240` sets the family's bare last resort at
`+0x158` to the interned **vehicle name** (`0x0047b110`–`0x0047b11b`: intern of the params' own
name at `+0x4`) — where the player family's last resort is the literal `player`.

**Bind.** `FUN_00475820` (params → live vehicle) copies `+0x158` → vehicle `+0x6d0` and the
vector `+0x160`/`+0x164` → vehicle `+0x6e0`/`+0x6e4` — exactly the fields the crash selector
reads. `FUN_00476250` (plane setup) then ERASES that vector and rebuilds it as `player_crash_*`
(and `+0x6d0` as `player`) **only when the vehicle's name compares equal to `player`** — so every
AI aircraft keeps the `ai_crash_*` vector its params carried.

**Select.** `FUN_0048b920` `0x0048bac5`–`0x0048bb00`, unchanged: index vehicle `+0x6e0` with the
struck material's surface id; null material / negative / out-of-range / empty slot → slot 0;
empty vector or empty slot 0 → the bare handle at `+0x6d0`. One cascade, two vectors, keyed by
which vehicle crashed. `Flight/Airframe/SurfaceDefTable.cs` was reused verbatim with
`lastResort = the plane's own name`.

**Census.** All 8 chapters ship exactly three defs: `ai_crash_default` / `_dirt` / `_water`
(`extracted/<Cx>/cam_anim/kestrel-ai_crash_*.json`) — the same trio as the player and touchdown
families, so the same eleven slots fall back to slot 0. All 24 defs are authored NAME=`kestrel`
(the AI airframe they were written against; the player family's counterpart is `player`). They
are far simpler than the player defs: deactivate `dontmove`/`markers`/`healthy`/`destroyed` (the
AI wreck is NOT shown — the plane vanishes), play one surface boom (`snd_exp_ground_a` on dirt;
water's `snd_exp_water_a` rides the called `plane_big_splash`; default authors `air_mixed_exp_sg`
only), and CALL `call_car_trails` (default/dirt — anchored on `carnage_trails`, the same template
root as the player family's `call_crash_trails`), `flydirt_plane` (dirt) or `plane_big_splash`
(water). No chapter gamez carries a node named `kestrel` (0/8 scanned), so the engine-side rig
stages a meshless scaffold of that name.

## 2026-08-13 — A1: what the shipped weapons actually author, counted (`PLAN-surface-id-weapons` A1)

**Source established.** `WeaponDefs.cs`'s doc comment names `weapons.json`; the data is
`extracted/zrdr/weapons.zrd.json` (48 `BALLISTICS` entries, matching `WeaponDefs.All.Count` and the
`docs/architecture.md` line for the reader). `CSVM.Tests/fixtures/zrdr/weapons.json` is a 2-key
synthetic unit fixture (`probegun`/`PROBE_FUTURE_KEY`) and is not this data.

**Correction to the 2026-08-12 section above: `fault` is not in the shipped data.** That section
says the parse-time name match is "the fate of `fault`, ... an authored row that can never be
selected" — a literal `IMPACT` block named `fault`. It does not exist: zero occurrences of the
string `fault` (word-bounded; `default` contains it as a substring, which is what an unanchored
search finds) anywhere in `weapons.zrd.json`. Every `IMPACT` class name that any of the 48 weapons
authors maps to a registry name — there are no unmapped/discarded names in the shipped data at all,
so A1's planned "dead list of unmapped names" is empty by construction, not populated with `fault`
as its anchor. Where this claim came from is not established; treat the `fault` literal as
disproven rather than corrected to a different address.

**Per-id summary, all 48 weapons.** Six registry names appear as `IMPACT` keys anywhere in the data;
the other eight registry ids are authored by zero weapons.

| id | name | authoring weapons | with an effect (`value`) | authored, no effect (`null` value) |
|---|---|---|---|---|
| 0 | `default` | 47/48 | 47 | 0 |
| 1 | `water` | 47/48 | 47 | 0 |
| 2 | `seafloor` | 0/48 | 0 | 0 |
| 3 | `quicksand` | 3/48 (`wep_04`, `wep_25`, `wep_27`) | 3 | 0 |
| 4 | `lava` | 0/48 | 0 | 0 |
| 5 | `fire` | 0/48 | 0 | 0 |
| 6 | `player` | 44/48 | 44 | 0 |
| 7 | `enemy` | 31/48 | 3 (`wep_01`/`wep_02`/`wep_03`, the base 40/50/60-cal guns) | 28 |
| 8 | `airstrip` | 0/48 | 0 | 0 |
| 9 | `opensesame` | 0/48 | 0 | 0 |
| 10 | `death` | 0/48 | 0 | 0 |
| 11 | `buildings` | 47/48 | 47 | 0 |
| 12 | `dzone` | 0/48 | 0 | 0 |
| 13 | `dirt` | 0/48 | 0 | 0 |

`wep_26` (`FW`) is the one weapon with no `IMPACT` block at all — not even a `default` row — so it
is the "1/48" absence behind every `default`/`water`/`buildings` row above. `enemy`'s 28
authored-but-`null` rows parse into nothing by both the original's name-match-then-body-parse and
`WeaponDefs.ParseImpact` (`ZrdrDict.FromAlternating` on a null body never runs), so they are
functionally identical to not being authored at all for effect purposes — listed separately here
because the *name* is present in the data, unlike `fault`.

**Per weapon, not as a union** (the per-id table above hides that any one weapon's silence on an id
is universal — every weapon *except* the ones listed under "authoring weapons" above is silent on
that id):

| id | name | authored rows (value) | authored, empty | absent among the six used |
|---|---|---|---|---|
| wep_00 | 30slug | default, water, player, buildings | enemy | quicksand |
| wep_01 | 40slug | default, water, enemy, player, buildings | - | quicksand |
| wep_02 | 50slug | default, water, player, enemy, buildings | - | quicksand |
| wep_03 | 60slug | default, water, enemy, player, buildings | - | quicksand |
| wep_04 | 9M | default, water, quicksand, player, buildings | - | enemy |
| wep_05 | ARMOR | default, water, buildings | - | quicksand, player, enemy |
| wep_06 | BOOM | default, water, buildings | - | quicksand, player, enemy |
| wep_07 | FLAK | player, default, water, buildings | - | quicksand, enemy |
| wep_08 | SONIC | default, water, player, buildings | - | quicksand, enemy |
| wep_09 | FLASH | default, water, player, buildings | - | quicksand, enemy |
| wep_10 | BEEPER | default, water, player, buildings | - | quicksand, enemy |
| wep_11 | SEEKER | default, water, player, buildings | - | quicksand, enemy |
| wep_12 | CHOKER | default, water, player, buildings | - | quicksand, enemy |
| wep_13 | SMOKER | default, water, player, buildings | - | quicksand, enemy |
| wep_14 | TORPDO | default, water, player, buildings | - | quicksand, enemy |
| wep_15 | FLARE | default, water, player, buildings | - | quicksand, enemy |
| wep_23 | MPTUR | default, water, player, buildings | enemy | quicksand |
| wep_24 | BOOM | default, water, buildings | - | quicksand, player, enemy |
| wep_25 | gb | default, water, quicksand, player, buildings | - | enemy |
| wep_26 | FW | (none) | - | default, water, quicksand, player, enemy, buildings |
| wep_27 | FLAK | default, water, quicksand, player, buildings | - | enemy |
| wep_28 | CB | default, water, player, buildings | - | quicksand, enemy |
| wep_29 | TURRET | default, water, player, buildings | enemy | quicksand |
| wep_30 | 30slug | default, water, player, buildings | enemy | quicksand |
| wep_31 | 30 DD | default, water, player, buildings | enemy | quicksand |
| wep_32 | 30 AP | default, water, player, buildings | enemy | quicksand |
| wep_33 | 30 EX | default, water, player, buildings | enemy | quicksand |
| wep_40 | 40slug | default, water, player, buildings | enemy | quicksand |
| wep_41 | 40 DD | default, water, player, buildings | enemy | quicksand |
| wep_42 | 40 AP | default, water, player, buildings | enemy | quicksand |
| wep_43 | 40 EX | default, water, player, buildings | enemy | quicksand |
| wep_50 | 50slug | default, water, player, buildings | enemy | quicksand |
| wep_51 | 50 DD | default, water, player, buildings | enemy | quicksand |
| wep_52 | 50 AP | default, water, player, buildings | enemy | quicksand |
| wep_53 | 50 EX | default, water, player, buildings | enemy | quicksand |
| wep_60 | 60slug | default, water, player, buildings | enemy | quicksand |
| wep_61 | 60 DD | default, water, player, buildings | enemy | quicksand |
| wep_62 | 60 AP | default, water, player, buildings | enemy | quicksand |
| wep_63 | 60 EX | default, water, player, buildings | enemy | quicksand |
| wep_70 | 70slug | default, water, player, buildings | enemy | quicksand |
| wep_71 | 70 DD | default, water, player, buildings | enemy | quicksand |
| wep_72 | 70 AP | default, water, player, buildings | enemy | quicksand |
| wep_73 | 70 EX | default, water, player, buildings | enemy | quicksand |
| wep_130 | 30slug | default, water, player, buildings | enemy | quicksand |
| wep_140 | 40slug | default, water, player, buildings | enemy | quicksand |
| wep_150 | 50slug | default, water, player, buildings | enemy | quicksand |
| wep_160 | 60slug | default, water, player, buildings | enemy | quicksand |
| wep_170 | 70slug | default, water, player, buildings | enemy | quicksand |

**The dead ids — authored by no weapon — are `seafloor`(2), `lava`(4), `fire`(5), `airstrip`(8),
`opensesame`(9), `death`(10), `dzone`(12), `dirt`(13): 8 of the registry's 14 ids.** Of those,
`fire`/`airstrip`/`dzone`/`dirt` are also the four ids the 2026-08-12 section names as the ones
where a faithful build "plays no impact effect at all" — that specific list is confirmed. The other
four dead ids (`seafloor`, `lava`, `opensesame`, `death`) are outside that section's claim and are
additional silent ids, not previously counted.

**`quicksand` is authored but reaches no shipped material.** Three weapons author a populated
`quicksand` row, but the crash-cascade material census (this file, 2026-08-12 section, "used by
materials" row) shows no material in any of the 8 chapters ever carries soil id 3 — so those three
rows are unreachable in this install regardless of the A2 decision, the same shape as `fault` was
claimed to be, just reached a different way (unreachable by geometry, not by name mismatch).
`player`(6) and `enemy`(7) are in the same position — no material carries either id — which is
consistent with `WeaponDefs.cs`'s own doc comment that `Enemy` "has no meaning yet (nothing else
flies)": these two ids most likely key off a struck aircraft body's own id, not terrain, and sit
outside this file's material census either way.

**Cross-check against `PLAN-crash-surface-id` A1's per-chapter area table** (this file, 2026-08-12
section): summing the columns for the four ids a faithful build goes silent on
(`fire`+`airstrip`+`dzone`+`dirt`, reading `—` as 0%) gives, per chapter: C1 6.39%, C1B 0.01%,
C1C 0.00%, C2 10.58%, C2B 0.00%, C3 2.84%, C4 9.61%, C5 0.04%. This is the crash cascade's
*material*-soil-id area share (body granularity, all collidable ground), used here as the closest
available proxy for "how much of the ground a faithful weapon impact goes silent on" per the plan's
own instruction to make this cross-check — it is not a direct measurement of impacts fired at that
ground, and inherits that table's per-mesh/body granularity caveat (up to 16.4% of C1's `dirt`
polygons strand to `default` at body granularity, so these are upper bounds on the affected share,
not exact).

⚠ **Authoring no row for an id is inheritance, not silence** — those four ids play the weapon's
`default` impact ([`docs/org/weaponImpact.md`](../../docs/org/weaponImpact.md)). The area shares
above are the right measure of how much ground the id affects; they are not a silent share. What
genuinely plays nothing is a row the weapon **names** and leaves empty: `enemy`'s 28 null values and
the guns' empty `player` blocks, which this section already counts apart from "absent".
