using System;
using System.Collections.Generic;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;

namespace CSVM.UI.Menu;

/// <summary>What a seat's Back undid.</summary>
public enum SeatBack
{
    /// <summary>The seat was browsing; nothing to undo, so the presentation decides what Back means.</summary>
    Browsing,

    /// <summary>A selected airframe went back to browsing.</summary>
    Unselected,

    /// <summary>A confirmed airframe went back to selected.</summary>
    Unconfirmed,
}

/// <summary>One row of the shared aircraft roster every seat picks from: the display name, the
/// planes.zbd node a launch builds, and the saved custom plane behind the row where there is one
/// (a custom row flies its airframe's stock node with the def riding along).</summary>
public sealed record MenuAircraft(string Name, string Node, CustomPlaneDef? Custom = null)
{
    /// <summary>Whether this row is a saved custom plane rather than a stock airframe.</summary>
    public bool IsCustom => Custom != null;
}

/// <summary>
/// One joined seat: the input source that claimed it, its cursor over the roster, the stage of
/// its pick (browsing, selected, confirmed), whether its loadout list is open, and its fit edits.
/// The cursor is the presentation's to park, and may sit past the roster where a presentation
/// appends a row of its own; <see cref="PlayerSetupFeature.Select"/> refuses such a cursor. The
/// stages move only through the feature's operations.
/// </summary>
public sealed class PlayerSeat
{
    internal PlayerSeat(IMenuInputSource source)
    {
        Source = source;
    }

    /// <summary>The input source bound to the seat, its devices being that source's own detail.</summary>
    public IMenuInputSource Source { get; }

    /// <summary>The roster row the seat is on.</summary>
    public int Cursor { get; set; }

    /// <summary>Whether the seat has selected the airframe under its cursor, the first stage.</summary>
    public bool Locked { get; internal set; }

    /// <summary>Whether the seat has confirmed its selection, the stage the launch gate reads.</summary>
    public bool Confirmed { get; internal set; }

    /// <summary>Whether the seat's loadout list is open in place of the roster.</summary>
    public bool InLoadout { get; internal set; }

    /// <summary>The cursor inside the loadout list, the presentation's.</summary>
    public int FitRow { get; set; }

    /// <summary>The seat's loadout edits; stock until edited.</summary>
    public LoadoutChoice Fit { get; } = new();

    /// <summary>False once the seat has left; every operation on it is then refused.</summary>
    public bool Joined { get; internal set; } = true;
}

/// <summary>
/// Player setup as a shared feature: the seats and the input sources that claimed them, the
/// aircraft roster every seat picks from, each seat's cursor, two-stage pick and fit, the
/// launch gate per mode, and the <see cref="MenuSeatChoice"/> list a launch carries. Device-
/// neutral: a seat is bound to an <see cref="IMenuInputSource"/> and nothing here reads a
/// device; the flight binding a choice needs is asked of the presentation, which knows what is
/// behind its sources. Seat 0 is the process-lifetime first seat and never leaves; a switch
/// discards the others. Presentations decide how joining, picking and confirming are offered.
/// </summary>
public sealed class PlayerSetupFeature : IMenuFeature
{
    /// <summary>How many seats may join, the splitscreen rig's own maximum.</summary>
    public const int MaxSeats = 4;

    /// <summary>The kill target a screen may step up to. Remake-only: the original's Multiplayer
    /// is network play and authors no such box, so the ceiling is this port's, picked to keep the
    /// row one press per kill rather than a ladder of invented round numbers.</summary>
    public const int MaxKillTarget = 20;

    /// <summary>The match clock a screen may step up to, in minutes, remake-only for the same
    /// reason as <see cref="MaxKillTarget"/>.</summary>
    public const int MaxTimeLimitMinutes = 30;

    /// <summary>The shipped kill target, the same default <c>--vs-kills=</c> carries.</summary>
    public const int DefaultKillTarget = 5;

    /// <summary>The shipped match clock in minutes, the same default <c>--vs-time=</c> carries.</summary>
    public const int DefaultTimeLimitMinutes = 5;

    private readonly List<PlayerSeat> _seats = new();
    private readonly SourceView _sources;
    private IReadOnlyList<MenuAircraft> _roster = Array.Empty<MenuAircraft>();

    public PlayerSetupFeature()
    {
        _sources = new SourceView(_seats);
    }

    /// <summary>The joined seats, seat 0 first.</summary>
    public IReadOnlyList<PlayerSeat> Seats => _seats;

    /// <summary>The seats' input sources in seat order, live, the list the host lends presentations.</summary>
    public IReadOnlyList<IMenuInputSource> Sources => _sources;

    /// <summary>The roster every seat picks from: the stock airframes, then the saved customs.</summary>
    public IReadOnlyList<MenuAircraft> Roster => _roster;

    /// <summary>Dogfight's kill target, 0 meaning no kill limit. Rides a Versus
    /// <see cref="BuildExit"/> and is ignored by every other mode.</summary>
    public int KillTarget { get; private set; } = DefaultKillTarget;

    /// <summary>Dogfight's match clock in minutes, 0 meaning no time limit.</summary>
    public int TimeLimitMinutes { get; private set; } = DefaultTimeLimitMinutes;

    /// <summary>Bumps on every join and unjoin, so a presentation holding per-seat state can tell
    /// the seat list moved without comparing it.</summary>
    public int Revision { get; private set; }

    /// <summary>How many seats have confirmed.</summary>
    public int ConfirmedCount
    {
        get
        {
            int n = 0;
            foreach (var seat in _seats)
            {
                if (seat.Confirmed)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>The roster rule both presentations share: the stock rows in their given order,
    /// then one row per saved custom plane in the store's own order, each custom flying its
    /// airframe's stock node (<paramref name="nodeOfAirframe"/>, the picker roster's table). A
    /// campaign aeroplane nobody has exported yet is not offered
    /// (<see cref="CustomPlaneDef.AwaitingExport"/>).</summary>
    public static IReadOnlyList<MenuAircraft> BuildRoster(
        IReadOnlyList<(string Name, string Node)> stock,
        IReadOnlyList<CustomPlaneDef> customs,
        Func<int, string> nodeOfAirframe)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(customs);
        ArgumentNullException.ThrowIfNull(nodeOfAirframe);
        var rows = new List<MenuAircraft>(stock.Count + customs.Count);
        foreach (var (name, node) in stock)
        {
            rows.Add(new MenuAircraft(name, node));
        }

        foreach (var def in customs)
        {
            if (!def.AwaitingExport)
            {
                rows.Add(new MenuAircraft(def.Name, nodeOfAirframe(def.Airframe), def));
            }
        }

        return rows;
    }

    /// <summary>The fewest seats a mode launches with: two for Dogfight, one otherwise.</summary>
    public static int MinimumSeats(MenuMode mode) => mode == MenuMode.Versus ? 2 : 1;

    /// <summary>Steps the kill target, clamped to 0 (no kill limit) and
    /// <see cref="MaxKillTarget"/>.</summary>
    public void StepKillTarget(int direction) =>
        KillTarget = Math.Clamp(KillTarget + direction, 0, MaxKillTarget);

    /// <summary>Steps the match clock in minutes, clamped to 0 (no time limit) and
    /// <see cref="MaxTimeLimitMinutes"/>.</summary>
    public void StepTimeLimit(int direction) =>
        TimeLimitMinutes = Math.Clamp(TimeLimitMinutes + direction, 0, MaxTimeLimitMinutes);

    /// <summary>Replaces the roster. Cursors are left where they are; a presentation clamps its
    /// own, since it may keep a row of its own past the roster.</summary>
    public void SetRoster(IReadOnlyList<MenuAircraft> roster) =>
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));

    /// <summary>Joins a seat for <paramref name="source"/>, or returns null when every seat is
    /// taken or the source already holds one: a claim is one source, one seat, and two claims on
    /// the same frame are settled in the order they arrive.</summary>
    public PlayerSeat? Join(IMenuInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_seats.Count >= MaxSeats || IsClaimed(source))
        {
            return null;
        }

        var seat = new PlayerSeat(source);
        _seats.Add(seat);
        Revision++;
        return seat;
    }

    /// <summary>Whether a source already holds a seat.</summary>
    public bool IsClaimed(IMenuInputSource source) => SeatOf(source) != null;

    /// <summary>The seat a source holds, or null.</summary>
    public PlayerSeat? SeatOf(IMenuInputSource source)
    {
        foreach (var seat in _seats)
        {
            if (ReferenceEquals(seat.Source, source))
            {
                return seat;
            }
        }

        return null;
    }

    /// <summary>Removes a seat, whatever stage its pick was at. Seat 0 never leaves, and a seat
    /// already gone is a no-op, so an unjoin racing a lock or a confirm on the same frame wins
    /// whichever order they arrive in.</summary>
    public bool Unjoin(PlayerSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        int index = _seats.IndexOf(seat);
        if (index <= 0)
        {
            return false;
        }

        _seats.RemoveAt(index);
        seat.Joined = false;
        Revision++;
        return true;
    }

    /// <summary>Moves a browsing seat's cursor. A moved cursor resets the fit to stock, since
    /// a fit built for one airframe has nowhere to live on another; a selected seat's cursor does
    /// not move. Returns whether the cursor moved.</summary>
    public bool Browse(PlayerSeat seat, int index)
    {
        if (!Live(seat) || seat.Locked || index < 0)
        {
            return false;
        }

        if (seat.Cursor == index)
        {
            return false;
        }

        seat.Cursor = index;
        seat.Fit.ResetToStock();
        return true;
    }

    /// <summary>The first stage: selects the airframe under the seat's cursor. Refused for a
    /// seat already selected, one whose cursor is past the roster, or one that has left.</summary>
    public bool Select(PlayerSeat seat)
    {
        if (!Live(seat) || seat.Locked || seat.Cursor >= _roster.Count)
        {
            return false;
        }

        seat.Locked = true;
        return true;
    }

    /// <summary>The second stage: confirms a selected airframe, closing the seat's loadout list.
    /// Refused unless the seat is selected and still joined.</summary>
    public bool Confirm(PlayerSeat seat)
    {
        if (!Live(seat) || !seat.Locked || seat.Confirmed)
        {
            return false;
        }

        seat.Confirmed = true;
        seat.InLoadout = false;
        return true;
    }

    /// <summary>One step back through the stages: confirmed to selected, selected to browsing;
    /// a browsing seat reports <see cref="SeatBack.Browsing"/> and the presentation decides.</summary>
    public SeatBack Back(PlayerSeat seat)
    {
        if (!Live(seat))
        {
            return SeatBack.Browsing;
        }

        if (seat.Confirmed)
        {
            seat.Confirmed = false;
            return SeatBack.Unconfirmed;
        }

        if (seat.Locked)
        {
            seat.Locked = false;
            return SeatBack.Unselected;
        }

        return SeatBack.Browsing;
    }

    /// <summary>Opens the seat's loadout list, which only a selected, unconfirmed seat has.</summary>
    public bool OpenLoadout(PlayerSeat seat)
    {
        if (!Live(seat) || !seat.Locked || seat.Confirmed || seat.InLoadout)
        {
            return false;
        }

        seat.InLoadout = true;
        seat.FitRow = 0;
        return true;
    }

    /// <summary>Closes the seat's loadout list, keeping the fit.</summary>
    public bool CloseLoadout(PlayerSeat seat)
    {
        if (!Live(seat) || !seat.InLoadout)
        {
            return false;
        }

        seat.InLoadout = false;
        return true;
    }

    /// <summary>Every seat back to browsing with its list closed, cursors kept; the fits go back
    /// to stock when <paramref name="fits"/> is set. A return from flight resets everything but
    /// the cursors; seat 0 backing out of the aircraft screen keeps the fits.</summary>
    public void ResetPicks(bool fits)
    {
        foreach (var seat in _seats)
        {
            seat.Locked = false;
            seat.Confirmed = false;
            seat.InLoadout = false;
            if (fits)
            {
                seat.Fit.ResetToStock();
            }
        }
    }

    /// <summary>Why a launch in <paramref name="mode"/> is refused right now, or null when it
    /// may go: at least the mode's minimum of seats, every seat confirmed. A lone Dogfight seat
    /// is refused for the second seat before its own confirmation, unless
    /// <paramref name="networked"/>, where the opponent sits at another machine.</summary>
    public string? Refusal(MenuMode mode, bool networked = false)
    {
        if (_seats.Count == 0)
        {
            return "no seat joined";
        }

        if (!networked && _seats.Count < MinimumSeats(mode))
        {
            return "Dogfight needs a second seat";
        }

        int confirmed = ConfirmedCount;
        if (confirmed != _seats.Count)
        {
            return $"{_seats.Count - confirmed} of {_seats.Count} seats not confirmed";
        }

        return null;
    }

    /// <summary>Whether the launch gate is open for <paramref name="mode"/>; see
    /// <see cref="Refusal"/>.</summary>
    public bool CanLaunch(MenuMode mode, bool networked = false) => Refusal(mode, networked) == null;

    /// <summary>One <see cref="MenuSeatChoice"/> per seat in seat order: the roster row's node,
    /// the flight devices <paramref name="flightDevices"/> answers for the seat (the source's own
    /// detail), the fit edits or null for stock, and a custom row's def. Throws when a seat's
    /// cursor is off the roster, so a door row cannot be flown.</summary>
    public IReadOnlyList<MenuSeatChoice> Choices(Func<PlayerSeat, IReadOnlyList<int>> flightDevices)
    {
        ArgumentNullException.ThrowIfNull(flightDevices);
        var choices = new List<MenuSeatChoice>(_seats.Count);
        for (int i = 0; i < _seats.Count; i++)
        {
            var seat = _seats[i];
            if (seat.Cursor < 0 || seat.Cursor >= _roster.Count)
            {
                throw new InvalidOperationException($"seat {i + 1} is on row {seat.Cursor}, outside the roster of {_roster.Count}");
            }

            var row = _roster[seat.Cursor];
            choices.Add(new MenuSeatChoice(row.Node, flightDevices(seat), seat.Fit.IsStock ? null : seat.Fit, row.Custom));
        }

        return choices;
    }

    /// <summary>The typed exit for a mode with no feature of its own (Dogfight): the chapter, the
    /// seats' choices, the mode and, for Versus, the match rules. Throws when the gate is closed,
    /// so a half-built launch cannot leave the menu.</summary>
    public LaunchExit BuildExit(string chapter, MenuMode mode, Func<PlayerSeat, IReadOnlyList<int>> flightDevices)
    {
        ArgumentException.ThrowIfNullOrEmpty(chapter);
        if (Refusal(mode) is { } refusal)
        {
            throw new InvalidOperationException($"{mode} cannot launch: {refusal}");
        }

        var rules = mode == MenuMode.Versus ? new VersusRules(KillTarget, TimeLimitMinutes) : null;
        return new LaunchExit(chapter, Choices(flightDevices), mode, null, rules);
    }

    /// <summary>Drops every seat but the first and every stage of its pick, cursor included, and
    /// puts the match rules back to the shipped defaults: unfinished setup does not survive a
    /// presentation switch. The roster stays; it is read from the store, not chosen.</summary>
    public void Discard()
    {
        KillTarget = DefaultKillTarget;
        TimeLimitMinutes = DefaultTimeLimitMinutes;
        while (_seats.Count > 1)
        {
            var seat = _seats[^1];
            _seats.RemoveAt(_seats.Count - 1);
            seat.Joined = false;
            Revision++;
        }

        ResetPicks(fits: true);
        if (_seats.Count == 1)
        {
            _seats[0].Cursor = 0;
            _seats[0].FitRow = 0;
        }
    }

    private static bool Live(PlayerSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        return seat.Joined;
    }

    // The seats' sources as one live read-only list, so the host lends the same list forever
    // rather than a copy a presentation would have to re-fetch.
    private sealed class SourceView : IReadOnlyList<IMenuInputSource>
    {
        private readonly List<PlayerSeat> _seats;

        public SourceView(List<PlayerSeat> seats)
        {
            _seats = seats;
        }

        public int Count => _seats.Count;

        public IMenuInputSource this[int index] => _seats[index].Source;

        public IEnumerator<IMenuInputSource> GetEnumerator()
        {
            foreach (var seat in _seats)
            {
                yield return seat.Source;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
