using System;

namespace CSVM.Video;

/// <summary>
/// One cinema playing with its sound: a <see cref="MoviePlayback"/> for the picture, the movie's
/// own sound track handed out as PCM, and the two kept together by their presentation timestamps.
/// The clock is the sound the audio device has actually played, because a device consumes samples
/// at exactly the rate it was opened at where a frame callback does not, so the picture cannot
/// drift away from the sound however long the file runs.
/// The two streams carry their own container start times, whose difference is taken here rather
/// than by the caller. Nothing touches the engine, so the whole sync runs in a plain unit test; the
/// half that owns the device and the texture is <c>CSVM.UI.Screens.CinemaScreen</c>.
/// </summary>
public sealed class CinemaPlayback
{
    // A cinema plays once. The layout's endless play count belongs to a background film, and a
    // sound track that restarted under a finished picture would have nothing to be in step with.
    private const int SinglePass = 1;

    private readonly MpegMovie _movie;
    private readonly MoviePlayback _picture;

    // The seconds the picture's clock lags the sound's, when the sound starts first. Its opposite
    // number is the leading silence below, and at most one of the two is ever nonzero; neither
    // discards a sample, because the sound is the shorter stream and every one of it is heard.
    private readonly double _shift;

    // The sound frame being handed out and how much of it has gone, since a device asks for
    // whatever fits in its buffer rather than for whole layer II frames.
    private AudioFrame? _sound;
    private int _taken;

    // Samples of silence still owed before the first decoded one, when the sound starts after the
    // picture.
    private long _lead;

    private long _handed;
    private long _played;
    private bool _drained;
    private bool _silent;

    /// <summary>Opens <paramref name="movie"/> as a cinema, decoding its first picture so a caller
    /// has something to draw before the sound starts.</summary>
    public CinemaPlayback(MpegMovie movie)
    {
        _movie = movie;
        _picture = new MoviePlayback(movie, SinglePass);
        _picture.Advance(0.0);
        if (!movie.HasAudio)
        {
            _silent = true;
            return;
        }

        // The offset the container authors between the two streams. Nine of the ten cinemas
        // author none at all and the tenth starts its sound first, so the shipped files reach the
        // shift arm and never the silence one (docs/formats/cinemas.md).
        double offset = movie.AudioStartTime - movie.VideoStartTime;
        if (offset > 0.0)
        {
            _lead = (long)Math.Round(offset * movie.AudioSampleRate);
        }
        else
        {
            _shift = offset;
        }
    }

    /// <summary>Picture width in samples, which is the movie's own.</summary>
    public int Width => _picture.Width;

    /// <summary>Picture height in samples, which is the movie's own.</summary>
    public int Height => _picture.Height;

    /// <summary>The picture due now, as 8-bit RGBA with rows tightly packed, rewritten in place by
    /// every <see cref="Advance"/> that lands on a new one.</summary>
    public byte[] Pixels => _picture.Pixels;

    /// <summary>Whether a sound track is driving the clock. False for a movie that carries none
    /// and after <see cref="PlaySilent"/>, and the picture then runs on the caller's own step.</summary>
    public bool HasSound => !_silent;

    /// <summary>Samples per second per channel the sound track is handed out at.</summary>
    public int SampleRate => _movie.AudioSampleRate;

    /// <summary>Channels the sound track carries. One of the ten cinemas is mono, so a caller
    /// feeding a stereo device duplicates rather than assuming two.</summary>
    public int Channels => _movie.AudioChannels;

    /// <summary>Seconds into the movie the picture stands at.</summary>
    public double Clock => _picture.Clock;

    /// <summary>How many pictures have reached <see cref="Pixels"/>.</summary>
    public int FramesShown => _picture.FramesShown;

    /// <summary>Samples per channel handed to the device so far, counting the leading silence.
    /// This over <see cref="SampleRate"/> is the moment the sound has reached.</summary>
    public long SoundFramesHanded => _handed;

    /// <summary>Whether the picture has run out, the sound track with it, and the device has
    /// played everything it was given. Sound is still sounding when the last picture is put up in
    /// every one of the ten files, so this stays false for a moment after it and that tail is
    /// correct rather than a sync fault.</summary>
    public bool Finished => _picture.Finished && (_silent || (_drained && _played >= _handed));

    /// <summary>Fills <paramref name="destination"/> with up to <paramref name="frames"/> samples
    /// per channel, interleaved by channel, and answers how many it wrote. Samples are clamped
    /// into plus or minus one: six of the ten cinemas peak above it, which fixed-point output
    /// would otherwise wrap or clip audibly.</summary>
    public int ReadSound(float[] destination, int frames)
    {
        if (_silent)
        {
            return 0;
        }

        int channels = Channels;
        int written = 0;
        while (written < frames)
        {
            if (_lead > 0)
            {
                int silence = (int)Math.Min(_lead, frames - written);
                Array.Clear(destination, written * channels, silence * channels);
                _lead -= silence;
                written += silence;
                continue;
            }

            if (!NextSound())
            {
                break;
            }

            int take = Math.Min(_sound!.SamplesPerChannel - _taken, frames - written);
            Clamp(_sound.Samples, _taken * channels, destination, written * channels, take * channels);
            _taken += take;
            written += take;
        }

        _handed += written;
        return written;
    }

    /// <summary>Advances the picture to where the sound has reached.
    /// <paramref name="soundFramesPlayed"/> is what the device has consumed, and is the clock
    /// whenever a sound track is still playing; <paramref name="elapsedSeconds"/> drives the
    /// picture for a movie carrying none and for the tail past the last sample. Answers whether
    /// <see cref="Pixels"/> changed.</summary>
    public bool Advance(double elapsedSeconds, long soundFramesPlayed)
    {
        if (_silent)
        {
            return _picture.Advance(elapsedSeconds);
        }

        _played = soundFramesPlayed;

        // ⚠ Do not leave the clock on the sound past the last sample. Two of the ten cinemas run
        // their sound track out before the last picture's own interval expires, and a clock that
        // stopped with the sound would leave those two never finishing.
        if (_drained && _played >= _handed)
        {
            return _picture.Advance(elapsedSeconds);
        }

        return _picture.Advance((soundFramesPlayed / (double)SampleRate) + _shift - _picture.Clock);
    }

    /// <summary>Gives up the sound track and runs the picture off the caller's own step instead.
    /// A host that could not open an audio device has nothing else to run on, and a cinema with no
    /// sound is better than a cinema that never advances.</summary>
    public void PlaySilent() => _silent = true;

    private static void Clamp(float[] source, int from, float[] destination, int at, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float sample = source[from + i];
            destination[at + i] = sample < -1f ? -1f : (sample > 1f ? 1f : sample);
        }
    }

    // Leaves a sound frame with samples still in it, or reports the track exhausted.
    private bool NextSound()
    {
        while (_sound == null || _taken >= _sound.SamplesPerChannel)
        {
            _sound = _movie.NextAudioFrame();
            _taken = 0;
            if (_sound == null)
            {
                _drained = true;
                return false;
            }
        }

        return true;
    }
}
