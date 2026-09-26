namespace CSVM.Flight.Airframe;

using Godot;

/// <summary>The original's collision damage arithmetic (<c>FUN_0048d2c0</c>, decode in
/// docs/org/flightModel.md's "Collision damage"): what one contact costs the struck object and the
/// striker. Pure, so the suites pin it without a world.
/// ⚠ The severity is a COSINE and carries NO airspeed term: a 400 mph belly-flop and a 90 mph
/// belly-flop at the same attitude cost the same. Scaling by closing speed is the mistake this
/// replaced, and no choice of constant repairs it.</summary>
public static class CollisionDamage
{
    /// <summary>The entity-versus-entity cut (<c>0x0048d51a</c>): a NON-PLAYER striker that
    /// resolved an aeroplane deals a fifth. The player skips the detection that sets it, so a
    /// player ram is never cut, <see cref="FlightController"/> owns that asymmetry.</summary>
    public const float EntityCut = 0.2f;

    /// <summary>Collision grace written to BOTH parties after an entity-versus-entity impact
    /// (<c>obj+0xAC</c>, <c>0x0048d383</c>/<c>0x0048d395</c>), in seconds. It suppresses the whole
    /// sweep, not just the damage.</summary>
    public const float EntityGrace = 1.0f;

    public const float SpawnGrace = 1.5f;

    /// <summary>Per-metre-per-second of the contact shake's linear term (<c>0x006080c4</c>, read at
    /// <c>0x0048d3dc</c>).</summary>
    public const float ContactShakeFactor = 0.03f;

    /// <summary>The contact shake's ceiling in radians (<c>0x006036a8</c>, compared at
    /// <c>0x0048d3eb</c>), which any contact above roughly 5 m/s of closing speed reaches.</summary>
    public const float ContactShakeCap = 0.15f;

    /// <summary>Impact severity, <c>s = -(v̂ · n̂)</c>: the cosine between the unit velocity and the
    /// contact normal, floored at zero so a receding contact is not a hit. Both arguments must
    /// already be unit vectors, which is what the original normalises for at
    /// <c>0x0048df8b</c>.</summary>
    public static float Severity(Vector3 velocityDir, Vector3 normal) =>
        Mathf.Max(0f, -velocityDir.Dot(normal));

    /// <summary>The camera kick one contact spends, <c>min(speed · s · 0.03, 0.15)</c> radians
    /// (<c>0x0048d3cc</c>–<c>0x0048d409</c>), which <see cref="Camera.PlaneShake.ContactHit"/> takes.
    /// ⚠ This one DOES carry airspeed and reads the raw cosine, where the damage pair below carries
    /// neither: the two laws sit in the same function and must not be merged.</summary>
    public static float ContactShake(float speed, float severity) =>
        severity <= 0f || speed <= 0f
            ? 0f
            : Mathf.Min(speed * severity * ContactShakeFactor, ContactShakeCap);

    /// <summary>One term of the damage pair: <c>max(scale · s³, floor)</c>, the cube at
    /// <c>0x0048d4c1</c>. Both terms use the same shape over their own authored range, and a zero
    /// severity costs nothing at all rather than the floor.</summary>
    public static float Term(float severity, float floor, float scale) =>
        severity <= 0f ? 0f : Mathf.Max(scale * severity * severity * severity, floor);
}
