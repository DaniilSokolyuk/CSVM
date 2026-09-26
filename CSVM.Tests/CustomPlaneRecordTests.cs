using System;
using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The 204-byte importer against seven genuine saves from the user's own installs
/// (<c>fixtures/planes204/</c>, original filenames kept). The four Fury fixtures were saved in
/// the original's paint UI wearing its four shipped Fury patterns, so their colour triples are
/// asserted as the files hold them; where they differ from the vehicle.json scheme table the
/// difference is intentional, not a bug here.
/// </summary>
public class CustomPlaneRecordTests
{
    private static readonly string[] FixtureNames =
    [
        "A", "B", "Blue Streak", "Fury BlackSwan", "Fury Fortune", "Fury Hughes", "Fury Studio Sec",
    ];

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("Blue Streak")]
    [InlineData("Fury BlackSwan")]
    [InlineData("Fury Fortune")]
    [InlineData("Fury Hughes")]
    [InlineData("Fury Studio Sec")]
    public void EveryFixture_Parses_NameMatchesFilename_AirframeInRange(string name)
    {
        var def = ReadFixture(name);
        Assert.Equal(name, def.Name);
        Assert.InRange(def.Airframe, 0, CustomPlaneDef.MaxAirframe);
    }

    [Fact]
    public void BlueStreak_ReadsEveryDecodedField()
    {
        var def = ReadFixture("Blue Streak");
        Assert.Equal("Blue Streak", def.Name);
        Assert.Equal(3, def.Airframe);
        Assert.Equal(4, def.Engine);
        Assert.Equal(1, def.LeftHardpoints);
        Assert.Equal(1, def.RightHardpoints);
        Assert.Equal(4, def.PaintPattern);
        Assert.Equal(40, def.NoseDecal);   // 40ohSoBlue, nose art
        Assert.Equal(8, def.TailDecal);    // 08Fhunter_logo2
        Assert.Equal(7, def.WingDecal);    // 07Fhunter_logo1
        Assert.Equal(new[] { 1, 26, 26 }, def.PaintColours);
        Assert.Equal(new[] { 8, 0, 9 }, def.PaintShades);
        Assert.Equal(new PaintColour(223, 0, 41), def.Colour1);
        Assert.Equal(new PaintColour(25, 25, 25), def.Colour2);
        Assert.Equal(new PaintColour(255, 255, 255), def.Colour3);
        Assert.Equal(4, def.ArmourNose);
        Assert.Equal(4, def.ArmourTail);
        Assert.Equal(4, def.ArmourLeftWing);
        Assert.Equal(4, def.ArmourRightWing);
        Assert.Equal(new GunChoice(1, Twin: true), def.Guns[0]);
        Assert.Equal(new GunChoice(0, Twin: true), def.Guns[1]);
        Assert.Equal(new GunChoice(null, Twin: false), def.Guns[2]);
        Assert.Equal(new GunChoice(null, Twin: false), def.Guns[3]);
    }

    [Fact]
    public void FuryFixtures_AllCarryTheFuryAirframe()
    {
        foreach (var name in FixtureNames.Where(n => n.StartsWith("Fury", StringComparison.Ordinal)))
        {
            Assert.Equal(7, ReadFixture(name).Airframe);
        }
    }

    // The colour triples as the four Fury saves actually hold them. Against the vehicle.json
    // scheme table: blckswan and every slot-1 colour match exactly; each scheme's (0,0,0) slot is
    // saved as (25,25,25), the paint UI's darkest picker shade.
    [Theory]
    [InlineData("Fury BlackSwan", 1, 23, 23, 21, 48, 47, 39, 196, 193, 186)]
    [InlineData("Fury Fortune", 4, 223, 0, 41, 25, 25, 25, 255, 255, 255)]
    [InlineData("Fury Hughes", 6, 243, 194, 0, 25, 25, 25, 255, 255, 255)]
    [InlineData("Fury Studio Sec", 11, 32, 90, 167, 255, 255, 255, 25, 25, 25)]
    public void FuryFixtures_ReadTheirPatternAndColours(
        string name, int pattern, int r1, int g1, int b1, int r2, int g2, int b2, int r3, int g3, int b3)
    {
        var def = ReadFixture(name);
        Assert.Equal(pattern, def.PaintPattern);
        Assert.Equal(new PaintColour((byte)r1, (byte)g1, (byte)b1), def.Colour1);
        Assert.Equal(new PaintColour((byte)r2, (byte)g2, (byte)b2), def.Colour2);
        Assert.Equal(new PaintColour((byte)r3, (byte)g3, (byte)b3), def.Colour3);
    }

    /// <summary>The colour and shade indices resolve to exactly the RGBA the record caches at
    /// +0x68, on every fixture and every slot. That cache is the engine's own output (FUN_00406840
    /// recomputes it from the indices before each save), so this is the swatch table, the resolver
    /// and the importer checked against the original in one assertion.</summary>
    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("Blue Streak")]
    [InlineData("Fury BlackSwan")]
    [InlineData("Fury Fortune")]
    [InlineData("Fury Hughes")]
    [InlineData("Fury Studio Sec")]
    public void ResolvedColoursMatchTheRecordsOwnCache(string name)
    {
        var bytes = File.ReadAllBytes(TestData.Fixture("planes204", name));
        var def = ReadFixture(name);

        for (int slot = 0; slot < 3; slot++)
        {
            Assert.Equal(CustomPlaneRecord.StoredColour(bytes, slot), def.PaintColourAt(slot));
        }
    }

    /// <summary>Every fixture's stored index pairs are its pattern's own six defaults, which is
    /// the pattern table at 0x0061daf0 checked against seven genuine saves.</summary>
    [Theory]
    [InlineData("Fury BlackSwan", 1)]
    [InlineData("Fury Fortune", 4)]
    [InlineData("Fury Hughes", 6)]
    [InlineData("Fury Studio Sec", 11)]
    public void FixtureIndicesAreTheirPatternsDefaults(string name, int pattern)
    {
        var def = ReadFixture(name);
        var entry = HangarPaintTables.Default.PatternEntry(pattern);

        Assert.NotNull(entry);
        Assert.Equal(entry!.Colours, def.PaintColours);
        Assert.Equal(entry.Shades, def.PaintShades);
    }

    // Fixture B's +0x84 dword is 0xc3: bits 0-1 are its two twin mounts, bits 6-7 are stray
    // (its slots 2-3 are empty). Only the per-slot bit may be read.
    [Fact]
    public void TwinBits_ReadPerSlot_IgnoringStrayHighBits()
    {
        var def = ReadFixture("B");
        Assert.Equal(new GunChoice(2, Twin: true), def.Guns[0]);
        Assert.Equal(new GunChoice(2, Twin: true), def.Guns[1]);
        Assert.Equal(new GunChoice(null, Twin: false), def.Guns[2]);
        Assert.Equal(new GunChoice(null, Twin: false), def.Guns[3]);
        Assert.Equal(12, def.ArmourNose);
        Assert.Equal(12, def.ArmourRightWing);
    }

    [Fact]
    public void Read_TooShort_IsNull()
    {
        Assert.Null(CustomPlaneRecord.Read(Array.Empty<byte>()));
        Assert.Null(CustomPlaneRecord.Read(new byte[CustomPlaneRecord.Length - 1]));
    }

    [Fact]
    public void Read_EmptyName_IsNull()
    {
        Assert.Null(CustomPlaneRecord.Read(new byte[CustomPlaneRecord.Length]));
    }

    [Fact]
    public void Read_OutOfRangeField_IsNull()
    {
        var bytes = File.ReadAllBytes(TestData.Fixture("planes204", "Blue Streak"));
        bytes[0x2c] = 0x40;
        Assert.Null(CustomPlaneRecord.Read(bytes));
    }

    [Fact]
    public void ReadFile_MissingFile_IsNull()
    {
        Assert.Null(CustomPlaneRecord.ReadFile(TestData.Fixture("planes204", "No Such Plane")));
    }

    [Fact]
    public void ImportDirectory_ReadsEveryFixture_SortedByName()
    {
        var planes = CustomPlaneRecord.ImportDirectory(TestData.Fixture("planes204"));
        Assert.Equal(FixtureNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), planes.Select(p => p.Name));
    }

    [Fact]
    public void ImportDirectory_MissingDirectory_IsEmpty()
    {
        Assert.Empty(CustomPlaneRecord.ImportDirectory(TestData.Fixture("planes204", "absent-subdir")));
    }

    [Fact]
    public void ImportDirectory_RelativePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => CustomPlaneRecord.ImportDirectory("Planes"));
    }

    private static CustomPlaneDef ReadFixture(string name)
    {
        var def = CustomPlaneRecord.ReadFile(TestData.Fixture("planes204", name));
        Assert.NotNull(def);
        return def;
    }
}
