using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The scrapbook's results block: the outcome line, the four drawn rows and the Best to
/// Date / Most Recent tabs, reproduced against <c>Campaign Mission End screen CM01.png</c> and
/// <c>Campaign Scrapbook CM02 Mission select after another Mission.png</c>
/// (<c>docs/org/debrief.md#the-screen-is-the-scrapbook</c>).</summary>
public class CampaignScrapbookResultsTests
{
    [Fact]
    public void Cm01MostRecentReproducesTheShippedRow()
    {
        var result = new MissionResult
        {
            Seq = 0,
            Latest = Run(mask: 1, timeMs: 215000, shots: 25, hits: 4, money: 0, kills: (8, 3)), // Kestrel
        };

        var rows = CampaignScrapbookResults.Rows(result, bestToDate: false);

        Assert.True(CampaignScrapbookResults.Won(result, bestToDate: false));
        Assert.Contains(rows, l => l.Text == "Mission Completed");
        Assert.Contains(rows, l => l.Text == "03:35");
        Assert.Contains(rows, l => l.Text == "16%");
        Assert.Contains(rows, l => l.Text == "$0");
        Assert.Equal(3, CampaignScrapbookResults.PlanesDowned(result, bestToDate: false));
    }

    [Fact]
    public void Cm02MostRecentReproducesTheShippedRow()
    {
        var result = new MissionResult
        {
            Seq = 1,
            // 2 Balmoral (i 2) and 3 Peacemaker (i 9) plain, a starred 1 Peacemaker ace: 6 total.
            Latest = Run(mask: 1, timeMs: 199000, shots: 50, hits: 9, money: 500,
                kills: (2, 2), kills2: (9, 3), aceKills: (9, 1)),
        };

        var rows = CampaignScrapbookResults.Rows(result, bestToDate: false);

        Assert.Contains(rows, l => l.Text == "03:19");
        Assert.Contains(rows, l => l.Text == "18%");
        Assert.Equal(6, CampaignScrapbookResults.PlanesDowned(result, bestToDate: false));
    }

    [Fact]
    public void GunHitRatioTruncatesRatherThanRounds()
    {
        // 5/33 = 15.15...%, which rounds to 15 either way; 5/32 = 15.625%, which would round up
        // to 16 but must truncate to 15, matching the original's ftol call rather than Math.Round.
        var result = new MissionResult { Latest = Run(mask: 1, timeMs: 0, shots: 32, hits: 5, money: 0) };

        var rows = CampaignScrapbookResults.Rows(result, bestToDate: false);

        Assert.Contains(rows, l => l.Text == "15%");
    }

    [Fact]
    public void RocketsExpendedIsNotAmongTheDrawnRows()
    {
        var result = new MissionResult { Latest = Run(mask: 1, timeMs: 0, shots: 0, hits: 0, money: 0) };

        var rows = CampaignScrapbookResults.Rows(result, bestToDate: false);

        Assert.DoesNotContain(rows, l => l.Text.Contains("Rocket"));
        Assert.Equal(10, rows.Count); // outcome + heading + four (title, value) pairs
    }

    /// <summary>Both tabs read their own half's mask. The merged half is written only by an
    /// attempt that won, so a merged record carries the primary bit and Best to Date reads
    /// Mission Completed.</summary>
    [Fact]
    public void BothTabsReadTheirOwnHalfsMask()
    {
        var result = new MissionResult
        {
            Latest = Run(mask: 1, timeMs: 1000, shots: 1, hits: 1, money: 0),
            Best = Run(mask: 1, timeMs: 1000, shots: 1, hits: 1, money: 0),
        };

        Assert.True(CampaignScrapbookResults.Won(result, bestToDate: false));
        Assert.True(CampaignScrapbookResults.Won(result, bestToDate: true));

        var rows = CampaignScrapbookResults.Rows(result, bestToDate: true);
        Assert.Contains(rows, l => l.Text == "Mission Completed");
    }

    /// <summary>A mission attempted but never completed: the merged half is all zero, so Best to
    /// Date reads Mission Failed while Most Recent reports the failed attempt.</summary>
    [Fact]
    public void BestToDateReadsFailedForANeverCompletedMission()
    {
        var result = new MissionResult
        {
            Latest = Run(mask: 2, timeMs: 1000, shots: 1, hits: 1, money: 0),
        };

        Assert.False(CampaignScrapbookResults.Won(result, bestToDate: false));
        Assert.False(CampaignScrapbookResults.Won(result, bestToDate: true));

        var rows = CampaignScrapbookResults.Rows(result, bestToDate: true);
        Assert.Contains(rows, l => l.Text == "Mission Failed");
    }

    [Fact]
    public void TabTitlesAreTheOriginalsWords()
    {
        Assert.Equal("Most Recent", CampaignScrapbookResults.TabTitle(bestToDate: false));
        Assert.Equal("Best to Date", CampaignScrapbookResults.TabTitle(bestToDate: true));
    }

    /// <summary>CM02: slot 0 <c>2 Balmoral</c>, slot 1 <c>3 Peacemaker</c>, slot 2 a starred
    /// <c>1 Peacemaker</c>, over 6. Balmoral is airframe index 2, Peacemaker index 9; the ace
    /// stamp's frame is 9 + 11 = 20.</summary>
    [Fact]
    public void Cm02StampsFillDenselyPlainThenAce()
    {
        var result = new MissionResult
        {
            Latest = Run(mask: 1, timeMs: 199000, shots: 50, hits: 9, money: 500,
                kills: (2, 2), kills2: (9, 3), aceKills: (9, 1)),
        };

        var stamps = CampaignScrapbookResults.Stamps(result, bestToDate: false);

        Assert.Equal(3, stamps.Count);
        Assert.Equal(new CampaignScrapbookResults.KillStamp(0, 2, 2), stamps[0]);
        Assert.Equal(new CampaignScrapbookResults.KillStamp(1, 9, 3), stamps[1]);
        Assert.Equal(new CampaignScrapbookResults.KillStamp(2, 20, 1), stamps[2]);
    }

    /// <summary>The same airframe filling two slots, once plain and once starred, must not be
    /// collapsed by a layout keyed uniquely on airframe.</summary>
    [Fact]
    public void SameAirframePlainAndStarredFillsTwoSlots()
    {
        var result = new MissionResult
        {
            Latest = Run(mask: 1, timeMs: 0, shots: 0, hits: 0, money: 0, kills: (9, 3), aceKills: (9, 1)),
        };

        var stamps = CampaignScrapbookResults.Stamps(result, bestToDate: false);

        Assert.Equal(2, stamps.Count);
        Assert.Equal(9, stamps[0].Frame);
        Assert.Equal(20, stamps[1].Frame);
    }

    [Fact]
    public void StampPicturesAndLabelsUseTheAuthoredSlotPositions()
    {
        var result = new MissionResult
        {
            Latest = Run(mask: 1, timeMs: 0, shots: 0, hits: 0, money: 0, kills: (2, 2), kills2: (9, 3)),
        };

        var pictures = CampaignScrapbookResults.StampPictures(result, bestToDate: false);
        var labels = CampaignScrapbookResults.StampLabels(result, bestToDate: false);

        Assert.Equal(2, pictures.Count);
        // Slot 0 (Balmoral, frame 2) sits at SB_KILL0, 560,109 -- NOT the leftmost on screen:
        // SB_KILL1 (467,93) is left of and above it, so a reading-order layout would swap these.
        Assert.Equal(560f, pictures[0].X);
        Assert.Equal(109f, pictures[0].Y);
        Assert.Equal(2, pictures[0].Frame);
        Assert.Equal(467f, pictures[1].X);
        Assert.Equal(93f, pictures[1].Y);

        Assert.Equal(2, labels.Count);
        Assert.Equal("2", labels[0].Text);
        Assert.Equal(588f, labels[0].X);
        Assert.Equal(128f, labels[0].Y);
        Assert.Equal("3", labels[1].Text);
    }

    [Fact]
    public void StampsStopAtElevenSlots()
    {
        var run = new MissionRun { CompletedMask = 1 };
        for (int i = 0; i < CampaignProgression.AirframeCount; i++)
        {
            run.Kills[i] = 1;
            run.AceKills[i] = 1;
        }

        var result = new MissionResult { Latest = run };
        var stamps = CampaignScrapbookResults.Stamps(result, bestToDate: false);

        Assert.Equal(CampaignProgression.AirframeCount, stamps.Count);
    }

    private static MissionRun Run(
        int mask, int timeMs, int shots, int hits, int money,
        (int Index, int Count)? kills = null, (int Index, int Count)? kills2 = null,
        (int Index, int Count)? aceKills = null)
    {
        var run = new MissionRun
        {
            CompletedMask = mask,
            TimeMs = timeMs,
            Shots = shots,
            Hits = hits,
            Money = money,
        };
        if (kills is { } k)
        {
            run.Kills[k.Index] = k.Count;
        }

        if (kills2 is { } k2)
        {
            run.Kills[k2.Index] = k2.Count;
        }

        if (aceKills is { } a)
        {
            run.AceKills[a.Index] = a.Count;
        }

        return run;
    }
}
