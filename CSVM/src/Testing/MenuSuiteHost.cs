using System;
using System.Collections.Generic;
using CSVM.Bindings;
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
    /// <summary>A bare host with no presentation, exits in <paramref name="exits"/>, defs under
    /// <paramref name="dataRoot"/>. <paramref name="saveBindings"/> stays null unless the suite
    /// pointed <see cref="CSVM.Bindings.BindingStore.DirectoryOverride"/> at scratch. Null
    /// <paramref name="chapterCinema"/> and <paramref name="closingCinema"/> play no film.
    /// <paramref name="netDoor"/> replaces the default socket door with a suite's own.</summary>
    internal static MenuHost Bare(
        List<MenuExit> exits,
        string dataRoot,
        out BuiltInSeat seat,
        Action<int, BindingProfile>? saveBindings = null,
        ChapterCinema? chapterCinema = null,
        ClosingCinema? closingCinema = null,
        NetPlayFeature? netDoor = null)
    {
        var host = new MenuHost(new PresentationRegistry(), new SilentMenuAudio(), exits.Add);
        AddFeatures(host, dataRoot, saveBindings, chapterCinema, closingCinema, netDoor);
        seat = new BuiltInSeat(new MenuInput { Keyboard = true });
        host.AddSeat(seat);
        return host;
    }

    /// <summary>Registers the shared features a launchscreen needs, as the launcher does. Before
    /// the seat: the host lends the setup feature's seat list once the feature is in, so seat 0
    /// has to be joined through it.</summary>
    internal static void AddFeatures(
        MenuHost host, string dataRoot, Action<int, BindingProfile>? saveBindings = null,
        ChapterCinema? chapterCinema = null, ClosingCinema? closingCinema = null,
        NetPlayFeature? netDoor = null)
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
        host.Features.Add(netDoor ?? NetDoor());
    }

    /// <summary>The multiplayer door as a suite gets it: the real ENet carrier, bound to the
    /// loopback address, and no port mapping at all. ⚠ Neither the wildcard bind nor the UPnP
    /// search belongs in a run: one raises a firewall dialog, the other reaches the router.
    /// </summary>
    internal static NetPlayFeature NetDoor() =>
        new((port, guests, bind) => CSVM.Net.EnetTransport.Host(port, guests, bind),
            (address, port) => CSVM.Net.EnetTransport.Join(address, port))
        {
            BindAddress = "127.0.0.1",
        };

    /// <summary>A launchscreen over a bare host, for a suite that drives the screens and reads
    /// nothing back from the host.</summary>
    internal static LaunchMenu Menu(TestContext ctx)
    {
        var host = Bare(new List<MenuExit>(), ctx.DataRoot, out var seat);
        return LaunchMenu.Build(ctx.ZrdrPath, ctx.DataRoot, host, seat.Input);
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
