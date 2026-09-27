using System;
using System.Linq;
using CSVM.Bindings;
using CSVM.Flight;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Net;
using Godot;

namespace CSVM.Testing;

/// <summary>An aeroplane somebody else is flying, on this machine: a real
/// <see cref="FlightController"/> whose pose comes out of a received sample stream instead of the
/// flight model. The stream is scripted, so what the arm does with a late, a stopped and a
/// starved one is asserted rather than waited for. Every rule that would move the aeroplane
/// locally is checked to be off: the model step, the local trigger and the respawn button. Each
/// of the three is checked against an identical rig that still has it.</summary>
internal static class RemoteAirframeSuites
{
    private const float StepDt = 1f / 60f;

    // The scripted send cadence, and how long the stream runs before it is cut off.
    private const float SendInterval = 0.1f;
    private const float StreamSeconds = 2f;
    private const float SpawnAltitudeM = 800f;

    // The scripted flight: level, 120 m/s down -Z, yawing steadily. That makes the attitude read
    // a moving quantity, not a constant any wrong answer would also match.
    private const float YawRate = 0.2f;

    // The sender's own stick, carried by every sample: what reaches the surface animator here.
    private const float SentThrottle = 0.6f;
    private const float SentAileron = 0.25f;
    private const float SentElevator = -0.5f;
    private const float SentRudder = 0.75f;

    private static readonly Vector3 Cruise = new(0f, 0f, -120f);

    [Suite("remote-airframe",
        "an aeroplane flown from received samples: its pose tracks the scripted stream through "
        + "the interpolation delay, its attitude and velocity are the sender's, the sender's stick "
        + "reaches the control surfaces, a stream that stops holds one extrapolation cap past the "
        + "last sample and integrates no further, a hit still spends its damage ledger without "
        + "moving it, and neither this machine's gun trigger nor its respawn button reaches it -- "
        + "each against an identical locally flown rig that answers all three")]
    internal static void RemoteAirframe(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter);
        ctx.RequireData(texturesPath, $"{ctx.Chapter} textures");

        var textures = new TextureArchive(texturesPath);
        var planes = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);

        var received = new RemotePoseBuffer(SendInterval);
        var remoteSpawn = new Vector3(0f, SpawnAltitudeM, 0f);
        // Far enough from the remote rig that neither sweeps into the other while both fly.
        var localSpawn = new Vector3(4000f, SpawnAltitudeM, 0f);
        FlightController? remote = null;
        FlightController? local = null;
        try
        {
            remote = Rig(ctx, planes, textures, weapons, pool, remoteSpawn, received, 0, "RemoteRig");
            local = Rig(ctx, planes, textures, weapons, pool, localSpawn, null, 1, "LocalRig");
            // The pool counts a cannon round only for a shooter it scores. Both seats are scored
            // here, so a silent remote in the trigger leg is the gate and not the filter.
            pool.ScoredShooters.Add(remote.PlayerIndex);
            pool.ScoredShooters.Add(local.PlayerIndex);
            ctx.Check(remote.RemoteOwned && !local.RemoteOwned,
                $"the buffer is what makes the seat remote, and the rig beside it has none");

            int sent = Track(ctx, remote, received, remoteSpawn);
            Starve(ctx, remote, local, received, remoteSpawn, localSpawn, sent);
            Hit(ctx, remote, weapons);
            Triggers(ctx, remote, local, pool);
        }
        finally
        {
            remote?.Free();
            local?.Free();
            pool.Free();
            textures.Dispose();
        }

        ctx.Note($"a received pose flies the airframe and nothing local moves it");
    }

    // The tracking leg: samples arrive on the scripted cadence while the rig steps. Every step
    // past the first pair is compared against where the stream says the aeroplane was, one buffer
    // delay ago. Answers how many samples went in.
    private static int Track(TestContext ctx, FlightController remote, RemotePoseBuffer received,
        Vector3 spawn)
    {
        int sent = 0;
        float worstPos = 0f;
        float worstYaw = 0f;
        int compared = 0;
        for (int step = 0; step * StepDt < StreamSeconds; step++)
        {
            while (sent * SendInterval <= received.Now + 1e-4)
            {
                double at = sent * SendInterval;
                received.Add(SampleAt(spawn, (ushort)(sent + 1), at), at);
                sent++;
            }

            remote.SimStep(StepDt);
            double target = received.Now - RemotePoseBuffer.BufferDelaySeconds;
            if (target < SendInterval)
                continue;
            compared++;
            worstPos = Mathf.Max(worstPos, remote.WorldPosition.DistanceTo(PathAt(spawn, target)));
            worstYaw = Mathf.Max(worstYaw,
                Mathf.Abs(YawOf(remote.NoseDirection) - (YawRate * (float)target)));
        }

        ctx.Check(compared > 60 && worstPos < 0.5f,
            $"the pose tracks the stream over {compared} steps, worst error {worstPos:0.000} m");
        ctx.Check(worstYaw < 0.02f,
            $"…and so does the attitude, worst yaw error {Mathf.RadToDeg(worstYaw):0.00} deg");
        ctx.Check(remote.WorldVelocity.DistanceTo(Cruise) < 0.5f,
            $"…and the velocity is the sender's ({remote.WorldVelocity.Length():0.0} m/s), not one integrated here");
        var stick = remote.LastCommand;
        ctx.Check(Mathf.Abs(stick.Roll - SentAileron) < 0.01f
            && Mathf.Abs(stick.Pitch - SentElevator) < 0.01f
            && Mathf.Abs(stick.Yaw - SentRudder) < 0.01f
            && Mathf.Abs(stick.Throttle - SentThrottle) < 0.01f,
            $"…and the sender's own stick reaches the surfaces roll={stick.Roll:0.00} pitch={stick.Pitch:0.00} yaw={stick.Yaw:0.00} thr={stick.Throttle:0.00}");
        return sent;
    }

    // The leg where the stream stops: the answer rides the last velocity for one cap and then
    // holds, and nothing integrates it on from there. The locally flown rig is the control.
    private static void Starve(TestContext ctx, FlightController remote, FlightController local,
        RemotePoseBuffer received, Vector3 remoteSpawn, Vector3 localSpawn, int sent)
    {
        for (int step = 0; step < 60; step++)
            remote.SimStep(StepDt);

        double last = (sent - 1) * SendInterval;
        var capped = PathAt(remoteSpawn, last + RemotePoseBuffer.ExtrapolationCapSeconds);
        var held = remote.WorldPosition;
        ctx.Check(held.DistanceTo(capped) < 0.5f,
            $"a stream that stops holds one cap past its last sample, {held.DistanceTo(capped):0.000} m off it");

        for (int step = 0; step < 120; step++)
            remote.SimStep(StepDt);
        ctx.Check(remote.WorldPosition.DistanceTo(held) < 0.01f,
            $"…and two further seconds move it {remote.WorldPosition.DistanceTo(held):0.000} m: the flight model never integrates");
        ctx.Check(received.Count > 0 && Mathf.Abs(remote.WorldPosition.Y - SpawnAltitudeM) < 0.01f,
            $"…including downwards, so it is the samples flying it and not gravity");

        for (int step = 0; step < 180; step++)
            local.SimStep(StepDt);
        float flown = local.WorldPosition.DistanceTo(localSpawn);
        ctx.Check(flown > 100f,
            $"ABLE-TO-FAIL CONTROL: the same rig without a buffer flies its own model {flown:0} m over the same steps");
    }

    // Being shot is a local matter and stays live; where the aeroplane is, is not.
    private static void Hit(TestContext ctx, FlightController remote, WeaponDefs weapons)
    {
        if (remote.Damage is not { } ledger)
        {
            throw new SuiteSkippedException($"{remote.Name} carries no damage data to spend");
        }
        // Not a cannon round: the pilot's own warning-shot shield absorbs one of those. An
        // unspent ledger would then read as a gating bug rather than the shield working.
        var round = weapons.All.FirstOrDefault(w => !w.IsCannon && w.HealthDamage is > 0f);
        if (round == null)
        {
            throw new SuiteSkippedException($"no non-cannon weapon with HEALTH_DAMAGE in the data");
        }

        float before = ledger.WholeHealth + ledger.WholeArmor;
        var poseBefore = remote.WorldPosition;
        remote.TakeProjectileHit(round, remote.WorldPosition + new Vector3(1f, 0f, 0f), "fuselage", 3);
        remote.SimStep(StepDt);
        float after = ledger.WholeHealth + ledger.WholeArmor;

        ctx.Check(after < before,
            $"a hit on a remote airframe spends its ledger {before:0.0} to {after:0.0}");
        ctx.Check(remote.WorldPosition.DistanceTo(poseBefore) < 0.01f,
            $"…and moves it nowhere: the damage is this machine's, the pose is not");
    }

    // The two controls this machine must not reach a remote aeroplane with: the gun trigger and
    // the respawn button. Both are checked against the locally flown rig, which answers both.
    private static void Triggers(TestContext ctx, FlightController remote, FlightController local,
        ProjectilePool pool)
    {
        int before = pool.CannonRoundsFired;
        remote.AutoFire = true;
        for (int step = 0; step < 60; step++)
            remote.SimStep(StepDt);
        ctx.Check(pool.CannonRoundsFired == before,
            $"a held trigger here fires nothing off a remote airframe (rounds {pool.CannonRoundsFired})");

        local.AutoFire = true;
        for (int step = 0; step < 60; step++)
            local.SimStep(StepDt);
        ctx.Check(pool.CannonRoundsFired > before,
            $"ABLE-TO-FAIL CONTROL: the same trigger on the local rig fires {pool.CannonRoundsFired - before} rounds");

        remote.DebugForceCrash();
        ctx.Check(remote.Crashed, $"the remote airframe is down and its owner has not placed it yet");
        remote.HoldActionForTest(InputAction.Respawn, true);
        remote.SimStep(StepDt);
        ctx.Check(remote.Crashed,
            $"…and a held respawn here does not bring it back: the host places a remote seat");

        local.DebugForceCrash();
        local.HoldActionForTest(InputAction.Respawn, true);
        local.SimStep(StepDt);
        ctx.Check(!local.Crashed,
            $"ABLE-TO-FAIL CONTROL: the same hold flies the local rig again");
    }

    // One aircraft rig, built the way the flight suites build theirs, remote when it is handed a
    // buffer and locally flown when it is not.
    private static FlightController Rig(TestContext ctx, GameZ planes, TextureArchive textures,
        WeaponDefs weapons, ProjectilePool pool, Vector3 spawn, RemotePoseBuffer? received,
        int index, string name)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planes, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            PlayerIndex = index,
            IsHumanPiloted = true,
            Projectiles = pool,
            RemotePoses = received,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            InfiniteAmmo = true,
            Team = AimAssist.PlayerTeam,
            Name = name,
        };
        rig.AddChild(model);
        if (StockLoadouts.Load().For(stats.DefName) is { } stock)
            rig.Loadout = Loadout.Bind(stock, model, weapons);
        rig.Setup(new FlightModel(stats), null, new CamParams(), spawn, spawn + Vector3.Forward);
        ctx.Host.AddChild(rig);
        return rig;
    }

    private static Vector3 PathAt(Vector3 spawn, double t) => spawn + (Cruise * (float)t);

    // The heading the nose is on, +Y-handed like the scripted yaw: a nose of -Z is zero.
    private static float YawOf(Vector3 nose) => Mathf.Atan2(-nose.X, -nose.Z);

    private static AircraftStateMessage SampleAt(Vector3 spawn, ushort sequence, double t) => new(
        Seat: 1,
        Sequence: sequence,
        Position: PathAt(spawn, t),
        Attitude: new Quaternion(Vector3.Up, YawRate * (float)t),
        Velocity: Cruise,
        Throttle: SentThrottle,
        Aileron: SentAileron,
        Elevator: SentElevator,
        Rudder: SentRudder,
        Nitro: false);
}
