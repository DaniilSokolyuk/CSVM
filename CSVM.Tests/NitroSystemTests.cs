using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Utils;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The nitro lifecycle against the decode in docs/org/flightModel.md, "Nitro": the one-shot engage
/// from a 99 % tank, the 3/s net burn, the 5 % cutoff, the 28.2 s refill to re-arm, the injector
/// and engine-out gates, the AI arm's missing engage line, the animation edges, and the force
/// couplings (lever replaced by 1.8, drag scaled by 0.8) on a nitro engine against a plain one.
/// </summary>
public class NitroSystemTests
{
    private const float Dt = 1f / 60f;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [Fact]
    public void ANitroEngineSpawnsWithAFullTankAndNoBoost()
    {
        var n = Installed();
        Assert.Equal(NitroSystem.Capacity, n.Charge);
        Assert.False(n.Boosting);
        Assert.Equal(30f, NitroSystem.Capacity);
    }

    [Fact]
    public void AHeldCommandEngagesFromAFullTank()
    {
        var n = Installed();
        Tick(n, held: true);
        Assert.True(n.Boosting);
        Assert.True(n.EngagedThisTick, "the edge survives the tank update, which is where its only consumer reads it");
    }

    // The edge's whole point is being readable at the END of the step that raised it: the boost
    // animation, the shake and the loop sound all hang off this one read, and a clear placed
    // between the arm and the read silently cancels all three.
    [Fact]
    public void TheEngageEdgeSurvivesTheTankUpdateAndClearsOnTheNextStep()
    {
        var n = Installed();
        n.BeginStep();
        n.HumanCommand(true, false, Dt);
        Assert.True(n.EngagedThisTick);
        Assert.True(n.BoostAnimAlive);
        n.Advance(Dt, false);
        Assert.True(n.EngagedThisTick);
        n.BeginStep();
        Assert.False(n.EngagedThisTick);
    }

    [Fact]
    public void WithoutTheInjectorNothingEngages()
    {
        var n = new NitroSystem();
        for (int i = 0; i < 60; i++)
            Tick(n, held: true);
        Assert.False(n.Boosting);
        Assert.Equal(NitroSystem.Capacity, n.Charge);
    }

    [Fact]
    public void AnEngineOutAircraftCannotEngageAndLosesARunningBoost()
    {
        var n = Installed();
        Tick(n, held: true, engineOut: true);
        Assert.False(n.Boosting);

        var running = Installed();
        Tick(running, held: true);
        Assert.True(running.Boosting);
        running.EngineLost(Dt);
        Assert.False(running.Boosting);
    }

    [Fact]
    public void ReleasingTheCommandDoesNotStopABurn()
    {
        var n = Installed();
        Tick(n, held: true);
        for (int i = 0; i < 120; i++)
            Tick(n, held: false);
        Assert.True(n.Boosting);
        Assert.True(n.Charge < NitroSystem.Capacity - 5f, $"the tank should be spending: {n.Charge}");
    }

    [Fact]
    public void ABurnSpendsTheTankAtThreePerSecondNetAndEndsAtTheCutoff()
    {
        var n = Installed();
        Tick(n, held: true);
        float afterOneSecond = 0f;
        float seconds = 0f;
        int ticks = 0;
        while (n.Boosting && ticks < 60 * 30)
        {
            Tick(n, held: false);
            ticks++;
            seconds += Dt;
            if (ticks == 60)
                afterOneSecond = n.Charge;
        }

        // Burn 4/s against the unconditional refill 1/s: 3/s net, so the first second costs 3.
        Assert.InRange(NitroSystem.Capacity - afterOneSecond, 3f - 0.1f, 3f + 0.1f);
        // 30 → 1.5 at 3/s is 9.5 s.
        Assert.False(n.Boosting);
        Assert.InRange(seconds, 9.5f - 0.1f, 9.5f + 0.1f);
        Assert.True(n.Charge < NitroSystem.CutoffFraction * NitroSystem.Capacity + 0.1f);
    }

    [Fact]
    public void AHeldCommandCannotReEngageUntilTheTankRefillsToTheEngageLine()
    {
        var n = Installed();
        Tick(n, held: true);
        while (n.Boosting)
            Tick(n, held: false);
        n.DecayFinished();

        float seconds = 0f;
        while (!n.Boosting && seconds < 60f)
        {
            Tick(n, held: true);
            seconds += Dt;
        }

        // 1.5 → 29.7 at 1/s is 28.2 s, from the cutoff to the engage line.
        Assert.True(n.Boosting, "the tank refilled and the held command never re-engaged");
        Assert.InRange(seconds, 28.2f - 0.15f, 28.2f + 0.15f);
    }

    [Fact]
    public void ATankUnderTheEngageLineIgnoresTheCommand()
    {
        // METHOD-9's control for the line: the same held command from a 98 % tank stays off, and
        // from a 99 % tank engages.
        var n = Installed();
        Tick(n, held: true);
        while (n.Boosting)
            Tick(n, held: false);
        n.DecayFinished();
        while (n.Charge < 0.98f * NitroSystem.Capacity)
            Tick(n, held: false);
        Tick(n, held: true);
        Assert.False(n.Boosting);
        while (n.Charge < NitroSystem.EngageFraction * NitroSystem.Capacity)
            Tick(n, held: false);
        Tick(n, held: true);
        Assert.True(n.Boosting);
    }

    [Fact]
    public void TheDecayAnimationBlocksAReEngageUntilItFinishes()
    {
        var n = Installed();
        n.AiSet(true, false, Dt);
        Assert.True(n.Boosting);
        // Two seconds in, the AI releases. The engage timer only runs inside the setter, which the
        // AI reaches once per frame only outside a nitro maneuver, so the boost animation lasts
        // one more second of release calls before the decay starts.
        for (int i = 0; i < 120; i++)
            n.Advance(Dt, false);
        n.AiSet(false, false, Dt);
        Assert.False(n.Boosting);
        Assert.True(n.BoostAnimAlive);
        for (int i = 0; i < 60; i++)
            n.AiSet(false, false, Dt);
        Assert.True(n.ReleasedThisTick);
        Assert.False(n.BoostAnimAlive);
        Assert.True(n.DecayAnimPlaying);

        n.AiSet(true, false, Dt);
        Assert.False(n.Boosting);
        n.DecayFinished();
        n.AiSet(true, false, Dt);
        Assert.True(n.Boosting);
    }

    [Fact]
    public void TheBoostAnimationOutlivesAnEarlyReleaseForOneSecond()
    {
        var n = Installed();
        n.AiSet(true, false, Dt);
        n.AiSet(false, false, Dt);
        Assert.False(n.Boosting);
        Assert.True(n.BoostAnimAlive, "released inside the first second, the boost def keeps playing");
        for (int i = 0; i < 70; i++)
            n.AiSet(false, false, Dt);
        Assert.False(n.BoostAnimAlive);
        Assert.True(n.DecayAnimPlaying);
    }

    [Fact]
    public void TheAiArmHasNoEngageLine()
    {
        var n = Installed();
        n.AiSet(true, false, Dt);
        for (int i = 0; i < 300; i++)
            n.Advance(Dt, false);
        for (int i = 0; i < 70; i++)
            n.AiSet(false, false, Dt);
        n.DecayFinished();
        Assert.True(n.Charge < NitroSystem.EngageFraction * NitroSystem.Capacity);
        n.AiSet(true, false, Dt);
        Assert.True(n.Boosting, "the AI engages from any tank above the cutoff");

        var human = Installed();
        human.HumanCommand(true, false, Dt);
        for (int i = 0; i < 300; i++)
            human.Advance(Dt, false);
        Assert.True(human.Boosting);
    }

    /// <summary>The keyed loop sound's cadence on the AI arm, which is not a sustain: the refresh
    /// lives inside the setter, the maneuver starter reaches it once, and nothing reaches it again
    /// until the per-frame release calls that follow the maneuver. So the loop is a blip at the
    /// engage and about a second of sound after the maneuver ends, which is the whole of what an
    /// AI's injector is audible for.</summary>
    [Fact]
    public void TheAiLoopIsABlipAtTheEngageAndASecondAfterTheManeuver()
    {
        var n = Installed();
        n.BeginStep();
        n.AiSet(true, false, Dt);
        n.Advance(Dt, false);
        Assert.True(n.LoopRefreshedThisTick, "the engage's own call refreshes the loop");

        // nitro_evade's single step is six seconds long, and nothing calls the setter inside it.
        int duringManeuver = 0;
        for (int i = 0; i < 360; i++)
        {
            n.BeginStep();
            n.Advance(Dt, false);
            if (n.LoopRefreshedThisTick)
                duringManeuver++;
        }

        Assert.Equal(0, duringManeuver);
        Assert.True(n.BoostAnimAlive, "the boost animation is still alive, and still silent");

        int afterManeuver = 0, ticks = 0;
        while (n.BoostAnimAlive && ticks < 600)
        {
            n.BeginStep();
            n.AiSet(false, false, Dt);
            n.Advance(Dt, false);
            if (n.LoopRefreshedThisTick)
                afterManeuver++;
            ticks++;
        }

        Assert.InRange(afterManeuver * Dt, 0.95f, 1.05f);
        Assert.False(n.LoopRefreshedThisTick, "the tick that stops the boost animation refreshes nothing");
    }

    /// <summary>The human arm's own cadence, for contrast: with the command held every frame lands
    /// in one of the three arms, so the loop is refreshed continuously and sounds for the whole
    /// burn. ⚠ Not "every frame whatever the input": a released command inside the first 0.1 s,
    /// while the tank still reads at or above the engage line, reaches no arm at all.</summary>
    [Fact]
    public void TheHumanLoopIsRefreshedEveryFrameOfAHeldBurn()
    {
        var n = Installed();
        Tick(n, held: true);
        Assert.True(n.LoopRefreshedThisTick);
        for (int i = 0; i < 120; i++)
        {
            Tick(n, held: true);
            Assert.True(n.LoopRefreshedThisTick, $"frame {i} of the burn left the loop unrefreshed");
        }
    }

    [Fact]
    public void TheAiArmNeedsTheInjectorToo()
    {
        var n = new NitroSystem();
        n.AiSet(true, false, Dt);
        Assert.False(n.Boosting);
    }

    [Fact]
    public void ResetRefillsTheTankAndClearsTheStateButNotTheInjector()
    {
        var n = Installed();
        Tick(n, held: true);
        Tick(n, held: false);
        n.Reset();
        Assert.Equal(NitroSystem.Capacity, n.Charge);
        Assert.False(n.Boosting);
        Assert.False(n.BoostAnimAlive);
        Assert.True(n.Installed);
    }

    /// <summary>The force couplings: with the boost flag set the lever is 1.8 whatever the throttle
    /// (an idle boost accelerates exactly like a full-throttle boost) and the drag coefficient is
    /// 0.8 of itself. Measured off one integration step from a level cruise, where thrust and drag
    /// are the only along-path terms.</summary>
    [ExtractedDataFact]
    public void TheBoostReplacesTheLeverAndScalesTheDrag()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        float plainFull = AlongPathAccel(stats, 1f, boost: false);
        float boostFull = AlongPathAccel(stats, 1f, boost: true);
        float boostIdle = AlongPathAccel(stats, 0f, boost: true);
        Assert.True(boostFull > plainFull + 1f,
            $"the boost adds no acceleration: {boostFull:0.000} against {plainFull:0.000} m/s²");
        Assert.True(Mathf.Abs(boostFull - boostIdle) < 1e-4f,
            $"an idle boost ({boostIdle:0.000}) differs from a full-throttle boost ({boostFull:0.000}): "
            + "the lever is replaced, not scaled");

        // Separate the two couplings: the thrust curve at lever 1.8 alone, and the drag remainder.
        var m = new FlightModel(stats);
        float speed = stats.FdSpeed * 0.8f;
        float thrustPlain = m.ThrustAccelAt(speed, 1f);
        float thrustBoost = m.ThrustAccelAt(speed, 1.8f);
        float dragPlain = thrustPlain - plainFull;
        float dragBoost = thrustBoost - boostFull;
        Assert.InRange(dragBoost / dragPlain, 0.8f - 1e-3f, 0.8f + 1e-3f);
    }

    /// <summary>The same plant with the flag clear is the plain-engine plant to the bit, which is
    /// the eleven-airframe dump's invariance stated at the step level.</summary>
    [ExtractedDataFact]
    public void APlainEngineIsTheUnboostedPlantExactly()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        var a = Cruise(stats);
        var b = Cruise(stats);
        for (int i = 0; i < 600; i++)
        {
            a.Step(new FlightInput { Throttle = 0.7f }, Dt);
            b.Step(new FlightInput { Throttle = 0.7f, Boost = false }, Dt);
        }

        Assert.Equal(a.Speed, b.Speed);
        Assert.Equal(a.Position, b.Position);
    }

    private static NitroSystem Installed() => new() { Installed = true };

    // One human tick as FlightController.AdvanceNitro runs it: the step opens, then the command
    // arm, then the tank update, and the caller reads the edges after all three.
    private static void Tick(NitroSystem n, bool held, bool engineOut = false)
    {
        n.BeginStep();
        n.HumanCommand(held, engineOut, Dt);
        n.Advance(Dt, engineOut);
    }

    private static FlightModel Cruise(PlaneStats stats)
    {
        var m = new FlightModel(stats);
        m.Reset(new Vector3(0f, 500f, 0f), Basis.Identity, stats.FdSpeed * 0.8f, 1f);
        return m;
    }

    private static float AlongPathAccel(PlaneStats stats, float throttle, bool boost)
    {
        var m = Cruise(stats);
        float before = m.Speed;
        m.Step(new FlightInput { Throttle = throttle, Boost = boost }, Dt);
        return (m.Speed - before) / Dt;
    }
}
