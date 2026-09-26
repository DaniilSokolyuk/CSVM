using System;
using System.Linq;
using CSVM.UI.Menu;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Instant Action Table of Contents: the 19 preset scenarios decoded from `0x0061b090`
/// (docs/formats/instant-action.md, "Table of Contents presets") and the name-to-cursor resolution
/// that applies one to the setup screens. The table is hand-transcribed, so these are the tests
/// that catch a typo in it, every name has to resolve against a live roster, and a wave's aircraft
/// has to be one its own militia actually flies.
/// </summary>
public class InstantActionPresetsTests
{
    private const int WaveSlots = 4;

    /// <summary>The nineteen names, langui 3600 to 3618 in that order, the order the contents
    /// list shows and the order a preset's own stored index means.</summary>
    [Fact]
    public void TheNineteenPresetsExistInLanguiOrder() =>
        Assert.Equal(
            new[]
            {
                "Girl Trouble", "Sour Grapes", "Me and My Big Mouth", "The Angry Luau", "Hat Trick",
                "Seaside Show-Off", "Two to Tango", "Death of the Gemini", "Hares and Tortoises",
                "Swan's Gauntlet", "Aloha, Ace!", "Black Hats and Hoplites", "From Russia with Hate",
                "Honor, Hollywood Style", "Manhattan Tea Party", "Let's You, Me, and Him Fight",
                "The Longest New York Minute", "Rocky Mountain Hijinks", "The Hollywood Brawl",
            },
            InstantActionPresets.All.Select(p => p.Name));

    /// <summary>Every name in the table resolves against a live roster: the environment, the
    /// mission type (against that environment's own filtered list), the player and wingman
    /// aircraft, and each wave's militia, aircraft and skill. A transcription typo anywhere in the
    /// 19 rows throws here rather than flying the wrong enemy.</summary>
    [Fact]
    public void EveryPresetResolvesAgainstTheLiveRosters()
    {
        for (int i = 0; i < InstantActionPresets.All.Count; i++)
        {
            var applied = InstantActionPresets.Resolve(i, WaveSlots);
            Assert.Equal(WaveSlots, applied.Waves.Length);
        }
    }

    /// <summary>All four mission types and all seven environments appear, and no environment is
    /// tied to one mission type, the decoded table's own statement that the presets are not a
    /// per-environment set.</summary>
    [Fact]
    public void ThePresetsCoverEveryMissionTypeAndEveryEnvironment()
    {
        Assert.Equal(4, InstantActionPresets.All.Select(p => p.MissionType).Distinct().Count());
        Assert.Equal(7, InstantActionPresets.All.Select(p => p.Environment).Distinct().Count());
    }

    /// <summary>Dogfighting an ace is a solo duel in the data as well as in the UI: the five ace
    /// presets carry no waves and no wingmen, which is the same rule the setup script's own
    /// `0 == WT` branch enforces on the screen.</summary>
    [Fact]
    public void TheAcePresetsCarryNoWavesAndNoWingmen()
    {
        var aces = InstantActionPresets.All.Where(p => p.MissionType == "dogfight_ace").ToList();
        Assert.Equal(5, aces.Count);
        Assert.All(aces, p =>
        {
            Assert.Empty(p.Waves);
            Assert.Equal(0, p.NumWingmen);
        });
    }

    /// <summary>A wingman aircraft is reported exactly when there are wingmen to fly it. At 0 the
    /// screen hides the field and the decode reports no value, so the resolver returns null rather
    /// than moving that cursor to something invented.</summary>
    [Fact]
    public void WingmanAircraftIsPresentExactlyWhenThereAreWingmen()
    {
        for (int i = 0; i < InstantActionPresets.All.Count; i++)
        {
            var preset = InstantActionPresets.All[i];
            var applied = InstantActionPresets.Resolve(i, WaveSlots);
            Assert.Equal(preset.NumWingmen > 0, preset.WingmanPlane is not null);
            Assert.Equal(preset.NumWingmen > 0, applied.WingmanPlaneIndex is not null);
        }
    }

    /// <summary>The unused wave slots take `FUN_004102c0`'s own sentinel substitution, militia 4
    /// (Fortune Hunter), aircraft 5 (Devastator), skill 1 (veteran), at 0 enemies. It never
    /// reaches a flown mission, but it is what a pilot inherits on raising an empty wave's
    /// count.</summary>
    [Fact]
    public void UnusedWaveSlotsTakeTheDecodedSubstitution()
    {
        // "Girl Trouble" uses two of the four slots.
        var applied = InstantActionPresets.Resolve(0, WaveSlots);
        foreach (var wave in applied.Waves.Skip(2))
        {
            Assert.Equal(0, wave.Count);
            Assert.Equal(Array.IndexOf(LaunchMenu.MilitiaNames(), "Fortune Hunter"), wave.MilitiaIndex);
            Assert.Equal(Array.IndexOf(LaunchMenu.AircraftFor("Fortune Hunter"), "Devastator"), wave.AircraftIndex);
            Assert.Equal(Array.IndexOf(LaunchMenu.SkillKeys(), "veteran"), wave.SkillIndex);
        }
    }

    /// <summary>One preset resolved end to end, against the rosters' own decoded orders: Girl
    /// Trouble is a Sky Haven squadron in a Firebrand with two Peacemaker wingmen, against four
    /// veteran Medusa Kestrels then two ace Black Swan Furys.</summary>
    [Fact]
    public void GirlTroubleResolvesToItsDecodedCursors()
    {
        var applied = InstantActionPresets.Resolve(0, WaveSlots);
        Assert.Equal(5, applied.EnvironmentIndex);   // Sky Haven, sixth in the decoded dropdown
        Assert.Equal(1, applied.MissionTypeIndex);   // dogfight_squadron, C4 filtering nothing
        Assert.Equal(6, applied.PlayerPlaneIndex);   // Firebrand, in the 3700 order
        Assert.Equal(2, applied.NumWingmen);
        Assert.Equal(9, applied.WingmanPlaneIndex);  // Peacemaker
        Assert.Equal(new InstantActionPresets.AppliedWave(4, 7, 1, 1), applied.Waves[0]);
        Assert.Equal(new InstantActionPresets.AppliedWave(2, 1, 0, 2), applied.Waves[1]);
    }

    /// <summary>The mission-type cursor indexes the roster the environment actually offers, not the
    /// four-row master list. "The clouds" (C2B) bars Stunt Flying, so a zeppelin run there is the
    /// third row, not the fourth, the one case where the filter shifts a preset's own index.</summary>
    [Fact]
    public void APresetOnTheCloudsIndexesTheFilteredMissionTypeRoster()
    {
        Assert.Equal(3, LaunchMenu.MissionTypeKeysFor("C2B").Length);
        var gemini = InstantActionPresets.Resolve(7, WaveSlots); // Death of the Gemini
        Assert.Equal("the clouds", InstantActionPresets.All[7].Environment);
        Assert.Equal("zeppelin_run", InstantActionPresets.All[7].MissionType);
        Assert.Equal(2, gemini.MissionTypeIndex);
    }

    [Fact]
    public void AnOutOfRangePresetThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InstantActionPresets.Resolve(-1, WaveSlots));
        Assert.Throws<ArgumentOutOfRangeException>(() => InstantActionPresets.Resolve(19, WaveSlots));
    }
}
