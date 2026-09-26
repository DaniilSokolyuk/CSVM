using System;
using System.IO;
using CSVM.Utils;
using CSVM.Video;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// A movie as something a composition can draw: a <see cref="MoviePlayback"/> and the
/// <see cref="ImageTexture"/> its pixels are uploaded to. There is no node here, so the caller
/// hangs the texture wherever its own layout puts it and this surface never learns which screen
/// it is on. Every timing decision belongs to the playback, which holds no engine type, and this
/// half is the upload and nothing else. Read <c>src/Video/MoviePlayback.cs</c> for the clock.
/// </summary>
public sealed class MovieSurface
{
    private readonly MoviePlayback _playback;
    private readonly Image _image;

    /// <summary>Plays <paramref name="movie"/> <paramref name="loops"/> times over, endlessly
    /// when that count is zero. The first picture is decoded here, so the texture carries the
    /// movie rather than nothing from the moment a caller takes it.</summary>
    public MovieSurface(MpegMovie movie, int loops)
    {
        _playback = new MoviePlayback(movie, loops);
        _playback.Advance(0.0);
        _image = Image.CreateFromData(
            _playback.Width, _playback.Height, false, Image.Format.Rgba8, _playback.Pixels);
        Texture = ImageTexture.CreateFromImage(_image);
    }

    /// <summary>What the picture is drawn from. It is made once and updated in place, so a caller
    /// may hold it for as long as the surface lives.</summary>
    public ImageTexture Texture { get; }

    /// <summary>Whether the play count is used up and the last picture has had its time on
    /// screen. An endless background never reports true.</summary>
    public bool Finished => _playback.Finished;

    /// <summary>How many pictures have reached the texture, across every pass.</summary>
    public int FramesShown => _playback.FramesShown;

    /// <summary>Opens a movie file, or returns null when it cannot be read or is not a movie,
    /// because a screen missing its background still has everything else on it.</summary>
    public static MovieSurface? Open(string path, int loops)
    {
        try
        {
            return new MovieSurface(MpegMovie.FromFile(path), loops);
        }
        catch (Exception e)
            when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Warn("ui", $"movie {path} did not open, drawing the screen without it: {e.Message}");
            return null;
        }
    }

    /// <summary>Advances the clock by that many seconds, uploading only when the picture changed,
    /// and answering whether it did so a caller can repaint on the frames that need it.</summary>
    public bool Advance(double elapsedSeconds)
    {
        if (!_playback.Advance(elapsedSeconds))
        {
            return false;
        }

        _image.SetData(_playback.Width, _playback.Height, false, Image.Format.Rgba8, _playback.Pixels);
        Texture.Update(_image);
        return true;
    }
}
