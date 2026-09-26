using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Weapons;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The VELOCITY/ACCELERATION/GRAVITY integration (<see cref="Ballistics"/>) the live rounds and the
/// gun reticle share. Every expectation here is computed by hand from the scheme
/// (semi-implicit Euler: accelerate, then advance by the new velocity), never captured from the
/// implementation, a test that records what the code does cannot catch the code being wrong.
///
/// <para>The last two are the census behind the reticle's fixed step: no weapon a gun group can
/// resolve carries a non-zero ACCELERATION or GRAVITY, so the fixed <c>1/120 s</c> march and the
/// rounds' sim step produce the same straight line. They are tripwires, a data or loadout change
/// that makes an accelerating weapon gun-reachable fails them.</para>
/// </summary>
[Trait("Tier", "Quick")]
public class BallisticsTests
{
    private static string SharedZrdr =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>With no ACCELERATION and no GRAVITY the march is a straight line of exactly the
    /// asked-for path length, and the step size cannot move it, which is the whole reason the
    /// reticle may integrate at a different <c>dt</c> from the rounds.</summary>
    [Fact]
    public void AStraightLineMarchLandsAtTheAskedForDistanceWhateverTheStep()
    {
        var gun = new WeaponDef { Velocity = 1000f, Range = 1000f };
        var at120 = Ballistics.March(gun, Vector3.Zero, new Vector3(0f, 0f, -1f), Vector3.Zero, 500f, 1f / 120f);
        var at60 = Ballistics.March(gun, Vector3.Zero, new Vector3(0f, 0f, -1f), Vector3.Zero, 500f, 1f / 60f);

        Assert.Equal(0f, at120.X, 3);
        Assert.Equal(0f, at120.Y, 3);
        Assert.Equal(-500f, at120.Z, 2);
        Assert.Equal(at120.Z, at60.Z, 2);
    }

    /// <summary>The plane's own velocity is added to the muzzle velocity, so the march runs along
    /// the sum rather than along the muzzle's facing: 600 m of path down the unit vector of
    /// (100, 0, -1000).</summary>
    [Fact]
    public void TheMarchFollowsTheMuzzleVelocityPlusTheInheritedOne()
    {
        var gun = new WeaponDef { Velocity = 1000f, Range = 1000f };
        var p = Ballistics.March(gun, Vector3.Zero, new Vector3(0f, 0f, -1f), new Vector3(100f, 0f, 0f), 600f, 1f / 120f);

        // |(100,0,-1000)| = 1004.98756, so 600 m of path is 0.5970224 of it.
        Assert.Equal(59.7022f, p.X, 2);
        Assert.Equal(0f, p.Y, 3);
        Assert.Equal(-597.0223f, p.Z, 2);
    }

    /// <summary>100 steps of 0.01 s at 20 m/s² gravity. The scheme adds gravity to the velocity
    /// before advancing, so the drop is <c>g·dt²·Σn = 20 · 0.0001 · 5050 = 10.1 m</c>, one
    /// <c>g·dt²</c> more than the continuous <c>½gt² = 10.0 m</c>, which is what makes this an
    /// assertion about the integrator rather than about physics.</summary>
    [Fact]
    public void GravityDropsTheRoundByTheDiscreteSumNotTheContinuousOne()
    {
        var pos = Vector3.Zero;
        var vel = new Vector3(0f, 0f, -100f);
        for (int i = 0; i < 100; i++)
            Ballistics.Step(ref pos, ref vel, 0f, 20f, 100f, 0.01f);

        Assert.Equal(-10.1f, pos.Y, 3);
        Assert.Equal(-100f, pos.Z, 3);   // gravity is vertical: the horizontal run is untouched
        Assert.Equal(-20f, vel.Y, 3);    // 1 s at 20 m/s²
        Assert.Equal(-100f, vel.Z, 3);
    }

    /// <summary>A rocket motor accelerates along the current heading, so 10 steps of 0.1 s at
    /// 150 m/s² from 450 m/s covers <c>0.1 · Σ(450 + 15n) = 0.1 · 5325 = 532.5 m</c>.</summary>
    [Fact]
    public void AccelerationActsAlongTheHeadingAndCompoundsPerStep()
    {
        var pos = Vector3.Zero;
        var vel = new Vector3(0f, 0f, -450f);
        for (int i = 0; i < 10; i++)
            Ballistics.Step(ref pos, ref vel, 150f, 0f, 1000f, 0.1f);

        Assert.Equal(-532.5f, pos.Z, 2);
        Assert.Equal(-600f, vel.Z, 3);   // 450 + 10 × 15
        Assert.Equal(0f, pos.Y, 3);
    }

    /// <summary>The motor stops at the cap and leaves the round there: 150 m/s² from 450 m/s
    /// against a 500 m/s cap reaches it on the fourth step and holds, so the run is
    /// <c>0.1 · (465 + 480 + 495 + 500 + 500) = 244 m</c> rather than the uncapped 247.5.</summary>
    [Fact]
    public void TheMotorClampsAtTheCapAndHoldsThere()
    {
        var pos = Vector3.Zero;
        var vel = new Vector3(0f, 0f, -450f);
        for (int i = 0; i < 5; i++)
            Ballistics.Step(ref pos, ref vel, 150f, 0f, 500f, 0.1f);

        Assert.Equal(-500f, vel.Z, 3);
        Assert.Equal(-244f, pos.Z, 2);
    }

    /// <summary>Nothing in the integrator reduces a round's own speed: neither a round already
    /// FASTER than its cap (the original leaves it alone rather than clamping down) nor one
    /// flying with no motor at all loses a metre per second over a long flight. The absence of
    /// drag is a decoded fact and the reason the original's rounds carry so far.</summary>
    [Fact]
    public void NoStepEverSlowsARound()
    {
        var pos = Vector3.Zero;
        var overspeed = new Vector3(0f, 0f, -700f);
        var coasting = new Vector3(0f, 0f, -400f);
        for (int i = 0; i < 600; i++)
        {
            Ballistics.Step(ref pos, ref overspeed, 150f, 0f, 500f, 1f / 60f);
            Ballistics.Step(ref pos, ref coasting, 0f, 0f, 400f, 1f / 60f);
        }

        Assert.Equal(-700f, overspeed.Z, 3);
        Assert.Equal(-400f, coasting.Z, 3);
    }

    /// <summary>A weapon's GRAVITY is the round's vertical acceleration in m/s², not a scale on
    /// world gravity: 1.0 costs the round 1 m/s of climb per second, not 9.8. Every shipped entry
    /// authors 0.0, so this pins the unit rather than any shipped flight.</summary>
    [Fact]
    public void WeaponGravityIsMetresPerSecondSquaredNotAScale()
    {
        var bomb = new WeaponDef { Velocity = 100f, Gravity = 1f, Range = 10000f };
        var p = Ballistics.March(bomb, Vector3.Zero, new Vector3(0f, 0f, -1f), Vector3.Zero, 100f, 0.01f);

        // 1 s of flight at 100 m/s: the discrete drop is 1 · 0.0001 · (100·101/2) = 0.505 m.
        Assert.Equal(-0.505f, p.Y, 3);
        Assert.Equal(-100f, p.Z, 1);
    }

    /// <summary>The launch pair the pool and the march share: a round with no motor is seeded AT
    /// its cap (so nothing accelerates it, however fast its launcher), while one with a motor
    /// leaves at the launcher's speed and climbs to VELOCITY above it.</summary>
    [Fact]
    public void AMotorRoundLeavesAtTheLaunchersSpeedAndClimbsToVelocityAboveIt()
    {
        var dumb = Ballistics.LaunchSpeed(450f, 0f, 120f);
        Assert.Equal(450f, dumb.Speed, 3);
        Assert.Equal(450f, dumb.Cap, 3);

        var motor = Ballistics.LaunchSpeed(450f, 150f, 120f);
        Assert.Equal(120f, motor.Speed, 3);
        Assert.Equal(570f, motor.Cap, 3);
    }

    /// <summary>The march stops at the weapon's RANGE, not at the distance the caller asked for,
    /// a round never converges past where it expires.</summary>
    [Fact]
    public void TheMarchStopsAtRangeWhenRangeIsShorterThanTheAskedForDistance()
    {
        var gun = new WeaponDef { Velocity = 1000f, Range = 800f };
        var p = Ballistics.March(gun, Vector3.Zero, new Vector3(0f, 0f, -1f), Vector3.Zero, 1000f, 1f / 120f);

        Assert.Equal(-800f, p.Z, 2);
    }

    /// <summary>The cap is on path length, not on time: a round diving under gravity speeds up
    /// every step, and still stops exactly 200 m down its path. The partial last step is what makes
    /// the endpoint exact rather than the first overshoot.</summary>
    [Fact]
    public void TheRangeCapMeasuresPathLengthEvenWhileTheRoundIsAccelerating()
    {
        var bomb = new WeaponDef { Velocity = 100f, Gravity = 1f, Range = 200f };
        var p = Ballistics.March(bomb, Vector3.Zero, Vector3.Down, Vector3.Zero, 1000f, 0.01f);

        Assert.Equal(0f, p.X, 3);
        Assert.Equal(-200f, p.Y, 2);
        Assert.Equal(0f, p.Z, 3);
    }

    /// <summary>A round with no speed at all must return its origin rather than spin the march's
    /// 4096 iterations for a step that never advances.</summary>
    [Fact]
    public void AZeroSpeedRoundMarchesNowhere()
    {
        var inert = new WeaponDef { Velocity = 0f, Range = 1000f };
        Assert.Equal(Vector3.Zero, Ballistics.March(inert, Vector3.Zero, Vector3.Forward, Vector3.Zero, 500f, 1f / 120f));
    }

    /// <summary>The census: of the 48 shipped weapons, four carry a non-zero ACCELERATION (the
    /// incendiary rocket, the AA flak rocket, the glide bomb and the fake weapon) and none carries
    /// a non-zero GRAVITY. Not one of the four is a gun.</summary>
    [ExtractedDataFact]
    public void OnlyFourOfTheFortyEightWeaponsAccelerateAndNoneHasGravity()
    {
        var accelerating = new List<string>();
        foreach (var def in WeaponDefs.Load(SharedZrdr).All)
        {
            Assert.Equal(0f, def.Gravity ?? 0f);
            if ((def.Acceleration ?? 0f) != 0f)
            {
                accelerating.Add(def.Id);
                Assert.False(def.IsGun, $"{def.Id} ({def.Name}) accelerates and is a gun");
            }
        }
        accelerating.Sort(System.StringComparer.Ordinal);
        Assert.Equal(new[] { "wep_04", "wep_25", "wep_26", "wep_27" }, accelerating);
    }

    /// <summary>…and none of those four is reachable from a gun group, which is what licenses the
    /// reticle's fixed integration step. A gun resolves to caliber + ammo offset, so the reachable
    /// set is every ammo type of every stock caliber, not just the stock slug.</summary>
    [ExtractedDataFact]
    public void NoWeaponAGunGroupCanResolveAcceleratesOrFalls()
    {
        var weapons = WeaponDefs.Load(SharedZrdr);
        var loadouts = StockLoadouts.Load(
            Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));

        int reached = 0;
        foreach (var (plane, loadout) in loadouts.All)
        {
            foreach (var gun in loadout.Guns)
            {
                foreach (var ammo in new[] { "slug", "dumdum", "ap", "magnesium" })
                {
                    var id = StockLoadouts.GunWeaponId(gun.Caliber, ammo);
                    var def = weapons.Get(id);
                    Assert.True(def != null, $"{plane} slot {gun.Slot}: {id} not in weapons.json");
                    Assert.Equal(0f, def!.Acceleration ?? 0f);
                    Assert.Equal(0f, def.Gravity ?? 0f);
                    reached++;
                }
            }
        }
        Assert.True(reached >= 20, $"only {reached} gun weapons reached; the loadouts stopped resolving");
    }
}
