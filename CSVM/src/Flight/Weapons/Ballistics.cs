using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>The VELOCITY/ACCELERATION/GRAVITY integration every round in this game steps with,
/// shared by the live rounds (<see cref="ProjectilePool.SimStep"/>) and the reticle's projected
/// impact point (<see cref="Airframe.FlightController"/>), so the two cannot silently disagree. No Godot
/// <c>Node</c> dependency: both callers keep their own loop shape (the raycast/fuse test, the
/// range cap/iteration bound) and own their own <c>dt</c>.
///
/// <para>The motor is the original's own arithmetic (<c>FUN_005afd50</c>, decoded in
/// org/ordnanceTypes.md): speed rises by <c>ACCELERATION × dt</c> only while it is below the
/// round's cap, and is clamped there. <b>Nothing here reduces a round's speed</b>, the original
/// has no drag term, which is why its rounds carry so far, and the only decrease anywhere is the
/// steering step's turn penalty.</para></summary>
public static class Ballistics
{
    /// <summary>The own speed a round whose weapon authors <c>ACCELERATION</c> leaves at, m/s
    /// (<c>FUN_005aef40</c> at <c>0x005af318</c> writes 1e-4 before adding the launcher's speed).
    /// It is not zero so the round still has a heading to accelerate along.</summary>
    public const float MotorLaunchSpeed = 1e-4f;

    /// <summary>One integration step: accelerate along the current heading toward
    /// <paramref name="speedCap"/>, apply gravity, then advance position by the resulting velocity.
    /// Mutates both in place. <paramref name="vel"/> is the round's OWN velocity; a launcher's
    /// share is the caller's to carry (<see cref="ProjectilePool"/>'s <c>Inherited</c>).</summary>
    public static void Step(ref Vector3 pos, ref Vector3 vel, float accel, float grav, float speedCap, float dt)
    {
        ApplyForces(ref vel, accel, grav, speedCap, dt);
        pos += vel * dt;
    }

    /// <summary>The own speed a round leaves at and the cap it climbs to, off a platform travelling
    /// at <paramref name="launcherSpeed"/> m/s. A weapon with no <c>ACCELERATION</c> is seeded AT
    /// its cap, which is why nothing accelerates it; one with a motor starts at the launcher's speed
    /// and climbs to <c>VELOCITY</c> above it (<c>FUN_005aef40</c>, <c>0x005af0ea</c>/
    /// <c>0x005af147</c> against <c>0x005af318</c>/<c>0x005af356</c>/<c>0x005af371</c>).</summary>
    public static (float Speed, float Cap) LaunchSpeed(float velocity, float acceleration, float launcherSpeed) =>
        acceleration != 0f
            ? (launcherSpeed + MotorLaunchSpeed, velocity + launcherSpeed)
            : (velocity, velocity);

    /// <summary>Where a round of <paramref name="weapon"/> fired from <paramref name="origin"/>
    /// along <paramref name="forward"/> (carrying <paramref name="inheritVel"/>, the plane's
    /// velocity) sits after travelling <paramref name="maxDistance"/> m of path, integrated at
    /// <paramref name="dt"/>, the reticle's capped walk. Capped at the weapon's <c>RANGE</c> (a
    /// round never converges past where it expires) and a hard iteration bound.</summary>
    public static Vector3 March(WeaponDef weapon, Vector3 origin, Vector3 forward,
        Vector3 inheritVel, float maxDistance, float dt)
    {
        float accel = weapon.Acceleration ?? 0f;
        // The march runs the same two-vector round the pool flies: an own velocity the motor and
        // gravity act on, plus the launcher's, which nothing here changes (no gun authors LOCK_ON,
        // so the reticle's rounds carry theirs undecayed for the whole walk).
        var (speed, cap) = LaunchSpeed(weapon.Velocity ?? 500f, accel, inheritVel.Length());
        var vel = forward * speed;
        float grav = weapon.Gravity ?? 0f;
        float rangeCap = Mathf.Min(maxDistance, weapon.Range ?? maxDistance);
        var pos = origin;
        float travelled = 0f;
        for (int i = 0; i < 4096 && travelled < rangeCap; i++)
        {
            ApplyForces(ref vel, accel, grav, cap, dt);
            var stepv = (vel + inheritVel) * dt;
            float step = stepv.Length();
            if (step < 1e-6f)
                break; // a degenerate near-zero speed must never spin the loop
            if (travelled + step > rangeCap)
            {
                pos += stepv * ((rangeCap - travelled) / step); // don't overshoot the convergence range
                break;
            }
            pos += stepv;
            travelled += step;
        }
        return pos;
    }

    private static void ApplyForces(ref Vector3 vel, float accel, float grav, float speedCap, float dt)
    {
        float speed = vel.Length();
        // ⚠ The gate is the original's: a round already at or past its cap is left alone rather
        // than clamped down, so this branch can only ever raise a speed.
        if (accel != 0f && speed < speedCap && speed > 0f)
            vel = vel / speed * Mathf.Min(speed + accel * dt, speedCap);
        if (grav != 0f)
            vel += Vector3.Down * (grav * dt);
    }
}
