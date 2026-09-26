using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Bindings;
using CSVM.Sticks;

namespace CSVM.UI.Menu.Original;

/// <summary>How the KEYS AND BUTTONS page splits a row's bindings between its authored Control A and
/// Control B cells and the port's own Stick column. A stick binding is one on a stick model's identity
/// (<see cref="StickModel.Device"/>), and everything else stays with the two authored columns. So a
/// stick never shows twice and a capture on A or B never replaces one.</summary>
internal static class KeysStickColumn
{
    /// <summary>What stands between two captions in one cell. A caption itself holds spaces and a
    /// trailing "-" or "+", so a slash is the mark no caption carries.</summary>
    public const string Separator = " / ";

    /// <summary>Whether <paramref name="binding"/> belongs in the Stick column.</summary>
    public static bool IsStick(Binding binding) => StickModel.TryFromDevice(binding.Device, out _);

    /// <summary>The row's bindings in their own order, stick ones apart from the rest.</summary>
    public static (List<Binding> Others, List<Binding> Sticks) Split(IReadOnlyList<Binding> bindings)
    {
        var others = new List<Binding>(bindings.Count);
        var sticks = new List<Binding>();
        foreach (var binding in bindings)
        {
            (IsStick(binding) ? sticks : others).Add(binding);
        }

        return (others, sticks);
    }

    /// <summary>The slot in the whole list that the n-th non-stick binding holds. Without one it is
    /// the count, the empty slot a capture adds at. Control A is n 0 and
    /// Control B n 1, so a stick bound ahead of the keys never shifts which binding they replace.
    /// </summary>
    public static int SlotOfOther(IReadOnlyList<Binding> bindings, int nth)
    {
        int seen = 0;
        for (int i = 0; i < bindings.Count; i++)
        {
            if (IsStick(bindings[i]))
            {
                continue;
            }

            if (seen++ == nth)
            {
                return i;
            }
        }

        return bindings.Count;
    }

    /// <summary>The slot of the first stick binding, the one the Stick cell names, or the count when
    /// the row holds none.</summary>
    public static int SlotOfStick(IReadOnlyList<Binding> bindings)
    {
        for (int i = 0; i < bindings.Count; i++)
        {
            if (IsStick(bindings[i]))
            {
                return i;
            }
        }

        return bindings.Count;
    }

    /// <summary>The Stick cell's text: every stick binding's caption in the row's order, the first
    /// being the one the clear gesture drops. An unnamed stick prints its control alone
    /// (<see cref="StickLabels.Column(Binding)"/>). A cell too narrow for it scrolls.</summary>
    public static string Text(IReadOnlyList<Binding> sticks) => Joined(sticks, StickLabels.Column);

    /// <summary>Several bindings' captions as one cell's line, in their own order.</summary>
    public static string Joined(IEnumerable<Binding> bindings, Func<Binding, string> caption)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(caption);
        return string.Join(Separator, bindings.Select(caption));
    }
}
