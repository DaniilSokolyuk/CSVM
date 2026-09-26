using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>
/// One AI aircraft's engine audio, positional: the same two slots the pilot's own
/// <see cref="FlightAudio"/> drives (engine, and the whine no shipped airframe names), on
/// <see cref="AudioStreamPlayer3D"/>s riding this node, plus the distance cull that silences an
/// aircraft the player is too far from. Every number comes from
/// <see cref="EngineAudioCurves"/>, which the own-ship path reads too.
/// ⚠ Own-ship concepts stay out: no splitscreen mix gain (the panes' listeners already decide who
/// hears this), no start ramp, no prop-start cue, no crash or graze one-shots, an AI kill is
/// audible from the crash animation's own authored sound events.
/// </summary>
public sealed partial class AiEngineAudio : Node3D
{
    /// <summary>The nitro loop's sound definition, looked up by this literal name at vehicle-def
    /// load rather than authored per airframe, so every aircraft's injector sounds the same.</summary>
    public const string NitroSound = "snd_nitro";

    /// <summary>Where the session's human pilots are, one per splitscreen pane, the same
    /// nearest-human seam <see cref="AiWeaponAudio"/> and <c>ProjectilePool</c> measure against.
    /// The cull takes the nearest of them, so one aircraft answers one listener model however you
    /// ask it. Left null, the cull falls back to this node's own viewport camera.</summary>
    public Func<IReadOnlyList<Vector3>>? Listeners;

    private PlaneStats _stats = null!;
    private AudioStreamPlayer3D? _engine, _whine, _nitro;
    private AudioStreamWav? _engineStream, _damagedStream;
    private float _engineVol = 1f, _damagedVol = 1f, _whineVol = 1f, _nitroVol = 1f;
    private float _nitroKeyedS;    // s the keyed nitro loop has left before it expires
    private EngineSlotPhase _phase;
    private float _enginePitchMul = 1f;
    private bool _enginePitchable;     // whether the def on the slot takes a frequency write
    private IReadOnlyDictionary<string, SoundDef>? _defs;
    private bool _culled = true;   // starts culled so the first in-range frame logs its start

    /// <summary>Whether the engine loop is sounding, read off the live player rather than off a
    /// flag this component keeps, which could agree with itself while the voice is stopped.
    /// Internal for the listener suite's cull half.</summary>
    internal bool EngineSounding => _engine is { Playing: true };

    /// <summary>Whether the nitro loop is sounding, read off the live player for the same reason
    /// <see cref="EngineSounding"/> is.</summary>
    internal bool NitroSounding => _nitro is { Playing: true };

    /// <summary>Builds this aircraft's engine audio and hangs it under <paramref name="controller"/>,
    /// or returns null when the session found no sound archive. The spawner's whole share of the
    /// job; call it after the controller has joined the tree, since the cull reads a world position.
    /// <paramref name="listeners"/> left null falls back to the node's own viewport camera.</summary>
    public static AiEngineAudio? Attach(FlightController controller, SoundArchive? archive,
        IReadOnlyDictionary<string, SoundDef>? defs, PlaneStats stats,
        Func<IReadOnlyList<Vector3>>? listeners = null)
    {
        if (archive == null || defs == null)
        {
            // Said out loud, because the alternative reading of a silent aircraft is a slot that
            // failed to resolve: no archive means no line from Setup below, and no line at all is
            // the one case that would otherwise be indistinguishable from never having tried.
            Log.Info("sound", $"ai engine {controller.Name}: no sound archive, this aircraft carries no engine loop");
            return null;
        }
        var audio = new AiEngineAudio { Name = "EngineAudio", Listeners = listeners };
        controller.AddChild(audio);
        audio.Setup(archive, defs, stats);
        return audio;
    }

    /// <summary>Builds the slots this airframe's data names. The engine slot is resolved with both
    /// its candidate streams up front, so the damaged swap is an assignment; the whine slot is built
    /// only when a definition names it, which no shipped airframe does.</summary>
    public void Setup(SoundArchive archive, IReadOnlyDictionary<string, SoundDef> defs, PlaneStats stats)
    {
        _stats = stats;
        _defs = defs;
        _engine = MakeLoop(archive, defs, stats.EngineSound, out _engineVol);
        _engineStream = _engine?.Stream as AudioStreamWav;
        // The slot opens on the plain definition, and only a swap re-reads this below.
        _enginePitchable = EngineAudioCurves.SlotIsPitched(defs, stats.EngineSound);
        // No player of its own: this stream replaces the engine's on the one slot.
        if (stats.DamagedEngineSound is { } damagedName)
            _damagedStream = LoadStream(archive, defs, damagedName, out _damagedVol);
        if (stats.WhineSound is { } whineName)
        {
            _whine = MakeLoop(archive, defs, whineName, out _whineVol);
        }
        _nitro = MakeLoop(archive, defs, NitroSound, out _nitroVol);
        // The build half of the headless observable (WorldSounds.Debug's precedent), which with the
        // cull transitions below separates the two silences: a slot logged `unresolved` here never
        // had a stream, one logged `ok` and later `culled` is silent by distance alone.
        Log.Info("sound", $"ai engine {Aircraft()}: engine={SlotState(stats.EngineSound, _engineStream != null)} damaged={SlotState(stats.DamagedEngineSound, _damagedStream != null)} whine={SlotState(stats.WhineSound, _whine != null)} nitro={SlotState(NitroSound, _nitro != null)} cull={EngineAudioCurves.CullDistance:0} m");
    }

    /// <summary>Per-frame drive, same arguments as <see cref="FlightAudio.Update"/> and called from
    /// the same place, so both paths run off the sim clock. Beyond the cull both slots stop; back
    /// inside, a healthy loop starts again but a damaged one waits out the shared re-arm timer.</summary>
    public void Update(float dt, in EngineDrive drive, float speedFrac, float healthFrac,
        bool engineDead = false)
    {
        bool damaged = EngineAudioCurves.EngineDamaged(healthFrac, engineDead) && _damagedStream != null;
        float distSq = AudioListeners.NearestDistanceSq(this, Listeners);
        bool culled = distSq > EngineAudioCurves.CullDistanceSq;
        if (culled != _culled)
        {
            _culled = culled;
            // Transitions only, and always logged: audio cannot be screenshot-verified, and this
            // line is what separates "silent because it is past the cull" from "silent because its
            // definition never resolved".
            Log.Debug("sound", $"ai engine {Aircraft()} {(culled ? "culled" : "audible")} at {Mathf.Sqrt(distSq):0} m (cull {EngineAudioCurves.CullDistance:0} m)");
        }
        if (culled)
        {
            _engine?.Stop();
            _whine?.Stop();
            _nitro?.Stop();
            // The original's cull skips the re-arm timer; only the damage edge's direction is kept.
            if (!damaged && _phase != EngineSlotPhase.Healthy)
                RestoreHealthyStream();
            else if (damaged && _phase == EngineSlotPhase.Healthy)
                _phase = EngineSlotPhase.Out;
            return;
        }
        if (_engine != null && StepEngineSlot(_engine, damaged, dt))
        {
            var (pitch, volume) = EngineAudioCurves.Engine(
                _stats, drive, _enginePitchMul, _enginePitchable);
            UpdateLoop(_engine, volume * (_phase == EngineSlotPhase.Damaged ? _damagedVol : _engineVol), pitch);
        }
        if (_whine != null)
        {
            var (pitch, volume) = EngineAudioCurves.Whine(_stats, speedFrac);
            UpdateLoop(_whine, volume * _whineVol, pitch);
        }
    }

    /// <summary>The injector's own loop, keyed rather than driven: a refresh gives it
    /// <see cref="NitroSystem.LoopKeyedSeconds"/> more, and it stops when nothing has refreshed it
    /// for that long. The caller passes <see cref="NitroSystem.LoopRefreshedThisTick"/>, so the
    /// cadence is the state machine's own call pattern and not a length invented here. Decode:
    /// docs/org/flightModel.md, "Nitro".</summary>
    public void RefreshNitroLoop(bool refreshed, float dt)
    {
        if (refreshed)
            _nitroKeyedS = NitroSystem.LoopKeyedSeconds;
        else if (_nitroKeyedS > 0f)
            _nitroKeyedS -= dt;
        if (_nitro == null)
            return;
        if (_nitroKeyedS > 0f)
            UpdateLoop(_nitro, _nitroVol, 1f);
        else if (_nitro.Playing)
            _nitro.Stop();
    }

    /// <summary>Kills every slot for good, the aircraft is down, and its crash animation owns
    /// everything audible from here on.</summary>
    public void Stop()
    {
        _engine?.Stop();
        _whine?.Stop();
        _nitro?.Stop();
        _nitroKeyedS = 0f;
    }

    // One slot's line in the build log: the definition this airframe names, and whether a stream
    // came back for it. "none" is the airframe naming no definition at all, which is what every
    // shipped whine slot reads.
    private static string SlotState(string? sndName, bool resolved) =>
        sndName == null ? "none" : $"{sndName}({(resolved ? "ok" : "unresolved")})";

    private static void UpdateLoop(AudioStreamPlayer3D player, float volume, float pitch)
    {
        if (volume <= 0.002f)
        {
            if (player.Playing)
                player.Stop();
            return;
        }
        if (!player.Playing)
            player.Play();
        player.PitchScale = pitch;
        player.VolumeDb = Mathf.LinearToDb(volume);
    }

    private static AudioStreamWav? LoadStream(SoundArchive archive,
        IReadOnlyDictionary<string, SoundDef> defs, string sndName, out float baseVolume)
    {
        baseVolume = 1f;
        if (!defs.TryGetValue(sndName, out var def))
        {
            GD.PushWarning($"sound def not found in sounds.json: {sndName}");
            return null;
        }
        baseVolume = def.Volume;
        return archive.Find(def.WavName, def.Looped);
    }

    // Which aircraft every line here is about: the controller this component hangs under, whose
    // name the spawner already logged. Never this node's own name, which is "EngineAudio" on all
    // of them.
    private string Aircraft() => GetParent()?.Name.ToString() ?? Name.ToString();

    // The engine slot's damage phase, stepped by the same rule the own-ship path runs so the two
    // cannot drift. Returns whether the slot should sound this frame: false while Out, the silence
    // the damage edge opens and the shared re-arm timer closes.
    private bool StepEngineSlot(AudioStreamPlayer3D engine, bool damaged, float dt)
    {
        var rng = Rng.Stream(Rng.FlightAudio);
        float u = damaged ? rng.Randf() : 0f;
        var from = _phase;
        bool sounding = engine.Playing;
        _phase = EngineAudioCurves.StepEnginePhase(from, damaged, sounding, _stats.DamagedTimer, dt, u);
        if (EngineAudioCurves.StartsDamagedLoop(from, _phase, sounding))
        {
            var (name, pitchMul) = EngineAudioCurves.EngineDefFor(_stats, true, rng);
            _enginePitchMul = pitchMul;
            _enginePitchable = EngineAudioCurves.SlotIsPitched(_defs, name);
            engine.Stop();
            engine.Stream = _damagedStream;
            Log.Debug("sound", $"ai engine {Aircraft()} slot 0 -> {name} pitchMul={_enginePitchMul:0.000} pitched={_enginePitchable}");
        }
        else if (_phase == EngineSlotPhase.Out)
        {
            engine.Stop();
        }
        else if (_phase == EngineSlotPhase.Healthy && from != EngineSlotPhase.Healthy)
        {
            RestoreHealthyStream();
        }
        return _phase != EngineSlotPhase.Out;
    }

    // The healthy direction swaps at once, no re-arm wait; UpdateLoop starts it on this frame.
    private void RestoreHealthyStream()
    {
        _phase = EngineSlotPhase.Healthy;
        var (name, pitchMul) = EngineAudioCurves.EngineDefFor(_stats, false, Rng.Stream(Rng.FlightAudio));
        _enginePitchMul = pitchMul;
        _enginePitchable = EngineAudioCurves.SlotIsPitched(_defs, name);
        bool wasPlaying = _engine!.Playing;
        _engine.Stop();
        _engine.Stream = _engineStream;
        if (wasPlaying)
            _engine.Play();
        Log.Debug("sound", $"ai engine {Aircraft()} slot 0 -> {name} pitchMul={_enginePitchMul:0.000} pitched={_enginePitchable}");
    }

    // RANGE is [full-volume distance, audible distance], mapped onto Godot's inverse-distance curve
    // the way WorldSounds maps every other 3D emitter, an approximation of the original's roll-off,
    // and TUNE for the same reason it is there.
    private AudioStreamPlayer3D? MakeLoop(SoundArchive archive,
        IReadOnlyDictionary<string, SoundDef> defs, string sndName, out float baseVolume)
    {
        var stream = LoadStream(archive, defs, sndName, out baseVolume);
        if (stream == null || !defs.TryGetValue(sndName, out var def))
        {
            return null;
        }
        var player = new AudioStreamPlayer3D
        {
            Stream = stream,
            UnitSize = def.RangeMin,
            MaxDistance = def.RangeMax,
            VolumeDb = -60f,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            Bus = AudioBuses.Effects,
        };
        AddChild(player);
        return player;
    }
}
