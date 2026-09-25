# Bindings

What a binding is, how one resolves against hardware, and the named actions a polling site asks for. The registry that turns a device identity into a live pad and the map that holds the actions both sit on top of these types.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/Bindings/DeviceId.cs
Which device a binding sits on, as a value that survives a replug: `DeviceKind` plus, for a joypad,
the stable hardware string its platform reports. Keyboard and mouse are singletons because the
platform reports one state each whatever produced it. The default value names nothing, which is what
a binding read from a file with a missing device becomes. Turning an identity into a live joypad
index is `DeviceRegistry.cs`, read next.

## src/Bindings/BindingControl.cs
The tagged control half of a binding: `ControlKind` picks which of `Key`, `Button`, `Axis`, `Hat`,
`Mouse` and `FullAxis` the numeric members mean, and six static factories are the only way to build
one, because they hold the per-kind invariants. A key carries `KeyModifiers` as well, so `E` and
Shift+`E` are two controls. `HatDirection` is a flags enum so a device can report a diagonal while a
binding names one direction. `Deadzone` is both the noise gate and the digital threshold; a full
axis, a stick's whole axis over an action pair with `Sign` -1 meaning inverted, honours one up to
`MaxFullAxisDeadzone`. The original's four fixed typed slots, and why an axis cannot be bound there
at all: [../org/input.md](../org/input.md).

## src/Bindings/AxisPairs.cs
The four action pairs a full axis drives (pitch, roll, yaw, throttle) and which member of each is the
positive side: the end a pad's shipped half-axis puts on raw positive travel. `SideOf` is what the
map resolves a full axis with, `PositiveOf` is the one row a file writes it under, and `FullAxisFor`
turns a capture on either row into the binding with invert inferred from the direction moved.
`IsAbsolute` names the one full-axis row outside every pair, Throttle (lever), whose capture
direction is toward full throttle. One table for the map, the store and capture, so a stick cannot
resolve backwards on one path only.

## src/Bindings/LeverTakeover.cs
When a bound Throttle (lever) drives the commanded throttle setting. A lever holds its position
untouched, so it takes over only when it moves past `Epsilon` and hands back when a rate key, a digit
or a schedule commands while it is still. `Release` re-seeds it after the throttle is placed. The
flight model steps one per aircraft; the order against the other commands is in `FlightController`'s
`ReadKeyboard`.

## src/Bindings/Binding.cs
A device identity plus a control, and the whole read: `Resolve` reaches hardware only through
`IDeviceState`, so the model is exercised without an engine. A half axis reports raw travel; a full
axis reads the side of its pair the caller names, rescaled from the deadzone edge, or through
`ResolveAbsolute` its whole travel as a 0 to 1 lever position. `ControlValue` is the held/how-far
pair, with `Pressed` true exactly when `Value` is above zero. `ModifierGate` is the modifier rule: a
key wanting modifiers wants exactly those, and a bare key stands down under a modifier the same keymap
holds it under, which the gate reads once a tick and the map alone decides. Structural equality is
what a rebinding screen compares when it takes a control off its previous owner.

## src/Bindings/IDeviceState.cs
The tick's raw hardware state, addressed by device identity rather than connection index: keys,
buttons, mouse buttons, axis travel with no deadzone applied, and a hat's full direction flags. Every
member answers for an absent device instead of throwing, which is how an unplugged pad silences its
actions without the map losing the rows. Two implementations ship: `GodotDeviceState.cs` for a single
device identity through the registry, and `SeatDeviceState.cs` for a seat that reads a whole pad set.

## src/Bindings/GodotDeviceState.cs
The live `IDeviceState` over Godot's `Input` singleton, addressed by identity through an owned
`DeviceRegistry` rather than a raw index. `RefreshDevices` rebuilds that table from the connected
roster; it is meant to run at launch and on every `joy_connection_changed`, and the call site belongs
to whoever builds the per-tick resolver, since this type has no polling loop of its own. Godot
exposes no raw hat, so hat index 0 is the d-pad read back off its four buttons and every other hat
index reads nothing. A seat holding a set of pads reads `SeatDeviceState.cs` instead.

## src/Bindings/DeviceRegistry.cs
The live joypad index-to-identity table, rebuilt from a connected-pad list on `Refresh` rather than
trusting an index to stay put across a replug. A pad's stable id is its GUID, or its reported name
where the platform gives no GUID, and a blank one is skipped. Two connected pads reporting the same
id (identical hardware sharing one SDL identity) is a real collision: the first one seen claims it
and the second stays unresolved, rather than the pair silently driving one binding together.
`IdentityOf` runs the other way, which is how a capture turns "index 2 pressed" into the id worth
saving. Pure and engine-free; reading the tuples off Godot is `GodotDeviceState.cs`.

## src/Bindings/SeatDeviceState.cs
One seat's hardware behind the binding seam, which is what every polling site actually holds. A seat
reads a SET of pads while a binding names one device, so pad defaults sit on a placeholder identity
and this answers for it: buttons ORed, axes taken at the largest magnitude across the set. Pad reads
go through `Pads.For` rather than a registry index, and only the pad half is gated, because Godot
polls joypads regardless of window focus while key state is focus-scoped. `Refresh` takes the pad
list once a tick. Every other joypad identity goes to an optional stick reader
(`Sticks/StickDeviceState.cs`, seat 1's only), muted with the pads. Read `PlayerActions.cs` above it.

## src/Bindings/BindingSet.cs
The bindings one action holds. `Resolve` ORs them, which is the original's four-slots rule
(`FUN_00537530`, [../org/input.md](../org/input.md)) without its four fixed slots, and takes the
deepest deflection any of them reports so a half-pressed trigger cannot beat a held button on the
same action. `Add` drops a duplicate rather than rejecting it, `Remove` is the losing half of the
steal rule, and `Clone` gives an editing screen something it can throw away. It owns no action name
and no device lookup; `ActionMap.cs` owns the first and `DeviceRegistry.cs` the second.

## src/Bindings/InputAction.cs
The named actions, one member per binding a polling site holds, so migrating a site is a lookup swap
rather than a rename. The members are contiguous from zero because `ActionSnapshot` indexes arrays by
them, and `ThrottleSet0` to `ThrottleSet8`, the original's nine absolute throttle settings, are
contiguous and in order because their readers do the arithmetic. Debug and lab keys are deliberately
outside the enum: they are development instruments, and a
rebinding screen that offered them would let a player break their own diagnostics. Which context owns
each member is `DefaultBindings.ContextOf`.

## src/Bindings/ActionMap.cs
One player's keymap, an action to a `BindingSet`. `Assign` is the winning half of the steal rule and
returns every action that lost the control, in enum order, so a screen can name each loss; `OwnersOf`
asks without committing. `Add` binds without stealing, for the shipped defaults and a loaded file
where a control is deliberately on two actions; `Shares` exempts `SkipCutscene` from stealing either
way. `SameControl` is "the same control", modifiers included, a full axis matching either half of
its axis. A full axis sits on both rows of its pair or neither, and `Unassign` or `Clear` on either
row clears both; the lever row holds its full axis alone. `ResolveInto` reads every action once per
tick, each full axis on its action's side or as the lever's position, via `ContestedFor`.

## src/Bindings/ControlCapture.cs
What a rebinding screen may capture, and the scan that turns a press into a `Binding`: the bindable
key list, Godot's pad button and SDL axis ranges, and the mouse buttons past the pointer's own. `Arm`
masks everything already held. Every pad control is stamped with the seat's own identity, not a
hardware GUID. A key pressed under Shift, Ctrl or Alt carries them; a modifier bound alone resolves
on its release. A pad axis is rest-then-move, since a resting stick drifts, with a fixed
`CapturedDeadzone`. A pad's hat is not scanned; sticks are, through `StickCapture`, steered by the
constructor's `row` and `sticksOnly`. Escape and the pad's Back cancel and are never captured.

## src/Bindings/StickCapture.cs
The stick half of a capture, over every identity the reader lists (`IStickDevices`): buttons 0..127,
hats 0..3 per direction (release-first), and axes 0..7 measured from the value each read when the
capture armed, since a lever rests anywhere. The axis moved furthest past `MoveThreshold` wins. On a
row where `AxisPairs.TakesFullAxis` holds it becomes `AxisPairs.FullAxisFor`, invert inferred, with
`DeadzoneFor(row)` (0.08 on the throttle rate pair, 0.02 elsewhere); on any other row a half axis,
captured only once it sits past its own deadzone. Nothing is scanned while `ReadsBlocked` holds, and
the rests and masks are taken on the first unblocked poll, never from blocked zeros.

## src/Bindings/IStickDevices.cs
The seam a capture learns the seat's stick identities through, so `Bindings` never names the stick
library: `Sticks/StickDeviceState.cs` implements it, and `SeatDeviceState` passes its stick reader's
list through (empty with no stick reader or on a keyboard-half reader).

## src/Bindings/ICaptureDevices.cs
The hardware a rebinding screen captures through: one `IDeviceState` per `InputContext`, together
with the pad identity that context's bindings are authored on. The identity and the reader are one
interface because a seat's reader answers for exactly one identity and reads nothing for any other,
so a call site that paired a reader with the wrong identity would capture no pad control at all while
the keyboard kept working. `SeatCaptureDevices.cs` is the shipped implementation.

## src/Bindings/SeatCaptureDevices.cs
One seat's capture readers: a `SeatDeviceState` per `InputContext` over the same pad list, each built
on the identity that context's bindings are authored on, from the single `padOf` the seat passes in
so the two cannot disagree. The three polling sites do not share one placeholder, which is why a
reader answers for one context and reads nothing for the other two. `For` takes this frame's pad list
before handing the reader back, because a capture reads between the poller's own ticks. An optional
stick reader goes to every context; `UI/Screens/MenuControlsSeats.cs` passes each seat
`StickDeviceState.Live`, which reads only for seat 1. Read `ControlCapture.cs` next.

## src/Bindings/BindingLabels.cs
What a rebinding screen prints: an action's name, a control's name in keycap terms rather than enum
terms, one row of an action's whole binding list, and the clause naming what a steal took a control
from. An action the original binds prints the original's own keybind-page caption, unprefixed and in
its own case, since those pages read under a category heading; a modified key prints `Shift+E`. A
row states how many bindings it is not showing, because the original draws only the first two of
four slots (`FUN_00449fc0`, [../org/input.md](../org/input.md)) and hides the rest silently. A stick
control prints its `StickName` prefix and its index counted from 1, as VKB's tool and Windows
count, so `button:#17` reads "R Button 18". Separate from `BindingStore`'s tokens.

## src/Bindings/ActionSnapshot.cs
The tick's resolved held/how-far pair per action, so two consumers asking the same question in one
tick cannot disagree. It carries no went-down and no went-up: edge detection stays in the consumer
that already owns its previous-frame slot. The instance is reused every tick. `Axis` reads zero with
both ends held, which is what the flight and camera pairs do; a menu cursor axis gives the negative
end priority instead, so `MenuInput.Dir` reads those rather than this.

## src/Bindings/PlayerActions.cs
What a polling site holds: a player's `ActionMap`, the tick's snapshot, and `Poll` as the one place
hardware is read. `ReadsKeyboard` is the player-1-only keyboard rule, kept on the seat rather than in
the map, so a pad-only splitscreen player keeps the shipped keyboard defaults in their map and simply
reads none of them. Several of these over one `ActionMap` is the supported shape, for an action whose
keyboard and pad halves take different processing and are then summed rather than ORed. Read
`BindingProfile.cs` for the whole seat above this.

## src/Bindings/InputContext.cs
Which controls a seat is reading: flight, menu, or the free camera. A seat holds one `ActionMap` per
context rather than one map overall, because the shipped keymap gives one control several meanings
(`W` pitches down in flight, moves a menu cursor up, and flies the spectator camera forward) while a
map holds a control once. The steal rule therefore runs inside a context, which is also the scope a
rebinding screen edits: a conflict is two flight actions wanting one button, not flight and the menu
sharing it.

## src/Bindings/DefaultBindings.cs
The shipped keymap as data, one `ActionMap` per context, transcribed from
[../controls.md](../controls.md); flight is the original's own shipped table, key for key, with this
port's own actions on keys it leaves free. Every action is either bound here or on the `Unbound`
list, which stops a migrating site meeting a hole one call at a time, and `ContextOf` with
`ActionsIn` is the action-to-context census a store and a screen both walk. A pad default names the
placeholder identity `AnyPad`, which no hardware reports; `MapFor` and `Retarget` put the seat's own
pad in its place, so a seat with no pad keeps the rows and resolves them to nothing. The set is built
through `ActionMap.Add` rather than `Assign`. Read `BindingStore.cs` next.

## src/Bindings/BindingProfile.cs
One seat's whole input: an `ActionMap` and a `PlayerActions` per context, and the keyboard gate that
applies to all of them at once. This is what a polling site is handed and what a rebinding screen
edits. `Poll` resolves every context on the tick rather than only the mode in front of the player,
because a pause board and the aeroplane behind it are both live on one tick. It also holds the
seat's `ActiveDevice`, fed the tick's two halves by whoever polls them, so every prompt on the seat
names one device, and the seat's flying scheme (`MouseFlying`) and its `MouseSensitivity`, which
two players at one machine choose separately. Where a seat's profile comes from is
`LaunchBindings.cs`.

## src/Bindings/SensitivityScale.cs
The Fly scheme's mouse sensitivity as one set of numbers: a multiplier from 0.25 to 4 with 1 as the
default, and the 0 to 100 slider scale both presentations step it on, even in ratio so that a
`LevelStep` of 5 multiplies it by the same amount anywhere. `Clamp` reads a non-finite value as the
default, which is what keeps a hand-edited keymap file from stopping the stick. `MouseCapture`
divides its full-deflection travel by the value. The arithmetic and the range's reasoning:
[../controls.md](../controls.md), "Flying with the mouse".

## src/Bindings/ActiveDevice.cs
Which side of a seat's hardware produced its last real input, keyboard and mouse against the pads,
and which of an action's bindings a prompt on that side names. One side at a time: a press hands the
line over, a held control does not, and a stick short of `PressTravel` is drift, because the flight
axes carry no deadzone of their own. A prompt takes the active side's binding and falls back to the
other side's where that half is unbound, which is how an unbound pad action still reads as a key. A
seat owns one through `BindingProfile.cs`, read next; `BindingLabels.cs` turns the chosen binding
into the words.

## src/Bindings/BindingStore.cs
The keymap file: versioned JSON, one per player under `user://`, written atomically through a temp
file and a rename. Named and versioned against the original, which writes 2400 unversioned raw bytes
to the registry and points its live array at the loaded buffer, so a record-layout change there
reinterprets an old save. `Encode` and `Decode` are the token grammar (version 3 adds full-axis and
stick hat tokens, the lever row reusing the full-axis token); an older file loads whole, so the
reader checks no version. `StoredRow` writes a full axis once, under its pair's positive row. A named control leaves any default still holding it.
Shape, tokens and unreadable rows: [../org/input.md](../org/input.md). `DirectoryOverride` keeps a
suite off this machine's own keymap.

## src/Bindings/LaunchBindings.cs
Where a seat's keymap comes from when the seat is built: the player's saved file, or the shipped
defaults. `FlightController`, `SpectatorCamera` and `MenuInput` each ask this instead of building
`BindingProfile.Defaults` for themselves, so there is one place the read is gated and one place to
look when a rebind is not felt. `Configure` resolves that gate once at launch and it is shut until
called. A seat is put on the loaded map through `ActionMap.Fill`. Player 1's profile is completed
from the stick profiles through `StickRows` (`src/Bindings/IStickRows.cs`). Coverage:
`CSVM.Tests/LaunchBindingsTests.cs` and the `bindings-launch-load` engine suite.

## src/Bindings/IStickRows.cs
The seam seat 1's keymap is completed through: stick rows live in per-model profile files, not in
`bindings_p1.json`, so `LaunchBindings.Profile` hands player 1's loaded profile to the registered
`IStickRows`, which replaces its stick bindings with the active profiles' rows. The stick side
implements it (`src/Sticks/StickProfileSet.cs`), which keeps `Bindings` free of the stick library.

## src/Bindings/PadRumble.cs
One seat's rumble, routed through `Pads.For` to the pads that seat's own bindings read, so a
splitscreen pane never buzzes another pilot's controller, and sent only while the seat's own
`ActiveDevice` (the reading its control prompts take) says the last input came off the pad.
`RumbleEvent` is the original's event list; a fixed table gives each row a weak magnitude, a strong
one and a length, and the static band functions (`GunFire`, `Launch`, `CannonHit`, `OrdnanceHit`,
`Contact`) hold the original's edges. `Overspeed` is the one sustained cue, restarted on a cadence.
`IRumbleSink` is the unit tests' seam over `Input.StartJoyVibration`; the static `Enabled` is the Game
Options toggle, held off under `--det`. Every number and why the bearing is dropped: [../org/input.md](../org/input.md).
