using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>
/// One AI aircraft's weapon audio, positional: the sustained-fire gun loop and the dry-trigger cue
/// the pilot's own <see cref="FlightAudio"/> plays flat, on <see cref="AudioStreamPlayer3D"/>s riding
/// this node, plus the cull that silences an aircraft firing further off than its cue reaches.
/// Both definitions are selected through <see cref="WeaponAudioCues"/>, which the own-ship path reads
/// too. The sibling of <see cref="AiEngineAudio"/>, and separate from it because that component's
/// contract is the two engine slots and nothing else.
/// ⚠ Own-ship concepts stay out: no splitscreen mix gain (the panes' listeners already decide who
/// hears this), no incoming-fire cue (those are the pilot being shot at), and no pitch term of any
/// kind (see <see cref="StartGunLoop"/>).
/// </summary>
public sealed partial class AiWeaponAudio : Node3D
{
    /// <summary>Where the session's human pilots are, the same nearest-human seam
    /// <c>ProjectilePool</c> measures its weapon one-shots against. The cull takes the nearest of
    /// them. Left null, it falls back to this node's own viewport camera.</summary>
    public Func<IReadOnlyList<Vector3>>? Listeners;

    private SoundArchive? _archive;
    private IReadOnlyDictionary<string, SoundDef>? _defs;
    private AudioStreamPlayer3D? _gunLoop;
    private string? _gunLoopName;
    private float _gunLoopVol = 1f;
    private float _gunLoopRangeMin;           // the live loop's RANGE full-volume distance, m
    private float _gunLoopRangeMax;           // its RANGE audible distance, m
    private float _gunLoopCull;               // the live loop's cue distance, its RANGE audible one grown by the margin
    private AudioStreamPlayer3D? _emptyClip;
    private string? _emptyClipName;
    private float _emptyClipVol = 1f;
    private float _emptyClipRangeMin;
    private float _emptyClipRangeMax;
    // Null until the loop's first frame, so THAT frame always logs its verdict. A gun's authored
    // audible distance is 150-550 m and traffic engages further out than that, so a first frame
    // seeded `culled` would leave the common case with no line at all (INSTR-45).
    private bool? _culled;

    /// <summary>Whether the gun loop is sounding, read off the live player rather than off a flag
    /// this component keeps, which could agree with itself while the voice is stopped. Internal for
    /// the emitter suite's cull half.</summary>
    internal bool LoopSounding => _gunLoop is { Playing: true };

    /// <summary>Where the live gun loop goes silent, the caliber's own
    /// <see cref="WeaponSoundCue.CullDistance"/>, and zero until a caliber has fired. Internal so
    /// the emitter suite reads the distance the cull actually uses rather than the attenuation
    /// curve's end, which is the shorter of the two.</summary>
    internal float LoopCull => _gunLoopCull;

    /// <summary>The level the decoded law last set the gun loop to, decibels, and
    /// <see cref="SoundFalloff.FloorDb"/> while no caliber has fired. Internal so the emitter suite
    /// reads the gain the listener actually gets rather than re-deriving it from a distance.</summary>
    internal float LoopGainDb => _gunLoop?.VolumeDb ?? SoundFalloff.FloorDb;

    /// <summary>Builds this aircraft's weapon audio and hangs it under <paramref name="controller"/>,
    /// or returns null when the session found no sound archive. The spawner's whole share of the job;
    /// call it after the controller has joined the tree, since the cull reads a world position.
    /// <paramref name="weapons"/> carries the dry cue's <c>NO_AMMO_WARNING</c> name;
    /// <paramref name="listeners"/> left null falls back to the node's own viewport camera.</summary>
    public static AiWeaponAudio? Attach(FlightController controller, SoundArchive? archive,
        IReadOnlyDictionary<string, SoundDef>? defs, WeaponDefs? weapons,
        Func<IReadOnlyList<Vector3>>? listeners = null)
    {
        if (archive == null || defs == null)
        {
            // Said out loud for the reason AiEngineAudio says it: no archive means no line from Setup
            // below, and no line at all is the one case indistinguishable from never having tried.
            Log.Info("sound", $"ai weapons {controller.Name}: no sound archive, so this aircraft's guns are silent");
            return null;
        }
        var audio = new AiWeaponAudio { Name = "WeaponAudio", Listeners = listeners };
        controller.AddChild(audio);
        audio.Setup(archive, defs, weapons);
        return audio;
    }

    /// <summary>Keeps the archive and definitions for the gun loop, which is built on the first frame
    /// a caliber fires rather than up front (an airframe's loadout can hold several, and a plane that
    /// never shoots should decode none), and resolves the dry-trigger cue now, since there is exactly
    /// one of it.</summary>
    public void Setup(SoundArchive archive, IReadOnlyDictionary<string, SoundDef> defs,
        WeaponDefs? weapons)
    {
        _archive = archive;
        _defs = defs;
        _emptyClipName = WeaponAudioCues.EmptyClipName(weapons);
        var empty = WeaponAudioCues.EmptyClip(archive, defs, weapons);
        _emptyClip = MakePlayer(empty);
        _emptyClipVol = empty?.Volume ?? 1f;
        _emptyClipRangeMin = empty?.RangeMin ?? 0f;
        _emptyClipRangeMax = empty?.RangeMax ?? 0f;
        // The build half of the observable INSTR-45 asks for, WorldSounds.Debug's precedent: a cue
        // logged `unresolved` here never had a stream, one logged `ok` and later `culled` is silent by
        // distance alone. The gun loop's own line comes when a caliber first fires.
        Log.Info("sound", $"ai weapons {Aircraft()}: dry={SlotState(_emptyClipName, empty)} loop=on first burst");
    }

    /// <summary>Starts (or keeps playing) the gun firing loop for the given
    /// <c>LOOPED_SOUND_NAME</c>, from this aircraft's position. Called every frame the loop is wanted,
    /// so the distance cull rides here rather than on a per-frame call of its own; the loop exists
    /// only while the trigger is held.
    /// ⚠ Do not add a pitch term. `CAP-09` measured no Doppler on the original's world emitters, and
    /// Instant Action traffic is unmeasured, so any pitch here would be an invention.</summary>
    public void StartGunLoop(string? sndName)
    {
        if (_gunLoopName != sndName)
        {
            var cue = WeaponAudioCues.GunLoop(_archive, _defs, sndName);
            RebuildGunLoop(sndName, cue);
        }
        if (_gunLoop == null)
        {
            return;
        }
        float dist = Mathf.Sqrt(AudioListeners.NearestDistanceSq(this, Listeners));
        bool culled = GunLoopSound.Apply(_gunLoop, dist, _gunLoopCull, _gunLoopRangeMin,
            _gunLoopRangeMax, _gunLoopVol);
        SetCulled(culled, dist);
    }

    /// <summary>The trigger came off, or the aircraft respawned.</summary>
    public void StopGunLoop() => _gunLoop?.Stop();

    /// <summary>The dry-trigger cue, one shot when this aircraft's guns or pylons come up empty.
    /// Positional by decision: the original plays every aircraft's flat and at full volume from any
    /// distance (docs/org/weaponFire.md), and this port keeps the world emitter instead. Levelled
    /// once here rather than per frame: the cue holds a voice for a fraction of a second, so the
    /// distance it starts at is the distance it plays at, as a <see cref="WorldSounds"/> one-shot
    /// is levelled at its spawn.</summary>
    public void PlayEmptyClip()
    {
        if (_emptyClip == null)
        {
            return;
        }
        float dist = Mathf.Sqrt(AudioListeners.NearestDistanceSq(this, Listeners));
        _emptyClip.VolumeDb = SoundFalloff.SessionGainDb(dist, _emptyClipRangeMin, _emptyClipRangeMax,
            _emptyClipVol);
        _emptyClip.Play();
    }

    /// <summary>Kills the loop for good, because the aircraft is down and its crash animation owns
    /// everything audible from here on.</summary>
    public void Stop() => _gunLoop?.Stop();

    /// <summary>How many positional players this aircraft's weapon voice holds, and where they are.
    /// Internal so the emitter-count suite can read the pairing the log prints.</summary>
    internal IReadOnlyList<(string Name, Vector3 Position, float RangeMax)> Emitters()
    {
        var live = new List<(string, Vector3, float)>();
        if (_gunLoop != null)
        {
            live.Add((_gunLoopName ?? "?", _gunLoop.GlobalPosition, _gunLoopRangeMax));
        }
        if (_emptyClip != null)
        {
            live.Add((_emptyClipName ?? "?", _emptyClip.GlobalPosition, _emptyClipRangeMax));
        }
        return live;
    }

    // One cue's word in the build log: the definition asked for, and whether a positional stream came
    // back. `flat` is a definition carrying no 3D flag, which by the data's own sorting has no
    // distance model at all and so gets no world player here (docs/formats/sounds.md).
    private static string SlotState(string? sndName, WeaponSoundCue? cue) => cue switch
    {
        null => $"{sndName ?? "none"}(unresolved)",
        { Is3D: false } c => $"{c.Name}(flat)",
        { } c => $"{c.Name}(ok)",
    };

    // A definition without the 3D flag gets no player: the data says it has no distance model, so
    // placing it in the world would invent one. ⚠ Do not restore an engine attenuation model or a
    // MaxDistance; the two play paths level from SoundFalloff, the same law WorldSounds runs.
    // ⚠ The player hangs on this node and takes no muzzle offset: the original's fire tick hands
    // its firing loop the vehicle's own position (docs/org/weaponFire.md).
    private AudioStreamPlayer3D? MakePlayer(WeaponSoundCue? cue)
    {
        if (cue is not { Is3D: true } resolved)
        {
            return null;
        }
        var player = new AudioStreamPlayer3D
        {
            Stream = resolved.Stream,
            VolumeDb = SoundFalloff.VolumeDb(resolved.Volume),
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
            Bus = AudioBuses.Effects,
        };
        AddChild(player);
        return player;
    }

    // The loop slot takes a new caliber: drop the old player rather than restream it, since the two
    // carry different RANGE pairs and a reused player would keep the first one's.
    private void RebuildGunLoop(string? sndName, WeaponSoundCue? cue)
    {
        if (_gunLoop != null)
        {
            _gunLoop.Stop();
            _gunLoop.QueueFree();
            _gunLoop = null;
        }
        _gunLoopName = null;
        _gunLoopCull = 0f;
        _gunLoopRangeMin = 0f;
        _gunLoopRangeMax = 0f;
        _gunLoop = MakePlayer(cue);
        if (_gunLoop == null || cue is not { } resolved)
        {
            return;
        }
        _gunLoopName = sndName;
        _gunLoopVol = resolved.Volume;
        _gunLoopRangeMin = resolved.RangeMin;
        _gunLoopRangeMax = resolved.RangeMax;
        // The caliber's own cue distance, which is the one GunLoopSound tests each frame against.
        _gunLoopCull = resolved.CullDistance;
        _culled = null;   // re-armed with the slot, so the first frame of this caliber logs its verdict
        Log.Info("sound", $"ai weapons {Aircraft()}: loop={SlotState(sndName, cue)} audible={resolved.RangeMax * SoundFalloff.RangeScale:0} m cull={_gunLoopCull:0} m range=x{SoundFalloff.RangeScale:0.###}");
    }

    // The first frame and every transition after it, always logged. Audio cannot be
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
        Log.Debug("sound", $"ai weapons {Aircraft()} {(culled ? "culled" : "sounding")} at {dist:0} m, {SoundFalloff.SessionGainDb(dist, _gunLoopRangeMin, _gunLoopRangeMax, _gunLoopVol):0.0} dB (cull {_gunLoopCull:0} m, range x{SoundFalloff.RangeScale:0.###})");
    }

    // Which aircraft every line here is about: the controller this component hangs under, whose name
    // the spawner already logged. Never this node's own name, which is "WeaponAudio" on all of them.
    private string Aircraft() => GetParent()?.Name.ToString() ?? Name.ToString();
}
