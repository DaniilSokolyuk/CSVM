using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using Godot;

namespace CSVM.Testing;

/// <summary>The weapon selectors from the control to the cursor: the four keys the original's own
/// keybind page names reach the four selector actions through the shipped keymap, the pad's one
/// button per class arrives on its own field rather than on the key half, and the tap/hold split
/// FireControl makes of that field steps the cursor forward on a tap and back on a hold.
/// ⚠ A headless run holds down no key and no stick, so the tick's two device sides are supplied
/// (<c>ObserveDeviceForTest</c>) and the seat's own reads answer them. The FireControl below is a
/// real one over fake slots, so the whole chain under test is the shipping code.</summary>
internal static class WeaponSelectorSuites
{
    private const float StepDt = 1f / 60f;

    // Frames either side of TapHoldButton.PadHoldSeconds at 60 Hz (15 frames).
    private const int TapFrames = 6;
    private const int HoldFrames = 40;

    [Suite("weapon-selector-input",
        "the weapon selectors on the original's own four keys and on the pad's one button per "
        + "class: F3/F4 reach the gun cycle both ways and F5/F6 the rocket cycle, each key on its "
        + "own reading, the pad's d-pad arrives on the pad field with the key half still released, "
        + "a tap on it steps the cursor forward only once it is released, a hold steps it back "
        + "once and its release adds nothing, and the chase view no longer sits on a weapon key")]
    internal static void WeaponSelectorInput(TestContext ctx)
    {
        var rig = new FlightController
        {
            PlayerIndex = 0,
            UseKeyboard = true,
            PadDevices = Array.Empty<int>(),
        };
        try
        {
            Keys(ctx, rig);
            PadSide(ctx, rig);
            ChaseView(ctx, rig);
        }
        finally
        {
            rig.Free();
        }

        ctx.Note($"the four selector keys and the pad's tap/hold split reach the cursor");
    }

    // Each of the four keys on its own tick: the action it drives reads true and the other three
    // read false, which is what a pair bound the wrong way round would fail.
    private static void Keys(TestContext ctx, FlightController rig)
    {
        var expected = new (Key Key, string What, Func<FireInputs, bool> Field)[]
        {
            (Key.F3, "F3 cycles the guns clockwise", i => i.GunSelectHeld),
            (Key.F4, "F4 cycles the guns counterclockwise", i => i.GunSelectBackHeld),
            (Key.F5, "F5 cycles the rockets clockwise", i => i.RocketSelectHeld),
            (Key.F6, "F6 cycles the rockets counterclockwise", i => i.RocketSelectBackHeld),
        };

        var idle = Read(rig, null, null);
        ctx.Check(!idle.GunSelectHeld && !idle.GunSelectBackHeld && !idle.RocketSelectHeld
                  && !idle.RocketSelectBackHeld && !idle.GunSelectPadHeld && !idle.RocketSelectPadHeld,
            $"ABLE-TO-FAIL CONTROL: a tick with nothing down reads every selector released");

        foreach (var (key, what, field) in expected)
        {
            var read = Read(rig, key, null);
            int others = 0;
            foreach (var (_, _, other) in expected)
            {
                if (other != field && other(read))
                {
                    others++;
                }
            }

            ctx.Check(field(read) && others == 0,
                $"{what}, and the other three selectors stay released ({others} did not)");
        }
    }

    // The pad's own button, and what FireControl makes of it. The key half must stay released while
    // the d-pad is down: a combined read would step forward on the press AND back on the release.
    private static void PadSide(TestContext ctx, FlightController rig)
    {
        var down = Read(rig, null, JoyButton.DpadRight);
        ctx.Check(down.GunSelectPadHeld && !down.GunSelectHeld,
            $"the d-pad arrives on the pad field alone (pad={down.GunSelectPadHeld}, key={down.GunSelectHeld})");

        var fire = new FireControl(Guns(4), new List<FakePylon>(), false, false);
        var duringTap = new List<int>();
        for (int frame = 0; frame < TapFrames; frame++)
        {
            fire.Step(StepDt, Read(rig, null, JoyButton.DpadRight));
            duringTap.Add(fire.GunSel);
        }
        bool stillFirst = duringTap.TrueForAll(slot => slot == 0);
        fire.Step(StepDt, Read(rig, null, null));
        ctx.Check(stillFirst && fire.GunSel == 1,
            $"a tap steps the gun cursor forward on release, not on the press (slot {fire.GunSel})");

        for (int frame = 0; frame < HoldFrames; frame++)
        {
            fire.Step(StepDt, Read(rig, null, JoyButton.DpadRight));
        }
        int afterHold = fire.GunSel;
        fire.Step(StepDt, Read(rig, null, null));
        ctx.Check(afterHold == 0 && fire.GunSel == 0,
            $"a hold steps it back once and its release adds nothing (slot {afterHold} then {fire.GunSel})");

        var rockets = new FireControl(new List<FakeGun>(), Pylons(4), false, false);
        for (int frame = 0; frame < HoldFrames; frame++)
        {
            rockets.Step(StepDt, Read(rig, null, JoyButton.DpadLeft));
        }
        rockets.Step(StepDt, Read(rig, null, null));
        ctx.Check(rockets.SelectedPylon == 3,
            $"the pad's rocket button splits the same way, its hold walking the belt back to pylon {rockets.SelectedPylon}");
    }

    // The chase view ships unbound (the cockpit-view cycle reaches it), so it can take none of the
    // keys the weapon selectors own. The loop stays: a default handed back here must still keep off
    // them.
    private static void ChaseView(TestContext ctx, FlightController rig)
    {
        var chase = rig.FlightKeymap.Bindings(InputAction.SelectChaseView);
        bool onWeaponKey = false;
        foreach (var binding in chase)
        {
            if (binding.Control.Kind == ControlKind.Key
                && binding.Control.Index is (int)Key.F3 or (int)Key.F4 or (int)Key.F5 or (int)Key.F6)
            {
                onWeaponKey = true;
            }
        }

        ctx.Check(chase.Count == 0 && !onWeaponKey,
            $"the chase view ships on no control at all ({chase.Count}), so no weapon cycle's key is spent on it");
    }

    // One tick with at most one key and at most one pad button down, read through the seat's own
    // production reads.
    private static FireInputs Read(FlightController rig, Key? key, JoyButton? button)
    {
        var keyboard = new OneSide();
        if (key is { } k)
        {
            keyboard.Keys.Add((int)k);
        }

        var pad = new OneSide();
        if (button is { } b)
        {
            pad.Buttons.Add((DefaultBindings.AnyPad, (int)b));
        }

        rig.ObserveDeviceForTest(keyboard, pad);
        return rig.SelectorInputsForTest();
    }

    private static List<FakeGun> Guns(int count)
    {
        var guns = new List<FakeGun>();
        for (int i = 0; i < count; i++)
        {
            guns.Add(new FakeGun());
        }

        return guns;
    }

    private static List<FakePylon> Pylons(int count)
    {
        var pylons = new List<FakePylon>();
        for (int i = 0; i < count; i++)
        {
            pylons.Add(new FakePylon());
        }

        return pylons;
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

    // An armed slot with nothing to draw: the selectors only ever read the ammo counter.
    private sealed class FakeGun : IGunSlot
    {
        public WeaponDef Weapon { get; } = new() { Id = "wep_suite_gun", Name = "wep_suite_gun", FireRate = 10f };

        public int Ammo { get; set; } = 10;

        public int Capacity => 10;

        public int MuzzleCount => 1;
    }

    private sealed class FakePylon : IPylonSlot
    {
        public WeaponDef Weapon { get; } = new() { Id = "wep_suite_rocket", Name = "wep_suite_rocket", FireRate = 1f };

        public int Ammo { get; set; } = 2;

        public int Capacity => 2;
    }
}
