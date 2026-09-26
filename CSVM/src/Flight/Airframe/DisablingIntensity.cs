using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>The one intensity number a <c>SONIC</c> or <c>FLASH</c> burst produces, from the
/// original's <c>FUN_0042e840</c>. Both consumers read it: the player's screen wash takes the
/// intensity as its weight, the AI stun takes five times the intensity as its duration in seconds.
/// The two flags share this routine and differ only in the facing test (<c>FLASH</c> requires the
/// victim to be looking at the burst, <c>SONIC</c> does not) and in the wash colour, which is not
/// this module's business.
///
/// <para>⚠ Distances come in <b>squared</b>, because the engine's distance routine returns a square
/// and nothing on this path takes a root. Handing it plain distances moves the plateau edge from
/// 77% of the radius to 60% of it. Pure and Godot-<c>Node</c>-free so it unit-tests. Decode:
/// <c>docs/org/ordnanceTypes.md</c>, "SONIC and FLASH: an intensity, then a stun".</para></summary>
public static class DisablingIntensity
{
    /// <summary>Where the plateau ends, as a fraction of the squared radius: full strength while
    /// d² is under 0.6·R², which is 77% of R in distance.</summary>
    public const float PlateauRatio = 0.6f;

    /// <summary>Seconds of stun per unit of intensity, so a dead-centre hit stuns for 5 s.</summary>
    public const float StunSecondsPerIntensity = 5f;

    /// <summary>One burst against one victim, both radii squared. <c>requiresFacing</c> is the
    /// <c>FLASH</c> flag and <c>facingDot</c> (see <see cref="FacingDot"/>) is read only under it.
    /// False is the original's own "no effect" return (the facing test, or the fade consuming the
    /// intensity), and both outputs are then zero. Rounding leaves about 6e-8 at the radius rather
    /// than a clean zero, as the original's arithmetic does. <c>intensity</c> is the wash weight in
    /// (0, 1] and <c>stunSeconds</c> is five times it.</summary>
    public static bool TryResolve(float distanceSq, float impactProximitySq, bool requiresFacing,
        float facingDot, out float intensity, out float stunSeconds)
    {
        intensity = 0f;
        stunSeconds = 0f;
        if (impactProximitySq <= 0f)
            return false; // the original's divide gives an infinite ratio here, which fades to nothing

        float ratio = Mathf.Min(distanceSq / impactProximitySq, 1f);
        float fade = ratio < PlateauRatio ? 0f : (ratio - PlateauRatio) * 2.5f;
        float value = 1f - fade;
        if (value <= 0f)
            return false;

        if (requiresFacing)
        {
            if (facingDot < 0f)
                return false;
            // Not a halving: the original scales by twice the dot below 0.5, which meets the
            // unscaled value exactly at 0.5 and ramps to nothing at 0.
            if (facingDot < 0.5f)
                value *= facingDot * 2f;
            if (value <= 0f)
                return false;
        }

        intensity = value;
        stunSeconds = value * StunSecondsPerIntensity;
        return true;
    }

    /// <summary>The dot <see cref="TryResolve"/> wants, in the original's convention: the victim's
    /// unit forward axis against the unit direction from the victim toward the burst, so 1 is
    /// looking straight at it. Zero, which rejects the hit, when the two positions coincide and
    /// there is no direction to take.</summary>
    public static float FacingDot(Vector3 victimForward, Vector3 victimPosition, Vector3 burstPosition)
    {
        var toBurst = burstPosition - victimPosition;
        return toBurst.LengthSquared() <= 0f ? 0f : victimForward.Dot(toBurst.Normalized());
    }
}
