# Backlog, unscheduled future work

Everything known-but-not-scheduled, so it survives between polish runs. Which plan is active, if
any, is `PROJECT_CONTEXT.md`'s "Current status", never restated here. Per-item history/diagnosis
detail is in commit messages (`git log --grep=BL-NNN`; earlier in the archived development log's
dated entries) and `docs/architecture.md` (module bullets); how to
verify a change without fooling yourself is `docs/verification.md`. **The live list of hand-tuned
constants awaiting playtest is the set of items tagged `[Tuning]`** (consolidated actionable
index: [`playtest.md`](playtest.md)). When an item gets scheduled into a plan, move it there; when
it lands, delete it here.

**Item IDs.** Every entry carries a flat `BL-NNN` tag, assigned once at minting and never
renumbered or reused, even when the item it names is deleted, so a stale cross-reference elsewhere
fails loudly instead of silently pointing at the wrong item. **Mint a new ID by running
`./New-ItemId.ps1 -Kind BL`**, never by scanning this file or taking "max + 1" by hand. The
counter lives in `.git/item-id-counters.json` (shared by every worktree, outside version
control) and the script increments it under an exclusive file lock, so two concurrent sessions
cannot be handed the same number.
⚠ **Run it for EVERY id, every time, it is not a once-per-session lookup.** Minting one id and
then deriving the next by adding 1, or reusing a number the script handed you earlier in the
session, desynchronises the counter from the file: the id you invented is not recorded, so the
next call hands it out again and the duplicate-id hook fails a later commit. Need several at
once? `-Count n` reserves a block in one call. The failure is silent at the time and surfaces
in someone else's commit, which is why the rule is absolute rather than a default.

**Structure.** Items are grouped into twelve theme sections, in this fixed order: Damage &
destruction · Weapons & combat · Flight model & collision physics · Environment & world · Effects
& animation runtime · Audio · Cameras & views · HUD & UI · Splitscreen · Missions, modes &
campaign · Tooling, platform & docs · Misc. Within a theme, items sort by ascending ID. A straddler goes to the theme
whose system you would open to fix it; Misc is the escape hatch for items with no such system,
if it grows past a handful, that is a missing theme, not a working bucket. Splitscreen is the one
cross-cutting exception: an item whose subject is the single-viewer/single-player assumption goes
there, even though the fix opens another theme's system.

Every item is one flat bullet:

    - `BL-NNN` `[Type]` `[Status?]` `[Size]` `[Next: …]` `[Impact: …]` `[Evidence: …]` `[Scope?]` **One-sentence claim, the symptom or goal.** body…

`[Type]` is exactly one of: `[Bug]` (behaviour is wrong vs the original or vs intent),
`[Feature]` (something the engine does not do yet), `[Research]` (the deliverable is an answer,
not code), `[Tuning]` (a hand-tuned constant needing judgement at the controls), `[Cleanup]`
(debt with no player-visible behaviour change), `[Fidelity]` (the mechanism is decoded and ours
differs, with no symptom yet), `[Perf]` (frame time or memory), `[Tooling]` (scripts, hooks and
the verification loop), `[Testing]` (a missing test or test aid). The optional status tag is `[Owed-playtest]`
(the code/constant side is done; what is missing is a human at the controls) or
`[Blocked: <blocker>]` (cannot start regardless of priority, the blocker is named: a capture
`CAP-nn`, a milestone, another item, an upstream release, a user decision). No status tag means
open and unblocked.

**Property tags** follow the type and status tags, in this fixed order, and say what a full read
of the body would say, so that the artifact can filter on them. `CheckItemIds.ps1` rejects a value
outside these vocabularies; it does not require the tags, so an item minted without them still
commits, and they are added when its body is next reshaped.
- `[S]` / `[M]` / `[L]` is the fix's size by shape, never by hours: `S` is one file, one constant,
  a test-only or doc-only change, or a close with no code; `M` is one subsystem, a few files, or a
  change that re-pins goldens; `L` is plan-sized, cross-cutting, or needs a design first.
- `[Next: decode|data|code|look|decide]` is the first step before the item can move: `decode`
  reads the original executable; `data` reads extracted game data, footage or a measurement;
  `code` writes code or tests now with nothing owed first; `look` needs a human at the controls
  (every `[Owed-playtest]` item and every `CAP-nn` blocker); `decide` needs a user decision.
- `[Impact: high|low|none]` is what a player would notice if the item were done: `high` shows in
  ordinary play, `low` is subtle, rare or only visible when looked for, `none` is cleanup, tooling,
  instrumentation or a research answer with no visible change.
- `[Evidence: decoded|data|footage|spec|feel|trace]` is the strongest source the item's
  *Evidence:* line rests on, in the standing notes' order of trust: an executable decode, extracted
  data files, a capture under `OriginalScreenshots/`, the pre-release design spec, or a feel report
  or estimate. `trace` is the case with no original-game source at all: a read of CSVM's own code,
  a log or a profile.
- `[Scope]` is optional and names the one mission or scene the item is tied to (`[CM14]`, `[C5]`,
  `[MP1]`), spelled as the item spells it. An item that spans missions carries none.

**Body template for new entries** (existing bodies are reshaped opportunistically, when an edit
touches them anyway): after the bold title sentence, labelled run-in lines, each present only
when it has content, *Evidence:* (what was measured or checked, with `file:line`/data paths,
the one field every entry should have) · *Fix shape:* · *⚠ Traps:* (mechanisms already ruled
out, unit ambiguities, "do not fix it by X") · *Playtest after fix:* (launch command + what to
look for) · *Cross-refs:* (related `BL-nnn`/`CAP-nn`/docs, with why).

## Standing notes

- **Scheduled items live in their plan, do not re-add them here.** If one is closed without
  landing, its record goes in the closing commit's message.
- **[`playtest.md`](playtest.md) is the actionable, consolidated checklist** for everything
  tagged `[Owed-playtest]` or blocked on a `CAP-nn`, what to look for, the launch command, and
  what each blocks. The backlog keeps the deep evidence/traps; keep the two in step.
- **Reference shots are in `OriginalScreenshots/`** (gitignored, cited by filename).
- Bare code paths are relative to the Godot project's `src/`.
- **On the original pre-release design spec:** its structural claims have held up against our
  data, per-hardpoint cluster sizes, the 8-firepoint rig, the zeppelin launch-altitude gate, the
  two-volume danger zones, the armour/health damage split. Its per-item art and balance numbers
  have repeatedly failed, gun ranges, rocket speeds, zone hit points, the crash fireball's
  timing, shell ejection's calibre gate and mount position. Take the mechanism from it, never the
  magnitudes or the art direction, and prefer extracted data or an `OriginalScreenshots/` capture
  wherever either exists. Where an entry rests on the document alone, it says so and marks the
  value TUNE.
- **The flight constants are coupled and `--run-tests` guards them.** Thrust sets speed, speed
  scales the yaw `eff`, so a change in one moves others; `FlightEnvelopeTests` asserts five
  decoded scenarios and will fail if a change breaks one. There is no thrust scale left to turn:
  thrust is `EnginePower · ref_area · curve(Mach) · lever`, every number in it is the executable's,
  and the level-equilibrium curve it produces is asserted per airframe and per lever position
  ([`docs/org/flightModel.md`](docs/org/flightModel.md), "Part-throttle equilibrium"). Chasing a
  *transient* or a feel report through the force path is the forbidden move. ⚠ **`PitchTune`/`YawTune`/`RollTune` are no longer
  in that category: all three are 1, because `FUN_0048c470` carries no per-axis factor on any axis
  ([`docs/org/flightModel.md`](docs/org/flightModel.md), "The `*Tune` rates"), and
  `FlightConstantInventoryTests` now pins them there.** Every asserted target is the decoded
  plant's own value or a named exception; a footage figure that disagrees (the filmed 33.00 °/s
  pitch rate, the 28.60 s `yaw-360`, `accel-150-290`, `decel-290-150`) is discarded and kept only
  as row prose that gates nothing ([`docs/org/flightModel.md`](docs/org/flightModel.md), "Parity
  ledger"). `FlightScenarios` is 4.

## Damage & destruction

- `BL-060` `[Feature]` `[Blocked: the faithful crash settling]` `[L]` `[Next: code]` `[Impact: low]` `[Evidence: feel]` **Improve on the original crash, the bespoke "breaking apart" (branch `bespoke-crash-animation`).**
  *Decision:* stays parked on its branch; nothing is blended until the faithful recreation is settled.
  User's call (2026-07-23): the retired bespoke `CrashBreakup` wreck-scatter looked *better* than the
  faithful data-driven crash, so it was preserved on that branch rather than deleted. **The A/B playtest
  passed (2026-07-23), the faithful data-driven crash is confirmed as the default**, so this is now the
  standing follow-up: once the faithful recreation is fully settled, revisit blending the branch's nicer
  breaking-apart (free-body scatter + down-ray ground-rest) into (or over) the data-driven path, an
  explicit "improve on the original" opportunity, not a faithfulness regression.
  ⚠ **Traps (from slices 1–2, 2026-07-23).** The `blend`/`softParticles` `Puffer.Create` overrides
  exist and default to a byte-identical shader, reuse them; a MIX-blend dark puffer near the
  ground also needs `softParticles: false` or the depth-fade zeroes it. A fading additive fireball
  reads as smoke in a screenshot, isolate the emitter (suppress the others, freeze the crash with
  no `--hold`) before believing an effect is present. Anchor at the plane centre (`pose.Origin` =
  `healthy`), not the impact point. For the debris arcs, the executable decode governs now
  (`PLAN-object-motion-decode`, 2026-08-13): `translation_range` gives `dirY = elevation/90` and
  horizontal `1 − |elevation|/90` (an L1 direction, not spherical), `initial` the launch speed,
  `delta` an acceleration. The retired `DebrisTune.LaunchScale` of 0.65 was a footage fit laid over
  the earlier, wrong spherical reading and is deleted along with the whole tune class, a blend
  here starts from the authored arc, with no compensating scalar. The unscaled arc reads like the
  original at the controls, so nothing is owed on it and there is no scalar to put back. What stays
  TUNE is the `fly_trailN` anchor being invisible so that
  only the trail shows; and the DISTANCE interval hides behind an inverted flag
  (`has_interval_value` false, key off `interval_type`).

- `BL-297` `[Research]` `[Owed-playtest]` `[S]` `[Next: look]` `[Impact: low]` `[Evidence: decoded]` **Panel-damage semantics: what the original actually
  shows when a part is damaged, the user's re-test verdict is that our authored-data reading has
  the feature wrong.** User at the controls 2026-08-06, after `BL-288`'s pooling fix landed
  (bursts no longer teleport, that mechanical fix stands and is not in question): (1) nose
  damage sprays effects at the WINGS; (2) panels appear to tear while armor should still be
  absorbing; (3) identical repeated debris bursts read as "the same panel flies away again", a
  torn panel should be gone once. Expectation: debris matches the point of destruction, is
  health-gated, and each panel tears exactly once.
  *Evidence (data reading, 2026-08-06):* the per-part `injure_anims` DO map panels to their part
  (`extracted/zrdr/vehicle.zrd.json`, player-1: nose→`pdpanel7`, tail→`pdpanel8`,
  leftwing→`pdpanel5`/`4`/`3` at 0.5/0.3/0.15, rightwing→`pdpanel6`/`1`/`2` at 0.4/0.3/0.15). The
  cross-part bleed the user sees is authored *elsewhere* in our reading: (a) every part's 0.99
  `<part>_damage_effects` shim → `random_gun_impact`, which sparks a random `pdp1` (40%)/`pdp2`
  (40%) and ALWAYS `pdp4`, wing sites, whatever part was hit; (b) the vehicle-level 0.85
  `player_fuelleak` (ANY part's fraction) plays a gunhit flash + fuel vapor at a random `pdp1–3`.
  The "repeats" have two shapes: `pdpanel7` (nose) is authored to throw FOUR `gimmeflakes` bursts
  within 0.4 s (one extended burst), and every panel's burst uses the same 7-flake `planeflakes`
  template, so successive panels' bursts look identical.
  *Decoded 2026-08-15 (`crimson.exe`). All three symptoms are settled without the capture.*
  Write-up: `docs/org/vehicleDamage.md` ("Damage staging", the two new subsections).
  **(2) armor, answered; ours is wrong.** Not a scale ambiguity: `FUN_004b3d70` keys the per-part
  `injure_anims` on `[part+0x30] / [part+0x2c]`, health only, and `FUN_004b7f80` zeroes the health
  damage outright while the part's armour pool covers the incoming armour damage. So a fully-armoured
  part crosses NO per-part threshold, not even the 0.99 `<part>_damage_effects` shim: the original
  shows nothing at all on a fresh armoured plane. Our combined armour+HP scale
  (`PartState.Fraction`) was why panels tore early; that is **fixed**, the per-part loop re-based on
  `PartState.HealthFraction` and the hull loop split out onto `SummaryHealthFraction`
  (`git log --grep=BL-384`). Owed at the controls as `PT-80`.
  **(1) location, answered; ours is faithful in mechanism and wrong in timing.** `FUN_00521180` binds
  an anim's node names through `FUN_004efaf0`, which searches the instance's context subtree, then
  the anim's local tables, then a GLOBAL by-name lookup (`FUN_004d0280(7, name)`). `pdpN` names are
  unique on the airframe, so a context miss falls through and finds the same node anyway: the
  context disambiguates a name, it never redirects one. There is no part-relative retarget on this
  path, so the original really does spark wing sites on a nose hit. It just does not do it until
  that part's armour is gone.
  **(3) repetition, answered; a panel tears once.** The handle arrays (part`+0x4c`, inst`+0x890`)
  start an entry only when its slot reads zero and clear the slot on the UPWARD crossing alone,
  never when the anim ends. So each entry fires once per downward crossing and can only re-fire
  after a repair (`FUN_004b3e20` wipes, `FUN_004b8180` restages). The `pdp4` repeat the user saw is
  authored and faithful: `<part>_damage_effects` is a separate entry on each of the four zones with
  its own slot, so `pdp4` legitimately sparks up to four times a flight, once as each zone first
  crosses.
  *Fix shape:* no code change is owned here. Symptom (2) is fixed (`git log --grep=BL-384`),
  and is `PT-80`'s to confirm at the controls. Symptoms (1) and (3)
  are faithful-as-authored and this item closes on them once `CAP-29` confirms the look. Do NOT
  resolve `random_gun_impact`/`player_fuelleak`'s panel pick to the nearest pdpN. The decode says
  the original does not do that.
  *What `CAP-29` still owes:* the look only. Does the flung debris read as a piece of that panel or
  as generic flakes, and what visibly changes on the airframe. Questions (a) and (b) are now
  confirmation, not decision.
  *Not decoded:* whether the interpreter's selection event really is the 40/40/always-`pdp4`
  weighted pick our data reading describes. The exe executes the authored def; what was verified is
  where the nodes resolve, not how the random branch is evaluated.
  *⚠ Traps:* do not "fix" by suppressing the authored shims wholesale (`CAP-27` already probes
  whether the spark shim exists at all in the original, coordinate, don't overlap). Do not
  re-open `BL-288`'s pooling, the theft mechanism was real and its fix is verified independent
  of these semantics.
  *Cross-refs:* `CAP-29` (the capture), `CAP-27` (spark-shim existence), `BL-288` landing
  (`PLAN-m3-polish-10` A1), `DamageVisuals.cs` (the consumer),
  `extracted/zrdr/vehicle.zrd.json` (the authority).

## Weapons & combat

- `BL-693` `[Tuning]` `[Owed-playtest]` `[S]` `[Next: look]` `[Impact: low]` `[Evidence: feel]` **The rebinding screen's three axis-capture constants are
  picked, not measured.** *Evidence:* `ControlCapture.RestBand` **0.25**, `MoveThreshold` **0.6** and
  `CapturedDeadzone` **0.5** are what decide whether a stick or a trigger a player pushes becomes a
  binding, and none of them has a decode behind it: the original cannot bind an axis to a command at
  all (`docs/org/input.md`, `FUN_00537090`'s four typed slots), so there is nothing to match. The
  ordering is the rule and is deliberate, `RestBand` < `CapturedDeadzone` < `MoveThreshold`: an axis
  must be seen inside the rest band before a move counts, so drift cannot latch; the move must clear
  a threshold well past that band, so a sloppy centre cannot either; and the deadzone stamped on the
  binding sits between the two, because the value a stick crosses is not the value it settles at.
  *Owed at the controls:* with a real pad, bind a flight action to a stick direction and to a trigger.
  Does 0.6 feel like a decisive push rather than a nudge, and does 0.25 forgive the stick the pad
  actually rests at? Then fly the result: at 0.5 the bound half of the stick has to feel like a
  button, on and off, without a dead patch a player reads as a broken binding.
  ⚠ A trigger already bound at another deadzone is not a second control: `ActionMap.SameControl`
  ignores the deadzone on purpose, so capturing the right trigger takes it from both Camera Boost
  (0.5) and Camera Dolly Out (0) rather than stacking a third reading. Raising or lowering these
  numbers does not change that, and must not be used to try to.
  *Cross-refs:* `PT-120` (the pad sitting that judges the three), `BL-296`, `docs/org/input.md`, `docs/org/targeting.md`, `docs/controls.md`.

- `BL-603` `[Bug]` `[S]` `[Next: look]` `[Impact: low]` `[Evidence: decoded]` **The human rig sweeps the mesh hull where the original sweeps its def's six
  `collision` probes.** *Evidence:* decoded for `BL-601` (`git log --grep=BL-601`): `FUN_0048d7f0`
  carries the def's `collision` list as rays from the previous pose, six points on the `p*` player
  defs; `FlightController.SweepProbes` does that for an AI rig and keeps the mesh-derived hull sweep
  for the human rig, which is wider than the six points. *Fix shape:* fly a slot the six points
  clear and the hull does not (CM13's dbase arch on dzpath2) in both games; if the original passes,
  sweep the player's probes too. *Cross-refs:* `PlaneStats.CollisionProbes`, `docs/formats/vehicle.md`.

## Flight model & collision physics

- `BL-562` `[Perf]` `[M]` `[Next: data]` `[Impact: low]` `[Evidence: data]` `[CM11]` **CM11 (C2/M02) still spends a single physics tick of about 36 ms on the sortie's
  first part destruction, and about 130 to 138 ms on the first tick after the world build.** *Evidence (traced):* the bracketed
  instrument (`PhysicsTickCost`, `--perf`'s `phys_tick_ms` / `phys_tick_max_ms` / `phys_hz`), with a
  temporary sub-scope Stopwatch splitting one whole `FlightController.SimStep`, over three
  78-sim-second `--no-det` runs of 150 windows each, one aeroplane and nobody at the controls with
  17 AI aircraft alive. Median `phys_hz` is 60.0 in every run and 4 to 10 windows of 150 exceed
  16.7 ms. (a) The FIRST tick after the build costs 130 to 138 ms, 95 ms of it the 20 animation
  runtimes' first `Advance` (46,355 index rows walked over five cold `FindAll` misses, plus a 29 ms
  first sim step); it is the world-build settling regime, not flight. (b) **The collision sweep is
  ruled out.** The 36 to 43 ms tick a player meets in flight is the sortie's FIRST part destruction:
  on the step where the pilot's right wing reaches 0 % against `g1176/col_buildings` the whole step
  costs 36.1 / 36.6 / 37.0 ms across the three runs, of which `AircraftContactResolver.Resolve`
  holds 35.6 / 36.2 / 36.4, `IContactEffects.SpendDamage` 29.1 / 29.5 / 29.7 and `ShatterStruck`
  4.6 / 4.7 / 4.8, while the sweep costs 0.35 to 0.41 ms and `UnEmbed`'s single `Overlaps` 0.04 ms,
  with no collection anywhere across the step. What that `SpendDamage` does is start the damage
  presentation for the first time (`rightwing_damage_effects`, the `pdpanel6` / `pdpanel1` /
  `pdpanel2` swaps, `player_fuelleak`); an earlier graze in the same sortie that destroyed no part
  spends 0.73 ms there. Only one part destruction happens per unattended run, so whether the second
  costs the same or the first is paying a warm-up is unmeasured. The sweep's own worst step over the
  three runs is 2.9 / 5.3 / 2.9 ms: the 2.9 ms is the session's first sweep and is managed
  first-call cost (0.4 ms of it in the physics server), and the 5.3 ms is one `CenterRayContact` ray
  that struck nothing, so no collider is answerable for it and the report the query fills never
  reads above 0.00 ms. (c) **The 33 to 36 ms steps carrying `gc=1/1/0` were the AI target ranking
  reading the world's node names on every tick, and that is fixed.** A per-phase allocation meter
  (`--perf`'s `[perf] alloc` line, `sim_alloc_b=`, now permanent) puts 98 % of a settled tick's
  allocation in `SimPhase.CapturedAiAircraft` at about 309,000 B per tick, and a temporary bisect
  inside it lands on `TargetPool.CollectOwners` (251,904 B per tick over 164 calls, 81 % of the
  tick) and `TargetPool.NameOf` (about 36 KB per tick over 197 calls, 12 %). Both are Godot
  node-name reads: every AI shooter asks every ranked structure candidate for its whole gamez
  ancestor chain on every physics tick, and each ancestor's name comes back as a fresh string, a
  finalizable `StringName` wrapper and a `DisposablesTracker` entry, which is the finalization rate
  the pause follows (PERF-20). Measured and ruled out in the same pass: the AI pilot decision (15 to
  21 B per tick), the collision sweep (308 B), the ray casts (about 2.9 KB) and the whole `_Process`
  pass except `Flight` (about 13.8 KB per frame). The ancestor chain is now cached on the
  destructible instance, keyed by the anchor's parent so an authored re-parent re-walks. Two before
  and three after runs on the same rig (78 sim seconds, `--no-det`, one aeroplane, nobody at the
  controls): `CapturedAiAircraft` 309,000 to 57,100 B per tick, `alloc_mb_s` 19.2 to 19.7 down to
  4.1 to 5.6, `fin_per_s` about 60,500 down to about 9,300, per-10-wall-second collections gc0=4
  gc1=4 down to gc0=1 gc1=1 (gc2=0 in the settled regime both ways), `pause_per_s_ms` 25 to 41 down
  to 3.5 to 8.5. Worst tick per window over the last 60 windows: mean 31.7 and 36.7 ms down to 8.3,
  9.5 and 15.6; windows over 16.7 ms 20 and 21 of 60 down to 3, 3 and 9 of 60; maximum 116 and 123
  ms down to 55, 72 and 91. Median `phys_hz` stays 60.0. The spread across the three after-runs is
  fight variance under `--no-det`, so the GC terms are the stable measure (PERF-20). What is left on
  the tick is `TargetPool.NameOf` at about 42 KB per tick over 220 calls, then
  `Enumerator[AiRatingBias]` boxing in `AiTargetRanking.MatchedBias`'s `foreach` over an
  `IReadOnlyList`. The 50 ms `AnimRuntime.Advance` step is not a fourth class: window 240, two
  seconds in, reads 49 to 57 ms with only about 21 ms of it named by any simulation phase, and sits
  inside the first GC window's 211 ms of pause over 16 gen-0, 15 gen-1 and 7 gen-2 collections, so
  it belongs with (a). *Decision:* measure a second part destruction in the same sortie first,
  since whether the 36 ms is a per-destruction cost or the first one's warm-up is unmeasured and
  decides which of the two is worth touching. *Fix shape:* (b) next. (b) is now a
  question about the damage presentation rather than about collision: decide whether the first
  damage-stage start is spread off the contact tick, which touches `SpendDamage` and the damage
  pools, not the sweep. (a) is worth a separate look only if a cutscene handoff or a mid-mission
  stage build repeats it. *⚠ Traps:* **the sweep premise is dead**: the sweep is under 2 % of the
  step it sits in on the spiking tick, so do not re-derive a sweep cost from a tick maximum or from
  the `HumanAircraft` phase maximum that holds it. A gen-1 GC pause lands inside whichever
  sub-scope of a bracket happens to be open, so read the GC counters across the same span before
  naming the term a bracket reports (`docs/verification.md` PERF-34). **The cap-exhaustion premise
  is dead**, the entry used to claim 72, 102 and 177 ms ticks discarding about six sim steps each
  against Godot's default `max_physics_steps_per_frame` of 8; over four paired 83-second runs on the
  current build no window's worst tick reaches 133 ms, so nothing exhausts the cap. **Do not chase
  the sustained step** either: the ~39 ms step and half-speed sim the entry once claimed were a
  misreading of Godot's `physics_ms` monitor, which holds the WORST tick of the last wall second
  (`docs/verification.md` PERF-1). The recurring 18 to 25 ms band that dominated every earlier
  reading was the ungated once-a-sim-second telemetry print, one write per live aircraft in the same
  tick (PERF-23); it is gated and gone, so do not re-derive it. Compare durations in sim seconds,
  never wall seconds; state which mode a re-measurement flew (the numbers here are `--no-det` with
  nobody at the controls, so they under-weight projectiles and destruction cascades); and do not
  raise `max_physics_steps_per_frame`, which deepens the catch-up spiral rather than recovering
  lost steps. The per-sim-step query objects those ray casts build are now reused rather than made
  fresh (`GodotWorldQuery`), so a re-measurement of the tick meets a different allocator than C22's.
  *Cross-refs:* `PLAN-M5-polish-6` C22, `docs/verification.md` PERF-1, PERF-19, PERF-20, PERF-23
  and PERF-34.
- `BL-1014` `[Cleanup]` `[L]` `[Next: decide]` `[Impact: none]` `[Evidence: trace]` **`FlightController.cs`
  is 4490 lines and changes for unrelated reasons; the responsibilities that have their own
  state and rules leave as real modules.** *Evidence:* one review range added 1045 lines to the
  file over about 90 scattered hunks for six reasons that share no state: the engine-out propeller
  audio, mouse capture and the virtual cursor, the pilot view mode, contact resolution, the Danger
  Zone photograph, and head-look with the pause. The class is already `partial` across
  `FlightControllerBuild.cs`, `BeeperTags.cs` and `SmokeScreens.cs`, which hides the size without
  reducing the public surface any caller sees. *Fix shape:* pick the responsibilities whose state
  never crosses the flight tick (the view-mode dispatch, the mouse capture, the photograph
  latch, head-look are the candidates) and give each a type with its own public members that the
  controller composes, measuring the split by the controller's public member count before and
  after. *⚠ Traps:* a new `partial` file is not a split; the repo's standing rule is that a partial
  is not a deepening, and the existing three are accepted, not a pattern to extend. Do not move the
  flight tick's own steps (forces, contact, damage) out; they share the accumulator and belong
  together. *Cross-refs:* `BL-1015`, `BL-1016` (the same shape in `GameSession.cs` and
  `OriginalOptionsScreen.cs`), `docs/architecture/Flight.md`.

## Environment & world

- `BL-272` `[Tuning]` `[M]` `[Next: look]` `[Impact: low]` `[Evidence: footage]` **Precipitation: every unit mapping from `weather.json` to a look is invented, and
  one remake-only rule is deliberately held back** (`Precipitation.cs:29-62`, type/tint/rate/density
  are authored; fall speed, box size, particle counts, streak length/width, sway are 16 TUNE
  constants; the sprites themselves are procedural stand-ins for the original's untextured
  line/point primitives, and rain streaking along fall-direction-vs-velocity is a documented
  remake-only rule pending an A/B). Needs original rain and snow footage to calibrate, worth a CAP
  when weather work resumes.
  ⚠ One calibration fact is already on file (`CAP-11`, 2026-08-07, user): C2B IA1's rain falls
  below the cloud cover as **one-pixel-wide streaks**, narrow enough that the 2560-wide Game DVR
  capture swallows them entirely, while ours are plainly visible in the same scene
  (`playtest/CAP-11/csvm-c2b-low.png`). Streak width is the first constant to revisit.

- `BL-322` `[Research]` `[L]` `[Next: look]` `[Impact: high]` `[Evidence: decoded]` `[C5]` **The original shades a lit surface per vertex and clamps the
  product at white, so reproducing its sun term darkens every away-facing surface of every chapter
  and cannot darken a C5 city block at all** (split out of `BL-303` at its close). Explicitly NOT
  fog, `BL-303`'s own adjunct note, and the Wave B fog work moved none of it.
  *Evidence, decoded:* the whole term is in `docs/org/vertexLighting.md`, "The sun's own term, and
  where the product is clamped". `SUNLIGHT_BICOLORED` is light flag `0x400` (`FUN_004dbe80`, tested
  once per light at `0x00568a7f`) and it selects whether the ambient half of the term carries
  `SUNLIGHT_COLOR_AMBIENT` or reuses `SUNLIGHT_COLOR_DIFFUSE`; `FUN_00472ea0` writes both colours on
  every zone apply, so the second one always exists and the bit only decides whether it is read. All
  32 C5 zone blocks author white for both, so **in C5 the bit is a no-op on the value** and the term
  is the single scalar `0.5 + 1.5 × max(N·L, 0)`. It is live elsewhere: 28 of the install's 212 zone
  blocks are bicolored with two different colours, C4's `IA1` pairing a warm diffuse with a cool
  ambient. C5 also places exactly one `Light` node among 11,438 and authors no `LIGHT_STATE` at all,
  and the two point-light lists `FUN_00568790` fills add their term without the directional loop's
  `− 1`, so nothing but the sun ever reaches the accumulator there.
  *The measured band is refuted on the family it was read off:* `FUN_00554550` multiplies the
  per-vertex factor by the **authored vertex colour** and clamps the product at 255 (`0x00555289`
  then `0x005552b2`), where `Weather.WorldLightFactor` clamps its collapsed scalar to 1 *before* the
  multiply. All 8,549 authored vertex colours on C5's `cblock1`-`cblock7` city-block skins are 255,
  so the original's product saturates at white for every `N·L ≥ 1/3` and draws exactly what we draw,
  and is *darker* than us below it. Only the `bldg1`-`bldg4` tower skins (51 % to 71 % at 255, tenth
  percentile 0 to 112) stay unsaturated, and there the ratio ours/theirs is `1 / (0.5 + 1.5 N·L)`,
  which is 0.66 at `N·L` 0.68 and 0.58 at 0.82. So the term can produce the measured band, but not
  on the low-rise blocks a midtown frame is mostly made of.
  *Frame-wide is ruled out, so this is a surface term, not exposure or gamma:* the five HUD gauge
  discs are the same 2D art at the same pixels in both frames and read ours/orig **0.97** (rockets
  0.97, ALT 1.07, damage 0.80, guns 1.03, MPH 1.00; the "2400" readout box 0.96). Nothing ×0.6
  survives into the capture or onto the composite.
  ⚠ **The `CAP-11` C5 pair is not a matched pose.** `t0.5-c5-spawn-night-city.png` looks north over
  midtown from altitude and `csvm-c5-night-city.png` sits over water, so 10.2/15.5 and 21.7/37.6
  compare *different buildings*, and the decode above says the difference between two buildings is
  exactly what decides the ratio. C5's sky dome cannot serve as the in-world control either: at a
  level view its rows read 0.83, 0.69 and 0.43 of the original's from zenith to horizon, a gradient
  mismatch of its own rather than one scalar. The matched poses are the `CAP-58` stills below.
  *Matched poses, `playtest/CAP-58/pairs/`:* seven original spawn stills of C5 `IA1`, each paired
  with our render of the same `dogfight_ace` entry (`--pos`/`--direction` from `ia.zrd`, frame 30),
  original above ours in `pair1`..`pair7`, `sheet.png` the overview and `patches.png` the measured
  regions. Every still matched an entry by its scenery, so seven of the eight spawns are covered
  (entries 0, 1, 2, 3, 4, 5, 7; entry 6 was not caught). Frame-wide the pose matches (the bridge
  towers stand at the same screen columns), but the original's chase camera holds the aircraft
  smaller and the horizon lower than ours, so regions were placed per image, not shared (`BL-885`).
  *What the pairs show:* the answer depends on the surface, in both directions. On the `bldg`
  tower skins in `pair2` (entry 7, amid the towers) the wall between the windows is darker in ours:
  the right tower's face turned away from the camera reads ours/theirs 0.48 at the median and its
  face toward the camera 0.44, the tenth percentile 1 in ours against 5 to 12 in the original. The
  two faces stand at right angles and read the same ratio, so these pairs show a flat halving of
  the wall texel, not a term that differs by facing. Plain surfaces go the other way: the bridge
  deck underside (`pair5`) reads 1.26 at the median and the bridge tower masonry (`pair4`) 1.30,
  both brighter in ours. The HUD control holds, the ALT gauge disc reads 0.98 (mean), so nothing
  frame-wide is in either number. The original stills are JPEG and the wall levels sit near black,
  so the tenth-percentile figures carry compression noise and only the medians are quoted as
  ratios. No pair holds a close plain `cblock` wall toward the camera, so the low-rise blocks are
  still unmeasured. This is a luminance reading of matched frames, and the verdict on whether the
  tower walls are too dark or the deck too bright stays with the eye at the controls.
  *Fix shape:* per lit vertex, `drawn = clamp(authored_vertex_colour × (SUNLIGHT_AMBIENT +
  SUNLIGHT_DIFFUSE × max(dot(N, L), 0)), 0, 1)` in place of `ALBEDO *= csky_world_light`. Four
  files: `Weather.cs` stops collapsing (the uncollapsed pair is already on `ZoneWeather`),
  `WeatherRig.cs` publishes the pair and the sun direction beside `csky_world_light`,
  `csky_atmosphere.gdshaderinc` declares them, and `SceneBuilder.cs`'s shaded world variant reads a
  normal instead of the flat scalar. It is not a one-uniform change: 66 % of C5's 21,577 lit
  polygons and 57 % of C1's 16,580 carry no normal array, so `SceneBuilder` has to emit a face
  normal per polygon for those rather than the smoothed normal `SurfaceTool` generates.
  ⚠ **Taking it is a look change on every lit surface of every chapter, and its largest effect is a
  darkening nobody has asked for.** A vertex authored at 255 facing away from the sun halves, and
  most of a city at night faces away from a sun bearing −135°. That is owed a verdict at the
  controls and not a luminance distance.
  *Playtest after fix:* the C5 night poses in `playtest/CAP-11/README.md`.

- `BL-325` `[Research]` `[Blocked: CAP-59]` `[M]` `[Next: look]` `[Impact: low]`
  `[Evidence: footage]` **The C1B night clouds are lit flat, where the original's footage may show
  a cloud lit by which side of it turns to the moon.**
  *Evidence:* `CAP-11` C1B (`playtest/CAP-11/`): cloud cores p90 **218** at t16 against **70** at
  t5, and our spawn pose (`--chapter=C1B --pos=-5406,55,-7200 --direction=-0.391,0,-0.921`) at
  p90 173.0 whole frame, 165.7 on the moon side, 175.3 away.
  ⚠ **The measured split may not be a moon side at all.** At t5 the dim cloud hangs directly
  beside the moon, far off and deep in the night fog; at t16 the bright cloud is close to the
  aircraft and also on the moon's bearing. Both frames put their
  cloud on the moon's side, so the 218/70 split reads as distance through the fog, not as the side
  a cloud turns to the moon. Moderately sure, from two stills.
  *What the mechanism can reach:* not that frame in any case. C1B ships **no `fvol` volumes**
  (`FogVolumeTests` `C1B "0|-|206.25|bare|cloudsprite:absent"`), and its **70 placed
  `cloudparent` facades** (1,620 card nodes) are authored `lighting: false` with an empty normal
  array, so the original's per-vertex term has neither the gate nor the geometry. What that term
  does buy is on the `fvol` cards, and `FogVolumeClutter` applies it for the chapters authoring
  `lighting: true` (C1C, C2B, C5), per vertex off the zone's uncollapsed `SUNLIGHT`, while C1B
  moves by nothing. Nothing here touches `SceneBuilder` or `WeatherRig` lighting.
  *What would settle it:* `CAP-59`, one C1B cloud filmed from two headings at the same range, one
  with the moon behind the camera and one with it behind the cloud.
  ⚠ **Vocabulary, three populations, never one phrase for two:** **`cloudsprite1`/`cloudsprite2`**
  are the `fvol*` clutter scatter (the deck field, world-locked and tiled); **`cloudparent`** are
  discrete world-placed clusters (C1B's 70, C1's 28, C4's 45); and the **plane-local ambient
  wisps** each chapter's `speed_cue.zrd` emits 60 m ahead of the player (`Flight.Hud.SpeedCue`,
  `docs/formats/effects.md`) are a third. A claim about one is not evidence about the others, and
  the first two **share their textures**, `--tex-override` on `cloud1.tif`/`cloud2.tif` paints
  both (`SHOT-21`), so separate them by altitude or cluster position, never by texture.
  ⚠ Traps: `csky_world_light` is CAP-11-calibrated on terrain, so a directional cloud term must be
  cloud-local. Nothing in the original scales a cloud card's colour except its own per-vertex term
  (decoded, `docs/org/cloudCards.md`), so this must not become a global cloud brightness knob.
  *Playtest after fix:* the C1B night spawn above against `playtest/CAP-11/`'s t5 and t16 frames,
  saying for every puff how far off it is and which side of the moon it faces; and one lit chapter
  (C1C above the band) for a verdict on whether the per-vertex shading on the `fvol` cards reads
  like the original.
  *Cross-refs:* `docs/org/cloudCards.md` (the deck field's lattice, its decoded perturbation and
  the shipped 30 m offset over it), `docs/formats/effects.md`'s speed-cue section (the third
  population), `docs/org/vertexLighting.md`'s facade section, `CAP-11`, `CAP-59`.

- `BL-329` `[Tuning]` `[Owed-playtest]` `[S]` `[Next: look]` `[Impact: low]` `[Evidence: decoded]` **The D32 in-cloud flicker's rate and ramp are declared
  TUNE, not decoded** (`PLAN-weather-decompile-match` D32, 2026-08-09;
  `Session/WeatherRig.BandFlicker`). `FUN_0042ee40`'s drift rate multiplies a per-mission
  weather-struct field ≈ `+0x934` that no reader decodes and no capture pins a value for, so
  `BandFlicker.DefaultRate = 5.5f` is picked, not measured: at the re-randomized drift speed's
  midpoint (0.2..1.0, mean 0.6) it traverses the blend parameter's full [0,1] range in `5.5 * 0.6 *
  0.1 = 0.33`/s, i.e. ~3 s at the mean and 1.8–9.2 s across the randomized range, "a full traverse
  in a few seconds", not a decoded figure. `BandFlicker.RampFrames = 30` (0.5 s at the fixed 60 Hz
  step) is the amplitude ramp that keeps a session's frame 0 (and any static probe/golden capture
  at a rig's first tick) reading the unremapped `WeatherState.WhiteoutAmount` exactly, its length
  is also a guess, chosen only to be short next to a flight and long next to one frame.
  *Evidence:* `PLAN-weather-decompile-match`'s D32 entry; the curve shapes themselves
  (`BandFlicker.LogCurve`/`AtanCurve`/`Remap`) ARE decoded from `FUN_0042ee40` and are not part of
  this TUNE, only the rate and ramp length are a judgement call.
  *Fix shape:* none pending, needs in-cloud footage of the original with visible timing (a static
  screenshot cannot show a drift rate) before either constant can move off a guess.
  *Playtest after fix:* fly into C1's cloud band and hold in the RAMP, not the opaque core, the
  core (1032–1062 m) is fully whited out and the flicker's own guard skips it there by design
  (`--freecam --chapter=C1 --pos=-2000,1000,-1792 --direction=0,0,-1` sits in the bottom ramp),
  and compare the shimmer's pace against any original in-cloud footage (`CAP-12`'s C4 take has
  in-cloud frames) once such footage is reviewed for timing rather than just colour.
  *Cross-refs:* `docs/architecture.md`'s `Session/World/WeatherRig.cs` entry (D32 bullet).

- `BL-341` `[Research]` `[M]` `[Next: look]` `[Impact: low]` `[Evidence: data]` `[C5]` **Reopened `BL-250`: with the real `no_clutter` gate landed, 7.6% of C5's ground
  (13.8 million m², the flagged overlay area with no base layer beneath it) renders bare, and
  whether that is what the original does is untested.** `BL-250` closed 2026-08-07 on a curated
  list (`ClutterBuilder.BuriedClutterDistricts`, excluding `cblock4/5/6` map-wide) that turned out
  to be standing in for a mechanism nobody had decoded yet. B13/B15
  (`PLAN-clutter-uv-placement`) landed that mechanism, `PlaceOnMesh` now skips a
  polygon carrying the decoded `no_clutter` flag, the flag SELECTS which of two coplanar layers
  decorates rather than meaning "bare here", and the curated list was retired because it was wrong
  on 14.1% of the map even though it happened to be right where `CAP-22` looked (78.3% of C5's
  ground by area is genuinely tower country). *Evidence:* B15's landing commit
  (`git log --grep="B15: land BL-305"`) measured the gate against every flagged/base pair in C5
  and found 35% of flagged overlay area has no coplanar base polygon underneath it at all, for
  that ground the gate now has nothing left to fall back on and leaves it undecorated. *Fix
  shape:* find what the original actually draws on that 7.6%, either a third layering mechanism
  this plan didn't decode, or the original genuinely leaves it bare too (which would close this
  outright). Start from a located landmark pose, the gate itself was confirmed by the user's own
  flyover 1 km north of C5's `brooklynbridge` node, not by a nadir (`SHOT-28`,
  `docs/verification.md`), and not from `CAP-22`'s pose, which
  cannot resolve this question (it already reads correctly). *⚠ Traps:* (a) do not re-curate a
  list as a stopgap, that is exactly the mistake this item exists to not repeat. (b) A nadir
  shot cannot distinguish a painted rooftop from bare ground any better than it could distinguish
  a rooftop from a building (`SHOT-28`); use a low oblique. *Cross-refs:* `BL-250` and `BL-305`
  (both closed, the doubled-district curation and the C5 packing bug the gate that surfaced this
  replaced; `git log --grep=BL-305`. Do not reopen either ID; IDs are never reused, per this
  file's own rule).

- `BL-1037` `[Bug]` `[M]` `[Next: code]` `[Impact: high]` `[Evidence: feel]` **Under Enhanced
  Graphics, faint diagonal bands cross the water and the aircraft's self-shadow carries noise; the
  item also takes the sun's penumbra filter down to SoftHigh.** *Verdict at the controls:* after `BL-803`'s fix
  (the filter at SoftUltra), "it fixes some artifacts but the stripes still remain", the stripes
  being fine low-contrast diagonal bands over the water at `--pos="-6037.015,395.381,-6040.959"
  --direction="0.58931,-0.04872,0.80644"` (`Screenshots/crimsonskies_2026-09-20_07-12-26-864.png`),
  and "some shadows on the plane have visual noise". The cost call `BL-803`'s closing commit left
  open is taken: this item moves the filter to the SoftHigh rung (4.63 ms against SoftUltra's
  5.78 ms of GPU time at the C1 waterfall); the build still runs SoftUltra until it does, and the
  item owns whatever the drop gives back along with the two symptoms.
  *Evidence:* `git log --grep=BL-803` and `analysis/screen-dither/FINDINGS.md`: the 2x2 alternation
  instrument found the soft-shadow pass at 2.168 over 81 percent of a C1 waterfall frame, SSAO, SSR
  and glow under 0.11, SoftUltra removing 86 percent of the excess and SoftHigh 72. Open water was
  not one of the instrument's poses, and noise inside an airframe's own shadow is acne at the
  receiver, which the filter's rung does not decide. *Fix shape:* `EnhancedShadowFilterQuality` to
  SoftHigh; bisect the water bands at the pose with the four doors (`--no-soft-shadows`,
  `--no-ssao`, `--no-ssr`, `--no-glow`) and against the Faithful presentation at the same pose,
  since a band both presentations draw is the water's own texture; for the airframe, the shadow
  bias and normal bias against its own casting at a pose with the sun low across the fuselage.
  *⚠ Traps:* `EnhancedShadowFilterQuality` carries a prohibition: do not lower it while
  `EnhancedShadowAngularDistance` stays above the sun's real 0.5 degrees, because Godot resolves the
  penumbra through a disc rotated per screen pixel and too few samples for the disc's width leave
  that rotation as a woven pattern over every lit surface. The drop to SoftHigh therefore brings
  the angular distance down to 0.5 degrees in the same change, or the weave `BL-803` removed
  returns. Do not raise the rung back to chase the water; the cost call is the user's. A pattern
  the Faithful presentation also draws is not this item. *Playtest after fix:* the pose above under
  Enhanced, still and turning, the water flat; then a chase view with the sun across the airframe,
  its shadow on itself clean. *Cross-refs:* `git log --grep=BL-803`, `SHOT-42`, `docs/cli.md` (the
  four doors), `docs/PLAN-enhanced-graphics-2.md`.

## Effects & animation runtime

- `BL-674` `[Bug]` `[M]` `[Next: look]` `[Impact: high]` `[Evidence: data]` `[CM10]` **CM10's attack-balloon wave flies from 990 m down to water level and back up
  during its scripted entrance.** *Evidence:* driving C1/M05's shipped `OBJECTIVE10` wake and
  sampling the assembly every 0.1 s for 70 s traces its world Y from 990 m (the hidden entrance
  altitude) to -0.26 m at about t = 57.3 s, then climbing again at the `rise` sequence's authored
  3.33 units per second. The objective marker follows it down, which is the symptom `BL-656` was
  filed on; that item is disproven because the marker is tracking the geometry correctly, and the
  geometry is what goes to the water. The motion is the entrance's own SiScript-to-`rise` handoff,
  so it lives in the animation runtime (`PoseChannel.cs`, `FromToMotion.cs`, `ScriptPlayback.cs`),
  not in `ObjectiveSites.cs`. *Fix shape:* first decide whether the original does this at all, by
  watching a CM10 wave arrive in footage or at the controls; a balloon that dips to the sea on its
  way in may be authored. Only if it does not, find whether the handoff between the entrance script
  and `rise` drops an altitude the original keeps. *⚠ Traps:* do not add an altitude floor to the
  assembly, and do not offset the marker upward; both were removed on decoded evidence and the
  balloons descend as they attack, so no constant is right at two altitudes. The anchor rule itself
  is the original's (`FUN_004cf2c0`, midpoint of the node's active bounding box) and is correct.
  *Cross-refs:* `BL-656`'s closing commit, `PT-111`, `docs/org/targeting.md` "Where a mission
  structure is".

- `BL-537` `[Tuning]` `[Owed-playtest]` `[S]` `[Next: look]` `[Impact: low]` `[Evidence: feel]` **Effect pools at four players, judged in play.** The pool sizes in `CSVM/data/effect_pools.json` were re-judged on a build with no first-use construction cost: rockets and the sonic burst never wrap, a four-object simultaneous death wraps `flame_ball_01` at 4 and 6 slots and is quiet at 8 (now shipped), and seven or more identical deaths in one frame wrap at the 16 ceiling and cannot be sized away. At the controls the single-player half reads right: four fireballs burn out in place, and the seven-death wrap is not visible under the debris. Still owed: a 4-player splitscreen session with everyone firing, judged for anything that reads as shared between panes, and the ceiling for many-player builds (at 16 players the default root wants 19 and gets 16). The instrument is `AnimRuntime.PoolRecycles` and the `anim: effect pool for '<name>' recycled slot` DEBUG line in the log file sink; the sizes staged print on the world-effects build line. ⚠ Raise only a root that logs a recycle, never the default; the three gun roots stay at 1; a root sized 0 clamps to 1. Each slot copies the root's subtree (155 templates at 1 player, 263 at 4).
  *Cross-refs:* `PT-129` (the four-player flight that judges it), `BL-296` (the other splitscreen-scoped item).

## Audio

- `BL-281` `[Tuning]` `[Blocked: CAP-27]` `[S]` `[Next: look]` `[Impact: low]` `[Evidence: feel]` **The ricochet sounds are audible but very faint.** `PT-25` (c), 2026-08-05:
  `snd_ricochet1–4` play under the per-impact spark burst but sit too low to read. A mix-gain
  question with no reference recording behind it, to be judged at the controls rather than derived. ⚠ Judge only after `CAP-27` decides
  whether the original has this effect at all: `BL-090` already calls the 0.99 `injure_anims`
  entry that drives it "plausibly an authoring leftover", present on 1 of 11 aircraft, so the
  capture may delete the feature rather than tune it.

- `BL-285` `[Bug]` `[S]` `[Next: decode]` `[Impact: low]` `[Evidence: decoded]` **The decoded
  exhaust smoke is ported and measures about half the original's density at a matched slam.**
  *Verdict at the controls:* against CAP-21, "its a lot denser in the original"; the AI aircraft's
  trails (`git log --grep=BL-969`) draw and read right. The matched-speed compare below stands as
  the measurement; no further look is owed, the next step is the decode of the draw path. *Evidence:* the original's exhaust
  smoke is its one code-built puffer, a near-black 0.4 m trail per `exhaust%d` marker whose opacity
  charges from the commanded lever running ahead of the live one and decays at 1.5/s
  (`FUN_004afa20`, `FUN_004afbc0`, fed from `FUN_0048e580`; decode in `docs/formats/effects.md`,
  "Aircraft throttle-rise exhaust"). `Flight.Airframe.ExhaustSmoke` ports it in place of the borrowed
  `nitropuffN` puffers and the invented 0.25 threshold gate, which had no counterpart: every
  constant is now decoded and none is left to tune. The decode reproduces both ends CAP-21 bounds,
  a 1/8 step peaking at opacity 0.013 and an idle-to-full slam at 0.356, out 3.9 sim-s later (the
  footage's plume is gone about 2.8 wall-s after the slam, 3.9 sim-s at the capture's 1.39 ratio).
  *Measured at matched speed:* `--lever=` (`docs/cli.md`) presses the throttle digit row on a
  schedule, so a headless capture can fly the footage's own input history: idle from the spawn, one
  `8` press at 10 sim-s, the chase camera 0.8 s after it, a Bloodhawk as CAP-21 flies, 1280x720.
  The control is the same sim frame rendered again with the trail suppressed, so the difference
  between the two frames is the plume and nothing else, and the footage is read the same way
  against a background estimated per row from the band's own margins. Over the hundred rows a
  hundred pixels below the wing line, the original darkens its background by 41% and 39% in its two
  plumes, peaking at 62% and 72%, over median widths of 77 px and 60 px; the port darkens by 20%
  in both, peaking at 36% and 41%, over 49 px each. The port therefore stands at about half the
  original's opacity and about 0.7 of its width at the same moment of the same manoeuvre in the
  same airframe, which confirms the verdict at the controls and rules the matched speed out as the
  cause. *The question:* which term in the draw carries the missing factor, since the emitter has
  none left. Three candidates, all in the renderer: the quad-rim fade in
  `MultiMeshEmitterRenderer`'s shader, which ramps alpha over the outer 12% of each card edge and
  is authored against additive rectangles over dark ground yet also applies to the mixed blend this
  plume draws in; `Puffer.SizeScaleDefault`, the decoded radius-to-diameter 2; and the
  `smoke101..103` alpha channel, mean 0.263 and peak 0.639, which caps one card at 0.23 opacity
  under the slam's 0.355 birth alpha. None of the three is settled by the decode as it stands, so
  the next step is the original's own draw path for a near-black mixed sprite, never a constant of
  this emitter. ⚠ Traps: slam with a digit key, not the
  throttle-up key. A held key moves the commanded lever at the slew's own rate, so the gap stays
  one step's slew and the original shows nothing for it either. A scripted `--hold` feeds the
  smoke no gap, since it bypasses the lever; `--lever=` is the capture flag that reaches the
  plume. *Playtest after fix:* from idle at a steady
  cruise, slam to full with the `8` digit key and watch from the chase camera as CAP-21 does around
  12.5 s: near-black smoke from each exhaust, strongest about a second after the slam and gone
  about four seconds after it, as wide and as dark near the tail as the footage's at the same
  speed; a single 1/8 step at most a faint wisp, idle to 5/8 a plume about half as dark.
  *Cross-refs:* `git log --grep=BL-285` (the port), `git log --grep=BL-969` (the AI trails).

- `BL-934` `[Bug]` `[M]` `[Next: look]` `[Impact: high]` `[Evidence: feel]` **The dynamic enemy and
  ally voice lines dispatch in the suite and are now heard at the controls, but far more rarely
  than the original's: one friendly line in a squadron fight and no enemy line, where the
  original's Blake talks every few seconds.**
  *Verdict at the controls:* first "still no dynamic voice from friends or foes, not even on
  friendly fire, nothing in the logs"; after the flat radio path (`git log --grep=BL-978`, a line
  was placed at the speaker and attenuated by distance) "Frequency of voice lines is a lot lower
  then the original, Fighting in C01 against Paladin Blake and he talks every few seconds in the
  original. Fighing a Squadrion i only heard one friendly voice line but no enemies". The rate is
  the items below (`BL-986` to `BL-995`); this item is the re-listen after they land, `PT-161`.
  ⚠ "Nothing in the logs" is not yet evidence of no dispatch: the `ai voice:` lines
  are `Log.Info` on the `sound` category, so run with `--log=sound:debug` and read for
  `ai voice: <name>: trigger #`. A speaker whose pilot owns a family's clips but had none
  prewarmed now warns once per family (`owns the clips but none was prewarmed`), so a silent pilot
  without that warning is not a prewarm gap. A campaign sortie that carries no such line with the
  category up is a third cause, the trigger evaluation itself (talker test, the human-quarry rule
  on the bearing and attack call-outs), and gets its own diagnosis here.
  *Evidence:* the first silence was the mode-machine subscription sitting inside `RegisterAi`'s voiced
  branch, so an aircraft carrying no accent was never watched, and the shipped rosters leave nearly
  every enemy on `accentID` -1 while voicing the player's own flight. A flown C1 session now logs
  `ai voice: ai2_player_pfighter: trigger #8 -> snd_id7_WA-Enemy-6 (Talker test passed. Play AI
  sound #8.)`, an accentless hostile committing to the human and a voiced ally speaking the bearing
  call-out, and the `ai-voice-mission` suite holds the same chain over C1/M02's shipped roster
  through to a playing stream on the radio's Voice-bus player; a playing stream is not an audible
  line (`docs/verification.md`). *⚠ Traps:* combat lines and the scripted lines now share
  `Mech3/MissionRadio.cs`'s one queue, so a scripted line on air holds a bark past its 0.8 s
  tolerance and drops it, which is the original's rule and not a silence to fix; the aircraft that speaks is never the enemy that was spotted, so a silent enemy
  is not the symptom; a pilot silent on one trigger is often data, since VO 4 (accent 13) owns no
  `WA-*` or `TA-*` family and the DI tiers read the whole-vehicle health pool, so a wingman whose
  armor soaked the hits stays at full health. *Playtest:* `PT-161` (C1/M02 from the campaign with
  `--log=sound:debug`; keep the log). *Cross-refs:* `docs/formats/combat-voice.md`,
  `git log --grep=BL-934`, `git log --grep=BL-978` (the flat radio path),
  `git log --grep=BL-977` (the Instant Action prewarm gap),
  the saved Voice level (`Utils/AudioMix.cs`, the `audio-buses` suite) if the lines dispatch and
  stay inaudible; `git log --grep=BL-986` (the attack pair now raised off the quarry rather than
  off the mode edge, which was the largest single cause of the low rate) and `git log --grep=BL-991`
  (`PR-DngrZn`, the last trigger id to be wired).

## Cameras & views

- `BL-266` `[Research]` `[M]` `[Next: look]` `[Impact: low]` `[Evidence: decoded]` **Plane wobble: residual decode questions after the
  wiring landed.** *Decision:* the dive rattle and the nitro wobble both read too small and janky
  next to the original's, and the mechanism was the reason rather than any magnitude. Both now run
  the original's own component block. **Owed sortie:** fly the merged build against
  `OriginalScreenshots/Videos/Dive Wobble.mkv` and `OriginalScreenshots/Videos/Nitro Wobble.mkv`,
  a dive past rated max for the first and a nitro engage for the second, and judge the ported
  amplitude and rate before touching `DiveRattleKickScale` or `NitroWobbleKickScale` (both default
  to the faithful step). The oscillators are wired (`ShakeDefs`/`PlaneShake`, visual-only roll on
  the plane node; law and measurement in [`docs/formats/shakes.md`](docs/formats/shakes.md) and
  `analysis/gun-wobble-shake/`). The dressing behind the shake is a decoded camera
  random-walk (`crimson.exe`), not the remake's dated sawtooth, details below.
  **Resolved:**
  - **(a) made faithful:** the fire source IS a random-walk accumulator
    (`PlaneShake.FireBullet` steps `Walk += (rand−0.5)×2·(factor×caliber)·7.54`
    = uniform ±7.54·(factor×caliber)/shot, wep40 ±2.11e-2 rad; decayed
    by the authored `damp` τ≈80 ms). Merged to `main` (`eba69782`, branch experiment `bl266-random-walk`).
    **`GunBuzzKickScale` (default 1.0 = faithful) is the one tune knob, dial it, never
    `magnitude_factor`.** So the fire gap was a **mechanism/law mismatch, not a render-pipeline
    loss**, and this port is the first real feel of the kick law.
    (The old approach-(B) suspects are also settled: fire-rate is one round per tick at authored
    `FIRE_RATE` (8.0 for wep_40), the "12–13/s" was a redraw-window artifact, and 60 fps
    pose-interpolated render loss tested NEGATIVE.)
    ⚠ **The (d)/(e) decode says that step is the wrong size and the wrong kind, so (a) is not
    closed for fidelity.** The `7.54` came from the camera constructor's *defaults*, a `2.0` gain
    and the `6.2832` waveform branch, which `FUN_0042bc10` overwrites for every authored source.
    `fire_bullet` authors `sawtooth 1` and `frequency 15.0`, so the original's step is
    `mag × 15 × 4 × 1.2` and the kick is a **velocity** of ±36·mag rad/s into block 0's `[3]`, not
    a displacement. A displacement walk at ±36 would render roughly forty times the original's
    angle, so the number cannot simply be swapped; `_fire` keeps its landed step until (a)'s own
    look decides, and the choice is between the whole component block (as (d)/(e) took) and the
    envelope it has. `docs/org/shakes.md`, "`fire_bullet`, per-shot roll".
  - **(d) the dive rattle now runs the original's component block, ported.** The per-frame player
    updater `FUN_0048c470` reads block 4's `min_speed`/`magnitude_quotient` and calls the same
    `FUN_0042c070`/`FUN_0042be10` dispatcher the `fire_bullet` path uses on component index 4
    (`camera+0xd4/+0xd8/+0xdc`), every frame the excess-over-gate law
    (`(speedRatio − min_speed)/magnitude_quotient`, `PlaneShake.SetSpeedRatio`) is positive. What
    the kick adds to is a **velocity**, and the rendered angle is the position `FUN_0042bec0`
    integrates out of it on the block's `sawtooth 1` branch, so `PlaneShake` now carries the pair
    rather than an envelope: a per-tick kick of ±36·magnitude rad/s and the two-branch integrator
    at the original's `1/150` substep. `DiveRattleKickScale` (default 1 = the faithful step) is the
    one knob; `magnitude_quotient` is decode. Trace:
    `analysis/gun-wobble-shake/FINDINGS.md` (high_speed section) and
    [`docs/org/shakes.md`](docs/org/shakes.md).
  - **(e) the nitro wobble runs the same block, ported.** `FUN_004b2131` kicks block 6 with the raw
    authored `magnitude` 0.05 once per engage, player only, and the block's authored
    `frequency 4.0, damp 3.0, sawtooth 1` turns that into a velocity kick of ±0.48 rad/s that
    renders as a decaying triangle over about a second and a half. `NitroWobbleKickScale`
    (default 1) is the knob. The judgement that drove this ("janky at the beginning, larger but
    very fast, then too small but still very fast") was three complaints the mechanism explains at
    once, and none of them was a magnitude. ⚠ **The plane wobbling is the decode, not the defect**
    ([`docs/org/shakes.md`](docs/org/shakes.md), "Two oscillators, one mechanism"): a plane-mounted
    camera inherits the roll, and `PT-86` asked for a *camera* shake it should not have. The
    contradiction this clause flagged is settled from the binary: `[3]/[4]/[5]` are the velocities
    and `[6]/[7]/[8]` the positions the consumer sums, `docs/org/shakes.md` had it right and
    `analysis/gun-wobble-shake/FINDINGS.md` had it reversed twice; the findings page is corrected.
    The `sawtooth` branch constant `4.0` is a literal at `0x00603514` and genuinely separate from
    the authored `frequency 4.0`, so that coincidence is not one.
    *Still unanswered:* whether the original's engage moves the nose or only the roll.
  **Still open, all data/fidelity questions:**
  - (b) the impact sources' `magnitude_factor` constants are decoded (`bullet_impact` 5e-4,
    `missile_impact` 1e-3 plus `he_factor` 2.0 on HE rounds, both read at `FUN_004b9bc0`'s block
    1/2 kicks), but what each multiplies is not: the per-event drive quantity `FUN_004b9bc0` passes
    alongside `magnitude_factor` is undecoded, so CSVM's stand-ins (an incoming gun round reusing
    the caliber law, a rocket's armor damage doubled by `he_factor`) stay TUNE. A being-hit capture
    pins them; a further decode of `FUN_004b9bc0`'s own multiplicand would settle it without one.
    The `explosion` source's magnitude term is decoded as never filling in the original: the parser
    reads the key `max_magnitude` into block 3's slot while `shakes.zrd` authors `magnitude_factor`
    instead, so the retail explosion shake is zero regardless of blast damage
    ([`docs/org/shakes.md`](docs/org/shakes.md)). `PlaneShake.ExplosionAt` still kicks the authored
    `magnitude_factor` against a damage stand-in, so the port and the decode now disagree here;
    matching the original means zeroing that kick, not tuning it.
  - (c) the `ON_CALL` `small/medium/large` `damage_shakes` defs are the AI half of the five camera
    kicks, not script calls: `FUN_00473430(index)` plays them on any vehicle that is not the
    player's, from the same five sites ([`docs/org/shakes.md`](docs/org/shakes.md), "The seven
    component blocks and every kicker"). The nitro engage is wired
    (`FlightController.AdvanceNitro`); a round fired, a round taken, the overspeed arm and a
    collision contact are decoded and still unwired.
  - **(fidelity) judge the port, then dial.** Playtest owed on all three knobs, each defaulting to
    its faithful step: `GunBuzzKickScale` against the firing clip, and `DiveRattleKickScale` and
    `NitroWobbleKickScale` on the sortie named at the top of this entry. Two honest caveats: the
    fire source's **decay model (τ≈80 ms) is an engineering guess, not a decode**, and its step is
    the one (a) now flags as wrong-sized; and the ported dive rattle is about 1.1× the replaced
    sawtooth's RMS but 2.2× its peak, so the mechanism alone only partly answers "too small" and
    the look is what decides whether a knob moves.
  ⚠ Traps: `SHAKES_CAMERA` is NOT the fire-path shake mechanism, its sole carrier among all
  48 weapons is `wep_26` "FW", a zero-damage scripted fake weapon (a scripted detonation-shake
  marker); the fire path is the unflagged `fire_bullet` source. And the near-match trap: several
  magnitude candidates coincide with authored constants, wire nothing on one coincidence (the
  caliber law stood because the candidates separated by an order of magnitude each way).
- `BL-885` `[Fidelity]` `[Owed-playtest]` `[S]` `[Next: look]` `[Impact: high]` `[Evidence: decoded]` **The chase camera's base elevation is authored
  (`thirdp_pitch`), where CSVM holds a hand-picked 15.7°.** The chase placement builds its direction
  from the head's shown azimuth and the head's shown elevation PLUS the camparam block's `+0x28`:
  `FUN_0042c7f0` loads `DAT_0064ef58` at `0042c881`, adds `[ECX + 0x28]` at `0042c88d` and hands the
  pair to the direction builder `FUN_0053f550` at `0042c8a2`. The reader `FUN_0042f700` puts
  `thirdp_pitch` there in radians (`0042f81d`-`0042f83b`, the authored degrees × π/180). Shipped that
  is **0.29°** on every airframe and **0.2°** on Balmoral's block, i.e. the original's settled chase
  camera sits essentially dead astern, level with the aeroplane. CSVM instead normalises a
  hand-picked `BaseUp` 4.5 / `BaseBack` 16 pair, which is **15.7°** above the tail, and
  `CameraController`'s own comment calls the direction hand-picked because
  `docs/formats/camparam.md` had read 0.29° as too small to be an elevation. It is the elevation.
  *Fix shape:* take the base elevation off `CamParams` (`thirdp_pitch`, already parsed) and feed it
  as the resting elevation the head's own angle adds to, so `ChaseSwing` keeps working unchanged;
  `BaseUp`/`BaseBack` then reduce to "behind the tail" plus that angle. `thirdp_height`'s units are
  still unknown, so the radius stays where it is (`dist` + `dist_factor`).
  ⚠ **This moves every pinned golden shot with a chase camera**, so it is a re-pin, and the new pose
  drops the framing of every flight capture by 15°. Judge it at the controls before re-pinning: a
  camera level with the aeroplane sees less ground and more sky, and the original's own footage is
  the reference. ⚠ **Do not read the 15.7° as wrong-by-construction**: it was picked to look right
  and has never been judged against the authored figure side by side.
  *Cross-refs:* the numpad views' three level keys (`Kp2`/`Kp4`/`Kp6`), which sit at this same
  base elevation (`git log --grep=BL-150`), `docs/formats/camparam.md` (`thirdp_pitch`, and the Known limits paragraph this corrects),
  `docs/org/cameraViews.md` (head-look controller, the chase placement's own elevation).
- `BL-1023` `[Feature]` `[S]` `[Next: decide]` `[Impact: low]` `[Evidence: trace]` **In smooth look
  mode (`J`) a released right stick leaves the view where it was aimed, in the chase view and in
  first person alike, the way a released numpad direction or mouse pan already does.** Today the
  stick is absolute in every look mode: stick position is view position and centring the stick
  returns the view to its settled pose (`docs/controls.md`, the right-stick row). The `J` row lists
  only the numpad and the mouse as the inputs whose released position persists, so a pad player
  has no way to park the view off centre. *Evidence:* `CameraController.PadLook`
  (`CSVM/src/Flight/Camera/CameraController.cs:316`) is the chase view's own rigid path, fed from
  `FlightController`'s `_chaseLook` filter (`FlightController.cs:2268`), and `Chase` resumes the
  instant the filter reads released, whatever `HeadLook.Mode` is. In first person `HeadLook`'s
  smooth-mode branch (`CSVM/src/Flight/Camera/HeadLook.cs:301`) assigns the head target from the
  filtered stick and runs nothing once the stick is back inside `PadAimCentreBand`, so the head
  target stays near the stick's last filtered position there; whether that reads as parked or as
  a stick that never quite centres is unjudged at the controls. *Fix shape:* one rule for both
  views: in `LookMode.FreeLook` the stick's last aim is held on release (the chase path keeps the
  last `PadLook` angle pair rather than resuming `Chase`), and in snap mode both views return as
  they do now. A user decision first, since the absolute stick is a UX call of this port with no
  original to match, and a persistent stick changes how `K` and the centre key read on a pad.
  *⚠ Traps:* the persistence must take the stick's aim at the moment it crossed into the centre
  band, not the filter's lagged value after it, or the parked view drifts a little toward centre
  on every release; do not add a second mode key for the pad, `J` and `K` are the original's two
  and the pad shares them. *Playtest after fix:* `.\RunGame.ps1`, press `J`, push the right stick
  half over in chase then in Cockpit and let go: the view stays; press `K` and repeat: it returns.
  *Cross-refs:* `PT-120` (g) (the look stick's steadiness, judged in snap mode only),
  `docs/controls.md` (the `J`, `K` and right-stick rows), `docs/org/cameraViews.md` (head-look
  controller).

## HUD & UI

- `BL-181` `[Tuning]` `[M]` `[Next: code]` `[Impact: low]` `[Evidence: feel]` **Marker HUD + scoreboard layout is a provisional pass, not a
  fidelity sign-off.** Playtested 2026-07-30
  (`./RunGame.ps1 --stunt --chapter=C4 --plane=player_fury`): the stunt run HUD and scoreboard placement,
  fonts and distance units "work for now." The verdict is explicitly contingent: it says these read
  acceptably in isolation, and a fidelity sign-off needs them read against the chrome the rest of the
  game's UI uses, which does not exist yet. ⚠ **The composed campaign boards do not discharge this,
  and should not be read as doing so.** They are painted original artwork positioned at authored
  pixels with a per-background ink palette (`BoardPalette`), so they carry no type scale, no distance
  units and no shared font choice for an in-flight overlay to match. What this waits on is a UI
  surface that defines those three things for chrome the original did not paint, which is what the
  menu-hub milestone was standing in for. That surface now exists in first draft: the Original
  join board (`OriginalJoinBoard.cs`) hand-sets seven font sizes (heading 26, articles 22, subtitle
  and tag 14, device 17, rule 15, status 13) with a comment saying the shared scale takes them over.
  *Fix shape:* lift the join board's sizes into the type scale (one font choice, a size scale and
  a distance-unit convention for chrome the original never painted), pose the board on it, then
  re-review `StuntRunHud.cs`/`TargetHud.cs`/`StuntScoreboard.cs` placement against it rather than in
  isolation. Built-in's Start-to-join strip behind `--force-builtin` is the second join path
  `BL-951` said must fold into the board's own gesture; it folds in here. *⚠ Traps:* the board
  landed before the scale, so its sizes are a draft to lift, not a reference to match; do not add a
  third set of sizes for the HUD. *Cross-refs:* `BL-449`, whose landing prompted this wording,
  `git log --grep=BL-951`.

- `BL-1016` `[Cleanup]` `[L]` `[Next: decide]` `[Impact: none]` `[Evidence: trace]` **`OriginalOptionsScreen.cs`
  is 2535 lines and 326 members after absorbing four screens; each page becomes its own module
  behind the one form, and the two new `OriginalShell` partials fold into real types.** *Evidence:*
  the Audio, Video, Game Options and Controls screens were deleted into this one class. Its own
  summary argues it is one form, and the per-page `switch` arms near the row composer and the
  accept handler are the counter-evidence: every page-specific rule goes through the same two
  switches. The same review range added `OriginalCheats.cs` (140 lines) and
  `OriginalShellDialog.cs` (149 lines) as `partial class OriginalShell`, while halving the shell's
  other partials into the `IOriginalScreenModule` seam. *Fix shape:* one page module per former
  screen behind `IOriginalScreenModule` or a page-sized sibling of it, the form keeping only the
  frame, the ACCEPT/CANCEL plaques and the page switch; the cheats and the dialog become the
  types their file names already suggest, owned by the shell rather than spliced into it. *⚠ Traps:*
  the pages share the plaque-row pair and the section pitch table, so extract those first or the
  four modules duplicate them; a page module that reaches back into the form's fields for its
  layout is the form in another file. *Cross-refs:* `BL-1014`, `BL-1015`,
  `docs/menu-presentations.md`, `docs/architecture/UI.md`.

## Splitscreen

Our splitscreen mode (2–4 players) has no counterpart in the original, so every rule it authored
against "the player" or "the camera" needs an explicit splitscreen verdict: generalise it, take a
nearest/union rule, or record it as deliberately single/global. This theme collects that work
(sweep of 2026-08-15). **Route fixes through the two existing seams instead of minting new ones:**
`GameSession.PlayerPositions` (nearest human) for gameplay rules that say "the player", and the
viewer set behind `ProjectilePool.Viewers` / `ScreenSize.NearestFloor` for draw rules that say
"the camera". Sim state stays global, the mission wind is the worked example
(`Session/WeatherRig.Tick`, stepped once per frame outside the per-rig loop on purpose). Splitscreen-scoped items that live with
their own system: `BL-537` (the 4-player pool judgement), `BL-296` (per-player ActionMap),
`BL-314` (race countdown).

The theme's first batch (`BL-126`, `BL-365`–`BL-376`) landed via
`PLAN-splitscreen-polish` (2026-08-15,
complete, the chrome playtest F52/`BL-126` closed it out). New splitscreen findings mint here as
usual.

- `BL-380` `[Bug]` `[Blocked: per-instance fog shader uniforms]` `[L]` `[Next: code]` `[Impact: low]` `[Evidence: trace]` **Fog-zone selection stays
  player-1-only in splitscreen: `csky_fog_color`/`_range`/`_alt`/`csky_world_light` are one GLOBAL
  shader uniform set, written from rig 0's camera weather state alone
  (`Session/World/WeatherRig.cs:459-466`), so a pane on the other side of a fog-zone boundary from P1
  renders P1's fog, not its own.** Split out of the `BL-338` residual sweep 2026-08-15 (plan B14):
  the whiteout overlay and the deck regime are already per-rig (the same `WeatherRig.Tick` loop),
  only the fog GLOBALS lag behind, because `ApplyFogGlobals` writes session-wide shader uniforms,
  never per-instance ones.
  *Evidence:* `WeatherRig.cs:459`'s own comment: "Driven by rig 0, because the fog parameters this
  writes are GLOBAL shader uniforms, one set for the whole session ... In splitscreen with one
  player under the deck and one over it, both panes therefore wear player 1's fog." Pre-existing
  (`SetupWeather` always wrote one global set before splitscreen existed), not introduced by it.
  *Fix shape:* per-instance fog uniforms on every fogged mesh instance, selected by whichever
  pane's camera the instance should answer to, a shader-architecture change (per-instance state
  keyed off the viewer set), not a wiring change.
  *⚠ Traps:* a second `RenderingServer.GlobalShaderParameterSet` call does not fix this, that is
  still one value for the whole process, not one per viewport. Any new `instance uniform` this adds
  to `shaders/csky_instance_uniforms.gdshaderinc` must be APPENDED, never inserted, Godot assigns
  instance-uniform slots by declaration order per shader, and the file's own header names the
  2026-07-17 `csky_fog_on` index-collision bug this ordering contract exists to prevent.

- `BL-389` `[Tuning]` `[S]` `[Next: look]` `[Impact: low]` `[Evidence: feel]` **Splitscreen weapon mix needs a retune: rockets too quiet, guns too loud,
  especially four guns firing at once.** Found at the `BL-126` chrome playtest (F52,
  2026-08-15), `FlightAudio.MixGain`/`Projectile.cs`'s pool `MixGain` (the `1/sqrt(N)` equal-power
  attenuation D31/D32 landed) reads right in isolation but the per-weapon balance under it does
  not: a 4-player Dogfight with simultaneous gunfire is too loud relative to rocket explosions,
  which read as too quiet against it. *Look for:* rocket vs. gun relative level across 2P/4P,
  specifically four guns firing together. *Fix shape:* a judgement call at the controls on the
  per-def volume terms feeding `Projectile.cs`'s `def.Volume * 0.2f * MixGain * distanceGain`
  (line ~2238), not the `1/sqrt(N)` splitscreen term itself, which is confirmed correct.

- `BL-434` `[Research]` `[M]` `[Next: look]` `[Impact: low]` `[Evidence: trace]` **Splitscreen cockpit interior/audio behaviour is unprofiled and unjudged
  past one pilot.** `PLAN-cockpit-view` (Decision 5) built cockpit rendering and the
  `cockpit_engine_sound` swap verified single-player-only, no further. (a) **Per-viewport interior
  draw cost is now profiled, not yet judged**: `analysis/campaign-coop-4p-perf/FINDINGS.md` (D33)
  measured CM18 (C4/M03) at 1P/4P x external/cockpit: cockpit view adds ~5% more draws at both
  player counts (697 to 733 at 1P, 3972 to 4169 at 4P) and ~0.4-2 ms of frame time, both within or
  just past this machine's measured noise floor (`docs/verification.md` PERF-9…PERF-11); going 1P
  to 4P moves draws 5.7x (697 to 3972, more than the 4x pane count) while `render_cpu_ms`/`gpu_ms`
  stay flat, so the draw-count growth outpaces panes and has not yet been isolated to the cockpit
  subtree specifically vs. the rest of the per-pane rig. (b) **The per-pilot `cockpit_engine_sound`
  swap against splitscreen's `MixGain` term is unjudged at the controls**; the single-player
  engine level is judged matching (`git log --grep=BL-391`), a listen that never isolated the
  `_cp` def specifically. (c) **Today's hiding mechanism is node visibility on a shared plane node, not a
  per-viewport render flag**: `CockpitVisibility` hides the OWN rig's `healthy` body node, so a
  pilot sitting in the cockpit hides THAT AIRCRAFT'S body in every pane that can see it, not just
  their own, a cross-pane effect unjudged at `N > 1`.
  *Fix shape:* isolate the draw-count growth's split between the cockpit subtree and the rest of a
  4P rig; a splitscreen listen for the cockpit-swap/`MixGain` interaction; confirm or fix the
  cross-pane body-hide visually at the controls with 2+ cockpit-view pilots in the same session.
  *Cross-refs:* `PLAN-cockpit-view` B11 ("Splitscreen posture"), `BL-389` (splitscreen weapon
  mix, same playtest family).

## Missions, modes & campaign

- `BL-314` `[Feature]` `[Blocked: PT-45]` `[L]` `[Next: look]` `[Impact: high]` `[Evidence: feel]` **Race countdown, a rolling start on rails before the run clock
  opens.** The abreast starting grid landed 2026-08-08 (`StartGrid`), so every pilot in a splitscreen
  stunt race now begins on one line, on one heading, at one altitude. What is still missing is the
  moment a race starts: today the clock is running the instant the world appears, so whoever's
  loading screen ends first is flying first. Deliberately split off from the grid rather than landing
  with it, because it changes a **deliberate** clock rule and that rule deserves its own scrutiny.
  Do not start it before `PT-45` has judged the grid at the controls, a countdown over a grid nobody
  has flown tunes the presentation of an unvalidated start.

  **The shape, as decided.** (a) A **rolling start**, not a full freeze: the aircraft stay
  physics-alive and moving through the count, which reads as a race start rather than four parked
  planes popping into motion, and is exactly as fair as a freeze since nobody may manoeuvre. (b) The
  countdown flight is **on rails**, a kinematic level walk of the field, driven straight into
  `_model.Reset(...)` (`FlightController.cs:649` is the existing call shape:
  `_model.Reset(pos, attitude, SpawnSpeed, throttle)`), arranged so that **GO is exactly today's
  spawn state**. Nothing is simulated during the count, so there is no sink to fight, no per-plane
  divergence, and the handoff is the pose the flight model already starts from. (c) `--det` never
  sees any of this: like the grid, the countdown is reached only through the race path, so a scripted
  run must remain byte-identical and the whole feature stays hand-flown verification only.

  **⚠ Traps, read before touching this.**

  1. **Do not derive the pre-GO setback from a speed.** Spawn speed is the mission's own
     (`PLAYER_INIT[4] × 0.1`, 18 m/s in nearly every mission), resolved per session by
     `SpawnPicker.StartState` and carried on `FlightStart`
     ([`docs/formats/spawns.md`](docs/formats/spawns.md)). It is data, so it differs
     between missions and can differ again whenever a mission is re-read; any "start N seconds back
     at the spawn speed" arithmetic therefore hard-codes one map's number into a rule meant to hold
     on all of them. The on-rails walk above avoids this by construction: it simulates nothing and
     it ends on the spawn pose whatever the speed is.
  2. **This changes `StuntMission.Elapsed`'s documented rule.** "The clock never stops" is stated
     twice and on purpose (`StuntMission.cs:109-113` on the property, `:247-249` on `Tick`), it is
     why a mid-run crash freeze still costs you time. A countdown means the clock must not *start*
     until GO, which is a different claim from stopping it mid-run; make the distinction explicit in
     both comments rather than deleting the rule, or the next reader reads the crash freeze as
     negotiable too.
  3. **Do not simulate the count and do not freeze the sim.** A physics-alive count that is actually
     flown re-opens the sink (`FlightController.Respawn`'s start state) and diverges per plane; a hard freeze
     was rejected as the presentation this mode wants. Both are the alternatives already considered.
  4. **The instrument is a hand-flown sitting, not a screenshot.** A race grid is not photographable
     (chase-cam panes; a 60 m neighbour is out of frustum), and `--det` cannot reach this path at
     all, so an automated check can prove only that scripted runs are unchanged. Everything about
     whether the count *feels* like a race start comes from `PT-45`'s sortie.

  *Unlocked by the grid, noted here rather than promised:* race best-times become feasible once a
  race has a defined start (`StuntRace.cs`, `ScoreStore.GetBest`/`RecordIfBest`), and would want
  their own key namespace, since a countdown makes race and solo totals diverge again.

- `BL-1015` `[Cleanup]` `[L]` `[Next: decide]` `[Impact: none]` `[Evidence: trace]` **`GameSession.cs`
  is 4402 lines and changes for unrelated reasons; the build steps and the per-mode runtimes it
  hosts leave as real modules.** *Evidence:* one review range added 532 lines over scattered hunks
  for the load screen, the Instant Action and campaign flows, the results boards, the debris and
  weather rigs and the debug dumps, none sharing state with the others beyond the `BuildState`. The
  class is a single file with no partials, so its size is the plain measure. *Fix shape:* the
  ordered build steps that read `BuildState` and write one subsystem each (the pattern
  `WorldEffectsFactory` already follows) move behind a build pipeline the session composes, and
  the mode-specific tails (Instant Action, campaign, versus) move onto the directors that already
  own those modes. Measure by the session's public member count. *⚠ Traps:* a `partial` file is
  not a split. The per-frame tick order across the runtimes is the one thing the session must
  keep in one place; do not scatter it into the extracted modules. *Cross-refs:* `BL-1014`,
  `BL-1016`, `docs/architecture/Session.md`.

## Tooling, platform & docs

- `BL-033` `[Cleanup]` `[Blocked: SDL >= 3.4.4]` `[S]` `[Next: code]` `[Impact: none]` `[Evidence: data]` **Drop the `SDL_JOYSTICK_DIRECTINPUT=0` launch-script workaround** (set 2026-07-19 in
  RunGame.ps1/RunDev.ps1) once tools/godot ships a Godot bundling **SDL ≥ 3.4.4**. *Decision:*
  wait for that SDL; the variable stays in the launch scripts and out of the export until then,
  and the item lands with the Godot bump that carries the fix. The bundled
  SDL (3.2.28 up to Godot 4.7.1) hard-freezes the engine when a >255-button DirectInput device
  disconnects, the 8BitDo Ultimate 2 dongle's HID interface is one (`Uint8` loop counter vs
  uncapped dinput `nbuttons`; godot#115667, SDL#14961, fixed by SDL#15304). Check the bundled
  `thirdparty/sdl/joystick/SDL_joystick.c` `SDL_PrivateJoystickForceRecentering` for the `int i`
  fix before removing. Side effects while active: DirectInput-only controllers (non-XInput sticks
  without an SDL HIDAPI driver) are invisible in-game, and, since the var is set by the launch
  scripts and never by the export, a shipped build enumerates DirectInput devices that no dev or
  test run sees. An 8BitDo Ultimate 2 arrives as three joypads there, which is how a device that
  holds a roster position without producing input came to take the seat `AssignPads` fills by
  position; `Pads.LogPads` records the roster so the next one reads off the log rather than being
  inferred. Dropping the var also closes that divergence.

## Misc

- `BL-284` `[Bug]` `[Blocked: CAP-34]` `[M]` `[Next: look]` `[Impact: low]` `[Evidence: footage]` **Wing-light flare: soft round glow vs the original's sharp star burst.** Follow-up from
  `BL-119`: with the blink at the measured ~1 frame, the flare reads as a compact soft amber glow,
  much closer than the old billboard blob, but the PT-03 reference still shows sharp radiating star
  points that our plain radial `oil_liteflare` sprite does not produce. Whether the original draws
  the flare from every angle is answered by data: the flare mesh is a `Facade`/`SphericalY` model,
  and `PlaneBuilder`'s flare material poses it through `csky_facade_spherical`
  (`git log --grep=BL-1039`). The orbit clip settles the star shape alone; any player plane works: `vehicle.zrd.json`
  wires `wing_lights_blink` (or `brigand`'s own `wing_lights_brigand`) into every player craft's
  `start_anims` except the Bloodhawk, which has neither the anim nor flare nodes. (Earlier notes
  here said only piratefighter/brigand carried it, that read `wing_light.zrd.json`'s two
  `ANIMATION_DEFINITION`s alone; `vehicle.zrd.json`'s per-plane `start_anims` is the wider
  wiring and is what the runtime actually plays from, per `WingLights.cs`'s doc comment.) Also riding
  here: `WingLightBlinker.LightEnergy = 1.0` is a declared TUNE, the def authors the point
  lights' range/colour only, no intensity.
  ⚠ Traps: (a) Godot's billboard mode is the rejected fix, PT-03's screenshot is against it; the
  facade look-at the flare takes reads the eye's position alone, so a bank cannot roll it.
  (b) don't edit or swap the sprite to fake the star: the star points may be the original engine's
  flare *rendering* (a cross-flare pass), not the texture asset, the orbit clip decides first.
