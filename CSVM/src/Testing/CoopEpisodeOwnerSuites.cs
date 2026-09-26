using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The episode owner over CM02's own capture: with two humans flying, the airframe swap
/// the mission raises rebuilds the rig whose trigger started the episode rather than player 1's.
/// The mission's own definition is played through the runtime both ways, so the leg carries its
/// able-to-fail control: the same capture claimed by nobody swaps the scripted player, and claimed
/// by the guest swaps the guest and leaves the scripted player's aeroplane alone.</summary>
internal static class CoopEpisodeOwnerSuites
{
    // CM02's story position, and the two airframes the humans fly in on. Different records on
    // purpose: every reading below tells the two rigs apart by what they are flying.
    private const int Cm02Seq = 1;
    private const string ScriptedPlane = "player_bhawk";
    private const string GuestPlane = "player_pfighter";

    private const float StepDt = 1f / 60f;

    // Long enough to outlast the wing walk's own 19.25 s motion, which is what the capture's called
    // definition raises its swap behind.
    private const float PlayBudgetS = 30f;

    [Suite("campaign-coop-episode-owner",
        "the episode owner over CM02's own capture with two humans flying: the mission's swap "
        + "rebuilds the rig whose trigger claimed the episode, on the record the code names, "
        + "while the other human keeps the aeroplane it was flying; the swapped pilot keeps "
        + "their own shooter id and the pool's registration under it, the other pilot's is "
        + "untouched, and the same capture claimed by nobody swaps the scripted player")]
    internal static void CampaignCoopEpisodeOwner(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), Cm02Seq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {Cm02Seq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");

        var report = new StringBuilder();
        report.AppendLine($"seq {Cm02Seq} -> {chapter}/{folder}");
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder,
                world => Drive(ctx, world, chapter, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-campaign-coop-episode-owner-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"{chapter}/{folder}: the capture's swap follows the human whose trigger owns the episode");
    }

    internal static FlightController StageAi(FlightRoster roster, string name, string planeNode, Vector3 at)
    {
        var aim = at - Vector3.Forward;
        return roster.SpawnAi(new AiSpawn(planeNode, at, aim, AiPilot.HoldingCourse(at, aim),
            Team: AimAssist.PlayerTeam + 1, Inert: false, ShippedSkins: true, NodeName: name));
    }

    // With seats, every seat not flown here is built the way a session builds a guest's copy. Two
    // rosters in one process each need their own worldRoot. Godot renames a sibling whose name is
    // taken, and a roster finds its spawns by name.
    internal static FlightRoster BuildRoster(TestContext ctx, string chapter, TextureArchive textures,
        ProjectilePool pool, IReadOnlyList<PlayerRig> rigs, IReadOnlyList<Net.NetSeat>? seats = null,
        Node3D? worldRoot = null)
    {
        var spec = SessionSpec.Parse(new[] { $"--plane={ScriptedPlane},{GuestPlane}" });
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var resources = new AircraftAssemblyResources
        {
            PlanesGamez = planesGamez,
            StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
            AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
            CamParamsFor = _ => new CamParams(),
            PaintRng = new RandomNumberGenerator(),
            ZrdrPath = ctx.ZrdrPath,
            StockLoadouts = StockLoadouts.Load(),
            WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
            WeaponMessages = Messages.Load(ctx.MessagesPath),
            Textures = textures,
            Shakes = ShakeDefs.Load(ctx.ZrdrPath),
        };
        return new FlightRoster(FlightRosterPolicy.From(spec),
            new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
            new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), worldRoot ?? ctx.Host, resources,
            new FlightWorldBindings
            {
                Projectiles = pool,
                Gamez = planesGamez,
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter),
            },
            new HumanRosterBindings
            {
                RigCount = rigs.Count,
                Rigs = rigs,
                NetSeats = seats ?? Array.Empty<Net.NetSeat>(),
                PauseState = new PauseState(),
                MenuInputFor = _ => new MenuInput(),
                ExitSession = () => { },
            }, new CoopStarts());
    }

    private static void Drive(TestContext ctx, TestWorld world, string chapter, StringBuilder report)
    {
        var call = SwapCallIn(world.Session.Program)
            ?? throw new SuiteSkippedException($"{chapter} carries no airframe-swap callback to own");
        var wanted = AirframeSwapCodes.For(call.Code)!.Value;
        report.AppendLine($"'{call.Root}-{call.Anim}' authors callback {call.Code} -> '{wanted.PlaneNode}'");
        ctx.Check(!string.Equals(wanted.PlaneNode, ScriptedPlane, StringComparison.OrdinalIgnoreCase)
                  && !string.Equals(wanted.PlaneNode, GuestPlane, StringComparison.OrdinalIgnoreCase),
            $"the mission's swap names '{wanted.PlaneNode}', which neither human is already flying");

        WithTwoHumans(ctx, world, chapter, wanted, call,
            staged => Own(ctx, world, staged, wanted, call, guest: false, report));
        WithTwoHumans(ctx, world, chapter, wanted, call,
            staged => Own(ctx, world, staged, wanted, call, guest: true, report));
    }

    // One episode, claimed by the guest or by nobody, played the way a flown session plays it: the
    // slot is written before the definition starts, the runtime dispatches the code to the host, and
    // the seam the session fills sends the swap to the rig the order names.
    private static void Own(TestContext ctx, TestWorld world, Staged staged, AirframeSwapCode wanted,
        SwapCall call, bool guest, StringBuilder report)
    {
        var (roster, rigs, before, cutscene, pool) = staged;
        var owner = guest ? rigs[1] : rigs[0];
        var other = guest ? rigs[0] : rigs[1];
        string who = guest ? "the guest (P2)" : "nobody";
        // Read off the outgoing aircraft BEFORE the swap frees it, since every reading below is
        // about what carried across and the aeroplane it carried from is gone by then.
        var was = new Flying(before[owner.Index], before[other.Index]);
        var savedClock = GameClock.Current;
        var savedHost = world.Runtime.CallbackHost;
        PlayerRig? ordered = null;
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            // ⚠ The node table is indexed for the claimed leg alone, and that leg runs last: a leg
            // frees its aircraft on the way out, and a second indexing walk would then compare its
            // own keys against a disposed node. A session frees nothing mid-mission.
            cutscene.BindWorld(world.Runtime, guest ? world.Session.Aircraft : null);
            cutscene.HostDefinitions(ClosureOf(world, call.Anim));
            cutscene.BindRigs(rigs, () => roster.AiAircraft);
            // The session's own seam, and the whole subject of this suite: the order names the rig,
            // and an order naming none falls back to the scripted player's.
            cutscene.SwapAirframe = order =>
            {
                ordered = order.Owner;
                return roster.RunSwap(order.Owner ?? rigs[0], order, handsOver: false);
            };
            world.Runtime.CallbackHost = cutscene.Host;
            ctx.Check(ReferenceEquals(cutscene.EpisodeOwner, rigs[0]),
                $"with no trigger claimed, the host answers the scripted player as the episode owner");

            cutscene.Own(call.Anim, guest ? rigs[1] : null);
            world.Runtime.Play(call.Anim);
            bool swapped = Play(world, cutscene, owner, before[owner.Index]);

            report.AppendLine($"claimed by {who}: swapped={swapped} owner=" +
                $"P{(cutscene.EpisodeOwner?.Index ?? -1) + 1} ordered=" +
                $"P{(ordered?.Index ?? -1) + 1} codes {string.Join(",", cutscene.Codes)}");
            ctx.Check(swapped, $"playing '{call.Anim}' reaches callback {call.Code} through the runtime");
            ctx.Check(ReferenceEquals(cutscene.EpisodeOwner, owner),
                $"the episode claimed by {who} belongs to P{owner.Index + 1}");
            ctx.Check(ReferenceEquals(ordered, owner),
                $"…and the swap order carries that rig, so the seam is answered rather than guessed");
            CheckRigs(ctx, wanted, owner, other, before, was, report);
            CheckRegistrations(ctx, pool, was, report);
            if (guest)
            {
                CheckStaged(ctx, world.Session.Aircraft, owner, other, report);
            }
        }
        finally
        {
            world.Runtime.CallbackHost = savedHost;
            GameClock.Current = savedClock;
        }
    }

    // The owner flies the airframe the code named; the other human keeps their existing aeroplane.
    private static void CheckRigs(TestContext ctx, AirframeSwapCode wanted, PlayerRig owner,
        PlayerRig other, IReadOnlyList<FlightController> before, Flying was, StringBuilder report)
    {
        var after = owner.Controller;
        var stayed = other.Controller;
        report.AppendLine($"P{owner.Index + 1}: '{was.OwnerDef}' -> '{after?.Loadout?.Def.Def ?? "-"}'; " +
            $"P{other.Index + 1}: '{was.OtherDef}' -> '{stayed?.Loadout?.Def.Def ?? "-"}'");
        ctx.Check(after != null && !ReferenceEquals(after, before[owner.Index]),
            $"the owner's rig is rebuilt rather than the one that happened to be player 1");
        ctx.Check(string.Equals(after?.Loadout?.Def.Def, wanted.Def, StringComparison.OrdinalIgnoreCase),
            $"…on '{wanted.Def}', the record the code names");
        ctx.Check(ReferenceEquals(stayed, before[other.Index]),
            $"and P{other.Index + 1} is still flying the aircraft it was flying, not a rebuilt one");
        ctx.Check(stayed != null && GodotObject.IsInstanceValid(stayed) && stayed.IsInsideTree(),
            $"…still in the world, so the swap took one aeroplane out of the mission and no more");
        ctx.Same(was.OwnerId, after?.PlayerIndex ?? -1,
            $"the swapped pilot keeps their own shooter id, which is what a round already in the air scores to");
        ctx.Check(was.OwnerId != was.OtherId && (stayed?.PlayerIndex ?? -1) == was.OtherId,
            $"…and it is not the other human's, so the two pilots stay apart in the pool");
    }

    // The other thing the episode owner decides: which aeroplane a hookup definition resolves its
    // own hook, wings and mount offset off. It is the owner's replacement, not the aircraft player
    // one happens to be flying.
    private static void CheckStaged(TestContext ctx, AircraftStage? stage, PlayerRig owner,
        PlayerRig other, StringBuilder report)
    {
        report.AppendLine($"staged flown: " +
            $"'{(stage?.Flown is { } node ? AnimRuntime.NameOf(node) : "-")}'");
        ctx.Check(stage?.Flown != null && ReferenceEquals(stage.Flown, owner.Controller?.PlaneModel),
            $"the runtime's node table carries the episode owner's own airframe as the flown one");
        ctx.Check(!ReferenceEquals(stage?.Flown, other.Controller?.PlaneModel),
            $"…and not the other human's, which is the aeroplane a single-player mission would have staged");
    }

    // ⚠ The pool's shooter registrations are why the removal precedes the build in the roster's
    // swap: DetachRosterBindings drops the outgoing pilot's aircraft body and the replacement
    // registers its own under the same shooter id.
    private static void CheckRegistrations(TestContext ctx, ProjectilePool pool, Flying was,
        StringBuilder report)
    {
        var mine = pool.RigOfShooter(was.OwnerId);
        var theirs = pool.RigOfShooter(was.OtherId);
        bool Live(FlightController? rig) =>
            rig != null && GodotObject.IsInstanceValid(rig) && rig.IsInsideTree();
        report.AppendLine($"shooter ids: owner {(Live(mine) ? "live" : "missing")}, " +
            $"other {(Live(theirs) ? "live" : "missing")}");
        ctx.Check(Live(mine),
            $"the swapped pilot's shooter id resolves a live aircraft, so the replacement took the registration the outgoing rig gave up");
        ctx.Check(Live(theirs),
            $"and the human who kept their aeroplane keeps their own, untouched");
    }

    // The episode played out on a realtime clock, answering whether the owner's rig was replaced.
    // The whole budget is run rather than broken out of at the swap, so a second, later swap onto
    // the wrong rig would still be visible in the readings below.
    private static bool Play(TestWorld world, CutsceneController cutscene, PlayerRig owner,
        FlightController before)
    {
        bool swapped = false;
        for (int i = 0; i < (int)(PlayBudgetS / StepDt); i++)
        {
            GameClock.Current!.BeginFrame(StepDt);
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
            swapped |= !ReferenceEquals(owner.Controller, before);
        }

        return swapped;
    }

    // Two humans off the session's own roster, flying different airframes, over the mission's built
    // world. Each leg gets its own, because a swap replaces the rig it ran on.
    private static void WithTwoHumans(TestContext ctx, TestWorld world, string chapter,
        AirframeSwapCode wanted, SwapCall call, Action<Staged> leg)
    {
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var rigs = new[]
        {
            new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane },
            new PlayerRig { Index = 1, Camera = ctx.Camera, HudParent = pane, Viewport = pane },
        };
        FlightRoster? roster = null;
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        try
        {
            roster = BuildRoster(ctx, chapter, textures, pool, rigs);
            roster.BuildPlayers(rigs);
            var before = new List<FlightController>();
            foreach (var rig in rigs)
            {
                before.Add(rig.Controller
                    ?? throw new InvalidOperationException($"rig {rig.Index} was not built"));
            }

            // A real enemy roster spawn under the capture definition's own root, which is the only
            // thing the swap reads it by.
            StageAi(roster, call.Root, wanted.PlaneNode,
                before[0].WorldPosition + (before[0].NoseDirection * 300f));
            leg(new Staged(roster, rigs, before, cutscene, pool));
        }
        finally
        {
            // Only what this suite put on the host: the host is shared with every suite in the run.
            var live = new List<FlightController>();
            foreach (var rig in rigs)
            {
                if (rig.Controller is { } controller)
                {
                    live.Add(controller);
                }
            }

            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var plane in live)
            {
                plane.Free();
            }

            foreach (var ai in members)
            {
                ai.Free();
            }

            cutscene.Free();
            pane.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // The first definition in the mission's compiled program that raises a swap code, read out of
    // the shipped data rather than named here.
    private static SwapCall? SwapCallIn(AnimProgram program)
    {
        foreach (var def in program.Defs)
        {
            if (def.AnimName is not { } anim)
            {
                continue;
            }

            string root = def.RootName is { Length: > 0 } named ? named : def.Name;
            foreach (var sequence in def.Sequences)
            {
                foreach (var ev in sequence.Events)
                {
                    if (!string.Equals(ev.Kind, "Callback", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int code = (int)(ev.Data.Num("value") ?? -1f);
                    if (AirframeSwapCodes.For(code) != null)
                    {
                        return new SwapCall(anim, code, root);
                    }
                }
            }
        }

        return null;
    }

    // Every definition the capture can reach: what the host answers for, and the only way a called
    // definition's codes are hosted.
    private static IReadOnlyList<string> ClosureOf(TestWorld world, string anim)
    {
        var names = new List<string>();
        foreach (var def in world.Session.Program.Subset(new[] { anim }).Defs)
        {
            if (def.AnimName is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var mission in missions)
        {
            if (mission.Seq == seq)
            {
                return mission;
            }
        }

        return null;
    }

    // The definition that raises a swap code, the code, and the root node it is raised from.
    private readonly record struct SwapCall(string Anim, int Code, string Root);

    // What the two humans were flying, read before the swap frees the aeroplane it replaces.
    private readonly record struct Flying(int OwnerId, string OwnerDef, int OtherId, string OtherDef)
    {
        public Flying(FlightController owner, FlightController other)
            : this(owner.PlayerIndex, owner.Loadout?.Def.Def ?? "-",
                   other.PlayerIndex, other.Loadout?.Def.Def ?? "-")
        {
        }
    }

    // The actors one staged episode hands its leg: the session-shaped roster and its two rigs, what
    // each was flying before, the host, and the pool the registrations are counted in.
    private sealed record Staged(
        FlightRoster Roster,
        IReadOnlyList<PlayerRig> Rigs,
        IReadOnlyList<FlightController> Before,
        CutsceneController Cutscene,
        ProjectilePool Pool);

    // The abreast pair a co-op campaign start places, high enough over the mission's own terrain
    // that both aircraft are flying rather than resolving a ground contact.
    private sealed class CoopStarts : IFlightStarts
    {
        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = new Vector3(i * 60f, 1200f, 0f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 0.5f, 90f);
            }

            return starts;
        }
    }
}
