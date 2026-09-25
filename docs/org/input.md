# The command map: keybindings, devices and dispatch, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-09-02. Every claim below names the function or
address it came from.

Everything here is a description of *behaviour*. No decompiler output is reproduced; the addresses
are given so any claim can be re-checked at source.

⚠ **This page is a decode, not a proposal.** The original's binding model is narrower than anything
CSVM can ship (one joystick, ten buttons, no axes). Where this page describes a limit, it is
recording what the original does, not setting a bar for the port. See "What this means for CSVM".

**Where the neighbours live.** The eleven targeting actions this page lists by id are decoded as
*behaviour* in [`targeting.md`](targeting.md). The camera actions (`0x30`-`0x39`) are
[`cameraViews.md`](cameraViews.md). The bindings CSVM ships today are `../controls.md`, whose
flight table is the shipped defaults below, key for key, with this port's own actions on the keys
the original leaves free.

## The headline

There is **no default keymap table in the data**. The 64 shipped defaults are emitted by code, one
call each, and the live map is a flat array of **one 32-bit word per command id**, indexed by that
id. Each word packs four binding slots: two keyboard codes, one joystick button, one mouse button.
Any of the four fires the action.

Three findings will not be guessed from the keybind screens:

- **The UI's two columns are not two fields.** The record has four slots, and the row renderer packs
  the first two non-empty of them into the two visible columns. A row showing "E" and "Joystick 3"
  is slot A and the joystick field, with slot B empty.
- **A joystick binding is a bare button index with no device.** There is one joystick pointer in the
  whole program, and axes and hats cannot be bound at all.
- **The keymap persists to the registry, byte for byte.** The saved blob is the in-memory array,
  2400 bytes, not a serialized or textual form.

## Function map

| Address | Role |
|---|---|
| `FUN_004936c0` | **The defaults.** 64 calls, one per shipped binding, `0x004936c0`-`0x00493cf5` |
| `FUN_00493630` | The per-default shim: resolves the message id, applies the Japanese remap, forwards six arguments |
| `FUN_00493600` | The Japanese-keyboard code remap |
| `FUN_005384a0` | The keyboard-type probe. Returns 99 unless `GetKeyboardType(0) == 7` |
| `FUN_00537360` | **Define command.** The sole entry point that writes a command's word |
| `FUN_00537090` | The word encoder (the bit layout below) |
| `FUN_005370f0` / `FUN_00537110` | Read keyboard slot A / slot B |
| `FUN_00537130` / `FUN_00537150` | Read the joystick button field / the mouse button field |
| `FUN_005371d0` / `FUN_00537230` | Reassign a code, clearing it out of its previous owner's word |
| `FUN_00536f50` | Allocates the map. `FUN_005bde90("CmdMap", 7, count * 4, 1)` |
| `FUN_00537890` | Creates the manager. Called once from `0x004a7340` with `count = 0x258` |
| `FUN_00536e80` | Rebuilds the four reverse-lookup arrays whenever the map changes |
| `FUN_00537530` | **The per-frame test.** ORs the state of all four slots |
| `FUN_005357b0` | The buffered keyboard read. Composes the control code and dispatches |
| `FUN_00535a00` | The control-code range gate, `0..0x7dd` |
| `FUN_00535fb0` | Handler registration. Refuses an already-claimed code |
| `FUN_00536b50` | `GetDeviceState` into a `DIJOYSTATE2` |
| `FUN_00536c40` | Reads one joystick button, gated `1 <= n < 0xb` |
| `FUN_00536cb0` | Clears joystick buttons `1..0xa` |
| `FUN_00537910` | Fills the joystick button name table for indices 1-10 |
| `FUN_00449fc0` | The keybind screen's row renderer |
| `FUN_005375f0` | The keyboard control-code name formatter |
| `FUN_00537830` / `FUN_005379b0` | The joystick / mouse button name formatters |
| `FUN_005bdad1` / `FUN_005bdc89` | Registry load / save |
| `FUN_005bd900` | Builds the registry key path |
| `FUN_0043fb50` | Startup order: register, defaults, registry load, rebuild |

## The record

The live map is an array at `inputMgr + 8`, allocated by `FUN_00536f50` as 600 dwords (`0x258`
commands, 2400 bytes). The manager object is `DAT_0075caf4` (read at `0x00537bfa`), size `0x3f5c`,
created by `FUN_00537890` from `0x004a7340`. Parallel arrays in the same object hold the handler
pointers (`+0x10`), the action name strings (`+0x14`, `char[0x50]` each) and the count (`+0x04`).

One command is one 32-bit word. From the encoder `FUN_00537090` and its four accessors:

| Bits | Field | Accessor |
|---|---|---|
| 0-10 | Keyboard binding A, an 11-bit control code | `FUN_005370f0` |
| 11-21 | Keyboard binding B, same encoding | `FUN_00537110` |
| 22-25 | Joystick button, 1-10, 0 meaning unbound | `FUN_00537130` |
| 26-27 | Mouse button, 1-3, 0 meaning unbound | `FUN_00537150` |
| 28-31 | Unused | |

### The keyboard control code

A control code is a **raw DirectInput DIK scancode** in bits 0-7, OR'd with modifier flags
`0x100` Alt, `0x200` Ctrl, `0x400` Shift (`FUN_005357b0`, whose source path is
`D:\zipper\gamez\zinput\zin_kbd.c`; the name formatter is `FUN_005375f0`). It is not a virtual-key
code, so a binding is a physical key position and prints differently under a different layout.

Left and right modifier variants collapse onto the same flag: `0x1d`/`0x9d` Ctrl, `0x2a`/`0x36`
Shift, `0x38`/`0xb8` Alt.

Worked examples from the shipped defaults: `E` is `0x012`, `Shift+E` is `0x412`, `Ctrl+E` is
`0x212`, `Ctrl+X` is `0x22d`, `Shift+L` is `0x426`.

The code space is 2014 values (`0..0x7dd`), gated at `FUN_00535a00` and matching the 2014-entry
tables cleared by `FUN_00536e80` and `FUN_00535fe0`.

### The joystick and mouse fields

A joystick binding is **a button index alone**. `FUN_00536c40` reads the current and previous
`rgbButtons[n-1]` of a single `DIJOYSTATE2` at `DAT_0075c1e4` (0x110 bytes, previous frame's copy at
`DAT_0075c2f4`, filled by `GetDeviceState` in `FUN_00536b50`). The index is gated `1 <= n < 0xb`, so
ten buttons are bindable; `FUN_00536cb0` clears exactly `1..0xa` and `FUN_00537910` fills the name
table over the same range as localized `MSG_JBTN_%d` with a `"Button %d"` fallback.

A mouse binding is a third encoding: the 2-bit field indexes `DAT_0075ccc0 + 0x30/+0x34/+0x38`
(`FUN_00537530`), named Left, Right and Middle by `FUN_005379b0`.

## What the keybind screen shows

`FUN_00449fc0` emits up to four strings for a row, in slot order: keyboard A (`FUN_00537c30` into
`FUN_005375f0`), keyboard B (the same formatter), joystick (`FUN_00537c50` into `FUN_00537830`),
mouse (`FUN_00537c70` into `FUN_005379b0`). Empty slots are skipped and the survivors pack into the
visible columns. So the screen's "Control A" and "Control B" headings name positions in the row, not
fields in the record, and a row reading "E / Joystick 3" has slot B empty.

## The shipped defaults

64 rows, from `FUN_004936c0`. `FUN_00493630` also stores each command's message id into the global
array at `0x0071c5b8` (write at `0x00493697`). Names are resolved by message id through
`language.dll`; the English text is not in `crimson.exe` and is quoted here from the extracted
message table. Control codes are the raw stored values.

| Page | Cmd | Action | Keyboard A | Keyboard B | Joy | Mouse |
|---|---|---|---|---|---|---|
| Movement | `0x01` | Point Nose Down | `0x0c8` Up | | | |
| | `0x02` | Point Nose Up | `0x0d0` Down | | | |
| | `0x03` | Roll Left | `0x0cb` Left | | | |
| | `0x04` | Roll Right | `0x0cd` Right | | | |
| | `0x05` | Turn Left | `0x033` `,` | `0x052` Numpad0 | | |
| | `0x06` | Turn Right | `0x034` `.` | `0x053` NumpadDecimal | | |
| | `0x2f` | Level Off | `0x426` Shift+L | | | |
| Throttle | `0x07` | Throttle Up | `0x00d` DIK_EQUALS | | | |
| | `0x08` | Throttle Down | `0x00c` DIK_MINUS | | | |
| | `0x09`-`0x11` | Throttle 0/8 through 8/8 | `0x002`-`0x00a` (`1`-`9`) | | | |
| Weapons | `0x13` | Fire Guns | `0x039` Space | | 1 | |
| | `0x14` | Fire Rockets | `0x02d` X | | 2 | |
| | `0x19` | Cycle guns clockwise | `0x03d` F3 | | | |
| | `0x1a` | Cycle guns counterclockwise | `0x03e` F4 | | 7 | |
| | `0x20` | Cycle rockets clockwise | `0x03f` F5 | | | |
| | `0x21` | Cycle rockets counterclockwise | `0x040` F6 | | 8 | |
| Targeting | `0x24` | Next Enemy/Objective | `0x012` E | | 3 | |
| | `0x25` | Previous Enemy/Objective | `0x412` Shift+E | | | |
| | `0x26` | Nearest Enemy/Objective | `0x212` Ctrl+E | | | |
| | `0x27` | Next Ally | `0x011` W | | | |
| | `0x28` | Previous Ally | `0x411` Shift+W | | | |
| | `0x29` | Nearest Ally | `0x211` Ctrl+W | | | |
| | `0x2a` | Next Non-Aircraft | `0x013` R | | 6 | |
| | `0x2b` | Previous Non-Aircraft | `0x413` Shift+R | | | |
| | `0x2c` | Nearest Non-Aircraft | `0x213` Ctrl+R | | | |
| | `0x2d` | Select Target Nearest Crosshairs | `0x010` Q | | | |
| | `0x2e` | Target Nothing | `0x014` T | | | |
| Views 1 | `0x30` | Toggle Spyglass | `0x41f` Shift+S | | 5 | |
| | `0x31` | Cycle Cockpit Views | `0x042` F8 | | 4 | |
| | `0x32` | External Camera Down | `0x043` F9 | | | |
| | `0x33` | External Camera Back | `0x044` F10 | | | |
| | `0x34` | External Camera Left | `0x057` F11 | | | |
| | `0x35` | External Camera Right | `0x058` F12 | | | |
| | `0x36` | Access Chase View | `0x041` F7 | | | |
| | `0x37` | Access Snap Look Mode | `0x025` K | | | |
| | `0x38` | Track Target | `0x026` L | | | |
| | `0x39` | Access Smooth Look Mode | `0x024` J | | | |
| Views 2 | `0x3a`-`0x42` | Look Up/Left, Left, Rear, through Look Up/Right | Numpad 1-9 (`0x04f`, `0x050`, `0x051`, `0x04b`, `0x04c`, `0x04d`, `0x047`, `0x048`, `0x049`) | | | |
| | `0x43` | External Camera Zoom In | `0x04e` NumpadPlus | | | |
| | `0x44` | External Camera Zoom Out | `0x04a` NumpadMinus | | | |
| Other | `0x12` | Use Nitro-Booster | `0x031` N | | | |
| | `0x22` | Bail Out | `0x22d` Ctrl+X | | | |
| | `0x23` | Display Scores (Multiplayer Only) | `0x00f` Tab | | | |
| | `0x45` | Pause/Quit/Objectives | `0x001` Esc | | | |
| | `0x6a` | Auto-Dock | `0x01e` A | | | |
| | `0x6d` | Chat to Everyone | `0x029` DIK_GRAVE | | | |
| | `0x6e` | Chat to Team | `0x429` Shift+GRAVE | | | |
| | `0x6f` | View Help | `0x03b` F1 | | | |

⚠ **The clockwise and counterclockwise names are inverted against the message keys.** "Cycle guns
clockwise" is `MSG_CMD_CANNON_PREV` and "counterclockwise" is `MSG_CMD_CANNON_NEXT`. Take the
displayed name, not the key, when reading a direction off this table.

⚠ **The joystick column carries only the counterclockwise half of each weapon cycle**, button 7 for
the guns and button 8 for the rockets. A pad player of the original steps one way and reaches the
other by going round, which is why a second pad control was never a given here.

⚠ **Three codes differ on Japanese hardware.** `FUN_00493630` remaps A and B through
`FUN_00493600` when `FUN_005384a0` returns 1-98, which happens only when `GetKeyboardType(0) == 7`.
On that path `0x0d` becomes `0x90`, `0x29` becomes `0x7d` and `0x429` becomes `0x47d`. Every other
keyboard, German included, gets the literals above.

## Dispatch

Not a scan. The buffered DirectInput keyboard read in `FUN_005357b0` composes
`code = DIK | modifiers` and writes the press or release state straight into
`(&DAT_007582e4)[code * 2]`, with the registered handler for that code at `(&DAT_007582e8)[code * 2]`.
Lookup is a direct index by control code.

`FUN_00536e80` maintains the inverse direction as four arrays inside the manager, one per slot
(`+0x18` keyboard A, `+0x1f90` keyboard B, `+0x3f08` joystick, `+0x3f48` mouse), each mapping a code
to a command id and rebuilt whenever the map changes.

Two consequences:

- **An action holds up to four bindings and any of them fires it.** `FUN_00537530` ORs all four
  slots.
- **Two actions cannot share a code.** `FUN_005371d0` and `FUN_00537230` clear a code out of its
  previous owner's word when it is reassigned, and `FUN_00535fb0` refuses a second handler
  registration for a claimed code, returning `0xffffffff`. The reverse arrays are single-valued, so
  the last write wins.

## Persistence

The map is saved to the **registry**, not to a file. `FUN_005bde90` registers it as name `"CmdMap"`,
type 7, size 2400, scope flag 1. `FUN_005bdc89` saves and `FUN_005bdad1` loads; flag 1 selects
`HKEY_CURRENT_USER` and flag 0 `HKEY_LOCAL_MACHINE`. `FUN_005bd900` builds the path from
`"SOFTWARE\%s\%s\%s"` (literal at `0x0061f0d0`), giving
`SOFTWARE\Microsoft\Microsoft Games\Crimson Skies\1.0`.

Type 7 writes a `REG_BINARY` of the raw buffer, and `FUN_00536f50` points `inputMgr + 8` at that
same buffer, so **the persisted form is the in-memory array**: 2400 bytes, 600 little-endian dwords
in the layout above, indexed by command id. There is no versioning, no key names and no textual
form.

Startup order is `FUN_0043fb50`: register the variables, run `FUN_004936c0` to lay down the
defaults, load from the registry over the top, then rebuild the reverse arrays.

## What this means for CSVM

| | Original | CSVM today |
|---|---|---|
| Binding identity | a physical scancode plus three modifier bits, or a bare button index | a device identity plus a tagged control (`Binding`); a key control carries the same three modifier bits, so `E` and Shift+`E` are two controls |
| Slots per action | four, fixed by type: keyboard, keyboard, joystick button, mouse button | a list of any length, ORed together (`BindingSet`) |
| Joystick devices | exactly one, `DAT_0075c1e0`, with no index in the record | any number, named by stable hardware string and resolved to a live index per tick |
| Bindable joystick controls | buttons 1-10 only. Axes and hats are read outside the map and cannot be bound | every button the platform reports, either half of any axis, a whole axis over an action pair, and a stick's hat directions; no pad hat, since a pad's d-pad arrives as buttons |
| Rebinding | a keybind screen writing into the same word the defaults wrote | a screen editing an `ActionMap`, the steal rule naming every action that loses the control |
| Persistence | 2400 raw bytes under `HKEY_CURRENT_USER` | versioned JSON per player under `user://`, in the shape below |

Two things are worth taking. The **four-slots-ORed-together** semantics give an action several
bindings at once without a mode, and the **code-uniqueness rule** (reassigning a control steals it
from its previous owner rather than double-binding) is the behaviour a rebinding UI needs anyway.

The encoding is not worth taking. A binding here is a fixed slot of a fixed type on the one device
the program can see, which is why axes are unbindable and why ten buttons is a hard number rather
than a configured one. Wanting more than one pad, an axis driving a digital action, or a device that
survives a replug is what makes a binding a device identity plus a tagged control rather than a
scancode, held in a list rather than in typed slots.

## The CSVM keymap file

One JSON file per player under `user://`, named `bindings_p<n>.json`, written atomically through a
sibling temp file and a rename. It is versioned, and the version says how the tokens below are
encoded rather than which actions exist: an action a file does not name simply stays at its shipped
default, so adding one needs no bump.

```json
{
  "version": 3,
  "player": 1,
  "mouseFlying": false,
  "contexts": {
    "flight": {
      "FireGuns": ["keyboard/key:Space", "pad:*/button:B"],
      "PitchUp": ["pad:*/axis:LeftY+@0.25", "pad:03005fcf1d2300000002000000000000/fullaxis:1+@0.02"],
      "SelectGunGroup": ["pad:03005fcf1d2300000002000000000000/hat:0:Right"],
      "TargetPreviousEnemy": ["keyboard/key:Shift+E"],
      "TargetNextAlly": []
    },
    "menu": { },
    "camera": { }
  }
}
```

A binding is one string, `device/control`, in words rather than numbers so a player can correct a
row by hand.

- The device is `keyboard`, `mouse`, or `pad:<hardware id>`. A shipped pad row is authored on the
  placeholder id `*`, which the loader replaces with the seat's own pad.
- The control is `key:<name>`, `button:<name>`, `mouse:<name>`, `axis:<name><sign>@<deadzone>`,
  `fullaxis:<index><sign>@<deadzone>` or `hat:<index>:<direction>`. A name is the engine's own enum
  name, or `#<number>` for a code the engine does not name or names only as a range sentinel
  (`SdlMax`, `Max`); a bare number is accepted on the way back in either way. An axis carries its
  sign and its deadzone, which is both the noise gate and the digital threshold.
- A `fullaxis:` token is one binding over an action pair (pitch, roll, yaw, throttle), written once,
  under the pair's positive row (`PitchUp`, `RollRight`, `YawRight`, `ThrottleUp`), and loaded onto
  both rows. Its sign is invert: `+` feeds raw positive travel to the positive row, `-` to the
  negative one. Each side reads 0 at the deadzone edge and 1 at full travel. The deadzone is
  honoured anywhere in 0 to 0.95 and written back exactly as read. The same token under
  `ThrottleLever` is an absolute lever on that row alone: its whole travel maps to 0 (idle) to 1
  (full), `-` swapping the ends, and the deadzone trims both ends of the travel instead of the
  centre. A full axis on any other row outside every pair, or on the keyboard or mouse, is
  unreadable.
- A `hat:` direction is `Up`, `Right`, `Down` or `Left` in any case, on a joypad other than the
  placeholder `*`. Version 3 of the file adds the full-axis and hat tokens; an older file names
  neither and still loads whole.
- A key name may carry the original's own modifiers in front of it, `key:Shift+E`, `key:Ctrl+E`,
  any of `Shift`, `Ctrl` and `Alt` in any order and any case. That prefix is version 2 of the file;
  a version 1 file names no modifier, and since a bare key token means the same thing in both, such
  a file still loads whole and the reader checks no version.

Every action of every context is written, the ones bound to nothing included, so a deliberate unbind
survives a reload rather than coming back at its default. Whether a seat reads the keyboard is not
written: a saved file could otherwise hand a pad-only splitscreen seat the keyboard back.

`mouseFlying` is the seat's flying scheme, the CONTROLS page's own row (`docs/controls.md`): true
gives the stick the mouse, false leaves it to head-look. It is a scheme rather than a binding, so it
sits beside the contexts instead of in one, and a file that does not name it reads false, which is
why it costs no version bump.

Nothing costs the file. A row the reader cannot read costs that action its saved bindings and
nothing more, leaving it on the shipped default while the rest of the file loads. That covers an
unknown context or action name, a token in a shape this build does not know, and a `hat:` row on
the placeholder pad `*`, which is deliberately unreadable because Godot reports a pad's d-pad as four
buttons and a hat row there would be a second encoding of a control the defaults author as a button.

Stick rows are the exception to "every row lives here". A binding on a flight stick's identity
(`pad:stick:<model>`) belongs to the stick profile files below, so player 1's keymap is completed
from them when it is loaded, and a stick token found in `bindings_p1.json` is dropped at that
merge. The keymap file is saved without stick rows.

## The CSVM stick profile files

One JSON file per stick model and layout. Every stick belongs to seat 1, so these files complete
player 1's keymap: for each connected stick model one file is active, and its rows replace every
stick binding the keymap carries. There are two folders. The shipped profiles are
`CSVM/data/stick_profiles/` (`res://data/stick_profiles/`, exported through the preset's
`data/*.json` include filter, whose `*` crosses folders) and are read-only. The player's own are
`user://stick_profiles/`, and every save writes there.

```json
{
  "version": 1,
  "model": "231D/0200",
  "name": "R",
  "companions": ["231D/0201"],
  "ignore": false,
  "contexts": {
    "flight": {
      "PitchUp": ["fullaxis:1+@0.02"],
      "FireGuns": ["button:#0"],
      "SelectGunGroup": ["hat:0:Right"]
    },
    "menu": { },
    "camera": { }
  }
}
```

- `model` is the stick's USB vendor and product id in hex, `VVVV/PPPP`, the form `--dump-sticks`
  and the stick roster log print. Any case reads and upper case is written, since a binding's
  device identity compares ordinally. A missing or malformed model refuses the whole file, with one
  log line naming it.
- `companions` lists the other models that must all be connected for the file to apply. The
  model itself is dropped from the list and duplicates collapse. One malformed entry refuses the
  whole file: reading it as fewer companions would make the file active on the wrong hardware.
- `name` is the short label screens print ("R", "L"); empty when absent, and left out of a saved
  file when empty (a saved generic default has none). An unnamed stick prints
  as `Stick 231D/0200` on the remake Controls screen and in status lines, so two unnamed sticks
  read apart, and as its control alone in the KEYS AND BUTTONS page's narrow Stick column.
- `ignore: true` makes the model yield no bindings while the file is active, for a device that
  enumerates as a joystick but flies nothing (a gaming keypad). It still counts as a profiled
  device, so the generic single-stick default does not claim it. The shipped `1532-022B.json`
  ignores the Razer Tartarus keypad this way; a user file for `1532/022B` overrides it.
- `contexts` holds the rows, per context and action, with the keymap file's context and action
  names. A row is an array of tokens in the keymap grammar above, written bare, without the
  device, since the file's model is the device: `button:#27`, `axis:#3+@0.1`, `fullaxis:1+@0.02`,
  `hat:0:Up`. Buttons and half axes are written by number, because the engine's gamepad names mean
  nothing on a stick. A full keymap token naming this file's model (`pad:stick:231d/0200/button:#27`)
  also reads. Only button, axis, full-axis and hat controls are readable. A full axis follows the
  keymap file's rules: written once under the pair's positive row, the lever row taking one too.
- **The file counts from 0; the screens count from 1.** Button, axis and hat numbers in a file are
  SDL2's 0-based indices, the numbers `--dump-sticks` prints. The Controls screens label each one
  higher, matching VKB's configuration tool and Windows: `button:#4` shows as "R Button 5",
  `fullaxis:2-@0.02` as "R Axis 3 inverted", `hat:1:Left` as "R Hat 2 Left". Hat 0 shows without a
  number ("R Hat Up"). A hand edit uses the file's number, one lower than the label.
- Only bound actions are written. A profile has no defaults, so an absent action is unbound.
- A row is read whole or not at all. An unreadable row (an unknown context or action, a token for
  another device, a deadzone outside 0 to 0.95) binds nothing, and a re-save writes it back
  verbatim unless the action has been bound since. A deadzone is written back exactly as typed.
- `version` is the schema of the header and the row shape, currently 1; the tokens follow the
  keymap file's version. The reader checks no version, on the keymap file's rule. Comments and
  trailing commas are accepted on read; a re-save keeps neither, nor any unknown top-level field.

**Which file is active.** For each connected model, a file applies when its model and every one of
its companions are connected. Among the files that apply, the one naming more companions wins, then
a user file over a shipped one, then the ordinally first file name. So an R stick alone takes its
solo file and R with L takes the file naming L, and a user copy of a layout overrides the shipped
file of that layout. The choice is made again on every plug and unplug, and a seat already flying
reads the new rows when it re-reads its keymap. Two identical units of one model are one device and
share one file. A companion that Godot reads as a pad is not in the stick roster and does not count
as connected.

**File names.** A name carries no meaning on read, only in the last tie-break. A copy made beside a
user file (Explorer's `231D-0200 - Kopie.json`) ties with it and sorts first, so it becomes the
active file; the log warns with both names whenever one file wins on its name alone. A save of a user
file rewrites that file; any other save writes the model as `231D-0200`, then each companion in
model order after a `+`, then `.json`, into the user folder. That is how two
layouts of one model sit side by side (`231D-0200.json` and `231D-0200+231D-0201.json`), and how a
save of a shipped profile becomes the user copy that overrides it.

**Saving from a Controls screen.** Accepting player 1's changes writes each connected model's stick
rows to its active file (a shipped one becoming the user copy), or to a new solo user file when no
file applies, and writes only models whose rows changed. A save replaces every context of the
file, the menu and camera rows included, from what the screen holds. The screen therefore stages
seat 1's stick rows from the profiles in force: the pause menus read through a seated reader, a
registration follows the profiles when a save or a plug changes them, and the generic default that
a save hands to another stick writes no file for that stick. The keymap file is written without
stick rows. The remake Controls screen's "Open profiles folder" row creates `user://stick_profiles/` when
missing and opens it in the system file browser. A binding's deadzone has no screen control; it is
edited in these files.

### The generic stick default

A stick no profile file covers still flies, under one condition: it is the **only** connected
model that is both unprofiled and stick-shaped. An ignored profile counts as a profile. With two
or more candidates nothing is claimed, since axis numbers on an unknown stick are a convention and
two candidates leave no way to tell which one should fly.

- **Stick-shaped** means at least three axes, with axes 0 and 1 resting within 0.1 of centre. The
  rest is sampled once per connection, ten roster updates after the device opens, because
  DirectInput reads zeros until a device has been polled a few times. Other axes may rest anywhere:
  a throttle lever parks where it was left (the VKB L reads 1.00 on axis 2 at rest, the R -0.57).
  A stick held deflected while it is sampled is judged not stick-shaped until it is plugged in
  again. While any candidate is still unsampled nothing is claimed.
- **The layout.** Axis 0 (X) is roll and axis 1 (Y) pitch, each a full axis with deadzone 0.02 and
  pitch not inverted (pulled back is positive, as on a pad). Axis 5 (Rz, the twist) is yaw when the
  device has six axes or more, DirectInput's usual X, Y, Z, Rx, Ry, Rz order; on the VKB Gladiator
  EVO R the twist reads -1..1 on axis 5 through SDL2, negative twisted right, so yaw is bound
  inverted. Axis 2 (Z) is the absolute Throttle (lever),
  inverted so raw -1 is full throttle, deadzone 0.02; on the VKB R the lever reads -1 pushed
  forward. The lever's takeover rule keeps a parked lever from moving the throttle until it is moved. Button
  0 fires the guns and button 1 the rockets. In menus the hat moves the cursor, button 0 confirms
  and button 1 backs out. Button 0 is also Skip Cutscene (below).
- **It lives in memory.** The profile log names it `generic (generic default)`. A controls screen
  save that changes any of its rows writes the whole layout as a user file under the model's name,
  which from then on is the model's profile; an unchanged save writes nothing.
- A device that enumerates as a joystick but is no flight stick (a gaming keypad whose axes rest
  centred) passes the shape test, so the default can claim it once it is the only unprofiled
  device. A profile file with `"ignore": true` for that model keeps it off. The Razer Tartarus
  (`1532/022B`, six centred axes under SDL2) ships with one, so it neither takes the default nor
  counts against a stick beside it.

### Skip Cutscene, the stick's skip

A stick raises no Godot input event, so none of the event-driven skips (the in-world cutscene's
press, the cinema screen's authored sets, a boot film or still) sees it. `SkipCutscene` is the
Menu-context action that stands in for them, read by `Sticks/StickSkip.cs` from seat 1's active
stick profile alone.

- **Where it counts.** Its press is `CinemaSkips.StickPress`, which is `CinemaPress.PadButton`, so it
  ends exactly the cinemas and boot cards a pad button ends, and an Escape-only set stays exempt. In
  flight it is the same skip a key press gets; declined (no skip armed), the held trigger
  fast-forwards the scene until it is released.
- **Who reads it.** Seat 1 only, as for every stick read, and nothing under `--no-pads` or `--det`
  (no roster, no profile set). The roster's input gate reads it neutral like any stick control.
- **Defaults.** The generic default binds it to button 0 (`Button 1`, the trigger). Keyboard, mouse
  and pad ship it unbound and a row there adds nothing, since any key or pad button already skips.
  A shipped or user profile carries it as `"SkipCutscene": ["button:#0"]` in its `menu` context; a
  profile without the row gives the stick no skip until one is bound.
- **It shares its control.** Binding the skip never steals from another action in its context, and
  no other action steals from it (`ActionMap.Shares`), so the trigger stays menu accept and the
  controls screen asks nothing when the skip takes it. The steal rule holds for every other pair.
- The remake Controls screen lists it on the Menu tab and the original KEYS AND BUTTONS page on its
  Other tab, both as `Skip Cutscene`; an accepted screen saves it into the stick profile.

## Force feedback

The original drives an Immersion TouchSense stick through `CImmProject`, and every effect it plays
is authored in `CrimsonFF.ifr` (the filename string at `0x00628790`), which ships beside the
executable. The code never builds an effect: it loads thirteen of them by name at startup and then
only starts, stops and rescales them.

`FUN_00480840` is the constructor that opens the file and loads the set. It runs only when
`FUN_00440540()` returns 2 (the force-feedback device class) and `FUN_00536d10()` reports a device,
takes the `IDirectInputDevice2A` from `FUN_00536cf0`, binds it through `CImmDXDevice::Initialize`,
and then makes one `CImmProject::CreateEffect` call per name. Every carrier below asks two questions
before it touches the hardware: `FUN_00480d30()`, the per-call enable predicate, and the byte at
`DAT_0071c298 + 0x91d`, a global suppress flag that shuts the whole system off while it is set.

### The effect slots

Loaded in this order, each into a fixed offset on the manager object. Six of them fall back to a
neighbour when the file does not carry them, and the fallback is what the gain writes further down
exist for.

| Effect | Name string | Slot | Falls back to |
|---|---|---|---|
| `FireGun_large` | `0x006287a0` | `+0x148` | none |
| `RocketFire_large` | `0x006287b0` | `+0x158` | none |
| `GunHit` | `0x006287c4` | `+0x160` | none |
| `RocketHit` | `0x006287cc` | `+0x164` | none |
| `Collision_large` | `0x006287d8` | `+0x168` | none |
| `NitroStart` | `0x006287e8` | `+0x170` | none |
| `ExcessiveSpeed` | `0x006287f4` | `+0x184` | none |
| `TurretFire` | `0x00628804` | `+0x190` | `+0x148`, flag `+0x14c` |
| `FireGun_small` | `0x00628810` | `+0x140` | `+0x148`, flag `+0x14c` |
| `FireGun_medium` | `0x00628820` | `+0x144` | `+0x148`, flag `+0x14c` |
| `RocketFire_small` | `0x00628830` | `+0x150` | `+0x158`, flag `+0x15c` |
| `RocketFire_small_rear` | `0x00628844` | `+0x154` | `+0x158`, flag `+0x15c` |
| `Collision_small` | `0x0062885c` | `+0x16c` | `+0x168`, no flag |

The file carries two more effects, `EngineStart` and `EngineStop`, that no `CreateEffect` call names.
They are authored and unreachable.

### The carriers

Thirteen effects, nine routines. Every event the original rumbles for is one of these.

| Routine | Event | Condition, and what it writes |
|---|---|---|
| `FUN_004810d0` | gun fire | The weapon's `CALIBER`: below `0x32` takes `+0x140`, below `0x46` takes `+0x144`, at or above takes `+0x148`. Sets the hold-over `+0x13c` to the clock plus 0.3 s and stops the turret effect if that shares the slot. The gains 0.647, 0.82 and 1.0 are written only on the fallback path. |
| `FUN_00480f50` | ordnance launch | Only for a weapon whose flag word (`**(uint**)(*param_3 + 0x210)`) has `0x10` set. `0x08` (torpedo) takes `+0x158`, else `0x20000` (rear mount) takes `+0x154`, else `+0x150`. The gains 1.0, 0.58 at bearing 180 and 0.79 are fallback-path only. |
| `FUN_00481330` | a gun round taken | Always `+0x160`, with the gain written every time: 0.75 below 6.0 damage, 1.0 at or above. |
| `FUN_004813c0` | any other round taken | Always `+0x164`, gain written every time: 0.8 below 100.0 damage, 1.0 at or above. |
| `FUN_00481450` | a collision | Below 50.5 damage takes `+0x16c`, at or above takes `+0x168`. The gain is 1.0 for both; the 0.85 appears only where `Collision_small` fell back onto `Collision_large`. |
| `FUN_004814f0` | the nitro engaging | `+0x170`, a plain stop and start with no gain written at all. |
| `FUN_00481540` | flight past the rated maximum | `+0x184`, started once and held: sets the hold-over `+0x178` to the clock plus 0.2 s on every tick past the gate. Re-parameterises the gain from its float argument, but only when that has moved more than 0.1 since `+0x17c` and at most once a second (`+0x180`). |
| `FUN_00481640` | the turret gunner firing | `+0x190`, started once and held, hold-over `+0x18c` at the clock plus 0.2 s. The gain 0.515 at bearing 180 is fallback-path only. |
| `FUN_00480d60` | the per-frame tick | Stops each of the three held effects once the clock passes its hold-over, which is what gives the gun, overspeed and turret effects their length. |

### The authored effects

Read out of `CrimsonFF.ifr` itself. `Magnitude` and `AttackLevel` are 0 to 10000, `Duration` is
microseconds, `Direction` is hundredths of a degree.

| Effect | Type | Magnitude | Duration | Direction | Infinite |
|---|---|---|---|---|---|
| `FireGun_small` | Periodic | 4400 | 90900 | 0 | yes |
| `FireGun_medium` | Periodic | 5600 | 125000 | 0 | yes |
| `FireGun_large` | Periodic | 9534 | 167000 | 0 | yes |
| `RocketFire_small` | Vector Force | 7500 | 250000 | 0 | no |
| `RocketFire_small_rear` | Vector Force | 5500 | 250000 | 18000 | no |
| `RocketFire_large` | Vector Force | 9371 | 500000 | 0 | no |
| `GunHit` | Vector Force | 8000 | 100000 | 27000 | no |
| `RocketHit` | Compound | push 8500, rumble 6434 | 400000 and 500000 | 27000 and 0 | no |
| `Collision_large` | Compound | pop 10000, rumble 8500 | 800000 and 1000000 | 27000 and 0 | no |
| `Collision_small` | Compound | push 8500, rumble 6434 | 400000 and 500000 | 27000 and 0 | no |
| `NitroStart` | Pop | attack 10000 | 1000000 | 0 | no |
| `ExcessiveSpeed` | Periodic | 4500 | 1000000 | 27000 | yes |
| `TurretFire` | Periodic | 3500 | 1000000 | 18000 | yes |

A compound is a list of child GUIDs, and the code starts the parent. The three infinite effects
carry a `Duration` that never applies; their length at the controls is the hold-over above.

### What CSVM plays

`CSVM/src/Bindings/PadRumble.cs` holds this survey as a table of fifteen rows, one per event the
carriers above distinguish. The mapping to a two-motor pad is mechanical: a `Periodic` child drives
the weak motor and a `Vector Force` or `Pop` child the strong one, which is the nearest a pad comes
to hardware that took a bearing. A magnitude is the authored one over 10000, except where the code
writes a gain on every call (the two hit routines and the collision routine), where the gain is the
number. A length is the authored `Duration`, except on the three infinite effects, where it is the
hold-over `FUN_00480d60` stops them after.

⚠ **Direction is dropped, not folded in.** Seven of the thirteen effects aim along a bearing to what
caused them. A pad has two motors and nothing to aim, so the bearing is discarded rather than turned
into a left/right split, which would be a different cue rather than a smaller one.

The fallback gains are not reproduced. They exist so a file missing an effect still rumbles with a
rescaled neighbour, and CSVM's table has a row for every event already.

A seat rumbles only while its last real input came off the pad side, the same `ActiveDevice`
reading that picks which control a prompt names. A seat flown on the keyboard with a pad plugged in
and untouched stays silent until that pad is pressed, and a key press silences it again. This is a
gate in front of the table rather than a change to it: every row, band and the Game Options toggle
stand as above.

### The one quantity not pinned

`FUN_00481540` rewrites the overspeed effect's gain from a float its caller hands it, rate-limited to
once a second. That float is a speed ratio, but the scale it is expressed on was not established, so
CSVM plays the effect at its authored 4500 throughout a dive instead of tracking the ratio. The cue
is there and its strength is the original's; what a deeper dive adds to it is not.

## Open questions

- The `key_*` string table at `0x0060cdcc`-`0x0060d30f`, with a 120-entry pointer array at
  `0x00640708`, is the GOSSCRIPT console's key-name constants and has no visible xrefs. It is not
  the command map and its parallel value array was not chased.
- Commands are 600 slots wide but only 64 ids are defined, sparsely, up to `0x6f`. Whether the gaps
  are cut features, debug commands or slots for another build was not investigated.
