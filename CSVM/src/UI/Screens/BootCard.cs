using System;
using System.Globalization;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The engine half of the boot sequence: the black the block runs on, the node the copyright card
/// is drawn on, and the clock its holds run down. What the card is made of belongs to
/// <see cref="BootSequence.Card"/>; a <see cref="ComposedBoardView"/> draws it, so the art and the
/// strings resolve out of the extraction the way every other screen's do. The card goes down with
/// the first film and the black outlives it, so the waits and the fade have nothing to hold and
/// pass without a frame of their own.
/// <see cref="Play"/> is the one call a caller makes.
/// </summary>
public sealed partial class BootCard : Node
{
    private readonly ComposedBoard _card;
    private readonly string _dataRoot;

    private CanvasLayer? _layer;
    private ComposedBoardView? _view;
    private Action? _then;
    private double _left;

    private BootCard(string dataRoot)
    {
        Name = "boot_card";
        _dataRoot = dataRoot;
        _card = BootSequence.Card(dataRoot);
    }

    /// <summary>Runs the whole boot sequence on <paramref name="host"/>: this node puts up the
    /// stills, <paramref name="film"/> plays the three movies, and <paramref name="then"/> runs
    /// after the last of them, by which point nothing of the sequence is left on screen.</summary>
    public static void Play(Node host, string dataRoot, CinemaPlay film, Action then)
    {
        var card = new BootCard(dataRoot);
        host.AddChild(card);
        new BootSequence(film, card.Hold, card.Drop).Run(() =>
        {
            card.Close();
            then();
        });
    }

    /// <summary>Puts <paramref name="hold"/> on screen for <paramref name="seconds"/> and runs
    /// <paramref name="then"/> when it expires, or at once when a key or a left click ends it
    /// early. A hold of no seconds runs it in the same frame, so nothing of the block stands
    /// between two films. This has <see cref="BootSequence.ShowStill"/>'s shape and is what the
    /// sequence drives.</summary>
    public void Hold(BootHold hold, double seconds, Action then)
    {
        _then = then;
        _left = seconds;
        if (hold == BootHold.Card)
        {
            // Neither of the card's two inks reads the palette, so which one it is handed cannot
            // reach the picture.
            _view?.Show(_card, BoardPalette.Paper, string.Empty, string.Empty);
        }

        Log.Info("ui", $"boot {hold} {seconds.ToString("0.###", CultureInfo.InvariantCulture)}s");

        // ⚠ Do not leave a hold of no seconds to _Process: the original runs the films back to
        // back, so even one frame of black between two of them is a gap it does not have.
        if (_left <= 0.0)
        {
            Advance();
        }
    }

    /// <summary>Takes the card off the screen, leaving the block's black behind, and is called as
    /// the first film starts. ⚠ Do not hold the card under the films: <c>SHOWIMAGE</c>'s picture
    /// does not survive a <c>PLAYAVI</c>, the original showing the copyright notice at the
    /// beginning only (docs/formats/cinemas.md).</summary>
    public void Drop()
    {
        _view?.QueueFree();
        _view = null;
        Log.Info("ui", $"boot {BootHold.Card} down");
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        // The block owns a black screen for its whole length, so what a gap between two films
        // shows is black rather than whatever an empty viewport clears to.
        var black = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _view = ComposedBoardView.Build(_dataRoot);

        // The launchscreen's own layer, because the card is the screen standing in front of the
        // launchscreen; a film at HudLayers.Cinema then covers both of these.
        _layer = new CanvasLayer { Layer = HudLayers.Board, Name = "boot_card_layer" };
        _layer.AddChild(black);
        _layer.AddChild(_view);
        AddChild(_layer);
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_then == null || !Skips(@event))
        {
            return;
        }

        GetViewport().SetInputAsHandled();
        Advance();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_then == null)
        {
            return;
        }

        _left -= delta;
        if (_left <= 0.0)
        {
            Advance();
        }
    }

    // Any press at all, which is CinemaScreen.BootKeys read at a screen with no playback of its own
    // to ask. The stills take the films' set through the films' own member, so the sequence has one
    // rule and a pad button ends a still exactly as it ends a film.
    private static bool Skips(InputEvent @event) =>
        CinemaScreen.BootKeys.Skips(CinemaSkips.PressOf(@event, !Pads.InputBlocked));

    private void Advance()
    {
        var then = _then;
        _then = null;
        then?.Invoke();
    }

    private void Close()
    {
        _layer?.QueueFree();
        _layer = null;
        QueueFree();
    }
}
