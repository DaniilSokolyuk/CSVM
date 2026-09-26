using System;
using System.IO;
using CSVM.Flight.Modes;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// One Danger Zone photograph shown large over the board that opened it: the PNG the camera wrote,
/// fitted into the window with its own proportions on a dark backdrop, and a caption naming the
/// marker, the run clock and the way out. Built-in's results boards and Original's wrap-up page both
/// open this one viewer and each decides from its own cursor when it closes; the viewer reads no
/// device. A viewer built to take clicks raises <see cref="Dismissed"/> on one. A shot whose file
/// cannot be read shows its strip thumbnail instead.
/// </summary>
public sealed partial class ShotViewer : Control
{
    // The share of the window the picture may fill, with the band under it holding the caption.
    private const float PictureTop = 0.04f;
    private const float PictureBottom = 0.88f;
    private const float PictureSide = 0.04f;
    private const float CaptionTop = 0.90f;
    private const int CaptionFont = 18;

    private TextureRect _picture = null!;
    private Label _caption = null!;

    /// <summary>A click on the viewer, for the board that closes it on one.</summary>
    public event Action? Dismissed;

    /// <summary>The photograph showing, or null while the viewer is closed.</summary>
    public StuntShot? Shot { get; private set; }

    /// <summary>Whether a photograph is showing.</summary>
    public bool IsOpen => Shot != null;

    /// <summary>The pixel size of the picture showing: the PNG's own, or the thumbnail's where the
    /// file could not be read.</summary>
    public Vector2I ImageSize { get; private set; }

    /// <summary>Builds the (closed) viewer to fill its parent. <paramref name="clickCloses"/> is
    /// whether it takes the mouse: a board whose pointer is Godot's own wants the click, while a
    /// board that polls its own pointer sees the click itself and must not have it swallowed here.
    /// </summary>
    public static ShotViewer Build(bool clickCloses)
    {
        var viewer = new ShotViewer
        {
            Name = "shot_viewer",
            Visible = false,
            FocusMode = FocusModeEnum.None,
            MouseFilter = clickCloses ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore,
        };
        viewer.SetAnchorsPreset(LayoutPreset.FullRect);

        var backdrop = new ColorRect { Color = new Color(0f, 0f, 0f, 0.88f), MouseFilter = MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        viewer.AddChild(backdrop);

        viewer._picture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = PictureSide,
            AnchorRight = 1f - PictureSide,
            AnchorTop = PictureTop,
            AnchorBottom = PictureBottom,
        };
        viewer.AddChild(viewer._picture);

        viewer._caption = ResultsBoard.Label(string.Empty, CaptionFont, ResultsBoard.RowColor);
        viewer._caption.HorizontalAlignment = HorizontalAlignment.Center;
        viewer._caption.MouseFilter = MouseFilterEnum.Ignore;
        viewer._caption.AnchorLeft = 0f;
        viewer._caption.AnchorRight = 1f;
        viewer._caption.AnchorTop = CaptionTop;
        viewer._caption.AnchorBottom = 1f;
        viewer.AddChild(viewer._caption);
        return viewer;
    }

    /// <summary>Whether <paramref name="shot"/> has a picture to show: its frame has landed and
    /// was not lost. A shot still on its way is refused until it lands.</summary>
    public static bool CanOpen(StuntShot? shot) => shot is { Landed: true, Thumb: not null };

    /// <summary>Shows <paramref name="shot"/> full size, read from its file. Refused, the viewer
    /// left as it was, for a shot <see cref="CanOpen"/> turns down.</summary>
    public bool Open(StuntShot shot)
    {
        if (!CanOpen(shot))
        {
            return false;
        }

        var image = Read(shot.Path) ?? shot.Thumb!;
        ImageSize = image.GetSize();
        _picture.Texture = ImageTexture.CreateFromImage(image);
        float scale = IsInsideTree() ? Mathf.Max(1f, GetViewportRect().Size.Y / 720f) : 1f;
        _caption.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(CaptionFont * scale));
        _caption.Text = $"{shot.DzName}   ·   {StuntMission.FormatTime(shot.At)}   ·   Esc (pad B) close";
        Shot = shot;
        Visible = true;
        return true;
    }

    /// <summary>Hides the viewer and lets its picture go.</summary>
    public void Close()
    {
        Shot = null;
        Visible = false;
        _picture.Texture = null;
        ImageSize = Vector2I.Zero;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true } && IsOpen)
        {
            AcceptEvent();
            Dismissed?.Invoke();
        }
    }

    // The camera's own PNG, or null where it cannot be read (never written, or since deleted).
    private static Image? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var image = Image.LoadFromFile(path);
        return image != null && image.GetWidth() > 0 && image.GetHeight() > 0 ? image : null;
    }
}
