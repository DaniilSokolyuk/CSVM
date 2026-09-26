using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Hud;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>The control prompts over a real seat: which device the auto-dock and respawn lines name
/// as the seat's last real input moves from the pad to the keyboard and back, what a line reads when
/// the active device has no binding for the action, and where each line sits once composed. The
/// auto-dock wording comes from the install's own message table, so each check is on the whole
/// composed line rather than on the selection alone.
/// ⚠ A headless run holds down no key and no stick, so the tick's two halves are supplied
/// (<c>ObserveDeviceForTest</c>) rather than polled. The seat's own poll, the prompt composition and
/// the recompose on a handover are the shipping ones.</summary>
internal static class PromptDeviceSuites
{
    private const string PressKey = "MSG_PRESS_AUTOLAND";

    [Suite("bindings-prompt-device",
        "the auto-dock prompt follows the device the seat last took input from: a seat that has "
        + "touched nothing names its key in words, a pad button hands the line to the stick button "
        + "and gives the slot a glyph, a tick with nothing held leaves it alone, a key press takes "
        + "it back to words, and an auto-land the pad has no binding for falls back to naming the "
        + "key; the seat's pad rumble follows the same side, silent on a rostered pad while the "
        + "keyboard drives and handed back by the first pad press")]
    internal static void BindingsPromptDevice(TestContext ctx)
    {
        RumbleFollowsTheSide(ctx);

        ctx.RequireData(ctx.MessagesPath, $"the install's message table");
        var strings = Messages.Load(ctx.MessagesPath);
        string onKey = strings.Format(PressKey, "A");
        string onPad = strings.Format(PressKey, "Pad Left Stick");
        var rig = new FlightController
        {
            PlayerIndex = 0,
            UseKeyboard = true,
            PadDevices = Array.Empty<int>(),
        };
        try
        {
            rig.UseMessages(strings);
            ctx.Check(rig.PilotHud.AutoLandPrompt.Text == onKey && rig.PilotHud.AutoLandPrompt.Glyph == null,
                $"a seat that has touched nothing names its key in words: '{rig.PilotHud.AutoLandPrompt.Text}'");

            var padButton = OnPad(JoyButton.LeftStick);
            rig.ObserveDeviceForTest(new OneSide(), padButton);
            ctx.Check(rig.ActiveDeviceSide == DeviceSide.Pad && rig.PilotHud.AutoLandPrompt.Text == onPad,
                $"the pad button hands the line to the pad control: '{rig.PilotHud.AutoLandPrompt.Text}'");
            ctx.Check(rig.PilotHud.AutoLandPrompt.Glyph is { Kind: ControlKind.Button },
                $"…and that control draws as a glyph rather than as words: {rig.PilotHud.AutoLandPrompt.Glyph}");

            rig.ObserveDeviceForTest(new OneSide(), new OneSide());
            ctx.Check(rig.ActiveDeviceSide == DeviceSide.Pad && rig.PilotHud.AutoLandPrompt.Text == onPad,
                $"…and a tick with nothing held leaves it there: '{rig.PilotHud.AutoLandPrompt.Text}'");

            var key = new OneSide();
            key.Keys.Add((int)Key.A);
            rig.ObserveDeviceForTest(key, new OneSide());
            ctx.Check(rig.ActiveDeviceSide == DeviceSide.Keyboard && rig.PilotHud.AutoLandPrompt.Text == onKey
                && rig.PilotHud.AutoLandPrompt.Glyph == null,
                $"a key press takes the line back to the keyboard's words: '{rig.PilotHud.AutoLandPrompt.Text}'");

            Fallback(ctx, rig, onKey);
        }
        finally
        {
            rig.Free();
        }
    }

    [Suite("hud-auto-dock-line",
        "the auto-dock prompt stands on a centred line of its own rather than in the flight "
        + "readout block: the line the seat composes reaches the prompt's own control while the "
        + "offer holds and leaves it the frame the offer drops, the readout block never carries "
        + "it, the crashed, halted and held gates still keep it off, a handover to the pad moves "
        + "the wording with the seat's device, and the anchor is half the pane's width and three "
        + "tenths of its height in a full pane, a splitscreen pane and a wide one")]
    internal static void HudAutoDockLine(TestContext ctx)
    {
        ctx.RequireData(ctx.MessagesPath, $"the install's message table");
        Anchor(ctx);
        var strings = Messages.Load(ctx.MessagesPath);
        string onKey = strings.Format(PressKey, "A");
        string onPad = strings.Format(PressKey, "Pad Left Stick");
        var canvas = new CanvasLayer();
        var rig = new FlightController
        {
            PlayerIndex = 0,
            UseKeyboard = true,
            PadDevices = Array.Empty<int>(),
        };
        try
        {
            ctx.Host.AddChild(canvas);
            rig.UseMessages(strings);
            // The shipping attach path, off the tree the pane's canvas would give it.
            var hud = rig.PilotHud;
            hud.Attach(canvas, canvas, null, null);
            ctx.Check(hud.AutoDock != null, $"the pane's HUD carries a prompt control of its own");
            if (hud.AutoDock is { } prompt)
            {
                Offer(ctx, hud, prompt, onKey);
                Handover(ctx, rig, hud, prompt, onPad);
            }
        }
        finally
        {
            rig.Free();
            canvas.Free();
        }
    }

    [Suite("hud-crash-prompt",
        "the respawn prompt a crashed pilot reads: it stands on a centred line of its own, parented "
        + "to the MESSAGE layer the crash camera leaves up rather than the HUD layer it hides, it "
        + "names the seat's own Respawn control and follows a handover to the pad, a pad seat "
        + "drawing that control as a glyph in the slot the words fill while a keyboard seat keeps "
        + "the words, the readout block carries none of it, and a flying, halted or spectating "
        + "frame leaves it empty")]
    internal static void HudCrashPrompt(TestContext ctx)
    {
        var canvas = new CanvasLayer { Name = "hud" };
        var messages = new CanvasLayer { Name = "hud_messages" };
        var rig = new FlightController
        {
            PlayerIndex = 0,
            UseKeyboard = true,
            PadDevices = Array.Empty<int>(),
        };
        try
        {
            ctx.Host.AddChild(canvas);
            ctx.Host.AddChild(messages);
            rig.UseMessages(null);
            var hud = rig.PilotHud;
            hud.Attach(canvas, messages, null, null);
            ctx.Check(hud.CrashPrompt != null && hud.CrashPrompt.GetParent() == messages,
                $"the prompt hangs from the layer the crash cut leaves up: '{hud.CrashPrompt?.GetParent()?.Name}'");
            ctx.Check(hud.AutoDock != null && hud.AutoDock.GetParent() == canvas,
                $"…and the auto-dock line stays on the HUD layer: '{hud.AutoDock?.GetParent()?.Name}'");
            if (hud.CrashPrompt is { } prompt)
            {
                Crashed(ctx, hud, prompt);
                CrashHandover(ctx, rig, hud, prompt);
                OneDevice(ctx, rig, hud, prompt);
            }
        }
        finally
        {
            rig.Free();
            canvas.Free();
            messages.Free();
        }
    }

    // The crashed frames themselves: the composed line lands on the prompt control and nowhere in
    // the readout block, and the three states the button cannot answer keep it off.
    private static void Crashed(TestContext ctx, FlightHud hud, PromptLine prompt)
    {
        var down = new FlightHudState { Crashed = true, RespawnOffered = true };
        hud.Draw(in down);
        ctx.Check(prompt.Line == "Press Backspace to respawn",
            $"the crash puts the seat's own respawn line on the prompt control: '{prompt.Line}'");
        ctx.Check(hud.DrawnText is { } block && !block.Contains("respawn") && block.Contains("SPD"),
            $"…and the readout block carries the telemetry without it: '{hud.DrawnText ?? "<none>"}'");

        foreach (var state in new[]
        {
            new FlightHudState { RespawnOffered = true },
            new FlightHudState { Crashed = true, Halted = true, RespawnOffered = true },
            new FlightHudState { Crashed = true },
        })
        {
            hud.Draw(in state);
            ctx.Check(prompt.Line.Length == 0 && !FlightHud.ShowsRespawnPrompt(in state),
                $"…as do crashed={state.Crashed} halted={state.Halted} offered={state.RespawnOffered}");
        }

        hud.Draw(in down);
        hud.SetVisible(false);
        ctx.Check(!prompt.Visible, $"a cutscene's hide takes the respawn prompt with the rest");
        hud.SetVisible(true);
        ctx.Check(prompt.Visible, $"…and gives it back");
    }

    // A pad-side seat is named its pad button, never the key, over the same handover the auto-dock
    // line follows.
    private static void CrashHandover(TestContext ctx, FlightController rig, FlightHud hud,
        PromptLine prompt)
    {
        rig.ObserveDeviceForTest(new OneSide(), OnPad(JoyButton.LeftStick));
        hud.Draw(new FlightHudState { Crashed = true, RespawnOffered = true });
        ctx.Check(rig.ActiveDeviceSide == DeviceSide.Pad && prompt.Line == "Press Pad Y to respawn",
            $"a handover to the pad moves the respawn line with it: '{prompt.Line}'");
        ctx.Check(prompt.Prompt.Glyph is { Kind: ControlKind.Button, Index: (int)JoyButton.Y }
            && prompt.Prompt.Prefix == "Press " && prompt.Prompt.Suffix == " to respawn",
            $"…the pad button drawing as its own glyph in the slot the words fill: {prompt.Prompt.Glyph}");
    }

    // The two halves of a line the seat's device gate decides: a keyboard seat keeps words and no
    // glyph, so one line cannot show a key cap and a pad button at once.
    private static void OneDevice(TestContext ctx, FlightController rig, FlightHud hud, PromptLine prompt)
    {
        var key = new OneSide();
        key.Keys.Add((int)Key.A);
        rig.ObserveDeviceForTest(key, new OneSide());
        hud.Draw(new FlightHudState { Crashed = true, RespawnOffered = true });
        ctx.Check(rig.ActiveDeviceSide == DeviceSide.Keyboard && prompt.Prompt.Glyph == null
            && prompt.Prompt.Words == "Backspace",
            $"a keyboard seat keeps the key's words and takes no glyph: '{prompt.Line}'");
    }

    // The decoded anchor, a fraction of the PANE in every pane shape: a wide pane centres the
    // prompt in its own viewport rather than in the reading box the text block measures from.
    private static void Anchor(TestContext ctx)
    {
        var full = PromptLine.LineAnchor(new Vector2(2560f, 1440f));
        ctx.Check(Mathf.IsEqualApprox(full.X, 1280f) && Mathf.IsEqualApprox(full.Y, 432f),
            $"the prompt anchors at the pane's centre three tenths down: {full}");
        var quarter = PromptLine.LineAnchor(new Vector2(640f, 360f));
        ctx.Check(Mathf.IsEqualApprox(quarter.X, 320f) && Mathf.IsEqualApprox(quarter.Y, 108f),
            $"…each splitscreen pane on its own viewport: {quarter}");
        var wide = new Vector2(2560f, 720f);
        var stretched = PromptLine.LineAnchor(wide);
        ctx.Check(Mathf.IsEqualApprox(stretched.X, wide.X / 2f)
                  && Mathf.IsEqualApprox(stretched.Y, wide.Y * 0.3f),
            $"…and a wide pane on its own width and height, not the reading box's: {stretched}");
    }

    // The offer's own frames: the composed line lands on the prompt control and nowhere in the
    // readout block, and the three gates that answer no offer keep it off.
    private static void Offer(TestContext ctx, FlightHud hud, PromptLine prompt, string onKey)
    {
        var offered = new FlightHudState { AutoLandOffered = true, SpeedMps = 50f };
        hud.Draw(in offered);
        ctx.Check(prompt.Line == onKey,
            $"the offer puts the seat's own line on the prompt control: '{prompt.Line}'");
        ctx.Check(hud.DrawnText is { } block && !block.Contains(onKey) && block.Contains("SPD"),
            $"…and the readout block carries the telemetry without it: '{hud.DrawnText ?? "<none>"}'");

        hud.Draw(new FlightHudState { SpeedMps = 50f });
        ctx.Check(prompt.Line.Length == 0,
            $"the frame the offer drops leaves the line empty: '{prompt.Line}'");

        foreach (var state in new[]
        {
            new FlightHudState { AutoLandOffered = true, Held = true },
            new FlightHudState { AutoLandOffered = true, Crashed = true },
            new FlightHudState { AutoLandOffered = true, Halted = true },
        })
        {
            hud.Draw(in state);
            ctx.Check(prompt.Line.Length == 0 && !FlightHud.ShowsAutoLandPrompt(in state),
                $"…as do held={state.Held} crashed={state.Crashed} halted={state.Halted}");
        }

        hud.Draw(in offered);
        hud.SetVisible(false);
        ctx.Check(!prompt.Visible, $"a cutscene's hide takes the prompt with the rest");
        hud.SetVisible(true);
        ctx.Check(prompt.Visible, $"…and gives it back");
    }

    // The wording still follows the seat's device once the line has its own control to sit on.
    private static void Handover(TestContext ctx, FlightController rig, FlightHud hud,
        PromptLine prompt, string onPad)
    {
        rig.ObserveDeviceForTest(new OneSide(), OnPad(JoyButton.LeftStick));
        hud.Draw(new FlightHudState { AutoLandOffered = true, SpeedMps = 50f });
        ctx.Check(rig.ActiveDeviceSide == DeviceSide.Pad && prompt.Line == onPad,
            $"a handover to the pad moves the prompt's own line with it: '{prompt.Line}'");
    }

    // The pad side active on an action the pad carries no binding for: the line names the other
    // device's control rather than going empty, which is what an unbound half has to read as. The
    // pad control held is another action's, so the side still moves; the observe recomposes the
    // prompt itself, over the binding list the unassign shortened.
    private static void Fallback(TestContext ctx, FlightController rig, string onKey)
    {
        foreach (var binding in new List<Binding>(rig.FlightKeymap.Bindings(InputAction.AutoLand)))
        {
            if (binding.Control.Kind != ControlKind.Key)
                rig.FlightKeymap.Unassign(InputAction.AutoLand, binding);
        }

        rig.ObserveDeviceForTest(new OneSide(), OnPad(JoyButton.B));
        ctx.Check(rig.ActiveDeviceSide == DeviceSide.Pad && rig.PilotHud.AutoLandPrompt.Text == onKey
            && rig.PilotHud.AutoLandPrompt.Glyph == null,
            $"an auto-land the pad has no binding for names the key: '{rig.PilotHud.AutoLandPrompt.Text}'");
    }

    // The rumble gate over a real seat holding pad 0: the device memory it reads is the one the
    // prompts read, so a keyboard seat sends nothing and a pad press hands the rumble back. The
    // process-wide toggle, sink and pad gate are borrowed and restored, since --det holds them off.
    private static void RumbleFollowsTheSide(TestContext ctx)
    {
        var rig = new FlightController
        {
            PlayerIndex = 0,
            UseKeyboard = true,
            PadDevices = new[] { 0 },
        };
        var sink = new RumbleLog();
        var (enabled, was, disabled, focused) = (PadRumble.Enabled, PadRumble.Sink, Pads.Disabled, Pads.Focused);
        (PadRumble.Enabled, PadRumble.Sink, Pads.Disabled, Pads.Focused) = (true, sink, false, true);
        try
        {
            rig.RumbleForTest(RumbleEvent.ContactHeavy);
            ctx.Check(rig.ActiveDeviceSide == DeviceSide.Keyboard && sink.Pads.Count == 0,
                $"a keyboard seat with a pad in its roster rumbles nothing: {sink.Pads.Count} sends");

            rig.ObserveDeviceForTest(new OneSide(), OnPad(JoyButton.LeftStick));
            rig.RumbleForTest(RumbleEvent.ContactHeavy);
            ctx.Check(rig.ActiveDeviceSide == DeviceSide.Pad && sink.Pads.Count == 1 && sink.Pads[0] == 0,
                $"…and the first pad press hands the rumble back to pad 0: [{string.Join(", ", sink.Pads)}]");

            var key = new OneSide();
            key.Keys.Add((int)Key.A);
            rig.ObserveDeviceForTest(key, new OneSide());
            rig.RumbleForTest(RumbleEvent.ContactHeavy);
            ctx.Check(sink.Pads.Count == 1, $"…and a key press takes it off again: {sink.Pads.Count} sends");
        }
        finally
        {
            (PadRumble.Enabled, PadRumble.Sink, Pads.Disabled, Pads.Focused) = (enabled, was, disabled, focused);
            rig.Free();
        }
    }

    // One pad button held, on the placeholder identity a seat's pad rows sit on.
    private static OneSide OnPad(JoyButton button)
    {
        var side = new OneSide();
        side.Buttons.Add((DefaultBindings.AnyPad, (int)button));
        return side;
    }

    // One side's hardware for a tick the suite writes itself: keys for the keyboard half, buttons
    // and axes for the pad half, nothing at all for the other side's reads.
    private sealed class OneSide : IDeviceState
    {
        public HashSet<int> Keys { get; } = new();

        public HashSet<(DeviceId Device, int Index)> Buttons { get; } = new();

        public Dictionary<(DeviceId Device, int Index), float> Axes { get; } = new();

        public bool IsKeyDown(DeviceId device, int keyCode) =>
            device == DeviceId.Keyboard && Keys.Contains(keyCode);

        public bool IsButtonDown(DeviceId device, int button) => Buttons.Contains((device, button));

        public bool IsMouseButtonDown(DeviceId device, int button) => false;

        public float AxisValue(DeviceId device, int axis) =>
            Axes.TryGetValue((device, axis), out float value) ? value : 0f;

        public HatDirection HatState(DeviceId device, int hat) => HatDirection.None;
    }

    // What the seat asked of which pad, in order.
    private sealed class RumbleLog : IRumbleSink
    {
        public List<int> Pads { get; } = new();

        public void Play(int device, float weak, float strong, float seconds) => Pads.Add(device);
    }
}
