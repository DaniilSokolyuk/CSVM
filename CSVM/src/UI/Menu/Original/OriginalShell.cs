using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Screens;

namespace CSVM.UI.Menu.Original;

/// <summary>The Original presentation's screens. The top level, the Instant Action screen, the
/// campaign's screens and the hangar's screens are decoded. The others are remake-only screens
/// composed in the decoded chrome's conventions. The campaign's members run from
/// <see cref="CampaignRoster"/> to <see cref="CampaignScrapbookZoom"/> and the hangar's sit last,
/// from <see cref="PlaneName"/> on, which is what the shell reads its two branches off.</summary>
public enum OriginalScreen
{
    /// <summary>The main menu: the decoded <c>[@MainMenu@]</c> rows plus the Free Flight and Dogfight doors.</summary>
    TopLevel,

    /// <summary>The remake-only Free Flight screen: a chapter list, the aircraft list, the seats, BACK and FLY.</summary>
    FreeFlight,

    /// <summary>The remake-only Dogfight screen: the Free Flight screen's shape over the Dogfight gate.</summary>
    Dogfight,

    /// <summary>The remake-only per-seat aircraft screen: the campaign plane-selection board's
    /// shape over the sortie roster, one joined seat picking at a time.</summary>
    SeatPlane,

    /// <summary>The Options screen: the decoded <c>[@Preferences@]</c> page, its GAME OPTIONS,
    /// AUDIO and VIDEO doors live and its CONTROLS door drawn disabled.</summary>
    Options,

    /// <summary>The decoded <c>[@GameOptions@]</c> page: the shared options in its authored row
    /// shape, with ACCEPT CHANGES and CANCEL CHANGES under them.</summary>
    GameOptions,

    /// <summary>The decoded <c>[@Audio@]</c> page: the four volume levels as sliders in its
    /// authored row shape, with ACCEPT CHANGES and CANCEL CHANGES under them.</summary>
    Audio,

    /// <summary>The decoded <c>[@Video@]</c> page: the display settings in its authored row shape,
    /// with ACCEPT CHANGES and CANCEL CHANGES beside them.</summary>
    Video,

    /// <summary>The decoded <c>[@ControlsPrefs@]</c> page: the seat whose keymap is edited, the
    /// KEYS AND BUTTONS door, and ACCEPT CHANGES and CANCEL CHANGES under them.</summary>
    ControlsPrefs,

    /// <summary>The decoded <c>[@Keys@]</c> page: the seven category tabs, the action list in its
    /// two control columns, RESET TO DEFAULT and the exit pair.</summary>
    Keys,

    /// <summary>The decoded <c>[@Credits@]</c> screen: the background pane the credit names are
    /// painted into, ABOUT drawn disabled and the DONE plaque.</summary>
    Credits,

    /// <summary>The remake-only join board, the one screen a pad signs onto a seat from
    /// (<see cref="OriginalJoinBoard"/>).</summary>
    JoinBoard,

    /// <summary>The decoded <c>[@InstantAction@]</c> setup screen: the Table of Contents, the
    /// dropdowns, the paged enemy rows, the radio pair and its buttons.</summary>
    InstantAction,

    /// <summary>The Instant Action screen's Weapon Loadout, the decoded <c>[@OrdinanceLayout@]</c>
    /// chrome over the fit of the seat the radio pair names.</summary>
    InstantActionLoadout,

    /// <summary>The decoded <c>[@IA_WrapUp@]</c> page: one ended Instant Action mission's final
    /// numbers on the notepad, with CONTINUE back to the Instant Action screen.</summary>
    InstantActionWrapup,

    /// <summary>The Multiplayer Connection page: the LAN TCP/IP and Internet ways, the IP Address
    /// box, Host, Connect and Exit Multiplayer, drawn from the multiplayer scripts' placements.
    /// </summary>
    Connection,

    /// <summary>The games list a LAN Connect opens, with the Searching box over it until the
    /// first answer.</summary>
    ConnectionGames,

    /// <summary>The Multiplayer Lobby a Dogfight's host and guests share before its launch: the
    /// player list, the four tabs, Ready, the chat and Leave Game.</summary>
    Lobby,

    /// <summary>The decoded <c>[@Campaign@]</c> player profile screen: the name box, the roster,
    /// CONTINUE, DELETE PLAYER and CANCEL.</summary>
    CampaignRoster,

    /// <summary>The decoded <c>[@PassengerCabin@]</c> hub.</summary>
    CampaignCabin,

    /// <summary>The decoded <c>[@MomentoSelection@]</c> chooser, CHANGE MEMENTO's destination.</summary>
    CampaignMemento,

    /// <summary>The decoded <c>[@ScrapBook_TOC@]</c> table of contents, PREVIOUS MISSIONS' destination.</summary>
    CampaignPreviousMissions,

    /// <summary>The briefing dialog, <c>Briefing.zrd</c>'s own chrome over the mission's reveal.</summary>
    CampaignBriefing,

    /// <summary>The decoded <c>[@FlightCheck@]</c> screen, one check per joined human.</summary>
    CampaignFlightCheck,

    /// <summary>The decoded <c>[@OrdinanceLayout@]</c> ammo selection.</summary>
    CampaignAmmo,

    /// <summary>The decoded <c>[@PlaneSelection@]</c> screen.</summary>
    CampaignPlaneSelection,

    /// <summary>The decoded <c>[@ScrapBook@]</c> spread, the mission end's destination.</summary>
    CampaignScrapbook,

    /// <summary>The decoded <c>[@ScrapbookZoom@]</c> detail view of one scrap.</summary>
    CampaignScrapbookZoom,

    /// <summary>The decoded <c>[@PlaneName@]</c> screen: the edit box, the defaults box, OK and Cancel.</summary>
    PlaneName,

    /// <summary>The Plane Construction hub with the <c>[@AirFrame@]</c> tab on its page.</summary>
    HangarAirframe,

    /// <summary>The hub with the <c>[@Engine@]</c> tab.</summary>
    HangarEngine,

    /// <summary>The hub with the <c>[@Armor@]</c> tab.</summary>
    HangarArmor,

    /// <summary>The hub with the <c>[@Guns@]</c> tab.</summary>
    HangarGuns,

    /// <summary>The hub with the <c>[@HardPoints@]</c> tab.</summary>
    HangarHardpoints,

    /// <summary>The hub with the <c>[@Paint@]</c> tab.</summary>
    HangarPaint,

    /// <summary>The hub with the <c>[@Purchase@]</c> totals page, READY TO PURCHASE's destination.</summary>
    HangarPurchase,

    /// <summary>The <c>[@Hangar@]</c> inventory, SELL PLANES' destination.</summary>
    HangarInventory,
}

/// <summary>How a row draws and reacts.</summary>
public enum OriginalRowKind
{
    /// <summary>A decoded button strip: the words are painted in, the state is the frame drawn.</summary>
    Button,

    /// <summary>A plaque with a label written over it in the four state colours.</summary>
    TextButton,

    /// <summary>One entry of a text list.</summary>
    ListRow,

    /// <summary>A decoded dropdown: its box shows the picked value, Accept opens its list, a
    /// sideways step picks the next value.</summary>
    Dropdown,

    /// <summary>One button of a decoded radio pair, or a checkbox, drawn from an eight-state strip.</summary>
    Radio,

    /// <summary>A decoded edit box: its label is the text typed so far, and the seat's typed
    /// characters feed it while its screen shows.</summary>
    TextField,

    /// <summary>A decoded slider: a thumb held and moved along a slot, or stepped sideways, over
    /// the whole numbers its track spans. The one continuous control the shell has.</summary>
    Slider,
}

/// <summary>One slider carried by its row, for the pointer's hold-and-move and the sideways step.
/// It is the track the thumb runs on, the value it stands at, and the write that puts it somewhere
/// else. The slot art stands under it, the row's own art being the thumb, the one piece that moves.
/// A page declares one of these and the shell needs to know nothing else about the setting behind
/// it. That is the same bargain <see cref="OriginalList"/> strikes for a scrolled list.</summary>
public sealed record OriginalSlider(SliderTrack Track, int Value, Action<int> SetValue, BoardArt? Slot);

/// <summary>One interactive element of a screen in authored 800x600 pixels. It carries what it is,
/// where it is, whether it reacts, and which column it belongs to for the seat's cursor. It also
/// carries whether it is on screen, and the slider it carries when it is one. A list row outside
/// its window keeps its place for the keyboard, unseen and unhit.</summary>
public sealed record OriginalRow(
    string Key, string Label, OriginalRowKind Kind, float X, float Y, float Width, float Height,
    bool Enabled, int Column, BoardArt? Art, bool Visible = true, OriginalSlider? Slider = null)
{
    /// <summary>Whether an authored point lies on the row.</summary>
    public bool Contains(float x, float y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>What one frame of input did: the cues to play, the exit to take if any, and whether
/// the picture changed.</summary>
public sealed record OriginalStep(IReadOnlyList<string> Cues, MenuExit? Exit, bool Changed);

/// <summary>One scrolling list on the screen showing, for the pointer's wheel and thumb drag. It
/// is the window as the list widget describes it, and the write that puts the window's first row
/// somewhere else. That write also pulls the focus inside the window when it stood on a row the
/// move would hide. The arrows and the keyboard never go through this.</summary>
public sealed record OriginalList(string Key, ListWindow Window, Action<int> ScrollTo);

/// <summary>The colours the shell writes in, read off the layout: the file-wide four state
/// colours and the paper plaque's own label tail.</summary>
public sealed record OriginalInks(
    MenuLayoutColor Disabled, MenuLayoutColor Active, MenuLayoutColor Rollover, MenuLayoutColor Depressed,
    MenuLayoutColor LabelNormal, MenuLayoutColor LabelRollover, MenuLayoutColor LabelDepressed);

/// <summary>The colours the Options screen and the pages behind its doors write in, read off
/// <c>[@Preferences@]</c>. They are its description rows' authored text colour and its title's,
/// which <c>[@GameOptions@]</c> and <c>[@Video@]</c> repeat row for row.</summary>
public sealed record OriginalPreferencesInks(MenuLayoutColor Text, MenuLayoutColor Title);

/// <summary>
/// The Original presentation's screen graph, engine-free, driven by each seat's semantic commands
/// and composed into a <see cref="ComposedBoard"/> in the authored 800x600 space. It holds the
/// decoded top level with the remake-only Free Flight and Dogfight doors, the sortie screens, and
/// the Options screen over the Preferences chrome. The Game Options, AUDIO and VIDEO pages stand
/// behind its three live doors, the Instant Action, loadout, campaign and hangar screens over
/// their shared features. Seat 0's pointer arrives mapped into that space, and hovering a live
/// row moves the focus onto it: keyboard, pad and pointer share one cursor. A dialog (the
/// original's messagebox) can stand over any screen, and while one does its answers are the only
/// rows. Every rectangle and art name comes from the layout, the art's pixel size from the
/// injected measurer; each screen family is its own partial file.
/// </summary>
public sealed partial class OriginalShell : IOriginalScreenHost
{
    /// <summary>The Campaign row's key on the top level.</summary>
    public const string CampaignKey = "MM_B_CAMPAIGN";

    /// <summary>The Multiplayer row's key on the top level, the Connection page's door.</summary>
    public const string MultiplayerKey = "MM_B_MULTIPLAYER";

    /// <summary>The Free Flight door's key on the top level.</summary>
    public const string FreeFlightKey = "FREEFLIGHT";

    /// <summary>The join board's door on the top level.</summary>
    public const string JoinBoardKey = "JOINBOARD";

    /// <summary>The Free Flight screen's leave button.</summary>
    public const string BackKey = "BACK";

    /// <summary>The Free Flight screen's launch button.</summary>
    public const string FlyKey = "FLY";

    /// <summary>The Options screen's section in the layout, whose chrome it is composed over.</summary>
    public const string PreferencesSection = "Preferences";

    /// <summary>The Options screen's way back, <c>[@Preferences@]</c>'s own RETURN TO MAIN MENU.</summary>
    public const string OptionsBackKey = "PF_B_MAINMENU";

    /// <summary>The Preferences page's four page doors, in their authored order, onto the Game
    /// Options, AUDIO, VIDEO and CONTROLS pages. The fourth draws disabled only where no shared
    /// <see cref="ControlsFeature"/> stands behind it.</summary>
    public static readonly string[] PreferencesPageKeys =
    {
        OriginalOptionsScreen.GameOptionsDoorKey, OriginalOptionsScreen.AudioDoorKey,
        OriginalOptionsScreen.VideoDoorKey, OriginalOptionsScreen.ControlsDoorKey,
    };

    private const float PreferencesTitleFont = 20f;
    private const float PreferencesTextFont = 14f;

    // The Free Flight door beside the button frame, level with the frame's first row. The frame
    // column is full, so the door stands in the clear left margin at the row pitch's height.
    private const float DoorX = 42f;
    private const float DoorY = 293f;

    // The join board's door, a third plaque under the two sortie doors.
    private const float JoinBoardDoorY = DoorY + 72f;

    // The remake-only screens' list geometry. It is two columns under the logo, one authored text
    // height (STDTEXTH, 16) plus air per row, and the two plaques on the bottom margin.
    private const float ListTop = 286f;
    private const float RowPitch = 22f;
    private const float RowHeight = 20f;
    private const float ListWidth = 300f;
    private const float LeftColumnX = 60f;
    private const float RightColumnX = 440f;
    private const float PlaqueY = 540f;
    private const float RowFont = 16f;
    private const float HeadingFont = 20f;
    private const float FooterY = 578f;
    private const float FooterFont = 12f;
    private const float OptionsX = 319f;
    private const float OptionsTop = 300f;
    private const float OptionsPitch = 40f;

    // A plaque's size when its art cannot be measured, so the row still has a rectangle.
    private const float FallbackPlaqueWidth = 162f;
    private const float FallbackPlaqueHeight = 28f;
    private const float FallbackButtonWidth = 220f;
    private const float FallbackButtonHeight = 42f;

    // A dropdown's arrow size where its own art cannot be measured, and the font its value is
    // written in. The numbers are the Instant Action screen's own, borrowed by the plate pages.
    private const float FallbackArrowWidth = 15f;
    private const float FallbackArrowHeight = 14f;
    private const float PlateItemFont = 13f;

    // The layout key every screen that authors a background movie spells it under, [@FinalCinema@]
    // and [@CampaignIntro@] excepted. Those two are cinemas rather than screens with one behind them.
    private const string MovieKey = "MOVIE";

    // Which section's background movie a screen composes. Save and Load author a row of their own
    // and take an entry here whenever Original composes them, needing nothing else.
    // ⚠ The five Preferences leaves author no movie row and still run the page's own behind them.
    // Every leaf still of the film shows that. They take the Preferences row for the same reason
    // they take its logo (docs/org/menu-inventory.md).
    private static readonly IReadOnlyDictionary<OriginalScreen, string> MovieSections =
        new Dictionary<OriginalScreen, string>
        {
            [OriginalScreen.TopLevel] = OriginalAvailability.MainMenuSection,
            [OriginalScreen.Options] = PreferencesSection,
            [OriginalScreen.GameOptions] = PreferencesSection,
            [OriginalScreen.Audio] = PreferencesSection,
            [OriginalScreen.Video] = PreferencesSection,
            [OriginalScreen.ControlsPrefs] = PreferencesSection,
            [OriginalScreen.Keys] = PreferencesSection,
        };

    private static readonly string[] TopLevelButtons =
    {
        "MM_B_CAMPAIGN", "MM_B_INSTANTACTION", "MM_B_MULTIPLAYER", "MM_B_PREFERENCES", "MM_B_CREDITS", "MM_B_QUIT",
    };

    private readonly MenuLayout _layout;
    private readonly FreeFlightFeature _free;
    private readonly PlayerSetupFeature _setup;
    private readonly Func<string, (int Width, int Height)?> _measure;
    private readonly Func<PlayerSeat, IReadOnlyList<int>> _flightDevices;
    private readonly IReadOnlyList<OriginalChapter> _chapters;
    private readonly BoardArt? _plaque;
    private readonly BoardArt _activePointer;
    private readonly BoardArt _passivePointer;
    private readonly ControlsFeature? _controls;
    private readonly CSVM.Flight.Hangar.CustomPlaneStore? _planes;
    // The stock loadouts reader, shared. The campaign opens over it, the Instant Action module
    // holds it, and the per-seat aircraft screen reads a stock fit's ratings off it.
    private readonly Func<CSVM.Flight.Weapons.StockLoadouts?>? _stock;
    private readonly CampaignLayout _campaignLayout;
    private readonly InstantActionFeature _instantAction;
    // The screen modules this shell stands over, each asked which screens it owns. One dispatch
    // lookup (ModuleFor) replaces a field and a screen-range check per family. A further module is
    // one more entry here and one more typed accessor.
    private readonly IReadOnlyList<IOriginalScreenModule> _modules;
    private readonly SliderControl _slider = new();
    private readonly CinemaFilm _film = new();
    private readonly int[] _focus = new int[Enum.GetValues<OriginalScreen>().Length];
    private readonly Dictionary<string, (int Width, int Height)?> _sizes = new(StringComparer.OrdinalIgnoreCase);

    // The network door, replaceable so a screenshot aid or a suite can stand in its own.
    private NetPlayFeature? _net;
    private OriginalScreen _screen;
    private int _hover = -1;
    private int _pressed = -1;
    // The key of the row a press landed on. A press activates nothing. The row draws its held
    // frame while the button is down and fires only when the button comes up still on it. A press
    // released anywhere else fires nothing.
    private string? _armed;
    // Which bitmap the pointer wears. It answers an enter or a leave and is recomputed only when
    // the pointer moves. A screen drawn under a still pointer keeps the bitmap it arrived with.
    private bool _pointerLive;
    private (float X, float Y)? _pointer;
    // A thumb drag in progress: which list, where the pointer took hold and where the window stood.
    private (string Key, float StartY, int StartTop)? _drag;
    private int _pickedChapter = -1;

    /// <summary>A shell over <paramref name="layout"/> and the shared features. The measurer
    /// <paramref name="measure"/> answers an art name with its strip's pixel size, null when the file is
    /// missing, and <paramref name="flightDevices"/> a seat with its launch's devices. The chapters
    /// default to <see cref="OriginalRosters"/>, a missing Instant Action feature to a private one.
    /// Instant Action's Build Custom Plane needs <paramref name="hangar"/> and <paramref name="planes"/>
    /// together, the Campaign row <paramref name="campaign"/> and <paramref name="profiles"/>.</summary>
    public OriginalShell(
        MenuLayout layout,
        FreeFlightFeature free,
        PlayerSetupFeature setup,
        Func<string, (int Width, int Height)?> measure,
        Func<PlayerSeat, IReadOnlyList<int>>? flightDevices = null,
        IReadOnlyList<OriginalChapter>? chapters = null,
        InstantActionFeature? instantAction = null,
        HangarFeature? hangar = null,
        CSVM.Flight.Hangar.CustomPlaneStore? planes = null,
        CampaignFeature? campaign = null,
        Func<CSVM.Session.Campaign.CampaignProfileStore>? profiles = null,
        Func<CSVM.Flight.Weapons.StockLoadouts?>? stock = null,
        string? dataRoot = null,
        // Reads the saved options the Options screen shows back; null opens it on the defaults,
        // which is what an engine-free test wants. The shell never writes them.
        Func<CSVM.Utils.OptionsDef>? options = null,
        // Reads the sizes the window's own screen can hold, the resolution row's words and the size
        // a saved one the screen lacks falls back to. Null offers every candidate size, there being
        // no screen to ask without an engine.
        Func<CSVM.Utils.SizeList>? screenSizes = null,
        // Reads the screens the machine has, the monitor row's words and the screen a saved index
        // that names none falls back to. Null offers the one screen an engine-free caller can.
        Func<CSVM.Utils.ScreenList>? screens = null,
        // The shared rebinding feature the CONTROLS door stands over; null draws that door
        // disabled and leaves the two pages behind it unreachable.
        ControlsFeature? controls = null,
        // The pad roster the join board signs pads onto; null draws four open entries and answers
        // no gesture, which is what an engine-free test sees.
        IJoinRoster? joinRoster = null,
        // The network door the Multiplayer plaque and the cabin's Host Co-op stand over; null
        // draws the plaque disabled and hides the cabin's button.
        NetPlayFeature? net = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _free = free ?? throw new ArgumentNullException(nameof(free));
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _measure = measure ?? throw new ArgumentNullException(nameof(measure));
        _flightDevices = flightDevices ?? (_ => Array.Empty<int>());
        _chapters = chapters ?? OriginalRosters.Chapters;
        _instantAction = instantAction ?? new InstantActionFeature(_ => Mech3.InstantAction.Defaults());
        _planes = planes;
        _stock = stock;
        _controls = controls;
        _net = net;
        _campaignLayout = CampaignLayout.Over(layout);
        InstantAction = new OriginalInstantActionScreen(_instantAction, _setup, planes, layout, measure, this, _stock);
        Options = new OriginalOptionsScreen(layout, this, options, screenSizes, screens, controls);
        Campaign = new OriginalCampaignScreen(
            campaign, _setup, planes, _campaignLayout, this, profiles, _stock, _flightDevices, dataRoot, () => _net);
        Hangar = hangar != null ? new OriginalHangarScreen(hangar, planes, layout, measure, this) : null;
        Wrapup = new OriginalWrapupScreen(_campaignLayout, measure, this, InstantAction.OpenInstantAction);
        JoinBoard = new OriginalJoinBoard(layout, this, joinRoster);
        Lobby = new OriginalLobbyScreen(
            () => _net, this, dataRoot, _stock,
            () => _setup.Seats.Count > 0 ? _flightDevices(_setup.Seats[0]) : Array.Empty<int>(),
            () => profiles?.Invoke().LastPlayedPilotName);
        Connection = new OriginalConnectionScreen(() => _net, this, dataRoot, Lobby.OpenHost);
        _modules = Hangar != null
            ? new IOriginalScreenModule[] { InstantAction, Options, Campaign, Hangar, Wrapup, JoinBoard, Connection, Lobby }
            : new IOriginalScreenModule[] { InstantAction, Options, Campaign, Wrapup, JoinBoard, Connection, Lobby };
        var plaqueRow = layout.Screen("FlightCheck")?.Widget("FC_B_CHANGEPLANE");
        _plaque = plaqueRow is { Art.Count: > 0 } ? new BoardArt(BoardArtLibrary.Ui, plaqueRow.Art[0], plaqueRow.Frames) : null;
        Inks = ReadInks(layout, plaqueRow);
        PreferencesInks = ReadPreferencesInks(layout, Inks);
        _activePointer = new BoardArt(BoardArtLibrary.Ui, PointerArt(layout, "activepointerz.png"));
        _passivePointer = new BoardArt(BoardArtLibrary.Ui, PointerArt(layout, "passivepointerz.png"));
        for (int i = 0; i < _focus.Length; i++)
        {
            _focus[i] = -1;
        }
    }

    /// <summary>The screen showing.</summary>
    public OriginalScreen Screen => _screen;

    /// <summary>The colours the shell writes in.</summary>
    public OriginalInks Inks { get; }

    /// <summary>The colours the Options screen and the Game Options page write in.</summary>
    public OriginalPreferencesInks PreferencesInks { get; }

    /// <summary>The current screen's rows, in focus order: a standing dialog's answers alone,
    /// else the screen's own.</summary>
    public IReadOnlyList<OriginalRow> Rows => _dialog != null ? DialogRows() : BuildRows();

    /// <summary>The focused row's index into <see cref="Rows"/>, or -1 when nothing can take focus.</summary>
    public int Focus => EnsureFocus(Rows);

    /// <summary>The focused row's key, or "".</summary>
    public string FocusedKey
    {
        get
        {
            var rows = Rows;
            int focus = EnsureFocus(rows);
            return focus >= 0 ? rows[focus].Key : string.Empty;
        }
    }

    /// <summary>The row under the pointer, or -1.</summary>
    public int Hover => _hover;

    /// <summary>Whether the pointer wears the active bitmap. Set on an enter or a leave and on
    /// nothing else, so it can disagree with <see cref="Hover"/> on a screen the pointer did not
    /// move onto.</summary>
    public bool PointerLive => _pointerLive;

    /// <summary>The key of the row a press is holding, or "". It fires when the button comes up
    /// still on it and never otherwise.</summary>
    public string ArmedKey => _armed ?? string.Empty;

    /// <summary>The screen's scrolling lists as the pointer sees them, the topmost first. Under a
    /// dialog there are none, and while a dropdown stands open its list is the only one. Otherwise
    /// they are the screen's own.</summary>
    public IReadOnlyList<OriginalList> Lists
    {
        get
        {
            var lists = new List<OriginalList>();
            if (_dialog != null)
            {
                return lists;
            }

            switch (_screen)
            {
                case OriginalScreen.FreeFlight:
                case OriginalScreen.Dogfight:
                    SortieLists(lists);
                    break;
                case var _ when ModuleFor(_screen) is { } module:
                    module.Lists(lists);
                    break;
                case OriginalScreen.SeatPlane:
                    SeatPlaneLists(lists);
                    break;
            }

            return lists;
        }
    }

    /// <summary>Whether the screen showing is one of the hangar's: the name screen, a tab, the
    /// totals page or the inventory. The one family the presentation asks after by name, for the
    /// palette its blueprint pages are drawn in. The answer is the dispatch lookup's own, not a
    /// second reading of the screen's number.</summary>
    public bool IsHangarScreen => Hangar != null && ReferenceEquals(ModuleFor(_screen), Hangar);

    /// <summary>Whether seat 0's typed characters feed a text field right now. The field is the
    /// campaign roster's name box, one of the hangar's own, or an armed typed cheat. That cheat's
    /// latch is the shell's rather than any module's. None of them while a dialog stands over the
    /// screen.</summary>
    public bool CapturingText =>
        _dialog == null
        && (TypingCheat || _screen == OriginalScreen.CampaignRoster || (Hangar?.CapturingText ?? false)
            || Connection.CapturingText || Lobby.CapturingText);

    /// <summary>The hangar module behind the hangar screens, with its own state and inks, or null
    /// on a shell built without a hangar feature.</summary>
    public OriginalHangarScreen? Hangar { get; }

    /// <summary>The module behind the Instant Action screen and its Weapon Loadout, with its own
    /// state and inks. A shell without an Instant Action feature keeps a private one, so this
    /// module always stands.</summary>
    public OriginalInstantActionScreen InstantAction { get; }

    /// <summary>The module behind the five pages the Options hub's doors open. It holds the saved
    /// settings every page shows back and the choices each page's apply would carry. The hub
    /// itself is the shell's, being a column of doors and nothing else.</summary>
    public OriginalOptionsScreen Options { get; }

    /// <summary>The module behind the campaign's ten screens, holding the open campaign and the
    /// pages it composes. It stands on a shell built without a campaign feature too, with its
    /// Campaign row disabled and every door inside it shut.</summary>
    public OriginalCampaignScreen Campaign { get; }

    /// <summary>The module behind the Instant Action wrap-up page, holding the final numbers one
    /// ended mission handed over. It stands empty until a session hands one in.</summary>
    public OriginalWrapupScreen Wrapup { get; }

    /// <summary>The module behind the join board, the one screen a pad signs onto a seat from.
    /// </summary>
    public OriginalJoinBoard JoinBoard { get; }

    /// <summary>The module behind the Multiplayer Connection page and its games list. It stands on
    /// a shell built without a network door too, with the Multiplayer plaque disabled.</summary>
    public OriginalConnectionScreen Connection { get; }

    /// <summary>The module behind the Multiplayer Lobby, standing empty until a Dogfight is hosted
    /// or joined.</summary>
    public OriginalLobbyScreen Lobby { get; }

    /// <summary>Which campaign board the screen showing wears, or null when it wears none; what
    /// the presentation picks the board's palette by. The campaign's own screens answer for
    /// themselves and the remake-only per-seat aircraft screen wears the plane-selection board.</summary>
    public CampaignScreen? CampaignPage =>
        Campaign.Board ?? (_screen == OriginalScreen.SeatPlane ? CampaignScreen.PlaneSelection : null);

    /// <summary>The list whose thumb the pointer is dragging, or null.</summary>
    public string? Dragging => _drag?.Key;

    /// <summary>The slider row the pointer is dragging, or null. Kept apart from
    /// <see cref="Dragging"/> so a drag can never change which of the two it belongs to.</summary>
    public string? DraggingSlider => _slider.Held;

    /// <summary>The picked chapter's code, or null.</summary>
    public string? PickedChapter => _pickedChapter >= 0 ? _chapters[_pickedChapter].Code : null;

    /// <summary>The pointer's last authored position, or null when the seat has none.</summary>
    public (float X, float Y)? Pointer => _pointer;

    // The campaign's table where one is open, the hangar's otherwise, empty with neither: the words
    // the messagebox answers and the per-seat screen's ratings take. Either family may have loaded one.
    private CSVM.Mech3.UiStrings MenuStrings =>
        Campaign.Strings ?? Hangar?.Strings ?? CSVM.Mech3.UiStrings.Empty;

    // The open drop-down list on the screen showing, campaign or per-seat, or null. The axis walks
    // its entries, and the pointer takes its rows as one of the screen's lists.
    private CampaignCombo? CurrentCombo =>
        _screen == OriginalScreen.SeatPlane
            ? (_seatPage?.List is { Open: true } list ? list : null)
            : Campaign.OpenCombo;

    /// <summary>Opens the hangar from the screen showing, wallet-free from Instant Action's Build
    /// Custom Plane and over <paramref name="wallet"/> from the cabin's PLANE CONSTRUCTION. Nothing
    /// happens when the shell has no hangar feature or no store.</summary>
    public void OpenHangar(IHangarWallet? wallet = null) => Hangar?.OpenHangar(wallet, DoorAirframe());

    /// <summary>Opens a hangar tab directly on a default-configuration build named
    /// <paramref name="name"/>, the screenshot aids' door.</summary>
    public void OpenHangarTab(OriginalScreen tab, string name, IHangarWallet? wallet = null) =>
        Hangar?.OpenHangarTab(tab, name, wallet, DoorAirframe());

    /// <summary>Stands the shell on its top level, the landing point of every return and of a cold
    /// start. The list cursors stay where they were. Every seat's pick goes back to browsing, so a
    /// return from flight cannot fly again on a stale pick. An open campaign is dropped, since the
    /// two flight returns reopen it on the profile the mission wrote.</summary>
    public void ReturnToTopLevel()
    {
        _setup.ResetPicks(fits: true);
        Campaign.CloseCampaign();
        Open(OriginalScreen.TopLevel);
    }

    /// <summary>Opens a screen directly, the screenshot aids' door. Any screen the walk does not
    /// stand on (<see cref="OnSeatWalk"/>) ends a seat walk in progress.</summary>
    public void Open(OriginalScreen screen)
    {
        _screen = screen;
        _hover = -1;
        _pressed = -1;
        _armed = null;
        if (!OnSeatWalk)
        {
            _pickingSeat = null;
            _seatPage = null;
            InstantAction.ClearLoadoutSeat();
        }

        _drag = null;
        _slider.LetGo();
        ResetCreditsSecret();
        ResetCheats();
        Options.ScreenOpened(screen);
    }

    /// <summary>Applies one frame of seat 0's commands, under the rule <see cref="StepSeat"/>
    /// applies to it. On a screen standing for another seat, only its pointer counts. Those screens
    /// are the per-seat aircraft screen picking for one and the campaign check's screens on a
    /// guest's. The pointer, when present, is in authored pixels.</summary>
    public OriginalStep Step(MenuCommands commands) => StepSeat(0, commands);

    /// <summary>One menu frame of the network door, whatever screen shows: the door is stepped, the
    /// cabin's co-op offer renewed and the Connection pages kept current. Returns whether the
    /// picture changed. It has whenever the door's <see cref="NetPlayFeature.Revision"/> moved,
    /// as a guest's Ready, a join, a host's word or a lobby line moves it.</summary>
    public bool StepNet(double dt)
    {
        if (_net is not { } net)
        {
            return false;
        }

        int revision = net.Revision;
        net.Step(dt);
        bool changed = FollowCoopGuest(net) | FollowDogfight(net) | Campaign.StepCoop() | (net.Revision != revision);
        return Connection.Tick(dt) | Lobby.Tick(dt) || changed;
    }

    /// <summary>The Connection page following the door's join again: a co-op guest whose link to
    /// its host ended lands here, and the page's box says why.</summary>
    public void ReturnToConnection()
    {
        Campaign.CloseCampaign();
        Connection.FollowAgain();
        Connection.OpenConnection();
    }

    /// <summary>A co-op or Dogfight guest's launch once its host has launched, taken after
    /// <see cref="StepNet"/>, or null.</summary>
    public MenuExit? TakeNetExit() => _net != null ? Lobby.GuestLaunch() ?? Campaign.GuestLaunch() : null;

    /// <summary>Stands <paramref name="door"/> in for the network door, the screenshot aids' and
    /// the suites' way to show a door they drive themselves.</summary>
    internal void StandInNetDoor(NetPlayFeature door) => _net = door ?? throw new ArgumentNullException(nameof(door));

    // A joined co-op guest leaves the Connection page for its host's boards once the host names
    // one. It goes back to the page when the link ends, and the page then says why.
    private bool FollowCoopGuest(NetPlayFeature net)
    {
        if (Campaign.IsGuest)
        {
            if (net.IsCoopGuest)
            {
                return false;
            }

            ReturnToConnection();
            return true;
        }

        if (!net.IsCoopGuest || net.CoopFlow == null || net.Released || !Connection.Owns(_screen))
        {
            return false;
        }

        Connection.StopFollowing();
        return Campaign.OpenGuestCampaign();
    }

    // A joined Dogfight guest leaves the Connection pages for the lobby once its host's lobby
    // answers. A lobby whose door has closed goes back to the Connection page, which says why.
    private bool FollowDogfight(NetPlayFeature net)
    {
        if (_screen == OriginalScreen.Lobby)
        {
            if (net.Dogfight != null || net.DogfightLaunchDue || net.Released)
            {
                return false;
            }

            ReturnToConnection();
            return true;
        }

        if (!net.IsDogfightGuest || net.Dogfight == null || net.Released || !Connection.Owns(_screen))
        {
            return false;
        }

        Connection.StopFollowing();
        Lobby.OpenGuest();
        return true;
    }

    // The module that owns a screen, or null where the shell itself does. Every dispatch site asks
    // once and calls what comes back, so no site knows how many modules there are or which screens
    // each takes.
    private IOriginalScreenModule? ModuleFor(OriginalScreen screen)
    {
        foreach (var module in _modules)
        {
            if (module.Owns(screen))
            {
                return module;
            }
        }

        return null;
    }

    // The airframe a default-configuration build opens on: the pilot's current plane on whichever
    // screen the hangar door stands on. Instant Action's door means its Pilot Plane pick, and the
    // cabin's means the seated pilot's own aircraft. Any other door has no current plane to
    // inherit (docs/org/hangar.md, "What Load Default Configuration loads").
    private int DoorAirframe()
    {
        if (_screen == OriginalScreen.InstantAction && _instantAction != null)
        {
            return _instantAction.PlayerPlaneIndex;
        }

        return Campaign.SeatedAirframe ?? HangarFeature.DefaultAirframe;
    }

    // Typed characters and Backspace into whichever edit box is showing. The campaign roster's own
    // rule applies first, else whichever the hangar owns. The name screen and the hub share the
    // hangar name's character set and cap.
    private bool TypeName(MenuCommands commands, List<string> cues) =>
        _screen switch
        {
            OriginalScreen.CampaignRoster => Campaign.TypeName(commands, cues),
            OriginalScreen.Connection => Connection.TypeAddress(commands, cues),
            OriginalScreen.Lobby => Lobby.TypeText(commands),
            _ => Hangar?.TypeName(commands, cues) ?? false,
        };

    // The per-seat screen's one list for the pointer: its open drop-down, which hangs over the screen.
    private void SeatPlaneLists(List<OriginalList> lists)
    {
        if (_seatPage?.List is { Open: true } list && CampaignBoards.ComboWindow(list) is { } window)
        {
            lists.Add(new OriginalList(OriginalWidgets.EntryKeyPrefix + "LIST", window, top => list.ScrollTo(top)));
        }
    }

    // A click off an open per-seat list closes it and picks nothing, the rule the campaign module
    // applies to its own.
    private bool CloseSeatCombo() =>
        _screen == OriginalScreen.SeatPlane && _seatPage?.List is { Open: true } list && list.Collapse();

    // Inside an open list the axis walks its entries rather than the screen's rows.
    private bool MoveCombo(int direction) => CurrentCombo is { } combo && combo.Move(direction);

    private void RefreshRosterFromStore()
    {
        if (_planes == null)
        {
            return;
        }

        _setup.SetRoster(OriginalRosters.Roster(_planes.List()));
        foreach (var seat in _setup.Seats)
        {
            seat.Cursor = Math.Clamp(seat.Cursor, 0, Math.Max(0, _setup.Roster.Count - 1));
        }
    }

    // One seat's frame applied to the screen showing, the seat's own right to drive it already
    // settled by StepSeat.
    private OriginalStep ApplyFrame(MenuCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        // A capture in progress swallows the frame. The player is pressing a control to BIND it.
        // Reading the same press as a menu command would move the cursor and fire a row under them.
        // The capture reads the seat's own hardware, which is where Escape is answered.
        if (_screen == OriginalScreen.Keys && _controls is { Capturing: true } capturing)
        {
            return new OriginalStep(Array.Empty<string>(), null, capturing.Poll());
        }

        // A film in front of the screen owns the frame, and so does the tail of the press that
        // ended one. The board never saw that press go down, so its release fires nothing. The
        // swallowed frame asks for a redraw, the screen having changed unread (CinemaFilm).
        if (_film.Up)
        {
            return new OriginalStep(Array.Empty<string>(), null, false);
        }

        if (_film.Swallows(commands.Pointer is { Pressed: true }))
        {
            return new OriginalStep(Array.Empty<string>(), null, true);
        }

        var cues = new List<string>();
        MenuExit? exit = null;
        Campaign.SyncField();
        // A typed cheat holding the keyboard swallows the frame's characters: the script's own
        // focus moved the caret off whatever edit box the screen carries.
        bool changed = TypingCheat ? TypeCheat(commands) : TypeName(commands, cues);
        if (OnSeatWalk && _pickingSeat is not { Joined: true })
        {
            // The seat this screen was picking for has gone. The walk moves on or ends, and a
            // Weapon Loadout it had open on that seat's own fit goes with it.
            InstantAction.DropLoadout();
            exit = AdvanceSeatWalk();
            changed = true;
        }

        var rows = Rows;
        int focus = EnsureFocus(rows);

        if (commands.Pointer is { } pointer)
        {
            bool pointerMoved = _pointer != (pointer.X, pointer.Y);
            changed |= pointerMoved;
            _pointer = (pointer.X, pointer.Y);
            // The one screen that reads the secondary button reads it whatever the rows are doing.
            // Its region holds no row, so no hit test can carry it.
            if (_screen == OriginalScreen.Credits)
            {
                changed |= HoldCreditsSecret(pointer);
            }

            // The three typed cheats read the primary button the same way, their regions holding
            // no row either.
            changed |= ArmCheat(pointer);

            // The thumb, the slider and the wheel come before the rows. A held one owns the
            // pointer until it is let go, and a wheel step moves the rows the hit test then reads.
            // The thumb has first refusal and stands down while a slider holds, so neither crosses.
            bool dragging = _slider.Held == null && DragThumb(pointer, ref changed);
            if (!dragging)
            {
                dragging = _slider.Drive(rows, pointer, out bool moved);
                changed |= moved;
            }

            if (!dragging && pointer.Wheel != 0)
            {
                changed |= WheelList(pointer);
            }

            rows = Rows;
            focus = EnsureFocus(rows);
            int over = dragging ? -1 : HitTest(rows, pointer.X, pointer.Y);
            if (pointerMoved)
            {
                bool live = over >= 0 && rows[over].Enabled;
                changed |= live != _pointerLive;
                _pointerLive = live;
            }

            if (over != _hover)
            {
                _hover = over;
                changed = true;
                if (over >= 0 && rows[over].Enabled)
                {
                    // An open campaign list's entry takes the highlight, not the focus, which
                    // stays on the field the list hangs from.
                    if (OriginalWidgets.HoverOnly(rows[over]))
                    {
                        OriginalWidgets.Highlight(CurrentCombo, rows[over].Key);
                    }
                    else
                    {
                        focus = over;
                    }

                    if (rows[over].Kind != OriginalRowKind.ListRow)
                    {
                        cues.Add(OriginalCues.Rollover);
                    }
                }
            }

            if (pointer.Clicked && !dragging && over >= 0 && rows[over].Enabled)
            {
                _armed = rows[over].Key;
            }

            bool onArmed = _armed != null && over >= 0 && rows[over].Enabled && rows[over].Key == _armed;
            int pressed = onArmed ? over : -1;
            changed |= pressed != _pressed;
            _pressed = pressed;
            bool fires = onArmed && !pointer.Pressed;
            if (!pointer.Pressed)
            {
                _armed = null;
            }

            if (dragging)
            {
                // A drag's click was spent on the thumb or the slider it took hold of; nothing
                // under the pointer is activated.
            }
            else if (fires)
            {
                if (!OriginalWidgets.HoverOnly(rows[over]))
                {
                    focus = over;
                    _focus[(int)_screen] = focus;
                }

                exit = Activate(rows[over], cues, byPointer: true);
                changed = true;
                rows = Rows;
                focus = EnsureFocus(rows);
            }
            else if (pointer.Clicked && over < 0
                && ((ModuleFor(_screen)?.CloseDropdown() ?? false) || CloseSeatCombo()))
            {
                // A click off an open list closes it and picks nothing.
                changed = true;
                rows = Rows;
                focus = EnsureFocus(rows);
            }
        }

        if (commands.MoveY != 0)
        {
            // Inside an open campaign list the axis walks the list's entries.
            if (!MoveCombo(commands.MoveY))
            {
                focus = StepWithinColumn(rows, focus, commands.MoveY);
            }

            changed = true;
        }

        if (commands.MoveX != 0)
        {
            // A sideways step changes a value where the cursor stands on one. A slider comes first,
            // belonging to no one screen, then the screen's own module where one owns it.
            // Otherwise it crosses columns.
            if (SliderControl.StepValue(rows, focus, commands.MoveX))
            {
                rows = Rows;
                focus = EnsureFocus(rows);
            }
            else if (ModuleFor(_screen) is { } module && module.StepSideways(rows, focus, commands.MoveX))
            {
                rows = Rows;
                focus = EnsureFocus(rows);
            }
            else
            {
                focus = StepColumn(rows, focus, commands.MoveX);
            }

            changed = true;
        }

        _focus[(int)_screen] = focus;
        if (commands.Unbind && _screen == OriginalScreen.Keys && focus >= 0 && Options.ClearCell(rows[focus]))
        {
            changed = true;
        }

        if (exit == null && commands.Accept && focus >= 0 && rows[focus].Enabled)
        {
            exit = Activate(rows[focus], cues, byPointer: false);
            changed = true;
        }
        else if (exit == null && commands.Back)
        {
            exit = Back();
            changed = true;
        }

        // With seat 0's aircraft picked, a joined seat still to confirm gets its own screen.
        if (exit == null && IsSortie && BeginSeatWalkIfDue())
        {
            changed = true;
        }

        Options.SyncWindows();
        return new OriginalStep(cues, exit, changed);
    }

#pragma warning disable SA1202 // ApplyFrame above stays beside the Step that delegates to it.
    /// <summary>The screen as a composed board in the authored space, a standing dialog over it
    /// and the pointer drawn last.</summary>
    public ComposedBoard Compose()
    {
        var rows = Rows;
        int focus = EnsureFocus(rows);
        // Under a dialog the screen is drawn from its own rows with nothing focused. The dialog's
        // answers are the rows the pointer and the cursor see.
        var screenRows = _dialog == null ? rows : BuildRows();
        int screenFocus = _dialog == null ? focus : -1;
        var layers = new BoardLayers();
        ComposeMovie(layers.Backdrop);
        var main = _layout.Screen(OriginalAvailability.MainMenuSection);
        var screenModule = ModuleFor(_screen);
        bool ownPage = _screen is OriginalScreen.Options or OriginalScreen.SeatPlane or OriginalScreen.Credits
            || screenModule != null;
        if (!ownPage && main?.Widget("MM_LOGO") is { Art.Count: > 0 } logo)
        {
            layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, logo.Art[0], logo.Frames), logo.Int("X"), logo.Int("Y")));
        }

        if (_screen == OriginalScreen.TopLevel && main?.Widget("BFRAME") is { Art.Count: > 0 } structure)
        {
            layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, structure.Art[0], structure.Frames), structure.Int("X"), structure.Int("Y")));
        }

        switch (_screen)
        {
            case var _ when screenModule != null:
                screenModule.Compose(screenRows, screenFocus, layers);
                break;
            case OriginalScreen.SeatPlane:
                ComposeSeatPlane(focus, layers);
                break;
            case OriginalScreen.FreeFlight:
            case OriginalScreen.Dogfight:
                ComposeSortie(rows, layers);
                break;
            case OriginalScreen.Options:
                ComposeOptions(layers);
                break;
            case OriginalScreen.Credits:
                ComposeCredits(layers);
                break;
        }

        if (!ownPage || _screen is OriginalScreen.Options or OriginalScreen.Credits)
        {
            ComposeRows(screenRows, screenFocus, layers);
        }

        if (_dialog != null)
        {
            ComposeDialog(rows, focus, layers.Overlays);
        }

        if (_pointer is { } at)
        {
            layers.Overlays.Add(new BoardPanel(
                Array.Empty<BoardFill>(),
                new[] { new BoardPicture(_pointerLive ? _activePointer : _passivePointer, at.X, at.Y) },
                Array.Empty<BoardLine>()));
        }

        return new ComposedBoard(layers.Pictures, layers.Strokes, layers.Lines, layers.Plaques, layers.Notes,
            backdrop: layers.Backdrop, fills: layers.Fills, overlays: layers.Overlays);
    }
#pragma warning restore SA1202

    private static OriginalPreferencesInks ReadPreferencesInks(MenuLayout layout, OriginalInks inks)
    {
        var screen = layout.Screen(PreferencesSection);
        var description = screen?.Widget("PF_T_GODESC");
        var title = screen?.Widget("PF_T_TITLE");
        return new OriginalPreferencesInks(
            description != null && description.TryColor("Color", out var text) ? text : inks.Disabled,
            title != null && title.TryColor("Color", out var heading) ? heading : inks.Active);
    }

    private static OriginalInks ReadInks(MenuLayout layout, MenuLayoutWidget? plaque)
    {
        var disabled = layout.GlobalColor("DISABLED") ?? new MenuLayoutColor(255, 188, 188, 188);
        var active = layout.GlobalColor("ACTIVE") ?? new MenuLayoutColor(255, 255, 255, 255);
        var rollover = layout.GlobalColor("ROLLOVER") ?? active;
        var depressed = layout.GlobalColor("DEPRESSED") ?? new MenuLayoutColor(255, 0, 0, 0);
        var labelNormal = plaque != null && plaque.TryColor("ColorActive", out var n) ? n : active;
        var labelRollover = plaque != null && plaque.TryColor("ColorRollover", out var r) ? r : rollover;
        var labelDepressed = plaque != null && plaque.TryColor("ColorDepressed", out var d) ? d : depressed;
        return new OriginalInks(disabled, active, rollover, depressed, labelNormal, labelRollover, labelDepressed);
    }

    // The n-th art a row names as a strip, the shared drop-list rule's own reading. The option
    // pages and the Keys page name their arrows, bars and checkbox strips this way.
    private static BoardArt? StripArt(IReadOnlyList<string> art, int index, int frames = 4) =>
        OriginalDropLists.StripArt(art, index, frames);

    // The pointer bitmaps are named by the globals script, not by any layout row, so they are
    // read off the script-named asset list. The bare file name is the fallback.
    private static string PointerArt(MenuLayout layout, string fileName)
    {
        foreach (var asset in layout.ExternalAssets)
        {
            if (asset.Kind == "file" && asset.Path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                int slash = asset.Path.LastIndexOf('/');
                return slash >= 0 ? asset.Path[(slash + 1)..] : asset.Path;
            }
        }

        return fileName;
    }

    private static int HitTest(IReadOnlyList<OriginalRow> rows, float x, float y)
    {
        // Later rows draw over earlier ones, so the last hit wins.
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Visible && rows[i].Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    private static int StepWithinColumn(IReadOnlyList<OriginalRow> rows, int focus, int dir)
    {
        if (focus < 0)
        {
            return focus;
        }

        int column = rows[focus].Column;
        int i = focus;
        for (int n = 0; n < rows.Count; n++)
        {
            i = (i + dir + rows.Count) % rows.Count;
            if (rows[i].Column == column && rows[i].Enabled)
            {
                return i;
            }
        }

        return focus;
    }

    private static int StepColumn(IReadOnlyList<OriginalRow> rows, int focus, int dir)
    {
        if (focus < 0)
        {
            return focus;
        }

        int columns = 0;
        foreach (var row in rows)
        {
            columns = Math.Max(columns, row.Column + 1);
        }

        if (columns < 2)
        {
            return focus;
        }

        int from = rows[focus].Column;
        int ordinal = OrdinalInColumn(rows, focus);
        int target = (from + dir + columns) % columns;
        int best = -1;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Column != target || !rows[i].Enabled)
            {
                continue;
            }

            int distance = Math.Abs(OrdinalInColumn(rows, i) - ordinal);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best >= 0 ? best : focus;
    }

    private static int OrdinalInColumn(IReadOnlyList<OriginalRow> rows, int index)
    {
        int ordinal = 0;
        for (int i = 0; i < index; i++)
        {
            if (rows[i].Column == rows[index].Column)
            {
                ordinal++;
            }
        }

        return ordinal;
    }

    // A strip's one-frame size from this shell's own measurer, the shared reading.
    private (float Width, float Height) StripSize(BoardArt? art, float fallbackWidth, float fallbackHeight) =>
        OriginalWidgets.StripSize(art, Measure, fallbackWidth, fallbackHeight);

    // One of a section's own button strips as a row, at its authored corner in its measured size.
    private void AddStrip(MenuLayoutScreen screen, List<OriginalRow> rows, string key, OriginalRowKind kind, bool enabled, int column)
    {
        if (screen.Widget(key) is not { } widget)
        {
            return;
        }

        var art = StripArt(widget.Art, 0, widget.Frames);
        var size = StripSize(art, FallbackButtonWidth, FallbackButtonHeight);
        rows.Add(new OriginalRow(key, widget.Text ?? string.Empty, kind, widget.Int("X"), widget.Int("Y"),
            size.Width, size.Height, enabled, column, art));
    }

    // Puts the focus on the row carrying a key, when the current rows have it.
    private void FocusKey(string key)
    {
        var rows = Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == key)
            {
                _focus[(int)_screen] = i;
                return;
            }
        }
    }

    // A thumb drag. A click on a list's thumb takes hold of it, and while the button stays down
    // the window follows the pointer down the track. Letting go ends it. True while one holds.
    private bool DragThumb(MenuPointer pointer, ref bool changed)
    {
        if (_drag is { } drag)
        {
            if (!pointer.Pressed)
            {
                _drag = null;
                changed = true;
                return false;
            }

            foreach (var list in Lists)
            {
                if (list.Key != drag.Key)
                {
                    continue;
                }

                int top = list.Window.TopAfterDrag(drag.StartTop, pointer.Y - drag.StartY);
                if (top != list.Window.Top)
                {
                    list.ScrollTo(top);
                    changed = true;
                }

                return true;
            }

            // The list the drag began on is gone with its screen.
            _drag = null;
            return false;
        }

        if (!pointer.Clicked)
        {
            return false;
        }

        foreach (var list in Lists)
        {
            if (list.Window.OnThumb(pointer.X, pointer.Y))
            {
                _drag = (list.Key, pointer.Y, list.Window.Top);
                _hover = -1;
                changed = true;
                return true;
            }
        }

        return false;
    }

    // A wheel step over a list moves its window by that many rows. The first list containing the
    // pointer takes it, and a list that fits its window ignores it.
    private bool WheelList(MenuPointer pointer)
    {
        foreach (var list in Lists)
        {
            if (!list.Window.Contains(pointer.X, pointer.Y))
            {
                continue;
            }

            int top = list.Window.TopAfterWheel(pointer.Wheel);
            if (top == list.Window.Top)
            {
                return false;
            }

            list.ScrollTo(top);
            return true;
        }

        return false;
    }

    // The rows of a screen that has no page of its own, which are the top level, the sortie screens
    // and the Options screen. They draw a decoded strip in its state frame, a paper plaque with its
    // label, and list text. A plaque whose art is missing takes an outlined label. Nothing is
    // focused or pressed while a dialog stands over the screen. A row outside its list's window
    // draws nothing, since the window is what the pointer scrolls.
    private void ComposeRows(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.Visible)
            {
                continue;
            }

            bool focused = i == focus;
            bool pressed = focus >= 0 && i == _pressed;
            switch (row.Kind)
            {
                case OriginalRowKind.Button when row.Art != null:
                    int stripFrame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
                    layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, i, stripFrame, string.Empty, BoardInk.LabelNormal));
                    break;
                case OriginalRowKind.TextButton:
                    var ink = row.Enabled ? ComposedBoard.PlaqueInk(focused, pressed) : BoardInk.Detail;
                    if (row.Art != null)
                    {
                        int plaqueFrame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
                        layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, i, plaqueFrame, row.Label, ink));
                    }
                    else
                    {
                        layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 255, 255, 255, 0.6f, Border: true));
                        layers.Lines.Add(new BoardLine(row.Label, row.X, row.Y + 4f, row.Width, RowFont, ink, i, false, BoardJustify.Center));
                    }

                    break;
                default:
                    if (IsPicked(row))
                    {
                        layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 255, 255, 255, 0.18f));
                    }

                    layers.Lines.Add(new BoardLine(row.Label, row.X + 6f, row.Y + 1f, row.Width - 12f, RowFont,
                        focused ? BoardInk.RowFocused : BoardInk.Row, i));
                    break;
            }
        }
    }

    // The screen's background movie, under everything else it draws. The row places it at its own
    // corner and scales the picture by a percentage of the picture's own size. The measurer answers
    // that size, and no number here does. A movie that does not measure composes nothing, which is
    // this screen with its background missing and the rest of it intact.
    private void ComposeMovie(List<BoardPicture> backdrop)
    {
        if (!MovieSections.TryGetValue(_screen, out string? section)
            || _layout.Screen(section)?.Widget(MovieKey) is not { Art.Count: > 0 } row
            || Measure(row.Art[0]) is not { } size)
        {
            return;
        }

        backdrop.Add(new BoardPicture(
            new BoardArt(BoardArtLibrary.Movie, row.Art[0]), row.Int("X"), row.Int("Y"),
            Width: size.Width * row.Int("ScaleX", 100) / 100f,
            Height: size.Height * row.Int("ScaleY", 100) / 100f));
    }

    // The Options screen's chrome, [@Preferences@]'s own: its logo and background panes, its title
    // and the description beside each page door. The rows themselves (the four doors and RETURN TO
    // MAIN MENU) are drawn by the row loop. With no section the page's own door stands alone over
    // the top level's logo.
    private void ComposeOptions(BoardLayers layers)
    {
        var screen = _layout.Screen(PreferencesSection);
        if (screen == null)
        {
            if (_layout.Screen(OriginalAvailability.MainMenuSection)?.Widget("MM_LOGO") is { Art.Count: > 0 } logo)
            {
                layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, logo.Art[0], logo.Frames), logo.Int("X"), logo.Int("Y")));
            }

            layers.Lines.Add(new BoardLine("OPTIONS", OptionsX, OptionsTop - 44f, 0f, HeadingFont, BoardInk.Heading));
            return;
        }

        foreach (string key in new[] { "PF_LOGO", "PF_BACKGROUND" })
        {
            if (screen.Widget(key) is { Art.Count: > 0 } pane)
            {
                layers.Pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, pane.Art[0], Math.Max(1, pane.Frames)), pane.Int("X"), pane.Int("Y")));
            }
        }

        if (screen.Widget("PF_T_TITLE") is { } title)
        {
            layers.Lines.Add(new BoardLine(title.Text ?? "PREFERENCES", title.Int("X"), title.Int("Y"), title.Int("Width"), PreferencesTitleFont,
                BoardInk.Heading, -1, false, title.Int("Justify") == 1 ? BoardJustify.Center : BoardJustify.Left));
        }

        foreach (string key in new[] { "PF_T_GODESC", "PF_T_APDESC", "PF_T_VPDESC", "PF_T_CPDESC" })
        {
            if (screen.Widget(key) is { } description)
            {
                layers.Lines.Add(new BoardLine(description.Text ?? string.Empty, description.Int("X"), description.Int("Y"),
                    description.Int("Width"), PreferencesTextFont, BoardInk.Row));
            }
        }
    }

    private int EnsureFocus(IReadOnlyList<OriginalRow> rows)
    {
        int focus = _focus[(int)_screen];
        if (focus >= 0 && focus < rows.Count && rows[focus].Enabled)
        {
            return focus;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Enabled)
            {
                _focus[(int)_screen] = i;
                return i;
            }
        }

        _focus[(int)_screen] = -1;
        return -1;
    }

    // The byPointer flag is whether the gesture is a pointer release on the row rather than the
    // cursor's Accept. Only an edit box tells the two apart. A click in one puts the caret there
    // and does nothing else. Accept in it takes the box's own default button (MB.JM), which on the
    // profile screen is CM_B_START (docs/formats/campaign-screens.md).
    private MenuExit? Activate(OriginalRow row, List<string> cues, bool byPointer)
    {
        if (row.Kind != OriginalRowKind.ListRow)
        {
            cues.Add(OriginalCues.Click);
        }

        // A standing dialog takes the answer whatever screen it stands over.
        if (_dialog != null)
        {
            AnswerDialog(row.Key);
            return null;
        }

        if (byPointer && row.Kind == OriginalRowKind.TextField)
        {
            return null;
        }

        switch (_screen)
        {
            case OriginalScreen.TopLevel:
                switch (row.Key)
                {
                    case FreeFlightKey:
                        Open(OriginalScreen.FreeFlight);
                        break;
                    case DogfightKey:
                        Open(OriginalScreen.Dogfight);
                        break;
                    case JoinBoardKey:
                        JoinBoard.Open();
                        break;
                    case CampaignKey:
                        Campaign.OpenCampaign();
                        break;
                    case "MM_B_INSTANTACTION":
                        InstantAction.OpenInstantAction();
                        break;
                    case MultiplayerKey:
                        Connection.OpenConnection();
                        break;
                    case "MM_B_PREFERENCES":
                        Open(OriginalScreen.Options);
                        break;
                    case CreditsDoorKey:
                        Open(OriginalScreen.Credits);
                        break;
                    case "MM_B_QUIT":
                        return new QuitExit();
                }

                break;
            case OriginalScreen.FreeFlight:
            case OriginalScreen.Dogfight:
                return ActivateSortie(row);
            case OriginalScreen.Credits:
                return ActivateCredits(row);
            case var _ when ModuleFor(_screen) is { } module:
                return module.Activate(row);
            case OriginalScreen.SeatPlane:
                return ActivateSeatPlane(row);
            case OriginalScreen.Options:
                switch (row.Key)
                {
                    case OriginalOptionsScreen.GameOptionsDoorKey:
                        Options.OpenGameOptions();
                        break;
                    case OriginalOptionsScreen.AudioDoorKey:
                        Options.OpenAudio();
                        break;
                    case OriginalOptionsScreen.VideoDoorKey:
                        Options.OpenVideo();
                        break;
                    case OriginalOptionsScreen.ControlsDoorKey:
                        Options.OpenControlsPrefs();
                        break;
                    case BackKey:
                    case OptionsBackKey:
                        Open(OriginalScreen.TopLevel);
                        break;
                }

                break;
        }

        return null;
    }

    // Back with a dialog standing takes its declining answer, the messagebox script's own Escape.
    // On a sortie screen it undoes seat 0's pick a stage at a time, then leaves. The per-seat
    // screen has its own, whose meaning depends on who pressed it. A module answers it on its own
    // screens, each page's own declining answer. The campaign and the hangar walk their graphs
    // back, and the top level quits as MAINMENU.SCRIPT's Quit does.
    private MenuExit? Back()
    {
        if (_dialog is { } dialog)
        {
            AnswerDialog(dialog.Answers[dialog.Answers.Count - 1].Key);
            return null;
        }

        if (_screen == OriginalScreen.TopLevel)
        {
            return new QuitExit();
        }

        if (_screen == OriginalScreen.SeatPlane)
        {
            return BackSeatPlane();
        }

        if (IsSortie && Seat0 is { } seat && _setup.Back(seat) != SeatBack.Browsing)
        {
            return null;
        }

        // A module answers Back on its own screens. Instant Action leaves one case to the shell's
        // own way out below: nothing open there and nothing to cancel is its Exit.
        if (ModuleFor(_screen) is { } module && module.Back())
        {
            return null;
        }

        Open(OriginalScreen.TopLevel);
        return null;
    }

    private IReadOnlyList<OriginalRow> BuildRows()
    {
        var rows = new List<OriginalRow>();
        switch (_screen)
        {
            case OriginalScreen.TopLevel:
                rows.Add(TextButton(FreeFlightKey, "FREE FLIGHT", DoorX, DoorY, true, 0));
                rows.Add(TextButton(DogfightKey, "DOGFIGHT", DoorX, DogfightDoorY, true, 0));
                rows.Add(TextButton(JoinBoardKey, "JOIN BOARD", DoorX, JoinBoardDoorY, true, 0));
                var main = _layout.Screen(OriginalAvailability.MainMenuSection);
                foreach (string key in TopLevelButtons)
                {
                    if (main?.Widget(key) is { } widget)
                    {
                        bool enabled = key is "MM_B_QUIT" or "MM_B_PREFERENCES" or "MM_B_INSTANTACTION" or CreditsDoorKey
                            || (key == CampaignKey && Campaign.CanOpen) || (key == MultiplayerKey && _net != null);
                        rows.Add(Button(widget, enabled));
                    }
                }

                break;
            case OriginalScreen.Credits:
                BuildCreditsRows(rows);
                break;
            case var _ when ModuleFor(_screen) is { } module:
                module.BuildRows(rows);
                break;
            case OriginalScreen.SeatPlane:
                if (_seatPage is { } seatPage)
                {
                    OriginalWidgets.PageRows(
                        seatPage, CurrentCombo, _ => true, _campaignLayout, Measure, rows);
                }

                break;
            case OriginalScreen.FreeFlight:
            case OriginalScreen.Dogfight:
                SortieRows(rows);
                break;
            case OriginalScreen.Options:
                BuildOptionsRows(rows);
                break;
        }

        return rows;
    }

    // The Options screen over [@Preferences@]. It is the four page doors at their authored corners
    // and the section's own RETURN TO MAIN MENU. The GAME OPTIONS, AUDIO and VIDEO doors are live,
    // CONTROLS disabled since no shared controls option stands behind it. Without the section the
    // three live doors stand alone with a BACK plaque, so the screen is still navigable.
    private void BuildOptionsRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(PreferencesSection);
        if (screen == null)
        {
            rows.Add(TextButton(OriginalOptionsScreen.GameOptionsDoorKey, "GAME OPTIONS", OptionsX, OptionsTop, true, 0));
            rows.Add(TextButton(OriginalOptionsScreen.AudioDoorKey, "AUDIO", OptionsX, OptionsTop + OptionsPitch, true, 0));
            rows.Add(TextButton(OriginalOptionsScreen.VideoDoorKey, "VIDEO", OptionsX, OptionsTop + (2f * OptionsPitch), true, 0));
            rows.Add(TextButton(BackKey, "BACK", OptionsX, OptionsTop + (3f * OptionsPitch), true, 0));
            return;
        }

        foreach (string key in PreferencesPageKeys)
        {
            if (screen.Widget(key) is { } door)
            {
                rows.Add(Button(door, key != OriginalOptionsScreen.ControlsDoorKey || _controls != null));
            }
        }

        if (screen.Widget(OptionsBackKey) is { } back)
        {
            rows.Add(Button(back, true));
        }
        else
        {
            rows.Add(TextButton(BackKey, "BACK", OptionsX, OptionsTop, true, 0));
        }
    }

    private OriginalRow Button(MenuLayoutWidget widget, bool enabled)
    {
        string art = widget.Art.Count > 0 ? widget.Art[0] : string.Empty;
        int frames = Math.Max(1, widget.Frames);
        var size = Measure(art);
        float width = size?.Width ?? FallbackButtonWidth;
        float height = size != null ? (float)Math.Floor(size.Value.Height / (float)frames) : FallbackButtonHeight;
        return new OriginalRow(widget.Key, string.Empty, OriginalRowKind.Button, widget.Int("X"), widget.Int("Y"),
            width, height, enabled, 0, art.Length > 0 ? new BoardArt(BoardArtLibrary.Ui, art, frames) : null);
    }

    // The box that marks a focused row on the pages composed over a painted plate. The slider row
    // and the dropdown rows that take boxOnFocus share it, so one outline covers every marked row
    // there. It is the layout's own DISABLED grey rather than the dropdown's authored black, which
    // on dark paint is a dark line nobody sees. It is a mark rather than standing chrome, so only
    // the row the cursor is on ever carries it. The <paramref name="outset"/> argument stands the
    // outline that many pixels clear of the row, for a row whose art fills its own rectangle.
    private BoardFill FocusBox(OriginalRow row, float outset = 0f)
    {
        var mark = Inks.Disabled;
        return new BoardFill(
            row.X - outset, row.Y - outset, row.Width + (2f * outset), row.Height + (2f * outset),
            mark.R, mark.G, mark.B, 0.75f, Border: true);
    }

    // A slider as drawn: the slot, then the thumb at the value's own place on it. The thumb is one
    // frame with no focused or pressed state, so focus is the focus box and the wash under it.
    // ⚠ Do not drop either half of that pair, nor the unmeasured-art rectangles. The box is the
    // readable half, the wash is the region it encloses, and the rectangles are how the level
    // still shows. The readings are in docs/menu-presentations.md and docs/org/menu-inventory.md.
    private void ComposeSlider(OriginalRow row, bool focused, BoardLayers layers)
    {
        if (row.Slider is not { } slider)
        {
            return;
        }

        var track = slider.Track;
        if (focused)
        {
            layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 0, 0, 0, 0.10f));
            layers.Fills.Add(FocusBox(row));
        }

        float thumbX = track.ThumbX(slider.Value);
        if (slider.Slot != null && row.Art != null && Measure(slider.Slot.Name) != null && Measure(row.Art.Name) != null)
        {
            layers.Pictures.Add(new BoardPicture(slider.Slot, track.X, track.Y));
            layers.Pictures.Add(new BoardPicture(row.Art, thumbX, track.ThumbY));
            return;
        }

        layers.Fills.Add(new BoardFill(track.X, track.Y, track.Width, track.Height, 255, 255, 255, 0.6f, Border: true));
        layers.Fills.Add(new BoardFill(thumbX, track.ThumbY, track.ThumbWidth, track.ThumbHeight, 255, 255, 255, 0.6f));
    }

    // One row of a page over a painted plate as drawn. ⚠ The dropdown's box is the focus mark, not
    // standing chrome. It draws a dropdown's value in its box, a slider, and the two plaque kinds.
    // A permanent black rectangle over a plate's paint, around boxes the layout authors at
    // differing widths, reads as chrome nobody chose. As a mark it is the one the slider row uses,
    // so one vocabulary covers every marked row. The paper pages print the box every frame instead
    // (OriginalInstantActionScreen.ComposeRow); the hangar's rows fall through here.
    private void ComposePlateRow(OriginalRow row, bool focused, bool pressed, int index, BoardLayers layers)
    {
        switch (row.Kind)
        {
            case OriginalRowKind.Dropdown:
                // No wash under this one. The dropdown's box encloses the plate's own recessed
                // groove and the value written in it, so it already has a region. The slider's
                // encloses flat paint and needs one. A wash that changes nothing is noise.
                if (focused)
                {
                    layers.Fills.Add(FocusBox(row));
                }

                float arrowWidth = 0f;
                if (row.Art != null)
                {
                    var size = StripSize(row.Art, FallbackArrowWidth, FallbackArrowHeight);
                    arrowWidth = size.Width;
                    int frame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
                    layers.Pictures.Add(new BoardPicture(row.Art, row.X + row.Width - size.Width, row.Y + ((row.Height - size.Height) / 2f), frame));
                }

                layers.Lines.Add(new BoardLine(row.Label, row.X + 4f, row.Y + 2f, Math.Max(1f, row.Width - arrowWidth - 6f), PlateItemFont,
                    focused ? BoardInk.RowFocused : BoardInk.Row, index));
                break;
            case OriginalRowKind.Slider:
                ComposeSlider(row, focused, layers);
                break;
            case OriginalRowKind.TextButton when row.Art != null:
                int labelFrame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
                var ink = row.Enabled ? ComposedBoard.PlaqueInk(focused, pressed) : BoardInk.Detail;
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, index, labelFrame, row.Label, ink));
                break;
            case OriginalRowKind.Button when row.Art != null:
                int stripFrame = row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, focused, pressed) : 0;
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, index, stripFrame, string.Empty, BoardInk.LabelNormal));
                break;
        }
    }

    private OriginalRow TextButton(string key, string label, float x, float y, bool enabled, int column)
    {
        var size = PlaqueSize();
        return new OriginalRow(key, label, OriginalRowKind.TextButton, x, y, size.Width, size.Height, enabled, column, _plaque);
    }

    private (float Width, float Height) PlaqueSize()
    {
        if (_plaque == null || Measure(_plaque.Name) is not { } size)
        {
            return (FallbackPlaqueWidth, FallbackPlaqueHeight);
        }

        return (size.Width, (float)Math.Floor(size.Height / (float)Math.Max(1, _plaque.Frames)));
    }

    private (int Width, int Height)? Measure(string art)
    {
        if (art.Length == 0)
        {
            return null;
        }

        if (!_sizes.TryGetValue(art, out var size))
        {
            size = _measure(art);
            _sizes[art] = size;
        }

        return size;
    }

#pragma warning disable SA1201 // Explicit, since the interface's own vocabulary (Screen,
    // FocusedRow, RaiseDialog, ...) is narrower and sometimes differently named than the shell's
    // public one. It is grouped here rather than beside each member it wraps, the seam being the
    // screen modules' alone to see.
    OriginalScreen IOriginalScreenHost.Screen => _screen;

    bool IOriginalScreenHost.DialogOpen => _dialog != null;

    string IOriginalScreenHost.FocusedKey => FocusedKey;

    int IOriginalScreenHost.FocusedRow
    {
        get => _focus[(int)_screen];
        set => _focus[(int)_screen] = value;
    }

    int IOriginalScreenHost.PressedRow => _pressed;

    int IOriginalScreenHost.HoveredRow => _hover;

    int IOriginalScreenHost.FocusBeforeDialog => _focusBeforeDialog;

    (float X, float Y)? IOriginalScreenHost.Pointer => _pointer;

    CSVM.Flight.Hangar.CustomPlaneStore? IOriginalScreenHost.CampaignPlanes => Campaign.Planes;

    CSVM.Mech3.UiStrings IOriginalScreenHost.MenuStrings => MenuStrings;

    bool IOriginalScreenHost.CanBuildPlane => Hangar != null && _planes != null;

    void IOriginalScreenHost.Open(OriginalScreen screen) => Open(screen);

    void IOriginalScreenHost.FocusKey(string key) => FocusKey(key);

    void IOriginalScreenHost.RaiseDialog(string message, DialogIcon icon, params OriginalDialogAnswer[] answers) =>
        RaiseDialog(message, icon, answers);
#pragma warning restore SA1201

    void IOriginalScreenHost.CloseDialog() => _dialog = null;

    void IOriginalScreenHost.Frame(MenuCommands commands) => Step(commands);

    void IOriginalScreenHost.PlayFilm(Action<Action> play, Action then) => _film.Play(play, then);

    (int Width, int Height)? IOriginalScreenHost.Measure(string art) => Measure(art);

    void IOriginalScreenHost.ResumeCampaign() => Campaign.ResumeCampaign();

    void IOriginalScreenHost.RefreshInstantActionRoster() => InstantAction.RefreshRoster();

    void IOriginalScreenHost.RefreshRosterFromStore() => RefreshRosterFromStore();

    void IOriginalScreenHost.OpenHangar(IHangarWallet? wallet) => OpenHangar(wallet);

    MenuExit? IOriginalScreenHost.BeginSeatWalk() => BeginInstantActionSeatWalk();

    int IOriginalScreenHost.CheatedMission(int ordinary) => CheatedMission(ordinary);

    BoardPanel? IOriginalScreenHost.SeatPanel(bool onPaper) => CampaignSeatPanel(onPaper);

    void IOriginalScreenHost.ComposeGenericRow(OriginalRow row, bool focused, bool pressed, int index, BoardLayers layers) =>
        ComposePlateRow(row, focused, pressed, index, layers);

    OriginalRow IOriginalScreenHost.PlaqueRow(string key, string label, int row, bool enabled, int column) =>
        TextButton(key, label, OptionsX, OptionsTop + (row * OptionsPitch), enabled, column);

    void IOriginalScreenHost.ComposePlainPage(string heading, IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        layers.Lines.Add(new BoardLine(heading, OptionsX, OptionsTop - 44f, 0f, HeadingFont, BoardInk.Heading));
        ComposeRows(rows, focus, layers);
    }

    BoardFill IOriginalScreenHost.FocusMark(OriginalRow row) => FocusBox(row);
}
