# Ordnance effects and projectile prototypes

Part of: [weapon effects](../weapon-effects.md).

## Ordnance effect readers

An ordnance weapon splits its effects across a `*_control` reader (the impact/ground burst) plus
trail/puffer sub-readers driven from it. The `IMPACT`/`FIRE`/`FLYOUT` target → reader map:

| Reader | Defines (binding target) | Bound via | Role |
|---|---|---|---|
| `flak_control.zrd.json` | `flak_effect` | IMPACT | flak airburst; `flak_trails.zrd.json` supplies the smoke trails |
| `he_control.zrd.json` | `he_ground_effect` | IMPACT | high-explosive ground burst; `he_effects.zrd.json` the sub-effects |
| `ap_control.zrd.json` | `ap_ground_effect` | IMPACT | armor-piercing ground burst |
| `sonic_control.zrd.json` | `sonic_ground_effect` | IMPACT | sonic burst; `sonic_puffers` / `sonic_rings` the shockwave |
| `scatter_control.zrd.json` | `scatter_effect` | IMPACT | scatter/choker burst; `scatter_trails` the submunition trails |
| `flash_control.zrd.json` | `flash_effect`, `rear_flash_effect` | IMPACT | blinding-flash burst |
| `torpedo_effects.zrd.json` | `torpedo_trail` (FLYOUT), `torpedo_ground_effect`, `torpedo_water_effect` (IMPACT) | both | aerial-torpedo wake + impacts |
| `rear_arc.zrd.json` | `deploy_reararc` (FLYOUT), `rear_flash_effect` | both | the rear-arc flare deployment |
| `missile_puffers.zrd.json` | `generate_smokescreen` | FIRE | the smoke-screen laydown |

### FLYOUT `MODEL_ANIMATION`, the in-flight smoke trails

Every rocket's `FLYOUT` also names a `MODEL_ANIMATION`, an `ON_CALL` def sharing the projectile
prototype's name (`he_rocket`, `flak`, `sonic`, …). Reader source: `missile_puffers.zrd.json`
(most types) / `torpedo_effects.zrd.json` / `rear_arc.zrd.json`; all are also compiled into every
chapter's `cam_anim`, where the engine reads them. Each def activates the prototype node and runs
one or two **`DISTANCE_INTERVAL` `PUFFER_STATE`s** `AT_NODE` the round itself, the authored trail:
one puff per interval meters of flight, texture `splashbase` (a soft round blob), random velocity
±0.8 m/s, size 0.3–0.9 m, growth `[0→1, 1→0.25]`, and a per-type `COLORS` ramp that is the trail's
whole character (each stops emitting at animation time 10 s):

| Type (weapon) | Puffer(s) | Interval | Lifetime | Colour ramp (rgb, life fraction) |
|---|---|---|---|---|
| HE (`wep_06`/`_24`) | `he_rocket_trail` | 1.5 m | 3.0–4.5 s | 255,180,0 → 100,100,100 @ 0.1 |
| AP (`wep_05`) | `trailpuffer_ap` | 2.0 m | 3.0–5.0 s | 204,255,0 → 234,255,151 @ 0.15 → 100,100,100 @ 0.3 |
| Flak (`wep_07`) | `trailpuffer_dark` | 2.0 m | 3.5–5.0 s | 255,180,0 → 50,50,50 @ 0.05 (near-black) |
| Incendiary (`wep_04`/`_11`/`_25`/`_26`) | `trailpuffer2` | 2.0 m | 3.5–6.5 s | 230,90,90 → 255,220,163 @ 0.07 → white @ 0.2 |
| Sonic (`wep_08`) | `sonicpuffertrail1`+`2` | 2.0 m | 3.5–6.5 s | 131,200,190 → 68,115,109 @ 0.07 → 40,40,40 @ 0.2 (teal; size 0.2–0.5, friction 0.2) |
| Scatter / beeper / flash | `scatterpuffer_dark` / `beeper_trail` / `flash_trail` | 2.0 m | 2.5–5 s | the flak ramp |
| AA flak (`wep_27`) | `trailpuffer` | 2.0 m | 2.0–3.0 s | no ramp, a fire→smoke flipbook (`fireflare1`/`fire_f01`/`smoke101`/`smoke102`) |
| Cannonball (`wep_28`) | `trailpuffer2` + `forwardpuffer` | 2.0 m | 0.2–0.3 s | white trail + an orange forward glow (`local_velocity` z −350) |
| Torpedo (`wep_14`) | `torpuffertrail1` (+ `torpufferblast` from 3.5 s) | 0.2 m | 0.5–1.0 s | fire flipbook `fire_f01`…`f06`, off at 3.5 s; the blast cloud is TIME-interval 0.001 on `smoke101`…`103`, off 30 s later; `torpuffertrail2` is defined and never called |

The `sonic` def additionally runs a `sonic_spinner` sequence: a steady `OBJECT_MOTION`
`XYZ_ROTATION` roll of the round's body at **8.7266 rad/s (500°/s)** about z, looped forever,
the only rocket that spins. The torpedo def is a whole launch sequence, timed on the anim clock:
`RESET_STATE` folds `rightwing`/`leftwing` and hides `atprop`, and at 3.5 s the wings swing out
over 5 s, the prop comes on and spins, `atpayload` grows, `snd_propstart` and four
`snd_Atorp_armed` beeps play. Decoded event by event in
[`org/ordnanceTypes.md`](../../org/ordnanceTypes.md), "The launch look is the def's timeline".

**Engine wiring.** `ProjectilePool` resolves each rocket's `MODEL_ANIMATION` name through the
world `AnimProgram` and runs the def as a per-round `AnimInstance` on the sequence interpreter
(`ProjectileFlyoutAnim.cs`), the pool being the host: every `PUFFER_STATE` the timeline switches
on becomes a pooled `Puffer` fed each step (`DISTANCE_INTERVAL` and `TIME_INTERVAL` alike), the
spinner rate rolls the FLYOUT body, node visibility, from-to tweens and `ObjectOpacityFromTo` fades
drive the round's own model instance, and the def's sounds play at the round. A fade is written per
instance, as the world runtime writes one (the `csky_opacity` instance parameter, plus a translucent
twin material as that instance's surface override where the shader has no alpha path), so the
shared prototype material the pylon-mounted copies draw with is never edited. The rear-arc flare's
`sflsh` therefore fades in over 0.05 s and out over 2.25 s while it scales 1 to 2. The round's def
ends with the round, as the original's retire stops the round's `MODEL_ANIMATION` instance and
returns its model clone (`FUN_005ad2d0`, decoded in
[`org/ordnanceTypes.md`](../../org/ordnanceTypes.md), "The def ends with the round"): at the 2.0 s
`DETONATION_TIME` the body is freed with the star still at about 13 % opacity, and the def's own
`rear_flash_effect` call at 4.0 s is never reached. The detonation plays the same
`rear_flash_effect` through the flare's `IMPACT` `default` and `player` rows, so a fired flare
flashes once.

`large_fireball` / `small_fireball` (bound by `FIRE`/`IMPACT` on the heaviest ordnance) are the
**shared** destruction fireballs defined in `flame_ball.zrd.json` and reused by nearly every
destructible, see [destructibles.md](../destructibles.md) and [effects.md](../effects.md), not a
weapon-specific asset.

Supporting effect readers with no direct binding target, driven by the controls above or by the
guns: `gunshell.zrd.json` (the ejected shell casing), `flak_trails` / `scatter_trails` /
`sonic_puffers` / `sonic_rings` / `he_effects` / `pufftrails`, `beeper_plug.zrd.json` (the beeper
tag), `cockpit_bulletholes.zrd.json` (hits on the player's own canopy), and the emplacement guns
`8inch_cannon` / `aa_gun` / `maa_gun` / `fbgun` / `fbgun2`.

## Projectile prototypes (gamez roots)

`FLYOUT`'s `MODEL` and a few `IMPACT`/`FIRE` targets name a **node prototype**, a model root
present in every chapter's `nodes.json` (all confirmed in C1). `SceneBuilder` instances it at
the firepoint or impact point. "Confirmed" here means **the name resolves to a gamez node**,
not that the node carries a mesh, see the footnote below for `gunshell`/`muzzle_burst`, the
two names known to differ:

| Group | Prototype roots |
|---|---|
| Gun rounds (`FLYOUT MODEL`) | `slug.flt`, `dumdum.flt`, `armorpiercing.flt`, `magnesium.flt` |
| Ordnance (`FLYOUT MODEL`) | `ap_rocket`, `he_rocket`, `flak`, `flash`, `beeper`, `scatter`, `incendiary`, `smoker`, `sonic`, `reararc`, `a_torpedo`, `aaflak`, `cannonball` |
| Muzzle | `muzzle_burst`¹, `muzzle_burst_slug` / `_ap` / `_dum` / `_mag`, `muzzle_burst2` |
| Impact / misc | `gunhit`, `dum_gunhit`, `mag_gunhit`, `gunshell`¹, `ballflare.flt`, `bsplsh.flt`, `splash1.flt` |

¹ Measured across all 8 chapters (the retired `analysis/weapon-effects-node-shape/`,
`git show analysis-archive:analysis/weapon-effects-node-shape/FINDINGS.md`, `BL-140`): both roots
carry `model_index: -1` (no mesh of their own) and exactly one child. `muzzle_burst`'s child
(`dummy`) is also `model_index: -1`, the whole subtree is genuinely meshless, so instancing
this root alone lights/moves nothing visible. `gunshell`'s child (`g1`) carries a real mesh
(`model_index: 60` in every chapter, 10 vertices / 7 polygons) and is structurally parented
under `gunshell` itself, so the *casing* prototype does resolve to a visible mesh, one node
below the name `FLYOUT`/`CallAnimation` target. The C22 ejection wiring instances the whole
`gunshell` subtree per shot (see the muzzle-flash engine-wiring section above), which renders
the `g1` mesh with its own materials, never assume the root alone shows anything.

`firepoint` is the marker prototype (the aircraft's own firepoints are documented in
[markers.md](../markers.md)).

## Water splash playback (`splash1.flt` / `bsplsh.flt`)

The two defs are identical in shape. Read from their `OBJECT_MOTION`/`OBJECT_OPACITY_FROM_TO`
events over the model's `*_base` disc and `*_splash` column:

- `*_base` disc: `SCALE` xz 1→2 over the first 0.2 s, then eases back to 1.8 over
  `[1.0, 2.0]` s (`EVENT_OFFSET 0.8`).
- `*_splash` column: pops to its authored scale `(1, 100, 1)` and collapses to zero over the
  full 2.0 s run.
- Opacity, on the whole root (base disc **and** column together, not the column alone):
  fade-in 0→1 over 0.05 s, hold, then fade-out 1→0 over 1.0 s starting at 1.0 s
  (`0.05 + EVENT_OFFSET 0.95`). That start coincides numerically with the base disc's own
  ease-start above, but the two are independent authored events, not one shared value.
- Column flipbook: `OBJECT_CYCLE_TEXTURE` resets `splash01`→`splash03`, 3 frames at 4 fps
  (C1B `materials.json` material 135's `cycle` block).
- `splash1_splash`'s authored quad is 5 cm wide, sub-pixel past ~30 m. The reference stills
  (`Water Splash.png`) measure its ticks at ~0.35 m, an 8× match; judged at the controls with
  the fades in, 1× reads as a thin stripe. `ProjectilePool.SplashColumnWidthScale` is the 8×
  gloss, kept a `static readonly` rather than a `const` so a run can still be set to 1 to reach
  the authored literal width.

Implementation: `ProjectilePool`'s `Splash*` constants and `AdvanceSplash`.
