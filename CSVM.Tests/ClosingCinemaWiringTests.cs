using System;
using System.IO;
using System.Linq;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The closing cinema as both presentations' campaigns actually reach it: the mission-end door onto
/// the scrapbook in each, the book arriving on the frame the film stops, a win short of the last
/// mission and a loss on it reaching the book with no film, and one instance serving both doors. A
/// campaign whose feature carries no closing cinema opens the book exactly as it always did, which
/// is what every suite and every golden gets. The click that skips the film is spanned across the
/// hand-back, so the book it opens fires nothing on the release.
/// </summary>
[Trait("Tier", "Quick")]
public class ClosingCinemaWiringTests : IDisposable
{
    private const int LastSeq = CampaignSequence.MissionCount - 1;

    private readonly string _dir;
    private readonly CampaignProfileStore _store;
    private readonly CustomPlaneStore _planes;

    public ClosingCinemaWiringTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-closing-cinema-" + Guid.NewGuid().ToString("N"));
        _store = new CampaignProfileStore(Path.Combine(_dir, "Profiles"));
        _planes = new CustomPlaneStore(Path.Combine(_dir, "Planes"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void BuiltInsMissionEndDoorPlaysTheClosingFilmAndTheBookFollowsIt()
    {
        var closing = new Recorder();
        var flow = Flow(closing, out _);

        flow.OpenScrapbookAfterMission(Flown("Zachary", CampaignSequence.MissionCount), LastSeq, missionWon: true);

        Assert.Equal(ClosingCinema.Name, closing.Name);
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        closing.Stop();
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.Equal(23, flow.MissionSeq);
    }

    // The reported fault, at the door it came through: a mission lost on a profile that has
    // finished the campaign opens the book and plays nothing.
    [Fact]
    public void ALostReplayOfTheLastMissionOpensBuiltInsBookWithNoFilm()
    {
        var closing = new Recorder();
        var flow = Flow(closing, out _);

        flow.OpenScrapbookAfterMission(Flown("Zachary", CampaignSequence.MissionCount), LastSeq, missionWon: false);

        Assert.Equal(0, closing.Plays);
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
    }

    // The negative control: the same door after any other mission plays nothing at all, which is
    // what a player sees 23 times out of 24.
    [Fact]
    public void AnUnfinishedCampaignOpensBuiltInsBookWithNoFilm()
    {
        var closing = new Recorder();
        var flow = Flow(closing, out _);

        flow.OpenScrapbookAfterMission(Flown("Zachary", 3), 2, missionWon: true);

        Assert.Equal(0, closing.Plays);
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
    }

    // ⚠ The mission-end door must not play a chapter film. Finishing a chapter's last mission puts
    // the profile on a chapter-opening position, and the cabin door's film would land the player on
    // the cabin instead of the book the mission just earned.
    [Fact]
    public void TheMissionEndDoorPlaysNoChapterFilmOnAChapterOpeningPosition()
    {
        var closing = new Recorder();
        var flow = Flow(closing, out Recorder chapters);

        flow.OpenScrapbookAfterMission(Flown("Zachary", 5), 4, missionWon: true);

        Assert.Equal(0, chapters.Plays);
        Assert.Equal(0, closing.Plays);
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
    }

    // What every engine suite and every golden gets: nothing hands those a cinema, so the book door
    // is the plain one it was before the wiring.
    [Fact]
    public void ACampaignWithNoCinemaOpensTheBookStraightAway()
    {
        var feature = new CampaignFeature(UiStrings.Empty, Airframe);
        feature.Open(_store, _planes, null, null);
        var flow = new CampaignFlow(feature);

        flow.OpenScrapbookAfterMission(Flown("Zachary", CampaignSequence.MissionCount), LastSeq, missionWon: true);

        Assert.Null(feature.ClosingCinema);
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
    }

    [Fact]
    public void OriginalsMissionEndDoorPlaysTheClosingFilmAndTheBookFollowsIt()
    {
        _store.Save(Flown("Zachary", CampaignSequence.MissionCount));
        var closing = new Recorder();
        var shell = Shell(closing, out _);
        shell.Campaign.OpenCampaignOver(_store);

        Assert.True(shell.Campaign.ShowScrapbook("Zachary", LastSeq, missionWon: true));

        Assert.Equal(ClosingCinema.Name, closing.Name);
        Assert.Equal(OriginalScreen.CampaignRoster, shell.Screen);
        closing.Stop();
        Assert.Equal(OriginalScreen.CampaignScrapbook, shell.Screen);
    }

    [Fact]
    public void AnUnfinishedCampaignOpensOriginalsBookWithNoFilm()
    {
        _store.Save(Flown("Zachary", 3));
        var closing = new Recorder();
        var shell = Shell(closing, out _);
        shell.Campaign.OpenCampaignOver(_store);

        Assert.True(shell.Campaign.ShowScrapbook("Zachary", 2, missionWon: true));

        Assert.Equal(0, closing.Plays);
        Assert.Equal(OriginalScreen.CampaignScrapbook, shell.Screen);
    }

    // The closing film hands back through the same latch the chapter films do: the press that skips
    // it is consumed through its release, so the book it opens fires nothing under the pointer.
    [Fact]
    public void TheClickThatSkipsTheClosingFilmFiresNothingOnTheBookItOpens()
    {
        _store.Save(Flown("Zachary", CampaignSequence.MissionCount));
        var closing = new Recorder();
        var shell = Shell(closing, out _);
        shell.Campaign.OpenCampaignOver(_store);
        Assert.True(shell.Campaign.ShowScrapbook("Zachary", LastSeq, missionWon: true));

        closing.Stop();
        Assert.Equal(OriginalScreen.CampaignScrapbook, shell.Screen);
        var cabin = shell.Rows.Single(r => r.Key == nameof(BoardButton.ReturnToCabin));
        shell.Step(new MenuCommands { Pointer = new MenuPointer(cabin.X + 3f, cabin.Y + 3f, true, true) });
        Assert.Equal(string.Empty, shell.ArmedKey);
        shell.Step(new MenuCommands { Pointer = new MenuPointer(cabin.X + 3f, cabin.Y + 3f, false, false) });
        Assert.Equal(OriginalScreen.CampaignScrapbook, shell.Screen);
    }

    // The decision the wiring owns: one instance for the process, reached by both presentations'
    // mission-end doors, and each door asking it about the mission that was just flown.
    [Fact]
    public void OneInstanceServesBothPresentationsAndEachDoorAsksAboutItsOwnMission()
    {
        var closing = new Recorder();
        var feature = new CampaignFeature(
            UiStrings.Empty, Airframe, new ChapterCinema(new Recorder().Play), new ClosingCinema(closing.Play));
        feature.Open(_store, _planes, null, null);
        var flow = new CampaignFlow(feature);
        flow.OpenScrapbookAfterMission(Flown("Zachary", CampaignSequence.MissionCount), LastSeq, missionWon: true);
        closing.Stop();
        Assert.Equal(1, closing.Plays);

        var shell = new OriginalShell(MenuLayoutReaderTests.OriginalLayout(), new FreeFlightFeature(), Setup(), Measure,
            planes: _planes, campaign: feature, profiles: () => _store);
        _store.Save(Flown("Zachary", CampaignSequence.MissionCount));
        shell.Campaign.OpenCampaignOver(_store);
        Assert.True(shell.Campaign.ShowScrapbook("Zachary", LastSeq, missionWon: false));

        Assert.Equal(1, closing.Plays);
        Assert.Equal(OriginalScreen.CampaignScrapbook, shell.Screen);

        // And the same instance plays again for a won replay, in either presentation.
        Assert.True(shell.Campaign.ShowScrapbook("Zachary", LastSeq, missionWon: true));
        Assert.Equal(2, closing.Plays);
        closing.Stop();
        Assert.Equal(OriginalScreen.CampaignScrapbook, shell.Screen);
    }

    private static string Airframe(int airframe) => $"node{airframe}";

    private static CampaignProfileDef Flown(string name, int missions)
    {
        var profile = CampaignProfileDef.NewProfile(name);
        for (int seq = 0; seq < missions; seq++)
        {
            CampaignProgression.Record(profile, new MissionAttempt(
                seq, CampaignProgression.PrimaryObjectiveMask, 300_000, 400, 120, 5, "Gypsy Magic"));
        }

        return profile;
    }

    private static PlayerSetupFeature Setup()
    {
        var setup = new PlayerSetupFeature();
        setup.SetRoster(OriginalPresentation.Roster(Array.Empty<CustomPlaneDef>()));
        setup.Join(new ScriptedMenuSeat());
        return setup;
    }

    // The fixture's strips, as OriginalCampaignTests measures them.
    private static (int Width, int Height)? Measure(string art) => art switch
    {
        "PM_B_Paper.png" => (160, 112),
        _ when art.StartsWith("PM_B_", StringComparison.Ordinal) => (240, 200),
        _ => null,
    };

    private CampaignFlow Flow(Recorder closing, out Recorder chapters)
    {
        chapters = new Recorder();
        var feature = new CampaignFeature(
            UiStrings.Empty, Airframe, new ChapterCinema(chapters.Play), new ClosingCinema(closing.Play));
        feature.Open(_store, _planes, null, null);
        return new CampaignFlow(feature);
    }

    private OriginalShell Shell(Recorder closing, out CampaignFeature campaign)
    {
        campaign = new CampaignFeature(
            UiStrings.Empty, Airframe, new ChapterCinema(new Recorder().Play), new ClosingCinema(closing.Play));
        var store = _store;
        return new OriginalShell(MenuLayoutReaderTests.OriginalLayout(), new FreeFlightFeature(), Setup(), Measure,
            planes: _planes, campaign: campaign, profiles: () => store);
    }

    // The stand-in for Launcher.PlayCinema: it records what it was asked for and hands the film's
    // end back to the caller, so a test decides when the cinema stops.
    private sealed class Recorder
    {
        private Action? _then;

        public string? Name { get; private set; }

        public int Plays { get; private set; }

        public void Play(string name, Action then, CinemaSkip skip)
        {
            Name = name;
            Plays++;
            _then = then;
        }

        public void Stop() => _then?.Invoke();
    }
}
