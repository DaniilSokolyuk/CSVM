using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Audio;

/// <summary>Everything a mount needs to build a gun voice: the node the emitter hangs under, the
/// archive and definitions its cue is selected from, and the session's listener seam. One of these
/// serves every mount a session builds, so no builder grows four audio parameters of its own.</summary>
/// <param name="Node">Parent for the emitters: a host aircraft, or the world's sound node.</param>
/// <param name="Archive">The session sound archive; null leaves every mount silent.</param>
/// <param name="Defs">The <c>sounds.json</c> definitions a cue name is looked up in.</param>
/// <param name="Listeners">Where the human pilots are, for the cull; null falls back to the camera.</param>
public sealed record GunVoiceHome(Node3D Node, SoundArchive? Archive,
    IReadOnlyDictionary<string, SoundDef>? Defs, Func<IReadOnlyList<Vector3>>? Listeners);

/// <summary>
/// One mounted gun's firing voice: a single <see cref="AudioStreamPlayer3D"/> carrying the mount's
/// own cue, moved to where it fires from and held sounding by a lease each renewal resets, so a
/// firing spell is one continuous burst rather than a string of isolated clips. The cue is selected
/// through <see cref="WeaponAudioCues"/>, the seam <see cref="Ai.AiWeaponAudio"/> and the pilot's own
/// <see cref="FlightAudio"/> read too, and the cull is that cue's own
/// <see cref="WeaponSoundCue.CullDistance"/>, the one an aircraft's gun loop takes as well.
/// ⚠ One voice per mount, never one per owner: the original mints a sound slot per turret, so a
/// zeppelin's rings each hold their own (docs/formats/turrets.md).
/// </summary>
public sealed partial class GunVoice : Node3D
{
    private readonly Func<IReadOnlyList<Vector3>>? _listeners;
    private readonly AudioStreamPlayer3D _player;
    private readonly string _cue;
    private readonly float _cull;
    private readonly float _rangeMin;
    private readonly float _rangeMax;
    private readonly float _volume;
    private readonly string _label;
    private readonly float _lease;

    private float _leaseLeft;
    private bool _leased;
    // The one step of grace every lease gets, which is what the original's own ordering gives it:
    // the renewal stamps an expiry of now + lease and the release pass drops the voice only once
    // the sound clock is PAST it, so a zero lease survives the frame it was renewed in.
    private bool _renewedThisStep;
    // Null until the voice's first shot, so THAT shot always logs its verdict. A turret cue is
    // audible to 200 m and a gun engages further out than that, so a state seeded `culled` would
    // leave the common case with no line at all (INSTR-45).
    private bool? _culled;

    private GunVoice(WeaponSoundCue cue, Func<IReadOnlyList<Vector3>>? listeners, string label,
        float lease)
    {
        _cue = cue.Name;
        _cull = cue.CullDistance;
        _rangeMin = cue.RangeMin;
        _rangeMax = cue.RangeMax;
        _volume = cue.Volume;
        _listeners = listeners;
        _label = label;
        _lease = lease;
        Name = "GunVoice";
        // ⚠ Do not restore an engine attenuation model or a MaxDistance; Sound drives the level
        // from SoundFalloff, the same law WorldSounds runs. Either one costs the mount its level
        // well inside the distance its own RANGE pair calls audible (INSTR-87).
        _player = new AudioStreamPlayer3D
        {
            Stream = cue.Stream,
            VolumeDb = SoundFalloff.VolumeDb(cue.Volume),
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
            Bus = AudioBuses.Effects,
        };
        AddChild(_player);
    }

    /// <summary>How long one renewal keeps this voice sounding, seconds. ⚠ Per mount, not one
    /// constant: the two fire paths carry different literals, and each owns its own
    /// (docs/formats/turrets.md, docs/org/weaponFire.md).</summary>
    public float LeaseSeconds => _lease;

    /// <summary>Whether the voice is sounding, read off the live player rather than off a flag this
    /// component keeps, which could agree with itself while the stream is stopped.</summary>
    internal bool Sounding => _player.Playing;

    /// <summary>The level the decoded law last set this voice to, decibels, and the definition's
    /// own <c>VOLUME</c> before the first renewal. Internal so an emitter suite reads the gain the
    /// listener actually gets rather than re-deriving it from a distance.</summary>
    internal float GainDb => _player.VolumeDb;

    /// <summary>Builds the voice for a mount whose cue is <paramref name="sndName"/>, or null when
    /// the session has no archive, the mount authors no cue, or the definition is not positional.
    /// <paramref name="label"/> names the mount in this path's log lines and
    /// <paramref name="leaseSeconds"/> is its own fire path's lease. Declining is always said out
    /// loud: no line at all is the one case indistinguishable from never having tried.</summary>
    public static GunVoice? Attach(GunVoiceHome? home, string? sndName, string label,
        float leaseSeconds)
    {
        if (home == null || string.IsNullOrEmpty(sndName))
        {
            return null;
        }
        if (WeaponAudioCues.GunLoop(home.Archive, home.Defs, sndName) is not { Is3D: true } cue)
        {
            Log.Info("sound", $"gun voice {label}: {sndName} did not resolve as a positional cue, so this mount is silent");
            return null;
        }
        var voice = new GunVoice(cue, home.Listeners, label, leaseSeconds);
        home.Node.AddChild(voice);
        Log.Info("sound", $"gun voice {label}: loop={cue.Name} audible={cue.RangeMax * SoundFalloff.RangeScale:0} m cull={voice._cull:0} m range=x{SoundFalloff.RangeScale:0.###} lease={leaseSeconds:0.00} s");
        return voice;
    }

    /// <summary>The gun is firing, from <paramref name="worldPos"/>: the voice moves there and
    /// renews its lease, which starts the loop again if the previous lease had run out. What
    /// renews it is the caller's own decoded event, a round leaving for a turret and a pass of the
    /// fire decision for a hull.</summary>
    public void Renew(Vector3 worldPos)
    {
        // ⚠ The NODE moves, not the player under it: the cull reads this node's own world position
        // (see Sound), so moving only the player would measure every world gun's distance from the
        // world sound node it hangs under instead of from the gun.
        GlobalPosition = worldPos;
        _leaseLeft = _lease;
        _leased = true;
        _renewedThisStep = true;
        Sound();
    }

    /// <summary>Ages the lease by one sim step and silences the voice when it runs out. Called on
    /// every tick of the mount, the ticks its fire gates close included, so a gun that stops
    /// shooting goes quiet within the lease instead of holding the loop for good.</summary>
    public void Tick(float dt)
    {
        if (!_leased)
        {
            return;
        }
        if (_renewedThisStep)
        {
            _renewedThisStep = false;
            Sound();
            return;
        }
        _leaseLeft -= dt;
        if (_leaseLeft <= 0f)
        {
            _leased = false;
            _player.Stop();
            return;
        }
        Sound();
    }

    /// <summary>Drops the lease and silences the voice at once, for a mount whose death
    /// choreography owns everything audible from here on.</summary>
    public void Stop()
    {
        _leaseLeft = 0f;
        _leased = false;
        _renewedThisStep = false;
        _player.Stop();
    }

    /// <summary>The emitter's cue name, world position, authored audible distance and the cull
    /// past it. Internal so an emitter suite reads the pairing this path's log prints rather than
    /// inferring it, the two distances included: they differ by the margin above.</summary>
    internal (string Name, Vector3 Position, float RangeMax, float Cull) Emitter() =>
        (_cue, _player.GlobalPosition, _rangeMax, _cull);

    private void Sound()
    {
        float dist = Mathf.Sqrt(AudioListeners.NearestDistanceSq(this, _listeners));
        bool culled = GunLoopSound.Apply(_player, dist, _cull, _rangeMin, _rangeMax, _volume);
        SetCulled(culled, dist);
    }

    // The first shot and every transition after it, always logged. Audio cannot be
    // screenshot-verified (INSTR-45), and this line separates "silent past the cull" from "silent
    // because its definition never resolved". ⚠ The word is `sounding`, not `audible`: the level
    // between the audible radius and the cull runs to -100 dB (INSTR-92).
    private void SetCulled(bool culled, float dist)
    {
        if (culled == _culled)
        {
            return;
        }
        _culled = culled;
        Log.Debug("sound", $"gun voice {_label} {(culled ? "culled" : "sounding")} at {dist:0} m, {SoundFalloff.SessionGainDb(dist, _rangeMin, _rangeMax, _volume):0.0} dB (cull {_cull:0} m, range x{SoundFalloff.RangeScale:0.###})");
    }
}

/// <summary>The cull test and per-frame level every gun firing loop shares: a mount's voice here
/// and an AI aircraft's caliber loop alike. The cull is the cue's own
/// <see cref="WeaponSoundCue.CullDistance"/>, never EngineAudioCurves.CullDistance. A gun loop is
/// authored audible to 150-550 m, far inside the engine-audio routine's 2000 m. That number never
/// bites first, so reading it here would be a borrowed constant.</summary>
internal static class GunLoopSound
{
    /// <summary>Stops <paramref name="player"/> past <paramref name="cull"/> metres, and otherwise
    /// levels it by the decoded law and starts it. True when the distance culled it. The level is
    /// written on every call, not once at the start. Emitter and listener are both flying, so one
    /// level is the level of wherever they were then.</summary>
    internal static bool Apply(AudioStreamPlayer3D player, float dist, float cull, float rangeMin,
        float rangeMax, float volume)
    {
        if (dist > cull)
        {
            player.Stop();
            return true;
        }
        player.VolumeDb = SoundFalloff.SessionGainDb(dist, rangeMin, rangeMax, volume);
        if (!player.Playing)
        {
            player.Play();
        }
        return false;
    }
}
