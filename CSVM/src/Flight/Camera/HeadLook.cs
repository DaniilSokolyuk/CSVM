using System;
using Godot;

namespace CSVM.Flight.Camera;

/// <summary>Which of the head's behaviours is live, the original's own look-state byte
/// (docs/org/cameraViews.md, head-look controller). Snap returns the head to straight ahead the
/// moment its direction is released, and is where autohead runs. Free-look pans the head and
/// leaves it wherever it was pointed. Padlock aims it at the selected target and reads no look
/// control at all. Exactly one is live per frame, and all three are written by the same writers
/// <see cref="HeadLook.Step"/> runs, not by paths of their own.</summary>
public enum LookMode
{
    /// <summary>The original's state 0, and the mode a head starts in.</summary>
    Snap,

    /// <summary>The original's state 1, its "smooth look".</summary>
    FreeLook,

    /// <summary>The original's state 2, Track Target: the head holds the selected target's own
    /// bearing every frame, and a look direction is what leaves it.</summary>
    Padlock,
}

/// <summary>One frame of look input, in the head's own conventions (docs/controls.md); the
/// optional members default off, so a forced path leaves no stale deflection. A snap direction is
/// a composed (x, y), +x right and +y forward; a free-look direction and the pad's absolute aim
/// are +right and +up. Both relative directions are read for DIRECTION only, so a half-deflected
/// input pans as fast as a full one, as the original's hat switch does. The
/// <paramref name="PadRight"/> and <paramref name="PadUp"/> pair is exempt: it carries a
/// MAGNITUDE, because the pad aims absolutely. A held <paramref name="Looking"/> claims the pan,
/// so a still mouse holds the pose rather than reading as idle. The
/// <paramref name="TogglePadlock"/> flag is Track Target; <paramref name="ForceSnap"/> writes
/// snap, so the cockpit's look-back reaches dead astern and returns in either mode.</summary>
public readonly record struct HeadLookInput(
    float SnapX, float SnapY, float FreeRight, float FreeUp, bool Center,
    float PadRight = 0f, float PadUp = 0f, bool Looking = false,
    bool SelectSnapMode = false, bool SelectFreeLookMode = false, bool TogglePadlock = false,
    bool ForceSnap = false);

/// <summary>The pilot's head, in the two first-person views and on the chase camera alike,
/// engine-free apart from <see cref="Mathf"/>. It holds the TARGET angles it is told to look at,
/// set through whichever <see cref="LookMode"/> is live. The SHOWN angles are where it actually
/// looks, chasing exponentially at the decoded rates, and <see cref="CameraController"/> poses the
/// view from them. Elevation is 0 at level and +π/2 straight up, clamped to
/// <see cref="ElevationFloor"/>..π/2, the floor the placing view sets. Azimuth is 0 straight ahead
/// and positive to the left, CLAMPED to ±π, the original's own stop at dead astern. Both bounds
/// bind the relative paths (snap, free-look, center); the absolute pad aim carries its own
/// envelope, see <see cref="PadAimTargets"/>.</summary>
public sealed class HeadLook
{
    /// <summary>Free-look pan rate, rad/s, in whichever direction the input points
    /// (docs/org/cameraViews.md, head-look controller: <c>angle += 2·dt·…</c>).</summary>
    public const float FreeLookRate = 2f;

    /// <summary>Exponential catch-up rate of the shown elevation toward its target, 1/s
    /// (τ ≈ 0.33 s). Elevation is deliberately slower than azimuth, as in the original.</summary>
    public const float ElevationSmoothRate = 3f;

    /// <summary>Exponential catch-up rate of the shown azimuth toward its target, 1/s
    /// (τ ≈ 0.20 s).</summary>
    public const float AzimuthSmoothRate = 5f;

    /// <summary>The highest the head looks: straight up. The original's own ceiling, shared by
    /// every caller of the controller.</summary>
    public const float MaxElevation = Mathf.Pi / 2f;

    /// <summary>The elevation floor a first-person frame sets: level. The head never looks below
    /// the horizon there (docs/org/cameraViews.md, head-look controller).</summary>
    public const float FirstPersonElevationFloor = 0f;

    /// <summary>The elevation floor a chase-camera frame sets: straight down, the literal
    /// <c>0xbfc90fdb</c> the chase placement passes the controller, so the view can look below
    /// level as well as above it (docs/org/cameraViews.md).</summary>
    public const float ChaseElevationFloor = -Mathf.Pi / 2f;

    /// <summary>The snap elevation for a diagonal direction: 45° up (<c>0.7853982</c>).</summary>
    public const float DiagonalElevation = Mathf.Pi / 4f;

    /// <summary>The pad look envelope: where a fully deflected right stick aims, left/right and
    /// up/down. NOT decoded (the original binds no absolute stick), a UX call for this port, and
    /// shared with <see cref="CameraController.PadLook"/> so the chase camera and the first-person
    /// head cannot drift apart. ⚠ The pitch bound is the CHASE camera's gimbal margin, not the
    /// head's: at 90° that camera sits over the plane and <c>Basis.LookingAt</c>'s up hint goes
    /// parallel to the view. Widening it here gimbals chase.</summary>
    public const float PadLookYawMaxDeg = 150f, PadLookPitchMaxDeg = 60f;

    /// <summary>How fast the filtered look stick catches its raw position, 1/s, a time constant of
    /// 40 ms. Short enough to stay under the hand, long enough to bury the noise a stick reports
    /// around any held position, which an absolute aim otherwise shows frame for frame. TUNE: the
    /// original binds no axis to look at all, so there is nothing to decode.</summary>
    public const float PadAimSmoothRate = 25f;

    /// <summary>How far the look stick must leave centre before it aims anything, as a fraction of
    /// full deflection, measured radially so no direction is favoured. Crossing it moves the aim by
    /// 3° round and 1.2° up and down, under the wobble it gates out. TUNE, for the same reason
    /// <see cref="PadAimSmoothRate"/> is.</summary>
    public const float PadAimCentreBand = 0.02f;

    // How near a direction must be to dead ahead, and to a 45° diagonal, for the snap to lift the
    // head: 0.1° and 0.09° respectively. Windows rather than equalities because the original's
    // input is a POV angle in hundredths of a degree; a digital key cluster hits them exactly.
    private const float ForwardWindowRad = 0.001745f;
    private const float DiagonalWindowRad = 0.001571f;

    // How near centre counts as at rest, 0.1°: the window Settled reads.
    private const float SettledWindowRad = 0.001745f;

    // The look stick's own filter, stepped on every frame this head is stepped so its state never
    // lags the mode that is about to read it.
    private readonly StickLookFilter _padFilter = new();

    // The three mode keys as they stood last frame. The mode is written on the PRESS EDGE, as the
    // original's command handlers fire. A key held down writes it once, not every frame.
    private bool _snapKeyDown;

    private bool _smoothKeyDown;

    private bool _padlockKeyDown;

    /// <summary>The floor this head starts on; the placing view sets it per frame afterwards.
    /// </summary>
    public HeadLook(float elevationFloor = FirstPersonElevationFloor) =>
        ElevationFloor = elevationFloor;

    /// <summary>Consulted on a <see cref="LookMode.Snap"/> frame with no look input, and on a
    /// <see cref="LookMode.Padlock"/> frame with nothing selected. The original's autohead gate is
    /// those two arms and no other. Its answer becomes the targets directly, with only the azimuth
    /// clamped: elevation passes <see cref="ElevationFloor"/> on purpose, since autohead's own
    /// floor is below level. Null (the default) returns the idle head to straight ahead.
    /// ⚠ Never consulted in <see cref="LookMode.FreeLook"/>.</summary>
    public Func<(float Elevation, float Azimuth)?>? IdleAim { get; set; }

    /// <summary>Where the selected target sits in the PLANE's own frame, or null for nothing
    /// selected: the only thing <see cref="LookMode.Padlock"/> reads, and it is read every frame.
    /// A delegate for the reason <see cref="IdleAim"/> is one, this head knows about no targeting
    /// module. Null (the default) makes every padlock frame a no-target frame.</summary>
    public Func<Vector3?>? TargetOffset { get; set; }

    /// <summary>Which behaviour this frame runs, and the only thing that decides it: exactly one
    /// mode is live per frame. Written only by the three mode keys, padlock's own exit and
    /// <see cref="HeadLookInput.ForceSnap"/>, never by a device moving. The numpad and the mouse
    /// both obey whichever mode the keys chose.</summary>
    public LookMode Mode { get; private set; }

    /// <summary>The lowest elevation this head may be told to look at:
    /// <see cref="FirstPersonElevationFloor"/> in the cockpit and <see cref="ChaseElevationFloor"/>
    /// on the chase camera. Settable rather than fixed because the original's one head serves both
    /// placements and they differ only in this, so the view placing the frame sets it before
    /// <see cref="Step"/> (<see cref="CameraController.StepHead"/>). A lowered target above the new
    /// floor is left alone; the next input path re-clamps it.</summary>
    public float ElevationFloor { get; set; }

    /// <summary>Whether the head is at rest, straight ahead and level, to within a tenth of a
    /// degree. A released head decays toward centre asymptotically and never lands on it exactly,
    /// so this is a window rather than an equality.</summary>
    public bool Settled =>
        Mathf.Abs(Elevation) < SettledWindowRad && Mathf.Abs(Azimuth) < SettledWindowRad;

    /// <summary>Where the head is being told to look, above level.</summary>
    public float TargetElevation { get; private set; }

    /// <summary>Where the head is being told to look, left of the nose.</summary>
    public float TargetAzimuth { get; private set; }

    /// <summary>Where the head is actually looking, above level, the angle a view basis is built
    /// from.</summary>
    public float Elevation { get; private set; }

    /// <summary>Where the head is actually looking, left of the nose.</summary>
    public float Azimuth { get; private set; }

    /// <summary>The snap mapping: a direction becomes an azimuth (its own angle, mirrored so that
    /// pointing right looks right) and one of three elevations, dead ahead looks straight UP, a
    /// 45° diagonal looks 45° up, anything else looks level. Returns null for no direction at all.
    /// Pure; <paramref name="dirX"/> is +right and <paramref name="dirY"/> +forward.</summary>
    public static (float Elevation, float Azimuth)? SnapTargets(float dirX, float dirY)
    {
        if (dirX == 0f && dirY == 0f)
        {
            return null;
        }
        float angle = Mathf.Atan2(dirX, dirY);      // 0 = dead ahead, positive = to the right
        float a = Mathf.Abs(angle);
        float elevation = 0f;
        if (a <= ForwardWindowRad)
        {
            elevation = MaxElevation;
        }
        else if (Mathf.Abs(a - (Mathf.Pi / 4f)) <= DiagonalWindowRad
              || Mathf.Abs(a - (3f * Mathf.Pi / 4f)) <= DiagonalWindowRad)
        {
            elevation = DiagonalElevation;
        }
        return (elevation, Wrap(-angle));
    }

    /// <summary>The absolute pad mapping: stick position IS head position, scaled by the shared
    /// <see cref="PadLookYawMaxDeg"/>/<see cref="PadLookPitchMaxDeg"/> envelope. Deliberately
    /// ignores <see cref="ElevationFloor"/>: that floor bounds the original's relative controls,
    /// and first person floors it at level, which would leave the lower half of an absolute
    /// stick's travel inert. Pure; <paramref name="right"/> is +right, <paramref name="up"/> +up,
    /// both expected in [−1, 1].</summary>
    public static (float Elevation, float Azimuth) PadAimTargets(float right, float up) =>
        (Mathf.Clamp(up, -1f, 1f) * Mathf.DegToRad(PadLookPitchMaxDeg),
         Mathf.Clamp(-right, -1f, 1f) * Mathf.DegToRad(PadLookYawMaxDeg));

    /// <summary>The padlock bearing: a target offset in the PLANE's own frame becomes the head's
    /// two angles. Elevation is off the horizontal plane, azimuth about the up axis. The law has
    /// no rate limit and no smoothing of its own, since the shown angles do all of it. Its one
    /// bound is the placing view's floor (docs/org/cameraViews.md, padlock). Pure;
    /// <paramref name="localOffset"/> is +right, +up, +aft.</summary>
    public static (float Elevation, float Azimuth) PadlockTargets(Vector3 localOffset) =>
        (Mathf.Atan2(localOffset.Y,
             Mathf.Sqrt((localOffset.X * localOffset.X) + (localOffset.Z * localOffset.Z))),
         Mathf.Atan2(-localOffset.X, -localOffset.Z));

    /// <summary>An angle folded onto the near side of <paramref name="shown"/>. A chase toward it
    /// then crosses the ±π seam by the short arc, not by unwinding through the front. The original
    /// applies this before every chase; only <see cref="LookMode.Padlock"/> reaches the seam here.
    /// </summary>
    public static float Nearest(float target, float shown) => shown + Wrap(target - shown);

    /// <summary>The autohead aim: where the nose will point <paramref name="turnTime"/> seconds on
    /// at the present <see cref="Airframe.FlightModel.BodyRates"/>. The lead is a half-angle, so a capped
    /// lead turns by twice its magnitude, as the plant's own step does. The head therefore leads
    /// into a turn and centres as the rates die (docs/org/cameraViews.md, "Autohead"). Elevation
    /// floors at <paramref name="minPitch"/>, below <see cref="ElevationFloor"/> on purpose.
    /// ⚠ Not the linear velocity: that lags the nose and aims the head outside a turn.</summary>
    public static (float Elevation, float Azimuth) AutoheadTarget(
        Vector3 bodyRates, float turnTime, float turnMax, float minPitch)
    {
        Vector3 lead = bodyRates * turnTime;
        float half = lead.Length();
        if (half <= 1e-9f)
        {
            return (0f, 0f);
        }
        if (half > turnMax)
        {
            lead *= turnMax / half;
            half = turnMax;
        }
        Vector3 nose = new Basis(lead / half, 2f * half) * Vector3.Forward;
        var (elevation, azimuth) = PadlockTargets(nose);
        return (Mathf.Max(elevation, minPitch), azimuth);
    }

    /// <summary>The decoded smoothing law: <c>shown = target + (shown − target)·e^(−rate·dt)</c>.
    /// Frame-rate independent by construction, and pure so it unit-tests at the decoded
    /// rates.</summary>
    public static float Approach(float shown, float target, float rate, float dt) =>
        target + ((shown - target) * Mathf.Exp(-rate * dt));

    /// <summary>An angle folded into ±π, used to normalise an INPUT angle before it becomes a
    /// target. The live azimuth is clamped, not wrapped: see <see cref="ClampAzimuth"/>.</summary>
    public static float Wrap(float angle) => Mathf.PosMod(angle + Mathf.Pi, Mathf.Tau) - Mathf.Pi;

    /// <summary>The azimuth hard stop: the head reaches dead astern (±π) and goes no further,
    /// so free-look cannot wind past the tail and come round the other side.</summary>
    public static float ClampAzimuth(float angle) => Mathf.Clamp(angle, -Mathf.Pi, Mathf.Pi);

    /// <summary>One frame: settle <see cref="Mode"/>, pick this frame's target through that mode
    /// alone, then chase it. Padlock owns the whole frame and reads no look control, the centre key
    /// included. Otherwise the centre key beats everything. Snap runs the direction table, the pad,
    /// the mouse pan, or the idle frame <see cref="IdleAim"/> owns. Free-look integrates the numpad
    /// direction, else the pad, else the mouse pan, else holds. A centred pad claims no frame.
    /// </summary>
    public void Step(float dt, in HeadLookInput input)
    {
        bool direction = input.SnapX != 0f || input.SnapY != 0f;
        bool panning = input.Looking || input.FreeRight != 0f || input.FreeUp != 0f;
        // Ahead of the modes, so the filter advances on every frame and a mode that takes the pad
        // over from another path reads this frame's deflection rather than the last one it saw.
        _padFilter.Step(dt, input.PadRight, input.PadUp);
        SelectMode(input);
        bool padlocked = Mode == LookMode.Padlock;

        if (padlocked)
        {
            PadlockFrame();
            // The exit scan runs AFTER the bearing, as the original's does. The frame a look
            // direction arrives on still aims at the target, and snap owns the next one.
            if (direction || panning)
            {
                Mode = LookMode.Snap;
            }
        }
        else if (input.Center)
        {
            SetTargets(0f, 0f);
        }
        else if (Mode == LookMode.Snap)
        {
            SnapFrame(SnapTargets(input.SnapX, input.SnapY), panning, input, dt);
        }
        else if (direction)
        {
            // Free-look reads the numpad through the integrator, not the table. A held key pans at
            // the decoded rate; a released one leaves the head where it was pointed.
            FreeLook(input.SnapX, input.SnapY, dt);
        }
        else if (_padFilter.Active)
        {
            PadAim();
        }
        else if (panning)
        {
            // A held control claims this arm with no motion on it, so a still mouse holds the pose
            // rather than reading as idle. An idle frame holds it too, so the two agree.
            FreeLook(input.FreeRight, input.FreeUp, dt);
        }

        Elevation = Approach(Elevation, TargetElevation, ElevationSmoothRate, dt);
        // A plain chase, no wrap: the relative paths' targets are clamped to ±π. The head swings
        // back through the front to reach the other side, as a hard-stopped head does. A padlocked
        // head is the one that reaches the seam, and it crosses by the short arc.
        Azimuth = padlocked
            ? Wrap(Approach(Azimuth, Nearest(TargetAzimuth, Azimuth), AzimuthSmoothRate, dt))
            : Approach(Azimuth, TargetAzimuth, AzimuthSmoothRate, dt);
    }

    /// <summary>Put the head straight ahead at once, targets and shown angles together, and back
    /// in <see cref="LookMode.Snap"/>. That is what a fresh spawn wants, and what the original's
    /// own camera init writes. As against the center key's smoothed return, which leaves the mode
    /// alone.</summary>
    public void Reset()
    {
        SetTargets(0f, 0f);
        Elevation = 0f;
        Azimuth = 0f;
        Mode = LookMode.Snap;
    }

    // This frame's mode, in writer order: the keys on their press edge, then the forced snap. ⚠ No
    // device writes the mode. A numpad direction writing Snap would make the free-look pan
    // impossible, and a mouse pan writing FreeLook would stop the head springing back in snap.
    private void SelectMode(in HeadLookInput input)
    {
        if (input.SelectSnapMode && !_snapKeyDown)
        {
            Mode = LookMode.Snap;
        }

        // Between the two selectors, the slot order of the original's own Views 1 page. It leaves
        // padlock for SNAP and never back to free-look, and it centres nothing on the way in.
        if (input.TogglePadlock && !_padlockKeyDown)
        {
            Mode = Mode == LookMode.Padlock ? LookMode.Snap : LookMode.Padlock;
        }

        if (input.SelectFreeLookMode && !_smoothKeyDown)
        {
            // The original's own handler centres the head, shown angles included, before entering
            // the mode, so smooth look always starts from straight ahead.
            Reset();
            Mode = LookMode.FreeLook;
        }

        _snapKeyDown = input.SelectSnapMode;
        _padlockKeyDown = input.TogglePadlock;
        _smoothKeyDown = input.SelectFreeLookMode;
        // Padlock is left by its own exit scan, which runs in Step after the frame's bearing.
        if (input.ForceSnap && Mode != LookMode.Padlock)
        {
            Mode = LookMode.Snap;
        }
    }

    // The padlock frame: the selected target's bearing becomes the targets outright, with only the
    // placing view's floor applied. A target below a cockpit head therefore holds at level. With
    // nothing selected the original zeroes both angles and hands the frame to the idle rule, the
    // same autohead arm a released snap direction reaches.
    private void PadlockFrame()
    {
        if (TargetOffset?.Invoke() is { } offset)
        {
            var (elevation, azimuth) = PadlockTargets(offset);
            TargetElevation = Mathf.Max(elevation, ElevationFloor);
            TargetAzimuth = azimuth;
        }
        else if (IdleAim?.Invoke() is { } idle)
        {
            TargetElevation = idle.Elevation;
            TargetAzimuth = ClampAzimuth(idle.Azimuth);
        }
        else
        {
            SetTargets(0f, 0f);
        }
    }

    // The snap mode's frame: the direction table, else the pad's standing aim, else the mouse pan
    // while its control is held, else the idle rule. A released input returns the head to straight
    // ahead, which is this mode's whole difference from free-look, and the frame autohead may own.
    private void SnapFrame(
        (float Elevation, float Azimuth)? snap, bool panning, in HeadLookInput input, float dt)
    {
        if (snap != null)
        {
            SetTargets(snap.Value.Elevation, snap.Value.Azimuth);
        }
        else if (_padFilter.Active)
        {
            PadAim();
        }
        else if (panning)
        {
            FreeLook(input.FreeRight, input.FreeUp, dt);
        }
        else if (IdleAim?.Invoke() is { } idle)
        {
            TargetElevation = idle.Elevation;
            TargetAzimuth = ClampAzimuth(idle.Azimuth);
        }
        else
        {
            SetTargets(0f, 0f);
        }
    }

    // The pad's absolute aim, this port's own path: a standing instruction, so it sits below the
    // discrete commands and above the pan. Read off the filter rather than the raw pair, and
    // assigned rather than SetTargets'd, since it owns its own bounds.
    private void PadAim()
    {
        var (elevation, azimuth) = PadAimTargets(_padFilter.X, _padFilter.Y);
        TargetElevation = elevation;
        TargetAzimuth = azimuth;
    }

    // Integrate the targets at the fixed pan rate along the input direction. The direction is
    // normalised first: the rate is the original's whole law here, so a barely-deflected stick and
    // a hard one pan alike, and the caller may pass raw pixels or a curved axis.
    private void FreeLook(float right, float up, float dt)
    {
        float length = Mathf.Sqrt((right * right) + (up * up));
        if (length <= 0f)
        {
            return;
        }
        float step = FreeLookRate * dt;
        SetTargets(TargetElevation + (step * (up / length)), TargetAzimuth - (step * (right / length)));
    }

    private void SetTargets(float elevation, float azimuth)
    {
        TargetElevation = Mathf.Clamp(elevation, ElevationFloor, MaxElevation);
        TargetAzimuth = ClampAzimuth(azimuth);
    }
}

/// <summary>What sits between the raw look stick and every view it aims: a radial centre band, then
/// a first-order lag at <see cref="HeadLook.PadAimSmoothRate"/> on each component. The pair is the
/// stick's own and carries no direction of its own, because the head reads it as right and up while
/// the chase swing reads it as right and down.
/// ⚠ Ask <see cref="Active"/> whether the stick is being used, never the filtered pair. The lag
/// never lands on its input exactly, so a held stick's pair is never its raw one, while inside the
/// band the state is zeroed and both read exactly 0, which is what lets a reader's own return to
/// centre start on the frame the stick was let go.</summary>
public sealed class StickLookFilter
{
    /// <summary>The filtered deflection along the stick's first axis.</summary>
    public float X { get; private set; }

    /// <summary>The filtered deflection along the stick's second axis.</summary>
    public float Y { get; private set; }

    /// <summary>Whether the raw stick sat outside the centre band on the last <see cref="Step"/>,
    /// which is the whole test of whether it is claiming a view this frame.</summary>
    public bool Active { get; private set; }

    /// <summary>Advance one frame over the raw pair. Inside the band the state is zeroed, so
    /// nothing of a held deflection survives letting go.</summary>
    public void Step(float dt, float x, float y)
    {
        if (Mathf.Sqrt((x * x) + (y * y)) <= HeadLook.PadAimCentreBand)
        {
            X = 0f;
            Y = 0f;
            Active = false;
            return;
        }

        X = HeadLook.Approach(X, x, HeadLook.PadAimSmoothRate, dt);
        Y = HeadLook.Approach(Y, y, HeadLook.PadAimSmoothRate, dt);
        Active = true;
    }
}
