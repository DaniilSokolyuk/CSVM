using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Net;
using CSVM.Session;
using CSVM.UI.Menu;
using CSVM.Utils;
using Godot;
using Ends = CSVM.Testing.NetCombatSuites.Ends;

namespace CSVM.Testing;

/// <summary>A campaign mission flown together, whole sessions and doors in one process over a
/// lossy loopback. Each end runs the door the menus hold and the session the launcher builds from
/// it, stepped the way the launcher steps them. The host's field and fits come from the
/// launcher's own helpers, so a claim here is about the path a player's launch takes.</summary>
internal static class NetCoopMissionSuites
{
    private const ulong HostSeed = 0xC0DE0C24UL;

    // Steps each end is given for a reliable word to land, on a link with 30 ms of latency.
    private const int SettleSteps = 30;

    // The longest a doomed field is given to reach its ending, wrecks falling included.
    private const int EndingSteps = 60 * 90;

    // The longest a guest's door is stepped for its host's opener to arrive.
    private const int OpenerSteps = 600;

    private const string GuestName = "Lucy";

    // What each end flies. The host picks dumdum and Lucy magnesium, and the third end keeps the
    // starter's stock slug, so a seat's rounds say whose fit reached it.
    private static readonly int[] HostAmmo = { 1, 1, 1, 1 };
    private static readonly int[] LucyAmmo = { 3, 3, 3, 3 };

    [Suite("net-coop-mission",
        "a co-op campaign mission flown by a host and two guests, one process, lossy loopback: each "
        + "guest is seated under its player name or its player number, every machine builds each "
        + "seat with its own pilot's ammunition, a guest's director has no profile store and the "
        + "user's profiles are untouched, a guest walking out through its pause sheet leaves at "
        + "once, the host alone ends the mission and a guest holds its result, Retry clears every "
        + "Ready, and a dropped host ends the guest's flight with Host left the game")]
    internal static void ACoopMissionFlownTogether(TestContext ctx)
    {
        var mission = Mission(ctx);
        var stock = StockLoadouts.Load();
        string profiles = Path.Combine(ProjectSettings.GlobalizePath("user://"), "Profiles");
        var before = Snapshot(profiles);

        var mesh = LoopbackTransport.Mesh(3, new LoopbackConditions(0.03, 0.01, 0.25), new Random(2403));
        // The star a real host is: each guest links to the host alone.
        mesh[1].Disconnect(2);
        var host = Door(mesh[0]);
        var lucy = Door(mesh[1]);
        var third = Door(mesh[2]);
        lucy.PlayerName = GuestName;
        var ambient = NetCombatSuites.Ambient.Save();
        try
        {
            host.OpenCoopHost(NetPlayFeature.CoopHumans - 1);
            host.Offer(mission.Seq, "Host", 1);
            lucy.OpenJoin();
            third.OpenJoin();
            Pump(SettleSteps, host, lucy, third);
            ctx.Check(lucy.IsCoopGuest && third.IsCoopGuest && host.CoopGuests.Count == 2,
                $"both guests join the host's campaign door ({lucy.Stage}, {third.Stage}, {host.CoopGuests.Count} seated)");
            if (!lucy.IsCoopGuest || !third.IsCoopGuest)
            {
                return;
            }

            FirstFlight(ctx, mission, stock, mesh, host, lucy, third);
            SecondFlight(ctx, mission, stock, mesh, host, third);
        }
        finally
        {
            ambient.Restore();
            third.Discard();
            lucy.Discard();
            host.Discard();
        }

        var after = Snapshot(profiles);
        ctx.Check(before.Count == after.Count
                  && before.All(f => after.TryGetValue(f.Key, out var now) && now.AsSpan().SequenceEqual(f.Value)),
            $"no file under the user's profile store was written, added or removed ({before.Count} before, {after.Count} after)");
    }

    // Three seats: the fits and names reach every machine, Lucy walks out, and the host's loss
    // ends the mission on the guest that stayed.
    private static void FirstFlight(TestContext ctx, CampaignMission mission, StockLoadouts stock,
        IReadOnlyList<LoopbackTransport> mesh, NetPlayFeature host, NetPlayFeature lucy, NetPlayFeature third)
    {
        var lucyFit = CoopFit.Of(LucyAmmo, null);
        Select(host, mission);
        Pump(SettleSteps, host, lucy, third);
        lucy.PickCoop(NetPlayFeature.StarterAirframe, true, lucyFit);
        Pump(SettleSteps, host, lucy, third);
        ctx.Check(!host.CoopAllReady,
            $"ABLE-TO-FAIL CONTROL: one guest Ready holds the launch ({string.Join(", ", host.CoopGuests.Select(g => g.Ready))})");
        third.PickCoop(NetPlayFeature.StarterAirframe, true);
        Pump(SettleSteps, host, lucy, third);
        ctx.Check(host.CoopAllReady, $"both guests Ready opens the launch");

        var hostEnd = Launch(ctx, mission, stock, host, HostAmmo, out var seatFits);
        Ends? lucyEnd = null;
        Ends? thirdEnd = null;
        try
        {
            lucyEnd = Follow(ctx, mission, stock, lucy, lucyFit, hostEnd, host);
            thirdEnd = Follow(ctx, mission, stock, third, default, hostEnd, host);
            ctx.Check(hostEnd.Built && lucyEnd.Built && thirdEnd.Built,
                $"three campaign sessions build in one process (host {hostEnd.Built}, Lucy {lucyEnd.Built}, third {thirdEnd.Built})");
            if (!hostEnd.Built || !lucyEnd.Built || !thirdEnd.Built)
            {
                return;
            }

            var ends = new[] { hostEnd, lucyEnd, thirdEnd };
            var doors = new[] { host, lucy, third };
            // The mission's opening film holds the world, and nothing in this rig plays it out.
            foreach (var end in ends)
            {
                end.Session.Cutscene?.Skip();
            }

            Fly(SettleSteps, ends, doors);
            Starts(ctx, ends);
            Names(ctx, ends);
            Fits(ctx, ends, seatFits);
            Stores(ctx, lucyEnd, thirdEnd, mission);
            WalkOut(ctx, hostEnd, thirdEnd, lucyEnd, lucy, host, third);
            Lost(ctx, hostEnd, thirdEnd, host, third);
            RetryClearsReady(ctx, mission, hostEnd, thirdEnd, host, third);

            // Lucy leaves the game, which drops her from the next flight's field.
            lucy.Close();
            mesh[1].Disconnect(0);
            Pump(SettleSteps, host, third);
        }
        finally
        {
            thirdEnd?.Close();
            lucyEnd?.Close();
            hostEnd?.Close();
        }
    }

    // Two seats, and the host's link cut mid-flight.
    private static void SecondFlight(TestContext ctx, CampaignMission mission, StockLoadouts stock,
        IReadOnlyList<LoopbackTransport> mesh, NetPlayFeature host, NetPlayFeature third)
    {
        third.PickCoop(NetPlayFeature.StarterAirframe, true);
        Pump(SettleSteps, host, third);
        ctx.Check(host.CoopAllReady && host.CoopGuests.Count == 1,
            $"after the retry the one guest left is Ready again ({host.CoopGuests.Count} seated)");

        var hostEnd = Launch(ctx, mission, stock, host, HostAmmo, out _);
        Ends? thirdEnd = null;
        try
        {
            thirdEnd = Follow(ctx, mission, stock, third, default, hostEnd, host);
            if (!hostEnd.Built || !thirdEnd.Built)
            {
                ctx.Check(false, $"the retried flight builds (host {hostEnd.Built}, guest {thirdEnd.Built})");
                return;
            }

            var ends = new[] { hostEnd, thirdEnd };
            var doors = new[] { host, third };
            Fly(SettleSteps, ends, doors);
            ctx.Check(third.Stage == NetDoorStage.Joined && !Launcher.CoopGuestFlightOver(third),
                $"ABLE-TO-FAIL CONTROL: with the host on the wire the guest's flight goes on ({third.Stage})");
            ctx.Check(thirdEnd.Session.NetSeats[1].Callsign == "P2",
                $"a guest with no player name is seated under its player number in the new field ({thirdEnd.Session.NetSeats[1].Callsign})");

            mesh[0].Disconnect(2);
            Fly(SettleSteps, ends, doors);
            ctx.Check(third.Stage == NetDoorStage.Failed && third.Fault == CoopDoorText.HostLeft,
                $"a dropped host fails the guest's door with \"{CoopDoorText.HostLeft}\" ({third.Stage}, \"{third.Fault}\")");
            ctx.Check(Launcher.CoopGuestFlightOver(third),
                $"and the launcher's rule ends the guest's flight, which returns it to the Connection page");
            ctx.Check(hostEnd.Session.SeatRigs[1].Controller is { Inert: true }
                      && Shows(hostEnd, CoopDoorText.Left("P2")) && hostEnd.Session.Campaign?.Result == null,
                $"while the host takes the guest's seat out, says \"{CoopDoorText.Left("P2")}\", and flies on");
        }
        finally
        {
            thirdEnd?.Close();
            hostEnd.Close();
        }
    }

    // The field starts abreast on every machine, one grid slot a seat, so no two humans open inside
    // each other. A machine flying one seat of its own still lays out the whole field.
    private static void Starts(TestContext ctx, Ends[] ends)
    {
        var gaps = ends.Select(e => Enumerable.Range(1, e.Session.SeatRigs.Count - 1)
            .Select(s => Gap(e, s - 1, s)).Min()).ToArray();
        string reading = string.Join(", ", gaps.Select(g => $"{g:0} m"));
        ctx.Check(gaps.All(g => g > StartGrid.SlotSpacingDefault * 0.5f),
            $"every machine opens its three seats at least half a grid slot apart (closest pair {reading})");
        ctx.Check(ends.All(e => e.Session.SeatRigs.All(r => r.Controller is { InPlay: true })),
            $"and no human is down anywhere once the opening film is skipped");
    }

    private static float Gap(Ends end, int a, int b) =>
        end.Session.SeatRigs[a].Controller!.WorldPosition.DistanceTo(end.Session.SeatRigs[b].Controller!.WorldPosition);

    // Every machine seats Lucy under her own name. The third guest, who has none, is seated
    // under its player number.
    private static void Names(TestContext ctx, Ends[] ends)
    {
        var names = ends.Select(e => string.Join(",", e.Session.NetSeats.Select(s => s.Callsign))).ToArray();
        ctx.Check(ends.All(e => e.Session.NetSeats.Count == 3
                                && e.Session.NetSeats[1].Callsign == GuestName
                                && e.Session.NetSeats[2].Callsign == "P3"),
            $"each machine seats the named guest as {GuestName} and the unnamed one as P3 ({string.Join(" | ", names)})");
    }

    // A seat's rounds on every machine are its own pilot's pick. The third guest flies stock, so
    // its slug is the control a fit that never arrived would also read.
    private static void Fits(TestContext ctx, Ends[] ends, CoopFit[] seatFits)
    {
        string[] want = { AmmoName(HostAmmo[0]), AmmoName(LucyAmmo[0]) };
        var read = ends.Select(e => Enumerable.Range(0, 3).Select(s => Ammo(e, s)).ToArray()).ToArray();
        string reading = string.Join(" | ", read.Select(r => string.Join(",", r)));
        ctx.Check(seatFits.Length == 3 && seatFits[1] == CoopFit.Of(LucyAmmo, null),
            $"the host's launch carries each seat's fit, Lucy's pick among them ({seatFits.Length} seat(s))");
        ctx.Check(read.All(r => r[0] == want[0] && r[1] == want[1]),
            $"every machine builds the host's seat on {want[0]} and Lucy's on {want[1]} ({reading})");
        ctx.Check(read.All(r => r[2] is { } stock && stock != want[0] && stock != want[1]),
            $"ABLE-TO-FAIL CONTROL: the stock seat reads neither pick on any machine ({reading})");
    }

    // A guest's director keeps its result in memory with no store behind it.
    private static void Stores(TestContext ctx, Ends lucy, Ends third, CampaignMission mission)
    {
        var guests = new[] { lucy.Session.Campaign, third.Session.Campaign };
        ctx.Check(guests.All(c => c is { HasStore: false, PilotName: CampaignDirector.CoopGuestPilot }),
            $"each guest's director flies as {CampaignDirector.CoopGuestPilot} with no profile store ({string.Join(", ", guests.Select(c => $"{c?.PilotName}/{c?.HasStore}"))})");

        // ABLE-TO-FAIL CONTROL: a director handed a store says so.
        string dir = Path.Combine(Path.GetTempPath(), $"csvm-coop-store-{System.Environment.ProcessId}");
        string zrdr = SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder.ToUpperInvariant(),
            mission.MissionFolder.ToUpperInvariant());
        var stored = CampaignDirector.Create(ObjectiveScript.Load(zrdr), mission,
            CampaignProfileDef.NewProfile("Control"), new CampaignProfileStore(dir));
        ctx.Check(stored.HasStore, $"ABLE-TO-FAIL CONTROL: a director over a store reports one");
    }

    // Lucy leaves through her pause sheet, in the launcher's order: her word, her session freed,
    // her wire back to her door. Her link stands, so her word is what takes her seat out.
    private static void WalkOut(TestContext ctx, Ends host, Ends third, Ends lucyEnd, NetPlayFeature lucy,
        NetPlayFeature hostDoor, NetPlayFeature thirdDoor)
    {
        ctx.Check(host.Session.SeatRigs[1].Controller is { Inert: false },
            $"ABLE-TO-FAIL CONTROL: Lucy's seat flies on the host before she leaves");
        lucy.LeaveCoopMission();
        lucyEnd.Close();
        lucy.Reclaim();
        int steps = 0;
        while (steps < SettleSteps && host.Session.SeatRigs[1].Controller is { Inert: false })
        {
            FlyOnce(new[] { host, third }, new[] { hostDoor, thirdDoor });
            lucy.Step(GameClock.FixedDt);
            steps++;
        }

        Fly(SettleSteps, new[] { host, third }, new[] { hostDoor, thirdDoor });
        ctx.Check(host.Session.SeatRigs[1].Controller is { Inert: true },
            $"the host takes Lucy's seat out {steps} step(s) after she leaves, with her link still up");
        ctx.Check(Shows(host, CoopDoorText.Left(GuestName)) && Shows(third, CoopDoorText.Left(GuestName)),
            $"and the host and the other guest both show \"{CoopDoorText.Left(GuestName)}\"");
        ctx.Check(third.Session.SeatRigs[1].Controller is { Inert: true }
                  && host.Session.Campaign?.Result == null && third.Session.Campaign?.Result == null,
            $"the other guest's copy of her aeroplane leaves too, and the mission goes on");
    }

    // The field is lost when its last flying human is. The guest going down first ends nothing,
    // and the host's loss then ends the mission on both machines.
    private static void Lost(TestContext ctx, Ends host, Ends third, NetPlayFeature hostDoor,
        NetPlayFeature thirdDoor)
    {
        var ends = new[] { host, third };
        var doors = new[] { hostDoor, thirdDoor };
        third.Session.SeatRigs[2].Controller!.DebugForceCrash();
        Fly(SettleSteps * 4, ends, doors);
        ctx.Check(host.Session.Campaign?.Result == null && third.Session.Campaign?.Result == null,
            $"ABLE-TO-FAIL CONTROL: the guest's own loss ends nothing while the host flies");

        host.Session.SeatRigs[0].Controller!.DebugForceCrash();
        int steps = 0;
        while (steps < EndingSteps && (host.Session.Campaign?.Result == null || third.Session.Campaign?.Result == null))
        {
            FlyOnce(ends, doors);
            steps++;
        }

        var mine = host.Session.Campaign?.Result;
        var theirs = third.Session.Campaign?.Result;
        ctx.Check(mine?.Outcome == MissionOutcome.Lost && theirs?.Outcome == MissionOutcome.Lost,
            $"the host's loss, with the seat that left not waited on, ends the mission Lost on both machines after {steps} step(s) (host {mine?.Outcome}, guest {theirs?.Outcome})");
    }

    // The host's debrief reaches the guest, and its Retry puts both back through selection.
    private static void RetryClearsReady(TestContext ctx, CampaignMission mission, Ends host, Ends third,
        NetPlayFeature hostDoor, NetPlayFeature thirdDoor)
    {
        var result = third.Session.Campaign?.Result;
        host.Close();
        hostDoor.Reclaim();
        hostDoor.ShowCoopResult(false, 0, 0);
        hostDoor.ShowCoop(NetCoopScreen.Debrief, mission.Seq, 0, 1 << NetPlayFeature.StarterAirframe);
        for (int i = 0; i < SettleSteps; i++)
        {
            third.Session._PhysicsProcess(GameClock.FixedDt);
            hostDoor.Step(GameClock.FixedDt);
            thirdDoor.Step(GameClock.FixedDt);
        }

        ctx.Check(Launcher.CoopGuestFlightOver(thirdDoor) && thirdDoor.CoopFlow is { Screen: NetCoopScreen.Debrief, Won: false },
            $"the guest's flight ends on the host's debrief, which names the loss ({thirdDoor.CoopFlow?.Screen}, won {thirdDoor.CoopFlow?.Won})");
        ctx.Check(result?.Outcome == MissionOutcome.Lost,
            $"and the guest carries the host's result to it ({result?.Outcome})");
        third.Close();
        thirdDoor.Reclaim();

        hostDoor.ShowCoop(NetCoopScreen.Briefing, mission.Seq, 0, 1 << NetPlayFeature.StarterAirframe);
        Pump(SettleSteps, hostDoor, thirdDoor);
        ctx.Check(thirdDoor.CoopFlow is { Screen: NetCoopScreen.Briefing } && !thirdDoor.CoopPickReady && !hostDoor.CoopAllReady,
            $"the host's Retry puts the guest back on the briefing with its Ready cleared ({thirdDoor.CoopFlow?.Screen}, ready {thirdDoor.CoopPickReady})");
        hostDoor.ShowCoop(NetCoopScreen.FlightCheck, mission.Seq, 0, 1 << NetPlayFeature.StarterAirframe);
    }

    // The host's launch: the door names the flight and the launcher's helper builds the field and
    // the fits. The fits go out before the session's opener.
    private static Ends Launch(TestContext ctx, CampaignMission mission, StockLoadouts stock,
        NetPlayFeature door, int[] ammo, out CoopFit[] seatFits)
    {
        var launch = door.BuildLaunch()!;
        var own = CampaignLoadout.For(CoopFit.Of(ammo, null), stock);
        var planes = new[] { UI.PlanePickerRoster.AirframeNode(NetPlayFeature.StarterAirframe) };
        (var roster, seatFits) = Launcher.CoopLaunchField(door, launch.Transport, planes, new[] { own }, stock);
        door.TellSeatFits(seatFits);
        var fits = seatFits;
        return NetCombatSuites.Ends.Open(ctx, Spec(mission, own), launch.Transport, isHost: true, HostSeed,
            roster, UI.PlanePickerRoster.StockAirframes, seat => Launcher.CoopSeatFitFor(seat, fits, null, stock));
    }

    // A guest's launch: its door waits for the host's opener while the host flies. It then builds a
    // session with no profile, its own fit on its own seat and the host's word on every other.
    private static Ends Follow(TestContext ctx, CampaignMission mission, StockLoadouts stock,
        NetPlayFeature door, CoopFit fit, NetCombatSuites.Ends host, NetPlayFeature hostDoor)
    {
        for (int i = 0; i < OpenerSteps && !door.CoopLaunchDue; i++)
        {
            if (host.Built)
            {
                host.Session._PhysicsProcess(GameClock.FixedDt);
            }

            hostDoor.Step(GameClock.FixedDt);
            door.Step(GameClock.FixedDt);
        }

        ctx.Check(door.CoopLaunchDue, $"a guest's door hears the host's opener");
        var launch = door.BuildLaunch()!;
        return NetCombatSuites.Ends.Open(ctx, Spec(mission, CampaignLoadout.For(fit, stock)), launch.Transport,
            isHost: false, HostSeed + 1, null, UI.PlanePickerRoster.StockAirframes,
            seat => Launcher.CoopSeatFitFor(seat, null, door, stock));
    }

    // A co-op launch with no profile behind it. Only a guest's is so in play, and here the host's
    // is too, so the suite writes nothing to the user's store.
    private static SessionSpec Spec(CampaignMission mission, LoadoutChoice? fit) =>
        SessionSpec.FromCampaign(SessionSpec.Parse(new[] { "--mute", "--no-pads" }), "", mission.Seq,
            new[] { UI.PlanePickerRoster.AirframeNode(NetPlayFeature.StarterAirframe) }, 1, new[] { fit });

    // The host's boards on the flight check of the suite's mission, with the starter offered.
    private static void Select(NetPlayFeature host, CampaignMission mission) =>
        host.ShowCoop(NetCoopScreen.FlightCheck, mission.Seq, 0, 1 << NetPlayFeature.StarterAirframe);

    private static CampaignMission Mission(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = CampaignSequence.Load(ctx.ZrdrPath).Cast<CampaignMission?>()
            .FirstOrDefault(m => m!.Value.ChapterFolder.Length > 0)
            ?? throw new SuiteSkippedException($"cm_sequence carries no mission with a chapter");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, chapter), $"{chapter} gamez");
        return mission;
    }

    private static NetPlayFeature Door(LoopbackTransport wire) =>
        new((_, _, _) => wire, (_, _) => wire);

    // Each door steps its own wire while no flight carries it.
    private static void Pump(int steps, params NetPlayFeature[] doors)
    {
        for (int i = 0; i < steps; i++)
        {
            foreach (var door in doors)
            {
                door.Step(GameClock.FixedDt);
            }
        }
    }

    // A flight's step on every end, the way the launcher runs one. The session's fixed step comes
    // first, then the door, then the host's word on any guest that walked out.
    private static void Fly(int steps, Ends[] ends, NetPlayFeature[] doors)
    {
        for (int i = 0; i < steps; i++)
        {
            FlyOnce(ends, doors);
        }
    }

    private static void FlyOnce(Ends[] ends, NetPlayFeature[] doors)
    {
        for (int e = 0; e < ends.Length; e++)
        {
            ends[e].Session._PhysicsProcess(GameClock.FixedDt);
            doors[e].Step(GameClock.FixedDt);
            if (doors[e].IsCoopHost)
            {
                foreach (var guest in doors[e].CoopGuests.Where(g => g.Left))
                {
                    ends[e].Session.TakeGuestLeft(guest.Peer);
                }
            }
        }
    }

    // The first gun's ammunition on one machine's copy of a seat.
    private static string? Ammo(Ends end, int seat) =>
        seat < end.Session.SeatRigs.Count && end.Session.SeatRigs[seat].Controller?.Loadout?.Def is { Guns.Count: > 0 } def
            ? def.Guns[0].Ammo
            : null;

    private static string AmmoName(int stored) => CampaignLoadout.AmmoNames[stored];

    // Whether any local pane's message stack shows the line.
    private static bool Shows(Ends end, string line) =>
        end.Session.Rigs.Any(r => r.Controller?.MessageStack is { } stack
            && Enumerable.Range(0, HudMessages.Slots).Any(s => stack.LineAt(s) == line));

    private static Dictionary<string, byte[]> Snapshot(string dir)
    {
        var files = new Dictionary<string, byte[]>();
        if (!Directory.Exists(dir))
        {
            return files;
        }

        foreach (string path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(dir, path)] = File.ReadAllBytes(path);
        }

        return files;
    }
}
