using System;

namespace CSVM.UI.Campaign;

/// <summary>Which of <c>MB_B_Icon.Png</c>'s three stacked icons a message box draws.
/// <c>MESSAGEBOX.SCRIPT</c> picks it off the button mask in <c>@globals@OR.UR</c> and never off the
/// button count: the <c>0x4</c> and <c>0x8</c> masks take frame 0, every other mask frame 1, and a
/// set <c>XR</c> takes frame 2 in place of that 1, its test sitting inside the default arm alone.
/// ⚠ The <c>0x2</c> mask is why the count decides nothing, being a two-button box that still draws
/// the warning.</summary>
public enum DialogIcon
{
    /// <summary>The question mark the <c>0x4</c> and <c>0x8</c> masks draw: the confirms.</summary>
    Query = 0,

    /// <summary>The exclamation mark every other mask draws: the notices and the refusals.</summary>
    Warning = 1,

    /// <summary>The skull a set <c>XR</c> draws over the <c>ma_</c> widget prefix. The credits
    /// screen's About box is the one box that asks for it.</summary>
    Death = 2,
}

/// <summary>
/// A dialog standing over a campaign screen, the original's <c>messagebox.script</c>: a message, an
/// icon and one button, holding every input until it is answered. The plane selection screen raises
/// two of them, langui 710 refusing a plane both crew slots would fly and langui 702 confirming an
/// export, and the script reaches both the same way, by setting a message id and running that
/// script over whatever screen was showing.
///
/// It lives on <see cref="CampaignFlow"/> rather than on a page because it is a facility
/// every screen shares. ⚠ It is not <see cref="CampaignFlow.Message"/>, which is the one-line
/// refusal band any navigation clears: a refusal answered in both places says itself twice.
/// </summary>
public sealed class CampaignModal
{
    private readonly Action? _confirmed;

    /// <summary>Builds a dialog over its words, its button's label, what its confirm runs and the
    /// icon its class draws. The icon defaults to the warning because both boxes the flow raises
    /// are the plane screen's <c>0x1</c> masks, langui 710 and 702.</summary>
    public CampaignModal(string message, string button, Action? confirmed = null, DialogIcon icon = DialogIcon.Warning)
    {
        Message = message;
        Button = button;
        Icon = icon;
        _confirmed = confirmed;
    }

    /// <summary>What the dialog says, wrapped into the box's own text widget.</summary>
    public string Message { get; }

    /// <summary>The button's words, which is the only answer a one-button dialog takes.</summary>
    public string Button { get; }

    /// <summary>Which icon the box draws, its message class rather than its button count.</summary>
    public DialogIcon Icon { get; }

    /// <summary>Answers the dialog, running whatever was to happen after it. The flow clears it.</summary>
    public void Confirm() => _confirmed?.Invoke();
}
