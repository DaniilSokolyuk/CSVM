using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Sticks;
using Godot;

namespace CSVM.Testing;

/// <summary>A flight stick's buttons on the discrete flight rows that the re-entry latch does not
/// read. They are the eleven targeting rows, the flyby view, the throttle digits and the camera rows. Each row gets its own stick button, the tick's pad side holds that button alone,
/// and the seat's production read answers.
/// ⚠ A headless run holds down no stick, so the sides are supplied (<c>ObserveDeviceForTest</c>, and
/// the camera's injected stick reader).</summary>
internal static class StickButtonSuites
{
    private static readonly DeviceId Stick = new StickModel(0x231D, 0x0200).Device;

    private static readonly InputAction[] TargetRows =
    {
        InputAction.TargetNextAlly,
        InputAction.TargetNextNonAircraft,
        InputAction.TargetNearest,
        InputAction.TargetClear,
        InputAction.TargetPreviousEnemy,
        InputAction.TargetPreviousAlly,
        InputAction.TargetPreviousNonAircraft,
        InputAction.TargetNearestEnemy,
        InputAction.TargetNearestAlly,
        InputAction.TargetNearestNonAircraft,
    };

    [Suite("stick-discrete-buttons",
        "a flight stick's button bound on a discrete flight row reaches it: each of the ten targeting "
        + "rows besides next-enemy reads down on its own button, next-enemy's stick button reaches the "
        + "tap/hold splitter alone while its key reaches the row alone, so each press dispatches once, "
        + "the flyby view and a throttle digit read their buttons, and the free camera's pad half reads "
        + "a stick button on a camera row")]
    internal static void StickDiscreteButtons(TestContext ctx)
    {
        var rig = new FlightController
        {
            PlayerIndex = 0,
            UseKeyboard = true,
            PadDevices = Array.Empty<int>(),
        };
        try
        {
            Targeting(ctx, rig);
            ViewAndThrottle(ctx, rig);
        }
        finally
        {
            rig.Free();
        }

        FreeCamera(ctx);
        ctx.Note($"every discrete flight row outside the latch reads a stick button bound to it");
    }

    private static void Targeting(TestContext ctx, FlightController rig)
    {
        for (int i = 0; i < TargetRows.Length; i++)
        {
            rig.FlightKeymap.Add(TargetRows[i], Button(i + 1));
        }

        rig.FlightKeymap.Add(InputAction.TargetNextEnemy, Button(20));

        Observe(rig, null, null);
        int idle = 0;
        foreach (var target in TargetRows)
        {
            idle += rig.TargetControlDownForTest(target) ? 1 : 0;
        }

        ctx.Check(idle == 0 && !rig.TargetControlDownForTest(InputAction.TargetNextEnemy) && !rig.TargetSplitterDownForTest(),
            $"ABLE-TO-FAIL CONTROL: a tick with nothing down reads every targeting row released ({idle} read down)");

        for (int i = 0; i < TargetRows.Length; i++)
        {
            Observe(rig, null, i + 1);
            int others = 0;
            foreach (var other in TargetRows)
            {
                if (other != TargetRows[i] && rig.TargetControlDownForTest(other))
                {
                    others++;
                }
            }

            ctx.Check(rig.TargetControlDownForTest(TargetRows[i]) && others == 0,
                $"stick button {i + 1} reads {TargetRows[i]} down and no other targeting row ({others} did)");
        }

        Observe(rig, null, 20);
        bool splitter = rig.TargetSplitterDownForTest();
        bool row = rig.TargetControlDownForTest(InputAction.TargetNextEnemy);
        ctx.Check(splitter && !row,
            $"next-enemy's stick button reaches the tap/hold splitter alone, so it dispatches once (splitter {splitter}, row {row})");

        Observe(rig, Key.E, null);
        splitter = rig.TargetSplitterDownForTest();
        row = rig.TargetControlDownForTest(InputAction.TargetNextEnemy);
        ctx.Check(row && !splitter, $"and its key reaches the row alone (row {row}, splitter {splitter})");
    }

    private static void ViewAndThrottle(TestContext ctx, FlightController rig)
    {
        rig.FlightKeymap.Add(InputAction.FlybyView, Button(30));
        rig.FlightKeymap.Add(InputAction.ThrottleSet4, Button(31));

        Observe(rig, null, null);
        ctx.Check(!rig.FlybyDownForTest() && rig.RequestedThrottleForTest() == null,
            $"ABLE-TO-FAIL CONTROL: nothing down asks for no flyby and no eighth ({rig.RequestedThrottleForTest()})");

        Observe(rig, null, 30);
        ctx.Check(rig.FlybyDownForTest(), $"a stick button on the flyby view reads it down");

        Observe(rig, null, 31);
        ctx.Check(rig.RequestedThrottleForTest() is 0.5f,
            $"a stick button on the fourth throttle digit asks for half throttle ({rig.RequestedThrottleForTest()})");
    }

    private static void FreeCamera(TestContext ctx)
    {
        var sticks = new OneSide();
        var camera = new Camera3D();
        var spectator = new SpectatorCamera(camera, Vector3.Zero, Vector3.Forward,
            Array.Empty<int>(), useKeyboard: true, playerIndex: 0, sticks: sticks);
        try
        {
            spectator.BindForTest(InputAction.CameraBoost, Button(40));
            ctx.Check(!spectator.PadHeldForTest(InputAction.CameraBoost),
                $"ABLE-TO-FAIL CONTROL: the free camera reads its boost row released with nothing down");

            sticks.Buttons.Add((Stick, 40));
            ctx.Check(spectator.PadHeldForTest(InputAction.CameraBoost),
                $"a stick button on the free camera's boost row reads it down");
        }
        finally
        {
            spectator.Free();
            camera.Free();
        }
    }

    private static Binding Button(int index) => new(Stick, BindingControl.Button(index));

    // One tick with at most one key and at most one stick button down.
    private static void Observe(FlightController rig, Key? key, int? stickButton)
    {
        var keyboard = new OneSide();
        if (key is { } k)
        {
            keyboard.Keys.Add((int)k);
        }

        var pad = new OneSide();
        if (stickButton is { } b)
        {
            pad.Buttons.Add((Stick, b));
        }

        rig.ObserveDeviceForTest(keyboard, pad);
    }

    // One side's hardware for a tick the suite writes itself.
    private sealed class OneSide : IDeviceState
    {
        public HashSet<int> Keys { get; } = new();

        public HashSet<(DeviceId Device, int Index)> Buttons { get; } = new();

        public bool IsKeyDown(DeviceId device, int keyCode) =>
            device == DeviceId.Keyboard && Keys.Contains(keyCode);

        public bool IsButtonDown(DeviceId device, int button) => Buttons.Contains((device, button));

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public float AxisValue(DeviceId device, int axis) => 0f;

        public HatDirection HatState(DeviceId device, int hat) => HatDirection.None;
    }
}
