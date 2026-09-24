# Testing

The in-engine assertion harness behind `--run-tests`, plus the `CSVM.Tests/` xUnit project's engine-free reader units and golden invariants. The `--dump-*` probes, the capture loop and the glTF export the suites also drive live in `Tooling.md`.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## CSVM.Tests/
The xUnit project `dotnet test` runs (net8.0, `ProjectReference` to `CSVM.csproj`): the
engine-free reader units (`Zrdr`, `WavFile`, `SoundDefs`, `WeaponDefs`, `Messages`,
`SessionPaths`, `GameZ` transform arithmetic, `MarkerRig`, `AnimDefs` and the rest) plus the
golden invariants over a player's own `extracted/`. `TestData.cs` owns both input sources, the
hand-authored `fixtures/` tree and the `CSVM_DATA_ROOT` lookup behind `[ExtractedDataFact]`, and
states the rule binding each. A check that needs a live Godot belongs in `CSVM/src/Testing`
instead; read `TestHarness.cs` for that half.

## src/Testing/TestHarness.cs
`--run-tests[=filter]`: the suite registry, `TestContext` (assert verbs, resolved data paths, a
scene-tree host, `SyncPhysics` for the space a one-frame run leaves behind, and the `WithWorld`
chapter-world builder over `WorldSession`), the PASS/FAIL/SKIP table, `test-report.json` in
`TestContext.ScratchDir`, and the process exit code. `Select` is the pure selector over the flag's
value; `SuiteShards` handles the one term that divides rather than selects. The world cache and its
eviction, the mission-override and private-world forms, the shared `DecodeCache`, the per-build
`StartupProfile` and the engine-error allowlist each carry their own rule at their member. Read
`SuiteCatalog.cs` for registration and `PhaseAttribution.cs` for a build's time in the report.

## src/Testing/SuiteShards.cs
Godot-free and pure (`CSVM.Tests` proves it without the engine): the `shard:<index>/<count>` term
and the division behind it. `Parse` lifts that term out of a `--run-tests=` value and hands the rest
back as the selector; `Plan` divides an already-selected list longest-unit-first onto the lightest
shard and returns each shard in the input's order, so one tree always divides the same way.
`SuiteWeights` reads the measured per-suite seconds in `analysis/engine-suite-weights.json`, with a
default for a suite the file does not name and `Groups` for the sets a shard may not split. Read
`TestHarness.cs` for where a plan is applied.

## src/Testing/PhaseAttribution.cs
Godot-free and pure (`CSVM.Tests` proves it without the engine): buckets a `StartupProfile`'s raw
phase names into archive/decode, sound preparation and runtime/world construction, and does the
suite-level arithmetic (`Rest`, `Overrun`) behind the console phase suffix and `test-report.json`'s
per-suite and run totals. Which phase name lands in which bucket, and what happens to one that
lands in none, are stated at the sets themselves. Read `TestHarness.cs` for where a live profile is
fed in.

## src/Testing/CountingEmitterFactory.cs
`IEmitterFactory` for a suite: `Create` always succeeds and hands back a `CountingEmitter` that
holds no Godot type in its own state, just started/stopped counts, whether it is sustaining now and
the last position it was fed. Reached by installing it on `TestContext.EmitterFactory` before a
`WithWorld` build; `Built` is the list a suite reads to confirm the fake was reached rather than a
real `Puffer`. Read `RecordingEmitterRenderer.cs` for the seam one level lower.

## src/Testing/RecordingEmitterRenderer.cs
`IEmitterRenderer` for a suite: it keeps the particles a `Puffer` hands it (`LastFrame`, `Shown`,
`MaxShown`, `MaxIndex`, `MaxFrame`, plus the `Capacity` the emitter sized) instead of drawing them,
so a suite asserts on burst, distance-trail and sustain with no atlas, `TextureArchive` or
`MultiMesh` in the path. The mirror of `CountingEmitterFactory` one seam lower: that fake replaces
the whole emitter so `EmitterDirector`'s lifetime is assertable, this one replaces the draw so the
emitter's own modes are. Neither covers the other's job.

## src/Testing/SuiteCatalog.cs
The registry of the in-engine assertion suites, discovered from the `[Suite("name", "what")]`
attribute each body carries in the `*Suites.cs` modules; the catalog keeps no per-suite table, so
adding a suite means adding one marked body in one of those modules and nothing else. `QuickTier`
is the checked-in membership of `--run-tests=tier:quick`, resolved through `Tier(name)`. The
determinism rule behind the alphabetical registry order, and why a malformed declaration throws
rather than being skipped, are stated at the members themselves. Read `SuiteShards.cs` for what
depends on that order.

## src/Testing/*Suites.cs
The in-engine scenario bodies, one module per domain: the emitter model, combat and ordnance,
Instant Action, AI, targeting, wingmen, the campaign and its menus, music, zeppelins, damage and
destroy choreography, animation and effects, the built world's data gates and censuses, and the
landing, capture and coop surfaces. Each module holds one or more `[Suite("name", "what")]` bodies,
a `static void` taking a `TestContext`; `SuiteCatalog` discovers them by that attribute, so adding
a suite means adding a marked body to the module that already covers its domain, or a new module
when none does, and registering nothing anywhere else. The membership itself is the catalog's
output: `--run-tests` prints the table and writes `test-report.json`. They reach the harness only
through `TestContext`, and shared fixtures are separate focused modules (`SuiteConstants.cs`,
`BurstTimeline.cs`, `SuiteViewers.cs`, `EffectStageSuiteHelper.cs`, `MenuSuiteHost.cs`) rather than
an all-purpose helper. Per-suite traps live as comments on the suites themselves, in code.

## src/Testing/SuiteConstants.cs
The shared golden inputs used by more than one scenario module: airframe and weapon counts, puffer
timing, the destructible census, texture samples, and the ordnance-burst step and slack.

## src/Testing/BurstTimeline.cs
The three value types describing an authored ordnance-burst timeline and its observed dispatches.

## src/Testing/SuiteViewers.cs
Builds a test pane camera at a supplied world position for suites that exercise `ViewerSet`.

## src/Testing/EffectStageSuiteHelper.cs
Builds and frees a production-shaped, pooled effect-template stage for mesh-visibility suites.

## src/Testing/MenuSuiteHost.cs
The launchscreen fixture a menu suite stands a `LaunchMenu` on. `Bare` builds a `MenuHost` over an
empty `PresentationRegistry`, a silent `IMenuAudio` and a caller-owned exit list; `AddFeatures`
registers the same Free Flight, Instant Action, player-setup, hangar, campaign and controls
features the launcher wires; `Build` stands a launchscreen on a host over the suite's own
`ScratchPlanes` store, and `Menu` does so on a bare host. Presentations take that store too, so no
menu suite reads `user://Planes`; `DropScratchPlanes` removes it. Seat 0 joins through the setup
feature, so it is added after the features; the controls feature is the form that saves nothing.
Read `UI/Menu/MenuHost.cs` for the host itself.
