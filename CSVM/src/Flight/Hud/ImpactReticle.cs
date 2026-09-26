using System.IO;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>
/// The gun aiming reticle: the game's own <c>impact_point.png</c> pipper, a viewport-filling
/// <see cref="Control"/> fed a world impact point each frame by <see cref="Airframe.FlightController"/>,
/// projected through the live camera at <see cref="_Draw"/> time (never cached, mirrors
/// <see cref="TargetHud"/>). Fixed screen size scaled by <see cref="HudMetrics"/>.
/// ⚠ Deliberately not pinned to screen centre, and it marks the nose axis, not the aim assist's
/// line, so an assisted round does not go where the pipper points, the assist is meant to be
/// felt, not seen (docs/org/aim-assist.md "What the gun pipper follows").</summary>
public sealed partial class ImpactReticle : Control
{
    private const float RefSize = 40f; // pipper draw size in px at the 1440p reference (TUNE)

    private Texture2D _texture = null!;
    private Camera3D _camera = null!;

    /// <summary>The world-space ballistic impact point to mark (set each frame by the controller).</summary>
    public Vector3 ImpactPoint { get; set; }

    /// <summary>Whether to draw this frame. False hides the pipper, no firable gun, crashed, or no
    /// valid firing solution. Set by <see cref="Airframe.FlightController"/>.</summary>
    public bool Active { get; set; }

    /// <summary>Loads a single PNG from the extracted <c>rimage</c> UI set as a texture (the reticle
    /// pipper); null (with one log line) when the file is absent. These images carry their own alpha,
    /// so no colour-keying is needed, unlike the HUD font atlas.</summary>
    public static Texture2D? LoadTexture(string rimageDir, string file)
    {
        var path = Path.Combine(rimageDir, file);
        if (!File.Exists(path))
        {
            Log.Info("flight", $"[reticle] no {file} in {rimageDir}, gun reticle off (run ExtractRof.ps1)");
            return null;
        }
        var img = Image.LoadFromFile(path);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    /// <summary>Builds the reticle over the loaded pipper texture and this player's camera. Add it to
    /// the HUD canvas; feed <see cref="ImpactPoint"/> and <see cref="Active"/> each frame.</summary>
    public static ImpactReticle Build(Texture2D texture, Camera3D camera)
    {
        return new ImpactReticle
        {
            _texture = texture,
            _camera = camera,
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
        };
    }

    public override void _Process(double delta)
    {
        // Track the (resizable / splitscreen) pane and repaint, the pipper moves every frame.
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
        QueueRedraw();
    }

    public override void _Draw()
    {
        // A draw can land before _Process has sized us to the pane; HudMetrics would then read a
        // zero window and the rect would be degenerate. Nothing to draw at zero height anyway.
        if (!Active || Size.Y <= 0f)
        {
            return;
        }
        // A point behind the camera unprojects mirrored through centre, never draw it (as TargetHud).
        if (_camera.IsPositionBehind(ImpactPoint))
        {
            return;
        }
        var sp = _camera.UnprojectPosition(ImpactPoint);
        float size = RefSize * HudMetrics.Scale(this);
        DrawTextureRect(_texture, new Rect2(sp.X - size / 2f, sp.Y - size / 2f, size, size), false);
    }
}
