using CSVM.Flight.Audio;
using CSVM.Flight.Weapons;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The weapon-sound selection's name reads. The dry-trigger cue is the one both audio paths take,
/// and the fixture's <c>NO_AMMO_WARNING</c> is deliberately not the stock <c>snd_emptyclip</c>, so
/// a selection that answered a literal instead of the file would fail here.
/// </summary>
[Trait("Tier", "Quick")]
public class WeaponAudioCuesTests
{
    [Fact]
    public void TheDryCueNameComesFromTheCatalogueNotALiteral()
    {
        var weapons = WeaponDefs.Load(TestData.Fixture("zrdr"));

        Assert.Equal("snd_probe_emptyclip", WeaponAudioCues.EmptyClipName(weapons));
        Assert.Equal(weapons.EmptyClipSound, WeaponAudioCues.EmptyClipName(weapons));
    }

    /// <summary>A session that found no weapon catalogue has no dry cue to name, rather than a
    /// stock name standing in: the audio paths build no player for a null, which is the same
    /// silence an unresolved definition gets.</summary>
    [Fact]
    public void NoCatalogueNamesNoDryCue()
    {
        Assert.Null(WeaponAudioCues.EmptyClipName(null));
    }
}
