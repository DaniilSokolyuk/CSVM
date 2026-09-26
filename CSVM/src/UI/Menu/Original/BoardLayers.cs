using System.Collections.Generic;
using CSVM.UI.Boards;

namespace CSVM.UI.Menu.Original;

/// <summary>The layers one Original screen is composed into, the eight lists a
/// <see cref="ComposedBoard"/> is built out of. Every composer takes this one collector rather
/// than the same eight parameters in an order of its own. A composer adds to the layer its shape
/// belongs to by name, so no argument order can move a shape between layers. Draw order is the
/// board's own and no rule of this type's. The shell builds one per compose and hands its lists
/// to the board. A module, a page and a row composer all write into that same one.</summary>
public sealed class BoardLayers
{
    /// <summary>The screen's fixed art under everything: its background movie and panes.</summary>
    public List<BoardPicture> Backdrop { get; } = new();

    /// <summary>Rectangles over the backdrop and under the pictures.</summary>
    public List<BoardFill> Fills { get; } = new();

    /// <summary>The page's own pictures, over the fills.</summary>
    public List<BoardPicture> Pictures { get; } = new();

    /// <summary>Connector lines over the pictures.</summary>
    public List<BoardStroke> Strokes { get; } = new();

    /// <summary>Text over the pictures.</summary>
    public List<BoardLine> Lines { get; } = new();

    /// <summary>Button plaques, one layer over every picture.</summary>
    public List<BoardPlaque> Plaques { get; } = new();

    /// <summary>List widgets whose entries flow, drawn with the text.</summary>
    public List<BoardNote> Notes { get; } = new();

    /// <summary>What is drawn over the finished screen: an open list, then a dialog over it.</summary>
    public List<BoardPanel> Overlays { get; } = new();
}
