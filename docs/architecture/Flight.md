# Flight

The plane as a flying, shooting, damageable thing, plus its HUD and stunt mode. Reads plane stats from the extracted zrdr; owns the arcade physics and everything drawn over the pilot's view. Eight sub-namespaces, one folder each, and one page for all of them: `Flight.Airframe` (the flying node, its physics, collision and damage), `Flight.Weapons` (fire control, the projectile pool, targeting, turrets), `Flight.Ai` (the AI pilot and the surface hulls), `Flight.Camera`, `Flight.Hud`, `Flight.Modes` (stunt, Dogfight and the pause state), `Flight.Hangar` (the custom plane and its economy) and `Flight.Audio`. `Airframe`, `Weapons` and `Ai` name each other, since the controller owns the pilot and the pool that both drive it back; `Camera` names only `Airframe`, and nothing else in `Flight` names `Hangar`. The module index in `docs/architecture.md` groups the entries by sub-namespace.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Flight/Weapons/WeaponDefs.cs
Typed reader over the shared `weapons.zrd.json` `BALLISTICS` block: 48 `WeaponDef`s (guns, rockets,
ordnance) keyed by `wep_*`, plus the `NO_AMMO_WARNING` empty-clip sound. It owns ballistics, damage,
allotment, the class flags, the specials and the `FIRE`/`FLYOUT`/`IMPACT` bindings, `IMPACT` keyed by
`SurfaceRegistry` id and `DESC` resolved through `Messages`. Modelled on `PlaneStats`. `RANGE`,
`DETONATION_DISTANCE` and `IMPACT_PROXIMITY` are each exposed twice, authored and squared, and the
rule for comparing them sits at the fields themselves; `ImpactHook` is the per-weapon detonation hook
`ProjectilePool.RunImpactHook` dispatches. Schema: [../formats/weapons.md](../formats/weapons.md);
behaviour: [../org/ordnanceTypes.md](../org/ordnanceTypes.md). Inspect with `--dump-weapons`.

## src/Flight/Weapons/Loadout.cs
Two layers over `CSVM/data/stock_loadouts.json`. `StockLoadouts.Load` parses the file into per-plane
`LoadoutDef`s; `Loadout.Bind(def, builtPlane, WeaponDefs)` resolves each gun slot's markers to live
muzzle `Node3D`s and its caliber plus ammo to a `WeaponDef`, and each hardpoint to its `pylon`,
yielding `GunGroup`s with their own ammo counters and `Hardpoint`s. Turret slots bind but stay inert.
`Loadout.ForRig` synthesizes a lab loadout covering the airframe's whole rig rather than only what
stock names, and runs it through the same `Bind`, so there is exactly one bind path. `Hangs`, `WingPylons`, `WingCounts` and `PylonForCell` read a fit the other way, which pylons it carries, how many each wing hangs and which one a saved ordnance cell names, off the rig's odd-to-port split rather than a count heuristic, so the flight check, the ammo screen and a fresh hangar build all bound their cells alike. Inspect with
`--dump-loadout`. Slot-to-firepoint binding: [../formats/markers.md](../formats/markers.md); schema:
[../formats/loadouts.md](../formats/loadouts.md). Read `LoadoutChoice.cs` next.

## src/Flight/Weapons/LoadoutChoice.cs
One pilot's edits to a fit, and the rosters the Ammo Selection screen offers. `LoadoutOptions` holds
the two dropdowns parsed from the same file's `selectable` block, authored in the original's own
order and neither derived nor sorted. `LoadoutChoice` keys its picks by slot identity rather than by
position in a def's arrays, and `ApplyTo` lays them over a base handed in rather than looked up, so a
custom plane's saved fit takes the same path and a pick for a slot the base lacks is dropped. Guns
are slots 1 to 4; ordnance is either a physical pylon, which the loadout screens read off the fit,
or a saved record's wing cell naming a pylon only against a fit (`Loadout.PylonForCell`). `None` is
an explicit empty mount taking no pick, a null entry no choice at all. Fills: `Session/Campaign/CampaignLoadout.cs`.

## src/Flight/Weapons/WeaponBench.cs
The world-less "do all 48 weapons mount and fire without throwing" pass check behind `--weapon-test`
and the `weapons-fire` in-engine suite: one static `Run(plane, Loadout, WeaponDefs, ProjectilePool)`
over a parked plane that spawns straight into the caller's pool and returns the report plus the
counts a suite asserts on. It needs no world, no colliders and no frame, since `Spawn` does the
muzzle math and the pool insert synchronously. Both callers hand it `Loadout.ForRig`'s loadout, so
every weapon fires from every mount of its class rather than only from what stock names. Read
`Loadout.cs` for the rig it binds and `Projectile.cs` for the pool it fills.

## src/Flight/Weapons/FireControl.cs
The fire-control state machine, a plain engine-free class: trigger edges, per-group `FIRE_RATE`
accumulators and muzzle rotation, ammo draw-down, both weapon selectors in either direction with
their on-empty auto-advance, the rocket pull and cooldown gate, and the two once-only dry cues.
`Step(dt, FireInputs)` takes raw held booleans, detects every edge inside, splits the pad's one
button per class into a tap forward and a hold back, and returns decisions in one reused `FireOutcome`; `FlightController.ApplyFireOutcome` performs them against muzzle
transforms, `ProjectilePool` and `FlightAudio`. Ammo mutates through the node-free
`IGunSlot`/`IPylonSlot` views, so `Loadout` stays the single store the gauges read and a decision
cannot diverge from the counters mid-tick. The slot index math is `WeaponCursor.cs`; read it next.

## src/Flight/Weapons/AimAssist.cs
The gun aim assist, decoded in [../org/aim-assist.md](../org/aim-assist.md). `GunAimSlot` is one gun barrel's
plane-local state and `Tick` the per-frame forget and catch-up pass over it; `TryIntercept` is the constant-velocity
lead solver every other module in the namespace consumes rather than re-deriving; `Scan` picks the target the original
would pick out of an `AimCandidateSet`, whose four lists are kept separately and filled by `ProjectilePool` and
`DestructibleRegistry`, the structure one whole through `AddStructures` for the player and narrowed to the flagged
mission-structure nodes through `AddMissionStructures` for an AI pilot; `FireDirection` is the whole fire call in one
place and `Scatter` its launch cone. A static, engine-free class that reads no clock of its own, so it unit-tests
without a `FlightController`, which owns one `GunAimSlot[]` per firable gun group. Read `FireControl.cs` next.

## src/Flight/Weapons/TargetRef.cs
The one abstraction over everything the player can select: an enemy Fury, a zeppelin engine and a
turret emplacement are three unrelated C# types, and every consumer downstream reads this and never
the underlying type. It wraps an `AimCandidate` rather than restating it, adding what the aim assist
has no use for: `Kind`, `Class` and `Objective`, the display names and labels, and optional health
and armor. `Classify` is the decoded class model, `CategoryLine` composes the marker's first line,
and `SortsFirst` is the pair of overrides that put an objective or a hostile round in flight ahead of
every sector. Pure data, no Godot node; pinned by the `target-ref` suite. Decode:
[../org/targeting.md](../org/targeting.md). Read `TargetPool.cs` next.

## src/Flight/Weapons/TargetPool.cs
The player's classed candidate pool: three lists of `TargetRef` (`Enemy`, `Ally`, `NonAircraft`, reached through
`Of(TargetClass)`), rebuilt from scratch on every `Rebuild`, the original's own contract and why a spawn appears and a
death disappears with no extra plumbing. It walks the aim assist's aeroplanes and live ordnance only; the mission's
sites arrive through `objectives` under their record's flag, `objective` on the Enemy cycle and `other_target` on the
Non-Aircraft, and a roster block's own flag marks its aeroplane's candidate. Sub-parts arrive through `subParts` only
while `selectedWeapon` carries `LOCK_ON`; a gun emplacement is on no cycle. The gamez ancestor chain `CollectOwners`
hands the `rating_biases` match, and a pool's own anchor name `NameOf` returns, are cached per destructible instance:
each name read allocates a finalizable `StringName`. Read `TargetSelection.cs`; decode: [../org/targeting.md](../org/targeting.md).

## src/Flight/Weapons/TargetSelection.cs
One pilot's target selection: the sticky choice, the eleven actions and the lifecycle. One instance
per pane, and it owns its `TargetPool`. The split between the action handlers, which only mutate the
class and the selection identity, and `Resolve`, the per-frame pass that re-sorts and re-finds the
selection by entity before falling back to the list head, is the original's, and that one fallback is
the entire lifecycle: auto-acquire, switch-on-death and drop-on-class-change are all the same failed re-find. `NearestAfterKill`, the remake setting off by default, is the one departure: it moves the lost-selection re-resolve alone onto the nearest entry by distance, leaving the acquire and every class change on the head. `SectorKey` is the cycle comparator, and `Select`/`ApplyInitial` are `--target=`'s seam. No
Godot node dependency; pinned by the `target-selection` and `target-flag` suites. Decode:
[../org/targeting.md](../org/targeting.md). Read `TargetHud.cs` for what draws the result.

## src/Flight/Weapons/TurretDefs.cs
Typed reader over the shared `ai.zrd`'s `TURRET` section, 42 `TurretDef`s: the carried/standalone
split (`CREATE_STANDALONE` present and zero means carried, looked up by `TITLE` from a host, while
`NODES` patterns mean world emplacements), the `PARTS` kinematic chain, the `WEAPON` sub-block whose
`NAME` is a BALLISTICS id, the arcs, the attack and bored duty-cycle windows, and `SOUNDS.CANNON`. It
tolerates the eight engine-accepted keys nothing authors, and `FindByTitle` mirrors the engine's
lookup, where a titleless entry matches unconditionally. `TeamId` carries the decoded loader default.
Schema: [../formats/turrets.md](../formats/turrets.md); the team space:
[../org/targeting.md](../org/targeting.md). Read `TurretController.cs` next.

## src/Flight/Weapons/TurretController.cs
One `ai.zrd` turret gunner, both families: carried (`BuildCarried`, per host off the vehicle def's
`TurretMount`s, ticked from `FlightController.SimStep`) and world emplacement (`BuildEmplacements`,
per matched `NODES` pattern node, ticked by `Session/TurretEmplacementRuntime`). Per tick it takes
the nearest hostile out of the vehicle list and then the mission structures, stopping at the vehicle
pass while `AiTargetRanking.AircraftFirst` holds and an aircraft is in reach, solves the lead through
`AimAssist.TryIntercept`, slews the PARTS nodes inside the authored arcs, and runs the fire gates:
activation, the attack window, the barrel-on-solution cone, a cached line of sight and the
`FIRE_RATE` redraw, each round renewing its `GunVoice` off `SOUNDS.CANNON`. A carried turret reports each round to its host rig, which is what a network match puts on the wire, and the gunner on an aeroplane flown elsewhere does not run here at all. An acquired human player is reported once per episode to `ProjectilePool`, the combat voice's `WA-Turret` site. Aliveness, teams, and what a gun's own mount is to its sight line and to its rounds sit at their members: [../org/targeting.md](../org/targeting.md), [../formats/turrets.md](../formats/turrets.md), [../formats/combat-voice.md](../formats/combat-voice.md).

## src/Flight/Ai/SurfaceVehicle.cs
One built hull: no pilot, no flight model, no `FlightController`. Its movement is the scripted-path
follower's law (`PathFollower.cs`, [../org/flightModel.md](../org/flightModel.md)) over an
unbounded route, the generator's take-off run then a lazy walk of the patrol net's edges, height
pinned to the water. `Patrol` is the roster and `SET_AI_NET` assignment, `Launch` the generator's,
`Wake` the `WAKEUP_ENEMIES` arm a block's `deactivated` waits on. Damage is the chapter's pool on
the root, a rung of the injure ladder playing as it falls, the death leaving the parts to the death
sequence. Its gun is `SurfaceGunner.cs`, stepped for a woken, undestroyed hull; `Session/World/SurfaceVehicleRuntime.cs`
builds one. A replicated hull chases the host's `TryReadPatrol` samples under `Airframe/ZeppelinReplica.cs`'s law.

## src/Flight/Ai/SurfaceGunner.cs
One hull's gun ([../org/aiPilot.md](../org/aiPilot.md) "What a `mode ship` vehicle runs"): the
acquisition, mount and fire decision a patrol boat runs, built from the def's own `weapons` tuple
and the model's `turret` > `gun` > `firepoint` chain, or not built when any input is missing. It
sweeps the pool's three candidate lists under the team gate, ranks with the non-`jet` scorer and the
defs' class biases, holds a target for a hardcoded 20 s, aims through `SurfaceGunMount` and
fires on the authored window, interval and magazine, dropping non-aircraft candidates so a boat does
not shoot a boat, which is why the aircraft-first preference is not spent here. No pursue gate and no
quick draw, neither reaching a hull. `AttackRadius` is the hull's own ATTACK volume, the one both decoded scorers admit on, never its activation ([../org/aiPilot.md](../org/aiPilot.md)). Its `GunVoice` is renewed per tick from the hull origin ([../org/weaponFire.md](../org/weaponFire.md)). Suites: `surface-vehicle-guns`, `surface-gun-voices`.

## src/Flight/Weapons/ISurfaceVehicles.cs
What a plane and its projectile pool read of a mission's surface hulls: `Vessels`, the list the
proximity fuse measures against, and `CollectVehicles`, which puts every hull on the aim assist's
and the target scan's vehicle list. `FlightController.SurfaceVehicles` and
`ProjectilePool.SurfaceVehicles` hold it. The session builds and steps the hulls, so its
`Session/World/SurfaceVehicleRuntime.cs` is the one implementation and `Flight` never names it.

## src/Flight/Hud/WeaponCursor.cs
`FireControl`'s internal ammo-slot index math, an `internal` class nothing else may call: `NextArmed`
is the firing cursor, the selected slot while it has rounds and otherwise the next armed slot
forward-wrapping, and `NextSelectable`/`PrevSelectable` are where a manual selector press lands, the
nearest armed slot either side of the cursor with empties skipped. Both are one private walk taken in
two directions, so the skipping and the wrap cannot drift apart. Each slot is its own selectable
position whatever it carries, so the selector steps across slots rather than ordnance types and
cycles even a uniform loadout, the per-hardpoint reading the original gives at the controls.
Stateless, proven through `FireControl`'s own interface. Read `FireControl.cs` next.

## src/Flight/Airframe/FlightReentryLatch.cs
Flight's consumed-input latch. A cutscene skip or a pause-sheet dismiss hands input back on the
frame the control that confirmed it is still down, and one control serves both sides: gamepad B is
`MenuBack` and `FireGuns`, gamepad A is `MenuAccept` and `FireRockets`, and a cutscene takes any key.
`Arm` runs where input comes back and `Read` is each latched action's own read after it, released
until that control lets go; `Latched` names the discrete commands covered and
`FlightController.ReadLatched` is the one read site they share. The arm takes no button reading: a
re-entry point can run inside an input handler whose snapshot predates the press this exists to
swallow. Pure state, public so its own unit tests drive it. Read `FireControl.cs` next.

## src/Flight/Weapons/Ballistics.cs
The VELOCITY/ACCELERATION/GRAVITY integration every round steps with: a static, Godot-`Node`-free
class holding `Step`, one round's per-frame advance mutating position and velocity in place, and
`March`, the reticle's whole capped walk with its range cap and iteration bound. Extracted so the
live pool and the pipper cannot silently diverge, while each caller keeps its own step size and its
own surrounding loop. `LaunchSpeed` is the seed pair both take, and the motor rule and the prohibition
on ever reducing a speed are stated at the members that hold them. Behaviour:
[../org/ordnanceTypes.md](../org/ordnanceTypes.md); the census behind a fixed `dt` for `March`:
[../formats/weapons.md](../formats/weapons.md). Read `Projectile.cs` for the pool that steps it.

## src/Flight/Airframe/DisablingIntensity.cs
The shared `SONIC`/`FLASH` intensity, decoded in
[../org/ordnanceTypes.md](../org/ordnanceTypes.md): a static, Godot-`Node`-free `TryResolve`
returning the wash weight and the stun duration, plus `FacingDot` for the `FLASH` direction
convention. The curve is a plateau that fades over the last quarter of the radius, so both distance
inputs are squares, the engine's own distance routine returning a square and this path never taking a
root. `FLASH` adds the facing test and `SONIC` does not, which is the only behavioural difference
between the two flags. The consumers are the player's screen wash, the AI stun and the smoke screen,
and this module knows about none of them. Read `SmokeScreens.cs` next.

## src/Flight/Hangar/Difficulty.cs
The difficulty setting, as the engine's own 0/1/2, and the two things one integer `k` does at spawn:
multiply a hostile vehicle's armour and health maxima
([../org/vehicleDamage.md](../org/vehicleDamage.md)), and shift that pilot's nine skill ratings
before they interpolate ([../org/aiControlLaw.md](../org/aiControlLaw.md)). `AppliesTo` owns the one
gate both stand behind, which is hostility, so a neutral takes neither; `FactorForSpawn` and
`SkillRatingForSpawn` are the two answers, the second exempting an `ace` block. `Parse` takes both
shipped vocabularies, which name the same three tiers. Read `PlaneStats.cs` next.

## src/Flight/Weapons/TanglerChoke.cs
The choker's engine-dead duration, decoded in
[../org/ordnanceTypes.md](../org/ordnanceTypes.md) "The choker, settled": a static,
Godot-`Node`-free `Duration`, plus `EngineDeadBounds`, which resolves the bounds the way the original
does, as a pair of globals every `TANGLER` parse overwrites so the last entry carrying one wins for
every choker in the install. The curve's numerator is squared while its radius is raw, an authentic
unit mismatch the floor covers, and whether an aircraft is caught at all is the cloud's own
squared-radius test in `ProjectilePool.StepTanglerClouds`. The seconds go to
`FlightController.TryChokeEngine`; this module knows nothing about aircraft. Read `Projectile.cs`.

## src/Flight/Airframe/SmokeScreens.cs
The `SMOKE_SCREEN` mechanism, decoded in [../org/ordnanceTypes.md](../org/ordnanceTypes.md)
"SMOKE_SCREEN is a stun trap", as three types in one file. `SmokeScreenRule` is static and
Godot-`Node`-free, holding the catch test and the human wash's cadence; `SmokeScreenTunables` reads
the three `player.json` keys with the loader's own image defaults; `SmokeScreens` is the world
registry, where `Lay` is the fire path's entry (such a weapon spawns no round) and `SimStep` runs the
timer down and stuns or washes every other in-play aircraft inside the cone about the layer's live
pose. It is not an occluder: no collision, no visibility and no targeting role. Each screen drives
its own emitter over the `ISmokeEmitter` seam, whose engine side is `SmokeScreenEmitters`.

## src/Flight/Airframe/BeeperTags.cs
The `BEEPER` and `BEEPER_SEEKER` pair, decoded in
[../org/ordnanceTypes.md](../org/ordnanceTypes.md) "The beeper and the seeker, which are one weapon
in two halves", as three types in one file. `IBeeperSubject` is the three facts a tag reads off its
aircraft, implemented by `FlightController` through a partial declaration here so the registry and
its tests run without an engine. `BeeperTagRule` is the static arithmetic: the countdown with its
dead-aircraft slam, the inverted alignment dot, and the running-best comparison with its four
literals. `BeeperTags<TAircraft>` is the world registry holding every creation gate, the per-step
list with its tail, and the seeker's per-frame `PickTarget`. Read `Projectile.cs` next.

## src/Flight/Camera/CamParams.cs
One aircraft's camera tuning out of `camparam.json`
([../formats/camparam.md](../formats/camparam.md)): the `default` block with the plane's own block
layered on top. Seven of the eleven airframes carry one; the other four take the 13.0 default.
Mirrors `PlaneStats.Load`'s shape (the same `Load(zrdrPath, planeNodeName)`, the same nearest-wins
resolution) and like it is loaded once per distinct plane and cached by `GameSession`. `FromData`
says whether the values came out of the file, so a partial extraction still flies and the log
distinguishes a value the data states from the built-in fallback. Read `CameraController.cs` next.

## src/Flight/Camera/PilotViewMode.cs
The three views a pilot can SELECT, valued as the engine's own camera modes (Chase 0, Cockpit 6,
Nose 7), and `PilotView`, the pure rules over them: the cycle key's three-stop order, the
first-person test the anim data's `PLAYER_1ST_PERSON` condition is answered with, the view in force
this frame (a held look-behind is an external pose outside first person and a head look-back inside
it), and the `--view=` spelling. It also holds the selectable list, the label and the step both Options screens draw the Default View row from, so one vocabulary serves the camera and the menus.
Engine-free, so the decisions unit-test without a camera, while
`CameraController` holds the state and the `Camera3D` they act on. Decode:
[../org/cameraViews.md](../org/cameraViews.md).

## src/Flight/Camera/CameraController.cs
The flown aircraft's camera: the roll-following chase pose and the head that swings it, the
look-behind, the right-stick look-around, the weapon lab's held-airframe orbit, the three static
cameras through `Statics`, and the pilot's selected view mode (`PilotViewMode` decides, this class
holds the state and the camera; `ResetToChase` is the player's own destroy callback). The chase
radius is per plane and dynamic, `Dist + DistFactor` times speed plus `DistTransient`'s authored
throttle term; `ExternalRadius` bounds it and carries the numpad zoom outward from the near bound.
`ChaseSwing` turns the chase offset, its image up and its look-ahead point together by `Head`'s angles, so the snap cluster and the mouse orbit the camera while a settled head returns the exact identity, and `StepHead` is where the placing view hands the head its elevation floor. Cockpit and Nose mount rigidly at the
authored `cockpit_camera` marker with `Head`'s angles and their own FOV; every other pose restores `ExternalFovDeg`, the decoded 60 degree horizontal base the whole port draws the world at, which `GameSession` and `Launcher` also read when they build a camera. Steers a `Camera3D` it does not own, `FlightController` its only host. Decode: [../org/cameraViews.md](../org/cameraViews.md).

## src/Flight/Camera/StaticCameras.cs
The three cameras that hold a WORLD point and re-aim at the aeroplane: the crash cut, the death
camera the pilot's own destruction enters, and the flyby. One placement shape serves both random
ones, a circle about the flight axis carried `speed · interval + z` along the nose, and one
clearance serves all three, a vertical probe that never lets a spot sit closer than `crash_elev`
above the terrain under it. The flyby adds its own bookkeeping: a drawn watch deadline in sim time
and a drawn switch distance, the re-site landing on the step after both are past. Placement and
clearance are static and engine-free but for the `IWorldQuery` probe; `CameraController` owns one
and does the aiming. Decode: [../formats/camparam.md](../formats/camparam.md).

## src/Flight/Camera/HeadLook.cs
The pilot's head, decoded from the original's shared look controller and shared by every view: it
aims the two first-person views and swings the chase camera, floored per frame by whoever places
the frame (level in first person, straight down on the chase camera, the original's own two literals). It holds the TARGET angles the input sets and the SHOWN angles chasing them exponentially, 3.0/s in elevation and 5.0/s in azimuth, and `Step` picks this frame's target before always chasing it, so
the snap, free-look, padlock, the centre key and autohead all reach the eye through one law. The four input paths are the original's: a snap-mode numpad direction mapped through `SnapTargets`, a free-look numpad direction or mouse pan integrating the targets at a fixed 2 rad/s along the normalised input direction, the centre key zeroing both, and padlock taking the whole bearing of whatever `TargetOffset` offers through `PadlockTargets`, with the caller's floor as its only clamp and no azimuth limit at all.
`LookMode` is the original's own mode byte (0 snap, 1 free-look, 2 padlock) and `SelectMode` writes it exactly once a frame: the `K`, `L` and `J` selectors on their press edge, then `HeadLookInput.ForceSnap` (the cockpit look-back); no device writes it, so both the numpad and the mouse obey the mode the keys chose, and padlock is left only by its own exit scan, which `Step` runs after the frame's bearing so the frame a direction arrives on still aims at the target and the snap state owns the next one.
A snap frame with no direction and no held pan zeroes the targets, and it and a padlock frame with nothing offered are the only kinds that consult `IdleAim`, the no-input hook `AutoheadTarget` fills; a free-look frame holds the pose the pan reached until a selector, a further pan or the centre key moves it.
`HeadLookInput.Looking` claims the pan with no motion on it, in either mode, so a held control over a still mouse holds the pose. `Nearest` wraps the padlock target onto the near side of the shown angle, the original's own crossing of the tail, and is scoped to that arm alone so the clamped relative paths still swing back through the front.
`StickLookFilter` is this file's other type, the centre band and 40 ms lag the raw look stick passes through before it aims anything, one instance inside the head and one in `FlightController` for the rigid chase swing; ask its `Active` whether the stick is claiming a view, never its filtered pair. Engine-free apart from `Mathf`; owned by `CameraController` as `Head`, stepped by `FlightController` on the sim clock.

## src/Flight/Hud/CockpitVisibility.cs
The per-mode node hiding the original applies to the pilot's OWN aircraft while a first-person view
is on the screen: Cockpit draws `cockpit1` and hides the `healthy` body, Nose hides the interior,
the body and the `markers` and `dontmove` groups, and every external pose renders the plane as it was
built. `Rules` is the pure decision over `(PilotViewMode, firstPerson)`; `Bind` finds the four
groups and `Apply` writes one frame's answer, called by `FlightController._Process` keyed to the pose
that frame took, so a held look-behind brings the body back. `ShowForPhotograph` shows the hidden
airframe for the Danger Zone camera's frame on a layer no pane draws; `EndPhotograph` undoes it.
Decode: [../org/cameraViews.md](../org/cameraViews.md).

## src/Flight/Hud/CockpitOverlay.cs
The cockpit interior's own render pass, the shipped path `--no-cockpit-pass` opts out of. It re-parents
the built `cockpit1` node into a `SubViewport` with a `World3D` of its own, on the mount basis
`PlaneBuilder` gave it, and puts the pass camera at that world's origin aimed by
`CameraController.FirstPersonPose` with the plane position and `cockpit_camera` offset both zero,
since those cancel between eye and panel, so the projection is the main world's exactly. The
viewport sits on `HudLayers.CockpitPass`, over the flare and whiteout overlays and under the HUD,
lit by a re-aimed copy of the world's sun and a duplicated Environment (enhanced adds shadows). `GameSession.BuildCockpitPasses` builds one per `PlayerRig`; `Sync` follows
the interior `Visible` and sets each material's `light_origin` to the eye for the point lights.

## src/Flight/Hud/CockpitGauges.cs
The authored instrument panel inside the pilot's own `cockpit1` interior: five needle nodes take an
absolute angle each frame, the artificial-horizon ball and the compass drum take the engine's own
basis, and the belts, damage-zone skins, readouts and two warning lamps take the state their
condition says. All of it reads `GaugeCluster`'s already-computed values rather than re-deriving
them, so the 3D panel and the screen-space dials cannot disagree. `Bind` finds the nodes in one
built interior (null on any plane without one) and `Apply` is the per-frame write, the same shape
`CockpitVisibility` uses, called only while the interior is on the screen. Node names, the
authored-rotation rule and the per-airframe name variants: [../formats/hud.md](../formats/hud.md).

## src/Flight/Weapons/ImpactOutcome.cs
"What should happen when this weapon hits this surface id" as a value: the effect name and which
`IMPACT` slot it came from, the sound, the stand-in burst and the damage/blast-radius pair.
`Resolve` is the whole decision, with no Godot type, no scene and no sound archive behind it, so a
unit test reaches the dispatch directly; `ImpactSuppression` is the mask a weapon's impact hook
returns. `ProjectilePool.Impact` reads the struck surface id and calls it, `Apply` performs the
answer and decides nothing. The stand-in ladder (whose `effectBound` arm keeps a burst off a row the
effects runtime renders) and the `default`-row backfill are decoded at their own members.
Decode: [../org/weaponImpact.md](../org/weaponImpact.md), [../formats/weapons.md](../formats/weapons.md).

## src/Flight/Airframe/CraterGate.cs
Whether a round's ground strike asks for a crater, as a pure function with no scene behind it.
Two rules meet in `For`: the original's own, the struck node's `CAN_MODIFY` flag over a `CRATER`
weapon, which no shipped node satisfies, and the remake's own carve option, under which any rocket
warhead's ground burst carves. The answer is a `CraterAsk`, not a bool, because only the faithful
rule suppresses the weapon's `ANIMATION` and `SURFACE_ANIMATION` slots: the option adds a bowl under
a burst that still plays. `Enabled` is the run's answer, armed at boot from the saved
`rocketCraters` key (never under `--det`) or from `--craters`, its only two doors since no menu row
offers it, and read by `ProjectilePool.Impact`. Decode: [../org/craters.md](../org/craters.md).

## src/Flight/Weapons/Projectile.cs
`ProjectilePool`, the shared-world weapon-fire subsystem: a fixed pool of rounds integrated off the
weapon data (launch and inherited velocity with its decay, acceleration, gravity, the steering step,
the two fuses and the three end conditions), the swept hit ray over world and aircraft, and the
impact that follows, the struck material's `IMPACT` row for a ray hit and the `default` row for a
self-ended round ([../org/ordnanceTypes.md](../org/ordnanceTypes.md), "Which row a burst reads").
Visuals: tracers and tip discs, the flash triad (none from the firing pilot's Cockpit view), the
muzzle light (the first-person pair joins the `WorldLights` point term in original mode via
`BindPointLights`), the `IMPACT` effect, sound, stand-in burst and water splash. Damage and presentation
leave through the sinks (`DamageSink` behind `WorldDamageGate`, `EffectSink`, `WashSink`, `BeeperTags`, and
`TurretAcquiredPlayer`, the seam both turret families report an acquired player through); a burst gathers
bodies and aircraft nearest-first, cover-tested, never the firing plane. Remake-own rules: the velocity decay
ignores the held target (`InheritedFraction`); the tracer's pixel floor ([../org/tracers.md](../org/tracers.md)); Enhanced's astern burst ring.

## src/Flight/Weapons/ProjectileFlyoutAnim.cs
The `FLYOUT` `MODEL_ANIMATION` half of `ProjectilePool`, a partial-class file. Every ordnance round
runs its own `AnimInstance` of the weapon's def (`he_rocket`, `sonic`, `torpedo_trail`) on the real
sequence interpreter with the pool standing in as the `ISequenceHost`: `StartFlyoutAnim` poses
`RESET_STATE` and fires the t=0 events inside `Spawn`, and `AdvanceFlyoutAnim` runs the instance
each sim step once the round has moved. `Dispatch` covers the kinds these defs author (node
visibility and scale, from-to tweens, opacity fades through `OpacityWriter`, spins, puffer trails, sounds, sequence and animation calls)
and logs anything else once. The trail puffers, the sonic's body roll and the torpedo's launch look
all come off this instance; `PoseAtResetState` gives every `BuildFlyoutBody` body the reset pose. Decode: [../org/ordnanceTypes.md](../org/ordnanceTypes.md).

## src/Flight/Hud/WarningShotCue.cs
The incoming-fire shield's shipped accumulator (player.json `warning_shot_max` / `_dissipation` /
`_interval`), lifted out of `FlightController` so it unit-tests without a live node. `Absorbs` is the
decoded flag a fresh airframe starts SET: while it stands, a gun round on the pilot's own aeroplane
has its damage discarded and rings `bullet_warning_sg` instead of the ricochet. `Tick` ages the
shipped interval and answers with the hit count the interval closed with, charging the accumulator
by the interval's own elapsed length and dropping the shield at the max, or draining and re-arming
on a quiet one. That answer also drives `CanopyHoleCue`, since the original runs both off this tick.
Decode: [../org/weaponFire.md](../org/weaponFire.md). Read `FlightController.cs` for the hit path.

## src/Flight/Hud/CanopyHoleCue.cs
The decoded cadence behind the canopy-glass cue, engine-free like `WarningShotCue`, whose tick hands
it the intervals. An interval that closed with at least one gun hit on the pilot's own aeroplane
opens one of the five `bullethole_anims` holes when the airframe's health fraction is under the
closed-hole share and the shipped 0.3 draw comes up. A hole opens once per sortie, and `Reset` is
what the `reset_bulletholes` spawn anim does to the ledger. `WindowHitSound` is the group the opened
hole's def sounds, and `ForceOpen` is `--canopy-holes=`'s way into the same ledger, past the gate and
the draw. `FlightController.OpenCanopyHole` runs the def an opened number names.
Decode: [../org/weaponFire.md](../org/weaponFire.md). Read `FlightAudio.cs` for what it plays.

## src/Flight/Ai/AiNetFollower.cs
Walks an `AiNet` patrol graph as a waypoint stream, aircraft-agnostic on purpose: positions in and a
target node out, with `AiPilot.Patrol` and `ZeppelinMotion` its two consumers. Given the vehicle's
nose it is the decoded walk, seating on the nearest node and flying the far end of the edge whose leg
best lines up with that nose, then repeating that pick at every arrival with the edge just flown
excluded, which is what makes a group sharing one net fly in formation. Arrival is measured along the
leg rather than as a capture sphere. It also holds a net's live stop-point flags and the structural
dead-end hold, both gated on `ObservesStopPoints`, and `Reseat` is the original's activation snap.
Decode: [../org/aiPilot.md](../org/aiPilot.md). Read `AiPilot.cs` next.

## src/Flight/Modes/DangerZoneRibbon.cs
The decoded danger-zone run ([../org/aiPilot.md](../org/aiPilot.md) "The danger-zone run"),
engine-free: `DangerZoneRibbon` is one `dzpathN` route as the original builds it, the polygon's
vertices joined by cubics parameterised in metres plus the lane table; `DangerZoneRun` is a pilot's
cursor on it, entered from the nearer end, walking the segments either way and `Done` past the exit;
`DangerZoneRail` is the state-5 integrator that writes the pose off the ribbon in place of the flight
model, closing the aeroplane's residual offset, banking the wings into the bend and settling on the
cruise speed. Every constant is read out of the image and named at its declaration. Pinned by
`DangerZoneRibbonTests`. Read `DangerZoneRibbons.cs` for the set a mission carries.

## src/Flight/Modes/DangerZoneRibbons.cs
A mission's ribbon set read straight off the chapter gamez, independent of `--debug-dzpaths`: every
`dzpathN` node's route polygon by the route-versus-gate-pair material rule
([../formats/missions.md](../formats/missions.md)), never by polygon index, its children as lanes,
the node's own flag word as the zone difficulty, and `dzones.zrd`'s `disable` list as the inactive
flag. `ByIndex` serves a numbered net tag, `NearestEnd` the negative one, and `ProximityPick` the daredevil roll's own walk, the active, free-lane and difficulty admission and then the end inside 500 m whose ribbon leads away best.
One instance per session, shared through `AiPilot.DangerZones`, because lanes are occupancy-counted across pilots; `CampaignDirector.Attach` builds it and hands it to every roster pilot. Read `DangerZoneRibbon.cs` for one route's geometry.

## src/Flight/Modes/DangerZonePhotograph.cs
The Danger Zone camera's eye, one per human pilot, the `PaneRequest` `StuntCapture` and
`Session/CampaignSnapshot` are handed. `Pose` is the decoded pose, engine-free: 2.5 `camparam`
`dist` ahead on the nose's level heading, a world-axis scatter of 0.15, 0.25 and 0.15 `dist`, and a
roll-free look back at the aircraft. The node is a `SubViewport` on the pane's world that poses its
camera in `_Process` after the controller's, at the external FOV with no HUD, shows a first-person
pilot's airframe through `CockpitVisibility`, arms the fill light (`csky_photo_eye`) on its pilot's
instances, renders once, disarms, and reads back after `FramePostDraw`. Decode:
[../formats/campaign-screens.md](../formats/campaign-screens.md), "The danger-zone slot".

## src/Flight/Airframe/ZeppelinBroadside.cs
The pure zeppelin broadside law, engine-free: the decoded 90 degree arc against the moving hull's
lateral axis, where side alternation is geometric and the opposite cones never both bear; the
per-cannon stowed, deploy, ready and fire machine, whose `Step` emits the deploy, retract and ready
lists on durations taken from the authored anim defs, with its own re-fire timer; `TryAim`, which
consumes `AimAssist.TryIntercept`; `PickGasbag` and `FirstLiveTarget`, the decoded candidate walk in
authored order with no team or hostility read; and `CannonsEngaged`, the decoded byte the mission
script writes, while clear of which nothing deploys. `Session/World/ZeppelinRuntime.Cannons.cs` wires it.
Chain: [../formats/mission-entities.md](../formats/mission-entities.md).

## src/Flight/Airframe/ZeppelinDamage.cs
The pure zeppelin kill arithmetic, engine-free: `Survivors` and `IsDead` over the record's `healthy`
list, counted literally entry by entry because a shipped chapter names one gasbag twice; the
`AliveEngines` recount; `MayDamageGasbag`, the gasbag-only routing gate; and `CrossedStages` over the
record's stage list, where every crossed threshold fires once. The zone pools live in
`DestructibleRegistry` and `Session/ZeppelinRuntime` supplies the aliveness views. Pinned by
`ZeppelinDamageTests` and the `zeppelin-damage` suite. Read `ZeppelinMotion.cs` next.

## src/Flight/Airframe/ZeppelinMotion.cs
The kinematic zeppelin motion law: flies a `ZeppelinDef` along its net through `AiNetFollower`,
forward-only along the facing, speed by `max_accel` toward `max_speed`, and yaw and pitch through the
decoded per-axis steer law, whose commanded rate eases inside 25 degrees of error and whose angle
advances scaled by the speed fraction the hull is making, so a stopped hull cannot turn. No per-step
pitch band (the record's pair is degrees compared against radians). Stop points: throttle cut inside
the hold distance, then pose decayed onto the node and the leg's bearing; a seated follower
station-keeps. `Follow` takes a replicated hull's pose. Steering:
[../formats/mission-entities.md](../formats/mission-entities.md). Read `ZeppelinReplica.cs` next.

## src/Flight/Airframe/ZeppelinReplica.cs
A network guest's zeppelin, the original's receiver law: each host sample sets a target position,
speed and facing, the target is dead-reckoned along the facing between samples, and the hull closes
on both by `e^(-2 dt)` per step (`ChaseRatePerS`). A sample older than the newest by its per-zeppelin
sequence is dropped; `Reseat` moves the drawn pose when a scripted motion hands the hull back. On a
straight leg the hull trails the target by the speed over the chase rate. Pinned by
`ZeppelinReplicaTests` and the `net-zeppelin-path` suite. Decode:
[../org/multiplayer-messages.md](../org/multiplayer-messages.md). Pure state, no `Node`.

## src/Flight/Ai/AiPilot.cs
The non-player `FlightModel` driver: standing orders in (heading, altitude, throttle, an optional
`Patrol` net follower, an optional `Gunner` whose live target is chased at the decoded lead offset,
an optional `Machine` and an optional `Escort`), one `FlightInput` per sim step out, read by a
`FlightController` whose `Pilot` is set. A `Machine` is stepped first and picks this step's aim point
and parameter table; an `Escort` whose leader is in play takes the dispatch away from every mode but
stunned and avoid crash, which is the original's own wingman fork. `Stun` is the AI stun's entry,
leaving the throttle lever where it was so the aircraft coasts under power. Both danger-zone entries are here and share one `StartDangerZoneRun`: the reached net node's tag, and the decoded daredevil roll's proximity pick, which is offered only while a combat mode carries the machine's `Evading` flag.
Pure and seeded, so a fixed-dt run is deterministic. Decode: [../org/aiPilot.md](../org/aiPilot.md).

## src/Flight/Ai/AiControlLaw.cs
The original's own AI steering law, documented in
[../org/aiControlLaw.md](../org/aiControlLaw.md); read that page before changing anything here. An
aim point, that point's velocity and one of four parameter tables read out of the image in, one
`FlightInput` out: a desired speed from the aim point's own speed plus range-weighted lead terms, an
intercept solve for the direction, bank-to-turn with the elevator joining once the bank command is
inside a deadband, a wings-level rule, a low-speed unload, and a per-axis scale and limit stage off
`PlaneStats`. The throttle lever has one path, the walk toward the desired speed; the original's
distance-gated open-loop branch is not ported. The aim-altitude band is clamped for every caller but one: the danger-zone approach opens it for its own solve and closes it again, as the original does. Engine-free and pure over its arguments.

## src/Flight/Ai/AiEscort.cs
The formation-escort law a netless `mode wingman` aircraft flies, which in the shipped data is the
campaign's `wingman_N` and `bswingman_N` roster blocks and nothing else. A leader snapshot (position,
attitude basis, velocity, whether it is the player) and an optional target snapshot in, one station
point and that point's velocity out, over the engine's own five-state machine: close on the leader,
hold the body-frame station, fly a station on the target, and the two re-join states nothing in the
law enters. Every constant is decoded, the two stations included, and the separation push is what
makes the hold a weave rather than a tight join. Engine-free and deterministic; the driver is
`AiPilot.Escort`. Decode: [../org/aiPilot.md](../org/aiPilot.md) "The escort law".

## src/Flight/Ai/AiModeMachine.cs
The nine-mode AI state machine, owned by `AiPilot.Machine` and stepped from its `Next`: the mode list and
vocabulary are the engine's own debug-readout dispatch. It carries the three decoded cylinders as mutable fields: `AttackRange` is the decoded admission volume a picker is handed, `ReturnRange` is the chase leash, tested as a cylinder about `PursuitAnchor` (the pursuer's own pose where the promotion began) and the only geometry that ends a pursuit, and `ActivationRange` is the engine's simulation gate, which nothing in the machine reads.
Decoded and wired are the promotion into pursue on whatever quarry the selection hands over, paced by one dwell stamp (`AttackDwellS`/`NotPursuitDwellS`, 20/15 s for a non-vehicle quarry) that refuses a promotion while it stands, ends a chase when it passes and is lifted for the assigned target, the steady-hand roll a hit provokes as a power law over the bite it takes of
the pre-hit pools, looping on the leftover (`RollLogged` reports every hit reaching the pilot, rolls taken
and skipped alike, so its line count is the hit count), the `Evading` flag a failed test sets and the
weighted library draw it enters under the natural-touch, injector and predicted-end altitude culls (that last one vetoing a program whose predicted end falls under the floor and sweeping the predicted path below the ceiling, from the position and attitude `Update` was last handed), chaining a fresh maneuver until
the pursuer's nose falls off, the sixth-sense roll and its stun, the `Stun` entry, the rubber-band `lay off` `--no-assist` disables, `avoid crash`'s bands, and `RollDaredevil` with the 5 s stamp every refusal re-arms, which is the danger-zone look's own roll. Engine-free, inventions marked where declared.
Decode: [../org/aiPilot.md](../org/aiPilot.md), [../org/aiControlLaw.md](../org/aiControlLaw.md).

## src/Flight/Ai/ManeuverExecutor.cs
Plays one library maneuver's timed step program as `FlightInput` values: `Next(model, dt)` each sim step
until `Done`, consumed the way `AiPilot` is, with the state machine holding one per `evasive maneuver` run
and switching back to its own law on `Done`. Steps are target attitudes in degrees rather than stick
deflections or rates, composed onto the entry frame, which is the level entry-heading frame normally and
the full entry attitude for a `relative` maneuver; a positive-duration step is held for its time and a
zero-duration step advances when the attitude is captured. `PredictedPath` composes those same steps onto
that same frame without flying them, one decoded step reach each, which is the estimate the selection-time
altitude veto reads. Pure and engine-free, deterministic on a fixed dt (`ManeuverExecutorTests`).

## src/Flight/Ai/AiGunner.cs
The AI's forward-gun gunnery: per sim tick the host `FlightController` hands it the fire geometry (`Solve`), it
answers with the trigger (`WantsFire`) and the intercept, and each round leaves along `ShotDirection(muzzlePos)`, the
line from that barrel to the intercept point so wing guns converge, perturbed inside the dead-eye cone by one seeded
draw per shot. Gates in the engine's order: the quick-draw cone off the target's nose-tail axis, the separation inside
the slot's authored engagement window, then the airframe's traverse clamp on the lead with the residual the clamp
leaves gated in turn, so the employable cone is the traverse limit plus that gate. It also carries the standing target:
`TakeTarget` stamps the engine's 20 s `TargetHoldSeconds` and keeps the rank the host re-scores while the hold stands, and `IsPrimaryTarget` says whether a target is the roster's assigned `PrimaryTargetName`.
Engine-free; the live half is the `ai-gunnery` suite. Decode: [../org/aiPilot/aiWeapons.md](../org/aiPilot/aiWeapons.md).

## src/Flight/Weapons/SurfaceGunMount.cs
The gun mount a `mode ship` hull carries, pure and frame-local
([../org/aiPilot/aiWeapons.md](../org/aiPilot/aiWeapons.md) "A `mode ship` vehicle's mount"): the
animated branch of the per-mount aim update, which a boat and a truck take and no aeroplane does.
`Guard` pins the desired elevation into the band while preserving azimuth and unit length, and never
touches yaw. `Slew` closes a fixed FRACTION of the remaining angle per step, over the engine's own
lerp/slerp/opposed three-way, snapping whole once a step covers the turn. `AimQuality` is measured
against the RAW lead, so the guard's give-away is charged to the shot the way an aeroplane's
traverse clamp is. Pinned by `SurfaceGunMountTests`; not the aeroplane's mount.

## src/Flight/Ai/AiRocketeer.cs
The AI's ordnance employment, the gun path's twin: per sim tick the host `FlightController` ages the
vehicle-wide lockout and hands over the fire geometry (`Solve`), which answers with the trigger, the
hardpoint it chose and the direction the round leaves along, the clamped mount aim rather than the
raw lead. Gates in the engine's order: the quick-draw cone aborting the whole pass, then per pylon
the armed check, the two-way `DAMAGES_ZEPPELIN` match, the squared engagement band and the traverse
clamp's residual against an aim-quality cosine tighter than the gun's. The lead is solved per pylon
in the frame that round flies in, and each unlocked pass leaves a verdict behind, keyed without its
numbers so a host logs a gate change. Engine-free. Decode: [aiWeapons.md](../org/aiPilot/aiWeapons.md).

## src/Flight/Ai/AiVoiceDispatcher.cs
The combat-voice trigger dispatch, engine-free
([../formats/combat-voice.md](../formats/combat-voice.md)): events in, speaker, clip and outcome
decisions out, over the talker roll, the hardcoded halving on the bearing ids, the broadcast speaker
election where a failed roll passes to the next candidate, the damage tiers taken most-severe-first,
the death cries with force, and the computed bearing and taunt trigger ids. Availability comes from the injected
resolver rather than from def presence, and the "already talking" test from the `IsTalking` hook the
session answers off the radio channel. Pinned by `AiVoiceDispatcherTests` and the `ai-voice` suite.

## src/Flight/Ai/AiTargetRanking.cs
The decoded target-ranking formula ([../org/aiPilot.md](../org/aiPilot.md) "Target acquisition"): a
rank built from a weight, the distance and the bias terms, and MINIMISED, with the player carrying
a lower base weight than everyone else, a wingman a higher one, a gasbag a lower one, ±0.2 terms for
ahead/behind on a half-metre deadband, altitude sign and closing, and an effectively infinite rank
beyond the scorer's own ATTACK radius, the volume both decoded scorers admit on (the activation volume is the engine's awake test alone and reaches admission nowhere). `AiScorer` names the engine's two implementations and is required
because the wrong one is silent: `Other` drops those three geometry terms. Snapshots in, index and
score out, engine-free. `SelectBest` prefers the best candidate no ally holds; `ObjectiveBiasFor` matches `rating_biases` patterns, first match wins, saturating at always-target and at exclusion; a candidate's `ClassBias` carries the def's `target_bias`/`struct_bias` in raw rank units beside the objective bias, both negative and so both attracting. `KeepsStandingTarget` is the decoded hold's own per-tick test, a standing target kept while it still scores valid. `AircraftFirst`, the launch-scoped switch behind `--ai-targeting=`, is CSVM's departure: while any aircraft ranks, every structure-class candidate is withdrawn, so a picker fights a structure only with no aeroplane in reach, and the same withdrawal runs inside the hold so an aeroplane coming into reach takes an ally off a building at once.

## src/Flight/Ai/PursuitQuarry.cs
The flight law's snapshot of `AiGunner.Target` for one step, whatever its class: position, velocity,
the nose axis of an aircraft or zero for a turret or structure, the aircraft-only facts the merge
rule, the lay-off assist and the sixth-sense trigger read, and the vehicle and assigned-target facts
the pursuit dwell reads. `Of` is the one place a standing target
becomes this shape, so a pilot pursues a zeppelin engine through the same arm it pursues a fighter,
which is the original's `Target` vtable read ([../org/aiPilot.md](../org/aiPilot.md) "What pursue
does with a non-vehicle target").

## src/Flight/Weapons/IncomingFire.cs
`--incoming[=metres[,wep_id]]`, the incoming-fire test rig: a phantom shooter on each player's six,
firing the target's own gun or a named weapon into the shared pool under a shooter identity no
player holds. It exists so the shield and both of its cues are reachable deterministically, since an
AI gunner has to find its shot and a splitscreen pilot needs a second human. It aims along the
target's own nose, so the round overtakes on a parallel track and lands with no lead maths; the
metres argument offsets that track sideways, alternating sides, and a wide one is the rig's own
able-to-fail control. Read `WarningShotCue.cs` for what the rounds meet.

## src/Flight/Airframe/PhysicsConstants.cs
`PhysicsConstants.NomGravity`, the single player.json `nom_gravity` value shared by
`PlaneStats.Gravity`'s default and `ProjectilePool.WorldGravity` so the two cannot drift apart.

## src/Flight/Airframe/PlaneStats.cs
Typed per-plane stats ([../formats/vehicle.md](../formats/vehicle.md)), the one reader every flight
consumer takes its numbers from: vehicle.json `dynamics` resolved through the `kind_of` def chain,
the def's `turrets` block as `TurretMount`s, engines.json stock engine power, and player.json's
globals, which are the flight constants plus the tuning the cue, aim-assist, head-look and damage
paths read. It also carries the `crash` block's restitution ceiling, the engine sound defs and their
curves, `destroyable_parts` as `DestroyablePart` records with the def-level injure anims, and the
`collision` probe list, and `AiTargetBias`/`AiStructBias`, the def's two acquisition rank terms, and `AiAttackDwell`/`AiNotPursuitDwell`, the pursuit timers, all read off the chain the vehicle spawns as. `Load` resolves down the player chain, `LoadForAi` takes only the damage model off the AI chain, and the `With*` family layers roster, difficulty and hangar overrides on.

## src/Flight/Airframe/PlaneRoster.cs
Static, spec-free lookups over a `SessionSpec`'s plane roster: `PlaneFor(spec, index)`,
`PlaneDisplayName(stats)`, `Humanize(s)`. A plane's display name is the def's AUTHORED `title`
(`PlaneStats.AiTitle`, "Medusa Kestrel") where something has resolved it through the string table,
and the def-name derivation ("Bloodhawk") otherwise, which is what a player load and a bare rig get.
No session state: every call takes the `SessionSpec` explicitly rather than caching one, since
these are pure over their arguments.

## src/Flight/Modes/SpawnPoints.cs
Reads the flight spawn from a mission's OWN zrdr, a different archive than the shared `--zrdr`, in
three schemas: `LoadIa` for the instant-action `spawn_points` per scenario, which only some folders
carry and the original picks one of at random per launch, `LoadNetFreeForAll` for a multiplayer
mission's `net.zrd` table, and `LoadPlayerInit` for the story objectives' `PLAYER_INIT`. The first
two yield `SpawnPoint(Position, HeadingDeg)`, the third a whole `PlayerStart`. A spawn's `Forward`
is the nose axis its heading yaws to, so every placement reads one look-at point rather than
restating the conversion. Schema: [../formats/spawns.md](../formats/spawns.md) and
[../formats/net-spawns.md](../formats/net-spawns.md).

## src/Flight/Weapons/MissionTargets.cs
A mission's `targets.json` as one table: target key to its objective display keys
(`description`, `category_label`, `help_label`, resolved through `Messages`) plus the valueless
`objective`/`other_target` marker flags the mission starts with. A bare node name keys itself and
a nested `[parent, child]` entry keys `parent/child`, the same spelling `ObjectiveTarget` gives
the script's directives, so the two tables meet on one string. `Load(mission, chapter)` walks the
original's reader search path, and `ByNode` exposes the whole table for a consumer that wants the
starting flags rather than one key's labels. Schema: [../formats/missions.md](../formats/missions.md).

## src/Flight/Weapons/ObjectiveTarget.cs
One argument of a target directive (`ADD_`/`REMOVE_OBJECTIVE_TARGET`, `ADD_`/`REMOVE_OTHER_TARGET`,
`SET_HELP_LABEL`): a bare node name, or an authored `[parent, child]` path that is ONE target.
`Key` joins the path with `/` and is the identity every objective store and every site is keyed
by; `Node` is the last segment, what `targets.zrd` is looked up by. The script that reads it is
`Session/Objectives/ObjectiveScript.cs`. Format: [../formats/objectives.md](../formats/objectives.md).

## src/Flight/Weapons/ObjectiveSite.cs
One live objective site as the targeting path sees it: the flagged `ObjectiveTarget`, its resolved
name, the two label lines its marker prints, its position this frame, and which of the record's two
flags it stands on. ONE instance per site for as long as the mission flags it, because a selection
is held by source identity. `TargetPool` labels it; `Session/Objectives/ObjectiveSites.cs` builds and refreshes
the set. Decode: [../org/targeting.md](../org/targeting.md).

## src/Flight/Hud/MarkerDraw.cs
The world marker's drawing primitives, shared by `TargetHud` and `StuntRunHud`: the shadowed
reticle, the edge arrow with its tail stroke, and the centred text block with the pane-clamped
variant an off-screen marker needs. Owns the marker blue and the drop shadow, while colour and
scaled sizes stay with the caller, since each HUD scales through its own `HudMetrics.Scale`.
Where a marker goes is `EdgeMarker`'s, which is the module to read next; this is only what it
looks like.

## src/Flight/Modes/StuntMission.cs
Stunt Flying's per-pilot run state: `Load` builds the ordered danger-zone list from a mission's
ia.json `dzones` (marker positions, gate polygons, strings through `MissionTargets` and `Messages`,
null where a mission authors none), `Update` requires both polygon-plane crossings in either order,
`CollectTargets` offers the still-unflown zones to that pilot's own target pool as objectives, and
`Elapsed`, `CompletedAt`, `CompletionOrder` and `InCompletionOrder` carry the clock and the splits.
`ForAnotherPlayer()` clones an independent run so the archives parse once per session. Engine-free
apart from its logging. Read `TargetSelection` for how a pilot picks a zone, `StuntRunHud` for the
rest of what a run draws, and `StuntScoreboard` for what it scores.

## src/Flight/Hud/HudMetrics.cs
The one place the flight HUD decides how big it draws: `Scale(control, reference = 1440)` is
window height over the reference, damped by `PaneFactor`, the square root of pane height over
window height, inside a splitscreen pane. `hud.statusTextScale` and `hud.markerTextScale` multiply
only their matching flight-HUD text within a clamp, leaving arrows and layout at the base scale.
`ReadingBox` is the placement half: the reference frame's 16:9 at the pane's full height, centred,
which a pane at or under that aspect equals exactly, so a column anchored to it stays within
reading width on an ultrawide screen and on a stacked 2-player pane. `ColumnOutdent` is the narrow
half, a pane under that aspect giving its columns all but a minimal border back. One module.

## src/Flight/Hud/HudFont.cs
The game's own HUD bitmap font, rebuilt from `extracted/rimage/5pointhud.png` and the brighter
`5pointhudbrite.png` highlight variant: a proportional five-pixel font covering printable ASCII.
`Load` returns null with one log line where the atlas is absent, and `Draw`/`Measure` render onto
any caller's canvas at a scale `HudMetrics` supplies. Layout and colours:
[../formats/hud.md](../formats/hud.md); `HudFontTest` is the overlay that proves the renderer.

## src/Flight/Hud/HudFontTest.cs
The `--hud-font-test` verification overlay for `HudFont`: a known string drawn in the normal and
highlight variants near the top left of each player's pane, sized through `HudMetrics` so a single
view and a splitscreen pane can be screenshot and compared. A thin rule under each line marks
`HudFont.Measure`'s reported width, which is what confirms the metric agrees with the glyphs
actually drawn. Not part of the flight HUD; it is added only when the flag is set.

## src/Flight/Hud/ImpactReticle.cs
The gun aiming reticle: a viewport-filling `Control` drawing the game's own `impact_point.png`
pipper at a world impact point `FlightController` feeds it each frame, projected through
`Camera3D.UnprojectPosition` at draw time rather than cached. Fixed screen size scaled by
`HudMetrics`, one per player pane. What the pipper follows, and why it is deliberately not the
aim assist's line, is decoded in [../org/aim-assist.md](../org/aim-assist.md).

## src/Flight/Hud/EdgeMarker.cs
The off-screen edge marker's placement rules, engine-free and pure: `Resolve(projected, behind,
paneSize, anchorInset)` answers on-screen against edge-clamped as a `Placement` (the whole-pane
test, the behind-the-camera mirror, the degenerate-direction fallback, the anchor's clamp to the
inset boundary and the `Tip`'s to the pane itself, which are the arrow's two ends), and `ClockHour`
is the bearing in hours. Owns `InsetFraction`, the original's 5 percent per pane axis; it and
`anchorInset` (the spyglass disc's half window) place the ANCHOR alone, never the on-screen test.
The camera stays with the callers, each keeping its own arrow, tag and label styling. `MarkerDraw`
draws what this places; coverage is `CSVM.Tests/EdgeMarkerTests.cs`.

## src/Flight/Camera/Spyglass.cs
The spyglass's decoded rules, engine-free and pure. `RangeGate(fogRange, held)` is the slant range
the picture is allowed at: `near + (far - near) * 0.8`, capped at `RangeCapM`, times `EngageFactor`
while nothing is held, so it engages nearer than it releases and cannot strobe at the boundary; a
band whose far is no further than its near carries no fog information and takes the cap alone.
`FovDeg(radius, distance)` is `2 * atan(1.1 R / d)` clamped to 1.5 and 90 degrees. `Pose` stands
the camera at the pilot's own aircraft looking at the target, rolling with its attitude for an
aircraft and level otherwise. Also owns `RefWindow`, `RefRadius` and `DefaultOn`. Decode:
[../org/spyglass.md](../org/spyglass.md); coverage `CSVM.Tests/SpyglassTests.cs`.

## src/Flight/Camera/SpyglassView.cs
The spyglass picture: a square `SubViewport` rendering the SHARED world through a `Camera3D` of its
own, one per pane, hung on `TargetHud` so it sits inside that pane's viewport. `Aim` points it
(`Spyglass.Pose`/`FovDeg`), sizes it to the disc's drawn diameter, borrows the pane camera's clip
planes and cull mask and starts it rendering; `Idle` stops it. The world is inherited rather than
owned, so the target is the one in play and wears the flown zone's fog. `DiscMask` is the one
departure from the pane's view: the eye stands inside the pilot's own aeroplane, so that aeroplane's
layer (`UI.Boards.SplitScreen.OwnAirframeLayer`, stamped by `Session/HumanFlightAdapter`) is dropped, on
the original at the controls and not on the decode. `TargetHud.DrawDisc` masks it to a circle.

## src/Flight/Modes/StuntRunHud.cs
The stunt run's own readouts, one per pane and sized through `HudMetrics.Scale`: the clock and
zones-cleared status line, the one-shot intro banner, the zone-cleared flash, and the completion
banner, which in a race becomes this pilot's placing and who they are still waiting on. It draws
no marker: a danger zone is an objective on the pilot's own cycle and `TargetHud` marks it like
every other one. What it reports is `StuntMission`'s.

## src/Flight/Modes/StuntCapture.cs
The Danger Zone camera: one photograph of the pilot's aircraft per `dzN` marker per stunt run,
latched once on the physics frame the plane first crosses inside `StuntMission.DzRadius` of the
marker centre, lingering or a later pass latching nothing; the run's completing frame is the last
tested. A latch counts the shot, fires the `snd_dangerzone_camera` sting, raises `ShotLatched` and
requests the pane. The frame lands later on a worker, which writes the PNG named by chapter,
marker and run clock into `screenshots/stunts/` and the thumbnail; `Settle` completes the record
on the main thread and raises `ShotLanded`. The pixels come from the caller's `PaneRequest`
(`DangerZonePhotograph` live, the pane headless, a synthetic frame in a suite).

## src/Flight/Modes/ScoreStore.cs
Stunt best-time persistence: one JSON object in `user://stunt_scores.json` keyed
`chapter/mission/plane`, with `GetBest` and `RecordIfBest`, which never worsens a record and
answers whether the run was a new best. The public `Load()` always opens the player's own file;
the internal path overload exists only so a suite can point at a throwaway directory instead.
`CustomPlaneStore` is the same file-backed shape for a heavier record.

## src/Flight/Modes/StuntRace.cs
Splitscreen stunt-race bookkeeping: one `Racer` per player over their own `StuntMission`, with
finishing stamping the next placing, `RaceCompleted` firing once the last pilot is in, and
`Standings()` ordering finishers by placing then in-flight players by progress. `Restart()` resets
every mission and clears the placings, leaving the respawn to `GameSession`, which owns the
planes. Membership is append-only apart from the internal `Remove`, which compensates an
uncommitted roster build. Off-engine coverage: `CSVM.Tests/StuntRaceTests.cs`. Read
`StuntRaceBoard` for what a finished race draws.

## src/Flight/Modes/VersusMatch.cs
Dogfight deathmatch bookkeeping, engine-free: every pilot carries one signed score, `KillScore` per kill to the shooter
and `SuicideScore` per death with no killer to the pilot who died, the original's own amounts. `RegisterKill`/`RegisterDeath`
report those facts, `Advance(dt)` is the host-fed match clock, `MatchCompleted` fires once on a score reaching the target or
on the time-out (leader wins, equal top scores draw), `Standings()` ranks by score with ties sharing a rank and carries kills
and deaths for display, and `Restart()` zeroes everything and re-arms completion. `ApplyScore` writes a seat's row as the host reports it,
so a guest mirrors the host's board. `Replicate()` hands the clock, both limits and the ending to that host too: `Advance` then moves
nothing and only `ApplyState` ends or re-arms a match. `OutOfLives` holds a spent pilot down, `Leave` marks a dropped one, fewer than two
pilots with lives end the match as `AllAlone` (reason 4), and `NextWatched` picks the seat a spent pilot watches. Read `VersusMatchTests.cs`, `VersusHud`, `VersusBoard` and `docs/org/multiplayer-scoring.md`.

## src/Flight/Modes/VersusSpawnRotation.cs
Where a Dogfight seat comes back, engine-free: it owns the per-seat spawn-list ledger the opening
spawn sets (one living seat per point) and picks a respawn among the roomiest entries against the
living field, weighing the killer at `KillerWeight` and drawing between everything within
`RoomyShare` of the best, so the point rotates and no seat can be camped. `For` returns null when
there is no list, `Restart` reopens a round on the opening points, and the draw comes from a
caller-supplied `Random` so a pinned run replays. `GameSession` feeds it the live field; offline it hands the pick to `FlightController.RespawnPlacement`, and in a match only the host holds a rotation at all, its pick crossing the wire as a table entry.
Off-engine coverage: `CSVM.Tests/VersusSpawnRotationTests.cs`; the suites are `versus-spawn-rotation` and `net-spawn-rotation`.

## src/Flight/Modes/VersusHud.cs
The per-pane Dogfight HUD: a compact status line (remaining time, this pane's kills and deaths, the
leader's tag) in `StuntRunHud`'s run-status slot, and one marker per living opponent rig, either an
on-screen tag or `EdgeMarker`'s arrow and bearing in that opponent's own `SplitScreen.PlayerColor`.
`Build` binds the match and this pane's own camera; `HumanFlightAdapter` attaches the live rig list
and `FlightController` feeds the pose each frame. A kill has no banner of its own here: `KillLine`
words the match's version of it and `HudMessages` shows it, the one message element the original
has. The per-opponent marker is CSVM's splitscreen answer to the original's radar; the shape's
provenance is in [../org/targeting.md](../org/targeting.md).

## src/Flight/Hud/HudMessages.cs
The original's one centred HUD message element: four slots a fifth of the way down the pane, newest
in slot 0, each carrying its own colour and its own five seconds, a newer line pushing the older
ones down with their remaining time and a re-post of slot 0 refreshing it rather than duplicating
it. `KillLine` words one death as the reading pane sees it (its own pilot by name, a wingman with
no name, any other aeroplane by its title, anything else destroyed), `SideOf` picks the colour arm
off the victim's team, `WordsKillLine` keeps a hull flown into the world off that line, and
`PostCrash`/`PostTimeExpired` are the two notices that are not a death. All static, so a suite
asserts the decode with no `Control`. Decode: [../org/vehicleDamage.md](../org/vehicleDamage.md).

## src/Flight/Hud/PromptLine.cs
A control prompt's own centred line, three tenths of the way down the pane, in the landings rig's
flat pale yellow and without the drop shadow the markers and the message stack carry. Two per pane:
the original's auto-dock offer on the HUD layer, and the port's respawn prompt on the message layer,
the one the crash camera leaves up. `FlightHud` owns when each shows and what it reads; this owns
only where it sits, as `LineAnchor` over a pane size, static so a suite asserts the placement with no
`Control`. The fraction is of the pane, never of `HudMetrics`' reading box, so each splitscreen pane
centres its own. `Prompt` is a `UI/Boards/ControlLine.cs`, not a string, so a pad seat's control draws as a
glyph where the words go; `Line` is still the words. Decode: [../formats/anim-definitions/cutscenes.md](../formats/anim-definitions/cutscenes.md) "The prompt's own placement".

## src/Flight/Hud/TargetHud.cs
The per-pane targeting HUD, built on every human pane in every flight session: the pilot's own
selection from `TargetSelection` (objective sites included), a nearest AI-hostile fallback where no
selection exists, and the F16 / `--debug-markers` every-aircraft overlay. Draws the original's
bracket box and label block and owns the colour table, the label layout, the selected gun's reach
gate and the debug identity string. Off screen it owns the arrow, `ArrowHead`, `ShaftTail` and
`EdgeLabelAnchor` over `EdgeMarker`'s placement. It owns the spyglass's gates (sim pose, `PlanePos`)
and draws the picture: the eye on the drawn `RenderPose`, the aim on the target's drawn pose,
read after the flight rigs (`AfterFlightRigs`). Decode: [targeting](../org/targeting.md), [spyglass](../org/spyglass.md).

## src/Flight/Airframe/HaltReason.cs
Why the sim clock is stopped, as a flags set: `Paused`, which a player asked for and which carries
an owner, and `Ended`, which a results board raises. The clock advances only when the set is empty,
which is what lets two systems halt it at once without either resuming it out from under the
other. `PauseState` arbitrates and nothing else writes a reason.

## src/Flight/Modes/PauseState.cs
Who is holding the sim clock and why, engine-free in `VersusMatch`'s shape: one instance per
session, built by `GameSession` ahead of the rig loop and assigned to every rig's
`FlightController`. `TryToggle(playerIndex)` owns the pause half, claiming ownership on the way in
and resuming only for the owner, and it is refused outright while `Ended` is set. `Raise` and
`Clear` own the results-board half and reject `Paused`, which carries ownership and must go
through `TryToggle`; `ForceResume` drops a pause whoever owns it, for a rerun or an exit chosen
from a menu. A network session's pause is an `Overlay`: the sheet is up and `ClockHeld` stays
false. Off-engine coverage: `CSVM.Tests/PauseStateTests.cs`. Read `PauseBoard` next.

## src/Flight/Airframe/Weather.cs
`WeatherState`, the flown mission's own weather.json as per-zone `ZoneWeather` records: fog colour,
ranges and altitude, the sunlight block resolved into a world light, a sun orientation and its two
uncollapsed colours, the cloud-cover whiteout band, wind, and precipitation. `DefaultDiffuse` and
`DefaultAmbient` are the install's modal day pair, public because both lighting mappings anchor a
zone against them. `ResolveZone` picks the flown zone by name, falling back to the one zone whose
horizon subtree carries meshes where the requested one is empty and this mission also fogs it;
`CameraWeatherState` and `ZoneForState` are the per-frame camera zone `WeatherRig.Tick` publishes.
Schema: [../formats/weather.md](../formats/weather.md); runtime: [../org/weather.md](../org/weather.md).

## src/Flight/Audio/FlightAudio.cs
The own plane's non-positional audio: the engine, overspeed whine and rattle loops, plus the
one-shots a crash, a ground or water explosion, a survivable graze, an engine stop and a stunt
run's Danger Zone camera fire, most drawing the sound their own definition authors, not a fixed name.
The three incoming-fire cues are group draws, flat as the original plays them: `OnWarningShot`,
`OnBulletHit` and `OnWindowHit`, rate-limited by `FlightController`. The engine slot's pitch, gain,
definition and damage phase come from `EngineAudioCurves`, the gun loop and dry cue from
`WeaponAudioCues`; `MixGain` is the only own-ship scale left, for splitscreen. `AiEngineAudio` and
`AiWeaponAudio` are the positional pair an AI aircraft carries instead of this.

## src/Flight/Audio/EngineAudioCurves.cs
The engine-audio slot maths both audio paths read, because the original runs one per-frame routine
for the player and every AI vehicle: whether an airframe counts as damaged, the slot's damage phase
(`StepEnginePhase`: Healthy, Out while the re-arm timer runs, Damaged), which definition it holds
and the swap's one-off pitch draw, each slot's pitch and gain off the `PlaneStats` curves, the
rattle gate, the drive parameter and the cull distance. The drive adds a turn rate and a climb
attitude to each curve's parameter under a clamp with headroom above 1, which is why `SoundCurve`
exposes its steps separately. A pitch reaches the voice only where the definition accepts a
frequency write (`SlotIsPitched`), leaving the damaged and cockpit loops at their own rate. Decode: [../formats/vehicle.md](../formats/vehicle.md), [../formats/sounds.md](../formats/sounds.md), [../org/shakes.md](../org/shakes.md).

## src/Flight/Ai/AiEngineAudio.cs
The positional twin of `FlightAudio` an AI-flown aircraft carries instead of it: the same two engine
slots on `AudioStreamPlayer3D`s plus the injector's `snd_nitro` loop, and the cull that stops them
past `EngineAudioCurves`' cull distance and starts them again inside it. The nitro slot is keyed,
`RefreshNitroLoop` giving it 0.1 s more each time, so its cadence is the state machine's own calls.
Its listeners are the human pilots, the seam `AiWeaponAudio` and `ProjectilePool` read too.
`Attach` is the whole spawner-side surface. The damage phase steps through `EngineAudioCurves`
like the own ship's, except that a culled aircraft never ticks the re-arm timer. The `sound` log
carries each slot's verdict, a line per cull transition and one per damaged-engine swap.

## src/Flight/Ai/AiWeaponAudio.cs
The weapon half of what an AI-flown aircraft carries instead of `FlightAudio`: the sustained-fire
gun loop and the dry-trigger cue on `AudioStreamPlayer3D`s riding this node, so another aircraft's
guns are heard from where it is, which is where the original's fire tick puts its own
([../org/weaponFire.md](../org/weaponFire.md)), so no muzzle offset belongs here; the dry cue is
positional by decision where the original's is flat. `Attach` is the whole spawner-side surface;
the cues come from `WeaponAudioCues` and the cull with them, the margin over a cue's audible
distance ([../formats/sounds.md](../formats/sounds.md)). Neither player carries an attenuation
model or a `MaxDistance`: the level is `Mech3.SoundFalloff`'s. No `3D` flag means no world player.

## src/Flight/Audio/GunVoice.cs
One mounted gun's firing voice: a single `AudioStreamPlayer3D` on the mount's own cue, moved to where
the gun fires from and held sounding by a lease each renewal resets, so a firing spell is one
continuous burst rather than a clip restarted per projectile. The lease is the caller's, per mount: a
turret renews half a second per round, a hull's gun zero every tick
([../org/weaponFire.md](../org/weaponFire.md)). `Attach` takes the `GunVoiceHome` bundle of parent,
archive and listeners a session builds once, one voice per mount and never one per owner. The cue
and cull come from `WeaponAudioCues`; the player carries no attenuation model and no `MaxDistance`,
its level `Mech3.SoundFalloff`'s, and the `sound` log names each verdict with the gain it stood at.

## src/Flight/Audio/AudioListeners.cs
Where the session's audio listeners are, for every positional flight-audio path: the human pilots'
own positions, falling back to the node's viewport camera and then to zero range, which is what keeps
a missing-listener defect from reading as a correct silence. `AiEngineAudio`, `AiWeaponAudio` and
`GunVoice` all cull through this one call, so an aircraft cannot answer one listener model for its
engine and another for its guns. The same nearest-human seam `ProjectilePool` measures its one-shots
against.

## src/Flight/Audio/WeaponAudioCues.cs
The weapon-sound selection both audio paths read, `EngineAudioCurves`' counterpart for guns: a
definition name to a `WeaponSoundCue` carrying the stream, the definition's unscaled `VOLUME`, its
`RANGE` pair, its `3D` flag and the one cull distance past that pair, which every weapon voice takes
so the aircraft loop and a mount's gun cannot cull differently. It selects and
nothing else, which keeps own-ship concepts out of the world path. A firing loop is decoded `LOOPED`
whatever its definition says, and that flag is also the prewarm key (`WeaponDefs.SoundCues`). The
dry-trigger cue resolves through `WeaponDefs.EmptyClipSound`, the `NO_AMMO_WARNING` read of record,
so neither path can hold a name of its own. Definitions: [../formats/sounds.md](../formats/sounds.md).

## src/Flight/Camera/SpectatorCamera.cs
The `--freecam`/`--anim-lab` observation camera: WASD move, RMB-held mouse look, wheel speed and
pads through `Pads.For(_padDevices)`, every key and pad read resolving through
`InputContext.Camera` (`src/Bindings/`). The lab additions are `Frame(Aabb)`, the `FollowNode`
orbit lock (released by any translation input, re-locked by the target key through `OrbitLock.Next`
over `GameSession.LockCandidateAircraft`'s roster) and a public `Camera` accessor; while locked the
orbit answers the right stick and the triggers as well as the mouse. It is also the pane a pilot
out of the mission watches from (`Session/Roster/SpectateHandoff.cs`), with `padDevices`/`useKeyboard`
filtering each watcher to its own seat so two watchers move independently. Read `OrbitLock.cs` next.

## src/Flight/Camera/OrbitLock.cs
`SpectatorCamera`'s re-lock rule, taken as positions and an index rather than nodes so it runs
off-engine (`OrbitLockTests`), the same split `Pads.AssignPads`'s pure overload uses. `Next` answers
the nearest target to the eye from no lock, the next one outward from a held one, and wraps past the
farthest back to the nearest, so no press is a dead one. It RANKS rather than sorts: the answer is
an index into the caller's own list, which a sorted copy would only have to undo. Ties break on the
index, so two aircraft at equal range still get distinct turns and neither is unreachable. A
`current` out of range reads as no lock, which is what a stale index resolves to.

## src/Flight/Airframe/FlightModel.cs
The decoded, data-driven aircraft plant. Rotation sums stick torque, bank coupling, the
`return_rate` weathervane and the ground blow before exponential `ang_momentum_damp` decay;
`BodyRates` holds the original's quaternion half-angle rate, and `PhysicalBodyRates` and the
attitude update double it. Authored speed curves scale the stick command alone, and
`OpposingCommandLimitAt` softens a pitch or yaw command that separates nose from path, on the
decoded G ramp and AOA window. Every force and torque term comes off the attitude the step ENTERS
with, so that ramp reads this step's own delivered lift. Translation is a clamped lift demand plus
decoded Mach drag, thrust and gravity, the velocity direction rotating only through that lift and
the ground-blow steer. `FarFieldPlant` is the original's LOD branch, re-decided each step off
`FlightInput.NearestHumanDistSqM`; `Collide` is the decoded contact response, placement and
human-only normal impulse, lifecycle left to `AircraftContactResolver`. `FlightInput.Boost`
replaces the thrust lever and scales drag. Decode and ledger: [../org/flightModel.md](../org/flightModel.md).

## src/Flight/Airframe/StickRamp.cs
The original's keyboard stick as an accumulator rather than an on/off flag: a held key ramps the
axis toward full deflection at `Rate` 2.5 per second, so full travel takes 0.4 s, and releasing or
reversing drops the axis to centre in one frame. That asymmetry is why a fast stick cadence reaches
far less deflection than a slow one, and the analogue axes bypass it entirely. Pure and
engine-free; `FlightController` steps one per keyboard axis. Decode:
[../org/flightModel.md](../org/flightModel.md).

## src/Flight/Airframe/MouseFlight.cs
The mouse as a stick, decoded from the mouse arm of `FUN_00487460`: a cursor offset over the pane in
`[-1, 1]` per axis, each source dead inside its own deadzone (0.1 for the cursor's two axes, 0.3 for
the third) and rescaled so the pane's edge is full deflection, plus the `is_autogyro` exchange that
takes an autogyro's bank off the third axis and its yaw off the sideways travel, both negated. That
third axis is always zero here, so an autogyro's yaw is the one slot the exchange feeds from a cursor
axis and takes that travel's own 0.1, a port rule and not a decoded one. Pure and engine-free, so a
suite drives it with no window; `FlightController` sums what it returns into the keyboard and pad
deflections. Decode: [../org/flightModel.md](../org/flightModel.md); the scheme: [../controls.md](../controls.md).

## src/Flight/Airframe/AnalogAxes.cs
The two analogue shares of a seat's flight command. `Pad` bends pitch and roll through `PadCurve`
(0.15 deadzone, squared), the curve `FlightController.StickCurve` forwards to. `Stick` reads the
same actions with the same signs and no curve, since a stick's full-axis binding already deadzones
and rescales, so a throttle-pair full axis is a rate in proportion to deflection. The controller
sums both into the keyboard and mouse deflections and clamps each axis. `LeverPosition` reads a
Throttle (lever) from whichever side it is bound on and still connected, and `StepLever` releases
the takeover when neither is, so an unplugged stick does not read as half throttle. Engine-free.

## src/Flight/Airframe/StickSplit.cs
An `IDeviceState` filter that passes a seat's flight-stick identities alone (`SticksOnly`) or
everything else (`WithoutSticks`), deciding by `StickModel.TryFromDevice`. `FlightController` polls
one keymap through each, so a pad row and a stick row on the same action resolve apart for
`AnalogAxes`. The seat's own reader stays the only thing that reads hardware.

## src/Flight/Camera/MouseCapture.cs
The mouse a flight seat takes while it flies, under either mouse scheme. A captured pointer reports
one frozen position, so this scales relative motion into a virtual cursor confined to the pane
(`DeflectionCounts`, `FullDeflectionCounts` over the seat's sensitivity, reach its edge) for
`MouseFlight.Offset`, and banks the raw travel for head-look. `Centred` widens the stick's centre
band to `CentreBand` on this path only, ahead of the decoded 0.1. `Allowed` is the guard: a real
display with somebody at the controls, so the test desktop and `--det` keep their mouse mode.
`Restorable` is what a board with its own pointer puts back, never a capture. `FlightController` owns the mode write and release, and
`Session/Roster/FlightRosterInputs.cs` resolves the guard once per session. Read `MouseFlight.cs` next.

## src/Flight/Airframe/NitroSystem.cs
The original's nitro boost lifecycle, engine-free: a 30-unit tank burned at 4/s while boosting and
refilled at 1/s always, so a burn nets 3/s and runs 9.5 s from full to the 5 % cutoff.
`HumanCommand` engages on a held command only from a 99 % tank and re-asserts the flag until the
cutoff; `AiSet` has no engage line and fires once per nitro-flagged maneuver. Both refuse an
engine-out aircraft and a re-engage while the boost or decay animation is alive. `Installed` is the
injector, and `EngagedThisTick`/`ReleasedThisTick`/`LoopRefreshedThisTick` are the edges
`FlightController.AdvanceNitro` turns into the shake kick (a person's camera block, an AI's own
`medium_aishake`), the two defs and the keyed `snd_nitro` loop. Decode: [../org/flightModel.md](../org/flightModel.md).

## src/Flight/Ai/PathFollower.cs
The engine's SECOND movement law and the exclusive alternative to `FlightModel`: the dispatcher
picks between the two before any flight law runs, so nothing here is a steering input. Pure state
and maths, driven by `Session/World/ScriptedPathVehicles.cs`. `Following` (the path owns this vehicle) and
`Frozen` (placed and waiting) are held apart, because folding them together cannot express the state
most authored path vehicles spend a mission in. Every constant is decoded: the final leg steers at,
and ends at, the point 300 m along it from the waypoint behind, raised with speed, so a shorter
final leg flies past its last waypoint climbing (`FinalLegOvershoot`). Law, constants and the
unidentified ride-height field: [../org/flightModel.md](../org/flightModel.md).

## src/Flight/Airframe/PropAnimator.cs
Spins the flying aircraft's prop and rotor blur discs. `Build` collects every node `PropParts`
classifies (rest pose plus rate, radians per second about local axes) and `Advance` recomputes each
disc's absolute pose from its stored rest pose through `SpinMotion.ComposeSpin`, the same
accumulate-from-rest decode `AnimRuntime` plays the ambient world's `XYZ_ROTATION` spins through, so
a long flight session cannot drift. `FlightController` drives it throttle-scaled with a
`PropIdleSpin` floor and zero while crashed. `--fly` only; the static viewer keeps the still disc.

## src/Flight/Airframe/ExhaustSmoke.cs
The engine exhaust smoke, the original's one code-built puffer (`FUN_004afa20`, one per
`exhaust%d` marker from `FUN_00476250`): a near-black 0.4 m distance trail per marker. `Update(dt,
leverGap)` ports `FUN_004afbc0`: a non-negative commanded-minus-live gap adds `dt · gap` to an
intensity, which then decays by `exp(-1.5 dt)`; the trail runs above 0.01 and each particle keeps
`min(intensity, 1)` as its opacity through `Puffer.BirthAlpha`. Human and AI rigs both build it;
`FlightController` reads either pilot's gap before the slew, where `FUN_0048e580` does, and a held
seat or a scripted `--hold` feeds 0. `Reset()` clears it on crash, respawn and placement. Curves
and seeding: [../formats/effects.md](../formats/effects.md).

## src/Flight/Airframe/FuelTank.cs
The flown aircraft's tank, engine-free so the arithmetic is testable without a scene. `Step(dt,
lever)` takes `dt · lever · 5` off `Remaining`, clamps at zero and returns whether the throttle
lever may move this tick; false on a dry tank is what freezes the lever where it stands rather than
closing it. The lever handed in is the value entering the tick, before that tick's slew, matching
the original's ordering. `Capacity` comes from `PlaneStats.FuelCapacity` and `Fill` is the
spawn-time top-up; a non-positive capacity frees the lever, so a fixture without the authored key
still flies. Only `FlightController.ReadKeyboard` burns, the original's player-only gate, and nitro
costs no fuel, the burn reading the lever. Decode: [../org/flightModel.md](../org/flightModel.md).

## src/Flight/Hud/SpeedCue.cs
Loads `cuepuffer1..3` from the chapter's `speed_cue.zrd` and drives exactly one of them through
`Puffer.Emit` at the aircraft pose. Camera altitude selects the authored 30/15/8/15 m density bands;
within 50 m AGL none emits, while the script's empty branch above 1500 m keeps the current
selection. `LateralSpreadFor` is the one aspect-dependent term in the effect, a remake rule widening
the selected emitter's lateral spawn half-width by the pane's aspect over the authored 4:3. One
instance is built per player, its renderers stamped onto that rig's visual layer, and it shares the
session `EffectAmbience`, so the fade and the wind apply. `Reset` hard-clears all three on crash or
respawn, and `Dispose` removes every puffer node when roster assembly is rolled back.

## src/Flight/Airframe/ControlSurfaceMix.cs
The decoded angle solver behind the control surfaces, engine-free so the arithmetic is testable
without a scene: roll drives the two aileron slots at ∓0.5 rad, pitch the two elevator slots at
−0.6 rad with a ±0.18 rad differential roll term added, and yaw both rudder slots at −0.61086524 rad
times the reverse-authority factor. Aileron and elevator targets are clamped, the rudder is not, and
each slot then decays exponentially toward its target at 2/s, so the shape holds at any frame rate.
`Advance`'s `animate` flag is the original's player-only guard: false writes no slot at all, which
is how an AI aircraft's surfaces stay frozen. Addresses and the list population that fixes which
node takes which slot: [../org/flightModel.md](../org/flightModel.md).

## src/Flight/Airframe/ControlSurfaceAnimator.cs
The node side of `ControlSurfaceMix`: collects every classified surface and poses it absolutely
(`Basis = base · Rot(hingeAxis, slotAngle · scale)`) from its build-time local basis rather than
accumulating, owning the two frame flips the original has no need of, a mount rotated by yaw π and a
nose-mounted canard that raises the nose the other way. `--fly` only, frozen while paused or
crashed, reset on respawn. `FlightController` passes its `IsHumanPiloted` as the guard, so every
human pilot animates for splitscreen while AI stays frozen as the original has it.

## src/Flight/Airframe/WingLightBlinker.cs
Flashes the wingtip flares for `FlashDuration` (about one frame of the original's own footage) each
`WingLights.BlinkPeriod`, and toggles a matching `OmniLight3D` per flare, its range and colour from
`WingLights`, on the same window. Gated on the session's ANIMATION_LOD quality flag: below HIGH the
flares and lights stay off for the instance's life, which is the def's own low-detail branch.
`Reset` restarts the cycle with everything off and hands every suspended lamp back.
`Suspend(flareName)` hands one named lamp to whatever just deactivated it, which is how the fuel
leak's one-way `OBJECT_ACTIVE_STATE` deactivation survives the blink cycle; a suspended lamp is
skipped in `Advance`. Advanced each `_Process`, frozen while paused or crashed; `--fly` only.

## src/Flight/Weapons/PylonOrdnance.cs
The rockets mounted under a plane's wings. `Build` instances ONE flyout model body per loaded pylon
through `ProjectilePool.BuildFlyoutBody` (the same gamez prototype the round flies, posed at its
def's `RESET_STATE`) and parents it to that pylon marker at identity local transform, which is the
launch pose; `Update` shows or hides each per its live `Hardpoint.Ammo`, the root's only owner. `FlightController` drives `Update` after the rockets, and the
mounted body rides the plane and is freed with it. `Unmount` takes the set back off, detaching each
body from its pylon immediately rather than queueing it, so the weapon lab's rebuild-on-swap cannot
leave the old ordnance hanging beside the new. `--fly` only.

## src/Flight/Airframe/PlaneCollider.cs
Derives up to sixteen plane-frame convex hulls from the built model's mesh triangles alone, with no
per-plane data: region clipping partitioning the airframe on x and z (fuselage out to the wing band,
tail inside that band and aft, wing outboard), greedy volume-guided refinement cutting one or two
parallel planes per axis (the double cut separates bilateral pairs such as twin fins), then one
`ConvexHull` per refined piece, judged on the pieces' boxes so a hull never moves a cut or a name.
Single-sourced: the terrain sweep casts these hulls and `AircraftBody` mounts the same
`ConvexPolygonShape3D` resources as the plane's hittable body. `Layout` is the engine-free half the
`airframe-hull-coverage` suite measures; `docs/org/weaponRay.md` holds what they present over the mesh.

## src/Flight/Airframe/ConvexHull.cs
A convex hull over a point cloud with no engine dependency: vertices, outward faces, edges, bounds
and volume, plus `Contains` and the point-to-surface `Distance` the fuse and blast passes ask for.
Construction is incremental on millimetre integer coordinates with exact 64-bit volume signs, so a
near-coplanar mesh cannot fold it. A cloud thinner than the thickness floor along an axis is padded
to it first, the per-dimension floor the box shapes applied; a cloud too degenerate to hull falls
back to its padded bounding box.

## src/Flight/Hud/ScreenSize.cs
Screen-space sizing for world-space sprites, split out of `ProjectilePool` so the splitscreen rule
it feeds is testable without a live camera. `MinWorldSizeForPixels` inverts Godot's default vertical
projection to the smallest world size that still covers a pixel target at a distance, reading the
camera's OWN viewport height, which a splitscreen pane makes shorter than the window;
`NearestFloor` takes the SMALLEST of those over several `ViewerSample`s, which is what keeps one
shared mesh from inflating in every nearer pane. Degenerate inputs read as no floor. Decode:
[../org/tracers.md](../org/tracers.md).

## src/Flight/Camera/ShakeDefs.cs
Typed reader over the shared `shakes.zrd.json`, the six shake-oscillator sources, modelled on
`WeaponDefs`: loaded once into `AircraftAssemblyResources.Shakes`, with named accessors per source
and an unhandled-key tripwire. Each source is one law (frequency, damp, sawtooth) plus exactly one
magnitude-term variant (`magnitude_factor` with an optional `he_factor`, `min_speed` with
`magnitude_quotient`, or an absolute `magnitude`); an absent source reads as null and `PlaneShake`
no-ops it. Schema: [../formats/shakes.md](../formats/shakes.md); decode:
[../org/shakes.md](../org/shakes.md).

## src/Flight/Camera/PlaneShake.cs
The plane-wobble oscillators, summed each sim tick into `Roll`, the radians the controller writes to
`ShakePivot`; engine-free on purpose, so the pivot write is the controller's one line. The gunfire
buzz (`fire_bullet`), the being-hit rocks (`bullet_impact`/`missile_impact`/`explosion`) and
`ContactHit` (the oscillator no def authors, magnitude from `CollisionDamage.ContactShake`) are
decaying envelopes. The overspeed rattle (`high_speed`, per tick on the excess over its gate, which
sits at rated max) and the nitro engage (`nitro`, one kick, human pilots only) instead run the
original's own component block, a velocity kick into a two-branch integrator whose position renders;
`DiveRattleKickScale`/`NitroWobbleKickScale` are their only knobs. [../org/shakes.md](../org/shakes.md).

## src/Flight/Airframe/FlightControllerBuild.cs
The internal construction handoff from `FlightRoster` to `FlightController`: one resolved
controller's pre-tree state from either flight adapter, which `Bind` consumes exactly once. `HoldSegments` (the scripted hold), `LeverSteps` (the `--lever=` presses) and `RemotePoses` ride it
as `Pilot` does, so `FlightController` exposes a public field for none; `Bind` copies them before
resolving the `IFlightInputSource`, beside the `IWorldQuery` seam. A seat whose pose arrives over the
wire takes no stick arm at all: the buffer's presence IS the ownership, so no seat is half remote.
The same partial holds `ApplyProfile`, the one method a seat takes a keymap, mouse scheme and
sensitivity through, at build and on a Controls page accepted in flight.

## src/Flight/Airframe/IFlightInputSource.cs
The seam a sim step reads this frame's pilot intent through: `Read(dt)` returns one `FlightInput`.
`PilotInputSource` and `KeyboardInputSource` are thin wrappers over the matching `FlightController`
method, while `ScriptedInputSource` carries its own state instead, the segment list and its own
elapsed clock, so a suite can construct one with no `FlightController` in the process; its `Reset()`
is what a respawn calls to restart from the first segment. `FlightController.Bind` resolves which
one flies a given aircraft from whichever of the hold segments or `Pilot` is set, and stores it,
since both arrive through the build DTO before the first sim step; a remote-owned seat resolves to a neutral scripted hold instead, because its pose is received rather than flown and no device here may be read for it.
Ground-blow probing and the AI ground-blow write stay on `FlightController`, which has the live world a source does not.

## src/Flight/Hud/FlightHud.cs
Everything one pane draws for its pilot, none of it written from outside: the heading tape, the cockpit
dials and their two weapon gauges, the gun pipper, the stunt marker, the targeting HUD, `HudMessages`'
message stack, the two `PromptLine` prompts, the `--hud-font-test` overlay and the flight text block. With
the cockpit interior on screen the dials, tape and text block come off (`SetCockpitView`), its panel
carrying them; the pipper, marker, message and prompt HUDs stay, the respawn prompt on the message layer the
crash camera leaves up. `Draw(in FlightHudState)`, the per-frame entry, takes a struct of aircraft STATE, so
text, dials and gates compose and assert here with no `Control` (`ComputeStallWarning`, `ComputeAgl`,
`ComposeTextLines`, and both prompt gates and composers). Both composers return a `UI/Boards/ControlLine.cs` filled through the message table's own `%1` slot, so the seat's control reaches the line as words or as a glyph without either composer knowing which.

## src/Flight/Airframe/FlightController.cs
The flying-aircraft node: input through `FlightModel` to a transform (or, for an AI pilot publishing
a `RailPose`, the danger-zone ribbon's pose in place of the model step, the sweep still run), plus
weapon fire as `FireControl`'s engine adapter and the crash and respawn paths (`Respawn` takes what its `RespawnPlacement` hook answers, `RespawnAt` a pose handed to it instead, and `RespawnRequest` withholds the return altogether for a seat whose placement is somebody else's to grant). It keeps no rule it
can delegate: the camera is `CameraController`'s, the pilot HUD `FlightHud`'s, this frame's stick
one `IFlightInputSource`, the states an aircraft moves between `AircraftLifecycle`'s, and what a
contact costs `AircraftContactResolver`'s. This node reads the devices, performs what each of those
reports, and holds the state the engine can only hold as state. Every physics query runs through the
one `IWorldQuery` bound in `Bind`, and contact detection fills one `ContactReport` from the hull
sweep, the AI probe rays or the anti-tunnelling centre ray. An AI aircraft is this SAME node with
`Pilot` driving the input source, no camera and no HUD canvas, so flight, collision, weapons and
damage are the player's path exactly. `Held`, `ControlHold` (`FlightControlHold`: the discrete commands are swallowed and no crash cam brings a hull back, with the stick either the pilot's or neutral over the lever they left), `Inert`, `Spectating`, `CameraOwned` and
`AllowLiveRespawn` are the flags a session or a lab pins it with, and `RespawnPlacement` is the hook a session answers with where a respawn should put the aeroplane (`VersusSpawnRotation` in the dogfight), unset everywhere else so a respawn keeps the pose `Setup` fixed. `SelectRankedTarget` builds the pilot's four-pool candidate list, each entry carrying its own class bias, and hands it, with the machine's ATTACK radius as the reach, to `AiTargetRanking.SelectBest` under the session's targeting order; `HoldsStandingTarget` is the sweep's gate, re-scoring the standing target alone until the hold expires or the rank fails. A human seat also plays `Bindings/PadRumble.cs` at the sites that already carry a cue (gun fire, an ordnance launch, a round taken, a contact, the crash, the nitro, a turret shot and a dive past the rated maximum), on the pads `PadDevices` names and never another pane's. The once-per-death shutdown ends every flight system in one place, so the gun and engine loops, the `snd_propstop` cue and the propeller's own `stopprops` wind-down to the still disc leave together, and a respawn takes that wind-down off the slot before replaying the spawn choreography. `TickIncomingFire` runs the shield and the canopy cue off one tick, and `OpenCanopyHole` puts an opened hole through the rig as its authored def, with `EnsureViewCameraProxy` supplying the rig-local `camera1` that def's exterior branch poses against. The keyboard arm also plays `LeverSteps`, the `--lever=` schedule of commanded-lever presses at their own sim-seconds, which is how a headless capture slams the throttle as the digit row does and leaves the exhaust smoke a real lever gap to charge from; a live digit beats it. `RemotePoses` is the third pose source: set, `RemoteOwned` reads the sim pose out of that `Net/RemotePoseBuffer.cs` sample stream instead of stepping `FlightModel` at all, and every rule that would move the aeroplane locally is off with it (the stick read, the ground-blow probe, the nearest-human fill, the nitro and model steps, the sweep and contact resolution, the fire-control tick, a carried turret's own gunner, the under-map backstop and the respawn button), while being hit, damage visuals, engine and weapon audio, HUD markers and the crash rig stay live, because a remote human is a pose that arrives late and never a stick that arrives late. `WeaponFired` announces every round this rig spawns, `HitRouter` offers a strike to whoever set it before the local damage runs, and `TakeRemoteDeath` ends the aeroplane on a death decided elsewhere, while `RemoteAutoLand` is the auto-land button that seat's own machine reports holding; unset, all three leave the single-machine path exactly as it was. Read `AircraftLifecycle.cs` next.

## src/Flight/Airframe/PlaneDamage.cs
The decoded vehicle damage ledger: per-part pools from `destroyable_parts` plus a whole-vehicle
armour and health pair, authored where the def chain carries one and summed over the parts where
it does not. `Apply` is the decoded take-hit flow, spending armour first on the named zone,
redirecting a dead or unknown zone to a random survivor, recomputing the whole pair after every
part spend and draining it directly with whatever is left over, so a kill stays reachable with
every zone still healthy. The four fraction readings differ on purpose, each saying at its own
member which scale it is on. Decode: [../org/vehicleDamage.md](../org/vehicleDamage.md).

## src/Flight/Airframe/DamageVisuals.cs
Visible damage driven purely by data thresholds: as a part's health-only fraction crosses an
`injure_anims` entry it shows the torn `pdpN` panel, hides the healthy skin and plays that entry's
authored anim through `DamageEffectSink`, the player rig's own runtime. Staging is keyed per
ladder entry and cleared on the upward crossing, so a repair un-stages and the entry can fire
again. `RigAnimFor` is a curated membership test over the effect catalogue, which is what keeps a
cockpit gauge def from ever playing on an airframe, and the panel-pairing traps sit on
`PairHealthySkins`. The cockpit-interior twins ride the same panel table as their exterior
partners, so no separate cockpit rule exists. Decode: [../org/vehicleDamage.md](../org/vehicleDamage.md).

## src/Flight/Airframe/EffectCatalogue.cs
The record of which authored anims are playable effects and what their defs need staged: the name
tables every effect producer must stay inside, static and engine-free. It owns the impact and death
effect names, the crash rig's own def tables and the two surface-indexed def vectors, the
damage-stage and prop-choreography menus, the canopy-hole family a human rig alone binds, the
destroy-def lookup, the collider overlay's surface colour key, and the anchor-root derivation that IS
`WorldEffectsFactory`'s stage source. An unstageable anchor fails the build rather than leaving a def
anchored on nothing. Every name producer carries a producer-range tripwire in `CSVM.Tests` asserting
its whole range resolves inside these tables. Read `Session/World/WorldEffectsFactory.cs` next.

## src/Flight/Airframe/SurfaceDefTable.cs
One of the original's per-surface anim-def vectors (`"player_crash_" + name`, `"ai_crash_" +
name` or `"touchdown_" + name` over every `SurfaceRegistry` slot) plus the cascade that indexes
it with a struck material's numeric surface id (`SceneBuilder.SurfaceIdMeta`). Built once per
bind from a caller-supplied "does this def exist" test, so `PlayableDefs` is what a runtime binds
and `DefForSurfaceId` is what an impact asks. Engine-free and pure; the fallback cascade and its
empty-slot arm carry their prohibition at the type itself. The AI family runs the same cascade
over the same fields with the vehicle's own name as its last resort. Full decode, including the
touchdown family and the weapon `IMPACT` table: `analysis/surface-classification/FINDINGS.md`.

## src/Flight/Airframe/DamageLab.cs
The damage lab that F19 toggles: one armour slider for the parts the data gives an armour pool, one
health slider per destroyable part, and a `PartFrac` reading of health, armour or the combined
scale the mirrored gauge dial is on. `ReadSliders` floors a part's armour once its health reads
short of full, mirroring the real armour-first path, and a `--damage=` preset takes the same route.
One panel with two hosts chosen by the injected `IDamageLabTarget`: one drives `DamageVisuals` on
a parked plane, the other writes the flown plane's real `PlaneDamage` through single-pool `Apply`
calls. Neither host reimplements the visuals. The flag's own behaviour is in [../cli.md](../cli.md).

## src/Flight/Hud/CompassTape.cs
The original's top-centre heading tape, rebuilt from the game's own compass tick and text textures
as a cylindrical drum seen edge-on, headings increasing to the left under a clipped cosine-power
fade holding the bar's inner half flat and its outer quarter near black. Neither end is capped and
the comb stops a hem short of the bar's bottom edge. Metrics are probe-fitted reference constants
times `HudMetrics.Scale`, `Build` returns null where a texture is missing, the octant letters
squeeze with the drum like the ticks, and the control re-anchors on resize. The heading comes from `GaugeCluster`, and `ReadingDeg` is the one nose-vector-to-heading
conversion the gauges and pause chart icons share. Model: [../formats/hud.md](../formats/hud.md).

## src/Flight/Hud/GaugeCluster.cs
The original's cockpit dials as a screen-space HUD: altimeter, speedometer, damage display, the
gun and missile weapon gauges and the nitro dial, all geometry extracted from the plane's own
`gauges` subtree, drawn by data priority and bottom-anchored so splitscreen panes keep them on
screen. `ColumnAnchors` places the two columns: `HudMetrics.ReadingBox`'s edges, handed back to
the borders on a pane narrower than the reference frame. `HeadingDeg` carries the nose heading
`CompassTape` and `CockpitGauges` both read; `DamageZoneColor` bands the damage dial off combined
armour and health against thresholds mined from the data's own `injure_anims`. The sweep and lamp
structs need no `Control`, so `CSVM.Tests` drives them. [../formats/hud.md](../formats/hud.md).

## src/Flight/Hangar/CustomPlaneDef.cs
A custom-built plane as a pure model: exactly the decoded 204-byte record's chosen fields
(airframe, engine, per-zone armour units, four gun slots with their twin bits, per-wing hardpoint
counts, the paint pattern with its colour, shade and decal indices, and the name), and none of the
fields the original derives at commit, which are recomputed rather than stored. Engine-free, so
screens edit it, `HangarEconomy` prices it and `CustomPlaneStore` persists it without a session.
`Ammo` and `Ordnance` carry the campaign loadout export in `OwnedPlane`'s own encoding, written
only by `SetLoadout` and left alone by `Clamp`; `AwaitingExport` is the export gate the plane
pickers read. Record layout: [../formats/paint.md](../formats/paint.md).

## src/Flight/Hangar/CustomPlaneRecord.cs
Import-only reader for the original's 204-byte saved-plane files: one record, or a whole install's
`Planes` directory, into `CustomPlaneDef`s. Every paint field is read as the index it is, the
cached RGBA the engine writes alongside is left out and exposed only for a cross-check, and the
derived fields are ignored. A file it cannot make sense of reads as null rather than throwing, so
an import never breaks on one corrupt save. The read is one way: CSVM's own planes persist through
`CustomPlaneStore` and never in this format. Format: [../formats/paint.md](../formats/paint.md).

## src/Flight/Hangar/CustomPlaneStore.cs
JSON persistence for `CustomPlaneDef`, one file per plane under `user://Planes/`. The store works
over a plain absolute directory through `System.IO` so it unit-tests without an engine, and
`UserPlanes()` is the single Godot touch resolving the scheme. The name is the identity, as in the
original, so saving over an existing name replaces its file. A missing or malformed file reads as
nothing rather than throwing, since a corrupt save must never break a plane picker. The current
schema stores paint as the original's index pairs, the first schema still loads and upgrades on
its next save, and the two optional fields (the exported loadout, the export gate) deliberately did
not raise the version, both being absent from a file that predates them.

## src/Flight/Hangar/CustomPlaneBuild.cs
The join from a saved `CustomPlaneDef` onto the three things a spawn consumes, pure and engine-free
because every input is handed in. `LoadoutFor` builds over the airframe's unmutated stock fit,
turning a calibre row into a weapon the loadout bind resolves and a twin pick into one gun over a
marker pair, and hanging each wing's own pylons outboard-first from that wing's bought count alone,
every one carrying high explosive for the Ammo Selection layer to overwrite. `PaintFor` resolves the
record's three colours and decals under a caller-named pattern; `ArmouredParts` and `DamageFor` put
the bought armour on the damage zones by copy, leaving structure and unnamed zones alone. The decode
is [../org/hangar.md](../org/hangar.md), "Into the mission".

## src/Flight/Hangar/HangarEconomy.cs
The hangar's decoded economy over a `CustomPlaneDef`: the airframe, gun, engine and stock-armour
tables as data, the per-line cost and weight of everything a build carries, the two totals, the
purchase verdict and the display-only star ratings. Pure, so a build's price resolves session-free.
The
verdict covers capacity and engine presence only, because pricing a build never checks funds.
Provenance: [../org/hangar.md](../org/hangar.md), "The economy".

## src/Flight/Hangar/HangarPaintTables.cs
The paint screen's two decoded tables and its decal names, carried as CSVM's own JSON data: the
swatch table of base colours with their shade ramps and reset variants, and the pattern table of
airframe availability masks with the colour and shade defaults a pattern selection copies over.
`Resolve` is the original's own colour resolver, `Available` the availability mask, and `Nearest`
maps a first-schema store file's free triple onto an authored swatch. Engine-free and pure, so a
paint pick stays an index pair rather than free RGB. Decode:
[../formats/paint.md](../formats/paint.md), "The swatch table and the pattern defaults".

## src/Flight/Camera/PlayerRig.cs
One rendered view's state bag: the player index, the camera and its optional `SubViewport`, the
HUD parent, the visual layer, that player's `FlightController`, and the camera-anchored horizon,
deck and whiteout copies, which re-anchor every frame and so need one set per pane. The ambient
cloud field is deliberately not one of them, being world-anchored geometry every pane shares
behind a cull mask. `CameraWeatherState` is a per-rig field rather than a shared one, since
splitscreen panes can sit in different states at the same instant; `Session/WeatherRig.Tick`
writes it each frame.

## src/Flight/Camera/ViewerSet.cs
The "what do the cameras see" registry, session-owned and bound once after the rigs are built, so
every draw rule needing it shares one registration, single player included. `Cameras` hands back
the raw bound list for a consumer that needs each viewer's own field of view and pane height and
already skips a freed instance; `Positions` and `Poses` are the two derived shapes, the latter
filling a caller-owned buffer for a consumer that republishes the set every frame. It carries
cameras, not the screen-size or view-depth arithmetic, which stays in `ScreenSize`. Its consumers
are the tracer floor, the puffer distance fade, the screen wash and the world-light budget.

## src/Flight/Airframe/CollisionLayers.cs
The named physics collision layers, world and aircraft, plus the combined mask. The first and only
place a layer bit is given a meaning; a new layer goes here rather than inline at a collider.

## src/Flight/Airframe/CollisionDamage.cs
The original's collision damage arithmetic as pure statics: the impact severity cosine, the two
terms of the damage pair a contact costs, the camera kick, the entity-versus-entity cut, and the
grace and spawn windows. Being pure, the suites pin the whole table without a world. The severity
carries no airspeed term and the camera kick does; the two laws sit in the same original function
and the prohibition against merging them is on the members. `AircraftContactResolver` is the
caller that turns these numbers into one contact's outcome. Decode:
[../org/flightModel.md](../org/flightModel.md), "Collision damage".

## src/Flight/Airframe/AircraftBody.cs
The flying aircraft's physics body: one `AnimatableBody3D` under `FlightController` carrying one
collision shape per `PlaneCollider` part, reusing the same convex hulls and local transforms the
terrain sweep casts. It rides the controller's transform, maps a query's struck shape back to a
part name, caches the one-entry exclusion list the owner's own queries pass, and drops to no layer
while the plane is out of play. It is also the fuse and blast geometry oracle, answering nearest
hull, a swept segment's closest approach and a bound radius from the same hull set with no physics
query, and scaling a projectile hit's damage by the blast falloff share. A round's strike is offered to
the rig's `HitRouter` as one `AircraftHit` before the local damage runs, so a session can send it instead.

## src/Flight/Airframe/IWorldQuery.cs
The one seam onto the live physics world: a shape swept along a motion for the earliest stop
across a named part list, a single ray, and the standing overlap test the un-embed loop asks.
`FlightController` reads the world only through this and nothing else may reach
`DirectSpaceState`. The reports carry the struck collider as a plain node so a caller builds its
own name. A carried turret reads the same seam for its line of sight, and a synthetic
implementation proves the mask and the blocked and clear cases off-engine with no live node.
`GodotWorldQuery` is the only real adapter.

## src/Flight/Airframe/ContactReport.cs
One detected contact as the value both halves of detection fill: the impact point, the struck
surface normal, which airframe box reached it first, the collider's name, how far along the
frame's motion the airframe stopped, and whether an aeroplane was struck. The airframe sweep fills
it from the world query, and the anti-tunnelling centre ray fills the same shape where there is no
struck box and no surface normal. It holds no node on purpose: the only question the decision side
asks about the struck object is whether it is an aeroplane. Read `ContactOutcome` next.

## src/Flight/Airframe/ContactOutcome.cs
What one contact costs the striking aircraft, as a value with no node and no physics space behind
it: the fate, the decoded damage pair both parties spend, the doom rule's answer, the zone the
ledger actually charged, the pilot HUD's flash line, how far along the normal the caller must move
the striker to un-embed it, the camera kick, and the instruction to damage the struck aircraft
that only the caller can perform because only it holds that rig. One value with no optional parts,
so forgetting half a contact is forgetting one statement. `AircraftContactResolver` fills it.

## src/Flight/Airframe/AircraftContactResolver.cs
The decoded contact rules for one aircraft, holding a world query and no node: the damage pair
both parties spend, the fate (the doom rule, no damage data, health exhausted, an airframe that
cannot un-embed), and the un-embed loop over the seam's overlap test. One call answers one contact
with one `ContactOutcome` the caller performs. The engine effects it interleaves with, because
each result is the next rule's premise, go through `IContactEffects`, which `FlightController`
implements per contact. Every contact it is handed spends the pair; the alternate-step cadence is
the sweep's, never a gate on the spend. `AircraftContactResolverTests` pins the rule table
off-engine against a synthetic world query and a scriptable effects sink.

## src/Flight/Weapons/SweepCadence.cs
The original's alternate-step collision sweep as a pure value with no node: `Advance` answers
whether this sim step sweeps and, after a skipped step, the origin the sweep runs from, so the
carried motion is swept whole; `Respawn` resets the phase. The parity gates the sweep, never the
spend, so a contact the sweep resolves always spends the damage pair. `SweepCadenceTests` drives
it with the real resolver and ledger against a kinematic wall. Decode:
[../org/flightModel.md](../org/flightModel.md), "Collision response".

## src/Flight/Airframe/AircraftLifecycle.cs
The states one aircraft moves between and the rules that move it: in play, crashed, destroyed with
its wreck still flying, inert, and back to spawned. It owns those flags, the collision-grace,
carrier-drop and auto-respawn timers, and the crash-def table with the selection off it, and holds
no node, so the whole table runs in a unit test. Every transition reports what happened instead of
performing it, answering one outcome value carrying everything the caller owes.
`FlightController` forwards the flags and keeps the events the session subscribes to. That a
crashed aircraft cannot crash again is a transition rule here, with the falling wreck's own
landing as the single exception. `RespawnOnFire` (Auto Respawn off) waits on Fire Guns.

## src/Flight/Airframe/GroundShadowLaw.cs
The original's aircraft ground shadow as a rule, engine-free and pure: the projection direction
(straight down, with the player's own skewed along its nose), the horizontal distance fade, the
altitude ramp, the footprint scale, the flattening of one point onto the ground, the colour derived
from the mission's authored `SUNLIGHT` pair, and the spread and ramp the coverage texture is built
through. The texture's size is the one number here that departs from the decode, and the spread
takes the scale between the two sizes so its softening keeps its width on the ground. Its second
overload writes into a caller's buffer, for the per-frame raster.
`CSVM.Tests/GroundShadowLawTests` pins the numbers. Decode: [../org/shadows.md](../org/shadows.md).

## src/Flight/Airframe/GroundShadowSilhouette.cs
One caster's shape: the triangles of the node the original rasterises, taken once, and the 64x64
coverage texture rebuilt from them each frame. The airframe's triangles are held still in the
aircraft's frame; each propeller or rotor blur disc is its own group, re-posed from its live node so
it turns in the shadow without re-reading the model. Pose, flattening and the footprint collapse
into one affine map per group, so a vertex costs two dot products, and the fill is an incremental
edge walk with its bounds hand-inlined, which holds a debug-build raster near a third of a
millisecond per aircraft at that size. The mask is readable as data (`CoveredAt`), which is how the
suites pin a shape no ellipse can satisfy. Decode: [../org/shadows.md](../org/shadows.md).

## src/Flight/Airframe/GroundShadowPass.cs
The drawing half: one modulating quad per live aircraft, rebuilt from the rule each rendered frame,
with the surface height coming from a single downward ray and the roster, the players, the
session's rigs and the authored sunlight read fresh through delegates `GameSession` supplies. The
player's three exemptions belong to the pane whose pilot flies that aeroplane, so an aeroplane a
human flies carries a second quad on that pane's own visual layer. Built in original graphics mode
only. It owns a `GroundShadowSilhouette` per caster and binds its texture to the quad's shader. The
quad is flat where the original modulates the ground's own polygons, which the decode page records.
Read [../org/shadows.md](../org/shadows.md) next.

## src/Flight/Airframe/GodotWorldQuery.cs
The only adapter over Godot's `DirectSpaceState`, implementing `IWorldQuery`. It resolves the
wrapped node's world at each call rather than caching it, since the node may be bound before it
joins the tree, and `Sweep` holds the airframe's whole per-part cast and rest-info dance,
including the small nudge past the first overlap that a rest query coming back empty exactly at
the unsafe fraction requires.
