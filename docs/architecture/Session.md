# Session

The `Launcher` scene root, the per-launch `GameSession` node, and the low-coupling session-build clusters they delegate to.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Session/GameSession.cs
The per-launch orchestrator: `Launcher` constructs it from `(SessionSpec, LauncherContext)` and `StartSession` runs ordered build phases over one
local `BuildState`. It owns the session clock, world root, panes, seats, mode runtimes and resource lifetimes, delegating aircraft assembly and
membership to `FlightRoster`, world construction to `WorldSession`, and effects staging to `WorldEffectsFactory`. `_rigs` is the pane list every
camera-anchored system reads; `_seatRigs` is the whole field, a network match's guests included, and is what the roster, the spawn walk, the versus
board, the respawn rotation and the human-aircraft step are sized by. A context transport opens a `Net.NetSession` in the constructor, before the
world, so a host can answer a join it has not built for yet; `AwaitNetJoin` pumps that wire until the host's seed, seat and roster have landed,
ahead of `Rng.Reset` and the seat sizing, and the handshake's clock opens the `NetClockSlew` advanced each frame. After the synchronous build it
constructs one `SessionSimulation`, which owns the step order; both step paths step the wire before the step, so an arrival is applied on the step
after it landed, and the human-aircraft phase then puts every seat flown here on the wire on the `AircraftStateCadence`, as the SIM pose, while a
sample for a seat flown elsewhere reaches that seat's own pose buffer. `AllAircraft` combines the roster's AI view with the ordered rig controllers,
and `OrderWaveAirframes` with `StepOwedLoad` puts the coming waves behind the load screen. Exit frees the session subtree atomically and releases
only the non-node resources it owns; the prohibitions that keep these rules true sit on the members they bind. Read `SessionSimulation.cs` next.

## src/Session/SessionSimulation.cs
The session simulation: one plain-C# module owning hold admission and the exact order of flight,
combat, mission, radio, effects and match advancement. `Step(dt)` snapshots eligible AI
membership at entry, then advances incoming fire, projectiles, human aircraft, zeppelins,
emplacements, generators, surface vehicles, captured AI, landing approaches, Instant Action,
campaign, radio, smoke, tags, AI voice and Versus. Authored animation, cutscene presentation,
weather, lens flare, terrain extension and wall-time watches stay outside it, with
`GameSession`. `ISessionSimulationRuntime` is the named recording and production seam rather
than a participant registry. Read `GameSession.cs` for who owns each phase.

## src/Session/Launcher.cs
Main.tscn's root and the process bootstrap: CLI parse into `_cli`/`_spec`, data-root precedence, the
editor check that gives an export its `logs\` and audible volume default, the developer gain on bus 0 with the saved mix under it, at startup and on an Options apply (`Utils/MasterVolume.cs` resolves the first, `Utils/AudioMix.cs` writes the second), the `--dump-*`/`--run-tests` early quits,
and what outlives a session (camera, sun, audio, music, the perf and hitch instruments, and the one `ChapterCinema` and `ClosingCinema` the campaign's doors play through). It owns the
menu as one `MenuHost` built on the first show, the presentation resolution, the only options write (an apply from the in-flight `Flight/PausePreferences.cs` leaf takes that same route, without the presentation reselect a menu-side apply ends on),
the frame pacing and the window's screen, mode and size at startup and on an Options apply (`Utils/VSyncSetting.cs`, `Utils/MonitorSetting.cs`, `Utils/DisplayModeSetting.cs`, `Utils/ResolutionSetting.cs`),
and the sink every menu exit takes ([../menu-presentations.md](../menu-presentations.md)); with no
extraction it shows `UI/NoGameDataScreen.cs`. Each launch hands the session one `LauncherContext`, the settled paths, the persistent nodes, the process services and the match's wire (`NetTransport` with `NetHost` and `NetAirframes` beside it, or `NetSeats` and `NetHandshake` where a caller has them already), never a new argument, which is a `SessionSpec` change. `LaunchSession`, `ReturnToMenu`, `RestartSession` and
`BeginLaunch`/`RunOwedLaunch` are every path a session starts or ends on (the load screen stays up past the build while the session's owed build steps run one a frame through `GameSession.StepOwedLoad`, which is what makes it a yield of several frames; a CLI launch has no screen and drains them inside `LaunchSession`), a flight left early comes back to the screen it was launched from (settled by the launch through `MenuReturnDestination.ForLaunch`, not by the exit press), and what the persistent `WorldEnvironment` draws behind all of it is `Utils/WorldBackdrop.cs`'s: black while the menu owns the screen and at the quits that still draw, the sky again at every launch.

## src/Session/LiveryResolver.cs
Resolves which livery each player flies: the shipped paint catalog and the per-pattern
region-mask library (both lazy and cached), `PatternsForPlane`, and the per-player `SchemeFor`
pick that reads a `SessionSpec`'s `--paint=`/`--paint-color=`/`--paint-decal=` overrides.
Constructed once per session build, so a relaunch resolves against the fresh spec. With no
override every aircraft wears `DefaultPattern`, the only pattern covering all eleven airframes;
an AI spawn prefers an explicit scheme, then its own def's `paint_*`, and reaches that default
only when neither is authored. Instant Action enemies are the exception, for the reason
`InstantActionRuntime.cs` gives. Paint decode: [../org/paint.md](../org/paint.md).

## src/Session/SpawnPicker.cs
Resolves each player's flight spawn: `LoadSpawnList` (which list the session walks, the mission's
`ia.json` scenario or a Dogfight launch's `net.zrd` block), `ChooseSpawnBase` (the shared
`--spawn=`-or-random list index), `ChooseSpawn` (a player's position and look-at from that list,
`objectives.json`'s `PLAYER_INIT`, or the `--spawn-at=` debug override), `StartState` (the field's
throttle and speed) and `LogSpawn`. Constructed once per session build. Also the plain
`IFlightStarts`: `ChooseStarts` loops its own `ChooseSpawn`, the placement every session flies
except a splitscreen race or a co-op campaign mission. `StartGrid` takes its anchor from here, so
this type owns it. Data: [spawns](../formats/spawns.md), [net](../formats/net-spawns.md).

## src/Session/IFlightStarts.cs
Where every pilot in a session starts: `ChooseStarts(spawns, missionZrdrPath, spawnBase,
playerCount)` returns one `FlightStart` per player, the same `(pos, lookAt)` pair
`FlightController.Setup` takes. Two implementations: `SpawnPicker`, the plain per-player walk of
the mission's spawn list, and `StartGrid`. `HumanFlightAdapter` holds the interface and resolves
the field lazily on its first `Assemble`. Answering for the whole field at once is the point of
the seam, and the constraints that keep it so sit on the interface's own members.

## src/Session/StartGrid.cs
The abreast starting grid and second `IFlightStarts`: it fans slots symmetrically across one
anchor heading, then lifts the whole field by its lowest terrain clearance. Anchor selection
stays with `SpawnPicker`, preserving mission `PLAYER_INIT`, Instant Action lists, fallbacks and
`--pos`; the heading comes from the anchor's position-to-look-at pair, so `--direction` survives.
Terrain is an injected `Func<Vector3, float?>`, production using `GameSession.GroundSampler()`,
and `startGrid.slotSpacing`/`startGrid.groundClearance` are registered TUNE values.
`GameSession` selects this grid for multiplayer campaign sessions and eligible splitscreen stunt
races; solo, Dogfight and deterministic scripted race starts keep their own placement paths.

## src/Session/PlaneRoster.cs
Static, spec-free lookups over a `SessionSpec`'s plane roster: `PlaneFor(spec, index)`,
`PlaneDisplayName(stats)`, `Humanize(s)`. A plane's display name is the def's AUTHORED `title`
(`PlaneStats.AiTitle`, "Medusa Kestrel") where something has resolved it through the string table,
and the def-name derivation ("Bloodhawk") otherwise, which is what a player load and a bare rig get.
No session state: every call takes the `SessionSpec` explicitly rather than caching one, since
these are pure over their arguments.

## src/Session/SurfaceDefTable.cs
One of the original's per-surface anim-def vectors (`"player_crash_" + name`, `"ai_crash_" +
name` or `"touchdown_" + name` over every `SurfaceRegistry` slot) plus the cascade that indexes
it with a struck material's numeric surface id (`SceneBuilder.SurfaceIdMeta`). Built once per
bind from a caller-supplied "does this def exist" test, so `PlayableDefs` is what a runtime binds
and `DefForSurfaceId` is what an impact asks. Engine-free and pure; the fallback cascade and its
empty-slot arm carry their prohibition at the type itself. The AI family runs the same cascade
over the same fields with the vehicle's own name as its last resort. Full decode, including the
touchdown family and the weapon `IMPACT` table: `analysis/surface-classification/FINDINGS.md`.

## src/Session/InstantActionDirector.cs
The engine side of one Instant Action mission, behind `GameSession`'s one nullable `_iaDirector`
field: a plain sealed class that builds no node of its own, so every actor it makes parents under
the handed world root. `TryCreate` takes the wizard's def or `--ia=<path>`; `BuildActors` is the
contiguous actor phase (the chapter's first patrol net, the ace, the wingman fan and its escort
chain, every configured wave built inert at the world origin, each actor named on its `AiSpawn.PilotName`: the ace's `ace_name`, a wave's `enemy_name`, a wingman slot's fixed pilot); `Step` ticks the sequencer and activates what it returns; `WireEndConditions` routes each mode's own win signal, the lives
ledger and the whole-window wrap-up board, snapshotting the four counters at the ending and holding the pilots' seats (not the world, not the cameras) until the hold runs out and the board is due: a win keeps the stick and loses the commands, a loss loses both, and a hull lost inside the hold spends no life and takes no pane. The decoded rules stay engine-free in
`InstantActionRuntime.cs` and `InstantActionWaves.cs`; this class owns every `ia:` log line.

## src/Session/SpectateHandoff.cs
The shared pane handoff for an Instant Action pilot out of lives or a campaign human whose aircraft
is lost while teammates continue. `Begin` pins the wreck through `FlightController.Spectating`,
releases the pane camera through `CameraOwned`, and creates a `SpectatorCamera` at the crash view's
last pose. It follows the first other rig still `InPlay`, or starts free when none exists, and uses
the downed pilot's own device filter. A false result means that pane already has a spectator.
Candidate and tracking lists are optional for callers without a roster or rerun path.

## src/Session/InstantActionRuntime.cs
Owns one Instant Action mission's actor set: the loaded `InstantActionDef`, the ace's spawn draw and
rating, the wingmen's fan placement, each wave's per-member draws, the objective-zeppelin selection,
and the mission's end with the decoded `WrapupHoldS` that `Advance` spends before the `WrapupDue`
cue for the board. The static, engine-free helpers `InstantActionDirector` calls are here
(`ChooseAceSpawn`, `RepresentativeRating`, `WingmanSlotFor`/`FlownWingmen`, `RandomPilotStats`/
`ResolveWaveAccentId`, `VoiceAccentIds` (voice prewarm), the zeppelin lookups, `FormatElapsed`/`ShotPercent`).
The end half holds no engine type and calls no `GD.*`, like `VersusMatch`, and the director owns
every log line about it. Format and decode: [../formats/instant-action.md](../formats/instant-action.md).

## src/Session/InstantActionWaves.cs
The decoded wave sequencer's own selection, trigger and geometry logic (`FUN_0045b9d0`): pure
state over `Start`/`Step` calls, in the shape of `GeneratorCycle`, pinned off-engine by
`CSVM.Tests/InstantActionWavesTests.cs`. `InstantActionDirector.BuildActors` builds every
configured wave's members inert at the world origin, then calls `Start()` and activates whatever
it returns; the director's `Step` ticks this once per sim step, and its `ActivateWave` resolves
the spawn draw against every live human's current position before activating each member. Format
and decode: [../formats/instant-action.md](../formats/instant-action.md).

## src/Session/ObjectiveScript.cs
One mission's `objectives.zrd`, parsed into the typed shape `ObjectiveGraph` runs. `Load` takes a
mission zrdr scope and yields an empty script for a mission with no file, which is what every
Instant Action and multiplayer stub amounts to. Two parser rules are the original's and are what
keep the shipped data honest: blocks are read `OBJECTIVE1`, `OBJECTIVE2` and stop at the first
missing number, and every directive is found by exact name over the block's flat alternating
list, so a truncated or misspelled key lands in no field and stays dead. The four target
directives and `SET_HELP_LABEL` read `ObjectiveTarget`s, where a nested list is ONE path keyed
`parent/child`. Format and decode: [../formats/objectives.md](../formats/objectives.md).

## src/Session/ObjectiveSites.cs
The flown mission's flagged target sites, offered to each player's `TargetPool` carrying the flag
their own record authors: `CollectFlagged` runs once per `TargetFlag`, over `targets.zrd`'s
`objective` half (the Enemy cycle) and then its `other_target` half (the Non-Aircraft cycle), the
curated list admitting a mission's chosen structures and no other destructible. A campaign director's script edits both with
`ADD_`/`REMOVE_`; the director-free constructor is what Instant Action and the multiplayer modes
take, their table unedited. World SITES only, keyed by `ObjectiveTarget.Key`, re-read every frame so
a site tracks a moving node and reads `Live` off its `DestructibleRegistry` state; a roster block
that flags itself rides its own aeroplane. Bound by `GameSession`; [../org/targeting.md](../org/targeting.md).

## src/Session/CampaignHumanField.cs
Engine-free objective rules over every joined human, represented by `HumanState` position, captured
group, and wreck state. `CampaignDirector.World.SnapshotHumans` is the sole producer. `Travelers`
uses the nearest human, so approaching succeeds on the first arrival and departing on the last
exit; wrecks continue reporting their positions. `LiveInGroup` counts non-wrecked humans in a
captured group and ignores temporary cutscene inertia. The scripted player remains a separate P1
identity for authored `player` tokens, roster leaders, and anchored net trailers. Rules are pinned by
`CSVM.Tests/CampaignHumanFieldTests.cs`.

## src/Session/CampaignDangerZones.cs
A campaign mission's own danger zones: the `dzpathN` names its `objectives.zrd` authors inside
`DANGER_ZONES_COMPLETED`, narrowed by the mission's `dzones.zrd` `disable` list and resolved
against the world's gate geometry, with `Load` answering null when a mission arms none. The same
file's `objective_numbers` and `nosnapshot` ride on each armed zone and `TryZone` answers them: that
number is the zone's completed-objective bit and names the scrapbook photograph it writes. A zone is
a route ribbon plus a material-matched pair of gate polygons, crossed by a segment passing inside
each in either order, the rule `Flight/StuntMission.cs` reads off `ia.json`'s zone list; the two differ by authoring surface, not by mechanism. Crossings are tracked per human, each with its own previous position.
Gate decode: [../formats/missions.md](../formats/missions.md).

## src/Session/CampaignSnapshot.cs
The campaign Danger Zone photograph through `Flight/DangerZonePhotograph.cs`, scaled to the 640x480
every retail photograph is, written into the flying profile's directory under the
`Snap_<mission>_<objective>` name a capture row resolves against. `Stage` requests one zone's still on the crossing frame, and a worker
writes it under a `.PN_` pending name when the frame lands. `Commit` sweeps ids 10 to 31 at mission
end, keeping them under their scrapbook names on a win and deleting them on a loss (the original's
two-step); a still landing after the sweep takes the verdict left for it. `Flight/StuntCapture.cs`
is the other camera: an Instant Action run has no mission slot or objective to be named by. Rows and
gate: [../formats/campaign-screens.md](../formats/campaign-screens.md).

## src/Session/ObjectiveGraph.cs
The objectives runtime over a parsed script, pure state over `Step` calls in the shape of
`InstantActionWaves`: no Godot type, no logging, pinned off-engine by
`CSVM.Tests/ObjectiveGraphTests.cs`, with `CampaignDirector` owning every log line about it.
Implemented as decoded, shipped quirks included: the four states per objective, at most one
completion per tick from a rotating scan, dependency gating on an AWAKE target, the wake
executor's truncating early return, the nap that clears a completed flag, and the condition
families' OR, whose `DEDG` arm also widens the watched group's engagement volume on every tick it is tested. Four endings reach it, three from the script and `NotifyDockingComplete` from the
animation. World seam: `IObjectiveWorld`. Decode: [../formats/objectives.md](../formats/objectives.md).

## src/Session/CampaignDirector.cs
The engine side of one campaign mission and the sibling of `InstantActionDirector`: a plain sealed
class building no node of its own. `ResolveSpec` runs in `GameSession`'s constructor and turns a
`--campaign=<profile>:<seq>` position into a chapter and mission; `BuildRoster` plans and spawns
the `aiv` blocks through `CampaignRoster.cs`; `Attach` arms the graph once every runtime a
directive can touch is up; `BindCallbackHost` takes the `CALLBACK` slot ahead of the generator
runtime's, where 801 to 803 reactivate the lowest-numbered still-deactivated Black Hat of their
family, CM19's only launch path, and 968 takes C4/M03's escorting wingman out of the world as that mission's docking film says her name; `Step` runs the graph, the escort repair, the music and the danger-zone tracker, whose completed zones both photograph into the profile through `CampaignSnapshot` and make `DangerZoneMask`, the id 18 to 30 half of the completed-objective mask. The
nested `World` is the `IObjectiveWorld`, a directive with no seam here a named no-op, and `WidenGroupEngagement` is where an awake `DEDG` reaches its group's live members; `Memento` is the picture the flying profile hangs, which the pause sheet's own slot takes; mission end records the attempt, folds the persist log into the profile and holds before the cabin behind `LeavingFade`, the ramp `UI.MissionEndFade` paints. Debrief: [../org/debrief.md](../org/debrief.md).

## src/Session/CampaignProgression.cs
The campaign's progression rules over a profile: recording one mission attempt with the original's
best-of merge, raising the position, and granting the aircraft awards. The position is a single
monotonic integer that only a completed primary objective raises, because `cm_sequence.zrd` is a
flat list with no predicate and no alternates. `Record` also works out what a mission pays, since
the reward table is gated on what the profile has already banked, and reports it back through
`MissionRecorded`. The four-attempt skip offer is raised here and answered by `AcceptSkip` on the
screen that shows the result. `AirframeCount` fixes the kill tallies' width. Reward table:
[../org/hangar.md](../org/hangar.md).

## src/Session/CampaignMementos.cs
The pictures a campaign profile may hang on its cabin wall: the executable's own award table (name,
the mission that awards it and which bit of that mission's merged objective mask admits it) and the
rule that reads a profile's records to say which rows it holds. Seven rows carry no mission and are
held from the first frame, so a chooser is never empty; `Current` answers what an absent or unknown
stored name draws as, and `Bitmap` is the truncation the drawn file name takes. `BitmapFor` is the
one name the cabin wall, the pause sheet and the campaign load screen all draw, so a chosen picture
cannot reach one and miss another. The table, its addresses and the row it can never admit:
[../org/pause-screen.md](../org/pause-screen.md).

## src/Session/CampaignProfileStore.cs
JSON persistence for one named campaign profile under `user://Profiles/<name>/profile.json`,
following `ScoreStore` and `CustomPlaneStore`'s precedent: funds, owned planes with their per-gun
ammunition and per-pylon ordnance picks, each mission's record in the original's two halves
(latest attempt and best-of merge) with its failed-attempt counter, the completed-mission count,
the granted aircraft awards, the cabin's chosen memento and the cross-mission destruction log. An owned plane names a build
in the global `user://Planes/` store rather than copying it, so deleting a profile orphans
nothing. A file saved before a field existed reads it at rest rather than failing to load. Save
format: [../formats/saved-games.md](../formats/saved-games.md).

## src/Session/ChapterCinema.cs
Which film plays before a campaign chapter, and the one handoff to the passenger cabin that
follows it. The chapter is `seq / 5 + 1` over the profile's own position, so no screen passes a
chapter number in, and chapter N plays `chapN.mpg`. `CampaignCabinPage.MapPinCount` reads the same
story chapter for the cabin map's pins; `CampaignSequence.Chapter` is a different number, the world
folder. Playing is a `UI/CinemaHandoff.cs` `CinemaPlay` the caller supplies, `Launcher.PlayCinema`
being what it is handed, which leaves the film to `UI/CinemaScreen.cs` and keeps every decision here
testable with no engine present; the cabin opens through that file's `Once`. `Launcher` holds the process's one instance and hands it to `Menu/CampaignFeature.cs`, which is how both presentations' cabin doors reach it (`UI/CampaignFlow.cs`, `UI/Menu/Original/OriginalCampaignScreen.cs`). Films: [../formats/cinemas.md](../formats/cinemas.md).

## src/Session/ClosingCinema.cs
Whether the campaign's closing film plays before the scrapbook a flown mission opens, and the one
handoff to that book. The gate is the mission just flown, its result and its story position: a win
on the campaign's last mission plays the film, first flight and replay alike, and any other ending
reaches the book with no film, which is what the original's own script does when its gate callback
answers false. Nothing is latched and completion state decides nothing, so the flown result travels
with the menu return (`Menu/MenuReturnDestination.cs`). The film is the `FinalCinema` layout row's
name and the skip set is Escape and the left mouse alone, narrower than `ChapterCinema.cs`'s on
purpose; playing is a `UI/CinemaHandoff.cs` `CinemaPlay` (`Launcher.PlayCinema`) whose `Once` opens the book, and `Launcher` holds the one instance and hands it to `Menu/CampaignFeature.cs`. Films: [../formats/cinemas.md](../formats/cinemas.md).

## src/Session/CampaignPersistLog.cs
The cross-mission state log: what a campaign mission left destroyed, carried into later missions
of the SAME chapter, keyed by chapter, by the capturing mission's story position and by gamez node
index. `Capture` reads the live world through the pool the hit path damages; `Through`/`ApplyTo`
take the position of the most recent earlier mission of the chapter and carry only what positions
at or before it captured; `AnimRuntime.CarryState` re-applies the states silently, so a later hit
on a carried kill is a no-op. Only `PERSIST_LOG` defs are carried; the readers are where that flag
is authored, and `AnimProgram` hands it to whichever def its dedup keeps. The prohibitions on the cut and on the replay path sit on the members they
bind. Decode: [../formats/saved-games.md](../formats/saved-games.md).

## src/Session/CampaignLoadout.cs
The bridge between a campaign profile's stored picks and a flying aircraft's fit: one `OwnedPlane`'s
ammunition and ordnance arrays as the `LoadoutChoice` a launch hands the session, which
`Loadout.Bind` lays over the aircraft's base fit. Engine-free, and both encodings are the campaign
screens' own rather than the original's undecoded per-pylon ordnance id, so an unset pylon is left to
the base. `PylonRow` is the one decoder of the stored one-based value; a cell travels as a cell (a
wing and an ordinal), never a pylon number, since which pylon a wing's second cell is depends on what
the aircraft hangs. The same reading serves an exported `CustomPlaneDef`, which is how a campaign
plane flown from Instant Action carries its fit. [../formats/campaign-screens.md](../formats/campaign-screens.md).

## src/Session/AirframeSwap.cs
The three `CALLBACK` codes that hand the player a different airframe in mid mission, and what each
names: the vehicle def the stats and the stock fit come from, the planes.zbd node the model is
built from, an optional special-plane award template, and whether the rebuild draws the airframe's
shipped skins instead of the pilot's paint. `AirframeSwapOrder` is one raised swap (the airframe,
the raising definition's root, and the episode owner whose rig it lands on) and
`AirframeSwapResult` reports whether an aircraft was replaced and which one left the world. A swap
is always to the player's OWN rig, and what it deliberately does not rebuild is stated at the
request record. Callback table: [../formats/anim-definitions/cutscenes.md](../formats/anim-definitions/cutscenes.md).

## src/Session/CampaignRoster.cs
The engine-free half of the campaign roster spawner: `CampaignRosterPlan.Build` turns
`AiSkills.LoadRoster`'s blocks into one `RosterSpawnPlan` each, resolving the def behind a block
name, its `mode`, its player airframe and the chapter's `AiNet` for an authored `netids`. The
decoded fork lives here and nowhere else: `Escorts` is `mode wingman` with no authored net, `Net`
is any authored net the chapter carries, and a plan never has both. `ApplyPlan` is the
after-the-spawn half (volumes, maneuvers, gunner ratings), shared by the campaign placement and
the generator launch so the two cannot drift; `BuildGeneratorTemplate`/`ResolveGeneratorLaunch`
serve the enemy generators, and `WidenForDedg` is the one write an awake `DEDG` makes to a member's activation radius. Nets and modes: [../org/aiPilot.md](../org/aiPilot.md).

## src/Session/ScriptedPathVehicles.cs
One campaign mission's scripted-path vehicles. `Place` binds a spawned body to its authored
`ScriptedPath`, snapping it onto waypoint 0 facing down the first leg and freezing it there;
`Release` is what `START_TAXI` calls, and `Step` drives each follower and writes its pose onto the
body, or hands it to the caller's `setPose` where a simulation of its own owns that pose. A
finished vehicle raises its handoff callback with the speed the path left it at and leaves the
registry. The following law is `Flight/PathFollower.cs`, the route `Mech3/ScriptedPath.cs`.

## src/Session/SurfaceVehicleRuntime.cs
Builds and steps a mission's surface vehicles, the `mode ship` blocks (`patrolboat`, `t_truck`)
with no player airframe: each is a copy of the chapter's library-root model under the world root
at its authored spot, its height read off the water, indexed on the world runtime so the chapter's
definitions anchor on it and register its destructible pool. `GameSession` builds one lazily for
the roster phase and the generator block; `SessionSimulation` steps it after the generators that
may launch another hull. `CollectVehicles` offers every hull to the aim assist's vehicle list
([../org/aim-assist.md](../org/aim-assist.md)); `Projectiles`/`Weapons`/`Voices` arm and voice its gun on the attack radius `AttackRadiusOf` resolves in the engine's own write order, the block's and net's slot over the def's `attack` over the decoded 400 m default and never a zero reach ([../org/aiPilot.md](../org/aiPilot.md)); `Strings` names it, slot 20 into `MarkerName`.
Read `SurfaceVehicle.cs` next.

## src/Session/SurfaceVehicle.cs
One built hull: no pilot, no flight model, no `FlightController`. Its movement is the scripted-path
follower's law (`Flight/PathFollower.cs`, [../org/flightModel.md](../org/flightModel.md)) over an
unbounded route, the generator's take-off run then a lazy walk of the patrol net's edges, height
pinned to the water. `Patrol` is the roster and `SET_AI_NET` assignment, `Launch` the generator's,
`Wake` the `WAKEUP_ENEMIES` arm a block's `deactivated` waits on. Damage is the pool the chapter's
definition registered on the root: a rung of the injure ladder plays as the pool falls through its
fraction, the death leaving the parts to the death sequence. Its gun is `SurfaceGunner.cs`, stepped
from here for a woken, undestroyed hull; `SurfaceVehicleRuntime.cs` is how one is built.

## src/Session/SurfaceGunner.cs
One hull's gun ([../org/aiPilot.md](../org/aiPilot.md) "What a `mode ship` vehicle runs"): the
acquisition, mount and fire decision a patrol boat runs, built from the def's own `weapons` tuple
and the model's `turret` > `gun` > `firepoint` chain, or not built when any input is missing. It
sweeps the pool's three candidate lists under the team gate, ranks with the non-`jet` scorer and the
defs' class biases, holds a target for a hardcoded 20 s, aims through `Flight/SurfaceGunMount` and
fires on the authored window, interval and magazine, dropping non-aircraft candidates so a boat does
not shoot a boat, which is why the aircraft-first preference is not spent here. No pursue gate and no
quick draw, neither reaching a hull. `AttackRadius` is the hull's own ATTACK volume, the one both decoded scorers admit on, never its activation ([../org/aiPilot.md](../org/aiPilot.md)). Its `GunVoice` is renewed per tick from the hull origin ([../org/weaponFire.md](../org/weaponFire.md)). Suites: `surface-vehicle-guns`, `surface-gun-voices`.

## src/Session/CutsceneController.cs
The host a story mission's intro or landings definition raises its `CALLBACK` codes to, and the
session state those codes describe: the world and objectives held, the chrome off and the view off
the aircraft, the humans out of flight with the episode owner posed on the staged `player` marker
and the runtime's range gates reading where they last flew rather than where the film puts them,
the AI parked (at the start of a mission of any type, and again before any intro plays whether or not its own data authors 913, lifted by code 914 from whichever definition the mission bootstraps), the mid-mission airframe swap, the re-placement, the clearing of every round still in flight, and one restore at the definition's end or at a skip, refused once the mission has ended under the episode so the leaving fade keeps the film's shot. It also owns the held-input fast-forward (`Mech3/Anim/CutsceneFastForward.cs`), scoped to the episode's call closure and offered only where no skip is armed. A `Node`
only so it can tick last in the frame, after the animation advance that poses `camera1`. Which
definition and which human an episode belongs to is the slot `Own` claims, not the raiser of the first code. Decode: [../formats/anim-definitions/cutscenes.md](../formats/anim-definitions/cutscenes.md).

## src/Session/LandingApproachRuntime.cs
The mid-mission cutscene trigger: a story mission's resolved `LandingApproaches` are tested each
frame against every flying human (arming gate, speed band, attitude cone, condition volume), and
the first passing row is started as an explicit mission trigger with `CutsceneController.Own` given
both the row and the human who flew it. A row fires once per entry into its volume; an `auto` row
lights the auto-land prompt in each passing human's pane instead of starting anything. Rows whose
approach nodes are staged later are bound when those nodes appear. Read `CutsceneController.cs` for
what a started row then does, and
[../formats/anim-definitions/cutscenes.md](../formats/anim-definitions/cutscenes.md) for the table.

## src/Session/LadderSwitch.cs
The original's rope-ladder switch as an engine-free rule and state machine: the ladder is wanted
when the aircraft is within 45 degrees of upright and inside an active `pickups.zrd` sensor, and
the switch starts `drop_ladder` or `retract_ladder` to match, one transition at a time, holding a
transient state until the running definition's own callback settles it. `Holder` is the co-op half,
one incumbent human keeping the switch until they stop qualifying. Decode:
[../org/ladderSwitch.md](../org/ladderSwitch.md); the flown half is `LadderSwitchRuntime.cs`.

## src/Session/LadderSwitchRuntime.cs
`LadderSwitch` flown against the built world: outside a cutscene it reads each flying human's
attitude and position against the mission's pickup sensors, resolves the one holder, and starts the
ladder definitions as mission triggers, so the drop's `OBJECT_ADD_CHILD` can materialize the
library rope ladder. It takes the runtime's `CALLBACK` host slot and chains to the cutscene host
behind it, which is where the original registers the switch on each definition. Bound with the
landings trigger, story missions only. Read `LadderSwitch.cs` for the rule it flies.

## src/Session/GeneratorCycle.cs
The decoded egen launch timing law for ONE generator, pure over `Step` calls (no clock, no
randomness, no nodes) so a unit test pins it off-engine: the timer always advances, blocking holds
rather than cancels, and the individual and wave periods compose. `DoorOpen` runs the decoded
hangar-door timings and holds a spawn behind a door that has to travel; `HostDied` starts the wreck grace
the bay keeps launching through, after which the cycle disables permanently. `GrantCapacity` is
the one way launches arrive. Format and decode:
[../formats/mission-entities/enemy-generators.md](../formats/mission-entities/enemy-generators.md).
Read `AiGeneratorRuntime.cs` for what drives it.

## src/Session/NetTrailerTargets.cs
Resolves a patrol net's TRAILER name to a live position supplier, the session half of "an anchored
net rides its target", so `Flight/AiNetFollower` can do the arithmetic knowing nothing about
players or world nodes. `For(net)` answers only the anchored-and-named shape; the other shipped
shapes get null, which means "fly the authored coordinates". `player` is the scripted player's rig
and anything else a world node through the same lookup `ZeppelinRuntime` uses, while `OffsetOf` is
the overlay's read of the same offset. Every follower the session builds shares one instance.
Census of the shipped shapes: [../formats/ai-nets.md](../formats/ai-nets.md).

## src/Session/AiGeneratorRuntime.cs
Runs a mission's egen generators behind `--generators`: one `GeneratorCycle` per surviving
`EnemyGeneratorDef`, host altitude read live off the resolved host node, and spawns through the
handed roster callback at the origin node's live position. An air host drops its launch; a surface
host flies its own take-off path held and hands the aircraft to the flight model at the decoded
release point. Door transitions play the authored open and close anims scoped to the host,
`GrantWaveCapacity` is the one credit, and `NotifyHostDied` puts the matching cycles on the wreck
grace. Every drop, launch and door prints an `egen:` line. Decode:
[../formats/mission-entities/enemy-generators.md](../formats/mission-entities/enemy-generators.md).

## src/Session/ZeppelinRuntime.cs
Runs a mission's zeppelins behind `--zeppelins`: a `ZeppelinDef` whose world node and net resolve has
its hull switched on, is placed at its authored pose and flown by `ZeppelinMotion` over
`AiNetFollower`; an animation-driven hull is neither placed nor stepped. `WireDamage` builds the
per-part pools, `PollDamage` owns the kill (a healthy entry is dead when its pool is destroyed or
its own node is switched off), the Instant Action engine count and the generator disable; `CollectTargetParts` alone makes a structure selectable, and only under `TargetPool`'s
torpedo gate. A def whose net does not resolve is held out, zones unwired; `--zep=` grafts one on a
synthetic net. Script arms: `SetStopPoint`, `Hold`, `Wake`, `SetNet` (nearest-node seat from where
the hull stands) and `SetTeam` (one side over every pool and gun). Decode: [../formats/mission-entities.md](../formats/mission-entities.md).

## src/Session/ZeppelinRuntime.Cannons.cs
The broadside half of `ZeppelinRuntime`, the second file of that partial class. `WireCannons`
resolves the hardcoded `wep_28` round and each cannon's node and damage pool; per step the runtime
resolves the record's authored `targets`, gates on the authored fire range and the decoded arc,
plays the deploy and retract anims scoped to the hull, and fires real unowned rounds scattered by
the record's inaccuracy. The broadside stays off until a script arms it through the
`COMPLETED_ZEPCANNONS` seam, and a destroyed cannon thins the volley. Decode:
[../formats/mission-entities.md](../formats/mission-entities.md), "Broadside firing".

## src/Session/TurretEmplacementRuntime.cs
The world AA emplacements: the standalone `ai.zrd` turret family resolved against the built chapter
world, registered with the shared projectile pool so every player's aim assist sees them, and
stepped by `SessionSimulation` after the zeppelin runtime so a slung mount reads its ride's moved
pose. Built unconditionally with a chapter flight, as the original's own placement pass is.
`SetActivatedUnder` is the Instant Action builder's subtree write, `SetTeamUnder` the same walk for
the team a zeppelin record fans across its airship, and `WakeAll` the `--wake-turrets` stand-in.
Format and decode, including the wake ordering and the awake-by-data census:
[../formats/turrets.md](../formats/turrets.md).

## src/Session/AiVoiceRuntime.cs
Wires the combat-voice dispatcher into a running flight session, built with the rigs wherever the world has a `WorldSounds`
and ticked on the sim clock: an accented AI spawn is registered as a speaker on its own `FlightController.Team`, each human
rig as a damage source whose rounds draw the ally distress out of a teammate they strike, and the mode machine and death report of EVERY aircraft handed over are watched, accented or not,
because the bearing call-out, the taunt, the killer's gloat and the flight's enemy-down call on a player kill are spoken by an aircraft other than the one the event reached.
An evade episode's end speaks the successful-shake taunt, and only once the machine's own evade flag has cleared.
`Step` raises the rest of the pursue path for every AI whose gunner holds a human, at the slot cooldown's own interval rather than on a mode edge, so a commit inside the mute window is not lost: the taunt the pursuer's own nose against that human picks, its `WA-Attack`, and the flight's bearing call-out.
`RegisterAi` also mirrors `InPlay` into the speaker's liveness, the only place the engine-free dispatcher and a controller meet.
Lines play flat through `MissionRadio.Speak` (the queue the objective callouts share) with the speaker id that answers the "already talking" hook; every roll and first "no clip" refusal prints an `ai voice:` line. `WatchTurrets` adds the one non-aircraft source, a gunner's acquisition of a human player off the shared `ProjectilePool`, broadcast on that player's team. [../formats/combat-voice.md](../formats/combat-voice.md).

## src/Session/FlightRoster.cs
The session-owned aircraft aggregate. `BuildPlayers` commits the whole human field in ascending player order and `SpawnAi` commits one
later mission, wave or generator aircraft; both publish only finished controllers, preserve the shared livery and spawn streams, and roll
back new nodes and registrations on failure. It owns the live human and AI membership views, the target-source fan-out, and
`VehicleDowned`, the AI death report the HUD kill line is fed from. `SwapPlayerAirframe` is the third commit path, a mission putting one
player into a different airframe mid-flight, and `RunSwap` the whole order a cutscene code raises. The loading screen builds the coming
waves' aeroplanes through `OrderWaveAirframes` and `BuildOrderedAirframe`, and `PumpDeferredCrashRigs` takes one more off the owed list
per quiet frame, never on a frame a crash rig or a launch already builds on. `HumanFlightAdapter.cs`, `AiFlightAssembler.cs` and
`AiAirframePool.cs` are the private assembly paths; the swap decode is [../formats/anim-definitions/cutscenes.md](../formats/anim-definitions/cutscenes.md).

## src/Session/FlightRosterInputs.cs
The grouped construction facts `FlightRoster` accepts: the copied flight policy, the immutable
aircraft and archive resources, the live world services, and the human-session bindings. These
contracts keep the roster from taking all of `SessionSpec` or exposing either assembler, while
leaving its required dependencies explicit at the production seam. `HumanRosterBindings.RigCount`
counts SEATS, guests on other machines included, and `NetSeats` is that roster indexed by seat,
empty outside a network match. Read `FlightRoster.cs` next.

## src/Session/CrashRigQueue.cs
The session's queue of crash rigs whose aeroplane is already flying. A mid-flight AI introduction is
the one aircraft build that lands on a frame the player is watching, and the crash rig is the only
block of it the aeroplane does not need in order to be in the world, so `AiFlightAssembler` opens
the rig and hands it here instead of building it. `Pump`, once a frame from the session, advances
the head build by one step, while `Defer` arms the aeroplane itself so any reader of the rig builds
it in place first. `Drop` is the rollback path and `Discard` the membership clear's. Read
`WorldEffectsFactory.cs` for the build being stepped.

## src/Session/AiAirframePool.cs
The wave aeroplanes a mission is going to need, built before it starts and held outside the tree, so
a launch costs the bind, the loadout and the tree insert instead of the painted model, its collision
hulls and its animators. One slot per airframe and livery, `Order`ed off the roster blocks the waves
launch from, `BuildOne` per loading-screen step and per quiet frame in play, `Claim` at the launch,
`Discard` at teardown. A claim on an empty ordered slot counts a miss and the caller builds in place,
which is what every spawn did before the pool; `Claims`/`Misses`/`Owed`/`Ready` are what the hitch
suite reads. It holds no spawn index, the jitter draws its own at the launch, so nothing here moves
the spawn streams. Read `AiFlightAssembler.cs` for the build it calls.

## src/Session/AiFlightAssembler.cs
`FlightRoster`'s private AI assembly path: authored or fallback pilot skills and maneuvers, then the airframe, controller, livery,
loadout and ordnance, damage visuals, the positional engine and weapon voices that stand in for the own-ship `FlightAudio`, the
optional crash runtime, then the node placed. The airframe (painted model, hulls, prop, wing-light and surface animators) is CLAIMED
from `AiAirframePool.cs` where one is ready and built in place otherwise, over one shared `PlaneCollider` per airframe and one shared
`PlanePainter` per airframe and livery (PERF-22). That runtime is OPENED rather than built wherever the caller supplied a queue, so the
launch frame carries no rig and the prop choreography plays from the queue's completion hook. It chains the durability override ahead of
the enemy scale and the spawn jitter, the engine's own order ([../org/vehicleDamage.md](../org/vehicleDamage.md)), resolves the readout's
title, stamps the block's objective marker, and owns the AI skills cache. Read `FlightRoster.cs` next.

## src/Session/HumanFlightAdapter.cs
`FlightRoster`'s private human-aircraft path: `Assemble` builds the painted model, `FlightController`, loadout and ordnance, carried turrets, HUD and
instruments, damage visuals, audio, stunt and match bindings, target selection, the start placement, the Danger Zone eye, the crash runtime, and last
the `UI.SplitScreen.OwnAirframeLayer` stamp that keeps the model out of this pilot's spyglass disc. A seat the bindings' `NetSeats` marks remote is
flown elsewhere: it takes the aeroplane, paint, loadout, spawn slot and score row, is built with the `RemotePoseBuffer` that IS its ownership, and
skips every pane, HUD, camera, listener, pad and pause key, the roster's airframe pick beating this machine's launch flags. It reads only the roster's
copied policy plus the grouped aircraft, world and human-session contracts; player order decides the paint and spawn draws. An airframe swap lays its
captured scheme and own build over that assembly, the one path a bought plane takes. An Instant Action racer takes no `Race`, so it flies on through
the ending's hold, and `BuildDamageVisuals` opens AI damage too. Read `FlightRoster.cs` next.

## src/Session/EffectCatalogue.cs
The record of which authored anims are playable effects and what their defs need staged: the name
tables every effect producer must stay inside, static and engine-free. It owns the impact and death
effect names, the crash rig's own def tables and the two surface-indexed def vectors, the
damage-stage and prop-choreography menus, the canopy-hole family a human rig alone binds, the
destroy-def lookup, the collider overlay's surface colour key, and the anchor-root derivation that IS
`WorldEffectsFactory`'s stage source. An unstageable anchor fails the build rather than leaving a def
anchored on nothing. Every name producer carries a producer-range tripwire in `CSVM.Tests` asserting
its whole range resolves inside these tables. Read `WorldEffectsFactory.cs` next.

## src/Session/WorldEffectsFactory.cs
Builds the two effect stages a session needs and the runtimes bound to them: the world-effects
runtime for impacts and destruction, and the per-plane crash runtime, which despite its name binds
every def that plays ON one aircraft (the crash-def vector, the destroy def, the panel damage shims,
the prop choreography, and on a human rig the canopy holes, whose `PLAYER_1ST_PERSON` branch this rig's own pilot answers). Both stages are built in pool slots sized from `data/effect_pools.json`
and handed to their runtime sealed, and both pre-warm their emitters after the bind so a first burst
finds its puffers already made; `EnsureWorldEffects` hands the effects runtime the world's `WorldLights` as a contributor, so a burst's authored `LIGHT_STATE` renders on both presentations. `BeginFlightCrashRuntime` opens the crash build as a handle a caller
steps a phase at a time (`CrashRigQueue.cs`), where the pre-warm itself repeats a slice at a time so a rig's two hundred emitters never
land on one frame, and `BuildFlightCrashRuntime` is the one-call form. The names it binds are `EffectCatalogue.cs`; the slot mechanism is `Mech3/TemplateStage.cs`.

## src/Session/WeatherRig.cs
Applies the flown mission's weather, driving each rig's skydome, whiteout, deck regime and zone gate every frame; the whiteout is one pane-filling overlay per rig, carrying the cloud band and the fog-volume curtain on `HudLayers.Whiteout`, under that pane's own cockpit pass, so the window whites out and the interior stays clear.
`ApplyZone` writes the zone's authored fog and its `SUNLIGHT` pair through one arm per
graphics mode, mirrored onto every registered extra (sun, env) pair so a cockpit overlay crosses
zones too; both arms put the ambient half through `WriteColorAmbient`, colour-sourced and never a sky
contribution, so a night zone's scene fill is darker than a day zone's. `ApplyFogState` is the
animation runtime's `FOG_STATE` sink, writing only the fields the event carries under the same
last-writer order. Enhanced mode also caps a night zone, paints the sky the zone's own fog colour and
pushes the fog range out, which the sun's shadow distance follows. `SunlightRgb` publishes the applied pair scaled by its authored colours, for the reader that needs the light rather than an energy. Both arms also set `csky_sun_dir` and `csky_sun_light`, the same bearing and pair uncollapsed, for the lit cloud cards, which shade per vertex off normals that turn with the camera, and `csky_sun_ambient_rgb`/`csky_sun_diffuse_rgb` (`SunVertexLight`, the bicolored rule) for the in-flight aircraft's per-vertex term ([../org/vertexLighting.md](../org/vertexLighting.md)), plus `csky_sun_fill_rgb` (`PhotographFill`), the Danger Zone photograph's raised ambient half. Decode: [../org/weather.md](../org/weather.md), authored side [../formats/weather.md](../formats/weather.md).

## src/Session/LensFlareRig.cs
The sun's lens flare: four screen-space sprites strung along the sun-to-screen-centre vector, plus a
full-screen white wash whose opacity is about linear in the sun's screen distance from centre. It
mirrors `WeatherRig`, constructed once per session beside it, `Build` once and `Tick` from the same
per-rig block, one instance per pane. The per-pane state lives here rather than on `PlayerRig`
because an instance is several nodes plus fade state. `--no-flare` suppresses the effect so a
capture stays usable for unrelated comparisons. The spec is measured from capture footage; method
and calibration: `git show analysis-archive:analysis/bl-165-lens-flare/FINDINGS.md`.

## src/Session/ExtractionStamp.cs
Reads the provenance stamp the extraction scripts leave at `extracted/VERSION.json` (unzbd version
line, exe hash, fork commit, schema integer) and compares its schema against this class's own
`Schema` const, in `Launcher._Ready` right after the base paths settle. At most one warning line per
boot, each naming the fix, which is to re-run the extraction. Warn rather than block, because a dev
tree holds valid extractions older than the stamp. `Behind` is the same read for a caller that
blocks instead of warning: true only when the stamp is present, carries a schema and is under what
the caller asked for, so an unstamped or unreadable tree still runs. `Schema` also lives in both
extraction scripts, and `CSVM.Tests/ExtractionStampTests.cs` refuses a bump that moves fewer than all three.

## src/Session/MenuAudioService.cs
The host's `IMenuAudio` over the process's playback. `BeginNarration` ducks the music and restarts the narration player on
the resolved stream; `EndNarration` lifts the duck and stops it, idempotent because the launchscreen calls it every frame
no briefing shows. `Cue` resolves a semantic name through `MenuCueTable` to a file under the constructor's cue directory,
decodes it once and replays it from the start; an unknown name, a missing file or a failed decode is logged once and
cached as silence. `PreviewMix` applies a mix page's levels as they move and sounds the moved category over the original's
`sfx_loop.wav`/`voice_loop.wav`, leaving a running clip alone so a drag is one clip and not one per frame; `EndMixPreview`
puts back the captured gains, and previews are off unless the constructor says otherwise. Narration is begun and ended by
whichever presentation shows a briefing, so this service knows nothing of which screen is up.

## src/Session/MenuCueTable.cs
Which wav under the extracted sound directory a semantic menu cue name resolves to: the rollover,
the click and the two edit-box keystroke sounds, the four files the original's globals script binds.
The indirection from a name to a file is the remake's, since the original's scripts name raw wavs,
and it lives here so `MenuAudioService` owns lookup alone. Engine-free, so the table is unit-tested
against the names the presentations ask for. Read `MenuAudioService.cs` next.
