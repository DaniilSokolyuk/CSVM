using System.Collections.Generic;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Audio;

/// <summary>One resolved weapon sound: the stream a player takes, the definition's own unscaled
/// <c>VOLUME</c>, its authored <c>RANGE</c> pair, and whether it carries <c>3D</c>. The range and the
/// flag travel with the cue so a positional player's <c>UnitSize</c>, <c>MaxDistance</c> and cull
/// threshold cannot disagree with the definition they came from.</summary>
/// <param name="Name">The <c>sounds.json</c> definition name, for the log line.</param>
/// <param name="Stream">The decoded WAV, already forward-looped for a firing loop.</param>
/// <param name="Volume">The definition's <c>VOLUME</c>, unscaled: no mix gain, no distance term.</param>
/// <param name="RangeMin"><c>RANGE</c>'s full-volume distance, m.</param>
/// <param name="RangeMax"><c>RANGE</c>'s audible distance, m.</param>
/// <param name="Is3D">The positional opt-in; a definition without it has no distance model at all.</param>
public readonly record struct WeaponSoundCue(
    string Name, AudioStreamWav Stream, float Volume, float RangeMin, float RangeMax, bool Is3D)
{
    /// <summary>The margin the sound manager leaves over <c>RANGE</c>'s audible distance before it
    /// silences a positional voice. The decoded law's own factor, not a second copy of it: the
    /// aircraft gun loop and a mount's gun reach the same 3D update every world emitter does
    /// (docs/formats/sounds.md).</summary>
    public const float CullMargin = SoundFalloff.CullFactor;

    /// <summary>Where this cue goes silent, <see cref="RangeMax"/> grown by
    /// <see cref="CullMargin"/>. The pair drives the attenuation curve, so the cull sits outside
    /// that curve rather than on its end. Scaled with the curve by
    /// <see cref="SoundFalloff.RangeScale"/>, so the diagnostic factor cannot cut a voice the
    /// scaled curve still plays.</summary>
    public float CullDistance => RangeMax * SoundFalloff.RangeScale * CullMargin;
}

/// <summary>
/// The weapon-sound selection every aircraft shares: a definition name to a resolved
/// <see cref="WeaponSoundCue"/>. The pilot's own <see cref="FlightAudio"/> and the positional
/// <see cref="Ai.AiWeaponAudio"/> read their cues from here rather than each keeping a copy, the way
/// both engine paths read <see cref="EngineAudioCurves"/>. It selects and nothing else: which player
/// a cue lands on, what gain it takes and whether it is culled are the two paths' own business,
/// which is what keeps own-ship concepts (splitscreen mix gain, the incoming-fire cues) out of the
/// world. Definitions: docs/formats/sounds.md.
/// </summary>
public static class WeaponAudioCues
{
    /// <summary>The dry-trigger cue's definition name, <c>weapons.json</c>'s
    /// <c>NO_AMMO_WARNING</c>. Exposed apart from <see cref="EmptyClip"/> because it is the name
    /// the emitter roll-call and the suites compare against, and a name selection can be pinned
    /// without a decoded stream behind it.</summary>
    public static string? EmptyClipName(WeaponDefs? weapons) => weapons?.EmptyClipSound;

    /// <summary>The dry-trigger cue both weapon audio paths play, resolved through
    /// <see cref="WeaponDefs.EmptyClipSound"/> rather than a literal of this class's own, so the
    /// two paths cannot come to play different definitions on an install whose
    /// <c>NO_AMMO_WARNING</c> is not the stock name.</summary>
    public static WeaponSoundCue? EmptyClip(SoundArchive? archive,
        IReadOnlyDictionary<string, SoundDef>? defs, WeaponDefs? weapons) =>
        Resolve(archive, defs, EmptyClipName(weapons), looped: false);

    /// <summary>The sustained-fire loop for a caliber's <c>LOOPED_SOUND_NAME</c>, or null when the
    /// name is empty, unknown to <c>sounds.json</c>, or its WAV will not decode.</summary>
    public static WeaponSoundCue? GunLoop(SoundArchive? archive,
        IReadOnlyDictionary<string, SoundDef>? defs, string? sndName) =>
        Resolve(archive, defs, sndName, looped: true);

    // ⚠ A firing loop is decoded LOOPED whatever its definition says, because it is a sustained-fire
    // cue held while the trigger is; SoundArchive caches per (wav, looped), so the flag is also the
    // prewarm key (WeaponDefs.SoundCues). Everything else takes the definition's own flag.
    private static WeaponSoundCue? Resolve(SoundArchive? archive,
        IReadOnlyDictionary<string, SoundDef>? defs, string? sndName, bool looped)
    {
        if (string.IsNullOrEmpty(sndName) || archive == null || defs == null
            || !defs.TryGetValue(sndName!, out var def))
        {
            return null;
        }
        var stream = archive.Find(def.WavName, looped || def.Looped);
        return stream == null
            ? null
            : new WeaponSoundCue(def.Name, stream, def.Volume, def.RangeMin, def.RangeMax, def.Is3D);
    }
}
