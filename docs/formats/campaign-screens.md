# Campaign screens

Part of the [format documentation](README.md). This page is the behavioural decode of the six GUI
scripts that drive the out-of-mission campaign flow: the player profile screen
(`CAMPAIGN.SCRIPT`), the cabin hub (`PASSENGERCABIN.SCRIPT`), the chapter-intro movie player
(`CAMPAIGNINTRO.SCRIPT`), the flight check screen (`FLIGHTCHECK.SCRIPT`), the plane selection screen
(`PLANESELECTION.SCRIPT`) and the ammo selection
screen (`ORDINANCELAYOUT.SCRIPT`), and of the three that drive the scrapbook (`SCRAPBOOK.SCRIPT`,
`SCRAPBOOKZOOM.SCRIPT` and `SCRAPBOOK_TOC.SCRIPT`). It documents which widget triggers what, how
each list is filled, which engine callback each screen makes, and where every screen transition
goes.

The sources are the scripts in `extracted\rof\ASSETS\SCRIPTS\`, the widget table
`extracted\rof\ASSETS\LAYOUT.CSV`, the `langui` string table
([strings.md](strings.md)), and the `crimson.exe` callback handlers.
[rof.md](rof.md) holds the archive container and the script language's shape;
[instant-action.md](instant-action.md) is the sibling decode of the same machinery for
`INSTANTACTION.SCRIPT` and was the method precedent here.

The briefing screen is not a script: it is a `zrdr` dialog with its own reveal engine, decoded in
[briefing.md](briefing.md). The profile file this flow reads and writes is decoded in
[saved-games.md](saved-games.md), and the mission's objective text comes from the per-mission
choreography in [objectives.md](objectives.md).

## Contents

- [Conceptual model](#conceptual-model)
- [Screen flow](#screen-flow)
- [Player profile: `CAMPAIGN.SCRIPT`](#player-profile-campaignscript)
- [The cabin: `PASSENGERCABIN.SCRIPT`](#the-cabin-passengercabinscript)
- [Chapter intro: `CAMPAIGNINTRO.SCRIPT`](#chapter-intro-campaignintroscript)
- [Flight check: `FLIGHTCHECK.SCRIPT`](#flight-check-flightcheckscript)
- [Plane selection: `PLANESELECTION.SCRIPT`](#plane-selection-planeselectionscript)
- [Ammo selection: `ORDINANCELAYOUT.SCRIPT`](#ammo-selection-ordinancelayoutscript)
- [The scrapbook: `SCRAPBOOK.SCRIPT`, `SCRAPBOOKZOOM.SCRIPT`, `SCRAPBOOK_TOC.SCRIPT`](#the-scrapbook-scrapbookscript-scrapbookzoomscript-scrapbook_tocscript)
  - [The zoom view's sepia](#the-zoom-views-sepia)
  - [Fitting a block to its box](#fitting-a-block-to-its-box)
  - [Export to Desktop](#export-to-desktop)
  - [Entry, exit and the table of contents](#entry-exit-and-the-table-of-contents)
- [Callback reference](#callback-reference)
- [Reader rules and edge cases](#reader-rules-and-edge-cases)
- [Evidence and limits](#evidence-and-limits)

## Conceptual model

A screen is three files acting together.

- **`LAYOUT.CSV`** declares each widget by key: its type letter, its art, its position, its
  `IDS_*` string id, and, for a button, the **script it launches** (the `ScriptToExe` column). A
  large part of the navigation is therefore data, not code: pressing `PC_B_RETURNMM` runs
  `MainMenu` because that name sits in its layout row, and the script's own mailbox never mentions
  it.
- **The `.SCRIPT` file** creates one object per widget key, binds engine callbacks to the lists,
  and handles the messages the widgets send back. Identifiers are obfuscated (single letters,
  `$$X$$` placeholders); widget keys, callback ids and message ids are plaintext, and they carry
  the meaning.
- **`crimson.exe`** answers the callbacks. `FUN_004075d0` registers five named handlers:
  `gosCallback` (`FUN_00407670`), `uiData` (`0x004093a0`), `uiControl` (`FUN_00404960`),
  `gosPageInit` (`0x00403680`) and `gosLocalize` (`FUN_00404630`). The obfuscated first argument
  of `callback(...)` is one of those names, so a script's `callback($$E$$, 2010, -1)` and another's
  `callback($$UB$$, 2141, 0)` are calls into two different handlers. The three these screens use
  are `uiData` (the 2000-range data ids), `gosCallback` (small ids, the campaign verbs) and
  `uiControl` (registry and validation).

**Widget classes.** The script's `@ctl@XX` class names map one to one onto `LAYOUT.CSV`'s type
letters. Established by pairing every widget these six scripts create against its layout row:

| Script class | Layout type | Widget |
|---|---|---|
| `ZJ` | `P` | pane (art, optionally multi-frame) |
| `BE` | `B` | button |
| `PE` | `T` | static text |
| `SJ` | `A` | text list (fixed rows, no scrollbar) |
| `PM` | `D` | dropdown |
| `EN` | `L` | listbox |
| `JN` | `S` | scrolling text |
| `IM` | `E` | edit box |
| `AL` | `M` | movie |
| `SK` | `W` | sound |

**Widget properties** used below: `YC` is the layout key, `AK` an art path override, `CK` the
frame index of a multi-frame pane, `TJ` the `uiData` id a list fills itself from, `WM` the shared
list sub-script, `QG` the selected index, `RM` the rollover index, `UJ` the item count, `BC` the
text buffer, `HD` the text colour. `@globals@AR` is set immediately before a list is created and
restored to 32 after: it is the item-array capacity handed to the shared list script (5 for the
ammo dropdowns, 12 for the rocket dropdowns, 8 for the flight-check lists, 25 for the 24-mission
dropdown), and it is not the visible-row count, which comes from `LAYOUT.CSV`.

**Message ids.** The widget classes in `CTL.SCRIPT` define the vocabulary these screens speak:

| Message | Meaning |
|---|---|
| `10000` / `10018` | disable / enable a widget |
| `10001` / `10002` | pointer left / pointer over |
| `10003` | clicked (the widget plays `mouseclick.wav` itself) |
| `10013` | rollover row changed in a list |
| `10015` | selection changed in a list |
| `10021` | validate an edit box's text (the box calls `uiControl` 3107) |
| `10026` | a list row was double-clicked |
| `11003` / `11004` | pause / resume a sound object (`uiControl` 2503 / 2504) |
| `11006` | a sound or movie object reached its end |

**Music.** `GLOBALS.SCRIPT` creates one sound object, `@globals@RR`, whose wav is
`music_splash.wav` at loop count 5. Every out-of-mission screen drives that one object: the cabin
and the flight check mail it `11004` on entry, the cabin mails `11003` when it is paused and again
when Next Mission is pressed, and the scrapbook screens mail `11003`. No script anywhere assigns a
second track to it, so in the shipped scripts the profile screen, the cabin and the flight check
all play the splash music and nothing else.

## Screen flow

Every transition below is stated by either the button's `ScriptToExe` layout column or the
script's own `script_run` / `script_end` / `mail`, both of which are quoted per row.

| From | Control | Goes to | Stated by |
|---|---|---|---|
| Main menu | (campaign) | `CAMPAIGN.SCRIPT` | outside these five scripts |
| Profile | `CM_B_START`, or Enter in the name box, or a double-click on a roster row | the cabin: `script_end mainmenu`, `script_end campaign`, `script_run passengercabin.script` | script |
| Profile | `CM_B_CANCEL` | `activate(@mainmenu@)`, `script_end campaign.script` | script |
| Profile | `CM_B_DELETEPLAYER` | a confirm messagebox, then stays on the profile screen | script |
| Cabin | `PC_B_NEWMISSION` | the briefing: `script_end passengercabin`, `gosCallback` 6 | script |
| Cabin | `PC_B_PREVIOUS` | `ScrapBook_TOC` | layout |
| Cabin | `PC_B_PLANEX` | `PlaneName` (the plane-construction chain's entry) | layout |
| Cabin | `PC_B_RETURNMM` | `MainMenu` | layout |
| Cabin | `PC_B_CHANGEMOMENTO` | `MomentoSelection` | layout |
| Cabin | `PC_B_SAVE` | `save.script`, but the script deactivates the button at creation | script |
| Cabin (on entry) | chapter not yet started | `script_run campaignintro.script`, `script_pause passengercabin` | script |
| Chapter intro | Esc, Space, Enter or a click | `script_continue passengercabin`, `script_end campaignintro` | script |
| Briefing | GO TO FLIGHT CHECK | `FLIGHTCHECK.SCRIPT` | briefing dialog, outside these scripts |
| Flight check | `FC_B_RETURNBRIEF` | the briefing: `script_end flightcheck`, `gosCallback` 6 | script |
| Flight check | `FC_B_FLYMISSION` | the mission: `gosCallback` 1, `script_end flightcheck.script` | script |
| Flight check | `FC_B_CHANGEPLANE` / `..._CHANGEPLANEW` | `PlaneSelection`, with `@globals@ZQ` set to -1 (pilot) or -2 (wingman) | layout + script |
| Flight check | `FC_B_CHANGEAMMO` / `..._CHANGEAMMOW` | `OrdinanceLayout`, same `ZQ` convention | layout + script |
| Plane selection | `PS_B_ACCEPT` / `PS_B_CANCEL` | `FlightCheck` | layout |
| Ammo | `OL_B_ACCEPT` / `OL_B_CANCEL` | `FlightCheck` | layout |

⚠ **The buttons that carry a `ScriptToExe` name do not appear in their script's mailbox at all.**
`PASSENGERCABIN`'s handler names only Next Mission, Plane Construction (to clear two script
globals), Save and the memento frame; Previous Missions and Return to Main Menu are pure layout
rows. A reimplementation that reads only the scripts will find those two buttons inert.

## Player profile: `CAMPAIGN.SCRIPT`

Widgets: `cm_background` (pane), `cm_e_name` (edit box), `cm_l_players` (listbox),
`cm_b_start`, `cm_b_deleteplayer`, `cm_b_cancel`. The listbox's rows are drawn by the script's own
sub-script `VB`, not by a stock class: it fills the dark red selection bar (`0xff800000`) behind
`parent.QG` and a bright red frame (`0xffff0000`) around the row under the pointer.

**Filling the roster.** `gui_init` calls `gosCallback` 16 (zero the 792-byte roster block, then
enumerate the profile directories), then `uiData` 2099 with the current name to get the selection
index, then re-initializes the list. The list rows come from `uiData` 2102 through the list's `TJ`:
row -1 returns the roster count, any other row returns that profile's name. The roster is
**24 slots of 33 bytes** (a 32-character name plus its NUL) in the executable, which is the
capacity a reimplementation inherits, not an arbitrary limit.

**The name box.** `cm_e_name` is created with `MB.ED = 212`, `MB.KM = 707`, `MB.LM = 1`, its
`JM` (default button) pointed at `cm_b_start`, and is pre-filled from `uiData` 2101 with the last
used name. Message `10021` validates it through `uiControl` 3107, and that validator is strict
about the filesystem, because a profile name becomes a directory name:
`FUN_00406b60` rejects a fixed reserved string, rejects any of `" * / : < > ? \ |`, and then
actually creates and removes a directory of that name under `%TEMP%\RCSA\` to prove the name is
usable. A failed validation raises a messagebox and leaves the screen where it was.

**Starting.** `mail(11004, this)` is the screen's own commit path, reached from the START button,
from Enter in the name box, and from a `10026` double-click on a roster row. It calls `uiData` 2100
(store the typed name as the current profile, returning non-zero when the name changed) and, on a
change, `gosCallback` 9, which resets the in-memory profile (`FUN_004113b0`) and loads the named
one. Then it ends `mainmenu.script` and `campaign.script`, writes the name into the registry under
`UIPlayerName` (`uiControl` 2141 case 0) and runs `passengercabin.script`.

**Deleting.** DELETE PLAYER sets `@globals@OR.VR = 3`, checks the name exists with `uiData` 2099,
raises messagebox `DI = 201` with button mask `0x4`, and on the confirmed result calls `uiData`
2100 and `gosCallback` 17 (`FUN_0041a790`), then clears the name box. `@globals@OR` is the shared
messagebox parameter block declared in `GLOBALS.SCRIPT`: `DI` is the langui id of the prompt, `UR`
the button mask, `WR` an input flag, `VR` the result the box writes back, `TH` a string slot.

⚠ **One branch of this script is unreachable as shipped.** `int TB = 0` guards the
load-a-savegame branch (`script_run load.script`), so it never runs and the following
`if (24 > IB)` roster-full test always takes the true arm. Do not reproduce it as behaviour.

**The pilot name `crashcheat!` is a cheat, not dead code.** The script sets a script global,
accepts the name through `uiData` 2100 and `gosCallback` 9, then re-accepts the previous name
and stays on the screen. `gosCallback` 9 (case 9 of `FUN_00407670`) runs the fresh-profile
initialiser `FUN_004113b0` and then compares the pilot name to `CrashCheat!` case-insensitively,
skipping the new-pilot registration `FUN_004198c0` when it matches. The initialiser's only
branch on a binder flag is `fAllowAll` (`0x00647b5c`), which has no writer in code, so the
script global is that flag: with it set the profile starts with `nPlayerCash = 250000` and plane
slots 2 to 12 filled with the eleven stock airframes, and the airframe thresholds in
`FUN_0040ff00` / `FUN_0040ff50` / `FUN_0040ffa0` are bypassed. The flag is never cleared by a
script, so it holds for the session. The remake builds it on both presentations: the name is read
on the profile screen's own commit path before the validator would refuse its punctuation, so the
wallet fills to 250000, the eleven stock airframes land in slots 2 to 12, every airframe threshold
is bypassed for the rest of the session, and the box goes back to the previous player with the
screen still standing.

## The cabin: `PASSENGERCABIN.SCRIPT`

**The art is flat 2D from the UI archive, not a rendered set.** The screen is
`PC_BackGround.png` at `0,0` with `AlphaType 2` (colour key) at z 4, and `pc_plane` at `46,69`
at z 1 **behind** it: the background painting has a colour-keyed hole where the hangar window is,
and the plane photo shows through it. The photo is chosen at runtime,
`"assets\graphics\pc_p_hangar" + <airframe> + ".jpg"`, where `<airframe>` is `uiData` 2010 for the
pilot slot, so eleven photographs `PC_P_HANGAR0.JPG` .. `PC_P_HANGAR10.JPG` ship, one per airframe
in the `IDS_IA_PLANES` order. `PC_PlaneTemp.png` is only the layout row's placeholder. The rest of
the scene (desk, map, radio, blueprints) is painted into the background.

Two more art layers sit on the desk. `pc_memento` is a pane whose `AK` is
`"assets\graphics\scrapbook\" + <name>`, the name coming from `uiData` 2150, which reads the
profile's current memento file name (the `UIData +0x344` field of
[saved-games.md](saved-games.md)); `pc_frame` is `PC_Mementopicframe.png` drawn over it, and
clicking the frame forwards a `10003` to the CHANGE MEMENTO button. `pc_pin0` .. `pc_pin4` are five
panes sharing `PC_mappins.png` at five fixed map positions, each taking frame index `R`; the script
creates `callback($$A$$, 7, -2)` of them, which is the **story chapter number** of the campaign's
current position (see the callback reference), so the map shows one pin per chapter reached.

**Buttons.** NEXT MISSION, PREVIOUS MISSIONS, PLANE CONSTRUCTION, RETURN TO MAIN MENU, CHANGE
MEMENTO and SAVE GAME are created; SAVE GAME is deactivated immediately, which is why the reference
screenshot shows five. NEXT MISSION is disabled (`mail(10000, ...)`) when `uiData` 2600 returns 0,
which happens only when both the current mission and the completed count have passed 24, that is
when the campaign is finished.

**Next Mission** sets the mission and leaves: normally `uiData` 2104 with -2 (advance to
`completed + 1`, clamped to 24), then `mail(11003, @globals@RR)` to stop the music,
`script_end @passengercabin@`, and `gosCallback` 6 to open the briefing for the selected mission.

**The mission cheat.** The script's own click region is `5,336 to 85,479`; clicking there gives the
script keyboard focus, and typing `idaho` activates the otherwise-deactivated dropdown
`pc_d_missions` (24 rows, filled from `uiData` 2038 with langui `3450 + row`, the long mission
names). With the dropdown used, Next Mission passes `QG + 1` to 2104 instead of -2. The dropdown is
pre-selected to the campaign position, with 24 clamped to row 23. Its `WM` is
`@shareditems@BUA`, the list the plane selection and ammo screens' dropdowns also take, so it
draws exactly as theirs do over the cabin painting: `SHAREDITEMS.SCRIPT`'s `BUA` fills the closed
field `0xffc8d4e6` with a one-pixel frame in black (white while the pointer is over the list) and
prints the pick in `0xff000000`, and its list `CUA` fills the window the same paper with a white
frame, the picked row `0xffa7b9d7` and the row under the pointer `0xffe0e0e0`, every entry in
black. `XTA`/`YTA` (the option pages' dark `0xff221c17` field) and `ZTA`/`AUA` (the hangar tabs'
`0xffdacaa7` paper) are the file's other two list families. The remake builds it, and builds
the same matcher on the table of contents (`ispy`) and the construction hub (`gimme`): the click
arms the screen's own buffer, each typed character extends it, a character that leaves the word's
prefix empties it so a partial retype starts over, and the comparison is case-sensitive.

**Chapter intro.** `@globals@XQ` is set by `CAMPAIGN.SCRIPT` and means "the cabin was entered from
the profile screen". Only then does the cabin ask `uiData` 2151 for a chapter number and, if it is
non-zero, run `campaignintro.script` and pause itself. 2151 returns the chapter number only when
that chapter's first mission has no recorded time in either half of its mission-result record, so
the movie plays once per chapter, on first arrival. When `XQ` is clear the cabin just resumes the
music. Either way the script ends with `gosCallback` 31, which joins the background loader thread
and tears the mission world down.

## The memento chooser: `MOMENTOSELECTION.SCRIPT`

The screen the cabin's CHANGE MEMENTO plaque opens. Creation order is `ms_background`, `ms_glass`,
`ms_memento`, `ms_b_accept`, `ms_b_cancel`, `ms_b_arrowr`, `ms_b_arrowl`. The picture is the same
`"assets\graphics\scrapbook\" + <name>` pane the cabin draws, at the layout's `MS_MEMENTO`
(`184,48`, no authored size, so the 415x515 photograph draws at its own), with `MS_Reflection2.png`
over it at `206,71`. The layout's two arrow rows carry each other's art name: `MS_B_ARROWL` at
`175,257` is drawn with `MS_B_Right.png` and `MS_B_ARROWR` at `578,249` with `MS_B_Left.png`. Both
arrows widen their own click region by 24 pixels.

The name comes from `uiData` 2150 alone. Creation reads it with arg -1 (the profile's current
name); each arrow sets `GX` to 100 (forward) or 101 (back) and re-runs `gui_init`, which reads 2150
again with that argument and gets the next or previous name the profile has been awarded, so the
walk and its wrap are the executable's, not the script's. ACCEPT calls 2150 in write mode
(`callback($$E$$, 2150, 1, FX)`) and then `gosCallback` 12. CANCEL has no mailbox case at all: it
leaves through its layout row's own `PassengerCabin` target, which is why nothing is written.
Two flourishes are the script's own: the first ten frames alternate `10018` and `10000` at both
arrows, and a held left button swaps the picture for `ms_hourglass.png` until it is released. Which
names the profile may reach, and the objective bits that admit them, are in
[../org/pause-screen.md](../org/pause-screen.md).

## Chapter intro: `CAMPAIGNINTRO.SCRIPT`

The whole screen is one movie widget: `cm_movie` with `BL = "chap" + <n> + ".mpg"`, sized to
`getresx()` by `getresy()` and placed one z above the movie's own layer. Escape, Space, Enter or a
left click mails `11006` to itself, and the handler (guarded by a one-shot flag so the end-of-movie
`11006` and a keypress cannot both fire) continues `passengercabin` and ends itself. The chapter
number comes from the same `uiData` 2151 call the cabin makes.

This screen is the M5 plan's out-of-scope MPG playback in its entirety: five files, one skip
gesture, no other behaviour.

## Flight check: `FLIGHTCHECK.SCRIPT`

Widgets, all prefixed `fc_`: `background`, `p_pilotplane` and `p_wingplane` (panes over
`FC_PlaneIcons.png`, frame index = the slot's airframe from `uiData` 2010), `t_title`, `t_mission`,
`t_pilot` / `t_wingman`, `t_pilotplane` / `t_wingmanplane`, `t_objtitle`, `t_objectives`, the four
`t_gunlistp/w` and `t_rocketlistp/w` text lists, and the six buttons.

**The two slots.** `-1` is the pilot and `-2` the wingman throughout the campaign callbacks. On
creation the script calls `uiData` 2014 with `(-1, -3, 0)` and `(-2, -3, 0)`, which resets each
slot's plane override to "use the profile's own selection": `FUN_0040fac0` resolves a slot to its
override, and the sentinel -3 falls through to the profile fields, `UIData +0x33c` for the pilot
and `+0x340` for the wingman.

**Text.** The mission title is `uiData` 2009 with flags 1, which is langui `3450 + mission - 1`,
the long mission name (flags 2 would give the short name at `3480 + mission - 1`). Each slot's
plane line is `uiData` 2011, the saved plane's name followed by its airframe title, which is why
the reference screenshot reads `Gypsy Magic  Hughes P21-J MKIII Devastator` (langui 511 and 512
are the default pilot and wingman plane names a new profile is seeded with). The objectives note is
`uiData` 2022, which asks `gosCallback` 28 for the mission's objective lines and concatenates them;
those lines are the localized `MSG_BRF_*` texts the mission's own `objectives.zrd` `IDENTITY`
blocks name (see [objectives.md](objectives.md)).

**The gun and rocket lists are eight rows each, and the blanks are data.** Both are filled per row
through `TJ` (`2005` and `2006` for the pilot, `2007` and `2008` for the wingman; the pilot and
wingman ids share one handler apiece, so only the slot differs). Row -1 returns the count 8.

- A gun row `r` addresses **gun group `r >> 1`** of the plane record. The group is empty when its
  gun-slot id is 5, and the odd row of a group is drawn only when bit `r >> 1` of record `+0x84` is
  set, so a group holds one or two barrels. The row text is the calibre name plus the group's
  ammunition short name (langui `3360 + ammo`).
- A rocket row `r` addresses **pylon cell `r`** of the record and is empty when the cell holds 11.
  Cells 0 to 3 are one wing and 4 to 7 the other. CSVM departs here on purpose: its loadout
  screens key the boxes by physical pylon number in fill order (`LaunchMenu.AmmoPylons`), so the
  rows agree with the weapon gauge's belt lights, at the cost that a fill order crossing wings can
  draw a starboard pylon in the port box.

**The calibre name is `IDS_GUNSHORTNAME` (langui `3320 + slot`), not the long name the hangar and
the ammo screen use.** `Campaign Flight Check.png` settles it: the row reads `1)  .50-cal. Slug`,
not `1) Barret Arms .50-cal. Slug`, and the 3320 block's strings each lead with a space, which is
the gap the screenshot draws between the row number and the calibre. Nothing is inserted between a
row's number and its text on either list, which is why the rocket rows read `1)High explosive`
tight against their number: `IDS_ROCKETSHORTNAME` carries no leading space of its own.

**The wingman half exists only when the mission has a wingman.** `callback($$A$$, 27)` is
`FUN_0041aa50`, a byte read out of the loaded campaign sequence at stride 0x34, which is
`cm_sequence.zrd`'s own per-entry `wingman` flag ([saved-games.md](saved-games.md)). When it is
clear the script deactivates the wingman's two buttons and never creates the wingman panes, texts
or lists at all.

**Plane change.** Two independent rules disable it, and both are in the shipped build.

- `switch ($$KP$$) case 13: case 17:` calls `uiData` 2021 and deactivates `fc_b_changeplane`.
  `$$KP$$` is the campaign's current mission, 1-based over the 24-entry `cm_sequence`.
- `uiData` 2018 returns the count of owned planes (`FUN_0040fd10` over the 26-slot plane array),
  **minus one when the current mission is 13 or 17**, its own test of `UIData +0x00`. The script
  disables both CHANGE PLANE buttons when that count is below 3.

So the answer to "which missions allow a plane change" is: **all of them except campaign missions
13 and 17**, and additionally none of them while the profile owns fewer than three planes. Mission
13 is `cm_sequence` seq 12, Hollywood mission 3, langui 3462 "Nathan Zachary & The Nefarious Trap";
mission 17 is seq 16, Colorado mission 2, langui 3466 "Nathan Zachary & The Pirate's Duel".

**What 2021 does there is grant the mission's story aircraft.** `FUN_00405f00` walks a table of
20-byte records at `0x0061ae80` for the entry whose first field is the current mission, and its
fourth field is an airframe id (11 meaning none). If the entry's objective field is 0, or that
objective bit is already set in the mission's result record, and the airframe has not been granted
before, it copies the stock airframe record into a new plane slot, names it from langui, and makes
it the selected plane; if it was granted before, it just selects the existing one. The shipped
entries:

| Mission | Objective gate | Payout | Airframe | Plane name |
|---|---|---|---|---|
| 1 | 12 | 900 | none | |
| 2 | 1 | 0 | Balmoral | langui 513 `Jumping Jane` |
| 5 | 1 | 20000 | none | |
| 6 | 3 | 5000 | none | |
| 7 | 1 | 0 | Bloodhawk | langui 514 `Blue Streak` |
| 12 | 1 | 10000 | none | |
| 13 | 0 | 0 | Fury | langui 515 `Red Hot Spender` |
| 17 | 0 | 0 | Autogyro | langui 516 `Minx` |
| 19 | 1 | 5000 | Warhawk | langui 517 `Accipiter Annie` |
| 24 | 1 | 100000 | none | |

Only the two entries with objective gate 0 are unconditional, and those are exactly the two
missions the screen calls 2021 on and forbids a plane change on. The other three aircraft are
awarded on completing a numbered objective, through the mission-completion recorder that reads the
same table for its payouts ([saved-games.md](saved-games.md)).

**FLY MISSION** calls `gosCallback` 1 with argument 1 and ends the script. That op takes one of two
paths on `mission - 1`: `FUN_00417090` when the mission has not been completed before
(`completed < current`), `FUN_00417410` otherwise. Both end in the same builder, which reads the
selected plane record for the pilot and, when the mission has a wingman, the wingman's record, and
registers them under the names `player` and `wingman_1`, the same two section names a
`Persist.NNN` file carries.

## Plane selection: `PLANESELECTION.SCRIPT`

Both CHANGE PLANE buttons reach this screen through their `ScriptToExe` column, and the flight check
sets `@globals@ZQ` to -1 or -2 first. **The plane screen never reads `ZQ`.** It builds the pilot's
block and, where the mission has a wingman, the wingman's block, both at once, so the slot the
flight check recorded matters only to the ammo screen. ACCEPT and CANCEL each carry `FlightCheck` in
their own `ScriptToExe` with the end-script flag set, so either way out returns to the check.

**Widgets**, from `[@PlaneSelection@]` with the section's own `GX=553`, `V3=444`, `INFOX=430` and
`ITEMSDISPLAYED=13` and the global `STDITEMH=15` and `STDTEXTH=16` substituted. Layout keys match
case-insensitively: the layout spells one key `PS_B_EXPORTw` and the script asks for
`ps_b_exportw`.

| Key | Type | Position | Size or art |
|---|---|---|---|
| `PS_BACKGROUND` | pane | 0,0 | `PS_BackGround.jpg` |
| `PS_P_PILOTPLANE` / `PS_P_WINGPLANE` | pane | 444,138 / 444,356 | `FC_PlaneIcons.png`, 12 frames |
| `PS_T_TITLE` | text | 132,36 | 190x24, `IDS_PS_TITLE` (`PLANE SELECTION`) |
| `PS_T_MISSIONINFO` | text | 136,70 | 500x16 |
| `PS_T_PILOT` / `PS_T_WINGMAN` | text | 138,102 / 138,320 | 94x20, `IDS_FC_PILOT` / `IDS_FC_WINGMAN` |
| `PS_T_PILOTPLANE` / `PS_T_WINGPLANE` | text | 236,106 / 236,323 | 400x16 |
| `PS_D_PILOTPLANE` / `PS_D_WINGPLANE` | dropdown | 138,132 / 138,350 | 271 wide, 13 rows of 15 |
| `PS_T_TOPSPEEDP`, `ARMORP`, `AGILITYP`, `OFFENSEP` | text | 430, 228 / 245 / 262 / 279 | 140x16 |
| `PS_T_TOPSPEEDW`, `ARMORW`, `AGILITYW`, `OFFENSEW` | text | 430, 446 / 463 / 480 / 497 | 140x16 |
| `PS_A_PLANEWEAPONSP` / `..._PLANEWEAPONSW` | text list | 578,228 / 578,446 | 160 wide, item spacing 1 |
| `PS_B_EXPORTP` / `PS_B_EXPORTw` | button | 560,168 / 560,385 | `FC_B_PaperButton.png`, `IDS_PS_B_EXPORT` |
| `PS_B_SELLP` / `PS_B_SELLW` | button | 560,158 / 560,375 | `FC_B_PaperButton.png`, `IDS_PS_B_SELL` |
| `PS_B_ACCEPT` / `PS_B_CANCEL` | button | 341,553 / 551,553 | `PS_B_AcceptSelections.png` / `PS_B_CancelSelections.png` |

**Both SELL buttons are deactivated unconditionally.** `gui_create` ends with `deactivate(IS)` and
`deactivate(UOA)`, which are `ps_b_sellp` and `ps_b_sellw`, outside every `if` in the script, so no
build reaches the sell path and the reference screenshots show only Export in each slot. Each pair
is authored ten pixels apart at the same x (`560,158` against `560,168`), which is close enough that
the two were never meant to be drawn together.

**The two blocks.** The pilot's block is suppressed on the same two missions the flight check
forbids a plane change on: `switch ($$KP$$) case 13: case 17:` clears the local the script gates it
with, and the else arm deactivates the pilot's dropdown, its SELL and its EXPORT and creates none of
its panes, texts or list. The wingman's block is gated on `gosCallback` 27, the mission's wingman
flag, exactly as on the flight check, and its absence deactivates the wingman's dropdown and its two
buttons.

**The dropdowns fill from `uiData` 2018**, the same id the flight check queries for the owned plane
count: both scripts call it as `int JQ = -1; callback($$E$$, 2018, JQ)`, which is the row -1 count
answer of a list fill, and this screen additionally binds it as the dropdown's `TJ`. `@globals@AR`
is 26 around the two dropdowns, which is the size of the profile's plane array, and 5 around the two
weapons lists. Each dropdown takes the shared list sub-script in `WM` and its selected index in
`QG`. What a row says is not in the script, and the open list in
`Campaign Flight Check Change Plane Combo Box.png` reads the plane's name, a hyphen and the
airframe's short title (`Gypsy Magic - Devastator`), while `PS_T_PILOTPLANE` above it is `uiData`
2011, the same name with the full airframe title the flight check draws
(`Gypsy Magic  Hughes P21-J MKIII Devastator`).

**Each block is filled per slot on `gui_init`**, and refilled by `initialize(this)` after every
change: `uiData` 2010 for the silhouette's frame index, 2011 for the plane line, 2015 with flags
`0x20` for four strings, and `TJ` 2019 (pilot) or 2020 (wingman) for the weapons list. The mission
line is `uiData` 2009 with flags 1, the long mission name at langui `3450 + mission - 1`, which is
the `Hollywood - The Great Plane Robbery` the reference screenshots carry.

**The four rating strings carry their own captions.** 2015's four out-strings are assigned in widget
order to `TOPSPEED`, `ARMOR`, `AGILITY` and `OFFENSE`, and no separate label widget exists beside
them, so `TOP SPEED:  Average` is one string in one 140-wide box. The values are the five-word run
at langui 501 to 505, `IDS_QUALITY`: `Poor`, `Fair`, `Average`, `Good`, `Excellent`. All four words
are the native side's own arithmetic over the plane record, top speed off the engine's power rating
and offense off what the armament weighs; the four formulas are in
[`../org/hangar.md`](../org/hangar.md), "The four rating words".

**A rollover previews the whole block.** Message `10013` from either dropdown calls `uiData` 2014
with the slot, the rollover row `RM` and 0, which is the plane-override write the flight check
clears with the sentinel -3 on entry. The screen then redraws, so pointing at a row moves that
slot's silhouette, plane line, ratings and weapons list while the closed field still shows the
committed pick. Message `10001`, the pointer leaving, re-pushes both slots' working picks through
`uiData` 2013 and redraws, which is what puts the preview back; the cancel guard `EC` suppresses
that.

**The duplicate rule is enforced on the pick, and the revert is the script's own.** On `10015` the
script identifies the sender as the pilot's or the wingman's dropdown and permits the change when
`sender.QG` differs from the other dropdown's `QG`, or when either block is absent. Permitted, it
stores the pick in its `US[slot]` working copy. Refused, it sets `@globals@OR.DI` to 710
(`IDS_PS_CANTFLYSAMEPLANE`, `Pilot and Wingman must fly different planes.`), runs
`messagebox.script`, and only then assigns `sender.QG = US[slot]` and mails `10016` at the dropdown,
so the offending pick is still displayed under the box and the revert lands as the box closes, which
is what `Campaign Flight Check Change Plane Unique Warning.png` shows. Either way it finishes with
`uiData` 2013 for that slot with `sender.QG` and redraws.

**EXPORT is `gosCallback` 22 with the slot**, followed by `uiData` 2105 writing the message text into
`@globals@OR.TH` and the same message box. The text is langui 702, `IDS_PS_EXPORTPLANE`, `Your
%1!s! has been exported and is now available for Multiplayer and Instant Action missions.`, and
`Campaign Flight Check Change Plane Export dialog.png` fills `%1!s!` with `Devastator`, the airframe
title, not the plane's name. The script does not set `OR.DI` here: 2105 formats the string with its
argument and hands over the finished text.

**CANCEL restores, ACCEPT saves.** CANCEL sets `EC` and calls `uiData` 2013 for each present slot
with `XOA[0]` and `XOA[1]`, the picks read from `uiData` 2012 when the screen was created, so the
screen edits the live selection and puts it back on the way out. ACCEPT is `gosCallback` 12 with 0,
the profile write, which is the same op the ammo screen's ACCEPT ends on.

**The message box these three paths share.** `MESSAGEBOX.SCRIPT` reads four fields of
`@globals@OR`: `DI` is a langui id it fetches through `gosLocalize` into the message text, or 0 to
use the text already in `TH`; `WR` picks the widget prefix, `mp_` when it is set and otherwise `mb_`
or, when `XR` is set, `ma_`, and the plane screen clears `WR` before every box so it gets `mb_`; the
low nibble of `UR` picks the button set, `0x1` being the single centred OK that the refusal and the
export message both ask for and `0x4` a two-button box; and `VR` carries the answer back, 1 from the
OK button and 3 or 4 from the two-button pair. The icon frame follows the same nibble, and follows
it rather than the button count: `MESSAGEBOX.SCRIPT`'s `gui_create` switches on `UR & 0x0f` and
gives `0x4` and `0x8` frame 0, the question mark the confirms draw, and every other mask frame 1,
the exclamation mark both reference dialogs draw. `0x2` is the case that rules the count out, a
two-button box that still takes frame 1. Inside the default arm alone, a set `XR` takes frame 2
instead, the skull, which only the credits box asks for.

**The sell path is decoded but unreachable.** Behind the deactivated buttons, `10003` asks `uiData`
2018 for the plane count and refuses below three with langui 701 (`IDS_PS_CANTSELLPLANE`); otherwise
`uiData` 2103 writes the text and its answer picks the button set, a two-button box when it is
non-zero and a single OK when it is not. Langui 700 (`IDS_PS_QUERYSELL`, `Your %1!s! is worth
<B>$%2!d!<b>.  Are you sure you want to sell it?`) and 704 (`IDS_PS_SPECIALPLANE`, `This %1!s!,
%2!s!, cannot be sold.`) are the two strings that fit those two shapes. An answer of 3, the
two-button box's left button, calls `uiData` 2109, resets both slots' overrides through 2014,
re-reads both picks through 2012 and redraws. Nothing in the shipped build can enter it.

## Ammo selection: `ORDINANCELAYOUT.SCRIPT`

The screen serves two callers. `@globals@XQ` is read once into a local (`DKA`) and cleared: 0 means
the campaign flight check opened it, non-zero means Instant Action did. `@globals@ZQ` carries the
slot (-1 pilot, -2 wingman). `uiData` 2035 then copies the addressed plane record into a working
copy, `uiData` 2034 copies the working copy back on ACCEPT, and the `DKA` flag decides which record
is the destination: the profile's plane on the campaign path, a scratch record on the Instant
Action path. CANCEL simply leaves, and on the Instant Action path additionally continues
`instantaction`. On the campaign path ACCEPT also calls `gosCallback` 12 with 0, which writes the
profile out.

**Widgets.** `ol_background`, `ol_p_planetopicon` and `ol_p_planefrticon` (both taking the
airframe index from `uiData` 2010 as their frame), four `ol_t_gunname<N>` labels, four
`ol_d_ammo<N>` dropdowns (5 rows), eight `ol_d_rockets<N>` dropdowns (12 rows), two scrolling text
panes `ol_s_ammodesc` and `ol_s_rocketdesc` under the headings `ol_t_ammodesctitle` and
`ol_t_rocketdesctitle`, `ol_t_planeinfo` (`uiData` 2011), ACCEPT and CANCEL.

**Ammunition is per gun group, not per barrel.** `uiData` 2030 returns group `N`'s gun-slot id and
writes the group's calibre label and its current ammunition index. A group whose id is 5 has no gun:
the script greys its label to `0xff808080` and deactivates its dropdown, which is the greyed
fourth row visible in the reference screenshot. `uiData` 2036 writes a new pick into the working
copy.

**Ordnance is per pylon, and a pylon the aeroplane does not carry is deactivated.** `uiData` 2031
returns pylon `N`'s current ordnance, or -1 when `N` is past that wing's hardpoint count (cells 0
to 3 are checked against record `+0x34`, cells 4 to 7 against `+0x38` plus four). On -1 the script
parks the field's selection on row 0, initialises it like any other, and then deactivates it.
`uiData` 2037 writes the pick.

**⚠ A field this screen deactivates is not drawn at all.** The row is hidden, not greyed and not
left blank in its box. `Ammo Selector Hoplite.png` is the reference: a 1/1 record whose airframe's
own fit is wider, and each ROCKETS box holds a single field at its top slot with the three slots
below it bare paper, no box, no arrow and no words. `Ammo Selector Balmoral.png` is the 4/4
counterpart carrying all four. The empty gun group is the same act read through a second widget,
since the script greys `ol_t_gunname<N>` to `0xff808080` *and* deactivates `ol_d_ammo<N>`: the
Hoplite shot shows three greyed `No Gun` captions with no field under any of them, so what survives
a deactivation is the caption beside it, never the field. The rows that remain do not close up into
the gap either, because every `OL_D_ROCKETS<N>` carries its own `LAYOUT.CSV` rect (y 320, 348, 376
and 404, cells 0 to 3 anchored in `<V3>` and 4 to 7 in `<V4>`), so a hidden row leaves its slot
empty and every other row stays where the layout put it. `CAP-50.mkv` t=80.0 shows the gun half
again on a stock Warhawk (two `No Gun` captions, no field); that film's two aircraft both carry four
pylons a wing, so the rocket half rests on the Hoplite shot and the script. The Instant Action
page's blank boxes are a different act, a `10000` disable rather than a `deactivate`
([instant-action.md](instant-action.md#screen-controls)), and say nothing about this screen.

**⚠ A rocket dropdown's row index is not the ordnance id.** The rocket list is a table of twelve
8-byte records at `0x00619efc`; an entry is offered only when its first field is at most
`0x0064b678 + 1`, so the list grows as the campaign progresses, and the row index is a
position in the filtered list. `FUN_0040ff50` maps a row to the table index and
`FUN_0040ffa0` maps back; the ammunition list has no such filter and its row index is the
ammunition index directly. Three executable flags (`0x00647b5c`, `0x00647b68`, `0x00647b6c`)
bypass the filter and offer all twelve. The first is `fAllowAll`, the unlock-everything mode the
hidden pilot name sets, which switches off the airframe availability thresholds in the same way
([`org/hangar.md`](../org/hangar.md)); the other two this decode did not identify, and the Instant
Action and multiplayer paths are their likely users.

**⚠ The filter reads the profile's progress, not the mission being flown.** `0x0064b678` is the
save's `UIData +0x338`, the completed-mission count
([saved-games.md](saved-games.md#where-the-ammunition-and-ordnance-picks-live) for the record the
picks live in), and the mission wrap-up `FUN_00405ce0` only ever raises it to the flown mission's
index. Replaying an earlier mission therefore leaves it where the furthest mission put it, and the
pylon lists keep every row the pilot has already earned. The airframe list on the plane
construction screen is filtered against the same global by `FUN_00410120`, so one counter governs
both screens and neither one consults the sortie.

| Row | Ordnance | Available from mission |
|---|---|---|
| 0 | Armor-piercing rockets | 1 |
| 1 | High-explosive rockets | 1 |
| 2 | Flak rockets | 2 |
| 3 | Sonic rockets | 8 |
| 4 | Flash rockets | 7 |
| 5 | Rear flash rockets | 12 |
| 6 | Smoke screen | 7 |
| 7 | Choker rockets | 7 |
| 8 | Beeper rockets | 17 |
| 9 | Seeker rockets | 17 |
| 10 | Aerial torpedoes | 20 |
| 11 | None | 1 |

The second dword of each record (4, 4, 2, 2, 2, 2, 2, 4, 4, 1, 0, 0 in row order) is read by
nothing this decode found; it has no cross-reference in the executable.

**The table index is what a plane record stores**, and
[saved-games.md](saved-games.md#where-the-ammunition-and-ordnance-picks-live) carries the mapping
from it to the `wep_*` the pylon actually fires.

**The description strings.** Both description panes are two langui strings, a title and a body,
concatenated with a separator. All four blocks are contiguous and in list order:

| Block | Base id | Symbol |
|---|---|---|
| Ammunition list rows | 3360 | `IDS_AMMOSHORTNAME` (Slug, Dum-dum, Armor-piercing, Explosive, None) |
| Ammunition description title | 3350 | `IDS_AMMOLONGNAME` |
| Ammunition description body | 3370 | `IDS_AMMODESCRIPTION` |
| Rocket list rows | 3395 | `IDS_ROCKETSHORTNAME` |
| Rocket description title | 3380 | `IDS_ROCKETLONGNAME` |
| Rocket description body | 3410 | `IDS_ROCKETDESCRIPTION` |

The index into each block is the ammunition index (0 to 4) or the rocket **table** index (0 to 11),
never the dropdown row. `IDS_AMMOABBRNAME` at 3365 is a third ammunition spelling this screen does
not use. The description panes update on `10015` (a pick), on `10013` (the pointer moving over a
row of an open dropdown) and on `10001` (the pointer leaving, which restores the current pick), so
the panel previews what the pointer is over.

**Both panes are filled at once, and each follows only its own half.** `gui_init` writes the first
armed gun group's ammunition into `ol_s_ammodesc` through `uiData` 2032 and the first fitted pylon's
ordnance into `ol_s_rocketdesc` through 2033, taking index 4 when the plane carries no gun or no
hardpoint. Every later update is dispatched on the sender's `2027` class: an ammunition dropdown
writes the upper pane and a rocket dropdown the lower, neither ever touching the other. So the
screen reads a gun's ammunition and a pylon's ordnance side by side, and the half the pointer is
not in keeps the last pick it described.

## The scrapbook: `SCRAPBOOK.SCRIPT`, `SCRAPBOOKZOOM.SCRIPT`, `SCRAPBOOK_TOC.SCRIPT`

The scrapbook is the two-page book a finished campaign mission ends on. Three scripts share it:
`SCRAPBOOK.SCRIPT` draws one spread, `SCRAPBOOKZOOM.SCRIPT` is the detail view of a single scrap,
and `SCRAPBOOK_TOC.SCRIPT` is the mission list behind VIEW ALL MISSIONS, sized for 25 rows and
filled from the campaign position by `uiData` 2409. The three hand off
by pausing rather than ending, so a spread survives a trip into a scrap or the table of contents
and back. **The cabin's PREVIOUS MISSIONS button does not open the book**: `PC_B_PREVIOUS`'s
`ScriptToExe` is `ScrapBook_TOC`, so it opens the table of contents, and the book is reached only
by picking a row there or by a mission ending. The mission-end pass that fills the results record
is [`org/debrief.md`](../org/debrief.md); the record itself is [saved-games.md](saved-games.md),
"The mission-result array".

**None of the composition is in the scripts.** `SCRAPBOOK.SCRIPT` creates 32 empty pane slots, 32
empty text slots and 11 kill-stamp slots, then asks the engine, item by item, what to put in them.
What each page holds is a fourth data file, `extracted\rof\ASSETS\SCRAPBOOK.CSV`, and the widget
geometry is `LAYOUT.CSV` as for every other screen.

### `SCRAPBOOK.CSV`

One `[SCRAPBOOK]` section of 461 rows, keyed `<mission>_<spread>_<item>` and read by
`FUN_004061d0(mission, spread, item, existsOnly, forZoom)`, which looks the key up and splits the
value on commas into engine globals the script then reads back. The file carries its own column
header as a comment, and the handler agrees with it field for field:

| # | Column | Meaning |
|---|---|---|
| 0 | `Objective` | visibility gate, below |
| 1 | `ResourceID` | langui id of the zoom **caption** |
| 2 | `ImageName` | art file, without extension |
| 3 | `ImageType` | two letters: page extension, then zoom extension |
| 4, 5 | `X`, `Y` | position of the scrap on the 800×600 spread |
| 6 | `Alpha` | alpha type for an image; for a text item, an `%x` ARGB colour |
| 7, 8 | `Width`, `Height` | unused in the shipped rows, every one is 0 |
| 9 | `DrawOrder` | z, written back one higher, and 1000 higher again for a text item |
| 10 | `"Left,Top,Right,Bottom"` | the clickable region, one quoted field |
| 11 | `Zoom` | letter `A` to `Z` selecting a zoom layout family, or `0` for no zoom |
| 12, 13 | `ZoomX`, `ZoomY` | where the inset image sits in the zoom view |
| 14, 15 | `TitleResID`, `TextResID` | langui ids of the zoom title and body |

Item numbers are contiguous from 1 in all 47 populated spreads, which the enumerator requires: it
walks upwards and stops at the first missing key.

**Mission slots and spreads.** Slots run 0 to 24, slot 0 being the not-yet-started career. Spreads
are numbered from 1, and **a mission has one, two or three of them**, not always two: 8 missions
stop at one spread (3, 4, 5, 6, 10, 14, 15, 20), 10 have two, and 6 have three (16, 17, 19, 21, 23,
24). Spread 1 carries 2 to 10 items, spread 2 carries 5 to 24, spread 3 carries 12 to 24. Nothing
declares a count anywhere: `uiData` 2402's next-page helper `FUN_00406170` probes item 1 of the
following spread and, when the key is absent, rolls to spread 1 of the next mission, so the book's
extent is exactly the file's extent.

**Spread 1 is the results page, on a mission slot.** The script activates the stat card, the two
tabs, the kill stamps and the results rows only when `uiData` 2403, the current spread, equals 1
and a mission is open (`1 == callback(2403) && FRA`); the remaining spreads are story pages and
show scraps alone. Both kinds draw scraps, so the results page is a story page with the card laid
over its right half. Slot 0 fails the second half of that gate, so the career page's own spread 1
is a story page: ten scraps and the title, with no card, no tabs and no stamps.

### Resolving a row to a file

⚠ **Whether a row opens at all is the `Zoom` column alone (`!= 0`), not `ImageType`'s second
letter.** `ImageType`'s letters select extensions independently (`B` gives `.BMP`, `J` gives
`.JPG`, anything else `.PNG`), the same three-way rule applying to the page image (first letter)
and, when the row opens, the zoom inset (second letter). An earlier reading of this file blamed the
second letter for the open/closed split itself; it does not survive the data. `1_1_3`
(`SB_01_02_mag2`) ships `ImageType` `P0` and a real `Zoom` letter (`M`), and opens. What actually
splits the 461 rows: the 167 `DZ_generic_corners` photo-corner mounts all carry `Zoom=0` and never
open; the other 294 (167 captures, 127 ordinary scraps) all carry a real family letter and always
do. `ImageType` itself is `P0` (245: 161 of the mounts plus 84 ordinary scraps), `PP` (167, every
capture), `PJ` (43, ordinary scraps whose zoom inset is a distinct `.JPG`) or blank (6, the
remaining mounts, both letters absent). The page image is `Scrapbook\` plus the name, which the
script prefixes with `assets\graphics\`; the zoom background is
`Assets\Graphics\ScrapBook\SB_BG_<Zoom>.jpg` and the zoom's inset image is `Assets\Graphics\` plus
the name under its own (second-letter) extension. All 294 page images, all 43 `.JPG` zoom insets
and all 26 `SB_BG_*.jpg` backgrounds are present in the shipped install.

⚠ **A second letter `0` names no inset at all, not a `.PNG` one.** `FUN_004061d0` switches on that
letter and its `'0'` case stores an empty extension to `DAT_0064b32c`; the zoom's setup at
`0x0040a99c` leaves the inset name empty when that extension is, and `SCRAPBOOKZOOM.SCRIPT` then
creates no `sbz_image` and deactivates `sbz_b_export`. So every `P0` scrap that opens is the family
background and its words alone, with RETURN and no EXPORT TO DESKTOP, which the original's capture
shows on the Aloha Daily, the letter, the diary and the Bristol spec sheet. The "newspaper header"
such a zoom shows is the family background itself (`SB_BG_B.jpg` carries the Aloha Daily masthead),
not the page image, which is a picture of the clipping and never drawn in the zoom.

The `Zoom` letter also names the text layout: `SBZ_T_TITLE<letter>`, `SBZ_T_CAPTION<letter>` and
`SBZ_T_TEXT<letter>` in `LAYOUT.CSV` give each family its own box, colour and justification, 26
families in all. Two of those rows carry typos the engine will not parse as colours,
`xff000000` in `SBZ_T_CAPTIONA` and `oxff1E283C` throughout family `J`.

### The `Objective` gate

A row with `Objective` 0 always draws. Any other value first requires bit 0 of the mission's
**merged best-to-date** completion mask, meaning the mission has been won at least once; a positive
value then requires that bit of the same mask, and **a negative value requires that bit to be
clear**, so a scrap can be authored for having failed a secondary objective. Three rows use this
(`-11` and `-12`). The gate is skipped entirely while the unlock flag at `0x00647b80` is set. A
mission is readable at all only up to the campaign's current position, which is what leaves an
unreached slot showing langui 1219 `Not yet flown`.

### The danger-zone slot

A scrap whose name begins `Snap_` is a player capture, not shipped art: it resolves against the
profile directory instead of `assets\graphics\`, is skipped when the file is not on disk, and is
drawn into a fixed 164×123 rectangle on the page and at its written size in the zoom, that written
size being the 640×480 every file the retail game wrote is. 167 of the 461 rows
are these, named `Snap_<mission>_<objective>` and gated on that objective, with a
`DZ_generic_corners` row at identical coordinates one step higher in draw order supplying the
photo-corner mount.

**Which flight writes one.** The objective half of the name is the mission's own
`dzones.zrd` `objective_numbers` entry for a danger zone (`docs/formats/missions.md`, "Zone
overrides"), so the zone the pilot flies through and the row that draws its photograph are named by
the same data. Completing that zone on the campaign path photographs the pilot's aircraft (below)
into the seated profile's directory under a `.PN_` pending name; the mission end keeps the pending files as
`Snap_<mission>_<objective>.PNG` on a win and deletes them on a loss, the original's own sweep over
ids 10 to 31. A zone in the mission's `nosnapshot` list scores its objective and writes nothing.
`CampaignSnapshot` is the writer and `CampaignDangerZones` the binding; the stunt camera is a
different thing entirely and keeps its own names under `screenshots/stunts/`, since an Instant
Action run has no mission slot and no objective to be named by. Both take the same photograph.

**The camera.** [Evidence: decoded] The photograph is not the pilot's view. The zone update
`FUN_00446990`, on a zone completing whose record has the snapshot flag `+0x49` set and an
objective `+0x4c` of 0 or more, stores the objective in `DAT_00623ccc` (`0x00446ac5`), plays the
sting and raises `DAT_0064fb78` (`0x00446ae1`). The next main tick, `FUN_004a0220` from
`0x004a0372`, takes the photograph when that flag is up, the game is not a network game
(`FUN_00440ad0`), a player exists and the player is not in a cutscene (`+0x91d`). It saves the
camera's mode, FOV, position and orientation, then for one frame:

- sets the camera mode to chase (`+0x14c` = 0, `0x004a0400`), so the `cockpit1` interior is not
  drawn and the airframe is not hidden;
- places the eye at the aircraft position plus `2.5 × dist` along the nose's level heading (the
  backward row `+0x198` with y zeroed, normalised by `FUN_00422690`, times `dist` and the double
  -2.5 at `0x00608348`), where `dist` is the airframe's `camparam` chase distance, plus a uniform
  `rand()` scatter of up to ±0.15 `dist` on world X and Z and ±0.25 `dist` on Y. A nose pointing
  straight up or down has no level heading and leaves the eye on the scatter alone;
- turns the eye to look at the aircraft with no roll (`FUN_0053de20`: a yaw and a pitch off the two
  positions), set through `FUN_004d2710` and `FUN_004d2490`;
- sets the FOV to 60° (`FUN_0042b570(0x3f860a92)`), turns the cockpit and HUD chrome off
  (`FUN_00455800(0)`), and draws the player model and `cockpit1` opaque (`FUN_0049cc00`);
- on the hardware renderer, raises the sun's ambient scalar to `SUNLIGHT_AMBIENT × 1.5 + 0.1`
  while the player model draws (`FUN_004ccfd0`, `FUN_004cd060`; "The fill light" below);
- renders without the screen tint (`FUN_0049fe70(0)`) and saves the back buffer through
  `FUN_005a9800` as `<profile>\Snap_<mission>_<objective>.PN_`.

It then restores every saved setting, clears the flag (`0x004a0979`) and renders the normal frame
over the same back buffer before the flip, so the player never sees the pose. The result is a
head-on portrait of the aircraft from ahead of it, at the external field of view, with no HUD.
Because the eye stays level with the aircraft, a photograph taken in level flight shows the nose
and both wings coming at the camera, a bank shows as a tilted wing line, and one taken in a steep
dive shows the upper surface with the nose toward the bottom of the frame and the tail at the top.

**When it can fire.** [Evidence: decoded] There is no separate camera test. The trigger is inside
`FUN_00446990`'s completion branch (more than one gate passed, `0x004469dc`), behind the zone's
completed byte `+0x40`: tested at `0x00446aaa` before the completed-zones counter, and set at
`0x00446ae8` whether or not a photograph was taken. A zone therefore photographs at most once, on
its first completion, and the last photograph a run can take is the one its last zone's
completion takes; nothing photographs after the run completes. The zone list's builder
`FUN_004459f0` seeds every record with `+0x48` and `+0x49` set and `+0x4c` at -1, so only a zone
that `objective_numbers` gives a slot photographs at all. The remake's stunt camera latches on
the `dzN` marker sphere rather than the gate pair, and ends where the original does: its last test
is on the frame the run completes, and a marker first entered after that frame is not
photographed.

The remake takes the same pose in `DangerZonePhotograph`, a viewport of its own that shares the
pane's world, so the pane is never moved. A pilot in a first-person view has the hidden airframe
groups shown for that one frame on a visual layer no pane draws, and the fill light below lights
the pilot's own aircraft for that frame. A run that draws no frames (headless) photographs the pane
instead. The original's back buffer was 4:3 at every resolution it ran at, so its file is that
buffer scaled; a pane here can be any shape, and the remake writes the centred 4:3 window of it at
the pane's full height, which is what a 640×480 screen framed.

Authoring gaps stay as they are: CM18 numbers a zone 19 with no row for it, CM19 numbers one 31
with no row, CM23 ships rows 27 and 28 for zones it disables, and every `_31` row is dead because
the debrief's mask loop stops at 30 (`docs/org/debrief.md`).

#### The fill light

[Evidence: decoded] The fill is not a new light. It is the per-node ambient override every
`Object3d` node carries in the flag word `+0x2c`: bit 7 enables it (`FUN_004ccfd0` sets it,
`FUN_004cd020` reads it) and bits 9 to 16 hold its level as a byte (`FUN_004cd060` writes it,
`FUN_004cd0b0` reads it). The script commands `NodeSetOverrideAmbient` and
`NodeSetOverrideAmbientValue` (the latter's value times 255) drive the same two setters, and
`NodeSetOverrideDiffuse` is the matching diffuse override (`FUN_004cd0f0`), which the photograph
does not use.

`FUN_004a0220` sets it on the player's node (`player+0xc`) from `0x004a0593` to `0x004a0649`,
only when the hardware renderer flag `DAT_009be708` is up and the sunlight node `DAT_0071c2b8` (the
one the zone apply `FUN_00472ea0` writes) has its class data. It saves the node's flag and byte,
reads the sun's ambient scalar `A` (class data `+0xa0`, the zone's `SUNLIGHT_AMBIENT`), enables
the override and stores `ftol((A × 1.5 + 0.1) × 255 + 0.5)` clamped to 0..255 (the constants at
`0x00603460`, `0x006034a8`, `0x0060414c`, `0x006032e0`; `ftol` is `0x005f6e60`). After the save it
puts the old flag and byte back (`0x004a08e3` to `0x004a0909`).

The consumer is the `Object3d` draw `FUN_004d39c0`. For a node with the override set, when the
gathered directional light (`FUN_0056bc40`, the sun) exists, it saves that light's ambient scalar
and `DAT_0071ea14`, sets `DAT_0071ea14` to 1, sets the ambient scalar to `byte × 0.003921569`
through `FUN_004dbce0` (which recomputes the light's premultiplied ambient and combined colour
triples from its own colours), draws the node's model and every child, and restores both. The
whole aircraft subtree therefore draws with the sun's ambient half raised to the fill and its
direction, colour and diffuse half unchanged. C1's day `A` of 0.25 draws at 121/255, C5's 0.5 at
217/255, and any `A` from about 0.6 up saturates at 1. The software renderer takes no fill. What
`DAT_0071ea14` gates is not decoded.

The remake's faithful aircraft shader takes the fill as a second ambient triple,
`csky_sun_fill_rgb` (`WeatherRig.PhotographFill`, the same colour rule as the ambient half), in
place of `csky_sun_ambient_rgb` when its instance's `csky_photo_eye` is armed and the drawing
camera stands within `SceneBuilder.PhotoEyeReach` of that eye. `DangerZonePhotograph` arms its own
pilot's instances with its eye for the frame it draws and disarms them after that draw, so the
pane drawing the same aircraft on the same frame, and every later frame, keeps the zone's ambient.
Enhanced mode's lit aircraft takes no fill.

### The grime

`uiData` 2413 is a small deterministic random generator for the smudge overlay. Called with -1 it
seeds from `(mission << 8) | spread` and clears a ten-bit used-mask; called with an item index it
returns an unused value 0 to 9 and marks it used, resetting once all ten are taken; called with -2
it reseeds from the clock. The overlay is therefore stable for a given page and varies between
pages, and `SB_P_GRIME` is drawn one z above the scrap it dirties.

### The zoom view's sepia

A photograph reads warm and faded in the book and plain in the file EXPORT TO DESKTOP writes, and
no draw anywhere applies a colour matrix. The warmth is one piece of art drawn over the print.

`SB_P_GRIME`, the page wash, is a neutral stain: a 5 px opaque white border around an interior of
alpha 0.067 to 1.0 (median 0.149) whose colour is (11.7, 11.4, 11.2), so a grey 128 under it comes
out at R/G 1.000 and B/G 1.000. It darkens without tinting. The original's own page confirms that
nothing else colours the print: fitting that border's opaque white in a reference spread puts the
drawn wash at gain R 0.9740, G 0.9799, B 0.9800 and the shipped scraps beside it at R 1.0126,
G 1.0125, B 1.0125, neutral within a percent both times.

The zoom's own mount is where the sepia comes from. `DZ_ZOOMgrimeframe.png` is a cream torn photo
mount with a translucent 585×419 window at mount-local (37, 37): alpha 0.137 to 0.894 (median
0.302) over the colour (188.8, 173.0, 131.0), which is R/G 1.092 and B/G 0.757. Over a grey 128 the
window reads R 146.4, G 141.6, B 128.9, a ratio of R/G 1.034 and B/G 0.910. `LAYOUT.CSV` gives
`SBZ_GRIME` a Z of 200 against `SBZ_IMAGE`'s 0, so the mount draws over the print rather than under
it, and `SCRAPBOOKZOOM.SCRIPT` places the print at the mount's own position plus ten and eight
instead of at the row's authored `ZoomX`/`ZoomY`. The window is cut for a 640×480 print at that
offset, so a file of another shape lands inside the opaque mount instead of behind the window.
EXPORT TO DESKTOP copies the file, which never meets the mount, and the exported PNG is therefore
the plain print.

### Fitting a block to its box

Every `SBZ_T_TITLE<letter>`, `SBZ_T_CAPTION<letter>` and `SBZ_T_TEXT<letter>` row carries a `Height`
beside its `Width`, and **the original ignores it**. It draws a block at the point size the langui
row's `[FONTID]` names, one line per face height, from the box's top down, and words that do not
fit run on past the box and off the screen. Mission 1's second spread is the plain case: the diary
scrap `1_2_2` is `VIN14` in a 555×590 box at y 10, its shipped text wraps to about 597 px, and the
original's own page cuts the last line at the bottom edge of the 600 px screen. The original
neither shrinks, clips nor scrolls; it simply overruns.

**CSVM departs here.** A block taller than its box is stepped down a whole point at a time, its
pitch scaled with it, and drawn at the largest size that fits. Of the 165 blocks the shipped table
carries, ten are over their box at the authored size and take the step: `0_1_9`, `1_1_2`, `1_2_2`,
`4_1_2`, `7_1_5`, `9_1_3`, `16_1_3`, `20_1_1`, `23_2_2` and `24_2_1`. Nine are 14 pt bodies that
land at 13 pt or, for `0_1_9`, at 12 pt. The tenth is `20_1_1`'s `CENT36` headline, which wraps to
three lines in a 110 px band and lands at 30 pt, the deepest step any shipped scrap takes. The
floor is 8 pt: a block that will not fit even there is drawn at 8 pt and overruns, so a fault shows
rather than words being dropped silently. No shipped scrap reaches the floor.

Twelve further blocks draw a raw `IDS_SB_...` symbol, because `RESRC1.H` or the string table does
not carry it and an unresolved key degrades to itself. All twelve are single-line titles and all
twelve fit their box, so no overrun in the shipped table is a placeholder standing in for shorter
words.

The rule lives in the renderer rather than the composition, because how many lines words wrap to is
a font metric: `CampaignScrapbookZoomPage` puts the box's authored height on the `BoardLine` and
`ComposedBoardView` does the stepping, the same division `BoardNote`'s `Shrink` already uses. The
`scrapbook-fit` suite sweeps every row of the shipped table, names the blocks over their box in its
artifact, and fails if one is still over it after the step.

### Export to Desktop

`uiData` 2412 is `FUN_00406870`, and it is a plain file copy. `SHGetSpecialFolderLocation(NULL, 0)`
plus `SHGetPathFromIDListA` gives the desktop's own path, the scrap's base name (everything past
the last backslash) is appended, and the file's bytes are written with `CREATE_ALWAYS`, so an
export overwrites whatever stood there. A failed write is deleted again rather than left truncated.
The outcome is a message box either way: langui 705 (`This image has been saved to your desktop as
%1!s!.`) with the file name, or 706 with `FormatMessage`'s text for the last error.
`SBZ_B_EXPORT` is deactivated for a zoom whose inset image name is empty, so the button only ever
has a file to copy.

### The results rows, and the one that is not drawn

`uiData` 2406 returns five strings for the tab it is given: the outcome line and four values. The
script binds exactly those five to `sb_t_completetitle`, `sb_t_time`, `sb_t_hit`, `sb_t_cash` and
`sb_t_planes`. **`LAYOUT.CSV` and langui both carry a Rockets Expended row that nothing draws**:
`SB_T_ROCKETSTITLE` and `SB_T_ROCKETS` exist, `IDS_SB_ROCKETS_TITLE` is langui 1204, and
`SCRAPBOOK.SCRIPT` never creates either widget. Its y macro is the tell: the drawn rows sit at
`SLINE1` 369, `SLINE2` 412, `SLINE4` 436, `SLINE5` 460 and `SLINE6` 484, evenly spaced once Rockets
is absent, while `SLINE3` is stranded at 388 between the first two. The row was cut and the
remaining five were re-spaced over it.

The five strings are the outcome line (langui 1213 or 1214 on bit 0 of the tab's objective mask),
the run time as `mm:ss` out of the millisecond field, the gun hit ratio as a percentage of the
hit and shot words, the cash field, and the sum of the two per-airframe kill arrays. Only the last
is computed rather than read: [`org/debrief.md`](../org/debrief.md) has the arithmetic and where the
arrays come from.

### The kill stamps

The eleven stamps are fixed positions `SB_KILL0` to `SB_KILL10` with matching `SB_KILLTEXT0` to
`SB_KILLTEXT10`. The positions are scattered over the page rather than laid out in reading order
(`SB_KILL1` at `467,93` sits left of and above `SB_KILL0` at `560,109`), so a page with three stamps
puts its first in the middle, its second at the left and its third at the lower right.

The script asks `uiData` 2404 for ordinals 0 to 10 and stops at the first negative return, so the
slots fill densely from `SB_KILL0` in the order the engine reports rather than by airframe. Each
answer is a frame index for the shared art and a count string formatted through langui 520
`IDS_KILLCOUNT`. `SB_killMARKERcombined.png` is a vertical strip of **22 frames of 70x100**: frames
0 to 10 are the eleven airframes in the engine's own order (Hoplite, Hellhound, Balmoral, Bloodhawk,
Brigand, Devastator, Firebrand, Fury, Kestrel, Peacemaker, Warhawk) with the airframe name drawn
into the stamp, and frames 11 to 21 are the same eleven again over a star. The star is the ace
variant; which kills earn it is in [`org/debrief.md`](../org/debrief.md).

### Entry, exit and the table of contents

Every button that leaves the book, the zoom or the table of contents, stated the same way as the
[Screen flow](#screen-flow) table above: by its `LAYOUT.CSV` `ScriptToExe` column when it carries
one, or by the script's own `script_run` / `script_pause` / `script_continue` / `script_end` /
`mail` when it does not.

| From | Control | Goes to | Stated by |
|---|---|---|---|
| Cabin | `PC_B_PREVIOUS` | `SCRAPBOOK_TOC.SCRIPT` | layout |
| Mission end | (automatic) | `SCRAPBOOK.SCRIPT`, at the flown mission's spread 1 | `crimson.exe`, `FUN_0046fb60`; see [`org/debrief.md`](../org/debrief.md#the-pass-in-order) |
| Book | a scrap | `SCRAPBOOKZOOM.SCRIPT`, paused rather than ended | script |
| Book | `sb_b_toc`, or `sb_b_prev` at the front of the book | `SCRAPBOOK_TOC.SCRIPT`, paused rather than ended | script |
| Book | `sb_b_replay` | ends the book; Replay Mission's own fork, below | script |
| Book | `SB_B_RETURNPC` | the cabin | layout |
| Zoom | Esc, or `sbz_b_return` | back to the paused book | script |
| Zoom | `sbz_b_export` | `messagebox.script` (an export confirmation; does not return to the book) | script |
| Table of contents | `sbtoc_b_view`, `sbtoc_b_current`, or a double-clicked row | the book, at the picked mission's spread 1 | script |
| Table of contents | `sbtoc_b_replay` | ends the table of contents; Replay Mission's own fork, below | script |
| Table of contents | `SBTOC_B_RETURN` | the cabin | layout |

`SB_B_RETURNPC` and `SBTOC_B_RETURN` both carry `ScriptToExe = PassengerCabin`, the same
layout-declared-transition pattern the [Reader rules](#reader-rules-and-edge-cases) already record
for the cabin's own Return to Main Menu: neither button has a case in its script's `gui_mailbox`.

**Replay Mission forks on a session flag the scripts only read, `$$SR$$`.**
`PASSENGERCABIN.SCRIPT` zeroes it at its own `gui_create`; nothing in the three scrapbook scripts
sets it, so only the mission-end path can leave it set. The book's `sb_b_replay` handler (the table
of contents' `sbtoc_b_replay` is the same logic over its own selected row):

```
FRA = callback(uiData, 2405, 0)        // the mission the open page shows
if (FRA == $$KP$$ && $$SR$$)           // still the mission just flown, same session
    restart in place: callback(uiData, 2013, -1, ...), gosCallback 1
else
    the ordinary mission-select path: mail 11003, gosCallback 31, gosCallback 6
```

which is the same three calls (`mail` 11003, `gosCallback` 31, `gosCallback` 6) the cabin's own New
Mission button makes. Replay Mission's *availability* does not fork this way: `uiData` 2411 gates
it on the shown mission's own record alone, regardless of how the book was reached.
[`org/debrief.md`](../org/debrief.md#entering-the-book-replay-mission-and-the-table-of-contents)
has the full trace and what is still unread (which native function answers `PC_B_PREVIOUS` and
where `$$SR$$` is set).

## Callback reference

Only the ids these six scripts use. `uiData` dispatches ids 2000 to 2038 through a jump table at
`0x0040f518` and 2100 to 2413 through a byte table at `0x0040f788` plus a jump table at
`0x0040f5b4`; 2099 and 2600 are tested separately. A first argument of -1 or -2 selects the pilot
or wingman slot, and any other value is a plane index.

| id | Arguments | Behaviour |
|---|---|---|
| 2005 / 2007 | row, out | pilot / wingman gun row; count 8 |
| 2006 / 2008 | row, out | pilot / wingman rocket row; count 8 |
| 2009 | slot, flags, out | mission name, langui `3450 + m - 1` (flags 1) or `3480 + m - 1` (flags 2) |
| 2010 | slot | the slot's airframe index, 0 to 10 |
| 2011 | slot, out | the slot's plane name and airframe title |
| 2012 | slot | the slot's current plane index |
| 2013 | slot, plane | set the slot's plane |
| 2014 | slot, plane, fromIA | set the slot's plane override; -3 means "use the profile's selection" |
| 2015 | slot, flags, out x4, 1 | the slot's four rating lines, caption included, at flags `0x20` |
| 2018 | row, out | plane dropdown row; row -1 answers the owned plane count, less one on missions 13 and 17 |
| 2019 / 2020 | row, out | pilot / wingman weapons row on the plane screen |
| 2021 | | grant and select this mission's story aircraft |
| 2022 | mission, out | the mission's objectives text |
| 2027 | row, out | ammunition list row, langui `3360 + row`; count 5 |
| 2028 | row, out | rocket list row, langui `3395 + index`; count is the available subset |
| 2030 | slot, group, out, out | gun group's gun id (5 = none) and current ammunition |
| 2031 | slot, pylon, out | pylon's current ordnance, or -1 if the pylon does not exist |
| 2032 / 2033 | index, out | ammunition / rocket description, title plus body |
| 2034 / 2035 | slot, fromIA | commit / load the 204-byte working copy |
| 2036 / 2037 | slot, index, value | write an ammunition / ordnance pick into the working copy |
| 2038 | row, out | mission dropdown row, langui `3450 + row`; count 24 |
| 2099 | name, out | roster lookup; out is the index, or the roster count when not found (returns -1) |
| 2100 | name | set the current profile name; returns non-zero when it changed |
| 2101 | out | the current profile name |
| 2102 | row, out | roster row; count is the number of profiles |
| 2103 | slot, out | fills the sell messagebox; non-zero asks for the two-button box |
| 2104 | mission | set the current mission; -2 means `completed + 1`; clamped to 24 |
| 2105 | slot, out | fills the export messagebox, langui 702 over the airframe title |
| 2106 | out | fills a messagebox string (not decoded further) |
| 2109 | slot | sell the slot's plane |
| 2150 | mode, arg, out | read (mode 0, arg -1 current / 100 next / 101 previous awarded) or write (mode 1) the profile's memento file name |
| 2151 | out | the chapter number when that chapter has not been started, else 0 |
| 2600 | | non-zero while a next mission exists |

The scrapbook's own ids, `0x0040a30f` upwards in the same dispatcher. Resolve any of them by hand
with the rule above: byte at `0x0040f788 + (id - 2100)`, then dword at `0x0040f5b4 + entry * 4`.

| id | Handler | Behaviour |
|---|---|---|
| 2401 | `0x0040a682` | out flags: whether a next spread and a Current Mission jump are available |
| 2402 | `0x0040a6a2` | 0 jumps to the current mission at spread 1; 100 steps forward, 101 back, returning 0 at the front of the book |
| 2403 | `0x0040a30f` | the current spread number; 1 is the results page |
| 2404 | `0x0040a714` | the nth non-empty kill stamp of a tab: a frame index 0 to 21 and a count string, or -1 once the tab has no more |
| 2405 | `0x0040a633` | mode 1 opens a mission at spread 1, or the campaign's current mission when given -1; any other mode reads the open mission |
| 2406 | `0x0040a7d4` | the outcome line and four result values for a tab, above |
| 2407 | `0x0040aa52` | walk to the next drawable item of this spread, returning type 5 for an image and 6 for text |
| 2408 | `0x0040a453` | the page title, out through `ESA.BC`: langui 1215 (`%1!s! - %2!s!`) over the player's name and the mission's short name (langui `3480 + m - 1`), which is the "Zachary - The Lost Treasure" both scrapbook reference shots carry |
| 2409 | `0x0040a4b8` | a table-of-contents row: given an ordinal, out a plane-icon selector and three text lines (mission name, area, plane flown). Ordinal -1 answers the row count, which is the campaign position plus one, or 25 under the unlock flag; ordinal 0 is the career page rather than a mission. [`org/debrief.md`](../org/debrief.md) has the fields and the addresses |
| 2410 | `0x0040a935` | the zoom view: background, inset image and position, layout letter, and the title, caption and text ids |
| 2411 | `0x0040a408` | is Replay Mission offered, which is true once either half of the mission's record holds a time |
| 2412 | `0x0040a3d9` | export the open scrap to the desktop, below |
| 2413 | `0x0040a321` | the grime generator, above |

2414 and 2502 are outside the `2100`–`2413` table and are answered by a different widget; 2414 asks
whether a name resolves outside `assets\graphics\`, which is true only for a `Snap_` capture.

`gosCallback` ops:

| op | Behaviour |
|---|---|
| 1 | fly the current mission |
| 6 | open the briefing for the current mission |
| 7 | the story chapter (1 to 5) of a mission; -2 means the campaign's current position |
| 9 | reset the in-memory profile and load the named one |
| 12 | write the profile out |
| 16 | clear and re-enumerate the profile roster |
| 17 | delete the named profile |
| 22 | export the slot's plane to Instant Action and multiplayer |
| 27 | does this mission have a wingman |
| 28 | the mission's objective text lines |
| 31 | join the loader thread and tear down the mission world |

`uiControl` 2141 case 0 writes the current profile name to `HKCU`'s `UIPlayerName`; case 1 to 8 of
the same id, and its read twin 2140, cover the multiplayer callsign, game name, team name, voice,
auto-refresh, IP address, phone number and connection type. 3107 is the edit-box name validator.

## Reader rules and edge cases

- **Read the transitions from `LAYOUT.CSV` first.** The `ScriptToExe` column carries roughly half
  the navigation, and the scripts are silent about those buttons.
- **A slot argument of -1 or -2 is not a plane index**, and any other value is. The same convention
  runs through every campaign `uiData` id.
- **Missions are 1-based** in every callback above, and 0-based when a `cm_sequence` entry is
  addressed directly (`gosCallback` 1, 6, 7 and 27 all take `mission - 1`).
- **A blank list row is data.** Gun rows exist per barrel and are empty when the group has no gun
  or no second barrel; rocket rows are empty when the pylon holds 11 or does not exist. Neither is
  padding to fill in.
- **A dropdown's row count in `LAYOUT.CSV` is a display window**, and `@globals@AR` is an array
  capacity. Neither is the item count, which always comes from the fill callback's row -1 answer.
  This is the same trap [instant-action.md](instant-action.md) records for `ia_d_planep`.
- **A rollover preview is a write, not a read.** The plane screen previews the row under the pointer
  by writing the slot's plane override (`uiData` 2014), and what puts the committed pick back is the
  pointer leaving. Nothing clears the override on the way out of the screen; the flight check clears
  both slots with the sentinel -3 when it is created.
- **Do not derive an ordnance id from a dropdown row.** The rocket list is filtered by campaign
  progress; ammunition is not.
- **The profile name becomes a directory name.** The original rejects `" * / : < > ? \ |` and
  proves the name by creating a directory with it.
- **`brief_c<NN>` state keys are `<campaign folder index><mission number>`.**
  `cm_sequence.zrd`'s per-entry `campaign` and `mission` fields reproduce all 24 keys exactly
  (Hawaii is folder 6 with missions 1 to 5, so `brief_c61` to `brief_c65`; Northwest is folders 3,
  1, 2, 1, 1 with missions 1 to 5, so `brief_c31`, `brief_c12`, `brief_c23`, `brief_c14`,
  `brief_c15`, and so on for Hollywood, Colorado and Manhattan). The briefing narration wav's
  `m<N>` is likewise the folder mission number, not the story position:
  Hawaii's story mission 2 is folder mission 5 and plays `c1-HA-m5_briefing.wav`.
  This closes the open question in [briefing.md](briefing.md) about which shipped mission folder
  each briefing state belongs to.

## Evidence and limits

Every claim above comes from one of three places, named at the point of use: the script text, the
`LAYOUT.CSV` row, or a `crimson.exe` handler. The reference screenshots
(`OriginalScreenshots\Campaign *.png`) were checked against every documented transition and
corroborate but do not establish them.

| Screen | Script-proven | Screenshot-corroborated | Open |
|---|---|---|---|
| Player profile | every widget, the roster fill, the validator, all four exits | the roster list's own selection colour (`0xff800000`), the four controls, the pre-filled name | what `uiData` 2106's message says; the unreachable savegame branch |
| Cabin | the six buttons and their targets, the deactivated SAVE GAME, the plane photo path, the memento source, the pin count, the `idaho` dropdown, the intro trigger | five buttons and no SAVE GAME, the painted cabin, the framed memento, the map | whether anything on the screen animates or loops, and how the pin frames read; both need the cabin capture |
| Chapter intro | the movie name, the skip gesture, the one-shot guard | not covered by any screenshot | the MPG decode itself, deliberately out of scope |
| Flight check | both slots, all four lists, the wingman gate, both plane-change rules, the grant table, both exits | the title, plane lines, six of eight gun rows filled and two blank, the objectives note, the calibre label's string block, both buttons | the `10018`/`10000` state art |
| Plane selection | both blocks and their gates, the dropdown fill, the rollover preview, the duplicate rule and its revert, the export and cancel paths, the deactivated SELL pair | the widget positions, the list rows, the rating words and their captions, both message boxes verbatim, the preview following the pointer | what `gosCallback` 22 writes |
| Ammo selection | both callers, the working-copy commit, the greyed empty group, the pylon deactivation, all six string blocks | that a deactivated field is not drawn (the Hoplite's one pylon a wing against the Balmoral's four), the greyed `No Gun` captions standing without fields, both description panes, both plane diagrams | the rocket table's unread second field |
| Scrapbook | every button's transition, the composition file, the results rows, the kill stamps, the table of contents, the Replay Mission fork | the three reference spreads scrap for scrap, the stamps and total on two of them | the native side of the cabin entry and of `$$SR$$`, both in `crimson.exe`, unreached this session |

Where this decode stops:

- **Most of the plane screen's `uiData` handlers were not traced in `crimson.exe`.** 2012, 2013,
  2019, 2020, 2103, 2105 and 2109 are described above from their use in the script and from what the
  reference screenshots draw, which fixes each one's arguments and its visible effect but not the
  fields it reads. `2000` and `2001` belong to the save and load screens and were not followed
  either. 2015 is the exception: its handler at `0x004096ab` and the rating helper it calls four
  times are decoded in [`../org/hangar.md`](../org/hangar.md).
- **The three flags that bypass the ordnance availability filter** were located, not identified.
- **The obfuscated identifiers were not recovered.** Widget keys, callback ids, message ids and
  langui ids carry the meaning here, and the letters were left alone.
- **`$$KP$$`** is read as the current mission, 1-based, because the script's own `case 13: case 17:`
  and callback 2018's test of `UIData +0x00` against 13 and 17 must name the same missions, and
  because the two aircraft those missions grant are named `IDS_HW3FURYNAME` and
  `IDS_RM2HOPLITENAME`, which match missions 13 and 17 under that reading (Hollywood mission 3, and
  Rocky Mountains mission 2, which is `cm_sequence`'s `COLORADO` area) and not under the
  alternative. The placeholder's own binding was not traced.
- **`FUN_00417090` against `FUN_00417410`.** Which of the two a mission launch takes is decoded
  (first attempt against replay); what differs between them beyond an extra loader call and the
  save-system calls is not.
