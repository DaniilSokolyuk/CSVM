using System;

namespace CSVM.Mech3;

/// <summary>
/// The original's positional gain law: what a listener distance, a definition's <c>RANGE</c> pair
/// and its <c>VOLUME</c> turn into, in decibels. Decoded from the original's own sound manager 3D
/// update; the addresses, the constants and the shape of each band are in docs/formats/sounds.md.
/// Engine-free on purpose, so the table can be pinned without a live
/// <see cref="Godot.AudioStreamPlayer3D"/>, and because no Godot attenuation model expresses it:
/// the ramp is measured from the full-volume radius rather than from the emitter.
/// </summary>
public static class SoundFalloff
{
    /// <summary>Silence, DirectSound's own minimum in the path this is decoded from.</summary>
    public const float FloorDb = -100f;

    /// <summary>Past this multiple of the audible radius nothing is heard at all.</summary>
    public const float CullFactor = 1.1f;

    /// <summary>The share of the band past the full-volume radius that still plays unattenuated,
    /// and the ramp's reference distance: the curve leaves 0 dB one eighth of the way in.</summary>
    public const float ShelfFraction = 0.125f;

    /// <summary>Decibels lost per doubling of the ramp distance, which is the same scale the
    /// original converts a linear <c>VOLUME</c> on. Steeper per doubling than inverse distance's
    /// 6, but measured from the shelf, so it holds the level far longer in absolute distance.</summary>
    public const float DbPerDoubling = 10f;

    /// <summary>Where the ramp stands at the audible radius: three doublings past the shelf.
    /// The band beyond runs from here to <see cref="FloorDb"/>, a quiet tail rather than a
    /// cut.</summary>
    public const float EdgeDb = -30f;

    /// <summary>The factor every positional <c>RANGE</c> pair ships multiplied by, unless
    /// <c>--sound-range-scale=</c> says otherwise. It is a remake-only departure with nothing
    /// decoded behind it. The decode found no listener-side distance term, and at the authored
    /// radii the world emitters come in far closer than the original's. ⚠ Do not move the law's
    /// own numbers to chase it. The reading is in docs/formats/sounds.md.</summary>
    public const float ShippedRangeScale = 2.5f;

    // The quietest linear gain that is not simply silence, three doublings below the floor's ten.
    private const float QuietestVolume = 1f / 1024f;

    /// <summary>The session's multiplier on both <c>RANGE</c> radii and the cull that follows them,
    /// <see cref="ShippedRangeScale"/> unless <c>--sound-range-scale=</c> asks for another value.
    /// A suite or probe that pins a level at the data's own radii sets it to 1 for its own
    /// duration.</summary>
    public static float RangeScale { get; private set; } = ShippedRangeScale;

    /// <summary>A definition's linear <c>VOLUME</c> as decibels on the original's own scale. Not
    /// the usual 20 log10: the original's converter is ten decibels per doubling, so 0.5 is 10 dB down
    /// rather than 6, and a gain at or below a thousandth is written as silence outright.</summary>
    public static float VolumeDb(float volume)
    {
        if (volume >= 1f)
        {
            return 0f;
        }
        if (volume <= QuietestVolume)
        {
            return FloorDb;
        }
        return DbPerDoubling * MathF.Log2(volume);
    }

    /// <summary>What the distance alone costs, in decibels, for a definition whose <c>RANGE</c> is
    /// <paramref name="rangeMin"/> (full volume) to <paramref name="rangeMax"/> (audible). Zero
    /// inside the shelf, a logarithmic ramp to <see cref="EdgeDb"/> at the audible radius, then a
    /// straight run in decibels to the floor one tenth further out.</summary>
    public static float AttenuationDb(float distance, float rangeMin, float rangeMax)
    {
        if (!(rangeMax > 0f) || distance >= rangeMax * CullFactor)
        {
            return FloorDb;
        }
        if (distance >= rangeMax)
        {
            float over = (distance - rangeMax) / rangeMax;
            return EdgeDb + ((FloorDb - EdgeDb) * over / (CullFactor - 1f));
        }
        float band = rangeMax - rangeMin;
        if (distance <= rangeMin || !(band > 0f))
        {
            return distance <= rangeMin ? 0f : FloorDb;
        }
        float shelf = band * ShelfFraction;
        float reach = distance - rangeMin;
        if (reach <= shelf)
        {
            return 0f;
        }
        return DbPerDoubling * MathF.Log2(shelf / reach);
    }

    /// <summary>The level a positional player is set to: the definition's own volume plus the
    /// distance term, never below <see cref="FloorDb"/>. The clamp is where the original writes
    /// its minimum, so a quiet definition falls silent earlier than a loud one at the same
    /// distance.</summary>
    public static float GainDb(float distance, float rangeMin, float rangeMax, float volume)
    {
        float db = VolumeDb(volume) + AttenuationDb(distance, rangeMin, rangeMax);
        return db < FloorDb ? FloorDb : db;
    }

    /// <summary>Sets <see cref="RangeScale"/> for the session. A value that is not a finite
    /// positive number leaves the shipped factor in place.</summary>
    public static void SetRangeScale(float scale) =>
        RangeScale = float.IsFinite(scale) && scale > 0f ? scale : ShippedRangeScale;

    /// <summary><see cref="GainDb(float, float, float, float)"/> with both radii multiplied by
    /// <paramref name="rangeScale"/>, the cull and the tail moving with them. 1 is the identity.</summary>
    public static float GainDb(float distance, float rangeMin, float rangeMax, float volume,
        float rangeScale) =>
        GainDb(distance, rangeMin * rangeScale, rangeMax * rangeScale, volume);

    /// <summary>The level every positional play path sets: <see cref="GainDb(float, float, float, float, float)"/>
    /// at the session's <see cref="RangeScale"/>.</summary>
    public static float SessionGainDb(float distance, float rangeMin, float rangeMax, float volume) =>
        GainDb(distance, rangeMin, rangeMax, volume, RangeScale);
}
