using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Bindings;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// One launchscreen player's input source: the keyboard (player 1 only) and that player's own
/// gamepads, resolved through the named-action seam every frame with edge detection and
/// auto-repeat. Per-player rather than an any-pad OR across the roster, which is what makes the
/// join flow possible at all.
/// ⚠ <see cref="JoinPressed"/> and <see cref="LastActivePad"/> keep polling raw device state and
/// must stay that way: both answer "which pad did that", which an OR across a seat's bindings
/// cannot express. Do not move any of this to Godot's input map or focus system.
/// <see cref="Prime"/> seeds the edge flags from the current state, so a button still held from
/// whatever brought us here is not read as a fresh press on the next frame.
/// </summary>
public sealed class MenuInput
{
    /// <summary>The identity this seat's pad bindings sit on. A placeholder like
    /// <see cref="DefaultBindings.AnyPad"/>, because a menu seat reads a SET of pads (player 1 holds
    /// every unclaimed one) rather than one device, and no binding may store a connection index.
    /// <see cref="SeatDeviceState"/> is what answers for it.</summary>
    public static readonly DeviceId SeatPads = DeviceId.Joypad("menu-seat");

    /// <summary>Whether this player also flies the keyboard (player 1 only).</summary>
    public bool Keyboard;

    /// <summary>Whether the screen showing is taking typed text from this player's keyboard. The
    /// letter aliases below (W/A/S/D, Space, L, P) then read as dead, because otherwise typing a
    /// name would also walk the cursor and confirm the screen. The arrows, Enter and Escape stay
    /// live, and the pad is untouched: it types nothing and so collides with nothing.</summary>
    public bool TextEntry;

    /// <summary>The gamepad devices this player reads, or null for every connected pad, the same
    /// binding <see cref="CSVM.Flight.Airframe.FlightController"/> takes. A joined player has exactly one
    /// (the pad they pressed Start on); <b>player 1 holds every pad nobody has claimed</b>, which
    /// preserves the any-pad fix: phantom joypad devices can occupy the early slots, so binding
    /// player 1 to <c>pads[0]</c> would leave a real controller dead. Idle devices read as zero,
    /// so reading several is safe.</summary>
    public int[]? Pads = Array.Empty<int>();

    // Results of the last Poll, valid until the next one.
    public int Move;        // −1 up, +1 down, 0 none (auto-repeat already applied)
    /// <summary>−1 left, +1 right, 0 none (auto-repeat already applied), a second, independent
    /// axis from <see cref="Move"/> so a screen can carry a list cursor (vertical) and a numeric
    /// stepper (horizontal) at once, the way the Instant Action wizard's mission-type screen reads
    /// mission choice and lives together.</summary>
    public int MoveX;
    /// <summary>The pad's own halves of the two cursor axes, without the keyboard. A screen whose
    /// keyboard is typing text reads these instead: W, A, S and D are letters there, so the
    /// combined axes above would move the cursor on every second character typed.</summary>
    public int PadMove;

    /// <summary>The horizontal twin of <see cref="PadMove"/>, same reason.</summary>
    public int PadMoveX;

    /// <summary>The characters typed this frame, "" for none, keyboard only, edge-detected per
    /// key. Shift gives a letter's upper case and any other key's US-layout shifted symbol. Polled
    /// like everything else here rather than read off an input event, so one screen's text and its
    /// navigation share a clock.</summary>
    public string Typed = string.Empty;

    /// <summary>Backspace pressed this frame (edge), the deletion half of <see cref="Typed"/>.</summary>
    public bool Erase;

    /// <summary>Whether this poll moved the seat from one device to the other, a board hint's cue to
    /// recompose. The rule and the counting are <see cref="ActiveDevice"/>'s, the same handover the
    /// flight prompts follow.</summary>
    public bool DeviceMoved;

    public bool Accept;     // pressed this frame (edge)
    public bool Back;       // pressed this frame (edge)
    /// <summary>Back on the pad alone, without Escape, for a reader whose Escape is already spoken
    /// for elsewhere. A board menu's is: Escape toggles the pause that owns the board, so reading
    /// it here as well would toggle twice on one press.</summary>
    public bool PadBack;
    public bool Start;      // pressed this frame (edge)

    /// <summary>Open the loadout for whatever this screen is about (edge). Y is free in menu
    /// context, nothing else here reads it, and its only other use is in flight, so it can mean
    /// one thing everywhere the menu offers a fit to edit.</summary>
    public bool Loadout;

    /// <summary>Open the Instant Action Table of Contents (edge). INVENTED: the original picks a
    /// preset with a mouse on a list that shares its page with the dropdowns, so there is no
    /// decoded button here. X is the last free face button in menu context.</summary>
    public bool Presets;

    /// <summary>The last of this player's pads seen actually doing something (a menu button or the
    /// stick past the deadzone), −1 until one does. The launchscreen uses it to <i>claim</i> the
    /// pad player 1 drives the Mode/Chapter screens with, so that pad is player 1's for good and
    /// only the remaining ones can join. Start is excluded on purpose: it is the join gesture, not
    /// evidence that this player owns the pad.</summary>
    public int LastActivePad = -1;

    // Auto-repeat while a direction is held (carried over from the original LaunchMenu). Confirmed
    // at the controls: join/lock feel reads right at 2P and 4P, no retune owed.
    private const float RepeatInitial = 0.42f;   // s before the first repeat
    private const float RepeatInterval = 0.12f;  // s between repeats after that
    // What ScanActivePad counts as somebody actually steering with a stick. The cursor axes take
    // the same number from their own bindings (DefaultBindings), which is where it is tunable.
    private const float StickDeadzone = 0.5f;

    // The keys a text field takes a character from. Deliberately wider than any box's accept rule:
    // a character the box refuses has to reach the box for the box to cue its reject sound, and a
    // key that types nothing at all is silent instead.
    private static readonly Key[] TextKeys = BuildTextKeys();

    // One timing rule for both cursor axes, shared with TapHoldButton's hold instead of a pair of
    // hand-rolled timer fields.
    private readonly HoldToRepeat _repeat = new(RepeatInitial, RepeatInterval);
    private readonly HoldToRepeat _repeatX = new(RepeatInitial, RepeatInterval);
    private readonly HoldToRepeat _repeatPad = new(RepeatInitial, RepeatInterval);
    private readonly HoldToRepeat _repeatPadX = new(RepeatInitial, RepeatInterval);

    // Previous state of every text key, in TextKeys order, for the same edge detection the
    // buttons get.
    private readonly bool[] _textPrev = new bool[TextKeys.Length];

    // This seat's hardware, as the binding model addresses it. Deliberately not exposed: it answers
    // for SeatPads and nothing else, so a rebinding screen capturing a Flight or Camera control
    // through it would read every pad as false. A screen builds its own reader per context
    // (SeatCaptureDevices) from the identity that context's rows sit on.
    private readonly SeatDeviceState _devices;

    // The same seat with its pads muted, which is the only way to read the keyboard half on its own:
    // a reader over the live state sees both halves at once and could never say which one moved.
    private readonly SeatDeviceState _padMuted;

    // The seat read three ways on one tick: keyboard live, keyboard minus the typeable keys, and
    // the pad alone. Three seats over two maps rather than one, because the pad-only twins and the
    // text-entry aliasing are both narrower readings of the same bindings.
    private readonly PlayerActions _keys;
    private readonly PlayerActions _padOnly;

    // The keyboard half alone, over the pad-muted state, and which side the hints name.
    private readonly PlayerActions _keysOnly;
    private readonly ActiveDevice _device = new();

    private PlayerActions _typingKeys;

    // Whichever of the two keyboard seats TextEntry selected this tick.
    private PlayerActions _live;

    // A rebind landed on Map, so the text-entry reading (a clone with the typeable keys dropped) is
    // stale and is rebuilt on the next read rather than on every one.
    private bool _typingStale;

    private bool _acceptPrev, _backPrev, _padBackPrev, _startPrev, _loadoutPrev, _presetsPrev;
    private bool _erasePrev;
    private int _dirPrev, _dirXPrev, _dirPadPrev, _dirPadXPrev;

    public MenuInput()
    {
        _devices = new SeatDeviceState(SeatPads, () => Pads);
        _padMuted = new SeatDeviceState(SeatPads, () => Pads, readsPads: false);
        var map = DefaultBindings.MapFor(InputContext.Menu, SeatPads);

        // The keyboard gate follows the Keyboard field per tick (ReadDevices), not the value it
        // holds here: every caller sets it in an object initializer, after this runs.
        _keys = new PlayerActions(map, true);
        _typingKeys = new PlayerActions(TypingMap(map), true);
        _padOnly = new PlayerActions(map, false);
        _keysOnly = new PlayerActions(map, true);
        _live = _keys;
    }

    /// <summary>The keys a text field takes a character from, in the order <see cref="Typed"/>
    /// reports them. Wider than either name box's accept rule on purpose, so a refused character
    /// still arrives and the box can cue its reject sound.</summary>
    public static IReadOnlyList<Key> TypeableKeys => TextKeys;

    /// <summary>This seat's live menu keymap, the object a rebinding screen edits. Editing it moves
    /// the bindings this poller reads on its next frame, since the readers hold the map itself; call
    /// <see cref="RebindsApplied"/> afterwards so the text-entry reading is rebuilt too.</summary>
    public ActionMap Map => _keys.Map;

    /// <summary>The single pad this player is bound to, or −1 when it has none or several
    /// (player 1's unclaimed set), for logging and the join bookkeeping.</summary>
    public int Pad => Pads is { Length: 1 } ? Pads[0] : -1;

    /// <summary>Which side of this seat's hardware a control hint names, moved by the seat's own
    /// last real input. A seat with no keyboard reads the pad for the whole session.</summary>
    public DeviceSide Device => _device.Side;

    /// <summary>A short description of what drives this player, for the menu's join strip.</summary>
    public string DeviceLabel
    {
        get
        {
            string pads = Pads == null ? "any pad"
                : Pads.Length == 0 ? ""
                : Pads.Length == 1 ? $"pad {Pads[0]}" : $"{Pads.Length} pads";
            if (Keyboard)
                return pads.Length == 0 ? "keyboard" : $"keyboard + {pads}";
            return pads.Length == 0 ? "no device" : pads;
        }
    }

    /// <summary>Whether an unbound pad is pressing Start, the join gesture. Static because the
    /// pad has no player (and therefore no <see cref="MenuInput"/>) until it joins; the caller
    /// edge-detects per device. Gated like every other pad read, so nobody joins while the
    /// window is in the background.</summary>
    public static bool JoinPressed(int pad) =>
        !CSVM.Pads.InputBlocked && Input.IsJoyButtonPressed(pad, JoyButton.Start);

    /// <summary>Whether a pad is pressing A, the join board's sign-on gesture. Raw and static for
    /// the reason <see cref="JoinPressed"/> is: the pad has no player until it signs on, so no
    /// seat's bindings can answer for it.</summary>
    public static bool SignOnPressed(int pad) =>
        !CSVM.Pads.InputBlocked && Input.IsJoyButtonPressed(pad, JoyButton.A);

    /// <summary>Whether a pad is pressing B, the board's sign-off gesture. Raw for the same reason:
    /// the answer must name the pad that moved, not the seat that holds it.</summary>
    public static bool SignOffPressed(int pad) =>
        !CSVM.Pads.InputBlocked && Input.IsJoyButtonPressed(pad, JoyButton.B);

    /// <summary>One frame of one cursor axis: a fresh press or a direction flip fires immediately
    /// and arms the initial delay, a held direction repeats on the timer, and letting go releases
    /// it. Public so the d-pad shape unit-tests (the repo takes public members over
    /// InternalsVisibleTo); it reads no device itself.</summary>
    public static int StepAxis(int dir, ref int prev, HoldToRepeat repeat, float dt)
    {
        int move = 0;
        if (dir != prev)
        {
            if (dir != 0)
            {
                move = dir;
                repeat.Press();
            }
            else
            {
                repeat.Release();
            }
        }
        else if (dir != 0 && repeat.Tick(dt))
        {
            move = dir;
        }

        prev = dir;
        return move;
    }

    /// <summary>One cursor axis out of a resolved seat: negative wins a frame where both ends are
    /// held, which is the order the raw reads had. Public for the mapping unit tests, like
    /// <see cref="StepAxis"/>; it reads no device itself.</summary>
    public static int Dir(PlayerActions actions, InputAction negative, InputAction positive)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return actions.Held(negative) ? -1 : actions.Held(positive) ? 1 : 0;
    }

    /// <summary>The characters the typeable keys produce this frame: each key whose state rose
    /// since <paramref name="prev"/>, in table order, Shift deciding a letter's case and the
    /// symbol any other key prints.
    /// <paramref name="prev"/> is the caller's edge state, updated in place and sized from
    /// <see cref="TypeableKeys"/> so it cannot fall out of step with the table. Public so text
    /// entry unit-tests; it reads no device itself.</summary>
    public static string TypedFrom(Func<Key, bool> down, bool shift, bool[] prev)
    {
        ArgumentNullException.ThrowIfNull(down);
        ArgumentNullException.ThrowIfNull(prev);
        if (prev.Length != TextKeys.Length)
            throw new ArgumentException($"edge state must be {TextKeys.Length} long", nameof(prev));

        var typed = new StringBuilder();
        for (int i = 0; i < TextKeys.Length; i++)
        {
            bool held = down(TextKeys[i]);
            if (held && !prev[i])
                typed.Append(CharFor(TextKeys[i], shift));
            prev[i] = held;
        }

        return typed.Length > 0 ? typed.ToString() : string.Empty;
    }

    /// <summary>The menu keymap with every binding on a typeable key dropped, which is what
    /// <see cref="TextEntry"/> reads. A letter bound to a cursor action would otherwise walk the
    /// cursor on every second character typed into a name field. The rule is the key's typeability
    /// rather than a fixed list, so it still holds after a rebind.</summary>
    public static ActionMap TypingMap(ActionMap full)
    {
        ArgumentNullException.ThrowIfNull(full);
        var map = full.Clone();
        foreach (var action in DefaultBindings.ActionsIn(InputContext.Menu))
        {
            foreach (var binding in full.Bindings(action))
            {
                if (binding.Device.Kind == DeviceKind.Keyboard && Array.IndexOf(TextKeys, (Key)binding.Control.Index) >= 0)
                    map.Unassign(action, binding);
            }
        }

        return map;
    }

    /// <summary>Told after a rebinding screen edits <see cref="Map"/>, so the text-entry reading is
    /// rebuilt from the new bindings. Without it a control rebound onto a letter would stay live
    /// while a name is being typed, which is the collision <see cref="TypingMap"/> exists to stop.
    /// </summary>
    public void RebindsApplied() => _typingStale = true;

    /// <summary>One control hint for this seat: <paramref name="template"/>'s <c>%1</c> slot filled
    /// with whichever of <paramref name="action"/>'s bindings the seat's own device can reach, as
    /// words or as a glyph. Empty where this seat reaches none, which leaves the hint off rather
    /// than naming a control the player does not have.</summary>
    public ControlLine Hint(string template, InputAction action) =>
        ControlLine.For(template, Map, action, _device.Side, Keyboard);

    /// <summary>Puts this seat on the menu keymap <paramref name="player"/> saved, in place, so the
    /// map this poller's readers hold is the one that changed. Anything the file does not carry
    /// stays at its shipped default, and under the launch gate no file is read at all
    /// (<see cref="LaunchBindings"/>). Called once the seat knows which player it is.</summary>
    public void LoadSavedKeymap(int player)
    {
        Map.Fill(LaunchBindings.Map(player, InputContext.Menu, SeatPads, readsKeyboard: true));
        RebindsApplied();
    }

    /// <summary>Reads this player's devices and fills the result fields.</summary>
    public void Poll(float dt)
    {
        ReadDevices();
        DeviceMoved = _device.Observe(_keysOnly.Current, _padOnly.Current, Keyboard);
        Move = StepAxis(RawDir(), ref _dirPrev, _repeat, dt);
        MoveX = StepAxis(RawDirX(), ref _dirXPrev, _repeatX, dt);
        PadMove = StepAxis(RawPadDir(), ref _dirPadPrev, _repeatPad, dt);
        PadMoveX = StepAxis(RawPadDirX(), ref _dirPadXPrev, _repeatPadX, dt);
        PollText();

        bool accept = RawAccept();
        Accept = accept && !_acceptPrev;
        _acceptPrev = accept;

        bool back = RawBack();
        Back = back && !_backPrev;
        _backPrev = back;

        bool padBack = RawPadBack();
        PadBack = padBack && !_padBackPrev;
        _padBackPrev = padBack;

        bool start = RawStart();
        Start = start && !_startPrev;
        _startPrev = start;

        bool loadout = RawLoadout();
        Loadout = loadout && !_loadoutPrev;
        _loadoutPrev = loadout;

        bool presets = RawPresets();
        Presets = presets && !_presetsPrev;
        _presetsPrev = presets;

        int active = ScanActivePad();
        if (active >= 0)
            LastActivePad = active;
    }

    /// <summary>Seeds the edge flags from the current state (no press is reported for anything
    /// already held) and clears the last results.</summary>
    public void Prime()
    {
        ReadDevices();
        _acceptPrev = RawAccept();
        _backPrev = RawBack();
        _padBackPrev = RawPadBack();
        _startPrev = RawStart();
        _loadoutPrev = RawLoadout();
        _presetsPrev = RawPresets();
        _dirPrev = RawDir();
        if (_dirPrev != 0)
            _repeat.Press();
        else
            _repeat.Release();
        Move = 0;
        _dirXPrev = RawDirX();
        if (_dirXPrev != 0)
            _repeatX.Press();
        else
            _repeatX.Release();
        MoveX = 0;
        PrimePadAxes();
        PrimeText();
        // Seeds the handover's own counts too, so a button still held from whatever raised this
        // screen is not read as the press that hands the hints to the other device.
        _device.Observe(_keysOnly.Current, _padOnly.Current, Keyboard);
        Accept = Back = PadBack = Start = Loadout = Presets = DeviceMoved = false;
    }

    // The Key enum's letter, digit and punctuation values ARE their ASCII codes, so the unshifted
    // character is the key. Shift cases a letter and takes every other key to the US-layout symbol
    // printed above it, the one layout the Key names describe; a box's accept rule, not this table,
    // decides which of those it takes (the unlocking pilot name ends in Shift+1).
    private static char CharFor(Key key, bool shift)
    {
        if (key == Key.Space)
            return ' ';
        char c = (char)(int)key;
        if (key is >= Key.A and <= Key.Z)
            return shift ? c : char.ToLowerInvariant(c);
        return shift ? ShiftedUs(c) : c;
    }

    // The US-layout shifted row: the digits and the punctuation keys BuildTextKeys polls.
    private static char ShiftedUs(char c) => c switch
    {
        '1' => '!',
        '2' => '@',
        '3' => '#',
        '4' => '$',
        '5' => '%',
        '6' => '^',
        '7' => '&',
        '8' => '*',
        '9' => '(',
        '0' => ')',
        '\'' => '"',
        ',' => '<',
        '-' => '_',
        '.' => '>',
        '/' => '?',
        ';' => ':',
        '=' => '+',
        '[' => '{',
        '\\' => '|',
        ']' => '}',
        '`' => '~',
        _ => c,
    };

    // The letters, the digit row, the space bar and then the punctuation, in that order. The
    // punctuation is every printable non-alphanumeric key a US layout reports unshifted; it is
    // polled so a name box has a character to refuse rather than the press vanishing in here.
    private static Key[] BuildTextKeys()
    {
        var keys = new List<Key>();
        for (Key k = Key.A; k <= Key.Z; k++)
            keys.Add(k);
        for (Key k = Key.Key0; k <= Key.Key9; k++)
            keys.Add(k);
        keys.Add(Key.Space);
        keys.AddRange(new[]
        {
            Key.Apostrophe, Key.Comma, Key.Minus, Key.Period, Key.Slash, Key.Semicolon,
            Key.Equal, Key.Bracketleft, Key.Backslash, Key.Bracketright, Key.Quoteleft,
        });
        return keys.ToArray();
    }

    // The pad-only axes' half of Prime, kept together so neither can be seeded and the other not.
    private void PrimePadAxes()
    {
        _dirPadPrev = RawPadDir();
        if (_dirPadPrev != 0)
            _repeatPad.Press();
        else
            _repeatPad.Release();
        _dirPadXPrev = RawPadDirX();
        if (_dirPadXPrev != 0)
            _repeatPadX.Press();
        else
            _repeatPadX.Release();
        PadMove = PadMoveX = 0;
    }

    // A key still held from whatever opened the screen must not type itself into the field.
    private void PrimeText()
    {
        for (int i = 0; i < TextKeys.Length; i++)
            _textPrev[i] = KeyDown(TextKeys[i]);
        _erasePrev = KeyDown(Key.Backspace);
        Typed = string.Empty;
        Erase = false;
    }

    // Every typeable key pressed this frame, plus Backspace. Shift decides case, which is what lets
    // a profile name read as the original's own mixed-case roster does, and the shifted symbols.
    private void PollText()
    {
        Typed = TypedFrom(KeyDown, KeyDown(Key.Shift), _textPrev);
        bool erase = KeyDown(Key.Backspace);
        Erase = erase && !_erasePrev;
        _erasePrev = erase;
    }

    // The first of this player's pads currently producing menu input (excluding Start).
    // Phantom devices never register, they read idle, so a pad found here is demonstrably a
    // real one somebody is holding.
    private int ScanActivePad()
    {
        foreach (int pad in CSVM.Pads.For(Pads))
        {
            if (Input.IsJoyButtonPressed(pad, JoyButton.A) ||
                Input.IsJoyButtonPressed(pad, JoyButton.B) ||
                Input.IsJoyButtonPressed(pad, JoyButton.DpadUp) ||
                Input.IsJoyButtonPressed(pad, JoyButton.DpadDown) ||
                Mathf.Abs(Input.GetJoyAxis(pad, JoyAxis.LeftY)) > StickDeadzone)
                return pad;
        }
        return -1;
    }

    private bool KeyDown(Key key) => Keyboard && Input.IsKeyPressed(key);

    // One resolution of this tick for every read below. The snapshot guarantee is per Poll, so two
    // reads in one frame cannot disagree the way two hardware reads could; the keyboard gate is
    // taken from the live field because a caller sets it after construction.
    private void ReadDevices()
    {
        if (_typingStale)
        {
            _typingKeys = new PlayerActions(TypingMap(_keys.Map), Keyboard);
            _typingStale = false;
        }

        _keys.ReadsKeyboard = Keyboard;
        _typingKeys.ReadsKeyboard = Keyboard;
        _keysOnly.ReadsKeyboard = Keyboard;
        _devices.Refresh();
        _padMuted.Refresh();
        _live = TextEntry ? _typingKeys : _keys;
        _live.Poll(_devices);
        _padOnly.Poll(_devices);
        _keysOnly.Poll(_padMuted);
    }

    private int RawPadDir() => Dir(_padOnly, InputAction.MenuUp, InputAction.MenuDown);

    private int RawPadDirX() => Dir(_padOnly, InputAction.MenuLeft, InputAction.MenuRight);

    private int RawDir() => Dir(_live, InputAction.MenuUp, InputAction.MenuDown);

    private int RawDirX() => Dir(_live, InputAction.MenuLeft, InputAction.MenuRight);

    private bool RawAccept() => _live.Held(InputAction.MenuAccept);

    private bool RawBack() => _live.Held(InputAction.MenuBack);

    private bool RawPadBack() => _padOnly.Held(InputAction.MenuBack);

    // Start stays pad-only through its bindings: the keyboard is always player 1, who is joined
    // from the start and has nothing to join.
    private bool RawStart() => _live.Held(InputAction.MenuStart);

    private bool RawLoadout() => _live.Held(InputAction.MenuLoadout);

    private bool RawPresets() => _live.Held(InputAction.MenuPresets);

}
