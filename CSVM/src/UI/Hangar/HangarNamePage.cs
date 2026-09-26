using System;

namespace CSVM.UI.Hangar;

/// <summary>
/// The PLANENAME screen: the name that is also the plane's identity in
/// <see cref="Flight.Hangar.CustomPlaneStore"/>. A pad names a plane by stepping two word lists
/// (<see cref="PlaneNameTables"/>) or rolling both at once, so naming costs no keyboard; a keyboard
/// types over the result for a name no list holds. The two are one field rather than two modes:
/// typing marks the name freeform and the steppers stop describing it, and the next step throws the
/// typed name away and rebuilds from the pair.
/// </summary>
public sealed class HangarNamePage : HangarPage
{
    /// <summary>The longest name the screen writes, the shared feature's rule.</summary>
    public const int MaxLength = Menu.HangarFeature.MaxNameLength;

    /// <summary>The name itself, which is also what typing edits.</summary>
    public const int NameRow = 0;

    /// <summary>The first word's stepper.</summary>
    public const int AdjectiveRow = 1;

    /// <summary>The second word's stepper.</summary>
    public const int NounRow = 2;

    /// <summary>Rolls both words at once.</summary>
    public const int RerollRow = 3;

    private readonly Random _rng;
    private int _adjective;
    private int _noun;
    private bool _freeform;

    /// <summary>Binds the page to its flow, drawing on the flow's own name generator.</summary>
    public HangarNamePage(HangarFlow flow)
        : base(flow) => _rng = flow.NameRng;

    /// <inheritdoc/>
    public override HangarScreen Screen => HangarScreen.Name;

    /// <summary>The name, the two word steppers under it, and the reroll.</summary>
    public override int RowCount => 4;

    /// <summary>Whether the name is typed rather than the pair below it. A freeform name is the
    /// pilot's own, so the steppers describe nothing until one is pressed.</summary>
    public bool Freeform => _freeform;

    /// <summary>Whether the character can be typed into a name, the shared feature's rule. Public
    /// so the filename-safety of the whole set is one assertion rather than a walk over the
    /// keyboard.</summary>
    public static bool Accepts(char c) => Menu.HangarFeature.AcceptsNameChar(c);

    /// <summary>Rolls a name onto a plane that has none, so a pad reaches the build press without
    /// naming anything by hand. A plane that already carries a name keeps it: arriving here must
    /// never cost a pilot a name they chose, and an imported original save arrives carrying one
    /// this screen never composed.</summary>
    public override void Entered()
    {
        if (string.IsNullOrWhiteSpace(Scratch.Name))
        {
            Reroll();
            return;
        }

        _freeform = !string.Equals(Scratch.Name, Composed(), StringComparison.Ordinal);
    }

    /// <summary>A name costs nothing, so no row here takes the wallet mark.</summary>
    public override int? CostWith(int row) => null;

    /// <inheritdoc/>
    public override string RowText(int row) => row switch
    {
        NameRow => "Name: " + NameShown(),
        AdjectiveRow => "Adjective: " + PlaneNameTables.Adjectives[_adjective],
        NounRow => "Noun: " + PlaneNameTables.Nouns[_noun],
        _ => "Roll a new name",
    };

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        if (row == NameRow)
        {
            if (string.IsNullOrWhiteSpace(Scratch.Name))
            {
                // The commit is the gate (langui 203); saying so here is what stops a pilot
                // reaching the Build press and being refused for a name they never entered.
                return Flow.Strings.Text(203, "You must enter a name for your new plane.");
            }

            return Overwrites() ? $"Saving replaces your saved \"{Scratch.Name}\"" : string.Empty;
        }

        if (row == RerollRow)
        {
            return "Confirm for a fresh pair of words";
        }

        // A stepper cannot describe a typed name, so on one it offers what pressing it would cost
        // instead. That warning is the only notice given: there is no undo behind the step.
        if (_freeform)
        {
            return "Step to drop the typed name for " + Composed();
        }

        return row == AdjectiveRow
            ? $"{_adjective + 1} of {PlaneNameTables.Adjectives.Length}"
            : $"{_noun + 1} of {PlaneNameTables.Nouns.Length}";
    }

    /// <inheritdoc/>
    public override bool Step(int row, int dir)
    {
        if (dir == 0 || (row != AdjectiveRow && row != NounRow))
        {
            return false;
        }

        if (row == AdjectiveRow)
        {
            _adjective = PlaneNameTables.Wrap(_adjective + dir, PlaneNameTables.Adjectives.Length);
        }
        else
        {
            _noun = PlaneNameTables.Wrap(_noun + dir, PlaneNameTables.Nouns.Length);
        }

        _freeform = false;
        Scratch.Name = Composed();
        return true;
    }

    /// <summary>Confirm rolls on the reroll row and advances the flow everywhere else. The name row
    /// deliberately does not name the plane on confirm: it is the row a pilot is standing on when
    /// they are done, so its press has to be the one that moves on.</summary>
    public override bool Accept(int row)
    {
        if (row != RerollRow)
        {
            return false;
        }

        Reroll();
        return true;
    }

    /// <summary>Takes one typed character, or refuses it. The filter is on the keystroke and never
    /// on the stored name, so an imported original save's own characters survive being shown and
    /// saved even though nothing here would type them.</summary>
    public bool Type(char c)
    {
        if (!Accepts(c) || Scratch.Name.Length >= MaxLength)
        {
            return false;
        }

        // The first character typed over a rolled name replaces it rather than extending it: a
        // pilot who types at all wants a different name, not that one with a letter on the end.
        // Backspace is the way in for editing a rolled name, since it enters freeform in place.
        if (!_freeform)
        {
            _freeform = true;
            Scratch.Name = string.Empty;
        }

        Scratch.Name += c;
        return true;
    }

    /// <summary>Takes the last character back, entering freeform on a rolled name rather than
    /// replacing it, which is what makes a rolled name editable at all.</summary>
    public bool Backspace()
    {
        bool was = _freeform;
        _freeform = true;
        if (Scratch.Name.Length == 0)
        {
            return !was;
        }

        Scratch.Name = Scratch.Name[..^1];
        return true;
    }

    private string Composed() => PlaneNameTables.Compose(_adjective, _noun);

    // The caret marks freeform, which is the only sign of which of the two the name currently is.
    // An empty name shows the placeholder and no caret: a lone caret reads as a name of one
    // underscore, which is a name the pilot could actually type.
    private string NameShown() =>
        Scratch.Name.Length == 0 ? "-" : Scratch.Name + (_freeform ? "_" : string.Empty);

    private void Reroll()
    {
        (_adjective, _noun) = PlaneNameTables.Roll(_rng, Taken);
        _freeform = false;
        Scratch.Name = Composed();
    }

    // A name a commit would land on. Not the visible roster: over a campaign flow that roster is
    // ownership and the build directory still holds every Instant Action plane, which a campaign
    // build must not silently overwrite.
    private bool Taken(string name) => Flow.IsNameTaken(name);

    // Whether saving would land on somebody else's file, the shared feature's rule: the plane
    // being edited is not somebody else, and a warning that fires on every edit is one nobody
    // reads by the time it matters.
    private bool Overwrites() => Flow.Feature.Overwrites();
}
