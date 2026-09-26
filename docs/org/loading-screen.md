# The mission load screen, decoded from `crimson.exe`

Read out of the retail executable with Ghidra (static analysis of the shipped x86 build,
`crimson.exe`, `language x86:LE:32:default`), 2026-08-16. Every claim below names the function it
came from.

Everything here is a description of *behaviour and constants*. No decompiler output is reproduced;
the addresses are given so any claim can be re-checked at source.

**Where the other halves live.** The authored side is `extracted/zrdr/Loading.zrd.json`, whose
container format is [`formats/zrdr.md`](../formats/zrdr.md); the artwork is `extracted/rimage/`,
already read by `HudFont` and `ImpactReticle`. Our own load screen is `CSVM/src/UI/Screens/LoadBoard.cs`,
whose entry in [`architecture.md`](../architecture.md) carries the plumbing and the traps. This page
is the original's runtime: how it picks a screen, what fills its bar, and how it keeps drawing while
the mission loads.

⚠ **Everything below is a decode of the original, not a description of ours.** How the 800x600
source artwork meets a modern window is settled in [`campaign-board.md`](campaign-board.md), which
this screen inherits; what our own load screen draws out of the decode, and what it deliberately
does not, is in "Where CSVM differs" at the foot of this page.

## Function map

| Address | Role |
|---|---|
| `FUN_004a1910` | Load-dialog constructor: picks the definition file, resolves the dialog by name, binds `PROGRESS`, runs the screen's script block |
| `0x004a1eb0` | The name builder, `sprintf` over the format strings below, and the only caller of `FUN_004a1910` (at `0x004a1fbb`): the multiplayer branch first, then Instant Action, then campaign |
| `FUN_004a2100` | The progress setter: monotonic clamp, store, repaint |
| `0x005c3d40` | The `PROGRESS` control's repaint (its vtable slot `+0x8c`), which computes the fill width |
| `FUN_004a18a0` | The redraw pump, wall-clock throttled |
| `FUN_004a1db0` | The engine-init load steps, the clearest example of the milestone/pump pattern |
| `FUN_00463f40`, `FUN_00464680`, `FUN_00465370` | The mission-load steps carrying the remaining milestones |
| `FUN_004639b0` / `FUN_004639c0` / `FUN_004639a0` | The mode predicates: is-Instant-Action, is-multiplayer, and the one gating the campaign-only widgets |

## Which screen is shown

`FUN_004a1910` chooses its definition file by mode: `ia_loading.zrd` when `FUN_004639b0` holds,
`mp_loading.zrd` when `FUN_004639c0` holds, and `loading.zrd` when neither holds or when the
mode-specific file fails to open. This install ships only `loading.zrd`, which carries all three
families itself, so the fallback is the path actually taken.

The dialog is then looked up by a name built with `sprintf` from one of three format strings, and a
lookup that misses retries with the literal `"default"` entry:

| Format string | Address | Used for |
|---|---|---|
| `loading_i%d%c` | `0x00629658` | Instant Action |
| `loading_c%d%d` | `0x00629668` | campaign |
| `loading_m%d%c` | `0x00629648` | multiplayer |

⚠ **A second copy of the first two strings sits at `0x0062950c` and `0x0062951c` and belongs to the
pause screen**, whose own builder at `0x004a1428` reads them ([`pause-screen.md`](pause-screen.md)).
The two screens resolve the same dialog names out of different definition files.

The globals feeding them:

| Global | Meaning |
|---|---|
| `0x0071c09c` | environment number, the `%d` for Instant Action and campaign |
| `0x0071c0a0` | campaign mission number, the second `%d` |
| `0x00718cd8` | Instant Action mission-type index |
| `0x0071c194` | multiplayer game mode, 1-based |
| `0x0071c1a8` | multiplayer environment number |

The mission-type letter comes from a jump table at `0x004a14f0`, five entries wide with an index
above 4 falling to the default: `0` to `a`, `1` to `d`, `2` to `z`, `3` to `d`, `4` to `s`. The
multiplayer table at `0x004a201c` maps the mode minus one: `0` to `d`, `1` to `t`, `2` to `c`,
`3` to `z`. A second copy of the Instant Action table sits at `0x004a202c` for the fall-through
inside the multiplayer builder, and agrees entry for entry.

`FUN_004639a0` gates the campaign-only widgets. When it holds, `FUN_004a1910` additionally binds
`OBJECTIVESLIST` and `MEMENTO`; Instant Action and multiplayer screens carry neither, which is why
their definitions turn `OBJECTIVESLIST` off explicitly.

**What fills the two runtime bindings** is decoded in [`pause-screen.md`](pause-screen.md), whose
own screen binds the same members through the same functions: the `MEMENTO` primitive's picture is
the profile's memento image name with its extension stripped, and the `MAP` primitive's `CLIP` is a
rectangle in the chart bitmap whose `WORLD` pair is a world-to-screen window. That page also carries
the map drawer this screen shares with the pause screen and the briefing.

## The Instant Action screens are all the same screen

`Loading.zrd` defines 81 dialogs. Grouping the 28 `loading_i*` bodies by content yields five
distinct definitions, and they differ only by the mission-type letter: the environment digit selects
nothing. `loading_i6a` is the one exception, and its whole difference is one number, its `HEAD2`
at x 325 where the other six `a` dialogs put it at 365.
Every Instant Action dialog carries the same three photographs at the same positions, `MP-shotdown`
at `197,157`, `MP-crash` at `197,307` and `mp-dangerzone2` at `197,457`, each centred. The pictures
are authored artwork, not a runtime choice and not a capture of the player's own flying.

Multiplayer is the only family whose pictures vary, and they vary by game mode rather than by map:
the `c` dialogs carry `MP-flagcapture`, `MP-flagreturn`, `MP-shotdown` and `MP-crash`; the `z`
dialogs carry `MP-gasbag`, `MP-cannon`, `MP-torpedo` and `MP-zepdown`; `d` and `t` carry the same
three as Instant Action. Campaign dialogs carry none of those photographs: they use the `loadframe`
background with the parchment objectives list, place their bar at `90,548` with `prog_blkload` over
`prog_redload`, and draw the mission's own chart, flags and icons out of the script decoded below.

What the Instant Action letter does select is the text:

| Letter | String family | Heading |
|---|---|---|
| `a` | `MSG_BRF_IAS_*` | DOGFIGHT AN ACE. |
| `d` | `MSG_BRF_IAT_*` | SQUADRON |
| `s` | `MSG_BRF_IASF_*` | STUNT FLYING |
| `z` | `MSG_BRF_IAZ_*` | ZEPPELIN RUN |

Each family supplies `HEAD1` (always "INSTANT ACTION"), `HEAD2` (the heading above), and `OBJ1` and
`OBJ2`, the blurb and the win condition.

Where each of the four goes is the dialog's own script, and every family places them the same way.
The widget names in the script are `HEAD1`, `HEAD2`, `OBJ5` and `OBJ6`; the strings they bind are
the family's `HEAD1`, `HEAD2`, `OBJ1` and `OBJ2`:

| Widget | At | Font | Wrap |
|---|---|---|---|
| `HEAD1` | 70, 35 | `loadListTitle` | none |
| `HEAD2` | 325, 35 (365 in the `a` family bar `loading_i6a`) | `loadListTitle`, and the empty default face in the `s` family | 400 by 250 |
| `OBJ5` | 360, 135 | `loadListbody` | 400 by 350 |
| `OBJ6` | 360, 215 | `loadListbody` | none |

The empty font is the dialog's own default face, and the stunt screen is the only place on this
board that takes it. `OriginalScreenshots/Instant Action Loading Screen STUNT FLYING.png` shows it
as a bold letterspaced slab beside `HEAD1`'s light sans; `OriginalScreenshots/IA LoadScreen.png`
shows the `d` family's `HEAD2` in `HEAD1`'s own face, which is what its `loadListTitle` says.
Measured off that second shot, whose client area is the authored 800x600 unscaled: `loadListTitle`
sets a 12-pixel cap height on a baseline at y 52, and `loadListbody` a baseline at y 147 with its
wrapped lines 13 pixels apart.

Instant Action and multiplayer screens use the `loadframempt2` and `loadframempt` backgrounds
respectively, both 800x600, with the bar at `564,546` drawing `prog_red` over `prog_blk`. An
animated propeller sits beside it: a bitmap cycle at `506,549` stepping `prp0` to `prp7`, `prp15` to
`prp22` and `prp30` to `prp37` at **6.0 fps**.

## The campaign screen is the mission's chart

The 24 `loading_c<world><mission>` dialogs are one screen with one map swapped in. All 24 author the
same `PRIMITIVES`, `BUTTONS` and background: `loadframe`, a `MAP` at `POSITION [16, 19]`, a
`MEMENTO` at `[533, 326]` whose `momento_temp` bitmap the runtime replaces, and a `PROGRESS` at
`[90, 548]` drawing `prog_blkload` over `prog_redload`. Twelve map bitmaps serve the 24 missions
(`NW-m1MAP`, `HA-m1MAP`, `RM-m1map`, ...), each cropped `CLIP [211, 51]..[784, 551]` bar the four
Manhattan dialogs' `[211, 30]..[784, 530]`, with a per-mission `WORLD` window. How that crop and
that window are read is in [`pause-screen.md`](pause-screen.md), which shares this screen's map
control.

⚠ **A campaign dialog's `LOADING_SCRIPT` is a superset of the `ESC_SCRIPT` its `escape.zrd` twin
carries, so neither file's copy of a dialog stands for the other's.** The `PRIMITIVES` blocks agree
entry for entry across all 24, and so do the flag pins and the memento shadow; the loading script
adds the propeller cycle and the mission's device icons. `loading_c61` (`CM01`) is the smallest
case: 24 beats against the pause dialog's 20, the four extra being the `Cycle`/`On` pair for the
propeller and a `Pict`/`On` pair placing `NW-m1pda_icon` at `[106, 419]`.

The script's vocabulary is the briefing's ([`../formats/briefing.md`](../formats/briefing.md)) plus
one opcode of its own. Censused over the 24 campaign dialogs: `On` 315, `Pict` 180, `Objective` 86,
`Off` 47, `Spin` 46, `Cycle` 24, `ToBack` 24, `Wait` 20, `Line` 1, and nothing else. There is no
`PlaySound` and no `WaitForMarker`, so nothing waits on a narration.

- **`Cycle`** is the propeller, and only this screen's script authors one. Every dialog authors the
  same beat: `SPINNER` at `[435, 535]`, six bitmaps (`prp0`, `prp7`, `prp15`, `prp22`, `prp30`,
  `prp37`) at **6.0 fps**, turned on by the `On` after it. The position is the dialog's own, not the
  Instant Action board's `506,549`.
- **`Objective OBJn index k`** binds a parchment row to entry `k` of the mission's own
  `objectives.zrd` list, 0-based over the keyed `IDENTITY` rows in priority order
  ([`../formats/objectives.md`](../formats/objectives.md)). 86 such beats across the 24 dialogs, and
  a dialog may bind more rows than its mission has objectives, in which case the extra rows have no
  text to show.
- **The memento's shadow** is an ordinary `Pict`: `momento_shad` centred on `[661, 454]`, in all 24.
- **The flag pins** are `OBJPIN1` upward, 71 across the 24 dialogs, each `Pict` naming its own
  bitmap at its own point. ⚠ 19 of the 24 dialogs place their pins past an authored `Wait` of
  `0.1` seconds, always after a `Pict`/`Off`/`Spin` group setting up a device form, so a reader that
  runs the script once and stops draws those charts with no flags at all.
- **The device icons** are `Pict`s too, at points the dialog authors. Five dialogs place
  `NW-m1wv_icon` and four place `singledev`, which are the same bitmaps the shared block gives
  `MYZEP` and `OWNSHIP`. ⚠ Their presence on a chart is not the runtime placing an icon by world
  position: `FUN_004a1910` binds neither widget, so on this screen that art is authored scenery.

## The progress bar is a hand-authored milestone table

The engine does not measure its own load. `FUN_004a2100` takes a literal fraction and a debug label,
and is called at sixteen fixed points:

| Fraction | Label | Call site |
|---|---|---|
| 0.01 | `gModSetVertexShading` | `004a1ddd` |
| 0.02 | `Initialize global path to search zbd dir` | `004a1e08` |
| 0.04 | `zSndOpen(sounds.zrd)` | `004a1e38` |
| 0.07 | `Load Common Sounds` | `004a1e61` |
| 0.10 | `Begin CZMission::Load` | `004653a4` |
| 0.10 | `After zImgOpen()` | `00464005` |
| 0.20 | `InterpFilename` | `004640ee` |
| 0.30 | `Texture Load` | `00464107` |
| 0.40 | `Update database` | `00464125` |
| 0.50 | `Load Mission Sounds` | `00464702` |
| 0.60 | `Mission InterpFilename()` | `00464782` |
| 0.70 | `anim, objectives, weather, & weapons` | `00464824` |
| 0.71 | `StructsCampaignInit` | `0046484c` |
| 0.72 | `StructsMissionInit` | `00464865` |
| 0.80 | `PlayerOpen()` | `00464888` |
| 0.90 | `Finish LoadMissionData` | `00464960` |

The setter is **monotonic**: a fraction below the stored one is ignored, so no step can drag the bar
backwards, and the duplicated `0.10` is harmless because the comparison admits equality. Nothing
ever sets `1.0`. The highest milestone is `0.90`, and the screen is torn down from there, so a full
bar is never drawn.

The repaint at `0x005c3d40` clamps the fraction to `[0, 1]` and sets the fill to
**`floor(fillBitmapWidth * fraction)`** pixels of the fill bitmap drawn over the base one, left to
right. It is a pixel clip against the bitmap's own width, not a count of lamps, so the boundary cuts
through a lamp part-way. In `prog_red` the six lamps occupy x `37-54`, `66-83`, `95-112`, `124-140`,
`153-169` and `182-198` of a 236-pixel strip, which puts roughly two lamps lit at the `0.40`
milestone and all six lit at `0.90`.

`FUN_004a1910` also stores `8` in the dialog at `+0x69238`, and `FUN_004a2100` computes
`floor(8 * fraction)` from it. That value does not reach the fill width, which comes from the pixel
clip above; its role is unconfirmed and is most likely a gate so the repaint runs only when the
visible bucket changes.

## How the screen keeps drawing during a blocking load

`FUN_004a18a0` is the redraw pump. It is wall-clock throttled: it draws only when
`GetTickCount() * 0.001` exceeds the last draw's timestamp by more than **0.1 seconds**, so the load
screen repaints at most **ten times per second**.

The load code calls it immediately after each milestone. `FUN_004a1db0` is the pattern at its
clearest: do a unit of work, set the fraction, pump, do the next unit, set the fraction, pump. The
engine therefore neither threads the load nor rebuilds it as an incremental state machine. It runs
the load straight through on one thread and yields a rate-limited repaint from inside it.

## Where CSVM differs

`UI/Screens/LoadScreens.cs` composes the screen above at its authored coordinates and `UI/Screens/LoadBoard.cs`
hangs it over the build, through the campaign boards' own surface (`docs/org/campaign-board.md`):
the chart sheet for a campaign launch, the blackboard with its three centred photographs for
everything else. An Instant Action launch reads `loading_i1<letter>` and writes its four texts at
the positions and wrap widths in the table above; the environment digit is not plumbed, since it
selects nothing bar the `loading_i6a` heading. The multiplayer family has no caller here.

**The campaign sheet is the mission's own dialog and nothing of ours.** `UI/Menu/EscapeDialog.cs`
reads `Loading.zrd` the way it reads `escape.zrd`, `LoadSheet` resolves the launch's dialog key,
its mission's objectives and the profile's memento, and `UI/Overlays/MissionMap.cs` draws the chart at its
source crop with what the script placed on it. A launch takes the dialog its `cm_sequence` position
names, so the chart, the flags, the icons and the parchment rows are the mission being built.
The script is run out rather than played, the way the pause sheet's is, which is what releases the
`Wait` those 19 dialogs place their pins behind.

**The load screen places no ownship and no zeppelin icon**, because its own constructor binds
neither and the pause screen's is the only one that does. The five charts carrying `NW-m1wv_icon`
and the four carrying `singledev` get them from their own script.

**A row on the parchment is never marked here.** The screen stands before the mission it lists has
run, so the objectives list draws its rows and no check.

**Both screens hang the seated profile's own memento.** The cabin's chooser writes the choice into
the profile and `CampaignMementos.BitmapFor` turns it into the drawn bitmap name, so the load
screen, a real pause and the cabin wall all carry the one picture. A session with no profile behind
it draws `ms_p_initialpinup1`, the picture the original's own profile reset seeds.

**The three authored faces meet two of ours.** The extraction ships no menu typeface, so a board
writes in the one the engine has: `loadListTitle` becomes that face at 17 pixels, which puts its
baseline on the authored 52, and `loadListbody` the same face at 13. The empty default face differs
from `loadListTitle` by weight rather than by size, and one typeface cannot carry two weights, so it
is drawn emboldened at `loadListTitle`'s size. Our face also leads wider than the original's, which
runs a three-line blurb through the chalk rule under it, so the body widget carries the authored
13-pixel pitch and the renderer wraps to it rather than to the face's own metrics. Where the words
break is still ours: a wider face takes a wider line.

**Free flight and dogfight are ours rather than the original's**, and no shipped dialog describes
either. Both take the mode's own name at `HEAD1`'s authored place and write nothing else, since the
nearest dialog, the `d` family's, would state a win condition neither mode has.

**The bar fills and the propeller turns from a pump inside the blocking build**, which is the
original's own shape rather than a threaded or incremental load. `Utils/LoadProgress.cs` holds the
sixteen authored fractions in build order and clamps them monotonically the way `FUN_004a2100`
does. `FUN_004a18a0`'s 0.1 s throttle is kept for the case it was written for, a step reported at a
fraction the bar already stood at, which is what holds the propeller to its rate; a step the bar
moved on draws whatever the clock says. That is a deliberate departure: the original's phases are
slow enough that its throttle never swallows a milestone, while ours can cross ten of them inside
one 0.1 s window, and a throttled build shows its first fraction and then the mission.
`UI/Screens/LoadBoard.cs` installs the repaint while it is in the tree and presents through
`ComposedBoardView.PresentMoving`, which issues the fill and the propeller frame to a canvas item of
the view's own and calls `RenderingServer.ForceDraw()`. Going through the Control's own `_Draw` does
not work: `QueueRedraw` defers the callback through the MessageQueue, which only the main loop
flushes, and the build owns that loop until it returns, so a forced frame would present the same
canvas commands every time. `--debug-load[=dir]` stands this screen over a CLI launch and
photographs each presented frame, which is how the bar is read back with nobody at the menu.
The build stays one
synchronous block: `Session/Launch/Launcher.cs`, `Session/Launch/GameSession.cs` and `Mech3/WorldSession.cs`
report each phase boundary they cross, taking the fraction the table authors for it and never one
derived from how long the phase took. The fill is the repaint's own pixel clip,
`floor(fillWidth * fraction)` of `prog_red` over `prog_blk` on the blackboard and `prog_redload`
over `prog_blkload` on the chart sheet; the propeller steps the `Cycle` beat's six bitmaps
(`prp0`, `prp7`, `prp15`, `prp22`, `prp30`, `prp37`) at the authored 6 fps off the wall clock, on
the campaign sheet at the beat's own `435,535`. The highest milestone is 0.90 and the screen is
torn down there, so a full bar is never drawn. Only a launch through `Launcher.BeginLaunch` builds
the board, so the pump exists for the three interactive launches alone and a CLI, scripted or
golden run builds with nothing over it and gains no frame.
