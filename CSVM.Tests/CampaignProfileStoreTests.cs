using System;
using System.IO;
using System.Linq;
using CSVM.Session.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The persistence contract: a fresh profile starts with the traced $0 + two Devastators,
/// a profile round-trips through its JSON file with every field intact even across a simulated
/// relaunch (a second store instance over the same directory), a missing or malformed file reads
/// as nothing, and deleting a profile never touches anything outside its own directory.</summary>
public class CampaignProfileStoreTests
{
    [Fact]
    public void NewProfile_StartsWithZeroFundsAndTheTwoStarters()
    {
        var def = CampaignProfileDef.NewProfile("Zachary");

        Assert.Equal(0, def.Funds);
        Assert.Equal(0, def.MissionsCompleted);
        Assert.Equal(new[] { "Gypsy Magic", "The Knave" }, def.Planes.Select(p => p.Name).ToArray());
        Assert.All(def.Planes, p => Assert.Equal(5, p.Airframe));
    }

    /// <summary>Create, write, relaunch, read: a second store instance over the same directory
    /// (standing in for a process restart against the same <c>user://</c>) sees exactly what the
    /// first instance wrote: funds, the owned-plane fit and a recorded mission result.</summary>
    [Fact]
    public void RoundTrip_SurvivesASimulatedRelaunch()
    {
        var dir = TestData.TempDir();
        var first = new CampaignProfileStore(dir);
        var def = CampaignProfileDef.NewProfile("Zachary");
        def.Planes[0].Ammo[0] = 2;
        def.Planes[0].Ordnance[0] = 11;
        def.Funds = 900;
        def.MissionsCompleted = 1;
        def.Memento = "MS_P_Mom.jpg";
        def.GrantedAircraft.Add(2);
        def.PersistLog.Merge(6, 2, new[] { new PersistedObject(412, "susp_bridge", "rope1", true, 0f) });
        def.MissionResults.Add(new MissionResult
        {
            Seq = 0,
            Attempts = 3,
            Latest = new MissionRun { CompletedMask = 1, TimeMs = 45000, PlaneName = "Gypsy Magic" },
            Best = new MissionRun
            {
                CompletedMask = 1,
                BestAttemptMask = 1,
                TimeMs = 45000,
                Shots = 120,
                Hits = 30,
                Money = 900,
                Airframe = 5,
                PlaneName = "Gypsy Magic",
            },
        });
        first.Save(def);

        var relaunched = new CampaignProfileStore(dir);
        var loaded = relaunched.Load("Zachary");

        Assert.NotNull(loaded);
        Assert.Equal(900, loaded!.Funds);
        Assert.Equal(1, loaded.MissionsCompleted);
        Assert.Equal("MS_P_Mom.jpg", loaded.Memento);
        Assert.Equal(2, loaded.Planes[0].Ammo[0]);
        Assert.Equal(11, loaded.Planes[0].Ordnance[0]);
        var result = Assert.Single(loaded.MissionResults);
        Assert.Equal(0, result.Seq);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(1, result.Best.CompletedMask);
        Assert.Equal(1, result.Best.BestAttemptMask);
        Assert.Equal(45000, result.Best.TimeMs);
        Assert.Equal(120, result.Best.Shots);
        Assert.Equal("Gypsy Magic", result.Latest.PlaneName);
        Assert.Equal(new[] { 2 }, loaded.GrantedAircraft.ToArray());
        var persisted = Assert.Single(loaded.PersistLog.For(6));
        Assert.Equal(412, persisted.Node);
        Assert.True(persisted.Destroyed);
        Assert.Equal("susp_bridge", persisted.Def);
        Assert.Equal(2, persisted.Seq);
    }

    [Fact]
    public void Serialize_IsStableAcrossARoundTrip()
    {
        var first = CampaignProfileStore.Serialize(CampaignProfileDef.NewProfile("Zachary"));
        var reread = CampaignProfileStore.Deserialize(first);

        Assert.NotNull(reread);
        Assert.Equal(first, CampaignProfileStore.Serialize(reread!));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new CampaignProfileStore(TestData.TempDir());
        Assert.Null(store.Load("Nobody"));
    }

    [Fact]
    public void Load_MalformedFile_ReturnsNull()
    {
        var store = new CampaignProfileStore(TestData.TempDir());
        var path = store.Save(CampaignProfileDef.NewProfile("Zachary"));
        File.WriteAllText(path, "{ not json at all");

        Assert.Null(store.Load("Zachary"));
    }

    [Fact]
    public void Load_WrongVersion_ReturnsNull()
    {
        var store = new CampaignProfileStore(TestData.TempDir());
        var path = store.Save(CampaignProfileDef.NewProfile("Zachary"));
        File.WriteAllText(path, CampaignProfileStore.Serialize(CampaignProfileDef.NewProfile("Zachary"))
            .Replace($"\"version\": {CampaignProfileStore.Version}", "\"version\": 99"));

        Assert.Null(store.Load("Zachary"));
    }

    /// <summary>The three ways <c>Load</c>'s single null arises are told apart, and each answer
    /// names the file it looked at: a profile written to an older schema is on disk and readable,
    /// so reporting it as absent is what sends a reader looking for a path problem.</summary>
    [Fact]
    public void LoadProblem_TellsAnAbsentProfileFromARefusedOne()
    {
        var store = new CampaignProfileStore(TestData.TempDir());
        Assert.StartsWith("no such profile", store.LoadProblem("Nobody"), StringComparison.Ordinal);

        var path = store.Save(CampaignProfileDef.NewProfile("Zachary"));
        Assert.Equal(string.Empty, store.LoadProblem("Zachary"));

        File.WriteAllText(path, CampaignProfileStore.Serialize(CampaignProfileDef.NewProfile("Zachary"))
            .Replace($"\"version\": {CampaignProfileStore.Version}", "\"version\": 2"));
        Assert.Null(store.Load("Zachary"));
        var refused = store.LoadProblem("Zachary");
        Assert.Contains("schema version 2", refused, StringComparison.Ordinal);
        Assert.Contains($"reads version {CampaignProfileStore.Version}", refused, StringComparison.Ordinal);
        Assert.Contains(path, refused, StringComparison.Ordinal);

        File.WriteAllText(path, "{ not json at all");
        Assert.Contains("not readable", store.LoadProblem("Zachary"), StringComparison.Ordinal);
    }

    [Fact]
    public void List_SortsByName_AndSkipsTheUnreadable()
    {
        var store = new CampaignProfileStore(TestData.TempDir());
        store.Save(CampaignProfileDef.NewProfile("Zephyr"));
        store.Save(CampaignProfileDef.NewProfile("aardvark"));
        Directory.CreateDirectory(store.DirFor("Broken"));
        File.WriteAllText(Path.Combine(store.DirFor("Broken"), "profile.json"), "not even close");

        Assert.Equal(new[] { "aardvark", "Zephyr" }, store.List());
    }

    [Fact]
    public void List_MissingDirectory_IsEmpty()
    {
        var store = new CampaignProfileStore(Path.Combine(TestData.TempDir(), "never-created"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Save_SameName_Overwrites()
    {
        var store = new CampaignProfileStore(TestData.TempDir());
        store.Save(CampaignProfileDef.NewProfile("Zachary"));
        var replacement = CampaignProfileDef.NewProfile("Zachary");
        replacement.Funds = 5000;
        store.Save(replacement);

        Assert.Single(store.List());
        Assert.Equal(5000, store.Load("Zachary")!.Funds);
    }

    /// <summary>Deleting a profile removes only its own directory, proven here by a sentinel file
    /// dropped next to (never inside) the deleted profile's directory, standing in for the global
    /// <c>user://Planes/</c> store a real deletion must never touch.</summary>
    [Fact]
    public void Delete_RemovesOnlyThatProfilesDirectory()
    {
        var dir = TestData.TempDir();
        var store = new CampaignProfileStore(dir);
        store.Save(CampaignProfileDef.NewProfile("Zachary"));
        store.Save(CampaignProfileDef.NewProfile("Keeper"));
        var sentinel = Path.Combine(dir, "not-a-profile.txt");
        File.WriteAllText(sentinel, "hangar planes live outside a profile's directory");

        Assert.True(store.Delete("Zachary"));

        Assert.Null(store.Load("Zachary"));
        Assert.Equal("Keeper", Assert.Single(store.List()));
        Assert.True(File.Exists(sentinel));
    }

    /// <summary>Deleting is sanitised exactly as saving is, so a name a directory cannot carry is
    /// deleted by the same identity it was saved under.</summary>
    [Fact]
    public void Delete_SanitisesTheNameLikeSave()
    {
        var store = new CampaignProfileStore(TestData.TempDir());
        store.Save(CampaignProfileDef.NewProfile("Red/Blue: One"));

        Assert.True(store.Delete("Red/Blue: One"));
        Assert.Empty(store.List());
    }

    /// <summary>A name with no directory is a no-op, not an error: a caller may ask twice.</summary>
    [Fact]
    public void Delete_MissingProfile_IsANoOp()
    {
        var store = new CampaignProfileStore(Path.Combine(TestData.TempDir(), "never-created"));

        Assert.False(store.Delete("Nobody"));
        Assert.False(store.Delete("   "));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Save_EmptyName_Throws()
    {
        var store = new CampaignProfileStore(TestData.TempDir());
        Assert.Throws<ArgumentException>(() => store.Save(new CampaignProfileDef { Name = "   " }));
    }

    /// <summary>"." is the store's own root and ".." its parent, so a dot-only name must never
    /// reach the filesystem: Save refuses it, and Delete returns false instead of recursively
    /// removing every profile (or the store's parent directory).</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" .. ")]
    [InlineData("...")]
    public void DotOnlyNames_AreRejectedEverywhere(string name)
    {
        var dir = TestData.TempDir();
        var store = new CampaignProfileStore(dir);
        store.Save(CampaignProfileDef.NewProfile("Zachary"));

        Assert.Throws<ArgumentException>(() => store.Save(new CampaignProfileDef { Name = name }));
        Assert.False(store.Delete(name));
        Assert.Null(store.Load(name));
        Assert.True(Directory.Exists(Path.Combine(dir, "Zachary")));
    }

    /// <summary>The last-played record is a name beside the profile directories, so it survives a
    /// second store instance, an empty name clears it, and an unreadable file reads as none rather
    /// than throwing. It is never a row: <see cref="CampaignProfileStore.List"/> sorts.</summary>
    [Fact]
    public void LastPlayed_RoundTripsClearsAndToleratesRubbish()
    {
        var dir = TestData.TempDir();
        var store = new CampaignProfileStore(dir);
        Assert.Equal(string.Empty, store.LastPlayed);

        store.RecordLastPlayed("Nathan");
        Assert.Equal("Nathan", new CampaignProfileStore(dir).LastPlayed);
        Assert.Empty(store.List()); // the record is not a profile

        store.RecordLastPlayed(string.Empty);
        Assert.Equal(string.Empty, new CampaignProfileStore(dir).LastPlayed);

        File.WriteAllText(Path.Combine(dir, "last-played.json"), "not json at all");
        Assert.Equal(string.Empty, new CampaignProfileStore(dir).LastPlayed);
    }

    /// <summary>Deleting the recorded profile forgets it, so nothing points at a name the store no
    /// longer carries; deleting a different one leaves the record standing.</summary>
    [Fact]
    public void Delete_ClearsTheRecordOnlyForTheProfileItRemoves()
    {
        var dir = TestData.TempDir();
        var store = new CampaignProfileStore(dir);
        store.Save(CampaignProfileDef.NewProfile("Nathan"));
        store.Save(CampaignProfileDef.NewProfile("Zachary"));
        store.RecordLastPlayed("Nathan");

        store.Delete("Zachary");
        Assert.Equal("Nathan", store.LastPlayed);

        store.Delete("Nathan");
        Assert.Equal(string.Empty, store.LastPlayed);
    }

    /// <summary>The name a sortie outside a campaign flies under, which is the one its HUD kill
    /// line prints when that pilot is shot down: the profile last used, resolved through the
    /// profile itself, so a store that has recorded nobody and a record whose profile is gone both
    /// read as none and leave the line its unnamed fall-through.</summary>
    [Fact]
    public void LastPlayedPilotName_IsTheProfileLastUsedAndNoneWithoutOne()
    {
        var dir = TestData.TempDir();
        var store = new CampaignProfileStore(dir);
        Assert.Null(store.LastPlayedPilotName);

        store.Save(CampaignProfileDef.NewProfile("Nathan Zachary"));
        Assert.Null(store.LastPlayedPilotName); // saved, but nobody has been seated yet

        store.RecordLastPlayed("Nathan Zachary");
        Assert.Equal("Nathan Zachary", store.LastPlayedPilotName);
        Assert.Equal("Nathan Zachary", new CampaignProfileStore(dir).LastPlayedPilotName);

        Directory.Delete(store.DirFor("Nathan Zachary"), recursive: true);
        Assert.Equal("Nathan Zachary", store.LastPlayed);
        Assert.Null(store.LastPlayedPilotName);
    }
}
