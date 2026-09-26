using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The Original campaign's decoded screens: the profile, cabin, memento, table of contents,
/// briefing, flight check, ammo, plane selection, scrapbook and zoom. This one standalone module
/// over the shared <see cref="CampaignFeature"/> owns their screen graph, hit rectangles, rollover
/// and pressed frames, cues and every door out. What each screen draws is the shared board
/// component, <c>CampaignBoards.For</c> over the campaign pages, hosted in a <c>CampaignFlow</c>
/// over the feature and the campaign layout. ⚠ That flow is never walked: its
/// screen mirrors the one showing and its row the focus. A page's destination or dialog is read
/// off it and re-entered through this graph, and its editing state waits on the feature's commit.
/// The standing box, the hangar the cabin opens and the profile re-read are the shell's, raised
/// through the host seam.
/// </summary>
public sealed class OriginalCampaignScreen : IOriginalScreenModule
{
    /// <summary>The cabin's network door, Host Co-op while shut and Close Network while open.</summary>
    public const string CoopDoorKey = "NET_HOSTCOOP";

    // The roster's own list colours, CAMPAIGN.SCRIPT's sub-script VB. The selection bar behind the
    // picked row is 0xff800000, the frame around the row under the pointer 0xffff0000.
    private const byte RosterBarRed = 0x80;
    private const byte RosterFrameRed = 0xff;

    // The cabin's network door stands in the painting's empty top-left corner, clear of the
    // authored buttons and the seat strip. The host's band is written above it.
    private const float CoopDoorX = 14f;
    private const float CoopDoorY = 40f;
    private const float CoopBandY = 14f;
    private const float CoopBandSize = 13f;
    private const float CoopBandWidth = 520f;
    private const float CoopBandGround = 0.6f;

    private readonly CampaignFeature? _campaign;
    private readonly PlayerSetupFeature _setup;
    private readonly CSVM.Flight.CustomPlaneStore? _planes;
    private readonly CampaignLayout _layout;
    private readonly IOriginalScreenHost _host;
    private readonly Func<CampaignProfileStore>? _profiles;
    private readonly Func<CSVM.Flight.StockLoadouts?>? _stock;
    private readonly Func<PlayerSeat, IReadOnlyList<int>> _flightDevices;
    private readonly string? _dataRoot;
    private readonly Func<NetPlayFeature?> _net;

    // The pages' host, mirrored to the screen showing and never walked (see the class summary).
    private CampaignFlow? _flow;
    private OriginalScreen _briefingReturn = OriginalScreen.CampaignCabin;
    private OriginalScreen _bookReturn = OriginalScreen.CampaignCabin;

    // A co-op host's debrief: the book a mission end opened, the mission it is about and the
    // shared result it shows. Browsing onward from it stays the debrief until the host leaves the
    // book for the cabin or a briefing.
    private bool _debriefing;
    private int _debriefSeq;
    private bool _debriefWon;
    private int _debriefObjectives;
    private int _debriefCash;

    // A co-op guest's following: the host board and mission it last took and how many flows it
    // has heard. The attempt is its own at the mission the debrief is about.
    private NetCoopScreen _guestScreen = NetCoopScreen.Unknown;
    private int _guestSeq = -1;
    private int _guestFlows = -1;
    private MissionAttempt? _guestAttempt;

    /// <summary>A campaign module over <paramref name="campaign"/> and the campaign layout read off
    /// the shell's own. The store <paramref name="profiles"/> is the user's profiles, which the
    /// Campaign row's door opens over. The store <paramref name="planes"/> takes what an EXPORT
    /// inside writes. Then <paramref name="setup"/> and <paramref name="flightDevices"/> are the
    /// joined seats a launch carries. Without a feature or a store the Campaign row stands disabled
    /// and every door here does nothing.</summary>
    public OriginalCampaignScreen(
        CampaignFeature? campaign,
        PlayerSetupFeature setup,
        CSVM.Flight.CustomPlaneStore? planes,
        CampaignLayout layout,
        IOriginalScreenHost host,
        Func<CampaignProfileStore>? profiles = null,
        Func<CSVM.Flight.StockLoadouts?>? stock = null,
        Func<PlayerSeat, IReadOnlyList<int>>? flightDevices = null,
        string? dataRoot = null,
        // The network door the cabin's Host Co-op opens; null, or a door answering null, hides it.
        Func<NetPlayFeature?>? net = null)
    {
        _campaign = campaign;
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
        _planes = planes;
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _profiles = profiles;
        _stock = stock;
        _flightDevices = flightDevices ?? (_ => Array.Empty<int>());
        _dataRoot = dataRoot;
        _net = net ?? (() => null);
    }

    /// <summary>Whether a campaign is open on this module.</summary>
    public bool IsOpen => _flow != null;

    /// <summary>Whether the campaign open here is a co-op guest's, following its host's boards.
    /// </summary>
    public bool IsGuest => _flow != null && _campaign?.IsGuest == true;

    /// <summary>The campaign page composing the screen showing, or null off the campaign.</summary>
    public ICampaignPage? Content => Owns(_host.Screen) ? _flow?.Page : null;

    /// <summary>Which campaign board the screen showing wears, or null off the campaign; what the
    /// presentation picks the board's palette by.</summary>
    public CampaignScreen? Board => Owns(_host.Screen) ? CampaignScreenOf(_host.Screen) : null;

    /// <summary>The name in the roster's box.</summary>
    public string RosterName => RosterEntry?.Text ?? string.Empty;

    /// <summary>How many times the briefing showing has asked for its narration, or 0 off the
    /// briefing; the presentation starts playback whenever this rises.</summary>
    public int NarrationStarts =>
        _host.Screen == OriginalScreen.CampaignBriefing ? _campaign?.Briefing?.NarrationStarts ?? 0 : 0;

    /// <summary>The briefing's narration wav, or "" when there is none or the briefing is not showing.</summary>
    public string NarrationWav =>
        _host.Screen == OriginalScreen.CampaignBriefing ? _campaign?.Briefing?.NarrationWav ?? string.Empty : string.Empty;

    /// <summary>The seated profile as the hangar's wallet, or null with nobody seated. The cabin's
    /// PLANE CONSTRUCTION opens the hangar over it, and the campaign-hangar aid takes it.</summary>
    public IHangarWallet? Wallet => _campaign?.Wallet();

    /// <summary>What the menu's typed cheats have switched on, or null over a shell with no campaign
    /// feature. The latches themselves are the shell's, the screens carrying them crossing this
    /// module and the hangar's. What a completed word turns on is kept here, on the feature both
    /// presentations commit through.</summary>
    public CampaignCheats? Cheats => _campaign?.Cheats;

    /// <summary>Whether the Campaign row on the top level stands at all. It needs a feature and a
    /// store together, the door opening over the player's own profiles.</summary>
    internal bool CanOpen => _campaign != null && _profiles != null;

    /// <summary>The campaign's string table where one is loaded, or null: the words the shell's own
    /// messagebox answers take while a campaign stands behind them.</summary>
    internal UiStrings? Strings => _campaign?.Strings;

    /// <summary>The campaign's own build store, where a purchase over the cabin's wallet writes.</summary>
    internal CSVM.Flight.CustomPlaneStore? Planes => _campaign?.Planes;

    /// <summary>The airframe a default-configuration build inherits from the cabin's door: the
    /// seated pilot's own aircraft, or null with nobody seated.</summary>
    internal int? SeatedAirframe => _campaign?.Field.Plane(0)?.Airframe;

    /// <summary>The seat whose flight check is showing, as its index, or -1 off the check's screens.
    /// The check and the ammo and plane screens its own rows open all stand for one player at a
    /// time. The seat the field names owns them. The guest that opened ammo selection is still the
    /// seat whose aircraft it edits.</summary>
    internal int CheckSeat => OnCheck && _campaign is { } campaign ? campaign.Field.Current : -1;

    /// <summary>The open drop-down list on the campaign screen showing, or null. The shell drives it
    /// with the axis and closes it on a click off the list. Its own per-seat aircraft screen's list
    /// takes the same rule.</summary>
    internal CampaignCombo? OpenCombo =>
        _flow != null && !_host.DialogOpen && PageFocus >= 0 && _flow.Page.Combo(PageFocus) is { Open: true } combo
            ? combo
            : null;

    // Whether the cabin shows its network door: a pilot is seated and the build has a door. A
    // guest's cabin shows the host's band in its place.
    private bool CoopDoorShown =>
        _host.Screen == OriginalScreen.CampaignCabin && _campaign?.Profile != null && _net() != null && !IsGuest;

    private CampaignTextEntry? RosterEntry => _flow?.Page is CampaignRosterPage roster ? roster.TextEntry : null;

    // The focused row as a page row, for the pages that read the flow's cursor.
    private int PageFocus
    {
        get
        {
            if (_flow == null || _host.DialogOpen)
            {
                return _host.FocusBeforeDialog;
            }

            int focus = _host.FocusedRow;
            return focus >= 0 && focus < _flow.Page.RowCount ? focus : -1;
        }
    }

    // The flight check's screens: the check itself and the ammo and plane screens its own rows
    // open, which no other door reaches.
    private bool OnCheck =>
        _campaign != null && _host.Screen is OriginalScreen.CampaignFlightCheck
            or OriginalScreen.CampaignAmmo or OriginalScreen.CampaignPlaneSelection;

    // The host's last local check launches, and it waits while any seated guest is not Ready.
    private bool LaunchWaits =>
        _net() is { IsCoopHost: true } net && !net.CoopAllReady
        && _campaign is { } campaign && campaign.Field.Current + 1 >= campaign.Field.Players;

    /// <summary>Whether a screen is one of the campaign's.</summary>
    public bool Owns(OriginalScreen screen) =>
        screen >= OriginalScreen.CampaignRoster && screen <= OriginalScreen.CampaignScrapbookZoom;

    /// <summary>The Campaign row's door: opens the campaign over the user's profile store and lands
    /// on the profile screen. Nothing happens when the module has no feature or no store.</summary>
    public void OpenCampaign()
    {
        if (_profiles != null)
        {
            OpenCampaignOver(_profiles());
        }
    }

    /// <summary>Opens the campaign over <paramref name="store"/>, the aids' and the suites' door.
    /// It lands on the profile screen with the name box pre-filled with the last player seated,
    /// the way <c>CAMPAIGN.SCRIPT</c> pre-fills it from the registry. The store
    /// <paramref name="planes"/> stands in for the shell's build store while this campaign is
    /// open, which is how an aid's EXPORT writes into a scratch store.</summary>
    public void OpenCampaignOver(CampaignProfileStore store, CSVM.Flight.CustomPlaneStore? planes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (_campaign == null)
        {
            return;
        }

        _campaign.Open(store, planes ?? _planes, _stock?.Invoke(), _dataRoot);
        _flow = new CampaignFlow(_campaign, _layout);
        _host.CloseDialog();
        _briefingReturn = OriginalScreen.CampaignCabin;
        _bookReturn = OriginalScreen.CampaignCabin;
        string last = _campaign.LastPlayed;
        RosterEntry?.Set(RosterHas(last) ? last : string.Empty);
        ShowCampaign(OriginalScreen.CampaignRoster);
    }

    /// <summary>Seats the named profile re-read from the store and lands on the cabin, the door a
    /// flight return and the campaign screenshot aids take. False, on the profile screen, when the
    /// profile cannot be read. True says the profile is seated, not that the cabin is showing. A
    /// chapter cinema plays first where one is due, and the cabin arrives when it stops.</summary>
    public bool ShowCabin(string profile)
    {
        if (_flow == null || _campaign == null || !_campaign.SeatProfile(profile))
        {
            return false;
        }

        OpenCabin();
        return true;
    }

    /// <summary>Seats the named profile and opens the book on a mission's first spread with the
    /// cabin on its far side. That is the mission end's own door and the book's screenshot aid.
    /// False when the profile cannot be read. True says the profile is seated, not that the book is
    /// showing. The closing cinema plays first after a win on the campaign's last mission, and the
    /// book arrives when it stops.</summary>
    public bool ShowScrapbook(string profile, int seq, bool missionWon)
    {
        if (_flow == null || _campaign == null || !_campaign.SeatProfile(profile))
        {
            return false;
        }

        // What a co-op host's guests are shown: this book is the debrief, over the host's result.
        var run = _campaign.Profile is { } seated ? CampaignProgression.ResultOf(seated, seq)?.Latest : null;
        _debriefing = true;
        _debriefSeq = seq;
        _debriefWon = missionWon;
        _debriefObjectives = run?.CompletedMask ?? 0;
        _debriefCash = run?.Money ?? 0;
        if (_campaign.ClosingCinema is { } cinema)
        {
            _host.PlayFilm(then => cinema.OpenScrapbook(seq, missionWon, then), () => OpenBook(seq));
            return true;
        }

        OpenBook(seq);
        return true;
    }

    /// <summary>Opens a co-op guest's campaign on the host's word: a profile in memory over the
    /// host's hangar, standing on whichever board the host names. False when the door is not a
    /// co-op guest's or has heard nothing from the host yet.</summary>
    public bool OpenGuestCampaign()
    {
        if (_campaign == null || _net() is not { IsCoopGuest: true } net || net.CoopFlow is not { } flow)
        {
            return false;
        }

        // The guest's own last pilot names it to the host. Read only, since a guest's saves are
        // never touched.
        net.PlayerName = _profiles?.Invoke().LastPlayedPilotName ?? string.Empty;
        _campaign.OpenGuest(net.Advert?.Host ?? string.Empty, flow.Progress, flow.Airframes, _stock?.Invoke(), _dataRoot);
        _flow = new CampaignFlow(_campaign, _layout);
        _host.CloseDialog();
        _briefingReturn = OriginalScreen.CampaignCabin;
        _bookReturn = OriginalScreen.CampaignCabin;
        _guestScreen = NetCoopScreen.Unknown;
        _guestSeq = -1;
        _guestFlows = -1;
        FollowHost(net, flow);
        return true;
    }

    /// <summary>A co-op guest back from a flight: its campaign reopens on the host's word. The
    /// host's debrief carries this guest's own <paramref name="attempt"/>, or none when it flew
    /// nothing. False as <see cref="OpenGuestCampaign"/>.</summary>
    public bool ShowGuestDebrief(MissionAttempt? attempt)
    {
        _guestAttempt = attempt;
        return OpenGuestCampaign();
    }

    /// <summary>Names <paramref name="name"/> in the profile screen's box and presses DELETE PLAYER,
    /// so the two-answer messagebox stands over the screen: the screenshot aid's door. Nothing
    /// happens off the profile screen.</summary>
    public void ShowDeleteConfirm(string name)
    {
        if (_host.Screen != OriginalScreen.CampaignRoster || RosterEntry is not { } entry)
        {
            return;
        }

        entry.Set(name);
        BeginDelete();
    }

    /// <summary>Opens one of the screens past the cabin on the seated profile's next mission, the
    /// aids' door. That screen is the table of contents, the briefing, the flight check, ammo
    /// selection on the pilot's aircraft or plane selection on the pilot's slot. Nothing happens
    /// with nobody seated.</summary>
    public void ShowMissionScreen(OriginalScreen screen)
    {
        if (_flow == null || _campaign?.Profile == null || !Owns(screen))
        {
            return;
        }

        _campaign.SetMission(_campaign.NextMissionSeq);
        _campaign.SetAmmoSlot(0);
        _campaign.SetPlaneSlot(0);
        _briefingReturn = OriginalScreen.CampaignCabin;
        ShowCampaign(screen);
    }

    /// <summary>Presses the pilot's EXPORT on the plane-selection screen showing, so the one-button
    /// messagebox stands over it: the screenshot aid's door. Nothing happens off that screen or
    /// while a dialog already stands.</summary>
    public void PressExport() => PressBoardButton(BoardButton.ExportPlane);

    /// <summary>Replays a screenshot aid's colon argument on the campaign screen showing, the words
    /// <see cref="CampaignAidScript"/> reads for Built-in, so one string poses both presentations.
    /// The flow here is never walked. A cursor verb is one command frame through this graph, and a
    /// button word is that button's row taking the focus and the confirm. False for a script
    /// spelling the secondary verb x, which is refused whole because Original binds no such press,
    /// its lists selecting by click. Its caller ends the run.</summary>
    public bool RunAidScript(string script)
    {
        if (CampaignAidScript.Presses(script, CampaignAidScript.VerbsWithoutSecondary, "the Original presentation")
            is not { } presses)
        {
            return false;
        }

        foreach (CampaignAidScript.Step press in presses)
        {
            for (int i = 0; i < press.Count; i++)
            {
                PressAidStep(press);
            }
        }

        return true;
    }

    /// <summary>Moves the briefing's reveal on by a frame's worth of seconds while the briefing
    /// shows, the presentation's clock. Returns whether the reveal is still running, which is when
    /// the board has to repaint on the frame clock.</summary>
    public bool AdvanceBriefing(double seconds)
    {
        if (_host.Screen != OriginalScreen.CampaignBriefing || _campaign?.Briefing is not { } briefing)
        {
            return false;
        }

        briefing.Advance(seconds);
        return !briefing.Complete;
    }

    /// <summary>Drops the open campaign, if any: the feature's transient state and the pages.
    /// Re-reads the build store into the sortie roster, since an EXPORT inside may have crossed a
    /// plane into it. Every door out of the campaign and every return to the top level comes
    /// through here.</summary>
    public void CloseCampaign()
    {
        // ⚠ The co-op door is not closed here: a flight's return comes through this too, and a
        // host's guests stay linked through the debrief. The doors that leave the campaign close it.
        _campaign?.Discard();
        _flow = null;
        _debriefing = false;
        _host.CloseDialog();

        // An EXPORT inside the campaign just crossed a plane into the sortie lists. Those are
        // reached from the top level without another Activate to re-read the store on.
        if (_planes != null)
        {
            _setup.SetRoster(OriginalRosters.Roster(_planes.List()));
        }
    }

    /// <summary>The showing screen's rows, in focus order.</summary>
    public void BuildRows(List<OriginalRow> rows)
    {
        if (_flow is { } flow)
        {
            OriginalWidgets.PageRows(
                flow.Page, OpenCombo, row => flow.Page.Focusable(row) && RowEnabled(flow.Page, row),
                _layout, _host.Measure, rows);
            if (CoopDoorShown)
            {
                var door = _net()!;
                bool open = door.IsCoopHost;
                var row = _host.PlaqueRow(
                    CoopDoorKey, open ? CoopDoorText.CloseNetworkButton : CoopDoorText.HostCoopButton, 0,
                    open || door.Stage is NetDoorStage.Shut or NetDoorStage.Failed, 0);
                rows.Add(row with { X = CoopDoorX, Y = CoopDoorY });
            }
        }
    }

    /// <summary>The campaign's lists for the pointer: an open drop-down's list first, since it hangs
    /// over the screen, then the contents' mission list.</summary>
    public void Lists(List<OriginalList> lists)
    {
        if (OpenCombo is { } combo && CampaignBoards.ComboWindow(combo) is { } open)
        {
            lists.Add(new OriginalList(OriginalWidgets.EntryKeyPrefix + "LIST", open, top => combo.ScrollTo(top)));
        }

        if (_flow == null)
        {
            return;
        }

        // The contents page pulls its window over the flow's cursor. That cursor mirrors this
        // graph's focus only on a compose, so the mirror is brought up to date first.
        if (PageFocus >= 0)
        {
            _flow.FocusRow(PageFocus);
        }

        if (_flow.Page is CampaignPreviousMissionsPage contents && contents.PointerWindow is { } window)
        {
            lists.Add(new OriginalList("CONTENTS", window, top => ScrollContents(contents, top)));
        }
    }

    /// <summary>A sideways step on a closed field picks its next entry, the page's own stepper, which
    /// the plane selection may refuse with its dialog.</summary>
    public bool StepSideways(IReadOnlyList<OriginalRow> rows, int focus, int direction)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_flow == null || _host.DialogOpen || focus < 0 || focus >= rows.Count
            || rows[focus].Kind != OriginalRowKind.Dropdown)
        {
            return false;
        }

        var before = _flow.Screen;
        _flow.FocusRow(focus);
        _flow.Page.Step(focus, direction);
        SyncAfterPage(before);
        return true;
    }

    /// <summary>Closes an open field's list and picks nothing; false when none is open.</summary>
    public bool CloseDropdown() => OpenCombo is { } combo && combo.Collapse();

    /// <summary>The showing screen's answer to an activated row.</summary>
    public MenuExit? Activate(OriginalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_flow == null || _campaign == null)
        {
            return null;
        }

        // A guest's greyed row moves nothing, whichever way the press arrived.
        if (IsGuest && !GuestPressLive(row))
        {
            return null;
        }

        if (OriginalWidgets.Entry(row.Key) != null)
        {
            OriginalWidgets.Highlight(OpenCombo, row.Key);
            PagePress(PageFocus);
            return null;
        }

        if (row.Key == CoopDoorKey)
        {
            ToggleCoopDoor();
            return null;
        }

        int pageRow = RowIndexOf(row.Key);
        if (pageRow < 0)
        {
            return null;
        }

        // RETURN TO CABIN wherever it is pressed, one door on three screens. ⚠ Not the page's own
        // press through PagePress. A film defers the arrival, and SyncAfterPage mirrors only a move
        // already made, so a deferred one strands this graph on the screen just left.
        if (row.Key == nameof(BoardButton.ReturnToCabin))
        {
            OpenCabin();
            return null;
        }

        switch (_host.Screen)
        {
            case OriginalScreen.CampaignRoster:
                ActivateRoster(row, pageRow);
                return null;
            case OriginalScreen.CampaignCabin:
                ActivateCabin(row, pageRow);
                return null;
            case OriginalScreen.CampaignBriefing:
                ActivateBriefing(row);
                return null;
            case OriginalScreen.CampaignFlightCheck:
                return ActivateFlightCheck(row);
            default:
                PagePress(pageRow);
                return null;
        }
    }

    /// <summary>Back through the campaign's own graph. A guest's check retreats to the player
    /// before, and each screen returns to the one that opened it. The profile screen leaves the
    /// campaign. Always answered, the campaign having no screen the shell's own way out
    /// serves.</summary>
    public bool Back()
    {
        if (_flow == null || _campaign == null)
        {
            return true;
        }

        // A guest steps back only out of its own two screens. Anywhere else Back asks whether to
        // leave the session, since the host's boards are not the guest's to walk.
        if (IsGuest && _host.Screen is not (OriginalScreen.CampaignAmmo or OriginalScreen.CampaignPlaneSelection))
        {
            AskLeaveSession();
            return true;
        }

        switch (_host.Screen)
        {
            case OriginalScreen.CampaignRoster:
                LeaveCampaign();
                break;
            case OriginalScreen.CampaignCabin:
                ShowCampaign(OriginalScreen.CampaignRoster, keepFocus: true);
                break;
            case OriginalScreen.CampaignMemento:
            case OriginalScreen.CampaignPreviousMissions:
                BackTo(OriginalScreen.CampaignCabin);
                break;
            case OriginalScreen.CampaignBriefing:
                BackTo(_briefingReturn);
                break;
            case OriginalScreen.CampaignFlightCheck:
                if (_campaign.Field.Retreat())
                {
                    ShowCampaign(OriginalScreen.CampaignFlightCheck);
                }
                else
                {
                    _campaign.Field.Rewind();
                    ShowCampaign(OriginalScreen.CampaignBriefing, keepFocus: true);
                }

                break;
            case OriginalScreen.CampaignAmmo:
            case OriginalScreen.CampaignPlaneSelection:
                // The page's own Back. An open list closes, else the working copy is dropped or
                // the picks restored. The screen then falls back to the check.
                _flow.FocusRow(Math.Max(0, PageFocus));
                if (!_flow.Page.Back())
                {
                    ShowCampaign(OriginalScreen.CampaignFlightCheck, keepFocus: true);
                }

                break;
            case OriginalScreen.CampaignScrapbook:
                BackTo(_bookReturn);
                break;
            case OriginalScreen.CampaignScrapbookZoom:
                ShowCampaign(OriginalScreen.CampaignScrapbook, keepFocus: true);
                break;
        }

        return true;
    }

    /// <summary>The screen as the shared board component composes it over the page, with this
    /// graph's own additions. Those are the roster's list colours, its box's words, and the seat
    /// strip. The standing box is drawn by the shell, over everything here.</summary>
    public void Compose(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(layers);
        if (_flow == null)
        {
            return;
        }

        var page = _flow.Page;
        int pageFocus = PageFocus;
        if (pageFocus >= 0)
        {
            _flow.FocusRow(pageFocus);
        }

        bool pressed = !_host.DialogOpen && _host.PressedRow >= 0 && _host.PressedRow == focus;
        string detail = page.Screen == CampaignScreen.Ammo && pageFocus >= 0 ? page.Detail(pageFocus) : string.Empty;
        var board = CampaignBoards.For(page, pageFocus, pressed, detail, null, _layout);
        layers.Backdrop.AddRange(board.Backdrop);
        layers.Fills.AddRange(board.Fills);

        // The cabin's painting is the page's own picture, not a backdrop pane, so the pull-down's
        // paper would land under it. PASSENGERCABIN.SCRIPT's field is drawn over the scene, so the
        // scene, which leads the board's pictures, goes down as backdrop here.
        int scene = page.Screen == CampaignScreen.Cabin ? page.Pictures.Count : 0;
        for (int i = 0; i < board.Pictures.Count; i++)
        {
            (i < scene ? layers.Backdrop : layers.Pictures).Add(board.Pictures[i]);
        }

        layers.Strokes.AddRange(board.Strokes);
        foreach (var plaque in board.Plaques)
        {
            layers.Plaques.Add(Greyed(page, plaque) ? plaque with { Frame = 0 } : plaque);
        }

        layers.Notes.AddRange(board.Notes);
        layers.Overlays.AddRange(board.Overlays);
        if (page.Screen == CampaignScreen.Roster)
        {
            ComposeRoster(rows, focus, board.Lines, layers);
        }
        else
        {
            layers.Lines.AddRange(board.Lines);
        }

        ComposeCoopDoor(rows, focus, layers);
        if (_host.SeatPanel(false) is { } strip)
        {
            layers.Overlays.Add(strip);
        }
    }

    /// <summary>One frame's co-op upkeep. An open host door re-offers the mission the cabin stands
    /// on and names the board showing to its guests, the lobby sending only a change. A guest's
    /// campaign follows the board its host names. Returns whether the picture changed.</summary>
    internal bool StepCoop()
    {
        if (_net() is not { } net || _campaign == null)
        {
            return false;
        }

        // A launched host's flight names the board until the wire comes back.
        if (net.IsCoopHost && net.Released)
        {
            return false;
        }

        if (net.IsCoopHost && _campaign.Profile is { } profile && !_campaign.IsGuest)
        {
            int seq = _campaign.MissionSeq >= 0 ? _campaign.MissionSeq : _campaign.NextMissionSeq;
            net.Offer(seq, profile.Name, Math.Max(1, _setup.Seats.Count));
            var screen = HostCoopScreen();
            int shown = screen switch
            {
                NetCoopScreen.Debrief => _debriefSeq,
                NetCoopScreen.Briefing or NetCoopScreen.FlightCheck => _campaign.MissionSeq,
                _ => _campaign.NextMissionSeq,
            };

            // The result goes first, so the flow that names the debrief already carries it.
            net.ShowCoopResult(_debriefWon, _debriefObjectives, _debriefCash);
            net.ShowCoop(screen, shown, profile.MissionsCompleted, CampaignFeature.HangarAirframes(profile));
            return false;
        }

        if (IsGuest && net.IsCoopGuest && net.CoopFlow is { } flow)
        {
            return FollowHost(net, flow);
        }

        return false;
    }

    /// <summary>A co-op guest's launch, once its host has launched the mission it is seated for.
    /// It flies the guest's own aeroplane and ammunition on the host's mission. Null
    /// until then, and on a campaign that is not a guest's.</summary>
    internal MenuExit? GuestLaunch()
    {
        if (!IsGuest || _net() is not { CoopLaunchDue: true } net || net.CoopFlow is not { } flow)
        {
            return null;
        }

        _campaign!.SetMission(flow.MissionSeq);
        var pads = new List<IReadOnlyList<int>>
        {
            _setup.Seats.Count > 0 ? _flightDevices(_setup.Seats[0]) : Array.Empty<int>(),
        };
        var exit = _campaign.BuildExit(pads);
        return exit == null ? null : exit with { Net = net.BuildLaunch() };
    }

    /// <summary>Typed characters and Backspace into the roster's name box, the campaign's own
    /// character set and cap, each character cueing the box's keystroke or reject sound.</summary>
    internal bool TypeName(MenuCommands commands, List<string> cues)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(cues);
        if (RosterEntry is not { } entry || _host.DialogOpen)
        {
            return false;
        }

        bool changed = false;
        foreach (char c in commands.Typed)
        {
            if (entry.Type(c.ToString()))
            {
                changed = true;
                cues.Add(OriginalCues.Text);
            }
            else
            {
                cues.Add(OriginalCues.TextError);
            }
        }

        if (commands.Erase && entry.Backspace())
        {
            changed = true;
        }

        return changed;
    }

    /// <summary>The joined humans on the flight check, once a frame: the seats are the shared
    /// setup's.</summary>
    internal void SyncField()
    {
        // A co-op guest flies one aeroplane whatever its own seats, since the field is the host's.
        if (_host.Screen == OriginalScreen.CampaignFlightCheck && _campaign != null)
        {
            _campaign.Field.SetPlayers(IsGuest ? 1 : _setup.Seats.Count);
        }
    }

    /// <summary>Back from the hangar the cabin opened: the profile is re-read so a purchase or a
    /// sale shows.</summary>
    internal void ResumeCampaign()
    {
        _campaign?.Resume();
        OpenCabin(keepFocus: true);
    }

    private static CampaignScreen CampaignScreenOf(OriginalScreen screen) => screen switch
    {
        OriginalScreen.CampaignRoster => CampaignScreen.Roster,
        OriginalScreen.CampaignCabin => CampaignScreen.Cabin,
        OriginalScreen.CampaignMemento => CampaignScreen.MementoSelection,
        OriginalScreen.CampaignPreviousMissions => CampaignScreen.PreviousMissions,
        OriginalScreen.CampaignBriefing => CampaignScreen.Briefing,
        OriginalScreen.CampaignFlightCheck => CampaignScreen.FlightCheck,
        OriginalScreen.CampaignAmmo => CampaignScreen.Ammo,
        OriginalScreen.CampaignPlaneSelection => CampaignScreen.PlaneSelection,
        OriginalScreen.CampaignScrapbook => CampaignScreen.Scrapbook,
        _ => CampaignScreen.ScrapbookZoom,
    };

    private static OriginalScreen OriginalOf(CampaignScreen screen) => screen switch
    {
        CampaignScreen.Roster => OriginalScreen.CampaignRoster,
        CampaignScreen.Cabin => OriginalScreen.CampaignCabin,
        CampaignScreen.MementoSelection => OriginalScreen.CampaignMemento,
        CampaignScreen.PreviousMissions => OriginalScreen.CampaignPreviousMissions,
        CampaignScreen.Briefing => OriginalScreen.CampaignBriefing,
        CampaignScreen.FlightCheck => OriginalScreen.CampaignFlightCheck,
        CampaignScreen.Ammo => OriginalScreen.CampaignAmmo,
        CampaignScreen.PlaneSelection => OriginalScreen.CampaignPlaneSelection,
        CampaignScreen.Scrapbook => OriginalScreen.CampaignScrapbook,
        _ => OriginalScreen.CampaignScrapbookZoom,
    };

    // A guest's own controls are its aeroplane and its ammunition. Those are their two screens,
    // and on its check the two buttons that open them and its Ready. Everything else follows the host.
    // EXPORT writes a build to disk, and a guest's machine keeps nothing from the host's campaign.
    private static bool GuestRowLive(CampaignScreen screen, BoardButton button) => screen switch
    {
        CampaignScreen.Ammo => true,
        CampaignScreen.PlaneSelection => button != BoardButton.ExportPlane,
        CampaignScreen.FlightCheck => button is BoardButton.ChangeAmmo or BoardButton.ChangePlane or BoardButton.FlyMission,
        _ => false,
    };

    // One line of network state over a dark ground, in the painting's empty top-left corner.
    private static void ComposeBand(string band, BoardLayers layers)
    {
        if (band.Length > 0)
        {
            layers.Fills.Add(new BoardFill(CoopDoorX - 4f, CoopBandY - 3f, CoopBandWidth, CoopBandSize + 8f, 0, 0, 0, CoopBandGround));
            layers.Lines.Add(new BoardLine(band, CoopDoorX, CoopBandY, CoopBandWidth, CoopBandSize, BoardInk.Row, -1,
                Colour: new BoardTint(226, 224, 206)));
        }
    }

    private bool RosterHas(string name)
    {
        if (_campaign == null || name.Length == 0)
        {
            return false;
        }

        foreach (string stored in _campaign.Roster)
        {
            if (stored == name)
            {
                return true;
            }
        }

        return false;
    }

    // Enters a campaign screen. The pages' host is moved onto its page, its cursor landing on the
    // page's own opening row, which is where this screen's focus opens too. No dialog stands, and
    // the pointer state starts afresh. A way back onto a screen keeps the focus where it stood, on
    // the plaque that opened what is being left. A round trip then lands where it started.
    private void ShowCampaign(OriginalScreen screen, bool keepFocus = false)
    {
        if (_flow == null)
        {
            return;
        }

        if (screen is OriginalScreen.CampaignCabin or OriginalScreen.CampaignBriefing or OriginalScreen.CampaignRoster)
        {
            _debriefing = false;
        }

        _flow.GoTo(CampaignScreenOf(screen));
        _host.CloseDialog();
        _host.Open(screen);
        if (!keepFocus || _host.FocusedRow < 0)
        {
            _host.FocusedRow = _flow.Row;
        }
    }

    private void EnterBriefing(OriginalScreen returnTo)
    {
        _briefingReturn = returnTo;
        ShowCampaign(OriginalScreen.CampaignBriefing);
    }

    // Every door out of the campaign: CANCEL and Back on the profile screen, RETURN TO MAIN MENU.
    private void LeaveCampaign()
    {
        CloseCoopDoor();
        CloseCampaign();
        _host.Open(OriginalScreen.TopLevel);
    }

    // The cabin's NEXT MISSION is disabled once the campaign is finished, the script's own
    // mail(10000) when uiData 2600 answers 0. A co-op guest's boards are the host's, and a co-op
    // host's launch waits for every guest's Ready. Every other button is live as the page offers it.
    private bool RowEnabled(ICampaignPage page, int row)
    {
        var button = page.Button(row).Button;
        if (IsGuest)
        {
            return GuestRowLive(page.Screen, button);
        }

        if (page.Screen == CampaignScreen.FlightCheck && button == BoardButton.FlyMission && LaunchWaits)
        {
            return false;
        }

        return !(page.Screen == CampaignScreen.Cabin && button == BoardButton.NextMission && _campaign?.CampaignComplete == true);
    }

    // The strip's disabled frame for a co-op row that is not this player's to press. Those are a
    // guest's following boards and a host's FLY MISSION while a guest waits. Other refusals keep
    // the frames they have always drawn.
    private bool Greyed(ICampaignPage page, BoardPlaque plaque)
    {
        if (plaque.Art.Frames < 4 || plaque.Row < 0 || plaque.Row >= page.RowCount)
        {
            return false;
        }

        bool coop = IsGuest || (page.Screen == CampaignScreen.FlightCheck
            && page.Button(plaque.Row).Button == BoardButton.FlyMission && LaunchWaits);
        return coop && !RowEnabled(page, plaque.Row);
    }

    private int RowIndexOf(string key)
    {
        var rows = new List<OriginalRow>();
        BuildRows(rows);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    // The contents list's window moved to top. The page pulls its own cursor inside, and this
    // graph's focus takes the cursor back. The window is then not pulled home on the next compose.
    private void ScrollContents(CampaignPreviousMissionsPage contents, int top)
    {
        int focus = PageFocus;
        if (focus >= 0)
        {
            _flow!.FocusRow(focus);
        }

        contents.ScrollTo(top);
        if (focus >= 0)
        {
            _host.FocusedRow = _flow!.Row;
        }
    }

    // A press handed to the page. The page edits its own state, and whatever it named on the way
    // (a destination, a dialog, a refusal) is read off the host. That is re-entered through this
    // graph.
    private void PagePress(int pageRow)
    {
        if (_flow == null)
        {
            return;
        }

        var before = _flow.Screen;
        _flow.FocusRow(pageRow);
        _flow.Page.Accept(pageRow);
        SyncAfterPage(before);
    }

    // One script step as this graph's own presses. A cursor verb is a command frame, so the focus,
    // the cues and every door out are the ones a player's press takes. The verb x never arrives
    // here, the script carrying one having been refused whole.
    private void PressAidStep(CampaignAidScript.Step press)
    {
        if (press.Button != BoardButton.None)
        {
            PressBoardButton(press.Button);
            return;
        }

        switch (press.Verb)
        {
            case 'd':
                _host.Frame(new MenuCommands { MoveY = 1 });
                break;
            case 'u':
                _host.Frame(new MenuCommands { MoveY = -1 });
                break;
            case 'l':
                _host.Frame(new MenuCommands { MoveX = -1 });
                break;
            case 'r':
                _host.Frame(new MenuCommands { MoveX = 1 });
                break;
            case 'a':
                _host.Frame(new MenuCommands { Accept = true });
                break;
            case 'b':
                _host.Frame(new MenuCommands { Back = true });
                break;
        }
    }

    // A named button's own press: the row carrying it takes the focus and the confirm, which is
    // what clicking that plaque does. A screen without the button is left alone, and so is one with
    // a dialog standing, since the box's answers are the rows then.
    private void PressBoardButton(BoardButton button)
    {
        if (_flow == null || _host.DialogOpen || !Owns(_host.Screen))
        {
            return;
        }

        for (int row = 0; row < _flow.Page.RowCount; row++)
        {
            if (_flow.Page.Button(row).Button == button)
            {
                _host.FocusedRow = row;
                _host.Frame(new MenuCommands { Accept = true });
                return;
            }
        }
    }

    private void SyncAfterPage(CampaignScreen before)
    {
        if (_flow == null)
        {
            return;
        }

        if (_flow.TakeModal() is { } modal)
        {
            _host.RaiseDialog(modal.Message, modal.Icon, Ok(modal.Confirm));
        }
        else if (_flow.TakeMessage() is { Length: > 0 } message)
        {
            // A refusal band raised as a box is the plane screen's langui 710 and the sell path's
            // 701. Both are 0x1 masks, so it takes the warning.
            _host.RaiseDialog(message, DialogIcon.Warning, Ok());
        }

        if (_flow.Screen != before)
        {
            EnterFromPage(_flow.Screen, before);
        }
        else if (!_host.DialogOpen)
        {
            // The page may have moved the cursor onto the control just pressed (a page turn).
            _host.FocusedRow = _flow.Row;
        }
    }

    // The destination a page named, entered through this graph with its own return remembered.
    // A replay's briefing goes back to where it was pressed. The book remembers whether the
    // table of contents or the cabin opened it.
    private void EnterFromPage(CampaignScreen to, CampaignScreen from)
    {
        switch (to)
        {
            case CampaignScreen.Briefing:
                EnterBriefing(OriginalOf(from));
                break;
            case CampaignScreen.Scrapbook:
                if (from == CampaignScreen.PreviousMissions)
                {
                    _bookReturn = OriginalScreen.CampaignPreviousMissions;
                }
                else if (from is not (CampaignScreen.Scrapbook or CampaignScreen.ScrapbookZoom))
                {
                    _bookReturn = OriginalScreen.CampaignCabin;
                }

                ShowCampaign(OriginalScreen.CampaignScrapbook);
                break;
            case CampaignScreen.FlightCheck:
                // ACCEPT or CANCEL on the ammo and plane screens: back onto the plaque that opened them.
                ShowCampaign(OriginalScreen.CampaignFlightCheck, keepFocus: from is CampaignScreen.Ammo or CampaignScreen.PlaneSelection);
                break;
            case CampaignScreen.Cabin:
                // ACCEPT or CANCEL in the memento chooser: back onto the plaque that opened it.
                ShowCampaign(OriginalScreen.CampaignCabin, keepFocus: from is CampaignScreen.MementoSelection);
                break;
            default:
                ShowCampaign(OriginalOf(to));
                break;
        }
    }

    // On the profile screen, CONTINUE and Enter in the box both start on the name in the box. The
    // box names CM_B_START as its default button, the script's own commit path. A roster row fills
    // the box, and a second press on the filled row starts (the double-click). DELETE PLAYER asks,
    // CANCEL leaves. A click in the box reaches none of this: an edit box takes the caret and
    // nothing else. The shell's Activate rules that for every box on every screen.
    private void ActivateRoster(OriginalRow row, int pageRow)
    {
        if (RosterEntry is not { } entry || _campaign == null)
        {
            return;
        }

        switch (row.Key)
        {
            case nameof(BoardButton.Continue):
                ContinuePlayer();
                return;
            case nameof(BoardButton.DeletePlayer):
                BeginDelete();
                return;
            case nameof(BoardButton.CancelProfile):
                LeaveCampaign();
                return;
        }

        if (pageRow == 0)
        {
            ContinuePlayer();
            return;
        }

        int index = pageRow - 1;
        if (index >= 0 && index < _campaign.Roster.Count)
        {
            string name = _campaign.Roster[index];
            if (entry.Text == name)
            {
                ContinuePlayer();
                return;
            }

            entry.Set(name);
        }
    }

    private void ContinuePlayer()
    {
        if (_campaign == null || RosterEntry is not { } entry)
        {
            return;
        }

        // The hidden pilot name is not a name at all, so it is read before the rule the feature
        // applies would refuse its punctuation. What it switches on is the page's, since both
        // presentations commit this screen onto the same store.
        if (_flow?.Page is CampaignRosterPage roster && CampaignCheats.IsUnlockName(entry.Text))
        {
            roster.Unlock();
            return;
        }

        if (_campaign.ContinuePlayer(entry.Text) is { } refusal)
        {
            // CAMPAIGN.SCRIPT raises the missing-name and unknown-name refusals on the 0x1 mask.
            _host.RaiseDialog(refusal, DialogIcon.Warning, Ok());
            return;
        }

        entry.Set(_campaign.Profile?.Name ?? entry.Text.Trim());
        OpenCabin();
    }

    // Every door onto the cabin, inside the campaign and out. CONTINUE above, the flight return and
    // the screenshot aids come through ShowCabin, and so do each screen's RETURN TO CABIN and back
    // press. The seated profile's chapter cinema plays first where one is due, and the cabin opens
    // on the frame the film stops. A position inside a chapter, and a shell with no cinema (every
    // suite), opens the cabin straight away. The keepFocus flag is the way back's, landing on the
    // plaque that was left.
    private void OpenCabin(bool keepFocus = false)
    {
        if (_campaign?.ChapterCinema is { } cinema && _campaign.Profile is { } seated)
        {
            _host.PlayFilm(
                then => cinema.OpenCabin(seated, then),
                () => ShowCampaign(OriginalScreen.CampaignCabin, keepFocus));
            return;
        }

        ShowCampaign(OriginalScreen.CampaignCabin, keepFocus);
    }

    // A way back that may land on the cabin, which is then a cabin door with its film in front.
    private void BackTo(OriginalScreen screen)
    {
        if (screen == OriginalScreen.CampaignCabin)
        {
            OpenCabin(keepFocus: true);
            return;
        }

        ShowCampaign(screen, keepFocus: true);
    }

    // The book as a finished mission leaves it, with the cabin on its far side. Separate from
    // ShowScrapbook so the closing cinema can defer it to the frame the film stops.
    private void OpenBook(int seq)
    {
        _campaign?.EnterScrapbook(seq);
        _bookReturn = OriginalScreen.CampaignCabin;
        ShowCampaign(OriginalScreen.CampaignScrapbook);
    }

    // DELETE PLAYER raises the original's question (langui 201) as the two-answer box.
    // CAMPAIGN.SCRIPT's 0x4 mask puts it under the query icon. The deletion takes the profile's
    // own directory alone and clears the box.
    private void BeginDelete()
    {
        if (_campaign == null || RosterEntry is not { } entry)
        {
            return;
        }

        string name = entry.Text.Trim();
        if (name.Length == 0 || !_campaign.HasPlayer(name))
        {
            _host.RaiseDialog($"There is no player named \"{name}\".", DialogIcon.Warning, Ok());
            return;
        }

        _host.RaiseDialog(
            _campaign.Strings.Text(201, "Are you sure you want to delete this player and all associated saved games?"),
            DialogIcon.Query,
            Yes(() =>
            {
                _campaign.DeletePlayer(name);
                entry.Set(string.Empty);
                _host.FocusedRow = 0;
            }),
            No());
    }

    // Host Co-op opens a campaign listen server for the seats the local players leave free and
    // advertises the cabin's mission; Close Network hangs it up. A door the Connection page opened
    // is left alone, its button drawn greyed.
    private void ToggleCoopDoor()
    {
        if (_net() is not { } net)
        {
            return;
        }

        if (net.IsCoopHost)
        {
            net.Close();
            return;
        }

        if (net.Stage is not (NetDoorStage.Shut or NetDoorStage.Failed))
        {
            return;
        }

        net.Close();
        net.OpenCoopHost(NetSeats.MaxPlayers - Math.Max(1, _setup.Seats.Count));
        StepCoop();
        if (net.Fault.Length > 0)
        {
            _host.RaiseDialog($"The network did not open: {net.Fault}", DialogIcon.Warning, Ok());
        }
    }

    // The board this co-op host's guests are shown. The flight check's own two screens are the
    // check, the book a mission end opened is the debrief, and everything else is the cabin.
    private NetCoopScreen HostCoopScreen() => _host.Screen switch
    {
        OriginalScreen.CampaignBriefing => NetCoopScreen.Briefing,
        OriginalScreen.CampaignFlightCheck or OriginalScreen.CampaignAmmo or OriginalScreen.CampaignPlaneSelection => NetCoopScreen.FlightCheck,
        _ when _debriefing => NetCoopScreen.Debrief,
        _ => NetCoopScreen.Cabin,
    };

    // A guest takes the board its host names, re-entering it only when the board or the mission
    // moves. Its own cursor and its ammo and plane screens are left alone meanwhile.
    private bool FollowHost(NetPlayFeature net, CoopFlowMessage flow)
    {
        var campaign = _campaign!;
        campaign.FollowHost(flow.Progress, flow.Airframes);
        net.PickCoop(campaign.GuestAirframe, net.CoopPickReady, campaign.GuestCoopFit);
        bool ready = net.CoopReady;
        bool changed = campaign.GuestReady != ready || net.CoopFlows != _guestFlows;
        campaign.GuestReady = ready;
        if (flow.Screen == NetCoopScreen.Debrief && net.CoopFlows != _guestFlows)
        {
            campaign.RecordGuestResult(flow.MissionSeq, _guestAttempt, flow.Objectives, flow.Cash);
        }

        _guestFlows = net.CoopFlows;
        if (flow.Screen == _guestScreen && flow.MissionSeq == _guestSeq)
        {
            return changed;
        }

        _guestScreen = flow.Screen;
        _guestSeq = flow.MissionSeq;
        switch (flow.Screen)
        {
            case NetCoopScreen.Briefing:
                campaign.SetMission(flow.MissionSeq);
                _briefingReturn = OriginalScreen.CampaignCabin;
                ShowCampaign(OriginalScreen.CampaignBriefing);
                break;
            case NetCoopScreen.FlightCheck:
                campaign.SetMission(flow.MissionSeq);
                if (!OnCheck)
                {
                    ShowCampaign(OriginalScreen.CampaignFlightCheck);
                }

                break;
            case NetCoopScreen.Debrief:
                campaign.RecordGuestResult(flow.MissionSeq, _guestAttempt, flow.Objectives, flow.Cash);
                OpenBook(flow.MissionSeq);
                break;
            default:
                _guestAttempt = null;
                ShowCampaign(OriginalScreen.CampaignCabin);
                break;
        }

        return true;
    }

    // What a guest's press may do. Its own two screens take every press but EXPORT. Its check
    // takes the three buttons that are its own. Every other row is greyed and moves nothing.
    private bool GuestPressLive(OriginalRow row)
    {
        var screen = CampaignScreenOf(_host.Screen);
        int pageRow = RowIndexOf(row.Key);
        if (screen is CampaignScreen.Ammo or CampaignScreen.PlaneSelection)
        {
            return pageRow < 0 || GuestRowLive(screen, _flow!.Page.Button(pageRow).Button);
        }

        return pageRow >= 0 && GuestRowLive(screen, _flow!.Page.Button(pageRow).Button);
    }

    private void ToggleGuestReady()
    {
        if (_net() is not { IsCoopGuest: true } net || _campaign == null)
        {
            return;
        }

        net.PickCoop(_campaign.GuestAirframe, !net.CoopPickReady, _campaign.GuestCoopFit);
        _campaign.GuestReady = net.CoopPickReady;
    }

    // Leaving hangs up; the shell then takes the guest back to the Connection page.
    private void AskLeaveSession()
    {
        if (_net() is not { } net)
        {
            return;
        }

        _host.RaiseDialog(CoopDoorText.LeaveQuestion, DialogIcon.Query, Yes(net.Close), No());
    }

    // Leaving the campaign closes the cabin's door and gives the router's port back. A door the
    // Connection page opened is left alone.
    private void CloseCoopDoor()
    {
        if (_net() is { IsCoopHost: true } net)
        {
            net.Close();
        }
    }

    // The cabin's network door row, and the host's band over a dark ground while it is open.
    private void ComposeCoopDoor(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        if (IsGuest && _net() is { } guest)
        {
            ComposeBand(CoopDoorText.GuestBand(guest), layers);
            return;
        }

        if (!CoopDoorShown)
        {
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Key == CoopDoorKey)
            {
                bool pressed = !_host.DialogOpen && _host.PressedRow == i;
                _host.ComposeGenericRow(rows[i], i == focus && !_host.DialogOpen, pressed, i, layers);
            }
        }

        ComposeBand(CoopDoorText.HostBand(_net()!), layers);
    }

    private void ActivateCabin(OriginalRow row, int pageRow)
    {
        if (_campaign?.Profile is not { } profile)
        {
            LeaveCampaign();
            return;
        }

        // The cheat's mission pull-down is a row of the page, so its press is the page's own.
        if (row.Kind == OriginalRowKind.Dropdown)
        {
            PagePress(pageRow);
            return;
        }

        switch (row.Key)
        {
            case nameof(BoardButton.NextMission):
                _campaign.SetMission(_host.CheatedMission(CampaignProgression.NextMissionSeq(profile)));
                EnterBriefing(OriginalScreen.CampaignCabin);
                break;
            case nameof(BoardButton.PreviousMissions):
                ShowCampaign(OriginalScreen.CampaignPreviousMissions);
                break;
            case nameof(BoardButton.PlaneConstruction):
                _host.OpenHangar(_campaign.Wallet());
                break;
            case nameof(BoardButton.ReturnToMainMenu):
                LeaveCampaign();
                break;
            case nameof(BoardButton.ChangeMemento):
                ShowCampaign(OriginalScreen.CampaignMemento);
                break;
        }
    }

    private void ActivateBriefing(OriginalRow row)
    {
        switch (row.Key)
        {
            case nameof(BoardButton.ReplayBriefing):
                _campaign?.Briefing?.Restart();
                break;
            case nameof(BoardButton.GoToFlightCheck):
                ShowCampaign(OriginalScreen.CampaignFlightCheck);
                break;
        }
    }

    // The flight check. CHANGE AMMO and CHANGE PLANE name their crew slot and open their screen,
    // and RETURN TO BRIEFING abandons the walk. FLY MISSION advances to the next joined human's
    // check. On the last it leaves as the feature's launch with every seat's devices.
    private MenuExit? ActivateFlightCheck(OriginalRow row)
    {
        if (_campaign == null || _flow == null)
        {
            return null;
        }

        int pageRow = RowIndexOf(row.Key);
        var reference = pageRow >= 0 ? _flow.Page.Button(pageRow) : BoardButtonRef.None;
        switch (reference.Button)
        {
            case BoardButton.ChangeAmmo:
                _campaign.SetAmmoSlot(reference.Slot);
                ShowCampaign(OriginalScreen.CampaignAmmo);
                return null;
            case BoardButton.ChangePlane:
                _campaign.SetPlaneSlot(reference.Slot);
                ShowCampaign(OriginalScreen.CampaignPlaneSelection);
                return null;
            case BoardButton.ReturnToBriefing:
                _campaign.Field.Rewind();
                ShowCampaign(OriginalScreen.CampaignBriefing);
                return null;
            case BoardButton.FlyMission:
                if (IsGuest)
                {
                    ToggleGuestReady();
                    return null;
                }

                _campaign.Field.SetPlayers(_setup.Seats.Count);
                if (LaunchWaits)
                {
                    return null;
                }

                if (_campaign.Field.Advance())
                {
                    ShowCampaign(OriginalScreen.CampaignFlightCheck);
                    return null;
                }

                var pads = new List<IReadOnlyList<int>>(_setup.Seats.Count);
                foreach (var seat in _setup.Seats)
                {
                    pads.Add(_flightDevices(seat));
                }

                // A co-op host's guests fly with it, so the door's wire leaves with the launch.
                var exit = _campaign.BuildExit(pads);
                if (exit != null && _net() is { IsCoopHost: true } net)
                {
                    exit = exit with { Net = net.BuildLaunch() };
                }

                return exit;
            default:
                return null;
        }
    }

    // The profile screen's own list drawing, CAMPAIGN.SCRIPT's sub-script. The selection bar goes
    // behind the row the box names, the frame around the row under the pointer. Both go over the
    // list rows the board component wrote. The box shows the typed name with a caret while it is
    // the focused row. ⚠ None of the three stands while a box does. Its answers are the rows
    // then, and the page under it is drawn with nothing focused and nothing hovered.
    private void ComposeRoster(IReadOnlyList<OriginalRow> rows, int focus, IReadOnlyList<BoardLine> composed, BoardLayers layers)
    {
        string name = RosterName;
        int picked = -1;
        int hover = _host.HoveredRow;
        if (!_host.DialogOpen)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind == OriginalRowKind.ListRow && rows[i].Label.TrimStart('✓', ' ') == name && name.Length > 0)
                {
                    picked = i;
                }
            }
        }

        if (picked >= 0)
        {
            var bar = rows[picked];
            layers.Fills.Add(new BoardFill(bar.X, bar.Y, bar.Width, bar.Height, RosterBarRed, 0, 0));
        }

        if (!_host.DialogOpen && hover >= 0 && hover < rows.Count && rows[hover].Kind == OriginalRowKind.ListRow)
        {
            var framed = rows[hover];
            layers.Fills.Add(new BoardFill(framed.X, framed.Y, framed.Width, framed.Height, RosterFrameRed, 0, 0, Border: true));
        }

        foreach (var line in composed)
        {
            if (line.Row == 0)
            {
                bool caret = !_host.DialogOpen && focus == 0;
                layers.Lines.Add(line with { Text = caret ? name + "_" : name });
            }
            else if (line.Row > 0 && line.Text.StartsWith("✓ ", StringComparison.Ordinal))
            {
                layers.Lines.Add(line with { Text = line.Text[2..] });
            }
            else
            {
                layers.Lines.Add(line);
            }
        }
    }

    // The messagebox script's own answer words, read through the campaign's own string table. The
    // one-button box takes langui 100 (OK), the two-button pair 102 and 103 (Yes, No). The keys
    // are the shell's own, the box being the shell's: a key of this module's invention would
    // leave its answers unanswerable.
    private OriginalDialogAnswer Ok(Action? run = null) =>
        new(OriginalShell.DialogOkKey, CampaignBoards.DialogCenterKey, DialogWord(100, "OK"), run);

    private OriginalDialogAnswer Yes(Action run) =>
        new(OriginalShell.DialogYesKey, CampaignBoards.DialogLeftKey, DialogWord(102, "Yes"), run);

    private OriginalDialogAnswer No() =>
        new(OriginalShell.DialogNoKey, CampaignBoards.DialogRightKey, DialogWord(103, "No"), null);

    private string DialogWord(int id, string fallback)
    {
        string word = _campaign?.Strings.Text(id, fallback) ?? fallback;
        return word.Length > 0 ? word : fallback;
    }
}
