# CSVM — Crimson Skies open-source remake

CSVM is a fan-made remake engine for **Crimson Skies** (2000, Zipper Interactive /
Microsoft): a modern engine that plays the original game using your own legally-owned
game files.

**You must own Crimson Skies.** This zip contains no game data of any kind, only the
engine and an extraction tool. Everything you see and hear in the game is read from your
own retail install.

## Where this download comes from

CSVM is published in one place, the releases page of its repository:

<https://github.com/Laeresh/CSVM/releases>

Every release lists the SHA-256 of its zip. To check the file you downloaded, open a
terminal in the folder holding the zip and run this, with the name replaced by the zip you
actually have:

```
Get-FileHash CSVM-v0.1.0-win64.zip -Algorithm SHA256
```

The printed hash must match the one on the release page (upper and lower case do not
matter). If it does not, delete the file and download it again from the link above.
Nothing in this zip is code-signed, so that hash is what tells you this is the build the
project published, rather than something a third party rebuilt or altered.

## Requirements

- **Windows 10 or 11**, 64-bit.
- **A graphics card with a working Vulkan or Direct3D 12 driver.** Either one is enough;
  the build picks whichever is available. Below that line the program starts and then
  vanishes at the intro film, before any menu; "The game disappears after it starts" below
  says how to recognise that case.
- **A retail Crimson Skies install** (the original 2000 PC game) on this machine.
- **About 1 GB of free disk space** for the game data you extract.
- Nothing else. The .NET runtime this engine needs is inside the download, so there is no
  runtime or framework to install.

## Setup: extract, then fly

**1. Extract your game's assets.** This is done once. Double-click **`Extract.cmd`** in
this folder. It looks for your Crimson Skies install where the game is normally installed,
offers you a folder picker either way, and holds its window open at the end so you can
read what it did. Dragging your Crimson Skies install folder onto `Extract.cmd` uses that
folder directly.

The folder it wants is the one containing the `ZBD` and `GOSDATA` subfolders. Open your
install until you see those two side by side, and use that folder. Your install is only
read, never modified. The extracted data lands in an `extracted` folder next to
`CSVM.exe`.

To do the same from a terminal, with the path given directly:

```
powershell -ExecutionPolicy Bypass -File Extract.ps1 "C:\Program Files (x86)\Microsoft Games\Crimson Skies"
```

Use exactly that command spelling: Windows' default script policy blocks a plain
`.\Extract.ps1`, and the `-ExecutionPolicy Bypass -File` form is the supported way around
it for this one script.

**2. Fly.** Double-click **`CSVM.exe`** and pick a mode, chapter and plane in the menu.

Starting `CSVM.exe` before the extraction is not a silent failure: it puts a screen up
naming the step that produces the data, instead of loading a world it has nothing to build
it from.

## Disk space and time

Extraction produces about **0.6 GB** in the `extracted` folder (leave 1 GB free to be
safe) and takes **well under a minute** on an SSD, around 15 seconds on a fast machine.

## Windows SmartScreen

**Nothing in this zip is code-signed.** The first time you run something out of this
folder, Windows will likely show a blue **"Windows protected your PC"** screen. Click
**"More info"**, then **"Run anyway"**.

`Extract.cmd` raises a different, smaller prompt: an **"Open File - Security Warning"**
box saying the publisher could not be verified, with **Run** and **Cancel** buttons. Click
**Run**.

Both prompts mean Windows does not recognise the publisher, which is true of every
unsigned download and says nothing about what the file does. The check that does mean
something is the SHA-256 above, taken against the release page. If this zip reached you
from anywhere other than <https://github.com/Laeresh/CSVM/releases>, do that check before
you run any of it.

Windows also marks files that came from the internet. If something refuses to run at all,
right-click the zip file, choose **Properties**, tick **Unblock**, click OK, and unzip it
again.

## If something goes wrong

Logs are written to `logs\` next to `CSVM.exe`, one file per run, named for the mode and
the time it started. The newest file there is the run you just did, and its first line
states the build version. That same version is in the bottom-right corner of the menu.

Report a problem at <https://github.com/Laeresh/CSVM/issues>, through the bug report form.
It asks for the build version, what you did, your graphics card and the newest log file
from `logs\`, which is usually all it takes to identify the fault; attaching that log
answers most of the form by itself. CSVM is written by one person, so a report is read and
worked on in its issue rather than answered to a schedule.

Three failures answer themselves:

- **Extraction stopped with an error.** The extraction window prints which step failed and
  on which folder. That text is the useful part of a report about extraction.
- **The game warns about the extraction when it starts.** Re-run `Extract.cmd`. The warning
  names `ExtractAssets.ps1` and `ExtractRof.ps1`, which are the two scripts `Extract.cmd`
  runs for you.
- **The game disappears after it starts.** Read on.

### The game disappears after it starts

The window opens, the boot card shows, and a few seconds later, as the intro film begins,
the program vanishes with no window and no message. That is what this build does on a
machine below the requirement above, and there is no setting inside the game that changes
it. Without extracted data the same machine still shows the "No game data found" screen,
so seeing that screen does not mean the machine can play.

To tell that case apart from a genuine crash, open the newest file in `logs\` and find the
line beginning `[perf] gpu=`. It names your graphics adapter and the renderer in use. A
`gpu=Microsoft Basic Render Driver` there means Windows is rendering in software, with no
graphics card driving it, and this build cannot fly a mission that way.

The engine's own startup output may also say `Your video card drivers seem not to support
Vulkan, switching to Direct3D 12`. On its own that line is not a fault, because Direct3D 12
is one of the two drivers this build runs on. It matters only when the `[perf] gpu=` line
then names a software device.

The usual cause is a machine with no graphics driver installed, or a virtual machine
without graphics passthrough. Installing your graphics card's current driver is the fix
where there is one.

## Where your files live

The engine, your `extracted` game assets and the `logs` folder all sit in this one folder,
wherever you unzipped it. Your settings, control bindings, campaign profiles, stunt scores
and custom planes are kept outside it, in:

```
%APPDATA%\Godot\app_userdata\CSVM
```

(paste that into Explorer's address bar). Deleting those two folders removes everything
CSVM has written; your Crimson Skies install is never modified.

## What else is in this folder

- `CSVM.exe` and the `data_CSVM_windows_x86_64` folder beside it are the engine. They
  belong together; moving one without the other breaks the build.
- `Extract.cmd`, `Extract.ps1`, `ExtractAssets.ps1`, `ExtractRof.ps1`,
  `ExtractRof.MenuLayout.cs` and `tools\unzbd.exe` are the extraction step. It needs all
  six.
- `LICENSE` is the GNU GPL v3 the engine is under, and `LICENSE-unzbd` the EUPL-1.2 the
  bundled extraction tool is under.
- `LICENSE-thirdparty.txt` carries the copyright notices for the software built into those
  two binaries: the Godot engine and its own third-party components, the bundled .NET
  runtime, and the Rust libraries inside `tools\unzbd.exe`. It also carries the notice for
  `pl_mpeg`, the MIT-licensed library CSVM's video decoder is ported from, and the SIL Open
  Font License for PromptFont, the font the controller button pictures are drawn from.
- `BUILD-INFO.txt` records which commit each of the two shipped binaries was built from.
  Quote it in a bug report when you are not sure which build you have.

## Legal

This is an unofficial fan project, **not affiliated with, endorsed by, or sponsored by
Microsoft or Zipper Interactive**. "Crimson Skies" and all related names, marks and
artwork are the property of their respective owners.

No game content is distributed in this zip. The engine requires, and reads at runtime,
game files from your own legally-obtained copy of Crimson Skies. Nothing in this zip will
run without it.

## License

The CSVM engine is licensed under the **GNU General Public License, version 3 or later**
(see `LICENSE` in this folder). Source code: <https://github.com/Laeresh/CSVM>, at the
commit `BUILD-INFO.txt` names.

    Copyright (C) 2026 Gabriel Unmüßig

    This program is free software: you can redistribute it and/or modify it under
    the terms of the GNU General Public License as published by the Free Software
    Foundation, either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful, but WITHOUT ANY
    WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
    PARTICULAR PURPOSE. See the GNU General Public License for more details.

    You should have received a copy of the GNU General Public License along with this
    program. If not, see <https://www.gnu.org/licenses/>.

The bundled extraction tool `tools\unzbd.exe` is built from
[our fork](https://github.com/Laeresh/mech3ax/tree/cs-anim) of
[mech3ax](https://github.com/TerranMechworks/mech3ax), which is licensed under the
**EUPL-1.2** (see `LICENSE-unzbd` in this folder) and carries its own license terms. It is
a separate tool, not linked into the engine.

## AI assistance disclosure

This project is developed with the help of AI coding agents. Parsers, engine code and
documentation are AI-assisted and human-reviewed.
