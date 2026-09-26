using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using Xunit;

namespace CSVM.Tests;

/// <summary>The cross-mission state log's bookkeeping half: chapter scoping, the escalate-only
/// merge, and the backwards walk's cut on the capturing story position. Capturing from and
/// applying to a live world needs a built chapter, and is the <c>campaign-persistence</c> engine
/// suite's business.</summary>
public class CampaignPersistLogTests
{
    [Fact]
    public void StateIsScopedToItsChapter()
    {
        var log = new CampaignPersistLog();

        log.Merge(6, 0, new[] { Destroyed(412, "susp_bridge") });

        Assert.Single(log.For(6));
        Assert.Empty(log.For(1));
        Assert.Equal(1, log.Count);
    }

    /// <summary>A later mission's capture may only make a node worse. Healing one would let a
    /// replay undo what an earlier mission wrecked, which the original's backwards walk cannot
    /// do.</summary>
    [Fact]
    public void MergeEscalatesDamageAndNeverHeals()
    {
        var log = new CampaignPersistLog();

        log.Merge(1, 6, new[] { new PersistedObject(7, "ftank01", "ftank01", false, 40f) });
        log.Merge(1, 6, new[] { new PersistedObject(7, "ftank01", "ftank01", false, 90f) });
        Assert.Equal(40f, Assert.Single(log.For(1)).Health);

        log.Merge(1, 6, new[] { new PersistedObject(7, "ftank01", "ftank01", false, 10f) });
        Assert.Equal(10f, Assert.Single(log.For(1)).Health);

        log.Merge(1, 6, new[] { Destroyed(7, "ftank01") });
        Assert.True(Assert.Single(log.For(1)).Destroyed);

        log.Merge(1, 6, new[] { new PersistedObject(7, "ftank01", "ftank01", false, 90f) });
        Assert.True(Assert.Single(log.For(1)).Destroyed);
    }

    /// <summary>The engine's backwards walk stops at the most recent EARLIER mission of the
    /// chapter, so a mission never opens on its own wreckage: re-flying the chapter's first
    /// mission finds no carrier at all.</summary>
    [Fact]
    public void AMissionNeverOpensOnItsOwnCapture()
    {
        var log = new CampaignPersistLog();
        log.Merge(1, 6, new[] { Destroyed(2782, "aagun32") });
        log.Merge(1, 8, new[] { Destroyed(1950, "g_tower2") });

        Assert.Empty(log.Through(1, null));
        Assert.Empty(log.Through(1, 5));
        Assert.Single(log.Through(1, 6));
        Assert.Equal(2, log.Through(1, 8).Count);
        Assert.Equal(2, log.For(1).Count);
    }

    /// <summary>A log written before the capturing position was stored belongs to whichever
    /// earlier mission wrote it, so it carries wherever an earlier mission exists and nowhere
    /// else.</summary>
    [Fact]
    public void AStoredStateWithNoPositionCarriesOnlyWhereAnEarlierMissionExists()
    {
        var log = new CampaignPersistLog();
        log.Merge(1, -1, new[] { Destroyed(2782, "aagun32") });

        Assert.Empty(log.Through(1, null));
        Assert.Single(log.Through(1, 6));
    }

    /// <summary>Only a won mission writes the original's world-state carrier, so a lost or
    /// abandoned attempt leaves the chapter as the last won mission left it.</summary>
    [Fact]
    public void OnlyAWonMissionCommitsItsCapture()
    {
        Assert.True(CampaignPersistLog.CommitsOn(MissionOutcome.Won));
        Assert.False(CampaignPersistLog.CommitsOn(MissionOutcome.Lost));
        Assert.False(CampaignPersistLog.CommitsOn(MissionOutcome.None));
    }

    [Fact]
    public void ResetReplacesTheWholeLog()
    {
        var log = new CampaignPersistLog();
        log.Merge(6, 0, new[] { Destroyed(412, "susp_bridge") });

        log.Reset(new System.Collections.Generic.Dictionary<int, System.Collections.Generic.IReadOnlyList<PersistedObject>>
        {
            [1] = new[] { Destroyed(7, "ftank01") with { Seq = 6 } },
        });

        Assert.Empty(log.For(6));
        Assert.Single(log.For(1));
        Assert.Single(log.Through(1, 6));
    }

    private static PersistedObject Destroyed(int node, string def) => new(node, def, def, true, 0f);
}
