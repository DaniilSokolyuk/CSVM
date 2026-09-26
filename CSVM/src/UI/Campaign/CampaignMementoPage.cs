using System;
using System.Collections.Generic;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;

namespace CSVM.UI.Campaign;

/// <summary>
/// The memento chooser (<c>MOMENTOSELECTION.SCRIPT</c>): the picture on show under its glass, the
/// two arrows that step through the pictures the profile has been awarded, and ACCEPT CHANGES and
/// CANCEL CHANGES under them. ACCEPT writes the name into the profile's memento slot and returns to
/// the cabin; every other way off leaves the cabin's picture where it was.
/// </summary>
public sealed class CampaignMementoPage : CampaignPage
{
    // The picture and the reflection over it, at [@MomentoSelection@]'s MS_MEMENTO and MS_GLASS.
    // The pictures are the 415x515 photographs the scrapbook draws from, so the pane takes them at
    // their own size and the 370x467 reflection sits inside that block.
    private const int PictureX = 184;
    private const int PictureY = 48;
    private const int GlassX = 206;
    private const int GlassY = 71;

    // The script's own creation order: ACCEPT, CANCEL, then the forward and back arrows.
    private const int AcceptRow = 0;
    private const int CancelRow = 1;
    private const int NextRow = 2;
    private const int PreviousRow = 3;

    private static readonly string[] Rows =
    {
        "Accept Changes",
        "Cancel Changes",
        "Next Memento",
        "Previous Memento",
    };

    // The picture the arrows have stepped onto, or "" for none stepped onto yet, which is the
    // profile's own. Cleared by every way off the screen, so a chooser opened again stands on what
    // the cabin hangs rather than on a step a CANCEL threw away.
    private string _stepped = string.Empty;

    /// <summary>Binds the page to its flow, opening on the picture the profile hangs now.</summary>
    public CampaignMementoPage(CampaignFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override CampaignScreen Screen => CampaignScreen.MementoSelection;

    /// <inheritdoc/>
    public override string Title => "CHANGE MEMENTO";

    /// <inheritdoc/>
    public override int RowCount => Rows.Length;

    /// <inheritdoc/>
    /// <remarks>The forward arrow, so a cursor arrives on a control that steps rather than on the
    /// one that commits.</remarks>
    public override int OpeningRow => NextRow;

    /// <summary>The picture on show and the glass over it. The picture is the scrapbook photograph
    /// the profile's memento names, which is where the script reads it from, and the reflection is
    /// drawn after it so the sheen lands on the picture rather than under it.</summary>
    public override IReadOnlyList<BoardPicture> Pictures
    {
        get
        {
            var layout = Flow.Layout;
            const string section = CampaignLayout.MementoSection;
            var (pictureX, pictureY) = layout.At(section, "MS_MEMENTO", PictureX, PictureY);
            var (glassX, glassY) = layout.At(section, "MS_GLASS", GlassX, GlassY);
            return new[]
            {
                new BoardPicture(new BoardArt(BoardArtLibrary.Ui, "SCRAPBOOK/" + Showing), pictureX, pictureY),
                new BoardPicture(
                    layout.Art(section, "MS_GLASS", new BoardArt(BoardArtLibrary.Ui, "MS_Reflection2.png")), glassX, glassY),
            };
        }
    }

    /// <summary>The memento the chooser is standing on, which ACCEPT would write.</summary>
    public string Showing
    {
        get
        {
            var awarded = Awarded();
            return _stepped.Length > 0 && awarded.Contains(_stepped) ? _stepped : Opening(awarded);
        }
    }

    /// <summary>How many pictures this profile may choose between.</summary>
    public int Choices => Awarded().Count;

    /// <inheritdoc/>
    public override BoardButtonRef Button(int row) => row switch
    {
        AcceptRow => new BoardButtonRef(BoardButton.AcceptMemento),
        CancelRow => new BoardButtonRef(BoardButton.CancelMemento),
        NextRow => new BoardButtonRef(BoardButton.NextMemento),
        PreviousRow => new BoardButtonRef(BoardButton.PreviousMemento),
        _ => BoardButtonRef.None,
    };

    /// <inheritdoc/>
    public override string RowText(int row) => Rows[row];

    /// <inheritdoc/>
    public override string Detail(int row) => row switch
    {
        AcceptRow => "Hangs this picture in the cabin",
        CancelRow => "Leaves the cabin's picture as it was",
        _ => Awarded().Count > 1
            ? "Steps through the pictures you have been awarded"
            : "The campaign has awarded no other picture yet",
    };

    /// <inheritdoc/>
    /// <remarks>Sideways on any row steps the picture, so the two arrow plaques are the pointer's
    /// way to do what a cursor does with left and right.</remarks>
    public override bool Step(int row, int dir) => dir != 0 && Move(dir);

    /// <inheritdoc/>
    public override bool Accept(int row)
    {
        switch (row)
        {
            case AcceptRow:
                Flow.Feature.CommitMemento(Showing);
                Leave();
                return true;
            case CancelRow:
                Leave();
                return true;
            case NextRow:
                return Move(1);
            case PreviousRow:
                return Move(-1);
            default:
                return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Back is CANCEL's own door, so it throws the step away like the plaque does and then
    /// lets the flow pop the screen.</remarks>
    public override bool Back()
    {
        _stepped = string.Empty;
        return false;
    }

    // The step the arrows and the sideways cursor share: one place forward or back through the
    // awarded pictures, wrapping at both ends the way the script's own 100 and 101 do.
    private bool Move(int dir)
    {
        var awarded = Awarded();
        if (awarded.Count < 2)
        {
            return false;
        }

        int at = awarded.IndexOf(Showing);
        at = Math.Max(at, 0);
        _stepped = awarded[(at + (dir > 0 ? 1 : awarded.Count - 1)) % awarded.Count];
        return true;
    }

    // The pictures this profile may choose between, read fresh: a mission flown while the flow
    // stands adds its award, and the page outlives the visit that built it.
    private List<string> Awarded()
    {
        var awarded = Flow.Profile is { } profile
            ? new List<string>(CampaignMementos.Awarded(profile))
            : new List<string>();
        if (awarded.Count == 0)
        {
            awarded.Add(CampaignMementos.Seeded);
        }

        return awarded;
    }

    // The picture a fresh visit stands on: the one the cabin hangs, or the first awarded where the
    // profile's own name is not among them.
    private string Opening(List<string> awarded)
    {
        string current = CampaignMementos.Current(Flow.Profile);
        return awarded.Contains(current) ? current : awarded[0];
    }

    // Every way off the plaques: the step is thrown away, so the next visit opens on the cabin's
    // own picture.
    private void Leave()
    {
        _stepped = string.Empty;
        Flow.GoTo(CampaignScreen.Cabin);
    }
}
