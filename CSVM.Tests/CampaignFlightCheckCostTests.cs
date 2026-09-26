using System;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>What a repaint of the flight check screen costs, and what still reaches it. The board
/// recomposes every frame and asks the page for its rows a dozen times over, so a row list composed
/// per call is composed at frame rate: a hangar-built aircraft re-read from disk there cost the
/// screen most of its frame. Nothing about the board moves between two frames nobody touched.</summary>
public class CampaignFlightCheckCostTests
{
    // What a redraw of an untouched board may allocate. It composes nothing, so this is slack for
    // an incidental allocation and not a budget to spend.
    private const long RedrawBudgetBytes = 1024;

    private static readonly StockLoadouts Stock =
        StockLoadouts.Load(Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));

    private static int _sink;

    [Fact]
    public void ARedrawnBoardNobodyTouchedAllocatesNothing()
    {
        var page = NewPage(out _, out _, custom: true);

        Compose(page); // the first frame composes; the measured one is a steady-state redraw
        long before = GC.GetAllocatedBytesForCurrentThread();
        Compose(page);
        long cost = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(cost < RedrawBudgetBytes, $"a redraw allocated {cost} bytes");
    }

    // The other half of the cache: a loadout committed on the ammo screen edits the record the
    // rows were composed from, and the next redraw has to show it.
    [Fact]
    public void ACommittedLoadoutShowsOnTheNextRedraw()
    {
        var page = NewPage(out var flow, out _, custom: false);
        string before = page.Detail(0);

        var plane = flow.Profile!.Planes[0];
        flow.Feature.CommitLoadout(plane, new[] { 2, 2, 2, 2 }, (int[])plane.Ordnance.Clone());

        Assert.NotEqual(before, page.Detail(0));
    }

    // And a hangar visit, which is what can rewrite the build file behind an aircraft: the flow
    // re-reads the profile on the way back, which is the cached build's own key.
    [Fact]
    public void AHangarBuildSavedWhileTheScreenLivesShowsAfterTheResume()
    {
        var page = NewPage(out var flow, out var planes, custom: false);
        string before = page.Detail(0);

        var built = new CustomPlaneDef { Name = flow.Profile!.Planes[0].Name, Airframe = flow.Profile.Planes[0].Airframe };
        built.Guns[0] = new GunChoice(3, Twin: false); // .60-cal, single, which no stock fit carries
        planes.Save(built);
        flow.Resume();

        Assert.NotEqual(before, page.Detail(0));
    }

    // One frame as CampaignBoards.Compose asks for it: the captions, the pictures, and every row's
    // text, description, button and focusability. Every result is folded into an int, because
    // handing a bool or a struct to GC.KeepAlive boxes it and the box is what would be measured.
    private static void Compose(CampaignFlightCheckPage page)
    {
        var captions = page.Captions;
        for (int i = 0; i < captions.Count; i++)
        {
            _sink += captions[i].Text.Length;
        }

        var pictures = page.Pictures;
        for (int i = 0; i < pictures.Count; i++)
        {
            _sink += pictures[i].Frame;
        }

        int rowCount = page.RowCount;
        for (int row = 0; row < rowCount; row++)
        {
            _sink += page.RowText(row).Length + page.Detail(row).Length
                + (int)page.Button(row).Button + (page.Focusable(row) ? 1 : 0);
        }
    }

    // A profile whose pilot and wingman aircraft either carry a hangar build on file or fly their
    // airframe's stock fit.
    private static CampaignFlightCheckPage NewPage(out CampaignFlow flow, out CustomPlaneStore planes, bool custom)
    {
        string root = TestData.TempDir();
        var store = new CampaignProfileStore(Path.Combine(root, "Profiles"));
        planes = new CustomPlaneStore(Path.Combine(root, "Planes"));

        var profile = CampaignProfileDef.NewProfile("Zachary");
        store.Save(profile);
        if (custom)
        {
            foreach (var owned in profile.Planes)
            {
                var built = new CustomPlaneDef { Name = owned.Name, Airframe = owned.Airframe, LeftHardpoints = 2, RightHardpoints = 2 };
                built.Guns[0] = new GunChoice(1, Twin: true);
                planes.Save(built);
            }
        }

        flow = new CampaignFlow(store, UiStrings.Empty);
        flow.SelectProfile(store.Load("Zachary")!);
        flow.SetMission(0);
        return new CampaignFlightCheckPage(flow, planes, Stock, wingman: true);
    }
}
