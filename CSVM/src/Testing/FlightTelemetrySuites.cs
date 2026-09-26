using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.Session.Roster;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Whether the per-aircraft, once-a-sim-second flight telemetry line costs anything when
/// nobody asked for it. Every live aircraft crosses the one-second boundary on the same sim step,
/// so the line is emitted N times inside one physics tick or not at all; the cost of an unasked
/// emission is what put the tick over its budget (docs/verification.md PERF-23).</summary>
internal static class FlightTelemetrySuites
{
    // Past one sim second, and short of the second crossing 60 steps later, so a whole arm sees
    // exactly one line per aircraft.
    private const int StepsPerArm = 65;

    private const float StepDt = 1f / 60f;

    private const int Aircraft = 3;

    // What the emitted line is recognised by, in both sinks.
    private const string Marker = "telemetry pos=";

    [Suite("flight-telemetry-gate",
        "the once-a-sim-second flight telemetry line costs nothing unasked: three aircraft " +
        "flown a whole sim second write no line to either sink at the shipped --log= level, " +
        "and --log=flight brings back exactly one line per aircraft, all on the SAME sim " +
        "step, in the invariant culture")]
    internal static void FlightTelemetryGate(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null);
        var textures = new TextureArchive(texturesPath);
        bool wasCollected = Log.ConsoleShows("flight", Log.Level.Debug);
        ProjectilePool? pool = null;
        var rigs = new List<FlightController>();
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = weaponDefs,
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            // worldEffects null!: never dereferenced, CrashProgram/WorldScene stay null, so the
            // spawner's crash-runtime block (its only reader) is skipped.
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs,
                new FlightWorldBindings { Projectiles = live, Gamez = planesGamez }, new HumanRosterBindings());

            for (int i = 0; i < Aircraft; i++)
            {
                var at = new Vector3(i * 600f, 500f, 0f);
                rigs.Add(spawner.SpawnAi(new AiSpawn(ctx.PlaneName, at, at + Vector3.Forward,
                    AiPilot.HoldingCourse(at, at + Vector3.Forward),
                    Scheme: null, Team: InstantActionRuntime.EnemyTeam)));
            }

            ctx.Check(rigs.Count == Aircraft && rigs.All(r => r.InPlay),
                $"{Aircraft} aircraft are flying, so a whole sim second of steps is a real workload");

            // The category explicitly at its shipped level, so the arm reads the default rather
            // than whatever --log= this run carries.
            Log.Configure("flight:info");
            var quiet = Drive(ctx, rigs, "unasked");
            ctx.Check(quiet.Console == 0,
                $"a sim second of flight with --log= at its default writes no telemetry line to the console (got {quiet.Console})");
            ctx.Check(quiet.File == 0,
                $"…and none to the always-on log file either, so the line costs nothing when nobody asked (got {quiet.File})");

            Log.Configure("flight:debug");
            var asked = Drive(ctx, rigs, "asked");
            ctx.Check(asked.Console == Aircraft,
                $"--log=flight brings the line back, one per aircraft (got {asked.Console} of {Aircraft})");
            ctx.Check(asked.File == Aircraft,
                $"…and the same lines reach the log file (got {asked.File} of {Aircraft})");
            ctx.Check(asked.Steps.Count == 1,
                $"and every aircraft emits on the SAME sim step ({string.Join(",", asked.Steps)}), which is why the cost lands on one physics tick");
            string sample = asked.Lines.FirstOrDefault() ?? "";
            ctx.Note($"line: {sample}");
            ctx.Check(System.Text.RegularExpressions.Regex.IsMatch(sample, @"spd=\d+\.\d"),
                $"…written in the invariant culture, so a decimal reads '.' rather than the host's separator");
        }
        finally
        {
            Log.Configure(wasCollected ? "flight:debug" : "flight:info");
            pool?.Free();
            foreach (var rig in rigs)
            {
                rig.Free();
            }

            textures.Dispose();
        }
    }

    // Steps every aircraft past its one-second boundary, counting the telemetry lines that reach
    // the console sink and the log file, and which step numbers they arrived on.
    private static Arm Drive(TestContext ctx, List<FlightController> rigs, string label)
    {
        var arm = new Arm();
        long fileBefore = FileLines();
        var seen = new List<string>();
        using (Log.PushConsoleSink(line =>
        {
            if (line.Contains(Marker))
            {
                seen.Add(line);
            }
        }))
        {
            for (int step = 0; step < StepsPerArm; step++)
            {
                int before = seen.Count;
                foreach (var rig in rigs)
                {
                    rig.SimStep(StepDt);
                }

                if (seen.Count > before && !arm.Steps.Contains(step))
                {
                    arm.Steps.Add(step);
                }
            }
        }

        arm.Lines = seen;
        arm.Console = seen.Count;
        arm.File = (int)(FileLines() - fileBefore);
        ctx.Note($"{label}: {StepsPerArm} steps x {rigs.Count} aircraft -> console {arm.Console}, file {arm.File}, steps [{string.Join(",", arm.Steps)}]");
        return arm;
    }

    // The always-on file sink is opened for writing with FileShare.Read, so a reader has to share
    // Write back or the open is refused; a run without a sink answers 0 for both sides of an arm.
    private static long FileLines()
    {
        if (Log.SinkPath is not { } path || !File.Exists(path))
        {
            return 0;
        }
        using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        long count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains(Marker))
            {
                count++;
            }
        }
        return count;
    }

    private sealed class Arm
    {
        public int Console { get; set; }

        public int File { get; set; }

        public List<int> Steps { get; } = new();

        public List<string> Lines { get; set; } = new();
    }
}
