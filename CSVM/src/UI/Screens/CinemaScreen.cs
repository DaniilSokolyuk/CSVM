using System;
using System.IO;
using CSVM.UI.Boards;
using CSVM.Utils;
using CSVM.Video;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>Which presses end a cinema before it has played out. The sets differ per cinema and
/// the difference is the original's own: <c>CAMPAIGNINTRO.SCRIPT</c> takes Escape, Space, Return
/// and a left mouse press, where <c>FINALCINEMA.SCRIPT</c> takes Escape and the mouse alone.
/// ⚠ Do not unify them. Space and Return doing nothing on the closing cinema is authored.
/// A pad button is in every set, since a player holding one has no other press to offer, and
/// <see cref="CinemaSkips"/> is where a set meets a press.</summary>
[Flags]
public enum CinemaSkip
{
    /// <summary>Nothing skips: the cinema plays to its last frame.</summary>
    None = 0,

    /// <summary>Escape alone.</summary>
    Escape = 1,

    /// <summary>Space, which the chapter cinema takes and the closing one does not.</summary>
    Space = 2,

    /// <summary>Return, on the same footing as <see cref="Space"/>.</summary>
    Return = 4,

    /// <summary>A left mouse press, which both cinema scripts take.</summary>
    LeftMouse = 8,

    /// <summary>Any press at all, key, mouse or pad button alike, which is what the boot sequence
    /// offers a player who has not been taught a key yet.</summary>
    AnyPress = 16,

    /// <summary>A gamepad button, any of them. Read only where pad input counts at all, since a
    /// pad reports its first button pressed as it arrives.</summary>
    PadButton = 32,
}

/// <summary>
/// One cinema on screen: a <see cref="CinemaPlayback"/>, the <see cref="ImageTexture"/> its
/// pictures are uploaded to, and the <see cref="AudioStreamGenerator"/> its samples are pushed
/// into. The picture is drawn into the same 800x600 rectangle <see cref="BoardFit"/> maps every
/// board into, so a cinema and the screen it hands off to occupy exactly one area of the window.
/// Every timing decision belongs to the playback below the engine boundary; what is here is the
/// device, the upload and the skip. Self-mounting on its own canvas layer, and it frees itself when
/// it ends, so a caller that wants a cinema adds one and waits for <see cref="Ended"/>.
/// </summary>
public sealed partial class CinemaScreen : Node
{
    /// <summary>Escape, Space, Return or a left mouse press, the chapter cinema's set, and a pad
    /// button with them.</summary>
    public const CinemaSkip ChapterKeys = CinemaSkip.Escape | CinemaSkip.Space | CinemaSkip.Return
        | CinemaSkip.LeftMouse | CinemaSkip.PadButton;

    /// <summary>Escape or a left mouse press, the closing cinema's set and no more, and a pad
    /// button with them.</summary>
    public const CinemaSkip ClosingKeys =
        CinemaSkip.Escape | CinemaSkip.LeftMouse | CinemaSkip.PadButton;

    /// <summary>Any press whatsoever, the boot sequence's set.</summary>
    public const CinemaSkip BootKeys = CinemaSkip.AnyPress;

    /// <summary>Raised once, on the frame the cinema stops, whether it played out or was skipped.
    /// The flow that opened it hands off here.</summary>
    public Action? Ended;

    // Samples per channel per push. One layer II frame, so the buffers below are allocated once
    // and a partial read only ever happens on the last push of a file.
    private const int Chunk = 1152;

    // Seconds of sound the generator holds. Long enough that a frame that overran does not starve
    // the device, short enough that a skip stops being heard promptly.
    private const float BufferSeconds = 0.5f;

    private readonly CinemaPlayback _cinema;
    private readonly CinemaSkip _skip;
    private readonly float[] _samples;
    private readonly Vector2[] _frames = new Vector2[Chunk];

    private CanvasLayer? _layer;
    private ColorRect? _ground;
    private TextureRect? _screen;
    private Image? _image;
    private ImageTexture? _texture;
    private AudioStreamPlayer? _player;
    private AudioStreamGeneratorPlayback? _sink;
    private long _pushed;
    private int _capacity;
    private bool _ended;

    private CinemaScreen(MpegMovie movie, CinemaSkip skip)
    {
        Name = "cinema";
        _cinema = new CinemaPlayback(movie);
        _skip = skip;
        _samples = new float[Chunk * Math.Max(1, _cinema.Channels)];
    }

    /// <summary>Whether the cinema has stopped. Read by a suite; a flow uses
    /// <see cref="Ended"/>.</summary>
    public bool Finished => _ended;

    /// <summary>How many pictures have reached the screen.</summary>
    public int FramesShown => _cinema.FramesShown;

    /// <summary>Opens the cinema a name means under <paramref name="dataRoot"/>, or null when the
    /// file is not there or will not decode, because a flow that cannot show a cinema still has to
    /// reach the screen after it. The name resolves without regard to case
    /// (<see cref="CSVM.SessionPaths.Cinema"/>).</summary>
    public static CinemaScreen? Open(string dataRoot, string name, CinemaSkip skip)
    {
        string path = SessionPaths.Cinema(dataRoot, name);
        try
        {
            return new CinemaScreen(MpegMovie.FromFile(path), skip);
        }
        catch (Exception e)
            when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warn("ui", $"cinema {name} did not open, skipping it: {e.Message}");
            return null;
        }
    }

    /// <summary>Ends the cinema now, as a skip does, and raises <see cref="Ended"/>.</summary>
    public void Stop()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _player?.Stop();
        _layer?.QueueFree();
        _layer = null;
        Log.Info("ui", $"cinema ended frames={_cinema.FramesShown} clock={_cinema.Clock:0.###}s");
        Ended?.Invoke();
        QueueFree();
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        _image = Image.CreateFromData(
            _cinema.Width, _cinema.Height, false, Image.Format.Rgba8, _cinema.Pixels);
        _texture = ImageTexture.CreateFromImage(_image);
        _ground = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        _ground.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _screen = new TextureRect
        {
            Texture = _texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _layer = new CanvasLayer { Layer = HudLayers.Cinema, Name = "cinema_layer" };
        _layer.AddChild(_ground);
        _layer.AddChild(_screen);
        AddChild(_layer);
        OpenDevice();
        Fit();
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_ended || !_skip.Skips(CinemaSkips.PressOf(@event, !Pads.InputBlocked)))
        {
            return;
        }

        GetViewport().SetInputAsHandled();
        Stop();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_ended)
        {
            return;
        }

        PushSound();
        if (_cinema.Advance(delta, PlayedFrames()) && _image != null && _texture != null)
        {
            _image.SetData(_cinema.Width, _cinema.Height, false, Image.Format.Rgba8, _cinema.Pixels);
            _texture.Update(_image);
        }

        Fit();
        if (_cinema.Finished)
        {
            Stop();
        }
    }

    // What the device has actually consumed, which is the clock the picture runs on. The generator
    // reports space rather than progress, so the count is what was pushed less what is still
    // queued, and the capacity is read before the first push because that is when it is the whole
    // buffer.
    private long PlayedFrames() =>
        _sink == null ? 0 : _pushed - (_capacity - _sink.GetFramesAvailable());

    private void OpenDevice()
    {
        if (!_cinema.HasSound)
        {
            return;
        }

        var generator = new AudioStreamGenerator
        {
            MixRate = _cinema.SampleRate,
            BufferLength = BufferSeconds,
        };
        // The cinemas are narrated film soundtracks, so they ride the channel the briefing
        // narration already does; a player who turns Voice down is asking these quieter too.
        _player = new AudioStreamPlayer { Stream = generator, Bus = AudioBuses.Voice };
        AddChild(_player);
        _player.Play();
        _sink = _player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        if (_sink == null)
        {
            // No device: the picture then runs on the frame delta, because a silent cinema is
            // better than one whose clock never moves.
            Log.Warn("sound", $"cinema has no audio device; playing the picture silently");
            _cinema.PlaySilent();
            return;
        }

        _capacity = _sink.GetFramesAvailable();
    }

    private void PushSound()
    {
        if (_sink is not { } sink)
        {
            return;
        }

        while (sink.GetFramesAvailable() >= Chunk)
        {
            int got = _cinema.ReadSound(_samples, Chunk);
            if (got <= 0)
            {
                return;
            }

            int channels = _cinema.Channels;
            for (int at = 0; at < got; at++)
            {
                float left = _samples[at * channels];
                _frames[at] = new Vector2(left, channels > 1 ? _samples[(at * channels) + 1] : left);
            }

            sink.PushBuffer(got == Chunk ? _frames : _frames[..got]);
            _pushed += got;
        }
    }

    // The movie fills the same rectangle a board does. Both are 4:3, so the picture reaches every
    // edge of it and the window's remainder is the ground rect behind, exactly as BoardFit
    // letterboxes a screen.
    private void Fit()
    {
        if (_screen == null)
        {
            return;
        }

        var size = GetViewport().GetVisibleRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        _screen.Position = new Vector2(fit.X(0f), fit.Y(0f));
        _screen.Size = new Vector2(fit.Length(BoardFit.AuthoredWidth), fit.Length(BoardFit.AuthoredHeight));
    }
}
