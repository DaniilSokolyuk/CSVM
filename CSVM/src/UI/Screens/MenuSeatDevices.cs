using System;
using System.Collections.Generic;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The pad roster a join board draws and signs pads onto, without the board knowing what a pad is.
/// It holds four entries: entry 0 is the captain's claim beside the keyboard, the rest the joined
/// seats. The implementation is <see cref="MenuSeatDevices"/>; a shell built without one draws four
/// open entries and answers no gesture, which is what an engine-free test sees.
/// </summary>
public interface IJoinRoster
{
    /// <summary>Whether a pad has signed onto that entry, 0 to 3.</summary>
    bool SignedOn(int entry);

    /// <summary>The name of the device on that entry, empty where none has signed on.</summary>
    string Device(int entry);

    /// <summary>Gives every signed-on pad back, the way off the board that keeps nobody: the
    /// captain's claim released and each joined seat unjoined.</summary>
    void DropSignOns();
}

/// <summary>What one frame of the board's gestures did: whether the manifest moved, whether the
/// captain cast off, and whether any button was pressed. ⚠ The last of those is why the same press
/// cannot also arm the focused plaque. Seat 0 borrows every pad nobody holds, so its own poll reads
/// that A as Accept; the presentation drops its presses for that frame.</summary>
public readonly record struct BoardScan(bool Moved, bool Cast, bool Pressed);

/// <summary>
/// The pad side of the shared player setup, for any presentation: which pad seat 0 claimed by
/// steering with it, the join gesture (Start on an unclaimed pad, edge-detected per device),
/// hotplug (a pad gone past <see cref="DeviceGrace"/> unjoins its seat, one back at another index
/// keeps it, seat 0's poller is handed every pad nobody holds)
/// and the flight binding a seat's source carries. A joined pad becomes a
/// <see cref="BuiltInSeat"/> over a poller bound to that one pad. The feature never sees a pad
/// index; this is where a source's own detail is read. ⚠ Seat 0 reads every unclaimed pad until
/// it claims one, never <c>pads[0]</c>: phantom devices occupy the early slots.
/// It is also the join board's roster (<see cref="IJoinRoster"/>) and the board's three gestures.
/// </summary>
public sealed class MenuSeatDevices : IJoinRoster
{
    /// <summary>How long a seat keeps its pad, and seat 0 its claim, after the roster drops it.
    /// Steam Input re-enumerates its virtual pads mid-menu, and without the grace every blip
    /// unjoins the player and releases the claim, so a Start lands in a different seat each
    /// time.</summary>
    public const float DeviceGrace = 2f;

    private readonly MenuInput _player1;
    private readonly PlayerSetupFeature _setup;
    // Previous-frame Start state of every connected pad, for edge-detecting the join gesture on
    // pads that have no seat (and therefore no poller) yet.
    private readonly Dictionary<int, bool> _joinPrev = new();
    // Previous-frame state of the board's three buttons per pad. Held apart from _joinPrev because
    // the board's Start means cast off, not join, and reads on its own screen.
    private readonly Dictionary<int, (bool On, bool Off, bool Cast)> _boardPrev = new();
    // The pads already reported by ReportButton since joining opened, so the report is one line per
    // pad rather than one per frame.
    private readonly HashSet<int> _reported = new();
    // The stable guid of the device behind each pad a seat holds, seat 0's claim included. Godot
    // reuses a connection index the moment a pad drops, so the index alone cannot say whether the
    // pad that came back is the one that left.
    private readonly Dictionary<int, string> _guids = new();
    // How long each held pad has been off the roster, entries only while one is away.
    private readonly Dictionary<int, float> _away = new();

    /// <summary>Over seat 0's poller (<paramref name="player1"/>, the keyboard plus the pads it
    /// borrows) and the feature the seats live in.</summary>
    public MenuSeatDevices(MenuInput player1, PlayerSetupFeature setup)
    {
        _player1 = player1 ?? throw new ArgumentNullException(nameof(player1));
        _setup = setup ?? throw new ArgumentNullException(nameof(setup));
    }

    /// <summary>The pad seat 0 claimed by driving a screen with it, or -1 while it is on the
    /// keyboard and every connected pad is still free to join.</summary>
    public int P1Pad { get; private set; } = -1;

    /// <summary>The single pad a seat's source is bound to, or -1: a pad seat is a
    /// <see cref="BuiltInSeat"/> over a one-pad poller, and any other source has no pad of its own.</summary>
    public static int PadOf(IMenuInputSource source) => source is BuiltInSeat seat ? seat.Input.Pad : -1;

    /// <summary>The poller behind a seat's source, or null for a source that has none (a device-less
    /// seat, or a seat wearing a source of another kind). It is what a screen reading the seat's own
    /// devices needs, and the same detail read in the same one place as the pad above.</summary>
    public static MenuInput? PollerOf(IMenuInputSource source) => (source as BuiltInSeat)?.Input;

    /// <summary>The pads a seat flies with, the binding a launch carries: seat 0's poller's set,
    /// a joined pad's one device, nothing for a device-less seat.</summary>
    public IReadOnlyList<int> FlightPads(PlayerSeat seat)
    {
        ArgumentNullException.ThrowIfNull(seat);
        if (_setup.Seats.Count > 0 && ReferenceEquals(_setup.Seats[0], seat))
        {
            return _player1.Pads ?? Array.Empty<int>();
        }

        int pad = PadOf(seat.Source);
        return pad >= 0 ? new[] { pad } : Array.Empty<int>();
    }

    /// <summary>Whether a pad already belongs to a seat: seat 0's claimed pad or a joined pad.
    /// Before the claim, seat 0 only borrows its pads, so any of them can still join.</summary>
    public bool IsClaimed(int pad)
    {
        if (pad == P1Pad)
        {
            return true;
        }

        var seats = _setup.Seats;
        for (int i = 1; i < seats.Count; i++)
        {
            if (PadOf(seats[i].Source) == pad)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reconciles the seats with the live pad roster over <paramref name="dt"/> seconds: a
    /// device that reappears at another connection index keeps its seat, one still missing after
    /// <see cref="DeviceGrace"/> takes its seat with it (and frees seat 0's claim), and seat 0's
    /// poller is bound to its claimed pad or to every unclaimed one. Returns whether anything
    /// changed.</summary>
    public bool Sync(float dt)
    {
        var connected = Pads.Connected();
        var live = new Dictionary<int, string>(connected.Count);
        foreach (int pad in connected)
        {
            live[pad] = Input.GetJoyGuid(pad);
        }

        bool dirty = false;
        var seats = _setup.Seats;
        for (int i = seats.Count - 1; i >= 1; i--)
        {
            int pad = PadOf(seats[i].Source);
            if (pad < 0 || Holds(pad, live))
            {
                continue;
            }

            int moved = Moved(pad, live);
            if (moved >= 0)
            {
                Reseat(PollerOf(seats[i].Source), pad, moved);
                Log.Info("ui", $"launchscreen: P{i + 1}'s pad is back as {moved} \"{Input.GetJoyName(moved)}\", seat kept");
                dirty = true;
            }
            else if (Gone(pad, dt))
            {
                Log.Info("ui", $"launchscreen: P{i + 1}'s pad {pad} disconnected, player left");
                _setup.Unjoin(seats[i]);
                Forget(pad);
                dirty = true;
            }
        }

        if (P1Pad >= 0 && !Holds(P1Pad, live))
        {
            int moved = Moved(P1Pad, live);
            if (moved >= 0)
            {
                Reseat(null, P1Pad, moved);
                Log.Info("ui", $"launchscreen: P1's pad is back as {moved} \"{Input.GetJoyName(moved)}\", claim kept");
                P1Pad = moved;
                dirty = true;
            }
            else if (Gone(P1Pad, dt))
            {
                Log.Info("ui", $"launchscreen: P1's pad {P1Pad} disconnected, back to keyboard + any free pad");
                Forget(P1Pad);
                P1Pad = -1;
                dirty = true;
            }
        }

        var free = new List<int>(connected.Count);
        if (P1Pad >= 0)
        {
            free.Add(P1Pad);
        }
        else
        {
            foreach (int pad in connected)
            {
                if (!IsClaimed(pad))
                {
                    free.Add(pad);
                }
            }
        }

        // Seat 0 always binds an explicit set here; null (every connected pad) is the in-session
        // reading, which the menu never uses. A pad that just changed hands is primed, not read.
        var bound = _player1.Pads ?? Array.Empty<int>();
        if (!SameSet(free, bound))
        {
            _player1.Pads = free.ToArray();
            _player1.Prime();
            // ⚠ The last-active reading dies with the set it was taken from: it names a pad seat 0
            // was only BORROWING, and a guest steering the menu before they press Start leaves
            // theirs in it, which a later claim would pin seat 0 to.
            _player1.LastActivePad = -1;
            dirty = true;
        }

        return dirty;
    }

    /// <summary>Seeds the per-pad join edges from the current state, so a Start held while a
    /// screen with joining appears does not join a seat at once.</summary>
    public void PrimeJoins()
    {
        _joinPrev.Clear();
        _reported.Clear();
        foreach (int pad in Pads.Connected())
        {
            _joinPrev[pad] = MenuInput.JoinPressed(pad);
        }
    }

    /// <summary>Start on an unclaimed pad joins a seat over that pad, primed, while a seat is
    /// free. Returns whether anyone joined. The caller decides on which screens joining is open.</summary>
    public bool ScanJoins()
    {
        bool dirty = false;
        foreach (int pad in Pads.Connected())
        {
            bool pressed = MenuInput.JoinPressed(pad);
            _joinPrev.TryGetValue(pad, out bool prev);
            _joinPrev[pad] = pressed;
            ReportButton(pad);
            if (!pressed || prev || IsClaimed(pad) || _setup.Seats.Count >= PlayerSetupFeature.MaxSeats)
            {
                ReportRefusal(pad, pressed && !prev);
                continue;
            }

            var input = new MenuInput { Pads = new[] { pad } };
            input.Prime();
            if (_setup.Join(new BuiltInSeat(input)) != null)
            {
                // After the join, which is what decides the seat's player number and therefore
                // which saved keymap this pad navigates on.
                input.LoadSavedKeymap(_setup.Seats.Count);
                Log.Info("ui", $"launchscreen: P{_setup.Seats.Count} joined on pad {pad} \"{Input.GetJoyName(pad)}\"");
                dirty = true;
            }
        }

        return dirty;
    }

    /// <summary>Whether a pad is the captain's, the first to sign on: it shares seat 1 with the
    /// keyboard, and its Start casts off.</summary>
    public bool IsCaptain(int pad) => pad >= 0 && pad == P1Pad;

    /// <inheritdoc/>
    public bool SignedOn(int entry) =>
        entry == 0 ? P1Pad >= 0 : entry > 0 && entry < _setup.Seats.Count && PadOf(_setup.Seats[entry].Source) >= 0;

    /// <inheritdoc/>
    public string Device(int entry)
    {
        if (entry == 0)
        {
            return P1Pad >= 0 ? Input.GetJoyName(P1Pad) : string.Empty;
        }

        if (entry <= 0 || entry >= _setup.Seats.Count)
        {
            return string.Empty;
        }

        int pad = PadOf(_setup.Seats[entry].Source);
        return pad >= 0 ? Input.GetJoyName(pad) : _setup.Seats[entry].Source.DeviceLabel;
    }

    /// <inheritdoc/>
    public void DropSignOns()
    {
        var seats = _setup.Seats;
        for (int i = seats.Count - 1; i >= 1; i--)
        {
            int pad = PadOf(seats[i].Source);
            if (_setup.Unjoin(seats[i]) && pad >= 0)
            {
                Forget(pad);
            }
        }

        if (P1Pad >= 0)
        {
            Forget(P1Pad);
            P1Pad = -1;
        }

        Log.Info("ui", $"join board: left, the manifest is the keyboard alone again");
    }

    /// <summary>Signs <paramref name="pad"/> onto the manifest: the first pad takes the captain's
    /// chair, seat 1 beside the keyboard. Every later one joins a seat of its own. Refused for a pad
    /// another entry holds and once all four seats are taken. Returns whether the manifest moved.</summary>
    public bool SignOn(int pad)
    {
        if (pad < 0 || IsClaimed(pad))
        {
            return false;
        }

        if (P1Pad < 0)
        {
            P1Pad = pad;
            Log.Info("ui", $"join board: pad {pad} \"{Input.GetJoyName(pad)}\" takes the captain's chair");
            return true;
        }

        if (_setup.Seats.Count >= PlayerSetupFeature.MaxSeats)
        {
            return false;
        }

        var input = new MenuInput { Pads = new[] { pad } };
        input.Prime();
        if (_setup.Join(new BuiltInSeat(input)) == null)
        {
            return false;
        }

        // After the join, which is what decides the seat's player number and therefore which saved
        // keymap this pad navigates on.
        input.LoadSavedKeymap(_setup.Seats.Count);
        Log.Info("ui", $"join board: P{_setup.Seats.Count} signed on with pad {pad} \"{Input.GetJoyName(pad)}\"");
        return true;
    }

    /// <summary>Signs <paramref name="pad"/> off: the captain's pad gives its claim back to the
    /// keyboard, and any other seated pad leaves its seat. The entries above it keep their places,
    /// since a seat's number is its position in the list. Returns whether the manifest moved.</summary>
    public bool SignOff(int pad)
    {
        if (pad < 0)
        {
            return false;
        }

        if (pad == P1Pad)
        {
            Forget(pad);
            P1Pad = -1;
            Log.Info("ui", $"join board: pad {pad} left the captain's chair, seat 1 is the keyboard again");
            return true;
        }

        var seats = _setup.Seats;
        for (int i = seats.Count - 1; i >= 1; i--)
        {
            if (PadOf(seats[i].Source) == pad && _setup.Unjoin(seats[i]))
            {
                Forget(pad);
                Log.Info("ui", $"join board: P{i + 1} signed off pad {pad} \"{Input.GetJoyName(pad)}\"");
                return true;
            }
        }

        return false;
    }

    /// <summary>Seeds the board's per-pad gesture edges from the current state. A button still held
    /// from the door that opened the board then fires nothing.</summary>
    public void PrimeBoard()
    {
        _boardPrev.Clear();
        _reported.Clear();
        foreach (int pad in Pads.Connected())
        {
            _boardPrev[pad] = (MenuInput.SignOnPressed(pad), MenuInput.SignOffPressed(pad), MenuInput.JoinPressed(pad));
        }
    }

    /// <summary>One frame of the board's gestures over the live pad roster: A signs a pad on and B
    /// signs one off. Start casts off, but only from the captain's pad. Every button is
    /// edge-detected per device, so a held one fires once.</summary>
    public BoardScan ScanBoard()
    {
        bool dirty = false;
        bool cast = false;
        bool pressed = false;
        foreach (int pad in Pads.Connected())
        {
            var now = (On: MenuInput.SignOnPressed(pad), Off: MenuInput.SignOffPressed(pad), Cast: MenuInput.JoinPressed(pad));
            _boardPrev.TryGetValue(pad, out var prev);
            _boardPrev[pad] = now;
            ReportButton(pad);
            if (now.On && !prev.On)
            {
                dirty |= SignOn(pad);
                pressed = true;
            }

            if (now.Off && !prev.Off)
            {
                dirty |= SignOff(pad);
                pressed = true;
            }

            if (now.Cast && !prev.Cast)
            {
                cast |= IsCaptain(pad);
                pressed = true;
            }
        }

        return new BoardScan(dirty, cast, pressed);
    }

    /// <summary>Pins seat 0 to whichever pad it is steering with, once, so by the time joining
    /// opens every other pad is unambiguously a joiner. Steering with the keyboard claims nothing,
    /// and neither does a pad another seat already holds: two seats on one pad fly both planes off
    /// it and leave the other player's own pad bound to nobody. Returns whether a claim was made.
    /// </summary>
    public bool ClaimP1Pad()
    {
        int pad = _player1.LastActivePad;
        if (P1Pad >= 0 || pad < 0 || IsClaimed(pad))
        {
            return false;
        }

        P1Pad = pad;
        Log.Info("ui", $"launchscreen: P1 claimed pad {pad} \"{Input.GetJoyName(pad)}\" (other pads join at aircraft select)");
        return true;
    }

    private static bool SameSet(List<int> free, int[] bound)
    {
        if (free.Count != bound.Length)
        {
            return false;
        }

        for (int i = 0; i < free.Count; i++)
        {
            if (free[i] != bound[i])
            {
                return false;
            }
        }

        return true;
    }

    // Whether the device a seat was seated on is still answering at that connection index. The
    // first sight of a held pad records its guid, so a seat that has just joined answers true and
    // every later frame compares against what it joined on.
    private bool Holds(int pad, Dictionary<int, string> live)
    {
        if (!live.TryGetValue(pad, out string? guid))
        {
            return false;
        }

        if (_guids.TryGetValue(pad, out string? seated) && seated != guid)
        {
            return false;
        }

        _guids[pad] = guid;
        _away.Remove(pad);
        return true;
    }

    // The connection index a seat's device answers at now, or -1 while it is off the roster or has
    // landed on an index another seat holds. Steam Input's virtual pads come back at whichever
    // index is free, which is why this is a guid search and not an index comparison.
    private int Moved(int pad, Dictionary<int, string> live)
    {
        if (!_guids.TryGetValue(pad, out string? seated))
        {
            return -1;
        }

        foreach (var entry in live)
        {
            if (entry.Value == seated && !IsClaimed(entry.Key))
            {
                return entry.Key;
            }
        }

        return -1;
    }

    // Moves a seat's poller, and the bookkeeping above, onto the index its device now answers at.
    private void Reseat(MenuInput? poller, int from, int to)
    {
        if (poller != null)
        {
            poller.Pads = new[] { to };
            poller.Prime();
        }

        _guids[to] = _guids[from];
        Forget(from);
    }

    // Whether a held pad has been off the roster longer than the grace. The frame it goes missing
    // only starts the clock, so a re-enumeration that spans a frame or two costs nothing. There is
    // no roster to come back to under --no-pads, so the grace collapses there.
    private bool Gone(int pad, float dt)
    {
        if (Pads.Disabled)
        {
            return true;
        }

        _away.TryGetValue(pad, out float waited);
        waited += dt;
        _away[pad] = waited;
        return waited >= DeviceGrace;
    }

    private void Forget(int pad)
    {
        _guids.Remove(pad);
        _away.Remove(pad);
    }

    // Which button a pad is actually sending while joining is open, one line per pad per screen. A
    // device whose Start arrives on another index (a Steam Input or DirectInput mapping the platform
    // has no entry for) joins nobody and leaves no other trace, which reads exactly like a refusal.
    private void ReportButton(int pad)
    {
        if (_reported.Contains(pad) || Pads.InputBlocked)
        {
            return;
        }

        for (int button = 0; button < (int)JoyButton.SdlMax; button++)
        {
            if (!Input.IsJoyButtonPressed(pad, (JoyButton)button))
            {
                continue;
            }

            _reported.Add(pad);
            Log.Info("ui", $"launchscreen: pad {pad} \"{Input.GetJoyName(pad)}\" sends {(JoyButton)button} ({button}) while joining is open");
            return;
        }
    }

    // Why a Start that could have joined did not. The join is the only thing logged otherwise, so a
    // refused gesture and a button that never arrived are indistinguishable from the log.
    private void ReportRefusal(int pad, bool edge)
    {
        if (!edge)
        {
            return;
        }

        string why = pad == P1Pad ? "seat 0 claimed it"
            : IsClaimed(pad) ? "another seat holds it"
            : _setup.Seats.Count >= PlayerSetupFeature.MaxSeats ? $"all {PlayerSetupFeature.MaxSeats} seats are taken"
            : "the seat itself refused the claim";
        Log.Info("ui", $"launchscreen: Start on pad {pad} \"{Input.GetJoyName(pad)}\" joined nobody, {why}");
    }
}
