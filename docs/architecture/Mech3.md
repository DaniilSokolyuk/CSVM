# Mech3

Everything that turns the player's install into a live scene: the mech3ax extraction readers, the GameZ to Godot builders, and the animation runtime that drives the world (including its `Mech3/Anim/` subfolder).

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Mech3/GameZ.cs
Loads a mech3ax GameZ extraction (zip or unpacked dir) into plain C# objects (nodes, models,
materials, textures), reading both the v0.6.1 "legacy" and the fork "unified" shapes. It carries
the fields the builders read: `IntersectSurface`, `Active`, `ZoneId`, `MissionTargetWord`,
`SoilId`, `OverlayPasses`, `ZoneSet`, each model's `Lighting`/`Fog`, and the World node's partition
grid as both a flat `PartitionNodes` and the per-cell `PartitionCellNodes` a `WorldPartitionGrid`
query needs. It also answers what the JSON cannot: `WorldTransformOf`, `IsMarkerGizmo`,
`VertexColorsRestateMaterialColor`, and `MissionStructureTeam`/`MissionSlotOf`/`WorldObjectTeam`.
Fields: [../formats/gamez.md](../formats/gamez.md), [../formats/world-structure.md](../formats/world-structure.md); teams: [../org/targeting.md](../org/targeting.md). Read `SceneBuilder.cs` next.

## src/Mech3/TextureArchive.cs
Texture lookup over an unzbd texture extraction, a zip or an unpacked PNG dir. It absorbs the
stored-name quirks (20-char truncation prefix match, legacy `.-N` renames, the fork's trailing
doubled period) and classifies each texture's alpha twice, for two unrelated readers:
`LastHadAlpha`/`LastAlphaIsSoft`, off the decoded pixels and the named soft-alpha families (`IsNamedSoftAlpha`), which scissor-versus-blend keys on, and `LastAlphaClass` from the extractor's
manifest, the one reader that sees the `Simple` textures. That manifest also supplies
`RenderFlags`, whose bit 2 (`IsAdditive`) is the whole sprite-blend rule.
`Build` is the one construction path (decode, classify, drop-in, mip chain), `Find` caches it, `BuildMipped` hands it to `--dump-mips` un-cached, and `MipBias` reads the chapter's authored LOD
bias for `Launcher`. [../org/textures.md](../org/textures.md), [../org/vertexLighting.md](../org/vertexLighting.md), [../formats/gamez.md](../formats/gamez.md).

## src/Mech3/SceneBuilder.cs
Shared GameZ-subtree to MeshInstance3D builder: triangulation, material and mesh caches,
nearest-LOD only, a skip predicate. It replicates the original's draw order as depth bias (priority,
subface, overlay pass, within-mesh surface rank, then `NodeBiasOf`'s cross-node tie-break off the
world's `ConflictRank` map), applies CLAMP per surface off `UvsWithinUnitSquare` rather than blanket,
and colours a surface `vertex colour x material` except where the two restate each other.
`BuildSubtree`'s `zoneGate` flag moves each instance onto its `zone_id` visual layer
(`ZoneGate.cs`), off for the camera-anchored deck and dome. `CollidersForMesh` splits a mesh into one
trimesh per surface class and soil and by sidedness, both halves on one body registering with
`WorldCollision`; `MissionStructureTeamMeta` is the channel `DestructibleRegistry` reads a pool's
team through. A textured surface takes `csky_world_light` on its `lighting` flag alone, and every
mip-mapped arm fetches through `SampleAlbedo`, the one `csky_sample_albedo` carrying the chapter's
LOD bias, reused by `Clutter` and `MeshLab`. `AlphaOf` reads back the transparency verdict a built material's shader was generated for, the registry `HiddenAlpha` (`--hide-alpha`) drops a class by, dropping the surface rather than the instance so the classes isolate from each other. A `sunVertexLit` builder (the in-flight aircraft) draws original mode's shaded arm unshaded, with the original's per-vertex sun term, whose ambient half is the Danger Zone photograph's fill at an armed `PhotoEyeParam` eye. Arms and selection: [Root.md](Root.md), [../formats/gotchas.md](../formats/gotchas.md), [../org/vertexLighting.md](../org/vertexLighting.md), [../org/textures.md](../org/textures.md).

## src/Mech3/ZoneGate.cs
The original's per-node visibility gate (`FUN_0056c430`). `FUN_004d62d0` arms the camera each frame
with the zone set `{0, camera weather state}`; the walk draws a node iff its gamez `zone_id` is
`-1`, or is in that set. Four members: `Draws(zoneId, state)` (the rule), `LayerFor(zoneId)` (the
visual layer a gated zone's meshes are MOVED onto, 0 for `-1`/`0`, i.e. leave on the default
layer), `CullMask(mask, state)` (narrows the band to one zone, every other bit untouched) and
`OpenCullMask(mask)` (the whole band back, for `--no-zone-cull` and the launcher camera's per-session
reset). Constraints and evidence live on the class itself; see `CSVM.Tests/ZoneGateTests.cs`.

## src/Mech3/ConflictRank.cs
The world's cross-node draw-order tie-break. Buckets every built triangle by its world plane,
finds the cross-node pairs that are coplanar, same-priority, same-subface and genuinely overlap
(clipped area > 1 m², never an AABB touch), and layers that DAG by longest path, so a node's rank
counts conflicting layers beneath it, not nodes before it. Every edge runs low node index → high,
so the layering is a topological order of the original's own draw order and cannot invert authored
layering. `WorldBuilder.RankConflicts` runs it before the build; 11–45 ms per chapter.

## src/Mech3/WorldCollision.cs
Owns every `SceneBuilder`-built collider's `Disabled` flag and derives it: enabled exactly while the
owning node is visible in the scene tree and no ancestor is faded out. `Track` binds that to the
node's `VisibilityChanged` plus `TreeEntered`, since a world is assembled and bootstrapped while
still detached; `SetFaded` is the second input, an `OBJECT_OPACITY_*` fade being a shader parameter
visibility knows nothing about. `OwnerOf` reads the relation backwards, naming the world object a
collider body stands for: the per-surface-class bodies `SceneBuilder` carved from one mesh node
answer their shared parent (`SurfaceIdMeta`) and anything else answers itself, which is what a
caller counting objects rather than bodies keys on. Read `SceneBuilder.cs` next.

## src/Mech3/CraterShape.cs
One crater as geometry: a 7-vertex rim ring laid at the impact at radius 20, a mid ring halfway in
and one `DEPTH` further down, and an apex two `DEPTH`s under the impact. Every crater in the shipped
game is this one shape, because all six `CRATER` weapons author the block bare and leave the engine
template's randomisation spans at zero. `Footprint` is the XZ box the no-overlap rule compares and
`Clears` is that rule, the recorded box grown by `Clearance` on all four sides. `Covers` is the
decoration census, an XZ disc with no height test. Decode: [../org/craters.md](../org/craters.md).

## src/Mech3/CraterField.cs
Every crater one mission has carved and the rule that decides whether it may carve another.
`Request` refuses a footprint within the clearance of a carved one (`Refused`), finds the world node
under the body the round struck, hands the carve to `TerrainCarve` and the decorations to
`ClutterCull`, and returns which of the original's outcomes happened. A crater is permanent, nothing
ages one out, so what bounds a mission's count is the refusal alone. `TryCarve` is the sink shape
`ProjectilePool` holds, true only when the carve landed. The pool asks it only for a collider
stamped `SceneBuilder.CanModifyMeta`, which no shipped node carries, so a played round never reaches
it and only a direct caller (the `crater-carve` suite, the hand-armed carve option) carves.

## src/Mech3/TerrainCarve.cs
The mesh and collider surgery one carve performs on one world node. The ring is subtracted from
every up-facing triangle it covers by incremental half-plane clipping, so the hole is the ring's own
edges and the bowl shares them with no crack; the pieces keep their height, normal, colour and UV by
barycentric weights. The bowl is emitted as one further surface in the same skin, wound from the
struck mesh's own convention. Both the `ArrayMesh` and the struck trimesh are replaced by PRIVATE
copies, un-shared from `SceneBuilder`'s per-mesh-index caches, so a carve never reaches the other
instances of the same model. Read `SceneBuilder.cs` for those caches.

## src/Mech3/ClutterCull.cs
Counts and destroys the decorations a crater swallows. A decoration dies outright, with no health
test, no animation and no model swap, because the original's crater path reads no template field at
all. `ClutterBuilder` bakes every placement of one kind into one MultiMesh, so dying means the
instance's basis collapses to zero (the draw call and its custom data stay intact) and its shared
collision shape is switched off on the region body it was attached to by RID. Read `Clutter.cs`.

## src/Mech3/PlaneBuilder.cs
Builds one aircraft from its GameZ subtree, skipping the cockpit, damage, destroyed and shadow
subtrees and the airframe's `*_hook` skyhook group unless `dockingHook` asks for it. `Repaint`
re-liveries the built plane in place, `BuildDestroyed` builds the wreck subtree with the
plane-root to destroyed transform chain baked in, and `WingFlares`, `DamagePanels` and
`CockpitDamagePanels` expose the nodes the build collected. A caller building a second aeroplane of one airframe and livery passes the
`painter` the first composed, so those skins are composed once (PERF-22). `spinningProps` selects the flight propeller set
(`PropParts.cs`); `Build` also reads `CockpitCameraOffset` off the `cockpit_camera` marker, which the flight camera mounts at with the decoded `HeadPitchOffsetRad` tilt this file also owns. `cockpitInterior`
mounts `cockpit1` hidden at that offset under `InteriorScale` and that same tilt, then `ParkInteriorStates` walks it, darkening exactly what `reset_bulletholes` does (the `bulNx` hole quads, never the meshless `bulletN` groups over them) plus the two warning lamps. Camera decode: [../org/cameraViews.md](../org/cameraViews.md).

## src/Mech3/PaintScheme.cs
One aircraft livery: the pattern name, three colours and three decal indices of the `paint_*`
record a `vehicle.json` def carries. `LoadCatalog` keeps one scheme per pattern name (the 12
shipped patterns); `Random` draws a plausible livery when none is given. Read `PlanePainter.cs`
next. Authored side: [../formats/paint.md](../formats/paint.md); decode: [../org/paint.md](../org/paint.md).

## src/Mech3/PatternLibrary.cs
Decodes the original's `.BM` paint patterns from `extracted/rof/ASSETS/GRAPHICS/<PATTERN>/`, which
the extraction produces. `PatternsFor(prefix)` lists the patterns shipping skins for one
aircraft, a pattern being per plane; `Skin()` caches per (pattern, skin) so several aircraft in one
session share a decode. `.BM` layout: [../formats/rof.md](../formats/rof.md).

## src/Mech3/PlanePainter.cs
Applies a `PaintScheme` to one aircraft: composites its skins from the pattern's region masks and
swaps the three decal placeholders. `SkinNameFor` decides which `.BM` a model texture is painted
from, the original pairing the two by table rather than by name. The composite formula, the
shading-plane choice and the bottom-up `.BM` rows are decode, and belong to
[../formats/paint.md](../formats/paint.md),
[../formats/rof.md](../formats/rof.md) and [../org/paint.md](../org/paint.md). Read those before changing a composite step.

## src/Mech3/MilitiaPaint.cs
Each militia's paint pattern, keyed by the militia half of a vehicle's display name, which is all a
militia decides on the Instant Action path: the original builds every wingman, ace and wave member
from the plain AI def of its aircraft and writes the setup screen's pattern, decals and colours over
it, so no militia ever selects a vehicle def there. The pattern is read off whichever def of that
militia names one, since every def of a militia wears the same one. `PatternForWave` answers for a
single wave's `enemy_name`, or null where that militia names no pattern. Read `PaintScheme.cs`
next. Decode: [../formats/instant-action.md](../formats/instant-action.md), [../formats/paint.md](../formats/paint.md).

## src/Mech3/PropParts.cs
Classifies a plane's propeller and rotor subnodes by name (`staticpropN`/`staticrotorN`,
`nitropropN`, `propN`/`propNb`, `rotorN`/`rotorNb`) and supplies each spinning kind's local axis
and rate: the `XYZ_ROTATION` values from `plane_props.json` (`spinprops`) and `autogyro.json`
(`agyro_rotors`), props spinning about local Z and rotors about local Y. Units and keys:
[../formats/anim-definitions.md](../formats/anim-definitions.md). `PlaneBuilder.cs` is the one caller.

## src/Mech3/ControlSurfaces.cs
Classifies a plane's control-surface mesh nodes and their hinge axes: the deflecting node
(`l/r_aileronN`, `l/r_elevatorN`, `l/r_rudderN`, the Fury's `l/r_rudder_rotate`) hangs under a
hinge parent group whose transform places and orients the hinge line, ailerons and elevators
hinging about local X and rudders about local Y. The name patterns are the original's own six
`sprintf` node lists, which is why the elevators classify per side: they carry a differential roll
term, so left and right settle at different angles. Decode: [../org/flightModel.md](../org/flightModel.md), "The original's control-surface animation".

## src/Mech3/WingLights.cs
Single source of truth for wingtip nav lights: the flare-node predicate (wing_flare1/2), the glow
texture (oil_liteflare), the warm-amber flash colour (0.88, 0.78, 0.36 = wing_light.json's
LIGHT_STATE COLOR), the blink period (1.5 s = its LOOP SEQUENCE_OFFSET) and the point-light range
(0.5-1.25 m, also LIGHT_STATE). PlaneBuilder hides and re-skins the flares (additive tint, one-sided
as authored, no billboard); WingLightBlinker flashes them and emits a matching OmniLight3D per side.

## src/Mech3/WorldBuilder.cs
Builds a chapter world (fullbright): the World node's children plus every partition-referenced
subtree, skipping `horizon` (`BuildHorizon` builds the camera-anchored skydome separately),
`fvol*` (`IsFogVolumeNode`, the one list `FogVolumeSpec.VolumesOf` also reads, so the skipped set
and the cloud-scatter set cannot drift apart) and `dzpaths`. Every node the walk builds is stamped
with its own `zone_id` visual layer (`SceneBuilder`, `zoneGate: true`), so the camera's weather
state culls it; the cloud deck and the dome are the two exceptions, camera-anchored per rig and
taking the zone rule through `Session/WeatherRig.Tick` instead.
`HideUnplacedEntities`/`RestorePlacedEntities` switch off, then restore, the entities a chapter
parks at the world origin awaiting mission placement; `NoCollisionNode` exempts sky, cloud and
billboard geometry and anything authoring `intersect_surface: false` from colliders. The static
helpers (`HorizonZonesOf`, `CloudDeckAltitudeOf`, `DomeZonesToBuild`, `DetachedWorldAabb`,
`FogVolumeZoneIdOf`, `MatchNodes`) are pure and test off engine. Deck and dome: [../formats/weather.md](../formats/weather.md), [../org/weather.md](../org/weather.md).

## src/Mech3/MapEdgeExtender.cs
A rolling window of repeated border tiles and clutter continuing the world past the map edge, one
window per session shared by every player camera and diffed only on a cell crossing. Clutter copies
grow from `ClutterBuilder.ExportedKinds`, each keeping its source stamp's fade thresholds.
`ClassifyGroundMesh`, `IsCompletionStrip` and `FoldAxis` are pure statics pinned by
`MapEdgeTileTests`/`MapEdgeFoldTests`; `--dump-tilegrid` writes the per-cell acceptance census
`WriteCensus` builds. The original's own continuation behaviour and the per-chapter fold
measurements: [../formats/world-structure.md](../formats/world-structure.md). Read `Clutter.cs` next.

## src/Mech3/Clutter.cs
Stamps the boot-script clutter templates across placed polygons carrying the template's ground texture,
one stamp per integer UV repeat of the polygon's UV lattice: sprites become one fullbright billboard
MultiMesh per kind, turned toward the camera as that kind's own `FacadeMode` says, solids go through
`SceneBuilder.SharedMesh`, `ClassifyBillboard` the split. A card blends or scissors on the archive's own alpha verdict, one shader variant each, and the blended variant writes its body's depth through a prepass: a kind is one MultiMesh, a single draw in buffer order that sorts nothing, so a card writing no depth at all is painted over by every later card and kind whatever their distances. `BlendCardKinds`/`ScissorCardKinds` count the kinds each way, the census an `--hide-alpha` isolation is read against, and a hidden class builds no MultiMesh for its kinds. Every stamp carries its far fade as MultiMesh custom
data under `EffectsLevel`, and samples through `SceneBuilder.SampleAlbedo` for the chapter's mip bias. `TemplateNames` reads `AddClutterTemplates` unfiltered, the per-polygon `no_clutter` gate deciding
which patch a district dresses; `OverrideTemplateNames` is `--clutter-templates=`'s replacement.
A decoration is a node chain, and `FirstWithMesh` hands back the translation down to the node carrying the mesh, so a stamp lands where the chain puts the drawn card: C5's lamp glow rides 4.75 m up its post. A solid decoration's chain carries SEVERAL meshes, which `ExtraMeshes` collects (nearest LOD only, each in the drawn mesh's frame) so `Kind.ExtraParts` draws and collides the whole building: 12 of C5's city blocks hold two street walls and a roof cap on further nodes, and drawing the first mesh alone leaves them open on two sides.
Placement runtime: [../org/clutter.md](../org/clutter.md); authored side: [../formats/clutter.md](../formats/clutter.md), [../formats/templates.md](../formats/templates.md).

## src/Mech3/ClutterTemplates.cs
The chapter's `templates.zrd` (`ClutterTemplateSpec.Load`/`.Parse`): one `ClutterKindProps` per
clutter DECORATION MODEL, carrying `substitute`'s weighted roll, `scale_range`, `far_fade_range`
and the jitter, rotation, slope and damage keys the retail data leaves at their defaults. Static
over a reader list, so all eight chapters pin off engine (`CSVM.Tests/ClutterTemplatesTests.cs`).
`ClutterBuilder` consumes `substitute`, `scale_range` and `far_fade_range`, the last through
`FadeThresholds`, the pure per-stamp `(near squared, far squared, reciprocal)` the fade shaders
read. Schema, offsets, the per-chapter census and the keying rules: [../formats/templates.md](../formats/templates.md). Read `Clutter.cs` next.

## src/Mech3/Zrdr.cs
Zrdr extraction reader (zip or unpacked dir): `LoadFile`, `LoadFileOrEmpty`, content-sniffing
`LoadMatchingFiles`, name-predicate `LoadFilesNamed` (for families with nothing to sniff, e.g. the
`ne0*` nets), and `ZrdrDict`, the key/[values…] view over a reader's alternating list.

## src/Mech3/GamePath.cs
Splits a path the game's data names (`..\data\c1\m02\zrdr\cutscenes\cabpickup.zrd`) on `\` and `/`
alike on every host: `FileName` for the leaf, `HasFolder` for a folder segment. `System.IO.Path`
splits `\` on Windows only, so it keeps our own disk paths and never a game one. Callers:
`MissionCutscenes` (the `cutscenes\` entries), `AnimProgram` (the scope gates' stems).

## src/Mech3/LandingApproaches.cs
A chapter's `landings.zrd` approach table resolved against the gamez: each row names an animation
and a world node, and that node carries a `cone`, `half_cone` or `sphere` child whose single
authored triangle IS the condition volume, plus a `land_on` child a mission definition activates to
arm the row. `Resolve` drops a row whose animation the mission does not carry, which is the
original's own load-time rejection and why an Instant Action mission arms none of them. Each
resolved row holds its volume in the approach node's own frame, its attitude cone and its speed
band, and the geodesic attitude test beside them; the geometry is engine-free and
`Session/Campaign/LandingApproachRuntime.cs` flies a player against it. Decode: [../formats/anim-definitions/cutscenes.md](../formats/anim-definitions/cutscenes.md).

## src/Mech3/Pickups.cs
A mission's compact `pickups.zrd` sensor table as `PickupSpec` (node name, radius in metres): the
spheres `Session/Campaign/LadderSwitchRuntime.cs` tests the player against every frame. An absent or
unreadable file resolves to an empty list rather than throwing. Nothing here starts the pickup
timing; the train's own `train_on_track` definition calls `pickup_timing` at mission load. Decode:
[../org/ladderSwitch.md](../org/ladderSwitch.md).

## src/Mech3/SurfaceRegistry.cs
`Names[id]`: the id→name table a struck material's `soil` field indexes into, so a caller can build
`"player_crash_" + name` / `"touchdown_" + name` and resolve the same def the original resolves.
`IdForName` is the parse-time direction (a name the registry does not carry answers null and is
discarded, as `FUN_005ad630` discards it), which is how a weapon's `IMPACT` blocks land at their id;
`Default`/`Water`/`Player`/`Enemy`/`Buildings` name the five slots code branches on outright.
Ids 0–5 are compiled into `crimson.exe`; ids 6–13 are the `LoadSoils`-loaded list from `ZBD/zrdr.zbd`
`0xe631c`, reproduced by `analysis/surface-classification/soils_list.py`.

## src/Mech3/AiNets.cs
The chapter patrol-net reader ([../formats/ai-nets.md](../formats/ai-nets.md)): every `ne0NNNNN.zrd.json` in a chapter
zrdr scope joined with its `neindex.zrd.json` name: nodes, the explicit edge list, raw per-node
tags, the trailer attach target, and the net's own three volumes (`Volumes`, record elements 2–10
as an `AiVolumeSet`). Plus the lookups both ways the data references nets:
`ById` (aiv field 0), `ByName` (egen/zeppelins/objectives, case-insensitive), `Resolve` (either
spelling), and `ChapterFirst` (the net an Instant Action actor is given). Consumers:
`UI/Overlays/AiNetsOverlay.cs` and `Flight/Ai/AiNetFollower.cs`. Golden counts asserted in
`CSVM.Tests/AiNetsTests.cs`.

## src/Mech3/Maneuvers.cs
The shared maneuver-library reader ([../formats/ai-rosters.md](../formats/ai-rosters.md)): `zrdr/maneuvers.zrd`'s 17
entries as `Maneuver` (name, `natural_touch` difficulty, timed attitude steps, the
autogyro/relative/nitro/bias flags), plus the selection cull (`EligibleFor`: difficulty ≤ the
pilot's 1–9 `natural_touch`, with no interpolation table, the stat having no `ai_skill_parameters`
entry by design) and the roster `signature_maneuvers` bitmask decode (`SignatureNames`, over
`ExeTableOrder`). Consumers: `Flight/Ai/ManeuverExecutor.cs`; goldens in `ManeuversTests`.

## src/Mech3/EnemyGenerators.cs
The mission `egen.zrd.json` reader ([../formats/mission-entities.md](../formats/mission-entities.md)): the enemy generators that
feed AI aircraft into a live mission, typed as `EnemyGeneratorDef` in the three shipped shapes
(zeppelin launch 17, plain spawner 5, moving spawner 1); a `[null]` file reads as an empty list.
An unauthored door pair takes the loader's node-name default (`DefaultDoorAnim`, `%.5s_open%.2s`),
the close being the open name, as the original's loader has it. Consumed by
`Session/AiGeneratorRuntime`; golden counts in `CSVM.Tests/EnemyGeneratorsTests.cs`.

## src/Mech3/Zeppelins.cs
The mission `zeppelins.zrd.json` reader ([../formats/mission-entities.md](../formats/mission-entities.md)): the 58 zeppelin
instances typed as `ZeppelinDef`: motion limits, net name, targets, healthy zones plus
`num_healthy_required` (defaulted to 1 and clamped to the healthy count, the decoded load
rule), engines, gasbags, cannons and `cannon_health`. Motion keys feed
`Flight/Airframe/ZeppelinMotion` (F17); the damage half feeds `Flight/Airframe/ZeppelinDamage` +
`Session/ZeppelinRuntime.WireDamage` (F18). Fixture units + install goldens
in `CSVM.Tests/ZeppelinsTests.cs`.

## src/Mech3/InstantAction.cs
`InstantActionDef` plus the three producers that converge on it: `Load` for a chapter's shipped
`ia.zrd.json`, `LoadFromJson` for a hand-authored `--ia=<path>` object, and `BuildFromWizard` for
the launchscreen's Instant Action wizard. The first two funnel through one private
`BuildDef(ZrdrDict)`, `LoadFromJson` doing nothing but mapping a JSON object onto the same
key/[values] shape `ZrdrDict` already wraps, so a hand-authored mission parses through exactly the
path a real chapter's does; the wizard overlays only the fields a pilot can configure onto the
chosen environment's own `Load` result. `spawn_points` and `dzones` stay in `Flight/Modes/SpawnPoints`
and `Flight/Modes/StuntMission`, and the wingmen's fit rides beside the def (`SessionSpec.IaWingmanLoadout`) since it is a flight type. Every optional key's default: [../formats/instant-action.md](../formats/instant-action.md).

## src/Mech3/AiSkills.cs
The AI pilot-skill constants from `player.json`: the `ai_skill_parameters` block as
`[value@1, value@9]` endpoint pairs indexed by the 1 to 9 rating (`At`), plus the accessors that
read one aiv roster block's slots, every one defensive over a short block. `RosterSkills` is the
skill vector by stat name; `RosterPrimaryTarget`, `RosterRatingBiases`, `RosterAce`,
`RosterInitHealth`, `RosterArmor`, `RosterObjectiveTarget` and `RosterHelpLabel` are the named
single-slot reads, and the spawn-facing `Roster*` readers cover the rest. `LoadRoster` is the thin
per-mission loader; `LoadGeneratorRoster` adds the positional-header join exposing disabled
generator blocks. Slot numbers and meanings: [../formats/ai-rosters.md](../formats/ai-rosters.md).

## src/Mech3/AiVolumes.cs
`AiVolume` (radius, upper, lower) and `AiVolumeSet` (activation, attack, return): the one shape
both authors of an AI's range volumes are read into, a roster block's twelve slots 8–19
(`FromRosterSlots`, three named per volume plus a flag no block authors) and a net record's nine
floats at elements 2–10 (`FromNetRecord`). `Overlaid` is the engine's per-field non-zero test, so
`net.Overlaid(block)` is the decoded order ([../org/aiPilot.md](../org/aiPilot.md), "Net assignment"); the
`min_ai_active_dist` floor is `Session/Campaign/CampaignRoster.cs`'s `ApplyVolumes`. The altitude bands are
read and carried but have no consumer: `Flight/Ai/AiModeMachine.cs` gates on radii alone.

## src/Mech3/RosterMarkers.cs
Grafts a roster block's authored marker scaffolding onto the rig its spawn built. A chapter's own
copy of a vehicle is a library root the world never places, so whatever that copy adds under
`markers` past the shared airframe's is built here, hung under the airframe's mark of the same
name, switched to its authored `active` bit and indexed on the world runtime. That is what gives an
index-addressed definition a node to write and a condition volume that moves with its aircraft.
`Attach` also makes the rig answer for the library root's own name and index
(`AnimRuntime.IndexSpawnedVehicle`), so a definition posed `AT_NODE` the vehicle reaches the
aeroplane the mission actually spawned. Read `LandingApproaches.cs` next.

## src/Mech3/VehicleDefs.cs
The `vehicle.json` def table as an index, next to `Flight/Airframe/PlaneStats.cs`'s full read of one def:
`DefForBlock` strips a block name's trailing `_N` ordinals until a def matches, `ModeOf` walks
`kind_of` to the nearest authored `mode` (`jet` at the root, the engine's zero default),
`AirframeFor` finds the player airframe node an AI def's model is built from (the `p`-prefixed twin
of the nearest ancestor, else of the chain's `nodename`), `BaseDefForPlayerNode` is its inverse and
`DerivesFrom` is the variant test `PlaneStats.LoadForAi` enforces. `StartAnimsOf`, `InjureAnimsOf`,
`WeaponsOf` and `AttackOf` return the nearest authored value up that chain, the last two arming a hull with its def's own gun (an `AiWeaponSlot`, defined here) and giving it the radius its scorer admits candidates inside, `DefaultAttackRadiusM` the decoded 400 m both surface defs fall to for want of an authored `attack` ([../org/aiPilot.md](../org/aiPilot.md)).
Pure over `FromRoot`, pinned in `CampaignRosterPlanTests`.

## src/Mech3/FogVolumes.cs
The chapter's `fogvol.zrd` (`FogVolumeSpec.Load`/`Parse`) plus `VolumesOf`, the gamez census of
`fvol*` volumes: the two authored halves of the ambient cloud field `Effects/FogVolumeClutter`
renders. `FogVolumeBox` carries the authored shape twice: as face planes, so `SignedDistance`
walks the real geometry, and as `FogVolumeFace` polygons (every mesh face not flagged
`no_clutter`), the surfaces the scatter lays its lattice on and takes each sprite's normal from.
`FogVolumeWhiteout` is the in-volume whiteout rule C5 alone arms, `Session/WeatherRig.Tick` its
one consumer; `FindMapSpanningSlab` the test for a chapter's map-edge-continuation slab. Both
halves pin off engine for all eight chapters (`CSVM.Tests/FogVolumeTests.cs`); schema in [../formats/fogvol.md](../formats/fogvol.md).

## src/Mech3/Messages.cs
The game's localized string table: plain `System.Text.Json` over the extracted `messages.json`
(NOT a zrdr reader), a case-insensitive key→value map resolving the `MSG_*` keys missions reference.
`Fill`/`Format` substitute a template's `%1`…`%9` placeholders (the HUD strings' format). `Parse`
takes the JSON itself, so a unit with no file on disk reads the same rows; it is internal, since
every shipped caller holds a path.

## src/Mech3/UiStrings.cs
The original's UI string table by id, read from `extracted/rof/ui_strings.json`. Only the `langui`
rows are kept, since ids repeat across the file's two merged tables and every menu range this
remake reads sits in that one. `FormatMessage` positional placeholders (`%1!d!`) convert to
composite format rather than going to printf, and a leading `[FONTID]` tag is stripped as a
renderer directive rather than text; `Face` answers the tag a row carries, from its `font` field or
that stripped prefix, for a screen that draws the row in its own face. `Parse` takes the JSON itself, so the table and its formatting
test off engine; `Empty` is the fallback that lets a menu draw on a missing extraction. Ids and
rows: [../formats/strings.md](../formats/strings.md).

## src/Mech3/TgaImage.cs
The engine-free TGA decoder behind the hangar's art under `extracted/rof/ASSETS/GRAPHICS`, and the
decoded-image type the whole menu art seam is written against: types 2 and 10 (RLE) truecolour at
24 or 32 bits, both row orders, to top-down RGBA8. Anything else, or a malformed or absent file,
decodes as null rather than throwing, because menu art is optional by design and a bad file must
not take a screen down. `FromRgba` wraps an already-decoded buffer as one of these, which is how
`PngImage.cs` reaches the same art path. Read `ArtImage.cs` next.

## src/Mech3/PngImage.cs
The engine-free PNG decoder behind menu art, returning a `TgaImage` so both decoders feed one seam:
8 bits per channel, non-interlaced, truecolour with (colour type 6) or without (type 2) alpha, all
five row filters, and the `gAMA` transfer curve normalized to the UI baseline. `TryReadGamma` is
the metadata half the runtime board renderer needs while retaining Godot's native loader. A palette,
16-bit channel, Adam7 file or malformed input decodes as null rather than throwing. Read `ArtImage.cs` next.

## src/Mech3/ArtImage.cs
The one door menu art is loaded through: a path in, a decoded `TgaImage` or null out, with the
decoder picked from the extension (`.PNG` to `PngImage`, `.TGA` to `TgaImage`). It exists so a
screen names the file the extraction ships and stops caring what format it is. An extension no
decoder here covers returns null, and so does a file the decoder it has cannot read; null is the
correct answer, since a stand-in picture on a fidelity screen reads as a verdict about the
original. The JPEG pictures a screen wants are board pictures, read through the shell's own loader
in `UI/Boards/ComposedBoardView.cs`, which is why no JPEG decoder belongs here.

## src/Mech3/MarkerRig.cs
A player airframe's weapon marker rig read from the planes.zbd GameZ: `Extract` walks a `player_*`
root, accumulating locals down to each `firepoint*`/`pylon*`/`target`, and reports plane-frame
positions plus co-located groups (two gun groups on one mount). `Format` prints one dump block per
plane and `PlayerAirframes` is the model to display list; together they are the instrument
[../formats/markers.md](../formats/markers.md) regenerates from and the source `--dump-markers` and
`UI/Overlays/MarkerOverlay.cs` share. `FindNamedMarker` is the sibling read for one non-weapon node by name,
skipping the alternate-state subtrees so a plane whose interior or wreck carries a same-named node
still resolves to the authored one in the top-level `markers` group.

## src/Mech3/CompiledAnim.cs
Reader for the fork's compiled `cam_anim`/`mis_anim` extraction (zip or dir): typed definitions,
events, and the SI-script pool, where `Script(index)` parses lazily in `metadata.json` order. The
`unknown_seq` destruction slot parses into `AnimDefinition.DeathSlot`, deliberately off `Sequences`
so bootstrap and the sequence-walking derivations never see it and only
`AnimRuntime.RunDeathSequence` dispatches it. The `ACTIVATION_PREREQUISITE` node-state form parses
into `AnimDefinition.PrereqNodes` beside the anim-list form's `PrereqAnims`, and `AnimDefs.cs`
reads the reader spelling into the same list. Decode, including the `active_raw` bit a leaf's state
is read from: [../formats/anim-definitions.md](../formats/anim-definitions.md), [../formats/destructibles.md](../formats/destructibles.md).

## src/Mech3/AnimDefs.cs
The zrdr front-end: ANIMATION_DEFINITIONS reader files normalized into CompiledAnim's
`AnimDefinition` model (op key SNAKE_CASE→PascalCase IS the compiled tag; unclaimed bodies stay
under `raw`). Exists because compiled archives are incomplete: `zepstate`/`startanims` are reader-only.
Unit normalization happens HERE so handlers see one convention: reader rotations are DEGREES
(ROTATE_STATE, FROM_TO rotate, XYZ_ROTATION → radians), PLAYER_RANGE metres (→ m²), ANIMATION_LOD
tokens (to numbers). See [../formats/anim-definitions.md](../formats/anim-definitions.md).

## src/Mech3/AnimProgram.cs
Merges the compiled and reader front-ends for one mission plus `StartAnims`; `ScriptFor` resolves
an event slot to its archive SI script. Two gates run first, both only with a compiled mission
manifest present so a reader-only extraction is untouched: the mission-scope gate against that
manifest, and the shared-scope FILE gate (`ListedSharedFiles`, reported as `SharedFilesSkipped`).
Whatever survives is deduplicated on the (`NAME`, `ANIMATION_NAME`) pair in `Add`, compiled winning
because the archives load first, and the winner takes over the twin's reader-only `PERSIST_LOG`.
Which world ENTITIES a mission shows is `MissionSetup.cs` plus the interp boot script. Scope rules:
[../formats/anim-definitions.md](../formats/anim-definitions.md), "Mission library scope".

## src/Mech3/TextureCycler.cs
Runs the gamez material `cycle` flipbooks (water, surf, wakes, crowds) by swapping `albedo_tex`;
frames resolve at build time while the TextureArchive is open, so an incomplete flipbook stays static.

## src/Mech3/EffectCycles.cs
The `EFFECTS` block of the shared `effects.zrd`: the second source of material flipbooks, and the one
that lights C1's refinery vent. An entry names a node but animates that node's MATERIAL, so this pass
resolves each entry (node → first mesh under it → surface 0's material) and writes the frame list
onto that `GameZMaterial` before the world build, leaving `SceneBuilder.RegisterCycle` to pick it up
unchanged. Two entries exist install-wide (`fire1.flt` 12@10, `fire2.flt` 6@5).

## src/Mech3/WorldSounds.cs
`SOUND_NODE` ambient looping 3D emitters: one pooled `AudioStreamPlayer3D` per live emitter,
following its host's pose each frame. `PlayOneShot(name, worldPos, rng)` is the one-shot
`SOUND` half, fire-and-forget destruction and impact audio on Effects, resolving a `SOUND_GROUPS`
name to a member first. Radio lines, combat voice included, never come here: they are flat and
belong to `MissionRadio.cs`. `HasStream` answers availability after the prewarm, `OneShotsStarted` that a cue fired. `LateBy`, set only during a guest's catch-up, starts a one-shot that far in and skips one already over. Who hears
an emitter is `UI/Boards/SplitScreen.cs`'s per-pane model, fed by `SetListeners`: `Tick` measures to the
nearest and levels every player from `SoundFalloff.cs`, never Godot's. Next: `SoundFalloff.cs`.

## src/Mech3/SoundFalloff.cs
The original's positional gain law, engine-free: what a listener distance, a definition's `RANGE`
pair and its `VOLUME` come to in decibels. `AttenuationDb` is the distance term alone, `VolumeDb`
the linear-gain conversion (ten decibels per doubling, not `20 log10`), `GainDb` the sum with the
silence floor. No Godot attenuation model expresses the shape, since the ramp is measured from the
full-volume radius. `SessionGainDb` is what the play paths call: the law at `RangeScale`, the one
piece of state here, shipping at the remake-only `ShippedRangeScale` (`--sound-range-scale=`). The
decode and its addresses are [../formats/sounds.md](../formats/sounds.md). Callers: `WorldSounds`,
`Flight/Audio/GunVoice.cs`, `Flight/Ai/AiWeaponAudio.cs`.

## src/Mech3/WorldLights.cs
Packs the `LIGHT_STATE` point lights, each as colour times ambient + diffuse, into the 2xN texture
(`csky_light_data`, `csky_light_count`) the original-mode world and aircraft shaders read as the
per-vertex point term ([../org/vertexLighting.md](../org/vertexLighting.md)). `Commit`'s distance
fade and `MaxActive` significance rank answer to the NEAREST viewer handed in
(`AnimRuntime.LightViewerPositions`, from `GameSession`'s `ViewerSet`). The world runtime owns the
frame (`Begin`/`Add`/`Commit`); the world-effects runtime contributes through `AddSource`, so a
burst ranks against the beacons in one set. With a parent `Node3D` in enhanced mode, `Commit`
mirrors the committed set onto pooled `OmniLight3D` nodes ([Root.md](Root.md)).

## src/Mech3/MissionSetup.cs
Parses + applies the per-mission `.gw` interp script that decides which world entities a mission
shows; acts on `NodeSetActive`/`DeleteTree`/`Object3DSetScroll`/`Object3DTranslate`/`Object3DRotate`/
`WorldPartitionSetActive`, counts + reports every other verb. The area verb takes two calls, not one:
`BindPartitions(gamez)` resolves its rectangles to gamez node indices through `WorldPartitionGrid`
while the gamez is in hand, and `Apply`'s `setActiveByIndex` delegate switches them in the built
world. Without the bind the verb is counted unapplied. Decode: [../formats/interp.md](../formats/interp.md).

## src/Mech3/ScriptedPath.cs
One authored waypoint path, resolved against the BUILT world through the runtime's own name
resolver rather than out of the gamez: the roster names `pp1`, the chapter carries the transform-only
subtree `pp1_aipath`, and its `pp1_aipN` children are the waypoints in ordinal order. Ten vehicles in
three missions carry one. A missing subtree, or fewer than two waypoints, resolves to null so the
caller reports it instead of inventing a route. Where the name comes from:
[../formats/ai-rosters.md](../formats/ai-rosters.md)'s `taxiPath` slot.

## src/Mech3/WorldPartitionGrid.cs
The world's spatial cell grid as a query: which gamez nodes does a world-space XZ rectangle cover?
Built from `GameZNode.PartitionCellNodes` (the per-cell membership `PartitionNodes` flattens away)
and the cells' own bounds. Its one reader is `MissionSetup`'s area verb. The rectangle is half-open
in cell space and the two axes run opposite ways; both are in [../formats/interp.md](../formats/interp.md).

## src/Mech3/AnimRuntime.cs
The animation engine: bootstrap passes (mission setup, anchored RESET_STATEs, ON_STARTUP, startanims, a safety net), then dispatch-table
event playback; an unhandled event kind is counted, never fatal. It owns the live definition instances and their condition evaluation, the
destructible-damage entries (`DamageAt`, which also raises `DestructibleKilled` on a healthy-role
kill, `ApplyDamageStages`, `RunDeathSequence`, `CarryState`; a host publishes each stage change and kill off `DestructibleDamaged` and the health between stages off `DestructibleChipped`, and a guest's runtime is `DamageReplicated`, spending only through `ApplyReplicatedHealth`), the world-effects runtime (`PlayEffectAt` over a hidden template stage), the emitter prewarm (`PrewarmEmitters` in one call, `PrewarmSlice` resumable for a caller
with a frame budget, which never splits one def), the range-deferred start sweep
and the vehicle/library-root index, and hands every construction site a sealed `TemplateStage`. Its
range gates read the players through `RangePositions`: the last pose they flew, while
`PlayerRangeHeld` says a cutscene is posing their aeroplanes. `FastForward` is the per-definition
rate a held key raises a cutscene to (`Anim/CutsceneFastForward.cs`), which `Advance` spends as repeated passes of the instance walk. `CollectLateStarts` and `CatchUp` step only the instances started in between, with their motions, for a guest's late director event. `SuppressedMotionAnims` names the definitions whose `OBJECT_MOTION` events this runtime drops, for a pose another writer owns, which also ends a definition that motion was sustaining (docs/verification.md, INSTR-74). What binds a member is on that member:
the pool-slot checkout reset, the prewarm's scope, the mission-trigger closure, the undercover
probe's decode, the death call's site follow. Each dispatch axis is a sibling module; the router keeps the case labels and the public fields callers configure: `SequenceRunner.cs`, `Anim/MotionSet.cs`, `Anim/NameResolver.cs`, `Anim/EmitterDirector.cs`, `Anim/SoundChannel.cs`, `Anim/LightChannel.cs`, `Anim/PoseChannel.cs`, `Anim/TemplateStage.cs`. Decode: docs/org/sequences.md.

## src/Mech3/Anim/
`AnimRuntime`'s private nested types promoted to top-level `internal` types in their own namespace,
purely for file size: `IAnimMotion` (`ScriptPlayback`/`SpinMotion`/`FromToMotion`/`OpacityFade`/
`MotionRuntime`), `AnimLight` (built only by `LightChannel`) and the bind-census `AnchorKind` enum.
Not an independently owned subsystem; `AnimRuntime` drives all of it. `MotionSet`, `EmitterDirector`,
`SoundChannel`, `LightChannel`, `PoseChannel`, `NameResolver` and `TemplateStage` share the
namespace and ARE owned in their own right, each with its entry below. `SpinMotion.ComposeSpin` is
the one member reached from outside without `AnimRuntime` at all, by `Flight/Airframe/PropAnimator.cs`. The
motion decode, both contact tiers and the readings they supersede: [../org/objectMotion.md](../org/objectMotion.md).

## src/Mech3/Anim/MotionSet.cs
`AnimRuntime`'s live motions as a module: `Add` (owner stamp, `(Target, Channel)` eviction,
`LaunchCount`), the per-frame `Tick` sweep, `DiscardFor`/`Reset`, `HaltLaunchedBy` (a stop ending
the ballistic bodies its sequence launched), and the predicates the runtime asks: `Airborne` (the
retirement hold `AnimRuntime.Retirable` consults), `OwesBounce` (its armed-branch subset, which a
suite reads), `HasSpinOn` (the `Loop{-1}` spin re-assert guard) and `LiveFromToMotion` (the
still-live transform tween `PoseChannel` carries into a replacement channel). It never constructs a motion; `PoseChannel`
builds them and hands them over. `Node3D`-typed but never dereferenced, every operation here being
identity comparison, which is why the freed-target rule sits on `Tick`. Read `Anim/PoseChannel.cs`.

## src/Mech3/Anim/EmitterDirector.cs
One runtime's `PUFFER_STATE` emitters as a module: `Assert` (start, revive, re-home), the four stops
(`End`, `EndOn`, `EndFor`, `Discard`), `Prewarm` (built ahead and left unclaimed until the first
assert takes it over), `Reset` (the crash rig's respawn, which keeps every emitter whose host still
exists), the per-frame `Tick` follow and `Census`. `AnimRuntime` keeps only the dispatch case, the
`at_node` sentinel resolution and the `active_state` read. One director per runtime;
`IEmitterFactory` is the build seam (`Puffer`, `TextureArchive` and the parent node sit behind it),
so a suite can install a fake. The selector/disposition split across the stops and the emitter-keying
tradeoff are in this file's own doc comments. Emitter decode: [../org/puffer.md](../org/puffer.md).

## src/Mech3/Anim/SoundChannel.cs
One runtime's `SOUND_NODE`/`SOUND` events as a module: `HandleSoundNode` (declare, place and start
the pooled ambient emitter), `HandleSound` (the one-shot destruction/impact player, positioned by
`OneShotSoundPosition`), the late-failure census, `Reset` (the crash rig's respawn) and `DiscardFor`
(the teardown reach-in, keyed by anchor like lights). `AnimRuntime` keeps the two case labels and
the sound reach-ins inside `OBJECT_ACTIVE_STATE` and `OBJECT_ADD_CHILD`, and keeps `Sounds` and
`SoundHandledElsewhere` as public fields callers configure; the channel reads both through closures
rather than a constructor snapshot. Ambient emitter plumbing is `WorldSounds.cs`; the definition
grammar is docs/formats/sounds.md.

## src/Mech3/Anim/LightChannel.cs
One runtime's `LIGHT_STATE`/`LIGHT_ANIMATION` events: `HandleLightState` (a partial update that
never defaults an absent field), `HandleLightAnimation` (the signed-delta tween over `run_time`),
`Tick` (the advance, then the owner's whole `WorldLights` frame or, under
`AnimRuntime.ContributeLightsTo`, only a registered submission), `Reset` and `DiscardFor`, which
`PlayEffectAt` also runs on a reused pooled copy so each call starts dark like the original's
per-call clone. Both handlers report whether they applied through their return value. `Lights` and
`LightViewerPositions` stay fields on `AnimRuntime`, read through closures that fold in the
single-camera fallback. `AnimLight` is constructed nowhere else. Read `WorldLights.cs` next.

## src/Mech3/Anim/PoseChannel.cs
One runtime's object-pose/visual events as a module: the nine `OBJECT_*` handler bodies, the pose
helpers (`PoseTranslate`/`PoseRotate`/`PoseScale`/`PoseAtNode`, which mission setup's pass 0 also
drives), the subtree opacity and fade machinery, and the landing-resume marks. It carries the
motion-BUILDER role, parsing the motion events into `MotionRuntime`/`FromToMotion`/`SpinMotion`/
`ScriptPlayback`/`OpacityFade` and handing them to `MotionSet`, which stays a pure live-set
container, while the tick spine stays in `AnimRuntime.Advance`. The two `AT_NODE` rotate spellings
and the SI-script duration rules are on their own members. Spellings and census:
docs/formats/anim-definitions/cutscenes.md.

## src/Mech3/Anim/NameResolver.cs
Name to node resolution as one public module, generic over the node type (`NameResolver<TNode>`):
the index, the wildcard `Matcher`, the memoized `FindAll`, the scoped tier chain
(`Resolve`/`ResolveScoped`), the symbol authority (`SymbolClaims`/`NarrowToSymbolRoot`), `Anchors`
(NAME match, symbol narrowing, root lift) and the bind census. Node identity is
constructor-supplied, never the node type's inherited `Equals`; `DropFreed` retires the rows naming a freed node and `DropNodes` those naming a live subtree a second staging replaces, since a name resolves to whichever claimant was indexed first. `AdmissibleStaging` filters every tier, the owner's verdict on one pooled
copy, and `RefusesGlobalTier` withholds the last tier from a plain name written by a definition
the world holds several instances of. Decode: [../org/sequences.md](../org/sequences.md).

## src/Mech3/Anim/CutsceneFastForward.cs
The rate one cutscene episode's own definitions run at while the player holds a key through a scene
that offers no skip: the target, the ramp, the scoped definition set and `RateFor`. Engine-free and
owned by nobody but `CutsceneController`, which scopes it per episode and hands it to
`AnimRuntime.FastForward`; the runtime multiplies each definition's dt by it and spends a raised
rate as repeated passes of the instance walk. A remake-only rule, so both constants are design
choices rather than decoded figures. Read `Session/World/CutsceneController.cs` next; the reasoning is
docs/formats/anim-definitions/cutscenes.md, "Handoff and skip".

## src/Mech3/Anim/TemplateStage.cs
The effect-template stage as one module (`TemplateStage<TNode>`): pool-slot arithmetic (`SlotOf`,
`TakeNextSlot`, `RootsFor`, `AssignCallerSlot`), placement (`PlaceAt`/`PlaceOn`, plus the
`PlaceFollowing`/`FollowSites` a moving call site needs), the copy-identity questions (`IsAt`,
`RootsOf`, `SharedWithLiveInstance`), the pooled-copy staging entry `IndexPooledCopy`, and the
reveal/retire/sweep ritual. `Pooled`/`Shown`/`Places` are sealed constructor state. Its two
node-identity maps keep a freed call site as a KEY: `DropFreed` rebuilds them, `FreedKeys` counts
what one leaves, on `NameResolver`'s terms above. Generic, hooks late-bound via `Wire`; charter
`CSVM.Tests/TemplateStageTests.cs`; `AnimRuntime.cs` has the reset and `RetireFreedNodes`.

## src/Mech3/SequenceRunner.cs
The engine-free sequence interpreter, extracted from `AnimRuntime` behind the `ISequenceHost` seam.
`SequenceRunner` runs one sequence's event list against two clocks, its own and the owning
`AnimInstance.Clock` a `START_TIME ANIMATION` gates on; its scope is per-event START_TIME gating,
LOOP with authored count 0 as infinite, IF/ELSEIF/ELSE/ENDIF over a branch stack, and
`WAIT_FOR_COMPLETION` as a host-armed predicate. `AnimInstance` holds one runner per `Def.Sequences`
slot and walks them ascending, plus an unslotted list for the death and damage-stage runners, and
carries the CALL_SEQUENCE/STOP_SEQUENCE semantics. Anchors are opaque pass-through, and a test
drives the seam with a recorder host. Decode: docs/org/sequences.md.

## src/Mech3/DestructibleRegistry.cs
Live per-instance HP for the world's destructibles, any `AnimDefinition` with `HEALTH > 0`: one `Instance` per `(def,
anchor)` pair seeded from the authored `HEALTH`, plus a coarse healthy/damaged/destroyed `State` and a monotonic
`DamageStage`. Built in `AnimRuntime`'s bootstrap, read by `ANIM_HEALTH` evaluation, escalated by `ApplyDamageStages`,
damaged via `DamageAt`. `Resolve(struck)` climbs to the nearest claiming pool, which answers for its damage node and
everything under it (what the original stamps its handler over) and, through its anchor alone, for nothing. `Instance`
carries what a mission record authors (`Team`, `Owner`, `Gasbag`, `Dormant`, `Reseed`) and caches the anchor's gamez
ancestor names for `TargetPool.CollectOwners` under the parent's id, so an authored re-parent re-walks them and a
per-tick ranking ask does not. Schema: docs/formats/destructibles.md; teams docs/org/targeting.md.

## src/Mech3/WavFile.cs
Pure-C# WAV parser with an MS ADPCM to PCM16 decoder (`DecodeMsAdpcm`), no Godot dependencies:
Godot cannot load the game's WAV format (see docs/formats/sounds.md).

## src/Mech3/WavCues.cs
The RIFF `cue ` chunk of a WAV as times in seconds, read from a sound extraction (zip or directory)
or from bytes already in hand. The briefing narration is the one consumer: a reveal state's
`WaitForMarker n` blocks on cue point `n`, and the durations around it are authored constants, so
these times are the only clock the script does not carry itself. Times come back ascending, which
is what `n` indexes. A file with no cue chunk, a non-WAV or a truncated one yields an empty list
rather than a failure. Kept apart from `SoundArchive.cs`, which decodes to a Godot stream, so a
menu page needing only timings stays engine-free. Decode: docs/formats/briefing.md.

## src/Mech3/SoundArchive.cs
WAV lookup over a soundsh/soundsl extraction (zip or directory), decoded through `WavFile` into
cached `AudioStreamWav`s; `Find(name, looped)` marks the stream as a forward loop when asked, and
caches per `(wav, looped)` because the play site's flag wins over the definition's. A read that
fails returns null rather than throwing, since callers run inside the session step. `Dispose`
releases the OS handle without ending the archive's usable life: the entry map is built once and
kept, so a later read reopens the zip for its one entry. Directory-backed and zip-backed archives
therefore fail differently, which is what `--zip-assets` (docs/cli.md) exists to expose on a
developer tree. Read `WorldSounds.cs` for the prewarm that fills the cache.

## src/Mech3/MusicPlayer.cs
The state-driven score: one non-positional streaming channel beside `WorldSounds`' pooled 3D
emitters, so the menu, the cabin and the mission director all drive the same track. `Enter(state)`
cues the sound-group or definition name the original's data names for that state; `Cue(name)` is
the raw form for a name the data supplies directly. One track at a time, hard cuts, no crossfade;
`NoteCombat` + `Tick` run the battle hold and its fade. The channel's own volume is the fade gain
times the duck and nothing else: the player's music level is the Music bus's gain
(`Utils/AudioMix.cs`). The selection rules, the fade rates and the tracks that ship with no trigger
are docs/org/music.md.

## src/Mech3/MissionRadio.cs
The mission radio queue, the flat Voice channel beside `MusicPlayer` and `WorldSounds`' 3D emitters.
`Cue(name)` queues an objective callout (a radio definition or a VO dialogue chain) and returns its
line count, 0 for a name this channel does not own. `Speak(name, rng, speakerId)` queues a combat
voice line on the same queue without the cue delay; `IsSpeaking(speakerId)` answers the voice gate
whether that pilot's own line still holds the channel. One call speaks at a time: a chain runs back
to back, a later call queues behind rather than cutting in, `Cancel` is `STOP_QUEUED_SOUNDS`, and a
call waiting past its `QUEUE` tolerance is dropped. `LateBy` (a guest's catch-up) joins a call where the host's is, through `LateStart.cs`. Streams come from `WorldSounds.StreamFor`. Cue
delay: docs/formats/objectives.md; the queue, classes and tolerance: docs/formats/sounds.md.

## src/Mech3/LateStart.cs
Engine-free: where a run of back-to-back clips stands once started a given time late, as the clip
index and the offset into it, or past the end. `MissionRadio` joins a late radio call there, and
`CSVM.Tests/NetDirectorLinkTests.cs` pins it.

## src/Mech3/SoundDefs.cs
sounds.json SETS parser: `snd_*` name to `SoundDef` (wav name, flags, range, volume); the entry
grammar and flag/key meanings are in docs/formats/sounds.md. `LoadGroups` parses the sibling
`SOUND_GROUPS` block into `SoundGroup`s, the weighted random destruction/impact sounds a one-shot
`SOUND` event resolves through (`air_mixed_exp_sg` to `snd_exp_hit*`).

## src/Mech3/CombatVoice.cs
The combat-voice resolver: roster `accentID` (slot 65) to a `voice.zrd` ACCENT row, to a pilot VO
id pool, to clip defs. `PlayableFor(voId, family)` returns the one name to hand
`MissionRadio.Speak`: the shipped `snd_<FAMILY>-A_id<N>_random` variant group where one is
authored, else the bare def. `SessionPrewarmNames` is the flight session's mission-roster prewarm
set, reached through `WorldSession.Options.VoiceClipNames` with CLI and Instant Action accents joined in. Dispatch
sits above this seam, in `Flight/Ai/AiVoiceDispatcher.cs` (the rules) and `Session/Roster/AiVoiceRuntime.cs`
(the wiring), never in it. Decode: docs/formats/combat-voice.md.

## src/Mech3/MissionCutscenes.cs
Which of a mission's `mis_anim.zrd` `ANIMATION_DEFINITION_FILE` entries sit under its own
`cutscenes\` directory, and the `ANIMATION_NAME`s those reader files define. That directory is the
authored classifier for mid-mission choreography: the ambient world furniture (the zeppelin wiring,
the walkers, the ladders) sits outside it, and nine story missions ship one. A mission with no such
entry, and one whose reader is missing or unreadable, resolves to nothing, so a caller can treat
"hosts no cutscene" and "is not a story mission" alike. `WorldSession` hands the names to the
cutscene host and to `AnimRuntime.RangeGatedCalls`. Decode:
docs/formats/anim-definitions/cutscenes.md.

## src/Mech3/CampaignSequence.cs
The shared `cm_sequence.zrd` reader: the campaign's 24 flat mission entries in file order, each
one's storage address (world folder, mission folder, `Persist.NNN`/`Mission.NNN` save id) and
whether it flies with a wingman. `PreviousInSameChapter` is the engine's own backwards walk to the
last earlier mission of the same world folder, which is what cross-mission persistence is scoped
by. There is no branch, no predicate and no alternate; the only selection rule is "the next `seq`",
which is why `Session/Campaign/CampaignProgression.cs` models a single integer position. The three
numberings one mission carries, and the folder-number-is-not-the-act rule, are on their own members; `ChapterNumber` inverts `ChapterFolder`, which is the environment digit an Instant Action pause dialog is keyed by.
Decode: docs/formats/campaign-sequence.md.

## src/Mech3/WorldSession.cs
Builds one chapter world and binds its `AnimProgram`, the world+anim half of a session build:
`Build` returns Root, Runtime, Program, Builder, Clutter, CloudDeck and Lights, and stops before
the per-view steps the caller drives. `Options` is the whole seam, and everything above Mech3 comes
in through it: the chapter zrdr path, the shared `DecodeCache`, the emitter factory a suite
substitutes and the archive factory that builds the real one, the clutter debug switches, the
sound-group prewarm names, the callback and trigger hosts, and `CutsceneNames` (the session's
camera, bars and intro names), whose presence gates `camera1`, the letterbox bars, the composition
frames and the `AircraftStage`. It stands up the staged props a definition reparents onto placed content, and `ResolveLibraryRoot` is the lazy pool behind a call naming a library root, keyed on anchor and event. Read `WorldBuilder.cs`.

## src/Mech3/PufferState.cs
One decoded `PUFFER_STATE` block: emission mode and cadence, velocity, size, lifetime, the texture
flipbook and the fade bands, read by `Load` from an effects reader or by `FromAnimEvent` from a
compiled anim event. Data only, so the animation layer can carry it across the `IEmitterFactory`
seam without naming the renderer; `Effects/Puffer.cs` integrates it. Keys: [../formats/effects.md](../formats/effects.md).

## src/Mech3/SubtreeBounds.cs
A built subtree's world-space extent, the union of its own mesh boxes (`WorldAabb`), skipping any
node marked `OverlayMeta` so a tool's drawing parked on an object never grows its box. Shared by
`AnimRuntime`'s effect siting and the inspect tools behind `UI/Screens/SelectionService.cs`.

## src/Mech3/AircraftStage.cs
The aircraft-archive subtrees a story-mission intro, a hangar or chuteman drop, or a wing-walk
capture animates, staged into a chapter world before the animation bind: the `piratefighter` prop,
the bodiless `player` marker the flown aircraft is posed onto, `chuteman`'s parachutist,
`balmoral`, and the `FigureNodes`/`PropNodes` groups. Each node's shipped active state and the
holder it hangs under are on its own member, which decide whether it draws in a mission that never
names it. All carry a rebased gamez index (`PointerBaseOf`) a compiled cross-archive symbol table
binds; `StageFlown` puts the one flown airframe in the node table and parks its hook, taking the airframe it replaces back out so a second human's aeroplane cannot answer for the first's name. A skinned subtree gets its own
builder, so `Paint` gives it its stand-in's livery. Decode: docs/formats/anim-definitions/cutscenes.md.

## src/Mech3/SessionArchives.cs
`OpenFor(ArchiveIntent, gamezPath, texturesPath, soundsPath, zrdrPath, mute)` opens the five
archives one chapter build needs (gamez, textures, sounds, sound defs, sound groups) and returns
the `WorldSession.Options.TexturesOutliveBuild`/`SoundsOutliveBuild` pair the intent implies:
`Session` and `Lab` set the texture flag, only `Lab` the sound flag, `Suite` neither, though the
harness's `TestWorld` owns its texture archive until the world is destroyed. One seam for
`GameSession`, the anim lab and the harness, so none hand-sets those flags. `StartupProfile` calls
are unconditional, a no-op with no session under measurement, so the harness drives the same code
blind. The optional `decode` argument makes only the returned `Gamez` a shared read-only instance.

## src/Mech3/DecodeCache.cs
The decoded inputs a world build can reuse, keyed by the absolute paths they were decoded from:
`Gamez(path)` and `Anim(shared, chapterZrdr, missionZrdr, chapterAnim, missionAnim)`. The paths are
the whole key because they already carry data root, chapter and mission; collision, mute, the
emitter factory and the prewarm list all act after the decode, on objects this never holds. It owns
nothing disposable and nothing Godot. **Everything handed back is shared and read-only by
contract**, which is why `AnimProgram`'s three collections are `IReadOnlyList`; `GameZ` has no such
enforcement, so its one in-place writer (`EffectCycles.Apply`) has to stay idempotent, which it is.
Callers are serial by construction. An instance lives as long as its holder.

## src/Mech3/EmptyStage.cs
The `--stage=empty` test stage: a flat collidable 20 km ground plane under a 100 m grid, standing in
for a chapter world so flight and ballistics runs boot in about 2 s with nothing else in the frame.
It also carries the one patrol net a session on this stage has: `PatrolNet`, a closed eight-node ring
of 1000 m about the grid origin at the spawn altitude, authoring 2500/1500/700 m volumes so a vehicle
put on it runs on a net's gates rather than the mode machine's defaults. `--ai=<plane>:grid` and
`--zep=...:net=grid` reach it through `ResolveNet`, which answers before the chapter `neindex` lookup
on a name no shipped index carries. Built in code, like the grid texture: no chapter assets are here.
