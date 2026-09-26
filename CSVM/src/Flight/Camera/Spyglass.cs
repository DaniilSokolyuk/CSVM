using Godot;

namespace CSVM.Flight.Camera;

/// <summary>The spyglass's decoded rules, engine-free: the disc's own geometry and the fog-derived
/// range gate with its engage/release asymmetry. It also holds the field of view that keeps a
/// target at a constant apparent size, and the camera pose at the pilot's aircraft. The picture is
/// <see cref="SpyglassView"/>'s and its place on the pane is <see cref="Hud.EdgeMarker"/>'s. What
/// is drawn around it is <see cref="Hud.TargetHud"/>'s; this module answers only how far, how wide
/// and which way. Decode: <see href="../../../../docs/org/spyglass.md">org/spyglass.md</see>.</summary>
public static class Spyglass
{
    /// <summary>The authored <c>sgwin</c> window's side, 96 x 96 in every chapter's gamez, read at
    /// the 1440p reference and scaled by <see cref="Hud.HudMetrics"/> like every other HUD metric.
    /// </summary>
    public const float RefWindow = 96f;

    /// <summary>The mask circle's radius, the original's <c>ftol((96 + 1) * 0.5)</c> = 48, in the
    /// same reference pixels.</summary>
    public const float RefRadius = 48f;

    /// <summary>The share of the live fog band the range gate sits at (<c>FUN_0049d940</c>).
    /// </summary>
    public const float FogFraction = 0.8f;

    /// <summary>The gate's ceiling in metres, whatever the fog band says (<c>0x44fa0000</c>).
    /// </summary>
    public const float RangeCapM = 2000f;

    /// <summary>What the gate is multiplied by while nothing is held, so the picture engages
    /// nearer than it releases and cannot flicker at the boundary.</summary>
    public const float EngageFactor = 0.875f;

    /// <summary>The margin the framing keeps around the target's own radius, the <c>1.1</c> in
    /// <c>2 * atan(1.1 * R / d)</c>.</summary>
    public const float RadiusMargin = 1.1f;

    /// <summary>The field of view with no radius to frame, the original's 3 degrees
    /// (<c>0x3d567750</c>).</summary>
    public const float NoTargetFovDeg = 3f;

    /// <summary>Whether the spyglass is armed at level load (<c>0x004642e4</c>). The pilot's
    /// toggle flips it from here; nothing else writes it.</summary>
    public const bool DefaultOn = true;

    // The clamp the framing is held inside, 0x3cd67750 and 0x3fc90fdb. The floor is where a
    // distant target starts shrinking instead of holding its apparent size.
    private const float MinFovDeg = 1.5f;
    private const float MaxFovDeg = 90f;

    // A direction this close to the up reference leaves no roll to build a basis from.
    private const float UpTolerance = 0.999f;

    /// <summary>The slant range the picture is allowed at, from the live per-zone fog band:
    /// <c>near + (far - near) * 0.8</c>, capped at <see cref="RangeCapM"/>, times
    /// <see cref="EngageFactor"/> while <paramref name="held"/> is false. A band whose far is no
    /// further than its near carries no fog information and takes the cap alone.</summary>
    public static float RangeGate(Vector2 fogRange, bool held)
    {
        float gate = fogRange.Y > fogRange.X
            ? fogRange.X + ((fogRange.Y - fogRange.X) * FogFraction)
            : RangeCapM;
        if (gate > RangeCapM)
        {
            gate = RangeCapM;
        }

        return held ? gate : gate * EngageFactor;
    }

    /// <summary>The camera's field of view for a target of <paramref name="radius"/> metres at
    /// <paramref name="distance"/>: <c>2 * atan(1.1 * R / d)</c> in degrees, clamped to 1.5 and 90.
    /// A radius or distance of zero has nothing to frame and takes
    /// <see cref="NoTargetFovDeg"/>.</summary>
    public static float FovDeg(float radius, float distance)
    {
        if (radius <= 0f || distance <= 0f)
        {
            return NoTargetFovDeg;
        }

        float fov = Mathf.RadToDeg(2f * Mathf.Atan(RadiusMargin * radius / distance));
        return Mathf.Clamp(fov, MinFovDeg, MaxFovDeg);
    }

    /// <summary>Where the picture's camera stands and how it is turned: at <paramref name="eye"/>,
    /// the pilot's own aircraft, aimed at <paramref name="targetPos"/>. With
    /// <paramref name="keepRoll"/> the horizon rolls with <paramref name="attitude"/>, which the
    /// original does for an aircraft alone and for nothing else.</summary>
    public static Transform3D Pose(Vector3 eye, Vector3 targetPos, Basis attitude, bool keepRoll)
    {
        var forward = targetPos - eye;
        if (forward.LengthSquared() < 1e-6f)
        {
            return new Transform3D(attitude.Orthonormalized(), eye);
        }

        var dir = forward.Normalized();
        var up = keepRoll ? attitude.Y : Vector3.Up;
        // Looking straight along the up reference: take the other one, and world forward where
        // both are spent, so a target dead overhead still frames rather than producing no basis.
        if (Mathf.Abs(dir.Dot(up.Normalized())) > UpTolerance)
        {
            up = keepRoll ? Vector3.Up : attitude.Y;
        }

        if (Mathf.Abs(dir.Dot(up.Normalized())) > UpTolerance)
        {
            up = Vector3.Forward;
        }

        return new Transform3D(Basis.LookingAt(dir, up), eye);
    }
}
