# Extraction

The in-engine extraction: finding the player's Crimson Skies install, decoding its UI archives,
running the bundled unzbd over its ZBD archives, and stamping the tree. No type here touches the
engine except `RememberedInstall` through `OptionsStore.UserOptions`, which is what lets a plain
unit test run every decoder, and lets the same code run on any platform the engine exports to.

One `## src/...` entry per module, body at most 8 lines.

Traps do not live here; the rule is in `docs/architecture.md`. The output layout is in
[../formats/extraction.md](../formats/extraction.md), the archive and texture formats in
[../formats/rof.md](../formats/rof.md), the string table in
[../formats/strings.md](../formats/strings.md).

## src/Extraction/InstallLocator.cs
Finds the install and every file in it without regard to case. `ResolveDirectory` and
`ResolveFile` walk a relative path one segment at a time and answer the disk's spelling (an exact
match wins where a case-sensitive folder holds two), never renaming. `IsInstall` is the silent rule:
`ZBD` plus `GOSDATA/ASSETS`. `Check` judges a picked folder, and names the install when the pick sits
inside it or holds it up to two levels down. `Candidates` lists valid installs: the remembered path,
then Program Files and each fixed drive on Windows, or Wine and every Proton prefix on Linux,
folded through links. Read `RememberedInstall.cs` next.

## src/Extraction/RememberedInstall.cs
The install folder the last extraction read, stored as `OptionsDef.InstallPath` in
`user://options.json`. `Get` and `Set` take an `OptionsStore` for tests, and default to
`OptionsStore.UserOptions()`, which needs the engine. A save loads first and changes only this
field; the value is fully qualified, and the store drops one that is not.

## src/Extraction/RofExtraction.cs
`Run` takes a `RofExtractionRequest` of already-resolved absolute paths (both `.rof` archives, the
install's `MPG` folder, `langui.dll`, `language.dll`, the output root, a force flag) and a log
callback. It unpacks `crimson.rof` into the output root and `crimptch.rof` into `_crimptch/`,
skipping an archive whose `ASSETS` folder is already newer unless forced. Then it copies the
cinemas, writes `ui_strings.json` (langui rows first) and runs `MenuLayoutDecoder`. A null or
absent input is logged and skipped. The result carries each archive's counts and the movie count
the version stamp records. Finding the install and writing the stamp belong to the caller.

## src/Extraction/RofArchive.cs
`Walk` lists a `.rof` held in memory as `RofEntry` values, a directory before its contents, paths
joined with `/`, without inflating anything. `ReadMember` inflates one entry to its declared size
and throws when the payload falls short. An implausible node or an unknown entry kind throws
`InvalidDataException` rather than reading garbage. Read `BmTexture.cs` next.

## src/Extraction/BmTexture.cs
`TryDecode` splits a `.BM` into its shading plane and its three paint-slot masks packed as RGB,
or answers null when the bytes are shorter than the planes the header declares. Rows stay in
stored order. `WritePngsBeside` writes `<stem>.png` and `<stem>_mask.png` through `PngWriter`.
The runtime paint code reads the `.BM` itself (`Mech3/PatternLibrary.cs`), so these PNGs are for
inspection.

## src/Extraction/PngWriter.cs
`EncodeRgb` writes 8-bit RGB as a PNG: signature, `IHDR`, one deflated `IDAT` of unfiltered rows,
`IEND`, each chunk with its CRC. `Crc32` is public so a test can check a chunk. It replaces
`System.Drawing`, which is Windows-only, and Godot's `Image`, which needs a running engine.
`Mech3/PngImage.cs` is the decoder the tests read its output back with.

## src/Extraction/PeStringTable.cs
`Read` walks a PE file's resource directory (type, block id, language) to each `STRINGTABLE`
block and returns every used slot by id, ascending. Pure byte parsing over the section table's
RVA map, so the original's DLLs read the same on every platform. A file that is not a PE throws.

## src/Extraction/UiStringTable.cs
`ParseSymbols` reads `RESOURCE.H`'s `#define IDS_...` lines (first definition of an id wins),
`Rows` turns one DLL's table into `UiStringRow` values with the symbol joined and a single-line
`[FONTID]` tag split into `Font`, and `ToJson` writes the array without a BOM. `TextById` is the
first-row-wins text map `MenuLayoutDecoder` joins widget strings against. The runtime reader is
`Mech3/UiStrings.cs`.

## src/Extraction/MovieCopy.cs
`Run` copies every `.mpg` in the install's folder byte for byte under the name the install spells
it, skipping a target already at the source's length and replacing a read-only leftover.
`MovieCopyResult` counts copied and current files and names each of the ten `Expected` movies the
folder lacked, for the report and the stamp.

## src/Extraction/MenuLayoutDecoder.cs
`Run` reads an extracted rof tree and writes `menu_layout.json`; `ReadTree`, `Decode` and `ToJson`
are the pure steps a test drives on fixtures. It stays in the C# 5 subset while `ExtractRof.ps1`
still `Add-Type`s it. The format and every field order are in
[../formats/menu-layout.md](../formats/menu-layout.md); `UI/Menu/MenuLayout.cs` reads the output
at runtime.

## src/Extraction/ZbdExtraction.cs
`Run(installRoot, extractedDir, unzbd, options, progress, cancel)` walks `ZBD` (found through
`InstallLocator`) for `.zbd` in any case, runs `unzbd cs <mode> <in> <out>` on each archive in turn,
then `unzbd cs messages` on the install root's `strings.dll`. An archive fails on a non-zero exit
only, is counted, and the run goes on; a run with no failure stamps the tree. Output keeps the
install's spelling, relative path and base name. Unzip deletes and re-expands a folder older than its
zip, writing entry by entry so the later of two entries differing only by case wins, as
`Expand-Archive` does. Blocks its thread; the caller runs it off the main thread.

## src/Extraction/ZbdPlan.cs
The rules `ZbdExtraction` applies, each testable alone. `ModeFor` maps a lower-cased base name:
`interp` to `interp` (`.json`); `planes`, `gamez` to `gamez`; `soundsh`, `soundsl` to `sounds`;
`zrdr` to `reader`; `rimage`, `texture`, `rtexture<n>` to `textures`; `cam_anim`, `mis_anim` to
`anim` (all `.zip`); anything else is an unknown type, skipped. An output as new as its source is up
to date. `Classify` counts stderr notes: "object3d transform fail" (matrix recomposed inexactly, the
stored one is kept) and "VAL FAIL" or "anim def duplicate anim ref" (anim fields kept as read); every
other line is a warning.

## src/Extraction/ZbdProgress.cs
The types around the runner. `ZbdExtractionOptions` (`Force`, `Unzip`); the player's button uses the
defaults. `ZbdProgress` is one report per step (index of total, output path, `ZbdStep`, mode, notes,
detail), and `Lines()` gives the console lines the script printed. `ZbdExtractionResult` holds the
counts (extracted, up to date, skipped, unzipped, both note kinds), failures, unknown archives,
warnings, the stamp path, `Fatal` when nothing could start, and `Summary(unzip)`.

## src/Extraction/UnzbdTool.cs
unzbd stays a child process, never linked: it is EUPL-1.2 and the engine GPL-3. `FileName` and
`DefaultPath(executableFolder)` give `tools/unzbd.exe` on Windows and `tools/unzbd` elsewhere, the
release layout. `Run` passes arguments as a list and drains stdout and stderr concurrently, since anim
archives write thousands of lines; cancelling kills the process tree. `Identify` reads the
`--version` first line, the SHA-256, and the fork commit when the exe sits at
`<checkout>/target/release/` in a git checkout.

## src/Extraction/ExtractionStampWriter.cs
Writes `extracted/VERSION.json`: `schema` (always `ExtractionStamp.Schema`), `assets` (`script`,
`date` UTC, `unzbdVersion`, `unzbdSha256`, optional `unzbdCommit`) from `WriteAssets`, and `rof`
(`script`, `date`, `movies`) from `WriteRof`. `Merge` keeps every other field, reads an existing file
as text so a BOM from the PowerShell scripts parses, rewrites an unreadable one, and writes UTF-8
without a BOM. `script` is `CSVM`.
