using System;
using System.Collections.Generic;
using CSVM.Bindings;

namespace CSVM.UI.Menu;

/// <summary>A rebind waiting for the player's word, because the control it wants already belongs to
/// somebody. <see cref="Losers"/> is every action that would lose it, in enum order, and is never
/// empty: a control that is free is bound without asking.</summary>
public sealed record RebindSteal(InputAction Action, Binding Binding, IReadOnlyList<InputAction> Losers);

/// <summary>The shared rebinding screen: which seat is being edited, which context and action the
/// cursor is on, the capture in progress, and the steal it is about to perform. Engine-free like
/// every other feature: a presentation registers each seat's hardware readers once
/// (<see cref="ICaptureDevices"/>) and draws whatever this reports.
/// ⚠ Edits are staged. Every rebind, unbind and reset lands in a working copy of the seat's maps,
/// and only <see cref="Accept"/> writes them through to the maps the polling sites hold.
/// <see cref="Cancel"/> throws the working copy away, which is what makes a whole-map reset safe to
/// press. This is the original's model (`FUN_00419de0`, `FUN_00419d50`, `docs/org/input.md`).
/// ⚠ A steal is never silent. A capture that lands on a held control raises <see cref="Pending"/>
/// naming every action that would lose it, and nothing moves until <see cref="ConfirmSteal"/>.
/// ⚠ Every edit is scoped to <see cref="Player"/>'s own profile. Two seats hold two maps, so a
/// rebind for player 2 cannot reach player 1, and the screen never edits "the" keymap.</summary>
public sealed class ControlsFeature : IMenuFeature
{
    /// <summary>How many of an action's bindings a row prints before it says how many it is
    /// hiding. Four, because that is the longest row the shipped set holds, so nothing ships
    /// hidden.</summary>
    public const int RowBindings = 4;

    private readonly Dictionary<int, SeatState> _seats = new();
    private readonly List<int> _players = new();
    private readonly HashSet<int> _dirty = new();
    private readonly Action<int, BindingProfile>? _save;
    private readonly Func<string?>? _openProfilesFolder;

    private InputContext _context = InputContext.Flight;
    private ControlCapture? _capture;
    private ControlCapture? _stickCapture;
    private int _player;
    private int _row;
    private int _slot;

    /// <summary>A feature whose saves go through <paramref name="save"/>, or nowhere when that is
    /// null. The write is injected rather than reached for. So the feature stays engine-free and a
    /// test never touches the player's real keymap file. The folder opener returns the stick profile
    /// folder's path, or null when it could not open it (<paramref name="openProfilesFolder"/>).</summary>
    public ControlsFeature(Action<int, BindingProfile>? save = null, Func<string?>? openProfilesFolder = null)
    {
        _save = save;
        _openProfilesFolder = openProfilesFolder;
    }

    /// <summary>Raised by <see cref="Accept"/> once per seat it committed, after the save, with the
    /// player and the profile as accepted. A host whose seats copied that player's keymap before the
    /// page opened (a flight in progress) listens here, since the profile this feature edits is not
    /// the one those seats hold.</summary>
    public event Action<int, BindingProfile>? Accepted;

    /// <summary>The seats this screen can edit, in the order they were registered.</summary>
    public IReadOnlyList<int> Players => _players;

    /// <summary>Whichever seat's keymap the screen is showing.</summary>
    public int Player
    {
        get => _player;
        set
        {
            if (!_seats.ContainsKey(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "No seat is registered for that player.");
            _player = value;
            ResetCursor();
        }
    }

    /// <summary>Which of the three keymaps a seat holds is being edited. The steal rule runs inside
    /// a context (<see cref="InputContext"/>), so this is also the scope of every conflict.</summary>
    public InputContext Context
    {
        get => _context;
        set
        {
            _context = value;
            ResetCursor();
        }
    }

    /// <summary>The rows the screen draws: every action this context owns, bound or not.</summary>
    public IReadOnlyList<InputAction> Actions => DefaultBindings.ActionsIn(_context);

    /// <summary>The highlighted row.</summary>
    public int Row => _row;

    /// <summary>The action the highlighted row names.</summary>
    public InputAction Focused => Actions[_row];

    /// <summary>Which of the focused action's bindings is highlighted. Equal to the binding count
    /// when the cursor is on the empty slot past the end, which is how a control is added rather
    /// than replaced.</summary>
    public int Slot => _slot;

    /// <summary>Whether the screen is waiting for the player to press a control.</summary>
    public bool Capturing { get; private set; }

    /// <summary>The steal awaiting the player's word, or null when nothing is pending.</summary>
    public RebindSteal? Pending { get; private set; }

    /// <summary>The line the screen prints under the list: what just happened, or what is being
    /// asked. Empty when there is nothing to say.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Whether any seat's keymap has changed since it was last saved. Tracked per seat
    /// rather than per screen, because stepping to another player must not lose the first player's
    /// edits.</summary>
    public bool Dirty => _dirty.Count > 0;

    /// <summary>The focused action's bindings, in the order the row prints them.</summary>
    public IReadOnlyList<Binding> FocusedBindings => Map.Bindings(Focused);

    /// <summary>Whether the seat being edited reads the keyboard at all. False for a pad-only
    /// splitscreen seat, which cannot capture a key or a mouse button.</summary>
    public bool ReadsKeyboard => _seats[_player].ReadsKeyboard;

    /// <summary>Whether the seat being edited flies with the mouse, staged like every other edit on
    /// this screen, so the scheme is chosen where the controls it competes with are listed. A
    /// pad-only seat cannot hold it, having no mouse to give the stick.</summary>
    public bool MouseFlying
    {
        get => _seats[_player].MouseFlying;
        set
        {
            var seat = _seats[_player];
            bool wanted = value && seat.ReadsKeyboard;
            if (seat.MouseFlying == wanted)
                return;
            seat.MouseFlying = wanted;
            MarkDirty();
        }
    }

    /// <summary>The seat being edited's mouse sensitivity (<see cref="SensitivityScale"/>), staged
    /// beside <see cref="MouseFlying"/> and clamped to the scale's range.</summary>
    public float MouseSensitivity
    {
        get => _seats[_player].MouseSensitivity;
        set
        {
            var seat = _seats[_player];
            float wanted = SensitivityScale.Clamp(value);
            if (seat.MouseSensitivity == wanted)
                return;
            seat.MouseSensitivity = wanted;
            MarkDirty();
        }
    }

    private ActionMap Map => _seats[_player].Working[_context];

    /// <summary>Registers one seat's live keymap. The profile is the one its polling sites read, not
    /// a copy, so <see cref="Accept"/> reaches the seat without a reload; the screen itself edits a
    /// working copy of it. <paramref name="devices"/> is where a capture reads and which identity it
    /// stamps, per context rather than per seat, because the three polling sites do not share one
    /// placeholder (<see cref="ICaptureDevices"/>).</summary>
    public void AddSeat(int player, BindingProfile profile, ICaptureDevices devices, bool readsKeyboard)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(devices);
        if (!_seats.ContainsKey(player))
            _players.Add(player);
        _seats[player] = new SeatState(profile, devices, readsKeyboard);
        // A registration stages from the live maps, so nothing is owed to whoever held the number
        // before it: a dirty flag left behind would have Accept write a copy this seat never edited.
        _dirty.Remove(player);
        if (_players.Count == 1)
            _player = player;
    }

    /// <summary>Drops a seat this screen can no longer edit, with its staged edits, because its
    /// device is gone or its player number changed hands. Edits already accepted are in the maps
    /// the polling sites hold and stay there. Unknown players are ignored.
    /// ⚠ A host that registers seats must drop them too. A player row kept after its pad left has
    /// nothing to press, and the stepper would offer a seat that cannot capture.</summary>
    public void RemoveSeat(int player)
    {
        if (!_seats.Remove(player))
            return;

        _players.Remove(player);
        _dirty.Remove(player);
        if (_player == player && _players.Count > 0)
            Player = _players[0];
    }

    /// <summary>Brings <paramref name="player"/>'s live keymap up to rows that changed under the screen,
    /// through <paramref name="merge"/>, and restages the working copy from it when nothing is staged.
    /// Staged edits are kept, since a restage would throw them away. Unknown players are ignored.
    /// ⚠ Accept writes the working copy over the live maps. A copy staged before the change would put
    /// the old rows back, and the save would carry them.</summary>
    public void Follow(int player, Action<BindingProfile> merge)
    {
        ArgumentNullException.ThrowIfNull(merge);
        if (!_seats.TryGetValue(player, out var seat))
            return;

        merge(seat.Profile);
        if (!_dirty.Contains(player))
            seat.Restage();
    }

    /// <summary>The live keymap registered for <paramref name="player"/>, or null for none. The menu and
    /// the pause leaf register the same player numbers over different readers, and this says whose
    /// maps an Accept would save.</summary>
    public BindingProfile? ProfileOf(int player) => _seats.TryGetValue(player, out var seat) ? seat.Profile : null;

    /// <summary>That action's bindings on the seat being edited.</summary>
    public IReadOnlyList<Binding> Bindings(InputAction action) => Map.Bindings(action);

    /// <summary>That action's bindings in a context the cursor is not standing in, for a screen
    /// whose page lists rows from more than one keymap. A read, so it never moves
    /// <see cref="Context"/>: stepping the context resets the cursor and drops a capture in
    /// progress, which a screen drawing itself must not do.</summary>
    public IReadOnlyList<Binding> Bindings(InputContext context, InputAction action) =>
        _seats[_player].Working[context].Bindings(action);

    /// <summary>One row's controls as text, hiding nothing without saying so.</summary>
    public string RowText(InputAction action) => BindingLabels.Row(Map.Bindings(action), RowBindings);

    /// <summary>Points the row cursor at one of <see cref="Actions"/>, clamped, and puts the slot
    /// cursor back on that row's first control. The screen owns the wrap, because its own list
    /// carries the seat and context steppers above these rows.</summary>
    public void Focus(int row)
    {
        int count = Actions.Count;
        _row = row < 0 ? 0 : row >= count ? count - 1 : row;
        _slot = 0;
        CancelCapture();
    }

    /// <summary>Moves the slot cursor along the focused action's controls, clamped, with one place
    /// past the last for adding a control rather than replacing one.</summary>
    public void MoveSlot(int step)
    {
        int max = FocusedBindings.Count;
        int next = _slot + step;
        _slot = next < 0 ? 0 : next > max ? max : next;
        CancelCapture();
    }

    /// <summary>Starts listening for a control. Everything already held is masked, so the button
    /// that opened the capture is not read as the answer to it.
    /// ⚠ The hardware comes from the seat's own reader for the context on screen, never from a
    /// caller. A reader answers for one pad identity and the three contexts hold three, so a
    /// caller-supplied state mismatches silently: pads read false and nothing throws
    /// (<see cref="ICaptureDevices"/>).</summary>
    public void BeginCapture()
    {
        var seat = _seats[_player];
        Pending = null;
        Capturing = true;
        _capture = new ControlCapture(seat.PadOf(_context), seat.ReadsKeyboard, row: Focused);
        _capture.Arm(seat.Devices.For(_context));
        Status = CapturePrompt(Focused);
    }

    /// <summary>Opens the folder the stick profile files live in, through whatever opener the host
    /// supplied, and says where it is. The files are where a binding's deadzone is edited.</summary>
    public void OpenProfilesFolder()
    {
        string? path = _openProfilesFolder?.Invoke();
        Status = path is null
            ? "The stick profiles folder could not be opened."
            : $"Opened {path}. Deadzones are edited per binding in these files.";
    }

    /// <summary>Stops listening without binding anything.</summary>
    public void CancelCapture()
    {
        if (Capturing)
            Status = string.Empty;
        Capturing = false;
        _capture = null;
    }

    /// <summary>One frame of a capture in progress: returns true when something the screen draws
    /// changed. Does nothing while no capture is running.</summary>
    public bool Poll()
    {
        if (!Capturing || _capture is not { } capture)
            return false;

        // One read of the seat's hardware for the whole frame, so the cancel check and the capture
        // cannot see two different pad lists.
        var state = _seats[_player].Devices.For(_context);
        if (capture.Cancelled(state))
        {
            Capturing = false;
            _capture = null;
            Status = "Cancelled.";
            return true;
        }

        if (capture.Poll(state) is not { } binding)
            return false;

        Capturing = false;
        _capture = null;
        return ReferenceEquals(capture, _stickCapture) ? OfferStick(binding) : Offer(binding);
    }

    /// <summary>Starts listening for a stick control alone, the original-style page's Stick column.
    /// Keys and pad controls are not read, and what it hears goes through <see cref="OfferStick"/>.
    /// </summary>
    public void BeginStickCapture()
    {
        var seat = _seats[_player];
        Pending = null;
        Capturing = true;
        _capture = _stickCapture = new ControlCapture(
            seat.PadOf(_context), seat.ReadsKeyboard, Focused, sticksOnly: true);
        _capture.Arm(seat.Devices.For(_context));
        Status = $"Move or press a stick control for {BindingLabels.Name(Focused)}.";
    }

    /// <summary>Proposes a stick control in place of the focused action's binding on the same stick
    /// model, or beside the others when that model holds none. One stick's capture never takes
    /// another stick's, a key's or a pad's control (Decision 15b, docs/PLAN-flight-sticks.md).</summary>
    public bool OfferStick(Binding binding)
    {
        var bindings = FocusedBindings;
        _slot = bindings.Count;
        for (int i = 0; i < bindings.Count; i++)
        {
            if (bindings[i].Device == binding.Device)
            {
                _slot = i;
                break;
            }
        }

        return Offer(binding);
    }

    /// <summary>Proposes a control for the focused slot, as a capture does. Public because it is
    /// the whole rule and a test drives it without a device.</summary>
    public bool Offer(Binding binding)
    {
        // A full axis sits on both rows of its pair, so the partner holding it loses nothing.
        var partner = binding.Control.Kind == ControlKind.FullAxis ? AxisPairs.PartnerOf(Focused) : null;
        var losers = new List<InputAction>();
        foreach (var owner in Map.OwnersOf(binding))
        {
            if (owner != Focused && owner != partner)
                losers.Add(owner);
        }

        if (losers.Count == 0)
        {
            Commit(binding);
            return true;
        }

        Pending = new RebindSteal(Focused, binding, losers);
        Status = $"{BindingLabels.Describe(binding)} is bound to {BindingLabels.Clause(losers)}. "
            + "Accept takes it; Back leaves it alone.";
        return true;
    }

    /// <summary>Performs the pending steal, which is the only path that takes a control off another
    /// action.</summary>
    public void ConfirmSteal()
    {
        if (Pending is not { } pending)
            return;
        Pending = null;
        Commit(pending.Binding);
    }

    /// <summary>Drops the pending steal and leaves every action's controls where they were.
    /// </summary>
    public void DiscardSteal()
    {
        if (Pending is null)
            return;
        Pending = null;
        Status = "Left alone.";
    }

    /// <summary>Drops the highlighted control from the focused action, touching no other action.
    /// </summary>
    public void UnbindSlot()
    {
        var bindings = FocusedBindings;
        if (_slot >= bindings.Count)
            return;

        var dropped = bindings[_slot];
        Map.Unassign(Focused, dropped);
        _slot = Math.Min(_slot, FocusedBindings.Count);
        MarkDirty();
        var losers = new List<InputAction> { Focused };
        if (dropped.Control.Kind == ControlKind.FullAxis && AxisPairs.PartnerOf(Focused) is { } partner)
            losers.Add(partner);
        Status = $"{BindingLabels.Clause(losers)} lost {BindingLabels.Describe(dropped)}.";
    }

    /// <summary>Puts one of this seat's contexts back to the shipped defaults, in the working copy.
    /// The whole-map form is <see cref="ResetSeat"/>, which is the button the original ships.
    /// </summary>
    public void ResetContext()
    {
        var seat = _seats[_player];
        Restore(seat.Working[_context], DefaultBindings.MapFor(_context, seat.PadOf(_context)));
        _slot = 0;
        MarkDirty();
        Status = $"{ContextName(_context)} defaults restored. Accept to keep them.";
    }

    /// <summary>Puts this seat's whole keymap back to the shipped defaults, every context at once,
    /// in the working copy. The original's RESET TO DEFAULT rebuilds every category's rows from the
    /// defaults and leaves the live map alone until ACCEPT (`FUN_00419de0`), which is what makes one
    /// press of it recoverable.</summary>
    public void ResetSeat()
    {
        var seat = _seats[_player];
        foreach (var context in System.Enum.GetValues<InputContext>())
            Restore(seat.Working[context], DefaultBindings.MapFor(context, seat.PadOf(context)));

        _slot = 0;
        MarkDirty();
        Status = "Every control set restored to defaults. Accept to keep them.";
    }

    /// <summary>Commits every changed seat's working copy into the maps its polling sites hold, then
    /// writes each through whatever save the host supplied. Every seat rather than the one on
    /// screen, since a player who edits two seats and accepts once expects both.
    /// ⚠ The commit fills the existing <see cref="ActionMap"/> rather than replacing it, because the
    /// menu poller holds that object and a swapped reference would leave it on the old keymap.
    /// </summary>
    public void Accept()
    {
        foreach (int player in _players)
        {
            if (!_dirty.Contains(player))
                continue;

            var seat = _seats[player];
            foreach (var context in System.Enum.GetValues<InputContext>())
                Restore(seat.Profile.Map(context), seat.Working[context]);

            seat.Profile.MouseFlying = seat.MouseFlying;
            seat.Profile.MouseSensitivity = seat.MouseSensitivity;
            _save?.Invoke(player, seat.Profile);
            Accepted?.Invoke(player, seat.Profile);
        }

        _dirty.Clear();
        Pending = null;
        Capturing = false;
        _capture = null;
        Status = "Changes accepted.";
    }

    /// <summary>Throws away every seat's staged edits, a reset included, and re-stages from the live
    /// keymaps. Nothing the screen did since it was opened or last accepted survives.</summary>
    public void Cancel()
    {
        foreach (var seat in _seats.Values)
            seat.Restage();

        _dirty.Clear();
        ResetCursor();
        Status = "Changes cancelled.";
    }

    /// <summary>Drops the capture, the pending steal and the status line, leaving the staged edits
    /// and the live keymaps alone. A presentation switch is not an answer to Accept or Cancel.
    /// </summary>
    public void Discard()
    {
        Capturing = false;
        Pending = null;
        Status = string.Empty;
        ResetCursor();
    }

    // Refills one map from another in place, keeping the target object identity. Add rather than
    // Assign, because the shipped set deliberately puts one control on two actions and a steal here
    // would silently undo the second.
    private static void Restore(ActionMap target, ActionMap source)
    {
        target.Clear();
        foreach (var action in source.BoundActions)
        {
            foreach (var binding in source.Bindings(action))
                target.Add(action, binding);
        }
    }

    // The capture prompt. A row that takes a whole stick axis says which way to move it, since the
    // direction moved decides the invert (AxisPairs.FullAxisFor).
    private static string CapturePrompt(InputAction row)
    {
        string name = BindingLabels.Name(row);
        if (AxisPairs.IsAbsolute(row))
            return $"Press a control for {name}, or push a stick lever to full throttle.";
        if (AxisPairs.TakesFullAxis(row))
            return $"Press a control for {name}, or move a stick axis toward {name}.";
        return $"Press a control for {name}.";
    }

    private static string ContextName(InputContext context) => context switch
    {
        InputContext.Flight => "Flying",
        InputContext.Menu => "Menu",
        _ => "Free camera",
    };

    private void Commit(Binding binding)
    {
        var bindings = FocusedBindings;
        if (_slot < bindings.Count)
            Map.Unassign(Focused, bindings[_slot]);

        var stolen = Map.Assign(Focused, binding);
        _slot = Math.Max(0, Map.Bindings(Focused).Count - 1);
        MarkDirty();
        Status = stolen.Count == 0
            ? $"{BindingLabels.Name(Focused)} is now {BindingLabels.Describe(binding)}."
            : $"{BindingLabels.Name(Focused)} is now {BindingLabels.Describe(binding)}. "
                + $"{BindingLabels.Clause(stolen)} lost it.";
    }

    private void MarkDirty() => _dirty.Add(_player);

    private void ResetCursor()
    {
        _row = 0;
        _slot = 0;
        Capturing = false;
        _capture = null;
        Pending = null;
    }

    // One registered seat: the live profile, the working copy the screen edits, and the two things
    // a capture needs to know about the seat.
    private sealed class SeatState
    {
        public SeatState(BindingProfile profile, ICaptureDevices devices, bool readsKeyboard)
        {
            Profile = profile;
            Devices = devices;
            ReadsKeyboard = readsKeyboard;
            Restage();
        }

        public BindingProfile Profile { get; }

        public ICaptureDevices Devices { get; }

        public bool ReadsKeyboard { get; }

        public Dictionary<InputContext, ActionMap> Working { get; } = new();

        public bool MouseFlying { get; set; }

        public float MouseSensitivity { get; set; }

        public DeviceId PadOf(InputContext context) => Devices.PadOf(context);

        /// <summary>Takes the working copy back to what the polling sites currently hold.</summary>
        public void Restage()
        {
            foreach (var context in System.Enum.GetValues<InputContext>())
                Working[context] = Profile.Map(context).Clone();

            MouseFlying = Profile.MouseFlying && ReadsKeyboard;
            MouseSensitivity = Profile.MouseSensitivity;
        }
    }
}
