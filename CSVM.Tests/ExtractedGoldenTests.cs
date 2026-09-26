using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Golden invariants over the player's own extraction. The retail install is a fixed input, so
/// these counts and structural facts are stable, and a change to either the data or a reader
/// moves them; only numbers and shapes are recorded here, never extracted content.
/// Editing a number is deliberate: establish first whether a reader changed behaviour or the
/// extraction was rebuilt. Every test is <see cref="ExtractedDataFactAttribute"/> or
/// <see cref="ExtractedDataTheoryAttribute"/>, so a checkout without <c>extracted/</c> skips.
/// </summary>
public class ExtractedGoldenTests
{
    /// <summary>The eight chapter worlds and the number of readers each yields. C1C and C2B ship
    /// one file more than this (29 and 26): their <c>templates</c> reader extracts to a literal
    /// JSON <c>null</c>, which carries no list and so is not a reader.</summary>
    public static TheoryData<string, int> ChapterReaderCounts => new()
    {
        { "C1", 71 },
        { "C1B", 30 },
        { "C1C", 28 },
        { "C2", 71 },
        { "C2B", 25 },
        { "C3", 57 },
        { "C4", 58 },
        { "C5", 57 },
    };

    /// <summary>Every player airframe's shipped chase distance from <c>camparam.json</c>. Seven
    /// carry a block of their own; the four that do not (Devastator, Hoplite, Hellhound, Brigand)
    /// take the 13.0 default, and are listed precisely so a future block appearing for one of them
    /// shows up as a failure rather than passing unnoticed.</summary>
    public static TheoryData<string, float> ShippedChaseDistances => new()
    {
        { "player_bhawk", 18.5f },
        { "player_fury", 17.0f },
        { "player_peacemaker", 18.0f },
        { "player_kestrel", 14.5f },
        { "player_fbrand", 20.5f },
        { "player_warhawk", 20.0f },
        { "player_balmoral", 25.0f },
        { "player_pfighter", 13.0f },
        { "player_autogyro", 13.0f },
        { "player_avenger", 13.0f },
        { "player_brigand", 13.0f },
    };

    private static string Extracted => TestData.ExtractedRoot!;
    private static string DataRoot => TestData.DataRoot!;

    private static string SharedZrdr =>
        SessionPaths.PreferUnzipped(Path.Combine(Extracted, "zrdr.zip"));

    private static string Planes =>
        SessionPaths.PreferUnzipped(Path.Combine(Extracted, "planes.zip"));

    [ExtractedDataFact]
    public void TheSharedZrdrScopeShipsItsFullReaderSet()
    {
        // 222 files on disk; dlgMessage and mp_dialog extract to a literal JSON null and
        // therefore yield no reader list, which is why the loader sees 220.
        Assert.Equal(220, CountReaders(SharedZrdr));
    }

    [ExtractedDataTheory]
    [MemberData(nameof(ChapterReaderCounts))]
    public void EachChapterShipsItsOwnReaderSet(string chapter, int expected)
    {
        Assert.Equal(expected, CountReaders(SessionPaths.ChapterZrdr(DataRoot, chapter)));
    }

    [ExtractedDataFact]
    public void TheWeaponCatalogueHasItsFortyEightEntriesAndNoUnknownKeys()
    {
        var defs = WeaponDefs.Load(SharedZrdr);
        Assert.Equal(48, defs.All.Count);
        Assert.Equal("snd_emptyclip", defs.EmptyClipSound);

        var ids = new HashSet<string>();
        foreach (var def in defs.All)
        {
            Assert.True(ids.Add(def.Id), $"duplicate weapon id {def.Id}");
            Assert.NotEqual("", def.Name);
            Assert.NotEqual("", def.DescKey);
            // The tripwire: a key the reader has not learned means the data grew one.
            Assert.Empty(def.UnhandledKeys);
        }
    }

    [ExtractedDataFact]
    public void EveryStockLoadoutResolvesToWeaponsTheCatalogueCarries()
    {
        // The half of Loadout.Bind that needs no built plane: caliber+ammo and the pylon stock
        // must name real wep_* entries, or a bind throws at runtime.
        var weapons = WeaponDefs.Load(SharedZrdr);
        var loadouts = StockLoadouts.Load(
            Path.Combine(TestData.RepoRoot, "CSVM", "data", "stock_loadouts.json"));
        foreach (var (def, loadout) in loadouts.All)
        {
            foreach (var gun in loadout.Guns)
            {
                var id = StockLoadouts.GunWeaponId(gun.Caliber, gun.Ammo);
                Assert.True(weapons.Get(id) != null, $"{def} slot {gun.Slot}: {id} not in weapons.json");
            }
            if (loadout.Hardpoints is { } hp)
            {
                foreach (var stock in hp.Stock)
                {
                    Assert.True(weapons.Get(stock) != null,
                        $"{def}: hardpoint stock {stock} not in weapons.json");
                }
            }
        }
    }

    [ExtractedDataFact]
    public void TheMessageTableResolvesTheInstallsStrings()
    {
        Assert.Equal(1023, Messages.Load(Path.Combine(Extracted, "messages.json")).Count);
    }

    [ExtractedDataFact]
    public void TheSoundTableCarriesItsDefinitionsAndGroups()
    {
        Assert.Equal(2715, SoundDefs.Load(SharedZrdr).Count);

        var groups = SoundDefs.LoadGroups(SharedZrdr);
        // 488 weighted groups + 222 dialogue-chain groups (the loader keeps chains);
        // the per-kind split is pinned in CombatVoiceGoldenTests.
        Assert.Equal(710, groups.Count);
        // The destruction / combat one-shots the weapon and damage code resolves through.
        foreach (var name in new[]
                 {
                     "air_mixed_exp_sg", "ground_mixed_exp_sg", "plane_destroy_sg",
                     "bullet_hit_sg", "bullet_warning_sg", "window_hit_sg",
                 })
        {
            Assert.True(groups.ContainsKey(name), $"sound group {name} missing");
            Assert.NotEmpty(groups[name].Members);
        }
    }

    [ExtractedDataFact]
    public void EveryPlayerAirframeCarriesTheDocumentedMarkerRig()
    {
        // markers.md: 8 firepoints per plane (the Kestrel is the sole 7), 8 pylons, one target.
        var planes = GameZ.Load(Planes);
        Assert.Equal(11, MarkerRig.PlayerAirframes.Count);
        foreach (var (model, display) in MarkerRig.PlayerAirframes)
        {
            var rig = MarkerRig.Extract(planes, model);
            Assert.True(rig != null, $"{display}: no {model} root in planes.zbd");
            int firepoints = 0, pylons = 0, targets = 0;
            foreach (var marker in rig!.Markers)
            {
                switch (marker.Kind)
                {
                    case MarkerRig.MarkerKind.Firepoint: firepoints++; break;
                    case MarkerRig.MarkerKind.Pylon: pylons++; break;
                    case MarkerRig.MarkerKind.Target: targets++; break;
                }
            }
            Assert.Equal(model == "player_kestrel" ? 7 : 8, firepoints);
            Assert.Equal(8, pylons);
            Assert.Equal(1, targets);
        }
    }

    [ExtractedDataFact]
    public void MarkerPositionsAreInThePlaneFrameNotTheModelsSceneSlot()
    {
        // A count-only check passes on garbage coordinates. These are markers.md's own table:
        // the Bloodhawk's outer wing pair, and the Kestrel's lone centreline mount.
        var planes = GameZ.Load(Planes);
        var bloodhawk = MarkerRig.Extract(planes, "player_bhawk")!;
        Assert.InRange(LateralOf(bloodhawk, "firepoint5"), -3.67f, -3.65f);
        Assert.InRange(LateralOf(bloodhawk, "firepoint6"), 3.65f, 3.67f);

        var kestrel = MarkerRig.Extract(planes, "player_kestrel")!;
        Assert.InRange(LateralOf(kestrel, "firepoint7"), -0.005f, 0.005f);
    }

    [ExtractedDataFact]
    public void TheBloodhawkParsesBothHpAndArmorOnAllFourZones()
    {
        // The destroyable_parts pair is (hit points, armor); pbloodhawk is the
        // canonical stock example, 20/20 on every zone, not a dropped second float.
        var stats = PlaneStats.Load(SharedZrdr, "player_bhawk");
        Assert.Equal(4, stats.DestroyableParts.Count);
        foreach (var part in stats.DestroyableParts)
        {
            Assert.Equal(20f, part.MaxHp);
            Assert.Equal(20f, part.MaxArmor);
        }
    }

    [ExtractedDataFact]
    public void EveryChapterAnimDefinitionMirrorsItsNameForDedupe()
    {
        // A reader def whose AnimName is left null instantiates a second copy alongside its
        // compiled twin instead of colliding with it.
        var defs = AnimDefs.LoadArchive(SessionPaths.ChapterZrdr(DataRoot, "C1"));
        Assert.NotEmpty(defs);
        foreach (var def in defs)
        {
            Assert.NotEqual("", def.Name);
            Assert.NotNull(def.AnimName);
            Assert.NotEqual("", def.SourceFile);
        }
    }

    [ExtractedDataTheory]
    [MemberData(nameof(ShippedChaseDistances))]
    public void EveryAirframeResolvesItsShippedChaseDistance(string plane, float dist)
    {
        var cam = CamParams.Load(SharedZrdr, plane);
        Assert.True(cam.FromData);
        Assert.Equal(dist, cam.Dist, 3);
    }

    private static float LateralOf(MarkerRig rig, string name)
    {
        foreach (var marker in rig.Markers)
        {
            if (marker.Name == name)
            {
                return marker.Local.X;
            }
        }
        throw new Xunit.Sdk.XunitException(
            string.Format(CultureInfo.InvariantCulture, "{0} has no {1}", rig.PlaneRoot, name));
    }

    // A reader archive's file count, through the loader itself rather than a directory listing,
    // so the zipped and unpacked shapes are counted the same way.
    private static int CountReaders(string zrdrPath)
    {
        int count = 0;
        foreach (var _ in Zrdr.LoadMatchingFiles(zrdrPath, ""))
        {
            count++;
        }
        return count;
    }
}
