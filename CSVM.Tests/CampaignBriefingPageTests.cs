using System.IO;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>The mission briefing screen: which state a mission resolves to, the objectives note it
/// fills in as the narration reaches its cue points, and the three buttons' routes.</summary>
public class CampaignBriefingPageTests
{
    [Fact]
    public void TheScreenOpensOnItsThreeButtons()
    {
        var page = Briefing(NewFlow(null, 0));

        Assert.Equal(3, page.RowCount);
        Assert.Equal("REPLAY BRIEFING", page.RowText(CampaignBriefingPage.ReplayRow));
        Assert.Equal("RETURN TO CABIN", page.RowText(CampaignBriefingPage.CabinRow));
        Assert.Equal("GO TO FLIGHT CHECK", page.RowText(CampaignBriefingPage.FlightCheckRow));
    }

    [Fact]
    public void ReturnToCabinGoesBackToTheCabinItWasOpenedFrom()
    {
        var flow = NewFlow(null, 0);
        Briefing(flow);

        flow.FocusRow(CampaignBriefingPage.CabinRow);
        flow.Accept();

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    [Fact]
    public void GoToFlightCheckOpensTheFlightCheck()
    {
        var flow = NewFlow(null, 0);
        Briefing(flow);

        flow.FocusRow(CampaignBriefingPage.FlightCheckRow);
        flow.Accept();

        Assert.Equal(CampaignScreen.FlightCheck, flow.Screen);
    }

    [Fact]
    public void BackLeavesTheBriefingForTheCabin()
    {
        var flow = NewFlow(null, 0);
        Briefing(flow);

        flow.Back();

        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        Assert.Equal(CampaignExit.None, flow.Exit);
    }

    /// <summary>Without an extraction the screen still opens: no map, no note, no narration, and
    /// the buttons in the original's own words.</summary>
    [Fact]
    public void WithNoDataRootTheScreenDegradesRatherThanThrowing()
    {
        var page = Briefing(NewFlow(null, 0));

        Assert.Null(page.State);
        Assert.Null(page.Art);
        Assert.Empty(page.Objectives);
        Assert.Equal(string.Empty, page.NarrationWav);
    }

    /// <summary>⚠ The state and the narration come from the mission's storage address, never from
    /// its story position: Hawaii's second mission is <c>C3/M05</c> and plays the act's fifth wav,
    /// and its third is <c>C3/M02</c>. Computing either from <c>seq</c> gets both wrong.</summary>
    [ExtractedDataTheory]
    [InlineData(0, "brief_c61", "c1-HA-m1_briefing.wav", "HA-m1MAP")]
    [InlineData(1, "brief_c65", "c1-HA-m5_briefing.wav", "HA-m1MAP")]
    [InlineData(2, "brief_c62", "c1-HA-m2_briefing.wav", "HA-m2MAP")]
    [InlineData(5, "brief_c31", "c2-NW-m1_briefing.wav", "NW-m1MAP")]
    [InlineData(13, "brief_c54", "c3-HW-m4_briefing.wav", "HW-m4MAP")]
    [InlineData(23, "brief_c84", "c5-MH-m4_briefing.wav", "MH-m1map")]
    public void AMissionResolvesToItsOwnStateNarrationAndMap(
        int seq, string key, string wav, string map)
    {
        var page = Briefing(NewFlow(TestData.DataRoot, seq));

        Assert.Equal(key, page.State?.Key);
        Assert.Equal(map, page.State?.Background);
        Assert.Equal(wav, page.NarrationWav);
    }

    /// <summary>The note is the mission's own <c>MSG_BRF_*</c> text, in the order the strings'
    /// own numbering reads.</summary>
    [ExtractedDataFact]
    public void TheObjectivesNoteIsTheMissionsOwnText()
    {
        var page = Briefing(NewFlow(TestData.DataRoot, 0));

        Assert.Equal(4, page.Objectives.Count);
        Assert.Equal("MSG_BRF_HAM1_OBJ1", page.Objectives[0].Key);
        Assert.StartsWith("1)", page.Objectives[0].Text);
        Assert.StartsWith("4)", page.Objectives[3].Text);
    }

    /// <summary>The final mission binds one line, and it is the keyed objective rather than the
    /// keyless priority-1 block that sits above it in the reader.</summary>
    [ExtractedDataFact]
    public void TheLastMissionsSingleNoteLineIsItsKeyedObjective()
    {
        var page = Briefing(NewFlow(TestData.DataRoot, 23));

        Assert.Single(page.Objectives);
        Assert.Equal("MSG_BRF_NYM4_OBJ1", page.Objectives[0].Key);
    }

    /// <summary>The reveal runs off the narration's own cue points: nothing until the first
    /// marker, then the note fills in a line at a time, and REPLAY BRIEFING empties it again.
    /// ⚠ The screen's row count never moves: the note is written, not listed.</summary>
    [ExtractedDataFact]
    public void TheNoteFillsInAsTheNarrationReachesItsMarkersAndReplayEmptiesIt()
    {
        var page = Briefing(NewFlow(TestData.DataRoot, 0));

        Assert.Equal(0, page.Reveal?.BlockedOnMarker);
        Assert.Equal(3, page.RowCount);
        Assert.Empty(page.Notes);

        Play(page);
        Assert.True(page.Reveal?.Complete);
        Assert.Equal(3, page.RowCount);
        var entries = Assert.Single(page.Notes).Entries;
        Assert.Equal(4, entries.Count);
        Assert.StartsWith("1)", entries[0]);
        Assert.StartsWith("4)", entries[3]);

        page.Accept(CampaignBriefingPage.ReplayRow);
        Assert.Equal(3, page.RowCount);
        Assert.Empty(page.Notes);
        Assert.Equal(2, page.NarrationStarts);
    }

    /// <summary>The note sits in the parchment's own <c>LIST</c> widget, and nothing on the screen
    /// is a cursor stop but the three buttons.</summary>
    [ExtractedDataFact]
    public void TheNoteIsTheParchmentsOwnListWidgetAndNoneOfItIsSelectable()
    {
        var page = Briefing(NewFlow(TestData.DataRoot, 0));
        Play(page);

        var note = Assert.Single(page.Notes);
        Assert.Equal((35f, 335f, 185f, 240f, 5f), (note.X, note.Y, note.Width, note.Height, note.Spacing));
        for (int row = 0; row < page.RowCount; row++)
        {
            Assert.NotEqual(BoardButton.None, page.Button(row).Button);
        }
    }

    [ExtractedDataFact]
    public void TheMapIsTheStatesOwnBitmap()
    {
        var art = Briefing(NewFlow(TestData.DataRoot, 0)).Art;

        Assert.NotNull(art);
        Assert.Equal(800, art!.Image.Width);
        Assert.Equal(600, art.Image.Height);
    }

    /// <summary>The invariant that binds the two decodes: a state's <c>Objective</c> opcodes and
    /// its mission's keyed <c>IDENTITY</c> blocks are the same list, for all 24 missions.</summary>
    [ExtractedDataFact]
    public void EveryMissionsNoteHasALineForEveryObjectiveItsStateBinds()
    {
        var flow = NewFlow(TestData.DataRoot, 0);
        var page = Briefing(flow);
        for (int seq = 0; seq < CampaignSequence.MissionCount; seq++)
        {
            flow.SetMission(seq);
            int bound = 0;
            foreach (var step in page.State!.Steps)
            {
                if (step.Op == BriefingOp.Objective)
                {
                    bound++;
                }
            }

            Assert.True(
                bound == page.Objectives.Count,
                $"seq {seq} ({page.State.Key}) binds {bound} objectives but its note has " +
                $"{page.Objectives.Count}");
            Assert.NotEqual(string.Empty, page.NarrationWav);
        }
    }

    // The shell's own clock, a frame at a time: a briefing runs a couple of minutes, and a script
    // that blocks on an authored Wait cannot be jumped over in one step.
    private static void Play(CampaignBriefingPage page)
    {
        for (int frame = 0; frame < 4000 && page.Reveal is { Complete: false }; frame++)
        {
            page.Advance(0.05);
        }
    }

    private static CampaignFlow NewFlow(string? dataRoot, int seq)
    {
        var dir = Path.Combine(TestData.TempDir(), "Profiles");
        Directory.CreateDirectory(dir);
        var flow = new CampaignFlow(new CampaignProfileStore(dir), UiStrings.Empty, dataRoot);
        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary"));
        flow.SetMission(seq);
        return flow;
    }

    private static CampaignBriefingPage Briefing(CampaignFlow flow)
    {
        flow.GoTo(CampaignScreen.Briefing);
        return Assert.IsType<CampaignBriefingPage>(flow.Page);
    }
}
