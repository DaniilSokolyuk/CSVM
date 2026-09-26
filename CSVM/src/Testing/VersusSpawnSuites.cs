using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Where a Dogfight seat comes back, on real flight rigs: the fixed spawn a camper can
/// stand on, and the rotation that takes the seat somewhere else. The wiring is the one
/// <c>GameSession</c> builds in <c>--vs</c>, a <see cref="VersusSpawnRotation"/> over the chapter's
/// own <c>dogfight_ace</c> list feeding each rig's <c>RespawnPlacement</c>, so the suite exercises
/// the production seam rather than the chooser alone (<c>CSVM.Tests/VersusSpawnRotationTests.cs</c>
/// is the chooser's own coverage).</summary>
internal static class VersusSpawnSuites
{
    private const float StepDt = 1f / 60f;
    private const float RespawnDelay = 3f;
    private const string Scenario = "dogfight_ace";
    private const string MpMission = "MP1";

    [Suite("versus-spawn-rotation",
        "a downed dogfight seat does not come back to the point it was camped at: with no "
        + "placement wired it respawns on its fixed spawn (the control), and with the session's "
        + "rotation wired it takes a list point that is neither that one nor the one the living "
        + "opponent holds, far enough from the killer parked on its old spawn to clear the "
        + "roomiest candidate's share, and a second downing moves it again")]
    internal static void VersusSpawnRotationSeam(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter);
        ctx.RequireData(texturesPath, $"{ctx.Chapter} textures");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ctx.Chapter, "IA1");
        ctx.RequireData(missionZrdr, $"{ctx.Chapter}/IA1 zrdr");

        var spawns = SpawnPoints.LoadIa(missionZrdr, Scenario);
        if (spawns is not { Count: >= 4 })
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/IA1 authors no usable {Scenario} spawn list");
        }
        ctx.Check(spawns.Count >= 4,
            $"{ctx.Chapter}/IA1 ships {spawns.Count} {Scenario} spawns for the rotation to walk");

        var textures = new TextureArchive(texturesPath);
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var rigs = new List<FlightController>();
        try
        {
            for (int seat = 0; seat < 2; seat++)
                rigs.Add(Rig(ctx, planesGamez, textures, pool, stats, spawns[seat], seat));

            var victim = rigs[0];
            var camper = rigs[1];
            ctx.Check(victim.GlobalPosition.IsEqualApprox(spawns[0].Position)
                    && camper.GlobalPosition.IsEqualApprox(spawns[1].Position),
                $"both seats open on their own list entry, the walk SpawnPicker makes");

            // ABLE-TO-FAIL CONTROL: the same crash with nothing wired is the fixed-spawn return
            // this item exists to end, so the rotation's own assertions below cannot pass vacuously.
            victim.DebugForceCrash(killer: 1);
            FlyToRespawn(ctx, victim, "the unwired seat");
            ctx.Check(victim.GlobalPosition.IsEqualApprox(spawns[0].Position),
                $"ABLE-TO-FAIL CONTROL: with no placement wired the seat returns to its fixed spawn");

            // The session's own wiring, built exactly as the --vs block builds it.
            var match = new VersusMatch(2, killTarget: 0, timeLimit: 0f);
            var rotation = VersusSpawnRotation.For(spawns, 0, rigs.Count,
                new Random(Rng.IntSeedFor(Rng.VersusSpawn)))!;
            var lastKiller = new int?[rigs.Count];
            for (int seat = 0; seat < rigs.Count; seat++)
            {
                int own = seat;
                var pilot = rigs[seat];
                pilot.Match = match;
                pilot.RespawnPlacement = () => Placement(rotation, rigs, own, lastKiller[own]);
                pilot.Downed += (downed, killer) =>
                {
                    if (downed >= 0 && downed < lastKiller.Length)
                        lastKiller[downed] = killer;
                    if (killer is int k && k >= 0 && k < match.PlayerCount)
                        match.RegisterKill(k, downed);
                    else
                        match.RegisterDeath(downed);
                };
            }

            // The camp: the opponent parks on the victim's own spawn, which is what makes a fixed
            // return point unusable.
            camper.WarpTo(spawns[0].Position, spawns[0].HeadingDeg, 0f);
            victim.DebugForceCrash(killer: 1);
            ctx.Check(match.KillsOf(1) == 1 && match.DeathsOf(0) == 1,
                $"the camped kill is attributed kills(P2)={match.KillsOf(1)} deaths(P1)={match.DeathsOf(0)}");
            FlyToRespawn(ctx, victim, "the rotated seat");

            int landed = EntryAt(spawns, victim.GlobalPosition);
            ctx.Check(landed >= 0, $"the respawn landed on a {Scenario} list entry, not off the list");
            ctx.Check(landed != 0,
                $"the seat did not come back to the point it was downed at (entry {landed} of {spawns.Count})");
            ctx.Check(landed != rotation.IndexOf(1),
                $"nor to the entry the living opponent holds (entry {landed}, opponent on {rotation.IndexOf(1)})");

            // The pull away from the killer, measured against the list itself: the roomiest entry
            // the spacing rule leaves open, scaled by the share the rotation draws within.
            float roomiest = 0f;
            for (int i = 0; i < spawns.Count; i++)
                if (i != 0 && i != rotation.IndexOf(1))
                    roomiest = Mathf.Max(roomiest, spawns[i].Position.DistanceTo(camper.GlobalPosition));
            float away = victim.GlobalPosition.DistanceTo(camper.GlobalPosition);
            ctx.Check(away >= roomiest * VersusSpawnRotation.RoomyShare,
                $"and it is {away:0} m from the killer, within the {VersusSpawnRotation.RoomyShare:0.##} share of the roomiest open entry's {roomiest:0} m");

            // A second downing on the rotated point moves the seat again, which is the rotation
            // rather than one jump away from a single fixed spawn.
            victim.DebugForceCrash(killer: 1);
            FlyToRespawn(ctx, victim, "the twice-downed seat");
            int again = EntryAt(spawns, victim.GlobalPosition);
            ctx.Check(again >= 0 && again != landed,
                $"the next downing moves it once more (entry {landed} then {again})");
            ctx.Check(again != rotation.IndexOf(1),
                $"and still keeps the spacing rule against the living opponent (entry {again})");

            ctx.Note($"{spawns.Count} {Scenario} spawns, seat 1 camped on entry 0, respawns took entries {landed} then {again}");
        }
        finally
        {
            foreach (var rig in rigs)
                rig.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    [Suite("versus-spawn-net-table",
        "Dogfight on a multiplayer map opens on that map's own net.zrd spawn table: the picker "
        + "answers with the free-for-all block instead of falling back to the mission's single "
        + "PLAYER_INIT pose, all four seats land on distinct table entries at the original's own "
        + "multiplayer throttle and speed, the rotation's opening ledger is that same walk, and "
        + "the identical launch without --vs still reads no table")]
    internal static void VersusNetSpawnTable(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ctx.Chapter, MpMission);
        ctx.RequireData(missionZrdr, $"{ctx.Chapter}/{MpMission} zrdr");

        var spec = SessionSpec.Parse(new[]
        {
            "--vs", $"--chapter={ctx.Chapter}", $"--mission={MpMission}", "--players=4", "--spawn=0",
        });
        var picker = new SpawnPicker(spec);
        var table = picker.LoadSpawnList(missionZrdr, spec.Scenario);
        if (table is not { Count: > 0 })
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/{MpMission} authors no net.zrd table");
        }
        ctx.Check(picker.NetSpawns && table.Count == SpawnPoints.NetBlock,
            $"the picker answers {MpMission} with the net table's free-for-all block: {table.Count} entries, net={picker.NetSpawns}");

        var starts = picker.ChooseStarts(table, missionZrdr, picker.ChooseSpawnBase(table), 4);
        var seen = new List<int>();
        foreach (var start in starts)
            seen.Add(EntryAt(table, start.Pos));
        ctx.Check(seen.TrueForAll(i => i >= 0),
            $"every seat opens on a table point (entries {string.Join(", ", seen)})");
        ctx.Check(new HashSet<int>(seen).Count == seen.Count,
            $"and no two seats share one (entries {string.Join(", ", seen)})");

        var (throttle, speed) = picker.StartState(table, missionZrdr);
        ctx.Check(Mathf.IsEqualApprox(throttle, SpawnPoints.MultiplayerThrottleFrac)
                && Mathf.IsEqualApprox(speed, SpawnPoints.MultiplayerSpeedMps),
            $"on the original's multiplayer opening state, not PLAYER_INIT's: throttle={throttle:0.00} speed={speed:0.#}m/s");

        // The rotation is handed this list unchanged, so its opening ledger has to be the same
        // walk the seats were just placed by; nothing about its respawn rule moves for this mode.
        var rotation = VersusSpawnRotation.For(table, 0, 4, new Random(1))!;
        bool ledger = true;
        for (int seat = 0; seat < 4; seat++)
            ledger &= rotation.IndexOf(seat) == seen[seat];
        ctx.Check(ledger && rotation.PointCount == table.Count,
            $"the rotation opens on that same walk over all {rotation.PointCount} points");

        // ABLE-TO-FAIL CONTROL: the table is the Dogfight mode's alone, which is what keeps every
        // campaign mission's unread placeholder table out of every other launch.
        var flyPicker = new SpawnPicker(SessionSpec.Parse(new[]
        {
            "--fly", $"--chapter={ctx.Chapter}", $"--mission={MpMission}",
        }));
        ctx.Check(flyPicker.LoadSpawnList(missionZrdr, Scenario) == null && !flyPicker.NetSpawns,
            $"ABLE-TO-FAIL CONTROL: the same mission without --vs reads no table and falls back");

        ctx.Note($"{ctx.Chapter}/{MpMission}: {table.Count}-entry free-for-all block, seats on entries {string.Join(", ", seen)}");
    }

    // The placement closure GameSession installs: the living field read fresh at the respawn, so a
    // seat still on its crash camera neither holds a list entry nor pulls one away.
    private static (Vector3 Pos, Vector3 LookAt)? Placement(VersusSpawnRotation rotation,
        IReadOnlyList<FlightController> rigs, int seat, int? killer)
    {
        var field = new Vector3?[rigs.Count];
        for (int i = 0; i < rigs.Count; i++)
            field[i] = rigs[i] is { Crashed: false, Inert: false } flying ? flying.GlobalPosition : null;
        var point = rotation.Choose(seat, field, killer);
        return (point.Position, point.Position + point.Forward);
    }

    // Sim frames on the crash camera until the armed timer brings the seat back, with a margin
    // past the mark so a boundary frame does not read as a broken respawn.
    private static void FlyToRespawn(TestContext ctx, FlightController pilot, string what)
    {
        int steps = 0;
        int budget = (int)(RespawnDelay / StepDt) + 30;
        while (pilot.Crashed && steps < budget)
        {
            steps++;
            pilot.SimStep(StepDt);
        }
        ctx.Check(!pilot.Crashed, $"{what} flies again after {steps} of {budget} sim frames");
    }

    // Which list entry a pose sits on, or -1 for none. The respawn writes the entry's own position,
    // so the match is exact rather than a nearest-entry search.
    private static int EntryAt(IReadOnlyList<SpawnPoint> spawns, Vector3 at)
    {
        for (int i = 0; i < spawns.Count; i++)
            if (spawns[i].Position.IsEqualApprox(at))
                return i;
        return -1;
    }

    // One human seat on its own list entry, built the way the other flight suites build theirs: a
    // real aircraft node with real damage state, so a forced crash takes the production death path.
    private static FlightController Rig(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        ProjectilePool pool, PlaneStats stats, SpawnPoint at, int seat)
    {
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            PlayerIndex = seat,
            IsHumanPiloted = true,
            Projectiles = pool,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            AutoRespawnAfter = RespawnDelay,
            Name = $"VersusSpawnSeat{seat}",
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), null, new CamParams(), at.Position, at.Position + at.Forward);
        ctx.Host.AddChild(rig);
        return rig;
    }
}
