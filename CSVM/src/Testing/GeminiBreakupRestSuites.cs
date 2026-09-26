using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The Gemini's own breakup over C2B/M04's sea, the hull no resting check covered.
/// <c>zeppelin-breakup</c> pins C1/M04's piratezep and <c>gemini-gasbag-bays</c> starts
/// <c>killgmzep</c> over a collision-less world where nothing can come to rest, so between them
/// they leave this ship's five sections untested against the water. Here the world carries
/// collision and the contact mask a real session wires, the hull's colliders ride its nodes as
/// they do in a session, the hull is killed by its gasbags, and the verdict is where the wreck and
/// all five sections stop and whether any section climbs back once it has reached the sea.</summary>
internal static class GeminiBreakupRestSuites
{
    private const string Chapter = "C2B";
    private const string Mission = "M04";
    private const string Hull = "geminizep";
    private const float Tick = 1f / 30f;

    // Long enough for floatdown's -3.5 descent to bring the hull inside main_altitude_check's
    // decoded -45.5 m probe from cruise, with room to spare; the loop leaves early once the gate
    // has opened and the choreography behind it has had its time.
    private const float SinkSeconds = 120f;

    private const float AfterOpenSeconds = 25f;

    // How many frames of the settle the artifact traces, per section and per frame.
    private const int TraceFrames = 150;

    // C2B/M04's sea, and how close to it a body has to stop to count as resting on it.
    private const float SurfaceY = 0f, RestBandM = 10f;

    // The most a section may climb between two frames once inside the band: the energy-tested
    // 0.2 restitution hop off the water is a few tenths of a metre, the column answering with the
    // section's own structure and lifting it back out of the sea was tens of metres.
    private const float HopM = 1f;

    // Where the halted hull may stand relative to its height on the frame the stop dispatched.
    private const float HaltToleranceM = 0.5f;

    // The three gasbags the kill goes through: survivors 2 < the record's required 3. The middle
    // three are chosen so the two END sections reach the water undamaged, which is the pair the
    // report is about.
    private static readonly int[] KilledBags = { 2, 3, 4 };

    [Suite("gemini-breakup-rest",
        "C2B/M04's geminizep killed by its gasbags over the sea, on a world whose hull colliders " +
        "ride their nodes as in a session: killgmzep's main_altitude_check gate opens as floatdown " +
        "brings the wreck down, its STOP_SEQUENCE floatdown halts the hull where it stands, " +
        "breakupzep drops all five sections, and every one comes to rest on the water, never " +
        "climbing more than a restitution hop once inside the rest band, and dispatches its own " +
        "hit_waterN splash there. zeppelin-breakup pins the six-section piratezep and " +
        "gemini-gasbag-bays runs this hull with no colliders at all, so this is the only resting " +
        "check the Gemini has")]
    internal static void GeminiBreakupRest(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: true, mission: Mission, world =>
        {
            var runtime = world.Runtime;
            var defs = Zeppelins.Load(missionZrdr);
            var nets = AiNets.Load(chapterZrdr);
            ZeppelinRuntime? zeps = null;
            uint maskWas = runtime.ContactMask;
            try
            {
                // A real session wires the mask from BuildsCollision; a suite world does not, and
                // with no mask both contact tiers are structurally off.
                runtime.ContactMask = CollisionLayers.World;
                runtime.SurfaceIsWater = body => ProjectilePool.SurfaceIsWater(body as Node);
                zeps = new ZeppelinRuntime(defs,
                    name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null, nets);
                zeps.WireDamage(runtime);
                Run(ctx, runtime, zeps, report);
            }
            finally
            {
                runtime.ContactMask = maskWas;
                runtime.SurfaceIsWater = null;
                zeps?.Free();
            }
        });

        ctx.WriteArtifact("test-gemini-breakup-rest.txt", report.ToString());
    }

    private static void Run(TestContext ctx, AnimRuntime runtime, ZeppelinRuntime zeps,
        StringBuilder report)
    {
        var hull = runtime.FindNodes(Hull).FirstOrDefault();
        ctx.Check(hull != null, $"the {Hull} world node resolves in the {Chapter}/{Mission} world");
        if (hull == null)
        {
            return;
        }

        var bags = new List<Node3D>();
        for (int i = 1; i <= 5; i++)
        {
            if (runtime.FindNodes($"gasbag{i}", hull).FirstOrDefault() is { } bag)
            {
                bags.Add(bag);
            }
        }

        ctx.Same(5, bags.Count, $"the five sections break1..break5 drop resolve under the hull");
        if (bags.Count != 5)
        {
            return;
        }

        // The record carries `deactivated 1`, so the mission script's WAKEUP_ENEMIES is what puts
        // its pools in play. Without it every zone pool is Dormant and refuses damage.
        ctx.Check(zeps.Wake(Hull), $"{Hull} starts dormant on its record and the mission wake puts it in play");
        for (int i = 0; i < 30; i++)
        {
            runtime.Advance(Tick);
        }

        var fired = new List<string>();
        float? haltedY = null;
        runtime.OnEventDispatched = d =>
        {
            if (d.Def.AnimName is "killgmzep")
            {
                string line = $"{d.Sequence}:{d.EventKind}({d.EventName})";
                fired.Add(line);
                if (line == "main_altitude_check:StopSequence(floatdown)")
                {
                    haltedY = hull.GlobalPosition.Y;
                }
            }
        };

        report.AppendLine($"hull built at ({hull.GlobalPosition.X:0},{hull.GlobalPosition.Y:0},{hull.GlobalPosition.Z:0})");
        foreach (int bag in KilledBags)
        {
            runtime.DamageAt(runtime.FindNodes($"gasbag{bag}", hull).FirstOrDefault(), 10_000f);
        }

        zeps.SimStep(Tick);
        runtime.Advance(Tick);
        ctx.Check(zeps.IsDead(Hull),
            $"gasbags {string.Join(", ", KilledBags)} down kills the hull by survivor count ({zeps.SurvivorsOf(Hull)} left)");

        int brokeAt = -1;
        float brokeY = 0f;
        float startY = hull.GlobalPosition.Y;
        var lastY = new float[bags.Count];
        var inBand = new bool[bags.Count];
        var maxClimb = new float[bags.Count];
        int steps = (int)(SinkSeconds / Tick);
        for (int i = 0; i < steps; i++)
        {
            runtime.Advance(Tick);
            zeps.SimStep(Tick);
            SyncColliders(hull);
            // Read off breakupzep's own dispatch, not the runtime's NODE_UNDERCOVER tally: that
            // tally is world-wide and any other definition's probe answers into it.
            if (brokeAt < 0 && fired.Contains("breakupzep:CallSequence(break1)"))
            {
                brokeAt = i;
                brokeY = hull.GlobalPosition.Y;
            }

            for (int n = 0; n < bags.Count; n++)
            {
                float y = bags[n].GlobalPosition.Y;
                if (inBand[n])
                {
                    maxClimb[n] = Mathf.Max(maxClimb[n], y - lastY[n]);
                }

                inBand[n] |= Mathf.Abs(y - SurfaceY) <= RestBandM;
                lastY[n] = y;
            }

            // Per frame through the settle, since which frame a section lands on relative to the
            // wreck's own is the whole reading. Bounded so the artifact stays a page, not a run.
            if (brokeAt >= 0 && i <= brokeAt + TraceFrames)
            {
                report.AppendLine($"t={i * Tick:0.00} hull.y={hull.GlobalPosition.Y:0.00} " +
                    string.Join(" ", bags.Select((b, n) => $"g{n + 1}={b.GlobalPosition.Y:0.00}")));
            }

            if (brokeAt >= 0 && i > brokeAt + (int)(AfterOpenSeconds / Tick))
            {
                break;
            }
        }

        runtime.OnEventDispatched = null;
        report.AppendLine($"the break starts at step {brokeAt} (hull y={brokeY:0.0}, {startY - brokeY:0.0} m below the kill)");
        foreach (string line in fired)
        {
            report.AppendLine($"killgmzep {line}");
        }

        int dropped = 0;
        int afloat = 0;
        int splashed = 0;
        int steady = 0;
        for (int i = 0; i < bags.Count; i++)
        {
            float restY = bags[i].GlobalPosition.Y;
            report.AppendLine($"gasbag{i + 1} rests at y={restY:0.0} on {SurfaceUnder(bags[i])}, " +
                $"largest climb inside the band {maxClimb[i]:0.00} m");
            dropped += fired.Contains($"break{i + 1}:ObjectMotion(gasbag{i + 1})") ? 1 : 0;
            afloat += Mathf.Abs(restY - SurfaceY) <= RestBandM ? 1 : 0;
            steady += inBand[i] && maxClimb[i] <= HopM ? 1 : 0;
            // break{i}'s BOUNCE_SEQUENCE names hit_water{i}, whose first event is the splash. The
            // two sections that land last are the ones a def-instance-scoped dispatch loses.
            splashed += fired.Contains($"hit_water{i + 1}:CallAnimation(huge_splash)") ? 1 : 0;
        }

        float wreckY = hull.GlobalPosition.Y;
        report.AppendLine($"wreck rests at y={wreckY:0.0}, floatdown halted at y={haltedY:0.0}");
        report.AppendLine($"column tier: {runtime.Motions.ColumnLandings} landed by contact, " +
            $"{runtime.Motions.ColumnClockEndings} ran a clock out; " +
            $"killgmzep state={runtime.AnimStateOf("killgmzep")}");

        ctx.Same(5, dropped, $"breakupzep drops all five sections dropped={dropped} of 5");
        ctx.Check(haltedY is { } h && Mathf.Abs(wreckY - h) <= HaltToleranceM,
            $"STOP_SEQUENCE floatdown ends the hull's flight where it stands wreck y={wreckY:0.0} halted y={haltedY:0.0}");
        ctx.Same(5, afloat,
            $"…and every section comes to rest on the sea rather than falling through it afloat={afloat} of 5");
        ctx.Same(5, steady,
            $"…and none climbs more than a {HopM:0} m restitution hop once inside the band steady={steady} of 5");
        ctx.Same(5, splashed,
            $"…and every one dispatches its own hit_waterN splash on landing splashed={splashed} of 5");
        ctx.Note($"{Chapter}/{Mission} {Hull}: wreck halted at y={wreckY:0.0}, {afloat} of 5 sections on the sea, {steady} of 5 steady, {splashed} of 5 splashing; per-step trace in test-gemini-breakup-rest.txt");
    }

    // A suite runs its ticks inside one engine frame, so a StaticBody3D's physics-server transform
    // never follows its moving node. A real session's does, and a hull whose colliders stay where
    // the world built them hides every question of what a section's own column answers with.
    private static void SyncColliders(Node n)
    {
        if (n is CollisionObject3D body)
        {
            PhysicsServer3D.BodySetState(body.GetRid(), PhysicsServer3D.BodyState.Transform, body.GlobalTransform);
        }

        foreach (var child in n.GetChildren())
        {
            SyncColliders(child);
        }
    }

    // What a settled section is standing on, read from above so the answer is the first surface
    // over it rather than the sea it may already be a few centimetres into. Reported, not asserted;
    // the splash count is what pins that the surface each section struck was the water.
    private static string SurfaceUnder(Node3D bag)
    {
        if (bag.GetWorld3D()?.DirectSpaceState is not { } space)
        {
            return "no collision world";
        }

        var above = bag.GlobalPosition + (Vector3.Up * 200f);
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            above, above + (Vector3.Down * 400f), CollisionLayers.World));
        if (hit.Count == 0)
        {
            return "nothing";
        }

        var struck = hit["collider"].As<Node>();
        return $"{struck?.Name} y={hit["position"].AsVector3().Y:0.00} " +
            $"water={ProjectilePool.SurfaceIsWater(struck)}";
    }
}
