using System;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// The original's projected aircraft ground shadow as a rule: which way it projects, where it
/// lands, how large, how dark, and when it is not drawn at all. Every constant here is decoded
/// from the retail executable and carries its address in
/// <see href="../../../../docs/org/shadows.md">docs/org/shadows.md</see>; nothing is fitted. The
/// drawing half is <see cref="GroundShadowPass"/>, which is also where the remake-only rules live.
/// </summary>
public static class GroundShadowLaw
{
    /// <summary>Horizontal distance out to which another aircraft's shadow is at full strength.
    /// Pushed per caller by the shadow pass, with the Spruce Goose taking 300 instead.</summary>
    public const float NearDistance = 1f;

    /// <summary>Horizontal distance at which another aircraft's shadow is gone; the Spruce
    /// Goose takes 600. The player's own aircraft is exempt from the distance fade.</summary>
    public const float FarDistance = 200f;

    /// <summary>Height above the ground up to which the shadow is at full strength and its
    /// footprint untouched. The Spruce Goose takes 180.</summary>
    public const float FullShadowAltitude = 60f;

    /// <summary>Height above the ground at which the shadow is dropped outright rather than
    /// drawn transparent. The Spruce Goose takes 750.</summary>
    public const float CutoffAltitude = 250f;

    /// <summary>How far the player's own shadow direction is skewed along the horizontal part of
    /// its nose, which lands the shadow this many times its altitude ahead of it.</summary>
    public const float PlayerSkew = 1.5f;

    /// <summary>What the player's own footprint grows to at <see cref="CutoffAltitude"/>; every
    /// other aircraft's stays at 1.</summary>
    public const float PlayerMaxScale = 3f;

    /// <summary>The fixed factor on the darkening term, the one number in the colour that is
    /// neither authored nor derived.</summary>
    public const float Darkening = 0.8f;

    /// <summary>The live shadow texture's edge, in texels: the raster, the spread and the ramp
    /// all run over a buffer this size. ⚠ The one number here the original does not supply: it
    /// rasters <see cref="OriginalTextureSize"/>, which reads coarse under the player's own
    /// aircraft once its footprint grows to <see cref="PlayerMaxScale"/>.</summary>
    public const int TextureSize = 64;

    /// <summary>The edge the original rasters, and the step every texel-measured term of the
    /// spread and the raster is written against, so a finer texture keeps the softening's width
    /// on the ground instead of shrinking it with the step.</summary>
    public const int OriginalTextureSize = 32;

    /// <summary>How many texels of the live texture stand for one of the original's. Every term
    /// measured in texels is multiplied by it, which is what holds the picture fixed in world
    /// terms while the step gets finer.</summary>
    public const float TexelScale = TextureSize / (float)OriginalTextureSize;

    /// <summary>Entries in the colour ramp the spread indexes, white at 0 and the shadow colour
    /// at 10.</summary>
    public const int RampSteps = 11;

    // The original's spread reaches one texel of its own 32 in every direction, so the box it
    // sums over runs this far either side of a texel's centre. Scaled by the texel scale, it is
    // the same width of ground whatever the step.
    private const float OriginalSpreadHalfWidth = 1.5f;

    // What the spread is worth in ramp steps: a texel covered inside the scanned band takes this
    // for itself, and the eight neighbours of the original's own box are worth this together. A
    // lone covered texel reaches step 2 and an interior one saturates the ramp, as the decode has it.
    private const int SelfSteps = 2;
    private const int NeighbourSteps = 8;

    // The doubled weight of a texel wholly inside the spread's box, the unit its two end texels
    // take a fraction of. Doubled, since a box of half-integer width cuts its ends in half.
    private const int WholeWeight = 2;

    /// <summary>The direction the shadow projects along. Straight down for every aircraft, that
    /// being the <c>SHADOW_ANGLES</c> all 53 shipped weather files author, with the player's own
    /// skewed along its nose so the shadow runs ahead of it.</summary>
    public static Vector3 Direction(bool isPlayer, Vector3 nose)
    {
        var dir = Vector3.Down;
        if (!isPlayer)
            return dir;
        // The horizontal part of the nose only: the vertical component is left alone, so the
        // projection ratio becomes the skew itself. ⚠ The original subtracts row 2 of the
        // orientation matrix, and that row is MINUS the nose (docs/org/flightModel.md).
        var skewed = new Vector3(
            dir.X + (nose.X * PlayerSkew), dir.Y, dir.Z + (nose.Z * PlayerSkew));
        return skewed.Normalized();
    }

    /// <summary>The distance fade, linear in SQUARED horizontal distance from the player, so it
    /// is weighted hard toward the far end. Vertical separation does not enter it.</summary>
    public static float DistanceFactor(Vector3 position, Vector3 player)
    {
        float dx = position.X - player.X;
        float dz = position.Z - player.Z;
        float d2 = (dx * dx) + (dz * dz);
        float near2 = NearDistance * NearDistance;
        float far2 = FarDistance * FarDistance;
        if (d2 <= near2)
            return 1f;
        return d2 >= far2 ? 0f : (far2 - d2) / (far2 - near2);
    }

    /// <summary>The altitude ramp: full below <see cref="FullShadowAltitude"/>, gone at
    /// <see cref="CutoffAltitude"/>, linear between.</summary>
    public static float AltitudeFactor(float altitude)
    {
        if (altitude <= FullShadowAltitude)
            return 1f;
        return altitude >= CutoffAltitude
            ? 0f
            : (CutoffAltitude - altitude) / (CutoffAltitude - FullShadowAltitude);
    }

    /// <summary>What the footprint is scaled by about its projected origin: 1 for every aircraft
    /// but the player's own, which spreads to <see cref="PlayerMaxScale"/> as it climbs.</summary>
    public static float FootprintScale(bool isPlayer, float altitudeFactor) =>
        isPlayer ? PlayerMaxScale - (2f * altitudeFactor) : 1f;

    /// <summary>One point flattened onto the ground plane along <paramref name="direction"/>,
    /// which is how both the shadow's origin and each corner of its footprint are placed.</summary>
    public static Vector3 Project(Vector3 point, float groundY, Vector3 direction)
    {
        float travel = (groundY - point.Y) / direction.Y;
        return new Vector3(
            point.X + (travel * direction.X), groundY, point.Z + (travel * direction.Z));
    }

    /// <summary>The shadow colour as a multiplier on the ground, per channel, faded toward white
    /// by strength. It is the light the aircraft blocks, so it follows the mission's authored
    /// <c>SUNLIGHT_DIFFUSE</c>/<c>SUNLIGHT_AMBIENT</c> pair and lightens as ambient rises.
    /// Returned in the original's own gamma space, one unit being its 255.</summary>
    public static Color Colour(Vector3 diffuse, Vector3 ambient, Vector3 direction, float strength)
        => new(
            Channel(diffuse.X, ambient.X, direction.Y, strength),
            Channel(diffuse.Y, ambient.Y, direction.Y, strength),
            Channel(diffuse.Z, ambient.Z, direction.Y, strength));

    /// <summary>The original's fixed spread over a coverage mask, at its own step: every covered
    /// texel adds to itself and to each of its eight neighbours. The border band is never
    /// scanned, so a silhouette touching the edge spreads inward only. Returns the ramp index per
    /// texel, and <paramref name="texelScale"/> holds that picture at a finer step.</summary>
    public static int[] Spread(bool[] covered, int width, int height, float texelScale = 1f)
    {
        var acc = new int[covered.Length];
        Spread(covered, width, height, acc, texelScale);
        return acc;
    }

    /// <summary>The same spread into a caller's own buffer, for the per-frame raster, which runs
    /// once per live aircraft and has no reason to allocate one each time.</summary>
    public static void Spread(bool[] covered, int width, int height, int[] acc,
        float texelScale = 1f)
    {
        float half = OriginalSpreadHalfWidth * texelScale;
        int reach = (int)Math.Ceiling(half - 0.5f);
        int band = Math.Max(1, Mathf.RoundToInt(texelScale));
        // The box ends on a texel's edge only at odd scales; at even ones it cuts the two end
        // texels in half, which is why the weights are carried doubled.
        int end = Mathf.RoundToInt(WholeWeight * (half + 0.5f - reach));
        Array.Clear(acc, 0, acc.Length);
        for (int y = band; y < height - band; y++)
        {
            for (int x = band; x < width - band; x++)
            {
                if (covered[(y * width) + x])
                    Add(acc, width, height, x, y, reach, end);
            }
        }

        // The box's own weight with the covered texel itself taken out of it, which is what the
        // eight neighbours of the original's 3x3 are worth together.
        float neighbourhood = (4f * half * half) - 1f;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width) + x;
                bool source = covered[at] && x >= band && x < width - band
                    && y >= band && y < height - band;
                int neighbours = acc[at] - (source ? WholeWeight * WholeWeight : 0);
                int steps = (source ? SelfSteps : 0) + Mathf.RoundToInt(
                    NeighbourSteps * neighbours / (WholeWeight * WholeWeight * neighbourhood));
                acc[at] = Math.Min(steps, RampSteps - 1);
            }
        }
    }

    /// <summary>Where a ramp index sits between white and the shadow colour. The original's
    /// 11-entry table is exactly this mix, built once per frame per channel.</summary>
    public static float Coverage(int rampIndex) => rampIndex / (float)(RampSteps - 1);

    // One covered texel's box into the accumulator, in doubled weights: a texel wholly inside the
    // box is worth 2, and its two end texels take the fraction of a texel they cover. Clamped to
    // the buffer, so what a texel near the edge would spread past it is lost, as at the original's
    // own step.
    private static void Add(int[] acc, int width, int height, int x, int y, int reach, int end)
    {
        int fromY = Math.Max(0, y - reach);
        int toY = Math.Min(height - 1, y + reach);
        int fromX = Math.Max(0, x - reach);
        int toX = Math.Min(width - 1, x + reach);
        for (int ny = fromY; ny <= toY; ny++)
        {
            int down = ny == y - reach || ny == y + reach ? end : WholeWeight;
            int row = ny * width;
            for (int nx = fromX; nx <= toX; nx++)
                acc[row + nx] += down * (nx == x - reach || nx == x + reach ? end : WholeWeight);
        }
    }

    // One channel of the colour. k is the physical ratio "ambient alone" over "ambient plus
    // diffuse at normal incidence" for flat ground. ⚠ Keep the zero guards: with no ambient at
    // all, or a denominator that cancels, the original falls back to the raw ambient value
    // rather than to a ratio, which is the darkest shadow that channel can take.
    private static float Channel(float diffuse, float ambient, float directionY, float strength)
    {
        float denominator = ambient - (diffuse * directionY);
        float k = ambient == 0f || denominator == 0f ? ambient : ambient / denominator;
        return Math.Clamp((1f - strength) + (Darkening * strength * k), 0f, 1f);
    }
}
