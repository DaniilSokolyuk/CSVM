# CSVM — Crimson Skies open-source remake

CSVM is a fan-made remake engine for **Crimson Skies** (2000, Zipper Interactive / Microsoft):
a modern engine that plays the original game using your own legally-owned game files.

This repository ships **code and documentation only**. It contains no game assets of
any kind, and reproduces no game code. The engine reads your existing retail install at runtime.

## Status

What this build plays:

- The single-player campaign, with its cabin, briefing and flight-check screens.
- Instant Action's four mission types: dogfighting an ace, dogfighting a squadron, attacking
  a zeppelin, and stunt flying against the clock.
- 2 to 4-player splitscreen Dogfight deathmatch, and free flight.
- 11 aircraft and 8 chapter worlds, in the original's liveries, animated and with sound.
- Guns and rockets, AI aircraft that patrol, engage and evade, turrets, zeppelins, and world
  objects that take damage and die.
- Asset extraction, complete: every ZBD archive type this game ships round-trips
  byte-identically.

This is an early build; the release page lists what is known broken in it.

## Download and play

You need Windows 10 or 11, 64-bit, a graphics card with a working Vulkan or Direct3D 12 driver,
a retail Crimson Skies install on the same machine, and about 1 GB of free disk space for the
data you extract. Nothing else: the .NET runtime the engine needs is inside the download, so
there is no runtime or framework to install.

1. Download the `CSVM-v<version>-win64.zip` asset from the
   [latest release](https://github.com/Laeresh/CSVM/releases/latest).
2. Unzip it wherever you like.
3. Double-click **`Extract.cmd`** and point it at your Crimson Skies install, the folder
   holding the `ZBD` and `GOSDATA` subfolders. This is done once, and your install is only
   read, never modified.
4. Double-click **`CSVM.exe`** and pick a mode, chapter and plane in the menu.

[`packaging/README.md`](packaging/README.md) is the long form of those four steps and ships in
the zip as the `README.md` beside `CSVM.exe`: the SHA-256 to check the download against, the
Windows SmartScreen warning an unsigned download raises, where your logs and saved games live,
and what to read when the game vanishes after it starts.

## Reporting a problem, and contributing

Bugs go in an [issue](https://github.com/Laeresh/CSVM/issues/new?template=bug_report.yml).
The form asks for the build version and the newest log file from the `logs\` folder beside
`CSVM.exe`, which between them usually identify a fault without a round trip. A report is
worked on in its own issue; this repository's `backlog.md` is the author's internal list and
is not mirrored, so the issue is the thread to follow.

Small self-contained pull requests are welcome for `packaging/`, the extraction scripts,
the documentation and typo fixes. A change under `CSVM/src` needs an issue first, because
engine changes land through a golden-image tier a contributor cannot run.
[`CONTRIBUTING.md`](.github/CONTRIBUTING.md) has the detail. Security problems go through
[`SECURITY.md`](.github/SECURITY.md) rather than an issue.

## Building from source

The rest of this page is about the source tree; nothing in it is needed to play.

```
git clone https://github.com/Laeresh/CSVM.git
```

You need a legally-owned copy of Crimson Skies, [Godot 4.7 (.NET)](https://godotengine.org/)
and the .NET 8 SDK. Extract your install's assets with `ExtractAssets.ps1`, then see
[`CSVM/README.md`](CSVM/README.md) to build and run.

Extraction is done by our fork of mech3ax, which is where the Crimson Skies format support
lives: **[Laeresh/mech3ax, branch `cs-anim`](https://github.com/Laeresh/mech3ax/tree/cs-anim)**
— that branch is what the extractor binary is built from. (The fork's `main` is an upstream
mirror and does *not* carry the Crimson Skies work.)

### Build and run

```
dotnet build CSVM/CSVM.sln
godot --path CSVM res://scenes/Main.tscn -- --plane=player_bhawk
```

First time only, run Godot once with `--headless --import` before running scenes. `RunGame.ps1`
wraps the build step and launches the in-game menu with no args; `RunDev.ps1` is the same with
interactive console prompts. Verify a change with `.\RunTests.ps1` — one command, one exit code
(build → unit tests → in-engine suites → golden-image hashes).

### Package a release build

Godot export templates are user-global, not part of the repo. One-time setup: extract the inner
`templates/` files of `tools/godot-4.7-mono-export-templates.tpz` directly into
`%APPDATA%\Godot\export_templates\4.7.stable.mono\`.

```
.\ExportRelease.ps1
```

`ExportRelease.ps1` builds, imports, and exports the release preset to `.scratch\export\CSVM.exe`,
checking the export templates are installed and clearing `.scratch\export\` before it starts.
This produces a self-contained `CSVM.exe` (the .NET runtime is bundled, so a recipient installs
nothing) plus its data folder. It then copies the rest of the release in beside it,
`packaging/Extract.cmd` and `packaging/Extract.ps1` (what a recipient double-clicks, and the
dispatcher it runs against their own game install), the unmodified `ExtractAssets.ps1` /
`ExtractRof.ps1` / `ExtractRof.MenuLayout.cs`, the built `unzbd.exe`, and `packaging/README.md`
/ `LICENSE` / `LICENSE-unzbd` / `LICENSE-thirdparty.txt`, generates `BUILD-INFO.txt`, and zips
the folder to
`.scratch\CSVM-v<version>-win64.zip`, the archive to hand over. The version is
`application/config/version` in `CSVM/project.godot`, which the exe's file properties and the
first line of every log state as well. See [`packaging/MANIFEST.md`](packaging/MANIFEST.md) for
the layout and [`docs/tooling.md`](docs/tooling.md) for the full export and packaging detail.

```
.\PublishRelease.ps1 -NotesFile <file>
```

`PublishRelease.ps1` is the publish itself, in one run: it reads the same version, runs
`ExportRelease.ps1`, computes the zip's SHA-256, creates the annotated tag on the commit that
was built, pushes it, and creates the GitHub release with the zip as its only asset, so the
tag, the exe's version, the zip's name, the published checksum and the notes cannot disagree
with each other. It needs `gh` installed and authenticated, and it refuses a dirty tree, a
`tools/mech3ax` `cs-anim` that is dirty or unpushed, and a tag that already exists; a tag is
never re-pointed. `-DryRun` runs every check and the export and stops before the tag.

## Format documentation

The reverse-engineered format reference lives in [`docs/formats/`](docs/formats/) — 48 pages
covering the GameZ container, reader archives, paint schemes, animation definitions, world
structure and more. Most of it was decoded by inspecting extracted data and matching
behaviour against the original game. Most of those pages also rest on static analysis of the
retail `crimson.exe` wherever it settles a question the data cannot, naming the function or
address at the point of use rather than reproducing code. Findings that come from
the executable wholesale live apart from the format reference, in [`docs/org/`](docs/org/): the
flight model, the weapon and particle runtimes, the AI, and the rest of the original behaviour
this engine matches. Those pages describe behaviour and constants, and name the function
addresses so any claim can be re-checked at source. **No game code and no game assets are
reproduced or redistributed here**; the engine is an independent implementation.

## Legal

This is an unofficial fan project, **not affiliated with, endorsed by, or sponsored by
Microsoft or Zipper Interactive**. "Crimson Skies" and all related names, marks and artwork
are the property of their respective owners.

No game content is distributed here. The engine requires — and reads at runtime — game files
from your own legally-obtained copy of Crimson Skies. Nothing in this repository will run
without it.

## License

Code is licensed under the **GNU General Public License, version 3 or later**
([`LICENSE`](LICENSE)).

    Copyright (C) 2026 Gabriel Unmüßig

    This program is free software: you can redistribute it and/or modify it under
    the terms of the GNU General Public License as published by the Free Software
    Foundation, either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful, but WITHOUT ANY
    WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
    PARTICULAR PURPOSE. See the GNU General Public License for more details.

    You should have received a copy of the GNU General Public License along with this
    program. If not, see <https://www.gnu.org/licenses/>.

The format documentation in `docs/formats/` is licensed separately and more permissively,
under **Creative Commons Attribution 4.0 International**
([`docs/formats/LICENSE`](docs/formats/LICENSE)), so that the format knowledge can be reused
by any project regardless of its own license. Attribute to **CSVM**, linking
<https://github.com/Laeresh/CSVM>.

Asset extraction uses [our fork](https://github.com/Laeresh/mech3ax) of
[mech3ax](https://github.com/TerranMechworks/mech3ax), which is licensed under the EUPL-1.2
and carries its own license terms. It is a separate tool, not linked into this engine.

## AI assistance disclosure

This project is developed with the help of AI coding agents. Parsers, engine code and
documentation are AI-assisted and human-reviewed; commit trailers name the specific agent and
model that did the work.
