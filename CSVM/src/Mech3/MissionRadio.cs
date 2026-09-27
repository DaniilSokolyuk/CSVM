using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// The mission radio queue: the non-positional voice channel a mission's objective callouts and
/// every combat voice line play on, beside <see cref="MusicPlayer"/>'s streaming channel and <see cref="WorldSounds"/>'s pooled
/// 3D emitters. A cue names a radio definition or a VO dialogue chain; a chain plays its lines in
/// order as one call, and a call that arrives while another is speaking queues behind it rather
/// than cutting in. The start delay and the per-line wait tolerance are the original's, decoded in
/// docs/formats/objectives.md and docs/formats/sounds.md.
/// </summary>
public sealed partial class MissionRadio : Node
{
    /// <summary>Seconds between a cue being raised and its first line speaking. Both objective
    /// sound directives hand the play call this same delay.</summary>
    public const float CueDelaySeconds = 1f;

    /// <summary>Seconds a call may wait for a busy channel when its definition authors no QUEUE
    /// value, the sound library's own default before the key is read.</summary>
    public const float DefaultToleranceSeconds = 5f;

    /// <summary>Added to an authored QUEUE value to get the real tolerance. The library reads the
    /// key and then adds this.</summary>
    public const float ToleranceBias = 0.3f;

    /// <summary>The speaker id of a call no pilot owns (every objective callout), which
    /// <see cref="IsSpeaking"/> answers false for.</summary>
    public const int NoSpeaker = -1;

    private readonly IReadOnlyDictionary<string, SoundDef> _defs;
    private readonly IReadOnlyDictionary<string, SoundGroup> _groups;
    private readonly Func<string, AudioStreamWav?> _stream;
    private readonly List<RadioCall> _queue = new();
    private readonly AudioStreamPlayer _player = new() { Bus = AudioBuses.Voice };

    private RadioCall? _onAir;
    private float _lineLeft;

    public MissionRadio(IReadOnlyDictionary<string, SoundDef> defs,
        IReadOnlyDictionary<string, SoundGroup> groups,
        Func<string, AudioStreamWav?> stream)
    {
        _defs = defs;
        _groups = groups;
        _stream = stream;
    }

    /// <summary>How many lines actually started playing. The counter an in-engine suite asserts on,
    /// the <see cref="WorldSounds.OneShotsStarted"/> precedent: it moves only when a stream
    /// resolved and the player was started.</summary>
    public int LinesStarted { get; private set; }

    /// <summary>How many queued calls were dropped without being heard because they waited past
    /// their definitions' QUEUE tolerance.</summary>
    public int Dropped { get; private set; }

    /// <summary>How long ago the cues queued now were raised, set only while a guest catches up on
    /// a late director event. Such a call speaks from where the host's is, and skips what the
    /// host already said.</summary>
    public float LateBy { get; set; }

    /// <summary>Lines a late call skipped because the host had already finished saying them.</summary>
    public int SkippedLate { get; private set; }

    /// <summary>The definition name on air, or null when the channel is silent.</summary>
    public string? OnAir => _lineLeft > 0f && _onAir != null ? _onAir.Current : null;

    /// <summary>How many calls are waiting for the channel.</summary>
    public int Pending => _queue.Count;

    /// <summary>Queues one cue and returns how many lines it will speak, or 0 when the name is not
    /// a radio cue at all. A cue naming a positional or music definition returns 0 so the caller
    /// can route it to the channel that does own it.</summary>
    public int Cue(string name, Random rng)
    {
        if (Resolve(name, rng) is not { Count: > 0 } lines)
        {
            return 0;
        }

        Enqueue(name, lines, CueDelaySeconds, NoSpeaker);
        return lines.Count;
    }

    /// <summary>Queues one combat voice line on this same channel with no start delay, and returns
    /// the definition it will speak, or null when the name is no radio line or its WAV never
    /// decoded. The original hands a combat bark to the one queue the objective cues use, without
    /// their delay (docs/formats/sounds.md). <paramref name="speakerId"/> is the pilot the line
    /// belongs to, what <see cref="IsSpeaking"/> answers from.</summary>
    public string? Speak(string name, Random rng, int speakerId = NoSpeaker)
    {
        if (Resolve(name, rng) is not { Count: > 0 } lines || _stream(lines[0]) == null)
        {
            return null;
        }

        Enqueue(name, lines, 0f, speakerId);
        return lines[0];
    }

    /// <summary>Whether this pilot's own line holds the channel: queued, or on air with its length
    /// not yet elapsed. The combat-voice gate's "must not already be talking" reads this, so a
    /// speaker cannot queue a second line over its own first and have the tolerance drop it.</summary>
    public bool IsSpeaking(int speakerId)
    {
        if (speakerId == NoSpeaker)
        {
            return false;
        }

        if (_lineLeft > 0f && _onAir != null && _onAir.Speaker == speakerId)
        {
            return true;
        }

        foreach (var call in _queue)
        {
            if (call.Speaker == speakerId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops queued calls the named sounds belong to, if they have not started speaking:
    /// the STOP_QUEUED_SOUNDS directive, which cancels chatter a completion has made moot. Returns
    /// how many calls were removed.</summary>
    public int Cancel(IReadOnlyList<string> names)
    {
        int removed = 0;
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            foreach (var name in names)
            {
                if (_queue[i].Names(name))
                {
                    _queue.RemoveAt(i);
                    removed++;
                    break;
                }
            }
        }

        return removed;
    }

    /// <summary>Advances the channel: the speaking line's clock, each waiting call's start delay,
    /// and the wait tolerance that drops a call the channel kept waiting. Call once per frame from
    /// the mission layer.</summary>
    public void Tick(float dt)
    {
        bool busy = _lineLeft > 0f;
        Age(dt, busy);
        if (busy)
        {
            _lineLeft -= dt;
            return;
        }

        // A chain is one call: its remaining lines follow back to back, since the delay the data
        // authors is per cue and nothing in it spaces the lines of one script.
        if (_onAir != null && StartNextLine(_onAir))
        {
            return;
        }

        _onAir = null;
        if (_queue.Count == 0 || _queue[0].Delay > 0f)
        {
            return;
        }

        var call = _queue[0];
        _queue.RemoveAt(0);
        if (StartNextLine(call))
        {
            _onAir = call;
        }
    }

    /// <summary>Cuts the channel silent and forgets everything waiting, for a mission teardown.
    /// </summary>
    public void Stop()
    {
        _player.Stop();
        _queue.Clear();
        _onAir = null;
        _lineLeft = 0f;
    }

    public override void _Ready() => AddChild(_player);

    private void Enqueue(string name, List<string> lines, float delay, int speakerId)
    {
        // The tolerance is the first line's: the data authors one QUEUE value per mission, so
        // every line of a chain carries the same number anyway.
        float tolerance = _defs.TryGetValue(lines[0], out var first) && first.QueueSeconds is { } q
            ? q + ToleranceBias
            : DefaultToleranceSeconds;
        _queue.Add(new RadioCall
        {
            Cue = name,
            Lines = lines,
            Tolerance = tolerance,
            Delay = delay - Math.Max(0f, LateBy),
            Late = LateBy > 0f,
            Speaker = speakerId,
        });
        Log.Debug("sound", $"radio queue cue={name} lines={lines.Count} wait<={tolerance:0.#}s pending={_queue.Count}");
    }

    // Which lines a cue speaks: a chain group in order, a weighted group's pick, or a bare radio
    // definition. Null for anything this channel does not own.
    private List<string>? Resolve(string name, Random rng)
    {
        if (_groups.TryGetValue(name, out var group))
        {
            if (group.Chains.Count > 0)
            {
                var lines = new List<string>();
                foreach (var line in group.Chains[0])
                {
                    if (_defs.TryGetValue(line, out var def) && def.Queued)
                    {
                        lines.Add(line);
                    }
                }

                return lines;
            }

            return group.Pick(rng) is { } member ? Resolve(member, rng) : null;
        }

        return _defs.TryGetValue(name, out var single) && single.Queued
            ? new List<string> { name }
            : null;
    }

    // Starts the call's next playable line, skipping lines whose WAV never decoded. False once the
    // call has nothing left to say.
    private bool StartNextLine(RadioCall call)
    {
        double offset = call.Late ? SkipOverdue(call) : 0.0;
        call.Late = false;
        while (call.Next < call.Lines.Count)
        {
            string line = call.Lines[call.Next++];
            if (_stream(line) is not { } stream || !_defs.TryGetValue(line, out var def))
            {
                continue;
            }

            call.Current = line;
            _player.Stream = stream;
            _player.VolumeDb = Mathf.LinearToDb(Math.Max(def.Volume, 0.0001f));
            _player.Play((float)offset);
            _lineLeft = (float)(stream.GetLength() - offset);
            offset = 0.0;
            LinesStarted++;
            Log.Debug("sound", $"radio line={line} cue={call.Cue} len={_lineLeft:0.0}s");
            return true;
        }

        return false;
    }

    // A late call's overdue is how far past its delay it starts. The lines inside it are skipped,
    // and the offset into the next one is returned.
    private double SkipOverdue(RadioCall call)
    {
        var lengths = new List<double>(call.Lines.Count - call.Next);
        for (int i = call.Next; i < call.Lines.Count; i++)
        {
            lengths.Add(_stream(call.Lines[i]) is { } s && _defs.ContainsKey(call.Lines[i]) ? s.GetLength() : 0.0);
        }

        var (index, offset) = LateStart.Into(lengths, -call.Delay);
        for (int i = 0; i < index; i++)
        {
            SkippedLate += lengths[i] > 0.0 ? 1 : 0;
        }

        call.Next += index;
        return offset;
    }

    // ⚠ The tolerance clock runs only while the channel is busy: QUEUE is how long a line will wait
    // for a channel someone else holds, not a deadline on its own start delay. Age a call while it
    // is merely serving that delay and every 0.5 s combat bark is dropped before it can speak.
    private void Age(float dt, bool busy)
    {
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            var call = _queue[i];
            call.Delay -= dt;
            if (!busy)
            {
                continue;
            }

            // A late call is not overdue while the channel is held: the host's copy waited too.
            if (call.Late)
            {
                call.Delay = Math.Max(0f, call.Delay);
            }

            call.Waited += dt;
            if (call.Waited <= call.Tolerance)
            {
                continue;
            }

            _queue.RemoveAt(i);
            Dropped++;
            Log.Debug("sound", $"radio dropped cue={call.Cue} waited={call.Waited:0.#}s");
        }
    }

    // One queued call: the cue that raised it, the lines it speaks in order, and the pilot it
    // belongs to (NoSpeaker for an objective callout, which no voice gate asks about).
    private sealed class RadioCall
    {
        public string Cue = "";
        public List<string> Lines = new();
        public string Current = "";
        public int Next;
        public float Delay;
        public float Waited;
        public float Tolerance;
        public bool Late;
        public int Speaker = NoSpeaker;

        public bool Names(string name) =>
            string.Equals(Cue, name, StringComparison.OrdinalIgnoreCase)
            || Lines.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
