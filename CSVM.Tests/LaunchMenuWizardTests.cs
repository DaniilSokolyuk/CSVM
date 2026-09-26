using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Instant Action wizard's own pure static surface: the wave
/// editor's Militia/Aircraft/Skill rosters and its own build step. LaunchMenu itself is
/// engine-bound and untestable directly, so these are the facts standing behind its wave editor,
/// the same role SessionSpecMenuTests plays for the Mode/Environment/MissionType screens.
/// </summary>
public class LaunchMenuWizardTests
{
    /// <summary>The thirteen militias, in the langui dropdown order (3670), docs/formats/instant-action.md
    /// "The thirteen militias and their aircraft".</summary>
    [Fact]
    public void TheThirteenMilitiasExistInLanguiOrder()
    {
        Assert.Equal(
            new[]
            {
                "Black Hat", "Black Swan", "Blake Aviation", "British", "Fortune Hunter",
                "Hollywood Knight", "Hughes Aviation", "Medusa", "Russian", "Sacred Trust",
                "German", "Studio Security", "Broadway Bomber",
            },
            UI.Screens.LaunchMenu.MilitiaNames());
    }

    /// <summary>The `.BM` pattern-coverage reading (decision 7), not vehicle.json's narrower
    /// paint_pattern one, Fortune Hunter covers all eleven airframes and Sacred Trust covers the
    /// Warhawk, both of which the def-based reading would get wrong. Each roster is in the langui
    /// 3700 order, which is FUN_00410420's mask bit order: a militia never reorders the dropdown,
    /// it only filters it.</summary>
    [Theory]
    [InlineData("Black Hat", new[] { "Autogyro", "Brigand", "Warhawk" })]
    [InlineData("Black Swan", new[] { "Fury" })]
    [InlineData("Sacred Trust", new[] { "Hellhound", "Warhawk" })]
    [InlineData("Broadway Bomber", new[] { "Peacemaker" })]
    public void MilitiaAircraftCoverageMatchesTheDecodedTable(string militia, string[] expected) =>
        Assert.Equal(expected, UI.Screens.LaunchMenu.AircraftFor(militia));

    /// <summary>The eleven airframes in the langui 3700 order (docs/formats/instant-action.md
    /// "Option strings"), the order the original stores an aircraft as an index into. Names are
    /// ia.json's singular vocabulary, so 3700's plural "Hoplites" reads "Autogyro" here.</summary>
    [Fact]
    public void TheElevenAirframesExistInLanguiOrder() =>
        Assert.Equal(
            new[]
            {
                "Autogyro", "Hellhound", "Balmoral", "Bloodhawk", "Brigand", "Devastator",
                "Firebrand", "Fury", "Kestrel", "Peacemaker", "Warhawk",
            },
            UI.Screens.LaunchMenu.PlaneNames());

    /// <summary>Fortune Hunter, the player's own militia, is legal as an enemy militia in the
    /// original's list and is never filtered out here (trap c), it covers all eleven airframes,
    /// the same roster the Plane screen itself offers.</summary>
    [Fact]
    public void FortuneHunterCoversAllElevenAirframes() =>
        Assert.Equal(11, UI.Screens.LaunchMenu.AircraftFor("Fortune Hunter").Length);

    [Fact]
    public void AnUnrecognisedMilitiaThrows() =>
        Assert.Throws<System.ArgumentException>(() => UI.Screens.LaunchMenu.AircraftFor("Not A Militia"));

    [Fact]
    public void TheThreeSkillsExistInLanguiOrder() =>
        Assert.Equal(new[] { "novice", "veteran", "ace" }, UI.Screens.LaunchMenu.SkillKeys());

    /// <summary>The wave editor's own build step: a configured slot (count > 0) becomes a real
    /// InstantActionWave off the militia/aircraft/skill cursors; an unconfigured one (count 0)
    /// is <see cref="InstantAction.EmptyWave"/> regardless of what those cursors are sitting on,
    /// they are not "configured" until a pilot actually raises the count.</summary>
    [Fact]
    public void WaveForBuildsAConfiguredSlot()
    {
        var wave = UI.Screens.LaunchMenu.WaveFor(count: 5, militiaIndex: 0, aircraftIndex: 0, skillIndex: 2);
        Assert.Equal(5, wave.NumEnemies);
        Assert.Equal("Autogyro", wave.EnemyPlane); // Black Hat's first aircraft in the 3700 order
        Assert.Equal("ace", wave.EnemySkill);
        Assert.Equal("Black Hat Autogyro", wave.EnemyName);
        Assert.Equal(0, wave.EnemyAccentId); // Black Hat's own accent, the militia switch's write
    }

    /// <summary>The thirteen militia accent ids the setup screen writes into a wave's record, in
    /// the same langui 3670 order (docs/formats/instant-action.md, "A militia also picks the
    /// wave's voice"). Fortune Hunter's 12 is the wingman range's base, re-rolled per aircraft at
    /// spawn; a wave's voice is otherwise fixed by its militia alone.</summary>
    [Fact]
    public void EachMilitiaCarriesItsDecodedWaveAccent() =>
        Assert.Equal(
            new[] { 0, 1, 2, 3, 12, 7, 10, 8, 5, 9, 6, 10, 4 },
            UI.Menu.InstantActionFeature.Militias.Select(m => m.AccentId));

    [Fact]
    public void WaveForAnUnconfiguredSlotIsTheEmptyWaveWhateverTheCursorsAre()
    {
        Assert.Equal(InstantAction.EmptyWave, UI.Screens.LaunchMenu.WaveFor(0, militiaIndex: 4, aircraftIndex: 3, skillIndex: 1));
        Assert.Equal(InstantAction.EmptyWave, UI.Screens.LaunchMenu.WaveFor(-1, militiaIndex: 0, aircraftIndex: 0, skillIndex: 0));
    }

    /// <summary>The Ammo Selection list's pylon rows: the stock Bloodhawk's three, then the same
    /// airframe bought one hardpoint a wing, whose skipped fill-order entry gets no row. The
    /// original draws no field for a hardpoint the aeroplane does not carry
    /// (docs/formats/campaign-screens.md, the ammo screen).</summary>
    [Fact]
    public void TheAmmoListOffersOnlyThePylonsTheAeroplaneHangs()
    {
        var stock = StockLoadouts.Load(
            Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json")).ForModel("player_bhawk");
        Assert.NotNull(stock);
        Assert.Equal(new[] { 1, 5, 2 }, UI.Screens.LaunchMenu.AmmoPylons(stock));

        var built = CustomPlaneBuild.LoadoutFor(
            new CustomPlaneDef { Name = "Blue Streak", Airframe = 3, Engine = 1, LeftHardpoints = 1, RightHardpoints = 1 },
            stock!);
        Assert.Equal(new[] { 1, 2 }, UI.Screens.LaunchMenu.AmmoPylons(built));
        Assert.Empty(UI.Screens.LaunchMenu.AmmoPylons(null));
    }
}
