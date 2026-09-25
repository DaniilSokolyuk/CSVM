using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Session;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>What a wave spawn costs the frame it lands on, over C4/M03's own built world and its
/// own generator cycle. The measurement a hitch report needs is the whole session step a launch
/// happens in, not the assembler in isolation. That step carries the bind, the loadout and the
/// crash-rig arm together. The steps around it carry the deferred rig's pump and the pool's
/// refill. Both are read at one and at four human rigs off the stepping thread's own CPU time,
/// which other processes' load does not move. A fix that moves cost onto a neighbouring
/// frame cannot read as a win, nor can one that only holds at a single player count.</summary>
internal static class AiWaveLaunchHitchSuites
{
    private const string Chapter = "C4";
    private const string Mission = "M03";
    private const string Generator = "cargozep1";
    private const string BeautyShot = "cg_beauty_shot";
    private const int CreditCode = 800;

    private const float StepDt = 1f / 60f;

    // The credited wave runs out at one launch per ind 3 + wave 1 seconds after the door lead.
    // This covers all five, with a few seconds of pumped frames behind the last.
    private const float LaunchWindowS = 26f;

    // Regression bars on the thread-CPU clock, not the target. Each sits about 1.8x the worst
    // reading, alone, sharded and beside sixteen CPU-burning processes. The warm worst also leaves
    // room for a gen-0 collection, which runs on this thread. A build creeping back onto the launch
    // frame trips the median bar. Readings: docs/verification.md PERF-36.
    private const double MedianBarMs = 25.0;
    private const double WarmLaunchBarMs = 60.0;
    private const double IdleBarMs = 80.0;

    [Suite("ai-wave-launch-hitch",
        "what a wave spawn costs the frame it lands on: over C4/M03's built world the cargozep1 "
        + "generator's five credited launches run through the roster's own SpawnAi, and the sim "
        + "step each one lands in is timed on the stepping thread's own CPU time, at one and at "
        + "four human rigs, so another process's load cannot move the verdict. The load screen is "
        + "stood in for first, so the launches claim the aeroplanes it built and the window measures the bind rather than the build. The median, "
        + "the warm worst and the worst frame carrying no launch are each held under a regression "
        + "bar set from repeated readings, with the hitch monitor's own 40 ms floor reported "
        + "beside them, so a build creeping back onto the launch frame fails here and work merely "
        + "moved onto a neighbouring frame is visible")]
    internal static void AiWaveLaunchHitch(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        var defs = EnemyGenerators.Load(missionZrdr);
        EnemyGeneratorDef? found = null;
        foreach (var d in defs)
        {
            if (d.Node.Equals(Generator, StringComparison.OrdinalIgnoreCase))
            {
                found = d;
            }
        }
        ctx.Check(found != null, $"'{Generator}' is a generator of {Chapter}/{Mission}");
        if (found == null)
        {
            return;
        }

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: false, Mission,
            world => Drive(ctx, world, defs, missionZrdr, chapterZrdr, texturesPath, report));
        ctx.WriteArtifact($"test-ai-wave-launch-hitch-{Chapter}-{Mission}.txt", report.ToString());
    }

    // Both player counts over one built world, then the verdict on each. The pair is the entry's
    // own measurement shape: nothing in the assembly walks the human field. A launch frame that
    // grew with the player count would say the cost is somewhere else entirely.
    private static void Drive(TestContext ctx, TestWorld world, IReadOnlyList<EnemyGeneratorDef> defs,
        string missionZrdr, string chapterZrdr, string texturesPath, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var nets = AiNets.Load(chapterZrdr);
        var templates = CampaignRosterPlan.GeneratorTemplates(
            missionZrdr, VehicleDefs.Load(ctx.ZrdrPath), nets);
        try
        {
            foreach (int rigs in new[] { 1, 4 })
            {
                var window = RunWindow(ctx, world, defs, nets, templates, planesGamez, textures,
                    chapterZrdr, missionZrdr, rigs, report);
                Judge(ctx, window, rigs, report);
            }
        }
        finally
        {
            textures.Dispose();
        }
    }

    private static Window RunWindow(TestContext ctx, TestWorld world,
        IReadOnlyList<EnemyGeneratorDef> defs, IReadOnlyList<AiNet> nets,
        IReadOnlyDictionary<string, RosterSpawnPlan> templates, GameZ planesGamez,
        TextureArchive textures, string chapterZrdr, string missionZrdr, int rigCount,
        StringBuilder report)
    {
        var runtime = world.Runtime;
        var savedHost = runtime.CallbackHost;
        var window = new Window();
        var humans = new List<FlightController>();
        var launched = new List<FlightController>();
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        AiGeneratorRuntime? generators = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(Array.Empty<string>());
            var factory = new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero);
            var resources = SuiteConstants.AircraftResources(ctx, planesGamez, textures,
                Messages.Load(ctx.MessagesPath));
            for (int i = 0; i < rigCount; i++)
            {
                humans.Add(HumanRig(ctx, planesGamez, textures, live, i,
                    new Vector3(i * 60f, 900f, 0f)));
            }
            var field = humans;
            var positions = new List<Vector3>(rigCount);
            var built = new FlightRoster(FlightRosterPolicy.From(spec),
                new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
                factory, ctx.Host, resources,
                new FlightWorldBindings
                {
                    Projectiles = live,
                    Gamez = world.Gamez,
                    WorldScene = world.Session.Builder.Scene,
                    CrashProgram = world.Session.Program,
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                    HumanPositions = () =>
                    {
                        positions.Clear();
                        foreach (var rig in field)
                        {
                            positions.Add(rig.WorldPosition);
                        }
                        return positions;
                    },
                },
                new HumanRosterBindings { RigCount = rigCount });
            roster = built;

            // The load screen, stood in for. A launch orders its generators' aeroplanes, and the
            // screen builds them before the first frame. The window below measures what a launch
            // costs with that build behind the load rather than inside it.
            GameSession.OrderWaveAirframes(built, defs, templates);
            long loadMark = ThreadCpuClock.Now();
            window.Prebuilt = 0;
            while (built.BuildOrderedAirframe())
            {
                window.Prebuilt++;
            }

            window.LoadMs = ThreadCpuClock.Ms(ThreadCpuClock.Now() - loadMark);

            // The session's first frame: the pump is what puts the roster into deferring. Every
            // launch below is a mid-flight introduction and owes its crash rig to a later frame.
            built.PumpDeferredCrashRigs();

            AiGeneratorRuntime? gens = null;
            LaunchedVehicle SpawnFromGenerator(EnemyGeneratorDef def, Vector3 pos, Vector3 look,
                AiPilot pilot)
            {
                int ordinal = gens?.LaunchOrdinal ?? 0;
                if (CampaignRosterPlan.ResolveGeneratorLaunch(templates, def.VehicleParams, out var plan)
                    != GeneratorLaunch.Template || plan == null)
                {
                    return default;
                }
                window.PlaneNode = plan.PlaneNode;
                string name = EnemyGenerators.LaunchName(EnemyGenerators.LaunchBase(plan.Name), ordinal);
                long spawnMark = ThreadCpuClock.Now();
                var aircraft = built.SpawnAi(CampaignRosterPlan.SpawnFor(plan, pos, look, pilot,
                    $"{name}_p{rigCount}"));
                window.SpawnMs.Add(ThreadCpuClock.Ms(ThreadCpuClock.Now() - spawnMark));
                CampaignRosterPlan.ApplyPlan(pilot, plan, 0f);
                launched.Add(aircraft);
                return aircraft;
            }

            gens = new AiGeneratorRuntime(defs,
                (name, scope) => runtime.FindNodes(name, scope) is { Count: > 0 } hits ? hits[0] : null,
                nets, ctx.PlaneName, SpawnFromGenerator,
                (name, host) => runtime.PlayWithin(host, name, applyReset: false).Count,
                (name, host) => runtime.StopWithin(host, name));
            generators = gens;
            ctx.Check(gens.LiveCount > 0, $"{rigCount}P: '{Generator}' is live over the built world");

            runtime.CallbackHost = null;
            gens.BindCallbackHost(runtime);
            ctx.Check(runtime.CallbackHost!(CreditCode, BeautyShot, null),
                $"{rigCount}P: callback {CreditCode} credits '{Generator}' with {AiGeneratorRuntime.CallbackCredit} launches");

            int step = 0;
            for (float t = 0f; t < LaunchWindowS; t += StepDt)
            {
                int before = gens.LaunchOrdinal;
                int gen0 = GC.CollectionCount(0);
                long wallMark = Stopwatch.GetTimestamp();
                long mark = ThreadCpuClock.Now();
                gens.SimStep(StepDt);
                long mid = ThreadCpuClock.Now();
                built.PumpDeferredCrashRigs();
                long end = ThreadCpuClock.Now();
                double wallMs = (Stopwatch.GetTimestamp() - wallMark) * 1000.0 / Stopwatch.Frequency;
                double ms = ThreadCpuClock.Ms(end - mark);
                double pumpMs = ThreadCpuClock.Ms(end - mid);
                window.WorstWallMs = Math.Max(window.WorstWallMs, wallMs);
                if (gens.LaunchOrdinal > before)
                {
                    window.LaunchMs.Add(ms);
                    report.AppendLine(Line(
                        $"{rigCount}P step {step}: launch {gens.LaunchOrdinal} at t={t:0.00} s, {ms:0.0} ms on-thread, {wallMs:0.0} ms wall, {GC.CollectionCount(0) - gen0} gen-0 collection(s)"));
                }
                else if (ms > window.WorstIdleMs)
                {
                    window.WorstIdleMs = ms;
                    window.WorstIdleStep = step;
                    window.WorstIdlePumpMs = pumpMs;
                }
                window.WorstStepMs = Math.Max(window.WorstStepMs, ms);
                step++;
            }

            (window.Claims, window.Misses) = built.AirframeClaims;

            // The model build alone, warm, off the same airframe the launches flew. Not part of the
            // frame above: it is the share of it a pre-build would move rather than remove.
            if (window.PlaneNode is { Length: > 0 } plane)
            {
                long mark = ThreadCpuClock.Now();
                var probe = new PlaneBuilder(planesGamez, textures, spinningProps: true).Build(plane);
                window.ModelMs = ThreadCpuClock.Ms(ThreadCpuClock.Now() - mark);
                probe.Free();
            }
            return window;
        }
        finally
        {
            runtime.CallbackHost = savedHost;
            generators?.Free();
            roster?.ClearMembership();
            foreach (var controller in launched)
            {
                controller.Free();
            }
            foreach (var human in humans)
            {
                human.Free();
            }
            if (pool != null)
            {
                ctx.Host.RemoveChild(pool);
                pool.QueueFree();
            }
        }
    }

    private static void Judge(TestContext ctx, Window window, int rigCount, StringBuilder report)
    {
        ctx.Same(AiGeneratorRuntime.CallbackCredit, window.LaunchMs.Count,
            $"{rigCount}P: the credited wave launched through the roster's own spawn path");
        if (window.LaunchMs.Count == 0)
        {
            return;
        }

        // The first launch pays the one-time zrdr, skill, maneuver and material loads every later
        // launch finds warm. It is reported and kept out of the worst-launch bar. Read before
        // the sort, which destroys arrival order.
        double warmWorst = 0.0;
        for (int i = 1; i < window.LaunchMs.Count; i++)
        {
            warmWorst = Math.Max(warmWorst, window.LaunchMs[i]);
        }
        double cold = window.LaunchMs[0];

        window.LaunchMs.Sort();
        double median = window.LaunchMs[window.LaunchMs.Count / 2];
        double floor = HitchMonitor.FloorMsDefault;
        report.AppendLine(Line($"{rigCount}P launch frames (ms, sorted): {Join(window.LaunchMs)}"));
        report.AppendLine(Line($"{rigCount}P the assembly inside them, in arrival order: {Join(window.SpawnMs)}"));
        report.AppendLine(Line(
            $"{rigCount}P worst non-launch step {window.WorstIdleMs:0.0} ms at step {window.WorstIdleStep} ({window.WorstIdlePumpMs:0.0} ms of it the rig pump and the pool refill); worst step of the window {window.WorstStepMs:0.0} ms on-thread, {window.WorstWallMs:0.0} ms wall; warm model build {window.ModelMs:0.0} ms; load screen built {window.Prebuilt} aeroplane(s) in {window.LoadMs:0} ms; {window.Claims} claim(s), {window.Misses} miss(es)"));

        // The pool is what makes the frames below what they are. A run where the launches built
        // in place after all would otherwise read as a plain regression with no cause named.
        ctx.Same(window.LaunchMs.Count, window.Claims,
            $"{rigCount}P: every launch flew an aeroplane the load screen had built claims={window.Claims} misses={window.Misses} prebuilt={window.Prebuilt}");

        // The median rather than every launch: a gen-0 collection on one launch frame is the
        // allocator's cost (PERF-33). The middle of five launches is what the frame costs.
        ctx.Check(median < MedianBarMs,
            $"{rigCount}P: the median launch frame holds its bar median={median:0.0} ms bar={MedianBarMs:0} ms over {window.LaunchMs.Count} launch(es) min={window.LaunchMs[0]:0.0} max={window.LaunchMs[^1]:0.0}");
        ctx.Check(warmWorst < WarmLaunchBarMs,
            $"{rigCount}P: the worst warm launch frame holds its bar warm_worst={warmWorst:0.0} ms bar={WarmLaunchBarMs:0} ms cold_first={cold:0.0} ms");

        // The frames around the launch, not the launch. A threshold on the launch frame alone is
        // satisfiable by moving the block onto a neighbour, which is not a fix. The window's own
        // worst idle frame is barred too (docs/verification.md PERF-31).
        ctx.Check(window.WorstIdleMs < IdleBarMs,
            $"{rigCount}P: the worst frame carrying no launch holds its bar idle_worst={window.WorstIdleMs:0.0} ms at step {window.WorstIdleStep} bar={IdleBarMs:0} ms");

        ctx.Note($"{rigCount}P: on-thread CPU, median launch frame {median:0.0} ms (min {window.LaunchMs[0]:0.0}, max {window.LaunchMs[^1]:0.0}, cold first {cold:0.0}, warm worst {warmWorst:0.0}), worst frame of the window {window.WorstStepMs:0.0} ms (wall {window.WorstWallMs:0.0} ms), worst frame carrying no launch {window.WorstIdleMs:0.0} ms, warm model build {window.ModelMs:0.0} ms, load screen built {window.Prebuilt} aeroplane(s) in {window.LoadMs:0} ms, bars {MedianBarMs:0}/{WarmLaunchBarMs:0}/{IdleBarMs:0} ms, hitch floor {floor:0} ms, clock {ThreadCpuClock.Source} at {ThreadCpuClock.CyclesPerMs / 1000.0:0} MHz");
    }

    // A human rig in the world but not flying: the field the livery stream, the listener feed and
    // the shooter-id base count. That is all a launch's own assembly reads of the player count.
    private static FlightController HumanRig(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool live, int index, Vector3 pos)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            PlayerIndex = index,
            IsHumanPiloted = true,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam,
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), null, new CamParams(), pos, pos + Vector3.Forward);
        rig.Name = $"hitchplayer{index + 1}";
        ctx.Host.AddChild(rig);
        return rig;
    }

    private static string Join(List<double> values)
    {
        var parts = new List<string>(values.Count);
        foreach (double v in values)
        {
            parts.Add(v.ToString("0.0", CultureInfo.InvariantCulture));
        }
        return string.Join(", ", parts);
    }

    private static string Line(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);

    // ⚠ Do not bar these frames on wall time; it reads the machine's load, not the launch. Wall time
    // counts descheduled time and the console writes blocked on the runner's pipe (PERF-36). A
    // blocking wait therefore reads as nothing here, and the wall worst is reported beside it.
    private static class ThreadCpuClock
    {
        public static readonly string Source = OperatingSystem.IsWindows() ? "thread cycles" : "wall";

        public static readonly double CyclesPerMs = Calibrate();

        public static long Now()
        {
            if (!OperatingSystem.IsWindows())
            {
                return Stopwatch.GetTimestamp();
            }
            QueryThreadCycleTime(GetCurrentThread(), out ulong cycles);
            return (long)cycles;
        }

        public static double Ms(long cycles) => cycles / CyclesPerMs;

        // The cycle rate off short spins against the wall clock. A preempted spin reads fewer
        // cycles than its wall span, never more, so the fastest of many is the rate.
        private static double Calibrate()
        {
            if (!OperatingSystem.IsWindows())
            {
                return Stopwatch.Frequency / 1000.0;
            }
            double best = 0.0;
            long span = Stopwatch.Frequency / 500;
            for (int i = 0; i < 25; i++)
            {
                long cycles0 = Now();
                long wall0 = Stopwatch.GetTimestamp();
                while (Stopwatch.GetTimestamp() - wall0 < span)
                {
                    System.Threading.Thread.SpinWait(8);
                }
                long cycles = Now() - cycles0;
                double wallMs = (Stopwatch.GetTimestamp() - wall0) * 1000.0 / Stopwatch.Frequency;
                best = Math.Max(best, cycles / wallMs);
            }
            return best;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
    }

    private sealed class Window
    {
        public List<double> LaunchMs { get; } = new();

        public List<double> SpawnMs { get; } = new();

        public double WorstStepMs { get; set; }

        public double WorstWallMs { get; set; }

        public double WorstIdleMs { get; set; }

        public int WorstIdleStep { get; set; } = -1;

        public double WorstIdlePumpMs { get; set; }

        public double ModelMs { get; set; }

        public int Prebuilt { get; set; }

        public int Claims { get; set; }

        public int Misses { get; set; }

        public double LoadMs { get; set; }

        public string? PlaneNode { get; set; }
    }
}
