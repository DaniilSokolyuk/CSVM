using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>One row of the original's four AI steering parameter tables, read out of the image at
/// <c>0x61fb08</c>/<c>0x61fb28</c>/<c>0x61fb48</c>/<c>0x61fb68</c> and decoded in
/// <c>docs/org/aiControlLaw.md</c>. Eight floats there; the eighth is 0.0 in all four tables and is
/// never read, so it has no field here.</summary>
public readonly struct AiLawParams
{
    /// <summary>The engaged-pursuit table (<c>0x61fb08</c>): the widest throttle band and the
    /// loosest deadbands, used while the combat driver is actually chasing.</summary>
    public static readonly AiLawParams Engaged = new(0.3f, 1.3f, 0.08f, 0.01f, 0.2f, 0.025f, 0.9f);

    /// <summary>The wingman table (<c>0x61fb28</c>), the class-4 driver's.</summary>
    public static readonly AiLawParams Wingman = new(0.4f, 1.5f, 0.06f, 0.06f, 0.15f, 0.025f, 0.35f);

    /// <summary>The avoid-crash table (<c>0x61fb48</c>), always paired with the emergency arm.</summary>
    public static readonly AiLawParams AvoidCrash = new(0.6f, 1.3f, 0.06f, 0.06f, 0.15f, 0.025f, 0.35f);

    /// <summary>The cruise table (<c>0x61fb68</c>): patrol, and the combat driver while breaking
    /// off. A high throttle floor and a narrow speed cap.</summary>
    public static readonly AiLawParams Cruise = new(0.8f, 1.1f, 0.06f, 0.06f, 0.15f, 0.025f, 0.35f);

    public AiLawParams(float throttleMin, float speedCap, float crossDeadband, float levelDeadband,
        float leadAlong, float leadCross, float flipSpeed)
    {
        ThrottleMin = throttleMin;
        SpeedCap = speedCap;
        CrossDeadband = crossDeadband;
        LevelDeadband = levelDeadband;
        LeadAlong = leadAlong;
        LeadCross = leadCross;
        FlipSpeed = flipSpeed;
    }

    /// <summary><c>params[0]</c>: the throttle lever's floor.</summary>
    public float ThrottleMin { get; }

    /// <summary><c>params[1]</c>: the desired speed's cap as a multiple of <c>fd_speed</c>, AND the
    /// throttle lever's ceiling. One slot doing both jobs is the original's, not a shortcut here.
    /// Three of the four tables put it above 1.0, so an AI's commanded lever can exceed full.</summary>
    public float SpeedCap { get; }

    /// <summary><c>params[2]</c>: how small the roll command must be before the secondary channel
    /// (elevator, or rudder astern) is allowed to move at all.</summary>
    public float CrossDeadband { get; }

    /// <summary><c>params[3]</c>: the vertical half of the wings-level rule's gate.</summary>
    public float LevelDeadband { get; }

    /// <summary><c>params[4]</c>: desired speed added per metre of separation ALONG the aim point's
    /// own velocity.</summary>
    public float LeadAlong { get; }

    /// <summary><c>params[5]</c>: desired speed added per metre of separation ACROSS it.</summary>
    public float LeadCross { get; }

    /// <summary><c>params[6]</c>: below this horizontal aim magnitude, a below-the-nose aim point
    /// gets its lateral term mirrored rather than saturated.</summary>
    public float FlipSpeed { get; }
}

/// <summary>The original's AI steering law (<c>FUN_0041b560</c>), decoded in
/// <c>docs/org/aiControlLaw.md</c> (plan D31) and ported here. An aim point and that point's
/// velocity in, one <see cref="FlightInput"/> out: desired speed from the aim point's own speed
/// plus range-weighted lead terms, an intercept solve for the direction, bank-to-turn with an
/// elevator pull once the bank is nearly satisfied, and a per-axis scale/limit output stage.
/// Engine-free and pure over its arguments, so a fixed-dt run is deterministic. Two pieces of the
/// original are deliberately unported: the emergency arm's altitude/velocity assist (it writes
/// model state, which this seam must not) and the intercept solver's second-root preference
/// (unexposed by <see cref="AimAssist.TryIntercept"/>, and unreachable on patrol).</summary>
public static class AiControlLaw
{
    /// <summary>The desired speed's floor, 50 mph.</summary>
    public const float MinDesiredSpeed = 22.352f;

    /// <summary>How far the desired speed may sit either side of the aim point's own speed, 60 mph.</summary>
    public const float DesiredSpeedBand = 26.8224f;

    /// <summary>The desired speed used when the aim point is not moving, 180 mph.</summary>
    public const float StaticAimSpeed = 80.4672f;

    /// <summary>The hard ceiling on desired speed, 250 mph. ⚠ Not per-airframe: the def slots
    /// behind it carry no parser token and the def initialiser fixes them at 0 and this, so every
    /// AI aircraft in the original shares one 250 mph ceiling regardless of how fast its airframe
    /// is. On the quicker fighters this binds well before <c>fd_speed · SpeedCap</c> does.</summary>
    public const float SpeedCeiling = 111.76f;

    /// <summary>The floor the aim point's altitude is held above, a compiled 20 m global.
    /// ⚠ The danger-zone approach opens the whole band around its own solve and closes it again in
    /// the same call, so this is a one-solve exemption and never a mission-wide suspension; nothing
    /// else in the original writes the global (docs/org/aiPilot.md).</summary>
    public const float AimAltitudeFloor = 20f;

    /// <summary>How fast the commanded throttle walks toward the desired speed, per second.</summary>
    public const float ThrottleRatePerS = 0.35f;

    /// <summary>The projectile speed the head-on firing solution is solved at, a hard immediate in
    /// the original rather than a real round's speed.</summary>
    public const float GunSolutionSpeed = 860f;

    /// <summary>The backward axis's Y at which the low-speed recovery arms: −0.5 is the tail half a
    /// unit DOWN, so the nose is 30° UP. See <c>noseY</c>'s sign in the class's decode page.</summary>
    public const float RecoveryNoseY = -0.5f;

    /// <summary>Speed below which the low-speed recovery arms, 60 mph.</summary>
    public const float RecoverySpeed = 26.8224f;

    /// <summary>The roll authority the wings-level rule commands.</summary>
    public const float LevelAuthority = 0.2f;

    /// <summary>Nose verticality above which the wings-level rule stands down.</summary>
    public const float NearVerticalNoseY = 0.9f;

    /// <summary>Added to every per-axis scale while engaged.</summary>
    public const float EngagedScaleBonus = 0.5f;

    /// <summary>Added to every per-axis limit while engaged.</summary>
    public const float EngagedLimitBonus = 0.25f;

    /// <summary>Added to the skill factor while engaged.</summary>
    public const float EngagedSkillBonus = 0.08f;

    private const float LeadNear = 106.68f;         // 350 ft
    private const float LeadFar = 259.08f;          // 850 ft
    private const float LeadSpeedLo = 20.576f;      // 46 mph
    private const float LeadSpeedHi = 102.880005f;  // 230 mph
    private const float LeadSlope = 1.8516719f;     // metres of offset per m/s between the two

    /// <summary>The ceiling a station-keeping escort flies under: the decoded one, or the leader's
    /// speed plus <see cref="DesiredSpeedBand"/> when the leader is faster. ⚠ A remake-only lift,
    /// not a decoded value: <see cref="SpeedCeiling"/> sits below a fighter's own cruise
    /// (<c>fd_speed</c> is 302 mph on a Bloodhawk), so under it an escort throttles back the moment
    /// it passes 250 mph and never rejoins. Keep it keyed on station-keeping alone.</summary>
    public static float StationCeiling(float leaderSpeed) =>
        Mathf.Max(SpeedCeiling, leaderSpeed + DesiredSpeedBand);

    /// <summary>How far ahead of a target, along the target's own facing, the combat driver puts
    /// the aim point (<c>FUN_0041d9f0</c>): 350 ft at or below 46 mph, ramping linearly to 850 ft
    /// at or above 230 mph. This is the driver's, not the law's, and lives here because it is the
    /// same decode.</summary>
    public static float LeadOffsetFor(float targetSpeed) =>
        targetSpeed <= LeadSpeedLo ? LeadNear
        : targetSpeed >= LeadSpeedHi ? LeadFar
        : LeadNear + (targetSpeed - LeadSpeedLo) * LeadSlope;

    /// <summary>One step's stick and throttle for an aim point. <paramref name="emergency"/> is the
    /// crash-recovery arm; <paramref name="engaged"/> is the combat driver's authority bonus;
    /// <paramref name="gunLead"/> swaps the fly-to solve for a firing solution;
    /// <paramref name="stationKeeping"/> swaps the desired-speed ceiling for
    /// <see cref="StationCeiling"/> and is the escort's alone; <paramref name="openAltitudeBand"/>
    /// drops the aim-altitude clamp for this solve, which only the danger-zone approach asks.</summary>
    public static FlightInput Steer(FlightModel model, Vector3 aimPoint, Vector3 aimVelocity,
        in AiLawParams p, float throttle, float dt, bool emergency = false, bool engaged = false,
        bool gunLead = false, float skillFactor = 1f, bool stationKeeping = false,
        bool openAltitudeBand = false)
    {
        var stats = model.Stats;
        var att = model.Attitude;
        var pos = model.Position;
        // ⚠ `param_1[0x67]`, the BACKWARD axis's Y, so this is + with the nose DOWN. Kept under the
        // engine's name rather than renamed, because every constant compared against it is signed
        // the engine's way too.
        float noseY = att.Z.Y;

        // The aim point is held inside the AI's altitude band, and an aim velocity that would carry
        // it further outside is flattened rather than followed. The band opens only for a caller
        // that must reach a point outside it, which is the danger-zone approach and nothing else.
        if (!openAltitudeBand)
        {
            if (aimPoint.Y > stats.FlightCeiling)
            {
                aimPoint.Y = stats.FlightCeiling;
                if (aimVelocity.Y > 0f)
                    aimVelocity.Y = 0f;
            }
            if (aimPoint.Y < AimAltitudeFloor)
            {
                aimPoint.Y = AimAltitudeFloor;
                if (aimVelocity.Y < 0f)
                    aimVelocity.Y = 0f;
            }
        }

        var delta = aimPoint - pos;
        float rangeSq = delta.LengthSquared();
        if (rangeSq < 1e-6f)
            return new FlightInput { Throttle = Mathf.Clamp(throttle, 0f, 1f) };

        float want = DesiredSpeed(stats, delta, rangeSq, aimVelocity, p, emergency, noseY, stationKeeping);
        var aimDir = AimDirection(model, pos, aimPoint, aimVelocity, delta, want, gunLead);

        float bx = aimDir.Dot(att.X);   // + = the aim point is to the right
        float by = aimDir.Dot(att.Y);   // + = above
        float bz = aimDir.Dot(att.Z);   // + = BEHIND: row 2 of the basis at +0x180 is BACKWARD

        // ⚠ BEHIND, not ahead. Do not "fix" this to bz < 0: that was BL-387, and it hands the roll
        // channel full stick on a straight leg. aiControlLaw.md step 4 has the five confirmations.
        float h = Mathf.Sqrt((bx * bx) + (by * by));
        if (bz > 0f)
        {
            if (h == 0f)
                by = 1f;
            else
            {
                bx /= h;
                by /= h;
            }
            h = 1f;
        }

        float lever = Throttle(model, want, throttle, dt, p);

        // ⚠ CLEARING rudder_tol picks the BANK branch, so a higher rudder_tol means MORE rudder.
        // Astern h is pinned to 1 and always banks; ahead it is the true error. aiControlLaw.md
        // step 6 has the table, including why balmoral's authored 1.0 is all-rudder.
        float roll, pitch = 0f, yaw = 0f, absBx;
        if (h > stats.RudderTol || Mathf.Abs(bx) <= Mathf.Abs(by))
        {
            // Bank toward the lateral error and let the plant's own bank coupling turn; the
            // elevator only joins once the bank command is nearly satisfied.
            if (by < 0f)
                bx = emergency || h < p.FlipSpeed ? -bx : bx < 0f ? -1f : 1f;
            roll = -bx;
            absBx = Mathf.Abs(bx);
            if (absBx < p.CrossDeadband)
                pitch = by;
        }
        else
        {
            // A lateral-dominant error too small to be worth banking for: the vertical error drives
            // what roll there is and the lateral one goes on the rudder. Only reachable AHEAD,
            // since anything astern was renormalised to h = 1 and took the branch above.
            absBx = Mathf.Abs(bx);
            if (bx < 0f)
                by = -by;
            roll = by;
            if (Mathf.Abs(by) < p.CrossDeadband)
                yaw = -bx;
        }

        // The straight-ahead case: both body components tiny, which only survives unrenormalised,
        // so this is the rule that holds a tracking aeroplane's wings level.
        if (!emergency && absBx < p.CrossDeadband && Mathf.Abs(by) < p.LevelDeadband
            && Mathf.Abs(noseY) < NearVerticalNoseY)
        {
            roll = (att.Y.Y >= 0f ? -att.X.Y : att.X.Y < 0f ? 1f : -1f) * LevelAuthority;
        }

        // Steeply nose-UP and slow: push the nose down and firewall the lever, either way up. A
        // stall recovery, which is what makes the sign of noseY legible, see aiControlLaw.md.
        if (noseY < RecoveryNoseY && model.Speed < RecoverySpeed)
        {
            pitch = att.Y.Y >= 0f ? -1f : 1f;
            lever = p.SpeedCap;
        }

        // ⚠ NEAR-BANG-BANG, not proportional: the shipped scale (3.5) against a limit of 1 means
        // anything past ~0.29 of body-frame error saturates. Limits above 1 are the original's own
        // range; FlightModel.Step clamps to ±1, so do not clamp here too.
        float scaleBonus = engaged ? EngagedScaleBonus : 0f;
        float limitBonus = engaged ? EngagedLimitBonus : 0f;
        roll = Limit(roll * (stats.AiInputScaleRoll + scaleBonus), stats.AiInputLimitRoll + limitBonus);
        pitch = Limit(pitch * (stats.AiInputScalePitch + scaleBonus), stats.AiInputLimitPitch + limitBonus);
        yaw = Limit(yaw * (stats.AiInputScaleYaw + scaleBonus), stats.AiInputLimitYaw + limitBonus);

        // The skill factor (sixth_sense_factor) eases every channel, and the crash-recovery arm is
        // the one place it does not apply.
        if (!emergency)
        {
            float s = skillFactor + (engaged ? EngagedSkillBonus : 0f);
            roll *= s;
            pitch *= s;
            yaw *= s;
        }

        return new FlightInput { Pitch = pitch, Roll = roll, Yaw = yaw, Throttle = lever };
    }

    private static float DesiredSpeed(PlaneStats stats, Vector3 delta, float rangeSq,
        Vector3 aimVelocity, in AiLawParams p, bool emergency, float noseY, bool stationKeeping)
    {
        if (emergency)
        {
            // Nose up holds the slow target; nose down raises it in proportion to how steep the
            // dive is. The altitude/velocity assist that rides beside this in the original is not
            // ported (see the class remarks).
            return noseY >= 0f ? MinDesiredSpeed : MinDesiredSpeed * (1f - noseY);
        }

        float aimSpeed = aimVelocity.Length();
        float want;
        if (aimSpeed <= 0f)
        {
            want = StaticAimSpeed;
        }
        else
        {
            float along = delta.Dot(aimVelocity / aimSpeed);
            float crossSq = rangeSq - (along * along);
            float cross = crossSq > 0f ? Mathf.Sqrt(crossSq) : 0f;
            want = aimSpeed + (along * p.LeadAlong) + (cross * p.LeadCross);
            want = Mathf.Clamp(want, aimSpeed - DesiredSpeedBand, aimSpeed + DesiredSpeedBand);
        }

        // The airframe cap wins outright when it bites; only under it does the 50 mph floor apply.
        // That order is the original's and it matters when fd_speed · SpeedCap is itself tiny.
        float cap = stats.FdSpeed * p.SpeedCap;
        want = want > cap ? cap : Mathf.Max(want, MinDesiredSpeed);
        return Mathf.Clamp(want, 0f, stationKeeping ? StationCeiling(aimSpeed) : SpeedCeiling);
    }

    private static Vector3 AimDirection(FlightModel model, Vector3 pos, Vector3 aimPoint,
        Vector3 aimVelocity, Vector3 delta, float want, bool gunLead)
    {
        // gunLead solves the gun problem at a fixed round speed against RELATIVE velocity
        // (docs/org/aiControlLaw.md); a no-root geometry falls back to the straight line.
        var relative = gunLead ? aimVelocity - (model.VelocityDir * model.Speed) : aimVelocity;
        float speed = gunLead ? GunSolutionSpeed : want;
        return AimAssist.TryIntercept(pos, speed, aimPoint, relative, out var dir, out _)
            ? dir
            : delta.Normalized();
    }

    private static float Throttle(FlightModel model, float want, float throttle, float dt,
        in AiLawParams p)
    {
        float lever = throttle + (want <= model.Speed ? -ThrottleRatePerS * dt : ThrottleRatePerS * dt);
        return lever > p.SpeedCap ? p.SpeedCap : Mathf.Max(lever, p.ThrottleMin);
    }

    private static float Limit(float v, float limit) => Mathf.Clamp(v, -limit, limit);
}
