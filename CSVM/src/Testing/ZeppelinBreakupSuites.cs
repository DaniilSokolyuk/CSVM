using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The authored zeppelin breakup over C1/M04's own world, the mission where piratezep is
/// live. The kill plays the hull-death def, which calls <c>killpzep</c>; that definition's
/// <c>main_altitude_check</c> is an Initial sequence gated on <c>NODE_UNDERCOVER</c>, so the
/// choreography waits on a downward probe from the hull rather than on a clock. This suite is the
/// headless proof that the gate stays shut at cruise, opens as the wreck sinks, and that the
/// motions behind it (the pitch ease, the six gasbag drops) then run. It also pins where the
/// hull and its gasbags stop: the hull where the stop on floatdown catches it, the gasbags on the
/// sea the contact tier answers with.</summary>
internal static class ZeppelinBreakupSuites
{
    private const string Chapter = "C1";
    private const string Mission = "M04";
    private const string Hull = "piratezep";
    private const string Pitch = "rock_zeppelin";
    private const float Tick = 1f / 30f;

    // Long enough for floatdown's -3.5 descent to bring the hull inside the authored 65 m probe
    // from cruise altitude, with room to spare; the loop leaves early once the gate has opened.
    private const float SinkSeconds = 90f;

    // How long the clock runs past the gate opening: enough for the six engine gates, which poll
    // their own -4 m probe as the wreck goes on down, and for the gasbags to reach the water.
    private const float AfterOpenSeconds = 20f;

    // rotatezepdown takes the hull to -15 deg over 8 s and rotatezep eases it back to -7 deg at
    // the break, so a pitch above this threshold can only be the ease.
    private const float Eased = -0.15f;

    // C1/M04's sea, and how close to it a gasbag has to stop to count as resting on it. A gasbag
    // freezes its pose in the hull's frame and the rotatezep ease carries it a few metres more;
    // the failure this replaces was 1,228 m, not a metre.
    private const float SurfaceY = 0f, RestBandM = 10f;

    // Where the halted hull may stand relative to its height on the frame the stop dispatched.
    private const float HaltToleranceM = 0.5f;

    // The six engines breakupzep destroys, each behind its own -4 m water probe. Their destroy
    // defs switch the engine's healthy model off, which is the node the mission's twelve-entry
    // INACTIVE_COMPLETION_COUNT objectives read.
    private static readonly string[] BreakupEngines =
    {
        "leng11", "leng21", "leng31", "reng11", "reng21", "reng31",
    };

    // The other six of the twelve. Only a burning gasbag's own death def reaches these: gasbag N's
    // left and right halves each destroy the two engines on their side of bay N, so three gasbags
    // down eventually darkens the whole bank whether the wreck reaches the water or not.
    private static readonly string[] GasbagOnlyEngines =
    {
        "leng12", "leng22", "leng32", "reng12", "reng22", "reng32",
    };

    [Suite("zeppelin-breakup",
        "the authored zeppelin breakup on C1/M04's piratezep: killing the hull starts killpzep, " +
        "whose main_altitude_check gate reads a downward NODE_UNDERCOVER probe of the decoded " +
        "65 m. The gate stays shut while the wreck is still high, opens as floatdown's -3.5 " +
        "descent brings it down, and rotatezep, breakupzep's six gasbag drops and the stop on " +
        "floatdown all follow from it. The stop halts the wreck where it stands and all six " +
        "gasbags come to rest on the sea instead of falling through it. What the original's gates leave behind is read off the nodes " +
        "themselves: all twelve engine healthy models lose their active bit, six from the " +
        "breakup's own calls and six from the burning bays")]
    internal static void ZeppelinBreakup(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: true, mission: Mission, world =>
        {
            var runtime = world.Session.Runtime;
            var defs = Zeppelins.Load(missionZrdr);
            var nets = AiNets.Load(chapterZrdr);
            ZeppelinRuntime? zeps = null;
            uint maskWas = runtime.ContactMask;
            try
            {
                // A real session wires the mask from BuildsCollision; a suite world does not, and
                // with no mask the probe answers false the way a collision-less build does.
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

        ctx.WriteArtifact("test-zeppelin-breakup.txt", report.ToString());
    }

    private static void Run(TestContext ctx, AnimRuntime runtime, ZeppelinRuntime zeps,
        StringBuilder report)
    {
        var host = runtime.FindNodes(Hull).FirstOrDefault();
        ctx.Check(host != null, $"the {Hull} world node resolves in the {Chapter}/{Mission} world");
        if (host == null)
        {
            return;
        }

        var pitch = runtime.FindNodes(Pitch, host).FirstOrDefault();
        ctx.Check(pitch != null, $"{Pitch} resolves under the {Hull} subtree");
        var bags = new List<Node3D>();
        for (int i = 1; i <= 6; i++)
        {
            if (runtime.FindNodes($"gasbag{i}", host).FirstOrDefault() is { } bag)
            {
                bags.Add(bag);
            }
        }

        ctx.Same(6, bags.Count, $"the six gasbags break1..break6 drop resolve under the hull");
        if (pitch == null || bags.Count != 6)
        {
            return;
        }

        var bagRest = bags.Select(b => b.Position).ToList();
        int litAtStart = EnginesOut(runtime, host, BreakupEngines, null)
            + EnginesOut(runtime, host, GasbagOnlyEngines, null);
        ctx.Same(0, litAtStart,
            $"the twelve engine healthy models are all switched ON before the kill off={litAtStart} of 12");
        report.AppendLine($"hull built at ({host.GlobalPosition.X:0},{host.GlobalPosition.Y:0},{host.GlobalPosition.Z:0})");

        // What the gate lets through, taken at the dispatch seam rather than inferred from poses:
        // a CALL/STOP_SEQUENCE writes a state and moves nothing of its own.
        var fired = new List<string>();
        float? haltedY = null;
        runtime.OnEventDispatched = d =>
        {
            if (d.Def.AnimName is "killpzep")
            {
                string line = $"{d.Sequence}:{d.EventKind}({d.EventName})";
                fired.Add(line);
                if (line == "main_altitude_check:StopSequence(floatdown)")
                {
                    haltedY = host.GlobalPosition.Y;
                }
            }
        };

        // The kill: three gasbags down leaves survivors 3 < the record's required 4, which is the
        // decoded polarity the zeppelin-damage suite pins. That is what plays the hull death.
        for (int i = 1; i <= 3; i++)
        {
            runtime.DamageAt(runtime.FindNodes($"gasbag{i}", host).FirstOrDefault(), 10_000f);
        }

        zeps.SimStep(Tick);
        runtime.Advance(Tick);
        ctx.Check(zeps.IsDead(Hull), $"three gasbags down kills the hull by survivor count");

        var (openedAtStart, _) = runtime.ConditionTally("NodeUndercover");
        float startY = host.GlobalPosition.Y;
        ctx.Same(0, openedAtStart,
            $"the gate is SHUT at the kill: no NODE_UNDERCOVER has read true with the hull at y={startY:0} m");

        // Sink it. floatdown's -3.5 gravity is the only thing bringing the wreck down to the
        // probe's reach, so the loop simply runs the clock.
        int openedAt = -1;
        float openedY = 0f;
        bool everRunning = false;
        int steps = (int)(SinkSeconds / Tick);
        for (int i = 0; i < steps; i++)
        {
            runtime.Advance(Tick);
            zeps.SimStep(Tick);
            everRunning |= runtime.AnimStateOf("killpzep") == 2;
            var (opened, _) = runtime.ConditionTally("NodeUndercover");
            if (openedAt < 0 && opened > 0)
            {
                openedAt = i;
                openedY = host.GlobalPosition.Y;
            }

            if (i % 60 == 0)
            {
                report.AppendLine($"t={i * Tick:0.0} hull.y={host.GlobalPosition.Y:0.0} " +
                    $"pitch.x={pitch.Rotation.X:0.000} gate={opened}");
            }

            // The clock only has to outlast the choreography the open starts, not the whole
            // budget: the engine gates and the gasbag splashes are all inside this margin.
            if (openedAt >= 0 && i > openedAt + (int)(AfterOpenSeconds / Tick))
            {
                break;
            }
        }

        runtime.OnEventDispatched = null;
        var (gateTrue, gateFalse) = runtime.ConditionTally("NodeUndercover");
        report.AppendLine($"gate {gateTrue} true / {gateFalse} false; opened at step {openedAt} " +
            $"(y={openedY:0.0}, {startY - openedY:0.0} m below the kill)");
        foreach (string line in fired)
        {
            report.AppendLine($"killpzep {line}");
        }

        ctx.Check(everRunning, $"the kill starts the death choreography killpzep");
        ctx.Check(gateTrue > 0,
            $"the descent opens the gate: NODE_UNDERCOVER read true {gateTrue} time(s) after {gateFalse} false, {startY - openedY:0.0} m below the kill altitude");
        ctx.Check(gateFalse > 0,
            $"…and it was polled shut first, so the open is the descent and not the start");

        // main_altitude_check's three consequences, in its authored order.
        ctx.Check(fired.Contains("main_altitude_check:CallSequence(rotatezep)"),
            $"the open gate calls rotatezep");
        ctx.Check(fired.Contains("main_altitude_check:CallSequence(breakupzep)"),
            $"…and breakupzep");
        ctx.Check(fired.Contains("main_altitude_check:StopSequence(floatdown)"),
            $"…and reaches the StopSequence on floatdown half a second later");

        ctx.Check(pitch.Rotation.X > Eased,
            $"rotatezep eased the pitch back from rotatezepdown's -15 deg pitch.x={pitch.Rotation.X:0.000} rad (authored -0.122)");

        // The drop is read off the dispatched event rather than a displacement. A gasbag stopping
        // on the water at once barely moves in the hull's frame.
        int dropped = 0;
        int afloat = 0;
        for (int i = 0; i < bags.Count; i++)
        {
            float drop = bagRest[i].DistanceTo(bags[i].Position);
            float restY = bags[i].GlobalPosition.Y;
            report.AppendLine($"gasbag{i + 1} moved {drop:0.0} m in the hull's frame, rests at y={restY:0.0}");
            dropped += fired.Contains($"break{i + 1}:ObjectMotion(gasbag{i + 1})") ? 1 : 0;
            afloat += Mathf.Abs(restY - SurfaceY) <= RestBandM ? 1 : 0;
        }

        ctx.Same(6, dropped, $"breakupzep drops all six gasbags dropped={dropped} of 6");
        ctx.Same(6, afloat,
            $"…and every one of them comes to rest on the sea rather than falling through it afloat={afloat} of 6");

        // The six engine gates are the second decoded operand, -4 m against the hull's -65.
        int engines = fired.Where(f => f.Contains("CallAnimation(destroy_pz")).Distinct().Count();
        report.AppendLine($"engine gates opened: {engines} of 6");
        // Trap (c): an effect template snaps to an absolute world point, so a splash authored at a
        // falling gasbag is only right if it is sited from that gasbag when the call fires. The
        // CALL_ANIMATION arm reads the AT_NODE site's live transform, which is what this pins.
        int splashes = fired.Count(f => f.Contains("CallAnimation(huge_splash)"));
        int ripples = fired.Count(f => f.Contains("CallAnimation(huge_ripple)"));
        report.AppendLine($"water landings: {splashes} huge_splash, {ripples} huge_ripple");
        ctx.Same(6, splashes,
            $"each gasbag landing on water takes its bounce_sequence.water branch and calls huge_splash at that gasbag splashes={splashes}");
        ctx.Same(splashes, ripples,
            $"…and each one is followed half a second later by its huge_ripple ripples={ripples}");

        // Where the wreck came to rest: the stop on floatdown ends the hull's flight where it
        // stands, a few tens of metres over the sea its gasbags fall to (docs/org/sequences.md).
        float wreckY = host.GlobalPosition.Y;
        if (host.GetWorld3D()?.DirectSpaceState is { } space)
        {
            var probe = host.GlobalPosition + (Vector3.Up * 2000f);
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                probe, probe + (Vector3.Down * 6000f), CollisionLayers.World));
            string surface = hit.Count > 0 ? $"{hit["position"].AsVector3().Y:0.0}" : "none";
            report.AppendLine($"wreck rests at y={wreckY:0.0}, first surface above/below it y={surface}");
        }

        ctx.Check(haltedY is { } h && Mathf.Abs(wreckY - h) <= HaltToleranceM,
            $"STOP_SEQUENCE floatdown ends the hull's flight where it stands wreck y={wreckY:0.0} halted y={haltedY:0.0}");

        ctx.Same(6, engines,
            $"each engine's own -4 m NODE_UNDERCOVER opens and calls its destroy anim opened={engines} of 6");

        // A dispatched CALL_ANIMATION is not proof of its product: what an objective reads is the
        // active bit the callee's own destroyit sequence writes, so read the node.
        int dark = EnginesOut(runtime, host, BreakupEngines, report);
        int burnt = EnginesOut(runtime, host, GasbagOnlyEngines, report);
        ctx.Same(BreakupEngines.Length, dark,
            $"each destroy anim's destroyit switches its engine's healthy model off off={dark} of {BreakupEngines.Length}");
        ctx.Same(GasbagOnlyEngines.Length, burnt,
            $"…and the burning gasbags take the other six down with them off={burnt} of {GasbagOnlyEngines.Length}");
        ctx.Note($"gate opened {startY - openedY:0} m below the kill altitude ({gateTrue} true / {gateFalse} false); all {dark + burnt} engine healthy models switched off; per-step trace in test-zeppelin-breakup.txt");
    }

    // How many of the named engines have lost the active bit on their healthy model, walked the
    // way the objective script walks it: the hull, then the engine, then healthy inside it.
    private static int EnginesOut(AnimRuntime runtime, Node3D host, IReadOnlyList<string> engines,
        StringBuilder? report)
    {
        int off = 0;
        foreach (string engine in engines)
        {
            var node = runtime.FindNodes(engine, host).FirstOrDefault();
            var healthy = node != null ? runtime.FindNodes("healthy", node).FirstOrDefault() : null;
            off += healthy is { Visible: false } ? 1 : 0;
            report?.AppendLine(
                $"{engine}: healthy={(healthy == null ? "unresolved" : healthy.Visible ? "on" : "OFF")}");
        }

        return off;
    }
}
