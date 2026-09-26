# Linux port, in-engine extraction and a Linux build

**ACTIVE PLAN** (written 2026-09-26). It sits in `docs/`, which by this repo's convention makes it
a live plan; PROJECT_CONTEXT.md's "Current status" names it. When every item lands, the closing
commit deletes this file, records the completion in its message, and clears the "Current status"
pointer; any live prose linking this file by path is unlinked in the same commit.

This plan makes CSVM playable on Linux, with the Steam Deck (SteamOS, copied install folder) as the
reference device, and supported at a best-effort level: a published `.tar.gz` labelled
community-tested. It gets there in two steps. Wave A moves asset extraction out of the PowerShell
scripts and into the engine on both platforms, and ships that in a Windows release first. Wave B adds
the Linux export, a Linux `unzbd` built in WSL, the `.tar.gz`, a pre-release Linux check, and the
per-platform SDL2 load once `PLAN-flight-sticks` has landed.

Out of scope: AppImage and Flatpak packaging (the data root stays next to the executable), any
Steam Deck-specific code (detection, presets, a Deck profile), Lutris/Bottles/removable-media install
search, a Linux run in the landing gate, and verifying that the original CD installer works under
Proton (the author tests with a copied install folder, so the Proton prefix search ships untested).
No item is drawn from `backlog.md` or a `backlog` GitHub issue; everything here comes from one
grilling session and the file facts read during it.

## Milestone goal

- A player on Windows or Linux extracts their game data from inside CSVM: an **Extract** button on
  the no-data screen, a folder picker pre-filled with a best guess, a progress screen. No
  PowerShell, no `Extract.cmd`.
- A data tree that is missing or stamped with another schema brings up a screen that offers
  re-extraction from the remembered install path, in one press.
- `CSVM --extract=<install>` does the same headless, with the development options `--extract-force`,
  `--extract-unzip` and `--unzbd=<path>`.
- A `CSVM-v<version>-linux-x64.tar.gz` ships beside the Windows zip, with a static (musl) `unzbd`
  and a Linux README, and is checked in WSL before each release.
- The install folder is found regardless of the case of its folder and file names.

**One extraction implementation, in the engine.** The scripts that remain are wrappers that hold no
logic. A second implementation for one platform is the drift this plan exists to remove.

## Decisions (2026-09-26)

| # | Question | Decision |
|---|---|---|
| 1 | Support level for Linux | **Best effort**: a published build labelled community-tested, no Linux check promised beyond B14; the author's Steam Deck is the reference device. |
| 2 | Where extraction lives | **In the engine, for both platforms**: an Extract button on the no-data screen plus headless `--extract=<install>`. SteamOS has no PowerShell and a read-only system partition. |
| 3 | Fate of the PowerShell scripts | **One thin dev wrapper, repo-root `Extract.ps1`, calling `--extract`.** `ExtractAssets.ps1`, `ExtractRof.ps1`, `Extract.cmd` and `packaging\Extract.ps1` are deleted and none ship. |
| 4 | Linux download format | **`.tar.gz` folder**, the Windows zip's twin; `extracted/` stays next to the executable. AppImage/Flatpak only if asked for later. |
| 5 | How the install is found | **Picker always, pre-filled with a best guess**: last-used path, today's Windows candidates, `~/.wine/drive_c/Program Files*/Microsoft Games/Crimson Skies`, and every Steam Proton prefix. Valid = holds `ZBD` and `GOSDATA/ASSETS`, compared case-insensitively. |
| 5b | Route the author tests on the Deck | **Route 1, a copied install folder.** The Proton search ships untested; the Linux README asks players to report on it. |
| 6 | Where the Linux `unzbd` is built | **In the author's WSL Debian**, rustup with the `x86_64-unknown-linux-musl` target, called from `ExportRelease.ps1`. |
| 7 | `--extract` options | **`--data-root=`, `--extract-force`, `--extract-unzip`, `--unzbd=<path>`; `-Raw` dropped.** The in-game button always runs player defaults (zips only, incremental). |
| 8 | Missing or out-of-date data | **A screen that asks first**, remembered install path pre-filled, Extract as the default. Path stored in `app_userdata`. A newer-than-expected stamp gets the same screen with reversed wording. Unstamped dev trees keep warn-only. |
| 9 | Release order | **Windows first** with in-engine extraction; Linux follows after the Deck test passes and flight-sticks has landed. |
| 10 | Where the Linux run is checked | **Before each release only** (B14). Portable logic is covered by `CSVM.Tests` in the landing gate. |
| 11 | Steam Deck-specific work | **None.** The Deck test turns findings into backlog issues; the README gets an "On Steam Deck" section. |
| - | Settled by default, not asked | `unzbd` stays a separate process, never linked (the EUPL/GPL separation `packaging/README.md` states); the stamp schema becomes one engine constant; SDL2 resolved per platform after flight-sticks lands; the bug report form gains an OS field. |

## Ground rules

- **Original-game data drives everything.** Read the reader/compiled JSON before writing a handler;
  never guess a value. Inventing content is the trap this project falls into most often.
- **Evidence is a lead to verify, not a finding to implement.** Confirm every claim against the
  data/code before building on it; **a correct disproof that lands no code is a success here**, not a
  failure. Mark each item's Evidence with its confidence (traced-to-code / direction-sound-magnitude-
  TUNE / lead-only).
- **`PROJECT_CONTEXT.md` + the module's entry in `docs/architecture/<Namespace>.md` (plus its index
  bullet in `docs/architecture.md`) / `docs/formats/` are updated in the same turn** as each landed
  item; a landed item gets its record in the landing commit's message and is **deleted** from
  `backlog.md` (not marked FIXED there). New decodes land with their `docs/formats/` page.
- **Read `docs/verification.md` before measuring anything**, the instruments here mislead; cite the
  rule that bites per item.
- **Verify against a full 8-chapter `--freecam --chapter=<X>` regression** (zero errors, same
  mesh/node counts unless the change is meant to add coverage) plus a targeted capture at the
  location the report came from.
- **Read the module's entry in `docs/architecture/<Namespace>.md` (found through the index in
  `docs/architecture.md`) before modifying it,** then the comments on the members you touch; dead
  ends are in the landing commits (`git log --grep=<ID>`), so search those before re-chasing one.

## Checklist

Statuses: ☐ open · ◐ in progress · ☑ done · ❌ closed/disproven. **Keep this in sync as items land.**

### Wave A, in-engine extraction (Windows release)

1. ☑ Extraction decoders move into `CSVM/src/Extraction/`, engine-side and platform-neutral
2. ☐ `unzbd` runner: per-archive modes, messages, MPG copy, incremental skip, VERSION.json stamp
3. ☑ Install discovery and case-insensitive install lookup, remembered path in `app_userdata`
4. ☐ Headless `--extract=<install>` and its development options
5. ☐ Extraction UI: Extract button, picker, progress, and the out-of-date-data screen
6. ☐ Retire the scripts: `Extract.ps1` wrapper, one stamp constant, release payload, docs, bug form
7. ☐ Windows release with in-engine extraction, through the Sandbox release test

### Wave B, Linux build

11. ☐ Linux `unzbd`: WSL toolchain and a musl build called from `ExportRelease.ps1`
12. ☐ Linux export preset and `.tar.gz` packaging with executable bits
13. ☐ Linux README with an "On Steam Deck" section
14. ☐ Pre-release Linux check in WSL: extract, then a headless mission load
15. ☐ SDL2 stick bridge resolved per platform (after `PLAN-flight-sticks` lands)
16. ☐ Steam Deck test pass and the first Linux release

## Dependency and parallelism notes

A1 blocks A2, A4 and A5 (they call the decoders). A3 is independent of A1 and A2 and can run in
parallel with them. A4 needs A1 to A3. A5 needs A3 and A4's entry point. A6 needs A4 (the wrapper
calls it) and A5 (the engine messages point at the button). A7 closes Wave A.

Wave B starts after A7 ships (Decision 9). B11 and B12 can run in parallel; B13 needs B12's layout;
B14 needs B11 and B12. B15 is blocked on `PLAN-flight-sticks` landing on main and touches only that
plan's SDL2 bridge. B16 needs every other item.

File contention: A2 and A6 both edit `ExportRelease.ps1`'s payload list; A5 and A6 both edit the
messages in `ExtractionStamp.cs` / `NoGameDataScreen.cs`; B11, B12 and B14 all edit
`ExportRelease.ps1`. Don't run those pairs in parallel worktrees.

---

# Wave A, in-engine extraction (Windows release)

## A1 ☑ Extraction decoders move into `CSVM/src/Extraction/`, engine-side and platform-neutral

**Landed.** `CSVM.Extraction` holds everything `ExtractRof.ps1` does except the `VERSION.json`
stamp, one module per format: `RofArchive` (the `.rof` walk and inflate), `BmTexture` and
`PngWriter` (the shading and `_mask` PNGs), `PeStringTable` (the `STRINGTABLE` reader),
`UiStringTable` (the `RESOURCE.H` join, the `[FONTID]` split and `ui_strings.json`, langui rows
first), `MovieCopy` (the verbatim `.mpg` copy, idempotent on length, with the missing-movie
report) and `MenuLayoutDecoder` (moved by `git mv` from `ExtractRof.MenuLayout.cs`, API
unchanged, still C# 5 because `ExtractRof.ps1` `Add-Type`s it until A6). `RofExtraction.Run`
joins them; see the wiring contract below. `CSVM.Tests` no longer links a root file and tests the
engine code directly (`ExtractionDecoderTests`, `RofExtractionTests`, `MenuLayoutDecoderTests`),
including a metadata check that nothing under `CSVM.Extraction` references a `Godot.` type.
`ExtractRof.ps1` loads the decoder from `CSVM\src\Extraction\MenuLayoutDecoder.cs`, falling back to
`ExtractRof.MenuLayout.cs` beside itself; `ExportRelease.ps1` ships the moved file under that old
name, so the release layout is unchanged.

The PNG writer is a managed encoder over `ZLibStream` (`PngWriter.cs`). `CSVM.Tests` references
the engine assembly but runs without a Godot runtime, so `Godot.Image` cannot be constructed
there; the engine already had a managed PNG decoder (`Mech3/PngImage.cs`), which the tests use to
read the writer's output back.

Wiring contract for A2, A4 and A5: `RofExtraction.Run(new RofExtractionRequest(baseRof, patchRof,
mpgFolder, languiDll, languageDll, outputRoot, Force: bool), log)` where each input is an
absolute path or null, and `log` is an `Action<string>` receiving one line per step. It returns a
`RofExtractionResult`: per-archive `RofArchiveResult` (`Absent`, `UpToDate` or `Extracted`, with
counts), `Movies.Present` (the stamp's `movies` field), `StringRows` and the `MenuLayout`
document. It runs synchronously; the caller owns threading, install lookup and the stamp.

**Verified.** <pending orchestrator run>

Output comparison, run by the item agent: `ExtractRof.ps1 -Source <install>\GOSDATA\ASSETS -Dest
.scratch\old\rof` against `RofExtraction.Run` into `.scratch\new\rof` (the opt-in test
`RofExtractionTests.ExtractTheInstallIntoTheNamedFolder` with `CSVM_ROF_EXTRACT_TO` set). Both
trees hold the same 1,227 files; the 368 decoded PNGs are pixel-identical (decoded through GDI+
in a scratch script, with a mask-versus-shading control showing the comparison sees a
difference); the other 857 files are byte-identical; `menu_layout.json` is byte-identical and
`ui_strings.json` semantically equal (1,283 rows, the old file with a BOM and the new one
without). The runtime reader `UiStrings.TryLoad` reads with a UTF-8 decoder that accepts both.

**Original approach (kept for reference).**

**Goal.** The `.rof` reader, the `.BM` decoder (shading map and paint masks to PNG), the Win32
STRINGTABLE reader and the menu-layout decoder are ordinary C# in the engine project, producing the
same files `ExtractRof.ps1` produces today, with no `System.Drawing`.

**Evidence (confidence: traced for the locations, lead-only for the approach).** The `.BM` decoder
writes PNGs through `System.Drawing.Bitmap` (`ExtractRof.ps1:222`, `SaveBgr`), which does not run on
Linux under .NET 6+ (not verified in this session). The `.rof` reader
(`ExtractRof.ps1:109-190`, `DeflateStream`), the PE STRINGTABLE reader (`ExtractRof.ps1:245-314`,
pure byte parsing, no Win32 API) and the menu-layout decoder (`ExtractRof.MenuLayout.cs`, compiled by
`ExtractRof.ps1:320` and by `CSVM.Tests\CSVM.Tests.csproj:34`) are already portable C#. The MPG copy
is `ExtractRof.ps1:371-416`; the `ui_strings.json` and `menu_layout.json` writes are
`ExtractRof.ps1:461-479`.

**Approach.** Move the C# out of the here-string and `ExtractRof.MenuLayout.cs` into
`CSVM/src/Extraction/`, one module per format. Replace `SaveBgr` with a PNG writer.
`CSVM.Tests` stops linking `..\ExtractRof.MenuLayout.cs` and tests the engine code directly. Compare
the new output against a tree extracted by the current scripts before deleting anything.
The PNG writer is a small managed encoder over `ZLibStream` (resolved; see **Landed**).

**Model recommendation.** Opus, as run.

**Verify.** Byte- or pixel-identical output against the current `ExtractRof.ps1` tree for every
decoded `.BM`, `ui_strings.json` and `menu_layout.json` (JSON compared semantically if key order
changes). The comparison command is under **Verified** above.

**⚠ Traps.** `ui_strings.json` is written today with PowerShell 5.1's `-Encoding UTF8`, which adds a
BOM, and `ExtractionStamp.cs:55` reads text for that reason; a new writer without a BOM is fine for
readers that use text APIs but check every reader. `langui.dll` is read before `language.dll` and wins
a duplicate id (`ExtractRof.ps1:469`); keep that order.

## A2 ☐ `unzbd` runner: per-archive modes, messages, MPG copy, incremental skip, VERSION.json stamp

**Goal.** The engine walks the install's `ZBD` tree and runs the bundled `unzbd` on each archive as a
child process, producing the same `extracted/` tree `ExtractAssets.ps1` does, and writes
`extracted/VERSION.json`.

**Evidence (confidence: traced for the current behaviour, lead-only for the approach).** The mode map
is `ExtractAssets.ps1:99-111` (planes/gamez → `gamez`, soundsh/l → `sounds`, zrdr → `reader`,
rimage/texture/rtextureN → `textures`, cam_anim/mis_anim → `anim`); the call is
`ExtractAssets.ps1:167` (`unzbd cs <mode> <in> <out>`); the messages step is
`ExtractAssets.ps1:239`; the stamp fields (unzbd version line, SHA-256, fork commit) are
`ExtractAssets.ps1:285-312`. The release ships the tool at `tools\unzbd.exe`
(`ExportRelease.ps1:67`).

**Approach.** A runner that takes the install root, the data root and the unzbd path, runs off the
main thread, and reports progress per archive for A5. Tool name `unzbd.exe` on Windows and `unzbd`
on Linux. unzbd's stderr is diagnostics, not failure (`ExtractAssets.ps1:162`); failure is the exit
code. Keep the anim reader's "INTERVAL VAL FAIL" / "DELTA VAL FAIL" notes as notes
(`ExtractAssets.ps1:178`). `--extract-unzip` expands each zip into its sibling folder.
<TODO: whether archives run in parallel or in sequence>

**Model recommendation.** <TODO: not settled in session>

**Verify.** A fresh extraction by the engine and one by today's scripts give the same file list and
identical zip contents. <TODO: exact comparison command> Then the full 8-chapter `--freecam`
regression on the engine-extracted tree.

**⚠ Traps.** `unzbd` must stay a separate process: `packaging/README.md` states it is not linked into
the engine, which is what keeps the EUPL-1.2 tool separate from the GPL engine. The loaders prefer an
unpacked sibling folder over its zip (`SessionPaths.cs:28`), so a dev tree extracted with
`--extract-unzip` and then re-extracted without it can mix vintages; test with `--zip-assets`.

## A3 ☑ Install discovery and case-insensitive install lookup, remembered path in `app_userdata`

**Landed.** `CSVM/src/Extraction/InstallLocator.cs` holds the lookup, engine-free.
`ResolveDirectory(root, rel)` and `ResolveFile(root, rel)` walk each segment of a `/`- or
`\`-separated path by enumerating the folder, answer the disk's spelling (an exact match wins over a
case-folded one), and return null when a segment is absent. `IsInstall(folder)` is Decision 5's rule.
`Check(picked)` returns an `InstallCheck` (`Kind`, `Folder`, `InstallRoot`, `Message`): `Install`,
`Missing`, `InsideInstall` and `HoldsInstall` (both name the install found, searched up the ancestors
and two levels down), `NoZbd`, `NoAssets`, and `EmptyZbd` (no `.zbd` under `ZBD`, the incomplete-install
check `packaging\Extract.ps1` makes). Every message ends on "the folder that holds the ZBD and GOSDATA
folders side by side". `Candidates(InstallSearchRoots, remembered)` returns valid installs in order:
the remembered path; on Windows `<Program Files>\Microsoft Games\Crimson Skies` for each of
`ProgramFiles`, `ProgramFiles(x86)`, `ProgramW6432`, then `Microsoft Games\Crimson Skies`,
`Games\Crimson Skies` and `Crimson Skies` on each ready fixed drive; on Linux
`~/.wine/drive_c/Program Files*/Microsoft Games/Crimson Skies`, then the same under every
`pfx/drive_c` in `~/.local/share/Steam/steamapps/compatdata` and `~/.steam/steam/steamapps/compatdata`.
Duplicates fold through links (`ResolveLinkTarget` per segment). `InstallSearchRoots.ForThisMachine()`
is the production set; tests pass their own. The remembered path is the new `OptionsDef.InstallPath`
in `user://options.json` (dropped on load unless fully qualified), read and written through
`CSVM/src/Extraction/RememberedInstall.cs`. `CSVM.Tests/InstallLocatorTests.cs` covers the walk over
`Gosdata/assets`, `zbd`, `BINARIES/LANGUI.DLL`, exact-over-folded in a case-sensitive folder
(`fsutil file setCaseSensitiveInfo`, which works under `%TEMP%` on the dev machine's C: drive and is
refused on Z:), both mis-picks, the Windows order, a fake home with two Proton prefixes, and the two
Steam roots folded through a junction.

**Verified.** <pending orchestrator run>. Owed to A4: an extraction from a copy of the install with
lower-cased folder names inside a case-sensitive folder, once `--extract` exists.

**Original approach (kept for reference).**

**Goal.** Given a folder, the engine decides whether it is a Crimson Skies install and finds every
file extraction needs, whatever the case of the names. It offers a best-guess install folder and
remembers the one last used.

**Evidence (confidence: traced for today's Windows rules, lead-only for the Linux candidates).**
Today's validity rule is `ZBD` plus `GOSDATA\ASSETS` (`packaging\Extract.ps1:57-58`); the Windows
candidates are `Program Files\Microsoft Games\Crimson Skies` and `<drive>:\Microsoft Games\Crimson
Skies` (`packaging\Extract.ps1:68-74`). The scripts use literal names `crimson.rof`, `crimptch.rof`,
`GRAPHICS\MPG`, `BINARIES`, `langui.dll`, `language.dll` (`ExtractRof.ps1:324-437`). The runtime
already resolves cinemas case-insensitively (`SessionPaths.cs:84`). The Proton prefix location
(`~/.local/share/Steam/steamapps/compatdata/*/pfx/drive_c/...`) is from discussion, not checked on a
Deck.

**Approach.** One resolver that walks each path segment with a case-insensitive directory match, used
for every install-side lookup. Candidate list per Decision 5. The remembered path goes into the user
settings in `app_userdata`. Unit tests in `CSVM.Tests` with fixture folders spelled `Gosdata`, `zbd`,
`LANGUI.DLL` and a fake home folder holding a Proton prefix (Decision 10). The remembered path is
`OptionsDef.InstallPath` in `user://options.json`.

**Model recommendation.** Settled by landing.

**Verify.** The unit tests above; then extraction from a copy of the install with its folder names
lower-cased (on Windows, a case-sensitive directory set with `fsutil file setCaseSensitiveInfo`,
confirmed working under `%TEMP%` on C: and refused on Z:).

**⚠ Traps.** Extraction output keeps the names the install spells (`ExtractRof.ps1:367-369` explains
why the MPG names are not normalised); only the lookup is case-insensitive, never a rename.

## A4 ☐ Headless `--extract=<install>` and its development options

**Goal.** `CSVM --headless --extract=<install>` runs the full extraction and exits with a status code,
honouring `--data-root=`, `--extract-force`, `--extract-unzip` and `--unzbd=<path>`.

**Evidence (confidence: traced for the existing data-root handling, lead-only for the rest).**
`--data-root=` and `CSVM_DATA_ROOT` already set the data root (`Launcher.cs:429-433`). The flags
replace `ExtractAssets.ps1`'s `-Dest`, `-Force`, `-Unzip`, `-Unzbd` and `ExtractRof.ps1`'s `-Force`
(Decision 7). `docs/formats/extraction.md` promises a v0.6.1 rollback through `-Unzbd` with no code
change.

**Approach.** Parse the flags in `SessionSpec`, run A1 and A2 in order, print a summary like today's
scripts, exit non-zero on any failure. Document the flags in `docs/cli.md`.

**Model recommendation.** <TODO: not settled in session>

**Verify.** <TODO: a `--extract` run into a scratch data root, then a headless mission load from it>

**⚠ Traps.** `docs/cli.md` flag bullets are capped at 600 characters by `CheckDocEntries.ps1`.

## A5 ☐ Extraction UI: Extract button, picker, progress, and the out-of-date-data screen

**Goal.** A player with no data, or with data stamped under another schema, sees a screen naming the
problem, with the remembered or guessed install path filled in and Extract as the default. Extract
shows progress and lands in the menu when done.

**Evidence (confidence: traced for today's screens, lead-only for the design).** The no-data screen's
text points at `Extract.cmd` / the scripts (`NoGameDataScreen.cs:40-41`). A stale stamp is a log warning
(`ExtractionStamp.cs:45-74`) plus a refusal in `OriginalAvailability.cs:43`. Script names also appear
in engine messages in `HudFont.cs:50`, `ImpactReticle.cs:37`, `ObjectivesHud.cs:91`,
`PatternLibrary.cs:130` and `LiveryResolver.cs:34`.

**Approach.** Extend the no-data screen with the picker (Godot's own file dialog, usable with a
controller or touchscreen), a progress view fed by A2, and a stale-data variant per Decision 8.
Every message naming a script is reworded to name the Extract button. Unstamped trees keep
warn-only (`ExtractionStamp.cs:26-27`).

**Model recommendation.** <TODO: not settled in session>

**Verify.** <TODO: screenshots of the no-data, stale-data and progress screens for the author's
judgement; the picker driven with a controller only>

**⚠ Traps.** The picker has to be usable with the Deck's controls or touchscreen (Decision 11 keeps
this the one Deck-aware requirement). Look judgements are the author's.

## A6 ☐ Retire the scripts: `Extract.ps1` wrapper, one stamp constant, release payload, docs, bug form

**Goal.** The repo has one extraction implementation. `.\Extract.ps1 [-Unzip] [-Force] [-Unzbd <p>]`
calls the engine; the old scripts are gone; the release carries no scripts.

**Evidence (confidence: traced).** Script names are referenced from 25 files, including
`ExportRelease.ps1` (payload list, `:62-67`), `sandbox\PublicRelease.ps1` (11 references),
`packaging\BuildThirdPartyNotices.ps1`, `.github\ISSUE_TEMPLATE\bug_report.yml` and
`analysis\aim-assist-ttk\Census-RosterDurability.ps1`. The schema is kept in step across
`ExtractionStamp.cs:22`, `ExtractAssets.ps1` and `ExtractRof.ps1`, enforced by
`ExtractionStampTests` (`ExtractionStamp.cs:18-21`).

**Approach.** Write the wrapper, delete `ExtractAssets.ps1`, `ExtractRof.ps1`,
`ExtractRof.MenuLayout.cs`, `packaging\Extract.ps1` and `packaging\Extract.cmd`. Reduce the stamp
schema to `ExtractionStamp.Schema` and retire the three-way test. Update the release payload, the
Sandbox test, `packaging/README.md` (setup section, SmartScreen note for `Extract.cmd`, "What else is
in this folder"), `docs/tooling.md`, `docs/formats/extraction.md`, PROJECT_CONTEXT.md. Add an OS
field to the bug report form.

**Model recommendation.** <TODO: not settled in session>

**Verify.** `rg` for the deleted script names returns only the wrapper and commit history.
<TODO: exact search> The full `.\RunTests.ps1`.

**⚠ Traps.** None known yet.

## A7 ☐ Windows release with in-engine extraction, through the Sandbox release test

**Goal.** A Windows release ships in which a clean machine extracts and flies with no script.

**Evidence (confidence: lead-only).** Decision 9: the new extraction path is proven on Windows
before Linux ships.

**Approach.** The usual release path through `ExportRelease.ps1` and `PublishRelease.ps1`, with the
Sandbox release test driving the in-game Extract flow instead of `Extract.cmd`.

**Model recommendation.** <TODO: not settled in session>

**Verify.** The Sandbox release test passes on the published zip. <TODO: how the Sandbox test drives
the in-game button without synthetic input, or whether it uses `--extract` instead>

**⚠ Traps.** Agents never drive the keyboard or mouse or put a game window in the foreground; the
in-game flow is the author's to click through.

# Wave B, Linux build

## B11 ☐ Linux `unzbd`: WSL toolchain and a musl build called from `ExportRelease.ps1`

**Goal.** `ExportRelease.ps1` produces a static Linux `unzbd` from the same `tools/mech3ax` checkout
as the Windows one.

**Evidence (confidence: traced for the environment, lead-only for the build).** The author's WSL
Debian runs kernel 5.15 WSL2 and has no `cargo`, `rustc`, `dotnet` or `zig` installed (checked this
session). No `cfg(windows)`, `target_os` or `winapi` use was found in the fork's sources; `windows-sys`
appears only in `Cargo.lock`. The fork has not been compiled for Linux yet.

**Approach.** One-time setup: `rustup` plus `rustup target add x86_64-unknown-linux-musl` and
`musl-tools` in Debian, documented in PROJECT_CONTEXT.md's tools setup. `ExportRelease.ps1` calls
`wsl -d Debian -- cargo build --release --target x86_64-unknown-linux-musl` against the checkout
through `/mnt/z/...` and fails with a named setup step if the toolchain is missing, the way it does
for export templates (`ExportRelease.ps1:28-31`).

**Model recommendation.** <TODO: not settled in session>

**Verify.** `unzbd --version` runs in WSL and `unzbd cs gamez` on one chapter gives the same zip
contents as the Windows build. <TODO: exact command>

**⚠ Traps.** A build through `/mnt/z` can be slow and may leave a Linux `target/` beside the Windows
one; <TODO: decide whether to use `CARGO_TARGET_DIR` inside the distro>.

## B12 ☐ Linux export preset and `.tar.gz` packaging with executable bits

**Goal.** `ExportRelease.ps1` also produces `CSVM-v<version>-linux-x64.tar.gz` with the engine,
`tools/unzbd`, the licences and `BUILD-INFO.txt`, and both executables marked executable.

**Evidence (confidence: traced for the templates and preset, lead-only for the rest).** The only
export preset is Windows Desktop (`CSVM/export_presets.cfg:4`). The Godot 4.7 .NET Linux
x86_64 templates are installed in `%APPDATA%\Godot\export_templates\4.7.stable.mono` (checked this
session). The Windows zip name and payload are built in `ExportRelease.ps1:13` and `:62-67`.

**Approach.** Add a Linux x86_64 preset. Build the tarball inside WSL so `chmod +x` on `CSVM.x86_64`
and `tools/unzbd` survives. Record SHA-256 like the Windows zip.
<TODO: whether `PublishRelease.ps1` uploads both assets in one release>

**Model recommendation.** <TODO: not settled in session>

**Verify.** `tar -tvf` shows `rwx` on both executables; B14 runs on the unpacked tarball.

**⚠ Traps.** A zip made on Windows loses the executable bit, which is why this is a `.tar.gz`
(Decision 4).

## B13 ☐ Linux README with an "On Steam Deck" section

**Goal.** The Linux download carries a README that covers requirements (Vulkan only, no Direct3D 12
fallback), setup through the in-game Extract button, the settings folder, and the Deck.

**Evidence (confidence: lead-only).** Decisions 1, 5b and 11. The settings folder on Linux is
`~/.local/share/godot/app_userdata/CSVM` (Godot's default, not checked on a Linux run).

**Approach.** Derive from `packaging/README.md`. The Deck section: add `CSVM.x86_64` as a non-Steam
game from Desktop mode, run the first extraction in Desktop mode. State that the Proton prefix search
is untested and ask players to report whether the CD installer works under Proton. Label the build
community-tested.

**Model recommendation.** <TODO: not settled in session>

**Verify.** <TODO: the author reads it on the Deck while following it>

**⚠ Traps.** The writing-style rules in CLAUDE.md apply to shipped READMEs.

## B14 ☐ Pre-release Linux check in WSL: extract, then a headless mission load

**Goal.** Before a Linux release is published, the built tarball is unpacked in WSL, extracts from the
author's install, and loads a mission headless; any failure stops the release.

**Evidence (confidence: lead-only).** Decision 10. WSL's Vulkan support is too weak to render, so
the check is headless only.

**Approach.** A `sandbox\LinuxRelease.ps1`, the Linux sibling of `sandbox\PublicRelease.ps1`, called
by the release path. It uses `--extract` (A4) and a headless mission load.

**Model recommendation.** <TODO: not settled in session>

**Verify.** The check fails when `tools/unzbd` loses its executable bit and when a payload file is
missing (seen able to fail), then passes on a good tarball.

**⚠ Traps.** <TODO: which headless flag loads a mission end-to-end without a GPU>

## B15 ☐ SDL2 stick bridge resolved per platform (after `PLAN-flight-sticks` lands)

**Goal.** On Linux the stick bridge loads the system `libSDL2-2.0.so.0` or, if absent, runs with no
sticks and one log line, as the flight-sticks plan specifies for a missing `SDL2.dll`.

**Evidence (confidence: lead-only).** `PLAN-flight-sticks` Decision 1 ships `SDL2.dll` through
P/Invoke, and its A1 copies it beside the exe. Its scope names non-Windows builds as out of scope.
That plan's premise is that Godot 4.7's SDL3 misses DirectInput-only devices on Windows; whether
Godot's Linux joypad path already sees those sticks is unknown.

**Approach.** A `NativeLibrary` resolver mapping the P/Invoke name per platform. <TODO: once
flight-sticks has landed, check whether Godot on Linux already lists the sticks; if it does, the
gap-filler finds nothing to fill and the Linux work may reduce to the missing-library path>

**Model recommendation.** <TODO: not settled in session>

**Verify.** <TODO: a stick on the Deck or a Linux desktop, if the author has one available>

**⚠ Traps.** Blocked on `PLAN-flight-sticks` landing on main; the author asked that the plan not be
changed while it is in testing.

## B16 ☐ Steam Deck test pass and the first Linux release

**Goal.** The author installs the tarball on the Deck with a copied install folder, extracts, flies,
and publishes the Linux release; whatever looks wrong becomes backlog issues.

**Evidence (confidence: lead-only).** Decisions 1, 5b, 9 and 11. Expected areas to look at:
16:10 UI layout at 1280×800, frame rate on the Deck GPU, a controller appearing twice (Steam Input's
virtual pad beside the raw device), the picker with the controls.

**Approach.** The author's test at the controls; findings filed as `backlog` issues rather than fixed
in this item unless they block the release.

**Model recommendation.** <TODO: not settled in session>

**Verify.** The author's judgement on the Deck.

**⚠ Traps.** Look judgements are the author's; do not park a visible problem on a measurement alone.
