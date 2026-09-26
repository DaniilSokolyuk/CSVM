using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>
/// The gun mount a <c>mode ship</c> vehicle carries: the ANIMATED branch of the per-mount aim
/// update <c>FUN_004b7670</c>, which a patrol boat and a turret truck take and no aeroplane does
/// (docs/org/aiPilot/aiWeapons.md, "A <c>mode ship</c> vehicle's mount").
/// ⚠ Not the aeroplane's mount, and never to be collapsed onto it: an aeroplane clamps to an
/// authored band and arrives the same frame, a hull has an unauthored asymmetric elevation guard,
/// unrestricted yaw and a slew whose lag its aim quality pays for.
/// Pure and frame-local: the caller works in the hull's own basis and converts back.
/// </summary>
public static class SurfaceGunMount
{
    /// <summary>Elevation ceiling as the guarded direction's <c>y</c> in the hull frame: the
    /// literal <c>0.5</c> at <c>0x004b7?</c>, so 30° up.</summary>
    public const float MaxElevationY = 0.5f;

    /// <summary>Depression floor as the guarded direction's <c>y</c> in the hull frame: the literal
    /// <c>-0.2588</c>, so 15° down. ⚠ The band is ASYMMETRIC and the engine's own two literals are
    /// unrelated numbers, not a ± pair, mirroring either one is a silent aiming bug.</summary>
    public const float MinElevationY = -0.2588f;

    /// <summary>How fast the mount closes on the guarded direction, per second
    /// (<c>FUN_00460840</c>, the literal <c>4.0</c>). The turret system's own rate is a different
    /// constant on a different path (<see cref="TurretController.SlewRate"/>).</summary>
    public const float SlewRate = 4.0f;

    /// <summary>The dot product at which the interpolation stops being a slerp: past
    /// <c>+<see cref="SlerpGuardCos"/></c> the engine lerps outright, past the negative of it the
    /// two directions are treated as opposed and it sweeps a half turn about a perpendicular axis
    /// instead (<c>FUN_00538d70</c>). ⚠ The engine's own threshold, not the tighter one the turret
    /// path guards Slerp with; do not unify them.</summary>
    public const float SlerpGuardCos = 0.96f;

    /// <summary>The elevation guards, in the hull frame: pins <paramref name="desiredLocal"/>'s
    /// <c>y</c> into the band and rescales the horizontal part so the result stays unit length,
    /// which preserves the azimuth exactly (<c>FUN_004b7e70</c>). Yaw is never guarded, a hull
    /// traverses the full circle. A direction already inside the band comes back unchanged.</summary>
    public static Vector3 Guard(Vector3 desiredLocal)
    {
        var dir = desiredLocal.LengthSquared() > 1e-12f ? desiredLocal.Normalized() : Vector3.Forward;
        float y = dir.Y > MaxElevationY ? MaxElevationY
            : dir.Y < MinElevationY ? MinElevationY
            : dir.Y;
        if (Mathf.IsEqualApprox(y, dir.Y))
            return dir;
        float flat2 = (dir.X * dir.X) + (dir.Z * dir.Z);
        if (flat2 <= 1e-12f)
        {
            // Straight up or straight down: there is no azimuth left to preserve, and the engine
            // spreads the remainder over both horizontal axes rather than picking one.
            float side = Mathf.Sqrt(Mathf.Max(0f, 1f - (y * y)) * 0.5f);
            return new Vector3(side, y, side);
        }

        float scale = Mathf.Sqrt(Mathf.Max(0f, 1f - (y * y)) / flat2);
        return new Vector3(dir.X * scale, y, dir.Z * scale);
    }

    /// <summary>One step of the mount's travel toward <paramref name="desiredLocal"/>, in the hull
    /// frame: the engine's own three-branch interpolation (<c>FUN_00538d70</c>), snapping whole once
    /// one step covers the turn. ⚠ The rate is a FRACTION of what remains per second, not degrees
    /// per second. The near-parallel branch is renormalised where the engine leaves it unnormalised,
    /// since an unnormalised aim would bias the aim-quality cosine this feeds.</summary>
    public static Vector3 Slew(Vector3 currentLocal, Vector3 desiredLocal, float dt)
    {
        if (currentLocal.LengthSquared() <= 1e-12f || dt * SlewRate >= 1f)
            return desiredLocal;
        float t = dt * SlewRate;
        if (t <= 0f)
            return currentLocal;
        var from = currentLocal.Normalized();
        var to = desiredLocal.LengthSquared() > 1e-12f ? desiredLocal.Normalized() : from;
        float dot = from.Dot(to);
        if (dot > SlerpGuardCos)
            return (from.Lerp(to, t)).Normalized();
        if (dot < -SlerpGuardCos)
        {
            // Opposed: there is no shortest arc to take, so the engine sweeps pi*t about an axis
            // perpendicular to where the mount already points rather than picking a side.
            var perp = Mathf.IsZeroApprox(from.X)
                ? new Vector3(1f, 0f, 0f)
                : new Vector3(from.Y, -from.X, 0f).Normalized();
            float angle = Mathf.Pi * t;
            return ((from * Mathf.Cos(angle)) + (perp * Mathf.Sin(angle))).Normalized();
        }

        return from.Slerp(to, t).Normalized();
    }

    /// <summary>The mount's aim quality: the cosine between where the barrel actually points and
    /// the lead the solver asked for. ⚠ Measured against the RAW desired direction, never the
    /// guarded one (<c>0x004b78c9</c>), so a target outside the elevation band costs the shot the
    /// angle the guard gave away, the same way an aeroplane pays for its traverse clamp. Feeding
    /// the guarded direction here would let a hull shoot at anything overhead.</summary>
    public static float AimQuality(Vector3 actualLocal, Vector3 rawDesiredLocal) =>
        actualLocal.Dot(rawDesiredLocal);
}
