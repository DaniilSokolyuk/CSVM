using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CSVM.Bindings;

/// <summary>The keymap the game ships with, as data: one <see cref="ActionMap"/> per
/// <see cref="InputContext"/>, reproducing `docs/controls.md` binding for binding. Every load falls
/// back to it per action, so an unreadable file costs the player only the actions it named.
/// ⚠ No default is a hat binding, and nothing may author one on a pad. Godot reports a d-pad as
/// four buttons (`GodotDeviceState`). A hat and a d-pad button are one control read as two, so two
/// actions could hold it at once.
/// ⚠ A pad default names <see cref="AnyPad"/>, not a real pad. <see cref="MapFor"/> substitutes the
/// seat's own identity, because the shipped set cannot know a hardware string.</summary>
public static class DefaultBindings
{
    /// <summary>The stand-in device a shipped pad default is authored on, meaning "this seat's
    /// pad". No hardware reports it, so a map still holding it after <see cref="MapFor"/> resolves
    /// to nothing rather than to somebody else's pad.</summary>
    public static readonly DeviceId AnyPad = DeviceId.Joypad("*");

    // Menu navigation counts a stick deflection past this as a d-pad press (MenuInput), the
    // spectator camera gates its sticks here (SpectatorCamera), and a trigger driving a digital
    // action wants half its travel. The three numbers are the polling sites' own, kept so the
    // migration changes no behaviour; they are TUNE from here on.
    private const float MenuStickDeadzone = 0.5f;
    private const float CameraStickDeadzone = 0.18f;
    private const float TriggerThreshold = 0.5f;

    private static readonly ContextBuilder FlightDefaults = BuildFlight();
    private static readonly ContextBuilder MenuDefaults = BuildMenu();
    private static readonly ContextBuilder CameraDefaults = BuildCamera();

    /// <summary>The actions the shipped set deliberately leaves unbound, which is the list that
    /// keeps a migrating site from discovering a hole one call at a time.</summary>
    public static IReadOnlyList<InputAction> Unbound { get; } = new[]
    {
        InputAction.SelectChaseView,
        InputAction.MenuJoin,
        InputAction.ThrottleLever,
        InputAction.SkipCutscene,
    };

    /// <summary>Which context owns that action. Every member of <see cref="InputAction"/> has
    /// exactly one, bound or not.</summary>
    public static InputContext ContextOf(InputAction action)
    {
        if (FlightDefaults.Actions.Contains(action))
            return InputContext.Flight;
        if (MenuDefaults.Actions.Contains(action))
            return InputContext.Menu;
        if (CameraDefaults.Actions.Contains(action))
            return InputContext.Camera;
        throw new System.ArgumentOutOfRangeException(
            nameof(action), action, "Every action belongs to one context; add the new one here.");
    }

    /// <summary>Every action that context owns, in enum order, including the ones it leaves
    /// unbound. A saved file lists all of them so an unbind survives a reload.</summary>
    public static IReadOnlyList<InputAction> ActionsIn(InputContext context) => Builder(context).Actions;

    /// <summary>A fresh default map for that context, with <paramref name="pad"/> put in place of
    /// <see cref="AnyPad"/>. Pass <c>default</c> for a seat with no pad: the pad rows stay in the
    /// map and resolve to nothing, so plugging one in later is a re-materialisation and not a
    /// rebuild of the player's keymap.</summary>
    public static ActionMap MapFor(InputContext context, DeviceId pad)
    {
        var template = Builder(context).Map;
        var map = new ActionMap();
        foreach (var action in template.BoundActions)
        {
            foreach (var binding in template.Bindings(action))
            {
                map.Add(action, Retarget(binding, pad));
            }
        }

        return map;
    }

    /// <summary>The same substitution one binding at a time, for a loader turning a stored
    /// <see cref="AnyPad"/> row into this seat's pad.</summary>
    public static Binding Retarget(Binding binding, DeviceId pad) =>
        binding.Device == AnyPad && pad.Kind == DeviceKind.Joypad ? binding with { Device = pad } : binding;

    private static ContextBuilder Builder(InputContext context) => context switch
    {
        InputContext.Flight => FlightDefaults,
        InputContext.Menu => MenuDefaults,
        _ => CameraDefaults,
    };

    // Flight, the original's own shipped table (docs/org/input.md's 64 rows, docs/controls.md's
    // flight table). The attitude signs follow FlightController's key pairs: Down pitches up, Left
    // rolls left, the comma and the left shoulder yaw left. The numpad snap-look diagonals each
    // drive two actions at once, which the OR rule expresses and four typed slots cannot.
    private static ContextBuilder BuildFlight()
    {
        var b = new ContextBuilder();
        b.Keys(InputAction.PitchUp, Key.Down).Stick(InputAction.PitchUp, JoyAxis.LeftY, 1, 0f);
        b.Keys(InputAction.PitchDown, Key.Up).Stick(InputAction.PitchDown, JoyAxis.LeftY, -1, 0f);
        b.Keys(InputAction.RollLeft, Key.Left).Stick(InputAction.RollLeft, JoyAxis.LeftX, -1, 0f);
        b.Keys(InputAction.RollRight, Key.Right).Stick(InputAction.RollRight, JoyAxis.LeftX, 1, 0f);
        b.Keys(InputAction.YawLeft, Key.Comma).Buttons(InputAction.YawLeft, JoyButton.LeftShoulder);
        b.Keys(InputAction.YawRight, Key.Period).Buttons(InputAction.YawRight, JoyButton.RightShoulder);
        b.Keys(InputAction.ThrottleUp, Key.Equal).Stick(InputAction.ThrottleUp, JoyAxis.TriggerRight, 1, 0f);
        b.Keys(InputAction.ThrottleDown, Key.Minus).Stick(InputAction.ThrottleDown, JoyAxis.TriggerLeft, 1, 0f);

        // The nine eighths on the digit row, 1 for idle through 9 for full, which is why no targeting
        // action may take a digit. The pad has no counterpart: its trigger pair is the whole lever.
        for (int eighths = 0; eighths <= 8; eighths++)
        {
            b.Keys(InputAction.ThrottleSet0 + eighths, (Key)((int)Key.Key1 + eighths));
        }

        // The absolute lever ships unbound, since neither a keyboard nor a pad has a lever. A pad
        // stick bound here would drop to half throttle whenever its spring centred it.
        b.Leave(InputAction.ThrottleLever);

        // The two weapons sit under the mouse hand as well, the left button firing the guns and the
        // right the rockets, which is where a pilot flying with the mouse reaches for them. This
        // port's own rows: the original binds no mouse button to anything.
        b.Keys(InputAction.FireGuns, Key.Space).Buttons(InputAction.FireGuns, JoyButton.B)
            .Mouse(InputAction.FireGuns, MouseButton.Left);
        b.Keys(InputAction.FireRockets, Key.X).Buttons(InputAction.FireRockets, JoyButton.A)
            .Mouse(InputAction.FireRockets, MouseButton.Right);
        b.Keys(InputAction.SelectGunGroup, Key.F3).Buttons(InputAction.SelectGunGroup, JoyButton.DpadRight);
        b.Keys(InputAction.SelectOrdnance, Key.F5).Buttons(InputAction.SelectOrdnance, JoyButton.DpadLeft);

        // The four selector keys are the original's own, by the name its keybind page displays
        // (docs/controls.md, docs/org/input.md). The backward pair ships on no pad control: the
        // pad reaches it by HOLDING the forward button, which FireControl's tap/hold split reads.
        b.Keys(InputAction.SelectGunGroupPrev, Key.F4);
        b.Keys(InputAction.SelectOrdnancePrev, Key.F6);
        b.Keys(InputAction.Nitro, Key.N).Buttons(InputAction.Nitro, JoyButton.X);

        // Respawn is this port's own action and the original binds nothing to Backspace, so it sits
        // out of the flying hand's reach: it throws the airframe away and must not be brushed in a
        // manoeuvre. The original's Ctrl+X stays free for the bail-out this port does not have.
        b.Keys(InputAction.Respawn, Key.Backspace).Buttons(InputAction.Respawn, JoyButton.Y);
        b.Keys(InputAction.AutoLand, Key.A).Buttons(InputAction.AutoLand, JoyButton.LeftStick);
        b.Keys(InputAction.Pause, Key.Escape).Buttons(InputAction.Pause, JoyButton.Start);

        // The eleven targeting actions, the original's own run across Q W E R T with Shift stepping
        // a cycle back and Ctrl restarting it at its head. Only the three Next actions reach a pad
        // control, as the original gives only two of the eleven a joystick button.
        b.Keys(InputAction.TargetNextEnemy, Key.E).Buttons(InputAction.TargetNextEnemy, JoyButton.DpadUp);
        b.Mod(InputAction.TargetPreviousEnemy, KeyModifiers.Shift, Key.E);
        b.Mod(InputAction.TargetNearestEnemy, KeyModifiers.Ctrl, Key.E);
        b.Keys(InputAction.TargetNextAlly, Key.W);
        b.Mod(InputAction.TargetPreviousAlly, KeyModifiers.Shift, Key.W);
        b.Mod(InputAction.TargetNearestAlly, KeyModifiers.Ctrl, Key.W);
        b.Keys(InputAction.TargetNextNonAircraft, Key.R);
        b.Mod(InputAction.TargetPreviousNonAircraft, KeyModifiers.Shift, Key.R);
        b.Mod(InputAction.TargetNearestNonAircraft, KeyModifiers.Ctrl, Key.R);
        b.Keys(InputAction.TargetNearest, Key.Q);
        b.Keys(InputAction.TargetClear, Key.T);
        b.Keys(InputAction.CycleCockpitViews, Key.F8).Buttons(InputAction.CycleCockpitViews, JoyButton.DpadDown);

        // Selecting the chase view without walking the cycle is this port's own action, and it
        // ships unbound. The cycle on F8 reaches that view already, so a default here would spend a
        // control on a second way in. It stays rebindable, which is what Leave keeps.
        b.Leave(InputAction.SelectChaseView);
        // F7 is the original's own key for this camera ("Access Chase View", which its own decode
        // settles as the flyby rather than the following chase view); the pad has no spare button.
        b.Keys(InputAction.FlybyView, Key.F7);

        // The spyglass on the original's own Shift+S, now that a binding carries the modifier; Misc1
        // is the pad's one free control, and the original spends a joystick button on this too.
        b.Mod(InputAction.ToggleSpyglass, KeyModifiers.Shift, Key.S)
            .Buttons(InputAction.ToggleSpyglass, JoyButton.Misc1);
        b.Keys(InputAction.LookUp, Key.Kp7, Key.Kp8, Key.Kp9);
        b.Keys(InputAction.LookDown, Key.Kp1, Key.Kp2, Key.Kp3);
        b.Keys(InputAction.LookLeft, Key.Kp7, Key.Kp4, Key.Kp1);
        b.Keys(InputAction.LookRight, Key.Kp9, Key.Kp6, Key.Kp3);
        b.Keys(InputAction.LookCenter, Key.Kp5);
        b.Keys(InputAction.LookBack, Key.Kp0).Buttons(InputAction.LookBack, JoyButton.RightStick);
        b.Stick(InputAction.LookAimUp, JoyAxis.RightY, -1, 0f);
        b.Stick(InputAction.LookAimDown, JoyAxis.RightY, 1, 0f);
        b.Stick(InputAction.LookAimLeft, JoyAxis.RightX, -1, 0f);
        b.Stick(InputAction.LookAimRight, JoyAxis.RightX, 1, 0f);

        // The first-person free look is the held middle mouse button (docs/controls.md), the guns
        // and the rockets taking the two beside it. The freecam's own look posture stays on the
        // right button, read as an event rather than through this table (SpectatorCamera).
        b.Mouse(InputAction.FreeLook, MouseButton.Middle);

        // The three look-mode selectors on the original's own keys, which its keybind page calls
        // Access Snap Look Mode, Track Target and Access Smooth Look Mode. Keyboard only, as the
        // original has them: none reaches a joystick button there and the pad has no free control.
        b.Keys(InputAction.SnapLookMode, Key.K);
        b.Keys(InputAction.TrackTarget, Key.L);
        b.Keys(InputAction.SmoothLookMode, Key.J);
        return b;
    }

    // Menus and boards, from docs/controls.md's "Any mode" table and MenuInput's own reads. The
    // WASD and stick rows are the aliases that file records beside the arrows and the d-pad.
    private static ContextBuilder BuildMenu()
    {
        var b = new ContextBuilder();
        b.Keys(InputAction.MenuUp, Key.Up, Key.W).Buttons(InputAction.MenuUp, JoyButton.DpadUp)
            .Stick(InputAction.MenuUp, JoyAxis.LeftY, -1, MenuStickDeadzone);
        b.Keys(InputAction.MenuDown, Key.Down, Key.S).Buttons(InputAction.MenuDown, JoyButton.DpadDown)
            .Stick(InputAction.MenuDown, JoyAxis.LeftY, 1, MenuStickDeadzone);
        b.Keys(InputAction.MenuLeft, Key.Left, Key.A).Buttons(InputAction.MenuLeft, JoyButton.DpadLeft)
            .Stick(InputAction.MenuLeft, JoyAxis.LeftX, -1, MenuStickDeadzone);
        b.Keys(InputAction.MenuRight, Key.Right, Key.D).Buttons(InputAction.MenuRight, JoyButton.DpadRight)
            .Stick(InputAction.MenuRight, JoyAxis.LeftX, 1, MenuStickDeadzone);
        b.Keys(InputAction.MenuAccept, Key.Enter, Key.KpEnter, Key.Space)
            .Buttons(InputAction.MenuAccept, JoyButton.A);
        b.Keys(InputAction.MenuBack, Key.Escape).Buttons(InputAction.MenuBack, JoyButton.B);
        b.Buttons(InputAction.MenuStart, JoyButton.Start);
        b.Keys(InputAction.MenuLoadout, Key.L).Buttons(InputAction.MenuLoadout, JoyButton.Y);
        b.Keys(InputAction.MenuPresets, Key.P).Buttons(InputAction.MenuPresets, JoyButton.X);

        // Joining is any control on a pad no seat owns yet, which is a gesture rather than a
        // binding: every control it watches already belongs to another menu action here.
        b.Leave(InputAction.MenuJoin);

        // The skip ships unbound on the keyboard and the pad, because any key and any pad button
        // already skips a cutscene. A stick has no such reading, so its default is a stick row.
        b.Leave(InputAction.SkipCutscene);
        return b;
    }

    // The spectator and anim-lab camera, from docs/controls.md's freecam table and
    // SpectatorCamera's own key pairs: E and U rise, Q and Z descend, and the IJKL look cluster
    // pitches the way the right stick does (I down, K up).
    private static ContextBuilder BuildCamera()
    {
        var b = new ContextBuilder();
        b.Keys(InputAction.CameraForward, Key.W, Key.Up)
            .Stick(InputAction.CameraForward, JoyAxis.LeftY, -1, CameraStickDeadzone);
        b.Keys(InputAction.CameraBack, Key.S, Key.Down)
            .Stick(InputAction.CameraBack, JoyAxis.LeftY, 1, CameraStickDeadzone);
        b.Keys(InputAction.CameraLeft, Key.A, Key.Left)
            .Stick(InputAction.CameraLeft, JoyAxis.LeftX, -1, CameraStickDeadzone);
        b.Keys(InputAction.CameraRight, Key.D, Key.Right)
            .Stick(InputAction.CameraRight, JoyAxis.LeftX, 1, CameraStickDeadzone);
        b.Keys(InputAction.CameraUp, Key.E, Key.U).Buttons(InputAction.CameraUp, JoyButton.RightShoulder);
        b.Keys(InputAction.CameraDown, Key.Q, Key.Z).Buttons(InputAction.CameraDown, JoyButton.LeftShoulder);
        b.Keys(InputAction.CameraBoost, Key.Shift)
            .Stick(InputAction.CameraBoost, JoyAxis.TriggerRight, 1, TriggerThreshold);
        b.Keys(InputAction.CameraSlow, Key.Ctrl)
            .Stick(InputAction.CameraSlow, JoyAxis.TriggerLeft, 1, TriggerThreshold);
        b.Keys(InputAction.CameraLockTarget, Key.F).Buttons(InputAction.CameraLockTarget, JoyButton.X);
        b.Keys(InputAction.CameraLookUp, Key.K)
            .Stick(InputAction.CameraLookUp, JoyAxis.RightY, -1, CameraStickDeadzone);
        b.Keys(InputAction.CameraLookDown, Key.I)
            .Stick(InputAction.CameraLookDown, JoyAxis.RightY, 1, CameraStickDeadzone);
        b.Keys(InputAction.CameraLookLeft, Key.J)
            .Stick(InputAction.CameraLookLeft, JoyAxis.RightX, -1, CameraStickDeadzone);
        b.Keys(InputAction.CameraLookRight, Key.L)
            .Stick(InputAction.CameraLookRight, JoyAxis.RightX, 1, CameraStickDeadzone);

        // The dolly shares the trigger pair with boost and slow at its own deadzone of zero, because
        // it is a rate the whole travel feeds while boost is a switch that wants half of it. The two
        // readings are separate actions rather than one, so neither has to carry the other's number.
        b.Stick(InputAction.CameraDollyOut, JoyAxis.TriggerRight, 1, 0f);
        b.Stick(InputAction.CameraDollyIn, JoyAxis.TriggerLeft, 1, 0f);
        return b;
    }

    // One context's shipped set while it is being written: the map, and the actions it owns in the
    // order they were first named. Bindings go in through ActionMap.Add rather than Assign because
    // some controls are deliberately on two actions (a snap-look diagonal, d-pad up in flight),
    // which the steal rule would undo.
    private sealed class ContextBuilder
    {
        private readonly List<InputAction> _actions = new();

        public ActionMap Map { get; } = new();

        public IReadOnlyList<InputAction> Actions => _actions;

        public ContextBuilder Keys(InputAction action, params Key[] keys)
        {
            Track(action);
            foreach (var key in keys)
            {
                Map.Add(action, new Binding(DeviceId.Keyboard, BindingControl.Key((int)key)));
            }

            return this;
        }

        // One key under modifiers, the original's Shift and Ctrl forms. Its own method rather than a
        // parameter on Keys, because every other row is bare and would carry a None argument.
        public ContextBuilder Mod(InputAction action, KeyModifiers modifiers, Key key)
        {
            Track(action);
            Map.Add(action, new Binding(DeviceId.Keyboard, BindingControl.Key((int)key, modifiers)));
            return this;
        }

        public ContextBuilder Buttons(InputAction action, params JoyButton[] buttons)
        {
            Track(action);
            foreach (var button in buttons)
            {
                Map.Add(action, new Binding(AnyPad, BindingControl.Button((int)button)));
            }

            return this;
        }

        public ContextBuilder Stick(InputAction action, JoyAxis axis, int sign, float deadzone)
        {
            Track(action);
            Map.Add(action, new Binding(AnyPad, BindingControl.Axis((int)axis, sign, deadzone)));
            return this;
        }

        public ContextBuilder Mouse(InputAction action, MouseButton button)
        {
            Track(action);
            Map.Add(action, new Binding(DeviceId.Mouse, BindingControl.Mouse((int)button)));
            return this;
        }

        public ContextBuilder Leave(InputAction action)
        {
            Track(action);
            return this;
        }

        private void Track(InputAction action)
        {
            if (!_actions.Contains(action))
            {
                _actions.Add(action);
            }
        }
    }
}
