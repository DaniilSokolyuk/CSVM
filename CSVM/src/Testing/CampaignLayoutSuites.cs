using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>
/// The campaign boards over the decoded layout compose the same screen the hardcoded chrome
/// composes: every scratch-profile aid is opened twice through a real <see cref="LaunchMenu"/>,
/// once with the flow pinned to <see cref="CampaignLayout.Fallback"/> and once reading the data
/// root's own <c>menu_layout.json</c>, and the two composed boards are compared element by element.
/// The decoded side must have loaded, and the rows the boards pin to a measurement must really
/// differ from it, or the comparison would be the fallback against itself.
/// </summary>
internal static class CampaignLayoutSuites
{
    // The aids that draw a campaign board, with the argument each takes. campaign-hangar opens the
    // hangar's own board menu over the flow, which is not a composed board, so it has no line here.
    private static readonly (string Aid, int Join)[] Aids =
    {
        ("campaign-empty", 0), ("campaign-roster", 0), ("campaign-entry", 0), ("campaign-cabin", 0),
        ("campaign-previous", 0), ("campaign-scrapbook", 0), ("campaign-briefing:24", 0),
        ("campaign-flightcheck", 0), ("campaign-guestcheck:2", 3), ("campaign-ammo", 0),
        ("campaign-planeselection", 0), ("campaign-planeselection:export", 0),
    };

    [Suite("campaign-layout-parity",
        "every campaign screenshot aid composed twice through a real LaunchMenu, once over the "
        + "hardcoded chrome and once over the decoded menu layout, and the two composed boards "
        + "compared element by element; the decoded layout must have loaded, and the six rows "
        + "the boards pin to a measured value must differ from their fallback, so the "
        + "comparison is between two sources and not the fallback against itself")]
    internal static void CampaignLayoutParity(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var decoded = CampaignLayout.For(ctx.DataRoot);
        ctx.Check(decoded.Decoded != null && decoded.Reason == null,
            $"the data root's layout loaded ({decoded.Reason ?? "no reason"})");
        if (decoded.Decoded == null)
        {
            return;
        }

        PinnedRowsDiffer(ctx, decoded);
        var menu = MenuSuiteHost.Menu(ctx);
        ctx.Host.AddChild(menu);
        menu.SetProcess(false);
        var report = new StringBuilder();
        try
        {
            foreach (var (aid, join) in Aids)
            {
                menu.CampaignLayoutOverride = CampaignLayout.Fallback;
                string hardcoded = Compose(menu, aid, join);
                menu.CampaignLayoutOverride = null;
                string read = Compose(menu, aid, join);
                bool fromRoot = menu.Campaign?.Layout == decoded;
                ctx.Check(fromRoot, $"{aid}: the second composition reads the data root's layout");
                ctx.Check(hardcoded.Length > 0 && hardcoded == read,
                    $"{aid}: the board over the decoded layout is the board over the hardcoded chrome ({FirstDifference(hardcoded, read)})");
                report.Append("== ").Append(aid).Append(" (").Append(hardcoded.Split('\n').Length.ToString(CultureInfo.InvariantCulture))
                    .Append(" elements) ==\n").Append(read).Append('\n');
            }
        }
        finally
        {
            menu.CampaignLayoutOverride = null;
            ctx.Host.RemoveChild(menu);
            menu.QueueFree();
        }

        ctx.WriteArtifact("campaign-layout-parity.txt", report.ToString());
    }

    // The six values docs/org/campaign-board.md pins: each must still differ from its row, or the
    // pin is dead and the board has two sources for one pixel.
    private static void PinnedRowsDiffer(TestContext ctx, CampaignLayout layout)
    {
        ctx.Check(layout.Widget(CampaignLayout.RosterSection, "CM_B_START")?.Field("ArtPath") == "GN_B_Continue.png",
            $"CM_B_START's row names GN_B_Continue.png, which the board pins to CM_B_Start.png");
        ctx.Check(layout.At(CampaignLayout.FlightCheckSection, "FC_B_CHANGEPLANE", 0f, 0f).Y == 132f
            && layout.At(CampaignLayout.FlightCheckSection, "FC_B_CHANGEPLANEW", 0f, 0f).Y == 350f,
            $"the flight check's paper plaques are authored at 132 and 350, pinned at the measured 131 and 349");
        ctx.Check(layout.Box(CampaignLayout.AmmoSection, "OL_S_AMMODESC", 0f, 0f, 0f).Y == 96f
            && layout.Box(CampaignLayout.AmmoSection, "OL_S_ROCKETDESC", 0f, 0f, 0f).Y == 332f,
            $"the two description panes are authored at y 96 and 332, pinned at the measured 92 and 328");
        ctx.Check(layout.Box(CampaignLayout.AmmoSection, "OL_T_TITLE", 0f, 0f, 0f).X == 132f
            && layout.Box(CampaignLayout.FlightCheckSection, "FC_T_TITLE", 0f, 0f, 0f).X == 132f,
            $"the ammo and flight check titles are authored at x 132, centred, pinned at the measured 138 left-justified");
        ctx.Check(layout.Box(CampaignLayout.FlightCheckSection, "FC_T_GUNLISTW", 0f, 0f, 0f).Y == 400f,
            $"FC_T_GUNLISTW is authored at 400, pinned at the pilot pair's drop of 17 (399)");
        ctx.Check(layout.Box(CampaignLayout.PlaneSelectionSection, "PS_T_WINGPLANE", 0f, 0f, 0f).Y == 323f,
            $"PS_T_WINGPLANE is authored at 323, pinned at the pilot line's 106 + 218");
    }

    private static string Compose(LaunchMenu menu, string aid, int join)
    {
        menu.ShowMenu(aid);
        if (join > 0)
        {
            menu.DebugJoin(join);
        }

        return menu.ShownBoard is { } board ? Describe(board) : string.Empty;
    }

    // One line per element, in draw order, so a difference names the element that moved.
    private static string Describe(ComposedBoard board)
    {
        var text = new StringBuilder();
        Layer(text, "backdrop", board.Backdrop);
        Layer(text, "fill", board.Fills);
        Layer(text, "picture", board.Pictures);
        Layer(text, "stroke", board.Strokes);
        Layer(text, "line", board.Lines);
        Layer(text, "plaque", board.Plaques);
        foreach (var note in board.Notes)
        {
            text.Append("note ").Append(note with { Entries = System.Array.Empty<string>() })
                .Append(" [").Append(string.Join(" | ", note.Entries)).Append("]\n");
        }

        foreach (var overlay in board.Overlays)
        {
            text.Append("overlay\n");
            Layer(text, "  fill", overlay.Fills);
            Layer(text, "  picture", overlay.Pictures);
            Layer(text, "  line", overlay.Lines);
        }

        return text.ToString();
    }

    private static void Layer<T>(StringBuilder text, string name, IReadOnlyList<T> elements)
    {
        foreach (var element in elements)
        {
            text.Append(name).Append(' ').Append(element).Append('\n');
        }
    }

    private static string FirstDifference(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return a.Length == 0 ? "no board over the hardcoded chrome" : "no board over the decoded layout";
        }

        var left = a.Split('\n');
        var right = b.Split('\n');
        for (int i = 0; i < left.Length || i < right.Length; i++)
        {
            string l = i < left.Length ? left[i] : "<end>";
            string r = i < right.Length ? right[i] : "<end>";
            if (l != r)
            {
                return $"element {i}: hardcoded {l} / decoded {r}";
            }
        }

        return "identical";
    }
}
