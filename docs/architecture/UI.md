# UI

The launchscreen and splitscreen rig, the in-flight pause and results boards, plus the interactive debug labs. Six sub-namespaces, one folder each, beside the `UI.Menu` presentation tree (`docs/menu-presentations.md`), and one page for all of them: `UI.Boards` (the widget library every screen draws with: the composed board, its view, fit and palette, the board menu, the splitscreen rig and the canvas-layer order), `UI.Campaign` (the campaign pages, their flow and the scrapbook), `UI.Screens` (launch, boot, cinema, load, pause, results and wrap-up boards, and the seat input they poll), `UI.Hangar` (the hangar pages and the plane-picking tables they share), `UI.Overlays` (the debug and HUD overlays) and `UI.Labs` (the inspection labs). Nothing else in `UI` names `Labs`, `Hangar` names nothing else in `UI`, and `Campaign`, `Screens`, `Overlays` and `Labs` are built from `Boards`. Every lab has a scripted `--debug-*` twin so a finding can be reproduced headlessly; see `docs/cli.md`. The module index in `docs/architecture.md` groups the entries by sub-namespace.

One `## src/...` entry per module, body at most 8 lines, 12 for the highest-traffic modules.

Traps do not live here; the rule is in `docs/architecture.md`.

## src/UI/Screens/LaunchMenu.cs
The Built-in presentation's launchscreen: one CanvasLayer holding the whole screen graph and every
Godot control behind it. Mode leads to Chapter and Plane for Free Flight and Dogfight (whose Chapter screen also steps Dogfight's two match rules as rows below the maps), and to
Instant Action's own wizard; the Options, Controls, hangar and campaign doors hang off the same
graph. It owns the drawing, the per-seat `MenuInput` polling, the join scan, the screenshot key and
the mouse (player 1's rows take Godot's hit test through `gui_input`, folded into the next frame's
step, Accept and Back), and nothing else: rosters, seats, picks, gates and the typed exit are
the host's features (`Menu/MenuHost.cs`), the layout is `MenuZones`, and the hangar and campaign
screens are `HangarFlow` and `CampaignFlow` drawn through `ComposedBoardView`, whose `Film` owns a frame before any screen reads it. On the campaign boards player 1's L / Y opens and closes the co-op network door, whose band and remote guests' chips ride the chip strip; a co-op guest's Continue leads to the Network screen's waiting mode. Its Ammo Selection rows stand on the flown build's own fit, and `AmmoPylons` leaves out a pylon that build never bought, since the original draws no field for one. Contract: [../menu-presentations.md](../menu-presentations.md).

## src/UI/Boards/MenuZones.cs
How the launchscreen's three bands divide a window: a header and a footer held at the heights their
own metrics ask for, the middle taking the rest, and one scale all three share, capped so the
tallest screen still fits instead of losing its footer. Pure and public, so the division rule tests
with no menu instance behind it (`CSVM.Tests/MenuZonesTests.cs`).

## src/UI/Menu/InstantActionPresets.cs
The original's Instant Action Table of Contents as data: the 19 preset scenarios, in the shared menu
namespace beside the feature that applies them. Each row is transcribed by NAME rather than by the
record's own dropdown indices, so it diffs line for line against the decode; `Resolve` does the
name-to-index step against `InstantActionFeature`'s rosters, every presentation's single source of
order. Presets fly stock airframes, so nothing here waits on the hangar. The record layout, the
offsets, the sentinel substitution for an unused wave slot and each preset's configuration are in
[../formats/instant-action.md](../formats/instant-action.md), "Table of Contents presets".

## src/UI/Hangar/PlanePickerRoster.cs
The picker roster rule behind every human plane pick, engine-free so it tests without a menu
instance: `Build(stock, customs)` lists the stock rows in their given order, then one row per saved
`CustomPlaneDef` in the store's name-sorted order, each carrying its store name and its airframe's
stock node, skipping a campaign plane nobody has exported. `AirframeNode` and `AirframeOf` are the
airframe-id to `player_*` node table and its inverse, `IndexOf` the after-build auto-select's
case-blind lookup. Deliberately not `Flight.Airframe.PlaneRoster`, which answers "which plane does player N
fly" off a `SessionSpec`: this is the menu-side list, that one the session-side read. Tests:
`CSVM.Tests/PlanePickerRosterTests.cs`.

## src/UI/Hangar/HangarFlow.cs
The Build Custom Plane flow, Built-in's walk of the shared `HangarFeature` (`Menu/HangarFeature.cs`),
engine-free the way `BoardMenu` is: the launchscreen owns the Godot controls, the feature owns the
scratch plane, the rules and the store operations, and this file owns the screen order, the cursor
and the pages. `Order` is the original's nine screens and `PageFor` maps each to its `IHangarPage`,
the mount point handing the shell rows, a detail line, a stepper, the totals line's subject, each
row's would-be cost and optional art; over a campaign the flow adds the wallet line beside the
totals and the mark on a row the funds cannot cover. Nothing is written until `Commit()`, so
cancelling is residue-free. Screens, economy and the cash note: [../org/hangar.md](../org/hangar.md).

## src/UI/Hangar/Hangar*Page.cs
The eight hangar screens the flow walks, one file each, every one a `HangarPage` editing
`HangarFlow`'s scratch plane and priced through `HangarEconomy`: airframe (eleven rows, raising the
defaults ask on a swap that changes an edited build), engine (the airframe's six plus the explicit None row), armour (four zones
on the dropdown's units-times-five scale), guns (four slots stepping the eleven-entry calibre
cycle), hardpoints (a count per wing), paint (a pattern, three colour and shade pairs and three
decals over a live preview), name (two word lists, or typed over) and purchase (the itemised bill
and the gate in the original's own words). The plane-selection screen is `HangarFlow`'s own.
Strings, dropdowns and prices: [../org/hangar.md](../org/hangar.md).

## src/UI/Hangar/PlaneNameTables.cs
The two word lists the PLANENAME screen composes a name from, and the roll across them. Authored
fiction rather than a decode, which is why it is code and not a data table: there is no original
table to diff against, and an unreadable file would leave a pad with no name to offer. Every
adjective is meant to read against every noun, so the pair needs no compatibility table.

## src/UI/Hangar/PlaneDiagrams.cs
The two plane-diagram sheets the original draws beside a fitted aircraft: a plan view and a head-on
view, each one tall PNG of eleven equal frames in airframe-id order. `Frame` slices one airframe's
frame out of a sheet, and a sheet whose height is not a whole multiple of the airframe count draws
nothing rather than a mis-sliced picture. Shared because ammo selection, the campaign's flight
check and the hangar's airframe list all want it; decodes are cached per process, misses included.

## src/UI/Hangar/PlaneFit.cs
What one campaign aircraft is carrying: the four gun slots, barrels by calibre, hardpoint count,
armour units and engine id. Resolved from its hangar build where it has one and from the stock fit
where it does not, which is the case for the profile-seeded starters and every granted reward
aircraft. Engine-free, so the screens that print it test off engine. The caller resolves the build,
never this class. The wallet and the award templates: [../org/hangar.md](../org/hangar.md).

## src/UI/Hangar/PlaneRatings.cs
The four ratings the plane selection screen prints beside an aircraft, each a 0-to-4 index into
langui 501-505, Poor to Excellent. All four are the original's own integer arithmetic over one plane
record and its airframe's stat row: the engine's power for speed, the armour units, the agility stat
and what the armament weighs for offense. The four formulas and the aircraft they reproduce:
[../org/hangar.md](../org/hangar.md), "The four rating words".

## src/UI/Campaign/CampaignFlow.cs
Built-in's campaign screen graph as one engine-free flow over the shared `CampaignFeature`
(`Menu/CampaignFeature.cs`), the same split `HangarFlow` makes over its own feature: the feature
owns the profile, the seated player and every write into the store; a page owns its rows and its
navigation; the launchscreen owns every Godot control. Screens are a stack rather than a fixed
order, since the campaign's navigation is a graph, and `Registry` maps a `CampaignScreen` to its
page factory. A page contributes pictures, strokes and captions and names which authored button
each row presses; `CampaignBoards` supplies the geometry through `Layout`, which is Built-in's
alone. `Modal` and `Message` are the dialog and the refusal band every screen shares; `OpenCabin` is every door onto the cabin, RETURN TO CABIN and the back press included, and `OpenScrapbookAfterMission` the mission end's door onto the book, each playing one of the feature's two cinemas through `Film`, the span (`CinemaHandoff.cs`) a polling presentation reads before it applies a frame.

## src/UI/Menu/CampaignFlightField.cs
Owns a campaign sortie's humans as part of the shared `CampaignFeature` (`Feature.Field`, in
`CSVM.UI.Menu` so either presentation walks the same field): joined count, the flight check showing,
and each guest's pick. Player 0 keeps the seated profile's aircraft; later players fly
session-scoped stock records or copies, so a guest's edits reach no profile store. A rebuild (a
mission launch, a hangar visit, a re-seat) carries each guest's pick and its fit over, so the run's
later missions open on what they chose. `Advance`/`Retreat`/`Rewind` walk one reused flight-check
page through the field, and the seated player's first FLY MISSION latches `Locked`. `Taken` and
`Choose` are the no-duplicate rule, stock picks compared by airframe and profile picks by name.

## src/UI/Campaign/Campaign*Page.cs
The ten campaign screens, one file each, every one an `ICampaignPage` over `CampaignFlow`: the
player roster with its name field and confirmed delete, the cabin hub, the memento chooser its wall
opens over `Session/Campaign/CampaignMementos.cs`, the previous-missions contents list, the briefing with its
revealed map and parchment note, the flight check, ammo selection, plane selection with its ratings
and export, the scrapbook and one scrap's zoom view, whose torn mount over a photograph is the
book's sepia. Each names its own `LAYOUT.CSV` script and reads geometry through `CampaignLayout`, so
a page holds rows, detail text and its own refusals and nothing about pixels. The chrome:
[../org/campaign-board.md](../org/campaign-board.md) and [../org/debrief.md](../org/debrief.md); the scripts: [../formats/campaign-screens.md](../formats/campaign-screens.md).

## src/UI/Campaign/CampaignCombo.cs
A campaign screen's drop-down field (`PS_D_PILOTPLANE`, `OL_D_AMMO0`): the authored rectangle, the
row height and visible row count the list opens at, the window that scrolls when the cursor leaves
it, and a closed field's horizontal step. It never moves its own pick; a candidate comes back and
the owning screen selects, because some screens refuse some picks.

## src/UI/Campaign/CampaignModal.cs
The dialog a campaign screen raises over the composed board, the original's `messagebox.script`: a
message, one button and the callback its answer runs. Held by `CampaignFlow` rather than by a page,
since two screens reach the same box.

## src/UI/Campaign/CampaignTextEntry.cs
A campaign screen's one-line text field, the original's `cm_e_name` edit box: typed from the
keyboard and stepped through one alphabet from a pad, so the field needs no keyboard at all. What it
accepts is the shared feature's own name rule, so a stepped or typed name is always one the feature
would seat.

## src/UI/Campaign/CampaignAidScript.cs
The input script a campaign screenshot aid's `--menu=` colon argument spells, replayed on the flow
where the walk left it: counted cursor verbs, a confirm, a back and a secondary press, joined by
`-`, plus a word naming a `BoardButton` to focus and confirm. A confirm is what lets an aid leave a
drop-down standing open, which a step count could not reach. A count with no verb after it is a run
of downs, so a bare number is the step count it always was, and `export` is a button word rather
than a case beside the language. One grammar serves both presentations, and the replay refuses a
script spelling a press the running one lacks, which is how the secondary verb fails loudly under
Original. `docs/cli.md` states the grammar for the command line.

## src/UI/Campaign/ScrapbookComposition.cs
The scrapbook's per-spread scrap layout, read from the shipped `SCRAPBOOK.CSV` rather than invented:
`Items` walks a spread from item 1 and stops at the first missing key, the way the original's reader
does; `Pictures` gates each row against the mission's merged best-to-date mask and stacks the
survivors by draw order; `Openable` narrows the same gate to the rows that open a detail view;
`ZoomFamily` reads a family's three text boxes, each with the height its row authors beside its
width, which is what the zoom page's blocks are fitted against. A player capture resolves through a caller-supplied
path rather than the asset library, is forced into the page's own 164x123 region so the smudge cut
for that rectangle lands on the print, and is skipped when no file is there. Parsed rows are cached per file behind a lock. The columns and the gate: [../formats/campaign-screens.md](../formats/campaign-screens.md).

## src/UI/Campaign/ScrapbookExport.cs
EXPORT TO DESKTOP's copy: the open scrap's own file to the desktop under its own base name,
overwriting, answering whether it landed and either the name or the OS reason, which are langui
705's and 706's arguments. Engine-free, and the folder is a parameter so a test writes elsewhere.

## src/UI/Boards/LanguiFace.cs
A langui `[FONTID]` tag read as a typeface: the family letters resolved to the Windows face they
abbreviate, the point size, and the bold and italic suffixes. `Pixels` is the size in board pixels at
96 dpi, which is also the pitch the original sets that face's wrapped lines at. Engine-free; the
renderer decides whether the machine has the face. The tag table and the size rule:
[../formats/strings.md](../formats/strings.md).

## src/UI/Boards/ListWindow.cs
A scrolled list as a pointer sees it, in the board's authored pixels: the window's box, the thumb's
box on its track, and where the list stands inside it. `TopAfterWheel` steps the window by rows and
`TopAfterDrag` maps the thumb's free run down the track onto the rows the window can move, both
clamped. `ThumbHeightFor` and `ThumbYFor` are the one rule every list draws its thumb by: the share
of the list the window shows, floored at the scroll tile's own height and capped at the track, so a
thumb's length says how much is in view and a longer one shortens the run the drag divides by. Each
list widget builds one from its own geometry and the presentation that owns the pointer decides what
a new top writes back, so the arrows and the keyboard keep their own rules.

## src/UI/Boards/SliderTrack.cs
A continuous control's track as a pointer sees it, the list window's opposite number: the slot a
thumb slides along, the thumb's own size, and the whole numbers the slot spans. `ValueAt` reads a
pointer's X as a value and `ThumbX` puts the thumb back where it read, so the drawn thumb sits under
the finger that moved it; `Stepped` moves a value sideways. Every answer is clamped into the range
and none wraps, which is what keeps a step away from an end from landing on the other one. Each
slider widget builds one from its own geometry and the presentation that owns the pointer decides
what a new value writes back. Driven by `Menu/Original/SliderControl.cs`.

## src/UI/Boards/BoardFit.cs
How the original's fixed 800x600 campaign dialog space lands on an arbitrary window: one uniform
scale on both axes, the board centred, the remainder letterboxed. A `record struct`, so every
element goes through the same mapping and only the scale changes; a viewport with no area falls back
to 1:1 rather than a scale nothing can draw at. The rejected alternatives and why the art is sampled
nearest are in [../org/campaign-board.md](../org/campaign-board.md), and bind every campaign screen.

## src/UI/Boards/ComposedBoard.cs
What a composed campaign screen is made of, engine-free: the screen's fixed backdrop, the fills a
page paints on it, pictures at authored pixel positions, connector strokes, text lines, button
plaques and flowed list widgets, each in draw order. The backdrop is its own layer so a fill can
sit over the background and stay under the page's pictures, where a selection bar goes. `BoardNote`
is a widget's entries plus its wrap box (cut at a word where the box has no room for the rest, shrunk
to a face the whole list fits in, or no box at all where the widget's own list stops nowhere), its
marks, and `BoardCaret` an edit box's cursor on the line it follows, all placed by a caller that can measure text. A `BoardLine` carrying a `Height` names the box its block is fitted to, the renderer stepping its face down until the block fits, since only the renderer can measure. `PlaqueFrame` and `PlaqueInk` are a plaque's states, and a plaque whose art leaves
part of its frame empty carries its label's own baseline. `BoardArt` names a file and its frame count and the renderer resolves it; one of its libraries is a movie, so a background film reaches the backdrop with no engine type here, and one is an image already in memory (`Held`), a stunt photograph's thumbnail. `BoardCrop` takes a region of the source instead of the whole frame, which is a chart sheet's own window. A `BoardLine` carrying a `Glyph` names the pad button drawn where `GlyphSlot` stands in its text, the renderer owning the gap after the picture since only it can measure one, and a `BoardFill` carrying an `Ink` takes the palette's colour instead of three bytes, which is what a seat chip paints with.

## src/UI/Campaign/CampaignBoards.cs
The fixed chrome of all eight campaign screens, plus the composer that turns a page and a cursor
into a `ComposedBoard`. Every button slot, background pane and text slot names its `LAYOUT.CSV`
section and row and reads through `CampaignLayout` with the value the board drew before the layout
existed as its fallback, so a screen composes the same with or without the file; the briefing's
chrome is `Briefing.zrd`'s own, and a slot marked pinned keeps a measured value instead. `SlotOf`
and `DialogSlot` answer a plaque's rectangle for a pointer to hit-test, `DetailSlot` and
`DetailPaned` the description panes, and `DialogChrome` the messagebox widget set a box draws and
where its pane lands. The pinned values: [../org/campaign-board.md](../org/campaign-board.md).

## src/UI/Campaign/CampaignLayout.cs
The decoded menu layout as the campaign boards read it: one widget row's authored geometry and art
by section and key, every read taking the value the board drew before the layout existed as its
fallback. Engine-free, over `Menu/MenuLayout.cs`. `At` and `Box` answer with the whole row or the
whole fallback, never one coordinate from each, so a row missing a column cannot shift an element
half-way. `For(dataRoot)` reads the extracted layout once per data root and keeps it; a missing or
unreadable file is the `Fallback` instance with its reason logged once. The file's own sections and
keys: [../formats/menu-layout.md](../formats/menu-layout.md).

## src/UI/Screens/InstantActionWrapupPage.cs
The Instant Action wrap-up page's own content, engine-free over `CampaignLayout`: the heading, the four title/value pairs at `[@IA_WrapUp@]`'s authored rows, the magazine spread and the four
brushstrokes, the CONTINUE plaque's art and corner, and three pieces of remake furniture. `PostIts` writes the further lines the shipped page has no row for (the context naming the chapter and the
mission type, and the stunt run's splits without their total, which is the time row's figure again) onto yellow post-its: the first under the last value row and clear of the plaque, each further
one to its left, the lines shared out evenly and each post-it as tall as what it holds. `Prints` lays a stunt run's photographs out in marker order to the left of the post-its, in the grid `ShotGrid` picks for the room. `TickStrokes` draws the outcome as a box above CONTINUE, ticked on a win and empty on a loss. Every value is
read off the `IaWrapupSnapshot` the ending froze and never recomputed; the geometry is read off the page's own rows, so a layout that spaces them differently moves the furniture with them.
`Sample` and `LongSample` are the stand-in runs the `--menu=` aids and the coverage walk stand the page on. What the four numbers count:
[../formats/instant-action/wrap-up.md](../formats/instant-action/wrap-up.md).

## src/UI/Boards/ComposedBoardView.cs
The Godot half of the campaign boards: draws one `ComposedBoard` over the whole window through
`BoardFit`, with texture filtering pinned to Nearest so the authored pixel grid stays hard. Owns the
texture cache and the only art resolution there is, mission art and screen chrome under their own
extraction roots, and caches a miss so an absent extraction is probed once per name. A movie resolves
to a `MovieSurface`, whose one texture the cache holds and the surface rewrites in place, so the
picture animates with nothing invalidated; a held image gets one texture per image, dropped once a shown board stops drawing it; `AdvanceMovies` runs their clocks off the caller's own step and
`AdvanceCaret` blinks a text cursor off it, each saying whether to repaint. A line naming a `LanguiFace` draws in that installed Windows face, cached per tag, and keeps the board's own where the machine lacks it; a pitched block honours authored line breaks and indents and justifies as a whole, its lines left-aligned under the widest. Supplies the font metric a flowed
`BoardNote` and a caret cannot take, `Fitted` shrinking a note's face until its list fits its box rather than losing a row and stepping a `BoardLine` carrying a box height down a point at a time until its wrapped block fits, `Block` being that measurement on its own, the two-line hint band a pad needs, and `ArtSize` for a caller that must clip against a bitmap's own authored width. `PresentMoving` is the one repaint a caller holding the frame loop can still make: its pictures go on a canvas item of the view's own, fitted by the same maths and re-fitted on a resize, rather than through a queued redraw callback the blocked loop would never reach, so a load screen's build can move the bar it draws. A line carrying a glyph is drawn through `ControlLine`, the composition the flight prompts already use, so the picture and the gap around it are measured in one place and a board composer never spaces them itself.

## src/UI/Screens/CinemaScreen.cs
One cinema on screen: a `CinemaPlayback`, the `ImageTexture` its pictures upload into, and the
`AudioStreamGenerator` its samples are pushed to on the Voice bus, a cinema being a narrated film
rather than score or world sound. The picture fills the same 800x600 rectangle `BoardFit` maps a
board into, so a cinema and the screen it hands off to own one area of the window. `Open` answers
null for a file that will not read, `Ended` is how a flow learns it stopped, and `CinemaSkip` is
which presses end it early, the per-cinema differences there being the original's own. It mounts
itself on `HudLayers.Cinema` and frees itself; `Session/Launch/Launcher.cs`'s `PlayCinema` is the seam.
The three authored sets live here as constants and `CinemaSkips` answers them.

## src/UI/Screens/CinemaSkips.cs
Which press ends a cinema, for every screen that offers a skip. `Skips` is the one member that
decides, and it takes a `CinemaPress` rather than a device event, so all three sets are pinned off
engine; `PressOf` is the engine's half, reading a key, a click or a pad button into one of those and
holding no policy of its own. A press is not a set: only an any-press set takes a key with no name
of its own. A pad button is in every set, because a player holding one has no other press to offer
and would otherwise sit through a 145-second film; it counts only where pad input does at all
(`--no-pads`, an unfocused window), since a pad reports its first button as it connects. What each
cinema's set is, and why they differ, is [../formats/cinemas.md](../formats/cinemas.md).

## src/UI/Screens/CinemaHandoff.cs
What every cinema flow shares. `CinemaPlay` is the shape of the call that puts a film on screen, which
`Session/Launch/Launcher.cs` satisfies by handing over `PlayCinema` itself. `Once` wraps the continuation a film hands off to: a
skip can land on the frame the film plays out and both paths end it, so the next screen opens once however many times the
cinema reports it stopped; the boot block, whose continuations start the next film, chains unwrapped. `CinemaFilm` is for
the screen a film stands in front of rather than a flow that chains them: `Play` spans one film, `Up` says the film owns
the frame, and `Swallows` says this frame is the tail of the press that ended it, the pointer's lasting until the button
comes up and every other press spent where it lands. A screen without it reads the release of a press it never saw go
down as a gesture of its own. Which presses end a film is `CinemaSkips.cs`'s, not this file's.

## src/UI/Screens/BootSequence.cs
`fmv.zrd`'s boot block with no engine in it: `Card` composes the copyright card in the authored
800x600 space out of the extraction's own art, message-table strings and font metrics, and `Run`
calls the block's eight actions in the reader's order over three injected delegates, a `CinemaPlay`
for a film, one that puts up a still and one that takes the card down as the first film starts. Every
name, position and duration is the reader's ([../formats/cinemas.md](../formats/cinemas.md)), which
is also where the card's one showing, the unseen fade and the films running back to back are
settled; `Held` is the one member that says how much of an authored hold reaches the screen.
`BootCard` supplies the stills, `Session/Launch/Launcher.cs`'s `PlayCinema` the films.

## src/UI/Screens/BootCard.cs
The boot sequence's engine half, and the only file that knows a boot still is drawn at all: the
black the block runs on, a `ComposedBoardView` for the card, a countdown per hold, and the press
that ends a hold early, read through `CinemaSkips` against the films' own set so a still and a film
answer one rule. It mounts on `HudLayers.Board`, the launchscreen's own layer, so a
film at `HudLayers.Cinema` covers it; the card goes down with the first film and the black outlives
it, and a hold of no seconds runs on without a frame of its own. `Play` is the whole surface: it
mounts the node, runs a `BootSequence` over the caller's film call, and frees everything before the
handoff.

## src/UI/Boards/BoardPalette.cs
The ink a campaign board writes in, one palette per background family, because the screens are
painted art and the grey the flight check's forms use is invisible on the cabin's dark hangar. The
flight check and ammo values are their layout rows' own ARGB fields; the rest are chosen to read on
their background, and [../org/campaign-board.md](../org/campaign-board.md) says which is which.
`EscapeBlackboard` is the one crossing, the load screen's own chalk under the near-black labels the
escape strips' light plates need, which an Instant Action pause is the only screen to want both of.

## src/UI/Boards/SeatStrip.cs
The shape both presentations' player chip strip shares, so the two corners cannot drift apart: the
face, the inset from the top-right corner, the cell a chip centres in where a strip cannot measure
its own text, the ground's margin and height, and the `BoardInk` a seat's chip takes. The tag and
the colour themselves stay `SplitScreen`'s, and `ComposedBoardView` is what resolves a seat ink to
that colour. Built-in builds its chips as Godot labels (`LaunchMenu.cs`); Original composes them as
a board overlay (`Menu/Original/OriginalSeats.cs`).

## src/UI/Boards/BoardMenu.cs
A board's cursor and item list, engine-free so the selection rules test off engine. Holds no input
source: the board polls its owner through `MenuInput` and feeds one frame to `Handle`, which is what
stops a pad steering a menu it does not own, and the return says whether the highlight moved so a
board repaints only when it has to. It opens on the first item, so a board orders its rows with the
harmless one first and a stray confirm on a menu that just appeared cannot destroy a run. A results
board is not dismissable, since dismissing it would leave the player in a halted world with no way
back. `MoveTo` is how a board's pointer puts the cursor on the row under it, refusing a row outside
the list so a miss leaves the cursor alone. Off-engine coverage: `CSVM.Tests/BoardMenuTests.cs`.

## src/UI/Screens/LoadBoard.cs
The load screen drawn over the whole window while a session builds: `LoadScreens`' composition
through `ComposedBoardView`, so it inherits the authored-pixel surface and `BoardFit`'s scaling.
Populated in `_Ready`, since the view sizes itself off the viewport, and it tracks the window every
frame the way every shared board does. Holds no composition of its own, so what the screen says
tests off engine; a campaign launch hands it the `LoadSheet` its story position resolves. It is
also the pump `Utils/LoadProgress.cs` repaints through, installed for its own tree lifetime alone: a step the build reports puts the bar's fill and the propeller's frame on the view's moving layer and presents there, which is how the screen moves at all, a queued redraw being no use while the build holds the loop that would flush it.
`--debug-load` stands the screen over a CLI launch and photographs each presented frame, the only way to read the bar back with nobody at the menu, and `_ExitTree` writes every reported step against the build's own wall clock as one line.

## src/UI/Screens/LoadScreens.cs
What the load screen is made of, engine-free. `LoadSheet` is the campaign screen's authored half,
one `Loading.zrd` dialog with its mission's objectives and the seated profile's own memento; `LoadScreens`
composes either that chart sheet, through `MissionMap` the way `PauseScreens` does, or the Instant
Action blackboard with the four texts its own `loading_i` dialog places; `DialogTexts` takes that composition by file and key, so an Instant Action pause writes its `ia_escape.zrd` dialog's texts through it. The mission type picks the
blackboard's dialog by the exe's own letter; free flight and dogfight are ours, so they write the
mode's name and nothing else. `LoadMotion` is the moving half, the fill strip and the six propeller frames the sheet's own `Cycle` beat names, `Moving` places those two at a fraction and a frame, the clipped fill and the propeller face a pump presents on their own layer, and `Painted` re-lays the same pair into `Overlays` for a caller composing a whole board, so the still composition under them is never rewritten. An absent extraction yields the frame and the bar rather than
throwing, since this screen is shown while everything else is still loading. The dialogs, the beat
sheet and the face mapping: [../org/loading-screen.md](../org/loading-screen.md).

## src/UI/Boards/PauseScreens.cs
What the Original presentation's pause screen is made of, engine-free: the frame behind it, the
mission's chart at its authored source crop, the pins and icons its dialog's script places, the
objectives parchment, the memento, and the labelled button strips, the block's four plus the remake's own PHOTO MODE at the place that block leaves free. An Instant Action sortie's dialog carries none of that and draws the load screen's blackboard instead, its four texts composed through `LoadScreens` and its parchment left off by the dialog's own script.
`PauseSheet` is the authored half, read once per sortie, and `PauseReadout` the live half, read afresh on every
pause: its memento is the seated profile's own picture, `Rows` marks a note line by the runtime's answer for that line's own objective number, and
`Icon` turns one world pose into the chart icon a session and a suite place alike, through the
shared `MissionMap`, which draws nothing for a pose off the window. `RowAt` is the pointer's hit
test over the five 132x28 plates, and a pointer draws the dialog's own cursor; an unreadable extraction leaves the pause to the Built-in board. Decode: [../org/pause-screen.md](../org/pause-screen.md).

## src/UI/Screens/PausePreferences.cs
The Preferences leaf over a paused mission: an `OriginalShell` of its own on the Options screen,
over the held world and drawn through `ComposedBoardView`, its display rows the `DisplaySettingRows`
both Options screens draw. Either pause board's PREFERENCES opens it, the pausing player's reader
drives it, and every door out closes it onto the sheet with an `OptionsApplyExit` already applied;
the halt is never touched. Its other features are throwaways and its `ControlsFeature` the menu's
own, whose `Accepted` it hands to the flying seats `Open` was given (`ApplyProfile`), so a rebind
or mouse scheme takes hold before the resume, as do the head turn and targeting switch it carries.
`Build` answers null with no decoded layout. Decode: [../org/pause-screen.md](../org/pause-screen.md).

## src/UI/Overlays/MissionMap.cs
The one chart drawer every screen showing a mission's map shares, engine-free: the sheet as a
cropped picture, a reveal's visible elements as pictures in placement order over two layers, its
connector lines as strokes, and one icon placed by world position through the map's own window,
turned so its drawn nose reads against the compass: the heading, less however far that bitmap's own
art is drawn off the top of the sheet. It exists as one module because the original reaches all of
it through one control class from two dialog constructors, so the briefing, the pause screen and
the campaign load screen cannot drift apart here.
Decode: [../org/pause-screen.md](../org/pause-screen.md).

## src/UI/Boards/BoardMenuItem.cs
The rows a board menu can offer: Resume, Photo, Restart, Preferences and Exit. The board owning the
menu decides which it carries and what each does; Resume appears only on a pause board, Preferences
only where a `PausePreferences` leaf stands behind it, and Exit's label follows whether the session
can return to the launchscreen or only quit.

## src/UI/Boards/CursorRow.cs
One centred list row with its cursor marker, shared by every menu that has one: the launchscreen's
screens, its per-player aircraft panes, and every board menu through `BoardMenuView`. The marker is
a cell of its own with a mirror cell opposite it, which is what puts a label on the panel's centre
line whether or not its row is selected; the rule and its failure mode sit on the marker itself.

## src/UI/Boards/ControlGlyphs.cs
The per-control picture set, keyed the way `BindingControl` is: `GlyphKey` is a control's kind, its
index inside that kind and the sign an axis binding names, with deadzone and modifiers left out
because they decide when a control fires rather than what it looks like. `ControlGlyphSet` is the
swappable set (which controls it draws, how wide one is at a line's height, how to draw one) and
`ControlGlyphs.Set` the one holder every composing site reads, so replacing the whole look is one
assignment. The shipped `PromptFontGlyphs` draws pad controls as characters of PromptFont (SIL OFL 1.1, `CSVM/data/promptfont.ttf.bin`): its controller-neutral glyphs for the face buttons (the four-button cluster with the pressed one filled), the d-pad, the stick directions and clicks and the three menu buttons, and its Xbox-lettered shoulders and triggers, the font having no neutral ones. A button with no glyph, or every control when the font file is missing, draws as a lettered plaque; it declines keys, mouse buttons and hats, which is how a keyboard seat keeps `BindingLabels`' words. The file carries an extension Godot does not import and is read as bytes, so an export's `--import` leaves the tree clean and `export_presets.cfg`'s include filter packs it. The original ships no such art, so the set and its size on the line are judgements at the controls, not a decode.
`PadA`, `PadB` and `PadStart` are the three buttons named outright rather than through a binding, which is what the
join board's gestures need: a pad with no seat has no keymap for `For` to look a prompt up in.

## src/UI/Boards/ControlLine.cs
One prompt line with one control in it, and the row of them a board's footer is. `Compose` fills the
message table's `%1` slot through `Messages.Fill` with a sentinel, then splits the filled line, so
the halves either side of the control are exactly what a real fill would have written and no prompt
is ever built by concatenation; `Text` is the whole line in words, which is what a suite or a log
reads. `For` picks the binding through `ActiveDevice.PromptBinding`, so one seat names one device.
`Draw` writes a glyph-less line as a single string, the way a plain label always drew it, and only a
line carrying a glyph is drawn in parts. `ControlHintBar` lays several lines out in a row and
centres them in its own box; its items are composed one at a time so the device gate holds per item. `Around` builds the same line from a prefix, a suffix and a control named outright, for a caller writing its own words rather than filling a message template, and a line whose only content is its glyph still counts as something to draw.

## src/UI/Boards/BoardMenuView.cs
Draws a `BoardMenu`'s rows as `CursorRow`s inside the board style all five boards share, so the
cursor reads the same wherever it appears and a layout fix lands once. `Refresh` recolours from the
current highlight, touching only label overrides; `ShowsCursor` false draws no highlight while
the board's cursor stands on its photographs. A results board's footer is a `ControlHintBar`
over the seat's own Select and Confirm, composed off that seat's bindings and device rather than
off the shipped defaults, since nothing else on such a board teaches the cursor; `Relegend`
rewrites the row when the seat changes device. A pause board asks for no footer, as the original's
pause sheet carries none.

## src/UI/Boards/BoardMenuHost.cs
`BoardMenu` plus `BoardMenuView` plus the reader, kept together so a board wires a menu in two lines
rather than restating the poll, handle and repaint order five times. `Build` primes the reader, so a
button still held from whatever raised the board is not read as a fresh press. It reads the pad's
back button alone, Escape and Start reaching the pause toggle through `FlightController` instead.
`Poll` can offer each frame first to a second cursor region on the board (a results board's
photographs), which the rows then do not read. A poll that reports the seat moved device relegends the view, which is the one seam that gives every
results board its control hint; `Build`'s legend flag is how the pause board declines one.

## src/UI/Screens/ShotGrid.cs
The Danger Zone photographs' grid rule, engine-free and shared by the built-in boards'
`StuntShotStrip` and the Original page's `InstantActionWrapupPage.Prints`: `Fit` takes the fewest
rows whose pictures come within `Slack` of the widest any grid in the room allows, capped at the
thumbnail width, and the widest of those, so a few shots read as one strip and a long course wraps
before its pictures shrink. `ShotGridCursor` walks such a grid for a board: sideways in reading
order, up and down to the nearest cell by column, never onto a cell that refuses it (a frame not
yet landed), and off the grid on a step down past its last row.

## src/UI/Screens/ShotViewer.cs
One Danger Zone photograph shown large over the board that opened it, the one viewer both
presentations use: the camera's own PNG fitted to the window on a dark backdrop, with the marker,
the run clock and the way out under it, and the strip thumbnail where the file cannot be read. It
reads no device; the owner decides when it closes. A Built-in results board builds it to take
clicks and raise `Dismissed`, and the Original presentation builds it to take none, since the shell
polls its own pointer and closes it through the wrap-up page's rows.

## src/UI/Screens/ResultsBoard.cs
The shared shell every results board is built on (`StuntScoreboard`, `StuntRaceBoard`,
`VersusBoard`, `IaWrapupBoard`): backdrop and centred panel, palette and label factories, the
halt-and-retire contract on the sim clock, and the standard Photo Mode, Restart and Exit menu. A
panel taller than the window is re-centred and shrunk about its centre to fit. Photographs added
through `AddShotStrip` are the cursor's second region above the rows: up off Photo Mode (the
resting row) enters the grid, confirm opens one in a `ShotViewer` over the board, back or a click
closes it on its cell, and down out of the grid returns to Photo Mode. `PauseBoard` shares the
chrome statics but not the shell, since a held clock is not an ended run.

## src/UI/Screens/StuntScoreboard.cs
Stunt Flying's end-of-run results overlay on `ResultsBoard`'s shell: the plane and chapter heading
over a `StuntSplits` section of per-zone splits, total and best-time comparison, and under it the
pilot's `StuntShotStrip`. Wakes on `StuntMission.RunCompleted`, records through `ScoreStore.RecordIfBest` and logs the split
table so a headless run is reviewable. The one per-pane board among the results boards, which is
why it overrides the shell's whole-window placement. `FlightController` holds a finished pilot's
finish pose and takes R as a rerun only while it has this board, so an Instant Action pilot, who
has none, flies on through the ending's hold. Read `ResultsBoard` for the shared shell and
its halt contract, and `IaWrapupBoard` for the board Instant Action carries the splits on instead.

## src/UI/Screens/StuntShotStrip.cs
A stunt run's Danger Zone photographs as a board section, shared by `StuntScoreboard` and
`IaWrapupBoard`: one captioned thumbnail per shot in marker order, in the grid `ShotGrid` picks so
a long course wraps into rows. `Cursor` walks the landed cells for the board; the pointer reaches
them through `CellPointed` and `CellClicked`. It follows its `StuntCapture` in the tree: a marker
latched on the frame that completes the run arrives after the board woke and `ShotLatched` draws
the grid again with it. A pending cell is drawn empty and filled on `ShotLanded` (the first landing
under a guessed aspect lays the grid out again), and a frame that never arrived is left out.
Hidden while there is no shot.

## src/UI/Screens/StuntSplits.cs
The stunt run's split section, shared by `StuntScoreboard` and `IaWrapupBoard`: the per-zone rows
in the order flown with split and cumulative times, placeholder rows for zones never reached, the
total, and the new-best or stored-best comparison line. `StuntSummary` is the value a board hands
it, one run with its total and the stored best. A single flag keeps the two boards' shipped
layouts apart, since the scoreboard rules off its total and the wrap-up board runs the table
straight into it. `Lines` is the same table as flat text for the Original wrap-up page, whose
total line opens with `TotalLabel` so the page can leave it out.

## src/UI/Screens/StuntRaceBoard.cs
The race's shared ranked results overlay on `ResultsBoard`'s shell: one row per player from
`StuntRace.Standings()` with placing, tag, plane, zones, total and gap to the winner, and a DNF
row for an unfinished run. Whole-window rather than per-pane, since a race ends for everybody at
once. Wakes on `RaceCompleted` and retires once `AllFinished` clears, so the rematch is reachable
without going through the menu. No Instant Action run builds one (`GameSession.RaceBoardFor`):
there the last finish is the mission's win, and the director's hold and wrap-up end the run.
`StuntScoreboard` is the single-pilot form of the same table.

## src/UI/Screens/VersusBoard.cs
The Dogfight results overlay on `ResultsBoard`'s shell: the winner in their own
`SplitScreen.PlayerColor`, or a draw on a tie, over one ranked row per player with tag, score,
kills and deaths from `VersusMatch.Standings()`. Score is the ranked column, kills alone do not
explain it. Whole-window, because the match ends for everybody at once.
Wakes on `MatchCompleted` and retires on the rematch; the rows are populated only from that
completion, so they stay the ones the match ended with even after `Restart()` zeroes the live
state. Restart routes through `GameSession.RestartMatch`, which the keyboard and pad shortcuts
reach directly while the board is up. `StuntRaceBoard` is the same construction over a race.

## src/UI/Screens/IaWrapupBoard.cs
Instant Action's wrap-up board on `ResultsBoard`'s shell, whole-window since the mission ends for
every human at once: four label and value rows for time to complete, enemies shot down, danger
zones completed and shot percentage. It takes no live match object at all, only the caller's own
snapshot handed in once by `InstantActionRuntime`, so `InstantActionDirector` owns every source
and this class draws what it is given. Its static `FormatElapsed` is the decoded time row, which
the Original presentation's wrap-up page prints too. On a stunt mission it also grows a `StuntSplits` section and player 1's `StuntShotStrip` (the one live source, handed over at the wrap-up rather than the
ending so a marker latched after the run completed is on it), and no per-pane scoreboard is built. Its Restart reaches the Launcher's session restart
and rebuilds the world, because a mission's waves, ace and zeppelin cannot be put back in place.

## src/UI/Screens/PauseBoard.cs
The shared pause overlay, whole-window because pausing stops the game for everybody at once. Built
once by `GameSession` on the shared board layer and wired to `PauseState.Changed` rather than a
completion event, it shows the pausing player's tag in their own colour and a Resume, Photo Mode,
Preferences, Restart and Exit menu driven by that player alone, since `PauseState` lets only the
owner resume; the Preferences row is built only where a `PausePreferences` leaf stands behind it. A
fresh menu each pause, so the cursor starts on Resume and a stray confirm cannot destroy a run. Its
menu carries no control hints, the original's pause sheet having none. It shares `ResultsBoard`'s chrome but not its shell, and a campaign session's objectives readout rides
the same pause on a layer of its own. The Original presentation puts `OriginalPauseBoard` in its place.

## src/UI/Screens/MenuInput.cs
One player's menu input source: the keyboard flag, a `Pads` binding and the edge and auto-repeat
state, with `Poll(dt)` filling the cursor axes, accept, back and start out of the `Menu` binding
context (`src/Bindings/`) from three readings of one seat: keyboard live, keyboard minus the
typeable keys, and the pad alone. Its pad rows sit on the seat-local `SeatPads` identity, since a
seat reads a set of pads and no binding may hold a connection index. `Typed` and `Erase` serve a
text field, `PadMove`/`PadMoveX` are the axes such a screen reads instead, since W, A, S and D
are letters there. `TypeableKeys` is deliberately wider than any box's accept rule, and Shift gives each key its US-layout shifted character. `Device` and `DeviceMoved` come from an `ActiveDevice` over a fourth reading, the keyboard half alone, so a board hint names the side the seat last used and knows the tick it changed; `Hint` composes one such line. Wrapped by `Menu/BuiltIn/BuiltInSeat.cs`, bound by `MenuSeatDevices`; it also serves the in-flight boards. Beside all of that stand three static raw pad reads, `JoinPressed`, `SignOnPressed` and `SignOffPressed` for Start, A and B: a pad no seat owns has no keymap, so nothing bound can answer for the join gesture or the join board's two.

## src/UI/Boards/HudLayers.cs
The canvas-layer ordering for everything drawn over the 3D view, in one place, so "does the collider
overlay draw above the cloud whiteout?" is answered by reading one file rather than nine literals.
The order is measured off the original's footage rather than chosen, except for the debug and lab
layers, which the original never had and which sit above the sun wash on purpose. The three negative
tiers are the world seen from the seat: the flare sprites and the cloud whiteout are the sky, and the
cockpit pass draws over both, since the original's whiteout is a fog term the interior never takes.
The burst wash stays above the pass, being a framebuffer effect over the whole picture. The evidence
for the wash-over-HUD ordering is a verification rule; the weather decode is [../org/weather.md](../org/weather.md).

## src/UI/Boards/SplitScreen.cs
The splitscreen rig for two to four players (one player never constructs it): the black gutter
backdrop, one `SubViewport` pane per player sharing the main `World3D`, and the player colour and
tag table. Two panes stack, or stand side by side once each half would still be wider than it is
tall (`SideBySide`, true from 2:1 out); three and four are the 2x2 grid. **Every pane is a 3D audio
listener**, or nothing positional is audible at all: Godot takes the per-channel maximum over
listener-enabled viewports. `Fill(true)` gives pane 1 the whole window for a cutscene (one rect, no
rebuild); `NoteSkip` names a skipping player. `OwnAirframeLayer` is one bit per seat, dropped only by
that pilot's spyglass disc; `PhotographLayer` is one bit no pane draws, for the Danger Zone camera.

## src/UI/Boards/ScreenFlash.cs
The full-screen colour wash, two channels over one hidden `ColorRect` per rendered view. The ramp
channel is the `FBFX_COLOR_FROM_TO` wash a close HE, AP or flak burst authors, lerped in RGBA on sim
time and then ended rather than held, replacing whatever that pane was running, and painted on every
pane whose own camera stands inside the burst def's authored player-range gate. The blend channel is
`BlendWash`, addressed by the struck aircraft's player index and painting nothing for an AI. The two
composite at paint time alone, the blend over the ramp, so a pane with no blend wash paints exactly
the ramp's own colour. One state per pane, never one global: in splitscreen each pane is its own
picture. The wash keys: [../formats/anim-definitions.md](../formats/anim-definitions.md).

## src/UI/Boards/BlendWash.cs
One pane's victim-routed wash state, the original's start and tick routines held per pane instead of
in their one global; pure state and arithmetic, no node, so the rules test off the engine. A first
hit takes its weight as the peak and starts the displayed weight at zero; a hit landing on a running
wash blends both the weights and the colour by the original's own arithmetic and restarts the
envelope without dropping what is displayed. The envelope is attack, sustain and release at fixed
fractions of the duration, stepped on sim time, with a hard cut at the end. `Composite` is the
paint-time rule `ScreenFlash` applies. Decode: [../org/ordnanceTypes.md](../org/ordnanceTypes.md).

## src/UI/Overlays/ObjectivesHud.cs
The flown campaign mission's objectives readout, drawn on the pause screen and nowhere else: the
original keeps its objectives on the pause parchment and leaves the flight HUD to the gauges. Reads
`CampaignDirector`'s `ObjectiveGraph` rows directly, resolves text through the message table, and
shows every row rather than gating on its awake flag, which the member explains; a row with no
message key is dropped from the drawing but still counted. A completed row is marked with the art
(`obj_check1`, loaded once through `LoadMark`) centred on that row's origin, over its leading
characters, and keeps the colour an open row carries. Self-mounting, and one per rig where
`PerfHud` is one per window. Decode: [../formats/objectives.md](../formats/objectives.md).

## src/UI/Screens/MissionEndFade.cs
A full-screen `ColorRect` on a `CanvasLayer` at `HudLayers.MissionEndFade`, polling
`CampaignDirector.LeavingFade` every frame and painting that straight onto the rect's alpha. That
layer sits above the flight HUD and `SunWash` but under `Debug`/`Lab`, so the fade darkens the HUD
and the wash the way the original's copied framebuffer does, while the debug instruments stay
readable through it. This paints live over the running world instead of freezing a copy, since the
hold already stops the sim clock underneath it. Self-mounting, one per rig's `HudParent`, hidden
until the director's fade leaves 0. Decode: [../formats/objectives.md](../formats/objectives.md),
"The mission-end path, and what the player sees after it".

## src/UI/Screens/SessionStartFade.cs
A full-screen `ColorRect` on a `CanvasLayer` at `HudLayers.SessionStartFade`, raised with the load
screen and painting `Utils/StartCover.cs`'s alpha until that ramp is spent. It shares
`MissionEndFade`'s tier, so it covers the flight HUD, the world and the sun wash while the world
assembles and while an intro's camera is still being posed, and the load screen on `Board` keeps
drawing over it. `Build` returns null under `--det`, which is the one gate: nothing stands over a
frame a golden hashes. The launcher builds it, drops it once `Finished`, and hands it the session's
own first-frame answer; `Tick` is public because a suite never yields a frame.

## src/UI/Labs/LiveryLab.cs
The `--viewer` livery editor (key L): a squadron stepper that loads the whole squadron livery,
per-slot RGB sliders, decal steppers, a random livery and copy-CLI-args.

## src/UI/Overlays/NodeLabels.cs
Floating node-name labels in both the static viewer and flight, in a meshes mode and an all mode;
`--debug-names` sets the mode at launch and is the only way in, the labels carry no key. In
splitscreen the nearest and de-clutter pick is player 1's viewpoint alone, while every pane still
renders the resulting labels, since they are ordinary world-space children of the root.

## src/UI/Overlays/DebugMarkerToggle.cs
The all-aircraft markers key (F16): writes one answer to `TargetHud.MarkAll` on every human pane,
the overlay `--debug-markers` switches on at launch. Session-level rather than one handler per
pane, because a splitscreen pane's HUD sits in a `SubViewport` that routes unhandled input to the
window and never to its own nodes. The panes are read through a closure, since a rig's HUD is built
after this node and can go away mid-session.

## src/UI/Overlays/MarkerOverlay.cs
The `--viewer` marker overlay (key K, `--markers` at launch): every firepoint, pylon and target on
the parked aircraft as a coloured gizmo with a billboarded label. Reuses `MarkerRig`'s own
classification and co-location grouping, so its gizmos agree with the marker dump by construction.

## src/UI/Overlays/PhotoModeHud.cs
Photo mode's only screen furniture and its way out: a hint line naming the bindings on a layer of
its own, and the Escape or pad-B read that raises `Exit` for `GameSession.ExitPhotoMode` to act on.
It decides nothing about the mode itself. The hint fades rather than persisting, since the mode
exists to compose a frame, and the fade runs on wall time because photo mode holds the clock. Pad
reads go through the seat's own device filter, so in splitscreen another player's pad cannot close a
mode that is not theirs. The mode itself is `Session/Launch/GameSession.cs`'s.

## src/UI/Overlays/PerfHud.cs
The frame-cost readout (key F14, `--debug-fps` presets it): fps, the current frame's cost and the
worst recent frame, cycling off, compact and full. Built once by `Launcher`, never per session and
never per pane, since fps, frame cost and GC counts are process-wide facts; that hosting is also
what makes it work at the launchscreen, in the viewer and in flight alike. Fed the same raw
stopwatch cost `HitchMonitor` ticks on, every frame, so the worst-frame peak is already warm when
someone presses the key, and off by default so the golden screenshots stay byte-identical. Full adds
the frame's cost split, GC counts, breadcrumbs and a rolling frame-time strip, every term a second
view of data collected elsewhere rather than a new sample. What a frame number proves: PERF-1.

## src/UI/Screens/BuildStamp.cs
The build's version as `CSVM v<version>` in the menu's bottom-right corner, so a screenshot a
stranger sends carries the build it was taken on and the number is not read as the original
game's own. Built once by `Launcher` beside `PerfHud` and shown off the menu host's own "the
menu is up", which is what puts it on every presentation at once: the stamp is a fact about the
binary, not part of a presentation's screen graph, and Original draws decoded artwork with
nowhere to put one. It draws on `HudLayers.PerfReadout`, above the boards, for the same reason
that readout does. Hidden in flight, so no golden screenshot ever sees it. The number itself is
`Utils/BuildVersion.cs`.

## src/UI/Overlays/NetReadout.cs
The `--debug-net` corner readout: a network match's desync counters as
`Net/NetInstruments.cs`'s `Describe` writes them, one section to a line. Built by `Launcher` only
under the flag, so an ordinary run and the golden sweep never build it, and fed once a wall
second from `Launcher.TickNetReadout`, which logs the same line. Hidden while no session holds a
wire. Drawn on `HudLayers.Debug`, unscaled, in the top-left corner.

## src/UI/Screens/NoGameDataScreen.cs
The dead end a launch with no extraction under the data root reaches instead of the menu: the
title, the sentence naming the step that produces the data, the path that was looked in, and Esc
as the way out. `Missing` is the whole test, an absent or empty `extracted` directory, and it is
engine-free so the launcher's branch and its unit read one rule; `Instruction` is the single
sentence the screen and the launcher's own log line share, worded for a release payload
(`Extract.cmd`) or a repo checkout (the two extractor scripts). Provenance is not asked about
here: `Session/Launch/ExtractionStamp.cs` owns whether an extraction is stale and stays a warning.

## src/UI/Labs/MeshLab.cs
The geometry and shading lab (key M): normal lines, the smoothing-seam wireframe, collider boxes,
light sliders with a headlight, and cull by normal-source override cyclers, all scripted by
`--debug-mesh`. Two shapes: the `--viewer` lab owns the parked plane, and the scoped lab over a
`SelectionService` attaches to the current selection and restores on a change or a deselect. Its
override shader samples through `SceneBuilder.SampleAlbedo`, so a surface under the cull override
keeps the chapter's mip LOD bias and the lab stays a diagnostic twin of the real arm.

## src/UI/Labs/WeaponLab.cs
The weapon lab's panel (`--weapon-lab`, key B): a configurator for the held aircraft's live loadout,
hosted top right in flight. It owns no weapon and fires nothing; the steppers write into the
controller's bound loadout and the aircraft's own trigger fires it. Gun steppers arm a gun group,
hardpoint steppers re-arm every pylon, and "reset to stock" restores the fit the session launched
with. A left click casts the lab's own ray, names what it hit and re-parks the held plane on that
ray at the panel's stand-off; the scripted twins all end in the same placement. V hands the rig's
camera to a spectator camera and back. In splitscreen the lab stays player 1's alone, one panel on
rig 0's aircraft, and a log line says so while the other panes fly normally.

## src/UI/Boards/PanelFocus.cs
`Strip(subtree, who)` makes every control under a panel unfocusable and logs the tally, the
invariant every panel hosted in a flight session must hold. A focused button answers Space with
"press me again", so the pilot's fire key re-fires the last stepper instead of the guns and the
arrow keys walk the focus chain instead of reaching the aircraft or the lab's orbit camera.

## src/UI/Screens/SelectionService.cs
The shared world selection in `--freecam` and `--anim-lab`: a left click picks the mesh under the
cursor, PgUp and PgDn walk its `cs_name` ancestor ladder, and a breadcrumb line and wireframe box
show the current rung. Objects are picked by box, map-scale meshes (terrain) by triangle. `Current`,
`Ladder`, `Level`, `CurrentBox` and the `Changed` event are what the other inspect tools read, and
`--debug-select` replays a click for a scripted run. A Ctrl-held pick is reported as `CtrlPicked`,
and a tool may append a line to the breadcrumb through `HudLine`, which is how the export set
attaches without this service knowing what an export is. `ExtraRoots` walks props parked beside the
world content; `SubtreeWorldAabb` (over `Mech3/SubtreeBounds.cs`), `NewBoxInstance` and `DrawBox` are shared with the other tools.

## src/UI/Overlays/TargetingOverlay.cs
The targeting overlay (key F15, `--debug-targets`): a per-frame line from every turret gunner and AI
gunner to its acquired target, coloured by the gate holding the trigger, with that gate named per
shooter in the HUD. Depth test off, since the line into a hull is the one worth seeing. In
splitscreen the world-space lines draw in every pane on their own while the roll-call is drawn once
for the window, like `PerfHud`, because it is process-wide combat state.

## src/UI/Overlays/DebugKillTarget.cs
The debug kill key (F17): kills player 1's currently selected target through its own death path, so
kill counts and objective bookkeeping see it exactly as a real shot would, never by freeing the
node. It routes on the selection's source type, an aircraft through the attributed crash path and a
zeppelin sub-part through the anim runtime's damage call, the same call a rocket makes; a turret
selection is inert for a reason the member states. Over the wire, a target another machine owns is
claimed on its owner as one lethal hit (`../org/multiplayer-messages.md`). Player 1 only.
`KillSource` is exposed so a suite can drive the routing against hand-built sources.

## src/UI/Overlays/TileGridOverlay.cs
The map-edge tile-grid overlay, flag-only (`--debug-tilegrid`; no key is bound): every ground tile
tinted by repetition band, so one colour band is one block. `--map-edge-block` and `--map-edge-mode`
set the depth and the fold once at launch. This is the instrument the map-edge fold was settled
with; the measurements are in [../formats/world-structure.md](../formats/world-structure.md).

## src/UI/Overlays/ColliderOverlay.cs
The collision wireframe overlay (key C, scripted by `--collision=show` and `--debug-colliders`) in
`--freecam`, `--anim-lab` and `--fly`: one immediate mesh per collider host, coloured by the surface
id its body resolves to plus the three owner keys neither surface tag decides, rebuilt from the live
tree on every show. The legend is sourced from the colour function alone, so a palette change cannot
desync it, and it is drawn only where wireframes are. The id drawn is the resolved one and the
picture is of what the engine will select, not of the material data: the id is stamped per body
while the displayed name comes from the texture-derived class, and the two can disagree.

## src/UI/Overlays/AiNetsOverlay.cs
The AI patrol-net overlay (key F13, `--debug-ainets` scripts it), added to every chapter world by
the session's world stage. Draws each net in a stable id-derived colour, edges as segments off the
edge list, sphere markers per node and one label per net, all depth-tested; nets load lazily on the
first toggle and the census goes to the world log. A HUD field narrows the drawn set by name prefix.
It also draws live leashes from each AI aircraft to the node its follower is flying at, filled by a
supplier the session hands in rather than by the overlay knowing anything about aircraft. An
anchored net draws where it actually is, the trailer offset applied per frame as the root's
position, so nothing is rebuilt. The key range: [../controls.md](../controls.md).

## src/UI/Overlays/ClassOverlay.cs
The colour-by-class overlay (key H, `--debug-classoverlay` scripts it) over the same modes as
`ColliderOverlay`, a findable-targets view rather than a collision one. Mixes a class colour over
every drawn mesh at half strength, so a target stays recognisable as itself: destructible through
the registry's own resolve, facade through the billboard classification, clutter as every multimesh
under the world root, everything else scenery. Rebuilt on every press rather than cached. Keyed on
neither surface tag deliberately: one decides which collider a mesh's polygons join and the other
what happens when you touch it, and neither answers "what is this object".

## src/UI/Labs/NodeLab.cs
The node lab (key N) in `--freecam` and `--anim-lab`: the world's `cs_name` tree, a search box,
per-node frame, hide and glTF export into `Exports/`, the export set's three buttons, a dependency readout for the current selection (anim defs, destructible
pools, geometry and textures, colliders) and a destructibles view with coverage columns, plus
top-level branches for props parked beside the world content. `--debug-nodelab` is the scripted
twin. A row's text and colour follow live visibility, re-read on the panel's own status cadence.

## src/UI/Screens/ExportSet.cs
The node lab's export set: the nodes gathered with Ctrl+click or the panel's ± set, written as one
timestamped GLB in `Exports/` at their world transforms. It rides `SelectionService.CtrlPicked`
rather than reading the mouse itself, outlines each member in a cyan box that follows that member's
transform, and puts the count on the selection's breadcrumb through `HudLine`, because a set is
gathered whether or not the panel was ever opened. A member freed under it (a destructible swapping
to its wreck) leaves on its own. Nothing is drawn until the first node joins.

## src/UI/Labs/WorldDamageLab.cs
The world damage lab (key F19) in `--freecam` and `--anim-lab`: the destructible pools of whatever
the selection holds, each with live HP, and a slider with kill and reset on the one a weapon hit
reaches, driving the anim runtime's damage and reset calls. `--debug-damage` is the scripted twin,
an ordered script rather than a token set. Only the pool the registry resolves is drivable, since a
node can carry several; the rest are listed read-only with the reason, because driving a twin would
damage a pool nothing can ever hit.

## src/UI/Overlays/OrbitCamera.cs
The static inspection view's orbit-camera controller (drag to orbit, wheel to zoom, AABB framing):
owns the orbit state and drives a camera it does not own. `Frame` takes the eye and pivot the host
resolved, and `MergedAabb` merges a subtree's world-space mesh boxes, shared with the anim lab. The
`lookAt` argument is a pivot point rather than a direction, since with the eye it also sets the
radius the wheel and the drag work in.

## src/UI/Labs/AnimLab.cs
The `--anim-lab` debugger: a quiet world stage with a pinned seed, a fixed-dt clock, a transport
panel, a def picker, an `AnimTimeline`, a spectator freecam following the shared selection, and a
staged effect and crash anchor set so placeless on-call defs play at the camera. Interactive frames
draw each live transform-motion target interpolated between its last two sim poses, and sim poses
are restored before any step runs, so render smoothing never leaks into event held-pose seeding and
fixed stepping stays byte-identical. Puffer particle spread is unseeded, so same-step shots differ
in particle noise alone. The picker toggle is bound away from the camera's target key.

## src/UI/Labs/AnimTimeline.cs
The anim lab's authored-against-fired timeline, a custom-drawn control: authored blocks above, fired
ticks below, one lane per initial sequence, and a slanted first-firing connector meaning the
scheduler diverged. It re-derives the documented scheduling rule rather than calling the runtime's
own `SequenceRunner`, and that independence is the whole instrument.

## src/UI/Menu/PresentationId.cs
The identity a menu presentation registers under and Options persist: a value token wrapping a
non-empty string, compared ordinally, with `BuiltIn` (`built-in`) and `Original` (`original`) as
the shipped identities. The options store keeps the persisted value as a validated string and
resolves it against `PresentationRegistry.Registered`; an unknown token falls back rather than
throwing. Off-engine coverage: `CSVM.Tests/MenuSeamContractTests.cs`.

## src/UI/Menu/IMenuPresentation.cs
One menu presentation: a screen graph plus its navigation, interaction, animation and cue
selection over the shared features. `Activate(host, destination)` shows it at the screen its own
graph maps the semantic destination to, `Tick` drives it over the host's seats, `Hide` takes it
off screen with its state kept, and `Deactivate` tears it down for good. A flight is a re-`Activate`
on the same instance, so cursors survive it; a presentation switch builds a fresh instance, which
is what discards transient state. Built-in's implementation is `BuiltIn/BuiltInPresentation.cs` and
the host is `MenuHost.cs`; the seam end to end, with the checklist a further presentation follows,
is [../menu-presentations.md](../menu-presentations.md).

## src/UI/Menu/PresentationRegistry.cs
Where presentations register: one factory per `PresentationId`, filled once at startup, duplicate
registration refused. `TryCreate` hands out a fresh instance and answers an unknown id with
false, so a stale persisted token degrades to the Built-in fallback instead of a throw.
Availability (the Original presentation's asset manifest) is decided before asking here; the
registry only says what exists in the build.

## src/UI/Menu/IMenuHost.cs
What the process-lifetime menu host lends the active presentation: `Features`
(`MenuFeatureSet`), `Audio` (`IMenuAudio`), `Seats` (one `IMenuInputSource` each, a live list the
join flow grows) and `Exit(MenuExit)`, the only way out. The host owns all four across
presentation switches; a presentation borrows them between `Activate` and `Deactivate` and keeps
no reference past that. The implementation is `MenuHost` below.

## src/UI/Menu/MenuHost.cs
The process-lifetime host, engine-free, one per `Launcher`: it owns the feature set, the audio
service, the seats and the exit sink across every presentation switch. `Select` settles which
presentation runs through `PresentationResolution.Resolve` over registration plus the owner's
availability answer from the two command-line flags alone, keeps the pre-availability request for the startup log, and answers a
blank, unknown or unavailable request with a fallback reason rather than a throw. `Show` creates
the selected presentation once and re-activates that instance on every later call, so a return
from flight lands on the screens as they were left; `Tick` runs it only while `Shown`, and
`Deactivate` ends it and discards the features' transient state, the first half of a switch.

## src/UI/Menu/BuiltIn/BuiltInPresentation.cs
The Built-in presentation (`CSVM.UI.Menu.BuiltIn`): `LaunchMenu` registered under
`PresentationId.BuiltIn`. `Activate` builds the launchscreen under the parent node on the first
call and maps the return destination onto it, the top level being the Mode screen, `InstantAction`
the wizard's first screen over the setup that flew, `CabinReturn`
the profile's cabin and `DebriefReturn` the scrapbook on the flown mission; the `--menu=` aid is
consumed on that first call, so a return from flight lands on Mode with the cursors kept. `Tick`
runs the menu's frame, `Hide` takes it off screen and `Deactivate` frees the node. `Menu` exposes
the launchscreen for what is Built-in's alone. Read `src/UI/Screens/LaunchMenu.cs` next.

## src/UI/Menu/BuiltIn/BuiltInSeat.cs
A pad-side `IMenuInputSource`: wraps one `MenuInput`, polls it and translates the result into a
`MenuCommands` frame (`Move`/`MoveX` to `MoveY`/`MoveX`, `Start` to `Join`, `Presets` to
`Contents`, `TextEntry` to `CapturingText`). Seat 0's wraps the keyboard plus every unclaimed pad
(the same `MenuInput` is handed to `LaunchMenu` and `MenuSeatDevices`, which bind the devices
behind the seat); a seat a pad joined on wraps a poller bound to that one pad, which is how
`MenuSeatDevices.PadOf` reads the pad back. The seat itself carries only commands.

## src/UI/Menu/IMenuFeature.cs
The contract every shared menu feature implements. A feature is typed state plus semantic
operations for one area of play (configuration, validation, persistence, player setup, launch),
exposed as its own concrete members rather than a universal row/button/picture schema, so each
presentation decides how the operations are offered. `Discard()` drops unfinished setup when the
active presentation changes; persisted data survives.

## src/UI/Menu/MenuFeatureSet.cs
The host-owned registry of shared features, fetched by concrete type (`Get<T>`/`TryGet<T>`), one
instance outliving every presentation switch. `DiscardTransient()` calls every feature's
`Discard` and is the whole of what a switch discards, so anything a feature keeps past it is by
definition persisted data.

## src/UI/Menu/MenuCommands.cs
The device-neutral input seam: `MenuCommands` is one frame of one seat's semantic commands
(auto-repeated cursor steps, edge presses, typed text, an optional window-pixel `MenuPointer` whose
primary button arrives as a press and an edge and whose secondary as a held state driving no command
of its own), and `IMenuInputSource` is the per-seat producer (`Poll`/`Prime`/`CapturingText`). A
source is not synonymous with a pad: keyboard-plus-unclaimed-pads, one claimed pad, a mouse or a
future HOTAS/HOSAS binding all sit behind this contract, and a presentation never reads a device.
The implementations are `BuiltIn/BuiltInSeat.cs`, `Original/PointerSeat.cs` and `MenuIdleSource.cs`;
the seats themselves are the `PlayerSetupFeature`'s, claimed by source identity.

## src/UI/Menu/MenuIdleSource.cs
An `IMenuInputSource` with no device behind it: labelled "no device" and idle every frame. The
screenshot aid that seats extra players on a one-controller machine joins one per seat, so the
shot is deterministic and every seat still has a source to claim; each instance is its own claim,
since a claim is an identity. Read `MenuCommands.cs` for the seam and `PlayerSetupFeature.cs` for
the seats it is claimed by.

## src/UI/Menu/IMenuAudio.cs
The shared menu audio contract: a presentation requests a `MenuCue` by semantic name, starts or
stops narration at moments it owns, and states through `PreviewMix`/`EndMixPreview` the mix a page
that sets one stands at and which `MenuMixLevel` a frame moved; the service owns resolution,
playback, volume, the buses and the handoff into a launching session. The host implementation is
`MenuAudioService` (`src/Session/Launch/MenuAudioService.cs`); Built-in's one call site is the briefing
narration.

## src/UI/Menu/MenuExit.cs
The one typed way out of the menu, handed to `IMenuHost.Exit` and consumed by `Launcher`:
`LaunchExit` (chapter, per-seat `MenuSeatChoice`, `MenuMode`, optional `InstantActionDef` with the wingmen's edited fit beside it, and for Dogfight a `VersusRules` of kill target, minutes, lives and auto-respawn that an explicit `--vs-kills=`/`--vs-time=`/`--vs-lives=`/`--vs-no-respawn` beats),
`CampaignMissionExit` (profile, `cm_sequence` position, per-seat choices), `QuitExit` and
`OptionsApplyExit` (the graphics-mode and difficulty words, the four display settings, the four volume levels and the gameplay switches, null where never set).
An applied choice rides the exit rather than being saved by the screen that took it, so the options file keeps one writer, and a screen
hands back the settings it does not show; none of the fourteen is defaulted, so a page cannot hand back a null it never read, and a field no screen offers any more is dropped rather than left riding as a null the consumer would save. A custom
plane rides the exit as a resolved `CustomPlaneDef`, never a store name. Presentations never construct
sessions. The return side is `MenuReturnDestination`; the exit table and the scans holding the seam: [../menu-presentations.md](../menu-presentations.md).

## src/UI/Menu/DisplaySettingRows.cs
How the four display settings read as rows, shared so Built-in's Options screen and Original's VIDEO
page cannot disagree about a saved value: one label per `DisplayWords` entry in that order, since a
row reads and writes the store word by index, plus the two forgiving reads (an unknown word is the
vocabulary's first value, a size the screen does not offer is the project default) and the wrap a
sideways step takes. The size row is the one a display mode can own: under a `Pinned` one it reads
the screen's own size whatever is saved. The screens and the sizes are enumerated per machine by
`Utils/MonitorSetting.cs` and `Utils/ResolutionSetting.cs`, which is why neither is a list here.
Engine-free, so the rules test without a screen (`CSVM.Tests/DisplaySettingRowsTests.cs`).

## src/UI/Menu/MenuLayout.cs
The runtime reader of `extracted/rof/menu_layout.json`, the decoded menu layout `ExtractRof.ps1`
emits, engine-free in the shared namespace. `TryLoad` answers a missing or unreadable file with
null and a reason, never an empty layout; `Parse` builds the typed model of screens, widgets, the
file-wide macros, the `ScriptToExe` navigation edges, the script-named external assets and the
missing art. A `MenuLayoutWidget` keeps every field's resolved string beside the raw macro token
and the decoder's derived readings, and its typed accessors consult the artifact's own per-type
kind table and refuse a field of another kind, so the reader carries no field table of its own.
Schema, kinds and the extraction stamp: [../formats/menu-layout.md](../formats/menu-layout.md).

## src/UI/Menu/HangarFeature.cs
The hangar as a shared engine-free feature in the host's feature set: the rules and the store
operations both presentations walk, one build at a time. `Open` takes a `CustomPlaneStore` and an
optional `IHangarWallet` (what `CampaignWallet` implements: funds, affordability, airframe
availability, the owned builds, purchase and sale); the three starts pick how the scratch plane
begins, the per-tab operations clamp and report change, and `Refusal`, `CanCommit`, `Bill` and
`Commit` are the purchase gate and the write. `LoadStockWeapons` seeds an airframe at rest from its stock fit, whose per-wing pylon counts come off the rig through `Loadout.WingCounts`, so a stock plane's cells are the ones it hangs. It also owns every label the screens write, the wallet
line, the would-be cost behind the mark on an over-priced row, the name rules, and `Discard`, which
drops the build and touches nothing saved. Economy and strings: [../org/hangar.md](../org/hangar.md).

## src/UI/Menu/HangarDescriptions.cs
What one Plane Construction tab's description box holds, engine-free and presentation-neutral: a
`HangarInfo` of the figure lines, the heading over the prose and the prose itself, one composer per
tab. Each fills the tab's own shipped figures string from the decoded economy and appends the
component's prose row where the component has one, then splits the result at the string's own blank
line, so the heading is the shipped text's and never a literal here. A component with no prose row
leaves it empty and an unpicked engine or gun slot is prose alone. The string ids, the figure
arithmetic and which tab reads which block: [../org/hangar.md](../org/hangar.md).

## src/UI/Menu/CampaignFeature.cs
The campaign as a shared engine-free feature: the state and operations both presentations read and
write, with neither one's screen shell in it. `Open` opens a campaign over a `CampaignProfileStore`;
`OpenGuest` opens a co-op guest's with no profile, on the host's mission and stock hangar, writing
nothing, and `GuestCoopFit` is its ammo pick as the wire carries it. The roster operations create, seat, delete and record the last-played player in the
original's own words; the mission operations settle which `cm_sequence` entry the screens after the
cabin are about, with its briefing, wingman flag, per-slot change-plane rules and story aircraft;
the writes save the loadout, planes, memento (refused unless held), an exported build and the
mission exit. It carries the host's `ChapterCinema` and `ClosingCinema`. Read `CampaignFlow.cs` next.

## src/UI/Menu/BriefingScript.cs
The briefing reveal script, engine-free: the `Briefing.zrd` reader (`BriefingDialog`,
`BriefingState`, `BriefingStep`) and `BriefingReveal`, the interpreter that runs a state's
twelve-opcode beat sheet against a caller-advanced clock, blocking on an authored wait and on the
narration's cue times and keeping each element's opacity, rotation and position as its tweens
land. Elements come out in placement order, which is draw order; with no cue points every marker
releases at once, so the map finishes under the narration rather than a timing being invented.
Opcodes: [../formats/briefing.md](../formats/briefing.md). `ParseScript` is shared with
`EscapeDialog`, whose two scripts add only `Cycle`, the load screen's propeller.

## src/UI/Menu/EscapeDialog.cs
The reader behind the pause screen and the campaign load screen: one dialog's chart sheet with its
source crop and world window, its memento slot and its beat sheet, plus the block every dialog
borrows, which is the objectives parchment, the two icons the pause screen places by world position,
the four button strips and the cursor the screen is pointed at with. `escape.zrd`, `ia_escape.zrd`
and `Loading.zrd` read through it, and `Settled` runs a beat sheet out to the still either screen
draws. `EscapeMap.TryProject` is the world window: world X across, negated world Z down, false rather than a clamp for a position off it.
⚠ The `CLIP` is a rectangle in the bitmap and not on the screen, and every `WORLD` bound is
truncated toward zero, both of which the type's own members say. [../org/pause-screen.md](../org/pause-screen.md).

## src/UI/Menu/BriefingObjectives.cs
The briefing's parchment note, read from a mission's own `objectives.zrd`: every `IDENTITY`
carrying a message key, ordered by priority ascending, which is the list an `Objective id index`
opcode indexes 0-based. Each line also carries the `OBJECTIVEn` block it was read from, the number
the objectives runtime answers about, since priority is the note's row order and nothing more.
Takes the reader rather than a path, so it resolves without an extraction, and leaves an unresolved
key visible as its raw `MSG_*` symbol rather than inventing English. Why keyless entries are not
lines, and why file order is not the order, are recorded on the type itself.
Decode: [../formats/objectives.md](../formats/objectives.md).

## src/UI/Menu/CampaignBriefing.cs
One mission's briefing as the campaign feature holds it: the `cm_sequence` entry, the
`BriefingState` its formula names, the narration wav its sound resolves to, the objectives note,
the messages the buttons are labelled from, and the running `BriefingReveal`, whose progress is
feature state. `Load` reads everything once and degrades to a briefing with no state on a broken
or absent extraction; `Advance` and `Restart` are the presentation's, called on its own clock and
on REPLAY BRIEFING, and when to draw stays the presentation's business. `BriefingScript.cs` and
`BriefingObjectives.cs` sit beside it, since a reveal is authored campaign data both presentations
run identically rather than a drawing.

## src/UI/Menu/CampaignWallet.cs
The seated campaign profile as the hangar's `IHangarWallet`, built by `CampaignFeature.Wallet()`
for the cabin's Plane Construction and null on every wallet-free door: the funds and the
affordability answer, the airframe availability threshold against campaign progress, whether a
plane is a reward and whether it may be sold, the owned builds resolved to their stored build or
their award template or the campaign's starting spec, the sale price, and the purchase and sale
writes that move the funds, the ownership record and the build together. Off-engine coverage:
`CSVM.Tests/CampaignWalletTests.cs`. The thresholds, prices and decoded comparisons:
[../org/hangar.md](../org/hangar.md).

## src/UI/Menu/TypedCheat.cs
One screen's typed-word latch, the rule the cabin, the scrapbook contents and the plane
construction hub share in the original: a left click inside the script's authored region gives it
the keyboard, each character that follows extends a buffer, and the first character that leaves the
target word's prefix empties the buffer outright so a mistyped word starts again from its first
letter. The compare is case-sensitive, arming and disarming leave the buffer alone (the script's
`focus` moves only the caret), and reopening the screen resets both. Off-engine coverage:
`CSVM.Tests/CampaignCheatTests.cs`. The words, the regions and the script lines:
[../formats/campaign-screens.md](../formats/campaign-screens.md).

## src/UI/Menu/CampaignCheats.cs
What the original's four menu cheats leave switched on, carried by `CampaignFeature` so both
presentations read one answer: the cabin's mission pull-down and the mission it stands on, the
gallery reveal (the engine's `fViewAll`, 24 spreads whatever the campaign position), the
unlock-everything flag (`fAllowAll`) that the hidden pilot name sets, and the words and money
constants themselves. Closing a campaign drops the pull-down and its pick; the two engine globals
stay, because the original clears neither short of leaving the game. Off-engine coverage:
`CSVM.Tests/CampaignCheatTests.cs`.

## src/UI/Menu/CampaignAidProfiles.cs
The scratch profile store the campaign screenshot aids read, in the shared namespace so both
presentations' aids seat one player: `%TEMP%\CSVM\menu-aid-profiles`, emptied on every open,
seeded with two players when asked, the first progressed through the campaign's first three
missions with every objective bit set, plus the scratch build store the export aid writes into.
`LaunchMenu`'s `campaign-*` aids and `OriginalPresentation`'s read it; nothing here can reach
`user://Profiles` or `user://Planes`.

## src/UI/Menu/Original/OriginalShell.cs
The Original presentation's screen graph (`CSVM.UI.Menu.Original`), engine-free over `MenuLayout` and the shared Free Flight, player-setup,
Instant Action, hangar and campaign features, with the art measurer and the flight-devices answer injected. It owns the top level composed
from `[MainMenu]`'s own rows, the two remake-only sortie screens, the Options hub over the decoded Preferences chrome, and the messagebox
idiom every refusal and confirm goes through, whose box, `RaiseDialog` and answer keys are its own `OriginalShellDialog.cs` partial; the sortie and credits screens are its own partials, below, while the campaign, hangar, Instant Action, join-board and option families stand outside them as `OriginalCampaignScreen.cs`, `OriginalHangarScreen.cs`, `OriginalInstantActionScreen.cs`, `OriginalJoinBoard.cs` (behind the top level's remake-only JOIN BOARD door) and `OriginalOptionsScreen.cs`. Each is held as one `IOriginalScreenModule` in a list and reaches back through `IOriginalScreenHost` (`OriginalScreenHost.cs`); `ModuleFor` answers which module owns the screen showing, so `BuildRows`, `Lists`, the sideways step, the dropdown close, `Activate`, `Back` and `Compose` name a module through that one lookup rather than a field and a screen-range check per family, and `Campaign`, `Hangar`, `InstantAction`, `JoinBoard` and `Options` are the typed accessors the presentation and the suites read module-specific state through, the seat walk and the shell's own hangar and seat-strip members reaching campaign state through the first of them. `Step` applies one seat's frame, `Compose` is
the screen as a `ComposedBoard` whose backdrop takes a section's `movie` row at its bottom, and every page's row kinds live here,
`OriginalSlider` among them. A pointer press arms a row and only the release still on it activates (`ArmedKey`), on an edit box taking the
caret alone where Accept in one reaches the screen's own commit; the pointer's bitmap answers an enter or leave (`PointerLive`); and a film
in front of the board takes every frame, the tail of the press that ended it included (`CinemaFilm`). Screen by screen: [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/OriginalShellDialog.cs
The standing dialog, a partial of the shell itself rather than of any screen family: the
`OriginalDialog`/`OriginalDialogAnswer` pair, the `DIALOG:*` answer keys every family and both test
files read off `OriginalShell`, `RaiseDialog` with the chrome-bearing overload the credits About box
takes, `DialogRows`, `AnswerDialog` and `ComposeDialog`. The shell owns it because the shell answers
for it: while a box stands its answers are the only rows, `Compose` draws it over the screen's own
picture, and Back takes the declining answer. The rollover frame is the pointer's alone, the cursor's
answer marked with an outline three pixels clear of the strip instead, and the focus a raise took is
put back when it is answered, which is the `FocusBeforeDialog` a screen under the box draws itself from. `DialogRows` sizes an answer plaque through `OriginalWidgets.PlaqueSizeOf`, so the box reaches into no module; every module only raises, and a door onto a new screen closes the box it left behind. [../org/campaign-board.md](../org/campaign-board.md).

## src/UI/Menu/Original/OriginalScreenHost.cs
The two sides of the seam between `OriginalShell` and a standalone screen module. `IOriginalScreenHost` is what a module reads off the shell and
calls back into it for: the screen showing, the per-screen focus cursor every family shares, the pointer's row and position, whether a dialog stands,
the string table and the art measurer, the seat strip and the shell's own plate-row rule, the film a cinema plays in front of the board and the one
frame a screenshot aid replays, and the crossings into another family (the hangar a Build door opens, the walk FLY MISSION begins, a campaign resume, a roster re-read, the mission the cabin's typed cheat launches). The shell implements it explicitly, so the narrower vocabulary stays
the modules' own, and each module's tests implement it as a fake and build the module with no shell at all. `IOriginalScreenModule` is the other
side, what the shell calls on a module: `Owns` plus the seven dispatch members (`BuildRows`, `Lists`, `StepSideways`, `CloseDropdown`, `Activate`,
`Back`, `Compose`). The shell holds its modules as these alone, so a further family is one more entry in its list and no new dispatch arm.

## src/UI/Menu/Original/BoardLayers.cs
The eight lists a `ComposedBoard` is built out of (backdrop, fills, pictures, strokes, lines, plaques, notes, overlays) gathered into one collector, so
every composer in the Original presentation takes one parameter instead of the same eight in an order of its own. It is lists and nothing else: no
drawing rule, no clear, since the shell builds a fresh set per compose and hands them to the board. A composer adds to the layer its shape belongs to by
name, which is what keeps a misordered argument list from moving a shape between layers; draw order stays the board's own and is not this type's to
state. A helper that writes one layer still takes that one list, so its signature names the layer it writes and no reader has to open it to find out.
The seam that carries it across the shell/module boundary is `OriginalScreenHost.cs`, and the board it fills is `ComposedBoard.cs`.

## src/UI/Menu/Original/OriginalWidgets.cs
The layout-widget readings more than one Original screen module needs, a file-level static because a module is a sealed class of its own and a rule
two of them follow can live in neither: the slot number a numbered widget key carries (`AR_D_POINT2`, `OL_D_AMMO1`, and the same key behind a
`<key>:<index>` list row), where a section's background pane lands on the board, which for art smaller than the board is the centre its own
script sets rather than the corner it is authored at, a strip art's one-frame size, and the plaque size the shell's own messagebox measures an answer at.
The rows a screen drawn by the shared board component carries are here too, read off an `ICampaignPage`, because the campaign module and the shell's
per-seat aircraft screen build them the same way, with `ENTRY:` naming a scrapbook row the pointer only highlights. A pane that fills the board centres onto its own corner, and one authored away from the corner
keeps it. Each module binds its own measurer to the pane rule once, so no call site carries one. The open-dropdown window rule is the other such
reading, on `OriginalDropList.cs`.

## src/UI/Menu/Original/OriginalCheats.cs
The three typed cheats of the Original presentation, a partial of the shell over the `gui_char` bodies of `PASSENGERCABIN.SCRIPT`,
`SCRAPBOOK_TOC.SCRIPT` and `PLANECONSTRUCTION.SCRIPT`: the authored region of each screen, its own `TypedCheat`, the primary-button arm read off the
pointer before the row hit test (the secondary button belongs to the credits line alone), the typed characters routed here instead of to a screen's
edit box while a latch holds the keyboard, and what a completed word fires through `CampaignCheats` and `CampaignWallet`. The latches are the shell's
because the three screens carrying them belong to two different modules; it reads those through `Campaign.Cheats`, `Hangar.IsHub` and
`Hangar.OpenWallet`, and the campaign module reads the cheated mission back through the `IOriginalScreenHost.CheatedMission` seam. The cabin's NEXT
MISSION reads and empties the buffer, so the press after a cheated launch is the ordinary one, and every screen change resets all three. Engine
coverage: `menu-original-cheats`.

## src/UI/Menu/Original/OriginalDropList.cs
The one rule every open dropdown of the Original shell follows, held as the file-level `OriginalDropLists` because every page standing on it is a
standalone screen module of its own: a page hands over its key, its items, the box the list hangs
under and the layout widget behind it, and takes back the windowed rows, the `ListWindow` for the pointer and the write that scrolls it. The
window is the widget's authored `TotalDisplayed` clamped to the item count, so a short list is exactly as tall as its items and carries no
chrome. Every item is a row keyed `<key>:<index>`, the ones outside the window built but hidden, since the rows are the hit-test surface and a
dropped row would let a pointer hit what it cannot see; a scrolling list adds `<key>:up` and `<key>:down` in an arrow's width of its own right
edge and hangs the thumb between them. The Instant Action module's two screens and the options module's two listed pages come through here;
`OriginalHangarScreen.cs`'s list does not, its arrows being the closed box's `DropUp`/`DropDown` art. [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/SliderControl.cs
The Original shell's continuous control: a pointer's hold-and-move over a slider row, and the
sideways step that moves one from the keyboard or the pad. It is the shell's second hold-and-move
and keeps a hold of its own, the list thumb's being the first; the shell gives the thumb first
refusal each frame and consults this one only when no list holds, so neither drag can be continued
as the other. `Drive` reports a slider holding the pointer, which is how the shell knows a click was
spent and activates nothing under it. A row declares its slider through `OriginalShell.cs`'s
`OriginalSlider`; this class knows a track and a value and nothing about the setting behind them.

## src/UI/Menu/Original/OriginalOptionsScreen.cs
The five pages behind the Options hub's four doors as one standalone module over the decoded `[@GameOptions@]`, `[@Audio@]`, `[@Video@]`, `[@ControlsPrefs@]` and `[@Keys@]` sections; the hub itself stays the shell's. Game Options and VIDEO are one table shape: per row a key, the authored title, control and description widgets it stands on, and how the store field is read and written, so a further option is one entry plus its field and a layout that moves a row moves ours. Game Options is the original's own Difficulty, Default View and Auto Head Turn rows (the difficulty tiers, the three views its decoded `GO_D_VIEW` list names and the head-turn switch) plus the remake-only Next Target and Rumble rows; the plate grows one whole 62-pixel band per row past the three the art is painted with, tiled from the band between its own seams rather than stretched so the border art survives, the two plaques moving down with it; each row then stands on a band of the grown plate, a dropdown taking one to itself and the checkbox rows pairing from the top of their run where the canvas caps the growth short, each checkbox row's title on its own box's centre line in the dropdown rows' column (the placement the VIDEO section authors for the same pair), with the descriptions spread evenly down their own window and a single tightened pitch as the fallback where the rows outrun even the pairing; VIDEO is the monitor and Resolution rows enumerated per machine (`Utils/MonitorSetting.cs`, `Utils/ResolutionSetting.cs`, that row dead under borderless, which owns the size), Display Mode and V-Sync over `Utils/OptionsStore.cs`'s `DisplayWords`, and Enhanced Graphics on the Shadows checkbox whose gate it owns; the Graphics row's title and description are the page's own, the authored ones naming a 3D card this port has no answer to. AUDIO is four slider rows over `Utils/AudioMix.cs`'s 0..100 on the authored pitches 58, 57, 53 and 53, Master taking the In-Game Music row because a slider reaching zero is that checkbox in one fewer widget and Sound Quality left out; a slider answers no Accept (`SliderControl.cs`), `AudioPreviewMix` is the mix the open page stands at and `TakeAudioMoved` the level a frame moved, taken once, the host applying and sounding them. CONTROLS carries the seat chooser on the Controller Type row, the authored Mouse Sensitivity slider over `Bindings/SensitivityScale.cs`'s levels, the flying-scheme chooser on the Mouse panel's title line (the right half of the seat chooser's column, stopping above the slider's press region) and the KEYS AND BUTTONS door; KEYS carries seven category tabs, one action list under its heading in the listbox's own window, and each row's controls in the two authored columns (the first in Control A, the rest in Control B so nothing is hidden), a cell press arming a capture on that row's action and slot and the page swallowing the frame while one runs, all of it over the shared `ControlsFeature`.
An open list is windowed and drawn on `OriginalDropList.cs`'s rule. The module's own `ReadSavedOptions`/`AppliedOptions` pair is what every page reads and hands back through, so a page carries the settings it does not show; ACCEPT CHANGES leaves as the one `OptionsApplyExit` and only `Launcher.ApplyOptions` writes the store, while CANCEL CHANGES and Back drop the edits. It is one `IOriginalScreenModule` and reaches `OriginalShell` only through `IOriginalScreenHost` (`OriginalScreenHost.cs`), so `OriginalOptionsTests` drives it over a hand-written host with no shell at all; the shell dispatches to it through `ModuleFor` and exposes it whole as `Options`, the `*Choice` properties the presentation and the rebinding facts read included. Rows and readings: [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/OriginalCredits.cs
The credits screen, the shell's partial over the decoded `[@Credits@]` section behind the top
level's fifth row. The section is three widgets: a full-screen background pane, ABOUT and the DONE
plaque. The credit names are painted into the background art, so the pane is the whole composition
and the two buttons are drawn over it by the shell's row loop. DONE and Escape both land on the top
level, the plaque's own `ScriptToExe` and what `CREDITS.SCRIPT`'s `gui_char` does, so `Back` needs
no arm here. ABOUT raises the messagebox in its `ma_` set, centred on its own background and carrying
langui 1301 over the product id; the script's hidden line shows while the pointer's secondary button
is held in its region. The screen: [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/OriginalJoinBoard.cs
The join board, one standalone module behind the top level's remake-only JOIN BOARD door and the only screen a pad signs onto a
seat from. It draws as the open scrapbook (`SB_BackGround.jpg`, the `Album` palette): the CREW MANIFEST down the left page, one
entry per seat carrying that seat's identity chip and player tag and either the device name over "signed on" or the open seat
over the press that takes it, and the ARTICLES OF THE CREW down the right, three rules and the captain's cast-off line whose pad
buttons are drawn inline through `BoardLine.Glyph`. The gestures are `MenuSeatDevices`'s (`PrimeBoard`/`ScanBoard`), read raw off
the devices because a pad with no seat has no commands to read; CONTINUE keeps the manifest and BACK drops every sign-on. The
keyboard is never listed, since it holds seat 1 whatever the manifest says and the first pad to sign on shares that seat, and
`Pose` fills the entries for a screenshot with nobody at the controls: [../menu-presentations.md](../menu-presentations.md).

## src/UI/Menu/Original/OriginalSeats.cs
The shell's two sortie screens, Free Flight and Dogfight, over the shared player setup, plus the
seat rules every screen shares. Rows: the chapter column and BACK, then the aircraft column over
the setup's roster (an eleven-row sliding window) and FLY. Seat 0 alone drives these screens; each
joined seat then picks on its own screen (`OriginalSeatPlane.cs`). FLY is enabled once the mode's
gate is met and leaves as the mode's own typed exit, which the walk's last confirm reaches for it.
`JoiningOpen` is the per-screen joining rule the presentation reads, true on the join board alone, so these screens read the roster that board wrote and take no join gesture of their own. `CampaignSeatPanel` is the
seat strip the campaign boards and the Instant Action screen take as an overlay once a second seat
has joined, Built-in's chip row on `SeatStrip`'s shared shape. Remake-only by design: [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/OriginalSeatPlane.cs
The remake-only per-seat aircraft screen, a shell partial: once seat 0 has picked on a sortie
screen, or pressed FLY MISSION on Instant Action with a second pilot joined, each joined seat in
player order picks here. `SeatPlanePage` is an `ICampaignPage` over the sortie roster, so
`CampaignBoards.For` draws it in the plane-selection board's shape (its list field, WEAPON LOADOUT
over a selection, ACCEPT and CANCEL SELECTIONS), the seat strip over it. The picking seat's own
device drives it, and the mouse riding seat 0's source. Accept selects, a second Accept confirms,
Back and CANCEL SELECTIONS drop a selection then leave the walk with every seat kept and nothing
unjoined, and the last seat's confirm is the launch: [../menu-presentations.md](../menu-presentations.md).

## src/UI/Menu/Original/OriginalInstantActionScreen.cs
The Original Instant Action screen and its Weapon Loadout, one standalone module over the shared `InstantActionFeature` and the decoded `[@InstantAction@]` and `[@OrdinanceLayout@]` sections. Its rows are
the sections' own widgets keyed by their layout keys: the contents list in its authored window with its arrows and thumb, the dropdowns at their authored boxes, the enemy rows on two pages under both
paging buttons, the radio pair and the buttons. A box no setting can fill stands blank with a pale arrow rather than leaving the page; an open list is windowed by `OriginalDropList.cs`, bands its picked
row and the row under the pointer, and a closed box redraws its outline in cream under one. The Pilot Plane list is `OriginalRosters.Roster` (stock, then the saved builds, rows named `Stock <airframe>`
and `<build name> <airframe>`), re-read on every entry and on the hangar's return; a picked build flies its airframe's stock node with its def on the seat. Build opens the wallet-free hangar
(`OriginalHangarScreen.cs`); Weapon Loadout maps the section's four ammunition and eight rocket fields onto the flown aeroplane's gun slots and the pylons it actually hangs, over the stock table's option lists, so a fill-order entry a saved build leaves empty gets no rocket field at all, with the airframe's
diagram frames, the description pane and the snapshot CANCEL and Back restore, over seat 0's fit or the wingmen's shared one by the radio pair, or the per-seat picker's own `PlayerSeat.Fit`, which is what
decides the screen its exit returns to. It is one `IOriginalScreenModule` and reaches `OriginalShell` only through the shared `IOriginalScreenHost` seam (`OriginalScreenHost.cs`), so `OriginalInstantActionTests` drives it over a hand-written host with no shell at all; the shell still owns `Rows`/`Compose`/`ApplyFrame` dispatch, routes to whichever module owns the screen showing and exposes this one whole as `InstantAction`. Its `Back` answers false where nothing is open and nothing is to cancel, which is how Instant Action's own Exit is left to the shell. Remake-only is the Lives box, which the section authors no row for: it takes the mission dropdown's column and item height on the first clear line the setup stack leaves (read off the gaps between the authored boxes, never written down as a Y), and steps the shared `InstantActionFeature.StepLives`, reading Unlimited at zero and the count to nine. Option sets: [../formats/instant-action.md](../formats/instant-action.md); what the fit means at launch: `src/Flight/Weapons/LoadoutChoice.cs`.

## src/UI/Menu/Original/OriginalWrapupScreen.cs
The Original Instant Action wrap-up page, one standalone module over the decoded `[@IA_WrapUp@]` section and `InstantActionWrapupPage.cs`'s content. It stands only while it holds a snapshot, which
arrives as `InstantActionWrapupReturn` when a flown mission's hold ends and the session hands the menu its frozen numbers; `ShowWrapup` takes that run and opens the page. Its rows are the CONTINUE
plaque at its authored corner and one per print, enabled once that print's frame has landed; a print opens its photograph as `Viewing`, which the presentation shows in its `ShotViewer`, and while one is open the page is a single row covering it, which closes it as Back does, the cursor returning to the print. Otherwise both CONTINUE and Back drop the run and reopen the Instant Action screen through that screen's own door, so the sortie's roster and environment are re-read on the
way. `Compose` is the magazine spread as the backdrop, the four brushstrokes, the heading and the eight row lines, each post-it as fills under a shrinking `BoardNote` of its lines, the photographs as prints, the tick box as strokes, and the plaque. A print drawn empty because its shot has not landed is what `TakeLanded` reports once it has, which the presentation's tick reads to compose the page again. The
built-in presentation keeps its in-flight `UI/Screens/IaWrapupBoard.cs` instead and has no page here, which is why its own return lands on the Instant Action screen. It is one `IOriginalScreenModule`
reaching the shell only through `IOriginalScreenHost` (`OriginalScreenHost.cs`); the shell exposes it as `Wrapup`. The page's own decode:
[../formats/instant-action/wrap-up.md](../formats/instant-action/wrap-up.md).

## src/UI/Menu/Original/OriginalPauseBoard.cs
The Original presentation's pause screen, on `PauseBoard`'s own seam: built once by `GameSession`
over a `PauseSheet` its mission resolves, following `PauseState.Changed`, driven by the pausing
player's reader alone. What it draws is `PauseScreens`' composition through `ComposedBoardView`, so
the screen tests off engine and this node owns the cursor, the pointer and the five actions. An Instant Action sortie's sheet is the blackboard, which it writes in `BoardPalette.EscapeBlackboard` rather than the campaign sheet's ink. That
seat's pointer shares the cursor: a hover moves it, a press holds the strip, the release on it fires, and the OS pointer gives way to the dialog's own. Its readout is a delegate, since the objectives follow the running mission. Preferences stands `PausePreferences` over the held world and `Reprime`s on its close, and photo mode does the same over the frozen world. It draws no control hints, since the original's sheet carries none. Decode: [../org/pause-screen.md](../org/pause-screen.md).

## src/UI/Menu/Original/OriginalHangarScreen.cs
The Original hangar, a standalone module over the shared `HangarFeature` and the decoded hangar
sections: the PLANE NAME screen, the Plane Construction hub with one of six tab sections on its
right page, the totals page and the INVENTORY, entered from Instant Action's Build Custom Plane or
the cabin, the door naming the airframe a default build opens on. It is one `IOriginalScreenModule` and reaches `OriginalShell` only through `IOriginalScreenHost` (`OriginalScreenHost.cs`), the shell's own explicit-interface implementation narrowing it to the screen/cursor/dialog surface a screen family needs (`Open`, `FocusKey`, `RaiseDialog`, the focused row and the campaign plane roster), so `OriginalHangarTests` drives it over a hand-written host with no shell at all; the shell still owns `Rows`/`Compose`/`ApplyFrame` dispatch, finds this module through its own `Owns` (every screen from `PlaneName` on) and exposes it whole as `Hangar` (its typed name, open list, last build and `OriginalHangarInks`) rather than forwarding member by member. It owns the plane picture over
the blueprint panes; the hub's figures, which `HubBill` prices on the row an open list has under the cursor so they preview it and take nothing, the cost line reddening on that bill's funds verdict and the weight line on its capacity verdict, bar a previewed airframe row, whose weight line is pending and plain; the cash note on both doors (the wallet's funds, else the export door's figure), every combo row staying bare over either;
the tab bar with the standing tab latched and its labels on the strips' own baseline; the tab pages' description box, which `HangarDescriptions` fills and whose prose flows as a note inside it; every list under its box bar the decal picker, the page's own five-across grid of tiles carrying its chrome inside its right edge; the two name boxes with their
caret, the inventory's plane line on the middle of the dashed box the background paints rather than on its authored row, the airframe swap's own three-answer question as the shared messagebox (its answer keys mirroring `OriginalShellDialog.cs`'s `DialogOkKey`/`DialogYesKey`/`DialogNoKey`/`DialogCancelKey`), and the export door's own Export, Delete and delete confirm; the shared pane rule (`OriginalWidgets.cs`) centres a small pane and this module places its rows on it. [../org/hangar.md](../org/hangar.md), [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/OriginalCampaignScreen.cs
The Original campaign, one standalone module over the shared `CampaignFeature`: the profile screen,
the cabin, the table of contents, the flight check, ammo and plane selection, the book, a scrap's
zoom and the briefing. What each screen draws is the shared board component, so the module hosts
the Built-in campaign pages in a `CampaignFlow` of its own and copies every composed layer into the
board it hands back, the cabin's painting going down as backdrop so the mission pull-down's paper stands over it; that flow is never walked, its screen and cursor mirroring this module's. The
screen graph, the rows at the rectangles the board draws them at, the pointer hit-testing, the cues and every dialog raise are this file's, as are `OpenCabin` (every door onto the cabin, which is why RETURN TO CABIN is taken here rather than mirrored off a page), `ShowScrapbook`, where the feature's two cinemas play, and `CheckSeat`: the check and the two screens it opens stand for one player at a time, that seat's own device driving them while seat 0 keeps its pointer alone. It is one `IOriginalScreenModule` and reaches `OriginalShell` only through `IOriginalScreenHost` (`OriginalScreenHost.cs`), which raises its messageboxes, runs a script's frames and plays its films, so `OriginalCampaignTests` drives it over a hand-written host with no shell at all; the shell dispatches through `ModuleFor` and exposes it whole as `Campaign`, which is also how the seat walk and the hangar door's wallet reach campaign state. The scrapbook's pen is the only stroke any module draws, which is why `Compose` carries a strokes layer. Read `src/UI/Campaign/CampaignFlow.cs` for the pages; the screens and
their strings: [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/OriginalConnectionScreen.cs
The original's Multiplayer Connection page and the LAN games list behind its Connect, one
`IOriginalScreenModule` over `NetPlayFeature`. The multiplayer scripts place their widgets inline,
so every corner is the scripts' own rather than the layout's. Only LAN TCP/IP, which searches the
network, and Internet, which joins the typed address, are offered; Build Custom Plane draws greyed,
and Host and Create Game open `OriginalLobbyScreen` as a Dogfight's host. A join started here is followed on the shared messagebox over the page until it lands or
fails. A game of another build version lists in grey with its version as its status, and Join
Game refuses it in a box before any socket opens. Plaques draw as pictures, over a script's labels.
The geometry and strings: [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/OriginalLobbyScreen.cs
The original's Multiplayer Lobby, one `IOriginalScreenModule` over the door's `DogfightLobby`, with its four tabs (Mission Options, Select Plane, Select Ammo, Game Scores) in the scripts' own placements and art.
The host's option controls are live until it is Ready, a guest's are drawn greyed with the host's values, and what the lobby does not fly (Capture the Flag, the zeppelin mode, custom planes, teams, Boot) draws greyed. The Lives box is live only while Limited Lives is ticked.
Every player picks a stock plane and its ammunition, live at all times. Ready is live once the options have been heard. `Land` stands a completed match's peers back here on Game Scores, which is greyed until then. LAUNCH! is live on the host once every row is Ready, and hands the shell a Versus `LaunchExit` on the environment's chapter with the lobby's rules and the door's wire; `GuestLaunch` is a guest's same exit once its host has launched.
Leave Game closes the door and lands on the Connection page. The shell follows a Dogfight guest into this screen and out of it when the link ends. Geometry and strings: [../org/menu-inventory.md](../org/menu-inventory.md).

## src/UI/Menu/Original/OriginalPresentation.cs
The Original presentation node, registered under `PresentationId.Original`: a `CanvasLayer` on the board layer holding one
`ComposedBoardView`, so every screen scales as the campaign boards do. `Activate` builds the shell and the device
bookkeeping once, refreshes the roster from the saved-plane store on every call, stands the shell on the top level, maps the
return destination onto it and applies the `--menu=` aid on the first show. `Tick` keeps the pads in step (the board's sign-on scan
while the shell stands on the join board, and that frame's presses dropped from seat 0's commands, since seat 0 borrows every unheld pad and would otherwise read the same A as Accept), polls every seat, maps a window-pixel pointer into the
authored space, steps the shell, requests its cues, states the AUDIO page's mix while that page is open and ends the
preview on every door out and on `Hide`, drives the briefing's reveal, and runs the board's movies on the step the host was given,
`DebugPointer` standing in for seat 0's pointer when the screenshot aid asks. A `ShotViewer` over the view follows the wrap-up page's `Viewing` photograph. The shell's art sizes come from `OriginalArtSizes`; `PaletteFor` is the inks, and `CabinPalette` writes the cabin's pull-down in the paper forms' list inks.

## src/UI/Menu/Original/OriginalArtSizes.cs
The art measurer every host of `OriginalShell` hands it, since the layout carries a widget's
position and its art name but not that art's size, and a name that does not measure leaves the row
on a fallback rectangle, which is the rectangle the pointer then hits. One name answered with the
file's pixel size, cached per name over one extraction root: a bitmap through `Image.LoadFromFile`,
a movie off its sequence header, which no bitmap loader can read. A file that is not there measures
as null once and is logged once, under the host's own name, so the presentation and the in-flight
`PausePreferences` leaf are told apart in the log. Both hosts hold their own instance, so the cache
follows the screen that is standing rather than being shared across a teardown.

## src/UI/Menu/Original/OriginalAvailability.cs
The availability answer Original is selected on: `Load(dataRoot, out reason, out degraded)` refuses
a tree stamped below `OriginalAssetManifest.StampSchema` (`ExtractionStamp.Behind`), reads the
layout through `MenuLayout`, requires a `[MainMenu]` section in it, and then checks the manifest
derived from that layout. Returns the loaded layout when Original can run, else null and the one
reason, which the host appends to its fallback reason; `degraded` is the optional half, for the
caller to log once. `ArtPath` and `RelativeArtPath` are where a layout art name resolves, the
presentation's size read going through the first; `IsMovie` puts the movies one directory deeper,
under `MPG`, where the executable resolves them. Coverage: `CSVM.Tests/OriginalManifestTests.cs`.

## src/UI/Menu/Original/OriginalAssetManifest.cs
The versioned required/optional asset manifest, derived from the decoded layout rather than
hand-listed. `Derive` classes the art of the sections Original composes required, less two short
tables (rows it does not draw, and rows it draws whose file the screen survives the absence of,
which is where the background movies sit), everything else optional, and the files the scripts
name and Original draws anyway (the pointers, the font, the multiplayer pages and the lobby's art) required. `Check` reads no bitmap: existence plus the PNG signature
and IHDR size for a required entry, existence alone for an optional one, and one report naming every
fault with its section, row and file. `Schema` carries the rule for its own bumps; the asset policy
and the reconciliation against what the screens draw are [../menu-presentations.md](../menu-presentations.md).

## src/UI/Menu/Original/OriginalRosters.cs
The two rosters the Original sortie screens list. `Chapters` is the shared `MenuChapters` roster
with a short label per code; `Airframes` is the eleven stock names in the string table's own
order, resolved to their `planes.zbd` nodes through the Instant Action decode, so the
name-to-node map has one home. `Roster(customs)` appends the saved custom planes through the
shared player setup's roster rule, which is what both presentations pick from and what the
Instant Action Pilot Plane list offers. Read `src/UI/Menu/PlayerSetupFeature.cs` next.

## src/UI/Menu/Original/OriginalCues.cs
The four cue names the Original presentation asks the shared audio service for: a button rollover,
a button press, and an edit box's keystroke and reject sounds, which are the four the original's
globals script binds. The names are semantic and the cue table owns which wav each resolves to, so
the presentation names no file. The contract is `IMenuAudio.cs` and the table is
`src/Session/Launch/MenuCueTable.cs`.

## src/UI/Menu/Original/PointerSeat.cs
Seat 0 with a pointer: wraps the seat that polls the keyboard and the unclaimed pads and adds the
mouse as the frame's `MenuPointer` in window pixels, `Pressed` while the left button is down,
`Clicked` on the press edge, `Wheel` as the steps turned since the last poll and `RightPressed`
while the right button is down. `Prime` reads the primary button and drains the wheel, which also
drains every frame whether or not a pointer is on screen, so input from before the menu showed never
arrives as one jump. The four device reads are injected delegates, so the seat is engine-free and
`Launcher` supplies the mouse position, both button reads and the wheel it counts in `_Input`.
Built-in ignores the pointer; Original maps it into its authored space; a later pad seat has none.

## src/UI/Menu/MenuReturnDestination.cs
Where the menu stands when it comes back, said semantically: `TopLevel`, `InstantAction`, `InstantActionWrapupReturn(snapshot)`, `CabinReturn(profile)`, `DebriefReturn(profile, missionSeq)` and `LobbyReturn(scores)`, a completed Dogfight's landing on the lobby's Game Scores. The host names the destination and the
active presentation maps it into its own graph at `Activate`, so no presentation-specific screen id crosses the seam. `ForLaunch(exit)` reads off a launch's own exit the screen it came from, which is
where a flight left early lands; the exit and not the session's spec, since a spec inherits the command line's `--campaign=` and would call a Free Flight launched afterwards a campaign mission. A
destination names where the player stands and never a store: the two campaign returns name a profile, the store it is re-read from is the presentation's own, and an Instant Action return names
nothing, the sortie's setup being the feature's. The one exception is the wrap-up return, which carries `IaWrapupSnapshot` (declared here, so nothing outside the shared namespace crosses the seam but the stunt camera's own `StuntShot` records):
the session that counted an ended Instant Action mission's numbers is freed before any page can draw them. The `--menu=` aid is not a destination either, reaching the cold start alone, so a return is
always one of these. The namespace seam this whole
folder is held to, and the two scans that enforce it, are in [../menu-presentations.md](../menu-presentations.md).

## src/UI/Menu/MenuChapters.cs
The shared chapter roster: the eight chapter worlds as `MenuChapter(Code, DangerZones)` in code
order, with `For(mode)` (Stunt Flying only the six carrying Danger Zones), `Find` and
`DangerZonesFor`. The codes are separate terrain databases, not lighting variants
([../formats/spawns.md](../formats/spawns.md)); the flag says whether the chapter's `ia.json`
ships a `dzones` list, which is why Stunt Flying withholds the other two. Shared so that
`LaunchMenu`'s Chapter screen, the Free Flight feature and the Instant Action feature read one
roster. Off-engine coverage: `CSVM.Tests/FreeFlightFeatureTests.cs`.

## src/UI/Menu/FreeFlightFeature.cs
Free Flight as a shared `IMenuFeature`, the first feature cut out of `LaunchMenu`: the chapter
roster it offers, the pick (`SelectChapter`, a code outside the roster throwing), the launch gate
(`Refusal`/`CanLaunch`: a chapter picked, at least one seat, every seat confirmed), `BuildExit`
(the typed `LaunchExit` with mode Free, refusing a closed gate or a seat with no plane) and
`Discard`. Free Flight is the remake's own mode, so nothing here is decoded; the rules are the
launchscreen's, moved. The seats are the `PlayerSetupFeature`'s, so this feature never reads a
roster, a lock or a store. Owned by the host's feature set and read out of it by `LaunchMenu` and
`OriginalShell`. Off-engine coverage: `CSVM.Tests/FreeFlightFeatureTests.cs`.

## src/UI/Menu/ControlsFeature.cs
The rebinding screen as a shared `IMenuFeature`, engine-free: which seat's keymap is being edited
(one registered `BindingProfile` per seat), which of the three contexts, the row and slot cursors,
the capture in progress over the seat's own `IDeviceState`, and the steal it is about to perform.
A capture that lands on a free control binds it; one that lands on a held control raises `Pending`
naming every action that would lose it and moves nothing until `ConfirmSteal`, which keeps the
original's conflict rule from happening behind the player's back. `UnbindSlot`, `ResetContext`,
`Save` and `Accepted` (each committed seat, for a host whose seats hold their own keymaps) are the
rest. The seat's `MouseFlying` and `MouseSensitivity` are staged with the maps and written on accept. Editing is scoped to one seat's profile. Model: [../org/input.md](../org/input.md).

## src/UI/Menu/PlayerSetupFeature.cs
Player setup as a shared `IMenuFeature`, device-neutral and engine-free. `Seats` are `PlayerSeat`s
in join order, each bound to the `IMenuInputSource` that claimed it: a claim is one source and one
seat, settled in arrival order, and seat 0 never leaves. `Roster` is the `MenuAircraft` list every
seat picks from, set by the presentation and built by the shared rule (the stock rows in their given
order, then one row per saved custom flying its airframe's stock node, a campaign plane nobody has
exported left out). Per seat it owns the cursor, the two stages of the pick, the loadout door and
the backing-out ladder; the gate is the mode's minimum of seats and every seat confirmed. It also holds Dogfight's two match rules, `KillTarget` and `TimeLimitMinutes` with their steppers, starting at the command line's own 5 and 5 and riding a Versus exit. `Choices`
and `BuildExit` are the typed result. Nothing here reads a pad: `src/UI/Screens/MenuSeatDevices.cs`, below.

## src/UI/Menu/NetPlayFeature.cs
The multiplayer door as a shared `IMenuFeature`, engine-free and carrier-free: the port and address
a board edits, the socket it opens, and the readouts a board draws (`Stage`, `Peers`, `Link`,
`PortMap`, `Fault`, `HostStarted`, `Advert`). Carriers and port maps arrive as delegates (the
launcher's `Net/NetCarrier.cs`, or a suite's loopback mesh and no router); every open wraps its
carrier in a `Net/NetLobby.cs`. `OpenHost`, `OpenCoopHost` (whose `Offer` names the mission) and
`OpenJoin` open; `Step` carries the link; `Close` unmaps the lease. In co-op `ShowCoop` sends each
guest the host's boards, `CoopAllReady` holds FLY MISSION until every guest is Ready, `CoopLaunchDue` tells a guest to follow, `TellSeatFits` sends every seat's fit before the opener, and
`LeaveCoopMission` tells the host at once that a guest walked out. `OpenDogfightHost` opens a Dogfight lobby, and `Dogfight` is the `DogfightLobby` either end stands in, created on a guest when its host's advert names a Dogfight and unshown behind a Built-in host; `DogfightLaunchDue` tells a guest its host has launched, and a guest back from a match waits for the host's next round. `Version` is the build's `Net/NetBuildVersion.cs`: a host refuses a guest of another version, and a guest such a host, with both versions on `Fault`. Boards: `LaunchMenu.cs`.

## src/UI/Menu/DogfightLobby.cs
The Multiplayer Lobby's state over a `Net/NetLobby.cs`, engine-free, one class for both ends.
The host owns the options (environment, mission type, victory condition, time or score, the lives rule) and sends them to every guest; any change advances the round and clears every Ready, its own included.
A guest reads the options and the host's player list, and sends its stock plane, fit and Ready under the round it heard once a lobby screen `Show`s it; a changed pick clears its own Ready. `Say` sends one chat line, which the host relays.
`CanLaunch` is the host's gate, every row Ready; `RulesOf` is the `VersusRules` a launch carries, lives clamped to 1..99, and `ChapterOf` the chapter an environment flies on. Setters refuse on a guest and for a greyed choice. `CheckBuiltInLaunch` gates a Built-in host's launch on its lobby guests, and `Land` holds a match's `Scores` and opens the next round.
Wire: [../org/multiplayer-messages.md](../org/multiplayer-messages.md). Coverage: `CSVM.Tests/DogfightLobbyTests.cs`.

## src/UI/Menu/CoopDoorText.cs
The words the campaign's network door is drawn in, engine-free and built off the door alone: the
host's band (`HostBand`, the open port, the router's address and the guest count), the session an
advert names (`SessionName`), the join board's status (`JoinedStatus`) and the waiting board's
(`WaitingStatus`), the games list's cells with a version mark (`Status`), the refusal naming both
versions (`VersionMismatch`), and the rows and presses those boards show. The mission's long name comes in as
a delegate, since only the caller holds the langui table.

## src/UI/Menu/NetDoorAid.cs
The multiplayer doors the `--menu=` screenshot aids stand on: a host door over a loopback wire with
guests already on it and a router stub mapping at a documentation address, and a guest door already
joined to a loopback host advertising a campaign mission. `CoopGuest` stands a guest on a given
host flow, and `AnswerReady` makes a host's guest Ready. `DogfightDoors` is a Dogfight host and two
guests on one wire, `PoseDogfight` sets the lobby the `lobby` aid shows, and `PlayedScores` is the
finished match its Game Scores page lands. The games list's sample LAN holds one game of
`OtherVersion`, which the list marks. No aid opens a socket or asks a router.

## src/UI/Screens/MenuSeatDevices.cs
The pad side of the shared player setup, for any presentation, over seat 0's `MenuInput` and the
feature. `P1Pad` is the pad seat 0 claimed by steering a screen with it. `Sync` reconciles the
seats with the connected pads: a seat whose pad vanished is unjoined, a vanished claimed pad frees
seat 0, and seat 0's poller is bound to its claimed pad or to every unclaimed one. `PrimeJoins`
and `ScanJoins` are Built-in's join gesture, Start on an unclaimed pad while a seat is free, the caller deciding on which screens joining is open; `PrimeBoard`, `ScanBoard` and the `BoardScan` it answers with are Original's join board, A signing a pad on, B signing it off and Start on the captain's pad casting off, over `SignOn`, `SignOff`, `IsCaptain` and the `IJoinRoster` the board draws its manifest from. A first sign-on takes `P1Pad` rather than a seat of its own, since the keyboard holds seat 1 whatever the manifest says, and `DropSignOns` is BACK giving every one of them back.
`PadOf` reads a joined seat's pad back off its
`BuiltInSeat`, and `FlightPads` is the binding a launch carries, the answer both presentations
hand the feature's `Choices`. Read `src/UI/Menu/PlayerSetupFeature.cs` for the seats themselves.

## src/UI/Screens/MenuControlsSeats.cs
The rebinding screen's seat bookkeeping, for any presentation. `Sync` takes this frame's pollers,
one per joined seat in player order, and puts the shared `ControlsFeature`'s player rows in step
with them: a registration is kept while the seat behind its number is the same poller, a number
that changed hands is registered again, and a seat with nothing to press gets no row. `PadOf` is
the identity a context's rows sit on, the one function the capture reader and the captured control
both take, so neither can name a pad the other does not. The profile a seat is staged from is the
menu poller's own live map plus the saved flight and camera maps, mouse scheme and sensitivity, so
an accepted rebind is felt at once. Read `src/UI/Menu/ControlsFeature.cs` for the editing itself.

## src/UI/Menu/InstantActionFeature.cs
Instant Action as a shared `IMenuFeature`, owned by the host's feature set and configured by both
presentations. The option sets are static and decoded: the environments, the mission types with
the bans a chapter and stunt flying impose, the eleven airframes, the militias with their
aircraft and wave accent, the skills and the preset table. The setup is typed state with semantic
operations: select and confirm an environment (which re-fits the mission type and loads the
chapter's own base def), the mission type, the lives, the four waves, the wingmen, both plane
picks and a preset. `Refusal`/`CanLaunch`, `BuildDef`, `LaunchWingmanFit` and `BuildExit` are the gate and the launch.
`Discard` resets every field. Decode: [../formats/instant-action.md](../formats/instant-action.md).

## src/UI/Screens/MovieSurface.cs
A movie as something a composition can draw: a `CSVM.Video.MoviePlayback` and the `ImageTexture`
its pixels are uploaded to, made once and updated in place. There is no node, so a caller hangs
the texture where its own layout row puts it and this surface never learns which screen that is.
`Open` answers null for a file that cannot be read or is not a movie, because a screen missing its
background still has everything else on it. `Advance` says whether the picture changed, so a caller
repaints on the frames that need it and no others. Every timing decision belongs to the playback,
which holds no engine type, so this half is the upload alone. Read `src/Video/MoviePlayback.cs`
next.
