using CSVM.Utils;
using Godot;

namespace CSVM.UI.Boards;

/// <summary>
/// One rule for every debug panel hosted in a <b>flight</b> session: nothing in it takes keyboard
/// focus. A focused <see cref="Button"/> answers Space with "press me again", so the pilot's fire
/// key re-triggers whichever stepper was clicked last instead of the guns, and the arrow keys walk
/// the focus chain rather than reaching the aircraft (or the lab's orbit camera).
///
/// <para>Applied to a whole subtree rather than per widget so that a control added to a panel later
/// cannot re-open the hole, and it counts what it changed and what is left so the log line is a
/// measurement rather than a claim.</para>
/// </summary>
public static class PanelFocus
{
    /// <summary>Makes every <see cref="Control"/> under <paramref name="root"/> unfocusable and logs
    /// the tally under <paramref name="who"/>. <c>focusable_left</c> must read 0.</summary>
    public static void Strip(Node root, string who)
    {
        int controls = 0, stripped = 0;
        Walk(root, ref controls, ref stripped);
        // Re-walked rather than assumed 0 from what the first walk changed: the log line reports
        // what the tree actually holds now, not the caller's intent.
        int left = 0, ignored = 0;
        Walk(root, ref ignored, ref left, countOnly: true);
        Log.Debug("ui", $"{who}: {controls} control(s), {stripped} made unfocusable, focusable_left={left}");
    }

    private static void Walk(Node node, ref int controls, ref int focusable, bool countOnly = false)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Control c)
            {
                controls++;
                if (c.FocusMode != Control.FocusModeEnum.None)
                {
                    focusable++;
                    if (!countOnly)
                    {
                        c.FocusMode = Control.FocusModeEnum.None;
                    }
                }
            }
            Walk(child, ref controls, ref focusable, countOnly);
        }
    }
}
