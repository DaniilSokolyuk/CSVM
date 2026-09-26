using System;
using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>B11's persistence contract: a plane round-trips through its JSON file with every
/// chosen field intact, a missing or malformed file reads as nothing, the list is every loadable
/// plane, and the name is the identity (same name overwrites).</summary>
public class CustomPlaneStoreTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        store.Save(FullDef());

        var loaded = store.Load("Blue Streak");

        Assert.NotNull(loaded);
        var expected = FullDef();
        Assert.Equal(expected.Name, loaded!.Name);
        Assert.Equal(expected.Airframe, loaded.Airframe);
        Assert.Equal(expected.Engine, loaded.Engine);
        Assert.Equal(expected.ArmourNose, loaded.ArmourNose);
        Assert.Equal(expected.ArmourTail, loaded.ArmourTail);
        Assert.Equal(expected.ArmourLeftWing, loaded.ArmourLeftWing);
        Assert.Equal(expected.ArmourRightWing, loaded.ArmourRightWing);
        Assert.Equal(expected.Guns, loaded.Guns);
        Assert.Equal(expected.LeftHardpoints, loaded.LeftHardpoints);
        Assert.Equal(expected.RightHardpoints, loaded.RightHardpoints);
        Assert.Equal(expected.PaintPattern, loaded.PaintPattern);
        Assert.Equal(expected.PaintColours, loaded.PaintColours);
        Assert.Equal(expected.PaintShades, loaded.PaintShades);
        Assert.Equal(expected.NoseDecal, loaded.NoseDecal);
        Assert.Equal(expected.TailDecal, loaded.TailDecal);
        Assert.Equal(expected.WingDecal, loaded.WingDecal);
        Assert.Equal(expected.Colour1, loaded.Colour1);
        Assert.Equal(expected.Colour2, loaded.Colour2);
        Assert.Equal(expected.Colour3, loaded.Colour3);
    }

    /// <summary>Version 1 files still load: their three "pick" dwords were the decal indices all
    /// along, and each free RGB triple lands on the nearest authored swatch, which for a v1 file
    /// written from the shipped-scheme palette is that colour exactly.</summary>
    [Fact]
    public void Deserialize_Version1_MapsPicksToDecalsAndRgbToSwatches()
    {
        string v1 = "{\"version\": 1, \"name\": \"Old Save\", \"airframe\": 7, \"engine\": 3," +
            "\"paint\": {\"pattern\": 11, \"pick1\": 40, \"pick2\": 8, \"pick3\": 7," +
            "\"colour1\": [32, 90, 167], \"colour2\": [255, 255, 255], \"colour3\": [25, 25, 25]}}";

        var def = CustomPlaneStore.Deserialize(v1);

        Assert.NotNull(def);
        Assert.Equal(11, def!.PaintPattern);
        Assert.Equal(40, def.NoseDecal);
        Assert.Equal(8, def.TailDecal);
        Assert.Equal(7, def.WingDecal);
        Assert.Equal(new PaintColour(32, 90, 167), def.Colour1);
        Assert.Equal(new PaintColour(255, 255, 255), def.Colour2);
        Assert.Equal(new PaintColour(25, 25, 25), def.Colour3);

        // Saving it back writes the current schema, so a file upgrades on its next save.
        Assert.Contains("\"version\": 2", CustomPlaneStore.Serialize(def), StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_IsStableAcrossARoundTrip()
    {
        var first = CustomPlaneStore.Serialize(FullDef());
        var reread = CustomPlaneStore.Deserialize(first);

        Assert.NotNull(reread);
        Assert.Equal(first, CustomPlaneStore.Serialize(reread!));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        Assert.Null(store.Load("Never Built"));
    }

    [Fact]
    public void Load_MalformedFile_ReturnsNull()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        var garbled = store.Save(FullDef());
        File.WriteAllText(garbled, "{ not json at all");

        Assert.Null(store.Load("Blue Streak"));
    }

    [Fact]
    public void Load_WrongVersion_ReturnsNull()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        var path = store.Save(FullDef());
        File.WriteAllText(path, CustomPlaneStore.Serialize(FullDef()).Replace("\"version\": 2", "\"version\": 99"));

        Assert.Null(store.Load("Blue Streak"));
    }

    [Fact]
    public void List_SortsByName_AndSkipsTheUnreadable()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        store.Save(new CustomPlaneDef { Name = "Zephyr" });
        store.Save(new CustomPlaneDef { Name = "aardvark" });
        store.Save(new CustomPlaneDef { Name = "Jumping Jane" });
        File.WriteAllText(store.PathFor("Broken"), "not even close");

        var names = store.List();

        Assert.Equal(new[] { "aardvark", "Jumping Jane", "Zephyr" }, names.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void List_MissingDirectory_IsEmpty()
    {
        var store = new CustomPlaneStore(Path.Combine(TestData.TempDir(), "never-created"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Save_SameName_Overwrites()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        store.Save(FullDef());
        var replacement = FullDef();
        replacement.Engine = 5;
        store.Save(replacement);

        Assert.Single(store.List());
        Assert.Equal(5, store.Load("Blue Streak")!.Engine);
    }

    /// <summary>Delete removes the plane of that name and leaves the rest of the hangar alone
    /// (E46, the original's Sell Plane in a build with no economy).</summary>
    [Fact]
    public void Delete_RemovesThatPlaneOnly()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        store.Save(FullDef());
        store.Save(new CustomPlaneDef { Name = "Keeper", Airframe = 2 });

        Assert.True(store.Delete("Blue Streak"));

        Assert.Null(store.Load("Blue Streak"));
        Assert.Equal("Keeper", Assert.Single(store.List()).Name);
    }

    /// <summary>Deleting is sanitised exactly as saving is, so a name a filename cannot carry is
    /// deleted by the same identity it was saved under.</summary>
    [Fact]
    public void Delete_SanitisesTheNameLikeSave()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        store.Save(new CustomPlaneDef { Name = "Red/Blue: One" });

        Assert.True(store.Delete("Red/Blue: One"));
        Assert.Empty(store.List());
    }

    /// <summary>A name with no file is a no-op, not an error: a caller may ask twice.</summary>
    [Fact]
    public void Delete_MissingFile_IsANoOp()
    {
        var store = new CustomPlaneStore(Path.Combine(TestData.TempDir(), "never-created"));

        Assert.False(store.Delete("Nobody"));
        Assert.False(store.Delete("   "));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Save_EmptyName_Throws()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        Assert.Throws<ArgumentException>(() => store.Save(new CustomPlaneDef { Name = "   " }));
    }

    [Fact]
    public void Deserialize_ClampsOutOfRangeValues()
    {
        var json = CustomPlaneStore.Serialize(FullDef())
            .Replace("\"airframe\": 7", "\"airframe\": 99")
            .Replace("\"pattern\": 13", "\"pattern\": -2");

        var def = CustomPlaneStore.Deserialize(json);

        Assert.NotNull(def);
        Assert.Equal(CustomPlaneDef.MaxAirframe, def!.Airframe);
        Assert.Equal(0, def.PaintPattern);
    }

    /// <summary>The campaign's exported loadout round-trips, and is the only thing that puts the
    /// block in the file: a plane the hangar built writes exactly the file it wrote before the
    /// field existed.</summary>
    [Fact]
    public void RoundTrip_ExportedLoadout()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        var def = FullDef();
        def.SetLoadout(new[] { 2, 4, 0, 1 }, new[] { 3, 0, 0, 0, 11, 0, 0, 0 });
        store.Save(def);

        var loaded = store.Load("Blue Streak");

        Assert.NotNull(loaded);
        Assert.True(loaded!.HasLoadout);
        Assert.Equal(new[] { 2, 4, 0, 1 }, loaded.Ammo);
        Assert.Equal(new[] { 3, 0, 0, 0, 11, 0, 0, 0 }, loaded.Ordnance);
        Assert.DoesNotContain("loadout", CustomPlaneStore.Serialize(FullDef()), StringComparison.Ordinal);
    }

    /// <summary>The field is optional: a file written before it existed loads and reads exactly as
    /// it did, with nothing picked on any gun or pylon.</summary>
    [Fact]
    public void Deserialize_WithoutTheLoadoutBlock_PicksNothing()
    {
        var def = CustomPlaneStore.Deserialize(CustomPlaneStore.Serialize(FullDef()));

        Assert.NotNull(def);
        Assert.False(def!.HasLoadout);
        Assert.Equal(
            new[] { CustomPlaneDef.NoAmmoPick, CustomPlaneDef.NoAmmoPick, CustomPlaneDef.NoAmmoPick, CustomPlaneDef.NoAmmoPick },
            def.Ammo);
        Assert.All(def.Ordnance, cell => Assert.Equal(CustomPlaneDef.NoOrdnancePick, cell));
    }

    /// <summary>The export marker round-trips, and only a plane still waiting for EXPORT puts it in
    /// the file: a plane the campaign has exported, and every plane built at a wallet-free door,
    /// writes exactly the file they wrote before the field existed.</summary>
    [Fact]
    public void RoundTrip_AwaitingExport()
    {
        var store = new CustomPlaneStore(TestData.TempDir());
        var def = FullDef();
        def.AwaitingExport = true;
        store.Save(def);

        var loaded = store.Load("Blue Streak");

        Assert.NotNull(loaded);
        Assert.True(loaded!.AwaitingExport);
        Assert.Contains("\"awaitingExport\": true", CustomPlaneStore.Serialize(def), StringComparison.Ordinal);
        Assert.DoesNotContain("awaitingExport", CustomPlaneStore.Serialize(FullDef()), StringComparison.Ordinal);
    }

    /// <summary>The marker is optional and its absence means exported. A file written before the
    /// field existed must keep reading that way, or every build already on a player's disk would
    /// drop out of the Instant Action and Free Flight lists on an update.</summary>
    [Fact]
    public void Deserialize_WithoutTheMarker_ReadsAsExported()
    {
        const string preMarker = """
            {
              "version": 2,
              "name": "Blue Streak",
              "airframe": 3,
              "engine": 4
            }
            """;

        var def = CustomPlaneStore.Deserialize(preMarker);

        Assert.NotNull(def);
        Assert.False(def!.AwaitingExport);
        Assert.Equal("Blue Streak", def.Name);
    }

    [Fact]
    public void Constructor_RelativeDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CustomPlaneStore("Planes"));
    }

    /// <summary>A plane name is user text and becomes a filename, so a name carrying separators or
    /// a parent-directory hop must still resolve inside the store. Every campaign ownership record
    /// is looked up by this key too, which is the other way such a name reaches the disk.</summary>
    [Fact]
    public void PathFor_KeepsEveryNameInsideTheStore()
    {
        var dir = TestData.TempDir();
        var store = new CustomPlaneStore(dir);
        string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;

        foreach (var name in new[] { "..", ".", "../../pwned", @"..\..\pwned", "C:/pwned", "a/b" })
        {
            var full = Path.GetFullPath(store.PathFor(name));
            Assert.StartsWith(root, full, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(root, Path.GetDirectoryName(full) + Path.DirectorySeparatorChar);
        }
    }

    private static CustomPlaneDef FullDef() => new()
    {
        Name = "Blue Streak",
        Airframe = 7,
        Engine = 3,
        ArmourNose = 4,
        ArmourTail = 1,
        ArmourLeftWing = 12,
        ArmourRightWing = 0,
        LeftHardpoints = 2,
        RightHardpoints = 4,
        PaintPattern = 13,
        NoseDecal = 40,
        TailDecal = 8,
        WingDecal = 7,

        // fortune's own defaults: red, the darkest shade of the white ramp, white.
        PaintColours = { [0] = 1, [1] = 26, [2] = 26 },
        PaintShades = { [0] = 8, [1] = 0, [2] = 9 },
        Guns =
        {
            [0] = new GunChoice(2, Twin: true),
            [1] = new GunChoice(0, Twin: false),
            [2] = new GunChoice(null, Twin: false),
            [3] = new GunChoice(4, Twin: true),
        },
    };
}
