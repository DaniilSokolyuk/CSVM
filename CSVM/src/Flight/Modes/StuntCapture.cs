using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>One Danger Zone photograph: the marker it was latched at, the run clock at that
/// frame, the file it is written to, and the strip thumbnail the scoreboard draws. The first
/// three are fixed on the latch frame; the thumbnail arrives when the file lands.</summary>
public sealed class StuntShot
{
    public required string DzName { get; init; }

    /// <summary>Run clock, seconds, at the latch frame. The file name carries it, so a scripted
    /// run names the same file every time.</summary>
    public required float At { get; init; }

    public required string Path { get; init; }

    /// <summary>The downscaled copy the scoreboard strip draws, null until the shot has
    /// <see cref="Landed"/> and null after it when the frame never arrived. Kept in memory so the
    /// strip costs no disk read at run end.</summary>
    public Image? Thumb { get; internal set; }

    /// <summary>Whether the readback and the write have finished, successfully or not. Set on the
    /// main thread by <see cref="StuntCapture.Settle"/>.</summary>
    public bool Landed { get; internal set; }
}

/// <summary>
/// The Danger Zone camera: one latched photograph of the pilot's aircraft per <c>dzN</c> marker
/// per stunt run, taken through that pilot's <see cref="DangerZonePhotograph"/>. <see cref="Update"/> tests the plane against each marker centre at
/// <see cref="StuntMission.DzRadius"/> every physics frame and latches on the frame the aircraft
/// first crosses inside, requesting the pane and firing <see cref="Sting"/> on that frame. The PNG
/// under <see cref="ShotDir"/> and the strip thumbnail are made on a worker once the frame lands,
/// and <see cref="Settle"/> completes the shot's record on the main thread.
/// A marker that has been photographed is not photographed again in the same run, and one that is
/// still being flown through does not re-trigger while the aircraft stays inside its radius.
/// Per pilot, like the run itself: each pane latches its own pilot's photograph through the
/// <c>pane</c> request it was built with. <c>StuntShotStrip</c> draws the strip.
/// </summary>
public sealed class StuntCapture
{
    /// <summary>Strip thumbnail width in pixels, near the 164-wide scrap region the original's
    /// scrapbook forces a player capture into. The height follows the pane's aspect.</summary>
    public const int ThumbWidth = 164;

    private readonly StuntMission _run;
    private readonly string _chapter;
    private readonly PaneRequest _pane;
    private readonly StuntShot?[] _shots;
    private readonly bool[] _inside;

    // Shots whose file has landed on a worker, waiting for the main thread to complete their record.
    private readonly ConcurrentQueue<(StuntShot Shot, Image? Thumb)> _developed = new();

    // Whether the completed run has had its one test, the completing frame's.
    private bool _closed;

    /// <summary>Builds the camera for one pilot's run. <paramref name="chapter"/> names the file,
    /// and <paramref name="pane"/> requests that pilot's photograph, refusing on a frame with
    /// nothing to read.</summary>
    public StuntCapture(StuntMission run, string chapter, PaneRequest pane)
    {
        _run = run;
        _chapter = chapter;
        _pane = pane;
        _shots = new StuntShot?[run.TotalCount];
        _inside = new bool[run.TotalCount];
    }

    /// <summary>Raised on the main thread when a shot of the current run has landed, with its
    /// <see cref="StuntShot.Thumb"/> set, so a strip drawn before then can fill its cell.</summary>
    public event Action<StuntShot>? ShotLanded;

    /// <summary>Raised on the latch frame with the new shot, still pending, so a strip already
    /// drawn can take a marker photographed after the run completed on the same frame.</summary>
    public event Action<StuntShot>? ShotLatched;

    /// <summary>Where the shots go instead of <c>user://</c> while set, so a suite writes into its
    /// own scratch directory rather than beside the player's saves.</summary>
    public static string? DirectoryOverride { get; set; }

    /// <summary>Fired once per latched photograph, the <c>snd_dangerzone_camera</c> sting. Null
    /// leaves the latch silent, which is what a rig with no audio gets.</summary>
    public Action? Sting { get; set; }

    /// <summary>How many photographs this run has latched.</summary>
    public int Count { get; private set; }

    /// <summary><c>screenshots/stunts/</c> beside the saves: Godot's writable user directory,
    /// where <c>stunt_scores.json</c> and the profiles already live.</summary>
    public static string ShotDir() => Path.Combine(
        DirectoryOverride ?? ProjectSettings.GlobalizePath("user://"), "screenshots", "stunts");

    /// <summary>The file one latch writes: chapter, marker and run clock, e.g.
    /// <c>C4_dz3_0m12.4s.png</c>. Invariant culture and the run clock rather than the wall clock,
    /// so a scripted run is reproducible and a German-locale machine names the same file.</summary>
    public static string FileName(string chapter, string dzName, float at)
    {
        int min = (int)(at / 60f);
        return string.Format(CultureInfo.InvariantCulture, "{0}_{1}_{2}m{3:00.0}s.png",
            chapter, dzName, min, at - min * 60f);
    }

    /// <summary>This run's photographs in marker order, the order the zones are authored in, which
    /// is what the scoreboard strip shows. Skips markers not yet photographed.</summary>
    public IEnumerable<StuntShot> InMarkerOrder()
    {
        foreach (var shot in _shots)
        {
            if (shot != null)
            {
                yield return shot;
            }
        }
    }

    /// <summary>Physics-frame test against this frame's committed plane position: latches the
    /// markers the aircraft has just crossed into. The last test of a run is the first one that
    /// finds it complete, which is the completing frame's, since the caller tests the run first on
    /// every frame; a marker first entered after that is never photographed.</summary>
    public void Update(Vector3 planePos)
    {
        // ⚠ Do not test past the completing frame. The original photographs inside a zone's own
        // first completion, so once every zone has completed nothing can photograph again
        // (docs/formats/campaign-screens.md, "The danger-zone slot").
        if (!_run.AllComplete)
        {
            _closed = false;
        }
        else if (_closed)
        {
            return;
        }
        else
        {
            _closed = true;
        }

        float radius = StuntMission.DzRadius * StuntMission.DzRadius;
        for (int i = 0; i < _shots.Length; i++)
        {
            if (planePos.DistanceSquaredTo(_run.Zones[i].Position) > radius)
            {
                _inside[i] = false;
                continue;
            }
            // Inside: the latch is the CROSSING, so a pass that lingers inside the radius takes one
            // photograph on its first frame and none after it.
            if (_inside[i])
            {
                continue;
            }
            if (_shots[i] != null)
            {
                _inside[i] = true;
                continue;
            }
            // ⚠ Mark the pass only once the pane accepted the request. A session whose first
            // rendered frame has not landed yet would otherwise spend the marker on nothing.
            if (Latch(_run.Zones[i]) is { } shot)
            {
                _shots[i] = shot;
                _inside[i] = true;
                ShotLatched?.Invoke(shot);
            }
        }
    }

    /// <summary>A fresh run: every marker photographable again. ⚠ Does not clear which markers the
    /// aircraft is standing inside; a restart under a marker must not latch it without flying
    /// through it again.</summary>
    public void Reset()
    {
        Array.Clear(_shots, 0, _shots.Length);
        Count = 0;
    }

    /// <summary>Completes the record of every shot whose file has landed since the last call:
    /// sets its thumbnail, marks it <see cref="StuntShot.Landed"/> and raises
    /// <see cref="ShotLanded"/> for a shot of the current run. Main thread only. A landing
    /// schedules this itself, so a caller needs it only to settle within one frame.</summary>
    public void Settle()
    {
        while (_developed.TryDequeue(out var done))
        {
            done.Shot.Thumb = done.Thumb;
            done.Shot.Landed = true;
            if (Array.IndexOf(_shots, done.Shot) >= 0)
            {
                ShotLanded?.Invoke(done.Shot);
            }
        }
    }

    // The strip copy, made in place once the full frame is written: holding one full pane per
    // marker is far more memory than the strip needs.
    private static Image Thumbnail(Image full)
    {
        int height = Math.Max(1, ThumbWidth * full.GetHeight() / full.GetWidth());
        full.Resize(ThumbWidth, height, Image.Interpolation.Bilinear);
        return full;
    }

    private StuntShot? Latch(StuntZone zone)
    {
        float at = _run.Elapsed;
        var shot = new StuntShot
        {
            DzName = zone.DzName,
            At = at,
            Path = Path.Combine(ShotDir(), FileName(_chapter, zone.DzName, at)),
        };
        if (!_pane(frame => Develop(shot, frame)))
        {
            Log.Warn("flight", $"stunt capture: no pane image at {zone.DzName}, nothing latched");
            return null;
        }

        Count++;
        Sting?.Invoke();
        return shot;
    }

    // Worker thread: the PNG encode and the resize are the frame's former cost. The record is
    // completed on the main thread, since the scoreboard reads it there.
    private void Develop(StuntShot shot, Image? frame)
    {
        Image? thumb = null;
        if (frame == null || frame.GetWidth() <= 0 || frame.GetHeight() <= 0)
        {
            Log.Warn("flight", $"stunt capture: the pane never arrived for {shot.DzName}, nothing written");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shot.Path)!);
            var err = frame.SavePng(shot.Path);
            if (err != Error.Ok)
            {
                Log.Error("flight", $"stunt capture: could not write {shot.Path} ({err})");
            }
            else
            {
                Log.Info("flight", $"stunt capture: {shot.DzName} at {StuntMission.FormatTime(shot.At)} -> {shot.Path}");
            }

            thumb = Thumbnail(frame);
        }

        _developed.Enqueue((shot, thumb));
        Callable.From(Settle).CallDeferred();
    }
}
