# Menu presentations: the contract and the extension checklist

How the in-game menu is split between shared features and interchangeable presentations, what a
new presentation (a Modern one, say) plugs into, and what it must not assume it inherits from the
two that ship. The per-module detail is routed from [`architecture.md`](architecture.md) into
[`architecture/UI.md`](architecture/UI.md), one `##` entry per file named below; this page is the
seam read as a whole. The screen census and the evidence behind
the Original presentation are [`org/menu-inventory.md`](org/menu-inventory.md); the decoded layout
format is [`formats/menu-layout.md`](formats/menu-layout.md).

## Contents

- [What a presentation is, and is not](#what-a-presentation-is-and-is-not)
- [Registration](#registration)
- [The lifecycle and the host](#the-lifecycle-and-the-host)
- [The shared features](#the-shared-features)
- [Input sources and seats](#input-sources-and-seats)
- [Options, selection and availability](#options-selection-and-availability)
- [Audio](#audio)
- [Launch](#launch)
- [Return](#return)
- [The asset policy](#the-asset-policy)
- [The namespace seam and the scans](#the-namespace-seam-and-the-scans)
- [The `--menu=` aid convention](#the---menu-aid-convention)
- [What a new presentation does not inherit from Original](#what-a-new-presentation-does-not-inherit-from-original)
- [The extension checklist](#the-extension-checklist)
- [The verification layers](#the-verification-layers)

## What a presentation is, and is not

A menu presentation is a screen graph together with its navigation, interaction, animation and cue
selection over the shared features. Drawing alone is not the boundary: the two shipped
presentations, Built-in (`CSVM/src/UI/Menu/BuiltIn/`, the launchscreen in `CSVM/src/UI/Screens/LaunchMenu.cs`)
and Original (`CSVM/src/UI/Menu/Original/`, the decoded 800x600 screens over the player's extracted
menu data), reach the same features through different screen sequences, different input idioms
(cursor rows against a pointer-first page) and different sounds.

A presentation is not where play is configured, validated, persisted or launched. Those are the
shared features' operations, and a presentation only decides how to offer them. It never
constructs a session, never names a launcher type, never hides itself, and never reads a device: it
reads semantic commands from the host's seats and leaves through one typed exit.

Built-in is permanent. It is the fallback every unavailable request resolves to, it needs no
extracted menu artwork to be usable, and every other presentation is additive beside it.

## Registration

`PresentationRegistry` (`CSVM/src/UI/Menu/PresentationRegistry.cs`) holds one factory per
`PresentationId`, a non-empty ordinal token, the word `--presentation=` names. `Launcher.BuildMenuHost`
(`CSVM/src/Session/Launch/Launcher.cs`) fills the registry once per process:

```
registry.Register(PresentationId.BuiltIn,  () => new BuiltInPresentation(...));
registry.Register(PresentationId.Original, () => new OriginalPresentation(...));
```

Registration says what exists in the build, nothing more. A second registration of the same id
throws, since two rivals for one identity is a wiring error. The host asks the registry for a fresh
instance on every activation after a switch, which is what makes a switch discard transient
presentation state by construction. The factories run inside the first `Show`, so a factory may
read process state the launcher settled by then (the cold start's `--menu=` aid, the layout the
availability check loaded).

## The lifecycle and the host

`IMenuPresentation` (`CSVM/src/UI/Menu/IMenuPresentation.cs`) has four calls and an identity:

| Member | When the host calls it | What the presentation does |
|---|---|---|
| `Id` | never; read by the host and the tests | the token it registered under |
| `Activate(host, destination)` | on the cold start, after every `Hide`, and once after a switch (always with `TopLevel`) | builds its nodes on the first call, then stands on the screen of its own graph that the destination maps to |
| `Tick(dt)` | every frame while shown | polls the host's seats, drives its graph, requests cues, hands an exit to the host |
| `Hide()` | after the host consumed an exit | takes itself off screen and keeps its state; a flight is not a switch, and the next `Activate` lands on the screens as they were left |
| `Deactivate()` | the first half of a switch | tears down everything `Activate` built; the instance is discarded afterwards |

`IMenuHost` (`CSVM/src/UI/Menu/IMenuHost.cs`) is what a presentation borrows between `Activate` and
`Deactivate` and keeps no reference to past that: `Features` (the shared `MenuFeatureSet`), `Audio`
(the shared `IMenuAudio`), `Seats` (one `IMenuInputSource` per joined seat, a live list) and
`Exit(MenuExit)`, the only way out. The implementation is `MenuHost` (`CSVM/src/UI/Menu/MenuHost.cs`),
engine-free, owned by `Launcher` for the life of the process. `Launcher` shows it with a destination
on every entry, ticks it every frame, and receives every exit through the sink it constructed the
host with. `MenuHost.Shown` is what the owner reads for "the menu is up"; no presentation's node is
consulted.

A switch is three host calls in order: `Deactivate()` (ends the instance and calls
`Features.DiscardTransient()`), `Select(...)` (re-resolves from the two flags), then
`Show(TopLevel)`. The shipped build never switches at run time, since the presentation is the command
line's alone; the suites drive the three calls to hold the seam. `Launcher.ApplyOptions` makes the
first and the last of them, one frame after the `OptionsApplyExit` that asked, after saving every
choice it carries, so an apply restarts the same presentation at its top level.

The frame in between belongs to no presentation: the host hides the outgoing instance as it hands the
exit to the sink, and the fresh one does not stand up until the apply runs. Nothing opaque is
over the persistent `WorldEnvironment` on that frame, so `Launcher.ShowMenu` blacks its background
through `Utils/WorldBackdrop.cs` and a launch puts the sky back. A presentation still draws its own
opaque backdrop: the black is what the switch's own frame, and the held frame after a quit, fall
back to.

## The shared features

`MenuFeatureSet` (`CSVM/src/UI/Menu/MenuFeatureSet.cs`) holds one instance of each feature, fetched
by concrete type (`Get<T>`, `TryGet<T>`), outliving every switch. A feature implements `IMenuFeature`
(`CSVM/src/UI/Menu/IMenuFeature.cs`): typed state plus semantic operations for one area of play, and
`Discard()`, which drops unfinished setup when the active presentation changes and touches nothing
persisted. There is no universal row, button or picture schema; each presentation reads a feature's
concrete members and composes them its own way.

| Feature | Owns | Does not own |
|---|---|---|
| `FreeFlightFeature` | the chapter roster it offers, the chapter pick, the launch gate over the setup's seats, `BuildExit` | seats, aircraft, stores |
| `InstantActionFeature` | every decoded option set, the environment, mission type, lives, four waves, wingmen and their fit, the player plane, presets, the base def, `BuildExit` | which screens the fields appear on, the wizard order |
| `PlayerSetupFeature` | the seats claimed by input-source identity (four at most, seat 0 never leaving), the aircraft roster (`BuildRoster`), each seat's cursor, the two-stage pick and its fit, the per-mode gate, Dogfight's kill target and time limit with their steppers, `Choices`, `BuildExit` for Dogfight | the pad behind a seat (asked of the presentation through `MenuSeatDevices`), the join gesture, the split-pane or seat-strip drawing |
| `HangarFeature` | one scratch build at a time over a `CustomPlaneStore` and an optional `IHangarWallet`, the three starts, the airframe pick with its changed-since gate and the defaults ask's three answers, the per-tab operations, the purchase gate in the original's words, `Commit`, `DeleteSaved`, the labels and name rules | the nine-screen walk or the tab bar, the dropdowns, the dialog idiom |
| `ControlsFeature` | the seats it can edit and their staged keymaps, the context and action cursors, the capture and the steal it names first, `Accept`, `Cancel`, `ResetSeat` | which page the rows are split across, the tabs or columns they are drawn in, the join that raises a second seat's row |
| `NetPlayFeature` | the port and the address, the socket a host or a join opens, the stage, link, peer and port-mapping readouts, the timeout on a join, `BuildLaunch`, `Close` | the carrier and the port mapper (both injected as delegates), the board's rows, the roster, the seats and the session |
| `CampaignFeature` | the profile store, roster and seated profile, `ContinuePlayer` and `DeletePlayer` with their refusals, the mission position and its briefing state and reveal progress, the intents between screens, `CommitLoadout`, `CommitPlanes`, `ExportPlane`, the flight field, the wallet, `BuildExit` | the screen stack, the cursor, the refusal line, the modal, the working copies before ACCEPT, the reveal's clock |

The rule that makes two presentations replaceable: a feature never references a presentation, and
the dependency test rejects one that does (below). A feature also never reads a device: seat 0's
pointer, a pad's Start, the keyboard's letters all arrive as semantic commands.

Two shared readers sit beside the features in the same namespace and follow the same rule:
`MenuLayout` (`CSVM/src/UI/Menu/MenuLayout.cs`), the reader of `extracted/rof/menu_layout.json`, and
`MenuChapters`, the chapter roster. `CampaignAidProfiles` is the scratch profile store the campaign
screenshot aids read, so both presentations' aids seat one player and none can reach
`user://Profiles`.

## Input sources and seats

`IMenuInputSource` (`CSVM/src/UI/Menu/MenuCommands.cs`) is one seat's source: `Poll(dt)` returns
that frame's `MenuCommands`, `Prime()` seeds edge detection so a button held from before the menu
showed is not a fresh press, `CapturingText` says whether typed characters feed a text field, and
`DeviceLabel` names the device for a join strip. `MenuCommands` is already device-neutral: `MoveY`
and `MoveX` with auto-repeat applied, the edges `Accept`, `Back`, `Join`, `Loadout`, `Contents`,
`Erase`, the `Typed` characters, and an optional `MenuPointer` (window pixels, `Pressed`, `Clicked`
on the press edge, `Wheel` as the steps turned since the last poll and positive toward a list's
foot; null when the seat's devices have none). A presentation reads meaning and never a key, button
or axis. A presentation that polls its seats also reads the campaign flow's `Film`
(`CSVM/src/UI/Screens/CinemaHandoff.cs`) before it applies a frame: a cinema stops and hands off inside the
input flush, ahead of that poll, so the press that skipped the film would otherwise land as an edge
on the screen the film just opened.

The shipped sources: `BuiltInSeat` wraps one `MenuInput` (seat 0's reads the keyboard plus every
unclaimed pad; a joined seat's reads its one pad); `PointerSeat` wraps a seat and adds the mouse as
its pointer through three injected delegates; `MenuIdleSource` is a seat with no device, what the
`--debug-join=` aid seats. A source is not synonymous with a pad, and a later flight-control binding
plugs in as another `IMenuInputSource` with no change to any presentation.

The seats themselves are the `PlayerSetupFeature`'s. Once that feature is registered,
`MenuHost.Seats` is its live source list, `MenuHost.AddSeat` joins through it, and a join made
anywhere shows up in every presentation's `Seats` read. The pad side (`MenuSeatDevices`,
`CSVM/src/UI/Screens/MenuSeatDevices.cs`) is presentation-side and shared by both: seat 0's claimed pad,
hotplug reconciliation, the gesture scans (each presentation decides on which screens they are
open), and `FlightPads`, the binding a launch carries per seat. Built-in keeps the Start-to-join
scan (`PrimeJoins`/`ScanJoins`) on the screens that launch a flight and calls `ClaimP1Pad` every
frame off its Plane screen, so the pad seat 0 steers with is seat 0's for good and can never join
as a further seat. Original signs pads on at one screen only, the join board (`OriginalJoinBoard`,
behind the top level's JOIN BOARD door), through `PrimeBoard` and `ScanBoard`: A signs a pad onto
the next open seat, B on a seated pad gives that seat back, and the captain's Start casts off.
`OriginalShell.JoiningOpen` is true on the board alone, so the screens that launch a flight (Free
Flight, Dogfight, Instant Action and the campaign flight check) only read the roster the board
wrote. Once a second seat has signed on, Original draws a seat strip over every campaign board and
over the Instant Action screen as an overlay; a solo campaign shows the authored board alone. That strip is Built-in's own chip row, the
player tags alone in the top-right corner each in its seat's identity colour over a dark ground
(`CSVM/src/UI/Boards/SeatStrip.cs` holds the shape the two share, and the Instant Action screen asks for
the more opaque ground its light paper needs). The sortie screens keep their own strip lower down,
where the device and the pick status stand beside the tag. A
presentation with a pointer maps the window-pixel pointer into its own space; Original does it
through the same `BoardFit` its board view draws with. Built-in reads no `MenuPointer` at all: its rows are Godot
`Control`s, so player 1's rows take Godot's own hit test through `gui_input` (`LaunchMenu.Pointable`)
and fold the mouse into the next frame's commands (a hover is the cursor step onto that row, a
press and release on one row is that step plus Accept in one frame, a wheel notch is a step, a
right-button press is Back on every screen but Mode, where Back is the quit; the frame's own step
outranks a hover, so a pad and the mouse cannot each own a row). The centred
layout's lists, the hangar pages and player 1's pane on a split aircraft screen are pointable;
the campaign boards under Built-in are one composed surface and stay on the keys and pads.

Under Original a seat picks its aircraft on a screen of its own, not down a list every seat shares.
Seat 0 picks on the sortie screen or in the Instant Action Pilot Plane row; then each joined seat in
player order gets the per-seat aircraft screen (`OriginalSeatPlane.cs`), the campaign plane-selection
board's shape over the sortie roster, where Accept selects and a second Accept confirms. Back and
CANCEL SELECTIONS each take back a selection, and over the open list each leaves the walk for the
screen it came from with every seat kept, so no press on that screen unjoins: a pilot leaves the
sortie with Back on the Instant Action screen or with their device. Joining stays closed on that
screen. The last seat's confirm is the launch, on either sortie screen as on Instant Action, so seat
0's pick is the ready and the walk is the launch; where FLY's own gate is unmet (Dogfight without a
second seat, a sortie with no map picked) the screen the walk came from returns instead with FLY to
press, FLY going live only when every joined seat is Confirmed. Over a selection the screen also
offers WEAPON LOADOUT, which opens the loadout chrome on that seat's own fit and airframe and
returns to the picker, so every joined pilot picks weapons as well as an aeroplane and the choice
rides that seat's own `Fit` into the launch; the original has no second pilot, so this is a remake
decision and not fidelity. The picking seat's own device drives that screen and the Weapon Loadout
it opens, and the mouse, which rides seat 0's source and is the one device a pilot without a pad of their own
can pick with; seat 0's stick, buttons and keyboard move nothing there, `StepSeat` reducing seat 0's
frame to its pointer while another seat picks. CANCEL SELECTIONS is therefore the pointer's own Back,
its second press leaving the walk once there is no selection left to drop. Nothing on that screen is
decoded.

The wheel and the thumb reach the lists through one seam. `OriginalShell.Lists` answers the screen's
scrolling lists as `OriginalList` records, topmost first, each a key, a `ListWindow`
(`CSVM/src/UI/Boards/ListWindow.cs`) the list widget built from its own geometry, and the write that puts
the window's first row somewhere else. Nothing is listed under a dialog, and while a drop-down is
open its list is the only one, since it hangs over the screen. Per frame the shell takes the thumb
first (a held thumb owns the pointer until it is let go, and the click that took hold activates
nothing under it), then a wheel step over the list the pointer stands in, then re-reads the rows and
hit-tests. A write that moves a window also pulls the focus to the window's nearer edge when it
stood on a row the move would hide, because the window otherwise follows the focus straight back.
The arrows and the keyboard do not go through any of this, so the wheel is an addition on top of the
decoded screens rather than a change to them. Original's mouse is the only one that reaches a list
this way; Built-in's rows are Godot controls and take the wheel through `gui_input`, as above.

The windows themselves are the list widgets': `CampaignBoards.ComboWindow` for an open campaign
drop-down, `CampaignPreviousMissionsPage.PointerWindow` for the scrapbook's contents page, and
Original's own for the hangar's dropdowns, Instant Action's dropdowns and contents window, and the
sortie screens' aircraft column. An open dropdown on Instant Action shows at most the authored
`TotalDisplayed` rows and keeps every item as a row keyed `<key>:<index>`, drawn and hit only inside
the window, so a scripted pose and a suite walk still pick by index whatever the window shows. Every
one of those thumbs is as long as the share of the list its window shows, its scroll tile drawn
stretched to that length and never shorter than the tile's own height, so a long list wears a short
thumb with a long free run and a barely scrolling one wears a thumb that nearly fills its track. The
Controls page's key list and the scrapbook's contents page still draw a fixed tile.

## Options, selection and availability

`OptionsStore` (`CSVM/src/Utils/OptionsStore.cs`) is the one process-wide options file,
`user://options.json`, independent of any profile: version-tolerant (an unknown version invalidates
the file, an unknown value drops only that field, a field the file does not carry reads as never
set), read as empty when missing or malformed, written atomically. Its word-valued fields are
`menuPresentation` (`built-in`, `original`; loaded and saved back so a file an older build wrote
round-trips, and read by nothing), `graphicsMode` (`original`, `enhanced`), `difficulty`
(`normal`, `hard`, `hardest`), `displayMode` (`windowed`, `borderless`, `fullscreen`) and `vsync`
(`on`, `off`, `60`, `120`, `144`); a value outside a field's set reads as never set. Two fields are
not words: `monitorIndex` is a screen index rendered decimal and `resolution` is a canonical
`1920x1080`, both validated by shape, so a malformed one is dropped the same way an unknown word is.
The four volume levels (`audioMaster`, `audioMusic`, `audioEffects`, `audioVoice`) are whole numbers
on `AudioMix`'s 0..100 and are validated by range, dropped the same way again. `nearestAfterKill` is
a switch, a JSON boolean, so a value of any other kind is what gets dropped there.
Shape is all the store can prove. Whether that screen is plugged in and whether it offers that mode
are questions for the caller holding an engine, which owns the fallback.

`resolution` is the one field a player is meant to be able to set by hand. Both presentations' size
rows offer the standard sizes the screen can hold (a 4:3 ladder of 800x600, 1024x768, 1280x960 and
1600x1200 among them), and a `resolution` the table does not carry stands beside them as an entry of
its own, where it sorts: the row opens on it, a step can leave it and step back onto it while the
page is open, and an accepted page writes it back unchanged. The screen still has to be able to hold
it, so a size larger than the screen reads as never set and the row shows the screen's own size. What
the size then means is the display mode's to say, for a hand-written size as for a listed one:
windowed takes it as the window's size, exclusive fullscreen as the size the game draws at inside a
screen-sized window, and borderless ignores it and runs at the desktop's own size.

**An option is a store field plus a row in each presentation's Options screen.** Adding one means
a nullable field on `OptionsDef` with its accepted-value set (or its shape check, and the canonical
form beside it so the writing and validating sides cannot drift), the two writes in `Serialize` and
`Deserialize`, a row in Built-in's Options screen and an entry in the table Original's Game Options,
AUDIO or VIDEO page draws its rows from, a value on `OptionsApplyExit`, and the line in
`Launcher.ApplyOptions` that saves it. The store's
`Version` does not move for a new field: a missing field already reads as never set, so a file
written before the field existed loads with everything it does have, and the version gate is
reserved for a field whose meaning or shape changed. Whichever module consumes the option decides
what "never set" falls back to and where the saved value sits among its other sources; for the
graphics mode that is `GraphicsMode.Resolve`, where the `--graphics=` flag beats the saved option,
which beats the `graphics.mode` config key (`docs/cli.md`); for the difficulty it is
`SessionSpec.WithSavedDifficulty`, applied by `Launcher.LaunchSession` at every launch, where a
parsed `--difficulty=` flag beats the saved word, a `--det` run reads no saved option, and the
default is `normal`. The nearest-after-a-kill targeting setting (`nearestAfterKill`, a boolean
rather than a word, so what stands in for an unknown value is a JSON kind the reader drops) takes
the same rule less the flag in `SessionSpec.WithSavedNearestAfterKill`: no flag names it, a `--det`
run reads nothing, and never set is off, which is the decoded head rule
(`docs/org/targeting.md`). The controller-rumble toggle (`rumble`) is the one boolean that reads the
other way, since the original rumbles unless the hardware is absent: never set is on, and
`Launcher` shuts it under `--det` so no deterministic run reaches the hardware on the desk
(`docs/org/input.md`). For the V-Sync choice it is `VSyncSetting.Resolve`, where `--no-vsync` beats
the saved word, which beats the `display.vsync` config key, which beats V-Sync off, and for the
display mode `DisplayModeSetting.Resolve`, where the saved word beats the borderless default and
there is no flag or config key above it. The window size is `ResolutionSetting.Resolve`, the same
two layers over the chosen screen's own size, with the extra rule that the saved size has to be one
that screen offers: a size it does not offer falls back to the screen's size rather than to the
nearest, since every other option falls through to its own default and a nearest match would hand
the player an aspect ratio they did not pick. The screen is `MonitorSetting.Resolve`, the one setting
whose saved value can name something that is not there: an index no screen answers to is dropped like an
unknown word and the window stays on the screen it already stands on, which is the primary on a launch
that has moved nothing. A display setting is also the case where the apply does
more than save, since `Launcher.ApplyOptions` puts the chosen pacing, mode and size on the window
there and then rather than at the next start. Those calls run in the order the window needs them: the
screen the window sits on first, since a mode applied before the move would fill the screen the
window is leaving, then the mode, then the size, then the pacing. The startup half of the pair is not
symmetric: the pacing is applied on every launch, the mode and the size only on a session someone is
at, a scripted run's window being hidden off screen with its capture compared against the viewport
`project.godot` pins.

A screen never writes the store. Both values ride the exit and `Launcher.ApplyOptions` is the only
writer, so the options file has exactly one, and no test or suite that drives an Options screen
through Apply can write the player's own file. Nor read it: `--run-tests` points
`OptionsStore.UserOptions()` at an emptied scratch directory (`OptionsStore.DirectoryOverride`)
before any suite runs, so every driven Options screen opens on the shipped defaults whatever the
player last saved at the controls.

`PresentationResolution` (`CSVM/src/Utils/PresentationResolution.cs`) fixes the precedence:
`--force-builtin`, then `--presentation=<token>`, then Original. ⚠ No saved word takes part and no
Options screen offers a presentation row: the two flags are the only doors to Built-in, and a
`menuPresentation` an older build saved is left in the file unread. Availability is checked after
the request is picked and never rewrites it, so the startup log's `requested=` (`MenuHost.Requested`)
names what was asked for even when `MenuHost.Selected` fell back to Built-in.

`MenuHost.Select(forceBuiltIn, cliOverride)` answers availability as registration plus
the owner's `Availability` delegate, a function from `PresentationId` to a reason or null. Built-in
is never asked. `Launcher.OriginalAvailable` is the shipped delegate: it loads the decoded layout
through `OriginalAvailability.Load`, which refuses a tree stamped behind
`OriginalAssetManifest.StampSchema`, a missing or unreadable layout, a layout with no `[MainMenu]`,
and any required file the manifest names as missing or unreadable, each with one reason the host
appends to its fallback reason and logs. The delegate is asked on every `Select`, which the shipped
build makes once per process start; an Options apply and a return from flight never re-select.

Every presentation exposes Options. Built-in's is the Mode screen's Options row (`--menu=options`); Original's is the Game Options
page behind its Preferences page's first door (`--menu=game-options` under
`--presentation=original`), with the graphics mode on the VIDEO page behind the third
(`--menu=video`), the four volume levels on the AUDIO page behind the second
(`--menu=audio`) and the keymap on the CONTROLS page behind the fourth (`--menu=controls`, and
`--menu=keys` for the KEYS AND BUTTONS page behind its own door). Built-in's one screen carries
every setting Original spreads over those pages, its rows in its own stepper
convention: the difficulty, the opening view, the automatic head turn, the nearest-after-a-kill
targeting switch and the rumble, the graphics mode, the monitor, the window size, the display mode
and the V-Sync choice, then the four volume levels. Built-in has no continuous control, so a level
is a stepper moving by the AUDIO page slider's own keyboard step (`SliderControl.KeyStep`, five)
and clamped at both ends; a step that moves nothing writes nothing, so a level never touched stays
never set. Built-in has no live preview either: a level is heard once the apply writes it. Sixteen
rows overflow the band, so the screen is a window of the Controls list's height with its position
in the heading. The two presentations read the four display settings through one rule
set (`CSVM/src/UI/Menu/DisplaySettingRows.cs`) over the same per-machine enumerations, so a saved
value cannot read one way on the VIDEO page and another on Built-in's screen. Every option page reads the saved options from the store on entry and leaves
through an `OptionsApplyExit` carrying every choice, whichever page it was sent from, so the store
keeps its one writer. Both graphics choosers cover `original` and `enhanced` and say in their description that
the choice takes effect on the next start, since the mode is resolved once at launch and applying
it rebuilds nothing; while the choice differs from the running mode the description names the
running mode and says a restart is still owed, read off `GraphicsMode.Enhanced`. The
startup recovery is `--force-builtin`, which beats everything and rewrites nothing.

**What an Options page focuses with, and what it draws focus as.** Two choices on these pages are
not derivable from the layout, because the authored data offers a plausible wrong answer for each.

The focused row takes the file-wide `ACTIVE`, not the page title's colour. The Preferences page
authors a title colour that is a duller grey than the description cream every unfocused row is
written in, so a row focused with the title colour reads as the disabled one, inverting the page.
`ACTIVE` is what every other Original screen already focuses with, so taking it keeps this family
from being the one that dims what it highlights. The rest of the palette does come from the page:
the description colour for row text, the title colour for the heading, and the paper plaque's label
tail for the chooser.

A slider's focus is the focus box and the wash under it, because the thumb art carries one frame
with no focused or pressed state. The box is the readable half. The wash lands over a widened press
region around a three-pixel slot, so on its own it is a faint band over mostly background; it stays
only because the box needs a region to enclose, and without it the outline reads as four loose
lines. Where neither piece of art is measurable both stand as rectangles, the way a missing plaque
leaves an outlined label, so the control still shows its level. The box is a focus mark rather than
standing chrome, so exactly one row carries it: drawn on every row it becomes a permanent hard box
around rectangles the layout authors at differing widths.

## Audio

`IMenuAudio` (`CSVM/src/UI/Menu/IMenuAudio.cs`): `Cue(MenuCue)` plays one semantic cue by name,
`BeginNarration(wavName)` starts spoken narration, replacing any playing and ducking the music,
`EndNarration()` stops it, idempotent, and `PreviewMix(levels, moved)`/`EndMixPreview()` carry the
mix a page that sets one stands at. The presentation chooses which cue to ask for and when; the
service (`MenuAudioService`, `CSVM/src/Session/Launch/MenuAudioService.cs`) owns lookup, decoding,
playback, volume, the buses and the handoff into a launching session. A cue name the table lacks, a
missing file or a failed decode is logged once and cached as silence; a presentation never learns
whether a sound exists.

**The bus model and the four levels.** The process runs four audio buses, shipped as
`CSVM/default_bus_layout.tres`: Master, with Music, Effects and Voice sending into it. Every site
that builds a player names its category at construction (`Utils/AudioBuses.cs`), because Godot
resolves an unknown bus name to Master with no error and a misplaced player is otherwise silent
about it; the `audio-buses` suite walks the live tree and fails on any player left on Master. The
AUDIO page carries four levels on 0..100, Master, Music, Effects and Voice, and a fresh install
opens on 100, 50, 50 and 50, the authored `CurrentValue` of the three category rows and full on the
added one. `Utils/AudioMix.cs` turns them into one gain per category bus, `category/100 x
master/100`, so Master is a multiplier over the other three rather than a level of its own.

**Bus 0 is not part of that mix.** It carries the developer gain alone, `--volume=` over the
`audio.volume` key over silence in a repo run (`Utils/MasterVolume.cs`), and `AudioMix` refuses to
write index 0 by construction. The two gains therefore reach the output as a product: `--volume=0`
still silences a scripted run whatever the saved levels say, a full-volume launch still leaves bus 0
untouched, and a `--det` launch reads no saved level at all and mixes the shipped defaults. The
`audio-levels-launch` suite drives that ladder from a parsed command line, since a screenshot proves
nothing about audio and a golden sweep cannot see the `--det` drop fail
([verification.md](verification.md)'s INSTR-45 and DET-14).

**The mix preview.** While a page that sets the four volume levels is open, it states them every
frame and names which one that frame moved (`MenuMixLevel`, the page's own rows and never a bus).
The service applies them through `AudioMix` and sounds the moved category on a player of its own:
Effects over `SFX_LOOP.WAV` and Voice over `VOICE_LOOP.WAV`, the two of the original's three preview
clips this port needs, since Music and Master are already audible through the menu's own score. A
clip that is still running is left to run, its loudness following the bus the level moved, so a drag
sounds one clip rather than one per frame. `EndMixPreview` puts back the gains that stood when the
page opened and is called on every door out of the page and on `Hide`, so a preview cannot survive
the page; an accepted mix is written and reapplied by `Launcher.ApplyOptions`, which stays the
options file's one writer. ⚠ Previews are off in any run that drives itself (`--det`,
`--run-tests`, `--screenshot`), so a scripted run's mix cannot become a function of a menu walk.

**A clip on the move is a deliberate departure.** The original's own page
(`extracted/rof/ASSETS/SCRIPTS/AUDIO.SCRIPT`) builds one sound object per category on `gui_create`,
starts them there, sends only `setvolume` as a slider moves and stops all three on `gui_destroy`;
no play message reaches a category's object on a move. This port fires a clip on the level that
moved instead, so a slider is audible at the moment the player touches it rather than only while
something happens to be running. Whether the original's clips loop or play once is undecodable from
the script: `ZB = 0` stands on every `@ctl@SK` object the shipped scripts build, including
`GLOBALS.SCRIPT`'s menu music, which plays on while the menu is up, so the field settles nothing.
Film of the original is the only thing that would.

The cue table (`MenuCueTable`, `CSVM/src/Session/Launch/MenuCueTable.cs`) resolves the four names the
original's globals script binds: `menu.rollover`, `menu.click`, `menu.text`, `menu.text-error`, each
to a wav under the extracted rof tree's `ASSETS/SOUNDS`. Original asks for all four
(`OriginalCues`); Built-in asks for none and uses the service for briefing narration alone. A new
presentation may request these names or add its own to the table; the wavs it resolves to must come
from the player's extraction or from assets the repository may ship. Narration is begun by
whichever presentation shows a briefing and ended by it on every door out and in `Hide`; the
service knows nothing of which screen is up.

## Launch

`MenuExit` (`CSVM/src/UI/Menu/MenuExit.cs`) is the one way out, handed to `IMenuHost.Exit` and
consumed by `Launcher.OnMenuExit`. The hierarchy is closed:

| Exit | Carries | The consumer's action |
|---|---|---|
| `LaunchExit` | chapter, one `MenuSeatChoice` per seat, `MenuMode`, an `InstantActionDef` for Instant Action, a `VersusRules` for Dogfight, a `MenuNetLaunch` for a networked one | derive the session spec from the CLI plus the payload, bind the seats' pads, build |
| `CampaignMissionExit` | the profile name, the `cm_sequence` position, one `MenuSeatChoice` per joined human | the same, over the campaign's story position |
| `QuitExit` | nothing | quit the process |
| `OptionsApplyExit` | the graphics-mode and difficulty words, the four display settings, the four volume levels, the nearest-after-a-kill switch, the controller-rumble toggle, the opening view and the automatic head turn | save every one of them, then one frame later end the presentation and show it again at its top level |

`MenuSeatChoice` is the plane node, the pad devices the seat claimed, the fit and, for a saved
custom plane, its resolved `CustomPlaneDef`; the consumer never reads a store. The features build
these exits (`FreeFlightFeature.BuildExit`, `InstantActionFeature.BuildExit`,
`PlayerSetupFeature.BuildExit`, `CampaignFeature.BuildExit`), so a presentation assembles no payload
of its own beyond handing the feature the seats. The host hides the presentation before the sink
runs, and a failed build shows the same instance again where it stood.

Dogfight's two match rules, the kill target that ends a match early and the match clock in minutes,
are the `PlayerSetupFeature`'s (`KillTarget`, `TimeLimitMinutes`, stepped by `StepKillTarget` and
`StepTimeLimit`, either at 0 disabling that limit). They start at the command line's own defaults of
5 kills and 5 minutes, ride `LaunchExit.Match` into `SessionSpec.FromMenu`, and lose to an explicit
`--vs-kills=`/`--vs-time=`, one flag beating only the rule it names. Built-in offers both as two rows
under the map list on its Dogfight map screen. Original offers neither, because its Dogfight screen
is remake-designed with no authored layout section to take a widget, so its Dogfight launches carry
the defaults or whatever the flags say. A presentation that wants the rules on a screen reads the
feature's two values and steps them; nothing in the launch leg changes.

A networked Dogfight leaves through the same exit with one more field. `MenuNetLaunch` is the open
transport and which end owns the match, built by `NetPlayFeature.BuildLaunch` and taken by the
session, which from then on steps it and closes it; the door keeps only the router's port mapping,
so it can give that back when the flight ends. The local two-seat minimum does not apply to such a
launch, since the opponent is at another machine. Built-in offers the door as the Mode screen's
last row. Original does not, for the same reason it offers no match rules: its Dogfight screen is
remake-designed with no authored layout section to take one. The feature is shared, so a board
there is rows and a sink, not a second mechanism.

## Return

`MenuReturnDestination` (`CSVM/src/UI/Menu/MenuReturnDestination.cs`) is where the player stands
when the menu comes back, said semantically; the hierarchy is closed:

| Destination | Raised by | Built-in lands on | Original lands on |
|---|---|---|---|
| `TopLevel` | the cold start, a Free Flight or Dogfight sortie left early, a failed build, every switch | the Mode screen with its cursors kept | the top level with its list cursors kept and every pick dropped |
| `InstantAction` | an Instant Action sortie left early | the wizard's first screen, over the setup that flew | the Instant Action screen, over the same setup |
| `CabinReturn(profile)` | leaving a campaign screen for the cabin, and a campaign mission left early | the cabin over the named profile, re-read from the presentation's store | the same, through its own campaign graph |
| `DebriefReturn(profile, missionSeq)` | a campaign mission's end, won or lost | the scrapbook on the flown mission | the book on the flown mission |

Which of them a flight comes back to is the launch's own business: `MenuReturnDestination.ForLaunch`
reads it off the exit that started the flight, and `Launcher` holds that answer for every way a
session ends without a result (the pause sheet's Exit, the wrap-up board, the scoreboard). A
campaign mission left early lands on the cabin and never the debrief, since an abandoned mission
was not flown; a mission that ends raises `DebriefReturn` instead.

A destination names where the player stands and never a store or a screen id. The store a profile
is re-read from is the presentation's own (`user://Profiles`, or the scratch store a suite sets), and
a destination a graph lacks maps to the nearest one it has, with a logged warning when a named
profile cannot be read. The `--menu=` aid is not a destination: it reaches the cold start alone (the
launcher parks it, the factory reads it, the first `Show` consumes it), so a return after a flight is
always one of the four above and never the aid's screen again.

## The asset policy

Built-in is usable with no extracted menu artwork: where it draws extracted art (the campaign
boards, the hangar's blueprints) it degrades to its hardcoded chrome or to no picture, and it never
refuses to run.

Original is gated before entry. `OriginalAssetManifest` (`CSVM/src/UI/Menu/Original/OriginalAssetManifest.cs`)
derives its classification from the decoded layout on every start: the art of the sections
Original composes is required, less two short tables (rows it never draws, and rows it draws whose
file the screen survives the absence of, which today is the two background movies); every other
section's art, those rows' art and the media a script names are optional; the files the scripts name
and Original draws anyway (the two pointers, the font, the two export plaques) are required. `Check`
reads no bitmap (existence plus the PNG signature and header size for required entries, existence
alone for optional ones) and answers one report naming every fault with its section, row and file.
A required fault makes the presentation unavailable and Built-in runs with the reason logged; an
optional absence is logged once and degrades where it is drawn (a row with no readable strip keeps
a fallback rectangle and no picture). The manifest's `StampSchema` is the extraction stamp's own
schema (`ExtractionStamp.Schema`, recorded in `extracted/VERSION.json`), so a tree extracted before
the layout decode, or before the movies were copied into it, is refused with the re-extract
instruction rather than read as empty or opened on a menu with nothing running behind it; the
extractor re-stamps an existing tree without re-extracting it.

The classification is reconciled against what the screens draw: `OriginalCoverageTests` collects
every screen-chrome name its journeys draw and fails any the manifest classes optional. Movies are
outside that reconciliation on purpose, being the one thing a screen draws and survives without. No game asset enters
the repository; every test over real data is an `[ExtractedDataFact]` that skips when the extraction
is absent, and every fixture is hand-authored.

## The namespace seam and the scans

Shared contracts, features and readers live in the namespace `CSVM.UI.Menu` exactly (folder
`CSVM/src/UI/Menu/`). Each presentation lives in a sub-namespace (`CSVM.UI.Menu.BuiltIn`,
`CSVM.UI.Menu.Original`) and may depend on anything, `Godot` included. `ComposedBoard`, `BoardFit`
and the other board types stay presentation-side in `CSVM.UI.Boards`, beside the other flat UI
sub-namespaces (`CSVM.UI.Campaign`, `.Screens`, `.Hangar`, `.Overlays`, `.Labs`).

Two scans over the compiled metadata enforce it (`CSVM.Tests/MenuNamespaceDependencyTests.cs`,
through `AssemblyDependencyScan`, which walks signatures and method-body IL alike without loading
the assembly):

- no type in `CSVM.UI.Menu` references `Godot.*` or any `CSVM.UI.*` type outside that exact
  namespace, which is what keeps every feature free of both presentations and of the engine;
- nothing in `CSVM.UI` or any of its sub-namespaces names `GameSession`, `Launcher` or
  `LauncherContext`, which is what keeps every presentation from building a session or reaching
  the launcher.

The scanner's own fixtures prove it sees a signature-level and a body-only reference, and a scan
matching no types fails rather than passing. A new presentation's namespace falls under the second
scan automatically; anything it adds to the shared namespace falls under the first.

## The `--menu=` aid convention

`--menu=<value>` opens the cold start on one screen for a `--screenshot`, and its values belong to
the active presentation: every value in [`cli.md`](cli.md)'s bullet is Built-in's unless
`--presentation=original` is set, in which case the same flag carries Original's own values
(`free-flight`, `dogfight`, `instant-action`, `instant-action:pilot-plane` with its Pilot Plane
list open, `instant-action:weapon-loadout` on the pilot's loadout screen and
`instant-action:lives[:<count>]` with the remake-only Lives box focused, at Unlimited without a
count and at that count with one (the screen opens on one life, so neither reading is a state a
plain shot of it can show), `instant-action-wrapup` and `instant-action-wrapup:failed` on the
wrap-up page a flown mission's ending lands on, over a sample stunt run in each outcome, and
`instant-action-wrapup:long` over a seventeen-zone run whose splits take three post-its,
`instant-action-wrapup:photos` and `instant-action-wrapup:long-photos` over the same two runs with a
stand-in photograph per zone (the long run's last one still on its way) (Built-in
shows a board inside the flight instead and has no menu page to open), `options`,
`game-options` and `game-options:open` with its Difficulty list standing open, `audio` and
`audio:mixed` with its four sliders at four distinct levels, `video`,
`video:checked` with its Enhanced Graphics box ticked and `video:open` with its Resolution list
standing open, the one leaf list whose sizes can outrun the window its row authors,
`controls`, `keys` and `keys:<category>` with the KEYS AND BUTTONS page standing on the tab of that
name (spaces and case ignored), `keys:other` being the one category that outruns its list window and
`keys:targeting` the eleven targeting actions, `credits` and
`credits:about` with the About box standing over it, `join-board` on the crew manifest with nobody
signed on and `join-board:<pads>` with that many entries posed as signed on, the
`plane-*` hangar poses with `plane-construction:open` standing the airframe list open on a row the
hub's figures preview, `plane-construction:overweight` on a build past its capacity,
`plane-construction:defaults` with the airframe swap's three-button question standing over an
edited build and
`plane-paint:decals` standing the nose decal picker open as its five-across grid, `campaign`
and the shared scratch-store campaign poses, `campaign-delete`, `connection` on the Multiplayer
Connection page, and `connection-games` and `connection-games:searching` on the LAN games list
over an in-process network holding five sample games, one of another build version, or none, and `lobby[:host|guest[:tab]]` on
the Multiplayer Lobby over an in-process wire with two guests), and any other value opens that 
presentation's top level. Built-in's values and output stay stable whatever presentation is added.

A new presentation's aids follow the same rules: they select a screen of its own graph, they never
write into `user://Profiles` or `user://Planes` (the campaign poses read `CampaignAidProfiles`'
scratch store; a hangar pose commits nothing), the one door onto the real profile store is the value
`campaign` (`CampaignAidProfiles.PlayerDoor`), each value is documented in `cli.md`'s `--menu=`
bullet under its presentation, and no flag is added, so `cli.md`'s flag index and the parser's count
stay equal. An aid reaches the cold start alone and is consumed on the first `Activate`.

## What a new presentation does not inherit from Original

Original's fidelity choices are Original's, and a presentation registered beside it starts from
the contract above, not from Original's code. In particular it does not inherit:

- **The decoded layout and the 800x600 authored space.** `MenuLayout`, the layout sections, the
  widget rows, the four-frame button strips and their measured sizes are Original's evidence base.
  A new presentation may read `MenuLayout` or ignore it entirely; nothing in the host requires a
  decoded geometry.
- **The 4:3 board fit.** `BoardFit`'s uniform fit, centring and letterboxing, and the nearest
  sampling of extracted art, are the rule for screens composed in the original's coordinate space.
  A presentation that lays out for the window's own aspect owes `BoardFit` nothing.
- **The remake-only screens and rules.** Original's Free Flight and Dogfight doors and screens, its
  wallet-free hangar entry and Weapon Loadout screen behind the Instant Action screen's two buttons,
  the words and control kinds its Game Options rows take, the seven category tabs its KEYS AND
  BUTTONS page splits this port's three keymaps across, its keyboard and pad focus over a
  pointer-driven original, and its pointer hotspot are
  readings recorded in the inventory as remake-only. A new
  presentation makes its own choices for the same operations and records them the same way.
- **The pointer bitmaps and the cue names.** The two extracted pointer bitmaps and the four cue
  names are what Original draws and asks for. A presentation with a pointer draws whatever it
  likes; one with sounds asks the service for names of its own or reuses these.
- **The shared campaign board component.** Original hosts Built-in's campaign pages inside its own
  graph (`CampaignBoards.For` over a `CampaignFlow` of its own, mirroring the screen and row) so the
  campaign screens look the same in both. That is a choice, not a requirement: a new presentation
  may compose the campaign feature's state its own way, and the classification of what is shared
  (`CampaignFeature`) against what is presentation-side (the stack, the cursor, the refusal line,
  the modal, the working copies, the reveal's clock) is what it reads to decide.
- **The asset manifest's classification.** The required set is derived from what Original
  composes. A new presentation that needs extracted art supplies its own availability answer
  (below) and its own required set; one that ships no extracted art is available whenever it is
  registered, like Built-in.
- **Original's `--menu=` values.** The aid names are per presentation; a new one mints its own.

What it does inherit, and may rely on: the features and their operations, the seats and their
semantic commands, the audio service and its narration handoff, the store and the resolution rule,
the four exits and the four destinations, the debug-join seating of device-less players, and the
launcher's whole leg (spec derivation, pad binding, the build, the failed-build return, the
frame-deferred Options restart).

## The extension checklist

In order. Each step names the file it touches and the test that proves it.

1. **Mint the identity.** Add a `PresentationId` token beside `BuiltIn` and `Original`
   (`CSVM/src/UI/Menu/PresentationId.cs`). `--presentation=<token>` reaches it with no further
   wiring, and it is the only door: no Options screen offers a presentation.
2. **Create the namespace.** `CSVM/src/UI/Menu/<Name>/`, namespace `CSVM.UI.Menu.<Name>`. Put every
   node, drawing type and screen graph there; put nothing in `CSVM.UI.Menu` that is not shared by
   every presentation. `MenuNamespaceDependencyTests` covers the new namespace with no edit.
3. **Write the screen graph engine-free first.** Original's shape is a shell class over the
   features (`OriginalShell`) driven by `MenuCommands` frames and composing a picture, with a thin
   node (`OriginalPresentation`) that polls the seats, maps the pointer and draws. The shell
   unit-tests with no engine; write those tests against a hand-authored fixture, never against the
   player's data.
4. **Implement `IMenuPresentation`.** `Activate` builds on the first call and maps all three
   destinations (a destination the graph lacks maps to the nearest it has, logged); `Tick` polls
   `host.Seats` every frame (the list is live), sets each seat's `CapturingText` while a text field
   shows, and hands every exit to `host.Exit`; `Hide` keeps state and ends any narration;
   `Deactivate` frees everything. Apply the cold start's aid on the first `Activate` only.
5. **Offer every shared feature's operations.** Free Flight, Instant Action, Dogfight through the
   player setup, the hangar and the campaign, each reached from the presentation's top level and
   each with a way back that lands on the top level with no open campaign, build or dialog behind
   it. Write a coverage test in `OriginalCoverageTests`' shape: every screen reached and left by
   every input family the presentation supports, every exit typed.
6. **Expose Options.** A screen that reads the saved options from `OptionsStore`, offers every
   option the store carries bar the unread `menuPresentation`, and leaves through
   `OptionsApplyExit`.
7. **Register it.** One `registry.Register(PresentationId.<Name>, () => new <Name>Presentation(...))`
   in `Launcher.BuildMenuHost`, reading `_menuAid` for the cold start's aid as the two shipped
   factories do.
8. **Answer availability.** If the presentation needs extracted files, add its check to the
   `Availability` delegate `Launcher` installs (`OriginalAvailable` is the shipped one; extend it or
   compose a second), deriving the required set from what the screens draw and reconciling it in
   the coverage test as `OriginalCoverageTests` does. If it needs nothing, registration alone is its
   availability.
9. **Add its aids.** `--menu=` values under `--presentation=<token>`, documented in `cli.md`'s
   bullet, no flag added. Keep every campaign pose on `CampaignAidProfiles`' scratch store.
10. **Add the driven suites.** One in-engine suite per journey family in `CSVM/src/Testing/`, over
    a real `MenuHost` through `MenuSuiteHost`, standing for the presentation what
    `menu-original-tracer`, `menu-original-instant-action`, `menu-original-hangar`,
    `menu-original-campaign`, `menu-original-cheats` and the Original half of
    `menu-player-setup-seats` stand for Original;
    add the presentation's cases to `menu-launch-return`, which drives every exit and every
    destination at the host's sink. Register each suite in `SuiteCatalog`.
11. **Prove Built-in unchanged.** Shoot Built-in's aids before and after and compare decoded pixels;
    a new presentation makes no Built-in change.
12. **Document it.** Its `##` entries in `architecture/UI.md` with their index lines in
    `architecture.md`, its remake-only readings where they belong, `cli.md`'s aids, and this
    page's registration paragraph.
13. **Run the landing gate.** The complete `.\RunTests.ps1`, then the at-the-controls pass: each
    mode to FLY and back through the pause board's Exit, a campaign mission to its end and back to
    the scrapbook, a real pad's join and walk, the sounds, the pointer, a cold start on an aid, a
    cold start under `--presentation=<token>` and one without it.

## The verification layers

Five layers, each catching what the others cannot:

- **Engine-free contracts.** The seam fixtures (`MenuSeamContractTests`, two fake presentations
  driving one fake feature to the same exit), the host (`MenuHostTests`), the store and the
  resolution rule, every feature's own tests, the shell tests over hand-authored layouts, the
  coverage check, the manifest cases, and the two metadata scans. `dotnet test` runs them all;
  those over the player's data are `[ExtractedDataFact]`s.
- **Hand-authored legal fixtures.** `CSVM.Tests/fixtures/menu-layout/` for the decoder and
  `fixtures/menu-layout-original/` for the shell: invented geometry and file names in the shipped
  file's shape, so no game data is committed and every rule is still exercised.
- **Local extracted-data comparisons.** Built-in's aids shot before and after a change and compared
  by decoded pixels; Original's aids shot at 800x600, 1280x720, 1920x1080 and 600x750 to show the
  fit; the manifest's recovery probes over a scratch copy of the data root, never the player's tree.
- **Driven suites.** The `menu-*` suites and `campaign-layout-parity` through real hosts and real
  shells over the install's own layout, up to the host's sink. The harness runs a suite before any
  session builds, so no suite flies a launch or performs the frame-deferred Options restart.
- **At the controls.** The launcher's leg and everything a suite cannot hear or hold: the flown
  launch and its return in every mode and both presentations, a cold start under each flag, a real
  pad, the sounds, the pointer's rollover and pressed frames.
