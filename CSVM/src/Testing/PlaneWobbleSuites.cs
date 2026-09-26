using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>Whether the plane wobble reaches the flown aircraft's own node as the original's
/// random walk rather than as a periodic waveform: the overspeed rattle over its gate and the
/// nitro engage, read off <c>ShakePivot</c> on a roster-built rig. The law itself is pinned
/// engine-free in <c>PlaneShakeTests</c>; what this adds is the whole chain, the flight step's
/// speed read, the engage edge and the pivot write.</summary>
internal static class PlaneWobbleSuites
{
    private const float Dt = 1f / 60f;

    // Fast cruise, short of the authored min_speed 1.0 gate, and a dive well past it.
    private const float CruiseRatio = 0.9f;
    private const float DiveRatio = 1.4f;

    [Suite("plane-wobble-walk",
        "the overspeed rattle and the nitro engage reach the flown plane node as the original's " +
        "random walk: cruise short of the authored gate leaves the shake pivot dead still, a dive " +
        "past it rolls the pivot both ways with a swing amplitude that wanders rather than " +
        "repeating one envelope, and an engage swings the pivot on the decoded ramp law's own " +
        "rate, ramping into each turn instead of wrapping like the sawtooth it replaced")]
    internal static void PlaneWobbleWalk(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? rig = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
            var inputs = SuiteConstants.AircraftResources(ctx, planesGamez, textures);
            // worldEffects null!: never dereferenced here, as in FlightTelemetrySuites, because
            // CrashProgram/WorldScene stay null and the spawner's crash-runtime block is skipped.
            var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, null!, ctx.Host, inputs,
                new FlightWorldBindings { Projectiles = live, Gamez = planesGamez }, new HumanRosterBindings());

            var at = new Vector3(0f, 500f, 0f);
            rig = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, at, at + Vector3.Forward,
                AiPilot.HoldingCourse(at, at + Vector3.Forward),
                Scheme: null, Team: InstantActionRuntime.EnemyTeam));
            float rated = rig.Stats?.FdSpeed ?? 0f;
            if (rig.ShakePivot == null || rig.Shake == null || rated <= 0f)
            {
                ctx.Check(false, $"the roster built the rig's shake and its pivot (rated max {rated})");
                return;
            }

            // Cruise: nothing else kicks an untouched aeroplane, so the gate is the only reason
            // the pivot could move, and below it the original's own updater never calls the kicker.
            var cruise = Drive(rig, at, rated, CruiseRatio, ticks: 60);
            ctx.Check(cruise.All(r => r == 0f),
                $"cruise at {CruiseRatio:0.00} of rated max leaves the shake pivot dead still (worst {cruise.Max(Mathf.Abs):E2} rad)");

            var dive = Drive(rig, at, rated, DiveRatio, ticks: 300);
            var swings = Swings(dive);
            float peak = dive.Max(Mathf.Abs);
            double mean = swings.Count == 0 ? 0.0 : swings.Average(s => Mathf.Abs(s.Roll));
            double spread = swings.Count == 0 ? 0.0
                : Mathf.Sqrt(swings.Average(s => (Mathf.Abs(s.Roll) - mean) * (Mathf.Abs(s.Roll) - mean)));
            ctx.Note($"dive at {DiveRatio:0.00}x rated max: peak {peak:E2} rad over {dive.Count} ticks, {swings.Count} swings, mean {mean:E2}, spread {spread:E2}");
            ctx.Check(peak > 0f && swings.Any(s => s.Roll > 0f) && swings.Any(s => s.Roll < 0f),
                $"a dive past the gate rolls the plane node both ways (peak {peak:E2} rad, {swings.Count} swings)");
            // The discriminator: a deterministic waveform under a constant drive repeats one
            // amplitude, where a walk re-kicked every tick cannot.
            ctx.Check(mean > 0.0 && spread / mean > 0.15,
                $"…with a swing amplitude that wanders rather than repeating one envelope (spread/mean {spread / System.Math.Max(mean, 1e-9):0.000})");

            // The engage, flown as a person flies it, back under the speed gate so the nitro
            // source is the only thing the pivot carries. The dive's block rings on for about a
            // second after the gate shuts, so this waits it out; reading across it sums two sources.
            var settle = Drive(rig, at, rated, CruiseRatio, ticks: 180);
            ctx.Check(settle[^1] == 0f,
                $"the dive's rattle rings down to rest once the gate shuts (last {settle[^1]:E2} rad)");
            rig.IsHumanPiloted = true;
            rig.Nitro.Installed = true;
            rig.AutoNitro = true;
            var engage = Drive(rig, at, rated, CruiseRatio, ticks: 180);
            var engageSwings = Swings(engage);
            float engagePeak = engage.Max(Mathf.Abs);
            float biggestStep = 0f;
            for (int i = 1; i < engage.Count; i++)
            {
                biggestStep = Mathf.Max(biggestStep, Mathf.Abs(engage[i] - engage[i - 1]));
            }
            var gaps = engageSwings.Skip(1).Select((s, i) => s.Tick - engageSwings[i].Tick).ToList();
            ctx.Note($"engage: peak {engagePeak:E2} rad, {engageSwings.Count} swings, gaps [{string.Join(",", gaps)}], biggest tick step {biggestStep:E2}");
            ctx.Check(rig.Nitro.Boosting && engagePeak > 0f,
                $"the engage reaches the plane node (boosting={rig.Nitro.Boosting}, peak {engagePeak:E2} rad)");
            ctx.Check(biggestStep < engagePeak * 0.5f,
                $"…ramping into each turn rather than wrapping like a sawtooth (biggest tick step {biggestStep:E2} of peak {engagePeak:E2})");
            ctx.Check(gaps.Count >= 4 && gaps.All(g => g is >= 8 and <= 11),
                $"…at the ramp law's own rate rather than the authored 4 Hz (gaps [{string.Join(",", gaps)}])");
        }
        finally
        {
            rig?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // Flies the rig at a fixed multiple of its rated max, one warp per tick so the speed read the
    // flight step makes is the one this arm asked for rather than whatever the plant drifted to,
    // and collects the roll the step wrote to the shake pivot.
    private static List<float> Drive(FlightController rig, Vector3 at, float rated, float ratio, int ticks)
    {
        var rolls = new List<float>(ticks);
        for (int i = 0; i < ticks; i++)
        {
            rig.WarpTo(at, 0f, rated * ratio);
            rig.SimStep(Dt);
            rolls.Add(rig.ShakePivot?.Rotation.Z ?? 0f);
        }
        return rolls;
    }

    // Every turning point of a roll trace, with the tick it turned on: swing amplitude and swing
    // rate, which is what separates the decoded walk from the periodic waveform it replaced.
    private static List<(float Roll, int Tick)> Swings(List<float> rolls)
    {
        var swings = new List<(float Roll, int Tick)>();
        float last = 0f;
        for (int i = 1; i < rolls.Count; i++)
        {
            float step = rolls[i] - rolls[i - 1];
            if (step == 0f)
            {
                continue;
            }
            if (last != 0f && Mathf.Sign(step) != Mathf.Sign(last))
            {
                swings.Add((rolls[i - 1], i));
            }
            last = step;
        }
        return swings;
    }
}
