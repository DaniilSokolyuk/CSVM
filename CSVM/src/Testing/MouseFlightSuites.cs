using System;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Session.Roster;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// The mouse flight-control scheme through the seat's own stick reader. It carries the decoded
/// deadzone and the autogyro exchange over the shipped defs' own flag. The held free-look control
/// hands the mouse to head-look for as long as it is down. The mouse must leave the keyboard
/// scheme underneath exactly as it is.
/// </summary>
internal static class MouseFlightSuites
{
    private const float Dt = 1f / 60f;

    // High over an empty world, so nothing the rig spawns into resolves a ground contact.
    private const float SpawnAltitudeM = 800f;

    // Past the last control a row can hold, so a staged offer adds a control rather than replacing one.
    private const int RowSlots = 16;

    [Suite("flight-mouse-scheme",
        "the mouse flight-control scheme over the shipped defs: the Hoplite authors is_autogyro and "
        + "the Bloodhawk does not, sideways mouse motion banks the aeroplane where it yaws the "
        + "autogyro, both off the same reader and both past the decoded deadzone that leaves a "
        + "cursor near the middle flying nothing, an ordinary quarter-pane offset yaws and pitches "
        + "the autogyro into the plant where it banks the aeroplane, holding the free-look control "
        + "takes the stick off "
        + "the mouse for exactly as long as it is held and releasing it hands the mouse straight "
        + "back, the keys sum into the mouse's own deflections rather than being replaced by them, "
        + "and a seat on the keyboard scheme reads no stick from the mouse at all")]
    internal static void FlightMouseScheme(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var gyroStats = PlaneStats.Load(ctx.ZrdrPath, "player_autogyro");
        var planeStats = PlaneStats.Load(ctx.ZrdrPath, "player_bhawk");
        ctx.Check(gyroStats.IsAutogyro && !planeStats.IsAutogyro,
            $"ABLE-TO-FAIL CONTROL: the shipped defs author is_autogyro on the Hoplite alone (gyro {gyroStats.IsAutogyro}, aeroplane {planeStats.IsAutogyro})");

        var gyro = Rig(ctx, gyroStats, "MouseFlightGyro");
        var plane = Rig(ctx, planeStats, "MouseFlightPlane");
        try
        {
            gyro.MouseFlying = true;
            plane.MouseFlying = true;

            // Inside the deadzone the cursor flies nothing at all. A player can let go of the
            // mouse without the aeroplane holding a deflection.
            plane.MouseStickForTest = new Vector2(0.09f, 0.09f);
            var idle = plane.ReadKeyboard(Dt);
            ctx.Check(idle.Roll == 0f && idle.Pitch == 0f && idle.Yaw == 0f,
                $"a cursor inside the deadzone flies nothing (roll {idle.Roll:0.###}, pitch {idle.Pitch:0.###}, yaw {idle.Yaw:0.###})");

            plane.MouseStickForTest = new Vector2(0.8f, 0f);
            gyro.MouseStickForTest = new Vector2(0.8f, 0f);
            var planeRight = plane.ReadKeyboard(Dt);
            var gyroRight = gyro.ReadKeyboard(Dt);
            ctx.Check(planeRight.Roll < -0.7f && planeRight.Yaw == 0f,
                $"sideways mouse motion banks the aeroplane and yaws it nowhere (roll {planeRight.Roll:0.###}, yaw {planeRight.Yaw:0.###})");
            ctx.Check(gyroRight.Yaw < -0.7f && gyroRight.Roll == 0f,
                $"and yaws the autogyro and banks it nowhere, the exchange the decode names (yaw {gyroRight.Yaw:0.###}, roll {gyroRight.Roll:0.###})");

            plane.MouseStickForTest = new Vector2(0f, 0.6f);
            var pull = plane.ReadKeyboard(Dt);
            ctx.Check(pull.Pitch > 0.5f && pull.Roll == 0f,
                $"a cursor below the middle pulls the nose up on either airframe (pitch {pull.Pitch:0.###})");

            FreeLookHold(ctx, plane);
            // Before KeysSum, which leaves the autogyro's roll keys ramped up for its own reading.
            AutogyroFliesAnOrdinaryOffset(ctx, gyro, gyroStats, plane, planeStats);
            KeysSum(ctx, gyro);
            KeyboardScheme(ctx, plane);
        }
        finally
        {
            gyro.QueueFree();
            plane.QueueFree();
        }

        ctx.Note($"the mouse flies the aeroplane through the seat's own stick reader, free-look and all");
    }

    [Suite("flight-mouse-capture",
        "the desktop mouse a flight seat takes, and the guard that keeps it off this harness: a "
        + "session assembled from this launch's own command line resolves the capture OFF because "
        + "the launch is scripted, while the same resolution from an interactive command line on "
        + "this very display says yes, a human seat carrying the harness's own answer holds no "
        + "mouse and leaves Input.MouseMode exactly where the harness left it over a run of frames, "
        + "and the decision goes false for a halted frame, a photo-mode pane, the pause options "
        + "leaf and a watcher's seat, which is what hands the pointer back to every board that "
        + "draws one")]
    internal static void FlightMouseCapture(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var scripted = FlightRosterPolicy.From(SessionSpec.Parse(new[] { "--run-tests" }));
        var interactive = FlightRosterPolicy.From(SessionSpec.Parse(new[] { "--fly" }));
        bool realDisplay = DisplayServer.GetName() != "headless";
        ctx.Check(!scripted.MouseCaptureAllowed,
            $"a session assembled from this launch's command line takes no mouse ({scripted.MouseCaptureAllowed})");
        ctx.Check(interactive.MouseCaptureAllowed == realDisplay,
            $"ABLE-TO-FAIL CONTROL: the same resolution from an interactive command line answers the display alone (allowed {interactive.MouseCaptureAllowed}, real display {realDisplay})");

        var before = Godot.Input.MouseMode;
        var seat = Rig(ctx, PlaneStats.Load(ctx.ZrdrPath, "player_bhawk"), "MouseCaptureSeat",
            human: true);
        try
        {
            seat.MouseFlying = true;
            seat.MouseCaptureAllowed = scripted.MouseCaptureAllowed;
            for (int frame = 0; frame < 30; frame++)
            {
                seat.StepMouseCaptureForTest(halted: false);
            }

            ctx.Check(!seat.HoldsMouseForTest() && Godot.Input.MouseMode == before,
                $"and thirty frames of a mouse-flying seat leave the harness's mouse mode alone (holding {seat.HoldsMouseForTest()}, mode {Godot.Input.MouseMode}, was {before})");
            BoardsGetThePointerBack(ctx, seat);
        }
        finally
        {
            Godot.Input.MouseMode = before;
            seat.QueueFree();
        }

        ctx.Note($"the capture is guarded off on this harness and the mouse mode is left at {before}");
    }

    [Suite("flight-mouse-scheme-live",
        "a Controls page accepted over the pause reaches the seat already flying: the real pause "
        + "Preferences leaf over the install's decoded layout, holding a rebinding feature that "
        + "saves to scratch, opened over two human seats, walks to the CONTROLS page, flips the "
        + "Mouse row to Fly, steps the sensitivity slider and stages a flight rebind, and ACCEPT "
        + "CHANGES puts player 1's seat on the mouse stick, the new sensitivity and the new control "
        + "at once while player 2's seat stays as it was, the "
        + "capture decision reads the same under either scheme, and once the leaf has closed a "
        + "later accept from the menu reaches no seat")]
    internal static void FlightMouseSchemeLive(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(UI.Menu.MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = UI.Menu.Original.OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
            return;

        string dir = System.IO.Path.Combine(ctx.ScratchDir, "flight-mouse-scheme-live");
        if (System.IO.Directory.Exists(dir))
            System.IO.Directory.Delete(dir, recursive: true);
        System.IO.Directory.CreateDirectory(dir);
        // ⚠ Before the feature saves anything, or the accept lands on this machine's own keymap.
        string? previousDir = BindingStore.DirectoryOverride;
        BindingStore.DirectoryOverride = dir;
        var mode = Godot.Input.MouseMode;
        var stats = PlaneStats.Load(ctx.ZrdrPath, "player_bhawk");
        var one = Rig(ctx, stats, "LiveSchemeSeat1", human: true);
        var two = Rig(ctx, stats, "LiveSchemeSeat2", human: true);
        two.PlayerIndex = 1;
        var controls = new UI.Menu.ControlsFeature((player, profile) => BindingStore.UserBindings().Save(player, profile));
        var leaf = PausePreferences.Build(ctx.DataRoot, layout, controls, _ => { });
        try
        {
            ctx.Check(leaf != null, $"the leaf builds over the install's decoded layout");
            if (leaf == null)
                return;
            AcceptOverThePause(ctx, leaf, controls, one, two);
            leaf.Close();
            controls.MouseFlying = false;
            controls.Accept();
            ctx.Check(one.MouseFlying,
                $"with the leaf closed, an accept from the menu reaches no seat, the listener having come off ({one.MouseFlying})");
        }
        finally
        {
            leaf?.Close();
            leaf?.Free();
            one.QueueFree();
            two.QueueFree();
            BindingStore.DirectoryOverride = previousDir;
            Godot.Input.MouseMode = mode;
        }

        ctx.Note($"an accepted Controls page reaches the flying seat without a restart");
    }

    // The walk a pilot makes over the pause: Options, the CONTROLS door, the Mouse row, ACCEPT
    // CHANGES. The rebind is staged through the feature rather than by a capture, since the
    // scripted run presses no hardware. The keymap half only has to prove it rides the same accept.
    private static void AcceptOverThePause(
        TestContext ctx, PausePreferences leaf, UI.Menu.ControlsFeature controls, FlightController one, FlightController two)
    {
        const Key Rebound = Key.F8;
        one.MouseStickForTest = new Vector2(0.8f, 0f);
        two.MouseStickForTest = new Vector2(0.8f, 0f);
        ctx.Check(!one.MouseFlying && one.ReadKeyboard(Dt).Roll == 0f,
            $"ABLE-TO-FAIL CONTROL: player 1's seat starts on head-look, the cursor banking nothing ({one.MouseFlying})");
        bool wantedOnLook = CaptureDecision(one);

        leaf.Open(new[] { new UI.Screens.MenuInput { Keyboard = true } }, 0, new[] { one, two });
        WalkTo(leaf, UI.Menu.Original.OriginalOptionsScreen.ControlsDoorKey);
        leaf.Drive(new UI.Menu.MenuCommands { Accept = true });
        ctx.Check(leaf.Shell.Screen == UI.Menu.Original.OriginalScreen.ControlsPrefs,
            $"the CONTROLS door opens over the pause ({leaf.Shell.Screen})");
        WalkTo(leaf, UI.Menu.Original.OriginalOptionsScreen.ControlsMouseKey);
        leaf.Drive(new UI.Menu.MenuCommands { Accept = true });
        controls.Context = InputContext.Flight;
        controls.Focus(IndexOf(controls.Actions, InputAction.AutoLand));
        controls.MoveSlot(RowSlots);
        controls.Offer(new Binding(DeviceId.Keyboard, BindingControl.Key((int)Rebound)));
        controls.ConfirmSteal();
        ctx.Check(controls.MouseFlying && !one.MouseFlying && !Holds(one.FlightKeymap, Rebound),
            $"a staged flip and rebind reach no seat before the accept (page {controls.MouseFlying}, seat {one.MouseFlying})");

        // Five sideways steps on the slider, a quarter of its scale up: twice the sensitivity.
        WalkTo(leaf, UI.Menu.Original.OriginalOptionsScreen.ControlsSensitivityKey);
        for (int i = 0; i < 5; i++)
            leaf.Drive(new UI.Menu.MenuCommands { MoveX = 1 });
        ctx.Check(controls.MouseSensitivity == 2f && one.MouseSensitivity == SensitivityScale.Default,
            $"the slider stages twice the sensitivity and the seat keeps its own until the accept (page {controls.MouseSensitivity}, seat {one.MouseSensitivity})");

        WalkTo(leaf, UI.Menu.Original.OriginalOptionsScreen.ControlsAcceptKey);
        leaf.Drive(new UI.Menu.MenuCommands { Accept = true });
        var flown = one.ReadKeyboard(Dt);
        ctx.Check(one.MouseFlying && flown.Roll < -0.7f,
            $"ACCEPT CHANGES puts player 1's flying seat on the mouse stick at once (flying {one.MouseFlying}, roll {flown.Roll:0.###})");
        ctx.Check(Holds(one.FlightKeymap, Rebound),
            $"and on the flight control staged beside it, the keymap riding the same accept");
        ctx.Check(one.MouseSensitivity == 2f,
            $"and on the sensitivity, so the captured stick needs half the hand travel at once ({one.MouseSensitivity})");
        ctx.Check(!two.MouseFlying && two.ReadKeyboard(Dt).Roll == 0f && two.MouseSensitivity == SensitivityScale.Default,
            $"ABLE-TO-FAIL CONTROL: player 2's seat, which nobody edited, stays on head-look at the default sensitivity ({two.MouseFlying}, {two.MouseSensitivity})");
        var saved = BindingStore.UserBindings().Load(1, default, readsKeyboard: true);
        ctx.Check(saved.MouseFlying && saved.MouseSensitivity == 2f,
            $"and the scheme and sensitivity are saved to player 1's file too, so a restart reads the same seat ({saved.MouseSensitivity})");
        bool wantedOnFly = CaptureDecision(one);
        ctx.Check(wantedOnLook && wantedOnFly,
            $"the capture decision is the same under either scheme, the seat holding the mouse for the stick and for head-look alike (look {wantedOnLook}, fly {wantedOnFly})");
        one.MouseStickForTest = null;
        two.MouseStickForTest = null;
    }

    // The capture decision for an unhalted frame on a seat allowed the mouse, read and put back in
    // one call. The harness must never leave a seat allowed, or its next frame would take the mouse.
    private static bool CaptureDecision(FlightController seat)
    {
        seat.MouseCaptureAllowed = true;
        bool wanted = seat.WantsMouseCaptureForTest(halted: false) && !seat.WantsMouseCaptureForTest(halted: true);
        seat.MouseCaptureAllowed = false;
        return wanted;
    }

    private static int IndexOf(System.Collections.Generic.IReadOnlyList<InputAction> actions, InputAction action)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            if (actions[i] == action)
                return i;
        }

        return 0;
    }

    private static bool Holds(ActionMap map, Key key)
    {
        foreach (var binding in map.Bindings(InputAction.AutoLand))
        {
            if (binding.Control.Kind == ControlKind.Key && binding.Control.Index == (int)key)
                return true;
        }

        return false;
    }

    private static void WalkTo(PausePreferences leaf, string key)
    {
        for (int guard = 0; guard < 32 && leaf.Shell.FocusedKey != key; guard++)
            leaf.Drive(new UI.Menu.MenuCommands { MoveY = 1 });
    }

    // Hold-to-look, the posture under both mouse schemes. While the free-look control is down the
    // mouse is the head's and the stick reads nothing from it. The frame it comes up, the stick
    // has it back. A hold rather than a toggle, so one tap cannot strand the mouse on the head.
    private static void FreeLookHold(TestContext ctx, FlightController plane)
    {
        plane.MouseStickForTest = new Vector2(0.8f, 0f);
        plane.HoldActionForTest(InputAction.FreeLook, true);
        var looking = plane.ReadKeyboard(Dt);
        ctx.Check(plane.FreeLookActiveForTest() && looking.Roll == 0f,
            $"a held free-look control takes the stick off the mouse (held {plane.FreeLookActiveForTest()}, roll {looking.Roll:0.###})");

        plane.HoldActionForTest(InputAction.FreeLook, false);
        var released = plane.ReadKeyboard(Dt);
        ctx.Check(!plane.FreeLookActiveForTest() && released.Roll < -0.7f,
            $"the release hands the mouse straight back to the stick (held {plane.FreeLookActiveForTest()}, roll {released.Roll:0.###})");

        // The reading a toggle could not give: a second press takes the mouse off the stick AGAIN
        // rather than handing it back. A toggle would hand it back on that second press.
        plane.HoldActionForTest(InputAction.FreeLook, true);
        var again = plane.ReadKeyboard(Dt);
        ctx.Check(plane.FreeLookActiveForTest() && again.Roll == 0f,
            $"and a second hold takes it off once more, no toggle underneath (held {plane.FreeLookActiveForTest()}, roll {again.Roll:0.###})");
        plane.HoldActionForTest(InputAction.FreeLook, false);
    }

    // The keys are not replaced by the mouse, they sum with it, which is what the original's arm
    // does to the same slots. An autogyro pilot banks with the roll keys while the mouse yaws.
    private static void KeysSum(TestContext ctx, FlightController gyro)
    {
        gyro.MouseStickForTest = new Vector2(0.8f, 0f);
        var summed = default(FlightInput);
        for (int i = 0; i < 120; i++)
        {
            gyro.HoldActionForTest(InputAction.RollLeft, true);
            summed = gyro.ReadKeyboard(Dt);
        }

        ctx.Check(summed.Roll > 0.5f && summed.Yaw < -0.7f,
            $"the roll keys bank the autogyro while the mouse yaws it (roll {summed.Roll:0.###}, yaw {summed.Yaw:0.###})");
        gyro.HoldActionForTest(InputAction.RollLeft, false);
    }

    // The control the whole feature has to leave alone. A seat that did not choose the scheme reads
    // no stick from the mouse at all, whether the free-look control is down or up. That control
    // is the same hold head-look reads.
    private static void KeyboardScheme(TestContext ctx, FlightController plane)
    {
        plane.MouseFlying = false;
        plane.MouseStickForTest = new Vector2(0.8f, 0.8f);
        plane.HoldActionForTest(InputAction.FreeLook, true);
        var off = plane.ReadKeyboard(Dt);
        ctx.Check(off.Roll == 0f && off.Pitch == 0f && off.Yaw == 0f,
            $"the keyboard scheme takes no stick from the mouse (roll {off.Roll:0.###}, pitch {off.Pitch:0.###}, yaw {off.Yaw:0.###})");
        ctx.Check(plane.FreeLookActiveForTest(),
            $"and reads the same held free-look posture head-look has always read ({plane.FreeLookActiveForTest()})");

        plane.HoldActionForTest(InputAction.FreeLook, false);
        var up = plane.ReadKeyboard(Dt);
        ctx.Check(!plane.FreeLookActiveForTest() && up.Roll == 0f && up.Pitch == 0f,
            $"and releasing it leaves the stick on the keys, not on the cursor (roll {up.Roll:0.###}, pitch {up.Pitch:0.###})");
    }

    // An ordinary cursor offset, a quarter of the way across the pane, flown into the real plant
    // for three seconds. It sits inside the third axis's 0.3 dead band. A naive mapping there
    // reads roll 0, pitch 0 and yaw 0, and holds heading 0.0. The same cursor banks the aeroplane
    // 84.5 degrees over. The aeroplane is the control on both halves: its own lateral axis
    // answers, and its yaw, fed by the absent third axis, reads exactly zero.
    private static void AutogyroFliesAnOrdinaryOffset(
        TestContext ctx, FlightController gyro, PlaneStats gyroStats, FlightController plane, PlaneStats planeStats)
    {
        var cursor = new Vector2(0.25f, 0.25f);
        gyro.MouseStickForTest = cursor;
        plane.MouseStickForTest = cursor;
        var gyroStick = gyro.ReadKeyboard(Dt);
        var planeStick = plane.ReadKeyboard(Dt);
        ctx.Check(gyroStick.Yaw < -0.15f && gyroStick.Pitch > 0.15f && gyroStick.Roll == 0f,
            $"a quarter of the way across the pane yaws and pitches the autogyro and leaves its roll to the keys (yaw {gyroStick.Yaw:0.###}, pitch {gyroStick.Pitch:0.###}, roll {gyroStick.Roll:0.###})");
        ctx.Check(planeStick.Roll == gyroStick.Yaw && planeStick.Yaw == 0f,
            $"ABLE-TO-FAIL CONTROL: the same offset reaches the aeroplane's bank by the same amount and its yaw not at all (roll {planeStick.Roll:0.###}, yaw {planeStick.Yaw:0.###})");

        float gyroHeading = FlownHeadingDeg(gyro, gyroStats, out float gyroPitch);
        float planeHeading = FlownHeadingDeg(plane, planeStats, out _);
        ctx.Check(Mathf.Abs(gyroHeading) > 10f && gyroPitch > 10f,
            $"and the plant flies it: the autogyro's nose comes {gyroHeading:0.0} degrees round and {gyroPitch:0.0} degrees up in three seconds");
        ctx.Check(Mathf.Abs(planeHeading) > 1f,
            $"the aeroplane the same cursor already flew is unchanged by this ({planeHeading:0.0} degrees of heading)");

        gyro.MouseStickForTest = null;
        plane.MouseStickForTest = null;
    }

    // Three seconds of the seat's own stick into a throwaway plant on that airframe's shipped
    // stats. The result is the heading it came round to and the pitch it climbed to. It is a plant
    // rather than the rig's own model because what is under test is the deflection, not the seat's
    // tick.
    private static float FlownHeadingDeg(FlightController rig, PlaneStats stats, out float pitchDeg)
    {
        var model = new FlightModel(stats);
        model.Reset(new Vector3(0f, SpawnAltitudeM, 0f), Basis.Identity, 60f, 0.85f);
        for (int i = 0; i < 180; i++)
        {
            var input = rig.ReadKeyboard(Dt);
            input.Throttle = 0.85f;
            model.Step(input, Dt);
        }

        var nose = -model.Attitude.Z;
        pitchDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(nose.Y, -1f, 1f)));
        return Mathf.RadToDeg(Mathf.Atan2(-nose.X, -nose.Z));
    }

    // The hand-back rule, read off the decision rather than off a capture this harness may not
    // take. Every state that stands a board or a free camera over the flight answers false. That
    // gives the pause sheet, the preferences leaf, photo mode and a watcher their pointer.
    private static void BoardsGetThePointerBack(TestContext ctx, FlightController seat)
    {
        seat.MouseCaptureAllowed = true;
        ctx.Check(seat.WantsMouseCaptureForTest(halted: false),
            $"an allowed seat flying with nothing over it wants the mouse ({seat.WantsMouseCaptureForTest(false)})");
        ctx.Check(!seat.WantsMouseCaptureForTest(halted: true),
            $"a halted frame hands it back, which is every pause sheet and every wrap-up board ({seat.WantsMouseCaptureForTest(true)})");

        seat.BeginPhotoMode();
        ctx.Check(!seat.WantsMouseCaptureForTest(halted: false),
            $"photo mode hands it back to the free camera's own right-button look ({seat.WantsMouseCaptureForTest(false)})");
        seat.EndPhotoMode();

        seat.BeginPauseLeaf();
        ctx.Check(!seat.WantsMouseCaptureForTest(halted: false),
            $"the pause options leaf hands it back to the preferences page ({seat.WantsMouseCaptureForTest(false)})");
        seat.EndPauseLeaf();

        seat.Spectating = true;
        ctx.Check(!seat.WantsMouseCaptureForTest(halted: false),
            $"and a watcher's seat never takes it, its pane being the spectator camera's ({seat.WantsMouseCaptureForTest(false)})");
        seat.Spectating = false;
        seat.MouseCaptureAllowed = false;
    }

    // One flying seat over a plant and nothing else: no model to draw, no camera and no HUD. Every
    // reading here comes out of the stick reader rather than off the screen. The capture suite
    // asks for a human seat instead, the mouse being one of the things only a person is handed.
    private static FlightController Rig(TestContext ctx, PlaneStats stats, string name,
        bool human = false)
    {
        var model = new Node3D { Name = name + "Model" };
        var rig = new FlightController
        {
            PlaneModel = model,
            PlayerIndex = 0,
            IsHumanPiloted = human,
            UseKeyboard = true,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Name = name,
        };
        rig.AddChild(model);
        var spawn = new Vector3(0f, SpawnAltitudeM, 0f);
        rig.Setup(new FlightModel(stats), null, new CamParams(), spawn, spawn + Vector3.Forward);
        ctx.Host.AddChild(rig);
        return rig;
    }
}
