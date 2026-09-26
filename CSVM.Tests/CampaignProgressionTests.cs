using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Session.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The progression rules over a profile: the two-half mission record and its best-of
/// merge, the monotonic position that only a completed primary objective raises, the replay rule
/// Previous Missions flies under, and the five aircraft awards granted once per profile.</summary>
[Trait("Tier", "Quick")]
public class CampaignProgressionTests
{
    [Fact]
    public void CompletingTheFirstMissionAdvancesOneStep()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var recorded = CampaignProgression.Record(profile, Attempt(0, mask: 1, timeMs: 45000));

        Assert.True(recorded.PrimaryCompleted);
        Assert.True(recorded.Advanced);
        Assert.Equal(1, profile.MissionsCompleted);
        Assert.Equal(1, CampaignProgression.NextMissionSeq(profile));
    }

    /// <summary>Bit 0 gates everything: an attempt that completes secondary objectives only records
    /// its statistics as the latest attempt, merges nothing, and leaves the position alone.</summary>
    [Fact]
    public void AnAttemptWithoutThePrimaryObjectiveRecordsStatisticsAndNothingElse()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var recorded = CampaignProgression.Record(profile, Attempt(0, mask: 4, timeMs: 61000));

        Assert.False(recorded.PrimaryCompleted);
        Assert.False(recorded.Advanced);
        Assert.Equal(0, profile.MissionsCompleted);
        var result = Assert.Single(profile.MissionResults);
        Assert.Equal(4, result.Latest.CompletedMask);
        Assert.Equal(0, result.Best.CompletedMask);
        Assert.Empty(CampaignProgression.CompletedSeqs(profile));
    }

    /// <summary>Previous Missions: replaying a finished mission records the attempt and merges the
    /// best-of, but the position cannot move for a mission at or below the progress already
    /// stored.</summary>
    [Fact]
    public void ReplayingAFinishedMissionNeverAdvances()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0, mask: 1, timeMs: 45000));
        CampaignProgression.Record(profile, Attempt(1, mask: 1, timeMs: 50000));

        var replay = CampaignProgression.Record(profile, Attempt(0, mask: 3, timeMs: 40000));

        Assert.True(replay.PrimaryCompleted);
        Assert.False(replay.Advanced);
        Assert.Equal(2, profile.MissionsCompleted);
        Assert.True(CampaignProgression.CanFly(profile, 0));
        Assert.True(CampaignProgression.CanFly(profile, 2));
        Assert.False(CampaignProgression.CanFly(profile, 3));
    }

    [Fact]
    public void TheBestOfMergeKeepsTheBetterHalfOfEachField()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, new MissionAttempt(0, 1, 45000, 120, 30, 5, "Gypsy Magic"));

        CampaignProgression.Record(profile, new MissionAttempt(0, 5, 61000, 40, 30, 3, "The Knave"));

        var result = Assert.Single(profile.MissionResults);
        Assert.Equal(5, result.Best.CompletedMask);
        Assert.Equal(45000, result.Best.TimeMs);
        Assert.Equal(40, result.Best.Shots);
        Assert.Equal(30, result.Best.Hits);

        // Mission ordinal 1 pays on bit 12 and neither run set it, so the money field accumulates
        // nothing. What it accumulates when a reward IS owed is TheCashRewardIsPaidOncePerProfile.
        Assert.Equal(0, result.Best.Money);
        Assert.Equal(3, result.Best.Airframe);
        Assert.Equal("The Knave", result.Best.PlaneName);
        Assert.Equal(61000, result.Latest.TimeMs);
    }

    /// <summary>The plane is replaced by an attempt beating the best single attempt's own mask,
    /// which the OR-ed mask cannot answer: it only grows, so by the third win it holds four bits
    /// and would refuse the four-bit attempt the original accepts.</summary>
    [Fact]
    public void ThePlaneFollowsTheBestAttemptsMaskNotTheOr()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, new MissionAttempt(0, 0b0011, 45000, 10, 5, 5, "Gypsy Magic"));
        CampaignProgression.Record(profile, new MissionAttempt(0, 0b1101, 46000, 10, 5, 3, "The Knave"));

        CampaignProgression.Record(profile, new MissionAttempt(0, 0b1110001, 47000, 10, 5, 7, "Sky Ranger"));

        var result = Assert.Single(profile.MissionResults);
        Assert.Equal(0b1111111, result.Best.CompletedMask); // the OR, already four bits before this run
        Assert.Equal(0b1110001, result.Best.BestAttemptMask);
        Assert.Equal(7, result.Best.Airframe);
        Assert.Equal("Sky Ranger", result.Best.PlaneName);
    }

    /// <summary>The award is marked per airframe, so the second flight of the same mission grants
    /// nothing however it is reached.</summary>
    [Fact]
    public void AnAircraftAwardIsGrantedOncePerProfile()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0, mask: 1, timeMs: 45000));

        var first = CampaignProgression.Record(profile, Attempt(1, mask: 3, timeMs: 50000));
        var second = CampaignProgression.Record(profile, Attempt(1, mask: 3, timeMs: 48000));

        Assert.Equal(new[] { 2 }, first.AwardedAirframes.ToArray());
        Assert.Empty(second.AwardedAirframes);
        Assert.Equal(new[] { 2 }, profile.GrantedAircraft.ToArray());
        var awarded = Assert.Single(profile.Planes, p => p.Name == "Jumping Jane");
        Assert.True(awarded.Special);
        Assert.Equal(2, awarded.Airframe);
    }

    /// <summary>The award's own objective bit gates it: mission 2 pays on bit 1, so completing the
    /// mission without that objective grants nothing and leaves the award still owed.</summary>
    [Fact]
    public void AnAwardWhoseObjectiveWasNotCompletedIsNotGranted()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0, mask: 1, timeMs: 45000));

        var recorded = CampaignProgression.Record(profile, Attempt(1, mask: 1, timeMs: 50000));

        Assert.Empty(recorded.AwardedAirframes);
        Assert.Empty(profile.GrantedAircraft);
        Assert.Equal(new[] { 2, 7, 13, 17, 19 },
            CampaignProgression.MissionRewards.Where(r => r.AwardsAircraft).Select(r => r.Ordinal).ToArray());
    }

    [Fact]
    public void TheCampaignEndsAfterTwentyFourMissions()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        for (int seq = 0; seq < 24; seq++)
        {
            CampaignProgression.Record(profile, Attempt(seq, mask: 1, timeMs: 45000));
        }

        Assert.Equal(24, profile.MissionsCompleted);
        Assert.True(CampaignProgression.Complete(profile));
        Assert.Equal(23, CampaignProgression.NextMissionSeq(profile));
        Assert.False(CampaignProgression.CanFly(profile, 24));
    }

    /// <summary>An award grants a whole aircraft, not just an ownership row: mission 7 pays the
    /// Blue Streak, whose template carries engine 4 (the nitrous tier), one hardpoint per wing,
    /// four armour units in every zone and a twinned forty and thirty calibre.</summary>
    [Fact]
    public void TheBlueStreakAwardCarriesItsOwnBuildWithNitrous()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var recorded = CampaignProgression.Record(profile, Attempt(6, mask: 3, timeMs: 50000));

        var build = Assert.Single(recorded.AwardedBuilds);
        Assert.Equal("Blue Streak", build.Name);
        Assert.Equal(3, build.Airframe);
        Assert.Equal(4, build.Engine);
        Assert.True(CustomPlaneBuild.HasNitrous(build));
        Assert.Equal(1, build.LeftHardpoints);
        Assert.Equal(1, build.RightHardpoints);
        Assert.Equal(new[] { 4, 4, 4, 4 },
            new[] { build.ArmourNose, build.ArmourTail, build.ArmourLeftWing, build.ArmourRightWing });
        Assert.Equal(new GunChoice(1, true), build.Guns[0]);
        Assert.Equal(new GunChoice(0, true), build.Guns[1]);
        Assert.True(build.Guns[2].IsEmpty);
        Assert.True(build.Guns[3].IsEmpty);
    }

    /// <summary>The nitrous rides on the Blue Streak's own build and on nothing else: the other
    /// four templates carry engine 1, and an airframe the table does not award has no build at
    /// all, so a stock Bloodhawk is unaffected by the grant.</summary>
    [Fact]
    public void OnlyTheBlueStreakTemplateCarriesNitrous()
    {
        foreach (var reward in CampaignProgression.MissionRewards.Where(r => r.AwardsAircraft))
        {
            var build = CampaignProgression.AwardBuild(reward.Airframe);
            Assert.NotNull(build);
            Assert.Equal(reward.Airframe == 3, CustomPlaneBuild.HasNitrous(build!));
        }

        Assert.Null(CampaignProgression.AwardBuild(5));
    }

    /// <summary>A profile granted an award before the build store was written to still flies the
    /// award's own fit: the ownership row alone resolves the template. A hangar-built plane and the
    /// two starters resolve nothing here and stay on the store's answer.</summary>
    [Fact]
    public void AnOwnedRewardAircraftResolvesItsTemplateWithNoBuildOnFile()
    {
        var owned = new OwnedPlane { Name = "Blue Streak", Airframe = 3, Special = true };

        var build = CampaignProgression.BuildForOwned(owned);

        Assert.NotNull(build);
        Assert.Equal("Blue Streak", build!.Name);
        Assert.True(CustomPlaneBuild.HasNitrous(build));
        Assert.Null(CampaignProgression.BuildForOwned(
            new OwnedPlane { Name = "Gypsy Magic", Airframe = 5 }));
        Assert.Null(CampaignProgression.BuildForOwned(
            new OwnedPlane { Name = "Ruthless Saber", Airframe = 3 }));
    }

    /// <summary>A mission that grants nothing hands back no builds, so a caller writing them to the
    /// build store has nothing to write on an ordinary mission end.</summary>
    [Fact]
    public void AMissionWithNoAwardHandsBackNoBuilds()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var recorded = CampaignProgression.Record(profile, Attempt(0, mask: 1, timeMs: 45000));

        Assert.Empty(recorded.AwardedBuilds);
    }

    /// <summary>BL-622/B14: the offer is on a counter, not on a single failure. Three failures pass
    /// in silence and the fourth raises it, and the counter is the mission's own, so a failure at
    /// another mission does not move it (docs/org/debrief.md, "The four-attempt skip offer").</summary>
    [Fact]
    public void TheSkipOfferComesOnTheFourthFailedAttemptAndNotTheThird()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var failures = new bool[4];
        for (int i = 0; i < 4; i++)
        {
            CampaignProgression.Record(profile, Attempt(1, mask: 0, timeMs: 20000));
            failures[i] = CampaignProgression.Record(profile, Attempt(0, mask: 4, timeMs: 61000)).SkipOffered;
        }

        Assert.Equal(new[] { false, false, false, true }, failures);
        Assert.Equal(4, CampaignProgression.ResultOf(profile, 0)!.Attempts);
        Assert.Equal(0, profile.MissionsCompleted);
    }

    /// <summary>Declining is doing nothing: the offer leaves no mark of its own, so the campaign
    /// stands where the fourth failure left it and the fifth failure counts 5 rather than starting
    /// the count again.</summary>
    [Fact]
    public void DecliningTheSkipLeavesTheCampaignWhereItWas()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        for (int i = 0; i < 4; i++)
        {
            CampaignProgression.Record(profile, Attempt(0, mask: 0, timeMs: 61000));
        }

        var fifth = CampaignProgression.Record(profile, Attempt(0, mask: 0, timeMs: 61000));

        Assert.False(fifth.SkipOffered);
        Assert.Equal(5, CampaignProgression.ResultOf(profile, 0)!.Attempts);
        Assert.Equal(0, profile.MissionsCompleted);
        Assert.Equal(0, CampaignProgression.ResultOf(profile, 0)!.Best.CompletedMask);
        Assert.Empty(CampaignProgression.CompletedSeqs(profile));
    }

    /// <summary>Accepting is the original's synthetic win: the win flag is set and the debrief runs
    /// again, so the position advances, the best-of merges and the failed attempt's world state is
    /// committed the way a won mission's is.</summary>
    [Fact]
    public void AcceptingTheSkipAdvancesTheCampaignTheWayAWinDoes()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var attempt = Attempt(0, mask: 4, timeMs: 61000);
        for (int i = 0; i < 4; i++)
        {
            CampaignProgression.Record(profile, attempt);
        }

        var skipped = CampaignProgression.AcceptSkip(profile, attempt, chapter: 1, capture: new[]
        {
            new PersistedObject(12, "aa_gun", "aa_gun_03", true, 0f, 0),
        });

        Assert.True(skipped.PrimaryCompleted);
        Assert.True(skipped.Advanced);
        Assert.Equal(1, profile.MissionsCompleted);
        var result = CampaignProgression.ResultOf(profile, 0)!;
        Assert.Equal(5, result.Latest.CompletedMask);
        Assert.Equal(5, result.Best.CompletedMask);
        Assert.Equal(4, result.Attempts);
        Assert.Equal(1, profile.PersistLog.Count);
        Assert.Equal(new[] { 0 }, CampaignProgression.CompletedSeqs(profile).ToArray());
    }

    /// <summary>The counter moves only while the mission stands uncompleted, so a mission already
    /// finished never offers a skip however many replays are lost.</summary>
    [Fact]
    public void ACompletedMissionNeverOffersASkipHoweverOftenItIsLost()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        CampaignProgression.Record(profile, Attempt(0, mask: 1, timeMs: 45000));

        for (int i = 0; i < 8; i++)
        {
            Assert.False(CampaignProgression.Record(profile, Attempt(0, mask: 0, timeMs: 61000)).SkipOffered);
        }

        Assert.Equal(0, CampaignProgression.ResultOf(profile, 0)!.Attempts);
        Assert.Equal(1, profile.MissionsCompleted);
    }

    /// <summary>The six paying records, their bits and their amounts, as the table at
    /// <c>0x0061ae80</c> holds them. $140,900 is the campaign's whole cash income.</summary>
    [Fact]
    public void TheRewardTableIsTheSixPayingRecordsTheOriginalHolds()
    {
        var paying = CampaignProgression.MissionRewards.Where(r => r.Cash > 0).ToArray();

        Assert.Equal(new[] { 1, 5, 6, 12, 19, 24 }, paying.Select(r => r.Ordinal).ToArray());
        Assert.Equal(new[] { 12, 1, 3, 1, 1, 1 }, paying.Select(r => r.ObjectiveBit).ToArray());
        Assert.Equal(new[] { 900, 20_000, 5_000, 10_000, 5_000, 100_000 }, paying.Select(r => r.Cash).ToArray());
        Assert.Equal(140_900, CampaignProgression.MissionRewards.Sum(r => r.Cash));
    }

    /// <summary>Mission ordinal 1 pays $900 on bit 12 and nothing on any other bit, so the payout
    /// follows the objective and not the mere fact of completing the mission.</summary>
    [Fact]
    public void TheCashFollowsTheRecordsOwnObjectiveBit()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var without = CampaignProgression.Record(profile, Attempt(0, mask: 1, timeMs: 45000));

        Assert.Equal(0, without.MoneyPaid);
        Assert.Equal(0, profile.Funds);

        var with = CampaignProgression.Record(profile, Attempt(0, mask: 1 | (1 << 12), timeMs: 45000));

        Assert.Equal(900, with.MoneyPaid);
        Assert.Equal(900, profile.Funds);
    }

    /// <summary>Each bonus is taken once per profile, replays included: the gate is the mask as it
    /// stood before the run, so flying the same paying objective again banks nothing.</summary>
    [Fact]
    public void TheCashRewardIsPaidOncePerProfile()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        int bit12 = 1 | (1 << 12);

        var first = CampaignProgression.Record(profile, Attempt(0, mask: bit12, timeMs: 45000));
        var replay = CampaignProgression.Record(profile, Attempt(0, mask: bit12, timeMs: 40000));

        Assert.Equal(900, first.MoneyPaid);
        Assert.Equal(0, replay.MoneyPaid);
        Assert.Equal(900, profile.Funds);
        Assert.Equal(900, CampaignProgression.ResultOf(profile, 0)!.Best.Money);
        Assert.Equal(0, CampaignProgression.ResultOf(profile, 0)!.Latest.Money);
    }

    /// <summary>A lost run banks nothing however many paying objectives it met, since the whole
    /// reward pass sits behind the mission-won flag.</summary>
    [Fact]
    public void ALostRunBanksNothing()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var lost = CampaignProgression.Record(profile, Attempt(0, mask: 1 << 12, timeMs: 45000));

        Assert.False(lost.PrimaryCompleted);
        Assert.Equal(0, lost.MoneyPaid);
        Assert.Equal(0, profile.Funds);
    }

    /// <summary>Ordinal 19 is the one record that pays cash and grants an aircraft, which is why the
    /// two halves are one table: a single qualifying run takes both.</summary>
    [Fact]
    public void OrdinalNineteenPaysCashAndGrantsAnAircraft()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");

        var recorded = CampaignProgression.Record(profile, Attempt(18, mask: 3, timeMs: 45000));

        Assert.Equal(5_000, recorded.MoneyPaid);
        Assert.Equal(new[] { 10 }, recorded.AwardedAirframes.ToArray());
        Assert.Equal(5_000, profile.Funds);
    }

    /// <summary>The skip offer's Yes is a synthetic win, so it banks whatever the failed attempt's
    /// own mask qualifies for and nothing more.</summary>
    [Fact]
    public void TakingTheSkipOfferBanksWhatTheFailedMaskQualifiesFor()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var failed = Attempt(0, mask: 1 << 12, timeMs: 45000);

        var recorded = CampaignProgression.AcceptSkip(profile, failed, chapter: 1);

        Assert.True(recorded.PrimaryCompleted);
        Assert.Equal(900, recorded.MoneyPaid);
        Assert.Equal(900, profile.Funds);
    }

    private static MissionAttempt Attempt(int seq, int mask, int timeMs) =>
        new(seq, mask, timeMs, 100, 25, 5, "Gypsy Magic");
}
