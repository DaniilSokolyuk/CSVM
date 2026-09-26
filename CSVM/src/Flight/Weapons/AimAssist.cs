using System.Collections.Generic;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>Which of the original's four candidate lists a survivor came from. The lists differ
/// only in what they hold, scoring is identical across all four, so this is for the caller and
/// the suite, never for the scorer.</summary>
public enum AimTargetKind
{
    /// <summary><c>VehicleList</c>, aircraft, plus the AI ground/sea vehicles M4 will add.</summary>
    Vehicle,

    /// <summary>Turrets. Empty until M4 builds them.</summary>
    Turret,

    /// <summary><c>MStructList</c>, the mission structures; CSVM's approximation is the
    /// destructible registry.</summary>
    Structure,

    /// <summary>Live proximity-fused ordnance in flight, the reason guns snap onto an incoming
    /// rocket by design.</summary>
    Ordnance,
}

/// <summary>One gun barrel's aim-assist state, held in plane-local space
/// (docs/org/aim-assist.md "Per-muzzle state", the original's eight <c>0x24</c>-byte slots at
/// plane <c>+0x3a4</c>). Indexed by (gun group, muzzle) exactly as <see cref="FireControl"/>
/// already picks a muzzle, no re-derivation of the original's <c>weaponGroup·2+barrelToggle</c>
/// index is needed.</summary>
public struct GunAimSlot
{
    /// <summary>False until this barrel exists (mirrors the original's muzzle-handle field, 0 =
    /// unused); an inactive slot is skipped by <see cref="AimAssist.Tick"/> entirely.</summary>
    public bool Active;

    /// <summary>Plane-local unit vector: what the round is actually fired along. Lags
    /// <see cref="Target"/> by the catch-up slerp.</summary>
    public Vector3 Smoothed;

    /// <summary>Plane-local unit vector: what <see cref="Smoothed"/> is chasing, the candidate
    /// scan's winner once B4/B5 land, or <see cref="AimAssist.LocalForward"/> when the forget
    /// timer has unwound it.</summary>
    public Vector3 Target;

    /// <summary>Game-time seconds (<see cref="CSVM.Utils.GameClock.Time"/>) this slot was last
    /// touched, stamped by the forget reset here, and, once B5 lands, by every round that goes
    /// out (the original restamps it on every shot, which is why the forget timer measures time
    /// since the barrel last fired, not time since a lock was lost).</summary>
    public double LastUpdate;
}

/// <summary>One thing the assist may snap onto, in world space. Built per fire call by whoever
/// holds the live lists (<see cref="AimCandidateSet"/>); the scorer reads nothing else.</summary>
public struct AimCandidate
{
    /// <summary>World position the intercept is solved to.</summary>
    public Vector3 Position;

    /// <summary>World velocity, m/s. The scorer subtracts the shooter's before solving, since the
    /// engine's solver takes the RELATIVE velocity.</summary>
    public Vector3 Velocity;

    /// <summary>Team id (the engine's <c>+0x08</c>). Matching the shooter's rejects the pair, and
    /// so does <see cref="AimAssist.NeutralTeam"/> on EITHER side, read off
    /// <see cref="Airframe.FlightController.Team"/> for a live aircraft, or
    /// <see cref="AimAssist.TeamOfPilot"/> for that field's own default.</summary>
    public int Team;

    /// <summary>False for a candidate the engine's vtable <c>+0x14</c> predicate would reject,
    /// dead, or not live yet (a crashed pilot, a destroyed structure).</summary>
    public bool Live;

    /// <summary>The per-target cone override (<c>+0x50</c>), a half-angle in RADIANS, or
    /// <see cref="AimAssist.NoConeOverride"/> to use the firing weapon's own cone. Every shipped
    /// entity is the latter.</summary>
    public float ConeOverride;

    /// <summary>What this candidate IS, handed back on the winning result so the caller can act on
    /// it, and matched against <see cref="AimScan.Self"/> for the self-rejection. Null where there
    /// is no object to name (a pooled round).</summary>
    public object? Source;
}

/// <summary>One fire call's scan context (the engine's scan struct): where the round leaves from,
/// how fast and how far it flies, what counts as in-cone, and who is firing. All world-space.</summary>
public struct AimScan
{
    /// <summary>World muzzle position the intercept is solved from.</summary>
    public Vector3 MuzzlePosition;

    /// <summary>The shooter's own world velocity, m/s, subtracted from each candidate's to give
    /// the solver the relative velocity it wants.</summary>
    public Vector3 ShooterVelocity;

    /// <summary>The plane's world forward axis. Alignment (and so the cone gate and the score) is
    /// measured against THIS, not against the muzzle axis.</summary>
    public Vector3 Forward;

    /// <summary>The shooter's team (<see cref="AimAssist.TeamOfPilot"/>).</summary>
    public int Team;

    /// <summary>The round's speed, m/s, the weapon's <c>VELOCITY</c>.</summary>
    public float Speed;

    /// <summary>The weapon's <c>RANGE²</c>: an intercept the round cannot reach inside it is
    /// rejected.</summary>
    public float RangeSquared;

    /// <summary>The weapon's acceptance cone (<see cref="AimAssist.WeaponConeCos"/>), overridden
    /// per candidate by <see cref="AimCandidate.ConeOverride"/>.</summary>
    public float ConeCos;

    /// <summary><c>sticky_bullet_dist_factor</c>, per metre. Ships at 0.0, which deletes the
    /// distance term outright, selection is then purely most-aligned, at any range inside
    /// <c>RANGE</c>. The executable's own default is 2.5e-4; the shipped data turns it off
    /// deliberately, so a tuning pass must not "restore" it.</summary>
    public float DistFactor;

    /// <summary>The shooter, matched by reference against each candidate's
    /// <see cref="AimCandidate.Source"/> so it never snaps onto itself. Null where the caller has
    /// no object to name (the suites).</summary>
    public object? Self;
}

/// <summary>The scan's winner: which list it came from, the intercept direction to fire along
/// (world), when the round would arrive, and the score it won on.</summary>
public struct AimScanResult
{
    /// <summary>False when nothing survived the gates, the "no target found" answer, which leaves
    /// the slot's target on the plane's forward axis.</summary>
    public bool Found;

    /// <summary>Which of the four lists the winner came from.</summary>
    public AimTargetKind Kind;

    /// <summary>World unit vector: the intercept direction the winner was scored on.</summary>
    public Vector3 Direction;

    /// <summary>Time of flight to the intercept, seconds.</summary>
    public float TimeOfFlight;

    /// <summary><c>alignment − distance × dist_factor</c>.</summary>
    public float Score;

    /// <summary>The winning candidate's <see cref="AimCandidate.Source"/>, or null.</summary>
    public object? Source;
}

/// <summary>The per-muzzle gun aim assist: slot state (above), the per-frame forget + catch-up
/// pass, the constant-velocity intercept solver (docs/org/aim-assist.md "The lead
/// solver, <c>FUN_00460e30</c>") and the candidate scan with its rejection gates and scorer
/// ("Scoring one candidate"), so the whole assist is one testable unit that
/// <see cref="Airframe.FlightController"/> calls into.</summary>
public static class AimAssist
{
    /// <summary>A candidate advertising no cone of its own (the engine's <c>+0x50 = −1.0f</c>, what
    /// every entity constructor writes): the firing weapon's <c>CANNON_SPREAD</c> cone applies.
    /// Any value <c>≥ 0</c> is an override, read as a half-angle in radians.</summary>
    public const float NoConeOverride = -1f;

    /// <summary>The unaffiliated team. The engine rejects a candidate whose team id matches the
    /// shooter's AND one where either side is 0, so this is "never a target", not "everyone's
    /// enemy".</summary>
    public const int NeutralTeam = 0;

    /// <summary>The player's side, id 1, the decoded turret convention (0 neutral, 1
    /// ally, 2+ enemy) carried into the team model:
    /// every human and every wingman is this team, regardless of pilot index. Use this rather than
    /// <see cref="TeamOfPilot"/>(0) wherever "the player's side" is a fixed identity, not a
    /// particular pilot's default.</summary>
    public const int PlayerTeam = 1;

    /// <summary>The team a selectable zeppelin sub-part takes when its record authors none, so it
    /// lands on the player's Enemy cycle rather than the Ally one
    /// (<c>ZeppelinRuntime.CollectTargetParts</c>, the only reader left).
    /// ⚠ A remake-only rule, and the SELECTION cycle's alone. Hostility does not use it: an
    /// unauthored world object is neutral there, which is the original's own fall-through
    /// (docs/org/targeting.md "World objects are in the same space, and are normally neutral").</summary>
    public const int WorldTeam = 100;

    /// <summary>Where the extra humans of a splitscreen <c>--vs</c> session sit, clear of every id
    /// the world data authors. CSVM-only: the original has no per-pilot team ladder.
    /// ⚠ Never collapse this back onto a raw pilot index. An authored <c>TEAM</c> id is the same
    /// integer at runtime (docs/org/targeting.md "The team space"), so player two would take the id
    /// the 22 no-<c>TEAM</c> emplacements default to and stop being engaged by them.</summary>
    public const int VersusTeamBand = 10;

    /// <summary>The proximity fuse that puts a round in flight on the assist's ordnance list: the
    /// engine tests the def's <c>DETONATION_DISTANCE²</c> against 0.01, i.e. a fuse longer than
    /// 0.1 m, the 13 ordnance carriers in docs/formats/weapons.md. (The engine also admits a round
    /// whose def sets secondary-block flag <c>0x20</c>; CSVM does not read that block, and
    /// <see cref="ProjectilePool.CollectFusedOrdnance"/> stands <c>TARGETABLE</c> in for it.)</summary>
    public const float MinFuseDistance = 0.1f;

    /// <summary>Local forward, the "no target found" answer a slot's target unwinds to.</summary>
    public static readonly Vector3 LocalForward = new(0f, 0f, -1f);

    // Two directions this close to parallel (or its negation) make Godot's
    // Vector3.Slerp throw "Argument is not normalized": its rotation axis comes
    // from a cross product that degenerates at 0° and 180° separation. FlightModel
    // guards its own VelocityDir slerp the same way, for the same crash, a plane holding
    // straight and level, or a gun line that has already caught up to its target, hits this
    // every frame.
    private const float ParallelDot = 0.999f;

    /// <summary>The per-frame forget + catch-up pass, run once per active slot (docs/org/aim-assist.md
    /// "The per-frame slot update"). Forget: past <paramref name="forgetInterval"/> seconds since
    /// last touched, the target unwinds to local forward. Catch-up: <see cref="GunAimSlot.Smoothed"/>
    /// slerps toward <see cref="GunAimSlot.Target"/> at <paramref name="catchupRate"/> per second.
    /// ⚠ Call this BEFORE this tick's fire outcome; the original restamps <c>lastUpdate</c> on every
    /// round fired, so the pass must see the pre-shot state.</summary>
    public static void Tick(GunAimSlot[] slots, double now, float dt, float forgetInterval, float catchupRate)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (!slots[i].Active)
            {
                continue;
            }
            ref var slot = ref slots[i];
            if (slot.LastUpdate + forgetInterval < now)
            {
                slot.Target = LocalForward;
                slot.LastUpdate = now + forgetInterval;
            }
            float t = catchupRate * dt;
            if (t >= 1f)
            {
                slot.Smoothed = slot.Target;
                continue;
            }
            float dot = slot.Smoothed.Dot(slot.Target);
            slot.Smoothed = dot > ParallelDot
                ? (slot.Smoothed + (slot.Target - slot.Smoothed) * t).Normalized()
                : dot < -ParallelDot
                    ? slot.Target // degenerate opposite case: snap rather than crash on Slerp
                    : slot.Smoothed.Slerp(slot.Target, t).Normalized();
        }
    }

    /// <summary>The constant-velocity intercept solver (<c>FUN_00460e30</c>, docs/org/aim-assist.md
    /// "The lead solver"): given the muzzle, the round's speed, the target position and its velocity
    /// RELATIVE to the shooter, finds the fire direction and time of flight. Frame-agnostic, every
    /// input in the same space, and the answer comes out in that space.
    /// ⚠ Solves via <c>u = 1/t</c>, not the textbook quadratic in <c>t</c>; the docs page has the
    /// derivation. Do not swap it back in.</summary>
    public static bool TryIntercept(Vector3 muzzlePos, float speed, Vector3 targetPos, Vector3 relVel,
        out Vector3 aimDir, out float t)
    {
        aimDir = Vector3.Zero;
        t = 0f;

        Vector3 displacement = targetPos - muzzlePos;
        float a = displacement.LengthSquared(); // u's leading coefficient, see the doc comment
        if (a < 1e-6f)
        {
            return false; // zero separation: nothing to aim at
        }
        float b = 2f * relVel.Dot(displacement);
        float c = relVel.LengthSquared() - speed * speed;
        float disc = b * b - 4f * a * c;
        if (disc < 0f)
        {
            return false; // no real root: the target outruns the round on every heading
        }

        // The stable (Citardauq) quadratic form: avoids subtracting near-equal quantities when b
        // and sqrt(disc) are close in magnitude, which the textbook (-b±sqrt(disc))/2a form does
        // not, do not replace this with the textbook formula.
        float sq = Mathf.Sqrt(disc);
        float q = -0.5f * (b + (b >= 0f ? sq : -sq));
        float u1 = q / a;
        float u2 = Mathf.IsZeroApprox(q) ? float.NaN : c / q;

        // u = 1/t, so only a positive u is a forward-time solution; the larger positive root is
        // the smaller t, the earliest intercept.
        float u = u1 > 0f && u2 > 0f ? Mathf.Max(u1, u2) : u1 > 0f ? u1 : u2 > 0f ? u2 : float.NaN;
        if (float.IsNaN(u) || u <= 0f)
        {
            return false; // both roots non-positive: the target outran the round in this geometry
        }

        t = 1f / u;
        Vector3 dir = relVel + displacement / t;
        if (dir.LengthSquared() < 1e-12f)
        {
            t = 0f;
            return false;
        }
        aimDir = dir.Normalized();
        return true;
    }

    /// <summary>The assist's acceptance cone for one weapon, as a cosine in the positive-alignment
    /// convention (the engine stores <c>−cos</c> and tests both sides negated; docs/org/aim-assist.md
    /// "Sign convention", do not mix halves of the two). <c>CANNON_SPREAD</c> is a HALF-angle in
    /// degrees, 6.0 on every stock gun. An absent key is the whole forward hemisphere, not a
    /// built-in cone: the def's secondary block is <c>calloc</c>ed, so the slot keeps 0.0 =
    /// <c>−cos(90°)</c>.</summary>
    public static float WeaponConeCos(WeaponDef weapon) =>
        Mathf.Cos(Mathf.DegToRad(weapon.CannonSpread ?? 90f));

    /// <summary>The cone one candidate is judged against: its own <c>+0x50</c> override (a
    /// half-angle in RADIANS) when it advertises one, else the firing weapon's. No shipped entity
    /// sets it, the only authored writer is the turret key <c>STICKINESS</c>, which ships zero
    /// times, but the branch is ported anyway, since dropping it is a silent behaviour change the
    /// moment a mission authors one.</summary>
    public static float ConeCosFor(in AimCandidate candidate, float weaponConeCos) =>
        candidate.ConeOverride >= 0f ? Mathf.Cos(candidate.ConeOverride) : weaponConeCos;

    /// <summary>Whether two teams may shoot each other: the ids differ AND neither is
    /// <see cref="NeutralTeam"/>. Team 0 is not a wildcard, and the test is symmetric.
    /// The original runs this one predicate over one integer space for every combat object alike,
    /// aircraft, emplacement and world object (docs/org/targeting.md "The team space"), so every
    /// gate in CSVM asks it here rather than restating it.</summary>
    public static bool Hostile(int shooterTeam, int candidateTeam) =>
        shooterTeam != candidateTeam && shooterTeam != NeutralTeam && candidateTeam != NeutralTeam;

    /// <summary>The default team for a pilot index with no mission-assigned team: pilot 0 is
    /// <see cref="PlayerTeam"/>, every further pilot lands in <see cref="VersusTeamBand"/>, and a
    /// round nobody owns (<see cref="ProjectilePool.NoShooter"/>) is <see cref="NeutralTeam"/>.
    /// ⚠ Pilot 0 must keep <see cref="PlayerTeam"/>: the four authored <c>TEAM 1</c> emplacements
    /// are the player's own zeppelin's rings, and banding pilot 0 turns them on player one.</summary>
    public static int TeamOfPilot(int shooterId) => shooterId switch
    {
        < 0 => NeutralTeam,
        0 => PlayerTeam,
        _ => VersusTeamBand + shooterId,
    };

    /// <summary>The launch scatter (<c>FUN_00460940</c> → <c>FUN_004608a0</c>, docs/org/aim-assist.md
    /// "The scatter cone"), the only scatter the original applies to a player's round: rotate the aim
    /// direction about a perpendicular by <paramref name="inaccuracy"/> × a uniform [0,1).
    /// ⚠ The polar angle is uniform in <c>[0, inaccuracy]</c>, not over the cone's solid angle. Do
    /// not replace it with <c>sqrt(rand)</c> cap sampling, which piles rounds at the rim.</summary>
    public static Vector3 Scatter(Vector3 aimDir, float inaccuracy, RandomNumberGenerator rng)
    {
        if (inaccuracy <= 0f || aimDir.LengthSquared() < 1e-12f)
        {
            return aimDir;
        }
        Vector3 axis = aimDir.Normalized();
        // "Any perpendicular": cross with whichever world axis this direction is least aligned
        // with, so the cross never degenerates. Which one it is does not matter, the next line
        // rolls it to a uniform angle about the aim axis anyway.
        Vector3 seed = Mathf.Abs(axis.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        Vector3 perp = axis.Cross(seed).Normalized().Rotated(axis, rng.Randf() * Mathf.Tau);
        return axis.Rotated(perp, rng.Randf() * inaccuracy).Normalized();
    }

    /// <summary>One round's launch direction, in world space (<c>FUN_004b6530</c>'s whole step
    /// order): scan for a target and store the winner as the slot's new plane-local
    /// <see cref="GunAimSlot.Target"/>, then fire along the slot's plane-local
    /// <see cref="GunAimSlot.Smoothed"/> rotated back to world, scattered.
    /// ⚠ This frame's scan result is not what goes out; the smoothed value from previous frames is.
    /// Firing the scan result directly would remove the lag and read as an aimbot.</summary>
    public static Vector3 FireDirection(ref GunAimSlot slot, in AimScan scan,
        AimCandidateSet candidates, Basis planeBasis, double now, float inaccuracy,
        RandomNumberGenerator rng, out AimScanResult found)
    {
        Vector3 world = Scan(scan, candidates, out found) ? found.Direction : scan.Forward;
        var toLocal = planeBasis.Orthonormalized().Transposed(); // inverse of a rotation basis
        slot.Target = (toLocal * world).Normalized();
        slot.LastUpdate = now;
        Vector3 fired = (planeBasis.Orthonormalized() * slot.Smoothed).Normalized();
        return Scatter(fired, inaccuracy, rng);
    }

    /// <summary>The candidate scan (<c>FUN_004b6530</c>'s scan half, docs/org/aim-assist.md "The four
    /// lists"): one scorer over all four candidate lists, in the original's order, returning the
    /// highest-scoring survivor or false. Survivors rank by
    /// <c>alignment − distance × dist_factor</c>. Works in world space throughout; the caller rotates
    /// the winning direction into the slot's plane-local target.</summary>
    public static bool Scan(in AimScan scan, AimCandidateSet candidates, out AimScanResult best)
    {
        best = default;
        float bestScore = float.MinValue; // the engine's −FLT_MAX best-score seed
        ScoreList(scan, candidates.Vehicles, AimTargetKind.Vehicle, ref best, ref bestScore);
        ScoreList(scan, candidates.Turrets, AimTargetKind.Turret, ref best, ref bestScore);
        ScoreList(scan, candidates.Structures, AimTargetKind.Structure, ref best, ref bestScore);
        ScoreList(scan, candidates.Ordnance, AimTargetKind.Ordnance, ref best, ref bestScore);
        return best.Found;
    }

    // One list's pass. The engine runs four byte-identical scorers differing only in the container
    // accessor, so there is one scorer here and four calls, a list CSVM has not built yet iterates
    // nothing rather than being absent (M4 wires its turrets in, and the pass is already here).
    private static void ScoreList(in AimScan scan, List<AimCandidate> list, AimTargetKind kind,
        ref AimScanResult best, ref float bestScore)
    {
        foreach (var candidate in list)
        {
            if (scan.Self != null && ReferenceEquals(candidate.Source, scan.Self))
            {
                continue; // the shooter never snaps onto itself
            }
            if (!candidate.Live)
            {
                continue; // the engine's vtable +0x14 predicate: dead, or not live yet
            }
            if (!Hostile(scan.Team, candidate.Team))
            {
                continue;
            }
            if (!TryIntercept(scan.MuzzlePosition, scan.Speed, candidate.Position,
                    candidate.Velocity - scan.ShooterVelocity, out var dir, out float t))
            {
                continue; // a target outrunning the round is simply not assisted
            }
            float reach = scan.Speed * t;
            if (reach * reach > scan.RangeSquared)
            {
                continue; // the round could not reach the intercept point inside authored RANGE
            }
            float alignment = dir.Dot(scan.Forward);
            if (alignment < ConeCosFor(candidate, scan.ConeCos))
            {
                continue;
            }
            float score = alignment - scan.MuzzlePosition.DistanceTo(candidate.Position) * scan.DistFactor;
            if (score <= bestScore)
            {
                continue;
            }
            bestScore = score;
            best = new AimScanResult
            {
                Found = true,
                Kind = kind,
                Direction = dir,
                TimeOfFlight = t,
                Score = score,
                Source = candidate.Source,
            };
        }
    }
}

/// <summary>Everything one fire call may snap onto, kept as the engine's four lists rather than one
/// merged list: the scan order is theirs, and a list CSVM has not built yet must be EMPTY, not
/// absent, M4 wiring turrets in is then one <c>AddTurret</c> call and not a rediscovery of this
/// item. Reused across fire calls; <see cref="Clear"/> between them.</summary>
public sealed class AimCandidateSet
{
    /// <summary>Aircraft, and the AI ground/sea vehicles M4 adds. CSVM's live
    /// <see cref="Airframe.FlightController"/>s.</summary>
    public List<AimCandidate> Vehicles { get; } = new();

    /// <summary>Turrets, fed since M4 C9a/C9b by <see cref="ProjectilePool.CollectTurrets"/>: every
    /// registered aircraft's carried gunners (on their host's team) and every world emplacement (on
    /// its own). A dormant emplacement stays listed; only its death delists it as live.</summary>
    public List<AimCandidate> Turrets { get; } = new();

    /// <summary>The mission structures. CSVM's analogue is
    /// <see cref="DestructibleRegistry"/>, which is an APPROXIMATION and recorded as one: it is the
    /// shootable-world-object list, where the original's is the <c>targets.zrd</c> mission-structure
    /// list. (<c>MissionTargets</c> is not the analogue at all, objective display strings, nothing
    /// damageable.)</summary>
    public List<AimCandidate> Structures { get; } = new();

    /// <summary>Live proximity-fused rounds in flight, a filter over the live
    /// <see cref="ProjectilePool"/>, not a structure of its own.</summary>
    public List<AimCandidate> Ordnance { get; } = new();

    public void Clear()
    {
        Vehicles.Clear();
        Turrets.Clear();
        Structures.Clear();
        Ordnance.Clear();
    }

    public void AddVehicle(Vector3 position, Vector3 velocity, int team, bool live, object? source,
        float coneOverride = AimAssist.NoConeOverride) =>
        Vehicles.Add(Make(position, velocity, team, live, source, coneOverride));

    public void AddTurret(Vector3 position, Vector3 velocity, int team, bool live, object? source,
        float coneOverride = AimAssist.NoConeOverride) =>
        Turrets.Add(Make(position, velocity, team, live, source, coneOverride));

    public void AddStructure(Vector3 position, int team, bool live, object? source,
        float coneOverride = AimAssist.NoConeOverride) =>
        Structures.Add(Make(position, Vector3.Zero, team, live, source, coneOverride));

    public void AddOrdnance(Vector3 position, Vector3 velocity, int team, object? source,
        float coneOverride = AimAssist.NoConeOverride) =>
        Ordnance.Add(Make(position, velocity, team, live: true, source, coneOverride));

    /// <summary>Every registered destructible that is not already destroyed, as structure
    /// candidates. A pool carrying <see cref="DestructibleRegistry.Instance.Team"/> uses it; one
    /// carrying none falls through to <see cref="AimAssist.NeutralTeam"/> and is nobody's target,
    /// the original's own rule for a world object no data owns (docs/org/targeting.md). Skipped: an
    /// anchor outside the tree or hidden in it (a switched-off subtree is out of the world, as
    /// <see cref="TurretController.Alive"/> reads) and a dormant pool.</summary>
    public void AddStructures(DestructibleRegistry registry)
    {
        foreach (var inst in registry.All)
        {
            if (inst.Dormant || !inst.Anchor.IsInsideTree() || !inst.Anchor.IsVisibleInTree())
            {
                continue;
            }
            AddStructure(inst.Anchor.GlobalPosition, inst.Team ?? AimAssist.NeutralTeam,
                inst.Status != DestructibleRegistry.State.Destroyed, inst);
        }
    }

    /// <summary>The original's own <c>MStructList</c>, which is narrower than every destructible:
    /// the original builds one object per scene node carrying the mission-structure flag and sets
    /// the acquisition byte <c>+0x8d</c> on those alone, so a pool no mission structure stands on
    /// is not in the AI's list at all (docs/org/aiPilot.md, "What reaches the struct list"). The
    /// port's marker for that set is an authored team, since only a mission-structure node and a
    /// zeppelin record write one.</summary>
    public void AddMissionStructures(DestructibleRegistry registry)
    {
        foreach (var inst in registry.All)
        {
            if (inst.Team is not { } team || team == AimAssist.NeutralTeam)
            {
                continue;
            }
            if (inst.Dormant || !inst.Anchor.IsInsideTree() || !inst.Anchor.IsVisibleInTree())
            {
                continue;
            }
            AddStructure(inst.Anchor.GlobalPosition, team,
                inst.Status != DestructibleRegistry.State.Destroyed, inst);
        }
    }

    private static AimCandidate Make(Vector3 position, Vector3 velocity, int team, bool live,
        object? source, float coneOverride) =>
        new()
        {
            Position = position,
            Velocity = velocity,
            Team = team,
            Live = live,
            ConeOverride = coneOverride,
            Source = source,
        };
}

