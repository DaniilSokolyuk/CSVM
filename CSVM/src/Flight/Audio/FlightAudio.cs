using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hud;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Audio;

/// <summary>
/// Own-plane sound, all from the player's own game data: the plane's engine loop (per-plane WAV via
/// vehicle.json 'engine_sound', throttle-driven pitch and volume, cut on the damage edge and then
/// swapped for 'damaged_engine_sound' once the re-arm timer fires (<see cref="EngineSlotPhase"/>), and for 'cockpit_engine_sound' while the pilot's SELECTED view is
/// Cockpit or Nose, <see cref="EngineAudioCurves.EngineDefFor"/> is the one precedence rule both
/// swaps share), the overspeed whine ('prop_sound', which no shipped def names), the
/// airframe rattle (player.json 'rattle', a gate at full level past fd_speed), plus the one-shots
/// (snd_propstart/snd_propstop). Non-positional players: these are what the pilot hears, and
/// <see cref="Ai.AiEngineAudio"/> is the positional twin every other aircraft carries (own-ship only,
/// an AI rig has no selected view, so it never reads 'cockpit_engine_sound').
/// </summary>
public partial class FlightAudio : Node
{
    /// <summary>Overall gain for this plane's own-ship mix. 1 for single player;
    /// splitscreen sets 1/√N so N simultaneous engine stacks don't sum to a wall of noise
    /// (equal-power, so 2P ≈ −3 dB each, 4P ≈ −6 dB). TUNE, pending a real 4P listen.</summary>
    public float MixGain = 1f;

    private const float SilenceThreshold = 0.002f;
    // s for the loop to fade to full behind snd_propstart. Sourced from startprops' authored prop
    // cross-fade (plane_props.zrd.json, OBJECT_OPACITY_FROM_TO RUN_TIME 2.0 on staticpropN→propN)
    //, the nearest authored duration. TUNE pending a listen A/B.
    private const float EngineStartRamp = 2.0f;

    private readonly List<(string name, AudioStreamWav stream, float volume)> _crashSounds = new();

    private PlaneStats _stats = null!;
    private AudioStreamPlayer? _engine, _whine, _rattle;
    private float _engineVol = 1f, _whineVol = 1f, _rattleVol = 1f; // sounds.json VOLUME base gain

    // The engine slot's three candidate streams, all resolved at Setup so a mid-flight swap is an
    // assignment rather than an archive read: the session's SoundArchive outlives the build, but a
    // decode at the moment the airframe is hit or the view changes is a hitch nobody needs.
    private AudioStreamWav? _engineStream, _damagedStream, _cockpitStream;
    private float _damagedVol = 1f, _cockpitVol = 1f;
    private EngineSlotPhase _enginePhase;     // Healthy holds the plain or cockpit stream
    private bool _engineCockpitView;
    private float _enginePitchMul = 1f;       // the damaged swap's one-off pitch draw
    private bool _enginePitchable;            // whether the def on the slot takes a frequency write
    private AudioStreamPlayer? _crash;
    private AudioStreamPlayer? _groundExp, _waterExp;
    private float _groundExpVol = 1f, _waterExpVol = 1f;
    private AudioStreamPlayer? _grazeGround, _grazeWater;
    private float _grazeGroundVol = 1f, _grazeWaterVol = 1f;
    private AudioStreamPlayer? _propStart, _propStop;
    private float _propStartVol = 1f, _propStopVol = 1f;
    private AudioStreamPlayer? _dzCamera;
    private float _dzCameraVol = 1f;
    private float _engineRamp = 1f; // 0→1 gain envelope while the engine catches after a start

    // Gun firing: kept references so the looped firing sound + empty-clip cue can be built
    // on demand from any caliber's LOOPED_SOUND_NAME. Own-ship, non-positional (like the engine).
    private SoundArchive? _archive;
    private IReadOnlyDictionary<string, SoundDef>? _defs;
    private AudioStreamPlayer? _gunLoop;
    private string? _gunLoopName;
    private float _gunLoopVol = 1f;
    private AudioStreamPlayer? _nitroLoop;
    private float _nitroLoopVol = 1f;
    private AudioStreamPlayer? _emptyClip;
    private float _emptyClipVol = 1f;

    // The three incoming-fire cues, each a SOUND_GROUPS name rather than a plain definition, so the
    // variant is drawn per play through the group's own weighted recency rather than fixed at
    // Setup. One player each, restreamed. All flat: the original hands none of the three a position
    // (docs/org/weaponFire.md), whatever the members' own 3D flags say.
    private SoundGroup? _warningShotGroup, _bulletHitGroup, _windowHitGroup;
    private AudioStreamPlayer? _warningShot, _bulletHit, _windowHit;
    private System.Random? _cueRng;

    /// <summary>Which damage phase the engine slot is in (<see cref="EngineSlotPhase"/>), for the
    /// suite that walks the slot through the damage edge, the silence and the damaged loop.</summary>
    internal EngineSlotPhase EnginePhase => _enginePhase;

    /// <summary>Whether the engine slot is sounding, read off the live player.</summary>
    internal bool EngineSounding => _engine is { Playing: true };

    /// <summary>The engine slot's start ramp, 0 to 1; a respawn restarts it and a damaged start does not.</summary>
    internal float EngineRampLevel => _engineRamp;

    /// <summary>Whether the slot currently holds the <c>damaged_engine_sound</c> stream.</summary>
    internal bool EngineHoldsDamagedStream => _engine != null && _damagedStream != null
        && ReferenceEquals(_engine.Stream, _damagedStream);

    public void Setup(SoundArchive archive, Dictionary<string, SoundDef> defs, PlaneStats stats,
        WeaponDefs? weapons, IReadOnlyDictionary<string, SoundGroup>? groups = null)
    {
        _stats = stats;
        _archive = archive;
        _defs = defs;
        // The shared empty-clip cue (weapons.json NO_AMMO_WARNING), resolved through the selection
        // both weapon paths read so this and AiWeaponAudio cannot come to play different
        // definitions. Flat, because this is what the pilot hears.
        if (WeaponAudioCues.EmptyClip(archive, defs, weapons) is { } dry)
        {
            _emptyClipVol = dry.Volume;
            _emptyClip = new AudioStreamPlayer { Stream = dry.Stream, Bus = AudioBuses.Effects };
            AddChild(_emptyClip);
        }
        else
        {
            GD.PushWarning($"empty-clip cue unresolved: {WeaponAudioCues.EmptyClipName(weapons) ?? "none"}");
        }
        _engine = MakeLoop(archive, defs, stats.EngineSound, out _engineVol);
        _engineStream = _engine?.Stream as AudioStreamWav;
        // The slot starts on the plain definition, and only a swap goes through the two paths that
        // re-read this, so the opening state has to be set here rather than left at its default.
        _enginePitchable = EngineAudioCurves.SlotIsPitched(defs, stats.EngineSound);
        // Not built as a player of its own: this stream replaces the engine's on the one slot, so a
        // second player would be the blend the decode refuted.
        if (stats.DamagedEngineSound is { } damagedName)
            _damagedStream = LoadStream(archive, defs, damagedName, out _damagedVol);
        // Same non-player pattern: a swap candidate, not a player of its own (D31).
        if (stats.CockpitEngineSound is { } cockpitName)
            _cockpitStream = LoadStream(archive, defs, cockpitName, out _cockpitVol);
        if (stats.WhineSound is { } whineName)
            _whine = MakeLoop(archive, defs, whineName, out _whineVol);
        _rattle = MakeLoop(archive, defs, stats.RattleSound, out _rattleVol);

        // Prop start/stop one-shots (both non-looped, no VOLUME field → base gain 1.0):
        // snd_propstart plays as the engine ramps in on (re)spawn; snd_propstop is the
        // wind-down for a future landing/shutdown (see OnEngineStop).
        _propStart = MakeOneShot(archive, defs, "snd_propstart", out _propStartVol);
        _propStop = MakeOneShot(archive, defs, "snd_propstop", out _propStopVol);

        // The stunt run's camera sting, a flat SFX definition no SOUND_GROUPS entry and no world
        // data names: what the Danger Zone photograph sounds like.
        _dzCamera = MakeOneShot(archive, defs, "snd_dangerzone_camera", out _dzCameraVol);

        // Crash explosions: the game defines snd_exp_plane1..4 as a set; pick one at
        // random per crash, like the original.
        for (int i = 1; i <= 4; i++)
        {
            if (defs.TryGetValue($"snd_exp_plane{i}", out var def)
                && archive.Find(def.WavName, looped: false) is { } stream)
            {
                _crashSounds.Add((def.Name, stream, def.Volume));
            }
        }
        if (_crashSounds.Count > 0)
        {
            _crash = new AudioStreamPlayer { Bus = AudioBuses.Effects };
            AddChild(_crash);
        }

        // The ground/water crash booms, layered over plane_destroy_sg (docs/architecture.md).
        _groundExp = MakeOneShot(archive, defs, "snd_exp_ground_a", out _groundExpVol);
        _waterExp = MakeOneShot(archive, defs, "snd_exp_water_a", out _waterExpVol);

        // The graze reaction's authored sounds (touchdown.zrd), the lighter `_b` pair.
        _grazeGround = MakeOneShot(archive, defs, "snd_exp_ground_b", out _grazeGroundVol);
        _grazeWater = MakeOneShot(archive, defs, "snd_exp_water_b", out _grazeWaterVol);

        // The incoming-fire set, own-ship and non-positional so in splitscreen only the pilot being
        // shot at hears it: the absorbed round (player.json warning_shot_sound), the airframe
        // ricochet (player.json bullet_hit_sound) and the canopy glass (named by the hole defs).
        _cueRng = Rng.NewSystemRandom(Rng.Weapons);
        _warningShot = MakeGroupCue(groups, stats.WarningShotSound, out _warningShotGroup);
        _bulletHit = MakeGroupCue(groups, stats.BulletHitSound, out _bulletHitGroup);
        _windowHit = MakeGroupCue(groups, CanopyHoleCue.WindowHitSound, out _windowHitGroup);
    }

    /// <summary>Start (or keep playing) the gun firing loop for the given <c>LOOPED_SOUND_NAME</c>.
    /// Rebuilds the player only when the sound changes (a different caliber group starts firing).
    /// The cue itself comes from <see cref="WeaponAudioCues"/>, which <see cref="Ai.AiWeaponAudio"/>
    /// reads too; this path stays flat, since it is what the pilot hears.</summary>
    public void StartGunLoop(string? sndName)
    {
        if (string.IsNullOrEmpty(sndName))
        {
            return;
        }
        if (_gunLoopName != sndName)
        {
            _gunLoop?.Stop();
            _gunLoop = null;
            if (WeaponAudioCues.GunLoop(_archive, _defs, sndName) is { } cue)
            {
                _gunLoop = new AudioStreamPlayer { Stream = cue.Stream, Bus = AudioBuses.Effects };
                AddChild(_gunLoop);
                _gunLoopName = sndName;
                _gunLoopVol = cue.Volume;
            }
        }
        if (_gunLoop is { Playing: false })
        {
            _gunLoop.VolumeDb = Mathf.LinearToDb(Mathf.Max(SilenceThreshold, _gunLoopVol * MixGain));
            _gunLoop.Play();
        }
    }

    public void StopGunLoop() => _gunLoop?.Stop();

    /// <summary>Start (or keep playing) the <c>snd_nitro</c> sustain loop, own-ship like the gun
    /// loop; the start and stop stings are the boost/decay defs' own sound events.</summary>
    public void StartNitroLoop()
    {
        if (_nitroLoop == null && _archive != null && _defs != null
            && _defs.TryGetValue("snd_nitro", out var def)
            && _archive.Find(def.WavName, looped: true) is { } stream)
        {
            _nitroLoop = new AudioStreamPlayer { Stream = stream, Bus = AudioBuses.Effects };
            AddChild(_nitroLoop);
            _nitroLoopVol = def.Volume;
        }
        if (_nitroLoop is { Playing: false })
        {
            _nitroLoop.VolumeDb = Mathf.LinearToDb(Mathf.Max(SilenceThreshold, _nitroLoopVol * MixGain));
            _nitroLoop.Play();
        }
    }

    public void StopNitroLoop() => _nitroLoop?.Stop();

    /// <summary>The empty-clip cue, one shot when a dry gun's trigger is pulled.</summary>
    public void PlayEmptyClip() => PlayOneShot(_emptyClip, _emptyClipVol * MixGain);

    public override void _Ready() => StartEngine(); // whine/rattle start on demand

    /// <summary>Per-frame drive: <paramref name="speedFrac"/> is speed / fd_speed,
    /// <paramref name="healthFrac"/> and <paramref name="engineDead"/> are the damaged-engine
    /// inputs <see cref="EngineAudioCurves.EngineDamaged"/> gates on, and
    /// <paramref name="cockpitView"/> is whether the pilot's SELECTED view is the full Cockpit. A
    /// held numpad key or look-behind is a pose, not a selection, so neither retriggers that swap.
    /// Not called while crashed, so the loops stay dead until respawn.</summary>
    public void Update(float dt, in EngineDrive drive, float speedFrac, float healthFrac,
        bool engineDead = false, bool cockpitView = false)
    {
        if (_engineRamp < 1f)
            _engineRamp = Mathf.Min(1f, _engineRamp + dt / EngineStartRamp);
        UpdateEngineSlot(dt, EngineAudioCurves.EngineDamaged(healthFrac, engineDead), cockpitView);
        // Out is the damaged engine's silence, not a stopped loop to restart.
        if (_engine != null && _enginePhase != EngineSlotPhase.Out)
        {
            if (_enginePhase == EngineSlotPhase.Healthy && !_engine.Playing)
                StartEngine(); // respawn after a crash: propstart + fresh volume ramp-in
            var (pitch, volume) = EngineAudioCurves.Engine(
                _stats, drive, _enginePitchMul, _enginePitchable);
            _engine.PitchScale = pitch;
            float baseVol = _enginePhase == EngineSlotPhase.Damaged ? _damagedVol
                : _engineCockpitView ? _cockpitVol : _engineVol;
            _engine.VolumeDb = Mathf.LinearToDb(Mathf.Max(SilenceThreshold,
                volume * baseVol * _engineRamp * MixGain));
        }
        if (_whine != null)
        {
            var (pitch, volume) = EngineAudioCurves.Whine(_stats, speedFrac);
            UpdateLoop(_whine, volume * _whineVol * MixGain, pitch);
        }
        UpdateLoop(_rattle, EngineAudioCurves.Rattle(_stats, speedFrac) * _rattleVol * MixGain, 1f);
    }

    /// <summary>Holds (or releases) the own-plane loops where they are, for the sim-clock halt:
    /// the engine/whine/rattle keep their sample position and volume instead of droning through a
    /// frozen frame. One-shots already in flight are deliberately left to play out, they are
    /// short and stopping them mid-sample is the louder artefact.</summary>
    public void SetPaused(bool paused)
    {
        if (_engine != null)
        {
            _engine.StreamPaused = paused;
        }
        if (_whine != null)
        {
            _whine.StreamPaused = paused;
        }
        if (_rattle != null)
        {
            _rattle.StreamPaused = paused;
        }
    }

    /// <summary>Kills the flight loops (dead engine) and fires one of the game's
    /// plane-explosion one-shots.</summary>
    public void OnCrash()
    {
        ResetEngineSlot();
        _whine?.Stop();
        _rattle?.Stop();
        if (_crash == null)
            return;
        var (name, stream, volume) = _crashSounds[
            (int)(Rng.Stream(Rng.FlightAudio).Randi() % (uint)_crashSounds.Count)];
        _crash.Stream = stream;
        // Takes MixGain (D32, docs/architecture.md): a splitscreen pile-up fires this
        // once per downed rig in the same instant, so N booms must not sum into a clipped wall.
        float gain = volume * MixGain;
        _crash.VolumeDb = Mathf.LinearToDb(Mathf.Max(SilenceThreshold, gain));
        _crash.Play();
        // Which of the four explosions played, plus D32's computed gain, the only
        // trace this pick leaves outside the speakers.
        Log.Info("sound", $"crash sound: {name} MixGain={MixGain:0.00} vol={gain:0.000}");
    }

    /// <summary>The `dirt`(13) crash's earth-impact boom (snd_exp_ground_a), layered over the
    /// plane explosion <see cref="OnCrash"/> already fired. Called only when `player_crash_dirt`
    /// is the resolved def (FlightController.PlayCrashBoom), so it does not sound on a sea dive,
    /// the `player_crash_default` fallback, or a future mid-air destruct (which plays no
    /// `player_crash_*` def at all).</summary>
    // D32: layers over OnCrash's boom in the same instant, so it takes MixGain for the
    // same reason, a pile-up stacks this once per downed rig too.
    public void OnGroundExplosion()
    {
        float gain = _groundExpVol * MixGain;
        PlayOneShot(_groundExp, gain);
        Log.Info("sound", $"crash sound: snd_exp_ground_a MixGain={MixGain:0.00} vol={gain:0.000}");
    }

    /// <summary>The sea dive's counterpart (snd_exp_water_a, the `_a` pair, not the graze's
    /// lighter `_b`), layered over the plane explosion the same way. Authored one level down, in
    /// the plane_big_splash player_crash_water calls; the crash runtime renders effects only, so
    /// the sound comes from here.</summary>
    // D32: same crash-instant stacking as OnGroundExplosion; MixGain for the same reason.
    public void OnWaterExplosion()
    {
        float gain = _waterExpVol * MixGain;
        PlayOneShot(_waterExp, gain);
        Log.Info("sound", $"crash sound: snd_exp_water_a MixGain={MixGain:0.00} vol={gain:0.000}");
    }

    /// <summary>The survivable scrape's authored bark, alongside the <c>touchdown_*</c> effect the
    /// world-effects runtime renders: snd_exp_water_b off water, snd_exp_ground_b off everything
    /// else. Rate-limited by the caller (FlightController), not here, a long scrape would
    /// otherwise re-fire it every physics frame.</summary>
    public void OnGraze(bool water) => PlayOneShot(water ? _grazeWater : _grazeGround,
        (water ? _grazeWaterVol : _grazeGroundVol) * MixGain);

    /// <summary>A gun round the shield absorbed: one draw from the warning-shot group, which the
    /// original plays INSTEAD of the ricochet on a round whose damage it discards
    /// (<see cref="WarningShotCue"/> holds the rule). Returns the variant that played, or null when
    /// the cue is unbuilt.</summary>
    public string? OnWarningShot() => PlayGroupCue(_warningShot, _warningShotGroup);

    /// <summary>A gun round struck this airframe and the shield did not take it: one draw from
    /// <c>bullet_hit_sound</c>. Every such round rings it, as the original's does, the rate limit
    /// belongs to the canopy cue. Dispatched from the projectile-hit path, never from a
    /// contact.</summary>
    public string? OnBulletHit() => PlayGroupCue(_bulletHit, _bulletHitGroup);

    /// <summary>A canopy hole opened: one draw from <c>window_hit_sg</c>. The cadence is
    /// <see cref="CanopyHoleCue"/>'s, and the caller has already decided.</summary>
    public string? OnWindowHit() => PlayGroupCue(_windowHit, _windowHitGroup);

    /// <summary>The Danger Zone camera sting, one shot as the run latches that marker's
    /// photograph (<see cref="Modes.StuntCapture"/>). Takes MixGain like the other own-ship cues, so four
    /// racing pilots crossing markers at once do not sum into one wall of shutter.</summary>
    public void OnDangerZoneCamera()
    {
        float gain = _dzCameraVol * MixGain;
        PlayOneShot(_dzCamera, gain);
        Log.Info("sound", $"stunt capture: snd_dangerzone_camera MixGain={MixGain:0.00} vol={gain:0.000}");
    }

    /// <summary>Engine wind-down: plays snd_propstop and kills the loops. Layers over the crash
    /// explosion one-shot (<see cref="OnCrash"/>) rather than replacing it, FlightController
    /// calls both from the same crash/destruction moment, snd_propstop right after the boom, so
    /// the loops end on the authored cue instead of a cut. The loop-restart hook in
    /// <see cref="Update"/> is what fires snd_propstart again on the next respawn; nothing here
    /// needs to prevent that.</summary>
    public void OnEngineStop()
    {
        ResetEngineSlot();
        _whine?.Stop();
        _rattle?.Stop();
        // D32: fires right after OnCrash's boom, the same crash instant, MixGain for
        // the same pile-up reason, not the "your prop" respawn cue StartEngine plays below.
        float gain = _propStopVol * MixGain;
        PlayOneShot(_propStop, gain);
        Log.Info("sound", $"engine stop: snd_propstop MixGain={MixGain:0.00} vol={gain:0.000}");
    }

    private static void PlayOneShot(AudioStreamPlayer? player, float volume)
    {
        if (player == null)
            return;
        player.VolumeDb = Mathf.LinearToDb(Mathf.Max(SilenceThreshold, volume));
        player.Play();
    }

    private static void UpdateLoop(AudioStreamPlayer? player, float volume, float pitch)
    {
        if (player == null)
            return;
        if (volume <= SilenceThreshold)
        {
            if (player.Playing)
                player.Stop();
            return;
        }
        if (!player.Playing)
            player.Play();
        player.PitchScale = Mathf.Max(0.01f, pitch);
        player.VolumeDb = Mathf.LinearToDb(volume);
    }

    // A looped definition's stream and its unscaled VOLUME, same convention as WorldSounds' 3D
    // emitters. Split out from MakeLoop so the engine slot's swap candidate can be resolved without
    // building a player nothing would ever start.
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

    // One SOUND_GROUPS cue on a player of its own. The group is kept rather than a stream, since
    // the member is drawn per play; an unresolved name warns here and leaves the cue silent.
    private AudioStreamPlayer? MakeGroupCue(IReadOnlyDictionary<string, SoundGroup>? groups,
        string name, out SoundGroup? group)
    {
        group = null;
        if (groups == null || !groups.TryGetValue(name, out var found))
        {
            GD.PushWarning($"sound group not found in sounds.json: {name}");
            return null;
        }
        group = found;
        var player = new AudioStreamPlayer { Bus = AudioBuses.Effects };
        AddChild(player);
        return player;
    }

    // One draw from a group cue, restreamed onto that cue's own player. Null when the cue is
    // unbuilt or the draw names a definition the archive has no WAV for.
    private string? PlayGroupCue(AudioStreamPlayer? player, SoundGroup? group)
    {
        if (player == null || group == null || _archive == null || _defs == null)
            return null;
        string? name = group.Pick(_cueRng!);
        if (name == null || !_defs.TryGetValue(name, out var def))
            return null;
        var stream = _archive.Find(def.WavName, looped: false);
        if (stream == null)
            return null;
        player.Stream = stream;
        PlayOneShot(player, def.Volume * MixGain);
        return name;
    }

    // Steps the slot's damage phase with EngineAudioCurves.StepEnginePhase, the rule AiEngineAudio
    // runs too, and acts on a change. The edge silences the slot, the fired re-arm timer starts
    // damaged_engine_sound at a drawn multiplier, and a cleared mask restores the healthy loop at
    // once. While healthy, the view picks the stream.
    private void UpdateEngineSlot(float dt, bool damaged, bool cockpitView)
    {
        if (_engine == null)
        {
            return;
        }
        damaged &= _damagedStream != null;
        var rng = Rng.Stream(Rng.FlightAudio);
        // Drawn only while damaged, so a healthy flight leaves the stream the crash pick reads alone.
        float u = damaged ? rng.Randf() : 0f;
        var from = _enginePhase;
        bool sounding = _engine.Playing;
        _enginePhase = EngineAudioCurves.StepEnginePhase(from, damaged, sounding,
            _stats.DamagedTimer, dt, u);
        if (EngineAudioCurves.StartsDamagedLoop(from, _enginePhase, sounding))
        {
            var (name, pitchMul) = EngineAudioCurves.EngineDefFor(_stats, true, rng);
            _enginePitchMul = pitchMul;
            _enginePitchable = EngineAudioCurves.SlotIsPitched(_defs, name);
            _engine.Stop();
            _engine.Stream = _damagedStream;
            _engineRamp = 1f; // the original starts it at full level, with no start cue
            _engine.Play();
            Log.Info("sound", $"engine sound: slot 0 -> {name} pitchMul={_enginePitchMul:0.000} pitched={_enginePitchable}");
        }
        else if (_enginePhase == from)
        {
            if (from == EngineSlotPhase.Healthy)
                UpdateCockpitSwap(cockpitView);
        }
        else if (_enginePhase == EngineSlotPhase.Out)
        {
            _engine.Stop();
            Log.Info("sound", $"engine sound: slot 0 out, waiting on the damaged re-arm timer");
        }
        else
        {
            // Out has nothing to wait for; a stopped damaged loop is the crash the hook restarts.
            RestoreHealthyStream(cockpitView, play: from == EngineSlotPhase.Out || sounding);
        }
    }

    // The healthy arm's view swap: cockpit_engine_sound while the pilot's SELECTED view is the full
    // Cockpit, engine_sound otherwise. A hard cut, no crossfade is decoded.
    private void UpdateCockpitSwap(bool cockpitView)
    {
        if ((cockpitView && _cockpitStream != null) != _engineCockpitView)
        {
            RestoreHealthyStream(cockpitView, play: _engine!.Playing);
        }
    }

    private void RestoreHealthyStream(bool cockpitView, bool play)
    {
        _engineCockpitView = cockpitView && _cockpitStream != null;
        var (name, pitchMul) = EngineAudioCurves.EngineDefFor(
            _stats, false, Rng.Stream(Rng.FlightAudio), _engineCockpitView);
        _enginePitchMul = pitchMul;
        _enginePitchable = EngineAudioCurves.SlotIsPitched(_defs, name);
        _engine!.Stop();
        _engine.Stream = _engineCockpitView ? _cockpitStream : _engineStream;
        if (play)
            _engine.Play();
        // The headless observable for a swap nobody can screenshot: which def the slot took, and
        // whether that def accepts the throttle curve's pitch at all.
        Log.Info("sound", $"engine sound: slot 0 -> {name} pitchMul={_enginePitchMul:0.000} pitched={_enginePitchable}");
    }

    // A dead aircraft's slot goes back to healthy and silent, so the respawn's own start cue is
    // what brings it back rather than a leftover damaged phase.
    private void ResetEngineSlot()
    {
        if (_engine == null)
        {
            return;
        }
        _engine.Stop();
        _enginePhase = EngineSlotPhase.Healthy;
        _enginePitchMul = 1f;
        _enginePitchable = EngineAudioCurves.SlotIsPitched(
            _defs, _engineCockpitView ? _stats.CockpitEngineSound : _stats.EngineSound);
        _engine.Stream = _engineCockpitView ? _cockpitStream : _engineStream;
    }

    private AudioStreamPlayer? MakeLoop(SoundArchive archive,
        IReadOnlyDictionary<string, SoundDef> defs, string sndName, out float baseVolume)
    {
        var stream = LoadStream(archive, defs, sndName, out baseVolume);
        if (stream == null)
            return null;
        var player = new AudioStreamPlayer { Stream = stream, VolumeDb = -60f, Bus = AudioBuses.Effects };
        AddChild(player);
        return player;
    }

    // Loads a non-looped one-shot (crash/prop start/stop) from a sounds.json def.
    private AudioStreamPlayer? MakeOneShot(SoundArchive archive,
        IReadOnlyDictionary<string, SoundDef> defs, string sndName, out float baseVolume)
    {
        baseVolume = 1f;
        if (!defs.TryGetValue(sndName, out var def))
        {
            GD.PushWarning($"sound def not found in sounds.json: {sndName}");
            return null;
        }
        var stream = archive.Find(def.WavName, looped: false);
        if (stream == null)
            return null;
        baseVolume = def.Volume; // unscaled, same convention as MakeLoop above
        var player = new AudioStreamPlayer { Stream = stream, Bus = AudioBuses.Effects };
        AddChild(player);
        return player;
    }

    // Spin the engine loop up from silence behind snd_propstart. Used for the
    // initial spawn (here) and every respawn (via the loop-restart hook in Update).
    private void StartEngine()
    {
        _engineRamp = 0f;
        _engine?.Play();
        // Stays at raw volume, deliberately unlike the crash-boom family, a respawn does not
        // pile up with other rigs' at one instant.
        PlayOneShot(_propStart, _propStartVol);
    }
}
