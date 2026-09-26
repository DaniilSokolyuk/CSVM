using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Hangar;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The remake-only per-seat aircraft screen: once seat 0 has picked on a sortie screen, or has
/// pressed FLY MISSION on Instant Action with a second pilot joined, each joined seat in player
/// order picks its own aircraft here, on the campaign plane-selection board's shape (its list field,
/// ratings and weapon column, WEAPON LOADOUT over a selection, ACCEPT and CANCEL SELECTIONS) over
/// the sortie roster. The list stands open while the seat browses; Accept selects and closes it,
/// Accept again confirms and hands the screen on, and WEAPON LOADOUT opens the loadout screen on
/// that seat's own fit. Back and CANCEL SELECTIONS each drop a selection and reopen the list, and
/// over the open list each leaves the walk with every seat kept; nothing here unjoins. Only the
/// picking seat's device drives the screen, and the mouse riding seat 0's source. The last seat's
/// confirm is the launch, or the return of a screen whose FLY gate is unmet. Nothing is decoded.
/// </summary>
public sealed partial class OriginalShell
{
    /// <summary>The per-seat screen's list field, the one field row of its page.</summary>
    public const string SeatPlaneFieldKey = "FIELD:0";

    private SeatPlanePage? _seatPage;
    private PlayerSeat? _pickingSeat;
    private OriginalScreen _seatReturn = OriginalScreen.FreeFlight;

    /// <summary>The seat picking on the per-seat screen, as its index, or -1 off that screen.</summary>
    public int PickingSeat => _screen == OriginalScreen.SeatPlane && _pickingSeat is { } seat ? SeatIndex(seat) : -1;

    /// <summary>The screen the per-seat walk returns to, meaningful while <see cref="PickingSeat"/> is not -1.</summary>
    public OriginalScreen SeatReturn => _seatReturn;

    // The screens a per-seat walk stands on: the picker itself, and the Weapon Loadout it opens for
    // the picking seat. ⚠ Both, not just the picker. The seat that opened the loadout still owns
    // every frame while it is showing, and the walk has to survive the trip; treating only the
    // picker as the walk's screen made the picking seat's Back unjoin it from the loadout screen.
    private bool OnSeatWalk =>
        _screen == OriginalScreen.SeatPlane
        || (_screen == OriginalScreen.InstantActionLoadout && InstantAction.OnSeatLoadout);

    /// <summary>Picks the first chapter and the first aircraft for seat 0 on the sortie screen
    /// showing, the screenshot aid's pose; with a joined seat still to confirm, the per-seat
    /// screen opens at once. Nothing happens off the sortie screens.</summary>
    public void PoseSortiePick()
    {
        if (!IsSortie || Seat0 is not { } seat)
        {
            return;
        }

        PickedChapterIndex = 0;
        if (_screen == OriginalScreen.FreeFlight)
        {
            _free.SelectChapter(_chapters[0].Code);
        }

        if (!seat.Locked)
        {
            _setup.Browse(seat, 0);
            _setup.Select(seat);
        }

        BeginSeatWalkIfDue();
    }

    // Seat 0's frame as the screen showing takes it: whole, except on a screen standing for another
    // seat, where only the pointer is kept. That is a walk's screens while another seat is picking,
    // and the campaign check's screens while the field names a guest. ⚠ Keep the pointer. The mouse
    // rides seat 0's source, so dropping the whole frame would take the one device a pilot without
    // a pad of their own can pick with; the seat's identity, never the device kind, decides here.
    private MenuCommands SeatZeroFrame(MenuCommands commands) =>
        (OnSeatWalk && _pickingSeat != null && !ReferenceEquals(_pickingSeat, Seat0)) || Campaign.CheckSeat > 0
            ? new MenuCommands { Pointer = commands.Pointer }
            : commands;

    private int SeatIndex(PlayerSeat seat)
    {
        var seats = _setup.Seats;
        for (int i = 0; i < seats.Count; i++)
        {
            if (ReferenceEquals(seats[i], seat))
            {
                return i;
            }
        }

        return -1;
    }

    // The first joined seat after seat 0 that has not confirmed, or null: the order the walk takes.
    private PlayerSeat? NextUnconfirmed()
    {
        var seats = _setup.Seats;
        for (int i = 1; i < seats.Count; i++)
        {
            if (!seats[i].Confirmed)
            {
                return seats[i];
            }
        }

        return null;
    }

    // On a sortie screen, once seat 0 has picked, a joined seat still to confirm gets the per-seat
    // screen. Read every frame, so a seat joining after seat 0's pick is walked too.
    private bool BeginSeatWalkIfDue()
    {
        if (!IsSortie || Seat0 is not { Locked: true } || NextUnconfirmed() is not { } next)
        {
            return false;
        }

        EnterSeatPlane(next, _screen);
        return true;
    }

    // FLY MISSION with a second pilot joined: seat 0's pick is the Pilot Plane row, the setup's
    // roster becomes the Pilot Plane list so every seat's cursor indexes the same rows, and the
    // walk starts; with nobody left to pick it is the launch itself.
    private MenuExit? BeginInstantActionSeatWalk()
    {
        if (Seat0 is not { } seat)
        {
            return null;
        }

        var roster = InstantAction.PilotRoster;
        _setup.SetRoster(roster);
        int last = Math.Max(0, roster.Count - 1);
        foreach (var joined in _setup.Seats)
        {
            joined.Cursor = Math.Clamp(joined.Cursor, 0, last);
        }

        while (_setup.Back(seat) != SeatBack.Browsing)
        {
        }

        _setup.Browse(seat, Math.Clamp(InstantAction.PilotRow, 0, last));
        _setup.Select(seat);
        _seatReturn = OriginalScreen.InstantAction;
        return AdvanceSeatWalk();
    }

    private void EnterSeatPlane(PlayerSeat seat, OriginalScreen returnTo)
    {
        _pickingSeat = seat;
        _seatReturn = returnTo;
        _seatPage = new SeatPlanePage(this, seat);
        Open(OriginalScreen.SeatPlane);
        _focus[(int)OriginalScreen.SeatPlane] = 0;
    }

    // After a confirm or an unjoin: the next seat still to pick, else the end of the walk.
    private MenuExit? AdvanceSeatWalk()
    {
        if (NextUnconfirmed() is { } next)
        {
            EnterSeatPlane(next, _seatReturn);
            return null;
        }

        return FinishSeatWalk();
    }

    // Every seat confirmed: the launch, whichever screen the walk came from, the last seat's
    // confirm standing in for seat 0's press there. The screen it returns to opens first, so a
    // sortie launch reads that screen's own chapter and mode. ⚠ Leave the gate to FLY's own rule:
    // Dogfight without a second seat, or a sortie with no map picked, must return the screen with
    // FLY to press rather than fly.
    private MenuExit? FinishSeatWalk()
    {
        var back = _seatReturn;
        Open(back);
        if (back != OriginalScreen.InstantAction)
        {
            return IsSortie ? Fly() : null;
        }

        if (Seat0 is not { } seat)
        {
            return null;
        }

        _setup.Confirm(seat);
        return _instantAction.BuildExit(_setup.Choices(_flightDevices));
    }

    // Back's one way out of the walk, whoever pressed it: the screen it came from returns with
    // every seat kept, so a pick made again walks them again. ⚠ Unwind seat 0 all the way to
    // browsing; a sortie screen reopens the walk every frame seat 0's pick still stands, so a
    // single stage back would put the walk straight up again and Back would have no exit.
    private void CancelSeatWalk()
    {
        if (Seat0 is { } seat)
        {
            while (_setup.Back(seat) != SeatBack.Browsing)
            {
            }
        }

        Open(_seatReturn);
    }

    private MenuExit? ActivateSeatPlane(OriginalRow row)
    {
        if (_seatPage is not { } page || _pickingSeat is not { } seat)
        {
            return null;
        }

        if (OriginalWidgets.Entry(row.Key) is { } entry)
        {
            page.List.Move(entry - page.List.Highlight);
            TakeSeatPick(page, seat);
            return null;
        }

        switch (row.Key)
        {
            case SeatPlaneFieldKey:
                if (seat.Locked)
                {
                    return ConfirmSeatPick(seat);
                }

                if (page.List.Open)
                {
                    TakeSeatPick(page, seat);
                }
                else
                {
                    page.List.Expand();
                }

                return null;
            case nameof(BoardButton.ChangeAmmo):
                InstantAction.OpenSeatLoadout(seat);
                return null;
            case nameof(BoardButton.AcceptSelections):
                if (seat.Locked)
                {
                    return ConfirmSeatPick(seat);
                }

                TakeSeatPick(page, seat);
                return null;
            case nameof(BoardButton.CancelSelections):
                // The pointer's Back, in the picking seat's own two stages: the selection goes
                // first, then the walk. ⚠ Keep the second stage; a mouse has no other way off this
                // screen, Back being the picking seat's device alone.
                if (seat.Locked)
                {
                    ReopenSeatList(page, seat);
                }
                else
                {
                    CancelSeatWalk();
                }

                return null;
            default:
                return null;
        }
    }

    // The first Accept: the highlighted row becomes the seat's cursor and its selection, and the
    // list closes over it, the silhouette and ratings now standing for the pick.
    private void TakeSeatPick(SeatPlanePage page, PlayerSeat seat)
    {
        int pick = page.List.Confirm() ?? page.List.Highlight;
        page.List.Select(pick);
        _setup.Browse(seat, pick);
        _setup.Select(seat);
        _focus[(int)OriginalScreen.SeatPlane] = 0;
    }

    private MenuExit? ConfirmSeatPick(PlayerSeat seat)
    {
        _setup.Confirm(seat);
        return AdvanceSeatWalk();
    }

    // The picking seat's undo, taken by Back over a closed list and by CANCEL SELECTIONS: the
    // selection goes and the list reopens over it, the seat keeping its place in the walk.
    private void ReopenSeatList(SeatPlanePage page, PlayerSeat seat)
    {
        _setup.Back(seat);
        page.List.Expand();
        _focus[(int)OriginalScreen.SeatPlane] = 0;
    }

    // Back on the per-seat screen, which only the picking seat's device can press: it takes back a
    // selection first and leaves the walk from the open list, so two presses at most reach the
    // screen the walk came from. Neither unjoins, a pilot leaving the sortie only on the Instant
    // Action screen or with their device.
    private MenuExit? BackSeatPlane()
    {
        if (_seatPage is not { } page || _pickingSeat is not { } seat)
        {
            return AdvanceSeatWalk();
        }

        if (seat.Locked)
        {
            ReopenSeatList(page, seat);
            return null;
        }

        CancelSeatWalk();
        return null;
    }

    // The screen as the shared board component composes it over the seat's page, with the seat
    // strip naming who is picking.
    private void ComposeSeatPlane(int focus, BoardLayers layers)
    {
        if (_seatPage is not { } page)
        {
            return;
        }

        bool pressed = _pressed >= 0 && _pressed == focus;
        var board = CampaignBoards.For(page, focus, pressed, string.Empty, null, _campaignLayout);
        layers.Backdrop.AddRange(board.Backdrop);
        layers.Fills.AddRange(board.Fills);
        layers.Pictures.AddRange(board.Pictures);
        layers.Lines.AddRange(board.Lines);
        layers.Plaques.AddRange(board.Plaques);
        layers.Overlays.AddRange(board.Overlays);
        if (CampaignSeatPanel() is { } strip)
        {
            layers.Overlays.Add(strip);
        }
    }

    /// <summary>
    /// One seat's picker as a campaign page, so <c>CampaignBoards.For</c> draws it in the
    /// plane-selection board's shape: the list field over the sortie roster, the silhouette of
    /// the row under the cursor, its ratings and weapon column, WEAPON LOADOUT over a selection, and
    /// ACCEPT and CANCEL SELECTIONS. The page composes only; the shell applies every press itself,
    /// since the picks are the shared setup's stages and not a profile's slots.
    /// </summary>
    private sealed class SeatPlanePage : ICampaignPage
    {
        // The pilot block's authored geometry, the fallback under each row read.
        private const string Section = CampaignLayout.PlaneSelectionSection;
        private const float ComboX = 138f;
        private const float ComboY = 132f;
        private const float ComboWidth = 271f;
        private const int ItemHeight = 15;
        private const int ItemsDisplayed = 13;
        private const float HeadingY = 102f;

        // The board's second crew block, which one seat's picker leaves empty: the campaign page's
        // own drop onto the wingman heading, where the other seats' standing goes instead.
        private const float SlotDrop = 218f;
        private const float PlaneNameX = 236f;
        private const float PlaneNameY = 106f;
        private const float SilhouetteX = 444f;
        private const float SilhouetteY = 138f;
        private const float RatingX = 430f;
        private const float RatingY = 228f;
        private const float RatingPitch = 17f;
        private const float RatingWidth = 140f;
        private const float WeaponsX = 578f;
        private const float WeaponsWidth = 160f;
        private const float TitleX = 132f;
        private const float TitleY = 36f;
        private const float TitleWidth = 190f;
        private const float LineX = 136f;
        private const float LineY = 70f;
        private const float LineWidth = 500f;
        private const float TitleFont = 20f;
        private const float BodyFont = 12f;
        private const int SilhouetteFrames = 12;

        // The plaques after the list field, in the order the campaign board's own page puts them:
        // the crew block's paper button, then the two commit strips. WEAPON LOADOUT stands only over
        // a selection, because a fit needs an aeroplane to hang on and the seat has picked none
        // while its list is open. Every lock and unlock parks the focus back on the list, so the row
        // moving with the stage costs no cursor.
        private static readonly BoardButton[] SelectedPlaques =
            { BoardButton.ChangeAmmo, BoardButton.AcceptSelections, BoardButton.CancelSelections };

        private static readonly BoardButton[] BrowsingPlaques =
            { BoardButton.AcceptSelections, BoardButton.CancelSelections };

        private static readonly string[] RatingLabels = { "TOP SPEED:", "ARMOR:", "AGILITY:", "OFFENSE:" };
        private static readonly string[] RatingKeys = { "PS_T_TOPSPEEDP", "PS_T_ARMORP", "PS_T_AGILITYP", "PS_T_OFFENSEP" };
        private static readonly string[] RatingWords = { "Poor", "Fair", "Average", "Good", "Excellent" };
        private static readonly int[] Calibres = { 70, 60, 50, 40, 30 };

        private readonly OriginalShell _shell;
        private readonly PlayerSeat _seat;

        public SeatPlanePage(OriginalShell shell, PlayerSeat seat)
        {
            _shell = shell;
            _seat = seat;
            var layout = shell._campaignLayout;
            var (x, y, width) = layout.Box(Section, "PS_D_PILOTPLANE", ComboX, ComboY, ComboWidth);
            List = new CampaignCombo(x, y, width,
                layout.Int(Section, "PS_D_PILOTPLANE", "ItemHeight", ItemHeight),
                layout.Int(Section, "PS_D_PILOTPLANE", "TotalDisplayed", ItemsDisplayed));
            var roster = shell._setup.Roster;
            var entries = new List<string>(roster.Count);
            foreach (var row in roster)
            {
                entries.Add(row.Name);
            }

            List.Load(entries, seat.Cursor);
            if (!seat.Locked)
            {
                List.Expand();
            }
        }

        /// <summary>The list field: open while the seat browses, closed over its selection.</summary>
        public CampaignCombo List { get; }

        public CampaignScreen Screen => CampaignScreen.PlaneSelection;

        public string Title => "PLANE SELECTION";

        public int RowCount => Plaques.Length + 1;

        public int OpeningRow => 0;

        public string Footer => List.Open
            ? "↑↓  Choose       Enter / A  Select       Esc / B  Leave"
            : "Enter / A  Confirm       Esc / B  Change";

        public HangarArt? Art => null;

        public CampaignTextEntry? TextEntry => null;

        public IReadOnlyList<BoardStroke> Strokes => Array.Empty<BoardStroke>();

        public IReadOnlyList<BoardFill> Fills => Array.Empty<BoardFill>();

        public IReadOnlyList<BoardNote> Notes => Array.Empty<BoardNote>();

        /// <summary>The silhouette of the aircraft under the cursor, the airframe's frame of the
        /// icon sheet at <c>PS_P_PILOTPLANE</c>.</summary>
        public IReadOnlyList<BoardPicture> Pictures
        {
            get
            {
                if (Current is not { } row || PlanePickerRoster.AirframeOf(row.Node) is not { } airframe)
                {
                    return Array.Empty<BoardPicture>();
                }

                var layout = _shell._campaignLayout;
                var fallback = new BoardArt(BoardArtLibrary.Ui, "FC_PlaneIcons.Png", SilhouetteFrames);
                var (x, y) = layout.At(Section, "PS_P_PILOTPLANE", SilhouetteX, SilhouetteY);
                return new[] { new BoardPicture(layout.Art(Section, "PS_P_PILOTPLANE", fallback), x, y, airframe) };
            }
        }

        /// <summary>The title, the line naming whose pick this is and what the screen waits for,
        /// the seat's tag in the pilot heading's slot, the aircraft under the cursor and its
        /// ratings and weapons.</summary>
        public IReadOnlyList<BoardLine> Captions
        {
            get
            {
                var layout = _shell._campaignLayout;
                int player = _shell.SeatIndex(_seat) + 1;
                var (titleX, titleY, titleWidth) = layout.Box(Section, "PS_T_TITLE", TitleX, TitleY, TitleWidth);
                var (lineX, lineY, lineWidth) = layout.Box(Section, "PS_T_MISSIONINFO", LineX, LineY, LineWidth);
                var (headX, headY, headWidth) = layout.Box(Section, "PS_T_PILOT", ComboX, HeadingY, 94f);
                var (otherX, otherY, otherWidth) = layout.Box(Section, "PS_T_WINGMAN", ComboX, HeadingY + SlotDrop, 94f);
                var lines = new List<BoardLine>
                {
                    new(Title, titleX, titleY, titleWidth, TitleFont, BoardInk.Heading),
                    new(Status(player), lineX, lineY, lineWidth, 15, BoardInk.Detail, Italic: true),
                    new($"P{player}", headX, headY, headWidth, 15, BoardInk.Heading),
                    new(Others(), otherX, otherY, TitleWidth * 3f, 15, BoardInk.Detail),
                };
                if (Current is not { } row)
                {
                    return lines;
                }

                var (nameX, nameY, nameWidth) = layout.Box(Section, "PS_T_PILOTPLANE", PlaneNameX, PlaneNameY, 400f);
                lines.Add(new BoardLine(row.Name, nameX, nameY, nameWidth, BodyFont, BoardInk.Row));
                if (PlanePickerRoster.AirframeOf(row.Node) is not { } airframe)
                {
                    return lines;
                }

                var fit = PlaneFit.For(
                    airframe, row.Custom, _shell._stock?.Invoke()?.ForModel(row.Node));
                var ratings = PlaneRatings.For(fit);
                for (int i = 0; i < RatingLabels.Length; i++)
                {
                    var (x, y, width) = layout.Box(Section, RatingKeys[i], RatingX, RatingY + (i * RatingPitch), RatingWidth);
                    lines.Add(new BoardLine($"{RatingLabels[i]}   {RatingWord(ratings[i])}", x, y, width, BodyFont, BoardInk.Row));
                }

                var (weaponsX, weaponsY, weaponsWidth) = layout.Box(Section, "PS_A_PLANEWEAPONSP", WeaponsX, RatingY, WeaponsWidth);
                lines.Add(new BoardLine(WeaponList(fit), weaponsX, weaponsY, weaponsWidth, BodyFont, BoardInk.Row));
                return lines;
            }
        }

        private UiStrings Strings => _shell.MenuStrings;

        private BoardButton[] Plaques => _seat.Locked ? SelectedPlaques : BrowsingPlaques;

        // The roster row the screen stands for: the highlighted one while the list is open, the
        // selected one once it is closed.
        private MenuAircraft? Current
        {
            get
            {
                var roster = _shell._setup.Roster;
                int at = List.Open ? List.Highlight : List.Selected;
                return at >= 0 && at < roster.Count ? roster[at] : null;
            }
        }

        public BoardButtonRef Button(int row)
        {
            var plaques = Plaques;
            return row >= 1 && row <= plaques.Length ? new BoardButtonRef(plaques[row - 1]) : BoardButtonRef.None;
        }

        public CampaignCombo? Combo(int row) => row == 0 ? List : null;

        public HangarArt? RowArt(int row) => null;

        public bool Focusable(int row) => true;

        public string RowText(int row) => Button(row).Button switch
        {
            BoardButton.ChangeAmmo => "WEAPON LOADOUT",
            BoardButton.AcceptSelections => "ACCEPT SELECTIONS",
            BoardButton.CancelSelections => "CANCEL SELECTIONS",
            _ => List.Text,
        };

        public string Detail(int row) => string.Empty;

        public int DetailRow(BoardDetailPane pane, int row) => pane == BoardDetailPane.Upper ? row : -1;

        public bool Step(int row, int dir) => false;

        public bool Accept(int row) => false;

        public bool Secondary(int row) => false;

        public bool Back() => false;

        private string Status(int player)
        {
            string device = _seat.Source.DeviceLabel;
            return _seat.Locked
                ? $"P{player}  {device}   A again to confirm, B to change"
                : $"P{player}  {device}   choose your aircraft";
        }

        // Every seat but the one picking, with what it stands at: the aircraft it has taken, READY
        // once confirmed, waiting until its own turn comes. The walk's order, on the screen.
        private string Others()
        {
            var seats = _shell._setup.Seats;
            var roster = _shell._setup.Roster;
            var parts = new List<string>();
            for (int i = 0; i < seats.Count; i++)
            {
                if (ReferenceEquals(seats[i], _seat))
                {
                    continue;
                }

                string name = seats[i].Cursor < roster.Count ? roster[seats[i].Cursor].Name : string.Empty;
                string standing = seats[i].Confirmed ? $"{name}  READY" : seats[i].Locked ? name : "waiting";
                parts.Add($"P{i + 1}  {standing}");
            }

            return string.Join("     ", parts);
        }

        private string RatingWord(int stars)
        {
            int at = Math.Clamp(stars, 0, RatingWords.Length - 1);
            return Strings.Text(501 + at, RatingWords[at]);
        }

        // The gun and hardpoint column in the plane-selection screen's own form, "(2) .50-cal."
        // rows then the hardpoint count.
        private string WeaponList(PlaneFit fit)
        {
            var lines = new List<string>();
            foreach (int calibre in Calibres)
            {
                if (fit.Barrels.TryGetValue(calibre, out int barrels))
                {
                    int idx = Math.Clamp((calibre - 30) / 10, 0, 4);
                    lines.Add($"({barrels}) {Strings.Text(3320 + idx, $" .{30 + (idx * 10)}-cal.").Trim()}");
                }
            }

            if (fit.Hardpoints > 0)
            {
                lines.Add($"({fit.Hardpoints}) {Strings.Text(1008, "Hardpoints")}");
            }

            return string.Join("\n", lines);
        }
    }
}
