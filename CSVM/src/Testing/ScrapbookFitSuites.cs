using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// Every openable scrap's words measured against the box its zoom family authors. The shipped
/// <c>SCRAPBOOK.CSV</c> rows are opened one at a time through a real
/// <see cref="CampaignScrapbookZoomPage"/>. Each block is wrapped in the installed Windows face
/// its langui <c>[FONTID]</c> names. Ten blocks are taller than their box at the authored point
/// size. The renderer steps such a block down a point at a time until it fits. That departure is
/// recorded in <c>docs/formats/campaign-screens.md</c>, "Fitting a block to its box".
/// </summary>
internal static class ScrapbookFitSuites
{
    // The mission slots SCRAPBOOK.CSV keys its rows by (0 is the not-yet-started career), and the
    // most spreads any of them carries.
    private const int Slots = 25;
    private const int Spreads = 3;

    // An authored point in board pixels at 96 dpi. A fitted size is reported the way the langui
    // tag spells it, and LanguiFace.Pixels is the same ratio.
    private const float PointsPerPixel = 72f / 96f;

    // The degrade a scrap's title or body takes when RESRC1.H or the string table does not carry
    // its symbol. The symbol is drawn as itself, and its length is not the shipped text's.
    private const string Placeholder = "IDS_";

    [Suite("scrapbook-fit",
        "every openable scrap of the shipped SCRAPBOOK.CSV opened through the real zoom page and "
        + "its title, caption and body blocks measured in the installed face each langui row names "
        + "against the height its zoom family's LAYOUT.CSV row authors: the sweep names every block "
        + "taller than its box at the authored point size and whether that block carries shipped "
        + "text or an unresolved IDS_ symbol, and no block may still be over its box once the "
        + "renderer has stepped its point size down to fit")]
    internal static void ScrapbookFit(TestContext ctx)
    {
        string assets = Path.Combine(ctx.DataRoot, "extracted", "rof", "ASSETS");
        ctx.RequireData(Path.Combine(assets, "SCRAPBOOK.CSV"), $"the shipped scrapbook table");
        ctx.RequireData(Path.Combine(assets, "LAYOUT.CSV"), $"the shipped widget layout");
        var probe = new Control();
        var view = ComposedBoardView.Build(ctx.DataRoot);
        ctx.Host.AddChild(probe);
        try
        {
            if (probe.GetThemeDefaultFont() is not { } board)
            {
                throw new SuiteSkippedException("the default theme carries no font to measure with");
            }

            Sweep(ctx, view, board);
        }
        finally
        {
            ctx.Host.RemoveChild(probe);
            probe.QueueFree();
            view.QueueFree();
        }
    }

    // Every row of every spread of every slot, opened on the page that draws it. The page is asked
    // for its composed lines rather than the table being read again here. What is measured is
    // therefore what the screen puts on the board.
    private static void Sweep(TestContext ctx, ComposedBoardView view, Font board)
    {
        var fit = BoardFit.For(BoardFit.AuthoredWidth, BoardFit.AuthoredHeight);
        var flow = new CampaignFlow(
            new CampaignProfileStore(Path.Combine(ctx.ScratchDir, "scrapbook-fit", "Profiles")),
            UiStrings.TryLoad(ctx.DataRoot) ?? UiStrings.Empty,
            ctx.DataRoot);
        var page = new CampaignScrapbookZoomPage(flow);
        var report = new StringBuilder();
        var swollen = new List<string>();
        var unresolved = new List<string>();
        var faceless = new SortedSet<string>(StringComparer.Ordinal);
        int blocks = 0, boxed = 0, past = 0, placeholders = 0;
        for (int mission = 0; mission < Slots; mission++)
        {
            for (int spread = 1; spread <= Spreads; spread++)
            {
                foreach (var scrap in ScrapbookComposition.Items(ctx.DataRoot, mission, spread))
                {
                    if (!scrap.Opens)
                    {
                        continue;
                    }

                    flow.SetScrapbookZoom(mission, spread, scrap.Item);
                    foreach (var line in page.Captions)
                    {
                        blocks++;
                        var installed = line.Face is { } face ? view.Installed(face) : null;
                        if (line.Face is { } named && installed == null)
                        {
                            faceless.Add(named.Tag);
                        }

                        var font = installed ?? board;
                        bool symbol = line.Text.StartsWith(Placeholder, StringComparison.Ordinal);
                        placeholders += symbol ? 1 : 0;
                        float authored = ComposedBoardView.Block(fit, font, line);
                        var fitted = ComposedBoardView.Fitted(fit, font, line);
                        float drawn = ComposedBoardView.Block(fit, font, fitted);
                        string who = $"{mission}_{spread}_{scrap.Item} {scrap.ImageName} [{line.Face?.Tag ?? "-"}]";
                        string row = Line(who, line, authored, fitted, symbol);
                        report.AppendLine(row);
                        if (line.Height <= 0f)
                        {
                            continue;
                        }

                        boxed++;
                        past += drawn > line.Height ? 1 : 0;
                        if (authored > line.Height)
                        {
                            (symbol ? unresolved : swollen).Add(row);
                        }
                    }
                }
            }
        }

        Verdicts(ctx, blocks, boxed, past, placeholders, swollen, unresolved, faceless);
        report.AppendLine(
            $"{blocks} blocks, {boxed} with an authored box, {swollen.Count} over it at the "
            + $"authored size, {placeholders} drawn as an unresolved symbol, "
            + $"{faceless.Count} face(s) not installed here");
        ctx.WriteArtifact("test-scrapbook-fit.txt", report.ToString());
    }

    private static void Verdicts(
        TestContext ctx, int blocks, int boxed, int past, int placeholders,
        List<string> swollen, List<string> unresolved, SortedSet<string> faceless)
    {
        ctx.Check(blocks > 100 && boxed > 100, $"the sweep read the shipped table ({blocks} blocks, {boxed} boxed)");
        ctx.Check(
            swollen.Count > 0,
            $"the fit has something to prove: {swollen.Count} block(s) are taller than their box at the authored size");
        ctx.Same(0, past, $"and no block is still past its box once the renderer has fitted it");
        ctx.Note($"over the box at the authored size: {Join(swollen)}");
        ctx.Check(
            unresolved.Count == 0,
            $"no over-the-box block is an unresolved IDS_ symbol rather than shipped text ({Join(unresolved)})");
        ctx.Note($"{placeholders} block(s) draw an unresolved symbol as itself, none of them over its box");

        // A face the machine does not carry is measured, and drawn, in the board's own font, so the
        // fit above holds either way. Which faces those are is a property of the machine, not of the
        // change, so it is reported rather than failed.
        ctx.Note($"langui faces with no installed family here, measured in the board's own: {Join(faceless)}");
    }

    private static string Join(IReadOnlyCollection<string> rows) =>
        rows.Count == 0 ? "none" : string.Join(" | ", rows);

    private static string Line(string who, BoardLine line, float authored, BoardLine fitted, bool symbol)
    {
        var text = new StringBuilder(who);
        text.Append(CultureInfo.InvariantCulture, $" box {line.Width:0}x{line.Height:0}");
        text.Append(CultureInfo.InvariantCulture, $" block {authored:0} px at {line.Size * PointsPerPixel:0} pt");
        text.Append(CultureInfo.InvariantCulture, $" -> {fitted.Size * PointsPerPixel:0} pt");
        return text.Append(symbol ? " (unresolved symbol)" : string.Empty).ToString();
    }
}
