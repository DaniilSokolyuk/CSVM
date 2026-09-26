# The in-flight pause screen, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`). Every claim below names the function it came from.

Everything here is a description of *behaviour and constants*. No decompiler output is reproduced;
the addresses are given so any claim can be re-checked at source.

**Where the other halves live.** The authored side is `extracted/zrdr/escape.zrd.json` and its
Instant Action twin `ia_escape.zrd.json`, whose container format is
[`formats/zrdr.md`](../formats/zrdr.md); the artwork is `extracted/rimage/`. The objectives
parchment and its check are decoded in [`formats/objectives.md`](../formats/objectives.md), which
this page does not repeat. How the 800x600 source artwork meets a modern window is settled in
[`campaign-board.md`](campaign-board.md). The sibling screen, which shares this one's map drawer, is
[`loading-screen.md`](loading-screen.md).

## Function map

| Address | Role |
|---|---|
| `FUN_004a0d20` | The pause-dialog constructor: picks the definition file, resolves the dialog by name, binds `MAP`, `OWNSHIP`, `MYZEP`, `OBJECTIVESLIST` and `MEMENTO`, binds the buttons, runs `ESC_SCRIPT` |
| `0x004a1428` | The pause screen's dialog-name builder, `sprintf` over the two format strings below, and the only caller of `FUN_004a0d20` (at `0x004a14ba`) |
| `0x004a14f0` | The Instant Action mission-type letter table the name builder jumps through |
| `FUN_0045fde0` | The `MAP` control's reader (its vtable slot `+0x78`): `CLIP`, `WORLD`, then the base reader, then the derived screen rectangle |
| `FUN_0045ff20` | The `MAP` control's world-to-screen projection, and the in-window test |
| `FUN_00460000` | Places one icon control through the map's window: project, set position, or turn the icon off |
| `FUN_0041a820` | The profile's memento image name, with its extension stripped |
| `FUN_005c4b30` / `FUN_005c4a70` | Bind a named `PRIMITIVES` entry / a named `BUTTONS` entry into a dialog member |
| `FUN_00470c40` / `FUN_00429280` | The objectives-list control's constructor and its name binding |
| `FUN_00470df0` / `FUN_00470f10` | The objectives-list control's reader and its draw, vtable `PTR_FUN_00607f90` slots `+0x78` and `+0x24` |
| `FUN_004710c0` / `FUN_00471230` | Appends one row to the list, and turns a row on or off |
| `FUN_005c8280` / `FUN_00455ac0` | A text control's reader, and the `WORDWRAP` rectangle it sets |
| `FUN_0042ab40` | The script interpreter the screen's `ESC_SCRIPT` runs through |
| `FUN_004a15d0` / `FUN_004a1630` / `FUN_004a1620` / `FUN_004a15f0` | RESUME, RESTART, PREFERENCES and QUIT |
| `FUN_004a1650` / `FUN_004a1660` | LOAD GAME and SAVE GAME, which the shipped data never draws |
| `FUN_004639b0` | The is-Instant-Action predicate, which picks the definition file |
| `PTR_FUN_006086b0` | The `MAP` control's vtable, the same class the load screen's map member takes |

⚠ **The row for `0x004a1428` in [`loading-screen.md`](loading-screen.md)'s function map named the
load screen.** That block is this screen's builder: `0x0062950c` (`loading_i%d%c`) and `0x0062951c`
(`loading_c%d%d`) are read only from `0x004a1459` and `0x004a1472` inside it, and it calls
`FUN_004a0d20`. The load screen's builder is `0x004a1eb0`, which reads its own copies of the same
three format strings at `0x00629648`, `0x00629658` and `0x00629668` and calls `FUN_004a1910`.

## The pause screen and the load screen are one drawer with two bindings

`FUN_004a0d20` and `FUN_004a1910` are separate constructors that reach the same classes through the
same binders. What they share:

- the definition-file opener `FUN_00579c60` and the by-name entry lookup `FUN_0057a090`, with a
  lookup that misses retrying the literal `"default"` entry;
- the dialog loader `FUN_005c4250` (background images and cursor) and the primitive binder
  `FUN_005c4b30`;
- **the same `MAP` control class at the same member offset**, `+0x68f40` of the dialog, with the
  vtable `PTR_FUN_006086b0` whose reader is `FUN_0045fde0`;
- the same objectives-list class (`FUN_00470c40`, 0x43a4 bytes) at the same member, bound from the
  shared `LOADINGDIALOG` block and registered under the name `OBJECTIVESLIST` by `FUN_00429280`;
- the same `MEMENTO` member, filled by the same `FUN_0041a820` name and `FUN_005c51a0` load, and
  set to the same `-1.0` through vtable slot `+0x60`;
- the same script interpreter `FUN_0042ab40` and the same finishing sequence (`FUN_005c4c90`,
  `FUN_005c0d00`, the dialog's own slot `+0x8`, `FUN_005c4c40`, `FUN_0042ad60`).

What differs:

| | pause (`FUN_004a0d20`) | load (`FUN_004a1910`) |
|---|---|---|
| definition file | `ia_escape.zrd`, else `escape.zrd` | `ia_loading.zrd` / `mp_loading.zrd`, else `loading.zrd` |
| the campaign gate | none: `MAP`, `OBJECTIVESLIST` and `MEMENTO` are bound whatever the mode | `FUN_004639a0` must hold, or none of the three is bound |
| `OWNSHIP`, `MYZEP` | bound from the shared block and placed through the map window | not bound |
| buttons | `RESUME_MISSION_BTN`, `RESTART_MISSION_BTN`, `CONFIGURE_BTN`, `MAINMENU_BTN`, plus `LOADGAME_BTN` and `SAVEGAME_BTN` when `DAT_0071bb80` is 1 | `PROGRESS` |
| the script | `ESC_SCRIPT`, else `SCRIPT` | `LOADING_SCRIPT`, else `SCRIPT` |
| after building | returns | pumps `FUN_004a18a0` three times |

So one map drawer serves both screens, and the pause screen is the one that also places things by
world position.

⚠ **The two files' campaign dialogs are not copies of each other.** Their `PRIMITIVES` blocks agree
entry for entry across all 24, and so do the flag pins and the memento shadow, but every
`LOADING_SCRIPT` is a superset of the matching `ESC_SCRIPT`: the loading one also spins the
propeller and places the mission's device icons
([`loading-screen.md`](loading-screen.md)). Read each screen out of its own file.

## Which dialog is shown

`FUN_004a0d20` opens `ia_escape.zrd` when `FUN_004639b0` (is-Instant-Action) holds and falls back to
`escape.zrd` when that predicate is false or the file fails to open. Both files ship.

The dialog name is built at `0x004a1428` from the same globals the load screen reads, and there are
only two branches, so **a multiplayer pause takes the Instant Action path's format string**:

| Format string | Address | Used when |
|---|---|---|
| `loading_i%d%c` | `0x0062950c` | `FUN_004639b0` holds: `%d` is the environment number `0x0071c09c`, `%c` the mission-type letter |
| `loading_c%d%d` | `0x0062951c` | otherwise: `%d%d` is `0x0071c09c` then the mission number `0x0071c0a0` |

The letter comes from the jump table at `0x004a14f0`, five entries wide over the Instant Action
mission-type index `0x00718cd8` with an index above 4 falling to the default: `0` to `a`, `1` to `d`,
`2` to `z`, `3` to `d`, `4` to `s`. This agrees entry for entry with the load screen's own copy.

⚠ **The two digits of `loading_c%d%d` are the ZBD world folder and its `M0n` number, not the act and
its position.** `0x0071c09c` is 1 for `C1`, 2 for `C1B`, 3 for `C1C`, 4 for `C2`, 5 for `C2B`, 6 for
`C3`, 7 for `C4` and 8 for `C5`, which is the same pair the save id `campaign * 100 + mission` and
the briefing state `brief_c<NN>` are built from
([`formats/campaign-missions.md`](../formats/campaign-missions.md)). `CM01` (`C3/M01`) is therefore
`loading_c61`, and `loading_c31` is `CM06` (`C1C/M01`). The 24 campaign dialogs in `escape.zrd` are
exactly the 24 the sequence names.

## The map is a source crop, and its window is a world-to-screen map

`FUN_0045fde0` reads the `MAP` primitive in three steps.

1. `CLIP`'s `topleft` and `bottomright` land at the control's `+0xf4`, `+0xf8`, `+0xfc` and `+0x100`
   as **integers**, and `+0x44` is pointed at that rectangle, which is what the base draw crops with.
2. `WORLD`'s `topleft` and `bottomright` land at `+0x104`, `+0x108`, `+0x10c` and `+0x110`, also as
   integers. The reader (`FUN_0057a5b0` into `FUN_0057a2d0`) converts an authored float with `ftol`,
   so **every `WORLD` bound is truncated toward zero**: `-1571.470947` is stored as `-1571`.
3. The base reader `FUN_005c4fd0` takes `POSITION` and `BITMAP`, and then the derived screen
   rectangle is computed from the control's own on-screen position (vtable slots `+0x68` and `+0x6c`)
   plus the clip's size:

   ```
   screenX0 = POSITION.x                      -> +0x114
   screenY0 = POSITION.y                      -> +0x118
   screenX1 = POSITION.x + (CLIP.x1 - CLIP.x0) -> +0x11c
   screenY1 = POSITION.y + (CLIP.y1 - CLIP.y0) -> +0x120
   ```

**`CLIP` is a rectangle in the map bitmap, not on the screen.** The bitmap is drawn so that
`CLIP.topleft` lands on `POSITION`, and the visible map therefore occupies `POSITION` to
`POSITION + CLIP size`. That is the only reading the derived rectangle above supports, and it is what
the reference stills show: every campaign dialog puts the map at `POSITION [16, 19]` with
`CLIP [211, 51]..[784, 551]`, which is the 573x500 chart area of the 800x600 sheet landing in
`loadframe`'s own 573x500 window. The four Manhattan dialogs crop `[211, 30]..[784, 530]` instead,
the same size two rows higher up the sheet.

`FUN_0045ff20` is the projection. Given a world position `p`:

```
u = (p.x  - WORLD.x0) / (WORLD.x1 - WORLD.x0)
v = (-p.z - WORLD.y0) / (WORLD.y1 - WORLD.y0)
screen.x = ftol(screenX0 + (screenX1 - screenX0) * u)
screen.y = ftol(screenY0 + (screenY1 - screenY0) * v)
```

and it answers "on the map" only when `0 <= u <= 1` **and** `0 <= v <= 1` (the two constants
compared against are `0.0` at `0x00603398` and `1.0` at `0x006032e8`). The map's vertical axis is
**negated world Z**, and world Y (altitude) is not read at all, so the chart is a plan view and a
climb moves nothing. Neither `WORLD` bound is required to be the smaller one: the campaign dialogs
author x ascending and y descending, so `v` grows as `-z` shrinks.

`FUN_00460000` is the only caller of the projection. It projects the icon's world position; when the
answer is off the map it calls the icon's vtable slot `+0x64` and the icon **does not draw**;
otherwise it sets the icon's position through slot `+0xc` and finishes with slot `+0x60` at `-1.0`,
the same value the memento takes.

## What the pause screen places by world position

`FUN_004a0d20` binds two icons from the shared `LOADINGDIALOG` block, neither of which carries a
`POSITION`, because both get one at runtime:

- **`OWNSHIP`** (`singledev`, `CENTER [1]`) is the player. Its world position is read from
  `DAT_0071c298 + 0x204` and its rotation from
  `atan2(-*(float *)(DAT_0071c298 + 0x1e0), -*(float *)(DAT_0071c298 + 0x1e8))`, which is the
  player transform's own forward vector, so the icon turns with the nose. That angle is the
  mission data's own yaw, which runs opposite a compass heading, and nothing is added to it.
- **`MYZEP`** (`NW-m1wv_icon`, `CENTER [1]`) is the zeppelin named `piratezep`. `FUN_004bd3e0`
  looks that object up by name and takes its transform's translation and its own stored angle;
  failing that, `FUN_004d0280(7, "piratezep")` is tried and the object's position is taken with a
  zero angle; failing both, the icon is turned off. A mission with no `piratezep` therefore draws
  no zeppelin icon.

## The objectives list stops nowhere, and its authored box is one row's, not the list's

`FUN_00470df0`, the list control's reader, takes five named entries out of `OBJECTIVESLIST`:
`BACKGROUND` into `+0x38`, `TITLE` into `+0x12c`, `SPACING` into `+0x22d8` as an integer, `LIST`
into the text control at `+0x22dc`, and `CHECKMARK` into `+0x21e4`. The lookup `FUN_00579ff0`
recurses into nested blocks, which is why `SPACING [5]`, authored inside `LIST`, is found from the
`OBJECTIVESLIST` block. The rows themselves are a vector of `0x20bc`-byte entries at `+0x4398`,
`+0x439c` and `+0x43a0`, each a copy of the `LIST` control with the row's completed byte in front
of it.

**`WORDWRAP`'s height is the box one row wraps in, and it is never a limit.** `FUN_005c8280` reads
the pair at `0x005c82c2` into the rectangle `{0, 0, 190, 255}` and hands it to the text control's
vtable slot `+0x70`, `FUN_00455ac0`, which stores it at `+0x2080`..`+0x208c` and raises the flag at
`+0x207c`. The layout then takes the larger of the two in each axis: at `0x005c76a1` the laid-out
bottom becomes `max(bottom, 255)` and at `0x005c76b3` the right becomes `max(right, 190)`, so the
rectangle is a **minimum** extent on the control's reported box, never a clip on its text. The
height the list advances by, `+0x20b0`, is taken at `0x005c7692`, before that clamp, and is the
text's own measured height. Because every row is a copy of the one `LIST` control, the 255 belongs
to each row separately, and no single objective is anywhere near that tall.

**So the original neither scrolls, clips nor shrinks a list longer than its artwork.** The draw
`FUN_00470f10` walks the row vector from `0x00470f7c` to `0x00471013`, and the only condition
inside the loop is each row's own hidden flag and whether its checkmark is set; the y accumulator
grows at `0x00470fe7`..`0x00470ffb` by the row's measured height plus `SPACING`, and the loop ends
only when the row pointer reaches `+0x439c`. Nothing is compared against a box. `FUN_004710c0`,
which appends a row, is called only from the script's `Objective` opcode at `0x00429590` and is
guarded by the display row count alone (`FUN_004ad180`), so it refuses nothing either. A list
taller than the 240x312 `parchment` bitmap would simply run down off it and keep drawing.

**The remake keeps every row but holds them on the paper.** Stacking rows off the artwork is what
the executable does, and on the parchment it reads as ink spilled over the torn edge, so the remake
takes the box from the bitmap instead. The largest fully opaque rectangle in the 240x312 `parchment`
art (alpha at least 250) runs from x 15 to x 214 and y 21 to y 283, which off its `POSITION
[555, 6]` puts the paper's right edge at x 770 and its bottom at y 290. `EscapeObjectivesList`
carries those two and gives the list `RowWrap` 190 (the authored `WORDWRAP` width exactly) and
`RowBox` 240 from its `[580, 50]` corner. `BoardNote.Shrink` then asks the renderer for a fit:
`UI/Boards/ComposedBoardView.cs`'s `Fitted` steps the face down a point at a time until every entry fits
that box in both axes, and never cuts a row or a word. `RowFont` is 13 rather than the authored 14
because the substitute face is wider and taller per character; at 13 all 24 campaign lists keep the
original's own line breaks and stand inside the paper, the deepest, `CM15` (`C2/M05`), ending at
y 286 and the widest, `C4/M05`, reaching x 770. The `pause-sheet` and `load-sheet` suites measure
the paper off the bitmap again and hold every row of every sheet inside it.

## The flags are authored art, not a runtime reveal

Which numbered flag stands at each objective point, and which reads `?`, is **per-dialog authored
data in the `ESC_SCRIPT`'s own `Pict` opcode**, and the executable has no part in it.

- The bitmap set is `pin1` to `pin8` for the numbers, except that **`pin6` is the question-mark flag
  and `pin6_1` is the numbered six**. A dialog that wants a concealed point names `pin6`.
- The executable holds no `OBJPIN` string and no `pin%d` format string, so no code path substitutes
  one pin bitmap for another; `FUN_0042ab40` places the name the script gives it.
- `CM01` (`loading_c61`) authors `pin6` at `[506, 149]`, `pin6` at `[396, 254]`, `pin6` at
  `[181, 244]` and `pin4` at `[106, 319]`, which is the three question marks and the one `4` the
  reference stills show. `loading_c31` (`CM06`) authors `pin1` to `pin4` in order, and
  `loading_c43` (`CM13`) authors eight pins including `pin6_1` for its sixth.

So the answer to "which flags read `?` while rows 1 to 3 complete" is that none of them ever
changes: the sheet is a printed chart, and `CM01`'s first three treasure sites are drawn unknown on
it whatever the mission does. A `Pict`'s `center [true]` is the bitmap's middle landing on the
authored point, in both axes; measured against the reference still, `pin6` at `[506, 149]` and
`pin4` at `[106, 319]` each match to the pixel, as do the parchment at `[555, 6]` and the button
strips at their own positions.

## The four buttons, and the two that never draw

`FUN_004a0d20` binds four buttons unconditionally and two more when `DAT_0071bb80` is 1, which is
the same flag that distinguishes a campaign session. Each is its own one-off control class whose
vtable slot `+0x30` is the click handler; every handler opens with `FUN_005c2b90`, the button sound.

| Widget | Label | Handler | Does |
|---|---|---|---|
| `RESUME_MISSION_BTN` | `MSG_BTN_RESUME` | `FUN_004a15d0` | pops the screen off the state machine at `DAT_0071d3a0` and nothing else |
| `RESTART_MISSION_BTN` | `MSG_BTN_RESTART` | `FUN_004a1630` | asks the campaign state `DAT_0071d57c` for mission `-1`, the current one, through `FUN_00416f40`, then pops the screen |
| `CONFIGURE_BTN` | `MSG_BTN_PREFERENCES` | `FUN_004a1620` | tail-calls `FUN_00471660`, the preferences dialog |
| `MAINMENU_BTN` | `MSG_BTN_QUIT` | `FUN_004a15f0` | tears the mission down (`FUN_00455200`), runs `FUN_004a0a30`, and asks for `FUN_00419700` in a campaign or `FUN_00419440` otherwise |
| `LOADGAME_BTN` | | `FUN_004a1650` | `FUN_00419860` |
| `SAVEGAME_BTN` | | `FUN_004a1660` | `FUN_00419840` |

⚠ **`LOADGAME_BTN` and `SAVEGAME_BTN` have no entry in the shipped `escape.zrd`.** The shared
`LOADINGDIALOG` `BUTTONS` block defines exactly the first four, so `FUN_005c4a70` finds nothing for
the other two and they are never drawn or hit even in a campaign, which is why the reference stills
show four strips. The executable's own gate is live; the data simply does not answer it.

**QUIT's two targets differ by one code, and the code is as far as the decode reaches.**
`FUN_00419700` (the campaign one) and the untyped block at `0x00419440` (the other) both enter the
shell state at `DAT_0071d57c`, the `CZGOSState` `FUN_00416a10` builds, and both write
`DAT_0064b348` on the way: 6 for a campaign, 4 otherwise, where a cold start leaves 1
(`FUN_00416a10`) and the debrief 2 (`FUN_004194e0`). The state's own entry, `FUN_00416ad0`, reads
that field for one thing only, tearing the mission down again on 6; which screen each code opens is
not in the executable's own code and is presumably the shell script's. So the decode establishes
that the original distinguishes leaving a campaign mission from leaving any other, and not what
either lands on. This port lands each on the screen its flight was launched from, the campaign
mission on the cabin and an Instant Action sortie on the Instant Action screen, which is also where
the wrap-up board's `IAWU_B_CONTINUE` edge goes
([menu-inventory.md](menu-inventory.md)).

Each of the four is a three-bitmap strip with three faces: `escape_button1` with
`BtnEscapeNormal`, `escape_button2` with `BtnEscapeRollover` under the pointer and `escape_button3`
with `BtnEscapeActivate` while held, the label centred at the strip's own `offset [66, 7]` over a
132x28 plate. There is no disabled frame, so a strip with nothing behind it is still drawn live.

**The screen is pointed at, and says so in its own data.** Every dialog in `escape.zrd` carries a
`CURSOR` block of the same shape, `BITMAP [daglove]` with `CENTER [0]` and a `ROLLOVER` naming
`BITMAP [dafinger]`, so the pointer wears the glove bitmap by its top-left corner and swaps to the
pointing finger over a live widget. Both are 40x40 in the rimage set. That is the evidence that the
four strips are mouse targets and not a pad list: the rollover frame the strips author has a device
that can be over one strip without the cursor having walked there.

## The memento is the profile's own image name

`FUN_0041a820` copies the 0x104-byte string at `DAT_0064b684` into a scratch buffer and truncates it
at its last `.`; `FUN_004a0d20` hands the result to `FUN_005c51a0` on the `MEMENTO` control, so the
image the primitive draws is **the profile's memento file name without its extension**, and
`momento_temp` in the data is a placeholder the runtime always replaces. `FUN_004113b0` seeds
`DAT_0064b684` with `MS_P_InitialPinup1.jpg` when a profile is reset, and the names the campaign can
award are a 12-byte-record table at `0x0061af60`: a `char *` into the string block at `0x0061e12c`,
a mission number and an objective bit of 0, 1, 2 or 4. Index 0 is `MyMemento.jpg` at mission -1,
then 23 pictures, then an empty-name terminator at `0x006463b0`. Seven of the 23 carry mission 0
(`InitialPinup1`, `InitialPinup3`, `JustineBattleax`, `Mom`, `DoggiePhoto`, `Swan&NathaninCabin`,
`ZacharyandPlane`) and 16 name a mission, whose number agrees with the digits in their own file
names (`MS_P_12_01_BettysScreentest2.jpg` against mission 12) wherever the name carries digits;
`MS_P_IllsaandSparks.jpg` carries none and is mission 7. The table is read from `FUN_004113b0`,
`FUN_00410270` and `0x0040c85f`.

`MyMemento.jpg` is the player's own picture and never appears: its admission at `0x0040c8d8` is a
file test on the directory string at `0x0061f354` (`Assets\Graphics\Scrapbook`) concatenated with
the name and no separator between them, which resolves to no shipped path. Every other row is
admitted at `0x0040c896`: a mission 0 row always, and a row naming mission `m` once
`*(0x64cca4 + 168*(m-1))`, that mission's merged best objective mask, carries both bit 0 (the
mission won) and the row's own objective bit. Those records are the mission-result array of
[../formats/saved-games.md](../formats/saved-games.md) (`UIData +0x1868`, indexed from 1, 168 bytes
each with the merged half at `+0x54`): `FUN_004113b0` clears it as 0x3f0 dwords at `0x0064cc50`,
which is record 1 through record 24. `FUN_00410270` walks the table with `lstrcmpiA` and answers index 1, the seeded
pin-up, for a name it does not find. This screen draws whatever the profile holds.

`FUN_005c4b30` binds the control from the **dialog's own** `PRIMITIVES`, so the position (`[533, 326]`
in every campaign dialog) is authored while the picture is not. The script then places the shadow
`momento_shad` centred on `[661, 454]` as an ordinary `Pict`.

The remake draws the same picture the cabin wall does. `CampaignMementos.BitmapFor` is the one
resolution the three screens share: the flown mission's `CampaignDirector.Memento` hands it to the
pause readout, `Launcher` reads the seated profile off its store for the campaign load screen and
for the `--menu=pauseboard` screenshot door, and `CampaignCabinPage` hangs it on the wall. A
session with nobody seated draws the seeded pin-up, which is what the `--menu=pauseboard` door
shows when no `--campaign=` names a profile.

## An Instant Action pause draws the blackboard, not a map

This is the unfilmed case, and the data settles it. `ia_escape.zrd` opens for an Instant Action
session, and its `loading_i<env><letter>` dialogs carry the load screen's own blackboard: the
`loadframempt2` background, `MP-shotdown` at `[197, 157]`, `MP-crash` at `[197, 307]` and
`mp-dangerzone2` at `[197, 457]`, the four texts `HEAD1`, `HEAD2`, `OBJ5` and `OBJ6`, and an
explicit `Off [OBJECTIVESLIST]`. They carry no `PRIMITIVES` block at all, so no map, no memento and
no parchment, and `FUN_004a0d20`'s unconditional binds find nothing to bind. The four escape strips
come from the shared block and are the only difference from the Instant Action load screen.

`escape.zrd`'s own `loading_i*` dialogs do carry a map and a memento, but they are unreachable:
`ia_escape.zrd` ships and opens, so the fallback is never taken for an Instant Action session.

A multiplayer pause takes the same Instant Action format string (the builder has no multiplayer
branch), so it reads an `loading_i<env><letter>` dialog too, with the multiplayer environment number
never reaching it.

## The script is a beat sheet, and seven dialogs place their pins past a wait

`ESC_SCRIPT` is the same twelve-opcode vocabulary the briefing's own script uses
([`../formats/briefing.md`](../formats/briefing.md)), and a campaign dialog's is `ToBack`, `On`,
`Off`, `Objective`, `Pict`, `Spin` and `Wait`. The dialogs carry no `PlaySound` and no
`WaitForMarker`, so nothing waits on a narration; the only blocking beat is `Wait`, which seven of
the 24 campaign dialogs author at `0.1` seconds, always after the icons it turns off and before the
flag pins.

⚠ **Those seven place their pins after the wait**, so a reader that runs the script once and stops
draws their charts with no flags on them at all: `loading_c15`, `loading_c23`, `loading_c43`,
`loading_c45`, `loading_c54`, `loading_c65` and `loading_c82`. The pattern is always the same, a
`Pict`/`Spin`/`Off` group setting up an icon, the wait, then the `On` that shows it and the pins
after; the original's own redraw pump advances past it within a frame or two of the screen
appearing. Across the 24 dialogs the sequence authors 71 pins, and all 71 stand once the waits have
released.

## Where CSVM differs

`UI/Boards/PauseScreens.cs` composes the pause sheet at its authored coordinates and
`UI/Menu/Original/OriginalPauseBoard.cs` hangs it over the flown world in the Original presentation; the
Built-in presentation keeps `UI/Screens/PauseBoard.cs`. `UI/Menu/EscapeDialog.cs` reads `escape.zrd`,
its Instant Action twin and the load screen's `Loading.zrd`, and `UI/Overlays/MissionMap.cs` is the one map
drawer this screen shares with the campaign briefing and the load screen, which is where the world
window and the pin placement live.

**An Instant Action sortie pauses on its own blackboard.** `Session/Launch/GameSession.cs` keys the sheet on
the sortie's chapter, through `Mech3/CampaignSequence.cs`'s `ChapterNumber`, and its mission type's
own letter, reads it out of `ia_escape.zrd`, and hands `UI/Menu/Original/OriginalPauseBoard.cs` a board written
in `UI/Boards/BoardPalette.cs`'s `EscapeBlackboard`, the load screen's chalk with the near-black label inks
the strips' light plates need. The four texts are composed through `UI/Screens/LoadScreens.cs`'s
`DialogTexts`, the drawing the load screen already has for these dialogs, so the two screens write
the same words at the same authored points. The parchment stands or not on the dialog's own script,
which is what keeps it off this sheet, and with no map, memento or parchment the board asks for no
readout at all. RESTART reruns the sortie rather than a campaign mission, and PREFERENCES opens the
same leaf the campaign sheet opens. Free flight and the dogfight are modes of ours that no shipped
dialog describes, so they keep the Built-in board, the same split the load screen makes. The
`--menu=pauseboard-ia` door composes one with no sortie behind it.

**The three authored faces meet one of ours.** The extraction ships no menu typeface, so
`BtnEscapeNormal`, `BtnEscapeRollover` and `BtnEscapeActivate` become one face in three palette
inks rather than three faces, which is the same mapping every other composed board takes
([`campaign-board.md`](campaign-board.md)).

**The parchment's rows lean, and are set a point smaller than the data authors.** `ObjList` is
Andy Bold 14 italic, so both screens set the rows through `UI/Boards/ComposedBoardView.cs`'s synthetic
oblique, a 0.25-em x-shear of the board's own face; the `ObjListTitle` line above them stays
upright, which is how the original sets it. The shear is a transform on the glyph outlines and
leaves their advances alone, so it changes no line break. What does is the substitute face itself,
which is wider per character than Andy Bold: at the authored 14 inside the authored `WORDWRAP` of
190 the filmed mission's first objective takes two lines where the reference crop shows one. The
measure stays at 190, since that is the paper the parchment bitmap holds under the list, and the
face comes down to 13 instead. At 13 CM01's four rows break 1/2/2/1, the crop's own pattern, and 15
of the sequence's 78 rows still run to three lines or more because the face is wider than the one
the text was written for. A wider measure would buy those lines back only by writing over the
bitmap's torn right edge. Line breaks are a font metric and move with the window scale, so a list
that leaves the paper at some other scale is caught by `BoardNote.Shrink` rather than by these
numbers.

**One cursor serves all three devices.** The authored pointer is drawn, the glove over the sheet and
the finger over a strip, and the OS pointer is hidden while the sheet stands; but a hover moves the
shared cursor onto the strip it lands on rather than lighting a rollover the keyboard cannot see, so
a click fires the row the pad would have fired. That is the rule every Original page keeps
([`../architecture/UI.md`](../architecture/UI.md)), and it costs the original's ability to hover one
strip while the keyboard's selection rests on another. Only a seat holding a mouse points at all,
which is seat 0, so a pad player's pause is the pad's alone and the OS cursor is left as it was.

**Preferences opens the Original options over the held world.** The original opens its own
preferences dialog over the paused mission, and this port stands `UI/Screens/PausePreferences.cs`
there: the Original presentation's Options screen with the Game Options, AUDIO, VIDEO and
rebinding pages behind its doors, hosted over the pause rather than over the menu. The halt is
untouched while it stands, so the world stays held beneath it, and every door out (a page's
ACCEPT CHANGES, Back, or RETURN TO MAIN MENU) closes the leaf back onto the sheet with what it
applied already in force. An accepted page writes the options file and applies the display and mix
settings through the same route the menu takes, without the presentation teardown a menu-side apply
does, since the flight is what the leaf returns to. The rebinding pages hold the menu's own
`ControlsFeature`, so an in-flight rebind edits the one keymap, and an accepted one reaches the
seats flying behind the leaf at once, mouse scheme included. Auto Head Turn takes effect mid-flight
as the original's does (by the user's recall of it; the executable's caller of the settings apply is
not decoded): every accepted page puts the saved value on each human seat, and the remake's Next
Target switch with it, written into the live selection so its cycle and lock survive. The Default
View and the difficulty are saved for the next sortie and leave the running flight alone. Built-in's board reaches the same
leaf through its PREFERENCES row. Where the install carries no decoded layout there is nothing to
compose, so the Original strip is drawn and unbound and Built-in's row is left off.

**Photo mode is a fifth strip the original does not author.** It is this port's own feature, so the
sheet stands it in the authored plates and label offset the block's own RESUME carries, between
RESUME and PREFERENCES in the order a cursor walks, and the four authored strips keep their own
points. Where it stands is read off the block rather than fixed: the campaign block's two columns
of two leave a 128-pixel channel between them on RESUME's row, which the 132-pixel plate takes with
two columns of overlap at each neighbour's rounded end, while `ia_escape.zrd`'s three across leave
no channel (their midpoint is RESTART's own point) and the strip takes the free cell under RESTART
instead, level with MAINMENU. The rule is which of the two candidate points covers less authored
plate. The hit test answers the earlier row in walk order for a column two strips share. Built-in's
board keeps its own Photo Mode row.

**The progress bar in a campaign dialog is not drawn.** Every campaign `escape.zrd` dialog carries a
`PROGRESS` entry at `[90, 548]` copied from its `Loading.zrd` sibling, and `FUN_004a0d20` binds no
`PROGRESS`, so the original never draws it here either.

**The script is run out, not played.** `PauseSheet` advances its reveal until nothing is blocked and
no tween is live, so a `Wait` releases and a `Spin` lands at its end revolutions. The screen is a
still and the original's pump reaches the same state within a frame or two, so nothing here animates
what the original animates once.

**The ownship art is drawn off the top of the sheet, and this port takes that back off.** The
`singledev` bitmap is a 32x32 plan view whose own mirror axis lies at exactly 45 degrees: rotating
it 225 degrees clockwise is what stands it upright, the propeller disc at the top and the tailplane
at the end of the rear fuselage, so its drawn nose points up and to the left, an eighth of a turn
counter-clockwise of the chart's north. `nw-m1wv_icon`, the zeppelin, is drawn nose up, as is every
other icon the chart places. The original turns that art by the heading alone, so its own sheet
draws the player leaning by that eighth; `UI/Overlays/MissionMap.cs` takes the art's own nose off the turn
instead (`ArtRevs`, keyed by bitmap name), so the drawn nose lands on the heading the compass tape
reads. A chart whose icon and compass disagree is the one thing a pilot reads the sheet for, which
is why the departure is taken rather than reproduced. The chart is north up: its projection puts
world -Z at the top, which is the compass's own zero, and the sheet's own printed compass rose
agrees.

**The ownship icon needs a position inside the window.** Nothing draws it where the flown position
falls off the chart, which is the original's own answer and is what a mission's authored spawn hits:
`CM01`'s `PLAYER_INIT` is at world `[-1426, 150, -1813]` and its window's x runs `-10818` to
`-2204`, so the `--menu=pauseboard` door draws no plane. On the reference stills the icon stands
mid-flight instead. The window itself is cross-checked against real mission geometry: `CM01`'s two
literal `TRAVELERS` waypoints, `[-5458, 120, -5390]` and `[-6311, -27, -4611]`, project to
`[372, 237]` and `[315, 282]`, inside the island the chart draws and within 30 pixels of the pin the
first of them belongs to.
