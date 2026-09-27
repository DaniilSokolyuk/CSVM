# Tooling, the extraction pipeline, the launch scripts, and the fork

How game files become `extracted/`, how the game gets launched, and how the mech3ax fork is
maintained. For *which* archive types extract and how far each is validated, see
[extraction](formats/extraction.md); for the engine's own flags, [cli.md](cli.md).

## `extracted/`, the extraction workdir (git-ignored)

Populated by the engine's extraction (below), mirroring the game's own ZBD folder structure:
top-level `planes.zip` (unzbd of `planes.zbd`), `zrdr.zip`, `soundsh.zip`/`soundsl.zip`, `interp.json`,
`rimage.zip`, plus per-chapter `C1/gamez.zip`, `C1/texture.zip`, `C1/rtexture*.zip`, `C1/zrdr.zip`,
and per-mission `C1/IA1/zrdr.zip`.

The viewer's defaults read `planes.zip`, `C1/gamez.zip`, the chapter's top texture tier (below),
`zrdr.zip` and `soundsh.zip`, **preferring the unpacked sibling folder when present**
(`extracted/C1/rtexture15/` over `C1/rtexture15.zip`). Each loader takes a zip or a directory, and
an explicit `--gamez=`/`--textures=`/`--zrdr=`/`--sounds=` wins.

**Chapter textures load from the top `rtextureN` tier, falling back to `texture.zip`**
(`SessionPaths.ChapterTextures`), where `N` is a **size budget in MB of video-card texture
memory**. ⚠ **The tiers are not mere downscales of `texture.zbd`, which a resolution comparison
misses**: the top tier holds the same file set at the same dimensions, but hundreds of those files
differ in pixel content, and the tier copies are richer, never worse (see
[formats/hud.md](formats/hud.md) on the gauge needle). `rimage.zip` is not loaded at all.

**`extracted/rof/`** is the extraction's second half and holds the unpacked `.rof` UI
archives plus `ui_strings.json`. `PatternLibrary` reads the paint patterns out of it (`--rof=`,
default `extracted/rof`).

## The extraction, in the engine

One implementation, `CSVM/src/Extraction/` ([architecture/Extraction.md](architecture/Extraction.md)),
reached three ways: the Extract screen a player sees when `extracted/` is missing or stamped under
another schema, the headless `--extract=<install>` flag ([cli.md](cli.md)), and the repo-root
wrapper below. No script holds extraction logic.

The ZBD half walks the install's `ZBD` folder and runs `unzbd cs <mode>` on every archive as a
child process, with the mode for its type, writing to the mirrored relative path under
`extracted/`, basename kept:

| Source | Mode | Output |
|---|---|---|
| `interp.zbd` | `interp` | `.json` |
| `planes.zbd`, `gamez.zbd` | `gamez` | `.zip` |
| `soundsh`/`soundsl` | `sounds` | `.zip` |
| `zrdr.zbd` | `reader` | `.zip` |
| `rimage`, `texture`, `rtexture*` | `textures` | `.zip` |
| `cam_anim`, `mis_anim` | `anim` | `.zip` |

An output newer than its source is skipped unless forced. **`messages.json`** comes from a step
after the walk, since `strings.dll` sits at the install root outside it: `unzbd cs messages`,
skipped with a note when the DLL is absent, in which case the engine falls back to raw `MSG_*`
keys.

The `.rof` half covers the UI archives (`GOSDATA/ASSETS/crimson.rof` plus the `crimptch.rof` patch
overlay), the loose cinemas and the `langui.dll`/`language.dll` string tables, all into
`extracted/rof/`. It writes each member at its archive path in upper case, decodes each `.BM` texture to
`<NAME>.PNG` (the greyscale shading map) and `<NAME>_MASK.PNG` (**the paint region masks**, R/G/B =
paint slots 1/2/3), emits `ui_strings.json`, every UI string joined to its `RESOURCE.H` symbol, and
**`menu_layout.json`**, the decoded `LAYOUT.CSV` screens the runtime reads instead of the
originals. Formats: [rof](formats/rof.md), [strings](formats/strings.md),
[menu layout](formats/menu-layout.md).

A failure-free run stamps `extracted/VERSION.json` with the `unzbd --version` line verbatim, the
exe's SHA-256, the fork HEAD, the date, and `ExtractionStamp.Schema`, the one schema integer, which
the engine compares at boot and which any reader change that invalidates old extractions bumps.

## `Extract.ps1` (repo root), the developer's wrapper

`.\Extract.ps1 [-Install <path>] [-DataRoot <path>] [-Unzbd <path>] [-Unzip] [-Force] [-NoBuild]`
builds the solution, runs Godot `--headless` with `--extract=<install> --data-root=<root>
--unzbd=<tool>` plus `--extract-unzip` / `--extract-force`, prints the engine's output, and exits
with its code. The install defaults to `CrimsonSkiesGame`, the tool to the fork build
(`tools/mech3ax/target/release/unzbd.exe`), both next to the script, else under
`CSVM_DATA_ROOT` as a worktree needs. `-Unzbd` pointing at the pinned
`tools/mech3ax-v0.6.1-.../unzbd.exe` rolls back to the old binary with **no code change**, because
the loaders read either extraction shape. `-Unzip` expands each `.zip` into a sibling folder, which
is what makes the viewer read loose files. ⚠ The data root defaults to the script's own folder,
never `CSVM_DATA_ROOT`, so a worktree run cannot overwrite the primary tree's `extracted/`.

## Launch scripts

**`RunGame.ps1`, the play entry point.** `dotnet build`, then Godot with **no user args**, so the
launchscreen (`src/UI/Screens/LaunchMenu.cs`, Mode → Chapter → Plane) shows. Args are forwarded verbatim,
so a content arg (`--fly`/`--stunt`/`--plane=`/`--chapter=`/`--screenshot=`) bypasses it.

**`RunDev.ps1`, the dev helper**, same build step but with console prompts: no args gives
interactive menus (plane roster, chapter) and then `--fly`, flight flags prompt for what is
missing, and the static views (`--plane=`, `--chapter=`, `--damage=`) pass through promptless
apart from `--damage=`'s own plane prompt.

**The Steam build flavour.** `dotnet build CSVM/CSVM.sln -p:CsvmSteam=true` defines `CSVM_STEAM`,
which makes `CSVM/src/Net/NetCarrier.cs` select the Steam carrier instead of ENet and nothing
else. The Steamworks SDK is not in this repo and cannot be, so that carrier throws at every way
in; the flavour exists to keep the seam honest, and both flavours build clean and pass the unit
suite. `RunTests.ps1` and every release build are the default flavour.

**`RunTests.ps1`, the verification entry point.** One command, one summary block, one exit code.
Stages, in order, each reported `PASS` / `FAIL` / `SKIP` / `TODO`:

| Stage | Runs / reads its verdict from / fails on |
|---|---|
| `build` | `dotnet build CSVM/CSVM.sln`; its exit code; a compile error, which also stops the run |
| `units` | `dotnet test --no-build`; the TRX log in `.scratch/testresults/`, never the console summary; any failed test |
| `engine` | Godot `--run-tests` in `-Shards` processes, windowed (LOG-8); each shard's JSON report; a failed suite, a missing report, a watchdog timeout (exit 124) |
| `goldens` | One `--det` Godot per manifest shot; the raw-pixel md5 on its `[core] shot pixmd5=…` line, not the PNG bytes (SHOT-6); a hash, frame or size off the manifest |
| `perf` | `-Perf` only: `analysis/perf/scenarios.json`; the `[perf]` lines, medianed into `perf-history.jsonl`; nothing, it records only |
| `hitch` | `-Hitch` only, last: two scripted launches; each launch's `.hitches.jsonl` sidecar; nothing, awareness only |

Switches: **`-Suite <name>[,<name>]`** (exact in-engine suite names), **`-Filter <substring>`**
(engine suite names), **`-UnitFilter <expr>`** (into `dotnet test --filter`), **`-Shards <n>`**,
**`-Quick`**, **`-SkipUnits`**, **`-SkipEngine`**, **`-SkipGoldens`**, **`-RegenGoldens`**,
**`-GoldenWorkers <n>`**, **`-Hitch`**, **`-SkipHitch`**, **`-Perf`** (+ `-PerfLabel`,
`-PerfCompare`, `-PerfFilter`, `-PerfIterations`, `-PerfFrames`), and **`-Graphics
original|enhanced`** (default `original`, which appends nothing; `enhanced` appends
`--graphics=enhanced` to the perf and hitch launches only).

**Every stage prints its wall time against a budget, and a budget never fails a run**.
The numbers live in `analysis/verification-budgets.json`, one lane for the complete gate and one
for `-Quick`, and live nowhere else so they cannot drift; each is the slowest of three
back-to-back warm runs plus 50 %. A skipped stage is compared against nothing, and the total only
when its lane's stages all ran.

**A selection that matches nothing is a failure**: `-Suite`, `-Filter` and `-UnitFilter` each fail
their stage naming the term, rather than reporting a green zero.

**`-Quick` is the broad partial gate**: build, the quick unit tier (`--filter Tier=Quick`), the
quick engine tier (`--run-tests=tier:quick`), no goldens and no hitch. Membership of both tiers is
checked in, as the `[Trait("Tier", "Quick")]` classes and `SuiteCatalog.QuickTier`. An explicit
`-Suite`/`-Filter` unions with the engine tier and a `-UnitFilter` replaces the unit tier. Quick
prints a `not checked:` line per omitted surface, and never satisfies the landing gate.

**The engine stage runs the full catalog in concurrent Godot processes.** `-Shards <n>` sets how
many; the default is 6 for a full run and 1 whenever `-Suite`/`-Filter`/`-Quick` names a selection,
and `-Shards 1` is the serial reference path. Membership comes from the harness's
`shard:<index>/<count>` term over the per-suite weights at `analysis/engine-suite-weights.json`, so
the same tree always divides the same way; an unweighted suite is charged the default and printed
as `not checked:`. Regenerate that file from a warm `-Shards 1` run's report.

Each shard gets its own log, streams, report and artifacts under `.scratch/engine/owner-<pid>/`;
what that does not isolate is a suite whose store sits outside `.scratch/`, so overlapping runs are
not safe (LOG-13). **The stage's verdict is the merge of every shard's report**, in registry order
via each suite's `index`: counts sum, the error allowlist's caps are re-checked against the SUMMED
counts, and **a shard exiting 0 with no report FAILS the stage**.

**`test-report.json`'s schema is versioned** (`"schema"`, bumped when a field changes meaning or
goes). Besides the per-suite rows and their registry `index`, it holds a `binary` block
(the loaded `CSVM.dll`'s path and MD5), the run's `selector`, a `shard` block and a `phaseTotals`
block splitting wall time into world build, disposal and the rest, so a report can be matched to
its build and the shard merge proved complete. Read the current field list from a report.

**The golden stage is scripted, not an in-engine suite**, because the `--run-tests` harness runs
every suite inside one `_Ready` call without yielding a frame, so no suite there can photograph
anything. A mismatch leaves the actual PNG and that shot's log in
`.scratch/goldens/<shot>.{png,log}`. **`-RegenGoldens`** re-renders every shot and rewrites
`manifest.json` in place, byte-identically apart from the hash lines that moved; GOLD-1 says when
that is the right answer.

**A shot's `frame` is checked before its hash**, off the capture's own `frame=N clock=<sim|render>`
field, because a shot that photographed a different moment is a clock regression and reads as
neither a pass nor a pixel change once it is folded into the hash compare. The capture names the
counter, never the harness: a run with a session reports its sim clock, which under `--det` advances
one step per rendered frame, and a screen with no session reports the rendered frames its countdown
waited, read off Godot's own counter rather than off the countdown itself. Both answer the same
question, so one manifest number covers a flight shot and a menu shot alike.

**Golden shots launch concurrently, `-GoldenWorkers` of them at a time (default 4)**, in
registry-order batches, each with its own watchdog, process, log and PNG. The default is
the fastest worker count that stayed bit-identical on the one machine measured, and
**`-GoldenWorkers 1`** is the serial reference path.

**The hitch stage is opt-in (`-Hitch`), not part of the landing gate**, because it never changes
the exit code: run it when landing a change to `HitchMonitor.cs`, `HitchSidecar.cs` or the hitch
tick in `Launcher.cs`. **`-SkipHitch`** forces it off even when `-Hitch` is given, and `-Quick`
never runs it. A clean `--frames=180` launch should
stay silent and `--hitch-inject=50@300 --frames=310` should trip once on frame 300; it runs last so
its evidence is never taken beside another stage's load (LOG-13, PERF-12/13/14).

### The perf stage (`-Perf`)

**A duration here is a count of SIM frames, never wall seconds.** Each scenario runs
`--det --perf --no-vsync --mute` plus `--frames=N --screenshot=`, and the saved-shot line prints
`sim_frame=N` so the count is proved rather than assumed (every perf scenario runs a session, so
its captures are always sim-clocked). `--perf` reports
a `[perf] window …` line per 60 rendered frames and a `[perf] startup …` line that closes
arithmetically over the build; the script parses both.

**Scenario set** (`analysis/perf/scenarios.json`, whose `args` are the literal command a human
re-runs): `empty-stage` (no gamez, the control a world or clutter change must leave alone),
`c1-flight` (the only moving camera), `c2b-water`, `c4-terrain` and `c5-city`. Defaults are 300 sim
frames × 3 launches, overridden by `-PerfFrames` and `-PerfIterations`; `-PerfFilter` narrows the
set.

**Protocol and verdict.** The first launch of each scenario is discarded, because a cold file cache
*reshapes* a startup profile instead of scaling it (PERF-7), and each kept launch drops its first
window, which carries shader compilation. The record stores medians plus commit, `--dirty` flag,
GPU string, the launch's `"vsync"` mode and the **md5 of the loaded `CSVM.dll`** (METHOD-6).
**A paired A/B is the only verdict**: `-PerfLabel base`, flip the one line under test (METHOD-5),
rebuild, then `-PerfLabel change -PerfCompare base` prints the ratios, calling identical hashes a
noise floor.

**What is read and what is refused.** The verdict metrics are `render_cpu_ms`, `gpu_ms`, the counts
`draws` / `prims` / `nodes` and the startup phases, and a row is marked `*` only when it clears
**both** a relative band and an absolute floor, each this machine's own noise (PERF-9…PERF-11).
Every other recorded metric prints as awareness only, each because its own instrument is unfit for
a ratio (PERF-1, PERF-2), with the reason in the manifest's `notes`.

**Exit-code contract: 1 if any stage FAILED, 0 otherwise, and a skip is not a failure.** A missing
dependency and `-SkipUnits`/`-SkipEngine` report `SKIP` at 0 with a `not checked:` line, so "the
data was not there" never reads as "the check held". `-RegenGoldens` reports `REGEN`, never `PASS`.

**Worktrees.** Extracted data and Godot are both found through `CSVM_DATA_ROOT`, so
`$env:CSVM_DATA_ROOT = 'Z:\CSVM'; .\RunTests.ps1` from a worktree behaves identically to the
primary tree; without it the engine stage reports `SKIP` with the path it looked at. The unit tests
fall back to their own checkout, so a root holding no extraction skips the engine suites but *not*
the data-dependent units. The other variable a scripted run may want is
`CSVM_TRACE_SISCRIPT=<node name>`, which makes `ScriptPlayback` log a `sitrace` line in the `anim`
category with that node's per-step pose, `dt` and step distance every tick.

Stray Godots are killed before the engine, golden, hitch and perf stages, **filtered to this tree's
project dir AND an argument only that stage's own launches carry** (SHELL-2): `--run-tests`, or the
`.scratch\goldens\` / `.scratch\hitchcheck\` / `.scratch\perf\` output paths. All quit by
themselves, so one still alive is stuck and ours; any other Godot here is left alone.

**All three scripts set `SDL_JOYSTICK_DIRECTINPUT=0`**, respecting a pre-set value, as the
controller-disconnect freeze workaround: Godot's bundled SDL hangs the main thread forever when a
>255-button DirectInput device disconnects, and disabling that backend removes the phantom views
while real pads keep working via XInput/HIDAPI. Removal conditions are in `backlog.md` under "Drop
the `SDL_JOYSTICK_DIRECTINPUT=0` workaround".

## Exporting a release build

`CSVM/export_presets.cfg` (committed) holds one preset, **"Windows Desktop"**: release export,
x86_64, `embed_pck=true`, so a single `CSVM.exe` with the pck inside, plus the **self-contained**
.NET publish output beside it as `data_CSVM_windows_x86_64/`. The exported build resolves every root
to the exe's own folder: it reads `extracted/` from there and writes its logs to a plain `logs\`
beside the exe rather than to the `.scratch\logs\` a repo run uses (`Log.DirectoryFor` is the one
switch). Per-user state is not in that folder at all: options, bindings, campaign profiles, scores
and custom planes are written through `user://`, which is `%APPDATA%\Godot\app_userdata\CSVM`.

**One-time template install.** The Godot export templates are user-global, not part of the repo's
pinned editor: extract the inner `templates/` FILES of `tools/godot-4.7-mono-export-templates.tpz`
into `%APPDATA%\Godot\export_templates\4.7.stable.mono\` (create the dir; do not keep the
`templates/` level).

**`ExportRelease.ps1` (repo root)** run with no parameters runs the whole Windows sequence: it checks the
export templates, the fork-built `tools\mech3ax\target\release\unzbd.exe` and the rest of the
payload exist (named errors up front), empties `.scratch\export\`,
builds, imports headless, exports, copies the payload in beside the output, and zips the folder to
`.scratch\CSVM-v<version>-win64.zip`. That folder is cleared because all of it is zipped, and the
clear refuses to run if it holds a junction, since PowerShell 5.1's recursive delete follows one
into its target. In a worktree, which has no `tools\`, it takes Godot, `unzbd.exe` and the mech3ax
checkout from the tree `CSVM_DATA_ROOT` names, as `RunTests.ps1` does.

**`-Linux`** is opt-in and leaves the Windows zip as it is. After the zip it exports the
`Linux/X11` preset into `.scratch\export-linux\`, stages the Linux payload (`packaging/MANIFEST.md`'s
Linux table) and packs `.scratch\CSVM-v<version>-linux-x64.tar.gz` inside WSL (`wsl -d Debian`),
since only a tar written on Linux can carry the executable bit: the files are copied into the
distro's own filesystem, given `0755` (`CSVM.x86_64`, `tools/unzbd`, directories) or `0644`
(everything else), and archived root-owned. The script reads both executables' modes back from the
archive and prints its SHA-256. Before the Windows build starts, the script builds the Linux
`unzbd` from the same `tools\mech3ax` checkout as `unzbd.exe`: `cargo build --release --locked
--target x86_64-unknown-linux-musl --bin unzbd` inside WSL, with `CARGO_TARGET_DIR` at
`~/cargo-target/mech3ax` in the distro (a target dir on `/mnt/z` is slow and would put a Linux tree
in the checkout), then copies the static binary to
`tools\mech3ax\target\x86_64-unknown-linux-musl\release\unzbd`. `-LinuxUnzbd <path>` ships an
existing binary instead and skips the build. A missing toolchain, target or compiler, a missing
`-LinuxUnzbd` file, a missing Linux export template or an unreachable WSL distro is a named error
before the Windows build starts. One-time setup in WSL Debian: `sudo apt install build-essential
curl musl-tools`, rustup from <https://rustup.rs> (into `~/.cargo/bin`, where the script looks),
then `rustup target add --toolchain 1.91.1 x86_64-unknown-linux-musl`. The toolchain is the one
`tools/mech3ax/rust-toolchain.toml` pins; adding the target to `stable` does not reach it. The
musl `unzbd` of a given fork commit writes byte-identical archives to `unzbd.exe`'s. The engine csproj sets
`InvariantGlobalization`, so the self-contained .NET runtime never loads `libicu`; without it, a
system lacking that library (the author's WSL Debian among them) aborts at startup with "Couldn't
find a valid ICU package installed on the system". `sandbox\LinuxRelease.ps1` checks the tarball
this writes (see "The Linux release check in WSL" below), and `PublishRelease.ps1 -Linux` publishes
it beside the zip ("Publishing a release").

**The version has one home: `application/config/version` in `CSVM/project.godot`.** Bump it there
and nowhere else. The engine reads it at startup for the log's first line and the menu's corner
stamp; the Windows export preset stamps it into the exe's file and product version, which is what
`application/modify_resources=true` in the preset is for (off, the export succeeds and ships an exe
whose Properties pane still names Godot's own template); and `ExportRelease.ps1` reads the key back
to name the zip. The script reads the stamp off the exported exe afterwards and throws if it is not
the version it started from, since nothing else about a missing stamp is visible. ⚠ The script
snapshots and restores `project.godot` around the export and is its only writer, the version is
read from that file, never written into it, and never stamped into the preset during a run.
The zip is built through `System.IO.Compression`, since `Compress-Archive` reports success after
writing nothing when a single file is locked.

**Which number to bump** is decided per release, against what changed since the last tag
(`git log v<last>..HEAD`), in the commit that is about to be published; builds between releases
keep stating the last released version, and `BUILD-INFO.txt` names the exact commit. The **patch**
number is for a release that only fixes, including a fix that brings behaviour closer to the
original; a player finds nothing new in it. The **minor** number is for a release that adds
something a player can see (a mode, a screen, an input device, a mechanic), which is where a
milestone lands; fixes shipped alongside a feature do not make it a patch. The **major** number is
the author's call that the remake stands in for the original end to end; until then the version
stays `0.x`, and afterwards a major bump is reserved for a change that breaks saved profiles or
replaces a subsystem wholesale. ⚠ A patch release never changes the format of anything written to
`user://` and never changes the network protocol, so builds that differ only in the patch number
read each other's profiles and can play in the same session. The network half is enforced at the
join: each lobby sends its major.minor of `BuildVersion` (`Net/NetBuildVersion.cs`), the host
refuses a mismatched guest through `SessionClosed` with both versions named, and the LAN games list
greys a game of another major.minor.

The payload is `packaging/MANIFEST.md`'s table, copied from its repo sources on every export, which
keeps it byte-identical.

**`packaging/README.md` is the whole of what a downloader is told**, written for someone who found
the zip on the releases page and knows nothing else about the project: where the download comes
from and how to check its SHA-256 against the release page, the requirements including the renderer
floor below, the in-game extraction on first start, the `logs\` and `user://` locations, what the other files
at the zip root are, and where a report goes. Four things in it restate facts that live in code or
in this file, and go stale silently when one of them moves: the renderer floor and the
`[perf] gpu=` line a below-floor machine writes, the log directory and the version on the log's
first line, the extraction screen's behaviour, and the payload list. ⚠ **The author reviews it before
any release**, since outward communication is theirs; it is the one payload file that is not
finished when it is correct.

**`packaging/README-linux.md` is its Linux twin**, shipped as `README.md` at the tarball root. It
drops the Windows-only material (SmartScreen, Direct3D 12) and adds the
community-tested label, the Vulkan-only requirement, unpacking with the executable bits, where the
original game's folder comes from (a copied Windows install; the Wine and Proton prefix search is
untested), the settings folder under `~/.local/share/godot/app_userdata/CSVM`, and an "On Steam
Deck" section. It restates the same facts as the Windows README plus the install search places in
`InstallLocator`, and the author reviews it on the same terms.

**Two of the zip's files are about the build rather than part of it.**
`LICENSE-thirdparty.txt` carries the notices the payload's own contents oblige it to carry, which
`LICENSE` (CSVM, GPL-3) and `LICENSE-unzbd` (mech3ax, EUPL-1.2) do not cover: the Godot engine
linked into `CSVM.exe`, the engine's own third-party components, the self-contained .NET runtime in
`data_CSVM_windows_x86_64\`, and the Rust crates linked into `unzbd.exe`.
`packaging/BuildThirdPartyNotices.ps1` assembles it, reading every text out of the shipped artefact
rather than off a website: ⚠ the Godot Windows distribution and the export-template archive ship no
`LICENSE.txt` or `COPYRIGHT.txt` on disk at all, so the script runs the pinned editor headless over
a throwaway project in `TEMP` and reads them back through `Engine.get_license_text()`,
`get_copyright_info()` and `get_license_info()`; the .NET half is `LICENSE.TXT` and
`THIRD-PARTY-NOTICES.TXT` from the `Microsoft.NETCore.App.Runtime.win-x64` NuGet pack the publish
draws from, not the machine-wide `dotnet` install, which is usually a newer build; and the crate
half is `cargo metadata --offline --filter-platform x86_64-pc-windows-msvc` over the fork, with each
crate's licence text taken from the registry checkout it was built from and deduplicated by content.
The file's header states the Godot build, the .NET runtime version, the runtime pack, the crate
target and the `cs-anim` commit it was assembled for, and `ExportRelease.ps1` re-checks all of them
against what it is packaging, so a stale notice is a build failure rather than a wrong claim inside
a shipped zip. Regenerate when one of them throws; ⚠ a moved `cs-anim` counts even when the fork's
own code did not change, because the crate list enumerates that commit's dependency tree.

**The tarball ships its own notice**, `packaging/LICENSE-thirdparty-linux.txt` under the same name
`LICENSE-thirdparty.txt`, written beside the zip's by `BuildThirdPartyNotices.ps1 -Linux` (which
rewrites both). Its .NET half is the `Microsoft.NETCore.App.Runtime.linux-x64` pack of the same
version, its crate half is `--filter-platform x86_64-unknown-linux-musl` (the trees differ: `libc`,
`addr2line`, `gimli` and `object` on Linux, `windows-sys` and its companions on Windows), and a
section 9 carries the musl C library that Rust's musl target links statically into `tools/unzbd`,
from `packaging/LICENSE-musl` (the musl-1.2.3 release's `COPYRIGHT`; the bundled `libc.a` carries
no text). The Godot sections are the editor's, which is valid because the engine compiles its
licence tables from one `COPYRIGHT.txt` on every platform; the script runs the Linux template's
`--version` in WSL and refuses a template of another build. ⚠ The script also refuses a
`rust-toolchain.toml` pin other than Rust 1.91.1, because a new toolchain can bundle another musl
and section 9 would then name the wrong release. Neither notice may name the other platform's
runtime pack, crate target or file names: the script, `ExportRelease.ps1` and
`sandbox\LinuxRelease.ps1`'s payload stage each refuse one that does, so the zip's notice cannot
ship in the tarball. Neither notice has an SDL section; the zip carries SDL's own licence as
`LICENSE-SDL2.txt`, and the tarball ships no SDL2.

`BUILD-INFO.txt` is the one payload file generated rather than copied, because what it states is
different on every run: the CSVM commit and the mech3ax `cs-anim` commit the two shipped binaries
were built from, each with whether its worktree was clean, plus whether `cs-anim` is pushed. The
export records those qualifiers instead of refusing on them, since building off a dirty tree is the
normal development case and only a published binary makes a false source-correspondence claim to
anybody; `PublishRelease.ps1` is where a qualifier becomes a refusal.

Two filters in the preset are required.
`include_filter="data/*.json"` keeps `stock_loadouts.json`/`effect_pools.json`, non-imported
resources the default export silently drops, without which every plane flies unarmed; their loaders
read them through `Godot.FileAccess` rather than `GlobalizePath` plus System.IO, which is what
makes the pck copies reachable. ⚠ `exclude_filter="config.json"` keeps the dev box's personal
tuning override out of every build. Smoke-test an export from a **bare folder** with a **foreign
CWD** and no `CSVM_DATA_ROOT`; either can mask a broken default root.

## Publishing a release

**`PublishRelease.ps1` (repo root)** is the publish, from one run: it reads the version from
`CSVM/project.godot`, runs `ExportRelease.ps1`, checks the zip that came out, computes its SHA-256,
creates the annotated tag on the commit that was built, pushes it, and creates the GitHub release
with the zip as its only asset, or with the Linux tarball beside it under `-Linux`. The pre-release flag stays off, because a build that is hidden from
the repository's Latest badge is not the one a visitor lands on. Because the tag, the exe's stamped
version, the zip's name, the published checksum and the notes all come out of that single run, none
of them can disagree with another.

`-NotesFile` supplies the prose that goes above the generated sections; the script writes the
verification and provenance sections itself, so the file never states a checksum or a commit of its
own, and it refuses a notes file that does. The generated text restates `BUILD-INFO.txt`'s two
commits as links, so the release page and the archive give the same account of what was built.
`-DryRun` runs every check and the export and stops before the tag. `-TagSuffix rehearsal`
publishes under `v<version>-rehearsal` instead: a real tag, upload and release to exercise the whole
path, deleted afterwards with the `gh release delete ... --cleanup-tag` command the run prints, so
the release version's own tag is still minted exactly once. `-Yes` skips the confirmation prompt,
which is otherwise the last point at which the tag and the upload can be called off.

**`-Linux`** makes the same release carry the Linux tarball beside the zip: one tag, two assets.
The export runs as `ExportRelease.ps1 -Linux`, and the tarball gets the zip's checks (it exists and
postdates the run; its `BUILD-INFO.txt`, read out of the archive with Windows' own `tar.exe`, names
both commits and records no qualifier). Then `sandbox\LinuxRelease.ps1` runs in full on the tarball
(see "The Linux release check in WSL" below), and a failure ends the run before the tag with nothing
created. The tree and `HEAD` re-check comes after that check, so it covers its minutes too. The
generated notes then list both downloads with their sizes and SHA-256 (`Get-FileHash` for the zip,
`sha256sum` for the tarball), link the Linux README's "On Steam Deck" section at the tagged commit
rather than restating it, and name both executables and both `unzbd` builds under the two commits;
the tag message carries both checksums and `gh release create` uploads both files. Without `-Linux`
the run, its output and its notes are the Windows-only release. The Windows Sandbox run
(`sandbox\PublicRelease.ps1`) is not part of either path; it is run by hand. The mech3ax checkout is
found the way `ExportRelease.ps1` finds its tools, from `CSVM_DATA_ROOT` in a worktree, so both
scripts read the same `cs-anim` commit.

**What it refuses.** A dirty CSVM worktree, since the release says the zip was built from a commit.
A dirty `tools/mech3ax` `cs-anim`, or one that is not on its origin: `unzbd.exe`'s source commit is
published as a fact about a binary in the zip, and a commit that exists only on this workstation is
not a source anyone can read. The CSVM commit needs no pushed-check of its own because pushing the
tag publishes it, though the script says so when `HEAD` is on no remote branch, since a reader who
opens the branch expects to find the release's commit in its history. It also refuses a `BUILD-INFO.txt`
that records a dirty or unpushed source, an existing release, and a tag that already exists either
locally or on the remote. ⚠ **A tag is never re-pointed**, which is why there is no `-Force`: it is
the source correspondence for binaries that may already be downloaded, and nothing on the
downloader's machine says it moved. The way to correct a bad release is another version, not another
meaning for this one. The tree and `HEAD` are re-read after the export for the same reason, since a
build takes minutes and an edit landing during one would tag a commit that is not what was built.

**`gh` must be installed and authenticated** (`winget install GitHub.cli`, then `gh auth login` with
the `repo` scope). ⚠ A shell started before the install does not have `gh` on `PATH`, so open a
fresh one; the script looks in winget's install locations before giving up, which is what keeps a
long-lived agent shell working. Every `gh` call passes `--repo` explicitly rather than letting it
infer the repository from the working directory, and the upload passes `--verify-tag` so `gh` cannot
invent a tag of its own when the push has not happened. ⚠ A `gh` or `git` call whose failure is a
normal answer, such as asking for a release that does not exist yet, goes through the script's
`Invoke-Probe`: PowerShell 5.1 turns a native command's *redirected* stderr into a terminating
`NativeCommandError` under `$ErrorActionPreference = 'Stop'`, so `2>$null` around one of those ends
the run instead of answering the question.

## Clean-machine runs in Windows Sandbox

**`RunSandbox.ps1` (repo root)** runs a release zip on a machine that has never seen this project.
It stages `.scratch\sandbox\<timestamp>-<vgpu|novgpu>\` with an `input\` folder (the zip plus the
driver script, mapped read-only) and a writable `output\`, writes the `.wsb`, starts Windows Sandbox
on it and waits for the driver's `done.txt`. `-NoVGpu` is the below-the-floor machine, `-MemoryMB`
its memory, `-MapReadOnly` adds host folders the zip does not carry (a retail install, an extraction
tree), `-Networking` gives the guest a network (off by default; the SmartScreen verdict on an
unknown download is fetched, so a run that records that prompt needs it), and `-Driver` chooses what
runs inside, so a later item can supply its own procedure without rebuilding the harness. The drivers
share `sandbox/SandboxCommon.ps1`, copied in beside the driver and dot-sourced by name: the step log,
the window census, screenshots, the machine facts and the watched launch of `CSVM.exe`. Everything
the run produced stays on the host in `output\`: screenshots, both streams, the build's own `logs\`,
a line-by-line `steps.log` and `summary.json`.

**`sandbox/RendererFloor.ps1`** is the driver that answers "what does this machine do with this
build". It unzips to `C:\CSVM`, launches `CSVM.exe` the way a recipient double-clicks it, records
every window and dialog it sees (title, class, owning process and child-control text), screenshots
at eight seconds and at the end, copies `logs\` back, and reports the renderer from the build's own
`[perf] gpu=` line. A launch counts as reaching the game only if it drew its own window, put no
dialog on screen, wrote a log and was still running when the watch ended; when the plain launch does
not, the driver repeats it with `--rendering-driver d3d12`, with `--rendering-method
gl_compatibility` and with `--rendering-driver opengl3`, so a fallback that does work becomes a
documented troubleshooting line rather than a guess. With an extraction tree mapped it also flies a
chapter with `--no-vsync`, because whether a machine renders a menu says nothing about whether it
can fly.

**`sandbox/PublicRelease.ps1`** is the driver that follows `packaging/README.md` literally on a zip
carrying the mark of the web, with a retail install mapped in (`-MapReadOnly`). The zip is copied
into Downloads as a browser leaves it, unzipped through the shell's own copy engine so the mark
propagates to the files inside, and then each double-click the README names is done twice: once
through Explorer, which is where the security prompt appears and is recorded, and once as a plain
process, which is what "Run anyway" leads to. `CSVM.exe` is started before the extraction for the
screen that offers to extract, the extraction runs as `CSVM.exe --headless -- --extract=<install>`
on the mapped install (the in-game button needs a click a script may not give, and both run the
same pipeline), and the menu and a C1 flight run on the data the machine extracted itself. The summary records the mark on the zip and on the extracted files, the extraction's
time, file count and size, the save folder, and each launch's windows and dialogs.

What the rig had to learn, none of it visible in a failed run:

- ⚠ **The element is `<vGPU>`.** A `<VGpu>` spelling is ignored without an error, and the run then
  measures a machine with the host's GPU passed through, which is not a floor test at all. The check
  is the guest's own hardware, which the driver records first: below the floor it has only the
  "Microsoft Remote Display Adapter" and no registered Vulkan ICD, and with the vGPU on it has the
  "Microsoft Virtual Render Driver" and the host card's Vulkan.
- ⚠ **Ask the person to close the sandbox window; never close it from a script.** Killing the
  processes wedges the Container Manager until an elevated `Restart-Service CmService -Force`; the
  window ignores a posted close; and an Alt+F4 is delivered into the guest, or, when the raise the
  script asked for is refused, into whatever window the person was working in.
- **One session at a time.** A second session started while one is live comes up with no mapped
  folders and no driver, so nothing runs and nothing is written. The process to test for is
  `WindowsSandboxRemoteSession`.
- **WMI is access-denied to the sandbox account**, so the machine facts come from the display-class
  registry key rather than from `Win32_VideoController`.
- **The engine's flags are user args, so a scripted run passes them after a bare `--`.** Before it
  they reach Godot, which ignores them, and a `--fly` silently becomes a plain menu launch.
- **The logon command can fire before the mapped folders mount,** so it polls for them, and its
  console is invisible, so it redirects. That redirect is still buffered when the session ends, which
  is why the driver appends `steps.log` line by line and reads its own `summary.json` back off the
  share before saying it finished. The append opens the file with every share flag, because a host
  that tails the log while the guest writes it otherwise makes every later append fail silently.
- ⚠ **A file carrying the mark of the web is not double-clicked with `Start-Process`.** ShellExecute
  does not return until the security prompt is answered, so the driver hangs there, and a prompt
  raised from a hidden helper process never reaches the screen at all. The driver hands the file to
  `explorer.exe`, which returns at once and shows the prompt where a person would see it.
- **CIM is access-denied to the sandbox account too**, and `Get-NetAdapter` throws a terminating
  error through `-ErrorAction SilentlyContinue`, which empties every machine fact gathered in the
  same expression. Network presence is read from `NetworkInterface.GetIsNetworkAvailable()`.

**The renderer floor, as observed.** The build does not refuse to start without Vulkan. On the
below-floor machine Godot reports `Required Vulkan instance extension VK_KHR_surface not found`,
then `Your video card drivers seem not to support Vulkan, switching to Direct3D 12`, and runs
Forward+ on Direct3D 12's `Microsoft Basic Render Driver`, which is the WARP software rasterizer.
No error dialog appears at any point, and nothing has to be passed to reach that fallback. The menu
and the no-game-data screen render normally. A flight does not: loading a chapter fills the software
device, `buffer_create` fails with `0x8007000e` (out of memory) tens of thousands of times, and the
process dies of an access violation (`0xC0000005`) about fourteen seconds in, leaving no window and
no message. Guest memory is not the constraint, since 8 GB and 16 GB fail identically. So the floor
is a GPU with a working Vulkan or Direct3D 12 driver. What a machine below it shows a player is the
boot card and then nothing: with game data present the intro film's first 4 MB vertex buffer fails
with `DXGI_ERROR_DEVICE_REMOVED` (`0x887a0005`) and the process dies of the same access violation a
few seconds in, before any menu. Only the no-game-data screen survives on that machine, so a menu
observed without data says nothing about the floor.

## The Linux release check in WSL

**`sandbox/LinuxRelease.ps1`** is the Linux sibling of `sandbox/PublicRelease.ps1`, run on the host
rather than inside a sandbox: it drives WSL Debian over the tarball `ExportRelease.ps1 -Linux`
built (`-Tarball` names another) and the author's install (`CrimsonSkiesGame\` under this tree or
the one `CSVM_DATA_ROOT` names; `-Install` names another). It fails on any failure and prints
RunTests.ps1-style stage lines and one verdict; a full run takes about 95 s (extraction 21 s, the
suites 70 s at six shards). Three stages, each run even when an earlier one failed, where it still
can:

- **payload**: the archive's listing against `packaging/MANIFEST.md`'s Linux table, read from that
  file rather than restated: every named entry present (a folder name must hold a file), nothing
  at the root the table does not name, and `CSVM.x86_64` and `tools/unzbd` at `-rwxr-xr-x`.
- **extract**: the unpacked `CSVM.x86_64 --headless -- --extract=<install>` into a fresh data root
  with the player's defaults (zips only), which must exit 0 and stamp `VERSION.json`. A `tools/unzbd`
  without its bit fails here too ("Permission denied" starting the process), and a missing runtime
  file in `data_CSVM_linuxbsd_x86_64/` fails the launch.
- **engine** (skipped by `-NoSuites`): `--run-tests=shard:<i>/<n>` over that same zips-only root,
  the shape every player's install reads, in `-Shards` processes (default 6).
  The suite list is the harness registry's and the division is `analysis/engine-suite-weights.json`,
  copied in beside the exe where the harness looks for it; the merge refuses a missing report, a
  suite run twice, a coverage short of the registry, and an unexpected engine error line.

Everything in the distro sits under `~/csvm-linux-check`, wiped when the next run starts, with
`XDG_DATA_HOME`, `XDG_CONFIG_HOME` and `XDG_CACHE_HOME` set per process inside it, so no run
touches the distro user's `~/.local/share/godot` and parallel shards share no `user://`. The
listing, logs, reports and each shard's engine log are copied to `.scratch\linux-check\<timestamp>\`.
`PublishRelease.ps1 -Linux` runs this check on the tarball it just exported and stops before the tag
when it fails.

What the check had to learn:

- ⚠ **The engine flags go before the bare `--` and the game flags after it**, as everywhere else;
  without the `--` an export ignores `--run-tests` and boots the menu.
- ⚠ **Pass no `--log-file`.** On Linux the managed side of an export cannot read Godot's own flags
  back (`Environment.GetCommandLineArgs` does not carry them there, where it does on Windows), so
  the harness would report the engine log unscreened. Without the flag, each process logs to its
  own `user://logs/godot.log` and the harness screens that.
- **Some suites cannot pass headless on any platform.** They read back what only a renderer or a
  display produces (mesh and MultiMesh instance data, viewport pixels, windows and screens). The
  `$HeadlessOnly` table at the top of the script lists them with the reason each fails, beside
  `$HeadlessEngineErrors`, the two engine error lines only a headless process prints. The same
  suites and the same error counts come out of the Windows export run headless, which is how an
  entry is admitted: a suite that fails on Linux alone is a Linux bug and never goes on the list.
  Listed suites still run, and one that passes is reported so a stale entry is seen.
- **The suites read the player's zips-only tree, where the Windows battery reads unpacked
  folders.** A texture archive refuses a read after `Dispose` in both shapes alike, so a read of a
  closed archive fails the battery as it would fail here, rather than passing on the folders alone.
- ⚠ **A shard that exits with a signal after writing its report fails the stage.** A render
  instance still alive at exit (a mesh instance on a node nobody freed) crashes an exported build in
  its teardown, on either platform: 139 or 134 on Linux, `0xC0000005` or `0xC0000374` (heap
  corruption) from the Windows export. The editor survives the same leak and prints
  `Pages in use exist at exit in PagedAllocator` on stderr, which `RunTests.ps1`'s engine stage
  fails on, so the battery sees the leak before an export does. A player's quit takes the same
  teardown, which is why the check does not trust the report here.
- One-time setup in WSL Debian: `sudo apt install libfontconfig1`. Godot's Linux build loads it for
  system fonts and logs an engine error on every lookup without it; players' systems have it, a
  minimal WSL Debian does not, and the script refuses to start without it. The goldens are not run,
  since WSL cannot render them.

## `tools/` (git-ignored)

Downloaded binaries: mech3ax v0.6.1 (pinned pre-fork extractor, for rollback), the fork checkout
(below), the Godot 4.7 .NET editor at `tools/godot/Godot_v4.7-stable_mono_win64/` (use
`*_console.exe` for CLI runs), and the SDL2 runtime at `tools/sdl2/` (below).

### SDL2 for flight sticks (`tools/sdl2/`)

The SDL3 inside Godot 4.7 enumerates no DirectInput-only device on the author's machine, so CSVM
reads flight sticks through the official SDL2 runtime instead (`docs/architecture/Sticks.md`).
**`InstallSdl2.ps1` (repo root)** downloads SDL 2.32.10's `SDL2-2.32.10-win32-x64.zip` from the
libsdl-org GitHub release, checks it against a pinned SHA-256, and writes three files into
`tools/sdl2/`: `SDL2.dll`, the zip's `README-SDL.txt`, and SDL's zlib `LICENSE.txt`, which the
runtime zip does not carry and which is read from the release's own commit. Every file is hashed
against its pin in a staging folder before any installed file is replaced, and a run over an
install that already matches downloads nothing. `-Root <checkout>` installs into another tree,
`-Force` re-downloads, and `-Verify` installs nothing: it throws unless the three files match their
pins and otherwise returns the version, commit and DLL hash. The pins live in that script alone.
⚠ Moving the version means repeating the stick-detection check on real hardware, because 2.32.10
is the build that check passed on; a new hash alone says nothing about whether the sticks still
enumerate.

One install serves every worktree. Run it once in the primary checkout; a worktree finds the DLL
through `CSVM_DATA_ROOT` like it finds Godot, and needs no copy of its own.

**Nothing puts the DLL on `PATH`.** No launch script changes the environment or the DLL search
path for it; the game loads it by absolute path, taking the first of these that exists:

1. `SDL2.dll` in the running executable's own folder. This is the exported build, where
   `ExportRelease.ps1` puts it beside `CSVM.exe`.
2. `<repo root>/tools/sdl2/SDL2.dll`, the repo root being `res://`'s parent on disk (the
   `Launcher` repo root of an editor-hosted run).
3. `$CSVM_DATA_ROOT/tools/sdl2/SDL2.dll`, the fallback a worktree uses.
4. `tools/sdl2/SDL2.dll` of the checkout that supplied the running Godot, found two folders above
   the executable (`tools/godot/<build>/`), so a worktree launched without `CSVM_DATA_ROOT` still
   finds it.

The system's own DLL search is never consulted, since any `SDL2.dll` on `PATH` is an unpinned
build of unknown version. When no candidate exists, or the load fails, the game runs without
sticks and logs one line naming the paths it tried; a missing DLL never stops a launch, and
`InstallSdl2.ps1` is not called by any launch script. `ExportRelease.ps1` does require it: the
export runs `InstallSdl2.ps1 -Verify` before building, ships `SDL2.dll` and `README-SDL.txt`
beside the exe with the licence as `LICENSE-SDL2.txt`, and records the SDL version, commit and DLL
hash in `BUILD-INFO.txt`. With `-ToolsRoot <checkout>` a worktree's export takes the SDL2 files
from that checkout's `tools/sdl2/`, as it does Godot and the mech3ax fork.

**Linux.** The bridge is SDL2's joystick API alone. `Sdl2Sticks` uses no `DllImport`: it loads
one library with `NativeLibrary.TryLoad` and binds every export by name with `TryGetExport`, and
the 24 functions it calls exist unchanged in every SDL2 build, sdl2-compat included. The roster,
profiles, bindings, capture, prompts and glyphs never see the library. What differs off Windows:

- **The library and where it comes from.** `Sdl2Sticks.ForPlatform` picks the list. Off Windows it
  is `libSDL2-2.0.so.0` beside the executable, then the bare soname, which `Load` hands to the
  system loader (`dlopen`'s search: `LD_LIBRARY_PATH`, the loader cache, `/usr/lib`). The system
  search is what the Windows rule above forbids, and Linux relies on it: libsdl-org publishes no
  Linux binary to pin, and a distribution's SDL2 is built against that system's libraries. SteamOS
  ships `/usr/lib/libSDL2-2.0.so.0` from sdl2-compat (SDL2's API over the system SDL3). The tarball
  ships no SDL2, and its `BUILD-INFO.txt` has no SDL block. No library found is the same one
  `sticks: off, no libSDL2-2.0.so.0 (tried ...)` line and a launch without sticks as a missing
  `SDL2.dll`. A library that is present but cannot load (a missing dependency) reads the same,
  since `TryLoad` reports no reason. A loaded one logs the file the loader chose, read from
  `/proc/self/maps`: `sticks: SDL 2.32.4 from libSDL2-2.0.so.0 (system: /usr/lib/...)`.
- **The hints in `Sdl2Sticks.Load`** are set on both platforms. `SDL_JOYSTICK_HIDAPI=0` matters on
  Linux too: it keeps SDL2 off the hidraw nodes, where it would handshake with the Deck's built-in
  controls and with pads Steam or Godot's SDL3 are driving. RawInput, WGI and XInput are Windows
  backends, and their hints do nothing elsewhere. `SDL_NO_SIGNAL_HANDLERS=1` keeps SDL2's SIGINT
  and SIGTERM handlers out of Godot's process. On Linux SDL2 reads evdev, where a second reader
  shares a device rather than taking it.
- **The gap-filler's Linux rules.** Godot's joypad layer on Linux has no DirectInput-style gap for
  gamepads, so a gamepad is Godot's there, and the model match is not left as the only guard: a
  pad Godot reports no `vendor_id`/`product_id` for would otherwise be read twice. `StickRoster` is built with
  `godotReadsGamepads` off Windows and then also skips a listing SDL2 maps as a gamepad
  (`SDL_IsGameController`) and any device of Valve's vendor id `28DE`: the Deck's built-in
  controls, a Steam Controller and Steam Input's virtual pad. Each skip logs its reason on a
  `stick skipped:` line. Windows keeps the model match alone.
- **Whether the bridge is needed at all.** The bridge exists because Godot's SDL3 enumerates no
  DirectInput-only stick on Windows. If Godot's SDL3 on Linux lists a stick, `StickRoster` skips it
  by model, and the stick reaches the game as an ordinary Godot joypad, without its stick profile,
  stick glyphs or stick column. Which roster holds a real stick on Linux is not yet seen: run
  `--dump-sticks` with it connected, whose first line lists Godot's pad models.
- **Device GUIDs** are SDL's 16 bytes printed in memory order on both platforms. Their content
  differs (a Linux GUID carries the bus type, vendor, product and version), and only the log
  prints them; bindings and profiles key on the model.
- **Windows only:** `InstallSdl2.ps1`, which pins the `win32-x64` zip; the three SDL2 files
  `ExportRelease.ps1` ships in the zip, with the DLL's hash in `BUILD-INFO.txt`; and
  `SDL_JOYSTICK_DIRECTINPUT=0` in the launch scripts, a workaround for Godot's SDL3 (BL-033).

## The mech3ax fork (`tools/mech3ax/`)

**Crimson Skies support lives in the fork, not upstream.** Upstream removed it, so the fork is its
home and upstream commits get merged *into* the fork. Upstream is dormant, so syncing is a
check-occasionally rather than a routine.

- **Remotes:** `origin` = `git@github.com:Laeresh/mech3ax.git`,
  `upstream` = `https://github.com/TerranMechworks/mech3ax.git`.
- **Branches, all three pushed to `origin`**, since the fork and not this workstation is the
  authoritative copy: `cs-anim` = integration, and what the release binary is built from;
  `pr-cs-anim` / `pr-cs-gamez` = the same work split by concern; `main` = an upstream mirror.
- **Sync:** `git fetch upstream && git merge upstream/main` into the fork, then rebuild
  `target/release/unzbd.exe` and re-extract if the output shape moved.

The shelved upstream-contribution package is archived at [upstream-pr/](upstream-pr/).

### Window focus: scripted runs stay out of your way

The game window is **created without focus** (`display/window/size/no_focus` in `project.godot`),
and a scripted session also **hides its window** through `ScriptedWindow.Hide` once
`Launcher._Ready` knows the flags, which leaves rendering untouched where minimizing would stop it
(SHELL-13, SHOT-16). A hand launch still shows a brief flash, because the window exists before
`_Ready` can run, so `RunTests.ps1` instead runs **every launch on a separate Windows desktop**
(`HiddenDesktop.ps1`: `CreateDesktop`, then `CreateProcess` with `STARTUPINFO.lpDesktop`), which a
window can only join through the process that started it, so placement is decided *before* the
process runs. The summary line names the desktop used, and a refused desktop continues
visibly rather than failing; the rejected alternatives are in the retired `analysis/hidden-desktop/`
(`git show analysis-archive:analysis/hidden-desktop/FINDINGS.md`).

`RunGame.ps1` and `RunDev.ps1` hand the foreground to the new window themselves, finding it by pid
via `EnumWindows` (SHELL-13). Every `RunTests.ps1` stage launches through its private
`Invoke-Godot` helper, which uses the non-console binary and redirects both streams to
`<its --log-file>.out` / `.err` (SHELL-10).

**`RunProbe.ps1`, the same launch for ad-hoc runs.** A hand-launched probe would otherwise inherit
both the window flash and the console scribble a bare `& $GodotExe …` sprays over the calling
terminal (SHELL-10): **never invoke the Godot binary directly for a scripted run, go through
`.\RunProbe.ps1 <user args>`.** It forwards every argument verbatim (no build step, so build
first), runs on its own hidden desktop (`csvm-probe`, falling back to a visible but still
redirected run if the OS refuses one), parks the streams beside the run's `--log-file` or in
`.scratch/logs/probe-<stamp>.out/.err`, and exits with Godot's code. **`-TimeoutSec`** (default
**300**, `0` = wait forever) kills the run and exits **124**, so a probe that never quits cannot
hang a session. **`-Resolution WxH`** is forwarded as Godot's own `--resolution` ahead of the `--`,
which is the only way a scripted capture lands at a size a player runs: the project ships
1280x720, and a saved size cannot raise it because `--screenshot` implies `--det`, which drops
every saved option (DET-8). A resolution-sensitive artefact is invisible at the default size.
