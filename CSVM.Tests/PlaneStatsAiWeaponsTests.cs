using System.IO;
using CSVM.Flight.Airframe;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The AI armament half of the identity split: an AI load reads its own def's <c>weapons</c>
/// 5-tuples, a militia variant reads the variant's, and a player load reads none at all. The tuple's
/// field order is decoded in docs/org/aiPilot/aiWeapons.md; these cases pin it against the shipped
/// install rather than against numbers copied into the test.
/// </summary>
public class PlaneStatsAiWeaponsTests
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    // The Fury's three authored entries, in list order: two ordnance types then the gun. The gun's
    // 0.05 s over 1 to 900 m is only sensible one way round, which is what pins the field order.
    [ExtractedDataFact]
    public void AnAiLoadReadsItsOwnDefsWeaponTuples()
    {
        var s = PlaneStats.LoadForAi(ZrdrPath, "player_fury");

        Assert.Equal("fury", s.AiDefName);
        Assert.Equal(3, s.AiWeapons.Count);

        Assert.Equal("wep_04", s.AiWeapons[0].WeaponId);
        Assert.Equal(4, s.AiWeapons[0].Rounds);
        Assert.Equal(200f, s.AiWeapons[0].RefireSeconds, 3);
        Assert.Equal(30f, s.AiWeapons[0].MinRangeM, 3);
        Assert.Equal(800f, s.AiWeapons[0].MaxRangeM, 3);

        Assert.Equal("wep_07", s.AiWeapons[1].WeaponId);
        Assert.Equal(2, s.AiWeapons[1].Rounds);

        Assert.Equal("wep_130", s.AiWeapons[2].WeaponId);
        Assert.Equal(9000, s.AiWeapons[2].Rounds);
        Assert.Equal(0.05f, s.AiWeapons[2].RefireSeconds, 4);
        Assert.Equal(1f, s.AiWeapons[2].MinRangeM, 3);
        Assert.Equal(900f, s.AiWeapons[2].MaxRangeM, 3);
    }

    // A militia variant overrides the base def's whole block, which is the only way the Black Hat
    // Warhawk's eight torpedoes reach a session: the base warhawk carries rockets instead.
    [ExtractedDataFact]
    public void AMilitiaVariantFliesItsOwnFitRatherThanTheBaseDefs()
    {
        var militia = PlaneStats.LoadForAi(ZrdrPath, "player_warhawk", "bhatwarhawk");
        var stock = PlaneStats.LoadForAi(ZrdrPath, "player_warhawk");

        Assert.Equal("bhatwarhawk", militia.AiDefName);
        Assert.Equal("wep_14", militia.AiWeapons[0].WeaponId);
        Assert.Equal(8, militia.AiWeapons[0].Rounds);
        Assert.Equal(5f, militia.AiWeapons[0].RefireSeconds, 3);
        Assert.Equal(350f, militia.AiWeapons[0].MinRangeM, 3);
        Assert.NotEqual(stock.AiWeapons[0].WeaponId, militia.AiWeapons[0].WeaponId);
    }

    // The damage model follows the same name, so a militia variant is not an armament-only switch:
    // its armor/health pair is the one the plane flies with.
    [ExtractedDataFact]
    public void AMilitiaVariantAlsoCarriesItsOwnDamageModel()
    {
        var militia = PlaneStats.LoadForAi(ZrdrPath, "player_warhawk", "bhatwarhawk");

        Assert.NotNull(militia.VehicleHealth);
        Assert.NotNull(militia.VehicleArmor);
        Assert.Empty(militia.DestroyableParts);
    }

    // The guard against flying one airframe's def on another: nothing else stops a Fury's armament
    // and paint from being resolved onto a Warhawk.
    [ExtractedDataFact]
    public void ADefFromAnotherAirframeIsRefused()
    {
        Assert.Throws<System.ArgumentException>(
            () => PlaneStats.LoadForAi(ZrdrPath, "player_warhawk", "secfury"));
    }

    // The nine-slot pilot vector and the voice accent are def facts too: Black Hat's Warhawk flies
    // at 6 across most slots with a steady hand of 1, which is the pilot the militia authored.
    [ExtractedDataFact]
    public void AMilitiaVariantCarriesItsOwnPilotSkills()
    {
        var militia = PlaneStats.LoadForAi(ZrdrPath, "player_warhawk", "bhatwarhawk");

        Assert.Equal(6, militia.AiPilotSkills.DeadEye);
        Assert.Equal(6, militia.AiPilotSkills.QuickDraw);
        Assert.Equal(6, militia.AiPilotSkills.DareDevil);
        Assert.Equal(1, militia.AiPilotSkills.SteadyHand);
        Assert.NotNull(militia.AiAccentId);
    }

    [ExtractedDataFact]
    public void APlayerLoadReadsNoPilotSkillsEither()
    {
        var s = PlaneStats.Load(ZrdrPath, "player_warhawk");

        Assert.Null(s.AiPilotSkills.DeadEye);
        Assert.Null(s.AiAccentId);
    }

    // player_airplane authors a weapons block of its own, the 39-id buyable catalogue. Reading it as
    // a fit would arm the player's plane with every weapon in the game.
    [ExtractedDataFact]
    public void APlayerLoadReadsNoWeaponsBlockAtAll()
    {
        var s = PlaneStats.Load(ZrdrPath, "player_fury");

        Assert.Null(s.AiDefName);
        Assert.Empty(s.AiWeapons);
    }
}
