using System.Collections.Generic;
using System.Globalization;
using CSVM.Bindings;
using CSVM.Sticks;

namespace CSVM.UI.Menu.Original;

/// <summary>How the KEYS AND BUTTONS page splits a row's bindings between its authored Control A and
/// Control B cells and the port's own Stick column. A stick binding is one on a stick model's identity
/// (<see cref="StickModel.Device"/>), and everything else stays with the two authored columns. So a
/// stick never shows twice and a capture on A or B never replaces one.</summary>
internal static class KeysStickColumn
{
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

    /// <summary>The Stick cell's text: the first stick binding's caption and, when more sticks hold
    /// the row, how many more. The column is half a panel wide, so a second caption would not fit,
    /// and an unnamed stick prints its control alone (<see cref="StickLabels.Column(Binding)"/>).
    /// </summary>
    public static string Text(IReadOnlyList<Binding> sticks)
    {
        if (sticks.Count == 0)
        {
            return string.Empty;
        }

        string first = StickLabels.Column(sticks[0]);
        return sticks.Count == 1
            ? first
            : first + " +" + (sticks.Count - 1).ToString(CultureInfo.InvariantCulture);
    }
}
