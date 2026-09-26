using System;
using System.Collections.Generic;
using CSVM.Net;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The Original shell's two sortie screens, Free Flight and the remake-only Dogfight, over the
/// shared player setup: the chapter column, the aircraft column as a window onto the shared roster
/// (the stock airframes, then the saved customs), the seat strip, the join hint, BACK and FLY. Seat 0
/// alone drives the screen: the focus, the pointer, the chapter, its aircraft and FLY. Once seat 0 has
/// picked, each joined seat picks in turn on the per-seat screen (<c>OriginalSeatPlane.cs</c>); on the
/// sortie screen itself a later seat can only leave with Back. FLY is seat 0's confirmation and the
/// launch in one press, so it stands only once every other seat has confirmed and, for Dogfight, a
/// second seat has joined; the walk's last confirm takes that press for it. Also the joining rule the
/// presentation reads (<see cref="JoiningOpen"/>) and the same seat strip over the campaign boards
/// and the Instant Action screen once a second seat has joined. Nothing here is decoded.
/// </summary>
public sealed partial class OriginalShell
{
    /// <summary>The Dogfight door's key on the top level.</summary>
    public const string DogfightKey = "DOGFIGHT";

    /// <summary>How many aircraft rows the column shows at once; the window follows the focus.</summary>
    public const int AirframeWindow = 11;

    private const string AirframeKeyPrefix = "AIRFRAME:";

    // The Dogfight door under the Free Flight door, one plaque plus air below it.
    private const float DogfightDoorY = DoorY + 36f;

    // The seat strip under the chapter column, the hint between the two plaques, the scroll
    // marks over and under the aircraft window.
    private const float SeatStripY = 468f;
    private const float SeatStripPitch = 14f;
    private const float SeatStripWidth = 360f;
    private const float SeatFont = 12f;
    private const float HintX = 190f;
    private const float HintWidth = 420f;
    private const float HintY = 546f;
    private const float MarkSize = 12f;

    // A remote guest's chip spans two pitches, since its tag carries the network mark.
    private const int RemoteChipPitches = 2;

    // A Ready chip's word is longer than the network mark and would wrap in two pitches.
    private const int ReadyChipPitches = 3;

    // The aircraft column's scrollbar, remake chrome on a remake screen: a thin track down the
    // window's right edge with a thumb no shorter than a row, drawn only once the roster outruns
    // the window.
    private const string AirframeListKey = "AIRFRAMES";
    private const float AirframeBarWidth = 6f;
    private const float AirframeThumbMin = 20f;

    // The focused chip's lit cell over the strip's ground, the one state the chips carry that
    // Built-in's row has no use for: which seat's flight check is the one on show.
    private const byte CampaignStripLit = 0xFF;
    private const float CampaignStripLitAlpha = 0.22f;

    // The strip's ground: dark under every chip, since the four identity colours are light. The
    // Instant Action paper needs a more opaque plate than a painted panel does to hold them.
    private const float CampaignStripGround = 0.45f;
    private const float CampaignStripGroundOnPaper = 0.72f;

    // A Ready chip's cell, a green wash over the dark ground so the mark reads before its word.
    private const byte CampaignStripReadyRed = 0x30;
    private const byte CampaignStripReadyGreen = 0xB0;
    private const byte CampaignStripReadyBlue = 0x40;
    private const float CampaignStripReadyAlpha = 0.45f;

    private int _pickedVersusChapter = -1;
    private int _airframeTop;

    /// <summary>The airframe seat 0 has selected, as its node, or null while browsing.</summary>
    public string? PickedAirframe
    {
        get
        {
            var roster = _setup.Roster;
            return Seat0 is { Locked: true } seat && seat.Cursor < roster.Count ? roster[seat.Cursor].Node : null;
        }
    }

    /// <summary>The Dogfight screen's picked chapter code, or null.</summary>
    public string? PickedDogfightChapter => _pickedVersusChapter >= 0 ? _chapters[_pickedVersusChapter].Code : null;

    /// <summary>The first roster row the aircraft column shows.</summary>
    public int AirframeTop => _airframeTop;

    /// <summary>Whether Start on a free pad joins a seat on the standing screen: the four screens
    /// that launch a flight, and the CONTROLS page. The two sortie screens give a joined seat an
    /// aircraft column, the flight check a check of its own; Instant Action opens so a second pilot
    /// can join before FLY MISSION rather than nowhere at all; the CONTROLS page opens because its
    /// seat chooser is the only door to a second player's keymap. Everywhere else a pad's Start
    /// does nothing.</summary>
    public bool JoiningOpen =>
        _screen is OriginalScreen.FreeFlight or OriginalScreen.Dogfight or OriginalScreen.InstantAction
            or OriginalScreen.CampaignFlightCheck or OriginalScreen.ControlsPrefs;

    private bool IsSortie => _screen is OriginalScreen.FreeFlight or OriginalScreen.Dogfight;

    // The strip's focused line: the seat picking on the per-seat screen, else the seat whose flight
    // check shows, else none. The ammo and plane screens the check opens name no seat of their own,
    // their strip standing for the campaign rather than for a check.
    private int StripFocus =>
        _screen == OriginalScreen.SeatPlane ? PickingSeat
        : _screen == OriginalScreen.CampaignFlightCheck ? Campaign.CheckSeat
        : -1;

    private MenuMode SortieMode => _screen == OriginalScreen.Dogfight ? MenuMode.Versus : MenuMode.Free;

    private PlayerSeat? Seat0 => _setup.Seats.Count > 0 ? _setup.Seats[0] : null;

    private int PickedChapterIndex
    {
        get => _screen == OriginalScreen.Dogfight ? _pickedVersusChapter : _pickedChapter;
        set
        {
            if (_screen == OriginalScreen.Dogfight)
            {
                _pickedVersusChapter = value;
            }
            else
            {
                _pickedChapter = value;
            }
        }
    }

    /// <summary>The row key of the roster's aircraft at <paramref name="index"/>.</summary>
    public static string AirframeKey(int index) => AirframeKeyPrefix + index;

    /// <summary>Applies one frame of one seat's commands. A seat drives its walk's screens while it
    /// is the one picking, the per-seat aircraft screen and the Weapon Loadout opened from it, and
    /// its own check on the campaign, the check and the ammo and plane screens it opens; seat 0
    /// drives every other screen, its frame reduced to its pointer on a screen standing for another
    /// seat (see <see cref="SeatZeroFrame"/>). A later seat anywhere else can only leave with Back,
    /// from any screen, as a guest may.</summary>
    public OriginalStep StepSeat(int index, MenuCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (index == 0)
        {
            return ApplyFrame(SeatZeroFrame(commands));
        }

        var seats = _setup.Seats;
        if (index < 0 || index >= seats.Count)
        {
            return new OriginalStep(Array.Empty<string>(), null, false);
        }

        var seat = seats[index];
        bool own = OnSeatWalk ? ReferenceEquals(seat, _pickingSeat) : Campaign.CheckSeat == index;
        if (own)
        {
            return ApplyFrame(commands);
        }

        bool changed = commands.Back && _setup.Unjoin(seat);
        return new OriginalStep(Array.Empty<string>(), null, changed);
    }

    private static bool TryAirframeIndex(string key, out int index)
    {
        index = -1;
        return key.StartsWith(AirframeKeyPrefix, StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(AirframeKeyPrefix.Length), out index);
    }

    private static string SeatStatus(PlayerSeat seat, IReadOnlyList<MenuAircraft> roster)
    {
        string name = seat.Cursor < roster.Count ? roster[seat.Cursor].Name : string.Empty;
        return seat.Confirmed ? $"{name}  READY" : seat.Locked ? name : "choosing";
    }

    private static CampaignChip Chip(int seat, bool own, bool ready)
    {
        string mark = ready ? LaunchMenu.ReadyChipMark : own ? "" : LaunchMenu.RemoteChipMark;
        int pitches = ready ? ReadyChipPitches : mark.Length > 0 ? RemoteChipPitches : 1;
        return new CampaignChip(SplitScreen.PlayerTag(seat) + mark, seat, pitches, own, ready);
    }

    // Built-in's chip row in the Original presentation's own space, drawn over a campaign board
    // only once a second seat has joined so a solo campaign shows the authored screen alone. No
    // device and no pick status: the campaign's picks are the flight field's, and the sortie
    // screens' own strip names the devices.
    // ⚠ Pass onPaper on the Instant Action screen: its background is light, and a chip's colour
    // reads only over a ground dark enough to hold it.
    private BoardPanel? CampaignSeatPanel(bool onPaper = false)
    {
        var chips = CampaignChips();
        if (chips.Count < 2)
        {
            return null;
        }

        int current = StripFocus;
        int pitches = 0;
        foreach (var chip in chips)
        {
            pitches += chip.Pitches;
        }

        float width = pitches * SeatStrip.Pitch;
        float left = BoardFit.AuthoredWidth - SeatStrip.Inset - width;
        float top = SeatStrip.Inset - SeatStrip.Pad;
        var fills = new List<BoardFill>(2)
        {
            new BoardFill(left - SeatStrip.Pad, top, width + (2f * SeatStrip.Pad), SeatStrip.Ground,
                0, 0, 0, onPaper ? CampaignStripGroundOnPaper : CampaignStripGround),
        };

        var lines = new List<BoardLine>(chips.Count);
        float x = left;
        for (int i = 0; i < chips.Count; i++)
        {
            var chip = chips[i];
            float chipWidth = chip.Pitches * SeatStrip.Pitch;
            if (chip.Ready)
            {
                fills.Add(new BoardFill(x, top, chipWidth, SeatStrip.Ground, CampaignStripReadyRed, CampaignStripReadyGreen,
                    CampaignStripReadyBlue, CampaignStripReadyAlpha));
            }
            else if (chip.Local && chip.Seat == current)
            {
                fills.Add(new BoardFill(x, top, chipWidth, SeatStrip.Ground,
                    CampaignStripLit, CampaignStripLit, CampaignStripLit, CampaignStripLitAlpha));
            }

            lines.Add(new BoardLine(chip.Text, x, SeatStrip.Inset, chipWidth,
                SeatStrip.Font, SeatStrip.Ink(chip.Seat), -1, false, BoardJustify.Center));
            x += chipWidth;
        }

        return new BoardPanel(fills, Array.Empty<BoardPicture>(), lines);
    }

    // The campaign strip's chips, player 1 first. A human at another machine takes a chip but no
    // pane, and its chip says so. In co-op a guest's chip says Ready instead once it is, as the
    // original lobby's list marks a player. A co-op guest's own strip is the host's field.
    private List<CampaignChip> CampaignChips()
    {
        var chips = new List<CampaignChip>(NetSeats.MaxPlayers);
        if (Campaign.IsGuest && _net is { IsCoopGuest: true, CoopFlow: { } flow } guest)
        {
            for (int slot = 0; slot < Math.Min((int)flow.Humans, NetSeats.MaxPlayers); slot++)
            {
                bool own = slot == flow.Slot;
                bool ready = own ? guest.CoopReady : flow.IsReady(slot);
                chips.Add(Chip(slot, own, ready));
            }

            return chips;
        }

        var seats = _setup.Seats;
        for (int i = 0; i < seats.Count; i++)
        {
            chips.Add(new CampaignChip(SplitScreen.PlayerTag(i), i, 1, true, false));
        }

        if (_net is { IsCoopHost: true } host)
        {
            foreach (var coop in host.CoopGuests)
            {
                if (chips.Count < NetSeats.MaxPlayers)
                {
                    chips.Add(Chip(chips.Count, false, coop.Ready));
                }
            }
        }

        return chips;
    }

    // The sortie screen's rows: the chapters and BACK in column 0, the aircraft window and FLY in
    // column 1. Rows outside the window keep their place in the column for the keyboard but are
    // neither drawn nor hit; the window slides so the focused row is always inside it.
    private void SortieRows(List<OriginalRow> rows)
    {
        var roster = _setup.Roster;
        int offset = _chapters.Count + 1;
        int focused = _focus[(int)_screen] - offset;
        if (focused >= 0 && focused < roster.Count)
        {
            if (focused < _airframeTop)
            {
                _airframeTop = focused;
            }
            else if (focused >= _airframeTop + AirframeWindow)
            {
                _airframeTop = focused - AirframeWindow + 1;
            }
        }

        _airframeTop = Math.Clamp(_airframeTop, 0, Math.Max(0, roster.Count - AirframeWindow));

        for (int i = 0; i < _chapters.Count; i++)
        {
            rows.Add(new OriginalRow(_chapters[i].Code, _chapters[i].Label, OriginalRowKind.ListRow,
                LeftColumnX, ListTop + (i * RowPitch), ListWidth, RowHeight, true, 0, null));
        }

        rows.Add(TextButton(BackKey, "BACK", LeftColumnX, PlaqueY, true, 0));
        for (int i = 0; i < roster.Count; i++)
        {
            bool visible = i >= _airframeTop && i < _airframeTop + AirframeWindow;
            rows.Add(new OriginalRow(AirframeKey(i), roster[i].Name, OriginalRowKind.ListRow,
                RightColumnX, ListTop + ((i - _airframeTop) * RowPitch), ListWidth, RowHeight, true, 1, null, visible));
        }

        var flySize = PlaqueSize();
        rows.Add(TextButton(FlyKey, "FLY", RightColumnX + ListWidth - flySize.Width, PlaqueY, FlyEnabled(), 1));
    }

    // The aircraft column as the pointer's one list on a sortie screen.
    private void SortieLists(List<OriginalList> lists)
    {
        if (AirframeWindowFor(_setup.Roster.Count) is { } window)
        {
            lists.Add(new OriginalList(AirframeListKey, window, ScrollAirframes));
        }
    }

    // The column's window, thumb and track; null while the roster fits the window.
    private ListWindow? AirframeWindowFor(int count)
    {
        if (count <= AirframeWindow)
        {
            return null;
        }

        int lastTop = count - AirframeWindow;
        int top = Math.Clamp(_airframeTop, 0, lastTop);
        float height = AirframeWindow * RowPitch;
        float thumbHeight = ListWindow.ThumbHeightFor(height, AirframeWindow, count, AirframeThumbMin);
        float thumbY = ListWindow.ThumbYFor(ListTop, height, thumbHeight, top, lastTop);
        return new ListWindow(
            RightColumnX, ListTop, ListWidth, height,
            RightColumnX + ListWidth - AirframeBarWidth, thumbY, AirframeBarWidth, thumbHeight,
            ListTop, height, count, AirframeWindow, top);
    }

    // Puts the window's first row at top; a focus standing on an aircraft row the move would hide
    // is pulled to the window's nearer edge, since the window otherwise slides back to it.
    private void ScrollAirframes(int top)
    {
        int count = _setup.Roster.Count;
        _airframeTop = Math.Clamp(top, 0, Math.Max(0, count - AirframeWindow));
        int offset = _chapters.Count + 1;
        int focused = _focus[(int)_screen] - offset;
        if (focused >= 0 && focused < count)
        {
            _focus[(int)_screen] = offset + Math.Clamp(focused, _airframeTop, _airframeTop + AirframeWindow - 1);
        }
    }

    private bool FlyEnabled()
    {
        if (PickedChapterIndex < 0 || Seat0 is not { Locked: true })
        {
            return false;
        }

        var seats = _setup.Seats;
        if (seats.Count < PlayerSetupFeature.MinimumSeats(SortieMode))
        {
            return false;
        }

        for (int i = 1; i < seats.Count; i++)
        {
            if (!seats[i].Confirmed)
            {
                return false;
            }
        }

        return true;
    }

    private MenuExit? ActivateSortie(OriginalRow row)
    {
        switch (row.Key)
        {
            case BackKey:
                Open(OriginalScreen.TopLevel);
                return null;
            case FlyKey:
                return Fly();
        }

        if (row.Column == 0)
        {
            for (int i = 0; i < _chapters.Count; i++)
            {
                if (_chapters[i].Code == row.Key)
                {
                    PickedChapterIndex = i;
                    if (_screen == OriginalScreen.FreeFlight)
                    {
                        _free.SelectChapter(row.Key);
                    }
                }
            }

            return null;
        }

        // A click on another aircraft re-picks; the same row again changes nothing.
        if (TryAirframeIndex(row.Key, out int index) && Seat0 is { } seat)
        {
            if (seat.Locked && seat.Cursor == index)
            {
                return null;
            }

            if (seat.Locked)
            {
                _setup.Back(seat);
            }

            _setup.Browse(seat, index);
            _setup.Select(seat);
        }

        return null;
    }

    // FLY: seat 0's confirmation and the launch, reached by its own press and by the per-seat
    // walk's last confirm. Free Flight leaves through its feature with the chapter handed over;
    // Dogfight, the remake's own mode with no feature of its own, leaves through the setup's exit.
    private MenuExit? Fly()
    {
        if (!FlyEnabled() || Seat0 is not { } seat)
        {
            return null;
        }

        _setup.Confirm(seat);
        string code = _chapters[PickedChapterIndex].Code;
        if (_screen == OriginalScreen.Dogfight)
        {
            return _setup.BuildExit(code, MenuMode.Versus, _flightDevices);
        }

        _free.SelectChapter(code);
        return _free.BuildExit(_setup.Choices(_flightDevices));
    }

    private bool IsPicked(OriginalRow row)
    {
        if (row.Column == 0)
        {
            return PickedChapterIndex >= 0 && row.Key == _chapters[PickedChapterIndex].Code;
        }

        return TryAirframeIndex(row.Key, out int index) && Seat0 is { Locked: true } seat && seat.Cursor == index;
    }

    // What the screen is waiting for, in the order a pilot resolves it. The map is checked last
    // because it is seat 0's own press at any moment, unlike a seat that has yet to pick. It rides
    // the arm for whatever else is outstanding rather than taking one of its own.
    private string SortieHint()
    {
        var seats = _setup.Seats;
        bool noMap = PickedChapterIndex < 0;
        if (Seat0 is not { Locked: true })
        {
            return noMap ? "Pick a map, then an aircraft" : "Pick an aircraft";
        }

        if (seats.Count < PlayerSetupFeature.MinimumSeats(SortieMode))
        {
            return "Dogfight needs a second seat: press START on a free pad to join";
        }

        for (int i = 1; i < seats.Count; i++)
        {
            if (!seats[i].Confirmed)
            {
                return $"Waiting for P{i + 1} to pick an aircraft";
            }
        }

        if (noMap)
        {
            return "Pick a map, then FLY";
        }

        return seats.Count >= PlayerSetupFeature.MaxSeats
            ? "Four seats joined, the maximum. FLY when ready"
            : "FLY when ready, or press START on a free pad to join";
    }

    // The sortie screen's own words: the heading, the column labels, the seat strip, the scroll
    // marks, the hint and the controls line, plus the aircraft column's scrollbar as an overlay.
    // The rows themselves are drawn by Compose's row loop.
    private void ComposeSortie(IReadOnlyList<OriginalRow> rows, BoardLayers layers)
    {
        if (AirframeWindowFor(_setup.Roster.Count) is { } window)
        {
            layers.Overlays.Add(new BoardPanel(
                new[]
                {
                    new BoardFill(window.ThumbX, window.TrackTop, window.ThumbWidth, window.TrackHeight, 0x40, 0x40, 0x40, 0.5f),
                    new BoardFill(window.ThumbX, window.ThumbY, window.ThumbWidth, window.ThumbHeight, 0xC0, 0xC0, 0xC0, 0.9f),
                },
                Array.Empty<BoardPicture>(),
                Array.Empty<BoardLine>()));
        }

        var lines = layers.Lines;
        lines.Add(new BoardLine(_screen == OriginalScreen.Dogfight ? "DOGFIGHT" : "FREE FLIGHT",
            LeftColumnX, ListTop - 44f, 0f, HeadingFont, BoardInk.Heading));
        lines.Add(new BoardLine("MAP", LeftColumnX, ListTop - 20f, 0f, RowFont, BoardInk.Detail));
        lines.Add(new BoardLine("AIRCRAFT", RightColumnX, ListTop - 20f, 0f, RowFont, BoardInk.Detail));
        lines.Add(new BoardLine(
            "Up / Down  Choose       Left / Right  Column       Enter / A / Click  Pick       Esc / B  Back       START  Join",
            0f, FooterY, BoardFit.AuthoredWidth, FooterFont, BoardInk.Detail, -1, false, BoardJustify.Center));
        lines.Add(new BoardLine(SortieHint(), HintX, HintY, HintWidth, FooterFont, BoardInk.Detail, -1, false, BoardJustify.Center));

        var seats = _setup.Seats;
        var roster = _setup.Roster;
        for (int i = 0; i < seats.Count; i++)
        {
            lines.Add(new BoardLine($"P{i + 1}  {seats[i].Source.DeviceLabel}   {SeatStatus(seats[i], roster)}",
                LeftColumnX, SeatStripY + (i * SeatStripPitch), SeatStripWidth, SeatFont,
                seats[i].Locked ? BoardInk.RowFocused : BoardInk.Detail));
        }

        float markX = RightColumnX + ListWidth - 20f;
        if (_airframeTop > 0)
        {
            lines.Add(new BoardLine("▲", markX, ListTop - 16f, 16f, MarkSize, BoardInk.Detail, -1, false, BoardJustify.Center));
        }

        if (_airframeTop + AirframeWindow < roster.Count)
        {
            lines.Add(new BoardLine("▼", markX, ListTop + (AirframeWindow * RowPitch), 16f, MarkSize, BoardInk.Detail, -1, false, BoardJustify.Center));
        }
    }

    // One chip on the campaign strip: its words, its colour and how many pitches it spans. It also
    // says whether it is a seat at this machine, and whether it is Ready.
    private readonly record struct CampaignChip(string Text, int Seat, int Pitches, bool Local, bool Ready);
}
