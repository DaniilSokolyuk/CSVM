# Sticks

`CSVM/src/Sticks/`, the flight sticks Godot's own SDL3 does not enumerate, read through the pinned
`SDL2.dll` (`docs/tooling.md`, "SDL2 for flight sticks") beside Godot's pads, never instead of them.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Sticks/StickModel.cs
A stick's identity as a value: USB vendor and product id, printed `231D/0201`. Bindings and
profiles key on it rather than on a unit, so identical units of one model are one device. Parses
its printed form and Godot's decimal `vendor_id`/`product_id` strings, which is how the gap-filler
compares the two rosters. `Device` is its binding identity, the joypad id `stick:231D/0201` (stored
as `pad:stick:231D/0201/<control>`, split at the last slash); `TryFromDevice` reads it back.

## src/Sticks/Stick.cs
The two shapes a device takes: `StickListing` (listed, unopened: instance id, name, model, GUID),
which is all the gap-filler needs to decide, and `Stick` (opened, with its axis, button and hat
counts), which is what the roster holds and every read is addressed by.

## src/Sticks/IStickNative.cs
The stick library as the roster sees it: pump, list, open, close and raw reads by SDL instance id.
The seam that keeps `StickRoster` engine-free; `Sdl2Sticks` is the live implementation and
`CSVM.Tests/FakeStickNative.cs` is the fake.

## src/Sticks/Sdl2Sticks.cs
`SDL2.dll` behind `IStickNative`: the load-order candidates (pure), the absolute-path load, the
hints that reduce SDL2 to its DirectInput backend so it cannot disturb the SDL3 inside Godot, and
the per-frame `SDL_JoystickUpdate` plus event drain that reports hot-plug. Exports are bound by
name from the loaded handle, so a wrong DLL fails as one log line, not as a crash.

## src/Sticks/StickRoster.cs
The gap-filling roster: every listed device whose model Godot's pad roster lacks, opened, kept
current across plugs and across changes in Godot's roster, and logged on every change. Reads
(axes -1..1, buttons up to 128, hats as `Bindings.HatDirection`) answer neutral while the gate
holds, the same `Pads.InputBlocked` pads obey. `ModelAxis`/`ModelButton`/`ModelHat` merge the
units of one model. `RestingAxes` is each stick's axes sampled ungated `SettleUpdates` updates
after it opened, which the shape test reads. Engine-free; read `Pads.cs` for the roster-versus-gate
split it follows.

## src/Sticks/StickDeviceState.cs
The sticks behind the binding seam: an `IDeviceState` answering for `StickModel.Device` identities
through the roster's `Model*` reads, so L and R are two devices and identical units one. Only
player index 0 (seat 1) reads; any other seat, and a null roster, read nothing. It adds no gate of
its own, the roster's is `Pads.InputBlocked`. `Devices()` lists the connected models' identities,
which is `Bindings/IStickDevices.cs`, the list a capture scans.
`Live` reads `StickPump.Roster`; seat 1's `Bindings/SeatDeviceState.cs` in `FlightController`
holds one. Tests build it over a `StickRoster` on `CSVM.Tests/FakeStickNative.cs`.

## src/Sticks/StickPump.cs
The engine side: `Start` loads SDL2 once per process from `Launcher` (never under `--no-pads`, so
never in a test or golden), publishes the one live roster as `StickPump.Roster`, and pumps it every
frame at priority -1001, ahead of the session node, focused or not. `Dump` is `--dump-sticks`,
and `--dump-sticks=<seconds>` adds a watch that logs every control that moves. It starts
`StickProfiles` with the roster and refreshes the live set whenever the roster changes or a stick
settles.

## src/Sticks/StickProfile.cs
One stick model's bindings in one layout, the content of one profile file: the model, the
companion models it needs connected (kept distinct, in model order, never its own model), the short
display name, the ignore flag, and an `ActionMap` per context whose every binding is on
`StickModel.Device`. Rows the file held that this build could not read ride in `Unread` and go
back out verbatim. `StickProfileFile` pairs a profile with its source (shipped or user) and file
name. The format is `docs/org/input.md`, "The CSVM stick profile files".

## src/Sticks/StickProfileStore.cs
The files on disk. Shipped profiles arrive as texts (a pck is not a directory), user profiles are
read from and saved to one directory, and a save is atomic and always a user file: a shipped
profile saved becomes a user copy under `FileNameFor` (`231D-0200+231D-0201.json`). Rows reuse
`Bindings/BindingStore.cs`'s tokens, written bare and by number; a full keymap token naming the
file's model in any case also reads. An unusable model or companion refuses the whole file, with
one log line.

## src/Sticks/StickProfileResolver.cs
Pure selection: connected models plus files give the active file per model. A file applies when its
model and every companion are connected; more companions win, then user over shipped, then the
ordinal file name. Also the rows step: `Rows` (one context's stick-only map), `MergeInto` (a
keymap's stick bindings replaced by the active rows, ignored profiles adding none), `ReplaceRows`
(named models' rows only, for a reset) and `WithoutStickRows` (the copy the keymap file is saved
from).

## src/Sticks/StickProfileSet.cs
The profiles in force: the loaded files, the connected models, the resolver's choice, and
`Revision`/`Changed` when that choice moves. It is `Bindings/IStickRows.cs` for seat 1's keymap,
`Map` for a stick-only action source, `Save` for one profile (copy-on-write), and `SaveFrom` for an
accepted controls screen (each changed model's rows to the profile the keymap was staged under, or
a new user profile). `ResetDefaults` and `ResetInto` are a screen reset's stick rows, from shipped
files alone. The generic default joins the choice for the one model it claims, and a file that
wins on its name alone logs a warning naming both. `MergeIfChanged` follows the choice into a
flying seat's keymap or a menu seat's map. Engine-free; tests run it over `FakeStickNative`.

## src/Sticks/StickShape.cs
Whether a device looks like a flight stick: at least three axes, with axes 0 and 1 resting near
centre in the roster's rest sample (`StickRoster.RestingAxes`). Other axes may rest anywhere, since
a throttle lever parks where it was left. Unsettled until the sample exists. `Of` judges a model by
its units in the roster. Read `GenericStickDefault.cs` for the one question it answers.

## src/Sticks/GenericStickDefault.cs
The in-memory profile the one connected, stick-shaped, unprofiled model gets: X roll, Y pitch, Rz
yaw from six axes up, Z as the absolute Throttle (lever), buttons 0 and 1 for guns and rockets, and
in menus the hat, button 0 to confirm and button 2 to back out, button 0 also skipping a cutscene
(`SkipButton`). `Pick` is the exactly-one rule, `For`
the rows. A changed screen save turns it into a user file. The layout and rule are in
`docs/org/input.md`, "The generic stick default".

## src/Sticks/StickSkip.cs
Seat 1's `SkipCutscene` off the active profile's Menu rows alone: `Pressed` (the edge) and `Held`,
one `Poll` per frame. `Prime` swallows a trigger already down, so the press that opened a screen
cannot skip it. Reads nothing for any other seat or with sticks off. Polled by
`UI/Screens/CinemaScreen.cs`, `UI/Screens/BootCard.cs` and `Session/Launch/GameSession.cs` (into
`CutsceneController.TakeStickPress`). Rules in `docs/org/input.md`, "Skip Cutscene, the stick's
skip"; covered by `CSVM.Tests/StickSkipTests.cs`.

## src/Sticks/StickProfiles.cs
The engine side of the profiles: the shipped folder `res://data/stick_profiles/` (read through
Godot's file API, exported by the preset's `data/*.json` filter), the user folder
`user://stick_profiles/` (with a suite override), and the one live `StickProfileSet`, which
`StickPump` starts and which registers itself as `LaunchBindings.StickRows`.

## src/Sticks/StickScreens.cs
What the rebinding screens take from the stick side, injected by `Launcher` into the one
`UI/Menu/ControlsFeature.cs`: `Save`, the accepted keymap split so player 1's stick rows go to
`StickProfileSet.SaveFrom` and the keymap file is written from `WithoutStickRows` (other players
unchanged), and `OpenUserFolder`, which creates the user profile folder and opens it through
`OS.ShellOpen`. Covered by `CSVM.Tests/ControlsStickTests.cs`.

## src/Sticks/StickLabels.cs
How a rebinding screen names a stick: its active profile's short name ("R"), else `Stick` and its
model, so two unnamed sticks never read as one. `StickPump.Start` registers it as
`BindingLabels.StickName` before any other check, so a stick row reads as a stick even with sticks
off. `Column` is the KEYS AND BUTTONS Stick column's shorter caption, which drops the `Stick`
prefix and model of an unnamed stick and prints the control alone, since the column is half a
panel wide. `Prefix` and `Column`'s two-argument form are the pure forms a test drives with its
own names.
