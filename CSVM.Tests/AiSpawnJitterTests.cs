using System;
using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's per-spawn dynamics jitter, ported as <see cref="PlaneStats.WithAiSpawnJitter"/>.
/// Full decode: <c>docs/org/flightModel.md</c> "The per-spawn jitter". Pins which slots move,
/// that the shared per-airframe cache is not itself perturbed, that draws are independent per
/// slot, and that the whole-vehicle damage pair is scaled while per-part pools are not.
/// The seeding policy lives at the spawn site; its determinism is pinned in <c>RngTests</c>.
/// </summary>
public class AiSpawnJitterTests
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The five dynamics slots the decode names, each inside the ±5 % band and each
    /// actually moved: <c>fd_speed</c>, <c>ThrustFactor</c>, <c>drag_factor</c>,
    /// <c>pitch_torque</c>, <c>roll_torque</c>.</summary>
    [Fact]
    public void TheFiveJitteredDynamicsSlotsLandInsideTheFivePercentBand()
    {
        var authored = Authored();

        var ai = authored.WithAiSpawnJitter(new Random(7));

        InBand(authored.FdSpeed, ai.FdSpeed, "fd_speed");
        InBand(authored.EnginePower, ai.EnginePower, "ThrustFactor");
        InBand(authored.DragFactor, ai.DragFactor, "drag_factor");
        InBand(authored.PitchTorque, ai.PitchTorque, "pitch_torque");
        InBand(authored.RollTorque, ai.RollTorque, "roll_torque");
    }

    /// <summary>Everything else in the <c>dynamics</c> block stays authored. <c>veh_weight</c> and
    /// <c>ref_area</c> matter most: they are what <c>FlightModel.StallSpeed</c> is computed from
    /// once at construction, and the decode does not draw for either.</summary>
    [Fact]
    public void TheRestOfTheDynamicsBlockIsUntouched()
    {
        var authored = Authored();

        var ai = authored.WithAiSpawnJitter(new Random(7));

        Assert.Equal(authored.VehWeight, ai.VehWeight);
        Assert.Equal(authored.RefArea, ai.RefArea);
        Assert.Equal(authored.RudderTorque, ai.RudderTorque);
        Assert.Equal(authored.ReturnRate, ai.ReturnRate);
        Assert.Equal(authored.AngMomentumDamp, ai.AngMomentumDamp);
        Assert.Equal(authored.RecInertia, ai.RecInertia);
    }

    /// <summary>The session hands every aircraft off one airframe the SAME cached
    /// <see cref="PlaneStats"/>. Perturbing it in place would spread the whole flight at once and
    /// re-spread it on every further spawn, so the jitter has to return a copy.</summary>
    [Fact]
    public void TheSharedAirframeStatsAreNotPerturbedInPlace()
    {
        var shared = Authored();

        var first = shared.WithAiSpawnJitter(new Random(1));
        var second = shared.WithAiSpawnJitter(new Random(2));

        Assert.Equal(100f, shared.FdSpeed);
        Assert.Equal(0.5f, shared.EnginePower);
        Assert.NotEqual(first.FdSpeed, second.FdSpeed);
    }

    /// <summary>Eleven draws, not one: the original calls <c>rand()</c> per slot, so two slots of an
    /// aircraft are not scaled by a common factor.</summary>
    [Fact]
    public void EachSlotDrawsItsOwnFactor()
    {
        var authored = Authored();

        var ai = authored.WithAiSpawnJitter(new Random(11));

        Assert.True(Math.Abs(ai.FdSpeed / authored.FdSpeed - ai.DragFactor / authored.DragFactor) > 1e-4f,
            "fd_speed and drag_factor were scaled by the same factor");
        Assert.True(Math.Abs(ai.PitchTorque / authored.PitchTorque - ai.RollTorque / authored.RollTorque) > 1e-4f,
            "pitch_torque and roll_torque were scaled by the same factor");
    }

    /// <summary>Same draws in, same aircraft out, the property a <c>--det</c> replay rests on, and
    /// the reason the spawn site keys its generator rather than sharing a stream.</summary>
    [Fact]
    public void TheSameDrawsGiveTheSameAircraft()
    {
        var authored = Authored();

        var a = authored.WithAiSpawnJitter(new Random(4));
        var b = authored.WithAiSpawnJitter(new Random(4));

        Assert.Equal(a.FdSpeed, b.FdSpeed);
        Assert.Equal(a.EnginePower, b.EnginePower);
        Assert.Equal(a.VehicleHealth, b.VehicleHealth);
    }

    /// <summary>Two spawn ordinals off the keyed generator the spawn site uses give two different
    /// aircraft, the other half of the replay property, and what makes a flight of four look like
    /// four aeroplanes rather than one repeated.</summary>
    [Fact]
    public void TwoSpawnOrdinalsGiveTwoDifferentAircraft()
    {
        var authored = Authored();

        var first = authored.WithAiSpawnJitter(Rng.NewSystemRandom(Rng.Spawn, 0, 0));
        var second = authored.WithAiSpawnJitter(Rng.NewSystemRandom(Rng.Spawn, 1, 0));

        Assert.NotEqual(first.FdSpeed, second.FdSpeed);
        Assert.NotEqual(first.RollTorque, second.RollTorque);
    }

    /// <summary>The whole-vehicle pair is what the original scales (<c>+0x2c4</c>/<c>+0x2cc</c>, each
    /// mirrored into its "current" slot); the per-part pools it never touches. A player airframe
    /// authors no pair, so the port writes the resolved sum over parts out explicitly, otherwise
    /// <see cref="PlaneDamage"/> would re-derive the unscaled hull and the draw would vanish.</summary>
    [Fact]
    public void TheHullPoolsAreScaledAndThePartPoolsAreNot()
    {
        var authored = Authored();
        Assert.Null(authored.VehicleHealth);

        var ai = authored.WithAiSpawnJitter(new Random(3));
        var damage = PlaneDamage.For(ai);

        InBand(40f, damage.WholeHealthMax, "hull health max");
        InBand(40f, damage.WholeArmorMax, "hull armour max");
        Assert.NotEqual(damage.WholeHealthMax, damage.WholeArmorMax);  // two independent draws
        foreach (var part in ai.DestroyableParts)
        {
            Assert.Equal(20f, part.MaxHp);
            Assert.Equal(20f, part.MaxArmor);
        }
    }

    /// <summary>An authored whole pair (the AI base defs carry one) is scaled where it stands rather
    /// than replaced by the parts' sum.</summary>
    [Fact]
    public void AnAuthoredHullPairIsScaledInPlace()
    {
        var authored = Authored();
        authored.VehicleHealth = 64f;
        authored.VehicleArmor = 64f;

        var ai = authored.WithAiSpawnJitter(new Random(5));

        InBand(64f, ai.VehicleHealth!.Value, "authored hull health");
        InBand(64f, ai.VehicleArmor!.Value, "authored hull armour");
    }

    /// <summary>The shipped Bloodhawk end to end: the jitter runs on real loaded stats, moves the
    /// five dynamics slots and leaves the airframe's identity, its weight/area pair and its AI range
    /// gates (def-level props, not <c>dynamics</c>) alone.</summary>
    [ExtractedDataFact]
    public void TheShippedAirframeIsJitteredWithoutLosingItsIdentity()
    {
        var stock = PlaneStats.Load(ZrdrPath, "player_bhawk");

        var ai = stock.WithAiSpawnJitter(new Random(9));

        Assert.Equal(stock.DefName, ai.DefName);
        Assert.Equal(stock.NodeName, ai.NodeName);
        Assert.Equal(stock.AiAttackRange, ai.AiAttackRange);
        Assert.Equal(stock.AiReturnRange, ai.AiReturnRange);
        Assert.Equal(stock.VehWeight, ai.VehWeight);
        Assert.Equal(stock.RefArea, ai.RefArea);
        InBand(stock.FdSpeed, ai.FdSpeed, "fd_speed");
        InBand(stock.EnginePower, ai.EnginePower, "ThrustFactor");
        InBand(stock.DragFactor, ai.DragFactor, "drag_factor");
        InBand(stock.PitchTorque, ai.PitchTorque, "pitch_torque");
        InBand(stock.RollTorque, ai.RollTorque, "roll_torque");
    }

    // A stats object with nothing loaded: enough to watch the arithmetic on, and the two
    // destroyable parts give the hull pair a sum to be derived from.
    private static PlaneStats Authored()
    {
        var stats = new PlaneStats
        {
            FdSpeed = 100f,
            EnginePower = 0.5f,
            DragFactor = 0.4f,
            PitchTorque = 2f,
            RollTorque = 6f,
            RudderTorque = 1.4f,
            ReturnRate = 3f,
            AngMomentumDamp = 5f,
            VehWeight = 1900f,
            RefArea = 330f,
        };
        stats.DestroyableParts.Add(new DestroyablePart { Name = "nose", MaxHp = 20f, MaxArmor = 20f });
        stats.DestroyableParts.Add(new DestroyablePart { Name = "tail", MaxHp = 20f, MaxArmor = 20f });
        return stats;
    }

    private static void InBand(float authored, float jittered, string what)
    {
        Assert.True(Math.Abs(jittered - authored) <= authored * PlaneStats.AiSpawnJitterSpread + 1e-4f,
            $"{what}: {jittered} is outside 1 ± 5 % of {authored}");
        Assert.NotEqual(authored, jittered);
    }
}
