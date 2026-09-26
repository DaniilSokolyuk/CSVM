# `LAYOUT.CSV`, the menu widget layout

The authored geometry of the original's menus. `ASSETS/LAYOUT.CSV` inside `crimson.rof` declares
every widget of every single-player screen: its rectangle, its artwork, its string id, its colours
and, for a button, the screen it navigates to. It is the composition half of a screen; the GUI
script of the same name is the behaviour half ([rof.md](rof.md), and
[campaign-screens.md](campaign-screens.md) for the campaign flow's scripts).

`ExtractRof.ps1` decodes it during extraction and writes `extracted/rof/menu_layout.json`. Runtime
reads that file and never parses `LAYOUT.CSV` itself. The decoder is
`CSVM/src/Extraction/MenuLayoutDecoder.cs`, compiled by the engine and `CSVM.Tests` and
`Add-Type`d by the script; the runtime reader is `CSVM/src/UI/Menu/MenuLayout.cs`, whose entry in `docs/architecture.md` says
what it exposes.

## Contents

- [Conceptual model](#conceptual-model), the four line kinds, the two macro mechanisms, the
  three-file screen.
- [Reference](#reference), the field order of each of the ten used widget types, the navigation
  columns, and the emitted artifact's shape.
- [Reader rules and edge cases](#reader-rules-and-edge-cases), what a parser has to tolerate.
- [Evidence & limits](#evidence--limits), what is proven, what stays ambiguous.

## Conceptual model

### The file

56,148 bytes, 1,222 lines, ASCII, CRLF, no header row. It is an INI-like sectioned key/value file
whose values are comma-separated positional records. Four line kinds:

| Kind | Shape | Notes |
|---|---|---|
| Comment | `;…` | Lines 1-41 are a self-documenting block declaring one field list per widget type. |
| Section header | `[NAME]` | `[GLOBALVARS]` plus one `[@ScriptName@]` per screen. Indentation is cosmetic. |
| Macro definition | `G<n>=NAME,VALUE` or `V<n>=NAME,VALUE` | File-wide in `[GLOBALVARS]`, per screen elsewhere. |
| Widget row | `KEY=<type>,field,field,…` | The key may be tab-padded before the `=`. |

35 sections: `[GLOBALVARS]` and 34 screens. **The 34 screen sections are exactly the 34
single-player GUI scripts, one to one.** The archive's other 27 scripts (5 infrastructure, 22
multiplayer) have no section: multiplayer screens use the `CC` widget library and assign geometry
inline, so decoding this file reaches 34 screens and no part of multiplayer.

**The header's indentation is not nesting.** The designers indented related sections to show a
hierarchy; every section is a sibling.

### Two substitution mechanisms

`<NAME>` is textual macro substitution. It resolves against the enclosing section's own `V<n>`
definitions first and `[GLOBALVARS]`' `G<n>` definitions second, and expands to a number, an ARGB
colour or a filename. Substitution happens before any field is interpreted, and a macro's value
never contains another macro.

`[@ScriptName@]` is a section header only. It never appears inline, and cross-screen navigation
names the bare script name in a button's `ScriptToExe` field rather than a bracketed macro.

The two are easy to confuse because a macro's *name* may itself be `V2`, `V3` or `V4`
(`V2=V2,132` in `[@FlightCheck@]` defines a macro called `V2` whose value is 132), so `<V2>` is an
ordinary named macro, not a reference to the second definition line.

### A screen is three files

The layout row declares the widget, the script of the same name creates and drives it, and
`crimson.exe` answers the callbacks. Roughly half the navigation is stated only by the layout, so
a reimplementation reading only the scripts finds those buttons inert. The reverse also happens: a
script can create a widget with no layout row at all.

## Reference

### Widget types

The comment block declares eleven types. Ten are used; `W` (sound object) is declared and never
used, so the shipped file authors no audio. The script class is the `@ctl@XX` control the screen's
script binds the key to, which agrees with the type letter on all 403 script-created keys.

| Type | Widget | Rows | Script class |
|---|---|---|---|
| `T` | text | 297 | `@ctl@PE` |
| `B` | button | 119 | `@ctl@BE` |
| `P` | pane | 106 | `@ctl@ZJ` |
| `D` | dropdown | 70 | `@ctl@PM` |
| `A` | text list | 16 | `@ctl@SJ` |
| `S` | scrolling text | 8 | `@ctl@JN` |
| `M` | movie | 6 | `@ctl@AL` |
| `L` | listbox | 6 | `@ctl@EN` |
| `E` | edit box | 4 | `@ctl@IM` |
| `Z` | slider | 4 | `@ctl@DL` |
| `W` | sound object | 0 | `@ctl@SK` |

### ⚠ The comment block is a hypothesis, not the shipped field order

**No shipped row carries `HelpID`, `TabOrder`, `ScriptPointer` or `Group`, all four of which the
comment block declares.** Removing exactly those four from the declared lists reproduces every
row's field count in the file, for all ten used types, with no other change. The comment's own
`Group` note ("not working yet") is the visible edge of the same drift.

The orders below are established from the rows. Field counts are exact: a row has as many fields
as the list, plus for `B` an optional four-colour tail.

| Type | Fields, in order |
|---|---|
| `B` | `ArtPath, X, Y, Z, ResID, ScriptToExe, ScriptPri, EndScript, Left, Top, Right, Bottom, Style, Checked` then optionally `ColorDisabled, ColorActive, ColorRollover, ColorDepressed` |
| `P` | `ArtPath, X, Y, Z, NumFrames, IsRegion, AlphaType, Volatile` |
| `T` | `ResID, X, Y, Z, Width, Height, Color, Justify` |
| `D` | `Slider, UpArrow, DownArrow, DropUp, DropDown, X, Y, Z, Width, ItemHeight, TotalDisplayed` |
| `A` | `X, Y, Z, Width, Height, Color, Justify, ItemSpacing` |
| `S` | `BorderColor, BackColor, Slider, UpArrow, DownArrow, X, Y, Z, Width, Height, ResID, Color` |
| `M` | `ArtPath, X, Y, Z, ScaleX, ScaleY, Loops, IsRegion` |
| `L` | `Slider, UpArrow, DownArrow, X, Y, Z, Width, ItemHeight, TotalDisplayed` |
| `E` | `FontId, X, Y, Z, Width, Height, MaxChars, TextColor, FrameColor, CursorColor` |
| `Z` | `X, Y, Z, MinValue, MaxValue, CurrentValue, RegionArt, SliderArt, Left, Top, Right, Bottom` |
| `W` | `WavFile, Channel, Volume, LoopCount, AutoStart` (declared, never used) |

`M` also gains a column the comment omits entirely: the movie filename, which is the row's first
field exactly as `ArtPath` is on a `B` or `P`.

`ResID` and `ScriptToExe` sit adjacent at indices 5 and 6 of a `B` row, which is what makes the
comment's `TabOrder` visibly absent: `MM_B_CAMPAIGN=B,MM_B_Campaign.png,279,282,0,0,Campaign,0x1000,0,…`
puts `Campaign` where a `ResID` cannot go, and
`FC_B_CHANGEPLANE=B,<V4>,128,<V2>,0,IDS_FC_B_CHANGEPLANE,PlaneSelection,0x1000,1,…` puts
`IDS_FC_B_CHANGEPLANE` where a `TabOrder` cannot.

### Buttons: navigation, frames and colours

**`ScriptToExe` is the navigation graph.** 46 of the 119 button rows name a target script, and no
script mentions those edges. All 25 distinct targets name a shipped script. `ScriptPri` is a
priority word taking one of three values (`0x1000` on 68 rows, `0x1020` on 9, `0x1100` on the 7
`PlaneConstruction` tab-bar buttons), and `EndScript` is a boolean saying whether the current
screen terminates on the way out.

**A button's art is a vertical strip of stacked frames.** `Style` is `0` normal, `1` check, `2`
radio; a normal button divides its art into **four** frames and a check or radio button into
**eight**. The shipped artwork names its own frame count on the check and radio rows
(`PF_B_CheckBoxSmall.png`, `MP_B_CheckBox8States.png`, `IA_B_Radio8States.png`).

**The four-colour tail is disabled / normal / rollover / depressed**, in that order, confirmed by
`[GLOBALVARS]`' own `G1=DISABLED / G2=ACTIVE / G3=ROLLOVER / G4=DEPRESSED` and by the
`MessageBox` rows that spell the tail as `<DISABLED>,<ACTIVE>,<ROLLOVER>,<DEPRESSED>`. The comment
block's rule that colours are only meaningful beside a `ResID` holds in the data: the words are
painted into the plaque art on rows without one.

### Panes and movies

`NumFrames` on a `P` row divides its art the same way a button's does; it ranges from 1 to 50.
`Volatile` marks a pane whose picture is replaced at runtime. `Loops` on an `M` row is `0` for a
continuous loop and `1` for play-once, matching the sound object's documented `0 = continuous`.

### A sub-screen's pane is centred, and its section's rows are relative to it

A section whose art is smaller than the 800x600 board authors that pane at `0, 0` all the same,
and its script centres it: `PLANENAME.SCRIPT` initializes `pn_p_background` with `relative = 1`
and then sets the screen's own `location` to `((getresx() - LA.WB) / 2, (getresy() - LA.DD) / 2)`,
its width and height. Every other row of the section carries `relative = 1` too, so the small
coordinates on them (`PN_E_NAME` `23, 40`, `PN_B_OK` `74, 130`) are measured from that corner and
not from the screen's. `PX_PlaneNameBackground.Png` is 264x177, so the dialog lands at `268, 211`.

The messagebox is the same rule, and `MESSAGEBOX.SCRIPT` spells it out: its 410x300
`MB_Background.png` is authored at `0, 0` and drawn at `195, 150`, and `MB_P_ICON` `36, 65` and
`MB_T_MESSAGE` `94, 70` sit on the pane. That section carries three widget sets under the prefixes
`mb_`, `mp_` and `ma_`, and the script centres whichever one it picked by that set's own background,
so the `ma_` set's 505x416 `CR_AboutMessageBox.png` lands at `147, 92`. A section's full-page background (`PX_BackGround.jpg`,
`PS_BackGround.jpg`) is board-sized and centres onto its own corner, which is why the two rules
cannot be told apart there.

### An `E` row's `X` and `Width` may be the script's, not the file's

`PX_E_NAME` is authored `X=0`, `Width=0` on a screen where the box plainly is neither, because
`PLANECONSTRUCTION.SCRIPT` computes both after `initialize`: `R = QNA.location.x + QNA.WB` (the
PLANE NAME title's corner plus its drawn width), `SNA.WB = 302 - R`, `SNA.location.x = R`. So the
box begins where the title's text ends and runs to 302. A zero in either column on an `E` row is a
placeholder for a runtime value, not a control of no width at the board's edge.

### Lists: `ItemHeight`, not box height

The `Height` column of a `D` or `L` row is **one item's height**, not the control's. The visible
box is `ItemHeight x TotalDisplayed`. Three independent rows agree on a 140-pixel list area built
two different ways (`20 x 7` on the campaign roster, `35 x 4` on the save and load lists), the
`[GLOBALVARS]` names for the column are item names (`STDITEMH,15`, `PF_DROPH,17`), and the paint
screen's decal pickers use `73` for a two-row thumbnail strip.

**`TotalDisplayed` is that window, not the item count.** `ia_d_planep` and `ia_tl_contents` are
the two rows where the two differ; [instant-action.md](instant-action.md) records the same trap.

Sibling dropdowns on one screen carry *descending* `Z` values (`155, 150, 145, 140` on the four
ammo pickers, `100 … 70` on the seven video pickers) so that an opened list draws over the rows
below it.

### Sliders: two art files and a widened press region

A `Z` row carries no `Width`, no `Height` and no frame count. **The slot's rectangle is `RegionArt`'s
own pixel size at `X, Y`, and the thumb is `SliderArt` at its own size**, each one image rather than
a strip, so a thumb has no rollover or depressed frame to draw. All four shipped rows name
`PF_B_SliderSlot.png` (`171 x 3`) and `PF_B_Slider.png` (`43 x 21`), a thumb seven times the slot's
height standing centred on it, and all four run `MinValue 1, MaxValue 100, CurrentValue 50`.

**`Left, Top, Right, Bottom` on a `Z` row are insets from that slot rectangle into the region a press
has to land in, negative outward.** The region runs from `X + Left, Y + Top` to
`X + Width - Right, Y + Height - Bottom`. All four rows author `0, -10, 1, -10`, which turns the
3-pixel slot into a 23-pixel region reaching ten pixels above and below it. That is the only reading
of those numbers that leaves the 21-pixel thumb pressable: read the other way round, as outsets added
to each edge, the top would move down ten and the bottom up ten and the rectangle would invert.
`Right 1` stops the region a pixel short of the slot's right edge, which is where the thumb's own
extent ends when it stands at `MaxValue`.

The three `[@Audio@]` volume rows and `CP_S_MOUSE` in `[@ControlsPrefs@]` are the four rows that
exercise this, so the fields are not an audio-only convention. What a `B` row's four columns of the
same name mean is a separate question the shipped data still cannot answer;
[Evidence & limits](#evidence--limits) records that one as open.

### Strings

`ResID` is a symbol, not a number. It joins through `ASSETS/SCRIPTS/RESOURCE.H` (`#define IDS_…
<id>`) to a numeric id and then to the string table in `BINARIES/langui.dll`, which
`ExtractRof.ps1` emits as `ui_strings.json` ([strings.md](strings.md)).

`!` in the field means the script supplies the string at runtime; empty and `0` mean the widget
carries no string. **Keep the symbol as well as the resolved text** so a missing string is
diagnosable rather than invisible.

⚠ **`RESOURCE.H` aliases ids, and `ui_strings.json` keeps only one symbol per id.** Three ids
carry two symbols each (`IDS_IA_CONTINUED` and `IDS_IA_B_LOAD` are both 1072). Joining a layout
symbol through `ui_strings.json`'s `symbol` field therefore loses aliases; join through
`RESOURCE.H`'s symbol → id map and then id → text.

### Art named outside the layout

The layout names 124 distinct art files. The rest of what a screen loads is named by the scripts,
or built at runtime by string concatenation, so **an asset manifest cannot be generated from
`LAYOUT.CSV` alone.** The decode carries both halves: complete names (`assets/sounds/mouseclick.wav`,
`assets/graphics/activepointerz.png`, `assets/graphics/arial8.tga`) and the fragments a runtime
name is assembled from (`assets/graphics/pc_p_hangar` + airframe + `.jpg`,
`assets/graphics/scrapbook/`, `assets/graphics/mpg/`).

Adjacent string literals in a script concatenate: `"assets\\sounds\\" "mouseclick.wav"` is one
path.

### `SCRAPBOOK.CSV`

The same sectioned-CSV grammar, one `[SCRAPBOOK]` section, 461 rows keyed
`<mission>_<spread>_<item>`, and a 16-field record its own comment header documents accurately:
`Objective, ResourceID, ImageName, ImageType, X, Y, Alpha, Width, Height, DrawOrder,
"Left,Top,Right,Bottom", Zoom, ZoomX, ZoomY, TitleResID, TextResID`.

⚠ **Field 11 is quoted and contains commas.** A splitter that ignores double quotes produces 19
fields and silently shifts every column after it.

### The patch overlay

`crimptch.rof` unpacks to `_crimptch/` and carries exactly one member,
`ASSETS/SCRIPTS/AIRFRAME.SCRIPT`. **The patch wins**: the engine reads the patch archive after the
base one. Nothing in the extracted tree states that, so the decode does: `menu_layout.json`'s
`patchOverlay` block names the root, the rule and every shadowing member.

### The emitted artifact

`extracted/rof/menu_layout.json`, artifact schema 1 (the extraction stamp's schema is a separate
integer, see below), about 430 KB. Top-level keys:

| Key | Holds |
|---|---|
| `schema`, `sources` | the artifact version and the files it was decoded from |
| `patchOverlay` | `root`, `rule`, and the members that shadow the base tree |
| `widgetTypes` | per type: the field order above, each field's `kind` (`int`, `color`, `art`, `resid`, `script`, `bool`, `text`) and whether it is optional |
| `globals` | the `[GLOBALVARS]` macros, in file order |
| `screens[]` | `section`, `script`, `macros`, `widgets`, `scriptWidgets`, `scriptRefs`, `keysWithoutLayoutRow`, `rowsNoScriptCreates` |
| `navigation[]` | the 46 `ScriptToExe` edges, flattened, with `priority` and `endScript` |
| `externalAssets[]` | script-named art: `path`, `kind` (`file` / `fragment`), `script`, `present` |
| `scrapbook[]` | the 461 scrapbook rows, keys split into `mission` / `spread` / `item` |
| `missingArt`, `unresolvedMacros`, `unresolvedStrings`, `warnings` | everything the decode could not settle |
| `counts` | the census below, so a consumer can compare a tree without re-counting |

A widget carries `fields` (name → resolved value), `authored` (name → the raw `<MACRO>` token, only
where substitution changed the value), `art`, `resIdSymbol`, `resId`, `text`, `textSource`
(`resource` / `runtime` / `none` / `unresolved-symbol` / `unresolved-string`), `navigateTo` and
`frames`.

Values stay strings. `widgetTypes[].fields[].kind` is how a reader knows to parse one as an
integer or a colour, which keeps the artifact self-describing rather than requiring the reader to
carry its own copy of the field table.

## Reader rules and edge cases

- **Resolve macros before interpreting any field.** A column can be entirely macro-valued.
- **A line with no `=` that is not a section header is a comment.** The shipped file has one
  (`:For about box, these two are dummy objects…`), a `;` typed as `:`.
- **A trailing comma is padding, not a field.** Nine `B` rows and one `Z`-tail `B` row end in one,
  producing an empty field past the declared order. Ignore a trailing empty; a trailing *non-empty*
  field means the reading is wrong.
- **A `B` row has colours or it does not.** 45 rows carry the four-colour tail, 74 do not. There is
  no partial tail.
- **Emit an unresolvable macro's raw token, never a guess.** The shipped file happens to resolve
  every token it uses, but the rule is what makes a future divergence visible.
- **A key a script creates need not have a layout row.** `MAINMENU.SCRIPT` creates `mm_t_title`
  and fills it from `uiData` 2152; `[@MainMenu@]` has no `MM_T_TITLE`. The text exists with no
  authored geometry, so a decoder keyed on the layout alone does not know the widget is there.
- **The reverse mismatch is mostly an artifact of concatenation.** Scripts build keys at runtime
  (`sbz_t_title` + a letter, `ol_d_ammo` + an index), so a layout row no script *statically*
  creates is usually created dynamically. Of the four script keys with no layout row, three
  (`sbz_t_title`, `sbz_t_caption`, `sbz_t_text`) are such prefixes and only `mm_t_title` is a real
  widget with no row.
- **`ASSETS/BINARIES/` is empty in the archive.** `GLOBALS.SCRIPT` names
  `assets\binaries\langui.dll`, which ships as a loose file on disk at the same path, so that one
  script-named asset reads as absent from the extraction and is not a fault.

## Evidence & limits

**The census, from the retail install.** 34 screens over 61 scripts; 636 widget rows in 10 types;
186 macro definitions (29 file-wide, 157 per screen), all resolving; 46 navigation edges; 124
distinct art references, all present once the extraction has run (the two that are not in the
archive are `CrimFlag.MPG` and `Final.MPG`, which the extraction copies in from the install's loose
`GRAPHICS\MPG`); 152 `IDS_*` symbols of which 149 resolve; 403 script-created keys of which 4 have no layout
row; 124 script-named external asset entries covering 50 distinct complete names and 8 name
fragments; 461 scrapbook rows; 1 warning (the stray line above).

**`S`'s eleventh column is unproven.** The `S` list loses exactly one of the comment's `TabOrder`
and `ResID`, and all eight shipped rows leave the surviving column empty, so the data cannot say
which. It is read as `ResID` on the file-wide rule that no row carries a `TabOrder`.

**`Z`'s third column is odd.** All four slider rows carry `100` where the order puts the Z-order,
against `0`-`2` on most other widgets and `70`-`155` on stacked dropdowns. The count matches the
comment exactly, so there is no basis for shifting the list; the value is simply high.

**A `B` row's `Left/Top/Right/Bottom` are exercised six times.** Only the `PlaneConstruction` tab
buttons set them, all to `0,<V5>,0,0`. The comment does not say what they are and six identical
rows cannot show it; they are carried through unread. A `Z` row's four columns of the same name are
read as insets into the press region, on evidence a slider row carries and a button row does not
([Sliders](#sliders-two-art-files-and-a-widened-press-region)), and that reading does not extend to
these six: all six values are `0` or one macro, so they would look the same under any of the
readings a slider row rules out.

**Twelve colour tokens are byte-rotated.** Four rows in `[@Hangar@]` and `[@PlaneName@]` author
colours with the alpha byte last (`0x76767600` where every sibling row writes `0xff767676`).
Treat the columns as ARGB and the four as authoring slips: an alpha of `0x00` on a button that
plainly draws is not a reading.

**The `V`-beats-`G` precedence is a convention, not an observation.** No section macro in the
shipped file shares a name with a `[GLOBALVARS]` macro, so the file never exercises the order. It
is pinned by a hand-authored fixture instead.

**The five macros the campaign board page measures off screenshots are defined in the file.**
[`org/campaign-board.md`](../org/campaign-board.md) measures `<GX>`, `<Y>`, `<V2>`, `<V3>` and
`<V4>` against reference screenshots. They are ordinary
per-section `V<n>` definitions (`[@FlightCheck@]` gives `GX,553`, `V2,132`, `V3,350`,
`V4,FC_B_PaperButton.Png`; `[@Campaign@]` gives `Y,547`), and the file's values agree with the
screenshot measurements to within a pixel, which is a useful cross-check of both.

**The extraction stamp is schema 3, and each bump waited for a reader.** Adding an output does not
invalidate an existing extraction until a reader requires it. Schema 2 is this artifact: the runtime
reader (`CSVM/src/UI/Menu/MenuLayout.cs`) requires it, so a tree extracted before the decode is
reported as stale at boot rather than read as an empty menu. Schema 3 is the ten `.mpg` files: the
copy step that puts them in the tree shipped first and moved nothing, and the number moved when the
front end began playing one. `$StampSchema` in `ExtractAssets.ps1` and `ExtractRof.ps1` and
`ExtractionStamp.Schema` are one number in three places, which
`CSVM.Tests/ExtractionStampTests.cs` reads all of. The layout reader itself treats a missing or
unreadable file as a reason to fall back, never as a layout with no screens.

**Reader-side notes.** The reader keeps values as strings and types them on demand through
`widgetTypes[].fields[].kind`, so a field is only ever read as the kind the artifact declares; a
`B` row with no colour tail simply has no `ColorActive` field. Keys are matched
case-insensitively, since scripts lowercase the keys the layout writes in capitals. The pointer
bitmaps are taken from `externalAssets` (the globals script names them), not from any layout row.
An art file's pixel size is not in the artifact; a consumer that needs a widget's rectangle
measures the strip and divides its height by `frames`.

**Interaction is not decoded here.** A rectangle, a frame count and a colour prove composition.
What a press does, what a rollover changes and what is disabled when come from the scripts, the
executable, and the owed captures listed in [`org/menu-inventory.md`](../org/menu-inventory.md).
