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
units of one model. Engine-free; read `Pads.cs` for the roster-versus-gate split it follows.

## src/Sticks/StickDeviceState.cs
The sticks behind the binding seam: an `IDeviceState` answering for `StickModel.Device` identities
through the roster's `Model*` reads, so L and R are two devices and identical units one. Only
player index 0 (seat 1) reads; any other seat, and a null roster, read nothing. It adds no gate of
its own, the roster's is `Pads.InputBlocked`. `Devices()` lists the connected models' identities.
`Live` reads `StickPump.Roster`; seat 1's `Bindings/SeatDeviceState.cs` in `FlightController`
holds one. Tests build it over a `StickRoster` on `CSVM.Tests/FakeStickNative.cs`.

## src/Sticks/StickPump.cs
The engine side: `Start` loads SDL2 once per process from `Launcher` (never under `--no-pads`, so
never in a test or golden), publishes the one live roster as `StickPump.Roster`, and pumps it every
frame at priority -1001, ahead of the session node, focused or not. `Dump` is `--dump-sticks`.
