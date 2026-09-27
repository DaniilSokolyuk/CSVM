using System.Collections.Generic;
using System.IO;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>Why the archives are being opened, fixes the <see cref="WorldSession.Options"/>
/// lifetime flags that come back with them, so a caller cannot forget one (forgetting
/// <c>TexturesOutliveBuild</c> is exactly the shipped-bug shape this guards against).</summary>
public enum ArchiveIntent
{
    /// <summary>A game/viewer/flight session: the texture archive belongs to the session (freed on
    /// return-to-menu), so the world runtime keeps baking `PUFFER_STATE` atlases at RUNTIME
    ///, <c>TexturesOutliveBuild = true</c>. The sound archive stays a `using` local of
    /// the build (the prewarm is what makes that survivable), <c>SoundsOutliveBuild = false</c>.</summary>
    Session,

    /// <summary>The animation lab: both archives outlive the build, the lab node owns their
    /// disposal so effects can build at any playhead time.</summary>
    Lab,

    /// <summary>A test-harness suite. The sound archive is a `using` local of the build. The
    /// texture archive belongs to the built <c>TestWorld</c>, whose scene reads it after the build.
    /// Neither flag is set, so the world runtime still retires its puffer factory. The suites and
    /// their counting factories are written against that runtime.</summary>
    Suite,
}

/// <summary>The five archives one chapter world needs, opened together because every caller opens
/// the same five (`GameSession`, the anim lab, the test harness). They come with the
/// <see cref="WorldSession.Options"/> lifetime flags <see cref="ArchiveIntent"/> implies. A caller
/// sets those by naming its intent, not by hand.
/// ⚠ <see cref="OpenFor"/> only opens the archives and states the two flags; it does not dispose
/// anything. Ownership past its return is each caller's own (session fields, lab fields, the
/// harness's <c>TestWorld</c> and its <c>using</c> sound archive).</summary>
public sealed class SessionArchives
{
    public required GameZ Gamez { get; init; }
    public required TextureArchive Textures { get; init; }
    public SoundArchive? Sounds { get; init; }
    public Dictionary<string, SoundDef>? SoundDefs { get; init; }
    public Dictionary<string, SoundGroup>? SoundGroups { get; init; }

    /// <summary>See <see cref="WorldSession.Options.TexturesOutliveBuild"/>.</summary>
    public required bool TexturesOutliveBuild { get; init; }

    /// <summary>See <see cref="WorldSession.Options.SoundsOutliveBuild"/>.</summary>
    public required bool SoundsOutliveBuild { get; init; }

    /// <summary>Opens gamez, textures, sounds, sound defs and sound groups for one chapter build,
    /// timing each into <see cref="StartupProfile"/> exactly as <c>WorldSession.Build</c>'s own
    /// phases do, unconditionally even under the test harness's no-op <c>Record</c>.
    /// ⚠ The sound archive is scoped to the build and the texture archive is not; deliberate,
    /// see <see cref="ArchiveIntent"/>. Never normalise the two to match. A non-null
    /// <paramref name="decode"/> shares the <see cref="Gamez"/> alone (<see cref="DecodeCache"/>).</summary>
    public static SessionArchives OpenFor(ArchiveIntent intent, string gamezPath, string texturesPath,
        string soundsPath, string zrdrPath, bool mute, DecodeCache? decode = null)
    {
        long mark = StartupProfile.Mark();
        var gamez = decode == null ? GameZ.Load(gamezPath) : decode.Gamez(gamezPath);
        StartupProfile.Record("gamez", mark);

        mark = StartupProfile.Mark();
        var textures = new TextureArchive(texturesPath);
        StartupProfile.Record("textures", mark);

        bool haveSounds = !mute && (File.Exists(soundsPath) || Directory.Exists(soundsPath));
        mark = StartupProfile.Mark();
        var sounds = haveSounds ? new SoundArchive(soundsPath) : null;
        StartupProfile.Record("sounds", mark);

        mark = StartupProfile.Mark();
        var loadedSoundDefs = haveSounds ? Mech3.SoundDefs.Load(zrdrPath) : null;
        var loadedSoundGroups = haveSounds ? Mech3.SoundDefs.LoadGroups(zrdrPath) : null;
        StartupProfile.Record("zrdr", mark);

        if (!mute && !haveSounds)
            GD.PushWarning($"sound archive not found, flying silent: {soundsPath}");

        return new SessionArchives
        {
            Gamez = gamez,
            Textures = textures,
            Sounds = sounds,
            SoundDefs = loadedSoundDefs,
            SoundGroups = loadedSoundGroups,
            TexturesOutliveBuild = intent != ArchiveIntent.Suite,
            SoundsOutliveBuild = intent == ArchiveIntent.Lab,
        };
    }
}
