using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Bindings;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>The menu host a suite stands a <see cref="LaunchMenu"/> on: the Free Flight, Instant
/// Action, player-setup, hangar and campaign features, one Built-in seat over a real poller (which reads nothing in
/// a scripted run), a silent audio service, and a sink that records every exit. The suites that
/// drive the launchscreen directly build through <see cref="Menu"/>; the host tracers register
/// the presentation itself.</summary>
internal static class MenuSuiteHost
{
    /// <summary>A bare host with no presentation registered, exits landing in <paramref name="exits"/>,
    /// the Instant Action feature reading environment defs under <paramref name="dataRoot"/>.
    /// <paramref name="saveBindings"/> is where an accepted rebind goes, and must stay null unless
    /// the suite has pointed <see cref="CSVM.Bindings.BindingStore.DirectoryOverride"/> at scratch.
    /// <paramref name="chapterCinema"/> and <paramref name="closingCinema"/> are the campaign's two
    /// films; null, every other suite's, means a cabin door plays none.</summary>
    internal static MenuHost Bare(
        List<MenuExit> exits,
        string dataRoot,
        out BuiltInSeat seat,
        Action<int, BindingProfile>? saveBindings = null,
        ChapterCinema? chapterCinema = null,
        ClosingCinema? closingCinema = null)
    {
        var host = new MenuHost(new PresentationRegistry(), new SilentMenuAudio(), exits.Add);
        AddFeatures(host, dataRoot, saveBindings, chapterCinema, closingCinema);
        seat = new BuiltInSeat(new MenuInput { Keyboard = true });
        host.AddSeat(seat);
        return host;
    }

    /// <summary>Registers the shared features a launchscreen needs, as the launcher does. Before
    /// the seat: the host lends the setup feature's seat list once the feature is in, so seat 0
    /// has to be joined through it.</summary>
    internal static void AddFeatures(
        MenuHost host, string dataRoot, Action<int, BindingProfile>? saveBindings = null,
        ChapterCinema? chapterCinema = null, ClosingCinema? closingCinema = null)
    {
        host.Features.Add(new FreeFlightFeature());
        host.Features.Add(InstantActionFeature.ForDataRoot(dataRoot));
        host.Features.Add(new PlayerSetupFeature());
        // The hangar reads its labels and stock defaults the way the launcher wires them: the langui
        // table, the stock fits on first need and the zrdr scope the data root carries.
        string zrdr = SessionPaths.PreferUnzipped(System.IO.Path.Combine(dataRoot, "extracted", "zrdr.zip"));
        var strings = UiStrings.TryLoad(dataRoot) ?? UiStrings.Empty;
        host.Features.Add(new HangarFeature(strings, PlanePickerRoster.AirframeNode, () => StockLoadouts.Load(), zrdr));
        host.Features.Add(new CampaignFeature(
            strings, PlanePickerRoster.AirframeNode, chapterCinema, closingCinema));
        // No save by default: a suite must never write over the keymap saved at this machine's
        // controls, and only a suite holding the store's directory override may pass one.
        host.Features.Add(new ControlsFeature(saveBindings));
    }

    /// <summary>A launchscreen over a bare host, for a suite that drives the screens and reads
    /// nothing back from the host. Its saved planes are <paramref name="suite"/>'s scratch store,
    /// as <see cref="Build"/> sets them.</summary>
    internal static LaunchMenu Menu(TestContext ctx, string suite)
    {
        var host = Bare(new List<MenuExit>(), ctx.DataRoot, out var seat);
        return Build(ctx, host, seat, suite);
    }

    /// <summary>A launchscreen over <paramref name="host"/> for <paramref name="seat"/>, reading and
    /// writing its saved planes in a fresh <see cref="ScratchPlanes"/> store for
    /// <paramref name="suite"/> and never in the player's <c>user://Planes</c>. The store makes its
    /// folder only on a save, so only a suite that saves a plane needs
    /// <see cref="DropScratchPlanes"/> after.</summary>
    internal static LaunchMenu Build(TestContext ctx, MenuHost host, BuiltInSeat seat, string suite)
    {
        var menu = LaunchMenu.Build(ctx.ZrdrPath, ctx.DataRoot, host, seat.Input);
        // Before the first ShowMenu: the roster refresh there is the store's first read.
        menu.PlaneStore = ScratchPlanes(ctx, suite);
        return menu;
    }

    /// <summary>A saved-plane store over a fresh <c>Planes</c> folder in <paramref name="suite"/>'s
    /// scratch directory, holding <paramref name="customs"/> builds written through the store's own
    /// writer. A launchscreen or presentation handed it reads this roster, not the player's
    /// <c>user://Planes</c>, whose contents would otherwise decide what the suite sees.
    /// <see cref="DropScratchPlanes"/> removes it.</summary>
    internal static CustomPlaneStore ScratchPlanes(TestContext ctx, string suite, int customs = 0)
    {
        DropScratchPlanes(ctx, suite);
        var store = new CustomPlaneStore(Path.Combine(ctx.ScratchDir, suite, "Planes"));
        for (int i = 0; i < customs; i++)
        {
            store.Save(new CustomPlaneDef
            {
                Name = $"Scratch Build {i + 1:D2}",
                Airframe = i % (CustomPlaneDef.MaxAirframe + 1),
            });
        }

        return store;
    }

    /// <summary>Deletes the folder <see cref="ScratchPlanes"/> made for <paramref name="suite"/>.</summary>
    internal static void DropScratchPlanes(TestContext ctx, string suite)
    {
        string dir = Path.Combine(ctx.ScratchDir, suite, "Planes");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>An audio service that plays nothing and records nothing: a scripted run has no
    /// sound to hear, and the launchscreen tolerates a silent install already.</summary>
    internal sealed class SilentMenuAudio : IMenuAudio
    {
        public void Cue(MenuCue cue)
        {
        }

        public void BeginNarration(string wavName)
        {
        }

        public void EndNarration()
        {
        }

        public void PreviewMix(CSVM.Utils.AudioLevels levels, MenuMixLevel moved)
        {
        }

        public void EndMixPreview()
        {
        }
    }
}
