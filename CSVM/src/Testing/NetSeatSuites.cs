using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>A seat flown on another machine, built on the production roster seam. It is an
/// aircraft on the field with a spawn slot, a score row and a marker colour. It owns no pane, no
/// HUD in anybody's pane, no camera, no listener and no input device. The wiring is the one
/// <c>GameSession</c> builds for a network match, a <see cref="NetSeat"/> list on
/// <c>HumanRosterBindings</c> over the whole field's rigs.</summary>
internal static class NetSeatSuites
{
    private const string MpMission = "MP1";

    // The airframe the remote seat's roster entry names. Different from the launch's own pick, so
    // the assertion that the roster wins cannot be satisfied by the default.
    private const string RemotePlane = "player_fbrand";

    [Suite("net-seats",
        "a network match's remote seats are pilots without panes: the roster commits all three "
        + "seats in order, the spawn walk places each on its own table entry, the dogfight board "
        + "keeps a score row for a seat flown elsewhere and the rotation holds its opening entry, "
        + "the roster's own airframe pick beats this machine's launch flags, and every remote seat "
        + "is built with no HUD in a pane, no pad, no keyboard, no pause key, no target selection "
        + "and no camera-anchored cue, while the local seat in the same build has all of them")]
    internal static void RemoteSeatsWithoutPanes(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, ctx.Chapter);
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ctx.Chapter, MpMission);
        ctx.RequireData(texturesPath, $"{ctx.Chapter} textures");
        ctx.RequireData(chapterZrdr, $"{ctx.Chapter} zrdr");
        ctx.RequireData(missionZrdr, $"{ctx.Chapter}/{MpMission} zrdr");

        var spec = SessionSpec.Parse(new[]
        {
            "--vs", $"--chapter={ctx.Chapter}", $"--mission={MpMission}", "--players=1", "--spawn=0",
        });
        var picker = new SpawnPicker(spec);
        var table = picker.LoadSpawnList(missionZrdr, spec.Scenario);
        if (table is not { Count: >= 3 })
        {
            throw new SuiteSkippedException($"{ctx.Chapter}/{MpMission} authors no usable net.zrd table");
        }

        // Seat 0 is this machine's pane; seats 1 and 2 are flown elsewhere. Seat 1 names its own
        // airframe, which is how a peer's pick reaches this build.
        var roster = new NetSeat[]
        {
            new() { PeerId = 1, SeatIndex = 0, IsLocal = true, Callsign = "host" },
            new() { PeerId = 2, SeatIndex = 1, Callsign = "guest1", PlaneNode = RemotePlane },
            new() { PeerId = 3, SeatIndex = 2, Callsign = "guest2" },
        };
        NetSeats.Validate(roster);

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var rigs = new List<PlayerRig>
        {
            new() { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane },
            new() { Index = 1, Camera = null!, HudParent = ctx.Host },
            new() { Index = 2, Camera = null!, HudParent = ctx.Host },
        };
        FlightRoster? flightRoster = null;
        try
        {
            var match = new VersusMatch(roster.Length, killTarget: 0, timeLimit: 0f);
            flightRoster = new FlightRoster(FlightRosterPolicy.From(spec),
                new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
                new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), ctx.Host,
                new AircraftAssemblyResources
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
                },
                new FlightWorldBindings
                {
                    Projectiles = pool,
                    Gamez = planesGamez,
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                },
                new HumanRosterBindings
                {
                    RigCount = rigs.Count,
                    NetSeats = roster,
                    Rigs = rigs,
                    SpawnList = table,
                    SpawnBase = picker.ChooseSpawnBase(table),
                    VersusMatch = match,
                    PauseState = new PauseState(),
                    MenuInputFor = _ => new UI.Screens.MenuInput(),
                    ExitSession = () => { },
                }, picker);
            flightRoster.BuildPlayers(rigs);

            ctx.Same(rigs.Count, flightRoster.Humans.Count,
                $"the roster commits every seat in the match, panes and guests alike");
            var pilots = rigs.Select(rig => rig.Controller!).ToArray();
            ctx.Check(pilots.All(p => p != null) && pilots.Select((p, i) => p.PlayerIndex == i).All(ok => ok),
                $"each seat's aircraft carries its own seat index: {string.Join(", ", pilots.Select(p => p?.PlayerIndex))}");

            // The spawn walk: SpawnPicker was handed the seat count, and knows nothing about which
            // seats are flown here. A remote seat takes a table entry like any other.
            var entries = pilots.Select(p => EntryAt(table, p.GlobalPosition)).ToArray();
            ctx.Check(entries.All(i => i >= 0),
                $"every seat opens on a net table point (entries {string.Join(", ", entries)})");
            ctx.Check(new HashSet<int>(entries).Count == entries.Length,
                $"and no two seats share one (entries {string.Join(", ", entries)})");

            // The score rows and the respawn rotation are sized by the seat count. A kill by a
            // pilot nobody here watches is scored, and its seat has somewhere to come back to.
            match.RegisterKill(2, 1);
            ctx.Check(match.PlayerCount == roster.Length && match.KillsOf(2) == 1 && match.DeathsOf(1) == 1,
                $"the board keeps a row per seat: {match.PlayerCount} rows, kills(seat 2)={match.KillsOf(2)}, deaths(seat 1)={match.DeathsOf(1)}");
            var rotation = VersusSpawnRotation.For(table, picker.ChooseSpawnBase(table), rigs.Count,
                new Random(Rng.IntSeedFor(Rng.VersusSpawn)))!;
            ctx.Check(rotation.IndexOf(2) == entries[2],
                $"the rotation's opening ledger holds the remote seat's own entry ({rotation.IndexOf(2)} against {entries[2]})");

            ctx.Check(flightRoster.FlyingAirframeOf(1)?.PlaneNode == RemotePlane,
                $"the roster's airframe pick is what a remote seat flies ({flightRoster.FlyingAirframeOf(1)?.PlaneNode})");
            ctx.Check(roster.Select(s => s.Color).Distinct().Count() == roster.Length,
                $"and every seat, remote included, carries its own marker colour");

            var remotes = new[] { pilots[1], pilots[2] };
            ctx.Check(remotes.All(p => p.HudParent == null),
                $"a remote seat parents no HUD into a pane");
            ctx.Check(remotes.All(p => p.GetChildren().OfType<CanvasLayer>().All(c => c.GetParent() == p)),
                $"and the canvases it does build stay on its own node, out of every pane's tree");
            ctx.Check(remotes.All(p => !p.UseKeyboard && p.PadDevices is { Length: 0 } && !p.AllowPause),
                $"it reads no keyboard, no pad and no pause key on this machine");
            ctx.Check(remotes.All(p => p.Targeting == null && p.VersusHud == null
                                       && p.Photograph == null && p.SpeedCue == null),
                $"and nothing that needs a camera or a pane is built for it");
            ctx.Check(remotes.All(p => p.IsHumanPiloted),
                $"while it stays a person's aeroplane, not an AI one (the flight model's own force path)");

            // ABLE-TO-FAIL CONTROL: the local seat in this same build takes every one of those.
            // The assertions above cannot be passing because the roster built nothing at all.
            var local = pilots[0];
            ctx.Check(ReferenceEquals(local.HudParent, pane) && local.VersusHud != null
                      && local.Targeting != null && local.AllowPause && local.UseKeyboard,
                $"ABLE-TO-FAIL CONTROL: the pane in the same build has its HUD, board, targeting, pause key and keyboard");
            ctx.Check(pane.GetChildren().OfType<CanvasLayer>().Any(),
                $"ABLE-TO-FAIL CONTROL: and its own canvases are in the pane");

            ctx.Note($"{roster.Length} seats ({NetSeats.MaxPlayers} admitted, tables {NetSeats.SeatCapacity} wide), 1 pane, entries {string.Join(", ", entries)}");
        }
        finally
        {
            flightRoster?.ClearMembership();
            foreach (var rig in rigs)
            {
                rig.Controller?.Free();
            }
            pane.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // Which table entry a placed aircraft is standing on, or -1. The picker raises a start off the
    // ground under it, so the match is on the horizontal position alone.
    private static int EntryAt(IReadOnlyList<SpawnPoint> table, Vector3 pos)
    {
        for (int i = 0; i < table.Count; i++)
        {
            var d = table[i].Position - pos;
            if (Mathf.Abs(d.X) < 1f && Mathf.Abs(d.Z) < 1f)
            {
                return i;
            }
        }
        return -1;
    }
}
