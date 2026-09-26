# weather.json, per-mission atmosphere (validated on this install)

Part of the [format documentation](README.md) (see also [zrdr.md](zrdr.md),
[world-structure.md](world-structure.md)). Covers the mission's `weather.json` reader: distance fog, the cloud-cover
whiteout band, wind, and the shared **colour-triple encoding rule**. Consumed by
`CSVM/src/Flight/Airframe/Weather.cs` (`WeatherState`) + `Session.World.WeatherRig.Build`.

This reference covers fog colour, precipitation
(item 5) and the `SUNLIGHT_*` world-lighting decode (item 6, the night/overcast brightness
calibration).

## Contents

- [Location & shape](#location-shape)
- [Colour-triple encoding](#colour-triple-encoding)
- [Per-zone fog](#per-zone-fog-zonen)
- [World lighting](#world-lighting)
- [Atmosphere controls](weather/atmosphere.md)
## Location & shape

One `weather.json` per mission folder, in the mission's own zrdr archive
(`extracted/<chapter>/<mission>/zrdr/`), alongside `ia.json`/`objectives.json` (see
[spawns.md](spawns.md)). Some folders are multiplayer-only or lack the file, the loader
returns "no fog" then.

`weather.json` root[0] is one alternating **dict** (`key, [values…]`, see the
[shared conventions](README.md#shared-conventions-zrdr-readers)). Its blocks:

| Block | Read as | Notes |
|---|---|---|
| `VIEWING_RANGE` | dict | not used by the remake (the original scales `FOG_RANGES`/far clip by its per-detail `FOG_SCALE`/`CLIP_SCALE`; HIGH is 1.0 everywhere, so inert at full detail, see the decompile section) |
| `WIND` | **bare-scalar block** | `STATIC_VELOCITY [x,y,z]`, `RANDOM_MAX_SPEED s`, `RANDOM_ACCEL a`, `RANDOM_ANG_VEL deg/s` |
| `CLOUD_COVER` | **bare-scalar block** | `TOP`, `BOTTOM`, `THICKNESS` (metres) + optional `TOP_COLOR`/`BOTTOM_COLOR` |
| `ZONE<n>` | dict (+ `SW_*` twin) | per-zone fog; the `SW_*` software-renderer twin is ignored. **The names are per chapter, see below** |

**Bare-scalar blocks** (`CLOUD_COVER`, `WIND`, and the item-5 precipitation block) pair
each key with a *bare* value (`"TOP", 1124`) or a list (`"TOP_COLOR", [192,192,192]`), not
the uniform `key,[list]` the dict view assumes, so `ZrdrDict.FromAlternating` can't read
them; Weather.cs walks them as raw key/next-element pairs (`ScalarAfter`/`ListAfter`/
`Vec3After`). The per-zone blocks are all list-valued and go through the normal dict.

## Colour-triple encoding

Colour triples in weather.json use **two coexisting encodings**, even within one file:

- **Normalized float** `[0.69, 0.69, 0.69]` (C1 `FOG_COLOR`; C1B/C3 night-blue
  `[0.063, 0.094, 0.188]`; a C2 sky-fog `[0.80, 0.84, 1.0]`).
- **Integer 0–255** `[192, 192, 192]` (C4/C5 `FOG_COLOR`; C5 `[16,16,16]`; **every**
  `TOP_COLOR`/`BOTTOM_COLOR` seen, `[192]³`, C5 `[220]³`/`[64]³`, C1B/C3 `[32,56,72]`,
  integer-encoded even in the float-fog night chapters).

Rule (`Weather.ParseColor`): **divide the triple by 255 iff any component is strictly
> 1.** Verified unambiguous across *every* weather.json in the install:
- the only `1.0`-bearing colour is the float sky-fog `[0.80, 0.84, 1.0]`, its max is
  *exactly* 1, so strict `>1` correctly leaves it a float (a `>=1` test would wrongly
  shrink it);
- no integer colour is all-{0,1} (smallest non-zero integer component is 16); `[0,0,0]`
  is black under either interpretation.

The normalized value is a **DX7 sRGB framebuffer colour**: `WeatherRig` converts it
sRGB→linear before handing it to the world shader (which mixes fog in linear space).
Round-trip check: a fully-fogged pixel renders back at its source byte value, C4's 192
measures 192 gray, C1's 0.69 measures 176 (0.69·255).

*Bug this fixed:* the integer chapters built `Color(192,192,192)` (an HDR
colour far above 1), which sRGB→linear then blew to pure white, C4's Rocky-Mountains fog
was a blown-white wall with a hard horizon cut instead of its data's 192 haze.

## Per-zone fog (`ZONE<n>`)

### The zone names are per chapter, not a fixed `ZONE1`/`ZONE2` pair

Surveyed across all 53 `weather.json` in the install:

| chapter | zones (file order) |
|---|---|
| C1, C1B, C1C, C2, C2B, C3, C4 | `ZONE1`, `ZONE2` |
| **C5** | `ZONE1`, **`ZONE3`**, all 8 missions, no `ZONE2` at all |

Corroborated by the gamez `horizon` subtree, whose children carry the same names: C5's are
`zone3`/`zone1`, everyone else's `zone1`/`zone2`. (Note the two orders disagree, C5's
weather.json lists `ZONE1` first, its horizon lists `zone3` first, so "the first zone" is
only well defined per file.)

*Bug this fixed (polish-3 item 2):* the reader hardcoded `{ "ZONE1", "ZONE2" }`, so C5's
`ZONE3` was never read and the `zone2` default matched nothing, **every C5 flight rendered
with no fog and `WorldLight` 1 (fullbright)**, and its skydome built empty because
`BuildHorizon("zone2")` skipped both of C5's zone children. The zone table is now read from
whatever `ZONE<digits>` keys the file carries (the `SW_*` twins stay excluded), and an absent
request falls back to the file's first zone (`WeatherState.ResolveZone`), logged once.
**The default stays `zone2`**, see the selection note below.

### Mission zone selection

Not in the mission `zrdr` (`ia`, `objectives`, `targets`, `dzones`, `aiv`, `location`, `map`,
`egen`, `net`, `startanims`), not in the 53 mission `.gw` interp scripts (1,215 statements,
zero zone mentions), not in the ROF/DLL string tables (`DEBUGINFO.TXT`'s zone strings are
Danger-Zones **UI widget** names, `ozonestitle`, `o_radbutzone`). Zone selection therefore
happens engine-side in the binary, the same shape as the `fire2` trigger (see
[anim-definitions.md](anim-definitions.md#fire-animations)).

**`interp.json` neither.** Four zone-bearing strings in its 98
scripts, and none of them selects anything:

- `support\c1\load.gw` `CameraSetHorizonXZ zone2_cloud_floor` (×2) and `support\c1b\load.gw`
  `CameraSetHorizonXZ moon_reflection` (×2). **Neither argument is a node either chapter's gamez
  holds**, both are dangling. All 8 chapters additionally carry the plain
  `CameraSetHorizon horizon` (×2 each), so the verb pair is camera anchoring by node name, and
  `moon_reflection` shows the `XZ` form is not about zones at all.
- `support\c1\tex_fx.gw` and `support\c1b\tex_fx.gw` `FindNode h_zone1scroll` +
  `Object3DSetScroll on 0.07 0.0`, a UV scroll on the sky band, not a selection. C1 has that
  node; **C1B does not** (its `horizon/zone1` children are `g1163`–`g1166`), so C1B's statement is
  dead as well. `MissionSetup` already treats an unmatched `FindNode` as expected, not a warning.

⚠ **This retires the "C1's own scripts disagree" reading**, which had made C1 the priority for the
remaining `BL-100` A/B. The two C1 strings are not two zone selections: one names a node C1 does
not have, the other scrolls a texture. Nothing in `interp.json` bears on which zone a mission
flies.

#### Zone selection at runtime

The binary answers the selection question, and the answer is that **nothing selects one zone per
mission, the engine switches between them at runtime, per camera position**. The per-frame
atmosphere update (`FUN_0042ee40`, called from `FUN_0042f480`) computes a state 1/2/3, and on any
change `FUN_00472ea0` applies the matching `ZONE<n>` block wholesale:

- **State 1 → `ZONE1`**: the default, camera below the cloud band (or no band at all).
- **State 2 → `ZONE2`**: camera altitude ≥ the whiteout core's bottom edge (band centre −
  `THICKNESS`/2, precomputed at world init `FUN_004735b0` into `0071c2d0`). The check sits
  *inside* the `CLOUD_COVER`-exists gate, so **a mission without `CLOUD_COVER` can never enter
  zone 2**.
- **State 3 → `ZONE3`**: camera inside a gamez `fvol*` volume, armed only when `fogvol.zrd`'s
  `fog_zone` flag is set (see [fogvol.md](fogvol.md), which this decode also settles).

Applying a zone means: world fog near/far = `FOG_RANGES` × the active detail level's `FOG_SCALE`;
world fog altitude pair and `FOG_COLOR` (written into the world node's fog block at world+0x38);
camera near/far clip = `CLIP_RANGES` with far × `CLIP_SCALE` (a second camera's far is clamped
≥ 6.0); and the whole `SUNLIGHT_*` block pushed onto the gamez `sunlight` light node. The
hardware renderer indexes `ZONE1–3`, the software renderer `SW_ZONE1–3`, confirming the
"software twin" assumption above (the loader `FUN_004bc680` even initializes `SW_*` as a memcpy
of `ZONE1–3` before parsing its overrides, so an absent twin inherits the hardware zone). So
`VIEWING_RANGE` *is* consumed by the original, as those per-detail `FOG_SCALE`/`CLIP_SCALE`
multipliers, but with HIGH authoring 1.0 everywhere it is inert at full detail, and the
remake ignoring it stays correct.

**Every settled per-chapter verdict above is consistent with this rule**, the renders that
settled C1/C1C/C2B/C4 on `zone2` were all shot *above the deck* (state 2), the below-deck
reference is literally named `Zone1 environment`, C1B/C2/C3 author their `CLOUD_COVER` band out
of reach (C1B/C3 10000–11000, C2 19024–20124, checked in the extracted files), so
state 2 never fires and they fly zone 1 always, which is also why C2's empty `zone2` dome is
never a hole, and why the controls saw `ZONE1` haze persist above its 1024 m `FOG_ALTITUDE` top
(B13: the fade is per fragment *within* a zone; the *switch* is `CLOUD_COVER`, a different key),
and
C5, the one chapter with a `ZONE3` and `fog_zone 1`, is exactly the fog-volume-interior case.
What changes is the model: a deck chapter flies **both** zones, switched at the core's bottom
edge, a different altitude from the deck-regime flip at the band *centre*, but both sit
inside the fully-opaque core (C1: switch 1032, flip 1047, core 1032–1062), which is what makes
either invisible. The remake's one-static-zone `--sky-zone` model is therefore an approximation
that is exact above the band and wrong below it (a below-deck C1 should wear `ZONE1`'s fog);
whether that gap is visible enough to chase is a backlog question, not settled here.

> **Reader rule.** The remake switches the FOG
> block (`FOG_RANGES`, `FOG_ALTITUDE`, `FOG_COLOR` and the `SUNLIGHT`-derived world light) per
> camera state, on the state edge, exactly as `FUN_00472ea0` does, a below-deck C1 river pose
> measures a fog wall at **1752 m** against `ZONE1`'s authored 1750, where the static model drew
> it at 4000. The SKY DOME still comes from one zone per flight (`WeatherRig._activeZone`,
> `B14`'s business), and the per-zone `CLIP_RANGES` far is still not applied, a kept divergence,
> per the `CLIP_RANGES` note below. `--sky-zone` becomes a state override (Decision 5): explicit
> ⇒ static, default ⇒ state-driven. **The `SUNLIGHT_*` pair is NOT uniformly identical across the
> deck chapters**, 6 of the 24 C1/C1C/C2B/C4 missions author a different `SUNLIGHT_AMBIENT`/
> `_DIFFUSE`/`_COLOR_*` per zone (C1 M02/M05, C1C M01/MP1/MP3, C2B M04), so the world light rides
> the state too; C1/IA1, the mission every golden and every recorded A/B pose flies, is one of
> the 18 identical ones, which is why the change reads as fog-only in every instrument on file.

Decompiled sources are reproducible from `crimson.exe` in Ghidra at the addresses named
(loader `FUN_004bc680` = `weather.cpp`, zone parser `FUN_004bc3e0`, per-frame `FUN_0042ee40`,
zone applier `FUN_00472ea0`, world init `FUN_004735b0`).

So the remake selects it via `--sky-zone` (default `zone2` = night). **Every chapter is settled.** [Horizon geometry](#horizon-geometry) identifies C1B, C2, and C3; the C5 evidence distinguishes its two candidates; and [the deck chapters](#deck-chapters-use-zone2-at-the-documented-camera-state) use `zone2` at the documented camera state. C5's two candidates were the far-apart pair:

| | `ZONE1` | `ZONE3` |
|---|---|---|
| `FOG_COLOR` | `[0,0,0]` | `[16,16,16]` |
| `FOG_RANGES` | 1500 – 2250 | **50 – 250** |
| `CLIP_RANGES` | 5 – 2500 | **5 – 300** |
| `FOG_ALTITUDE` | 9000 – 10000 | 9000 – 10000 (identical) |

Note the identical `FOG_ALTITUDE`: whatever else distinguishes C5's two zones, altitude cannot be
it. ⚠ **`FOG_ALTITUDE` does not select a zone in any chapter**, the altitude-triggered
zone-switch reading died on C2, whose `ZONE1` band top (1024 m) is routinely flown while its
`horizon/zone2` holds **zero** meshes, so a switch there would open a hole in the sky. It is a fade
inside one zone's fog, nothing more; see [the zone keys](#zone-keys) below.

#### C5 uses `zone1`

The user flew C5/IA1 in the original and **can see across the city**, which `ZONE3`'s 50–250 m
fog and 300 m clip make impossible. So C5 = `zone1`, and the far-apart candidates above are no
longer an open question.

The remake already renders it: the `zone2` default matches nothing in C5 and
`WeatherState.ResolveZone` falls back to the file's first zone, which is `ZONE1`. That is
**stable, not lucky**, all 8 C5 missions list `ZONE1` before `ZONE3`, so every one resolves to
`zone1`. Verified at runtime: `weather: C5/IA1 has no 'zone2' (zones: zone1/zone3) — rendering
'zone1'`, fog 1500–2250.

⚠ **Do not "simplify" the fallback into taking the horizon subtree's first zone instead.** The
two orders disagree, C5's weather.json lists `ZONE1` first, its horizon lists `zone3` first
(above), so that change would silently render the sky of one zone with the fog of another.
`WeatherRig`'s `LoadWeather` step resolves against weather.json *first* and passes the result into
`BuildHorizon`, which is what keeps the pair consistent; `BuildHorizon`'s own fallback is a
no-op in that path and exists only for a mission with no weather.json at all.

#### Horizon geometry

`ZONE2` being *defined* is not the same as it being *flown*. In **C1B, C2 and C3** the gamez
`horizon/zone2` node is a bare marker, `model_index: -1`, `child_indices: []`, so the `zone2`
default resolved to itself (they do carry `ZONE2` fog) and `BuildHorizon("zone2")` built a dome of
**zero meshes**: what rendered was the engine's clear colour, with a hard horizon cut. The fog was
wrong the same way (C3 wore night-blue on a mission its own data lights at diffuse 1.5, sun 25° up;
C1B fogged only the 1128–1256 m band).

Meshed nodes under each `horizon/zone*` subtree, install-wide (the zone node included, gamez child
order, which is **not** zone-number order):

| chapter | horizon zones | flown |
|---|---|---|
| C1 | zone1 **2**, zone2 **4** | **zone2** (both build, settled by render, below) |
| **C1B** | zone1 **4**, zone2 **0** | **zone1** |
| C1C | zone2 **4**, zone1 **1** | **zone2** (both build, settled by asset parity, below) |
| **C2** | zone2 **0**, zone1 **3** | **zone1** |
| C2B | zone2 **2**, zone1 **1** | **zone2** (both build, settled by render, below) |
| **C3** | zone2 **0**, zone1 **3** | **zone1** |
| C4 | zone2 **4**, zone1 **1** | **zone2** (both build, settled by render, below) |
| C5 | zone3 **1**, zone1 **2** | zone1 (no `zone2` at all, the fallback above) |

The gamez node counts agree ([world-structure.md](world-structure.md)'s `zone_id` census): C1B and
C3 author their worlds under zone 1 (2,101 and 1,647 nodes, against 2 in zone 2), C2 likewise (766
against 1). ⚠ That agreement holds only for these three *single-zone* chapters. For the chapters
that populate both, the census is **not** evidence about which zone is flown, `zone_id` is a
per-frame camera-state filter, so a chapter legitimately ships content in both and switches between
them (the census is not zone evidence; C1/C1C/C2B/C4 = `zone2` was settled by render instead).

So `WeatherState.PreferPopulatedHorizonZone` swaps the request for the one zone that has geometry,
and only then, the rule is deliberately narrow, and every other shape keeps the request:

- the request builds a dome → kept (C1, C1C, C2B, C4 are untouched, and their choice stays the
  open fidelity question below);
- the request is not one of the horizon's zones at all → kept, so `ResolveZone`'s weather-file
  fallback owns it (C5). **This is why the rule must not simply take the horizon's first zone**:
  the two orders disagree (C5's weather.json lists `ZONE1` first, its horizon lists `zone3` first),
  and doing so would render one zone's sky under another's fog;
- no zone, or more than one, builds geometry → kept (not decidable from geometry).

The correction is applied only if the mission's own weather.json also defines fog for the new zone,
so the sky and the fog can never come from different zones, and it is skipped entirely under an
explicit `--sky-zone=`, which stays a literal request (empty dome and all) for inspection and for
the repro poses recorded in `analysis/`. Coverage: `CSVM.Tests/SkyZoneTests.cs` pins both the rule
and the real per-chapter census.

⚠ **A dome bigger than the far plane is clipped open.** The dome is camera-anchored, so its far
wall sits at (its own radius × `GameSession.HorizonScale`) from the eye. Every chapter's dome is
6.4–12.0 km and clears the 40 km far plane at the 2.5× anchor scale, except **C1B's zone1 at
21.8 km**, where 2.5× reaches 54.5 km and the sky renders as a hole onto the engine clear colour
(seen at the controls the moment this selection first chose that zone). `HorizonScaleFor` therefore
treats 2.5× as a maximum and fits the dome inside `HorizonFarFraction` of the far plane; C1B lands
at ~1.65×, every other chapter keeps 2.5× exactly.

#### The dome below the horizon uses `FOG_COLOR`

Every chapter's dome is **two pieces sharing one ring at local Y = 0**, which, the dome being
camera-anchored, is always the camera's own altitude, i.e. the horizon line:

- the **wall**, textured (`Sky1.tif`/`c4sky2.tif`/`c5sky2.tif`/`sky2.tif`), running Y 0 → +1.6…+4.1 km
  with a vertex-colour gradient, closed by a flat cap polygon at the top;
- the **skirt**, an inward-tapering cone running Y 0 → −3.0…−11.7 km, closed by a flat disc.

The skirt carries **no texture**: one `Colored` material, and its colour is the flown zone's own
`FOG_COLOR`, byte-exact in six of the seven chapters that have one:

| chapter (flown zone) | `FOG_COLOR` | skirt `Colored` | skirt node |
|---|---|---|---|
| C1 `zone2` | 176,176,176 | 176,176,176 | `g1155` |
| C1B `zone1` | 16,24,48 | 16,24,48 | `g1165` |
| C1C `zone2` | 176,176,176 | 176,176,176 | `g1155` |
| C2 `zone1` | 205,215,255 | 205,215,255 | `g1155` |
| C2B `zone2` | 176,176,176 | 176,176,176 | `g1168` |
| C3 `zone1` | 201,201,201 (0.79) | 200,200,200 | `g1155` |
| C4 `zone2` | 192,192,192 | 192,192,192 | `g1166` |
| C5 `zone1` | 0,0,0 | textured `c5sky2.tif`, vertex colour 0,0,0 | `g1171` |

**That is how the original hides the horizon seam.** Terrain fades to `FOG_COLOR` with distance and
stops at the map edge; below the horizon the dome simply *is* that same colour, so there is nothing
to blend. The seam needs no gradient, no fog on the dome, and no scaling change, B18's whole fix
was to stop applying that one authored colour twice (`docs/architecture.md`, `SceneBuilder.cs`).
⚠ **A skirt colour that does not match its zone's `FOG_COLOR` means the wrong zone is being flown**,
not that the dome needs painting, the pair is the check.

**The dome's colour is authored end to end, so there is no grading stage to reproduce.** Wall
vertex gradient + `FOG_COLOR` base ring + skirt + wall texture is the whole of it: `BuildHorizon`
applies no tint, grade or tonemap, the horizon models are authored `lighting: false` so not even
`csky_world_light` reaches them, and the `Environment` is Linear with no exposure or adjustment.
Rendered that way the sky lands inside ±10 per channel of the original's at the pinned above-deck
poses (C1, C2B, the zone-verdict table below). `CLOUD_COVER`'s `TOP_COLOR`/`BOTTOM_COLOR` are the
in-cloud whiteout's colours, not a sky grade.

#### The lowest wall ring uses `FOG_COLOR`

The wall shares more than a ring with the skirt: its bottom row of vertices carries the same
`FOG_COLOR` value, so the two pieces meet at one continuous colour and the join is invisible by
construction. Install-wide, flown zone, no exceptions:

| chapter | `FOG_COLOR` | wall base ring | grades to (next ring up) |
|---|---|---|---|
| C1 / C1C / C2B | 176,176,176 | 176,176,176 | 99,112,154 |
| C1B | 16,24,48 | 16,24,48 | 36,48,72 |
| C2 | 205,215,255 | 205,215,255 | 178,193,255 |
| C3 | 200,200,200 | 200,200,200 | 199,207,218 |
| C4 | 192,192,192 | 192,192,192 | 173,180,203 |
| C5 | 0,0,0 | 0,0,0 | 16,18,27 |

⚠ **The gradient above that ring is authored and steep, and it is NOT a defect to paint over.** On
C1's wall the next ring up sits at only **9.8° elevation**, so the first degree above the horizon has
already lost ~7 units, measured on our own render as **176.00 at the horizon falling to 168.60 at
13 px** (1.24°), which is the authored interpolation to the decimal. The wall's texture cannot mask
it either: the shipped `rtexture*` `sky1` carries a **27-row white band** top and bottom, so the
lowest ~13 px of wall multiplies the vertex colour by exactly 1.0 and the authored value lands once,
unmodulated (a flat-white `--tex-override` there leaves the frame byte-identical). **The original's
own frames show no such gradient near the horizon**, `C1 IA1 Fog river.png` is dead-flat `175.00`
(per-row sd 0.00) for 36 px above its horizon, so whatever hides it in the original is *not* a wall
painting rule. Do not "fix" the wall's colours.

⚠ **Engine-side consequence:** this ring's own colours and gradient are untouched, what differs
is how far out the deck sheet reaches before it hands off to them. The sheet's 144 textured tiles
end at 6,144 m and a plain fog-saturated annulus (`WorldBuilder.AddDeckAnnulus`) extends it to a
20,480 m half-span, a picked constant that sits inside the smallest flown dome. The floor is
world-fixed at the tiles' authored altitude, so the rim's edge sits at `f·(cameraY − floorY)/20480`
px below the horizon and grows with altitude: a few pixels at deck height, where this ring has
lost only ~2 units of its own gradient, invisible. Every deck chapter (C1/C1C/C2B/C4) gets the same
extension.

#### Deck chapters use `zone2` at the documented camera state

These four define `ZONE2` *and* build a dome for it, so the geometry above cannot decide them, and
the `interp.json` re-read had already retired the "C1's scripts disagree" tiebreak. The **render**
decides, at a pose where the dome fills the frame, and all four land on the zone the remake
already defaults to, so nothing in the selection code changes.

| chapter | verdict | what settled it |
|---|---|---|
| **C1** | **`zone2`** | `OriginalScreenshots/C1 IA1 Cloud Puffs and Moon.png` shows a crater-textured **moon**; `C1 IA1 Fog above clouddeck.png` shows a **faint star field** (8 isolated 1–2 px points, +13…+29 over a sky of sd 3.4). Only `horizon/zone2` holds a `moon` or `stars` mesh, `zone1` is `h_zone1scroll` + `o28`. At the pinned above-deck pose our `zone2` measures **(66.1, 74.6, 105.0)** against the original's **(64.9, 73.6, 103.1)**, inside ±10 on every channel; `zone1` renders flat **(169.4, 169.6, 170.7)**, off by +104/+96/+68 and neutral grey where the original is blue |
| **C4** | **`zone2`** | `playtest/CAP-12/c4/t21.5-1230m-above.png` shows the same moon disc cut by the top-left frame edge. C4's originals are video-graded, so absolute levels are not comparable and the within-frame statistic is the blue shift: the original runs **B−R +8.6…+15.6**, our `zone2` **+29.7**, our `zone1` **exactly 0.0** |
| **C2B** | **`zone2`** | `playtest/CAP-11/t50-c2b-above-deck.png` sky **(71.7, 77.6, 110.3)** vs our `zone2` **(64.3, 72.3, 100.5)**, inside ±10; `zone1` is flat `176³` |
| **C1C** | **`zone2`**, **asset parity only** | no original C1C footage exists. Its `horizon/zone2` is the *same four meshes with the same bboxes* as C1's (`moon`, `stars`, `g1155`, `h_zone2scroll`); its `zone1` is a single mesh rendering a featureless field. This one verdict rests on identity with a settled chapter, not on a comparison |

⚠ **A daylit mission does render a moon and a star field.** Three arguments pointed at `zone1`,
the moon/stars read as nocturnal on missions lit as day; only `ZONE1` is per-mission-tuned across
the 53 weather files; `zone1`'s `FOG_ALTITUDE` equals `[CLOUD_COVER BOTTOM, band centre]` exactly
3/3, and all three rest on that single unstated premise, which the original's own stills refute.

⚠ **Shoot the comparison with fog neutralised on BOTH sides** (`--no-fog`). With the dome fogged,
either zone renders the same flat grey below the band and the test is degenerate (`METHOD-1`).

~~⚠ **The gamez `zone_id` node census is not admissible evidence about which zone a mission
flies**, even though it happened to agree. Nothing in `CSVM/src` reads `zone_id`; its agreement
with the four already-settled chapters is degenerate (three of them have no zone-2 world to
disagree with, and C5, the one that could speak, disagrees); and in C1 the visibility reading
contradicts itself, since `zone_id 1` holds the mission's own `ap_transmitter` and `dz1`–`dz5`
targets while `zone_id 2` holds the entire cloud deck, all 9 `fvol` volumes and the dome, so a
draw-only-the-flown-zone gate leaves mission content unbuilt whichever zone is chosen. It is a
partition tag of one world whose runtime meaning is unknown. Do not re-cite it.~~

**Correction (decompile).** `zone_id` is not a per-mission zone selector and was
never claimed to be one by anything that reads it at runtime, it is the **per-frame visibility
gate** [decoded above](#zone-selection-at-runtime):
`FUN_0056c430(node_zone_id)` draws a node iff `zone_id` is −1, or `zone_id` is in the camera's
armed zone set. That set is armed every frame by `FUN_0042ee40` via `FUN_004d62d0` with
`{count=2, 0, state}`, `state` being the camera's *current* 1/2/3, not a fixed per-mission
choice. The "mission content unbuilt whichever zone is chosen" objection dissolves under this
reading: nothing is ever built-once-and-picked. A node with `zone_id 1` (C1's `ap_transmitter`,
`dz1`–`dz5`) draws whenever the camera is in state 1 (below the deck) and is culled in state 2
(above); `zone_id 2` (the deck tiles, `cloudparent` facades, the `fvol*` volumes, the zone-2
dome) is the mirror. Both draw across a single flight that crosses the deck, which is exactly
what a mission with below- and above-deck objectives needs. The full install-wide census
(C1/C1C/C2B/C4 decks, C1B/C2/C3/C5 horizon zones) is tabulated in
[the deck census below](#deck-census), it
confirms the tiles/`cloudparent`/`fvol*` = `zone_id 2`, mission content = `zone_id 1` pattern in
three of the four deck chapters and records where it does not (C2B's `fvol*` are `zone_id −1`,
and C2B ships no `cloudparent` nodes at all, a real per-chapter divergence, not a re-derivation
of this disproof).

⚠ **The authored `zone_id` is only half the gate.** A flown object is not authored into a zone at
all, it earns one every frame from its own altitude against the cloud band's midpoint
(`FUN_00489f60`), which is what keeps an aeroplane or an airship on the far side of the band out of
the picture. That half is decoded in
[atmosphere.md](weather/atmosphere.md#an-object-on-the-far-side-of-the-band-is-not-drawn-at-all).

**The skirt/`FOG_COLOR` pair agrees, but it cannot discriminate here.** Each dome's untextured
skirt is authored in its zone's own `FOG_COLOR` (above), and every render of these four is
consistent with that: C1/C1C/C2B show a 176 skirt against 176 fog, C4 a 192 skirt against 192. It
is a *consistency* check only in these four chapters, because both of their zones ship the **same**
`FOG_COLOR` (C1/C1C/C2B `0.69³`, C4 `192³`), so the pair would look right under either verdict.
Where the two zones' colours differ it is a real check, which is the form the ⚠ above states it in.

#### Deck census

Read from `extracted/<CH>/gamez/nodes.json` + `models.json` (only C1 had been measured before
this pass; C1C, C2B, C4 and the four non-deck chapters' horizon subtrees are new). Deck-tile
population found by material texture prefix (`cloudlayer*` for C1/C1C/C2B, `sky1*` for C4, per
`WorldBuilder`'s own classifier comment):

| chapter | deck tiles (count / altitude / `zone_id`) | `cloudparent` `zone_id` | `fvol*` `zone_id` | mission content `zone_id` |
|---|---|---|---|---|
| C1 | 144 / 960 m / **2** | 28 nodes / **2** | 9 / **2** | `ap_transmitter`, `dz1`–`dz5` = **1** |
| C1C | 144 / 960 m / **2** | 30 nodes / **2** | 21 / **2** | no `ap_transmitter`/`dz*` by name; 146 generic `zone_id 1` nodes (`gNNNNN`), the pattern (mission-placed geometry gated to 1) holds, the target names are chapter-specific |
| C2B | 144 / 960 m / **2** | **none** | 9 / **−1** | same generic `zone_id 1` population (149 nodes) as C1C |
| C4 | 144 / 1050 m / **2** | 45 nodes / **2** | 9 / **2** | `dz1`–`dz5` = **1** |

⚠ **C2B contradicts the C1 pattern on two counts, and it is recorded, not smoothed over.**
C2B ships **zero** `cloudparent` nodes (C1/C1C/C4 have 28–45), and its `fvol*` volumes are
`zone_id −1` (always visible) rather than `2`, so C2B's fog volumes are never culled by camera
state at all, unlike the other three deck chapters'. Both are real per-chapter authoring facts,
not a reading error (re-run, same result). Neither breaks the visibility-gate mechanism itself
(`zone_id −1` just means "always drawn," which `FUN_0056c430` handles the same as any other −1
node), but a `zone_id`-gate implementation must not assume every deck chapter's `fvol*`
population is gated.

**`B12` landed the gate on exactly this reading .** The remake now reads the zone off
each chapter's own volumes (`WorldBuilder.FogVolumeZoneIdOf`) instead of assuming one, and C2B is
the chapter that proves it: at `(-7325, 192, -3829)` its ambient cloud field measures **123,989
sprite px with the gate on against 0 with `--no-zone-cull`**, the gate makes it *more* visible,
because the `zone_id 2` deck that was occluding it from below is culled and the `zone_id −1` field
is not. C1 at the identical pose reads 0 both ways, its field being `zone_id 2`. Engine-side:
`docs/architecture.md`'s `src/Mech3/ZoneGate.cs` entry.

#### Deck tiles author `fog: true`

Read from `extracted/<CH>/gamez/models.json` for every tile the deck classifier picks (one flat,
untilted 4-vertex quad at the coverage-winning altitude):

| chapter | tiles | texture | `fog` | `lighting` | `clouds` |
|---|---|---|---|---|---|
| C1 / C1C / C2B | 144 each @ y 960 | `cloudlayer.tif` | **`true` 144/144** | `false` | `false` |
| C4 | 144 @ y 1050 | `Sky1.tif` | **`true` 144/144** | `false` | `false` |

The `fog: false` recorded in [`fogvol.md`](fogvol.md) belongs to the `fvol` cloud **cards**, and
to nothing else in the overcast: the deck tiles fog, and so do all 626 / 1056 / 1453
`cloudparent` facades in C1 / C1C / C4. What *does* author `fog: false` is **every horizon model
in every chapter** (C1 6/6, C1C 5/5, C2B 3/3, C4 5/5), which is why the below-deck ceiling never
fogs out. ⚠ **So "the original's ceiling texture survives to ~12.6 km" is a fact about the DOME
with no tile-flag component at all**; the tiles' `fog: true` is instead what makes the ABOVE-deck
floor, the 144 tiles and `C26`'s annulus alike, both through the same fogged material path, fade
to `FOG_COLOR` at the horizon.

#### Deck tiles carry two SUNLIGHT-dimmed variants

The deck tiles author `lighting: false` like the dome and C1's and C4's `fvol` cloud cards (C1C,
C2B and C5 author theirs `true`, which buys them a per-vertex directional term rather than a
brightness scalar; see [`../org/vertexLighting.md`](../org/vertexLighting.md)), but the deck
alone was measured to be SUNLIGHT-dimmed in the original, `Flight/Airframe/Weather.cs`'s `SunIncidence`
was calibrated on this exact texture. `WorldBuilder.Add` therefore force-lights the deck's own
tiles (`forceLit: isDeck`) regardless of the authored flag, applying `csky_world_light` deck-local,
never as a change to the `lighting` gate or to `csky_world_light` itself.

⚠ **That dimming is the BELOW-band regime only.** The ceiling a camera under the band sees is the
overcast's dimmed underside; the floor a camera above it sees is the undimmed top, the original's
own above-band frames contain no pixel below `FOG_COLOR` at all. `WorldBuilder` therefore builds
each tile in BOTH `forceLit` variants (`RecordDeckUndimmedMesh`) and `Session/WeatherRig.Tick`
assigns one per rig at the band crossing. C4's deck is unaffected by construction (its
`WorldLight` clamps to 1.0), which is the control proving the fix is deck-local rather than a
hidden global change.

#### Below-deck ceiling profile

At the C1 river pose (`-7323,192,-3829` / `-0.997,-0.1,0.070`) against
`OriginalScreenshots/C1 IA1 Fog river.png`, both frames anchored on their own terrain silhouette
(the two are not framing-matched, ours rises 115 px above its true horizon, the original's sits
48 px below its own) and measured in a HUD-free column band:

| | ceiling texture survives to | dead-flat band above the silhouette |
|---|---|---|
| original | the silhouette (0 px) | **20 px at 175.000** |
| ours (`ZONE1` 1000–1750 + the zone-1 dome) | the silhouette (0 px) | **21 px at 176.000** |
| ours under static `ZONE2` 1000–4000 (the pre-`B11` fog) | dies 110 px above it | 66 px |

⚠ **The "the original is dead-flat for 36 px above its horizon" residual (restated by `B14`) does
not exist**, it was a full-width sd (the still's HUD gauges and crosshair sit in those rows)
anchored on a horizon row of 370 derived as "terrain onset 411 − 41"; the still is a level 713-row
frame, so its true horizon is its centre row **356**, and its terrain onset in a clean band is
**404**, which reproduces `C21`'s own "41 px below the horizon" exactly. Our band reads 176.000
against the original's 175.000, C1's authored `FOG_COLOR` against a one-unit-lower render, a DX7 quantisation-sized offset, recorded and not chased.

`horizon/zone1` and `horizon/zone2` subtrees, all eight chapters (`—` = zone absent/empty):

| chapter | `zone1` meshed children | `zone1` Y-levels (model, `bbox_mid.y`) | `zone2` meshed children |
|---|---|---|---|
| C1 | `h_zone1scroll` + `o28` (skirt) | 768: −2000/270.7/789/1835/2519.4/**2792.8**, mid **396.4** (cap); 769 (`o28`): −2000/270.7, mid −864.7 (skirt) | `moon`, `g1155`, `stars`, `h_zone2scroll` |
| C1C | **`g1164` only**, one combined mesh, no separate skirt, no `h_zone1scroll` | 156: −5568.8/326.7/2374.7, mid **−1597.1** | `moon`, `g1155`, `stars`, `h_zone2scroll` (asset parity with C1) |
| C2B | **`g1166` only**, same shared geometry as C1C's `g1164` (identical vertex Y-set/`bbox_mid`) | 154: −5568.8/326.7/2374.7, mid **−1597.1** | `g1167`, `g1168` (scroll+skirt equivalents; **no moon/stars**) |
| C4 | **one node, confusingly named `h_zone2scroll`**, no separate skirt | 349: −3274.5/982.0, mid **−1146.3** | `g1165`…`g1168` (moon/skirt/stars/scroll equivalents, full parity with C1) |
| C1B | `g1163`–`g1166` (4 nodes, full dome+skirt+sun-adjacent set) | - |, (empty marker) |
| C2 | `sun`, `h_zone2scroll`, `g1155` (3 nodes) | - |, (empty marker) |
| C3 | `sun`, `h_zone2scroll`, `g1155` (3 nodes) | - |, (empty marker) |
| C5 | `moon`, `g1171` (2 nodes) | - | `zone3` (1 node, `zone_id 3`) |

⚠ **The "~396 m authored cap centre" is a C1-only number, and B14 will need one per chapter, not
a shared constant.** C1's `h_zone1scroll` is a two-piece dome (upward cap + separate downward
`o28` skirt); C1C and C2B's zone-1 geometry is a **single** mesh with no node named
`h_zone1scroll` at all (matching the plan's own B14 trap note that C1C has no scroll statement,
confirmed, and the zone-1 subtree there is exactly one node, `g1164`, not a
`g1163`–`g1166` range); C4's sole zone-1 node is even named `h_zone2scroll`, a leftover/reused
name, not `h_zone1scroll`. None of the three reproduce C1's dome-mid-at-+396 shape, their
whole-mesh `bbox_mid.y` values are strongly negative (skirt-dominated), because there is no
separate cap piece pulling the average up. Whatever each chapter's below-deck ceiling distance
actually reads as at the controls, it must be measured per chapter; C1's 396.4 does not transfer.

#### The `+396.4 m` cap centre is a bounding-box midpoint

**`396.4` is `bbox_mid.y` of C1's `h_zone1scroll` model, i.e. `(−2000 + 2792.8) / 2`.** There is no
polygon within 2 km of it. `B14` read the model's own vertices and polygons to place the ceiling and
found the mesh's flat ceiling CAP, a 12-gon closing the vault, at **+2792.8 m dome-local**. The
coincidence that made the wrong number persuasive is that A7's *measured* below-deck ceiling was
"~400 m"; A7 was measuring the relocated deck sheet, and `C21`/`C25` later re-fit that same
measurement to 110–155 m, so the agreement was never evidence in the first place. Everything the
`A1` census says about the SHAPE of these meshes stands; only the reading of `bbox_mid.y` as a "cap
centre" is retired. Do not re-cite 396.4.

Each deck chapter's zone-1 dome, read from `models.json` (vertices + polygon materials), with the
rim elevation the cap subtends from the camera, the only quantity a render can measure, since the
dome is camera-centred and uniformly scaled about the camera:

| chapter | zone-1 dome | flat ceiling cap (dome-local Y / radius / rim elevation) | texture | UV scroll |
|---|---|---|---|---|
| C1 | `h_zone1scroll` (vault) + `o28` (skirt) | **+2792.8** / 2470–2609 / **46.9–48.5°** | `sky2.tif` on the vault, elevations **1.76°…48.5°**; cap, skirt and floor disc are flat `FOG_COLOR` 176 | **0.07 u/s** (`texture_scroll` in the model, = `tex_fx.gw`'s `Object3DSetScroll on 0.07 0.0`) |
| C1C | `g1164`, one mesh | **+2374.7** / 1448.2 / **58.6°** | **none**, every polygon is `Colored` 176 | none |
| C2B | `g1166`, one mesh, identical vertex data to C1C's | **+2374.7** / 1448.2 / **58.6°** | **none**, `Colored` 176 | none |
| C4 | `h_zone2scroll` (reused name; one mesh) | **+982.0** / 6400.0 / **8.7°** | **none**, `Colored` 192 | none |
| C5 `zone3` | the zone node itself carries the model | **+982.0** / 10137.1 / **5.5°** | **none**, `Colored` 16 | none |

**Two findings in that table, both authored rather than incidental.**

1. **C1 is the only chapter whose below-deck sky carries a TEXTURE at all**, and the only one that
   scrolls it. The other three deck chapters' zone-1 geometry is a single untextured shell painted
   their own zone's `FOG_COLOR`, which is not a stub: an unfogged surface painted `FOG_COLOR` is
   exactly what infinitely distant fogged geometry looks like, so below the deck those chapters
   render a seamless flat overcast in every direction, by construction.
2. **The `B18`/`C26` `FOG_COLOR` rule extends to the zone-1 domes unchanged**, verified against
   each chapter's own `ZONE1` block, not `ZONE2`'s: C1/C1C/C2B `ZONE1 FOG_COLOR` 0.69 = **176** =
   the `Colored` value on C1's `o28` skirt/cap/disc and on the whole of C1C's and C2B's shells;
   C4 `ZONE1` **192**; C5 `ZONE3` **16** = its `zone3` shell. Nothing was repainted (`C26`'s ⚠
   stands), the data already agrees.

⚠ **The cap altitudes are dome-local metres and are NOT an altitude the player can measure.** The
dome is centred on the camera and scaled uniformly about it, and it is unfogged, so the metres are
unobservable and the rim ELEVATION is the whole of what a frame shows. Measured on our own render
(C1 river pose, camera pitched +30°, `f` = 599.1 px): the flat cap's edge appears at **48–52°**
elevation against the authored 46.9–48.5° at the centre column, the spread being the 12-gon's own
inradius/circumradius and the off-centre columns' geometry. See `docs/architecture.md`'s
`GameSession.HorizonScaleFor` entry for why `B14` therefore keeps one uniform scale rather than
pinning Y to metric.

### Zone keys

List-valued dict keys:

| Key | Meaning |
|---|---|
| `FOG_COLOR` | fog colour (dual-encoded, above) |
| `FOG_RANGES` | `[near, far]` metres of **horizontal** view distance (the original's fog volume is a vertical cylinder around the camera, not a sphere). A D3D `FOGSTART`/`FOGEND` pair, ramped **linearly** between them, see below |
| `FOG_ALTITUDE` | `[low, high]` metres: full fog at/below `low`, none at/above `high`, the cylinder's vertical fade. C4's `[10000, 11000]` sits above every flyable altitude ⇒ a pure cylinder, full fog at all heights (matches RM's valley haze) |
| `CLIP_RANGES` | `[near, far]` hard clip; `far` kept as informational (the remake's far plane is much larger, fog, not the clip, hides distant terrain) |

#### The `near`-to-`far` ramp is linear

**Confidence: the curve is traced to a field in the data; the render could not discriminate it.**

`FOG_RANGES` is a D3D `FOGSTART`/`FOGEND` pair, and the *mode* is declared beside it in the
gamez: every chapter's `world1` node carries a `World` struct whose `fog_state` field is the raw
u32 **1 = LINEAR** (mech3ax `nodes/src/cs/world/data.rs` **asserts** it, so it is present and
identical in all eight chapters; `0` in that field would mean OFF and `2` EXPONENTIAL). The rest
of that struct, `fog_color`, `fog_range`, `fog_altitude`, `fog_density`, is zeroed, which is
what makes a *written* 1 significant rather than doubtful: the runtime fog parameters come from
this file, and the node declares only the mode. So `csky_fog_amount` ramps
`clamp((d − near) / (far − near), 0, 1)`; it used a `smoothstep` until B15, and that S-curve was
the remake's own invention with nothing behind it.

⚠ **The renders did not decide this, and the record should not pretend they did.** A smoothstep
is *below* the linear ramp for the near half of the range and *above* it for the far half, so it
trades one end for the other and every measured pose splits accordingly: at C3's canyon pose the
smoothstep leaves more terrain unwashed (canyon slope 86 vs 106, vegetation 29 % vs 22 % of the
lower frame, against the original's 36.5 and 47.8 %), while at C1's river pose and on C1B's
moonlit cloud tops the linear ramp reaches further (deck-ceiling texture surviving to 3.7 km vs
3.6 km; cloud p90 187 vs 193 against originals at 155–182). Both are "our fog is still too
strong", at opposite ends, which is a statement about a residual, not evidence for a curve. The
authored field breaks the tie.

**`VIEWING_RANGE` does not scale them.** All eight chapters ship HIGH `FOG_SCALE`/`CLIP_SCALE`
**1.0** (MED 0.85, LOW 0.7), every multiplier in the block is ≤ 1, so nothing in the shipped data
shortens a range. The remake used to halve them with a `fogRangeFactor = 2.0` in `WeatherRig`
("this does not seem to be radius but diameter"); that TUNE predated the fog-colour sRGB fix and
is **deleted**. Measured at C1's river pose, the halved range washed the overcast ceiling to flat
fog at ~1.8 km against the original still's ~12.6 km; the authored range takes that to ~3.7 km.

**The fade is by FRAGMENT altitude, not by the camera's**. Settled at the controls of the original
in **C2**, the only chapter whose flown band sits inside the flight envelope, so the only place the
two readings differ at all: climbing well above `ZONE1`'s 1024 m top, the distant city **"still
dissolves into haze"**. A camera-altitude fade would have switched the fog off entirely up there and
handed the ground back its unfogged luminance, ~100 units away on distant ground, so the observation
is categorical rather than a judgement of degree. That is exactly what `csky_fog_amount` computes
(`CSVM/shaders/csky_atmosphere.gdshaderinc`), and this observation is the whole of the evidence
for it.

⚠ **The old corroboration is retired: "C1/IA1 `zone1` 970→1047 is exactly cloud-band-bottom →
whiteout-centre" belongs to a zone C1 never flies.** C1 flies `zone2` (settled above), whose band
is 4000→5000 m, above the 2500 m flight ceiling, i.e. night fog at every flyable altitude. The
identity itself is real and it is **3/3** across every chapter whose band is reachable at all,
C1 970/1047, C1C 1055/1082.5, C2B 924/1024, each exactly `[CLOUD_COVER BOTTOM,
WeatherState.CloudBandCentre]` — but it is an identity in the **unflown** zone in all three, so it
cannot be evidence for what `FOG_ALTITUDE` does at runtime. It is recorded here as a real and
unexplained property of the authoring, not deleted.

**Un-retired (decompile): `zone1` is not unflown in the deck chapters.** The
"belongs to a zone C1 never flies" reading assumed one static zone per mission; the decompile
[settles that a deck chapter flies both zones, switched by camera altitude at runtime](#zone-selection-at-runtime),
below the deck the camera is in state 1 and wears `ZONE1`'s fog, `zone1`'s dome geometry is what
renders as the ceiling (see the correction on the deck's two-object mechanism, above), and
`zone1`'s `FOG_ALTITUDE` pair is therefore live, flown data during below-deck flight in C1, C1C
and C2B, not an inert property of geometry the camera never reaches. The 3/3 identity (band
BOTTOM = `WeatherState.CloudBandCentre`) stands as originally measured; only its "unflown, so
inadmissible" qualifier is retracted.

⚠ **Exactly one flown band in the whole install is inside the flight envelope, so exactly one
chapter exercises the altitude term.** Every other chapter's flown zone authors 4000–5000 m
(C1, C1C), 9000–10000 m (C2B, C3, C5) or 10000–11000 m (C1B, C4), all above the 2500 m ceiling,
where the term is a constant 1.0 and `FOG_ALTITUDE` is inert. Only **C2 `ZONE1`'s 256–1024 m** can
tell one altitude rule from another. A degenerate census is a fact about the instrument, not about
the question (`INSTR-7`): measure any change to the altitude term in C2, and do not tune it
against a scene that cannot exercise it.

## World lighting

Each zone also carries a `SUNLIGHT_*` block, the directional light the original uses to
light the baked-vertex world. List-valued keys:

| Key | Meaning |
|---|---|
| `SUNLIGHT_ACTIVE` | `[1]`/`[0]`, **`1` in every `ZONE*`, `0` in every `SW_ZONE*`, all 212 blocks install-wide**, so on the hardware path (ours) the light is unconditionally on |
| `SUNLIGHT_ORIENTATION` | `[pitch, yaw, roll]°` sun **shading** direction, consumed, see below |
| `SUNLIGHT_DIFFUSE` | directional intensity (`[0.4]`…`[2.0]` across the install) |
| `SUNLIGHT_AMBIENT` | ambient floor (`[0.15]`…`[0.6]`) |
| `SUNLIGHT_COLOR_DIFFUSE` / `SUNLIGHT_COLOR_AMBIENT` | light colours (usually white) |
| `SUNLIGHT_STATIC` / `SUNLIGHT_BICOLORED` | flags |

`DIFFUSE`/`AMBIENT` **vary per mission and track the scene**: C1B night `0.6 / 0.15`
(dim), C1/IA1 overcast `1.2 / 0.25`, C1C day missions `2.0 / 0.6` (bright). This is the
data source for the remake's **world-brightness calibration** (item 6): the remake renders
the world fullbright (texture × baked vertex colour), which is *brighter* than the original
because the original also modulates by this SUNLIGHT. The original's directional term,
averaged over the predominantly up-facing world (ground + cloud deck), collapses to a
per-mission scalar `WorldLight = clamp(AMBIENT + DIFFUSE·k, 0.15, 1)`, with `k ≈ 0.46` the
average up-facing sun incidence, **one TUNE constant** calibrated to the C1/IA1 reference
(`OriginalScreenshots/C1 IA1 Zone1 environment Spawn3.png`: overcast deck 210→169, terrain
→~57). It then self-scales from the data: C1/IA1 → 0.80, C1B night → 0.43, C1C day →
clamp 1.0. **Matched-pose footage supports the calibration** (`CAP-11`, at
0.426 / 0.784 / clamp 1.0, C1B terrain −12%, C2B deck tops −9%, C2 suburb +5–15%;
`git log --grep=BL-110`, evidence `playtest/CAP-11/README.md`). One exemption the original applies
that we do not: water renders unmodulated (`BL-304`). A second reading, that night cloud sprites are
directionally moonlit rather than uniformly dimmed (`BL-325`), is open on its footage but cannot
come from the lighting gate: C1B's clouds are placed `cloudparent` facades and every one of them in
every deck chapter is authored `lighting: false`, so the original's sun reaches none of them
([`../org/vertexLighting.md`](../org/vertexLighting.md)). `Weather.WorldLightFactor` computes it (`ZoneWeather.WorldLight`); `WeatherRig` sets
the global shader scalar `csky_world_light`, **linearised** first, so the shader's
linear-space `ALBEDO ×` lands the dimming in gamma space (matching the DX7 chain
texel×vertex×light, all sRGB-space; a raw linear ×0.80 only reaches 210→190, gamma-space
lands 210→169). Applied before the fog mix, so `FOG_COLOR` is unaffected. Paired with the
**gamma-space vertex modulate** (the other item-6 half, see `SceneBuilder.cs`), which fixes
the terrain's washed-yellow → saturated-green hue independent of brightness.

### `SUNLIGHT_ORIENTATION` - the shading direction

Read per zone into `ZoneWeather.SunOrientation` and written to the world's one
`DirectionalLight3D` by the same zone-apply that writes the fog, so it follows a zone change
(`WeatherRig.ApplyZone`). It varies by chapter and is adopted with **no TUNE**:

| Chapter | `[pitch, yaw]°` |
|---|---|
| C1 | `[-25, 90]` |
| C1B / C1C / C2 / C2B | `[-65, 90]` |
| C3 | `[-25, 135]` |
| C4 | `[-45, 135]` |
| C5 | `[-25, -135]` |

Effectively constant across a chapter's zones, **C2/MP2 and C2/MP3 are the only two files in the
install whose `ZONE1` (−65°) and `ZONE2` (−25°) pitch differ**, which is why the in-engine
`sun-orientation` suite flies exactly that mission: anywhere else a zone change moves the light
from a value to the same value and would pass with the write deleted.

The mapping needs **no conversion**. The binary multiplies these degrees by `0.017453292`
(`FUN_004bc3e0`) and writes the three radians into an ordinary gamez node rotation triple via
`zclass\Light.c`'s setter (`FUN_004dc610`), on a **named `sunlight` Light node** that ships in all
eight chapters' gamez (`active: false`, parentless, no model, resolved by
`FUN_004d0280(0xa, "sunlight")`). We already read gamez node eulers as
`Basis.FromEuler(v, EulerOrder.Yxz)`, Godot's default order is YXZ, and a directional light shines
along local −Z, which reproduces the original's own euler→direction helper `FUN_0053c610`
(`(-cos p·sin y, sin p, -cos p·cos y)`) exactly.

⚠ **It is the shading direction, not the sun's position.** The gamez `sun` billboard the lens
flare anchors to is a *different node* and disagrees, C3 authors yaw 135 while its `sun` sits at
yaw 45. Nothing in the binary links the two, so the original disagrees with itself and we
reproduce that rather than reconcile it (`WORLD-26`, `BL-165`).

⚠ **Shadows do not follow it.** A top-level `SHADOW_ANGLES` key outranks the sunlight direction in
the original's shadow renderer (`FUN_0049d0a0`), and all 53 shipped files author
`[-90, 0, 0]` → straight down; `SHADE_ANGLES`, the parser's second key, appears in none. The
ground shadow CSVM draws therefore projects straight down whatever the sun bearing is, and reads
this key not at all (`org/shadows.md`).

⚠ **It shades aircraft only.** The world is fullbright, so an unshaded surface takes neither light
nor shadow from this, which is why the eight `--freecam` goldens did not move when it landed and
the three flight goldens did. Its *intensity* is still hardcoded (`BL-332`).

⚠ **It does not reach every surface, and that is the data's decision, not a special case.** A
model authored `flags.lighting: false` skips the multiply entirely, the original turns
D3D lighting off for it, so it draws at full brightness ([gamez.md](gamez.md)). That is 3,003 models
install-wide: the sprite cards, clutter trees, glows, effect meshes, lit signage and the whole
skydome. Light-source glow flares were already exempt by a hand-rolled rule; the flag turns out to
agree with it, and now covers the rest. The visible consequence is that a night mission's clouds,
splashes and beacons stay bright while its terrain and water still dim.

*Caveat:* `k` rests on the single C1 overcast reference; the night/day self-scaling is a
principled prediction pending a matched C1B-night and a bright-day original to confirm/refine
the constant.

## Atmosphere controls

See [weather atmosphere controls](weather/atmosphere.md) for cloud cover, wind, and precipitation.

## Evidence & limits

This page states current format facts. Claim-specific evidence and limits remain beside the claims they support.
