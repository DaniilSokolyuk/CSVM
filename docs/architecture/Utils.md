# Utils

The things every subsystem depends on: the clock, the log, the seed. Changing one of these changes determinism repo-wide; read `docs/verification.md` first. `Utils` is a leaf: no file here names a `Flight`, `Session` or `Effects` type.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Utils/HoldToRepeat.cs
Tap-versus-hold timing for a single button: `Press` arms the timer, `Tick(dt)` returns true once
the initial delay has elapsed and then once per repeat interval until `Release`. A zero repeat
interval fires every tick after the delay, which is the pause transport's "hold to step every
rendered frame"; a nonzero one is `MenuInput`'s d-pad initial-delay and repeat-rate shape, driven
through `MenuInput.StepAxis` for both cursor axes. Engine-free, so it decodes without a device.
Read `TapHoldButton.cs`, which wraps it for the tap-or-hold split.

## src/Utils/TapHoldButton.cs
One button carrying two actions, split by how long it is held. Feed it the button's LEVEL each
frame; it edge-detects, times over `HoldToRepeat`, and answers `TapHold.Tap` / `Hold` / `None`.
Engine-free: the input read stays with the caller, which is what makes the decoding unit-testable
when the device is not. Pinned by the `target-input` suite.

## src/Utils/GameClock.cs
The session's simulation clock: `BeginFrame(wallDelta)` sets `Steps` and `Dt`, and `GameSession`
turns those into requests to `SessionSimulation`, so pausing, single-stepping and fixed-dt replay
all enter through one ordered step. Modes are Realtime, FixedAccum (the interactive anim lab) and
FixedStep (scripted runs and `--det`); `Halted`, `SimHeld` and `AuthoredAnimationHeld` are
orthogonal holds, each carrying its own rule at its own field. Published as `GameClock.Current`,
session-scoped and nulled on teardown, where null reads as the raw frame delta. Read
`RenderPoses.cs` for the render half of the same tick and `ShaderTime.cs` for the shaders' view.

## src/Utils/RenderPoses.cs
The render half of the fixed-tick simulation, shared by every subsystem that moves something
visible. A realtime session advances poses on the 60 Hz physics tick while the display redraws at
its own rate, so a pose drawn raw is held for a frame and then jumps. Two consumption shapes over
one gate and one fraction: a node writer calls `Record(node)` right after writing the node and
`Draw()` places it between the last two simulation poses, while a subsystem holding its own
coordinates (`Projectile`'s rounds) reads `Fraction` and interpolates its own pair. `Restore(tick)`
opens every physics callback, so nothing the simulation seeds from a node's transform can observe
a drawn one. Realtime only; elsewhere `Fraction` is 1 and every path is an identity rewrite.

## src/Utils/Log.cs
The diagnostic log: `Log.Info("world", $"…")` and its `Warn` / `Error` / `Debug` siblings over a
fixed category vocabulary and four levels, with two sinks that have different jobs. The console is
the human's view and stays quiet by default; the `<mode>-<stamp>.log` file is the machine's and
always takes everything, line-flushed. `DirectoryFor` decides where that file lands, in one place:
`.scratch/logs/` in a repo run, a plain `logs/` beside the exe in an exported build. `ConsoleSink`
and the scoped `PushConsoleSink` redirect console lines, so a plain class that logs is callable
without an engine. Categories, levels, the file-line grammar, the `--log=` filter and the sink
path: [../org/logging.md](../org/logging.md). `HitchSidecar.cs` shares this sink's stem.

## src/Utils/BuildVersion.cs
The build's own version, read once from `application/config/version` in `project.godot`, which is
the number's one home. Three surfaces state it back so a report names its build without being
asked: `Log.Open` writes it as the log file's first line, `UI/Screens/BuildStamp.cs` draws it in the menu's
corner, and the Windows export preset stamps it into the exe's file properties (`ExportRelease.ps1`
reads the same key to name the zip, and refuses an export whose exe does not carry it). Reads
`ProjectSettings`, so it resolves only inside a running engine, which is why `Log.Open` takes the
version as an argument instead of reading it.

## src/Utils/ShaderTime.cs
The GPU's view of the clock: the `csky_time` global shader uniform (seconds), registered once in
`Launcher._Ready` and written once per rendered frame from `GameClock.Time`. Every animated
shader this project generates reads it instead of Godot's `TIME`.

## src/Utils/StartupProfile.cs
The always-on startup timing report: one `[perf] startup …` line per session build, split into the
phases the build spends its time in. `Mark()` and `Record(phase, mark)` are ambient statics over
`Current`, so the shared build code (`WorldSession`, which the test harness also drives) records
without being handed an accumulator. `Phases` is a read-only view of the same accumulator for a
caller that wants to aggregate the spans without emitting the line, which is how
`src/Testing/PhaseAttribution.cs` reads a harness build. Line grammar, the phase rules and the
`boot` / `rest` / `first_frame` terms: [../org/startup-profile.md](../org/startup-profile.md).

## src/Utils/LoadProgress.cs
The load screen's progress while a build holds the frame loop: the original's sixteen authored
milestone fractions, one per `LoadStep`, a monotonic setter that no step can drag backwards, and a
wall-clock pump throttled to one repaint every 0.1 s. The throttle holds off only a step that left the bar where it was, since our phases can cross ten milestones inside one window and a bar the build skips past shows its first fraction and then the mission; `Trace` and `Draws` are the read-back, every step against the build's own wall clock with the undrawn ones marked.
Ambient over `Current` for the reason `StartupProfile` is, so `Launcher`, `GameSession` and `WorldSession` report a boundary they have crossed without being handed a sink, and a launch with no screen over it draws nothing at all.
Engine-free: `FillPixels` and `FrameAt` are the bar's pixel clip and the propeller's frame, and
`UI/Screens/LoadBoard.cs` is what turns them into a drawn frame. Decode:
[../org/loading-screen.md](../org/loading-screen.md).

## src/Utils/StartCover.cs
The ramp behind the cover a session starts under: opaque while the world is still assembling, then
up from a dark tone over about a second once the session reports the first frame the player is
meant to see. Owns the tone, the fade length, the clamp on the huge delta a blocking build hands
the frame that closes over it, and the hold cap that releases a cover no session ever answers. A
`--det` run builds a disabled ramp that covers nothing, so no pinned golden and no `--frames=N`
shot sees it. Engine-free; `UI/Screens/SessionStartFade.cs` paints it and `Session/Launch/Launcher.cs` owns when
one is raised.

## src/Utils/HitchMonitor.cs
The always-on frame-hitch detector, ticked from `Launcher._Process` in every mode: a frame costing
far more than its recent neighbours gets a `HitchRecord` assembled for it, describing the frame's
cost split, its engine counts as absolutes and as deltas, its GC activity, the ring of frames
leading up to it, and the named work `PerfSample` attributed. It only detects, and nothing is
logged from here, so a clean run is silent and `HitchSidecar.cs` owns the write path. Godot-free by
construction (the caller samples the engine counters into a `FrameCounters` and hands them in), so
the trigger and its edges unit-test off-engine. Trigger formula, the `hitchMonitor.*` keys and the
record's fields: [../org/hitch.md](../org/hitch.md).

## src/Utils/HitchSidecar.cs
`HitchMonitor`'s write path: a tripped record is copied, never referenced, into a small
preallocated queue and drained a few seconds later to one `[perf] hitch …` line plus one JSON line
in `.scratch/logs/<mode>-<stamp>.hitches.jsonl`, which shares the main log's stem. Writing off the
hitching frame is the point, since a string interpolation and a file write are avoidable
allocation-heavy work at the worst possible moment. A record is all numeric apart from the site
names, so the JSON is hand-written rather than serialized. Line grammar, the JSON shape, the
`hitchSidecar.*` keys and the flush behaviour: [../org/hitch.md](../org/hitch.md).

## src/Utils/PerfSample.cs
Ambient timed leaf scopes: `using (PerfSample.Scope(PerfSite.DebrisSpawn))` adds its wall time to
that site's total for the frame in progress, and any code path can do it without knowing the
monitor, the readout, or whether anything is listening. Statics over a preallocated per-site array,
the same ambient shape `StartupProfile` uses and for the same reason, since a scope several call
layers down cannot be handed an accumulator. `Launcher._Process` calls `EndFrame()` where it stamps
the frame's wall cost, so the scopes and the `frame_ms` they ran inside describe the same span. The
site vocabulary, the seeded call sites and the attribution terms a record carries:
[../org/hitch.md](../org/hitch.md).

## src/Utils/PaneReadback.cs
One frame of a viewport read back without stalling the frame that asks: the Danger Zone camera's
own viewport (`Flight/Modes/DangerZonePhotograph.cs`), a pilot's pane where no frames draw, and the
screenshot key's frame. A synchronous `GetImage` plus a PNG encode of a 5120x1440 pane costs the
frame that runs them over a second. The request copies the viewport's render target through
`RenderingDevice.TextureGetDataAsync`, whose callback arrives after the device's frame queue, and
a worker builds the image (RGB8 for an opaque viewport, as `GetImage` answers) and runs the
caller's continuation, which encodes there too and defers to the main thread for anything touching
the scene. No rendering device, or a render target other than RGBA8, falls back to the synchronous read.

## src/Utils/WallCostBank.cs
One `--perf` cost meter: the open/close bracket, the banked wall milliseconds, the worst single
span, the spans that closed, and a tally carried alongside them (the planes an AI walk stepped).
`PhysicsTickCost`, `ProcessPassCost` and `AiStepCost` are three instances behind static facades
that name their own terms and drop the slots their readout does not print, so the drain semantics
are written once: a close with no open standing banks nothing, a reset drops a half-open span, and
a span still open at the drain is carried into the next window; an optional timestamp source lets a
test assert all three by value. `WallCostBracket` opens or closes one end of a pair, named from the
bank's label and pinned to an extreme of the process or the physics priority order.

## src/Utils/PhysicsTickCost.cs
The wall cost of one whole Godot physics tick and how many ticks a wall second actually got,
measured by two `WallCostBracket` nodes pinned to the extremes of the physics priority order so
the pair spans every `_PhysicsProcess` callback in the tree. `Take()` drains the window as total
milliseconds, the worst single tick in it and the tick count; `NominalHz` turns that count into the
sim seconds a wall second bought, which is what shows a sim running at half speed. Godot's
`TIME_PHYSICS_PROCESS` monitor answers neither question, and the misreading it invites is
`docs/verification.md` PERF-1.

## src/Utils/ProcessPassCost.cs
The wall cost of one whole `_Process` pass, measured by two `WallCostBracket` nodes pinned to the
extremes of the process priority order so the pair spans every `_Process` callback in the tree.
`Take()` drains the window as total milliseconds, the worst single pass in it and the pass count
the `--perf` window means over; a caller reading from inside the pass gets the frame in progress in
its next window instead. `PhysicsTickCost` above is the same bank around the physics tick. Godot's
`TIME_PROCESS` monitor answers neither question, and the misreading it invites is
`docs/verification.md` PERF-1.

## src/Utils/AiStepCost.cs
The wall cost of the flight roster's AI walks and how many aircraft they walked, banked by an
`Open`/`Close` pair around `SessionSimulationRuntime.StepCapturedAiAircraft` rather than by bracket
nodes, since no priority order isolates the walk from the callback it rides. It is the only
`--perf` term that attributes frame cost to the AI: `proc_ms` and `phys_tick_ms` bracket whichever
callback the clock mode makes the walk ride, so a plane-count sweep otherwise reads only as a
whole-frame differential. The plane count rides the bank's tally slot, so `ai_planes` comes off the
same bracket as `ai_ms`, which divides its total by FRAMES rather than walks.

## src/Utils/PhaseCost.cs
A row of named WallCostBank slots, one per phase of a pass: only one is open at a time, and opening the next closes
the one before it. SimPhaseCost is the row SessionSimulation.Enter and AnimRuntime's physics advance feed, one slot
per phase, printed as sim_ms= divided by the window's TICKS so it reads beside phys_tick_ms; ProcessSiteCost is the
row a using scope at the projectile pool's, animation runtime's, aircraft's, emitters' and session's _Process feeds,
as proc_sites_ms= divided by FRAMES beside proc_ms. Each slot prints mean/max, the max the longest single span, so a
stall names its slot; the gap to the whole-pass term is unnamed. Each slot also banks the BYTES allocated inside it,
drained by the same TakeRow and printed by AllocRow as sim_alloc_b=, since a millisecond figure names where a GC pause
landed and never what earned it (verification PERF-34).

## src/Utils/GcTrace.cs
The `--perf` GC readout: one `[perf] gc` line per ten wall seconds carrying the pause the process
spent, the collections it spent it in, the bytes allocated, and how many FINALIZABLE objects died,
read off the runtime's own `GCHeapStats` event through an `EventListener` on the GC keyword. The
finalizable count is the figure a change to the frame path moves, and pause per wall second the
figure it is judged on; every line carries process uptime so the world-build regime is excluded by
uptime rather than by guesswork. What the two numbers mean and why the per-collection pause is the
wrong one to read is `docs/verification.md` PERF-19 and PERF-20. Under `--gc-types` the listener
runs Verbose and adds a `[perf] gc-types` line naming the allocation sampler's most-seen types.

## src/Utils/Rng.cs
The session's randomness policy: one master seed and a named generator per subsystem derived from
it, independent across subsystems so a draw added to one cannot shift another's. The stream names
are the `public const string` fields on `Rng` itself, each carrying the reason it is its own
stream. `Reset(master, pinned)` runs once per session build, before anything draws; `Stream(name)`
is the shared generator, `SeedFor` / `IntSeedFor` the pure seed, and `NewIntSeed` /
`NewSystemRandom` a per-instance stream off the subsystem's own. Unpinned, the master comes from
`TimeSeed()` so the shipped game keeps its variety; a scripted flag implies `--det` and pins it.

## src/Utils/EffectPools.cs
The `CSVM/data/effect_pools.json` reader: how many copies of each effect-template ROOT the
world-effects stage builds. Hand-authored engine config, in a file rather than a `const` because it
is invented; the original copies its template per CALL_ANIMATION and has no such number, so any
finite pool is our approximation and the user must be able to move it without a rebuild. Three
sections for three namespaces: `roots` / `default` (the world stage, per-player scaled),
`localCallRoots` (library-root clones) and `crashRoots` / `crashDefault` (the per-player crash
rig). `SlotsFor(root, players) = clamp(base + perExtraPlayer × (players − 1), 1, maxSlots)`, and
`UnknownRoots` / `UnknownCrashRoots` name an authored root the derived stage set does not carry.

## src/Utils/Config.cs
Dev-facing tuning-override layer: static `Config` parses an optional sparse `res://config.json`,
and the typed getters (`GetFloat` / `GetInt` / `GetBool` / `GetString`) return the file's value for
a present key, else the caller's in-code `const` default, read through at the point of use. Keys
are `moduleCamelCase.fieldCamelCase`, grouped one nesting level in the JSON and flattened to
dot-keys. Nothing writes the file and `config.json` is git-ignored, so the consts stay canonical;
querying a key is also what registers it for `--dump-config`. `Config` names none of the modules
that read it: the startup read of every key is `Session/Launch/TuningWarmup.cs`.

## src/Utils/EffectsLevel.cs
The original's graphics EffectsLevel option as a config key (`graphics.effectsLevel`: `high`,
`medium` or `low`, default `high`), and the one global it drives today:
`csky_clutter_fade_scale_sq`, the squared distance scale every templates-clutter fade and the
`fvol` cloud field's own fade multiply into their camera distance. The level's meaning and direction:
[../formats/templates.md](../formats/templates.md). A second key, `graphics.clutterFarFade`, is the
remake's own switch: false resolves the global to a never-fades scale, so clutter draws out to the
fog instead of ending at the authored metres. Enhanced mode scales it by
`WeatherRig.EnhancedFogScale()` squared. Read `ClutterBuilder` and `MapEdgeExtender` next.

## src/Utils/GraphicsMode.cs
The opt-in enhanced-lighting mode's setting (`original` or `enhanced`, default `original`),
resolved once by `Launcher._Ready` into the single boolean `GraphicsMode.Enhanced` every later
scene builder reads, so the sources can be layered without touching a reader. The order mirrors
`PresentationResolution`'s: `--graphics=` beats the saved `graphicsMode` option (`OptionsStore`),
which beats the `graphics.mode` config key, which beats the default; an unknown word at any layer
warns and falls back. `--det` drops both machine-state layers and keeps only an explicit
`--graphics=`, which is how a golden or a deterministic capture pins the mode on purpose. The mode
itself is written up as a divergence in `docs/architecture/Root.md`.

## src/Utils/VSyncSetting.cs
The frame pacing, one setting carrying both whether the loop waits for the screen and the cap it
runs to without it, since a cap only means anything with V-Sync off. `Resolve` layers the sources
the way `GraphicsMode` does: `--no-vsync`, then the saved `vsync` word, then the `display.vsync`
config key (true turns it on), then off and uncapped; a word outside `DisplayWords.VSyncChoices`
reads as never set, and `Default` is the word a never-set VIDEO row shows. `SavedWord` holds the
`--det` guard. `Apply` is the one place `DisplayServer.WindowSetVsyncMode` and `Engine.MaxFps` are
called, by `Launcher`'s startup and its Options apply, and logs the source that won. The cap is a
render rate and reaches no simulation. Read `Session/Launch/Launcher.cs` next for both call sites.

## src/Utils/DisplayModeSetting.cs
The window's display mode over `DisplayWords.DisplayModes`: a bordered window, a borderless one filling
the screen, or exclusive fullscreen. `Resolve` layers the way `VSyncSetting` does with one layer fewer,
there being no config key: the saved `displayMode` word, then borderless; an unknown word reads as never
set. `SavedWord` holds the `--det` guard. `Apply` is the one place `DisplayServer.WindowSetMode` is called
and skips it when the window already stands in that mode. Godot's names invert the reading: `Fullscreen`
is the borderless window, `ExclusiveFullscreen` the exclusive mode. Nothing here touches focus, which
`Launcher._Ready` owns (`../verification.md`'s SHELL-13); that startup call is skipped for a scripted run,
which keeps `project.godot`'s windowed 1280x720 viewport, so the borderless default never reaches a golden.

## src/Utils/ResolutionSetting.cs
The size the game draws at, whose meaning the display mode sets. Godot exposes no video-mode list, only a screen's own size, so `Sizes` builds the
per-screen `SizeList` as the standard desktop sizes that fit inside `ScreenGetSize` plus that size, the list's fallback; the standard table's 4:3 ladder
carries four rungs, that being the original's own shape. `SizeList.Including` widens a list with the size `options.json` names where the table lacks it and
the screen holds it, which is how a hand-written size is shown, picked and saved back rather than replaced; `Unknown` is the engine-free list, `SavedWord`
the `--det` guard. `Resolve` layers the saved `resolution` over the list, a size the screen cannot hold falling back to that fallback and never to the
nearest, which would hand the player an aspect ratio they did not pick; a `Pinned` mode, which borderless is, takes the fallback whatever is saved, and both
Options screens draw the size row dead under it. `Apply` sizes only a windowed window and re-centres it, a resize growing off-screen; Godot's exclusive
fullscreen keeps the screen's size on Windows and switches no display mode, so the chosen size becomes the render target through `Window.ContentScaleSize`.

## src/Utils/MonitorSetting.cs
The screen the window sits on. `Screens` labels the machine's screens one per index, "Screen 0 (1920x1080)"
off the engine's own zero-based index so the page, the options file and the log line name a screen the same
way, and carries the screen the window stands on as the fallback. `Resolve` layers the saved `monitorIndex`
over that: this is the one display setting whose saved value can name something absent, so an index no
screen answers to is dropped like an unknown word, and the standing screen (the primary on a launch that has
moved nothing) leaves a window where the player is looking. `SavedWord` holds the `--det` guard. `Apply` is
the one place `DisplayServer.WindowSetCurrentScreen` is called and skips a window already there; both of
`Launcher`'s call sites make it before the mode and the size, a mode applied first filling the old screen.

## src/Utils/ScriptedWindow.cs
Win32-only window hiding for scripted runs: `ScriptedWindow.Hide()` calls `ShowWindow(SW_HIDE)` on
the native window handle. Fully static, one call site in `Launcher._Ready` right after the `--det`
block, where the same predicate drives both window hiding and the interactive run's focus request.

## src/Utils/OptionsStore.cs
Process-wide, version-tolerant JSON persistence for `OptionsDef`: the graphics mode and difficulty words, the four
display settings (monitor index, resolution, display mode, V-Sync), the four volume levels, the nearest-after-a-kill targeting switch, the default view a flight opens in and the automatic head turn. One file, `user://options.json`,
independent of `Session/Campaign/CampaignProfileStore.cs`. A missing or malformed file reads as empty, an unknown version invalidates it, an
unknown value drops only that field, and a field the file does not carry reads as never set, which is why adding a field does not bump
`Version`. Four reads hold that one contract: a word set (`DisplayWords`, `DifficultyWords` and `ViewWords` hold the vocabularies, whose resolved tier and view mode belong to `Flight`), a shape predicate for the
monitor index and the canonical `1920x1080` resolution, `AudioMix`'s 0..100 range for a level, which is `int?` so a saved mute stays
distinct from never set, and a JSON-kind check for the switch, `bool?` for the same reason. `Save` writes a sibling temp file and renames it. Under `--run-tests`, `UserOptions()` uses an emptied scratch
directory (`DirectoryOverride`), so no suite touches the player's file; `Launcher.ApplyOptions` is the only writer.

## src/Utils/AudioBuses.cs
The names of the four buses `CSVM/default_bus_layout.tres` ships: `Master`, and `Music`, `Effects`
and `Voice` sending into it. A resource rather than an `AudioServer.AddBus` call at startup, so a
bus exists before the first node enters the tree. Every site that builds an `AudioStreamPlayer` or
`AudioStreamPlayer3D` sets `Bus` from here at construction, because Godot resolves an unknown or
unset bus name to Master with no error and a misplaced player is therefore silent about it. Bus 0
carries the developer `--volume=` gain and the focus mute (`Session/Launch/Launcher.cs`); the three
children carry the player's mix, written by `AudioMix`. The `audio-buses` suite holds both.

## src/Utils/AudioMix.cs
The player's mix: four 0..100 levels (Master, Music, Effects, Voice) into one linear gain per category bus,
`category/100 x master/100`, floored at -80 dB so a level of 0 is silence rather than negative infinity. Master multiplies
the other three instead of being a level of its own, so `Apply` writes only the three child buses and refuses index 0,
which keeps `--volume=0` silencing a scripted run whatever the levels say. `Apply` takes a nullable level per category
and falls back to the shipped default; it is the startup apply (`Session/Launch/Launcher.cs`), the live one, and idempotent.
`SavedLevels(det)` is the levels' one reader and answers four nulls under `--det`, so a mix saved at one machine's
controls never reaches a scripted run. `Capture`/`Restore` take and put back the three child buses' gains verbatim, for
the AUDIO page's preview, which owes back the mix it opened over. The arithmetic is pure and unit-tested.

## src/Utils/MasterVolume.cs
The developer output gain, the whole of what bus 0 carries: `Resolve` takes the command line's `--volume=` over the
`audio.volume` config key over a default that is silence in a repo run and the resting gain in an exported one, and
`VolumeDb` converts it with the same -80 dB floor `AudioMix` uses. The config key is read even where the flag beats it,
so it self-registers for `--dump-config`. Resolution only: `Session/Launch/Launcher.cs` is the one caller that writes the bus,
and is where a launch resolving to the resting gain writes nothing at all, keeping a full-volume launch byte-identical
in output and console log. ⚠ The player's four saved levels are no part of this gain. They multiply on the three child
buses underneath it (`AudioMix`), so the two reach the output as a product and a level saved at the controls cannot
un-silence a scripted run. The `audio-levels-launch` suite drives that whole ladder from a parsed command line.

## src/Utils/PresentationResolution.cs
The requested-versus-active menu presentation resolver: force-Built-in → CLI override → the
Original default, with the caller's availability check applied only after the request
is picked, so a machine without the extracted menu data lands on Built-in with a reason. `Resolve`
never rewrites what `Requested` would answer. ⚠ No saved word takes part: a `menuPresentation` an
older build wrote stays in the options file unread. Presentation names are plain strings; no presentation contract type lives here.

## src/Utils/WorldBackdrop.cs
The background of the process's one `WorldEnvironment`, which is a `ProceduralSkyMaterial` as the
lighting rig builds it and belongs to no menu and no mission. `Black` writes flat black and `Sky`
puts the sky back, leaving the sky material in place either way, and `IsBlack` is what a suite asks
of a frame. `Session/Launch/Launcher.cs` owns every call: black on each menu show and at the quits that
still draw a frame, the sky at each launch, before a world or the cockpit pass's copy of the
environment can read it. A menu frame with no presentation on screen is what this exists for: the
apply's switch runs a frame after the exit that asked for it, and the presentation is already
hidden. Read `Session/Launch/Launcher.cs` next for the three sites.
