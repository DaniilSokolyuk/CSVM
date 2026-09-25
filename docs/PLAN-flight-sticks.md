# Flight sticks, generic joystick support through SDL2

**ACTIVE PLAN** (written 2026-09-25). It sits in `docs/`, which by this repo's convention makes it
a live plan; PROJECT_CONTEXT.md's "Current status" names it. When every item lands, the closing
commit deletes this file, records the completion in its message, and clears the "Current status"
pointer; any live prose linking this file by path is unlinked in the same commit.

This plan makes DirectInput-only flight hardware (the user's VKBsim Gladiator EVO HOSAS, and any
similar stick, pedal set or throttle) detectable, bindable and flyable in CSVM. Godot 4.7's bundled
SDL3 reports none of these devices on the user's machine, so the plan adds a second input stack:
`SDL2.dll` read through P/Invoke, filling only the gaps Godot's roster leaves. On top of that it
adds per-model stick profiles (shareable JSON files that switch automatically on which companion
sticks are connected), full-axis and absolute-throttle bindings, stick capture across 128 buttons,
8 axes and hats, and a Stick column on the original-style KEYS AND BUTTONS page.

Out of scope: gamepads (they stay on Godot's path exactly as today), splitscreen seats flying a
stick (every stick belongs to seat 1), a response-curve or deadzone slider UI (deadzones are
per-binding and edited in the profile files), non-Windows builds (the only export preset is
Windows Desktop), and finding out why SDL3's DirectInput path enumerates nothing here (an upstream
report is optional and not an item). No item is drawn from `backlog.md` or a `backlog` GitHub
issue; everything here comes from one grilling session and the probes run during it.

## Milestone goal

- A VKB Gladiator EVO L and R, plugged in together or alone, appear in CSVM with their real names,
  and every axis, button and hat on them can be bound.
- A stick flies pitch, roll and yaw linearly past a small per-binding deadzone, without the pad's
  `StickCurve`.
- Throttle works either as an absolute lever (new **Throttle (lever)** action) or as a rate through
  the existing ThrottleUp/ThrottleDown pair bound full-axis.
- Stick bindings live in per-model profile files under `user://stick_profiles/`, override shipped
  read-only profiles, can be shared by copying the file, and switch live when a companion stick is
  plugged in or out.
- A single unknown stick-shaped device flies out of the box through a generic default; the shipped
  VKB solo-R and HOSAS profiles cover the user's hardware.
- Sticks navigate menus (hat, trigger confirms, a second button backs out).

**Godot's gamepad path is not touched.** SDL2 only adds devices whose VID/PID Godot's roster lacks,
so a pad behaves exactly as it does today, and a future Godot/SDL3 that sees these devices silences
the SDL2 path on its own.

## Decisions (2026-09-25)

| # | Question | Decision |
|---|---|---|
| 1 | Where do stick readings come from, given Godot 4.7 sees no DirectInput-only device? | **Ship `SDL2.dll` (zlib) and read it through P/Invoke**, proven on the user's machine; no NuGet package. |
| 2 | Which devices does the SDL2 reader own? | **Gap-filler**: only devices whose VID/PID Godot's roster lacks. |
| 3 | How are stick bindings keyed, and which seat owns a stick? | **Per model (VID/PID), never a placeholder; every stick belongs to seat 1.** Refined by 7b from per-unit GUID to per model. |
| 4 | Shape of an analog stick binding | **Full-axis binding** with inferred invert, resolving into the existing positive/negative action pairs. |
| 5 | Absolute throttle lever | **New absolute Throttle (lever) action**; it takes over while the lever moves, rate keys still work. |
| 5b | Throttle on a spring-centred stick (the HOSAS L stick) | **ThrottleUp/Down bound full-axis as a rate**; absolute stays available for levers and wheels. |
| 6 | Response curve for stick axes | **Linear past a tiny per-binding deadzone, rescaled to 0..1**; sticks bypass `StickCurve`. |
| 7 | Bindings before the player binds anything | **Generic single-stick default, plus shipped per-model profiles, shareable as files.** Ship two examples: VKB solo R and VKB HOSAS. |
| 7a | Which unprofiled sticks get the generic default | **Only when exactly one** unprofiled device is connected. |
| 7b | Profile vs the player's edits | **Live, keyed by model**: stick rows are stored in the profile file, not in `bindings_pN.json`; editing edits the file. |
| 7c | What one profile file covers | **One file per model.** |
| 7d | One model, several layouts (R solo vs R in a HOSAS) | **Auto by companions**: a file may name companion models; the most specific file whose companions are all connected is active. |
| 8 | Where user profile files live | **`user://stick_profiles/`**, plus an "Open profiles folder" button in the controls screen. Shipped profiles are read-only; the first edit writes a user copy that overrides them. |
| 9 | Capture gesture for a full axis | **Move toward the row's direction**: a stick axis captured on either row of a pair binds the whole axis with invert inferred; clearing from either row clears both. |
| 10 | Do sticks drive menus? | **Yes**: hat navigates, trigger confirms, a second button backs out. |
| 11 | Joystick-class devices that are not flight sticks (the Tartarus) | **The generic default needs a stick shape**: at least 3 axes, resting near centre at connect. A profile can mark a model "ignore". |
| 12 | HOSAS throttle deadzone | **L stick Y as a rate, 0.08 deadzone**; flight axes stamp 0.02. |
| 12b | Deadzone configuration | **Per binding in the profile files**, honoured 0..0.95 and kept across a re-save. No UI slider in this plan. |
| 13 | Where `SDL2.dll` comes from | **Pinned, SHA-256-checked download into `tools/sdl2/`**, copied beside the exe by `ExportRelease.ps1` with SDL's license. |
| 14 | Who authors the shipped VKB profiles | **The user binds both layouts in-game; the resulting files are committed.** |
| 15 | Original-style KEYS AND BUTTONS page (only Control A / B) | **Add a Stick column** showing and capturing only stick bindings. |
| 15b | Capturing into the Stick column when bindings exist | **Replace per device**: at most one binding per action per stick model; a full-axis pair counts as one. |
| - | Settled by default, not asked | Sticks obey `--no-pads` (so `--det`) and the focus gate like pads; hot-plug by polling SDL2 events each frame, with live profile switching; a missing `SDL2.dll` means no sticks and one log line; stick input reaches the flight model through its own action source. |

## ⚠ Read this before implementing anything

| # | The wrong claim | How it died |
|---|---|---|
| 1 | "Godot filters out joysticks that are not gamepads." | `drivers/sdl/joypad_sdl.cpp` at `4.7-stable` opens a non-gamepad through `SDL_OpenJoystick` in the `SDL_EVENT_JOYSTICK_ADDED` handler with no type, axis-count or hint filter. SDL itself never raises the event for these devices. |
| 2 | "The hidden-desktop probe showing zero pads proves detection fails" (or, the reverse, "the hidden desktop is why it shows zero"). | Neither followed from that run. A foreground probe the user ran also reported `count=0`, which is what established the failure. |
| 3 | "Setting `SDL_JOYSTICK_WGI=1` rescues Godot." | Standalone SDL 3.4.16 with WGI sees all three devices (with generic names), but Godot 4.7 with the variable set inside the launching script still reports `count=0`. The first attempt proved nothing: the `!` prefix runs through bash, so a PowerShell `$env:` assignment there never reaches the process. |
| 4 | "Two VKB sticks share a GUID, and `DeviceRegistry` drops one." | R is `VID_231D&PID_0200`, L is `PID_0201` (DirectInput OEM names `VKBsim Gladiator EVO R` / `L`), so their GUIDs differ. |
| 5 | "godotengine/godot#82136 is this bug." | That issue is Godot 4.1 with a VKB T-Rudder, before Godot's input moved onto SDL (4.5). Same symptom, different code path. |

| Confidence | Items | What that means for you |
|---|---|---|
| **Traced to an exact mechanism in code, with the data that proves it** | none | |
| **Direction sound, magnitude a judgement call** | A2 (detection split measured this session) | The *what* is settled; deadzones and the stick-shape thresholds are TUNE. |
| **Leads only, no mechanism yet** | A1, A3, B4-B6, C7-C9, D10-D12, E13 | Design settled by discussion, not by code. Budget for the seams to look different up close. |

## What the data actually ships

Probe results from 2026-09-25 on the user's machine (Windows 11 Pro 10.0.26200, VKB firmware
version 8600 per SDL's HIDAPI log):

| Stack | L | R | Tartarus | Notes |
|---|---|---|---|---|
| Godot 4.7 (bundled SDL3), foreground | no | no | no | `count=0`; `--verbose` prints `SDL: Init OK!` and no "Joystick connected" line. |
| Godot 4.7 + `SDL_JOYSTICK_WGI=1` | no | no | no | `count=0` |
| SDL 3.4.16 standalone (PySDL3), default hints | no | no | no | `count 0`; its HIDAPI layer lists both VKBs by full name with `driver = NONE (DISABLED)`. |
| SDL 3.4.16 standalone + `SDL_JOYSTICK_WGI=1` | yes | yes | yes | All three named `HID-konformer Gamecontroller`. |
| SDL 2.32.10 standalone (pygame-ce) | yes | yes | yes | Correct names, see below. |

SDL 2.32.10's view of the devices:

| Device | GUID | Axes | Buttons | Hats |
|---|---|---|---|---|
| VKBsim Gladiator EVO L | `03003fc91d2300000102000000000000` | 8 | 128 | 1 |
| VKBsim Gladiator EVO R | `03005fcf1d2300000002000000000000` | 8 | 128 | 1 |
| Joystick (Razer Tartarus V2) | `03004280321500002b02000000000000` | 6 | 24 | 1 |

Godot's `SDL_build_config_private.h` at `4.7-stable` compiles in `SDL_JOYSTICK_DINPUT`, `_RAWINPUT`,
`_WGI`, `_XINPUT` and `_HIDAPI`, so a backend that isn't compiled in doesn't explain the empty
roster. Why SDL3's DirectInput path finds nothing here was not established.

CSVM-side walls, independent of detection:

- `CSVM/src/Bindings/ControlCapture.cs:48-61, 210-224`: capture scans buttons `0..JoyButton.SdlMax`
  (21) and axes `0..JoyAxis.SdlMax` (6) only; hats are never scanned (lines 12-14).
- `CSVM/src/Bindings/SeatDeviceState.cs:57-90`: a seat reads every pad through one placeholder
  (`DefaultBindings.AnyPad`, `DefaultBindings.cs:22`), ORing buttons and taking the largest axis,
  and `HatState` always answers `None`.
- `CSVM/src/Bindings/GodotDeviceState.cs:42-52`: hat 0 is the d-pad buttons; no other hat exists.
- `CSVM/src/Bindings/Binding.cs:98-101`: an axis binding covers one half of its travel and reports
  raw travel past the deadzone, not rescaled.
- `CSVM/src/Bindings/DefaultBindings.cs:100-107`: every flight axis already ships two bindings (a
  key and a pad control).
- `CSVM/src/Flight/Airframe/FlightController.cs:2436`: `StickCurve` is a 0.15 deadzone plus a
  squared response, applied to pad pitch and roll (lines 4280-4281).
- `CSVM/src/Flight/Airframe/FlightController.cs:4296-4311`: `_throttleSetting` is a continuous 0..1
  lever that rate input moves at `ThrottleRate` and the engine slews toward.
- `CSVM/src/Bindings/BindingStore.cs:68, 163`: the keymap is `bindings_p<N>.json`; an axis token
  already carries its deadzone (`axis:<i><sign>@<dz>`).
- `CSVM/src/UI/Menu/Original/OriginalOptionsScreen.cs:911-942, 2517-2519`: the original-style page
  shows Control A (binding 1) and Control B (binding 2 plus "+N more"); capturing on B replaces
  binding 2.
- `CSVM/src/UI/Menu/ControlsFeature.cs:29, 213-217` and `CSVM/src/UI/Screens/LaunchMenu.cs:2937`:
  the remake Controls screen has no per-action cap (4 shown, then "+N more", and an `[add]` slot).

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

### Wave A, SDL2 bridge and stick roster

1. ☑ Pinned SDL2 download into `tools/sdl2/`, on the DLL path for every launch script and the release zip
2. ☐ SDL2 P/Invoke bridge: gap-filling stick roster, hot-plug, gates, logging
3. ☐ Stick device state: 128 buttons, 8 axes, hats, per-model identity, owned by seat 1

### Wave B, binding model

4. ☐ Full-axis binding kind, and hats made bindable for sticks
5. ☐ Stick action source in the flight model: linear, bypassing `StickCurve`
6. ☐ Absolute Throttle (lever) action with the takeover rule

### Wave C, profiles and defaults

7. ☐ Per-model stick profile files with companions, shipped vs user override, ignore flag
8. ☐ Generic single-stick default for exactly one stick-shaped unprofiled device
9. ☐ Menu navigation from sticks

### Wave D, capture and screens

10. ☐ Stick capture: full range, hats, full-axis inference, deadzone stamping, labels
11. ☐ Remake Controls screen: stick rows saved to the active profile, "Open profiles folder"
12. ☐ Original-style KEYS AND BUTTONS page: Stick column, replace per device

### Wave E, shipped profiles

13. ☐ The user binds VKB solo-R and HOSAS in-game; commit the files as shipped profiles

## Dependency and parallelism notes

A1 blocks A2 (the DLL must load), and A2 blocks A3. A3 blocks all of Waves B-D. B4 blocks B5, B6,
C7 (the profile file stores full-axis tokens) and D10. C7 blocks C8, C9, D11 and D12. D10 blocks
D11 and D12. E13 needs everything before it and the user at the controls.

Can run in parallel: B5 and B6 once B4 lands, but both edit
`CSVM/src/Flight/Airframe/FlightController.cs` around `ReadKeyboard`, so **not in parallel
worktrees**. C8 and C9 both touch the generic default's layout, so run them in one worktree or in
sequence. D11 (`ControlsFeature.cs`, `LaunchMenu.cs`) and D12 (`OriginalOptionsScreen.cs`) have
separate file ownership and can run in parallel after D10, as long as the shared save path from
D11 lands first or is agreed up front. <TODO: confirm D11/D12 file ownership once C7's save seam
exists.>

---

# Wave A, SDL2 bridge and stick roster

## A1 ☑ Pinned SDL2 download into `tools/sdl2/`, on the DLL path for every launch script and the release zip

**Landed.** `InstallSdl2.ps1` (repo root) is the setup step, following the `tools/` pattern of a
git-ignored download installed once in the primary checkout and borrowed by worktrees through
`CSVM_DATA_ROOT`. It pins SDL **2.32.10**, the newest 2.32.x release and the build the pygame-ce
probe saw the sticks with:

| File | Source | SHA-256 |
|---|---|---|
| release zip | `https://github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10-win32-x64.zip` | `6CF9706EEFD0A4A06DC764007934D428AFAF029FABDD408A9E646048C91E18FB` (equal to GitHub's published asset digest) |
| `SDL2.dll` | that zip | `B37740A72A7A9706216DF9F0134894BB7A850B356FD149398C67D874CBCFACB4` |
| `README-SDL.txt` | that zip | `F17D8919136F9627468B4DFFBD7BDDD188EF2AAA8ED21914D2107D1C759E99D7` |
| `LICENSE.txt` (zlib) | `https://raw.githubusercontent.com/libsdl-org/SDL/5d249570393f7a37e037abf22cd6012a4cc56a71/LICENSE.txt` | `97F35B302B361680EC1E891E95D2D52097BB95ABFF361434916D99DC1305F127` |

The runtime zip carries no licence, only `README-SDL.txt` and a `.git-hash` naming commit
`5d249570...`, which is the `release-2.32.10` tag; the licence is read from that commit. Every file
is hashed in staging before any installed file is replaced. `-Verify` installs nothing and returns
the version, commit and DLL hash, and is the one place the pins live. `ExportRelease.ps1` runs it
before building, copies `SDL2.dll`, `README-SDL.txt` and `LICENSE-SDL2.txt` to the zip root beside
`CSVM.exe`, and adds an `SDL2.dll` section (version, source tag, commit, DLL hash) to
`BUILD-INFO.txt`. `packaging/MANIFEST.md` gained the three rows, `docs/tooling.md` the section
"SDL2 for flight sticks", and `PROJECT_CONTEXT.md` the script's bullet.

**No launch script changed, and none alters `PATH`.** The C# side loads the DLL by absolute path
(Decision 13's resolver option), so `RunGame.ps1`, `RunDev.ps1`, `RunTests.ps1` and `RunProbe.ps1`
already reach it: Godot inherits `CSVM_DATA_ROOT` from them, which is the only input the resolver
needs beyond paths the process knows. **The wiring contract A2 implements** (also in
`docs/tooling.md`): `NativeLibrary.TryLoad(<absolute path>)` on the first of these that exists,
through a `NativeLibrary.SetDllImportResolver` for the name `SDL2` or an explicit handle, never
`SetDllDirectory`, `PATH` or the OS default search:

1. `<folder of OS.GetExecutablePath()>/SDL2.dll` (the exported build).
2. `<Launcher's repo root>/tools/sdl2/SDL2.dll`, the repo root being `res://`'s parent in an
   editor-hosted run.
3. `$CSVM_DATA_ROOT/tools/sdl2/SDL2.dll` (the worktree fallback, as `RunProbe.ps1:63-66` does for
   Godot).
4. `<folder of OS.GetExecutablePath()>/../../sdl2/SDL2.dll`, the `tools/` of the checkout whose
   Godot is running, which covers a worktree launched without `CSVM_DATA_ROOT`.

No candidate, or a failed load, means no sticks and one log line naming the paths tried; the launch
continues. An `SDL2.dll` found through `PATH` is an unpinned build and is not used. A2 may log the
loaded version through `SDL_GetVersion`, which needs no `SDL_Init`.

**Verified.** <pending orchestrator run>

**Original approach (kept for reference).**

**Goal.** A fresh setup fetches the official SDL2 Windows x64 runtime, every dev launch
(`RunGame.ps1`, `RunDev.ps1`, `RunTests.ps1`, `RunProbe.ps1`) and every worktree loads it, and the
release zip carries `SDL2.dll` beside the exe with SDL's license text.

**Evidence (confidence: lead-only).** The repo tracks no binaries and gitignores `/tools/`
(`.gitignore`); Godot itself lives in `tools/godot/` with a `CSVM_DATA_ROOT` fallback for
worktrees (`RunProbe.ps1:63-66`). The only export preset is Windows Desktop
(`CSVM/export_presets.cfg`). SDL 2.32.10 is the version proven to see the sticks (pygame-ce probe).

**Approach.** A setup step downloads the libsdl-org `SDL2-2.32.x-win32-x64.zip` release, checks a
pinned SHA-256, and extracts `SDL2.dll` and the license to `tools/sdl2/`. Launch scripts resolve it
with the same `CSVM_DATA_ROOT` fallback Godot uses and make it loadable (the process DLL search
path or an explicit `NativeLibrary.SetDllImportResolver` in A2). `ExportRelease.ps1` copies it
beside the exe with the license. The release, its hashes, the setup script and the resolver
choice are answered under **Landed** above.

**Model recommendation.** A mid-tier model: a download script, an export payload row and docs, no
engine code.

**Verify.** `InstallSdl2.ps1` installs, is idempotent, and refuses a tampered file or a wrong pin
without touching the installed copy; the installed DLL loads by absolute path in a 64-bit process
and reports 2.32.10; `ExportRelease.ps1` parses and its `-Verify` call returns the facts
`BUILD-INFO.txt` quotes. A fresh-worktree load through the fallback and a run with the DLL removed
are A2's to show, since until A2 nothing in the game loads it. The release zip listing `SDL2.dll`
and `LICENSE-SDL2.txt` needs a full `ExportRelease.ps1` run, which opens a rendering window for
the shader bake.

**⚠ Traps.** Never junction or symlink `tools/` from a worktree into the main checkout (CLAUDE.md);
use the `CSVM_DATA_ROOT` fallback. Keep any script that writes repo files pure ASCII with explicit
`-Encoding utf8`.

## A2 ☐ SDL2 P/Invoke bridge: gap-filling stick roster, hot-plug, gates, logging

**Goal.** CSVM initialises SDL2's joystick subsystem, lists every device whose VID/PID Godot's
roster lacks, follows plugs and unplugs live, reads nothing while `Pads.InputBlocked` holds
(`--no-pads`, `--det`, window unfocused), and logs the stick roster through the run log. A missing
`SDL2.dll` means no sticks and one log line.

**Evidence (confidence: direction-sound).** Measured this session: SDL 2.32.10 sees L, R and the
Tartarus with correct names while Godot 4.7 sees none (see "What the data actually ships"). The
gap-filler rule and the gates are design decisions (Decisions 2 and "settled by default").
`Pads.cs:40-42` holds the input gate; `Pads.cs:165-188` shows the roster-logging pattern; Godot
pads already expose `vendor_id`/`product_id` through `Input.GetJoyInfo` (`Pads.cs:197-203`).

**Approach.** A small P/Invoke surface: `SDL_Init(SDL_INIT_JOYSTICK)`, the device-count, open,
name, GUID, vendor, product, axis, button and hat calls, and an event pump each frame for
added/removed events. Stick identity is the model (VID/PID), per Decision 3/7b. Exclude any
VID/PID present in `Pads.Connected()`. Route logging through `Log`, never `GD.Print`.
<TODO: where the per-frame pump runs (Launcher, a node, or the seat refresh); whether SDL2 needs
`SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS` given the focus gate; how two identical units of one
model are handled (Decision 3 accepts they merge).>

**Model recommendation.** <TODO>

**Verify.** <TODO: unit tests over the pure gap-filler rule; a foreground run by the user showing
the stick roster log line with both VKBs; `--no-pads` silences them.>

**⚠ Traps.** SDL2 runs beside Godot's statically linked SDL3 in one process; keep SDL2 to the
joystick subsystem only. A probe on the hidden desktop is not evidence about device detection
(⚠ table row 2); any detection check needs the user's foreground run. The Tartarus enumerates as a
joystick; its F13+ debug keys arrive through its keyboard interface and must keep working.

## A3 ☐ Stick device state: 128 buttons, 8 axes, hats, per-model identity, owned by seat 1

**Goal.** `IDeviceState` answers button, axis and hat reads for a stick's model identity, and seat
1's reader includes every connected stick, keeping L and R apart instead of merging them behind
`AnyPad`.

**Evidence (confidence: lead-only).** `SeatDeviceState.cs:57-90` merges all pads behind one
placeholder and answers `HatDirection.None`; `DeviceRegistry.cs:20-35` keys pads by GUID with a
first-wins collision rule; `GodotDeviceState.cs:42-52` has only hat 0. Decision 3: every stick
belongs to seat 1.

**Approach.** Add a stick device identity (model-keyed; <TODO: a new `DeviceKind` or a
`DeviceId.Joypad` id prefix>), a stick state source reading A2's bridge, and make seat 1's reader
answer for stick identities alongside the pad placeholder. Stick reads go through the same
`Pads.InputBlocked` gate.

**Model recommendation.** <TODO>

**Verify.** <TODO: suites with a fake stick source proving L button 1 and R button 1 resolve apart,
hats resolve per direction, and a splitscreen seat never reads a stick.>

**⚠ Traps.** Do not route sticks through `AnyPad`; that merge is the reason this item exists. Keep
the pad path's phantom-device policy (`Pads.cs:82-110`) untouched.

# Wave B, binding model

## B4 ☐ Full-axis binding kind, and hats made bindable for sticks

**Goal.** One stored binding maps a whole physical axis onto an action pair (PitchUp/PitchDown,
RollLeft/RollRight, YawLeft/YawRight, ThrottleUp/ThrottleDown) with an invert flag and a deadzone,
producing 0..1 per side rescaled from the deadzone edge; stick hats can be stored and resolved.

**Evidence (confidence: lead-only).** Today an axis binding is a half-axis with raw travel past the
deadzone (`Binding.cs:98-101`); the token already carries a deadzone (`BindingStore.cs:163`);
`BindingControl.Hat` exists but `BindingStore` rejects the token and no capture scans hats
(`ControlCapture.cs:12-14`, `SeatDeviceState.cs:87-90`). Decisions 4, 6, 12b.

**Approach.** A new control kind (or an axis flag) serialised as its own token, resolving into the
positive action on one side and the negative on the other, so `ActionSnapshot.Axis` and the flight
model need no new API. Accept deadzones 0..0.95 from files. Enable hat tokens for stick identities.
<TODO: the token grammar; how `ActionMap.SameControl` treats a full axis against a half-axis on the
same physical axis; the schema-version bump in `BindingStore`.>

**Model recommendation.** <TODO>

**Verify.** <TODO: round-trip tests for the token incl. a hand-edited deadzone; resolve tests for
invert, rescale and both sides.>

**⚠ Traps.** Pads keep half-axis bindings and their current behaviour; this kind is for sticks.
Deadzone values are TUNE, not fact.

## B5 ☐ Stick action source in the flight model: linear, bypassing `StickCurve`

**Goal.** Stick pitch, roll and yaw reach the plane linearly past the binding's own deadzone, while
pad input keeps `StickCurve` and keyboard input keeps `StickRamp`; a stick bound to
ThrottleUp/ThrottleDown moves the throttle as a rate scaled by deflection.

**Evidence (confidence: lead-only).** `FlightController.cs:4280-4283` reads pad axes through
`_padActions` and applies `StickCurve` (`:2436`, 0.15 deadzone + squared) to pitch and roll;
`:4296-4300` adds pad throttle into the rate. Decisions 5b and 6.

**Approach.** Give the flight model a stick action source separate from `_padActions` and combine
it without `StickCurve`. <TODO: how pad, stick and keyboard contributions combine when several are
active (sum, clamp, or largest magnitude); where the source is built for seat 1.>

**Model recommendation.** <TODO>

**Verify.** <TODO: a harness check that a stick at 10% deflection produces 10%-scaled pitch
command, and the pad path's outputs are bit-identical before and after; the user at the controls.>

**⚠ Traps.** Flight feel is the user's judgement, not an instrument's. Do not retune `StickCurve`
for pads in this item.

## B6 ☐ Absolute Throttle (lever) action with the takeover rule

**Goal.** A new **Throttle (lever)** action sets `_throttleSetting` directly from a lever's
position (0 to max); while the lever is still, keyboard and pad rate keys move the throttle, and
the lever takes over again the next time it moves.

**Evidence (confidence: lead-only).** `_throttleSetting` is continuous 0..1
(`FlightController.cs:4296-4304`), and `ScheduledThrottle`/`RequestedThrottle` already override it,
so an absolute source fits the same slot. Decision 5.

**Approach.** Add the action to `InputAction`, its label and the Flight context; map axis -1..1 to
0..1 with the binding's invert; track the last lever value and apply it only on change beyond a
small epsilon. <TODO: the epsilon; ordering against `ScheduledThrottle`/`RequestedThrottle`;
whether ThrottleSet0..8 digits count as rate input for the takeover rule.>

**Model recommendation.** <TODO>

**Verify.** <TODO: unit test of the takeover rule; the user with a lever or wheel, if one is
available.>

**⚠ Traps.** A spring-centred stick bound here drops to half throttle on release; the HOSAS profile
uses the rate pair instead (Decision 5b).

# Wave C, profiles and defaults

## C7 ☐ Per-model stick profile files with companions, shipped vs user override, ignore flag

**Goal.** Stick bindings load from and save to one JSON file per model. A file may name companion
models; for each connected model the most specific file whose companions are all connected is
active, and it switches live on hot-plug. Shipped files are read-only; a user file for the same
model and companions in `user://stick_profiles/` overrides them. A file can mark a model "ignore"
and carries a short display name ("R", "L").

**Evidence (confidence: lead-only).** Decisions 7, 7b, 7c, 7d, 8, 11, 12b. The per-player keymap
format is `BindingStore.cs` (`bindings_p<N>.json`, versioned, atomic write at `:232`).

**Approach.** A profile store beside `BindingStore`, reusing its token format from B4, its atomic
write and its versioning. Stick rows are written to the active profile on Accept rather than to
`bindings_p1.json`. Hand-edited deadzones survive a re-save. <TODO: the JSON schema (model key,
companions, short name, ignore, contexts, rows); the shipped profiles' `res://` location; file
naming; the tie-break when two files match equally.>

**Model recommendation.** <TODO>

**Verify.** <TODO: tests for companion selection (R alone vs R+L), override precedence,
copy-on-write, ignore, deadzone preservation; a hot-plug test through the fake stick source.>

**⚠ Traps.** Two identical units of one model share a profile and cannot be told apart (accepted,
Decision 3/7b). A user edit must never write into the shipped `res://` file.

## C8 ☐ Generic single-stick default for exactly one stick-shaped unprofiled device

**Goal.** When exactly one connected, non-ignored device has no matching profile and looks like a
flight stick (at least 3 axes, axes resting near centre at connect), it gets: axis 0 roll, axis 1
pitch, axis 2 yaw, button 0 primary fire, button 1 secondary fire, hat navigating menus (C9). Any
other case gets nothing, and the controls screen says the device needs binding.

**Evidence (confidence: lead-only).** Decisions 7, 7a, 11. The Tartarus reports 6 axes, 24 buttons
and 1 hat under SDL2 and would otherwise qualify as the one unprofiled stick once the VKBs carry
profiles.

**Approach.** Evaluate on connect and on roster change; the default lives in memory until the
player edits it, at which point it becomes a user profile for that model. <TODO: the "near centre"
threshold; whether the check re-runs when a device's axes settle after connect; the axis signs
for pitch; whether the default's rows are written out immediately or only on edit.>

**Model recommendation.** <TODO>

**Verify.** <TODO: tests for 0, 1 and 2 unprofiled devices, an ignored device, and a device with
two axes or an off-centre axis at connect.>

**⚠ Traps.** Axis indices on an unmapped stick are a convention, not a standard; that is why the
default is limited to the single-device case. The shape thresholds are TUNE.

## C9 ☐ Menu navigation from sticks

**Goal.** A stick's hat navigates menus, its trigger confirms and a second button backs out, in the
generic default and in the shipped profiles, and menu contexts accept stick bindings in the
controls screens.

**Evidence (confidence: lead-only).** Decision 10. Menu input reads pads through `MenuInput`
(`CSVM/src/UI/Screens/MenuInput.cs`), not yet traced for this plan.

**Approach.** <TODO: read `MenuInput` and the menu contexts in `DefaultBindings` and say where stick
bindings enter menu polling, and how the join flow treats a stick (seat 1 only, Decision 3).>

**Model recommendation.** <TODO>

**Verify.** <TODO>

**⚠ Traps.** Sticks must not join splitscreen seats through the menu join flow.

# Wave D, capture and screens

## D10 ☐ Stick capture: full range, hats, full-axis inference, deadzone stamping, labels

**Goal.** Capturing on any row accepts a stick's buttons 0..127, axes 0..7 and hat directions. A
stick axis moved on either row of an axis pair binds the whole axis to the pair with invert
inferred from the direction moved; on the Throttle (lever) row it binds the absolute action.
Captured flight axes get a 0.02 deadzone, rate-throttle axes 0.08. Labels use the profile's short
name ("R Button 27", "L Axis 3").

**Evidence (confidence: lead-only).** `ControlCapture.cs:48-61, 210-224` limits the scan to the SDL
gamepad range; its rest-then-move rule (`:27-40`, `:226-247`) is reusable for stick axes;
`BindingLabels.cs:51` labels axes by `JoyAxis` names. Decisions 9, 12.

**Approach.** Give `ControlCapture` the seat's stick identities and scan them after pad buttons;
turn an axis capture on a pair row into a B4 full-axis binding. <TODO: whether Escape and pad B
remain the only cancel controls while a stick is capturing; label format for hats.>

**Model recommendation.** <TODO>

**Verify.** <TODO: capture suites with a fake stick: button 100, axis 6, hat up, full-axis
inference both directions, throttle-lever row.>

**⚠ Traps.** A resting stick drifts; keep the rest-then-move mask. A stick at rest in a HOSAS can
sit slightly off-centre; the capture's rest band must not latch it.

## D11 ☐ Remake Controls screen: stick rows saved to the active profile, "Open profiles folder"

**Goal.** Stick bindings appear in the existing rows of the remake Controls screen, save to the
active profile file on Accept (staged like every other edit), clearing a full-axis binding from
either row clears both, and an "Open profiles folder" entry opens `user://stick_profiles/`.

**Evidence (confidence: lead-only).** `ControlsFeature.cs:12-23` stages edits and writes on
Accept; the row already shows any number of bindings (`:29`, `LaunchMenu.cs:2937`). Decision 8, 9.

**Approach.** Split the Accept write so stick rows go to C7's store and the rest to `BindingStore`.
<TODO: where the folder entry sits in the screen; how the steal prompt treats a stick control.>

**Model recommendation.** <TODO>

**Verify.** <TODO>

**⚠ Traps.** An edit made while a flight is in progress reaches the live seats through the
`Accepted` event (`ControlsFeature.cs:47-51`); stick rows must follow the same route.

## D12 ☐ Original-style KEYS AND BUTTONS page: Stick column, replace per device

**Goal.** The original-style page shows a third **Stick** column beside Control A and B that lists
and captures only stick bindings from the active profile; a capture there replaces that stick
model's binding for the action and keeps the other stick's (for example "R Btn 1 / L Btn 1").

**Evidence (confidence: lead-only).** `OriginalOptionsScreen.cs:911-942` builds the A/B cell text;
`:2517-2519` shows capture on B replacing binding 2, so a stick capture there would overwrite the
shipped pad binding. Decisions 15, 15b.

**Approach.** Add a third cell per row with its own capture path that accepts stick input only.
<TODO: the column's position and width against the authored art; how the page's cursor steps
across three columns.>

**Model recommendation.** <TODO>

**Verify.** <TODO: a screenshot of the page for the user to judge; capture suites for replace per
device.>

**⚠ Traps.** The column goes beyond the authored two-column art; how it looks is the user's call,
shown as a screenshot and asked, never settled by an instrument.

# Wave E, shipped profiles

## E13 ☐ The user binds VKB solo-R and HOSAS in-game; commit the files as shipped profiles

**Goal.** Two shipped example profiles: VKB solo R (R flies pitch, roll, yaw on twist, throttle and
weapons alone) and VKB HOSAS (an R file naming L as a companion, and an L file naming R, with L's Y
axis bound as a ThrottleUp/Down rate at 0.08).

**Evidence (confidence: lead-only).** Decisions 7, 12, 14. The user's firmware numbering and
preferred layout are not known to this plan.

**Approach.** With everything above landed, the user binds both layouts in the controls screen
(L unplugged for solo), and the resulting `user://stick_profiles/` files are copied into the
shipped location and committed. This doubles as the end-to-end test of capture, companions and
saving.

**Model recommendation.** <TODO>

**Verify.** The user flies with both layouts and unplugs L mid-session to confirm the live switch.
<TODO: any automated check on the shipped files (they parse, their models match 231D/0200 and
231D/0201).>

**⚠ Traps.** The shipped files are the user's layout; do not edit their bindings by hand afterwards
without asking.
