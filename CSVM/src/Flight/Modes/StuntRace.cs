using System;
using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>One seat in a splitscreen stunt race: the player's own
/// <see cref="StuntMission"/>, their own zone progress and run clock, plus where they placed
/// once they crossed the last Danger Zone.</summary>
public sealed class Racer
{
    /// <summary>0-based player index (player 1 = 0), also their pane, colour and tag.</summary>
    public int Index;

    /// <summary>This player's independent run over the shared zone list.</summary>
    public StuntMission Mission = null!;

    /// <summary>Readable aircraft name for the results board ("Bloodhawk").</summary>
    public string PlaneDisplay = "";

    /// <summary>Finishing position, 1 = winner; 0 while the player is still flying.</summary>
    public int Rank;

    /// <summary>The player's own run clock at the moment they finished (0 until then). Held
    /// separately from <see cref="StuntMission.Elapsed"/> so the board still reads right after a
    /// rematch resets the mission.</summary>
    public float FinishTime;

    internal Action? CompletionHandler;

    public bool Finished => Rank > 0;

    /// <summary>The player's identity colour + short tag, shared with the launchscreen's join
    /// strip and plane select so a player recognises "their" colour from menu to results.</summary>
    public Color Color => UI.Boards.SplitScreen.PlayerColor(Index);

    public string Tag => UI.Boards.SplitScreen.PlayerTag(Index);
}

/// <summary>
/// The splitscreen stunt race: every player flies the same mission's Danger Zones concurrently
/// in one shared world, each with their own progress, marker HUD and clock. Bookkeeping only, on
/// top of the per-player <see cref="StuntMission"/>s: who has finished, in what order, and
/// whether the race is over.
/// A player's clock stops at their own <see cref="StuntMission.AllComplete"/> while the others
/// fly on; when the last one is in, <see cref="RaceCompleted"/> raises the shared
/// <c>StuntRaceBoard</c>. Deliberately not a Node, it is freed with the session, so that
/// event needs no teardown.
/// ⚠ Do not wire <see cref="ScoreStore"/> best-time persistence in here: a race field starts on a
/// synthetic abreast-grid point no solo run starts at, so a race total is not comparable to one.
/// </summary>
public sealed class StuntRace
{
    private readonly List<Racer> _racers = new();

    /// <summary>Fired once when the last player finishes (the results board wakes on it).</summary>
    public event Action? RaceCompleted;

    /// <summary>The racers in player order (index 0 = player 1).</summary>
    public IReadOnlyList<Racer> Racers => _racers;

    /// <summary>How many players have cleared every zone.</summary>
    public int FinishedCount { get; private set; }

    /// <summary>True once every player is in, the shared results board's cue.</summary>
    public bool AllFinished => _racers.Count > 0 && FinishedCount >= _racers.Count;

    /// <summary>"1st" / "2nd" / "3rd" / "4th", placings, shared by the marker HUD's finish banner
    /// and the results board.</summary>
    public static string Ordinal(int rank) => rank switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{rank}th",
    };

    /// <summary>Enters a player, hooking their run's completion so it stamps a placing. Call in
    /// player order at session build; the mission is theirs alone
    /// (<see cref="StuntMission.ForAnotherPlayer"/>).</summary>
    public Racer Add(int index, StuntMission mission, string planeDisplay)
    {
        var racer = new Racer { Index = index, Mission = mission, PlaneDisplay = planeDisplay };
        _racers.Add(racer);
        racer.CompletionHandler = () => OnFinished(racer);
        mission.RunCompleted += racer.CompletionHandler;
        return racer;
    }

    /// <summary>The racer flying as player <paramref name="index"/>, or null (single player, or an
    /// index outside the race).</summary>
    public Racer? Of(int index)
    {
        foreach (var r in _racers)
            if (r.Index == index)
                return r;
        return null;
    }

    /// <summary>Rematch (R on the results board): every player's zones, clock and placing cleared,
    /// same aircraft and spawns. The planes themselves are respawned by the session, which owns
    /// them.</summary>
    public void Restart()
    {
        foreach (var r in _racers)
        {
            r.Mission.Reset();
            r.Rank = 0;
            r.FinishTime = 0f;
        }
        FinishedCount = 0;
    }

    /// <summary>The field in finishing order for the results board: finishers by placing, then
    /// anyone still flying, best progress first (zones cleared, then the faster clock), so a board
    /// raised early still reads sensibly.</summary>
    public IEnumerable<Racer> Standings()
    {
        var ordered = new List<Racer>(_racers);
        ordered.Sort((a, b) =>
        {
            if (a.Finished && b.Finished)
                return a.Rank.CompareTo(b.Rank);
            if (a.Finished != b.Finished)
                return a.Finished ? -1 : 1;
            int zones = b.Mission.CompletedCount.CompareTo(a.Mission.CompletedCount);
            return zones != 0 ? zones : a.Mission.Elapsed.CompareTo(b.Mission.Elapsed);
        });
        return ordered;
    }

    internal void Remove(int index)
    {
        int at = _racers.FindIndex(racer => racer.Index == index);
        if (at < 0)
            return;
        var racer = _racers[at];
        if (racer.CompletionHandler != null)
            racer.Mission.RunCompleted -= racer.CompletionHandler;
        _racers.RemoveAt(at);
    }

    private void OnFinished(Racer racer)
    {
        if (racer.Finished)
            return; // a rematch re-subscribes nothing, but never double-count a stray event
        FinishedCount++;
        racer.Rank = FinishedCount;
        racer.FinishTime = racer.Mission.Elapsed;
        Log.Info("flight",
            $"stunt race: {racer.Tag} finished {Ordinal(racer.Rank)} in {StuntMission.FormatTime(racer.FinishTime)} ({racer.PlaneDisplay})");
        if (AllFinished)
        {
            Log.Info("flight", $"stunt race: RACE COMPLETE");
            RaceCompleted?.Invoke();
        }
    }
}
