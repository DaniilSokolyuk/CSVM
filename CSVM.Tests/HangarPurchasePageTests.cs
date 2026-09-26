using System;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The PURCHASE screen: the itemised review with one row per priced thing the
/// scratch plane carries (airframe always, engine when chosen, armed gun slots named as the GUNS
/// screen names them, armoured zones via langui 1191-1194, wings with hardpoints via 1176/1177),
/// each detailed with its decoded cost and weight, then the totals row and the Purchase Now row.
/// The row set is dynamic like the original's, and the Purchase Now row mirrors the original's
/// button gate: flagged with the problems text visible whenever the verdict is not Ok, not only
/// refusing on press.
/// </summary>
public class HangarPurchasePageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _profilesDir;
    private readonly CustomPlaneStore _store;
    private readonly CampaignProfileStore _profiles;

    public HangarPurchasePageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-purchase-" + Guid.NewGuid().ToString("N"));
        _profilesDir = Path.Combine(Path.GetTempPath(), "csvm-purchase-profiles-" + Guid.NewGuid().ToString("N"));
        _store = new CustomPlaneStore(_dir);
        _profiles = new CampaignProfileStore(_profilesDir);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _dir, _profilesDir })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A bare build (no engine, no guns, no armour, no hardpoints) reviews as exactly
    /// the airframe, the totals and the Purchase Now row: absent components get no row, the
    /// original's dynamic count.</summary>
    [Fact]
    public void ABareBuildShowsOnlyAirframeTotalsAndBuild()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);

        Assert.Equal(3, flow.Page.RowCount);
        Assert.Equal("Airframe 0", flow.Page.RowText(0));
        Assert.Equal("$6800   1400 lbs.", flow.Page.Detail(0));
        Assert.Equal("Totals", flow.Page.RowText(1));
    }

    /// <summary>A chosen engine gets its own row, priced by the decoded base-plus-offset line.</summary>
    [Fact]
    public void AnEngineRowAppearsWhenChosen()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        flow.Scratch.Engine = 1;

        Assert.Equal(4, flow.Page.RowCount);
        Assert.Equal("Engine 1", flow.Page.RowText(1));
        Assert.Equal("$850   1000 lbs.", flow.Page.Detail(1));
    }

    /// <summary>Each armed gun slot rows exactly as the GUNS screen names it (slot title, then
    /// the shared calibre naming with its twin prefix), priced by the airframe's wing or turret
    /// column, doubled for twin. Empty slots get no row.</summary>
    [Fact]
    public void ArmedGunSlotsRowAsTheGunsScreenNamesThem()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        flow.Scratch.Airframe = 1; // Hellhound: turret mask 0x08, slot 3 is the turret
        flow.Scratch.Guns[0] = new GunChoice(2, Twin: true);
        flow.Scratch.Guns[3] = new GunChoice(0, Twin: false);

        Assert.Equal(5, flow.Page.RowCount); // airframe + two guns + totals + build
        Assert.Equal("Slot 1: (2) .50-cal.", flow.Page.RowText(1));
        Assert.Equal("$820   960 lbs.", flow.Page.Detail(1)); // wing column, twinned
        Assert.Equal("Slot 4: .30-cal.", flow.Page.RowText(2));
        Assert.Equal("$440   520 lbs.", flow.Page.Detail(2)); // the turret column
    }

    /// <summary>Armoured zones row through their own langui formats (1191-1194) showing the units
    /// the presses bought, priced at the decoded $4 and 4 lb a unit, so $20 and 20 lb a press;
    /// empty zones get no row.</summary>
    [Fact]
    public void ArmouredZonesRowThroughTheirLanguiFormats()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":1191,\"text\":\"Nose: %1!d! units\",\"dll\":\"langui\"}," +
            "{\"id\":1193,\"text\":\"Left Wing: %1!d! units\",\"dll\":\"langui\"}]");
        var flow = OpenOnPurchase(strings);
        flow.Scratch.ArmourNose = 5;
        flow.Scratch.ArmourLeftWing = 2;

        Assert.Equal(5, flow.Page.RowCount);
        Assert.Equal("Nose: 25 units", flow.Page.RowText(1));
        Assert.Equal("$100   100 lbs.", flow.Page.Detail(1));
        Assert.Equal("Left Wing: 10 units", flow.Page.RowText(2));
        Assert.Equal("$40   40 lbs.", flow.Page.Detail(2));
    }

    /// <summary>Each wing with hardpoints rows through 1176/1177 at the decoded $410 / 480 lb
    /// each; a bare wing gets no row.</summary>
    [Fact]
    public void WingsWithHardpointsGetTheirRows()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":1177,\"text\":\"Right Wing: %1!d!\",\"dll\":\"langui\"}]");
        var flow = OpenOnPurchase(strings);
        flow.Scratch.RightHardpoints = 2;

        Assert.Equal(4, flow.Page.RowCount);
        Assert.Equal("Right Wing: 2", flow.Page.RowText(1));
        Assert.Equal("$820   960 lbs.", flow.Page.Detail(1));
    }

    /// <summary>A missing string table falls back to the same shapes in plain text.</summary>
    [Fact]
    public void RowTextFallsBackWithoutStrings()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        flow.Scratch.ArmourTail = 3;
        flow.Scratch.LeftHardpoints = 1;

        Assert.Equal("Tail: 15 units", flow.Page.RowText(1));
        Assert.Equal("Left Wing: 1", flow.Page.RowText(2));
        Assert.Equal("Purchase Now", flow.Page.RowText(flow.Page.RowCount - 1)[^12..]);
    }

    /// <summary>The totals row carries the bill's grand total judged against the airframe's
    /// capacity, the same line the shell shows on every hangar screen.</summary>
    [Fact]
    public void TheTotalsRowJudgesWeightAgainstCapacity()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        flow.Scratch.Engine = 1;

        int totals = flow.Page.RowCount - 2;
        Assert.Equal("Totals", flow.Page.RowText(totals));
        Assert.Equal("$7650   2400 / 4160 lbs.", flow.Page.Detail(totals));
    }

    /// <summary>The gate is the button, not only the commit: an engineless build flags the
    /// Purchase Now row and shows the problems text before any press, the original's greyed
    /// pur_b_purchase with pur_t_problems carrying the reason.</summary>
    [Fact]
    public void TheBuildRowIsFlaggedAndExplainsWhenBlocked()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        var page = (HangarPurchasePage)flow.Page;

        int build = page.RowCount - 1;
        Assert.False(page.BuildEnabled);
        Assert.StartsWith("✕", page.RowText(build), StringComparison.Ordinal);
        Assert.Equal("CAN'T PURCHASE: No Engine Selected", page.Detail(build));

        flow.Scratch.Engine = 1;
        Assert.True(page.BuildEnabled);
        Assert.Equal("Purchase Now", page.RowText(page.RowCount - 1));
        Assert.Equal(string.Empty, page.Detail(page.RowCount - 1));
    }

    /// <summary>An overweight build flags with the original's word on the Purchase Now row and on
    /// the totals line both.</summary>
    [Fact]
    public void AnOverweightBuildFlagsWithTheOriginalsWord()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        MakeOverweight(flow.Scratch);

        Assert.Contains("OVERWEIGHT", flow.Page.Detail(flow.Page.RowCount - 1), StringComparison.Ordinal);
        Assert.Contains("OVERWEIGHT", flow.Page.Detail(flow.Page.RowCount - 2), StringComparison.Ordinal);
    }

    /// <summary>The Purchase Now press commits: the existing gate semantics, now reached through
    /// the itemised page's own build row.</summary>
    [Fact]
    public void TheBuildRowCommits()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "Reviewed";

        Assert.True(flow.Page.Accept(flow.Page.RowCount - 1));
        Assert.Equal(HangarExit.Built, flow.Exit);
        Assert.Equal("Reviewed", Assert.Single(_store.List()).Name);
    }

    /// <summary>A press on a review row is a no-op: it neither commits nor advances, since the
    /// purchase screen is the last one.</summary>
    [Fact]
    public void AReviewRowPressDoesNotCommit()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "Not Yet";

        Assert.True(flow.Page.Accept(0));
        Assert.Equal(HangarExit.None, flow.Exit);
        Assert.Empty(_store.List());
    }

    /// <summary>A blocked build's press is still refused with the commit's own words; the flag on
    /// the row is a mirror of that rule, not a replacement for it.</summary>
    [Fact]
    public void ABlockedBuildPressIsStillRefused()
    {
        var flow = OpenOnPurchase(UiStrings.Empty);
        flow.Scratch.Name = "Engineless";

        Assert.True(flow.Page.Accept(flow.Page.RowCount - 1));
        Assert.Equal(HangarExit.None, flow.Exit);
        Assert.Contains("No Engine Selected", flow.Message, StringComparison.Ordinal);
        Assert.Empty(_store.List());
    }

    /// <summary>The decoded slot cap is the row's own third campaign reason: a profile already
    /// holding its bought planes draws the Purchase Now row refused, saying what the press would
    /// say (langui 204), rather than staying live and refusing afterwards.</summary>
    [Fact]
    public void TheBuildRowIsRefusedAtTheSlotCap()
    {
        var page = (HangarPurchasePage)OpenOnPurchaseOverWallet(CampaignWallet.PurchasedPlaneCap).Page;

        int build = page.RowCount - 1;
        Assert.False(page.BuildEnabled);
        Assert.StartsWith("✕", page.RowText(build), StringComparison.Ordinal);
        Assert.Contains("hangar limit", page.Detail(build), StringComparison.Ordinal);
    }

    /// <summary>One plane under the cap the same build is live and its row says nothing, so what
    /// greys the row is the cap itself and not the campaign door.</summary>
    [Fact]
    public void TheBuildRowStaysLiveOnePlaneUnderTheSlotCap()
    {
        var page = (HangarPurchasePage)OpenOnPurchaseOverWallet(CampaignWallet.PurchasedPlaneCap - 1).Page;

        Assert.True(page.BuildEnabled);
        Assert.Equal("Purchase Now", page.RowText(page.RowCount - 1));
        Assert.Equal(string.Empty, page.Detail(page.RowCount - 1));
    }

    /// <summary>The press-time refusal stands underneath the greyed row: a row can be enabled and
    /// the profile fill behind it, so the commit checks the cap again in the same words.</summary>
    [Fact]
    public void ThePressIsStillRefusedWhenTheCapIsReachedBehindTheRow()
    {
        var flow = OpenOnPurchaseOverWallet(CampaignWallet.PurchasedPlaneCap - 1);
        var page = (HangarPurchasePage)flow.Page;
        Assert.True(page.BuildEnabled);

        flow.Campaign!.Profile.Planes.Add(new OwnedPlane { Name = "Filled Behind It", Airframe = 10 });
        flow.Scratch.Name = "One Too Many";

        Assert.True(page.Accept(page.RowCount - 1));
        Assert.Contains("hangar limit", flow.Message, StringComparison.Ordinal);
        Assert.Null(_store.Load("One Too Many"));
    }

    // The overweight rig HangarFlowTests uses: a Balmoral loaded far past its 15760 lb capacity.
    private static void MakeOverweight(CustomPlaneDef def)
    {
        def.Airframe = 2;
        def.Engine = 5;
        def.ArmourNose = 12;
        def.ArmourTail = 12;
        def.ArmourLeftWing = 12;
        def.ArmourRightWing = 12;
        for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
        {
            def.Guns[slot] = new GunChoice(4, Twin: true);
        }

        def.LeftHardpoints = 4;
        def.RightHardpoints = 4;
    }

    // A flow standing on the purchase screen over a fresh scratch plane.
    private HangarFlow OpenOnPurchase(UiStrings strings)
    {
        var flow = new HangarFlow(_store, strings);
        for (int guard = 0; flow.Screen != HangarScreen.Purchase && guard < HangarFlow.Order.Length + 3; guard++)
        {
            // A no-op except on the airframe-defaults ask (E41), which the airframe screen's own
            // confirm raises before the next confirm advances (E49).
            flow.AnswerDefaultsAsk(false);
            flow.Accept();
        }

        Assert.Equal(HangarScreen.Purchase, flow.Screen);
        return flow;
    }

    // The same walk over a campaign wallet whose profile already owns `bought` planes, with funds
    // and progress well clear of the other two campaign reasons so the slot cap is the only one
    // left to read, and an engine picked so the economy's own verdict is Ok.
    private HangarFlow OpenOnPurchaseOverWallet(int bought)
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Funds = 1_000_000;
        profile.MissionsCompleted = 20;
        for (int i = profile.Planes.Count; i < bought; i++)
        {
            profile.Planes.Add(new OwnedPlane { Name = "Bought " + i, Airframe = 10 });
        }

        var flow = new HangarFlow(_store, UiStrings.Empty, campaign: new CampaignWallet(_profiles, profile, _store));
        for (int guard = 0; flow.Screen != HangarScreen.Purchase && guard < HangarFlow.Order.Length + 3; guard++)
        {
            flow.AnswerDefaultsAsk(false);
            flow.Accept();
        }

        Assert.Equal(HangarScreen.Purchase, flow.Screen);
        flow.Scratch.Engine = 1;
        return flow;
    }
}
