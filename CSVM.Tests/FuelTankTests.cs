using System.IO;
using CSVM.Flight.Airframe;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The tank the player's lever burns, and the freeze a dry one causes. Decode:
/// docs/org/flightModel.md, "Part-throttle equilibrium", the burn is
/// <c>remaining -= dt · lever · 5</c> off the live lever, and a tank at zero skips the throttle
/// slew, holding the lever where it stands. The rate and the freeze are asserted against the
/// decoded literals; the capacity is read off the shipped data rather than written here, because
/// only the data says what a full tank is.
/// </summary>
public class FuelTankTests
{
    private const float Dt = 1f / 60f;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The burn rate, at three lever settings over one second of ticks. Linear in the
    /// lever, so a half-open lever costs half as much, and a closed one costs nothing at all.</summary>
    [Theory]
    [InlineData(1f, 5f)]
    [InlineData(0.5f, 2.5f)]
    [InlineData(0f, 0f)]
    public void TheBurnIsLeverTimesFivePerSecond(float lever, float expected)
    {
        var tank = new FuelTank { Capacity = 100f };
        tank.Fill();

        for (int i = 0; i < 60; i++)
            Assert.True(tank.Step(Dt, lever));

        Assert.Equal(100f - expected, tank.Remaining, 3);
    }

    /// <summary>A tank with less left than one tick costs lands on exactly zero, not below it.
    /// The original clamps at the store, and a negative remaining would read as a live tank to any
    /// consumer testing the sign rather than the comparison.</summary>
    [Fact]
    public void TheLastTickEmptiesTheTankWithoutGoingNegative()
    {
        var tank = new FuelTank { Capacity = 0.01f };
        tank.Fill();

        Assert.True(tank.Step(Dt, 1f));
        Assert.Equal(0f, tank.Remaining);
        Assert.True(tank.Dry);
    }

    /// <summary>The freeze: once the tank is dry the lever is refused, every tick, and no further
    /// fuel is drawn. Able to fail on an implementation that closes the lever instead, because that
    /// one would report the lever free at a zero command.</summary>
    [Fact]
    public void ADryTankRefusesTheLeverForeverAndBurnsNothingMore()
    {
        var tank = new FuelTank { Capacity = 5f };
        tank.Fill();

        // One second at full lever is exactly the capacity, so the tank ends this loop empty.
        for (int i = 0; i < 60; i++)
            tank.Step(Dt, 1f);

        Assert.True(tank.Dry);
        for (int i = 0; i < 10; i++)
            Assert.False(tank.Step(Dt, 1f));
        Assert.Equal(0f, tank.Remaining);
    }

    /// <summary>Inert at a full tank: the lever is free for every tick a shipped capacity survives,
    /// which is what keeps the flight envelope unmoved. Asserted over an hour of sim ticks rather
    /// than a handful, since the term only becomes visible at that scale.</summary>
    [Fact]
    public void AFullTankNeverRefusesTheLever()
    {
        var tank = new FuelTank { Capacity = 54926f };
        tank.Fill();

        for (int i = 0; i < 60 * 60 * 60; i++)
            Assert.True(tank.Step(Dt, 1f));

        Assert.True(tank.Remaining > 0f);
    }

    /// <summary>An airframe with no authored tank flies with a free lever rather than a frozen one.
    /// This arm is CSVM's, not the original's: every shipped player chain authors the key, so only a
    /// fixture built without one reaches it.</summary>
    [Fact]
    public void AnUnauthoredTankLeavesTheLeverFree()
    {
        var tank = new FuelTank();
        tank.Fill();

        Assert.False(tank.Dry);
        Assert.True(tank.Step(Dt, 1f));
    }

    /// <summary>The capacity comes off the shipped data, inherited down the player chain from the
    /// one def that authors it, so every airframe carries the same tank. At a fully open lever
    /// 54926 units is a little over three hours, which is why no shipped mission runs one dry.
    /// Read here rather than written, because only the data says what a full tank is.</summary>
    [ExtractedDataFact]
    public void EveryPlayerAirframeInheritsTheAuthoredCapacity()
    {
        Assert.Equal(54926f, PlaneStats.Load(ZrdrPath, "player_bhawk").FuelCapacity, 1);
        Assert.Equal(54926f, PlaneStats.Load(ZrdrPath, "player_balmoral").FuelCapacity, 1);
    }
}
