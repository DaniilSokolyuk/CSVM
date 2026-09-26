using System;
using System.Collections.Generic;
using CSVM.Session.World;
using Xunit;

namespace CSVM.Tests;

public sealed class SessionSimulationTests
{
    [Fact]
    public void OneRequestAdvancesEveryPhaseOnceInCanonicalOrder()
    {
        var runtime = new RecordingRuntime("ai-1", "ai-2");
        var simulation = new SessionSimulation(runtime);

        simulation.Step(0.25f);

        Assert.Equal(new[]
        {
            "capture-ai", "incoming-fire:0.25", "projectiles:0.25", "human-aircraft:0.25",
            "zeppelins:0.25", "turret-emplacements:0.25", "generators:0.25",
            "surface-vehicles:0.25",
            "ai-1:0.25", "ai-2:0.25", "landing-approaches", "instant-action:0.25",
            "campaign:0.25", "radio:0.25", "smoke-screens:0.25", "beeper-tags:0.25",
            "ai-voice:0.25", "versus:0.25",
        }, runtime.Calls);
    }

    [Fact]
    public void HeldSessionAdvancesNothing()
    {
        var runtime = new RecordingRuntime("ai-1") { SimHeld = true };

        new SessionSimulation(runtime).Step(0.25f);

        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public void GeneratorSpawnBecomesEligibleOnTheNextStep()
    {
        var runtime = new RecordingRuntime("existing") { SpawnFromGenerator = "spawned" };
        var simulation = new SessionSimulation(runtime);

        simulation.Step(0.25f);
        simulation.Step(0.25f);

        Assert.Equal(2, runtime.Calls.FindAll(call => call == "existing:0.25").Count);
        Assert.DoesNotContain("spawned:0.25", runtime.Calls.GetRange(0, runtime.Calls.IndexOf("versus:0.25") + 1));
        Assert.Equal(1, runtime.Calls.FindAll(call => call == "spawned:0.25").Count);
    }

    [Fact]
    public void OneRequestDoesNotDoubleStepAConcretePhase()
    {
        var runtime = new RecordingRuntime("ai-1");

        new SessionSimulation(runtime).Step(0.25f);

        Assert.Single(runtime.Calls, call => call == "projectiles:0.25");
    }

    [Fact]
    public void PhaseFailureStopsTheRemainderOfTheStepAndIsCaught()
    {
        var runtime = new RecordingRuntime("ai-1") { ThrowAt = "generators" };
        var simulation = new SessionSimulation(runtime);

        // Does not escape: Godot already swallowed it at the callback boundary, so letting it out
        // bought nothing and cost the only record. Caught here, it reaches our own log by phase.
        simulation.Step(0.25f);

        Assert.Equal("generators:0.25", runtime.Calls[^1]);
        Assert.DoesNotContain("ai-1:0.25", runtime.Calls);
        Assert.Equal(1, simulation.PhaseFailures);
    }

    [Fact]
    public void APhaseThatThrowsEveryStepIsCountedEveryTime()
    {
        var runtime = new RecordingRuntime("ai-1") { ThrowAt = "generators" };
        var simulation = new SessionSimulation(runtime);

        simulation.Step(0.25f);
        simulation.Step(0.25f);
        simulation.Step(0.25f);

        Assert.Equal(3, simulation.PhaseFailures);
    }

    [Fact]
    public void APhaseFailureDoesNotStopThePhasesBeforeItOnLaterSteps()
    {
        var runtime = new RecordingRuntime("ai-1") { ThrowAt = "generators" };
        var simulation = new SessionSimulation(runtime);

        simulation.Step(0.25f);
        simulation.Step(0.25f);

        // The player kept flying while the AI stood still: this is the shipped symptom, and it is
        // the ordering that produces it, not a dead session.
        Assert.Equal(2, runtime.Calls.FindAll(call => call == "human-aircraft:0.25").Count);
        Assert.DoesNotContain("ai-1:0.25", runtime.Calls);
    }

    [Fact]
    public void LandingCutsceneHoldHaltsMissionAndLaterPhases()
    {
        var runtime = new RecordingRuntime("ai-1") { HoldAtLanding = true };

        new SessionSimulation(runtime).Step(0.25f);

        Assert.Equal("landing-approaches", runtime.Calls[^1]);
        Assert.DoesNotContain("instant-action:0.25", runtime.Calls);
    }

    [Fact]
    public void CampaignCutsceneHoldHaltsLaterPhases()
    {
        var runtime = new RecordingRuntime("ai-1") { HoldAtCampaign = true };

        new SessionSimulation(runtime).Step(0.25f);

        Assert.Equal("campaign:0.25", runtime.Calls[^1]);
        Assert.DoesNotContain("radio:0.25", runtime.Calls);
    }

    [Fact]
    public void CampaignEndingHaltsLaterPhasesAndLaterRequestsAdvanceOnlyTheLeavingHold()
    {
        var runtime = new RecordingRuntime("ai-1") { EndAtCampaign = true };
        var simulation = new SessionSimulation(runtime);

        simulation.Step(0.25f);
        Assert.Equal("campaign:0.25", runtime.Calls[^1]);
        int firstStepCalls = runtime.Calls.Count;

        simulation.Step(0.25f);
        Assert.Equal(new[] { "leaving-hold:0.25" }, runtime.Calls.GetRange(
            firstStepCalls, runtime.Calls.Count - firstStepCalls));
    }

    private sealed class RecordingRuntime(params string[] aiAircraft) : ISessionSimulationRuntime
    {
        private readonly List<string> _aiAircraft = new(aiAircraft);
        private string[] _eligibleAiAircraft = [];

        public List<string> Calls { get; } = [];
        public bool SimHeld { get; set; }
        public string? SpawnFromGenerator { get; init; }
        public string? ThrowAt { get; init; }
        public bool HoldAtLanding { get; init; }
        public bool HoldAtCampaign { get; init; }
        public bool EndAtCampaign { get; init; }
        public bool EndingHold { get; private set; }

        public void CaptureAiAircraft()
        {
            Calls.Add("capture-ai");
            _eligibleAiAircraft = _aiAircraft.ToArray();
        }

        public void StepIncomingFire(float dt) => Record("incoming-fire", dt);
        public void StepProjectiles(float dt) => Record("projectiles", dt);
        public void StepHumanAircraft(float dt) => Record("human-aircraft", dt);
        public void StepZeppelins(float dt) => Record("zeppelins", dt);
        public void StepTurretEmplacements(float dt) => Record("turret-emplacements", dt);
        public void StepGenerators(float dt)
        {
            Record("generators", dt);
            if (SpawnFromGenerator is { } spawned && !_aiAircraft.Contains(spawned))
                _aiAircraft.Add(spawned);
        }

        public void StepSurfaceVehicles(float dt) => Record("surface-vehicles", dt);

        public void StepCapturedAiAircraft(float dt)
        {
            foreach (string aircraft in _eligibleAiAircraft)
                Record(aircraft, dt);
        }

        public void StepLandingApproaches()
        {
            Calls.Add("landing-approaches");
            if (HoldAtLanding)
                SimHeld = true;
        }

        public void StepInstantAction(float dt) => Record("instant-action", dt);
        public void StepCampaign(float dt)
        {
            Record("campaign", dt);
            if (HoldAtCampaign)
                SimHeld = true;
            if (EndAtCampaign)
                EndingHold = true;
        }
        public void StepEndingHold(float dt) => Record("leaving-hold", dt);
        public void StepRadio(float dt) => Record("radio", dt);
        public void StepSmokeScreens(float dt) => Record("smoke-screens", dt);
        public void StepBeeperTags(float dt) => Record("beeper-tags", dt);
        public void StepAiVoice(float dt) => Record("ai-voice", dt);
        public void StepVersus(float dt) => Record("versus", dt);

        private void Record(string phase, float dt)
        {
            Calls.Add($"{phase}:{dt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
            if (ThrowAt == phase)
                throw new InvalidOperationException(phase);
        }
    }
}
