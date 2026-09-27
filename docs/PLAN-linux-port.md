# Linux port, in-engine extraction and a Linux build

**ACTIVE PLAN** (written 2026-09-26). It sits in `docs/`, which by this repo's convention makes it
a live plan; PROJECT_CONTEXT.md's "Current status" names it. When every item lands, the closing
commit deletes this file, records the completion in its message, and clears the "Current status"
pointer; any live prose linking this file by path is unlinked in the same commit.

This plan makes CSVM playable on Linux, with the Steam Deck (SteamOS, copied install folder) as the
reference device, and supported at a best-effort level: a published `.tar.gz` labelled
community-tested. It gets there in two steps. Wave A moves asset extraction out of the PowerShell
scripts and into the engine on both platforms, proven on Windows by a local test. Wave B adds
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
| 9 | Release order | **One release with both downloads**, after the Deck test passes and flight-sticks has landed. The Windows build is proven by the author's local test instead of a release of its own: the player base is small and mostly holds data already stamped under the current schema, which the new build accepts without asking. |
| 10 | Where the Linux run is checked | **Before each release only** (B14). Portable logic is covered by `CSVM.Tests` in the landing gate. |
| 11 | Steam Deck-specific work | **None.** The Deck test turns findings into backlog issues; the README gets an "On Steam Deck" section. |
| 12 | The Linux build aborts without `libicu`, which .NET needs for culture data | **`InvariantGlobalization` on both platforms**, set in the engine csproj: no native locale dependency in either build, and the engine suites run in the mode that ships. The unit host keeps ICU, because its culture-safety tests build `de-DE`. |
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

### Wave A, in-engine extraction (Windows)

1. ☑ Extraction decoders move into `CSVM/src/Extraction/`, engine-side and platform-neutral
2. ☑ `unzbd` runner: per-archive modes, messages, MPG copy, incremental skip, VERSION.json stamp
3. ☑ Install discovery and case-insensitive install lookup, remembered path in `app_userdata`
4. ☑ Headless `--extract=<install>` and its development options
5. ☑ Extraction UI: Extract button, picker, progress, and the out-of-date-data screen
6. ☑ Retire the scripts: `Extract.ps1` wrapper, one stamp constant, release payload, docs, bug form
7. ☑ Windows build with in-engine extraction, tested locally by the author and in the Sandbox

### Wave B, Linux build

11. ☑ Linux `unzbd`: WSL toolchain and a musl build called from `ExportRelease.ps1`
12. ☑ Linux export preset and `.tar.gz` packaging with executable bits
13. ☑ Linux README with an "On Steam Deck" section
14. ☑ Pre-release Linux check in WSL: extract, then a headless mission load
15. ☑ SDL2 stick bridge resolved per platform (after `PLAN-flight-sticks` lands)
16. ☐ Steam Deck test pass and one release carrying the Windows zip and the Linux tarball

## Dependency and parallelism notes

A1 blocks A2, A4 and A5 (they call the decoders). A3 is independent of A1 and A2 and can run in
parallel with them. A4 needs A1 to A3. A5 needs A3 and A4's entry point. A6 needs A4 (the wrapper
calls it) and A5 (the engine messages point at the button). A7 closes Wave A.

Wave B does not wait for A7; both waves meet in B16's one release (Decision 9). B11 and B12 can run in parallel; B13 needs B12's layout;
B14 needs B11 and B12. B15 is blocked on `PLAN-flight-sticks` landing on main and touches only that
plan's SDL2 bridge. B16 needs every other item.

File contention: A2 and A6 both edit `ExportRelease.ps1`'s payload list; A5 and A6 both edit the
messages in `ExtractionStamp.cs` / `NoGameDataScreen.cs`; B11, B12 and B14 all edit
`ExportRelease.ps1`. Don't run those pairs in parallel worktrees.

---

# Wave A, in-engine extraction (Windows)

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

**One case for the rof tree (found on the Deck).** A Linux build started in Built-in with the data
extracted: the archive's members are upper case (`ASSETS/GRAPHICS/CM_BACKGROUND.PNG`), the install's
movies lower case (`chap0.mpg`), while `menu_layout.json`, the scripts and our own board art name the
same files in any case (`AP_BackGround.png`, `assets/graphics/mp_b_radio.png`, `CrimFlag.MPG`). The
manifest checked each path verbatim, so on a case-sensitive disk it refused 119 of 119 required
files. `Extraction/RofTree.cs` now fixes one case, upper, for every game-named path under
`extracted/rof/`: `RofExtraction` writes members through it, `MovieCopy` copies the movies under
upper-case names and `BmTexture` writes `.PNG`/`_MASK.PNG`. Every reader maps a data name through
the same helper (`OriginalAvailability.ArtPath`/`RelativeArtPath`, `OriginalAsset.PathUnder`,
`SessionPaths.Cinema`, `ComposedBoardView`'s UI art, the scrapbook, cabin, flight-check and hangar
pages, the menu cue folder), so no read scans a directory. EXPORT TO DESKTOP names its copy with
the scrap's own spelling rather than the upper-case file it copies from. `SessionPaths.Cinema` no longer scans the
`MPG` folder. The rimage tree is already lower case from unzbd and its readers lower-case their
names, and `PatternLibrary` scans with case-blind maps, so neither changed. No stamp bump: on
Windows the disk ignores case, and on Linux an older tree already has upper-case members, so only
the (optional) movies are missing until a plain re-extract copies them. That re-extract leaves the
older lower-case `.mpg` copies beside the new ones (106 MB), which can be deleted by hand.
Verified in WSL on a tarball exported from this tree: the old build on the B11 tree logs
`menu presentation active=built-in ... refuses 119 of 119 required files`, the new build on the
same tree `active=original` (two optional movies absent), a fresh `--extract` writes no name with a
lower-case letter outside the authored files and then logs `active=original` with nothing degraded.
Tests: `RofExtractionTests.EveryGameNamedPathIsWrittenUpperCase`,
`MoviesAreCopiedUpperCase...`, `OriginalManifestTests.TheDatasOwnSpellingResolvesToTheNameTheExtractionWrote`,
each comparing names on disk ordinally, which fail on the old code.

**One case for the chapter trees (found on the Deck).** On Linux a campaign mission loaded and flew,
but its load screen showed only the bar (`Loading.zrd has no sheet for c3/m01`) and its briefing
never played. The ZBD half writes the install's own spelling, chapter and mission folders upper case
(`C3/M01/zrdr.zip`), while `CampaignMission.ChapterFolder`/`MissionFolder` are `c3`/`m01`. The flight
path worked because `CampaignDirector` upper-cases both before launching; the load sheet, the pause
aid, the flight-check objectives note and `CampaignBriefing` passed them verbatim to
`SessionPaths.MissionZrdr`. In the briefing the missing `objectives.json` threw after the state was
read, so the catch left no reveal and no narration, and nothing was logged. `Extraction/ZbdTree.cs`
now fixes the case the retail install already uses: folders upper, file names lower.
`ZbdPlan.OutputRelativePath` writes through it and every chapter path reads through it
(`SessionPaths`' four chapter and mission paths, `AnimProgram.ArchivePaths`, the probes' mission
discovery), so a caller may name a chapter in any case. No stamp bump and no re-extract: Windows
ignores case, and an existing Linux tree extracted from the retail install already has this layout.
Entries inside a zip are unchanged, since `Zrdr` looks them up ordinally on every platform, and the
`cloud1.tif`/`cloud2.tif` warning on C3 is the data's own (Windows logs it too). Verified in WSL on
an exported tarball, headless `--run-tests`: on the old B11 tree the old build fails `load-sheet`
(`FAIL c3/m01 resolves loading_c61` and the other campaign missions), `campaign-briefing-note` (all 24
objectives notes empty) and `menu-original-campaign` (no briefing for seq 0); the new build passes all
three on the same tree and on a fresh `--extract`, which writes no chapter folder with a lower-case
letter. Tests: `ZbdExtractionTests.TheOutputTakesUpperCaseFoldersALowerCaseNameAndItsExtension`, the
runner test's on-disk names, `SessionPathsTests.ALowerCaseChapterAndMissionMapToTheCaseTheExtractionWrites`
and `EveryCampaignMissionResolvesToWhereTheExtractionWritesIt`, which fail on the old code.

**One split for game paths (found on the Deck).** The readers name files by Windows install path
(`..\data\c1\m02\zrdr\cutscenes\cabpickup.zrd`), and `System.IO.Path` splits on `\` on Windows only.
`MissionCutscenes` and `AnimProgram.StemOf` turned such a path into `\` form and asked `Path` for
the leaf, so on Linux every leaf was the whole path: no mission found its `cutscenes\` files, and
the shared and chapter anim gates matched no stem, which loaded no reader definition at all.
Nothing was logged beyond the census lines. `Mech3/GamePath.cs` now splits a game path on both
separators on every host, and both callers go through it. The sweep of every other `Path` call and
`\` comparison found no further game path reaching `Path`: the interp script names
(`support\c1\adjust.gw`, compared whole by `Clutter`, `LensFlareRig`, `MissionSetup` and
`TextureArchive.MipBias`) are the same bytes in a Linux extraction, zip entries use `/`, texture,
sound and bitmap names carry no separator, and the remaining backslash values in the data
(`IMAGE_PATH`, `SOUND_PATH`, a C3 node name) are read by nothing. Verified in WSL on an exported
tarball, headless `--run-tests` over a Linux-extracted tree: the old build fails
`dropoff-placement`, `cutscene-handoff-unposed`, `landings-hangar-drop-gate` and
`campaign-coop-dropoff` (empty cutscene lists) and logs `0 reader` defs with 189 shared files
skipped; the new build passes all four with the counts a Windows run logs for the same missions
(C3/M01: 786 defs, 28 reader, 88 shared skipped). Tests: `GamePathTests`, and
`MissionCutscenesTests`' listed-path cases, which compare against literals rather than `Path`;
`GamePathTests.NoSourceNormalisesAPathToBackslashesForTheHostToSplit` fails on the old code on
Windows too.

**Verified.** The full battery passes on the merged branch: units 4,886 (3 data skips), engine 383 of 383, 19 golden shots hash-identical.

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

## A2 ☑ `unzbd` runner: per-archive modes, messages, MPG copy, incremental skip, VERSION.json stamp

**Landed.** Everything `ExtractAssets.ps1` does is engine C# under `CSVM/src/Extraction/`,
engine-free. The MPG copy is A1's, beside the other `.rof`-half outputs. `ZbdExtraction.Run(installRoot,
extractedDir, unzbd, ZbdExtractionOptions(Force, Unzip), progress, cancel)` finds `ZBD` through
`InstallLocator`, runs each `.zbd` (any case) through `unzbd cs <mode>` as a child process, then
`strings.dll` into `messages.json`, expands zips on `Unzip`, and stamps the tree when nothing failed.
It returns a `ZbdExtractionResult` (counts, notes, failures, unknowns, warnings, stamp path, `Fatal`,
`Summary`). The pure rules are `ZbdPlan` (mode map, output naming, up-to-date and unzip rules,
stderr notes); the process and its identity are `UnzbdTool` (`DefaultPath(executableFolder)` gives
`tools/unzbd.exe` or `tools/unzbd`). `ExtractionStampWriter` writes `VERSION.json` without a BOM:
`WriteAssets` for this half, `WriteRof(movies)` for A1's, both merging and both stamping
`ExtractionStamp.Schema`. Archives run in sequence: a full run takes 11 s against the script's 16 s,
so parallel runs would buy little and cost a deterministic progress order. `CSVM.Tests/ZbdExtractionTests.cs`
covers the rules, the stamp merge over a BOM file, and the runner over a fake install with a batch
file standing in for unzbd. The scripts and their `$StampSchema` stay until A6.

**Verified.** The full battery passes on the merged branch: units 4,886 (3 data skips), engine 383 of 383, 19 golden shots hash-identical. Against `ExtractAssets.ps1` with the same fork unzbd, into
two scratch trees: 186 files each with identical names, the two unzbd `.json` outputs byte-identical,
183 zips with identical entry lists and entry contents (59,610 entries), 185 extracted, 172 transform
and 4,714 anim notes in both. A second run left 185 up to date. With unzip, 59,794 files each, all
42,043 unpacked non-JSON files and 17,567 JSON files identical; only `VERSION.json` differs (date and
`script`). Through A4's `--extract`, the 8-chapter `--freecam` regression on an engine-extracted
tree matches the script-extracted one: every chapter exits 0 with 0 errors, equal warning, node and
mesh counts, identical screenshot hashes.

**Original approach (kept for reference).**

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
Archives run in sequence (see **Landed.**).

**Model recommendation.** Settled by landing.

**Verify.** A fresh extraction by the engine and one by today's scripts give the same file list and
identical zip contents: file lists compared case-sensitively, non-zip files by SHA-256, and each zip
opened with `ZipFile.OpenRead` to compare entry names in order and each entry's SHA-256. Then the full 8-chapter `--freecam`
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

**Verified.** Units pass on the merged tree. Through A4's `--extract`, a copy of the install's 199
input files with every name lower-cased, inside a case-sensitive folder under `%TEMP%`, extracts
with exit 0, and a second run reports all 185 archives up to date.

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

## A4 ☑ Headless `--extract=<install>` and its development options

**Landed.** `CSVM/src/Extraction/ExtractionRun.cs` is the one pipeline, engine-free, for `--extract`
and A5's screen. `ExtractionRun.Run(new ExtractionRequest(install, dataRoot, unzbd, Force, Unzip),
progress, cancel)` checks the pick with `InstallLocator.Check` (a non-install or a mis-pick fails
before anything is written; the message names the install, it is not auto-corrected), runs
`ZbdExtraction` into `<dataRoot>/extracted`, then `RofExtraction` into `extracted/rof` with every
input resolved case-insensitively (`RofRequest`), then `ExtractionStampWriter.WriteRof`. It returns an
`ExtractionResult` (install check, both halves' results, failures, warnings, `Succeeded`,
`Summary(unzip)`). Failure rules: any ZBD failure, including a missing unzbd, stops the run before
the `.rof` half; a missing `crimson.rof` fails; a missing `crimptch.rof`, missing movies or no string
rows only warn; unknown archives warn. `ExtractionProgress` carries `ExtractionPhase` (Zbd, Rof,
Done), a fraction (the ZBD half is 0 to 0.85, `ZbdFraction`), the console lines, and the raw
`ZbdProgress`; a `Done` report at 1.0 always closes the run. Cancelling throws
`OperationCanceledException` from either half. `RunToConsole` prints header, lines and summary and
answers 0 or 1. `SessionSpec` parses `--extract=<install>` (`ExtractInstall`; a bare `--extract` is
an empty path the check refuses), `--extract-force`, `--extract-unzip` and `--unzbd=`; each option
without `--extract` warns and is ignored. `--extract` is a scripted mode named `extract` (hidden
window, `extract-*.log`). The `Launcher` skips the boot stamp check, and after the instruments are
built runs `RunToConsole` on a worker thread with per-frame processing off, then quits with its
code, before any world or menu. The unzbd is `--unzbd`, else `tools/unzbd[.exe]` beside an exported
executable, else `tools/mech3ax/target/release/` under the repo root (a worktree passes `--unzbd`).
One change to A2's module: the root `rimage.zip` is always expanded (`ZbdPlan.AlwaysUnzipped`),
because the HUD font, the reticle and the board art read its PNGs loose and `packaging\Extract.ps1`
expanded it for a player's tree. The headless run does not write `RememberedInstall`; that is A5's.
`CSVM.Tests/ExtractionRunTests.cs` covers the case-insensitive `.rof` inputs, the tool default, the
rimage rule, progress order and monotonic fraction, the non-install and missing-tool refusals, a
whole run and its up-to-date re-run over a fake unzbd, a failed and a missing archive, cancel, and
the flag parsing and its stray-option warnings.

**Verified.** The full battery passes on the merged branch: units 4,886 (3 data skips), engine 383 of 383, 19 golden shots hash-identical. With the fork unzbd, `--headless --extract=CrimsonSkiesGame
--extract-unzip --data-root=.scratch\a4-data` exited 0 (185 extracted, 183 unzipped, 172 transform
and 4,714 anim notes; 847 `.rof` files, 184 `.BM` decoded, 10 of 10 movies, 1,283 string rows).
Against `Z:\CSVM\extracted`: 61,021 files each with identical names; 60,651 byte-identical including
all 183 zips, 368 PNGs pixel-identical, and only `VERSION.json` and `rof\ui_strings.json` differ in
bytes, the latter equal row for row. A headless `--damage-test --chapter=C1` from that tree loaded
its mission with no error. A missing install, the install's own `ZBD` folder, a missing `--unzbd`
file and the absent default tool each exit 1 and write nothing. The 8-chapter `--freecam` regression
through `RunProbe.ps1`'s hidden desktop, main tree against the engine tree: every chapter exit 0, no
errors, identical warning, node and mesh counts, and identical screenshots (A2's owed item). A copy
of the install's inputs with every name lower-cased in a case-sensitive `%TEMP%` folder extracted
with exit 0 and re-ran as 185 up to date (A3's owed item); the copy was deleted.

**Original approach (kept for reference).**

**Goal.** `CSVM --headless --extract=<install>` runs the full extraction and exits with a status code,
honouring `--data-root=`, `--extract-force`, `--extract-unzip` and `--unzbd=<path>`.

**Evidence (confidence: traced for the existing data-root handling, lead-only for the rest).**
`--data-root=` and `CSVM_DATA_ROOT` already set the data root (`Launcher.cs:429-433`). The flags
replace `ExtractAssets.ps1`'s `-Dest`, `-Force`, `-Unzip`, `-Unzbd` and `ExtractRof.ps1`'s `-Force`
(Decision 7). `docs/formats/extraction.md` promises a v0.6.1 rollback through `-Unzbd` with no code
change.

**Approach.** Parse the flags in `SessionSpec`, run A1 and A2 in order, print a summary like today's
scripts, exit non-zero on any failure. Document the flags in `docs/cli.md`.

**Model recommendation.** Settled by landing.

**Verify.** A `--headless --extract=<install> --data-root=<scratch> --unzbd=<fork>` run, the tree
compared with the script-built `extracted/` (file lists, SHA-256, zip entries, PNG pixels), then a
headless `--damage-test --chapter=C1` mission load from it and the 8-chapter `--freecam` regression.

**⚠ Traps.** `docs/cli.md` flag bullets are capped at 600 characters by `CheckDocEntries.ps1`.

## A5 ☑ Extraction UI: Extract button, picker, progress, and the out-of-date-data screen

**Landed.** `CSVM/src/UI/Screens/NoGameDataScreen.cs` is the extraction screen, extended in place.
The `Launcher` checks the data root before `BuildMusic` (an open sound archive would hold a stale
tree's files) through `ExtractionFlow.ProblemAt`: no extraction, a run that never finished (the
`ExtractionFlow.UnfinishedMarker` file a screen run writes first and deletes on success), or a stamp
naming another schema (`ExtractionStamp.Standing`, new, with `StampStanding`) stops a menu launch at
the screen; an unstamped or unreadable tree stays warn-only, and a missing one no longer logs the
stamp warning. The engine-free `CSVM/src/UI/Screens/ExtractionFlow.cs`
holds the state: the pre-fill (the remembered install while it is still one, else the first
`InstallLocator.Candidates` entry, else the remembered path), the pick (`InstallLocator.Check`, its
`Message` shown under the field), and `ExtractionRun.Run` on a worker whose progress and outcome cross
to the main thread only through `Tick`, called once a frame. Stale or unfinished data runs with `Force: true` and
adds `Unzip` when the tree already has unpacked siblings the loaders would prefer; missing data runs
the player defaults. Views: the folder field with Choose folder, Extract (focused) and Quit, plus Play
anyway on stale data; the phase, bar, latest line and Cancel (Esc or B); the failures with Try again,
Choose another folder and Quit. Success remembers the install (never in a scripted run), and the
`Launcher` re-resolves the data paths, builds the music and enters the menu in the same process.
`CSVM/src/UI/Screens/InstallPicker.cs` is Godot's own `FileDialog` in folder mode, embedded, with the
left shoulder going up a folder and Y taking the folder shown. The progress and failure views have
their own body text, and `ExtractionRun`'s failure lines say "the game archives" and "the menus and
interface files". `--unzbd=` applies to the screen and no longer warns without `--extract`, and
`--menu=extract-picker[:<folder>]` and `--menu=extract-run[:<install>]` are screenshot aids. The
five engine messages name the Extract screen. Tests: `CSVM.Tests/ExtractionFlowTests.cs`,
`NoGameDataScreenTests.cs`, and the engine suites `extraction-screen` and `extraction-picker`.

**Verified.** The full battery passes on the merged branch: units 4,886 (3 data skips), engine 383 of 383, 19 golden shots hash-identical. The look judgement on the six screenshots and a pad-only picker run on the Deck are the author's.

**Original approach (kept for reference).**

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

**Model recommendation.** Settled by landing.

**Verify.** Screenshots of the no-data, stale-data, progress, failure and picker views for the
author's judgement, and the picker driven with a controller only, both owed to the author.

**⚠ Traps.** The picker has to be usable with the Deck's controls or touchscreen (Decision 11 keeps
this the one Deck-aware requirement). Look judgements are the author's.

## A6 ☑ Retire the scripts: `Extract.ps1` wrapper, one stamp constant, release payload, docs, bug form

**Landed.** The repo has one extraction implementation, `CSVM/src/Extraction/`. Repo-root
`Extract.ps1 [-Install <p>] [-DataRoot <p>] [-Unzbd <p>] [-Unzip] [-Force] [-NoBuild]` is pure
ASCII and holds no extraction logic: it builds the solution, runs the Godot binary `--headless`
with `--extract=<install> --data-root=<root> --unzbd=<tool>` plus `--extract-unzip` /
`--extract-force`, echoes stdout line by line with real std handles (SHELL-10), and exits with
the engine's code. The Godot binary, the default install (`CrimsonSkiesGame`) and the default
unzbd (the fork build) are looked up beside the script, else under `CSVM_DATA_ROOT` like
`RunGame.ps1`; the data root defaults to the script's own folder and never to `CSVM_DATA_ROOT`,
so a worktree run cannot write the primary tree. `ExtractAssets.ps1`, `ExtractRof.ps1`,
`packaging\Extract.ps1` and `packaging\Extract.cmd` are deleted (`ExtractRof.MenuLayout.cs` had
gone in A1), and `MenuLayoutDecoder.cs` loses its C# 5 rule; the code stays as it is. The stamp
schema is `ExtractionStamp.Schema` alone: `ExtractionStampTests` keeps only the behind/current
check, `ExtractionStampWriter` already stamped the constant, and every `ExtractionStamp` message
names the in-game Extract screen, with `Extract.ps1` (`-Force` where the stamp is stale) for a
repo checkout. The Windows zip ships no script (`ExportRelease.ps1`'s `$ReleaseFiles`,
`packaging/MANIFEST.md`); `packaging/README.md` sets up through the in-game screen and keeps only
the `CSVM.exe` SmartScreen prompt; the notices header says the player extracts "with CSVM".
`sandbox\PublicRelease.ps1` runs `CSVM.exe --headless -- --extract="<mapped install>"` in place of
`Extract.cmd` and records `stamped`. The bug form has an OS dropdown (Windows, Linux, Steam Deck,
Other) and extraction options that name no script. Every live mention of the old scripts in
`docs/`, the READMEs, `PROJECT_CONTEXT.md`, `.github/`, engine comments outside A5's files and
the census script now names the engine's extraction, `--extract-unzip` or `Extract.ps1`.

Left for A5 (its files): the messages in `NoGameDataScreen.cs` (and `NoGameDataScreenTests`),
`HudFont.cs`, `ImpactReticle.cs`, `ObjectivesHud.cs`, `PatternLibrary.cs` (message and doc
comment), `LiveryResolver.cs`, plus the `NoGameDataScreen` entry in `docs/architecture/UI.md` and
the `playtest.md` step that expects the screen to name `Extract.cmd`. The stale-stamp message says
"re-extract from the Extract screen", but the up-to-date rule compares file times only, so a
schema bump over an unchanged install needs a forced run: A5's stale-data screen has to extract
with `Force`.

**Verified.** The full battery passes on the merged branch: units 4,886 (3 data skips), engine 383 of 383, 19 golden shots hash-identical. `.\Extract.ps1 -DataRoot .scratch\a6-data -Unzbd
Z:\CSVM\tools\mech3ax\target\release\unzbd.exe` (install from `CSVM_DATA_ROOT`) exited 0: 185
archives extracted, 847 `.rof` files, 10 of 10 movies, 1,283 string rows, and
`extracted\VERSION.json` stamped schema 3 with both halves; `Z:\CSVM\extracted` untouched. The
targeted units (`ExtractionStamp`, `OriginalManifest`, `MenuLayoutDecoder`, `ZbdExtraction`,
`ExtractionRun`, `RofExtraction`, `NoGameData`) pass, 82 with 1 opt-in skip.
`rg -i "ExtractAssets|ExtractRof|Extract\.cmd|packaging.Extract\.ps1"` over the tree returns
this plan, `docs/release-notes-v0.1.0.md` (a shipped release's notes), the old-format stamp
fixture in `ZbdExtractionTests`, and A5's files above. `ExportRelease.ps1` was not run (a full
export); its payload list was read. `sandbox\PublicRelease.ps1` was not run: it needs the Windows
Sandbox, and is A7's release test.

**Original approach (kept for reference).**

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

**Model recommendation.** Settled by landing.

**Verify.** `rg -i "ExtractAssets|ExtractRof|Extract\.cmd|packaging.Extract\.ps1"` returns only
intentional historical mentions (this plan, shipped release notes, an old-format stamp fixture).
The full `.\RunTests.ps1`.

**⚠ Traps.** None known yet.

## A7 ☑ Windows build with in-engine extraction, tested locally by the author and in the Sandbox

**Goal.** An exported Windows zip, built by `ExportRelease.ps1` and not published, in which a clean
machine extracts and flies with no script. It ships in B16's release (Decision 9).

**Evidence (confidence: lead-only).** Decision 9: the new extraction path is proven on Windows
before the release that carries it. A6 switched `sandbox\PublicRelease.ps1` to
`CSVM.exe --headless --extract`, which is how the Sandbox test extracts without synthetic input.

**Approach.** Rerun `packaging\BuildThirdPartyNotices.ps1` for the bundled .NET, export the zip, run
the Sandbox release test on it, then the author unpacks it locally and goes through the in-game
Extract flow (a fresh folder with no data) and a flight.

**Model recommendation.** Orchestrator for the export and the Sandbox run; the in-game flow is the
author's.

**Verify.** The Sandbox release test passes on the exported zip, and the author's local run extracts
from the screen and flies.

**⚠ Traps.** Agents never drive the keyboard or mouse or put a game window in the foreground; the
in-game flow is the author's to click through.

**Verified (orchestrator, Sandbox half).** `packaging\BuildThirdPartyNotices.ps1` regenerates the
Windows notice with no change beyond its platform stamps. `RunSandbox.ps1 -Driver
sandbox\PublicRelease.ps1 -MapReadOnly <install> -Networking` on the zip exported from linux-port at
948be7d9 (196 files) passes every step: the zip carries the mark of the web and the shell unzip
propagates it, SmartScreen is recorded on each shell launch, `CSVM.exe` with no data draws its own
window with no app dialog (the screen that offers to extract), `--headless -- --extract` exits 0 in
15 s with 1,668 files and the stamp, and the menu and a C1 flight run on the machine's own
extraction. The author's local run of the same zip extracts from the in-game screen and flies.

# Wave B, Linux build

## B11 ☑ Linux `unzbd`: WSL toolchain and a musl build called from `ExportRelease.ps1`

**Landed.** `ExportRelease.ps1 -Linux` builds the Linux `unzbd` itself, before the Windows build
starts: a generated LF script run through `wsl -d Debian` checks `~/.cargo/bin/{cargo,rustup}`,
`cc` and `musl-gcc`, and the `x86_64-unknown-linux-musl` target on the toolchain the checkout's
`rust-toolchain.toml` pins (1.91.1), then runs `cargo build --release --locked --target
x86_64-unknown-linux-musl --bin unzbd` in `tools/mech3ax` through `/mnt/z` and copies the binary to
`tools\mech3ax\target\x86_64-unknown-linux-musl\release\unzbd`, which the payload list ships as
`tools/unzbd`. Each missing piece is a named error with its setup command (rustup, `apt install
build-essential musl-tools`, `rustup target add --toolchain <pin> ...`). `-LinuxUnzbd <path>` ships
an existing file instead and skips the build; a named file that is absent is an error. cargo's
stderr is folded into stdout inside the script, because a caller that redirects the export's
streams turns native stderr into a terminating error under `Stop`. The `ConvertTo-WslPath` and
`Write-WslScript` helpers are shared with B12's pack step. The one-time setup is in
PROJECT_CONTEXT.md's `tools/` line and `docs/tooling.md`'s `-Linux` paragraph.
`packaging/BuildThirdPartyNotices.ps1` takes `tools\` from `CSVM_DATA_ROOT` in a worktree, as the
export does, and `packaging/LICENSE-thirdparty.txt` is regenerated for .NET 8.0.31.

**The TODOs, resolved.** `CARGO_TARGET_DIR` is `~/cargo-target/mech3ax` inside the distro: the
build stays out of the checkout's `target/` and off `/mnt/z` for its many small writes (a first
build takes about 45 s, an up-to-date one 2 s). The exact verify command is below.

**Verified.** Engineering checks by the item agent: `unzbd cs gamez
<install>/ZBD/C1/gamez.zbd <out>.zip` (the mode and argument order `ZbdExtraction` uses) run with the
musl build in WSL and with `unzbd.exe` on Windows gives byte-identical zips (5 entries, same names
and order, every entry's SHA-256 equal, whole-file SHA-256 equal); both print the same
transform-precision notes. The musl binary runs from the distro's filesystem. A full
`ExportRelease.ps1 -Linux` run built it, and the tarball's `CSVM.x86_64` and `tools/unzbd` list as
`-rwxr-xr-x`. The unpacked tarball, run in WSL as `./CSVM.x86_64 --headless -- --extract=<install>
--data-root=<distro folder>`, exits 0 with 185 ZBD archives extracted and the `.rof` half done.
Orchestrator check: `ldd` reports the musl `unzbd` as statically linked, the tarball holds 193
files, and the headless extraction takes 23 s and writes 847 `.rof` files (688 MB). B11 changes no
C#, so the battery on 37262923 stands for this tree.

**Original approach (kept for reference).**

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

## B12 ☑ Linux export preset and `.tar.gz` packaging with executable bits

**Landed.** `CSVM/export_presets.cfg` has a `Linux/X11` preset (`preset.1`, x86_64) carrying the
Windows preset's filters, script mode, texture formats, embedded pck, Shader Baker and .NET options;
it is not runnable and has no resource block to stamp. `ExportRelease.ps1 -Linux` is opt-in: a run
without it produces the same Windows zip as before. With it, after the zip, the script exports the
preset into `.scratch\export-linux\` (`CSVM.x86_64` plus `data_CSVM_linuxbsd_x86_64/`, the folder
name Godot 4.7 gives a Linux .NET export), stages `tools/unzbd`, the README, the three licences and
a Linux `BUILD-INFO.txt` (LF endings, Linux file names), and packs
`.scratch\CSVM-v<version>-linux-x64.tar.gz` inside WSL Debian: a copy in the distro's filesystem gets
`0755` on `CSVM.x86_64`, `tools/unzbd` and directories, `0644` on the rest, and is archived
root-owned with entries at the archive root like the zip's. The script reads the two executables'
modes back out of the archive and prints the tarball's SHA-256. `-LinuxUnzbd <path>` names the
Linux `unzbd` (default `tools\mech3ax\target\x86_64-unknown-linux-musl\release\unzbd`, B11's
output); a missing one, a missing `linux_release.x86_64` template or an unreachable WSL distro is a
named error before the build. The Linux payload drops the PowerShell extractors, which SteamOS
cannot run. The README is `packaging/README-linux.md` through `$LinuxReadme` (B13).
`packaging/MANIFEST.md` has the Linux table and `docs/tooling.md` the `-Linux` paragraph. In a
worktree the script now takes `tools\` from `CSVM_DATA_ROOT`, as `RunTests.ps1` does.

**PublishRelease (the TODO, resolved).** One release, two assets, one tag.
`PublishRelease.ps1 -Linux` passes `-Linux` to `ExportRelease.ps1`, checks the tarball exists and
postdates the run like the zip, runs B14's `sandbox\LinuxRelease.ps1` in full on it (a failure
throws before the tag), re-checks the tree and `HEAD` after both, reads `BUILD-INFO.txt` out of the
tarball with `%SystemRoot%\System32\tar.exe -xOf` under the zip's refusals, puts both sizes and
SHA-256s in the generated notes (which link `packaging/README-linux.md#on-steam-deck` at the tagged
commit) and both checksums in the tag message, and passes both paths to `gh release create`. Without
`-Linux` the script's output and notes are unchanged. It finds `tools\mech3ax` from `CSVM_DATA_ROOT`
in a worktree, as `ExportRelease.ps1` does. `sandbox\PublicRelease.ps1` is not called by either
path. Verified with `-DryRun -TagSuffix dryrun` from `a72d203f`: `-Linux` printed both assets and
hashes after the check passed (payload 193 files, extract 21 s, engine 414 passed, 0 failed, 11
headless-only of 426) and exited 0; with a manifest row naming a file the tarball lacks, the payload
stage failed and the run threw "The Linux release check failed ... Nothing was tagged or uploaded"
(exit 1) before the notes; without `-Linux` the script's own lines and the notes matched the
unmodified script's run apart from the zip's hash.

**Open, found while landing.** (1) The self-contained .NET runtime aborts at startup on a system
without `libicu` ("Couldn't find a valid ICU package"), which the author's WSL Debian lacks. Settled
by Decision 12: `InvariantGlobalization` in the engine csproj, so neither build needs a native
locale library and B13's README states no `libicu` requirement. (2) Settled: the tarball ships
`packaging/LICENSE-thirdparty-linux.txt` as `LICENSE-thirdparty.txt`, checked in beside the zip's
and written by `packaging\BuildThirdPartyNotices.ps1 -Linux` from the Linux payload's own inputs:
the `linux-x64` runtime pack, the crate tree for `x86_64-unknown-linux-musl` (which differs from the
msvc tree by `libc`, `addr2line`, `gimli` and `object` in and `windows-sys`, `windows-link`,
`anstyle-wincon` and `once_cell_polyfill` out), the Godot sections checked against the Linux
template's own build string, and a section 9 for musl 1.2.3, the C library Rust 1.91.1's musl
target links statically, from `packaging/LICENSE-musl`. No SDL section, as the tarball ships no
SDL2. Both headers stamp the runtime pack and crate target, and `ExportRelease.ps1`, the notices
script and `sandbox\LinuxRelease.ps1`'s payload stage refuse a notice naming the other platform's
pack, target or file names (`docs/tooling.md`). (3) The Linux export log reports a completed shader
bake; whether the baked pipelines are used on the Deck's driver is B16's to see. (4) Settled: both
notices carry the Rust standard library as section 9 (musl moves to section 10 on Linux), which
the notices script reads from the pinned toolchain: `COPYRIGHT-library.html` as plain text, the
in-tree licence texts from `share/doc/rust/licenses/`, and the crates the target's rust-std rlibs
name that the file omits (std's backtrace crates on musl, none on msvc) from their crates.io
releases. A moved toolchain pin is a refusal. (5) Settled: the tarball's text files are LF, staged
from the committed bytes by `ExportRelease.ps1`'s Linux path, while the zip keeps the checkout's
CRLF; `packaging/LICENSE-musl` is `-text` in `.gitattributes`, byte-identical to musl 1.2.3's
`COPYRIGHT` in every checkout; the Linux check's payload stage fails a top-level text file carrying
a carriage return.

**Verified.** The full battery passes on the merged branch: units 4,886 (3 data skips), engine 383 of 383, 19 golden shots hash-identical.

**Original approach (kept for reference).**

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

## B13 ☑ Linux README with an "On Steam Deck" section

**Landed.** `packaging/README-linux.md` is the tarball's `README.md`: `$LinuxReadme` in
`ExportRelease.ps1` names it, `Copy-ReleaseFiles` stages it at the root of `.scratch\export-linux\`,
and the WSL pack step archives everything in that folder. `packaging/MANIFEST.md`'s Linux table and
`docs/tooling.md` name it. It is derived from `packaging/README.md` with the Windows-only material
removed (`Extract.cmd`, the PowerShell scripts, SmartScreen, Direct3D 12) and covers: the
community-tested, best-effort label with the Steam Deck as the reference machine; `sha256sum`;
requirements (x86_64, Vulkan only, the original game's installed folder, no ICU library to install,
per Decision 12); unpacking into a new folder, since the archive's entries sit at its root, with
`tar` keeping the executable bits and a `chmod +x` fallback; where the original folder comes from
(a copied Windows install first, per Decision 5b; the Wine and Proton prefix search, stated as
untried, with a request to report whether the CD installer works under Proton); the in-game
extraction; an "On Steam Deck" section (non-Steam game from Desktop mode, no Proton version forced,
first extraction in Desktop mode); `logs/`, the `llvmpipe` software-rendering case; the settings
folder `~/.local/share/godot/app_userdata/CSVM` (no `config/use_custom_user_dir` in
`project.godot`, so Godot's default; `$XDG_DATA_HOME` moves it); the payload list and the licences.
The extraction paragraphs match A5's landed screen: the screen offered at startup with no data,
the pre-filled folder, choosing another, the mis-pick message, `extracted` beside the executable,
the menu opening afterwards, and the re-extraction screen for out-of-date data. Controllers on the Deck are left to the community testing (B15 and B16 are
open). The OpenGL wording is hedged: `project.godot` does not set
`rendering/rendering_device/fallback_to_opengl3`, so Godot's default decides whether a machine
without Vulkan switches to the Compatibility renderer, and the README calls that path untested.

**Verified.** No code; the staging was read through. The author reading it on the Deck while following it is owed.

**Original approach (kept for reference).**

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

## B14 ☑ Pre-release Linux check in WSL: extract, then a headless mission load

**Landed.** `sandbox\LinuxRelease.ps1` runs on the host and drives WSL Debian over the tarball
`ExportRelease.ps1 -Linux` built, in three stages with one verdict and a nonzero exit on any
failure. **payload**: the archive listing against `packaging/MANIFEST.md`'s Linux table, parsed
from that file (every named entry present, nothing unlisted at the root, `CSVM.x86_64` and
`tools/unzbd` at `-rwxr-xr-x`). **extract**: the unpacked exe's `--headless -- --extract=<install>`
from `/mnt/z/.../CrimsonSkiesGame` into a fresh data root with the player's defaults, which must exit
0 and stamp `VERSION.json`. **engine** (off with `-NoSuites`): the whole in-engine suite registry run
headless from the exported build, `--run-tests=shard:<i>/<n>` in six parallel processes divided by
`analysis/engine-suite-weights.json` (copied beside the exe, where an export's harness reads it), and
merged RunTests-style: a missing report, a suite run twice, coverage short of the registry or an
unexpected engine error line fails it. The suites that cannot pass headless on any platform are one
table at the top of the script, `$HeadlessOnly`, with the reason for each (11: cloud-field-fade,
clutter-card-depth, crater-carve, the four display-* suites, menu-original-tracer,
menu-screenshot-key, muzzle-flash-rides-muzzle, trail-world-anchor); each was admitted because it
fails identically from the same commit's Windows export run headless. They still run, and a pass is
reported as a stale entry. Beside it, `$HeadlessEngineErrors` allows the two engine error lines a
headless process prints on both platforms (the dummy renderer's `texture_2d_get`, the text server's
size cache). Everything in the distro lives under `~/csvm-linux-check`, wiped per run, with
`XDG_DATA_HOME`/`CONFIG`/`CACHE` per process; the logs and reports come back to
`.scratch\linux-check\<timestamp>\`. `docs/tooling.md` has the section and `PROJECT_CONTEXT.md` the
`sandbox/` pointer. `PublishRelease.ps1 -Linux` runs it on the tarball it exported and stops before
the tag when it fails (B12); `PublicRelease.ps1` is run by hand, not called by `PublishRelease.ps1`.

**The headless flag (the Traps TODO, resolved).** `--run-tests` is the headless mission load: the
suites build real chapter and mission worlds through `TestContext.WithWorld` and
`WorldSession.Build`, and the `campaign-*` suites load missions end to end with objectives, anims
and cutscenes, so running the registry covers more than one mission would. A headless `--fly` has no
quit condition.

**Found while landing (WSL and harness, handled in the script).** (1) The managed side of a Linux
export does not see Godot's own flags in `Environment.GetCommandLineArgs`, so with `--log-file` the
harness reports the engine log unscreened; the check passes no `--log-file` and the harness screens
the per-process `user://logs/godot.log`. (2) A headless export process crashed in teardown after
writing its report (139 or 134 on Linux, 0xC0000005 or 0xC0000374 from the Windows export) whenever a
suite's build threw: `SceneBuilder.BuildSubtree` orphaned the half-built node and its mesh instance,
which outlived the renderer and was freed after it. `BuildSubtree` and `WithEffectStage` now free
what they built before rethrowing, and the `scene-build-throw-frees` suite pins that. The check
fails a shard that crashes at exit, and the RunTests engine stage fails on the editor's
`Pages in use exist at exit` line, which is the same leak seen by a build that survives it. (3) On a zips-only tree 55 suites threw `ObjectDisposedException` on
`ZipArchive`, on Windows as on Linux, a harness bug and not a player one: the harness closed each
world's texture archive with the build while the built scene still read it (library-root copies,
effect stages, flyout bodies, decals), and the battery's unpacked folders served those reads. The
archive now belongs to `TestWorld`, and a folder-backed `TextureArchive` refuses a read after
`Dispose` as a zip does, so the Windows battery fails the same way. The check runs the suites on
the player's zips-only tree with no unzip pass. (4) Minimal WSL Debian lacks `libfontconfig1`, which Godot's
Linux build loads for system fonts; one-time `apt install`, and the script refuses to start
without it. (5) The manifest check cannot see a missing file inside `data_CSVM_linuxbsd_x86_64/`;
the extract stage catches it (the launch fails).

**Verified.** On the tarball from `0f46bd71` the check failed: payload and extract passed, the
engine stage ran 363 of 384 passed with 10 failures, all Linux path-separator bugs. Six in
cutscenes (blacke-drop-cameras, campaign-coop-dropoff, campaign-hangar-handover,
cutscene-handoff-unposed, dropoff-placement, landings-hangar-drop-gate; `MissionCutscenes.cs:90-96`
split mission paths on `\`, which `Path.GetFileName` does not split on Linux) and four in reader
anims (chapter-census, campaign-persistence, carried-state-silent, persist-chain-kill;
`AnimProgram.cs:313` `StemOf`, same cause, so the chapter reader files never matched their
`ANIMATION_DEFINITION_FILE` lists). All ten pass from the Windows export. lp-paths' `da760393` fixes
both; on the tarball rebuilt from it the check passes: payload 193 files against 8 manifest names,
extract 21 s (1,668 files, 688 MB), engine 373 passed, 0 failed, 11 headless-only, 0 harness skips
of 384 in 50 s, 79 s in all (`-NoSuites` 24 s). Seen able to fail, with `-NoSuites` on doctored
copies of the good tarball: `tools/unzbd` at `0644` fails payload ("-rw-r--r-- in the archive") and
extract ("Permission denied" starting `tools/unzbd`); `LICENSE-unzbd` removed fails payload;
`GodotSharp.dll` removed fails extract (exit 139, the .NET plugin initialisation error).
landings-hookup-airframe failed once in the Windows control and passed on Linux; it is flaky, not
listed.
Orchestrator, on linux-port with lp-paths merged: the check on that commit's tarball passes
(payload 193 files, extract 21 s, engine 373 passed, 0 failed, 11 headless-only of 384, 80 s total);
the Windows battery passes (units 5233 passed, 3 skipped; engine 384/384; goldens 19 identical).

**Original approach (kept for reference).**

**Goal.** Before a Linux release is published, the built tarball is unpacked in WSL, extracts from the
author's install, and loads a mission headless; any failure stops the release.

**Evidence (confidence: lead-only).** Decision 10. WSL's Vulkan support is too weak to render, so
the check is headless only.

**Approach.** A `sandbox\LinuxRelease.ps1`, the Linux sibling of `sandbox\PublicRelease.ps1`, called
by the release path. It uses `--extract` (A4) and a headless mission load.

**Model recommendation.** Top tier: the triage needs a Windows headless control to separate
headless-by-construction failures from Linux bugs.

**Verify.** The check fails when `tools/unzbd` loses its executable bit and when a payload file is
missing (seen able to fail), then passes on a good tarball.

**⚠ Traps.** The headless mission load is `--run-tests` (see above). Engine flags go before the
bare `--`; without it an export ignores the game flag and boots the menu.

## B15 ☑ SDL2 stick bridge resolved per platform (after `PLAN-flight-sticks` lands)

**Landed.** The bridge never used `DllImport`, so no `SetDllImportResolver` is needed: `Sdl2Sticks`
loads one library with `NativeLibrary.TryLoad` and binds its exports by name, and the per-platform
choice is the candidate list. `Sdl2Sticks.ForPlatform(windows, ...)` returns the unchanged four
rooted `SDL2.dll` candidates on Windows, and off Windows `LinuxCandidates`: `libSDL2-2.0.so.0`
beside the executable, then the bare soname. `Load` hands a bare name to the system loader and
treats a rooted one as before, so Windows never consults the system search and its log lines read
as they did. A system load logs the file the loader chose, read from `/proc/self/maps`. No library
is one `sticks: off, no libSDL2-2.0.so.0 (tried ...)` warning and a launch without sticks.
`StickPump` picks the platform with `OperatingSystem.IsWindows()`. The hints are unchanged and set
on both platforms: `SDL_JOYSTICK_HIDAPI=0` keeps SDL2 off hidraw (the Deck's built-in controls,
pads Steam drives), `SDL_NO_SIGNAL_HANDLERS=1` keeps SDL's SIGINT/SIGTERM handlers out of Godot,
and the RawInput, WGI and XInput hints are no-ops off Windows. Device GUIDs need nothing: the
layout is SDL's on both, only the log prints them, and bindings key on the model. The gap-filler
gains Linux rules: `StickListing` carries SDL's `SDL_IsGameController` answer (a 24th export,
present in every SDL2), and a `StickRoster` built with `godotReadsGamepads` (off Windows) also skips
any listing SDL maps as a gamepad and any Valve device (vendor `28DE`: the Deck's controls, a Steam
Controller, Steam Input's virtual pad), each with its reason on the `stick skipped:` line. Windows
keeps the model match alone. The tarball ships no SDL2 (below), so its `BUILD-INFO.txt`
has no SDL block, as B12 already built it. `docs/tooling.md`'s "What is Windows-specific" list is
now its "Linux" list; `docs/architecture/Sticks.md`, `docs/cli.md`'s `--dump-sticks`,
`packaging/README-linux.md`'s requirements and `packaging/MANIFEST.md`'s Linux paragraph say the
same.

**The tarball ships no SDL2 (the TODO, resolved).** SteamOS provides
`/usr/lib/libSDL2-2.0.so.0` from sdl2-compat, and every desktop distribution packages SDL2. A copy
in the tarball would be a libsdl-org source build of our own (there is no Linux binary to pin),
linked against whichever glibc and X11/Wayland libraries the build machine had, and on SteamOS it
would replace sdl2-compat's routing through the system SDL3 that Steam configures. The beside-the-exe
candidate stays, so a player on a system without SDL2 can drop one in. Nothing else ships, so the
stick-detection check on real hardware that pins the Windows DLL has no Linux counterpart to repeat.

**Open, found while landing.** (1) The Approach's TODO, whether Godot's joypad layer on Linux
lists a real flight stick, is still unseen, since nobody here has a stick on a Linux machine: if it does, the stick is skipped by model and plays as an ordinary Godot joypad,
without its stick profile, glyphs or stick column. `--dump-sticks` with the stick connected shows
which roster holds it. (2) A system SDL2 that is present but cannot load (a missing dependency)
logs the same "no libSDL2-2.0.so.0" line as an absent one, since `TryLoad` reports no reason.

**Verified.** Units: `StickRosterTests` adds six cases (the Windows list unchanged under
`ForPlatform`, the Linux list, a Linux load with no library returning null and naming the soname,
the Linux gap-filler rules against the Windows ones, and a roster that never opens a gamepad or a
Valve device off Windows); `FakeStickNative.Plug` takes `gamepad:`. The tarball built by
`ExportRelease.ps1 -Linux -ToolsRoot Z:\CSVM` ran headless in WSL Debian
(`./CSVM.x86_64 --headless -- --data-root=<scratch> --dump-sticks`, `XDG_*_HOME` pointed at the
scratch folder). With no SDL2 on the system it logged
`WARN [core] sticks: off, no libSDL2-2.0.so.0 (tried /tmp/csvm-b15/game/libSDL2-2.0.so.0, libSDL2-2.0.so.0)`
and exited 1, which is `--dump-sticks`'s verdict for no library. With Debian's
`libsdl2-2.0-0` 2.32.4 unpacked into the scratch folder and on `LD_LIBRARY_PATH` it logged
`sticks: SDL 2.32.4 from libSDL2-2.0.so.0 (system: /tmp/csvm-b15/sdlroot/usr/lib/x86_64-linux-gnu/libSDL2-2.0.so.0.3200.4)`,
`sticks dump: Godot pad roster models=[]` and `sticks dump: 0 stick(s), SDL 2.32.4`, exit 0: all 24
exports bound and the joystick subsystem started. On Windows the same headless `--dump-sticks`
against the pinned `SDL2.dll` still logs `sticks: SDL 2.32.10 from Z:\CSVM\tools\sdl2\SDL2.dll` and
opens the VKB Gladiator EVO R and the Tartarus, with the 24th export bound. The Steam Deck load is
owed: the Deck did not answer on SSH while this item ran. It is the same headless
`--dump-sticks` run from a scratch folder, expected to log `sticks: SDL 2.32.56 from
libSDL2-2.0.so.0 (system: /usr/lib/...)` and a `stick skipped:` line with the Valve reason for the
Deck's own controls if SDL2 lists them. A real flight stick on Linux is owed to whoever has one.
Orchestrator check on the merged tree (flight sticks from main, the rof case fix, B15): units 5207
passed with 3 skipped, engine 384/384, goldens 19 identical.

**Original approach (kept for reference).**

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

## B16 ☐ Steam Deck test pass and one release carrying the Windows zip and the Linux tarball

**Goal.** The author installs the tarball on the Deck with a copied install folder, extracts, flies,
and publishes one release with both downloads (Decision 9, through `PublishRelease.ps1 -Linux`,
described in B12); whatever looks wrong becomes backlog issues. That release is v0.2.0, which also
carries `PLAN-flight-sticks` and the Milestone 6 multiplayer work, so all three are on main before
the export: flight sticks and multiplayer land first, this branch merges over them, and B15 fixes
the SDL2 load on the merged tree.

**Evidence (confidence: lead-only).** Decisions 1, 5b, 9 and 11. Expected areas to look at:
16:10 UI layout at 1280×800, frame rate on the Deck GPU, a controller appearing twice (Steam Input's
virtual pad beside the raw device), the picker with the controls.

**Approach.** The author's test at the controls; findings filed as `backlog` issues rather than fixed
in this item unless they block the release.

**Model recommendation.** <TODO: not settled in session>

**Verify.** The author's judgement on the Deck.

**⚠ Traps.** Look judgements are the author's; do not park a visible problem on a measurement alone.

**Deck test pass (author, at the controls).** The 16:10 layout, the controllers (the Deck's own and
an 8BitDo pad) and a LAN match between the Deck and the Windows build work, and
the original presentation in single player holds 60 fps. Enhanced graphics and splitscreen fall
well below that (New York about 15 fps with enhanced graphics in single player), filed as
`BL-1051`. Publishing is still owed.
