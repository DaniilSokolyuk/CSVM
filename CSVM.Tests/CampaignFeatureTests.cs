using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The campaign feature's contract with the profile store, pinned over a scratch directory: what a
/// created player writes and where, what the refusals leave untouched, what a fit, a crew pick, an
/// export, a purchase, a sale, a launch and a deletion write, what a flown mission's record reads
/// back as, and that a discard touches no file. Every file is compared as the store's own canonical
/// text, so a changed write order or field is a failed test rather than a changed save.
/// </summary>
public class CampaignFeatureTests
{
    private const string Pilot = "Zachary";

    [Fact]
    public void ContinueOnANewNameWritesAFreshProfileAndRecordsThePlayer()
    {
        var (feature, store, dir) = Open();

        Assert.Null(feature.ContinuePlayer(Pilot));

        Assert.Equal(CampaignProfileStore.Serialize(CampaignProfileDef.NewProfile(Pilot)), ProfileText(store, Pilot));
        Assert.Equal(new[] { Pilot }, feature.Roster);
        Assert.Equal(Pilot, feature.Profile?.Name);
        Assert.Equal(Pilot, store.LastPlayed);
        Assert.Equal(new[] { "last-played.json" }, Names(Directory.GetFiles(dir)));
        Assert.Equal(new[] { Pilot }, Names(Directory.GetDirectories(dir)));
        using var last = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "last-played.json")));
        Assert.Equal(CampaignProfileStore.Version, last.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(Pilot, last.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void ContinueOnAnExistingPlayerSeatsWithoutRewritingTheProfile()
    {
        var (feature, store, _) = Open();
        var profile = CampaignProfileDef.NewProfile(Pilot);
        profile.Funds = 1234;
        store.Save(profile);
        string before = ProfileText(store, Pilot);
        feature.RefreshRoster();

        Assert.Null(feature.ContinuePlayer(Pilot));

        Assert.Equal(before, ProfileText(store, Pilot));
        Assert.Equal(1234, feature.Profile?.Funds);
        Assert.Equal(Pilot, store.LastPlayed);
    }

    [Fact]
    public void RefusedNamesWriteNothing()
    {
        var (feature, store, dir) = Open();

        Assert.Equal("You must enter a player name.", feature.ContinuePlayer("   "));
        Assert.Equal(
            "Your player name is limited to alphabetic and numeric characters and spaces.",
            feature.ContinuePlayer("Bad!Name"));
        Assert.Equal(
            $"Your player name is limited to {CampaignFeature.MaxNameLength} characters.",
            feature.ContinuePlayer(new string('a', CampaignFeature.MaxNameLength + 1)));

        Assert.False(Directory.Exists(dir));
        Assert.Null(feature.Profile);
        Assert.Equal(string.Empty, store.LastPlayed);
    }

    [Fact]
    public void AFullRosterRefusesANewPlayerAndStillContinuesAnExistingOne()
    {
        var (feature, store, _) = Open();
        for (int i = 0; i < CampaignFeature.MaxProfiles; i++)
        {
            store.Save(CampaignProfileDef.NewProfile($"Pilot {i:00}"));
        }

        feature.RefreshRoster();

        Assert.StartsWith("Crimson Skies supports only 24 active players", feature.ContinuePlayer("One More"));
        Assert.Equal(CampaignFeature.MaxProfiles, store.List().Count);
        Assert.Null(feature.ContinuePlayer("Pilot 03"));
        Assert.Equal("Pilot 03", feature.Profile?.Name);
    }

    [Fact]
    public void DeleteRemovesOnlyThatProfileAndClearsItsLastPlayedRecord()
    {
        var (feature, store, dir) = Open();
        feature.ContinuePlayer("Nathan");
        feature.ContinuePlayer(Pilot);
        string nathan = ProfileText(store, "Nathan");

        Assert.True(feature.DeletePlayer(Pilot));

        Assert.False(Directory.Exists(store.DirFor(Pilot)));
        Assert.Equal(nathan, ProfileText(store, "Nathan"));
        Assert.Equal(new[] { "Nathan" }, feature.Roster);
        Assert.Equal(string.Empty, store.LastPlayed);
        Assert.Equal(new[] { "Nathan" }, Names(Directory.GetDirectories(dir)));

        feature.ContinuePlayer("Nathan");
        Assert.False(feature.DeletePlayer("Nobody"));
        Assert.Equal("Nathan", store.LastPlayed);
    }

    [Fact]
    public void CommitLoadoutSavesTheSeatedPlanesFitAndNothingForAGuest()
    {
        var (feature, store, _) = Open();
        feature.ContinuePlayer(Pilot);
        var profile = feature.Profile!;
        feature.SetAmmoSlot(0);
        var target = feature.AmmoTarget()!;
        Assert.Same(profile.Planes[0], target);

        feature.CommitLoadout(target, new[] { 1, 2, 0, 3 }, new[] { 2, 0, 0, 0, 5, 0, 0, 0 });

        Assert.Equal(CampaignProfileStore.Serialize(profile), ProfileText(store, Pilot));
        var saved = store.Load(Pilot)!;
        Assert.Equal(new[] { 1, 2, 0, 3 }, saved.Planes[0].Ammo);
        Assert.Equal(new[] { 2, 0, 0, 0, 5, 0, 0, 0 }, saved.Planes[0].Ordnance);

        string before = ProfileText(store, Pilot);
        feature.Field.SetPlayers(2);
        Assert.True(feature.Field.Advance());
        var guestPlane = feature.AmmoTarget()!;
        Assert.True(feature.Field.IsStock(guestPlane));
        feature.CommitLoadout(guestPlane, new[] { 3, 3, 3, 3 }, new int[8]);

        Assert.Equal(new[] { 3, 3, 3, 3 }, guestPlane.Ammo);
        Assert.Equal(before, ProfileText(store, Pilot));
    }

    [Fact]
    public void CommitPlanesWritesThePicksAndLeavesTheWingmanWhenNoneIsGiven()
    {
        var (feature, store, _) = Open();
        feature.ContinuePlayer(Pilot);
        feature.Profile!.Planes.Add(new OwnedPlane { Name = "Test Bird", Airframe = 3 });

        feature.CommitPlanes(2, 0);
        var saved = store.Load(Pilot)!;
        Assert.Equal(2, saved.SelectedPlane);
        Assert.Equal(0, saved.WingmanPlane);
        Assert.Equal(3, saved.Planes.Count);

        feature.CommitPlanes(1, null);
        saved = store.Load(Pilot)!;
        Assert.Equal(1, saved.SelectedPlane);
        Assert.Equal(0, saved.WingmanPlane);
        Assert.Equal(CampaignProfileStore.Serialize(feature.Profile), ProfileText(store, Pilot));
    }

    [Fact]
    public void TheSeatedPairRuleComparesByName()
    {
        var (feature, _, _) = Open();
        feature.ContinuePlayer(Pilot);
        feature.Profile!.Planes.Add(new OwnedPlane { Name = "Gypsy Magic", Airframe = 3 });

        Assert.True(feature.SeatedPairClashes(0, 2));
        Assert.False(feature.SeatedPairClashes(0, 1));
        Assert.False(feature.SeatedPairClashes(0, 9));
    }

    [Fact]
    public void ExportWritesTheBuildStoreAndNeverTheProfile()
    {
        var planesDir = TestData.TempDir();
        var planes = new CustomPlaneStore(planesDir);
        var (feature, store, _) = Open(planes);
        feature.ContinuePlayer(Pilot);
        var plane = feature.Profile!.Planes[1];
        plane.Ammo = new[] { 2, 2, 2, 2 };
        string before = ProfileText(store, Pilot);

        Assert.True(feature.ExportPlane(plane));

        var built = planes.Load(plane.Name);
        Assert.NotNull(built);
        Assert.Equal(plane.Airframe, built!.Airframe);
        Assert.True(built.HasLoadout);
        Assert.Equal(before, ProfileText(store, Pilot));

        feature.Field.SetPlayers(2);
        feature.Field.Advance();
        var stock = feature.Field.Plane(1)!;
        Assert.False(feature.ExportPlane(stock));
        Assert.Null(planes.Load(stock.Name));
        Assert.False(feature.ExportPlane(new OwnedPlane { Name = " ", Airframe = 1 }));
    }

    [Fact]
    public void ExportIsRefusedWithoutABuildStore()
    {
        var (feature, _, _) = Open();
        feature.ContinuePlayer(Pilot);

        Assert.False(feature.ExportPlane(feature.Profile!.Planes[0]));
    }

    [Fact]
    public void TheWalletsPurchaseAndSaleWriteTheProfileAndTheSaleDeletesTheBuild()
    {
        var planes = new CustomPlaneStore(TestData.TempDir());
        var (feature, store, _) = Open(planes);
        feature.ContinuePlayer(Pilot);
        var profile = feature.Profile!;
        profile.Funds = 50_000;
        var wallet = feature.Wallet();
        Assert.NotNull(wallet);

        planes.Save(new CustomPlaneDef { Name = "Bought", Airframe = 5, Engine = 1 });
        wallet!.Purchase("Bought", 5, 12_000);

        var saved = store.Load(Pilot)!;
        Assert.Equal(38_000, saved.Funds);
        Assert.Equal(new[] { "Gypsy Magic", "The Knave", "Bought" }, saved.Planes.ConvertAll(p => p.Name));
        Assert.Equal(CampaignProfileStore.Serialize(profile), ProfileText(store, Pilot));

        int price = wallet.SellPrice("Bought");
        Assert.True(wallet.Sell("Bought"));

        saved = store.Load(Pilot)!;
        Assert.Equal(38_000 + price, saved.Funds);
        Assert.Equal(new[] { "Gypsy Magic", "The Knave" }, saved.Planes.ConvertAll(p => p.Name));
        Assert.Null(planes.Load("Bought"));
        Assert.Equal(CampaignProfileStore.Serialize(profile), ProfileText(store, Pilot));
    }

    [Fact]
    public void TheWalletNeedsASeatedProfileAndABuildStore()
    {
        var (feature, _, _) = Open();
        Assert.Null(feature.Wallet());
        feature.ContinuePlayer(Pilot);
        Assert.Null(feature.Wallet());
    }

    [Fact]
    public void FlyMissionSavesTheProfileAndCarriesOneSeatPerHuman()
    {
        var (feature, store, _) = Open();
        feature.ContinuePlayer(Pilot);
        var profile = feature.Profile!;
        profile.SelectedPlane = 1;
        feature.SetMission(3);
        feature.Field.SetPlayers(2);
        var guest = feature.Field.Plane(1)!;

        var exit = feature.BuildExit(new IReadOnlyList<int>[] { new[] { 0 }, new[] { 1, 2 } });

        Assert.NotNull(exit);
        Assert.Equal(Pilot, exit!.Profile);
        Assert.Equal(3, exit.MissionSeq);
        Assert.Equal(2, exit.Seats.Count);
        Assert.Equal("node5", exit.Seats[0].PlaneNode);
        Assert.Equal(new[] { 0 }, exit.Seats[0].Pads);
        Assert.NotNull(exit.Seats[0].Fit);
        Assert.Null(exit.Seats[0].Custom);
        Assert.Equal($"node{guest.Airframe}", exit.Seats[1].PlaneNode);
        Assert.Equal(new[] { 1, 2 }, exit.Seats[1].Pads);
        Assert.Equal(1, store.Load(Pilot)!.SelectedPlane);
        Assert.Equal(CampaignProfileStore.Serialize(profile), ProfileText(store, Pilot));
    }

    [Fact]
    public void FlyMissionWithNobodySeatedIsNothing()
    {
        var (feature, _, dir) = Open();

        Assert.Null(feature.BuildExit(new IReadOnlyList<int>[] { new int[0] }));
        Assert.False(Directory.Exists(dir));
    }

    /// <summary>A flown mission is written by the session's director through the progression rules
    /// and one save; what the menu reads back on the return is that file.</summary>
    [Fact]
    public void AFlownMissionsRecordIsWhatTheReturnReads()
    {
        var (feature, store, _) = Open();
        feature.ContinuePlayer(Pilot);
        var flown = store.Load(Pilot)!;
        var recorded = CampaignProgression.Record(flown, new MissionAttempt(
            0, CampaignProgression.PrimaryObjectiveMask, 300_000, 400, 120, flown.Planes[0].Airframe, flown.Planes[0].Name));
        store.Save(flown);
        Assert.True(recorded.Advanced);

        using var doc = JsonDocument.Parse(ProfileText(store, Pilot));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("missionsCompleted").GetInt32());
        Assert.Equal(recorded.MoneyPaid, root.GetProperty("funds").GetInt32());
        var result = root.GetProperty("missionResults")[0];
        Assert.Equal(0, result.GetProperty("seq").GetInt32());
        Assert.Equal(0, result.GetProperty("attempts").GetInt32());
        Assert.Equal(300_000, result.GetProperty("latest").GetProperty("timeMs").GetInt32());
        Assert.Equal(CampaignProgression.PrimaryObjectiveMask, result.GetProperty("best").GetProperty("completedMask").GetInt32());

        Assert.Equal(0, feature.Profile!.MissionsCompleted);
        Assert.True(feature.SeatProfile(Pilot));
        Assert.Equal(1, feature.Profile!.MissionsCompleted);
        Assert.Equal(1, feature.NextMissionSeq);
        Assert.False(feature.SeatProfile("Nobody"));
        Assert.Equal(Pilot, feature.Profile?.Name);
    }

    [Fact]
    public void ResumeReReadsTheSeatedProfile()
    {
        var (feature, store, _) = Open();
        feature.ContinuePlayer(Pilot);
        var outside = store.Load(Pilot)!;
        outside.Funds = 777;
        store.Save(outside);

        feature.Resume();

        Assert.Equal(777, feature.Profile?.Funds);
    }

    [Fact]
    public void ChangePlaneIsAnsweredPerCrewSlotAgainstTheOwnedCountLessOneOnTheGrantMissions()
    {
        var (feature, _, _) = Open();
        feature.ContinuePlayer(Pilot);
        feature.SetMission(3);
        Assert.False(feature.ChangePlaneAllowed(0));
        Assert.False(feature.ChangePlaneAllowed(1));

        feature.Profile!.Planes.Add(new OwnedPlane { Name = "Third", Airframe = 2 });
        Assert.True(feature.ChangePlaneAllowed(0));
        Assert.True(feature.ChangePlaneAllowed(1));

        // Missions 13 and 17 bar the pilot's button outright and are counted one plane lower, so
        // three owned falls under the floor there and takes the wingman's button with it.
        feature.SetMission(12);
        Assert.False(feature.ChangePlaneAllowed(0));
        Assert.False(feature.ChangePlaneAllowed(1));

        feature.Profile.Planes.Add(new OwnedPlane { Name = "Fourth", Airframe = 2 });
        Assert.False(feature.ChangePlaneAllowed(0));
        Assert.True(feature.ChangePlaneAllowed(1));

        feature.SetMission(16);
        Assert.False(feature.ChangePlaneAllowed(0));
        Assert.True(feature.ChangePlaneAllowed(1));
        feature.SetMission(17);
        Assert.True(feature.ChangePlaneAllowed(0));
        Assert.True(feature.ChangePlaneAllowed(1));
    }

    [Fact]
    public void TheFlightCheckGrantsTheMissionsOwnAircraftOnceAndSelectsItOnEveryEntry()
    {
        var (feature, store, _) = Open();
        feature.ContinuePlayer(Pilot);
        feature.SetMission(3);
        Assert.False(feature.GrantMissionAircraft());
        Assert.Equal(2, feature.Profile!.Planes.Count);

        feature.SetMission(12);
        Assert.True(feature.GrantMissionAircraft());
        var granted = feature.Profile.Planes[2];
        Assert.Equal("Red Hot Spender", granted.Name);
        Assert.Equal(7, granted.Airframe);
        Assert.True(granted.Special);
        Assert.Equal(2, feature.Profile.SelectedPlane);
        Assert.Equal(new[] { 7 }, feature.Profile.GrantedAircraft);
        Assert.Equal(3, store.Load(Pilot)!.Planes.Count);

        // A second entry grants nothing and writes nothing, and one made after the plane screen
        // moved the pilot's pick puts the story aircraft back.
        Assert.False(feature.GrantMissionAircraft());
        feature.Profile.SelectedPlane = 0;
        Assert.True(feature.GrantMissionAircraft());
        Assert.Equal(2, feature.Profile.SelectedPlane);
        Assert.Equal(3, feature.Profile.Planes.Count);
    }

    [Fact]
    public void TheScrapbookEntryCountsEveryOpeningAndTheCaptureResolvesInTheProfileDirectory()
    {
        var (feature, store, _) = Open();
        feature.ContinuePlayer(Pilot);

        feature.EnterScrapbook(2);
        feature.EnterScrapbook(2);
        Assert.Equal(2, feature.MissionSeq);
        Assert.Equal(2, feature.ScrapbookEntry);

        Assert.Null(feature.CapturePath("Snap_01.png"));
        File.WriteAllText(Path.Combine(store.DirFor(Pilot), "Snap_01.png"), "not a png");
        Assert.Equal(Path.Combine(store.DirFor(Pilot), "Snap_01.png"), feature.CapturePath("Snap_01.png"));
        feature.SetScrapbookZoom(3, 1, 2);
        Assert.Equal((3, 1, 2), feature.ZoomTarget);
    }

    [Fact]
    public void WithoutADataRootTheMissionAndTheBriefingReadAsAbsent()
    {
        var (feature, _, _) = Open();
        feature.ContinuePlayer(Pilot);
        feature.SetMission(0);

        Assert.Null(feature.Mission);
        Assert.False(feature.MissionHasWingman);
        Assert.Null(feature.Briefing);
    }

    [Fact]
    public void DiscardDropsEverythingAndTouchesNoFile()
    {
        var (feature, store, dir) = Open();
        feature.ContinuePlayer(Pilot);
        feature.SetMission(4);
        feature.SetAmmoSlot(1);
        feature.SetPlaneSlot(1);
        feature.EnterScrapbook(1);
        feature.Field.SetPlayers(3);
        feature.Field.Advance();
        var files = Snapshot(dir);

        feature.Discard();

        Assert.False(feature.IsOpen);
        Assert.Null(feature.Store);
        Assert.Null(feature.Profile);
        Assert.Empty(feature.Roster);
        Assert.Equal(-1, feature.MissionSeq);
        Assert.Equal(0, feature.AmmoSlot);
        Assert.Equal(0, feature.PlaneSlot);
        Assert.Equal(0, feature.ScrapbookEntry);
        Assert.Equal(1, feature.Field.Players);
        Assert.Equal(0, feature.Field.Current);
        Assert.False(feature.Field.Locked);
        Assert.Equal(files, Snapshot(dir));
        Assert.Equal("No campaign is open.", feature.ContinuePlayer(Pilot));
        Assert.Equal(Pilot, store.LastPlayed);
    }

    [Fact]
    public void OpeningAgainDropsTheSeatedProfileAndReadsTheNewStore()
    {
        var (feature, _, _) = Open();
        feature.ContinuePlayer(Pilot);
        var other = new CampaignProfileStore(Path.Combine(TestData.TempDir(), "Profiles"));
        other.Save(CampaignProfileDef.NewProfile("Nathan"));

        feature.Open(other);

        Assert.Null(feature.Profile);
        Assert.Equal(new[] { "Nathan" }, feature.Roster);
        Assert.Same(other, feature.Store);
    }

    [Fact]
    public void AGuestFliesTheHostsStockHangarAndWritesNothing()
    {
        var (feature, store, dir) = Open();
        feature.ContinuePlayer(Pilot);
        var files = Snapshot(dir);
        ushort hangar = (1 << 5) | (1 << 7);

        feature.OpenGuest("Zachary", 4, hangar);
        Assert.True(feature.IsGuest);
        Assert.True(feature.IsOpen);
        Assert.Null(feature.Store);
        Assert.Equal(new[] { 5, 7 }, feature.Profile!.Planes.ConvertAll(p => p.Airframe));
        Assert.All(feature.Profile.Planes, plane => Assert.False(plane.Special));
        Assert.Equal(5, feature.GuestAirframe);
        Assert.Equal(4, feature.NextMissionSeq);
        Assert.True(feature.ChangePlaneAllowed(0));
        Assert.False(feature.ChangePlaneAllowed(1));

        feature.SetMission(12);
        Assert.False(feature.GrantMissionAircraft());
        feature.Profile.SelectedPlane = 1;
        feature.RecordGuestResult(12, null, 0b11, 900);
        var exit = feature.BuildExit(new IReadOnlyList<int>[] { new[] { 0 }, new[] { 1 } })!;
        Assert.Equal("", exit.Profile);
        Assert.Equal(12, exit.MissionSeq);
        var seat = Assert.Single(exit.Seats);
        Assert.Equal("node7", seat.PlaneNode);
        Assert.Null(seat.Custom);

        var run = CampaignProgression.ResultOf(feature.Profile, 12)!.Latest!;
        Assert.Equal(0b11, run.CompletedMask);
        Assert.Equal(900, run.Money);

        // The host's hangar moving on keeps the pick where the host still holds it, and the results.
        feature.FollowHost(5, (ushort)(hangar | (1 << 2)));
        Assert.Equal(7, feature.GuestAirframe);
        Assert.Equal(3, feature.Profile.Planes.Count);
        Assert.NotNull(CampaignProgression.ResultOf(feature.Profile, 12));
        feature.FollowHost(5, 1 << 2);
        Assert.Equal(2, feature.GuestAirframe);

        // Nothing a guest did reached the disk, and the host's roster still reads as it was.
        Assert.Equal(files, Snapshot(dir));
        Assert.Equal(Pilot, store.LastPlayed);

        // ABLE-TO-FAIL CONTROL: the same exit from a seated profile saves it and names it.
        var (seated, seatedStore, _) = Open();
        seated.ContinuePlayer(Pilot);
        seated.SetMission(3);
        seated.Profile!.Funds = 4321;
        Assert.Equal(Pilot, seated.BuildExit(new IReadOnlyList<int>[] { new[] { 0 } })!.Profile);
        Assert.Equal(4321, seatedStore.Load(Pilot)!.Funds);
    }

    [Fact]
    public void AHostOffersEveryAirframeItsHangarHoldsAndAlwaysTheStarter()
    {
        var profile = CampaignProfileDef.NewProfile(Pilot);
        profile.Planes.Clear();
        profile.Planes.Add(new OwnedPlane { Name = "A", Airframe = 2 });
        profile.Planes.Add(new OwnedPlane { Name = "B", Airframe = 9 });
        Assert.Equal((1 << 2) | (1 << 5) | (1 << 9), CampaignFeature.HangarAirframes(profile));

        // ABLE-TO-FAIL CONTROL: nobody seated offers the starter alone.
        Assert.Equal(1 << 5, CampaignFeature.HangarAirframes(null));
    }

    [Fact]
    public void TheNameRuleIsTheOriginals()
    {
        Assert.True(CampaignFeature.ValidName("Nathan Zachary 2"));
        Assert.False(CampaignFeature.ValidName(string.Empty));
        Assert.False(CampaignFeature.ValidName("Nathan-Zachary"));
        Assert.False(CampaignFeature.ValidName(new string('z', 33)));
        Assert.True(CampaignFeature.ValidName(new string('z', 32)));
    }

    private static (CampaignFeature Feature, CampaignProfileStore Store, string Dir) Open(CustomPlaneStore? planes = null)
    {
        string dir = Path.Combine(TestData.TempDir(), "Profiles");
        var store = new CampaignProfileStore(dir);
        var feature = new CampaignFeature(UiStrings.Empty, airframe => $"node{airframe}");
        feature.Open(store, planes);
        return (feature, store, dir);
    }

    private static string ProfileText(CampaignProfileStore store, string name) =>
        File.ReadAllText(Path.Combine(store.DirFor(name), "profile.json"));

    private static string[] Names(string[] paths)
    {
        var names = new List<string>(paths.Length);
        foreach (string path in paths)
        {
            names.Add(Path.GetFileName(path));
        }

        names.Sort(System.StringComparer.Ordinal);
        return names.ToArray();
    }

    // Every file under the store with its text, so a write anywhere in it shows up as a difference.
    private static Dictionary<string, string> Snapshot(string dir)
    {
        var files = new Dictionary<string, string>();
        foreach (string path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(dir, path)] = File.ReadAllText(path);
        }

        return files;
    }
}
