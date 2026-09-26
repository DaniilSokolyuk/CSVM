using CSVM.Bindings;
using CSVM.Flight.Airframe;
using Godot;

namespace CSVM.Flight.Camera;

/// <summary>The mouse a flight seat takes from the desktop, and the virtual cursor that stands in
/// for the OS one while it holds it. A captured pointer stops reporting a position, so the absolute
/// reads the stick and head-look grew up on are fed from relative motion instead: this accumulates
/// that motion into a cursor confined to the pane, which the stick reads through the same offset and
/// the same gate as the real one. Two things differ: travel is scaled from mouse counts, and the
/// centre band is wider. Pure arithmetic with no device and no display in it, so a unit drives the
/// whole law; <see cref="FlightController"/> owns the mode write and the release.
/// </summary>
public sealed class MouseCapture
{
    /// <summary>Mouse counts from the pane's middle to its edge on either axis, so full deflection is
    /// the same hand movement whatever the pane's pixel size: 32 mm at 1600 dpi. This is the default
    /// sensitivity's travel; a seat's own divides it (<see cref="DeflectionCounts"/>). Remake-only, since
    /// the original read the desktop pointer; the arithmetic is in <c>docs/controls.md</c>, "Flying
    /// with the mouse".</summary>
    public const float FullDeflectionCounts = 2000f;

    /// <summary>The captured stick's centre band, as a fraction of the travel to the edge. ⚠ Wider
    /// than the decoded 0.1 on purpose, and only here: <see cref="MouseFlight.Gate"/> keeps the
    /// original's value for the desktop pointer (<see cref="Centred"/>).</summary>
    public const float CentreBand = 0.2f;

    private Vector2 _pendingCursor;
    private Vector2 _pendingLook;

    /// <summary>Whether the mouse is held right now.</summary>
    public bool Holding { get; private set; }

    /// <summary>Where the virtual cursor stands in the pane, in pane pixels, meaningless while
    /// nothing is held.</summary>
    public Vector2 Cursor { get; private set; }

    /// <summary>Whether a session may take the mouse at all. ⚠ Guarded on a real display and on
    /// nobody being at the controls: the hidden test desktop and every <c>--det</c> run must leave
    /// the mouse mode exactly as the harness set it, or a suite reads a pointer its own launch moved.
    /// <paramref name="scripted"/> is <c>SessionSpec.IsScripted</c>, which covers <c>--run-tests</c>,
    /// the screenshot goldens and every <c>--dump-*</c> report.</summary>
    public static bool Allowed(bool realDisplay, bool det, bool scripted) =>
        realDisplay && !det && !scripted;

    /// <summary>The mode a board that swapped in its own drawn pointer puts back on its close.
    /// ⚠ Never <c>Captured</c>: the pause sheet opens inside the seat's own poll, a frame before the
    /// seat lets go, so it saves the seat's capture, and its close runs at the session's deferred free,
    /// after the menu is already up. Restoring that capture leaves a menu nobody can point at. A resume
    /// needs no capture restored, since the seat takes it again on its next unhalted frame.</summary>
    public static Input.MouseModeEnum Restorable(Input.MouseModeEnum saved) =>
        saved == Input.MouseModeEnum.Captured ? Input.MouseModeEnum.Visible : saved;

    /// <summary>The captured cursor's offset with <see cref="CentreBand"/> taken out of the middle,
    /// placed so <see cref="MouseFlight.Read"/>'s own 0.1 gate lands the band's edge on zero and the
    /// pane's edge on full deflection. The desktop pointer's offset never passes through here.
    /// </summary>
    public static Vector2 Centred(Vector2 offset) => new(Widen(offset.X), Widen(offset.Y));

    /// <summary>Mouse counts from the pane's middle to its edge at a seat's sensitivity, which
    /// divides <see cref="FullDeflectionCounts"/>: a higher setting is less hand travel, so more
    /// sensitive. Clamped to the <see cref="SensitivityScale"/> range.</summary>
    public static float DeflectionCounts(float sensitivity) =>
        FullDeflectionCounts / SensitivityScale.Clamp(sensitivity);

    /// <summary>Takes the mouse, seeding the virtual cursor where the real one stood, so the capture
    /// starts from the stick position the pointer held rather than jumping to the middle.</summary>
    public void Take(Vector2 cursor)
    {
        Cursor = cursor;
        _pendingCursor = Vector2.Zero;
        _pendingLook = Vector2.Zero;
        Holding = true;
    }

    /// <summary>Gives the mouse back. The pending travel goes with it, so a later capture starts
    /// from where the real cursor then is rather than replaying a release's last motion.</summary>
    public void Release()
    {
        Holding = false;
        _pendingCursor = Vector2.Zero;
        _pendingLook = Vector2.Zero;
    }

    /// <summary>One motion event's relative travel, banked for both consumers. Ignored while nothing
    /// is held, which is what keeps an uncaptured session reading byte for byte as before.</summary>
    public void Moved(Vector2 relative)
    {
        if (!Holding)
            return;
        _pendingCursor += relative;
        _pendingLook += relative;
    }

    /// <summary>Folds this frame's travel into the virtual cursor and returns where it now stands.
    /// The travel is in mouse counts, scaled per axis so <see cref="DeflectionCounts"/> spans the
    /// half pane. Confined to <paramref name="pane"/> the way the OS pointer was confined to the
    /// window, so pushing past an edge saturates there instead of running up a debt to travel back.
    /// </summary>
    public Vector2 StepCursor(Vector2 pane, float sensitivity = SensitivityScale.Default)
    {
        var extent = new Vector2(Mathf.Max(pane.X, 0f), Mathf.Max(pane.Y, 0f));
        var travel = _pendingCursor * (extent * 0.5f / DeflectionCounts(sensitivity));
        Cursor = new Vector2(
            Mathf.Clamp(Cursor.X + travel.X, 0f, extent.X),
            Mathf.Clamp(Cursor.Y + travel.Y, 0f, extent.Y));
        _pendingCursor = Vector2.Zero;
        return Cursor;
    }

    /// <summary>The travel since the last read, for head-look's relative law. Consumed on every
    /// call, so a frame nobody looks on banks nothing towards the next one.</summary>
    public Vector2 TakeLook()
    {
        var look = _pendingLook;
        _pendingLook = Vector2.Zero;
        return look;
    }

    private static float Widen(float offset)
    {
        float past = Mathf.Abs(offset) - CentreBand;
        if (past <= 0f)
            return 0f;
        float gate = MouseFlight.AttitudeDeadzone;
        return Mathf.Sign(offset) * (gate + ((1f - gate) * past / (1f - CentreBand)));
    }
}
