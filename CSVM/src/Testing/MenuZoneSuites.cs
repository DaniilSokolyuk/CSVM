using CSVM.UI.Screens;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// Whether the launchscreen holds still. The centred layout is three bands, and the header and the
/// footer are fixed: what the focused row says goes into slots that are drawn whether or not they
/// have anything in them, so moving the cursor cannot move the title or the controls line. This
/// drives a real <see cref="LaunchMenu"/> over a page whose rows carry descriptions of very
/// different lengths, then resizes the viewport under it to check the repaint a resize owes.
/// </summary>
internal static class MenuZoneSuites
{
    private const double StepDt = 1.0 / 60.0;

    // The window the bands are measured in, and the one it is resized to. Doubling the height is
    // the clearest case: every band metric is authored at 720p, so all three should double.
    private static readonly Vector2I SmallWindow = new(1280, 720);
    private static readonly Vector2I LargeWindow = new(2560, 1440);

    [Suite("menu-zone-layout",
        "whether the launchscreen holds still: a real LaunchMenu is walked over every row of "
        + "the paint screen, whose descriptions differ in length row by row, and the two fixed "
        + "bands must keep their heights throughout and through a refusal; then the viewport "
        + "under it is resized and the layout must follow with no press to prompt it")]
    internal static void MenuZoneLayout(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var view = new SubViewport
        {
            Size = SmallWindow,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        ctx.Host.AddChild(view);
        var menu = MenuSuiteHost.Menu(ctx);
        view.AddChild(menu);
        try
        {
            Fixed(ctx, menu);
            Resized(ctx, menu, view);
        }
        finally
        {
            view.RemoveChild(menu);
            menu.QueueFree();
            ctx.Host.RemoveChild(view);
            view.QueueFree();
        }
    }

    // The paint screen: every row has a description of its own, several of them long enough to
    // wrap, and the focused row's decal tile comes and goes. If anything on a screen can move the
    // bands, it can here.
    private static void Fixed(TestContext ctx, LaunchMenu menu)
    {
        menu.ShowMenu("paint");
        if (menu.Hangar is not { } flow)
        {
            throw new SuiteSkippedException("the paint aid did not reach the hangar");
        }

        var first = menu.ShownZones;
        int rows = flow.Page.RowCount;
        int moved = 0;
        int shortest = int.MaxValue;
        int longest = 0;
        for (int row = 0; row < rows; row++)
        {
            flow.FocusRow(row);
            var zones = menu.ShownZones;
            if (zones.Header != first.Header || zones.Footer != first.Footer
                || zones.Scale != first.Scale)
            {
                moved++;
            }

            int length = flow.Page.Detail(row).Length;
            shortest = Mathf.Min(shortest, length);
            longest = Mathf.Max(longest, length);
        }

        ctx.Note($"{rows} rows, header {first.Header}, footer {first.Footer}, scale {first.Scale}, descriptions {shortest} to {longest} characters");
        ctx.Check(rows > 1 && longest > shortest,
            $"the screen really does put different descriptions under its {rows} rows ({shortest} to {longest} characters), so the check below had something to catch");
        ctx.Check(moved == 0,
            $"the header and the footer kept their heights on every one of the {rows} rows ({moved} moved), so a longer description cannot shift the screen under the cursor");

        // A refusal used to be a line of its own, which moved everything above it.
        menu.ShowError("that press does nothing here");
        var refused = menu.ShownZones;
        ctx.Check(refused.Header == first.Header && refused.Footer == first.Footer
            && refused.Scale == first.Scale,
            $"and a refusal takes the description's own slot rather than a line of its own (header {refused.Header}, footer {refused.Footer})");
    }

    // A window resize reaches no input handler at all, so the layout is only repainted if the menu
    // watches the viewport itself.
    private static void Resized(TestContext ctx, LaunchMenu menu, SubViewport view)
    {
        menu.ShowMenu();
        var small = menu.ShownZones;
        float built = menu.HeaderBand.CustomMinimumSize.Y;
        ctx.Check(Mathf.IsEqualApprox(built, small.Header),
            $"the band the layout is holding is the one the metrics asked for ({built} of {small.Header}), so the heights below are the ones on screen");

        view.Size = LargeWindow;
        menu._Process(StepDt);
        var large = menu.ShownZones;
        float repainted = menu.HeaderBand.CustomMinimumSize.Y;
        ctx.Note($"header {small.Header} at {SmallWindow.Y} px, {large.Header} at {LargeWindow.Y} px, band now {repainted}");
        ctx.Check(large.Header > small.Header && large.Footer > small.Footer,
            $"the bands grew with the window ({small.Header} to {large.Header})");
        ctx.Check(Mathf.IsEqualApprox(repainted, large.Header),
            $"and the resize alone repainted the screen ({repainted} of {large.Header}), with no press to prompt it");
    }
}
