# Enhanced Graphics 2, the temporal pass, lit explosions and lit clouds

**ACTIVE PLAN** (written 2026-09-16). It sits in `docs/`, which by this repo's convention makes it
a live plan; PROJECT_CONTEXT.md's "Current status" names it. When every item lands, the closing
commit deletes this file, records the completion in its message, and clears the "Current status"
pointer; any live prose linking this file by path is unlinked in the same commit.

The opt-in Enhanced mode (`GraphicsMode.Enhanced`, `--graphics=enhanced`) today lights the world
from the authored sun and ambient, mirrors the committed world lights onto real omni lights, casts
4-split soft shadow maps, runs SSAO, blurs a screen-space reflection on water, blooms only the
glow-arm sprites and tonemaps with AgX (`CSVM/src/Session/Launch/Launcher.cs` `SetupLighting`). Clouds are
still the original's flat, unshaded sprite cards and explosions are still unlit puffer billboards.
This plan adds four things to that stack, all behind the same switch: a temporal anti-aliasing pass
plus a render-scale display setting (Wave A), lit explosions (Wave B), lit cloud cards and volumetric
cloud banks (Wave C), and an enhanced golden set with a perf reading over the finished stack
(Wave D). `BL-803`, the whole-screen dither reported under Enhanced, is folded into Wave A because
Godot resolves SSAO and soft-shadow penumbrae with screen-space noise that only a temporal pass
integrates away, so TAA is the first attempt at it and the bisect is the fallback. Session
`orch-7` was asked on 2026-09-16 not to pick `BL-803` up.

Out of scope: any change to the faithful presentation, which stays bit-identical and keeps every
existing golden (the switch is the one gate on every item here); FSR 1.0/2.2 as an upscaler for
weak GPUs (this plan spends GPU headroom, it does not save it); the parked 4x texture upscaling
(branch `upscaling`); `BL-322`'s C5 facade brightness, cited as context and left to its own item;
the aircraft specular constant; menu presentation work beyond the one new VIDEO row.
Backlog items drawn in: `BL-803` was read in `backlog.md` this session and not re-verified against
the record or the code; `BL-325` (moonlit night clouds) is cited by C21 and not closed by it.

## Milestone goal

- Under Enhanced the image is temporally anti-aliased, the SSAO and penumbra noise is gone, and a
  Render Scale row on the VIDEO page lets a bored GPU render above native.
- A rocket or bomb burst lights the ground and the aircraft around it, its fireball blooms, its
  smoke reads lit by the sun, the air over it shimmers and the hit leaves a scorch.
- The cloud field reads lit: sun-side rims, shadowed undersides, no hard cut where a card meets
  terrain, and a soft volumetric bank under the cards for scatter and sun shafts.
- Speed and direction changes are felt: streaks stream past the camera with speed and G, and the
  chase camera trails the nose through a roll and widens with speed.
- A small `--graphics=enhanced` golden set pins the stack, and a perf reading shows the whole of it
  within 20% of the D31 baseline.

**Every item rides `GraphicsMode.Enhanced` and moves no faithful pixel.** The faithful path is the
project's deliverable; Enhanced is the remake-only arm that may invent. Render Scale is the one
exception, a display setting like V-Sync, and it is ignored under `--det` so no golden sees it.

## Decisions (2026-09-16)

| # | Question | Decision |
|---|---|---|
| 1 | Which features does the plan cover? | **TAA + BL-803, Render Scale, alpha-to-coverage, lit cloud cards + FogVolume banks, and all five explosion pieces** (burst light, bloom, sun-shaded smoke, heat shimmer, scorch decals). |
| 2 | Which AA and resolution stack? | **Godot TAA under Enhanced plus a bilinear Render Scale 1.0 to 2.0**; FSR 2.2 evaluated once (A5) as the alternative temporal pass and kept only if it reads better. Godot's FSR modes only upscale and FSR 2.2 replaces TAA, so they cannot supersample. |
| 3 | What gates each piece? | **TAA and alpha-to-coverage under the Enhanced switch; Render Scale a display setting on the VIDEO page in both modes**, default 1.0, ignored under `--det`. |
| 4 | How is BL-803 sequenced against TAA? | **Land TAA, the user judges, bisect only if the pattern persists** (A3). TAA is wanted regardless. |
| 5 | How far does the cloud work go? | **Cards lit first (C21), FogVolume banks second (C22), the cards stay.** Judged separately on C1 by day and C5 at night. |
| 6 | Which cloud sprites? | **The authored fvol masks first; a rendered puff set is its own follow-on item (C23)** picked by montage. |
| 7 | Which explosion pieces? | **All five**, each its own item with its own judgement. |
| 8 | How is a look item verified and closed? | **The user's eyes at the controls close each item; a separate enhanced golden set (D31) is the regression net.** No luminance metric gates a close. |
| 9 | Perf budget? | **The finished stack holds the D31 Enhanced frame time within 20%** on C4/C5/C3 at 4 panes, render scale 1.0. Render scale above 1.0 is the user's own spend. |
| 10 | Wave order and pacing? | **A, B, C, E, D. A wave halts on the user's flight, and the next wave starts as soon as it has no dependency on the halted one.** B, C and E need A1 landed (they are judged through the same temporal filter) but not A's verdict; D needs everything. |
| 11 | The feel of speed and of direction changes (Wave E)? | **Wind streaks past the camera and a chase camera that lags the nose and widens its FOV with speed, both under Enhanced.** Wingtip vapour is out (too much for a prop plane); radial motion blur is out (it competes with the temporal pass for sharpness). |
| 12 | What happens to the authored speed-cue wisps under Enhanced? | **They stay; the streaks are added over them.** The wisps are data (`speed_cue.zrd`), judged right on the faithful path (`git log --grep=BL-867`); whether Enhanced dims them under the streaks is a TUNE the user judges in E41's montage, not a decision made here. |
| 13 | Do the trees, rails and lattice that now blend rather than scissor change the plan? | **Yes: A4 covers only what stays scissored beside them**, which is 332 of the census's 388 cut names. Blended surfaces have no coverage edge to fix. |

## ⚠ Read this before implementing anything

| # | The wrong claim | How it died |
|---|---|---|
| 1 | "FSR super-resolution can render above native for a GPU with headroom." | Godot's `scaling_3d_scale` above 1.0 is bilinear only; FSR 1.0 and 2.2 accept scales at or below 1.0, and FSR 2.2 disables Godot's TAA in favour of its own temporal pass. Supersampling here is bilinear, and FSR 2.2 is an alternative AA, not an upscaler for this plan. |
| 2 | "The BL-803 dither is a deliberate dither somewhere in the enhanced stack." | `backlog.md`'s own entry: nothing in `SetupLighting` asks for one, debanding is off, and the clutter fade dithers only its own fragments. The candidate is Godot's temporal noise in SSAO and the soft shadow pass, which TAA integrates. Still a hypothesis until A1 lands and the user flies it. |

| Confidence | Items | What that means for you |
|---|---|---|
| **Traced to an exact mechanism in code, with the data that proves it** | A2 (where the viewports are built), B12 (the glow threshold contract) | Confirm the trace, then implement. |
| **Direction sound, magnitude a judgement call** | A1, A4, B11, C21, D31, D32 | The *what* is settled; every energy, radius, tint and offset is TUNE, judged by the user, never invented as fact. |
| **Leads only, no mechanism yet** | A3, A5, B13, B14, B15, C22, C23, E41, E42 | Budget for investigation; A3 and A5 may end in a disproof or a park. |

**⚠ Worktree hazard.** `git stash` is repo-global and shared across worktrees, never use it in a
worktree session here; use a local commit or a file copy.

## What the data actually ships

- **The enhanced Environment and sun.** `Launcher.cs` `SetupLighting` (lines 1497 to 1543 at
  writing): `EnableSunShadows` (4 splits, angular distance 2.0, blur 1.0), SSAO (radius 2.5,
  intensity 2.0, power 1.5, horizon 0.06, sharpness 0.98), `EnableWaterReflections` (SSR 64 steps),
  `EnableGlowAndTonemap` (HDR threshold 1.0, bloom 0.0, intensity 0.9, strength 1.1, screen blend,
  HDR scale 2.0, luminance cap 8.0, AgX white 6.0). Every constant is `TUNE` with its own comment.
- **Anti-aliasing today.** `CSVM/project.godot` sets `anti_aliasing/quality/msaa_3d=2` (4x MSAA)
  for both modes. `CockpitOverlay.cs:133`, `SpyglassView.cs:45` and `SplitScreen.cs:259` copy that
  project setting onto their own `SubViewport`s, so any viewport-level pass has four places to
  reach. No code reads `use_taa`, `screen_space_aa` or `scaling_3d_*`.
- **The capture trap.** `Tooling/CaptureDirector.cs:109`: a `--shots` image is the previous
  frame's render; `_00` is un-jittered and `_01+` carries the burst camera's dither. A dither
  verdict comes from the controls or an undithered capture, never from a burst frame.
- **World lights under Enhanced.** `Mech3/WorldLights.cs`: `Begin`/`Add`/`Commit` per frame, and
  in Enhanced mode `Commit` mirrors the committed set onto a growing pool of `OmniLight3D`
  (`ShadowEnabled = false`, `OmniAttenuationTune`), energy from the colour's peak channel. This is
  the pool a burst light joins.
- **The explosion sprites.** `Effects/EmitterRenderer.cs` `MultiMeshEmitterRenderer`: one shader
  per (blend, soft) pair, `unshaded`, blend per atlas column off the texture's own additive bit,
  `ALBEDO = t.rgb * srgb_to_linear(COLORS ramp)`, quad-rim fade, soft-particle depth fade,
  mission fog. The additive columns are what B12 lifts over the glow threshold; the mix columns
  are the smoke B13 shades.
- **The cloud field.** `Effects/FogVolumeClutter.cs` `ShaderCode(lit, fogged)`: one static
  MultiMesh per sprite kind, hand-billboarded, `unshaded`, `depth_draw_never`, no depth fade; the
  sprite's own polygon normal is in `INSTANCE_CUSTOM.xyz` (encoded `[0,1]`) and its fade draw in
  `.w`; colour is the authored per-vertex 240 and the texture a constant-RGB alpha mask, and the
  comment at line 225 forbids scaling it on the faithful path (`docs/org/cloudCards.md`).
- **The reference technique** (Zess-57, r/godot "Particle/Multimesh billboard clouds showcase",
  saved at `.scratch/RedditClouds/`): one MultiMesh of unshaded billboards, five rendered puff
  textures picked by `INSTANCE_ID`, sun tint `vec3(1.6 + sn * 1.2)` with `sn = dot(viewDir, sunDir)`,
  and a transmission rim: the alpha of a blurred sample (`textureLod(..., 3.0)`) at
  `UV + (sunDir · billboard right, -sunDir · billboard up) * scatter_distance` tints the albedo by
  `(1 + f2)`, `f2 = -a2 * 0.6 + (sunDir · billboard forward) * 0.4 + 0.4`. The author measured
  a 0.923 frame-time factor at a sky-filling view.
- **The perf baseline (D31, `git show 8aab7fa3`).** `RunTests.ps1 -Graphics enhanced` appends
  `--graphics=enhanced` to the perf and hitch launches only. Targeted C4, C5 and C3 probes at 4
  panes, frame_ms original vs enhanced: C4 10.40 vs 15.04, C5 11.00 vs 13.60, C3 9.15 vs 12.51.
  That table was provisional against Wave E of the first plan and is re-taken by D32 before the
  20% is applied.
- **The golden set.** `analysis/goldens/manifest.json` runs every shot under `--det --mute`, and
  under `--det` only the `--graphics=` flag reaches `GraphicsMode.Resolve`, so an enhanced shot is
  one more manifest entry with the flag in its args.
- **The speed cue as shipped.** `Flight/Hud/SpeedCue.cs` loads each chapter's `speed_cue.zrd`
  verbatim: three `cuepufferN` states picked by camera altitude, emitted 60 m ahead of the player
  and left in world space for the aircraft to pass (`docs/formats/effects.md` "Aircraft speed-cue
  wisps"); off within 50 m of the ground. Their opacity was judged right at the controls
  (`git log --grep=BL-867`); their lateral spread already widens with the pane's aspect over 4:3, and the opacity
  is what the item still owes. The original authors no other speed cue: the
  `high_speed` shake runs only above rated max speed and the `rattle` sound is speed-keyed
  volume (`docs/org/shakes.md`).
- **The chase camera's decoded law.** `docs/org/cameraViews.md` "The external camera's distance":
  distance grows with speed and carries an acceleration transient (`dist_vary`, `dist_catch_up`),
  ported as `CameraController.ExternalRadius`/`UpdateDynamics`/`DistTransient`. Nothing in the
  decoded law lags the camera's orientation behind the nose or moves the FOV with speed; the
  external FOV is one constant (`CameraController.cs:142`).
- **A camera-centred streak field exists.** `Effects/Precipitation.cs`: one MultiMesh whose shader
  derives each quad's position from a per-instance seed, `csky_time` and the camera position,
  wrapped into a camera-centred box, with a near fade and a rim fade; rain already draws stretched
  streaks (`MakeStreakTexture`, `streak_size`). Its own header notes a plane-relative streak look
  as an unauthored TUNE follow-up.
- **The night-cloud measurement (`BL-325`, CAP-11 C1B).** Cloud cores near the moon p90 218, the
  away side p90 70, our uniform WorldLight rendered p90 102. The lit cards of C21 are the arm that
  can answer it, under Enhanced only.

## Ground rules

- **Original-game data drives everything.** Read the reader/compiled JSON before writing a handler;
  never guess a value. Inventing content is the trap this project falls into most often.
- **Evidence is a lead to verify, not a finding to implement.** Confirm every claim against the
  data/code before building on it; **a correct disproof that lands no code is a success here**, not a
  failure. Mark each item's Evidence with its confidence (traced-to-code / direction-sound-magnitude-
  TUNE / lead-only).
- **`PROJECT_CONTEXT.md` + the module's entry in `docs/architecture/<Namespace>.md` (plus its index
  bullet in `docs/architecture.md`) / `docs/formats/` are updated in the same turn** as each landed
  item; a landed item gets its record in the landing commit's message and is **deleted** from
  `backlog.md` (not marked FIXED there). New decodes land with their `docs/formats/` page.
- **Read `docs/verification.md` before measuring anything**, the instruments here mislead; cite the
  rule that bites per item.
- **Verify against a full 8-chapter `--freecam --chapter=<X>` regression** (zero errors, same
  mesh/node counts unless the change is meant to add coverage) plus a targeted capture at the
  location the report came from.
- **Read the module's entry in `docs/architecture/<Namespace>.md` (found through the index in
  `docs/architecture.md`) before modifying it,** then the comments on the members you touch; dead
  ends are in the landing commits (`git log --grep=<ID>`), so search those before re-chasing one.

## Checklist

Statuses: ☐ open · ◐ in progress · ☑ done · ❌ closed/disproven. **Keep this in sync as items land.**

### Wave A, the temporal pass and the render scale

1. ☐ TAA on every 3D viewport under Enhanced
2. ☐ Render Scale, a VIDEO page row applied to every 3D viewport, ignored under `--det`
3. ☑ BL-803 closes on the flight after A1, or the bisect doors land and find the pass
4. ☐ Alpha-to-coverage on the cutout surfaces under Enhanced
5. ☐ FSR 2.2 tried once as the alternative temporal pass, kept or parked on the user's verdict

### Wave B, lit explosions

11. ☐ A burst omni light at each fireball, from the enhanced pool, flickering down over the burst
12. ☐ The additive fireball frames bloom
13. ☐ Sun-shaded smoke billboards
14. ☐ Heat shimmer over a fireball
15. ☐ Scorch decals at a hit

### Wave C, lit clouds

21. ☐ Lit cloud cards: sun tint, transmission rim, shadowed undersides, soft depth fade
22. ☐ FogVolume banks from the authored fvol slabs
23. ☐ A rendered puff sprite set under Enhanced, picked by montage

### Wave E, the feel of speed

41. ☐ Wind streaks past the camera, keyed to speed and G, over the authored speed cue
42. ☐ The chase camera lags the nose through a roll and widens its FOV with speed

### Wave D, the net and the reading

31. ☐ An enhanced golden set under `--det`
32. ☐ The perf reading over the finished stack, within 20% of the D31 baseline

## Dependency and parallelism notes

A1 lands before any look item is judged, since B and C are seen through the same temporal filter;
B and C do not wait for A's verdict (A3, A5). A1 and A2 both touch the four viewport construction
sites (`Launcher`, `CockpitOverlay`, `SpyglassView`, `SplitScreen`), so they run in sequence, not in
parallel worktrees; A4 touches shader code in `SceneBuilder`/`Clutter` and can run beside A2. A3
depends on A1's flight. A5 depends on A1 and A2 and is judged against them. A4 needs a tree forked
after the tree, rail and lattice families went from scissor to blend, since that change removes
most of A4's population.

B12 and B13 both edit `EmitterRenderer.cs`'s one shader: sequential. B11 touches `WorldLights.cs`
and the effect sink, B14 adds its own material, B15 touches the crater/decal path; those three can
run in parallel worktrees with that file ownership. C21 owns `FogVolumeClutter.cs`; C22 is a new
module and can run beside C21; C23 depends on C21's shader. E41 is a new `Effects` module and
E42 owns `CameraController.cs`; they run in parallel with each other and with Wave C, after A1.
E41 must not touch `SpeedCue.cs` or `Puffer.cs`, the faithful path's wisps. D31 and D32 depend on everything,
and D32's baseline re-take (the D31 table was provisional) is taken at Wave A's start so the 20%
has a current denominator.

Wave pacing (decision 10): a wave halts on the user's flight, the next wave starts when nothing in
it depends on the halted one.

---

# Wave A, the temporal pass and the render scale

## A1 ☐ TAA on every 3D viewport under Enhanced

**Goal.** Under Enhanced the world, cockpit, spyglass and split-screen panes are temporally
anti-aliased; edges, specular on water and the SSAO/penumbra noise are stable frame to frame. The
faithful path is unchanged.

**Evidence (confidence: direction-sound).** `project.godot` sets 4x MSAA for both modes, and
`CockpitOverlay.cs:133`, `SpyglassView.cs:45` and `SplitScreen.cs:259` copy that setting onto their
SubViewports; nothing reads `use_taa`. Godot's TAA is a `Viewport` property, so the same four sites
carry it. That it also removes the BL-803 pattern is the hypothesis A3 settles.

**Approach.** Read `GraphicsMode.Enhanced` once where each viewport is built (the root viewport in
`Launcher` after `Resolve`, the three SubViewports at construction) and set `UseTaa = true` there;
MSAA stays as it is (Godot allows both). Do not set it in `project.godot`, which would reach the
faithful path. Watch the billboard populations (puffer, clouds, clutter's dithered fade) and the
HUD sprites for ghosting; the clutter fade's own dither is the likely victim, and if it smears,
that is a finding for A3's record, not a reason to drop TAA.

**Model recommendation.** medium: four mechanical sites plus a flight to judge.

**Verify.** `--graphics=enhanced --chapter=C1` at the controls, chase view, a low pass over the
lake and a bank over the city; then the faithful `--freecam` goldens unchanged (the sweep must
report zero movers, which proves the gate). A `--shots` capture cannot judge this (`CaptureDirector.cs:109`).

**⚠ Traps.** A burst screenshot carries the previous frame and the jitter. The cockpit pass
duplicates the Environment but not the viewport flags, so its SubViewport needs its own `UseTaa`.
`--det` runs keep the flag (it is under the mode, and D31 pins enhanced goldens through it), so an
enhanced golden must be pinned with TAA settled, not before.

## A2 ☐ Render Scale, a VIDEO page row applied to every 3D viewport, ignored under `--det`

**Goal.** A "Render Scale" row on the VIDEO page of both presentations (100% to 200%) renders the
3D viewports above native and downsamples bilinearly; the setting is saved with the other display
settings and takes effect on the next start like Enhanced Graphics.

**Evidence (confidence: traced).** The VIDEO page rows are the `OriginalOptionsScreen.cs` table at
lines 361 to 386 (Monitor, Resolution, Display Mode, V-Sync, Enhanced Graphics), each a
`DisplaySettingRows` helper over an `OptionsStore` field; `BL-783`'s close put the same four
settings on the built-in presentation (`LaunchMenu.cs`). Godot's `scaling_3d_scale` accepts up to
2.0 in bilinear mode only, per the disproven-claims table. The four viewport sites are A1's.

**Approach.** A `RenderScaleSetting` in `Utils` modeled on `VSyncSetting` (word list 100/125/150/175/200,
default 100, `Resolve` off the saved option then a `graphics.renderScale` config key), an
`OptionsStore` field, one row in each presentation's table, and `Scaling3DScale` set on the four
viewports with `Scaling3DMode = Bilinear`. Under `--det` the saved option is not passed, exactly as
`GraphicsMode.Resolve` documents for its own saved option. Announce it on the `[world] graphics
mode:` log line. `<TODO: where V-Sync is applied to the window at launch, to place the viewport
write beside it>`

**Model recommendation.** medium.

**Verify.** `MenuOriginalSuites` and `DisplaySettingsSuites` gain the row (they count the VIDEO
titles and round-trip the store); at the controls, 200% on C5 visibly sharpens the skyline and the
`[perf]` frame time rises; `--det` goldens unchanged with a saved 200%.

**⚠ Traps.** Do not touch `msaa_3d`. The spyglass and cockpit SubViewports have their own sizes;
scale them the same way or the panes disagree in sharpness. The row is a display setting and is
NOT under the Enhanced switch (decision 3).

## A3 ☑ BL-803 closes on the flight after A1, or the bisect doors land and find the pass

Taken by the bisect route rather than the flight, so it does not wait on A1. The doors
`--no-ssao`, `--no-ssr`, `--no-glow` and `--no-soft-shadows` landed in `SessionSpec` and named the
sun's penumbra filter: it alone resolves through a screen-space pattern, over 81 % of the C1
waterfall frame. Raising the directional soft-shadow filter to its top rung removed 86 % of the
excess and held the judged penumbra width. `analysis/screen-dither/FINDINGS.md` holds the
instrument and the numbers; A1's TAA is no longer what this item waits on. What is still owed is
the look at the controls, which no instrument replaces.

**⚠ Traps.** Do not bisect on `--shots` frames. Debanding is off and is not the cause. Do not
widen the fix to the faithful path, which is not reported to show it.

## A4 ☐ Alpha-to-coverage on the cutout surfaces under Enhanced

**Goal.** Alpha-scissor edges (clutter, fences, lattice geometry, decals with cutout masks) resolve
through the MSAA samples instead of a hard 1-bit edge, under Enhanced only.

**Evidence (confidence: direction-sound).** MSAA does nothing for an alpha-scissored edge; Godot's
`alpha_to_coverage` render mode hands the alpha to the coverage mask. `SceneBuilder.GetBiasShader`
and `Clutter.ShaderCode` build their shader keys with an Enhanced bit already (`SceneBuilder.cs:1528`,
`:1724`, `:1819`), so an Enhanced-only render mode is one more key bit. ⚠ The tree, rail and
lattice families (56 textures) blend rather than scissor, in `TextureArchive` and `Clutter` alike,
because the original never alpha-tests; they have left this item's population, and so have the
coastline sheets. A4 is not moot: 332 of the census's 388 scissored names are outside those
families (`analysis/alpha-classification/`), among them the crane girders (`crane05`,
`cranesteel3`), `rope_bridge`, `lightpole` and the sign cards.

**Approach.** Add `alpha_to_coverage` (or `alpha_to_coverage_and_one`) to the cutout arms of those
shaders when `GraphicsMode.Enhanced`, through the existing key bits so the faithful shader text is
byte-identical. Judge on a surface that still cuts, the C5 lamp posts and C4's crane, not on a
fence or a tree.

**Model recommendation.** medium.

**Verify.** Enhanced flight past a C5 lamp line and C4's crane; faithful goldens zero movers;
the alpha-cutout ray suites still pass (they test the collider, not the pixel, so they must not
move).

**⚠ Traps.** Alpha-to-coverage with TAA can double-soften an edge; judge A4 with A1 on. The
clutter fade dithers alpha on purpose; coverage on a dithered alpha reads as a different pattern,
so try it with and without the fade arm.

## A5 ☐ FSR 2.2 tried once as the alternative temporal pass, kept or parked on the user's verdict

**Goal.** A recorded verdict on whether FSR 2.2 at scale 1.0 reads better than Godot's TAA under
Enhanced; one of the two ships.

**Evidence (confidence: lead-only).** FSR 2.2 carries its own temporal AA and disables Godot's TAA
when selected; at scale 1.0 it is an AA pass, not an upscaler (disproven-claims row 1). Its
sharpening and its ghosting on billboards differ from TAA's and only a flight tells.

**Approach.** A `graphics.temporal=taa|fsr2` config key read once beside `GraphicsMode`, defaulting
to whichever A1 shipped; the user flies both on C1 and C5; the loser is removed, not left as a
second door. If FSR 2.2 wins, A2's Render Scale stays bilinear above 1.0 and FSR 2.2 applies only
at 1.0, which the setting's comment must say.

**Model recommendation.** medium, low effort: two flags and a flight.

**Verify.** The user's verdict on a side-by-side at the controls; the closing commit records it.

**⚠ Traps.** FSR 2.2 needs motion vectors from every billboard shader that moves its vertices by
hand (puffer, clouds, clutter); those hand-rolled billboards give wrong vectors and smear. That is
likely the deciding defect, so look for it first.

# Wave B, lit explosions

## B11 ☐ A burst omni light at each fireball, from the enhanced pool, flickering down over the burst

**Goal.** A rocket or bomb burst lights the terrain, buildings and aircraft around it for the
fireball's life, brightest at ignition and flickering down; the faithful path draws no light.

**Evidence (confidence: direction-sound).** `WorldLights.cs` already mirrors committed lights onto
an `OmniLight3D` pool in Enhanced mode (`Begin`/`Add`/`Commit`, energy from the colour's peak
channel, `OmniAttenuationTune`). The effect chain that spawns the fireball is
`EffectCatalogue`/`AnimRuntime.PlayEffectAt` through `EffectSink` (`docs/org/ordnanceTypes.md`),
and the upper-ring rule (`Projectile.cs:601`) shows how an Enhanced-only rule is threaded through
that sink without touching the faithful spawn.

**Approach.** An `EffectSink` hook at the fireball spawn that, under Enhanced, registers a short-lived
light source (position, colour off the fireball's authored ramp, range TUNE, life equal to the
fireball's) with `WorldLights` so it rides the same `Add`/`Commit` and the same pool; the energy
envelope is a TUNE curve (ignition peak, a few Hz flicker, decay to zero) kept in one constant
block with the other Enhanced TUNE values. No new node type; the pool grows as it does today.

**Model recommendation.** high: it crosses the effect sink, the light pool and the per-frame commit.

**Verify.** `--chapter=C1 --graphics=enhanced --fire` at dusk over tarmac at the controls, a rocket
hit beside a hangar: the hangar wall and the ground flash and settle. Faithful goldens zero movers;
`WorldLights.LogOnce` count rises by the live bursts only.

**⚠ Traps.** The pool has no shadows and must stay that way (cost). A light that outlives its
fireball reads as a bug; tie the life to the emitter's, not a timer. Do not touch the fireball's
own sprite here; that is B12.
⚠ **The authored burst light now draws on both presentations.** The world-effects runtime submits
`he_ground_effect`'s `he_light`/`he_light1` ramp (4 to 20 m at ignition, a 104 to 320 m plateau, 400 m
at its last frame) into the world's `WorldLights` through `AddSource`, so the faithful path is no
longer dark and the Enhanced path already carries that ramp as an omni. Whether this envelope still
layers over it or retires is the user's call; a merge must re-pin the `burst-light` suite to both.

## B12 ☐ The additive fireball frames bloom

**Goal.** The fireball's additive frames exceed the glow threshold and bloom under Enhanced; smoke
and every mix-blend frame stay below it.

**Evidence (confidence: traced).** `Launcher.cs` `EnableGlowAndTonemap`: threshold 1.0, and its
comment states the contract that only the glow-arm sprites (C21 of the first plan) exceed 1.0.
`EmitterRenderer.cs` picks the blend per atlas column off the texture's additive bit and writes
`ALBEDO = t.rgb * srgb_to_linear(ramp)`, which never exceeds 1.0, so today no fireball blooms.

**Approach.** In the additive shader variant only, under Enhanced (a shader key bit as the
world shaders do), multiply `ALBEDO` by a TUNE `enhanced_additive_gain` so the bright core crosses
1.0 and the falloff does not; the HDR scale and luminance cap already bound it. Extend the
threshold comment's contract to name the additive puffer columns.

**Model recommendation.** medium.

**Verify.** The same C1 hit as B11 at the controls: the core halos, the smoke does not. Faithful
goldens zero movers (the gain is under the mode).

**⚠ Traps.** A gain on the whole additive column blooms the tracer and muzzle sprites too if they
share the additive bit; check the `Puffer` population first and gate by texture if they do. Screen
blend keeps the halo additive; do not switch the blend mode to get more glow.

## B13 ☐ Sun-shaded smoke billboards

**Goal.** Mix-blend smoke reads lit by the sun: brighter on the sun side of each puff, darker on the
far side, under Enhanced.

**Evidence (confidence: lead-only).** The smoke columns are the mix-blend variant of
`EmitterRenderer.cs`'s shader, `unshaded`, one colour ramp per emitter. The reference technique
(data survey) grades an unshaded billboard by the sun direction projected into the billboard's own
right/up axes; the emitter shader already has the billboard basis in `vertex()`.

**Approach.** In the mix variant under Enhanced, a gradient across the quad from the sun direction
projected into billboard space (a TUNE amplitude), plus the same transmission rim as C21 if it
reads well on the smoke masks. The sun direction global is the one `WeatherRig` already writes for
the lit world (`<TODO: name of the sun-direction global, or add one beside csky_world_light>`).

**Model recommendation.** high: shader look work judged by eye.

**Verify.** A C1 bomb hit at low sun at the controls, smoke column drifting; a montage of gradient
amplitudes for the user to pick.

**⚠ Traps.** Sequential with B12 (same shader). The smokeball sits on the ground with soft
particles disabled; keep that arm's exemption.

## B14 ☐ Heat shimmer over a fireball

**Goal.** The air over and behind a fireball refracts for its life under Enhanced.

**Evidence (confidence: lead-only).** No refraction pass exists; Godot's `screen_texture` sampling
in a spatial shader with a noise-driven UV offset is the ordinary way and costs one extra
transparent quad per burst.

**Approach.** One billboard quad per fireball spawn under Enhanced, a shader that samples the
screen texture with an animated noise offset scaled by a radial mask, life tied to the fireball's;
spawned through the same sink hook as B11.

**Model recommendation.** medium.

**Verify.** The C1 hit at the controls against a hangar edge: the edge wobbles for a second.

**⚠ Traps.** Screen-texture reads force a copy of the colour buffer; with several bursts live that
is several copies, so measure under D32. The quad must draw after the fireball and be excluded from
the glow pass.

## B15 ☐ Scorch decals at a hit

**Goal.** A rocket or bomb hit on ground leaves a dark scorch under Enhanced; the faithful path
leaves the ground untouched, as the original does.

**Evidence (confidence: lead-only).** `docs/org/craters.md`: the original never carves a crater in
play, since the carve gates on the struck node's `can_modify` flag and no shipped node carries it;
no scorch texture exists in the data. Godot's `Decal` node projects onto the lit world; on the
faithful fullbright world it would not read.

**Approach.** Under Enhanced, at the impact outcome (or at the crater build in
`CraterField`/`TerrainCarve`, should an enhanced option re-enable the carve), place a `Decal` with a procedural radial scorch texture
(TUNE size from the weapon's crater radius, TUNE fade), pooled and recycled with the same count
cap the crater field keeps.

**Model recommendation.** medium.

**Verify.** `--chapter=C1 --fire` at the controls, three rocket hits on tarmac and one on grass;
decals sit on the surface and fade at the cap. Faithful goldens zero movers.

**⚠ Traps.** Decals on water read wrong; skip the water surfaces `ClassifySurface` names.
Alpha-to-coverage (A4) does not apply to decals.

# Wave C, lit clouds

## C21 ☐ Lit cloud cards: sun tint, transmission rim, shadowed undersides, soft depth fade

**Goal.** Under Enhanced the fvol cloud cards read as lit puffs: brighter toward the sun, a bright
rim on the sun side, darker on the underside and where the shadow map covers them, and no hard cut
where a card meets terrain or an aircraft. The faithful field is untouched.

**Evidence (confidence: direction-sound).** `FogVolumeClutter.cs` `ShaderCode(lit, fogged)`: one
static MultiMesh per kind, hand-billboarded, `unshaded`, `depth_draw_never`, no depth fade; the
sprite's face normal is in `INSTANCE_CUSTOM.xyz`. The reference technique (data survey) is the
same architecture with three terms this shader lacks: a sun tint from `dot(viewDir, sunDir)`, a
transmission rim from a blurred alpha sample offset along the sun direction in billboard space,
and a per-instance tint. The `BL-325` measurement (p90 218 near the moon vs 70 away) says the
original itself lights night clouds directionally; this item is the arm that can answer it under
Enhanced, and `BL-325` stays open for the faithful path.

**Approach.** A third shader variant, `ShaderCode(lit, fogged, enhanced)`, selected by
`GraphicsMode.Enhanced` so the faithful text is byte-identical: (1) the sun tint and transmission
rim as the reference formulas, amplitudes TUNE; (2) an underside darkening from the card's own
face normal against the sun; (3) the soft-particle depth fade copied from `EmitterRenderer.cs`
(the `hint_depth_texture` sample and the last-metres fade); (4) shadow-map darkening only if
Godot's `light()` path can be reached from an unshaded billboard cheaply, else skipped and
recorded. The sun direction comes from the same global B13 uses. The authored 240 colour and the
authored masks stay (decision 6).

**Model recommendation.** high: shader look work with four interacting terms, judged by montage.

**Verify.** `--chapter=C1 --graphics=enhanced` at the controls by day through the cloud deck, then
C5 and C1B at night; a montage of tint/rim amplitudes for the user to pick. Faithful goldens zero
movers; the cloud-card golden (C1 at 1208 m looking down the deck) is the faithful control.

**⚠ Traps.** The comment at `FogVolumeClutter.cs:225` forbids scaling the authored colour and it
still holds on the faithful path; the enhanced variant is the one place a tint is allowed, and the
comment must say so. The transmission offset is in billboard UV space, so it needs the billboard's
right/up, which the vertex function already builds. Depth fade on a `depth_draw_never` field is
fine, but the depth texture excludes the field itself, so cards do not fade against each other.

## C22 ☐ FogVolume banks from the authored fvol slabs

**Goal.** Under Enhanced a soft volumetric bank sits under the cards inside each authored fvol
volume, scattering the sun and casting shafts; the whiteout band in C5 reads as being inside a
cloud rather than a flat fog colour.

**Evidence (confidence: lead-only).** `fogvol.zrd` (`docs/formats/fogvol.md`) gives every `fvol*`
volume's polygons; `FogVolumeClutter` walks them. Godot's `FogVolume` nodes fill the Environment's
froxel volumetric fog, which the enhanced Environment does not enable today. The whiteout is
`WeatherRig`'s `FogVolumeWhiteout`, armed only where `fogvol.zrd` arms `fog_zone`.

**Approach.** Enable `VolumetricFogEnabled` on the enhanced Environment with a low global density,
and place one `FogVolume` (box or the volume's own mesh as `Shape = LocalMesh`) per authored fvol
volume with a TUNE density and the zone's fog colour; tie its density to the whiteout band so the
two agree. Off in the faithful path.

**Model recommendation.** high.

**Verify.** C5 at night through the whiteout and C1 by day looking along the deck at the sun;
montage of densities. D32 measures the froxel cost.

**⚠ Traps.** Froxel fog is low resolution and swims with TAA off, so judge after A1. The sky dome
is gamez geometry drawn over the background; volumetric fog in front of it is fine, but the dome's
own fog arm must not double-fog. The cards stay (decision 5); the bank is under them.

## C23 ☐ A rendered puff sprite set under Enhanced, picked by montage

**Goal.** A small set of rendered cloud puff sprites replaces the authored masks per fvol kind
under Enhanced, if the user picks them over the authored masks on a montage.

**Evidence (confidence: lead-only).** The reference thread's look comes as much from its five
Blender-rendered puffs as from its shader; the authored masks are small constant-RGB alpha masks.
No rendered set exists yet.

**Approach.** Render a handful of puffs (Blender is available through the MCP rig; volume
scatter, a sun key, alpha over white) at the authored masks' aspect, map each fvol kind to one by
name in a small table read only under Enhanced, and put the swap behind C21's variant. Montage
authored vs rendered on C1 for the user.

**Model recommendation.** high for the montage judgement; medium for the render pipeline.

**Verify.** The user's pick; faithful goldens zero movers.

**⚠ Traps.** Remake-only art, so it lives beside the parked upscaling under the same rule: the
faithful path never reads it. Downscaled or lossy sprites read as blur; keep PNG at the render
size.

# Wave E, the feel of speed

## E41 ☐ Wind streaks past the camera, keyed to speed and G, over the authored speed cue

**Goal.** Under Enhanced, faint streaks stream past the camera along the aircraft's velocity,
invisible at cruise, rising with speed toward rated max and thickening in a hard turn or a dive,
so speed and a direction change are felt in every view. The authored wisps keep emitting.

**Evidence (confidence: lead-only).** Other flight games (Ace Combat, Project Wingman, Star Fox)
carry the sense of speed with a camera-local streak field whose density and length scale with
speed and G; the original's own cue is the world-space wisp field (`docs/formats/effects.md`),
whose visible duration falls with airspeed, which is the low-fidelity form of the same idea.
`Precipitation.cs` already draws a camera-centred, self-animating streak field with a near fade
and a rim fade, so the rendering pattern exists; what is new is the drive (velocity, G) and the
gate (Enhanced).

**Approach.** A new `Effects/WindStreaks.cs` on the `Precipitation` pattern: one MultiMesh in a
camera-centred box, each instance a thin streak quad aligned to the aircraft's world velocity,
positions derived in-shader from a seed and `csky_time` so the CPU writes only a handful of
uniforms per frame (velocity, speed fraction of the airframe's rated max, a G or turn-rate term,
camera position). Alpha is zero below a TUNE cruise fraction, rises with the speed fraction, and
adds a TUNE G term; streak length scales with speed. Built by the rig only when
`GraphicsMode.Enhanced`; per player pane like the speed cue. Whether Enhanced dims the authored
wisps beneath it is a TUNE the user picks in the montage (decision 12), not a change to
`SpeedCue.cs`.

**Model recommendation.** high: a look item with three interacting TUNE terms and a G reading off
the flight model. `<TODO: where the flight model exposes rated max speed and a turn-rate or load
factor the streaks can read>`

**Verify.** A C1 flight at the controls: level cruise shows nothing, full throttle and a dive
show streaks, a hard bank thickens them; cockpit, nose and chase views all read. A montage of
density and length pairs for the user. Faithful goldens zero movers; `--det` enhanced golden
(D31's rocket-hit pose) either excludes the field or pins it, decided at D31.

**⚠ Traps.** The rain field's header warns never to use Godot's `TIME`; drive from `csky_time`.
Streaks through the cockpit glass read wrong at the near plane: keep the near fade. Do not edit
the wisps' opacity from here. Split screen needs the field per pane, as the
speed cue's `decorate` hook does.

## E42 ☐ The chase camera lags the nose through a roll and widens its FOV with speed

**Goal.** Under Enhanced the chase camera's orientation trails the aircraft's nose on a roll or
yaw and springs back, and its FOV widens a few degrees toward rated max speed; the decoded
distance law and the faithful chase camera are untouched.

**Evidence (confidence: lead-only).** `docs/org/cameraViews.md`: the decoded external camera has
a speed-and-acceleration distance law (ported) and no orientation lag or speed FOV; the external
FOV is one constant carried at construction (`CameraController.cs:142`,
`RestoreExternalFov`). Ace Combat and Project Wingman use both cues, and the original's own
distance transient already moves the camera with the throttle, which E42 extends rather than
replaces.

**Approach.** In `CameraController`, under Enhanced only: (1) a lagged copy of the aircraft's
attitude eased toward the live one at a TUNE rate (the same exponential shape the decoded
`dist_catch_up` uses), used for the chase camera's orientation while the distance law keeps
reading the live state; (2) `_externalFovDeg` plus a TUNE widening scaled by the speed fraction,
applied wherever `RestoreExternalFov` writes the external FOV, so the first-person views and the
death and crash cuts keep their decoded FOV. A `--graphics=original` run must produce the same
camera pose to the bit.

**Model recommendation.** high: the camera is decoded and every frame of it is pinned; the
Enhanced arm has to be provably inert on the faithful path.

**Verify.** At the controls on C1: a snap roll shows the camera trailing then settling, full
throttle widens the view; the faithful goldens and the camera suites zero movers; a golden with
the chase camera under Enhanced pinned at D31.

**⚠ Traps.** The decoded easing rates are per real second, not per sim step
(`cameraViews.md` "The easing dt is WALL time"); the lag rate here is TUNE but the same clock
rule applies, or the lag changes with the frame cap. The look-behind arm hard-codes its own
direction factor; leave it out of the lag. Do not touch `ExternalRadius`/`DistTransient`.

# Wave D, the net and the reading

## D31 ☐ An enhanced golden set under `--det`

**Goal.** A small `--graphics=enhanced` golden set pins the finished stack so later changes cannot
silently move it.

**Evidence (confidence: direction-sound).** `analysis/goldens/manifest.json` runs every shot under
`--det --mute`; under `--det` only the `--graphics=` flag reaches `GraphicsMode.Resolve`
(`docs/cli.md`), so an enhanced shot is one more entry with the flag. `RunTests.ps1 -Graphics`
today reaches only the perf and hitch stages (D31 of the first plan), by design.

**Approach.** Five entries: C1 lake (water, shadows), C5 night city (omni pool, whiteout), C1 deck
at 1208 m (clouds), the C1 rocket hit pose (explosions, a `--hold` that lands a burst in frame),
and the cockpit view. Each `exercises` field states what the shot covers today, under 250 chars,
no item ids (`CheckGoldenProse.ps1`). Pin them after A5's verdict so the temporal pass is settled.

**Model recommendation.** medium.

**Verify.** The golden stage passes twice in a row on the same tree (TAA's jitter must be
deterministic under `--det`; if it is not, the set pins with TAA off and the record says so).

**⚠ Traps.** A burst frame carries the previous frame; the manifest's single-shot path is the
un-jittered one. Do not append to an `exercises` field; rewrite it on a re-pin.

## D32 ☐ The perf reading over the finished stack, within 20% of the D31 baseline

**Goal.** A current frame-time table for the enhanced stack, and every new pass inside a 20%
envelope over the re-taken baseline, or a lever recorded for the one that is not.

**Evidence (confidence: direction-sound).** `git show 8aab7fa3`: C4 15.04, C5 13.60, C3 12.51 ms
enhanced at 4 panes, provisional against Wave E of the first plan. `docs/verification.md`
`PERF-13` and `PERF-25` bind how a hitch count and a moved cost may be read.

**Approach.** At Wave A's start, re-take the baseline on the current tree with
`RunTests.ps1 -Graphics enhanced` and the three targeted probes, three runs per cell, render scale
1.0; at the end, the same table over the finished stack. Each Wave B/C item also records its own
delta in its landing commit. If the total exceeds 20%, the ordered levers are FogVolume density
and froxel resolution (C22), heat-shimmer copies (B14), then SSAO quality.

**Model recommendation.** medium, low effort.

**Verify.** The two tables in the closing commit; `-Hitch` silent on the clean run.

**⚠ Traps.** Render scale above 1.0 is excluded from the budget (decision 9); make sure the saved
option is 100% or the run is under `--det`. The sim clock lags wall on the user's rig on some late
missions; read frame_ms from the perf probes, not from a flight's feel.
