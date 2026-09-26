using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using CSVM.UI.Menu;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Launch;

/// <summary>
/// The menu host's <see cref="IMenuAudio"/> over the process's own playback: the music channel
/// and the sound archive <c>Launcher</c> already keeps, plus the briefing narration player, which
/// used to be the launchscreen's, and a cue player for the menu's own sounds. Narration ducks the
/// music for as long as it is begun and lifts the duck on end, the whole-stay duck the briefing
/// asks for; a missing stream is silence, not a refusal. A cue resolves through
/// <see cref="MenuCueTable"/> to a wav under the extracted rof tree's sounds folder, decoded once
/// and replayed from the start on every ask; a name the table lacks or a file the tree lacks is
/// logged once and stays silent. A mix page's live preview lands here too: the page states four
/// levels and which one moved, this applies them and sounds that category's own clip.
/// </summary>
public sealed partial class MenuAudioService : Node, IMenuAudio
{
    // The two clips a moved level is judged by, the original's own preview files for those
    // categories (its AUDIO script builds a sound object per category over music_loop.wav,
    // sfx_loop.wav and voice_loop.wav). Music needs none of the three here: the menu's score is
    // already sounding on that bus, which is what the Music and Master rows move.
    // ⚠ Firing one on a move is a deliberate departure: the original starts its three at page open
    // and sends only setvolume on a move (docs/menu-presentations.md's Audio section).
    private const string EffectsPreviewFile = "SFX_LOOP.WAV";
    private const string VoicePreviewFile = "VOICE_LOOP.WAV";

    private readonly MusicPlayer? _music;
    private readonly Func<string, AudioStreamWav?> _sounds;
    private readonly string? _cueDir;
    private readonly bool _previews;
    private readonly Dictionary<string, AudioStreamWav?> _cues = new(StringComparer.Ordinal);
    // Decoded preview clips by file name, cached like a cue: a null is cached like a hit, so a
    // level dragged across its range cannot log once per change about a clip the install lacks.
    private readonly Dictionary<string, AudioStreamWav?> _clips = new(StringComparer.Ordinal);
    private AudioStreamPlayer _narration = null!;
    private AudioStreamPlayer _cuePlayer = null!;
    private AudioStreamPlayer _effectsPreview = null!;
    private AudioStreamPlayer _voicePreview = null!;
    private AudioBusGains? _mixBeforePreview;
    private AudioLevels? _previewLevels;
    private MenuMixLevel _sounding;
    private int _previewStarts;
    // How many narrations have begun since the last end, so the log reads 1 on entry and 2 on
    // REPLAY BRIEFING, as the launchscreen's own counter did.
    private int _starts;

    /// <summary>A service over <paramref name="music"/> (null for a silent install) resolving
    /// narration WAVs through <paramref name="sounds"/> and menu cues under <paramref name="cueDir"/>
    /// (the extracted rof tree's <c>ASSETS/SOUNDS</c>; null leaves every cue silent).
    /// ⚠ <paramref name="previews"/> is whether a mix page's live preview may apply and sound, and
    /// defaults to off: a run that drives itself must not have its mix become a function of which
    /// menu page a walk opened (<c>docs/menu-presentations.md</c>).</summary>
    public MenuAudioService(MusicPlayer? music, Func<string, AudioStreamWav?> sounds, string? cueDir = null, bool previews = false)
    {
        _music = music;
        _sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
        _cueDir = cueDir;
        _previews = previews;
    }

    /// <summary>How many preview clips have begun since this service was built. A drag moves a
    /// level many times a second, so this is what a suite reads to hold the preview to one clip per
    /// move rather than one per frame.</summary>
    public int MixPreviewStarts => _previewStarts;

    public override void _Ready()
    {
        // Two categories from one node: a briefing narration is a spoken line and a menu cue is a
        // sound effect, so they take different buses. Master still reaches both, since every child
        // bus sends into it and that is where --volume=/audio.volume applies.
        _narration = new AudioStreamPlayer { Name = "briefing_narration", Bus = AudioBuses.Voice };
        AddChild(_narration);
        _cuePlayer = new AudioStreamPlayer { Name = "menu_cue", Bus = AudioBuses.Effects };
        AddChild(_cuePlayer);
        // A player per bus a level can be judged on, and not the two above: a preview holding the
        // cue player would cut off the click that opened the row, and one holding the narration
        // player would fight a briefing.
        _effectsPreview = new AudioStreamPlayer { Name = "mix_preview_effects", Bus = AudioBuses.Effects };
        AddChild(_effectsPreview);
        _voicePreview = new AudioStreamPlayer { Name = "mix_preview_voice", Bus = AudioBuses.Voice };
        AddChild(_voicePreview);
    }

    public void Cue(MenuCue cue)
    {
        if (!_cues.TryGetValue(cue.Name, out var stream))
        {
            stream = LoadCue(cue.Name);
            _cues[cue.Name] = stream;
        }

        if (stream == null)
        {
            return;
        }

        _cuePlayer.Stop();
        _cuePlayer.Stream = stream;
        _cuePlayer.Play();
        Log.Debug("sound", $"menu cue name={cue.Name} file={MenuCueTable.FileFor(cue.Name)}");
    }

    public void PreviewMix(AudioLevels levels, MenuMixLevel moved)
    {
        if (!_previews)
        {
            return;
        }

        // The mix the page opened over, taken once and held until the preview ends, so a cancel puts
        // back what actually stood rather than the shipped defaults. The levels behind those gains
        // are the launch's and this service never saw them.
        _mixBeforePreview ??= AudioMix.Capture();
        if (_previewLevels != levels)
        {
            // Only on a change: a page states its mix every frame it is open, and applying an
            // unchanged mix would write the same three gains and log a line sixty times a second.
            _previewLevels = levels;
            AudioMix.Apply(levels.Master, levels.Music, levels.Effects, levels.Voice);
        }

        var player = moved switch
        {
            MenuMixLevel.Effects => _effectsPreview,
            MenuMixLevel.Voice => _voicePreview,
            _ => null,
        };
        // ⚠ A clip already sounding is left to run: a drag moves a level many times a second, and
        // restarting on each of those is a stutter rather than a preview. Master and Music reach no
        // clip at all, the menu's own score already sounding on the bus they both move.
        if (player == null || (_sounding == moved && player.Playing))
        {
            return;
        }

        var stream = PreviewClip(moved);
        if (stream == null)
        {
            return;
        }

        _sounding = moved;
        _previewStarts++;
        player.Stop();
        player.Stream = stream;
        player.Play();
        Log.Debug("sound", $"mix preview level={moved} start={_previewStarts}");
    }

    public void EndMixPreview()
    {
        if (_mixBeforePreview is not { } gains)
        {
            return;
        }

        _mixBeforePreview = null;
        _previewLevels = null;
        _sounding = MenuMixLevel.None;
        _effectsPreview.Stop();
        _voicePreview.Stop();
        AudioMix.Restore(gains);
    }

    public void BeginNarration(string wavName)
    {
        ArgumentNullException.ThrowIfNull(wavName);
        if (_music != null)
        {
            _music.Ducked = true;
        }

        _narration.Stop();
        _narration.Stream = wavName.Length > 0 ? _sounds(wavName) : null;
        if (_narration.Stream != null)
        {
            _narration.Play();
        }

        _starts++;
        // Through the sink, not GD.Print: a scripted run at --volume=0 has no sound to hear, and
        // this line is what says the narration started at all.
        string got = _narration.Stream != null ? "yes" : "no";
        Log.Info("sound", $"briefing narration start={_starts} wav={wavName} stream={got}");
    }

    public void EndNarration()
    {
        _starts = 0;
        if (_music != null)
        {
            _music.Ducked = false;
        }

        if (_narration.Playing)
        {
            _narration.Stop();
        }

        _narration.Stream = null;
    }

    // The clip a moved level is judged by, decoded once and cached like a cue. The two files are
    // the service's own, not cue-table names: no presentation asks for them, and the page that
    // moves a level names a level rather than a sound.
    private AudioStreamWav? PreviewClip(MenuMixLevel moved)
    {
        string file = moved == MenuMixLevel.Voice ? VoicePreviewFile : EffectsPreviewFile;
        if (!_clips.TryGetValue(file, out var stream))
        {
            stream = LoadWav(file, $"mix preview level={moved}");
            _clips[file] = stream;
        }

        return stream;
    }

    // The one decode per cue name. Every failure is a debug line and a null, cached like a hit,
    // so a menu cannot log once per frame about a sound it does not have.
    private AudioStreamWav? LoadCue(string name)
    {
        string? file = MenuCueTable.FileFor(name);
        if (file == null)
        {
            Log.Debug("sound", $"menu cue name={name} unresolved: not in the cue table");
            return null;
        }

        return LoadWav(file, $"menu cue name={name}");
    }

    // One wav under the cue directory, with <paramref name="what"/> naming the asker so a missing
    // file reads the same whether a cue or a preview wanted it.
    private AudioStreamWav? LoadWav(string file, string what)
    {
        if (_cueDir == null)
        {
            Log.Debug("sound", $"{what} file={file} silent: no cue directory");
            return null;
        }

        string path = Path.Combine(_cueDir, file);
        if (!File.Exists(path))
        {
            Log.Debug("sound", $"{what} file={file} silent: not at {path}");
            return null;
        }

        try
        {
            var wav = WavFile.Parse(File.ReadAllBytes(path));
            var data = new byte[wav.Samples.Length * 2];
            Buffer.BlockCopy(wav.Samples, 0, data, 0, data.Length);
            return new AudioStreamWav
            {
                Data = data,
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = wav.SampleRate,
                Stereo = wav.Channels == 2,
                LoopMode = AudioStreamWav.LoopModeEnum.Disabled,
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or ArgumentException)
        {
            Log.Warn("sound", $"{what} file={file} failed to decode: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }
}
