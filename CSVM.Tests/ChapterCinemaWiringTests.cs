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
/// The chapter cinema as both presentations' campaigns actually reach it: the roster's CONTINUE,
/// the flight-return door and the in-campaign doors (RETURN TO CABIN and the back press) in each,
/// the cabin arriving on the frame the film stops, a position inside a chapter opening the cabin
/// with no film, and one instance serving both so a chapter plays once whichever door reached it.
/// A campaign whose feature carries no cinema opens the cabin exactly as it always did, which is
/// what every suite and every golden gets. The press that skips a film is spanned rather than
/// pressed after the hand-back, which is the only way to see the board behind the film read it:
/// with the pointer, with a key or a pad button, and held from under the film to past its end.
/// Built-in's presentation polls its seats, so the span itself is what these read off its flow.
/// </summary>
[Trait("Tier", "Quick")]
public class ChapterCinemaWiringTests : IDisposable
{
    private static readonly MenuCommands Accept = new() { Accept = true };

    private readonly string _dir;
    private readonly CampaignProfileStore _store;
    private readonly CustomPlaneStore _planes;

    public ChapterCinemaWiringTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-chapter-cinema-" + Guid.NewGuid().ToString("N"));
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
    public void BuiltInsContinuePlaysTheChapterFilmAndTheCabinFollowsIt()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.Accept();          // arm the name field
        flow.Type("Zachary");
        flow.Accept();          // CONTINUE

        Assert.Equal("chap1", cinema.Name);
        Assert.Equal(CampaignScreen.Roster, flow.Screen);
        cinema.Stop();
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    [Fact]
    public void BuiltInsFlightReturnDoorPlaysTheFilmOfTheChapterTheProfileHasReached()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.SelectProfile(Progressed("Zachary", 5));

        Assert.Equal("chap2", cinema.Name);
        Assert.Equal(CampaignScreen.Roster, flow.Screen);
        cinema.Stop();
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    // The case the original reaches and CSVM did not: win a chapter's last mission, land on the
    // book, press RETURN TO CABIN. That is a cabin entry at a chapter-opening position, and
    // PASSENGERCABIN.SCRIPT runs the chapter-intro script on every one of them.
    [Fact]
    public void BuiltInsReturnToCabinFromTheBookPlaysTheNextChaptersFilm()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.OpenScrapbookAfterMission(Progressed("Zachary", 5), 4, missionWon: true);
        Assert.Equal(0, cinema.Plays);
        PressReturnToCabin(flow);

        Assert.Equal("chap2", cinema.Name);
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        cinema.Stop();
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    // The same cabin, reached by the back press instead of the plaque: the door taken must not be
    // what decides whether a film is due.
    [Fact]
    public void BuiltInsBackFromTheBookPlaysTheNextChaptersFilm()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.OpenScrapbookAfterMission(Progressed("Zachary", 5), 4, missionWon: true);
        flow.Back();

        Assert.Equal("chap2", cinema.Name);
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        cinema.Stop();
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    // The negative control, and not a vacuous one: the same press on a profile inside a chapter
    // plays nothing and still lands on the cabin.
    [Fact]
    public void AnInCampaignCabinDoorInsideAChapterPlaysNoFilm()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.OpenScrapbookAfterMission(Progressed("Zachary", 3), 2, missionWon: true);
        PressReturnToCabin(flow);

        Assert.Equal(0, cinema.Plays);
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    // What the widening is for: the latch decides, not the door. The chapter watched on the way in
    // does not play again on the way back from the book.
    [Fact]
    public void TheLatchAndNotTheDoorDecidesWhetherAFilmIsDue()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.SelectProfile(Progressed("Zachary", 5));
        cinema.Stop();
        Assert.Equal(1, cinema.Plays);

        flow.OpenScrapbook(4);
        PressReturnToCabin(flow);

        Assert.Equal(1, cinema.Plays);
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    // The negative control the plan names, off engine: --menu=campaign-cabin seats a profile three
    // missions in, which is a position inside a chapter and plays nothing at all.
    [Fact]
    public void APositionInsideAChapterOpensBuiltInsCabinWithNoFilm()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.SelectProfile(Progressed("Zachary", 3));

        Assert.Equal(0, cinema.Plays);
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    // What every engine suite and every golden gets: nothing hands those a cinema, so the cabin
    // door is the plain one it was before the wiring.
    [Fact]
    public void ACampaignWithNoCinemaOpensTheCabinStraightAway()
    {
        var feature = new CampaignFeature(UiStrings.Empty, Airframe);
        feature.Open(_store, _planes, null, null);
        var flow = new CampaignFlow(feature);

        flow.SelectProfile(CampaignProfileDef.NewProfile("Zachary"));

        Assert.Null(feature.ChapterCinema);
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
    }

    // The span a presentation that polls its input reads off the flow: the film owns every frame it
    // plays, and the frame it hands back on is the tail of the press that ended it.
    [Fact]
    public void BuiltInsChapterFilmOwnsItsFramesAndTheTailOfThePressThatEndedIt()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.SelectProfile(Progressed("Zachary", 5));

        Assert.True(flow.Film.Up);
        cinema.Stop();
        Assert.False(flow.Film.Up);
        Assert.True(flow.Film.Swallows(pointerHeld: false));
        Assert.False(flow.Film.Swallows(pointerHeld: false));
    }

    [Fact]
    public void BuiltInsClosingFilmOwnsItsFramesAndTheTailOfThePressThatEndedIt()
    {
        var cinema = new Recorder();
        var feature = new CampaignFeature(
            UiStrings.Empty, Airframe, closingCinema: new ClosingCinema(cinema.Play));
        feature.Open(_store, _planes, null, null);
        var flow = new CampaignFlow(feature);

        flow.OpenScrapbookAfterMission(
            Progressed("Zachary", CampaignSequence.MissionCount), CampaignSequence.MissionCount - 1, missionWon: true);

        Assert.Equal(ClosingCinema.Name, cinema.Name);
        Assert.True(flow.Film.Up);
        Assert.Equal(CampaignScreen.Cabin, flow.Screen);
        cinema.Stop();
        Assert.Equal(CampaignScreen.Scrapbook, flow.Screen);
        Assert.True(flow.Film.Swallows(pointerHeld: false));
        Assert.False(flow.Film.Swallows(pointerHeld: false));
    }

    // The pointer's tail lasts while its button is down, the release being the gesture a board fires
    // on; a key or a pad button is spent on the one frame it lands on.
    [Fact]
    public void ThePointerTailOfABuiltInFilmLastsUntilTheButtonComesUp()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);
        flow.SelectProfile(Progressed("Zachary", 5));
        cinema.Stop();

        Assert.True(flow.Film.Swallows(pointerHeld: true));
        Assert.True(flow.Film.Swallows(pointerHeld: true));
        Assert.True(flow.Film.Swallows(pointerHeld: false));
        Assert.False(flow.Film.Swallows(pointerHeld: false));
    }

    // The negative control: a door with no film due hands its screen over inside the play call, so
    // no frame is owned and the very next press is the player's own.
    [Fact]
    public void ABuiltInCabinDoorWithNoFilmDueOwnsNoFrameAndSwallowsNothing()
    {
        var cinema = new Recorder();
        var flow = Flow(cinema);

        flow.SelectProfile(Progressed("Zachary", 3));

        Assert.Equal(0, cinema.Plays);
        Assert.False(flow.Film.Up);
        Assert.False(flow.Film.Swallows(pointerHeld: false));
    }

    [Fact]
    public void OriginalsContinuePlaysTheChapterFilmAndTheCabinFollowsIt()
    {
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        OpenCampaign(shell);

        shell.Step(new MenuCommands { Typed = "Zachary" });
        shell.Step(Accept);

        Assert.Equal("chap1", cinema.Name);
        Assert.Equal(OriginalScreen.CampaignRoster, shell.Screen);
        cinema.Stop();
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    [Fact]
    public void OriginalsFlightReturnDoorPlaysTheFilmOfTheChapterTheProfileHasReached()
    {
        _store.Save(Progressed("Zachary", 10));
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        shell.Campaign.OpenCampaignOver(_store);

        Assert.True(shell.Campaign.ShowCabin("Zachary"));

        Assert.Equal("chap3", cinema.Name);
        Assert.Equal(OriginalScreen.CampaignRoster, shell.Screen);
        cinema.Stop();
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    [Fact]
    public void OriginalsReturnToCabinFromTheBookPlaysTheNextChaptersFilm()
    {
        _store.Save(Progressed("Zachary", 5));
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        shell.Campaign.OpenCampaignOver(_store);
        Assert.True(shell.Campaign.ShowScrapbook("Zachary", 4, missionWon: true));
        Assert.Equal(0, cinema.Plays);

        Assert.Equal(nameof(BoardButton.ReturnToCabin), shell.FocusedKey);
        shell.Step(Accept);

        Assert.Equal("chap2", cinema.Name);
        Assert.Equal(OriginalScreen.CampaignScrapbook, shell.Screen);
        cinema.Stop();
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    [Fact]
    public void OriginalsBackFromTheBookPlaysTheNextChaptersFilm()
    {
        _store.Save(Progressed("Zachary", 5));
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        shell.Campaign.OpenCampaignOver(_store);
        Assert.True(shell.Campaign.ShowScrapbook("Zachary", 4, missionWon: true));

        shell.Step(new MenuCommands { Back = true });

        Assert.Equal("chap2", cinema.Name);
        Assert.Equal(OriginalScreen.CampaignScrapbook, shell.Screen);
        cinema.Stop();
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    // The negative control on Original, and not a vacuous one: the book of a profile inside a
    // chapter still hands RETURN TO CABIN the cabin, with no film.
    [Fact]
    public void OriginalsInCampaignCabinDoorInsideAChapterPlaysNoFilm()
    {
        _store.Save(Progressed("Zachary", 3));
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        shell.Campaign.OpenCampaignOver(_store);
        Assert.True(shell.Campaign.ShowScrapbook("Zachary", 2, missionWon: true));

        shell.Step(Accept);

        Assert.Equal(0, cinema.Plays);
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    [Fact]
    public void APositionInsideAChapterOpensOriginalsCabinWithNoFilm()
    {
        _store.Save(Progressed("Zachary", 3));
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        shell.Campaign.OpenCampaignOver(_store);

        Assert.True(shell.Campaign.ShowCabin("Zachary"));

        Assert.Equal(0, cinema.Plays);
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    // The decision the wiring owns: one instance for the process, so the chapter a player has
    // already watched does not play again when they leave the campaign and come back, whichever
    // presentation they come back through.
    [Fact]
    public void OneInstanceServesBothPresentationsAndAChapterPlaysOnce()
    {
        var cinema = new Recorder();
        var chapters = new ChapterCinema(cinema.Play);
        var feature = new CampaignFeature(UiStrings.Empty, Airframe, chapters);
        feature.Open(_store, _planes, null, null);
        var flow = new CampaignFlow(feature);
        flow.SelectProfile(Progressed("Zachary", 0));
        cinema.Stop();
        Assert.Equal(1, cinema.Plays);

        var shell = new OriginalShell(MenuLayoutReaderTests.OriginalLayout(), new FreeFlightFeature(), Setup(), Measure,
            planes: _planes, campaign: feature, profiles: () => _store);
        _store.Save(Progressed("Zachary", 0));
        shell.Campaign.OpenCampaignOver(_store);
        Assert.True(shell.Campaign.ShowCabin("Zachary"));

        Assert.Equal(1, cinema.Plays);
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    // The press that skips a film reaches the board on the frame the film hands back, and the board
    // is by then the screen the film opened: the pointer's release lands on whatever stands under
    // it. That is invisible to any check that presses only after the hand-back, so each of these
    // spans the film with one press.
    [Fact]
    public void TheClickThatSkipsTheChapterFilmFiresNothingOnTheCabinItOpens()
    {
        var cinema = new Recorder();
        var shell = Shell(cinema, out var campaign);
        OpenCampaign(shell);
        shell.Step(new MenuCommands { Typed = "Zachary" });
        shell.Step(Accept);
        Assert.Equal("chap1", cinema.Name);

        cinema.Stop();
        var home = shell.Rows.Single(r => r.Key == nameof(BoardButton.ReturnToMainMenu));
        var swallowed = shell.Step(Pointer(home.X + 3f, home.Y + 3f, pressed: true, clicked: true));
        Assert.Equal(string.Empty, shell.ArmedKey);
        // The swallowed frame still asks for a redraw: the screen changed under the film, where no
        // step was reading it.
        Assert.True(swallowed.Changed);
        shell.Step(Pointer(home.X + 3f, home.Y + 3f));
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
        Assert.True(campaign.IsOpen);

        // And the board is not deafened: the next press is one of its own.
        Click(shell, home.X + 3f, home.Y + 3f);
        Assert.Equal(OriginalScreen.TopLevel, shell.Screen);
        Assert.False(campaign.IsOpen);
    }

    // A keyboard key and a pad button are one command by the time the shell reads them
    // (MenuCommands is device-neutral), so this is both of them; which presses end which film is
    // CinemaSkipSuites' half.
    [Fact]
    public void TheKeyOrPadPressThatSkipsTheChapterFilmFiresNothingOnTheCabinItOpens()
    {
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        OpenCampaign(shell);
        shell.Step(new MenuCommands { Typed = "Zachary" });
        shell.Step(Accept);

        cinema.Stop();
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
        Assert.Equal("NextMission", shell.FocusedKey);
        shell.Step(Accept);
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);

        shell.Step(Accept);
        Assert.Equal(OriginalScreen.CampaignBriefing, shell.Screen);
    }

    // The film owns every frame it stands for, which is the other half: a press the film's own skip
    // set does not read still reaches the board behind it, and the board is not on screen.
    [Fact]
    public void APressHeldAcrossTheChapterFilmWalksNothingBehindItAndFiresNothingOnItsRelease()
    {
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        OpenCampaign(shell);
        shell.Step(new MenuCommands { Typed = "Zachary" });
        shell.Step(Accept);

        var start = shell.Rows.Single(r => r.Key == nameof(BoardButton.Continue));
        var step = shell.Step(Pointer(start.X + 3f, start.Y + 3f, pressed: true, clicked: true));
        Assert.False(step.Changed);
        Assert.Equal(string.Empty, shell.ArmedKey);
        Assert.Equal(OriginalScreen.CampaignRoster, shell.Screen);
        shell.Step(Accept);
        Assert.Equal(OriginalScreen.CampaignRoster, shell.Screen);

        cinema.Stop();
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
        shell.Step(Pointer(start.X + 3f, start.Y + 3f));
        Assert.Equal(OriginalScreen.CampaignCabin, shell.Screen);
    }

    // The negative control: a door with no film due hands its screen over inside the play call, so
    // nothing is outstanding and the very next press is the player's own.
    [Fact]
    public void ACabinDoorWithNoFilmDueSwallowsNothingFromTheNextPress()
    {
        _store.Save(Progressed("Zachary", 3));
        var cinema = new Recorder();
        var shell = Shell(cinema, out _);
        shell.Campaign.OpenCampaignOver(_store);
        Assert.True(shell.Campaign.ShowCabin("Zachary"));
        Assert.Equal(0, cinema.Plays);

        shell.Step(Accept);
        Assert.Equal(OriginalScreen.CampaignBriefing, shell.Screen);
    }

    private static string Airframe(int airframe) => $"node{airframe}";

    // The RETURN TO CABIN plaque on whatever screen the flow is showing, found by its authored
    // button rather than by a row number, since each screen orders its own rows.
    private static void PressReturnToCabin(CampaignFlow flow)
    {
        for (int row = 0; row < flow.Page.RowCount; row++)
        {
            if (flow.Page.Button(row).Button == BoardButton.ReturnToCabin)
            {
                flow.FocusRow(row);
                Assert.True(flow.Accept());
                return;
            }
        }

        Assert.Fail($"{flow.Screen} carries no RETURN TO CABIN row");
    }

    private static CampaignProfileDef Progressed(string name, int flown)
    {
        var profile = CampaignProfileDef.NewProfile(name);
        for (int seq = 0; seq < flown; seq++)
        {
            CampaignProgression.Record(profile, new MissionAttempt(
                seq, CampaignProgression.PrimaryObjectiveMask, 300_000, 400, 120, 5, "Gypsy Magic"));
        }

        return profile;
    }

    private static void OpenCampaign(OriginalShell shell)
    {
        var row = shell.Rows.Single(r => r.Key == OriginalShell.CampaignKey);
        Click(shell, row.X + 2f, row.Y + 2f);
    }

    private static MenuCommands Pointer(float x, float y, bool pressed = false, bool clicked = false) =>
        new() { Pointer = new MenuPointer(x, y, pressed, clicked) };

    // One click as the shell reads it: the press arms the row and the release on it fires, so the
    // step that carries the activation is the second one.
    private static void Click(OriginalShell shell, float x, float y)
    {
        shell.Step(Pointer(x, y, pressed: true, clicked: true));
        shell.Step(Pointer(x, y));
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

    private CampaignFlow Flow(Recorder cinema)
    {
        var feature = new CampaignFeature(UiStrings.Empty, Airframe, new ChapterCinema(cinema.Play));
        feature.Open(_store, _planes, null, null);
        return new CampaignFlow(feature);
    }

    private OriginalShell Shell(Recorder cinema, out CampaignFeature campaign)
    {
        campaign = new CampaignFeature(UiStrings.Empty, Airframe, new ChapterCinema(cinema.Play));
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
