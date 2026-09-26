# CSVM, a Crimson Skies open-source remake (Linux build)

CSVM is a fan-made remake engine for **Crimson Skies** (2000, Zipper Interactive /
Microsoft): a modern engine that plays the original game using your own legally-owned
game files.

**You must own Crimson Skies.** This archive contains no game data of any kind, only the
engine and an extraction tool. Everything you see and hear in the game is read from your
own copy of the original game.

**The Linux build is community-tested, on a best-effort basis.** It is built from the same
source as the Windows build, and a Steam Deck is the machine it is tried on before a
release. Other distributions and graphics drivers are expected to work where they meet the
requirements below, but nobody checks them before a release, so a report from your machine
is how a problem there becomes known.

## Where this download comes from

CSVM is published in one place, the releases page of its repository:

<https://github.com/Laeresh/CSVM/releases>

Every release lists the SHA-256 of its archive. To check the file you downloaded, open a
terminal in the folder holding it and run this, with the name replaced by the archive you
actually have:

```
sha256sum CSVM-v0.1.0-linux-x64.tar.gz
```

The printed hash must match the one on the release page (upper and lower case do not
matter). If it does not, delete the file and download it again from the link above.
Nothing in this archive is signed, so that hash is what tells you this is the build the
project published, rather than something a third party rebuilt or altered.

## Requirements

- **x86_64 Linux.** There is no ARM build.
- **A graphics driver with working Vulkan support.** Vulkan is the only renderer this build
  is made for; there is no Direct3D on Linux, and the build is not tested on Godot's OpenGL
  compatibility renderer, which the engine may switch to on a machine without Vulkan.
  Current Mesa drivers (AMD and Intel) and NVIDIA's own driver provide Vulkan.
- **A copy of the original game's installed folder**, the folder a Windows install of
  Crimson Skies (the original 2000 PC game) creates. "Getting the original game's folder"
  below covers where it can come from.
- **About 1 GB of free disk space** for the game data you extract.
- **For a flight stick only:** the system's SDL2 library, `libSDL2-2.0.so.0`. SteamOS and
  most desktop distributions install it already; elsewhere it is the `SDL2` or
  `libsdl2-2.0-0` package. Without it CSVM runs as usual and reads no flight stick, and the
  log says so on its `sticks: off` line. Gamepads, the keyboard and the mouse do not need it.
  Flight sticks on Linux are untested, so a report from anyone who flies with one is welcome.
- Nothing else. The .NET runtime this engine needs is inside the download and does not use
  the system's ICU library, so there is no runtime or framework to install.

## Unpacking

The archive's files sit at its root, not inside a folder of their own, so make a folder for
CSVM first and unpack into it. From a terminal:

```
mkdir CSVM
tar -xzf CSVM-v0.1.0-linux-x64.tar.gz -C CSVM
```

`tar` keeps the executable permission that `CSVM.x86_64` and `tools/unzbd` carry in the
archive. Put the folder somewhere your user can write to, because the extracted game data
and the logs are written inside it.

If a graphical archive tool unpacked it and `CSVM.x86_64` will not start, restore the two
permissions from a terminal in the CSVM folder:

```
chmod +x CSVM.x86_64 tools/unzbd
```

## Getting the original game's folder

CSVM needs the folder the original game was installed into: the one that holds the `ZBD`
and `GOSDATA` folders side by side. Your copy is only read, never modified. The case of the
folder and file names does not matter, so a copy whose names came out in upper or lower
case works the same.

**Copy an installed folder from a Windows machine.** This is the route the Linux build is
tested with. On a Windows PC where Crimson Skies is installed, the folder is usually
`C:\Program Files (x86)\Microsoft Games\Crimson Skies`. Copy that whole folder to the Linux
machine (a USB stick or a network share both work), for example to
`~/Games/Crimson Skies`.

**Install the game into a Wine or Proton prefix.** CSVM also looks for an install inside
`~/.wine` and inside every Steam Proton prefix, under `Program Files*/Microsoft Games/Crimson
Skies`. That search has not been tried on a real install, and whether the original CD
installer works under Proton or Wine at all is not known. If you try it, please report what
happened at <https://github.com/Laeresh/CSVM/issues>, whether it worked or not.

## Setup: extract, then fly

**1. Extract your game's assets.** This is done once. Start CSVM from a terminal in its
folder:

```
./CSVM.x86_64
```

or by double-clicking `CSVM.x86_64` in your file manager. With no game data yet, CSVM opens
a screen that offers to extract it. The install folder is filled in with the best guess it
found (the last folder you used, then the Wine and Proton places above), and you can choose
another folder instead. Choose the folder that holds `ZBD` and `GOSDATA` side by side; if
you choose a folder inside it or a folder that holds it, CSVM names the folder it expected.
The extracted data lands in an `extracted` folder next to `CSVM.x86_64`, and the menu opens
when it is done.

**2. Fly.** Pick a mode, chapter and plane in the menu.

A later CSVM version may need the data extracted again. When it does, the same screen
appears at startup with the folder you used last filled in, and one confirmation extracts
it again.

## Disk space

Extraction produces about **0.6 GB** in the `extracted` folder. Leave 1 GB free to be safe.

## On Steam Deck

The Deck runs the Linux build directly; do not set a Proton version for it.

1. Switch to Desktop mode, download the archive and unpack it as described under
   "Unpacking", for example into `~/Games/CSVM`. Copy the original game's folder onto the
   Deck as well.
2. In Desktop mode, open Steam and add `CSVM.x86_64` with **Add a Non-Steam Game to My
   Library**, browsing to the CSVM folder. In the new entry's properties, leave the Steam
   Play compatibility tool option off, since `CSVM.x86_64` is a native Linux program.
3. Run the first extraction in Desktop mode, where a keyboard, a mouse pointer and the file
   manager are at hand for choosing the original game's folder. Start CSVM from Steam or
   from the file manager and follow "Setup" above.
4. After that, start CSVM from Game mode like any other entry in your library.

The Deck's screen is 1280x800. How the built-in controls and Steam Input behave in CSVM is
part of what the community testing covers; report what you find.

## If something goes wrong

Logs are written to `logs/` next to `CSVM.x86_64`, one file per run, named for the mode and
the time it started. The newest file there is the run you just did, and its first line
states the build version. That same version is in the bottom-right corner of the menu.
Starting `./CSVM.x86_64` from a terminal also shows the engine's own startup output, which
includes messages from before the log file opens.

Report a problem at <https://github.com/Laeresh/CSVM/issues>, through the bug report form.
Say that you are on Linux, and which distribution or a Steam Deck. The form asks for the
build version, what you did, your graphics card and the newest log file from `logs/`, which
is usually all it takes to identify the fault; attaching that log answers most of the form
by itself. CSVM is written by one person, so a report is read and worked on in its issue
rather than answered to a schedule.

Two failures answer themselves:

- **Extraction stopped with an error.** The message names the step that failed and the
  folder it was working on. That text is the useful part of a report about extraction.
- **The game disappears after it starts, or draws nothing.** Open the newest file in
  `logs/` and find the line beginning `[perf] gpu=`. It names your graphics adapter and the
  renderer in use. An adapter named `llvmpipe` means Mesa is rendering in software with no
  graphics card driving it, and this build cannot fly a mission that way. The usual cause
  is a missing Vulkan driver for your graphics card (on many distributions a separate
  package, such as `mesa-vulkan-drivers` or NVIDIA's driver), or a virtual machine without
  graphics passthrough.

## Where your files live

The engine, your `extracted` game assets and the `logs` folder all sit in the CSVM folder,
wherever you unpacked it. Your settings, control bindings, campaign profiles, stunt scores,
custom planes and the install folder you last extracted from are kept outside it, in:

```
~/.local/share/godot/app_userdata/CSVM
```

(or under `$XDG_DATA_HOME/godot/app_userdata/CSVM` when `XDG_DATA_HOME` is set). Deleting
those two folders removes everything CSVM has written; your copy of the original game is
never modified.

## What else is in this folder

- `CSVM.x86_64` and the `data_CSVM_linuxbsd_x86_64` folder beside it are the engine. They
  belong together; moving one without the other breaks the build.
- `tools/unzbd` is the extraction tool the engine runs to unpack the game's archives.
- `LICENSE` is the GNU GPL v3 the engine is under, and `LICENSE-unzbd` the EUPL-1.2 the
  bundled extraction tool is under.
- `LICENSE-thirdparty.txt` carries the copyright notices for the software built into those
  two binaries: the Godot engine and its own third-party components, the bundled .NET
  runtime, and the Rust libraries inside `tools/unzbd`. It also carries the notice for
  `pl_mpeg`, the MIT-licensed library CSVM's video decoder is ported from, and the SIL Open
  Font License for PromptFont, the font the controller button pictures are drawn from.
- `BUILD-INFO.txt` records which commit each of the two shipped binaries was built from.
  Quote it in a bug report when you are not sure which build you have.

## Legal

This is an unofficial fan project, **not affiliated with, endorsed by, or sponsored by
Microsoft or Zipper Interactive**. "Crimson Skies" and all related names, marks and
artwork are the property of their respective owners.

No game content is distributed in this archive. The engine requires, and reads at runtime,
game files from your own legally-obtained copy of Crimson Skies. Nothing in this archive
will run without it.

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

The bundled extraction tool `tools/unzbd` is built from
[our fork](https://github.com/Laeresh/mech3ax/tree/cs-anim) of
[mech3ax](https://github.com/TerranMechworks/mech3ax), which is licensed under the
**EUPL-1.2** (see `LICENSE-unzbd` in this folder) and carries its own license terms. It is
a separate tool, not linked into the engine.

## AI assistance disclosure

This project is developed with the help of AI coding agents. Parsers, engine code and
documentation are AI-assisted and human-reviewed.
