# Crimson Skies format documentation

Reverse-engineered format reference for **Crimson Skies** (2000, Zipper Interactive /
Microsoft), validated against a retail install with this project's [mech3ax](https://github.com/TerranMechworks/mech3ax)
fork (`unzbd cs <mode>`; see [extraction.md](extraction.md)). This is the project's public deliverable under the XWVM legal
model: **format documentation and code only, no game asset data.** Pages carry field
tables and tiny excerpt values, never bulk extracted content.

Most of this was decoded by inspecting extracted data and matching behavior against the original
game (screenshots, videos, in-game measurements). Most pages additionally cite `crimson.exe`, at
each point where the retail binary settles a question the data cannot: 40 of the 48 pages here name
a function, address or global from it. That is sometimes a literal the file's meaning depends on,
such as the editor format comment naming every `aiv` roster field ([ai-rosters.md](ai-rosters.md))
or the absent `ThrustFactor` token ([vehicle.md](vehicle.md)), and more often the routine that
consumes the field, such as the parser and per-frame tick behind every directive's semantics in
[objectives.md](objectives.md). Such claims name their evidence at the point of use, so any of them
can be re-checked at source; no code is reproduced.

A page whose claims come from the executable **wholesale** does not live here at all, it goes in
[`docs/org/`](../org/), outside this directory and outside its licence:
[flightModel.md](../org/flightModel.md) (the flight model), [puffer.md](../org/puffer.md) (the
particle runtime behind [effects.md](effects.md)), [tracers.md](../org/tracers.md) (the projectile
visual runtime behind [weapons.md](weapons.md)), [objectMotion.md](../org/objectMotion.md) (the
launch/contact/run-time model behind the `OBJECT_MOTION` sections of
[anim-definitions.md](anim-definitions.md) and [destructibles.md](destructibles.md)),
[aim-assist.md](../org/aim-assist.md) (the sticky-bullet gun assist behind
[vehicle.md](vehicle.md)'s `player.json` table), [aiControlLaw.md](../org/aiControlLaw.md) (the AI
steering law behind [ai-rosters.md](ai-rosters.md)'s skill and control-scale slots),
[aiPilot.md](../org/aiPilot.md) (what a roster vehicle actually flies: the `mode` classes, the
patrol-net follower and the formation escort, behind [ai-rosters.md](ai-rosters.md) and
[ai-nets.md](ai-nets.md)),
[textures.md](../org/textures.md) (the texture header layout and the additive-vs-mix blend rule
behind [effects.md](effects.md)'s sprites),
[loading-screen.md](../org/loading-screen.md) (how the mission load screen is named, filled and
redrawn, behind the `Loading.zrd` dialogs in [zrdr.md](zrdr.md)),
[shakes.md](../org/shakes.md) (how the original consumes the shake laws, the per-shot/per-frame
magnitudes and the consumer that rocks the plane, behind [shakes.md](shakes.md) here),
[music.md](../org/music.md) (the music channel's routing, variant choice, battle timer and fades,
behind [sounds.md](sounds.md)'s `MUSIC` groups),
[craters.md](../org/craters.md) (the terrain-carving subsystem behind [weapons.md](weapons.md)'s
`CRATER` key),
[multiplayer-spawn.md](../org/multiplayer-spawn.md) (the opening and respawn placements behind
[net-spawns.md](net-spawns.md)) and
[debrief.md](../org/debrief.md) (the pass that fills the attempt half of the mission-result record
in [saved-games.md](saved-games.md), and the skip-this-mission offer it carries).

## Reader map

### Start here

- [extraction.md](extraction.md), extraction modes, output, and round-trip support.
- [gotchas.md](gotchas.md), cross-cutting reader and renderer rules.
- [zrdr.md](zrdr.md), reader archives and their family index.

### World and scene

- [gamez.md](gamez.md), [world-structure.md](world-structure.md), [interp.md](interp.md), [clutter.md](clutter.md), [templates.md](templates.md), and [fogvol.md](fogvol.md).
- [weather.md](weather.md), [anim-definitions.md](anim-definitions.md), [destructibles.md](destructibles.md), and [effects.md](effects.md).
  - [Weather atmosphere controls](weather/atmosphere.md), cloud cover, wind, and precipitation.
  - [Compiled animation archives](anim-definitions/compiled-archives.md), the compiled archive and SI-script reference.
  - [Cutscenes](anim-definitions/cutscenes.md), the `letterbox` bars and the `CALLBACK` code table.

### Aircraft and combat

- [vehicle.md](vehicle.md), [markers.md](markers.md), [loadouts.md](loadouts.md), [paint.md](paint.md), and [camparam.md](camparam.md).
  - [Player global blocks](vehicle/player-globals.md), `player.json` globals.
- [weapons.md](weapons.md), [weapon-effects.md](weapon-effects.md), [turrets.md](turrets.md), [shakes.md](shakes.md), and [sounds.md](sounds.md).
  - [Ordnance effects and projectile prototypes](weapon-effects/ordnance.md).

### Missions and AI

- [spawns.md](spawns.md), [net-spawns.md](net-spawns.md), [missions.md](missions.md), [objectives.md](objectives.md), [mission-entities.md](mission-entities.md), and [instant-action.md](instant-action.md).
  - [Enemy generators](mission-entities/enemy-generators.md), host, launch cycle, and capacity rules.
  - [Instant Action wrap-up](instant-action/wrap-up.md), scoring and friendly-fire rules.
- [ai-nets.md](ai-nets.md), [ai-rosters.md](ai-rosters.md), and [combat-voice.md](combat-voice.md).
- [saved-games.md](saved-games.md), the `SavedGames\` container, the campaign profile, and what
  `Persist.NNN` and `Mission.NNN` carry.
- [campaign-sequence.md](campaign-sequence.md), the campaign's 24-mission order, its mission ids,
  and the progression rule.
  - [Campaign mission lookup](campaign-missions.md), the `CM01`-`CM24` ↔ `C<n>/M0<n>` ↔ `seq`
    table, both directions.

### Presentation and UI

- [hud.md](hud.md), [rof.md](rof.md), [strings.md](strings.md), and [briefing.md](briefing.md).
- [menu-layout.md](menu-layout.md), `LAYOUT.CSV`, the authored widget geometry of every
  single-player menu screen, and the decoded `menu_layout.json` extraction emits from it.
- [campaign-screens.md](campaign-screens.md), the campaign GUI scripts: profile, cabin, chapter
  intro, flight check, and ammo selection.
- [cinemas.md](cinemas.md), the shipped `.mpg` cinemas, their container and codecs, and how CSVM
  would play them.
## Shared conventions (zrdr readers)

The zrdr "reader" files are the engine's config/script lists; mech3ax extracts each to a
JSON file of nested arrays. Conventions that recur across every reader family:

- **All numbers arrive as floats.** mech3ax emits every scalar through a single-precision
  path, so integer-looking values (`PARTICLES 100`) parse as `100.0`, read as float,
  cast as needed.
- **Alternating key/list dicts.** The dominant shape is a flat list alternating
  `"KEY", [values…]`, a dictionary by convention, not by JSON structure. Caveat:
  **duplicate keys can be meaningful** (multiple `SEQUENCE_DEFINITION`s per animation
  definition, repeated ops per sequence), a collapsing dict view silently loses data;
  walk the raw list where duplicates matter (see [anim-definitions.md](anim-definitions.md)).
- **Bare-scalar blocks.** Some blocks instead pair keys with a *bare* value
  (`"TOP", 1124` / `"TYPE", "SNOW"`) or mix bare scalars and lists, a dict view built
  for `key,[list]` drops the values. Known cases: `weather.json`'s `CLOUD_COVER`, `WIND`
  and precipitation blocks (see [weather.md](weather.md)).
- **A key followed by `null` (or by another key) is a bare flag** (`LOCAL_NODES_ONLY`,
  `LOOPED`).
- **Dual colour-triple encoding.** RGB triples coexist in two encodings, even within one
  file: normalized floats (`[0.69, 0.69, 0.69]`) and integer 0–255 (`[192, 192, 192]`).
  Rule, verified unambiguous across the install: **divide by 255 iff any component is
  strictly > 1** (a component of exactly 1.0 belongs to a float triple). The normalized
  value is a DX7 sRGB framebuffer colour. Details + the proof in [weather.md](weather.md).
- **`kind_of` inheritance.** Definition files (e.g. `vehicle.json`) alternate
  `defName, [properties…]`; a def's `kind_of` names its parent def and properties resolve
  nearest-first through the chain (`pbloodhawk` → `player_airplane` → `basic_airplane`).
- **Name wildcards.** Where readers reference scene-node names (animation definitions),
  each `*` stands for at most one digit (`crate**` ↔ `crate01`, never `craterlake`; the
  original stamps a digit per star, [org/sequences.md](../org/sequences.md), and its compiler
  instanced `lkshadow*` as plain `lkshadow`), `#` a run of
  digits, and the `.flt` model suffix
  is optional (`ap_radiotwr` ↔ node `ap_radiotwr.flt`). **Not only node names**, a
  `PUFFER_STATE`'s own `NAME`, which is a separate namespace, takes a `*` too and expands at
  compile time (`torch_puffer*` → `torch_puffer1`/`2`); see
  [anim-definitions.md](anim-definitions.md).
- **Units** are meters, seconds, and degrees throughout (`fd_speed 135` m/s ≈ 302 mph =
  the Bloodhawk's published top speed; `RANGE` distances in meters; rotations in degrees).

## Extraction map

The extraction (`CSVM/src/Extraction/`) runs the right `unzbd cs` mode per archive type:
`interp` → `interp.json` (boot scripts); `planes.zbd`/`gamez.zbd` → `gamez` (JSON + mesh
data); `soundsh/soundsl` → `sounds` (WAVs); `zrdr` → `reader` (JSON);
`rimage`/`texture`/`rtexture*` → `textures` (PNGs). The per-chapter `rtextureN` archives
are *downscaled* quality tiers of the base `texture` set (never higher-res); `rimage` is
menu/briefing UI only. `cam_anim.zbd`/`mis_anim.zbd` → `anim` (JSON defs + SI scripts),
**supported in this project's mech3ax fork**, not in upstream or in the pinned v0.6.1 binary;
the schema lives in [anim-definitions.md](anim-definitions.md). Per-type support and
round-trip status: [extraction.md](extraction.md).

Its second half covers the non-ZBD half of the install: the `.rof` UI resource
archives and the `langui.dll` string table, into `extracted\rof\`. These are decoded by this
project rather than by mech3ax, see [rof.md](rof.md) and [strings.md](strings.md).

## License

This directory is licensed under **Creative Commons Attribution 4.0 International**
([LICENSE](LICENSE)), deliberately more permissive than the GPL-3.0-or-later covering the
rest of the repository, so these findings can be reused by any project whatever its own
license. Attribute to **CSVM** (<https://github.com/Laeresh/CSVM>). The rest of the repository,
including the code that reads these formats, remains GPL-3.0-or-later.
