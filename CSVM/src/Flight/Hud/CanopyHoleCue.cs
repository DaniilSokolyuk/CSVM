using System;

namespace CSVM.Flight.Hud;

/// <summary>
/// The decoded canopy-glass cadence (<c>FUN_004b1210</c>): an interval that closed with at least
/// one gun hit on the pilot's own aeroplane may open one of the five <c>bullethole_anims</c> holes,
/// whose def is what sounds <c>window_hit_sg</c>. A hole opens once per sortie. The interval and
/// the hit count belong to <see cref="WarningShotCue"/>, the same block's tick, which is where the
/// original keeps them and why this rule owns no clock. Engine-free so it unit-tests without a
/// live node.
/// Decode: <c>docs/org/weaponFire.md</c>, "The incoming-fire cues".
/// </summary>
public sealed class CanopyHoleCue
{
    /// <summary>The glass cue the five hole defs sound. Not a player.json field: the name is
    /// authored in the defs themselves (<c>cockpit_bulletholes.zrd</c>), unlike the ricochet's
    /// <c>bullet_hit_sound</c>.</summary>
    public const string WindowHitSound = "window_hit_sg";

    /// <summary>The gamez node the hole defs pose AT_NODE, the view camera the cutscene definitions
    /// pose as well. A human rig stands its own copy up under this name
    /// (<see cref="Airframe.FlightController.EnsureViewCameraProxy"/>).</summary>
    public const string ViewCameraNode = "camera1";

    /// <summary>Holes a player def's <c>bullethole_anims</c> names, <c>bullet1</c>..<c>bullet5</c>,
    /// the same five on every player def.</summary>
    public const int HoleCount = 5;

    /// <summary>The chance an eligible interval opens a hole. Read, not chosen: the original draws
    /// <c>rand()/32768</c> and goes on only above 0.7 (<c>0x004b12ae</c>).</summary>
    public const float HoleChance = 0.3f;

    private readonly bool[] _opened = new bool[HoleCount];

    /// <summary>Holes still closed. The gate compares the airframe's health fraction against this
    /// share, so a pristine canopy needs damage before its first hole and each further one needs
    /// more.</summary>
    public int ClosedCount
    {
        get
        {
            int closed = 0;
            for (int i = 0; i < _opened.Length; i++)
                if (!_opened[i])
                    closed++;
            return closed;
        }
    }

    /// <summary>An interval closed with at least one gun hit: answers with the hole number (1..5)
    /// it opened, or null. The caller passes the whole-vehicle health fraction and the draw stream,
    /// so the rule itself owns no clock and no randomness. However many rounds the interval took,
    /// it opens at most one hole.</summary>
    public int? TryOpenHole(float healthFraction, Random rng)
    {
        int closed = ClosedCount;
        if (closed == 0 || healthFraction >= (float)closed / HoleCount)
            return null;
        if (rng.NextDouble() <= 1.0 - HoleChance)
            return null;
        // The span is the closed count LESS ONE (the DEC at 0x004b12c5), so the last closed hole is
        // reached only on a maximal draw. Reproduced rather than corrected: it is the shipped bias.
        int pick = (int)(rng.NextDouble() * (closed - 1));
        int seen = 0;
        for (int i = 0; i < _opened.Length; i++)
        {
            if (_opened[i])
                continue;
            if (seen == pick)
            {
                _opened[i] = true;
                return i + 1;
            }
            seen++;
        }
        return null;
    }

    /// <summary>Opens a numbered hole outright, past the health gate and the draw, and answers
    /// whether it was still closed. What <c>--canopy-holes=</c> spends so a scripted shot catches
    /// struck glass; a round that lands goes through <see cref="TryOpenHole"/> and nothing else.</summary>
    public bool ForceOpen(int hole)
    {
        if (hole < 1 || hole > HoleCount || _opened[hole - 1])
            return false;
        _opened[hole - 1] = true;
        return true;
    }

    /// <summary>Back to a pristine canopy, which is what the <c>reset_bulletholes</c> spawn anim
    /// does for the decals.</summary>
    public void Reset() => Array.Clear(_opened, 0, _opened.Length);
}
