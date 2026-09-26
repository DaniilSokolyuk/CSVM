using System;
using System.Text;
using CSVM.UI.Menu;

namespace CSVM.UI.Campaign;

/// <summary>
/// A campaign screen's one-line text field, the original's <c>cm_e_name</c> edit box. The keyboard
/// types into it and a pad drives the same buffer through an alphabet stepper, so the field needs
/// no keyboard at all. What it accepts is the campaign's own name rule
/// (<see cref="CampaignFeature.AcceptsNameChar"/>, <see cref="CampaignFeature.MaxNameLength"/>),
/// so a stepped or typed name is always one the feature would seat. The one exception is
/// <see cref="AcceptsNext"/>'s, which lets the unlocking pilot name be typed and nothing else.
/// </summary>
public sealed class CampaignTextEntry
{
    /// <summary>The longest name the field holds, <see cref="CampaignFeature.MaxNameLength"/>.</summary>
    public const int MaxLength = CampaignFeature.MaxNameLength;

    // What the pad steps through, in the order the stepper walks it. Upper case then digits then
    // the space, all of which the accept rule already admits, so a stepped name is always valid.
    private const string PadAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ";

    /// <summary>What has been typed, always a string the accept rule admits.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Whether the field is armed: the keyboard's letters are text rather than
    /// navigation, and the cursor axes edit the name rather than the screen's list.</summary>
    public bool Active { get; private set; }

    /// <summary>The field as a row draws it, with a caret while armed.</summary>
    public string Display => Active ? Text + "_" : Text;

    /// <summary>Whether one character may be typed: the original's alphanumeric-and-space
    /// rule (langui 707).</summary>
    public static bool Accepts(char c) => CampaignFeature.AcceptsNameChar(c);

    /// <summary>Whether a whole name is one the field would hold, empty names excluded.</summary>
    public static bool Valid(string text) => CampaignFeature.ValidName(text);

    /// <summary>Whether <paramref name="c"/> may follow <paramref name="text"/>. The name rule,
    /// widened by the one character the unlocking pilot name ends in and only where what stands in
    /// the box is that name's own prefix. Refusing it outright would make that cheat untypable,
    /// and admitting it anywhere would let a profile be created carrying one.</summary>
    public static bool AcceptsNext(string text, char c) =>
        Accepts(c)
        || CampaignCheats.UnlockName.StartsWith(text + c, StringComparison.OrdinalIgnoreCase);

    /// <summary>Arms the field for typing.</summary>
    public void Arm() => Active = true;

    /// <summary>Disarms the field, leaving what was typed.</summary>
    public void Disarm() => Active = false;

    /// <summary>Replaces the contents, dropping anything the accept rule refuses and truncating to
    /// <see cref="MaxLength"/>, so a name from any source is one the field could have typed.</summary>
    public void Set(string text)
    {
        var kept = new StringBuilder();
        foreach (char c in text)
        {
            if (Accepts(c) && kept.Length < MaxLength)
            {
                kept.Append(c);
            }
        }

        Text = kept.ToString();
    }

    /// <summary>Appends the characters typed this frame, silently dropping the ones the rule
    /// refuses and everything past the length cap. Returns whether the text changed.</summary>
    public bool Type(string chars)
    {
        string was = Text;
        var text = new StringBuilder(Text);
        foreach (char c in chars)
        {
            if (AcceptsNext(text.ToString(), c) && text.Length < MaxLength)
            {
                text.Append(c);
            }
        }

        Text = text.ToString();
        return Text != was;
    }

    /// <summary>Removes the last character. Returns whether anything went.</summary>
    public bool Backspace()
    {
        if (Text.Length == 0)
        {
            return false;
        }

        Text = Text[..^1];
        return true;
    }

    /// <summary>Adds one character at the alphabet's first entry, the pad's way of growing the
    /// name; the stepper then edits it. Returns whether one was added.</summary>
    public bool Append()
    {
        if (Text.Length >= MaxLength)
        {
            return false;
        }

        Text += PadAlphabet[0];
        return true;
    }

    /// <summary>Steps the last character through the alphabet, the pad's letter control. Returns
    /// whether it changed.</summary>
    public bool StepLast(int dir)
    {
        if (dir == 0 || Text.Length == 0)
        {
            return false;
        }

        char current = Text[^1];
        int at = PadAlphabet.IndexOf(current, StringComparison.Ordinal);
        int next = at < 0
            ? (dir > 0 ? 0 : PadAlphabet.Length - 1)
            : (((at + dir) % PadAlphabet.Length) + PadAlphabet.Length) % PadAlphabet.Length;
        if (PadAlphabet[next] == current)
        {
            return false;
        }

        Text = Text[..^1] + PadAlphabet[next];
        return true;
    }
}
