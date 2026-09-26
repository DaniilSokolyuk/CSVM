using System;
using System.Collections.Generic;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using CSVM.UI.Screens;

namespace CSVM.UI.Campaign;

/// <summary>The campaign's out-of-mission screens, in the order the original walks them.</summary>
public enum CampaignScreen
{
    /// <summary>Pick, create or delete a player profile.</summary>
    Roster,

    /// <summary>The cabin hub the rest of the campaign hangs off.</summary>
    Cabin,

    /// <summary>The memento chooser, the cabin's CHANGE MEMENTO door.</summary>
    MementoSelection,

    /// <summary>The scrapbook's finished-missions list, opened from the cabin.</summary>
    PreviousMissions,

    /// <summary>The mission briefing.</summary>
    Briefing,

    /// <summary>The pilot's and wingmen's planes and loadouts before the flight.</summary>
    FlightCheck,

    /// <summary>Ammunition and ordnance for one aircraft.</summary>
    Ammo,

    /// <summary>The aircraft each crew slot flies, picked from the profile's own.</summary>
    PlaneSelection,

    /// <summary>The scrapbook's results page (spread 1), opened on the mission a finished mission
    /// just flew, with the cabin on its far side.</summary>
    Scrapbook,

    /// <summary>One scrap's detail view, opened on <see cref="CampaignFlow.ZoomTarget"/> and
    /// closing back to <see cref="Scrapbook"/>.</summary>
    ScrapbookZoom,
}

/// <summary>How a campaign flow ended, or that it is still running.</summary>
public enum CampaignExit
{
    /// <summary>Still on a screen.</summary>
    None,

    /// <summary>Left the campaign for the launchscreen.</summary>
    Cancelled,

    /// <summary>Plane Construction: the shell opens the hangar over the profile's wallet and calls
    /// <see cref="CampaignFlow.Resume"/> when it closes, which lands back on the cabin.</summary>
    OpenHangar,

    /// <summary>FLY MISSION: the shell launches the campaign session for
    /// <see cref="CampaignFlow.MissionSeq"/> over <see cref="CampaignFlow.Profile"/>.</summary>
    FlyMission,
}

/// <summary>
/// One campaign screen, as the shell draws and drives it. Everything is plain text and plain
/// indices, so a page is engine-free and testable: the shell owns every Godot control. Navigation
/// is the page's own (<see cref="Accept"/> calls <see cref="CampaignFlow.GoTo"/>), unlike the
/// hangar's linear screen order; <see cref="Back"/> returning false lets the flow leave the screen.
/// A page with a <see cref="TextEntry"/> takes the keyboard's letters as text while that field is
/// armed, and the flow's cursor axes then edit the name instead of the list.
/// </summary>
public interface ICampaignPage
{
    /// <summary>Which screen this page is.</summary>
    CampaignScreen Screen { get; }

    /// <summary>The heading.</summary>
    string Title { get; }

    /// <summary>How many rows the page draws right now.</summary>
    int RowCount { get; }

    /// <summary>The row the cursor lands on when the flow arrives.</summary>
    int OpeningRow { get; }

    /// <summary>The controls line for this screen's current state.</summary>
    string Footer { get; }

    /// <summary>The picture the shell draws beside the rows, or null for none.</summary>
    HangarArt? Art { get; }

    /// <summary>The field this page types into, or null when it has none.</summary>
    CampaignTextEntry? TextEntry { get; }

    /// <summary>The pictures this page's own data decides, under the screen's fixed chrome and in
    /// draw order: the screen background where the page picks it, the photograph behind the cabin
    /// window, the briefing's flags. Positions are authored 800x600 pixels.</summary>
    IReadOnlyList<BoardPicture> Pictures { get; }

    /// <summary>The connector lines this page's data draws over its pictures.</summary>
    IReadOnlyList<BoardStroke> Strokes { get; }

    /// <summary>Rectangles this page paints on the screen's background and under its own pictures:
    /// a list widget's selection bar and its scrollbar track, which are what the original draws
    /// with <c>ldrawrect</c> rather than with art.</summary>
    IReadOnlyList<BoardFill> Fills { get; }

    /// <summary>Text the screen carries that is not a row: a panel heading, a title widget.</summary>
    IReadOnlyList<BoardLine> Captions { get; }

    /// <summary>List widgets whose entries flow rather than sitting at a fixed pitch: the briefing
    /// parchment's objectives note, whose lines are read and never selected.</summary>
    IReadOnlyList<BoardNote> Notes { get; }

    /// <summary>Which of the screen's authored buttons row <paramref name="row"/> presses.
    /// <see cref="BoardButton.None"/> means the row is list text, which is the default.</summary>
    BoardButtonRef Button(int row);

    /// <summary>The drop-down list row <paramref name="row"/> is, or null when it is not one. ⚠ A
    /// combo may only be open while its own row is focused: the flow finds the open one by asking
    /// the focused row, so a page that leaves one open behind a moved cursor strands it.</summary>
    CampaignCombo? Combo(int row);

    /// <summary>A second, smaller picture for the focused row, or null for none.</summary>
    HangarArt? RowArt(int row);

    /// <summary>Whether the cursor may stand on row <paramref name="row"/>. False makes the row
    /// text the screen draws and never selects: the flight check's crew headings name the aircraft
    /// under the cursor's reach rather than being a thing to press.</summary>
    bool Focusable(int row);

    /// <summary>Row <paramref name="row"/>'s text.</summary>
    string RowText(int row);

    /// <summary>The detail line under the list for the focused row, or "" for none.</summary>
    string Detail(int row);

    /// <summary>Which row's <see cref="Detail"/> pane <paramref name="pane"/> reads while the
    /// cursor is on <paramref name="row"/>, or -1 for a pane this screen does not author. A screen
    /// with one pane answers the focused row and nothing else; the ammo screen fills both at once,
    /// so the pane the cursor is not in names the row it keeps describing.</summary>
    int DetailRow(BoardDetailPane pane, int row);

    /// <summary>The horizontal stepper on the focused row. Returns whether anything changed.</summary>
    bool Step(int row, int dir);

    /// <summary>The confirm press on the focused row. Returns whether the page handled it.</summary>
    bool Accept(int row);

    /// <summary>The secondary press on the focused row (X), the one shortcut a screen offers beside
    /// its confirm. Returns whether the page handled it; a screen with no such shortcut says
    /// false and the press is nothing.</summary>
    bool Secondary(int row);

    /// <summary>The back press. Returns true when the page consumed it (a confirm stage closing, a
    /// text field disarming); false lets the flow leave the screen.</summary>
    bool Back();
}

/// <summary>
/// Built-in's campaign screen graph: the screens between the launchscreen and a mission, walked
/// as a stack over the shared <see cref="CampaignFeature"/>, which owns the profile, the seated
/// player, the mission named and every write into the store. Engine-free, so the screen graph, the
/// cursor and the cancel semantics test off engine; the launchscreen is only its renderer and input
/// source. Screens are a stack rather than the hangar's fixed order, because the campaign's own
/// navigation is a graph: the cabin opens a briefing, the briefing opens a flight check, and each
/// of them returns to what opened it. A screen with no registered page draws a placeholder. The
/// feature's state is read through the forwarding members here, so a page needs no second door.
/// </summary>
public sealed class CampaignFlow
{
    /// <summary>The roster's capacity, <see cref="CampaignFeature.MaxProfiles"/>.</summary>
    public const int MaxProfiles = CampaignFeature.MaxProfiles;

    // Which page draws which screen. THIS is the wave's mount point: a new screen lands as one
    // page file plus one line here, and nothing in the launchscreen changes.
    private static readonly Dictionary<CampaignScreen, Func<CampaignFlow, ICampaignPage>> Registry = new()
    {
        [CampaignScreen.Roster] = flow => new CampaignRosterPage(flow),
        [CampaignScreen.Cabin] = flow => new CampaignCabinPage(flow),
        [CampaignScreen.MementoSelection] = flow => new CampaignMementoPage(flow),
        [CampaignScreen.PreviousMissions] = flow => new CampaignPreviousMissionsPage(flow),
        [CampaignScreen.Briefing] = flow => new CampaignBriefingPage(flow),
        [CampaignScreen.FlightCheck] = flow => new CampaignFlightCheckPage(flow),
        [CampaignScreen.Ammo] = flow => new CampaignAmmoPage(flow),
        [CampaignScreen.PlaneSelection] = flow => new CampaignPlaneSelectionPage(flow),
        [CampaignScreen.Scrapbook] = flow => new CampaignScrapbookPage(flow),
        [CampaignScreen.ScrapbookZoom] = flow => new CampaignScrapbookZoomPage(flow),
    };

    private readonly Dictionary<CampaignScreen, ICampaignPage> _pages = new();

    // The film in front of the screens, which the two cinema doors below play through. ⚠ Do not
    // call a cinema around it: the screen a film opens is live the moment the film stops, and the
    // presentation polls its seats after the stop, so the press that ended the film would arrive
    // on that screen as an edge of its own.
    private readonly CinemaFilm _film = new();

    // The screens entered, innermost last. Never empty: popping the last one ends the flow.
    private readonly List<CampaignScreen> _stack = new() { CampaignScreen.Roster };

    // The layout the boards read their chrome through, resolved from the data root on first use
    // unless a caller handed one in (a unit test's fixture, a parity suite's fallback).
    private CampaignLayout? _layout;

    /// <summary>Opens a flow over <paramref name="store"/> through a private feature, reading its
    /// roster once. <paramref name="dataRoot"/> may be null; a page's art then simply loads none.
    /// <paramref name="planes"/> is the hangar's build store the flight check and ammo screens
    /// read an owned plane's guns and hardpoints from; null (every off-engine caller) means no
    /// hangar build exists and each plane reads as its airframe's stock fit, which is also what
    /// the two profile-seeded starters are.</summary>
    public CampaignFlow(CampaignProfileStore store, UiStrings strings, string? dataRoot = null,
        CustomPlaneStore? planes = null, StockLoadouts? stock = null, CampaignLayout? layout = null)
        : this(Opened(new CampaignFeature(strings, PlanePickerRoster.AirframeNode), store, planes, stock, dataRoot))
    {
        _layout = layout;
    }

    /// <summary>Opens a flow over the shared <paramref name="feature"/>, which must already be
    /// open on a store; the launchscreen builds every flow this way, over the host's one
    /// feature. <paramref name="layout"/> is the chrome source a caller pins (a suite composing
    /// the same screens twice); null reads the data root's own.</summary>
    public CampaignFlow(CampaignFeature feature, CampaignLayout? layout = null)
    {
        _layout = layout;
        Feature = feature ?? throw new ArgumentNullException(nameof(feature));
        if (!feature.IsOpen)
        {
            throw new InvalidOperationException("The campaign feature must be opened on a store before a flow is built over it.");
        }

        // The opening screen gets its own page's opening row too, not just the screens arrived at
        // later, or the roster would be the one screen that ignores the seam.
        Row = Page.OpeningRow;
        ClampedRow();
    }

    /// <summary>The shared feature this flow walks: the profile, the mission and every write.</summary>
    public CampaignFeature Feature { get; }

    /// <summary>The stock-loadout table (<c>stock_loadouts.json</c>) the flight check and ammo
    /// screens read an airframe's stock fit and the ordnance roster from, or null when the caller
    /// has none; a page loads the default itself only when it needs it, since the default path
    /// is <c>res://</c> and needs the engine.</summary>
    public StockLoadouts? Stock => Feature.Stock;

    /// <summary>The profile store this flow creates, reads and deletes through.</summary>
    public CampaignProfileStore Store => Feature.Store!;

    /// <summary>The hangar's build store (<c>user://Planes/</c>), or null when the caller has none.
    /// Pages never open the store themselves: that call needs the engine, and a page must stay
    /// constructible off it.</summary>
    public CustomPlaneStore? Planes => Feature.Planes;

    /// <summary>The langui table the screens label themselves from.</summary>
    public UiStrings Strings => Feature.Strings;

    /// <summary>What the original's menu cheats have switched on, which the cabin, the table of
    /// contents, the book and the profile screen all read.</summary>
    public CampaignCheats Cheats => Feature.Cheats;

    /// <summary>The humans flying this sortie: how many joined, whose flight check is showing, and
    /// what each guest picked. Solo until the shell says otherwise.</summary>
    public CampaignFlightField Field => Feature.Field;

    /// <summary>The folder <c>extracted/</c> sits in, or null when the caller has none.</summary>
    public string? DataRoot => Feature.DataRoot;

    /// <summary>The decoded menu layout the pages and <see cref="CampaignBoards"/> read the fixed
    /// chrome through: the one under <see cref="DataRoot"/>, or the fallback with no root. Built-in's
    /// alone; the feature carries no presentation geometry.</summary>
    public CampaignLayout Layout => _layout ??= CampaignLayout.For(DataRoot);

    /// <summary>Every stored profile's name, re-read by <see cref="RefreshRoster"/>.</summary>
    public IReadOnlyList<string> Roster => Feature.Roster;

    /// <summary>The profile the player picked, or null while the roster screen is still open.</summary>
    public CampaignProfileDef? Profile => Feature.Profile;

    /// <summary>The <c>cm_sequence.zrd</c> index (0..23) of the mission the briefing, flight check
    /// and ammo screens are about: the profile's next mission after Next Mission, any finished one
    /// after Previous Missions. -1 until the cabin sets it.</summary>
    public int MissionSeq => Feature.MissionSeq;

    /// <summary>Whose aircraft the ammo screen edits: 0 the pilot's, 1 the wingman's (the flight
    /// check's two rows, <c>docs/formats/campaign-screens.md</c>). The flight check sets it before
    /// opening the ammo screen.</summary>
    public int AmmoSlot => Feature.AmmoSlot;

    /// <summary>Which crew slot the plane selection screen opens focused on, 0 the pilot's combo
    /// and 1 the wingman's.</summary>
    public int PlaneSlot => Feature.PlaneSlot;

    /// <summary>How many times the book has been opened through <see cref="OpenScrapbook"/>. The
    /// page watches this rather than <see cref="MissionSeq"/> alone, so reopening it on the mission
    /// it is already browsing still lands on that mission's spread 1, which is what the original's
    /// <c>uiData</c> 2405 mode 1 does however the book is reached.</summary>
    public int ScrapbookEntry => Feature.ScrapbookEntry;

    /// <summary>The scrap <see cref="CampaignScreen.ScrapbookZoom"/> is open on: the
    /// <c>SCRAPBOOK.CSV</c> mission slot, spread and item a scrapbook page's row named. Null
    /// until <see cref="SetScrapbookZoom"/> is called.</summary>
    public (int Mission, int Spread, int Item)? ZoomTarget => Feature.ZoomTarget;

    /// <summary>The <c>cm_sequence.zrd</c> entry <see cref="MissionSeq"/> names, or null when the
    /// data root, the file or the entry is unavailable.</summary>
    public CampaignMission? Mission => Feature.Mission;

    /// <summary>Whether this mission flies a wingman, its <c>cm_sequence</c> flag.</summary>
    public bool MissionHasWingman => Feature.MissionHasWingman;

    /// <summary>The film standing in front of these screens, as the presentation driving them reads
    /// the span: the frames the film owns, and the tail of the press that ended it. A presentation
    /// that polls its input reads this before it applies a frame, or the press that skipped a film
    /// fires a row on the screen the film opened.</summary>
    public CinemaFilm Film => _film;

    /// <summary>The screen showing.</summary>
    public CampaignScreen Screen => _stack[^1];

    /// <summary>The page drawing the current screen.</summary>
    public ICampaignPage Page => PageFor(Screen);

    /// <summary>The focused row on that screen.</summary>
    public int Row { get; private set; }

    /// <summary>Whether the flow is still running, and how it ended if not.</summary>
    public CampaignExit Exit { get; private set; }

    /// <summary>The refusal line the last action left, or "". Cleared by any navigation.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>The dialog standing over the screen, or null. While one stands it takes every
    /// press, so the screen under it neither moves nor changes.</summary>
    public CampaignModal? Modal { get; private set; }

    /// <summary>Whether the keyboard's letters are text right now rather than navigation. The shell
    /// reads this to decide whether to hand over its cursor axes or the pad's alone.</summary>
    public bool CapturesText => Page.TextEntry is { Active: true };

    /// <summary>The drop-down list standing open, or null. Only the focused row's own combo can be
    /// one, which is the invariant <see cref="ICampaignPage.Combo"/> states.</summary>
    public CampaignCombo? OpenCombo =>
        Page.Combo(Math.Clamp(Row, 0, Math.Max(0, Page.RowCount - 1))) is { Open: true } combo
            ? combo
            : null;

    /// <summary>Whether a screen has a page of its own yet.</summary>
    public static bool HasPage(CampaignScreen screen) => Registry.ContainsKey(screen);

    /// <summary>Moves the row cursor, wrapping like every other launchscreen list. While a text
    /// field is armed the same axis grows and shrinks the name instead, which is how a pad enters
    /// one at all.</summary>
    public bool Move(int dir)
    {
        if (Modal != null)
        {
            return false;
        }

        if (Page.TextEntry is { Active: true } entry)
        {
            return dir < 0 ? entry.Append() : entry.Backspace();
        }

        // An open drop-down owns the axis: the cursor is inside the list, not on the screen's rows.
        if (OpenCombo is { } combo)
        {
            Message = string.Empty;
            return combo.Move(dir);
        }

        int count = Page.RowCount;
        if (dir == 0 || count <= 1)
        {
            return false;
        }

        Message = string.Empty;
        Row = (((Row + dir) % count) + count) % count;
        Settle(dir);
        return true;
    }

    /// <summary>Applies the horizontal stepper: the armed text field's letter control, else the
    /// focused row's own stepper.</summary>
    public bool Step(int dir)
    {
        if (dir == 0 || Modal != null)
        {
            return false;
        }

        if (Page.TextEntry is { Active: true } entry)
        {
            return entry.StepLast(dir);
        }

        // The stepper is the closed field's shortcut. Inside an open list it means nothing, and
        // stepping the pick under the cursor would leave the two disagreeing.
        if (OpenCombo != null)
        {
            return false;
        }

        Message = string.Empty;
        return Page.Step(ClampedRow(), dir);
    }

    /// <summary>Types characters into the page's armed text field.</summary>
    public bool Type(string chars)
    {
        if (Page.TextEntry is not { Active: true } entry || chars.Length == 0)
        {
            return false;
        }

        Message = string.Empty;
        return entry.Type(chars);
    }

    /// <summary>Removes the last character of the page's armed text field.</summary>
    public bool Backspace() =>
        Page.TextEntry is { Active: true } entry && entry.Backspace();

    /// <summary>The confirm press on the focused row, which is the page's alone: campaign screens
    /// navigate by naming where they go, not by advancing through a fixed order.</summary>
    public bool Accept()
    {
        // A dialog takes the confirm. The press that raised one cannot also answer it: a page
        // raises from inside its own Accept, which this check has already passed by then.
        if (DismissModal())
        {
            return true;
        }

        Message = string.Empty;
        return Page.Accept(ClampedRow());
    }

    /// <summary>The secondary press, the pad's X: the one shortcut the screen showing offers beside
    /// its confirm, and nothing at all on a screen that offers none. A refusal already on the line
    /// survives a press no page took, since nothing happened for it to be stale about.</summary>
    public bool Secondary()
    {
        if (Modal != null)
        {
            return false;
        }

        if (!Page.Secondary(ClampedRow()))
        {
            return false;
        }

        Message = string.Empty;
        return true;
    }

    /// <summary>The back press: the page first, then leaving the screen. Backing out of the first
    /// screen ends the flow.</summary>
    public bool Back()
    {
        if (DismissModal())
        {
            return true;
        }

        Message = string.Empty;
        if (Page.Back())
        {
            return true;
        }

        if (_stack.Count <= 1)
        {
            Exit = CampaignExit.Cancelled;
            return true;
        }

        // Backing onto the cabin is a cabin door like any other, so it takes OpenCabin rather than
        // the pop: GoTo truncates to the cabin exactly as removing the top would, and the film a
        // chapter opening is due plays in front of it.
        if (_stack[^2] == CampaignScreen.Cabin)
        {
            OpenCabin();
            return true;
        }

        _stack.RemoveAt(_stack.Count - 1);
        Entered();
        return true;
    }

    /// <summary>Opens a screen. One already open is returned to, closing everything entered after
    /// it, so RETURN TO CABIN from a briefing leaves no second cabin behind the first. Entering the
    /// briefing plays its reveal from the start, as its narration does; the load is kept.</summary>
    public void GoTo(CampaignScreen screen)
    {
        Message = string.Empty;
        // A reveal never advanced is left alone: the aid drives one from zero, and the script's
        // narration count stays what a first entry reads.
        if (screen == CampaignScreen.Briefing && Feature.Briefing is { Reveal.Clock: > 0 } briefing)
        {
            briefing.Restart();
        }

        int at = _stack.IndexOf(screen);
        if (at >= 0)
        {
            _stack.RemoveRange(at + 1, _stack.Count - at - 1);
        }
        else
        {
            _stack.Add(screen);
        }

        Entered();
    }

    /// <summary>Ends the flow for the launchscreen, the CANCEL and RETURN TO MAIN MENU press.</summary>
    public void Cancel() => Exit = CampaignExit.Cancelled;

    /// <summary>Hands the shell a job that leaves the flow standing: the hangar, or the mission
    /// itself. The shell reads <see cref="Exit"/>, does the job, and (for the hangar) calls
    /// <see cref="Resume"/>.</summary>
    public void Request(CampaignExit job) => Exit = job;

    /// <summary>Back from a job the shell ran on the flow's behalf: the flow stands where it was,
    /// its profile re-read so a hangar purchase or sale shows on the cabin.</summary>
    public void Resume()
    {
        Exit = CampaignExit.None;
        Message = string.Empty;
        Feature.Resume();
    }

    /// <summary>Names the mission the screens after the cabin are about.</summary>
    public void SetMission(int seq) => Feature.SetMission(seq);

    /// <summary>Points the ammo screen at the pilot's (0) or the wingman's (1) aircraft.</summary>
    public void SetAmmoSlot(int slot) => Feature.SetAmmoSlot(slot);

    /// <summary>Which crew slot's CHANGE PLANE press opened the plane selection screen, the
    /// original's <c>@globals@ZQ</c> of -1 and -2. The screen draws both slots either way; this
    /// only decides which of the two combos the cursor opens on.</summary>
    public void SetPlaneSlot(int slot) => Feature.SetPlaneSlot(slot);

    /// <summary>Where a scrapbook capture's file is for the seated profile, or null when there is
    /// none on disk (<see cref="CampaignFeature.CapturePath"/>).</summary>
    public string? CapturePath(ScrapbookScrap scrap) => Feature.CapturePath(scrap.FileName);

    /// <summary>Names the scrap <see cref="CampaignScreen.ScrapbookZoom"/> opens on.</summary>
    public void SetScrapbookZoom(int mission, int spread, int item) => Feature.SetScrapbookZoom(mission, spread, item);

    /// <summary>Opens the book on a mission's first spread, the original's <c>uiData</c> 2405 mode
    /// 1: the mission-end entry, the table of contents' VIEW SELECTED and both CURRENT MISSION
    /// bookmarks all take this door.</summary>
    public void OpenScrapbook(int seq)
    {
        Feature.EnterScrapbook(seq);
        GoTo(CampaignScreen.Scrapbook);
    }

    /// <summary>Seats a profile, puts the cabin on the book's far side and opens the book the way a
    /// flown mission leaves it: the whole of the mission-end return.
    /// ⚠ The feature's seat and a plain <see cref="GoTo"/>, never <see cref="SelectProfile"/> or
    /// <see cref="OpenCabin"/>: this cabin is stacked and not entered, and a chapter film in front
    /// of it would land the player on the cabin instead of the book they just earned.</summary>
    public void OpenScrapbookAfterMission(CampaignProfileDef profile, int seq, bool missionWon)
    {
        Feature.SelectProfile(profile);
        GoTo(CampaignScreen.Cabin);
        OpenScrapbookAfterMission(seq, missionWon);
    }

    /// <summary>Opens the book the way a finished mission does, playing the closing film first when
    /// the mission just flown earns it (<see cref="ClosingCinema.PlaysAfter"/>, a win on the
    /// campaign's last mission); the book then opens on the frame the film stops. The mission-end
    /// return and the book's own screenshot aid take this door, the aid with no win to report.
    /// ⚠ Not the door the table of contents and the two bookmarks take: those run inside the
    /// campaign, where the original reaches <c>scrapbook.script</c> without <c>FINALCINEMA</c>.</summary>
    public void OpenScrapbookAfterMission(int seq, bool missionWon)
    {
        if (Feature.ClosingCinema is { } cinema)
        {
            _film.Play(then => cinema.OpenScrapbook(seq, missionWon, then), () => OpenScrapbook(seq));
            return;
        }

        OpenScrapbook(seq);
    }

    /// <summary>Takes the joined-player count from the shell, once a frame.</summary>
    public void SetPlayers(int players) => Field.SetPlayers(players);

    /// <summary>The record the ammo screen is editing (<see cref="CampaignFeature.AmmoTarget"/>).</summary>
    public OwnedPlane? AmmoTarget() => Feature.AmmoTarget();

    /// <summary>Seats the profile every screen after the roster reads, and opens the cabin.</summary>
    public void SelectProfile(CampaignProfileDef profile)
    {
        Feature.SelectProfile(profile);
        OpenCabin();
    }

    /// <summary>Opens the cabin on the seated profile, playing that profile's chapter cinema first
    /// where one is due (<see cref="CampaignFeature.ChapterCinema"/>); the cabin then opens on the
    /// frame the film stops. Every door onto the cabin takes this one, inside the campaign and out,
    /// so the latch and the story position decide whether a film is due rather than which door was
    /// taken. With no cinema, and for a position inside a chapter, it is the plain
    /// <see cref="GoTo"/>.</summary>
    public void OpenCabin()
    {
        if (Feature.ChapterCinema is { } cinema && Feature.Profile is { } seated)
        {
            _film.Play(then => cinema.OpenCabin(seated, then), () => GoTo(CampaignScreen.Cabin));
            return;
        }

        GoTo(CampaignScreen.Cabin);
    }

    /// <summary>Re-reads the store's roster, after a profile was created or deleted.</summary>
    public void RefreshRoster() => Feature.RefreshRoster();

    /// <summary>Leaves a refusal on screen, in the original's own words where it has some.</summary>
    public void SetMessage(string message) => Message = message;

    /// <summary>Raises a dialog over the screen showing. A second raise replaces the first rather
    /// than stacking: the original's own box is one script run over the screen, not a stack.</summary>
    public void RaiseModal(string message, string button = "OK", Action? confirmed = null) =>
        Modal = new CampaignModal(message, button, confirmed);

    /// <summary>Puts the cursor on a row, clamped into the page's current list.</summary>
    public void FocusRow(int row)
    {
        Row = Math.Max(0, row);
        ClampedRow();
    }

    /// <summary>Takes the standing dialog off the flow without answering it, for a presentation
    /// that shows a page's dialog its own way and runs the answer itself; null when none stands.
    /// Built-in never calls this: its dialogs are answered through <see cref="Accept"/> and
    /// <see cref="Back"/>.</summary>
    public CampaignModal? TakeModal()
    {
        var modal = Modal;
        Modal = null;
        return modal;
    }

    /// <summary>Takes the refusal line off the flow, for a presentation that shows a page's
    /// refusal its own way; "" when none stands.</summary>
    public string TakeMessage()
    {
        string message = Message;
        Message = string.Empty;
        return message;
    }

    // A private feature opened for the store-first constructor, so the two constructors chain.
    private static CampaignFeature Opened(
        CampaignFeature feature, CampaignProfileStore store, CustomPlaneStore? planes, StockLoadouts? stock, string? dataRoot)
    {
        feature.Open(store, planes, stock, dataRoot);
        return feature;
    }

    // Answers a standing dialog, running whatever was to follow it. Both the confirm and the back
    // press take this door: a one-button dialog has one answer, so skipping the callback on one of
    // the two presses would make what happens next depend on which one the player used.
    private bool DismissModal()
    {
        if (Modal is not { } modal)
        {
            return false;
        }

        Modal = null;
        modal.Confirm();
        return true;
    }

    // The page for a screen, built on first sight and kept, so a page may hold state of its own
    // (a text field's contents, a confirm stage). An unregistered screen draws the placeholder.
    private ICampaignPage PageFor(CampaignScreen screen)
    {
        if (!_pages.TryGetValue(screen, out var page))
        {
            page = Registry.TryGetValue(screen, out var make)
                ? make(this)
                : new CampaignPlaceholderPage(this, screen);
            _pages[screen] = page;
        }

        return page;
    }

    // The screen just arrived on, opened before its page composes anything. ⚠ The flight check's
    // grant must run here and not from a page member: the rows are built at frame rate and read the
    // profile the grant rewrites, so a write behind that read would run every frame.
    private void Entered()
    {
        if (Screen == CampaignScreen.FlightCheck)
        {
            Feature.GrantMissionAircraft();
        }

        Row = Page.OpeningRow;
        ClampedRow();
    }

    // The focused row, kept inside the page's current list: a page whose row count shrank under the
    // cursor (a deleted profile, a confirm stage opening) must not index past its own end.
    private int ClampedRow()
    {
        Row = Math.Clamp(Row, 0, Math.Max(0, Page.RowCount - 1));
        return Settle(1);
    }

    // Walks the cursor along dir until it stands on a row the page takes focus on, one lap at most:
    // a page that refuses every row keeps the cursor where it was rather than spinning.
    private int Settle(int dir)
    {
        int count = Page.RowCount;
        for (int step = 0; step < count && !Page.Focusable(Row); step++)
        {
            Row = (((Row + (dir < 0 ? -1 : 1)) % count) + count) % count;
        }

        return Row;
    }
}

/// <summary>
/// The base every campaign page shares: its flow, and defaults for everything a plain list screen
/// does not need. A page overriding nothing but <see cref="Screen"/>, <see cref="RowCount"/> and
/// <see cref="RowText"/> is a legal screen that leaves on cancel and does nothing on confirm.
/// </summary>
public abstract class CampaignPage : ICampaignPage
{
    /// <summary>Binds the page to its flow.</summary>
    protected CampaignPage(CampaignFlow flow) => Flow = flow;

    /// <inheritdoc/>
    public abstract CampaignScreen Screen { get; }

    /// <inheritdoc/>
    public abstract string Title { get; }

    /// <inheritdoc/>
    public abstract int RowCount { get; }

    /// <inheritdoc/>
    public virtual int OpeningRow => 0;

    /// <inheritdoc/>
    public virtual string Footer => "↑↓  Choose       Enter / A  Select       Esc / B  Back";

    /// <inheritdoc/>
    public virtual HangarArt? Art => null;

    /// <inheritdoc/>
    public virtual CampaignTextEntry? TextEntry => null;

    /// <inheritdoc/>
    public virtual IReadOnlyList<BoardPicture> Pictures => Array.Empty<BoardPicture>();

    /// <inheritdoc/>
    public virtual IReadOnlyList<BoardStroke> Strokes => Array.Empty<BoardStroke>();

    /// <inheritdoc/>
    public virtual IReadOnlyList<BoardFill> Fills => Array.Empty<BoardFill>();

    /// <inheritdoc/>
    public virtual IReadOnlyList<BoardLine> Captions => Array.Empty<BoardLine>();

    /// <inheritdoc/>
    public virtual IReadOnlyList<BoardNote> Notes => Array.Empty<BoardNote>();

    /// <summary>The flow this page belongs to.</summary>
    protected CampaignFlow Flow { get; }

    /// <inheritdoc/>
    public virtual BoardButtonRef Button(int row) => BoardButtonRef.None;

    /// <inheritdoc/>
    public virtual CampaignCombo? Combo(int row) => null;

    /// <inheritdoc/>
    public virtual HangarArt? RowArt(int row) => null;

    /// <inheritdoc/>
    public virtual bool Focusable(int row) => true;

    /// <inheritdoc/>
    public abstract string RowText(int row);

    /// <inheritdoc/>
    public virtual string Detail(int row) => string.Empty;

    /// <summary>The one pane a screen authors carries the focused row's own description, which is
    /// every screen but the ammo one.</summary>
    public virtual int DetailRow(BoardDetailPane pane, int row) => pane == BoardDetailPane.Upper ? row : -1;

    /// <inheritdoc/>
    public virtual bool Step(int row, int dir) => false;

    /// <inheritdoc/>
    public virtual bool Accept(int row) => false;

    /// <inheritdoc/>
    public virtual bool Secondary(int row) => false;

    /// <inheritdoc/>
    public virtual bool Back() => false;
}

/// <summary>
/// A screen with no page of its own: the right heading, one row saying which screen is missing, and
/// a cancel that leaves. It edits nothing, so a flow that walks through it changes no profile.
/// </summary>
public sealed class CampaignPlaceholderPage : CampaignPage
{
    private readonly CampaignScreen _screen;

    /// <summary>Binds the page to its flow and the screen it stands in for.</summary>
    public CampaignPlaceholderPage(CampaignFlow flow, CampaignScreen screen)
        : base(flow) => _screen = screen;

    /// <inheritdoc/>
    public override CampaignScreen Screen => _screen;

    /// <inheritdoc/>
    public override string Title => _screen.ToString().ToUpperInvariant();

    /// <inheritdoc/>
    public override int RowCount => 1;

    /// <inheritdoc/>
    public override string RowText(int row) => "Back";

    /// <inheritdoc/>
    public override string Detail(int row) => $"The {_screen} screen has no page yet.";
}
