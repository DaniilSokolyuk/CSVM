# Effects

Particle systems: the puffer emitter and its GPU renderer seam, the ambient cloud clutter field, precipitation, and the mission wind that drives them.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Effects/Puffer.cs
The engine's billboard-particle emitter, read from `PUFFER_STATE` blocks by `PufferState.Load` or
`FromAnimEvent` (`Mech3/PufferState.cs`): `Puffer.Create` bakes the frame atlas and each frame's blend (the texture's additive
bit) into an `IEmitterRenderer` (`EmitterRenderer.cs`); this class owns only the CPU integration.
The authored state picks burst, distance-trail or sustained mode; callers drive it through
`Emit`/`Stop`, `Burst` and the hard-kill `Clear`, and `CreateWith` reaches all three with no atlas,
archive or GPU. `_Process` writes the frame's draws farthest-first against pane 0. `BirthAlpha` is
an opacity each particle keeps from birth, 1 except on the code-built exhaust trail. The distance fade, the accumulator, the pools and the two unauthored-interval constants carry their own constraint. Keys and decode: [../formats/effects.md](../formats/effects.md), [../org/puffer.md](../org/puffer.md).

## src/Effects/WorldWind.cs
Two types delivering the mission's authored wind to every puffer. `WorldWind` is the gust model, a
static base vector plus a horizontal random-walk gust stepped once per frame off its own `Rng.Wind`
stream, authored in `weather.zrd`'s `WIND` block (schema:
[../formats/weather.md](../formats/weather.md); decode: [../org/weather.md](../org/weather.md)).
`EffectAmbience` is the seam holding the per-frame state a `Puffer` reads, the wind and every
pane's camera pose. `GameSession` owns the one instance and `WeatherRig.Tick` writes it per frame;
the world build's emitter factory closes over that same instance, so its emitters fade and cull
like the player's own. `Still` is the camera-less null object an unwired puffer reads. Read `Puffer.cs` next.

## src/Effects/PufferEmitterFactory.cs
The one real implementation of the animation layer's `IEmitterFactory` seam (`Mech3/Anim/IEmitter.cs`):
`Create` turns a `PufferState` into a sustained `Puffer` wrapped as an `IEmitter`, parented under
the world root and reading the session's `EffectAmbience`. The seam is owned below, so the
animation runtime drives emitters without naming this layer; `GameSession` and the test harness
hand the factory in through `WorldSession.Options`. A texture-less state is a stub and builds nothing.

## src/Effects/EmitterRenderer.cs
`Puffer`'s lower seam. `IEmitterRenderer` takes live particles (`Attach` sizes the pool, `Grow`
re-sizes it, `Write` per particle, `Show` publishes the frame), reaching the three emitter modes
without a GPU via `RecordingEmitterRenderer`. `MultiMeshEmitterRenderer` draws them as MultiMeshes of camera-billboarded quads, over one
process-wide unit quad and one compiled shader per blend and soft pair, and owns that shader: quad-rim fade, flipbook column from
per-instance custom data, the soft-particle depth fade, `csky_srgb_to_linear` on the `COLORS` ramp, and the
mission's distance fog off the sky's globals. Blend arrives per atlas column from `Puffer.Create`,
keeping this seam free of `TextureArchive`; a column set spanning both draws one MultiMesh per
blend, each in write order and depth-sorted on its cloud's AABB centre. Read `Puffer.cs` next.

## src/Effects/FogVolumeClutter.cs
The ambient cloud field, entirely authored: `fogvol.zrd`'s weighted clutter table laid on a
staggered lattice over every unflagged polygon of every `fvol*` volume, one alpha-blended
MultiMesh per sprite kind, plus a map-edge continuation
(`ExtendPastMapEdge`/`EmitExtensionRegion`) past the map rim for a map-spanning slab. Templates
resolve through `ClutterBuilder.FindTemplateRoot`. `BandData` packs each sprite's own face normal
and the one draw both `far_fade_range` pairs are interpolated with into a custom-data slot, which
`csky_clutter_fade_alpha_angled` turns into the view-angle fade; that draw takes its own
`Rng.CloudBands` stream. The shipped field is that decoded lattice plus a remake-only X/Z offset per card (`ShippedJitter`, 30 m, overridden by `--cloud-jitter=`), drawn off `Rng.CloudJitter` and reaching no other population. The quad is posed by `csky_facade_spherical` (`shaders/csky_facade.gdshaderinc`), a world-up look-at standing in for the original's SphericalY tracker, which reads the eye's position and not its basis, so neither the camera's roll nor a sideways move turns a card ([../org/cloudCards.md](../org/cloudCards.md)). A `lighting: true` card (C1C, C2B, C5) carries its three authored normals and takes the original's per-vertex `AMBIENT + DIFFUSE x max(N.L, 0)` through that same pose off `WeatherRig`'s uncollapsed globals, never `csky_world_light` ([../org/vertexLighting.md](../org/vertexLighting.md)). Gating: `GameSession`/`WorldBuilder`/`WeatherRig`. Schema: [../formats/fogvol.md](../formats/fogvol.md).

## src/Effects/Precipitation.cs
Rain and snow from `weather.json`'s precipitation block (`WeatherState.PrecipData`): ONE MultiMesh
whose shader derives each quad's position from a per-instance seed, `csky_time` and
`CAMERA_POSITION_WORLD`, wrapped into a camera-centred box, so the field costs no per-frame CPU.
SNOW flutters as flakes; RAIN streaks along the data's world fall velocity. The sprites are
procedural (`MakeFlakeTexture`/`MakeStreakTexture`), the original having drawn untextured
primitives no archive carries. Schema and the data-to-look TUNE mapping:
[../formats/weather.md](../formats/weather.md).
