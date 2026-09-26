using System.Linq;
using CSVM.Flight.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>B13's contract: the decoded tables carry org/hangar.md's values verbatim, whole
/// builds total exactly as the original's formulas do, and the purchase gate answers all three
/// verdicts. Expected figures are hand-computed from org/hangar.md "The economy".</summary>
public class HangarEconomyTests
{
    [Theory]
    [InlineData(0, 6800, 1400, 4160, 20, 60, 12, 0x00)]
    [InlineData(2, 1870, 5460, 15760, -2, 125, 3, 0x0c)]
    [InlineData(10, 2423, 5005, 14675, 2, 120, 19, 0x00)]
    public void AirframeTable_MatchesTheDecode(
        int id, int cost, int weight, int capacity, int agility, int armour, int avail, int mask)
    {
        var row = HangarEconomy.Airframes[id];
        Assert.Equal(cost, row.Cost);
        Assert.Equal(weight, row.Weight);
        Assert.Equal(capacity, row.Capacity);
        Assert.Equal(agility, row.Agility);
        Assert.Equal(armour, row.Armour);
        Assert.Equal(avail, row.Availability);
        Assert.Equal(mask, row.TurretMask);
    }

    [Fact]
    public void AirframeTable_SlotTitles_MatchTheDecode()
    {
        Assert.Equal(new[] { 3061, 3070, 3062, 3069 }, Titles(0));
        Assert.Equal(new[] { 3061, 3062, 3060, 3073 }, Titles(2));
        Assert.Equal(new[] { 3061, 3070, 3062, 3069 }, Titles(10));

        static int[] Titles(int id)
        {
            var row = HangarEconomy.Airframes[id];
            return new[] { row.SlotTitle(0), row.SlotTitle(1), row.SlotTitle(2), row.SlotTitle(3) };
        }
    }

    [Theory]
    [InlineData(0, 240, 440, 280, 520, 10, 2800)]
    [InlineData(1, 320, 530, 380, 620, 9, 2400)]
    [InlineData(2, 410, 610, 480, 720, 8, 2000)]
    [InlineData(3, 490, 700, 580, 820, 7, 1600)]
    [InlineData(4, 580, 780, 680, 920, 6, 1200)]
    public void GunTable_MatchesTheDecode(
        int id, int wingCost, int turretCost, int wingWeight, int turretWeight, int rate, int magazine)
    {
        // The range column is 1000 ft down every calibre.
        Assert.Equal(
            new GunStats(wingCost, turretCost, wingWeight, turretWeight, rate, magazine, 1000),
            HangarEconomy.GunTable[id]);
    }

    [Theory]
    [InlineData(0, false, 10.0, 1400)]
    [InlineData(0, true, 5.0, 2800)]
    [InlineData(4, false, 6.0, 600)]
    [InlineData(4, true, 3.0, 1200)]
    public void GunFigures_HalveTheRateForATwinAndTheMagazineForASingle(int calibre, bool twin, double rate, int ammo)
    {
        var figures = HangarEconomy.GunFigures(new GunChoice(calibre, twin));
        Assert.Equal(rate, figures.Rate);
        Assert.Equal(ammo, figures.Ammo);
        Assert.Equal(1000, figures.Range);
    }

    [Fact]
    public void GunFigures_AnEmptyMountHasNone()
    {
        Assert.Equal(default, HangarEconomy.GunFigures(default));
        Assert.Equal(new[] { 30, 40, 50, 60, 70 }, new[] { 0, 1, 2, 3, 4 }.Select(HangarEconomy.Calibre));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 1)]
    public void AirframeTurrets_CountTheMaskedSlots(int airframe, int turrets)
    {
        Assert.Equal(turrets, HangarEconomy.Airframes[airframe].Turrets);
    }

    [Theory]
    [InlineData(0, 0, 425, 500)] // Hoplite base 850/1000, id 0 offsets -425/-500.
    [InlineData(2, 5, 3405, 4000)] // Balmoral base 2550/3000, id 5 offsets +855/+1000.
    [InlineData(10, 3, 2555, 3000)] // Warhawk base 2550/3000, id 3 offsets +5/+0.
    public void EngineLine_AppliesBaseAndOffsets(int airframe, int engineId, int cost, int weight)
    {
        Assert.Equal(new CostWeight(cost, weight), HangarEconomy.EngineLine(airframe, engineId));
    }

    [Fact]
    public void EngineLine_NoEngine_IsZero()
    {
        Assert.Equal(default(CostWeight), HangarEconomy.EngineLine(0, CustomPlaneDef.EngineNone));
    }

    [Fact]
    public void EmptyBuild_TotalsAirframePlusEngine_AndPassesTheGate()
    {
        // Hoplite 6800/1400 + engine id 1 (base 850/1000, offsets 0/0) and nothing else.
        var def = new CustomPlaneDef { Airframe = 0, Engine = 1 };

        var bill = HangarEconomy.Price(def);

        Assert.Equal(new CostWeight(6800, 1400), bill.Airframe);
        Assert.Equal(new CostWeight(850, 1000), bill.Engine);
        Assert.All(bill.Guns, g => Assert.Equal(default(CostWeight), g));
        Assert.Equal(default(CostWeight), bill.Armour);
        Assert.Equal(default(CostWeight), bill.Hardpoints);
        Assert.Equal(new CostWeight(7650, 2400), bill.Total);
        Assert.Equal(4160, bill.Capacity);
        Assert.Equal(PurchaseVerdict.Ok, bill.Verdict);
    }

    [Fact]
    public void MaxedBalmoral_GoesOverweight()
    {
        // Balmoral 1870/5460 + engine 5 (3405/4000) + four twin .70s per turret mask 0x0c (cost
        // 2*(580+580+780+780) = 5440, weight 2*(680+680+920+920) = 6400) + 48 armour presses at
        // 240 units (960/960) + 8 hardpoints (3280/3840) = 14955 / 20660, over capacity 15760.
        var def = new CustomPlaneDef
        {
            Airframe = 2,
            Engine = 5,
            ArmourNose = 12,
            ArmourTail = 12,
            ArmourLeftWing = 12,
            ArmourRightWing = 12,
            LeftHardpoints = 4,
            RightHardpoints = 4,
        };
        for (int i = 0; i < def.Guns.Length; i++)
        {
            def.Guns[i] = new GunChoice(4, true);
        }

        var bill = HangarEconomy.Price(def);

        Assert.Equal(new CostWeight(1160, 1360), bill.Guns[0]);
        Assert.Equal(new CostWeight(1160, 1360), bill.Guns[1]);
        Assert.Equal(new CostWeight(1560, 1840), bill.Guns[2]);
        Assert.Equal(new CostWeight(1560, 1840), bill.Guns[3]);
        Assert.Equal(new CostWeight(960, 960), bill.Armour);
        Assert.Equal(new CostWeight(3280, 3840), bill.Hardpoints);
        Assert.Equal(new CostWeight(14955, 20660), bill.Total);
        Assert.Equal(PurchaseVerdict.Overweight, bill.Verdict);
    }

    [Fact]
    public void EnginelessWarhawk_IsRejectedForItsEngine()
    {
        // Warhawk 2423/5005, no engine (0/0), slot 0 single .30 on a wing (mask 0x00):
        // 240/280. Nose armour 2 presses, so 10 units: 40/40. One left hardpoint: 410/480.
        // Totals 3113 / 5805, under capacity 14675, so only the engine blocks it.
        var def = new CustomPlaneDef { Airframe = 10, ArmourNose = 2, LeftHardpoints = 1 };
        def.Guns[0] = new GunChoice(0, false);

        var bill = HangarEconomy.Price(def);

        Assert.Equal(default(CostWeight), bill.Engine);
        Assert.Equal(new CostWeight(240, 280), bill.Guns[0]);
        Assert.Equal(new CostWeight(3113, 5805), bill.Total);
        Assert.Equal(PurchaseVerdict.NoEngine, bill.Verdict);
    }

    [Fact]
    public void StarRatings_MatchTheDecodedFormulas()
    {
        // Hoplite: agility (20-1)/4 = 4; armour (60-1)/73 = 0 with no units.
        var hoplite = HangarEconomy.Price(new CustomPlaneDef { Airframe = 0, Engine = 1 });
        Assert.Equal(4, hoplite.AgilityStars);
        Assert.Equal(0, hoplite.ArmourStars);

        // Balmoral maxed: agility (-2-1)/4 truncates to 0; armour reads the record's own unit
        // count, so 48 presses are 240 units and give (125 + 240 - 1)/73 = 4.
        var balmoral = HangarEconomy.Price(new CustomPlaneDef
        {
            Airframe = 2,
            Engine = 1,
            ArmourNose = 12,
            ArmourTail = 12,
            ArmourLeftWing = 12,
            ArmourRightWing = 12,
        });
        Assert.Equal(0, balmoral.AgilityStars);
        Assert.Equal(4, balmoral.ArmourStars);

        // Warhawk, 2 nose presses: agility (2-1)/4 = 0; armour (120 + 10 - 1)/73 = 1.
        var warhawk = HangarEconomy.Price(new CustomPlaneDef { Airframe = 10, Engine = 1, ArmourNose = 2 });
        Assert.Equal(0, warhawk.AgilityStars);
        Assert.Equal(1, warhawk.ArmourStars);
    }
}
