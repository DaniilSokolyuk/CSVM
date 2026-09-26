using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Bindings;
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

    // A pad id no machine enumerates, so the check reads the assignment and never a live device.
    private const int FirstPad = 901;

    private static readonly Binding Zed = new(DeviceId.Keyboard, BindingControl.Key((int)Key.Z));

    // What the stub stick source merges in, a control no shipped flight row uses.
    private static readonly Binding StickMarker = new(DeviceId.Keyboard, BindingControl.Key((int)Key.F12));

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
            flightRoster = NewRoster(ctx, spec, new Field(planesGamez, textures, pool, table, picker,
                chapterZrdr, missionZrdr), roster, rigs, match, padAssignment: null);
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

    [Suite("net-guest-keymap",
        "a network guest's own players fly this machine's keymaps: behind a remote host at seat 0, "
        + "the guest's first local player loads player one's saved file and the stick rows while its "
        + "second loads player two's, each reads the pads its local player was given, and the host's "
        + "seat reads no keymap file at all")]
    internal static void GuestKeymapIsLocal(TestContext ctx)
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

        // The guest's side of a match: the host flies seat 0 elsewhere, and this machine's two
        // players sit behind it at seats 1 and 2.
        var roster = new NetSeat[]
        {
            new() { PeerId = 1, SeatIndex = 0, Callsign = "host" },
            new() { PeerId = 2, SeatIndex = 1, IsLocal = true, Callsign = "P1" },
            new() { PeerId = 2, SeatIndex = 2, IsLocal = true, Callsign = "P2" },
        };
        NetSeats.Validate(roster);

        // ⚠ This opens the keymap gate --run-tests shuts, so the store points at scratch first and
        // both are restored in the finally, as bindings-launch-load does.
        string dir = Path.Combine(ctx.ScratchDir, "net-guest-keymap");
        Directory.CreateDirectory(dir);
        string? previousDir = BindingStore.DirectoryOverride;
        var previousRows = LaunchBindings.StickRows;
        BindingStore.DirectoryOverride = dir;
        WriteKeymap(dir, 1, InputAction.Nitro);
        WriteKeymap(dir, 2, InputAction.FireRockets);
        LaunchBindings.Configure(deterministic: false);
        LaunchBindings.StickRows = new MarkerStickRows();

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var panes = new[] { new SubViewport(), new SubViewport() };
        var rigs = new List<PlayerRig> { new() { Index = 0, Camera = null!, HudParent = ctx.Host } };
        for (int i = 0; i < panes.Length; i++)
        {
            ctx.Host.AddChild(panes[i]);
            var camera = new Camera3D();
            panes[i].AddChild(camera);
            rigs.Add(new() { Index = i + 1, Camera = camera, HudParent = panes[i], Viewport = panes[i] });
        }

        FlightRoster? flightRoster = null;
        try
        {
            var match = new VersusMatch(roster.Length, killTarget: 0, timeLimit: 0f);
            var pads = new[] { new[] { FirstPad }, new[] { FirstPad + 1 } };
            flightRoster = NewRoster(ctx, spec, new Field(planesGamez, textures, pool, table, picker,
                chapterZrdr, missionZrdr), roster, rigs, match, pads);
            flightRoster.BuildPlayers(rigs);
            var host = rigs[0].Controller!;
            var first = rigs[1].Controller!;
            var second = rigs[2].Controller!;

            ctx.Check(host.PlayerIndex == 0 && first.PlayerIndex == 1 && second.PlayerIndex == 2,
                $"every aircraft keeps its roster seat as its identity ({host.PlayerIndex}, {first.PlayerIndex}, {second.PlayerIndex})");
            ctx.Check(host.LocalPlayer == -1 && first.LocalPlayer == 0 && second.LocalPlayer == 1,
                $"and its local player is this machine's own count ({host.LocalPlayer}, {first.LocalPlayer}, {second.LocalPlayer})");

            // The fix itself. Unfixed, seat 1 flies player two's file and seat 2 a file nobody saved.
            ctx.Check(Holds(first, InputAction.Nitro, Zed) && !Holds(first, InputAction.FireRockets, Zed),
                $"the guest's first player flies player one's saved keymap, not player two's ({Names(first, InputAction.Nitro)}, {Names(first, InputAction.FireRockets)})");
            ctx.Check(Holds(first, InputAction.AutoLand, StickMarker),
                $"and takes player one's stick rows ({Names(first, InputAction.AutoLand)})");
            ctx.Check(Holds(second, InputAction.FireRockets, Zed) && !Holds(second, InputAction.Nitro, Zed)
                      && !Holds(second, InputAction.AutoLand, StickMarker),
                $"the guest's second player flies player two's keymap and no stick rows ({Names(second, InputAction.FireRockets)})");
            ctx.Check(first.PadDevices is [FirstPad] && second.PadDevices is [FirstPad + 1],
                $"each local player reads the pads it was given ([{string.Join(", ", first.PadDevices ?? Array.Empty<int>())}], [{string.Join(", ", second.PadDevices ?? Array.Empty<int>())}])");
            ctx.Check(!Holds(host, InputAction.Nitro, Zed) && !Holds(host, InputAction.AutoLand, StickMarker),
                $"the host's seat, flown elsewhere, reads neither a keymap file nor the sticks");

            // ABLE-TO-FAIL CONTROL: the two files differ, so a seat reading the wrong one shows it.
            ctx.Check(!Holds(first, InputAction.FireRockets, Zed) && Holds(second, InputAction.FireRockets, Zed),
                $"ABLE-TO-FAIL CONTROL: player two's rebound rockets reach only player two's seat");
        }
        finally
        {
            flightRoster?.ClearMembership();
            foreach (var rig in rigs)
            {
                rig.Controller?.Free();
            }

            foreach (var pane in panes)
            {
                pane.Free();
            }

            pool.Free();
            textures.Dispose();
            LaunchBindings.StickRows = previousRows;
            LaunchBindings.Configure(deterministic: true);
            BindingStore.DirectoryOverride = previousDir;
        }
    }

    private static FlightRoster NewRoster(TestContext ctx, SessionSpec spec, Field field,
        IReadOnlyList<NetSeat> roster, List<PlayerRig> rigs, VersusMatch match, int[][]? padAssignment) =>
        new(FlightRosterPolicy.From(spec),
            new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
            new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), ctx.Host,
            new AircraftAssemblyResources
            {
                PlanesGamez = field.PlanesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                CamParamsFor = _ => new CamParams(),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                WeaponMessages = Messages.Load(ctx.MessagesPath),
                Textures = field.Textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            },
            new FlightWorldBindings
            {
                Projectiles = field.Pool,
                Gamez = field.PlanesGamez,
                ChapterZrdrPath = field.ChapterZrdr,
                MissionZrdrPath = field.MissionZrdr,
            },
            new HumanRosterBindings
            {
                RigCount = rigs.Count,
                NetSeats = roster,
                Rigs = rigs,
                SpawnList = field.Table,
                SpawnBase = field.Picker.ChooseSpawnBase(field.Table),
                VersusMatch = match,
                PadAssignment = padAssignment,
                PauseState = new PauseState(),
                MenuInputFor = _ => new UI.Screens.MenuInput(),
                ExitSession = () => { },
            }, field.Picker);

    // One stored keymap with a single action moved onto Z, through the real serializer.
    private static void WriteKeymap(string dir, int player, InputAction action)
    {
        var profile = BindingProfile.Defaults(default, readsKeyboard: true);
        var map = profile.Map(InputContext.Flight);
        map.Clear(action);
        map.Add(action, Zed);
        File.WriteAllText(Path.Combine(dir, BindingStore.FileNameFor(player)),
            BindingStore.Serialize(1, profile), new UTF8Encoding(false));
    }

    private static bool Holds(FlightController pilot, InputAction action, Binding binding) =>
        pilot.FlightKeymap.Bindings(action).Any(held => ActionMap.SameControl(held, binding));

    private static string Names(FlightController pilot, InputAction action) =>
        $"{action}=[{string.Join(", ", pilot.FlightKeymap.Bindings(action).Select(BindingStore.Encode))}]";

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

    // The data one field is built from, read once per suite.
    private readonly record struct Field(GameZ PlanesGamez, TextureArchive Textures, ProjectilePool Pool,
        List<SpawnPoint> Table, SpawnPicker Picker, string ChapterZrdr, string MissionZrdr);

    // A stick source standing in for the live profiles: it adds one marker row, so a seat that took
    // player one's stick rows shows it.
    private sealed class MarkerStickRows : IStickRows
    {
        public void MergeInto(BindingProfile keymap) =>
            keymap.Map(InputContext.Flight).Add(InputAction.AutoLand, StickMarker);

        public void ResetInto(ActionMap reset, ActionMap staged, InputContext context)
        {
        }
    }
}
