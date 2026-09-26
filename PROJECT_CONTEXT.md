# PROJECT_CONTEXT.md

Tool-neutral project brief for CSVM. Any coding agent working in this repo should read this
file first, `CLAUDE.md` and `AGENTS.md` are thin, tool-specific pointers into it.

## ⚠ Keep the index files + `docs/` up to date

**This file is the compact, authoritative index of project context; deep detail lives in
`docs/`.** Update documentation in the same turn as the change it describes:

- Module purpose → the module's `## src/...` entry in `docs/architecture/<Namespace>.md`, reached through the index in `docs/architecture.md`. **An entry orients a reader: what the module is for, what it owns, and which other module to look at next. Nothing else.** Body at most 8 lines, 12 for the named highest-traffic modules; `CheckDocEntries.ps1` enforces the caps and the coverage in the commit gate. **`⚠` traps do not belong there.** A trap that constrains a future edit goes in the code, at the member it binds, under the comment caps below; a trap that is really format or decode knowledge goes in `docs/formats/` or `docs/org/`; a way a measurement misleads goes in `docs/verification.md`. **Read a module's entry there before modifying that module**, then read the code. Diagnosis narratives go in the landing commit's message body.
- A new, renamed or deleted module → its one-line bullet in `docs/architecture.md`'s Module index and its `##` entry in the namespace file, in the same edit. This file carries the namespace map, never a per-module list.
- Format / reverse-engineering knowledge → `docs/formats/`, or `docs/org/` when the page's claims come from the retail executable wholesale.
- The extraction pipeline, the launch scripts, or the mech3ax fork → `docs/tooling.md`.
- A way a MEASUREMENT can mislead (non-determinism, an instrument that manufactures its own answer, a masked effect) → `docs/verification.md`, as a transferable rule. A commit message alone buries it, nobody reads the log before starting work. Shape: a bold 1–2-line imperative + at most one sentence of measured evidence, no narrative.
- Landed work → the commit message body, what landed, how verified, outcome, plus tick the active plan's checklist and **swap** the "Current status" next-step pointer. The status section must be no longer after the landing edit than before it; the description of what landed lives in the commit and the plan, never here.
- Pure refactors with no external effect → usually no update needed. When in doubt, update.

**Budget and shape, this file is an index, not a narrative.** `CLAUDE.md` once grew to 162 KB because every session appended while nobody owned the total; three rules keep that from happening again:

- **Budget: this file stays under ~35 KB.** If a change would push it over, the content belongs in `docs/` and this file gets a *pointer* instead. Check with `(Get-Item PROJECT_CONTEXT.md).Length` before adding a paragraph, not after.
- **Shape: never restate a list another file already indexes, point at that file.** If a section here starts growing one line per *thing*, that list belongs in `docs/` with a pointer in its place.
- **"Current status" holds pointers and item IDs, never a description of landed work, in any tense.** The tripwire is size, over ~15 lines / ~2 KB means history crept in.

**Standing rule, AI-assistance disclosure.** Every outward-facing communication about this work discloses that it was done with the help of an AI coding agent: PR bodies, issues, discussion posts, comments, and any community writeup, not only an initial submission. Commits carry a `Co-Authored-By:` trailer naming the acting agent and model (e.g. `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`), whichever agent actually did the work, not a fixed name. The user owns all upstream/community communication, so this is a constraint on what gets *drafted* for them, not an instruction to post anything.

## Project Description

An XWVM-style remake of **Crimson Skies** (2000, Zipper Interactive, Microsoft): a modern engine that plays the original game using the player's own legally-owned game files.

**Public home: <https://github.com/Laeresh/CSVM>** (`origin`, branch `main`). The repo is named **CSVM**; use that name in outward-facing text and as the CC-BY attribution target for `docs/formats/`.

## 🚫 Hard rule: no game assets in version control, ever

Public open-source project under the **XWVM legal model**: the repo ships **code and documentation only**. Game files, extracted assets, ZBD contents, screenshots of hexdumps containing bulk asset data, reproduced game code or decompiler output, none of it gets committed or published. The importer reads the player's own install at runtime. `.gitignore` enforces this for `CrimsonSkiesGame/`, `extracted/`, and `tools/`; keep it that way when adding directories.

**Licensing.** Code is **GPL-3.0-or-later** (root `LICENSE`); `docs/formats/` is **CC BY 4.0** (`docs/formats/LICENSE`) so the format knowledge is reusable by permissively-licensed projects. New code files need no per-file header, the root `LICENSE` plus the `README.md` notice covers the repo. The mech3ax fork is **EUPL-1.2** (upstream's license, not a choice) and is a separate tool, not linked into the engine; GPL-3.0 was chosen partly because the EUPL Appendix lists it as compatible, so fork code *could* later be vendored in. Do not relicense any part permissively without checking that chain. `README.md` also carries the not-affiliated-with-Microsoft disclaimer and the AI-assistance disclosure.

## Scratch / probe output

When generating temporary artifacts, probe images, screenshots, rendered
previews, debug dumps, golden-test captures, etc., always write them into
`./.scratch/` inside this workspace. Create the folder if it doesn't exist.

- Do NOT write scratch output to the OS temp directory
  (e.g. AppData\Local\Temp\claude\...). Files there aren't reachable from
  VS Code's Explorer and clickable links to them don't resolve.
- Use descriptive filenames (e.g. `probe_full.png`, `zbd_texture_dump_01.png`)
  rather than UUIDs, so they're easy to find in the sidebar.
- When you produce such a file, print its workspace-relative path
  (e.g. `./.scratch/probe_full.png`) so it can be opened directly.
- `./.scratch/` is a tmp folder. If anything needs to stay as evidence move it into `./analyis`

## Architecture & key decisions

- **Engine:** Godot 4 .NET (C#). Consumes mech3ax extraction output (JSON/zip) via its own readers in `CSVM/src/Mech3/`, no Mech3DotNet or other package dependency.
- **Format reverse engineering:** happens in a fork of [mech3ax](https://github.com/TerranMechworks/mech3ax) (Rust) at `tools/mech3ax/`. Their byte-identical round-trip test harness (extract→repack) is the correctness standard. **The CS work stays in the fork, not upstream** (upstream dropped CS for maintenance reasons and is dormant), remotes, branch roles and the sync procedure are in `docs/tooling.md`.
- **Flight model:** parameterized by plane stats from extracted zrdr reader files, over the model decoded from the retail executable in [`docs/org/flightModel.md`](docs/org/flightModel.md), which is the authority for every flight quantity.
- **Division of labor:** the AI agent writes the Rust parsers, Godot code, and docs; the user reviews, playtests flight feel, and owns upstream/community communication.
- **Git: commit to `main`.** This is a single-developer repo with no internal PR workflow, so **do not create a branch** when asked to commit, commit straight to `main`. The user will say so explicitly if a particular change should go on its own branch. Pushing is still never automatic: commit when asked, push only when asked.

## Coding conventions
- Comments state what and why, briefly, never provenance (dates, plan/milestone/item references), never history, never instructions to a reviewer. If a comment's only content is where a change came from, it should not exist.
- **Use the terms in [`CONTEXT.md`](CONTEXT.md)**, and never a word that file lists under `_Avoid_`.
- **Comment length is capped, in `CSVM/src` and `CSVM.Tests`.** A comment block over its cap fails the commit; [`CheckCommentCaps.ps1`](CheckCommentCaps.ps1) is what the hook runs, and what you run yourself while editing.

  | comment | cap |
  |---|---|
  | `///` on a type | 12 lines |
  | `///` on a member | 6 lines |
  | `//` above a declaration | 6 lines |
  | `//` above a statement | 3 lines |

  Sentences are ≤ 25 words; a block is ≤ 6 sentences and covers one topic. A warning states the
  prohibition first and the reason second: `⚠ Do not remove the seen dedup; C1 and C4 still need it.`
- **What a comment holds depends on what it is attached to.** Above a statement, the code says the
  what, so the comment says only why. Above a `const`, field or member, the value itself is
  unrecoverable from the code, so the comment holds the binding rule and a pointer to the decode:
  `docs/org/<module>.md` or `docs/formats/<format>.md`.
- **Evidence lives in `docs/`, not in a comment.** Measurement tables, refuted hypotheses and how a
  value was arrived at go on the module's docs page. Before cutting a block, check that page
  actually covers the claim, and write it there in the same commit if it does not. The removed
  prose goes in the commit message body, as diagnosis narratives already do.
- **No XML doc on private members**, and no `<para>`, `<b>`, `<i>`, `<list>` or `<item>` anywhere:
  the build generates no documentation file (`.editorconfig` silences SA0001 for that reason), so
  they render for nobody. `<see cref>` and `<c>` stay; the IDE reads them.
- **Inspection labs are opt-in.** A lab builds no UI and changes no pixel until its key or its
  `--debug-*` flag is used, so an ordinary run and the pinned golden sweep never see it.
- **Every formatted number uses `CultureInfo.InvariantCulture`.** A German-locale machine
  otherwise renders `0,5` and corrupts logs, reports and parsed round-trips.
- **Log through `Log`, never `GD.Print`.** Only `Log` reaches the file sink and the `--log=` filter,
  so a `GD.Print` line is invisible to every scripted run and suite.

## Repo layout

One line each, **the extraction pipeline, the launch scripts and the mech3ax fork are in `docs/tooling.md`**; none of it is needed to write engine code.

- `CSVM/`, the Godot 4 .NET project (the actual remake; committed). See "Godot project" below.
- `CSVM/shaders/`, shared `.gdshaderinc` blocks `#include`d by the generated shaders (instance-uniform order, sRGB, fog, lights, clock).
- `CSVM/data/`, **committed** hand-authored engine config (not extracted assets), loaded via `res://`. Holds `stock_loadouts.json` (the 11 planes' stock weapon fit; see `docs/formats/loadouts.md`) and `effect_pools.json` (per-root effect-template pool sizes; see `docs/architecture/Utils.md` on `EffectPools`).
- `CSVM.Tests/`, the xUnit project (`dotnet test`): reader units on hand-authored fixtures + `extracted/` golden counts, skipped when absent.
- `CrimsonSkiesGame/`, the user's retail install (git-ignored): ZBD archives in `CrimsonSkiesGame/ZBD/` as chapters `C1`–`C5`, each with `IA1` / `M0x` / `MP1`–`3`; cutscenes are plain MPGs in `CrimsonSkiesGame/GOSDATA/ASSETS/GRAPHICS/MPG/`.
- `extracted/`, extraction output workdir (git-ignored), mirroring the game's ZBD structure. Loaders prefer an unpacked sibling folder over its `.zip`. Layout + what is deliberately not loaded: `docs/tooling.md`.
- `Extract.ps1`, the developer's extraction wrapper: builds, then runs the engine headless with `--extract=<install>` (install `CrimsonSkiesGame`, fork unzbd, data root the script's own folder unless given). It holds no extraction logic; the pipeline is `CSVM/src/Extraction/`, which the in-game Extract screen runs too. Details: `docs/tooling.md`.
- `RunGame.ps1` / `RunDev.ps1`, play and dev launch scripts (build + Godot; dev one prompts). Details: `docs/tooling.md`.
- `RunTests.ps1`, one command, one exit code: build → `dotnet test` → `--run-tests` (`-Shards`, default 4) → goldens (`-GoldenWorkers`, default 4) → perf (`-Perf`, A/B'd via the git-ignored `perf-history.jsonl`) → hitch (`-Hitch`, opt-in and last). Details: `docs/tooling.md`.
- `ExportRelease.ps1`, builds, headless-imports, and exports the "Windows Desktop" release preset to `.scratch/export/CSVM.exe`; checks the export templates are installed and creates `.scratch/export/` if missing before starting. Details: `docs/tooling.md`.
- `PublishRelease.ps1`, the publish in one run: version, `ExportRelease.ps1`, the zip's SHA-256, the annotated tag on the commit that was built, and the GitHub release carrying the zip (`-NotesFile` for the prose above the generated sections, `-DryRun`, `-TagSuffix` for a rehearsal run). Refuses a dirty tree, a dirty or unpushed `cs-anim`, or an existing tag, and never re-points one. Details: `docs/tooling.md`.
- `RunSandbox.ps1`, runs a release zip in a clean Windows Sandbox and collects what happened (`-NoVGpu` is the below-the-renderer-floor machine, `-Driver` chooses what runs inside, `-MapReadOnly` adds host folders the zip does not carry). Details: `docs/tooling.md`.
- `sandbox/`, the driver scripts `RunSandbox.ps1` runs inside the sandbox; `RendererFloor.ps1` launches the build, records the windows, dialogs, screenshots and logs, and flies a chapter when an extraction tree is mapped.
- `RunProbe.ps1`, **every ad-hoc scripted Godot launch goes through this** (`--screenshot=`, `--dump-*`, one-off `--run-tests=`): hidden desktop + streams redirected to files, so nothing flashes on screen or prints over the calling terminal. Never invoke the Godot exe directly for a probe. Details: `docs/tooling.md`.
- `HiddenDesktop.ps1`, dot-sourced by `RunTests.ps1`: runs every launch on a separate Windows desktop so no test window ever appears on screen. Details: `docs/tooling.md`.
- `CleanScratch.ps1`, sweeps `.scratch/` artifacts, finished `.claude/worktrees/` agent worktrees **and the unit suite's `%TEMP%\csvm-tests` scratch** (`-?` lists its switches). Spares backups, dirty worktrees and a temp root a live test run owns; leaves branches alone by default.
- `CheckCommentCaps.ps1`, the comment-length caps above, over `CSVM/src` and `CSVM.Tests`. Bare for the file:line list, `-Summary` for one line per file worst-first, or with paths for just those files. The line caps cover the whole scope; the 25-word sentence cap and the six-sentence block cap cover only the comment blocks the working tree has changed against HEAD (every block of a file named on the command line), since the tree carries older debt and a block is fixed by whoever next edits it. Scans the worktree the script file itself lives in, not the caller's working directory, so it is correct from any worktree regardless of where it is invoked. A pre-commit hook runs it; run it yourself while editing.
- `New-ItemId.ps1`, mints the next `BL-`/`CAP-`/`PT-` item ID (`-Kind BL`, optional `-Count n` to reserve a block). The counter sits in `.git/item-id-counters.json`, shared by all worktrees, incremented under an exclusive lock, so concurrent sessions can't mint the same number. **Never assign an item ID any other way, and run it for EVERY id rather than once per session**, deriving the next id by adding 1 (or reusing one it handed you earlier) leaves the counter behind the file, so the invented number is handed out again on the next call. Use `-Count n` when you need several at once. A pre-commit hook fails the commit if `backlog.md`/`playtest.md` define an ID twice.
- `InstallSdl2.ps1`, downloads the pinned SDL2 runtime the game reads flight sticks through into `tools/sdl2/`, SHA-256 checked; run once in the primary checkout (`-Verify` checks without installing). Details: `docs/tooling.md`.
- `tools/`, downloaded binaries (git-ignored): pinned mech3ax v0.6.1, the mech3ax fork, the Godot 4.7 .NET editor, the SDL2 runtime. The Linux release (`ExportRelease.ps1 -Linux`) also needs a one-time toolchain in WSL Debian: `build-essential`, `curl` and `musl-tools` from apt, rustup in `~/.cargo/bin`, and `rustup target add --toolchain 1.91.1 x86_64-unknown-linux-musl` for the version `tools/mech3ax/rust-toolchain.toml` pins. Details: `docs/tooling.md` (`-Linux`).
- `analysis/`, **committed** read-only analysis scripts + their `FINDINGS.md`, one dir per question. For instruments whose result `docs/` cites, because `.scratch/` is swept. No game data in them, ever. A directory no live file cites is deleted; the tag `analysis-archive` marks the tree before the bulk retirement, so `git show analysis-archive:analysis/<dir>/FINDINGS.md` recovers a retired one.
- `analysis/goldens/manifest.json`, the golden-image tripwire: the pinned `--det` shots as command line + raw-pixel md5. Hashes only, never pixels.
- `analysis/verification-budgets.json`, `RunTests.ps1`'s per-stage and total wall-time budgets ("Development verification loop" below has the awareness-only rule that reads them).
- `analysis/engine-suite-weights.json`, the measured per-suite engine weights the shard planner divides the catalog by, so one tree shards the same way every run.
- `docs/tooling.md`, the extraction pipeline, the launch scripts, and the fork's remotes/branches/sync procedure.
- `docs/architecture.md`, the Module index, one line per `CSVM/src` module, routing to `docs/architecture/<Namespace>.md`, where each module has one `##` entry (purpose, what it owns, what to read next). **Read a module's entry before changing it.**
- `docs/formats/`, the public reader-format reference, one page per format family; `README.md` is the index + shared reader conventions.
- `docs/cli.md`, the full per-flag CLI reference (this file keeps only the day-to-day table).
- `docs/verification.md`, how to verify a change here, and how the instruments lie. Read before measuring anything.
- `docs/menu-presentations.md`, the menu presentation contract read end to end (registration, lifecycle, features, seats, options, audio, launch, return, assets, the namespace seam) and the checklist a further presentation follows.
- `docs/PLAN-<name>.md`, the live plan, scheduled work as a checklist; "Current status" names the active one. **A completed plan is deleted in its closing commit, not archived**: the commit message records that it completed and what its last item was. Landed work is recorded in commit messages (`git log --grep=<ID>`). Live prose cites a completed plan by name and item (`PLAN-cockpit-panel C20`), never by path, and cites the pre-commit-message development log as "the archived development log". Both are read back from git: `git log --all --oneline -- docs/PLAN-<name>.md docs/plans/PLAN-<name>.md` finds the deleting commit `<sha>`, and `git show <sha>^:<path>` prints the file. The tag `docs-archive` marks the tree before the bulk removal: `git show docs-archive:docs/HISTORY.md` is the archived development log, `git show docs-archive:docs/plans/plans.md` the index of every plan completed up to it. The plan skeleton is `docs/agents/plan-template.md`; `/new-plan` scaffolds from it.
- `backlog.md`, unscheduled work, ordered by theme (Damage & destruction, Weapons & combat, …): every item one flat `BL-NNN` bullet with a type tag (`[Bug]`/`[Feature]`/`[Research]`/`[Tuning]`/`[Cleanup]`) and an optional status tag (`[Owed-playtest]`, `[Blocked: <blocker>]`). The TUNE list is the set of items tagged `[Tuning]`. Move items into a plan when scheduled; **delete when landed, a `FIXED`/closed entry does not stay here.** Its record belongs in the landing commit's message; its traps in `docs/verification.md` or as a comment on the member they bind. **If closing it leaves follow-up work, that follow-up becomes its own new entry with a `⚠ Traps` section** naming the rejected fixes and the misleading instruments, an open thread buried inside a section headed `FIXED` is invisible to anyone scanning for work.
- `playtest.md`, the at-the-controls checklist, holding **only what is actionable today** (`PT-nn`) plus one consolidated list of owed captures of the original (`CAP-nn`), each naming the `BL-` it unblocks. IDs are permanent and cited like `BL-nnn`. A test blocked on an unlanded fix does not live here, it rides its `backlog.md` entry as a `*Playtest after fix:*` line and comes back when the fix does. Deep evidence stays in `backlog.md`; keep the two in step.
- `playtest/`, captures staged for a `playtest.md` item, one subfolder per ID (`playtest/PT-03/`). Git-ignored, and unlike `.scratch/` **not swept by `CleanScratch.ps1`**, they survive until the item closes, then the folder goes with it.
- `OriginalScreenshots/`, user-captured reference shots + videos from the original game (git-ignored). `docs/` cites these by filename as evidence, so those citations resolve only in the user's local tree, **ask the user if a referenced capture is missing.**

### Development verification loop

- **During an edit, run the smallest red/green surface.** For one engine suite use
  `.\RunTests.ps1 -Suite <suite> -SkipUnits -SkipGoldens` (exact name; `-Filter` is still the
  substring form); for a unit use
  `dotnet test CSVM.Tests/CSVM.Tests.csproj --no-build --filter "FullyQualifiedName~<test>"`, or
  `.\RunTests.ps1 -UnitFilter "FullyQualifiedName~<test>" -SkipEngine -SkipGoldens` for the same
  filter with the summary block. Both loops measure about 3 seconds warm. The hitch check is opt-in
  (`-Hitch`), so nothing has to be passed to keep it out of them.
- **Broad development confidence is `.\RunTests.ps1 -Quick`:** build, the checked-in quick unit
  tier and quick engine tier, no goldens and no hitch, with every omitted surface printed as a
  `not checked:` line. A selector or filter matching nothing fails its stage rather than passing
  empty.
- **Before landing any change under `CSVM/`, run the complete `.\RunTests.ps1`.** Targeted and quick
  runs are partial by design and never satisfy that landing gate. `CSVM.Tests/`-only,
  documentation, and tooling changes do not require the full run.
- **Every stage prints its wall time against a budget from `analysis/verification-budgets.json`.**
  An `over budget` marker is awareness only and never changes the exit code, because a busy
  workstation must not fail correct code; `docs/tooling.md` holds the rule that set the numbers.

## Godot project (`CSVM/`)

Godot 4.7 .NET, C# / net8.0. Build & run:

```
dotnet build CSVM/CSVM.sln
GODOT --path CSVM res://scenes/Main.tscn -- --plane=player_bhawk
```

(First time only: run with `--headless --import` once before running scenes.)

**Module map, the index at [`docs/architecture.md`](docs/architecture.md) routes to the per-namespace file `docs/architecture/<Namespace>.md`.** Find the module in that index, then read only its `##` entry: `Grep "## src/Flight/Airframe/FlightModel.cs" -A 12 docs/architecture/` returns the whole entry. Read a module's entry before changing it.

- `src/Mech3/`, extraction readers, the GameZ→Godot builders, and the animation runtime: install → live world.
- `src/Flight/`, the aircraft as a flying, shooting, damageable thing, plus its HUD and stunt mode, in eight sub-namespaces, one folder each: `Airframe/` (`FlightController.cs`, the flying node, and `FlightModel.cs`, its physics, with collision and damage), `Weapons/` (fire control, the projectile pool, targeting, turrets), `Ai/` (the AI pilot and the surface hulls), `Camera/`, `Hud/`, `Modes/` (stunt, Dogfight, pause), `Hangar/` (the custom plane) and `Audio/`.
- `src/Effects/`, particle systems: puffers, the ambient cloud field, precipitation, the world wind.
- `src/UI/`, launchscreen, splitscreen rig, and the inspection labs (each with a scripted `--debug-*` twin), in six sub-namespaces, one folder each: `Boards/` (the widget library: the composed board and its view, the board menu, the splitscreen rig, the layer order), `Campaign/` (the campaign pages and the scrapbook), `Screens/` (`LaunchMenu.cs`, boot, cinema, load, pause and results boards, menu input), `Hangar/` (the hangar pages), `Overlays/` (debug and HUD overlays) and `Labs/`, beside `Menu/`, the presentation seam (`docs/menu-presentations.md`).
- `src/Video/`, the managed MPEG-1 decoder for the install's `.mpg` cinemas: system-stream demux, video decode, frames as pixel buffers. Holds no engine type.
- `src/Utils/`, session-wide services: clock, log, seed, shader time, config, startup profile, options, graphics mode. Determinism lives here.
- `src/Testing/`, the in-engine assertion harness behind `--run-tests`: the suites, their registry and fixtures. Nothing outside it depends on it except the `--run-tests` dispatch.
- `src/Tooling/`, runtime tooling the game and the harness share: the `--dump-*` probes and their wrappers, the `--screenshot`/`--shots` capture, the golden-image hash, the glTF export.
- `src/Bindings/`, the input binding model and the named-action seam: device identity, the tagged control, the binding list an action resolves through, the registry that resolves a device identity to a live pad, the seat device state a polling site reads its pad set through, a player's action map resolved once per tick, and the shipped default keymap with its versioned per-player file.
- `src/Sticks/`, flight sticks Godot does not enumerate, read through the pinned `SDL2.dll`: the gap-filling stick roster, its hot-plug and input gate, the per-frame pump, the per-model profile files and the generic single-stick default.
- `src/Session/`, six sub-namespaces, one folder each: `Launch/` (`Launcher.cs`, the Main.tscn root, and `GameSession.cs`, the per-launch session node it instantiates), `InstantAction/`, `Campaign/` (the campaign director and the profile), `Roster/` (the aircraft aggregate, livery and spawn resolution, the AI generators), `World/` (the simulation step, weather, effects, zeppelins, turrets, cutscenes) and `Objectives/` (the mission script and its rules).
- `src/` root, `SessionSpec.cs`, `SessionPaths.cs`, `Pads.cs`.
- `CSVM.Tests/`, the xUnit project: engine-free reader units. Anything reaching `GD.*` or a live `Node` belongs in `src/Testing/` instead.

Highest-traffic modules, so the common cases skip the index: `GameSession.cs` (session build), `FlightController.cs` (the flying node), `FlightModel.cs` (physics), `SceneBuilder.cs` (every mesh), `WorldBuilder.cs` (chapter worlds), `AnimRuntime.cs` (world animation), `Projectile.cs` (weapon fire), `Suites.cs` (golden counts).

### User args (after `--`)

**Flight is the default.** Any content arg builds a *flight* unless `--viewer` is present: `--plane=player_fury` flies the Fury and `--chapter=C4` flies over C4. `--viewer` gives the static inspection view, where the livery / mesh labs live. The damage lab now lives in both, F19 in `--viewer` drives a parked plane's visuals, F19 in `--fly` drives the flown plane's real HP, so `--fly` is redundant except with `--damage=`, which picks the parked viewer unless flight was asked for by name. A bare launch (no content arg) shows the launchscreen.

The day-to-day subset; `docs/cli.md` is the description of record. **[`docs/cli.md`](docs/cli.md) opens with an index of every flag, grouped**, covering the whole `--debug-*` family, the paint overrides, spawn/mission selection, scripted `--hold` input, the data-path overrides, and the deprecated `--campos`/`--spawn-at`/`--spawn-dir` spellings of the placement pair.

⚠ **These rows are glosses, not the spec: a behaviour change edits the `cli.md` bullet, and a row here only when the gloss went wrong.**  Adding a row is rarely right, the index is one file away.

| Flag | Does |
|---|---|
| `--chapter[=C1]` | which chapter world (`C1`/`C1B`/`C1C`/`C2`/`C2B`/`C3`/`C4`/`C5`); flown by default |
| `--stage=empty` | no *chapter* gamez: a collidable grid ground plane + the plane, booting in ~2 s, the flight/ballistics test stage |
| `--node=<cs_name>` | `--viewer`/`--anim-lab` build only that gamez subtree, auto-framed; multiple matches build the first, a miss lists candidates |
| `--plane=` | which aircraft; comma-separated gives one per splitscreen player |
| `--fly` | free flight (the default): world + skydome + plane + arcade controls; also hosts the damage lab (F19) on the flown plane |
| `--stunt` | flight + the mission's Danger Zones as timed fly-through objectives; a race with `--players` |
| `--vs` | "Dogfight": splitscreen free-for-all deathmatch on the `dogfight_ace` spawns; beats `--stunt` by fixed precedence |
| `--viewer` | the static inspection view; hosts the damage (F19), livery (L) and mesh (M) labs |
| `--freecam` | spectator mode: the live animated world, no aircraft, free-flying camera, click-selection |
| `--anim-lab` | the animation debugger: quiet world stage + def playback (`--play-anim=`, `--seed=`) on a fixed-dt clock; a transport button panel, the freecam camera, and click-to-follow the selection |
| `--players=N` | splitscreen 1–4 in one shared world, one pane/camera/HUD/pad each |
| `--pos=x,y,z` | place the mode's **subject**: the camera in `--freecam`/`--viewer`/`--anim-lab`, the plane in `--fly`/`--stunt` (bypassing the mission spawn list) |
| `--direction=x,y,z` | which way it faces there, view direction or nose. `--lookat=x,y,z` is the point form (and the `--viewer` orbit pivot). Quote comma args in PowerShell |
| `--view=1-9` | hold a numpad head-look snap direction for the run (2 dead ahead, 4/6 flanks, 8 belly); `--fly`/`--stunt` only. Swings the chase camera around the plane at the shared dynamic radius, or aims the head in first person, [`docs/cli.md`](docs/cli.md) |
| `--look=x,y` | hold a right-stick look deflection for the run, both in [−1, 1], +x right and +y up: the scripted twin of pushing the look stick, and the only way a headless run aims it. Aims the chase swing and the first-person head alike, so one run compares the two; a live stick beats it while deflected |
| `--screenshot=<path>` | render a few frames, save PNG, quit, the automated-verification workhorse |
| `--frames=N` / `--shots=N` | which sim frame the shot lands on (default 15), **a sim coordinate, not a wall-clock delay** / capture N consecutive frames |
| `--debug-anim` | log every live animation's pose and sound emitters once a second; conditions only when a verdict **flips** (a repeat line means a change) |
| `--perf` | log the frame-cost/draw-count split every 60 frames (the headless profiler stand-in) |
| `--run-tests[=filter]` | run the in-engine assertion suites, print the PASS/FAIL/SKIP table + `.scratch/test-report.json`, **exit nonzero on any failure** |
| `--log=` | console log filter, `cat[:level],…` over `anim`/`world`/`flight`/`weapons`/`sound`/`perf`/`test`/`ui`/`core`; every run always writes **everything** to `.scratch/logs/` (`logs/` in an exported build) regardless |
| `--det` | the determinism bundle: fixed-dt sim clock + master seed 1 + `--spawn=0` + pinned liveries + `--no-pads` + `--jitter=0`; **implied by every flag that drives and ends a session by itself**, `--screenshot=`, the `--dump-*` reports, `--damage-test`, `--effects-test`, `--weapon-test`, `--run-tests`, and announced as a `det …` log line |
| `--no-det` | opt back out, wall-clock sim and live randomness, **beating both the implication and an explicit `--det`** (`--det --no-det` runs on the wall clock) |
| `--seed=N` | the master seed every subsystem RNG derives from (spread, crash sound, spawn, liveries, anim dice, particles); pinned to 1 by `--det` |
| `--tex-override=<name>[=<color>]` | the named texture resolves flat magenta (or your colour) everywhere it is used, "is this thing drawing at all?" |
| `--tex-census[=names]` | every texture resolves to its own flat colour; map to `.scratch/tex_census.json`, per-texture pixel counts for a `--screenshot` beside it. **Pair with `--no-fog`**; counts are lower bounds, see [`docs/cli.md`](docs/cli.md) |
| `--collision[=show]` | build the world's colliders in a mode that builds none (freecam/anim-lab/viewer); `=show` opens the **C** wireframe overlay, but only in freecam/anim-lab, since C in `--viewer` is the mesh lab's cull cycler |
| `--no-pads` | ignore every gamepad, a drifting stick silently ruins a scripted run |
| `--mute` | skip flight audio, a **load-time** switch, so nothing plays *and nothing is counted or logged*; a muted baseline is blind to sound errors |
| `--volume=N` | master gain 0–1 (default 0 in a repo run and 1 in an exported build, `RunGame.ps1`/`RunDev.ps1` pass `--volume=1.0` so interactive play sounds). `--volume=0` is silent but **not** blind: audio still loads, plays, counts and logs, so a run is testable from `.scratch/logs/`. Also the `audio.volume` config key, which the flag beats |

the player controls during development are in `docs/controls.md`. **change them if the player input changes**

### Format gotchas

**The cross-cutting gotchas that bite constantly live in [`docs/formats/gotchas.md`](docs/formats/gotchas.md)**,  **Read it before writing any reader, transform, or shader code.**

**Never work out which campaign mission `CM17` is: [`docs/formats/campaign-missions.md`](docs/formats/campaign-missions.md) is the `CM01`-`CM24` ↔ `C<n>/M0<n>` ↔ `seq` lookup, both directions.**

Full validated format documentation lives in **`docs/formats/`**, one page per format family. **`README.md` there is the index + the shared reader conventions; start there** rather than duplicating its table here. **Rule: new decodes land with their docs page in the same change.**

## Agent skills

- Issue tracker: GitHub Issues on `Laeresh/CSVM` via `gh`, with `backlog`/`playtest`/`capture` labels in place of new `BL-`/`PT-`/`CAP-` ids. `backlog.md` and `playtest.md` hold only the entries filed before the switch, until each closes; `docs/PLAN-*.md` is still the live plan. See [`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md).
- Triage labels: the five canonical roles, unrenamed, created on the repo. See [`docs/agents/triage-labels.md`](docs/agents/triage-labels.md).
- Domain docs: single-context; glossary in `CONTEXT.md`, structural decisions in `docs/adr/`, the rest in `docs/` and git history. See [`docs/agents/domain.md`](docs/agents/domain.md).

## Current status / next step

**Fixed shape, five short paragraphs, ~15 lines / ~2 KB: this rule, where the project is, active plan + wave position, next items, pointers.** When work lands, the only edits allowed here are: advance the wave-position clause, swap the "Next" IDs, delete text. **Adding a sentence about the landed item is forbidden in every tense**,  If this section is longer after your edit than before it, the edit was wrong.

**Where the project is.** Milestones 1 through 5 are delivered: 11 flyable aircraft over 8 animated chapter worlds, launched from the in-game menu, with original liveries, weather, world animation and sound; extraction is complete and round-trips byte-identically. M3 added guns, rockets and world destructibles that take damage, die, lose collision, throw debris and reset; M4 added the combat AI (aircraft that patrol, engage, evade and die, turrets, zeppelins, pilot voice), and all four Instant Action mission types plus the 2–4-player splitscreen Dogfight deathmatch are playable and scored. M5 added the single-player campaign: per-profile progression across the cabin, briefing and flight-check screens, and missions that run their authored `objectives.zrd` choreography with intro cutscenes, letterbox and campaign wingmen.

**Active plans:** [`docs/PLAN-enhanced-graphics-2.md`](docs/PLAN-enhanced-graphics-2.md) at Wave A, orchestrated on branch `enhanced-graphics-2`; [`docs/PLAN-linux-port.md`](docs/PLAN-linux-port.md) at Wave B, orchestrated on branch `linux-port`.
Next: `BL-693` carries what input rebinding left open; CM13's race pace, `PT-119`'s two CM14 sorties and the enhanced mode's pass against `PLAN-enhanced-graphics`'s Open judgements are owed at the controls.

Use the targeted/quick development loop above, then verify landed code with the complete
**`.\RunTests.ps1`**; read [`docs/verification.md`](docs/verification.md) before measuring.

The owed at-the-controls playtests are in [`playtest.md`](playtest.md).

Everything else unscheduled, known issues, deferred items, fidelity questions, the TUNE list, is in `backlog.md`; keep it updated as items land or get scheduled.
