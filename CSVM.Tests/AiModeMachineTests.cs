using System;
using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The decoded mode machine's transition table, engine-free: the nine modes under fixed rolls
/// (a pool-covering bite, a zero exponent) and seeded rngs. Pins the promotion and its dwell, the
/// return-cylinder exit measured from the pursuit anchor, and the anchor's own life. The
/// steady-hand power law and the sixth-sense roll are pinned in the original's own vocabulary. So
/// are the evasive maneuver playing to Done and returning, the avoid-crash override on an injected
/// probe, and the signature-maneuver weighting. The D15 lay-off entry and exit (a chasing human
/// fallen behind, gated by the AssistEnabled switch) and seed determinism are pinned too.
/// </summary>
public class AiModeMachineTests
{
    // A dt that always leaves the per-plane probe timer due, whatever it drew from its rng.
    private const float Due = AiModeMachine.ProbeIntervalMaxS;

    private static readonly Vector3 Home = new(0f, 400f, 0f);
    private static readonly Vector3 Level = new(0f, 0f, -100f); // 100 m/s along -Z

    // Lay-off geometry: dead astern of a plane flying Level, and a velocity closing on it.
    private static readonly Vector3 Astern600 = Home + new Vector3(0f, 0f, 600f);
    private static readonly Vector3 Chasing = new(0f, 0f, -80f);

    [Fact]
    public void ModeNamesAreTheEngineVocabulary()
    {
        Assert.Equal("patrol", AiModeMachine.NameOf(AiMode.Patrol));
        Assert.Equal("pursue", AiModeMachine.NameOf(AiMode.Pursue));
        Assert.Equal("lay off", AiModeMachine.NameOf(AiMode.LayOff));
        Assert.Equal("evade", AiModeMachine.NameOf(AiMode.Evade));
        Assert.Equal("evasive maneuver", AiModeMachine.NameOf(AiMode.EvasiveManeuver));
        Assert.Equal("stunned", AiModeMachine.NameOf(AiMode.Stunned));
        Assert.Equal("avoid crash", AiModeMachine.NameOf(AiMode.AvoidCrash));
        Assert.Equal("approaching danger zone", AiModeMachine.NameOf(AiMode.ApproachingDangerZone));
        Assert.Equal("navigating danger zone", AiModeMachine.NameOf(AiMode.NavigatingDangerZone));
    }

    /// <summary>The promotion reads no range. The attack radius is the selection's admission
    /// test, so whatever quarry the machine is handed is chased the tick the dwell allows.</summary>
    [Fact]
    public void PatrolPromotesAnyHandedQuarryWithNoRangeOfItsOwn()
    {
        var m = Machine();
        m.ActivationRange = 1000f;
        m.AttackRange = 1000f;
        var transitions = new List<(AiMode From, AiMode To)>();
        m.ModeChanged += (from, to, _) => transitions.Add((from, to));

        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, 0.1f));
        Assert.Empty(transitions);

        Assert.Equal(AiMode.Pursue, m.Update(Home, Level, Home + new Vector3(2500f, 0f, 0f), null, 0.1f));
        Assert.Equal((AiMode.Patrol, AiMode.Pursue), Assert.Single(transitions));
    }

    /// <summary>A promotion arms the chase's deadline to attack_dwell, and the pursuit reverts
    /// the tick it passes. The revert re-arms not_pursuit_dwell, which refuses the next promotion
    /// until it has passed too.</summary>
    [Fact]
    public void TheChaseRevertsAtTheAttackDwellAndThePromotionWaitsOutTheRearm()
    {
        var m = Machine();
        m.AttackDwellS = 60f;
        m.NotPursuitDwellS = 5f;
        var target = Home + new Vector3(500f, 0f, 0f);
        PursueFrom(m, target);
        Assert.Equal(60f, m.DwellRemainingS, 3);

        Assert.Equal(AiMode.Pursue, Step(m, target, 59.8f));
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, target, null, 0.3f));
        Assert.Equal(5f, m.DwellRemainingS, 3);

        // Refused while the stamp stands, with the quarry still handed over every tick.
        Assert.Equal(AiMode.Patrol, Step(m, target, 4.8f));
        Assert.Equal(AiMode.Pursue, m.Update(Home, Level, target, null, 0.3f));
    }

    /// <summary>The return cylinder's revert re-arms the wait: the original's re-promotion on the
    /// very next tick, with a fresh anchor, is exactly what this refuses.</summary>
    [Fact]
    public void TheCylinderRevertRearmsTheWaitAndATurretTakesTheFifteenSecondHold()
    {
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        PursueFrom(m, target);
        var strayed = Home + new Vector3(1300f, 0f, 0f);
        Assert.Equal(AiMode.Patrol, m.Update(strayed, Level, target, null, 0.1f));
        Assert.Equal(m.NotPursuitDwellS, m.DwellRemainingS, 3);
        Assert.Equal(AiMode.Patrol, m.Update(strayed, Level, target, null, 0.1f));

        // A structure quarry: 20 s to promote on, 15 s of wait after the cylinder.
        var t = Machine();
        Assert.Equal(AiMode.Pursue, t.Update(Home, Level, target, null, 0.1f, quarryIsVehicle: false));
        Assert.Equal(AiModeMachine.NonVehicleAttackDwellS, t.DwellRemainingS, 3);
        Assert.Equal(AiMode.Patrol, t.Update(strayed, Level, target, null, 0.1f, quarryIsVehicle: false));
        Assert.Equal(AiModeMachine.NonVehicleNotPursuitDwellS, t.DwellRemainingS, 3);
    }

    /// <summary>A lost target re-arms not_pursuit_dwell, with no 15 s variant.</summary>
    [Fact]
    public void ALostTargetRearmsTheNotPursuitDwell()
    {
        var m = Machine();
        m.NotPursuitDwellS = 5f;
        var target = Home + new Vector3(500f, 0f, 0f);
        Assert.Equal(AiMode.Pursue, m.Update(Home, Level, target, null, 0.1f, quarryIsVehicle: false));
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, 0.1f));
        Assert.Equal(5f, m.DwellRemainingS, 3);
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, target, null, 1f));
    }

    /// <summary>The assigned primary_target lifts both ends: it is promoted onto inside the wait,
    /// and neither the deadline nor the cylinder ends that chase.</summary>
    [Fact]
    public void TheAssignedTargetIsExemptFromTheDwellAndTheLeash()
    {
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        PursueFrom(m, target);
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, 0.1f));
        Assert.True(m.DwellRemainingS > 0f);

        Assert.Equal(AiMode.Pursue, m.Update(Home, Level, target, null, 0.1f, quarryIsPrimary: true));
        var strayed = Home + new Vector3(5000f, 0f, 0f);
        Assert.Equal(AiMode.Pursue,
            m.Update(strayed, Level, target, null, 120f, quarryIsPrimary: true));

        // Once the quarry is no longer the assigned one, the passed deadline ends it.
        Assert.Equal(AiMode.Patrol, m.Update(strayed, Level, target, null, 0.1f));
    }

    /// <summary>An ordered engagement arms its own deadline, so a stamp left from the machine's
    /// start does not revert it on the next tick.</summary>
    [Fact]
    public void AnOrderedPursuitArmsItsOwnDeadline()
    {
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        m.Update(Home, Level, null, null, 100f);
        m.Enter(AiMode.Pursue);
        Assert.Equal(AiMode.Pursue, m.Update(Home, Level, target, null, 0.1f));
    }

    /// <summary>The dwell ends a pursue task only. An evade ordered off the net that loses its
    /// target leaves the wait alone. One that settles into pursue takes a fresh deadline rather
    /// than reverting on a stamp left from the machine's start.</summary>
    [Fact]
    public void AnEvadeOffTheNetIsNoChaseForTheDwellToEnd()
    {
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        m.Update(Home, Level, null, null, 100f);
        m.Enter(AiMode.Evade);
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, 0.1f));
        Assert.Equal(0f, m.DwellRemainingS);

        m.Enter(AiMode.Evade);
        Assert.Equal(AiMode.Pursue, m.Update(Home, Level, target, null, 0.1f));
        Assert.Equal(m.AttackDwellS, m.DwellRemainingS, 3);
        Assert.Equal(AiMode.Pursue, m.Update(Home, Level, target, null, 0.1f));
    }

    [Fact]
    public void PursuitEndsOnTargetLossAndOnTheReturnCylinderAlone()
    {
        // Target lost: straight back to patrol.
        var m = Machine();
        PursueFrom(m, Home + new Vector3(1500f, 0f, 0f));
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, 0.1f));

        // The decoded leash reverts on its own: outside the cylinder from the anchor. The target
        // is 300 m off, deep inside the activation radius the condition no longer reads.
        var m2 = Machine();
        PursueFrom(m2, Home + new Vector3(1500f, 0f, 0f));
        var strayed = Home + new Vector3(1300f, 0f, 0f); // > 1200 from the anchor
        Assert.Equal(AiMode.Patrol,
            m2.Update(strayed, Level, strayed + new Vector3(300f, 0f, 0f), null, 0.1f));

        // Inside it, a target far outside the activation radius does not end the chase.
        var m3 = Machine();
        PursueFrom(m3, Home + new Vector3(1500f, 0f, 0f));
        var farTarget = Home + new Vector3(4000f, 0f, 0f); // > 2000 from the plane
        Assert.Equal(AiMode.Pursue, m3.Update(Home + new Vector3(900f, 0f, 0f), Level, farTarget, null, 0.1f));
    }

    /// <summary>The leash is the decoded CYLINDER, not a sphere. The corner between the horizontal
    /// radius and the vertical band is inside it. The band alone recalls a pursuer that has
    /// climbed away without straying at all.</summary>
    [Fact]
    public void TheReturnLeashIsACylinderAboutTheAnchor()
    {
        var target = Home + new Vector3(500f, 0f, 0f);

        // 1000 m out and 1000 m up: 1414 m from the anchor, inside a 1200 m cylinder.
        var m = Machine();
        PursueFrom(m, target);
        Assert.Equal(AiMode.Pursue,
            m.Update(Home + new Vector3(1000f, 1000f, 0f), Level, target, null, 0.1f));

        // Straight up past the band, with no horizontal stray at all.
        var m2 = Machine();
        PursueFrom(m2, target);
        Assert.Equal(AiMode.Patrol,
            m2.Update(Home + new Vector3(0f, 1300f, 0f), Level, target, null, 0.1f));
    }

    /// <summary>The anchor is taken once, where the promotion happened, and nothing while the task
    /// stands moves it. A pilot is leashed to where it caught its quarry, not to where it spawned.
    /// A finished evasive program hands back to that same point.</summary>
    [Fact]
    public void ThePursuitAnchorIsWhereTheChaseBeganAndSurvivesAReaction()
    {
        var m = Machine();
        m.Library = new[] { QuickManeuver("bank_turn", 1) };
        Assert.Null(m.PursuitAnchor);

        // The patrol has carried it 6 km from Home before anything comes into reach.
        var chaseStart = Home + new Vector3(6000f, 0f, 0f);
        var target = chaseStart + new Vector3(500f, 0f, 0f);
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, 1f / 60f));
        Assert.Equal(AiMode.Pursue, m.Update(chaseStart, Level, target, null, 1f / 60f));
        Assert.Equal(chaseStart, m.PursuitAnchor!.Value);

        // A hit, its program flown to the end, and the anchor is still the chase's own start.
        var model = new FlightModel(new PlaneStats());
        m.SteadyHandExponent = float.PositiveInfinity;
        m.NotifyDamage(0f, 8f, 0f, 8f);
        Assert.Equal(AiMode.EvasiveManeuver, m.Mode);
        for (int i = 0; i < 40 && !m.Executor!.Done; i++)
            m.Executor.Next(model, 0.02f);
        Assert.Equal(AiMode.Pursue,
            m.Update(chaseStart, Level, target, null, 1f / 60f, targetNose: new Vector3(0f, 0f, -1f)));
        Assert.Equal(chaseStart, m.PursuitAnchor!.Value);

        // Leaving the cylinder reverts the task, and the anchor goes with it.
        var strayed = chaseStart + new Vector3(1300f, 0f, 0f);
        Assert.Equal(AiMode.Patrol, m.Update(strayed, Level, target, null, 1f / 60f));
        Assert.Null(m.PursuitAnchor);
    }

    [Fact]
    public void FailedSteadyHandEvadesAndAPassedOneDoesNot()
    {
        // A bite covering the whole pool fails outright whatever the exponent, in the decoded
        // vocabulary. With no library there is nothing to fly, so the flag stands over the
        // engagement and nothing breaks off.
        var m = Machine();
        string? logged = null;
        m.RollLogged += line => logged = line;
        PursueFrom(m, Home + new Vector3(500f, 0f, 0f));
        m.NotifyDamage(0f, 12f, 0f, 12f);
        Assert.True(m.Evading);
        Assert.Equal(AiMode.Evade, m.Mode);
        Assert.Contains("steady hand test failed. Evading.", logged);
        Assert.Contains("absorbed 12.0 damage", logged);

        // A zero exponent passes every roll, whatever the bite, so nothing moves.
        var m2 = Machine();
        string? logged2 = null;
        m2.RollLogged += line => logged2 = line;
        PursueFrom(m2, Home + new Vector3(500f, 0f, 0f));
        m2.SteadyHandExponent = 0f;
        m2.NotifyDamage(0f, 5f, 0f, 100f);
        Assert.Equal(AiMode.Pursue, m2.Mode);
        Assert.False(m2.Evading);
        Assert.Contains("steady hand test passed. Not evading.", logged2);
    }

    /// <summary>The roll is the decoded power law over the bite, not a flat chance. The same round
    /// evades far more often out of a worn-down pool than a fresh one. The exponent a higher
    /// rating resolves to evades MORE rather than less.</summary>
    [Fact]
    public void TheSteadyHandRollRisesWithTheBiteAndWithTheRating()
    {
        // The decoded spawn conversion, over the shipped 0.5-to-0.08 pair's endpoints.
        Assert.Equal(1.9434f, AiModeMachine.ExponentFor(0.5f), 3);
        Assert.Equal(3.7058f, AiModeMachine.ExponentFor(0.2666667f), 3);
        Assert.Equal(7.0813f, AiModeMachine.ExponentFor(0.08f), 3);

        // A wep_130 round (1.5 armour, 1.5 health) on a hostile Fury at Normal meets 108 of pool
        // at full health. Worn down, it meets a tenth of that.
        float e0 = AiModeMachine.ExponentFor(0.5f), e9 = AiModeMachine.ExponentFor(0.08f);
        double fresh0 = 1d - AiModeMachine.PassChance(1.5f, 108f, e0);
        double fresh9 = 1d - AiModeMachine.PassChance(1.5f, 108f, e9);
        double worn9 = 1d - AiModeMachine.PassChance(1.5f, 10.8f, e9);
        Assert.Equal(0.027, fresh0, 3);
        Assert.Equal(0.094, fresh9, 3);
        Assert.Equal(0.653, worn9, 3);
        Assert.True(fresh9 > fresh0, "the better pilot evades more often at the same bite");
        Assert.True(worn9 > fresh9, "and the same round evades more out of a worn-down pool");

        // The bite is armour-then-health against the pre-hit pair, and one covering the pool
        // never passes.
        Assert.Equal(2f, AiModeMachine.BiteOf(2f, 5f, 10f, 20f));
        Assert.Equal(15f, AiModeMachine.BiteOf(12f, 5f, 10f, 20f));
        Assert.Equal(30f, AiModeMachine.BiteOf(12f, 25f, 10f, 20f));
        Assert.Equal(0d, AiModeMachine.PassChance(30f, 30f, e0));
    }

    /// <summary>One impact takes several rolls when the pair outlives the pools it meets. The
    /// wrapper's leftover loop runs each pass against a pool the last one shrank.</summary>
    [Fact]
    public void LeftoverDamageTakesAFurtherRollAgainstAShrunkPool()
    {
        var m = Machine();
        var rolls = new List<string>();
        m.RollLogged += rolls.Add;
        PursueFrom(m, Home + new Vector3(500f, 0f, 0f));
        m.SteadyHandExponent = 0f; // every roll passes, so the loop is what the count shows

        // Armour 10 against 20 of armour damage: half the health damage is shielded, the rest
        // spends. Both leftovers re-enter against what is left of the pair.
        m.NotifyDamage(20f, 40f, 10f, 100f);
        Assert.True(rolls.Count >= 2, $"the leftover re-entered rolls={rolls.Count}");
        Assert.False(m.Evading);

        // A hit the first pass swallows whole takes exactly one roll.
        var m2 = Machine();
        var single = new List<string>();
        m2.RollLogged += single.Add;
        PursueFrom(m2, Home + new Vector3(500f, 0f, 0f));
        m2.SteadyHandExponent = 0f;
        m2.NotifyDamage(2f, 2f, 50f, 100f);
        Assert.Single(single);
    }

    /// <summary>The picker's injector cull: <c>nitro_evade</c> is difficulty 0. Only the injector
    /// keeps a pilot that cannot boost from flying six wings-level seconds as its
    /// evade.</summary>
    [Fact]
    public void ANitroFlaggedManeuverIsDrawnOnlyWithTheInjector()
    {
        var target = Home + new Vector3(500f, 0f, 0f);
        var library = new[] { NitroManeuver("nitro_evade"), QuickManeuver("bank_turn", 2) };

        var m = Machine();
        m.NaturalTouch = 9;
        m.Library = library;
        PursueFrom(m, target);
        m.NotifyDamage(0f, 8f, 0f, 8f);
        Assert.Equal("bank_turn", m.Executor!.Maneuver.Name);

        bool usable = true;
        var m2 = Machine();
        m2.NaturalTouch = 9;
        m2.Library = library;
        m2.NitroUsable = () => usable;
        int nitroPicks = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            var run = Machine(seed);
            run.NaturalTouch = 9;
            run.Library = library;
            run.NitroUsable = () => usable;
            PursueFrom(run, target);
            run.NotifyDamage(0f, 8f, 0f, 8f);
            if (run.Executor!.Maneuver.Nitro)
                nitroPicks++;
        }

        Assert.True(nitroPicks > 0, "the injector puts the flagged entry back in the draw");

        // The engine dying mid-fight takes it out again, the same cull on the same delegate.
        usable = false;
        PursueFrom(m2, target);
        m2.NotifyDamage(0f, 8f, 0f, 8f);
        Assert.False(m2.Executor!.Maneuver.Nitro);
    }

    /// <summary>The evade flag's own life: nothing times it out, the pursuer's nose alignment is
    /// the only thing that ends it, and it takes no second steady-hand roll while it stands.</summary>
    [Fact]
    public void TheEvadeFlagRunsOffThePursuersAlignmentAlone()
    {
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        var noseOn = new Vector3(-1f, 0f, 0f); // from the pursuer, straight at this aircraft
        PursueFrom(m, target);
        m.NotifyDamage(0f, 8f, 0f, 8f);
        Assert.True(m.Evading);

        // Twenty seconds with the nose held on, well past any plausible timeout: still set.
        for (int i = 0; i < 1200; i++)
            m.Update(Home, Level, target, null, 1f / 60f, targetNose: noseOn);
        Assert.True(m.Evading);
        Assert.Equal(AiMode.Evade, m.Mode);

        // A second hit rolls nothing at all while the flag stands, and says so rather than falling
        // silent. A trace can then tell a pilot that is never hit from one hit while already
        // evading.
        string? logged = null;
        m.RollLogged += line => logged = line;
        m.NotifyDamage(0f, 8f, 0f, 8f);
        Assert.Equal("absorbed 8.0 damage; no steady hand test (already evading)", logged);

        // The nose falls past the 0.85 cosine: the flag clears and the engagement resumes.
        m.Update(Home, Level, target, null, 1f / 60f, targetNose: new Vector3(0f, 0f, -1f));
        Assert.False(m.Evading);
        Assert.Equal(AiMode.Pursue, m.Mode);

        // The target gone instead: the flag clears and the pilot is back on patrol.
        var m2 = Machine();
        PursueFrom(m2, target);
        m2.NotifyDamage(0f, 8f, 0f, 8f);
        m2.Update(Home, Level, null, null, 1f / 60f);
        Assert.False(m2.Evading);
        Assert.Equal(AiMode.Patrol, m2.Mode);
    }

    /// <summary>The flag is the damage routine's to write. An ordered entry into the evade state
    /// leaves it clear, so the hit that follows still takes its own steady-hand roll.</summary>
    [Fact]
    public void AnOrderedEvadeCarriesNoFlag()
    {
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        string? logged = null;
        m.RollLogged += line => logged = line;
        PursueFrom(m, target);

        m.Enter(AiMode.Evade, "scripted");
        Assert.Equal(AiMode.Evade, m.Mode);
        Assert.False(m.Evading);

        // The roll the flag would have swallowed is taken, and it is what sets the flag.
        m.NotifyDamage(0f, 8f, 0f, 8f);
        Assert.Contains("steady hand test failed. Evading.", logged);
        Assert.True(m.Evading);

        // The same ordered entry through a maneuver: no flag either, and no executor to fly.
        var m2 = Machine();
        PursueFrom(m2, target);
        m2.Enter(AiMode.EvasiveManeuver, "scripted");
        Assert.False(m2.Evading);
        Assert.Null(m2.Executor);
    }

    /// <summary>A maneuver that runs out with the flag still set chains into another one, and the
    /// repeat penalty makes that another program rather than the same one again.</summary>
    [Fact]
    public void AnEvadeChainsAFreshManeuverWhenTheProgramRunsOut()
    {
        var target = Home + new Vector3(500f, 0f, 0f);
        var noseOn = new Vector3(-1f, 0f, 0f);
        var model = new FlightModel(new PlaneStats());
        int repeats = 0;
        const int runs = 80;
        for (int seed = 0; seed < runs; seed++)
        {
            var m = Machine(seed);
            m.NaturalTouch = 9;
            m.Library = new[] { QuickManeuver("bank_turn", 2), QuickManeuver("split_s", 8) };
            PursueFrom(m, target);
            m.NotifyDamage(0f, 8f, 0f, 8f);
            Assert.Equal(AiMode.EvasiveManeuver, m.Mode);
            string first = m.Executor!.Maneuver.Name;

            for (int i = 0; i < 40 && !m.Executor!.Done; i++)
                m.Executor.Next(model, 0.02f);
            Assert.Equal(AiMode.EvasiveManeuver,
                m.Update(Home, Level, target, null, 1f / 60f, targetNose: noseOn));
            if (m.Executor!.Maneuver.Name == first)
                repeats++;

            // ⚠ The clear cannot cut a program short: the pursuer turning away mid-program
            // leaves both the flag and the maneuver alone.
            var off = new Vector3(0f, 0f, -1f);
            Assert.Equal(AiMode.EvasiveManeuver,
                m.Update(Home, Level, target, null, 1f / 60f, targetNose: off));
            Assert.True(m.Evading);

            // The chain ends where that program does.
            for (int i = 0; i < 40 && !m.Executor!.Done; i++)
                m.Executor.Next(model, 0.02f);
            Assert.Equal(AiMode.Pursue,
                m.Update(Home, Level, target, null, 1f / 60f, targetNose: off));
            Assert.False(m.Evading);
            Assert.Null(m.Executor);
        }

        // The 0.1 repeat weight against 1.0: about one draw in eleven, never the usual case.
        Assert.InRange(repeats, 0, 20);
    }

    /// <summary>The flag makes the break-off branch unreachable, so however long a human pursuer
    /// sits behind an evading pilot it never eases off to let them catch up.</summary>
    [Fact]
    public void AnEvadingPilotNeverEasesIntoLayOff()
    {
        var m = Machine();
        var noseOn = new Vector3(0f, 0f, -1f); // the chaser, pointed along its own closure
        PursueFrom(m, Astern600);
        m.NotifyDamage(0f, 8f, 0f, 8f);
        Assert.Equal(AiMode.Evade, m.Mode);

        for (int i = 0; i < 300; i++)
            m.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, true, targetNose: noseOn);
        Assert.True(m.Evading);
        Assert.Equal(AiMode.Evade, m.Mode);

        // The same geometry with no flag does ease off, which is what makes the check above bite.
        var m2 = Machine();
        PursueFrom(m2, Astern600);
        for (int i = 0; i < 300; i++)
            m2.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, true);
        Assert.Equal(AiMode.LayOff, m2.Mode);
    }

    [Fact]
    public void FailedSixthSenseStunsAndRecoversAfterTheInterval()
    {
        var m = Machine();
        string? logged = null;
        m.RollLogged += line => logged = line;
        var target = Home + new Vector3(500f, 0f, 0f);
        PursueFrom(m, target);
        m.SixthSenseChance = 0f; // the pass roll always fails
        m.StunRecoveryIntervalS = 1.5f;

        // The in-machine trigger: the pursued AI target breaks into an evasive state.
        m.Update(Home, Level, target, AiMode.EvasiveManeuver, 1f / 60f);
        Assert.Equal(AiMode.Stunned, m.Mode);
        Assert.Contains("Sixth sense test failed; AI now stunned.", logged);

        // Nothing moves the mode during the stun; it recovers to the prior mode after the
        // interval.
        for (int i = 0; i < (int)(1.5f * 60f) - 5; i++)
            Assert.Equal(AiMode.Stunned, m.Update(Home, Level, target, null, 1f / 60f));
        for (int i = 0; i < 15; i++)
            m.Update(Home, Level, target, null, 1f / 60f);
        Assert.Equal(AiMode.Pursue, m.Mode);

        // A passed roll is logged and does not stun.
        var m2 = Machine();
        string? logged2 = null;
        m2.RollLogged += line => logged2 = line;
        PursueFrom(m2, target);
        m2.SixthSenseChance = 1f;
        m2.NotifyTargetEvaded();
        Assert.Equal(AiMode.Pursue, m2.Mode);
        Assert.Contains("Sixth sense test passed.", logged2);
    }

    /// <summary>The ordnance stun (a SONIC/FLASH burst, the smoke screen) enters the same
    /// state 4 the sixth-sense fail does, from ANY mode: the original writes the state without
    /// reading it, so a running maneuver or climb-out is dropped. It clears on its own and
    /// returns to the mode it interrupted.</summary>
    [Fact]
    public void AStunPreemptsAnyModeAndReturnsToTheInterruptedOne()
    {
        // From pursue: neutral for the given seconds, then pursue again.
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        PursueFrom(m, target);
        string? reason = null;
        m.ModeChanged += (_, to, why) => { if (to == AiMode.Stunned) reason = why; };
        m.Stun(2f, "sonic burst");
        Assert.Equal(AiMode.Stunned, m.Mode);
        Assert.Equal("sonic burst", reason);
        Assert.Equal(2f, m.StunRemainingS, 3);
        for (int i = 0; i < 119; i++)
            Assert.Equal(AiMode.Stunned, m.Update(Home, Level, target, null, 1f / 60f));
        m.Update(Home, Level, target, null, 1f / 60f);
        m.Update(Home, Level, target, null, 1f / 60f);
        Assert.Equal(AiMode.Pursue, m.Mode);
        Assert.Equal(0f, m.StunRemainingS);

        // From an evasive maneuver: the executor is dropped, and the stun still returns to the
        // mode the maneuver itself would have returned to.
        var m2 = Machine();
        m2.NaturalTouch = 2;
        m2.Library = new[] { QuickManeuver("bank_turn", 2, duration: 5f) };
        PursueFrom(m2, target);
        m2.NotifyDamage(0f, 8f, 0f, 8f);
        Assert.Equal(AiMode.EvasiveManeuver, m2.Mode);
        m2.Stun(0.5f);
        Assert.Equal(AiMode.Stunned, m2.Mode);
        Assert.Null(m2.Executor);
        for (int i = 0; i < 40; i++)
            m2.Update(Home, Level, target, null, 1f / 60f);
        Assert.Equal(AiMode.Pursue, m2.Mode);

        // From avoid crash: the climb-out is overwritten too; with the line clear afterwards the
        // pilot is back on patrol, not stuck in either state.
        var m3 = Machine();
        bool blocked = true;
        m3.ProbeBlocked = (_, _) => blocked ? "test/obstacle" : null;
        Assert.Equal(AiMode.AvoidCrash, m3.Update(Home, Level, null, null, Due));
        m3.Stun(0.5f);
        Assert.Equal(AiMode.Stunned, m3.Mode);
        blocked = false;
        for (int i = 0; i < 40; i++)
            m3.Update(Home, Level, null, null, 1f / 60f);
        Assert.Equal(AiMode.Patrol, m3.Mode);

        // Zero or negative seconds is not a stun at all.
        var m4 = Machine();
        PursueFrom(m4, target);
        m4.Stun(0f);
        Assert.Equal(AiMode.Pursue, m4.Mode);
    }

    /// <summary>Re-entrancy is the design, not an accident: the original accepts its own stun
    /// state as an input state and OVERWRITES the expiry (clock + seconds, no max). The smoke
    /// screen holds a pilot by re-stunning every frame; a weaker hit shortens a longer stun.</summary>
    [Fact]
    public void AStunLandingOnAStunnedPilotOverwritesTheExpiry()
    {
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        PursueFrom(m, target);
        m.Stun(5f);
        for (int i = 0; i < 60; i++)
            m.Update(Home, Level, target, null, 1f / 60f);
        Assert.Equal(4f, m.StunRemainingS, 2);

        // Refreshed every frame for a second: still the full interval left at the end of it.
        for (int i = 0; i < 60; i++)
        {
            m.Stun(5f);
            m.Update(Home, Level, target, null, 1f / 60f);
        }
        Assert.Equal(AiMode.Stunned, m.Mode);
        Assert.InRange(m.StunRemainingS, 4.9f, 5f);

        // A shorter stun on top does not extend; it replaces, and the pilot recovers on the
        // shorter clock.
        m.Stun(0.5f);
        Assert.Equal(0.5f, m.StunRemainingS, 3);
        for (int i = 0; i < 40; i++)
            m.Update(Home, Level, target, null, 1f / 60f);
        Assert.Equal(AiMode.Pursue, m.Mode);

        // The sixth-sense stun goes through the same entry, so it is refreshable too.
        var m2 = Machine();
        PursueFrom(m2, target);
        m2.SixthSenseChance = 0f;
        m2.StunRecoveryIntervalS = 1f;
        m2.NotifyTargetEvaded();
        Assert.Equal(AiMode.Stunned, m2.Mode);
        m2.Stun(3f);
        Assert.Equal(3f, m2.StunRemainingS, 3);
    }

    /// <summary>The stun cannot strand a pilot: an external mode override during it releases the
    /// controls. The damage handler gates on the evade flag alone. A hit taken while stunned still
    /// rolls, and a failure overwrites the stun. The sixth-sense roll is the one a stunned pilot
    /// does not take, because its trigger needs pursue or lay off.</summary>
    [Fact]
    public void AStunnedPilotStillRollsOnAHitAndIsReleasedByAnOverride()
    {
        var m = Machine();
        var target = Home + new Vector3(500f, 0f, 0f);
        PursueFrom(m, target);
        m.Stun(5f);
        m.SixthSenseChance = 0f;
        m.NotifyTargetEvaded();
        Assert.Equal(AiMode.Stunned, m.Mode);
        Assert.Equal(5f, m.StunRemainingS, 3); // no second timer piled on

        // The hit's own roll is not gated on the mode: a failure writes the reaction over it.
        m.NotifyDamage(0f, 10f, 0f, 10f);
        Assert.True(m.Evading);
        Assert.Equal(AiMode.Evade, m.Mode);
        Assert.Equal(0f, m.StunRemainingS);

        m.Enter(AiMode.Patrol, "scripted");
        Assert.Equal(AiMode.Patrol, m.Mode);
        Assert.Equal(0f, m.StunRemainingS);
    }

    [Fact]
    public void EvasiveManeuverPlaysAnEligibleEntryToDoneAndReturns()
    {
        var m = Machine();
        m.NaturalTouch = 2;
        m.Library = new[] { QuickManeuver("bank_turn", 2), QuickManeuver("split_s", 8), Stub("high_yo_yo") };
        var target = Home + new Vector3(500f, 0f, 0f);
        PursueFrom(m, target);
        string? reason = null;
        m.ModeChanged += (_, to, why) => { if (to == AiMode.EvasiveManeuver) reason = why; };
        m.NotifyDamage(0f, 8f, 0f, 8f);

        // Only the entry inside the natural-touch cull is playable; the stub never is.
        Assert.Equal(AiMode.EvasiveManeuver, m.Mode);
        Assert.NotNull(m.Executor);
        Assert.Equal("bank_turn", m.Executor!.Maneuver.Name);
        Assert.Contains("natural touch 2/2", reason);

        // Play the program out (the owner steps the executor; the machine watches Done).
        var model = new FlightModel(new PlaneStats());
        for (int i = 0; i < 20 && !m.Executor!.Done; i++)
            m.Executor.Next(model, 0.02f);
        Assert.True(m.Executor!.Done);
        Assert.Equal(AiMode.Pursue, m.Update(Home, Level, target, null, 1f / 60f));
        Assert.Null(m.Executor);
    }

    [Fact]
    public void AltitudeVetoCullsAProgramPredictedToEndBelowTheFloor()
    {
        // The shipped split_s ends one prediction step lower than it started. The veto turns on
        // the altitude it is flown at and on nothing else.
        foreach (float altitude in new[] { 60f, 600f })
        {
            var m = Machine();
            m.NaturalTouch = 9;
            m.Library = new[] { SplitS() };
            var pos = new Vector3(0f, altitude, 0f);
            m.Update(pos, Level, null, null, 1f / 60f, attitude: Basis.Identity);
            m.NotifyDamage(0f, 8f, 0f, 8f);

            if (altitude < ManeuverExecutor.PredictedStepM + AiModeMachine.AltitudeFloorM)
            {
                Assert.Equal(AiMode.Evade, m.Mode);
                Assert.Null(m.Executor);
            }
            else
            {
                Assert.Equal(AiMode.EvasiveManeuver, m.Mode);
                Assert.Equal("split_s", m.Executor!.Maneuver.Name);
            }
        }
    }

    [Fact]
    public void AltitudeVetoSweepsThePredictedPathAndStopsAtTheCeiling()
    {
        // A climb ends one prediction step higher, so the same program crosses the ceiling from
        // 7950 m and does not from 1000 m. Above it the obstacle sweep is not run at all.
        foreach (float altitude in new[] { 1000f, 7950f })
        {
            var m = Machine();
            m.NaturalTouch = 9;
            m.Library = new[] { Climb() };
            m.Update(new Vector3(0f, altitude, 0f), Level, null, null, 1f / 60f,
                attitude: Basis.Identity);
            int probes = 0;
            m.ProbeBlocked = (_, _) => { probes++; return "wall"; };
            m.NotifyDamage(0f, 8f, 0f, 8f);

            if (altitude > AiModeMachine.ProbeCeilingM - ManeuverExecutor.PredictedStepM)
            {
                Assert.Equal(AiMode.EvasiveManeuver, m.Mode);
                Assert.Equal(0, probes);
            }
            else
            {
                Assert.Equal(AiMode.Evade, m.Mode);
                Assert.Equal(1, probes);
            }
        }
    }

    [Fact]
    public void SignatureManeuversAreWeightedUpInTheDraw()
    {
        int signaturePicks = 0;
        const int runs = 200;
        for (int seed = 0; seed < runs; seed++)
        {
            var m = Machine(seed);
            m.NaturalTouch = 9;
            m.Library = new[] { QuickManeuver("loop", 7), QuickManeuver("split_s", 8) };
            m.SignatureManeuvers = new[] { "split_s" };
            PursueFrom(m, Home + new Vector3(500f, 0f, 0f));
            m.NotifyDamage(0f, 8f, 0f, 8f);
            if (m.Executor!.Maneuver.Name == "split_s")
                signaturePicks++;
        }

        // Weight 6 against 1: the expectation is 6/7 of the draws; well above an even split.
        Assert.InRange(signaturePicks, (int)(runs * 0.75), runs);
    }

    [Fact]
    public void AvoidCrashOverridesUntilTheProbeClears()
    {
        var m = Machine();
        bool blocked = false;
        m.ProbeBlocked = (_, _) => blocked ? "test/obstacle" : null;

        // Clear probes: patrol undisturbed.
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, Due));

        // Terrain inside the lookahead: the override takes the mode and orders a climb-out.
        blocked = true;
        Assert.Equal(AiMode.AvoidCrash, m.Update(Home, Level, null, null, Due));
        Assert.Equal(Home.Y + AiModeMachine.ClimbOutM, m.ClimbOutAltitude, 3);

        // Clear again: ONE clear ray releases it, in that same call. The original holds no
        // clear-streak (docs/org/aiPilot.md, "A clear ray releases the state in the same call").
        blocked = false;
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, Due));
    }

    [Fact]
    public void BelowTheFloorTheClimbOutArmsWithNoProbeAtAll()
    {
        var m = Machine();
        m.ProbeBlocked = (_, _) => null; // a clear line everywhere: only the floor can arm this

        var low = new Vector3(0f, AiModeMachine.AltitudeFloorM - 1f, 0f);
        Assert.Equal(AiMode.AvoidCrash, m.Update(low, Level, null, null, 0f));
        Assert.Equal(low.Y + AiModeMachine.ClimbOutM, m.ClimbOutAltitude, 3);

        // And back above it, the clear ray releases as usual.
        Assert.Equal(AiMode.Patrol, m.Update(Home, Level, null, null, Due));
    }

    [Fact]
    public void AboveTheCeilingNothingIsCastAndAClimbOutIsReleased()
    {
        var m = Machine();
        m.ProbeBlocked = (_, _) => "test/obstacle"; // blocked everywhere

        Assert.Equal(AiMode.AvoidCrash, m.Update(Home, Level, null, null, Due));

        // Above the ceiling the original runs no check and clears the state, blocked line or not.
        var high = new Vector3(0f, AiModeMachine.ProbeCeilingM + 1f, 0f);
        Assert.Equal(AiMode.Patrol, m.Update(high, Level, null, null, Due));
    }

    // ---- Lay off: the rubber-band assist ----------------------------------------------
    // Decoded: the mode and the sixth_sense_factor ease-off constant. Invented (named on the
    // machine's constants): the pursued-test cones, the enter/caught-up distances, the hold.

    [Fact]
    public void LayOffEntersWhenAChasingHumanFallsBehind()
    {
        var m = Machine();
        PursueFrom(m, Astern600);

        // One passing frame is NOT enough: the entry needs the geometry sustained (the
        // user-reported misfire regression, a turning fight satisfying the test momentarily).
        m.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, targetIsHuman: true);
        Assert.Equal(AiMode.Pursue, m.Mode);

        SustainPursuit(m, Astern600, Chasing);
        Assert.Equal(AiMode.LayOff, m.Mode);

        // The lay-off course is the entry velocity's heading at the entry altitude.
        Assert.Equal(AiPilot.HeadingDegOf(Level), m.LayOffHeadingDeg, 3);
        Assert.Equal(Home.Y, m.LayOffAltitude, 3);

        // Inside the enter distance nothing moves: the pursuer has not fallen behind.
        var m2 = Machine();
        var astern300 = Home + new Vector3(0f, 0f, 300f);
        PursueFrom(m2, astern300);
        SustainPursuit(m2, astern300, Chasing);
        Assert.Equal(AiMode.Pursue, m2.Mode);

        // An interrupted window restarts the clock: geometry, a break, geometry again.
        var m3 = Machine();
        PursueFrom(m3, Astern600);
        for (int i = 0; i < 60; i++)
            m3.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, targetIsHuman: true);
        m3.Update(Home, Level, Astern600, null, 1f / 60f, new Vector3(0f, 0f, 80f), targetIsHuman: true);
        for (int i = 0; i < 60; i++)
            m3.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, targetIsHuman: true);
        Assert.Equal(AiMode.Pursue, m3.Mode); // 1 s + 1 s with a break never reaches 1.5 s

        // The nose axis outranks the velocity: nose pointed AT the target (a turn toward it)
        // reads as not-pursued even while the velocity still points away.
        var m4 = Machine();
        PursueFrom(m4, Astern600);
        int frames = (int)(AiModeMachine.LayOffSustainS * 60f) + 2;
        for (int i = 0; i < frames; i++)
            m4.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, targetIsHuman: true,
                nose: new Vector3(0f, 0f, 1f)); // nose toward the target astern
        Assert.Equal(AiMode.Pursue, m4.Mode);
    }

    [Fact]
    public void LayOffNeverEntersWithoutTheAssistTheHumanOrTheChase()
    {
        // --no-assist's switch: same geometry, never entered.
        var m = Machine();
        m.AssistEnabled = false;
        PursueFrom(m, Astern600);
        SustainPursuit(m, Astern600, Chasing);
        Assert.Equal(AiMode.Pursue, m.Mode);

        // An AI pursuer gets no favours: the assist is for human players only.
        var m2 = Machine();
        PursueFrom(m2, Astern600);
        int frames = (int)(AiModeMachine.LayOffSustainS * 60f) + 2;
        for (int i = 0; i < frames; i++)
            m2.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, targetIsHuman: false);
        Assert.Equal(AiMode.Pursue, m2.Mode);

        // A target astern but flying AWAY is not pursuing.
        var m3 = Machine();
        PursueFrom(m3, Astern600);
        SustainPursuit(m3, Astern600, new Vector3(0f, 0f, 80f));
        Assert.Equal(AiMode.Pursue, m3.Mode);

        // A chasing target abeam is outside the rear cone.
        var m4 = Machine();
        var abeam = Home + new Vector3(600f, 0f, 0f);
        PursueFrom(m4, abeam);
        SustainPursuit(m4, abeam, new Vector3(-80f, 0f, 0f));
        Assert.Equal(AiMode.Pursue, m4.Mode);
    }

    [Fact]
    public void LayOffExitsWhenCaughtUpOrTheChaseEnds()
    {
        var m = Machine();
        PursueFrom(m, Astern600);
        SustainPursuit(m, Astern600, Chasing);
        Assert.Equal(AiMode.LayOff, m.Mode);

        // The anti-chatter hold: a caught-up gap inside it does not exit yet.
        var astern200 = Home + new Vector3(0f, 0f, 200f);
        Assert.Equal(AiMode.LayOff,
            m.Update(Home, Level, astern200, null, 1f / 60f, Chasing, targetIsHuman: true));

        // Hold out the dwell, then the pursuer catching up returns to pursue.
        for (int i = 0; i < (int)(AiModeMachine.LayOffMinHoldS * 60f) + 5; i++)
            m.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, targetIsHuman: true);
        Assert.Equal(AiMode.LayOff, m.Mode);
        Assert.Equal(AiMode.Pursue,
            m.Update(Home, Level, astern200, null, 1f / 60f, Chasing, targetIsHuman: true));

        // Re-enter, hold out, then the chase ending (velocity away) also returns to pursue.
        SustainPursuit(m, Astern600, Chasing);
        Assert.Equal(AiMode.LayOff, m.Mode);
        for (int i = 0; i < (int)(AiModeMachine.LayOffMinHoldS * 60f) + 5; i++)
            m.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, targetIsHuman: true);
        Assert.Equal(AiMode.Pursue,
            m.Update(Home, Level, Astern600, null, 1f / 60f, new Vector3(0f, 0f, 80f), targetIsHuman: true));
    }

    [Fact]
    public void SwitchingTheAssistOffReleasesARunningLayOff()
    {
        var m = Machine();
        PursueFrom(m, Astern600);
        SustainPursuit(m, Astern600, Chasing);
        Assert.Equal(AiMode.LayOff, m.Mode);
        m.AssistEnabled = false;
        Assert.Equal(AiMode.Pursue,
            m.Update(Home, Level, Astern600, null, 1f / 60f, Chasing, targetIsHuman: true));
    }

    /// <summary>The daredevil roll behind the proximity pick. A certainty passes and says so in
    /// the engine's own vocabulary. A zero chance never rolls at all, the class gate a non-jet
    /// spawn is given. A failed roll holds the next look five seconds.</summary>
    [Fact]
    public void TheDaredevilRollHoldsFiveSecondsAfterAFailedLook()
    {
        var m = Machine();
        var rolls = new List<string>();
        m.RollLogged += rolls.Add;

        m.DaredevilChance = 0f;
        Assert.False(m.RollDaredevil());
        Assert.Empty(rolls);

        m.DaredevilChance = 1f;
        Assert.True(m.RollDaredevil());
        Assert.Equal("Dare devil test passed. Looking for danger zones.", Assert.Single(rolls));

        // A look that started no run stamps the hold, and nothing rolls again inside it.
        m.StampDangerZoneRetry();
        m.Update(Home, Level, null, null, AiModeMachine.DangerZoneRetryS - 0.5f);
        Assert.False(m.RollDaredevil());
        Assert.Single(rolls);
        m.Update(Home, Level, null, null, 1f);
        Assert.True(m.RollDaredevil());

        // …and a failed roll arms the same hold, so the refusal costs one roll per interval.
        m.DaredevilChance = 0.0001f;
        m.Update(Home, Level, null, null, AiModeMachine.DangerZoneRetryS);
        Assert.False(m.RollDaredevil());
        Assert.Equal("Dare devil test failed. Not looking for danger zones.", rolls[^1]);
        int failed = rolls.Count;
        m.DaredevilChance = 1f;
        Assert.False(m.RollDaredevil());
        Assert.Equal(failed, rolls.Count);
    }

    [Fact]
    public void FixedSeedsTransitionIdentically()
    {
        AiModeMachine Build() // real (0..1) chances so the rolls are genuinely random draws
        {
            var m = new AiModeMachine(new Random(7))
            {
                SteadyHandExponent = AiModeMachine.ExponentFor(0.5f),
                SixthSenseChance = 0.5f,
                StunRecoveryIntervalS = 1f,
            };
            return m;
        }

        List<AiMode> Run(AiModeMachine m)
        {
            var target = Home + new Vector3(600f, 0f, 0f);
            var seen = new List<AiMode> { m.Mode };
            m.ModeChanged += (_, to, _) => seen.Add(to);
            m.Update(Home, Level, target, null, 1f / 60f);
            for (int i = 0; i < 600; i++)
            {
                if (i % 90 == 0)
                    m.NotifyDamage(0f, 6f, 0f, 40f);
                if (i % 240 == 120)
                    m.NotifyTargetEvaded();
                m.Update(Home, Level, target, null, 1f / 60f);
            }
            return seen;
        }

        Assert.Equal(Run(Build()), Run(Build()));
    }

    private static AiModeMachine Machine(int seed = 1) => new(new Random(seed));

    // Holds the pursued geometry through the sustain window plus one frame, the
    // entry now needs it CONTINUOUS, never one passing frame.
    private static void SustainPursuit(AiModeMachine m, Vector3 target, Vector3 chase)
    {
        int frames = (int)(AiModeMachine.LayOffSustainS * 60f) + 2;
        for (int i = 0; i < frames; i++)
            m.Update(Home, Level, target, null, 1f / 60f, chase, targetIsHuman: true);
    }

    // A one-step maneuver the executor finishes in a fraction of a second.
    private static Maneuver QuickManeuver(string name, int difficulty, float duration = 0.05f) =>
        new()
        {
            Name = name,
            Difficulty = difficulty,
            Steps = new[] { new ManeuverStep(duration, 10f, 0f, 0f, Array.Empty<float>()) },
        };

    // The library's one nitro-flagged shape: difficulty 0, so only the injector cull keeps it out.
    private static Maneuver NitroManeuver(string name) => new()
    {
        Name = name,
        Difficulty = 0,
        Nitro = true,
        Steps = new[] { new ManeuverStep(0.05f, 0f, 0f, 0f, Array.Empty<float>()) },
    };

    // The shipped split_s, verbatim from maneuvers.zrd: level, nose straight down, then two
    // half-rolled reversals. Its predicted path ends one step below where it began.
    private static Maneuver SplitS() => new()
    {
        Name = "split_s",
        Difficulty = 8,
        Steps = new[]
        {
            new ManeuverStep(0f, 0f, 0f, 0f, Array.Empty<float>()),
            new ManeuverStep(0f, -90f, 0f, 0f, Array.Empty<float>()),
            new ManeuverStep(0f, 0f, 180f, 180f, Array.Empty<float>()),
            new ManeuverStep(0f, 0f, 180f, 0f, Array.Empty<float>()),
        },
    };

    // The shipped climb: one step at 60 degrees nose up, held four seconds.
    private static Maneuver Climb() => new()
    {
        Name = "climb",
        Difficulty = 3,
        Bias = -0.5f,
        Steps = new[] { new ManeuverStep(4f, 60f, 0f, 0f, Array.Empty<float>()) },
    };

    private static Maneuver Stub(string name) => new()
    {
        Name = name,
        Difficulty = 99,
        Steps = Array.Empty<ManeuverStep>(),
    };

    // Holds position and quarry for the given seconds in 0.1 s ticks, returning the mode reached.
    private static AiMode Step(AiModeMachine m, Vector3 target, float seconds)
    {
        var mode = m.Mode;
        for (float t = 0f; t < seconds - 1e-4f; t += 0.1f)
            mode = m.Update(Home, Level, target, null, 0.1f);
        return mode;
    }

    private static AiMode PursueFrom(AiModeMachine m, Vector3 target)
    {
        var mode = m.Update(Home, Level, target, null, 1f / 60f);
        Assert.Equal(AiMode.Pursue, mode);
        return mode;
    }
}
