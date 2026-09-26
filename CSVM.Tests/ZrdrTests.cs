using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The alternating key/list convention every zrdr reader is built on
/// (<c>docs/formats/README.md</c> "Shared conventions") plus the archive lookup's two shapes.
/// Input is <c>fixtures/zrdr/shapes.json</c>, hand-authored to carry one instance of each shape
/// the convention allows.
/// </summary>
[Trait("Tier", "Quick")]
public class ZrdrTests
{
    private static string FixtureDir => TestData.Fixture("zrdr");

    [Fact]
    public void EveryNumberArrivesAsFloat()
    {
        // "PARTICLES 100" parses as 100.0, mech3ax emits every scalar through one
        // single-precision path, so a reader must never expect an int.
        var probe = ProbeBlock();
        Assert.IsType<float>(Assert.IsType<List<object?>>(probe.List("PAIR"))[0]);
        Assert.Equal(3f, probe.Float("PAIR"));
        Assert.Equal(7f, probe.Float("PAIR", index: 1));
    }

    [Fact]
    public void KeyFollowedByAnotherKeyIsABareFlag()
    {
        var probe = ProbeBlock();
        Assert.True(probe.Has("IS_AUTOGYRO"));
        Assert.Empty(Assert.IsType<List<object?>>(probe.List("IS_AUTOGYRO")));
        Assert.False(probe.TryFloat("IS_AUTOGYRO", out _));
    }

    [Fact]
    public void KeyFollowedByNullIsABareFlag()
    {
        var probe = ProbeBlock();
        Assert.True(probe.Has("NULL_FLAG"));
        Assert.Empty(Assert.IsType<List<object?>>(probe.List("NULL_FLAG")));
    }

    [Fact]
    public void KeyAtTheEndOfTheListIsABareFlag()
    {
        Assert.True(ProbeBlock().Has("TRAILING_FLAG"));
    }

    [Fact]
    public void AStrayValueIsSkippedRatherThanShiftingTheKeyPairing()
    {
        Assert.Equal("ok", ProbeBlock().Str("AFTER_STRAY"));
    }

    [Fact]
    public void DuplicateKeysCollapseToTheLastOccurrence()
    {
        // The collapse is why families with meaningful duplicates (SEQUENCE_DEFINITION,
        // repeated ops) walk the raw list instead of using this view.
        Assert.Equal(999f, ProbeBlock().Float("SPEED"));
    }

    [Fact]
    public void KeysAreCaseInsensitive()
    {
        Assert.True(ProbeBlock().Has("speed"));
    }

    [Fact]
    public void ANestedValueListIsItselfAnAlternatingDict()
    {
        var nested = Assert.IsType<ZrdrDict>(ProbeBlock().Dict("NESTED"));
        Assert.Equal(7f, nested.Float("INNER_COUNT"));
        Assert.Equal("deeper", nested.Str("INNER_LABEL"));
        Assert.True(nested.Has("INNER_FLAG"));
    }

    [Fact]
    public void KeysListsEveryKeyIncludingBareFlags()
    {
        // The unhandled-key tripwire in WeaponDefs is computed against this.
        var keys = ProbeBlock().Keys;
        Assert.Contains("IS_AUTOGYRO", keys);
        Assert.Contains("NULL_FLAG", keys);
        Assert.Contains("TRAILING_FLAG", keys);
        Assert.DoesNotContain("MISSING_KEY", keys);
    }

    [Fact]
    public void AnAbsentKeyYieldsTheFallbackNotAThrow()
    {
        var probe = ProbeBlock();
        Assert.Equal(42f, probe.Float("MISSING_KEY", 42f));
        Assert.Null(probe.Str("MISSING_KEY"));
        Assert.Null(probe.List("MISSING_KEY"));
        Assert.Null(probe.Dict("MISSING_KEY"));
        Assert.False(probe.Has("MISSING_KEY"));
    }

    [Fact]
    public void ColourTriplesKeepTheirEncodingForTheCallerToDecide()
    {
        // Normalized floats and integer 0-255 coexist; the "divide by 255 iff any component
        // is strictly > 1" rule is the consumer's, so the reader must hand both through intact.
        var probe = ProbeBlock();
        Assert.Equal(1.0f, probe.Float("COLOR_FLOAT", index: 2));
        Assert.Equal(255f, probe.Float("COLOR_BYTE", index: 2));
    }

    [Fact]
    public void TheForksZrdJsonNameIsAcceptedForTheSameRequest()
    {
        // mech3ax v0.6.1 wrote "vehicle.json"; the fork writes "vehicle.zrd.json".
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "probe.zrd.json"), "[[\"KEY\", [1]]]");
        var root = Zrdr.LoadFile(dir, "probe.json");
        Assert.Equal(1f, ZrdrDict.FromAlternating(Assert.IsType<List<object?>>(root[0])).Float("KEY"));
    }

    [Fact]
    public void AZipAndADirectoryOfTheSameFilesReadIdentically()
    {
        var dir = TestData.TempDir();
        var zipPath = Path.Combine(dir, "zrdr.archive");
        ZipFile.CreateFromDirectory(FixtureDir, zipPath);

        var fromDir = WeaponDefs.Load(FixtureDir);
        var fromZip = WeaponDefs.Load(zipPath);
        Assert.Equal(fromDir.All.Count, fromZip.All.Count);
        Assert.Equal(fromDir.EmptyClipSound, fromZip.EmptyClipSound);
    }

    [Fact]
    public void AnAbsentReaderFileThrowsRatherThanReturningEmpty()
    {
        Assert.Throws<FileNotFoundException>(() => Zrdr.LoadFile(FixtureDir, "not_a_reader.json"));
    }

    [Fact]
    public void LoadMatchingFilesSniffsContentBeforeParsing()
    {
        var anim = new List<string>();
        foreach (var (name, _) in Zrdr.LoadMatchingFiles(FixtureDir, "ANIMATION_DEFINITIONS"))
        {
            anim.Add(name);
        }
        Assert.Equal(new[] { "demo_anims.json" }, anim);

        var ballistics = new List<string>();
        foreach (var (name, _) in Zrdr.LoadMatchingFiles(FixtureDir, "BALLISTICS"))
        {
            ballistics.Add(name);
        }
        Assert.Equal(new[] { "weapons.json" }, ballistics);
    }

    private static ZrdrDict ProbeBlock()
    {
        var root = Zrdr.LoadFile(FixtureDir, "shapes.json");
        var outer = ZrdrDict.FromAlternating(Assert.IsType<List<object?>>(root[0]));
        return Assert.IsType<ZrdrDict>(outer.Dict("PROBE_BLOCK"));
    }
}
