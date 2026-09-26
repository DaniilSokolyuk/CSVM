# The campaign board: authored pixels on a modern window

The campaign's six out-of-mission screens (profile, cabin, previous missions, briefing, flight
check, ammo selection) are composed boards, not row lists: each places its art at the coordinates
the original authored, over that screen's own painted background, with the original's own button
plaques. This page holds the one decision every one of them inherits, which library each coordinate
came from, and what is deliberately not reproduced yet.

The code is `CSVM/src/UI/Boards/BoardFit.cs` (the mapping), `ComposedBoard.cs` (what a board is made of),
`CampaignBoards.cs` (the fixed chrome and the composer), `CampaignLayout.cs` (the boards' read of
the decoded layout) and `ComposedBoardView.cs` (the renderer). The decodes the coordinates come
from are [`formats/menu-layout.md`](../formats/menu-layout.md),
[`formats/campaign-screens.md`](../formats/campaign-screens.md) and
[`formats/briefing.md`](../formats/briefing.md).

## Contents

- [The authored space](#the-authored-space)
- [Decision: uniform fit, letterboxed, nearest](#decision-uniform-fit-letterboxed-nearest)
- [Where each coordinate comes from](#where-each-coordinate-comes-from)
- [The table of contents' mission list](#the-table-of-contents-mission-list)
- [Button plaques and their four frames](#button-plaques-and-their-four-frames)
- [Which player the profile screen opens on](#which-player-the-profile-screen-opens-on)
- [What is not reproduced](#what-is-not-reproduced)

## The authored space

Every campaign screen is authored against a fixed **800x600** dialog, and three independent
extractions agree on it. `PC_BackGround.png`, `FC_BackGround.jpg`, `OL_BackGround.jpg` and every
briefing map bitmap (`ha-m1map.png` and its twelve siblings) are exactly 800x600. `ASSETS\LAYOUT.CSV`
places the cabin's last button at `593,561` and the flight check's at `551,<GX>`, both of which only
sit on a bottom edge at that height. `Briefing.zrd`'s `BUTTONS` section puts its three plaques at
`y = 560` with a 32-pixel bitmap, which ends 8 pixels short of 600.

The reference screenshots under `OriginalScreenshots\Campaign *.png` are 802x634: an 800x600 client
area inside a one-pixel border under a 33-pixel title bar. Subtracting `(1, 33)` maps a screenshot
pixel onto an authored one, and that is the transform every measurement below was taken through.

## Decision: uniform fit, letterboxed, nearest

**One uniform scale on both axes, `min(viewportWidth / 800, viewportHeight / 600)`, the board
centred in the window, the remainder filled black, and every bitmap sampled nearest-neighbour.**
At the project's 1280x720 window that is a scale of 1.2, a 960x720 board and a 160-pixel bar on each
side.

The two alternatives were considered and rejected.

- **An integer scale of the authored resolution.** The pixel grid stays exact, but `floor` of the
  fit is 1 at 1280x720 and still 1 at 1920x1080, because 2x needs 1200 lines. That draws an
  800x600 island in the middle of a 1080p window with a black surround wider than the board is tall.
  A screen the player cannot read is worse than one whose pixels are unevenly doubled.
- **Fit to height with the background bled or cropped horizontally.** The composition does not
  survive it. The plaques hang off the authored bottom edge and the parchment starts at `x = 0`, so
  widening the frame either pulls those elements away from the edges they are anchored to or crops
  them off. There is also nothing to bleed: the background bitmaps are exactly 800x600 with no
  margin outside the composed frame.

Nearest-neighbour is the half of the decision that answers the "soft upscale reads as a bug"
objection. A fractional scale duplicates some source rows and not others, so the art reads as
chunky rather than blurred, which is how a scaled-up original reads and not how a resampled one
does. The filter is set once, on the view (`ComposedBoardView.Build`), so it covers every bitmap a
board draws.

Text is the exception and is drawn as a real face at `scale * authoredSize`, never as scaled
bitmap glyphs, because the original's own font table is not decoded and a bitmap font would have to
be invented.

## Where each coordinate comes from

Three sources, and a reader should know which one is under any given number.

- **`ASSETS\LAYOUT.CSV`**, for the script-driven screens, read at run time from the decoded
  `extracted/rof/menu_layout.json` through `CampaignLayout`. Every button, pane and text slot in
  `CampaignBoards` and in the pages names its section and row, and each read carries the value the
  board drew before the layout existed as its fallback: a row that is present answers, a missing
  file, section, row or column answers with the fallback whole (never one coordinate from the row
  and one from the fallback). A data root with no `menu_layout.json`, or an unreadable one, logs
  one `ui` warning naming the reason and every board draws from its fallbacks, which are the
  shipped rows' own values, so the screens are the same either way. The macros the shipped file
  resolves per section (`<GX>`, `<Y>`, `<V2>`, `<V3>`, `<V4>`) reach the boards already
  substituted. Where the row's value and the reference screenshot disagree, the board **pins** the
  measured value and reads the rest of the row; the table below names each.
- **`Briefing.zrd`'s `BRIEFINGDIALOG`**, for the briefing's parchment (`POSITION [0, 295]`), its
  title (`[35, 315]`), its list (`[35, 335]`, `WORDWRAP [185, 240]`, `SPACING [5]`) and its three
  plaques (`[197, 560]`, `[397, 560]`, `[597, 560]`). ⚠ The list's four numbers are a flow rule and
  not four slots: an entry wordwraps to 185, the next entry starts `SPACING` below whatever the last
  one actually drew, and the run stops at 240. Half the campaign's 79 objective lines are taller
  than one 30 px slot would be, so a fixed pitch draws them over each other (`BL-490`). How tall an
  entry drew is a font metric, which is why `BoardNote` carries the widget and the renderer
  measures it.
- **Measured off the reference screenshots**, for the values whose row disagrees with the
  screenshot. Each was found by matching the element's own bitmap against the screenshot at every
  offset and taking the best fit; the X the match returned agreed with the layout row's own X in
  every case, which is what makes the Y trustworthy. Most measurements agree with the decoded rows
  exactly (`<GX>` at 553 with `FC_B_ReturnToBriefing.png` matched at the row's own `x = 341`,
  `<Y>` at 547 with `CM_B_DeletePlayer.png` at `x = 193`, `<V4>` as `FC_B_PaperButton.Png`, the
  only paper button whose 117-pixel width matches the drawn plaque), so those read from the row.
  The ones that differ are pinned: the code keeps the measured value, reads the rest of the row,
  and the discrepancy is recorded here rather than in the layout.

| Row | Screen | Row says | Pinned at | How |
|---|---|---|---|---|
| `CM_B_START` art | profile | `GN_B_Continue.png` | `CM_B_Start.png` | the drawn CONTINUE plaque is the wider strip, matched in `Campaign Player Profile.png` |
| `FC_B_CHANGEPLANE` / `FC_B_CHANGEAMMO` y (`<V2>`) | flight check | `132` | `131` | the CHANGE AMMO plaque's edges in `Campaign Flight Check.png` |
| `FC_B_CHANGEPLANEW` / `FC_B_CHANGEAMMOW` y (`<V3>`) | flight check | `350` | `349` | the same match on the wingman block |
| `FC_T_TITLE` / `OL_T_TITLE` x and justification | flight check, ammo selection | `132`, centred in 190 | `138`, left | the heading's first glyph in both reference shots; the row's y and width read |
| `OL_S_AMMODESC` / `OL_S_ROCKETDESC` y | ammo selection | `96`, `332` | `92`, `328` | each column's first line in `Campaign Ammo Selection.png`, whose 236-pixel separation is the rows' own, so both panes take the same lift; the rows' x and width read |
| `FC_T_GUNLISTW` / `FC_T_ROCKETLISTW` y | flight check | `400` | `399` | the wingman tables keep the pilot pair's 17-pixel drop under their heading row |
| `PS_T_WINGPLANE` y | plane selection | `323` | `324` | the wingman's plane line keeps the pilot line's `106 + 218`, the drop every other widget of the pair has |

`PS_T_TITLE` is read at its row's `x = 132` and drawn left-justified where the row says centred;
only the justification is pinned there.

Two positions are chosen rather than decoded, and both are marked as such in the code. The cabin's
memento window (`179, 330`, 73x84) is the block each `PC_P_HANGAR*.JPG` keys out for it, where the
layout's `PC_MEMENTO` pane sits at `169, 325` with no size, and the picture inside it is whichever
keepsake the profile holds, the campaign's opening one until the chooser writes another. The profile screen's title mark
(`MM_Logo.png`) is `[@MainMenu@]`'s own `MM_LOGO` row, read from the layout; what is chosen is
standing it over the profile dialog at all, matched off `Campaign Player Profile.png`, since the
flag movie it belongs over is not decoded.

## The flight check's objectives note

The note is authored geometry, and all of it is in `LAYOUT.CSV`: `FC_T_OBJTITLE` at `558, 80` with
a 130-wide column, `FC_T_OBJECTIVES` at `554, 120` with a 206-wide one, both in the rows' own
`0xFF2D3843` ink, which is `BoardPalette.Paper`'s `Detail`. The title's face is decoded too, from
the `[AB19I]` tag `IDS_FC_OBJECTIVES` (langui 1014) leads with: 19 pixels, italic.

**One value is chosen rather than decoded**, and is marked as such in the code: the note body's
16-pixel face. `FC_T_OBJECTIVES` carries a height of 360000, the layout's "grows as it needs to"
sentinel, so the row names no line pitch and 16 is measured off `Campaign Flight Check.png`.

What goes on it is the mission's own display list, one line per unique `IDENTITY` priority
([objectives.md](../formats/objectives.md), "IDENTITY and the objectives display"). ⚠ The line's
number is part of the `MSG_` text, so nothing in the screen numbers them; an `IDENTITY` with no
message key is a row with empty text, and draws as a blank line.

## The table of contents' mission list

The previous-missions screen's list is `SBTOC_L_TOCList`, and an `L` row's columns are
`Slider,UpArrow,DownArrow,ScriptPointer,X,Y,Z,Width,Height,TotalDisplayed,TabOrder`. It reads
`420,140,0,325,80,4`: the widget's top left is `420,140`, a row is 325 wide and **80 tall**
(`Height` on an `L` row is one row's, not the widget's), and four of them are on screen at once.
The page reads the box, the row height, the window and the three scroll bitmaps off that row once
when it is built. Everything inside a row is `SCRAPBOOK_TOC.SCRIPT`'s own list sub-script, which the layout says
nothing about:

- The aircraft silhouette is `assets\graphics\fc_planeicons.png` at `location.x + 2`, one frame per
  row, 12 frames of 80x80. Frames 0 to 10 are the airframes in id order and frame 11 is the card
  fan the not-yet-started career row takes.
- The text column starts at `EZ`, the icon pane's width plus 20, so 100 in from the widget's left.
  Its three rows sit at `+10`, `+30` and `+50` down the row, and hold what `uiData` 2409 returns:
  the mission's short name (langui `3480 + m - 1`), its area (langui `1220 + chapter`) and the
  plane that flew it.
- The picked row is `ldrawrect` in `0x80f2e7b7` under an `ldrawframe` in `0xffdd9017`, both over
  the full 325-wide row. The row under the pointer takes the same frame over a `0x40f2e7b7` wash.
  On a pad the cursor is what a pointer was, so the focused row draws the second pair.

**Three values are measured rather than decoded**, and are marked as such in the code. The row face
is 17 pixels: every row is drawn in `@globals@gfont3d`, which no layout row sizes. `SBTOC_T_CHARACTER`
and `SBTOC_T_MISSIONS` name no face either, so the player's name is 14 and the heading over the list
is 11; both are `justify 2` (right) against their own 300-wide column, which is what puts them
against `x = 725` in the reference. The scrollbar column stands at `x = 730`, 16 wide, with an
11-pixel arrow at each end of the 320-pixel window and the list's own `0xff282418` behind the thumb,
all four taken off `Campaign CAP-41 Previous Mission 2.png`.

## Button plaques and their four frames

Every screen-specific button ships as one PNG holding **four stacked frames of equal height**, in
the order `LAYOUT.CSV`'s own colour columns name them: **disabled, normal, rollover, depressed**.
`PC_B_ReturnMainMenu.png` is 200x128, so four 200x32 frames; `CM_B_Start.png` is 113x136, so four
113x34. The words are painted into the art, so the state is entirely in which frame is drawn.

The generic paper buttons (`FC_B_PaperButton.png`, `SB_B_PaperButton.png`) and the briefing's
`brief_button1` carry no words: their layout rows name an `IDS_*` string instead, and the label is
drawn over the plaque. `brief_button1` is a single frame, and its focus state lives entirely in the
label's face, which is what the dialog's `BtnLabelNormal` / `BtnLabelRollover` / `BtnLabelActivate`
triad is for.

On a pad, focus is the rollover frame and a held confirm is the depressed one. That is the whole of
what changed about focus: the cursor was a highlighted text row and is now a plaque in the state
the original's own art already carried.

The messagebox's buttons take no screen palette. `MB_B_Buttons.Png` is 62x76, four 62x19 frames of
one pill shape at four brightnesses: the disabled frame fills at `0x55` inside a `0x99` rim, normal
at `0x21` inside `0x67`, rollover at `0x00` inside `0x35` and depressed at `0xB7` inside `0xFD`.
`[GLOBALVARS]` gives their labels `0xFFFFFFFF` on the normal and rollover frames and `0xFF000000`
on the depressed one (`G2` to `G4`), so `BoardInk.Dialog` and `BoardInk.DialogPressed` are fixed
white and black whatever screen the box covers. A palette ink would put the paper screens' black
`LabelActivate` on the black rollover frame.

**The rollover frame is the pointer's, not the focus's.** `MESSAGEBOX.SCRIPT`'s `gui_init` focuses
one button on every raise, `CX` for the `0x1` box and `BX` for the plain `0x4` pair (`DX` when the
`0x20` or `0x40` bit is set), and the reference shot of a `0x1` box has that focused OK on its
normal frame, its fill measuring `0x22` inside a `0x66` rim, with the pointer elsewhere. So the
original's focus moves no frame at all. A remake whose pointer and cursor are one thing cannot
leave the cursor invisible, so Original draws the answer under the pointer on the rollover frame
and marks the focused answer with the same `DISABLED` outline the option pages put on a row whose
art carries no focus state, three pixels clear of the strip and only while the pointer is
somewhere else.

## Which player the profile screen opens on

The screen opens with the cursor on the profile last played, already ticked, so continuing a
campaign is one press. The record is the profile **name**, held in `user://Profiles/last-played.json`
beside the profile directories, written when a profile is seated and cleared when it is deleted.

The name, and a store of our own, are both what the decode says. The original keeps the current
player as a name too, in `HKEY_CURRENT_USER\SOFTWARE\Microsoft\Microsoft Games\Crimson Skies\1.0`
under `UIPlayerName`, read back into `UIData +0x314` at startup
([saved-games.md](../formats/saved-games.md), "Which player is current, across runs"). Nothing in
the save container carries it, so there is no file to read and the semantics are all the decode
supplies. Writing that registry key itself is rejected: it is the retail game's own live state, and
a remake that edits it changes what the original game does on the same machine.

⚠ **A row index and a directory timestamp are both wrong and neither is used.** The roster sorts
alphabetically, so a stored row moves whenever a profile is created or deleted; and every save
touches a profile's directory, while a restore or a copy rewrites all of them at once. A recorded
name is resolved against the roster on each visit, and one the roster no longer carries opens the
cursor on the name field instead.

## What is not reproduced

Named so nobody reads their absence as a decode gap.

- **The profile screen's animated flag** (`CM_MOVIE`, `CrimFlag.MPG`). No MPG reader exists here and
  the shipped still (`MM_BackGround.png`) is a placeholder image reading "CS BACKGROUND", so the
  title mark stands over a plain ground.
- **The cabin's map pins** (`PC_PIN0`-`PC_PIN4`, `PC_mappins.png`). The layout puts them at z 0,
  behind a background at z 4, which cannot be what a pin on the desk map means; the count rule is
  tested in `CampaignCabinPage.MapPinCount` and the drawing waits on that contradiction being
  settled.
- **Per-widget chrome outside the table of contents**: dropdown boxes, and the scrollbars and
  selection bars of every other list. The boards draw their screens' backgrounds, plaques, pictures
  and text; the widgets those text runs sit in are each their own fidelity question, and only
  `SBTOC_L_TOCList` has been answered (above).
- **Italic as a real face.** The extraction ships no italic font, so a langui row asking for one
  (`[AB19I]`) is drawn as the board's own face sheared 0.25 em. That lean is chosen to read like the
  reference screenshot, not decoded from anything.
