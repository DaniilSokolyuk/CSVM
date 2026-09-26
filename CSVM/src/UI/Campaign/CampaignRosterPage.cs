using CSVM.UI.Boards;
using CSVM.UI.Menu;

namespace CSVM.UI.Campaign;

/// <summary>
/// The player-profile screen (<c>Campaign Player Profile.png</c>, <c>CAMPAIGN.SCRIPT</c>): a name
/// field over the roster, then CONTINUE, DELETE PLAYER and CANCEL. CONTINUE creates the named
/// profile or continues the one that exists and opens the cabin, which is the original's own commit
/// path from the button, from Enter in the box and from a double-click on a roster row alike. A
/// roster row selects on the first confirm and continues on a second, the launchscreen's
/// double-enter idiom, so no press does two things at once. Deleting is a separate confirmed stage
/// and takes only the profile's own directory, never a hangar plane. The rules and the writes are
/// the feature's (<c>CampaignFeature.ContinuePlayer</c>, <c>DeletePlayer</c>); this page owns the
/// field, the stages and the cursor.
/// </summary>
public sealed class CampaignRosterPage : CampaignPage
{
    private readonly CampaignTextEntry _entry = new();

    // The delete confirm's own stage, drawn over the list rather than beside it, so the press that
    // removes a profile names the profile it removes.
    private bool _confirming;

    // The roster row the player picked this visit, "" for none. While it is empty the screen
    // stands on the store's remembered player instead, which is what <see cref="Selected"/> is.
    private string _selected = string.Empty;

    /// <summary>Binds the page to its flow.</summary>
    public CampaignRosterPage(CampaignFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override CampaignScreen Screen => CampaignScreen.Roster;

    /// <summary>The original's screen carries no title text of its own; this names it for the
    /// board chrome, which heads every screen.</summary>
    public override string Title => "SELECT PLAYER";

    /// <inheritdoc/>
    public override CampaignTextEntry? TextEntry => _entry;

    /// <summary>The name field, one row per stored profile, then the three buttons. The confirm
    /// stage replaces all of it with its two answers.</summary>
    public override int RowCount => _confirming ? 2 : Flow.Roster.Count + 4;

    /// <inheritdoc/>
    public override string Footer
    {
        get
        {
            if (_confirming)
            {
                return "↑↓  Choose       Enter / A  Answer       Esc / B  Keep";
            }

            return _entry.Active
                ? "Type a name       ←→  Letter       ↑↓  Add / remove       Enter / A  Continue       Esc / B  Done"
                : "↑↓  Choose       Enter / A  Select       Esc / B  Back";
        }
    }

    /// <summary>The cursor opens on the remembered player's own row, so returning to a campaign is
    /// one press. Row 0, the name field, when nothing is remembered or its profile is gone.</summary>
    public override int OpeningRow => _confirming ? 1 : RosterRow(Selected);

    private int ContinueRow => Flow.Roster.Count + 1;

    private int DeleteRow => Flow.Roster.Count + 2;

    // The profile the screen stands on: the pilot's own pick, or the store's remembered player
    // until they make one. Re-derived rather than latched, because the roster is read after the
    // page is built and a profile can be created or deleted under it.
    private string Selected
    {
        get
        {
            if (_selected.Length > 0)
            {
                return _selected;
            }

            string last = Flow.Feature.LastPlayed;
            return RosterRow(last) > 0 ? last : string.Empty;
        }
    }

    // What the screen's three actions act on: the typed name, or the profile it stands on while
    // the field is untouched. Picking a row fills the field, so the two agree after the first pick.
    private string NameInPlay
    {
        get
        {
            string typed = _entry.Text.Trim();
            return typed.Length > 0 ? typed : Selected;
        }
    }

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        if (_confirming)
        {
            return row == 0 ? $"Delete {_entry.Text}" : $"Keep {_entry.Text}";
        }

        if (row == 0)
        {
            string shown = _entry.Display.Length > 0 ? _entry.Display : Selected;
            return "Name:  " + (shown.Length > 0 ? shown : "(none)");
        }

        if (row <= Flow.Roster.Count)
        {
            string name = Flow.Roster[row - 1];
            return name == Selected ? "✓ " + name : name;
        }

        return row == ContinueRow ? "CONTINUE" : row == DeleteRow ? "DELETE PLAYER" : "CANCEL";
    }

    /// <summary>The three plaques the panel carries. The delete confirm replaces the whole screen
    /// with its two answers, so it draws no button at all and the answers stay list rows.</summary>
    public override BoardButtonRef Button(int row)
    {
        if (_confirming)
        {
            return BoardButtonRef.None;
        }

        if (row == ContinueRow)
        {
            return new BoardButtonRef(BoardButton.Continue);
        }

        if (row == DeleteRow)
        {
            return new BoardButtonRef(BoardButton.DeletePlayer);
        }

        return row == DeleteRow + 1 ? new BoardButtonRef(BoardButton.CancelProfile) : BoardButtonRef.None;
    }

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        if (_confirming)
        {
            return Flow.Strings.Text(201,
                "Are you sure you want to delete this player and all associated saved games?");
        }

        if (row == 0)
        {
            return _entry.Active
                ? "Letters, digits and spaces, up to 32 characters"
                : "Enter / A to type a player name";
        }

        if (row <= Flow.Roster.Count)
        {
            return Flow.Roster[row - 1] == Selected
                ? "Enter / A again to fly this player's campaign"
                : "Enter / A selects this player";
        }

        return row == ContinueRow ? "Creates the named player, or continues the one that exists"
            : row == DeleteRow ? "Removes the named player and its campaign; hangar planes stay"
            : "Back to the main menu";
    }

    /// <inheritdoc/>
    public override bool Accept(int row)
    {
        if (_confirming)
        {
            return AcceptConfirm(row);
        }

        if (row == 0)
        {
            if (!_entry.Active)
            {
                _entry.Arm();
                return true;
            }

            return Continue();
        }

        if (row <= Flow.Roster.Count)
        {
            string name = Flow.Roster[row - 1];
            if (name == Selected)
            {
                _entry.Set(name);
                return Continue();
            }

            _selected = name;
            _entry.Set(name);
            return true;
        }

        if (row == ContinueRow)
        {
            return Continue();
        }

        if (row == DeleteRow)
        {
            return BeginDelete();
        }

        Flow.Cancel();
        return true;
    }

    /// <inheritdoc/>
    public override bool Back()
    {
        if (_confirming)
        {
            _confirming = false;
            Flow.FocusRow(DeleteRow);
            return true;
        }

        if (_entry.Active)
        {
            _entry.Disarm();
            return true;
        }

        return false;
    }

    /// <summary>The unlocking pilot name pressed: every airframe is offered for the rest of the
    /// session, the player the screen stood on before is accepted again with a full wallet and the
    /// eleven stock airframes, and the box goes back to that name with the screen still standing.
    /// That is the script's own <c>RB</c> path, which re-accepts <c>MB.ZC</c> and returns -1 rather
    /// than opening the cabin. Public because Original commits this screen through a path of its
    /// own and takes this one from here rather than repeating it.</summary>
    public bool Unlock()
    {
        Flow.Cheats.AllowEverything();
        string previous = Selected;
        _entry.Set(previous);
        if (previous.Length > 0 && Flow.Feature.ContinuePlayer(previous) == null)
        {
            Flow.Feature.Wallet()?.UnlockEverything();
        }

        return true;
    }

    // Which row a profile name sits on, or 0 (the name field) for a name the roster does not
    // carry. 0 is also where a remembered profile that has since been deleted lands.
    private int RosterRow(string name)
    {
        if (name.Length == 0)
        {
            return 0;
        }

        for (int i = 0; i < Flow.Roster.Count; i++)
        {
            if (Flow.Roster[i] == name)
            {
                return i + 1;
            }
        }

        return 0;
    }

    // The screen's one commit path: the feature validates the name in the original's own words,
    // creates the profile when it is new and seats it; the page then opens the cabin on it.
    private bool Continue()
    {
        string name = NameInPlay;
        if (CampaignCheats.IsUnlockName(name))
        {
            return Unlock();
        }

        if (Flow.Feature.ContinuePlayer(name) is { } refusal)
        {
            Flow.SetMessage(refusal);
            return true;
        }

        _entry.Disarm();
        _selected = name;
        // ⚠ The flow's cabin door, not GoTo: a chapter's film plays on the way in, and this press
        // is the door a player actually takes into the campaign.
        Flow.OpenCabin();
        return true;
    }

    // Opens the confirm, on a profile that exists. The cursor lands on the keep answer: the press
    // that follows a mistaken DELETE PLAYER must not be the one that destroys a campaign.
    private bool BeginDelete()
    {
        string name = NameInPlay;
        if (name.Length == 0 || !Flow.Feature.HasPlayer(name))
        {
            Flow.SetMessage($"There is no player named \"{name}\".");
            return true;
        }

        // The confirm names the profile it is about, and it reads the field, so a delete aimed at
        // the remembered player has to put that name there first.
        _entry.Set(name);
        _confirming = true;
        Flow.FocusRow(1);
        return true;
    }

    // The confirm's own press. Deleting removes the profile's directory alone, so the planes the
    // profile referenced by name are still in the hangar afterwards.
    private bool AcceptConfirm(int row)
    {
        if (row == 0)
        {
            string name = _entry.Text.Trim();
            Flow.Feature.DeletePlayer(name);
            if (_selected == name)
            {
                _selected = string.Empty;
            }

            _entry.Set(string.Empty);
        }

        _confirming = false;
        Flow.FocusRow(0);
        return true;
    }
}
