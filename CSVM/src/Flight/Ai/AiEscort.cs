using CSVM.Flight.Airframe;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>The five stations of the escort law's own state byte. The VALUES are the engine's
/// (0–4, vehicle <c>+0xd8</c>); the NAMES are ours, since the engine's debug readout never prints
/// them. They are not <see cref="AiMode"/> values and must not be confused with that
/// vocabulary.</summary>
public enum EscortState
{
    /// <summary>0: closing on the leader (or on a target) before the first join.</summary>
    Joining,

    /// <summary>1: holding the body-frame station. The law never leaves this state.</summary>
    Station,

    /// <summary>2: flying the station on the selected target instead of the leader.</summary>
    Engaging,

    /// <summary>3: re-joining. Nothing inside the law enters it (docs/org/aiPilot.md).</summary>
    Rejoining,

    /// <summary>4: closing the last 50 m onto the station, from <see cref="Rejoining"/>.</summary>
    Closing,
}

/// <summary>The formation leader's frame for one escort step: its position, its attitude basis
/// (X right, Y up, Z BACKWARD, the same convention <see cref="AiControlLaw"/> reads), its world
/// velocity, and whether it is the human player, which is the only thing the station offset is
/// keyed on.</summary>
public readonly struct EscortLeader
{
    public Vector3 Position { get; init; }

    public Basis Attitude { get; init; }

    public Vector3 Velocity { get; init; }

    public bool IsPlayer { get; init; }
}

/// <summary>The escorting pilot's selected target for one escort step: its position, its own
/// BACKWARD axis (the station is placed along the negation of it) and its world velocity, which
/// becomes the steering law's aim velocity while the wingman is on it.</summary>
public readonly struct EscortQuarry
{
    public Vector3 Position { get; init; }

    public Vector3 Backward { get; init; }

    public Vector3 Velocity { get; init; }
}

/// <summary>The original's formation-escort law (<c>FUN_0041e760</c>), the whole AI of a netless
/// <c>mode wingman</c> aircraft: a leader and the pilot's own selected target in, one station
/// point and that point's velocity out, per sim step. Every constant below is decoded
/// (docs/org/aiPilot.md, "The escort law"); nothing here is a tuning value. Engine-free and
/// deterministic (it holds no clock and draws no randomness), so the transition table and the
/// station geometry unit-test without a scene tree. <see cref="AiPilot"/> is the driver that
/// snapshots a live leader into it and hands the station to <see cref="AiControlLaw"/> on the
/// <see cref="AiLawParams.Wingman"/> table, which is the table the original passes here.</summary>
public sealed class AiEscort
{
    /// <summary>Range to the leader inside which the wingman joins the formation, metres
    /// (compared squared against 490000).</summary>
    public const float JoinRangeM = 700f;

    /// <summary>Own speed above which the join is allowed, m/s (46 mph).</summary>
    public const float JoinSpeedMps = 20.576f;

    /// <summary>Range to the leader at or beyond which a not-yet-joined wingman flies at the
    /// leader rather than at its own target, metres (compared squared against 3240000).</summary>
    public const float CloseOnLeaderRangeM = 1800f;

    /// <summary>How far ABOVE the leader that closing station sits, metres.</summary>
    public const float LeaderOverflyM = 200f;

    /// <summary>The weighted distance from the leader past which an engaging wingman breaks off
    /// and re-forms, metres, when the leader is the player.</summary>
    public const float BreakOffPlayerM = 1200f;

    /// <summary>The same break-off distance when the leader is another AI.</summary>
    public const float BreakOffAiM = 800f;

    /// <summary>The break-off test's weight on the altitude difference: the vertical error counts
    /// three times, and the range it is added to is HORIZONTAL only.</summary>
    public const float AltitudeErrorWeight = 3f;

    /// <summary>How close to the station counts as arrived, metres (compared squared against
    /// 2500).</summary>
    public const float CaptureRangeM = 50f;

    /// <summary>Inside this range from the leader the station is pushed back out along the
    /// leader-to-wingman line, scaled by <see cref="SeparationM"/> over the distance.</summary>
    public const float SeparationM = 80f;

    /// <summary>The station off a PLAYER leader, in the leader's body frame: 6 m out to the
    /// right, level, 18 m astern (<c>DAT_0061fb88</c>).</summary>
    public static readonly Vector3 PlayerLeaderStation = new(6f, 0f, 18f);

    /// <summary>The station off an AI leader: 8 m out, 2 m low, 8 m AHEAD
    /// (<c>DAT_0061fb98</c>).</summary>
    public static readonly Vector3 AiLeaderStation = new(8f, -2f, -8f);

    /// <summary>The leader whose station this pilot flies, the roster's <c>primary_target</c>,
    /// which for a wingman block is a formation leader and not a target assignment. Mutable: a
    /// mission script retargets it, and a dead leader is the host's to replace.</summary>
    public FlightController? Leader;

    /// <summary>Which station is being flown, in the engine's own five-state machine.</summary>
    public EscortState State { get; private set; }

    /// <summary>The last station point computed, world space. The 50 m capture tests measure
    /// against this stored value, exactly as the original tests against <c>+0xe0</c>.</summary>
    public Vector3 StationPoint { get; private set; }

    /// <summary>The body-frame station: the decoded offset for this leader kind, rotated into the
    /// leader's own frame and placed at its position.</summary>
    public static Vector3 FormationStation(Vector3 leaderPos, Basis leaderAttitude, bool playerLeader) =>
        leaderPos + (leaderAttitude * (playerLeader ? PlayerLeaderStation : AiLeaderStation));

    /// <summary>The station on the selected target: the same speed-ramped distance the combat
    /// driver leads by (<see cref="AiControlLaw.LeadOffsetFor"/>, 106.68 m to 259.08 m), placed
    /// along the NEGATION of the target's backward axis, so the escort's station sits that far
    /// AHEAD of its target where pursue's sits that far behind its own.</summary>
    public static Vector3 TargetStation(Vector3 targetPos, Vector3 targetBackward, float targetSpeed) =>
        targetPos - (targetBackward * AiControlLaw.LeadOffsetFor(targetSpeed));

    /// <summary>The separation push: inside <see cref="SeparationM"/> of the leader the station is
    /// displaced along the leader-to-wingman line by 80 m over the current distance. ⚠ The player
    /// station is 19 m from the leader, so this ALWAYS fires there and the commanded point
    /// alternates between the station and a point ~99 m out; the decoded hold is a loose trail
    /// around the leader, not a parade-tight join.</summary>
    public static Vector3 Separated(Vector3 station, Vector3 ownPos, Vector3 leaderPos)
    {
        var away = ownPos - leaderPos;
        float distSq = away.LengthSquared();
        if (distSq <= 0f || distSq >= SeparationM * SeparationM)
            return station;
        return station + (away * (SeparationM / Mathf.Sqrt(distSq)));
    }

    /// <summary>One step: transitions first, then the station that state flies, then the
    /// separation push, which the original applies to every state alike.
    /// <paramref name="aimVelocity"/> is the station point's own velocity for the steering law:
    /// the leader's on the formation states, the target's on the target ones, zero while closing
    /// on the leader's position.</summary>
    public Vector3 Next(Vector3 ownPos, float ownSpeed, in EscortLeader leader, EscortQuarry? quarry,
        out Vector3 aimVelocity)
    {
        // Escorting a non-player leader forces the engaging state every frame, so a
        // wingman-of-a-wingman has no persistent machine: target or station, decided per step.
        if (!leader.IsPlayer)
            State = EscortState.Engaging;

        Transition(ownPos, ownSpeed, leader, quarry);

        aimVelocity = Vector3.Zero;
        switch (State)
        {
            case EscortState.Joining:
                if (quarry is { } closing
                    && ownPos.DistanceSquaredTo(leader.Position) < CloseOnLeaderRangeM * CloseOnLeaderRangeM)
                {
                    StationPoint = TargetStation(closing.Position, closing.Backward, closing.Velocity.Length());
                    aimVelocity = closing.Velocity;
                }
                else
                {
                    StationPoint = leader.Position + (Vector3.Up * LeaderOverflyM);
                }
                break;

            case EscortState.Engaging when quarry is { } engaged:
                StationPoint = TargetStation(engaged.Position, engaged.Backward, engaged.Velocity.Length());
                aimVelocity = engaged.Velocity;
                break;

            case EscortState.Station:
            case EscortState.Closing:
                StationPoint = FormationStation(leader.Position, leader.Attitude, leader.IsPlayer);
                aimVelocity = leader.Velocity;
                break;

            default:
                break; // re-joining flies the station it already had
        }

        StationPoint = Separated(StationPoint, ownPos, leader.Position);
        return StationPoint;
    }

    // The break-off test: the altitude difference counts triple and the range is horizontal only.
    private static bool StrayedFromLeader(Vector3 ownPos, in EscortLeader leader)
    {
        float alt = (ownPos.Y - leader.Position.Y) * AltitudeErrorWeight;
        float horizontal = new Vector2(ownPos.X - leader.Position.X, ownPos.Z - leader.Position.Z)
            .LengthSquared();
        float limit = leader.IsPlayer ? BreakOffPlayerM : BreakOffAiM;
        return (alt * alt) + horizontal > limit * limit;
    }

    // The transition half of the same frame. Joining is left for good on the first frame the
    // wingman is close enough and fast enough; the engaging state returns to the formation when
    // the target is gone or the wingman has strayed too far from its LEADER (not from the target).
    private void Transition(Vector3 ownPos, float ownSpeed, in EscortLeader leader, EscortQuarry? quarry)
    {
        switch (State)
        {
            case EscortState.Joining:
                if (ownPos.DistanceSquaredTo(leader.Position) < JoinRangeM * JoinRangeM
                    && ownSpeed > JoinSpeedMps)
                {
                    State = EscortState.Station;
                }
                break;

            case EscortState.Engaging:
                if (quarry is null || StrayedFromLeader(ownPos, leader))
                    State = EscortState.Station;
                break;

            case EscortState.Rejoining:
                State = quarry is not null ? EscortState.Engaging
                    : Captured(ownPos) ? EscortState.Closing : State;
                break;

            case EscortState.Closing:
                State = quarry is not null ? EscortState.Engaging
                    : Captured(ownPos) ? EscortState.Station : State;
                break;

            default:
                break; // the formation station itself is never left inside the law
        }
    }

    private bool Captured(Vector3 ownPos) =>
        ownPos.DistanceSquaredTo(StationPoint) < CaptureRangeM * CaptureRangeM;
}
