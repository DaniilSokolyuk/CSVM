using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using CSVM.UI.Overlays;

namespace CSVM.UI.Campaign;

/// <summary>
/// The mission briefing (<c>Campaign Briefing.png</c>): the parchment map, the flag pins, the
/// objectives note that fills in beside them, the narration, and REPLAY BRIEFING / RETURN TO CABIN
/// / GO TO FLIGHT CHECK. Everything on it is the mission's own data, held by the feature as
/// <see cref="CampaignFeature.Briefing"/> for <see cref="CampaignFlow.MissionSeq"/>: the map bitmap
/// and the narration name come from the chosen state, never from the mission's story position.
///
/// <para>The reveal's progress is the feature's; its clock is the shell's, advanced through
/// <see cref="Advance"/> once a frame. This page composes what the reveal has placed. The screen's
/// only rows are its three buttons: an objective is written on the parchment through
/// <see cref="Notes"/>, read rather than selected.</para>
/// </summary>
public sealed class CampaignBriefingPage : CampaignPage
{
    /// <summary>REPLAY BRIEFING's row.</summary>
    public const int ReplayRow = 0;

    /// <summary>RETURN TO CABIN's row.</summary>
    public const int CabinRow = 1;

    /// <summary>GO TO FLIGHT CHECK's row.</summary>
    public const int FlightCheckRow = 2;

    private const int ButtonRows = 3;

    // The objectives note's own authored placement, the dialog's OBJECTIVESLIST BACKGROUND
    // (docs/formats/briefing.md). It draws under every reveal element, which is where ToBack, the
    // one opcode ever aimed at it, leaves it.
    private const int ParchmentY = 295;

    private static readonly BoardArt Parchment = new(BoardArtLibrary.Rimage, "parchment");
    private static readonly Messages NoMessages = new();

    private readonly Dictionary<string, HangarArt?> _art = new(StringComparer.OrdinalIgnoreCase);

    // The briefing the decoded art below belongs to, so a changed mission drops the cache.
    private CampaignBriefing? _artFor;

    /// <summary>Binds the page to its flow.</summary>
    public CampaignBriefingPage(CampaignFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override CampaignScreen Screen => CampaignScreen.Briefing;

    /// <summary>The original's screen carries no title text at all; this names it for the board
    /// chrome, which heads every screen, with the mission's long name (langui <c>3450 + seq</c>).</summary>
    public override string Title =>
        Briefing is { } briefing
            ? Flow.Strings.Text(3450 + briefing.Mission.Seq, "MISSION BRIEFING")
            : "MISSION BRIEFING";

    /// <summary>The three buttons, and nothing else. An uncovered objective is written on the
    /// parchment through <see cref="Notes"/>, never as a row, so the cursor never stops on the
    /// mission's own text.</summary>
    public override int RowCount => ButtonRows;

    /// <inheritdoc/>
    public override string Footer =>
        "↑↓  Choose       Enter / A  Select       Esc / B  Back to the cabin";

    /// <summary>The mission's parchment map, the bitmap the state's own
    /// <c>BACKGROUND_IMAGES</c> names.</summary>
    public override HangarArt? Art =>
        State is { } state && state.Background.Length > 0
            ? Picture(state.Background, MapCaption())
            : null;

    /// <summary>The narration wav for the mission showing, or "" when there is none. The shell
    /// plays it.</summary>
    public string NarrationWav => Briefing?.NarrationWav ?? string.Empty;

    /// <summary>How many times the script has asked for its narration. A shell restarts playback
    /// whenever this changes, which is how REPLAY BRIEFING restarts the voice.</summary>
    public int NarrationStarts => Briefing?.NarrationStarts ?? 0;

    /// <summary>The briefing state driving the screen, or null when the mission has none.</summary>
    public BriefingState? State => Briefing?.State;

    /// <summary>The running reveal, or null before a mission is named.</summary>
    public BriefingReveal? Reveal => Briefing?.Reveal;

    /// <summary>The mission's whole objectives note, revealed or not.</summary>
    public IReadOnlyList<BriefingObjective> Objectives => Briefing?.Objectives ?? Array.Empty<BriefingObjective>();

    /// <summary>The mission's map, the objectives parchment over it, then every element the reveal
    /// has placed: the photographs, the flag pins, the flourishes. Each carries the script's own
    /// position, opacity and rotation, so what is drawn is the authored composition and when it
    /// arrives stays <see cref="BriefingReveal"/>'s business.</summary>
    public override IReadOnlyList<BoardPicture> Pictures
    {
        get
        {
            var pictures = new List<BoardPicture>();
            if (State is { Background.Length: > 0 } state)
            {
                pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Rimage, state.Background), 0, 0));
            }

            pictures.Add(new BoardPicture(Parchment, 0, ParchmentY));
            if (Reveal is { } reveal)
            {
                MissionMap.Elements(pictures, reveal, back: true);
                MissionMap.Elements(pictures, reveal, back: false);
            }

            return pictures;
        }
    }

    /// <summary>The route line a state may draw between two of its pins, at most one per mission.</summary>
    public override IReadOnlyList<BoardStroke> Strokes => MissionMap.Strokes(Reveal);

    /// <summary>The parchment's own title widget, at the dialog's authored position.</summary>
    public override IReadOnlyList<BoardLine> Captions =>
        new[] { new BoardLine(NoteHeading(), 35, 315, 185, 17, BoardInk.Heading) };

    /// <summary>The parchment's list: one entry per objective the reveal has uncovered, in the
    /// order it uncovered them. The reveal decides when a line appears and the widget's own
    /// wordwrap box decides how far the next one sits below it.</summary>
    public override IReadOnlyList<BoardNote> Notes
    {
        get
        {
            if (Reveal is not { } reveal || reveal.RevealedObjectives.Count == 0)
            {
                return Array.Empty<BoardNote>();
            }

            var objectives = Objectives;
            var entries = new List<string>();
            foreach (int index in reveal.RevealedObjectives)
            {
                if (index >= 0 && index < objectives.Count)
                {
                    entries.Add(objectives[index].Text);
                }
            }

            return new[] { CampaignBoards.ObjectivesNote(entries) };
        }
    }

    // The feature's briefing for the mission showing; the art cache follows it.
    private CampaignBriefing? Briefing
    {
        get
        {
            var briefing = Flow.Feature.Briefing;
            if (!ReferenceEquals(briefing, _artFor))
            {
                _artFor = briefing;
                _art.Clear();
            }

            return briefing;
        }
    }

    /// <summary>Moves the reveal on by a frame's worth of seconds. The shell calls this while the
    /// briefing is the screen showing; nothing else on the page needs a clock.</summary>
    public void Advance(double seconds) => Briefing?.Advance(seconds);

    /// <summary>The three plaques the dialog's own <c>BUTTONS</c> section carries, in its
    /// order.</summary>
    public override BoardButtonRef Button(int row) => row switch
    {
        ReplayRow => new BoardButtonRef(BoardButton.ReplayBriefing),
        CabinRow => new BoardButtonRef(BoardButton.ReturnToCabin),
        FlightCheckRow => new BoardButtonRef(BoardButton.GoToFlightCheck),
        _ => BoardButtonRef.None,
    };

    /// <inheritdoc/>
    public override string RowText(int row) => row switch
    {
        ReplayRow => Label("MSG_BTN_REPLAY_BRIEFING", "REPLAY BRIEFING"),
        CabinRow => Label("MSG_BTN_RETURN_TO_CABIN", "RETURN TO CABIN"),
        FlightCheckRow => Label("MSG_BTN_GO_TO_FLIGHT_CHECK", "GO TO FLIGHT CHECK"),
        _ => string.Empty,
    };

    /// <inheritdoc/>
    public override string Detail(int row) => row switch
    {
        ReplayRow => "Plays the briefing again from the start",
        CabinRow => "Back to the cabin",
        FlightCheckRow => "On to the flight check",
        _ => string.Empty,
    };

    /// <inheritdoc/>
    public override bool Accept(int row)
    {
        switch (row)
        {
            case ReplayRow:
                Briefing?.Restart();
                return true;
            case CabinRow:
                Flow.OpenCabin();
                return true;
            case FlightCheckRow:
                Flow.GoTo(CampaignScreen.FlightCheck);
                return true;
            default:
                return false;
        }
    }

    private string NoteHeading() => Label("MSG_BRF_DLG_OBJECTIVES", "Objectives");

    private string MapCaption()
    {
        if (Briefing is not { } briefing)
        {
            return string.Empty;
        }

        int seq = briefing.Mission.Seq;
        string act = Flow.Strings.Text(1220 + (seq / 5), string.Empty);
        return act.Length > 0 ? act : Flow.Strings.Text(3480 + seq, string.Empty);
    }

    // A MSG_* label, with the original's own words as the fallback: Messages.Get hands back the
    // raw key when the table is missing, and a menu shows a label rather than a key.
    private string Label(string key, string fallback)
    {
        string text = (Briefing?.Messages ?? NoMessages).Get(key);
        return text.StartsWith("MSG_", StringComparison.Ordinal) ? fallback : text;
    }

    // One rimage picture, decoded once and kept including a miss, so an absent extraction is
    // probed once per bitmap rather than once per frame.
    private HangarArt? Picture(string bitmap, string caption)
    {
        if (_art.TryGetValue(bitmap, out var art))
        {
            return art;
        }

        art = null;
        if (Flow.DataRoot is { } root)
        {
            var path = Path.Combine(root, "extracted", "rimage", bitmap.ToLowerInvariant() + ".png");
            if (PngImage.TryLoad(path) is { } image)
            {
                art = new HangarArt(image, caption);
            }
        }

        _art[bitmap] = art;
        return art;
    }
}
