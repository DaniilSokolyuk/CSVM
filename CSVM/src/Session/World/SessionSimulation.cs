using System;
using System.Collections.Generic;
using CSVM.Utils;

namespace CSVM.Session.World;

/// <summary>The named runtime roles advanced by <see cref="SessionSimulation"/>.</summary>
public interface ISessionSimulationRuntime
{
    bool SimHeld { get; }
    bool EndingHold { get; }

    void StepEndingHold(float dt);
    void CaptureAiAircraft();
    void StepIncomingFire(float dt);
    void StepProjectiles(float dt);
    void StepHumanAircraft(float dt);
    void StepZeppelins(float dt);
    void StepTurretEmplacements(float dt);
    void StepGenerators(float dt);
    void StepSurfaceVehicles(float dt);
    void StepCapturedAiAircraft(float dt);
    void StepLandingApproaches();
    void StepInstantAction(float dt);
    void StepCampaign(float dt);
    void StepRadio(float dt);
    void StepSmokeScreens(float dt);
    void StepBeeperTags(float dt);
    void StepAiVoice(float dt);
    void StepVersus(float dt);
}

/// <summary>
/// Advances one haltable session-simulation step in the canonical dependency order. The runtime
/// retains every subsystem and its state; this module owns only admission, ordering and the hold
/// gate. Authored animation and presentation do not enter this boundary.
/// </summary>
public sealed class SessionSimulation
{
    private readonly ISessionSimulationRuntime _runtime;

    // Signature -> times seen, so a throw that fires on every trigger pull reports once and then
    // periodically instead of writing a stack trace per frame.
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);

    private string _phase = "none";

    public SessionSimulation(ISessionSimulationRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <summary>Total phase failures caught since this instance was built, a test asserts on it,
    /// since a swallowed throw is otherwise invisible to the caller.</summary>
    public int PhaseFailures { get; private set; }

    /// <summary>Advances exactly one requested step, or no-ops while the session is held.
    /// A phase that throws is reported and ENDS THE STEP, which is what an escaping exception
    /// already did (Godot catches it at the callback boundary); the difference is that it now
    /// lands in our own log naming the phase, instead of only in the engine's.</summary>
    public void Step(float dt)
    {
        try
        {
            StepPhases(dt);
        }
        catch (Exception e)
        {
            ReportPhaseFailure(e);
        }
        finally
        {
            // The phase in progress banks whether the step ended or threw, so a throwing phase
            // still shows its cost in the --perf split rather than being carried into the next one.
            SimPhaseCost.Leave();
        }
    }

    // Every phase enters itself first: the name is what makes the failure report actionable, and
    // the same call opens the phase's --perf slot (SimPhaseCost), closing the one before it.
    private void Enter(SimPhase phase)
    {
        _phase = SimPhaseCost.Label(phase);
        SimPhaseCost.Enter(phase);
    }

    private void StepPhases(float dt)
    {
        if (_runtime.EndingHold)
        {
            Enter(SimPhase.EndingHold);
            _runtime.StepEndingHold(dt);
            return;
        }
        if (_runtime.SimHeld)
            return;

        // Membership is fixed at step entry. A generator may append aircraft below, but those
        // aircraft are not eligible until the next step (including the next catch-up substep).
        Enter(SimPhase.CaptureAiAircraft);
        _runtime.CaptureAiAircraft();

        Enter(SimPhase.IncomingFire);
        _runtime.StepIncomingFire(dt);
        Enter(SimPhase.Projectiles);
        _runtime.StepProjectiles(dt);
        Enter(SimPhase.HumanAircraft);
        _runtime.StepHumanAircraft(dt);
        Enter(SimPhase.Zeppelins);
        _runtime.StepZeppelins(dt);
        Enter(SimPhase.TurretEmplacements);
        _runtime.StepTurretEmplacements(dt);
        Enter(SimPhase.Generators);
        _runtime.StepGenerators(dt);
        // A hull a generator launched this step is not eligible until the next one, the same
        // admission the captured-aircraft membership above takes.
        Enter(SimPhase.SurfaceVehicles);
        _runtime.StepSurfaceVehicles(dt);
        Enter(SimPhase.CapturedAiAircraft);
        _runtime.StepCapturedAiAircraft(dt);
        Enter(SimPhase.LandingApproaches);
        _runtime.StepLandingApproaches();
        if (_runtime.SimHeld)
            return;

        Enter(SimPhase.InstantAction);
        _runtime.StepInstantAction(dt);
        Enter(SimPhase.Campaign);
        _runtime.StepCampaign(dt);
        if (_runtime.EndingHold || _runtime.SimHeld)
            return;

        Enter(SimPhase.Radio);
        _runtime.StepRadio(dt);
        Enter(SimPhase.SmokeScreens);
        _runtime.StepSmokeScreens(dt);
        Enter(SimPhase.BeeperTags);
        _runtime.StepBeeperTags(dt);
        Enter(SimPhase.AiVoice);
        _runtime.StepAiVoice(dt);
        Enter(SimPhase.Versus);
        _runtime.StepVersus(dt);
    }

    // First occurrence in full (stack trace included, which is what names the throwing call), then
    // every 256th: the throws this exists for fire on a held trigger, 60 times a second.
    private void ReportPhaseFailure(Exception e)
    {
        PhaseFailures++;
        string signature = $"{_phase}|{e.GetType().Name}|{e.Message}";
        _failures.TryGetValue(signature, out int seen);
        _failures[signature] = ++seen;
        if (seen != 1 && seen % 256 != 0)
        {
            return;
        }
        Log.Error(
            "core",
            $"session step phase={_phase} threw, the rest of this step did not run (seen={seen})",
            e);
    }
}
