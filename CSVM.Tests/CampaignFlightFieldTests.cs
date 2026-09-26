using System.IO;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>The human field a co-op campaign sortie flies: the sequential walk through the flight
/// checks, the no-duplicate rule over the guests' rosters, and the copy-not-reference rule that
/// keeps a guest's ammunition edits out of the seated profile (the plan's decision 4).</summary>
public class CampaignFlightFieldTests
{
    [Fact]
    public void ASoloCampaignHasNoGuestsAndNowhereToAdvanceTo()
    {
        var flow = NewFlow(out _);

        Assert.Equal(1, flow.Field.Players);
        Assert.Empty(flow.Field.Guests);
        Assert.False(flow.Field.Advance());
        Assert.True(flow.Field.Locked);
    }

    [Fact]
    public void TheWalkVisitsEveryJoinedPlayerOnceAndThenStops()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(3);

        Assert.Equal(0, flow.Field.Current);
        Assert.True(flow.Field.Advance());
        Assert.Equal(1, flow.Field.Current);
        Assert.True(flow.Field.Advance());
        Assert.Equal(2, flow.Field.Current);
        Assert.False(flow.Field.Advance());
    }

    // The field is joinable up to and including the seated player's FLY MISSION, and
    // that press is now what starts the walk rather than what leaves the screen.
    [Fact]
    public void TheFieldLocksOnceTheWalkStartsAndUnlocksWhenItIsAbandoned()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(2);

        Assert.False(flow.Field.Locked);
        Assert.True(flow.Field.Advance());
        Assert.True(flow.Field.Locked);
        Assert.True(flow.Field.Retreat());
        Assert.True(flow.Field.Locked);
        Assert.False(flow.Field.Retreat());

        flow.Field.Rewind();

        Assert.False(flow.Field.Locked);
    }

    [Fact]
    public void RewindPutsTheSeatedPlayerBackOnTheirOwnCheck()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(4);
        flow.Field.Advance();
        flow.Field.Advance();

        flow.Field.Rewind();

        Assert.Equal(0, flow.Field.Current);
    }

    // A guest leaving mid-sequence drops the trailing record and pulls the cursor back into the
    // field, so the screen can never be showing a check nobody is flying.
    [Fact]
    public void AShrinkingFieldDropsTheTrailingGuestAndClampsTheCursor()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(3);
        flow.Field.Advance();
        flow.Field.Advance();

        flow.SetPlayers(2);

        Assert.Single(flow.Field.Guests);
        Assert.Equal(1, flow.Field.Current);
        Assert.True(flow.Field.Locked);
    }

    [Fact]
    public void AGuestOpensOnTheStockDevastatorTheCampaignItselfStartsOn()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(2);

        var plane = flow.Field.Plane(1)!;

        Assert.Equal(5, plane.Airframe);
        Assert.True(flow.Field.IsStock(plane));
    }

    // Decision 4's roster: the eleven stock airframes plus the seated profile's own aircraft.
    [Fact]
    public void AGuestsRosterIsTheStockAirframesPlusTheSeatedProfilesAircraft()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(2);

        var guest = flow.Field.Guests[0];

        Assert.Equal(11, guest.StockCount);
        Assert.Equal(13, guest.Choices.Count); // + Gypsy Magic and The Knave
        Assert.Contains(guest.Choices, p => p.Name == "Gypsy Magic");
        Assert.False(flow.Field.IsStock(guest.Choices[guest.StockCount]));
    }

    // "An aircraft taken by P2 is absent from P3's list": the picker asks Taken before it commits,
    // and Choose refuses the same entry, so no caller can seat two humans in one aeroplane. The
    // stock entries are the leading Choices in airframe order, so index 5 IS stock airframe 5.
    [Fact]
    public void AnAircraftAnotherPlayerTookIsRefusedToTheNextGuest()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(3);
        int taken = flow.Field.Plane(1)!.Airframe;

        Assert.True(flow.Field.Taken(2, taken));
        Assert.False(flow.Field.Choose(2, taken));
        Assert.NotEqual(taken, flow.Field.Plane(2)!.Airframe);
    }

    // The seated player's own aircraft is taken too: two humans in one aeroplane is the same
    // duplicate, and the profile's selected plane is the one thing a guest cannot have.
    [Fact]
    public void TheSeatedPlayersOwnAircraftIsNotOfferedToAGuest()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(2);
        var guest = flow.Field.Guests[0];

        int gypsy = -1;
        for (int i = 0; i < guest.Choices.Count; i++)
        {
            if (guest.Choices[i].Name == "Gypsy Magic")
            {
                gypsy = i;
            }
        }

        Assert.True(gypsy >= 0);
        Assert.True(flow.Field.Taken(1, gypsy));
        Assert.False(flow.Field.Choose(1, gypsy));
        Assert.NotEqual("Gypsy Magic", flow.Field.Plane(1)!.Name);
    }

    // A free entry is what Choose is for: the picker's ACCEPT, moving one guest's own pick.
    [Fact]
    public void ChoosingAFreeEntryMovesThatGuestsPickAndNobodyElses()
    {
        var flow = NewFlow(out var profile);
        flow.SetPlayers(3);
        int before = flow.Field.Guests[1].Choice;

        Assert.True(flow.Field.Choose(1, 9));

        Assert.Equal(9, flow.Field.Plane(1)!.Airframe);
        Assert.Equal(before, flow.Field.Guests[1].Choice);
        Assert.Equal(0, profile.SelectedPlane);
    }

    // ⚠ The rule decision 4 turns on: a guest flying one of the seated profile's aircraft flies a
    // COPY. A reference here would put their ammunition edits into the profile's own record.
    [Fact]
    public void AGuestFlyingAProfileAircraftFliesACopyOfIt()
    {
        var flow = NewFlow(out var profile);
        flow.SetPlayers(2);
        var guest = flow.Field.Guests[0];
        var copy = guest.Choices[guest.StockCount + 1]; // The Knave, which nobody took

        Assert.Equal("The Knave", copy.Name);
        Assert.NotSame(profile.Planes[1], copy);
        copy.Ammo[0] = 3;
        Assert.Equal(0, profile.Planes[1].Ammo[0]);
    }

    // Two guests never share a record either, or one guest's ammunition edits would follow the
    // aeroplane to whoever picked it up next.
    [Fact]
    public void TwoGuestsHoldSeparateCopiesOfTheSameProfileAircraft()
    {
        var flow = NewFlow(out _);
        flow.SetPlayers(3);

        var first = flow.Field.Guests[0];
        var second = flow.Field.Guests[1];

        Assert.NotSame(first.Choices[first.StockCount], second.Choices[second.StockCount]);
    }

    private static CampaignFlow NewFlow(out CampaignProfileDef profile)
    {
        var store = new CampaignProfileStore(Path.Combine(TestData.TempDir(), "Profiles"));
        store.Save(CampaignProfileDef.NewProfile("Zachary"));
        var flow = new CampaignFlow(store, UiStrings.Empty);
        profile = store.Load("Zachary")!;
        flow.SelectProfile(profile);
        flow.SetMission(0);
        return flow;
    }
}
