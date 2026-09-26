namespace CSVM.Video;

/// <summary>
/// One movie playing on a clock: an <see cref="MpegMovie"/>, the elapsed time it has been played
/// for, and the picture due now as RGBA. Which picture that is comes from the frames' own
/// presentation timestamps, so a file plays at the rate its headers declare and no rate is
/// written down here; two of the ten cinemas differ from the other eight.
/// A play count of zero plays endlessly, which is how the layout's <c>Loops</c> field spells a
/// background, and every pass after the first restarts through <see cref="MpegMovie.Rewind"/>.
/// Nothing here touches the engine, so the clock and the loop decision run in a plain unit test;
/// the texture they feed is <c>CSVM.UI.Screens.MovieSurface</c>.
/// </summary>
public sealed class MoviePlayback
{
    // A step longer than this is a window that was not drawing, not a slow frame. Catching such a
    // gap up picture by picture costs more decoding than anything anyone sees, so the clock takes
    // the cap instead and the movie runs late rather than the caller stalling.
    private const double MaxStep = 0.25;

    private readonly MpegMovie _movie;
    private readonly int _plays;
    private readonly byte[] _pixels;
    private VideoFrame? _pending;
    private double _clock;
    private double _passStart;
    private double _passLength;
    private double _shownAt;
    private bool _opened;

    /// <summary>Plays <paramref name="movie"/> <paramref name="loops"/> times over, or endlessly
    /// when that count is zero, which is what the layout's own field means
    /// (<c>docs/formats/cinemas.md</c>).</summary>
    public MoviePlayback(MpegMovie movie, int loops)
    {
        _movie = movie;
        _plays = loops;
        _pixels = new byte[movie.Width * movie.Height * 4];
    }

    /// <summary>Picture width in samples, which is the movie's own.</summary>
    public int Width => _movie.Width;

    /// <summary>Picture height in samples, which is the movie's own.</summary>
    public int Height => _movie.Height;

    /// <summary>The picture due now, as 8-bit RGBA with rows tightly packed. The buffer belongs
    /// to this playback and is rewritten in place, so a caller uploads or copies it rather than
    /// keeping it.</summary>
    public byte[] Pixels => _pixels;

    /// <summary>Seconds into the pass now playing, which restarts at each loop.</summary>
    public double Clock => _clock;

    /// <summary>How many pictures have been put in <see cref="Pixels"/>, across every pass.</summary>
    public int FramesShown { get; private set; }

    /// <summary>How many passes have run out of pictures.</summary>
    public int PassesPlayed { get; private set; }

    /// <summary>Whether the play count is used up and the last picture has had its time on
    /// screen. An endless playback never reports true.</summary>
    public bool Finished { get; private set; }

    /// <summary>Advances the clock by that many seconds and decodes forward to the picture now
    /// due, returning whether <see cref="Pixels"/> changed. Pictures the clock has already passed
    /// are decoded and dropped, so a late caller falls behind by time rather than by
    /// pictures.</summary>
    public bool Advance(double elapsedSeconds)
    {
        if (Finished)
        {
            return false;
        }

        if (elapsedSeconds > 0.0)
        {
            _clock += elapsedSeconds < MaxStep ? elapsedSeconds : MaxStep;
        }

        if (!_opened)
        {
            Open();
        }

        bool changed = false;
        while (!Finished)
        {
            if (_pending == null)
            {
                if (_clock < _passLength || !StartNextPass())
                {
                    break;
                }

                continue;
            }

            if (_pending.Time - _passStart > _clock)
            {
                break;
            }

            Show(_pending);
            changed = true;
            _pending = _movie.NextFrame();
            if (_pending == null)
            {
                PassesPlayed++;
                _passLength = _shownAt + (1.0 / _movie.FrameRate);
            }
        }

        return changed;
    }

    // The first picture's timestamp is the origin every later one is measured against, since the
    // container starts the video stream at a nonzero moment on its own clock.
    private void Open()
    {
        _opened = true;
        _pending = _movie.NextFrame();
        if (_pending == null)
        {
            Finished = true;
            return;
        }

        _passStart = _pending.Time;
    }

    // A pass ends when the last picture's own display interval expires rather than when the
    // decoder runs out, so that picture is on screen as long as every other one.
    private bool StartNextPass()
    {
        if (_plays > 0 && PassesPlayed >= _plays)
        {
            Finished = true;
            return false;
        }

        // ⚠ Restart only through Rewind. It clears state a cheaper reset leaves behind, including
        // the audio filter bank's history, which a replay would otherwise open with.
        _movie.Rewind();
        _clock -= _passLength;
        _pending = _movie.NextFrame();
        if (_pending == null)
        {
            Finished = true;
            return false;
        }

        return true;
    }

    private void Show(VideoFrame frame)
    {
        frame.WriteRgba(_pixels, Width * 4);
        _shownAt = frame.Time - _passStart;
        FramesShown++;
    }
}
