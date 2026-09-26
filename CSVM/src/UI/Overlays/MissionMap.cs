using System;
using System.Collections.Generic;
using CSVM.UI.Boards;
using CSVM.UI.Menu;

namespace CSVM.UI.Overlays;

/// <summary>
/// The one chart-sheet drawer, shared by every screen that shows a mission's map: the briefing, the
/// pause screen and the campaign load screen. It turns what a reveal has placed into board
/// elements, crops a sheet to its authored window, and places an icon by world position through
/// that window.
///
/// The original reaches all of this through one control class from two dialog constructors
/// (docs/org/pause-screen.md), so there is one drawer here rather than one per screen. Engine-free,
/// like everything else a composed board is made of.
/// </summary>
public static class MissionMap
{
    // Where the ownship art's own nose is drawn, in revolutions clockwise from the top of the
    // sheet: an eighth of a turn counter-clockwise of it, measured off the bitmap's own mirror
    // axis. The original turns that art by the heading alone, so its own chart leans by this much.
    private const float OwnShipArtRevs = -0.125f;

    // The bitmap the shared block names for the player, and the only art the chart places that is
    // not drawn nose up (docs/org/pause-screen.md).
    private const string OwnShipArt = "singledev";

    /// <summary>The chart sheet as a picture, cropped to the window the dialog authors and placed
    /// so the crop's top left lands on the map's own position. A map with no clip draws whole.</summary>
    public static BoardPicture Sheet(EscapeMap map) => new(
        new BoardArt(BoardArtLibrary.Rimage, map.Bitmap),
        map.Position.X,
        map.Position.Y,
        Crop: map.Clip.Width > 0 && map.Clip.Height > 0
            ? new BoardCrop(map.Clip.X0, map.Clip.Y0, map.Clip.Width, map.Clip.Height)
            : null);

    /// <summary>Every visible picture element of one draw layer, in the order the reveal placed
    /// them, which is the order they draw in. Call it for the back layer and then the front, so
    /// what a <c>ToBack</c> pushed behind stays behind.</summary>
    public static void Elements(List<BoardPicture> into, BriefingReveal reveal, bool back)
    {
        foreach (var element in reveal.Elements)
        {
            if (element.Back == back && element.Visible && element.Opacity > 0f
                && element.Bitmap.Length > 0)
            {
                into.Add(new BoardPicture(
                    new BoardArt(BoardArtLibrary.Rimage, element.Bitmap),
                    element.At.X, element.At.Y, 0, element.Center, element.Opacity, element.Revs));
            }
        }
    }

    /// <summary>The connector lines a reveal has drawn, at most one per mission on the shipped
    /// data.</summary>
    public static IReadOnlyList<BoardStroke> Strokes(BriefingReveal? reveal)
    {
        var strokes = new List<BoardStroke>();
        foreach (var element in reveal?.Elements ?? Array.Empty<BriefingElement>())
        {
            if (element.Visible && element.Opacity > 0f && element.Points.Count >= 2)
            {
                strokes.Add(new BoardStroke(
                    element.Points[0].X, element.Points[0].Y,
                    element.Points[1].X, element.Points[1].Y,
                    element.Color.R, element.Color.G, element.Color.B, element.Opacity));
            }
        }

        return strokes;
    }

    /// <summary>One icon placed by world position, centred on where the chart puts it, or null when
    /// the position falls outside the map's window. ⚠ Off the window draws nothing rather than an
    /// icon clamped to an edge, which is what the original's own placement does.</summary>
    public static BoardPicture? Icon(
        EscapeMap map, string bitmap, float worldX, float worldZ, float revs = 0f)
    {
        if (bitmap.Length == 0 || !map.TryProject(worldX, worldZ, out var at))
        {
            return null;
        }

        return new BoardPicture(
            new BoardArt(BoardArtLibrary.Rimage, bitmap), at.X, at.Y, 0, true, 1f, revs);
    }

    /// <summary>The heading a forward vector reads, in revolutions clockwise. The chart puts world
    /// +X right and world -Z up, which is the compass tape's own zero, so a nose at -Z is zero and
    /// this is the number the pilot reads off the tape.</summary>
    public static float Heading(float forwardX, float forwardZ) =>
        (float)(Math.Atan2(forwardX, -forwardZ) / (Math.PI * 2d));

    /// <summary>The turn one icon takes for a forward vector, in revolutions clockwise: its heading
    /// less however far its own art is drawn off the top of the sheet, so the drawn nose lands on
    /// the heading the compass reads.</summary>
    public static float IconRevs(string bitmap, float forwardX, float forwardZ) =>
        Heading(forwardX, forwardZ) - ArtRevs(bitmap);

    /// <summary>Where one icon bitmap's own nose is drawn, in revolutions clockwise from the top of
    /// the sheet, which a turn has to take back off before the icon can read against an
    /// instrument.</summary>
    public static float ArtRevs(string bitmap) =>
        bitmap.Equals(OwnShipArt, StringComparison.OrdinalIgnoreCase) ? OwnShipArtRevs : 0f;
}
