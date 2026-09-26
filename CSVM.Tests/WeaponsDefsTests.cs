using System.IO;
using System.Linq;
using CSVM.Flight.Weapons;
using CSVM.Tooling;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Every <c>weapons.json</c> <c>BALLISTICS</c> entry reads through the typed reader, against the
/// retail install (engine-free: <c>Probes.Weapons</c>
/// touches no live Node). Golden count: 48 entries, matching <see cref="WeaponDefsTests"/>'s
/// per-key coverage on the trimmed fixture.
/// </summary>
public class WeaponsDefsTests
{
    private const int WeaponDefCount = 48;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    private static string MessagesPath =>
        Path.Combine(TestData.ExtractedRoot!, "messages.json");

    [ExtractedDataFact]
    public void EveryBallisticsEntryReadsThroughTheTypedReader()
    {
        var r = Probes.Weapons(ZrdrPath, MessagesPath, "");
        Assert.True(r.Error == null, $"weapons.json loads error={r.Error ?? "-"}");
        Assert.Equal(WeaponDefCount, r.Total);
        Assert.Equal(0, r.UnhandledTotal);
        Assert.False(string.IsNullOrEmpty(r.EmptyClipSound));
    }

    // The prewarm's work list. These are definition names, not WAVs: snd_40cal resolves to
    // 40cal_gun.wav, snd_missile_bg to missile_bg.wav. Both were reached cold in flight once the
    // sound archive had closed, so each is the shipped bug in miniature.
    [ExtractedDataFact]
    public void SoundCuesCoverEveryWeaponBindingAtItsPlaySiteLoopFlag()
    {
        var cues = WeaponDefs.Load(ZrdrPath, null).SoundCues();

        // Every caliber's firing loop, forced-looped the way StartGunLoop asks for it.
        foreach (string gun in new[] { "snd_30cal", "snd_40cal", "snd_50cal", "snd_60cal", "snd_70cal", "snd_turretgun" })
        {
            Assert.Contains((gun, true), cues);
            // The looped flag is half the fix: SoundArchive caches per (wav, looped), so the same
            // gun decoded unlooped would leave the loop a cold read into a closed archive.
            Assert.DoesNotContain((gun, false), cues);
        }

        Assert.Contains(("snd_missile_bg", false), cues);
        Assert.Contains(("snd_missile_explode", false), cues);
        // A `_sg` name is a SOUND_GROUPS group whose member is picked per shot, so the prewarm has
        // to expand it rather than decode the group name.
        Assert.Contains(("bullet_hit_sg", false), cues);
        Assert.DoesNotContain(("snd_missile_explode", true), cues);

        Assert.All(cues, cue => Assert.False(string.IsNullOrEmpty(cue.Name)));
    }

    // The engine's squared-radius convention (docs/org/ordnanceTypes.md, "The engine stores radii
    // squared"). An unchanged number proves nothing here, so this asserts the relationship between
    // the two forms over every shipped entry, and that each square is absent exactly when the
    // authored value is.
    [ExtractedDataFact]
    public void EveryStoredSquareIsTheSquareOfItsAuthoredRadius()
    {
        var defs = WeaponDefs.Load(ZrdrPath, null);
        foreach (var def in defs.All)
        {
            AssertSquare(def.Id, "RANGE", def.Range, def.RangeSqM);
            AssertSquare(def.Id, "DETONATION_DISTANCE", def.DetonationDistance, def.DetonationDistanceSqM);
            AssertSquare(def.Id, "IMPACT_PROXIMITY", def.ImpactProximity, def.ImpactProximitySqM);
        }

        // Guards the loop itself: a reader that stopped populating the authored fields would leave
        // every pair null and every assertion above vacuously true.
        Assert.Equal(46, defs.All.Count(d => d.RangeSqM.HasValue));
        Assert.Equal(13, defs.All.Count(d => d.DetonationDistanceSqM.HasValue));
        Assert.Equal(14, defs.All.Count(d => d.ImpactProximitySqM.HasValue));
    }

    private static void AssertSquare(string id, string key, float? raw, float? square)
    {
        if (raw is not { } value)
        {
            Assert.True(square == null, $"{id} {key}: absent authored value must leave no square");
            return;
        }
        Assert.True(square == value * value, $"{id} {key}: {square} != {value}^2");
    }
}
