using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="AircraftLifecycle"/>'s transition table asserted with no <c>Node</c> and no world:
/// bare construction, only <c>CrashDefs</c> and <c>AutoRespawnAfter</c> set.
/// ⚠ CONTEXT.md fixes Rerun and Respawn as two different mechanisms. This module has only
/// <see cref="AircraftLifecycle.Respawn"/>, the one that returns a plane mid-run; Rerun belongs to
/// the node and never reaches here, so nothing below is named after it.
/// </summary>
public class AircraftLifecycleTests
{
    private const string CrashPrefix = "crash_";

    // ---- Destroy: in play -> destroyed ---------------------------------------------------------

    [Fact]
    public void DestroyWithADestroyDefArmsTheFallingWreck()
    {
        var life = new AircraftLifecycle();

        var outcome = life.Destroy("wreck_fall", killer: 3);

        Assert.True(outcome.Occurred);
        Assert.Equal("wreck_fall", outcome.DestroyDef);
        Assert.True(outcome.WreckFalling);
        Assert.True(life.WreckFalling);
        Assert.True(life.Crashed);
        Assert.True(life.Destroyed);
        Assert.True(outcome.EndFlightSystems);
        Assert.True(outcome.CutCamera);
        Assert.True(outcome.Downed);
        Assert.Equal(3, outcome.Killer);
    }

    [Fact]
    public void DestroyWithNoDestroyDefHidesTheHullInstead()
    {
        var life = new AircraftLifecycle();

        var outcome = life.Destroy(null, killer: null);

        Assert.True(outcome.Occurred);
        Assert.Null(outcome.DestroyDef);
        Assert.False(outcome.WreckFalling);
        Assert.False(life.WreckFalling);
        Assert.True(life.Crashed);
    }

    [Fact]
    public void DestroyArmsTheRespawnTimerToAutoRespawnAfterWhenSet()
    {
        var life = new AircraftLifecycle { AutoRespawnAfter = 4f };

        life.Destroy("wreck_fall", null);

        Assert.False(life.TickAutoRespawn(3.9f, scriptedRun: false));
        Assert.True(life.TickAutoRespawn(0.2f, scriptedRun: false));
    }

    [Fact]
    public void AutoRespawnOffWaitsForFireGunsOnceTheCrashSequenceHasRun()
    {
        var life = new AircraftLifecycle { AutoRespawnAfter = 3f, RespawnOnFire = true };
        int reads = 0;
        bool held = false;
        bool Fire()
        {
            reads++;
            return held;
        }

        life.Destroy("wreck_fall", null);
        held = true;
        Assert.False(life.TickAutoRespawn(2.9f, scriptedRun: false, Fire));
        Assert.Equal(0, reads);
        held = false;
        Assert.False(life.TickAutoRespawn(0.2f, scriptedRun: false, Fire));
        Assert.False(life.TickAutoRespawn(5f, scriptedRun: false, Fire));
        held = true;
        Assert.True(life.TickAutoRespawn(0.016f, scriptedRun: false, Fire));

        // ABLE-TO-FAIL CONTROL: Auto Respawn on, the same run-down timer is due with no press.
        var auto = new AircraftLifecycle { AutoRespawnAfter = 3f };
        auto.Destroy("wreck_fall", null);
        Assert.True(auto.TickAutoRespawn(3.1f, scriptedRun: false, () => false));
    }

    [Fact]
    public void DestroyFallsBackToTheFixedDelayWithNoAutoRespawnAfter()
    {
        var life = new AircraftLifecycle();

        life.Destroy("wreck_fall", null);

        // AutoRespawnDelay is 1.5s; a scripted run counts it down with no session timer armed.
        Assert.False(life.TickAutoRespawn(1.4f, scriptedRun: true));
        Assert.True(life.TickAutoRespawn(0.2f, scriptedRun: true));
    }

    // ---- Crash: in play -> crashed --------------------------------------------------------------

    [Fact]
    public void CrashSelectsTheSurfacesDefAndEndsFlightSystems()
    {
        var life = new AircraftLifecycle { CrashDefs = CrashTable(3) };

        var outcome = life.Crash(3, killer: 7);

        Assert.True(outcome.Occurred);
        Assert.False(outcome.WreckLanding);
        Assert.Equal(CrashPrefix + SurfaceRegistry.Names[3], outcome.CrashDef);
        Assert.Equal(outcome.CrashDef, life.LastCrashDef);
        Assert.True(outcome.EndFlightSystems);
        Assert.True(outcome.CutCamera);
        Assert.True(outcome.Downed);
        Assert.Equal(7, outcome.Killer);
        Assert.True(life.Crashed);
    }

    [Fact]
    public void CrashWithNoStruckMaterialResolvesSlotZero()
    {
        var life = new AircraftLifecycle { CrashDefs = CrashTable(0) };

        var outcome = life.Crash(null, null);

        Assert.Equal(CrashPrefix + SurfaceRegistry.Names[0], outcome.CrashDef);
    }

    [Fact]
    public void CrashArmsTheRespawnTimerLikeDestroyDoes()
    {
        var life = new AircraftLifecycle { AutoRespawnAfter = 2f };

        life.Crash(0, null);

        Assert.False(life.TickAutoRespawn(1.9f, scriptedRun: false));
        Assert.True(life.TickAutoRespawn(0.2f, scriptedRun: false));
    }

    // ---- The falling wreck's own landing: the one allowed re-entry ------------------------------

    [Fact]
    public void TheFallingWrecksLandingReportsWreckLandingWithNoSecondShutdown()
    {
        var life = new AircraftLifecycle { CrashDefs = CrashTable(5), AutoRespawnAfter = 9f };
        life.Destroy("wreck_fall", killer: 2); // arms the 9s timer and starts the fall

        var outcome = life.Crash(5, killer: 2);

        Assert.True(outcome.Occurred);
        Assert.True(outcome.WreckLanding);
        Assert.Equal(CrashPrefix + SurfaceRegistry.Names[5], outcome.CrashDef);
        Assert.Equal(outcome.CrashDef, life.LastCrashDef);
        Assert.False(outcome.EndFlightSystems);
        Assert.False(outcome.CutCamera);
        Assert.False(outcome.Downed);
        Assert.False(life.WreckFalling);
        // The kill's own timer is untouched: still due at 9s, not re-armed to it again.
        Assert.False(life.TickAutoRespawn(8.9f, scriptedRun: true));
        Assert.True(life.TickAutoRespawn(0.2f, scriptedRun: true));
    }

    // ---- Guard: a crashed aircraft cannot crash again -------------------------------------------

    [Fact]
    public void ACrashedAircraftRefusesASecondCrash()
    {
        var life = new AircraftLifecycle { CrashDefs = CrashTable(4) };
        life.Crash(4, killer: 1);

        var outcome = life.Crash(9, killer: 1);

        Assert.False(outcome.Occurred);
        // No state changed, including the def: it stays the first crash's, not the refused one's.
        Assert.Equal(CrashPrefix + SurfaceRegistry.Names[4], life.LastCrashDef);
    }

    [Fact]
    public void ACrashedAircraftRefusesDestroyOutright()
    {
        var life = new AircraftLifecycle { CrashDefs = CrashTable(4) };
        life.Crash(4, killer: 1);

        var outcome = life.Destroy("wreck_fall", killer: 5);

        Assert.False(outcome.Occurred);
        Assert.False(life.WreckFalling);
    }

    // ---- Respawn: crashed -> in play, timers untouched ------------------------------------------

    [Fact]
    public void RespawnClearsTheThreeOutOfPlayFlagsTogether()
    {
        var life = new AircraftLifecycle();
        life.Destroy("wreck_fall", null);

        life.Respawn();

        Assert.False(life.Crashed);
        Assert.False(life.Destroyed);
        Assert.False(life.WreckFalling);
        Assert.True(life.InPlay);
    }

    [Fact]
    public void RespawnDoesNotReArmTheSpawnTimers()
    {
        var life = new AircraftLifecycle();
        life.ArmSpawnTimers(carrierDrop: false);
        life.Crash(null, null);

        life.Respawn();

        // Only a spawn point calls ArmSpawnTimers; Respawn must leave the grace exactly where it
        // was, neither re-armed nor cleared.
        Assert.True(life.CollisionGraceActive);
        life.TickTimers(CollisionDamage.SpawnGrace + 0.1f);
        Assert.False(life.CollisionGraceActive);
    }

    // ---- StopWreckFall ---------------------------------------------------------------------------

    [Fact]
    public void StopWreckFallEndsTheFallWithoutRespawning()
    {
        var life = new AircraftLifecycle();
        life.Destroy("wreck_fall", null);

        life.StopWreckFall();

        Assert.False(life.WreckFalling);
        Assert.True(life.Crashed); // still the destroyed hull, just no longer flying itself down
    }

    // ---- Inert, on and off, asserted on both axes -----------------------------------------------

    [Fact]
    public void SetInertTrueMovesAndReportsInPlayFalse()
    {
        var life = new AircraftLifecycle();

        bool moved = life.SetInert(true);

        Assert.True(moved);
        Assert.True(life.Inert);
        Assert.False(life.InPlay);
    }

    [Fact]
    public void SetInertTrueTwiceOnlyMovesOnce()
    {
        var life = new AircraftLifecycle();
        life.SetInert(true);

        bool moved = life.SetInert(true);

        Assert.False(moved);
        Assert.True(life.Inert);
    }

    [Fact]
    public void SetInertFalseMovesBackAndInPlayFollows()
    {
        var life = new AircraftLifecycle();
        life.SetInert(true);

        bool moved = life.SetInert(false);

        Assert.True(moved);
        Assert.False(life.Inert);
        Assert.True(life.InPlay);
    }

    [Fact]
    public void InPlayIsFalseWhenCrashedEvenIfNotInert()
    {
        var life = new AircraftLifecycle();
        life.Destroy("wreck_fall", null);

        Assert.False(life.Inert);
        Assert.False(life.InPlay);
    }

    // ---- Parked: inert under a cutscene, still in the mission ----------------------------------

    [Fact]
    public void InertWithoutAParkIsDeactivated()
    {
        var life = new AircraftLifecycle();
        life.SetInert(true);

        Assert.True(life.Deactivated);
        Assert.False(life.Parked);
    }

    [Fact]
    public void ParkedInertIsOutOfPlayButNotDeactivated()
    {
        var life = new AircraftLifecycle();
        life.SetParked(true);
        life.SetInert(true);

        Assert.True(life.Inert);
        Assert.False(life.InPlay);
        Assert.True(life.Parked);
        Assert.False(life.Deactivated);
    }

    [Fact]
    public void ClearingTheParkWhileStillInertDeactivates()
    {
        var life = new AircraftLifecycle();
        life.SetParked(true);
        life.SetInert(true);

        life.SetParked(false);

        Assert.True(life.Inert);
        Assert.True(life.Deactivated);
    }

    [Fact]
    public void ALiveAircraftIsNeverDeactivated()
    {
        var life = new AircraftLifecycle();

        Assert.False(life.Deactivated);
        Assert.True(life.InPlay);
    }

    // ---- Spawn-timer arm, including the carrier-drop variant ------------------------------------

    [Fact]
    public void ArmSpawnTimersOpensOnlyTheCollisionGraceWithoutACarrierDrop()
    {
        var life = new AircraftLifecycle();

        life.ArmSpawnTimers(carrierDrop: false);

        Assert.True(life.CollisionGraceActive);
        Assert.False(life.PostDropGroundBlowActive);
        life.TickTimers(CollisionDamage.SpawnGrace - 0.1f);
        Assert.True(life.CollisionGraceActive);
        life.TickTimers(0.2f);
        Assert.False(life.CollisionGraceActive);
    }

    [Fact]
    public void ArmSpawnTimersCarrierDropAlsoOpensTheGroundBlowWindow()
    {
        var life = new AircraftLifecycle();

        life.ArmSpawnTimers(carrierDrop: true);

        Assert.True(life.PostDropGroundBlowActive);
        life.TickTimers(AircraftLifecycle.CarrierDropGroundBlow - 0.1f);
        Assert.True(life.PostDropGroundBlowActive);
        life.TickTimers(0.2f);
        Assert.False(life.PostDropGroundBlowActive);
    }

    [Fact]
    public void ArmCollisionGraceOpensTheShorterRamWindow()
    {
        var life = new AircraftLifecycle();

        life.ArmCollisionGrace();

        Assert.True(life.CollisionGraceActive);
        life.TickTimers(CollisionDamage.EntityGrace - 0.1f);
        Assert.True(life.CollisionGraceActive);
        life.TickTimers(0.2f);
        Assert.False(life.CollisionGraceActive);
    }

    [Fact]
    public void TickTimersKeepsDrainingPastDueRatherThanErroring()
    {
        var life = new AircraftLifecycle();
        life.ArmCollisionGrace();

        life.TickTimers(CollisionDamage.EntityGrace + 50f); // wildly past due

        Assert.False(life.CollisionGraceActive);
        life.TickTimers(0.01f);
        Assert.False(life.CollisionGraceActive);
    }

    // ---- TickAutoRespawn: counts down only with a scripted run or a session timer --------------

    [Fact]
    public void TickAutoRespawnNeverCountsDownWithNeitherArm()
    {
        var life = new AircraftLifecycle();
        life.Destroy("wreck_fall", null); // arms the internal timer, but AutoRespawnAfter stays null

        for (int i = 0; i < 100; i++)
            Assert.False(life.TickAutoRespawn(1000f, scriptedRun: false));
    }

    [Fact]
    public void TickAutoRespawnCountsDownOnAScriptedRunAloneWithNoSessionTimer()
    {
        var life = new AircraftLifecycle();
        life.Destroy("wreck_fall", null); // AutoRespawnDelay fallback, 1.5s

        Assert.False(life.TickAutoRespawn(1.4f, scriptedRun: true));
        Assert.True(life.TickAutoRespawn(0.2f, scriptedRun: true));
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>A crash table that plays exactly the listed registry slots, so a test can pin
    /// <see cref="SurfaceDefTable.DefForSurfaceId"/> to one exact name without depending on which
    /// surfaces the current install ships.</summary>
    private static SurfaceDefTable CrashTable(params int[] playableIds)
    {
        var defs = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (int id in playableIds)
            defs.Add(CrashPrefix + SurfaceRegistry.Names[id]);
        return new SurfaceDefTable(CrashPrefix, lastResort: "crash_default", defs.Contains);
    }
}
