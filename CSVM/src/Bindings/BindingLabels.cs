using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace CSVM.Bindings;

/// <summary>What a rebinding screen prints: an action's name, a control's name, and one row of an
/// action's whole binding list. Separate from <see cref="BindingStore"/>'s tokens on purpose, since
/// a file is parsed back and a label is only read; <c>KpEnter</c> is the right token and the wrong
/// caption.
/// ⚠ A row states how many bindings it is not showing. The original ships four slots per action and
/// draws the first two non-empty (`FUN_00449fc0`, `docs/org/input.md`), so its screen hides
/// bindings silently and a player cannot tell a two-binding action from a four-binding one. Ours
/// holds a list, so a row that will not fit says how much of it is missing.</summary>
public static class BindingLabels
{
    /// <summary>What a row prints when the action is bound to nothing.</summary>
    public const string Unbound = "unbound";

    /// <summary>The caption prefix of a stick identity ("R", or the model when no profile names it),
    /// null for any other device. Registered by the stick side, which keeps this namespace free of
    /// it, as <see cref="LaunchBindings.StickRows"/> does; unset, a stick reads as a pad.</summary>
    public static Func<DeviceId, string?>? StickName { get; set; }

    /// <summary>An action's caption: the original's own keybind-page string where it binds that
    /// action. Otherwise it is the enum name with its words separated, so <c>FireRockets</c> reads as
    /// "Fire Rockets". The throttle lever has a caption of its own. The original's captions read under their category heading, which is why the
    /// targeting ones carry no "Target" prefix.</summary>
    public static string Name(InputAction action)
    {
        if (Original(action) is { } original)
            return original;
        // Parenthesised so it reads beside the original's "Throttle Up" and "Throttle 0/8" rows as
        // one more way to set the same lever.
        if (action == InputAction.ThrottleLever)
            return "Throttle (lever)";

        string name = action.ToString();
        var text = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                text.Append(' ');
            text.Append(name[i]);
        }

        return text.ToString();
    }

    /// <summary>One control's caption, device included, because two pads' identical buttons are two
    /// different bindings and a row that hid the device would read as a duplicate.</summary>
    public static string Describe(Binding binding) => Describe(binding, StickName);

    /// <summary><see cref="Describe(Binding)"/> with the stick names taken from
    /// <paramref name="stickName"/> instead of the registered <see cref="StickName"/>.</summary>
    public static string Describe(Binding binding, Func<DeviceId, string?>? stickName)
    {
        var c = binding.Control;
        if (stickName?.Invoke(binding.Device) is { } stick)
            return stick + " " + StickControl(c);
        return c.Kind switch
        {
            ControlKind.Key => BindingControl.Prefix(c.Modifiers) + KeyName((Key)c.Index),
            ControlKind.Button => "Pad " + Spaced(EnumName<JoyButton>(c.Index)),
            ControlKind.Axis => "Pad " + Spaced(EnumName<JoyAxis>(c.Index)) + (c.Sign < 0 ? " -" : " +"),
            ControlKind.Mouse => "Mouse " + Spaced(EnumName<MouseButton>(c.Index)),
            ControlKind.FullAxis => "Axis " + c.Index.ToString(CultureInfo.InvariantCulture) + (c.Inverted ? " inverted" : string.Empty),
            _ => "Hat " + c.Index.ToString(CultureInfo.InvariantCulture) + " " + c.Direction,
        };
    }

    /// <summary>An action's whole binding list as one row, showing at most
    /// <paramref name="maxShown"/> of them and naming the count it left out. The count is the point:
    /// a screen that simply truncated would reproduce the original's hidden slots.</summary>
    public static string Row(IReadOnlyList<Binding> bindings, int maxShown)
    {
        if (bindings is null || bindings.Count == 0)
            return Unbound;

        int shown = maxShown < 1 ? 1 : maxShown < bindings.Count ? maxShown : bindings.Count;
        var text = new StringBuilder();
        for (int i = 0; i < shown; i++)
        {
            if (i > 0)
                text.Append(", ");
            text.Append(Describe(bindings[i]));
        }

        int hidden = bindings.Count - shown;
        if (hidden > 0)
            text.Append(", +").Append(hidden.ToString(CultureInfo.InvariantCulture)).Append(" more");
        return text.ToString();
    }

    /// <summary>A list of actions in one clause, for the sentence that names what a steal takes the
    /// control from: "Fire Guns", "Fire Guns and Nitro", "Fire Guns, Nitro and Respawn".</summary>
    public static string Clause(IReadOnlyList<InputAction> actions)
    {
        if (actions is null || actions.Count == 0)
            return string.Empty;

        var text = new StringBuilder(Name(actions[0]));
        for (int i = 1; i < actions.Count; i++)
            text.Append(i == actions.Count - 1 ? " and " : ", ").Append(Name(actions[i]));
        return text.ToString();
    }

    // The original's own caption for an action it binds, quoted from its keybind pages
    // (`OriginalScreenshots/Keybinds *.png`, decoded in docs/org/input.md), or null for an action
    // this port added. Its targeting and weapon rows are deliberately unprefixed and lowercase where
    // the page is: the page is the record, not this file's taste.
    private static string? Original(InputAction action) => action switch
    {
        InputAction.PitchDown => "Point Nose Down",
        InputAction.PitchUp => "Point Nose Up",
        InputAction.RollLeft => "Roll Left",
        InputAction.RollRight => "Roll Right",
        InputAction.YawLeft => "Turn Left",
        InputAction.YawRight => "Turn Right",
        InputAction.ThrottleUp => "Throttle Up",
        InputAction.ThrottleDown => "Throttle Down",
        InputAction.FireGuns => "Fire Guns",
        InputAction.FireRockets => "Fire Rockets",
        InputAction.SelectGunGroup => "Cycle guns clockwise",
        InputAction.SelectGunGroupPrev => "Cycle guns counterclockwise",
        InputAction.SelectOrdnance => "Cycle rockets clockwise",
        InputAction.SelectOrdnancePrev => "Cycle rockets counterclockwise",
        InputAction.TargetNextEnemy => "Next Enemy/Objective",
        InputAction.TargetPreviousEnemy => "Previous Enemy/Objective",
        InputAction.TargetNearestEnemy => "Nearest Enemy/Objective",
        InputAction.TargetNextAlly => "Next Ally",
        InputAction.TargetPreviousAlly => "Previous Ally",
        InputAction.TargetNearestAlly => "Nearest Ally",
        InputAction.TargetNextNonAircraft => "Next Non-Aircraft",
        InputAction.TargetPreviousNonAircraft => "Previous Non-Aircraft",
        InputAction.TargetNearestNonAircraft => "Nearest Non-Aircraft",
        InputAction.TargetNearest => "Select Target Nearest Crosshairs",
        InputAction.TargetClear => "Target Nothing",
        InputAction.ToggleSpyglass => "Toggle Spyglass",
        InputAction.CycleCockpitViews => "Cycle Cockpit Views",
        InputAction.FlybyView => "Access Chase View",
        InputAction.LookCenter => "Look Forward",
        InputAction.LookBack => "Look Back",
        InputAction.SnapLookMode => "Access Snap Look Mode",
        InputAction.SmoothLookMode => "Access Smooth Look Mode",
        InputAction.TrackTarget => "Track Target",
        InputAction.Nitro => "Use Nitro-Booster",
        InputAction.AutoLand => "Auto-Dock",
        InputAction.Pause => "Pause/Quit/Objectives",
        _ => ThrottleFraction(action),
    };

    // A stick control by its raw index, the number the profile file and --dump-sticks print, since a
    // stick's controls have no names. Hat 0 is the only hat most sticks have, so it drops its index.
    private static string StickControl(BindingControl c)
    {
        string index = c.Index.ToString(CultureInfo.InvariantCulture);
        return c.Kind switch
        {
            ControlKind.Button => "Button " + index,
            ControlKind.Axis => "Axis " + index + (c.Sign < 0 ? " -" : " +"),
            ControlKind.FullAxis => "Axis " + index + (c.Inverted ? " inverted" : string.Empty),
            ControlKind.Hat => (c.Index == 0 ? "Hat " : "Hat " + index + " ") + c.Direction,
            _ => c.Kind + " " + index,
        };
    }

    // The nine absolute-throttle actions, captioned as the original's Throttle page spells them:
    // "Throttle 0/8" for idle through "Throttle 8/8" for full.
    private static string? ThrottleFraction(InputAction action)
    {
        if (action < InputAction.ThrottleSet0 || action > InputAction.ThrottleSet8)
            return null;
        int eighths = action - InputAction.ThrottleSet0;
        return "Throttle " + eighths.ToString(CultureInfo.InvariantCulture) + "/8";
    }

    // A key's own caption. The numpad, the digit row and the punctuation keys are named the way a
    // keycap is rather than the way the enum is, because "Kp Enter", "Key5" and "Quoteleft" are not
    // what the player is looking at.
    private static string KeyName(Key key) => key switch
    {
        Key.Quoteleft => "`",
        Key.Bracketleft => "[",
        Key.Bracketright => "]",
        Key.Semicolon => ";",
        Key.Apostrophe => "'",
        Key.Comma => ",",
        Key.Period => ".",
        Key.Slash => "/",
        Key.Backslash => "\\",
        Key.Minus => "-",
        Key.Equal => "=",
        Key.KpAdd => "Numpad +",
        Key.KpSubtract => "Numpad -",
        Key.KpMultiply => "Numpad *",
        Key.KpDivide => "Numpad /",
        Key.KpPeriod => "Numpad .",
        Key.KpEnter => "Numpad Enter",
        >= Key.Kp0 and <= Key.Kp9 => "Numpad " + ((int)(key - Key.Kp0)).ToString(CultureInfo.InvariantCulture),
        >= Key.Key0 and <= Key.Key9 => ((int)(key - Key.Key0)).ToString(CultureInfo.InvariantCulture),
        _ => Spaced(EnumName<Key>((int)key)),
    };

    private static string EnumName<T>(int index)
        where T : struct, System.Enum
    {
        object value = System.Enum.ToObject(typeof(T), index);
        return System.Enum.IsDefined(typeof(T), value)
            ? System.Enum.GetName(typeof(T), value)!
            : "#" + index.ToString(CultureInfo.InvariantCulture);
    }

    // An engine enum name split into words the way an action name is, so "LeftShoulder" and
    // "TriggerRight" read as captions rather than as identifiers.
    private static string Spaced(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                text.Append(' ');
            text.Append(name[i]);
        }

        return text.ToString();
    }
}
