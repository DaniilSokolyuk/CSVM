using System;

namespace CSVM.Bindings;

/// <summary>When an absolute throttle lever drives the commanded setting, and when the rate keys,
/// the digit row or a schedule do instead. A lever holds its position when nobody touches it. Read
/// every tick, it would pin the throttle there and make every other command dead. The lever therefore takes over only when it moves, and holds the setting until another command
/// arrives while it is still. One per aircraft, stepped once per sim tick.</summary>
public sealed class LeverTakeover
{
    /// <summary>How far the lever position (0 to 1) has to move before it takes the throttle back.
    /// A sixth of one digit's eighth, above a cheap lever's jitter and below any deliberate move. TUNE.
    /// </summary>
    public const float Epsilon = 0.02f;

    private float _anchor;
    private bool _seeded;

    /// <summary>Whether the lever holds the commanded setting right now.</summary>
    public bool Engaged { get; private set; }

    /// <summary>The setting the lever commands this tick, or null while it does not. The first reading
    /// after <see cref="Release"/> only records where the lever stands. An untouched lever therefore
    /// never moves a throttle placed some other way. A move past
    /// <see cref="Epsilon"/> engages it even in a tick that also carries
    /// <paramref name="otherCommand"/>; another command with the lever still disengages it.</summary>
    public float? Step(float position, bool otherCommand)
    {
        if (!_seeded)
        {
            _seeded = true;
            _anchor = position;
            return null;
        }

        if (Math.Abs(position - _anchor) > Epsilon)
            Engaged = true;
        else if (otherCommand)
            Engaged = false;

        if (!Engaged)
            return null;
        _anchor = position;
        return position;
    }

    /// <summary>Forgets the lever. A writer that places the throttle calls it (a spawn, a respawn, a
    /// launch), as does a tick the lever cannot be read in. The next reading seeds.</summary>
    public void Release()
    {
        _seeded = false;
        Engaged = false;
    }
}
