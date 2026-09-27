using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using CSVM.Session.Launch;
using CSVM.Session.World;
using CSVM.Tooling;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The host-owned world between whole sessions in one process. AI aircraft fly on the
/// host and replicate on the guest, and only the host spends a destructible pool. The rig is
/// <see cref="NetSessionSuites"/>'s, one session per peer under its own <see cref="SubViewport"/>
/// and <see cref="World3D"/>.</summary>
internal static class NetWorldSuites
{
    private const string MpMission = "MP1";

    private const ulong HostSeed = 0xA11CE501UL;

    // Two AI aircraft, so the tracking reading has a second one to fail against. No gunner:
    // the fire reading below pulls the host's trigger itself, so nothing depends on a target.
    private const string AiField = "--ai=player_pfighter:n=2";

    // Steps between one event and the assertion on it. A reliable payload crosses this link in a
    // handful; the rest is a death sequence settling.
    private const int SettleSteps = 30;

    // How long the AI are flown for the tracking reading, in sim steps.
    private const int FlightSteps = 180;

    // The alignment search for the tracking reading, in sim steps. It covers the buffer delay plus
    // the link's latency and jitter, as in net-aircraft-replication.
    private const int MaxLagSteps = 30;

    // The tracking bar, in metres, mean over the measured window at the best whole-step lag. A
    // whole-step grid leaves up to half a step of misalignment, about a metre at these speeds.
    private const float MeanErrorBar = 3f;

    // How far apart the two ends' own placements of one AI must be for the control to mean
    // anything, in metres. Each end places --ai off its own pane's seat.
    private const float PlacementGap = 50f;

    // The chip burst: four hits of half a per cent each, within one step. Two per cent of a pool
    // is meant to cross no damage stage, and the stage check in the reading fails if it does.
    private const int ChipHits = 4;

    private const float ChipFraction = 0.005f;

    private static readonly string[] Airframes = { "player_pfighter", "player_fbrand" };

    [Suite("net-ai-world",
        "a host and a guest with two AI aircraft over a 30 ms, 25 per cent lossy loopback: both "
        + "admit the same AI in the same order, the guest's are replicated airframes that trace the "
        + "host's paths rather than their own placement, the host's AI gunfire is spawned on the "
        + "guest, a guest's hit on an AI spends nothing there and lands on the host, a host AI "
        + "death reaches the guest, the guest's debug kill key kills an AI and a pool on the host "
        + "and nowhere first, a destructible dies on the guest only when the host kills it, and "
        + "a host burst that crosses no stage reaches the guest's pool as one sample")]
    internal static void TheHostOwnsTheWorld(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ctx.Chapter, MpMission);
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, ctx.Chapter), $"{ctx.Chapter} gamez");
        ctx.RequireData(missionZrdr, $"{ctx.Chapter}/{MpMission} zrdr");
        var spec = SessionSpec.Parse(new[]
        {
            "--vs", $"--chapter={ctx.Chapter}", $"--mission={MpMission}", "--players=1", "--mute",
            "--no-pads", AiField,
        });
        var weapons = WeaponDefs.Load(ctx.ZrdrPath);
        var gun = weapons.All.FirstOrDefault(w => w.IsCannon && w.HealthDamage is > 0f)
            ?? throw new SuiteSkippedException($"the weapon catalogue holds no cannon with health damage");

        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.03, 0.01, 0.25), new Random(7717));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = Airframes[0] },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = Airframes[1] },
        };
        NetSeats.Validate(roster);

        ulong master = Rng.Master;
        bool pinned = Rng.Pinned;
        var clockWas = GameClock.Current;
        var profileWas = StartupProfile.Current;
        Ends? host = null;
        Ends? guest = null;
        try
        {
            host = Ends.Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            guest = Ends.Open(ctx, spec, mesh[1], isHost: false, HostSeed + 1, null);
            ctx.Check(host.Built && guest.Built,
                $"both sessions build in one process (host {host.Built}, guest {guest.Built})");
            if (!host.Built || !guest.Built)
            {
                return;
            }

            Lockstep(1, host.Session, guest.Session);
            var mine = host.Session.NetWorld!;
            var theirs = guest.Session.NetWorld!;
            Admission(ctx, mine, theirs);
            if (mine.Admitted < 2 || theirs.Admitted < 2)
            {
                return;
            }

            Tracking(ctx, host.Session, guest.Session);
            Fire(ctx, host.Session, guest.Session);
            Hits(ctx, host.Session, guest.Session, gun);
            Deaths(ctx, host.Session, guest.Session);
            DebugKills(ctx, host.Session, guest.Session);
            Destructibles(ctx, host.Session, guest.Session);
        }
        finally
        {
            guest?.Close();
            host?.Close();
            StartupProfile.Current = profileWas;
            GameClock.Current = clockWas;
            Rng.Reset(master, pinned);
        }
    }

    // Both ends admitted the same AI in the same order, and only the guest's are replicated.
    private static void Admission(TestContext ctx, NetWorldLink mine, NetWorldLink theirs)
    {
        ctx.Check(mine.Admitted == 2 && theirs.Admitted == 2,
            $"both ends admit the two AI aircraft (host {mine.Admitted}, guest {theirs.Admitted})");
        bool sameIds = Enumerable.Range(0, Math.Min(mine.Admitted, theirs.Admitted))
            .All(i => mine.AiAt(i)!.PlayerIndex == theirs.AiAt(i)!.PlayerIndex);
        ctx.Check(sameIds,
            $"and in the same order, ordinal for ordinal ({Ids(mine)} against {Ids(theirs)})");
        ctx.Check(Enumerable.Range(0, theirs.Admitted).All(i => theirs.AiAt(i)!.RemoteOwned)
                  && Enumerable.Range(0, mine.Admitted).All(i => !mine.AiAt(i)!.RemoteOwned),
            $"every AI on the guest is a replicated airframe and none on the host is");
    }

    // The guest's AI follows the host's, and not the placement the guest gave it at its own build.
    private static void Tracking(TestContext ctx, GameSession host, GameSession guest)
    {
        var mine = host.NetWorld!;
        var theirs = guest.NetWorld!;
        float gap = theirs.AiAt(0)!.WorldPosition.DistanceTo(mine.AiAt(0)!.WorldPosition);
        var hostPath = new[] { new List<Vector3>(), new List<Vector3>() };
        var guestPath = new List<Vector3>();
        for (int i = 0; i < FlightSteps; i++)
        {
            Lockstep(1, host, guest);
            hostPath[0].Add(mine.AiAt(0)!.WorldPosition);
            hostPath[1].Add(mine.AiAt(1)!.WorldPosition);
            guestPath.Add(theirs.AiAt(0)!.WorldPosition);
        }

        float flown = hostPath[0][0].DistanceTo(hostPath[0][^1]);
        float right = Track(hostPath[0], guestPath);
        float wrong = Track(hostPath[1], guestPath);
        ctx.Note($"AI tracking: {right:0.00} m mean against the right path, {wrong:0.0} m against the other AI's, placement gap {gap:0} m");
        ctx.Check(flown > 100f && right < MeanErrorBar,
            $"the guest's AI traces the host's own path to {right:0.00} m mean over {flown:0} m flown");
        // ABLE-TO-FAIL CONTROL. Where the guest put that AI itself, and the same metric against the
        // other AI. A guest flying its own copy would sit on the first. A metric that cannot tell
        // two AI apart would pass the line above over the second.
        ctx.Check(gap > PlacementGap && wrong > right * 5f,
            $"ABLE-TO-FAIL CONTROL: the guest's own placement stood {gap:0} m off the host's, and against the other AI the metric reads {wrong:0.0} m");
    }

    // The host's AI fires; its rounds are built on the guest under that AI's shooter id.
    private static void Fire(TestContext ctx, GameSession host, GameSession guest)
    {
        var gunner = host.NetWorld!.AiAt(0)!;
        var copy = guest.NetWorld!.AiAt(0)!;
        var pool = guest.SeatRigs[1].Controller!.Projectiles!;
        pool.ScoredShooters.Add(copy.PlayerIndex);
        int before = pool.CannonRoundsFired;
        gunner.AutoFire = true;
        Lockstep(60, host, guest);
        gunner.AutoFire = false;
        Lockstep(SettleSteps, host, guest);
        int rounds = pool.CannonRoundsFired - before;
        ctx.Note($"AI fire: {rounds} round(s) spawned on the guest from the host's events");
        ctx.Check(rounds > 0,
            $"the host's AI gunfire is spawned on the guest under that AI's own shooter id ({rounds} round(s))");
    }

    // A guest's round on an AI is claimed to the host; the host spends it and the guest does not.
    private static void Hits(TestContext ctx, GameSession host, GameSession guest, WeaponDef gun)
    {
        var owned = host.NetWorld!.AiAt(0)!;
        var copy = guest.NetWorld!.AiAt(0)!;
        if (owned.Damage == null || copy.Damage == null)
        {
            ctx.Check(false, $"the AI airframe carries a damage ledger to read");
            return;
        }

        float ownedBefore = Ledger(owned);
        float copyBefore = Ledger(copy);
        int taken = host.NetWorld.AiHitsTaken;
        copy.Body!.TakeProjectileHit(gun, copy.WorldPosition, 0, guest.SeatRigs[1].Controller!.PlayerIndex);
        ctx.Check(Mathf.IsEqualApprox(Ledger(copy), copyBefore),
            $"a guest's hit on an AI spends nothing on the guest's copy ({Ledger(copy):0.0} of {copyBefore:0.0})");
        Lockstep(SettleSteps, host, guest);
        ctx.Check(host.NetWorld.AiHitsTaken == taken + 1 && Ledger(owned) < ownedBefore,
            $"and lands on the host's AI as one claim ({ownedBefore:0.0} to {Ledger(owned):0.0}, {host.NetWorld.AiHitsTaken - taken} claim(s))");

        // ABLE-TO-FAIL CONTROL. The same strike under the host seat's shooter id is the host's to
        // decide, so the guest sends nothing and the claim count stands.
        taken = host.NetWorld.AiHitsTaken;
        copy.Body.TakeProjectileHit(gun, copy.WorldPosition, 0, guest.SeatRigs[0].Controller!.PlayerIndex);
        Lockstep(SettleSteps, host, guest);
        ctx.Check(host.NetWorld.AiHitsTaken == taken,
            $"ABLE-TO-FAIL CONTROL: a round the guest did not fire is not claimed ({host.NetWorld.AiHitsTaken - taken} claim(s))");

        // The host's side of the same rule. A round of the guest's seat is the guest's to claim.
        float waiting = Ledger(owned);
        owned.Body!.TakeProjectileHit(gun, owned.WorldPosition, 0, host.SeatRigs[1].Controller!.PlayerIndex);
        ctx.Check(Mathf.IsEqualApprox(Ledger(owned), waiting),
            $"the host spends nothing for a round the guest fired, which the guest claims ({Ledger(owned):0.0} of {waiting:0.0})");
        owned.Body.TakeProjectileHit(gun, owned.WorldPosition, 0, host.SeatRigs[0].Controller!.PlayerIndex);
        ctx.Check(Ledger(owned) < waiting,
            $"ABLE-TO-FAIL CONTROL: the host's own round spends at once ({waiting:0.0} to {Ledger(owned):0.0})");
    }

    // An AI the host kills dies on the guest, and only that one.
    private static void Deaths(TestContext ctx, GameSession host, GameSession guest)
    {
        var victim = guest.NetWorld!.AiAt(1)!;
        int applied = guest.NetWorld.WorldEventsApplied;
        var other = guest.NetWorld.AiAt(0)!;
        // A ram on the guest is the guest's half of the contact alone; the AI's half is the host's.
        other.TakeCollisionHit(1e6f, 1e6f, other.WorldPosition, guest.SeatRigs[1].Controller!.PlayerIndex);
        ctx.Check(!victim.Crashed && !other.Crashed,
            $"the guest's copies of both AI are flying before the host kills one, a lethal ram on the guest included (crashed {victim.Crashed}, {other.Crashed})");
        host.NetWorld!.AiAt(1)!.DebugForceCrash(host.SeatRigs[0].Controller!.PlayerIndex);
        Lockstep(SettleSteps, host, guest);
        ctx.Check(victim.Crashed && !other.Crashed,
            $"the host's AI kill reaches the guest as that AI's death and no other's (crashed {victim.Crashed}, the other {other.Crashed}; {guest.NetWorld.WorldEventsApplied - applied} world event(s) applied)");
    }

    // The guest's debug kill key reaches the host, which kills the AI and the pool itself. The
    // guest's copies die from the host's broadcasts, never on the key press.
    private static void DebugKills(TestContext ctx, GameSession host, GameSession guest)
    {
        var pilot = guest.SeatRigs[1].Controller!;
        var key = new UI.Overlays.DebugKillTarget(() => pilot, () => guest.NetWorld!.World);
        try
        {
            ctx.Check(UI.Overlays.DebugKillTarget.LethalWeapon(pilot) != null,
                $"the guest's fit carries a weapon a lethal claim can name");
            var owned = host.NetWorld!.AiAt(0)!;
            var copy = guest.NetWorld!.AiAt(0)!;
            int taken = host.NetWorld.AiHitsTaken;
            key.KillSource(copy, "ai0", pilot.PlayerIndex);
            // ABLE-TO-FAIL CONTROL. A key that crashed the guest's copy locally fails here, and the
            // host's AI then flies on to fail the check after the settle.
            ctx.Check(!copy.Crashed && !owned.Crashed,
                $"ABLE-TO-FAIL CONTROL: the guest's key press crashes nothing on either end at once (guest copy {copy.Crashed}, host AI {owned.Crashed})");
            Lockstep(SettleSteps, host, guest);
            ctx.Check(owned.Crashed && host.NetWorld.AiHitsTaken == taken + 1,
                $"a guest's debug kill reaches the host as one claim and kills the host's AI (crashed {owned.Crashed}, {host.NetWorld.AiHitsTaken - taken} claim(s))");
            ctx.Check(copy.Crashed,
                $"and the guest's copy dies from the host's death broadcast (crashed {copy.Crashed})");
            DebugKillPool(ctx, host, guest, key, pilot.PlayerIndex);
        }
        finally
        {
            key.Free();
        }
    }

    // The same key on a pool: the guest claims it, and the host's kill comes back as its health.
    private static void DebugKillPool(TestContext ctx, GameSession host, GameSession guest,
        UI.Overlays.DebugKillTarget key, int killer)
    {
        var mineWorld = host.NetWorld!.World;
        var theirWorld = guest.NetWorld!.World;
        if (mineWorld == null || theirWorld == null)
        {
            ctx.Check(false, $"both ends build a world runtime to hold the pools");
            return;
        }

        int index = Enumerable.Range(0, Math.Min(mineWorld.Destructibles.All.Count, theirWorld.Destructibles.All.Count))
            .Where(i => Standing(mineWorld.Destructibles.All[i]) && Standing(theirWorld.Destructibles.All[i])
                        && NetWorldLink.PoolKey(mineWorld.Destructibles.All[i]) == NetWorldLink.PoolKey(theirWorld.Destructibles.All[i]))
            .DefaultIfEmpty(-1).First();
        if (index < 0)
        {
            ctx.Check(false, $"{ctx.Chapter}/{MpMission} registers a standing pool at a matching index on both ends");
            return;
        }

        var owned = mineWorld.Destructibles.All[index];
        var copy = theirWorld.Destructibles.All[index];
        int taken = host.NetWorld!.DestructibleHitsTaken;
        key.KillSource(copy, copy.Anchor.Name, killer);
        // ABLE-TO-FAIL CONTROL. The guest's pool is untouched until the host's health arrives.
        ctx.Check(copy.Status != DestructibleRegistry.State.Destroyed && owned.Status != DestructibleRegistry.State.Destroyed,
            $"ABLE-TO-FAIL CONTROL: the key press on '{copy.Anchor.Name}' kills it on neither end at once ({copy.Status} on the guest, {owned.Status} on the host)");
        Lockstep(SettleSteps, host, guest);
        ctx.Check(owned.Status == DestructibleRegistry.State.Destroyed && host.NetWorld.DestructibleHitsTaken == taken + 1,
            $"a guest's debug kill on a pool reaches the host as one claim and kills it there ({owned.Status}, {host.NetWorld.DestructibleHitsTaken - taken} claim(s))");
        ctx.Check(copy.Status == DestructibleRegistry.State.Destroyed,
            $"and the guest's pool dies from the host's event ({copy.Status}, HP {copy.Health:0})");
    }

    // A pool on the guest dies when the host kills it, and a guest's own hit spends nothing.
    private static void Destructibles(TestContext ctx, GameSession host, GameSession guest)
    {
        var mineWorld = host.NetWorld!.World;
        var theirWorld = guest.NetWorld!.World;
        if (mineWorld == null || theirWorld == null)
        {
            ctx.Check(false, $"both ends build a world runtime to hold the pools");
            return;
        }

        var live = Enumerable.Range(0, Math.Min(mineWorld.Destructibles.All.Count, theirWorld.Destructibles.All.Count))
            .Where(i => Standing(mineWorld.Destructibles.All[i]) && Standing(theirWorld.Destructibles.All[i])
                        && NetWorldLink.PoolKey(mineWorld.Destructibles.All[i]) == NetWorldLink.PoolKey(theirWorld.Destructibles.All[i]))
            .Take(2).ToArray();
        ctx.Check(live.Length == 2,
            $"{ctx.Chapter}/{MpMission} registers two standing pools at matching indices on both ends ({live.Length} found of {mineWorld.Destructibles.All.Count})");
        if (live.Length < 2)
        {
            return;
        }

        var killed = mineWorld.Destructibles.All[live[0]];
        var follower = theirWorld.Destructibles.All[live[0]];
        var spared = theirWorld.Destructibles.All[live[1]];
        var sparedOnHost = mineWorld.Destructibles.All[live[1]];

        // ABLE-TO-FAIL CONTROL first: the guest's own lethal hit on the second pool spends nothing.
        bool struck = theirWorld.DamageAt(spared.DamageNode, spared.MaxHealth * 10f);
        ctx.Check(struck && spared.Status != DestructibleRegistry.State.Destroyed
                  && Mathf.IsEqualApprox(spared.Health, spared.MaxHealth),
            $"ABLE-TO-FAIL CONTROL: a guest's own lethal hit lands on '{spared.Anchor.Name}' and spends nothing ({spared.Health:0} of {spared.MaxHealth:0}, {spared.Status})");

        mineWorld.DamageAt(killed.DamageNode, killed.MaxHealth * 10f);
        ctx.Check(killed.Status == DestructibleRegistry.State.Destroyed && follower.Status != DestructibleRegistry.State.Destroyed,
            $"the host kills '{killed.Anchor.Name}', which the guest has not yet heard ({follower.Status})");
        Lockstep(SettleSteps, host, guest);
        ctx.Check(follower.Status == DestructibleRegistry.State.Destroyed && follower.Health <= 0f,
            $"and the guest's pool dies from the host's event ({follower.Status}, HP {follower.Health:0})");
        ctx.Check(spared.Status != DestructibleRegistry.State.Destroyed && sparedOnHost.Status != DestructibleRegistry.State.Destroyed,
            $"while the pool nobody killed on the host stands on both ends ({sparedOnHost.Status} on the host, {spared.Status} on the guest)");
        Chips(ctx, host, guest, sparedOnHost, spared);
    }

    // A burst on the host that crosses no stage still reaches the guest's pool, as one sample.
    private static void Chips(TestContext ctx, GameSession host, GameSession guest,
        DestructibleRegistry.Instance owned, DestructibleRegistry.Instance copy)
    {
        var mineWorld = host.NetWorld!.World!;
        int stage = owned.DamageStage;
        int sent = host.NetWorld.ChipSamplesSent;
        float chip = owned.MaxHealth * ChipFraction;
        for (int i = 0; i < ChipHits; i++)
        {
            mineWorld.DamageAt(owned.DamageNode, chip);
        }

        ctx.Check(owned.DamageStage == stage && owned.Status != DestructibleRegistry.State.Destroyed
                  && owned.Health < owned.MaxHealth,
            $"the host's burst of {ChipHits} hits lowers '{owned.Anchor.Name}' without a stage change (HP {owned.Health:0.##} of {owned.MaxHealth:0.##}, stage {stage} to {owned.DamageStage})");
        // ABLE-TO-FAIL CONTROL. The guest's pool reads full until the sample arrives. The equality
        // below is then a value the wire moved, not one the two ends shared already.
        ctx.Check(!Mathf.IsEqualApprox(copy.Health, owned.Health),
            $"ABLE-TO-FAIL CONTROL: before the sample the guest's pool reads {copy.Health:0.##} against the host's {owned.Health:0.##}");
        Lockstep(SettleSteps, host, guest);
        int samples = host.NetWorld.ChipSamplesSent - sent;
        ctx.Note($"chip damage: {samples} sample(s) for {ChipHits} hits, guest HP {copy.Health:0.##} against the host's {owned.Health:0.##}");
        ctx.Check(Mathf.IsEqualApprox(copy.Health, owned.Health) && copy.DamageStage == owned.DamageStage
                  && copy.Status != DestructibleRegistry.State.Destroyed,
            $"the guest's pool takes the host's health between stages ({copy.Health:0.##} against {owned.Health:0.##}, stage {copy.DamageStage} against {owned.DamageStage})");
        ctx.Check(samples == 1,
            $"and the burst crossed the wire as one coalesced sample ({samples})");
    }

    private static bool Standing(DestructibleRegistry.Instance inst) =>
        !inst.Dormant && inst.Status == DestructibleRegistry.State.Healthy && inst.MaxHealth > 0f;

    // Mean distance between the guest's shown path and the host's own, at the best whole-step lag.
    private static float Track(IReadOnlyList<Vector3> own, IReadOnlyList<Vector3> shown)
    {
        float best = float.MaxValue;
        for (int lag = 0; lag <= MaxLagSteps; lag++)
        {
            float sum = 0f;
            int n = 0;
            for (int i = MaxLagSteps; i < shown.Count; i++)
            {
                sum += shown[i].DistanceTo(own[i - lag]);
                n++;
            }

            best = Mathf.Min(best, sum / n);
        }

        return best;
    }

    private static float Ledger(FlightController rig) =>
        rig.Damage!.WholeArmor + rig.Damage.WholeHealth;

    private static string Ids(NetWorldLink link) =>
        string.Join(",", Enumerable.Range(0, link.Admitted).Select(i => link.AiAt(i)!.PlayerIndex));

    private static void Lockstep(int steps, GameSession host, GameSession guest)
    {
        for (int i = 0; i < steps; i++)
        {
            host._PhysicsProcess(GameClock.FixedDt);
            guest._PhysicsProcess(GameClock.FixedDt);
        }
    }

    // One peer's whole rig: its own pane, its own world, its own session node.
    private sealed record Ends(SubViewport Pane, GameSession Session, bool Built)
    {
        public static Ends Open(TestContext ctx, SessionSpec spec, INetTransport transport,
            bool isHost, ulong seed, IReadOnlyList<NetSeat>? roster)
        {
            var pane = new SubViewport
            {
                Size = new Vector2I(640, 480),
                OwnWorld3D = true,
                World3D = new World3D(),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            };
            var camera = new Camera3D { Fov = 60f, Far = 20000f };
            var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-45, 150, 0) };
            pane.AddChild(camera);
            pane.AddChild(sun);
            ctx.Host.AddChild(pane);
            var session = new GameSession(spec, new LauncherContext
            {
                RepoRoot = ctx.RepoRoot,
                DataRoot = ctx.DataRoot,
                PlanesGamezPath = ctx.PlanesGamezPath,
                ZrdrPath = ctx.ZrdrPath,
                SoundsPath = ctx.SoundsPath,
                InterpPath = ctx.InterpPath,
                MessagesPath = ctx.MessagesPath,
                RofPath = System.IO.Path.Combine(ctx.DataRoot, "extracted", "rof"),
                ProbeRunner = new ProbeRunner(ctx.RepoRoot, ctx.DataRoot, ctx.ZrdrPath, ctx.SoundsPath,
                    ctx.InterpPath, ctx.MessagesPath, ctx.PlanesGamezPath),
                CaptureDirector = new CaptureDirector(spec),
                MasterSeed = seed,
                Camera = camera,
                Orbit = new UI.Overlays.OrbitCamera(camera),
                Sun = sun,
                Env = new Godot.Environment(),
                MenuDriven = false,
                MenuPads = null,
                Presentation = UI.Menu.PresentationId.BuiltIn,
                ExitSession = () => { },
                RestartSession = () => { },
                NetSeats = isHost ? roster : null,
                NetTransport = transport,
                NetHost = isHost,
                NetAirframes = Airframes,
            });
            pane.AddChild(session);
            return new Ends(pane, session, session.StartSession());
        }

        public void Close()
        {
            Session.Free();
            Pane.Free();
        }
    }
}
