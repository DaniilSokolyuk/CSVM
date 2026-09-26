namespace CSVM.Flight.Weapons;

/// <summary>The choker's engine-dead duration, from the original's <c>TANGLER</c> branch in
/// <c>FUN_004b9bc0</c>: <c>duration = ENGINE_DEAD_max × (1 − d² / RADIUS)</c>, floored at
/// <c>ENGINE_DEAD_min</c>. The victim-side effect is <c>FlightController.TryChokeEngine</c>, which
/// takes these seconds; this module knows nothing about aircraft.
///
/// <para>⚠ The units do not match, and reproducing that is the point (the plan's Decision 7): the
/// numerator is a <b>squared</b> distance and <c>RADIUS</c> is the <b>raw</b> authored value, since
/// <c>FUN_004ba6f0</c> stores it unsquared where the other parser squares <c>IMPACT_PROXIMITY</c>.
/// With <c>wep_12</c>'s <c>RADIUS [35]</c> the term reaches the floor at √21.5 ≈ 4.6 m. Do not
/// square the radius to "fix" it. Decode: <c>docs/org/ordnanceTypes.md</c>, "The choker,
/// settled".</para></summary>
public static class TanglerChoke
{
    /// <summary>The <c>ENGINE_DEAD</c> pair the static image ships, before any <c>TANGLER</c>
    /// weapon is parsed. Reached only by a catalogue authoring no <c>TANGLER</c> at all.</summary>
    public const float ImageEngineDeadMin = 2f;
    public const float ImageEngineDeadMax = 10f;

    /// <summary>Seconds the engine stays dead for one choker hit. <paramref name="distanceSq"/> is
    /// squared and <paramref name="radiusRaw"/> is not, which is the original's own mismatch. The
    /// floor applies at every distance, so this never returns less than
    /// <paramref name="engineDeadMin"/>; whether a hit happens at all is the fuse's business, not
    /// this formula's. A radius of zero would divide by zero in the original, whose default of 10.0
    /// keeps it unreachable; here it takes the floor.</summary>
    public static float Duration(float distanceSq, float radiusRaw, float engineDeadMin, float engineDeadMax)
    {
        if (radiusRaw <= 0f)
            return engineDeadMin;
        float seconds = (1f - distanceSq / radiusRaw) * engineDeadMax;
        return seconds <= engineDeadMin ? engineDeadMin : seconds;
    }

    /// <summary>The <c>ENGINE_DEAD</c> bounds in force for the whole install. They are a pair of
    /// globals (<c>DAT_0062b120</c>/<c>DAT_0062b124</c>) written by every <c>TANGLER</c> parse, so
    /// the LAST entry carrying one wins for every choker; this install authors exactly one
    /// (<c>wep_12</c>, <c>[5, 13]</c>), which makes the rule invisible in play and worth stating
    /// here rather than reading a weapon's own field at the hit site.</summary>
    public static (float Min, float Max) EngineDeadBounds(WeaponDefs defs)
    {
        var bounds = (Min: ImageEngineDeadMin, Max: ImageEngineDeadMax);
        foreach (var def in defs.All)
        {
            if (def.Tangler?.EngineDead is { } pair)
                bounds = pair;
        }
        return bounds;
    }
}
