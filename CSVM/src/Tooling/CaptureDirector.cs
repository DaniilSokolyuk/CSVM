using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.UI.Overlays;
using CSVM.Utils;
using Godot;

namespace CSVM.Tooling;

/// <summary>The `--screenshot=`/`--shots=`/`--frames=` capture state machine, plus F11/F12's
/// placement print and ad-hoc save: constructed once in `_Ready` from the launch spec (a
/// --screenshot burst is a process-scoped capture, never re-armed by a menu relaunch), then
/// `Tick()`ed from the tail of `_Process`. Reads camera/orbit/rigs, passed in per call, no
/// back-reference to the host node. The saved line reports the frame the shot landed on and which
/// counter named it; `docs/tooling.md` holds the contract the golden harness reads it under.</summary>
public sealed class CaptureDirector
{
    // The capture still owed, taken from the spec at launch and cleared once written, a --shots=
    // burst counts down through _shotIndex and this goes null when the last frame lands.
    private string? _pendingShot;
    private int _shotDelay;            // frames still to wait before the first capture
    private int _shotIndex;            // 0-based index of the shot being written
    private Transform3D? _shotBaseXform;  // camera pose captured at the first burst frame
    private Vector3 _shotPivot;           // micro-orbit centre (keeps the subject framed)

    // Godot's own rendered-frame counter at the frame the countdown began on, and whether it has
    // begun. The engine's counter rather than a second count of this class's own: a capture that
    // reports a number it derived from the same field it counts down only ever agrees with itself,
    // and the frame a shot landed on is the one thing about it that has to be independently true.
    private ulong _countdownFrom;
    private bool _counting;

    public CaptureDirector(SessionSpec spec)
    {
        _pendingShot = spec.ScreenshotPath;
        _shotDelay = spec.ScreenshotFrames;
    }

    /// <summary>A capture is still owed, every other `--screenshot`-conditioned display choice
    /// (HUD/panel visibility, exit-on-build-failure) reads this instead of the raw field. Never
    /// re-derive it from the spec: a burst clears it mid-session.</summary>
    public bool Pending => _pendingShot != null;

    /// <summary>Format a vector as the "x,y,z" argument value ParseVec3 reads back (invariant
    /// culture, trimmed to 3 decimals).</summary>
    public static string Vec3Arg(Vector3 v) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.###},{1:0.###},{2:0.###}", v.X, v.Y, v.Z);

    /// <summary>Same, for a direction, normalized, and finer, since a unit vector's components
    /// are small enough that 3 decimals would quantise the aim to ~0.03°.</summary>
    public static string DirArg(Vector3 v) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.#####},{1:0.#####},{2:0.#####}", v.X, v.Y, v.Z);

    /// <summary>The one folder every ad-hoc capture lands in, whichever screen took it: the repo's
    /// git-ignored Screenshots/ (rendered frames are game-derived). Menu shots go here too, so a
    /// pilot has one place to look and one place to attach a picture to a report from.</summary>
    public static string ShotDir() =>
        Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "Screenshots"));

    /// <summary>Save <paramref name="viewport"/>'s current frame to a timestamped PNG under
    /// <see cref="ShotDir"/>. Bound to F12 in the orbit viewer, in free flight and on the menu
    /// screens; the full viewport is captured, HUD or board included.</summary>
    public static string? SaveScreenshot(Viewport viewport) =>
        SaveScreenshot(landed => PaneReadback.Request(viewport, landed));

    /// <summary>Asks <paramref name="pane"/> for the frame and writes it when it lands, logging
    /// "screenshot saved" then, so the key-press frame pays for neither the readback nor the encode.
    /// Returns the path the file will be written to, or null when there was no frame to ask for.
    /// ⚠ The file does not exist on return: a caller that reads it waits for it.</summary>
    public static string? SaveScreenshot(PaneRequest pane)
    {
        var dir = ShotDir();
        var path = Path.Combine(dir, $"crimsonskies_{System.DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
        if (!pane(frame => WriteShot(frame, dir, path)))
        {
            Log.Error("core", $"screenshot failed: no frame to read for {path}");
            return null;
        }

        return path;
    }

    /// <summary>The capture block at the tail of `_Process`. Nothing built yet: only shoot once a
    /// session's plane exists, unless the launchscreen is up (--menu --screenshot captures the
    /// menu itself for layout verification).</summary>
    public void Tick(Viewport viewport, SceneTree tree, SessionSpec spec, GameClock? clock,
        OrbitCamera orbit, Camera3D camera, Node3D? plane, bool menuVisible)
    {
        if (_pendingShot == null)
        {
            return;
        }
        if (plane == null && !menuVisible)
        {
            return;
        }
        if (!_counting)
        {
            _counting = true;
            _countdownFrom = Engine.GetProcessFrames();
        }
        // --frames=N is a SIM coordinate, not a wall-clock delay: this decrement must run exactly
        // once per _Process call, here and nowhere else, or a golden lands on a different sim frame.
        if (--_shotDelay > 0)
        {
            return;
        }
        // A burst (z-fight debugging) writes indexed files; a single shot keeps the plain path.
        // The image is the PREVIOUS frame's render, so _00 is un-jittered and _01+ carries the dither.
        var img = viewport.GetTexture().GetImage();
        if (img == null)
        {
            // Backstop for any renderer-less path the arg-parse guard in Launcher._Ready doesn't
            // catch: quit loudly instead of NRE-looping forever on a null image every frame.
            Log.Error("core", $"screenshot capture failed: no image from the viewport (no GPU context?)");
            tree.Quit(1);
            return;
        }
        var path = spec.ScreenshotShots > 1 ? IndexedShotPath(_pendingShot, _shotIndex) : _pendingShot;
        var saveErr = img.SavePng(path);
        // The frame the capture landed on, and what counted it: a session's sim clock, or the
        // rendered frames a screen with no session waited. docs/tooling.md holds why those are one
        // question, and why the render count is read off the engine rather than off the countdown.
        long simFrame = clock?.Frame ?? 0;
        double simTime = clock?.Time ?? 0.0;
        long frame = clock != null ? simFrame : (long)(Engine.GetProcessFrames() - _countdownFrom) + 1;
        string counter = clock != null ? "sim" : "render";
        if (saveErr == Error.Ok)
        {
            Log.Info("core", $"screenshot saved: {path} frame={frame} clock={counter} sim_frame={simFrame} sim_time={simTime:0.###}");
        }
        else
        {
            // A missing parent directory fails SavePng silently, say so instead of "saved".
            Log.Error("core", $"screenshot save FAILED ({saveErr}): {path} frame={frame} clock={counter} sim_frame={simFrame}");
        }
        // The golden-image tripwire's whole input: a hash of the RAW pixels (never the PNG, whose
        // encoded bytes differ between identical images), the size that hash is only valid at, and
        // the adapter that drew it. Emitted on every capture so any shot can become a golden.
        Log.Info("core", $"shot pixmd5={GoldenShot.PixelHash(img)} size={img.GetWidth()}x{img.GetHeight()} gpu={GoldenShot.Adapter()}");
        // No-op unless --tex-census: reads the frame just saved back as per-texture pixel counts.
        TextureDropIn.CountShot(img, path);
        if (++_shotIndex >= spec.ScreenshotShots)
        {
            _pendingShot = null;
            tree.Quit();
            return;
        }
        if (spec.JitterDeg > 0f && !spec.Fly)
        {
            ApplyShotJitter(orbit, camera, spec);
        }
    }

    /// <summary>Print the mode's SUBJECT placement as ready-to-paste arguments (F11, any mode),
    /// the same pair that placed it, so a pose found by hand reproduces in a deterministic
    /// --screenshot run. In flight that subject is the PLANE (player 1's position and nose), not
    /// the chase camera, because that is what --pos/--direction place there. The orbit view prints
    /// --lookat rather than --direction: its framed point is a pivot, and only the point
    /// reproduces the orbit radius as well as the angle.</summary>
    public void PrintPlacement(SessionSpec spec, List<PlayerRig> rigs, Camera3D camera, OrbitCamera orbit)
    {
        if (spec.Fly && rigs.Count > 0 && rigs[0].Controller is { } controller)
        {
            var xform = controller.GlobalTransform;
            Log.Info("core", $"placement: --pos=\"{Vec3Arg(xform.Origin)}\" --direction=\"{DirArg(-xform.Basis.Z)}\"");
            return;
        }
        var pos = camera.GlobalPosition;
        if (spec.Freecam || spec.AnimLab || spec.Fly)
        {
            Log.Info("core", $"placement: --pos=\"{Vec3Arg(pos)}\" --direction=\"{DirArg(-camera.GlobalTransform.Basis.Z)}\"");
            return;
        }
        Log.Info("core", $"placement: --pos=\"{Vec3Arg(pos)}\" --lookat=\"{Vec3Arg(orbit.OrbitCenter)}\"");
    }

    // Runs wherever the pane hands its frame over, a worker for a live readback.
    private static void WriteShot(Image? frame, string dir, string path)
    {
        if (frame == null || frame.GetWidth() <= 0 || frame.GetHeight() <= 0)
        {
            Log.Error("core", $"screenshot failed: the frame never arrived for {path}");
            return;
        }

        Directory.CreateDirectory(dir);
        var err = frame.SavePng(path);
        if (err == Error.Ok)
        {
            Log.Info("core", $"screenshot saved: {path}");
        }
        else
        {
            Log.Error("core", $"screenshot failed ({err}): {path}");
        }
    }

    // Insert a zero-padded frame index before the extension:
    // foo.png -> foo_00.png. Used for --shots=N burst capture.
    private static string IndexedShotPath(string path, int index)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, $"{stem}_{index:D2}{ext}");
    }

    // Rotate the burst camera a hair around the framed point each --shots frame so
    // coplanar surfaces re-decide the depth test and z-fighting flicker surfaces across the
    // sequence (a dead-still camera can render bit-identical frames). The eye micro-orbits
    // the pivot, depths change, but the camera keeps looking at the pivot so the subject
    // stays centred. Static mode only: in --fly the FlightController owns the camera each
    // frame (and the plane's own motion already surfaces the fight).
    private void ApplyShotJitter(OrbitCamera orbit, Camera3D camera, SessionSpec spec)
    {
        if (_shotBaseXform is not { } baseX)
        {
            baseX = camera.GlobalTransform;
            _shotBaseXform = baseX;
            _shotPivot = orbit.OrbitCenter;   // the point the orbit camera aims at
        }
        // Golden-angle spread so consecutive frames differ maximally.
        float mag = Mathf.DegToRad(spec.JitterDeg);
        float phase = _shotIndex * 2.399963f;
        var rot = new Basis(Vector3.Up, mag * Mathf.Cos(phase))
                * new Basis(baseX.Basis.X.Normalized(), mag * Mathf.Sin(phase));
        // Rigidly rotate the whole camera about the pivot: rotating both the eye offset and
        // the basis by the same rotation preserves the aim exactly, so framing is kept.
        var origin = _shotPivot + rot * (baseX.Origin - _shotPivot);
        camera.GlobalTransform = new Transform3D(rot * baseX.Basis, origin);
    }
}
