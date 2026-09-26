using System;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// Photo mode's only screen furniture, and its way out. A hint line naming the bindings, on a
/// canvas layer of its own, that fades to nothing after a few seconds; plus the Escape / pad-B
/// read that raises <see cref="Exit"/> for the session to act on. This node decides nothing about
/// the mode, it only says how to leave it and notices when you do.
/// ⚠ The hint FADES rather than persisting: the mode exists to compose a frame and a permanent
/// strip would be in it, while a mode that has swallowed the menu with no visible way back is the
/// worst thing it could be. Neither end of that is served by a toggle.
/// </summary>
public sealed partial class PhotoModeHud : CanvasLayer
{
    private const float HoldSeconds = 5f;    // fully lit before the fade starts. TUNE.
    private const float FadeSeconds = 1.5f;  // TUNE.

    private Label _hint = null!;
    private int[]? _padDevices;
    private bool _useKeyboard;
    private float _age;

    /// <summary>Escape (or pad B) was pressed: leave photo mode and bring the board back.</summary>
    public event Action? Exit;

    /// <summary>Builds the hint over <paramref name="padDevices"/>/<paramref name="useKeyboard"/>,
    /// the same per-seat filter the pane's camera reads, so in splitscreen only the player who
    /// opened photo mode can close it.</summary>
    public static PhotoModeHud Build(int[]? padDevices, bool useKeyboard)
    {
        var hud = new PhotoModeHud
        {
            Name = "photo_mode_hud",
            Layer = HudLayers.Board,
            _padDevices = padDevices,
            _useKeyboard = useKeyboard,
        };
        hud._hint = new Label
        {
            Text = "PHOTO MODE   ·   RMB / right stick look   ·   WASD/QE fly   ·   "
                 + "F (pad X) lock target   ·   Esc (pad B) menu",
            Position = new Vector2(18f, 14f),
            Modulate = new Color(0.78f, 0.9f, 1f),
        };
        hud._hint.AddThemeFontSizeOverride("font_size", 15);
        hud._hint.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.8f));
        hud._hint.AddThemeConstantOverride("shadow_offset_x", 1);
        hud._hint.AddThemeConstantOverride("shadow_offset_y", 1);
        hud.AddChild(hud._hint);
        return hud;
    }

    public override void _Process(double delta)
    {
        // Wall time, not sim time: photo mode holds the clock, so a sim-timed fade would never
        // start. This is screen furniture, not part of the picture the halt is preserving.
        if (_hint.Modulate.A <= 0f)
            return;
        _age += (float)delta;
        float over = _age - HoldSeconds;
        if (over <= 0f)
            return;
        var c = _hint.Modulate;
        c.A = Mathf.Clamp(1f - (over / FadeSeconds), 0f, 1f);
        _hint.Modulate = c;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false } when _useKeyboard:
                Leave();
                break;
            case InputEventJoypadButton { ButtonIndex: JoyButton.B, Pressed: true } pad
                when ReadsPad(pad.Device):
                Leave();
                break;
        }
    }

    private void Leave()
    {
        // Marked handled so the same press cannot also reach whatever the session put behind this
        //, the board is suspended rather than gone, and it reads Escape too.
        GetViewport().SetInputAsHandled();
        Exit?.Invoke();
    }

    // The seat's own pad filter, matching SpectatorCamera.ReadsPad: --no-pads and an unfocused
    // window both hold, and another player's pad cannot close a mode that is not theirs.
    private bool ReadsPad(int device)
    {
        foreach (int pad in CSVM.Pads.For(_padDevices))
            if (pad == device)
                return true;
        return false;
    }
}
