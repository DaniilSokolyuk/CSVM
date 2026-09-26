using System;
using System.Collections.Generic;
using CSVM.Flight.Weapons;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>One pylon as <see cref="AiRocketeer"/> sees it: enough to run the gates without the
/// engine. <see cref="Index"/> is the <see cref="Loadout"/> hardpoint index the decision names to
/// <see cref="FireControl.SelectPylon"/>; <see cref="MountPos"/> is where the round leaves from,
/// so the range window and the lead are measured from the pylon, not the fuselage.</summary>
public readonly struct RocketPylonView
{
    public int Index { get; init; }

    /// <summary>The weapon's <c>DAMAGES_ZEPPELIN</c> bit, which decides what it may be aimed at.</summary>
    public bool DamagesZeppelin { get; init; }

    /// <summary>Ammo remains on this pylon (or the run is on infinite ammo).</summary>
    public bool Armed { get; init; }

    public Vector3 MountPos { get; init; }

    /// <summary>The weapon's <c>VELOCITY</c>, for the lead solve: the speed a round without a motor
    /// flies at, and the speed one with a motor climbs to above its launcher.</summary>
    public float RoundSpeed { get; init; }

    /// <summary>The weapon's <c>ACCELERATION</c>, 0 for a round without a motor. Non-zero switches
    /// the lead onto the launcher's frame and the motor's ramp, which is the branch the original's
    /// per-mount solver takes on this same field (docs/org/aiPilot/aiWeapons.md).</summary>
    public float RoundAccel { get; init; }

    /// <summary>This slot's own authored engagement window and refire interval, metres and seconds.
    /// The original keeps all three per weapon slot; 0 means the fit authored none and the walk
    /// falls back to the vehicle-wide defaults on <see cref="AiRocketeer"/>.</summary>
    public float MinRangeM { get; init; }

    public float MaxRangeM { get; init; }

    public float RefireSeconds { get; init; }
}

/// <summary>The AI's ordnance employment: when a non-human pilot pulls the rocket trigger, decoded
/// in docs/org/aiPilot/aiWeapons.md. The gun half is <see cref="AiGunner"/>, which this deliberately
/// does not arbitrate with: the original couples the two because they share one mount and one aim
/// vector, and ours do not. Per sim tick the host <see cref="Airframe.FlightController"/> ticks the lockout
/// and hands over the fire geometry (<see cref="Solve"/>); the answer is the trigger
/// (<see cref="WantsFire"/>), the pylon it chose (<see cref="SelectedPylon"/>) and the direction the
/// round leaves along (<see cref="LaunchDirWorld"/>).
/// ⚠ It holds no target of its own. The original validates ONE target and then walks every weapon
/// slot against it, so the host passes <see cref="AiGunner.Target"/>'s geometry rather than letting
/// this acquire, which is the only way the guns and the rockets cannot chase different aircraft.</summary>
public sealed class AiRocketeer
{
    /// <summary>The ordnance aim-quality gate: the clamped mount aim must sit within
    /// <c>acos(0.9962)</c>, 5°, of the lead. The original's own literal, and tighter than the gun's
    /// <see cref="AiGunner.AimQualityCos"/> by design.</summary>
    public const float AimQualityCos = 0.9962f;

    /// <summary>The shot-acceptance cone off the target's nose/tail axis, degrees. The original
    /// applies this at the TOP of the fire decision, before the weapon walk, so it gates the
    /// ordnance and the guns alike (<see cref="AiGunner.QuickDrawAngleDeg"/> carries the same
    /// value for the gun path).</summary>
    public float QuickDrawAngleDeg = 50f;

    /// <summary>Per-launch ordnance dice: <c>quick_draw_chance</c> at the pilot's rating (0.05 at
    /// 1, 0.44 at 9). The parameter's only consumer, and not a gun term at all.
    /// ⚠ The roll is drawn AFTER the lockout is stamped, so failing it costs the whole refire
    /// interval rather than retrying next tick (see <see cref="Solve"/>).</summary>
    public float QuickDrawChance = 0.05f;

    /// <summary>Traverse yaw half-limit, degrees (<c>gun_yaw</c>), the ordnance fires from a gun
    /// mount and clamps into the same band.</summary>
    public float PylonYawLimitDeg = AiGunner.GunConeHalfAngleDeg;

    /// <summary>Traverse pitch half-limit, degrees (<c>gun_pitch</c>).</summary>
    public float PylonPitchLimitDeg = AiGunner.GunConeHalfAngleDeg;

    /// <summary>The engagement window's near end, metres, for a pylon whose fit authored none:
    /// 200 m on every militia variant the game ships (30 m on the five base defs that author fields
    /// 3 and 4 transposed). A pylon carrying its def's own window flies that instead.
    /// ⚠ The window is a BAND. A max-range-only gate lets an AI launch from the merge.</summary>
    public float MinRangeM = 200f;

    /// <summary>The engagement window's far end, metres (see <see cref="MinRangeM"/>).</summary>
    public float MaxRangeM = 800f;

    /// <summary>The ordnance lockout, seconds, for a pylon whose fit authored none: 30 on the
    /// shipped militia variants, 5 for the Black Hat torpedo. Every launch stamps two timers, as the
    /// original does: the vehicle-wide one, which blocks ordnance of any kind, and the launching
    /// slot's own next-ready. A plane carrying two rocket types therefore cannot alternate them
    /// (docs/org/aiPilot/aiWeapons.md, "The fire routine").</summary>
    public float RefireSeconds = 30f;

    private readonly Func<float> _roll;
    private readonly Dictionary<int, float> _slotLockouts = new();
    private float _lockout;

    /// <summary>The dice source is a delegate, not the engine's rng object: the roll is a decoded
    /// gate and has to be assertable without a Godot runtime, which a
    /// <see cref="RandomNumberGenerator"/> is not. The session hands it a seeded stream's
    /// <c>Randf</c>; a test hands it a scripted sequence.</summary>
    public AiRocketeer(Func<float> roll)
    {
        _roll = roll;
    }

    /// <summary>True when this tick's geometry passed every gate and the dice came up, the AI's
    /// rocket trigger, replacing the human one for a non-human pilot.</summary>
    public bool WantsFire { get; private set; }

    /// <summary>The hardpoint the walk settled on, or -1. Set whenever a pylon clears the
    /// deterministic gates, whether or not the roll then passed: the original takes the weapon
    /// selection during the trigger pass and rolls afterwards, in the shot routine.</summary>
    public int SelectedPylon { get; private set; } = -1;

    /// <summary>World-space direction the round leaves along while <see cref="WantsFire"/>: the
    /// lead clamped into the traverse band, which is what the original's mount fires along.
    /// ⚠ Our pylon does not rotate to match, so the mounted body and the round it becomes point
    /// up to the traverse limit apart at the launch instant (<c>BL-405</c>).</summary>
    public Vector3 LaunchDirWorld { get; private set; }

    /// <summary>Seconds until ordnance may fire again, the lockout, for tests and breadcrumbs.</summary>
    public float LockoutRemaining => _lockout;

    /// <summary>What the last unlocked <see cref="Solve"/> decided, for the host's breadcrumb:
    /// the pylon it took and the dice, or why every candidate pylon was passed over. A locked-out
    /// tick leaves the previous verdict standing.</summary>
    public string LastVerdict { get; private set; } = string.Empty;

    /// <summary>The gate and pylon behind <see cref="LastVerdict"/> without its numbers, so the
    /// host logs a verdict when the GATE changes rather than every metre the range moves.</summary>
    public string LastVerdictKey { get; private set; } = string.Empty;

    /// <summary>Whether both timers a launch stamps have run out for this pylon, the vehicle-wide
    /// lockout and the slot's own: the acquisition's gasbag gate reads it (<c>FUN_00420070</c>),
    /// since a gasbag is offered only to a pilot whose gasbag ordnance can fire NOW.</summary>
    public bool SlotReady(int index) =>
        _lockout <= 0f && (!_slotLockouts.TryGetValue(index, out float slot) || slot <= 0f);

    /// <summary>Clears the trigger and ages the lockout. Called every tick, including the ticks
    /// where no fire decision runs at all, so the lockout is a vehicle timer rather than one that
    /// stops whenever the AI loses its target or leaves Pursue.</summary>
    public void Tick(float dt)
    {
        WantsFire = false;
        if (_lockout > 0f)
        {
            _lockout -= dt;
        }
        // The per-slot next-ready timers age alongside the vehicle-wide one: the original stamps
        // both on every launch and gates on both, which is why a two-type fit cannot alternate.
        if (_slotLockouts.Count > 0)
        {
            foreach (int index in new List<int>(_slotLockouts.Keys))
            {
                if (_slotLockouts[index] > 0f)
                    _slotLockouts[index] -= dt;
            }
        }
    }

    /// <summary>One tick's ordnance decision, in the original's gate order: the quick-draw cone
    /// aborts the whole pass, then each pylon must be armed, match the target's gasbag class, sit
    /// inside the range band and bring the clamped aim within <see cref="AimQualityCos"/>. The
    /// first pylon clearing all of it takes the selection.
    /// ⚠ The lockout is stamped BEFORE the dice, so a failed roll has already spent the full
    /// <see cref="RefireSeconds"/>. Stamp it after and the attempt becomes per-tick.</summary>
    public void Solve(Vector3 ownPos, Vector3 ownVelocity, Basis ownBasis,
        Vector3 targetPos, Vector3 targetVelocity, Vector3 targetForward, bool targetIsGasbag,
        IReadOnlyList<RocketPylonView> pylons)
    {
        WantsFire = false;
        SelectedPylon = -1;
        if (_lockout > 0f)
            return;
        if (!AiGunner.QuickDrawAccepts(ownPos, targetPos, targetForward, QuickDrawAngleDeg))
        {
            LastVerdict = LastVerdictKey = "quick draw refuses the aspect";
            return; // too oblique an attack for this pilot's quick draw
        }
        var basis = ownBasis.Orthonormalized();
        var toLocal = basis.Transposed();
        string passedOver = "no pylon offered";
        string passedOverKey = passedOver;
        foreach (var p in pylons)
        {
            if (!p.Armed)
                continue;
            // The DAMAGES_ZEPPELIN match, both ways: a torpedo is offered ONLY against a gasbag,
            // and a plain rocket only against something else. Getting this wrong is how the Black
            // Hat Warhawk's eight torpedoes end up aimed at an aircraft.
            if (p.DamagesZeppelin != targetIsGasbag)
                continue;
            // Its own next-ready, checked in the trigger pass: a slot on cooldown is invisible to
            // the walk. Decoded rather than biting today, since one pylon per authored entry makes
            // it expire alongside the vehicle-wide timer the same launch set.
            if (_slotLockouts.TryGetValue(p.Index, out float slotLockout) && slotLockout > 0f)
                continue;
            float minRange = p.MinRangeM > 0f ? p.MinRangeM : MinRangeM;
            float maxRange = p.MaxRangeM > 0f ? p.MaxRangeM : MaxRangeM;
            float sep2 = p.MountPos.DistanceSquaredTo(targetPos);
            if (sep2 < minRange * minRange || sep2 > maxRange * maxRange)
            {
                passedOver = FormattableString.Invariant(
                    $"pylon{p.Index} out of its {minRange:0}-{maxRange:0} m band at {Mathf.Sqrt(sep2):0} m");
                passedOverKey = $"band:{p.Index}";
                continue;
            }
            // The original leads each round in the frame it flies in (FUN_0041afe0): a motor round
            // in the launcher's, on the ramp it climbs, and one without a motor at VELOCITY against
            // the target's world velocity, since it carries nothing of its launcher's.
            Vector3 aim;
            bool solved = p.RoundAccel != 0f
                ? TryMotorIntercept(p.MountPos, p.RoundSpeed, p.RoundAccel, targetPos,
                    targetVelocity - ownVelocity, out aim)
                : AimAssist.TryIntercept(p.MountPos, p.RoundSpeed, targetPos, targetVelocity,
                    out aim, out _);
            if (!solved)
            {
                passedOver = $"pylon{p.Index} has no intercept"; // a target outrunning the round is simply not shot at
                passedOverKey = $"intercept:{p.Index}";
                continue;
            }
            var local = (toLocal * aim).Normalized();
            var (yawDeg, pitchDeg) = TurretController.AnglesOfLocal(local);
            var clamped = TurretController.LocalDir(
                Mathf.Clamp(yawDeg, -PylonYawLimitDeg, PylonYawLimitDeg),
                Mathf.Clamp(pitchDeg, -PylonPitchLimitDeg, PylonPitchLimitDeg));
            if (clamped.Dot(local) < AimQualityCos)
            {
                // the mount cannot be brought close enough: keep maneuvering
                passedOver = FormattableString.Invariant(
                    $"pylon{p.Index} lead {yawDeg:0}° yaw {pitchDeg:0}° pitch off the mount");
                passedOverKey = $"aim:{p.Index}";
                continue;
            }
            SelectedPylon = p.Index;
            LaunchDirWorld = (basis * clamped).Normalized();
            float refire = p.RefireSeconds > 0f ? p.RefireSeconds : RefireSeconds;
            _lockout = refire;
            _slotLockouts[p.Index] = refire;
            // The dice, last and only here. Inclusive, as the original's compare is.
            WantsFire = _roll() <= QuickDrawChance;
            LastVerdict = FormattableString.Invariant(
                $"pylon{p.Index} taken, dice {(WantsFire ? "pass" : "fail")}, next in {refire:0} s");
            LastVerdictKey = $"taken:{p.Index}:{WantsFire}";
            return;
        }
        LastVerdict = passedOver;
        LastVerdictKey = passedOverKey;
    }

    // The accelerating intercept the original solves for a weapon authoring ACCELERATION
    // (FUN_00462ce0, fed ACCELERATION, VELOCITY and the launcher's own velocity by FUN_0041afe0).
    // In the launcher's frame the round starts at rest and climbs to VELOCITY, so the intercept is
    // the time at which the separation equals the path the round has flown, found by bisection
    // where the original roots the polynomial.
    private static bool TryMotorIntercept(Vector3 mountPos, float velocity, float accel,
        Vector3 targetPos, Vector3 relVel, out Vector3 aimDir)
    {
        aimDir = Vector3.Zero;
        if (velocity <= 0f || accel <= 0f)
            return false;
        var separation = targetPos - mountPos;
        if (separation.LengthSquared() < 1e-6f)
            return false; // zero separation: nothing to aim at
        float lo = 0f;
        float hi = 1f;
        // The gap closes for good once the motor outruns the target, so the first sign change
        // bounds the intercept and there is nothing later to prefer over it.
        for (int i = 0; i < 24 && Gap(hi) > 0f; i++)
        {
            lo = hi;
            hi *= 2f;
        }
        if (Gap(hi) > 0f)
            return false; // a target outrunning the round is simply not shot at
        for (int i = 0; i < 40; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Gap(mid) > 0f)
                lo = mid;
            else
                hi = mid;
        }
        var lead = separation + relVel * hi;
        if (lead.LengthSquared() < 1e-12f)
            return false;
        aimDir = lead.Normalized();
        return true;

        float Gap(float t) => (separation + relVel * t).Length() - Flown(t, velocity, accel);
    }

    // The path a motor round has flown by t in its launcher's frame: the ACCELERATION ramp until
    // the motor reaches VELOCITY, then that speed held.
    private static float Flown(float t, float velocity, float accel)
    {
        float ramp = velocity / accel;
        return t <= ramp ? 0.5f * accel * t * t : (velocity * t) - (0.5f * velocity * ramp);
    }
}
