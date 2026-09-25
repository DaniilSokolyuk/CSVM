using System;
using System.Collections.Generic;
using CSVM.Bindings;

namespace CSVM.Sticks;

/// <summary>
/// The bindings a stick gets before anyone binds it: a synthetic profile for the one connected,
/// stick-shaped model no profile file covers. It flies X roll, Y pitch, Rz yaw from six axes up, and
/// Z as the absolute Throttle (lever). Buttons 0 and 1 fire; in menus the hat moves, button 0
/// confirms and button 1 backs out. It lives in memory until a screen save changes it.
/// ⚠ Do not relax the exactly-one rule. Axis numbers on an unknown stick are a convention, and two
/// candidates leave no way to say which flies (<c>docs/org/input.md</c>, generic stick default).
/// </summary>
public static class GenericStickDefault
{
    /// <summary>The name the synthetic file carries in the profile log and on the active set. It is
    /// never a file on disk, so it cannot collide with a saved one.</summary>
    public const string FileName = "(generic default)";

    /// <summary>X, the roll axis.</summary>
    public const int RollAxis = 0;

    /// <summary>Y, the pitch axis. Pulled back reads positive, as a pad's left stick does.</summary>
    public const int PitchAxis = 1;

    /// <summary>Z, the throttle lever, in DirectInput's usual X, Y, Z, Rx, Ry, Rz order. Both VKB
    /// sticks carry a throttle here.</summary>
    public const int LeverAxis = 2;

    /// <summary>Rz, the twist. On the VKB Gladiator EVO R it reads -1..1 here under SDL2, negative
    /// twisted right, so the default binds it inverted.</summary>
    public const int TwistAxis = 5;

    /// <summary>The fewest axes a device has before axis 5 is read as its twist.</summary>
    public const int TwistMinAxes = 6;

    /// <summary>The deadzone each flight axis is stamped with, the same the capture stamps.</summary>
    public const float AxisDeadzone = 0.02f;

    /// <summary>The lever's deadzone, which trims both ends of its travel (TUNE).</summary>
    public const float LeverDeadzone = 0.02f;

    /// <summary>The one model the default claims, or null. The candidates are the connected models
    /// without an active profile file (an ignored one counts as a file). While any candidate is
    /// still unsettled nothing is claimed, so a second stick cannot lose the claim a moment later.
    /// Exactly one stick-shaped candidate is claimed; none or several claim nothing.</summary>
    public static StickModel? Pick(IEnumerable<StickModel> unprofiled, Func<StickModel, StickShape> shapeOf)
    {
        ArgumentNullException.ThrowIfNull(unprofiled);
        ArgumentNullException.ThrowIfNull(shapeOf);
        StickModel? chosen = null;
        int sticks = 0;
        foreach (var model in unprofiled)
        {
            switch (shapeOf(model).Fit)
            {
                case StickFit.Unsettled:
                    return null;
                case StickFit.Stick:
                    chosen = model;
                    sticks++;
                    break;
            }
        }

        return sticks == 1 ? chosen : null;
    }

    /// <summary>The default's rows for <paramref name="model"/>, a device with
    /// <paramref name="axes"/> axes.</summary>
    public static StickProfile For(StickModel model, int axes)
    {
        var profile = new StickProfile(model);
        var device = model.Device;
        var flight = profile.Map(InputContext.Flight);
        flight.Add(InputAction.RollRight, new Binding(device, BindingControl.FullAxis(RollAxis, false, AxisDeadzone)));
        flight.Add(InputAction.PitchUp, new Binding(device, BindingControl.FullAxis(PitchAxis, false, AxisDeadzone)));
        if (axes >= TwistMinAxes)
        {
            flight.Add(InputAction.YawRight, new Binding(device, BindingControl.FullAxis(TwistAxis, true, AxisDeadzone)));
        }

        // Inverted: a DirectInput throttle reads its low end pushed forward (the VKB R does), which
        // is full here. The takeover rule keeps a stick that differs from moving a parked lever.
        flight.Add(InputAction.ThrottleLever, new Binding(device, BindingControl.FullAxis(LeverAxis, true, LeverDeadzone)));
        flight.Add(InputAction.FireGuns, new Binding(device, BindingControl.Button(0)));
        flight.Add(InputAction.FireRockets, new Binding(device, BindingControl.Button(1)));

        var menu = profile.Map(InputContext.Menu);
        menu.Add(InputAction.MenuUp, new Binding(device, BindingControl.Hat(0, HatDirection.Up)));
        menu.Add(InputAction.MenuDown, new Binding(device, BindingControl.Hat(0, HatDirection.Down)));
        menu.Add(InputAction.MenuLeft, new Binding(device, BindingControl.Hat(0, HatDirection.Left)));
        menu.Add(InputAction.MenuRight, new Binding(device, BindingControl.Hat(0, HatDirection.Right)));
        menu.Add(InputAction.MenuAccept, new Binding(device, BindingControl.Button(0)));
        menu.Add(InputAction.MenuBack, new Binding(device, BindingControl.Button(1)));
        return profile;
    }
}
