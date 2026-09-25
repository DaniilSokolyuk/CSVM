using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Bindings;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.Utils;
using Godot;

namespace CSVM.UI;

/// <summary>
/// The in-game launchscreen shown on a bare launch. Free Flight and Dogfight go straight to
/// Chapter then Plane; Instant Action opens its own five-step wizard. Dogfight withholds the
/// launch until two players have joined; see <see cref="CanLaunch"/>. Input is polled per player
/// through <see cref="MenuInput"/>, not Godot's input map, since the join flow needs a named
/// device; the mouse through the rows' own <c>gui_input</c> (<see cref="PointerEvent"/>). More
/// than one player splits the Plane screen into <see cref="SplitScreen.PaneRect"/> panes.
/// Re-entrant on return from flight; see <see cref="ShowMenu"/>. The Built-in presentation's
/// screen graph: player 1's commands arrive through the host's first seat, every launch and the
/// quit leave through <see cref="IMenuHost.Exit"/>, and narration plays through the host's audio
/// service. Module map: docs/architecture.md. Wizard decode: docs/formats/instant-action.md.
/// </summary>
public sealed partial class LaunchMenu : CanvasLayer
{
    /// <summary>The row that opens the hangar, on the Mode screen and on the Instant Action plane
    /// pick. The original has no button string of its own for it; this is the phrase its own help
    /// text uses (langui 10524).</summary>
    public const string HangarRow = "Build Custom Plane";

    /// <summary>The row that opens the campaign, past the three modes and before the hangar's own
    /// door. The campaign is not a <see cref="MenuMode"/>: it owns its screens through
    /// <see cref="CampaignFlow"/> and only launches a mission from inside them.</summary>
    public const string CampaignRow = "Campaign";

    /// <summary>The row that opens the multiplayer door, the last on the Mode screen. It is not a
    /// <see cref="MenuMode"/> either. The door opens a wire and then walks the Dogfight screens,
    /// so the mode a network match flies is Dogfight's own.</summary>
    public const string NetworkRow = "Multiplayer";

    /// <summary>The row that opens the Options screen, under the modes. Options hold
    /// the process-wide choices (the difficulty, the targeting setting, the graphics mode and the
    /// four display settings). Which presentation runs is not among them: only the two command-line
    /// flags choose Built-in.</summary>
    public const string OptionsRow = "Options";

    /// <summary>The row that opens the rebinding screen, inside Options. It is not beside the
    /// steppers as a further choice: those are process-wide and leave through
    /// <c>OptionsApplyExit</c>, while a keymap is per player and saves itself.</summary>
    public const string ControlsRow = "Controls...";

    /// <summary>What <see cref="ShownScreen"/> reads while the Network screen stands as a guest's
    /// waiting board for a campaign host.</summary>
    public const string CoopWaitScreen = "CoopWait";

    /// <summary>What follows a remote guest's tag on the campaign chip strip. A guest at another
    /// machine holds a seat but no pane here, and the chip says which kind a player is.</summary>
    public const string RemoteChipMark = " net";

    // The multiplayer door's five rows, in the order they are drawn. Two fields a player edits,
    // two ways a socket opens, and the way on to the map. The door's own state is the feature's;
    // these are this screen's row numbers alone.
    private const int NetPortRow = 0;
    private const int NetAddressRow = 1;
    private const int NetHostRow = 2;
    private const int NetJoinRow = 3;
    private const int NetContinueRow = 4;
    private const int NetworkRows = 5;

    // Base metrics at 720p, scaled up on taller viewports (like StuntScoreboard). All TUNE.
    private const int TitleFont = 40;
    private const int HeadingFont = 20;
    private const int CrumbFont = 15;
    private const int RowFont = 22;
    private const int DetailFont = 16;
    private const int FooterFont = 15;
    private const int ErrorFont = 15;

    // The centred layout's content column at 720p: the width every band's text is centred in and
    // the description wraps at, and the vertical padding the header keeps above the title and the
    // footer below the controls line.
    private const int ContentWidth = 560;
    private const int ZonePad = 18;
    // How many description lines the footer reserves. The band's height must not depend on the
    // focused row, so the slot is this tall whatever the row says; a longer description than this
    // wraps into the space above the controls line rather than moving them.
    private const int DetailReserveLines = 2;
    // The separation every band's content column carries between its children.
    private const int ZoneSeparation = 6;

    // The hangar art block's 720p height; the 358x335 TGAs letterbox into it. It
    // stands beside the rows, so it is as tall as the column has room for rather than
    // as short as a block over them had to be.
    private const int HangarArtHeight = 200;
    // Its column's 720p width, to the left of the rows (E47b), and the focused row's own smaller
    // picture under it (E48's 66x66 decal tile).
    private const int HangarArtWidth = 220;

    // How long a pressed plaque holds its depressed frame. Short enough not to lag a press, long
    // enough to be seen at any frame rate the boards run at.
    private const int PressFrames = 6;
    private const int HangarRowArtHeight = 66;
    // Splitscreen plane select (several players): the bottom strip that keeps the breadcrumb +
    // join hint out of the panes, as a fraction of viewport height, and the pane's inner padding.
    // Reference values at 720p. Confirmed at the controls: join/lock feel
    // reads right at 2P and 4P, no retune owed.
    private const float StripHeightFrac = 0.12f;
    private const int PanePad = 10;
    // How many Table of Contents rows the list shows at once. `ia_tl_contents` is one of only two
    // LAYOUT.CSV rows whose last column is a visible-row WINDOW rather than an item count: 14 rows
    // onto the 19 presets (docs/formats/instant-action.md, "Screen controls"). Decoded, not a fit
    // to our own layout, do not "tidy" it to the item count.
    private const int PresetWindow = 14;
    // The Controls list's window, and the three stepper rows above it. The rows are the seat, its
    // mouse sensitivity, and which of the three keymaps is being edited. The keymap row stands
    // last, over the list it picks. Flight alone owns 35 actions, so the list is windowed like the
    // Table of Contents rather than shrinking the whole band to fit. TUNE.
    private const int ControlsWindow = 14;
    private const int ControlsHeaderRows = 3;
    private const int ControlsPlayerRow = 0;
    private const int ControlsSensitivityRow = 1;
    private const int ControlsContextRow = 2;
    // The three rows below the action list, in the original's own order: reset the whole keymap,
    // abandon every staged edit, commit them. The original draws these as persistent buttons on
    // every category page; here they are the tail of the one list this presentation has. TUNE.
    private const int ControlsFooterRows = 3;
    // The Options screen's stepper rows, above the Controls door and the apply row. The screen
    // is a form the cursor walks top to bottom. First the five gameplay settings: the three the
    // Original presentation's GAME OPTIONS page draws, in its order, then the targeting switch
    // and the rumble. Then the graphics mode and the rocket carve under it, and the four display
    // settings in the order the Original presentation's VIDEO page draws them. Then the four
    // volume levels in the order its AUDIO page draws them, then the two doors.
    private const int OptionsStepperRows = 15;
    // How many Options rows show at once. Seventeen rows do not fit the band at 720p, and a band
    // sized to all of them shrinks every row. The screen is windowed at the Controls list's
    // height, which is known to fit.
    private const int OptionsWindow = ControlsWindow;
    // The Controls list's two column widths and the extra band width they need, in ems of the row
    // font and in 720p points. TUNE: measured against the longest shipped action name and the
    // longest four-control row, not decoded from anything.
    private const float ControlsLabelEms = 11f;
    private const float ControlsValueEms = 20f;
    private const float ControlsExtraWidth = 260f;
    // The chip strip's separation, in authored board points like the rest of its shape. It is
    // scaled through the same BoardFit the board itself draws at, so the chips read like part of
    // that screen. The face, the corner inset and the colours are SeatStrip's, shared with
    // Original's own strip. The separation is Built-in's alone, since only this row measures its
    // own text.
    private const float ChipSeparation = 10f;

    // How far below the chips' top edge the network band's line starts, in font heights.
    private const float NetBandLineStep = 1.6f;

    // The three top-level modes, in MenuMode's ordinal order so the row index doubles as the
    // enum value. The enum member stays named Stunt (SessionSpec.cs) though this row reads
    // "Instant Action"; picking it opens the Environment wizard, not the plain Chapter screen.
    // See docs/architecture.md.
    private static readonly Choice[] Modes =
    {
        new("Free Flight", "Explore the map freely, no objectives, no clock."),
        new("Instant Action", "Pick an environment and a mission: ace, squadron, stunt or zeppelin."),
        new("Dogfight", "Splitscreen free-for-all, first to the kill target wins."),
    };

    // The eight chapter worlds: this screen's row text over the shared roster (MenuChapters owns
    // the codes, their order and the Danger Zones flag). ChaptersFor hides the flag-less chapters
    // from Stunt Flying, and a stunt run forced onto them via CLI falls back to free flight
    // (StuntMission).
    private static readonly (string Name, string Code, bool DangerZones)[] Chapters = BuildChapters();

    // The player-flyable roster: the shared Instant Action feature's eleven stock airframes, in the
    // langui 3700 dropdown order the original stores an aircraft as an index INTO, as the
    // (Name, Node) pairs the plane picker's roster is built from. Read off the feature rather than
    // copied, so the two cannot drift apart; stats are loaded lazily from vehicle.json for the
    // focused plane.
    private static readonly (string Name, string Node)[] Planes = BuildPlanes();

    private static readonly Color TitleColor = new(0.96f, 0.80f, 0.35f);
    private static readonly Color CrumbColor = new(0.55f, 0.68f, 0.86f);
    private static readonly Color HeadingColor = new(0.80f, 0.88f, 0.98f);
    private static readonly Color RowColor = new(0.55f, 0.62f, 0.72f);
    private static readonly Color RowFocusColor = new(1f, 0.86f, 0.38f);
    private static readonly Color DetailColor = new(0.66f, 0.78f, 0.92f);
    private static readonly Color FooterColor = new(0.52f, 0.60f, 0.70f);
    private static readonly Color ErrorColor = new(1f, 0.55f, 0.45f);

    // A selected-but-not-yet-flying aircraft. Distinct from RowFocusColor on purpose: the two
    // stages of the pick are the one thing on this screen a pilot must be able to tell apart at
    // a glance, and "the cursor is here" and "this is chosen" would otherwise look identical.
    private static readonly Color RowLockedColor = new(0.55f, 0.95f, 0.62f);

    private readonly Dictionary<string, PlaneStats?> _stats = new();
    // This screen's view of the shared setup's seats, player 1 first, one wrapper per seat with
    // the poller behind it and its last frame; SyncSlots keeps it in step with the feature.
    private readonly List<Slot> _slots = new();
    // Player 1's row controls by absolute row index, as last built, for RowControl.
    private readonly Dictionary<int, Control> _rowControls = new();
    private int _slotsRevision = -1;
    // The menu host: its first seat is player 1's commands, its feature set holds Free Flight's and
    // Instant Action's state and launch rules and the shared player setup, its audio service plays
    // the narration, and every exit goes to it.
    private IMenuHost _host = null!;
    private FreeFlightFeature _free = null!;
    // The Instant Action setup: the wizard's fields (environment, mission type, lives, waves,
    // wingmen and their fit, the applied preset) live here; this screen keeps only its cursors.
    private InstantActionFeature _ia = null!;
    // The shared player setup: the seats, their picks and the launch gate live there; this screen
    // offers them and draws them.
    private PlayerSetupFeature _setup = null!;
    // The multiplayer door: the port, the address, the socket and the two readouts. Optional the
    // way the keymap editor is, so a bare host needs no carrier behind it. The row is drawn
    // either way and refuses when there is nothing behind it.
    private NetPlayFeature? _net;
    // The cursor on the multiplayer door's own screen, and the readout as it last drew. A socket
    // changes state between presses, so the board is repainted off the reading rather than off
    // the input.
    private int _netIndex;
    private string _netStatus = "";
    // Whether the board is standing as a guest's waiting board for a campaign host's launch. It
    // is the Network screen with other rows rather than a screen of its own, since the door and
    // its readout are the same ones.
    private bool _coopWait;
    // The raw poller behind the host's first seat, player 1's, and the pad bookkeeping over it
    // (claiming, joining, hotplug); the commands themselves are read through the seats.
    private MenuInput _player1 = null!;
    private MenuSeatDevices _devices = null!;

    private string _zrdrPath = "";
    private string _dataRoot = "";
    private Screen _screen = Screen.Mode;
    private int _modeIndex, _chapterIndex;
    // The Options screen's cursor and the choices its stepper rows would apply, seeded from the
    // saved options when the screen opens so it shows back what was asked for, not what is active:
    // the graphics mode a running process resolved is the one the process started under.
    private int _optionsIndex, _optionsTop;
    private int _difficultyChoice = Difficulty.Normal;
    // The targeting setting as saved, null while never set, which the consumer reads as off: a
    // screen hands back "never set" rather than a choice the player did not make.
    private bool? _nearestAfterKillChoice;
    // The haptics setting as saved, null while never set, which the consumer reads as ON.
    private bool? _rumbleChoice;
    // The opening view as saved, null while never set, which the flight reads as Chase. Held as the
    // --view= word rather than the enum, the spelling the store carries.
    private string? _defaultViewChoice;
    // The automatic head turn as saved, null while never set. Null leaves the headLook.autohead
    // config key deciding, rather than overruling it with a default of this screen's own.
    private bool? _autoHeadTurnChoice;
    private string _graphicsChoice = GraphicsMode.Default;
    // The rocket carve as saved, null while never set, which reads as off: the original carves
    // nothing in play, so nobody gets a bowl in the ground without asking for one.
    private bool? _rocketCratersChoice;
    // The four display settings, stepped by the four rows under the graphics one. Each is stored as
    // the word the options file carries, never as a row index, so a screen unplugged or a size the
    // monitor stopped offering is answered by the resolver's own forgiving read rather than by a
    // stale position.
    private string? _monitorChoice, _resolutionChoice, _displayModeChoice, _vsyncChoice;
    // The size the options file named when this screen opened, which the size row offers as an entry
    // of its own (ResolutionSizes). It is held apart from the stepped choice, so a hand-written
    // size stays in the list after a step lands elsewhere. A step back then reaches it again.
    private string? _savedResolution;
    // The four volume levels as saved, null while never set, which the mixer reads as the shipped
    // default. A step that moves nothing leaves the field null, so walking a row writes no level
    // the player did not change.
    private int? _audioMasterChoice, _audioMusicChoice, _audioEffectsChoice, _audioVoiceChoice;
    // The Table of Contents' list cursor and the first visible row of its 14-row window; the
    // applied preset itself is the feature's.
    private int _presetCursor, _presetTop;
    // The Controls screen's list cursor and window top. The seat, the context, the focused action
    // and the capture in progress are all the shared feature's, so a switch of presentation keeps
    // them; this screen keeps only where the cursor sits.
    private int _controlsIndex, _controlsTop;
    private ControlsFeature _controls = null!;
    // The rebinding screen's seat bookkeeping, built on the first sync because the feature is
    // fetched in the screen's own construction.
    private MenuControlsSeats? _controlsSeats;
    // The cursors of the Waves, WaveEdit, Wingmen and WingmanLoadout screens: which wave row,
    // which wave is being edited, which of its fields, which wingman field, which fit row. The
    // values under them are the feature's.
    private int _waveListIndex, _waveEditIndex, _waveFieldIndex;
    private int _wingmenFieldIndex, _wingmanFitRow;
    // The stock-fit table and the Ammo Selection rosters, loaded once on first use: the loadout
    // rows are built from an airframe's own gun slots and pylon count, which only this file knows.
    private StockLoadouts? _stockFits;
    // Every human plane picker's roster: the eleven stock airframes then the store's saved
    // customs (PlanePickerRoster, Decision 6). Refreshed by ShowMenu and CloseHangar
    // (RefreshRoster), so a new save appears without a menu restart. Starts stock-only: reading
    // user:// needs the engine, which a bare construction (tests, --menu= screenshots before
    // ShowMenu) may not have.
    private IReadOnlyList<PickerPlane> _roster = PlanePickerRoster.Build(Planes, Array.Empty<CustomPlaneDef>());
    // The saved builds behind the roster's custom rows, refreshed with it. An Ammo Selection list
    // stands on the pylons the row actually bought, so the screen needs the def, not just the name.
    private IReadOnlyList<CustomPlaneDef> _customDefs = Array.Empty<CustomPlaneDef>();
    private MenuMode _mode;
    // The Build Custom Plane flow while it is open, and the screen it was opened from. Both
    // doors (the Mode screen's trailing row, the Instant Action plane pick) come through
    // OpenHangar, so cancelling always lands back where the pilot pressed.
    private HangarFlow? _hangar;
    private Screen _hangarReturn = Screen.Mode;
    // The shared hangar feature every flow here walks: the host's one instance, so a switch of
    // presentation discards the same scratch plane Original would have been editing.
    private HangarFeature _hangarFeature = null!;
    // The page's narration count as last acted on: the host's audio service is asked to begin the
    // narration whenever it moves and to end it the moment the briefing stops showing.
    private int _narrationStarts;
    // Whether the briefing's reveal was running on the previous frame, which is what buys the one
    // repaint after it finishes: the frame that lands the last tween is the frame that stops
    // being a running reveal.
    private bool _briefingRunning;
    // The campaign's out-of-mission flow while it is open. One door (the Mode screen's Campaign
    // row); its own screens are the flow's pages, so a new one needs no change here.
    private CampaignFlow? _campaign;
    // The shared campaign feature the flow walks: the host's one instance, opened over a store on
    // every door and discarded with the flow, so a switch of presentation drops the same seated
    // profile Original would have been reading.
    private CampaignFeature _campaignFeature = null!;
    // A character reached a plane's name since the last frame, so the menu owes a redraw that no
    // polled input asked for.
    private bool _typed;
    // The hangar page's art, as the one texture the shell owns: rebuilt only when
    // the page hands over a different decoded image, since Rebuild runs on every keypress.
    private TgaImage? _hangarArtSource;
    private ImageTexture? _hangarArtTexture;
    // The same pair again for the focused row's own picture (E48's decal tile), which changes on a
    // different beat from the page's art and so cannot share the one slot.
    private TgaImage? _hangarRowArtSource;
    private ImageTexture? _hangarRowArtTexture;
    // The langui table, loaded on first hangar entry (a session that never opens it never reads
    // the file). Null until then; a failed load leaves UiStrings.Empty here.
    private UiStrings? _uiStrings;
    private string _error = "";
    // The join strip as last drawn, _Process redraws when the live roster changes (hotplug).
    private string _stripText = "";
    // --menu=campaign-guestcheck: which guest's flight check the aid asked for, applied by
    // DebugJoin, since the aid runs inside ShowMenu and the players arrive right after it.
    private int _aidGuest;
    // The viewport as the layout was last built for. Every metric here is derived from it, so a
    // resize or a resolution change owes a repaint that no keypress asked for.
    private Vector2 _viewSize;
    // The centred layout's three bands, top to bottom: header (title, breadcrumb, join strip),
    // middle (heading, rows, the art column beside them), footer (the focused row's description,
    // the error slot, the controls line). Each holds one centred content column rebuilt per frame;
    // the bands themselves are built once and keep the fixed heights MenuZones gives them.
    private VBoxContainer _zones = null!;
    private HBoxContainer _header = null!;
    private HBoxContainer _middle = null!;
    private HBoxContainer _footer = null!;
    // The splitscreen plane-select root (one panel per player + a shared bottom strip). Shown
    // instead of _zones on the Plane screen once more than one player has joined.
    private Control _paneRoot = null!;

    // The campaign's composed-board surface, drawn instead of _zones on every campaign screen.
    private ComposedBoardView _boardRoot = null!;

    // The campaign chip strip: `P1 P2 P3 P4` in `SplitScreen.PlayerColor`, top-right, shown while a
    // campaign board is up AND more than one has joined, a solo campaign board looks exactly as
    // it does today. A composed board draws no full join strip by design (RebuildBoard's own
    // comment), so this is a deliberate exception drawn as a shell overlay rather than a page
    // contribution: CampaignBoards' authored geometry has nowhere to put a live, per-frame roster.
    private HBoxContainer _chipStrip = null!;

    // The open network door's band, on its own line under the chips. A cabin draws its footer
    // along the top edge, and one line holding both ran the address into the footer's presses.
    private Label _netBandLabel = null!;

    // Frames left to draw the pressed plaque depressed. The original's own button art carries that
    // frame, and a confirm that changes nothing on screen reads as a dead button on a pad.
    private int _pressFrames;

    // The mouse's commands since the last frame, folded into player 1's next frame by WithPointer:
    // a hover is a cursor step onto its row, a click is Accept, a wheel notch a step, the right
    // button Back. Held for the frame rather than applied in the event, since Rebuild replaces the
    // very controls the event is dispatched through.
    private MenuCommands _pointer = MenuCommands.None;
    // The row the left button went down on and whether the pointer is still over it, Godot's own
    // button rule: a release confirms only inside the control that took the press. Null between
    // clicks, and cleared by Rebuild, which frees the control the press landed on.
    private int? _pressRow;
    private bool _pressInside;

    private enum Screen { Mode, Chapter, Presets, Environment, MissionType, Waves, WaveEdit, Wingmen, Plane, WingmanLoadout, Hangar, Campaign, Options, Controls, Network }

    // What a fit row edits. The reset row carries no slot of its own and is the only one Accept
    // does anything on, since every other row is a live stepper.
    private enum FitRowKind { Gun, Pylon, Reset }

    /// <summary>The name of the last plane the hangar built this session, or "".
    /// <see cref="CloseHangar"/> reads it back out of the refreshed roster to auto-select the
    /// just-built plane (the original's index-11 contract, by name).</summary>
    public string LastBuiltPlane { get; private set; } = "";

    /// <summary>The campaign flow while one is open, or null. Read-only: the flow's own screens
    /// are driven through it, not around it.</summary>
    public CampaignFlow? Campaign => _campaign;

    /// <summary>The profile store the Campaign door and the two flight returns open the campaign
    /// over: <c>user://Profiles</c> unless a driven suite hands in a scratch store first, so no
    /// scripted journey can write a real player's progress. Resolved on every open rather than
    /// cached, so a store set after the build applies to the next door press.</summary>
    public CampaignProfileStore? CampaignProfiles { get; set; }

    /// <summary>The layout every campaign flow opened after this reads its fixed chrome through,
    /// or null for the data root's own decoded layout. A suite sets <see cref="CampaignLayout.Fallback"/>
    /// to compose the same screens with the hardcoded chrome and compare; the game never sets
    /// it.</summary>
    public CampaignLayout? CampaignLayoutOverride { get; set; }

    /// <summary>Where the screenshot key takes its frame from, or null for this menu's own viewport
    /// read back off the frame path. A suite sets it, since a suite runs inside one frame and a live
    /// readback lands only frames later; the game never sets it.</summary>
    public PaneRequest? ScreenshotPane { get; set; }

    /// <summary>The hangar flow while one is open, or null. Read-only, for the same reason
    /// <see cref="Campaign"/> is: its screens are driven through it, not around it.</summary>
    public HangarFlow? Hangar => _hangar;

    /// <summary>The composed board currently on screen, or null when no campaign screen is up.
    /// This is what the pilot is looking at, so a check that the screen keeps up with a running
    /// briefing reveal compares it against a board freshly composed from the page.</summary>
    public ComposedBoard? ShownBoard => _boardRoot?.Board;

    /// <summary>How the centred layout divides the window right now. A check that the screen does
    /// not jump reads this as the cursor moves: the two fixed bands must not move with it.</summary>
    public MenuZones ShownZones => Zones();

    /// <summary>The header band, the fixed one at the top. Public so a check can read the height
    /// the layout is actually holding it at rather than the one it was told to.</summary>
    public Control HeaderBand => _header;

    /// <summary>The footer band, the fixed one at the bottom.</summary>
    public Control FooterBand => _footer;

    /// <summary>The screen showing, by its own name (Mode, Chapter, Plane, ...). A journey check
    /// reads where a press landed without knowing how the screen is drawn.</summary>
    public string ShownScreen => _coopWait ? CoopWaitScreen : _screen.ToString();

    /// <summary>The chips the campaign boards' strip draws right now, player 1 first, or none
    /// while the strip is hidden. A remote guest's chip carries <see cref="RemoteChipMark"/>.
    /// </summary>
    public IReadOnlyList<string> ShownChips => ChipStripShown() ? ChipTexts() : Array.Empty<string>();

    /// <summary>The campaign host's network band as the boards draw it, or "" with none.</summary>
    public string ShownNetBand => ChipStripShown() ? NetBand() : "";

    /// <summary>The middle band's heading as the screen draws it right now.</summary>
    public string ShownHeading => Heading();

    /// <summary>The header band's breadcrumb as the screen draws it right now.</summary>
    public string ShownBreadcrumb => Breadcrumb();

    /// <summary>The footer's controls line as the screen draws it right now.</summary>
    public string ShownFooter => Footer();

    /// <summary>The focused row's description, or the refusal standing in its slot.</summary>
    public string ShownDetail => _error.Length > 0 ? _error : Detail(CurrentIndex);

    /// <summary>The hint beside the join strip, which names where joining opens.</summary>
    public string ShownJoinHint => JoinHint();

    /// <summary>Player 1's cursor row on the screen showing.</summary>
    public int ShownRow => CurrentIndex;

    /// <summary>How many rows the screen showing has.</summary>
    public int ShownRowCount => CurrentCount();

    /// <summary>The text drawn on player 1's cursor row.</summary>
    public string ShownRowText => RowText(CurrentIndex);

    // The chapter roster the picked mode offers, the Chapter screen and everything
    // downstream (breadcrumb, launch) index into this, never the full list. Free Flight/Dogfight
    // only; Instant Action uses Environments/CurrentMissionTypes
    // instead (its own environment list is decoded, not this table's alphabetic one).
    private (string Name, string Code, bool DangerZones)[] CurrentChapters => ChaptersFor(_mode);

    // Dogfight's two match rows sit under the map list, on the same screen rather than a step of
    // their own. The setup is a map and two numbers, and a screen carrying one row would read as a
    // step the pilot has to walk through. Free Flight draws neither, so its map screen is
    // unchanged.
    private int MatchRowCount => _mode == MenuMode.Versus ? 2 : 0;

    // The mission types the picked environment's chapter actually offers: all four, minus Stunt
    // Flying where that chapter's own `disallow_missions` bars it, the feature's own filter.
    private IReadOnlyList<InstantActionMissionType> CurrentMissionTypes => _ia.MissionTypes;

    // The single-player cursor position on the current screen (the plane screen reads
    // player 1's cursor). The Environment and Mission cursors ARE the feature's picks: a dropdown
    // has no cursor apart from its value.
    private int CurrentIndex => _screen switch
    {
        Screen.Mode => _modeIndex,
        Screen.Chapter => _chapterIndex,
        Screen.Presets => _presetCursor,
        Screen.Environment => _ia.EnvironmentIndex,
        Screen.MissionType => _ia.MissionTypeIndex,
        Screen.Waves => _waveListIndex,
        Screen.WaveEdit => _waveFieldIndex,
        Screen.Wingmen => _wingmenFieldIndex,
        Screen.WingmanLoadout => _wingmanFitRow,
        Screen.Hangar => _hangar?.Row ?? 0,
        Screen.Campaign => _campaign?.Row ?? 0,
        Screen.Options => _optionsIndex,
        Screen.Controls => _controlsIndex,
        Screen.Network => _netIndex,
        _ => _slots.Count == 1 && _slots[0].InLoadout ? _slots[0].FitRow : _slots[0].PlaneIndex,
    };

    // The row player 1's mouse steps from: the cursor of the list its rows were built from, which
    // on a split aircraft screen is pane 1's fit row while that pane is in its loadout.
    private int PointerIndex =>
        _screen == Screen.Plane && _slots[0].InLoadout ? _slots[0].FitRow : CurrentIndex;

    // The font the bands and the fit columns are measured in, or null before the theme has one.
    private Font? MenuFont => _zones.GetThemeDefaultFont();

    // The stock-fit table, loaded on first use. A failed load leaves the rosters empty, which
    // shows as a loadout list of nothing but its reset row rather than a crash on the way to
    // flying: the fit is optional and a launch must survive without it.
    private StockLoadouts Fits => _stockFits ??= StockLoadouts.Load();

    // How many rows the Wingmen screen shows right now: the Aircraft field is hidden at
    // 0 wingmen, matching the decoded setup screen's own behaviour.
    private int WingmenRowCount => _ia.NumWingmen > 0 ? 2 : 1;

    // Whether the plane pick offers the hangar DOOR row. The Build entry sits on the Instant
    // Action pick alone; a splitscreen pane never draws it, so no pane's PlaneIndex can
    // point past the roster; the roster itself (stock + customs) is every seat's alike.
    private bool HangarRowOnPlaneScreen => _mode == MenuMode.Stunt && _slots.Count == 1;

    // The plane pick's row count: the roster (stock + customs) plus the hangar door where it is
    // offered. The door sits AFTER the customs, so the clamp reasoning holds with the roster
    // grown: it is always the single trailing row, never locked, never launched.
    private int PlaneRowCount => _roster.Count + (HangarRowOnPlaneScreen ? 1 : 0);

    /// <summary>Builds the (hidden) launchscreen. <paramref name="zrdrPath"/> is the shared zrdr
    /// extraction the plane stats come from; <paramref name="dataRoot"/> is where <c>extracted/</c>
    /// lives. <paramref name="host"/> must already hold a <see cref="FreeFlightFeature"/> and an
    /// <see cref="InstantActionFeature"/> and, before the first frame, a first seat;
    /// <paramref name="player1"/> is the poller behind that seat. Add it to the tree, then
    /// <see cref="ShowMenu"/>.</summary>
    public static LaunchMenu Build(string zrdrPath, string dataRoot, IMenuHost host, MenuInput player1)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(player1);
        var menu = new LaunchMenu
        {
            _zrdrPath = zrdrPath,
            _dataRoot = dataRoot,
            _host = host,
            _free = host.Features.Get<FreeFlightFeature>(),
            _ia = host.Features.Get<InstantActionFeature>(),
            _setup = host.Features.Get<PlayerSetupFeature>(),
            _hangarFeature = host.Features.Get<HangarFeature>(),
            _campaignFeature = host.Features.Get<CampaignFeature>(),
            // Optional rather than required: a bare host in a suite that never opens the Controls
            // screen has no reason to carry a keymap editor, and a local one edits nothing shared.
            _controls = host.Features.TryGet<ControlsFeature>(out var controls) ? controls : new ControlsFeature(),
            // Optional for the same reason. A suite that never opens the door needs no carrier
            // behind it, and the door is the one feature that owns a socket.
            _net = host.Features.TryGet<NetPlayFeature>(out var net) ? net : null,
            _player1 = player1,
            Layer = HudLayers.Board,
            Visible = false,
        };
        menu._devices = new MenuSeatDevices(player1, menu._setup);
        menu._setup.SetRoster(MenuRoster(Array.Empty<CustomPlaneDef>()));

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        menu.AddChild(root);

        // Fully opaque backdrop so the empty 3D scene (procedural sky) never shows through.
        var bg = new ColorRect { Color = new Color(0.04f, 0.05f, 0.08f), MouseFilter = Control.MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(bg);

        // The three bands. The middle is the only one that expands, so the header sits on the top
        // edge and the footer on the bottom edge whatever the middle holds.
        menu._zones = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        menu._zones.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        menu._zones.AddThemeConstantOverride("separation", 0);
        root.AddChild(menu._zones);
        menu._header = Band(menu._zones);
        menu._middle = Band(menu._zones);
        menu._middle.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        menu._footer = Band(menu._zones);

        // The splitscreen plane select lives alongside the centred layout; exactly one is visible.
        menu._paneRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        menu._paneRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(menu._paneRoot);

        // The campaign's screens are composed boards rather than row lists, so they draw through
        // their own full-window surface; exactly one of the three layouts is ever visible.
        menu._boardRoot = ComposedBoardView.Build(dataRoot);
        menu._boardRoot.Visible = false;
        root.AddChild(menu._boardRoot);

        // The chip strip is a zero-size box pinned to the top-right corner; GrowHorizontal.Begin
        // spending its minimum width leftward from there (PerfHud's own PlaceTopRight pattern), so
        // the right edge stays on the inset however many chips are joined.
        menu._chipStrip = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        menu._chipStrip.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        menu._chipStrip.GrowHorizontal = Control.GrowDirection.Begin;
        root.AddChild(menu._chipStrip);

        menu._netBandLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        menu._netBandLabel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        menu._netBandLabel.GrowHorizontal = Control.GrowDirection.Begin;
        menu._netBandLabel.HorizontalAlignment = HorizontalAlignment.Right;
        menu._netBandLabel.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        menu._netBandLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        menu._netBandLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        root.AddChild(menu._netBandLabel);

        return menu;
    }

    /// <summary>The launch-gate RULE, pure and public so it is reachable from <c>CSVM.Tests</c>
    /// with no menu instance behind it. Everyone joined has locked a plane, and, Dogfight only,
    /// at least two have joined to fight each other. The <paramref name="networked"/> flag lifts
    /// that second rule, the opponent being elsewhere. Free Flight and Instant Action launch
    /// solo as before.</summary>
    public static bool CanLaunch(MenuMode mode, bool allLocked, int joinedCount, bool networked = false) =>
        allLocked && (networked || mode != MenuMode.Versus || joinedCount >= 2);

    /// <summary>The chapter list a mode actually offers, as codes: Stunt Flying only the maps with
    /// Danger Zones (a stunt run elsewhere would be an empty free flight); every other mode all
    /// eight. Static + public so the rule is testable without a menu instance.</summary>
    public static string[] ChapterCodesFor(MenuMode mode)
    {
        var list = ChaptersFor(mode);
        var codes = new string[list.Length];
        for (int i = 0; i < list.Length; i++)
            codes[i] = list[i].Code;
        return codes;
    }

    /// <summary>The Instant Action Environment screen's roster, as chapter codes, in the decoded
    /// dropdown order, C1, C2B, C3, C5, C1B, C4, C2, C1C never among them. The shared feature's
    /// roster, kept here as the screen's own read-out.</summary>
    public static string[] EnvironmentCodes() =>
        Names(InstantActionFeature.Environments, e => e.Code);

    /// <summary>The Instant Action Environment screen's roster as the display names the preset
    /// table names an environment by, in the same decoded dropdown order
    /// <see cref="EnvironmentCodes"/> returns codes in.</summary>
    public static string[] EnvironmentNames() =>
        Names(InstantActionFeature.Environments, e => e.Name);

    /// <summary>The MissionType screen's roster for one chapter, as `ia.json` `mission_type` keys
    /// in the UI dropdown order, with Stunt Flying dropped where `disallow_missions` bars it, the
    /// same rule <see cref="ChapterCodesFor"/> applies via <see cref="DangerZonesFor"/>.</summary>
    public static string[] MissionTypeKeysFor(string chapterCode) =>
        Names(InstantActionFeature.MissionTypesFor(chapterCode), m => m.Key);

    /// <summary>The eleven airframe display names in the langui 3700 order, the shared feature's
    /// roster as this screen reads it.</summary>
    public static string[] PlaneNames() =>
        Names(InstantActionFeature.Airframes, a => a.Name);

    /// <summary>The wave editor's Militia field roster, in the langui dropdown order (3670).</summary>
    public static string[] MilitiaNames() =>
        Names(InstantActionFeature.Militias, m => m.Name);

    /// <summary>The wave editor's Aircraft field roster for one militia (by <see cref="MilitiaNames"/>'s
    /// own name, case-sensitive), the `.BM` pattern coverage table
    /// (docs/formats/instant-action.md), never <c>vehicle.json</c>'s narrower <c>paint_pattern</c>
    /// reading. Throws <see cref="ArgumentException"/> on an unrecognised name.</summary>
    public static string[] AircraftFor(string militiaName) =>
        Names(InstantActionFeature.AircraftFor(militiaName), a => a);

    /// <summary>The pylons the Ammo Selection list offers for <paramref name="def"/>, in the
    /// order it lists them. They stand in fill order under their PHYSICAL number, so the screen
    /// agrees with the weapon gauge's belt lights rather than renumbering them 1..N. A fill-order
    /// entry the fit leaves empty is left out. It is a pylon the build never bought, and the
    /// original draws no field for one (docs/formats/campaign-screens.md, the ammo screen).</summary>
    public static IReadOnlyList<int> AmmoPylons(LoadoutDef? def)
    {
        var hp = def?.Hardpoints;
        var pylons = new List<int>();
        for (int i = 0; hp != null && i < hp.Count && i < Loadout.PylonFillOrder.Length; i++)
        {
            if (Loadout.Hangs(hp, Loadout.PylonFillOrder[i]))
            {
                pylons.Add(Loadout.PylonFillOrder[i]);
            }
        }

        return pylons;
    }
    /// <summary>The wave editor's Skill field roster, internal keys in the langui dropdown order
    /// (3695), the same vocabulary <c>InstantActionWave.EnemySkill</c> stores.</summary>
    public static string[] SkillKeys() => Names(InstantActionFeature.Skills, s => s);

    /// <summary>Builds one wizard wave slot into the <see cref="InstantActionWave"/>
    /// <see cref="Mech3.InstantAction.BuildFromWizard"/> stores, returning
    /// <see cref="InstantAction.EmptyWave"/> when <paramref name="count"/> is 0 so an unconfigured
    /// slot matches a JSON file's omitted `groupN` byte-identically. The feature's own build rule,
    /// kept here as the screen's read-out.</summary>
    public static InstantActionWave WaveFor(int count, int militiaIndex, int aircraftIndex, int skillIndex) =>
        InstantActionFeature.WaveFor(count, militiaIndex, aircraftIndex, skillIndex);

    /// <summary>Show the menu (normally from the Mode screen) and prime every input edge so a
    /// button still held from the transition here (the Esc that left a flight, the Start that
    /// joined a player) does not fire immediately. Joined players survive a return from flight;
    /// their plane locks do not. <paramref name="startScreen"/> opens on a later screen, a
    /// screenshot aid: docs/cli.md's <c>--menu=</c> list, with the hangar's own values handled by
    /// <see cref="OpenHangarAid"/> and the campaign's by <see cref="OpenCampaignAid"/>.</summary>
    public void ShowMenu(string startScreen = "")
    {
        _screen = startScreen switch
        {
            "chapter" or "dogfight" => Screen.Chapter,
            "presets" => Screen.Presets,
            "environment" => Screen.Environment,
            "missiontype" => Screen.MissionType,
            "waves" => Screen.Waves,
            "wingmen" => Screen.Wingmen,
            "plane" or "loadout" or "selected" => Screen.Plane,
            "wingmanloadout" => Screen.WingmanLoadout,
            "options" => Screen.Options,
            "controls" => Screen.Controls,
            "network" or "network-coopjoin" or "network-coopwait" => Screen.Network,
            _ => Screen.Mode,
        };
        if (_screen == Screen.Options)
        {
            OpenOptions();
        }

        if (_screen == Screen.Controls)
        {
            OpenControls();
        }

        // Environment/MissionType/Waves/Wingmen only exist under Instant Action, force it so a
        // --menu= opening straight onto one of them (a screenshot aid) renders the right
        // roster/filter rather than whatever _mode was last left at.
        if (_screen is Screen.Presets or Screen.Environment or Screen.MissionType or Screen.Waves
            or Screen.WaveEdit or Screen.Wingmen or Screen.WingmanLoadout)
        {
            _modeIndex = (int)MenuMode.Stunt;
            _mode = MenuMode.Stunt;
        }

        // The map screen's two match rows exist under Dogfight alone, so its own aid forces the
        // mode the way the wizard's aids force theirs. Plain `chapter` still opens on Free Flight.
        if (startScreen == "dogfight")
        {
            _modeIndex = (int)MenuMode.Versus;
            _mode = MenuMode.Versus;
        }

        // The wingman list needs a flight to arm and a mission that HAS wingmen, so the aid
        // configures both, the ace duel forces the count to 0, and opening onto a state no
        // player can reach is worse than not having the aid.
        if (_screen == Screen.WingmanLoadout)
        {
            int flown = 0;
            for (int i = 0; i < CurrentMissionTypes.Count; i++)
            {
                if (CurrentMissionTypes[i].Key != InstantActionFeature.AceKey)
                {
                    flown = i;
                    break;
                }
            }

            _ia.SelectMissionType(flown);
            if (_ia.NumWingmen == 0)
            {
                _ia.SetWingmen(2);
            }
        }
        // Opening straight onto Waves skips the accept that normally parks the cursor, so put it
        // where a player would find it, otherwise the aid screenshots a state nobody sees.
        if (_screen == Screen.Waves)
        {
            _waveListIndex = InstantActionFeature.WaveSlots;
        }
        // Opening straight onto the aircraft screen skips the Chapter Accept that hands Free
        // Flight its pick, so the cursor's chapter is handed over here instead.
        if (_screen == Screen.Plane && _mode == MenuMode.Free)
        {
            _free.SelectChapter(CurrentChapters[_chapterIndex].Code);
        }
        _error = "";
        // A flow never survives a trip through flight: it holds an unsaved scratch plane, and
        // resuming one after a session would be editing something nobody remembers starting.
        _hangar = null;
        // Same rule for the campaign: its flow holds a selected profile and a screen stack, and
        // resuming one after a session would be continuing something nobody remembers starting.
        _campaign = null;
        _campaignFeature.Discard();
        // The campaign's network door goes with the campaign that opened it.
        CloseCoopDoor();
        _coopWait = false;
        _aidGuest = 0;
        RefreshRoster();
        OpenHangarAid(startScreen);
        OpenCampaignAid(startScreen);
        OpenNetDoorAid(startScreen);
        Visible = true;
        // A host with no seat yet gets player 1's poller as seat 0, so there is a player to drive.
        if (_setup.Seats.Count == 0)
            _setup.Join(new BuiltInSeat(_player1));
        SyncSlots();
        // Every stage of the pick, not just the lock: a slot left Confirmed would satisfy the
        // launch gate on the first frame back from flight and fly again without a press.
        _setup.ResetPicks(fits: true);
        foreach (var slot in _slots)
            slot.Input.Prime();
        // A pane's own fit only exists once that slot has selected an airframe, so the aid makes
        // that press for the reader, after the reset above, which would undo it.
        if (startScreen is "loadout" or "selected")
        {
            _setup.Select(_slots[0].Seat);
            if (startScreen == "loadout")
                _setup.OpenLoadout(_slots[0].Seat);
        }
        _devices.Sync(0f);
        _devices.PrimeJoins();
        Rebuild();
    }

    /// <summary>Hide the menu (the host is about to build a session).</summary>
    public void HideMenu() => Visible = false;

    /// <summary>The control drawing player 1's row <paramref name="index"/>, or null when that row
    /// is not drawn (outside a list's window, or a composed campaign board). A check injects the
    /// mouse events Godot would dispatch through its <c>gui_input</c> and mouse-exit signals.</summary>
    public Control? RowControl(int index) => _rowControls.TryGetValue(index, out var row) ? row : null;

    /// <summary>Applies one frame of player 1's semantic commands in place of a device poll, then
    /// redraws if anything changed. The scripted journey suites drive the real screens through
    /// this, and the frame shape is the one a menu input source hands a presentation. Needs
    /// <see cref="ShowMenu"/> to have run, so there is a player 1 to drive.</summary>
    public bool Drive(MenuCommands frame)
    {
        SyncSlots();
        // Player 1's frame alone: the other seats read idle, not whatever their last poll held.
        for (int i = 1; i < _slots.Count; i++)
            _slots[i].Frame = MenuCommands.None;
        Apply(WithPointer(frame));
        bool dirty;
        try
        {
            dirty = HandleInput();
        }
        finally
        {
            // Edges, so the next real poll starts from an idle frame rather than a press.
            Apply(MenuCommands.None);
        }

        if (dirty && Visible)
            Rebuild();
        return dirty;
    }

    /// <summary>Opens the wizard's first screen with the Instant Action mode standing, the screen
    /// an Instant Action sortie returns to. The sortie's own settings are the feature's and are
    /// left alone, so the preset, the mission, the waves and the wingmen read as they did when FLY
    /// was pressed; the cursors are this screen's own and survived the flight with it.</summary>
    public void OpenInstantAction()
    {
        _modeIndex = (int)MenuMode.Stunt;
        _mode = MenuMode.Stunt;
        _screen = Screen.Environment;
        Rebuild();
    }

    /// <summary>Opens the campaign on the named profile's cabin, the screen a flown mission
    /// returns to. The profile is re-read from the store, so what the mission just recorded (a
    /// completed objective, an advanced position, a paid reward) is what the cabin shows. An
    /// unreadable profile leaves the flow on its roster rather than refusing.</summary>
    public void OpenCampaignCabin(string profileName)
    {
        OpenCampaign();
        if (_campaign is { } flow && flow.Store.Load(profileName) is { } profile)
        {
            flow.SelectProfile(profile);
        }

        Rebuild();
    }

    /// <summary>Opens the campaign on the named profile's scrapbook, at the mission a finished
    /// mission just flew, cabin on its far side after <see cref="Session.Launcher"/>'s deferred
    /// hop. The profile is re-read from the store, the same discipline as
    /// <see cref="OpenCampaignCabin"/>, so the shown record is what the mission just wrote. A win on
    /// the campaign's last mission watches the closing film first
    /// (<see cref="CampaignFlow.OpenScrapbookAfterMission(Session.CampaignProfileDef, int, bool)"/>).</summary>
    public void OpenCampaignScrapbook(string profileName, int seq, bool missionWon)
    {
        OpenCampaign();
        if (_campaign is { } flow && flow.Store.Load(profileName) is { } profile)
        {
            flow.OpenScrapbookAfterMission(profile, seq, missionWon);
        }

        Rebuild();
    }

    /// <summary>Show an error line on the current screen (e.g. a failed build sent us back here).</summary>
    public void ShowError(string message)
    {
        _error = message;
        if (Visible)
            Rebuild();
    }

    /// <summary>Debug/verification aid (--debug-join=N): add N device-less players so the
    /// multi-cursor plane screen can be screenshot on a machine with one controller. They can
    /// never act (no keyboard, no pad), so the shot is deterministic.</summary>
    public void DebugJoin(int extraPlayers)
    {
        for (int i = 0; i < extraPlayers && _setup.Seats.Count < SplitScreen.MaxPlayers; i++)
        {
            var seat = _setup.Join(new MenuIdleSource());
            if (seat == null)
                break;
            seat.Cursor = (i + 1) % Planes.Length;
            // Lock the last one so a screenshot shows both panel states (locked border lit
            // vs still choosing) side by side.
            if (i == extraPlayers - 1)
                _setup.Select(seat);
        }
        SyncSlots();
        Log.Info("ui", $"launchscreen: --debug-join → {_slots.Count} players (the added ones have no device)");
        // --menu=campaign-guestcheck's own walk, which needs the players this call just added: the
        // aid ran inside ShowMenu, before anybody had joined.
        if (_aidGuest > 0 && _campaign is { } flow)
        {
            flow.SetPlayers(_slots.Count);
            int walked = 0;
            while (walked < _aidGuest && flow.Field.Advance())
            {
                walked++;
            }
        }

        _aidGuest = 0;

        if (Visible)
            Rebuild();
    }

    /// <summary>Debug/verification aid (--debug-waves=N): pre-configure the first N (clamped 0-4)
    /// Instant Action wizard wave slots with a distinct, non-empty load, so the Waves screen's
    /// "N waves configured" states are screenshot-able with nobody at the controls. Forces Instant
    /// Action mode, since waves exist nowhere else.</summary>
    public void DebugWaves(int count)
    {
        _modeIndex = (int)MenuMode.Stunt;
        _mode = MenuMode.Stunt;
        int n = Math.Clamp(count, 0, InstantActionFeature.WaveSlots);
        for (int i = 0; i < n; i++)
        {
            _ia.SetWave(i, new InstantActionWaveSetup(
                4, i % InstantActionFeature.Militias.Count, 0, i % InstantActionFeature.Skills.Count));
        }
        Log.Info("ui", $"launchscreen: --debug-waves → {n} wave(s) pre-configured");
        if (Visible)
            Rebuild();
    }

    /// <summary>Debug/verification aid (--debug-preset=N): apply Table of Contents preset N and
    /// open on the wizard's step 1, so the FILLED wizard is screenshot-able with nobody at the
    /// controls. This is the aid for the failure mode units cannot see, a preset that applies the
    /// wrong aircraft or the wrong militia looks entirely plausible on screen. Forces Instant
    /// Action mode, since presets exist nowhere else.</summary>
    public void DebugPreset(int index)
    {
        _modeIndex = (int)MenuMode.Stunt;
        _mode = MenuMode.Stunt;
        int n = Math.Clamp(index, 0, InstantActionPresets.All.Count - 1);
        ApplyPreset(n);
        _presetCursor = n;
        ScrollPresetsToCursor();
        // The base def is normally loaded by Environment's own Accept, which this aid skips.
        _ia.ConfirmEnvironment();
        Log.Info("ui", $"launchscreen: --debug-preset → '{InstantActionPresets.All[n].Name}' applied");
        if (Visible)
            Rebuild();
    }

    /// <summary>Debug/verification aid (--debug-wingmen=N): pre-configure the Instant Action
    /// wizard's own wingman count (clamped 0-5) and a non-default aircraft, so the plane screen's
    /// flown-wingmen re-clamp (decision 8a) is screenshot-able alongside --debug-join=. Forces
    /// Instant Action mode, since wingmen exist nowhere else.</summary>
    public void DebugWingmen(int count)
    {
        _modeIndex = (int)MenuMode.Stunt;
        _mode = MenuMode.Stunt;
        _ia.SetWingmen(count);
        _ia.SelectWingmanPlane(1);
        Log.Info("ui", $"launchscreen: --debug-wingmen → {_ia.NumWingmen} wingman/wingmen pre-configured");
        if (Visible)
            Rebuild();
    }

    public override void _Process(double delta)
    {
        if (!Visible)
            return;

        // Device bookkeeping first: a pad that vanished must not still be driving a cursor, and a
        // pad that appeared should be joinable (or become P1's, if P1 has none).
        bool dirty = _devices.Sync((float)delta);
        dirty |= ScanJoins();
        SyncSlots();

        // Every metric on every one of the three layouts is a function of the window, and a resize
        // or a resolution change arrives as no input at all, so the size itself is watched.
        dirty |= GetViewport().GetVisibleRect().Size != _viewSize;

        // Player 1's commands come through the host's first seat and are applied onto its poller;
        // every other seat's frame is read from its own source. Text capture is set before the
        // poll: the PLANENAME screen's letter aliases must be dead for the frame that reads them.
        var seat = _host.Seats[0];
        seat.CapturingText = NamePage() != null || AddressField() != null;
        Apply(WithPointer(seat.Poll((float)delta)));
        for (int i = 1; i < _slots.Count; i++)
            _slots[i].Frame = _slots[i].Seat.Source.Poll((float)delta);
        dirty |= HandleInput();
        // After the input, so a press that opened or left the briefing is already reflected: the
        // reveal is a clock the page cannot own, and the narration is a node the page cannot hold.
        dirty |= TickCampaignAudio(delta);
        dirty |= TickNetDoor(delta);
        if (_pressFrames > 0)
        {
            _pressFrames--;
            dirty = true;
        }

        dirty |= _typed;
        _typed = false;

        // Live hotplug: redraw when the join strip's text changes even if nothing was pressed.
        if (dirty || JoinStripText() != _stripText)
        {
            if (Visible) // a launch during HandleInput hides us; don't rebuild a dead menu
                Rebuild();
        }
    }

    /// <summary>The screenshot key, and typed characters for the PLANENAME screen. Typing is taken
    /// as an event rather than by the raw polling everything else here uses, because only the event
    /// carries the character the pilot's own keyboard layout produced, and held keys arrive as
    /// echoes, which is where typing gets its auto-repeat.
    /// <para>⚠ The screenshot key is handled here rather than left to the launcher, so the menu
    /// screens own their binding (BL-489); marking it handled keeps one press to one file.</para></summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible)
        {
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F12 })
        {
            if (ScreenshotPane is { } pane)
            {
                Testing.CaptureDirector.SaveScreenshot(pane);
            }
            else
            {
                Testing.CaptureDirector.SaveScreenshot(GetViewport());
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        if (AddressField() is { } net)
        {
            TypeAddress(net, key);
            return;
        }

        if (NamePage() is not { } page)
        {
            return;
        }

        bool typed = key.Keycode == Key.Backspace
            ? page.Backspace()
            : key.Unicode > 0 && page.Type((char)key.Unicode);
        if (typed)
        {
            // Redrawn on the next frame rather than here: Rebuild replaces the very controls this
            // event is being dispatched through.
            _typed = true;
            GetViewport().SetInputAsHandled();
        }
    }

    // --- players / devices ---

    private static int Wrap(int index, int count) => ((index % count) + count) % count;

    // One volume row's step, on the Original AUDIO page's own terms so the two presentations write
    // the same levels. It is the keyboard step of that page's slider, clamped at both ends where
    // every other row here wraps. A step from silence must not land on full volume. A step that
    // moves nothing hands back the field unchanged, so a never-set level stays never set.
    private static int? StepLevel(int? level, int shipped, int dir)
    {
        int from = Math.Clamp(level ?? shipped, AudioMix.MinLevel, AudioMix.MaxLevel);
        int to = Math.Clamp(from + (dir * Menu.Original.SliderControl.KeyStep), AudioMix.MinLevel, AudioMix.MaxLevel);
        return to == from ? level : to;
    }

    private static string LevelLabel(int? level, int shipped) =>
        Math.Clamp(level ?? shipped, AudioMix.MinLevel, AudioMix.MaxLevel).ToString(CultureInfo.InvariantCulture);

    // The read-out helpers behind the public rosters: one name per row of a feature list.
    private static string[] Names<T>(IReadOnlyList<T> rows, Func<T, string> name)
    {
        var names = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            names[i] = name(rows[i]);
        return names;
    }

    private static (string Name, string Node)[] BuildPlanes()
    {
        var rows = new (string Name, string Node)[InstantActionFeature.Airframes.Count];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = (InstantActionFeature.Airframes[i].Name, InstantActionFeature.Airframes[i].Node);
        return rows;
    }

    private static (string Name, string Code, bool DangerZones)[] ChaptersFor(MenuMode mode) =>
        mode == MenuMode.Stunt ? Array.FindAll(Chapters, c => c.DangerZones) : Chapters;

    // Whether a chapter's `ia.json` ships `dzones`, read off the shared roster so the
    // Environment/MissionType screens and the plain Chapter screen cannot read two different
    // answers for the same chapter.
    private static bool DangerZonesFor(string chapterCode) => MenuChapters.DangerZonesFor(chapterCode);

    // The same roster as the shared setup's rows, so both presentations pick from one list.
    private static IReadOnlyList<MenuAircraft> MenuRoster(IReadOnlyList<CustomPlaneDef> customs) =>
        PlayerSetupFeature.BuildRoster(Planes, customs, PlanePickerRoster.AirframeNode);

    private static MenuInput InputBehind(IMenuInputSource source) =>
        source is BuiltInSeat seat ? seat.Input : new MenuInput();

    // This screen's row text per chapter code, zipped over the shared roster so the codes, their
    // order and the Danger Zones flags have exactly one home.
    private static (string Name, string Code, bool DangerZones)[] BuildChapters()
    {
        var rows = new (string Name, string Code, bool DangerZones)[MenuChapters.All.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var chapter = MenuChapters.All[i];
            rows[i] = (ChapterRowText(chapter.Code), chapter.Code, chapter.DangerZones);
        }

        return rows;
    }

    private static string ChapterRowText(string code) => code switch
    {
        "C1" => "Sea Haven (night), IA: an airfield",
        "C1B" => "The ocean, Sea Haven variant",
        "C1C" => "Sea Haven variant C (no IA, campaign/MP only)",
        "C2" => "Hollywood, IA: a movie studio",
        "C2B" => "The clouds, Hollywood variant",
        "C3" => "Hawaii (islands)",
        "C4" => "Rocky Mountains, IA: Sky Haven",
        "C5" => "New York, IA: Manhattan",
        _ => code,
    };

    private static int Mph(PlaneStats s) => Mathf.RoundToInt(s.FdSpeed * 2.23694f);

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // A match limit as its row reads it, 0 being the disabled one rather than a target of nothing.
    private static string LimitLabel(int value, string unit) =>
        value == 0 ? "no limit" : value.ToString(CultureInfo.InvariantCulture) + unit;

    private static Label Label(string text, int fontSize, Color color, HorizontalAlignment align)
    {
        var l = new Label { Text = text, HorizontalAlignment = align };
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        l.AddThemeFontSizeOverride("font_size", fontSize);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        l.AddThemeConstantOverride("shadow_offset_x", 1);
        l.AddThemeConstantOverride("shadow_offset_y", 1);
        return l;
    }

    private static Control Spacer(int height) => new() { CustomMinimumSize = new Vector2(0, height) };

    // One of the three bands: a full-width row whose single content column is centred in it.
    private static HBoxContainer Band(Node parent)
    {
        var band = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        parent.AddChild(band);
        return band;
    }

    // A band's content column. Nothing in a band expands horizontally, so the column is exactly
    // this wide unless a line of text is wider, which is what the description wraps against.
    // ⚠ The separation scales with the rest; a fixed one is height the band metrics do not budget.
    private static VBoxContainer Column(float width, float s)
    {
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(width, 0f) };
        column.AddThemeConstantOverride("separation", (int)(ZoneSeparation * s));
        return column;
    }

    // Empties a band before its column is rebuilt. Removed as well as freed: a queued-free child
    // is still a child for the rest of the frame, and would count twice towards the band's height.
    private static void Clear(Node band)
    {
        foreach (var child in band.GetChildren())
        {
            band.RemoveChild(child);
            child.QueueFree();
        }
    }

    // Player 1's frame of semantic commands onto the poller HandleInput reads, and onto its slot.
    // Join is not applied: joining is a per-pad scan (ScanJoins), not a seat's command.
    private void Apply(MenuCommands frame)
    {
        var p1 = _slots[0].Input;
        p1.Move = frame.MoveY;
        p1.MoveX = frame.MoveX;
        p1.Accept = frame.Accept;
        p1.Back = frame.Back;
        p1.Loadout = frame.Loadout;
        p1.Presets = frame.Contents;
        _slots[0].Frame = frame;
    }

    // The mouse's pending commands folded into player 1's frame, then dropped. The frame's own
    // step wins over a hover, so a pad press and a pointer move in one frame cannot each take
    // the cursor to a different row; focus stays one thing.
    private MenuCommands WithPointer(MenuCommands frame)
    {
        var pointer = _pointer;
        _pointer = MenuCommands.None;
        if (ReferenceEquals(pointer, MenuCommands.None))
            return frame;
        return frame with
        {
            MoveY = frame.MoveY != 0 ? frame.MoveY : pointer.MoveY,
            Accept = frame.Accept || pointer.Accept,
            Back = frame.Back || pointer.Back,
        };
    }

    // Puts one of player 1's rows under the mouse. The control takes Godot's own hit test (its
    // labels stay Ignore, so the row is hit as a whole) and routes its events into the frame.
    private void Pointable(Control row, int index)
    {
        row.MouseFilter = Control.MouseFilterEnum.Stop;
        row.GuiInput += ev => PointerEvent(index, ev);
        row.MouseEntered += () => { if (_pressRow == index) _pressInside = true; };
        row.MouseExited += () => { if (_pressRow == index) _pressInside = false; };
        _rowControls[index] = row;
    }

    // One mouse event over a row, or over the list between rows (row null, the wheel and the right
    // button alone). A motion focuses the row, a wheel notch steps the cursor, the left button
    // confirms on the release when it went down on this row and the pointer never left it, and the
    // right button is Back. Back takes the press rather than the release: it leaves the screen and
    // not a row, so there is no control a release would have to land back inside of.
    private void PointerEvent(int? row, InputEvent ev)
    {
        switch (ev)
        {
            case InputEventMouseMotion when row is { } hovered:
                _pointer = _pointer with { MoveY = hovered - PointerIndex };
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _pointer = _pointer with { MoveY = _pointer.MoveY - 1 };
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _pointer = _pointer with { MoveY = _pointer.MoveY + 1 };
                break;
            // ⚠ Not on the Mode screen: Back there is the quit, and a stray right click must not
            // take it. The footer names Esc for that one, as it always has.
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }
                when _screen != Screen.Mode:
                _pointer = _pointer with { Back = true };
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button when row is { } pressed:
                if (button.Pressed)
                {
                    _pressRow = pressed;
                    _pressInside = true;
                }
                else
                {
                    if (_pressRow == pressed && _pressInside)
                        Click(pressed);
                    _pressRow = null;
                }

                break;
        }
    }

    // A press and release on one row: the cursor steps onto it and Accept lands there in the same
    // frame, so no redraw can come between the two. A locked aircraft pick cannot move its cursor,
    // so a click on any other row is refused rather than confirming the locked one under it.
    private void Click(int row)
    {
        int delta = row - PointerIndex;
        if (delta != 0 && _screen == Screen.Plane && _slots[0].Locked && !_slots[0].InLoadout)
            return;
        _pointer = _pointer with { MoveY = delta, Accept = true };
    }

    // Start on an unclaimed pad joins a new player on the Plane, Campaign or Controls screen; the
    // scan itself is the device bookkeeping's. A campaign join closes at the seated player's FLY
    // MISSION. Controls takes the gesture because Options is reached with seat 0 alone, so this is
    // the only door to another player's keymap, and the pad that presses it is what that player
    // then captures with.
    private bool ScanJoins()
    {
        if (_screen != Screen.Plane && _screen != Screen.Campaign && _screen != Screen.Controls)
            return false;
        // The seated player's FLY MISSION opens the first guest's check instead of leaving, so
        // the field's own lock closes joining.
        if (_campaign is { Field.Locked: true })
            return false;
        // Local and remote players share one seat ceiling, so a full wire closes local joining.
        if (_slots.Count + RemoteGuests() >= NetSeats.MaxPlayers)
            return false;
        return _devices.ScanJoins();
    }

    // Joining opens on the screen being entered, so a Start held on the way in must not fire.
    private void PrimeJoins() => _devices.PrimeJoins();

    // Keeps the slot wrappers in step with the feature's seats: one per seat, seat 0 over player
    // 1's poller, a pad seat over the poller behind its source, a device-less seat over an idle one.
    private void SyncSlots()
    {
        var seats = _setup.Seats;
        if (_slotsRevision == _setup.Revision && _slots.Count == seats.Count)
            return;
        var kept = new Dictionary<PlayerSeat, Slot>(_slots.Count);
        foreach (var slot in _slots)
            kept[slot.Seat] = slot;
        _slots.Clear();
        for (int i = 0; i < seats.Count; i++)
        {
            if (!kept.TryGetValue(seats[i], out var slot))
                slot = new Slot(seats[i], i == 0 ? _player1 : InputBehind(seats[i].Source));
            _slots.Add(slot);
        }
        _slotsRevision = _setup.Revision;
    }

    private void Unjoin(int index)
    {
        Log.Info("ui", $"launchscreen: P{index + 1} left (pad {MenuSeatDevices.PadOf(_slots[index].Seat.Source)})");
        _setup.Unjoin(_slots[index].Seat);
        _slots.RemoveAt(index);
        _slotsRevision = _setup.Revision;
    }

    // --- navigation ---

    // Whether a campaign film owns this frame rather than the screen behind it. One standing in
    // front of that screen does, and so does the tail of the press that ended one. The screen
    // never saw that press go down and would read it as an edge of its own. The swallowed flag
    // says that frame still asks for a redraw, the screen having changed unread behind the film.
    // The pointer's tail lasts until its button comes up, where every other press is spent on the
    // frame it lands on.
    private bool CampaignFilmOwnsFrame(out bool swallowed)
    {
        swallowed = false;
        if (_campaign?.Film is not { } film)
        {
            return false;
        }

        if (film.Up)
        {
            return true;
        }

        swallowed = film.Swallows(Input.IsMouseButtonPressed(MouseButton.Left));
        return swallowed;
    }

    // Reads this frame's polled intents and applies them. Mode/Chapter are player 1's
    // alone (the others can only leave); the Plane screen runs every player's cursor at once and
    // fires Launch when they are all locked. Returns true if the view changed.
    private bool HandleInput()
    {
        // Before any screen reads the frame: a campaign film may own it.
        if (CampaignFilmOwnsFrame(out bool swallowed))
        {
            return swallowed;
        }

        bool dirty = false;
        if (_screen != Screen.Plane)
        {
            var p1 = _slots[0].Input;
            dirty |= _devices.ClaimP1Pad();
            if (_screen == Screen.Hangar)
            {
                return HandleHangarInput(p1) || dirty;
            }

            if (_screen == Screen.Campaign)
            {
                return HandleCampaignInput(p1) || dirty;
            }

            if (_screen == Screen.Controls)
            {
                return HandleControlsInput(p1) || dirty;
            }

            if (p1.Move != 0)
            {
                int n = CurrentCount();
                switch (_screen)
                {
                    case Screen.Mode: _modeIndex = Wrap(_modeIndex + p1.Move, n); break;
                    case Screen.Chapter: _chapterIndex = Wrap(_chapterIndex + p1.Move, n); break;
                    case Screen.Presets:
                        _presetCursor = Wrap(_presetCursor + p1.Move, n);
                        ScrollPresetsToCursor();
                        break;
                    case Screen.Environment: _ia.SelectEnvironment(Wrap(_ia.EnvironmentIndex + p1.Move, n)); break;
                    case Screen.MissionType: _ia.SelectMissionType(Wrap(_ia.MissionTypeIndex + p1.Move, n)); break;
                    case Screen.Waves: _waveListIndex = Wrap(_waveListIndex + p1.Move, n); break;
                    case Screen.WaveEdit: _waveFieldIndex = Wrap(_waveFieldIndex + p1.Move, n); break;
                    case Screen.Wingmen: _wingmenFieldIndex = Wrap(_wingmenFieldIndex + p1.Move, n); break;
                    case Screen.WingmanLoadout: _wingmanFitRow = Wrap(_wingmanFitRow + p1.Move, n); break;
                    case Screen.Options:
                        _optionsIndex = Wrap(_optionsIndex + p1.Move, n);
                        ScrollOptionsToCursor();
                        break;
                    case Screen.Network: _netIndex = Wrap(_netIndex + p1.Move, n); break;
                }
                dirty = true;
            }
            if (p1.MoveX != 0)
            {
                dirty |= HandleMoveX(p1.MoveX);
            }
            // X on the wizard's step 1 opens the Table of Contents. Step 1 only: the presets are
            // the mode's front door, and a jump back to step 1 from deeper in the wizard would have
            // to explain why picking a scenario threw the pilot three screens backwards.
            if (p1.Presets && _screen == Screen.Environment)
            {
                _screen = Screen.Presets;
                // Re-open on the applied preset, so backing in and out does not lose the place.
                _presetCursor = _ia.PresetIndex >= 0 ? _ia.PresetIndex : 0;
                ScrollPresetsToCursor();
                return true;
            }
            // Y on the Wingmen step opens the flight's one fit, the same meaning Y carries on a
            // plane pane. Gated on there being wingmen to arm, like the Aircraft row above it.
            if (p1.Loadout && _screen == Screen.Wingmen && _ia.NumWingmen > 0)
            {
                _screen = Screen.WingmanLoadout;
                _wingmanFitRow = 0;
                return true;
            }
            if (p1.Accept)
            {
                _error = "";
                HandleAccept();
                dirty = true;
            }
            else if (p1.Back || (p1.Loadout && _screen == Screen.WingmanLoadout))
            {
                if (_screen == Screen.Mode)
                    _host.Exit(new QuitExit());
                else if (_coopWait)
                {
                    // The waiting board backs onto the board it was continued from, link and all,
                    // as the Dogfight's map screen does. Its Leave row is what hangs up.
                    _coopWait = false;
                    _netIndex = NetContinueRow;
                }
                else
                {
                    // Backing off the board hangs up and gives the router's port back. Leaving a
                    // socket listening behind an abandoned screen is the one outcome a player
                    // cannot see and cannot undo from anywhere else in the menu.
                    if (_screen == Screen.Network)
                        _net?.Close();
                    _screen = _screen switch
                    {
                        // Backing out of the contents list leaves the wizard's fields as they were:
                        // a preset is applied on Accept, never on the cursor passing over it.
                        Screen.Presets => Screen.Environment,
                        Screen.Environment => Screen.Mode,
                        Screen.MissionType => Screen.Environment,
                        Screen.Waves => Screen.MissionType,
                        Screen.WaveEdit => Screen.Waves,
                        Screen.Wingmen => Screen.Waves,
                        Screen.WingmanLoadout => Screen.Wingmen,
                        // A Dogfight reached through the door came from the board, so Back goes
                        // there and the open socket stays open.
                        Screen.Chapter when Networked() => Screen.Network,
                        _ => Screen.Mode, // Chapter, Network
                    };
                }
                dirty = true;
            }
            // Everyone else can only drop out from here.
            for (int i = _slots.Count - 1; i >= 1; i--)
            {
                if (!_slots[i].Frame.Back)
                    continue;
                Unjoin(i);
                dirty = true;
            }
            return dirty;
        }

        // Plane screen: all joined players pick simultaneously, each with their own cursor; the
        // stages themselves move through the shared setup.
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            var slot = _slots[i];
            var seat = slot.Seat;
            var input = slot.Frame;

            // A pane inside its loadout reads nothing else, so one player arming cannot pull
            // anybody else out of browsing and cannot launch while somebody is still in there.
            if (slot.InLoadout)
            {
                dirty |= HandleFitInput(slot);
                continue;
            }

            if (input.MoveY != 0 && !slot.Locked)
            {
                // Browse resets the fit when the airframe changes and only then: backing out to
                // re-read the stats line must not cost the fit.
                _setup.Browse(seat, Wrap(slot.PlaneIndex + input.MoveY, i == 0 ? PlaneRowCount : _roster.Count));
                dirty = true;
            }
            // The hangar row is a door, not an aircraft: it never locks, so nothing downstream
            // ever indexes the roster with it.
            if (input.Accept && i == 0 && slot.PlaneIndex >= _roster.Count)
            {
                OpenHangar(Screen.Plane);
                return true;
            }

            if (input.Loadout && slot.Locked && !slot.Confirmed)
            {
                _setup.OpenLoadout(seat);
                dirty = true;
            }
            else if (input.Accept && !slot.Confirmed)
            {
                // Two stages: A selects the airframe, A again confirms it and launches once
                // everybody has. Unconditional, a pilot with no interest in weapons taps twice
                // and flies the stock fit, which is the old behaviour plus one press.
                if (slot.Locked)
                    _setup.Confirm(seat);
                else
                    _setup.Select(seat);
                _error = "";
                dirty = true;
            }
            else if (input.Back)
            {
                if (_setup.Back(seat) == SeatBack.Browsing)
                {
                    if (i == 0)
                    {
                        // Player 1 backing out returns everyone to whichever screen fed the Plane
                        // screen this time, mirroring the forward skip of Waves/Wingmen for
                        // Instant Action's ace duel.
                        _screen = _mode != MenuMode.Stunt ? Screen.Chapter
                            : _ia.IsAceDuel ? Screen.MissionType
                            : Screen.Wingmen;
                        _setup.ResetPicks(fits: false);
                        return true;
                    }

                    Unjoin(i);
                }
                dirty = true;
            }
        }

        if (CanLaunch())
        {
            FireLaunch();
            return false; // the host took the exit, hid us and is building
        }
        return dirty;
    }

    // A pane showing its loadout list. Live-editing steppers like every other wizard screen, and
    // B returns to the airframe keeping the fit rather than discarding it. ⚠ That last part is
    // INVENTED: the original pairs ACCEPT LOADOUT with CANCEL LOADOUT, but its Cancel is a button
    // you click, while ours would sit on the pad's navigation key, a discard there would throw
    // away a fit somebody was only stepping back from. Reset to stock is the revert we keep.
    private bool HandleFitInput(Slot slot)
    {
        var input = slot.Frame;
        var def = FitFor(slot.PlaneIndex);
        var rows = FitRowsFor(def, slot.Fit);
        bool dirty = false;
        if (input.MoveY != 0)
        {
            slot.FitRow = Wrap(slot.FitRow + input.MoveY, rows.Count);
            dirty = true;
        }

        slot.FitRow = Math.Clamp(slot.FitRow, 0, rows.Count - 1);
        var row = rows[slot.FitRow];
        if (input.MoveX != 0)
        {
            StepFit(def, slot.Fit, row, input.MoveX);
            dirty = true;
        }

        if (input.Accept && row.Kind == FitRowKind.Reset)
        {
            slot.Fit.ResetToStock();
            dirty = true;
        }

        if (input.Back || input.Loadout)
        {
            _setup.CloseLoadout(slot.Seat);
            dirty = true;
        }
        return dirty;
    }

    // The wingman list's rows, the one fit the whole flight carries, so it hangs off the
    // Wingmen step rather than any pane.
    private List<FitRow> WingmanFitRows() =>
        FitRowsFor(FitFor(_ia.WingmanPlaneIndex), _ia.WingmanFit);

    // The fit list the centred body is showing, or null when it is showing something else. A
    // lone pilot's plane screen keeps the centred layout, so its list draws through the same
    // path the wingman one does; a pane's list is drawn by RebuildPanes instead.
    private List<FitRow>? CentredFitRows() =>
        _screen == Screen.WingmanLoadout ? WingmanFitRows()
        : _screen == Screen.Plane && _slots.Count == 1 && _slots[0].InLoadout
            ? FitRowsFor(FitFor(_slots[0].PlaneIndex), _slots[0].Fit)
            : null;

    // The list's two column widths, measured rather than guessed: the label column takes the
    // widest mount name present, the value column the widest entry EITHER roster can produce, so
    // a row keeps its width whatever it is stepped to. Falls back to em estimates with no theme
    // font, which is the same guard the band metrics use.
    private Vector2 FitColumns(List<FitRow> rows, int fontSize)
    {
        var font = MenuFont;
        if (font == null)
        {
            return new Vector2(fontSize * 7f, fontSize * 8f);
        }

        float label = 0f;
        foreach (var row in rows)
        {
            label = Mathf.Max(label, font.GetStringSize(row.Label, HorizontalAlignment.Left, -1, fontSize).X);
        }

        float value = 0f;
        foreach (var option in Fits.Options.GunAmmo)
        {
            value = Mathf.Max(value, font.GetStringSize(option.Label, HorizontalAlignment.Left, -1, fontSize).X);
        }

        foreach (var option in Fits.Options.PylonOrdnance)
        {
            value = Mathf.Max(value, font.GetStringSize(option.Label, HorizontalAlignment.Left, -1, fontSize).X);
        }

        return new Vector2(label + (fontSize * 1.2f), value);
    }

    // One loadout row as a control: mount in the left column, what is fitted there in the right.
    private Control FitRowControl(List<FitRow> rows, int index, int fontSize, Color color, bool selected)
    {
        var columns = FitColumns(rows, fontSize);
        var row = rows[index];
        return CursorRow.BuildColumns(row.Label, row.Value, columns.X, columns.Y,
            fontSize, color, selected);
    }

    // The horizontal axis's effect, screen by screen, always a live-editing stepper on
    // whichever field the vertical cursor is focused on, never a "select and lock" gesture (that
    // is what Accept is for). Split out of HandleInput because it now has one branch
    // per wizard screen that carries a stepper: the mission choice's lives, and the wave
    // editor's four fields plus the wingman count/aircraft.
    private bool HandleMoveX(int dir)
    {
        switch (_screen)
        {
            case Screen.Options:
                // The fifteen choice rows are steppers; the doors under them have nothing to step.
                switch (_optionsIndex)
                {
                    case 0: StepDifficultyChoice(dir); return true;
                    case 1: StepDefaultViewChoice(dir); return true;
                    case 2: ToggleAutoHeadTurnChoice(); return true;
                    case 3: ToggleNearestAfterKillChoice(); return true;
                    case 4: ToggleRumbleChoice(); return true;
                    case 5: ToggleGraphicsChoice(); return true;
                    case 6: ToggleRocketCratersChoice(); return true;
                    case 7: StepMonitorChoice(dir); return true;
                    case 8: StepResolutionChoice(dir); return true;
                    case 9: StepDisplayModeChoice(dir); return true;
                    case 10: StepVSyncChoice(dir); return true;
                    case 11: _audioMasterChoice = StepLevel(_audioMasterChoice, AudioMix.DefaultMaster, dir); return true;
                    case 12: _audioMusicChoice = StepLevel(_audioMusicChoice, AudioMix.DefaultMusic, dir); return true;
                    case 13: _audioEffectsChoice = StepLevel(_audioEffectsChoice, AudioMix.DefaultEffects, dir); return true;
                    case 14: _audioVoiceChoice = StepLevel(_audioVoiceChoice, AudioMix.DefaultVoice, dir); return true;
                    default: return false;
                }
            case Screen.Network:
                // The port is the door's one stepper. The address is typed, and the three rows
                // under it are presses.
                if (_coopWait || _netIndex != NetPortRow)
                {
                    return false;
                }

                _net?.StepPort(dir);
                return true;
            case Screen.Chapter:
                // Only the two Dogfight rows under the map list step; a map row has nothing
                // sideways, and MatchRowCount is 0 in the other two modes.
                if (_chapterIndex < CurrentChapters.Length)
                {
                    return false;
                }

                if (_chapterIndex == CurrentChapters.Length)
                {
                    _setup.StepKillTarget(dir);
                }
                else
                {
                    _setup.StepTimeLimit(dir);
                }

                return true;
            case Screen.MissionType:
                // The lives stepper rides the same screen as the mission choice (decision 18),
                // so it never competes with the vertical list cursor above.
                _ia.StepLives(dir);
                return true;
            case Screen.WaveEdit:
                // The feature's own rules: the count clamps, the militia wraps and resets its
                // aircraft, the aircraft wraps within the militia's roster, the skill wraps.
                switch (_waveFieldIndex)
                {
                    case 0: _ia.StepWaveCount(_waveEditIndex, dir); break;
                    case 1: _ia.StepWaveMilitia(_waveEditIndex, dir); break;
                    case 2: _ia.StepWaveAircraft(_waveEditIndex, dir); break;
                    case 3: _ia.StepWaveSkill(_waveEditIndex, dir); break;
                }
                return true;
            case Screen.Wingmen:
                if (_wingmenFieldIndex == 0)
                {
                    _ia.StepWingmen(dir);
                    // The Aircraft row disappears at 0 wingmen, keep the cursor on a row that
                    // still exists rather than pointing at a field nothing draws.
                    _wingmenFieldIndex = Math.Min(_wingmenFieldIndex, WingmenRowCount - 1);
                }
                else
                {
                    _ia.StepWingmanPlane(dir);
                }
                return true;
            case Screen.WingmanLoadout:
                {
                    var rows = WingmanFitRows();
                    _wingmanFitRow = Math.Clamp(_wingmanFitRow, 0, rows.Count - 1);
                    StepFit(FitFor(_ia.WingmanPlaneIndex), _ia.WingmanFit, rows[_wingmanFitRow], dir);
                }
                return true;
            default:
                return false;
        }
    }

    // The Accept gesture's effect, screen by screen, split out of
    // HandleInput for the same reason HandleMoveX was: one branch per
    // wizard screen now, most of them advancing the wizard rather than picking a row.
    private void HandleAccept()
    {
        switch (_screen)
        {
            case Screen.WingmanLoadout:
                {
                    // Reset is the only row Accept does anything on: the rest are live steppers,
                    // and B is what leaves, so Accept has nothing else to mean here.
                    var rows = WingmanFitRows();
                    _wingmanFitRow = Math.Clamp(_wingmanFitRow, 0, rows.Count - 1);
                    if (rows[_wingmanFitRow].Kind == FitRowKind.Reset)
                    {
                        _ia.ResetWingmanFit();
                    }
                }
                break;
            case Screen.Options:
                // Accept on a stepper row is the sideways step forwards, so a row is walkable with
                // one gesture; the two rows past them are the doors this screen leaves by.
                if (_optionsIndex < OptionsStepperRows)
                {
                    HandleMoveX(1);
                }
                else if (_optionsIndex == OptionsStepperRows)
                {
                    _screen = Screen.Controls;
                    OpenControls();
                }
                else
                {
                    // The launcher persists every choice and restarts the menu; the screen stays
                    // standing for the host to hide.
                    _host.Exit(new OptionsApplyExit(_graphicsChoice,
                        Difficulty.Word(_difficultyChoice), _monitorChoice, _resolutionChoice,
                        _displayModeChoice, _vsyncChoice, _audioMasterChoice, _audioMusicChoice,
                        _audioEffectsChoice, _audioVoiceChoice, _nearestAfterKillChoice, _rumbleChoice,
                        _defaultViewChoice, _autoHeadTurnChoice, _rocketCratersChoice));
                }

                break;
            case Screen.Mode:
                // The four trailing rows are the campaign's, the hangar's, Options' and the
                // multiplayer door's top-level doors, past the three modes.
                if (_modeIndex == Modes.Length)
                {
                    OpenCampaign();
                    break;
                }

                if (_modeIndex == Modes.Length + 2)
                {
                    _screen = Screen.Options;
                    OpenOptions();
                    break;
                }

                if (_modeIndex == Modes.Length + 3)
                {
                    _screen = Screen.Network;
                    _netIndex = _net is { Stage: NetDoorStage.Shut } or null ? NetHostRow : NetContinueRow;
                    break;
                }

                if (_modeIndex > Modes.Length)
                {
                    OpenHangar(Screen.Mode);
                    break;
                }

                _mode = (MenuMode)_modeIndex; // the row order IS the enum order
                if (_mode == MenuMode.Stunt) // "Instant Action", the wizard's step 1
                {
                    _screen = Screen.Environment;
                }
                else
                {
                    _screen = Screen.Chapter;
                    // The roster may have shrunk (Stunt hid the dzone-less maps last time
                    // around), keep the cursor on a row that exists.
                    _chapterIndex = Wrap(_chapterIndex, CurrentChapters.Length);
                }
                break;
            case Screen.Network:
                HandleNetworkAccept();
                break;
            case Screen.Presets:
                ApplyPreset(_presetCursor);
                // The original's own page order: the contents list is page 1 and View Story opens
                // page 2, the configuration screen under the preset's name. Our wizard is that
                // second page unrolled into five screens, so Accept lands on the first of them.
                _screen = Screen.Environment;
                break;
            case Screen.Environment:
                _screen = Screen.MissionType;
                // The feature re-fits the mission cursor onto a row the environment offers (Stunt
                // Flying is hidden on "the clouds") and loads the environment's own ia.zrd.json as
                // the launch's ace/zeppelin/disallow_missions base, once here rather than per Rebuild.
                _ia.ConfirmEnvironment();
                break;
            case Screen.MissionType:
                if (_ia.IsAceDuel)
                {
                    // Dogfighting an Ace takes no wave or wingman configuration, the decoded
                    // setup screen's own behaviour (mission type 0 hides every enemy control).
                    _screen = Screen.Plane;
                    PrimeJoins();
                }
                else
                {
                    _screen = Screen.Waves;
                    // Opens on the trailing Continue row, not wave 1: every slot starts empty
                    // (decision 1), so configuring none is the common path and the harmless row is
                    // the one under the cursor. Coming BACK from a wave keeps that wave's row.
                    _waveListIndex = InstantActionFeature.WaveSlots;
                }
                break;
            case Screen.Chapter:
                // A match row's Accept is its own sideways step, so a row is walkable with one
                // gesture. Only a map row leaves the screen, which keeps the cursor on a map
                // whenever anything downstream reads the pick.
                if (_chapterIndex >= CurrentChapters.Length)
                {
                    HandleMoveX(1);
                    break;
                }

                if (_mode == MenuMode.Free)
                {
                    _free.SelectChapter(CurrentChapters[_chapterIndex].Code);
                }

                _screen = Screen.Plane;
                PrimeJoins(); // joining opens here, a Start held on the way in must not fire
                break;
            case Screen.Waves:
                if (_waveListIndex < InstantActionFeature.WaveSlots)
                {
                    _waveEditIndex = _waveListIndex;
                    _waveFieldIndex = 0;
                    _screen = Screen.WaveEdit;
                }
                else // the trailing "Continue" row
                {
                    _screen = Screen.Wingmen;
                    _wingmenFieldIndex = 0;
                }
                break;
            case Screen.WaveEdit:
                _screen = Screen.Waves; // live-edited already; Accept just means "done"
                break;
            case Screen.Wingmen:
                _screen = Screen.Plane;
                PrimeJoins();
                break;
        }
    }

    // --- the multiplayer door ---

    // The door's address field, when it is the row under the cursor. Every typed character asks
    // through here, so nothing reaches the address from another screen.
    private NetPlayFeature? AddressField() =>
        _screen == Screen.Network && !_coopWait && _netIndex == NetAddressRow ? _net : null;

    // One keypress into the address. Redrawn on the next frame rather than here, because Rebuild
    // replaces the very controls the event is being dispatched through.
    private void TypeAddress(NetPlayFeature net, InputEventKey key)
    {
        string before = net.Address;
        if (key.Keycode == Key.Backspace)
        {
            net.EraseAddress();
        }
        else if (key.Unicode > 0)
        {
            net.TypeAddress(((char)key.Unicode).ToString(CultureInfo.InvariantCulture));
        }

        if (net.Address != before)
        {
            _typed = true;
            GetViewport().SetInputAsHandled();
        }
    }

    // The board's presses. Accept on the port row steps it, which is the Chapter screen's own
    // rule that a row is walkable with one gesture. The address row is typed into instead.
    private void HandleNetworkAccept()
    {
        if (_net is not { } net)
        {
            _error = "this build has no multiplayer door";
            return;
        }

        // The waiting board's one row hangs up and hands the board back, shut, to join again.
        if (_coopWait)
        {
            net.Close();
            _coopWait = false;
            _netIndex = NetJoinRow;
            return;
        }

        switch (_netIndex)
        {
            case NetPortRow:
                HandleMoveX(1);
                break;
            case NetHostRow:
                net.OpenHost(NetSeats.MaxPlayers - 1);
                _netIndex = net.Stage == NetDoorStage.Hosting ? NetContinueRow : NetHostRow;
                _error = net.Fault;
                break;
            case NetJoinRow:
                net.OpenJoin();
                _error = net.Fault;
                break;
            case NetContinueRow:
                OpenNetworkSortie(net);
                break;
        }
    }

    // Leaving the board for the sortie the wire carries. A network match is a Dogfight, so the
    // mode is set here rather than picked again. ⚠ Both ends pick their own map: no pre-session
    // message carries the host's, so the two boards have to agree on one by hand.
    private void OpenNetworkSortie(NetPlayFeature net)
    {
        if (!net.CanLaunch)
        {
            _error = net.Fault.Length > 0 ? net.Fault : "host or join a match first";
            return;
        }

        // A campaign host flies its own mission, so a guest has no map to pick: it waits for
        // the launch instead.
        if (net.IsCoopGuest)
        {
            _coopWait = true;
            _netIndex = 0;
            _error = "";
            return;
        }

        _mode = MenuMode.Versus;
        _modeIndex = (int)MenuMode.Versus;
        _screen = Screen.Chapter;
        _chapterIndex = Wrap(_chapterIndex, CurrentChapters.Length);
        _error = "";
    }

    // --- the hangar ---

    // The PLANENAME page, when it is the one showing. Every typed-character path asks through here,
    // so nothing reaches a plane's name from another screen.
    private HangarNamePage? NamePage() =>
        _screen == Screen.Hangar && _hangar?.Page is HangarNamePage page ? page : null;

    // Opens the Build Custom Plane flow, remembering the screen to land back on. Both doors
    // (the Mode screen's trailing row and the Instant Action plane pick) come through here, so
    // there is one entry, one exit and one place the scratch plane lives.
    private void OpenHangar(Screen returnTo)
    {
        _hangarReturn = returnTo;
        _hangar = new HangarFlow(_hangarFeature, CustomPlaneStore.UserPlanes(), _dataRoot,
            nameRng: Rng.NewSystemRandom(Rng.PlaneName));
        _screen = Screen.Hangar;
        _error = "";
    }

    // The hangar's screens sit behind a flow rather than behind the screen enum, so --menu= opens
    // one and walks it: "hangar" the plane list, "airframe" the airframe list, "defaults" its
    // defaults ask, "paint" the preview on a Fury in Fortune Hunters colours. An aid only.
    private void OpenHangarAid(string startScreen)
    {
        if (startScreen is not ("hangar" or "airframe" or "defaults" or "paint" or "name"))
        {
            return;
        }

        OpenHangar(Screen.Mode);
        if (_hangar is not { } flow || startScreen == "hangar")
        {
            return;
        }

        flow.Accept(); // New Plane, on to the airframe screen
        if (startScreen == "airframe")
        {
            return;
        }

        flow.Accept(); // pick the focused airframe
        if (startScreen == "defaults")
        {
            // Only an airframe swap over an edited build asks, so an engine is picked by hand
            // first and the swap made onto the next row (E49).
            flow.Feature.SetEngine(1);
            flow.Move(1);
            flow.Accept();
            return;
        }

        var stopAt = startScreen == "name" ? HangarScreen.Name : HangarScreen.Paint;
        for (int guard = 0; flow.Screen != stopAt && guard < HangarFlow.Order.Length; guard++)
        {
            flow.Accept();
        }

        if (startScreen == "name")
        {
            return;
        }

        flow.Scratch.Airframe = 7;
        flow.Scratch.LoadPatternDefaults(4);
        flow.Scratch.NoseDecal = 40;
        flow.FocusRow(HangarPaintPage.NoseDecalRow); // so the shot shows the decal tile too
    }

    // One frame of player 1's input on a hangar screen. Nobody else steers it: the flow edits one
    // scratch plane, and a second cursor in it would have nothing of its own to move.
    private bool HandleHangarInput(MenuInput p1)
    {
        if (_hangar is not { } flow)
        {
            _screen = _hangarReturn;
            return true;
        }

        bool dirty = false;
        if (p1.Move != 0)
        {
            dirty |= flow.Move(p1.Move);
        }

        if (p1.MoveX != 0)
        {
            dirty |= flow.Step(p1.MoveX);
        }

        if (p1.Accept)
        {
            dirty |= flow.Accept();
        }
        else if (p1.Back)
        {
            dirty |= flow.Back();
        }

        // The gate's refusal (overweight, no engine, no name) rides the screen's own error line.
        _error = flow.Message;
        if (flow.Exit != HangarExit.None)
        {
            CloseHangar(flow);
            return true;
        }

        return dirty;
    }

    // Leaves the flow, built or cancelled, for the screen it was opened from. A cancelled flow
    // wrote nothing, so there is nothing to undo. Every close re-reads the store, so a build
    // shows up in the pickers without a menu restart; a build then puts player 1's cursor on the
    // new plane, the original's index-11 contract (select the first custom slot after a build),
    // done by name because our customs sort rather than filling slots.
    private void CloseHangar(HangarFlow flow)
    {
        _screen = _hangarReturn;
        _hangar = null;
        _error = "";
        RefreshRoster();
        // Built or cancelled alike: the campaign flow behind the door stood still while the hangar
        // ran, and resuming it re-reads the profile the wallet was spent from.
        if (_hangarReturn == Screen.Campaign)
        {
            _campaign?.Resume();
        }

        if (flow.Exit == HangarExit.Built && flow.BuiltPlaneName is { } name)
        {
            LastBuiltPlane = name;
            int at = PlanePickerRoster.IndexOf(_roster, name);
            if (at >= 0)
            {
                // Selected in the picker the hangar was entered from: from the plane pick this
                // is the visible cursor; from the Mode door it is where the pick opens later.
                _slots[0].PlaneIndex = at;
            }

            Log.Info("ui", $"launchscreen: hangar built \"{name}\", selected in the plane picker");
        }
    }

    // --- the campaign ---

    // Opens the campaign's out-of-mission flow on its first screen, the profile roster. There is
    // one door and one exit; every screen inside it is a page of the flow's own.
    private void OpenCampaign()
    {
        _campaign = NewCampaignFlow(CampaignProfiles ?? CampaignProfileStore.UserProfiles());
        _screen = Screen.Campaign;
        _error = "";
        PrimeJoins(); // joining opens here too, so a held Start must not fire on entry
    }

    // A campaign flow over the shared feature opened on one store, with the two stores its later
    // screens resolve fits through: the hangar's build store for a player-built aircraft, the stock
    // table for everything else (the two profile-seeded starters and the reward aircraft, neither
    // of which is hangar-built). Without them the flight check and ammo screens read every plane
    // as fit-less.
    private CampaignFlow NewCampaignFlow(CampaignProfileStore store, CustomPlaneStore? planes = null)
    {
        _campaignFeature.Open(store, planes ?? CustomPlaneStore.UserPlanes(), Fits, _dataRoot);
        return new CampaignFlow(_campaignFeature, CampaignLayoutOverride);
    }

    // The campaign's screens sit behind a flow rather than behind the screen enum, so --menu=
    // reaches them the way it reaches the hangar's. The player's door and campaign-fly run over
    // the presentation's store; every other value runs over the shared scratch directory, so the
    // shot is the same on every machine and no aid can write into a real campaign. The briefing's
    // colon argument is a seconds count, because its screen is a two-minute reveal and every stage
    // of it is a different picture; on the other screens it is a CampaignAidScript.
    private void OpenCampaignAid(string startScreen)
    {
        int colon = startScreen.IndexOf(':');
        string value = colon < 0 ? startScreen : startScreen[..colon];
        if (value is not (CampaignAidProfiles.PlayerDoor or "campaign-empty" or "campaign-roster" or "campaign-entry"
            or "campaign-cabin" or "campaign-previous" or "campaign-scrapbook" or "campaign-briefing"
            or "campaign-flightcheck" or "campaign-guestcheck" or "campaign-ammo"
            or "campaign-planeselection" or "campaign-hangar" or "campaign-fly" or "campaign-coop"))
        {
            return;
        }

        if (value == CampaignAidProfiles.PlayerDoor)
        {
            OpenCampaign();
            return;
        }

        // The one aid that runs over the REAL profile store, because it is the one that launches a
        // mission: the session's own director reads user://Profiles, so a scratch profile would
        // not exist by the time the world builds.
        if (value == "campaign-fly")
        {
            OpenCampaign();
            FlyFirstRealProfile();
            return;
        }

        // Two of the aids need a roster to pick from and five need a profile part-way through the
        // campaign; the seeded store carries both, since a second profile changes no later screen.
        bool seeded = value != "campaign-empty" && value != "campaign-entry";
        _campaign = NewCampaignFlow(AidProfileStore(seeded, progressed: value != "campaign-roster"), CampaignAidProfiles.Planes());
        _screen = Screen.Campaign;
        _error = "";
        PrimeJoins(); // this entry point needs the same held-Start guard as OpenCampaign
        if (_campaign is not { } flow)
        {
            return;
        }

        if (value == "campaign-entry")
        {
            flow.Accept();          // arm the name field
            flow.Type("Zachary");   // a name mid-entry, caret and all
            return;
        }

        string word = colon < 0 ? string.Empty : startScreen[(colon + 1)..];
        float.TryParse(word, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float argument);

        WalkCampaignAid(flow, value, argument);

        // Three aids spend the colon on a number WalkCampaignAid takes: seconds, a player, guests.
        // Every other screen's colon is the input script CampaignAidScript replays.
        if (value is not ("campaign-briefing" or "campaign-guestcheck" or "campaign-coop") && !CampaignAidScript.Replay(flow, word))
        {
            // A script this presentation cannot press leaves nothing worth shooting, so the run
            // ends before the capture takes a screen that looks like it simply did not respond.
            GetTree()?.Quit(1);
        }
    }

    // Walks the first stored profile to its next mission's flight check and presses FLY MISSION,
    // the end-to-end aid: it exercises the launch the cabin performs, not a screen. A store with
    // no profile leaves the flow on its roster, which is what a player with none would see.
    private void FlyFirstRealProfile()
    {
        if (_campaign is not { } flow || flow.Roster.Count == 0
            || flow.Store.Load(flow.Roster[0]) is not { } profile)
        {
            return;
        }

        flow.SelectProfile(profile);
        flow.SetMission(CampaignProgression.NextMissionSeq(profile));
        flow.GoTo(CampaignScreen.FlightCheck);
        flow.FocusRow(flow.Page.RowCount - 1); // FLY MISSION is the screen's last row
        flow.Accept();
    }

    // Walks a seeded flow to the screen an aid names. Every step is a call a player's own presses
    // would make, so no aid can reach a state the campaign itself cannot.
    private void WalkCampaignAid(CampaignFlow flow, string value, float seconds)
    {
        if (flow.Store.Load("Zachary") is not { } profile)
        {
            return;
        }

        flow.SelectProfile(profile);
        switch (value)
        {
            case "campaign-previous":
                flow.GoTo(CampaignScreen.PreviousMissions);
                return;
            case "campaign-scrapbook":
                // The book as a finished mission leaves it: opened on the last mission this
                // profile flew, which is the one door the mission end itself takes. No win is
                // reported, so the shot is the book and never the closing film.
                flow.GoTo(CampaignScreen.PreviousMissions);
                flow.OpenScrapbookAfterMission(
                    Math.Max(0, CampaignProgression.NextMissionSeq(profile) - 1), missionWon: false);
                return;
            case "campaign-briefing":
                flow.SetMission(CampaignProgression.NextMissionSeq(profile));
                flow.GoTo(CampaignScreen.Briefing);
                AdvanceBriefing(flow, seconds);
                return;
            case "campaign-flightcheck":
                flow.SetMission(CampaignProgression.NextMissionSeq(profile));
                flow.GoTo(CampaignScreen.FlightCheck);
                return;
            case "campaign-guestcheck":
                // The guest-check aid reuses the same screen. The argument names which guest
                // one (default P2), and --debug-join= is what puts them on the field.
                flow.SetMission(CampaignProgression.NextMissionSeq(profile));
                flow.GoTo(CampaignScreen.FlightCheck);
                _aidGuest = Math.Max(1, (int)seconds);
                return;
            case "campaign-ammo":
                flow.SetMission(CampaignProgression.NextMissionSeq(profile));
                flow.SetAmmoSlot(0);
                flow.GoTo(CampaignScreen.Ammo);
                return;
            case "campaign-planeselection":
                // The pilot's CHANGE PLANE press, which is what names the slot the cursor opens on.
                flow.SetMission(CampaignProgression.NextMissionSeq(profile));
                flow.SetPlaneSlot(0);
                flow.GoTo(CampaignScreen.PlaneSelection);
                return;
            case "campaign-hangar":
                // The cabin's own PLANE CONSTRUCTION press, so the shot is the hangar standing
                // over this profile's wallet rather than the wallet-free door.
                flow.FocusRow(CampaignCabinPage.PlaneConstructionRow);
                flow.Accept();
                return;
            case "campaign-coop":
                // The cabin with the network open, over the loopback door the aids stand on. The
                // colon argument is how many guests are already on its wire.
                _net = NetDoorAid.Host(Math.Max(0, (int)seconds), out _);
                NetDoorAid.OpenCoopHost(_net, flow.Feature.NextMissionSeq, localPlayers: 1);
                return;
            default:
                return; // campaign-cabin: SelectProfile already landed there
        }
    }

    // Runs the reveal forward to a stage worth photographing. ⚠ Stepped in frame-sized slices, not
    // one jump: the script blocks on authored waits and measured cue points, so a single huge
    // delta would leave it standing at the first of them.
    private void AdvanceBriefing(CampaignFlow flow, float seconds)
    {
        if (flow.Page is not CampaignBriefingPage page || seconds <= 0f)
        {
            return;
        }

        const float slice = 1f / 60f;
        for (float t = 0f; t < seconds; t += slice)
        {
            page.Advance(slice);
        }
    }

    // The scratch store the campaign screenshot aids read, the one both presentations' aids share
    // (CampaignAidProfiles): emptied on every open, seeded for the filled-roster shot, progressed
    // for the screens past the cabin.
    private CampaignProfileStore AidProfileStore(bool seeded, bool progressed = false) =>
        CampaignAidProfiles.Store(seeded, progressed);

    // One frame of player 1's input on a campaign screen. While a page's text field is armed the
    // keyboard's letters are text, so the cursor axes come from the pad alone.
    private bool HandleCampaignInput(MenuInput p1)
    {
        if (_campaign is not { } flow)
        {
            _screen = Screen.Mode;
            return true;
        }

        flow.SetPlayers(_slots.Count);
        var driver = CampaignDriver(flow, p1);
        bool typing = flow.CapturesText;
        int move = typing ? driver.PadMove : driver.Move;
        int step = typing ? driver.PadMoveX : driver.MoveX;
        bool dirty = false;
        if (move != 0)
        {
            dirty |= flow.Move(move);
        }

        if (step != 0)
        {
            dirty |= flow.Step(step);
        }

        if (typing)
        {
            dirty |= flow.Type(driver.Typed);
            dirty |= driver.Erase && flow.Backspace();
        }

        // X is the screen's own shortcut, not a press of the focused plaque, so it lights no button
        // and is read before the confirm. While a name field is armed the keyboard's own X alias is
        // a letter being typed, which must not fire it.
        if (!typing && driver.Presets)
        {
            dirty |= flow.Secondary();
        }

        // L / Y opens the campaign to the network once a profile is seated. Player 1's press
        // alone: on a guest's own flight check the driver is that guest, whose Y is not the host's.
        if (!typing && p1.Loadout && ReferenceEquals(driver, p1) && flow.Profile != null && flow.Modal == null)
        {
            ToggleCoopDoor(flow);
            dirty = true;
        }

        OfferCoopMission(flow);

        if (driver.Accept)
        {
            _pressFrames = PressFrames;
            dirty = true;
            flow.Accept();
        }
        else if (driver.Back)
        {
            dirty |= flow.Back();
        }

        // Every guest can only drop out from here; the driving player's Back above is the
        // flow's own navigation. The field closes guest exits once the sequence runs;
        // settled, and every Back on screen is the walk back through the checks.
        for (int i = _slots.Count - 1; i >= 1 && !flow.Field.Locked; i--)
        {
            if (!_slots[i].Input.Back)
                continue;
            Unjoin(i);
            dirty = true;
        }

        // A refusal (an empty name, a name the original's own rule rejects, a full roster) rides
        // the screen's error line, the same place the hangar's gate reports.
        _error = flow.Message;
        switch (flow.Exit)
        {
            case CampaignExit.None:
                return dirty;
            case CampaignExit.OpenHangar:
                OpenCampaignHangar(flow);
                return true;
            case CampaignExit.FlyMission:
                FlyCampaignMission(flow);
                return true;
            default:
                StopNarration();
                CloseCoopDoor();
                _screen = Screen.Mode;
                _campaign = null;
                _campaignFeature.Discard();
                _error = "";

                // An EXPORT in the campaign that was just left added a plane to the pickers, and the
                // sortie screens are reached from here without another Show to re-read on.
                RefreshRoster();
                return true;
        }
    }

    // Whose presses steer the campaign board. Player 1's everywhere, except a guest's own flight
    // check, which is that guest's screen to fill in. A driver with no device, --debug-join's
    // deviceless players, hands back to player 1, or a screenshot aid could never walk the
    // sequence at all.
    private MenuInput CampaignDriver(CampaignFlow flow, MenuInput p1)
    {
        int at = flow.Field.Current;
        if (at <= 0 || at >= _slots.Count)
        {
            return p1;
        }

        var input = _slots[at].Input;
        return input.Keyboard || input.Pads is { Length: > 0 } ? input : p1;
    }

    // PLANE CONSTRUCTION: the hangar over the profile's own wallet, the feature's CampaignWallet,
    // with the campaign flow left standing behind it. CloseHangar resumes the flow, which re-reads
    // the profile, so a purchase or a sale shows on the cabin the moment the hangar closes.
    private void OpenCampaignHangar(CampaignFlow flow)
    {
        if (flow.Feature.Wallet() is not { } wallet || flow.Planes is not { } planes)
        {
            flow.Resume();
            return;
        }

        _hangarReturn = Screen.Campaign;
        _hangar = new HangarFlow(_hangarFeature, planes, _dataRoot, wallet, Rng.NewSystemRandom(Rng.PlaneName));
        _screen = Screen.Hangar;
        _error = "";
    }

    // FLY MISSION: the feature saves the profile and builds the campaign mission exit for this
    // profile and story position, one seat per joined human with the pads that seat joined on (the
    // cabin's join flow is the only place that binding exists, and the consumer cannot re-derive it
    // from the connected roster). The wingman's own binding is resolved by CampaignDirector, which
    // has the profile open anyway.
    private void FlyCampaignMission(CampaignFlow flow)
    {
        int players = _slots.Count;
        var pads = new List<IReadOnlyList<int>>(players);
        for (int player = 0; player < players; player++)
        {
            pads.Add(_slots[player].Input.Pads ?? Array.Empty<int>());
        }

        if (flow.Feature.BuildExit(pads) is not { } exit)
        {
            flow.Resume();
            return;
        }

        Log.Info("ui", $"launchscreen: campaign '{exit.Profile}' flying mission seq {exit.MissionSeq} in \"{flow.Field.Plane(0)?.Name}\"");
        for (int player = 1; player < players; player++)
        {
            Log.Info("ui", $"launchscreen: campaign P{player + 1} flying \"{flow.Field.Plane(player)?.Name}\"");
        }

        StopNarration();

        // No campaign mission carries a wire yet, so the door closes rather than stranding its
        // guests on a socket nobody steps.
        CloseCoopDoor();
        _campaign = null;
        _campaignFeature.Discard();
        _screen = Screen.Mode;
        _error = "";
        _host.Exit(exit);
    }

    // The open socket is stepped on every menu frame, not only on the board. A host that walked
    // on to pick a map still has to admit guests, and a guest still has to hear its host. The
    // feature itself goes quiet once a launch has taken the wire off it. A campaign board
    // repaints on the same reading, since its band and its chips count the guests.
    private bool TickNetDoor(double delta)
    {
        if (_net is not { } net)
        {
            return false;
        }

        net.Step(delta);

        // A waiting board whose link dropped has nothing left to wait for; the board says why.
        if (_coopWait && !net.IsCoopGuest)
        {
            _coopWait = false;
            _netIndex = NetJoinRow;
            _error = net.Fault;
        }

        string reading = _screen == Screen.Campaign
            ? $"{NetBand()}|{string.Join(" ", ChipTexts())}"
            : $"{_coopWait}|{NetworkStatus()}|{RowText(CurrentIndex)}";
        bool changed = reading != _netStatus;
        _netStatus = reading;
        return changed && _screen is Screen.Network or Screen.Campaign;
    }

    // --- the campaign's network door ---

    // L / Y on a campaign board with a profile seated. It opens the carrier and asks the router for
    // the port, or closes both. A door already open from the Multiplayer board is that board's
    // to close, so the press refuses rather than taking it over.
    private void ToggleCoopDoor(CampaignFlow flow)
    {
        if (_net is not { } net)
        {
            flow.SetMessage("This build has no multiplayer door.");
            return;
        }

        if (net.IsCoopHost)
        {
            CloseCoopDoor();
            return;
        }

        if (net.Stage is NetDoorStage.Hosting or NetDoorStage.Joining or NetDoorStage.Joined)
        {
            flow.SetMessage("The network is already open from the Multiplayer board.");
            return;
        }

        net.Close();
        net.OpenCoopHost(NetSeats.MaxPlayers - _slots.Count);
        OfferCoopMission(flow);
        flow.SetMessage(net.Fault.Length > 0 ? $"The network did not open: {net.Fault}" : "");
    }

    // What the open door advertises: the mission the boards are about, else the profile's next.
    // Re-offered every frame; the lobby sends only a change, a guest arriving or a mission picked.
    private void OfferCoopMission(CampaignFlow flow)
    {
        if (_net is not { IsCoopHost: true } net || flow.Profile is not { } profile)
        {
            return;
        }

        int seq = flow.MissionSeq >= 0 ? flow.MissionSeq : flow.Feature.NextMissionSeq;
        net.Offer(seq, profile.Name, _slots.Count);
    }

    // Leaving the campaign, flying out of it and a menu reopened all close the carrier and give
    // the router's port back. A door the Multiplayer board opened is left alone.
    private void CloseCoopDoor()
    {
        if (_net is { IsCoopHost: true } net)
        {
            net.Close();
        }
    }

    // Whether the chip strip stands: on a campaign board, once a second player or the network band
    // has something to show. A solo board with the door shut looks as it always did.
    private bool ChipStripShown() =>
        _screen == Screen.Campaign && _campaign != null
        && (_slots.Count + RemoteGuests() > 1 || NetBand().Length > 0);

    // How many guests at other machines stand on the campaign's field: the open door's peers.
    private int RemoteGuests() => _net is { IsCoopHost: true } net ? net.Peers : 0;

    // The campaign host's band, or "" while the door is shut.
    private string NetBand() => _net is { } net ? CoopDoorText.HostBand(net) : "";

    // One chip per player on the field: the local seats, then the remote guests. A remote guest
    // takes a seat and a chip but no pane, and its chip says so.
    private List<string> ChipTexts()
    {
        var chips = new List<string>(_slots.Count + RemoteGuests());
        for (int i = 0; i < _slots.Count; i++)
        {
            chips.Add(SplitScreen.PlayerTag(i));
        }

        for (int i = 0; i < RemoteGuests() && chips.Count < NetSeats.MaxPlayers; i++)
        {
            chips.Add(SplitScreen.PlayerTag(chips.Count) + RemoteChipMark);
        }

        return chips;
    }

    // The long name a waiting board and a join board give a campaign mission.
    private string MissionName(int seq) => HangarStrings().Text(3450 + seq, $"Mission {seq + 1}");

    // The two guest aids: the board joined to a campaign host, and the waiting board past it.
    // Both stand on a loopback door, so no aid opens a socket or asks a router for anything.
    private void OpenNetDoorAid(string startScreen)
    {
        if (startScreen is not ("network-coopjoin" or "network-coopwait"))
        {
            return;
        }

        _net = NetDoorAid.JoinedGuest(missionSeq: 7, players: 3);
        _netIndex = NetContinueRow;
        if (startScreen == "network-coopwait" && _net is { } net)
        {
            OpenNetworkSortie(net);
        }
    }

    // The briefing's clock and its narration, the two things its page cannot own: a page holds no
    // Godot node and has no frame to advance on. Returns whether the board needs redrawing, which
    // is whenever the reveal uncovered a line or changed the map it is drawing.
    private bool TickCampaignAudio(double delta)
    {
        if (_screen != Screen.Campaign || _campaign?.Page is not CampaignBriefingPage page)
        {
            StopNarration();
            return false;
        }

        page.Advance(delta);
        if (page.NarrationStarts != _narrationStarts)
        {
            _narrationStarts = page.NarrationStarts;
            // The service ducks the music from here until EndNarration, the whole stay: the gaps
            // between lines are short, and a duck that lifted in them would pump the splash track.
            _host.Audio.BeginNarration(page.NarrationWav);
        }

        // ⚠ Do not turn this back into a did-it-change test over page properties. A reveal fades,
        // spins and moves its elements continuously, so such a list is only the cases someone
        // remembered; while it runs, the board repaints on the frame clock instead.
        bool running = page.Reveal is { Complete: false };
        bool repaint = running || _briefingRunning;
        _briefingRunning = running;
        return repaint;
    }

    // Ends the narration, which lifts the music duck too: every door out of the briefing comes
    // through here, including the launch, which hides the board and so stops TickCampaignAudio
    // from running again.
    private void StopNarration()
    {
        _narrationStarts = 0;
        _briefingRunning = false;
        _host.Audio.EndNarration();
    }

    // Re-reads the saved-plane store into the picker roster and keeps every cursor inside the
    // possibly-shrunk list (a deleted file, a rename). The door row, when offered, sits at
    // _roster.Count, which PlaneRowCount - 1 still admits.
    private void RefreshRoster()
    {
        var customs = CustomPlaneStore.UserPlanes().List();
        _customDefs = customs;
        _roster = PlanePickerRoster.Build(Planes, customs);
        _setup.SetRoster(MenuRoster(customs));
        foreach (var slot in _slots)
            slot.PlaneIndex = Math.Min(slot.PlaneIndex, PlaneRowCount - 1);
    }

    // The stock display name behind a roster row: the row's own name for a stock pick, the
    // airframe's stock aircraft for a custom, for consumers that speak ia.json's vocabulary.
    private string NominalPlaneName(int rosterIndex)
    {
        var pick = _roster[rosterIndex];
        if (!pick.IsCustom)
        {
            return pick.Name;
        }

        foreach (var (name, node) in Planes)
            if (node == pick.Node)
                return name;
        return pick.Name;
    }

    // The langui table, read once per session on first hangar entry. A missing extraction is a
    // warning, not a refusal: every hangar label carries its own fallback text.
    private UiStrings HangarStrings()
    {
        if (_uiStrings == null)
        {
            _uiStrings = UiStrings.TryLoad(_dataRoot);
            if (_uiStrings == null)
            {
                GD.PushWarning("launchscreen: no extracted/rof/ tree, hangar labels fall back");
                _uiStrings = UiStrings.Empty;
            }
        }

        return _uiStrings;
    }

    /// <summary>Writes one Table of Contents preset over the feature's fields, then mirrors its
    /// player aircraft onto player 1's cursor. Player 1 only: the original has one pilot and one
    /// aircraft dropdown; players 2-4 are our own divergence and the preset has nothing to say
    /// about them. It is a cursor position, not a lock, since presets are picked back at step 1
    /// and no slot has selected anything yet.</summary>
    private void ApplyPreset(int index)
    {
        _ia.ApplyPreset(index);
        _slots[0].PlaneIndex = _ia.PlayerPlaneIndex;
        _slots[0].Fit.ResetToStock();
    }

    // Keeps the 14-row window over the cursor, and never scrolls past the end of the list. The
    // cursor wraps (Wrap, like every other screen), so top-to-bottom jumps both ways are normal
    // here rather than an edge case.
    private void ScrollPresetsToCursor()
    {
        int last = Math.Max(0, InstantActionPresets.All.Count - PresetWindow);
        int top = Math.Clamp(_presetTop, 0, last);
        if (_presetCursor < top)
            top = _presetCursor;
        else if (_presetCursor >= top + PresetWindow)
            top = _presetCursor - PresetWindow + 1;
        _presetTop = Math.Clamp(top, 0, last);
    }

    // The Options window's counterpart of the one above, the cursor wrapping the same way.
    private void ScrollOptionsToCursor()
    {
        int last = Math.Max(0, CurrentCount() - OptionsWindow);
        int top = Math.Clamp(_optionsTop, 0, last);
        if (_optionsIndex < top)
            top = _optionsIndex;
        else if (_optionsIndex >= top + OptionsWindow)
            top = _optionsIndex - OptionsWindow + 1;
        _optionsTop = Math.Clamp(top, 0, last);
    }

    // Whether the Plane screen's launch gesture is live right now. The gate reads CONFIRMED, the
    // second stage, which leaves a window between selecting an airframe and flying it for the
    // loadout to be opened in. A lone Dogfight pilot stays on this screen with JoinHint naming
    // what it is waiting for. Free Flight's gate adds its chapter to the setup's seat rule.
    private bool CanLaunch() => _mode == MenuMode.Free
        ? _free.CanLaunch(_setup.Seats.Count, _setup.ConfirmedCount)
        : _setup.CanLaunch(_mode, Networked());

    // Whether this launch goes out over a wire the door already opened. A networked Dogfight
    // seats one pilot here and the rest at other machines. The local two-seat minimum is
    // therefore not the thing to gate it on.
    private bool Networked() => _net is { CanLaunch: true };

    // Every non-campaign launch leaves as one LaunchExit through the host. Each mode's exit is its
    // own feature's: Free Flight's, Instant Action's, and Dogfight's the shared player setup's,
    // which is where its match rules live. Our state is left as-is either way, so a failed build
    // can send us back with ShowMenu.
    private void FireLaunch()
    {
        var seats = SeatChoices();
        if (_mode == MenuMode.Free)
        {
            _host.Exit(_free.BuildExit(seats));
            return;
        }

        if (_mode == MenuMode.Stunt) // Instant Action
        {
            // Player 1's own pick, a nominal label only; the roster, not this value, decides
            // what any human actually flies. A custom pick names its airframe's stock aircraft:
            // the def's consumers speak ia.json's stock vocabulary.
            _host.Exit(_ia.BuildExit(seats, NominalPlaneName(_slots[0].PlaneIndex)));
            return;
        }

        var exit = _setup.BuildExit(CurrentChapters[_chapterIndex].Code, _mode, _devices.FlightPads);

        // The open wire rides out with the launch, and the door keeps nothing: from here the
        // session owns the transport, steps it and closes it.
        _host.Exit(Networked() ? exit with { Net = _net!.BuildLaunch() } : exit);
    }

    // Every joined seat's pick as the typed seat choice, built by the setup: the roster row's
    // node, the pads the seat joined on (the device bookkeeping's answer), its fit edits (null for
    // stock) and, for a custom row, the def the roster was read with. ⚠ A custom pick launches as
    // its airframe's stock node; the def rides along for the session build.
    private IReadOnlyList<MenuSeatChoice> SeatChoices() => _setup.Choices(_devices.FlightPads);

    // --- rendering ---

    private void Rebuild()
    {
        _viewSize = GetViewport().GetVisibleRect().Size;
        // Every layout below frees the row controls, so a press on one of them cannot complete.
        _rowControls.Clear();
        _pressRow = null;

        // A campaign screen is a composed board at authored pixel positions, not a row list, so it
        // takes the whole window and neither of the other two layouts draws behind it.
        bool board = _screen == Screen.Campaign && _campaign != null;

        // Several players choosing aircraft get a real split screen, one panel each, laid out by
        // SplitScreen.PaneRect, so you pick in the pane you will then fly in. Everything else (and
        // every single-player screen) keeps the centred layout untouched.
        bool split = !board && _screen == Screen.Plane && _slots.Count > 1;
        _zones.Visible = !split && !board;
        _paneRoot.Visible = split;
        _boardRoot.Visible = board;
        _chipStrip.Visible = ChipStripShown();
        _netBandLabel.Visible = _chipStrip.Visible && NetBand().Length > 0;
        if (board)
        {
            RebuildBoard();
            return;
        }

        if (split)
        {
            // The hangar DOOR exists only in the lone-pilot layout, so a pilot joining while
            // player 1 sits on it must not leave a cursor past the roster's end. Only the door
            // is clamped away: a custom row stays a valid pick in every pane (Decision 6).
            foreach (var slot in _slots)
                slot.PlaneIndex = Math.Min(slot.PlaneIndex, _roster.Count - 1);
            RebuildPanes();
            return;
        }

        // The header and the footer keep the heights their own contents need and nothing else, so
        // the rows in between are the only thing a screen can move.
        var zones = Zones();
        _header.CustomMinimumSize = new Vector2(0f, zones.Header);
        _footer.CustomMinimumSize = new Vector2(0f, zones.Footer);
        Clear(_header);
        Clear(_middle);
        Clear(_footer);
        _header.AddChild(HeaderColumn(zones.Scale));
        _middle.AddChild(MiddleColumn(zones.Scale));
        _footer.AddChild(FooterColumn(zones.Scale));
    }

    // The header band: the title, where in the menu the pilot is, and who is holding what. The same
    // three lines on every centred screen, which is what makes the band's height a constant.
    private Control HeaderColumn(float s)
    {
        var column = Column(ContentWidth * s, s);
        column.AddChild(Spacer((int)(ZonePad * s)));
        column.AddChild(Label("CRIMSON SKIES", (int)(TitleFont * s), TitleColor, HorizontalAlignment.Center));
        column.AddChild(Label(Breadcrumb(), (int)(CrumbFont * s), CrumbColor, HorizontalAlignment.Center));
        column.AddChild(JoinStrip(s));
        return column;
    }

    // The middle band: what this screen is, the hangar's running total, the rows themselves with
    // the art column beside them, and the two status slots the aircraft screen writes into.
    private Control MiddleColumn(float s)
    {
        // The hangar's art sits in a column of its own to the LEFT of the rows (E47b, the layout
        // the original's paint screen uses), so the band is that much wider when it shows.
        var hangarArt = PageArt();
        // The Controls list is two columns wide (the action, then every control on it), so it needs
        // more room than a centred one-line row does.
        float extra = (hangarArt != null ? HangarArtWidth : 0) + (_screen == Screen.Controls ? ControlsExtraWidth : 0);
        var column = Column((ContentWidth + extra) * s, s);
        column.AddChild(Spacer((int)(ZonePad * s)));
        column.AddChild(Label(Heading(), (int)(HeadingFont * s), HeadingColor, HorizontalAlignment.Center));

        // The hangar's totals with the campaign's money on hand beside them, error-coloured when
        // over either. The slot is reserved on every screen, so gaining or losing a line never
        // moves the rows under it.
        var totals = _screen == Screen.Hangar ? _hangar : null;
        column.AddChild(Reserved(TotalsAndWallet(totals), (int)(DetailFont * s),
            totals is { TotalsOverweight: true } or { WalletShort: true } ? ErrorColor : DetailColor));

        // The rows in a column of their own so the hangar's art can stand beside them.
        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", (int)(ZoneSeparation * s));
        content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // A windowed screen (the contents list, Controls, Options) draws a slice, and Row keeps
        // taking the ABSOLUTE index. That index is what the cursor comparison and the row text
        // both read. Every other screen draws its whole roster.
        int count = CurrentCount();
        int first = _screen switch
        {
            Screen.Presets => _presetTop,
            Screen.Controls => _controlsTop,
            Screen.Options => _optionsTop,
            _ => 0,
        };
        int last = Math.Min(count, first + DrawnRowCount());
        for (int i = first; i < last; i++)
        {
            var row = Row(i, s);
            Pointable(row, i);
            content.AddChild(row);
        }

        // The wheel between the rows: the column passes what no row took.
        content.GuiInput += ev => PointerEvent(null, ev);

        // C22's art seam: a hangar page may hand the shell one decoded TGA with a caption
        // (blueprint, icon, paint preview), plus a second one for the focused row (E48's decal
        // tile). This block and HangarArtColumn are the whole rendering.
        if (hangarArt != null)
        {
            var beside = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin };
            beside.AddThemeConstantOverride("separation", (int)(14 * s));
            beside.AddChild(HangarArtColumn(hangarArt, s));
            beside.AddChild(content);
            column.AddChild(beside);
        }
        else
        {
            column.AddChild(content);
        }

        // Both status slots are reserved rather than added when they have something to say: a
        // wingman count or a lock that appears mid-screen would otherwise shift the band.
        column.AddChild(Reserved(_screen == Screen.Plane ? WingmenLine() : "",
            (int)(DetailFont * s), DetailColor));
        column.AddChild(Reserved(SelectedLine(), (int)(DetailFont * s), RowLockedColor));
        return column;
    }

    // The footer band: what the focused row is (or why the last press did nothing) and the presses
    // that do something here. Its height is fixed, so the controls line stays on the same pixel.
    private Control FooterColumn(float s)
    {
        var column = Column(ContentWidth * s, s);
        column.AddChild(DetailBlock(s));
        column.AddChild(Label(Footer(), (int)(FooterFont * s), FooterColor, HorizontalAlignment.Center));
        column.AddChild(Spacer((int)(ZonePad * s)));
        return column;
    }

    // The lock line the aircraft screen writes into its second status slot, or "". A lone pilot's
    // lock is invisible in the centred layout otherwise, and an unacknowledged press on a screen
    // that used to launch on it reads as a freeze rather than as a stage.
    private string SelectedLine() =>
        _screen == Screen.Plane && _slots.Count == 1 && _slots[0].Locked
            ? $"✓  {_roster[_slots[0].PlaneIndex].Name} selected"
            : "";

    // Opens the Options screen on the saved options, so each stepper shows back what the player
    // asked for even when the running process resolved a graphics mode from a flag or the config
    // key instead.
    private void OpenOptions()
    {
        _optionsIndex = 0;
        _optionsTop = 0;
        var saved = OptionsStore.UserOptions().Load();
        _difficultyChoice = Difficulty.Parse(saved.Difficulty) ?? Difficulty.Normal;
        _nearestAfterKillChoice = saved.NearestAfterKill;
        _rumbleChoice = saved.Rumble;
        _defaultViewChoice = saved.DefaultView;
        _autoHeadTurnChoice = saved.AutoHeadTurn;
        _graphicsChoice = saved.GraphicsMode ?? GraphicsMode.Default;
        _rocketCratersChoice = saved.RocketCraters;
        _monitorChoice = saved.MonitorIndex;
        _resolutionChoice = saved.Resolution;
        _savedResolution = saved.Resolution;
        _displayModeChoice = saved.DisplayMode;
        _vsyncChoice = saved.VSync;
        _audioMasterChoice = saved.AudioMaster;
        _audioMusicChoice = saved.AudioMusic;
        _audioEffectsChoice = saved.AudioEffects;
        _audioVoiceChoice = saved.AudioVoice;
    }

    // Opens the rebinding screen on the joined seats' live keymaps. Joining is open here (ScanJoins)
    // because Options is reached with seat 0 alone, so a pad pressing Start is the only way another
    // player's keymap is ever on screen.
    private void OpenControls()
    {
        // Before the sync, not after: the aid path opens this screen out of ShowMenu, before any
        // frame has run, and a screen with no registered seat has no keymap to draw.
        SyncSlots();
        SyncControlsSeats();
        PrimeJoins(); // joining opens here, a Start held on the way in must not fire
        _controlsIndex = 0;
        _controlsTop = 0;
        // Cancel first, so the screen opens on what the game is actually playing rather than on a
        // staged edit left behind by an earlier visit, then Discard to clear its status line.
        _controls.Cancel();
        _controls.Discard();
    }

    // Keeps the rebinding screen's player rows in step with the joined seats, once a frame while it
    // is up. The bookkeeping itself is the shared one, so a keymap file belongs to one player
    // number whichever presentation edited it.
    private void SyncControlsSeats()
    {
        _controlsSeats ??= new MenuControlsSeats(_controls);
        var pollers = new List<MenuInput?>(_slots.Count);
        foreach (var slot in _slots)
            pollers.Add(slot.Input);
        _controlsSeats.Sync(pollers);
    }

    // One frame of the Controls screen. A capture in progress swallows the frame: the player is
    // pressing a control to BIND it, so reading the same press as a menu command would move the
    // cursor and confirm a row under them.
    private bool HandleControlsInput(MenuInput p1)
    {
        // First, so a pad that joined this frame already has its row before anything is drawn or
        // stepped onto, and a seat whose pad left has lost one.
        SyncControlsSeats();
        if (_controls.Capturing)
        {
            return _controls.Poll();
        }

        bool dirty = false;
        if (p1.Move != 0)
        {
            _controlsIndex = Wrap(_controlsIndex + p1.Move, CurrentCount());
            SyncControlsCursor();
            dirty = true;
        }

        if (p1.MoveX != 0)
        {
            dirty |= StepControls(p1.MoveX);
        }

        if (p1.Accept)
        {
            AcceptControls(p1);
            dirty = true;
        }
        else if (p1.Back)
        {
            BackFromControls();
            dirty = true;
        }

        return dirty || HandleControlsShortcuts(p1);
    }

    // The two gestures with no row of their own: unbind the highlighted control, and put this
    // seat's whole context back to the shipped keymap.
    private bool HandleControlsShortcuts(MenuInput p1)
    {
        bool dirty = false;
        if (p1.Loadout && IsControlsActionRow(_controlsIndex))
        {
            _controls.UnbindSlot();
            dirty = true;
        }

        if (p1.Presets)
        {
            _controls.ResetSeat();
            dirty = true;
        }

        return dirty;
    }

    private void AcceptControls(MenuInput p1)
    {
        if (_controls.Pending != null)
        {
            _controls.ConfirmSteal();
            return;
        }

        switch (_controlsIndex)
        {
            case ControlsPlayerRow: StepControlsPlayer(1); return;
            case ControlsSensitivityRow: return; // a range, not a wrap: only a sideways step moves it
            case ControlsContextRow: StepControlsContext(1); return;
        }

        switch (ControlsButton(_controlsIndex))
        {
            case 0: _controls.ResetSeat(); break;
            case 1: _controls.Cancel(); break;
            case 2: CommitControls(p1); break;
            default: _controls.BeginCapture(); break;
        }
    }

    // Accepting is where a rebind reaches the live maps, so it is also where the menu poller's
    // typing reading is rebuilt: MenuInput derives its dead-letter aliases from its own keymap.
    private void CommitControls(MenuInput p1)
    {
        _controls.Accept();
        p1.RebindsApplied();
    }

    // Back means the narrowest thing still open: the pending steal, then the capture, then the
    // screen, which it leaves the way CANCEL CHANGES does. Nothing is written on the way out; the
    // Accept row is the only commit.
    private void BackFromControls()
    {
        if (_controls.Pending != null)
        {
            _controls.DiscardSteal();
            return;
        }

        _controls.Cancel();
        _screen = Screen.Options;
    }

    private bool StepControls(int dir)
    {
        if (_controlsIndex == ControlsPlayerRow)
            return StepControlsPlayer(dir);
        if (_controlsIndex == ControlsSensitivityRow)
            return StepControlsSensitivity(dir);
        if (_controlsIndex == ControlsContextRow)
            return StepControlsContext(dir);
        return ControlsButton(_controlsIndex) >= 0 ? false : StepControlsSlot(dir);
    }

    private bool StepControlsPlayer(int dir)
    {
        var players = _controls.Players;
        if (players.Count < 2)
            return false;

        int at = 0;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i] == _controls.Player)
                at = i;
        }

        _controls.Player = players[Wrap(at + dir, players.Count)];
        SyncControlsCursor();
        return true;
    }

    // Steps the Original slider's own scale, so the two presentations land on the same values.
    private bool StepControlsSensitivity(int dir)
    {
        float before = _controls.MouseSensitivity;
        _controls.MouseSensitivity = SensitivityScale.Step(before, dir);
        return _controls.MouseSensitivity != before;
    }

    private bool StepControlsContext(int dir)
    {
        var all = Enum.GetValues<InputContext>();
        int at = Array.IndexOf(all, _controls.Context);
        _controls.Context = all[Wrap(at + dir, all.Length)];
        _controlsIndex = Math.Min(_controlsIndex, CurrentCount() - 1);
        SyncControlsCursor();
        return true;
    }

    private bool StepControlsSlot(int dir)
    {
        _controls.MoveSlot(dir);
        return true;
    }

    // Keeps the window over the cursor and the feature's focused action under it, so the row the
    // player is looking at is the row a capture binds.
    private void SyncControlsCursor()
    {
        int last = Math.Max(0, CurrentCount() - ControlsWindow);
        int top = Math.Clamp(_controlsTop, 0, last);
        if (_controlsIndex < top)
            top = _controlsIndex;
        else if (_controlsIndex >= top + ControlsWindow)
            top = _controlsIndex - ControlsWindow + 1;
        _controlsTop = Math.Clamp(top, 0, last);
        if (IsControlsActionRow(_controlsIndex))
            _controls.Focus(_controlsIndex - ControlsHeaderRows);
    }

    // Whether that row names an action, as against a stepper above the list or a button below it.
    private bool IsControlsActionRow(int index) =>
        index >= ControlsHeaderRows && index < ControlsHeaderRows + _controls.Actions.Count;

    // Which of the three buttons a row past the action list is, in the original's order, or -1.
    private int ControlsButton(int index)
    {
        int at = index - ControlsHeaderRows - _controls.Actions.Count;
        return at >= 0 && at < ControlsFooterRows ? at : -1;
    }

    private string ControlsRowLabel(int index)
    {
        if (index == ControlsPlayerRow)
            return "Player";
        if (index == ControlsSensitivityRow)
            return "Mouse sensitivity";
        if (index == ControlsContextRow)
            return "Control set";
        return ControlsButton(index) switch
        {
            0 => "Reset to default",
            1 => "Cancel changes",
            2 => "Accept changes",
            _ => BindingLabels.Name(_controls.Actions[index - ControlsHeaderRows]),
        };
    }

    // A row's controls, with the highlighted slot marked so the player can see which of several
    // bindings the next capture would replace. The empty slot past the end is what adds one.
    private string ControlsRowValue(int index)
    {
        if (index == ControlsPlayerRow)
            return _controls.Player.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (index == ControlsSensitivityRow)
            return SensitivityScale.Label(_controls.MouseSensitivity);
        if (index == ControlsContextRow)
            return ControlsContextLabel(_controls.Context);
        if (ControlsButton(index) >= 0)
            return _controls.Dirty ? "changed" : string.Empty;

        var action = _controls.Actions[index - ControlsHeaderRows];
        string row = _controls.RowText(action);
        if (index != _controlsIndex)
            return row;

        int count = _controls.Bindings(action).Count;
        return _controls.Slot >= count ? row + "   [add]" : $"{row}   [{_controls.Slot + 1}/{count}]";
    }

    private string ControlsContextLabel(InputContext context) => context switch
    {
        InputContext.Flight => "Flying",
        InputContext.Menu => "Menus and boards",
        _ => "Free camera",
    };

    // The status line the feature wrote, when it has something to say; otherwise what this row is.
    private string ControlsDetail(int focus)
    {
        if (_controls.Status.Length > 0)
            return _controls.Status;
        if (focus == ControlsPlayerRow)
            return "Whose keymap this is. Each seat holds its own, so rebinding here touches nobody else.";
        if (focus == ControlsSensitivityRow)
            return "How fast a flying mouse moves the stick. Higher needs less hand travel for full deflection.";
        if (focus == ControlsContextRow)
            return "Which keymap: one control means different things flying, on a board and in the free camera.";
        return ControlsButton(focus) switch
        {
            0 => "Puts every control set back to the shipped keymap. Cancel still undoes it.",
            1 => "Throws away everything changed here, a reset included.",
            2 => "Writes the changes to this seat's keymap and saves them.",
            _ => "Enter / A rebinds the marked control; ←→ picks which one.",
        };
    }

    private string ControlsFooter()
    {
        if (_controls.Capturing)
            return "Press a control       Esc / B  Cancel";
        if (_controls.Pending != null)
            return "Enter / A  Take it       Esc / B  Leave it alone";
        if (!IsControlsActionRow(_controlsIndex))
            return "↑↓  Choose       ←→  Change       Enter / A  Do it       Esc / B  Back without saving";
        return "↑↓  Choose       ←→  Which control       Enter / A  Rebind"
            + "       L / Y  Unbind       P / X  Defaults       Esc / B  Back without saving";
    }

    // A three-way stepper with wrap, Normal / Hard / Hardest in the campaign selector's order.
    private void StepDifficultyChoice(int dir) =>
        _difficultyChoice = ((Difficulty.Clamp(_difficultyChoice) + dir) % 3 + 3) % 3;

    // A three-way stepper with wrap over PilotView.Selectable, the order the original's own Default
    // View dropdown offers. A never-set view steps from Chase, which is what its absence reads as.
    private void StepDefaultViewChoice(int dir) =>
        _defaultViewChoice = PilotView.Name(PilotView.Step(
            PilotView.Parse(_defaultViewChoice ?? "") ?? PilotViewMode.Chase, dir));

    private string DefaultViewChoiceLabel() =>
        PilotView.Label(PilotView.Parse(_defaultViewChoice ?? "") ?? PilotViewMode.Chase);

    // A two-way toggle over a setting the flight reads three ways: on, off, and never set, which
    // leaves the headLook.autohead config key deciding. A first press turns it on, since the key
    // ships off, so a press has to change something.
    private void ToggleAutoHeadTurnChoice() => _autoHeadTurnChoice = _autoHeadTurnChoice != true;

    private string AutoHeadTurnChoiceLabel() => _autoHeadTurnChoice == true ? "On" : "Off";

    // A two-way toggle. A never-set field steps to on, since off is what its absence already reads
    // as and a first press has to change something.
    private void ToggleNearestAfterKillChoice() =>
        _nearestAfterKillChoice = _nearestAfterKillChoice != true;

    private string NearestAfterKillChoiceLabel() => _nearestAfterKillChoice == true ? "On" : "Off";

    // The same two-way toggle read the other way round. A never-set rumble is ON, the way the
    // original ships force feedback, so a first press has to turn it off.
    private void ToggleRumbleChoice() => _rumbleChoice = _rumbleChoice == false;

    private string RumbleChoiceLabel() => _rumbleChoice != false ? "On" : "Off";

    private void ToggleGraphicsChoice() =>
        _graphicsChoice = _graphicsChoice == GraphicsMode.EnhancedWord
            ? GraphicsMode.Default
            : GraphicsMode.EnhancedWord;

    private string GraphicsChoiceLabel() =>
        _graphicsChoice == GraphicsMode.EnhancedWord ? "Enhanced" : "Original";

    // Read the targeting switch's way round, not the rumble's: never set is OFF, since a carve is
    // the remake's own addition and the original leaves the ground alone (docs/org/craters.md).
    private void ToggleRocketCratersChoice() => _rocketCratersChoice = _rocketCratersChoice != true;

    private string RocketCratersChoiceLabel() => _rocketCratersChoice == true ? "On" : "Off";

    // The monitor and resolution rows ask the engine on every read rather than holding a list from
    // when the screen opened, since a monitor can be plugged in while the row stands focused and the
    // sizes are the standing screen's own. A saved index no screen answers to draws as the screen
    // the window already stands on, MonitorSetting.Resolve's own forgiving read, so the row cannot
    // name a screen the apply would not move the window to.
    private string MonitorChoiceLabel()
    {
        var screens = MonitorSetting.Screens();
        return screens.Labels[MonitorSetting.Resolve(_monitorChoice, screens).Screen];
    }

    private string ResolutionChoiceLabel()
    {
        var sizes = ResolutionSizes();
        return sizes.Words[DisplaySettingRows.ResolutionIndex(sizes, _resolutionChoice, _displayModeChoice)];
    }

    // The sizes the resolution row offers: the standing screen's own list, widened with the size the
    // options file named when the screen opened. A size written in by hand therefore stands in the
    // row where it sorts for as long as the page is open. A step off it can step back onto it, and
    // only a step the player makes replaces it.
    private SizeList ResolutionSizes() => ResolutionSetting.ScreenSizes().Including(_savedResolution);

    private string DisplayModeChoiceLabel() =>
        DisplaySettingRows.DisplayModeLabels[DisplaySettingRows.WordIndex(DisplayWords.DisplayModes, _displayModeChoice, DisplayModeSetting.Default)];

    private string VSyncChoiceLabel() =>
        DisplaySettingRows.VSyncLabels[DisplaySettingRows.WordIndex(DisplayWords.VSyncChoices, _vsyncChoice, VSyncSetting.Default)];

    // The four display steppers. Each writes back the word the options file carries rather than the
    // row's position, since the apply hands the word to the setting's own resolver; a step off a
    // value the machine no longer offers therefore starts from the forgiving read, not from -1.
    private void StepMonitorChoice(int dir)
    {
        var screens = MonitorSetting.Screens();
        int at = MonitorSetting.Resolve(_monitorChoice, screens).Screen;
        _monitorChoice = MonitorSetting.Word(DisplaySettingRows.Step(at, dir, screens.Labels.Count));
    }

    // Dead while the display mode owns the size, which borderless does. The saved size is left
    // where it is rather than overwritten with the screen's. Picking Windowed or Fullscreen again
    // then gives the player back the size they chose.
    private void StepResolutionChoice(int dir)
    {
        if (ResolutionSetting.Pinned(_displayModeChoice))
        {
            return;
        }

        var sizes = ResolutionSizes();
        int at = DisplaySettingRows.ResolutionIndex(sizes, _resolutionChoice, _displayModeChoice);
        _resolutionChoice = sizes.Words[DisplaySettingRows.Step(at, dir, sizes.Words.Count)];
    }

    private void StepDisplayModeChoice(int dir)
    {
        var words = DisplayWords.DisplayModes;
        int at = DisplaySettingRows.WordIndex(words, _displayModeChoice, DisplayModeSetting.Default);
        _displayModeChoice = words[DisplaySettingRows.Step(at, dir, words.Count)];
    }

    private void StepVSyncChoice(int dir)
    {
        var words = DisplayWords.VSyncChoices;
        int at = DisplaySettingRows.WordIndex(words, _vsyncChoice, VSyncSetting.Default);
        _vsyncChoice = words[DisplaySettingRows.Step(at, dir, words.Count)];
    }

    // The size row's detail says what the size does under the mode standing with it. The size does
    // something different in each mode, and under borderless the row does not step at all. A
    // stepper that refuses without saying why reads as a broken row.
    private string ResolutionDetail()
    {
        if (ResolutionSetting.Pinned(_displayModeChoice))
        {
            return "Borderless runs at the desktop's own size. Pick Windowed or Fullscreen to choose one.";
        }

        return _displayModeChoice == DisplayWords.Fullscreen
            ? "Select the size the game draws at, scaled up to fill the fullscreen window."
            : "Select the window size. The list is what the screen the window stands on can hold.";
    }

    // The graphics row's detail says whether a restart is still owed: the mode is resolved once at
    // launch, so a choice that differs from the running one reaches the world on the next start,
    // and a player who saved it and came back would otherwise read the unchanged world as a
    // failed switch.
    private string GraphicsDetail()
    {
        bool running = GraphicsMode.Enhanced;
        bool chosen = _graphicsChoice == GraphicsMode.EnhancedWord;
        return chosen == running
            ? "Original is the faithful world; Enhanced lights it. Takes effect on the next start."
            : $"Original is the faithful world; Enhanced lights it. This run is {(running ? "Enhanced" : "Original")}; restart to apply.";
    }

    // What this screen is, the middle band's first line.
    private string Heading()
    {
        return _screen switch
        {
            Screen.Mode => "SELECT MODE",
            Screen.Chapter => MatchRowCount > 0 ? "SELECT MAP AND MATCH RULES" : "SELECT MAP",
            // The window shows 14 of 19, so the position has to be on screen somewhere or the
            // list looks like it ends where the window does.
            Screen.Presets => $"TABLE OF CONTENTS  ({_presetCursor + 1}/{CurrentCount()})",
            Screen.Environment => "SELECT ENVIRONMENT",
            Screen.MissionType => "SELECT MISSION",
            Screen.Waves => "CONFIGURE WAVES",
            Screen.WaveEdit => $"WAVE {_waveEditIndex + 1}",
            Screen.Wingmen => "WINGMEN",
            Screen.WingmanLoadout => $"WINGMEN: AMMO SELECTION  ({_ia.WingmanPlane.Name})",
            Screen.Hangar => _hangar?.Page.Title ?? HangarRow,
            Screen.Campaign => _campaign?.Page.Title ?? CampaignRow,
            Screen.Options => $"OPTIONS  ({_optionsIndex + 1}/{CurrentCount()})",
            Screen.Controls => $"CONTROLS  ({_controlsIndex + 1}/{CurrentCount()})",
            Screen.Network => _coopWait ? CoopDoorText.WaitingHeading : "MULTIPLAYER",
            _ when _slots.Count == 1 && _slots[0].InLoadout =>
                $"AMMO SELECTION  ({_roster[_slots[0].PlaneIndex].Name})",
            _ when _slots.Count == 1 && _slots[0].Locked => "AIRCRAFT SELECTED",
            _ => _slots.Count > 1 ? "SELECT AIRCRAFT: ALL PLAYERS" : "SELECT AIRCRAFT",
        };
    }

    // One campaign screen as its composed board. The error line rides the detail slot, which is
    // where a refusal has to appear on a screen with no error row of its own.
    private void RebuildBoard()
    {
        if (_campaign is not { } flow)
        {
            return;
        }

        // A board draws no join strip, so the strip text is recorded here rather than left at
        // whatever the last centred screen wrote. Otherwise _Process's own strip comparison never
        // settles on a campaign screen, and a board repaints for a reason that is not its own.
        _stripText = JoinStripText();
        RebuildChipStrip();
        var page = flow.Page;
        int row = flow.Row;
        string detail = _error.Length > 0 ? _error : page.Detail(row);
        // A block of text is the screen's own to place; the one-line hint band takes only what a
        // screen has nowhere else to put, which is every short description and every refusal.
        bool banded = !CampaignBoards.DetailPaned(page, row, flow.Layout) && !detail.Contains('\n');
        _boardRoot.Show(
            CampaignBoards.For(page, row, _pressFrames > 0, detail, flow.Modal, flow.Layout),
            BoardPalette.For(page.Screen),
            banded ? detail : string.Empty,
            CampaignFooter(flow));
    }

    // A page's own footer, with the network press named on the cabin. The press works on every
    // board past the roster. The cabin is where a mission is chosen, and other footers are full.
    private string CampaignFooter(CampaignFlow flow) =>
        _net != null && flow.Screen == CampaignScreen.Cabin && flow.Modal == null
            ? $"{flow.Page.Footer}       {CoopDoorText.TogglePress}"
            : flow.Page.Footer;

    // The campaign chip strip content is built only while the board is up; visibility
    // itself is Rebuild's, off the same slot count, so a Back that drops the last guest hides the
    // strip on the same frame instead of leaving one stale chip behind.
    private void RebuildChipStrip()
    {
        foreach (var c in _chipStrip.GetChildren())
            c.QueueFree();
        var chips = ChipTexts();
        string band = NetBand();
        if (chips.Count <= 1 && band.Length == 0)
        {
            return;
        }

        var size = GetViewport().GetVisibleRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        float inset = fit.Length(SeatStrip.Inset);
        _chipStrip.OffsetRight = -inset;
        _chipStrip.OffsetLeft = _chipStrip.OffsetRight;
        _chipStrip.OffsetTop = inset;
        _chipStrip.OffsetBottom = _chipStrip.OffsetTop;
        _chipStrip.AddThemeConstantOverride("separation", Mathf.RoundToInt(fit.Length(ChipSeparation)));
        int font = Mathf.RoundToInt(fit.Length(SeatStrip.Font));
        // The band sits in the footer's neutral ink, so the address reads as the room's state
        // rather than as one more player.
        _netBandLabel.Text = band;
        _netBandLabel.AddThemeFontSizeOverride("font_size", font);
        _netBandLabel.AddThemeColorOverride("font_color", FooterColor);
        _netBandLabel.OffsetRight = -inset;
        _netBandLabel.OffsetLeft = _netBandLabel.OffsetRight;
        _netBandLabel.OffsetTop = inset + (font * NetBandLineStep);
        _netBandLabel.OffsetBottom = _netBandLabel.OffsetTop;

        // A solo board with the door open shows its lone chip too, so the count starts at one.
        for (int i = 0; i < chips.Count; i++)
        {
            _chipStrip.AddChild(Label(chips[i], font, SplitScreen.PlayerColor(i), HorizontalAlignment.Center));
        }
    }

    // The splitscreen aircraft select: one panel per player in that player's pane of the
    // screen (the same SplitScreen.PaneRect geometry the flight panes use), plus a
    // shared bottom strip carrying the breadcrumb, the join strip and the controls line. Each
    // panel shows the player's tag + device, the full aircraft roster with their own cursor, the
    // focused plane's stats, and their lock state, the panel border lights up in the player's
    // colour once locked, which is the at-a-glance "who are we waiting for".
    private void RebuildPanes()
    {
        foreach (var c in _paneRoot.GetChildren())
            c.QueueFree();

        var size = GetViewport().GetVisibleRect().Size;
        float s = Mathf.Max(1f, size.Y / 720f);
        // The strip's own Instant Action line (decision 8a's flown-wingmen re-clamp) is one more
        // row than StripHeightFrac was tuned for, grow the fixed band by a line's worth so it
        // does not push the controls line off the bottom of a centred, unclipped VBoxContainer.
        bool wingmenLine = WingmenLine().Length > 0;
        float stripH = size.Y * StripHeightFrac + (wingmenLine ? (FooterFont + 6) * s : 0f);
        var paneArea = new Vector2(size.X, Mathf.Max(1f, size.Y - stripH));

        for (int i = 0; i < _slots.Count; i++)
        {
            // ⚠ The axis comes from the WINDOW, not from `paneArea`: the bottom strip leaves this
            // area proportionally wider than the flight's, and reading the axis off it would pick
            // your aircraft in a left/right pane and then fly you in a top/bottom one.
            var rect = SplitScreen.PaneRect(i, _slots.Count, paneArea, SplitScreen.SideBySide(size));
            var panel = new PanelContainer
            {
                Position = rect.Position,
                Size = rect.Size,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            var color = SplitScreen.PlayerColor(i);
            bool locked = _slots[i].Locked;
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = locked ? new Color(color, 0.10f) : new Color(0.06f, 0.07f, 0.10f, 0.92f),
                BorderColor = locked ? color : new Color(color, 0.45f),
                BorderWidthLeft = (int)(2 * s),
                BorderWidthRight = (int)(2 * s),
                BorderWidthTop = (int)(2 * s),
                BorderWidthBottom = (int)(2 * s),
                ContentMarginLeft = PanePad * s,
                ContentMarginRight = PanePad * s,
                ContentMarginTop = PanePad * s,
                ContentMarginBottom = PanePad * s,
            });
            panel.AddChild(PaneBody(i, rect.Size - Vector2.One * (2f * PanePad * s), s));
            _paneRoot.AddChild(panel);
        }

        // The shared strip: what everyone already chose, who is in, and the controls.
        var strip = new VBoxContainer
        {
            Position = new Vector2(0f, size.Y - stripH),
            Size = new Vector2(size.X, stripH),
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        strip.AddChild(Label(Breadcrumb(), (int)(CrumbFont * s), CrumbColor, HorizontalAlignment.Center));
        strip.AddChild(JoinStrip(s));
        // RebuildPanes only ever draws the Plane screen (Rebuild's own split check), so the
        // flown-wingmen re-clamp (decision 8a) applies unconditionally here, same as the centred
        // layout's own Plane-screen-only check.
        if (wingmenLine)
            strip.AddChild(Label(WingmenLine(), (int)(FooterFont * s), DetailColor, HorizontalAlignment.Center));
        strip.AddChild(Label(
            "↑↓  Choose       Enter / A  Select, again to fly       L / Y  Weapons       Esc / B  Back",
            (int)(FooterFont * s), FooterColor, HorizontalAlignment.Center));
        if (_error.Length > 0)
            strip.AddChild(Label(_error, (int)(ErrorFont * s), ErrorColor, HorizontalAlignment.Center));
        _paneRoot.AddChild(strip);
    }

    // One player's panel contents. The roster is the full list, it fits, because the
    // font scale is derived from the pane's own height rather than the window's (a 4P quarter
    // pane and a 2P half pane are the same height, so both land on the same size).
    private Control PaneBody(int player, Vector2 inner, float s)
    {
        var slot = _slots[player];
        var color = SplitScreen.PlayerColor(player);
        var font = MenuFont;

        // Fit the roster + header + stats + status into the pane's height.
        float paneScale = s;
        if (font != null)
        {
            float refH = font.GetHeight(CrumbFont)                       // the player/device header
                       + _roster.Count * font.GetHeight(RowFont)         // the roster
                       + font.GetHeight(DetailFont)                      // stats
                       + font.GetHeight(FooterFont)                      // lock status
                       + 4 * 4;                                          // separations
            paneScale = Mathf.Min(s, inner.Y / refH);
        }

        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", (int)(2 * paneScale));

        box.AddChild(Label($"{SplitScreen.PlayerTag(player)}   {slot.Input.DeviceLabel}",
            (int)(CrumbFont * paneScale), color, HorizontalAlignment.Center));

        // This pane swaps to its own loadout list in place, so the players beside it keep
        // browsing untouched, and nobody can launch while somebody is still in here.
        if (slot.InLoadout)
        {
            var fitRows = FitRowsFor(FitFor(slot.PlaneIndex), slot.Fit);
            for (int i = 0; i < fitRows.Count; i++)
            {
                bool selected = i == slot.FitRow;
                var fitRow = FitRowControl(fitRows, i, (int)(RowFont * paneScale),
                    selected ? color : RowColor, selected);
                if (player == 0)
                    Pointable(fitRow, i);
                box.AddChild(fitRow);
            }

            box.AddChild(Label(_roster[slot.PlaneIndex].Name, (int)(DetailFont * paneScale),
                DetailColor, HorizontalAlignment.Center));
            box.AddChild(Label("←→ change    B done", (int)(FooterFont * paneScale),
                FooterColor, HorizontalAlignment.Center));
            return box;
        }

        // The mouse is seat 0's device, so only player 1's pane takes it.
        for (int i = 0; i < _roster.Count; i++)
        {
            bool sel = i == slot.PlaneIndex;
            var row = CursorRow.Build(_roster[i].Name, (int)(RowFont * paneScale), sel ? color : RowColor, sel);
            if (player == 0)
                Pointable(row, i);
            box.AddChild(row);
        }

        if (player == 0)
            box.GuiInput += ev => PointerEvent(null, ev);

        box.AddChild(Label(PlaneStat(_roster[slot.PlaneIndex].Node), (int)(DetailFont * paneScale),
            DetailColor, HorizontalAlignment.Center));
        box.AddChild(Label(
            slot.Confirmed ? "✓  READY" : slot.Locked ? "A again to fly    Y weapons" : "choosing…",
            (int)(FooterFont * paneScale),
            slot.Locked ? color : FooterColor, HorizontalAlignment.Center));
        return box;
    }

    // The three bands for the window being drawn into, measured at the 720p metrics and scaled
    // together. Only the middle term depends on the screen: every slot in the other two is drawn
    // whether or not it has anything to say, so the bands keep their height as the pilot moves.
    private MenuZones Zones()
    {
        // CanvasLayer is a Node (not a CanvasItem), so read the size off the Viewport directly.
        float viewH = GetViewport().GetVisibleRect().Size.Y;
        var font = MenuFont;
        if (font == null)
        {
            return MenuZones.For(viewH, 0f, 0f, 0f);
        }

        float rowsH = DrawnRowCount() * (font.GetHeight(RowFont) + ZoneSeparation);
        // The hangar art column stands beside the rows, so it only adds height where it is taller
        // than the rows it sits next to, not on top of them.
        float artH = PageArt() != null
            ? Mathf.Max(0f, HangarArtHeight + HangarRowArtHeight + (2 * font.GetHeight(FooterFont)) +
                12 - rowsH)
            : 0f;
        float header = ZonePad + font.GetHeight(TitleFont) + font.GetHeight(CrumbFont) +
            font.GetHeight(FooterFont) + (3 * ZoneSeparation);
        float middle = ZonePad + font.GetHeight(HeadingFont) + (3 * font.GetHeight(DetailFont)) +
            rowsH + artH + (5 * ZoneSeparation);
        float footer = (DetailReserveLines * font.GetHeight(DetailFont)) +
            font.GetHeight(FooterFont) + ZonePad + (2 * ZoneSeparation);
        return MenuZones.For(viewH, header, middle, footer);
    }

    // How many rows the middle band actually draws. A windowed screen differs from its item count,
    // and budgeting for every item would shrink the band for rows it does not draw.
    private int DrawnRowCount() => _screen switch
    {
        Screen.Presets => Math.Min(CurrentCount(), PresetWindow),
        Screen.Controls => Math.Min(CurrentCount(), ControlsWindow),
        Screen.Options => Math.Min(CurrentCount(), OptionsWindow),
        _ => CurrentCount(),
    };

    // One line at a font size, the height a reserved slot keeps whatever it holds.
    private float LineHeight(int fontSize) => MenuFont?.GetHeight(fontSize) ?? 0f;

    // A label whose slot is drawn whether or not it has anything to say, so what appears in it
    // never moves the rows above or the controls below.
    private Control Reserved(string text, int fontSize, Color color)
    {
        var label = Label(text, fontSize, color, HorizontalAlignment.Center);
        label.CustomMinimumSize = new Vector2(0f, LineHeight(fontSize));
        return label;
    }

    // The one reserved line over a hangar screen: the totals, then the wallet where a campaign funds
    // the build, either alone when the other is empty.
    private string TotalsAndWallet(HangarFlow? flow)
    {
        if (flow == null || _screen != Screen.Hangar)
        {
            return "";
        }

        string totals = flow.TotalsLine;
        string wallet = flow.WalletLine;
        return totals.Length > 0 && wallet.Length > 0 ? totals + "      " + wallet : totals + wallet;
    }

    // The fit behind a roster row, or null when the table has no def flying that model. A custom
    // row stands on its own build over that airframe's stock def. Its Ammo Selection list is the
    // pylons it bought, not the airframe's. Wingman indices land here too: wingmen
    // are stock-only, and the roster's first eleven rows ARE the stock table in its order.
    private LoadoutDef? FitFor(int planeIndex)
    {
        var pick = _roster[planeIndex];
        var stock = Fits.ForModel(pick.Node);
        if (stock == null || pick.CustomName is not { } saved)
        {
            return stock;
        }

        foreach (var build in _customDefs)
        {
            if (string.Equals(build.Name, saved, StringComparison.Ordinal))
            {
                return CustomPlaneBuild.LoadoutFor(build, stock);
            }
        }

        return stock;
    }


    // One aeroplane's loadout list: a row per firable gun slot, a row per pylon it hangs, then
    // reset. Turret slots are left out while they are built inert, an ammo pick there would change
    // nothing that can be fired.
    private List<FitRow> FitRowsFor(LoadoutDef? def, LoadoutChoice fit)
    {
        var rows = new List<FitRow>();
        if (def == null)
        {
            rows.Add(new FitRow(FitRowKind.Reset, 0, "Reset to stock", ""));
            return rows;
        }

        foreach (var gun in def.Guns)
        {
            if (gun.Turret)
            {
                continue;
            }
            string id = fit.GunAmmoFor(gun.Slot) ?? gun.Ammo;
            rows.Add(new FitRow(FitRowKind.Gun, gun.Slot, $".{gun.Caliber}-cal.", LabelFor(Fits.Options.GunAmmo, id)));
        }

        var hp = def.Hardpoints;
        foreach (int pylon in AmmoPylons(def))
        {
            int entry = Array.IndexOf(Loadout.PylonFillOrder, pylon);
            string id = fit.PylonFor(pylon)
                ?? (hp != null && entry >= 0 && entry < hp.Stock.Length ? hp.Stock[entry] : LoadoutChoice.None);
            rows.Add(new FitRow(FitRowKind.Pylon, pylon, $"Pylon {pylon}", LabelFor(Fits.Options.PylonOrdnance, id)));
        }

        rows.Add(new FitRow(FitRowKind.Reset, 0, "Reset to stock", ""));
        return rows;
    }

    // An id's dropdown label, falling back to the id so an unoffered stock value (a def naming
    // something the screen does not list) is visible rather than blank.
    private string LabelFor(IReadOnlyList<LoadoutOption> options, string id)
    {
        foreach (var option in options)
        {
            if (string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return option.Label;
            }
        }
        return id;
    }

    // The stepper on one fit row: walk the row's own roster from wherever it sits now. A value
    // the roster does not carry starts the walk at the first entry rather than refusing to move.
    private void StepFit(LoadoutDef? def, LoadoutChoice fit, FitRow row, int dir)
    {
        var options = row.Kind == FitRowKind.Gun ? Fits.Options.GunAmmo : Fits.Options.PylonOrdnance;
        if (def == null || options.Count == 0 || row.Kind == FitRowKind.Reset)
        {
            return;
        }

        string current = row.Kind == FitRowKind.Gun
            ? fit.GunAmmoFor(row.Key) ?? StockGunAmmo(def, row.Key)
            : fit.PylonFor(row.Key) ?? StockPylon(def, row.Key);
        int at = 0;
        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Id, current, StringComparison.OrdinalIgnoreCase))
            {
                at = i;
                break;
            }
        }

        string picked = options[Wrap(at + dir, options.Count)].Id;
        if (row.Kind == FitRowKind.Gun)
        {
            fit.SetGunAmmo(row.Key, picked);
        }
        else
        {
            fit.SetPylon(row.Key, picked);
        }
    }

    private string StockGunAmmo(LoadoutDef def, int slot)
    {
        foreach (var gun in def.Guns)
        {
            if (gun.Slot == slot)
            {
                return gun.Ammo;
            }
        }
        return "slug";
    }

    private string StockPylon(LoadoutDef def, int pylon)
    {
        var hp = def.Hardpoints;
        for (int i = 0; hp != null && i < hp.Count && i < Loadout.PylonFillOrder.Length; i++)
        {
            if (Loadout.PylonFillOrder[i] == pylon)
            {
                return i < hp.Stock.Length ? hp.Stock[i] : LoadoutChoice.None;
            }
        }
        return LoadoutChoice.None;
    }

    private int CurrentCount() => _screen switch
    {
        Screen.Mode => Modes.Length + 4, // + the campaign, hangar, options and multiplayer rows
        Screen.Network => _coopWait ? 1 : NetworkRows,
        Screen.Hangar => _hangar?.Page.RowCount ?? 1,
        Screen.Campaign => _campaign?.Page.RowCount ?? 1,
        Screen.Options => OptionsStepperRows + 2, // + the controls door and the apply row
        Screen.Controls => ControlsHeaderRows + _controls.Actions.Count + ControlsFooterRows,
        Screen.Chapter => CurrentChapters.Length + MatchRowCount,
        Screen.Presets => InstantActionPresets.All.Count,
        Screen.Environment => InstantActionFeature.Environments.Count,
        Screen.MissionType => CurrentMissionTypes.Count,
        Screen.Waves => InstantActionFeature.WaveSlots + 1, // + the trailing "Continue" row
        Screen.WaveEdit => 4, // Enemies / Militia / Aircraft / Skill
        Screen.Wingmen => WingmenRowCount,
        _ => CentredFitRows()?.Count ?? PlaneRowCount,
    };

    // One centred list row with a ▶ cursor, the item-4 layout, used by every screen
    // the centred body draws. A multi-player aircraft screen never comes through here: it splits
    // into per-player panes instead (RebuildPanes).
    private Control Row(int index, float s)
    {
        if (CentredFitRows() is { } fitRows)
        {
            bool focused = index == CurrentIndex;
            return FitRowControl(fitRows, index, (int)(RowFont * s),
                focused ? RowFocusColor : RowColor, focused);
        }

        if (_screen == Screen.Controls)
        {
            bool onRow = index == _controlsIndex;
            return CursorRow.BuildColumns(ControlsRowLabel(index), ControlsRowValue(index),
                ControlsLabelEms * RowFont * s, ControlsValueEms * RowFont * s, (int)(RowFont * s),
                onRow ? RowFocusColor : RowColor, onRow);
        }

        string text = RowText(index);
        bool sel = index == CurrentIndex;
        // A locked single-player pick recolours its row, because the centred layout has no
        // per-pane status line to carry the state the way the splitscreen panes do.
        bool locked = _screen == Screen.Plane && _slots.Count == 1 && _slots[0].Locked;
        var colour = sel ? locked ? RowLockedColor : RowFocusColor : RowColor;
        return CursorRow.Build(text, (int)(RowFont * s), colour, sel);
    }

    // The text on one row of the screen showing; a fit list's row reads as its mount label.
    private string RowText(int index)
    {
        if (CentredFitRows() is { } fitRows)
        {
            return fitRows[index].Label;
        }

        return _screen switch
        {
            Screen.Mode => index < Modes.Length ? Modes[index].Label
                : index == Modes.Length ? CampaignRow
                : index == Modes.Length + 1 ? HangarRow
                : index == Modes.Length + 2 ? OptionsRow : NetworkRow,
            Screen.Network => NetworkRowText(index),
            Screen.Hangar => _hangar?.RowText(index) ?? "",
            Screen.Campaign => _campaign?.Page.RowText(index) ?? "",
            Screen.Options => index switch
            {
                0 => $"Difficulty: {Difficulty.Label(_difficultyChoice)}",
                1 => $"Default View: {DefaultViewChoiceLabel()}",
                2 => $"Auto Head Turn: {AutoHeadTurnChoiceLabel()}",
                3 => $"Nearest target after a kill: {NearestAfterKillChoiceLabel()}",
                4 => $"Controller rumble: {RumbleChoiceLabel()}",
                5 => $"Graphics: {GraphicsChoiceLabel()}",
                6 => $"Rocket craters: {RocketCratersChoiceLabel()}",
                7 => $"Monitor: {MonitorChoiceLabel()}",
                8 => $"Resolution: {ResolutionChoiceLabel()}",
                9 => $"Display mode: {DisplayModeChoiceLabel()}",
                10 => $"V-Sync: {VSyncChoiceLabel()}",
                11 => $"Master volume: {LevelLabel(_audioMasterChoice, AudioMix.DefaultMaster)}",
                12 => $"Music volume: {LevelLabel(_audioMusicChoice, AudioMix.DefaultMusic)}",
                13 => $"Effects volume: {LevelLabel(_audioEffectsChoice, AudioMix.DefaultEffects)}",
                14 => $"Voice volume: {LevelLabel(_audioVoiceChoice, AudioMix.DefaultVoice)}",
                15 => ControlsRow,
                _ => "Apply and restart the menu",
            },
            Screen.Controls => $"{ControlsRowLabel(index)}   {ControlsRowValue(index)}",
            Screen.Chapter => index < CurrentChapters.Length
                ? CurrentChapters[index].Name
                : MatchRowText(index - CurrentChapters.Length),
            Screen.Presets => InstantActionPresets.All[index].Name,
            Screen.Environment => InstantActionFeature.Environments[index].Name,
            Screen.MissionType => CurrentMissionTypes[index].Label,
            Screen.Waves => WaveListRowText(index),
            Screen.WaveEdit => WaveFieldRowText(index),
            Screen.Wingmen => WingmenFieldRowText(index),
            _ => index < _roster.Count ? _roster[index].Name : HangarRow,
        };
    }

    // One Dogfight match row: the kill target, then the match clock. A 0 on either reads as no
    // limit, the meaning VersusMatch and the two flags already give it. A match with neither
    // limit set runs until somebody leaves.
    private string MatchRowText(int row) => row == 0
        ? $"Kill target     {LimitLabel(_setup.KillTarget, "")}"
        : $"Time limit      {LimitLabel(_setup.TimeLimitMinutes, " min")}";

    // One multiplayer-door row. The two fields read as fields whether or not a socket is open.
    // The two openers name what they will do rather than what the door is doing, which the
    // status line under them says instead.
    // A guest linked to a campaign host is not walking on to a map, so its way on says where it
    // does go. The waiting board's one row replaces all five.
    private string NetworkRowText(int index) => _coopWait ? CoopDoorText.LeaveRow : index switch
    {
        NetPortRow => $"Port            {(_net?.Port ?? NetPlayFeature.DefaultPort).ToString(CultureInfo.InvariantCulture)}",
        NetAddressRow => $"Address         {_net?.Address ?? NetPlayFeature.DefaultAddress}",
        NetHostRow => "Host a match",
        NetJoinRow => "Join that address",
        _ when _net is { } net && net.IsCoopGuest => CoopDoorText.WaitRow,
        _ => "Continue → Map",
    };

    // The line under the door's rows: what the socket is doing, who is on it, and what the
    // router said. This is the whole readout, so a player who cannot fly can see why.
    private string NetworkStatus()
    {
        if (_net is not { } net)
        {
            return "No network carrier is registered in this build.";
        }

        if (_coopWait)
        {
            return CoopDoorText.WaitingStatus(net, MissionName);
        }

        string link = net.Link is { } state ? $", link {state.ToString().ToLowerInvariant()}" : "";
        string mapped = net.PortMap is { } map
            ? map.IsMapped
                ? $". Router mapped port {map.Port.ToString(CultureInfo.InvariantCulture)}, reachable at {map.ExternalAddress}"
                : $". Router did not map the port ({map.Outcome.ToString().ToLowerInvariant()}); guests on this network still join"
            : "";
        return net.Stage switch
        {
            NetDoorStage.Hosting =>
                $"Hosting on port {net.Port.ToString(CultureInfo.InvariantCulture)}{link}, {net.Peers.ToString(CultureInfo.InvariantCulture)} joined{mapped}",
            NetDoorStage.Joining => $"Joining {net.Address}:{net.Port.ToString(CultureInfo.InvariantCulture)}{link}",
            NetDoorStage.Joined => CoopDoorText.JoinedStatus(net, link, MissionName),
            NetDoorStage.Failed => $"That did not open: {net.Fault}",
            _ => "Host a match, or type an address and join one. The host picks the map.",
        };
    }

    // One Waves-screen row: an unconfigured slot reads "empty" (decision 1's own "starts
    // empty" wizard, not the original's always-four dropdowns), a configured one summarises its
    // count/militia/aircraft/skill, and the trailing row advances to Wingmen.
    private string WaveListRowText(int index)
    {
        if (index == InstantActionFeature.WaveSlots)
            return "Continue → Wingmen";
        var w = _ia.Waves[index];
        var militia = InstantActionFeature.Militias[w.MilitiaIndex];
        string summary = w.Count == 0
            ? "empty"
            : $"{w.Count}x {militia.Name} {militia.Aircraft[w.AircraftIndex]} ({Cap(InstantActionFeature.Skills[w.SkillIndex])})";
        return $"Wave {index + 1}: {summary}";
    }

    // One WaveEdit-screen field row: the label plus the field's own current value, since
    // this screen has no separate detail area, HandleMoveX edits whichever one the
    // cursor sits on.
    private string WaveFieldRowText(int index)
    {
        var w = _ia.Waves[_waveEditIndex];
        var militia = InstantActionFeature.Militias[w.MilitiaIndex];
        return index switch
        {
            0 => $"Enemies         {w.Count}",
            1 => $"Militia         {militia.Name}",
            2 => $"Aircraft        {militia.Aircraft[w.AircraftIndex]}",
            _ => $"Skill           {Cap(InstantActionFeature.Skills[w.SkillIndex])}",
        };
    }

    // One Wingmen-screen field row, the Aircraft row (index 1) only ever draws while
    // it exists (WingmenRowCount is 1 at 0 wingmen), matching the decoded setup
    // screen's own hidden-at-zero control.
    private string WingmenFieldRowText(int index) => index switch
    {
        0 => $"Wingmen         {_ia.NumWingmen}",
        _ => $"Aircraft        {_ia.WingmanPlane.Name}",
    };

    // The detail area: one stats line for the focused entry, autowrapped. The hangar's
    // airframe-defaults ask (string 206) is a two-sentence question, and an unwrapped label makes
    // the centred body as wide as the sentence, which runs off a 16:9 screen (E45). A Label reports
    // a minimum width of 1 once autowrap is on, so it takes the column's width rather than setting
    // it, and every other screen's one-liner is unaffected.
    private Control DetailBlock(float s)
    {
        int size = (int)(DetailFont * s);
        // A refusal takes the description's own slot, the way a campaign board's hint band takes
        // it: it is the one line on the screen that says why the last press did nothing.
        bool refused = _error.Length > 0;
        var label = Label(refused ? _error : Detail(CurrentIndex), size,
            refused ? ErrorColor : DetailColor, HorizontalAlignment.Center);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        // Top-aligned in a slot of its own: a one-line description starts on the same pixel as a
        // two-line one, and a longer one grows up into the band rather than moving the controls.
        label.VerticalAlignment = VerticalAlignment.Top;
        label.CustomMinimumSize = new Vector2(0f, DetailReserveLines * LineHeight(size));
        return label;
    }

    // The picture the current page asks the shell to draw beside its rows, or null. Hangar and
    // campaign pages share the one art column, so a page needs no change here to use it.
    private HangarArt? PageArt() => _screen switch
    {
        Screen.Hangar => _hangar?.Page.Art,
        Screen.Campaign => _campaign?.Page.Art,
        _ => null,
    };

    // The focused row's own smaller picture, same seam.
    private HangarArt? PageRowArt() => _screen switch
    {
        Screen.Hangar => _hangar is { } hangar ? hangar.Page.RowArt(hangar.Row) : null,
        Screen.Campaign => _campaign is { } campaign ? campaign.Page.RowArt(campaign.Row) : null,
        _ => null,
    };

    // The art column a hangar page may request: the page's picture over the focused
    // row's own, both captioned. Each texture is rebuilt only when the page hands over a different
    // decoded image, since Rebuild runs on every keypress.
    private Control HangarArtColumn(HangarArt art, float s)
    {
        var box = new VBoxContainer();
        box.CustomMinimumSize = new Vector2(HangarArtWidth * s, 0f);
        box.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        box.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        AddArt(box, art, HangarArtHeight * s, ref _hangarArtSource, ref _hangarArtTexture, s);
        if (PageRowArt() is { } rowArt)
        {
            box.AddChild(Spacer((int)(6 * s)));
            AddArt(box, rowArt, HangarRowArtHeight * s, ref _hangarRowArtSource,
                ref _hangarRowArtTexture, s);
        }

        return box;
    }

    // One captioned picture into a column, reusing the caller's cached texture when the page hands
    // over the same decoded image again.
    private void AddArt(Control box, HangarArt art, float height, ref TgaImage? source,
        ref ImageTexture? texture, float s)
    {
        if (!ReferenceEquals(source, art.Image))
        {
            var image = Image.CreateFromData(art.Image.Width, art.Image.Height, false,
                Image.Format.Rgba8, art.Image.Rgba);
            texture = ImageTexture.CreateFromImage(image);
            source = art.Image;
        }

        var rect = new TextureRect
        {
            Texture = texture,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            CustomMinimumSize = new Vector2(0, height),
        };
        rect.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        box.AddChild(rect);
        var caption = Label(art.Caption, (int)(FooterFont * s), DetailColor, HorizontalAlignment.Center);
        caption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(caption);
    }

    // The join strip shown under the breadcrumb on every screen: who is in, on what
    // device, plus the hint that free pads can join with Start.
    private Control JoinStrip(float s)
    {
        _stripText = JoinStripText();
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", (int)(14 * s));
        for (int i = 0; i < _slots.Count; i++)
        {
            var label = Label($"{SplitScreen.PlayerTag(i)}  {_slots[i].Input.DeviceLabel}",
                (int)(FooterFont * s), SplitScreen.PlayerColor(i), HorizontalAlignment.Center);
            label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            row.AddChild(label);
        }
        var hint = Label(JoinHint(), (int)(FooterFont * s), FooterColor, HorizontalAlignment.Center);
        hint.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        row.AddChild(hint);
        return row;
    }

    // The strip as plain text, compared each frame so a hotplug (or a join) redraws
    // even when nothing was pressed.
    private string JoinStripText()
    {
        var parts = new List<string>(_slots.Count + 1);
        for (int i = 0; i < _slots.Count; i++)
            parts.Add($"{SplitScreen.PlayerTag(i)} {_slots[i].Input.DeviceLabel}");
        parts.Add(JoinHint());
        return string.Join(" | ", parts);
    }

    // The hint beside the join strip. Joining happens on the aircraft screen and on the rebinding
    // screen, so the other screens say where it will be rather than inviting a press that does
    // nothing. The rebinding screen names what the press is for there, since a pad joins to be
    // rebound rather than to fly. Dogfight below 2 players gets its own line, CanLaunch is
    // withholding the launch gesture, so the generic "you may join" hint would undersell what is
    // actually blocking it.
    private string JoinHint()
    {
        if (_slots.Count >= SplitScreen.MaxPlayers)
            return $"({SplitScreen.MaxPlayers}-player maximum)";
        if (_screen == Screen.Controls)
            return Pads.Connected().Count > 0
                ? "(press START on a free pad to edit its own keymap)"
                : "(connect a pad and press START to edit its own keymap)";
        if (_screen != Screen.Plane)
            return "(other players join at aircraft select)";
        // A networked Dogfight is already a fight, so it asks for nobody: the opponent is at
        // another machine and no press here would seat them.
        if (_mode == MenuMode.Versus && _slots.Count < 2 && !Networked())
            return $"(Dogfight needs a fight, {SplitScreen.PlayerTag(_slots.Count)}: press START to join)";
        return Pads.Connected().Count > 0
            ? "(press START on a free pad to join)"
            : "(connect a pad and press START to join)";
    }

    private string Footer()
    {
        if (CentredFitRows() != null)
        {
            return "↑↓  Choose mount       ←→  Change       L / Y or Esc / B  Done";
        }

        if (_screen == Screen.Campaign)
        {
            // The campaign's pages name their own presses: a screen with an armed text field has a
            // different control set from the same screen with the cursor on its list.
            return _campaign is { } flow ? CampaignFooter(flow) : "Esc / B  Back";
        }

        if (_screen == Screen.Hangar)
        {
            // Name the presses this hangar screen actually has: the two pick screens select on
            // Enter and have no stepper at all (E49), and the defaults ask is two answers.
            if (_hangar?.DefaultsAsk != null)
            {
                return "↑↓  Choose       Enter / A  Answer       Esc / B  Back";
            }

            if (_hangar?.Screen == HangarScreen.PlaneSelection)
            {
                return "↑↓  Choose       Enter / A  Select       Esc / B  Back";
            }

            if (_hangar?.Screen == HangarScreen.Name)
            {
                // W/A/S/D are dead here (MenuInput.TextEntry), so the arrows are named alone.
                return "↑↓  Choose       ←→  Change word       Type / Backspace  Rename"
                    + "       Enter / A  Continue       Esc / B  Back";
            }

            return _hangar?.Screen is HangarScreen.Airframe or HangarScreen.Engine
                ? "↑↓  Choose       Enter / A  Select, again to continue       Esc / B  Back"
                : "↑↓  Choose       ←→  Change       Enter / A  Continue       Esc / B  Back";
        }

        if (_screen == Screen.Controls)
        {
            return ControlsFooter();
        }

        string back = _screen == Screen.Mode ? "Esc / B  Quit" : "Esc / B  Back";
        string who = _slots.Count > 1 ? "       (P1 chooses)" : "";
        string nav = _screen switch
        {
            Screen.MissionType => "↑↓  Choose mission       ←→  Lives",
            Screen.WaveEdit or Screen.Wingmen or Screen.Options => "↑↓  Choose field       ←→  Change",
            // W/A/S/D are dead on the address row (MenuInput.TextEntry), so the arrows are named
            // alone, as the hangar's own name screen names them.
            Screen.Network when _coopWait => "↑↓  Navigate",
            Screen.Network => "↑↓  Choose row       ←→  Port       Type / Backspace  Address",
            // Dogfight's map screen carries the two match rows, whose stepper is an unbound axis
            // nobody can guess at. Free Flight's map screen has nothing sideways and says so.
            Screen.Chapter when MatchRowCount > 0 => "↑↓  Choose map or rule       ←→  Change",
            _ => "↑↓  Navigate",
        };
        // The loadout is an unbound face button, so it is invisible unless the footer says so.
        // Named only where it does something: a lone pilot at aircraft select, and the wingmen
        // step once there are wingmen to arm.
        string fit = _screen == Screen.Plane || (_screen == Screen.Wingmen && _ia.NumWingmen > 0)
            ? "       L / Y  Weapons"
            : "";
        // Same rule for the contents list, and the same reason: an unbound face button nobody can
        // guess at. Named on step 1, the one screen it opens from.
        string presets = _screen == Screen.Environment ? "       P / X  Scenarios" : "";
        // Name the press that is actually next. Before the lock that is "select"; after it, "fly"
        //, a footer still offering "select" on an already-selected plane is why the second press
        // was not obvious in the first place.
        string select = _screen != Screen.Plane ? "Enter / A  Select"
            : _slots.Count == 1 && _slots[0].Locked ? "Enter / A  FLY"
            : "Enter / A  Select";
        return $"{nav}       {select}{fit}{presets}       {back}{who}";
    }

    private string Breadcrumb()
    {
        string mode = Modes[(int)_mode].Label;
        return _screen switch
        {
            Screen.Mode => "Mode  ›  Map  ›  Aircraft",
            Screen.Hangar => $"{HangarRow}  ›  {_hangar?.Page.Title}",
            Screen.Campaign => $"{CampaignRow}  ›  {_campaign?.Page.Title}",
            Screen.Options => OptionsRow,
            Screen.Controls => $"{OptionsRow}  ›  Controls  ›  Player {_controls.Player}",
            Screen.Network when _coopWait => $"{NetworkRow}  ›  Campaign co-op  ›  Waiting for the host",
            Screen.Network => $"{NetworkRow}  ›  Map  ›  Aircraft",
            Screen.Chapter => $"{mode}  ›  Map  ›  Aircraft",
            Screen.Presets => $"{mode}  ›  Table of Contents",
            Screen.Environment => $"{mode}{PresetCrumb()}  ›  Environment  ›  Mission  ›  Aircraft",
            Screen.MissionType => $"{mode}{PresetCrumb()}  ›  {_ia.Environment.Name}  ›  Mission  ›  Aircraft",
            Screen.Waves or Screen.WaveEdit =>
                $"{mode}{PresetCrumb()}  ›  {_ia.Environment.Name}  ›  {_ia.MissionType.Label}  ›  Waves  ›  Aircraft",
            Screen.Wingmen or Screen.WingmanLoadout =>
                $"{mode}{PresetCrumb()}  ›  {_ia.Environment.Name}  ›  {_ia.MissionType.Label}  ›  Wingmen  ›  Aircraft",
            _ when _mode == MenuMode.Stunt =>
                $"{mode}{PresetCrumb()}  ›  {_ia.Environment.Name}  ›  {_ia.MissionType.Label}  ›  Aircraft",
            _ => $"{mode}  ›  {CurrentChapters[_chapterIndex].Name}  ›  Aircraft",
        };
    }

    // The applied preset's name, as the configuration page's own heading crumb, the original's
    // View Story formats it through IDS_IA_STORYTITLE, whose whole text is `%1!s!`, and nothing
    // clears it when a dropdown is then changed by hand. Empty on the custom path.
    private string PresetCrumb() =>
        _ia.PresetIndex >= 0 ? $"  ›  {InstantActionPresets.All[_ia.PresetIndex].Name}" : "";

    private string Detail(int focus) => _screen switch
    {
        Screen.Mode => focus < Modes.Length ? Modes[focus].Detail
            : focus == Modes.Length ? "Fly the story: pick a player, then the cabin."
            : focus == Modes.Length + 1 ? "Build a plane in the hangar and fly it."
            : focus == Modes.Length + 2
                ? "Choose the difficulty, the graphics mode, the display settings and the volume levels."
                : "Host a Dogfight over the network, or join a Dogfight or a campaign by address.",
        Screen.Network => NetworkStatus(),
        Screen.Hangar => _hangar?.Page.Detail(focus) ?? "",
        Screen.Campaign => _campaign?.Page.Detail(focus) ?? "",
        // One arm per row of the Options screen, in the order RowText writes them. A row that lost
        // its arm would take the one under it, and every row below would read one line wrong. The
        // two lists stay the same length.
        Screen.Options => focus switch
        {
            0 => "Select the difficulty level for a solo campaign. Enemy armour and health scale with it at spawn.",
            1 => "Select your default view. A --view= on the command line still outranks it.",
            2 => "Select to turn your head automatically as your aircraft turns. Cockpit views only.",
            3 => "Take the nearest target after a kill instead of the first of the list.",
            4 => "Rumble the gamepad for guns, launches, hits, the nitro and a dive past the rated maximum.",
            5 => GraphicsDetail(),
            6 => "Let a rocket's ground burst dig a crater and flatten what stood in it. The original digs none.",
            7 => "Select the monitor the game opens on. Applied on the way out, before the size.",
            8 => ResolutionDetail(),
            9 => "Select how the window sits on the screen. Borderless leaves the desktop beneath it.",
            10 => "Select the frame pacing. On follows the screen; off runs free, or to a frame cap.",
            11 => "Set the overall volume of all sounds. Heard once the choices are applied.",
            12 => "Set the volume of the in-game music. Heard once the choices are applied.",
            13 => "Set the volume of the sound effects. Heard once the choices are applied.",
            14 => "Set the volume of the voices. Heard once the choices are applied.",
            15 => "Rebind any control, per player. Saved on the way out; the shipped keymap is one press away.",
            _ => "Saves every choice and restarts the menu at its top level; unfinished setup is discarded.",
        },
        Screen.Controls => ControlsDetail(focus),
        Screen.Presets => PresetDetail(focus),
        Screen.Chapter => focus < CurrentChapters.Length
            ? $"Region {CurrentChapters[focus].Code}"
            : focus == CurrentChapters.Length
                ? "←→  how many kills end the match; no limit leaves the clock to end it."
                : "←→  how long the match runs; no limit leaves the kill target to end it.",
        Screen.Environment => $"Region {InstantActionFeature.Environments[focus].Code}",
        Screen.MissionType => LivesDetail(),
        Screen.Waves => focus == InstantActionFeature.WaveSlots ? "Enter / A  on to the wingmen" : "Enter / A  edit a wave",
        Screen.WaveEdit or Screen.Wingmen => "←→  change",
        // Blank: the footer already names the steppers, and a second copy of "←→ change" directly
        // over it reads as two different controls rather than one.
        _ when CentredFitRows() != null => "",
        _ => focus < _roster.Count
            ? PlaneStat(_roster[focus].Node)
            : "Build a plane in the hangar and fly it.",
    };

    // The focused preset's own line: what picking it would fill the wizard with. The enemy total
    // is the sum of its waves, which is 0 for the five ace presets, a duel, not an empty mission.
    private string PresetDetail(int focus)
    {
        var preset = InstantActionPresets.All[focus];
        int enemies = 0;
        foreach (var wave in preset.Waves)
            enemies += wave.Count;
        string label = Mech3.InstantAction.MissionTypeLabel(preset.MissionType);
        string wingmen = preset.NumWingmen == 0 ? "alone" : $"{preset.NumWingmen} wingmen";
        return $"{label} over {preset.Environment}   ·   {preset.PlayerPlane}, {wingmen}   ·   {enemies} enemies";
    }

    // The lives stepper's own line, shown where the other screens show the focused row's
    // stat/region, it is not per-row, so it does not vary with the mission-type cursor.
    private string LivesDetail() =>
        $"Lives   {InstantActionFeature.LivesLabel(_ia.Lives)}        ◀ ▶  change";

    // The Plane screen's own Instant Action line: the flown-wingmen re-clamp (decision
    // 8a, InstantActionRuntime.FlownWingmen) against the CURRENT joined-player count
    //, recomputed every Rebuild, so it tracks a pilot joining live. Empty outside Instant Action
    // or at 0 configured wingmen, which is what lets the caller skip the row entirely rather than
    // draw a blank one.
    private string WingmenLine()
    {
        int wingmen = _ia.NumWingmen;
        if (_mode != MenuMode.Stunt || wingmen == 0)
            return "";
        int flown = InstantActionRuntime.FlownWingmen(wingmen, _slots.Count);
        return flown == wingmen
            ? $"Wingmen  {wingmen}"
            : $"Wingmen  {flown} of {wingmen} configured (flight capped at 6)";
    }

    // A couple of stats for the focused plane, loaded lazily from vehicle.json and cached
    // (null = load failed, shown as unavailable, never blocks the menu). fd_speed → mph is the
    // validated top-speed figure (see PlaneStats).
    private string PlaneStat(string node)
    {
        var s = StatsFor(node);
        if (s == null)
            return "(stats unavailable)";
        return $"Top Speed  {Mph(s)} mph        Weight  {s.VehWeight:0}";
    }

    // Just the top speed, the compact form used in the per-player pick lines.
    private string PlaneSpeed(string node)
    {
        var s = StatsFor(node);
        return s == null ? "stats n/a" : $"{Mph(s)} mph";
    }

    private PlaneStats? StatsFor(string node)
    {
        if (!_stats.TryGetValue(node, out var s))
        {
            try { s = PlaneStats.Load(_zrdrPath, node); }
            catch (Exception e) { Log.Info("ui", $"launchscreen: no stats for {node}: {e.Message}"); s = null; }
            _stats[node] = s;
        }
        return s;
    }

    // One editable line of a loadout list. Key is the gun slot (1-4) or the physical pylon
    // number (1-8), slot identity, the same key LoadoutChoice uses, never a row index.
    private readonly record struct FitRow(FitRowKind Kind, int Key, string Label, string Value);

    private readonly record struct Choice(string Label, string Detail);

    // This screen's view of one joined seat: the shared seat (cursor, stages, fit), the poller
    // behind it (the device label, the campaign's guest check) and its last frame of commands.
    private sealed class Slot
    {
        public readonly PlayerSeat Seat;
        public readonly MenuInput Input;
        public MenuCommands Frame = MenuCommands.None;

        public Slot(PlayerSeat seat, MenuInput input)
        {
            Seat = seat;
            Input = input;
        }

        // Starts at Planes's index 0, the Autogyro under the 3700 order, matching gui_continue's
        // own no-custom-planes selection rather than being positional by accident.
        public int PlaneIndex
        {
            get => Seat.Cursor;
            set => Seat.Cursor = value;
        }

        // Browsing → Locked → Confirmed, moved only through the setup's operations.
        public bool Locked => Seat.Locked;

        public bool Confirmed => Seat.Confirmed;

        // This pane is showing its loadout list instead of the roster. Per seat, so one player
        // arming cannot pull anybody else out of browsing.
        public bool InLoadout => Seat.InLoadout;

        public int FitRow
        {
            get => Seat.FitRow;
            set => Seat.FitRow = value;
        }

        public LoadoutChoice Fit => Seat.Fit;
    }
}
