using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the fast-forward a held input applies to a cutscene the player may not
/// skip. CM02's capture is the case for the same reason the skip suite uses it: its whole call
/// closure raises no hold code, so the original plays it out in full. Played over that mission's
/// built world twice, undisturbed and with the input held, and read on the authored codes, the
/// definition time each fired at, and the cutscene camera's path between them.</summary>
internal static class CutsceneFastForwardSuites
{
    private const int Cm02Seq = 1;

    private const float StepDt = 1f / 60f;

    // Long enough for the capture and the wing walk it calls to run out at real speed.
    private const float PlayBudgetS = 30f;

    // How far apart the two legs may put one authored code on the DEFINITION's own clock. The held
    // leg reads its codes on a coarser grid, so the floor is that grid rather than a judgement.
    private const float BeatToleranceS = 0.25f;

    // The node whose world pose stands for this episode's POSE channel: the wing walk's own
    // parent, flown by the definition's 19.25 s OBJECT_MOTION and touched by nothing else.
    // Not "camera1": that node is shared, and a definition outside the episode takes it over on
    // its own real-time schedule, which reads as drift and is not (docs/verification.md).
    private const string PoseNode = "wingwalk_parent";

    // How far apart the pose may be at one definition time, in metres. The two legs sample the
    // same path half a step out of phase, which is centimetres at any speed this shot flies.
    private const float PoseToleranceM = 10f;

    // Below this the episode moves nothing worth comparing, and the path check is noted rather
    // than asserted against a node nothing moved.
    private const float PoseMovedM = 5f;

    // What the measured speed-up must clear. The ramp costs the leading fraction of a second, so
    // the whole episode reads a little below the held rate; this is well clear of both.
    private const float MinMeasuredRate = 2f;

    [Suite("campaign-cutscene-fast-forward",
        "a held input fast-forwards a cutscene the player may not skip, over CM02's BUILT world: "
        + "the mission's capture raises no hold code, so its episode takes the rate rather than a "
        + "skip, and playing it undisturbed and again with the input held raises every authored "
        + "code in the same order and at the same time on the definition's own clock, flies the "
        + "same pose path through that clock, and only reaches the end sooner in real seconds "
        + "-- with the rate reaching the definitions of that episode alone and every one of them "
        + "handed back at 1 when it ends")]
    internal static void HeldFastForward(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = CutsceneSkipSuites.MissionOf(CampaignSequence.Load(ctx.ZrdrPath), Cm02Seq)
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
            ctx.WithWorld(chapter, collision: false, folder, world => Drive(ctx, world, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-cutscene-fast-forward-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"played {chapter}/{folder}'s capture at 1x and under a held fast-forward");
    }

    private static void Drive(TestContext ctx, TestWorld world, StringBuilder report)
    {
        var call = CutsceneSkipSuites.SwapCallIn(world.Session.Program)
            ?? throw new SuiteSkippedException("no definition in this mission authors a swap code");
        var closure = world.Session.Program.Subset(new[] { call.Anim }).Defs;
        report.AppendLine($"'{call.Anim}' authors callback {call.Code}, closure of {closure.Count} definition(s)");

        var posed = First(world.Runtime.FindNodes(PoseNode));
        var played = Play(ctx, world, call.Anim, closure, posed, held: false, report);
        var held = Play(ctx, world, call.Anim, closure, posed, held: true, report);
        Compare(ctx, played, held, report);
        CheckScope(ctx, world, call.Anim, report);
    }

    // One played leg. `held` holds the fast-forward the whole way through, which is the player
    // holding the key from the first frame; the rate's own ramp is what keeps that from a jump.
    private static Leg Play(TestContext ctx, TestWorld world, string anim,
        IReadOnlyList<AnimDefinition> closure, Node3D? posed, bool held, StringBuilder report)
    {
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var savedClock = GameClock.Current;
        var savedHost = world.Runtime.CallbackHost;
        var leg = new Leg();
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            cutscene.BindWorld(world.Runtime);
            cutscene.HostDefinitions(NamesOf(closure));
            world.Runtime.CallbackHost = cutscene.Host;
            // Through the trigger slot, the way a mission's own landings row starts it: the episode
            // then belongs to this definition rather than to whichever callee raises its first
            // code, and the rate is scoped to that definition's whole call closure.
            world.Runtime.MissionTriggerOwner = name => cutscene.Own(name);
            world.Runtime.PlayMissionTrigger(anim);

            int seen = 0;
            for (float now = 0f; now < PlayBudgetS; now += StepDt)
            {
                cutscene.HoldFastForward(held);
                clock.BeginFrame(StepDt);
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                leg.DefTimeS += StepDt * cutscene.FastForwardRate;
                leg.PeakRate = Mathf.Max(leg.PeakRate, cutscene.FastForwardRate);
                for (; seen < cutscene.Codes.Count; seen++)
                {
                    leg.Beats.Add(new Beat(cutscene.Codes[seen], now, leg.DefTimeS));
                }

                // ⚠ Sample only while the episode holds the session. The handoff puts the posed
                // nodes back, and a sample past it compares a parked node to a flown one rather
                // than two readings of the same path.
                if (cutscene.Playing)
                {
                    leg.Path.Add((leg.DefTimeS, Where(posed)));
                    continue;
                }

                if (leg.Beats.Count > 0)
                {
                    leg.EndedAtS = now;
                    break;
                }
            }

            leg.EndRate = cutscene.FastForwardRate;
        }
        finally
        {
            world.Runtime.CallbackHost = savedHost;
            world.Runtime.MissionTriggerOwner = null;
            GameClock.Current = savedClock;
            cutscene.Free();
        }

        report.AppendLine($"{(held ? "held" : "1x  ")}: {leg.Beats.Count} code(s) "
            + $"[{string.Join(",", Codes(leg))}], last at {leg.LastRealS:0.##} s real / "
            + $"{leg.LastDefS:0.##} s definition, episode ended {leg.EndedAtS:0.##} s, "
            + $"peak rate {leg.PeakRate:0.##}, end rate {leg.EndRate:0.##}");
        foreach (var beat in leg.Beats)
        {
            report.AppendLine($"    code {beat.Code} at {beat.RealS:0.###} s real / {beat.DefS:0.###} s definition");
        }

        return leg;
    }

    private static void Compare(TestContext ctx, Leg played, Leg held, StringBuilder report)
    {
        ctx.Check(played.Beats.Count > 0,
            $"the capture raises authored codes at real speed ({played.Beats.Count}), so there is an order to compare");
        ctx.Check(SameCodes(played, held),
            $"and the held leg raises the same codes in the same order ([{string.Join(",", Codes(held))}] against [{string.Join(",", Codes(played))}]), rather than dropping or reordering a beat");
        ctx.Check(played.PeakRate == 1f,
            $"the undisturbed leg never leaves real speed (peak {played.PeakRate:0.##})");
        ctx.Check(Mathf.IsEqualApprox(held.PeakRate, CutsceneFastForward.Target),
            $"the held leg's ramp reaches the whole rate ({held.PeakRate:0.##} of {CutsceneFastForward.Target:0.##})");
        ctx.Check(played.EndRate == 1f && held.EndRate == 1f,
            $"and both legs hand every definition back at 1 when the episode ends ({played.EndRate:0.##}, {held.EndRate:0.##})");
        if (!SameCodes(played, held))
        {
            return;
        }

        float worstBeat = 0f;
        for (int i = 0; i < played.Beats.Count; i++)
        {
            worstBeat = Mathf.Max(worstBeat, Mathf.Abs(played.Beats[i].DefS - held.Beats[i].DefS));
        }

        report.AppendLine($"worst beat drift {worstBeat:0.###} s on the definition's own clock");
        ctx.Check(worstBeat <= BeatToleranceS,
            $"every code fires at the same time on the definition's own clock either way (worst drift {worstBeat:0.###} s), which is the rate reaching the callbacks rather than beats being skipped past");
        ctx.Check(held.LastRealS < played.LastRealS,
            $"the held leg reaches the last code sooner in real seconds ({held.LastRealS:0.##} s against {played.LastRealS:0.##} s)");
        float measured = held.LastRealS > 0f ? played.LastRealS / held.LastRealS : 0f;
        report.AppendLine($"measured speed-up {measured:0.##}x over the whole episode");
        ctx.Check(measured >= MinMeasuredRate,
            $"by a factor of {measured:0.##}, which is the held rate less what the ramp costs at the start");
        ComparePose(ctx, played, held, report);
    }

    // The pose half: at a given time on the definition's own clock the posed node must be where
    // the undisturbed leg had it, or the picture and the callbacks have come apart under the rate.
    private static void ComparePose(TestContext ctx, Leg played, Leg held, StringBuilder report)
    {
        if (played.Path.Count == 0 || held.Path.Count == 0)
        {
            ctx.Check(false, $"both legs sampled '{PoseNode}' while the episode held the session");
            return;
        }

        float moved = 0f;
        foreach (var (_, at) in played.Path)
        {
            moved = Mathf.Max(moved, at.DistanceTo(played.Path[0].At));
        }

        report.AppendLine($"'{PoseNode}' moved {moved:0.#} m over the undisturbed leg, "
            + $"{played.Path.Count} samples against {held.Path.Count}");
        if (moved < PoseMovedM)
        {
            ctx.Note($"the capture moves '{PoseNode}' too little to read ({moved:0.#} m), so the pose check is the beat drift alone");
            return;
        }

        float worst = 0f;
        foreach (var (defS, at) in held.Path)
        {
            if (defS <= played.Path[^1].DefS)
            {
                worst = Mathf.Max(worst, at.DistanceTo(NearestOn(played.Path, defS)));
            }
        }

        report.AppendLine($"worst pose drift {worst:0.##} m at one definition time");

        ctx.Check(worst <= PoseToleranceM,
            $"'{PoseNode}' flies the same path through the definition's own clock either way (worst {worst:0.##} m of a {moved:0.#} m flight), so the pose channel took the rate the callbacks did");
    }

    // The scope: the rate reaches the episode's own definitions and nothing else, which is what
    // keeps the world around a mid-mission cutscene at real time.
    private static void CheckScope(TestContext ctx, TestWorld world, string anim, StringBuilder report)
    {
        if (world.Runtime.FastForward is not { } rate)
        {
            ctx.Check(false, $"the bind handed the runtime a fast-forward to scope");
            return;
        }

        var closure = world.Session.Program.Subset(new[] { anim }).Defs;
        AnimDefinition? outside = null;
        foreach (var def in world.Session.Program.Defs)
        {
            if (!closure.Contains(def))
            {
                outside = def;
                break;
            }
        }

        rate.Scope(closure);
        rate.Held = true;
        rate.Ramp(CutsceneFastForward.RampSeconds);
        report.AppendLine($"scoped {closure.Count} definition(s); one ramp's worth of held time reads {rate.Rate:0.##}");
        ctx.Check(Mathf.IsEqualApprox(rate.Rate, CutsceneFastForward.Target),
            $"one ramp's worth of held time reaches the whole rate ({rate.Rate:0.##})");
        ctx.Check(Mathf.IsEqualApprox(rate.RateFor(closure[0]), CutsceneFastForward.Target),
            $"which a definition of the episode runs at");
        ctx.Check(outside == null || rate.RateFor(outside) == 1f,
            $"while a definition outside it stays at exactly 1, so the world around the cutscene keeps real time");
        rate.Clear();
        ctx.Check(rate.Rate == 1f && rate.RateFor(closure[0]) == 1f,
            $"and clearing the scope hands every definition back at 1 with no ramp");
    }

    private static Node3D? First(IReadOnlyList<Node3D> found) => found.Count > 0 ? found[0] : null;

    private static Vector3 Where(Node3D? node) =>
        node != null && GodotObject.IsInstanceValid(node) ? node.GlobalPosition : Vector3.Zero;

    private static IReadOnlyList<string> NamesOf(IReadOnlyList<AnimDefinition> defs)
    {
        var names = new List<string>();
        foreach (var def in defs)
        {
            if (def.AnimName is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static IReadOnlyList<int> Codes(Leg leg)
    {
        var codes = new List<int>();
        foreach (var beat in leg.Beats)
        {
            codes.Add(beat.Code);
        }

        return codes;
    }

    private static bool SameCodes(Leg a, Leg b)
    {
        if (a.Beats.Count != b.Beats.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Beats.Count; i++)
        {
            if (a.Beats[i].Code != b.Beats[i].Code)
            {
                return false;
            }
        }

        return true;
    }

    // Where a leg's posed node was at a given time on the definition's own clock: the nearest
    // sample, since the two legs read the same path on grids of different coarseness.
    private static Vector3 NearestOn(IReadOnlyList<(float DefS, Vector3 At)> path, float defS)
    {
        var best = Vector3.Zero;
        float bestGap = float.MaxValue;
        foreach (var (t, at) in path)
        {
            float gap = Mathf.Abs(t - defS);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = at;
            }
        }

        return best;
    }

    // One authored code as a leg saw it: the code, and when it fired in real seconds and on the
    // definition's own clock.
    private readonly record struct Beat(int Code, float RealS, float DefS);

    // What one played leg leaves behind.
    private sealed class Leg
    {
        public readonly List<Beat> Beats = new();
        public readonly List<(float DefS, Vector3 At)> Path = new();
        public float DefTimeS;
        public float PeakRate = 1f;
        public float EndRate = 1f;
        public float EndedAtS = -1f;

        public float LastRealS => Beats.Count > 0 ? Beats[^1].RealS : 0f;

        public float LastDefS => Beats.Count > 0 ? Beats[^1].DefS : 0f;
    }
}
