# Release zip manifest (for the assembler, not the recipient)

The release zip holds these pieces. Everything sits at the zip root except `tools\unzbd.exe`;
the recipient unzips, double-clicks `Extract.cmd`, then `CSVM.exe`.

`ExportRelease.ps1` assembles it: it exports into `.scratch\export\`, copies the rows below in
beside the export output, and zips that folder to `.scratch\CSVM-v<version>-win64.zip`, the version
being `application/config/version` from `CSVM/project.godot` — the same number the exe's file
properties and the first line of every log state. This file is the
statement of what belongs in the zip; the script is what performs it, so a copied row added here
needs a matching entry in the script's `$ReleaseFiles`. Two rows are not copies and say so in
their Source column: the export's own output, and `BUILD-INFO.txt`, whose content differs on
every run and therefore has no repo home to be copied from. Two rows are in the table without
being in the zip at all, `LICENSE-plmpeg` and `LICENSE-promptfont`, because they are inputs the
notices file is assembled from; their Zip path column names them as no zip row and they take no
`$ReleaseFiles` entry.

| Zip path | Source | Notes |
|---|---|---|
| `CSVM.exe` + export payload (`.pck`/`.dll`s etc.) | the Godot release export output | Whatever the export produces at its root, copied verbatim; the exe's file properties carry the version |
| `Extract.cmd` | `packaging/Extract.cmd` | The double-clickable wrapper: runs `Extract.ps1` beside it with the execution-policy switch applied, forwards a dropped folder, and holds the window open on both outcomes |
| `Extract.ps1` | `packaging/Extract.ps1` | The thin dispatcher; contains no extraction logic. Passed no path it probes the usual install locations and offers a folder picker |
| `ExtractAssets.ps1` | repo root `ExtractAssets.ps1` | UNMODIFIED repo script — do not fork a package variant |
| `ExtractRof.ps1` | repo root `ExtractRof.ps1` | UNMODIFIED repo script — do not fork a package variant |
| `ExtractRof.MenuLayout.cs` | repo root `ExtractRof.MenuLayout.cs` | The menu-layout decoder `ExtractRof.ps1` `Add-Type`s from beside itself; without it the extraction fails on the recipient's machine |
| `tools\unzbd.exe` | `Z:\CSVM\tools\mech3ax\target\release\unzbd.exe` | The fork build (branch `cs-anim`), NOT the pinned v0.6.1 binary (Decision 5) |
| `SDL2.dll` | `tools/sdl2/SDL2.dll` | The official libsdl-org SDL 2.32.10 Windows x64 runtime, which the game reads flight sticks through. Installed by `InstallSdl2.ps1` against a pinned SHA-256; the game loads it from beside the exe (`docs/tooling.md`, "SDL2 for flight sticks") |
| `README-SDL.txt` | `tools/sdl2/README-SDL.txt` | From the same release zip, which asks for it to be distributed with the runtime |
| `LICENSE-SDL2.txt` | `tools/sdl2/LICENSE.txt` | SDL's zlib licence, read by `InstallSdl2.ps1` from the release's own commit because the runtime zip carries none; renamed so it does not read as CSVM's licence |
| `README.md` | `packaging/README.md` | The one document a downloader reads: the releases page and the zip's SHA-256, the requirements including the renderer floor, the extraction and first flight, the log and save locations, what the other files here are, and where to report a problem. Author-reviewed before any hand-off or release (standing rule: the author owns outward communication) |
| `LICENSE` | `packaging/LICENSE` | GPL-3, byte-identical to repo root `LICENSE` |
| `LICENSE-unzbd` | `packaging/LICENSE-unzbd` | EUPL-1.2, byte-identical to `tools/mech3ax/LICENSE` |
| `LICENSE-thirdparty.txt` | `packaging/LICENSE-thirdparty.txt` | The notices the two binaries' contents oblige the zip to carry: the Godot engine, its own third-party components, the self-contained .NET runtime, and the Rust crates in `unzbd.exe`, plus one notice for source rather than payload, `pl_mpeg`, which CSVM's managed MPEG-1 decoder is ported from, and the SIL Open Font License for PromptFont, the font packed into the export that the pad glyphs are drawn from. Assembled by `packaging/BuildThirdPartyNotices.ps1` from the shipped artefacts themselves; its header names the three payload versions it speaks for and the export refuses to ship it against any other |
| none, not a zip row | `packaging/LICENSE-plmpeg` | An input to the row above, not a payload: `BuildThirdPartyNotices.ps1` reads it into section 7 of the notices file, so it ships inside that file and never beside it. It holds the MIT terms because `pl_mpeg` declares MIT by SPDX identifier alone and publishes no licence file for the script to read out of an artefact |
| none, not a zip row | `packaging/LICENSE-promptfont` | An input to the notices row, not a payload: `BuildThirdPartyNotices.ps1` reads it into section 8, under the copyright statement it reads out of `CSVM/data/promptfont.ttf.bin`'s own name table. Byte-identical to the `LICENSE.txt` in PromptFont's release, which is the bare OFL text with no copyright line of its own |
| `BUILD-INFO.txt` | GENERATED by `ExportRelease.ps1` | Which commit each shipped binary was built from: CSVM's `HEAD` and the mech3ax `cs-anim` commit, each with whether its worktree was clean, plus whether `cs-anim` is pushed, and the SDL release, commit and DLL hash `SDL2.dll` came from. `PublishRelease.ps1` reads it and refuses a release carrying any of those qualifiers |

Not in the zip, created on the recipient's machine by `Extract.ps1`: `extracted\` (game data —
must never ship; the zip contains zero game assets by construction).

Assembly checks before hand-off. The copies are taken from the sources above on every export,
so byte-identity is by construction and only the unzbd build needs confirming:

- `tools\unzbd.exe` is the fork build: `unzbd.exe --version` shows the fork's build
  timestamp, and its SHA-256 should match the `unzbdSha256` a dev-tree extraction stamps
  into `extracted/VERSION.json`. The script checks the path exists, not which build it is.
- `LICENSE-thirdparty.txt` needs no manual check: the export reads the Godot build, .NET runtime
  version and `cs-anim` commit out of its header and throws if any of the three disagrees with what
  it is packaging. Regenerate with `packaging\BuildThirdPartyNotices.ps1` when one does. ⚠ A moved
  `cs-anim` counts even when the fork's own code did not change, because the file enumerates that
  commit's crate tree. The `pl_mpeg` section carries no such stamp and needs none: it speaks for
  source CSVM's own code was ported from, which moves with CSVM's commit rather than with a payload
  version, and `BUILD-INFO.txt` already records that commit.
- The three SDL2 rows need no manual check: the export runs `InstallSdl2.ps1 -Verify`, which
  throws unless all three match their pinned SHA-256, and `BUILD-INFO.txt` records the SDL version,
  commit and DLL hash.
- Everything in the staging folder goes into the zip, so the script deletes `.scratch\export\`
  at the start of every run: what is in the archive is what that run produced, never a leftover
  from an earlier export or a hand assembly.
