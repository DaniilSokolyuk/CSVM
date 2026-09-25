using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight;
using CSVM.Net;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Generator aircraft launched by a host and built by a guest at the host's admission
/// ordinal. A host and a guest session share a lossy loopback on a stage with two generators.
/// Each end is credited differently, so a guest spawning off its own cycles would build a
/// different aircraft first. The rig is <see cref="NetCombatSuites.Ends"/>.
/// The first-seen position is the reading that tells the two apart.</summary>
internal static class NetAiSpawnSuites
{
    // C1/M02 authors two surface generators on separate airfields, eairg31 due at 15 s and eairg32
    // at 20 s (ind + wave). Each launches a roster-template aircraft down a take-off path.
    private const string Chapter = "C1";
    private const string Mission = "M02";
    private const string FirstGenerator = "eairg31";
    private const string SecondGenerator = "eairg32";

    private const ulong HostSeed = 0xA15E0001UL;

    // The most the two launches may take, in sim steps. That is the later generator's due time,
    // then the other's door lead once it is credited, with margin.
    private const int LaunchSteps = 40 * 60;

    // Steps between one event and the assertion on it, as in net-ai-world.
    private const int SettleSteps = 30;

    // How long both launches are flown for the tracking reading, in sim steps.
    private const int FlightSteps = 240;

    // The tracking alignment search and bar, net-ai-world's.
    private const int MaxLagSteps = 30;
    private const float MeanErrorBar = 3f;

    // How close the guest's copy must first stand to the host's launch point. The copy is placed
    // there by the launch and then moved by the host's samples of an aircraft leaving at rest.
    private const float LaunchToleranceM = 20f;

    [Suite("net-ai-spawn",
        "a host and a guest session on C1/M02 with generators over a 30 ms, 25 per cent lossy "
        + "loopback, the host launching off eairg32 and then eairg31 while the guest's own credit "
        + "comes due on eairg31 first: the guest refuses its own launch and builds the host's two at "
        + "the host's ordinals, each first seen "
        + "at the host's launch point and named as the host named it, then tracking the host's "
        + "path; a sample for an unadmitted ordinal admits nothing, and the host's deactivation of "
        + "an AI reaches the guest while a cutscene park does not")]
    internal static void GuestsBuildTheHostsLaunches(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");
        ctx.RequireData(SessionPaths.ChapterGamez(ctx.DataRoot, Chapter), $"{Chapter} gamez");
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        var spec = SessionSpec.Parse(new[]
        {
            "--fly", $"--chapter={Chapter}", $"--mission={Mission}", "--generators", "--players=1",
            "--mute", "--no-pads",
        });
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.03, 0.01, 0.25), new Random(4141));
        var roster = new NetSeat[]
        {
            new() { PeerId = 0, SeatIndex = 0, IsLocal = true, Callsign = "host", PlaneNode = "player_pfighter" },
            new() { PeerId = 1, SeatIndex = 1, Callsign = "guest", PlaneNode = "player_fbrand" },
        };
        NetSeats.Validate(roster);

        var ambient = NetCombatSuites.Ambient.Save();
        NetCombatSuites.Ends? host = null;
        NetCombatSuites.Ends? guest = null;
        try
        {
            host = NetCombatSuites.Ends.Open(ctx, spec, mesh[0], isHost: true, HostSeed, roster);
            guest = NetCombatSuites.Ends.Open(ctx, spec, mesh[1], isHost: false, HostSeed + 1, null);
            ctx.Check(host.Built && guest.Built,
                $"both sessions build in one process (host {host.Built}, guest {guest.Built})");
            if (!host.Built || !guest.Built)
            {
                return;
            }

            var hostGen = host.Session.Generators;
            var guestGen = guest.Session.Generators;
            ctx.Check(hostGen != null && guestGen != null && hostGen.LiveCount == 2 && guestGen.LiveCount == 2,
                $"both ends load {Chapter}/{Mission}'s two generators (host {hostGen?.LiveCount}, guest {guestGen?.LiveCount})");
            if (hostGen == null || guestGen == null)
            {
                return;
            }

            Lockstep(1, host.Session, guest.Session);
            var mine = host.Session.NetWorld!;
            var theirs = guest.Session.NetWorld!;
            int first = mine.Admitted;
            ctx.Check(theirs.Admitted == first && guestGen.Replicated && !hostGen.Replicated,
                $"both ends start from {first} admitted AI and only the guest replicates its generators");

            var launches = new List<(GeneratorAircraftLaunch Launch, FlightController Aircraft)>();
            hostGen.AircraftLaunched += (launch, aircraft) => launches.Add((launch, aircraft));
            int hostFed = hostGen.GrantWaveCapacity(SecondGenerator, 1);
            int guestFed = guestGen.GrantWaveCapacity(FirstGenerator, 1);
            ctx.Check(hostFed == 1 && guestFed == 1,
                $"the host credits '{SecondGenerator}' and the guest '{FirstGenerator}' (granted {hostFed}, {guestFed})");

            var seen = Launch(host.Session, guest.Session, first, launches);
            Launches(ctx, mine, theirs, hostGen, guestGen, launches, seen, first);
            if (launches.Count < 2 || theirs.Admitted < first + 2)
            {
                return;
            }

            Tracking(ctx, host.Session, guest.Session, first);
            Unknown(ctx, host.Session, guest.Session);
            Presence(ctx, host.Session, guest.Session, first);
        }
        finally
        {
            guest?.Close();
            host?.Close();
            ambient.Restore();
        }
    }

    // Steps both ends through the launches, noting where each new ordinal first stands on the guest.
    // The host's second airfield is credited once its first launch has flown. The host's order is
    // then the reverse of the order the guest's own credit would launch in.
    private static Dictionary<int, Vector3> Launch(GameSession host, GameSession guest, int first,
        List<(GeneratorAircraftLaunch Launch, FlightController Aircraft)> launches)
    {
        var seen = new Dictionary<int, Vector3>();
        var theirs = guest.NetWorld!;
        bool second = false;
        int settle = SettleSteps;
        for (int s = 0; s < LaunchSteps && settle > 0; s++)
        {
            if (launches.Count == 2)
            {
                settle--;
            }

            Lockstep(1, host, guest);
            if (!second && launches.Count == 1)
            {
                host.Generators!.GrantWaveCapacity(FirstGenerator, 1);
                second = true;
            }

            for (int i = first; i < theirs.Admitted; i++)
            {
                if (!seen.ContainsKey(i))
                {
                    seen[i] = theirs.AiAt(i)!.WorldPosition;
                }
            }
        }

        return seen;
    }

    private static void Launches(TestContext ctx, NetWorldLink mine, NetWorldLink theirs,
        AiGeneratorRuntime hostGen, AiGeneratorRuntime guestGen,
        List<(GeneratorAircraftLaunch Launch, FlightController Aircraft)> launches,
        Dictionary<int, Vector3> seen, int first)
    {
        ctx.Note($"launches: host sent {mine.SpawnsSent}, guest took {theirs.SpawnsTaken} and dropped {theirs.SpawnsRefused}, guest refused {guestGen.RefusedLaunches} of its own; launch counter host {hostGen.LaunchOrdinal}, guest {guestGen.LaunchOrdinal}");
        ctx.Check(launches.Count == 2 && mine.SpawnsSent == 2,
            $"the host launches one aircraft off each airfield and announces both ({launches.Count} launched, {mine.SpawnsSent} sent)");
        ctx.Check(guestGen.RefusedLaunches == 1 && hostGen.RefusedLaunches == 0,
            $"the guest's own '{FirstGenerator}' cycle came due and was refused, and the host refused none (guest {guestGen.RefusedLaunches}, host {hostGen.RefusedLaunches})");
        ctx.Check(theirs.SpawnsTaken == 2 && theirs.SpawnsRefused == 0 && mine.Admitted == first + 2 && theirs.Admitted == first + 2,
            $"the guest builds both host launches and nothing else (admitted host {mine.Admitted}, guest {theirs.Admitted}, taken {theirs.SpawnsTaken}, dropped {theirs.SpawnsRefused})");
        ctx.Check(guestGen.LaunchOrdinal == hostGen.LaunchOrdinal,
            $"and its launch counter stands where the host's does ({guestGen.LaunchOrdinal} against {hostGen.LaunchOrdinal})");
        if (launches.Count < 2 || theirs.Admitted < first + 2)
        {
            return;
        }

        var positions = launches.Select(l => l.Launch.Position).ToArray();
        float apart = positions[0].DistanceTo(positions[1]);
        for (int k = 0; k < 2; k++)
        {
            int ordinal = first + k;
            var owned = mine.AiAt(ordinal)!;
            var copy = theirs.AiAt(ordinal)!;
            int launched = launches.FindIndex(l => ReferenceEquals(l.Aircraft, owned));
            float right = launched >= 0 && seen.TryGetValue(ordinal, out var at) ? at.DistanceTo(positions[launched]) : float.MaxValue;
            ctx.Check(launched >= 0 && right < LaunchToleranceM,
                $"ordinal {ordinal}: the guest's copy is first seen {right:0.0} m from the host's launch point of '{owned.Name}'");
            ctx.Check(copy.Name == owned.Name && copy.RemoteOwned && !owned.RemoteOwned,
                $"ordinal {ordinal}: the guest's copy is named '{copy.Name}' as the host's '{owned.Name}' and is a replicated airframe");
        }

        // ABLE-TO-FAIL CONTROL. The two airfields stand far enough apart that a copy built off the
        // other generator fails the launch-point line above. The guest's own credit would have put
        // eairg31's launch at the first ordinal, which the host gave to eairg32's.
        ctx.Check(apart > LaunchToleranceM * 5f,
            $"ABLE-TO-FAIL CONTROL: the two launch points stand {apart:0} m apart");
    }

    // Both copies trace the host's own paths, and not each other's.
    private static void Tracking(TestContext ctx, GameSession host, GameSession guest, int first)
    {
        var mine = host.NetWorld!;
        var theirs = guest.NetWorld!;
        var hostPath = new[] { new List<Vector3>(), new List<Vector3>() };
        var guestPath = new[] { new List<Vector3>(), new List<Vector3>() };
        for (int s = 0; s < FlightSteps; s++)
        {
            Lockstep(1, host, guest);
            for (int k = 0; k < 2; k++)
            {
                hostPath[k].Add(mine.AiAt(first + k)!.WorldPosition);
                guestPath[k].Add(theirs.AiAt(first + k)!.WorldPosition);
            }
        }

        for (int k = 0; k < 2; k++)
        {
            float flown = hostPath[k][0].DistanceTo(hostPath[k][^1]);
            float right = Track(hostPath[k], guestPath[k]);
            float wrong = Track(hostPath[1 - k], guestPath[k]);
            ctx.Note($"ordinal {first + k} tracking: {right:0.00} m mean on its own path over {flown:0} m flown, {wrong:0} m against the other");
            ctx.Check(right < MeanErrorBar,
                $"ordinal {first + k}: the guest's copy traces the host's path to {right:0.00} m mean over {flown:0} m flown");
            ctx.Check(wrong > MeanErrorBar * 10f,
                $"ABLE-TO-FAIL CONTROL: ordinal {first + k} against the other launch's path reads {wrong:0} m");
        }
    }

    // A sample naming an ordinal the guest never admitted is dropped, and admits nothing.
    private static void Unknown(TestContext ctx, GameSession host, GameSession guest)
    {
        var theirs = guest.NetWorld!;
        int admitted = theirs.Admitted;
        ushort ordinal = (ushort)(admitted + 5);
        host.NetLink!.Broadcast(
            new AiStateMessage(ordinal, 0, new Vector3(0f, 500f, 0f), Quaternion.Identity, Vector3.Zero,
                0.5f, 0f, 0f, 0f, false),
            NetChannels.Events);
        Lockstep(SettleSteps, host, guest);
        ctx.Check(theirs.Admitted == admitted && theirs.AiAt(ordinal) == null,
            $"a sample for unadmitted ordinal {ordinal} admits nothing on the guest ({theirs.Admitted} admitted)");
    }

    // The host's deactivation and reactivation reach the guest's copy; a cutscene park does not.
    private static void Presence(TestContext ctx, GameSession host, GameSession guest, int first)
    {
        var owned = host.NetWorld!.AiAt(first)!;
        var copy = guest.NetWorld!.AiAt(first)!;
        int applied = guest.NetWorld.WorldEventsApplied;
        owned.Inert = true;
        Lockstep(SettleSteps, host, guest);
        bool followedOut = copy.Inert;
        owned.Inert = false;
        Lockstep(SettleSteps, host, guest);
        bool followedIn = !copy.Inert;
        ctx.Check(followedOut && followedIn,
            $"the host's deactivation of ordinal {first} reaches the guest, and so does its return (out {followedOut}, back {followedIn}, {guest.NetWorld.WorldEventsApplied - applied} event(s))");

        // ABLE-TO-FAIL CONTROL. A cutscene park sets the park before the inert bit, as the
        // cutscene host does. Each end's own cutscene parks its own copy, so nothing is sent.
        owned.Parked = true;
        owned.Inert = true;
        Lockstep(SettleSteps, host, guest);
        bool stayed = !copy.Inert;
        owned.Inert = false;
        owned.Parked = false;
        Lockstep(SettleSteps, host, guest);
        ctx.Check(stayed && !copy.Inert,
            $"ABLE-TO-FAIL CONTROL: a cutscene park of ordinal {first} on the host leaves the guest's copy in play ({(stayed ? "in play" : "inert")})");
    }

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

    private static void Lockstep(int steps, GameSession host, GameSession guest)
    {
        for (int i = 0; i < steps; i++)
        {
            host._PhysicsProcess(GameClock.FixedDt);
            guest._PhysicsProcess(GameClock.FixedDt);
        }
    }
}
