using System.Collections.Generic;
using CSVM.Flight.Weapons;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="FireControl"/>, the firing half extracted from <see cref="Flight.Airframe.FlightController"/>: trigger
/// edges, per-group fire-rate accumulators, muzzle rotation, ammo draw-down, the two weapon
/// selectors with their on-empty auto-advance, the rocket launch gate and both dry-clip cues.
/// Engine-free: fakes for <see cref="IGunSlot"/>/<see cref="IPylonSlot"/>, tick-by-tick
/// <see cref="FireControl.Step"/>, decisions read off the reused <see cref="FireOutcome"/>.
/// The slot-selection cases prove <see cref="Flight.Hud.WeaponCursor"/>'s facts through the calling interface.
/// ⚠ Where a case depends on exact fire-rate/ammo/tick arithmetic, its rate is chosen so every
/// accumulator threshold falls on an exact multiple of <c>dt</c>, so the comparisons are between
/// bit-identical floats rather than results that depend on rounding.
/// </summary>
public class FireControlTests
{
    private const float Dt = 1f / 60f;

    // ==== Guns =================================================================================

    [Fact]
    public void TheFirstShotLeavesTheBarrelTheInstantTheTriggerGoesDown()
    {
        // A low fire rate (interval way bigger than dt) so the press-edge boost can only ever
        // produce one shot this tick, not a burst, isolates the "instant on press" behaviour from
        // the rate mechanics covered separately below.
        var gun = Gun(fireRate: 1f, ammo: 1000, capacity: 1000);
        var fc = Make(new List<FakeGun> { gun }, new List<FakePylon>());

        Step(fc, fire: false); // idle tick, establishes the "was not held" edge
        var o = Step(fc, fire: true); // the press

        Assert.Single(o.GunShots);
    }

    /// <summary>Holding fire at a 10/s rate front-loads one press-edge shot on top of the steady
    /// 10 Hz cadence, pinned to 11 total. The window is 61 ticks, not 60, because the press-tick
    /// subtraction loses a float epsilon that can push the interval-crossing tick either
    /// way.</summary>
    [Fact]
    public void HoldingFireJustOverASecondFiresThePressShotPlusTheStatedRate()
    {
        var gun = Gun(fireRate: 10f, ammo: 1000, capacity: 1000);
        var fc = Make(new List<FakeGun> { gun }, new List<FakePylon>());

        int shots = 0;
        for (int i = 0; i < 61; i++)
        {
            shots += Step(fc, fire: true).GunShots.Count;
        }

        Assert.Equal(11, shots);
    }

    [Fact]
    public void MuzzlesAlternateWhileTheGroupStaysTheSelectedIndex()
    {
        var gun = Gun(fireRate: 10f, ammo: 1000, capacity: 1000, muzzleCount: 2);
        var fc = Make(new List<FakeGun> { gun }, new List<FakePylon>());

        var muzzles = new List<int>();
        var groups = new List<int>();
        for (int i = 0; i < 30 && muzzles.Count < 4; i++)
        {
            foreach (var (group, muzzle) in Step(fc, fire: true).GunShots)
            {
                groups.Add(group);
                muzzles.Add(muzzle);
            }
        }

        Assert.Equal(new[] { 0, 1, 0, 1 }, muzzles.GetRange(0, 4));
        Assert.All(groups, g => Assert.Equal(0, g));
    }

    [Fact]
    public void FiringDrawsDownAmmoUntilTheClipIsEmpty()
    {
        var gun = Gun(fireRate: 10f, ammo: 3, capacity: 3);
        var fc = Make(new List<FakeGun> { gun }, new List<FakePylon>());

        int shots = 0;
        for (int i = 0; i < 60; i++)
        {
            shots += Step(fc, fire: true).GunShots.Count;
        }

        Assert.Equal(3, shots);
        Assert.Equal(0, gun.Ammo);
    }

    /// <summary>The selected group runs dry mid-burst and the selector hands off to the next armed
    /// group without a gap in the firing loop's sound. Group 0's FireRate=60 makes interval == dt
    /// exactly, so its drain tick is exact-multiple arithmetic; group 1's much lower rate keeps its
    /// cold accumulator short of its own threshold on the hand-off tick.</summary>
    [Fact]
    public void MidBurstGroupHandOffKeepsTheLoopSoundContinuous()
    {
        var g0 = Gun(fireRate: 60f, ammo: 3, capacity: 3, loopSound: "loop0", id: "wep_g0");
        var g1 = Gun(fireRate: 10f, ammo: 5, capacity: 5, loopSound: "loop1", id: "wep_g1");
        var fc = Make(new List<FakeGun> { g0, g1 }, new List<FakePylon>());

        // Tick 1 (press edge): the boost inflates group 0's accumulator by a full interval on top of
        // dt, and because interval==dt here that is exactly 2 intervals' worth -> 2 shots this tick.
        var o1 = Step(fc, fire: true);
        Assert.Equal(2, o1.GunShots.Count);
        Assert.Equal(0, fc.GunSel);
        Assert.True(o1.GunLoopWanted);
        Assert.Equal("loop0", o1.GunLoopSound);

        // Tick 2 (steady, no boost): exactly 1 more interval accumulates -> the 3rd and last round.
        // Group 0 still reports ammo>0 going INTO this tick (the pre-shot wantLoop check runs before
        // the draw-down), so the loop is still "group 0's".
        var o2 = Step(fc, fire: true);
        Assert.Equal(1, o2.GunShots.Count);
        Assert.Equal(0, fc.GunSel);
        Assert.True(o2.GunLoopWanted);
        Assert.Equal("loop0", o2.GunLoopSound);

        // Group 0's pre-shot check now sees ammo==0 and hands off to group 1 within this Step;
        // group 1 fires nothing yet but its pre-shot check sees ammo>0, so the loop sound
        // switches one tick before group 1's first shot leaves the barrel.
        var o3 = Step(fc, fire: true);
        Assert.Empty(o3.GunShots);
        Assert.Equal(1, fc.GunSel);
        Assert.True(o3.GunLoopWanted);
        Assert.Equal("loop1", o3.GunLoopSound);

        // Tick 4: group 0 is no longer selected (contributes nothing) and group 1 is the one whose
        // pre-shot check now runs -> the sound has switched. Still no gap.
        var o4 = Step(fc, fire: true);
        Assert.Empty(o4.GunShots); // group 1's accumulator is only at 2 dt, still short of its interval
        Assert.Equal(1, fc.GunSel);
        Assert.True(o4.GunLoopWanted);
        Assert.Equal("loop1", o4.GunLoopSound);
    }

    [Fact]
    public void TheDryCueLatchesOnceAndRefillRearmsIt()
    {
        var gun = Gun(fireRate: 10f, ammo: 0, capacity: 5);
        var fc = Make(new List<FakeGun> { gun }, new List<FakePylon>());

        int dryCues = 0;
        for (int i = 0; i < 20; i++)
        {
            if (Step(fc, fire: true).GunDryCue)
            {
                dryCues++;
            }
        }

        Assert.Equal(1, dryCues); // latched -- it only ever sounds once across the whole held burst

        fc.Refill();
        Assert.Equal(5, gun.Ammo);

        var o = Step(fc, fire: false);
        Assert.False(o.GunDryCue);
        o = Step(fc, fire: true); // fresh press after a refill: real rounds again
        Assert.NotEmpty(o.GunShots);
        Assert.False(o.GunDryCue);
    }

    [Fact]
    public void InfiniteAmmoNeverDepletesNeverAutoAdvancesNeverSoundsDry()
    {
        var g0 = Gun(fireRate: 10f, ammo: 1, capacity: 1);
        var g1 = Gun(fireRate: 10f, ammo: 1, capacity: 1);
        var fc = Make(new List<FakeGun> { g0, g1 }, new List<FakePylon>(), infiniteAmmo: true);

        int shots = 0;
        for (int i = 0; i < 60; i++)
        {
            var o = Step(fc, fire: true);
            shots += o.GunShots.Count;
            Assert.False(o.GunDryCue);
            Assert.Equal(0, fc.GunSel);
        }

        Assert.True(shots > 1); // many shots, well past what 1 round could ever produce
        Assert.Equal(1, g0.Ammo);
        Assert.Equal(1, g1.Ammo);
    }

    // ==== Rockets ===============================================================================

    [Fact]
    public void HoldingTheTriggerLaunchesOnlyOnceForOneDiscretePull()
    {
        var pylon = Pylon(fireRate: 1f, ammo: 1000, capacity: 1000);
        var fc = Make(new List<FakeGun>(), new List<FakePylon> { pylon });

        int launches = 0;
        for (int i = 0; i < 30; i++)
        {
            if (Step(fc, rocket: true).RocketPylon >= 0)
            {
                launches++;
            }
        }

        Assert.Equal(1, launches);
    }

    [Fact]
    public void APullWithinTheCooldownLaunchesNothingAndIsNotADryPull()
    {
        var pylon = Pylon(fireRate: 1f, ammo: 1000, capacity: 1000); // 1s cooldown
        var fc = Make(new List<FakeGun>(), new List<FakePylon> { pylon });

        var first = Step(fc, rocket: true);
        Assert.True(first.RocketPylon >= 0);

        Step(fc, rocket: false); // release -- restores the discrete-pull edge
        var second = Step(fc, rocket: true); // pulled again a tick later: cooldown still hot

        Assert.Equal(-1, second.RocketPylon);
        Assert.False(second.RocketDryCue); // the pylon is still armed -- this is a cooldown gate, not a dry clip
    }

    /// <summary>An empty-pylon pull sounds the dry cue even while the launch cooldown from
    /// the very last shot is still hot, because the code checks "is anything armed" before it checks
    /// the cooldown. Drains two single-round pylons (waiting out the cooldown between each REAL
    /// launch, as the case calls for), then pulls again immediately -- cooldown still hot, but every
    /// pylon is spent, so the dry cue must fire anyway. Also proves the cue latches and that Refill()
    /// re-arms it.</summary>
    [Fact]
    public void ADryPullSoundsTheCueEvenWithAHotCooldownAndLatchesUntilRefill()
    {
        var p0 = Pylon(fireRate: 1f, ammo: 1, capacity: 1, id: "wep_p0"); // 1s cooldown
        var p1 = Pylon(fireRate: 1f, ammo: 1, capacity: 1, id: "wep_p1");
        var fc = Make(new List<FakeGun>(), new List<FakePylon> { p0, p1 });

        // Launch 1: drains pylon 0.
        var launch1 = Step(fc, rocket: true);
        Assert.Equal(0, launch1.RocketPylon);
        Step(fc, rocket: false);

        // Wait out the cooldown (a little over 1s) before pulling again, so this pull is a REAL
        // launch rather than being swallowed by the cooldown gate.
        for (int i = 0; i < 65; i++)
        {
            Step(fc, rocket: false);
        }

        // Launch 2: drains pylon 1 -- every pylon is now empty.
        var launch2 = Step(fc, rocket: true);
        Assert.Equal(1, launch2.RocketPylon);
        Step(fc, rocket: false);

        // Pull again IMMEDIATELY -- the cooldown from launch 2 is still hot, but nothing is armed.
        var dryPull = Step(fc, rocket: true);
        Assert.Equal(-1, dryPull.RocketPylon);
        Assert.True(dryPull.RocketDryCue);

        // Latched: further pulls give no second cue.
        Step(fc, rocket: false);
        var again = Step(fc, rocket: true);
        Assert.False(again.RocketDryCue);

        // Refill re-arms it: force both pylons empty again (simulating a drain) and pull once more.
        fc.Refill();
        p0.Ammo = 0;
        p1.Ammo = 0;
        Step(fc, rocket: false);
        var afterRefillDry = Step(fc, rocket: true);
        Assert.True(afterRefillDry.RocketDryCue);
    }

    [Fact]
    public void TheSelectedPylonDrainsThenTheCursorLeavesItInstantly()
    {
        var p0 = Pylon(fireRate: 1f, ammo: 1, capacity: 1, id: "wep_p0");
        var p1 = Pylon(fireRate: 1f, ammo: 1, capacity: 1, id: "wep_p1");
        var fc = Make(new List<FakeGun>(), new List<FakePylon> { p0, p1 });

        Assert.Equal(0, fc.SelectedPylon);
        var o = Step(fc, rocket: true);
        Assert.Equal(0, o.RocketPylon);

        // Advanced on the SAME launch that emptied it -- not deferred to the next pull.
        Assert.Equal(1, fc.SelectedPylon);
    }

    [Fact]
    public void RefillPutsTheCursorBackAtZeroAndReTopsEveryPylon()
    {
        var p0 = Pylon(fireRate: 1f, ammo: 1, capacity: 3, id: "wep_p0");
        var p1 = Pylon(fireRate: 1f, ammo: 1, capacity: 3, id: "wep_p1");
        var fc = Make(new List<FakeGun>(), new List<FakePylon> { p0, p1 });

        Step(fc, rocket: true); // drains p0, cursor advances to p1
        Assert.Equal(1, fc.SelectedPylon);

        fc.Refill();

        Assert.Equal(0, fc.SelectedPylon);
        Assert.Equal(3, p0.Ammo);
        Assert.Equal(3, p1.Ammo);
    }

    [Fact]
    public void TheGunPickPersistsAcrossARefill()
    {
        var g0 = Gun(fireRate: 10f, ammo: 3, capacity: 3, id: "wep_g0");
        var g1 = Gun(fireRate: 10f, ammo: 3, capacity: 3, id: "wep_g1");
        var fc = Make(new List<FakeGun> { g0, g1 }, new List<FakePylon>());

        fc.SelectGunGroup(1);
        fc.Refill();

        Assert.Equal(1, fc.GunSel);
    }

    /// <summary>Only <c>--fire-rockets</c> (soak runs) auto-repeats a held trigger; the cooldown still
    /// caps the rate. FireRate=1 => a 1s cooldown => at 60 held ticks/s, exactly 2 launches land
    /// inside a 100-tick (1.67s) window: one at t~0 and one at t~1s.</summary>
    [Fact]
    public void AutoFireRocketsRepeatsAtTheCooldownCap()
    {
        var pylon = Pylon(fireRate: 1f, ammo: 1000, capacity: 1000);
        var fc = Make(new List<FakeGun>(), new List<FakePylon> { pylon }, autoFireRockets: true);

        int launches = 0;
        for (int i = 0; i < 100; i++)
        {
            if (Step(fc, rocket: true).RocketPylon >= 0)
            {
                launches++;
            }
        }

        Assert.Equal(2, launches);
    }

    // ==== Selectors =============================================================================

    [Fact]
    public void GHeldForManyTicksAdvancesTheGunSelectorExactlyOnce()
    {
        var g0 = Gun(fireRate: 10f, ammo: 3, capacity: 3, id: "wep_g0");
        var g1 = Gun(fireRate: 10f, ammo: 3, capacity: 3, id: "wep_g1");
        var fc = Make(new List<FakeGun> { g0, g1 }, new List<FakePylon>());

        for (int i = 0; i < 10; i++)
        {
            Step(fc, gunSel: true);
        }

        Assert.Equal(1, fc.GunSel); // one advance despite 10 held ticks -- edge-driven, not level-driven

        Step(fc, gunSel: false); // release
        Step(fc, gunSel: true); // press again

        Assert.Equal(0, fc.GunSel); // wraps back
    }

    [Fact]
    public void HStepsThroughEverySlotOnAUniformAmmoLoadout()
    {
        // Port of the old WeaponCursorTests case: 4 armed pylons, all loaded identically.
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 3, 3, "wep_p0"), Pylon(1f, 3, 3, "wep_p1"),
            Pylon(1f, 3, 3, "wep_p2"), Pylon(1f, 3, 3, "wep_p3"),
        };
        var fc = Make(new List<FakeGun>(), pylons);

        var trace = new List<int>();
        for (int press = 0; press < 5; press++)
        {
            Step(fc, rocketSel: true);
            trace.Add(fc.SelectedPylon);
            Step(fc, rocketSel: false);
        }

        Assert.Equal(new[] { 1, 2, 3, 0, 1 }, trace);
    }

    [Fact]
    public void TheSelectorSkipsEmptySlots()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 2, 2, "wep_p0"), Pylon(1f, 0, 2, "wep_p1"),
            Pylon(1f, 0, 2, "wep_p2"), Pylon(1f, 2, 2, "wep_p3"),
        };
        var fc = Make(new List<FakeGun>(), pylons);

        Step(fc, rocketSel: true); // from slot 0, jumps straight past the two empties to slot 3
        Assert.Equal(3, fc.SelectedPylon);

        Step(fc, rocketSel: false);
        Step(fc, rocketSel: true); // from slot 3, wraps past the empties back to slot 0
        Assert.Equal(0, fc.SelectedPylon);
    }

    [Fact]
    public void TheSelectorStaysPutWhenNoOtherSlotIsArmed()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 0, 3, "wep_p0"), Pylon(1f, 3, 3, "wep_p1"),
            Pylon(1f, 0, 3, "wep_p2"), Pylon(1f, 0, 3, "wep_p3"),
        };
        var fc = Make(new List<FakeGun>(), pylons);
        fc.SelectPylon(1);

        Step(fc, rocketSel: true);

        Assert.Equal(1, fc.SelectedPylon); // the only armed slot is the one already selected
    }

    [Fact]
    public void ASingleSlotNeverMovesUnderTheSelector()
    {
        var pylons = new List<FakePylon> { Pylon(1f, 3, 3) };
        var fc = Make(new List<FakeGun>(), pylons);

        Step(fc, rocketSel: true);

        Assert.Equal(0, fc.SelectedPylon); // the <2-slots guard: nothing to advance to
    }

    /// <summary>Port of the old WeaponCursorTests.FiringDrainsTheSelectedSlotBeforeAdvancing, now run
    /// through FireControl.Step instead of WeaponCursor directly. FireRate=60 (interval==dt) so every
    /// accumulator crossing lands on an exact tick and this is deterministic. Started on group 2 via
    /// SelectGunGroup; 20 held ticks is comfortably more than the ~13 needed to drain all 4 groups of
    /// 3 rounds each.</summary>
    [Fact]
    public void FiringDrainsTheSelectedGroupBeforeAdvancingToTheNext()
    {
        var guns = new List<FakeGun>
        {
            Gun(60f, 3, 3, id: "wep_g0"), Gun(60f, 3, 3, id: "wep_g1"),
            Gun(60f, 3, 3, id: "wep_g2"), Gun(60f, 3, 3, id: "wep_g3"),
        };
        var fc = Make(guns, new List<FakePylon>());
        fc.SelectGunGroup(2);

        var firedGroups = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            foreach (var (group, _) in Step(fc, fire: true).GunShots)
            {
                firedGroups.Add(group);
            }
        }

        Assert.Equal(new[] { 2, 2, 2, 3, 3, 3, 0, 0, 0, 1, 1, 1 }, firedGroups);
    }

    [Fact]
    public void InfiniteAmmoSelectorStillStepsFreely()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 1, 1, "wep_p0"), Pylon(1f, 1, 1, "wep_p1"), Pylon(1f, 1, 1, "wep_p2"),
        };
        var fc = Make(new List<FakeGun>(), pylons, infiniteAmmo: true);

        var trace = new List<int>();
        for (int press = 0; press < 3; press++)
        {
            Step(fc, rocketSel: true);
            trace.Add(fc.SelectedPylon);
            Step(fc, rocketSel: false);
        }

        Assert.Equal(new[] { 1, 2, 0 }, trace);
    }

    /// <summary>A full eight-pylon fit binds in <c>Loadout.PylonFillOrder</c> (1,5,2,6,3,7,4,8), so
    /// stepping the list itself sent the gauge arrow back and forth across the belt. Given the step
    /// order the arrow walks the belt: list positions 0,2,4,6,1,3,5,7 are pylons 1..8 in turn.</summary>
    [Fact]
    public void HWalksThePylonsInMountOrderNotInTheOrderTheFitWasBuilt()
    {
        var pylons = new List<FakePylon>();
        for (int i = 0; i < 8; i++)
        {
            pylons.Add(Pylon(1f, 3, 3, $"wep_p{i}"));
        }
        // Loadout.StepOrder's output for the fill order 1,5,2,6,3,7,4,8.
        var fc = Make(new List<FakeGun>(), pylons, stepOrder: new[] { 0, 2, 4, 6, 1, 3, 5, 7 });

        var trace = new List<int> { fc.SelectedPylon };
        for (int press = 0; press < 8; press++)
        {
            Step(fc, rocketSel: true);
            trace.Add(fc.SelectedPylon);
            Step(fc, rocketSel: false);
        }

        Assert.Equal(new[] { 0, 2, 4, 6, 1, 3, 5, 7, 0 }, trace);
    }

    /// <summary>The rocket trigger's own cursor walks the same sequence as H: with only pylons 1 and
    /// 2 (list positions 0 and 2) armed, draining the first hands the next launch to the second
    /// rather than to whichever slot happens to sit next in the list.</summary>
    [Fact]
    public void TheFiringCursorAdvancesAlongTheSameMountOrder()
    {
        var pylons = new List<FakePylon>();
        for (int i = 0; i < 8; i++)
        {
            pylons.Add(Pylon(60f, i == 0 || i == 2 ? 1 : 0, 1, $"wep_p{i}"));
        }
        var fc = Make(new List<FakeGun>(), pylons, stepOrder: new[] { 0, 2, 4, 6, 1, 3, 5, 7 });

        var launched = new List<int>();
        for (int i = 0; i < 8; i++)
        {
            var o = Step(fc, rocket: i % 2 == 0); // discrete pulls: the gate is one launch per press
            if (o.RocketPylon >= 0)
            {
                launched.Add(o.RocketPylon);
            }
        }

        Assert.Equal(new[] { 0, 2 }, launched);
    }

    [Fact]
    public void AStepOrderThatIsNotAPermutationIsALoudError()
    {
        var pylons = new List<FakePylon> { Pylon(1f, 1, 1), Pylon(1f, 1, 1) };

        Assert.Throws<System.ArgumentException>(() => Make(new List<FakeGun>(), pylons, stepOrder: new[] { 1, 1 }));
        Assert.Throws<System.ArgumentException>(() => Make(new List<FakeGun>(), pylons, stepOrder: new[] { 0 }));
    }

    // ==== Selectors, backward ===================================================================

    /// <summary>The backward step walks the same physical mount order as the forward one, read the
    /// other way: on the full eight-pylon fit whose list order is the fill order 1,5,2,6,3,7,4,8,
    /// one press back off pylon 1 lands on pylon 8 and the walk continues along the belt.</summary>
    [Fact]
    public void TheBackwardHardpointStepWalksTheSameMountOrderInReverse()
    {
        var pylons = new List<FakePylon>();
        for (int i = 0; i < 8; i++)
        {
            pylons.Add(Pylon(1f, 3, 3, $"wep_p{i}"));
        }
        var fc = Make(new List<FakeGun>(), pylons, stepOrder: new[] { 0, 2, 4, 6, 1, 3, 5, 7 });

        var trace = new List<int> { fc.SelectedPylon };
        for (int press = 0; press < 8; press++)
        {
            Press(fc, rocketSelBack: true);
            trace.Add(fc.SelectedPylon);
        }

        Assert.Equal(new[] { 0, 7, 5, 3, 1, 6, 4, 2, 0 }, trace);
    }

    /// <summary>A mixed fit, the case custom loadouts make ordinary: with slot 2 spent, both
    /// directions skip it and each lands on an armed slot. The two traces are each other's
    /// reverse, which is what makes the pair usable as a correction at the controls.</summary>
    [Fact]
    public void BothDirectionsSkipEmptySlotsOnAMixedFit()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 2, 2, "wep_p0"), Pylon(1f, 2, 2, "wep_p1"),
            Pylon(1f, 0, 2, "wep_p2"), Pylon(1f, 2, 2, "wep_p3"),
        };
        var fc = Make(new List<FakeGun>(), pylons);

        var forward = new List<int>();
        for (int press = 0; press < 3; press++)
        {
            Press(fc, rocketSel: true);
            forward.Add(fc.SelectedPylon);
        }

        var backward = new List<int>();
        for (int press = 0; press < 3; press++)
        {
            Press(fc, rocketSelBack: true);
            backward.Add(fc.SelectedPylon);
        }

        Assert.Equal(new[] { 1, 3, 0 }, forward);
        Assert.Equal(new[] { 3, 1, 0 }, backward);
    }

    [Fact]
    public void OneStepEachWayReturnsTheHardpointCursorToWhereItStarted()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 2, 2, "wep_p0"), Pylon(1f, 0, 2, "wep_p1"),
            Pylon(1f, 2, 2, "wep_p2"), Pylon(1f, 0, 2, "wep_p3"),
        };
        var fc = Make(new List<FakeGun>(), pylons);

        Press(fc, rocketSel: true);
        Assert.Equal(2, fc.SelectedPylon);

        Press(fc, rocketSelBack: true);
        Assert.Equal(0, fc.SelectedPylon);
    }

    /// <summary>One armed slot is the degenerate fit: the backward press has nowhere to go and
    /// leaves the cursor alone rather than parking it on a spent pylon, on a belt of four and on a
    /// single-pylon plane the <c>&lt;2 slots</c> guard turns away before the scan.</summary>
    [Fact]
    public void TheBackwardStepStaysPutWhenOnlyOneSlotIsArmed()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 0, 3, "wep_p0"), Pylon(1f, 3, 3, "wep_p1"),
            Pylon(1f, 0, 3, "wep_p2"), Pylon(1f, 0, 3, "wep_p3"),
        };
        var fc = Make(new List<FakeGun>(), pylons);
        fc.SelectPylon(1);

        Press(fc, rocketSelBack: true);
        Assert.Equal(1, fc.SelectedPylon);

        var single = Make(new List<FakeGun>(), new List<FakePylon> { Pylon(1f, 3, 3) });
        Press(single, rocketSelBack: true);
        Assert.Equal(0, single.SelectedPylon);
    }

    /// <summary>The gun-group cycle was never watched at the original's controls, but its keybind
    /// page names both directions (`Cycle guns clockwise` / `counterclockwise`), so both are wired
    /// and both are held to the same empty-skipping walk the hardpoints take.</summary>
    [Fact]
    public void TheGunGroupSelectorStepsBothWays()
    {
        var guns = new List<FakeGun>
        {
            Gun(10f, 3, 3, id: "wep_g0"), Gun(10f, 0, 3, id: "wep_g1"),
            Gun(10f, 3, 3, id: "wep_g2"), Gun(10f, 3, 3, id: "wep_g3"),
        };
        var fc = Make(guns, new List<FakePylon>());

        var forward = new List<int>();
        for (int press = 0; press < 3; press++)
        {
            Press(fc, gunSel: true);
            forward.Add(fc.GunSel);
        }

        var backward = new List<int>();
        for (int press = 0; press < 3; press++)
        {
            Press(fc, gunSelBack: true);
            backward.Add(fc.GunSel);
        }

        Assert.Equal(new[] { 2, 3, 0 }, forward);
        Assert.Equal(new[] { 3, 2, 0 }, backward);
    }

    [Fact]
    public void BothDirectionsHeldAtOnceLeaveTheCursorWhereItWas()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 3, 3, "wep_p0"), Pylon(1f, 3, 3, "wep_p1"),
            Pylon(1f, 3, 3, "wep_p2"), Pylon(1f, 3, 3, "wep_p3"),
        };
        var fc = Make(new List<FakeGun>(), pylons);

        Press(fc, rocketSel: true, rocketSelBack: true);

        Assert.Equal(0, fc.SelectedPylon); // one step out and one straight back, on the same edge
    }

    [Fact]
    public void TheBackwardSelectorIsEdgeDrivenLikeTheForwardOne()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 3, 3, "wep_p0"), Pylon(1f, 3, 3, "wep_p1"), Pylon(1f, 3, 3, "wep_p2"),
        };
        var fc = Make(new List<FakeGun>(), pylons);

        for (int i = 0; i < 10; i++)
        {
            Step(fc, rocketSelBack: true);
        }

        Assert.Equal(2, fc.SelectedPylon); // one step back despite 10 held ticks
    }

    [Fact]
    public void APadTapStepsTheGunSelectorForwardOnRelease()
    {
        var fc = Make(FourGuns(), new List<FakePylon>());

        var duringPress = new List<int>();
        for (int frame = 0; frame < 6; frame++)   // 0.1 s, well inside the hold threshold
        {
            Step(fc, gunSelPad: true);
            duringPress.Add(fc.GunSel);
        }
        Step(fc);

        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0 }, duringPress); // the tap resolves on release, not on press
        Assert.Equal(1, fc.GunSel);
    }

    [Fact]
    public void APadHoldStepsTheGunSelectorBackOnceAndDoesNotAlsoTap()
    {
        var fc = Make(FourGuns(), new List<FakePylon>());

        // Two seconds down: the hold fires once, on the frame it crosses the threshold, and the
        // release after it is spent.
        for (int frame = 0; frame < 120; frame++)
        {
            Step(fc, gunSelPad: true);
        }
        int afterHold = fc.GunSel;
        Step(fc);

        Assert.Equal(3, afterHold);          // one step BACK from slot 0, wrapping onto the tail
        Assert.Equal(3, fc.GunSel);          // and the release adds no forward step of its own
    }

    [Fact]
    public void ThePadsRocketButtonSplitsTheSameWay()
    {
        var pylons = new List<FakePylon>
        {
            Pylon(1f, 3, 3, "wep_p0"), Pylon(1f, 3, 3, "wep_p1"),
            Pylon(1f, 3, 3, "wep_p2"), Pylon(1f, 3, 3, "wep_p3"),
        };
        var fc = Make(new List<FakeGun>(), pylons);

        for (int frame = 0; frame < 6; frame++)
        {
            Step(fc, rocketSelPad: true);
        }
        Step(fc);
        int afterTap = fc.SelectedPylon;

        for (int frame = 0; frame < 120; frame++)
        {
            Step(fc, rocketSelPad: true);
        }
        Step(fc);

        Assert.Equal(1, afterTap);
        Assert.Equal(0, fc.SelectedPylon);   // the hold walks back off it again
    }

    /// <summary>The keyboard keeps a key per direction, so a held key is still one step: a pilot
    /// leaning on F3 must not have it read as the counterclockwise cycle.</summary>
    [Fact]
    public void AHeldKeyStepsOnceHoweverLongItIsDown()
    {
        var fc = Make(FourGuns(), new List<FakePylon>());

        for (int frame = 0; frame < 120; frame++)
        {
            Step(fc, gunSel: true);
        }
        Step(fc);

        Assert.Equal(1, fc.GunSel);
    }

    // ==== Fakes and builders ====================================================================

    // Four armed gun slots, the shape the selector walks in both directions.
    private static List<FakeGun> FourGuns() => new()
    {
        Gun(10f, 3, 3, id: "wep_g0"), Gun(10f, 3, 3, id: "wep_g1"),
        Gun(10f, 3, 3, id: "wep_g2"), Gun(10f, 3, 3, id: "wep_g3"),
    };

    private static FakeGun Gun(float fireRate, int ammo, int capacity, int muzzleCount = 1, string? loopSound = null, string id = "wep_gun") =>
        new()
        {
            Weapon = new WeaponDef { Id = id, Name = id, FireRate = fireRate, LoopedSoundName = loopSound },
            Ammo = ammo,
            Capacity = capacity,
            MuzzleCount = muzzleCount,
        };

    private static FakePylon Pylon(float fireRate, int ammo, int capacity, string id = "wep_rocket") =>
        new()
        {
            Weapon = new WeaponDef { Id = id, Name = id, FireRate = fireRate },
            Ammo = ammo,
            Capacity = capacity,
        };

    private static FireControl Make(List<FakeGun> guns, List<FakePylon> pylons,
        bool autoFireRockets = false, bool infiniteAmmo = false, int initialGunSelect = 0,
        IReadOnlyList<int>? stepOrder = null) =>
        new(guns, pylons, autoFireRockets, infiniteAmmo, initialGunSelect, stepOrder);

    // One tick. Held levels only (no edges, FireControl does its own edge
    // detection), dt fixed at 60 Hz throughout. The returned FireOutcome is the SAME
    // reused instance every call, callers must read what they need before the next Step.
    private static FireOutcome Step(FireControl fc, bool fire = false, bool rocket = false,
        bool gunSel = false, bool rocketSel = false, bool gunSelBack = false, bool rocketSelBack = false,
        bool gunSelPad = false, bool rocketSelPad = false) =>
        fc.Step(Dt, new FireInputs
        {
            FireHeld = fire,
            RocketHeld = rocket,
            GunSelectHeld = gunSel,
            RocketSelectHeld = rocketSel,
            GunSelectBackHeld = gunSelBack,
            RocketSelectBackHeld = rocketSelBack,
            GunSelectPadHeld = gunSelPad,
            RocketSelectPadHeld = rocketSelPad,
        });

    // One press of a selector direction and the release after it, which is what the rising-edge
    // gate needs to see. Returns nothing: every caller reads the cursor off the FireControl.
    private static void Press(FireControl fc, bool gunSel = false, bool rocketSel = false,
        bool gunSelBack = false, bool rocketSelBack = false)
    {
        Step(fc, gunSel: gunSel, rocketSel: rocketSel, gunSelBack: gunSelBack, rocketSelBack: rocketSelBack);
        Step(fc);
    }

    private sealed class FakeGun : IGunSlot
    {
        public WeaponDef Weapon { get; set; } = new();
        public int Ammo { get; set; }
        public int Capacity { get; set; }
        public int MuzzleCount { get; set; } = 1;
    }

    private sealed class FakePylon : IPylonSlot
    {
        public WeaponDef Weapon { get; set; } = new();
        public int Ammo { get; set; }
        public int Capacity { get; set; }
    }
}
