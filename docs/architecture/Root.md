# Root

The three files at the root of `CSVM/src` (`SessionSpec.cs`, `SessionPaths.cs`, `Pads.cs`), plus the enhanced graphics mode, a rendering divergence that spans every namespace.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## Rendering: the enhanced graphics mode (a documented divergence)

Enhanced graphics mode is an opt-in divergence: a deliberate, recorded departure from the original,
never presented as the original's own behaviour. It is gated by the saved `graphicsMode` option both
Options screens write, under it the `graphics.mode` config key (`original`/`enhanced`, default
`original`, `src/Utils/GraphicsMode.cs`), and over both an explicit `--graphics=` flag
([../cli.md](../cli.md)) that survives `--det`, so a golden or a deterministic capture can ask for
the enhanced path on purpose while every ordinary `--det` run, including the full golden sweep,
stays on `original`.

Original mode's rendering does not move under this mode: enhanced mode replaces the fullbright
world with real Godot lighting. The world shades under decoded, pre-negated vertex normals as a
matte material (`SceneBuilder.cs`'s lit-world arm); a `DirectionalLight3D` and the Environment's
ambient are driven from the mission's authored `SUNLIGHT_DIFFUSE`/`SUNLIGHT_AMBIENT` values and
colours instead of the launcher's hardcoded numbers (`WeatherRig.cs`); the sun casts PSSM shadow
maps, with each zone's authored fog pushed out 2x and the shadow's max distance following that
pushed far so shadows never end in clear air; `LIGHT_STATE` point lights are mirrored onto real
`OmniLight3D` nodes that light the world and the aircraft, not only the per-vertex point term's data texture
(`WorldLights.cs`); the light-source class of glow-arm sprites (flares, beacons, signal lamps)
scales its colour above 1.0 to feed an Environment glow pass, and an AgX tonemap rolls the
resulting HDR scene off instead of clipping it; SSAO adds contact shading in ambient light, and
SSR reflects the shoreline off water surfaces the engine already classifies as `"water"`
(`Launcher.cs`'s `SetupLighting`); the sky those water surfaces reflect where SSR finds nothing is
the flown zone's own `FOG_COLOR`, painted flat over Godot's procedural placeholder as a `Sky`
resource (`Launcher.cs`'s `UseMissionSky`, `WeatherRig.WriteSkyColor`). The cockpit interior pass
and every splitscreen pane pick up the same settings and the same per-zone updates, since both
duplicate or share the session's own sun and Environment (`CockpitOverlay.cs`, `SplitScreen.cs`).

One drawing runs in **original mode alone**, the only difference in that direction: the aircraft's
projected ground shadow (`Flight/Airframe/GroundShadowPass.cs`, decoded in `../org/shadows.md`). It is the
original's own substitute for shadow mapping, so under enhanced mode, where the sun casts real
shadow maps, the pass is not built at all and the aircraft's own shadow is the mapped one.

The energy mapping from authored SUNLIGHT units to Godot light energies, the 2x fog-range push and
the shadow distance following it, the night key read off `FOG_COLOR` luminance with its 0.25
separator and its 0.6 / 0.15 energy cap, and how far SSR smears on wave-less water planes are TUNE:
judged at the controls against captures, not derived from a decoded rule. The night key in
particular is a proxy the original never uses, which lights from SUNLIGHT and darkens from
FOG_COLOR independently.

Both Options screens expose the mode as a two-way row saved into the menu plan's options store
([../menu-presentations.md](../menu-presentations.md)); every reader in this codebase consults the
resolved `GraphicsMode.Enhanced` boolean only, so neither the store nor the screens reach any of
them. The saved word changes on Apply and the world takes it on the next start, since the shader
memos and the Environment are built from the value resolved once at launch, which is why each
screen's description line says so.

## src/Pads.cs
Single source of truth for which gamepads exist: every reader goes through it rather than
`Input.GetConnectedJoypads()`. Owns the phantom-device policy (span every pad, never `pads[0]`),
`Disabled` (`--no-pads`) and the focus gate that suppresses reads without un-joining anyone.
`AssignPads` is the launch-time roster split: P2 to P4 take roster POSITIONS in order and P1 gets
every pad none of them claimed, so a device occupying a position without producing input takes that
seat and leaves the real pad in P1's pool, flying P1's plane beside P1's own. `LogPads` records the
roster with position and id separately, which is what makes that mismatch readable afterwards. A
menu-driven launch binds by device id instead (`Launcher.BindMenuPads`) and so seats no phantom.

## src/SessionPaths.cs
Static resolver for the extracted-data paths (`ChapterTextures`/`ChapterGamez`/`ChapterZrdr`/
`MissionZrdr`) under a data root, plus `PreferUnzipped` (an unpacked sibling dir beats its `.zip`)
and the `--zip-assets` switch that inverts it. The `rtextureN` tier decode is on `docs/tooling.md`;
the `--gamez=`/`--textures=` override policy stays in `GameSession`, not here.

## src/SessionSpec.cs
Everything the command line settles about a session as one immutable, engine-free record:
`Parse(args)` parses **and** resolves, so a consumer reads an answer instead of re-deriving one, and
the pure arg parsers (`ParseVec3`, `ParsePlanes`, `ParseHold`, …) are public so they are testable.
`SessionMode` is closed (Menu/Fly/Viewer/Freecam/AnimLab), with the `Stunt`, `Versus` and `Coop`
modifiers and `SessionProbe` beside it. `FromMenu` and `FromCampaign` are the launchscreen's and the
campaign cabin's counterparts to a command line, each carrying its own no-re-resolve rule on itself;
a menu value that a spelled-out flag beats says so in an `*Explicit` field (`ScenarioExplicit`, `ViewModeExplicit` over the opening view, which keeps a `--view=` above the saved Default View even where both name chase, and `VsKillsExplicit`/`VsTimeExplicit` over Dogfight's kill target and time limit). The `WithSaved*` folds are how a saved option reaches a spec, each dropped under `--det`.
`Resolve` carries its step order and the type's purity contract (DET-9). Per-rule coverage lives in `CSVM.Tests/SessionSpec*Tests`.
