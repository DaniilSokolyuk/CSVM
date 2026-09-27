# Crimson Skies remake — Godot project

Godot 4 (.NET/C#) engine remake of Crimson Skies (2000). This project ships **no game
assets**: it reads the player's own legally-owned install at runtime, via
[mech3ax](https://github.com/TerranMechworks/mech3ax) extraction output.

## Prerequisites

- Godot 4.7 (.NET edition)
- .NET SDK 8+
- mech3ax `unzbd` extraction output from your own Crimson Skies install. The easiest
  way is the repo-root `Extract.ps1` script, which runs the engine's own extraction into
  `extracted/`, mirroring the game's folder structure (`extracted/C1/gamez.zip`,
  `extracted/C1/texture.zip`, `extracted/planes.zip`, …):
  - `.\Extract.ps1 -Unzip`
  - or manually, the two the viewer needs by default:
    - `unzbd cs gamez <install>/ZBD/planes.zbd extracted/planes.zip`
    - `unzbd cs textures <install>/ZBD/C1/texture.zbd extracted/C1/texture.zip`

By default the viewer looks for the `extracted/` directory next to (one level above)
this project directory; override with `--gamez=` / `--textures=`.

## Run the plane viewer

```
dotnet build CSVM.sln
godot --path . res://scenes/Main.tscn -- --plane=player_bhawk
```

Left-drag orbits, mouse wheel zooms, Escape quits. Useful args (all after `--`):
`--plane=<node>` (e.g. `player_kestrel`, `player_autogyro`), `--yaw=` / `--pitch=`
(radians), `--screenshot=<path>` (renders a few frames, saves a PNG, exits).
