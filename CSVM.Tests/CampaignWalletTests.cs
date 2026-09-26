using System;
using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The wallet gate B13 wires at the hangar's existing Purchase/Sell seam: a campaign flow refuses
/// an unaffordable build, an unavailable airframe and a purchase past the decoded slot cap, credits
/// the wallet at the decoded full sell price with the special-plane and two-plane floor refusals,
/// and the two wallet-free doors (a null <see cref="HangarFlow.Campaign"/>) never consult any of
/// it.
/// </summary>
public class CampaignWalletTests : IDisposable
{
    private readonly string _planesDir;
    private readonly string _profilesDir;
    private readonly CustomPlaneStore _planes;
    private readonly CampaignProfileStore _profiles;

    public CampaignWalletTests()
    {
        _planesDir = Path.Combine(Path.GetTempPath(), "csvm-campaign-planes-" + Guid.NewGuid().ToString("N"));
        _profilesDir = Path.Combine(Path.GetTempPath(), "csvm-campaign-profiles-" + Guid.NewGuid().ToString("N"));
        _planes = new CustomPlaneStore(_planesDir);
        _profiles = new CampaignProfileStore(_profilesDir);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _planesDir, _profilesDir })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A fresh profile is $0 (docs/org/hangar.md "starting funds are $0"): the cheapest
    /// legal build (a bare, engineless-but-fixed airframe with an engine) is still refused.</summary>
    [Fact]
    public void BuyIsRefusedUnderFunds()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var campaign = new CampaignWallet(_profiles, profile, _planes);
        var flow = new HangarFlow(_planes, UiStrings.Empty, campaign: campaign);
        flow.Accept(); // New Plane
        flow.Scratch.Airframe = 5; // Devastator: availability 1, so a fresh profile clears the threshold
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "Broke Build";
        Walk(flow, HangarScreen.Purchase);

        Assert.False(flow.Commit());
        Assert.Contains("INSUFFICIENT FUNDS", flow.Message, StringComparison.Ordinal);
        Assert.Empty(_planes.List());
        Assert.Equal(0, profile.Funds);
    }

    /// <summary>The 11-airframe threshold field, wired here: a fresh
    /// profile's progress (0 missions + 1) is below the Balmoral's availability (3), so it is
    /// refused even with unlimited funds.</summary>
    [Fact]
    public void BuyIsRefusedUnderThreshold()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Funds = 1_000_000;
        var campaign = new CampaignWallet(_profiles, profile, _planes);
        Assert.False(campaign.IsAirframeAvailable(2)); // Balmoral, availability 3

        var flow = new HangarFlow(_planes, UiStrings.Empty, campaign: campaign);
        flow.Accept();
        flow.Scratch.Airframe = 2;
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "Too Soon";
        Walk(flow, HangarScreen.Purchase);

        Assert.False(flow.Commit());
        Assert.Contains("not available yet", flow.Message, StringComparison.Ordinal);
        Assert.Empty(_planes.List());
        Assert.Equal(1_000_000, profile.Funds);
    }

    /// <summary>The decoded slot cap: a profile already holding its 20 bought planes is refused the
    /// next purchase in the original's own words (langui 204), with the wallet, the ownership list
    /// and the build store all untouched.</summary>
    [Fact]
    public void BuyIsRefusedAtTheSlotCap()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Funds = 1_000_000;
        profile.MissionsCompleted = 20;
        FillBought(profile, CampaignWallet.PurchasedPlaneCap);

        var flow = CampaignFlow(profile);
        flow.Accept(); // the buy row
        flow.Scratch.Airframe = 10;
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "One Too Many";
        Walk(flow, HangarScreen.Purchase);

        Assert.False(flow.Commit());
        Assert.Contains("hangar limit", flow.Message, StringComparison.Ordinal);
        Assert.Equal(1_000_000, profile.Funds);
        Assert.Equal(CampaignWallet.PurchasedPlaneCap, profile.Planes.Count);
        Assert.Null(_planes.Load("One Too Many"));
    }

    /// <summary>The five reward aircraft sit outside the cap, as the original's own arithmetic has
    /// them: 19 bought planes plus all five awards still leaves the slot for a twentieth purchase,
    /// which fills the 25-record array exactly and is the last one the profile can make.</summary>
    [Fact]
    public void RewardAircraftDoNotCountAgainstTheSlotCap()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Funds = 1_000_000;
        FillBought(profile, CampaignWallet.PurchasedPlaneCap - 1);
        for (int i = 0; i < 5; i++)
        {
            profile.Planes.Add(new OwnedPlane { Name = "Award " + i, Airframe = 2, Special = true });
        }

        var campaign = new CampaignWallet(_profiles, profile, _planes);
        Assert.Equal(24, profile.Planes.Count);
        Assert.True(campaign.HasFreeSlot);

        campaign.Purchase("Last One", 10, 500);

        Assert.Equal(25, profile.Planes.Count);
        Assert.False(campaign.HasFreeSlot);
    }

    /// <summary>Enough funds and progress: the build lands, the wallet is debited the exact total
    /// the economy priced, and the plane is now owned.</summary>
    [Fact]
    public void BuyWithFundsAndThresholdMetSucceeds()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Funds = 10_000;
        profile.MissionsCompleted = 20; // clears every airframe's threshold
        var campaign = new CampaignWallet(_profiles, profile, _planes);

        var flow = new HangarFlow(_planes, UiStrings.Empty, campaign: campaign);
        flow.Accept();
        flow.Scratch.Airframe = 10; // Warhawk, cheapest airframe
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "Paid For";
        Walk(flow, HangarScreen.Purchase);
        int cost = HangarEconomy.Price(flow.Scratch).Total.Cost;

        Assert.True(flow.Commit());
        Assert.Equal(10_000 - cost, profile.Funds);
        Assert.Contains(profile.Planes, p => p.Name == "Paid For" && p.Airframe == 10);
        Assert.NotNull(_planes.Load("Paid For"));
    }

    /// <summary>Selling credits exactly the decoded full build cost (no depreciation,
    /// docs/org/hangar.md "The sell price is the full build cost"): buying then selling the same
    /// build leaves the wallet where it started.</summary>
    [Fact]
    public void SellCreditsExactlyTheDecodedPrice()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Funds = 10_000;
        profile.MissionsCompleted = 20;
        var campaign = new CampaignWallet(_profiles, profile, _planes);

        var flow = new HangarFlow(_planes, UiStrings.Empty, campaign: campaign);
        flow.Accept();
        flow.Scratch.Airframe = 10;
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "Round Trip";
        Walk(flow, HangarScreen.Purchase);
        flow.Commit();
        int fundsAfterBuy = profile.Funds;
        int price = campaign.SellPrice("Round Trip");

        // A third plane so the two-plane floor does not refuse this sale.
        Assert.True(campaign.Sell("Round Trip"));

        Assert.Equal(fundsAfterBuy + price, profile.Funds);
        Assert.Equal(10_000, profile.Funds); // the exact round trip: no loss, no gain
        Assert.DoesNotContain(profile.Planes, p => p.Name == "Round Trip");
        Assert.Null(_planes.Load("Round Trip"));
    }

    /// <summary>A fresh profile owns exactly the two starters: selling either would drop below the
    /// decoded floor (docs/org/hangar.md, langui 701), so both are refused.</summary>
    [Fact]
    public void SellIsRefusedAtTheTwoPlaneFloor()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var campaign = new CampaignWallet(_profiles, profile, _planes);

        Assert.False(campaign.CanSell("Gypsy Magic"));
        Assert.False(campaign.Sell("Gypsy Magic"));
        Assert.Equal(0, profile.Funds);
        Assert.Equal(2, profile.Planes.Count);
    }

    /// <summary>A reward aircraft (docs/org/hangar.md, the mission reward table; the record's
    /// Special flag) cannot be sold at all, even with plenty of planes to spare.</summary>
    [Fact]
    public void SpecialPlanesCannotBeSold()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Add(new OwnedPlane { Name = "Jumping Jane", Airframe = 2, Special = true });
        profile.Planes.Add(new OwnedPlane { Name = "Extra", Airframe = 10 });
        var campaign = new CampaignWallet(_profiles, profile, _planes);

        Assert.True(campaign.IsSpecial("Jumping Jane"));
        Assert.False(campaign.CanSell("Jumping Jane"));
        Assert.False(campaign.Sell("Jumping Jane"));
        Assert.Equal(4, profile.Planes.Count); // the two starters plus Jumping Jane plus Extra
    }

    /// <summary>The two existing doors (IA Build, top-level entry) pass no campaign context, so
    /// funds and threshold are never consulted: an otherwise-legal build commits exactly as
    /// It remains available on a plane whose profile funds are $0.</summary>
    [Fact]
    public void InstantActionHangarStaysWalletFree()
    {
        var flow = new HangarFlow(_planes, UiStrings.Empty); // no campaign argument at all
        Assert.Null(flow.Campaign);
        flow.Accept();
        flow.Scratch.Airframe = 2; // Balmoral: campaign-gated above, unrestricted here
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "IA Balmoral";
        Walk(flow, HangarScreen.Purchase);

        Assert.True(flow.Commit());
        Assert.Equal(HangarExit.Built, flow.Exit);
    }

    /// <summary>A fresh profile owns exactly the two seeded Devastators (docs/org/hangar.md, "The
    /// campaign instead starts with two aircraft"), and neither was ever hangar-built, so the
    /// roster has to resolve them from the ownership records themselves.</summary>
    [Fact]
    public void AFreshProfileListsExactlyItsTwoStarters()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var flow = CampaignFlow(profile);

        Assert.Equal(new[] { "Gypsy Magic", "The Knave" }, flow.Saved.Select(p => p.Name));
        Assert.All(flow.Saved, p => Assert.Equal(5, p.Airframe));
    }

    /// <summary>Ownership separates the two modes, not storage: an Instant Action build sits in
    /// the same user://Planes/ directory and the campaign roster does not list it, while Instant
    /// Action's own door still does.</summary>
    [Fact]
    public void AnInstantActionBuildDoesNotAppearInTheCampaignList()
    {
        _planes.Save(new CustomPlaneDef { Name = "IA Kestrel", Airframe = 8, Engine = 0 });
        var profile = CampaignProfileDef.NewProfile("Zachary");

        Assert.DoesNotContain(CampaignFlow(profile).Saved, p => p.Name == "IA Kestrel");
        Assert.Contains(new HangarFlow(_planes, UiStrings.Empty).Saved, p => p.Name == "IA Kestrel");
    }

    /// <summary>A purchase debits the wallet, names the build into the profile and shows up in the
    /// campaign roster the next time the door opens.</summary>
    [Fact]
    public void APurchaseAddsTheBuildToTheProfileListAndDebitsTheWallet()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Funds = 10_000;
        profile.MissionsCompleted = 20;
        var flow = CampaignFlow(profile);
        Assert.Equal("Buy a New Plane", flow.Page.RowText(0));

        flow.Accept(); // the buy row
        flow.Scratch.Airframe = 10;
        flow.Scratch.Engine = 0;
        flow.Scratch.Name = "Bought";
        Walk(flow, HangarScreen.Purchase);
        int cost = HangarEconomy.Price(flow.Scratch).Total.Cost;
        Assert.True(flow.Commit());

        Assert.Equal(10_000 - cost, profile.Funds);
        Assert.Contains(profile.Planes, p => p.Name == "Bought" && p.Airframe == 10);
        Assert.Equal(
            new[] { "Gypsy Magic", "The Knave", "Bought" },
            CampaignFlow(profile).Saved.Select(p => p.Name));
    }

    /// <summary>The campaign's trailing row is a sale, not a delete: it credits the wallet at the
    /// decoded full build cost and drops the plane from the profile and the build store together.</summary>
    [Fact]
    public void TheSellRowSellsThePlaneAndCreditsTheFullBuildCost()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Add(new OwnedPlane { Name = "Spare", Airframe = 10 });
        _planes.Save(new CustomPlaneDef { Name = "Spare", Airframe = 10, Engine = 0 });
        var campaign = new CampaignWallet(_profiles, profile, _planes);
        var flow = new HangarFlow(_planes, UiStrings.Empty, campaign: campaign);
        int price = campaign.SellPrice("Spare");
        Assert.True(price > 0);

        Assert.Equal("Sell a plane", flow.Page.RowText(flow.Saved.Count + 1));
        flow.FocusRow(flow.Saved.Count + 1);
        flow.Accept(); // open the sell list
        Assert.Equal("Sell Spare", flow.Page.RowText(2));
        flow.FocusRow(2);
        flow.Accept();

        Assert.Equal(price, profile.Funds);
        Assert.DoesNotContain(profile.Planes, p => p.Name == "Spare");
        Assert.Null(_planes.Load("Spare"));
        Assert.Equal(new[] { "Gypsy Magic", "The Knave" }, flow.Saved.Select(p => p.Name));
    }

    /// <summary>A reward aircraft is refused on the sell row itself, and the row says so before
    /// the press (docs/org/hangar.md, langui 704).</summary>
    [Fact]
    public void TheSellRowRefusesARewardAircraft()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Planes.Add(new OwnedPlane { Name = "Jumping Jane", Airframe = 2, Special = true });
        profile.Planes.Add(new OwnedPlane { Name = "Spare", Airframe = 10 });
        var flow = CampaignFlow(profile);

        flow.FocusRow(flow.Saved.Count + 1);
        flow.Accept();
        Assert.Contains("cannot be sold", flow.Page.Detail(2), StringComparison.Ordinal);
        flow.FocusRow(2);
        flow.Accept();

        Assert.Contains("cannot be sold", flow.Message, StringComparison.Ordinal);
        Assert.Equal(0, profile.Funds);
        Assert.Equal(4, profile.Planes.Count);
    }

    /// <summary>The two-aircraft floor holds on the row too: a fresh profile's sell list refuses
    /// both of its starters and the wallet never moves (langui 701).</summary>
    [Fact]
    public void TheSellRowHoldsTheTwoAircraftFloor()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var flow = CampaignFlow(profile);

        flow.FocusRow(flow.Saved.Count + 1);
        flow.Accept();
        Assert.Contains("at least two planes", flow.Page.Detail(0), StringComparison.Ordinal);
        flow.FocusRow(0);
        flow.Accept();

        Assert.Contains("at least two planes", flow.Message, StringComparison.Ordinal);
        Assert.Equal(0, profile.Funds);
        Assert.Equal(2, profile.Planes.Count);
    }

    /// <summary>An owned row is inert over a campaign flow: the decoded economy has no partial
    /// upgrade, so editing in place would charge for the build again and strand the old plane's
    /// value. Instant Action's own door still opens a saved plane for editing.</summary>
    [Fact]
    public void AnOwnedRowDoesNotOpenAnEditOverACampaignFlow()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var flow = CampaignFlow(profile);
        flow.FocusRow(1);
        flow.Accept();

        Assert.Equal(HangarScreen.PlaneSelection, flow.Screen);
        Assert.Null(flow.EditingName);
    }

    /// <summary>A campaign build cannot silently overwrite an Instant Action build: the taken-name
    /// check runs over the whole build directory, not over the visible campaign roster.</summary>
    [Fact]
    public void ACampaignFlowSeesInstantActionNamesAsTaken()
    {
        _planes.Save(new CustomPlaneDef { Name = "IA Kestrel", Airframe = 8, Engine = 0 });
        var flow = CampaignFlow(CampaignProfileDef.NewProfile("Zachary"));

        Assert.DoesNotContain(flow.Saved, p => p.Name == "IA Kestrel");
        Assert.True(flow.IsNameTaken("IA Kestrel"));
        Assert.True(flow.IsNameTaken("Gypsy Magic"));
        Assert.False(flow.IsNameTaken("Nothing Named This"));
    }

    /// <summary>Rebuilding under a name the profile already owns leaves one ownership record: the
    /// commit writes one file per name, so a second record would name one aeroplane twice and a
    /// later sale would remove both.</summary>
    [Fact]
    public void RebuildingAnOwnedNameKeepsOneOwnershipRecord()
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        profile.Funds = 10_000;
        var campaign = new CampaignWallet(_profiles, profile, _planes);

        campaign.Purchase("Gypsy Magic", 7, 500);

        Assert.Equal(2, profile.Planes.Count);
        Assert.Equal(7, profile.Planes[0].Airframe);
        Assert.Equal(9_500, profile.Funds);
    }

    /// <summary>The two wallet-free doors keep Instant Action's own verbs: a New Plane row and a
    /// delete that credits nothing.</summary>
    [Fact]
    public void InstantActionKeepsItsOwnRows()
    {
        _planes.Save(new CustomPlaneDef { Name = "IA Kestrel", Airframe = 8, Engine = 0 });
        var flow = new HangarFlow(_planes, UiStrings.Empty);

        Assert.Equal("New Plane", flow.Page.RowText(0));
        Assert.Equal("Delete a saved plane", flow.Page.RowText(2));
    }

    // Ownership records up to a wanted total, the two seeded starters counting towards it.
    private static void FillBought(CampaignProfileDef profile, int total)
    {
        for (int i = profile.Planes.Count; i < total; i++)
        {
            profile.Planes.Add(new OwnedPlane { Name = "Bought " + i, Airframe = 10 });
        }
    }

    private static void Walk(HangarFlow flow, HangarScreen target)
    {
        for (int guard = 0; flow.Screen != target && guard < HangarFlow.Order.Length + 3; guard++)
        {
            if (flow.Screen == HangarScreen.Airframe)
            {
                flow.FocusRow(flow.Scratch.Airframe);
            }

            flow.Accept();
        }

        Assert.Equal(target, flow.Screen);
    }

    private HangarFlow CampaignFlow(CampaignProfileDef profile) =>
        new(_planes, UiStrings.Empty, campaign: new CampaignWallet(_profiles, profile, _planes));
}
