using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Flight;
using CSVM.Net;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The Original presentation: <see cref="OriginalShell"/> drawn through a <see cref="ComposedBoardView"/>
/// on the board layer, registered under <see cref="PresentationId.Original"/>. <see cref="Activate"/>
/// builds the layer on the first call, refreshes the shared roster from the saved-plane store and
/// stands the shell on the destination's screen on every call; <see cref="Tick"/> keeps the pad
/// roster in step, claims seat 0's pad while joining is closed, scans the join gesture on the
/// screens the shell opens it on (<see cref="OriginalShell.JoiningOpen"/>), polls every seat, maps a
/// pointer from window pixels into the authored space through <see cref="BoardFit"/>, steps the
/// shell per seat, requests its cues and hands its exit to the host. The OS pointer is hidden
/// while the presentation is on screen, since the shell draws the original's own.
/// </summary>
public sealed class OriginalPresentation : IMenuPresentation
{
    /// <summary>The aid value that opens the Free Flight screen.</summary>
    public const string FreeFlightAid = "free-flight";

    /// <summary>The aid value that opens the Dogfight screen.</summary>
    public const string DogfightAid = "dogfight";

    /// <summary>The Free Flight aid's argument that picks the first chapter and aircraft for
    /// seat 0, so with <c>--debug-join</c> the per-seat aircraft screen shows.</summary>
    public const string SeatPlaneAid = "seat-plane";

    /// <summary>The aid value that opens the Options screen.</summary>
    public const string OptionsAid = "options";

    /// <summary>The aid value that opens the Game Options page behind it.</summary>
    public const string GameOptionsAid = "game-options";

    /// <summary>The Game Options aid's argument that leaves its Difficulty dropdown standing open.</summary>
    public const string GameOptionsOpenAid = "open";

    /// <summary>The aid value that opens the AUDIO page behind the Preferences page's second door.</summary>
    public const string AudioAid = "audio";

    /// <summary>The AUDIO aid's argument that stands its four sliders at four distinct levels.</summary>
    public const string AudioMixedAid = "mixed";

    /// <summary>The aid value that opens the VIDEO page behind the Preferences page's third door.</summary>
    public const string VideoAid = "video";

    /// <summary>The VIDEO aid's argument that leaves its Enhanced Graphics checkbox checked.</summary>
    public const string VideoCheckedAid = "checked";

    /// <summary>The VIDEO aid's argument that leaves the Resolution list standing open, the one
    /// leaf list whose items can outrun the window its own row authors.</summary>
    public const string VideoOpenAid = "open";

    /// <summary>The aid value that opens the CONTROLS page behind the Preferences page's fourth
    /// door.</summary>
    public const string ControlsAid = "controls";

    /// <summary>The aid value that opens the KEYS AND BUTTONS page behind the CONTROLS page's own
    /// door, on its first category tab.</summary>
    public const string KeysAid = "keys";

    /// <summary>The KEYS aid's argument that stands the page on its last category, the one whose
    /// rows outrun the list's window. Any other category name works the same way, spaces and case
    /// ignored, so every tab has a shot of its own.</summary>
    public const string KeysOtherAid = "other";

    /// <summary>The aid value that opens the credits screen behind the top level's fifth row.</summary>
    public const string CreditsAid = "credits";

    /// <summary>The credits aid's argument that leaves the About box standing over the screen.</summary>
    public const string CreditsAboutAid = "about";

    /// <summary>The aid value that opens the Instant Action screen.</summary>
    public const string InstantActionAid = "instant-action";

    /// <summary>The Instant Action aid's argument that leaves its Pilot Plane list standing open.</summary>
    public const string InstantActionPilotPlaneAid = "pilot-plane";

    /// <summary>The Instant Action aid's argument that opens its Weapon Loadout for the pilot.</summary>
    public const string InstantActionLoadoutAid = "weapon-loadout";

    /// <summary>The Instant Action aid's argument that stands its remake-only lives control at the
    /// count a further <c>:n</c> names, and at Unlimited when it names none.</summary>
    public const string InstantActionLivesAid = "lives";

    /// <summary>The aid value that opens the Instant Action wrap-up page on a sample completed run,
    /// the page a flown mission's ending lands on. Original's own: Built-in shows a board inside
    /// the flight instead and has no menu page to open.</summary>
    public const string InstantActionWrapupAid = "instant-action-wrapup";

    /// <summary>The wrap-up aid's argument that shows the failed outcome instead of the complete
    /// one, the two headlines being the page's only difference between them.</summary>
    public const string InstantActionWrapupFailedAid = "failed";

    /// <summary>The wrap-up aid's argument that shows a seventeen-zone stunt run, the longest the
    /// install ships, whose splits take more than one post-it.</summary>
    public const string InstantActionWrapupLongAid = "long";

    /// <summary>The wrap-up aid's argument that shows the sample run with a stand-in photograph
    /// per zone, every one landed.</summary>
    public const string InstantActionWrapupPhotosAid = "photos";

    /// <summary>The wrap-up aid's argument that shows the seventeen-zone run with a stand-in
    /// photograph per zone, the last one still on its way.</summary>
    public const string InstantActionWrapupLongPhotosAid = "long-photos";

    /// <summary>The aid value that opens the hangar's name screen on a fresh build.</summary>
    public const string PlaneNameAid = "plane-name";

    /// <summary>The aid value that opens the Plane Construction hub on its airframe tab.</summary>
    public const string PlaneConstructionAid = "plane-construction";

    /// <summary>The Plane Construction aid's argument that leaves its airframe list standing open
    /// with a row other than the standing pick under the cursor, so the shot shows the hub's
    /// figures previewing that row.</summary>
    public const string PlaneConstructionOpenAid = "open";

    /// <summary>The Plane Construction aid's argument that builds past the airframe's weight
    /// capacity, so the shot shows the reddened CURRENT WEIGHT.</summary>
    public const string PlaneConstructionOverweightAid = "overweight";

    /// <summary>The Plane Construction aid's argument that leaves the airframe-defaults question
    /// standing: an engine is hand-picked so the build is edited, then the airframe swapped, which
    /// is the only way the question is raised.</summary>
    public const string PlaneConstructionDefaultsAid = "defaults";

    /// <summary>The aid value that opens the hub's paint tab on a Fury in Fortune Hunters colours.</summary>
    public const string PlanePaintAid = "plane-paint";

    /// <summary>The paint aid's argument that leaves the nose decal list standing open, which is
    /// the five-across grid of tiles over the page rather than a list under its box.</summary>
    public const string PlanePaintDecalsAid = "decals";

    /// <summary>The aid value that opens the hub's construction totals page.</summary>
    public const string PlanePurchaseAid = "plane-purchase";

    /// <summary>The aid value that opens the hangar's inventory.</summary>
    public const string PlaneInventoryAid = "plane-inventory";

    /// <summary>The aid value that opens the two-player profile screen with DELETE PLAYER pressed,
    /// the two-answer messagebox standing over it; Original's own, since Built-in's confirm is two
    /// rows of the screen and not a box.</summary>
    public const string CampaignDeleteAid = "campaign-delete";

    /// <summary>The aid value that opens the cabin with its network door open over the aids'
    /// loopback door. Its colon argument is how many guests are on the wire.</summary>
    public const string CampaignCoopAid = "campaign-coop";

    /// <summary>The aid value that shows a joined co-op guest following the aids' loopback host.
    /// Its colon argument names the host's board: cabin (the default), briefing, flightcheck,
    /// ready, planeselection, ammo or debrief.</summary>
    public const string CampaignCoopGuestAid = "campaign-coop-guest";

    /// <summary>The aid value that shows a co-op host's flight check with two guests on the aids'
    /// loopback wire. The first is Ready and the second not, so FLY MISSION waits.</summary>
    public const string CampaignCoopReadyAid = "campaign-coop-ready";

    /// <summary>The aid value that opens the Multiplayer Connection page.</summary>
    public const string ConnectionAid = "connection";

    /// <summary>The aid value that opens the games list over a LAN answering with the aids' sample
    /// games.</summary>
    public const string ConnectionGamesAid = "connection-games";

    /// <summary>The <see cref="ConnectionGamesAid"/> argument for a LAN that answers nothing, so the
    /// Searching box stands.</summary>
    public const string ConnectionSearchingAid = "searching";

    /// <summary>The aid value that opens the Multiplayer Lobby over the aids' loopback wire with two
    /// guests on it. Its first colon argument names the view: host (the default), guest (Ready) or
    /// waiting (a guest not yet Ready). The second names the tab: mission (the default), plane,
    /// ammo, rockets or scores, which lands a finished match first.</summary>
    public const string LobbyAid = "lobby";

    /// <summary>The campaign aid values Original shares with Built-in, each over the scratch
    /// profile store: the empty profile screen, the two-player one, the cabin, the table of
    /// contents, the book on the last mission flown, the briefing (with its seconds argument),
    /// the flight check, ammo selection, plane selection and the hangar over the profile's wallet
    /// (with a tab argument); plus Original's own <see cref="CampaignDeleteAid"/>.</summary>
    public static readonly IReadOnlyList<string> CampaignAids = new[]
    {
        "campaign-empty", "campaign-roster", "campaign-cabin", "campaign-previous", "campaign-scrapbook",
        "campaign-briefing", "campaign-flightcheck", "campaign-ammo", "campaign-planeselection", "campaign-hangar",
        CampaignDeleteAid, CampaignCoopAid, CampaignCoopGuestAid, CampaignCoopReadyAid,
    };

    /// <summary>The cabin's palette: the shared cabin board's, with the mission pull-down's words
    /// in the paper forms' list inks. <c>PASSENGERCABIN.SCRIPT</c> gives <c>pc_d_missions</c> the
    /// <c>@shareditems@BUA</c> list, the one the plane selection and ammo screens take, which prints
    /// black on its <c>0xffc8d4e6</c> paper whatever screen it stands on
    /// (<c>docs/formats/campaign-screens.md</c>, "The mission cheat").</summary>
    public static readonly BoardPalette CabinPalette = BoardPalette.Panel with
    {
        Row = BoardPalette.Paper.Row,
        Focus = BoardPalette.Paper.Focus,
    };

    // The aids' scratch build carries this name, so the shots read the same on every machine; it
    // is never committed by an aid.
    private const string AidPlaneName = "Sample Plane";

    // The briefing aid's reveal is advanced in frame-sized slices, since the script blocks on
    // authored waits and cue points and one large step would stand at the first of them.
    private const double AidSlice = 1.0 / 60.0;

    // The campaign-hangar aid's tab argument, each the hub screen it opens on over the wallet.
    private static readonly IReadOnlyDictionary<string, OriginalScreen> CampaignHangarTabs =
        new Dictionary<string, OriginalScreen>(StringComparer.OrdinalIgnoreCase)
        {
            ["airframe"] = OriginalScreen.HangarAirframe,
            ["engine"] = OriginalScreen.HangarEngine,
            ["armor"] = OriginalScreen.HangarArmor,
            ["guns"] = OriginalScreen.HangarGuns,
            ["hardpoints"] = OriginalScreen.HangarHardpoints,
            ["paint"] = OriginalScreen.HangarPaint,
            ["purchase"] = OriginalScreen.HangarPurchase,
        };

    private readonly Node _parent;
    private readonly string _dataRoot;
    private readonly MenuLayout _layout;
    private readonly MenuInput _player1;
    private readonly OriginalArtSizes _sizes;
    // Consumed by the first Activate: an aid names a screen a shot wants, never where a return lands.
    private string _aid;
    private int _debugJoin;
    private CanvasLayer? _layer;
    private ComposedBoardView? _view;
    private ShotViewer? _shotViewer;
    private OriginalShell? _shell;
    private MenuSeatDevices? _devices;
    private IMenuHost? _host;
    private BoardPalette _palette = BoardPalette.Chalk;
    private BoardPalette _preferencesPalette = BoardPalette.Chalk;
    private BoardPalette _paperPalette = BoardPalette.Paper;
    private BoardPalette _hangarPalette = BoardPalette.Paper;
    private bool _shown;
    private bool _joiningOpen;
    private bool _debugPointerDown;
    // The rebinding pages' seat bookkeeping, null where no shared controls feature is registered.
    private MenuControlsSeats? _controlsSeats;

    // The narration bookkeeping: how many starts the briefing had asked for when playback last
    // began (0 while nothing plays), and whether the reveal was running last frame, so the frame
    // it finishes still repaints.
    private int _narrationStarts;
    private bool _revealRunning;

    /// <summary>A presentation drawing under <paramref name="parent"/> over the art beneath
    /// <paramref name="dataRoot"/>, composed from <paramref name="layout"/>, opening its first show
    /// on <paramref name="aid"/> (an Original <c>--menu=</c> value or "") and every later one on
    /// the destination itself, binding pads over seat 0's poller <paramref name="player1"/>, and seating
    /// <paramref name="debugJoin"/> device-less players once, the screenshot aid.</summary>
    public OriginalPresentation(Node parent, string dataRoot, MenuLayout layout, string aid, MenuInput player1, int debugJoin = 0)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _aid = aid ?? string.Empty;
        _player1 = player1 ?? throw new ArgumentNullException(nameof(player1));
        _debugJoin = debugJoin;
        _sizes = new OriginalArtSizes(_dataRoot, "original presentation");
    }

    public PresentationId Id => PresentationId.Original;

    /// <summary>The shell while built, for the suites that read the screen back.</summary>
    public OriginalShell? Shell => _shell;

    /// <summary>The screenshot aid's pointer (<c>--debug-pointer=</c>) in authored pixels, which
    /// stands in for seat 0's own for the run: a rollover and a held plaque are then shot with
    /// nobody at the controls. Null leaves the mouse alone.</summary>
    public (float X, float Y, bool Down, bool Right)? DebugPointer { get; set; }

    /// <summary>The pad bookkeeping while built, for the suites that read seat 0's claim back.</summary>
    public MenuSeatDevices? Devices => _devices;

    /// <summary>The profile store the Campaign row and the two flight returns open the campaign
    /// over: <c>user://Profiles</c> unless a suite sets a scratch store here, so no driven journey
    /// can write a real player's progress. Read on every open, so it may be set before the first
    /// show or between shows.</summary>
    public CampaignProfileStore? CampaignProfiles { get; set; }

    /// <summary>The viewer showing the wrap-up page's open photograph, for a suite reading it back;
    /// null while the presentation has no layer.</summary>
    internal ShotViewer? PhotoViewer => _shotViewer;

    /// <summary>The board palette the shell's inks resolve to: list text in the file-wide
    /// disabled grey with the active white for the focused row, plaque labels in the paper
    /// button's own three colours.</summary>
    public static BoardPalette PaletteFor(OriginalInks inks) => new(
        Row: ToColor(inks.Disabled),
        Focus: ToColor(inks.Active),
        Heading: ToColor(inks.Active),
        Detail: ToColor(inks.Disabled),
        LabelNormal: ToColor(inks.LabelNormal),
        LabelRollover: ToColor(inks.LabelRollover),
        LabelActivate: ToColor(inks.LabelDepressed),
        Hint: ToColor(inks.Disabled));

    /// <summary>The Options screen's palette, taken from the Preferences page's authored inks.
    /// ⚠ Do not focus with the page title's colour; the focused row takes the file-wide
    /// <c>ACTIVE</c>. The title is duller than the description cream, so it dims what it
    /// highlights (<c>docs/menu-presentations.md</c>, <c>docs/org/menu-inventory.md</c>).</summary>
    public static BoardPalette PaletteFor(OriginalPreferencesInks inks, OriginalInks labels) => new(
        Row: ToColor(inks.Text),
        Focus: ToColor(labels.Active),
        Heading: ToColor(inks.Title),
        Detail: ToColor(inks.Text),
        LabelNormal: ToColor(labels.LabelNormal),
        LabelRollover: ToColor(labels.LabelRollover),
        LabelActivate: ToColor(labels.LabelDepressed),
        Hint: ToColor(inks.Text));

    /// <summary>The Instant Action screen's palette: every text in the screen's own authored text
    /// colour over its paper background, the button labels in the paper buttons' own tail.</summary>
    public static BoardPalette PaletteFor(OriginalInstantActionInks inks) => new(
        Row: ToColor(inks.Text),
        Focus: ToColor(inks.Text),
        Heading: ToColor(inks.Text),
        Detail: ToColor(inks.Text),
        LabelNormal: ToColor(inks.LabelNormal),
        LabelRollover: ToColor(inks.LabelRollover),
        LabelActivate: ToColor(inks.LabelDepressed),
        Hint: ToColor(inks.Text));

    /// <summary>The plane-construction screens' palette: the right page's authored black text with
    /// the title colour for headings, the tab bar's white labels with the standing tab in its
    /// disabled colour, the left page's white through the dialog ink.</summary>
    public static BoardPalette PaletteFor(OriginalHangarInks inks) => new(
        Row: ToColor(inks.Text),
        Focus: ToColor(inks.Title),
        Heading: ToColor(inks.Title),
        Detail: ToColor(inks.TabCurrent),
        LabelNormal: ToColor(inks.TabLabel),
        LabelRollover: ToColor(inks.TabLabel),
        LabelActivate: ToColor(inks.TabDepressed),
        Hint: ToColor(inks.Text));

    /// <summary>The roster both presentations pick from: the eleven stock airframes, then the
    /// saved customs, each flying its airframe's stock node.</summary>
    public static IReadOnlyList<MenuAircraft> Roster(IReadOnlyList<CustomPlaneDef> customs) =>
        OriginalRosters.Roster(customs);

    public void Activate(IMenuHost host, MenuReturnDestination destination)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        var setup = host.Features.Get<PlayerSetupFeature>();
        if (_shell == null)
        {
            _devices = new MenuSeatDevices(_player1, setup);
            _shell = new OriginalShell(_layout, host.Features.Get<FreeFlightFeature>(), setup, _sizes.Measure, _devices.FlightPads,
                instantAction: host.Features.Get<InstantActionFeature>(),
                hangar: host.Features.TryGet<HangarFeature>(out var hangar) ? hangar : null,
                planes: CustomPlaneStore.UserPlanes(),
                campaign: host.Features.TryGet<CampaignFeature>(out var campaign) ? campaign : null,
                profiles: () => CampaignProfiles ?? CampaignProfileStore.UserProfiles(),
                stock: () => StockLoadouts.Load(),
                dataRoot: _dataRoot,
                options: () => OptionsStore.UserOptions().Load(),
                screenSizes: ResolutionSetting.ScreenSizes,
                screens: MonitorSetting.Screens,
                controls: host.Features.TryGet<ControlsFeature>(out var controls) ? controls : null,
                net: host.Features.TryGet<NetPlayFeature>(out var net) ? net : null);
            _controlsSeats = host.Features.TryGet<ControlsFeature>(out var rebinds) ? new MenuControlsSeats(rebinds) : null;
            _palette = PaletteFor(_shell.Inks);
            _preferencesPalette = PaletteFor(_shell.PreferencesInks, _shell.Inks);
            _paperPalette = PaletteFor(_shell.InstantAction.Inks);
            if (_shell.Hangar != null)
            {
                _hangarPalette = PaletteFor(_shell.Hangar.Inks);
            }
        }

        if (_layer == null)
        {
            _layer = new CanvasLayer { Name = "original_menu", Layer = HudLayers.Board };
            _view = ComposedBoardView.Build(_dataRoot);
            _layer.AddChild(_view);
            // The shell polls its own pointer, so the viewer over the page takes no click.
            _shotViewer = ShotViewer.Build(clickCloses: false);
            _layer.AddChild(_shotViewer);
            _parent.AddChild(_layer);
        }

        // Re-read on every show, so a plane saved in the hangar or by another presentation is
        // offered without a restart; cursors past a shrunk roster come back onto it.
        setup.SetRoster(Roster(CustomPlaneStore.UserPlanes().List()));
        foreach (var seat in setup.Seats)
        {
            seat.Cursor = Math.Clamp(seat.Cursor, 0, Math.Max(0, setup.Roster.Count - 1));
        }

        _shell.ReturnToTopLevel();
        StopNarration();
        // Seated before the aid opens its screen, so a pose that walks the seats finds them.
        DebugJoin(setup);
        string aid = _aid;
        _aid = string.Empty;
        if (destination is CabinReturn cabin)
        {
            // The two flight returns reopen the campaign on the user's store and seat the profile
            // the mission wrote; a profile that cannot be read leaves the profile screen showing.
            _shell.Campaign.OpenCampaign();
            if (!_shell.Campaign.ShowCabin(cabin.Profile))
            {
                Log.Warn("ui", $"original presentation: cabin return could not seat '{cabin.Profile}'; the profile screen shows instead");
            }
        }
        else if (destination is InstantActionReturn)
        {
            // The sortie's settings are the feature's, which outlives the flight, so the screen
            // stands on them again; the roster re-read is the door's own.
            _shell.InstantAction.OpenInstantAction();
        }
        else if (destination is InstantActionWrapupReturn wrapup)
        {
            // The numbers are final at the ending and travel with the destination. The session
            // that counted them is already gone by the time this page draws.
            _shell.Wrapup.ShowWrapup(wrapup.Snapshot);
        }
        else if (destination is DebriefReturn debrief)
        {
            _shell.Campaign.OpenCampaign();
            if (!_shell.Campaign.ShowScrapbook(debrief.Profile, debrief.MissionSeq, debrief.MissionWon))
            {
                Log.Warn("ui", $"original presentation: debrief return could not seat '{debrief.Profile}'; the profile screen shows instead");
            }
        }
        else if (destination is CoopGuestReturn guest)
        {
            // Back onto the host's boards, or onto the Connection page saying why the link ended.
            if (!_shell.Campaign.ShowGuestDebrief(guest.Attempt))
            {
                _shell.ReturnToConnection();
            }
        }
        else if (destination is LobbyReturn landing)
        {
            // The lobby the match was launched from, on its scores, or the Connection page saying
            // why the link ended.
            if (!_shell.Lobby.Land(landing.Scores))
            {
                _shell.ReturnToConnection();
            }
        }
        else if (aid.Length > 0 && OpenCampaignAid(aid))
        {
            // A campaign aid over the scratch store, shared with Built-in's aids of the same name.
        }
        else
        {
            switch (aid)
            {
                case CampaignAidProfiles.PlayerDoor:
                    // The player's own door, over the presentation's store and never the scratch one.
                    _shell.Campaign.OpenCampaign();
                    break;
                case FreeFlightAid:
                    _shell.Open(OriginalScreen.FreeFlight);
                    break;
                case FreeFlightAid + ":" + SeatPlaneAid:
                    _shell.Open(OriginalScreen.FreeFlight);
                    _shell.PoseSortiePick();
                    break;
                case DogfightAid:
                    _shell.Open(OriginalScreen.Dogfight);
                    break;
                case OptionsAid:
                    _shell.Open(OriginalScreen.Options);
                    break;
                case GameOptionsAid:
                case GameOptionsAid + ":" + GameOptionsOpenAid:
                    OpenGameOptionsAid(aid);
                    break;
                case AudioAid:
                    _shell.Options.OpenAudio();
                    break;
                case AudioAid + ":" + AudioMixedAid:
                    // The four rows open on two levels between them, so a shot of the shipped mix
                    // says nothing about where a thumb stands at a level it was moved to.
                    _shell.Options.OpenAudio();
                    _shell.Options.PoseAudioMix();
                    break;
                case VideoAid:
                    _shell.Options.OpenVideo();
                    break;
                case VideoAid + ":" + VideoCheckedAid:
                    // Onto the checkbox by name: the page opens on its first row, which is a
                    // display setting rather than the graphics one this pose is about.
                    _shell.Options.OpenVideoOn(OriginalOptionsScreen.GraphicsKey);
                    _shell.Step(new MenuCommands { Accept = true });
                    break;
                case VideoAid + ":" + VideoOpenAid:
                    // Onto the Resolution row by name: the page opens on the monitor row above it,
                    // whose one screen on this machine says nothing about a windowed list.
                    _shell.Options.OpenVideoOn(OriginalOptionsScreen.ResolutionKey);
                    _shell.Step(new MenuCommands { Accept = true });
                    break;
                case ControlsAid:
                    SyncControlsSeats();
                    _shell.Options.OpenControlsPrefs();
                    break;
                case KeysAid:
                    SyncControlsSeats();
                    _shell.Options.OpenKeys();
                    break;
                case string keys when keys.StartsWith(KeysAid + ":", StringComparison.Ordinal):
                    SyncControlsSeats();
                    _shell.Options.OpenKeys();
                    _shell.Options.ShowKeysTab(KeysTabOf(keys[(KeysAid.Length + 1)..]));
                    break;
                case CreditsAid:
                    _shell.Open(OriginalScreen.Credits);
                    break;
                case ConnectionAid:
                    _shell.Connection.OpenConnection();
                    break;
                case ConnectionGamesAid:
                case ConnectionGamesAid + ":" + ConnectionSearchingAid:
                    _shell.StandInNetDoor(NetDoorAid.Searching(silent: aid.EndsWith(ConnectionSearchingAid, StringComparison.Ordinal)));
                    _shell.Connection.OpenConnection();
                    _shell.Connection.SearchLan();
                    _shell.StepNet(0.0);
                    break;
                case string lobby when lobby == LobbyAid || lobby.StartsWith(LobbyAid + ":", StringComparison.Ordinal):
                    OpenLobbyAid(lobby[LobbyAid.Length..].TrimStart(':'));
                    break;
                case CreditsAid + ":" + CreditsAboutAid:
                    // The screen opens on ABOUT, its first row, so one accept raises the box.
                    _shell.Open(OriginalScreen.Credits);
                    _shell.Step(new MenuCommands { Accept = true });
                    break;
                case InstantActionAid:
                    _shell.InstantAction.OpenInstantAction();
                    break;
                case InstantActionAid + ":" + InstantActionPilotPlaneAid:
                    _shell.InstantAction.OpenInstantAction();
                    _shell.InstantAction.OpenDropdownOn(OriginalInstantActionScreen.PlayerPlaneKey);
                    break;
                case InstantActionAid + ":" + InstantActionLoadoutAid:
                    _shell.InstantAction.OpenInstantAction();
                    _shell.InstantAction.OpenLoadout();
                    break;
                case string lives when lives.StartsWith(InstantActionAid + ":" + InstantActionLivesAid, StringComparison.Ordinal):
                    // The screen opens on one life, so neither the unlimited reading nor a count
                    // above one is a state a plain shot of it can show.
                    _shell.InstantAction.OpenInstantAction();
                    _shell.InstantAction.PoseLives(AidLives(lives));
                    break;
                case InstantActionWrapupAid:
                    _shell.Wrapup.ShowWrapup(InstantActionWrapupPage.Sample(won: true));
                    break;
                case InstantActionWrapupAid + ":" + InstantActionWrapupFailedAid:
                    _shell.Wrapup.ShowWrapup(InstantActionWrapupPage.Sample(won: false));
                    break;
                case InstantActionWrapupAid + ":" + InstantActionWrapupLongAid:
                    _shell.Wrapup.ShowWrapup(InstantActionWrapupPage.LongSample());
                    break;
                case InstantActionWrapupAid + ":" + InstantActionWrapupPhotosAid:
                    _shell.Wrapup.ShowWrapup(WithSamplePhotos(InstantActionWrapupPage.Sample(won: true), pending: 0));
                    break;
                case InstantActionWrapupAid + ":" + InstantActionWrapupLongPhotosAid:
                    _shell.Wrapup.ShowWrapup(WithSamplePhotos(InstantActionWrapupPage.LongSample(), pending: 1));
                    break;
                case PlaneNameAid:
                    _shell.OpenHangar();
                    break;
                case PlaneConstructionAid:
                    _shell.OpenHangarTab(OriginalScreen.HangarAirframe, AidPlaneName);
                    break;
                case PlaneConstructionAid + ":" + PlaneConstructionOpenAid:
                    // The airframe list open on a row that is not the standing pick, the pose the
                    // hub's previewed figures need, reached by the keyboard walk a pilot has.
                    _shell.OpenHangarTab(OriginalScreen.HangarAirframe, AidPlaneName);
                    _shell.Step(new MenuCommands { Accept = true });
                    _shell.Step(new MenuCommands { MoveY = 1 });
                    break;
                case PlaneConstructionAid + ":" + PlaneConstructionDefaultsAid:
                    // The airframe swap's own question, which only an edited build raises: a
                    // hand-picked engine is the edit and the list's next row is the swap, taken
                    // through the same presses a pilot has.
                    _shell.OpenHangarTab(OriginalScreen.HangarAirframe, AidPlaneName);
                    if (host.Features.TryGet<HangarFeature>(out var asked) && asked.IsOpen)
                    {
                        asked.SetEngine(2);
                    }

                    _shell.Step(new MenuCommands { Accept = true });
                    _shell.Step(new MenuCommands { MoveY = 1 });
                    _shell.Step(new MenuCommands { Accept = true });
                    break;
                case PlaneConstructionAid + ":" + PlaneConstructionOverweightAid:
                    // A build past its airframe's capacity: the lightest airframe carrying every
                    // armour press and every hardpoint, which is the state the weight line reddens
                    // on and which no default build reaches.
                    _shell.OpenHangarTab(OriginalScreen.HangarHardpoints, AidPlaneName);
                    if (host.Features.TryGet<HangarFeature>(out var heavy) && heavy.IsOpen)
                    {
                        heavy.PickAirframe(0);
                        for (int zone = 0; zone < 4; zone++)
                        {
                            heavy.SetArmour(zone, CustomPlaneDef.MaxArmourUnits);
                        }

                        heavy.SetHardpoints(0, CustomPlaneDef.MaxHardpointsPerWing);
                        heavy.SetHardpoints(1, CustomPlaneDef.MaxHardpointsPerWing);
                    }

                    break;
                case PlanePaintAid:
                case PlanePaintAid + ":" + PlanePaintDecalsAid:
                    // The same pose as Built-in's paint aid: a Fury in Fortune Hunters colours with
                    // a nose decal chosen, so the two presentations' shots show one plane.
                    _shell.OpenHangarTab(OriginalScreen.HangarPaint, AidPlaneName);
                    if (host.Features.TryGet<HangarFeature>(out var paint) && paint.IsOpen)
                    {
                        paint.PickAirframe(7);
                        paint.SetPattern(4);
                        paint.SetDecal(0, 40);
                    }

                    if (aid.EndsWith(PlanePaintDecalsAid, StringComparison.Ordinal))
                    {
                        _shell.Hangar?.OpenHangarDropdownOn(OriginalHangarScreen.NoseDecalKey);
                    }

                    break;
                case PlanePurchaseAid:
                    _shell.OpenHangarTab(OriginalScreen.HangarPurchase, AidPlaneName);
                    break;
                case PlaneInventoryAid:
                    _shell.OpenHangarTab(OriginalScreen.HangarInventory, AidPlaneName);
                    break;
            }
        }

        foreach (var seat in host.Seats)
        {
            seat.Prime();
        }

        _devices!.Sync(0f);
        _devices.PrimeJoins();
        _joiningOpen = _shell.JoiningOpen;
        if (!_joiningOpen)
        {
            _devices.ClaimP1Pad();
        }

        _layer.Visible = true;
        _shown = true;
        Input.MouseMode = Input.MouseModeEnum.Hidden;
        Redraw();
    }

    public void Tick(float dt)
    {
        if (!_shown || _shell == null || _host == null || _devices == null || _host.Seats.Count == 0 || _view == null)
        {
            return;
        }

        bool changed = _devices.Sync(dt);
        bool joining = _shell.JoiningOpen;
        if (joining && !_joiningOpen)
        {
            _devices.PrimeJoins();
        }

        _joiningOpen = joining;
        if (joining)
        {
            changed |= _devices.ScanJoins();
        }
        else
        {
            // While joining is closed the pad steering seat 0 becomes seat 0's for good, so once a
            // screen opens joining every other pad is unambiguously a joiner.
            changed |= _devices.ClaimP1Pad();
        }

        // Before the poll, so a pad that joined this frame already has its player row and a seat
        // whose pad left has lost one before anything reads the keymaps.
        if (_shell.Screen is OriginalScreen.ControlsPrefs or OriginalScreen.Keys)
        {
            SyncControlsSeats();
        }

        var size = _view.GetViewportRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        changed |= TickBriefing(dt);
        // The network door is stepped every frame whatever shows. A guest arriving, a search
        // answer or a hang-up then lands without waiting for a screen to ask.
        changed |= _shell.StepNet(dt);
        if (_shell.TakeNetExit() is { } launched)
        {
            _host.Audio.EndMixPreview();
            _host.Exit(launched);
            return;
        }

        // Text capture is set before the poll: the name screen's letters must be text, not
        // cursor aliases, for the frame that reads them.
        _host.Seats[0].CapturingText = _shell.CapturingText;
        // The seat list is live and a Back can shorten it mid-loop, so the count is re-read.
        for (int i = 0; i < _host.Seats.Count; i++)
        {
            var commands = _host.Seats[i].Poll(dt);
            if (commands.Pointer is { } pointer)
            {
                commands = commands with
                {
                    Pointer = pointer with
                    {
                        X = (pointer.X - fit.OriginX) / fit.Scale,
                        Y = (pointer.Y - fit.OriginY) / fit.Scale,
                    },
                };
            }

            // The aid's pointer is already in the authored space, so it replaces seat 0's mapped
            // one rather than being mapped again. Its press arrives as an edge once, which is what
            // arms the plaque under it; it is never released, so the held state is what gets shot.
            if (i == 0 && DebugPointer is { } aid)
            {
                bool edge = aid.Down && !_debugPointerDown;
                _debugPointerDown = aid.Down;
                commands = commands with { Pointer = new MenuPointer(aid.X, aid.Y, aid.Down, edge, 0, aid.Right) };
            }

            var step = _shell.StepSeat(i, commands);
            foreach (string cue in step.Cues)
            {
                _host.Audio.Cue(new MenuCue(cue));
            }

            if (step.Exit != null)
            {
                // The exit ends this presentation, so a mix page's preview goes before the host acts
                // on it: an accepted mix is applied by Launcher.ApplyOptions, and every other door
                // owes back the mix the page opened over.
                _host.Audio.EndMixPreview();
                _host.Exit(step.Exit);
                return;
            }

            changed |= step.Changed;
        }

        // Leaving the briefing this frame, by any door, ends its narration and lifts the duck.
        if (_shell.Screen != OriginalScreen.CampaignBriefing)
        {
            StopNarration();
        }

        // The AUDIO page's levels are heard while it is open and the mix it opened over goes back the
        // moment it is left, by any door. Read off the shell rather than a seat's step: the page is
        // seat 0's, and a guest's own step carries no mix, so its poll would end the preview.
        if (_shell.Options.AudioPreviewMix is { } mix)
        {
            _host.Audio.PreviewMix(mix, _shell.Options.TakeAudioMoved());
        }
        else
        {
            _host.Audio.EndMixPreview();
        }

        // And again after the frame, so a screen change this frame is what the next poll reads.
        _host.Seats[0].CapturingText = _shell.CapturingText;
        // The same for the player rows, so the press that opened a rebinding page leaves it already
        // holding its seats rather than blank until the next frame.
        if (_shell.Screen is OriginalScreen.ControlsPrefs or OriginalScreen.Keys)
        {
            SyncControlsSeats();
        }

        // The board's own movies run on the step the host was given. A new picture repaints without
        // recomposing: nothing about the screen changed, only the pixels behind it.
        bool picture = _view.AdvanceMovies(dt);
        // An edit box's caret blinks on the same step, and repaints for the same reason: the
        // screen has not changed, only the pixels the box draws.
        picture |= _view.AdvanceCaret(dt);
        // A photograph landing after the wrap-up page woke fills its print, which is a new picture
        // on the board rather than new pixels in one already drawn, so the page is composed again.
        changed |= _shell.Screen == OriginalScreen.InstantActionWrapup && _shell.Wrapup.TakeLanded();
        SyncShotViewer();
        if (changed)
        {
            Redraw();
        }
        else if (picture)
        {
            _view.QueueRedraw();
        }
    }

    public void Hide()
    {
        _shown = false;
        if (_layer != null)
        {
            _layer.Visible = false;
        }

        _shotViewer?.Close();

        // Off screen nothing types, so a seat left capturing on the name screen is released, and
        // nothing narrates: a launch from the briefing's flight check ends the voice with it.
        if (_host is { Seats.Count: > 0 } host)
        {
            host.Seats[0].CapturingText = false;
        }

        StopNarration();
        // Off screen the AUDIO page's preview goes with it, the mix it opened over put back: a hide
        // is a door out that no frame follows.
        _host?.Audio.EndMixPreview();
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void Deactivate()
    {
        Hide();
        _host = null;
        if (_layer == null)
        {
            return;
        }

        _parent.RemoveChild(_layer);
        _layer.QueueFree();
        _layer = null;
        _view = null;
        _shotViewer = null;
    }

    private static Color ToColor(MenuLayoutColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1f);

    // The lives aid's count: the digits after a further colon, else 0, the unlimited reading. The
    // feature clamps a count past its cap, so an out-of-range aid poses the cap rather than failing.
    private static int AidLives(string aid)
    {
        int colon = aid.LastIndexOf(':');
        return colon > 0 && int.TryParse(
            aid[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lives) ? lives : 0;
    }

    // Which category the KEYS aid's argument names, matched against the tab captions themselves
    // with spaces and case ignored so "views1" reaches "Views 1". An argument that names no tab
    // leaves the page on its first category, which is where the bare aid opens it anyway.
    private static int KeysTabOf(string argument)
    {
        var tabs = OriginalOptionsScreen.ControlTabs;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (string.Equals(tabs[i].Name.Replace(" ", string.Empty), argument, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    // One stand-in photograph per zone the sample completed, the last pending ones never landing.
    // Each is a sky over a tilted horizon in its own hue, so the prints tell apart in a shot.
    private static IaWrapupSnapshot WithSamplePhotos(IaWrapupSnapshot sample, int pending)
    {
        var shots = new List<StuntShot>();
        for (int i = 0; i < sample.ZonesCompleted; i++)
        {
            var shot = new StuntShot { DzName = $"dz{i + 1}", At = i, Path = $"sample_dz{i + 1}.png" };
            if (i < sample.ZonesCompleted - pending)
            {
                shot.Thumb = SamplePhoto(i);
                shot.Landed = true;
            }

            shots.Add(shot);
        }

        return sample with { Shots = shots };
    }

    private static Image SamplePhoto(int index)
    {
        const int width = StuntCapture.ThumbWidth, height = StuntCapture.ThumbWidth * 9 / 16;
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgb8);
        var sky = Color.FromHsv(0.55f + (index * 0.037f % 0.2f), 0.45f, 0.9f);
        var ground = Color.FromHsv(0.25f + (index * 0.053f % 0.12f), 0.5f, 0.45f);
        float tilt = ((index % 5) - 2) * 0.12f;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float horizon = (height * 0.6f) + ((x - (width / 2f)) * tilt);
                image.SetPixel(x, y, y < horizon ? sky.Darkened(y / (float)height * 0.3f) : ground);
            }
        }

        return image;
    }

    // The Game Options aid's posed state, the keyboard walk that reaches it rather than a state the
    // page can only be put in from outside: Accept on the opening focus stands the Difficulty
    // dropdown's list open.
    // The rebinding pages' player rows, in step with the joined seats. Seat 0 reads the poller this
    // presentation was built on; every other seat is a one-pad poller of its own.
    private void SyncControlsSeats()
    {
        if (_controlsSeats == null || _host == null)
        {
            return;
        }

        var pollers = new List<MenuInput?>(_host.Seats.Count);
        for (int i = 0; i < _host.Seats.Count; i++)
        {
            pollers.Add(i == 0 ? _player1 : MenuSeatDevices.PollerOf(_host.Seats[i]));
        }

        _controlsSeats.Sync(pollers);
    }

    // The lobby posed over the aids' wire. The shown door is opened through the screen itself, so it
    // goes by the pilot's name the way a player's own lobby does.
    private void OpenLobbyAid(string argument)
    {
        string[] parts = argument.Split(':');
        bool waiting = parts[0] == "waiting";
        bool guestView = parts[0] == "guest" || waiting;
        string tab = parts.Length > 1 ? parts[1] : parts[0] is "host" or "guest" or "waiting" ? string.Empty : parts[0];
        var (host, guests) = NetDoorAid.DogfightDoors();
        var shown = guestView ? guests[waiting ? 1 : 0] : host;
        _shell!.StandInNetDoor(shown);
        if (guestView)
        {
            host.OpenDogfightHost(NetSeats.MaxPlayers - 1);
        }
        else
        {
            _shell.Lobby.OpenHost();
        }

        foreach (var guest in guests)
        {
            guest.OpenJoin();
        }

        NetDoorAid.SettleDogfight(host, guests);
        if (guestView)
        {
            _shell.Lobby.OpenGuest();
        }

        NetDoorAid.PoseDogfight(host, guests);
        if (tab == "scores")
        {
            // Game Scores fills only on the way back from a match, so every door lands one.
            var scores = NetDoorAid.PlayedScores(host);
            foreach (var door in guests.Prepend(host).Where(door => door != shown))
            {
                door.Dogfight?.Land(scores);
            }

            _shell.Lobby.Land(scores);
            NetDoorAid.SettleDogfight(host, guests);
        }

        _shell.Lobby.ShowTab(
            tab switch
            {
                "plane" => LobbyTab.Plane,
                "ammo" or "rockets" => LobbyTab.Ammo,
                "scores" => LobbyTab.Scores,
                _ => LobbyTab.Mission,
            },
            rockets: tab == "rockets");
        _shell.StepNet(0.0);
    }

    private void OpenGameOptionsAid(string aid)
    {
        _shell!.Options.OpenGameOptions();
        if (aid.IndexOf(':') >= 0)
        {
            _shell.Step(new MenuCommands { Accept = true });
        }
    }

    // The briefing's clock and its narration, the two things the shared feature leaves to the
    // presentation: the reveal moves on by the frame, playback begins whenever the script asks
    // for its narration again (REPLAY BRIEFING raises the count), and the board repaints while
    // the reveal runs and once more when it stops. Returns whether to redraw.
    private bool TickBriefing(float dt)
    {
        if (_shell == null || _host == null)
        {
            return false;
        }

        bool running = _shell.Campaign.AdvanceBriefing(dt);
        int starts = _shell.Campaign.NarrationStarts;
        if (starts != _narrationStarts && starts > 0)
        {
            _narrationStarts = starts;
            _host.Audio.BeginNarration(_shell.Campaign.NarrationWav);
        }

        bool repaint = running || _revealRunning;
        _revealRunning = running;
        return repaint;
    }

    // Ends the narration where one has begun, which lifts the music duck too; idempotent, so
    // every door out of the briefing and every hide can call it.
    private void StopNarration()
    {
        _revealRunning = false;
        if (_narrationStarts == 0)
        {
            return;
        }

        _narrationStarts = 0;
        _host?.Audio.EndNarration();
    }

    // A campaign aid: the same values and the same scratch store as Built-in's, so the two
    // presentations' shots show one seeded player. False for a value that is not one.
    private bool OpenCampaignAid(string aid)
    {
        if (_shell == null)
        {
            return false;
        }

        int colon = aid.IndexOf(':');
        string value = colon < 0 ? aid : aid[..colon];
        string argument = colon < 0 ? string.Empty : aid[(colon + 1)..];
        if (!CampaignAids.Contains(value))
        {
            return false;
        }

        bool seeded = value != "campaign-empty";
        _shell.Campaign.OpenCampaignOver(
            CampaignAidProfiles.Store(seeded, progressed: value != "campaign-roster"), CampaignAidProfiles.Planes());
        switch (value)
        {
            case CampaignDeleteAid:
                _shell.Campaign.ShowDeleteConfirm(CampaignAidProfiles.Pilot);
                break;
            case "campaign-cabin":
                // A leading typed word stands the mission pull-down up as typing it on the wall
                // would, and the script after it walks the cabin with the field there.
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                if (argument.StartsWith(CampaignCheats.MissionWord, StringComparison.Ordinal))
                {
                    _shell.Campaign.Cheats?.ShowMissionList();
                    argument = argument[CampaignCheats.MissionWord.Length..];
                }

                break;
            case CampaignCoopAid:
                // The cabin with its network door open over the aids' loopback door, the same pose
                // as Built-in's aid of this name. The colon argument is the guests on its wire.
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                int.TryParse(argument, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int guests);
                var door = NetDoorAid.Host(guests, out _);
                _shell.StandInNetDoor(door);
                NetDoorAid.OpenCoopHost(door, CampaignAidProfiles.MissionsFlown, localPlayers: 1);
                _shell.StepNet(0.0);
                argument = string.Empty;
                break;
            case CampaignCoopGuestAid:
                PoseCoopGuest(argument);
                argument = string.Empty;
                break;
            case CampaignCoopReadyAid:
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                var ready = NetDoorAid.Host(2, out _, out var guestEnds);
                _shell.StandInNetDoor(ready);
                NetDoorAid.OpenCoopHost(ready, CampaignAidProfiles.MissionsFlown, localPlayers: 1);
                _shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignFlightCheck);
                _shell.StepNet(0.0);
                NetDoorAid.AnswerReady(ready, guestEnds[0], _shell.Campaign.SeatedAirframe ?? HangarFeature.DefaultAirframe);
                _shell.StepNet(0.0);
                break;
            case "campaign-previous":
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                _shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignPreviousMissions);
                break;
            case "campaign-scrapbook":
                // The book as a finished mission leaves it: opened on the last mission this
                // profile flew. No win is reported, so the shot is the book and never the film.
                _shell.Campaign.ShowScrapbook(
                    CampaignAidProfiles.Pilot, Math.Max(0, CampaignAidProfiles.MissionsFlown - 1), missionWon: false);
                break;
            case "campaign-briefing":
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                _shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignBriefing);
                double.TryParse(argument, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds);

                for (double t = 0; t < seconds; t += AidSlice)
                {
                    _shell.Campaign.AdvanceBriefing(AidSlice);
                }

                break;
            case "campaign-flightcheck":
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                _shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignFlightCheck);
                break;
            case "campaign-ammo":
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                _shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignAmmo);
                break;
            case "campaign-planeselection":
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                _shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignPlaneSelection);
                break;
            case "campaign-hangar":
                // The cabin's own PLANE CONSTRUCTION press, so the shot carries the cash note: the
                // name screen bare, or the named tab on the aid's build.
                _shell.Campaign.ShowCabin(CampaignAidProfiles.Pilot);
                if (colon >= 0 && CampaignHangarTabs.TryGetValue(aid[(colon + 1)..], out var tab))
                {
                    _shell.OpenHangarTab(tab, AidPlaneName, _shell.Campaign.Wallet);
                }
                else
                {
                    _shell.OpenHangar(_shell.Campaign.Wallet);
                }

                break;
        }

        // The briefing spends its colon on the reveal's seconds and the hangar's names a tab, both
        // taken above; every other screen's is the script CampaignAidScript reads, the same words
        // Built-in's aids take, with export still that button's own press.
        if (value is not ("campaign-briefing" or "campaign-hangar") && !_shell.Campaign.RunAidScript(argument))
        {
            // A script this presentation cannot press leaves nothing worth shooting, so the run
            // ends before the capture takes a screen that looks like it simply did not respond.
            _parent.GetTree()?.Quit(1);
        }

        return true;
    }

    // The guest as the host's boards leave it, three humans on the wire and this guest the second.
    // The third is Ready on the check, so a shot shows another's mark beside this guest's own.
    private void PoseCoopGuest(string board)
    {
        var host = CampaignAidProfiles.Store(seeded: true, progressed: true).Load(CampaignAidProfiles.Pilot);
        ushort airframes = CampaignFeature.HangarAirframes(host);
        byte flown = CampaignAidProfiles.MissionsFlown;
        var flow = board switch
        {
            "briefing" => new CoopFlowMessage(NetCoopScreen.Briefing, flown, 1, 1, 0, 3, flown, false, airframes, 0, 0),
            "flightcheck" or "planeselection" or "ammo" =>
                new CoopFlowMessage(NetCoopScreen.FlightCheck, flown, 1, 1, 0, 3, flown, false, airframes, 0, 0),
            "ready" => new CoopFlowMessage(NetCoopScreen.FlightCheck, flown, 1, 1, 0b100, 3, flown, false, airframes, 0, 0),
            "debrief" => new CoopFlowMessage(NetCoopScreen.Debrief, (byte)(flown - 1), 2, 1, 0, 3, flown, true, airframes, 0b11, 4500),
            _ => new CoopFlowMessage(NetCoopScreen.Cabin, flown, 1, 1, 0, 3, flown, false, airframes, 0, 0),
        };

        _shell!.StandInNetDoor(NetDoorAid.CoopGuest(flow, ready: board == "ready"));
        _shell.Connection.OpenConnection();
        _shell.StepNet(0.0);
        if (board == "planeselection")
        {
            _shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignPlaneSelection);
        }
        else if (board == "ammo")
        {
            _shell.Campaign.ShowMissionScreen(OriginalScreen.CampaignAmmo);
        }
    }

    // --debug-join=N, once: N device-less seats with distinct cursors, the last one selected, so
    // the seat strip and the aircraft tags can be shot with one controller.
    private void DebugJoin(PlayerSetupFeature setup)
    {
        int extra = _debugJoin;
        _debugJoin = 0;
        for (int i = 0; i < extra; i++)
        {
            var seat = setup.Join(new MenuIdleSource());
            if (seat == null)
            {
                break;
            }

            seat.Cursor = (i + 1) % Math.Max(1, OriginalRosters.Airframes.Count);
            if (i == extra - 1)
            {
                setup.Select(seat);
            }
        }

        if (extra > 0)
        {
            Log.Info("ui", $"original presentation: --debug-join seated {setup.Seats.Count} players (the added ones have no device)");
        }
    }

    private void Redraw()
    {
        if (_shell != null && _view != null)
        {
            // Paper pages write in authored black, the loadout in the ammo form's palette, the hub
            // in its own inks, the three options pages in the Preferences page's, a campaign screen
            // in its shared board component's palette, and the rest in the file-wide inks.
            var palette = _shell.Screen is OriginalScreen.InstantAction or OriginalScreen.InstantActionWrapup
                    or OriginalScreen.HangarInventory ? _paperPalette
                : _shell.Screen == OriginalScreen.InstantActionLoadout ? BoardPalette.Paper
                : _shell.Screen is OriginalScreen.Options or OriginalScreen.GameOptions or OriginalScreen.Audio
                    or OriginalScreen.Video or OriginalScreen.ControlsPrefs or OriginalScreen.Keys ? _preferencesPalette
                : _shell.IsHangarScreen ? _hangarPalette
                : _shell.CampaignPage == CampaignScreen.Cabin ? CabinPalette
                : _shell.CampaignPage is { } campaign ? BoardPalette.For(campaign)
                : _palette;
            _view.Show(_shell.Compose(), palette, string.Empty, string.Empty);
        }
    }

    // The viewer follows the wrap-up page's open photograph, which the page's own rows open and
    // close, so any other screen shows none.
    private void SyncShotViewer()
    {
        if (_shotViewer == null || _shell == null)
        {
            return;
        }

        var shot = _shell.Screen == OriginalScreen.InstantActionWrapup ? _shell.Wrapup.Viewing : null;
        if (shot == _shotViewer.Shot)
        {
            return;
        }

        if (shot == null)
        {
            _shotViewer.Close();
        }
        else
        {
            _shotViewer.Open(shot);
        }
    }
}
