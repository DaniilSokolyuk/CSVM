using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Session.Campaign;

/// <summary>Where the rope ladder is between the two authored definitions. The two transient
/// states hold until the running definition's own <c>CALLBACK</c> settles them, so the rule can
/// flip during a drop without cancelling it.</summary>
public enum LadderState
{
    Retracted,
    Deployed,
    Deploying,
    Retracting,
}

/// <summary>The original's rope-ladder switch, engine-free: every tick the player is flying and
/// no cutscene runs, the ladder is wanted when the aircraft is within 45 degrees of upright and
/// inside an active <c>pickups.zrd</c> sensor, and the switch starts <c>drop_ladder</c> or
/// <c>retract_ladder</c> to match, one transition at a time. A mission that authors neither
/// definition still flips the state silently. Decode: docs/org/ladderSwitch.md.</summary>
public sealed class LadderSwitch
{
    /// <summary>The attitude gate: the aircraft's up axis dotted with world up must exceed this,
    /// cos 45 degrees as the original's immediate. Bank and pitch both count against it.</summary>
    public const float LevelCosine = 0.707f;

    /// <summary>The authored <c>CALLBACK</c> value both ladder definitions raise, which is what
    /// moves the switch out of its transient state.</summary>
    public const int SettleCode = 123;

    public const string DropAnim = "drop_ladder";
    public const string RetractAnim = "retract_ladder";

    public LadderState State { get; private set; } = LadderState.Retracted;

    /// <summary>The attitude half of the rule, on a body-to-world basis whose Y column is the
    /// aircraft's own up.</summary>
    public static bool IsLevel(Basis attitude) => attitude.Y.Y > LevelCosine;

    /// <summary>The proximity half: inside the sensor's sphere, boundary included, the way the
    /// original compares squared distances.</summary>
    public static bool WithinSensor(Vector3 player, Vector3 sensor, float radius) =>
        sensor.DistanceSquaredTo(player) <= radius * radius;

    /// <summary>Which human holds the switch, given who held it last: the incumbent keeps it for as
    /// long as they qualify, and only once they stop is the field asked, first qualifier in player
    /// order winning. Null when nobody qualifies, which is what retracts the ladder. ⚠ A single
    /// owner rather than "any human qualifies", because both definitions hold a transient state
    /// until their own <c>CALLBACK</c> settles it and an "any" rule thrashes the drop against the
    /// retract as two humans drift through one sensor.</summary>
    public static T? Holder<T>(T? held, IReadOnlyList<T> humans, Func<T, bool> qualifies)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(humans);
        ArgumentNullException.ThrowIfNull(qualifies);
        if (held != null && qualifies(held))
        {
            return held;
        }

        foreach (var human in humans)
        {
            if (qualifies(human))
            {
                return human;
            }
        }

        return null;
    }

    /// <summary>One evaluation. <paramref name="startAnimation"/> starts the named definition and
    /// reports whether one existed; without one the state flips at once, which is the original's
    /// null-definition branch. Returns the definition started, or null.</summary>
    public string? Step(bool wanted, Func<string, bool> startAnimation)
    {
        if (wanted)
        {
            if (State != LadderState.Retracted)
            {
                return null;
            }
            if (!startAnimation(DropAnim))
            {
                State = LadderState.Deployed;
                return null;
            }
            State = LadderState.Deploying;
            return DropAnim;
        }

        if (State != LadderState.Deployed)
        {
            return null;
        }
        if (!startAnimation(RetractAnim))
        {
            State = LadderState.Retracted;
            return null;
        }
        State = LadderState.Retracting;
        return RetractAnim;
    }

    /// <summary>The <c>CALLBACK</c> host: a settle code raised by either ladder definition lands
    /// the switch in that definition's end state, whatever it was in. Any other code or
    /// definition is declined so the next host can answer it.</summary>
    public bool Settle(int code, string? animName)
    {
        if (code != SettleCode)
        {
            return false;
        }
        if (string.Equals(animName, DropAnim, StringComparison.OrdinalIgnoreCase))
        {
            State = LadderState.Deployed;
            return true;
        }
        if (string.Equals(animName, RetractAnim, StringComparison.OrdinalIgnoreCase))
        {
            State = LadderState.Retracted;
            return true;
        }
        return false;
    }

    /// <summary>Back to the load-time state, for a re-bind against a rebuilt world.</summary>
    public void Reset() => State = LadderState.Retracted;
}
